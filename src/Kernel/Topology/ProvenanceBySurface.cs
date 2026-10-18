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
    /// <summary>Грань результата лежит на поверхности грани-источника (середины её рёбер — на поверхности до 1e-6 мм).</summary>
    static bool OnSourceSurface(TFace r, TFace src)
    {
        var s = src.Surface is FacetedSurface fs && fs.Source != null ? fs.Source : src.Surface;
        int n = 0;
        foreach (var ce in r.Loops.SelectMany(l => l.CoEdges).Take(6))
        {
            var c = ce.Edge.Curve; var p = c.PointAt((c.T0 + c.T1) / 2);
            try { if ((s.PointAt(s.ProjectToUV(p)) - p).Length > 1e-6) return false; } catch { return false; }
            n++;
        }
        return n > 0;
    }

    /// <summary>Пометка производной грани (на другой поверхности, чем грань-источник: стенка оболочки, копия).</summary>
    public const string Derived = "derived: another surface than the source face";
    static readonly bool DerivedOff = Environment.GetEnvironmentVariable("P3D_PROVDERIVED") == "0";
    static object Key(TFace f) => f.Surface is FacetedSurface fs && fs.Source != null ? fs.Source : f.Surface;

    /// <summary>Карта годна для результата: каждая грань результата в ней, источники — грани входа.</summary>
    public static bool Covers(Provenance? pv, TSolid result, IReadOnlyList<TSolid> inputs)
    {
        if (pv == null) return false;
        var inputSet = new HashSet<TFace>(inputs.SelectMany(b => b.Faces), ReferenceEqualityComparer.Instance);
        var mapped = new HashSet<TFace>(pv.Faces.Select(x => x.Result), ReferenceEqualityComparer.Instance);
        return result.Faces.All(mapped.Contains) && pv.Faces.All(x => x.Source == null || inputSet.Contains(x.Source));
    }

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
        // m.h2: ПРОИЗВОДНАЯ ГРАНЬ (стенка оболочки — смещённая копия грани, экземпляр массива) приходит к той же грани входа
        // через копию тела, но лежит на ДРУГОЙ поверхности. Если от грани входа есть и грань на её собственной поверхности —
        // производные — изменённые копии (как Modified у смещения и переноса в OpenCascade), а не «части» грани. P3D_PROVDERIVED=0 — как прежде (разделена).
        if (!DerivedOff)
            foreach (var g in pv.Faces.Where(x => x.Source != null).GroupBy(x => x.Source!, ReferenceEqualityComparer.Instance).ToList())
            {
                var list = g.ToList(); if (list.Count < 2) continue;
                var sf = (TFace)g.Key;
                var on = list.ToDictionary(x => x.Result, x => OnSourceSurface(x.Result, sf), (IEqualityComparer<TFace>)ReferenceEqualityComparer.Instance);
                if (!on.Values.Any(v => v) || on.Values.All(v => v)) continue;
                foreach (var x in list.Where(x => !on[x.Result]))
                {
                    int i = pv.Faces.IndexOf(x);
                    pv.Faces[i] = new FaceProvenance(x.Result, x.Source, FaceOrigin.Modified, Derived);
                }
            }
        var used = new HashSet<TFace>(pv.Faces.Where(x => x.Source != null).Select(x => x.Source!), ReferenceEqualityComparer.Instance);
        // разделение: несколько граней результата от одной грани входа
        for (int i = 0; i < pv.Faces.Count; i++)
        {
            var x = pv.Faces[i];
            if (x.Source != null && x.Kind != FaceOrigin.Split && x.Note != Derived && pv.Faces.Count(y => ReferenceEquals(y.Source, x.Source) && y.Note != Derived) > 1)
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
