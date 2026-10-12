using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// KRN-06/обмен: КУСОК ЛЕНТЫ ПО ФОРМУЛЕ СЕЧЕНИЯ (дуга окружности в каждом сечении) — РАЦИОНАЛЬНЫМ B-СПЛАЙНОМ для записи в
/// STEP. По v — дуга точно (рациональная квадратичная: P0, P1 на пересечении касательных, P2; веса 1, cos(φ/2), 1); по u —
/// кубическая интерполяция однородных координат (w·P, w) трёх рядов через N + 1 сечений с равным шагом (глобальная
/// интерполяция, усреднённые узлы). В сечениях-узлах поверхность точна, между ними — приближение; отход измеряется в
/// серединах промежутков (5 значений v) по самой формуле, N удваивается до бюджета. null — бюджет не достигнут или дуга
/// не годится для одного рационального сегмента (угол ≥ π).
/// </summary>
public static class BandNurbs
{
    public static (NurbsSurface S, double Deviation)? Fit(SectionSurface ss, double ua, double ub, double budget, int maxN = 1024)
    {
        if (!ss.IsProcedural) return null;
        for (int n = 16; n <= maxN; n *= 2)
        {
            var secs = new SweepSection[n + 1];
            for (int i = 0; i <= n; i++) { secs[i] = ss.ExactSectionAt(ua + (ub - ua) * i / n); if (!secs[i].IsArc || !(secs[i].Phi > 1e-9 && secs[i].Phi < Math.PI - 1e-6)) return null; }
            // однородные опорные точки сечений: 3 ряда × (n + 1) сечений
            var H = new Vector4d[3, n + 1];
            for (int i = 0; i <= n; i++)
            {
                var s = secs[i]; double h = s.Phi / 2, wm = Math.Cos(h);
                var p0 = s.C + s.E1 * s.R; var p2 = s.C + (s.E1 * Math.Cos(s.Phi) + s.E2 * Math.Sin(s.Phi)) * s.R;
                var p1 = s.C + (s.E1 + s.E2 * Math.Tan(h)) * s.R;
                H[0, i] = new Vector4d(p0, 1); H[1, i] = new Vector4d(p1 * wm, wm); H[2, i] = new Vector4d(p2, 1);
            }
            // глобальная кубическая интерполяция по равномерным параметрам t_i = i/n
            int deg = 3, m = n + 1;
            var t = Enumerable.Range(0, m).Select(i => (double)i / n).ToArray();
            var knots = new double[m + deg + 1];
            for (int i = 0; i <= deg; i++) { knots[i] = 0; knots[^(i + 1)] = 1; }
            for (int j = 1; j < m - deg; j++) knots[j + deg] = t.Skip(j).Take(deg).Average();
            var A = new double[m, m];
            for (int i = 0; i < m; i++) { var nb = Basis(knots, deg, t[i], m); for (int j = 0; j < m; j++) A[i, j] = nb[j]; }
            var D = new Vector4d[3, m];
            for (int row = 0; row < 3; row++)
            {
                var rhs = new Vector4d[m]; for (int i = 0; i < m; i++) rhs[i] = H[row, i];
                var sol = Solve(A, rhs, m); if (sol == null) return null;
                for (int j = 0; j < m; j++) D[row, j] = sol[j];
            }
            var cp = new Vector3d[m, 3]; var w = new double[m, 3];
            for (int j = 0; j < m; j++)
                for (int row = 0; row < 3; row++)
                {
                    var d = D[row, j]; if (!(d.W > 1e-9)) return null;
                    cp[j, row] = d.Xyz / d.W; w[j, row] = d.W;
                }
            var nurbs = new NurbsSurface(cp, deg, 2, w, knots, new double[] { 0, 0, 0, 1, 1, 1 });
            double dev = 0;
            for (int i = 0; i < n; i++)
            {
                double tm = (i + 0.5) / n; var ex = ss.ExactSectionAt(ua + (ub - ua) * tm);
                foreach (double v in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
                {
                    var q = nurbs.PointAt(new Vector2d(tm, v));
                    // точка рационального сплайна при параметре v — не точка сечения при той же доле угла: мерить расстояние
                    // до дуги сечения (её плоскость и окружность)
                    var nrm = Vector3d.Cross(ex.E1, ex.E2).Normalized(); var dq = q - ex.C; double off = Vector3d.Dot(dq, nrm);
                    var inPlane = dq - nrm * off; double rad = inPlane.Length - ex.R;
                    dev = Math.Max(dev, Math.Sqrt(off * off + rad * rad));
                }
            }
            if (dev <= budget) return (nurbs, dev);
        }
        return null;
    }

    static double[] Basis(double[] U, int p, double u, int n)
    {
        var N = new double[n];
        if (u >= U[n]) { N[n - 1] = 1; return N; }
        int span = p; while (span < n - 1 && u >= U[span + 1]) span++;
        var left = new double[p + 1]; var right = new double[p + 1]; var Nd = new double[p + 1]; Nd[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - U[span + 1 - j]; right[j] = U[span + j] - u; double saved = 0;
            for (int r = 0; r < j; r++) { double tmp = Nd[r] / (right[r + 1] + left[j - r]); Nd[r] = saved + right[r + 1] * tmp; saved = left[j - r] * tmp; }
            Nd[j] = saved;
        }
        for (int j = 0; j <= p; j++) N[span - p + j] = Nd[j];
        return N;
    }

    static Vector4d[]? Solve(double[,] A0, Vector4d[] b0, int n)
    {
        var A = (double[,])A0.Clone(); var b = (Vector4d[])b0.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
            if (Math.Abs(A[piv, c]) < 1e-14) return null;
            if (piv != c) { for (int k = 0; k < n; k++) (A[c, k], A[piv, k]) = (A[piv, k], A[c, k]); (b[c], b[piv]) = (b[piv], b[c]); }
            for (int r = c + 1; r < n; r++)
            {
                double f = A[r, c] / A[c, c]; if (f == 0) continue;
                for (int k = c; k < n; k++) A[r, k] -= f * A[c, k];
                b[r] -= b[c] * f;
            }
        }
        var x = new Vector4d[n];
        for (int r = n - 1; r >= 0; r--) { var s = b[r]; for (int k = r + 1; k < n; k++) s -= x[k] * A[r, k]; x[r] = s / A[r, r]; }
        return x;
    }
}
