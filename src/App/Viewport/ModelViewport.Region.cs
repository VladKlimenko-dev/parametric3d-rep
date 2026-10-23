using Avalonia;
using Avalonia.Input;
using OpenTK.Mathematics;
using Parametric3D.Render;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Viewport;

/// <summary>
/// u13: ВЫБОР РАМКОЙ И АРКАНОМ, ПЛОСКОСТЬ СЕЧЕНИЯ, ЗЕБРА. Протяжка левой кнопкой по пустому месту — рамка (слева направо —
/// только целиком внутри, справа налево — задетые), в режиме аркана — произвольный контур (целиком внутри).
/// </summary>
public sealed partial class ModelViewport
{
    public enum RegionKind { Box, Lasso }
    static readonly bool RegionPointsOff = Environment.GetEnvironmentVariable("P3D_REGIONPOINTS") == "0";
    /// <summary>u13: сборка — протяжка левой кнопкой двигает выбранный компонент (в плоскости вида).</summary>
    public bool DragComponents;
    /// <summary>Компонент перетаскивается: начало и конец (окно решает ограничения и записывает шаг отмены).</summary>
    public event Action<Part>? ComponentDragStarted, ComponentDragEnded;
    private Part? _dragPart;

    private bool ComponentDragMove(Point p)
    {
        if (!DragComponents || _drag != Drag.Select) return false;
        if (_dragPart == null)
        {
            if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4) return false;
            _dragPart = Selection.Items.Select(r => r.Target as Part ?? r.Owner as Part).OfType<Part>().FirstOrDefault(x => !x.Fixed);
            if (_dragPart == null) return false;
            ComponentDragStarted?.Invoke(_dragPart);
            _last = _down;
        }
        double s = Scaling; float k = 1f / Math.Max(1e-6f, Camera.WorldToPx);
        float dx = (float)((p.X - _last.X) * s) * k, dy = (float)((p.Y - _last.Y) * s) * k;
        var right = Vector3.Transform(Vector3.UnitX, Quaternion.Invert(Camera.State.Rotation));
        _dragPart.Translation += right * dx - Camera.Up * dy;
        _last = p;
        MarkDirty();
        return true;
    }

    private bool ComponentDragEnd()
    {
        if (_dragPart == null) return false;
        var p = _dragPart; _dragPart = null;
        ComponentDragEnded?.Invoke(p);
        return true;
    }

    /// <summary>u13: щелчок по ребру выбирает касательную цепочку.</summary>
    public bool AutoChain = true;
    /// <summary>u13: замена выбранного щелчком (режим «операция»: грань → операция).</summary>
    public Func<PickRef, PickRef>? PickMap;
    /// <summary>Протяжка по пустому месту выбирает областью (рамка или аркан); выключено — ничего не делает.</summary>
    public bool RegionSelect = true;
    public RegionKind RegionMode = RegionKind.Box;
    /// <summary>Контур области на экране во время протяжки (null — протяжки нет).</summary>
    public event Action<IReadOnlyList<Point>?>? RegionChanged;
    private List<Point>? _region;

    /// <summary>u13: плоскость сечения (мир): точка и нормаль; отсекается сторона, куда смотрит нормаль.</summary>
    public (Vector3d At, Vector3d N)? SectionPlane { get; private set; }
    public void SetSection((Vector3d At, Vector3d N)? plane)
    {
        if (plane == null) { Section = null; _r.CapFrame = null; SectionFrame = null; SectionSelected = false; SectionHover = false; }
        SectionPlane = plane;
        if (plane is { } pl) { var n = pl.N.Normalized(); _r.ClipPlane = new Vector4((float)n.X, (float)n.Y, (float)n.Z, (float)-Vector3d.Dot(n, pl.At)); }
        else _r.ClipPlane = Vector4.Zero;
        MarkDirty();
        SectionChanged?.Invoke();
    }
    /// <summary>
    /// k102 (замечание 7): РАЗМЕРЫ ОПЕРАЦИИ НА ВИДЕ — при правке операции (глубина выдавливания и т. п.): размерная линия со
    /// стрелками, выносные линии и подпись (значение или идентификатор — TAB), подпись — в плоскости экрана.
    /// </summary>
    public readonly List<ViewDim> OpDims = new();
    public void SetOpDims(IEnumerable<ViewDim>? dims) { OpDims.Clear(); if (dims != null) OpDims.AddRange(dims); _overlayDirty = true; Redraw(); }

    private void AddOpDims(List<Render.Engine.LineInst> lines)
    {
        if (OpDims.Count == 0 || ActiveSketch != null) return;
        static Vector3 F(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        var th = _r.Theme;
        var col = th.DimLocked; col.W = 1;
        double px = PixelSize;
        var camR = Camera.Right; var camU = Camera.Up;
        var R = new Vector3d(camR.X, camR.Y, camR.Z).Normalized(); var U = new Vector3d(camU.X, camU.Y, camU.Z).Normalized();
        foreach (var vd in OpDims)
        {
            var side = vd.Side.LengthSquared > 1e-18 ? vd.Side.Normalized() : R;
            double off = 26 * px;
            var a1 = vd.A + side * off; var b1 = vd.B + side * off;
            lines.Add(Inst(F(vd.A + side * (4 * px)), F(a1 + side * (6 * px)), col, 1.1f, 0));
            lines.Add(Inst(F(vd.B + side * (4 * px)), F(b1 + side * (6 * px)), col, 1.1f, 0));
            lines.Add(Inst(F(a1), F(b1), col, 1.1f, 0));
            var ab = b1 - a1; double len = ab.Length;
            if (len > 1e-12)
            {
                var t = ab / len;
                double ah = Math.Min(10 * px, len * 0.3), aw = ah * 0.35;
                foreach (var (tip, dir) in new[] { (a1, t), (b1, -t) })
                {
                    var n = Vector3d.Cross(t, Vector3d.Cross(R, U)); if (n.LengthSquared < 1e-18) n = side; n = n.Normalized();
                    lines.Add(Inst(F(tip), F(tip + dir * ah + n * aw), col, 1.3f, 0));
                    lines.Add(Inst(F(tip), F(tip + dir * ah - n * aw), col, 1.3f, 0));
                }
            }
            // подпись — посередине, в плоскости экрана
            string text = SketchDimensions.ShowIds ? vd.Id : vd.Value;
            double h = SketchDimensions.TextHeightPx * px;
            var mid = (a1 + b1) / 2 + side * (6 * px);
            var strokes = new List<List<Vector2d>>();
            double w = StrokeFont.Width(text) * h;
            StrokeFont.LayoutFill(text, new Vector2d(-w / 2, 2 * px), Vector2d.UnitX, Vector2d.UnitY, h, 0.7 / SketchDimensions.TextHeightPx, px * 0.4, strokes);
            if (strokes.Count == 0) StrokeFont.Layout(text, new Vector2d(-w / 2, 2 * px), Vector2d.UnitX, Vector2d.UnitY, h, strokes);
            foreach (var pl in strokes)
                for (int i = 1; i < pl.Count; i++)
                    lines.Add(Inst(F(mid + R * pl[i - 1].X + U * pl[i - 1].Y), F(mid + R * pl[i].X + U * pl[i].Y), col, Core.Settings.Current.DimensionTextOutlineWidth, 0));
            OpDimsDrawn++;
        }
    }
    /// <summary>Разбор (k102): сдвиг рёбер к глазу (мир) при нынешнем масштабе вида.</summary>
    public double EdgeBiasNow { get { UpdateEdgeBias(); return _r.EdgeBiasAt((float)PixelSize); } }
    /// <summary>Разбор: сколько размеров операции нарисовано (накопительно).</summary>
    public int OpDimsDrawn { get; private set; }

    /// <summary>k99: настройки сечения (null — сечение задано плоскостью напрямую или выключено).</summary>
    public SectionState? Section { get; private set; }
    /// <summary>k99: рамка сечения на виде — центр, оси, полуразмер (контур и крышки).</summary>
    public (Vector3d O, Vector3d U, Vector3d V, double Half)? SectionFrame { get; private set; }
    /// <summary>Щелчок по контуру или значку плоскости сечения (вне операции) — открыть её настройки.</summary>
    public event Action? SectionClicked;
    /// <summary>Плоскость сечения задана, изменена или снята.</summary>
    public event Action? SectionChanged;

    /// <summary>Применить настройки сечения: плоскость отсечения, рамка контура, крышки (центр рамки — проекция центра модели).</summary>
    public void ApplySection(SectionState? s)
    {
        if (s == null || _doc == null) { SetSection(null); return; }
        var b = _doc.Bounds;
        var (at, n, u, v) = s.Compute(b);
        Section = s;
        double diag = b.IsEmpty ? 100 : (b.Max - b.Min).Length;
        var mid = b.IsEmpty ? at : (b.Min + b.Max) / 2;
        var o = mid - n * Vector3d.Dot(mid - at, n);
        double half = Math.Max(10, diag * 0.6);
        SectionFrame = (o, u, v, half);
        SectionPlane = (at, n);
        _r.ClipPlane = new Vector4((float)n.X, (float)n.Y, (float)n.Z, (float)-Vector3d.Dot(n, at));
        _r.CapFrame = (V3(o), V3(u), V3(v), (float)half);
        _r.HatchStep = (float)Math.Max(0.2, diag / 55);
        _overlayDirty = true;
        MarkDirty();
        SectionChanged?.Invoke();
    }

    /// <summary>Углы рамки сечения (мир).</summary>
    public Vector3d[]? SectionCorners() => SectionFrame is { } f
        ? new[] { f.O - f.U * f.Half - f.V * f.Half, f.O + f.U * f.Half - f.V * f.Half, f.O + f.U * f.Half + f.V * f.Half, f.O - f.U * f.Half + f.V * f.Half }
        : null;

    /// <summary>Контур сечения подсвечен (под курсором или настройки открыты).</summary>
    public bool SectionHot { get => _sectionHot; set { if (_sectionHot == value) return; _sectionHot = value; _overlayDirty = true; Redraw(); } }
    private bool _sectionHot;

    /// <summary>Курсор у контура рамки сечения (6 px).</summary>
    public bool SectionOutlineHit(Point p)
    {
        if (SectionCorners() is not { } c) return false;
        for (int i = 0; i < 4; i++)
            if (Project(c[i]) is { } a && Project(c[(i + 1) % 4]) is { } b)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
                double ex = a.X + dx * t - p.X, ey = a.Y + dy * t - p.Y;
                if (ex * ex + ey * ey <= 36) return true;
            }
        return false;
    }

    /// <summary>Разбор: показать щелчок по контуру сечения.</summary>
    public void RaiseSectionClicked() { SectionSelected = true; SectionClicked?.Invoke(); }
    /// <summary>Разбор: двойной щелчок по контуру сечения.</summary>
    public void RaiseSectionDoubleClicked() => SectionDoubleClicked?.Invoke();
    /// <summary>k103: двойной щелчок по контуру сечения — его настройки.</summary>
    public event Action? SectionDoubleClicked;

    // ------------------------------------------------------------------ k103 (замечания 6, 7): выбор и ручки плоскости сечения

    public static readonly bool SectionSelectOff = Environment.GetEnvironmentVariable("P3D_SECTIONSELECT") == "0";

    /// <summary>Курсор у контура сечения (подсветка, «Под курсором: Плоскость сечения»).</summary>
    public bool SectionHover { get => _sectionHover; private set { if (_sectionHover == value) return; _sectionHover = value; _overlayDirty = true; Redraw(); } }
    private bool _sectionHover;

    /// <summary>
    /// Плоскость сечения выбрана (щелчок по контуру): контур — цветом выбора, ручки — стрелка вдоль нормали основы
    /// (смещение) и кольца вокруг осей основы (углы A и B); тянут — сечение меняется сразу. Щелчок мимо снимает выбор.
    /// </summary>
    public bool SectionSelected
    {
        get => _sectionSelected;
        set
        {
            value = value && SectionFrame != null && !SectionSelectOff;
            if (_sectionSelected == value) return;
            _sectionSelected = value;
            if (value) { Selection.Clear(); Gizmo = SectionGizmoSpec(); _sectionGizmo = true; }
            else if (_sectionGizmo) { _sectionGizmo = false; Gizmo = null; }
            _overlayDirty = true; Redraw();
            SectionSelectionChanged?.Invoke();
        }
    }
    private bool _sectionSelected, _sectionGizmo;
    public event Action? SectionSelectionChanged;

    /// <summary>Ручки сечения: начало — точка плоскости, оси — основы (поворот A — вокруг первой, B — вокруг второй).</summary>
    public GizmoSpec? SectionGizmoSpec()
    {
        if (Section is not { } s || SectionPlane is not { } sp) return null;
        var f = s.BaseFrame();
        return new GizmoSpec(GizmoMode.MoveRotate, sp.At, f.U.Normalized(), f.V.Normalized(), f.W.Normalized());
    }

    private double _sg0;
    private GizmoPart _sgPart = GizmoPart.None;
    /// <summary>Перетаскивание ручки выбранного сечения (вне настройки).</summary>
    public void SectionGizmoStart(GizmoPart part)
    {
        if (Section is not { } s) return;
        _sgPart = part;
        _sg0 = part == GizmoPart.AxisX ? s.AngleA : part == GizmoPart.AxisY ? s.AngleB : s.Offset;
    }
    public void SectionGizmoMove(GizmoDrag d)
    {
        if (Section is not { } s) return;
        switch (_sgPart)
        {
            case GizmoPart.AxisX: s.AngleA = Math.Round(_sg0 + d.AngleDeg, 2); break;
            case GizmoPart.AxisY: s.AngleB = Math.Round(_sg0 + d.AngleDeg, 2); break;
            case GizmoPart.AxisZ: s.Offset = Math.Round(_sg0 + d.Delta.Z, 3); break;
            default: return;
        }
        ApplySection(s);
    }
    public void SectionGizmoEnd()
    {
        _sgPart = GizmoPart.None;
        if (_sectionSelected) Gizmo = SectionGizmoSpec();
    }

    /// <summary>Разбор: наведение курсора в точку p (подсветка контура сечения).</summary>
    public bool SectionHoverAt(Point p) => UpdateSectionHover(p);

    /// <summary>Наведение на контур сечения (вне эскиза и инструмента): подсветка; true — курсор на контуре.</summary>
    private bool UpdateSectionHover(Point p)
    {
        bool hit = SectionFrame != null && ActiveSketch == null && Tool == null && !SectionSelectOff && SectionOutlineHit(p);
        if (hit != SectionHover)
        {
            SectionHover = hit;
            if (hit && Selection.SetHover(PickRef.Empty)) _flagsDirty = true;
            HoverChanged?.Invoke(hit ? Loc.L("Плоскость сечения") : "");
        }
        return hit;
    }

    /// <summary>u13: полосы-отражения («зебра») на гранях.</summary>
    public bool Zebra { get => _r.Zebra; set { _r.Zebra = value; Redraw(); } }

    /// <summary>Точка мира → экран (DIP); null — за камерой.</summary>
    public Point? Project(Vector3d w)
    {
        var vp = Camera.ViewMatrix * Camera.ProjMatrix;
        var c = new Vector4((float)w.X, (float)w.Y, (float)w.Z, 1f) * vp;
        if (c.W <= 1e-9f) return null;
        double s = Scaling;
        double x = (c.X / c.W + 1) * 0.5 * Camera.ViewportWidth, y = (1 - c.Y / c.W) * 0.5 * Camera.ViewportHeight;
        return new Point(x / s, y / s);
    }

    private bool RegionMove(Point p)
    {
        if (_drag != Drag.Select || !RegionSelect) return false;
        if (_region == null)
        {
            if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4) return false;
            _region = new List<Point> { _down };
        }
        if (RegionMode == RegionKind.Lasso) { if ((p - _region[^1]).X * (p - _region[^1]).X + (p - _region[^1]).Y * (p - _region[^1]).Y > 9) _region.Add(p); }
        else _region = new List<Point> { _down, new(p.X, _down.Y), p, new(_down.X, p.Y) };
        RegionChanged?.Invoke(_region);
        return true;
    }

    private bool RegionEnd(Point p, KeyModifiers mods)
    {
        if (_region == null) return false;
        var poly = _region; _region = null;
        RegionChanged?.Invoke(null);
        bool crossing = RegionMode == RegionKind.Box && p.X < _down.X;
        var items = ItemsInRegion(poly, crossing);
        if (!mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Shift)) Selection.Clear();
        foreach (var r in items) if (!Selection.Contains(r.Id)) Selection.Select(r, additive: true);
        _flagsDirty = true; Redraw();
        return true;
    }

    static bool InPoly(IReadOnlyList<Point> poly, Point q)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    /// <summary>Что попадает в область: целиком (окно) или хоть одной точкой (секущая рамка). Учитывает фильтр выбора.</summary>
    static readonly bool RegionAllPointsOff = Environment.GetEnvironmentVariable("P3D_REGIONALLPOINTS") == "0";

    public List<PickRef> ItemsInRegion(IReadOnlyList<Point> poly, bool crossing)
    {
        var res = new List<PickRef>();
        if (_doc == null || poly.Count < 3) return res;
        bool Hit(IEnumerable<Vector3d> pts)
        {
            bool any = false, all = true, seen = false;
            foreach (var w in pts)
            {
                seen = true;
                var q = Project(w);
                bool inn = q is { } qq && InPoly(poly, qq);
                any |= inn; all &= inn;
                if (crossing && any) return true;
                if (!crossing && !all) return false;
            }
            return seen && (crossing ? any : all);
        }
        if (ActiveSketch is { } sk)
        {
            var pl = sk.Plane;
            Vector3d W(Vector2d p) => pl.Origin + pl.U * p.X + pl.V * p.Y;
            foreach (var e in sk.Entities)
            {
                if (e is SkPoint sp)
                {
                    if (Selection.Accepts(PickKind.SketchPoint) && Hit(new[] { W(sp.P) })) res.Add(new PickRef(sp.Id, PickKind.SketchPoint, sk, sp));
                    continue;
                }
                if (!Selection.Accepts(PickKind.SketchCurve)) continue;
                List<Vector3d> pts;
                try { var l = new List<Vector3d>(); e.ToCurve3(pl).Tessellate(l, true); pts = l; } catch { continue; }
                if (Hit(pts)) res.Add(new PickRef(e.Id, PickKind.SketchCurve, sk, e));
            }
            // k96 (п. 5): точки фигур — центры окружностей и дуг, концы отрезков — в рамке выбираются, если сама фигура не
            // выбрана (рамка вокруг двух центров выбирает центры)
            if (Selection.Accepts(PickKind.SketchPoint) && !RegionPointsOff)
            {
                var chosen = new HashSet<SkEntity>(res.Select(r => (SkEntity)r.Target!), ReferenceEqualityComparer.Instance);
                var seen = new HashSet<SkPoint>(ReferenceEqualityComparer.Instance);
                foreach (var e in sk.Entities)
                {
                    // k98 (замечание 10): и у выбранных рамкой фигур — их точки тоже (прежде — только у невыбранных: рамка вокруг
                    // ломаной давала отрезки и одну точку). P3D_REGIONALLPOINTS=0 — прежнее.
                    if (e is SkPoint || (chosen.Contains(e) && RegionAllPointsOff) || e.Role == SkRole.Reference) continue;
                    foreach (var h in e.Handles)
                        if (seen.Add(h) && !res.Any(r => ReferenceEquals(r.Target, h)) && Hit(new[] { W(h.P) })) res.Add(new PickRef(h.Id, PickKind.SketchPoint, sk, h));
                }
            }
            return res;
        }
        foreach (var part in Parts(_doc.Root))
        {
            if (!part.Visible || part.Mesh is not { } md) continue;
            if (ActivePart != null && !ReferenceEquals(part, ActivePart)) continue;
            if (Selection.Accepts(PickKind.Face))
                foreach (var g in md.Vertices.GroupBy(v => v.FaceId))
                    if (Hit(g.Select(v => new Vector3d(v.P.X, v.P.Y, v.P.Z)))) res.Add(new PickRef(g.Key, PickKind.Face, part, null));
            if (Selection.Accepts(PickKind.Edge))
                foreach (var (id, pts) in md.EdgePolylines)
                    if (Hit(pts.Select(v => new Vector3d(v.X, v.Y, v.Z)))) res.Add(new PickRef(id, PickKind.Edge, part, null));
            if (Selection.Accepts(PickKind.Vertex) && Selection.Filter != PickFilter.All)
                foreach (var (id, p) in md.Points)
                    if (Hit(new[] { new Vector3d(p.X, p.Y, p.Z) })) res.Add(new PickRef(id, PickKind.Vertex, part, null));
        }
        return res;
    }

    /// <summary>u13: линии поверх модели — гребёнка кривизны, измерения (мир, цвет RGBA).</summary>
    public readonly List<(Vector3d A, Vector3d B, Vector4 C)> Annotations = new();
    public void SetAnnotations(IEnumerable<(Vector3d A, Vector3d B, Vector4 C)> lines) { Annotations.Clear(); Annotations.AddRange(lines); _overlayDirty = true; Redraw(); }
    /// <summary>u13: сетка на плоскости XY в режиме детали.</summary>
    public bool ModelGrid { get => _modelGrid; set { _modelGrid = value; _overlayDirty = true; Redraw(); } }
    private bool _modelGrid;

    /// <summary>u13k: клетки SubD (FreeForm) — тонкие линии поверх модели; складки — красным.</summary>
    public readonly List<(Vector3d A, Vector3d B, bool Crease)> CageLines = new();
    public void SetCages(IEnumerable<(Vector3d A, Vector3d B, bool Crease)> lines) { CageLines.Clear(); CageLines.AddRange(lines); _overlayDirty = true; Redraw(); }

    private void AddExtraOverlay(List<Render.Engine.LineInst> grid, List<Render.Engine.LineInst> lines)
    {
        static Vector3 F(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        foreach (var (a, b, c) in Annotations) lines.Add(Inst(F(a), F(b), c, 1.2f, 0));
        AddOpDims(lines);
        // k99: контур плоскости сечения (без заливки)
        if (SectionCorners() is { } sc && ActiveSketch == null)
        {
            // k103: под курсором — цвет наведения, выбрана — цвет выбора
            bool hot = SectionHot || SectionHover;
            var col = hot ? new Vector4(1f, 0.75f, 0.2f, 1f) : SectionSelected ? _r.Theme.Select : new Vector4(0.35f, 0.62f, 1f, 0.95f);
            if (SectionSelected && !hot) col.W = 1;
            for (int i = 0; i < 4; i++) lines.Add(Inst(F(sc[i]), F(sc[(i + 1) % 4]), col, hot || SectionSelected ? 2.4f : 1.5f, 0));
        }
        if (Annotations.Count == 0)
            foreach (var (a, b, cr) in CageLines) lines.Add(Inst(F(a), F(b), cr ? new Vector4(1f, 0.4f, 0.3f, 0.9f) : new Vector4(0.35f, 0.75f, 1f, 0.55f), 1f, 0));
        if (!_modelGrid || ActiveSketch != null) return;
        var th = _r.Theme;
        double step = GridStep();
        var c3 = Camera.State.Target;
        var gf = GridFrame;
        var loc = gf.ToLocal2(new Vector3d(c3.X, c3.Y, c3.Z));
        double half = Math.Max(Bounds.Width, Bounds.Height) * PixelSize * 0.75;
        int n = (int)Math.Min(200, Math.Ceiling(half / step));
        double x0 = Math.Round(loc.X / step) * step, y0 = Math.Round(loc.Y / step) * step;
        var minor = th.Dark ? new Vector4(1, 1, 1, 0.05f) : new Vector4(0, 0, 0, 0.06f);
        var major = th.Dark ? new Vector4(1, 1, 1, 0.11f) : new Vector4(0, 0, 0, 0.12f);
        for (int i = -n; i <= n; i++)
        {
            double x = x0 + i * step, y = y0 + i * step;
            bool mx = Math.Abs(Math.Round(x / step) % 5) < 0.5, my = Math.Abs(Math.Round(y / step) % 5) < 0.5;
            grid.Add(Inst(F(gf.ToWorld(x, y0 - n * step)), F(gf.ToWorld(x, y0 + n * step)), mx ? major : minor, 1f, 0));
            grid.Add(Inst(F(gf.ToWorld(x0 - n * step, y)), F(gf.ToWorld(x0 + n * step, y)), my ? major : minor, 1f, 0));
        }
    }

    /// <summary>u13h: плоскость сетки детали (в FreeForm — плоскость построения).</summary>
    public Frame3 GridFrame { get => _gridFrame; set { _gridFrame = value; _overlayDirty = true; Redraw(); } }
    private Frame3 _gridFrame = Frame3.XY;

    static IEnumerable<Part> Parts(ModelItem it)
    {
        if (it is Part p) yield return p;
        foreach (var c in it.Children) foreach (var q in Parts(c)) yield return q;
    }
}

/// <summary>k102: размер операции на виде: от A до B, выносится в сторону Side; подпись — значение или идентификатор.</summary>
public sealed record ViewDim(Vector3d A, Vector3d B, Vector3d Side, string Value, string Id);
