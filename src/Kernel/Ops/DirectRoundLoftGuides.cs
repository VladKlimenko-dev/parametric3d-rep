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
    static List<ICurve3>? SplitCircleAtGuides(ArcCurve3 a, List<Vector4d[]> g4, ref string? why)
    {
        var U = a.Frame.U.Normalized(); var V = a.Frame.V.Normalized(); var W = Vector3d.Cross(U, V).Normalized(); var O = a.Frame.Origin;
        var ang = new List<double>();
        foreach (var q in g4)
        {
            double F(double t) => Vector3d.Dot(Pt4(q, t) - O, W);
            const int M = 512; double? root = null;
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
            var p = Pt4(q, root.Value) - O;
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
    static ICurve3 GuideEdge(ICurve3 src, Vector4d[] sub, Vector3d A, Vector3d B)
    {
        switch (src)
        {
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
