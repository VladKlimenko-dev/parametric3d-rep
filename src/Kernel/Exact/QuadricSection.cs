using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Exact;

/// <summary>Что получилось в сечении квадрики плоскостью.</summary>
public enum SectionKind
{
    /// <summary>Пусто: плоскость не задевает поверхность.</summary>
    Empty,
    /// <summary>Одна точка: плоскость касается.</summary>
    Point,
    /// <summary>Окружность — единственный случай, который выражается точно в рациональных.</summary>
    Circle,
    /// <summary>Эллипс: оси иррациональны, хранится приближённо.</summary>
    Ellipse,
    /// <summary>Две прямые: плоскость вдоль оси цилиндра.</summary>
    TwoLines,
    /// <summary>Одна прямая: плоскость касается цилиндра вдоль образующей.</summary>
    OneLine,
    /// <summary>Вид не разобран: для общей квадрики и наклонной плоскости.</summary>
    Unknown
}

/// <summary>
/// СЕЧЕНИЕ КВАДРИКИ ПЛОСКОСТЬЮ. Это первый шаг к булевой без гранения: чтобы резать цилиндром,
/// а не 55-гранной призмой, надо уметь находить след цилиндра на каждой грани.
///
/// Важно различать, что считается ТОЧНО, а что лишь приближённо:
///
///   — цилиндр, пересечённый плоскостью ПЕРПЕНДИКУЛЯРНО оси, даёт окружность: её центр и
///     КВАДРАТ радиуса рациональны, то есть представимы целыми числами ядра без потери;
///   — плоскость ВДОЛЬ оси даёт две прямые (или одну при касании) — тоже точно;
///   — наклонная плоскость даёт эллипс, у которого полуоси уже иррациональны;
///   — сфера даёт окружность при любой плоскости, но её радиус иррационален, если плоскость не
///     проходит через центр.
///
/// Радиус хранится КВАДРАТОМ именно поэтому: √ вводит иррациональность, а квадрат её не имеет,
/// и все проверки («лежит ли точка на окружности») остаются точными.
/// </summary>
public readonly struct QuadricSection
{
    public readonly SectionKind Kind;

    /// <summary>Центр окружности или эллипса; для прямых — точка на первой из них.</summary>
    public readonly Vector3d Centre;

    /// <summary>Квадрат радиуса окружности (точен для цилиндра, перпендикулярного плоскости).</summary>
    public readonly double RadiusSquared;

    /// <summary>Нормаль плоскости сечения.</summary>
    public readonly Vector3d Normal;

    /// <summary>Направление прямых (для двух прямых) и смещение второй от первой.</summary>
    public readonly Vector3d LineDir;
    public readonly Vector3d LineOffset;

    private QuadricSection(SectionKind kind, Vector3d centre, double r2, Vector3d normal,
                           Vector3d dir = default, Vector3d offset = default)
    {
        Kind = kind; Centre = centre; RadiusSquared = r2; Normal = normal;
        LineDir = dir; LineOffset = offset;
    }

    public double Radius => RadiusSquared <= 0 ? 0 : Math.Sqrt(RadiusSquared);

    public override string ToString() => Kind switch
    {
        SectionKind.Circle => $"окружность в {Centre}, радиус {Radius:0.####}",
        SectionKind.Ellipse => $"эллипс в {Centre}",
        SectionKind.TwoLines => $"две прямые вдоль {LineDir}",
        SectionKind.OneLine => $"прямая вдоль {LineDir}",
        SectionKind.Point => $"точка {Centre}",
        SectionKind.Empty => "пусто",
        _ => "не разобрано"
    };

    /// <summary>
    /// Сечение цилиндра, ось которого вдоль Z, плоскостью z = value: всегда окружность того же
    /// радиуса. Считается точно.
    /// </summary>
    public static QuadricSection CylinderZByPlaneZ(Vector3d axisAt, double radius, double z) =>
        new(SectionKind.Circle, new Vector3d(axisAt.X, axisAt.Y, z), radius * radius, Vector3d.UnitZ);

    /// <summary>
    /// Сечение цилиндра вдоль Z плоскостью, ПАРАЛЛЕЛЬНОЙ оси: x = value. Даёт две прямые,
    /// одну (касание) или ничего — смотря по расстоянию от оси.
    /// </summary>
    public static QuadricSection CylinderZByPlaneX(Vector3d axisAt, double radius, double x)
    {
        double dx = x - axisAt.X;
        double d2 = radius * radius - dx * dx;

        if (d2 < 0) return new QuadricSection(SectionKind.Empty, Vector3d.Zero, 0, Vector3d.UnitX);
        if (Math.Abs(d2) < 1e-18)
            return new QuadricSection(SectionKind.OneLine, new Vector3d(x, axisAt.Y, axisAt.Z), 0,
                                      Vector3d.UnitX, Vector3d.UnitZ);

        double dy = Math.Sqrt(d2);
        return new QuadricSection(SectionKind.TwoLines,
                                  new Vector3d(x, axisAt.Y - dy, axisAt.Z), 0, Vector3d.UnitX,
                                  Vector3d.UnitZ, new Vector3d(0, 2 * dy, 0));
    }

    /// <summary>
    /// Сечение сферы плоскостью: окружность, точка или пусто. КВАДРАТ радиуса считается точно
    /// (r² − h²), сам радиус — уже приближённо.
    /// </summary>
    public static QuadricSection SphereByPlane(Vector3d centre, double radius, Vector3d planeAt, Vector3d normal)
    {
        if (normal.LengthSquared < 1e-18) return new QuadricSection(SectionKind.Unknown, Vector3d.Zero, 0, normal);
        Vector3d n = normal.Normalized();

        double h = Vector3d.Dot(centre - planeAt, n);          // расстояние от центра до плоскости
        double r2 = radius * radius - h * h;

        if (r2 < -1e-18) return new QuadricSection(SectionKind.Empty, Vector3d.Zero, 0, n);

        Vector3d at = centre - n * h;                          // проекция центра на плоскость
        if (r2 <= 1e-18) return new QuadricSection(SectionKind.Point, at, 0, n);

        return new QuadricSection(SectionKind.Circle, at, r2, n);
    }

    /// <summary>
    /// Сечение цилиндра вдоль Z НАКЛОННОЙ плоскостью: эллипс. Малая полуось равна радиусу,
    /// большая — радиусу, делённому на косинус наклона; обе выражаются через корень, поэтому
    /// сечение помечается эллипсом и хранится приближённо.
    /// </summary>
    public static QuadricSection CylinderZByTiltedPlane(Vector3d axisAt, double radius,
                                                        Vector3d planeAt, Vector3d normal)
    {
        if (normal.LengthSquared < 1e-18) return new QuadricSection(SectionKind.Unknown, Vector3d.Zero, 0, normal);
        Vector3d n = normal.Normalized();

        double cos = Math.Abs(Vector3d.Dot(n, Vector3d.UnitZ));
        if (cos <= 1e-12) return CylinderZByPlaneX(axisAt, radius, planeAt.X);   // плоскость вдоль оси
        if (cos >= 1 - 1e-12) return CylinderZByPlaneZ(axisAt, radius, planeAt.Z);

        // Центр эллипса — точка оси на плоскости.
        double t = Vector3d.Dot(planeAt - axisAt, n) / Vector3d.Dot(Vector3d.UnitZ, n);
        Vector3d at = axisAt + Vector3d.UnitZ * t;

        return new QuadricSection(SectionKind.Ellipse, at, radius * radius, n);
    }

    /// <summary>
    /// ЭЛЛИПС В СЕЧЕНИИ НАКЛОННОЙ ПЛОСКОСТИ. Полуоси известны точно по смыслу: малая равна
    /// радиусу цилиндра (в направлении, перпендикулярном и оси, и нормали плоскости), большая —
    /// радиусу, делённому на косинус наклона.
    ///
    /// Возвращает центр, оба направления полуосей и их длины. Направления нормированы: они
    /// нужны для построения кривой, а не для точных сравнений, поэтому корень здесь допустим —
    /// в отличие от предиката стороны, который остаётся целочисленным.
    /// </summary>
    public static (Vector3d Centre, Vector3d Major, Vector3d Minor, double A, double B)?
        CylinderEllipse(Vector3d axisAt, Vector3d axisDir, double radius, Vector3d planeAt, Vector3d planeNormal)
    {
        if (axisDir.LengthSquared < 1e-18 || planeNormal.LengthSquared < 1e-18) return null;

        Vector3d d = axisDir.Normalized();
        Vector3d n = planeNormal.Normalized();

        double cos = Vector3d.Dot(d, n);
        if (Math.Abs(cos) < 1e-12) return null;              // плоскость вдоль оси: не эллипс

        // Центр — точка оси на плоскости.
        double t = Vector3d.Dot(planeAt - axisAt, n) / cos;
        Vector3d centre = axisAt + d * t;

        // Малая полуось — вдоль общего перпендикуляра оси и нормали.
        Vector3d minor = Vector3d.Cross(d, n);
        if (minor.LengthSquared < 1e-18)
        {
            // Ось перпендикулярна плоскости: сечение — окружность, обе полуоси равны.
            Vector3d any = Math.Abs(d.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
            Vector3d u = Vector3d.Cross(d, any).Normalized();
            return (centre, u, Vector3d.Cross(d, u).Normalized(), radius, radius);
        }

        minor = minor.Normalized();
        Vector3d major = Vector3d.Cross(n, minor).Normalized();

        return (centre, major, minor, radius / Math.Abs(cos), radius);
    }

    /// <summary>
    /// ТОЧНАЯ ПРОВЕРКА: лежит ли рациональная точка на окружности сечения. Сравниваются
    /// КВАДРАТЫ расстояний, поэтому корня не возникает и ответ не зависит от округления.
    /// </summary>
    public static bool OnCircleExact(BigInteger x, BigInteger y, BigInteger z, BigInteger w,
                                     BigInteger cx, BigInteger cy, BigInteger cz, BigInteger cw,
                                     BigInteger r2Num, BigInteger r2Den)
    {
        // (x/w − cx/cw)² + … = r2Num/r2Den  ⟺  сравнение целых после приведения.
        BigInteger dx = x * cw - cx * w;
        BigInteger dy = y * cw - cy * w;
        BigInteger dz = z * cw - cz * w;
        BigInteger left = (dx * dx + dy * dy + dz * dz) * r2Den;
        BigInteger right = r2Num * w * w * cw * cw;
        return left == right;
    }
}
