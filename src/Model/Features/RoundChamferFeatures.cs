using Parametric3D.Core;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

/// <summary>
/// Скругление рёбер.
///
/// Рёбра запоминаются ПО ГЕОМЕТРИИ — концевыми точками, — а не по номерам:
/// номера выдаются заново при каждой перестройке, и операция с номерами
/// разваливалась бы при первом же изменении эскиза.
/// </summary>
public sealed class RoundFeature : Feature
{
    public readonly List<EdgeRef> Edges = new();
    /// <summary>KRN-08 (08.3): рёбра по роли (кромки операции и т. п.), разрешаются при каждой перестройке.</summary>
    public readonly List<EdgeSelector> Selectors = new();
    public ValueRef Radius = new(2);
    /// <summary>KRN-06 (06.14): радиус в конце ребра (линейный закон от Radius в начале ссылки); null — постоянный радиус.</summary>
    public ValueRef? RadiusEnd;

    public RoundFeature(string name = "Round") { Name = name; }

    public override string Summary => $"Round R{Radius}{(RadiusEnd is { } re0 ? "→" + re0 : "")} ({Edges.Count} edges{(Selectors.Count > 0 ? ", " + string.Join(", ", Selectors) : "")})";

    public override void Execute(BuildContext ctx)
    {
        var edges = new List<EdgeRef>(Edges);
        foreach (var sel in Selectors)
        {
            var found = sel.Resolve(ctx.Bodies);
            if (found.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Round: the selector {sel} selects no edges.");
            edges.AddRange(found.Select(EdgeRef.Of));
        }
        if (edges.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Round: no edges selected.");

        double r = Radius.Resolve(ctx.Parameters);
        if (r <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Round: radius must be greater than zero.");

        // ЧТО В СКРУГЛЕНИИ ТОЧНО, А ЧТО ПРИБЛИЖЕНО.
        //
        // Поверхность скругления и его рёбра — аналитические: грань несёт
        // выдавленную дугу, а границы после булевой распознаются обратно
        // в дуги. Показывается и выбирается настоящая геометрия.
        //
        // А вот САМ РЕЗ делается многоугольником: разбиение пространства
        // работает с плоскими кусками, и дуга уходит в него ломаной.
        // Срез получается чуть больше точного — при шестнадцати хордах
        // на четверть окружности примерно на полпроцента от объёма
        // самого среза.
        //
        // Прежний текст говорил «поверхность гранёная». После того как
        // поверхность стала аналитической, он сделался неверным, а неверное
        // предупреждение хуже отсутствующего: ему перестают верить.
        int w0 = ctx.Warnings.Count;

        // K02: через контракт операции — результат проверяется, отказ не меняет тела дерева.
        if (RadiusEnd is { } re)
        {
            double r1 = re.Resolve(ctx.Parameters);
            if (r1 <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Round: the end radius must be greater than zero.");
            Kernel.OperationContract.Apply("Round", ctx.Bodies, b => FilletOp.RoundEdgesVariable(b, edges, r, r1, ctx.Warnings));
        }
        else Kernel.OperationContract.Apply("Round", ctx.Bodies, b => FilletOp.RoundEdges(b, edges, r, ctx.Warnings));
        // KRN-06: прямой построитель (плоскость/плоскость, плоскость/цилиндр) — сопряжение точное; иначе — резец с хордами
        if (!ctx.Warnings.Skip(w0).Any(x => x.StartsWith("Round: direct blend", StringComparison.Ordinal) || x.StartsWith("Round: direct variable blend", StringComparison.Ordinal)))
            ctx.Warnings.Add("Round: the surface and its edges are exact, but the cut itself " +
                             "is made with a chord approximation — the removed volume is about " +
                             "half a percent larger. Vertices where several rounds meet are " +
                             "left unpatched.");

        // Объём после скругления называется отдельно и раньше фаски:
        // если оболочка рвётся уже здесь, разбираться надо со скруглением,
        // а не с тем, что идёт следом.
    }
}

public enum ChamferKind
{
    TwoDistances = 0,   // dx и dy
    DistanceAngle = 1   // dx и угол
}

/// <summary>Фаска рёбер.</summary>
public sealed class ChamferFeature : Feature
{
    public readonly List<EdgeRef> Edges = new();
    public ChamferKind Mode = ChamferKind.TwoDistances;
    public ValueRef D1 = new(1);
    public ValueRef D2 = new(1);
    public ValueRef Angle = new(45);

    /// <summary>KRN-08 (08.3): рёбра по роли, разрешаются при каждой перестройке.</summary>
    public readonly List<EdgeSelector> Selectors = new();

    public ChamferFeature(string name = "Chamfer") { Name = name; }

    public override string Summary => Mode == ChamferKind.TwoDistances
        ? $"Chamfer {D1}x{D2} ({Edges.Count} edges)"
        : $"Chamfer {D1}x{Angle}\u00b0 ({Edges.Count} edges)";

    public override void Execute(BuildContext ctx)
    {
        var edges = new List<EdgeRef>(Edges);
        foreach (var sel in Selectors)
        {
            var found = sel.Resolve(ctx.Bodies);
            if (found.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Chamfer: the selector {sel} selects no edges.");
            edges.AddRange(found.Select(EdgeRef.Of));
        }
        if (edges.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Chamfer: no edges selected.");

        double d1 = D1.Resolve(ctx.Parameters);
        double d2 = Mode == ChamferKind.TwoDistances
            ? D2.Resolve(ctx.Parameters)
            : d1 * Math.Tan(Angle.Resolve(ctx.Parameters) * Math.PI / 180.0);

        if (d1 <= Tol.Weld || d2 <= Tol.Weld)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Chamfer: distances must be greater than zero.");

        // ФАСКА ТОЧНА. Срез — плоскость, а плоскости булева режет без
        // приближения, в отличие от дуги скругления. Несимметричная фаска
        // и фаска «по углу» строятся наравне с симметричной: разные катеты
        // треугольника сечения ничего не усложняют.
        Kernel.OperationContract.Apply("Chamfer", ctx.Bodies, b => FilletOp.ChamferEdges(b, edges, d1, d2, ctx.Warnings));
    }
}
