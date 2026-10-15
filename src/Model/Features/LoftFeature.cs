using Parametric3D.Core;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>
/// ЛОФТ (по сечениям) — операция дерева. В ядре она была, но из модели ею воспользоваться было
/// нельзя. Сечения задаются эскизами по порядку: порядок и есть смысл операции, поэтому он
/// хранится списком, а не набором.
/// </summary>
public sealed class LoftFeature : Feature
{
    /// <summary>Эскизы сечений, по порядку от первого к последнему.</summary>
    public readonly List<uint> SectionSketchIds = new();

    /// <summary>Сколько точек размечается по длине каждого сечения.</summary>
    public int Samples = 64;

    /// <summary>KRN-07 (07.4): гладкий лофт (интерполяция поперёк сечений) вместо линейчатого.</summary>
    public bool Smooth;

    /// <summary>KRN-07 (07.4): эскизы направляющих — каждая кривая эскиза (кроме точек) — направляющая через соответственные
    /// вершины сечений.</summary>
    public readonly List<uint> GuideSketchIds = new();

    /// <summary>KRN-07 (07.4): касательная к нормали первого / последнего сечения (длина — доля расстояния между центрами
    /// крайних сечений; 0 — без условия).</summary>
    public double StartTangent, EndTangent;

    public LoftFeature(string name = "Loft") { Name = name; }
    static readonly bool LoftSolveOff = Environment.GetEnvironmentVariable("P3D_LOFTSOLVE") == "0";

    public override string Summary => $"Loft ({SectionSketchIds.Count} sections{(GuideSketchIds.Count > 0 ? $", {GuideSketchIds.Count} guide sketch(es)" : "")}{(Smooth ? ", smooth" : "")})" + (Cut ? " cut" : "");

    public override void Execute(BuildContext ctx)
    {
        if (SectionSketchIds.Count < 2)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: at least two sections are needed.");

        var sections = new List<Profile>();
        foreach (uint id in SectionSketchIds)
        {
            var sk = ctx.FindSketch(id)
                     ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: a section sketch is not found.");
            // KRN-07 (07.4): сечения решаются, как у прочих операций по эскизу. Прежде лофт брал эскиз как есть: размер,
            // заданный параметром, не действовал (измерено: радиус по формуле R=3 — лофт строился по нарисованному r=5,
            // объём на 26% больше). Мутация P3D_LOFTSOLVE=0.
            if (!LoftSolveOff)
            {
                if (ctx.Parameters != null) sk.UpdateDimensionsFromParameters(ctx.Parameters);
                sk.Solve(ctx.Solver);
                sk.RequireSolved(Name);
            }
            var prof = sk.ToSolidProfile("Loft");
            if (prof.IsEmpty) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: a section sketch is empty or not closed.");
            sections.Add(prof);
        }

        List<Parametric3D.Kernel.Curves.ICurve3>? guides = null;
        foreach (uint gid in GuideSketchIds)
        {
            var gs = ctx.FindSketch(gid)
                     ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: a guide sketch is not found.");
            if (ctx.Parameters != null) gs.UpdateDimensionsFromParameters(ctx.Parameters);
            gs.Solve(ctx.Solver);
            gs.RequireSolved(Name);
            guides ??= new();
            var gc = gs.Entities.Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint).Select(e => e.ToCurve3(gs.Plane)).ToList();
            if (gc.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: a guide sketch has no curves.");
            guides.AddRange(gc);
        }
        var solid = LoftOp.Build(sections, Math.Clamp(Samples, 8, 512), Smooth, guides, StartTangent, EndTangent);

        if (Cut) BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        else BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        foreach (uint id in SectionSketchIds) if (id != 0) yield return id;
        foreach (uint id in GuideSketchIds) if (id != 0) yield return id;
    }
}
