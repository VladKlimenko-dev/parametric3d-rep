using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model.Features;

/// <summary>
/// УКЛОН — операция дерева. В ядре она была, но из модели ею воспользоваться было нельзя.
///
/// Грани задаются точками на них, нейтральная плоскость — точкой и нормалью: числами, а не
/// номерами граней, которые меняются при перестроении.
/// </summary>
public sealed class DraftFeature : Feature
{
    /// <summary>Точки на гранях, которые наклоняются.</summary>
    public readonly List<Vector3d> OnFaces = new();

    /// <summary>K05: устойчивые имена граней уклона (запоминаются при первом выборе по точке).</summary>
    public readonly List<string?> OnFaceNames = new();

    /// <summary>KRN-08 (08.3): грани уклона по роли — разрешаются при каждой перестройке.</summary>
    public readonly List<FaceSelector> OnSelectors = new();

    /// <summary>Нейтральная плоскость: сечение, которое остаётся на месте.</summary>
    public Vector3d NeutralPoint;
    public Vector3d NeutralNormal = Vector3d.UnitZ;

    public ValueRef Angle = new(3);

    public DraftFeature(string name = "Draft") { Name = name; }

    public override string Summary => $"Draft {Angle}\u00b0 ({OnFaces.Count} faces)";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: no body.");
        if (OnFaces.Count == 0 && OnSelectors.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: no faces selected.");
        if (NeutralNormal.LengthSquared < 1e-18)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: the neutral plane is not set.");

        double deg = Angle.Resolve(ctx.Parameters);
        if (Math.Abs(deg) < 1e-9) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: the angle must not be zero.");

        bool drafted = false;
        var faces = OnFaces.Count > 0 ? FaceRefs.Resolve(ctx.Bodies[0], OnFaces, OnFaceNames, "Draft") : new List<Vector3d>();
        foreach (var sel in OnSelectors)
        {
            var fs = sel.Resolve(ctx.Bodies);
            if (fs.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Draft: the selector {sel} selects no faces.");
            faces.AddRange(fs.Select(FaceRefs.Inside));
        }
        Kernel.OperationContract.Apply("Draft", ctx.Bodies, b => drafted = DraftOp.Apply(b, faces, NeutralPoint, NeutralNormal.Normalized(), deg, ctx.Warnings));
        if (!drafted)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Draft: the faces could not be drafted.");
    }
}

/// <summary>
/// РЕБРО ЖЁСТКОСТИ по эскизу: незамкнутый контур наращивается в заданную сторону до встречи с
/// телом и утолщается.
/// </summary>
public sealed class RibFeature : Feature
{
    public uint SketchId;

    /// <summary>Направление наращивания (обычно вниз, к телу).</summary>
    public Vector3d Grow = -Vector3d.UnitZ;

    public ValueRef Thickness = new(3);

    /// <summary>Предел наращивания, чтобы ребро не уходило в бесконечность.</summary>
    public ValueRef MaxLength = new(1000);

    public RibFeature(string name = "Rib") { Name = name; }

    public override string Summary => $"Rib {Thickness}";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: no body.");

        var sketch = ctx.FindSketch(SketchId)
                     ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: the sketch is not found.");

        double t = Thickness.Resolve(ctx.Parameters);
        if (t <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: the thickness must be greater than zero.");
        if (Grow.LengthSquared < 1e-18) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: the direction is not set.");

        var profile = sketch.ToProfile();
        bool built = false;
        if (profile.IsEmpty)
        {
            // K09: открытый эскиз (отрезок, дуга) — свободная кромка ребра.
            var edge = sketch.Entities.Where(e => e.Role == SkRole.Object && e is not SkPoint).Select(e => e.ToCurve3(sketch.Plane)).ToList();
            Kernel.OperationContract.Apply("Rib", ctx.Bodies, b => built = RibOp.BuildOpen(b, sketch.Plane, edge, Grow.Normalized(), t, MaxLength.Resolve(ctx.Parameters), ctx.Warnings));
        }
        else
            Kernel.OperationContract.Apply("Rib", ctx.Bodies, b => built = RibOp.Build(b, profile, Grow.Normalized(), t, MaxLength.Resolve(ctx.Parameters), ctx.Warnings));
        if (!built)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Rib: the rib could not be built. " + string.Join(" ", ctx.Warnings.Where(w => w.StartsWith("Rib:")).TakeLast(1)));
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }
}
