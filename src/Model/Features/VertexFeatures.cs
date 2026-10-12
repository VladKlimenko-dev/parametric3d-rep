using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>
/// ФАСКА ОДНОЙ ВЕРШИНЫ (выпуклый угол из N ≥ 3 рёбер): равномерная (один размер на все рёбра)
/// или с отдельным размером для каждого ребра (точка на ребре + размер). Вершина — точкой.
/// </summary>
public sealed class VertexChamferFeature : Feature
{
    public Vector3d Vertex;
    /// <summary>KRN-08: имя вершины (грани вокруг неё) — ссылка идёт за вершиной при правках.</summary>
    public string? VertexKey;
    /// <summary>Равномерный размер (используется, если отдельных размеров нет).</summary>
    public ValueRef Distance = new(2);
    /// <summary>Отдельные размеры: точка на ребре (выбирает ребро) и расстояние вдоль него.</summary>
    public readonly List<(Vector3d OnEdge, ValueRef D)> PerEdge = new();

    public VertexChamferFeature(string name = "Vertex chamfer") { Name = name; }

    public override string Summary => PerEdge.Count > 0
        ? "Vertex chamfer " + string.Join("×", PerEdge.Select(x => x.D.ToString())) : $"Vertex chamfer {Distance}";

    public override void Execute(BuildContext ctx)
    {
        var at = VertexRefs.Resolve(ctx.Bodies, Vertex, ref VertexKey, "Vertex chamfer");
        var shift = at - Vertex;                                 // вершина ушла правкой — точки на рёбрах сдвигаются с ней
        Kernel.OperationContract.Apply("VertexChamfer", ctx.Bodies, b =>
        {
            if (PerEdge.Count == 0)
                VertexOp.Chamfer(b, at, new[] { Distance.Resolve(ctx.Parameters) }, ctx.Warnings);
            else
            {
                var body = b.FirstOrDefault(x => x.Edges.Any(e => (e.V0.P - at).Length < 1e-3 || (e.V1.P - at).Length < 1e-3))
                           ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex chamfer: no vertex at the selected point.");
                var corner = VertexOp.Find(body, at);
                var d = VertexOp.MatchDistances(corner, PerEdge.Select(x => (x.OnEdge + shift, x.D.Resolve(ctx.Parameters))).ToList());
                VertexOp.Chamfer(b, at, d, ctx.Warnings);
            }
        }, ctx.Warnings);
    }
}

/// <summary>СКРУГЛЕНИЕ ОДНОЙ ВЕРШИНЫ (выпуклый угол из N ≥ 3 рёбер) радиусом R — часть сферы с центром на оси угла.</summary>
public sealed class VertexRoundFeature : Feature
{
    public Vector3d Vertex;
    /// <summary>KRN-08: имя вершины (грани вокруг неё) — ссылка идёт за вершиной при правках.</summary>
    public string? VertexKey;
    public ValueRef Radius = new(2);

    public VertexRoundFeature(string name = "Vertex round") { Name = name; }

    public override string Summary => $"Vertex round {Radius}";

    public override void Execute(BuildContext ctx)
    {
        var at = VertexRefs.Resolve(ctx.Bodies, Vertex, ref VertexKey, "Vertex round");
        Kernel.OperationContract.Apply("VertexRound", ctx.Bodies, b => VertexOp.Round(b, at, Radius.Resolve(ctx.Parameters), ctx.Warnings), ctx.Warnings);
    }
}
