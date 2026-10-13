using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Robust;

/// <summary>
/// KRN-02 (02.4): ЕДИНЫЕ ГЕОМЕТРИЧЕСКИЕ ПРЕДИКАТЫ — одна знаковая конвенция, фильтр ошибки, точное уточнение.
/// <para>
/// Конвенция (как у Shewchuk, «Adaptive Precision Floating-Point Arithmetic and Fast Robust Geometric Predicates»):
/// <list type="bullet">
/// <item>Orient2D(a, b, c) &gt; 0 — a, b, c обходятся ПРОТИВ часовой стрелки (c слева от направленной прямой a→b); 0 — на одной прямой.</item>
/// <item>Orient3D(a, b, c, d) &gt; 0 — d НИЖЕ плоскости abc, где «выше» — сторона, откуда a, b, c видны против часовой
/// (знак (a−d)·((b−d)×(c−d))); 0 — четыре точки в одной плоскости.</item>
/// <item>InCircle(a, b, c, d) &gt; 0 — d ВНУТРИ окружности через a, b, c при a, b, c против часовой (при обходе по часовой
/// знак меняется — как у определителя); 0 — на окружности.</item>
/// </list>
/// Ответ — ТОЧНЫЙ знак для заданных double-координат, без допусков: сначала определитель в double с границей погрешности
/// (оценки A Shewchuk), если значение внутри границы — точно в целых (каждое double — мантисса·2^порядок, общий
/// наименьший порядок). Допуски (сварка, «почти на прямой») — дело вызывающего, не предиката: предикат отвечает за
/// согласованность (антисимметрия, циклическая инвариантность, транзитивность решений), без которой триангуляции и
/// классификации получают противоречивые ответы на почти вырожденных входах.
/// </para>
/// </summary>
public static class Predicates
{
    const double Eps = 1.1102230246251565e-16;                   // 2^-53
    const double O2dBound = (3 + 16 * Eps) * Eps;
    const double O3dBound = (7 + 56 * Eps) * Eps;
    const double IccBound = (10 + 96 * Eps) * Eps;

    /// <summary>Сколько раз фильтр не решил и понадобилась точная арифметика (диагностика).</summary>
    [ThreadStatic] public static long ExactFallbacks;

    public static int Orient2D(Vector2d a, Vector2d b, Vector2d c) => Orient2D(a.X, a.Y, b.X, b.Y, c.X, c.Y);

    public static int Orient2D(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double l = (ax - cx) * (by - cy), r = (ay - cy) * (bx - cx);
        double det = l - r;
        double bound = O2dBound * (Math.Abs(l) + Math.Abs(r));
        if (det > bound) return 1;
        if (-det > bound) return -1;
        if (l == 0 && r == 0) return 0;
        ExactFallbacks++;
        var v = Scale(stackalloc double[] { ax, ay, bx, by, cx, cy });
        return ((v[0] - v[4]) * (v[3] - v[5]) - (v[1] - v[5]) * (v[2] - v[4])).Sign;
    }

    public static int Orient3D(Vector3d a, Vector3d b, Vector3d c, Vector3d d)
    {
        double adx = a.X - d.X, ady = a.Y - d.Y, adz = a.Z - d.Z;
        double bdx = b.X - d.X, bdy = b.Y - d.Y, bdz = b.Z - d.Z;
        double cdx = c.X - d.X, cdy = c.Y - d.Y, cdz = c.Z - d.Z;
        double bdxcdy = bdx * cdy, cdxbdy = cdx * bdy;
        double cdxady = cdx * ady, adxcdy = adx * cdy;
        double adxbdy = adx * bdy, bdxady = bdx * ady;
        double det = adz * (bdxcdy - cdxbdy) + bdz * (cdxady - adxcdy) + cdz * (adxbdy - bdxady);
        double permanent = (Math.Abs(bdxcdy) + Math.Abs(cdxbdy)) * Math.Abs(adz)
                         + (Math.Abs(cdxady) + Math.Abs(adxcdy)) * Math.Abs(bdz)
                         + (Math.Abs(adxbdy) + Math.Abs(bdxady)) * Math.Abs(cdz);
        double bound = O3dBound * permanent;
        if (det > bound) return 1;
        if (-det > bound) return -1;
        if (permanent == 0) return 0;
        ExactFallbacks++;
        var v = Scale(stackalloc double[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z, d.X, d.Y, d.Z });
        BigInteger ax = v[0] - v[9], ay = v[1] - v[10], az = v[2] - v[11];
        BigInteger bx = v[3] - v[9], by = v[4] - v[10], bz = v[5] - v[11];
        BigInteger cx = v[6] - v[9], cy = v[7] - v[10], cz = v[8] - v[11];
        return (az * (bx * cy - cx * by) + bz * (cx * ay - ax * cy) + cz * (ax * by - bx * ay)).Sign;
    }

    public static int InCircle(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        double adx = a.X - d.X, bdx = b.X - d.X, cdx = c.X - d.X;
        double ady = a.Y - d.Y, bdy = b.Y - d.Y, cdy = c.Y - d.Y;
        double bdxcdy = bdx * cdy, cdxbdy = cdx * bdy, alift = adx * adx + ady * ady;
        double cdxady = cdx * ady, adxcdy = adx * cdy, blift = bdx * bdx + bdy * bdy;
        double adxbdy = adx * bdy, bdxady = bdx * ady, clift = cdx * cdx + cdy * cdy;
        double det = alift * (bdxcdy - cdxbdy) + blift * (cdxady - adxcdy) + clift * (adxbdy - bdxady);
        double permanent = (Math.Abs(bdxcdy) + Math.Abs(cdxbdy)) * alift
                         + (Math.Abs(cdxady) + Math.Abs(adxcdy)) * blift
                         + (Math.Abs(adxbdy) + Math.Abs(bdxady)) * clift;
        double bound = IccBound * permanent;
        if (det > bound) return 1;
        if (-det > bound) return -1;
        if (permanent == 0) return 0;
        ExactFallbacks++;
        var v = Scale(stackalloc double[] { a.X, a.Y, b.X, b.Y, c.X, c.Y, d.X, d.Y });
        BigInteger ax = v[0] - v[6], ay = v[1] - v[7], bx = v[2] - v[6], by = v[3] - v[7], cx = v[4] - v[6], cy = v[5] - v[7];
        var e = (ax * ax + ay * ay) * (bx * cy - cx * by) + (bx * bx + by * by) * (cx * ay - ax * cy) + (cx * cx + cy * cy) * (ax * by - bx * ay);
        return e.Sign;
    }

    /// <summary>
    /// Пересекаются ли замкнутые отрезки ab и cd (касание концом и наложение на одной прямой — тоже пересечение) — точно.
    /// </summary>
    public static bool SegmentsIntersect(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        int d1 = Orient2D(c, d, a), d2 = Orient2D(c, d, b), d3 = Orient2D(a, b, c), d4 = Orient2D(a, b, d);
        if (d1 * d2 < 0 && d3 * d4 < 0) return true;
        bool On(Vector2d p, Vector2d q, Vector2d r) => Math.Min(p.X, q.X) <= r.X && r.X <= Math.Max(p.X, q.X) && Math.Min(p.Y, q.Y) <= r.Y && r.Y <= Math.Max(p.Y, q.Y);
        return (d1 == 0 && On(c, d, a)) || (d2 == 0 && On(c, d, b)) || (d3 == 0 && On(a, b, c)) || (d4 == 0 && On(a, b, d));
    }

    /// <summary>Пересекаются ли отрезки ВНУТРЕННИМИ точками обоих (собственное пересечение, концы не в счёт) — точно.</summary>
    public static bool SegmentsCrossProperly(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        int d1 = Orient2D(c, d, a), d2 = Orient2D(c, d, b), d3 = Orient2D(a, b, c), d4 = Orient2D(a, b, d);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    /// <summary>double → целые с общим наименьшим порядком (точно; общий множитель 2^e на знаки не влияет).</summary>
    internal static BigInteger[] Scale(ReadOnlySpan<double> xs)
    {
        Span<long> m = stackalloc long[xs.Length]; Span<int> e = stackalloc int[xs.Length];
        int emin = int.MaxValue;
        for (int i = 0; i < xs.Length; i++)
        {
            (m[i], e[i]) = Decompose(xs[i]);
            if (m[i] != 0) emin = Math.Min(emin, e[i]);
        }
        var r = new BigInteger[xs.Length];
        for (int i = 0; i < xs.Length; i++) r[i] = m[i] == 0 ? BigInteger.Zero : new BigInteger(m[i]) << (e[i] - emin);
        return r;
    }

    /// <summary>x = m·2^e, m — целое со знаком (до 53 бит).</summary>
    internal static (long M, int E) Decompose(double x)
    {
        if (!double.IsFinite(x)) throw new ArgumentException("non-finite coordinate");
        if (x == 0) return (0, 0);
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool negative = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        exp -= 1075;
        while ((mant & 1) == 0) { mant >>= 1; exp++; }
        return (negative ? -mant : mant, exp);
    }
}
