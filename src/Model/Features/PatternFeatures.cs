using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>
/// ЗЕРКАЛО — операция дерева. В ядре она была, но из модели ею воспользоваться было нельзя.
/// Плоскость задаётся точкой и нормалью: числами, а не ссылкой на грань, — так операция не
/// зависит от того, какие грани переименовались при перестроении.
/// </summary>
public sealed class MirrorFeature : Feature
{
    public Vector3d At;
    public Vector3d Normal = Vector3d.UnitX;

    public MirrorFeature(string name = "Mirror") { Name = name; }

    public override string Summary => $"Mirror about ({Normal.X:0.##}, {Normal.Y:0.##}, {Normal.Z:0.##})";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Mirror: no body.");
        if (Normal.LengthSquared < 1e-18) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Mirror: the plane normal is not set.");

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
    /// <summary>KRN-09 (09.6): наибольшее число копий массива.</summary>
    public const int MaxCopies = 10000;
    internal static readonly bool CopyLimitOff = Environment.GetEnvironmentVariable("P3D_COPYLIMIT") == "0";
    public Vector3d Step = new(20, 0, 0);
    public ValueRef Count = new(3);

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

        bool patterned = false;
        Kernel.OperationContract.Apply("LinearPattern", ctx.Bodies, b => patterned = TransformOp.LinearPattern(b, Step, n, ctx.Warnings, Key));
        if (!patterned)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Linear pattern: the copies could not be built.");
    }
}

/// <summary>КРУГОВОЙ МАССИВ: ось задаётся точкой и направлением.</summary>
public sealed class CircularPatternFeature : Feature
{
    const int MaxCopies = LinearPatternFeature.MaxCopies;
    static bool CopyLimitOff => LinearPatternFeature.CopyLimitOff;
    public Vector3d At;
    public Vector3d Axis = Vector3d.UnitZ;
    public ValueRef Count = new(4);

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

        bool rotated = false;
        Kernel.OperationContract.Apply("CircularPattern", ctx.Bodies, b => rotated = TransformOp.CircularPattern(b, At, Axis.Normalized(), n, ctx.Warnings, Key));
        if (!rotated)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Circular pattern: the copies could not be built.");
    }
}
