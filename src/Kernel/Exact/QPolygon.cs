using System.Numerics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// Ссылка на поверхность точного ядра: плоскость ИЛИ квадрика. Хранит сторону, как и прежние
/// ссылки на плоскость.
/// </summary>
public readonly struct ExactSurfaceRef
{
    public readonly OPlane Plane;
    public readonly OQuadric Quadric;
    public readonly bool IsQuadric;

    private ExactSurfaceRef(OPlane plane) { Plane = plane; Quadric = default; IsQuadric = false; }
    private ExactSurfaceRef(OQuadric quadric) { Plane = default; Quadric = quadric; IsQuadric = true; }

    public static ExactSurfaceRef Of(OPlane p) => new(p);
    public static ExactSurfaceRef Of(OQuadric q) => new(q);

    public ExactSurfaceRef Flipped => IsQuadric ? Of(Quadric.Flipped) : Of(Plane.Flipped);

    public override string ToString() => IsQuadric ? $"квадрика {Quadric.Id}×{Quadric.Side}"
                                                   : $"плоскость {Plane.Id}×{Plane.Side}";
}

/// <summary>
/// ГРАНЬ ТОЧНОГО ЯДРА, ДОПУСКАЮЩАЯ КВАДРИКИ. Прежний многоугольник опирался только на
/// плоскости: и несущая поверхность, и все границы. Из-за этого цилиндрический резец приходилось
/// гранить, а тело с настоящей цилиндрической гранью в точное представление не укладывалось
/// вовсе — и последующие операции над ним не работали (измерено: скругление по кромке такого
/// отверстия не строится).
///
/// Здесь и несущая поверхность, и границы — ссылки, каждая из которых может быть плоскостью или
/// квадрикой. Всё остальное устройство сохраняется: грань задаётся пересечением полупространств,
/// и вопрос «лежит ли точка внутри» решается предикатами, а не перебором вершин.
/// </summary>
public sealed class QPolygon
{
    public readonly ExactSurfaceRef Support;
    public readonly ExactSurfaceRef[] Bounds;
    public readonly uint Tag;

    public QPolygon(ExactSurfaceRef support, ExactSurfaceRef[] bounds, uint tag = 0)
    {
        Support = support;
        Bounds = bounds ?? Array.Empty<ExactSurfaceRef>();
        Tag = tag;
    }

    public bool HasQuadric
    {
        get
        {
            if (Support.IsQuadric) return true;
            foreach (var b in Bounds) if (b.IsQuadric) return true;
            return false;
        }
    }

    public QPolygon Flipped() =>
        new(Support.Flipped, Bounds.Select(b => b).ToArray(), Tag);

    public override string ToString() => $"грань на {Support}, границ {Bounds.Length}";
}

/// <summary>
/// Предикаты для граней, допускающих квадрики. Отделены от самой грани: она описывает, ЧЕМ
/// ограничена, а как это проверить — дело справочников.
/// </summary>
public sealed class QSolidTester
{
    private readonly PlaneRegistry _planes;
    private readonly QuadricRegistry _quadrics;

    public QSolidTester(PlaneRegistry planes, QuadricRegistry quadrics)
    {
        _planes = planes;
        _quadrics = quadrics;
    }

    /// <summary>Сторона рациональной точки относительно ссылки на поверхность.</summary>
    public int Side(BigInteger x, BigInteger y, BigInteger z, BigInteger w, ExactSurfaceRef s)
    {
        if (s.IsQuadric) return _quadrics.Side(x, y, z, w, s.Quadric);

        // Плоскость: подстановка рациональной точки, знак — в целой арифметике.
        var (a, b, c, d) = _planes.Coefficients(s.Plane.Id);
        return s.Plane.Side * (a * x + b * y + c * z + d * w).Sign;
    }

    /// <summary>Сторона точки с иррациональными координатами.</summary>
    public int Side(QPoint p, ExactSurfaceRef s)
    {
        if (s.IsQuadric) return _quadrics.Side(p, s.Quadric);

        var (a, b, c, d) = _planes.Coefficients(s.Plane.Id);
        return s.Plane.Side * p.SideOfPlane(a, b, c, d);
    }

    /// <summary>
    /// Лежит ли ТОЧКА в пределах грани: на её несущей поверхности и внутри всех границ.
    /// Допуск здесь не нужен — все ответы точны.
    /// </summary>
    public bool Contains(BigInteger x, BigInteger y, BigInteger z, BigInteger w, QPolygon face)
    {
        if (Side(x, y, z, w, face.Support) != 0) return false;
        foreach (var b in face.Bounds)
            if (Side(x, y, z, w, b) > 0) return false;
        return true;
    }

    /// <summary>То же для точки с корнем.</summary>
    public bool Contains(QPoint p, QPolygon face)
    {
        if (Side(p, face.Support) != 0) return false;
        foreach (var b in face.Bounds)
            if (Side(p, b) > 0) return false;
        return true;
    }
}
