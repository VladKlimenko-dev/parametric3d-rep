using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>
/// ЗЕРКАЛО — операция дерева. В ядре она была, но из модели ею воспользоваться было нельзя.
/// Плоскость задаётся точкой и нормалью: числами, а не ссылкой на грань, — так операция не
/// зависит от того, какие грани переименовались при перестроении.
/// </summary>
public sealed class MirrorFeature : Feature
{
    /// <summary>k113: массив операций-резцов — по сеткам (FeatureReplay решает, годятся ли операции).</summary>
    public override bool InstantPreviewSafe => Sources.Count > 0;
    public Vector3d At;
    public Vector3d Normal = Vector3d.UnitX;
    /// <summary>u12: ключи операций для повтора (зеркало операций); пусто — зеркало тела.</summary>
    public readonly List<string> Sources = new();
    /// <summary>u13: тело заменяется отражённым (зеркальный компонент сборки), без объединения с исходным.</summary>
    public bool ReplaceBody;

    public MirrorFeature(string name = "Mirror") { Name = name; }

    public override string Summary => $"Mirror about ({Normal.X:0.##}, {Normal.Y:0.##}, {Normal.Z:0.##})";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Mirror: no body.");
        if (Normal.LengthSquared < 1e-18) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Mirror: the plane normal is not set.");

        if (Sources.Count > 0) { FeatureReplay.Run(ctx, this, Sources, new[] { TransformOp.Mirror(At, Normal.Normalized()) }, "Mirror"); return; }
        if (ReplaceBody)
        {
            var x = TransformOp.Mirror(At, Normal.Normalized());
            for (int i = 0; i < ctx.Bodies.Count; i++) ctx.Bodies[i] = Kernel.Topology.TopologyTransform.Apply(ctx.Bodies[i], x) ?? TransformOp.Copy(ctx.Bodies[i], x);
            return;
        }
        bool mirrored = false;
        Kernel.OperationContract.Apply("Mirror", ctx.Bodies, b => mirrored = TransformOp.MirrorBody(b, At, Normal.Normalized(), ctx.Warnings, Key));
        if (!mirrored)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Mirror: the body could not be mirrored.");
    }
}

/// <summary>
/// ЛИНЕЙНЫЙ МАССИВ. Шаг задаётся вектором и числом повторений; число — целое, и меньше двух
/// не имеет смысла, о чём операция говорит прямо, а не строит одно тело молча.
/// </summary>
public sealed class LinearPatternFeature : Feature
{
    /// <summary>k113: массив операций-резцов — по сеткам (FeatureReplay решает, годятся ли операции).</summary>
    public override bool InstantPreviewSafe => Sources.Count > 0;
    /// <summary>KRN-09 (09.6): наибольшее число копий массива.</summary>
    public const int MaxCopies = 10000;
    internal static readonly bool CopyLimitOff = Environment.GetEnvironmentVariable("P3D_COPYLIMIT") == "0";
    public Vector3d Step = new(20, 0, 0);
    public ValueRef Count = new(3);
    /// <summary>u12: ключи операций для повтора (массив операций); пусто — массив тела.</summary>
    public readonly List<string> Sources = new();

    public LinearPatternFeature(string name = "Linear pattern") { Name = name; }

    public override string Summary => $"Linear pattern ×{Count}";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Linear pattern: no body.");

        int n = (int)Math.Round(Count.Resolve(ctx.Parameters));
        if (n < 2) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Linear pattern: the count must be at least two.");
        // KRN-09 (09.6): предел ресурса — число копий из файла 1e9 вешало перестроение (отмена не доходила до цикла копий).
        // Мутация P3D_COPYLIMIT=0.
        if (!CopyLimitOff && n > MaxCopies) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Linear pattern: the count {n} exceeds the limit of {MaxCopies} copies.");
        if (Step.LengthSquared <= Tol.Weld * Tol.Weld)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Linear pattern: the step must be greater than zero.");

        if (Sources.Count > 0) { FeatureReplay.Run(ctx, this, Sources, Enumerable.Range(1, n - 1).Select(i => TransformOp.Move(Step * i)).ToList(), "Linear pattern"); return; }
        bool patterned = false;
        Kernel.OperationContract.Apply("LinearPattern", ctx.Bodies, b => patterned = TransformOp.LinearPattern(b, Step, n, ctx.Warnings, Key));
        if (!patterned)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Linear pattern: the copies could not be built.");
    }
}

/// <summary>КРУГОВОЙ МАССИВ: ось задаётся точкой и направлением.</summary>
public sealed class CircularPatternFeature : Feature
{
    /// <summary>k113: массив операций-резцов — по сеткам (FeatureReplay решает, годятся ли операции).</summary>
    public override bool InstantPreviewSafe => Sources.Count > 0;
    const int MaxCopies = LinearPatternFeature.MaxCopies;
    static bool CopyLimitOff => LinearPatternFeature.CopyLimitOff;
    public Vector3d At;
    public Vector3d Axis = Vector3d.UnitZ;
    public ValueRef Count = new(4);
    /// <summary>u12: ключи операций для повтора (массив операций); пусто — массив тела.</summary>
    public readonly List<string> Sources = new();

    public CircularPatternFeature(string name = "Circular pattern") { Name = name; }

    public override string Summary => $"Circular pattern ×{Count}";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Circular pattern: no body.");

        int n = (int)Math.Round(Count.Resolve(ctx.Parameters));
        if (n < 2) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Circular pattern: the count must be at least two.");
        // KRN-09 (09.6): предел ресурса — число копий из файла 1e9 вешало перестроение (отмена не доходила до цикла копий).
        // Мутация P3D_COPYLIMIT=0.
        if (!CopyLimitOff && n > MaxCopies) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Circular pattern: the count {n} exceeds the limit of {MaxCopies} copies.");
        if (Axis.LengthSquared < 1e-18) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Circular pattern: the axis is not set.");

        if (Sources.Count > 0) { FeatureReplay.Run(ctx, this, Sources, Enumerable.Range(1, n - 1).Select(i => TransformOp.Rotate(At, Axis.Normalized(), 360.0 * i / n)).ToList(), "Circular pattern"); return; }
        bool rotated = false;
        Kernel.OperationContract.Apply("CircularPattern", ctx.Bodies, b => rotated = TransformOp.CircularPattern(b, At, Axis.Normalized(), n, ctx.Warnings, Key));
        if (!rotated)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Circular pattern: the copies could not be built.");
    }
}

/// <summary>
/// u12: ЗЕРКАЛО И МАССИВЫ ОПЕРАЦИЙ. Выбранные операции дерева (до этой) повторяются под каждым преобразованием на текущем
/// теле — каждая своим путём (вырез — резец под преобразованием, скругление — на ребре в отражённом месте). Прежде
/// повторялось всё тело: массив выреза 3 × 5 мм объединял три смещённых куба (перестроение надолго), зеркало скругления
/// объединяло деталь с отражением — скругление заполнялось, ошибки не было.
/// </summary>
internal static class FeatureReplay
{
    public static void Run(BuildContext ctx, Feature owner, IReadOnlyList<string> keys, IReadOnlyList<TransformOp.Xform> xs, string what)
    {
        // k113: повтор по сеткам — только операций-резцов (резец из своих данных); скругление и фаска ищут рёбра текущего
        // тела — носителю сетки они не годятся
        bool instant = Parametric3D.Kernel.Ops.InstantPreview.Allowed;
        var srcAll = ctx.Part.Features.Where(f => keys.Contains(f.Key)).ToList();
        if (ReplayOff || srcAll.Any(f => !f.InstantReplaySafe)) Parametric3D.Kernel.Ops.InstantPreview.Allowed = false;
        try { RunCore(ctx, owner, keys, xs, what); }
        finally { Parametric3D.Kernel.Ops.InstantPreview.Allowed = instant; }
    }

    static readonly bool ReplayOff = Environment.GetEnvironmentVariable("P3D_INSTANTREPLAY") == "0";

    static void RunCore(BuildContext ctx, Feature owner, IReadOnlyList<string> keys, IReadOnlyList<TransformOp.Xform> xs, string what)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: no body.");
        var all = ctx.Part.Features.ToList();
        int me = all.IndexOf(owner);
        var src = all.Where((f, i) => keys.Contains(f.Key) && (me < 0 || i < me)).ToList();
        if (src.Count != keys.Distinct().Count())
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: {keys.Distinct().Count() - src.Count} selected operation(s) are missing or come after the {what.ToLowerInvariant()}.");
        src = src.Where(f => !f.Suppressed).ToList();
        if (src.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: all selected operations are suppressed.");
        int k = 0;
        foreach (var x in xs)
        {
            k++;
            foreach (var f in src)
            {
                KernelContext.Checkpoint("pattern.feature");
                try { f.ExecuteTransformed(ctx, x); }
                catch (RebuildException ex)
                {
                    throw new RebuildException(ex.Outcome, $"{what}: copy {k} of «{f.Name}» failed — {ex.Message}");
                }
            }
        }
        ctx.Warnings.Add($"{what}: {src.Count} operation(s) repeated {xs.Count} time(s).");
    }
}
