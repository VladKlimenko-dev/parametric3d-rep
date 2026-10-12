using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

/// <summary>
/// KRN-08: УСТОЙЧИВАЯ ССЫЛКА НА ВЕРШИНУ. Вершина запоминается точкой и — при первом удачном выборе — именем: набором
/// имён граней вокруг неё (имена несут ключи породивших операций). Дальше вершина ищется по имени: правка размеров
/// переносит вершину — ссылка идёт за ней (прежде точка «вершины» после правки оказывалась в пустоте, и операция отказывала).
/// Несколько вершин с тем же именем (копии массива) — по точке, если она ровно на одной; иначе — неоднозначность с
/// кандидатами. Нет такой вершины — явная ошибка. Мутация P3D_VERTEXNAMES=0 — только по точке (прежнее поведение).
/// </summary>
public static class VertexRefs
{
    static readonly bool Off = Environment.GetEnvironmentVariable("P3D_VERTEXNAMES") == "0";

    /// <summary>Имя вершины в точке p: имена граней всех рёбер, сходящихся в ней (по положению).</summary>
    public static string KeyAt(IEnumerable<TSolid> bodies, Vector3d p)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in bodies.SelectMany(b => b.Edges))
            if ((e.V0.P - p).Length < 1e-7 || (e.V1.P - p).Length < 1e-7)
                foreach (var ce in e.CoEdges) if (ce.Loop?.Face?.Tag is { Length: > 0 } t) tags.Add(t);
        return string.Join(";", tags);
    }

    public static Vector3d Resolve(IReadOnlyList<TSolid> bodies, Vector3d point, ref string? key, string what)
    {
        if (Off) return point;
        var verts = bodies.SelectMany(b => b.Edges).SelectMany(e => new[] { e.V0.P, e.V1.P })
                          .Aggregate(new List<Vector3d>(), (l, q) => { if (!l.Any(x => (x - q).Length < 1e-7)) l.Add(q); return l; });
        if (key == null)
        {
            var at = verts.Where(v => (v - point).Length <= 1e-3).ToList();
            if (at.Count == 1) { var k = KeyAt(bodies, at[0]); if (k.Length > 0) key = k; }
            return point;
        }
        var k0 = key;
        var named = verts.Where(v => KeyAt(bodies, v) == k0).ToList();
        if (named.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: the referenced vertex ({string.Join(", ", k0.Split(';').Select(EdgeRef.Bare))}) no longer exists.");
        if (named.Count == 1) return named[0];
        var on = named.Where(v => (v - point).Length <= 1e-3).ToList();
        if (on.Count == 1) return on[0];
        throw new AmbiguousReferenceException(what, named.Select(v => (EdgeRef.Bare(k0), v)).ToList(),
            $"{named.Count} vertices have the same faces around them and the stored point lies on {(on.Count == 0 ? "none" : on.Count.ToString())} of them");
    }
}
