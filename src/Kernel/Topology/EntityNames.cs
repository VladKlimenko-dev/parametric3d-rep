using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-01 (01.11): ИМЕНА ЗАТРОНУТЫХ СУЩНОСТЕЙ ДЛЯ ДИАГНОСТИКИ — устойчивые, а не номера построения: грань — её имя
/// («#ключ операции/роль»), ребро — имена двух его граней, вершина — точка. По ним отказ находится в модели и после
/// перестроения (номера сущностей у каждого построения свои).
/// </summary>
public static class EntityNames
{
    public static string Face(TFace f) => f.Tag.Length > 0 ? f.Tag : $"face#{f.Id}";

    public static string Edge(TEdge e)
    {
        var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().Select(Face).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return fs.Count == 0 ? $"edge#{e.Id}" : "edge[" + string.Join(" | ", fs) + "]";
    }

    public static string Vertex(TVertex v) => $"vertex({v.P.X:0.######}, {v.P.Y:0.######}, {v.P.Z:0.######})";

    /// <summary>Имена сущностей тел по номерам (грани, рёбра, вершины); неизвестный номер — пропускается.</summary>
    public static List<string> Of(IEnumerable<TSolid> bodies, IEnumerable<uint> ids)
    {
        var want = ids.ToHashSet(); var res = new List<string>();
        if (want.Count == 0) return res;
        foreach (var b in bodies)
        {
            foreach (var f in b.Faces) if (want.Contains(f.Id)) res.Add(Face(f));
            foreach (var e in b.Edges) if (want.Contains(e.Id)) res.Add(Edge(e));
            foreach (var v in b.Vertices) if (want.Contains(v.Id)) res.Add(Vertex(v));
        }
        return res.Distinct().ToList();
    }

    /// <summary>Гранёные грани и рёбра тела (по аудиту геометрии) — поимённо.</summary>
    public static List<string> Faceted(TSolid b)
    {
        // многогранное представление тела (как в GeometryAudit.Faceted): кривые грани, а у тела из одних плоскостей — все
        if ((b.Exact != null && b.ExactAuthoritative) || b.Guarantee is GuaranteeLevel.ExactPolyhedral or GuaranteeLevel.Approximate)
        {
            var curved = b.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is not { IsPlanar: true }).ToList();
            return (curved.Count > 0 ? curved : b.Faces.ToList()).Select(Face).Distinct().ToList();
        }
        var a = GeometryAudit.Of(b);
        var ids = a.Faces.Where(x => x.Level != GeometryLevel.Analytic).Select(x => x.Face)
                  .Concat(a.Edges.Where(x => x.Level != GeometryLevel.Analytic).Select(x => x.Edge));
        return Of(new[] { b }, ids);
    }
}
