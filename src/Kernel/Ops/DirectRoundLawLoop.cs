using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_DRLAWLOOP=0: закон радиуса на касательном контуре — отказ (прежний путь резцов).</summary>
    static readonly bool LawLoopOn = Environment.GetEnvironmentVariable("P3D_DRLAWLOOP") != "0";
    /// <summary>Мутация P3D_DRLUNE=0: стык звеньев — сечение следующего звена без «луночки» (щель между лентами).</summary>
    static readonly bool LuneOn = Environment.GetEnvironmentVariable("P3D_DRLUNE") != "0";
    public static long LawLoops, LawLunes, LawCreases, CreaseFallbacks, LawOpenChains, LawMitres;
    /// <summary>Мутация P3D_DRLAWMITRE=0: стык «на ус» в цепочке с законом — отказ.</summary>
    static readonly bool MitreOn = Environment.GetEnvironmentVariable("P3D_DRLAWMITRE") != "0";
    /// <summary>Мутация P3D_DRLAWOPEN=0: открытая цепочка с законом — отказ.</summary>
    static readonly bool LawOpenOn = Environment.GetEnvironmentVariable("P3D_DRLAWOPEN") != "0";
    /// <summary>Мутация P3D_DRCREASE=0: перекрытие лент на стыке — отказ.</summary>
    static readonly bool CreaseOn = Environment.GetEnvironmentVariable("P3D_DRCREASE") != "0";

    /// <summary>
    /// KRN-06 (06.2, 06.14): ЗАКОН РАДИУСА НА КАСАТЕЛЬНОМ КОНТУРЕ — целый контур плоской грани Fp из отрезков (стенка —
    /// плоскость ⟂ Fp) и дуг (стенка — цилиндр с осью ⟂ Fp), радиус r(F) — по доле длины контура F от начала первой ссылки.
    /// Шар радиуса r(s) касается Fp и стенки: центр C = P + σr(n_p + n₂), контакты Q₁ = P + σr n₂ (на Fp), Q₂ = P + σr n_p
    /// (на стенке). Лента — огибающая шаров: в каждом s — характеристическая окружность (плоскость ⟂ C′, центр
    /// C − (r r′/|C′|²) C′, радиус √(r² − (r r′/|C′|)²)); она проходит через Q₁ и Q₂ при любом r′ (n_p ⟂ C′ − σr′(n_p + n₂)).
    /// C′ = T(1 + σ r κ) + σ r′(n_p + n₂): у стыка отрезка с дугой κ скачет, и при r′ ≠ 0 окружности с двух сторон разные —
    /// обе на одном шаре стыка и обе через его контакты: огибающая там — «луночка» этого шара между ними (предел сглаживания
    /// хребта). Так же в начале контура, где закон замыкается с изломом r′. null-путь — отказ с причиной.
    /// </summary>
    static bool TangentLoopLawBlends(List<TEdge> sel, Dictionary<TFace, FDesc> byFace, RadiusLaw law, Vector3d start, Vector3d next,
                                             List<FDesc> blends, ref string? why)
    {
        if (!LawLoopOn) { why = "law loop: off"; return false; }
        var faceSets = sel.Select(e => e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList()).ToList();
        if (faceSets.Any(s => s.Count != 2)) { why = "law loop: an edge is not between two faces"; return false; }
        var common = faceSets[0].Where(f => faceSets.All(s => s.Contains(f)) && byFace[f].Surf.IsPlanar).ToList();
        if (common.Count != 1) { why = "law loop: the edges do not share one planar face"; return false; }
        var Fp = byFace[common[0]];
        if (Fp.N.Length < 0.5) { why = "law loop: plane normal undefined"; return false; }
        var np = Fp.N.Normalized();
        var selSet = new HashSet<TEdge>(sel);
        var loops = Fp.Loops.Where(l => l.Any(s => s.Id != null && selSet.Contains(s.Id))).ToList();
        if (loops.Count != 1) { why = "law loop: the selection is not in one loop of the face"; return false; }
        var loop = loops[0]; int NL = loop.Count;
        bool IsSel(Seg x) => x.Id != null && selSet.Contains(x.Id);
        // ЦЕПОЧКА: весь контур (замкнутая) или одна непрерывная серия звеньев (открытая, концы — свободные: торец — плоская
        // грань соседнего невыбранного звена, как у постоянного радиуса)
        bool closedRun = loop.All(IsSel);
        int st = 0;
        if (!closedRun)
        {
            if (!LawOpenOn) { why = "law loop: open chains are off"; return false; }
            st = -1;
            for (int k = 0; k < NL; k++) if (IsSel(loop[k]) && !IsSel(loop[(k - 1 + NL) % NL])) { if (st >= 0) { why = "law chain: several runs in one loop"; return false; } st = k; }
        }
        var orig = new List<Seg>();                // исходные звенья: геометрия формул (контур Fp правится ниже)
        for (int k = st; orig.Count < NL && IsSel(loop[k % NL]); k++) orig.Add(loop[k % NL]);
        if (orig.Count != sel.Count) { why = "law loop: selected edges outside one run of the face loop"; return false; }
        int n = orig.Count;

        // звенья: стенка, n₂, кривизна
        var P = new TPiece[n];
        for (int k = 0; k < n; k++)
        {
            var s = orig[k];
            var wf = s.Id!.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().First(f => byFace[f] != Fp);
            var W = byFace[wf]; var p = new TPiece { S = s, W = W };
            if (s.Curve is LineCurve3)
            {
                if (!W.Surf.IsPlanar || W.N.Length < 0.5 || Math.Abs(Vector3d.Dot(W.N.Normalized(), np)) > 1e-12) { why = "law loop: a wall is not a plane perpendicular to the face"; return false; }
            }
            else if (s.Curve is ArcCurve3 a && !a.IsClosed && W.IsCyl)
            {
                double sc = Math.Max(1, a.Radius);
                if (Math.Abs(Math.Abs(Vector3d.Dot(W.AxW, np)) - 1) > 1e-12) { why = "law loop: an arc's wall is not a cylinder along the face normal"; return false; }
                var dO = a.Frame.Origin - W.AxO;
                if ((dO - W.AxW * Vector3d.Dot(dO, W.AxW)).Length > 1e-9 * sc || Math.Abs(a.Radius - W.AxR) > 1e-9 * sc) { why = "law loop: an arc is not the cylinder's section"; return false; }
                p.Arc = a;
                var m = a.PointAt(0.5); var d = m - a.Frame.Origin; d -= np * Vector3d.Dot(d, np);
                p.SgnRad = Vector3d.Dot(NormalOf(W, m), d.Normalized()) > 0 ? 1 : -1;
            }
            else { why = "law loop: an edge is neither a line (planar wall) nor an open arc (cylinder wall)"; return false; }
            P[k] = p;
        }
        // длины и направления звеньев (по обходу контура A → B)
        var len = new double[n]; var s0 = new double[n + 1];
        for (int k = 0; k < n; k++)
        {
            var c = orig[k].Curve;
            len[k] = c is ArcCurve3 a ? Math.Abs(a.Sweep) * a.Radius : (orig[k].B - orig[k].A).Length;
            s0[k + 1] = s0[k] + len[k];
        }
        double L = s0[n];
        Vector3d Radial(ArcCurve3 a, Vector3d x) { var d = x - a.Frame.Origin; d -= np * Vector3d.Dot(d, np); return d.Normalized(); }
        // точка звена k по длине σ ∈ [0, len]: обход от A к B
        Vector3d Pt(int k, double sg)
        {
            var sgm0 = orig[k];
            if (P[k].Arc is { } a)
            {
                bool fw = (a.PointAt(a.T0) - sgm0.A).Length <= (a.PointAt(a.T0) - sgm0.B).Length;
                double f = sg / len[k]; return a.PointAt(fw ? a.T0 + (a.T1 - a.T0) * f : a.T1 - (a.T1 - a.T0) * f);
            }
            return sgm0.A + (sgm0.B - sgm0.A) * (sg / len[k]);
        }
        Vector3d Tan(int k, double sg)
        {
            if (P[k].Arc is { } a)
            {
                var x = Pt(k, sg); var rad = Radial(a, x); var t = Vector3d.Cross(np, rad);
                // направление обхода: по разности соседних точек
                var dx = Pt(k, Math.Min(len[k], sg + 1e-6 * len[k])) - Pt(k, Math.Max(0, sg - 1e-6 * len[k]));
                return Vector3d.Dot(t, dx) >= 0 ? t : -t;
            }
            return (orig[k].B - orig[k].A).Normalized();
        }
        for (int k = 0; k < n; k++)
            if ((Pt(k, 0) - orig[k].A).Length > 1e-7 || (Pt(k, len[k]) - orig[k].B).Length > 1e-7) { why = $"law loop: piece {k} parametrisation does not meet its ends"; return false; }
        Vector3d N2(int k, Vector3d x) => P[k].Arc is { } a ? Radial(a, x) * P[k].SgnRad : (P[k].W.N.Normalized() - np * Vector3d.Dot(P[k].W.N.Normalized(), np)).Normalized();
        // стыки: касательные — или «на ус» (два отрезка с плоскими стенками; ребро стенок в вершине — вдоль n_p, проверяется при
        // укорачивании): ленты продлеваются друг в друга и обрезаются по шву их пересечения (как перекрытие на касательном стыке)
        var mitre = new bool[n];
        for (int k = 0; k < (closedRun ? n : n - 1); k++)
        {
            int k2 = (k + 1) % n;
            if ((orig[k].B - orig[k2].A).Length > 1e-7) { why = "law loop: loop not chained"; return false; }
            if (Vector3d.Dot(Tan(k, len[k]), Tan(k2, 0)) < 1 - 1e-9)
            {
                if (!MitreOn) { why = "law loop: a joint is not tangent"; return false; }
                if (P[k].Arc != null || P[k2].Arc != null) { why = "law chain: a sharp joint next to an arc"; return false; }
                if (Vector3d.Dot(Tan(k, len[k]), Tan(k2, 0)) < -0.9) { why = "law chain: a sharp joint folds back"; return false; }
                mitre[k] = true; continue;
            }
            if ((N2(k, orig[k].B) - N2(k2, orig[k2].A)).Length > 1e-7) { why = "law loop: wall normals differ at a joint"; return false; }
        }
        // выпуклость
        double? sigma = null;
        for (int k = 0; k < n; k++)
        {
            var m = Pt(k, len[k] / 2); var n2 = N2(k, m);
            var d = IntoPlanarFace(Fp, m, n2);
            if (d == null) { why = "law loop: convexity undecided"; return false; }
            double sg = Vector3d.Dot(d.Value, n2) < 0 ? -1 : 1;
            if (sigma == null) sigma = sg; else if (sigma != sg) { why = "law loop: convex and concave edges"; return false; }
        }
        double sgm = sigma!.Value;

        // закон: доля F от начала первой ссылки в её сторону; звено kS начинается или кончается в start. У открытой цепочки —
        // от её свободного конца, ближнего (по цепочке) к началу первой ссылки, к другому концу
        int kS = -1; double dirS = 0;
        for (int k = 0; k < n && kS < 0; k++)
        {
            if ((orig[k].A - start).Length < 1e-7 && (orig[k].B - next).Length < 1e-7) { kS = k; dirS = 1; }
            else if ((orig[k].B - start).Length < 1e-7 && (orig[k].A - next).Length < 1e-7) { kS = k; dirS = -1; }
        }
        if (kS < 0) { why = "law loop: the law start is not an edge of the loop"; return false; }
        double sStart = dirS > 0 ? s0[kS] : s0[kS + 1];
        if (!closedRun) { if (sStart <= L - sStart) { sStart = 0; dirS = 1; } else { sStart = L; dirS = -1; } }
        if (closedRun && Math.Abs(law.R(0) - law.R(1)) > 1e-12 * Math.Max(1, law.R(0))) { why = "law loop: the radius law does not close (r(0) ≠ r(1))"; return false; }
        // доля F на звене k — непрерывно по звену (шов закона — только на стыке звеньев: начало — вершина)
        double Fk(int k, double sg)
        {
            double f = dirS * (s0[k] + sg - sStart) / L;
            if (!closedRun) return f;
            double fm = dirS * (s0[k] + len[k] / 2 - sStart) / L;
            double sh = Math.Floor(fm);
            return f - sh;
        }
        double Rk(int k, double sg) => law.R(Fk(k, sg));
        double dRk(int k, double sg) => law.dR(Fk(k, sg)) * dirS / L;
        // проверки размера: радиус положителен, у дуг — меньше радиуса выпуклой дуги (без полюса)
        for (int k = 0; k < n; k++)
            for (int q = 0; q <= 64; q++)
            {
                double sg = len[k] * q / 64, r = Rk(k, sg);
                if (!(r > 1e-6)) { why = "law loop: the radius is not positive"; return false; }
                if (P[k].Arc is { } a && !(a.Radius + sgm * r * P[k].SgnRad > 1e-6 * Math.Max(1, a.Radius))) { why = "law loop: radius too large for an arc of the loop"; return false; }
            }

        // формулы звена
        Vector3d Cn(int k, double sg) { var x = Pt(k, sg); return x + (np + N2(k, x)) * (sgm * Rk(k, sg)); }
        Vector3d Q1(int k, double sg) { var x = Pt(k, sg); return x + N2(k, x) * (sgm * Rk(k, sg)); }
        Vector3d Q2(int k, double sg) { var x = Pt(k, sg); return x + np * (sgm * Rk(k, sg)); }
        Vector3d dC(int k, double sg)
        {
            var x = Pt(k, sg); var t = Tan(k, sg); double r = Rk(k, sg), rp = dRk(k, sg);
            double kap = P[k].Arc is { } a ? P[k].SgnRad / a.Radius : 0;
            return t * (1 + sgm * r * kap) + (np + N2(k, x)) * (sgm * rp);
        }
        SweepSection? Sec(int k, double sg)
        {
            var C = Cn(k, sg); var d = dC(k, sg); double dl = d.Length, r = Rk(k, sg), rp = dRk(k, sg);
            double del = -r * rp / dl; if (!(Math.Abs(del) < r)) return null;
            var Cc = C + d * (del / dl); double rho = Math.Sqrt(r * r - del * del);
            var q1 = Q1(k, sg); var q2 = Q2(k, sg);
            var e1 = (q1 - Cc) / rho; var w = q2 - Cc; var e2v = w - e1 * Vector3d.Dot(w, e1);
            if (e2v.Length < 1e-12 * rho) return null;
            var e2 = e2v.Normalized();
            double phi = Math.Atan2(Vector3d.Dot(w, e2), Vector3d.Dot(w, e1));
            if (!(phi > 0 && phi < Math.PI)) return null;
            return SweepSection.Arc(Cc, e1, e2, rho, phi);
        }
        // согласованность формулы: окружность проходит через оба контакта
        for (int k = 0; k < n; k++)
            for (int q = 0; q <= 16; q++)
            {
                double sg = len[k] * q / 16; var sc = Sec(k, sg);
                if (sc == null) { why = "law loop: the radius changes too fast (no characteristic circle)"; return false; }
                double r0 = Rk(k, sg), e1 = Math.Abs((Q1(k, sg) - sc.Value.C).Length - sc.Value.R), e2 = Math.Abs((Q2(k, sg) - sc.Value.C).Length - sc.Value.R);
                if (e1 > 1e-9 * Math.Max(1, r0) || e2 > 1e-9 * Math.Max(1, r0)) { why = "law loop: characteristic circle misses a contact"; return false; }
            }
        // СТЫК «НА УС»: контакты на Fp встречаются в точке T* (кривые Q₁ двух звеньев в плоскости грани — ньютоном по (s, s′));
        // звено k — до s₁ ≤ len, следующее — от s₁′ ≥ 0
        var mA1 = new double[n]; var mB1 = new double[n];
        for (int k = 0; k < n; k++) { mA1[k] = 0; mB1[k] = len[k]; }
        for (int k = 0; k < n; k++)
        {
            if (!mitre[k]) continue;
            int k2 = (k + 1) % n; double rj = Rk(k, len[k]);
            var u0 = Tan(k, len[k]); var v0 = Vector3d.Cross(np, u0);
            Vector2d F2(double a, double b) { var d = Q1(k, a) - Q1(k2, b); return new Vector2d(Vector3d.Dot(d, u0), Vector3d.Dot(d, v0)); }
            double sa = len[k] - rj, sb = rj; bool ok = false;
            for (int it = 0; it < 60; it++)
            {
                var f = F2(sa, sb); if (f.Length < 1e-13 * Math.Max(1, rj)) { ok = true; break; }
                double h = 1e-6 * Math.Max(1, rj);
                var ja = (F2(sa + h, sb) - F2(sa - h, sb)) / (2 * h); var jb = (F2(sa, sb + h) - F2(sa, sb - h)) / (2 * h);
                double det = ja.X * jb.Y - ja.Y * jb.X; if (Math.Abs(det) < 1e-14) break;
                double da = (f.X * jb.Y - f.Y * jb.X) / det, db = (ja.X * f.Y - ja.Y * f.X) / det;
                sa -= da; sb -= db;
            }
            if (!ok || !(sa > 0 && sa < len[k]) || !(sb > 0 && sb < len[k2])) { why = "law chain: the contacts of a sharp joint do not meet inside both edges (reflex corner or radius too large)"; return false; }
            mB1[k] = sa; mA1[k2] = sb;
            Interlocked.Increment(ref LawMitres);
        }
        // место: смещённый контур не ближе r к другим звеньям
        for (int k = 0; k < n; k++)
            for (int q = 1; q < 16; q++)
            {
                double sg = mA1[k] + (mB1[k] - mA1[k]) * q / 16; var x = Q1(k, sg); double r = Rk(k, sg);
                for (int o = 0; o < n; o++) if (DistToCurve(orig[o].Curve, x) < r * (1 - 1e-6)) { why = "law loop: the offset loop runs into itself (radius too large)"; return false; }
            }
        foreach (var l in Fp.Loops.Where(l => l != loop).Append(closedRun ? new List<Seg>() : Enumerable.Range(0, NL).Where(i => !IsSel(loop[i]) && i != (st - 1 + NL) % NL && i != (st + n) % NL).Select(i => loop[i]).ToList()))
            foreach (var s in l)
                for (int q = 0; q < 32; q++)
                {
                    var x = s.Curve.PointAt(s.Curve.T0 + (s.Curve.T1 - s.Curve.T0) * q / 32.0);
                    for (int o = 0; o < n; o++) if (DistToCurve(orig[o].Curve, x) <= law.Knots.Concat(new[] { 0.0, 1.0 }).Max(f => law.R(f)) + 1e-7) { why = "law loop: another loop of the face lies in the blend strip"; return false; }
                }

        // СВОБОДНЫЕ КОНЦЫ открытой цепочки: концевое звено — отрезок, торец We — плоская грань соседнего невыбранного звена Fp.
        // Лента продлевается за конец (отрезок и закон — продолжением) и обрезается плоскостью торца: контакты — корни
        // (Q(s) − V)·n_e = 0, край ленты на торце — для каждой доли v сечения корень по s (процедурная кривая).
        var uLo = new double[n]; var uHi = new double[n];
        for (int k = 0; k < n; k++) { uLo[k] = 0; uHi[k] = len[k]; }
        // «на ус»: лента звена после стыка продлена назад (шов пересечения может лечь на продление)
        for (int k = 0; k < n; k++)
            if (mitre[k]) { int k2 = (k + 1) % n; uLo[k2] = -Math.Min(len[k2], Math.Max(0.45 * Math.Min(len[k2], Rk(k, len[k])), 1.5 * mA1[k2])); }
        double a1s = 0, a2s = 0, b1e = len[n - 1], b2e = len[n - 1];
        ProcCurve3? trimA = null, trimB = null; FDesc? WeA = null, WeB = null; Vector3d VA = default, VB = default;
        if (!closedRun)
        {
            foreach (bool atA in new[] { true, false })
            {
                int k = atA ? 0 : n - 1;
                if (P[k].Arc != null) { why = "law chain: an end of the chain is an arc"; return false; }
                var nb = atA ? loop[(st - 1 + NL) % NL] : loop[(st + n) % NL];
                if (nb.Curve is not LineCurve3) { why = "law chain: the neighbour at a free end is not a line"; return false; }
                var V = atA ? orig[0].A : orig[n - 1].B;
                var weF = nb.Id?.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().FirstOrDefault(f => byFace[f] != Fp);
                if (weF == null || !byFace[weF].Surf.IsPlanar || byFace[weF].N.Length < 0.5) { why = "law chain: the end face is not planar"; return false; }
                var We = byFace[weF]; var ne = We.N.Normalized();
                double tn = Vector3d.Dot(Tan(k, atA ? 0 : len[k]), ne);
                if (Math.Abs(tn) < 0.1) { why = "law chain: end face too oblique"; return false; }
                double rEnd = Rk(k, atA ? 0 : len[k]);
                double ext = 4 * rEnd / Math.Abs(tn) + 1e-3 * len[k];
                double lo = atA ? -ext : len[k] - ext, hi = atA ? ext : len[k] + ext;
                if (atA) uLo[k] = lo; else uHi[k] = hi;
                for (int q = 0; q <= 32; q++) { double sg = lo + (hi - lo) * q / 32; if (!(Rk(k, sg) > 1e-6) || Sec(k, sg) == null) { why = "law chain: the radius law does not continue past a free end"; return false; } }
                int kk = k; var Vc = V;
                double Root(Func<double, double> g)
                {
                    double a = lo, b = hi, fa = g(a), fb = g(b);
                    if (!(fa * fb < 0)) return double.NaN;
                    for (int it = 0; it < 200 && b - a > 1e-15 * (hi - lo); it++) { double m = (a + b) / 2, fm = g(m); if (fa * fm <= 0) b = m; else { a = m; fa = fm; } }
                    return (a + b) / 2;
                }
                double s1 = Root(sg => Vector3d.Dot(Q1(kk, sg) - Vc, ne)), s2 = Root(sg => Vector3d.Dot(Q2(kk, sg) - Vc, ne));
                if (double.IsNaN(s1) || double.IsNaN(s2)) { why = "law chain: the contacts do not reach the end face"; return false; }
                // Q₁ — внутри соседнего звена Fp
                {
                    var q1 = Q1(k, s1); var a0 = nb.A; var b0 = nb.B; double ln = (b0 - a0).Length;
                    double u = Vector3d.Dot(q1 - a0, b0 - a0) / (ln * ln), dist = (a0 + (b0 - a0) * u - q1).Length;
                    if (dist > 1e-7 * Math.Max(1, ln) || u <= 1e-6 || u >= 1 - 1e-6) { why = "law chain: radius too large for the end neighbour"; return false; }
                }
                var memoT = new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d>();
                Vector3d Trim(double v)
                {
                    if (v <= 0) return Q1(kk, s1);
                    if (v >= 1) return Q2(kk, s2);
                    return memoT.GetOrAdd(v, vv =>
                    {
                        double sv = Root(sg => Vector3d.Dot(Sec(kk, sg)!.Value.Point(vv) - Vc, ne));
                        return double.IsNaN(sv) ? new Vector3d(double.NaN) : Sec(kk, sv)!.Value.Point(vv);
                    });
                }
                for (int q = 1; q < 16; q++) if (double.IsNaN(Trim(q / 16.0).X)) { why = "law chain: the blend does not cross the end face"; return false; }
                var tc = new ProcCurve3(Trim, 0, 1, false, "blend end");
                if (atA) { a1s = s1; a2s = s2; trimA = tc; WeA = We; VA = V; } else { b1e = s1; b2e = s2; trimB = tc; WeB = We; VB = V; }
            }
            Interlocked.Increment(ref LawOpenChains);
        }
        double A1p(int k) => !closedRun && k == 0 ? a1s : mA1[k]; double A2p(int k) => !closedRun && k == 0 ? a2s : 0;
        double B1p(int k) => !closedRun && k == n - 1 ? b1e : mB1[k]; double B2p(int k) => !closedRun && k == n - 1 ? b2e : len[k];

        // ленты: поверхности по формуле сечения на [0, len] (концевые звенья открытой цепочки — с продлением)
        var surf = new SectionSurface[n]; var cQ1 = new ProcCurve3[n]; var cQ2 = new ProcCurve3[n];
        for (int k = 0; k < n; k++)
        {
            int kk = k;
            SweepSection Gen(double sg) => Sec(kk, Math.Clamp(sg, uLo[kk], uHi[kk])) ?? throw new InvalidOperationException("law loop: section");
            // стыки закона внутри звена (узлы — доли F)
            var brk = new List<double>();
            foreach (var f in law.Knots)
                for (int w = -1; w <= 1; w++)
                {
                    double sg = (sStart + dirS * (f + w) * L) - s0[k];
                    if (sg > uLo[k] + 1e-9 * len[k] && sg < uHi[k] - 1e-9 * len[k]) brk.Add(sg);
                }
            if (uLo[k] < 0) brk.Add(0); if (uHi[k] > len[k]) brk.Add(len[k]);       // конец звена — стык продления
            brk = brk.OrderBy(x => x).Aggregate(new List<double>(), (acc, x) => { if (acc.Count == 0 || x - acc[^1] > 1e-9 * len[k]) acc.Add(x); return acc; });
            double span = uHi[k] - uLo[k];
            var us = Enumerable.Range(0, 257).Select(i => uLo[k] + span * i / 256).Concat(brk).Distinct().OrderBy(x => x).ToList();
            surf[k] = new SectionSurface(Gen, us, us.Select(Gen).ToList(), false, uLo[k], uHi[k], brk, span / 32768);
            double worst = 0;
            for (int i = 0; i < 400; i++)
            {
                double U = uLo[k] + span * (i + 0.5) / 400; var tb = surf[k].SectionAt(U); var ex = surf[k].ExactSectionAt(U);
                foreach (double v in new[] { 0.0, 0.5, 1.0 }) worst = Math.Max(worst, (tb.Point(v) - ex.Point(v)).Length);
            }
            if (worst > Math.Max(1e-8, 0.2 * ToleranceContext.Current.ModelAccuracy)) { why = $"law loop: section table {worst:E1}"; return false; }
            cQ1[k] = new ProcCurve3(sg => Q1(kk, sg), A1p(k), B1p(k), false, "rolling-ball contact");
            cQ2[k] = new ProcCurve3(sg => Q2(kk, sg), A2p(k), B2p(k), false, "rolling-ball contact");
        }

        // правка Fp: звено → кривая контакта Q₁ (то же направление обхода)
        for (int k = 0; k < n; k++)
        {
            int at = loop.IndexOf(P[k].S);
            loop[at] = new Seg { Curve = cQ1[k], A = Q1(k, A1p(k)), B = Q1(k, B1p(k)), Id = P[k].S.Id };
        }
        if (!closedRun)
        {
            // соседние невыбранные звенья Fp — до контактов на торцах
            int ia = (st - 1 + NL) % NL, ib = (st + n) % NL;
            var qa = Q1(0, a1s); var qb = Q1(n - 1, b1e);
            Seg? na2, nb2;
            if (ia == ib) { na2 = nb2 = Shorten(loop[ia], qb, qa); }
            else { na2 = Shorten(loop[ia], null, qa); nb2 = Shorten(loop[ib], qb, null); }
            if (na2 == null || nb2 == null) { why = "law chain: an end neighbour cannot be shortened"; return false; }
            loop[ia] = na2; loop[ib] = nb2;
        }
        Fp.Touched = true;
        // стенки: ребро → кривая Q₂; рёбра стыков стенок (вдоль n_p) — укоротить до Q₂ стыка
        int nJ = closedRun ? n : n - 1;
        var jointPts = Enumerable.Range(0, nJ).Select(k => P[k].S.B).ToList();
        var jointQ2 = Enumerable.Range(0, nJ).Select(k => Q2(k, len[k])).ToList();
        var wallHeight = new Dictionary<FDesc, double>();
        for (int k = 0; k < n; k++)
        {
            var W = P[k].W; bool done = false;
            foreach (var l in W.Loops)
                for (int i = 0; i < l.Count && !done; i++)
                    if (l[i].Id == P[k].S.Id && l[i].Orig != null)
                    {
                        int kk = k;
                        Vector3d C2(Vector3d x) => (x - P[kk].S.A).Length < 1e-7 ? Q2(kk, A2p(kk)) : Q2(kk, B2p(kk));
                        l[i] = new Seg { Curve = cQ2[k], A = C2(l[i].A), B = C2(l[i].B), Id = P[k].S.Id };
                        done = true;
                    }
            if (!done) { why = "law loop: the edge is not in its wall's loops"; return false; }
            W.Touched = true;
        }
        foreach (var W in P.Select(p => p.W).Distinct())
            foreach (var l in W.Loops)
                for (int i = 0; i < l.Count; i++)
                {
                    var s = l[i]; if (s.Id == null || selSet.Contains(s.Id)) continue;
                    int ia = jointPts.FindIndex(x => (x - s.A).Length < 1e-7), ib = jointPts.FindIndex(x => (x - s.B).Length < 1e-7);
                    if (ia < 0 && ib < 0) continue;
                    if (ia >= 0 && ib >= 0) { why = "law loop: a wall edge joins two joints"; return false; }
                    var Pj = ia >= 0 ? s.A : s.B; var nP = jointQ2[ia >= 0 ? ia : ib];
                    if (s.Curve is not LineCurve3 || Math.Abs(Math.Abs(Vector3d.Dot((s.B - s.A).Normalized(), np)) - 1) > 1e-9) { why = "law loop: a joint edge of the walls is not along the face normal"; return false; }
                    var far = ia >= 0 ? s.B : s.A; double ln = (far - Pj).Length, u = Vector3d.Dot(nP - Pj, far - Pj) / (ln * ln);
                    if (!(u > 1e-9 && u < 1 - 1e-9)) { why = "law loop: a wall is not taller than the blend"; return false; }
                    var ns = Shorten(s, ia >= 0 ? nP : null, ib >= 0 ? nP : null);
                    if (ns == null) { why = "law loop: a joint edge cannot be shortened"; return false; }
                    l[i] = ns;
                    wallHeight[W] = Math.Min(wallHeight.TryGetValue(W, out var h0) ? h0 : double.MaxValue, ln);
                }
        if (!closedRun)
        {
            // свободные концы: ребро стенки и торца (вдоль n_p в вершине V) — до Q₂; в контур торца — край ленты от Q₁ к Q₂
            foreach (bool atA in new[] { true, false })
            {
                int k = atA ? 0 : n - 1; var V = atA ? VA : VB; var We = atA ? WeA! : WeB!; var q1 = Q1(k, atA ? a1s : b1e); var q2 = Q2(k, atA ? a2s : b2e);
                var tc = atA ? trimA! : trimB!;
                bool doneW = false;
                foreach (var l in P[k].W.Loops)
                    for (int i = 0; i < l.Count && !doneW; i++)
                    {
                        var sw = l[i]; if (sw.Id != null && selSet.Contains(sw.Id)) continue;
                        bool atAv = (sw.A - V).Length < 1e-7, atBv = (sw.B - V).Length < 1e-7;
                        if (!atAv && !atBv) continue;
                        if (sw.Curve is not LineCurve3 || Math.Abs(Math.Abs(Vector3d.Dot((sw.B - sw.A).Normalized(), np)) - 1) > 1e-9) continue;
                        var nsw = Shorten(sw, atAv ? q2 : null, atBv ? q2 : null);
                        if (nsw == null) { why = "law chain: the end wall edge cannot be shortened"; return false; }
                        l[i] = nsw; doneW = true;
                    }
                if (!doneW) { why = "law chain: the end wall edge is not found"; return false; }
                bool doneE = false;
                foreach (var loopE in We.Loops)
                    for (int i = 0; i < loopE.Count && !doneE; i++)
                    {
                        var s0e = loopE[i];
                        if ((s0e.B - V).Length >= 1e-7) continue;
                        var nx = loopE[(i + 1) % loopE.Count];
                        bool sOnFp = DistToCurve(s0e.Curve, q1) < 1e-7;
                        var qa = sOnFp ? q1 : q2; var qb = sOnFp ? q2 : q1;
                        var sNew = Shorten(s0e, null, qa); var nNew = Shorten(nx, qb, null);
                        if (sNew == null || nNew == null) { why = "law chain: end face neighbours cannot be shortened"; return false; }
                        loopE[i] = sNew; loopE[(i + 1) % loopE.Count] = nNew;
                        loopE.Insert(i + 1, new Seg { Curve = tc, A = qa, B = qb });
                        We.Touched = true; doneE = true;
                    }
                if (!doneE) { why = "law chain: the end vertex is not in the end face"; return false; }
            }
        }
        foreach (var k in Enumerable.Range(0, n).Where(k => P[k].Arc != null))
        {
            if (!wallHeight.TryGetValue(P[k].W, out var h)) { why = "law loop: wall height unknown"; return false; }
            var pm = Pt(k, len[k] / 2); double rm = Rk(k, len[k] / 2);
            P[k].W.Hint = Q2(k, len[k] / 2) + np * (sgm * (h - rm) / 2);
        }

        // сечения на концах звеньев; стык — общая дуга или две дуги и луночка шара стыка
        ICurve3 ArcOf(SweepSection sc) => new ArcCurve3(new Frame3(sc.C, sc.E1, sc.E2), sc.R, 0, sc.Phi);
        var endArc = new ICurve3[n]; var startArc = new ICurve3[n];
        for (int k = 0; k < nJ; k++)
        {
            int k2 = (k + 1) % n;
            var sa = Sec(k, len[k])!.Value; var sb = Sec(k2, 0)!.Value;
            double sc = Math.Max(1, Rk(k, len[k]));
            bool same = !mitre[k] && (sa.C - sb.C).Length < 1e-12 * sc && Math.Abs(sa.R - sb.R) < 1e-12 * sc && (sa.E2 - sb.E2).Length < 1e-12 && Math.Abs(sa.Phi - sb.Phi) < 1e-12;
            if (!mitre[k] && (sa.Point(0) - sb.Point(0)).Length > 1e-9 * sc || (sa.Point(1) - sb.Point(1)).Length > 1e-9 * sc) { why = $"law loop: joint sections do not share the contacts (joint {k}: {sa.Point(0)} / {sb.Point(0)}, {sa.Point(1)} / {sb.Point(1)}; r {Rk(k, len[k])} / {Rk(k2, 0)}; P {Pt(k, len[k])} / {Pt(k2, 0)}; n2 {N2(k, Pt(k, len[k]))} / {N2(k2, Pt(k2, 0))}; σ {sgm}; Q1 {Q1(k, len[k])} / {Q1(k2, 0)}; sec C {sa.C} {sa.R} {sa.E1} / {sb.C} {sb.R} {sb.E1})"; return false; }
            var aA = ArcOf(sa);
            if (same || !LuneOn) { endArc[k] = aA; startArc[k2] = same ? aA : ArcOf(sb); continue; }
            var aB = ArcOf(sb);
            // окружность следующего звена позади (против хода): ленты перекрываются — граница объединения шаров идёт по их
            // пересечению («шов» от Q₁ к Q₂), луночки нет. Шов на ленте k: для доли v сечения — s, где точка ленты k
            // ложится на огибающую шаров звена k2: min по s′ (|X − C(s′)| − r(s′)) = 0 (точка вне шаров k2 — больше нуля).
            bool ahead = !mitre[k] && Vector3d.Dot(sb.Point(0.5) - sa.Point(0.5), Tan(k, len[k])) > 0;
            Debug?.Invoke($"law joint {k}→{k2} at {orig[k].B}: r {Rk(k, len[k]):G6}, r′ {dRk(k, len[k]):E2}/{dRk(k2, 0):E2}, {(ahead ? "lune" : "crease")}, mid gap {Vector3d.Dot(sb.Point(0.5) - sa.Point(0.5), Tan(k, len[k])):E2}");
            if (!ahead)
            {
                if (!CreaseOn) { why = "law loop: overlapping sections at a joint"; return false; }
                int ka = k, kb = k2; double ha = 0.45 * Math.Min(len[k], Rk(k, len[k])), hb = 0.45 * Math.Min(len[k2], Rk(k, len[k]));
                if (mitre[k]) { ha = Math.Min(len[k], Math.Max(ha, 1.5 * (len[k] - mB1[k]))); hb = Math.Min(len[k2], Math.Max(hb, 1.5 * mA1[k2])); }
                var cq1 = Q1(ka, mB1[ka]); bool isMitre = mitre[k];
                double Db(Vector3d X)
                {
                    double F(double t) => (X - Cn(kb, t)).Length - Rk(kb, t);
                    // «на ус»: лента соседа продлена назад за стык (отрезок и закон — продолжением): шов — пересечение продлённых лент
                    double lo = isMitre ? -hb : 0, hi = hb; const double g = 0.6180339887498949;
                    double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = F(x1), f2 = F(x2);
                    for (int it = 0; it < 90; it++)
                    {
                        if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = F(x1); }
                        else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = F(x2); }
                    }
                    return Math.Min(Math.Min(f1, f2), Math.Min(F(isMitre ? -hb : 0), F(hb)));
                }
                var memo = new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d>();
                // «на ус»: у вершины T* (v → 0) обе ленты касаются Fp, корень там неопределён — на [0, v₀] кубика через T* и три
                // точки v₀, 2v₀, 3v₀ (шов гладкий)
                const double v0 = 2e-3;
                Vector3d Crease(double v)
                {
                    if (v <= 0) return cq1;
                    if (v >= 1) return Q2(ka, len[ka]);
                    if (isMitre && v < v0)
                    {
                        double x = v / v0; var p1 = CreaseCore(v0); var p2 = CreaseCore(2 * v0); var p3 = CreaseCore(3 * v0);
                        return cq1 * (-(x - 1) * (x - 2) * (x - 3) / 6) + p1 * (x * (x - 2) * (x - 3) / 2) + p2 * (-x * (x - 1) * (x - 3) / 2) + p3 * (x * (x - 1) * (x - 2) / 6);
                    }
                    return CreaseCore(v);
                }
                Vector3d CreaseCore(double v)
                {
                    return memo.GetOrAdd(v, vv =>
                    {
                        double G(double t) => Db(Sec(ka, len[ka] - t)!.Value.Point(vv));
                        double lo = 0, hi = ha, flo = G(lo), fhi = G(hi);
                        if (isMitre)
                        {
                            // «на ус»: сечение стыка — вне шаров соседа; шов — первая перемена знака от стыка назад (дальше — вход ленты
                            // в шары соседа с тыла, не огибающая)
                            // наименьшее G по t (внутри шаров соседа одна впадина) — шов между стыком и ней
                            double ga = 0, gb = ha; const double gr = 0.6180339887498949;
                            double x1 = gb - gr * (gb - ga), x2 = ga + gr * (gb - ga), f1 = G(x1), f2 = G(x2);
                            for (int it = 0; it < 100; it++) { if (f1 < f2) { gb = x2; x2 = x1; f2 = f1; x1 = gb - gr * (gb - ga); f1 = G(x1); } else { ga = x1; x1 = x2; f1 = f2; x2 = ga + gr * (gb - ga); f2 = G(x2); } }
                            double tm = (ga + gb) / 2, gm = G(tm);
                            if (!(gm < 0) || !(G(0) > 0)) { Interlocked.Increment(ref CreaseFallbacks); return new Vector3d(double.NaN); }
                            lo = tm; flo = gm; hi = 0; fhi = G(0);          // G(lo) < 0 < G(hi): как у перекрытия
                        }
                        if (!(flo <= 0 && fhi > 0)) { Interlocked.Increment(ref CreaseFallbacks); return Sec(ka, len[ka])!.Value.Point(vv); }
                        double md = 0; int side = 0;
                        for (int it = 0; it < 200; it++)
                        {
                            md = (lo * fhi - hi * flo) / (fhi - flo); double fm = G(md);
                            if (fm == 0 || Math.Abs(hi - lo) < 1e-15 * ha) break;
                            if (fm < 0) { lo = md; flo = fm; if (side == -1) fhi /= 2; side = -1; }
                            else { hi = md; fhi = fm; if (side == 1) flo /= 2; side = 1; }
                        }
                        return Sec(ka, len[ka] - md)!.Value.Point(vv);
                    });
                }
                // проверка: шов внутри (v ∈ (0, 1)) отходит от сечения стыка, лежит на обеих лентах
                for (int q = 1; q < 16; q++)
                {
                    double v = q / 16.0;
                    if (isMitre) { if (double.IsNaN(Crease(v).X)) { why = "law chain: no crease at a sharp joint"; return false; } continue; }
                    double G0 = Db(sa.Point(v)); if (!(G0 < 0)) { why = "law loop: joint overlap not confirmed"; return false; }
                    if (!(Db(Sec(k, len[k] - ha)!.Value.Point(v)) > 0)) { why = "law loop: joint overlap wider than the search window"; return false; }
                }
                var cr = new ProcCurve3(Crease, 0, 1, false, "blend crease");
                endArc[k] = cr; startArc[k2] = cr;
                Interlocked.Increment(ref LawCreases);
                continue;
            }
            endArc[k] = aA; startArc[k2] = aB;
            // луночка: шар стыка (центр — C стыка, радиус r стыка)
            var Cj = Cn(k, len[k]); double rj = Rk(k, len[k]);
            var q1 = Q1(k, len[k]); var q2 = Q2(k, len[k]);
            // луночка — пучок окружностей шара через Q₁ и Q₂: плоскость сечения поворачивается вокруг прямой Q₁Q₂ от
            // плоскости дуги aA к плоскости aB (все окружности пучка — на шаре стыка). Лента по формуле сечения (как «линза»
            // тройника): сетка и объём — общим путём лент; сфера с тонким клювом в углах сеткой не строилась.
            var ax = (q2 - q1).Normalized();
            var na = Vector3d.Cross(sa.E1, sa.E2).Normalized(); var nb = Vector3d.Cross(sb.E1, sb.E2).Normalized();
            na -= ax * Vector3d.Dot(na, ax); na.Normalize(); nb -= ax * Vector3d.Dot(nb, ax); nb.Normalize();
            if (Vector3d.Dot(na, nb) < 0) nb = -nb;
            double dAng = Math.Atan2(Vector3d.Dot(Vector3d.Cross(na, nb), ax), Vector3d.Dot(na, nb));
            SweepSection Fan(double u)
            {
                double th = dAng * Math.Clamp(u, 0, 1);
                var nu = na * Math.Cos(th) + Vector3d.Cross(ax, na) * Math.Sin(th);
                var cc = Cj - nu * Vector3d.Dot(Cj - q1, nu); double rho = (q1 - cc).Length;
                var e1 = (q1 - cc) / rho; var w = q2 - cc; var e2 = (w - e1 * Vector3d.Dot(w, e1)).Normalized();
                return SweepSection.Arc(cc, e1, e2, rho, Math.Atan2(Vector3d.Dot(w, e2), Vector3d.Dot(w, e1)));
            }
            var fu = Enumerable.Range(0, 65).Select(i => i / 64.0).ToList();
            var fan = new SectionSurface(Fan, fu, fu.Select(Fan).ToList(), false, 0, 1, null, 1.0 / 32768);
            for (int q = 0; q <= 8; q++)
            {
                double v = q / 8.0;
                if ((fan.ExactSectionAt(0).Point(v) - aA.PointAt(v)).Length > 1e-9 * Math.Max(1, rj) || (fan.ExactSectionAt(1).Point(v) - aB.PointAt(v)).Length > 1e-9 * Math.Max(1, rj))
                { why = "law loop: lune fan does not meet the joint sections"; return false; }
            }
            var lh = fan.ExactSectionAt(0.5).Point(0.5); var dir = (lh - Cj).Normalized();
            var lune = new FDesc { Surf = fan, Rev = Vector3d.Dot(fan.NormalAt(fan.ProjectToUV(lh)), dir * -sgm) < 0, Tag = "round:face", Touched = true, Hint = lh };
            lune.Loops.Add(new List<Seg> { new() { Curve = aA, A = q1, B = q2 }, new() { Curve = aB, A = q2, B = q1 } });
            blends.Add(lune);
            Interlocked.Increment(ref LawLunes);
        }
        for (int k = 0; k < n; k++)
        {
            var A1 = Q1(k, A1p(k)); var B1 = Q1(k, B1p(k)); var A2 = Q2(k, A2p(k)); var B2 = Q2(k, B2p(k));
            ICurve3 sEdge = !closedRun && k == 0 ? trimA! : startArc[k], eEdge = !closedRun && k == n - 1 ? trimB! : endArc[k];
            var loopB = new List<Seg>
            {
                new() { Curve = cQ1[k], A = B1, B = A1 },
                new() { Curve = sEdge, A = A1, B = A2 },
                new() { Curve = cQ2[k], A = A2, B = B2 },
                new() { Curve = eEdge, A = B2, B = B1 },
            };
            double sm = len[k] / 2; var mid = surf[k].ExactSectionAt(sm).Point(0.5);
            var want = (mid - Cn(k, sm)).Normalized() * -sgm;          // наружу из материала: у выпуклого (σ = −1) — от центра шара
            var bl = new FDesc { Surf = surf[k], Tag = "round:face", Touched = true, Hint = mid };
            bl.Rev = Vector3d.Dot(surf[k].NormalAt(surf[k].ProjectToUV(mid)), want) < 0;
            bl.Loops.Add(loopB);
            blends.Add(bl);
        }
        Interlocked.Increment(ref LawLoops);
        return true;
    }

    /// <summary>
    /// KRN-06 (06.2): ЗАКОН РАДИУСА НА ЦЕПОЧКЕ РЁБЕР — касательный контур плоской грани (TangentLoopLawBlends); доля F — по
    /// длине контура от начала первой ссылки в сторону её конца.
    /// </summary>
    static TSolid? BuildLawLoop(TSolid body, IReadOnlyList<EdgeRef> refs, IReadOnlyList<(double F, double R)> points, ref string? why)
    {
        var sel = new List<TEdge>();
        var whole = FilletOp.WholeNamedEdges(new List<TSolid> { body }, refs);
        foreach (var rf in refs)
        {
            var e = FilletOp.Resolve(new List<TSolid> { body }, rf, out _) ?? (whole.TryGetValue(rf, out var we) ? we : null);
            if (e == null) { why = "edge not resolved"; return null; }
            if (!sel.Contains(e)) sel.Add(e);
        }
        var law = RadiusLaw.Points(refs[0].Start, refs[0].End, points);
        if (law == null) { why = "radius law points (need σ = 0 and 1, increasing)"; return null; }
        // начало и следующая вершина — концы первого ребра со стороны Start
        var e0 = sel[0];
        bool startAtV0 = (e0.V0.P - refs[0].Start).Length <= (e0.V1.P - refs[0].Start).Length;
        var st = startAtV0 ? e0.V0.P : e0.V1.P; var nx = startAtV0 ? e0.V1.P : e0.V0.P;
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;
        var blends = new List<FDesc>();
        if (!TangentLoopLawBlends(sel, byFace, law, st, nx, blends, ref why)) return null;
        return FinishDirect(faces, blends, "round.direct", ref why);
    }
}
