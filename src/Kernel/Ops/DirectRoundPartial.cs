using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_DRPARTIAL=0: выбор части рёбер вершины степени n > 3 — отказ (прежний путь резцов).</summary>
    static readonly bool PartialVertexOn = Environment.GetEnvironmentVariable("P3D_DRPARTIAL") != "0";
    [ThreadStatic] static bool _inPartial;
    public static long PartialVertexBuilt;

    /// <summary>
    /// KRN-06 (06.1, 06.9): В ВЕРШИНЕ СТЕПЕНИ n > 3 ВЫБРАНА ЧАСТЬ РЁБЕР (2 … n − 1). Снятое — объединение уголков рёбер (уголок —
    /// область между гранями ребра и лентой катящегося шара, продлённая вдоль ребра): результат не зависит от порядка. Строится
    /// этапами: на этапе — рёбра, не делящие такую вершину (одиночное ребро у вершины — торец из граней, MultiEnd). Каждый этап — прямой точный построитель на исходном теле; итог — точное пересечение результатов этапов
    /// (касание лент с гранями по рёбрам контакта разрешено только здесь); отказ этапа — отказ всего (тело не тронуто).
    /// </summary>
    static IEnumerable<List<int>> Perms(List<int> xs)
    {
        if (xs.Count <= 1) { yield return new List<int>(xs); yield break; }
        for (int i = 0; i < xs.Count; i++)
        {
            var rest = xs.Where((_, j) => j != i).ToList();
            foreach (var p in Perms(rest)) { p.Insert(0, xs[i]); yield return p; }
        }
    }

    static TSolid? BuildPartialVertex(TSolid body, IReadOnlyList<EdgeRef> refs, double r, ref string? why)
    {
        var one = new List<TSolid> { body };
        var es = refs.Select(rf => FilletOp.Resolve(one, rf, out _)).ToList();
        if (es.Any(e => e == null)) { why = "an edge is not resolved"; return null; }
        var all = body.Edges.ToList();
        static bool Touch(TEdge e, Vector3d p) => (e.V0.P - p).Length < 1e-7 || (e.V1.P - p).Length < 1e-7;
        var partialV = new List<Vector3d>();
        foreach (var p in es.SelectMany(e => new[] { e!.V0.P, e.V1.P }))
        {
            if (partialV.Any(q => (q - p).Length < 1e-7)) continue;
            int deg = all.Count(e => Touch(e, p)), nsel = es.Count(e => Touch(e!, p));
            if (deg > 3 && nsel >= 2 && nsel <= deg - 1) partialV.Add(p);
        }
        if (partialV.Count == 0) { why = "no vertex with part of its edges selected"; return null; }
        // этапы: рёбра одного этапа не делят частичную вершину
        var stages = new List<List<int>>();
        for (int i = 0; i < es.Count; i++)
        {
            var vi = partialV.Where(p => Touch(es[i]!, p)).ToList();
            var st = stages.FirstOrDefault(sg => sg.All(j => !vi.Any(p => Touch(es[j]!, p))));
            if (st == null) stages.Add(new List<int> { i }); else st.Add(i);
        }
        // каждый этап — на ИСХОДНОМ теле (снятое этапа — его уголки); итог — пересечение: тело − (У₁ ∪ У₂ …) = ∩ (тело − Уᵢ)
        var parts = new List<TSolid>();
        _inPartial = true;
        try
        {
            for (int s = 0; s < stages.Count; s++)
            {
                var bl = new List<TSolid> { TopologyClone.Deep(body) }; var w = new List<string>();
                string? reason = null; var prev = Debug; Debug = m => { if (m.StartsWith("declined")) reason = m; prev?.Invoke(m); };
                bool ok;
                try { ok = TryRound(bl, stages[s].Select(i => refs[i]).ToList(), r, w); }
                finally { Debug = prev; }
                if (!ok || bl.Count != 1 || bl[0].Guarantee != GuaranteeLevel.ExactAnalytic) { why = $"stage {s + 1} of {stages.Count} ({stages[s].Count} edge(s)): {reason ?? "not exact"}"; return null; }
                parts.Add(bl[0]);
            }
        }
        finally { _inPartial = false; }
        // ПОРЯДОК ПЕРЕСЕЧЕНИЙ: ленты соседних рёбер (общая грань) дают шов, касающийся этой грани в точке встречи контактов;
        // такой шов в операнде следующей булевой — касательное событие с плоскостью (не доказывается). Сначала — этапы без общих
        // граней, затем остальные; не вышло — следующий порядок (до 6 попыток)
        HashSet<TFace> FacesOfStage(int s) => stages[s].SelectMany(i => es[i]!.CoEdges.Select(c => c.Loop?.Face)).OfType<TFace>().ToHashSet();
        var orders = new List<List<int>>();
        {
            var rest = Enumerable.Range(0, stages.Count).ToList(); var o = new List<int> { 0 }; rest.Remove(0); var acc = FacesOfStage(0);
            while (rest.Count > 0) { var nx = rest.OrderBy(j => FacesOfStage(j).Count(acc.Contains)).First(); o.Add(nx); rest.Remove(nx); acc.UnionWith(FacesOfStage(nx)); }
            orders.Add(o);
            foreach (var perm in Perms(Enumerable.Range(0, stages.Count).ToList())) { if (orders.Count >= 6) break; if (!orders.Any(x => x.SequenceEqual(perm))) orders.Add(perm); }
        }
        TSolid? cur = null; string? last = null;
        foreach (var ord in orders)
        {
            var c = TopologyClone.Deep(parts[ord[0]]); bool okAll = true;
            for (int s = 1; s < ord.Count && okAll; s++)
            {
                var l = new List<TSolid> { c }; var w = new List<string>();
                bool was = Analytic.GeneralBoolean.TangentAlongEdgeScope; Analytic.GeneralBoolean.TangentAlongEdgeScope = true;
                var prevR = BooleanOp.AnalyticRouteDebug; if (Debug != null) BooleanOp.AnalyticRouteDebug = m => Debug("partial ∩ route: " + m);
                try { BooleanOp.Intersect(l, TopologyClone.Deep(parts[ord[s]]), w); }
                catch (OperationFailedException ex) { last = "intersection of stage results failed: " + ex.Message; okAll = false; break; }
                finally { Analytic.GeneralBoolean.TangentAlongEdgeScope = was; BooleanOp.AnalyticRouteDebug = prevR; }
                if (l.Count != 1 || l[0].Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(l[0]).IsValid) { last = $"intersection of stage results is not exact ({l.Count} bodies, {l.FirstOrDefault()?.Guarantee})"; okAll = false; break; }
                c = l[0];
            }
            if (okAll) { cur = c; break; }
        }
        if (cur == null) { why = last; return null; }
        return cur;
    }
}
