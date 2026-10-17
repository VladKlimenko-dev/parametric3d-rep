using Avalonia;
using Avalonia.Input;
using OpenTK.Mathematics;
using Parametric3D.Core;
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

    private void AddExtraOverlay(List<Render.Engine.LineInst> grid, List<Render.Engine.LineInst> lines)
    {
        static Vector3 F(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        foreach (var (a, b, c) in Annotations) lines.Add(Inst(F(a), F(b), c, 1.2f, 0));
        if (!_modelGrid || ActiveSketch != null) return;
        var th = _r.Theme;
        double step = GridStep();
        var c3 = Camera.State.Target;
        double half = Math.Max(Bounds.Width, Bounds.Height) * PixelSize * 0.75;
        int n = (int)Math.Min(200, Math.Ceiling(half / step));
        double x0 = Math.Round(c3.X / step) * step, y0 = Math.Round(c3.Y / step) * step;
        var minor = th.Dark ? new Vector4(1, 1, 1, 0.05f) : new Vector4(0, 0, 0, 0.06f);
        var major = th.Dark ? new Vector4(1, 1, 1, 0.11f) : new Vector4(0, 0, 0, 0.12f);
        for (int i = -n; i <= n; i++)
        {
            double x = x0 + i * step, y = y0 + i * step;
            bool mx = Math.Abs(Math.Round(x / step) % 5) < 0.5, my = Math.Abs(Math.Round(y / step) % 5) < 0.5;
            grid.Add(Inst(new Vector3((float)x, (float)(y0 - n * step), 0), new Vector3((float)x, (float)(y0 + n * step), 0), mx ? major : minor, 1f, 0));
            grid.Add(Inst(new Vector3((float)(x0 - n * step), (float)y, 0), new Vector3((float)(x0 + n * step), (float)y, 0), my ? major : minor, 1f, 0));
        }
    }

    static IEnumerable<Part> Parts(ModelItem it)
    {
        if (it is Part p) yield return p;
        foreach (var c in it.Children) foreach (var q in Parts(c)) yield return q;
    }
}
