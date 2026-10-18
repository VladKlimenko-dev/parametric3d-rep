using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// m.g4: СМЕЩЕНИЕ NURBS-ПОВЕРХНОСТИ — NURBS-ПОВЕРХНОСТЬЮ С ПРОВЕРЕННЫМ ОТХОДОМ. Смещение S + d·n свободной формы не
/// рационально; для оболочки оно приближается бикубическим сплайном глобальной интерполяцией по сетке (n + 1)² точек
/// смещения над областью, продлённой за края на долю <c>margin</c> (смещённая грань у острого ребра выходит за исходную область:
/// продолжение — многочленами крайних промежутков, DerivsExtended). Сетка удваивается, пока отход в серединах клеток
/// не станет меньше бюджета; иначе — null.
/// </summary>
public static class OffsetNurbs
{
    public static (NurbsSurface S, double Deviation)? Fit(NurbsSurface s, double d, double budget, double margin = 0.05, int maxN = 128)
    {
        double lo = -margin, hi = 1 + margin;
        Vector3d P(double u, double v)
        {
            s.DerivsExtended(new Vector2d(u, v), out var S, out var Su, out var Sv);
            var n = Vector3d.Cross(Su, Sv);
            return n.LengthSquared < 1e-24 ? new Vector3d(double.NaN) : S + n.Normalized() * d;
        }
        for (int n = 8; n <= maxN; n *= 2)
        {
            int m = n + 1, deg = 3;
            var t = Enumerable.Range(0, m).Select(i => (double)i / n).ToArray();
            var knots = new double[m + deg + 1];
            for (int i = 0; i <= deg; i++) { knots[i] = 0; knots[^(i + 1)] = 1; }
            for (int j = 1; j < m - deg; j++) knots[j + deg] = t.Skip(j).Take(deg).Average();
            var A = new double[m, m];
            for (int i = 0; i < m; i++) { var nb = BandNurbs.Basis(knots, deg, t[i], m); for (int j = 0; j < m; j++) A[i, j] = nb[j]; }
            var G = new Vector3d[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    var q = P(lo + (hi - lo) * t[i], lo + (hi - lo) * t[j]);
                    if (!double.IsFinite(q.X)) return null;
                    G[i, j] = q;
                }
            var R = new Vector3d[m, m];
            for (int j = 0; j < m; j++)
            {
                var rhs = new Vector4d[m]; for (int i = 0; i < m; i++) rhs[i] = new Vector4d(G[i, j], 1);
                var sol = BandNurbs.Solve(A, rhs, m); if (sol == null) return null;
                for (int i = 0; i < m; i++) R[i, j] = sol[i].Xyz;
            }
            var C = new Vector3d[m, m];
            for (int i = 0; i < m; i++)
            {
                var rhs = new Vector4d[m]; for (int j = 0; j < m; j++) rhs[j] = new Vector4d(R[i, j], 1);
                var sol = BandNurbs.Solve(A, rhs, m); if (sol == null) return null;
                for (int j = 0; j < m; j++) C[i, j] = sol[j].Xyz;
            }
            var w = new double[m, m]; for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) w[i, j] = 1;
            var fit = new NurbsSurface(C, deg, deg, w, (double[])knots.Clone(), (double[])knots.Clone());
            double dev = 0;
            for (int i = 0; i < n && dev <= budget; i++)
                for (int j = 0; j <= 2 * n && dev <= budget; j++)
                {
                    double a = (i + 0.5) / n, b = j / (2.0 * n);
                    dev = Math.Max(dev, (fit.PointAt(new Vector2d(a, b)) - P(lo + (hi - lo) * a, lo + (hi - lo) * b)).Length);
                    dev = Math.Max(dev, (fit.PointAt(new Vector2d(b, a)) - P(lo + (hi - lo) * b, lo + (hi - lo) * a)).Length);
                }
            if (dev <= budget) return (fit, dev);
        }
        return null;
    }
}
