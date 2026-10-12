using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// K05. СОГЛАШЕНИЕ О НАПРАВЛЕНИИ ПОЛУРЁБЕР.
///
/// Признак Reversed у полуребра в ядре не поддерживался: у тела после булевой обе грани обходили
/// КАЖДОЕ ребро в одну сторону (сферический вырез: 15 рёбер из 15), у бруска — 4 из 12. Отсюда
/// невозможно было ни требовать встречного обхода в проверке, ни выдавать верные
/// ORIENTED_EDGE в STEP, ни опираться на направление контура.
///
/// Соглашение (версия 1):
///   • каждый контур грани раскладывается на замкнутые КОЛЬЦА, полурёбра кольца идут подряд,
///     конец каждого — начало следующего;
///   • идя по кольцу, материал грани остаётся СЛЕВА, если смотреть против наружной нормали
///     тела (наружный контур — против часовой стрелки, отверстие — по часовой);
///   • поэтому у ребра замкнутого тела два полуребра обходят его навстречу;
///   • Reversed = true, если обход идёт от V1 к V0.
///
/// Направление выводится не из геометрии каждой грани, а из связности: кольца связаны рёбрами,
/// и встречность задаёт относительное направление всех колец компоненты. Геометрия нужна
/// один раз на компоненту — для выбора знака по самой крупной плоской грани (её наибольшее
/// кольцо наружное и обязано обходить наружную нормаль против часовой стрелки).
/// </summary>
public static class TopologyConventions
{
    public const int Version = 1;

    /// <summary>Раскладывать ли многокольцевые контуры на отдельные (часть соглашения версии 1).</summary>
    private static bool _gSplitRings = Environment.GetEnvironmentVariable("P3D_SPLITRINGS") != "0";
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.SplitRings), если он задан; иначе общее по умолчанию.</summary>
    public static bool SplitRings
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.SplitRings : _gSplitRings;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gSplitRings, value, "TopologyConventions.SplitRings");
    }

    public sealed class Report
    {
        public int Faces, Loops, Rings, MultiRingLoops, UnclosedRings;
        public int Components, Unanchored, Contradictions, IrregularEdges;
        public int ReversedChanged, OrderChanged, SplitLoops, RolesChanged, MultiRegionFaces, FacesSplit;
        public int SameDirectionBefore, SameDirectionAfter;
        public readonly List<string> Problems = new();
        public bool Consistent => UnclosedRings == 0 && Contradictions == 0 && IrregularEdges == 0;
        public override string ToString() =>
            $"faces {Faces}, loops {Loops}, rings {Rings} (multi-ring loops {MultiRingLoops}), components {Components}, " +
            $"unanchored {Unanchored}, unclosed {UnclosedRings}, contradictions {Contradictions}, irregular edges {IrregularEdges}, " +
            $"same-direction edges {SameDirectionBefore}→{SameDirectionAfter}, Reversed changed {ReversedChanged}, reordered loops {OrderChanged}, " +
            $"split loops {SplitLoops}, roles changed {RolesChanged}, multi-region faces {MultiRegionFaces}, faces split {FacesSplit}";
    }

    private sealed class Ring
    {
        public TLoop Loop = null!;
        public readonly List<(TCoEdge Ce, int Dir)> Items = new();   // Dir +1: V0→V1
        public int Sign;                                              // 0 — ещё не решено
        public int Component = -1;
    }

    /// <summary>Посчитать, что изменила бы нормализация, ничего не меняя.</summary>
    public static Report Analyze(TSolid solid) => Run(solid, apply: false);

    /// <summary>
    /// Привести тело к соглашению: порядок полурёбер в контуре и признаки Reversed. Геометрия,
    /// рёбра, вершины и состав контуров не меняются. Тело помечается версией соглашения только
    /// если всё согласовано (кольца замкнуты, противоречий нет, каждая компонента опознана).
    /// </summary>
    public static Report Normalize(TSolid solid) => Run(solid, apply: true);
    /// <summary>Мутация P3D_NORMPROV=0: приведение к соглашению теряет карту происхождения (прежде).</summary>
    static readonly bool KeepProvOff = Environment.GetEnvironmentVariable("P3D_NORMPROV") == "0";

    private static Report Run(TSolid solid, bool apply)
    {
        // KRN-01 (01.5): соглашение не меняет граней (только обход полурёбер) — карта происхождения остаётся годной
        var keepProv = apply && !KeepProvOff ? solid.Provenance : null;
        if (apply) solid.BeginEdit("TopologyConventions.Normalize", EditKind.Layout);
        if (keepProv != null) solid.SetProvenance(keepProv);
        var rep = new Report();
        var rings = new List<Ring>();
        var faces = solid.Faces.ToList();
        rep.Faces = faces.Count;
        var _pt = Diagnostics.Prof.Time("соглашение: обход колец");
        rep.SameDirectionBefore = SameDirection(solid);

        foreach (var f in faces)
            foreach (var loop in f.Loops)
            {
                rep.Loops++;
                var found = Trace(loop, out bool closed);
                if (!closed) rep.UnclosedRings++;
                if (found.Count > 1) rep.MultiRingLoops++;
                rings.AddRange(found);
            }
        rep.Rings = rings.Count;
        _newell = new(); _outerOf = new(); _all = rings; _ptsCache = new(); _roleCache = new(); _boxCache = new(ReferenceEqualityComparer.Instance);
        _byFace = CacheOff ? null : rings.Where(r => r.Loop.Face != null).GroupBy(r => r.Loop.Face!).ToDictionary(g => g.Key, g => g.ToList());

        // Ребро → его использования кольцами.
        var uses = new Dictionary<TEdge, List<(Ring R, int Dir)>>(ReferenceEqualityComparer.Instance);
        foreach (var r in rings)
            foreach (var (ce, dir) in r.Items)
            {
                if (!uses.TryGetValue(ce.Edge, out var l)) uses[ce.Edge] = l = new();
                l.Add((r, dir));
            }

        _pt.Dispose(); var _pc = Diagnostics.Prof.Time("соглашение: компоненты и якоря");
        // Связность колец через рёбра: s1·d1 = −s2·d2.
        foreach (var start in rings)
        {
            if (start.Component >= 0) continue;
            int comp = rep.Components++;
            start.Sign = 1; start.Component = comp;
            var _pq = Diagnostics.Prof.Time("соглашение: обход компоненты");
            var queue = new Queue<Ring>(); queue.Enqueue(start);
            var members = new List<Ring>();
            while (queue.Count > 0)
            {
                var r = queue.Dequeue(); members.Add(r);
                foreach (var (ce, dir) in r.Items)
                {
                    var l = uses[ce.Edge];
                    if (l.Count != 2) continue;
                    if (ReferenceEquals(l[0].R, l[1].R))
                    {
                        // Шов: ребро пройдено одним кольцом дважды — обязано навстречу.
                        if (l[0].Dir == l[1].Dir) rep.Contradictions++;
                        continue;
                    }
                    var (o, od) = ReferenceEquals(l[0].R, r) ? l[1] : l[0];
                    int want = -r.Sign * dir * od;      // знак, при котором o обходит ребро навстречу
                    if (o.Sign == 0) { o.Sign = want; o.Component = comp; queue.Enqueue(o); }
                    else if (o.Sign != want) rep.Contradictions++;
                }
            }

            _pq.Dispose();
            // Знак компоненты — по наибольшему кольцу плоской грани в этой компоненте. Компонента —
            // не обязательно тело целиком: кольцо отверстия связано рёбрами со стенкой отверстия,
            // но не с наружным контуром той же грани. Поэтому роль кольца (наружное или
            // отверстие) берётся из его грани: наружное — наибольшее по площади.
            var _pan = Diagnostics.Prof.Time("соглашение: якорь (плоские кольца)");
            Ring? anchor = null; double best = 0;
            foreach (var r in members)
            {
                if (!IsPlanarRing(r)) continue;
                double a = NewellOf(r).Length;
                if (a > best) { best = a; anchor = r; }
            }
            // KRN-03: КОМПОНЕНТА ИЗ ОДНИХ ШВОВ (целый тор: два кольца-шва одной грани) — разрез параметризации, а не граница
            // материала; её направление стороны тела не задаёт, якорь не нужен (прежде — «unanchored», соглашение тора не
            // ставилось, объём не считался).
            _pan.Dispose();
            bool seamOnly = members.All(r => r.Items.All(it => uses[it.Ce.Edge] is { Count: 2 } l2 && ReferenceEquals(l2[0].R.Loop.Face, l2[1].R.Loop.Face)));
            if (seamOnly) continue;
            if (anchor == null || best < 1e-12)
            {
                // Компонента без плоских колец (стенка поперечного отверстия): знак — по
                // треугольникам разбиения её грани. Они ориентированы наружу, и их сторона,
                // лежащая на краю грани, идёт так, как требует соглашение.
                int? bySign;
                using (Diagnostics.Prof.Time("соглашение: сторона материала"))
                    bySign = (MaterialSide ? SignFromMaterialSide(members, rings) : null) ?? SignFromFacets(members) ?? SignFromLocalNormal(members);
                if (bySign == null) { rep.Unanchored++; continue; }
                if (bySign < 0) foreach (var r in members) r.Sign = -r.Sign;
                continue;
            }
            if (anchor.Sign * Vector3d.Dot(NewellOf(anchor), OutwardNormal(anchor)) * RoleOf(anchor) < 0)
                foreach (var r in members) r.Sign = -r.Sign;
        }

        // РОЛЬ КОЛЬЦА ПЛОСКОЙ ГРАНИ — ПО ЕГО ОБХОДУ. Против часовой стрелки (если смотреть против
        // наружной нормали) — граница области, по часовой — отверстие. Грань после булевой может
        // нести несколько областей (паз разрезал верх бруска надвое): «наибольшее кольцо
        // наружное, остальные отверстия» было бы неверно. Проверка: у каждой плоской грани есть
        // хотя бы одна область, и каждое отверстие лежит внутри какой-то области этой грани.
        _pc.Dispose(); var _pr = Diagnostics.Prof.Time("соглашение: роли колец");
        var roleBySign = new Dictionary<Ring, int>();
        foreach (var r in rings)
        {
            if (!IsPlanarRing(r) || r.Sign == 0) continue;
            var nw = NewellOf(r);
            if (nw.Length < 1e-12) continue;
            roleBySign[r] = r.Sign * Vector3d.Dot(nw, OutwardNormal(r)) > 0 ? 1 : -1;
        }
        foreach (var g in roleBySign.Keys.GroupBy(r => r.Loop.Face!))
        {
            var outers = g.Where(r => roleBySign[r] > 0).ToList();
            var holes = g.Where(r => roleBySign[r] < 0).ToList();
            if (outers.Count == 0)
            {
                rep.Contradictions++;
                if (rep.Problems.Count < 8) rep.Problems.Add($"face {g.Key.Id}: no ring bounds a region");
                continue;
            }
            if (outers.Count > 1) rep.MultiRegionFaces++;
            var n = OutwardNormal(outers[0]).Normalized();
            foreach (var hole in holes)
            {
                var p = RingPoints(hole, +1)[0];
                if (!outers.Any(o => Inside(RingPoints(o, +1), p, n)))
                {
                    rep.Contradictions++;
                    if (rep.Problems.Count < 8) rep.Problems.Add($"face {g.Key.Id}: a hole lies outside every region");
                }
            }
        }

        _pr.Dispose(); var _pa = Diagnostics.Prof.Time("соглашение: применение");
        rep.IrregularEdges = uses.Values.Count(l => l.Count != 2);

        if (apply)
        {
            foreach (var g in rings.GroupBy(r => r.Loop))
            {
                var loop = g.Key;
                var newOrder = new List<TCoEdge>(loop.CoEdges.Count);
                foreach (var r in g)
                {
                    var items = r.Sign >= 0 ? r.Items.ToList() : Enumerable.Reverse(r.Items).Select(x => (x.Ce, x.Dir)).ToList();
                    // Кольцо начинается с полуребра наименьшего ребра: повторная нормализация
                    // даёт тот же порядок (неподвижная точка), а порядок не зависит от истории списка.
                    int k0 = 0;
                    for (int k = 1; k < items.Count; k++) if (items[k].Ce.Edge.Id < items[k0].Ce.Edge.Id) k0 = k;
                    if (k0 > 0) items = items.Skip(k0).Concat(items.Take(k0)).ToList();
                    foreach (var (ce, dir) in items)
                    {
                        bool rev = dir * (r.Sign >= 0 ? 1 : -1) < 0;
                        if (ce.Reversed != rev) { ce.Reversed = rev; rep.ReversedChanged++; }
                        newOrder.Add(ce);
                    }
                }
                if (newOrder.Count == loop.CoEdges.Count && !newOrder.SequenceEqual(loop.CoEdges))
                {
                    rep.OrderChanged++;
                    loop.CoEdges.Clear();
                    loop.CoEdges.AddRange(newOrder);
                }
            }

            // Роль контура плоской грани — по обходу его кольца (см. выше).
            foreach (var r in rings)
                if (roleBySign.TryGetValue(r, out int role) && r.Loop.CoEdges.Count == r.Items.Count && r.Loop.IsOuter != (role > 0))
                { r.Loop.IsOuter = role > 0; rep.RolesChanged++; }

            // КАЖДОЕ КОЛЬЦО — СВОЙ КОНТУР. Сборка тела из многоугольников клала наружный контур и
            // отверстия грани в один список, и всё, что читает контуры (запись STEP: один
            // EDGE_LOOP на контур; выбор построителя сетки по числу контуров), видело одно кольцо
            // там, где их несколько.
            if (SplitRings && rep.UnclosedRings == 0)
                foreach (var g in rings.GroupBy(r => r.Loop).Where(g => g.Count() > 1).ToList())
                {
                    var loop = g.Key; var face = loop.Face!;
                    var list = g.ToList();
                    Ring outer = IsFlat(face.Surface)
                        ? list.Where(r => roleBySign.GetValueOrDefault(r) > 0).OrderByDescending(r => NewellOf(r).Length).FirstOrDefault() ?? list[0]
                        : list[0];
                    int at = face.Loops.IndexOf(loop);
                    foreach (var r in list)
                    {
                        if (ReferenceEquals(r, outer)) continue;
                        // У кривой грани роль кольца по обходу не определяется: кольца полосы стенки —
                        // её края, а не вырезы. Помечать второе кольцо отверстием нельзя: по признаку
                        // «есть внутренний контур с кривой пересечения» показная сетка уводит грань на
                        // точный построитель для вала с поперечным отверстием, и стенка наклонного
                        // отверстия выходила наполовину вывернутой (найдено K08).
                        var nl = new TLoop { Face = face, IsOuter = !IsFlat(face.Surface) || roleBySign.GetValueOrDefault(r) > 0 };
                        // Порядок — как в уже переписанном контуре (порядок обхода), а не как в
                        // кольце при трассировке: у кольца с отрицательным знаком они обратны.
                        // Ошибка давала контуры, записанные против обхода; найдена объёмом K08.
                        var members = new HashSet<TCoEdge>(r.Items.Select(x => x.Ce));
                        var ordered = loop.CoEdges.Where(members.Contains).ToList();
                        foreach (var ce in ordered) { loop.CoEdges.Remove(ce); ce.Loop = nl; nl.CoEdges.Add(ce); }
                        face.Loops.Insert(++at, nl);
                        rep.SplitLoops++;
                    }
                }
            if (rep.Consistent && rep.Unanchored == 0)
            {
                using (Diagnostics.Prof.Time("соглашение: многообластные грани")) rep.FacesSplit = SplitMultiRegionFaces(solid);
                solid.TopologyConvention = Version;
                solid.DisplayMeshKey = null;
            }
        }
        _pa.Dispose();
        rep.SameDirectionAfter = apply ? SameDirection(solid) : -1;
        _newell = null; _outerOf = null; _all = null; _ptsCache = null; _roleCache = null; _boxCache = null; _byFace = null;
        return rep;
    }

    /// <summary>Сколько рёбер с двумя полурёбрами обходятся ими в одну сторону.</summary>
    public static int SameDirection(TSolid solid) =>
        solid.Edges.Count(e => e.CoEdges.Count == 2 && e.CoEdges[0].Reversed == e.CoEdges[1].Reversed);

    /// <summary>
    /// Разложить контур на кольца. Направление каждого полуребра в кольце выбирается по
    /// связности (конец предыдущего — начало следующего), а не по прежнему признаку Reversed.
    /// </summary>
    private static List<Ring> Trace(TLoop loop, out bool allClosed)
    {
        allClosed = true;
        var result = new List<Ring>();
        var left = new List<TCoEdge>(loop.CoEdges);

        bool Same(TVertex a, TVertex b) =>
            ReferenceEquals(a, b) || (a.P - b.P).Length <= Math.Max(a.EffectiveTolerance, b.EffectiveTolerance);

        while (left.Count > 0)
        {
            var ring = new Ring { Loop = loop };
            // Начало — по прежнему направлению первого оставшегося полуребра.
            var first = left[0]; left.RemoveAt(0);
            int dir0 = first.Reversed ? -1 : 1;
            ring.Items.Add((first, dir0));
            TVertex startV = dir0 > 0 ? first.Edge.V0 : first.Edge.V1;
            TVertex at = dir0 > 0 ? first.Edge.V1 : first.Edge.V0;

            while (!ReferenceEquals(at, startV))
            {
                int pick = -1, pickDir = 0;
                // Сперва — полуребро, чьё прежнее направление продолжает обход, затем любое
                // инцидентное (с разворотом).
                for (int pass = 0; pass < 2 && pick < 0; pass++)
                    for (int i = 0; i < left.Count; i++)
                    {
                        var c = left[i];
                        int d = c.Reversed ? -1 : 1;
                        var s = d > 0 ? c.Edge.V0 : c.Edge.V1;
                        var e = d > 0 ? c.Edge.V1 : c.Edge.V0;
                        if (pass == 0 && Same(s, at)) { pick = i; pickDir = d; break; }
                        if (pass == 1 && Same(e, at)) { pick = i; pickDir = -d; break; }
                    }
                // Замыкание по допуску — только когда продолжать некуда: иначе микроребро (концы
                // ближе допуска вершины) замыкало бы «кольцо» из одного полуребра.
                if (pick < 0) { if (!Same(at, startV)) allClosed = false; break; }
                var ce = left[pick]; left.RemoveAt(pick);
                ring.Items.Add((ce, pickDir));
                at = pickDir > 0 ? ce.Edge.V1 : ce.Edge.V0;
            }
            result.Add(ring);
        }
        return result;
    }

    [ThreadStatic] private static Dictionary<Ring, Vector3d>? _newell;
    [ThreadStatic] private static Dictionary<TFace, Ring>? _outerOf;
    [ThreadStatic] private static List<Ring>? _all;
    // KRN-10: кэши одного прохода нормализации. Роль кольца (вложенность) пересчитывалась для каждой компоненты, с
    // повторной разметкой всех колец грани: у пластины с 96 отверстиями — 97×97 разметок колец на операцию, 2.2 с из 4.6 с
    // перестроения детали и 3.2 ГБ выделений; рост сверхквадратичный. Точки кольца, роль, габарит — один раз за проход.
    // Мутация P3D_CONVCACHE=0.
    static readonly bool CacheOff = Environment.GetEnvironmentVariable("P3D_CONVCACHE") == "0";
    [ThreadStatic] private static Dictionary<Ring, List<Vector3d>>? _ptsCache;
    [ThreadStatic] private static Dictionary<Ring, int>? _roleCache;
    [ThreadStatic] private static Dictionary<List<Vector3d>, (Vector3d Lo, Vector3d Hi)>? _boxCache;
    [ThreadStatic] private static Dictionary<TFace, List<Ring>>? _byFace;

    private static bool IsPlanarRing(Ring r) => r.Loop.Face is { } f && IsFlat(f.Surface);

    /// <summary>
    /// K05. ГРАНЬ ИЗ НЕСКОЛЬКИХ ОБЛАСТЕЙ ДЕЛИТСЯ НА ГРАНИ. Паз, разрезавший верх бруска, оставлял
    /// одну грань с двумя наружными контурами: в STEP это две FACE_OUTER_BOUND у одной грани (по
    /// стандарту — одна), а история и ссылки видели одну грань там, где их две. Здесь каждая
    /// область (наружный контур и отверстия внутри него) становится своей гранью с той же
    /// поверхностью и тем же именем; треугольники гранения делятся по областям.
    /// Только плоские грани (у них роль кольца определена обходом).
    /// </summary>
    /// <summary>Деление плоских граней из нескольких областей (для тел, согласованных по построению).</summary>
    public static int SplitRegions(TSolid solid) => SplitMultiRegionFaces(solid);

    private static int SplitMultiRegionFaces(TSolid solid)
    {
        int made = 0;
        foreach (var shell in solid.Shells)
            foreach (var face in shell.Faces.ToList())
            {
                if (!IsFlat(face.Surface)) { if (SplitCurvedRegions && face.Loops.Count >= 2) made += SplitCurvedByConnectivity(shell, face); continue; }
                var outers = face.Loops.Where(l => l.IsOuter).ToList();
                if (outers.Count < 2) continue;
                var n = face.NormalAt(outers[0].CoEdges[0].Start.P).Normalized();
                List<Vector3d> Poly(TLoop l) { var pts = new List<Vector3d>(); foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0); return pts; }
                double Area(List<Vector3d> pts) { var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]); return Math.Abs(Vector3d.Dot(nw, n)) / 2; }
                var polys = outers.ToDictionary(l => l, Poly);
                // Отверстие — к наименьшей области, внутри которой оно лежит.
                var holesOf = outers.ToDictionary(l => l, _ => new List<TLoop>());
                foreach (var h in face.Loops.Where(l => !l.IsOuter))
                {
                    var p0 = Poly(h)[0];
                    var owner = outers.Where(o => Inside(polys[o], p0, n)).OrderBy(o => Area(polys[o])).FirstOrDefault() ?? outers[0];
                    holesOf[owner].Add(h);
                }
                var fsOld = face.Surface as Surfaces.FacetedSurface;
                // g.11: треугольник — области, у которой он внутри наружного контура И вне её отверстий (наименьшей из таких):
                // по одному наружному контуру остров внутри отверстия другой области отдавал свои треугольники той области
                // (брусок минус тор: грань y = −3 — 159 сторон без пары). Мутация P3D_REGIONFACETS=0.
                var holePolys = outers.ToDictionary(o => o, o => holesOf[o].Select(Poly).ToList());
                TLoop? RegionOf(Vector3d c)
                {
                    TLoop? best = null; double bestA = double.MaxValue;
                    foreach (var o in outers)
                    {
                        if (!Inside(polys[o], c, n)) continue;
                        if (!RegionFacetsOld && holePolys[o].Any(h => Inside(h, c, n))) continue;
                        double a = Area(polys[o]); if (a < bestA) { bestA = a; best = o; }
                    }
                    return best;
                }
                // Первая область остаётся этой гранью, прочие — новые грани.
                foreach (var o in outers.Skip(1))
                {
                    var nf = new TFace { Tag = face.Tag, Reversed = face.Reversed, MaterialSlot = face.MaterialSlot };
                    if (!RegionFacetsOld) nf.SourceTag = face.SourceTag;
                    bool InRegion(Vector3d c) => RegionFacetsOld ? Inside(polys[o], c, n) : ReferenceEquals(RegionOf(c), o);
                    if (fsOld != null)
                    {
                        var nfs = new Surfaces.FacetedSurface(fsOld.Source, face.NormalAt(polys[o][0]).Normalized());
                        foreach (var t in fsOld.Facets.ToList())
                            if (InRegion((t.A + t.B + t.C) / 3)) { nfs.Facets.Add(t); fsOld.Facets.Remove(t); }
                        // Видимая сетка делится так же (её точки рёбер общие с соседями — сбрасывать
                        // её нельзя: разметка по треугольникам гранения разошлась бы с соседними гранями).
                        if (fsOld.Display != null)
                        {
                            var keepD = new List<(Vector3d, Vector3d, Vector3d)>(); var keepN = new List<(Vector3d, Vector3d, Vector3d)>();
                            var moveD = new List<(Vector3d, Vector3d, Vector3d)>(); var moveN = new List<(Vector3d, Vector3d, Vector3d)>();
                            for (int i = 0; i < fsOld.Display.Count; i++)
                            {
                                var t = fsOld.Display[i];
                                bool here = InRegion((t.A + t.B + t.C) / 3);
                                (here ? moveD : keepD).Add(t);
                                if (fsOld.DisplayNormals != null && i < fsOld.DisplayNormals.Count) (here ? moveN : keepN).Add(fsOld.DisplayNormals[i]);
                            }
                            bool hasN = fsOld.DisplayNormals != null;
                            fsOld.SetDisplay(keepD, hasN ? keepN : null); nfs.SetDisplay(moveD, hasN ? moveN : null);
                        }
                        nf.Surface = nfs;
                    }
                    else nf.Surface = face.Surface;
                    foreach (var l in new[] { o }.Concat(holesOf[o]))
                    {
                        face.Loops.Remove(l); l.Face = nf; nf.Loops.Add(l);
                    }
                    shell.Add(nf);
                    made++;
                }
            }
        return made;
    }

    private static bool _gSplitCurvedRegions = Environment.GetEnvironmentVariable("P3D_SPLITCURVED") != "0";
    /// <summary>
    /// Поставка 4: делить ли КРИВЫЕ грани из нескольких областей (значение контекста — KernelSettings.SplitCurvedRegions;
    /// P3D_SPLITCURVED=0 — нет).
    /// </summary>
    public static bool SplitCurvedRegions
    {
        get => KernelContext.Current is { } kc ? kc.Settings.SplitCurvedRegions : _gSplitCurvedRegions;
        set => KernelContext.SetGlobal(ref _gSplitCurvedRegions, value, "TopologyConventions.SplitCurvedRegions");
    }

    /// <summary>
    /// ПОСТАВКА 4: ДЕЛЕНИЕ КРИВОЙ ГРАНИ ИЗ НЕСКОЛЬКИХ ОБЛАСТЕЙ — ПО СВЯЗНОСТИ ЕЁ ТРЕУГОЛЬНИКОВ, а не по развёртке.
    /// Роли колец в (u, v) у периодических поверхностей (пояса, швы, полюса) определять хрупко; гранёная поверхность
    /// несёт треугольники разбиения из точной булевой, и области грани — их связные компоненты (сосед — общее ребро).
    /// Каждый контур лежит на краю ровно одной компоненты и относится к ней по своим вершинам. Делится, только если всё
    /// согласовано: компонент ≥ 2, каждый контур целиком в одной, каждая компонента с контуром. Иначе — без изменений.
    /// Пример: вал с кольцевой проточкой — боковая грань из верхнего и нижнего поясов.
    /// </summary>
    private static int SplitCurvedByConnectivity(TShell shell, TFace face)
    {
        if (face.Surface is not Surfaces.FacetedSurface fs || fs.Facets.Count < 2) return 0;
        var tris = fs.Facets.ToList();
        // Компоненты треугольников по общим рёбрам (точные совпадения вершин — они из точного тела).
        var parent = Enumerable.Range(0, tris.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var byEdge = new Dictionary<(Vector3d, Vector3d), int>();
        static (Vector3d, Vector3d) Key(Vector3d a, Vector3d b) =>
            (a.X, a.Y, a.Z).CompareTo((b.X, b.Y, b.Z)) <= 0 ? (a, b) : (b, a);
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            foreach (var k in new[] { Key(a, b), Key(b, c), Key(c, a) })
            {
                if (byEdge.TryGetValue(k, out int j)) parent[Find(i)] = Find(j); else byEdge[k] = i;
            }
        }
        var comps = Enumerable.Range(0, tris.Count).Select(Find).Distinct().ToList();
        if (comps.Count < 2) return 0;
        // Вершина → компонента (вершина принадлежит одной компоненте, иначе компоненты соединены — деления нет).
        var compOf = new Dictionary<Vector3d, int>();
        for (int i = 0; i < tris.Count; i++)
            foreach (var p in new[] { tris[i].A, tris[i].B, tris[i].C })
            {
                int c = Find(i);
                if (compOf.TryGetValue(p, out int c0) && c0 != c) return 0;      // касание через вершину — не делим
                compOf[p] = c;
            }
        var verts = compOf.Keys.ToList();
        int CompAt(Vector3d q)
        {
            if (compOf.TryGetValue(q, out int c)) return c;
            double best = double.MaxValue; int bc = -1;
            foreach (var v in verts) { double d = (v - q).LengthSquared; if (d < best) { best = d; bc = compOf[v]; } }
            return best < 1e-10 ? bc : -1;
        }
        var loopComp = new Dictionary<TLoop, int>(ReferenceEqualityComparer.Instance);
        foreach (var l in face.Loops)
        {
            int c = -2;
            foreach (var ce in l.CoEdges)
            {
                int cc = CompAt(ce.Start.P);
                if (cc < 0 || (c != -2 && cc != c)) return 0;                    // контур не в одной компоненте
                c = cc;
            }
            if (c < 0) return 0;
            loopComp[l] = c;
        }
        if (comps.Any(c => !loopComp.Values.Contains(c))) return 0;              // компонента без контура
        // Первая компонента (по первому контуру) остаётся гранью, прочие — новые грани.
        int keep = loopComp[face.Loops[0]];
        // Видимая сетка делится так же, по ближайшей вершине разбиения к центру треугольника.
        int CompNear(Vector3d q)
        {
            double best = double.MaxValue; int bc = keep;
            foreach (var v in verts) { double d = (v - q).LengthSquared; if (d < best) { best = d; bc = compOf[v]; } }
            return bc;
        }
        if (!RegionFacetsOld && face.InteriorHint is { } fh0 && CompNear(fh0) != keep)
        {
            var bigK = Enumerable.Range(0, tris.Count).Where(i => Find(i) == keep).OrderByDescending(i => Vector3d.Cross(tris[i].Item2 - tris[i].Item1, tris[i].Item3 - tris[i].Item1).LengthSquared).FirstOrDefault(-1);
            if (bigK >= 0) face.InteriorHint = (tris[bigK].Item1 + tris[bigK].Item2 + tris[bigK].Item3) / 3;
        }
        int made = 0;
        foreach (int c in comps.Where(c => c != keep))
        {
            var nf = new TFace { Tag = face.Tag, Reversed = face.Reversed, MaterialSlot = face.MaterialSlot };
            // g.11/g.05: исходная метка (многоугольники точной формы) и точка грани — у каждой части свои
            if (!RegionFacetsOld)
            {
                nf.SourceTag = face.SourceTag;
                var big = Enumerable.Range(0, tris.Count).Where(i => Find(i) == c).OrderByDescending(i => Vector3d.Cross(tris[i].Item2 - tris[i].Item1, tris[i].Item3 - tris[i].Item1).LengthSquared).FirstOrDefault(-1);
                if (big >= 0) nf.InteriorHint = (tris[big].Item1 + tris[big].Item2 + tris[big].Item3) / 3;
            }
            var nfs = new Surfaces.FacetedSurface(fs.Source, fs.FallbackNormal);
            for (int i = 0; i < tris.Count; i++) if (Find(i) == c) { nfs.Facets.Add(tris[i]); fs.Facets.Remove(tris[i]); }
            if (fs.Display != null)
            {
                var keepD = new List<(Vector3d, Vector3d, Vector3d)>(); var keepN = new List<(Vector3d, Vector3d, Vector3d)>();
                var moveD = new List<(Vector3d, Vector3d, Vector3d)>(); var moveN = new List<(Vector3d, Vector3d, Vector3d)>();
                for (int i = 0; i < fs.Display.Count; i++)
                {
                    var t = fs.Display[i];
                    bool here = CompNear((t.Item1 + t.Item2 + t.Item3) / 3) == c;
                    (here ? moveD : keepD).Add(t);
                    if (fs.DisplayNormals != null && i < fs.DisplayNormals.Count) (here ? moveN : keepN).Add(fs.DisplayNormals[i]);
                }
                bool hasN = fs.DisplayNormals != null;
                fs.SetDisplay(keepD, hasN ? keepN : null); nfs.SetDisplay(moveD, hasN ? moveN : null);
            }
            nf.Surface = nfs;
            foreach (var l in face.Loops.Where(l => loopComp[l] == c).ToList())
            {
                face.Loops.Remove(l); l.Face = nf; nf.Loops.Add(l);
            }
            shell.Add(nf);
            made++;
        }
        return made;
    }

    /// <summary>
    /// G4. Знак компоненты без плоских колец и без треугольников гранения (точная булева: стенка
    /// поперечного отверстия и кромка на стенке вала — грани на точных поверхностях): малое
    /// замкнутое кольцо на кривой грани, смотрящее вдоль местной нормали, — отверстие в ней;
    /// отверстие обходится по часовой стрелке относительно наружной нормали.
    /// </summary>
    /// <summary>
    /// KRN-03: ЗНАК ПО ПРАВИЛУ «МАТЕРИАЛ СЛЕВА». При обходе кольца направление n × t (n — наружная нормаль грани, t —
    /// касательная обхода) смотрит внутрь области грани. «Внутрь» — к другому кольцу той же грани (полоса цилиндра, конуса),
    /// а у грани с одним кольцом на поверхности вращения или выдавливания — к середине образующей (шапка конуса с вершиной).
    /// Прежнее правило (SignFromLocalNormal) считало кольцо с нормалью Ньюэлла вдоль нормали грани ОТВЕРСТИЕМ — у острия
    /// сверла (конус 118°) это внешний контур шапки: знак выходил обратным, и вся компонента «низ цилиндра + конус»
    /// переворачивалась согласованно — сверлёное глухое отверстие ни разу не шло аналитическим путём.
    /// </summary>
    static readonly bool MaterialSide = Environment.GetEnvironmentVariable("P3D_MATSIDE") != "0";
    static readonly bool MidTarget = Environment.GetEnvironmentVariable("P3D_MATSIDEMID") != "0";
    /// <summary>Кольцо обходит ось поверхности (вращения — ось вращения; выдавливания из замкнутой образующей — линия через её
    /// центр по направлению выдавливания): суммарный угол вокруг оси ≈ ±2π.</summary>
    static bool Wraps(List<Vector3d> ring, Surfaces.ISurface3 src)
    {
        Vector3d o, w;
        if (src is Surfaces.RevolvedSurface rv) { o = rv.Axis.Origin; w = rv.Axis.W.Normalized(); }
        else if (src is Surfaces.ExtrudedSurface ex && BaseClosed(ex.Base))
        {
            w = ex.Dir.Normalized(); o = Vector3d.Zero;
            for (int i = 0; i < 16; i++) o += ex.Base.PointAt(ex.Base.T0 + (ex.Base.T1 - ex.Base.T0) * i / 16) / 16;
        }
        else return false;
        var u = Vector3d.Cross(w, Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized(); var v = Vector3d.Cross(w, u);
        double A(Vector3d p) { var d = p - o; return Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)); }
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            double da = A(ring[(i + 1) % ring.Count]) - A(ring[i]);
            while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
            sum += da;
        }
        return Math.Abs(sum) > Math.PI;
    }
    static bool BaseClosed(Curves.ICurve3 c) => (c.PointAt(c.T0) - c.PointAt(c.T1)).Length < 1e-9 * Math.Max(1, c.PointAt(c.T0).Length);
    [ThreadStatic] public static Action<string>? AnchorDebug;
    static readonly bool HintOff = Environment.GetEnvironmentVariable("P3D_FACEHINT") == "0";
    static readonly bool RegionFacetsOld = Environment.GetEnvironmentVariable("P3D_REGIONFACETS") == "0";
    static readonly bool TorusSideOff = Environment.GetEnvironmentVariable("P3D_TORUSSIDE") == "0";

    static int? TorusVote(TFace f, Geometry.Ssi.Torus t, List<Vector3d> ring, List<List<Vector3d>> faceRings, Vector3d hint)
    {
        var w = t.Axis.Normalized(); var u = Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY; u = (u - w * Vector3d.Dot(u, w)).Normalized(); var v = Vector3d.Cross(w, u);
        Vector2d Ang(Vector3d p) { var q = p - t.C; double z = Vector3d.Dot(q, w); var rv = q - w * z; return new Vector2d(Math.Atan2(Vector3d.Dot(rv, v), Vector3d.Dot(rv, u)), Math.Atan2(z, rv.Length - t.R)); }
        Vector3d OnTorus(Vector2d a) { var radial = u * Math.Cos(a.X) + v * Math.Sin(a.X); return t.C + radial * (t.R + t.r * Math.Cos(a.Y)) + w * (t.r * Math.Sin(a.Y)); }
        double W(double x) => Math.IEEERemainder(x, 2 * Math.PI);
        int votes = 0;
        for (int k = 1; k + 1 < ring.Count; k += Math.Max(1, ring.Count / 5))
        {
            // точка — ВЕРШИНА ломаной (на кривой), касательная — по соседям: середина хорды отстоит от кривой на прогиб
            // (0.02 мм у окружности R 9), он больше сдвига, и проба всегда попадала по одну сторону
            var m = ring[k]; var tg = ring[k + 1] - ring[k - 1];
            if (tg.LengthSquared < 1e-24) continue;
            Vector3d n; try { n = f.NormalAt(m); } catch { continue; }
            if (n.LengthSquared < 1e-24) continue;
            var left = Vector3d.Cross(n.Normalized(), tg.Normalized());
            var q = OnTorus(Ang(m + left * Math.Max(2e-3 * t.r, 1e-5)));
            var a = Ang(q); var h = Ang(hint); var b = new Vector2d(a.X + W(h.X - a.X), a.Y + W(h.Y - a.Y));
            int cr = 0;
            foreach (var poly in faceRings)
            {
                if (poly.Count < 2) continue;
                var prev = Ang(poly[0]);
                for (int i = 1; i < poly.Count; i++)
                {
                    var cur0 = Ang(poly[i]); var cur = new Vector2d(prev.X + W(cur0.X - prev.X), prev.Y + W(cur0.Y - prev.Y));
                    var mid = (a + b) / 2; var sm = (prev + cur) / 2;
                    double kx = Math.Round((mid.X - sm.X) / (2 * Math.PI)), ky = Math.Round((mid.Y - sm.Y) / (2 * Math.PI));
                    for (int sx = -1; sx <= 1; sx++)
                        for (int sy = -1; sy <= 1; sy++)
                        {
                            var sh = new Vector2d((kx + sx) * 2 * Math.PI, (ky + sy) * 2 * Math.PI);
                            var c0 = prev + sh; var c1 = cur + sh; var rr = b - a; var e = c1 - c0; double den = rr.X * e.Y - rr.Y * e.X;
                            if (Math.Abs(den) < 1e-300) continue;
                            var ww = c0 - a; double tt = (ww.X * e.Y - ww.Y * e.X) / den, uu = (ww.X * rr.Y - ww.Y * rr.X) / den;
                            if (tt > 0 && tt <= 1 && uu >= 0 && uu < 1) cr++;
                        }
                    prev = cur;
                }
            }
            votes += (cr & 1) == 0 ? 1 : -1;
        }
        return votes == 0 ? null : Math.Sign(votes);
    }

    private static int? SignFromMaterialSide(List<Ring> members, List<Ring> all)
    {
        int votes = 0;
        foreach (var r in members)
        {
            var f = r.Loop.Face; if (f == null) continue;
            var src = f.Surface is Surfaces.FacetedSurface fs ? fs.Source : f.Surface;
            if (src is not (Surfaces.RevolvedSurface or Surfaces.ExtrudedSurface)) continue;
            var pts = RingPoints(r, r.Sign >= 0 ? 1 : -1);
            if (pts.Count < 3) continue;
            var others = all.Where(o => !ReferenceEquals(o, r) && ReferenceEquals(o.Loop.Face, f)).Select(o => RingPoints(o, 1)).Where(l => l.Count > 0).ToList();
            // g.05: ТОР С ТОЧКОЙ ГРАНИ — сторона кольца по чётности: точка чуть слева от кольца (по нормали грани), путь от неё к
            // точке грани в развёртке (θ, φ) по кратчайшему подъёму; пересечения с кольцами грани (граница грани разделяет
            // поверхность — чётность от пути не зависит). Чётно — слева грань. Прежде компонента «пояс тора + цилиндр» без
            // плоских колец переворачивалась эвристикой (тор минус соосный цилиндр: объём −1352.9). Мутация P3D_TORUSSIDE=0.
            if (f.InteriorHint is { } th && !TorusSideOff && Geometry.ExactEdges.ShapeOf(src) is Geometry.Ssi.Torus tor && TorusVote(f, tor, pts, all.Where(o => ReferenceEquals(o.Loop.Face, f)).Select(o => RingPoints(o, 1)).ToList(), th) is int tv)
            {
                AnchorDebug?.Invoke($"torus ring: face {f.Tag} vote {tv}");
                return tv;
            }
            // g.02: ЦИЛИНДР И ТОЧКА ГРАНИ: кольцо, не обходящее ось, в развёртке (Rθ, z) — многоугольник; слева от него —
            // внутренность (обход против часовой) или внешность (по часовой). Точка грани внутри/вне — по числу оборотов.
            // Прежде — направление к ближайшей точке другого кольца, почти вдоль касательной у невыпуклого кольца пересечения
            // шара со стенкой (сторона −0.43 по одной пробе) — вычитание шара оставляло не ту часть сферы.
            if (f.InteriorHint is { } hc && !HintOff && src is Surfaces.ExtrudedSurface exc && exc.Base is Curves.ArcCurve3 arcc && !Wraps(pts, src))
            {
                var w = exc.Dir.Normalized(); var o = arcc.Frame.Origin; var e1 = (arcc.Frame.U - w * Vector3d.Dot(arcc.Frame.U, w)).Normalized(); var e2 = Vector3d.Cross(w, e1);
                double Th(Vector3d q) { var d = q - o; return Math.Atan2(Vector3d.Dot(d, e2), Vector3d.Dot(d, e1)); }
                double th0 = Th(hc), R = arcc.Radius;
                var poly = new List<Vector2d>(); double acc = 0, prevT = double.NaN;
                foreach (var q in pts)
                {
                    double t = Th(q) - th0;
                    if (double.IsNaN(prevT)) { while (t > Math.PI) t -= 2 * Math.PI; while (t <= -Math.PI) t += 2 * Math.PI; acc = t; }
                    else { double dt = t - prevT; while (dt > Math.PI) dt -= 2 * Math.PI; while (dt <= -Math.PI) dt += 2 * Math.PI; acc += dt; }
                    prevT = Th(q) - th0; poly.Add(new Vector2d(acc * R, Vector3d.Dot(q - o, w)));
                }
                double area2 = 0; for (int k = 0; k < poly.Count; k++) { var a0 = poly[k]; var b0 = poly[(k + 1) % poly.Count]; area2 += a0.X * b0.Y - a0.Y * b0.X; }
                int wn = 0; double zh = Vector3d.Dot(hc - o, w);
                foreach (double sh in new[] { 0.0, 2 * Math.PI * R, -2 * Math.PI * R })
                {
                    double tot = 0; var hp = new Vector2d(sh, zh);
                    for (int k = 0; k < poly.Count; k++) { var a0 = poly[k] - hp; var b0 = poly[(k + 1) % poly.Count] - hp; tot += Math.Atan2(a0.X * b0.Y - a0.Y * b0.X, Vector2d.Dot(a0, b0)); }
                    int wk = (int)Math.Round(tot / (2 * Math.PI)); if (wk != 0) { wn = wk; break; }
                }
                double nsd = Vector3d.Dot(f.NormalAt(pts[0]), (pts[0] - o) - w * Vector3d.Dot(pts[0] - o, w)) >= 0 ? 1 : -1;
                if (Math.Abs(area2) > 1e-12)
                {
                    bool ccw = area2 > 0, inside = wn != 0;
                    int vote = (ccw == inside) ? 1 : -1;
                    AnchorDebug?.Invoke($"cylinder ring: face {f.Tag} ccw {ccw} hint inside {inside} nside {nsd} vote {vote * nsd}");
                    return (int)(vote * nsd);
                }
            }
            // g.02: ГРАНЬ-СФЕРА С ОДНИМ КОЛЬЦОМ И ИЗВЕСТНОЙ ТОЧКОЙ ГРАНИ: стереографическая проекция из точки, противоположной
            // точке грани; кольцо обходит её образ (начало координат) против часовой — точка слева (обход «материал слева»,
            // взгляд снаружи), по часовой — справа. Точно для любого кольца (не только плоского): вычитание шара из цилиндра
            // с кольцом пересечения на боковой стенке оставляло не ту часть сферы (снималось 5 мм³ вместо 274).
            // Колец несколько (пояс шара между двумя окружностями): кольцо, не обходящее образ точки, ограничивает «дыру» —
            // точка слева от него, если оно обходит по часовой (знак площади в проекции). Объединение шара с цилиндром,
            // прошедшим насквозь, давало объём 2703 вместо 14590 (разностное сравнение, серия 4).
            if (f.InteriorHint is { } hs && !HintOff && SphereCentre(src) is { } sc)
            {
                var d = (hs - sc).Normalized();
                var e1 = Vector3d.Cross(d, Math.Abs(d.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized(); var e2 = Vector3d.Cross(d, e1);
                double tot = 0; bool okp = true; Vector2d? prev = null, first = null;
                foreach (var q in pts)
                {
                    var u = (q - sc).Normalized(); double den = 1 + Vector3d.Dot(u, d);
                    if (den < 1e-9) { okp = false; break; }
                    var xy = new Vector2d(Vector3d.Dot(u, e1) / den, Vector3d.Dot(u, e2) / den);
                    if (prev is { } pv) tot += Math.Atan2(pv.X * xy.Y - pv.Y * xy.X, Vector2d.Dot(pv, xy));
                    first ??= xy; prev = xy;
                }
                if (okp && prev is { } pl2 && first is { } fx) tot += Math.Atan2(pl2.X * fx.Y - pl2.Y * fx.X, Vector2d.Dot(pl2, fx));
                int wnd = okp ? (int)Math.Round(tot / (2 * Math.PI)) : 0;
                double nside = Vector3d.Dot(f.NormalAt(pts[0]), pts[0] - sc) >= 0 ? 1 : -1;
                double parea = 0;
                if (okp && wnd == 0)
                {
                    var pp = pts.Select(q => { var u = (q - sc).Normalized(); double den = 1 + Vector3d.Dot(u, d); return new Vector2d(Vector3d.Dot(u, e1) / den, Vector3d.Dot(u, e2) / den); }).ToList();
                    for (int k2 = 0; k2 < pp.Count; k2++) { var a0 = pp[k2]; var b0 = pp[(k2 + 1) % pp.Count]; parea += a0.X * b0.Y - a0.Y * b0.X; }
                }
                AnchorDebug?.Invoke($"sphere ring winding: face {f.Tag} wnd {wnd} area {parea:G3} nside {nside} others {others.Count}");
                if (wnd != 0) return Math.Sign(wnd * nside);
                if (okp && Math.Abs(parea) > 1e-15) return -Math.Sign(parea) * (int)nside;
            }
            for (int k = 0; k + 1 < pts.Count; k += Math.Max(1, pts.Count / 6))
            {
                var p = (pts[k] + pts[k + 1]) / 2; var t = pts[k + 1] - pts[k];
                if (t.LengthSquared < 1e-24) continue;
                Vector3d n;
                try { n = f.NormalAt(p); } catch { continue; }
                if (n.LengthSquared < 1e-24) continue;
                n = n.Normalized();
                Vector3d target;
                if (others.Count > 0)
                {
                    var q = others.SelectMany(l => l).OrderBy(x => (x - p).LengthSquared).First();
                    target = q;
                    // KRN-03: ЦЕЛЬ — НА САМОЙ ПОВЕРХНОСТИ, посередине между кольцами по параметру образующей (поверхность вращения)
                    // или выдавливания. Прямая к ближайшей точке другого кольца у выпуклого пояса (пояс тора между двумя
                    // окружностями) идёт через пустоту почти по нормали: её проекция на касательную плоскость — остаток
                    // случайного знака, сторона выбиралась неверно (объём пояса −3426 мм³ при «BRep: ok»). Непериодическая
                    // образующая — середина однозначна; периодическая — прежнее правило. Только у ПОЯСА: у грани два кольца и оба
                    // обходят ось (у вала с поперечным отверстием кольцо отверстия ближе, и «середина» попадала в пустоту
                    // отверстия — 8 проверок allB). P3D_MATSIDEMID=0 — прежнее.
                    if (MidTarget && others.Count == 1 && Wraps(pts, src) && Wraps(others[0], src))
                    {
                        var up = src.ProjectToUV(p); var uq = src.ProjectToUV(q);
                        if (src is Surfaces.RevolvedSurface rvs && !BaseClosed(rvs.Base)) target = src.PointAt(new Vector2d((up.X + uq.X) / 2, up.Y));
                        else if (src is Surfaces.ExtrudedSurface) target = src.PointAt(new Vector2d(up.X, (up.Y + uq.Y) / 2));
                    }
                }
                else if (f.InteriorHint is { } hint && !HintOff)
                {
                    // g.02: ТОЧКА ВНУТРИ ГРАНИ ИЗВЕСТНА ПОСТРОИТЕЛЮ (общая булева: точка области разбиения). Без неё цель —
                    // середина образующей, а у грани-сферы с одним кольцом она не обязательно в грани: шар без шапки
                    // (объединение пересекающихся шаров) обращался в шапку — объединение давало объём линзы, результат
                    // принимался (найдено разностным сравнением с OpenCascade). Мутация P3D_FACEHINT=0.
                    target = hint;
                }
                else
                {
                    var uv = src.ProjectToUV(p);
                    target = src is Surfaces.RevolvedSurface ? src.PointAt(new Vector2d(0.5, uv.Y)) : src.PointAt(new Vector2d(uv.X, 0.5));
                }
                var dir = target - p; dir -= n * Vector3d.Dot(dir, n);
                if (dir.Length < 1e-9) continue;
                double side = Vector3d.Dot(Vector3d.Cross(n, t.Normalized()), dir.Normalized());
                // g.02: ПЛОСКОЕ КОЛЬЦО И ТОЧКА ГРАНИ: плоскость кольца делит поверхность (шар) на две области; сторона «слева»
                // от кольца — та, куда смещает L = n × t, точка грани — по знаку расстояния до плоскости. Направление «к
                // точке» по касательной неверно, когда кратчайший путь к ней идёт через другую область (точка грани шара
                // без шапки рядом с краем шапки: путь пересекал шапку — сторона выбиралась наоборот).
                if (others.Count == 0 && f.InteriorHint is { } h2 && !HintOff && PlaneOf(pts) is { } pl)
                {
                    double sl = Vector3d.Dot(Vector3d.Cross(n, t.Normalized()), pl.N), sh = Vector3d.Dot(h2 - pl.C, pl.N);
                    if (Math.Abs(sl) > 1e-6 && Math.Abs(sh) > 1e-9 * Math.Max(1, h2.Length)) side = Math.Sign(sl) * Math.Sign(sh);
                }
                if (k == 0) AnchorDebug?.Invoke($"   p {p:0.###} n {n:0.###} t {t.Normalized():0.###} target {target:0.###} dir {dir.Normalized():0.###} side {side:0.###} surf {f.Surface.GetType().Name} rev {f.Reversed}");
                if (Math.Abs(side) < 1e-6) continue;
                votes += side > 0 ? 1 : -1;
            }
            AnchorDebug?.Invoke($"material side: face {f.Tag} ring sign {r.Sign} votes {votes} others {others.Count}");
            if (votes != 0) return Math.Sign(votes);
        }
        return null;
    }

    /// <summary>Центр сферы, если поверхность — сфера (вращение дуги с центром на оси, полный оборот).</summary>
    static Vector3d? SphereCentre(Surfaces.ISurface3 s)
    {
        if (s is not Surfaces.RevolvedSurface rv || rv.Base is not Curves.ArcCurve3 a || Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) > 1e-9) return null;
        var w = rv.Axis.W.Normalized(); var dd = a.Frame.Origin - rv.Axis.Origin;
        return (dd - w * Vector3d.Dot(dd, w)).Length < 1e-9 * Math.Max(1, a.Radius) ? a.Frame.Origin : null;
    }

    /// <summary>Плоскость точек кольца (Ньюэлл), если они лежат в ней с точностью 1e-7 размера; иначе null.</summary>
    static (Vector3d C, Vector3d N)? PlaneOf(List<Vector3d> pts)
    {
        var c = pts.Aggregate(Vector3d.Zero, (a, q) => a + q) / pts.Count;
        var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i] - c, pts[(i + 1) % pts.Count] - c);
        if (nw.Length < 1e-18) return null;
        var n = nw.Normalized(); double size = pts.Max(q => (q - c).Length);
        return pts.All(q => Math.Abs(Vector3d.Dot(q - c, n)) <= 1e-7 * Math.Max(1, size)) ? (c, n) : null;
    }

    private static int? SignFromLocalNormal(List<Ring> members)
    {
        foreach (var r in members)
        {
            var f = r.Loop.Face; if (f == null) continue;
            var pts = RingPoints(r, +1); if (pts.Count < 3) continue;
            var c = pts.Aggregate(Vector3d.Zero, (a, q) => a + q) / pts.Count;
            var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
            if (nw.Length < 1e-12) continue;
            Vector3d n;
            try { n = f.NormalAt(pts[0]).Normalized(); } catch { continue; }
            double along = Vector3d.Dot(nw.Normalized(), n);
            if (Math.Abs(along) < 0.5) continue;            // кольцо обходит период (не отверстие) — не годится
            // Отверстие: r.Sign · (nw · n) < 0.
            return r.Sign * Vector3d.Dot(nw, n) < 0 ? 1 : -1;
        }
        return null;
    }

    /// <summary>Плоская поверхность, в том числе крышка тела вращения (отрезок поперёк оси).</summary>
    private static bool IsFlat(Surfaces.ISurface3 s)
    {
        if (s.IsPlanar) return true;
        var src = s is Surfaces.FacetedSurface fs ? fs.Source : s;
        if (src == null) return false;
        if (src.IsPlanar) return true;
        return src is Surfaces.RevolvedSurface rv && rv.Base is Curves.LineCurve3 ln &&
               Math.Abs(Vector3d.Dot((ln.B - ln.A).Normalized(), rv.Axis.W.Normalized())) < 1e-12;
    }

    private static Vector3d NewellOf(Ring r)
    {
        _newell ??= new();
        if (!_newell.TryGetValue(r, out var v)) _newell[r] = v = Newell(r);
        return v;
    }

    private static Vector3d OutwardNormal(Ring r)
    {
        var (ce, dir) = r.Items[0];
        var p = dir > 0 ? ce.Edge.V0.P : ce.Edge.V1.P;
        return r.Loop.Face!.NormalAt(p);
    }

    /// <summary>
    /// +1 — кольцо ограничивает область своей грани, −1 — отверстие. По ВЛОЖЕННОСТИ: кольцо,
    /// лежащее внутри нечётного числа других колец той же грани, — отверстие. Прежде роль давал
    /// размер («наибольшее — наружное»), и у грани из двух равных областей (сечение тора
    /// плоскостью) вторая область считалась отверстием — нормализация не сходилась (K08).
    /// </summary>
    private static int RoleOf(Ring r)
    {
        _outerOf ??= new();
        if (!CacheOff && _roleCache != null && _roleCache.TryGetValue(r, out int cachedRole)) return cachedRole;
        var f = r.Loop.Face!;
        var same = (!CacheOff && _byFace != null && _byFace.TryGetValue(f, out var bf) ? bf : _all!.Where(x => ReferenceEquals(x.Loop.Face, f))).Where(x => !ReferenceEquals(x, r)).ToList();
        if (same.Count == 0) return 1;
        var n = OutwardNormal(r).Normalized();
        var p = RingPoints(r, +1)[0];
        int depth = same.Count(o => Inside(RingPoints(o, +1), p, n));
        int role = depth % 2 == 0 ? 1 : -1;
        if (!CacheOff && _roleCache != null) _roleCache[r] = role;
        return role;
    }

    private static List<Vector3d> RingPoints(Ring r, int sign)
    {
        if (!CacheOff && _ptsCache != null)
        {
            if (!_ptsCache.TryGetValue(r, out var cached)) _ptsCache[r] = cached = RingPointsRaw(r);
            if (sign >= 0) return cached;                                   // только чтение
            var rev = new List<Vector3d>(cached); rev.Reverse(); return rev;
        }
        var raw = RingPointsRaw(r); if (sign < 0) raw.Reverse(); return raw;
    }

    private static List<Vector3d> RingPointsRaw(Ring r)
    {
        Diagnostics.Prof.Count("соглашение: разметок колец", 1);
        const int sign = 1;
        var pts = new List<Vector3d>();
        foreach (var (ce, dir) in r.Items)
        {
            var seg = new List<Vector3d>();
            ce.Edge.Curve.Tessellate(seg, true);
            if (seg.Count == 0) continue;
            if (!ReferenceEquals(ce.Edge.V0, ce.Edge.V1) && (seg[0] - ce.Edge.V0.P).LengthSquared > (seg[^1] - ce.Edge.V0.P).LengthSquared) seg.Reverse();   // замкнутое ребро — по параметру кривой
            if (dir < 0) seg.Reverse();
            if (pts.Count > 0) seg.RemoveAt(0);
            pts.AddRange(seg);
        }
        if (sign < 0) pts.Reverse();
        return pts;
    }

    /// <summary>Точка внутри многоугольника на плоскости с нормалью n (чётность пересечений).</summary>
    private static bool Inside(List<Vector3d> poly, Vector3d p, Vector3d n)
    {
        // быстрый отказ по габариту многоугольника (с запасом)
        if (!CacheOff && _boxCache != null && poly.Count > 0)
        {
            if (!_boxCache.TryGetValue(poly, out var bx))
            {
                var lo = poly[0]; var hi = poly[0];
                foreach (var q in poly) { lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q); }
                double pad = 1e-9 * Math.Max(1, (hi - lo).Length);
                _boxCache[poly] = bx = (lo - new Vector3d(pad), hi + new Vector3d(pad));
            }
            // точка в плоскости многоугольника: вне габарита по любой оси (кроме нормали) — снаружи
            var np = Vector3d.Abs(n.Normalized());
            if ((np.X < 0.99 && (p.X < bx.Lo.X || p.X > bx.Hi.X)) || (np.Y < 0.99 && (p.Y < bx.Lo.Y || p.Y > bx.Hi.Y)) || (np.Z < 0.99 && (p.Z < bx.Lo.Z || p.Z > bx.Hi.Z))) return false;
        }
        var u = Math.Abs(n.X) < 0.9 ? Vector3d.Cross(n, Vector3d.UnitX).Normalized() : Vector3d.Cross(n, Vector3d.UnitY).Normalized();
        var v = Vector3d.Cross(n, u);
        double px = Vector3d.Dot(p, u), py = Vector3d.Dot(p, v);
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double xi = Vector3d.Dot(poly[i], u), yi = Vector3d.Dot(poly[i], v), xj = Vector3d.Dot(poly[j], u), yj = Vector3d.Dot(poly[j], v);
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// +1 — кольца компоненты уже обходят наружную нормаль против часовой стрелки (по текущим
    /// знакам), −1 — наоборот, null — определить нельзя (нет фасетной грани или сторона не найдена).
    /// </summary>
    private static int? SignFromFacets(List<Ring> members)
    {
        foreach (var r in members)
        {
            if (r.Loop.Face?.Surface is not Surfaces.FacetedSurface fs || fs.Facets.Count == 0) continue;
            // Стороны разбиения на краю грани: встречаются один раз.
            var count = new Dictionary<(Vector3d, Vector3d), int>();
            foreach (var (a, b, c) in fs.Facets)
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    var k = string.CompareOrdinal(x.ToString(), y.ToString()) < 0 ? (x, y) : (y, x);
                    count[k] = count.GetValueOrDefault(k) + 1;
                }
            var border = new List<(Vector3d A, Vector3d B)>();
            foreach (var (a, b, c) in fs.Facets)
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    var k = string.CompareOrdinal(x.ToString(), y.ToString()) < 0 ? (x, y) : (y, x);
                    if (count[k] == 1) border.Add((x, y));
                }
            if (border.Count == 0) continue;
            var pts = RingPoints(r, r.Sign >= 0 ? 1 : -1);
            if (pts.Count < 2) continue;
            int votes = 0;
            for (int i = 0; i + 1 < pts.Count && Math.Abs(votes) < 5; i += Math.Max(1, pts.Count / 8))
            {
                var p0 = pts[i]; var p1 = pts[i + 1]; var m = (p0 + p1) / 2;
                var nearest = border.OrderBy(e => ((e.A + e.B) / 2 - m).LengthSquared).First();
                if (((nearest.A + nearest.B) / 2 - m).Length > 0.5 * (p1 - p0).Length + 1e-3) continue;
                votes += Vector3d.Dot(nearest.B - nearest.A, p1 - p0) > 0 ? 1 : -1;
            }
            if (votes != 0) return Math.Sign(votes);
        }
        return null;
    }

    /// <summary>Вектор Ньюэлла кольца для обхода в направлении элементов кольца.</summary>
    private static Vector3d Newell(Ring r)
    {
        var pts = new List<Vector3d>();
        foreach (var (ce, dir) in r.Items)
        {
            var seg = new List<Vector3d>();
            ce.Edge.Curve.Tessellate(seg, true);
            if (seg.Count == 0) continue;
            // Кривая может идти от V1 к V0 — сверяемся с вершинами ребра.
            if (!ReferenceEquals(ce.Edge.V0, ce.Edge.V1) && (seg[0] - ce.Edge.V0.P).LengthSquared > (seg[^1] - ce.Edge.V0.P).LengthSquared) seg.Reverse();   // замкнутое ребро — по параметру кривой
            if (dir < 0) seg.Reverse();
            if (pts.Count > 0) seg.RemoveAt(0);
            pts.AddRange(seg);
        }
        var n = Vector3d.Zero;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            n += new Vector3d((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return n / 2;
    }
}
