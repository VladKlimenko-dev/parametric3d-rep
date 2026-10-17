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
    /// <summary>
    /// KRN-06 (06.2): ЗАКОН РАДИУСА ПО ТОЧКАМ — (доля длины F ∈ [0, 1] от начала первой ссылки, радиус); у цепочки (касательного
    /// контура) — доля длины контура. Пусто — постоянный радиус (или линейный закон RadiusEnd).
    /// </summary>
    public readonly List<(double F, ValueRef R)> LawPoints = new();

    public RoundFeature(string name = "Round") { Name = name; }

    public override void ExecuteTransformed(BuildContext ctx, Kernel.Ops.TransformOp.Xform x)
    {
        if (Selectors.Count > 0) base.ExecuteTransformed(ctx, x);
        var c = new RoundFeature(Name) { Radius = Radius, RadiusEnd = RadiusEnd, Key = Key + "x" + ctx.Warnings.Count };
        c.LawPoints.AddRange(LawPoints);
        foreach (var e in Edges) c.Edges.Add(Xf(e, x));
        c.Execute(ctx);
    }
    internal static EdgeRef Xf(EdgeRef e, Kernel.Ops.TransformOp.Xform x) => new(x.Point(e.Start), x.Point(e.End), e.Mid is { } m ? x.Point(m) : null);

    public override string Summary => $"Round R{Radius}{(LawPoints.Count >= 2 ? " law " + string.Join("/", LawPoints.Select(p => p.R.Value.ToString("0.###"))) : "")}{(RadiusEnd is { } re0 ? "→" + re0 : "")} ({Edges.Count} edges{(Selectors.Count > 0 ? ", " + string.Join(", ", Selectors) : "")})";

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
        if (LawPoints.Count >= 2)
        {
            var pts = LawPoints.Select(p => (p.F, p.R.Resolve(ctx.Parameters))).ToList();
            if (pts.Any(p => p.Item2 <= Tol.Weld)) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Round: law radii must be greater than zero.");
            Kernel.OperationContract.Apply("Round", ctx.Bodies, b => FilletOp.RoundEdgesLaw(b, edges, pts, ctx.Warnings));
        }
        else if (RadiusEnd is { } re)
        {
            double r1 = re.Resolve(ctx.Parameters);
            if (r1 <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Round: the end radius must be greater than zero.");
            Kernel.OperationContract.Apply("Round", ctx.Bodies, b => FilletOp.RoundEdgesVariable(b, edges, r, r1, ctx.Warnings));
        }
        else Kernel.OperationContract.Apply("Round", ctx.Bodies, b => FilletOp.RoundEdges(b, edges, r, ctx.Warnings));
        // KRN-06: прямой построитель (плоскость/плоскость, плоскость/цилиндр) — сопряжение точное; иначе — резец с хордами
        if (!ctx.Warnings.Skip(w0).Any(x => x.StartsWith("Round: direct blend", StringComparison.Ordinal) || x.StartsWith("Round: direct variable blend", StringComparison.Ordinal)))
            // (у закона по точкам прямой путь пишет «Round: direct blend … radius law»)
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
    /// <summary>u12: переменная фаска — размер в конце цепочки (равные катеты от D1 до SizeEnd плавно по длине); null — постоянная.</summary>
    public ValueRef? SizeEnd;

    /// <summary>KRN-08 (08.3): рёбра по роли, разрешаются при каждой перестройке.</summary>
    public readonly List<EdgeSelector> Selectors = new();

    public ChamferFeature(string name = "Chamfer") { Name = name; }

    public override void ExecuteTransformed(BuildContext ctx, Kernel.Ops.TransformOp.Xform x)
    {
        if (Selectors.Count > 0) base.ExecuteTransformed(ctx, x);
        var c = new ChamferFeature(Name) { Mode = Mode, D1 = D1, D2 = D2, Angle = Angle, SizeEnd = SizeEnd, Key = Key + "x" + ctx.Warnings.Count };
        foreach (var e in Edges) c.Edges.Add(RoundFeature.Xf(e, x));
        c.Execute(ctx);
    }

    public override string Summary => SizeEnd is { } se ? $"Chamfer {D1}→{se} ({Edges.Count} edges)" : Mode == ChamferKind.TwoDistances
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

        if (SizeEnd is { } se)
        {
            double e1 = se.Resolve(ctx.Parameters);
            if (e1 <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Chamfer: the end size must be greater than zero.");
            Kernel.OperationContract.Apply("Chamfer", ctx.Bodies, b =>
            {
                if (!Kernel.Ops.DirectRound.TryChamferVariable(b, edges, d1, e1, ctx.Warnings, out var why))
                    throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported,
                        $"Chamfer: a variable chamfer is built for an edge or a tangent chain of a planar face with walls square to it — here: {why}");
            });
            return;
        }

        // ФАСКА ТОЧНА. Срез — плоскость, а плоскости булева режет без
        // приближения, в отличие от дуги скругления. Несимметричная фаска
        // и фаска «по углу» строятся наравне с симметричной: разные катеты
        // треугольника сечения ничего не усложняют.
        Kernel.OperationContract.Apply("Chamfer", ctx.Bodies, b => FilletOp.ChamferEdges(b, edges, d1, d2, ctx.Warnings));
    }
}
