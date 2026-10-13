using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>
    /// m.f3: СКРУГЛЕНИЕ И ФАСКА ВЕРШИНЫ ПРЯМЫМ ПОСТРОЕНИЕМ. Прежде — резцы и булева (VertexOp): шар-резец раздувался на
    /// 2e-5·R ради трансверсального касания рёбер, и рёбра кончались в ~0.04 мм от точки касания (форма — не та, что задана).
    /// Теперь угол из N прямых рёбер и N плоских граней (выпуклый или вогнутый) перестраивается сам: рёбра укорачиваются до
    /// точек Qᵢ (у скругления — где сфера касается или пересекает ребро, у фаски — на расстояниях dᵢ), у каждой грани угол
    /// заменяется звеном Qₐ → Q_b (у скругления — дуга окружности «сфера ∩ плоскость грани» со стороны угла, у фаски —
    /// отрезок), новая грань — часть сферы радиуса R (центр — на оси угла, VertexOp.RoundGeometry) или плоскость через Qᵢ.
    /// Сфера ровно касается рёбер: точки касания — вершины тела, рёбра доходят до них. Проверки — те же, что у прямого
    /// построителя рёбер (годность, аналитический объём, объём по фасетам). Не подошло (точки фаски не в одной плоскости,
    /// звено короче допуска) — прежний путь резцов. Мутация P3D_DRVERTEX=0.
    /// </summary>
    static readonly bool VertexOff = Environment.GetEnvironmentVariable("P3D_DRVERTEX") == "0";
    public static long VertexBuilt, VertexDeclined;

    public static bool TryVertex(List<TSolid> bodies, Vector3d point, double? radius, double[]? dists, List<string> warnings)
    {
        if (Off || OffForTest || VertexOff || bodies.Count == 0) return false;
        int k = bodies.FindIndex(b => b.Edges.Any(e => (e.V0.P - point).Length < 1e-3 || (e.V1.P - point).Length < 1e-3));
        if (k < 0) return false;
        string? why = null;
        TSolid? res;
        try { res = BuildVertex(bodies[k], point, radius, dists, ref why); }
        catch (RebuildException) { throw; }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { res = null; why = "exception: " + ex.Message; }
        if (res == null) { Interlocked.Increment(ref VertexDeclined); Debug?.Invoke("vertex declined: " + why); return false; }
        bodies[k] = res; Interlocked.Increment(ref VertexBuilt);
        warnings.Add(radius != null ? $"Vertex round: direct build (exact sphere, R {radius:0.###})." : "Vertex chamfer: direct build (exact plane).");
        return true;
    }

    static TSolid? BuildVertex(TSolid body, Vector3d point, double? radius, double[]? dists, ref string? why)
    {
        var c = VertexOp.Find(body, point);                       // угол: рёбра, грани; отказы входа — RebuildException
        int n = c.Dirs.Length;
        if (body.Edges.Any(e => e.Curve is not LineCurve3 && (e.V0.P - c.P).Length < 1e-7 && (e.V1.P - c.P).Length < 1e-7)) { why = "micro-edges at the vertex"; return null; }
        // точки на рёбрах
        double[] cut; Vector3d centre = default;
        if (radius is { } r)
        {
            (centre, cut) = VertexOp.RoundGeometry(c, r);
        }
        else
        {
            cut = dists!.Length == 1 ? Enumerable.Repeat(dists[0], n).ToArray() : dists;
            if (cut.Length != n) { why = "distances"; return null; }
        }
        for (int i = 0; i < n; i++) if (!(cut[i] > 1e-6) || cut[i] >= c.Lengths[i] - 1e-6) { why = "a point does not fit on its edge"; return null; }
        var q = Enumerable.Range(0, n).Select(i => c.P + c.Dirs[i] * cut[i]).ToArray();

        // описание граней тела
        var faces = new List<FDesc>();
        foreach (var f in body.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src == null) { why = "face without an exact surface"; return null; }
            bool rev = f.Reversed;
            if (!ReferenceEquals(src, f.Surface) && f.Loops.FirstOrDefault()?.CoEdges.FirstOrDefault()?.Start.P is Vector3d sp
                && Vector3d.Dot(src.NormalAt(src.ProjectToUV(sp)) * (f.Reversed ? -1 : 1), f.NormalAt(sp)) < 0) rev = !rev;
            var d = new FDesc { Surf = src, Rev = rev, Tag = f.Tag, Hint = f.InteriorHint, Slot = f.MaterialSlot, Orig = f };
            foreach (var l in f.Loops)
            {
                var ord = l.Ordered();
                if (ord == null) { why = "loop not closed"; return null; }
                d.Loops.Add(ord.Select(ce => new Seg { Curve = ce.Edge.Curve, A = ce.Start.P, B = ce.End.P, Orig = ce.Edge, Id = ce.Edge }).ToList());
            }
            faces.Add(d);
        }
        bool AtP(Vector3d x) => (x - c.P).Length < 1e-7;
        int EdgeOf(Seg s)
        {
            // какое ребро угла: дальний конец звена — Far[i]
            var far = AtP(s.A) ? s.B : s.A;
            for (int i = 0; i < n; i++) if ((far - c.Far[i]).Length < 1e-7) return i;
            return -1;
        }
        // звено нового контура грани на углу: от точки ребра a к точке ребра b
        var newSegs = new List<(int A, int B, ICurve3 Curve)>();
        foreach (var fd in faces)
            foreach (var loop in fd.Loops)
                for (int j = 0; j < loop.Count; j++)
                {
                    var s = loop[j]; if (!AtP(s.B)) continue;
                    int jn = (j + 1) % loop.Count; var t = loop[jn];
                    if (!AtP(t.A)) { why = "a loop passes the vertex without leaving it"; return null; }
                    int ia = EdgeOf(s), ib = EdgeOf(t);
                    if (ia < 0 || ib < 0 || ia == ib) { why = "corner edges not matched"; return null; }
                    if (!fd.Surf.IsPlanar) { why = "a face at the corner is not planar"; return null; }
                    // звено: дуга окружности сфера ∩ плоскость грани со стороны угла или отрезок
                    ICurve3 link;
                    if (radius is { } rr)
                    {
                        var pl = fd.Surf.NormalAt(fd.Surf.ProjectToUV(c.P)).Normalized();
                        var cj = centre - pl * Vector3d.Dot(centre - c.P, pl);
                        double rj2 = rr * rr - (centre - cj).LengthSquared; if (rj2 <= 1e-12) { why = "the sphere misses a face"; return null; }
                        double rj = Math.Sqrt(rj2);
                        var u = (q[ia] - cj).Normalized(); var v = Vector3d.Cross(pl, u).Normalized();
                        double Ang(Vector3d x) { var dd = x - cj; return Math.Atan2(Vector3d.Dot(dd, v), Vector3d.Dot(dd, u)); }
                        double ab = Ang(q[ib]); if (ab <= 0) ab += 2 * Math.PI;        // против часовой (по pl) от Qa до Qb
                        var mid = cj + (c.P - cj).Normalized() * rj;                   // ближняя к углу точка окружности
                        double am = Ang(mid); if (am < 0) am += 2 * Math.PI;
                        double a0 = 0, a1 = ab;
                        if (!(am > 1e-9 && am < ab - 1e-9)) a1 = ab - 2 * Math.PI;      // угол — по другую сторону: обход по часовой
                        link = new ArcCurve3(new Frame3(cj, u, v), rj, a0, a1);
                        if ((link.PointAt(1) - q[ib]).Length > 1e-7 * Math.Max(1, rr)) { why = "arc end off its edge point"; return null; }
                    }
                    else link = new LineCurve3(q[ia], q[ib]);
                    // укоротить звенья ребёр: конец s — Qa, начало t — Qb; между ними — новое звено
                    loop[j] = new Seg { Curve = new LineCurve3(s.A, q[ia]), A = s.A, B = q[ia], Id = s.Id };
                    loop[jn] = new Seg { Curve = new LineCurve3(q[ib], t.B), A = q[ib], B = t.B, Id = t.Id };
                    loop.Insert(j + 1, new Seg { Curve = link, A = q[ia], B = q[ib] });
                    newSegs.Add((ia, ib, link));
                    fd.Touched = true;
                    break;
                }
        if (newSegs.Count != n) { why = $"{newSegs.Count} faces at the corner of {n} edges"; return null; }
        // рёбра угла, не лежащие ни в одной грани угла по сторонам, укорачиваются в своих гранях (уже сделано выше); проверка
        // новая грань: контур из звеньев цепочкой (сторону выставит нормализация)
        var loopN = new List<Seg>(); var left = newSegs.ToList();
        var cur = left[0]; left.RemoveAt(0); loopN.Add(new Seg { Curve = cur.Curve, A = q[cur.A], B = q[cur.B] }); int at = cur.B;
        while (left.Count > 0)
        {
            int m = left.FindIndex(x => x.A == at || x.B == at); if (m < 0) { why = "corner loop not closed"; return null; }
            var x = left[m]; left.RemoveAt(m);
            bool fw = x.A == at;
            loopN.Add(new Seg { Curve = x.Curve, A = fw ? q[x.A] : q[x.B], B = fw ? q[x.B] : q[x.A] }); at = fw ? x.B : x.A;
        }
        if (at != newSegs[0].A) { why = "corner loop does not return"; return null; }
        var dsum = c.FaceNormals!.Aggregate(Vector3d.Zero, (s, x) => s + x);
        ISurface3 surf; bool srev; Vector3d hint;
        if (radius is { } R)
        {
            // сфера: ось перпендикулярна направлению на середину угла (полюсов в грани нет), половина окружности вращается
            var dir = (c.P - centre).Normalized();
            var helper = Math.Abs(dir.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
            var W = Vector3d.Cross(dir, helper).Normalized(); var U = dir; var Vv = Vector3d.Cross(W, U);
            surf = new RevolvedSurface(new ArcCurve3(new Frame3(centre, U, W), R, -Math.PI / 2, Math.PI / 2), new Frame3(centre, U, Vv), 0, 2 * Math.PI);
            // наружу у выпуклого угла — от центра; у вогнутого — к центру
            var nS = surf.NormalAt(surf.ProjectToUV(centre + dir * R));
            srev = Vector3d.Dot(nS, dir) < 0 ^ c.Concave;
            hint = centre + dir * R;
        }
        else
        {
            var a0 = loopN[0].A; var nt = Vector3d.Cross(loopN[0].B - a0, loopN[1].B - a0);
            if (nt.Length < 1e-12) { why = "degenerate chamfer face"; return null; }
            nt.Normalize();
            if (q.Any(x => Math.Abs(Vector3d.Dot(x - a0, nt)) > 1e-9 * Math.Max(1, cut.Max()))) { why = "chamfer points not in one plane"; return null; }
            if (Vector3d.Dot(nt, dsum) < 0) nt = -nt;           // наружу — по сумме наружных нормалей граней угла (и у вогнутого)
            surf = new PlaneSurface(Frame3.FromNormal(a0, nt)); srev = false;
            hint = q.Aggregate(Vector3d.Zero, (s, x) => s + x) / n;
        }
        var nf = new FDesc { Surf = surf, Rev = srev, Tag = radius != null ? "round:vertex" : "chamfer:vertex", Touched = true, Hint = hint };
        nf.Loops.Add(loopN);
        foreach (var fd in faces.Where(x => x.Touched)) fd.Hint = InteriorPoint(fd);
        var all = faces.Append(nf).ToList();
        var solid = Assemble(all, ref why);
        if (solid == null) return null;
        TopologyConventions.Normalize(solid);
        var w = new List<string>();
        if (Analytic.PrismBuild.Finish(solid, w) > 0) { why = "faces not meshed: " + string.Join(" / ", w.Take(2)); return null; }
        solid.Guarantee = GuaranteeLevel.ExactAnalytic;
        var val = SolidValidator.Validate(solid);
        if (!val.IsValid) { why = "result invalid: " + val.Summary(); return null; }
        if (AnalyticVolume.Of(solid) == null) { why = "no analytic volume"; return null; }
        if (BooleanOp.VolumeVsFacets(solid) is { } vf && !vf.Agree) { why = $"volume {vf.Va:G8} vs facets {vf.Vf:G8}"; return null; }
        Analytic.EntityTolerance.Assign(solid);
        if (Prov)
        {
            var pv = new Provenance { Operation = radius != null ? "vertex-round.direct" : "vertex-chamfer.direct" };
            foreach (var fd in all)
                if (fd.Made != null && solid.Faces.Contains(fd.Made))
                    pv.Faces.Add(new FaceProvenance(fd.Made, fd.Orig, fd.Orig == null ? FaceOrigin.Generated : fd.Touched ? FaceOrigin.Modified : FaceOrigin.Kept, fd.Orig == null ? fd.Tag : null));
            solid.SetProvenance(pv);
        }
        if (Lazy)
            solid.LazyDerivedExact = (new Lazy<Exact.ExactBody>(() => Exact.ExactAdapter.ToExact(TopologyClone.Deep(solid), new Exact.PlaneRegistry(), null),
                LazyThreadSafetyMode.ExecutionAndPublication), solid.Revision);
        return solid;
    }
}
