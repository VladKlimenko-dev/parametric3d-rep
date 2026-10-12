using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация: P3D_DRTANLOOP=0 — касательные контуры из отрезков и дуг не строятся прямо (прежний отказ).</summary>
    static readonly bool TanLoops = Environment.GetEnvironmentVariable("P3D_DRTANLOOP") != "0";

    sealed class TPiece
    {
        public Seg S = null!; public FDesc W = null!; public ArcCurve3? Arc; public double SgnRad;
        public ICurve3 Q1C = null!, Q2C = null!;
    }

    /// <summary>
    /// KRN-06 (06.10/06.11): КАСАТЕЛЬНЫЙ КОНТУР — целый контур плоской грани Fp из отрезков (стенка — плоскость ⟂ Fp) и дуг
    /// (стенка — прямой круговой цилиндр с осью ⟂ Fp), соседние звенья касаются (контур выдавленного профиля после
    /// скругления вертикальных рёбер). Стенки перпендикулярны Fp, поэтому в каждой точке P контура центр шара
    /// C = P + σr(n_p + n_2), контакты Q1 = P + σr n_2 (на Fp: смещённый профиль — те же прямые и концентрические дуги радиуса
    /// R + σ r s) и Q2 = P + σr n_p (на стенке: тот же профиль, сдвинутый на σr по нормали Fp). Лента: над отрезком — цилиндр,
    /// над дугой — тор (ось — ось цилиндра стенки); фаска — плоскость и конус. Стык звеньев — четверть окружности (отрезок у
    /// фаски) в плоскости поперёк контура: общая кривая двух лент, без срезов и угловых патчей. Вертикальные рёбра стыка стенок
    /// укорачиваются на r. Открытые цепочки, наклонные стенки, разные размеры — не здесь (отказ, прежний путь).
    /// </summary>
    static bool TangentLoopBlends(List<TEdge> sel, Dictionary<TFace, FDesc> byFace, double r, bool ch, List<FDesc> blends, ref string? why)
    {
        if (_chamfer != null || _law != null) { why = "tangent loop: unequal chamfer or variable radius"; return false; }
        var faceSets = sel.Select(e => e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList()).ToList();
        if (faceSets.Any(s => s.Count != 2)) { why = "tangent loop: an edge is not between two faces"; return false; }
        var common = faceSets[0].Where(f => faceSets.All(s => s.Contains(f)) && byFace[f].Surf.IsPlanar).ToList();
        if (common.Count != 1) { why = "tangent loop: the edges do not share one planar face"; return false; }
        var Fp = byFace[common[0]];
        if (Fp.N.Length < 0.5) { why = "tangent loop: plane normal undefined"; return false; }
        var np = Fp.N.Normalized();
        var selSet = new HashSet<TEdge>(sel);
        var loops = Fp.Loops.Where(l => l.Any(s => s.Id != null && selSet.Contains(s.Id))).ToList();
        if (loops.Any(l => l.Any(s => s.Id == null || !selSet.Contains(s.Id)))) { why = "tangent loop: only whole loops of the face (open chains not in this family)"; return false; }
        if (loops.Sum(l => l.Count) != sel.Count) { why = "tangent loop: selected edges outside the face loops"; return false; }

        // звенья: стенка и её нормаль
        var pieces = new List<List<TPiece>>();
        foreach (var l in loops)
        {
            var pl = new List<TPiece>();
            foreach (var s in l)
            {
                var wf = s.Id!.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().First(f => byFace[f] != Fp);
                var W = byFace[wf]; var p = new TPiece { S = s, W = W };
                if (s.Curve is LineCurve3)
                {
                    if (!W.Surf.IsPlanar || W.N.Length < 0.5 || Math.Abs(Vector3d.Dot(W.N.Normalized(), np)) > 1e-9) { why = "tangent loop: a wall is not perpendicular to the face"; return false; }
                }
                else if (s.Curve is ArcCurve3 a && !a.IsClosed)
                {
                    if (!W.IsCyl || Math.Abs(Math.Abs(Vector3d.Dot(W.AxW, np)) - 1) > 1e-12) { why = "tangent loop: an arc's wall is not a cylinder along the face normal"; return false; }
                    var dO = a.Frame.Origin - W.AxO; double sc = Math.Max(1, a.Radius);
                    if ((dO - W.AxW * Vector3d.Dot(dO, W.AxW)).Length > 1e-9 * sc || Math.Abs(a.Radius - W.AxR) > 1e-9 * sc) { why = "tangent loop: an arc is not the cylinder's section"; return false; }
                    p.Arc = a;
                    var m = a.PointAt(0.5); var rad = Radial(a, np, m);
                    p.SgnRad = Vector3d.Dot(NormalOf(W, m), rad) > 0 ? 1 : -1;
                }
                else { why = "tangent loop: an edge is neither a line nor an open arc"; return false; }
                pl.Add(p);
            }
            pieces.Add(pl);
        }
        static Vector3d Radial(ArcCurve3 a, Vector3d np, Vector3d x) { var d = x - a.Frame.Origin; d -= np * Vector3d.Dot(d, np); return d.Normalized(); }
        Vector3d N2(TPiece p, Vector3d x) => p.Arc is { } a ? Radial(a, np, x) * p.SgnRad : p.W.N.Normalized();

        // сторона (выпуклость) — по геометрии: направление от ребра внутрь Fp — ±n_2; выпукло, если против n_2
        double? sigma = null;
        foreach (var p in pieces.SelectMany(x => x))
        {
            var m = p.S.Curve.PointAt((p.S.Curve.T0 + p.S.Curve.T1) / 2); var n2 = N2(p, m);
            var d = IntoPlanarFace(Fp, m, n2);
            if (d == null) { why = "tangent loop: convexity undecided"; return false; }
            double sg = Vector3d.Dot(d.Value, n2) < 0 ? -1 : 1;
            if (sigma == null) sigma = sg; else if (sigma != sg) { why = "tangent loop: convex and concave edges"; return false; }
        }
        double sgm = sigma!.Value;
        Vector3d Q1(TPiece p, Vector3d x) => x + N2(p, x) * (sgm * r);
        Vector3d Q2(Vector3d x) => x + np * (sgm * r);
        Vector3d Cc(TPiece p, Vector3d x) => x + (np + N2(p, x)) * (sgm * r);

        // стыки: касание и непрерывность нормали стенки
        static Vector3d Dir(Seg s, bool atEnd)
        {
            bool fw = (s.Curve.PointAt(s.Curve.T0) - s.A).Length <= (s.Curve.PointAt(s.Curve.T0) - s.B).Length;
            double t = atEnd == fw ? s.Curve.T1 : s.Curve.T0;
            var tg = s.Curve.TangentAt(t).Normalized();
            return fw ? tg : -tg;
        }
        var joints = new List<(Vector3d P, TPiece Before, TPiece After)>();
        foreach (var pl in pieces)
            for (int k = 0; k < pl.Count; k++)
            {
                var a = pl[k]; var b = pl[(k + 1) % pl.Count];
                if ((a.S.B - b.S.A).Length > 1e-7) { why = "tangent loop: loop not chained"; return false; }
                var P = a.S.B;
                if (Vector3d.Dot(Dir(a.S, true), Dir(b.S, false)) < 1 - 1e-9) { why = "tangent loop: a joint is not tangent"; return false; }
                if ((N2(a, P) - N2(b, P)).Length > 1e-7) { why = "tangent loop: wall normals differ at a joint"; return false; }
                joints.Add((P, a, b));
            }

        // смещённые звенья
        foreach (var p in pieces.SelectMany(x => x))
        {
            if (p.Arc is { } a)
            {
                double rho = a.Radius + sgm * r * p.SgnRad;
                if (!(rho > 1e-6 * Math.Max(1, a.Radius))) { why = "tangent loop: radius too large for an arc of the loop"; return false; }
                p.Q1C = new ArcCurve3(new Frame3(a.Frame.Origin, a.Frame.U, a.Frame.V), rho, a.A0, a.A1);
                p.Q2C = new ArcCurve3(new Frame3(a.Frame.Origin + np * (sgm * r), a.Frame.U, a.Frame.V), a.Radius, a.A0, a.A1);
            }
            else
            {
                var l = (LineCurve3)p.S.Curve;
                p.Q1C = new LineCurve3(Q1(p, l.A), Q1(p, l.B)); p.Q2C = new LineCurve3(Q2(l.A), Q2(l.B));
            }
            foreach (var x in new[] { p.S.A, p.S.B })
            {
                double e1 = Math.Min((p.Q1C.PointAt(p.Q1C.T0) - Q1(p, x)).Length, (p.Q1C.PointAt(p.Q1C.T1) - Q1(p, x)).Length);
                double e2 = Math.Min((p.Q2C.PointAt(p.Q2C.T0) - Q2(x)).Length, (p.Q2C.PointAt(p.Q2C.T1) - Q2(x)).Length);
                if (e1 > 1e-7 || e2 > 1e-7) { why = "tangent loop: offset curve does not meet the contact points"; return false; }
            }
        }

        // место: смещённый контур не ближе r к другим звеньям (узкие пазы), другие контуры Fp — не в полосе
        var all = pieces.SelectMany(x => x).ToList();
        foreach (var p in all)
            for (int k = 1; k < 16; k++)
            {
                var x = p.Q1C.PointAt(p.Q1C.T0 + (p.Q1C.T1 - p.Q1C.T0) * k / 16.0);
                foreach (var o in all) if (DistToCurve(o.S.Curve, x) < r * (1 - 1e-6)) { why = "tangent loop: the offset loop runs into itself (radius too large)"; return false; }
            }
        foreach (var l in Fp.Loops.Where(l => !loops.Contains(l)))
            foreach (var s in l)
                for (int k = 0; k < 32; k++)
                {
                    var x = s.Curve.PointAt(s.Curve.T0 + (s.Curve.T1 - s.Curve.T0) * k / 32.0);
                    foreach (var o in all) if (DistToCurve(o.S.Curve, x) <= r + 1e-7) { why = "tangent loop: another loop of the face lies in the blend strip"; return false; }
                }

        // правка Fp: контур → смещённый (то же направление)
        foreach (var (l, pl) in loops.Zip(pieces))
            for (int k = 0; k < l.Count; k++)
                l[k] = new Seg { Curve = pl[k].Q1C, A = Q1(pl[k], pl[k].S.A), B = Q1(pl[k], pl[k].S.B), Id = pl[k].S.Id };
        Fp.Touched = true;

        // правка стенок: ребро → сдвинутое на σr по n_p; рёбра стыков вдоль n_p — укоротить на r
        var jointPts = joints.Select(j => j.P).ToList();
        var wallHeight = new Dictionary<FDesc, double>();
        foreach (var p in all)
        {
            var W = p.W; bool done = false;
            foreach (var l in W.Loops)
                for (int k = 0; k < l.Count && !done; k++)
                    if (l[k].Id == p.S.Id && l[k].Orig != null)
                    {
                        l[k] = new Seg { Curve = p.Q2C, A = Q2(l[k].A), B = Q2(l[k].B), Id = p.S.Id };
                        done = true;
                    }
            if (!done) { why = "tangent loop: the edge is not in its wall's loops"; return false; }
            W.Touched = true;
        }
        foreach (var W in all.Select(p => p.W).Distinct())
            foreach (var l in W.Loops)
                for (int k = 0; k < l.Count; k++)
                {
                    var s = l[k]; if (s.Orig == null || (s.Id != null && selSet.Contains(s.Id))) continue;
                    int ia = jointPts.FindIndex(P => (P - s.A).Length < 1e-7), ib = jointPts.FindIndex(P => (P - s.B).Length < 1e-7);
                    if (ia < 0 && ib < 0) continue;
                    if (ia >= 0 && ib >= 0) { why = "tangent loop: a wall edge joins two joints"; return false; }
                    if (s.Curve is not LineCurve3 || Math.Abs(Math.Abs(Vector3d.Dot((s.B - s.A).Normalized(), np)) - 1) > 1e-9) { why = "tangent loop: a joint edge of the walls is not along the face normal"; return false; }
                    var P = ia >= 0 ? s.A : s.B; var far = ia >= 0 ? s.B : s.A; var nP = Q2(P);
                    double len = (far - P).Length, u = Vector3d.Dot(nP - P, far - P) / (len * len);
                    if (!(u > 1e-9 && u < 1 - 1e-9)) { why = "tangent loop: a wall is not taller than the blend"; return false; }
                    var ns = Shorten(s, ia >= 0 ? nP : null, ib >= 0 ? nP : null);
                    if (ns == null) { why = "tangent loop: a joint edge cannot be shortened"; return false; }
                    l[k] = ns;
                    wallHeight[W] = Math.Min(wallHeight.TryGetValue(W, out var h0) ? h0 : double.MaxValue, len);
                }
        // точка цилиндрической стенки — середина оставшейся полосы под сдвинутой дугой
        foreach (var p in all.Where(p => p.Arc != null))
        {
            if (!wallHeight.TryGetValue(p.W, out var h)) { why = "tangent loop: wall height unknown"; return false; }
            p.W.Hint = p.Q2C.PointAt((p.Q2C.T0 + p.Q2C.T1) / 2) + np * (sgm * (h - r) / 2);
        }

        // ленты и стыки
        var jointCurve = new Dictionary<int, ICurve3>();
        ICurve3 Joint(TPiece p, Vector3d P)
        {
            int ji = jointPts.FindIndex(x => (x - P).Length < 1e-7);
            if (jointCurve.TryGetValue(ji, out var c)) return c;
            var q1 = Q1(p, P); var q2 = Q2(P); var C = Cc(p, P);
            c = ch ? new LineCurve3(q1, q2) : new ArcCurve3(new Frame3(C, (q1 - C) / r, (q2 - C) / r), r, 0, Math.PI / 2);
            jointCurve[ji] = c; return c;
        }
        foreach (var p in all)
        {
            var A = p.S.A; var B = p.S.B;
            var jA = Joint(p, A); var jB = Joint(p, B);
            if ((jA.PointAt(jA.T0) - Q1(p, A)).Length > 1e-7 || (jA.PointAt(jA.T1) - Q2(A)).Length > 1e-7) { why = "tangent loop: joint arc does not meet the contacts"; return false; }
            var loopB = new List<Seg>
            {
                new() { Curve = p.Q1C, A = Q1(p, B), B = Q1(p, A) },
                new() { Curve = jA, A = Q1(p, A), B = Q2(A) },
                new() { Curve = p.Q2C, A = Q2(A), B = Q2(B) },
                new() { Curve = jB, A = Q2(B), B = Q1(p, B) },
            };
            var xm = p.S.Curve.PointAt((p.S.Curve.T0 + p.S.Curve.T1) / 2);
            var hint = ch ? (Q1(p, xm) + Q2(xm)) / 2 : Cc(p, xm) - (np + N2(p, xm)).Normalized() * (sgm * r);
            ISurface3 surf; bool rev;
            if (p.Arc is { } a)
            {
                var O = a.Frame.Origin; var U = Radial(a, np, A); var fr = Frame3.FromNormal(O, np);
                var Ct = O + np * (sgm * r); double rho = a.Radius + sgm * r * p.SgnRad; var tube = Ct + U * rho;
                var qP = O + U * rho; var qC = Ct + U * a.Radius;
                ICurve3 gen; Vector3d mid, want;
                if (ch) { gen = new LineCurve3(qP, qC); mid = (qP + qC) / 2; want = (np + U * p.SgnRad).Normalized(); }
                else
                {
                    var mf = new Frame3(tube, U, np);
                    double a0 = Math.Atan2(Vector3d.Dot(qP - tube, np), Vector3d.Dot(qP - tube, U));
                    double a1 = Math.Atan2(Vector3d.Dot(qC - tube, np), Vector3d.Dot(qC - tube, U));
                    double da = a1 - a0; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
                    gen = new ArcCurve3(mf, r, a0, a0 + da);
                    mid = gen.PointAt(0.5); want = (mid - tube).Normalized() * (-sgm);
                }
                if ((gen.PointAt(gen.T0) - qP).Length > 1e-9 * Math.Max(1, rho) || (gen.PointAt(gen.T1) - qC).Length > 1e-9 * Math.Max(1, rho)) { why = "tangent loop: torus generator does not meet the contacts"; return false; }
                var rs = new RevolvedSurface(gen, new Frame3(Ct, fr.U, fr.V), 0, 2 * Math.PI);
                surf = rs; rev = Vector3d.Dot(rs.NormalAt(rs.ProjectToUV(mid)), want) < 0;
            }
            else
            {
                var t = (B - A).Normalized(); var C0 = Cc(p, A);
                if (ch)
                {
                    var nc = Vector3d.Cross(t, Q2(A) - Q1(p, A)).Normalized(); if (Vector3d.Dot(nc, np + N2(p, A)) < 0) nc = -nc;
                    surf = new PlaneSurface(Frame3.FromNormal(Q1(p, A), nc)); rev = false;
                }
                else
                {
                    var e1 = (Q1(p, A) - C0) / r; var e2 = Vector3d.Cross(t, e1);
                    surf = new CylinderSurface(new Frame3(C0, e1, e2), r); rev = sgm > 0;
                }
            }
            var bl = new FDesc { Surf = surf, Rev = rev, Tag = ch ? "chamfer:face" : "round:face", Touched = true, Hint = hint };
            if (surf is PlaneSurface) bl.N = (surf.NormalAt(surf.ProjectToUV(hint)) * (rev ? -1 : 1)).Normalized();
            bl.Loops.Add(loopB);
            blends.Add(bl);
        }
        return true;
    }

    /// <summary>Расстояние от точки до отрезка или дуги (иначе — по 64 точкам кривой).</summary>
    static double DistToCurve(ICurve3 c, Vector3d x)
    {
        if (c is LineCurve3 l)
        {
            var ab = l.B - l.A; double u = ab.LengthSquared < 1e-30 ? 0 : Math.Clamp(Vector3d.Dot(x - l.A, ab) / ab.LengthSquared, 0, 1);
            return (l.A + ab * u - x).Length;
        }
        if (c is ArcCurve3 a)
        {
            double best = Math.Min((a.PointAt(a.T0) - x).Length, (a.PointAt(a.T1) - x).Length);
            var W = a.Frame.W.Normalized(); var d = x - a.Frame.Origin; var dp = d - W * Vector3d.Dot(d, W);
            if (dp.Length > 1e-12)
            {
                double th = Math.Atan2(Vector3d.Dot(dp, a.Frame.V.Normalized()), Vector3d.Dot(dp, a.Frame.U.Normalized()));
                double tau = (th - a.A0) / a.Sweep, per = 2 * Math.PI / Math.Abs(a.Sweep);
                while (tau < 0) tau += per; while (tau > per) tau -= per;
                if (tau <= 1 || a.IsClosed) best = Math.Min(best, (a.Frame.Origin + dp.Normalized() * a.Radius - x).Length);
            }
            return best;
        }
        double m = double.MaxValue;
        for (int k = 0; k <= 64; k++) m = Math.Min(m, (c.PointAt(c.T0 + (c.T1 - c.T0) * k / 64.0) - x).Length);
        return m;
    }
}
