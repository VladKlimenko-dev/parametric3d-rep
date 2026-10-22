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
    public bool ShowOpenEnds { get; set; } = true;   // k96 (п. 7): по умолчанию включено
    /// <summary>u13: заливка (штриховка) замкнутых областей эскиза.</summary>
    public bool ShowFilled { get; set; } = true;     // k96 (п. 7): по умолчанию включено

    /// <summary>Что выбирается в режиме эскиза: эскиз, а также рёбра и вершины тела — кандидаты в ссылки (п. 2.3).</summary>
    private PickFilter SketchFilter => ActiveSketch == null ? PickFilter.All
        : _tool != null ? PickFilter.SketchPoints | PickFilter.Edges | PickFilter.Vertices
        : PickFilter.Sketch | PickFilter.Edges | PickFilter.Vertices | (FaceRefsOff ? PickFilter.None : PickFilter.Faces);
    static readonly bool FaceRefsOff = Environment.GetEnvironmentVariable("P3D_SKFACEREFS") == "0";

    /// <summary>k96 (п. 14): грань детали, проекция которой — ссылочные линии эскиза (очерк цилиндра, плоскость поперёк).</summary>
    public List<(Vector2d A, Vector2d B, double R, bool Circle)>? FaceReference(PickRef r)
    {
        if (FaceRefsOff || ActiveSketch is not { } sk || r.Kind != PickKind.Face || r.Owner is not Part part || part.FindFace(r.Id) is not { } f) return null;
        try { var pr = SketchReferences.FaceProjection(sk, f); return pr.Count > 0 ? pr : null; } catch { return null; }
    }

    // k97 (замечание 5): в эскизе проход выбора видеокарты не рисует граней (рёбра ловятся сквозь тело), и очерк цилиндра
    // под курсором не находился. Очерки граней активной детали — на процессоре, по расстоянию до линии на экране.
    private (object? Mesh, Sketch? Sk, List<(PickRef R, List<(Vector2d A, Vector2d B, double R, bool Circle)> L)> Items)? _faceRefCache;
    /// <summary>Мутация P3D_SKFACEPICK=0: очерк грани ловится только попаданием в саму грань (в эскизе — никогда).</summary>
    static readonly bool FacePickOff = Environment.GetEnvironmentVariable("P3D_SKFACEPICK") == "0";

    /// <summary>Мутация P3D_CONHOVER=0: значок связи под курсором не подсвечивает её элементы.</summary>
    static readonly bool ConstraintHoverOff = Environment.GetEnvironmentVariable("P3D_CONHOVER") == "0";
    /// <summary>Разбор: сколько элементов подсвечено значком связи под курсором при последней сборке слоя.</summary>
    public int ConstraintHoverShown { get; private set; }

    /// <summary>Очерки кривых граней активной детали в плоскости эскиза (память — по сетке детали и эскизу).</summary>
    public List<(PickRef R, List<(Vector2d A, Vector2d B, double R, bool Circle)> L)>? FaceRefItems()
    {
        if (FaceRefsOff || ActiveSketch is not { } sk || _doc == null) return null;
        var part = ActivePart ?? _doc.AllParts().FirstOrDefault();
        if (part?.Mesh is not { } mesh) return null;
        if (_faceRefCache is { } c && ReferenceEquals(c.Mesh, mesh) && ReferenceEquals(c.Sk, sk)) return c.Items;
        var items = new List<(PickRef, List<(Vector2d, Vector2d, double, bool)>)>();
        foreach (var b in part.Bodies)
            foreach (var f in b.Faces)
            {
                if (f.Surface is Parametric3D.Kernel.Surfaces.PlaneSurface) continue;
                List<(Vector2d, Vector2d, double, bool)> pr;
                try { pr = SketchReferences.FaceProjection(sk, f); } catch { continue; }
                if (pr.Count > 0) items.Add((new PickRef(f.Id, PickKind.Face, part, null), pr));
            }
        _faceRefCache = (mesh, sk, items);
        return items;
    }

    /// <summary>Мутация P3D_SKSILHOUETTE=0: очерки кривых граней в эскизе — только под курсором (прежде).</summary>
    static readonly bool SilhouetteOff = Environment.GetEnvironmentVariable("P3D_SKSILHOUETTE") == "0";
    /// <summary>Разбор: сколько отрезков очерка нарисовано в последнем слое.</summary>
    public int SilhouetteDrawn { get; private set; }

    /// <summary>Грань детали, очерк которой (ссылочная линия эскиза) проходит под курсором; пусто — нет.</summary>
    public PickRef FaceReferenceAt(Point p)
    {
        if (FacePickOff || FaceRefsOff || ActiveSketch is not { } sk || _doc == null || ScreenToSketch(p) is not { } q) return PickRef.Empty;
        if (!Selection.Accepts(PickKind.Face)) return PickRef.Empty;
        if (FaceRefItems() is not { } items0) return PickRef.Empty;
        var c = (Items: items0, 0);
        double tol = Core.Settings.Current.PickTolerancePx * PixelSize, best = tol;
        var hit = PickRef.Empty;
        foreach (var (r, list) in c.Items)
            foreach (var (a, b, rad, circle) in list)
            {
                double d = circle ? Math.Abs((q - a).Length - rad) : SegDist(q, a, b);
                if (d < best) { best = d; hit = r; }
            }
        return hit;
        static double SegDist(Vector2d x, Vector2d a, Vector2d b)
        {
            var ab = b - a; double l2 = ab.LengthSquared;
            double t = l2 < 1e-24 ? 0 : Math.Clamp(Vector2d.Dot(x - a, ab) / l2, 0, 1);
            return (x - (a + ab * t)).Length;
        }
    }

    /// <summary>Щелчок по ребру или вершине тела в режиме эскиза — добавить в ссылки эскиза.</summary>
    public event Action<PickRef>? ReferencePicked;
    /// <summary>Двойной щелчок по размеру эскиза — правка значения.</summary>
    public event Action<SkDimension, Point>? DimensionEditRequested;

    internal bool TryReference(PickRef r)
    {
        if (ActiveSketch == null || r.Owner is not Part) return false;
        if (r.Kind == PickKind.Face) { if (FaceReference(r) == null) return false; }
        else if (r.Kind is not (PickKind.Edge or PickKind.Vertex)) return false;
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

    static readonly bool UpKeep = Environment.GetEnvironmentVariable("P3D_SKUPKEEP") == "1";

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
        // k98 (замечание 8): «верх» вида — ось V эскиза (U — вправо), как в Creo: «Горизонтально/Вертикально» — по осям
        // эскиза, и при «верхе» по оси U почти вертикальный отрезок от «Вертикально» ложился горизонтально.
        // P3D_SKUPKEEP=1 — прежний выбор оси, ближайшей к нынешнему верху.
        var up = UpKeep ? new[] { V3(pl.V), -V3(pl.V), V3(pl.U), -V3(pl.U) }.OrderByDescending(a => Vector3.Dot(a, camUp)).First() : V3(pl.V);
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
            foreach (var pt in sk.AllPoints().Append(sk.OriginPoint))
            {
                double d = (pt.P - q).Length;
                if (d < bd) { bd = d; best = pt; }
            }
        if (best != null) return new SketchInput(sk, best.P, best);
        // u13: привязка к кривой — ближайшая точка отрезка, дуги, окружности (точка эскиза не ловится)
        if (SnapItems)
        {
            SkEntity? ce = null; double cd = tol * 0.6;
            foreach (var e in sk.Entities.Append(sk.AxisH).Append(sk.AxisV)) { if (e is SkPoint) continue; double d = e.DistanceTo(q); if (d < cd) { cd = d; ce = e; } }
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
        // п. 12: фигура построена — инструмент снимается (режим по умолчанию: выбор и перетаскивание), без Esc;
        // повтор инструмента — настройка SketchToolRepeat
        if (_tool is { Finished: true } t) _tool = t.Continuous && Settings.Current.SketchToolRepeat ? t.Restart() : null;
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

    private bool GridStale() => (ActiveSketch != null || _modelGrid || _handle != null || _gizmo != null || _ghostLines != null || (_scene?.Origins.Count ?? 0) > 0) && Math.Abs(GridKeyNow() - _gridKey) > 1e-9;
    private double GridKeyNow() => Math.Round(Camera.WorldToPx, 4) + Camera.State.Target.X * 1e-3 + Camera.State.Target.Y * 1e-5 + Camera.State.Target.Z * 1e-7
        + Camera.State.Rotation.X * 7 + Camera.State.Rotation.Y * 11 + Camera.State.Rotation.Z * 13 + Camera.State.Rotation.W * 17;

    /// <summary>Разбор (проверки без OpenGL): собрать слой поверх вида — число линий сетки, линий и точек.</summary>
    public (int Grid, int Lines, int Points) BuildOverlayForTest() { var (g, l, p) = BuildOverlay(); return (g.Count, l.Count, p.Count); }

    private (List<LineInst> Grid, List<LineInst> Lines, List<PointInst> Points) BuildOverlay()
    {
        var grid = new List<LineInst>();
        var lines = new List<LineInst>();
        var pts = new List<PointInst>();
        AddHandleOverlay(lines, pts);
        AddGizmoOverlay(lines, pts);
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
        // k97: на светлом фоне сетка темнее (прежние 0.07/0.14 не различались)
        var minor = th.Dark ? new Vector4(1, 1, 1, 0.06f) : new Vector4(0.10f, 0.13f, 0.20f, 0.17f);
        var major = th.Dark ? new Vector4(1, 1, 1, 0.13f) : new Vector4(0.10f, 0.13f, 0.20f, 0.32f);
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
                    if (dm.UserLocked && !dm.Conflict && !dm.Flagged && !dm.Reference) dc = th.DimUserLocked;
                    hov = ReferenceEquals(dm, HoverDimension) || ReferenceEquals(dm, DraggedDimension);
                }
                // текст — контуры глифов ГОСТ: тонкой линией (толстая сливала цифры)
                bool txt = st >= LineStyle.DimensionText;
                float wd = txt ? Settings.Current.DimensionTextOutlineWidth : 1.1f;
                if (hov) { dc = new Vector4(MathF.Min(1, dc.X * 1.25f + 0.15f), MathF.Min(1, dc.Y * 1.25f + 0.15f), MathF.Min(1, dc.Z * 1.25f + 0.15f), 1); wd *= txt ? 1.3f : 1.9f; }
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

        // k97 (замечание 8): под курсором значок связи — подсветить то, к чему она приложена
        if (ActiveSketch is { } skc && !ConstraintHoverOff && Selection.Hover.Kind == PickKind.SketchConstraint && Selection.Hover.Target is SkConstraint hc0)
        {
            var hcol = th.Hover; hcol.W = 1;
            var items = new HashSet<object>(hc0.Items.Cast<object>(), ReferenceEqualityComparer.Instance);
            foreach (var (e, pts0, closed) in skc.Polylines().Concat(new[] { skc.AxisH, skc.AxisV }.Select(a => ((SkEntity)a, new List<Vector2d> { a.A.P, a.B.P }, false))))
            {
                if (!items.Contains(e)) continue;
                int segs = closed ? pts0.Count : pts0.Count - 1;
                for (int i = 0; i < segs; i++) lines.Add(Inst(W(pts0[i]), W(pts0[(i + 1) % pts0.Count]), hcol, th.SketchWidth + 1.8f, 0));
            }
            foreach (var it in hc0.Items)
                if (it is SkPoint hp) pts.Add(new PointInst { P = W(hp.P), Color = SceneData.Pack(th.Hover), Px = 9, Shape = 0 });
            ConstraintHoverShown = hc0.Items.Count;
        }
        else ConstraintHoverShown = 0;

        // k98 (замечание 2): очерки кривых граней детали (образующие цилиндра в плоскости эскиза) — всегда, тонкой линией
        // цвета ссылок: рёбер у них нет, и в эскизе была видна только кромка торца
        SilhouetteDrawn = 0;
        if (ActiveSketch != null && !SilhouetteOff && FaceRefItems() is { } sil)
        {
            var sc = th.SketchRefEdge;
            foreach (var (_, list) in sil)
                foreach (var (a, b, r, circle) in list)
                {
                    if (circle)
                    {
                        for (int i = 0; i < 96; i++)
                        {
                            double t0 = 2 * Math.PI * i / 96, t1 = 2 * Math.PI * (i + 1) / 96;
                            lines.Add(Inst(W(a + new Vector2d(Math.Cos(t0), Math.Sin(t0)) * r), W(a + new Vector2d(Math.Cos(t1), Math.Sin(t1)) * r), sc, 1.2f, 0));
                        }
                    }
                    else lines.Add(Inst(W(a), W(b), sc, 1.2f, 0));
                    SilhouetteDrawn++;
                }
        }

        // k96 (п. 14): под курсором грань, дающая ссылочные линии (очерк цилиндра) — показать их цветом наведения
        if (ActiveSketch is { } skh && FaceReference(Selection.Hover) is { } fr)
        {
            var hc2 = th.Hover; hc2.W = 1;
            foreach (var (a, b, r, circle) in fr)
            {
                if (!circle) { lines.Add(Inst(W(a), W(b), hc2, 2.2f, 0)); continue; }
                int nseg = 96;
                for (int i = 0; i < nseg; i++)
                {
                    double t0 = 2 * Math.PI * i / nseg, t1 = 2 * Math.PI * (i + 1) / nseg;
                    lines.Add(Inst(W(a + new Vector2d(Math.Cos(t0), Math.Sin(t0)) * r), W(a + new Vector2d(Math.Cos(t1), Math.Sin(t1)) * r), hc2, 2.2f, 0));
                }
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

    // ============================================================ п. 6: перетаскивание вершин и фигур эскиза

    private SkPoint? _dragPoint;
    private Dictionary<SkPoint, Vector2d>? _dragShape;
    private Vector2d _geomFrom;
    private bool _geomMoved;

    /// <summary>Что сейчас тащат: вершину или фигуру (для подсказки и проверок).</summary>
    public string SketchDragKind => _dragPoint != null ? "point" : _dragShape != null ? "shape" : "";

    /// <summary>Точку можно тянуть: не ссылка, не зафиксирована (флаг или связь «Зафиксировать» на ней или её элементе).</summary>
    public static bool IsMovable(Sketch sk, SkPoint p)
    {
        if (p.Fixed || p.Role == SkRole.Reference || p.Owner is { } o && (o.Fixed || o.Role == SkRole.Reference)) return false;
        foreach (var c in sk.Constraints)
            if (c.Enabled && c.Type == ConstraintType.Fix && c.Items.Any(it => ReferenceEquals(it, p) || p.Owner != null && ReferenceEquals(it, p.Owner)))
                return false;
        return true;
    }

    /// <summary>
    /// Фигура, к которой принадлежит элемент: элементы, связанные общими точками и связями «Совпадение»
    /// (прямоугольник — четыре отрезка, паз — отрезки с дугами). Возвращает все их точки.
    /// </summary>
    public static HashSet<SkPoint> ShapePoints(Sketch sk, SkEntity start)
    {
        var owners = new Dictionary<SkPoint, List<SkEntity>>();
        foreach (var e in sk.Entities)
            foreach (var h in e.Handles) { if (!owners.TryGetValue(h, out var l)) owners[h] = l = new(); l.Add(e); }
        var links = new Dictionary<SkPoint, List<SkPoint>>();
        foreach (var c in sk.Constraints)
        {
            if (!c.Enabled || c.Type != ConstraintType.Coincident) continue;
            var ps = c.Items.OfType<SkPoint>().ToList();
            for (int a = 0; a < ps.Count; a++)
                for (int b = 0; b < ps.Count; b++)
                    if (a != b) { if (!links.TryGetValue(ps[a], out var l)) links[ps[a]] = l = new(); l.Add(ps[b]); }
        }
        var seenE = new HashSet<SkEntity>();
        var pts = new HashSet<SkPoint>();
        var stack = new Stack<SkEntity>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (!seenE.Add(e) || e.Role == SkRole.Reference) continue;
            foreach (var h in e.Handles)
            {
                if (!pts.Add(h)) continue;
                var near = new List<SkPoint> { h };
                if (links.TryGetValue(h, out var lk)) near.AddRange(lk);
                foreach (var q in near)
                {
                    pts.Add(q);
                    if (owners.TryGetValue(q, out var es)) foreach (var x in es) stack.Push(x);
                    if (q.Owner != null) stack.Push(q.Owner);
                }
            }
        }
        return pts;
    }

    internal bool BeginSketchDrag(Point p, Avalonia.Input.KeyModifiers mods)
    {
        _dragPoint = null; _dragShape = null; _geomMoved = false;
        if (ActiveSketch is not { } sk || _tool != null || ScreenToSketch(p) is not { } q) return false;
        if (mods.HasFlag(Avalonia.Input.KeyModifiers.Control) || mods.HasFlag(Avalonia.Input.KeyModifiers.Shift)) return false;
        if (ConstraintGlyphAt(p) is { IsEmpty: false }) return false;
        double tol = PixelSize * Settings.Current.SnapTolerancePx;
        SkPoint? best = null; double bd = tol;
        foreach (var pt in sk.AllPoints())
        {
            double d = (pt.P - q).Length;
            if (d < bd) { bd = d; best = pt; }
        }
        if (best != null)
        {
            if (!IsMovable(sk, best)) return false;
            _dragPoint = best; _geomFrom = q;
            _dragShape = new Dictionary<SkPoint, Vector2d> { [best] = best.P };
            return true;
        }
        SkEntity? ce = null; double cd = tol * 0.7;
        foreach (var e in sk.Entities)
        {
            if (e is SkPoint || e.Role == SkRole.Reference) continue;
            double d = e.DistanceTo(q);
            if (d < cd) { cd = d; ce = e; }
        }
        if (ce == null) return false;
        var shape = ShapePoints(sk, ce);
        if (shape.Count == 0 || shape.Any(x => !IsMovable(sk, x))) return false;
        _dragShape = shape.ToDictionary(x => x, x => x.P);
        _geomFrom = q;
        return true;
    }

    /// <summary>Тянуть к точке плоскости эскиза (для проверок — без мыши).</summary>
    public void SketchDragTo(Vector2d q)
    {
        if (ActiveSketch is not { } sk || _dragShape == null) return;
        var delta = q - _geomFrom;
        if (!_geomMoved && delta.Length < 3 * PixelSize) return;
        _geomMoved = true;
        foreach (var (pt, start) in _dragShape) pt.P = start + delta;
        var solver = _doc?.Solver ?? new NumericSketchSolver();
        var num = solver as NumericSketchSolver;
        double w0 = num?.DragWeight ?? 0;
        try
        {
            // перетаскивание: курсор держит точку в 100 раз крепче, чем остальные держатся на месте
            // (вес всё равно много меньше веса связей — связи не нарушаются)
            if (num != null) { num.DragWeight = Math.Max(w0, num.Regularization * 100); if (_dragPoint == null) num.DraggedPoints = _dragShape.Keys.ToHashSet(); }
            sk.Solve(solver, _dragPoint ?? _dragShape.Keys.First());
        }
        catch (Exception ex) { AppLog.Write("sketch drag", ex); }
        finally { if (num != null) { num.DraggedPoints = null; num.DragWeight = w0; } }
        _overlayDirty = true;
        MarkDirty();
    }

    internal void SketchDragMove(Point p)
    {
        if (ScreenToSketch(p) is { } q) SketchDragTo(q);
    }

    /// <summary>Отпустили; true — тащили (щелчок без движения — обычный выбор).</summary>
    public bool EndSketchDrag()
    {
        bool moved = _geomMoved && _dragShape != null;
        _dragPoint = null; _dragShape = null; _geomMoved = false;
        if (!moved || ActiveSketch is not { } sk) return false;
        sk.NotifyChanged(sk);
        RefreshDefaultDimensions();
        SketchEdited?.Invoke();
        MarkDirty();
        return true;
    }

    /// <summary>Начать перетаскивание в точке плоскости эскиза (для проверок).</summary>
    public bool BeginSketchDragAt(Vector2d q)
    {
        if (ActiveSketch is not { } sk) return false;
        var w = Project(sk.Plane.ToWorld(q));
        if (w is not { } pt || !BeginSketchDrag(pt, Avalonia.Input.KeyModifiers.None)) return false;
        _geomFrom = q;   // точно (экранная точка округлена до пикселя)
        return true;
    }
}
