using System.Numerics;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Exact;

namespace Parametric3D.Kernel.Topology;

/// <summary>Класс пары треугольников сетки (проверка самопересечений).</summary>
public enum PairClass
{
    /// <summary>Общих точек нет.</summary>
    Disjoint,
    /// <summary>Общая вершина или общее ребро (по номерам вершин) и больше ничего.</summary>
    Adjacent,
    /// <summary>Собственное пересечение (отрезок пересечения проходит внутри обоих), в т. ч. через общую вершину.</summary>
    Transversal,
    /// <summary>Лежат в одной плоскости, внутренности пересекаются по ненулевой площади (наложение, складка).</summary>
    CoplanarOverlap,
    /// <summary>Касание без пересечения внутренностей: вершина на другом треугольнике, рёбра соприкасаются (T-стык).</summary>
    TJunction,
    /// <summary>Те же три точки (в любом порядке и обходе): двойная грань или плёнка.</summary>
    Duplicate,
}

/// <summary>
/// ТОЧНЫЕ ПРЕДИКАТЫ НА DOUBLE. Знак определителя сначала считается в double с границей погрешности
/// (Shewchuk, «Adaptive precision floating-point arithmetic», оценки A); если значение внутри границы —
/// точно в целых: каждое double — мантисса·2^порядок, все приводятся к общему наименьшему порядку.
/// Ответ — точный знак для заданных double-координат; никаких допусков.
/// </summary>
internal static class ExactPredicates
{
    private const double Eps = 1.1102230246251565e-16;          // 2^-53
    private const double O3dBound = (7 + 56 * Eps) * Eps;
    private const double O2dBound = (3 + 16 * Eps) * Eps;

    /// <summary>Знак (a−d)·((b−d)×(c−d)): положителен, если d по ту сторону плоскости abc, куда смотрит … (как Orient прежней проверки).</summary>
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
        Exact++;
        var v = Scale(stackalloc double[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z, d.X, d.Y, d.Z });
        BigInteger ax = v[0] - v[9], ay = v[1] - v[10], az = v[2] - v[11];
        BigInteger bx = v[3] - v[9], by = v[4] - v[10], bz = v[5] - v[11];
        BigInteger cx = v[6] - v[9], cy = v[7] - v[10], cz = v[8] - v[11];
        var e = az * (bx * cy - cx * by) + bz * (cx * ay - ax * cy) + cz * (ax * by - bx * ay);
        return e.Sign;
    }

    /// <summary>Знак (b−a)×(c−a) на плоскости.</summary>
    public static int Orient2D(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double l = (ax - cx) * (by - cy), r = (ay - cy) * (bx - cx);
        double det = l - r;
        double bound = O2dBound * (Math.Abs(l) + Math.Abs(r));
        if (det > bound) return 1;
        if (-det > bound) return -1;
        if (l == 0 && r == 0) return 0;
        Exact++;
        var v = Scale(stackalloc double[] { ax, ay, bx, by, cx, cy });
        return ((v[0] - v[4]) * (v[3] - v[5]) - (v[1] - v[5]) * (v[2] - v[4])).Sign;
    }

    /// <summary>Сколько раз понадобилась точная арифметика (для замеров).</summary>
    [ThreadStatic] public static long Exact;

    /// <summary>Точки → целые координаты с общим наименьшим порядком (точно; общий множитель 2^e не важен для знаков).</summary>
    internal static BigInteger[][] ScalePoints(IReadOnlyList<Vector3d> ps)
    {
        var flat = new double[ps.Count * 3];
        for (int i = 0; i < ps.Count; i++) { flat[3 * i] = ps[i].X; flat[3 * i + 1] = ps[i].Y; flat[3 * i + 2] = ps[i].Z; }
        var v = Scale(flat);
        var r = new BigInteger[ps.Count][];
        for (int i = 0; i < ps.Count; i++) r[i] = new[] { v[3 * i], v[3 * i + 1], v[3 * i + 2] };
        return r;
    }

    /// <summary>double → целые с общим наименьшим порядком (точно).</summary>
    private static BigInteger[] Scale(ReadOnlySpan<double> xs)
    {
        Span<long> m = stackalloc long[xs.Length]; Span<int> e = stackalloc int[xs.Length];
        int emin = int.MaxValue;
        for (int i = 0; i < xs.Length; i++)
        {
            (m[i], e[i]) = Decompose(xs[i]);
            if (m[i] != 0) emin = Math.Min(emin, e[i]);
        }
        var r = new BigInteger[xs.Length];
        for (int i = 0; i < xs.Length; i++)
            r[i] = m[i] == 0 ? BigInteger.Zero : new BigInteger(m[i]) << (e[i] - emin);
        return r;
    }

    /// <summary>x = m·2^e, m — целое со знаком (до 53 бит).</summary>
    private static (long M, int E) Decompose(double x)
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

/// <summary>
/// КЛАССИФИКАЦИЯ ПАРЫ ТРЕУГОЛЬНИКОВ — точно, без допусков (ExactPredicates).
/// Компланарные пары — в проекции на координатную плоскость, где нормаль треугольника наибольшая:
/// для лежащих в одной плоскости точек такая проекция сохраняет все знаки (с общим множителем).
/// Наложение — по разделяющей стороне: внутренности двух выпуклых многоугольников не пересекаются тогда и
/// только тогда, когда у одного из них есть сторона, за которой (или на прямой которой) лежит весь другой.
/// </summary>
public static class TrianglePairs
{
    /// <param name="ia">Номера вершин A (тождество вершин — по номеру), ib — B.</param>
    public static PairClass Classify(Vector3d[] a, int[] ia, Vector3d[] b, int[] ib)
    {
        // ---- общие вершины (по номерам)
        var sharedA = new bool[3]; var sharedB = new bool[3]; int shared = 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (ia[i] == ib[j]) { sharedA[i] = true; sharedB[j] = true; shared++; }

        // ---- дубликат: те же три точки
        if (shared == 3 || SamePoints(a, b)) return PairClass.Duplicate;

        Span<int> oa = stackalloc int[3]; Span<int> ob = stackalloc int[3];
        for (int j = 0; j < 3; j++) oa[j] = sharedB[j] ? 0 : ExactPredicates.Orient3D(a[0], a[1], a[2], b[j]);
        for (int i = 0; i < 3; i++) ob[i] = sharedA[i] ? 0 : ExactPredicates.Orient3D(b[0], b[1], b[2], a[i]);

        bool coplanar = oa[0] == 0 && oa[1] == 0 && oa[2] == 0;
        if (coplanar)
        {
            int axis = DominantAxis(a);
            if (axis < 0) return shared > 0 ? PairClass.Adjacent : PairClass.Disjoint; // вырожденный A
            if (InteriorsOverlap2D(a, b, axis)) return PairClass.CoplanarOverlap;
            for (int j = 0; j < 3; j++) if (!sharedB[j] && InClosedTriangle(a, b[j], axis)) return PairClass.TJunction;
            for (int i = 0; i < 3; i++) if (!sharedA[i] && InClosedTriangle(b, a[i], axis)) return PairClass.TJunction;
            return shared > 0 ? PairClass.Adjacent : PairClass.Disjoint;
        }

        // ---- B целиком по одну сторону плоскости A (или A — B), кроме общих вершин: не пересекаются
        if (OneSide(oa, sharedB) || OneSide(ob, sharedA)) return shared > 0 ? PairClass.Adjacent : PairClass.Disjoint;

        // ---- собственное пересечение: ребро одного строго проходит внутри другого
        for (int k = 0; k < 3; k++)
            if (Crosses(a[k], a[(k + 1) % 3], b) || Crosses(b[k], b[(k + 1) % 3], a)) return PairClass.Transversal;

        // ---- вырожденное пересечение: каждый треугольник строго по обе стороны плоскости другого, а отрезок
        // пересечения проходит по рёбрам и диагоналям (целые координаты, совпадающие разбиения). Поверхности
        // пересекаются, если отрезки обоих треугольников на общей прямой перекрываются на ненулевой длине;
        // перекрытие в точку — касание.
        if (Straddles(oa) && Straddles(ob) && OverlapOnLine(a, b)) return PairClass.Transversal;

        // ---- касание помимо общих вершин: вершина на другом треугольнике, рёбра пересекаются внутри
        for (int j = 0; j < 3; j++)
            if (!sharedB[j] && oa[j] == 0 && InClosedTriangle(a, b[j], DominantAxis(a))) return PairClass.TJunction;
        for (int i = 0; i < 3; i++)
            if (!sharedA[i] && ob[i] == 0 && InClosedTriangle(b, a[i], DominantAxis(b))) return PairClass.TJunction;
        for (int k = 0; k < 3; k++)
            for (int l = 0; l < 3; l++)
                if (SegmentsCrossInside(a[k], a[(k + 1) % 3], b[l], b[(l + 1) % 3])) return PairClass.TJunction;
        return shared > 0 ? PairClass.Adjacent : PairClass.Disjoint;
    }

    private static bool Straddles(ReadOnlySpan<int> s)
    {
        bool pos = false, neg = false;
        foreach (int x in s) { if (x > 0) pos = true; else if (x < 0) neg = true; }
        return pos && neg;
    }

    /// <summary>
    /// Отрезки пересечения треугольников с прямой их плоскостей перекрываются на ненулевой длине (точно).
    /// Вызывается, только если каждый треугольник строго по обе стороны плоскости другого.
    /// </summary>
    private static bool OverlapOnLine(Vector3d[] a, Vector3d[] b)
    {
        var q = ExactPredicates.ScalePoints(new[] { a[0], a[1], a[2], b[0], b[1], b[2] });
        BigInteger[] Sub(BigInteger[] u, BigInteger[] v) => new[] { u[0] - v[0], u[1] - v[1], u[2] - v[2] };
        BigInteger[] Cross(BigInteger[] u, BigInteger[] v) =>
            new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
        BigInteger Dot(BigInteger[] u, BigInteger[] v) => u[0] * v[0] + u[1] * v[1] + u[2] * v[2];
        var na = Cross(Sub(q[1], q[0]), Sub(q[2], q[0]));
        var nb = Cross(Sub(q[4], q[3]), Sub(q[5], q[3]));
        var dir = Cross(na, nb);
        // Интервал треугольника (вершины t0..t2) на прямой: параметр — проекция на dir точек, где стороны
        // треугольника пересекают плоскость другого (n·x = n·p0).
        (BigRational Lo, BigRational Hi)? Interval(int t, BigInteger[] n, BigInteger[] p0)
        {
            BigInteger d0 = Dot(n, p0);
            var s = new BigInteger[3]; for (int i = 0; i < 3; i++) s[i] = Dot(n, q[t + i]) - d0;
            BigRational? lo = null, hi = null;
            void Add(BigRational x) { if (lo == null || x < lo.Value) lo = x; if (hi == null || x > hi.Value) hi = x; }
            for (int i = 0; i < 3; i++)
            {
                if (s[i].IsZero) Add(Dot(dir, q[t + i]));
                int j = (i + 1) % 3;
                if (s[i].Sign * s[j].Sign < 0)
                {
                    // x = (s_j·p_i − s_i·p_j)/(s_j − s_i); проекция на dir — линейна.
                    var num = s[j] * Dot(dir, q[t + i]) - s[i] * Dot(dir, q[t + j]);
                    Add(new BigRational(num, s[j] - s[i]));
                }
            }
            return lo == null ? null : (lo.Value, hi!.Value);
        }
        var ia = Interval(0, nb, q[3]); var ib = Interval(3, na, q[0]);
        if (ia == null || ib == null) return false;
        var low = ia.Value.Lo > ib.Value.Lo ? ia.Value.Lo : ib.Value.Lo;
        var high = ia.Value.Hi < ib.Value.Hi ? ia.Value.Hi : ib.Value.Hi;
        return low < high;
    }

    /// <summary>Все НЕ общие вершины строго по одну сторону (ноль у не общей вершины — возможное касание).</summary>
    private static bool OneSide(ReadOnlySpan<int> s, bool[] shared)
    {
        int sign = 0;
        for (int i = 0; i < 3; i++)
        {
            if (shared[i]) continue;
            if (s[i] == 0) return false;
            if (sign == 0) sign = s[i]; else if (s[i] != sign) return false;
        }
        return sign != 0;
    }

    private static bool SamePoints(Vector3d[] a, Vector3d[] b) =>
        a.All(p => b.Contains(p)) && b.All(p => a.Contains(p));

    /// <summary>Отрезок pq собственно пересекает внутренность треугольника t (все знаки строгие).</summary>
    private static bool Crosses(Vector3d p, Vector3d q, Vector3d[] t)
    {
        int o1 = ExactPredicates.Orient3D(t[0], t[1], t[2], p), o2 = ExactPredicates.Orient3D(t[0], t[1], t[2], q);
        if (o1 == 0 || o2 == 0 || o1 == o2) return false;
        int s1 = ExactPredicates.Orient3D(p, q, t[0], t[1]), s2 = ExactPredicates.Orient3D(p, q, t[1], t[2]),
            s3 = ExactPredicates.Orient3D(p, q, t[2], t[0]);
        return s1 != 0 && s1 == s2 && s2 == s3;
    }

    /// <summary>Ось, отбрасываемая при проекции: наибольшая по модулю компонента нормали (−1 — вырожден).</summary>
    private static int DominantAxis(Vector3d[] t)
    {
        var n = Vector3d.Cross(t[1] - t[0], t[2] - t[0]);
        double x = Math.Abs(n.X), y = Math.Abs(n.Y), z = Math.Abs(n.Z);
        if (x == 0 && y == 0 && z == 0) return -1;
        return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
    }

    private static (double U, double V) P2(Vector3d p, int axis) =>
        axis == 0 ? (p.Y, p.Z) : axis == 1 ? (p.Z, p.X) : (p.X, p.Y);

    private static int O2(Vector3d a, Vector3d b, Vector3d c, int axis)
    {
        var (ax, ay) = P2(a, axis); var (bx, by) = P2(b, axis); var (cx, cy) = P2(c, axis);
        return ExactPredicates.Orient2D(ax, ay, bx, by, cx, cy);
    }

    /// <summary>Точка (в плоскости t) лежит в замкнутом треугольнике t.</summary>
    private static bool InClosedTriangle(Vector3d[] t, Vector3d p, int axis)
    {
        if (axis < 0) return false;
        int s = O2(t[0], t[1], t[2], axis);
        if (s == 0) return false;
        for (int k = 0; k < 3; k++) if (O2(t[k], t[(k + 1) % 3], p, axis) * s < 0) return false;
        return true;
    }

    /// <summary>Внутренности компланарных треугольников пересекаются (ненулевая площадь).</summary>
    private static bool InteriorsOverlap2D(Vector3d[] a, Vector3d[] b, int axis)
    {
        int sa = O2(a[0], a[1], a[2], axis), sb = O2(b[0], b[1], b[2], axis);
        if (sa == 0 || sb == 0) return false;
        bool Separates(Vector3d[] t, int st, Vector3d[] other)
        {
            for (int k = 0; k < 3; k++)
            {
                bool allOut = true;
                for (int j = 0; j < 3 && allOut; j++)
                    if (O2(t[k], t[(k + 1) % 3], other[j], axis) * st > 0) allOut = false;
                if (allOut) return true;
            }
            return false;
        }
        return !Separates(a, sa, b) && !Separates(b, sb, a);
    }

    /// <summary>Непараллельные отрезки одной плоскости пересекаются во внутренних точках обоих.</summary>
    private static bool SegmentsCrossInside(Vector3d p, Vector3d q, Vector3d r, Vector3d s)
    {
        if (ExactPredicates.Orient3D(p, q, r, s) != 0) return false;
        var n = Vector3d.Cross(q - p, s - r);
        double x = Math.Abs(n.X), y = Math.Abs(n.Y), z = Math.Abs(n.Z);
        if (x == 0 && y == 0 && z == 0) return false;   // параллельны: касание даст вершина на ребре
        int axis = x >= y && x >= z ? 0 : y >= z ? 1 : 2;
        int a1 = O2(p, q, r, axis), a2 = O2(p, q, s, axis), b1 = O2(r, s, p, axis), b2 = O2(r, s, q, axis);
        return a1 * a2 < 0 && b1 * b2 < 0;
    }
}
