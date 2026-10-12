using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Geometry;

/// <summary>Интервал [Lo, Hi].</summary>
public readonly record struct Iv(double Lo, double Hi)
{
    public static Iv Of(double a, double b) => a <= b ? new Iv(a, b) : new Iv(b, a);
    public static Iv Pt(double a) => new(a, a);
    public double Mid => (Lo + Hi) / 2;
    public double Rad => (Hi - Lo) / 2;
    public double Mag => Math.Max(Math.Abs(Lo), Math.Abs(Hi));
    public static Iv operator +(Iv a, Iv b) => new(a.Lo + b.Lo, a.Hi + b.Hi);
    public static Iv operator -(Iv a, Iv b) => new(a.Lo - b.Hi, a.Hi - b.Lo);
    public static Iv operator -(Iv a) => new(-a.Hi, -a.Lo);
    public static Iv operator *(Iv a, double k) => k >= 0 ? new(a.Lo * k, a.Hi * k) : new(a.Hi * k, a.Lo * k);
    public static Iv operator *(double k, Iv a) => a * k;
    public static Iv operator *(Iv a, Iv b)
    {
        double p = a.Lo * b.Lo, q = a.Lo * b.Hi, r = a.Hi * b.Lo, s = a.Hi * b.Hi;
        return new(Math.Min(Math.Min(p, q), Math.Min(r, s)), Math.Max(Math.Max(p, q), Math.Max(r, s)));
    }
    public Iv Widen(double e) => new(Lo - e, Hi + e);
}

/// <summary>
/// 04.2: СТРОГОЕ ОТДЕЛЕНИЕ КОРНЕЙ системы F(x) = 0, x ∈ ℝⁿ (n ≤ 6), ПО КРАВЧИКУ. Для ящика X с центром x̃, K = (середина J(X))⁻¹:
/// K(X) = x̃ − K F(x̃) + (I − K J(X))(X − x̃). K(X) внутри X — в X ровно один корень (его даёт Ньютон из x̃); K(X) ∩ X = ∅ —
/// корней нет; иначе ящик делится по наибольшей (в масштабе) стороне. Ящик меньше предела без решения — не доказано.
/// J(X) — строгие интервалы производных по ящику (поставляет задача), Excl — быстрое исключение (габариты врозь).
/// </summary>
public sealed class Krawczyk
{
    public int N;
    public Func<double[], double[]> F = null!;
    /// <summary>Строгие интервалы якобиана по ящику [lo, hi]; null — оценить нельзя (ящик делится).</summary>
    public Func<double[], double[], Iv[,]?> J = null!;
    public Func<double[], double[], bool>? Excl;
    /// <summary>Масштаб переменных (мм на единицу): деление по наибольшей стороне в мм, предел — в мм.</summary>
    public double[] Scale = null!;
    public double MinSize = 1e-9;
    public int Budget = 200000;
    /// <summary>Сколько ящиков осталось нерешёнными (касание или кратный корень).</summary>
    public int Undecided;
    public string? Why;
    [ThreadStatic] public static Action<string>? Dbg;

    public List<double[]>? Solve(IEnumerable<(double[] Lo, double[] Hi)> boxes)
    {
        var roots = new List<double[]>(); var stack = new Stack<(double[] Lo, double[] Hi, int Depth)>();
        foreach (var (lo, hi) in boxes) stack.Push((lo, hi, 0));
        int n = N, visited = 0;
        while (stack.Count > 0)
        {
            if (++visited > Budget) { Why = "too many boxes"; return null; }
            var (lo, hi, depth) = stack.Pop();
            if (Excl != null && Excl(lo, hi)) continue;
            var xm = new double[n]; for (int i = 0; i < n; i++) xm[i] = (lo[i] + hi[i]) / 2;
            var Jx = J(lo, hi);
            int res = Jx == null ? 0 : Test(lo, hi, xm, Jx, roots);
            if (res != 0) continue;                       // решено: корень найден или корней нет
            // делить по наибольшей стороне (в мм)
            int k = 0; double best = -1;
            for (int i = 0; i < n; i++) { double w = (hi[i] - lo[i]) * Scale[i]; if (w > best) { best = w; k = i; } }
            if (best < MinSize || depth > 200) { Undecided++; Why ??= "undecided box (tangency or multiple root)"; return null; }
            // деление не точно пополам: корень на «круглом» параметре (середине) иначе ложится на сторону обоих половин
            double mid = lo[k] + (hi[k] - lo[k]) * 0.4917;
            var h1 = (double[])hi.Clone(); h1[k] = mid; var l2 = (double[])lo.Clone(); l2[k] = mid;
            stack.Push((lo, h1, depth + 1)); stack.Push((l2, hi, depth + 1));
        }
        // корни в смежных ящиках (на общей стороне) — один
        var outR = new List<double[]>();
        foreach (var r in roots)
        {
            bool dup = false;
            foreach (var q in outR) { double d = 0; for (int i = 0; i < n; i++) d = Math.Max(d, Math.Abs(r[i] - q[i]) * Scale[i]); if (d < 1e-8) { dup = true; break; } }
            if (!dup) outR.Add(r);
        }
        return outR;
    }

    /// <summary>1 — в ящике один корень (добавлен), −1 — корней нет, 0 — не решено.</summary>
    int Test(double[] lo, double[] hi, double[] xm, Iv[,] Jx, List<double[]> roots)
    {
        int n = N;
        var A = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) A[i, j] = Jx[i, j].Mid;
        var K = Inverse(A, n); if (K == null) return 0;
        var fx = F(xm);
        // c = x̃ − K F(x̃)
        var c = new double[n]; double fmag = 0;
        for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += K[i, j] * fx[j]; c[i] = xm[i] - s; }
        for (int j = 0; j < n; j++) fmag = Math.Max(fmag, Math.Abs(fx[j]));
        // (I − K J(X))(X − x̃): |M_ij|max · r_j
        var kr = new Iv[n];
        for (int i = 0; i < n; i++)
        {
            double t = 0;
            for (int j = 0; j < n; j++)
            {
                Iv m = Iv.Pt(i == j ? 1 : 0);
                for (int q = 0; q < n; q++) m = m - Jx[q, j] * K[i, q];
                t += m.Mag * (hi[j] - lo[j]) / 2;
            }
            // округление: относительный запас
            double slack = 1e-12 * (Math.Abs(c[i]) + Math.Abs(xm[i]) + t) + 1e-15 * (hi[i] - lo[i] + 1);
            kr[i] = new Iv(c[i] - t - slack, c[i] + t + slack);
        }
        bool disjoint = false, inside = true;
        for (int i = 0; i < n; i++)
        {
            if (kr[i].Hi < lo[i] || kr[i].Lo > hi[i]) disjoint = true;
            if (!(kr[i].Lo > lo[i] && kr[i].Hi < hi[i])) inside = false;
        }
        if (Dbg != null) Dbg($"box [{string.Join(",", lo.Select(x => x.ToString("G6")))}]..[{string.Join(",", hi.Select(x => x.ToString("G6")))}] fx [{string.Join(",", fx.Select(x => x.ToString("G3")))}] c [{string.Join(",", c.Select(x => x.ToString("G6")))}] kr [{string.Join(",", kr.Select(x => $"{x.Lo:G6}:{x.Hi:G6}"))}] disjoint {disjoint} inside {inside}");
        if (disjoint) return -1;
        if (!inside) return 0;
        // единственный корень: Ньютон из x̃ в пределах X
        var x = (double[])xm.Clone();
        for (int it = 0; it < 60; it++)
        {
            var f = F(x); double mx = 0; for (int j = 0; j < n; j++) mx = Math.Max(mx, Math.Abs(f[j]));
            if (mx < 1e-15) break;
            var d = new double[n]; for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += K[i, j] * f[j]; d[i] = s; }
            bool small = true;
            for (int i = 0; i < n; i++) { double nx = Math.Clamp(x[i] - d[i], lo[i], hi[i]); if (Math.Abs(nx - x[i]) * Scale[i] > 1e-15) small = false; x[i] = nx; }
            if (small) break;
        }
        Dbg?.Invoke($"root [{string.Join(",", x.Select(v => v.ToString("G9")))}] |F| {F(x).Max(Math.Abs):G3}");
        roots.Add(x);
        return 1;
    }

    static double[,]? Inverse(double[,] a, int n)
    {
        var m = new double[n, 2 * n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n + i] = 1; }
        for (int c = 0; c < n; c++)
        {
            int p = c; for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-300) return null;
            if (p != c) for (int k = 0; k < 2 * n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
            double d = m[c, c]; for (int k = 0; k < 2 * n; k++) m[c, k] /= d;
            for (int r = 0; r < n; r++) if (r != c) { double f = m[r, c]; if (f != 0) for (int k = 0; k < 2 * n; k++) m[r, k] -= f * m[c, k]; }
        }
        var inv = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }
}
