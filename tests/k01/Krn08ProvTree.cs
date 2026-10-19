using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// KRN-08 (08.2): ПРОИСХОЖДЕНИЕ НА УРОВНЕ ДЕРЕВА ДЕТАЛИ. Реалистичные детали K08 (3 зерна, по 12 операций): у каждой
    /// выполненной операции результат перестроения называет тела после неё — то же тело или новое с картой происхождения;
    /// каждая карта полна (каждая грань тела — ровно одна запись, записи — только о гранях этого тела). Операции без карты
    /// построителя (первое выдавливание, точный прилив по окружности) — с картой, выведенной по несущим поверхностям; рёбра —
    /// выведенное происхождение по кривым (каждое ребро — одна запись; у операции над телом — сохранённые и новые).
    /// </summary>
    static void Krn08ProvTreeChecks()
    {
        Check("KRN08-PROVENANCE-TREE", () =>
        {
            var fails = new List<string>(); int feats = 0, maps = 0, fresh = 0, unchanged = 0, inferred = 0, edgeMaps = 0;
            for (int seed = 1; seed <= 3; seed++)
            {
                var part = RealisticPart(seed, 12, out _);
                part.Invalidate();
                var res = part.Compute(null);
                foreach (var f in part.Features)
                {
                    if (res.Errors.TryGetValue(f, out var err) && err != null) continue;
                    if (!res.Provenance.TryGetValue(f, out var list)) { if (res.FeatureMs.ContainsKey(f)) fails.Add($"seed {seed} {f.Name}: no tree provenance"); continue; }
                    feats++;
                    foreach (var bp in list)
                    {
                        if (bp.Unchanged) { unchanged++; continue; }
                        fresh++;
                        // рёбра: каждое ребро тела — одна запись; у операции над телом — есть сохранённые и новые
                        if (bp.Edges is not { } ep) fails.Add($"seed {seed} {f.Name}: no edge provenance");
                        else
                        {
                            var es = bp.Body.Edges.ToList();
                            if (ep.Edges.Count != es.Count || es.Any(e => ep.Edges.Count(x => ReferenceEquals(x.Result, e)) != 1)) fails.Add($"seed {seed} {f.Name}: edge map covers {ep.Edges.Count} of {es.Count}");
                            bool first0 = part.Features.IndexOf(f) == 0;
                            if (first0 && ep.Edges.Any(x => x.Kind != FaceOrigin.Generated)) fails.Add($"seed {seed} {f.Name}: first body edges not generated");
                            if (!first0 && (!ep.Edges.Any(x => x.Kind is FaceOrigin.Kept or FaceOrigin.Modified or FaceOrigin.Split) || !ep.Edges.Any(x => x.Kind == FaceOrigin.Generated)))
                                fails.Add($"seed {seed} {f.Name}: edge map {string.Join(",", ep.Edges.GroupBy(x => x.Kind).Select(g => g.Key + ":" + g.Count()))}");
                            edgeMaps++;
                        }
                        if (bp.Map is not { } m) { if (Environment.GetEnvironmentVariable("P3D_PROVDBG") == "1") Console.WriteLine($"  NOMAP {f.GetType().Name} {f.Name}"); continue; }
                        maps++;
                        if (bp.Inferred)
                        {
                            inferred++;
                            // первое тело — всё новое; прилив/вырез на теле — часть граней сохранена, новые есть
                            bool first = part.Features.IndexOf(f) == 0;
                            if (first && m.Faces.Any(x => x.Kind != FaceOrigin.Generated)) fails.Add($"seed {seed} {f.Name}: first body has non-generated faces");
                            if (!first && (!m.Faces.Any(x => x.Kind == FaceOrigin.Kept) || !m.Faces.Any(x => x.Kind == FaceOrigin.Generated) || !m.Faces.Any(x => x.Kind == FaceOrigin.Modified)))
                                fails.Add($"seed {seed} {f.Name}: inferred map {string.Join(",", m.Faces.GroupBy(x => x.Kind).Select(g => g.Key + ":" + g.Count()))}");
                        }
                        var faces = bp.Body.Faces.ToList();
                        var set = new HashSet<TFace>(faces, ReferenceEqualityComparer.Instance);
                        var recs = m.Faces.Where(x => set.Contains(x.Result)).GroupBy(x => x.Result, ReferenceEqualityComparer.Instance).ToList();
                        if (recs.Count != faces.Count || recs.Any(g => g.Count() != 1)) fails.Add($"seed {seed} {f.Name}: map covers {recs.Count} of {faces.Count} faces");
                        if (m.Faces.Any(x => !set.Contains(x.Result))) fails.Add($"seed {seed} {f.Name}: map names faces of another body");
                    }
                }
            }
            Log(new { test = "KRN08-PROVENANCE-TREE", feats, fresh, unchanged, maps, inferred, edgeMaps });
            if (feats < 20 || maps != fresh) fails.Add($"{feats} features, {maps} maps for {fresh} new bodies");
            Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
        });
    }
}
