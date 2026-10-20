using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// СБОРКА B-REP ИЗ СЛОЁВ (аналитическое ядро, этап 1). Тело задано направлением d, рамкой (U, V), уровнями и сечениями
/// слоёв (Region2). Грани — точные: боковые — плоскости и цилиндры над кусками сечений (база — ориентированный кусок,
/// область слева: нормаль «касательная × d» смотрит наружу сама), торцы — плоскости: вверх = ниже − выше, вниз = выше − ниже.
/// Рёбра на уровне общие для всех подходящих к нему граней: куски верхних краёв нижних боковых, нижних краёв верхних
/// боковых и контуров торцов режутся в общих точках (разбиение уровня), каждый кусок — одно ребро; контур любой грани —
/// цепочка этих рёбер. Вертикальные рёбра — в вершинах сечений слоёв.
/// </summary>
public static class PrismBuild
{
    static double Eps => Region2Ops.Eps;

    sealed class Level
    {
        public double H;
        public Vector3d U, V, D;
        public readonly List<TVertex> Verts = new();
        public readonly List<(Seg2 S, TEdge E)> Pieces = new();
    }

    /// <summary>Объём результата последней булевой по слоям (для самопроверки маршрута).</summary>
    [ThreadStatic] public static double LastVolume;
    /// <summary>Тело последней сборки до объединения граней (боковые грани — прямоугольные полосы).</summary>
    [ThreadStatic] public static TSolid? LastPreUnify;
    internal static void ReleaseThreadState() { LastPreUnify = null; LastPreUnifyMap = null; LastProvenance = null; }
    /// <summary>Для каждой грани копии (по порядку) — грань результата, в которую она вошла.</summary>
    [ThreadStatic] public static List<TFace>? LastPreUnifyMap;
    /// <summary>Объёмы операндов последней булевой по слоям (для независимой сверки распознавания).</summary>
    [ThreadStatic] public static double LastVolumeA, LastVolumeB;

    /// <summary>
    /// KRN-02 (02.9): ЛОКАЛЬНАЯ СИСТЕМА СЛОЁВ. 2D-координаты сечений и высоты уровней — от центра габарита первого операнда, а не
    /// от мирового начала: площадь слоя по формуле шнурования с координатами ~3e5 мм теряла ~1e-5 относительной точности (у
    /// детали 3 мм на 3e5 мм слои давали 5.99999237 при точных 6 — сверка отвергала маршрут, и цепочка уходила в многогранный
    /// путь). На время одной булевой; вне неё — ноль. Мутация P3D_PRISMLOCAL=0.
    /// </summary>
    [ThreadStatic] internal static Vector3d LocalOrigin;
    static readonly bool PrismLocalOff = Environment.GetEnvironmentVariable("P3D_PRISMLOCAL") == "0";

    /// <summary>Булева двух 2.5-мерных тел с общим направлением; null — не подходит (why).</summary>
    public static TSolid? Boolean(TSolid a, TSolid b, Bool2 op, out string why)
    {
        var prevOrigin = LocalOrigin;
        if (!PrismLocalOff)
        {
            var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); bool any = false;
            foreach (var v in a.Vertices) { lo = Vector3d.ComponentMin(lo, v.P); hi = Vector3d.ComponentMax(hi, v.P); any = true; }
            // начало — ноль у деталей в пределах ±1000 мм (поведение побитно прежнее: повторное использование сеток граней
            // сверяется побитно, G7-MESH-REUSE-IDENTICAL), дальше — центр, округлённый к кратному 1024 мм (не меняется от правки к
            // правке; сдвиг на степень двойки вычитается точно)
            var c = any ? (lo + hi) / 2 : Vector3d.Zero;
            static double Snap(double x) => Math.Abs(x) < 1000 ? 0 : Math.Round(x / 1024) * 1024;
            LocalOrigin = new Vector3d(Snap(c.X), Snap(c.Y), Snap(c.Z));
        }
        try { return BooleanCore(a, b, op, out why); }
        finally { LocalOrigin = prevOrigin; }
    }

    static TSolid? BooleanCore(TSolid a, TSolid b, Bool2 op, out string why)
    {
        // Операнд, не приведённый к соглашению о полурёбрах (сырой резец, копия тела TransformOp.Copy — контуры не
        // сцеплены), распознаётся по нормализованной копии (имена граней копия сохраняет).
        static TSolid Conventional(TSolid t)
        {
            if (t.TopologyConvention >= TopologyConventions.Version) return t;
            var c = TopologyClone.Deep(t); TopologyConventions.Normalize(c); return c;
        }
        var a0 = a; var b0 = b;
        a = Conventional(a); b = Conventional(b);
        // копия ради соглашения — те же грани в том же порядке: происхождение указывает на грани ИСХОДНЫХ операндов
        var orig = new Dictionary<TFace, TFace>(ReferenceEqualityComparer.Instance);
        foreach (var (x, x0) in new[] { (a, a0), (b, b0) })
            if (!ReferenceEquals(x, x0))
            {
                var fx = x.Faces.ToList(); var fx0 = x0.Faces.ToList();
                if (fx.Count == fx0.Count) for (int i = 0; i < fx.Count; i++) orig[fx[i]] = fx0[i];
            }
        TFace Orig(TFace f) => orig.TryGetValue(f, out var o) ? o : f;
        // Направление — по ОБОИМ операндам: сначала оси цилиндров любого из них, затем нормали плоскостей.
        Prism25? pa = null, pb = null; why = "no common direction";
        var profRec = Diagnostics.Prof.Time("призма: распознавание");
        foreach (var d0 in Prism25.DirectionCandidates(a, b))
        {
            var u0 = Math.Abs(d0.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
            var U0 = (u0 - d0 * Vector3d.Dot(u0, d0)).Normalized(); var V0 = Vector3d.Cross(d0, U0);
            pa = Prism25.RecognizeAlong(a, d0, U0, V0, out var wa);
            if (pa == null) { why = "first operand: " + wa; continue; }
            pb = Prism25.RecognizeAlong(b, d0, U0, V0, out var wb);
            if (pb == null) { why = "second operand: " + wb; pa = null; continue; }
            break;
        }
        profRec.Dispose();
        if (pa == null || pb == null) return null;
        var profSec = Diagnostics.Prof.Time("призма: сечения");
        var levels = pa.Levels.Concat(pb.Levels).OrderBy(x => x).ToList();
        var lv = new List<double>();
        foreach (var l in levels) if (lv.Count == 0 || l - lv[^1] > 1e-7) lv.Add(l);
        var secs = new List<Region2>();
        for (int k = 0; k + 1 < lv.Count; k++)
        {
            double z = (lv[k] + lv[k + 1]) / 2;
            secs.Add(Region2Ops.Boolean(pa.SectionAt(z), pb.SectionAt(z), op));
        }
        LastVolume = 0; for (int k = 0; k < secs.Count; k++) LastVolume += secs[k].Area * (lv[k + 1] - lv[k]);
        LastVolumeA = pa.Volume; LastVolumeB = pb.Volume;
        string? Namer(FaceInfo fi)
        {
            foreach (var op in new[] { pa, pb })
            {
                string? tag = fi.Side ? op.SideTagAt(fi.Carrier, (fi.Lo + fi.Hi) / 2) : op.CapTagAt(fi.Lo, fi.Inside);
                if (tag != null) return tag;
            }
            return null;
        }
        profSec.Dispose();
        TSolid? res; Dictionary<TFace, FaceInfo> info;
        using (Diagnostics.Prof.Time("призма: сборка тела")) res = Build(pa.D, pa.U, pa.V, lv, secs, out why, out info, Namer);
        LastProvenance = null;
        using var profProv = Diagnostics.Prof.Time("призма: происхождение");
        if (res != null && !ProvOff)
        {
            // KRN-08 (08.2): ИСТОЧНИК КАЖДОЙ ГРАНИ — грань операнда на той же несущей (боковая — та же прямая/окружность в её
            // пределах по высоте и месту; торец — тот же уровень, область содержит точку торца). Несколько граней результата от
            // одной — разделена; та же граница (все вершины на месте) — сохранена; иначе — изменена. Источника нет —
            // новая (не бывает у слоёв: каждая грань лежит на поверхности операнда).
            var pv = new Topology.Provenance { Operation = "boolean.prism." + op };
            var src = new Dictionary<TFace, TFace?>(ReferenceEqualityComparer.Instance);
            foreach (var (f, fi) in info)
            {
                TFace? s0 = null;
                foreach (var o in new[] { pa, pb })
                {
                    s0 = fi.Side ? o.SideFaceAt(fi.Carrier, (fi.Lo + fi.Hi) / 2) : o.CapFaceAt(fi.Lo, fi.Inside);
                    if (s0 != null) break;
                }
                src[f] = s0;
            }
            static List<Vector3d> Corners(TFace f) => f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Start.P).ToList();
            foreach (var f in res.Faces)
            {
                var s0 = src.GetValueOrDefault(f);
                if (s0 == null) { pv.Faces.Add(new Topology.FaceProvenance(f, null, Topology.FaceOrigin.Generated, "no operand face on the carrier")); continue; }
                int n = src.Values.Count(x => ReferenceEquals(x, s0));
                var ca = Corners(f); var cb = Corners(s0);
                bool same = ca.Count == cb.Count && ca.All(p => cb.Any(q => (p - q).Length < 1e-9));
                pv.Faces.Add(new Topology.FaceProvenance(f, Orig(s0), n > 1 ? Topology.FaceOrigin.Split : same ? Topology.FaceOrigin.Kept : Topology.FaceOrigin.Modified));
            }
            // неиспользованная грань операнда: на несущей грани результата и той же стороной — слилась с ней (совпадающие
            // торцы и стенки двух операндов), иначе — удалена
            foreach (var (o, body) in new[] { (pa, a), (pb, b) })
                foreach (var f in body.Faces)
                {
                    if (src.Values.Any(x => ReferenceEquals(x, f))) continue;
                    TFace? into = null;
                    foreach (var (rf, fi) in info)
                    {
                        if (!o.OnCarrierOf(f, fi.Side, fi.Carrier, fi.Lo, fi.Hi)) continue;
                        var q = rf.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Start.P).FirstOrDefault();
                        if (Vector3d.Dot(f.NormalAt(q), rf.NormalAt(q)) > 0) { into = rf; break; }
                    }
                    if (into != null) pv.Merged.Add((Orig(f), into)); else pv.Deleted.Add(Orig(f));
                }
            LastProvenance = pv;
        }
        return res;
    }

    /// <summary>KRN-08 (08.2): происхождение граней последнего результата Boolean в этом потоке.</summary>
    [ThreadStatic] public static Topology.Provenance? LastProvenance;
    static readonly bool ProvOff = Environment.GetEnvironmentVariable("P3D_PROVENANCE") == "0";

    /// <summary>Мутация P3D_CAPCOMPONENTS=0: торец уровня — одна грань из всех его кусков (прежде).</summary>
    static readonly bool CapComponentsOff = Environment.GetEnvironmentVariable("P3D_CAPCOMPONENTS") == "0";
    /// <summary>
    /// KRN-07 (07.7), KRN-03 (03.2): ТОРЕЦ — ПО КОМПОНЕНТАМ. Область торца одного уровня может состоять из нескольких
    /// несвязных кусков (верх двух брусков, объединённых в одну булеву). Прежде это была одна грань с несколькими внешними
    /// контурами и ОДНИМ именем — по точке в первом куске: у копии массива верх и низ получали имя исходного бруска. Теперь
    /// каждый внешний контур со своими отверстиями (отверстие — к наименьшему содержащему его внешнему) — своя грань.
    /// </summary>
    static IEnumerable<Region2> CapComponents(Region2 reg)
    {
        double A(List<Seg2> l) => l.Sum(x => x.AreaTerm());
        var outers = reg.Loops.Where(l => A(l) > 0).ToList();
        if (CapComponentsOff || outers.Count <= 1) { yield return reg; yield break; }
        var parts = outers.OrderBy(A).Select(o => { var g = new Region2(); g.Loops.Add(o); return g; }).ToList();
        foreach (var hole in reg.Loops.Where(l => A(l) <= 0))
        {
            var q = hole[0].At(0.5);
            var owner = parts.FirstOrDefault(g => g.Contains(q));
            if (owner == null) { yield return reg; yield break; }      // не разобрать — как прежде, одной гранью
            owner.Loops.Add(hole);
        }
        foreach (var g in parts) yield return g;
    }

    /// <summary>Точка внутри области: середина куска, сдвинутая внутрь (влево) на малую долю его длины.</summary>
    static Vector2d InsidePoint(Region2 r)
    {
        foreach (var l in r.Loops)
            foreach (var sgm in l.OrderByDescending(x => x.Length))
            {
                var t = sgm.Tangent(0.5).Normalized(); var left = new Vector2d(-t.Y, t.X);
                foreach (double k in new[] { 1e-3, 1e-2, 1e-1 })
                {
                    var q = sgm.At(0.5) + left * (k * Math.Max(1e-3, sgm.Length));
                    if (r.Contains(q)) return q;
                }
            }
        return r.Loops.Count > 0 ? r.Loops[0][0].A : Vector2d.Zero;
    }

    /// <summary>
    /// ИМЕНА ГРАНЕЙ ИЗ ОПЕРАНДОВ: боковая — имя грани операнда с той же несущей (прямая/окружность), в её пределах по
    /// высоте и по месту на несущей; торец — имя торца операнда на том же уровне, область которого содержит точку внутри
    /// торца результата. Без подходящей — служебное имя.
    /// </summary>
    static void InheritTags(Dictionary<TFace, FaceInfo> info, params Prism25[] operands)
    {
        foreach (var (f, fi) in info)
        {
            foreach (var op in operands)
            {
                string? tag = fi.Side ? op.SideTagAt(fi.Carrier, (fi.Lo + fi.Hi) / 2) : op.CapTagAt(fi.Lo, fi.Inside);
                if (tag != null) { f.Tag = tag; break; }
            }
        }
    }

    static bool SameRegion(Region2 x, Region2 y)
    {
        double scale = Math.Max(1, Math.Max(Math.Abs(x.Area), Math.Abs(y.Area)));
        if (Math.Abs(x.Area - y.Area) > 1e-9 * scale) return false;
        return Math.Abs(Region2Ops.Boolean(x, y, Bool2.Subtract).Area) < 1e-9 * scale
            && Math.Abs(Region2Ops.Boolean(y, x, Bool2.Subtract).Area) < 1e-9 * scale;
    }

    /// <summary>Сведения о грани собранного тела: несущая боковой, уровень и сторона торца, точка внутри торца.</summary>
    public sealed record FaceInfo(bool Side, Seg2 Carrier, double Lo, double Hi, bool Up, Vector2d Inside);

    /// <summary>Тело из уровней и сечений слоёв (сечения[k] — между уровнями k и k + 1).</summary>
    public static TSolid? Build(Vector3d d, Vector3d U, Vector3d V, List<double> levels0, List<Region2> secs0, out string why) =>
        Build(d, U, V, levels0, secs0, out why, out _);

    public static TSolid? Build(Vector3d d, Vector3d U, Vector3d V, List<double> levels0, List<Region2> secs0, out string why,
                                out Dictionary<TFace, FaceInfo> info) => Build(d, U, V, levels0, secs0, out why, out info, null);

    /// <summary>
    /// namer — имя грани по её сведениям (наследование из операндов). Имена присваиваются СРАЗУ при создании граней — до
    /// снятия копии для ленивой точной формы: иначе следующая многогранная операция наследовала служебные имена из неё
    /// (уступ → отверстие: грани «analytic:*»).
    /// </summary>
    public static TSolid? Build(Vector3d d, Vector3d U, Vector3d V, List<double> levels0, List<Region2> secs0, out string why,
                                out Dictionary<TFace, FaceInfo> info, Func<FaceInfo, string?>? namer)
    {
        info = new Dictionary<TFace, FaceInfo>(ReferenceEqualityComparer.Instance);
        // Пустые слои по краям и одинаковые соседние слои — слить.
        var lv = new List<double>(); var secs = new List<Region2>();
        for (int k = 0; k < secs0.Count; k++)
        {
            bool empty = Math.Abs(secs0[k].Area) < 1e-12;
            if (secs.Count == 0)
            {
                if (empty) continue;
                lv.Add(levels0[k]); secs.Add(secs0[k]); lv.Add(levels0[k + 1]); continue;
            }
            if (SameRegion(secs[^1], secs0[k])) { lv[^1] = levels0[k + 1]; continue; }
            secs.Add(secs0[k]); lv.Add(levels0[k + 1]);
        }
        while (secs.Count > 0 && Math.Abs(secs[^1].Area) < 1e-12) { secs.RemoveAt(secs.Count - 1); lv.RemoveAt(lv.Count - 1); }
        if (secs.Count == 0) { why = "the result is empty"; return null; }
        if (secs.Any(s => Math.Abs(s.Area) < 1e-12)) { why = "an empty layer between material (the result falls apart along the direction)"; return null; }

        Vector3d P3(Vector2d p, double h) => LocalOrigin + U * p.X + V * p.Y + d * h;
        var frame = new Frame3(Vector3d.Zero, U, V);

        // Контуры, подходящие к каждому уровню: края боковых граней снизу и сверху и торцы.
        int nL = lv.Count;
        var profLv = Diagnostics.Prof.Time("призма: сборка — уровни");
        var levelObjs = new Level[nL];
        var capUp = new Region2[nL]; var capDown = new Region2[nL];
        for (int k = 0; k < nL; k++)
        {
            var below = k > 0 ? secs[k - 1] : new Region2();
            var above = k < secs.Count ? secs[k] : new Region2();
            using (Diagnostics.Prof.Time("призма: уровни — торцы"))
            {
                capUp[k] = k > 0 ? Region2Ops.Boolean(below, above, Bool2.Subtract) : new Region2();
                capDown[k] = k < secs.Count ? Region2Ops.Boolean(above, below, Bool2.Subtract) : new Region2();
            }
            var all = below.Loops.SelectMany(l => l).Concat(above.Loops.SelectMany(l => l))
                      .Concat(capUp[k].Loops.SelectMany(l => l)).Concat(capDown[k].Loops.SelectMany(l => l)).ToList();
            using (Diagnostics.Prof.Time("призма: уровни — разбиение")) levelObjs[k] = Arrange(all, lv[k], P3, frame, d);
        }

        profLv.Dispose();
        var profFaces = Diagnostics.Prof.Time("призма: сборка — грани");
        var solid = new TSolid(); var shell = new TShell { Solid = solid }; solid.Shells.Add(shell);
        int faces = 0;
        var carrier = new Dictionary<TFace, Seg2>(ReferenceEqualityComparer.Instance);
        var heights = new Dictionary<TFace, (double Lo, double Hi)>(ReferenceEqualityComparer.Instance);

        // Боковые грани слоёв.
        var vertical = new Dictionary<(int Layer, TVertex Bottom), TEdge>(new LayerVertexComparer());
        TEdge Vertical(int k, Vector2d p)
        {
            var vb = FindVertex(levelObjs[k], p) ?? throw new InvalidOperationException("no vertex at a layer bottom");
            if (vertical.TryGetValue((k, vb), out var e)) return e;
            var vt = FindVertex(levelObjs[k + 1], p) ?? throw new InvalidOperationException("no vertex at a layer top");
            e = new TEdge { V0 = vb, V1 = vt, Curve = new LineCurve3(vb.P, vt.P) };
            vertical[(k, vb)] = e;
            return e;
        }
        for (int k = 0; k < secs.Count; k++)
        {
            double h0 = lv[k], h1 = lv[k + 1];
            foreach (var loop in secs[k].Loops)
                foreach (var s in loop)
                {
                    // Плоская боковая грань — ПЛОСКОСТЬ (PlaneSurface: U вдоль куска, V = d, нормаль U × V = t × d — наружу),
                    // как у многогранного пути; цилиндрическая — выдавливание дуги. Выдавливание отрезка интегратор объёма
                    // считал на дуговом вырезе с ошибкой (пол уступа с отверстием: 1.59 мм³).
                    ISurface3 surf = s.IsArc
                        ? new ExtrudedSurface(new ArcCurve3(new Frame3(P3(s.C, h0), U, V), s.R, s.T0, s.T0 + s.Sweep), d * (h1 - h0))
                        : new PlaneSurface(new Frame3(P3(s.A, h0), (P3(s.B, h0) - P3(s.A, h0)).Normalized(), d));
                    var face = new TFace { Surface = surf, Reversed = false, Tag = "analytic:side" };
                    carrier[face] = s; heights[face] = (h0, h1);
                    if (namer?.Invoke(new FaceInfo(true, s, h0, h1, false, s.At(0.5))) is { } sideName) face.Tag = sideName;
                    var l = face.AddLoop(true);
                    foreach (var (e, rev) in Cover(levelObjs[k], s)) l.Add(e, rev);                       // низ: вдоль куска
                    l.Add(Vertical(k, s.B), false);                                                     // вверх в конце
                    foreach (var (e, rev) in Cover(levelObjs[k + 1], s.Reversed())) l.Add(e, rev);       // верх: назад
                    l.Add(Vertical(k, s.A), true);                                                      // вниз в начале
                    shell.Add(face); faces++;
                }
        }

        // Торцы: вверх — контуры как есть (против часовой вокруг +d), вниз — обращённые.
        for (int k = 0; k < nL; k++)
            foreach (var (reg0, upward) in new[] { (capUp[k], true), (capDown[k], false) })
            foreach (var reg in CapComponents(reg0))
            {
                if (reg.Loops.Count == 0 || Math.Abs(reg.Area) < 1e-12) continue;
                var n = upward ? d : -d;
                var face = new TFace { Surface = new PlaneSurface(Frame3.FromNormal(P3(Vector2d.Zero, lv[k]), n)), Reversed = false, Tag = upward ? "analytic:top" : "analytic:bottom" };
                info[face] = new FaceInfo(false, default, lv[k], lv[k], upward, InsidePoint(reg));
                if (namer?.Invoke(info[face]) is { } capName) face.Tag = capName;
                foreach (var loop in reg.Loops)
                {
                    double area = loop.Sum(x => x.AreaTerm());
                    var segs = upward ? loop : loop.AsEnumerable().Reverse().Select(x => x.Reversed()).ToList();
                    var tl = face.AddLoop(area > 0);
                    foreach (var s in segs) foreach (var (e, rev) in Cover(levelObjs[k], s)) tl.Add(e, rev);
                }
                shell.Add(face); faces++;
            }

        // Копия ДО объединения граней — для производной многогранной формы: здесь каждая боковая грань — прямоугольная
        // полоса (кусок сечения в одном слое), и гранение выдавливания полной полосой (контуров оно не учитывает) верно.
        profFaces.Dispose();
        var mergedInto = new Dictionary<TFace, TFace>(ReferenceEqualityComparer.Instance);
        using (Diagnostics.Prof.Time("призма: сборка — объединение сторон")) UnifySides(shell, carrier, heights, d, U, V, mergedInto);
        using (Diagnostics.Prof.Time("призма: сборка — слияние рёбер")) MergeCollinearEdges(shell);
        // Копия ПОСЛЕ объединения граней — для ленивой точной формы: у каждого имени одна грань (в копии до объединения
        // стороны, разрезанные уровнем, были двумя полосами с одним именем — многогранный путь собирал грань результата
        // с разрывом контура 8 мм). Плоские стороны — PlaneSurface (гранится по контурам), цилиндрические — полосы
        // («прямоугольники» в развёртке, см. UnifySides). Соответствие «грань копии → грань результата» — по порядку:
        // деталь дописывает ключ операции к именам уже после операции, только в теле результата.
        var finalFaces = shell.Faces.ToList();
        using (Diagnostics.Prof.Time("призма: сборка — копия для ленивой формы")) LastPreUnify = TopologyClone.Deep(solid);
        LastPreUnifyMap = finalFaces;
        foreach (var (f, c) in carrier)
            info[f] = new FaceInfo(true, c, heights[f].Lo, heights[f].Hi, false, c.At(0.5));
        why = "";
        return solid;
    }

    /// <summary>
    /// Завершение, как у других точных путей (CrossHole, ExactCylinderCut): соглашение о полурёбрах, деление граней
    /// из нескольких областей, разметка граней по контурам, ориентация оболочки.
    /// </summary>
    /// <summary>Возвращает число граней, не построенных по контурам (у результата маршрута — повод отказать).</summary>
    public static int Finish(TSolid solid, List<string>? warnings) => Finish(solid, warnings, null);

    public static int Finish(TSolid solid, List<string>? warnings, Func<TFace, FacetedSurface?>? donor)
    {
        TopologyConventions.Report norm;
        using (Diagnostics.Prof.Time("аналитика: завершение — соглашение")) norm = TopologyConventions.Normalize(solid);
        if (solid.TopologyConvention == 0 && norm.Consistent && norm.Problems.Count == 0 && norm.Unanchored > 0)
        {
            TopologyConventions.SplitRegions(solid);
            solid.TopologyConvention = TopologyConventions.Version;
        }
        int failed;
        using (Diagnostics.Prof.Time("аналитика: завершение — разметка граней")) failed = Export.StepImport.MeshFacesFromLoops(solid, warnings, "AnalyticPrism", donor);
        Export.StepImport.OrientShellPublic(solid);
        return failed;
    }

    /// <summary>
    /// ТРЕУГОЛЬНИКИ ЦИЛИНДРИЧЕСКИХ ГРАНЕЙ ДЛЯ ЛЕНИВОЙ ТОЧНОЙ ФОРМЫ — ПО ТОЧКАМ РЁБЕР. Гранение выдавливания идёт полосой по
    /// базовой дуге, а торцы гранятся по ломаным рёбер: точки на общей дуге не совпадали (результат скругления — 129
    /// незамкнутых отрезков). Как в многогранном мире (грани — треугольники по рёбрам): грань — «прямоугольник» в развёртке
    /// (θ, z), между ломаной нижних дуг и ломаной верхних — «застёжка» по углу. Нормаль — наружу (по поверхности).
    /// </summary>
    public static void FacetCylinderSides(TSolid raw)
    {
        foreach (var f in raw.Faces.ToList())
        {
            if (f.Surface is not ExtrudedSurface ex || ex.Base is not ArcCurve3 arc) continue;
            var axis = ex.Dir.Normalized(); var o = arc.Frame.Origin;
            double h0 = Vector3d.Dot(o, axis), h1 = h0 + ex.Dir.Length;
            var bottom = new List<Vector3d>(); var top = new List<Vector3d>();
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
            {
                if (ce.Edge.Curve is not ArcCurve3) continue;
                var pts = new List<Vector3d>(); ce.Edge.Curve.Tessellate(pts, true);
                double h = Vector3d.Dot(pts[0], axis);
                var dst = Math.Abs(h - h0) < Math.Abs(h - h1) ? bottom : top;
                foreach (var p in pts) if (dst.All(q => (q - p).LengthSquared > 1e-20)) dst.Add(p);
            }
            if (bottom.Count < 2 || top.Count < 2) continue;
            // параметр вдоль базы: угол в рамке дуги от её начала в сторону обхода, доля [0, 1]
            double sw = arc.Sweep;
            double Par(Vector3d p)
            {
                var q = p - o; double a = Math.Atan2(Vector3d.Dot(q, arc.Frame.V), Vector3d.Dot(q, arc.Frame.U));
                double dd = (a - arc.A0) * Math.Sign(sw); dd %= 2 * Math.PI; if (dd < 0) dd += 2 * Math.PI;
                // точка у начала с округлением «чуть раньше начала» даёт почти 2π — вернуть к началу
                if (dd > Math.Abs(sw) + 1e-7 && Math.Abs(Math.Abs(sw) - 2 * Math.PI) > 1e-9) dd -= 2 * Math.PI;
                return Math.Clamp(dd / Math.Abs(sw), 0, 1);
            }
            bool band = Math.Abs(Math.Abs(sw) - 2 * Math.PI) < 1e-9;
            var B = bottom.Select(p => (P: p, T: Par(p))).OrderBy(x => x.T).ToList();
            var T = top.Select(p => (P: p, T: Par(p))).OrderBy(x => x.T).ToList();
            if (band) { B.Add((B[0].P, 1)); T.Add((T[0].P, 1)); }
            var tris = new List<(Vector3d, Vector3d, Vector3d)>();
            int i = 0, j = 0;
            while (i < B.Count - 1 || j < T.Count - 1)
            {
                if (i < B.Count - 1 && (j == T.Count - 1 || B[i + 1].T <= T[j + 1].T)) { tris.Add((B[i].P, B[i + 1].P, T[j].P)); i++; }
                else { tris.Add((B[i].P, T[j + 1].P, T[j].P)); j++; }
            }
            // ориентация — наружу (нормаль поверхности в середине первого треугольника)
            var (a0, b0, c0) = tris[0];
            var nTri = Vector3d.Cross(b0 - a0, c0 - a0);
            var mid = (a0 + b0 + c0) / 3;
            var nOut = ex.NormalAt(ex.ProjectToUV(mid));
            if (f.Reversed) nOut = -nOut;
            if (Vector3d.Dot(nTri, nOut) < 0) tris = tris.Select(t => (t.Item1, t.Item3, t.Item2)).ToList();
            var fs = new FacetedSurface(ex, nOut);
            foreach (var t in tris) fs.Facets.Add(t);
            f.Surface = fs;
        }
    }

    /// <summary>Одна ли несущая у двух кусков: та же прямая с тем же направлением или та же окружность с тем же обходом.</summary>
    static bool SameCarrier(Seg2 a, Seg2 b)
    {
        if (a.IsArc != b.IsArc) return false;
        if (a.IsArc) return (a.C - b.C).Length <= Eps && Math.Abs(a.R - b.R) <= Eps && Math.Sign(a.Sweep) == Math.Sign(b.Sweep);
        var da = (a.B - a.A).Normalized(); var db = (b.B - b.A).Normalized();
        if (Vector2d.Dot(da, db) < 1 - 1e-12) return false;
        var n = new Vector2d(-da.Y, da.X);
        return Math.Abs(Vector2d.Dot(b.A - a.A, n)) <= Eps;
    }

    /// <summary>
    /// ОБЪЕДИНЕНИЕ БОКОВЫХ ГРАНЕЙ НА ОДНОЙ НЕСУЩЕЙ (в духе UnifySameDomain): грани с одной несущей и общим ребром — в
    /// группу; внутренние рёбра группы удаляются, оставшиеся полурёбра заново собираются в контуры по вершинам. Стороны
    /// бруска, разрезанные уровнем дна кармана, — снова одна грань; две половины окружности слоя — цельный пояс.
    /// </summary>
    static void UnifySides(TShell shell, Dictionary<TFace, Seg2> carrier, Dictionary<TFace, (double Lo, double Hi)> heights,
                           Vector3d d, Vector3d U, Vector3d V, Dictionary<TFace, TFace> mergedInto)
    {
        var sides = carrier.Keys.ToList();
        var parent = new Dictionary<TFace, TFace>(ReferenceEqualityComparer.Instance);
        foreach (var f in sides) parent[f] = f;
        TFace Find(TFace x) { while (!ReferenceEquals(parent[x], x)) x = parent[x] = parent[parent[x]]; return x; }
        var byEdge = new Dictionary<TEdge, List<TFace>>(ReferenceEqualityComparer.Instance);
        foreach (var f in sides)
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
            { if (!byEdge.TryGetValue(ce.Edge, out var l)) byEdge[ce.Edge] = l = new(); l.Add(f); }
        foreach (var (e, fs) in byEdge)
            if (fs.Count == 2 && !ReferenceEquals(fs[0], fs[1]) && SameCarrier(carrier[fs[0]], carrier[fs[1]]))
                parent[Find(fs[0])] = Find(fs[1]);
        // Цилиндрическая группа должна остаться «прямоугольником» в развёртке (θ, z): в каждом слое группы — тот же набор
        // дуг (стопка одинаковых дуг или полные пояса). Гранение выдавливания идёт полной полосой и L-образную
        // цилиндрическую грань не опишет. Иначе — объединение только внутри слоёв.
        var groups = new List<List<TFace>>();
        foreach (var g0 in sides.GroupBy(Find, ReferenceEqualityComparer.Instance).Where(g => g.Count() > 1))
        {
            var mem = g0.ToList();
            if (!carrier[mem[0]].IsArc) { groups.Add(mem); continue; }
            static string PieceKey(Seg2 c) => $"{Math.Round(c.A.X / Eps)},{Math.Round(c.A.Y / Eps)},{Math.Round(c.B.X / Eps)},{Math.Round(c.B.Y / Eps)}";
            var byLayer = mem.GroupBy(m => heights[m]).ToList();
            var sig = byLayer.Select(l => string.Join("|", l.Select(m => PieceKey(carrier[m])).OrderBy(x => x, StringComparer.Ordinal))).Distinct().Count();
            if (sig == 1) groups.Add(mem);
            else foreach (var l in byLayer) if (l.Count() > 1) groups.Add(l.ToList());
        }
        foreach (var members in groups)
        {
            var cnt = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
            foreach (var ce in members.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges))) cnt[ce.Edge] = cnt.GetValueOrDefault(ce.Edge) + 1;
            var rest = members.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Where(ce => cnt[ce.Edge] == 1)
                              .Select(ce => (ce.Edge, ce.Reversed, Start: ce.Start, End: ce.End)).ToList();
            var used = new bool[rest.Count];
            var loops = new List<List<(TEdge, bool)>>();
            bool ok = true;
            for (int i = 0; i < rest.Count && ok; i++)
            {
                if (used[i]) continue;
                var loop = new List<(TEdge, bool)>(); int cur = i; var origin = rest[i].Start;
                for (int guard = 0; guard <= rest.Count; guard++)
                {
                    used[cur] = true; loop.Add((rest[cur].Edge, rest[cur].Reversed));
                    if (ReferenceEquals(rest[cur].End, origin)) break;
                    int next = -1;
                    for (int k = 0; k < rest.Count; k++) if (!used[k] && ReferenceEquals(rest[k].Start, rest[cur].End)) { next = k; break; }
                    if (next < 0) { ok = false; break; }
                    cur = next;
                }
                loops.Add(loop);
            }
            if (!ok) continue;                                             // не замыкается — грани как были
            // Поверхность объединённой грани: несущая на нижнем уровне группы, высота — по всей группе.
            double lo = members.Min(m => heights[m].Lo), hi = members.Max(m => heights[m].Hi);
            var cr = carrier[members[0]];
            Vector3d P3(Vector2d p, double h) => LocalOrigin + U * p.X + V * p.Y + d * h;
            ISurface3 surf;
            if (!cr.IsArc) surf = new PlaneSurface(new Frame3(P3(cr.A, lo), (P3(cr.B, lo) - P3(cr.A, lo)).Normalized(), d));
            else
            {
                // База — ВСЯ цепочка дуг слоя (а не дуга первого куска): гранение выдавливания идёт полосой по базе, и
                // при базе из одного куска ленивая форма объединённой грани не замыкалась.
                var layer = members.Where(m => heights[m].Lo == members.Min(x => heights[x].Lo)).Select(m => carrier[m]).ToList();
                double t0 = cr.T0, sweep = 0;
                var starts = layer.Where(p => !layer.Any(q => (q.B - p.A).Length <= Eps)).ToList();
                bool closedChain = starts.Count == 0;
                var cur = closedChain ? layer[0] : starts[0];
                t0 = cur.T0;
                var left = new List<Seg2>(layer); left.Remove(cur);
                sweep = cur.Sweep;
                while (left.Count > 0)
                {
                    int nx = left.FindIndex(q => (q.A - cur.B).Length <= Eps);
                    if (nx < 0) break;
                    cur = left[nx]; left.RemoveAt(nx); sweep += cur.Sweep;
                }
                if (closedChain || Math.Abs(Math.Abs(sweep) - 2 * Math.PI) < 1e-9) sweep = Math.Sign(cr.Sweep) * 2 * Math.PI;
                surf = new ExtrudedSurface(new ArcCurve3(new Frame3(P3(cr.C, lo), U, V), cr.R, t0, t0 + sweep), d * (hi - lo));
            }
            if (GeneralBoolean.CorruptMergeForTest && loops.Count > 1) { GeneralBoolean.CorruptMergeForTest = false; loops.RemoveAt(loops.Count - 1); }
            var nf = new TFace { Surface = surf, Reversed = false, Tag = members[0].Tag };
            foreach (var l in loops) { var tl = nf.AddLoop(true); foreach (var (e, r) in l) tl.Add(e, r); }
            // 05.7: ДОКАЗАТЕЛЬСТВО ОБЪЕДИНЕНИЯ — точная площадь объединённой грани равна сумме площадей частей (по кривым рёбер; знак —
            // обход), иначе части остаются раздельными (площадь не считается — тоже). P3D_UNIFYPROOF=0 — без доказательства.
            if (!UnifyProofOff)
            {
                double? am = Topology.AnalyticVolume.FaceArea(nf);
                double sum = 0; bool okA = am != null;
                foreach (var m in members) { if (Topology.AnalyticVolume.FaceArea(m) is double a1) sum += Math.Abs(a1); else okA = false; }
                if (!okA || Math.Abs(Math.Abs(am!.Value) - sum) > 1e-9 * sum + 1e-12)
                {
                    foreach (var ce in nf.Loops.SelectMany(l => l.CoEdges)) ce.Edge.CoEdges.Remove(ce);
                    GeneralBoolean.MergesRefused++;
                    continue;
                }
            }
            foreach (var m in members)
            {
                // полурёбра убираемой грани отсоединяются от рёбер (иначе ребро «видит» лишние грани)
                foreach (var ce in m.Loops.SelectMany(l => l.CoEdges)) ce.Edge.CoEdges.Remove(ce);
                int at = shell.Faces.IndexOf(m); if (at >= 0) shell.Faces.RemoveAt(at);
            }
            shell.Add(nf);
            carrier[nf] = carrier[members[0]]; heights[nf] = (lo, hi);
            foreach (var m in members) mergedInto[m] = nf;
            foreach (var m in members) { carrier.Remove(m); heights.Remove(m); }
        }
    }

    /// <summary>
    /// Вершина, у которой осталось ровно два коллинеарных отрезка (вертикальные рёбра соседних слоёв после объединения
    /// граней), убирается: отрезки сливаются в один во всех контурах.
    /// </summary>
    /// <summary>Мутация P3D_MERGEFAST=0: слияние с полным пересчётом соседства после каждого шага (прежнее, квадратичное).</summary>
    static readonly bool UnifyProofOff = Environment.GetEnvironmentVariable("P3D_UNIFYPROOF") == "0";
    static readonly bool MergeFastOff = Environment.GetEnvironmentVariable("P3D_MERGEFAST") == "0";
    /// <summary>Проверки: прежнее слияние в этом потоке (сравнение результатов).</summary>
    [ThreadStatic] internal static bool MergeOldForTest;

    internal static void MergeCollinearEdges(TShell shell)
    {
        if (!MergeFastOff && !MergeOldForTest) { MergeCollinearEdgesFast(shell); return; }
        for (bool again = true; again;)
        {
            again = false;
            var inc = new Dictionary<TVertex, HashSet<TEdge>>(ReferenceEqualityComparer.Instance);
            foreach (var ce in shell.Faces.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)))
                foreach (var v in new[] { ce.Edge.V0, ce.Edge.V1 })
                { if (!inc.TryGetValue(v, out var hs)) inc[v] = hs = new(ReferenceEqualityComparer.Instance); hs.Add(ce.Edge); }
            foreach (var (v, es) in inc)
            {
                if (es.Count != 2) continue;
                var e1 = es.First(); var e2 = es.Last();
                var a = ReferenceEquals(e1.V0, v) ? e1.V1 : e1.V0; var b = ReferenceEquals(e2.V0, v) ? e2.V1 : e2.V0;
                TEdge ne;
                if (e1.Curve is LineCurve3 && e2.Curve is LineCurve3)
                {
                    var u1 = (v.P - a.P).Normalized(); var u2 = (b.P - v.P).Normalized();
                    if (Vector3d.Dot(u1, u2) < 1 - 1e-12) continue;
                    ne = new TEdge { V0 = a, V1 = b, Curve = new LineCurve3(a.P, b.P) };
                }
                else if (e1.Curve is ArcCurve3 c1 && e2.Curve is ArcCurve3 c2
                         && (c1.Frame.Origin - c2.Frame.Origin).Length <= Eps && Math.Abs(c1.Radius - c2.Radius) <= Eps
                         && Math.Abs(Math.Abs(Vector3d.Dot(c1.Frame.W.Normalized(), c2.Frame.W.Normalized())) - 1) < 1e-12)
                {
                    // Дуги одной окружности: обход a → v → b; углы — в рамке первой. Полная окружность — замкнутое ребро.
                    double Ang(Vector3d p) { var q = p - c1.Frame.Origin; return Math.Atan2(Vector3d.Dot(q, c1.Frame.V), Vector3d.Dot(q, c1.Frame.U)); }
                    double s1 = ReferenceEquals(e1.V0, a) ? c1.Sweep : -c1.Sweep;                                   // a → v
                    double s2 = (ReferenceEquals(e2.V0, v) ? c2.Sweep : -c2.Sweep) * Math.Sign(Vector3d.Dot(c1.Frame.W, c2.Frame.W));   // v → b, в рамке первой
                    if (Math.Sign(s1) != Math.Sign(s2)) continue;
                    double total = s1 + s2;
                    if (Math.Abs(total) > 2 * Math.PI + 1e-9) continue;
                    double a0 = Ang(a.P);
                    bool closed = ReferenceEquals(a, b);
                    ne = new TEdge { V0 = a, V1 = closed ? a : b, Curve = new ArcCurve3(c1.Frame, c1.Radius, a0, a0 + (closed ? Math.Sign(total) * 2 * Math.PI : total)) };
                }
                else continue;
                foreach (var f in shell.Faces)
                    for (int li = 0; li < f.Loops.Count; li++)
                    {
                        var loop = f.Loops[li];
                        var ces = loop.CoEdges.ToList();
                        int i1 = ces.FindIndex(c => ReferenceEquals(c.Edge, e1)), i2 = ces.FindIndex(c => ReferenceEquals(c.Edge, e2));
                        if (i1 < 0 || i2 < 0) continue;
                        int n = ces.Count;
                        int firstI = (i1 + 1) % n == i2 ? i1 : i2;                   // первое из пары по обходу
                        bool forward = ReferenceEquals(ces[firstI].Start, a);        // обход a → v → b ?
                        var list = new List<(TEdge, bool)>();
                        for (int k = 0; k < n; k++)
                        {
                            if (k == firstI) { list.Add((ne, !forward)); continue; }
                            if (k == (firstI + 1) % n) continue;
                            list.Add((ces[k].Edge, ces[k].Reversed));
                        }
                        bool outer = loop.IsOuter;
                        foreach (var ce in ces) ce.Edge.CoEdges.Remove(ce);         // старый контур — отсоединить
                        f.Loops.RemoveAt(li);
                        var nl = new TLoop { IsOuter = outer, Face = f };
                        foreach (var (e, r) in list) nl.Add(e, r);
                        f.Loops.Insert(li, nl);
                    }
                again = true; break;
            }
        }
    }

    /// <summary>
    /// KRN-10 (10.7): ТО ЖЕ СЛИЯНИЕ БЕЗ ПЕРЕСЧЁТА ВСЕГО ТЕЛА. Прежде после каждого слитого стыка соседство вершин строилось
    /// заново по всем граням, а контуры искались перебором всех граней — на пластине с отверстиями 101 МБ выделений из 616 на
    /// перестройку. Теперь соседство — одно на всё слияние и правится на месте, контуры — по полурёбрам самих рёбер. Порядок
    /// тот же: каждый раз берётся первый годный стык в порядке первого появления вершин (как у полного пересчёта), поэтому
    /// результат — тот же.
    /// </summary>
    static void MergeCollinearEdgesFast(TShell shell)
    {
        // ранг ребра — номер его первого полуребра в обходе граней (как у полного пересчёта: порядок добавления в множества);
        // слитое ребро стоит на месте первого из пары
        var rank = new Dictionary<TEdge, long>(ReferenceEqualityComparer.Instance);
        var inc = new Dictionary<TVertex, List<TEdge>>(ReferenceEqualityComparer.Instance);
        long pos = 0;
        foreach (var ce in shell.Faces.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)))
        {
            var e = ce.Edge;
            if (!rank.ContainsKey(e)) rank[e] = pos;
            pos++;
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (!inc.TryGetValue(v, out var l)) inc[v] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
        }
        // порядок вершин — первое появление: ребро наименьшего ранга, у него V0 раньше V1
        (long, int) Key(TVertex v) { long best = long.MaxValue; int side = 1; foreach (var e in inc[v]) { long r = rank[e]; int sd = ReferenceEquals(e.V0, v) ? 0 : 1; if (r < best || (r == best && sd < side)) { best = r; side = sd; } } return (best, side); }
        var faceOrder = shell.Faces.ToList();
        while (true)
        {
            bool merged = false;
            foreach (var v in inc.Keys.Where(x => inc[x].Count > 0).OrderBy(Key).ToList())
            {
                var es = inc[v];
                if (es.Count != 2) continue;
                var e1 = rank[es[0]] <= rank[es[1]] ? es[0] : es[1]; var e2 = ReferenceEquals(e1, es[0]) ? es[1] : es[0];
                var a = ReferenceEquals(e1.V0, v) ? e1.V1 : e1.V0; var b = ReferenceEquals(e2.V0, v) ? e2.V1 : e2.V0;
                TEdge ne;
                if (e1.Curve is LineCurve3 && e2.Curve is LineCurve3)
                {
                    var u1 = (v.P - a.P).Normalized(); var u2 = (b.P - v.P).Normalized();
                    if (Vector3d.Dot(u1, u2) < 1 - 1e-12) continue;
                    ne = new TEdge { V0 = a, V1 = b, Curve = new LineCurve3(a.P, b.P) };
                }
                else if (e1.Curve is ArcCurve3 c1 && e2.Curve is ArcCurve3 c2
                         && (c1.Frame.Origin - c2.Frame.Origin).Length <= Eps && Math.Abs(c1.Radius - c2.Radius) <= Eps
                         && Math.Abs(Math.Abs(Vector3d.Dot(c1.Frame.W.Normalized(), c2.Frame.W.Normalized())) - 1) < 1e-12)
                {
                    double Ang(Vector3d p) { var q = p - c1.Frame.Origin; return Math.Atan2(Vector3d.Dot(q, c1.Frame.V), Vector3d.Dot(q, c1.Frame.U)); }
                    double s1 = ReferenceEquals(e1.V0, a) ? c1.Sweep : -c1.Sweep;
                    double s2 = (ReferenceEquals(e2.V0, v) ? c2.Sweep : -c2.Sweep) * Math.Sign(Vector3d.Dot(c1.Frame.W, c2.Frame.W));
                    if (Math.Sign(s1) != Math.Sign(s2)) continue;
                    double total = s1 + s2;
                    if (Math.Abs(total) > 2 * Math.PI + 1e-9) continue;
                    double a0 = Ang(a.P);
                    bool closed = ReferenceEquals(a, b);
                    ne = new TEdge { V0 = a, V1 = closed ? a : b, Curve = new ArcCurve3(c1.Frame, c1.Radius, a0, a0 + (closed ? Math.Sign(total) * 2 * Math.PI : total)) };
                }
                else continue;
                var loops = e1.CoEdges.Select(c => c.Loop).OfType<TLoop>().Where(l => e2.CoEdges.Any(c => ReferenceEquals(c.Loop, l))).Distinct().ToList();
                loops.Sort((x, y) => { int fx = faceOrder.IndexOf(x.Face!), fy = faceOrder.IndexOf(y.Face!); return fx != fy ? fx.CompareTo(fy) : x.Face!.Loops.IndexOf(x).CompareTo(y.Face!.Loops.IndexOf(y)); });
                long? neRank = null;
                foreach (var loop in loops)
                {
                    var f = loop.Face!; int li = f.Loops.IndexOf(loop);
                    var ces = loop.CoEdges.ToList();
                    int i1 = ces.FindIndex(c => ReferenceEquals(c.Edge, e1)), i2 = ces.FindIndex(c => ReferenceEquals(c.Edge, e2));
                    int n = ces.Count;
                    int firstI = (i1 + 1) % n == i2 ? i1 : i2;
                    neRank ??= rank[ces[firstI].Edge];          // новое ребро — на месте первого из пары в первом контуре
                    bool forward = ReferenceEquals(ces[firstI].Start, a);
                    var list = new List<(TEdge, bool)>();
                    for (int k = 0; k < n; k++)
                    {
                        if (k == firstI) { list.Add((ne, !forward)); continue; }
                        if (k == (firstI + 1) % n) continue;
                        list.Add((ces[k].Edge, ces[k].Reversed));
                    }
                    bool outer = loop.IsOuter;
                    foreach (var ce in ces) ce.Edge.CoEdges.Remove(ce);
                    f.Loops.RemoveAt(li);
                    var nl = new TLoop { IsOuter = outer, Face = f };
                    foreach (var (e, r) in list) nl.Add(e, r);
                    f.Loops.Insert(li, nl);
                }
                rank[ne] = neRank ?? Math.Min(rank[e1], rank[e2]);
                inc[v].Clear();
                inc[a].Remove(e1); inc[b].Remove(e2);
                if (!inc[a].Contains(ne)) inc[a].Add(ne);
                if (!inc[b].Contains(ne)) inc[b].Add(ne);
                merged = true; break;
            }
            if (!merged) break;
        }
    }

    sealed class LayerVertexComparer : IEqualityComparer<(int, TVertex)>
    {
        public bool Equals((int, TVertex) x, (int, TVertex) y) => x.Item1 == y.Item1 && ReferenceEquals(x.Item2, y.Item2);
        public int GetHashCode((int, TVertex) x) => HashCode.Combine(x.Item1, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x.Item2));
    }

    static TVertex? FindVertex(Level L, Vector2d p)
    {
        foreach (var v in L.Verts) if ((v.P - Lift(L, p)).Length <= Eps) return v;
        return null;
    }
    static Vector3d Lift(Level L, Vector2d p) => LocalOrigin + L.U * p.X + L.V * p.Y + L.D * L.H;

    /// <summary>Разбиение уровня: все куски режутся в общих точках; каждый уникальный кусок — одно ребро.</summary>
    /// <summary>Мутация P3D_ARRANGEBOX=0: все пары кусков уровня — без отбора по габариту (прежнее).</summary>
    static readonly bool ArrangeBoxOff = Environment.GetEnvironmentVariable("P3D_ARRANGEBOX") == "0";

    static Level Arrange(List<Seg2> segs, double h, Func<Vector2d, double, Vector3d> p3, Frame3 frame, Vector3d d)
    {
        var L = new Level { H = h, U = frame.U, V = frame.V, D = d };
        // точки разбиения: концы и взаимные пересечения
        var pts = new List<Vector2d>();
        void AddPt(Vector2d p) { foreach (var q in pts) if ((q - p).Length <= Eps) return; pts.Add(p); }
        foreach (var s in segs) { AddPt(s.A); AddPt(s.B); }
        // KRN-10 (10.7): габарит куска (у дуги — её окружность) с запасом; пары врозь пересечений не имеют — не считаются
        // (прежде — каждая пара с перечислителем: на пластине с отверстиями ~10⁵ пар на перестройку). Точки — те же.
        var box = new (Vector2d Lo, Vector2d Hi)[segs.Count];
        for (int i = 0; i < segs.Count; i++)
        {
            var sg = segs[i]; double m = 1e3 * Eps + 1e-9;
            box[i] = sg.IsArc ? (sg.C - new Vector2d(sg.R + m), sg.C + new Vector2d(sg.R + m))
                              : (Vector2d.ComponentMin(sg.A, sg.B) - new Vector2d(m), Vector2d.ComponentMax(sg.A, sg.B) + new Vector2d(m));
        }
        for (int i = 0; i < segs.Count; i++)
            for (int j = i + 1; j < segs.Count; j++)
            {
                if (!ArrangeBoxOff && !MergeOldForTest && (box[i].Lo.X > box[j].Hi.X || box[j].Lo.X > box[i].Hi.X || box[i].Lo.Y > box[j].Hi.Y || box[j].Lo.Y > box[i].Hi.Y)) continue;
                foreach (var q in Region2Ops.Intersections(segs[i], segs[j])) AddPt(q);
            }
        foreach (var p in pts) L.Verts.Add(new TVertex(p3(p, h)));
        // куски
        foreach (var s in segs)
            foreach (var piece in SplitByPoints(s, pts))
            {
                if (FindPiece(L, piece) != null) continue;
                var v0 = FindVertex(L, piece.A)!; var v1 = FindVertex(L, piece.B)!;
                ICurve3 curve = piece.IsArc
                    ? new ArcCurve3(new Frame3(p3(piece.C, h), frame.U, frame.V), piece.R, piece.T0, piece.T0 + piece.Sweep)
                    : new LineCurve3(v0.P, v1.P);
                L.Pieces.Add((piece, new TEdge { V0 = v0, V1 = v1, Curve = curve }));
            }
        return L;
    }

    static IEnumerable<Seg2> SplitByPoints(Seg2 s, List<Vector2d> pts)
    {
        var cuts = pts.Where(p => (p - s.A).Length > Eps && (p - s.B).Length > Eps && OnSeg(s, p))
                      .Select(p => (U: Region2Ops.ParamOf(s, p), P: p)).Where(c => c.U > 0 && c.U < 1).OrderBy(c => c.U).ToList();
        double u0 = 0; var p0 = s.A;
        foreach (var (u, p) in cuts) { yield return s.Part(u0, u, p0, p); u0 = u; p0 = p; }
        yield return s.Part(u0, 1, p0, s.B);
    }

    static bool OnSeg(Seg2 s, Vector2d p)
    {
        if (!s.IsArc)
        {
            var dd = s.B - s.A; double l = dd.Length; if (l == 0) return false;
            double u = Vector2d.Dot(p - s.A, dd) / (l * l);
            return u > 0 && u < 1 && (p - (s.A + dd * u)).Length <= Eps;
        }
        if (Math.Abs((p - s.C).Length - s.R) > Eps) return false;
        return Region2Ops.OnArc(s, Math.Atan2(p.Y - s.C.Y, p.X - s.C.X), out double uu) && uu > 0 && uu < 1;
    }

    /// <summary>Ребро уровня для куска (в любом направлении): (ребро, обращено ли).</summary>
    static (TEdge E, bool Rev)? FindPiece(Level L, Seg2 piece)
    {
        var m = piece.At(0.5);
        foreach (var (s, e) in L.Pieces)
        {
            if (s.IsArc != piece.IsArc) continue;
            if ((s.At(0.5) - m).Length > 1e-7) continue;
            if ((s.A - piece.A).Length <= Eps && (s.B - piece.B).Length <= Eps) return (e, false);
            if ((s.A - piece.B).Length <= Eps && (s.B - piece.A).Length <= Eps) return (e, true);
        }
        return null;
    }

    /// <summary>Цепочка рёбер уровня, покрывающая кусок s в его направлении.</summary>
    static IEnumerable<(TEdge E, bool Rev)> Cover(Level L, Seg2 s)
    {
        var pts = L.Verts.Select(v => new Vector2d(Vector3d.Dot(v.P - LocalOrigin, L.U), Vector3d.Dot(v.P - LocalOrigin, L.V))).ToList();
        foreach (var piece in SplitByPoints(s, pts))
        {
            var f = FindPiece(L, piece) ?? throw new InvalidOperationException($"no level edge covers a piece at h {L.H}");
            yield return f;
        }
    }
}
