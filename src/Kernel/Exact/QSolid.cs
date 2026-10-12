using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// ТЕЛО ТОЧНОГО ЯДРА, ДОПУСКАЮЩЕЕ КВАДРИКИ: набор граней и оба справочника.
///
/// Прежнее точное тело состояло только из плоских граней, и цилиндр в него не укладывался — его
/// приходилось гранить. Здесь грань может опираться на цилиндр, сферу или конус, и вопрос
/// «точка внутри тела» решается ТОЧНО, целой арифметикой, без допусков.
///
/// Как решается: точка внутри, если она с внутренней стороны каждой ограничивающей поверхности.
/// Для выпуклого тела этого достаточно; для тела общего вида нужен обход разбиения, и это
/// следующий шаг. Здесь сознательно сделана выпуклая часть — она проверяема и на ней строится
/// всё остальное.
/// </summary>
public sealed class QSolid
{
    public readonly PlaneRegistry Planes;
    public readonly QuadricRegistry Quadrics;
    public readonly List<QPolygon> Faces;

    private readonly QSolidTester _tester;

    public QSolid(PlaneRegistry planes, QuadricRegistry quadrics, List<QPolygon> faces)
    {
        Planes = planes;
        Quadrics = quadrics;
        Faces = faces;
        _tester = new QSolidTester(planes, quadrics);
    }

    public bool HasQuadric => Faces.Any(f => f.HasQuadric);

    /// <summary>
    /// Внутри ли точка выпуклого тела: с внутренней стороны каждой несущей поверхности.
    /// Возвращает −1 внутри, 0 на поверхности, +1 снаружи.
    /// </summary>
    public int Where(BigInteger x, BigInteger y, BigInteger z, BigInteger w)
    {
        bool onSurface = false;

        foreach (var f in Faces)
        {
            int s = _tester.Side(x, y, z, w, f.Support);
            if (s > 0) return 1;                 // снаружи хотя бы одной — снаружи тела
            if (s == 0) onSurface = true;
        }

        return onSurface ? 0 : -1;
    }

    /// <summary>То же для точки с иррациональными координатами.</summary>
    public int Where(QPoint p)
    {
        bool onSurface = false;

        foreach (var f in Faces)
        {
            int s = _tester.Side(p, f.Support);
            if (s > 0) return 1;
            if (s == 0) onSurface = true;
        }

        return onSurface ? 0 : -1;
    }

    /// <summary>
    /// ЦИЛИНДР КАК ТОЧНОЕ ТЕЛО: боковая поверхность — квадрика, торцы — плоскости. Ни одной
    /// хорды: прежде такое тело существовало только в виде призмы из 55 граней.
    /// </summary>
    public static QSolid Cylinder(Vector3d at, double radius, double height,
                                  PlaneRegistry? planes = null, QuadricRegistry? quadrics = null)
    {
        planes ??= new PlaneRegistry();
        quadrics ??= new QuadricRegistry();

        long s = 1_000_000;
        BigInteger zLow = new(Math.Round(at.Z * s));
        BigInteger zHigh = new(Math.Round((at.Z + height) * s));
        BigInteger scale = s;

        var side = ExactSurfaceRef.Of(quadrics.Add(Quadric.CylinderAlongZ(at, radius)));
        var low = ExactSurfaceRef.Of(planes.Plane(0, 0, -scale, zLow));      // z ≥ zLow
        var high = ExactSurfaceRef.Of(planes.Plane(0, 0, scale, -zHigh));    // z ≤ zHigh

        var faces = new List<QPolygon>
        {
            new(side, new[] { low, high }, 1),
            new(low, new[] { side }, 2),
            new(high, new[] { side }, 3)
        };

        return new QSolid(planes, quadrics, faces);
    }

    /// <summary>Шар как точное тело: одна квадрика и никаких границ.</summary>
    public static QSolid Sphere(Vector3d centre, double radius,
                                PlaneRegistry? planes = null, QuadricRegistry? quadrics = null)
    {
        planes ??= new PlaneRegistry();
        quadrics ??= new QuadricRegistry();

        var surf = ExactSurfaceRef.Of(quadrics.Add(Quadric.Sphere(centre, radius)));
        return new QSolid(planes, quadrics, new List<QPolygon> { new(surf, Array.Empty<ExactSurfaceRef>(), 1) });
    }

    public override string ToString() =>
        $"точное тело: граней {Faces.Count}" + (HasQuadric ? ", с квадриками" : ", только плоскости");
}
