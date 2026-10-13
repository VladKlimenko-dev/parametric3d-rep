using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>Вращение эскиза вокруг осевой линии эскиза или базовой оси.</summary>
public sealed class RevolveFeature : Feature
{
    public uint SketchId;
    /// <summary>Явно заданная ось (DatumAxis). 0 — берётся осевая линия эскиза.</summary>
    public uint AxisId;

    /// <summary>
    /// Отрезок эскиза, выбранный как ось вращения. 0 — не выбран.
    ///
    /// Нужен, когда осевой линии в эскизе нет: пользователь указывает
    /// любой сегмент, и вращение идёт вокруг прямой, которую он задаёт.
    /// Длина отрезка при этом безразлична.
    /// </summary>
    public uint AxisEntityId;

    public ValueRef StartAngle = new(0);
    public ValueRef EndAngle = new(360);

    public RevolveFeature(string name = "Revolve") { Name = name; }

    public override string Summary => $"{(Cut ? "Revolve cut" : "Revolve")} {StartAngle}\u00b0..{EndAngle}\u00b0";

    public override void Execute(BuildContext ctx)
    {
        var sketch = ctx.FindSketch(SketchId)
                     ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Revolve: sketch not found.");

        sketch.UpdateDimensionsFromParameters(ctx.Parameters!);
        sketch.Solve(ctx.Solver);
        sketch.RequireSolved(Name);

        Profile profile = sketch.ToSolidProfile("Revolve");
        if (profile.IsEmpty)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Revolve: the sketch has no closed loop.");

        Frame3? axis = null;
        if (AxisId != 0)
        {
            var da = ctx.Part.FindById<DatumAxis>(AxisId);
            if (da != null) axis = Frame3.FromNormal(da.Origin, da.Direction);
        }
        if (axis == null && AxisEntityId != 0) axis = sketch.AxisFromEntity(AxisEntityId);
        axis ??= sketch.RevolutionAxis();

        if (axis == null)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, 
                "Revolve: no axis. Draw a centerline in the sketch or pick a segment as the axis.");

        double a0 = StartAngle.Resolve(ctx.Parameters) * Math.PI / 180.0;
        double a1 = EndAngle.Resolve(ctx.Parameters) * Math.PI / 180.0;

        var solid = RevolveOp.Build(profile, axis.Value, a0, a1);

        if (Cut) BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        else BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
    }
    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }

}
