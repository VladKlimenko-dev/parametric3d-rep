using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-01 (01.5): ПРОИСХОЖДЕНИЕ ПО ТОЖДЕСТВУ ПОВЕРХНОСТЕЙ — для построителей, которые карту не пишут. Грань результата,
/// лежащая на том же ОБЪЕКТЕ поверхности (или гранёной обёртке того же источника), что грань входа, — из неё. Объекты
/// поверхностей неизменяемы и переносятся построителями (обрезка, булевы, перенос рёбер), поэтому тождество объекта —
/// знание о происхождении, а не сопоставление по сходству. Неоднозначность (одну поверхность несут несколько граней входа)
/// — источник не назначается (грань «новая» с пометкой).
/// </summary>
public static class ProvenanceBySurface
{
    static object Key(TFace f) => f.Surface is FacetedSurface fs && fs.Source != null ? fs.Source : f.Surface;

    /// <summary>
    /// Карта составной операции: от грани результата — по картам промежуточных тел (каждая булева записала свою относительно
    /// своих операндов) до грани входа; не дошли (грань резца, тело без карты) — по тождеству поверхностей; иначе новая.
    /// Вид — худший по цепочке (сохранена → изменена → разделена).
    /// </summary>
    public static Provenance Compose(string operation, IReadOnlyList<TSolid> inputs, TSolid result)
    {
        var inputSet = new HashSet<TFace>(inputs.SelectMany(b => b.Faces), ReferenceEqualityComparer.Instance);
        var bySurf = Of(operation, inputs, result);
        var pv = new Provenance { Operation = operation + " (composed)" };
        foreach (var f in result.Faces)
        {
            TFace? cur = f; FaceOrigin kind = FaceOrigin.Kept; int guard = 0; TFace? src = null;
            while (cur != null && guard++ < 64)
            {
                if (inputSet.Contains(cur)) { src = cur; break; }
                var step = cur.Shell?.Solid?.Provenance?.Of(cur);
                if (step == null || step.Source == null) break;
                if (step.Kind > kind) kind = step.Kind;
                cur = step.Source;
            }
            if (src != null) { pv.Faces.Add(new FaceProvenance(f, src, kind)); continue; }
            var bs = bySurf.Of(f);
            pv.Faces.Add(bs ?? new FaceProvenance(f, null, FaceOrigin.Generated));
        }
        var used = new HashSet<TFace>(pv.Faces.Where(x => x.Source != null).Select(x => x.Source!), ReferenceEqualityComparer.Instance);
        // разделение: несколько граней результата от одной грани входа
        for (int i = 0; i < pv.Faces.Count; i++)
        {
            var x = pv.Faces[i];
            if (x.Source != null && x.Kind != FaceOrigin.Split && pv.Faces.Count(y => ReferenceEquals(y.Source, x.Source)) > 1)
                pv.Faces[i] = x with { Kind = FaceOrigin.Split };
        }
        foreach (var f in inputSet) if (!used.Contains(f)) pv.Deleted.Add(f);
        return pv;
    }

    public static Provenance Of(string operation, IReadOnlyList<TSolid> inputs, TSolid result)
    {
        var pv = new Provenance { Operation = operation + " (by surface identity)" };
        var bySurf = new Dictionary<object, List<TFace>>(ReferenceEqualityComparer.Instance);
        foreach (var f in inputs.SelectMany(b => b.Faces))
        {
            var k = Key(f);
            if (!bySurf.TryGetValue(k, out var l)) bySurf[k] = l = new List<TFace>();
            l.Add(f);
        }
        var results = result.Faces.ToList();
        var srcOf = new Dictionary<TFace, TFace?>(ReferenceEqualityComparer.Instance);
        var notes = new Dictionary<TFace, string?>(ReferenceEqualityComparer.Instance);
        foreach (var f in results)
        {
            TFace? src = null; string? note = null;
            if (bySurf.TryGetValue(Key(f), out var cands))
            {
                if (cands.Count == 1) src = cands[0];
                else note = $"surface shared by {cands.Count} input faces";
            }
            srcOf[f] = src; notes[f] = note;
        }
        var count = new Dictionary<TFace, int>(ReferenceEqualityComparer.Instance);
        foreach (var s in srcOf.Values) if (s != null) count[s] = count.TryGetValue(s, out var c) ? c + 1 : 1;
        static HashSet<object> Curves(TFace f) => new(f.Loops.SelectMany(l => l.CoEdges).Select(ce => (object)ce.Edge.Curve), ReferenceEqualityComparer.Instance);
        foreach (var f in results)
        {
            var s = srcOf[f];
            FaceOrigin kind = s == null ? FaceOrigin.Generated
                            : count[s] > 1 ? FaceOrigin.Split
                            : Curves(f).SetEquals(Curves(s)) ? FaceOrigin.Kept : FaceOrigin.Modified;
            pv.Faces.Add(new FaceProvenance(f, s, kind, notes[f]));
        }
        foreach (var f in inputs.SelectMany(b => b.Faces))
            if (!count.ContainsKey(f) && !(bySurf.TryGetValue(Key(f), out var l) && l.Count > 1 && results.Any(r => ReferenceEquals(Key(r), Key(f)))))
                pv.Deleted.Add(f);
        return pv;
    }
}
