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
    /// <summary>Мутация P3D_TWEAKPROV=0: результат замены плоскостей (уклон, оболочка) без карты происхождения.</summary>
    internal static readonly bool ProvOff = Environment.GetEnvironmentVariable("P3D_TWEAKPROV") == "0";

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

    /// <summary>Выпуклая ли цилиндрическая грань (материал внутри): наружная нормаль грани смотрит от оси.</summary>
    public static bool? CylConvex(TFace f)
    {
        if (Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is not Geometry.Ssi.Cylinder cy) return null;
        var src = GeometryAudit.AuthoritativeSurface(f)!;
        var w = cy.Axis.Normalized();
        var ce0 = f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault(); if (ce0 == null) return null;
        var q0 = ce0.Edge.Curve.PointAt(ce0.Edge.Curve.T0 + 0.3 * (ce0.Edge.Curve.T1 - ce0.Edge.Curve.T0));
        Vector3d nq;
        if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0)
        {
            var best = fs.Facets.ReadView.OrderBy(t => ((t.A + t.B + t.C) / 3 - q0).LengthSquared).First();
            nq = Vector3d.Cross(best.B - best.A, best.C - best.A) * (f.Reversed ? -1 : 1);
        }
        else nq = src.NormalAt(src.ProjectToUV(q0)) * (f.Reversed ? -1 : 1);
        var rad = q0 - cy.P; rad -= w * Vector3d.Dot(rad, w);
        return Vector3d.Dot(nq, rad) > 0;
    }

    /// <param name="newPlane">Для грани (номер в body.Faces и сама грань) — новая плоскость: точка и наружная нормаль; null — грань не меняется.</param>
    /// <param name="newRadius">KRN-07 (07.5): для цилиндрической грани — новый радиус (та же ось); null — не меняется.
    /// Цилиндр ограничен полными окружностями в плоскостях поперёк оси; окружность идёт за своей плоскостью (центр — на оси)
    /// и за радиусом своего цилиндра.</param>
    public static TSolid? Apply(TSolid body, Func<int, TFace, (Vector3d P, Vector3d N)?> newPlane, ref string? why, Func<int, TFace, double?>? newRadius = null)
    {
        var faces = body.Faces.ToList();
        var picks = new Dictionary<int, (Vector3d P, Vector3d N)>();
        var radii = new Dictionary<int, double>();
        for (int i = 0; i < faces.Count; i++)
        {
            if (newPlane(i, faces[i]) is { } q) picks[i] = (q.P, q.N.Normalized());
            else if (newRadius?.Invoke(i, faces[i]) is double r) radii[i] = r;
        }
        if (picks.Count == 0 && radii.Count == 0) { why = "no faces"; return null; }
        var clone = TopologyClone.Deep(body);
        var cf = clone.Faces.ToList();
        if (cf.Count != faces.Count) { why = "clone mismatch"; return null; }
        var idxOf = new Dictionary<TFace, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < cf.Count; i++) idxOf[cf[i]] = i;
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
        Geometry.Ssi.Cylinder? CylOf(int i) => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(faces[i])) as Geometry.Ssi.Cylinder;
        // окружности плоскость/цилиндр: замкнутое ребро-дуга между плоской гранью поперёк оси и цилиндром
        var circles = new Dictionary<TEdge, (int Plane, int Cyl)>(ReferenceEqualityComparer.Instance);
        foreach (var e in clone.Edges)
        {
            if (e.Curve is not ArcCurve3 ac || !ReferenceEquals(e.V0, e.V1)) continue;
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Select(f => idxOf[f]).Distinct().ToList();
            if (fs.Count != 2) continue;
            int ip = fs.FirstOrDefault(k => PlaneOf(faces[k]) != null, -1), ic = fs.FirstOrDefault(k => CylOf(k) != null, -1);
            if (ip < 0 || ic < 0) continue;
            var w = CylOf(ic)!.Axis.Normalized();
            if (Math.Abs(Math.Abs(Vector3d.Dot(w, ac.Frame.W.Normalized())) - 1) > 1e-9 || Math.Abs(Math.Abs(Vector3d.Dot(w, PlaneOf(faces[ip])!.N.Normalized())) - 1) > 1e-9) continue;
            circles[e] = (ip, ic);
        }
        foreach (var (i, _) in radii)
            foreach (var e in cf[i].Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge))
                if (!circles.ContainsKey(e)) { why = "a cylinder to offset is not bounded by full circles across its axis"; return null; }
        var circleVerts = new HashSet<TVertex>(circles.Keys.Select(e => e.V0), ReferenceEqualityComparer.Instance);
        double scale = Math.Max(1, body.Vertices.Max(v => v.P.Length));
        var moved = new Dictionary<TVertex, Vector3d>(ReferenceEqualityComparer.Instance);
        foreach (var (v, fs) in faceOfLoop)
        {
            if (!fs.Any(picks.ContainsKey) || circleVerts.Contains(v)) continue;
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
        // окружности: центр — пересечение оси с (новой) плоскостью, радиус — (новый) радиус цилиндра
        var newArcs = new Dictionary<TEdge, ArcCurve3>(ReferenceEqualityComparer.Instance);
        foreach (var (e, (ip, ic)) in circles)
        {
            if (!picks.ContainsKey(ip) && !radii.ContainsKey(ic)) continue;
            var v = e.V0;
            if (faceOfLoop[v].Count != 2) { why = "a circle vertex is shared with other faces"; return null; }
            var ac = (ArcCurve3)e.Curve; var cy = CylOf(ic)!; var w = cy.Axis.Normalized();
            var (pp, pn) = Pl(ip)!.Value;
            double den = Vector3d.Dot(w, pn); if (Math.Abs(den) < 1e-12) { why = "a circle plane is along the axis"; return null; }
            var c = ac.Frame.Origin; var c2 = c + w * (Vector3d.Dot(pp - c, pn) / den);
            double r2 = radii.TryGetValue(ic, out var rr) ? rr : ac.Radius;
            if (!(r2 > 1e-6)) { why = "a circle would shrink to a point"; return null; }
            var na = new ArcCurve3(new Frame3(c2, ac.Frame.U, ac.Frame.V), r2, ac.A0, ac.A1);
            newArcs[e] = na; moved[v] = na.PointAt(na.T0);
        }
        foreach (var e in clone.Edges.Where(e => (moved.ContainsKey(e.V0) || moved.ContainsKey(e.V1)) && !newArcs.ContainsKey(e)))
        {
            if (e.Curve is not LineCurve3) { why = "a curved edge meets a drafted face"; return null; }
            var a0 = moved.TryGetValue(e.V0, out var q0) ? q0 : e.V0.P; var a1 = moved.TryGetValue(e.V1, out var q1) ? q1 : e.V1.P;
            var od = e.V1.P - e.V0.P; var nd = a1 - a0;
            if (nd.Length < 1e-6 || Vector3d.Dot(od, nd) <= 0) { why = "an edge would vanish or turn over (the angle changes the topology)"; return null; }
        }
        // цилиндр с окружностями, ушедшими друг за друга (высота ≤ 0), — отказ
        foreach (var ic in circles.Values.Select(x => x.Cyl).Distinct())
        {
            var es = circles.Where(kv => kv.Value.Cyl == ic).Select(kv => kv.Key).ToList();
            if (es.Count != 2) continue;
            var w = CylOf(ic)!.Axis.Normalized();
            ArcCurve3 A(TEdge e) => newArcs.TryGetValue(e, out var na) ? na : (ArcCurve3)e.Curve;
            double h0 = Vector3d.Dot(((ArcCurve3)es[1].Curve).Frame.Origin - ((ArcCurve3)es[0].Curve).Frame.Origin, w);
            double h1 = Vector3d.Dot(A(es[1]).Frame.Origin - A(es[0]).Frame.Origin, w);
            if (Math.Abs(h1) < 1e-6 || h0 * h1 <= 0) { why = "a cylinder would vanish or turn over"; return null; }
        }
        var cylConvex = radii.Keys.Concat(circles.Values.Select(x => x.Cyl)).Distinct().ToDictionary(i => i, i => CylConvex(faces[i]));
        foreach (var (v, p) in moved) v.P = p;
        var outward = faces.Select(OutwardNormal).ToList();
        var touched = new HashSet<int>();
        foreach (var (v, fs) in faceOfLoop) if (moved.ContainsKey(v)) touched.UnionWith(fs);
        foreach (var i in radii.Keys) touched.Add(i);
        foreach (var e in clone.Edges.Where(e => moved.ContainsKey(e.V0) || moved.ContainsKey(e.V1)))
        { e.Curve = newArcs.TryGetValue(e, out var na) ? na : new LineCurve3(e.V0.P, e.V1.P); e.PolylineCache = null; }
        foreach (int i in touched)
        {
            var f = cf[i];
            // плоскость с НАРУЖНОЙ нормалью и Reversed = false: сторона не зависит от того, как была записана исходная грань
            if (picks.TryGetValue(i, out var q)) { f.Surface = new PlaneSurface(Frame3.FromNormal(q.P, q.N)); f.Reversed = false; }
            else if (outward[i] is { } no && PlaneOf(faces[i]) is { } pl) { f.Surface = new PlaneSurface(Frame3.FromNormal(pl.P, no)); f.Reversed = false; }
            else if (CylOf(i) is { } cy && GeometryAudit.AuthoritativeSurface(faces[i]) is ExtrudedSurface ex && ex.Base is ArcCurve3 ba
                     && cylConvex.TryGetValue(i, out var cvx) && cvx is bool convex)
            {
                double r2 = radii.TryGetValue(i, out var rr) ? rr : ba.Radius;
                var ns = new ExtrudedSurface(new ArcCurve3(ba.Frame, r2, ba.A0, ba.A1), ex.Dir);
                f.Surface = ns;
                // сторона: нормаль поверхности в точке против желаемой наружной (выпуклая — от оси, вогнутая — к оси)
                var qp = ns.PointAt(new Vector2d(0.3, 0)); var w = cy.Axis.Normalized(); var nr = qp - cy.P; nr -= w * Vector3d.Dot(nr, w);
                f.Reversed = Vector3d.Dot(ns.NormalAt(ns.ProjectToUV(qp)), nr) * (convex ? 1 : -1) < 0;
            }
            else { why = "a touched face has no outward normal"; return null; }
            f.InteriorHint = null;
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { ce.PCurve = null; ce.PCurveDeviation = 0; }
        }
        clone.Exact = null; clone.QExact = null; clone.DisplayMeshKey = null;
        var wl = new List<string>();
        if (Analytic.PrismBuild.Finish(clone, wl) > 0) { why = "faces not meshed: " + string.Join(" / ", wl.Take(2)); return null; }
        clone.Guarantee = GuaranteeLevel.ExactAnalytic;
        var vr = SolidValidator.Validate(clone);
        if (!vr.IsValid) { why = "result invalid: " + vr.Summary() + " " + string.Join(" / ", vr.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)); return null; }
        if (AnalyticVolume.Of(clone) is not { } vol || vol <= 0) { why = "no analytic volume"; return null; }
        if (BooleanOp.VolumeVsFacets(clone) is { } vf && !vf.Agree) { why = $"volume {vf.Va:G8} vs facets {vf.Vf:G8}"; return null; }
        // KRN-01 (01.5): происхождение — та же грань (порядок клона), изменённая, если её форма или края сдвинулись
        if (!ProvOff)
        {
            var pv = new Provenance { Operation = "Plane replacement" };
            var srcFaces = body.Faces.ToList(); var resFaces = clone.Faces.ToList();
            for (int k = 0; k < resFaces.Count; k++) pv.Faces.Add(new FaceProvenance(resFaces[k], srcFaces[k], touched.Contains(k) ? FaceOrigin.Modified : FaceOrigin.Kept));
            clone.SetProvenance(pv);
        }
        return clone;
    }
}
