using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// K08/K10. КУБИЧЕСКИЙ B-СПЛАЙН ЧЕРЕЗ ТОЧКИ КРИВОЙ — С ПРОВЕРЕННЫМ ОТКЛОНЕНИЕМ.
///
/// Кривая пересечения двух поверхностей точной записи в обменных форматах не имеет; её пишут
/// B-сплайном. Здесь сплайн проходит через N + 1 точку кривой (параметры по длине хорд, узлы —
/// усреднением, NURBS Book 9.8), и отклонение проверяется по плотной выборке самой кривой.
/// N удваивается, пока отклонение не станет меньше допуска; иначе — null (вызывающий решает,
/// писать ли приближение грубее, и говорит об этом).
/// </summary>
public static class SplineFit
{
    public static (SplineCurve3 Spline, double Deviation)? Fit(ICurve3 curve, double tolerance, int maxPoints = 512)
    {
        for (int n = 16; n <= maxPoints; n *= 2)
        {
            var s = Interpolate(curve, n);
            if (s == null) continue;
            double dev = Deviation(curve, s, n);
            if (dev <= tolerance) return (s, dev);
        }
        return null;
    }

    /// <summary>Наибольшее расстояние от точек сплайна до кривой (плотная выборка обеих).</summary>
    public static double Deviation(ICurve3 curve, SplineCurve3 s, int n)
    {
        int dense = 16 * n;
        var truth = new Vector3d[dense + 1];
        for (int i = 0; i <= dense; i++) truth[i] = At(curve, (double)i / dense);
        double worst = 0;
        int j = 0;
        for (int k = 0; k <= 4 * n; k++)
        {
            var p = s.PointAt(k / (4.0 * n));
            // Ближайшая точка кривой — поиском около предыдущей (обе идут в одну сторону).
            double best = double.MaxValue; int bj = j;
            for (int q = Math.Max(0, j - 64); q <= Math.Min(dense, j + 64); q++)
            {
                double d = (truth[q] - p).LengthSquared;
                if (d < best) { best = d; bj = q; }
            }
            // Уточнение на отрезке между соседними плотными точками.
            double seg = Math.Sqrt(best);
            foreach (int q in new[] { bj - 1, bj })
            {
                if (q < 0 || q + 1 > dense) continue;
                var a = truth[q]; var b = truth[q + 1]; var ab = b - a;
                double t = Math.Clamp(Vector3d.Dot(p - a, ab) / Math.Max(ab.LengthSquared, 1e-30), 0, 1);
                seg = Math.Min(seg, (a + ab * t - p).Length);
            }
            worst = Math.Max(worst, seg);
            j = bj;
        }
        return worst;
    }

    /// <summary>
    /// Точка кривой по доле параметра — с отступом от самых концов: у кривой пересечения концы
    /// отдаются точками затравки без уточнения, и они отходят от настоящей кривой (до 0.01 мм у
    /// поперечного отверстия). Кубический сплайн через такой скачок колебался, и отклонение не
    /// падало с ростом числа точек.
    /// </summary>
    private static Vector3d At(ICurve3 c, double f) =>
        c.PointAt(c.T0 + (c.T1 - c.T0) * Math.Clamp(f, 1e-9, 1 - 1e-9));

    private static SplineCurve3? Interpolate(ICurve3 curve, int n)
    {
        const int p = 3;
        var Q = new Vector3d[n + 1];
        for (int i = 0; i <= n; i++) Q[i] = At(curve, (double)i / n);
        // Параметры по длине хорд.
        var u = new double[n + 1];
        double total = 0;
        for (int i = 1; i <= n; i++) total += (Q[i] - Q[i - 1]).Length;
        if (total <= 0) return null;
        for (int i = 1; i <= n; i++) u[i] = u[i - 1] + (Q[i] - Q[i - 1]).Length / total;
        u[n] = 1;
        // Узлы усреднением.
        int m = n + p + 1;
        var U = new double[m + 1];
        for (int i = 0; i <= p; i++) { U[i] = 0; U[m - i] = 1; }
        for (int j = 1; j <= n - p; j++)
        {
            double sum = 0;
            for (int i = j; i <= j + p - 1; i++) sum += u[i];
            U[j + p] = sum / p;
        }
        // Система N·P = Q (плотная, n ≤ 512).
        int size = n + 1;
        var A = new double[size, size];
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                A[r, c] = Basis(c, p, u[r], U);
        var bx = Q.Select(q => q.X).ToArray(); var by = Q.Select(q => q.Y).ToArray(); var bz = Q.Select(q => q.Z).ToArray();
        if (!Solve(A, bx, by, bz)) return null;
        var P = new Vector3d[size];
        for (int i = 0; i < size; i++) P[i] = new Vector3d(bx[i], by[i], bz[i]);
        return new SplineCurve3(P, p, null, U);
    }

    /// <summary>Базисная функция N_{i,p}(t) по Коксу — де Буру; правый конец включается в последний пролёт.</summary>
    private static double Basis(int i, int p, double t, double[] U)
    {
        if (p == 0)
        {
            // Правый конец относится к последнему непустому пролёту.
            double tt = Math.Min(t, U[^1] - 1e-12);
            return U[i] <= tt && tt < U[i + 1] ? 1 : 0;
        }
        double a = 0, b = 0;
        double d1 = U[i + p] - U[i], d2 = U[i + p + 1] - U[i + 1];
        if (d1 > 0) a = (t - U[i]) / d1 * Basis(i, p - 1, t, U);
        if (d2 > 0) b = (U[i + p + 1] - t) / d2 * Basis(i + 1, p - 1, t, U);
        return a + b;
    }

    /// <summary>Гаусс с выбором главного элемента для трёх правых частей.</summary>
    private static bool Solve(double[,] A, double[] x, double[] y, double[] z)
    {
        int n = x.Length;
        for (int col = 0; col < n; col++)
        {
            int piv = col; double best = Math.Abs(A[col, col]);
            for (int r = col + 1; r < n; r++) if (Math.Abs(A[r, col]) > best) { best = Math.Abs(A[r, col]); piv = r; }
            if (best < 1e-14) return false;
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (A[col, c], A[piv, c]) = (A[piv, c], A[col, c]);
                (x[col], x[piv]) = (x[piv], x[col]); (y[col], y[piv]) = (y[piv], y[col]); (z[col], z[piv]) = (z[piv], z[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = A[r, col] / A[col, col];
                if (f == 0) continue;
                for (int c = col; c < n; c++) A[r, c] -= f * A[col, c];
                x[r] -= f * x[col]; y[r] -= f * y[col]; z[r] -= f * z[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            for (int c = r + 1; c < n; c++) { x[r] -= A[r, c] * x[c]; y[r] -= A[r, c] * y[c]; z[r] -= A[r, c] * z[c]; }
            x[r] /= A[r, r]; y[r] /= A[r, r]; z[r] /= A[r, r];
        }
        return true;
    }
}
