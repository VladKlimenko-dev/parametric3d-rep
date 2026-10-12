using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// KRN-07 (07.5, 07.6): ЗАМЕНА ПЛОСКОСТЕЙ ГРАНЕЙ без смены топологии. Выбранные плоские грани получают новые плоскости
/// (точка и НАРУЖНАЯ нормаль); каждая вершина, которой касается такая грань, — пересечение трёх независимых плоскостей её
/// граней (прочие обязаны проходить через ту же точку); рёбра у сдвинутых вершин — отрезки между новыми вершинами. Отказ
/// (null и причина): у сдвигаемой вершины кривая грань или кривое ребро; вершина не задана тремя независимыми плоскостями;
/// ребро исчезло бы или сменило направление (топология изменилась бы); результат негоден (B-rep, сетка, объём). Результат —
/// новое тело (вход не меняется), ExactAnalytic.
/// </summary>
public static class PlanarTweak
{
    static Geometry.Ssi.Plane? PlaneOf(TFace f) => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) as Geometry.Ssi.Plane;

    /// <summary>Наружная нормаль плоской грани — по самой поверхности и обращению грани (не по нормали плоскости-формы, у которой знак свой).</summary>
    public static Vector3d? OutwardNormal(TFace f)
    {
        if (PlaneOf(f) is not { } pl || GeometryAudit.AuthoritativeSurface(f) is not { } src) return null;
        Vector3d n;
        if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0)
        {
            // у гранёной грани сторону несут треугольники (источник может смотреть в другую сторону при Reversed = false)
            n = Vector3d.Zero;
            foreach (var (a, b, c) in fs.Facets.ReadView) n += Vector3d.Cross(b - a, c - a);
            if (f.Reversed) n = -n;
        }
        else n = src.NormalAt(src.ProjectToUV(pl.P)) * (f.Reversed ? -1 : 1);
        if (n.LengthSquared < 1e-24) return null;
        var np = pl.N.Normalized();
        return Vector3d.Dot(np, n) >= 0 ? np : -np;
    }

    /// <param name="newPlane">Для грани (номер в body.Faces и сама грань) — новая плоскость: точка и наружная нормаль; null — грань не меняется.</param>
    public static TSolid? Apply(TSolid body, Func<int, TFace, (Vector3d P, Vector3d N)?> newPlane, ref string? why)
    {
        var faces = body.Faces.ToList();
        var picks = new Dictionary<int, (Vector3d P, Vector3d N)>();
        for (int i = 0; i < faces.Count; i++) if (newPlane(i, faces[i]) is { } q) picks[i] = (q.P, q.N.Normalized());
        if (picks.Count == 0) { why = "no faces"; return null; }
        var clone = TopologyClone.Deep(body);
        var cf = clone.Faces.ToList();
        if (cf.Count != faces.Count) { why = "clone mismatch"; return null; }
        var faceOfLoop = new Dictionary<TVertex, HashSet<int>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < cf.Count; i++)
            foreach (var ce in cf[i].Loops.SelectMany(l => l.CoEdges))
                foreach (var v in new[] { ce.Start, ce.End })
                {
                    if (!faceOfLoop.TryGetValue(v, out var set)) faceOfLoop[v] = set = new HashSet<int>();
                    set.Add(i);
                }
        (Vector3d P, Vector3d N)? Pl(int i)
        {
            if (picks.TryGetValue(i, out var q)) return q;
            return PlaneOf(faces[i]) is { } pl ? (pl.P, pl.N.Normalized()) : null;
        }
        double scale = Math.Max(1, body.Vertices.Max(v => v.P.Length));
        var moved = new Dictionary<TVertex, Vector3d>(ReferenceEqualityComparer.Instance);
        foreach (var (v, fs) in faceOfLoop)
        {
            if (!fs.Any(picks.ContainsKey)) continue;
            var pls = new List<(Vector3d P, Vector3d N)>();
            foreach (int i in fs) { if (Pl(i) is not { } q) { why = "a non-planar face meets a drafted face"; return null; } pls.Add(q); }
            // три независимые плоскости (сначала выбранные), прочие — проверка
            var order = fs.OrderByDescending(picks.ContainsKey).Select(i => Pl(i)!.Value).ToList();
            (Vector3d P, Vector3d N)[]? basis = null;
            for (int x = 0; x < order.Count && basis == null; x++)
                for (int y = x + 1; y < order.Count && basis == null; y++)
                    for (int z = y + 1; z < order.Count && basis == null; z++)
                        if (Math.Abs(Vector3d.Dot(order[x].N, Vector3d.Cross(order[y].N, order[z].N))) > 1e-6) basis = new[] { order[x], order[y], order[z] };
            if (basis == null) { why = "a vertex is not fixed by three independent planes"; return null; }
            var n1 = basis[0].N; var n2 = basis[1].N; var n3 = basis[2].N;
            double d1 = Vector3d.Dot(n1, basis[0].P), d2 = Vector3d.Dot(n2, basis[1].P), d3 = Vector3d.Dot(n3, basis[2].P);
            double det = Vector3d.Dot(n1, Vector3d.Cross(n2, n3));
            var x0 = (Vector3d.Cross(n2, n3) * d1 + Vector3d.Cross(n3, n1) * d2 + Vector3d.Cross(n1, n2) * d3) / det;
            if (order.Any(q => Math.Abs(Vector3d.Dot(x0 - q.P, q.N)) > 1e-9 * scale)) { why = "a vertex of more than three faces would leave one of them"; return null; }
            moved[v] = x0;
        }
        foreach (var e in clone.Edges.Where(e => moved.ContainsKey(e.V0) || moved.ContainsKey(e.V1)))
        {
            if (e.Curve is not LineCurve3) { why = "a curved edge meets a drafted face"; return null; }
            var a0 = moved.TryGetValue(e.V0, out var q0) ? q0 : e.V0.P; var a1 = moved.TryGetValue(e.V1, out var q1) ? q1 : e.V1.P;
            var od = e.V1.P - e.V0.P; var nd = a1 - a0;
            if (nd.Length < 1e-6 || Vector3d.Dot(od, nd) <= 0) { why = "an edge would vanish or turn over (the angle changes the topology)"; return null; }
        }
        foreach (var (v, p) in moved) v.P = p;
        var outward = faces.Select(OutwardNormal).ToList();
        var touched = new HashSet<int>();
        foreach (var (v, fs) in faceOfLoop) if (moved.ContainsKey(v)) touched.UnionWith(fs);
        foreach (var e in clone.Edges.Where(e => moved.ContainsKey(e.V0) || moved.ContainsKey(e.V1)))
        { e.Curve = new LineCurve3(e.V0.P, e.V1.P); e.PolylineCache = null; }
        foreach (int i in touched)
        {
            var f = cf[i];
            // плоскость с НАРУЖНОЙ нормалью и Reversed = false: сторона не зависит от того, как была записана исходная грань
            if (picks.TryGetValue(i, out var q)) { f.Surface = new PlaneSurface(Frame3.FromNormal(q.P, q.N)); f.Reversed = false; }
            else if (outward[i] is { } no && PlaneOf(faces[i]) is { } pl) { f.Surface = new PlaneSurface(Frame3.FromNormal(pl.P, no)); f.Reversed = false; }
            else { why = "a touched face has no outward normal"; return null; }
            f.InteriorHint = null;
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { ce.PCurve = null; ce.PCurveDeviation = 0; }
        }
        clone.Exact = null; clone.QExact = null; clone.DisplayMeshKey = null;
        var w = new List<string>();
        if (Analytic.PrismBuild.Finish(clone, w) > 0) { why = "faces not meshed: " + string.Join(" / ", w.Take(2)); return null; }
        clone.Guarantee = GuaranteeLevel.ExactAnalytic;
        var vr = SolidValidator.Validate(clone);
        if (!vr.IsValid) { why = "result invalid: " + vr.Summary() + " " + string.Join(" / ", vr.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)); return null; }
        if (AnalyticVolume.Of(clone) is not { } vol || vol <= 0) { why = "no analytic volume"; return null; }
        if (BooleanOp.VolumeVsFacets(clone) is { } vf && !vf.Agree) { why = $"volume {vf.Va:G8} vs facets {vf.Vf:G8}"; return null; }
        return clone;
    }
}
