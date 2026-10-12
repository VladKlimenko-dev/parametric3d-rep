using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>
/// ОБОЛОЧКА — операция дерева. В ядре она была давно, но в дереве построения её не было, и
/// воспользоваться ею из модели было нельзя: дерево знало только выдавливание, вращение,
/// протяжку, скругление и фаску.
///
/// Открытые грани задаются точками на них: точка переживает изменение размеров лучше, чем
/// номер грани, а к устойчивым именам граней операция перейдёт вместе с остальными.
/// </summary>
public sealed class ShellFeature : Feature
{
    /// <summary>Толщина стенки; наружу — при отрицательном значении.</summary>
    public ValueRef Thickness = new(2);

    /// <summary>Точки на гранях, которые остаются открытыми.</summary>
    public readonly List<Vector3d> OpenFaces = new();

    /// <summary>K05: устойчивые имена открытых граней (запоминаются при первом выборе по точке).</summary>
    public readonly List<string?> OpenFaceNames = new();

    /// <summary>KRN-08 (08.3): открытые грани по роли (все грани операции / с ролью) — разрешаются при каждой перестройке.</summary>
    public readonly List<FaceSelector> OpenSelectors = new();

    public ShellFeature(string name = "Shell") { Name = name; }

    public override string Summary =>
        $"Shell {Thickness}" + (OpenFaces.Count > 0 ? $" ({OpenFaces.Count} open)" : "");

    public override void Execute(BuildContext ctx)
    {
        double t = Thickness.Resolve(ctx.Parameters);
        if (Math.Abs(t) <= Tol.Weld)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Shell: the wall thickness must be greater than zero.");
        if (ctx.Bodies.Count == 0)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Shell: no body to hollow.");

        bool hollowed = false;
        var open = OpenFaces.Count > 0 ? FaceRefs.Resolve(ctx.Bodies[0], OpenFaces, OpenFaceNames, "Shell") : null;
        if (OpenSelectors.Count > 0)
        {
            open ??= new List<Vector3d>();
            foreach (var sel in OpenSelectors)
            {
                var fs = sel.Resolve(ctx.Bodies);
                if (fs.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Shell: the selector {sel} selects no faces.");
                open.AddRange(fs.Select(FaceRefs.Inside));
            }
        }
        Kernel.OperationContract.Apply("Shell", ctx.Bodies, b => hollowed = ShellOp.Hollow(b, t, open, ctx.Warnings));
        if (!hollowed)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Shell: the body could not be hollowed.");
    }
}
