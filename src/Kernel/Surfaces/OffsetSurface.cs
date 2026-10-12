using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// ПОВЕРХНОСТЬ СМЕЩЕНИЯ: исходная поверхность, отодвинутая по своей нормали на заданное
/// расстояние. Нужна оболочке и утолщению: сейчас они работают только там, где смещённую
/// поверхность можно выразить иначе (плоскость — плоскостью, цилиндр — цилиндром), а по
/// свободным формам не работают вовсе.
///
/// Параметры остаются исходными: точка смещённой поверхности отвечает той же паре параметров,
/// что и точка исходной. Поэтому контуры грани переносятся без пересчёта.
///
/// ОГОВОРКА: при смещении внутрь на величину больше наименьшего радиуса кривизны поверхность
/// вырождается и самопересекается. Здесь это не лечится — проверка на вырождение отдаётся
/// операции, которая смещение заказывает.
/// </summary>
public sealed class OffsetSurface : ISurface3
{
    public readonly ISurface3 Base;
    public readonly double Distance;

    public OffsetSurface(ISurface3 baseSurface, double distance)
    {
        Base = baseSurface ?? throw new ArgumentNullException(nameof(baseSurface));
        Distance = distance;
    }

    public Vector3d PointAt(Vector2d uv)
    {
        Vector3d n = Base.NormalAt(uv);
        return Base.PointAt(uv) + (n.LengthSquared < 1e-24 ? Vector3d.Zero : n.Normalized() * Distance);
    }

    /// <summary>
    /// Нормаль совпадает с исходной: смещение по нормали её направления не меняет, пока
    /// поверхность не выродилась.
    /// </summary>
    public Vector3d NormalAt(Vector2d uv) => Base.NormalAt(uv);

    public Vector2d ProjectToUV(Vector3d p)
    {
        // Первое приближение: снять смещение по нормали исходной поверхности в найденной точке,
        // затем повторить — за два-три шага сходится, потому что нормаль меняется медленно.
        Vector2d uv = Base.ProjectToUV(p);
        for (int i = 0; i < 4; i++)
        {
            Vector3d n = Base.NormalAt(uv);
            if (n.LengthSquared < 1e-24) break;
            Vector2d next = Base.ProjectToUV(p - n.Normalized() * Distance);
            if ((next - uv).Length < 1e-9) { uv = next; break; }
            uv = next;
        }
        return uv;
    }

    public bool IsPlanar => Base.IsPlanar;

    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        // Сетка строится исходной поверхностью, а точки сдвигаются: так контуры грани и её
        // дробность остаются теми же, что у исходной.
        var inner = new MeshBuilder();
        Base.TessellateFace(face, inner);

        var pts = inner.PositionsD;
        var idx = inner.IndicesList;
        int start = mb.VertexCount;

        foreach (var q in pts)
        {
            Vector2d uv = Base.ProjectToUV(q);
            Vector3d n = Base.NormalAt(uv);
            Vector3d dir = n.LengthSquared < 1e-24 ? Vector3d.Zero : n.Normalized();
            if (face.Reversed) n = -n;
            mb.AddVertex(q + dir * Distance, n);
        }

        for (int i = 0; i + 2 < idx.Count; i += 3)
            mb.AddTriangle(start + idx[i], start + idx[i + 1], start + idx[i + 2]);
    }

    /// <summary>
    /// Смещение, выраженное ТОЧНО, если это возможно: плоскость даёт плоскость, цилиндр —
    /// цилиндр другого радиуса, сфера и тор — себя же. Такой ответ дешевле и точнее обёртки,
    /// поэтому операции стоит спрашивать его первым.
    /// </summary>
    public static ISurface3? Exact(ISurface3 src, double d)
    {
        switch (src)
        {
            case PlaneSurface pl:
                return new PlaneSurface(new Frame3(pl.F.Origin + pl.F.W.Normalized() * d, pl.F.U, pl.F.V));

            case ExtrudedSurface ex when ex.Base is Curves.ArcCurve3 arc:
            {
                double r = arc.Radius + d;
                if (r <= Tol.Weld) return null;         // смещение съело цилиндр
                return new ExtrudedSurface(new Curves.ArcCurve3(arc.Frame, r, arc.A0, arc.A1), ex.Dir);
            }

            case RevolvedSurface rev when rev.Base is Curves.ArcCurve3 gen:
            {
                // Сфера и тор: смещение меняет радиус образующей.
                double r = gen.Radius + d;
                if (r <= Tol.Weld) return null;
                return new RevolvedSurface(new Curves.ArcCurve3(gen.Frame, r, gen.A0, gen.A1),
                                           rev.Axis, rev.A0, rev.A1);
            }
        }
        return null;
    }
}
