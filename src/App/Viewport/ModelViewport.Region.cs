using Avalonia;
using Avalonia.Input;
using OpenTK.Mathematics;
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
        SectionPlane = plane;
        if (plane is { } pl) { var n = pl.N.Normalized(); _r.ClipPlane = new Vector4((float)n.X, (float)n.Y, (float)n.Z, (float)-Vector3d.Dot(n, pl.At)); }
        else _r.ClipPlane = Vector4.Zero;
        MarkDirty();
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
                    if (e is SkPoint || chosen.Contains(e) || e.Role == SkRole.Reference) continue;
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
