using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// 04.2: КУСОК РАЦИОНАЛЬНОЙ КРИВОЙ БЕЗЬЕ — для строгих оценок событий: однородные опорные точки (x·w, y·w, z·w, w), w &gt; 0 —
/// кривая в выпуклой оболочке своих точек; производная — по годографу однородного многочлена (центрированная форма).
/// Отрезок — степень 1; дуга и эллипс — рациональные квадратичные куски не больше 90°; сплайн и кривая Безье — куски Безье.
/// Параметр s ∈ [0, 1] куска; у отрезка, сплайна и кривой Безье ему линейно отвечает параметр кривой [T0, T1].
/// </summary>
public sealed class RBezierCurve
{
    public readonly Vector4d[] Pw;
    public readonly double T0, T1;
    public readonly bool LinearParam;
    /// <summary>Пределы этого куска в параметре исходного куска (у подкуска — [a, b]): производные — на единицу исходного s.</summary>
    public readonly double S0 = 0, S1 = 1;
    public RBezierCurve(Vector4d[] pw, double t0, double t1, bool linear) { Pw = pw; T0 = t0; T1 = t1; LinearParam = linear; }
    RBezierCurve(Vector4d[] pw, double t0, double t1, bool linear, double s0, double s1) : this(pw, t0, t1, linear) { S0 = s0; S1 = s1; }

    static Vector4d H(Vector3d p, double w) => new(p.X * w, p.Y * w, p.Z * w, w);

    /// <summary>Куски кривой; null — тип не представим (кривая пересечения, процедурная, ломаная).</summary>
    public static List<RBezierCurve>? Of(ICurve3 c)
    {
        switch (c)
        {
            case LineCurve3 l: return new() { new(new[] { H(l.A, 1), H(l.B, 1) }, 0, 1, true) };
            case BezierCurve3 b: return new() { new(b.P.Select(p => H(p, 1)).ToArray(), 0, 1, true) };
            case SplineCurve3 sp:
            {
                var res = new List<RBezierCurve>();
                foreach (var (piece, from, to) in sp.WithUnitKnots().ToBezierSegments())
                {
                    if (piece.ControlPoints.Count != piece.Degree + 1) return null;
                    res.Add(new(piece.ControlPoints.Select((p, i) => H(p, piece.Weights[i])).ToArray(), from, to, true));
                }
                return res;
            }
            case ArcCurve3 a: return Conic(a.Frame, a.Radius, a.Radius, a.A0, a.A1);
            case EllipseCurve3 e: return Conic(e.Frame, e.RU, e.RV, e.A0, e.A1);
        }
        return null;
    }

    /// <summary>Дуга эллипса (окружности) куски не больше 90°: P0, P1 = точка в ½ угла / cos(½Δ), P2; веса 1, cos(½Δ), 1.</summary>
    static List<RBezierCurve> Conic(Frame3 f, double ru, double rv, double a0, double a1)
    {
        double sw = a1 - a0; int m = Math.Max(1, (int)Math.Ceiling(Math.Abs(sw) / (Math.PI / 2) - 1e-12));
        var res = new List<RBezierCurve>();
        for (int k = 0; k < m; k++)
        {
            double b0 = a0 + sw * k / m, b1 = a0 + sw * (k + 1) / m, h = (b1 - b0) / 2, bm = (b0 + b1) / 2, cw = Math.Cos(h);
            Vector3d P(double ang, double s) => f.ToWorld(Math.Cos(ang) * ru * s, Math.Sin(ang) * rv * s);
            res.Add(new(new[] { H(P(b0, 1), 1), H(P(bm, 1 / cw), cw), H(P(b1, 1), 1) }, (double)k / m, (double)(k + 1) / m, false));
        }
        return res;
    }

    public Vector3d Point(double s)
    {
        var t = (Vector4d[])Pw.Clone(); int n = t.Length;
        for (int r = 1; r < n; r++) for (int i = 0; i < n - r; i++) t[i] = t[i] * (1 - s) + t[i + 1] * s;
        return new Vector3d(t[0].X / t[0].W, t[0].Y / t[0].W, t[0].Z / t[0].W);
    }

    /// <summary>Тот же многочлен над [a, b] (полярные формы).</summary>
    public RBezierCurve Sub(double a, double b)
    {
        int n = Pw.Length - 1; var res = new Vector4d[n + 1]; var tmp = new Vector4d[n + 1];
        for (int i = 0; i <= n; i++)
        {
            Array.Copy(Pw, tmp, n + 1);
            for (int r = 0; r < n; r++) { double s = r < i ? b : a; for (int k = 0; k < n - r; k++) tmp[k] = tmp[k] * (1 - s) + tmp[k + 1] * s; }
            res[i] = tmp[0];
        }
        return new RBezierCurve(res, T0 + (T1 - T0) * a, T0 + (T1 - T0) * b, LinearParam, S0 + (S1 - S0) * a, S0 + (S1 - S0) * b);
    }

    public (Vector3d Lo, Vector3d Hi) Box()
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        foreach (var q in Pw) { var p = new Vector3d(q.X / q.W, q.Y / q.W, q.Z / q.W); lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
        return (lo, hi);
    }

    /// <summary>Строгие габарит C и интервалы C' (на единицу s): C' = ((P − c·w)' − (C − c)·w')/w; null — вес ≤ 0.</summary>
    public (Vector3d Lo, Vector3d Hi, Vector3d DLo, Vector3d DHi)? DerivBounds()
    {
        double wmin = double.MaxValue, wmax = double.MinValue;
        foreach (var q in Pw) { wmin = Math.Min(wmin, q.W); wmax = Math.Max(wmax, q.W); }
        if (!(wmin > 0)) return null;
        var (lo, hi) = Box(); var cen = (lo + hi) / 2; var half = (hi - lo) / 2;
        int n = Pw.Length - 1;
        var dlo = new Vector4d(double.MaxValue); var dhi = new Vector4d(double.MinValue);
        if (n == 0) dlo = dhi = Vector4d.Zero;
        for (int i = 0; i < n; i++)
        {
            Vector4d C(int j) => new(Pw[j].X - cen.X * Pw[j].W, Pw[j].Y - cen.Y * Pw[j].W, Pw[j].Z - cen.Z * Pw[j].W, Pw[j].W);
            var d = (C(i + 1) - C(i)) * (n / (S1 - S0));
            dlo = Vector4d.ComponentMin(dlo, d); dhi = Vector4d.ComponentMax(dhi, d);
        }
        double wd = Math.Max(Math.Abs(dlo.W), Math.Abs(dhi.W));
        var rl = new Vector3d(); var rh = new Vector3d();
        for (int k = 0; k < 3; k++)
        {
            double nl = dlo[k] - half[k] * wd, nh = dhi[k] + half[k] * wd;
            double q1 = nl / wmin, q2 = nl / wmax, q3 = nh / wmin, q4 = nh / wmax;
            rl[k] = Math.Min(Math.Min(q1, q2), Math.Min(q3, q4)); rh[k] = Math.Max(Math.Max(q1, q2), Math.Max(q3, q4));
        }
        return (lo, hi, rl, rh);
    }

    /// <summary>Строгий интервал g·C' по куску — скалярный многочлен g·(P − c·w), без покомпонентной обёртки.</summary>
    public (double Lo, double Hi)? DirBound(Vector3d g)
    {
        double wmin = double.MaxValue, wmax = double.MinValue;
        foreach (var q in Pw) { wmin = Math.Min(wmin, q.W); wmax = Math.Max(wmax, q.W); }
        if (!(wmin > 0)) return null;
        var (lo, hi) = Box(); var cen = (lo + hi) / 2; var half = (hi - lo) / 2;
        int n = Pw.Length - 1; if (n == 0) return (0, 0);
        double N(int j) => g.X * (Pw[j].X - cen.X * Pw[j].W) + g.Y * (Pw[j].Y - cen.Y * Pw[j].W) + g.Z * (Pw[j].Z - cen.Z * Pw[j].W);
        double nl = double.MaxValue, nh = double.MinValue, wl = double.MaxValue, wh = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double dn = (N(i + 1) - N(i)) * (n / (S1 - S0)), dw = (Pw[i + 1].W - Pw[i].W) * (n / (S1 - S0));
            nl = Math.Min(nl, dn); nh = Math.Max(nh, dn); wl = Math.Min(wl, dw); wh = Math.Max(wh, dw);
        }
        double gs = Math.Abs(g.X) * half.X + Math.Abs(g.Y) * half.Y + Math.Abs(g.Z) * half.Z, wd = Math.Max(Math.Abs(wl), Math.Abs(wh));
        double a = nl - gs * wd, b = nh + gs * wd;
        double q1 = a / wmin, q2 = a / wmax, q3 = b / wmin, q4 = b / wmax;
        return (Math.Min(Math.Min(q1, q2), Math.Min(q3, q4)), Math.Max(Math.Max(q1, q2), Math.Max(q3, q4)));
    }
}
