using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_DRAFTTANGENT=0: уклон стенок со скруглёнными рёбрами (касательные цилиндры) не строится.</summary>
    internal static readonly bool DraftTangentOff = Environment.GetEnvironmentVariable("P3D_DRAFTTANGENT") == "0";
    public static long DraftTangents;

    /// <summary>
    /// KRN-07 (07.6): УКЛОН СТЕНОК СО СКРУГЛЕНИЯМИ. Выбранные стенки (плоскости и цилиндры вдоль направления вытяжки m) и всё,
    /// что касается их по вертикальным рёбрам (скругления вертикальных рёбер, касательные стенки), наклоняются на угол a вокруг
    /// нейтральной плоскости: точка грани на высоте H сдвигается по наружной нормали на δ(H) = −tg a·s·H (s — сторона тела от
    /// нейтрали). Плоскость поворачивается вокруг линии на нейтрали; цилиндр радиуса R становится конусом с полууглом a и тем же
    /// радиусом на нейтрали (R ± δ: выпуклый/вогнутый) — касание соседних стенок сохраняется (нормаль конуса на той же
    /// образующей совпадает с нормалью повёрнутой плоскости). Вертикальные рёбра касания — образующие на тех же азимутах;
    /// острые — пересечения новых плоскостей; рёбра с невыбранными гранями (верх, низ) — отрезки и соосные окружности на
    /// той же высоте. Вершины — пересечение вертикального ребра с невыбранной плоскостью или трёх плоскостей. Каждая точка
    /// проверяется на всех новых поверхностях; тело собирается заново (та же топология) и проверяется. null — отказ.
    /// </summary>
    internal static TSolid? DraftSolid(TSolid body, HashSet<TFace> selected, Vector3d neutralPoint, Vector3d m, double a, out int propagated, ref string? why)
    {
        propagated = 0;
        if (DraftTangentOff) { why = "tangent draft is off"; return null; }
        m = m.Normalized();
        double scale = 1; foreach (var v in body.Vertices) scale = Math.Max(scale, v.P.Length);
        double tol = 1e-9 * scale;
        var centre = Vector3d.Zero; int nv = 0; foreach (var v in body.Vertices) { centre += v.P; nv++; }
        centre /= Math.Max(1, nv);
        double sgn = Math.Sign(Vector3d.Dot(centre - neutralPoint, m)); if (sgn == 0) sgn = 1;
        double ta = Math.Tan(a);
        double H(Vector3d x) => Vector3d.Dot(x - neutralPoint, m);
        double Delta(Vector3d x) => -ta * sgn * H(x);

        bool RevOf(TFace f)
        {
            var src = GeometryAudit.AuthoritativeSurface(f)!;
            bool rev = f.Reversed;
            if (!ReferenceEquals(src, f.Surface) && f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P is Vector3d sp
                && Vector3d.Dot(src.NormalAt(src.ProjectToUV(sp)) * (f.Reversed ? -1 : 1), f.NormalAt(sp)) < 0)
                rev = !rev;
            return rev;
        }
        Vector3d Out(TFace f, Vector3d x)
        {
            var su = GeometryAudit.AuthoritativeSurface(f)!;
            var n = su.NormalAt(su.ProjectToUV(x)).Normalized();
            return RevOf(f) ? -n : n;
        }
        (Vector3d O, Vector3d W, double R)? CylOf(TFace f) => GeometryAudit.AuthoritativeSurface(f) switch
        {
            CylinderSurface cs => (cs.Axis.Origin, cs.Axis.W.Normalized(), cs.Radius),
            ExtrudedSurface { Base: ArcCurve3 ba } ex when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), ex.Dir.Normalized())) - 1) < 1e-12
                => (ba.Frame.Origin, ex.Dir.Normalized(), ba.Radius),
            _ => null,
        };
        bool Wall(TFace f)
        {
            var su = GeometryAudit.AuthoritativeSurface(f);
            if (su == null) return false;
            if (su.IsPlanar) { var p = f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P; return p != null && Math.Abs(Vector3d.Dot(Out(f, p.Value), m)) < 1e-9; }
            return CylOf(f) is { } c && Math.Abs(Math.Abs(Vector3d.Dot(c.W, m)) - 1) < 1e-9;
        }
        static List<TFace> FacesOf(TEdge e) => e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        bool Vertical(TEdge e) => e.Curve is LineCurve3 l && Math.Abs(Math.Abs(Vector3d.Dot((l.B - l.A).Normalized(), m)) - 1) < 1e-9;

        // уклоняемые грани: выбор + замыкание по касательным вертикальным рёбрам
        foreach (var f in selected) if (!Wall(f)) { why = "draft: a selected face is not a wall along the pull direction"; return null; }
        var D = new HashSet<TFace>(selected);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var e in body.Edges.Where(Vertical).ToList())
            {
                var fs = FacesOf(e); if (fs.Count != 2) continue;
                if (D.Contains(fs[0]) == D.Contains(fs[1])) continue;
                var mid = e.Curve.PointAt(0.5);
                if ((Out(fs[0], mid) - Out(fs[1], mid)).Length > 1e-9) continue;     // острое ребро — не продолжение
                var g = D.Contains(fs[0]) ? fs[1] : fs[0];
                if (!Wall(g)) continue;
                D.Add(g); propagated++; grew = true;
            }
        }
        if (!D.Any(f => CylOf(f) != null)) { why = "draft: no tangent cylinders (plane replacement covers this)"; return null; }

        // поверхности
        var newSurf = new Dictionary<TFace, (ISurface3 S, bool Rev)>();
        var coneR = new Dictionary<TFace, Func<Vector3d, double>>();
        double hMin = double.MaxValue, hMax = double.MinValue;
        foreach (var v in body.Vertices) { hMin = Math.Min(hMin, H(v.P)); hMax = Math.Max(hMax, H(v.P)); }
        double pad = 0.1 * Math.Max(1, hMax - hMin);
        foreach (var f in body.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src == null) { why = "face without an exact surface"; return null; }
            if (!D.Contains(f)) { newSurf[f] = (src, RevOf(f)); continue; }
            var probe = f.Loops.SelectMany(l => l.CoEdges).First().Start.P;
            var no = Out(f, probe);
            if (src.IsPlanar)
            {
                var p0 = probe - m * H(probe);
                var nn = (no * Math.Cos(a) + m * (sgn * Math.Sin(a))).Normalized();
                newSurf[f] = (new PlaneSurface(Frame3.FromNormal(p0, nn)), false);
            }
            else if (CylOf(f) is { } cy)
            {
                var w = cy.W; var rad = probe - cy.O; rad -= w * Vector3d.Dot(rad, w);
                double kappa = Vector3d.Dot(rad, no) > 0 ? 1 : -1;
                Func<Vector3d, double> R = x => cy.R + kappa * Delta(x);
                var ax0 = cy.O - w * Vector3d.Dot(cy.O - neutralPoint, w);       // точка оси на нейтрали
                Vector3d AxAt(double h) => ax0 + m * h;
                double h0 = hMin - pad, h1 = hMax + pad;
                if (!(R(AxAt(h0)) > tol && R(AxAt(h1)) > tol)) { why = "draft: the cone would close within the body"; return null; }
                var u = Vector3d.Cross(m, Math.Abs(m.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
                var gen = new LineCurve3(AxAt(h0) + u * R(AxAt(h0)), AxAt(h1) + u * R(AxAt(h1)));
                var cone = new RevolvedSurface(gen, new Frame3(ax0, u, Vector3d.Cross(m, u)), 0, 2 * Math.PI);
                var qp = gen.PointAt(0.5); var nr = qp - ax0; nr -= m * Vector3d.Dot(nr, m);
                bool rev = Vector3d.Dot(cone.NormalAt(cone.ProjectToUV(qp)), nr) * kappa < 0;
                newSurf[f] = (cone, rev); coneR[f] = R;
            }
            else { why = "draft: a drafted face is neither a plane nor a cylinder"; return null; }
        }
        bool OnSurf(ISurface3 s, Vector3d x) => (s.PointAt(s.ProjectToUV(x)) - x).Length <= tol;
        bool OnAll(IEnumerable<TFace> fs, Vector3d x) => fs.All(f => OnSurf(newSurf[f].S, x));

        // вертикальные рёбра между уклоняемыми гранями — новые прямые
        var vline = new Dictionary<TEdge, (Vector3d P, Vector3d Dir)>();
        foreach (var e in body.Edges.Where(Vertical))
        {
            var fs = FacesOf(e); if (fs.Count != 2 || !D.Contains(fs[0]) || !D.Contains(fs[1])) continue;
            var l = (LineCurve3)e.Curve; var mid = l.PointAt(0.5);
            Vector3d p1, p2;
            if ((Out(fs[0], mid) - Out(fs[1], mid)).Length <= 1e-9)
            {
                // касание: точка сдвигается по общей наружной нормали на δ(H) — образующая на том же азимуте
                p1 = l.A + Out(fs[0], l.A) * Delta(l.A); p2 = l.B + Out(fs[0], l.B) * Delta(l.B);
            }
            else if (newSurf[fs[0]].S is PlaneSurface pa && newSurf[fs[1]].S is PlaneSurface pb)
            {
                var na = pa.F.W.Normalized(); var nb = pb.F.W.Normalized(); var dir = Vector3d.Cross(na, nb);
                if (dir.Length < 1e-12) { why = "draft: parallel drafted planes"; return null; }
                dir.Normalize();
                // точка на обеих плоскостях, ближайшая к середине старого ребра
                var pt = Solve3(new[] { na, nb, dir }, new[] { Vector3d.Dot(na, pa.F.Origin), Vector3d.Dot(nb, pb.F.Origin), Vector3d.Dot(dir, mid) });
                if (pt == null) { why = "draft: drafted planes do not meet"; return null; }
                p1 = pt.Value; p2 = pt.Value + dir;
            }
            else { why = "draft: a sharp vertical edge next to a cone"; return null; }
            if (!OnAll(fs, p1) || !OnAll(fs, p2)) { why = "draft: a vertical edge does not lie on its drafted faces"; return null; }
            vline[e] = (p1, (p2 - p1).Normalized());
        }

        // вершины
        var vmap = new Dictionary<TVertex, Vector3d>();
        foreach (var v in body.Vertices)
        {
            var fs = v.Edges.SelectMany(FacesOf).Distinct().ToList();
            if (!fs.Any(D.Contains)) { vmap[v] = v.P; continue; }
            Vector3d? x = null;
            foreach (var e in v.Edges.Where(vline.ContainsKey))
            {
                var (P, Dir) = vline[e];
                foreach (var q in fs.Where(f => !D.Contains(f) && newSurf[f].S is PlaneSurface))
                {
                    var pl = (PlaneSurface)newSurf[q].S; var n = pl.F.W.Normalized(); double dn = Vector3d.Dot(Dir, n);
                    if (Math.Abs(dn) < 1e-9) continue;
                    x = P + Dir * (Vector3d.Dot(pl.F.Origin - P, n) / dn); break;
                }
                if (x != null) break;
            }
            if (x == null && fs.All(f => newSurf[f].S is PlaneSurface))
            {
                var ns = new List<(Vector3d N, double D)>();
                foreach (var f in fs) { var pl = (PlaneSurface)newSurf[f].S; var n = pl.F.W.Normalized(); if (!ns.Any(q => Math.Abs(Math.Abs(Vector3d.Dot(q.N, n)) - 1) < 1e-12)) ns.Add((n, Vector3d.Dot(n, pl.F.Origin))); }
                for (int i0 = 0; i0 < ns.Count && x == null; i0++)
                    for (int i1 = i0 + 1; i1 < ns.Count && x == null; i1++)
                        for (int i2 = i1 + 1; i2 < ns.Count && x == null; i2++)
                            x = Solve3(new[] { ns[i0].N, ns[i1].N, ns[i2].N }, new[] { ns[i0].D, ns[i1].D, ns[i2].D });
            }
            if (x == null || !OnAll(fs, x.Value)) { why = "draft: a vertex has no position on its new faces"; return null; }
            vmap[v] = x.Value;
        }

        // рёбра
        var emap = new Dictionary<TEdge, ICurve3>();
        foreach (var e in body.Edges)
        {
            var fs = FacesOf(e);
            ICurve3? nc = null;
            var a0 = vmap[e.V0]; var a1 = vmap[e.V1];
            if (e.Curve is LineCurve3 l)
            {
                bool fw = (l.A - e.V0.P).Length <= (l.A - e.V1.P).Length;
                nc = fw ? new LineCurve3(a0, a1) : new LineCurve3(a1, a0);
            }
            else if (e.Curve is ArcCurve3 ac)
            {
                var cf = fs.FirstOrDefault(f => coneR.ContainsKey(f));
                if (cf == null) { if ((a0 - e.V0.P).Length > tol || (a1 - e.V1.P).Length > tol) { why = "draft: an arc between undrafted faces moved"; return null; } nc = ac; }
                else
                {
                    if (Math.Abs(Math.Abs(Vector3d.Dot(ac.Frame.W.Normalized(), m)) - 1) > 1e-9) { why = "draft: an arc on a drafted cylinder is not across the axis"; return null; }
                    double r = coneR[cf](ac.Frame.Origin);
                    nc = new ArcCurve3(ac.Frame, r, ac.A0, ac.A1);
                }
            }
            else if (fs.Any(D.Contains)) { why = $"draft: a {e.Curve.GetType().Name} edge on a drafted face"; return null; }
            else nc = e.Curve;
            for (int k = 0; k <= 6; k++)
            {
                var q = nc.PointAt(nc.T0 + (nc.T1 - nc.T0) * k / 6.0);
                if (!OnAll(fs, q)) { why = "draft: a new edge leaves its faces"; return null; }
            }
            var c0 = nc.PointAt(nc.T0); var c1 = nc.PointAt(nc.T1);
            if (!(((c0 - a0).Length <= tol && (c1 - a1).Length <= tol) || ((c0 - a1).Length <= tol && (c1 - a0).Length <= tol)))
            { why = "draft: an edge does not end at its vertices"; return null; }
            if (e.V0 != e.V1 && (a0 - a1).Length < 1e-6) { why = "draft: an edge would vanish"; return null; }
            emap[e] = nc;
        }

        var res = Rebuild(body, newSurf, vmap, emap, null, (f, h) => D.Contains(f) ? h + Out(f, h) * Delta(h) : h, "draft", ref why);
        if (res != null) Interlocked.Increment(ref DraftTangents);
        return res;
    }
}
