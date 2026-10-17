using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model;

/// <summary>
/// ПОСТРОЕНИЕ ВСПОМОГАТЕЛЬНОЙ ГЕОМЕТРИИ по привязкам, как в Creo.
///
/// Прежде плоскость, ось и точка умели только ХРАНИТЬ своё положение: задать их можно было
/// лишь числами. Здесь — способы построения от существующей геометрии: через три точки, со
/// смещением, под углом, по касательной, по пересечению и так далее.
///
/// Привязки передаются геометрией (точка, прямая, плоскость, цилиндр), а не номерами элементов:
/// так построение одинаково работает и от грани тела, и от другой вспомогательной геометрии, и
/// проверяется без оконной части.
///
/// Каждый способ ОТКАЗЫВАЕТСЯ, когда привязок не хватает для однозначного положения: плоскость,
/// параллельная другой, без точки не определена, а касательная к цилиндру — без направления.
/// Молча выбрать одно из решений хуже, чем сказать, чего не хватает.
/// </summary>
public static class DatumBuilder
{
    // ------------------------------------------------------------------ плоскости

    /// <summary>Через три точки. Отказ, если точки на одной прямой.</summary>
    public static Frame3 PlaneThroughPoints(Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d n = Vector3d.Cross(b - a, c - a);
        if (n.LengthSquared < 1e-18)
            throw new InvalidOperationException("Плоскость через три точки: точки лежат на одной прямой.");
        return Frame3.FromNormal(a, n.Normalized());
    }

    /// <summary>Через прямую и точку вне её.</summary>
    public static Frame3 PlaneThroughLineAndPoint(Vector3d lineAt, Vector3d lineDir, Vector3d p)
    {
        if (lineDir.LengthSquared < 1e-18)
            throw new InvalidOperationException("Плоскость через прямую и точку: направление прямой не задано.");
        Vector3d n = Vector3d.Cross(lineDir, p - lineAt);
        if (n.LengthSquared < 1e-18)
            throw new InvalidOperationException("Плоскость через прямую и точку: точка лежит на прямой.");
        return Frame3.FromNormal(lineAt, n.Normalized());
    }

    /// <summary>Смещённая от плоскости на расстояние (по её нормали).</summary>
    public static Frame3 PlaneOffset(Frame3 from, double distance) =>
        new(from.Origin + from.W * distance, from.U, from.V);

    /// <summary>Параллельная плоскости через точку: точка и задаёт положение.</summary>
    public static Frame3 PlaneParallelThroughPoint(Frame3 from, Vector3d through) =>
        new(through, from.U, from.V);

    /// <summary>
    /// Перпендикулярная прямой, проходящая через точку: прямая задаёт нормаль.
    /// </summary>
    public static Frame3 PlaneNormalToLine(Vector3d lineDir, Vector3d through)
    {
        if (lineDir.LengthSquared < 1e-18)
            throw new InvalidOperationException("Плоскость перпендикулярно прямой: направление не задано.");
        return Frame3.FromNormal(through, lineDir.Normalized());
    }

    /// <summary>
    /// Под углом к плоскости с поворотом вокруг оси. Ось обязана лежать в плоскости: иначе
    /// положение не определено.
    /// </summary>
    public static Frame3 PlaneAtAngle(Frame3 from, Vector3d axisAt, Vector3d axisDir, double degrees)
    {
        if (axisDir.LengthSquared < 1e-18)
            throw new InvalidOperationException("Плоскость под углом: ось поворота не задана.");
        Vector3d k = axisDir.Normalized();
        if (Math.Abs(Vector3d.Dot(k, from.W)) > 1e-6)
            throw new InvalidOperationException("Плоскость под углом: ось поворота не лежит в плоскости.");

        double a = degrees * Math.PI / 180;
        Vector3d n = from.W * Math.Cos(a) + Vector3d.Cross(k, from.W) * Math.Sin(a);
        return Frame3.FromNormal(axisAt, n.Normalized());
    }

    /// <summary>
    /// Касательная к цилиндру и параллельная направлению: без второй привязки касательных
    /// плоскостей бесконечно много, поэтому направление обязательно.
    /// </summary>
    public static Frame3 PlaneTangentToCylinder(Vector3d axisAt, Vector3d axisDir, double radius,
                                                Vector3d towards)
    {
        if (axisDir.LengthSquared < 1e-18 || radius <= Tol.Weld)
            throw new InvalidOperationException("Касательная плоскость: цилиндр задан неполно.");

        Vector3d k = axisDir.Normalized();
        Vector3d side = towards - k * Vector3d.Dot(towards, k);
        if (side.LengthSquared < 1e-18)
            throw new InvalidOperationException("Касательная плоскость: направление совпадает с осью — точка касания не определена.");

        side = side.Normalized();
        return Frame3.FromNormal(axisAt + side * radius, side);
    }

    /// <summary>Касательная к сфере в точке, заданной направлением от центра.</summary>
    public static Frame3 PlaneTangentToSphere(Vector3d centre, double radius, Vector3d towards)
    {
        if (radius <= Tol.Weld || towards.LengthSquared < 1e-18)
            throw new InvalidOperationException("Касательная плоскость: сфера или направление заданы неполно.");
        Vector3d n = towards.Normalized();
        return Frame3.FromNormal(centre + n * radius, n);
    }

    // ------------------------------------------------------------------ оси

    /// <summary>Перпендикулярно плоскости в точке.</summary>
    public static (Vector3d At, Vector3d Dir) AxisNormalToPlane(Frame3 plane, Vector3d through) =>
        (through, plane.W);

    /// <summary>Через две точки.</summary>
    public static (Vector3d At, Vector3d Dir) AxisThroughPoints(Vector3d a, Vector3d b)
    {
        Vector3d d = b - a;
        if (d.LengthSquared < 1e-18)
            throw new InvalidOperationException("Ось через две точки: точки совпадают.");
        return (a, d.Normalized());
    }

    /// <summary>
    /// По оси цилиндрической или конической грани — самый частый способ. Берётся из самой
    /// поверхности, а не из её сетки.
    /// </summary>
    public static (Vector3d At, Vector3d Dir) AxisOfFace(TFace face)
    {
        var src = Kernel.Topology.GeometryAudit.AuthoritativeSurface(face) ?? (face.Surface is Kernel.Surfaces.FacetedSurface fs ? fs.Source : face.Surface);
        switch (src)
        {
            case Kernel.Surfaces.ExtrudedSurface ex when ex.Base is ArcCurve3 arc:
                return (arc.Frame.Origin, ex.Dir.Normalized());
            case Kernel.Surfaces.RevolvedSurface rev:
                return (rev.Axis.Origin, rev.Axis.W);
            // u13: цилиндр как поверхность (лента скругления, точная грань после булевых)
            case Kernel.Surfaces.CylinderSurface cs:
                return (cs.Axis.Origin, cs.Axis.W.Normalized());
            default:
                throw new InvalidOperationException("Ось грани: грань не цилиндрическая и не коническая.");
        }
    }

    /// <summary>По пересечению двух плоскостей. Отказ, если они параллельны.</summary>
    public static (Vector3d At, Vector3d Dir) AxisOfPlanes(Frame3 a, Frame3 b)
    {
        Vector3d d = Vector3d.Cross(a.W, b.W);
        if (d.LengthSquared < 1e-12)
            throw new InvalidOperationException("Ось по пересечению: плоскости параллельны.");
        d = d.Normalized();

        // Точка на линии пересечения: решение двух плоскостных уравнений в направлении,
        // перпендикулярном обеим нормалям.
        double da = Vector3d.Dot(a.W, a.Origin), db = Vector3d.Dot(b.W, b.Origin);
        Vector3d p = (Vector3d.Cross(b.W, d) * da + Vector3d.Cross(d, a.W) * db) / d.LengthSquared;
        return (p, d);
    }

    /// <summary>Касательно к кривой в точке по параметру.</summary>
    public static (Vector3d At, Vector3d Dir) AxisTangentToCurve(ICurve3 curve, double t)
    {
        double tt = curve.T0 + (curve.T1 - curve.T0) * Math.Clamp(t, 0, 1);
        Vector3d d = curve.TangentAt(tt);
        if (d.LengthSquared < 1e-18)
            throw new InvalidOperationException("Ось по касательной: касательная в этой точке не определена.");
        return (curve.PointAt(tt), d.Normalized());
    }

    // ------------------------------------------------------------------ точки

    /// <summary>На кривой по доле длины (0 — начало, 1 — конец).</summary>
    public static Vector3d PointOnCurve(ICurve3 curve, double ratio) =>
        curve.PointAt(curve.T0 + (curve.T1 - curve.T0) * Math.Clamp(ratio, 0, 1));

    /// <summary>На поверхности по её параметрам.</summary>
    public static Vector3d PointOnSurface(Kernel.Surfaces.ISurface3 surface, Vector2d uv) =>
        surface.PointAt(uv);

    /// <summary>Центр дуги или окружности.</summary>
    public static Vector3d PointAtCentre(ICurve3 curve) => curve switch
    {
        ArcCurve3 a => a.Frame.Origin,
        _ => throw new InvalidOperationException("Центр: кривая не дуга и не окружность.")
    };

    /// <summary>Пересечение прямой и плоскости. Отказ, если прямая параллельна плоскости.</summary>
    public static Vector3d PointLineMeetsPlane(Vector3d lineAt, Vector3d lineDir, Frame3 plane)
    {
        double den = Vector3d.Dot(lineDir, plane.W);
        if (Math.Abs(den) < 1e-12)
            throw new InvalidOperationException("Пересечение: прямая параллельна плоскости.");
        double t = Vector3d.Dot(plane.Origin - lineAt, plane.W) / den;
        return lineAt + lineDir * t;
    }

    /// <summary>
    /// Смещение от системы координат: набор точек по таблице координат — так в Creo задают
    /// сразу группу точек.
    /// </summary>
    public static List<Vector3d> PointsFromCsys(Frame3 csys, IEnumerable<Vector3d> local)
    {
        var res = new List<Vector3d>();
        foreach (var p in local)
            res.Add(csys.Origin + csys.U * p.X + csys.V * p.Y + csys.W * p.Z);
        return res;
    }
}
