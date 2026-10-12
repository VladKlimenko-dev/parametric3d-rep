using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// ТОЧКА С ИРРАЦИОНАЛЬНЫМИ КООРДИНАТАМИ — та, что получается на пересечении прямой с квадрикой.
/// Все три координаты имеют вид (p + q·√r)/s с ОДНИМ И ТЕМ ЖЕ подкоренным: он приходит из
/// различителя одного квадратного уравнения, поэтому общий для всех координат. Это и делает
/// точные сравнения возможными.
///
/// Главное, ради чего точка существует: у неё можно ТОЧНО спросить, с какой стороны плоскости
/// она лежит. Именно на этом вопросе держится всё разбиение пространства, и именно он ломается,
/// если считать такие точки дробными числами.
/// </summary>
public readonly struct QPoint
{
    public readonly SqrtRational X, Y, Z;

    public QPoint(SqrtRational x, SqrtRational y, SqrtRational z) { X = x; Y = y; Z = z; }

    /// <summary>Рациональная точка — частный случай: под корнем ноль.</summary>
    public static QPoint Rational(BigInteger x, BigInteger y, BigInteger z, BigInteger w) =>
        new(SqrtRational.FromRational(x, w), SqrtRational.FromRational(y, w), SqrtRational.FromRational(z, w));

    public bool IsRational => X.IsRational && Y.IsRational && Z.IsRational;

    /// <summary>Приближённые координаты — для показа и сетки, не для решений.</summary>
    public Vector3d Approx => new(X.Approx, Y.Approx, Z.Approx);

    /// <summary>
    /// СТОРОНА ОТНОСИТЕЛЬНО ПЛОСКОСТИ a·x + b·y + c·z + d = 0 с целыми коэффициентами.
    /// Подстановка даёт снова число с корнем, и его знак известен точно — без округления.
    /// </summary>
    public int SideOfPlane(BigInteger a, BigInteger b, BigInteger c, BigInteger d)
    {
        // a·X + b·Y + c·Z + d, приведённое к общему знаменателю.
        BigInteger s = X.S * Y.S * Z.S;

        BigInteger p = a * X.P * (Y.S * Z.S)
                     + b * Y.P * (X.S * Z.S)
                     + c * Z.P * (X.S * Y.S)
                     + d * s;

        BigInteger q = a * X.Q * (Y.S * Z.S)
                     + b * Y.Q * (X.S * Z.S)
                     + c * Z.Q * (X.S * Y.S);

        BigInteger r = !X.R.IsZero ? X.R : !Y.R.IsZero ? Y.R : Z.R;
        return new SqrtRational(p, q, r, s).Sign();
    }

    /// <summary>Совпадают ли точки ТОЧНО.</summary>
    public bool SameAs(QPoint o) =>
        X.CompareTo(o.X) == 0 && Y.CompareTo(o.Y) == 0 && Z.CompareTo(o.Z) == 0;

    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>
/// ПЕРЕСЕЧЕНИЕ ПРЯМОЙ С КВАДРИКОЙ. Прямая задаётся точкой и направлением в целых числах —
/// такими её и даёт пересечение двух плоскостей ядра. Подстановка в уравнение второй степени
/// даёт квадратное уравнение с целыми коэффициентами, и его корни считаются точно.
///
/// Это первые вершины, лежащие на цилиндре ТОЧНО, а не на его хорде. Ради них всё и затевалось:
/// пока резец гранится, вершина отстоит от настоящей поверхности на прогиб (серединное 0.013 мм
/// по ста телам), и от этого расходятся и объёмы, и дуги рёбер.
/// </summary>
public static class QuadricLine
{
    /// <summary>
    /// Точки, где прямая (at + t·dir) встречает квадрику. Ноль, одна (касание) или две.
    /// Координаты точки и знаменатель задаются целыми: точка — (ax/aw, ay/aw, az/aw).
    /// </summary>
    public static QPoint[] Intersect(Quadric q,
                                     BigInteger ax, BigInteger ay, BigInteger az, BigInteger aw,
                                     BigInteger dx, BigInteger dy, BigInteger dz)
    {
        // Подставляем x = (ax + t·dx·aw)/aw и так далее — то есть работаем с точкой
        // (ax, ay, az, aw) и направлением (dx, dy, dz) в однородных координатах.
        //
        // Q(a + t·d) = A·t² + B·t + C, где коэффициенты собираются из коэффициентов квадрики.

        // Часть при t²: только квадратичные члены от направления.
        BigInteger A = q.A * dx * dx + q.B * dy * dy + q.C * dz * dz
                     + q.D * dx * dy + q.E * dx * dz + q.F * dy * dz;

        // Часть при t: смешанные члены точки и направления плюс линейные от направления.
        BigInteger B = 2 * (q.A * ax * dx + q.B * ay * dy + q.C * az * dz)
                     + q.D * (ax * dy + ay * dx)
                     + q.E * (ax * dz + az * dx)
                     + q.F * (ay * dz + az * dy)
                     + (q.G * dx + q.H * dy + q.I * dz) * aw;

        // Свободная часть: значение квадрики в самой точке.
        BigInteger C = q.ValueAt(ax, ay, az, aw);

        // Множители на aw² у A и B приводятся так, чтобы уравнение было в целых:
        // Q(a + t·d) = A·t²·aw² + B·t·aw + C... уравнение делится на aw², но для знака и
        // корней достаточно решить A·aw²·t² + B·aw·t + C = 0 — коэффициенты уже целые.
        var roots = SqrtRational.SolveQuadratic(A * aw * aw, B * aw, C);

        var res = new QPoint[roots.Length];
        for (int i = 0; i < roots.Length; i++) res[i] = At(ax, ay, az, aw, dx, dy, dz, roots[i]);
        return res;
    }

    /// <summary>Точка прямой при заданном значении параметра.</summary>
    private static QPoint At(BigInteger ax, BigInteger ay, BigInteger az, BigInteger aw,
                             BigInteger dx, BigInteger dy, BigInteger dz, SqrtRational t)
    {
        // x = ax/aw + t·dx  ⟹  (ax·t.S + t.P·dx·aw + t.Q·dx·aw·√r) / (aw·t.S).
        SqrtRational Coord(BigInteger a, BigInteger d) =>
            new(a * t.S + t.P * d * aw, t.Q * d * aw, t.R, aw * t.S);

        return new QPoint(Coord(ax, dx), Coord(ay, dy), Coord(az, dz));
    }
}
