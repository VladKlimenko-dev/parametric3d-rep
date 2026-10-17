using OpenTK.Mathematics;
using Parametric3D.Kernel;

namespace Parametric3D.Model;

/// <summary>Способ построения аналитической плоскости.</summary>
public enum DatumPlaneMode
{
    Standard,          // XY / XZ / YZ детали
    ThreePoints,
    LineAndPoint,
    OffsetFromFace,    // грань/плоскость + расстояние
    ParallelThroughPoint,
    AngleToFace,       // грань + ребро (ось) + угол
    NormalToLine       // u12: перпендикулярно прямой через точку
}

public sealed class DatumPlane : ModelItem
{
    public override ItemKind Kind => ItemKind.DatumPlane;

    public Frame3 Frame = Frame3.XY;
    public DatumPlaneMode Mode = DatumPlaneMode.Standard;

    /// <summary>Ссылки на исходную геометрию (id граней/рёбер/точек) для перестроения.</summary>
    public readonly List<uint> References = new();
    public double Offset;
    public double Angle;
    public bool Flip;

    /// <summary>
    /// Полуразмер квадрата, которым плоскость показывается в виде (мм).
    /// Это МИНИМУМ: если в детали или сборке уже есть геометрия, плоскость
    /// растягивается так, чтобы её охватить (см. RenderScene.DatumHalfSize).
    /// </summary>
    public double DisplaySize = 60;

    /// <summary>
    /// Фактический ПРЯМОУГОЛЬНИК плоскости в её собственных координатах,
    /// вычисленный при последней синхронизации сцены. Хранится здесь,
    /// чтобы отрисовка и выбор мышью пользовались одним и тем же
    /// прямоугольником.
    ///
    /// Прямоугольник, а не полуразмер. Раньше хранился полуразмер, то есть
    /// КВАДРАТ, симметричный относительно нуля. Тело же обычно лежит
    /// в стороне от нуля — выдавленный из начала координат кубоид целиком
    /// в положительной четверти. Симметричному квадрату приходилось
    /// растягиваться до самой дальней координаты И в обратную сторону,
    /// поэтому плоскость выходила больше тела вдвое с лишним.
    /// </summary>
    public Vector2d EffectiveMin = new(-60, -60);
    public Vector2d EffectiveMax = new(60, 60);

    /// <summary>Наибольшее удаление края плоскости от её нуля.</summary>
    public double EffectiveSize => Math.Max(
        Math.Max(Math.Abs(EffectiveMin.X), Math.Abs(EffectiveMax.X)),
        Math.Max(Math.Abs(EffectiveMin.Y), Math.Abs(EffectiveMax.Y)));

    public DatumPlane(string name, Frame3 frame)
    {
        Name = name;
        Frame = frame;
    }

    public Vector3[] DisplayCorners()
    {
        double u0 = EffectiveMin.X, u1 = EffectiveMax.X;
        double v0 = EffectiveMin.Y, v1 = EffectiveMax.Y;

        if (u1 - u0 < 1e-6) { u0 -= 1e-3; u1 += 1e-3; }
        if (v1 - v0 < 1e-6) { v0 -= 1e-3; v1 += 1e-3; }

        var pts = new[]
        {
            Frame.ToWorld(u0, v0), Frame.ToWorld(u1, v0),
            Frame.ToWorld(u1, v1), Frame.ToWorld(u0, v1)
        };

        var r = new Vector3[4];
        for (int i = 0; i < 4; i++) r[i] = new Vector3((float)pts[i].X, (float)pts[i].Y, (float)pts[i].Z);
        return r;
    }

    /// <summary>Задаёт прямоугольник плоскости.</summary>
    public void SetExtent(Vector2d min, Vector2d max)
    {
        EffectiveMin = min;
        EffectiveMax = max;
    }

    /// <param name="halfSize">Полуразмер квадрата в единицах модели.</param>
    public Vector3[] DisplayCorners(double halfSize)
    {
        double s = Math.Max(1e-6, halfSize);
        var pts = new[]
        {
            Frame.ToWorld(-s, -s), Frame.ToWorld(s, -s),
            Frame.ToWorld(s, s),   Frame.ToWorld(-s, s)
        };
        return pts.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToArray();
    }
}

public sealed class DatumAxis : ModelItem
{
    public override ItemKind Kind => ItemKind.DatumAxis;
    public Vector3d Origin;
    public Vector3d Direction = Vector3d.UnitZ;
    public double DisplayLength = 80;
    public readonly List<uint> References = new();

    public DatumAxis(string name, Vector3d origin, Vector3d dir)
    {
        Name = name; Origin = origin; Direction = dir.Normalized();
    }
}

public sealed class DatumPoint : ModelItem
{
    public override ItemKind Kind => ItemKind.DatumPoint;
    public Vector3d P;
    public readonly List<uint> References = new();
    public DatumPoint(string name, Vector3d p) { Name = name; P = p; }
}

/// <summary>Линия в пространстве детали/сборки (не элемент эскиза).</summary>
public sealed class Line3DItem : ModelItem
{
    public override ItemKind Kind => ItemKind.DatumAxis;
    public Vector3d A, B;
    public Line3DItem(string name, Vector3d a, Vector3d b) { Name = name; A = a; B = b; }
}
