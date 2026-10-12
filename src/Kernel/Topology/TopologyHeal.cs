using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// ЗАЛЕЧИВАНИЕ ТОПОЛОГИИ. Промышленное ядро обязано принимать несовершенные тела: после булевой
/// остаются рёбра длиной в доли микрона и грани нулевой площади. Сами по себе они не мешают
/// объёму, но ломают счёт: у тела характеристика Эйлера расходится с характеристикой его сетки
/// (на прогоне 450 последовательностей — 44 предупреждения), а экспорт и скругления по таким
/// рёбрам дают брак.
///
/// Здесь убирается то, что заведомо лишнее: рёбра короче допуска (их концы сливаются) и
/// контуры, выродившиеся после этого в точку. Геометрия при этом не меняется — сливаются точки,
/// отстоящие меньше чем на допуск склейки.
/// </summary>
public static class TopologyHeal
{
    /// <summary>Сколько рёбер и граней убрано при последнем залечивании.</summary>
    public static int RemovedEdges, RemovedFaces;

    /// <summary>
    /// Убирает короткие рёбра. Допуск по умолчанию — допуск склейки: всё, что короче, в
    /// геометрии неразличимо.
    /// </summary>
    public static bool Heal(TSolid solid, double tolerance = 0, List<string>? warnings = null)
    {
        solid.BeginEdit("TopologyHeal.Heal", EditKind.Derive);
        // Общий порог — лишь нижняя граница: у каждого ребра он может быть только больше.
        double tol = tolerance > 0 ? tolerance : Tol.Weld;
        RemovedEdges = 0;
        RemovedFaces = 0;

        // 1. Короткие рёбра: концы сливаются в одну вершину.
        var merge = new Dictionary<TVertex, TVertex>();
        TVertex Root(TVertex v)
        {
            while (merge.TryGetValue(v, out var up) && !ReferenceEquals(up, v)) v = up;
            return v;
        }

        foreach (var e in solid.Edges.ToList())
        {
            var a = Root(e.V0);
            var b = Root(e.V1);
            if (ReferenceEquals(a, b)) continue;

            // ПОРОГ БЕРЁТСЯ У САМОГО РЕБРА, а не один на всё тело. Прежде лечили всё короче
            // микрона, и вместе с мусором исчезали рёбра, на которые опирается построитель
            // сетки: охват падал с 90 тел из 100 до 83. Ребро со своим допуском говорит само,
            // что для него неразличимо.
            double own = Math.Max(tol, Math.Max(e.EffectiveTolerance, Math.Max(a.EffectiveTolerance, b.EffectiveTolerance)));
            if ((a.P - b.P).Length > own) continue;

            // Сливаем в ту вершину, у которой больше связей: так меньше правок.
            if (b.Edges.Count > a.Edges.Count) (a, b) = (b, a);
            merge[b] = a;
        }

        if (merge.Count == 0) return false;

        // 2. Правим рёбра и убираем те, у которых концы совпали.
        foreach (var face in solid.Faces.ToList())
            foreach (var loop in face.Loops.ToList())
            {
                var keep = new List<TCoEdge>();
                foreach (var ce in loop.CoEdges)
                {
                    var e = ce.Edge;
                    var a = Root(e.V0);
                    var b = Root(e.V1);
                    if (ReferenceEquals(a, b)) { RemovedEdges++; continue; }   // ребро схлопнулось
                    keep.Add(ce);
                }

                if (keep.Count == loop.CoEdges.Count) continue;

                loop.CoEdges.Clear();
                foreach (var ce in keep) loop.CoEdges.Add(ce);
            }

        // 3. Переставляем вершины у оставшихся рёбер.
        foreach (var e in solid.Edges.ToList())
        {
            var a = Root(e.V0);
            var b = Root(e.V1);
            if (!ReferenceEquals(a, e.V0)) e.V0 = a;
            if (!ReferenceEquals(b, e.V1)) e.V1 = b;
        }

        // 4. Контуры, оставшиеся без рёбер, и грани без контуров — убираем.
        foreach (var face in solid.Faces.ToList())
        {
            face.Loops.RemoveAll(l => l.CoEdges.Count == 0);
            if (face.Loops.Count != 0) continue;

            face.Shell?.Faces.Remove(face);
            RemovedFaces++;
        }

        if (warnings != null && (RemovedEdges > 0 || RemovedFaces > 0))
            warnings.Add($"Heal: {RemovedEdges} short edge(s) and {RemovedFaces} empty face(s) removed.");

        return RemovedEdges > 0 || RemovedFaces > 0;
    }

    /// <summary>Сколько рёбер тела короче допуска — мерка «грязности» без правки.</summary>
    public static int ShortEdges(TSolid solid, double tolerance = 0)
    {
        double tol = tolerance > 0 ? tolerance : Tol.Weld;
        return solid.Edges.Count(e => (e.V1.P - e.V0.P).Length <= tol);
    }
}
