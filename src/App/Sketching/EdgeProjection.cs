using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Sketching;

/// <summary>
/// ПРОЕКЦИЯ РЁБЕР ТЕЛА НА ПЛОСКОСТЬ ЭСКИЗА: отрезок — отрезком, окружность и дуга в параллельной плоскости — окружностью
/// и дугой, остальное — ломаной. Точки концов общие (через фабрику), чтобы контур замыкался по общим точкам.
/// </summary>
public static class EdgeProjection
{
    public static Vector2d On(Sketch sk, Vector3d p) => sk.Plane.ToLocal2(p);

    /// <summary>Общие точки по координате (сшивка концов соседних рёбер).</summary>
    public sealed class PointPool
    {
        private readonly List<SkPoint> _pts = new();
        private readonly SkRole _role;
        private readonly string _source;
        public PointPool(SkRole role, string source) { _role = role; _source = source; }
        public SkPoint Get(Vector2d p)
        {
            foreach (var q in _pts) if ((q.P - p).Length < 1e-6) return q;
            var n = new SkPoint(p) { Role = _role, ReferenceSource = _source };
            _pts.Add(n);
            return n;
        }
    }

    public static List<SkEntity> Project(Sketch sk, ICurve3 c, PointPool pool, SkRole role, string source)
    {
        var res = new List<SkEntity>();
        T Mark<T>(T e) where T : SkEntity { e.Role = role; e.ReferenceSource = source; res.Add(e); return e; }
        var n = sk.Plane.W;
        switch (c)
        {
            case LineCurve3 l:
            {
                var a = On(sk, l.A); var b = On(sk, l.B);
                if ((a - b).Length < 1e-7) Mark(pool.Get(a));
                else Mark(new SkLine(pool.Get(a), pool.Get(b)));
                return res;
            }
            case ArcCurve3 arc when Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), n)) - 1) < 1e-9:
            {
                var ctr = On(sk, arc.Frame.Origin);
                if (arc.IsClosed) { Mark(new SkCircle(pool.Get(ctr), arc.Radius)); return res; }
                var s = On(sk, arc.PointAt(arc.T0)); var e = On(sk, arc.PointAt(arc.T1)); var m = On(sk, arc.PointAt(0.5 * (arc.T0 + arc.T1)));
                // центр и направление обхода — по трём точкам дуги
                var sa = SkArc.FromThreePoints(s, m, e);
                if (sa != null) { Mark(new SkArc(pool.Get(sa.Center.P), pool.Get(s), pool.Get(e), sa.CCW)); return res; }
                break;
            }
        }
        // общий случай — ломаная
        var pts = new List<Vector3d>();
        c.Tessellate(pts, true);
        var p2 = pts.Select(p => On(sk, p)).ToList();
        for (int i = 1; i < p2.Count; i++)
            if ((p2[i] - p2[i - 1]).Length > 1e-7) Mark(new SkLine(pool.Get(p2[i - 1]), pool.Get(p2[i])));
        return res;
    }
}

/// <summary>ССЫЛКИ ЭСКИЗА (как «References» в Creo): рёбра и вершины тела, спроецированные в эскиз как ссылочная геометрия.</summary>
public static class SketchReferences
{
    public static string SourceOf(PickRef r) => r.Kind == PickKind.Vertex ? $"Вершина #{r.Id}" : $"Ребро #{r.Id}";

    /// <summary>Добавить ссылку; null — добавлена, иначе — почему нет.</summary>
    public static string? Add(Sketch sk, Part part, PickRef r)
    {
        string src = SourceOf(r);
        if (sk.Entities.Any(e => e.Role == SkRole.Reference && e.ReferenceSource == src)) return Loc.L("Уже в ссылках");
        var pool = new EdgeProjection.PointPool(SkRole.Reference, src);
        if (r.Kind == PickKind.Vertex && part.FindVertex(r.Id) is { } v)
            sk.AddEntity(pool.Get(EdgeProjection.On(sk, v.P)));
        else if (r.Kind == PickKind.Edge && part.FindEdge(r.Id) is { Curve: { } c })
            foreach (var e in EdgeProjection.Project(sk, c, pool, SkRole.Reference, src)) sk.AddEntity(e);
        else return Loc.L("Ссылка не найдена");
        if (!sk.ReferenceIds.Contains(r.Id)) sk.ReferenceIds.Add(r.Id);
        sk.BumpStructure();
        return null;
    }

    public static IEnumerable<string> Sources(Sketch sk) =>
        sk.Entities.Where(e => e.Role == SkRole.Reference).Select(e => e.ReferenceSource).Where(s => s.Length > 0).Distinct();

    /// <summary>Убрать ссылку и связи, наложенные на неё.</summary>
    public static void Remove(Sketch sk, string source)
    {
        var gone = sk.Entities.Where(e => e.Role == SkRole.Reference && e.ReferenceSource == source).ToList();
        var pts = new HashSet<SkEntity>(gone.SelectMany(e => e.Handles).Concat(gone));
        sk.Constraints.RemoveAll(c => c.Items.Any(pts.Contains));
        sk.Dimensions.RemoveAll(d => d.Items.Any(pts.Contains));
        foreach (var e in gone) sk.Entities.Remove(e);
        if (source.Split('#') is [_, var id] && uint.TryParse(id, out var n)) sk.ReferenceIds.Remove(n);
        sk.BumpStructure();
    }
}
