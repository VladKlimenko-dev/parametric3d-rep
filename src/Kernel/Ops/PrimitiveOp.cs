using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ЗАГОТОВКИ: шар, конус, тор, цилиндр. Все они — тела вращения, и точная поверхность у них
/// та же, что у вращения: сфера получается вращением полукруга, конус — трапеции, тор — круга.
/// Отдельных классов поверхностей для них не нужно.
/// </summary>
public static class PrimitiveOp
{
    /// <summary>Шар радиуса r с центром в at.</summary>
    public static TSolid Sphere(Vector3d at, double r)
    {
        if (r <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sphere: the radius must be greater than zero.");
        // Профиль: полукруг в плоскости XZ (ось вращения — Z), замкнутый диаметром.
        var plane = new Frame3(at, Vector3d.UnitX, Vector3d.UnitZ);
        var arc = new ArcCurve3(plane, r, -Math.PI / 2, Math.PI / 2);
        var profile = ProfileBuilder.Build(plane, new List<ICurve3>
        {
            arc,
            new LineCurve3(at + new Vector3d(0, 0, r), at - new Vector3d(0, 0, r))
        });
        return RevolveOp.Build(profile, new Frame3(at, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
    }

    /// <summary>Усечённый конус: радиусы r0 у основания и r1 у верха, высота h вдоль Z.</summary>
    public static TSolid Cone(Vector3d at, double r0, double r1, double h)
    {
        if (h <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Cone: the height must be greater than zero.");
        if (r0 <= Tol.Weld && r1 <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Cone: both radii are zero.");
        var plane = new Frame3(at, Vector3d.UnitX, Vector3d.UnitZ);
        Vector3d a = at + new Vector3d(r0, 0, 0);
        Vector3d b = at + new Vector3d(r1, 0, h);
        Vector3d c = at + new Vector3d(0, 0, h);
        var curves = new List<ICurve3> { new LineCurve3(at, a), new LineCurve3(a, b), new LineCurve3(b, c), new LineCurve3(c, at) };
        return RevolveOp.Build(ProfileBuilder.Build(plane, curves), new Frame3(at, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
    }

    /// <summary>Тор: труба радиуса tube по окружности радиуса ring в плоскости XY.</summary>
    public static TSolid Torus(Vector3d at, double ring, double tube)
    {
        if (tube <= Tol.Weld || ring <= tube) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Torus: the tube must be smaller than the ring.");
        var plane = new Frame3(at + new Vector3d(ring, 0, 0), Vector3d.UnitX, Vector3d.UnitZ);
        var circle = new ArcCurve3(plane, tube, 0, 2 * Math.PI);
        return RevolveOp.Build(ProfileBuilder.Build(plane, new List<ICurve3> { circle }),
                               new Frame3(at, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
    }

    /// <summary>Цилиндр радиуса r и высоты h вдоль Z.</summary>
    public static TSolid Cylinder(Vector3d at, double r, double h)
    {
        if (r <= Tol.Weld || h <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Cylinder: the size must be greater than zero.");
        var plane = new Frame3(at, Vector3d.UnitX, Vector3d.UnitY);
        var circle = new ArcCurve3(plane, r, 0, 2 * Math.PI);
        return ExtrudeOp.Build(ProfileBuilder.Build(plane, new List<ICurve3> { circle }), new Vector3d(0, 0, h));
    }
}
