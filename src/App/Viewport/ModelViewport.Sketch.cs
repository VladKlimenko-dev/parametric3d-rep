using Avalonia;
using OpenTK.Mathematics;
using Parametric3D.App.Sketching;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Viewport;

/// <summary>
/// РЕЖИМ ЭСКИЗА ВИДА: камера нормально к плоскости (ортогонально), сетка на плоскости, инструмент построения с
/// резиновой нитью поверх модели, привязка к точкам эскиза (в пределах SnapTolerancePx) и к сетке. Точки эскиза в
/// правке выбираются (SceneBuilder). Выход — с возвратом прежней камеры.
/// </summary>
public sealed partial class ModelViewport
{
    public Sketch? ActiveSketch { get; private set; }
    private CameraState _beforeSketch;
    private ISketchTool? _tool;
    private bool _overlayDirty;
    private double _gridKey;
    private SkPoint? _snap;
    private Vector2d _cursor2;

    /// <summary>Эскиз изменён инструментом (дерево, состояние, решатель).</summary>
    public event Action? SketchEdited;
    /// <summary>Сменился инструмент или его шаг (подсказка у вида).</summary>
    public event Action? ToolChanged;

    /// <summary>Привязка к сетке (кнопка панели эскиза).</summary>
    public bool SnapGrid { get; set; } = true;
    /// <summary>u13: привязка к элементам эскиза (точкам и кривым); выключена — только сетка.</summary>
    public bool SnapItems { get; set; } = true;
    /// <summary>u13: показ открытых концов контура (точки, к которым не примыкает другой конец).</summary>
    public bool ShowOpenEnds { get; set; }
    /// <summary>u13: заливка (штриховка) замкнутых областей эскиза.</summary>
    public bool ShowFilled { get; set; }

    /// <summary>Что выбирается в режиме эскиза: эскиз, а также рёбра и вершины тела — кандидаты в ссылки (п. 2.3).</summary>
    private PickFilter SketchFilter => ActiveSketch == null ? PickFilter.All
        : _tool != null ? PickFilter.SketchPoints | PickFilter.Edges | PickFilter.Vertices
        : PickFilter.Sketch | PickFilter.Edges | PickFilter.Vertices;

    /// <summary>Щелчок по ребру или вершине тела в режиме эскиза — добавить в ссылки эскиза.</summary>
    public event Action<PickRef>? ReferencePicked;
    /// <summary>Двойной щелчок по размеру эскиза — правка значения.</summary>
    public event Action<SkDimension, Point>? DimensionEditRequested;

    internal bool TryReference(PickRef r)
    {
        if (ActiveSketch == null || r.Owner is not Part || r.Kind is not (PickKind.Edge or PickKind.Vertex)) return false;
        ReferencePicked?.Invoke(r);
        return true;
    }

    /// <summary>Размер под курсором (по подписи), null — нет.</summary>
    public SkDimension? DimensionAt(Point p)
    {
        if (ActiveSketch is not { } sk || ScreenToSketch(p) is not { } q) return null;
        double px = PixelSize, best = 14 * px;
        SkDimension? hit = null;
        foreach (var d in sk.Dimensions)
        {
            double dist = (d.TextCenter - q).Length;
            if (dist < best) { best = dist; hit = d; }
        }
        return hit;
    }

    /// <summary>u12: размер под курсором (подсветка) и перетаскиваемый.</summary>
    public SkDimension? HoverDimension { get; private set; }
    public SkDimension? DraggedDimension { get; private set; }
    private Vector2d _dimDragStart, _dimOffsetStart;
    private bool _dimMoved;

    /// <summary>Наведение в эскизе: размер под курсором подсвечивается.</summary>
    internal void UpdateDimensionHover(Point p)
    {
        if (ActiveSketch == null) { if (HoverDimension != null) { HoverDimension = null; _overlayDirty = true; Redraw(); } return; }
        var d = DimensionAt(p);
        if (ReferenceEquals(d, HoverDimension)) return;
        HoverDimension = d; _overlayDirty = true; Redraw();
    }

    /// <summary>Нажали на подпись размера — её можно тащить мышью (место подписи; значение не меняется).</summary>
    internal bool BeginDimensionDrag(Point p)
    {
        if (ActiveSketch == null || DimensionAt(p) is not { } d || ScreenToSketch(p) is not { } q) return false;
        DraggedDimension = d; _dimDragStart = q; _dimOffsetStart = d.TextOffset; _dimMoved = false;
        return true;
    }

    internal void DimensionDragMove(Point p)
    {
        if (DraggedDimension is not { } d || ScreenToSketch(p) is not { } q) return;
        var delta = q - _dimDragStart;
        if (!_dimMoved && delta.Length < 3 * PixelSize) return;
        if (!_dimMoved && !d.UserPlaced && ActiveSketch is { } skd)
        {
            // подпись ещё не ставили (сторона выбрана автоматически): смещение берётся от нынешнего места подписи —
            // по составляющей поперёк размера знак тот, что сейчас на экране (без скачка на другую сторону)
            var before = d.TextCenter;
            d.UserPlaced = true;
            var probe = new DimBatch { PixelSize = PixelSize };
            try { SketchDimensions.Build(skd, probe, PixelSize); } catch { }
            if ((d.TextCenter - before).Length > 4 * PixelSize) { _dimOffsetStart = -_dimOffsetStart; d.TextOffset = _dimOffsetStart; try { SketchDimensions.Build(skd, probe, PixelSize); } catch { } }
            if ((d.TextCenter - before).Length > 4 * PixelSize) _dimOffsetStart = d.TextOffset = -_dimOffsetStart;   // не помогло — как было
        }
        _dimMoved = true;
        d.TextOffset = _dimOffsetStart + delta;
        d.UserPlaced = true;
        _overlayDirty = true;
        Redraw();
    }

    /// <summary>Отпустили размер; true — его тащили (щелчок без движения — обычный выбор).</summary>
    internal bool EndDimensionDrag()
    {
        var d = DraggedDimension; DraggedDimension = null;
        if (d == null || !_dimMoved) return false;
        ActiveSketch?.NotifyChanged(ActiveSketch);
        _overlayDirty = true; Redraw();
        return true;
    }

    internal bool TryEditDimension(Point p)
    {
        if (DimensionAt(p) is not { } d) return false;
        DimensionEditRequested?.Invoke(d, p);
        return true;
    }

    /// <summary>Размеры по умолчанию (серые) — эскиз определён полностью: каждая точка задана от соседей, ссылок или нуля.</summary>
    public void RefreshDefaultDimensions()
    {
        if (ActiveSketch is not { } sk) return;
        try { DefaultDimensions.Refresh(sk, _doc?.Solver ?? new NumericSketchSolver()); }
        catch (Exception ex) { AppLog.Write("default dimensions", ex); }
        _overlayDirty = true;
        Redraw();
    }

    public ISketchTool? Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            if (value != null && ActiveSketch == null) _tool = null;
            Selection.Filter = SketchFilter;
            _overlayDirty = true;
            ToolChanged?.Invoke();
            Redraw();
        }
    }

    public void EnterSketch(Sketch sk, bool animate = true)
    {
        if (ActiveSketch == null) _beforeSketch = Camera.State;
        ActiveSketch = sk;
        Selection.Clear();
        Selection.Filter = SketchFilter;
        var to = Camera.State;
        var pl = sk.Plane;
        // «верх» вида — ось плоскости, ближайшая к нынешнему верху (вид не переворачивается)
        var camUp = Camera.Up;
        var up = new[] { V3(pl.V), -V3(pl.V), V3(pl.U), -V3(pl.U) }.OrderByDescending(a => Vector3.Dot(a, camUp)).First();
        to.Rotation = ViewMath.FromForwardUp(-V3(pl.W), up);
        to.Target = V3(pl.Origin);
        to.Mode = ProjectionMode.Orthographic;
        if (animate) AnimateTo(to); else { Camera.State = to; CameraChanged?.Invoke(); }
        RefreshDefaultDimensions();
        _overlayDirty = true;
        MarkDirty();
    }

    public void ExitSketch(bool restoreCamera = true)
    {
        if (ActiveSketch == null) return;
        _tool = null;
        ActiveSketch = null;
        Selection.Filter = PickFilter.All;
        Selection.Clear();
        if (restoreCamera) AnimateTo(_beforeSketch);
        _overlayDirty = true;
        ToolChanged?.Invoke();
        MarkDirty();
    }

    private static Vector3 V3(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>Размер логического пикселя на плоскости (мм).</summary>
    public double PixelSize => 1.0 / Math.Max(1e-6, Camera.WorldToPx) * Scaling;

    /// <summary>Точка экрана на плоскости эскиза (без привязок); null — луч параллелен плоскости.</summary>
    public Vector2d? ScreenToSketch(Point p)
    {
        if (ActiveSketch is not { } sk) return null;
        double s = Scaling;
        Camera.ScreenRay((int)(p.X * s), (int)(p.Y * s), out var o, out var d);
        var pl = sk.Plane;
        var n = pl.W;
        double den = Vector3d.Dot(new Vector3d(d.X, d.Y, d.Z), n);
        if (Math.Abs(den) < 1e-9) return null;
        double t = Vector3d.Dot(pl.Origin - new Vector3d(o.X, o.Y, o.Z), n) / den;
        var w = new Vector3d(o.X, o.Y, o.Z) + new Vector3d(d.X, d.Y, d.Z) * t;
        return pl.ToLocal2(w);
    }

    /// <summary>Точка с привязкой: ближняя точка эскиза в пределах допуска, иначе узел сетки.</summary>
    private SketchInput? Capture(Point p)
    {
        if (ActiveSketch is not { } sk || ScreenToSketch(p) is not { } q) return null;
        double tol = PixelSize * Settings.Current.SnapTolerancePx;
        SkPoint? best = null; double bd = tol;
        if (SnapItems)
            foreach (var pt in sk.AllPoints())
            {
                double d = (pt.P - q).Length;
                if (d < bd) { bd = d; best = pt; }
            }
        if (best != null) return new SketchInput(sk, best.P, best);
        // u13: привязка к кривой — ближайшая точка отрезка, дуги, окружности (точка эскиза не ловится)
        if (SnapItems)
        {
            SkEntity? ce = null; double cd = tol * 0.6;
            foreach (var e in sk.Entities) { if (e is SkPoint) continue; double d = e.DistanceTo(q); if (d < cd) { cd = d; ce = e; } }
            if (ce is SkLine sl) { var ab = sl.B.P - sl.A.P; double t = Math.Clamp(Vector2d.Dot(q - sl.A.P, ab) / Math.Max(ab.LengthSquared, 1e-30), 0, 1); return new SketchInput(sk, sl.A.P + ab * t, null); }
            if (ce is SkCircle or SkArc && Parametric3D.App.Sketching.SkGeom.CircleOf(ce) is { } cc && (q - cc.C).Length > 1e-12) return new SketchInput(sk, cc.C + (q - cc.C).Normalized() * cc.R, null);
        }
        if (SnapGrid)
        {
            double step = GridStep();
            q = new Vector2d(Math.Round(q.X / step) * step, Math.Round(q.Y / step) * step);
        }
        return new SketchInput(sk, q, null);
    }

    private void ToolMove(Point p)
    {
        if (_tool == null || Capture(p) is not { } i) return;
        _tool.Move(i);
        _snap = i.Snap;
        _cursor2 = i.P;
        _overlayDirty = true;
        Redraw();
    }

    private void ToolClick(Point p)
    {
        if (_tool == null || Capture(p) is not { } i) return;
        bool changed = _tool.Click(i);
        AfterTool(changed);
    }

    /// <summary>Enter, правая кнопка, двойной щелчок: закончить начатое; повторно (ничего не начато) — снять инструмент.</summary>
    public void ToolFinish()
    {
        if (_tool == null || ActiveSketch == null) return;
        bool changed = _tool.Finish(ActiveSketch);
        AfterTool(changed);
    }

    private void AfterTool(bool changed)
    {
        if (ActiveSketch is { } sk && changed)
        {
            try { sk.Solve(_doc?.Solver ?? new NumericSketchSolver()); }
            catch (Exception ex) { AppLog.Write("sketch solve", ex); }
            sk.NotifyChanged(sk);
            MarkDirty();
            SketchEdited?.Invoke();
        }
        // размеры по умолчанию — когда примитив закончен (не между щелчками одного построения)
        if (_tool is { Finished: true } || (changed && _tool == null)) RefreshDefaultDimensions();
        if (_tool is { Finished: true } t) _tool = t.Continuous ? t.Restart() : null;
        Selection.Filter = SketchFilter;
        _overlayDirty = true;
        ToolChanged?.Invoke();
        Redraw();
    }

    /// <summary>Щелчок инструмента в точке вида (доли размера) — для сценариев проверок.</summary>
    public void SimulateToolClick(double fx, double fy)
    {
        var p = new Point(Bounds.Width * fx, Bounds.Height * fy);
        ToolMove(p);
        ToolClick(p);
    }

    /// <summary>Шаг сетки: «круглое» число, чтобы клетка была 25–60 пикселей.</summary>
    public double GridStep()
    {
        if (ActiveSketch is { SnapToGrid: true } sk && sk.GridStep > 0) return sk.GridStep;
        double raw = PixelSize * 30;
        double e = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double m in new[] { 1, 2, 5, 10 }) if (m * e >= raw) return m * e;
        return 10 * e;
    }

    private bool GridStale() => (ActiveSketch != null || _modelGrid || _handle != null || _ghostLines != null || (_scene?.Origins.Count ?? 0) > 0) && Math.Abs(GridKeyNow() - _gridKey) > 1e-9;
    private double GridKeyNow() => Math.Round(Camera.WorldToPx, 4) + Camera.State.Target.X * 1e-3 + Camera.State.Target.Y * 1e-5 + Camera.State.Target.Z * 1e-7
        + Camera.State.Rotation.X * 7 + Camera.State.Rotation.Y * 11 + Camera.State.Rotation.Z * 13 + Camera.State.Rotation.W * 17;

    private (List<LineInst> Grid, List<LineInst> Lines, List<PointInst> Points) BuildOverlay()
    {
        var grid = new List<LineInst>();
        var lines = new List<LineInst>();
        var pts = new List<PointInst>();
        AddHandleOverlay(lines, pts);
        AddOriginOverlay(lines, pts);
        AddExtraOverlay(grid, lines);
        if (ActiveSketch is not { } sk) { _gridKey = GridKeyNow(); return (grid, lines, pts); }
        _gridKey = GridKeyNow();
        var th = _r.Theme;
        var pl = sk.Plane;
        Vector3 W(Vector2d p) => V3(pl.ToWorld(p));

        // сетка: вокруг проекции центра вида на плоскость, на размер вида
        double step = GridStep();
        var c3 = Camera.State.Target;
        var c = pl.ToLocal2(new Vector3d(c3.X, c3.Y, c3.Z));
        double half = Math.Max(Bounds.Width, Bounds.Height) * PixelSize * 0.75;
        int n = (int)Math.Min(200, Math.Ceiling(half / step));
        double x0 = Math.Round(c.X / step) * step, y0 = Math.Round(c.Y / step) * step;
        var minor = th.Dark ? new Vector4(1, 1, 1, 0.06f) : new Vector4(0, 0, 0, 0.07f);
        var major = th.Dark ? new Vector4(1, 1, 1, 0.13f) : new Vector4(0, 0, 0, 0.14f);
        for (int i = -n; i <= n; i++)
        {
            double x = x0 + i * step, y = y0 + i * step;
            bool mx = Math.Abs(Math.Round(x / step) % 5) < 0.5, my = Math.Abs(Math.Round(y / step) % 5) < 0.5;
            grid.Add(Inst(W(new Vector2d(x, y0 - n * step)), W(new Vector2d(x, y0 + n * step)), mx ? major : minor, 1f, 0));
            grid.Add(Inst(W(new Vector2d(x0 - n * step, y)), W(new Vector2d(x0 + n * step, y)), my ? major : minor, 1f, 0));
        }
        // оси эскиза
        grid.Add(Inst(W(new Vector2d(x0 - n * step, 0)), W(new Vector2d(x0 + n * step, 0)), new Vector4(0.85f, 0.35f, 0.35f, 0.55f), 1.2f, 0));
        grid.Add(Inst(W(new Vector2d(0, y0 - n * step)), W(new Vector2d(0, y0 + n * step)), new Vector4(0.35f, 0.75f, 0.4f, 0.55f), 1.2f, 0));

        // размеры эскиза — поверх, постоянного размера на экране
        if (Show.Dimensions)
        {
            var batch = new DimBatch { PixelSize = PixelSize, Normal = V3(pl.W) };
            var camR = Vector3.Transform(Vector3.UnitX, Quaternion.Invert(Camera.State.Rotation));
            var camU = Camera.Up;
            SketchDimensions.ScreenRight = new Vector2d(Vector3d.Dot(new Vector3d(camR.X, camR.Y, camR.Z), pl.U), Vector3d.Dot(new Vector3d(camR.X, camR.Y, camR.Z), pl.V));
            SketchDimensions.ScreenUp = new Vector2d(Vector3d.Dot(new Vector3d(camU.X, camU.Y, camU.Z), pl.U), Vector3d.Dot(new Vector3d(camU.X, camU.Y, camU.Z), pl.V));
            try { SketchDimensions.Build(sk, batch, PixelSize); }
            catch (Exception ex) { AppLog.Write("dimensions", ex); }
            // u12 (замечание 17): слабый — серый, сильный — голубой, с замком — оранжевый; под курсором — ярче и толще
            var byId = new Dictionary<uint, SkDimension>();
            foreach (var dd in sk.Dimensions) byId[dd.Id] = dd;
            foreach (var (a, b, st, dimId) in batch.Segments)
            {
                var dc = st switch
                {
                    LineStyle.DimensionConflict or LineStyle.DimensionTextConflict => th.Error,
                    LineStyle.DimensionReference or LineStyle.DimensionTextReference => th.DimReference,
                    LineStyle.DimensionLocked or LineStyle.DimensionTextLocked => th.DimLocked,
                    _ => th.DimDefault,
                };
                bool hov = false;
                if (byId.TryGetValue(dimId, out var dm))
                {
                    if (dm.UserLocked && !dm.Conflict && !dm.Reference) dc = th.DimUserLocked;
                    hov = ReferenceEquals(dm, HoverDimension) || ReferenceEquals(dm, DraggedDimension);
                }
                float wd = st >= LineStyle.DimensionText ? 1.3f : 1.1f;
                if (hov) { dc = new Vector4(MathF.Min(1, dc.X * 1.25f + 0.15f), MathF.Min(1, dc.Y * 1.25f + 0.15f), MathF.Min(1, dc.Z * 1.25f + 0.15f), 1); wd *= 1.9f; }
                lines.Add(Inst(a, b, dc, wd, 0));
            }
        }

        // u13: открытые концы контура и заливка замкнутых областей
        if (ActiveSketch is { } skd)
        {
            if (ShowOpenEnds)
            {
                try { SketchDiagnostics.Update(skd); } catch { }
                foreach (var p in skd.AllPoints().Where(p => p.Dangling && p.Owner != null && p.Role != SkRole.Construction))
                    pts.Add(new PointInst { P = W(p.P), Color = SceneData.Pack(th.Error), Px = 10, Shape = 1 });
            }
            if (ShowFilled)
            {
                var hc = th.SketchCurve; hc.W = 0.35f;
                foreach (var (a, b) in Hatch(skd)) lines.Add(Inst(W(a), W(b), hc, 1f, 0));
            }
        }

        // резиновая нить инструмента
        if (_tool != null)
        {
            var pl2 = new List<Vector2d[]>();
            var pp = new List<Vector2d>();
            _tool.Preview(pl2, pp);
            var col = th.SketchCurve; col.W = 0.9f;
            foreach (var poly in pl2)
                for (int i = 1; i < poly.Length; i++) lines.Add(Inst(W(poly[i - 1]), W(poly[i]), col, th.SketchWidth, 6));
            foreach (var p in pp) pts.Add(new PointInst { P = W(p), Color = SceneData.Pack(th.SketchPoint), Px = 6, Shape = 0 });
            if (_snap != null) pts.Add(new PointInst { P = W(_snap.P), Color = SceneData.Pack(th.Select), Px = 11, Shape = 1 });
        }
        return (grid, lines, pts);
    }

    /// <summary>Штриховка замкнутых областей эскиза под 45° (чёт-нечет по всем контурам профиля).</summary>
    private static List<(Vector2d, Vector2d)> Hatch(Sketch sk)
    {
        var res = new List<(Vector2d, Vector2d)>();
        Kernel.Ops.Profile prof;
        try { prof = sk.ToProfile(); } catch { return res; }
        var loops = prof.Loops.Select(l => { try { return l.Sample(sk.Plane); } catch { return new List<Vector2d>(); } }).Where(l => l.Count >= 3).ToList();
        if (loops.Count == 0) return res;
        // поворот на 45°: штрихи — горизонтали в повёрнутых координатах
        const double c = 0.70710678118654752, s = 0.70710678118654752;
        Vector2d R(Vector2d p) => new(p.X * c + p.Y * s, -p.X * s + p.Y * c);
        Vector2d Rinv(Vector2d p) => new(p.X * c - p.Y * s, p.X * s + p.Y * c);
        var rl = loops.Select(l => l.Select(R).ToList()).ToList();
        double y0 = rl.SelectMany(x => x).Min(p => p.Y), y1 = rl.SelectMany(x => x).Max(p => p.Y);
        double ext = rl.SelectMany(x => x).Max(p => p.X) - rl.SelectMany(x => x).Min(p => p.X);
        double step = Math.Max((y1 - y0) / 60, Math.Max(ext, y1 - y0) / 80);
        if (!(step > 0)) return res;
        for (double y = y0 + step / 2; y < y1; y += step)
        {
            var xs = new List<double>();
            foreach (var l in rl)
                for (int i = 0; i < l.Count; i++)
                {
                    var a = l[i]; var b = l[(i + 1) % l.Count];
                    if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y)) xs.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2) res.Add((Rinv(new Vector2d(xs[k], y)), Rinv(new Vector2d(xs[k + 1], y))));
        }
        return res;
    }

    private static LineInst Inst(Vector3 a, Vector3 b, Vector4 c, float w, float dash) =>
        new() { A = a, B = b, Color = SceneData.Pack(c), Width = w, Dash = dash, Pick = 0 };

    /// <summary>Значок ограничения под точкой экрана (DIP) в активном эскизе; Empty — нет.</summary>
    public PickRef ConstraintGlyphAt(Point p)
    {
        if (ActiveSketch is not { } sk || !Settings.Current.ShowConstraintGlyphs) return PickRef.Empty;
        PickRef best = PickRef.Empty; double bestD = double.MaxValue;
        foreach (var c in sk.Constraints)
        {
            if (!c.Enabled) continue;
            double half = ConstraintLayout.SizePx(c.Type) * 0.5 + 3;
            foreach (var g in c.GlyphPositions)
            {
                if (Project(sk.Plane.ToWorld(g)) is not { } q) continue;
                double dx = Math.Abs(q.X - p.X), dy = Math.Abs(q.Y - p.Y);
                if (dx > half || dy > half) continue;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = new PickRef(c.Id, PickKind.SketchConstraint, sk, c); }
            }
        }
        return best;
    }
}
