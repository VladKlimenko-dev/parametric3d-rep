using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// KRN-06 (06.9): ПРЯМОЙ ПОСТРОИТЕЛЬ СКРУГЛЕНИЯ — семейство «плоскость/плоскость». Не резец и не булева: B-rep правится
/// напрямую. Для прямого ребра между двумя плоскими гранями с нормалями n1, n2 центр катящегося шара радиуса r — прямая на
/// расстоянии r от обеих плоскостей (spine); контактные прямые — проекции spine на плоскости; сопряжение — точный цилиндр.
/// В каждом конце ребра (вершина степени 3, третья грань плоская, соседние рёбра — прямые) вершина заменяется дугой сечения
/// цилиндра плоскостью торцевой грани (окружность или эллипс), соседние рёбра укорачиваются до контактных точек.
/// Тело собирается заново из описаний граней (вершины — по положению, рёбра — по концам и середине).
///
/// Поддержано: набор прямых рёбер без общих вершин, выпуклых и вогнутых, плоские грани вокруг. Не поддержано (отказ — и
/// прежний построитель): общие вершины выбранных рёбер (цепочки, углы — 06.10, 06.13), кривые соседние рёбра и грани,
/// контактная прямая вне грани (радиус больше соседних рёбер), другие контуры грани в полосе сопряжения.
/// Мутация: P3D_DIRECTROUND=0 — только прежний построитель.
/// </summary>
public static partial class DirectRound
{
    static readonly bool Off = Environment.GetEnvironmentVariable("P3D_DIRECTROUND") == "0";
    [ThreadStatic] public static Action<string>? Debug;
    [ThreadStatic] public static Action<string>? Prof;
    /// <summary>Мутация P3D_DRSRCSIDE=0: сторона источника — по Reversed фасетной грани.</summary>
    /// <summary>Мутация P3D_DRCHAINS=0: цепочки (общая вершина двух выбранных рёбер) — отказ, прежний построитель.</summary>
    static readonly bool Chains = Environment.GetEnvironmentVariable("P3D_DRCHAINS") != "0";
    /// <summary>Мутация P3D_DRASYM=0: несимметричный стык цепочки — отказ (прежний построитель).</summary>
    static readonly bool AsymOff = Environment.GetEnvironmentVariable("P3D_DRASYM") == "0";
    /// <summary>Несимметричных стыков цепочек построено (разбор, KRN06-DIRECT-ROUND-ASYM-CHAIN).</summary>
    public static long AsymJunctions;
    /// <summary>Мутация P3D_DRPRINCIPAL=0: сопряжённые полудиаметры вместо главных осей эллипса сечения.</summary>
    static readonly bool PrincipalAxes = Environment.GetEnvironmentVariable("P3D_DRPRINCIPAL") != "0";
    /// <summary>Мутация P3D_DRCORNERS=0: угловой узел из трёх рёбер — отказ, прежний построитель.</summary>
    static readonly bool Corners = Environment.GetEnvironmentVariable("P3D_DRCORNERS") != "0";
    /// <summary>06.13: угол из n &gt; 3 скруглений (все рёбра вершины степени n, плоские грани, общий шар) — сферический
    /// n-угольник (мутация P3D_DRNCORNER=0 — отказ, как прежде).</summary>
    static readonly bool NCorner = Environment.GetEnvironmentVariable("P3D_DRNCORNER") != "0";
    public static long NCorners;
    static readonly bool TrueNormal = Environment.GetEnvironmentVariable("P3D_DRTRUENORMAL") != "0";
    static readonly bool ChamferOff = Environment.GetEnvironmentVariable("P3D_DIRECTCHAMFER") == "0";
    static readonly bool Prov = Environment.GetEnvironmentVariable("P3D_DRPROVENANCE") != "0";
    static readonly bool Lazy = Environment.GetEnvironmentVariable("P3D_DRLAZY") != "0";
    static readonly bool LineCyl = Environment.GetEnvironmentVariable("P3D_DRLINECYL") != "0";
    /// <summary>06.15: фаска прямого ребра плоскость/цилиндр вдоль оси прямым построителем (мутация P3D_DRLINECYLCH=0 — прежний путь резцов).</summary>
    static readonly bool LineCylCh = Environment.GetEnvironmentVariable("P3D_DRLINECYLCH") != "0";
    public static long LineCylChamfers;
    static readonly bool ConvGeom = Environment.GetEnvironmentVariable("P3D_DRCONVGEOM") != "0";
    static readonly bool Scan = Environment.GetEnvironmentVariable("P3D_DRSCAN") != "0";
    static readonly bool Circles = Environment.GetEnvironmentVariable("P3D_DRCIRCLES") != "0";
    static readonly bool SourceSide = Environment.GetEnvironmentVariable("P3D_DRSRCSIDE") != "0";
    public static long Built, Declined;

    sealed class Seg
    {
        public ICurve3 Curve = null!; public Vector3d A, B;      // начало и конец по обходу контура
        public TEdge? Orig;                                      // исходное ребро (если не изменено)
        public TEdge? Id;                                        // исходное ребро звена (сохраняется при укорачивании)
        public ICurve3? TrimOf;                                  // кривая — часть кривой ребра Id (укорочение, не новая)
        public bool Closed => (A - B).LengthSquared < 1e-24 && Curve is ArcCurve3 { IsClosed: true } or ProcCurve3 { IsClosed: true };
        public Seg Clone() => new() { Curve = Curve, A = A, B = B, Orig = Orig, Id = Id, TrimOf = TrimOf };
    }
    sealed class FDesc
    {
        public ISurface3 Surf = null!; public bool Rev; public string Tag = ""; public Vector3d? Hint; public int Slot;
        public List<List<Seg>> Loops = new(); public TFace? Orig; public bool Touched;
        public Vector3d N;                                       // наружная нормаль плоской грани (по поверхности и Reversed)
        public TFace? Made;                                      // грань собранного тела
        public bool IsCyl; public Vector3d AxO, AxW; public double AxR;   // прямой круговой цилиндр: ось и радиус
    }

    /// <summary>Скруглить рёбра прямым построением; false — семейство не поддержано (тела не тронуты).</summary>
    /// <summary>Проверки обходного пути (страховка резца): прямой построитель выключен в этом потоке.</summary>
    [ThreadStatic] public static bool OffForTest;

    public static bool TryRound(List<TSolid> bodies, IReadOnlyList<EdgeRef> refs, double r, List<string> warnings)
    {
        if (Off || OffForTest || refs.Count == 0 || !(r > 0) || OwnerBody(bodies, refs) is not int k) return false;
        string? why = null;
        _mixedSigma = null;
        var res = Build(bodies[k], refs, r, false, ref why);
        bool staged = false;
        if (res == null && _mixedSigma is { } ms && MixedOn) { _mixedSigma = null; res = BuildMixed(bodies[k], refs, r, ms, ref why); staged = res != null; }
        _mixedSigma = null;
        int added = 0;
        if (res == null && TanProp && TangentClosure(bodies[k], refs) is { } more)
        {
            // РАСПРОСТРАНЕНИЕ ПО КАСАТЕЛЬНЫМ (как у OpenCascade и привычных CAD): выбор, который кончается у касательного
            // продолжения (дуга после отрезка, отрезок после дуги), дополняется звеньями до острых стыков
            string? w2 = null;
            var res2 = Build(bodies[k], more, r, false, ref w2);
            _mixedSigma = null;
            if (res2 != null) { res = res2; added = more.Count - refs.Count; Interlocked.Increment(ref TanPropagations); }
            else why += " | with tangent propagation: " + w2;
        }
        if (res == null && OpenOn && !(_law != null) && _chamfer == null)
        {
            // KRN-06 (06.12, 06.14): незамкнутые рёбра между кривыми гранями — катящийся шар с обрезкой торцами
            string? w3 = null;
            var r3 = BuildOpen(bodies[k], refs, r, ref w3);
            if (r3 != null) { res = r3; Interlocked.Increment(ref OpenBuilt); }
            else { why += " | open curved: " + w3; Interlocked.Increment(ref OpenDeclined); }
        }
        if (res == null) { Interlocked.Increment(ref Declined); Debug?.Invoke("declined: " + why); return false; }
        bodies[k] = res; Interlocked.Increment(ref Built);
        if (added > 0) warnings.Add($"Round: tangent propagation added {added} edge(s) to the selection.");
        warnings.Add(staged ? $"Round: direct blend ({refs.Count} edge(s), exact blend surfaces; concave edges first at mixed corners)."
                            : $"Round: direct blend ({refs.Count} edge(s), exact blend surfaces).");
        return true;
    }

    /// <summary>
    /// KRN-06 (06.15): ФАСКА расстояние/расстояние на той же инфраструктуре — только равные расстояния (несимметричная
    /// фаска: сторона по ходу цепочки — прежний построитель). Контактные прямые: P + d·u_i, u_i — в грани i поперёк
    /// ребра; лента — плоскость; торцы, стыки цепочек — отрезки; угол из трёх рёбер — плоский треугольник.
    /// Мутация P3D_DIRECTCHAMFER=0.
    /// </summary>
    public static bool TryChamfer(List<TSolid> bodies, IReadOnlyList<EdgeRef> refs, double d1, double d2, List<string> warnings)
    {
        bool unequal = Math.Abs(d1 - d2) > 1e-9 * Math.Max(1, d1);
        if (Off || OffForTest || ChamferOff || refs.Count == 0 || !(d1 > 0) || !(d2 > 0) || (unequal && UnequalOff) || OwnerBody(bodies, refs) is not int k) return false;
        string? why = null;
        if (unequal)
        {
            // KRN-06 (06.15): несимметричная фаска — грань первого размера по правилу резцов (ход цепочки, «правая рука»)
            var one = new List<TSolid> { bodies[k] }; var whole = FilletOp.WholeNamedEdges(one, refs);
            var es = refs.Select(rf => FilletOp.Resolve(one, rf, out _) ?? (whole.TryGetValue(rf, out var we) ? we : null)).ToList();
            if (es.Any(x => x == null)) return false;
            var first = FilletOp.FirstSizeFaces(es!.Distinct().ToList()!);
            _chamfer = (d1, d2, first);
        }
        TSolid? res;
        try
        {
            res = Build(bodies[k], refs, d1, true, ref why);
            // KRN-06 (06.15): рёбра между кривыми гранями (замкнутые и незамкнутые) — фаска на инфраструктуре катящегося шара:
            // точки граней на хордовом расстоянии в плоскости поперёк ребра, грань — линейчатая по формуле сечения. P3D_DRCURVCH=0 — отказ.
            if (res == null && CurvedChamfer)
            {
                var cm = _chamfer;
                Func<TEdge, TFace, double> dist = (x, f) => cm is { } c && c.First.TryGetValue(x, out var ff) ? (ReferenceEquals(ff, f) ? c.D1 : c.D2) : d1;
                string? w2 = null;
                res = CurvedChamferBuild(bodies[k], refs, d1, dist, ref w2);
                if (res != null) Interlocked.Increment(ref CurvedChamfers); else why += " | curved chamfer: " + w2;
            }
        }
        finally { _chamfer = null; }
        if (res == null) { Interlocked.Increment(ref Declined); Debug?.Invoke("declined: " + why); return false; }
        bodies[k] = res; Interlocked.Increment(ref Built);
        warnings.Add(unequal ? $"Chamfer: direct cut {d1:0.###}x{d2:0.###} (plane/plane, {refs.Count} edge(s), exact planes)." : $"Chamfer: direct cut (plane/plane, {refs.Count} edge(s), exact planes).");
        return true;
    }

    /// <summary>06.2/06.13: смешанный угол (в вершине сходятся выпуклые и вогнутые выбранные рёбра) — в два этапа: сначала вогнутые,
    /// затем выпуклые вместе с новыми звеньями (дуга вогнутой ленты на общей грани выпуклых) — открытая касательная цепочка
    /// отрезок–дуга–отрезок (цилиндр–тор–цилиндр). Так же строит OpenCascade (BRepFilletAPI_MakeFillet: 3 цилиндра + тор,
    /// объём совпадает до 1e-15). Мутация P3D_DRMIXED=0 — отказ, как прежде.</summary>
    static readonly bool MixedOn = Environment.GetEnvironmentVariable("P3D_DRMIXED") != "0";
    internal static readonly bool TanGroups = Environment.GetEnvironmentVariable("P3D_DRTANGROUPS") != "0";
    [ThreadStatic] static Dictionary<TEdge, int?>? _mixedSigma;
    public static long MixedCorners;

    static TSolid? BuildMixed(TSolid body, IReadOnlyList<EdgeRef> refs, double r, Dictionary<TEdge, int?> sigma, ref string? why)
    {
        var one = new List<TSolid> { body };
        var res = refs.Select(rf => (rf, e: FilletOp.Resolve(one, rf, out _))).ToList();
        if (res.Any(x => x.e == null || !sigma.TryGetValue(x.e, out var sg) || sg == null)) { why = "mixed corner: an edge without convexity"; return null; }
        var conc = res.Where(x => sigma[x.e!] == 1).ToList(); var conv = res.Where(x => sigma[x.e!] == -1).ToList();
        if (conc.Count == 0 || conv.Count == 0) { why = "mixed corner: not mixed"; return null; }
        // смешанные вершины и общая грань выпуклых рёбер в каждой
        static bool Touches(TEdge e, Vector3d p) => (e.V0.P - p).Length < 1e-7 || (e.V1.P - p).Length < 1e-7;
        static List<TFace> FacesOf(TEdge e) => e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        var mixedV = new List<(Vector3d V, string FpTag)>();
        foreach (var p in conc.SelectMany(x => new[] { x.e!.V0.P, x.e!.V1.P }))
        {
            var cv = conv.Where(x => Touches(x.e!, p)).Select(x => x.e!).ToList();
            if (cv.Count == 0 || mixedV.Any(m => (m.V - p).Length < 1e-7)) continue;
            var common = cv.Select(FacesOf).Aggregate((a, b) => a.Intersect(b).ToList());
            if (common.Count != 1) { why = "mixed corner: the convex edges do not share one face"; return null; }
            mixedV.Add((p, common[0].Tag));
        }
        if (mixedV.Count == 0) { why = "mixed corner: no mixed vertex"; return null; }
        string? w1 = null;
        var b1 = Build(body, conc.Select(x => x.rf).ToList(), r, false, ref w1);
        _mixedSigma = null;
        if (b1 == null) { why = "mixed corner: concave stage: " + w1; return null; }
        var refs2 = conv.Select(x => x.rf).ToList();
        foreach (var e in b1.Edges)
        {
            var fs = FacesOf(e); if (fs.Count != 2) continue;
            foreach (var (V, tag) in mixedV)
                if (fs.Any(f => f.Tag == tag) && fs.Any(f => f.Tag == "round:face") && Math.Min((e.V0.P - V).Length, (e.V1.P - V).Length) < 2 * r + 1e-7
                    && !refs2.Any(x => ReferenceEquals(FilletOp.Resolve(new List<TSolid> { b1 }, x, out _), e)))
                    refs2.Add(EdgeRef.Of(e));
        }
        string? w2 = null;
        var b2 = Build(b1, refs2, r, false, ref w2);
        _mixedSigma = null;
        if (b2 == null) { why = "mixed corner: convex stage: " + w2; return null; }
        Interlocked.Increment(ref MixedCorners);
        return b2;
    }

    /// <summary>06.9/06.11: распространение выбора по касательным звеньям, когда без него прямой построитель отказывает
    /// (мутация P3D_DRTANPROP=0 — без распространения, отказ, как прежде).</summary>
    static readonly bool TanProp = Environment.GetEnvironmentVariable("P3D_DRTANPROP") != "0";
    public static long TanPropagations;

    /// <summary>Замыкание выбора по касательным продолжениям: звено e' в конце V выбранного e добавляется, если у них ровно
    /// одна общая грань, касательные в V продолжают друг друга, а вторые грани касаются в V. null — добавлять нечего.</summary>
    static List<EdgeRef>? TangentClosure(TSolid body, IReadOnlyList<EdgeRef> refs)
    {
        var one = new List<TSolid> { body };
        var sel = new List<TEdge>();
        foreach (var rf in refs) { var e = FilletOp.Resolve(one, rf, out _); if (e == null) return null; if (!sel.Contains(e)) sel.Add(e); }
        static List<TFace> FacesOf(TEdge e) => e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        static Vector3d Away(TEdge e, Vector3d V)
        {
            var c = e.Curve; bool atStart = (c.PointAt(c.T0) - V).Length <= (c.PointAt(c.T1) - V).Length;
            var tg = c.TangentAt(atStart ? c.T0 : c.T1).Normalized();
            return atStart ? tg : -tg;
        }
        static Vector3d Nrm(TFace f, Vector3d V) { var su = GeometryAudit.AuthoritativeSurface(f); return su.NormalAt(su.ProjectToUV(V)).Normalized(); }
        var all = body.Edges.ToList(); var added = new List<TEdge>();
        for (int i = 0; i < sel.Count; i++)
        {
            var e = sel[i]; var fe = FacesOf(e); if (fe.Count != 2) continue;
            foreach (var V in new[] { e.V0.P, e.V1.P })
                foreach (var x in all)
                {
                    if (sel.Contains(x) || !((x.V0.P - V).Length < 1e-7 || (x.V1.P - V).Length < 1e-7)) continue;
                    var fx = FacesOf(x); if (fx.Count != 2) continue;
                    var common = fe.Intersect(fx).ToList(); if (common.Count != 1) continue;
                    if (Vector3d.Dot(Away(e, V), Away(x, V)) > -1 + 1e-9) continue;          // не продолжение
                    var ge = fe.First(f => f != common[0]); var gx = fx.First(f => f != common[0]);
                    if (Math.Abs(Vector3d.Dot(Nrm(ge, V), Nrm(gx, V))) < 1 - 1e-9) continue;  // вторые грани не касаются
                    sel.Add(x); added.Add(x);
                }
        }
        return added.Count == 0 ? null : refs.Concat(added.Select(EdgeRef.Of)).ToList();
    }

    /// <summary>Мутация P3D_DRMULTIBODY=0: прямой построитель только у детали из одного тела (прежде).</summary>
    static readonly bool MultiBodyOff = Environment.GetEnvironmentVariable("P3D_DRMULTIBODY") == "0";
    /// <summary>
    /// KRN-07 (07.7): ТЕЛО ВЫБРАННЫХ РЁБЕР. Прежде прямой построитель брался только за деталь из одного тела: кромка отверстия
    /// в экземпляре массива (детали из нескольких тел) шла обходным путём резцов — гранёные грани и откат «результат касается
    /// себя в точке» по чужому телу. Теперь — тело, на котором находятся ВСЕ ссылки, если оно одно (ни одна ссылка не
    /// находится на другом теле); иначе — прежний путь.
    /// </summary>
    static int? OwnerBody(List<TSolid> bodies, IReadOnlyList<EdgeRef> refs)
    {
        if (bodies.Count == 1) return 0;
        if (MultiBodyOff || bodies.Count == 0) return null;
        int? owner = null;
        for (int i = 0; i < bodies.Count; i++)
        {
            var one = new List<TSolid> { bodies[i] };
            var whole = FilletOp.WholeNamedEdges(one, refs);
            int hits = refs.Count(rf => (FilletOp.Resolve(one, rf, out _) ?? (whole.TryGetValue(rf, out var w) ? w : null)) != null);
            if (hits == 0) continue;
            if (hits != refs.Count || owner != null) return null;
            owner = i;
        }
        return owner;
    }

    [ThreadStatic] static Func<Vector3d, double>? _law;
    [ThreadStatic] static (double D1, double D2, Dictionary<TEdge, TFace> First)? _chamfer;
    /// <summary>Мутация P3D_DRUNEQUAL=0: несимметричная фаска — только путь резцов (прежде).</summary>
    static readonly bool UnequalOff = Environment.GetEnvironmentVariable("P3D_DRUNEQUAL") == "0";
    /// <summary>Мутация P3D_DRUNEQUALCHAIN=0: несимметричная фаска — только одиночные рёбра (цепочки и углы — путь резцов).</summary>
    static readonly bool UnequalChainsOff = Environment.GetEnvironmentVariable("P3D_DRUNEQUALCHAIN") == "0";
    /// <summary>Мутация P3D_DRUNEQUALCIRCLE=0: несимметричная фаска окружности плоскость/цилиндр — путь резцов.</summary>
    static readonly bool UnequalCircleOff = Environment.GetEnvironmentVariable("P3D_DRUNEQUALCIRCLE") == "0";
    /// <summary>Мутация P3D_DRVARIABLE=0: переменный радиус — только обходной путь резцов (прежде).</summary>
    static readonly bool VariableOff = Environment.GetEnvironmentVariable("P3D_DRVARIABLE") == "0";
    static readonly bool VarCurved = Environment.GetEnvironmentVariable("P3D_DRVARCURVED") != "0";

    /// <summary>
    /// KRN-06 (06.14): ПРЯМОЕ СКРУГЛЕНИЕ С ЛИНЕЙНО МЕНЯЮЩИМСЯ РАДИУСОМ — одно прямое ребро между плоскостями, концы на плоских
    /// торцах. Огибающая шаров — круговой конус (точная поверхность), контакты — прямые, торцы — дуги эллипса (сечение конуса
    /// плоскостью торца, точно). Закон — по ссылке: R(Start) = r0, R(End) = r1, линейно по проекции на ребро.
    /// </summary>
    public static bool TryRoundVariable(List<TSolid> bodies, IReadOnlyList<EdgeRef> refs, double r0, double r1, List<string> warnings)
    {
        if (Off || OffForTest || VariableOff || refs.Count != 1 || !(r0 > 0) || !(r1 > 0) || Math.Abs(r1 - r0) < 1e-12 || OwnerBody(bodies, refs) is not int k) return false;
        var S = refs[0].Start; var E = refs[0].End; double l2 = (E - S).LengthSquared; if (l2 < 1e-18) return false;
        _law = x => r0 + (r1 - r0) * Vector3d.Dot(x - S, E - S) / l2;
        try
        {
            string? why = null;
            var res = Build(bodies[k], refs, Math.Max(r0, r1), false, ref why);
            bool curved = false;
            if (res == null && OpenOn && VarCurved)
            {
                // KRN-06 (06.14): ребро между кривыми гранями — катящийся шар переменного радиуса (закон по длине ребра),
                // сечения — характеристические окружности огибающей. Мутация P3D_DRVARCURVED=0.
                string? w2 = null; var law = _law; _law = null;
                res = BuildOpen(bodies[k], refs, Math.Max(r0, r1), ref w2, null, (S, r0, E, r1));
                _law = law;
                if (res != null) { curved = true; Interlocked.Increment(ref OpenBuilt); } else why += " | curved variable: " + w2;
            }
            if (res == null) { Interlocked.Increment(ref Declined); Debug?.Invoke("declined: " + why); return false; }
            bodies[k] = res; Interlocked.Increment(ref Built);
            warnings.Add(curved ? $"Round: direct variable blend R{r0:0.###}→R{r1:0.###} (rolling ball, exact envelope)." : $"Round: direct variable blend R{r0:0.###}→R{r1:0.###} (exact cone).");
            return true;
        }
        finally { _law = null; }
    }

    /// <summary>
    /// Дуга сечения кругового конуса (вершина A, ось w — в сторону роста радиуса, полуугол α) плоскостью (V, n) от q1 до q2 —
    /// эллипс, точно: образующие G(φ) = w cos α + (U cos φ + V sin φ) sin α, U — проекция n поперёк оси; точка X(φ) = A + G·d/(n·G),
    /// d = n·(V − A). Большая ось — в плоскости симметрии (φ = 0, π), малая — вдоль V-рамки; её полуось — из cos φ, при котором
    /// проекция на большую ось — центр (решение в замкнутом виде). Дуга — короткая (сопряжение меньше полуоборота).
    /// </summary>
    static ICurve3? ConeSection(Vector3d A, Vector3d w, double alpha, Vector3d V0, Vector3d n, Vector3d q1, Vector3d q2)
    {
        n = n.Normalized(); w = w.Normalized();
        double d = Vector3d.Dot(n, V0 - A);
        var nu = n - w * Vector3d.Dot(n, w);
        Vector3d U = nu.Length > 1e-12 ? nu.Normalized() : Vector3d.Cross(w, Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
        var Vv = Vector3d.Cross(w, U);
        double ca = Math.Cos(alpha), sa = Math.Sin(alpha);
        double a = Vector3d.Dot(n, w) * ca, b = Vector3d.Dot(n, U) * sa;
        if (!(Math.Abs(a) > Math.Abs(b) + 1e-12)) return null;                 // не эллипс (парабола, гипербола)
        Vector3d X(double c, double sgnS) { double sn = sgnS * Math.Sqrt(Math.Max(0, 1 - c * c)); var g = w * ca + (U * c + Vv * sn) * sa; return A + g * (d / (a + b * c)); }
        var X0 = X(1, 1); var Xp = X(-1, 1);
        if (Vector3d.Dot(X0 - A, w) <= 0 || Vector3d.Dot(Xp - A, w) <= 0) return null;   // другая полость конуса
        var Cc = (X0 + Xp) / 2; var e1 = X0 - Xp; double ra = e1.Length / 2; e1 /= 2 * ra;
        // cos φ: проекция X(φ) − C на e1 равна нулю; X − A = d (w ca + U sa c)/(a + b c) (+ V-часть, ортогональна e1)
        double pw = Vector3d.Dot(w, e1) * ca * d, pu = Vector3d.Dot(U, e1) * sa * d, c0 = Vector3d.Dot(Cc - A, e1);
        // (pw + pu c)/(a + b c) = c0  →  c = (c0 a − pw)/(pu − c0 b)
        double den = pu - c0 * b; if (Math.Abs(den) < 1e-15) return null;
        double cm = (c0 * a - pw) / den; if (Math.Abs(cm) > 1) return null;
        double rb = Math.Abs(Vector3d.Dot(X(cm, 1) - Cc, Vv));
        var e2 = Vector3d.Cross(n, e1).Normalized(); if (Vector3d.Dot(e2, Vv) < 0) e2 = -e2;
        double Ang(Vector3d q) => Math.Atan2(Vector3d.Dot(q - Cc, e2) / rb, Vector3d.Dot(q - Cc, e1) / ra);
        double a1 = Ang(q1), a2 = Ang(q2), sw = a2 - a1;
        while (sw > Math.PI) sw -= 2 * Math.PI; while (sw < -Math.PI) sw += 2 * Math.PI;
        var el = new EllipseCurve3(new Frame3(Cc, e1, e2), ra, rb, a1, a1 + sw);
        if ((el.PointAt(0) - q1).Length > 1e-7 * Math.Max(1, ra) || (el.PointAt(1) - q2).Length > 1e-7 * Math.Max(1, ra)) return null;
        return el;
    }

    /// <summary>Окружность между плоской гранью и цилиндром, чья ось ⟂ плоскости (семейство тора, 06.11).</summary>
    static bool PlaneCylCircle(TEdge e)
    {
        var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (fs.Count != 2) return true;
        var sh = fs.Select(f => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))).ToList();
        var pl = sh.OfType<Geometry.Ssi.Plane>().FirstOrDefault(); var cy = sh.OfType<Geometry.Ssi.Cylinder>().FirstOrDefault();
        return pl != null && cy != null && Math.Abs(Math.Abs(Vector3d.Dot(pl.N.Normalized(), cy.Axis.Normalized())) - 1) < 1e-12;
    }

    static bool OnEdge(Seg s, TEdge x)
    {
        if (x.Curve is not LineCurve3) return s.Id == x;     // дуга: и укороченное звено помнит своё ребро
        var a = x.V0.P; var b = x.V1.P; var ab = b - a; double l2 = ab.LengthSquared; if (l2 < 1e-24) return false;
        foreach (var p in new[] { s.A, s.B })
        {
            double u = Vector3d.Dot(p - a, ab) / l2;
            if (u < -1e-9 || u > 1 + 1e-9 || (a + ab * u - p).Length > 1e-7) return false;
        }
        return s.Curve is LineCurve3;
    }

    static TSolid? Build(TSolid body, IReadOnlyList<EdgeRef> refs, double r, bool ch, ref string? why)
    {
        var swStage = System.Diagnostics.Stopwatch.StartNew(); string stageName = "describe";
        void Stage(string next) { if (Prof != null) Prof($"{stageName} {swStage.Elapsed.TotalMilliseconds:F1} ms"); stageName = next; swStage.Restart(); }
        var edges = new List<TEdge>(); var circs = new List<TEdge>(); var closedCC = new List<TEdge>();
        var whole = FilletOp.WholeNamedEdges(new List<TSolid> { body }, refs);
        foreach (var rf in refs)
        {
            var e = FilletOp.Resolve(new List<TSolid> { body }, rf, out _) ?? (whole.TryGetValue(rf, out var we) ? we : null);
            if (e == null) { why = "edge not resolved"; return null; }
            // окружность между плоскостью и цилиндром, ось ⟂ плоскости — тор (06.11); прочие замкнутые окружности (плоскость/конус,
            // цилиндр/шар…) — общий построитель замкнутых рёбер (06.12/06.14), P3D_DRQUADRIC=0 — прежний отказ
            if (e.Curve is ArcCurve3 && Circles && !(QuadricBlendsOn && CylCyl && !ch && ReferenceEquals(e.V0, e.V1) && !PlaneCylCircle(e))) { if (!circs.Contains(e)) circs.Add(e); continue; }
            if (e.Curve is ArcCurve3 && QuadricBlendsOn && CylCyl && !ch && ReferenceEquals(e.V0, e.V1)) { if (!closedCC.Contains(e)) closedCC.Add(e); continue; }
            // замкнутое ребро-кривая (цилиндр/цилиндр, 06.12) — скругление; P3D_DRCYLCYL=0 — прежний построитель
            if (CylCyl && !ch && e.Curve is not (LineCurve3 or ArcCurve3) && ReferenceEquals(e.V0, e.V1)) { if (!closedCC.Contains(e)) closedCC.Add(e); continue; }
            if (e.Curve is not LineCurve3) { why = "not a straight edge"; return null; }
            if (edges.Contains(e)) continue;
            edges.Add(e);
        }
        // KRN-06: касательный контур из отрезков и дуг (P3D_DRTANLOOP) — свой построитель ниже; иначе прежний отказ
        bool tanLoop = circs.Count > 0 && edges.Count > 0 && TanLoops && closedCC.Count == 0;
        if (circs.Count > 0 && edges.Count > 0 && !tanLoop) { why = "circular and straight edges in one operation"; return null; }
        var law = _law; var cham = _chamfer;
        if (cham != null && UnequalChainsOff && edges.Any(e => new[] { e.V0.P, e.V1.P }.Any(p => edges.Count(x => (x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7) > 1))) { why = "unequal chamfer: chains and corners not in this family"; return null; }
        // размер фаски ребра x на грани F: несимметричная — по грани первого размера, иначе r
        double Dist(TEdge x, TFace F) => cham is { } cmd && cmd.First.TryGetValue(x, out var ff) ? (ReferenceEquals(ff, F) ? cmd.D1 : cmd.D2) : r;
        // точка пересечения прямой q + tq·s с прямой A2 + t2·s (или null, если не пересекаются)
        static Vector3d? MeetLines(Vector3d q, Vector3d tq, Vector3d A2, Vector3d t2, double scale)
        {
            var cr12 = Vector3d.Cross(tq, t2); if (cr12.LengthSquared < 1e-18) return null;
            double sq = Vector3d.Dot(Vector3d.Cross(A2 - q, t2), cr12) / cr12.LengthSquared;
            var pt = q + tq * sq;
            return (pt - (A2 + t2 * Vector3d.Dot(pt - A2, t2))).Length < 1e-9 * Math.Max(1, scale) ? pt : null;
        }
        if (law != null && (ch || edges.Count != 1 || circs.Count > 0 || closedCC.Count > 0)) { why = "variable radius: one straight plane/plane edge only"; return null; }
        if (closedCC.Count > 0 && (edges.Count > 0 || circs.Count > 0)) { why = "cyl/cyl edges with other edges in one operation"; return null; }
        // общие вершины выбранных рёбер: ровно два ребра в вершине — стык цепочки (06.10, срез по биссекторной плоскости);
        // три и больше — угловой узел (06.13, не в этом семействе)
        TEdge? ChainMate(TEdge e, Vector3d p)
        {
            var m = edges.Where(x => x != e && ((x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7)).ToList();
            return m.Count == 1 ? m[0] : null;
        }
        foreach (var e in edges)
            foreach (var p in new[] { e.V0.P, e.V1.P })
                if (edges.Count(x => (x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7) is int nsel && ((nsel > 3 && (!NCorner || ch || !Corners)) || (nsel == 3 && !Corners))) { why = "three or more selected edges meet at a vertex (corner off or degree > 3)"; return null; }
                else if (ChainMate(e, p) != null && !Chains) { why = "selected edges share a vertex (chains off)"; return null; }

        // смежность по положению (вершинные списки ведут не все построители)
        var allEdges = body.Edges.ToList();
        List<TEdge> At(Vector3d p) => allEdges.Where(e => (e.V0.P - p).Length < 1e-7 || (e.V1.P - p).Length < 1e-7).ToList();

        if (Debug != null)
            foreach (var ed in body.Edges)
                if (ed.CoEdges.Count == 2 && ed.CoEdges[0].Reversed == ed.CoEdges[1].Reversed)
                    Debug($"ORIG same-sense edge {ed.V0.P:0.###}->{ed.V1.P:0.###} faces {ed.CoEdges[0].Loop?.Face?.Tag}/{ed.CoEdges[1].Loop?.Face?.Tag}");
        // описание граней
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;

        var blends = new List<FDesc>();
        if (tanLoop)
        {
            // несколько несвязанных цепочек (передняя и задняя грань) — каждая своим проходом (P3D_DRTANGROUPS=0 — одним)
            var allT = edges.Concat(circs).ToList();
            var groups = new List<List<TEdge>>();
            if (TanGroups)
            {
                var left = new List<TEdge>(allT);
                while (left.Count > 0)
                {
                    var g = new List<TEdge> { left[0] }; left.RemoveAt(0);
                    for (bool grew = true; grew;)
                    {
                        grew = false;
                        for (int i = left.Count - 1; i >= 0; i--)
                            if (g.Any(x => new[] { x.V0.P, x.V1.P }.Any(p => (left[i].V0.P - p).Length < 1e-7 || (left[i].V1.P - p).Length < 1e-7)))
                            { g.Add(left[i]); left.RemoveAt(i); grew = true; }
                    }
                    groups.Add(g);
                }
                if (groups.Any(g => !g.Any(x => circs.Contains(x)))) { why = "tangent chains: a chain without arcs next to tangent chains"; return null; }
            }
            else groups.Add(allT);
            foreach (var g in groups)
                if (!TangentLoopBlends(g, byFace, r, ch, blends, ref why)) return null;
            edges.Clear(); circs.Clear();
        }
        if (circs.Count > 0 && !CircularBlends(circs, byFace, blends, r, ch, ref why)) return null;
        if (closedCC.Count > 0 && !CylCylBlends(body, closedCC, byFace, blends, r, ref why)) return null;
        var corners = new Dictionary<Vector3d, (Vector3d C, Vector3d[] Ns, List<(Vector3d A, Vector3d B, ICurve3 Arc)> Arcs)>();
        var asymFix = new List<(FDesc Fb, TEdge E3, Vector3d P, Vector3d R, ICurve3 Sec)>();
        // выпуклость ребра: (n1 × n2)·t1 > 0 по направлению ребра в контуре первой грани
        int? SigmaOf(TEdge e)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count != 2) return null;
            if (!byFace[fs[0]].Surf.IsPlanar && byFace[fs[1]].Surf.IsPlanar && LineCyl) fs.Reverse();   // плоская — первой
            var a = byFace[fs[0]].N;
            var b = byFace[fs[1]].Surf.IsPlanar ? byFace[fs[1]].N : NormalOf(byFace[fs[1]], (e.V0.P + e.V1.P) / 2);
            if (a.Length < 1e-12 || b.Length < 1e-12) return null;
            if (ConvGeom)
            {
                // ВЫПУКЛОСТЬ ПО ГЕОМЕТРИИ, НЕ ПО ОБХОДУ: направление от ребра внутрь первой грани (проверка точки по её
                // контурам, чётностью — от обхода не зависит); выпукло, если оно против нормали второй грани.
                // Мутация P3D_DRCONVGEOM=0 — по направлению звена в контуре первой грани (у обращённого контура неверно).
                var tt = (e.V1.P - e.V0.P).Normalized();
                var d1 = IntoPlanarFace(byFace[fs[0]], (e.V0.P + e.V1.P) / 2, Vector3d.Cross(a.Normalized(), tt).Normalized());
                if (d1 == null) return null;
                double cg = Vector3d.Dot(b.Normalized(), d1.Value);
                return Math.Abs(cg) < 1e-9 ? null : (cg < 0 ? -1 : 1);
            }
            var sg = byFace[fs[0]].Loops.SelectMany(l => l).FirstOrDefault(x => x.Orig == e);
            if (sg == null) return null;
            double cv = Vector3d.Dot(Vector3d.Cross(a.Normalized(), b.Normalized()), (sg.B - sg.A).Normalized());
            return Math.Abs(cv) < 1e-9 ? null : (cv > 0 ? -1 : 1);
        }
        var sigmaOf = edges.ToDictionary(x => x, x => SigmaOf(x));
        if (Debug != null)
        {
            foreach (var f in body.Faces) Debug($"face {f.Tag} NI {AnalyticVolume.NormalIntegral(f).Normalized():0.###} NA {(f.Loops.First().CoEdges.First().Start.P is var pp ? f.NormalAt(pp) : default):0.###}");
            foreach (var x in edges) Debug($"edge {x.V0.P:0.#}->{x.V1.P:0.#} sigma {sigmaOf[x]}");
        }
        foreach (var e in edges)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count != 2) { why = "edge is not between two faces"; return null; }
            var F1 = byFace[fs[0]]; var F2 = byFace[fs[1]];
            // ПЛОСКОСТЬ/ЦИЛИНДР (06.11): прямое ребро вдоль оси цилиндра — скругление (фаска — прежний построитель)
            bool mixed = !(F1.Surf.IsPlanar && F2.Surf.IsPlanar);
            if (law != null && mixed) { why = "variable radius: plane/plane only"; return null; }
            if (mixed && !(LineCyl && (!ch || LineCylCh) && ((F1.Surf.IsPlanar && F2.IsCyl) || (F2.Surf.IsPlanar && F1.IsCyl))))
            { why = "a face of the edge is not planar (plane/cylinder: rounds of edges along the axis only)"; return null; }
            var n1 = F1.Surf.IsPlanar ? F1.N : NormalOf(F1, e.V0.P); var n2 = F2.Surf.IsPlanar ? F2.N : NormalOf(F2, e.V0.P);
            if (n1.Length < 1e-12 || n2.Length < 1e-12) { why = "face normal undefined"; return null; }
            n1.Normalize(); n2.Normalize();
            double c12 = Vector3d.Dot(n1, n2);
            if (c12 > 1 - 1e-6 || c12 < -1 + 1e-3) { why = "faces (anti)parallel"; return null; }
            // направление ребра в контуре F1: выпуклость — (n1 × n2)·t1 > 0
            if (sigmaOf[e] is not int sg0) { why = "convexity undecided (tangent faces or the face side not found)"; return null; }
            double sigma = sg0;                                     // −1 выпуклое, +1 вогнутое
            var P0 = e.V0.P; var P1 = e.V1.P; var t = (P1 - P0).Normalized(); double L = (P1 - P0).Length;
            double lr0 = 0, lr1 = 0, lAlpha = 0; Vector3d lW = default, lApex = default, lq10 = default, lq11 = default, lq20 = default, lq21 = default;
            Vector3d C0, q10, q20;                                  // точка оси (spine) при P0 и контакты при P0
            FDesc? Cy = null; Vector3d qCyl0 = default, A0c = default;
            if (!mixed)
            {
                C0 = P0 + (n1 + n2) * (sigma * r / (1 + c12));
                q10 = C0 - n1 * (sigma * r); q20 = C0 - n2 * (sigma * r);
                if (law != null)
                {
                    // KRN-06 (06.14): ПЕРЕМЕННЫЙ РАДИУС (линейный закон) — шары радиуса R(s) с центрами C(s) = P0 + t·s + k·R(s),
                    // k = σ(n1 + n2)/(1 + n1·n2): касаются обеих плоскостей; огибающая — круговой конус (ось — линия центров,
                    // sin α = |R'|/|C'|); контакты — прямые Q_i(s) = C(s) − σ R(s) n_i.
                    lr0 = law(P0); lr1 = law(P1);
                    if (!(lr0 > 1e-9) || !(lr1 > 1e-9)) { why = "variable radius: a radius is not positive"; return null; }
                    var kk = (n1 + n2) * (sigma / (1 + c12));
                    double gL = (lr1 - lr0) / L;
                    if (Math.Abs(gL) < 1e-12) { why = "variable radius: constant law"; return null; }
                    Vector3d Cs(double s0) => P0 + t * s0 + kk * (lr0 + gL * s0);
                    C0 = Cs(0); q10 = C0 - n1 * (sigma * lr0); q20 = C0 - n2 * (sigma * lr0);
                    lq11 = Cs(L) - n1 * (sigma * lr1); lq21 = Cs(L) - n2 * (sigma * lr1);
                    var dC = t + kk * gL; double sinA = Math.Abs(gL) / dC.Length;
                    if (!(sinA < 0.99)) { why = "variable radius: the radius changes too fast"; return null; }
                    lAlpha = Math.Asin(sinA); lW = dC.Normalized() * Math.Sign(gL); lApex = Cs(-lr0 / gL);
                    lq10 = q10; lq20 = q20;
                }
            }
            else if (ch)
            {
                // ФАСКА прямого ребра плоскость/цилиндр вдоль оси (06.15): контакты — ниже, после размеров по граням
                var Pl = F1.Surf.IsPlanar ? F1 : F2; Cy = Pl == F1 ? F2 : F1;
                if (Math.Abs(Math.Abs(Vector3d.Dot(Cy.AxW, t)) - 1) > 1e-12) { why = "plane/cylinder: the edge is not along the cylinder axis"; return null; }
                A0c = Cy.AxO + Cy.AxW * Vector3d.Dot(P0 - Cy.AxO, Cy.AxW);
                C0 = P0; q10 = P0; q20 = P0;
            }
            else
            {
                // сечение поперёк оси: центр шара — на расстоянии σr от плоскости и ρ = R + σ·s·r от оси цилиндра (s = +1 —
                // материал внутри цилиндра); из двух решений — ближнее к ребру
                var Pl = F1.Surf.IsPlanar ? F1 : F2; Cy = Pl == F1 ? F2 : F1;
                if (Math.Abs(Math.Abs(Vector3d.Dot(Cy.AxW, t)) - 1) > 1e-12) { why = "plane/cylinder: the edge is not along the cylinder axis"; return null; }
                var np = Pl.N; A0c = Cy.AxO + Cy.AxW * Vector3d.Dot(P0 - Cy.AxO, Cy.AxW);
                double sC = Vector3d.Dot(NormalOf(Cy, P0), (P0 - A0c).Normalized()) > 0 ? 1 : -1;
                double rho = Cy.AxR + sigma * sC * r;
                if (!(rho > 1e-9)) { why = "plane/cylinder: radius too large for the cylinder"; return null; }
                var ex = Vector3d.Cross(t, np).Normalized();
                double xA = Vector3d.Dot(A0c - P0, ex), yA = Vector3d.Dot(A0c - P0, np), yC = sigma * r;
                double disc = rho * rho - (yC - yA) * (yC - yA);
                if (disc < 0) { why = "plane/cylinder: no ball position"; return null; }
                double xC1 = xA + Math.Sqrt(disc), xC2 = xA - Math.Sqrt(disc), xC = Math.Abs(xC1) < Math.Abs(xC2) ? xC1 : xC2;
                C0 = P0 + ex * xC + np * yC;
                var qPl = C0 - np * (sigma * r); qCyl0 = A0c + (C0 - A0c) * (Cy.AxR / rho);
                q10 = Pl == F1 ? qPl : qCyl0; q20 = Pl == F1 ? qCyl0 : qPl;
            }
            // фаска: контактные прямые P + d·u_i (u_i — в грани i поперёк ребра, от ребра в грань)
            var u1 = (n2 - n1 * c12).Normalized() * sigma; var u2 = (n1 - n2 * c12).Normalized() * sigma;
            double dF1 = r, dF2 = r;
            if (cham is { } cm)
            {
                if (!cm.First.TryGetValue(e, out var f1st)) { why = "unequal chamfer: the first-size face is unknown"; return null; }
                dF1 = ReferenceEquals(F1.Orig, f1st) ? cm.D1 : cm.D2; dF2 = ReferenceEquals(F2.Orig, f1st) ? cm.D1 : cm.D2;
            }
            if (mixed && ch)
            {
                // плоскость: P0 + d·u (поперёк ребра в плоскости); цилиндр — точка сечения на ХОРДЕ d от ребра (как у
                // BRepFilletAPI_MakeChamfer: сверка объёмов до 1e-12), поворот вокруг оси на φ = 2·asin(d/2R) в сторону грани.
                // Грань фаски — плоскость через две прямые, параллельные оси.
                bool plFirst = F1.Surf.IsPlanar;
                double dPl = plFirst ? dF1 : dF2, dCy = plFirst ? dF2 : dF1;
                if (!(dCy < 2 * Cy!.AxR * 0.999)) { why = "plane/cylinder chamfer: size too large for the cylinder"; return null; }
                var rad = (P0 - A0c); double Rc = rad.Length; rad /= Rc;
                var uc = plFirst ? u2 : u1; uc = (uc - rad * Vector3d.Dot(uc, rad) - t * Vector3d.Dot(uc, t)).Normalized();
                double phi = 2 * Math.Asin(dCy / (2 * Rc));
                var qC = A0c + rad * (Rc * Math.Cos(phi)) + uc * (Rc * Math.Sin(phi));
                var qP = P0 + (plFirst ? u1 : u2) * dPl;
                q10 = plFirst ? qP : qC; q20 = plFirst ? qC : qP; qCyl0 = qC;
                Interlocked.Increment(ref LineCylChamfers);
            }
            Vector3d Q(int i, Vector3d onSpine) => ch ? (mixed ? (i == 1 ? q10 : q20) : P0 + (i == 1 ? u1 * dF1 : u2 * dF2)) : (i == 1 ? q10 : q20);
            var dir1 = (q10 - C0) / (law != null ? lr0 : r); var dir2 = (q20 - C0) / (law != null ? lr0 : r);   // направления от оси к контактам
            // базис сечения
            var e1 = (dir1 - t * Vector3d.Dot(dir1, t)).Normalized(); var e2 = Vector3d.Cross(t, e1);
            double Th(Vector3d d) => Math.Atan2(Vector3d.Dot(d, e2), Vector3d.Dot(d, e1));
            double th1 = Th(dir1), th2 = Th(dir2);
            double dth = th2 - th1; while (dth > Math.PI) dth -= 2 * Math.PI; while (dth < -Math.PI) dth += 2 * Math.PI;

            var endPts = new Dictionary<int, (Vector3d Q1, Vector3d Q2, ICurve3 Arc)>();
            var chainOf = new Dictionary<int, List<(ICurve3 C, Vector3d A, Vector3d B)>>();   // торец из нескольких кривых: от Q1 к Q2
            foreach (var (vi, V) in new[] { (0, P0), (1, P1) })
            {
                var inc = At(V);
                var others = inc.Where(x => x != e).ToList();
                // вершина степени n > 3 — только угол, где выбраны все n рёбер (06.13)
                bool nCorner = inc.Count > 3 && NCorner && !ch && Corners && !mixed
                    && others.All(x => edges.Contains(x));
                if (inc.Count != 3 && !nCorner) { why = $"vertex of degree {inc.Count}"; return null; }
                if (others.Any(x => x.Curve is not LineCurve3 && !(mixed && x.Curve is ArcCurve3))) { why = "curved neighbour edge"; return null; }
                if (mixed && edges.Any(x => x != e && ((x.V0.P - V).Length < 1e-7 || (x.V1.P - V).Length < 1e-7))) { why = "plane/cylinder: chains and corners not in this family"; return null; }
                var cornerSel = edges.Where(x => x != e && ((x.V0.P - V).Length < 1e-7 || (x.V1.P - V).Length < 1e-7)).ToList();
                if (cornerSel.Count >= 2 && cornerSel.Count == inc.Count - 1)
                {
                    // УГЛОВОЙ УЗЕЛ (06.13): три выбранных ребра в вершине степени 3 — шар радиуса r, касающийся трёх граней
                    // (центр C — пересечение трёх смещённых плоскостей). Цилиндр касается шара по большой окружности в
                    // плоскости через C поперёк оси: торец ленты — эта дуга; угол — сферический треугольник из трёх дуг.
                    if (cornerSel.Any(x => sigmaOf[x] != (int)sigma)) { why = "corner of convex and concave edges"; _mixedSigma = sigmaOf; return null; }
                    var cf = edges.Where(x => x == e || cornerSel.Contains(x)).SelectMany(x => x.CoEdges.Select(c => c.Loop?.Face)).OfType<TFace>().Distinct().ToList();
                    if (cf.Count != inc.Count || cf.Any(f => !byFace[f].Surf.IsPlanar)) { why = "corner faces are not planes, one per edge"; return null; }
                    if (!corners.TryGetValue(V, out var cr))
                    {
                        var ns = cf.Select(f => byFace[f].N).ToArray();
                        var ds = ns.Select(n => Vector3d.Dot(n, V) + sigma * r).ToArray();   // n·C = n·V + σ r
                        Vector3d? Cc = null;
                        for (int i0 = 0; i0 < ns.Length && Cc == null; i0++)
                            for (int i1 = i0 + 1; i1 < ns.Length && Cc == null; i1++)
                                for (int i2 = i1 + 1; i2 < ns.Length && Cc == null; i2++)
                                    Cc = Solve3(new[] { ns[i0], ns[i1], ns[i2] }, new[] { ds[i0], ds[i1], ds[i2] });
                        if (Cc == null) { why = "corner planes degenerate"; return null; }
                        // n > 3: шар должен касаться ВСЕХ граней (иначе угол — не сфера: заполнение общего вида — отказ)
                        for (int i = 0; i < ns.Length; i++)
                            if (Math.Abs(Vector3d.Dot(ns[i], Cc.Value) - ds[i]) > 1e-9 * Math.Max(1, r)) { why = "corner of degree > 3 without a common ball"; return null; }
                        if (ns.Length > 3) Interlocked.Increment(ref NCorners);
                        cr = (Cc.Value, ns, new List<(Vector3d A, Vector3d B, ICurve3 Arc)>());
                        corners[V] = cr;
                    }
                    var Ck = cr.C;
                    Vector3d k1, k2; ICurve3 karc;
                    if (ch)
                    {
                        // угол фаски: точка на грани — пересечение контактных прямых двух рёбер этой грани
                        Vector3d? Meet(TFace F, Vector3d q, Vector3d tq)
                        {
                            var x = cornerSel.First(y => y.CoEdges.Any(c => c.Loop?.Face == F));
                            var xf = x.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                            var nO = byFace[xf.First(f => f != F)].N; var nF = byFace[F].N;
                            var ux = (nO - nF * Vector3d.Dot(nF, nO)).Normalized() * sigmaOf[x]!.Value;
                            var A2 = x.V0.P + ux * Dist(x, F); var t2 = (x.V1.P - x.V0.P).Normalized();
                            var cr12 = Vector3d.Cross(tq, t2); if (cr12.LengthSquared < 1e-18) return null;
                            double sq = Vector3d.Dot(Vector3d.Cross(A2 - q, t2), cr12) / cr12.LengthSquared;
                            var pt = q + tq * sq;
                            return (pt - (A2 + t2 * Vector3d.Dot(pt - A2, t2))).Length < 1e-9 * Math.Max(1, r) ? pt : null;
                        }
                        var m1 = Meet(fs[0], Q(1, C0), t); var m2 = Meet(fs[1], Q(2, C0), t);
                        if (m1 == null || m2 == null) { why = "chamfer corner lines do not meet"; return null; }
                        k1 = m1.Value; k2 = m2.Value; karc = new LineCurve3(k1, k2);
                    }
                    else
                    {
                        // ось ленты проходит через центр шара
                        var dC = Ck - C0; if ((dC - t * Vector3d.Dot(dC, t)).Length > 1e-9 * Math.Max(1, r)) { why = "corner centre off the blend axis"; return null; }
                        k1 = Ck - n1 * (sigma * r); k2 = Ck - n2 * (sigma * r);
                        karc = Section(C0, t, e1, e2, r, Ck, t, th1, dth);
                    }
                    if ((karc.PointAt(karc.T0) - k1).Length > 1e-7 || (karc.PointAt(karc.T1) - k2).Length > 1e-7) { why = "corner arc does not meet the contact points"; return null; }
                    endPts[vi] = (k1, k2, karc);
                    cr.Arcs.Add((k1, k2, karc));
                    continue;
                }
                var mate = ChainMate(e, V);
                if (mate != null)
                {
                    // СТЫК ЦЕПОЧКИ: два цилиндра равного радиуса с пересекающимися осями пересекаются по плоским эллипсам в
                    // биссекторных плоскостях; нужная — биссектор двух НЕобщих граней (через третье ребро вершины).
                    if (sigmaOf[mate] != (int)sigma) { why = "chain of convex and concave edges"; _mixedSigma = sigmaOf; return null; }
                    var mateFaces = mate.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                    var Fa = mateFaces.Contains(fs[0]) ? fs[1] : mateFaces.Contains(fs[1]) ? fs[0] : null;
                    var Fb = mateFaces.FirstOrDefault(f => f != fs[0] && f != fs[1]);
                    if (Fa == null || Fb == null) { why = "chain edges without a common face"; return null; }
                    var e3 = others.First(x => x != mate);
                    var na = byFace[Fa].N; var nb = byFace[Fb].N;
                    var nm = na - nb;
                    if (nm.Length < 1e-9) { why = "degenerate chain bisector"; return null; }
                    nm.Normalize();
                    double tm = Vector3d.Dot(t, nm);
                    if (Math.Abs(tm) < 0.1) { why = "chain bisector too oblique"; return null; }
                    Vector3d OnM(Vector3d p0) => p0 + t * (Vector3d.Dot(V - p0, nm) / tm);
                    var m1 = OnM(Q(1, C0)); var m2 = OnM(Q(2, C0));
                    if (ch && cham != null)
                    {
                        // KRN-06 (06.15): несимметричная фаска на стыке — на общей грани пересечение контактных прямых двух рёбер,
                        // на необщей — пересечение контактной прямой с третьим ребром вершины (биссектор не годится: d1 ≠ d2)
                        var tMate = (mate.V1.P - mate.V0.P).Normalized(); var t3 = (e3.V1.P - e3.V0.P).Normalized();
                        // Fa, Fb — НЕобщие грани ребра и его соседа; общая — третья грань ребра
                        var Fc = fs[0] == Fa ? fs[1] : fs[0];
                        var nFb = byFace[Fb].N; var nFc = byFace[Fc].N;
                        var uMate = (nFb - nFc * Vector3d.Dot(nFc, nFb)).Normalized() * sigmaOf[mate]!.Value;
                        Vector3d? Pt(int i)
                        {
                            var Fi = fs[i - 1];
                            return Fi == Fc ? MeetLines(Q(i, C0), t, mate.V0.P + uMate * Dist(mate, Fc), tMate, r)
                                            : MeetLines(Q(i, C0), t, e3.V0.P, t3, r);
                        }
                        if (Pt(1) is not { } mm1 || Pt(2) is not { } mm2) { why = "unequal chamfer: chain contact lines do not meet"; return null; }
                        m1 = mm1; m2 = mm2;
                    }
                    // KRN-06 (06.10): НЕСИММЕТРИЧНЫЙ СТЫК (n_c·n_a ≠ n_c·n_b). Оси двух цилиндров лежат в плоскости, смещённой от
                    // общей грани на r, и пересекаются в O; цилиндры равного радиуса пересекаются по плоскому эллипсу в плоскости
                    // через O с нормалью t_e − t_m (направления рёбер от вершины). Эллипс идёт от общей точки контакта на общей
                    // грани и раньше доходит до контактной прямой одного из цилиндров на его необщей грани (точка P); другой
                    // цилиндр продолжается до необщей грани соседа: от P по сечению этой плоскостью до R — своей контактной прямой
                    // на ней (R лежит на третьем ребре). Так строит и OpenCascade (два цилиндра, без углового лоскута).
                    // Мутация P3D_DRASYM=0 — прежний отказ.
                    if (!ch && law == null && !AsymOff)
                    {
                        var Fc = fs[0] == Fa ? fs[1] : fs[0];
                        var nc = byFace[Fc].N.Normalized(); var nA = na.Normalized(); var nB = nb.Normalized();
                        // ось соседа: та же формула хребта
                        var mfs = mate.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                        var mn1 = byFace[mfs[0]].N.Normalized(); var mn2 = byFace[mfs[1]].N.Normalized(); double mc12 = Vector3d.Dot(mn1, mn2);
                        var mC0 = mate.V0.P + (mn1 + mn2) * (sigma * r / (1 + mc12)); var mt = (mate.V1.P - mate.V0.P).Normalized();
                        var Oax = MeetLines(C0, t, mC0, mt, Math.Max(1, (V - C0).Length));
                        if (Oax == null) { why = "chain: blend axes do not meet"; return null; }
                        var O = Oax.Value;
                        var tAway = (Vector3d.Dot(t, (P0 + P1) / 2 - V) > 0 ? t : -t);
                        var oV = (mate.V0.P - V).Length < 1e-7 ? mate.V1.P : mate.V0.P; var mAway = (oV - V).Normalized();
                        var nE = (tAway - mAway).Normalized();
                        var QC = O - nc * (sigma * r);
                        Vector3d HitPlane(Vector3d q, Vector3d d, Vector3d pp, Vector3d pn) => q + d * (Vector3d.Dot(pp - q, pn) / Vector3d.Dot(d, pn));
                        var PX = HitPlane(O - nA * (sigma * r), t, O, nE);      // эллипс ∩ контакт ребра на его необщей грани
                        var PY = HitPlane(O - nB * (sigma * r), mt, O, nE);     // эллипс ∩ контакт соседа на его необщей грани
                        if ((PX - PY).Length > 1e-9 * Math.Max(1, r))
                        {
                            // угол на цилиндре ребра от контакта на общей грани
                            Vector3d Dir(Vector3d x) { var d = x - O; d -= t * Vector3d.Dot(d, t); return d.Normalized(); }
                            double Ang(Vector3d x) => Math.Acos(Math.Clamp(Vector3d.Dot(Dir(x), -nc * sigma), -1, 1));
                            bool otherFirst = Ang(PY) < Ang(PX);               // эллипс раньше доходит до контакта соседа
                            Vector3d endFa; var ch3 = new List<(ICurve3 C, Vector3d A, Vector3d B)>();
                            double ThX(Vector3d x) => Th(Dir(x));
                            double Sweep(double a0, double a1) { double d = a1 - a0; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return d; }
                            if (otherFirst)
                            {
                                // эллипс QC → PY, затем сечение плоскостью необщей грани соседа PY → R
                                var R = HitPlane(O - nA * (sigma * r), t, e3.V0.P, nB);
                                var ell = Section(C0, t, e1, e2, r, O, nE, ThX(QC), Sweep(ThX(QC), ThX(PY)));
                                var sec = Section(C0, t, e1, e2, r, e3.V0.P, nB, ThX(PY), Sweep(ThX(PY), ThX(R)));
                                ch3.Add((ell, QC, PY)); ch3.Add((sec, PY, R)); endFa = R;
                                asymFix.Add((byFace[Fb], e3, PY, R, sec));
                            }
                            else
                            {
                                var ell = Section(C0, t, e1, e2, r, O, nE, ThX(QC), Sweep(ThX(QC), ThX(PX)));
                                ch3.Add((ell, QC, PX)); endFa = PX;
                            }
                            foreach (var (cc, ca, cb) in ch3)
                                if ((cc.PointAt(cc.T0) - ca).Length > 1e-7 || (cc.PointAt(cc.T1) - cb).Length > 1e-7) { why = "asymmetric chain: junction curve does not meet its points"; return null; }
                            // контакт на необщей грани — внутри третьего ребра (R) или на нём (P на своей контактной прямой — внутри ребра)
                            if (otherFirst)
                            {
                                var a = e3.V0.P; var b = e3.V1.P; double len = (b - a).Length;
                                var chk = endFa;
                                double u = Vector3d.Dot(chk - a, b - a) / (len * len), dist = ((a + (b - a) * u) - chk).Length;
                                if (dist > 1e-7 * Math.Max(1, len) || u <= 1e-6 || u >= 1 - 1e-6) { why = "radius too large for the chain's third edge"; return null; }
                            }
                            bool f1c = byFace[Fc] == F1;                        // Q1 — на F1
                            var q1a = f1c ? QC : endFa; var q2a = f1c ? endFa : QC;
                            if (!f1c) { ch3.Reverse(); ch3 = ch3.Select(x => (x.C, x.B, x.A)).ToList(); }
                            endPts[vi] = (q1a, q2a, ch3[0].C); chainOf[vi] = ch3; Interlocked.Increment(ref AsymJunctions);
                            continue;
                        }
                    }
                    // контакт на необщей грани — внутри третьего ребра
                    var qa3 = byFace[Fa] == F1 ? m1 : m2;
                    {
                        var a = e3.V0.P; var b = e3.V1.P; double len = (b - a).Length;
                        double u = Vector3d.Dot(qa3 - a, b - a) / (len * len), dist = ((a + (b - a) * u) - qa3).Length;
                        if (dist > 1e-7 * Math.Max(1, len) || u <= 1e-6 || u >= 1 - 1e-6) { why = "radius too large for the chain's third edge"; return null; }
                    }
                    ICurve3 marc = ch ? new LineCurve3(m1, m2) : Section(C0, t, e1, e2, r, V, nm, th1, dth);
                    if ((marc.PointAt(marc.T0) - m1).Length > 1e-7 || (marc.PointAt(marc.T1) - m2).Length > 1e-7) { why = "miter arc does not meet the contact points"; return null; }
                    endPts[vi] = (m1, m2, marc);
                    continue;
                }
                // торцевая грань: содержит обе соседние прямые, не F1 и не F2
                var Fe = faces.FirstOrDefault(fd => fd != F1 && fd != F2 && fd.Loops.SelectMany(l => l).Count(s => others.Any(o => OnEdge(s, o))) == 2);
                if (Fe == null || !Fe.Surf.IsPlanar) { why = "end face missing or not planar"; return null; }
                var ne = Fe.N;
                double tn = Vector3d.Dot(t, ne);
                if (Math.Abs(tn) < 0.1) { why = "end face too oblique"; return null; }
                // контактные точки на плоскости торца
                Vector3d OnEnd(Vector3d p0) => p0 + t * (Vector3d.Dot(V - p0, ne) / tn);
                Vector3d LineHit(Vector3d a, Vector3d b) => a + (b - a) * (Vector3d.Dot(V - a, ne) / Vector3d.Dot(b - a, ne));
                var q1 = law != null ? LineHit(lq10, lq11) : OnEnd(Q(1, C0)); var q2 = law != null ? LineHit(lq20, lq21) : OnEnd(Q(2, C0));
                // каждая — внутри своего соседнего ребра (F_i ∩ Fe)
                foreach (var (q, Fi) in new[] { (q1, F1), (q2, F2) })
                {
                    var ne_i = others.FirstOrDefault(x => Fi.Loops.SelectMany(l => l).Any(s => OnEdge(s, x)));
                    if (ne_i == null) { why = "neighbour edge of the face not found"; return null; }
                    if (ne_i.Curve is ArcCurve3 nar)
                    {
                        double? tau = ArcParam(nar, q);
                        if (tau == null || tau <= 1e-6 || tau >= 1 - 1e-6) { why = "radius too large for a neighbour arc"; return null; }
                        continue;
                    }
                    var a = ne_i.V0.P; var b = ne_i.V1.P; double len = (b - a).Length;
                    double u = Vector3d.Dot(q - a, b - a) / (len * len), dist = ((a + (b - a) * u) - q).Length;
                    if (dist > 1e-7 * Math.Max(1, len) || u <= 1e-6 || u >= 1 - 1e-6) { why = "radius too large for a neighbour edge"; return null; }
                }
                // дуга сечения цилиндра плоскостью торца
                ICurve3? arcL = law != null ? ConeSection(lApex, lW, lAlpha, V, ne, q1, q2) : null;
                if (law != null && arcL == null) { why = "variable radius: the end plane does not cut the cone in an ellipse"; return null; }
                ICurve3 arc = arcL ?? (ch ? new LineCurve3(q1, q2) : Section(C0, t, e1, e2, r, V, ne, th1, dth));
                if ((arc.PointAt(arc.T0) - q1).Length > 1e-7 || (arc.PointAt(arc.T1) - q2).Length > 1e-7) { why = "end arc does not meet the contact points"; return null; }
                endPts[vi] = (q1, q2, arc);
                // правка торцевой грани: в вершине V — укоротить соседние и вставить дугу
                foreach (var loop in Fe.Loops)
                    for (int k = 0; k < loop.Count; k++)
                    {
                        var s = loop[k];
                        if ((s.B - V).Length >= 1e-7) continue;
                        var nx = loop[(k + 1) % loop.Count];
                        bool sIn1 = others.Any(o => OnEdge(s, o) && F1.Loops.SelectMany(l => l).Any(z => OnEdge(z, o)));
                        var qa = sIn1 ? q1 : q2; var qb = sIn1 ? q2 : q1;
                        var sNew = Shorten(s, null, qa); var nNew = Shorten(nx, qb, null);
                        if (sNew == null || nNew == null) { why = "end face: a neighbour cannot be shortened"; return null; }
                        var arcSeg = new Seg { Curve = arc, A = qa, B = qb };
                        loop[k] = sNew; loop[(k + 1) % loop.Count] = nNew;
                        loop.Insert(k + 1, arcSeg);
                        Fe.Touched = true;
                        break;
                    }
            }
            // правка F1, F2: ребро → контактная прямая, соседние — укоротить
            foreach (var (Fi, i) in new[] { (F1, 1), (F2, 2) })
            {
                foreach (var loop in Fi.Loops)
                    for (int k = 0; k < loop.Count; k++)
                    {
                        var s = loop[k];
                        if (s.Id != e) continue;
                        // направление звена — по исходному ребру (звено могло быть уже укорочено соседом по цепочке)
                        bool fwd = Vector3d.Dot(s.B - s.A, P1 - P0) > 0;
                        var qP0 = i == 1 ? endPts[0].Q1 : endPts[0].Q2; var qP1 = i == 1 ? endPts[1].Q1 : endPts[1].Q2;
                        var qa = fwd ? qP0 : qP1; var qb = fwd ? qP1 : qP0;
                        var pv = Shorten(loop[(k - 1 + loop.Count) % loop.Count], null, qa); var nx = Shorten(loop[(k + 1) % loop.Count], qb, null);
                        if (pv == null || nx == null) { why = "a neighbour of the edge cannot be shortened"; return null; }
                        loop[(k - 1 + loop.Count) % loop.Count] = pv; loop[(k + 1) % loop.Count] = nx;
                        loop[k] = new Seg { Curve = new LineCurve3(qa, qb), A = qa, B = qb, Id = e };
                        Fi.Touched = true;
                        break;
                    }
                // полоса сопряжения не должна задевать другие контуры грани (у цилиндрической — проверит сборка и годность)
                if (!Fi.Surf.IsPlanar) continue;
                var strip = new[] { P0, P1, i == 1 ? endPts[1].Q1 : endPts[1].Q2, i == 1 ? endPts[0].Q1 : endPts[0].Q2 };
                foreach (var loop in Fi.Loops)
                    foreach (var s in loop)
                        for (int k = 1; k < 8; k++)
                        {
                            var x = s.Curve.PointAt(s.Curve.T0 + (s.Curve.T1 - s.Curve.T0) * k / 8.0);
                            if (InQuad(strip, x, i == 1 ? n1 : n2) && !OnBoundary(strip, x)) { why = "another loop of the face lies in the blend strip"; return null; }
                        }
            }
            if (Cy != null)
            {
                // точка цилиндрической грани: от ребра за контактную прямую (поворот вокруг оси в полтора раза дальше)
                var Am = A0c + t * (L / 2); var rp = (P0 + t * (L / 2) - Am).Normalized(); var rq = (qCyl0 + t * (L / 2) - Am).Normalized();
                double ang = Math.Atan2(Vector3d.Dot(Vector3d.Cross(rp, rq), Cy.AxW), Vector3d.Dot(rp, rq)) * 1.5;
                var rr = rp * Math.Cos(ang) + Vector3d.Cross(Cy.AxW, rp) * Math.Sin(ang);
                Cy.Hint = Am + rr * Cy.AxR;
            }
            // грань сопряжения
            FDesc bl;
            if (ch)
            {
                // плоскость фаски: наружная нормаль — в сторону n1 + n2 (и у выпуклого, и у вогнутого ребра)
                var nc = Vector3d.Cross(t, Q(2, C0) - Q(1, C0)).Normalized(); if (Vector3d.Dot(nc, n1 + n2) < 0) nc = -nc;
                bl = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(Q(1, C0), nc)), Rev = false, Tag = "chamfer:face", Touched = true, N = nc };
            }
            else
            {
                var axisFrame = new Frame3(C0, e1, e2);   // W = t
                bl = new FDesc { Surf = new CylinderSurface(axisFrame, r), Rev = sigma > 0, Tag = "round:face", Touched = true };
                if (law != null)
                {
                    // конус: образующая в полуплоскости угла 0 рамки оси, от вершины на высоты контактных точек (с запасом)
                    var uu = Vector3d.Cross(lW, Math.Abs(lW.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
                    double H(Vector3d x) => Vector3d.Dot(x - lApex, lW);
                    var hs = new[] { endPts[0].Q1, endPts[0].Q2, endPts[1].Q1, endPts[1].Q2 }.Select(H).ToList();
                    double h0 = hs.Min() * 0.5, h1 = hs.Max() * 1.5; double ta = Math.Tan(lAlpha);
                    var gen = new LineCurve3(lApex + lW * h0 + uu * (h0 * ta), lApex + lW * h1 + uu * (h1 * ta));
                    var cone = new RevolvedSurface(gen, new Frame3(lApex, uu, Vector3d.Cross(lW, uu)), 0, 2 * Math.PI);
                    var qm = endPts[0].Arc.PointAt(0.5); var nr = qm - lApex; nr -= lW * Vector3d.Dot(nr, lW);
                    var want = sigma < 0 ? nr : -nr;
                    bool rv = Vector3d.Dot(cone.NormalAt(cone.ProjectToUV(qm)), want) < 0;
                    bl = new FDesc { Surf = cone, Rev = rv, Tag = "round:face", Touched = true };
                }
            }
            var a0 = endPts[0]; var a1 = endPts[1];
            var loopB = new List<Seg> { new() { Curve = new LineCurve3(a0.Q1, a1.Q1), A = a0.Q1, B = a1.Q1 } };
            if (chainOf.TryGetValue(1, out var cb1)) foreach (var (cc, ca, cbb) in cb1) loopB.Add(new() { Curve = cc, A = ca, B = cbb });
            else loopB.Add(new() { Curve = a1.Arc, A = a1.Q1, B = a1.Q2 });
            loopB.Add(new() { Curve = new LineCurve3(a1.Q2, a0.Q2), A = a1.Q2, B = a0.Q2 });
            if (chainOf.TryGetValue(0, out var cb0)) foreach (var (cc, ca, cbb) in Enumerable.Reverse(cb0)) loopB.Add(new() { Curve = cc, A = cbb, B = ca });
            else loopB.Add(new() { Curve = a0.Arc, A = a0.Q2, B = a0.Q1 });
            var mid = ch ? (Q(1, C0) + Q(2, C0)) / 2 + t * (L / 2) : C0 + t * (L / 2) + (e1 * Math.Cos(th1 + dth / 2) + e2 * Math.Sin(th1 + dth / 2)) * r;
            // обход ленты — согласованно с соседней гранью F1: общая контактная прямая проходится ими во встречных
            // направлениях (как и всё соседство тела; нормализация выравнивает именно это)
            var c1 = F1.Loops.SelectMany(l => l).First(sx => sx.Orig == null && sx.Curve is LineCurve3 && (((sx.A - a0.Q1).Length < 1e-7 && (sx.B - a1.Q1).Length < 1e-7) || ((sx.A - a1.Q1).Length < 1e-7 && (sx.B - a0.Q1).Length < 1e-7)));
            bool f1Forward = (c1.A - a0.Q1).Length < 1e-7;             // F1 идёт Q1(V0) → Q1(V1)
            Debug?.Invoke($"blend loop: F1 forward {f1Forward} sigma {sigma}; F1 loops n {n1:0.###} surfN×rev {F1.Surf.NormalAt(F1.Surf.ProjectToUV(P0)) * (F1.Rev ? -1 : 1):0.###}; F2 loops n {n2:0.###} surfN×rev {F2.Surf.NormalAt(F2.Surf.ProjectToUV(P0)) * (F2.Rev ? -1 : 1):0.###} midRadial {(mid - (C0 + t * (L / 2))).Normalized():0.###}");
            if (f1Forward)
            {
                loopB.Reverse();
                foreach (var s in loopB) (s.A, s.B) = (s.B, s.A);
            }
            if (law != null && bl.Surf is RevolvedSurface lcs) { var mm = (endPts[0].Arc.PointAt(0.5) + endPts[1].Arc.PointAt(0.5)) / 2; mid = lcs.PointAt(lcs.ProjectToUV(mm)); }
            bl.Loops.Add(loopB); bl.Hint = mid;
            blends.Add(bl);
        }

        // 06.10: несимметричные стыки — третье ребро на необщей грани соседа укоротить до R, между P и R — сечение
        foreach (var (Fb, e3, Pp, Rr, sec) in asymFix)
        {
            bool done = false;
            foreach (var loop in Fb.Loops)
                for (int k = 0; k < loop.Count && !done; k++)
                {
                    var s0 = loop[k];
                    if (s0.Id != e3 && s0.Orig != e3) continue;
                    bool atA = (s0.A - Pp).Length < 1e-7, atB = (s0.B - Pp).Length < 1e-7;
                    if (!atA && !atB) continue;
                    var sh = atA ? Shorten(s0, Rr, null) : Shorten(s0, null, Rr);
                    if (sh == null) { why = "asymmetric chain: the third edge cannot be shortened"; return null; }
                    sh.Id = e3; loop[k] = sh;
                    if (atA) loop.Insert(k, new Seg { Curve = sec, A = Pp, B = Rr });
                    else loop.Insert(k + 1, new Seg { Curve = sec, A = Rr, B = Pp });
                    Fb.Touched = true; done = true;
                }
            if (!done) { why = "asymmetric chain: the neighbour's third edge not found"; return null; }
        }
        // угловые сферические треугольники
        foreach (var (V, cr) in corners)
        {
            if (cr.Arcs.Count != cr.Ns.Length) { why = "corner with a missing blend"; return null; }
            double sg = sigmaOf[edges.First(x => (x.V0.P - V).Length < 1e-7 || (x.V1.P - V).Length < 1e-7)]!.Value;
            var dsum = cr.Ns.Aggregate(Vector3d.Zero, (a, b) => a + b);
            var dir = (-dsum * sg).Normalized();                 // от центра к середине треугольника
            var helper = Math.Abs(dir.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
            var W = Vector3d.Cross(dir, helper).Normalized(); var U = -dir; var Vv = Vector3d.Cross(W, U);
            ISurface3 sph = ch ? new PlaneSurface(Frame3.FromNormal(cr.C, dsum.Normalized()))
                : new RevolvedSurface(new ArcCurve3(new Frame3(cr.C, U, W), r, -Math.PI / 2, Math.PI / 2), new Frame3(cr.C, U, Vv), 0, 2 * Math.PI);
            // контур: дуги цепочкой (направление — любое согласованное; сторону выставит нормализация)
            var left = cr.Arcs.ToList(); var loop = new List<Seg>();
            var cur = left[0]; left.RemoveAt(0);
            loop.Add(new Seg { Curve = cur.Arc, A = cur.A, B = cur.B });
            var at = cur.B;
            while (left.Count > 0)
            {
                int i = left.FindIndex(x => (x.A - at).Length < 1e-7 || (x.B - at).Length < 1e-7);
                if (i < 0) { why = "corner arcs do not close"; return null; }
                var x = left[i]; left.RemoveAt(i);
                bool fw = (x.A - at).Length < 1e-7;
                loop.Add(new Seg { Curve = x.Arc, A = fw ? x.A : x.B, B = fw ? x.B : x.A });
                at = fw ? x.B : x.A;
            }
            if ((at - loop[0].A).Length > 1e-7) { why = "corner loop not closed"; return null; }
            if (ch)
            {
                // плоский треугольник угла фаски: нормаль — по трём точкам, наружу — в сторону суммы нормалей граней
                var a0 = loop[0].A; var nt = Vector3d.Cross(loop[0].B - a0, loop[1].B - a0).Normalized(); if (Vector3d.Dot(nt, dsum) < 0) nt = -nt;
                sph = new PlaneSurface(Frame3.FromNormal(a0, nt));
            }
            var cfd = new FDesc { Surf = sph, Rev = !ch && sg > 0, Tag = ch ? "chamfer:corner" : "round:corner", Touched = true,
                                  Hint = ch ? (loop[0].A + loop[1].A + loop[2].A) / 3 : cr.C + dir * r };
            cfd.Loops.Add(loop);
            blends.Add(cfd);
        }

        // точки граней: прежняя, если не в срезанной полосе; иначе — у середины самого длинного звена внешнего контура
        foreach (var fd in faces.Where(x => x.Touched))
            fd.Hint = InteriorPoint(fd);
        foreach (var fd in blends.Where(x => x.Surf.IsPlanar && Scan))
            fd.Hint = ScanlinePoint(fd);

        Stage("assemble");
        var solid = Assemble(faces.Concat(blends).ToList(), ref why);
        if (solid == null) return null;
        if (Debug != null)
            foreach (var ed in solid.Faces.SelectMany(f => f.Loops).SelectMany(l => l.CoEdges).Select(c => c.Edge).Distinct())
                if (ed.CoEdges.Count == 2 && ed.CoEdges[0].Reversed == ed.CoEdges[1].Reversed)
                    Debug($"same-sense edge {ed.V0.P:0.###}->{ed.V1.P:0.###} {ed.Curve.GetType().Name} faces {ed.CoEdges[0].Loop?.Face?.Tag}/{ed.CoEdges[1].Loop?.Face?.Tag}");
        if (Debug != null) foreach (var f in solid.Faces.Where(x => x.Tag == "round:face")) Debug($"before normalize: loopsVec {AnalyticVolume.NormalIntegral(f):0.###} rev {f.Reversed}");
        var prevAD = TopologyConventions.AnchorDebug; if (Debug != null) TopologyConventions.AnchorDebug = m => Debug("anchor: " + m);
        Stage("normalize");
        var nrep = TopologyConventions.Normalize(solid);
        TopologyConventions.AnchorDebug = prevAD;
        Debug?.Invoke($"normalize: {nrep}");
        if (Debug != null) foreach (var f in solid.Faces.Where(x => x.Tag == "round:face")) Debug($"after normalize: loopsVec {AnalyticVolume.NormalIntegral(f):0.###} rev {f.Reversed}");
        var w = new List<string>();
        Stage("mesh");
        if (Analytic.PrismBuild.Finish(solid, w) > 0) { why = "faces not meshed: " + string.Join(" / ", w.Take(3)); return null; }
        solid.Guarantee = GuaranteeLevel.ExactAnalytic;
        // KRN-10 (g.13): проверки результата — сразу или, в предпросмотре, при принятии (BooleanOp.AcceptPreview)
        string? Checks()
        {
            if (BooleanOp.ForceAcceptFailure) return "forced acceptance failure (test)";
            Stage("validate");
            var v = SolidValidator.Validate(solid);
            if (!v.IsValid && Debug != null)
                foreach (var f in solid.Faces)
                {
                    var src = GeometryAudit.AuthoritativeSurface(f);
                    int agree = 0, dis = 0;
                    if (f.Surface is FacetedSurface fsd && src != null)
                        foreach (var (ta, tb, tc) in fsd.Facets.ReadView)
                        {
                            var cc = (ta + tb + tc) / 3; var n = src.NormalAt(src.ProjectToUV(cc)) * (f.Reversed ? -1 : 1);
                            if (Vector3d.Dot(Vector3d.Cross(tb - ta, tc - ta), n) >= 0) agree++; else { dis++; if (src is SectionSurface && dis <= 4) Debug($"   flipped facet uv {src.ProjectToUV(ta):G6} {src.ProjectToUV(tb):G6} {src.ProjectToUV(tc):G6} at {cc:0.###} area {Vector3d.Cross(tb - ta, tc - ta).Length / 2:G3}"); }
                        }
                    var uvs = f.Loops.SelectMany(l => l.CoEdges).SelectMany(ce => Enumerable.Range(0, 9).Select(k => src!.ProjectToUV(ce.Edge.Curve.PointAt(ce.Edge.Curve.T0 + (ce.Edge.Curve.T1 - ce.Edge.Curve.T0) * k / 8.0)))).ToList();
                    Debug($"face {f.Id} {src?.GetType().Name} rev {f.Reversed} tag {f.Tag} facets agree {agree} dis {dis} u {uvs.Min(q => q.X):G5}..{uvs.Max(q => q.X):G5} v {uvs.Min(q => q.Y):G5}..{uvs.Max(q => q.Y):G5}");
                }
            if (!v.IsValid) return "result invalid: " + v.Summary() + " " + string.Join(" / ", v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2));
            Stage("volume");
            if (AnalyticVolume.Of(solid) == null) return "no analytic volume";
            Stage("volfacets");
            if (BooleanOp.VolumeVsFacets(solid) is { } vf && !vf.Agree) return $"volume {vf.Va:G8} vs facets {vf.Vf:G8}";
            return null;
        }
        if (BooleanOp.PreviewMode) solid.PendingAcceptance = Checks;
        else if (Checks() is { } cw) { why = cw; return null; }
        Stage("tolerance");
        Analytic.EntityTolerance.Assign(solid);
        Stage("done");
        // производная многогранная форма — лениво, как у общего аналитического маршрута булевой (нужна многогранным
        // операциям и проверкам точной оболочки). Мутация P3D_DRLAZY=0.
        if (Prov)
        {
            // KRN-08 (08.2): происхождение — знание построителя: нетронутая грань — сохранена, правленая — изменена, лента и
            // угол — новые. Мутация P3D_DRPROVENANCE=0.
            var pv = new Provenance { Operation = ch ? "chamfer.direct" : "round.direct" };
            foreach (var fd in faces.Concat(blends))
                if (fd.Made != null && solid.Faces.Contains(fd.Made))
                    pv.Faces.Add(new FaceProvenance(fd.Made, fd.Orig, fd.Orig == null ? FaceOrigin.Generated : fd.Touched ? FaceOrigin.Modified : FaceOrigin.Kept, fd.Orig == null ? fd.Tag : null));
            solid.SetProvenance(pv);
        }
        if (Lazy)
            solid.LazyDerivedExact = (new Lazy<Exact.ExactBody>(() => Exact.ExactAdapter.ToExact(TopologyClone.Deep(solid), new Exact.PlaneRegistry(), null),
                LazyThreadSafetyMode.ExecutionAndPublication), solid.Revision);
        return solid;
    }

    /// <summary>
    /// Дуга сечения цилиндра (ось C + t·s, точка оси + r(e1 cos θ + e2 sin θ)) плоскостью через V с нормалью n, θ ∈ [θ1, θ1+Δθ].
    /// Сдвиг вдоль оси до плоскости даёт сопряжённые полудиаметры a = r(e1 − t(e1·n)/(t·n)), b = r(e2 − t(e2·n)/(t·n)); главные оси —
    /// поворотом на θ0: tg 2θ0 = 2a·b/(|a|² − |b|²). Окружность — когда плоскость поперечна оси.
    /// </summary>
    static ICurve3 Section(Vector3d C, Vector3d t, Vector3d e1, Vector3d e2, double r, Vector3d V, Vector3d n, double th1, double dth)
    {
        double tn = Vector3d.Dot(t, n);
        var O = C + t * (Vector3d.Dot(V - C, n) / tn);
        if (Math.Abs(Math.Abs(tn) - 1) < 1e-12) return new ArcCurve3(new Frame3(O, e1, e2), r, th1, th1 + dth);
        var a = (e1 - t * (Vector3d.Dot(e1, n) / tn)) * r; var b = (e2 - t * (Vector3d.Dot(e2, n) / tn)) * r;
        double th0 = PrincipalAxes ? 0.5 * Math.Atan2(2 * Vector3d.Dot(a, b), a.LengthSquared - b.LengthSquared) : 0;
        var A = a * Math.Cos(th0) + b * Math.Sin(th0); var B = -a * Math.Sin(th0) + b * Math.Cos(th0);
        return new EllipseCurve3(new Frame3(O, A, B), A.Length, B.Length, th1 - th0, th1 - th0 + dth);
    }

    static Vector3d? Solve3(Vector3d[] n, double[] d)
    {
        double det = Vector3d.Dot(n[0], Vector3d.Cross(n[1], n[2]));
        if (Math.Abs(det) < 1e-9) return null;
        return (Vector3d.Cross(n[1], n[2]) * d[0] + Vector3d.Cross(n[2], n[0]) * d[1] + Vector3d.Cross(n[0], n[1]) * d[2]) / det;
    }

    static bool InQuad(Vector3d[] q, Vector3d x, Vector3d n)
    {
        int sgn = 0;
        for (int i = 0; i < 4; i++)
        {
            double s = Vector3d.Dot(Vector3d.Cross(q[(i + 1) % 4] - q[i], x - q[i]), n);
            if (Math.Abs(s) < 1e-12) continue;
            int si = Math.Sign(s); if (sgn == 0) sgn = si; else if (si != sgn) return false;
        }
        return Math.Abs(Vector3d.Dot(x - q[0], n)) < 1e-6;
    }
    static bool OnBoundary(Vector3d[] q, Vector3d x)
    {
        for (int i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) % 4]; var ab = b - a; double u = Math.Clamp(Vector3d.Dot(x - a, ab) / ab.LengthSquared, 0, 1);
            if ((a + ab * u - x).Length < 1e-6) return true;
        }
        return false;
    }

    /// <summary>Точка внутри плоской грани: середина самого длинного звена внешнего контура, сдвинутая влево (материал слева).</summary>
    static Vector3d? InteriorPoint(FDesc fd)
    {
        if (!fd.Surf.IsPlanar) return fd.Hint;
        // точка внутри — сканирующей прямой по ломаной контуров: не зависит от направления обхода (у входа он бывает
        // обращён), прежний сдвиг «влево от длинного звена» попадал наружу (проверка «точка грани внутри», P3D_DRSCAN=0)
        if (Scan) return ScanlinePoint(fd);
        var n = fd.Surf.NormalAt(fd.Surf.ProjectToUV(fd.Loops[0][0].A)).Normalized();
        // нормаль по обходу внешнего контура (векторная площадь)
        var va = Vector3d.Zero; foreach (var s in fd.Loops[0]) va += Vector3d.Cross(s.A, s.B);
        if (va.Length > 1e-12) n = va.Normalized();
        Seg best = fd.Loops[0].OrderByDescending(s => (s.B - s.A).Length).First();
        var m = best.Curve.PointAt((best.Curve.T0 + best.Curve.T1) / 2); var tg = (best.B - best.A).Normalized();
        var left = Vector3d.Cross(n, tg);
        double minGap = fd.Loops.SelectMany(l => l).Where(s => s != best).Select(s => (s.Curve.PointAt((s.Curve.T0 + s.Curve.T1) / 2) - m).Length).DefaultIfEmpty(1).Min();
        double step = Math.Min(1e-3, 0.01 * Math.Max(1e-6, minGap));
        return m + left * step;
    }

    /// <summary>Наружная нормаль грани в точке (по поверхности и Reversed).</summary>
    static Vector3d NormalOf(FDesc fd, Vector3d p) => (fd.Surf.NormalAt(fd.Surf.ProjectToUV(p)) * (fd.Rev ? -1 : 1)).Normalized();

    /// <summary>Доля параметра точки q на дуге (0 — начало кривой, 1 — конец); null — не на окружности дуги.</summary>
    static double? ArcParam(ArcCurve3 a, Vector3d q)
    {
        var d = q - a.Frame.Origin; var W = a.Frame.W.Normalized();
        if (Math.Abs(Vector3d.Dot(d, W)) > 1e-7 || Math.Abs(d.Length - a.Radius) > 1e-7 * Math.Max(1, a.Radius)) return null;
        double th = Math.Atan2(Vector3d.Dot(d, a.Frame.V.Normalized()), Vector3d.Dot(d, a.Frame.U.Normalized()));
        double tau = (th - a.A0) / a.Sweep, per = 2 * Math.PI / Math.Abs(a.Sweep);
        while (tau < -1e-9) tau += per; while (tau > 1 + 1e-9) tau -= per;
        return tau;
    }

    /// <summary>Звено с новым началом и/или концом: отрезок — новый отрезок, дуга — та же окружность с суженным диапазоном.</summary>
    static Seg? Shorten(Seg s, Vector3d? newA, Vector3d? newB)
    {
        var n = s.Clone(); n.Orig = null;
        if (newA is { } na) n.A = na; if (newB is { } nb) n.B = nb;
        switch (s.Curve)
        {
            case LineCurve3: n.Curve = new LineCurve3(n.A, n.B); return n;
            case ArcCurve3 a when !s.Closed:
            {
                double? ta = ArcParam(a, n.A), tb = ArcParam(a, n.B);
                if (ta == null || tb == null) return null;
                double lo = Math.Min(ta.Value, tb.Value), hi = Math.Max(ta.Value, tb.Value);
                if (hi - lo < 1e-12) return null;
                n.Curve = new ArcCurve3(a.Frame, a.Radius, a.A0 + a.Sweep * lo, a.A0 + a.Sweep * hi);
                return n;
            }
            default: return null;
        }
    }

    /// <summary>Направление ±d от точки ребра m внутрь плоской грани (чётность пересечений по её контурам); null — не решено.</summary>
    static Vector3d? IntoPlanarFace(FDesc fd, Vector3d m, Vector3d d)
    {
        var fr = Frame3.FromNormal(m, fd.N.Length > 0.5 ? fd.N : fd.Surf.NormalAt(fd.Surf.ProjectToUV(m)).Normalized());
        var polys = new List<List<Vector2d>>(); double size = 0;
        foreach (var l in fd.Loops)
        {
            var poly = new List<Vector2d>();
            foreach (var sg in l)
            {
                bool fw = sg.Closed || (sg.Curve.PointAt(sg.Curve.T0) - sg.A).Length <= (sg.Curve.PointAt(sg.Curve.T0) - sg.B).Length;
                int n = sg.Curve is LineCurve3 ? 1 : 64;
                for (int k = 0; k < n; k++)
                {
                    double tk = fw ? (double)k / n : 1 - (double)k / n;
                    var q = sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * tk) - m;
                    poly.Add(new Vector2d(Vector3d.Dot(q, fr.U), Vector3d.Dot(q, fr.V))); size = Math.Max(size, q.Length);
                }
            }
            polys.Add(poly);
        }
        bool Inside(Vector2d x)
        {
            bool ins = false;
            foreach (var poly in polys)
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                    if ((poly[i].Y > x.Y) != (poly[j].Y > x.Y) && x.X < (poly[j].X - poly[i].X) * (x.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) ins = !ins;
            return ins;
        }
        double eps = Math.Max(1e-6, 1e-4 * size);
        var d2 = new Vector2d(Vector3d.Dot(d, fr.U), Vector3d.Dot(d, fr.V));
        bool plus = Inside(d2 * eps), minus = Inside(-d2 * eps);
        if (plus == minus) return null;
        return plus ? d : -d;
    }

    /// <summary>Точка внутри плоской грани с кривыми контурами: горизонталь через середину габарита, чётность пересечений,
    /// середина самого широкого промежутка «внутри».</summary>
    static Vector3d? ScanlinePoint(FDesc fd)
    {
        var p0 = fd.Loops[0][0].A; var fr = Frame3.FromNormal(p0, fd.Surf.NormalAt(fd.Surf.ProjectToUV(p0)).Normalized());
        var polys = new List<List<Vector2d>>();
        foreach (var l in fd.Loops)
        {
            var poly = new List<Vector2d>();
            foreach (var sg in l)
                for (int k = 0; k < 64; k++)
                {
                    // по обходу звена (кривая может идти навстречу), иначе ломаная зигзагом
                    bool fw = sg.Closed || (sg.Curve.PointAt(sg.Curve.T0) - sg.A).Length <= (sg.Curve.PointAt(sg.Curve.T0) - sg.B).Length;
                    double tk = fw ? k / 64.0 : 1 - k / 64.0;
                    var q = sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * tk) - p0;
                    poly.Add(new Vector2d(Vector3d.Dot(q, fr.U), Vector3d.Dot(q, fr.V)));
                }
            polys.Add(poly);
        }
        double y0 = polys.SelectMany(x => x).Min(v => v.Y), y1 = polys.SelectMany(x => x).Max(v => v.Y);
        double y = y0 + (y1 - y0) * 0.5137;                       // не точно по середине: мимо вершин ломаной
        var xs = new List<double>();
        foreach (var poly in polys)
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                if ((a.Y > y) != (b.Y > y)) xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
        xs.Sort();
        double best = -1, bx = 0;
        for (int i = 0; i + 1 < xs.Count; i += 2) if (xs[i + 1] - xs[i] > best) { best = xs[i + 1] - xs[i]; bx = (xs[i] + xs[i + 1]) / 2; }
        if (best <= 0) return fd.Hint;
        return p0 + fr.U * bx + fr.V * y;
    }

    /// <summary>
    /// KRN-06 (06.11): СЕМЕЙСТВО ПЛОСКОСТЬ/ЦИЛИНДР — круговое ребро между плоскостью и прямым круговым цилиндром, ось
    /// которого перпендикулярна плоскости (кромка отверстия, бобышки, основание бобышки). Ось шара — окружность: центр на оси
    /// цилиндра на расстоянии σr от плоскости, радиус ρ = R + σ·s·r (s = +1 — материал внутри цилиндра, бобышка; −1 —
    /// отверстие). Контакты — окружность радиуса ρ на плоскости и окружность радиуса R на цилиндре в σr от плоскости. Лента —
    /// тор (скругление: дуга радиуса r в меридиане, повёрнутая вокруг оси) или конус (фаска: отрезок). Мутация P3D_DRCIRCLES=0.
    /// </summary>
    static bool CircularBlends(List<TEdge> circs, Dictionary<TFace, FDesc> byFace, List<FDesc> blends, double r, bool ch, ref string? why)
    {
        // окружность ребра может быть одним замкнутым ребром или цепочкой дуг (отверстие операции «отверстие» — четверти):
        // группа — дуги одной окружности между одной парой граней
        var groups = new List<(FDesc Fp, FDesc Fc, Vector3d O, double R, List<TEdge> Arcs)>();
        foreach (var e in circs)
        {
            var arc = (ArcCurve3)e.Curve;
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count != 2) { why = "circle is not between two faces"; return false; }
            var Fp0 = fs.Select(f => byFace[f]).FirstOrDefault(d => d.Surf.IsPlanar);
            var Fc0 = fs.Select(f => byFace[f]).FirstOrDefault(d => d != Fp0);
            if (Fp0 == null || Fc0 == null) { why = "circle: no plane face"; return false; }
            var g = groups.FindIndex(x => x.Fp == Fp0 && x.Fc == Fc0 && (x.O - arc.Frame.Origin).Length < 1e-9 && Math.Abs(x.R - arc.Radius) < 1e-9);
            if (g < 0) groups.Add((Fp0, Fc0, arc.Frame.Origin, arc.Radius, new List<TEdge> { e })); else groups[g].Arcs.Add(e);
        }
        foreach (var (Fp, Fc, O, Rg, arcs) in groups)
        {
            var arc = (ArcCurve3)arcs[0].Curve;
            Vector3d ax, aO; double R;
            switch (Fc.Surf)
            {
                case ExtrudedSurface { Base: ArcCurve3 ba } ex when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), ex.Dir.Normalized())) - 1) < 1e-12:
                    ax = ex.Dir.Normalized(); aO = ba.Frame.Origin; R = ba.Radius; break;
                case CylinderSurface cy: ax = cy.Axis.W.Normalized(); aO = cy.Axis.Origin; R = cy.Radius; break;
                default: why = "circle: the other face is not a right circular cylinder"; return false;
            }
            var np = Fp.N; if (np.Length < 0.5) { why = "circle: plane normal undefined"; return false; }
            if (Math.Abs(Math.Abs(Vector3d.Dot(np, ax)) - 1) > 1e-12) { why = "circle: cylinder axis not perpendicular to the plane"; return false; }
            var dO = O - aO; if ((dO - ax * Vector3d.Dot(dO, ax)).Length > 1e-9 * Math.Max(1, R) || Math.Abs(Rg - R) > 1e-9 * Math.Max(1, R)) { why = "circle: edge is not the cylinder's section"; return false; }
            if (Math.Abs(arcs.Sum(x => Math.Abs(((ArcCurve3)x.Curve).Sweep)) - 2 * Math.PI) > 1e-9) { why = "circle: the selected arcs are not the whole circle"; return false; }
            // контуры: у плоскости и у цилиндра окружность — отдельный контур только из этих дуг; цилиндр — пояс из двух окружностей
            bool Whole(List<Seg> l) => l.Count == arcs.Count && l.All(sg => sg.Id != null && arcs.Contains(sg.Id));
            var lp = Fp.Loops.FirstOrDefault(Whole); var lc = Fc.Loops.FirstOrDefault(Whole);
            if (lp == null || lc == null) { why = "circle: not a loop of its own"; return false; }
            if (Fc.Loops.Count != 2) { why = "circle: cylinder face is not a band between two circles"; return false; }
            var otherLoop = Fc.Loops.First(l => l != lc);
            if (otherLoop.Any(sg => sg.Curve is not ArcCurve3)) { why = "circle: cylinder's other edge is not a circle"; return false; }
            var oa = (ArcCurve3)otherLoop[0].Curve;
            // сторона материала цилиндра (s) — по наружной нормали грани в точке ребра
            var p = arc.PointAt(arc.T0); var radial = p - O; radial -= ax * Vector3d.Dot(radial, ax); radial.Normalize();
            var nc = Fc.Surf.NormalAt(Fc.Surf.ProjectToUV(p)) * (Fc.Rev ? -1 : 1);
            double sC = Vector3d.Dot(nc, radial) > 0 ? 1 : -1;
            // окружность — отверстие плоской грани (грань идёт от неё наружу) или её внешняя граница
            var fr = Frame3.FromNormal(O, np);
            Vector2d P2(Vector3d q) => new(Vector3d.Dot(q - O, fr.U), Vector3d.Dot(q - O, fr.V));
            int around = 0;
            foreach (var l in Fp.Loops.Where(l => l != lp))
            {
                var poly = l.SelectMany(sg => Enumerable.Range(0, 32).Select(k => P2(sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * k / 32.0)))).ToList();
                bool ins = false;
                for (int i2 = 0, j2 = poly.Count - 1; i2 < poly.Count; j2 = i2++)
                    if ((poly[i2].Y > 0) != (poly[j2].Y > 0) && 0 < (poly[j2].X - poly[i2].X) * (0 - poly[i2].Y) / (poly[j2].Y - poly[i2].Y) + poly[i2].X) ins = !ins;
                if (ins) around++;
            }
            double aP = around % 2 == 1 ? 1 : -1;                // +1: плоская грань уходит от окружности наружу
            double sigma = aP * sC < 0 ? -1 : 1;                 // −1 выпуклое
            // KRN-06 (06.15): несимметричная фаска окружности — свой размер на плоскости (радиальный сдвиг) и на цилиндре (осевой)
            double dP = r, dC = r;
            if (ch && _chamfer is { } cmc && !UnequalCircleOff)
            {
                if (!cmc.First.TryGetValue(arcs[0], out var f1st)) { why = "unequal chamfer: the first-size face is unknown"; return false; }
                dP = ReferenceEquals(Fp.Orig, f1st) ? cmc.D1 : cmc.D2; dC = ReferenceEquals(Fc.Orig, f1st) ? cmc.D1 : cmc.D2;
            }
            else if (ch && _chamfer != null) { why = "unequal chamfer: circles off"; return false; }
            double rho = R + sigma * sC * dP;
            if (!(rho > 1e-6)) { why = "circle: radius too large for the boss"; return false; }
            // место на плоскости: другие контуры не ближе к оси, чем новая окружность (снаружи) / дальше (внутри)
            foreach (var l in Fp.Loops.Where(l => l != lp))
                foreach (var sg in l)
                    for (int k = 0; k < 32; k++)
                    {
                        double rq = P2(sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * k / 32.0)).Length;
                        if (aP > 0 ? rq <= rho + 1e-7 : rq >= rho - 1e-7) { why = "circle: another loop of the plane face in the blend strip"; return false; }
                    }
            // место на цилиндре: другая окружность дальше r вдоль оси в сторону σ·n_p
            var shift = np * (sigma * dC);
            if (Vector3d.Dot(oa.Frame.Origin - O, np * sigma) <= dC + 1e-7) { why = "circle: cylinder face shorter than the blend"; return false; }
            // новые дуги — те же угловые пределы и рамки, другой радиус (плоскость) и сдвиг вдоль оси (цилиндр); звено идёт
            // в ту же сторону, что прежнее
            Seg Moved(Seg sg, Vector3d centre, double rad)
            {
                var a = (ArcCurve3)sg.Curve;
                var na = new ArcCurve3(new Frame3(centre, a.Frame.U, a.Frame.V), rad, a.A0, a.A1);
                bool fw = sg.Closed || (a.PointAt(a.T0) - sg.A).Length <= (a.PointAt(a.T0) - sg.B).Length;
                return new Seg { Curve = na, A = fw ? na.PointAt(na.T0) : na.PointAt(na.T1), B = fw ? na.PointAt(na.T1) : na.PointAt(na.T0) };
            }
            var newP = lp.Select(sg => Moved(sg, O, rho)).ToList();
            var newC = lc.Select(sg => Moved(sg, O + shift, R)).ToList();
            lp.Clear(); lp.AddRange(newP); lc.Clear(); lc.AddRange(newC);
            Fp.Touched = true;
            Fc.Hint = (O + shift + oa.Frame.Origin) / 2 + radial * R;   // середина пояса цилиндра
            // лента: тор (дуга в меридиане) или конус (отрезок), вращением вокруг оси
            var Ct = O + shift; var U = radial; var Wm = np;           // меридиан — плоскость (U, n_p)
            var tube = Ct + U * rho;
            var qP = O + U * rho; var qC = O + shift + U * R;
            ICurve3 gen; Vector3d mid, want;
            if (ch)
            {
                gen = new LineCurve3(qP, qC); mid = (qP + qC) / 2;
                want = (np + nc).Normalized();                      // наружу — в сторону суммы нормалей граней
            }
            else
            {
                var mf = new Frame3(tube, U, Wm);                     // X = U, Y = n_p
                double a0 = Math.Atan2(Vector3d.Dot(qP - tube, Wm), Vector3d.Dot(qP - tube, U));
                double a1 = Math.Atan2(Vector3d.Dot(qC - tube, Wm), Vector3d.Dot(qC - tube, U));
                double da = a1 - a0; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
                gen = new ArcCurve3(mf, r, a0, a0 + da);
                if ((gen.PointAt(gen.T0) - qP).Length > 1e-9 || (gen.PointAt(gen.T1) - qC).Length > 1e-9) { why = "circle: torus generator does not meet the contacts"; return false; }
                mid = gen.PointAt(0.5);
                want = (mid - tube).Normalized() * (-sigma);           // выпуклое — от оси трубки, вогнутое — к ней
            }
            var rs = new RevolvedSurface(gen, new Frame3(Ct, fr.U, fr.V), 0, 2 * Math.PI);
            var nS = rs.NormalAt(rs.ProjectToUV(mid));
            var bl = new FDesc { Surf = rs, Rev = Vector3d.Dot(nS, want) < 0, Tag = ch ? "chamfer:face" : "round:face", Touched = true, Hint = mid };
            // контуры ленты: те же дуги, обход навстречу (направление окончательно выставит нормализация)
            static List<Seg> Back(List<Seg> l) => Enumerable.Reverse(l).Select(sg => new Seg { Curve = sg.Curve, A = sg.B, B = sg.A }).ToList();
            bl.Loops.Add(Back(newP)); bl.Loops.Add(Back(newC));
            blends.Add(bl);
        }
        return true;
    }

    /// <summary>Описание граней тела для прямых построителей (точная поверхность, сторона, контуры; плоскость — нормаль, цилиндр — ось).</summary>
    static List<FDesc>? Describe(TSolid body, out Dictionary<TFace, FDesc> byFace, ref string? why)
    {
        var faces = new List<FDesc>();
        byFace = new Dictionary<TFace, FDesc>();
        foreach (var f in body.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src == null) { why = "face without an exact surface"; return null; }
            // точная поверхность-источник может быть ориентирована иначе, чем фасетная грань (копия с поворотом):
            // сторона — по нормали самой грани в точке контура
            bool rev = f.Reversed;
            if (SourceSide && !ReferenceEquals(src, f.Surface) && f.Loops.FirstOrDefault()?.CoEdges.FirstOrDefault()?.Start.P is Vector3d sp
                && Vector3d.Dot(src.NormalAt(src.ProjectToUV(sp)) * (f.Reversed ? -1 : 1), f.NormalAt(sp)) < 0)
                rev = !rev;
            var d = new FDesc { Surf = src, Rev = rev, Tag = f.Tag, Hint = f.InteriorHint, Slot = f.MaterialSlot, Orig = f };
            foreach (var l in f.Loops)
            {
                var ord = l.Ordered();
                if (ord == null) { why = "loop not closed"; return null; }
                d.Loops.Add(ord.Select(ce => new Seg { Curve = ce.Edge.Curve, A = ce.Start.P, B = ce.End.P, Orig = ce.Edge, Id = ce.Edge }).ToList());
            }
            // НОРМАЛЬ ПЛОСКОЙ ГРАНИ — по поверхности и Reversed, не по обходу: контуры входа могут быть обращены (у выдавливания
            // нижняя грань обходится против своей нормали — соглашение выставит нормализация позже); прежде нормаль из обхода
            // давала «выпуклое и вогнутое» в углах нижней грани. Мутация P3D_DRTRUENORMAL=0 — нормаль по обходу.
            if (src.IsPlanar && f.Loops.FirstOrDefault()?.CoEdges.FirstOrDefault()?.Start.P is Vector3d fp)
                d.N = TrueNormal ? (src.NormalAt(src.ProjectToUV(fp)) * (rev ? -1 : 1)).Normalized() : AnalyticVolume.NormalIntegral(f).Normalized();
            switch (src)
            {
                case ExtrudedSurface { Base: ArcCurve3 ba } exc when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), exc.Dir.Normalized())) - 1) < 1e-12:
                    d.IsCyl = true; d.AxO = ba.Frame.Origin; d.AxW = exc.Dir.Normalized(); d.AxR = ba.Radius; break;
                case CylinderSurface cys:
                    d.IsCyl = true; d.AxO = cys.Axis.Origin; d.AxW = cys.Axis.W.Normalized(); d.AxR = cys.Radius; break;
            }
            faces.Add(d); byFace[f] = d;
        }

        return faces;
    }

    static TSolid? Assemble(List<FDesc> faces, ref string? why)
    {
        var solid = new TSolid(); var shell = new TShell { Solid = solid }; solid.Shells.Add(shell);
        var verts = new List<TVertex>();
        TVertex Vert(Vector3d p) { foreach (var v in verts) if ((v.P - p).Length < 1e-7) return v; var nv = new TVertex(p); verts.Add(nv); return nv; }
        var edges = new List<(TEdge E, Vector3d Mid)>();
        var origMap = new Dictionary<TEdge, TEdge>();
        foreach (var fd in faces)
        {
            var face = new TFace { Surface = fd.Surf, Reversed = fd.Rev, Tag = fd.Tag, InteriorHint = fd.Hint, MaterialSlot = fd.Slot };
            fd.Made = face;
            if (fd.Orig != null) face.Attr = fd.Orig.Attr;
            bool first = true;
            foreach (var loop in fd.Loops)
            {
                var tl = face.AddLoop(first); first = false;
                foreach (var s in loop)
                {
                    var va = Vert(s.A); var vb = Vert(s.B);
                    var mid = s.Curve.PointAt((s.Curve.T0 + s.Curve.T1) / 2);
                    TEdge? found = null; bool rev = false;
                    if (s.Closed)
                    {
                        // замкнутая окружность: одна вершина, направление — признаком обхода (по концам его не узнать)
                        foreach (var (E, M) in edges)
                            if (E.V0 == va && E.V1 == va && (ReferenceEquals(E.Curve, s.Curve) || (M - mid).Length <= 1e-7)) { found = E; break; }
                        if (found == null)
                        {
                            found = new TEdge { V0 = va, V1 = va, Curve = s.Curve };
                            if (s.Orig != null) { found.Tolerance = s.Orig.Tolerance; found.Tag = s.Orig.Tag; found.Attr = s.Orig.Attr; }
                            va.Edges.Add(found); edges.Add((found, mid));
                        }
                        tl.Add(found, false);   // направление выставит нормализация соглашения
                        continue;
                    }
                    foreach (var (E, M) in edges)
                    {
                        if ((M - mid).Length > 1e-7) continue;
                        if (E.V0 == va && E.V1 == vb) { found = E; rev = false; break; }
                        if (E.V0 == vb && E.V1 == va) { found = E; rev = true; break; }
                    }
                    if (found == null)
                    {
                        // кривая — по направлению сегмента (исходное ребро может идти навстречу)
                        var cv = s.Curve; var c0 = cv.PointAt(cv.T0);
                        bool curveFwd = (c0 - s.A).Length <= (c0 - s.B).Length;
                        found = new TEdge { V0 = curveFwd ? va : vb, V1 = curveFwd ? vb : va, Curve = cv };
                        if (s.Orig != null) { found.Tolerance = s.Orig.Tolerance; found.Tag = s.Orig.Tag; found.Attr = s.Orig.Attr; }
                        va.Edges.Add(found); if (vb != va) vb.Edges.Add(found);
                        edges.Add((found, mid));
                        rev = !curveFwd;
                    }
                    tl.Add(found, rev);
                }
            }
            shell.Add(face);
        }
        // каждое ребро — ровно у двух полурёбер
        foreach (var (E, _) in edges) if (E.CoEdges.Count != 2) { why = $"edge with {E.CoEdges.Count} uses after assembly"; return null; }
        return solid;
    }
}
