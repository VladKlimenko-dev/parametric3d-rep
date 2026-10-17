using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// u12b: ЗАОСТРЕНИЕ ИСЧЕЗАЮЩИХ СОПРЯЖЕНИЙ перед смещением тела. Скругление радиуса R, смещаемое на d в сторону своей оси
/// (выпуклое — внутрь, вогнутое — наружу) при |d| ≥ R вырождается: смещённая поверхность пропадает. Смещение такого угла
/// совпадает со смещением острого ребра (соседи, продлённые до встречи), поэтому полоса скругления убирается: её края
/// с соседями A и B заменяются одним ребром A ∩ B, торцы стягиваются в вершины (A ∩ B ∩ торец). Тело собирается тем же
/// путём, что и смещённое (FinishFaces), и уже его смещение — полость оболочки. Мутация P3D_OFFSETSHARPEN=0.
/// </summary>
public static partial class DirectRound
{
    static readonly bool SharpenOff = Environment.GetEnvironmentVariable("P3D_OFFSETSHARPEN") == "0";
    /// <summary>Разбор: сколько тел заострено перед смещением.</summary>
    public static int OffsetSharpened;

    internal static double? OffsetRadiusOf(TFace f, double d) => OffsetRadius(f, d);
    internal static TSolid? SharpenFaces(TSolid body, HashSet<TFace> vanish, double tol, ref string? why) => Sharpen(body, vanish, tol, out _, ref why);

    /// <summary>Радиус смещённой поверхности (цилиндр, круговое выдавливание, тор и шар); null — у грани нет радиуса.</summary>
    static double? OffsetRadius(TFace f, double d)
    {
        var src = GeometryAudit.AuthoritativeSurface(f);
        if (src == null) return null;
        var probe = f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P ?? f.InteriorHint;
        if (probe == null) return null;
        var no = src.NormalAt(src.ProjectToUV(probe.Value)).Normalized();
        if (FaceRev(f)) no = -no;
        switch (src)
        {
            case CylinderSurface cs:
            {
                var w = cs.Axis.W.Normalized(); var rad = probe.Value - cs.Axis.Origin; rad -= w * Vector3d.Dot(rad, w);
                return cs.Radius + (Vector3d.Dot(rad, no) > 0 ? d : -d);
            }
            case ExtrudedSurface { Base: ArcCurve3 ba } ex when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), ex.Dir.Normalized())) - 1) < 1e-12:
            {
                var w = ex.Dir.Normalized(); var rad = probe.Value - ba.Frame.Origin; rad -= w * Vector3d.Dot(rad, w);
                return ba.Radius + (Vector3d.Dot(rad, no) > 0 ? d : -d);
            }
            case RevolvedSurface { Base: ArcCurve3 ga } rv:
            {
                var axW = rv.Axis.W.Normalized(); var ao = rv.Axis.Origin;
                var cg0 = ga.Frame.Origin - ao; double hz = Vector3d.Dot(cg0, axW); double rho = (cg0 - axW * hz).Length;
                var onGen = rv.PointAt(rv.ProjectToUV(probe.Value));
                var pr = onGen - ao; pr -= axW * Vector3d.Dot(pr, axW);
                var cAt = ao + axW * hz + (rho > 1e-12 && pr.Length > 1e-12 ? pr.Normalized() * rho : Vector3d.Zero);
                return ga.Radius + (Vector3d.Dot(probe.Value - cAt, no) > 0 ? d : -d);
            }
        }
        return null;
    }

    /// <summary>Сторона грани относительно её точной поверхности (как RevOf у смещения).</summary>
    static bool FaceRev(TFace f)
    {
        var src = GeometryAudit.AuthoritativeSurface(f)!;
        bool rev = f.Reversed;
        if (!ReferenceEquals(src, f.Surface) && f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P is Vector3d sp
            && Vector3d.Dot(src.NormalAt(src.ProjectToUV(sp)) * (f.Reversed ? -1 : 1), f.NormalAt(sp)) < 0)
            rev = !rev;
        return rev;
    }

    /// <summary>Точка на всех поверхностях (Ньютон с наименьшим шагом; совпадающие нормали — одна строка). null — не сошлось.</summary>
    static Vector3d? OnAll(Vector3d y, IReadOnlyList<ISurface3> surfs, double tol)
    {
        for (int it = 0; it < 60; it++)
        {
            var rows = new List<(Vector3d N, double G)>();
            foreach (var sf in surfs)
            {
                var uv = sf.ProjectToUV(y); var q = sf.PointAt(uv); var n = sf.NormalAt(uv);
                if (!(n.LengthSquared > 1e-18)) return null;
                n.Normalize(); double g = Vector3d.Dot(y - q, n);
                if (rows.Any(rw => Math.Abs(Math.Abs(Vector3d.Dot(rw.N, n)) - 1) < 1e-10)) continue;
                rows.Add((n, g));
            }
            double worst = surfs.Max(sf => (sf.PointAt(sf.ProjectToUV(y)) - y).Length);
            if (worst <= 0.1 * tol) return y;
            Vector3d step;
            if (rows.Count == 1) step = -rows[0].N * rows[0].G;
            else if (rows.Count == 2)
            {
                var (a, ga) = rows[0]; var (b, gb) = rows[1];
                double c = Vector3d.Dot(a, b), det = 1 - c * c; if (det < 1e-12) return null;
                step = a * ((-ga + c * gb) / det) + b * ((-gb + c * ga) / det);
            }
            else
            {
                // три (и более) строки: первые три независимые
                var s3 = Solve3(new[] { rows[0].N, rows[1].N, rows[2].N }, new[] { -rows[0].G, -rows[1].G, -rows[2].G });
                if (s3 == null) return null;
                step = s3.Value;
            }
            y += step;
            if (!double.IsFinite(y.X + y.Y + y.Z)) return null;
        }
        return surfs.All(sf => (sf.PointAt(sf.ProjectToUV(y)) - y).Length <= tol) ? y : null;
    }

    /// <summary>
    /// Тело без граней vanish: края-касания полосы с соседями заменены ребром соседей, торцы стянуты. faceOf — грань нового
    /// тела → исходная. null — не вышло (причина в why).
    /// </summary>
    static TSolid? Sharpen(TSolid body, HashSet<TFace> vanish, double tol, out Dictionary<TFace, TFace> faceOf, ref string? why)
    {
        faceOf = new();
        ISurface3 S(TFace f) => GeometryAudit.AuthoritativeSurface(f)!;
        TFace? Other(TEdge e, TFace f) => e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().FirstOrDefault(x => !ReferenceEquals(x, f));
        // края-касания (рельсы) каждой исчезающей грани: ровно два, к двум разным оставшимся граням
        var rails = new Dictionary<TEdge, TFace>();          // ребро-рельс → его исчезающая грань
        var pairOf = new Dictionary<TFace, (TEdge Ra, TFace A, TEdge Rb, TFace B)>();
        foreach (var v in vanish)
        {
            var rs = new List<(TEdge E, TFace F)>();
            foreach (var e in v.Edges.Distinct())
            {
                var o = Other(e, v);
                if (o == null || vanish.Contains(o)) continue;
                if (EdgeClass.IsTangentSeam(e)) rs.Add((e, o));
            }
            if (rs.Count != 2 || ReferenceEquals(rs[0].F, rs[1].F)) { why = $"sharpen: a vanishing {S(v).GetType().Name} face has {rs.Count} tangent neighbour edge(s)"; return null; }
            pairOf[v] = (rs[0].E, rs[0].F, rs[1].E, rs[1].F);
            rails[rs[0].E] = v; rails[rs[1].E] = v;
        }
        // стягиваемые рёбра: все прочие рёбра исчезающих граней (торцы, стыки полос)
        var parent = new Dictionary<TVertex, TVertex>();
        TVertex Find(TVertex x) { while (parent.TryGetValue(x, out var p) && !ReferenceEquals(p, x)) x = p; return x; }
        void Union(TVertex a, TVertex b) { a = Find(a); b = Find(b); if (!ReferenceEquals(a, b)) parent[a] = b; }
        var collapsed = new HashSet<TEdge>();
        foreach (var v in vanish)
            foreach (var e in v.Edges.Distinct())
                if (!rails.ContainsKey(e)) { collapsed.Add(e); Union(e.V0, e.V1); }
        // группа вершин → новое положение: на всех оставшихся гранях, сходящихся в группе
        var groups = body.Vertices.GroupBy(Find).ToList();
        var pos = new Dictionary<TVertex, Vector3d>();
        foreach (var g in groups)
        {
            var vs = g.ToList();
            bool touched = vs.Count > 1 || body.Edges.Any(e => (e.V0 == vs[0] || e.V1 == vs[0]) && (rails.ContainsKey(e) || collapsed.Contains(e)));
            if (!touched) { pos[vs[0]] = vs[0].P; continue; }
            var fs = new List<ISurface3>();
            foreach (var x in vs)
                foreach (var e in body.Edges.Where(e => e.V0 == x || e.V1 == x))
                    foreach (var f in e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>())
                        if (!vanish.Contains(f) && !fs.Any(s => ReferenceEquals(s, S(f)))) fs.Add(S(f));
            var start = vs.Aggregate(Vector3d.Zero, (a, x) => a + x.P) / vs.Count;
            var p = OnAll(start, fs, tol);
            if (p == null) { why = $"sharpen: no corner point for {vs.Count} vertex(es) near {start}"; return null; }
            foreach (var x in vs) pos[x] = p.Value;
        }
        Vector3d P(TVertex x) => pos[Find(x)];
        // рёбра оставшихся граней с перенесёнными концами
        var ecur = new Dictionary<TEdge, ICurve3>();
        foreach (var e in body.Edges)
        {
            if (collapsed.Contains(e) || rails.ContainsKey(e)) continue;
            var a = P(e.V0); var b = P(e.V1);
            if ((a - e.V0.P).Length <= tol && (b - e.V1.P).Length <= tol) { ecur[e] = e.Curve; continue; }
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().Select(S).ToList();
            ICurve3? nc = null;
            switch (e.Curve)
            {
                case LineCurve3 l:
                {
                    bool fw = (l.A - e.V0.P).Length <= (l.A - e.V1.P).Length;
                    nc = fw ? new LineCurve3(a, b) : new LineCurve3(b, a);
                    break;
                }
                case ArcCurve3 arc:
                {
                    var c = arc.Frame.Origin; var u = arc.Frame.U; var vv = arc.Frame.V;
                    if (Math.Abs((a - c).Length - arc.Radius) > tol || Math.Abs((b - c).Length - arc.Radius) > tol) break;
                    bool fw = (arc.PointAt(arc.A0) - e.V0.P).Length <= (arc.PointAt(arc.A0) - e.V1.P).Length;
                    var pa = fw ? a : b; var pb = fw ? b : a;
                    double Ang(Vector3d q) { var d0 = q - c; return Math.Atan2(Vector3d.Dot(d0, vv), Vector3d.Dot(d0, u)); }
                    double a0 = Ang(pa), a1 = Ang(pb), span = arc.A1 - arc.A0;
                    while (a0 - arc.A0 > Math.PI) a0 -= 2 * Math.PI; while (arc.A0 - a0 > Math.PI) a0 += 2 * Math.PI;
                    while (span > 0 ? a1 < a0 : a1 > a0) a1 += span > 0 ? 2 * Math.PI : -2 * Math.PI;
                    nc = new ArcCurve3(arc.Frame, arc.Radius, a0, a1);
                    break;
                }
            }
            if (nc == null) { why = $"sharpen: a {e.Curve.GetType().Name} edge cannot follow a moved corner"; return null; }
            var mid = nc.PointAt(0.5 * (nc.T0 + nc.T1));
            if (fs.Any(s => (s.PointAt(s.ProjectToUV(mid)) - mid).Length > tol)) { why = "sharpen: an extended edge leaves its faces"; return null; }
            ecur[e] = nc;
        }
        // новые рёбра A ∩ B — по одному на исчезающую грань
        var newEdge = new Dictionary<TFace, ICurve3>();
        foreach (var (v, (ra, fa, rb, fb)) in pairOf)
        {
            var g0 = Find(ra.V0); var g1 = Find(ra.V1);
            var h = new[] { Find(rb.V0), Find(rb.V1) };
            if (!(h.Contains(g0) && h.Contains(g1))) { why = "sharpen: the two tangent edges of a vanishing face do not end at the same corners"; return null; }
            var pa = pos[g0]; var pb = pos[g1];
            var sa = S(fa); var sb = S(fb);
            var rc = ra.Curve;
            bool fwr = (rc.PointAt(rc.T0) - ra.V0.P).Length <= (rc.PointAt(rc.T0) - ra.V1.P).Length;
            var probe = new List<Vector3d>();
            foreach (double k in new[] { 0.25, 0.5, 0.75 })
            {
                var q = OnAll(rc.PointAt(rc.T0 + (rc.T1 - rc.T0) * (fwr ? k : 1 - k)), new[] { sa, sb }, tol);
                if (q == null) { why = "sharpen: the neighbours of a vanishing face do not meet"; return null; }
                probe.Add(q.Value);
            }
            ICurve3? nc = null;
            if (sa.IsPlanar && sb.IsPlanar) nc = new LineCurve3(pa, pb);
            else if (Circle3(probe[0], probe[1], probe[2]) is { } c && (pa - pb).Length > tol)
            {
                double R = (probe[0] - c).Length;
                var w = Vector3d.Cross(probe[0] - c, probe[2] - c);
                if (w.LengthSquared > 1e-18 && Math.Abs((pa - c).Length - R) <= 10 * tol && Math.Abs((pb - c).Length - R) <= 10 * tol)
                {
                    w.Normalize();
                    var u = (pa - c).Normalized(); var vv = Vector3d.Cross(w, u);
                    double Ang(Vector3d q) { var d0 = q - c; return Math.Atan2(Vector3d.Dot(d0, vv), Vector3d.Dot(d0, u)); }
                    // направление обхода — через пробные точки (между pa и pb)
                    double am = Ang(probe[1]); if (am < 0) am += 2 * Math.PI;
                    double ab = Ang(pb); if (ab < 0) ab += 2 * Math.PI;
                    double a1 = am < ab ? ab : ab - 2 * Math.PI;
                    var arc = new ArcCurve3(new Frame3(c, u, vv), R, 0, a1);
                    if (probe.All(q => Math.Abs((q - c).Length - R) <= 10 * tol && Math.Abs(Vector3d.Dot(q - c, w)) <= 10 * tol)) nc = arc;
                }
            }
            if (nc == null)
            {
                var sa2 = sa; var sb2 = sb; var rc2 = rc; bool fw2 = fwr; double tl = tol; var pa2 = pa; var pb2 = pb;
                nc = new ProcCurve3(t => t <= 0 ? pa2 : t >= 1 ? pb2 : OnAll(rc2.PointAt(rc2.T0 + (rc2.T1 - rc2.T0) * (fw2 ? t : 1 - t)), new[] { sa2, sb2 }, tl) ?? new Vector3d(double.NaN), 0, 1, false, "sharp edge");
            }
            // проверка: точки нового ребра — на обеих поверхностях
            for (int k = 1; k < 6; k++)
            {
                var q = nc.PointAt(nc.T0 + (nc.T1 - nc.T0) * k / 6.0);
                if ((sa.PointAt(sa.ProjectToUV(q)) - q).Length > 10 * tol || (sb.PointAt(sb.ProjectToUV(q)) - q).Length > 10 * tol)
                { why = "sharpen: the sharp edge leaves a neighbour face"; return null; }
            }
            newEdge[v] = nc;
        }
        // описания граней
        var faces = new List<FDesc>();
        foreach (var f in body.Faces)
        {
            if (vanish.Contains(f)) continue;
            var src = S(f); bool rev = FaceRev(f);
            var fd = new FDesc { Surf = src, Rev = rev, Tag = f.Tag, Hint = f.InteriorHint, Slot = f.MaterialSlot, Orig = f, Touched = true };
            foreach (var l in f.Loops)
            {
                if (l.CoEdges.Count == 0) { fd.Loops.Add(new List<Seg>()); continue; }
                var ord = l.Ordered();
                if (ord == null) { why = "sharpen: loop not closed"; return null; }
                var segs = new List<Seg>();
                foreach (var ce in ord)
                {
                    var a = P(ce.Start); var b = P(ce.End);
                    if (collapsed.Contains(ce.Edge)) continue;
                    if (rails.TryGetValue(ce.Edge, out var v)) { segs.Add(new Seg { Curve = newEdge[v], A = a, B = b }); continue; }
                    var cv = ecur[ce.Edge];
                    segs.Add(new Seg { Curve = cv, A = a, B = b, Id = ce.Edge, Orig = ReferenceEquals(cv, ce.Edge.Curve) ? ce.Edge : null });
                }
                if (segs.Count == 0) { why = "sharpen: a loop vanished"; return null; }
                fd.Loops.Add(segs);
            }
            if (src.IsPlanar) fd.N = (src.NormalAt(src.ProjectToUV(fd.Loops[0][0].A)) * (rev ? -1 : 1)).Normalized();
            faces.Add(fd);
        }
        var solid = FinishFaces(faces, "sharpen", ref why, measureFirst: true, inherit: body.Guarantee);
        if (solid == null) return null;
        foreach (var fd in faces) if (fd.Made != null && fd.Orig != null) faceOf[fd.Made] = fd.Orig;
        Interlocked.Increment(ref OffsetSharpened);
        return solid;
    }
}
