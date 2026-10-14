using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// KRN-06 (06.5, 06.9): СОБЫТИЕ «ДРУГОЙ КОНТУР В ПОЛОСЕ» — отверстие или карман (полость) у скругляемого ребра, контур которого
/// на грани ребра попадает в полосу сопряжения. Прежде прямой построитель отказывал, обходной путь резцов давал многогранное
/// или негодное тело. Теперь: полость вынимается как отдельное тело (её стенки и крышки по контурам на гранях-носителях),
/// тело с заполненной полостью скругляется прямым построителем, полость вычитается обратно точной булевой — лента и стенки
/// полости пересекаются по точной кривой (как у OpenCascade: лента проходит, отверстие режет её). Выступ (бобышка) в полосе —
/// отказ: шар упёрся бы в его стенку. Мутация P3D_DRCAVITY=0.
/// </summary>
public static partial class DirectRound
{
    static readonly bool CavityOn = Environment.GetEnvironmentVariable("P3D_DRCAVITY") != "0";
    public static long CavityDetours;
    [ThreadStatic] static bool _inDetour;

    /// <summary>Полости, контуры которых на гранях выбранных рёбер ближе reach к рёбрам. null — есть контур, который не полость.</summary>
    static List<(TSolid Fill, TSolid Cut)>? StripCavities(TSolid body, List<TEdge> sel, double reach, out string? why)
    {
        why = null;
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;
        var tools = new List<(TSolid, TSolid)>(); var done = new HashSet<TFace>();
        var hosts = sel.SelectMany(e => e.CoEdges.Select(c => c.Loop?.Face)).OfType<TFace>().Distinct().ToList();
        double Dist(Vector3d p) => sel.Min(e => { double m = double.MaxValue; for (int k = 0; k <= 64; k++) m = Math.Min(m, (e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 64.0) - p).Length); return m; });
        foreach (var F in hosts)
            foreach (var L in F.Loops.Where(l => !l.IsOuter))
            {
                bool near = L.CoEdges.Any(ce => Enumerable.Range(0, 17).Any(k => Dist(ce.Edge.Curve.PointAt(ce.Edge.Curve.T0 + (ce.Edge.Curve.T1 - ce.Edge.Curve.T0) * k / 16.0)) <= reach));
                if (!near) continue;
                // ОСТРОВ: грани за рёбрами контура; переход через ребро внешнего контура соседа — сосед в острове, через ребро
                // внутреннего контура — сосед носитель (там — крышка)
                var island = new HashSet<TFace>(); var caps = new List<(TFace Host, TLoop Loop)> { (F, L) };
                var stack = new Stack<(TFace From, TEdge Via)>();
                foreach (var ce in L.CoEdges) stack.Push((F, ce.Edge));
                while (stack.Count > 0)
                {
                    var (from, via) = stack.Pop();
                    foreach (var oc in via.CoEdges)
                    {
                        var H = oc.Loop?.Face; if (H == null || H == from || island.Contains(H)) continue;
                        if (hosts.Contains(H) && H != F && oc.Loop!.IsOuter) { why = "cavity: reaches the other face of the edge"; return null; }
                        if (!oc.Loop!.IsOuter && H != from)
                        {
                            if (!caps.Any(c => c.Loop == oc.Loop)) caps.Add((H, oc.Loop));
                            continue;
                        }
                        if (H == F) continue;
                        island.Add(H);
                        if (island.Count > 64) { why = "cavity: too many faces"; return null; }
                        foreach (var l2 in H.Loops) foreach (var c2 in l2.CoEdges) stack.Push((H, c2.Edge));
                    }
                }
                if (island.Count == 0 || island.Any(done.Contains)) continue;
                if (island.Overlaps(hosts)) { why = "cavity: an island face is a face of a selected edge"; return null; }
                foreach (var g in island) done.Add(g);
                // полость или выступ: стенка от контура уходит внутрь тела (против наружной нормали носителя)
                var ce0 = L.CoEdges[0]; var p0 = ce0.Edge.Curve.PointAt((ce0.Edge.Curve.T0 + ce0.Edge.Curve.T1) / 2);
                var nF = F.NormalAt(p0);
                // по точкам рёбер острова: среднее — по ту сторону носителя, где материал (полость), или над ним (выступ)
                var pts = island.SelectMany(g => g.Loops).SelectMany(l => l.CoEdges).SelectMany(ce => Enumerable.Range(0, 9).Select(k => ce.Edge.Curve.PointAt(ce.Edge.Curve.T0 + (ce.Edge.Curve.T1 - ce.Edge.Curve.T0) * k / 8.0))).ToList();
                double side = pts.Average(q => Vector3d.Dot(q - p0, nF));
                bool inward = side < 0;
                if (!inward) { why = "cavity: a protrusion (boss) in the blend strip"; return null; }
                // тело полости: стенки острова — обращённые (наружу из полости — в материал), крышки — по контурам носителей,
                // сторона носителя (наружу из полости — наружу из тела)
                var descs = new List<FDesc>();
                foreach (var g in island)
                {
                    var d = byFace[g];
                    var c = new FDesc { Surf = d.Surf, Rev = !d.Rev, Tag = "cavity:" + d.Tag, Hint = d.Hint, N = -d.N, IsCyl = d.IsCyl, AxO = d.AxO, AxW = d.AxW, AxR = d.AxR };
                    foreach (var l in d.Loops) c.Loops.Add(l.Select(x => x.Clone()).ToList());
                    descs.Add(c);
                }
                foreach (var (host, loop) in caps)
                {
                    var d = byFace[host];
                    var ord = loop.Ordered();
                    if (ord == null) { why = "cavity: a cap loop not closed"; return null; }
                    var c = new FDesc { Surf = d.Surf, Rev = d.Rev, Tag = "cavity:cap", N = d.N };
                    c.Loops.Add(ord.Select(ce => new Seg { Curve = ce.Edge.Curve, A = ce.Start.P, B = ce.End.P, Orig = ce.Edge, Id = ce.Edge }).ToList());
                    descs.Add(c);
                }
                string? w2 = null;
                var tool = Assemble(descs, ref w2);
                if (tool == null) { why = "cavity: " + w2; return null; }
                TopologyConventions.Normalize(tool);
                var warn = new List<string>();
                if (Analytic.PrismBuild.Finish(tool, warn) > 0) { why = "cavity: the cavity body is not meshed"; return null; }
                tool.Guarantee = GuaranteeLevel.ExactAnalytic;
                if (!SolidValidator.Validate(tool).IsValid || !(AnalyticVolume.Of(tool) is double tv && tv > 0)) { why = "cavity: the cavity body is not valid"; return null; }
                // ИНСТРУМЕНТ ВЫЧИТАНИЯ — та же полость, продлённая за грани-носители призмой по контуру крышки: крышка на плоскости
                // носителя касалась бы ленты вдоль линии касания (плоскость грани касается цилиндра ленты) — булева такое касание
                // не считает точно. Продление годно, если стенки у крышки идут вдоль нормали носителя (сверлёное отверстие,
                // карман со стенками ⟂ грани); иначе — отказ.
                TSolid cut = TopologyClone.Deep(tool);
                foreach (var (host, loop) in caps)
                {
                    var nh = host.NormalAt(loop.CoEdges[0].Start.P).Normalized();
                    var prism = CapPrism(loop, nh, Math.Max(4 * reach, 1), byFace, out var whyP);
                    if (prism == null) { why = "cavity: " + whyP; return null; }
                    var cl = new List<TSolid> { cut }; var wl = new List<string>();
                    BooleanOp.Unite(cl, prism, wl);
                    if (cl.Count != 1 || cl[0].Guarantee != GuaranteeLevel.ExactAnalytic) { why = "cavity: the extended cavity is not exact"; return null; }
                    cut = cl[0];
                }
                tools.Add((tool, cut));
            }
        return tools;
    }

    /// <summary>Призма по контуру крышки наружу от носителя на высоту h (стены — продолжение стенок полости вдоль нормали).</summary>
    static TSolid? CapPrism(TLoop loop, Vector3d n, double h, Dictionary<TFace, FDesc> byFace, out string? why)
    {
        why = null;
        var ord = loop.Ordered(); if (ord == null) { why = "cap loop not closed"; return null; }
        var host = loop.Face!;
        var descs = new List<FDesc>();
        var bottom = new List<Seg>(); var top = new List<Seg>();
        Vector3d Up(Vector3d p) => p + n * h;
        foreach (var ce in ord)
        {
            var c = ce.Edge.Curve; var a = ce.Start.P; var b = ce.End.P;
            // стенка полости у этого ребра должна содержать направление n (стенка вдоль нормали носителя)
            var wall = ce.Edge.CoEdges.Select(x => x.Loop?.Face).OfType<TFace>().FirstOrDefault(f => f != host);
            if (wall == null || !byFace.TryGetValue(wall, out var wd)) { why = "cap edge without a wall"; return null; }
            var m = c.PointAt((c.T0 + c.T1) / 2);
            var wn = wd.Surf.NormalAt(wd.Surf.ProjectToUV(m)).Normalized();
            if (Math.Abs(Vector3d.Dot(wn, n)) > 1e-9) { why = "the cavity wall is not along the face normal (cannot extend)"; return null; }
            bottom.Add(new Seg { Curve = c, A = a, B = b });
            var cu = Shift(c, n * h); if (cu == null) { why = $"cap edge curve {c.GetType().Name} cannot be shifted"; return null; }
            top.Add(new Seg { Curve = cu, A = Up(a), B = Up(b) });
            // боковая грань: та же поверхность стенки (для дуги — цилиндр выдавливания, для отрезка — плоскость)
            ISurface3 side = c is LineCurve3 ? new PlaneSurface(new Frame3(a, b - a, n)) : new ExtrudedSurface(c, n);
            // наружу из полости — в материал: против наружной нормали стенки в теле
            var wOut = wn * (wd.Rev ? -1 : 1);
            var sn = side.NormalAt(side.ProjectToUV(m));
            var sd = new FDesc { Surf = side, Rev = Vector3d.Dot(sn, -wOut) < 0, Tag = "cavity:extension", N = c is LineCurve3 ? -wOut : default };
            sd.Loops.Add(new List<Seg>
            {
                new() { Curve = c, A = a, B = b },
                new() { Curve = new LineCurve3(b, Up(b)), A = b, B = Up(b) },
                new() { Curve = cu, A = Up(b), B = Up(a) },
                new() { Curve = new LineCurve3(Up(a), a), A = Up(a), B = a },
            });
            descs.Add(sd);
        }
        var hd = byFace[host];
        var bot = new FDesc { Surf = hd.Surf, Rev = !hd.Rev, Tag = "cavity:extension", N = -hd.N }; bot.Loops.Add(bottom); descs.Add(bot);
        var tp = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(Up(ord[0].Start.P), n)), Rev = false, Tag = "cavity:extension", N = n }; tp.Loops.Add(top); descs.Add(tp);
        string? w2 = null;
        var solid = Assemble(descs, ref w2);
        if (solid == null) { why = "prism: " + w2; return null; }
        TopologyConventions.Normalize(solid);
        var warn = new List<string>();
        if (Analytic.PrismBuild.Finish(solid, warn) > 0) { why = "prism not meshed"; return null; }
        solid.Guarantee = GuaranteeLevel.ExactAnalytic;
        if (!SolidValidator.Validate(solid).IsValid || !(AnalyticVolume.Of(solid) is double v && v > 0)) { why = "prism not valid"; return null; }
        return solid;
    }

    /// <summary>Кривая, сдвинутая на вектор (отрезок, дуга, эллипс); null — другой вид.</summary>
    static ICurve3? Shift(ICurve3 c, Vector3d d) => c switch
    {
        LineCurve3 l => new LineCurve3(l.A + d, l.B + d),
        ArcCurve3 a => new ArcCurve3(new Frame3(a.Frame.Origin + d, a.Frame.U, a.Frame.V), a.Radius, a.A0, a.A1),
        EllipseCurve3 e => new EllipseCurve3(new Frame3(e.Frame.Origin + d, e.Frame.U, e.Frame.V), e.RU, e.RV, e.A0, e.A1),
        _ => null
    };

    /// <summary>
    /// Обход полостей: заполнить, скруглить тем же построителем, вычесть обратно. null — не вышло (тела не тронуты).
    /// </summary>
    static TSolid? CavityDetour(TSolid body, IReadOnlyList<EdgeRef> refs, double r, ref string? why) =>
        CavityDetour(body, refs, r, w => TryRound(w, refs, r, new List<string>()), ref why);

    static TSolid? CavityDetour(TSolid body, IReadOnlyList<EdgeRef> refs, double r, Func<List<TSolid>, bool> op, ref string? why)
    {
        if (!CavityOn || _inDetour) return null;
        var sel = refs.Select(rf => FilletOp.Resolve(new List<TSolid> { body }, rf, out _)).OfType<TEdge>().ToList();
        if (sel.Count != refs.Count) return null;
        // полоса — не шире 4r от ребра (острый двугранный угол даёт r·ctg(α/2); запас)
        var tools = StripCavities(body, sel, 4 * r, out var whyC);
        if (tools == null || tools.Count == 0) { why += " | cavity: " + (whyC ?? "none"); return null; }
        var work = new List<TSolid> { TopologyClone.Deep(body) }; var w = new List<string>();
        try
        {
            _inDetour = true;
            foreach (var t in tools) BooleanOp.Unite(work, TopologyClone.Deep(t.Fill), w);
            if (work.Count != 1 || work[0].Guarantee != GuaranteeLevel.ExactAnalytic) { why += " | cavity: filling is not exact"; return null; }
            if (!op(work)) { why += " | cavity: the filled body is not blended directly"; return null; }
            foreach (var t in tools) BooleanOp.Subtract(work, TopologyClone.Deep(t.Cut), w);
            if (work.Count != 1 || work[0].Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(work[0]).IsValid) { why += $" | cavity: re-cut is not exact ({(work.Count == 1 ? work[0].Guarantee.ToString() : work.Count + " bodies")}; {string.Join(" / ", w.TakeLast(3))})"; return null; }
            Interlocked.Increment(ref CavityDetours);
            return work[0];
        }
        catch (Exception ex) { why += " | cavity: " + ex.Message; return null; }
        finally { _inDetour = false; }
    }
}
