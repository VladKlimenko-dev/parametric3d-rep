using OpenTK.Mathematics;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// KRN-06/07 (06.12, g.08): ГРАНИ ПО ФОРМУЛЕ В ОБЩЕЙ БУЛЕВОЙ. Лента катящегося шара, грань точной протяжки (поверхность по
/// формуле сечения) и угловой лоскут общий маршрут не распознавал («unrecognised surface») — следующая булева над телом с
/// такой гранью шла многогранно. Для булевой такая грань получает представление NURBS (рациональный B-сплайн: сечения —
/// дуги/отрезки точно, вдоль хода — интерполяция сечений формулы) с измеренным отходом от формулы не больше 0.2 точности
/// модели — та же граница, что у табличного представления самой формулы. Операнд копируется, грани заменяются; результат
/// несёт грани NURBS. null — замена не нужна или не удалась (маршрут — прежний). Мутация P3D_BOOLPROXY=0.
/// </summary>
public static class NurbsProxy
{
    static readonly bool Off = Environment.GetEnvironmentVariable("P3D_BOOLPROXY") == "0";
    public static long Proxied, Restored;

    /// <summary>Замены последнего Of в этом потоке: NURBS → прежняя поверхность грани (со стороной) и её вершины; край-строка → прежняя кривая.</summary>
    sealed class Rec { public ISurface3 Orig = null!; public bool OrigRev; public List<Vector3d> Verts = new(); public Vector3d Probe, ProbeN; }
    [ThreadStatic] static Dictionary<NurbsSurface, Rec>? _faces;
    [ThreadStatic] static Dictionary<Curves.ICurve3, Curves.ICurve3>? _rows;
    static readonly bool RestoreOff = Environment.GetEnvironmentVariable("P3D_PROXYRESTORE") == "0";

    /// <summary>Начать сбор замен (перед заменой обоих операндов).</summary>
    public static void Begin() { _faces = new(ReferenceEqualityComparer.Instance); _rows = new(ReferenceEqualityComparer.Instance); }

    /// <summary>
    /// ГРАНИ, НЕ ЗАДЕТЫЕ БУЛЕВОЙ, — обратно по формуле: та же поверхность-представление и те же вершины контура — прежняя
    /// поверхность (и сторона), рёбра-края — прежние кривые. Разрезанные грани остаются NURBS. Мутация P3D_PROXYRESTORE=0.
    /// </summary>
    public static List<(int Face, ISurface3 Proxy, bool Rev)> Restore(TSolid r)
    {
        var swaps = new List<(int, ISurface3, bool)>();
        if (RestoreOff || _faces == null || _faces.Count == 0) return swaps;
        int fi = -1;
        foreach (var f in r.Faces)
        {
            fi++;
            var su = GeometryAudit.AuthoritativeSurface(f);
            if (su is not NurbsSurface ns || !_faces.TryGetValue(ns, out var rec)) continue;
            var vs = f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Start.P).ToList();
            if (vs.Count != rec.Verts.Count || vs.Any(v => !rec.Verts.Any(q => (q - v).Length < 1e-9))) continue;
            var nNow = f.NormalAt(rec.Probe);
            bool flip = Vector3d.Dot(nNow, rec.ProbeN) < 0;
            swaps.Add((fi, f.Surface, f.Reversed));
            f.Surface = rec.Orig; f.Reversed = rec.OrigRev ^ flip;
            foreach (var e in f.Edges) if (_rows!.TryGetValue(e.Curve, out var oc)) e.Curve = oc;
            Interlocked.Increment(ref Restored);
        }
        return swaps;
    }

    /// <summary>
    /// Копия результата с представлениями NURBS у возвращённых граней — для классификатора проверки «согласие с CSG»
    /// (грань по формуле классификатором не распознаётся; представление — в пределах 0.2 точности модели). Мутация
    /// P3D_PROXYVIEW=0 — проверка без копии (отказ маршрута, как прежде).
    /// </summary>
    public static TSolid? ProxyView(TSolid r, List<(int Face, ISurface3 Proxy, bool Rev)> swaps)
    {
        if (ViewOff || swaps.Count == 0) return null;
        var c = TopologyClone.Deep(r); var fl = c.Faces.ToList();
        if (fl.Count != r.Faces.Count()) return null;
        foreach (var (i, sp, rev) in swaps) { fl[i].Surface = sp; fl[i].Reversed = rev; }
        return c;
    }
    static readonly bool ViewOff = Environment.GetEnvironmentVariable("P3D_PROXYVIEW") == "0";

    static bool Formula(TFace f) => GeometryAudit.AuthoritativeSurface(f) is SectionSurface { IsProcedural: true } or CornerPatchSurface;

    public static TSolid? Of(TSolid s, out string? why)
    {
        why = null;
        if (Off || !s.Faces.Any(Formula)) return null;
        double budget = 0.2 * ToleranceContext.Current.ModelAccuracy;
        var c = TopologyClone.Deep(s);
        // ОДНО ЧИСЛО СЕЧЕНИЙ у всех лент: соседние грани (звенья протяжки) интерполируют общий рельс по тем же узлам — их края
        // совпадают до бита, и линии пересечения с другим операндом сходятся в концах на рельсе
        int nMax = 16;
        foreach (var f in c.Faces)
            if (GeometryAudit.AuthoritativeSurface(f) is SectionSurface { IsProcedural: true } s0 && Export.StepExact.BandSpan(f, s0) is { } sp0
                && BandNurbs.Fit(s0, sp0.Ua, sp0.Ub, budget, 4096) is { } f0)
                nMax = Math.Max(nMax, f0.S.CountU - 1);
        var rows = new List<Curves.SplineCurve3>();           // края v = 0, 1 замещённых граней (рациональные сплайны по u)
        foreach (var f in c.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            (NurbsSurface S, double Deviation)? fit = src switch
            {
                SectionSurface { IsProcedural: true } ss when Export.StepExact.BandSpan(f, ss) is { } sp => BandNurbs.Fit(ss, sp.Ua, sp.Ub, budget, 4096, nMax),
                CornerPatchSurface cp => CornerPatchNurbs.Fit(cp, budget, 64),
                _ => null,
            };
            if (src is SectionSurface or CornerPatchSurface)
            {
                if (fit == null) { why = $"face {f.Id}: no NURBS representation within {budget:E1} mm"; return null; }
                {
                    var probe0 = f.InteriorHint ?? f.Loops[0].CoEdges[0].Start.P;
                    var rec = new Rec { Orig = f.Surface, OrigRev = f.Reversed, Verts = f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Start.P).ToList(), Probe = probe0, ProbeN = f.NormalAt(probe0) };
                    _faces?.TryAdd(fit.Value.S, rec);
                }
                // сторона: нормаль NURBS в точке грани против прежней нормали грани
                var p = f.Loops[0].CoEdges[0].Start.P;
                var hint = f.InteriorHint ?? p;
                var before = f.NormalAt(hint);
                var ns = fit.Value.S;
                var after = ns.NormalAt(ns.ProjectToUV(hint)) * (f.Reversed ? -1 : 1);
                if (Vector3d.Dot(before, after) < 0) f.Reversed = !f.Reversed;
                // треугольники грани (затравки луча классификатора, разметка) — прежние: та же форма в пределах гранения
                if (f.Surface is FacetedSurface oldFs && oldFs.Facets.Count > 0)
                {
                    var nfs = new FacetedSurface(ns, oldFs.FallbackNormal);
                    foreach (var t in oldFs.Facets.ReadView) nfs.Facets.Add(t);
                    f.Surface = nfs;
                }
                else f.Surface = ns;
                if (src is SectionSurface)
                    foreach (int j in new[] { 0, ns.CountV - 1 })
                        rows.Add(new Curves.SplineCurve3(Enumerable.Range(0, ns.CountU).Select(i => ns.ControlPoint(i, j)).ToList(), ns.DegreeU,
                                                         Enumerable.Range(0, ns.CountU).Select(i => ns.Weight(i, j)).ToList(), ns.KnotsU));
            }
        }
        // рёбра-процедурные кривые (рельсы протяжки, линии касания лент) — кубическим сплайном в том же бюджете: события
        // такой кривой с поверхностями другого операнда доказываются (у процедурной кривой — только выборкой)
        foreach (var e in c.Edges)
        {
            if (e.Curve is not Curves.ProcCurve3 pc) continue;
            // край замещённой грани, совпадающий с ребром (рельс протяжки, линия касания ленты), — сам этот край: ребро лежит на
            // грани точно; иначе — кубический сплайн в бюджете
            Curves.SplineCurve3? sp = null;
            foreach (var row in rows)
            {
                bool ok = true, rv = false;
                foreach (bool rev in new[] { false, true })
                {
                    ok = true;
                    for (int k = 0; k <= 8 && ok; k++)
                    {
                        double fr = k / 8.0;
                        var q = pc.PointAt(pc.T0 + (pc.T1 - pc.T0) * fr); var r0 = row.PointAt(row.T0 + (row.T1 - row.T0) * (rev ? 1 - fr : fr));
                        if ((q - r0).Length > 10 * budget + 1e-9) ok = false;
                    }
                    if (ok) { rv = rev; break; }
                }
                if (ok) { sp = rv ? (Curves.SplineCurve3)row.Reversed() : row; break; }
            }
            if (sp == null)
            {
                var fit = Curves.SplineFit.Fit(pc, budget, 1024);
                if (fit == null) { why = $"edge {e.Id}: no spline within {budget:E1} mm"; return null; }
                sp = fit.Value.Spline;
            }
            var cps = sp.ControlPoints.ToList();
            var origCurve = e.Curve;
            // концы — ровно в вершинах ребра (по направлению кривой)
            bool fw = (pc.PointAt(pc.T0) - e.V0.P).LengthSquared <= (pc.PointAt(pc.T0) - e.V1.P).LengthSquared || ReferenceEquals(e.V0, e.V1);
            cps[0] = fw ? e.V0.P : e.V1.P; cps[^1] = fw ? e.V1.P : e.V0.P;
            e.Curve = new Curves.SplineCurve3(cps, sp.Degree, sp.Weights, sp.Knots);
            _rows?.TryAdd(e.Curve, origCurve);
        }
        Interlocked.Increment(ref Proxied);
        return c;
    }
}
