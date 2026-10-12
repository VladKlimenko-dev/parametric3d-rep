using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model.Features;

/// <summary>
/// KRN-08 (ворота g.09): ЭСКИЗ НА ГРАНИ ИДЁТ ЗА ГРАНЬЮ. Прежде эскиз, поставленный на грань модели, хранил
/// неподвижную плоскость и номер грани, живущий до перестроения: правка высоты бруска уводила грань, а эскиз (и вырез по
/// нему) оставался в воздухе — молча. Теперь эскиз хранит устойчивое имя грани («#ключ/роль») и при каждом перестроении
/// переносит плоскость на текущую грань с этим именем: начало и ось U проецируются на новую плоскость, нормаль — нормаль
/// грани (сторона — как была). Грани нет — явная ошибка; одноимённые куски на разных плоскостях — неоднозначность.
/// Мутация P3D_SKETCHFACE=0 (плоскость не переносится).
/// </summary>
public static class SketchFaceRef
{
    internal static readonly bool Off = Environment.GetEnvironmentVariable("P3D_SKETCHFACE") == "0";

    /// <summary>Плоскость грани (точка, внешняя нормаль) или null, если грань не плоская.</summary>
    public static (Vector3d P, Vector3d N)? PlaneOf(TFace f)
    {
        if (GeometryAudit.AuthoritativeSurface(f) is not PlaneSurface ps) return null;
        return (ps.F.Origin, f.Reversed ? -ps.F.W : ps.F.W);
    }

    /// <summary>Переносит рамку на плоскость (p, n): начало и U проецируются, нормаль — n со стороной старой W.</summary>
    public static Frame3 Onto(Frame3 old, Vector3d p, Vector3d n)
    {
        n = n.Normalized();
        if (Vector3d.Dot(n, old.W) < 0) n = -n;
        var o = old.Origin - n * Vector3d.Dot(old.Origin - p, n);
        var u = old.U - n * Vector3d.Dot(old.U, n);
        if (u.LengthSquared < 1e-12) u = Frame3.FromNormal(o, n).U;
        u = u.Normalized();
        return new Frame3(o, u, Vector3d.Cross(n, u));
    }

    /// <summary>Привязывает эскиз к плоской грани: плоскость — по грани, имя грани запоминается.</summary>
    public static void Attach(Sketch sk, TFace face)
    {
        var pl = PlaneOf(face) ?? throw new ArgumentException("the face is not planar");
        sk.BaseFaceTag = string.IsNullOrEmpty(face.Tag) ? null : face.Tag;
        sk.Plane = Onto(sk.Plane, pl.P, pl.N);
        sk.BaseFrame = Onto(sk.BaseFrame, pl.P, pl.N);
    }

    /// <summary>
    /// Привязка по номеру грани из выбора в виде (номер живёт до перестроения, имя — дольше): грань ищется среди тел,
    /// плоская — привязывается. Не найдена или не плоская (выбрана вспомогательная плоскость) — false, эскиз не трогается.
    /// </summary>
    public static bool AttachById(Sketch sk, IEnumerable<TSolid> bodies, uint faceId)
    {
        if (faceId == 0) return false;
        var face = bodies.SelectMany(b => b.Faces).FirstOrDefault(f => f.Id == faceId);
        if (face == null || PlaneOf(face) == null || string.IsNullOrEmpty(face.Tag)) return false;
        Attach(sk, face);
        return true;
    }

    /// <summary>Переносит плоскость эскиза на текущую грань с его именем (если имя задано).</summary>
    public static void Follow(Sketch sk, BuildContext ctx)
    {
        if (Off || string.IsNullOrEmpty(sk.BaseFaceTag)) return;
        var named = ctx.Bodies.SelectMany(b => b.Faces).Where(f => f.Tag == sk.BaseFaceTag).ToList();
        if (named.Count == 0)
            throw new RebuildException(OperationOutcome.InvalidInput, $"Sketch «{sk.Name}»: the base face '{EdgeRef.Bare(sk.BaseFaceTag)}' no longer exists.");
        var planes = named.Select(f => (F: f, Pl: PlaneOf(f))).ToList();
        if (planes.Any(x => x.Pl == null))
            throw new RebuildException(OperationOutcome.InvalidInput, $"Sketch «{sk.Name}»: the base face '{EdgeRef.Bare(sk.BaseFaceTag)}' is no longer planar.");
        var (p0, n0) = planes[0].Pl!.Value;
        bool Same((Vector3d P, Vector3d N) a) =>
            Math.Abs(Math.Abs(Vector3d.Dot(a.N.Normalized(), n0.Normalized())) - 1) <= 1e-9
            && Math.Abs(Vector3d.Dot(a.P - p0, n0.Normalized())) <= 1e-6;
        if (planes.Any(x => !Same(x.Pl!.Value)))
            throw new AmbiguousReferenceException($"Sketch «{sk.Name}»", planes.Select(x => (x.F.Tag, x.Pl!.Value.P)).ToList(),
                $"{planes.Count} faces named '{EdgeRef.Bare(sk.BaseFaceTag)}' lie on different planes");
        sk.Plane = Onto(sk.Plane, p0, n0);
        sk.BaseFrame = Onto(sk.BaseFrame, p0, n0);
    }
}
