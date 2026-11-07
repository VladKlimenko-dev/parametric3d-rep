using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// KRN-07 (07.4): НАПРАВЛЯЮЩИЕ КРИВЫЕ ЛОФТА — вспомогательное. Направляющая — отрезок, кривая Безье или дуга короче 180°,
/// в однородных координатах — один кусок Безье (дуга — рациональный квадратичный). Направляющая проходит через вершину
/// каждого сечения (одну и ту же по сопоставлению); между соседними сечениями грань звена — поверхность Кунса в однородных
/// координатах по двум сечениям и двум направляющим (у вершины без направляющей — отрезок). Края такой поверхности —
/// ровно сечения и направляющие (угловые точки с весом 1), поверхность — NURBS (точно).
/// </summary>
public static partial class DirectRound
{
    static readonly bool LoftGuidesOff = Environment.GetEnvironmentVariable("P3D_LOFTGUIDES") == "0";
    const double GuideTol = 1e-7;

    /// <summary>
    /// k178: НАПРАВЛЯЮЩАЯ ИЗ НЕСКОЛЬКИХ КУСКОВ — цепочка однородных кусков Безье на параметре [0, 1] (B — границы кусков).
    /// Сплайн (кривая через N точек — N − 1 кубических кусков) раскладывается вставкой узлов до кратности степени.
    /// Мутация P3D_LOFTGUIDEPIECES=0 — только один кусок (прежнее).
    /// </summary>
    internal sealed class GuidePw
    {
        public List<Vector4d[]> P = new();
        public double[] B = { 0, 1 };
        public int Count => P.Count;
        public GuidePw Reversed()
        {
            var r = new GuidePw { P = Enumerable.Reverse(P).Select(q => Enumerable.Reverse(q).ToArray()).ToList() };
            r.B = Enumerable.Reverse(B).Select(b => 1 - b).ToArray();
            return r;
        }
        /// <summary>Кусок и местный параметр для параметра t.</summary>
        public (int I, double U) Local(double t)
        {
            int i = 0; while (i < P.Count - 1 && t > B[i + 1]) i++;
            double d = B[i + 1] - B[i];
            return (i, d > 0 ? Math.Clamp((t - B[i]) / d, 0, 1) : 0);
        }
        public Vector3d Pt(double t) { var (i, u) = Local(t); return Pt4(P[i], u); }
    }
    static readonly bool GuidePiecesOff = Environment.GetEnvironmentVariable("P3D_LOFTGUIDEPIECES") == "0";

    static GuidePw? GuideBezPw(ICurve3 c)
    {
        if (GuideBez(c) is { } q) return new GuidePw { P = { q } };
        if (GuidePiecesOff || c is not SplineCurve3 sp) return null;
        // вставка узлов до кратности степени во всех внутренних узлах
        var s = sp;
        int p = s.Degree;
        double lo = s.Knots[p], hi = s.Knots[s.Knots.Count - p - 1];
        if (!(hi > lo)) return null;
        for (int guard = 0; guard < 4096; guard++)
        {
            var kn = s.Knots; bool done = true;
            for (int i = p + 1; i < kn.Count - p - 1; i++)
            {
                double k = kn[i]; if (k <= lo + 1e-14 || k >= hi - 1e-14) continue;
                int mult = kn.Count(x => Math.Abs(x - k) <= 1e-14 * Math.Max(1, Math.Abs(k)));
                if (mult < p) { s = s.InsertKnot(k); done = false; break; }
            }
            if (done) break;
        }
        var cp = s.ControlPoints; var w = s.Weights;
        if ((cp.Count - 1) % p != 0) return null;
        int n = (cp.Count - 1) / p;
        var g = new GuidePw { P = new() };
        var br = new List<double> { 0 };
        var distinct = s.Knots.Where(k => k > lo + 1e-14 && k < hi - 1e-14).Distinct().OrderBy(k => k).ToList();
        if (distinct.Count != n - 1) return null;
        foreach (var k in distinct) br.Add((k - lo) / (hi - lo));
        br.Add(1);
        for (int i = 0; i < n; i++) g.P.Add(Enumerable.Range(i * p, p + 1).Select(j => new Vector4d(cp[j] * w[j], w[j])).ToArray());
        g.B = br.ToArray();
        return g;
    }

    /// <summary>Однородная кривая Безье на ступень выше (та же кривая).</summary>
    static Vector4d[] ElevateG(Vector4d[] q)
    {
        int n = q.Length - 1; var r = new Vector4d[n + 2]; r[0] = q[0]; r[n + 1] = q[n];
        for (int i = 1; i <= n; i++) { double a = (double)i / (n + 1); r[i] = q[i - 1] * a + q[i] * (1 - a); }
        return r;
    }

    /// <summary>
    /// Участок направляющей [t0, t1] — кускИ на местном параметре [0, 1] (границы — b). У одного рационального куска — прежняя
    /// нормировка весов концов; у нескольких — веса не трогаются (кривая через точки — веса 1).
    /// </summary>
    static (List<Vector4d[]> Q, double[] B) SubPieces(GuidePw g, double t0, double t1)
    {
        if (g.Count == 1) return (new() { SubPiece(g.P[0], t0, t1) }, new[] { 0.0, 1.0 });
        var res = new List<Vector4d[]>(); var bs = new List<double> { 0 };
        double L = t1 - t0;
        for (int i = 0; i < g.Count; i++)
        {
            double a = Math.Max(t0, g.B[i]), b = Math.Min(t1, g.B[i + 1]);
            if (!(b > a + 1e-13 * Math.Max(1, L))) continue;
            double d = g.B[i + 1] - g.B[i];
            double ua = (a - g.B[i]) / d, ub = (b - g.B[i]) / d;
            var q = g.P[i];
            if (ua > 1e-15) q = SplitAt(q, ua).R;
            double s = (ub - ua) / (1 - ua);
            if (s < 1 - 1e-15) q = SplitAt(q, s).L;
            res.Add((Vector4d[])q.Clone()); bs.Add((b - t0) / L);
        }
        bs[^1] = 1;
        return (res, bs.ToArray());
    }

    /// <summary>Куски на границах u (общее разбиение двух сторон грани): разрезаются в недостающих границах.</summary>
    static List<Vector4d[]> Refine(List<Vector4d[]> q, double[] b, List<double> u)
    {
        var res = new List<Vector4d[]>();
        for (int k = 0; k + 1 < u.Count; k++)
        {
            double a = u[k], c = u[k + 1], m = (a + c) / 2;
            int i = 0; while (i < q.Count - 1 && m > b[i + 1]) i++;
            double d = b[i + 1] - b[i], ua = (a - b[i]) / d, ub = (c - b[i]) / d;
            var r = q[i];
            if (ua > 1e-15) r = SplitAt(r, ua).R;
            double s = (ub - ua) / (1 - ua);
            if (s < 1 - 1e-15) r = SplitAt(r, s).L;
            res.Add(r);
        }
        return res;
    }

    /// <summary>
    /// k179: направляющая, перенесённая аффинно: G′(t) = L·G(t) + c + d·t на участке [t0, t1] исходной (параметр участка — [0, 1]).
    /// Многочлен — те же степени (c + d·t — степень 1, поднятая); рациональный кусок — степень на 1 выше (произведение на вес).
    /// </summary>
    static GuidePw TransformGuide(GuidePw g, Func<Vector3d, Vector3d> L, Vector3d c, Vector3d d, double t0, double t1)
    {
        var (subs, bs) = SubPieces(g, t0, t1);
        var r = new GuidePw { P = new(), B = (double[])bs.Clone() };
        for (int i = 0; i < subs.Count; i++)
        {
            var q = subs[i]; int D = q.Length - 1;
            // параметр исходной на куске: t = t0 + (t1 − t0)·(b_i + (b_i+1 − b_i)·s)
            double ta = t0 + (t1 - t0) * bs[i], tb = t0 + (t1 - t0) * bs[i + 1];
            // c + d·t в начале и в конце куска (в долях участка: t в [0, 1] участка — как у хорды A→B)
            double ua = (ta - t0) / (t1 - t0), ub = (tb - t0) / (t1 - t0);
            Vector3d e0 = c + d * ua, e1 = c + d * ub;
            bool poly = q.All(x => Math.Abs(x.W - 1) < 1e-15);
            if (poly)
                r.P.Add(Enumerable.Range(0, D + 1).Select(k => new Vector4d(L(q[k].Xyz) + e0 + (e1 - e0) * ((double)k / D), 1)).ToArray());
            else
            {
                // числитель: L(w·X) (степень D → D+1) + e(s)·w(s) (1 × D → D+1); вес: w (D → D+1)
                var lx = ElevateG(q.Select(x => new Vector4d(L(x.Xyz), x.W)).ToArray());
                var res = new Vector4d[D + 2];
                for (int k = 0; k <= D + 1; k++)
                {
                    var ew = Vector3d.Zero;
                    for (int a = 0; a <= 1; a++)
                    {
                        int b = k - a; if (b < 0 || b > D) continue;
                        double coef = Binom(1, a) * Binom(D, b) / Binom(D + 1, k);
                        ew += (a == 0 ? e0 : e1) * (coef * q[b].W);
                    }
                    res[k] = new Vector4d(lx[k].Xyz + ew, lx[k].W);
                }
                r.P.Add(res);
            }
        }
        return r;
    }
    static double Binom(int n, int k) { double r = 1; for (int i = 1; i <= k; i++) r = r * (n - k + i) / i; return r; }

    /// <summary>k179: цепочка кусков — B-сплайн (кратность внутренних узлов = степень).</summary>
    static SplineCurve3 PwToSpline(GuidePw g)
    {
        int p = g.P.Max(x => x.Length - 1);
        var pcs = g.P.Select(q => { while (q.Length - 1 < p) q = ElevateG(q); return q; }).ToList();
        var cp = new List<Vector3d>(); var w = new List<double>();
        for (int i = 0; i < pcs.Count; i++)
            for (int j = i == 0 ? 0 : 1; j <= p; j++) { cp.Add(pcs[i][j].Xyz / pcs[i][j].W); w.Add(pcs[i][j].W); }
        var kn = new List<double>(); for (int i = 0; i <= p; i++) kn.Add(0);
        for (int i = 1; i < g.B.Length - 1; i++) for (int k = 0; k < p; k++) kn.Add(g.B[i]);
        for (int i = 0; i <= p; i++) kn.Add(1);
        return new SplineCurve3(cp, p, w.All(x => Math.Abs(x - 1) < 1e-15) ? null : w, kn);
    }

    static (double T, double D) ClosestT(GuidePw g, Vector3d p)
    {
        double bt = 0, bd = double.MaxValue;
        for (int i = 0; i < g.Count; i++)
        {
            var (t, d) = ClosestT(g.P[i], p);
            if (d < bd) { bd = d; bt = g.B[i] + (g.B[i + 1] - g.B[i]) * t; }
        }
        return (bt, bd);
    }

    /// <summary>Направляющая — один однородный кусок Безье; null — вид не поддержан.</summary>
    static Vector4d[]? GuideBez(ICurve3 c)
    {
        switch (c)
        {
            case LineCurve3 l: return new[] { new Vector4d(l.A, 1), new Vector4d(l.B, 1) };
            case BezierCurve3 bz: return bz.P.Select(q => new Vector4d(q, 1)).ToArray();
            case ArcCurve3 a when Math.Abs(a.A1 - a.A0) < Math.PI - 1e-6:
            {
                double t0 = a.A0, t1 = a.A1, tm = (t0 + t1) / 2, half = Math.Abs(t1 - t0) / 2;
                var P0 = a.Frame.ToWorld(Math.Cos(t0) * a.Radius, Math.Sin(t0) * a.Radius);
                var P2 = a.Frame.ToWorld(Math.Cos(t1) * a.Radius, Math.Sin(t1) * a.Radius);
                double rr = a.Radius / Math.Cos(half), w1 = Math.Cos(half);
                var P1 = a.Frame.ToWorld(Math.Cos(tm) * rr, Math.Sin(tm) * rr);
                return new[] { new Vector4d(P0, 1), new Vector4d(P1 * w1, w1), new Vector4d(P2, 1) };
            }
        }
        return null;
    }

    static Vector4d Eval4(Vector4d[] q, double t)
    {
        var b = (Vector4d[])q.Clone();
        for (int r = 1; r < b.Length; r++) for (int i = 0; i < b.Length - r; i++) b[i] = b[i] * (1 - t) + b[i + 1] * t;
        return b[0];
    }
    static Vector3d Pt4(Vector4d[] q, double t) { var h = Eval4(q, t); return h.Xyz / h.W; }

    static (Vector4d[] L, Vector4d[] R) SplitAt(Vector4d[] q, double t)
    {
        int n = q.Length - 1; var b = (Vector4d[])q.Clone(); var L = new Vector4d[n + 1]; var R = new Vector4d[n + 1];
        L[0] = b[0]; R[n] = b[n];
        for (int r = 1; r <= n; r++) { for (int i = 0; i <= n - r; i++) b[i] = b[i] * (1 - t) + b[i + 1] * t; L[r] = b[0]; R[n - r] = b[n - r]; }
        return (L, R);
    }

    /// <summary>Кусок [t0, t1] в стандартном виде (веса концов — 1; перепараметризация Мёбиуса не меняет кривой).</summary>
    static Vector4d[] SubPiece(Vector4d[] q, double t0, double t1)
    {
        var r = t0 > 0 ? SplitAt(q, t0).R : (Vector4d[])q.Clone();
        double s = (t1 - t0) / (1 - t0);
        if (s < 1) r = SplitAt(r, s).L;
        int n = r.Length - 1; double w0 = r[0].W, wn = r[n].W;
        double rho = Math.Pow(w0 / wn, 1.0 / n);
        for (int i = 0; i <= n; i++) r[i] *= Math.Pow(rho, i) / w0;
        return r;
    }

    /// <summary>Параметр ближайшей точки направляющей и расстояние до неё.</summary>
    static (double T, double D) ClosestT(Vector4d[] q, Vector3d p)
    {
        const int M = 512; double bt = 0, bd = double.MaxValue;
        for (int i = 0; i <= M; i++) { double t = (double)i / M; double d = (Pt4(q, t) - p).Length; if (d < bd) { bd = d; bt = t; } }
        double lo = Math.Max(0, bt - 1.0 / M), hi = Math.Min(1, bt + 1.0 / M);
        for (int it = 0; it < 100; it++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if ((Pt4(q, m1) - p).Length < (Pt4(q, m2) - p).Length) hi = m2; else lo = m1;
        }
        double tt = (lo + hi) / 2; double dd = (Pt4(q, tt) - p).Length;
        return dd < bd ? (tt, dd) : (bt, bd);
    }

    /// <summary>Сечение-окружность делится на дуги в точках, где его плоскость пересекают направляющие. null — направляющая
    /// не пересекает окружность (причина в why).</summary>
    static List<ICurve3>? SplitCircleAtGuides(ArcCurve3 a, List<GuidePw> g4, ref string? why)
    {
        var U = a.Frame.U.Normalized(); var V = a.Frame.V.Normalized(); var W = Vector3d.Cross(U, V).Normalized(); var O = a.Frame.Origin;
        var ang = new List<double>();
        foreach (var q in g4)
        {
            double F(double t) => Vector3d.Dot(q.Pt(t) - O, W);
            int M = 512 * Math.Max(1, q.Count); double? root = null;
            for (int i = 0; i <= M && root == null; i++)
            {
                double t = (double)i / M; if (Math.Abs(F(t)) < 1e-12) { root = t; break; }
                if (i < M && F(t) * F((double)(i + 1) / M) < 0)
                {
                    double lo = t, hi = (double)(i + 1) / M;
                    for (int it = 0; it < 200; it++) { double m = (lo + hi) / 2; if (F(lo) * F(m) <= 0) hi = m; else lo = m; }
                    root = (lo + hi) / 2;
                }
            }
            if (root == null) { why = "loft: a guide does not cross the plane of a circular section"; return null; }
            var p = q.Pt(root.Value) - O;
            if (Math.Abs(p.Length - a.Radius) > GuideTol * 10) { why = $"loft: a guide misses a circular section by {Math.Abs(p.Length - a.Radius):E1} mm"; return null; }
            ang.Add(Math.Atan2(Vector3d.Dot(p, V), Vector3d.Dot(p, U)));
        }
        ang.Sort();
        for (int i = 1; i < ang.Count; i++) if (ang[i] - ang[i - 1] < 1e-9) { why = "loft: two guides cross a circular section at one point"; return null; }
        var fr = new Frame3(O, U, V);
        var res = new List<ICurve3>();
        for (int i = 0; i < ang.Count; i++)
        {
            double a0 = ang[i], a1 = i + 1 < ang.Count ? ang[i + 1] : ang[0] + 2 * Math.PI;
            res.Add(new ArcCurve3(fr, a.Radius, a0, a1));
        }
        return res;
    }

    /// <summary>Край грани по куску направляющей между сечениями: кривая того же вида с концами A, B.</summary>
    static ICurve3 GuideEdge(ICurve3 src, List<Vector4d[]> subs, double[] b, Vector3d A, Vector3d B)
    {
        if (subs.Count == 1) return GuideEdge(src, subs[0], A, B);
        // несколько кусков — B-сплайн (кратность внутренних узлов = степень), концы — вершины сечений
        int p = subs.Max(x => x.Length - 1);
        var pcs = subs.Select(q => { while (q.Length - 1 < p) q = ElevateG(q); return q; }).ToList();
        var cp = new List<Vector3d>(); var w = new List<double>();
        for (int i = 0; i < pcs.Count; i++)
            for (int j = i == 0 ? 0 : 1; j <= p; j++) { cp.Add(pcs[i][j].Xyz / pcs[i][j].W); w.Add(pcs[i][j].W); }
        cp[0] = A; cp[^1] = B;
        var kn = new List<double>(); for (int i = 0; i <= p; i++) kn.Add(0);
        for (int i = 1; i < b.Length - 1; i++) for (int k = 0; k < p; k++) kn.Add(b[i]);
        for (int i = 0; i <= p; i++) kn.Add(1);
        return new SplineCurve3(cp, p, w.All(x => Math.Abs(x - 1) < 1e-15) ? null : w, kn);
    }

    static ICurve3 GuideEdge(ICurve3 src, Vector4d[] sub, Vector3d A, Vector3d B)
    {
        // k179: рациональный кусок сплайна (перенесённая направляющая дуги) — рациональная кривая Безье, не многочлен
        if (src is SplineCurve3 or BezierCurve3 && sub.Any(x => Math.Abs(x.W - 1) > 1e-15))
        {
            var cp = sub.Select(x => x.Xyz / x.W).ToList(); cp[0] = A; cp[^1] = B;
            int p = sub.Length - 1;
            return new SplineCurve3(cp, p, sub.Select(x => x.W).ToList(), Enumerable.Repeat(0.0, p + 1).Concat(Enumerable.Repeat(1.0, p + 1)).ToList());
        }
        switch (src)
        {
            case SplineCurve3: return new BezierCurve3(sub.Select(x => x.Xyz / x.W));
            case BezierCurve3: return new BezierCurve3(sub.Select(x => x.Xyz / x.W));
            case ArcCurve3 a:
            {
                var U = a.Frame.U.Normalized(); var V = a.Frame.V.Normalized(); var O = a.Frame.Origin;
                double Th(Vector3d p) { var d = p - O; return Math.Atan2(Vector3d.Dot(d, V), Vector3d.Dot(d, U)); }
                double ta = Th(A), d0 = Math.IEEERemainder(Th(B) - ta, 2 * Math.PI);
                return new ArcCurve3(new Frame3(O, U, V), a.Radius, ta, ta + d0);
            }
            default: return new LineCurve3(A, B);
        }
    }
}
