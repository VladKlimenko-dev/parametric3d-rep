using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_OFFSETSOLID=0: смещение тела с кривыми гранями (шар, тор, сопряжения) не строится (прежний отказ).</summary>
    internal static readonly bool OffsetSolidOff = Environment.GetEnvironmentVariable("P3D_OFFSETSOLID") == "0";
    public static long OffsetSolids;

    /// <summary>
    /// KRN-07 (07.5): СМЕЩЕНИЕ ТЕЛА БЕЗ СМЕНЫ ТОПОЛОГИИ — каждая грань сдвигается вдоль наружной нормали на d(грань):
    /// плоскость — параллельно, цилиндр — радиус R ± d (та же ось), шар и тор — радиус образующей окружности ± d (та же ось
    /// вращения и центр образующей). Вершина — решение n_i·(P′ − V) = d_i по различным нормалям её граней (у гладкой
    /// вершины — одна нормаль, P′ = V + d·n); ребро: отрезок — между новыми вершинами, дуга — окружность через три смещённые
    /// точки (параметризация сохраняется). Каждая новая точка ПРОВЕРЯЕТСЯ: лежит на всех смещённых поверхностях своих граней
    /// (до 1e-9 отн.) — иначе отказ (смещение не той формы, ребро исчезло бы и т. п.). Тело собирается заново, проверка
    /// годности, объём — аналитический и по фасетам. null — отказ (причина в why).
    /// </summary>
    internal static TSolid? OffsetSolid(TSolid body, Func<TFace, double> offset, string? tag, ref string? why)
    {
        if (OffsetSolidOff) { why = "offset of curved faces is off"; return null; }
        var newSurf = new Dictionary<TFace, (ISurface3 S, bool Rev, double D)>();
        double scale = 1;
        foreach (var v in body.Vertices) scale = Math.Max(scale, v.P.Length);
        double tol = 1e-9 * scale;

        Vector3d Out(TFace f, Vector3d x)
        {
            var su = GeometryAudit.AuthoritativeSurface(f)!;
            var n = su.NormalAt(su.ProjectToUV(x)).Normalized();
            return RevOf(f) ? -n : n;
        }
        bool RevOf(TFace f)
        {
            var src = GeometryAudit.AuthoritativeSurface(f)!;
            bool rev = f.Reversed;
            if (!ReferenceEquals(src, f.Surface) && f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P is Vector3d sp
                && Vector3d.Dot(src.NormalAt(src.ProjectToUV(sp)) * (f.Reversed ? -1 : 1), f.NormalAt(sp)) < 0)
                rev = !rev;
            return rev;
        }
        bool OnSurf(ISurface3 s, Vector3d x) => (s.PointAt(s.ProjectToUV(x)) - x).Length <= tol;

        // поверхности
        foreach (var f in body.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src == null) { why = "face without an exact surface"; return null; }
            double d = offset(f);
            var probe = f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P ?? f.InteriorHint ?? src.PointAt(new Vector2d(0.3, 0.3));
            var no = Out(f, probe);
            ISurface3? ns = null;
            switch (src)
            {
                case { IsPlanar: true }:
                    ns = new PlaneSurface(Frame3.FromNormal(probe + no * d, no)); break;
                case CylinderSurface cs:
                {
                    var w = cs.Axis.W.Normalized(); var rad = probe - cs.Axis.Origin; rad -= w * Vector3d.Dot(rad, w);
                    double R = cs.Radius + (Vector3d.Dot(rad, no) > 0 ? d : -d);
                    if (!(R > tol)) { why = "offset: a cylinder radius vanishes"; return null; }
                    ns = new CylinderSurface(cs.Axis, R); break;
                }
                case ExtrudedSurface { Base: ArcCurve3 ba } ex when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), ex.Dir.Normalized())) - 1) < 1e-12:
                {
                    var w = ex.Dir.Normalized(); var rad = probe - ba.Frame.Origin; rad -= w * Vector3d.Dot(rad, w);
                    double R = ba.Radius + (Vector3d.Dot(rad, no) > 0 ? d : -d);
                    if (!(R > tol)) { why = "offset: a cylinder radius vanishes"; return null; }
                    ns = new ExtrudedSurface(new ArcCurve3(ba.Frame, R, ba.A0, ba.A1), ex.Dir); break;
                }
                case RevolvedSurface { Base: LineCurve3 gl } rvl:
                {
                    // конус (цилиндр, плоское кольцо) вращения: образующая сдвигается вдоль наружной нормали (в её меридиане)
                    var sg = rvl.NormalAt(rvl.ProjectToUV(gl.PointAt(0.5))).Normalized() * (RevOf(f) ? -1 : 1);
                    var sh = sg * d;
                    ns = new RevolvedSurface(new LineCurve3(gl.A + sh, gl.B + sh), rvl.Axis, rvl.A0, rvl.A1); break;
                }
                case RevolvedSurface { Base: ArcCurve3 ga } rv:
                {
                    // шар (центр образующей на оси) или тор: точка образующей сдвигается вдоль нормали — радиус ± d
                    var cg = ga.Frame.Origin;
                    // центр образующей «в положении» точки probe: поворот вокруг оси на угол probe
                    var su = rv; var uv = su.ProjectToUV(probe);
                    var onGen = su.PointAt(uv);
                    var axW = rv.Axis.W.Normalized(); var ao = rv.Axis.Origin;
                    var cg0 = cg - ao; double hz = Vector3d.Dot(cg0, axW); var cgr = cg0 - axW * hz; double rho = cgr.Length;
                    var pr = onGen - ao; pr -= axW * Vector3d.Dot(pr, axW);
                    var cAt = ao + axW * hz + (rho > 1e-12 && pr.Length > 1e-12 ? pr.Normalized() * rho : Vector3d.Zero);
                    double R = ga.Radius + (Vector3d.Dot(probe - cAt, no) > 0 ? d : -d);
                    if (!(R > tol)) { why = "offset: a sphere or torus radius vanishes"; return null; }
                    ns = new RevolvedSurface(new ArcCurve3(ga.Frame, R, ga.A0, ga.A1), rv.Axis, rv.A0, rv.A1); break;
                }
            }
            if (ns == null) { why = $"offset: a {src.GetType().Name} face is not supported"; return null; }
            var p2 = probe + no * d;
            if (!OnSurf(ns, p2)) { why = $"offset: the {src.GetType().Name} offset does not carry the moved point"; return null; }
            bool rev2 = Vector3d.Dot(ns.NormalAt(ns.ProjectToUV(p2)), no) < 0;
            newSurf[f] = (ns, rev2, d);
        }

        // вершины
        var vmap = new Dictionary<TVertex, Vector3d>();
        List<TFace> FacesAt(TVertex v) => v.Edges.SelectMany(e => e.CoEdges.Select(c => c.Loop?.Face)).OfType<TFace>().Distinct().ToList();
        Vector3d? Move(Vector3d x, IReadOnlyList<TFace> fs)
        {
            var ns = new List<(Vector3d N, double D)>();
            foreach (var f in fs)
            {
                var n = Out(f, x); double d = newSurf[f].D;
                int j = ns.FindIndex(q => (q.N - n).Length < 1e-9);
                if (j >= 0) { if (Math.Abs(ns[j].D - d) > tol) return null; continue; }
                ns.Add((n, d));
            }
            Vector3d? r = ns.Count switch
            {
                1 => x + ns[0].N * ns[0].D,
                2 => Two(ns[0], ns[1]),
                _ => Three(ns),
            };
            if (r == null) return null;
            foreach (var f in fs) if (!OnSurf(newSurf[f].S, r.Value)) return null;
            return r;
            Vector3d? Two((Vector3d N, double D) a, (Vector3d N, double D) b)
            {
                double c = Vector3d.Dot(a.N, b.N), det = 1 - c * c;
                if (det < 1e-12) return null;
                double ka = (a.D - c * b.D) / det, kb = (b.D - c * a.D) / det;
                return x + a.N * ka + b.N * kb;
            }
            Vector3d? Three(List<(Vector3d N, double D)> q)
            {
                for (int i0 = 0; i0 < q.Count; i0++)
                    for (int i1 = i0 + 1; i1 < q.Count; i1++)
                        for (int i2 = i1 + 1; i2 < q.Count; i2++)
                            if (Solve3(new[] { q[i0].N, q[i1].N, q[i2].N }, new[] { q[i0].D + Vector3d.Dot(q[i0].N, x), q[i1].D + Vector3d.Dot(q[i1].N, x), q[i2].D + Vector3d.Dot(q[i2].N, x) }) is { } s)
                                return s;
                return null;
            }
        }
        foreach (var v in body.Vertices)
        {
            var p = Move(v.P, FacesAt(v));
            if (p == null) { why = "offset: a vertex has no consistent position on the offset faces"; return null; }
            vmap[v] = p.Value;
        }

        // рёбра
        var emap = new Dictionary<TEdge, ICurve3>();
        foreach (var e in body.Edges)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count != 2 && !(fs.Count == 1 && e.CoEdges.Count == 2)) { why = "offset: an edge is not between two faces"; return null; }   // шов — одна грань
            ICurve3? nc = null;
            switch (e.Curve)
            {
                case LineCurve3 l:
                {
                    bool fw = (l.A - e.V0.P).Length <= (l.A - e.V1.P).Length;
                    var a = vmap[fw ? e.V0 : e.V1]; var b = vmap[fw ? e.V1 : e.V0];
                    nc = new LineCurve3(a, b);
                    break;
                }
                case ArcCurve3 arc:
                {
                    var pts = new[] { 0.0, 1.0 / 3, 2.0 / 3 }.Select(t => Move(arc.PointAt(t), fs)).ToList();
                    if (pts.Any(p => p == null)) { why = "offset: an arc point has no position on the offset faces"; return null; }
                    var w = arc.Frame.W.Normalized();
                    // центр — на оси дуги (окружности соосны): проекция и равные расстояния
                    var c = Circle3(pts[0]!.Value, pts[1]!.Value, pts[2]!.Value);
                    if (c == null) { why = "offset: arc points are collinear"; return null; }
                    double R = (pts[0]!.Value - c.Value).Length;
                    var u = arc.Frame.U; var vv = arc.Frame.V;
                    nc = new ArcCurve3(new Frame3(c.Value, u, vv), R, arc.A0, arc.A1);
                    break;
                }
            }
            if (nc == null) { why = $"offset: a {e.Curve.GetType().Name} edge is not supported"; return null; }
            // проверка: точки новой кривой — смещённые точки старой (та же параметризация) и лежат на обеих смещённых гранях
            for (int k = 0; k <= 6; k++)
            {
                double t = e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 6.0;
                var q = nc.PointAt(nc.T0 + (nc.T1 - nc.T0) * k / 6.0);
                var want = k == 0 || k == 6 ? (Vector3d?)null : Move(e.Curve.PointAt(t), fs);
                // отрезок: смещённые точки — на прямой (концы могут сдвинуться вдоль неё); дуга — та же параметризация
                double off = want == null ? 0 : nc is LineCurve3 nl ? DistToLine(nl, want.Value) : (want.Value - q).Length;
                if (want != null && off > tol) { why = $"offset: the new edge curve does not follow the offset points ({e.Curve.GetType().Name}, k {k}, off {off:G3}, faces {string.Join("/", fs.Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name))})"; return null; }
                foreach (var f in fs) if (!OnSurf(newSurf[f].S, q)) { why = "offset: the new edge curve leaves an offset face"; return null; }
            }
            emap[e] = nc;
        }
        // концы новых рёбер — новые вершины
        foreach (var e in body.Edges)
        {
            var nc = emap[e]; var a = nc.PointAt(nc.T0); var b = nc.PointAt(nc.T1);
            var va = vmap[e.V0]; var vb = vmap[e.V1];
            if (!(((a - va).Length <= tol && (b - vb).Length <= tol) || ((a - vb).Length <= tol && (b - va).Length <= tol)))
            { why = "offset: an edge does not end at its offset vertices"; return null; }
            if (e.V0 != e.V1 && (va - vb).Length < 1e-6) { why = "offset: an edge would vanish"; return null; }
        }

        var res = Rebuild(body, newSurf.ToDictionary(kv => kv.Key, kv => (kv.Value.S, kv.Value.Rev)), vmap, emap, tag,
                          (f, h) => h + Out(f, h) * newSurf[f].D, "offset", ref why);
        if (res != null) Interlocked.Increment(ref OffsetSolids);
        return res;
    }

    /// <summary>Сборка тела той же топологии с новыми поверхностями граней, новыми точками вершин и кривыми рёбер;
    /// проверка годности, аналитический объём, объём по фасетам. null — отказ (причина в why).</summary>
    internal static TSolid? Rebuild(TSolid body, Dictionary<TFace, (ISurface3 S, bool Rev)> newSurf, Dictionary<TVertex, Vector3d> vmap,
                                    Dictionary<TEdge, ICurve3> emap, string? tag, Func<TFace, Vector3d, Vector3d> hintMap, string what, ref string? why)
    {
        var faces = new List<FDesc>();
        foreach (var f in body.Faces)
        {
            var (ns, rev) = newSurf[f];
            Vector3d? hint = f.InteriorHint is { } h ? hintMap(f, h) : null;
            if (hint is { } hh) hint = ns.PointAt(ns.ProjectToUV(hh));
            var fd = new FDesc { Surf = ns, Rev = rev, Tag = tag ?? f.Tag, Hint = hint, Slot = f.MaterialSlot, Orig = f, Touched = true };
            if (ns.IsPlanar) fd.N = (ns.NormalAt(ns.ProjectToUV(vmap[f.Loops.SelectMany(l => l.CoEdges).First().Start])) * (rev ? -1 : 1)).Normalized();
            foreach (var l in f.Loops)
            {
                if (l.CoEdges.Count == 0) { fd.Loops.Add(new List<Seg>()); continue; }   // замкнутая поверхность без края (целый шар)
                var ord = l.Ordered();
                if (ord == null) { why = "loop not closed"; return null; }
                fd.Loops.Add(ord.Select(ce => new Seg { Curve = emap[ce.Edge], A = vmap[ce.Start], B = vmap[ce.End], Id = ce.Edge }).ToList());
            }
            faces.Add(fd);
        }
        var solid = FinishFaces(faces, what, ref why);
        if (solid == null) return null;
        if (Prov)
        {
            // KRN-08 (08.2): происхождение — грань той же поверхности и с теми же точками контура сохранена, прочие изменены
            var pv = new Provenance { Operation = what + ".rebuild" };
            foreach (var fd in faces)
            {
                if (fd.Made == null || fd.Orig == null) continue;
                var src = GeometryAudit.AuthoritativeSurface(fd.Orig);
                var oldPts = fd.Orig.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                var newPts = fd.Made.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                bool same = ReferenceEquals(src, fd.Surf) && oldPts.Count == newPts.Count && newPts.All(p => oldPts.Any(q => (p - q).Length < 1e-12));
                pv.Faces.Add(new FaceProvenance(fd.Made, fd.Orig, same ? FaceOrigin.Kept : FaceOrigin.Modified, null));
            }
            solid.SetProvenance(pv);
        }
        return solid;
    }

    /// <summary>Сборка тела из описаний граней: точки граней, сборка, соглашение, разметка, проверки (годность,
    /// аналитический объём, объём по фасетам), допуски сущностей. null — отказ.</summary>
    static TSolid? FinishFaces(List<FDesc> faces, string what, ref string? why)
    {
        foreach (var fd in faces.Where(x => x.Surf.IsPlanar)) fd.Hint = InteriorPoint(fd) ?? fd.Hint;

        var solid = Assemble(faces, ref why);
        if (solid == null) return null;
        TopologyConventions.Normalize(solid);
        var w2 = new List<string>();
        if (Analytic.PrismBuild.Finish(solid, w2, BooleanOp.DonorFrom(faces.Where(d => d.Orig != null).Select(d => d.Orig!))) > 0) { why = what + ": faces not meshed: " + string.Join(" / ", w2.Take(3)); return null; }
        solid.Guarantee = GuaranteeLevel.ExactAnalytic;
        var vr = SolidValidator.Validate(solid);
        if (!vr.IsValid) { why = what + ": result invalid: " + vr.Summary() + " " + string.Join(" / ", vr.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)); return null; }
        if (AnalyticVolume.Of(solid) == null) { why = what + ": no analytic volume"; return null; }
        if (BooleanOp.VolumeVsFacets(solid) is { } vf && !vf.Agree) { why = $"{what}: volume {vf.Va:G8} vs facets {vf.Vf:G8}"; return null; }
        Analytic.EntityTolerance.Assign(solid);
        return solid;
    }

    static double DistToLine(LineCurve3 l, Vector3d x)
    {
        var d = (l.B - l.A).Normalized(); var r = x - l.A; return (r - d * Vector3d.Dot(r, d)).Length;
    }

    /// <summary>Центр окружности через три точки (null — точки на одной прямой).</summary>
    static Vector3d? Circle3(Vector3d a, Vector3d b, Vector3d c)
    {
        var ab = b - a; var ac = c - a; var n = Vector3d.Cross(ab, ac);
        double n2 = n.LengthSquared; if (n2 < 1e-24) return null;
        return a + (Vector3d.Cross(n, ab) * ac.LengthSquared + Vector3d.Cross(ac, n) * ab.LengthSquared) / (2 * n2);
    }
}
