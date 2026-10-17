using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// Булевы операции над телами: объединение и вычитание.
///
/// КАК УСТРОЕНО И ЧЕМ ЗА ЭТО ПЛАТИМ.
///
/// Тела разбиваются на плоские многоугольники, операция выполняется
/// двоичным разбиением пространства (Csg), результат собирается обратно
/// в тело. Точное пересечение аналитических поверхностей не делается:
/// это отдельная подсистема, которую невозможно написать без обширной
/// проверки на реальных моделях.
///
/// Что сохраняется:
///   * форма — верна, проверено по объёмам на двенадцати расположениях
///     тел, включая соприкосновение гранями и сквозной паз;
///   * принадлежность граням — каждый кусок помнит исходную грань,
///     поэтому грани результата не сливаются в одну и выбираются мышью;
///   * аналитическая поверхность НЕРАЗРЕЗАННОЙ грани — плоскую стенку
///     после выреза по-прежнему можно взять как плоскость-базис эскиза.
///
/// Что теряется:
///   * кривые поверхности после операции становятся гранёными;
///   * рёбра восстанавливаются по стыкам граней отрезками, а не кривыми,
///     поэтому скругление по такому ребру пока не построить;
///   * прямое ребро может выйти разбитым на несколько отрезков подряд.
///     Слияние коллинеарных кусков я пробовал и убрал: на проверке оно
///     съедало часть настоящих рёбер, а лишний стык на прямой линии
///     не виден, тогда как пропавшее ребро видно сразу.
/// </summary>
public static class BooleanOp
{
    /// <summary>Допуск склейки совпадающих вершин результата, единиц модели.</summary>
    /// <summary>KRN-02: бюджет сшивки (ToleranceContext.Sewing, по умолчанию 1e-6 мм).</summary>
    private static double WeldTol => ToleranceContext.Current.Sewing;

    /// <summary>
    /// СВЕРЯТЬ ЛИ КАЖДУЮ ОПЕРАЦИЮ С ОБЪЁМНОЙ МОДЕЛЬЮ.
    ///
    /// По умолчанию выключено, и это не осторожность, а цена: сверка строит
    /// три объёмные модели — оба операнда и результат — и стоит дороже
    /// самой операции. Включается на время разбора, когда есть подозрение,
    /// что булева врёт.
    ///
    /// Результат попадает в предупреждения детали: там его видно рядом
    /// с операцией, к которой он относится, а не отдельным журналом,
    /// который никто не открывает.
    /// </summary>
    private static bool _gAudit;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool Audit
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.Audit : _gAudit;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gAudit, value, "BooleanOp.Audit");
    }

    /// <summary>Разрешение сверки. Каждая единица — восьмикратная цена.</summary>
    private static int _gAuditDepth = Kernel.Voxels.Occupancy.DefaultDepth;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static int AuditDepth
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.AuditDepth : _gAuditDepth;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gAuditDepth, value, "BooleanOp.AuditDepth");
    }

    /// <summary>
    /// ВЕСТИ БУЛЕВУ ТОЧНЫМ ЯДРОМ.
    ///
    /// Точное ядро (Kernel/Exact) решает всё знаком целого числа, без допусков,
    /// и результат замкнут по построению. Прежнее (Csg) остаётся запасным путём:
    /// если точное откажет — например, на входе, который нельзя замкнуть, —
    /// операция уходит в прежнее с предупреждением, а не падает.
    /// </summary>
    private static bool _gUseExactKernel = true;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool UseExactKernel
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.UseExactKernel : _gUseExactKernel;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gUseExactKernel, value, "BooleanOp.UseExactKernel");
    }

    /// <summary>ДИАГНОСТИКА: длинные цепочки при сборке рёбер.</summary>
    public static Action<string>? ChainDebug;



    /// <summary>
    /// КРИВАЯ РЕБРА ПОДГОНЯЕТСЯ ПОД ЕГО ВЕРШИНЫ. Дуга, подобранная по цепочке хорд, и прямая,
    /// восстановленная по отрезкам, кончаются не там, где стоят вершины: измерено расхождение до
    /// 0.02 мм — в двести раз больше допуска склейки. От этого расходятся характеристики тела и
    /// его сетки, а гранение по такой кривой отходит от вершин.
    ///
    /// Вызывается в КОНЦЕ операции: при сборке тела расхождения ещё нет (проверено — подгонка
    /// срабатывала ноль раз), оно появляется при восстановлении дуг из цепочек хорд.
    /// </summary>
    /// <summary>
    /// ИЗМЕРИТЬ СОБСТВЕННЫЕ ДОПУСКИ элементов тела: у каждого ребра — расхождение его кривой с
    /// вершинами, у вершины — наибольшее из её рёбер. Так проверки могут спрашивать элемент, а
    /// не общий допуск модели: в одном месте тела геометрия сходится до микрона, в другом лишь
    /// до сотых.
    /// </summary>
    public static void MeasureTolerances(TSolid solid)
    {
        foreach (var e in solid.Edges)
        {
            double t = e.MeasureTolerance();
            if (e.V0 != null) e.V0.Tolerance = Math.Max(e.V0.Tolerance, t);
            if (e.V1 != null) e.V1.Tolerance = Math.Max(e.V1.Tolerance, t);
        }
    }

    public static int SnapEdgeCurves(TSolid solid)
    {
        solid.BeginEdit("BooleanOp.SnapEdgeCurves", EditKind.Derive);
        int snapped = 0;

        // ПЕРЕНОС ВЕРШИН НА ДУГУ — ПРОБОВАЛ, ОТКАТИЛ. Окружность подбирается по ВНУТРЕННИМ
        // точкам цепочки хорд намеренно: концы цепочки лежат на хордах и отстоят от окружности
        // на долю прогиба (измерено 0.013–0.020 мм). Казалось, что неверна вершина, и её надо
        // поставить на окружность: расхождение падало (у одного тела с 7 рёбер до нуля, у
        // другого с 11 до 6). Но вершина лежит и на СОСЕДНЕЙ ПЛОСКОЙ грани, и перенос снимал её
        // оттуда — проверка «ребро на плоскости x = −50» перестала находить ребро. Вершина
        // обязана лежать на ВСЕХ смежных поверхностях сразу, поэтому её надо не проецировать,
        // а вычислять как их общую точку. Это и есть настоящая задача.

        foreach (var e in solid.Edges)
        {
            Vector3d a = e.Curve.PointAt(e.Curve.T0), b = e.Curve.PointAt(e.Curve.T1);
            if ((a - e.V0.P).Length <= Core.Tol.Weld && (b - e.V1.P).Length <= Core.Tol.Weld) continue;

            switch (e.Curve)
            {
                case LineCurve3:
                    e.Curve = new LineCurve3(e.V0.P, e.V1.P);
                    snapped++;
                    break;

                case ArcCurve3 arc:
                {
                    // ДУГА ПЕРЕСТРАИВАЕТСЯ ПО ТРЁМ ТОЧКАМ: обеим вершинам и середине прежней.
                    // Одной правки углов мало — вершина лежит ВНЕ прежней окружности (измерено
                    // расхождение до 0.013 мм), и подгонка только углов его не убирала.
                    if ((e.V0.P - e.V1.P).Length <= Core.Tol.Weld) break;      // замкнутую не трогаем

                    Vector3d mid = arc.PointAt((arc.T0 + arc.T1) / 2);
                    var fitted = FitArc(new List<Vector3d> { e.V0.P, mid, e.V1.P });
                    if (fitted is ArcCurve3 na)
                    {
                        double d0 = (na.PointAt(na.T0) - e.V0.P).Length;
                        double d1 = (na.PointAt(na.T1) - e.V1.P).Length;
                        if (d0 <= Core.Tol.Weld && d1 <= Core.Tol.Weld)
                        {
                            e.Curve = na;
                            snapped++;
                        }
                    }
                    break;
                }
            }
        }
        // K03: видимая сетка зависит от кривых рёбер — подогнанная кривая делает её устаревшей.
        if (snapped > 0) solid.DisplayMeshKey = null;
        return snapped;
    }

    /// <summary>
    /// Уровень гарантии тела, не прошедшего через булеву: его собственный, если задан; иначе — по геометрии: все грани на
    /// точных негранёных поверхностях — ExactAnalytic; есть гранёная грань без точного источника — неизвестно (Unspecified).
    /// </summary>
    internal static GuaranteeLevel GuaranteeOf(TSolid body)
    {
        if (body.Guarantee != GuaranteeLevel.Unspecified) return body.Guarantee;
        bool any = false;
        foreach (var f in body.Faces)
        {
            var s = Topology.GeometryAudit.AuthoritativeSurface(f);
            if (s == null || s is FacetedSurface) return GuaranteeLevel.Unspecified;
            any = true;
        }
        return any ? GuaranteeLevel.ExactAnalytic : GuaranteeLevel.Unspecified;
    }

    private static void UniteCore(List<TSolid> bodies, TSolid addition, List<string> warnings)
    {
        // Объединение с пустым набором — само тело. KRN-01: уровень гарантии не теряется (прежде — «не указан», 142 из
        // 1733 булевых allB): уровень самого тела, а у тела без уровня — по его геометрии.
        if (bodies.Count == 0) { bodies.Add(addition); OperationTrace.Route(GuaranteeOf(addition)); return; }

        var inputs = new HashSet<TSolid>(bodies, ReferenceEqualityComparer.Instance) { addition };
        Apply(bodies, addition, warnings, subtract: false);
        // K03: подгонка кривых и допусков — только у новых тел. Прочие тела списка и операнд
        // опубликованы и не правятся на месте.
        foreach (var b in bodies)
        {
            if (inputs.Contains(b)) continue;
            if (SnapVertices) VertexSnap.Apply(b, warnings);
            SnapEdgeCurves(b);
            Topology.AnalyticEdges.Repair(b, warnings);
            MeasureTolerances(b);
        }
    }

    /// <summary>Последнее тело, отвергнутое проверкой результата (только для разбора отказа).</summary>
    [ThreadStatic] public static TSolid? LastInvalidResult;

    /// <summary>Вид булевой операции для типизированного вызова.</summary>
    public enum BooleanKind { Unite, Subtract, Intersect }

    // ================================================================ K02: контракт операции
    //
    // Прежние void-вызовы сохранены ради 26 мест использования, но идут через Evaluate: отказ
    // по политике становится исключением OperationFailedException (дерево построения ловит его
    // и откатывает операцию), а список тел вызывающего меняется только при успехе.

    public static void Unite(List<TSolid> bodies, TSolid addition, List<string> warnings) =>
        Commit(Evaluate(bodies, addition, BooleanKind.Unite, OperationPolicyScope.Current, warnings), bodies);

    public static void Subtract(List<TSolid> bodies, TSolid tool, List<string> warnings) =>
        Commit(Evaluate(bodies, tool, BooleanKind.Subtract, OperationPolicyScope.Current, warnings), bodies);

    public static void Intersect(List<TSolid> bodies, TSolid other, List<string> warnings) =>
        Commit(Evaluate(bodies, other, BooleanKind.Intersect, OperationPolicyScope.Current, warnings), bodies);

    private static void Commit(OperationResult r, List<TSolid> bodies)
    {
        if (r.Status == OperationStatus.Failed) throw new OperationFailedException(r);
        if (bodies.Count == r.Bodies.Count && bodies.Zip(r.Bodies).All(p => ReferenceEquals(p.First, p.Second))) return;
        var keep = r.Bodies.ToList();
        bodies.Clear();
        bodies.AddRange(keep);
    }

    /// <summary>
    /// БУЛЕВА С ТИПИЗИРОВАННЫМ РЕЗУЛЬТАТОМ. Вход не изменяется: операция работает с копией
    /// списка тел, а при отказе возвращает пустой результат со статусом, этапом и причиной.
    ///
    /// Строгая политика отказывает, если точный маршрут подменён приближённым, если вход
    /// приближённый, если грань входа пропала при гранении, если любой этап бросил исключение
    /// и если тело результата не проходит проверку B-rep. Приближённая политика принимает
    /// подмену маршрута, но помечает тела GuaranteeLevel.Approximate; потерю грани, исключение и
    /// негодную топологию она не принимает: это не приближение, а брак.
    /// </summary>
    private static bool _gAnalyticPrism = Environment.GetEnvironmentVariable("P3D_ANALYTIC") != "0";
    /// <summary>
    /// Аналитический маршрут булевой для 2.5-мерных тел (Kernel/Analytic). ВКЛЮЧЁН ПО УМОЛЧАНИЮ (r34): весь корпус, FProbe и
    /// A06 проходят с ним; он точнее (точные поверхности) и быстрее многогранного пути (logs/perf_ab.txt, r33).
    /// Многогранный путь — запасной с явным контрактом. Значение контекста (KernelSettings.AnalyticPrism);
    /// P3D_ANALYTIC=0 — прежнее поведение.
    /// </summary>
    public static bool AnalyticPrism
    {
        get => KernelContext.Current is { } kc ? kc.Settings.AnalyticPrism : _gAnalyticPrism;
        set => KernelContext.SetGlobal(ref _gAnalyticPrism, value, "BooleanOp.AnalyticPrism");
    }

    private static GeometryPolicy _gGeometryPolicy = Environment.GetEnvironmentVariable("P3D_GEOMETRY") == "preserve" ? GeometryPolicy.PreserveBRep : GeometryPolicy.AllowBoundedFaceting;
    /// <summary>
    /// KRN-01: политика геометрии по умолчанию — AllowBoundedFaceting (прежнее поведение; понижение записывается в
    /// диагностику); P3D_GEOMETRY=preserve — PreserveBRep. Значение контекста.
    /// </summary>
    public static GeometryPolicy GeometryPolicy
    {
        get => KernelContext.Current is { } kc ? kc.Settings.GeometryPolicy : _gGeometryPolicy;
        set => KernelContext.SetGlobal(ref _gGeometryPolicy, value, "BooleanOp.GeometryPolicy");
    }

    private static bool _gAnalyticGeneral = Environment.GetEnvironmentVariable("P3D_ANALYTICGENERAL") != "0";
    /// <summary>
    /// Общая аналитическая булева (этап 2: плоскости и цилиндры любых направлений) — после 2.5-мерной, перед многогранной.
    /// ВКЛЮЧЕНА ПО УМОЛЧАНИЮ (r44): весь корпус, FProbe и A06 проходят с ней; на allB берёт 164 операции, не взятые
    /// 2.5-мерной; цена — шум. Значение контекста; P3D_ANALYTICGENERAL=0 — прежнее поведение.
    /// </summary>
    public static bool AnalyticGeneral
    {
        get => KernelContext.Current is { } kc ? kc.Settings.AnalyticGeneral : _gAnalyticGeneral;
        set => KernelContext.SetGlobal(ref _gAnalyticGeneral, value, "BooleanOp.AnalyticGeneral");
    }

    /// <summary>Эксперимент r48: ленивая форма результатов общей булевой — разметкой в 10 раз мельче (P3D_LAZYFINE=1).</summary>
    public static readonly bool LazyFine = Environment.GetEnvironmentVariable("P3D_LAZYFINE") == "1";

    /// <summary>Сферы в маршруте общей булевой (этап 2б). ВЫКЛЮЧЕНО; P3D_ANALYTICSPHERES=1 — включить.</summary>
    /// <summary>KRN-01 (g.10): исключение при разбиении грани входа — отдельный код (сбой), P3D_FACEFAILSPLIT=0 — прежний face-missing.</summary>
    internal static readonly bool SplitFaceFailure = Environment.GetEnvironmentVariable("P3D_FACEFAILSPLIT") != "0";
    /// <summary>
    /// Сферы в маршруте общей булевой — ВКЛЮЧЕНЫ по умолчанию с 2026-10-09 (g.14: многогранная сфера уходила в STEP точной
    /// поверхностью с подогнанными рёбрами, расхождение 0.02 мм; см. CHANGELOG «ворота g.14»). P3D_ANALYTICSPHERES=0 — выключить.
    /// </summary>
    public static readonly bool AnalyticSpheresDefault = Environment.GetEnvironmentVariable("P3D_ANALYTICSPHERES") != "0";
    /// <summary>KRN-03: значение контекста вычисления (KernelSettings.AnalyticSpheres), если он задан; иначе — из окружения.</summary>
    public static bool AnalyticSpheres => KernelContext.Current is { } kc ? kc.Settings.AnalyticSpheres : AnalyticSpheresDefault;

    /// <summary>
    /// Маршрут общей аналитической булевой. Принимается, только если все грани размечены по контурам, B-rep годен и
    /// результат СОГЛАСЕН С CSG ПО ТОЧКАМ: для случайных точек в габарите классификатор результата отвечает так же, как
    /// классификаторы операндов по виду операции (независимая проверка геометрии; точки «на границе» и «не знаю» —
    /// не в счёт; решённых — не меньше 64, расхождений — ни одного).
    /// </summary>
    /// <summary>
    /// Разбор (KRN-00): итог каждой булевой — уровень гарантии успешного результата. Один вызов Evaluate — одна запись:
    /// общий знаменатель долей маршрутов. Отказы — EvaluateFailureProbe.
    /// </summary>
    [ThreadStatic] public static Action<string>? EvaluateOutcomeProbe;

    /// <summary>Разбор: операнды булевой, где общая аналитическая булева отказала (тело, резец, вид, причина).</summary>
    [ThreadStatic] public static Action<TSolid, TSolid, BooleanKind, string>? GeneralDeclineProbe;
    /// <summary>Разбор: принятая общая булева (тело, резец, вид, результат).</summary>
    [ThreadStatic] public static Action<TSolid, TSolid, BooleanKind, TSolid>? GeneralAcceptProbe;
    /// <summary>Разбор: результат общей булевой, не прошедший проверку B-rep.</summary>
    [ThreadStatic] public static Action<TSolid>? GeneralInvalidProbe;
    /// <summary>Разбор: точка, где результат точного маршрута расходится с CSG операндов (точка, классы A, B, результата).</summary>
    [ThreadStatic] public static Action<Vector3d, int, int, int, TSolid>? CsgDisagreeProbe;

    /// <summary>KRN-02: измеренный допуск операции последней аналитической булевой (разбор).</summary>
    [ThreadStatic] public static Action<string, double>? ToleranceDebug;
    /// <summary>KRN-02, замер: P3D_TOLSTATS=1 — допуски сущностей результатов в stderr.</summary>
    static readonly bool PolyOperandOld = Environment.GetEnvironmentVariable("P3D_POLYOPERAND") == "0";
    static bool IsPolyhedral(TSolid s) => s.Exact != null && s.ExactAuthoritative;
    static readonly bool FacetScopeOff = Environment.GetEnvironmentVariable("P3D_FACETSCOPE") == "0";
    public static KernelContext.Scope? FacetingScope()
    {
        if (FacetScopeOff) return null;
        var tc = ToleranceContext.Current;
        if (Core.Tol.Chord == tc.FacetChord && Core.Tol.ChordAngle == tc.FacetAngle) return null;
        var baseCtx = KernelContext.Current ?? new KernelContext();
        return baseCtx.With(st => st with { Chord = tc.FacetChord, ChordAngle = tc.FacetAngle }).Enter();
    }

    static readonly bool FacetMeasureOff = Environment.GetEnvironmentVariable("P3D_FACETMEASURE") == "0";

    /// <summary>g.04: наибольшее отклонение фасет от источников граней и обещанное гранением (по наибольшему радиусу кривизны).</summary>
    internal static (double Measured, double Promised) FacetDeviation(IEnumerable<TSolid> bodies)
    {
        double dev = 0, promised = Core.Tol.Chord, ang = Core.Tol.ChordAngle;
        foreach (var b in bodies)
            foreach (var f in b.Faces)
            {
                if (f.Surface is not FacetedSurface fs || fs.Source is not { } src || src.IsPlanar) continue;
                double D(Vector3d q) { try { return (src.PointAt(src.ProjectToUV(q)) - q).Length; } catch { return 0; } }
                double rmax = 0;
                foreach (var (a, bb, c) in fs.Facets.ReadView)
                {
                    dev = Math.Max(dev, Math.Max(D((a + bb + c) / 3), Math.Max(D((a + bb) / 2), Math.Max(D((bb + c) / 2), D((c + a) / 2)))));
                    rmax = Math.Max(rmax, Math.Max((a - bb).Length, Math.Max((bb - c).Length, (c - a).Length)));
                }
                var kr = Geometry.ExactEdges.ShapeOf(src) switch
                {
                    Geometry.Ssi.Sphere sp => sp.R, Geometry.Ssi.Cylinder cy => cy.R,
                    Geometry.Ssi.Torus t => t.R + t.r, _ => double.NaN
                };
                // обещание: хорда или прогиб шага угла по наибольшему радиусу; без известного радиуса — по длине стороны
                double r = double.IsNaN(kr) ? rmax : kr;
                // треугольник на дважды искривлённой поверхности: центр треугольника отходит до ~2 прогибов стороны
                promised = Math.Max(promised, 2 * Math.Max(Core.Tol.Chord, r * (1 - Math.Cos(ang / 2))));
            }
        return (dev, promised);
    }

    static readonly bool VolFacetsOff = Environment.GetEnvironmentVariable("P3D_VOLFACETS") == "0";
    static readonly bool VolDbg = Environment.GetEnvironmentVariable("P3D_VOLDBG") == "1";
    static readonly bool VolFacetsLocalOff = Environment.GetEnvironmentVariable("P3D_VOLFACETSLOCAL") == "0";
    /// <summary>Для проверки защиты: подмена объёма по формулам (внесённая ошибка интеграла).</summary>
    [ThreadStatic] public static Func<double, double>? VolumeFaultForTest;
    /// <summary>
    /// Объём тела по формулам граней против объёма фасет его граней. Граница: Σ по кривым граням площадь × 2·прогиб (хорда или
    /// R(1 − cos(шаг угла / 2)) по радиусу кривизны поверхности, без известного радиуса — по наибольшей стороне фасеты).
    /// null — у какой-то грани нет фасет (сравнивать не с чем).
    /// </summary>
    internal static (bool Agree, double Va, double Vf, double Bound)? VolumeVsFacets(TSolid r)
    {
        double vf = 0, bound = 0, ang = Core.Tol.ChordAngle;
        // KRN-02 (02.9): объём фасет — в ЛОКАЛЬНОЙ системе (от центра габарита). Формула дивергенции в мировых координатах
        // у детали 3 мм на 3e5 мм теряла все знаки: фасеты тела объёмом 5.497 давали 0.064, точный результат отвергался,
        // и вся цепочка уходила в многогранный путь (известный дефект KRN02-SMALL-FAR). Мутация P3D_VOLFACETSLOCAL=0.
        var (blo0, bhi0) = BoxOf(r); var o = VolFacetsLocalOff ? Vector3d.Zero : (blo0 + bhi0) / 2;
        foreach (var f in r.Faces)
        {
            if (f.Surface is not FacetedSurface fs || fs.Facets.Count == 0) return null;
            double af = 0, rmax = 0;
            foreach (var (a, b, c) in fs.Facets.ReadView)
            {
                vf += Vector3d.Dot(a - o, Vector3d.Cross(b - o, c - o)) / 6;
                af += Vector3d.Cross(b - a, c - a).Length / 2;
                rmax = Math.Max(rmax, Math.Max((a - b).Length, Math.Max((b - c).Length, (c - a).Length)));
            }
            var src = fs.Source;
            if (VolDbg) { double vff = 0, sgn = 0; foreach (var (a, b, c) in fs.Facets.ReadView) { vff += Vector3d.Dot(a - o, Vector3d.Cross(b - o, c - o)) / 6; if (src != null) { var m = (a + b + c) / 3; var n = src.NormalAt(src.ProjectToUV(m)); if (f.Reversed) n = -n; sgn += Vector3d.Dot(Vector3d.Cross(b - a, c - a), n); } }
                          Console.Error.WriteLine($"VOLDBG {f.Tag} {src?.GetType().Name} rev {f.Reversed} facets {fs.Facets.Count} vol {vff:G8} an {Topology.AnalyticVolume.FaceVolumeAt(f, o):G8} orient {Math.Sign(sgn)}");
                          if (src is not null && !src.IsPlanar && Topology.AnalyticVolume.FaceVolumeAt(f, o) is { } anf && Math.Abs(anf - vff) > 0.01 * Math.Abs(vff))
                          { Console.Error.WriteLine($"  surf {src}  area facets {fs.Facets.ReadView.Sum(t => Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length / 2):G8} an {Topology.AnalyticVolume.FaceArea(f):G8}");
                            foreach (var lp in f.Loops) { Console.Error.WriteLine($"  loop outer {lp.IsOuter}"); foreach (var ce in lp.CoEdges) Console.Error.WriteLine($"    {ce.Edge.Curve.GetType().Name} rev {ce.Reversed} {ce.Start.P} -> {ce.End.P} mid {ce.Edge.Curve.PointAt((ce.Edge.Curve.T0 + ce.Edge.Curve.T1) / 2)} pc {ce.PCurve?.GetType().Name}"); } } }
            if (src == null || src.IsPlanar) continue;
            double kr = Geometry.ExactEdges.ShapeOf(src) switch
            {
                Geometry.Ssi.Sphere sp => sp.R, Geometry.Ssi.Cylinder cy => cy.R, Geometry.Ssi.Torus t => t.R + t.r, Geometry.Ssi.Cone => rmax * 4, _ => rmax * 4
            };
            bound += af * 2 * Math.Max(Core.Tol.Chord, kr * (1 - Math.Cos(ang / 2)));
        }
        double va = Topology.AnalyticVolume.Of(r) ?? double.NaN;
        if (VolumeFaultForTest is { } vft) va = vft(va);
        bound += 1e-9 * Math.Abs(va) + 1e-9;
        return (Math.Abs(va - vf) <= bound, va, vf, bound);
    }

    static readonly bool FullValidateOff = Environment.GetEnvironmentVariable("P3D_FULLVAL") == "0";
    static readonly bool RoutePCurveOff = Environment.GetEnvironmentVariable("P3D_ROUTEPCURVE") == "0";
    static readonly bool TolStats = Environment.GetEnvironmentVariable("P3D_TOLSTATS") == "1";
    /// <summary>KRN-03: раздельные компоненты результата — отдельные тела, полости — оболочки (P3D_SPLITCOMPONENTS=0 — прежнее).</summary>
    static readonly bool SplitComponents = Environment.GetEnvironmentVariable("P3D_SPLITCOMPONENTS") != "0";
    /// <summary>
    /// KRN-02: допуск операции = max(модельная точность, измеренный допуск операндов) — вместо глобальных 2e-5 мм.
    /// Меньше модельной точности он не бывает; грязные (импортированные, аппроксимированные) операнды его поднимают.
    /// </summary>
    internal static double OperationTolerance(TSolid a, TSolid b) => Analytic.OperandTolerance.Of(a, b);

    /// <summary>Знак «точный маршрут доказал пустой результат» (не тело).</summary>
    internal static readonly TSolid ProvedEmpty = new();

    private static TSolid? TryAnalyticGeneral(TSolid body, TSolid other, BooleanKind kind, List<string> warn)
    {
        var prevE = Analytic.Region2Ops.EpsOverride; var prevF = Analytic.FaceSplit.TolOverride;
        double tol; using (Diagnostics.Prof.Time("булева: допуск операции (общая)")) tol = OperationTolerance(body, other); ToleranceDebug?.Invoke("general", tol);
        Analytic.FaceSplit.TolOverride = tol; Analytic.Region2Ops.EpsOverride = tol;
        try { using (Diagnostics.Prof.Time("булева: общий маршрут целиком")) return TryAnalyticGeneralCore(body, other, kind, warn); }
        finally { Analytic.Region2Ops.EpsOverride = prevE; Analytic.FaceSplit.TolOverride = prevF; }
    }

    /// <summary>KRN-10 (g.13): режим предпросмотра текущего вычисления — проверки результата откладываются до принятия.</summary>
    internal static bool PreviewMode => !PreviewOff && KernelContext.Current?.Settings.Preview == true;
    static readonly bool PreviewOff = Environment.GetEnvironmentVariable("P3D_PREVIEW") == "0";
    /// <summary>Мутация: P3D_PREVIEWCHECKS=0 — принятие предпросмотра без проверок.</summary>
    static readonly bool PreviewChecksOff = Environment.GetEnvironmentVariable("P3D_PREVIEWCHECKS") == "0";
    static readonly bool PreviewPrismOff = Environment.GetEnvironmentVariable("P3D_PREVIEWPRISM") == "0";
    /// <summary>Сколько раз проверки призматического маршрута отложены предпросмотром (проверка KRN10-PART-PREVIEW-ACCEPT).</summary>
    public static long PrismChecksDeferred;
    /// <summary>Проверки: отложенная проверка отказывает (принятие должно это увидеть).</summary>
    [ThreadStatic] internal static bool ForceAcceptFailure;

    /// <summary>
    /// ПРИНЯТЬ ПРЕДПРОСМОТР: выполнить отложенные проверки маршрута на том же теле. null — принято (тело то же, отметка снята);
    /// иначе причина отказа — тело не принято, вызывающий строит операцию заново без предпросмотра (там проверка отвергнет
    /// маршрут и сработает прежний запасной путь).
    /// </summary>
    public static string? AcceptPreview(TSolid r)
    {
        if (r.PendingAcceptance is not { } f) return null;
        string? why = PreviewChecksOff ? null : f();
        if (why == null) r.PendingAcceptance = null;
        return why;
    }

    private static TSolid? TryAnalyticGeneralCore(TSolid body, TSolid other, BooleanKind kind, List<string> warn)
    {
        // g.02: ОПЕРАНД С АВТОРИТЕТНОЙ МНОГОГРАННОЙ ФОРМОЙ — не в точный маршрут. Его грани несут аналитические источники, а
        // рёбра подогнаны к фасетам: маршрут строил результат по источникам и объявлял его ExactAnalytic, хотя операнд —
        // многогранник (объединение цилиндра с шаром многогранно, затем − брусок: 3301.6 вместо 3354.7 мм³, тождество с
        // многогранным пересечением нарушено на 41 мм³; серия 8 разностного сравнения). Мутация P3D_POLYOPERAND=0.
        if (!PolyOperandOld && (IsPolyhedral(body) || IsPolyhedral(other))) { AnalyticRouteDebug?.Invoke("general declined: polyhedral operand"); return null; }
        // 06.12/g.08: грани по формуле (ленты, протяжки, угловые лоскуты) — представлением NURBS в пределах точности модели
        NurbsProxy.Begin();
        if (NurbsProxy.Of(body, out var pw1) is { } pb) body = pb; else if (pw1 != null) AnalyticRouteDebug?.Invoke("proxy: " + pw1);
        if (NurbsProxy.Of(other, out var pw2) is { } po) other = po; else if (pw2 != null) AnalyticRouteDebug?.Invoke("proxy: " + pw2);
        var op = kind == BooleanKind.Unite ? Analytic.Bool2.Union : kind == BooleanKind.Subtract ? Analytic.Bool2.Subtract : Analytic.Bool2.Intersect;
        // Сферы (этап 2б) в маршруте — за флагом: на корпусе скругление вершины куба R6 давало итоговую сетку с замечаниями
        // (K09-VERTEX-CHAMFER-AND-ROUND) — дефект дальше в цепочке, разобрать.
        if (!AnalyticSpheres && body.Faces.Concat(other.Faces).Any(fc => Geometry.ExactEdges.ShapeOf(Topology.GeometryAudit.AuthoritativeSurface(fc)) is Geometry.Ssi.Sphere))
        { AnalyticRouteDebug?.Invoke("general declined: sphere faces (P3D_ANALYTICSPHERES=1 to enable)"); return null; }
        TSolid? r;
        var proxySwaps = new List<(int Face, ISurface3 Proxy, bool Rev)>();
        try
        {
            OperationTrace.Enter("boolean.analytic.general");
            string why;
            using (Diagnostics.Prof.Time("аналитика (общая): сборка")) r = Analytic.GeneralBoolean.Run(body, other, op, out why);
            if (r != null) proxySwaps = NurbsProxy.Restore(r);
            if (r == null && Analytic.GeneralBoolean.LastProvedEmpty) { AnalyticRouteDebug?.Invoke("general: empty result proved"); return ProvedEmpty; }
            if (r == null)
            {
                AnalyticRouteDebug?.Invoke("general declined: " + why); GeneralDeclineProbe?.Invoke(body, other, kind, why);
                // KRN-05 (05.5): нарушенное разбиение грани — дефект ядра, а не неподдержанный случай: причина — в предупреждениях
                if (why.Contains("partition check")) warn.Add("Boolean: general analytic path refused — " + why);
                return null;
            }
        }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { AnalyticRouteDebug?.Invoke("general exception: " + ex.Message); return null; }
        Func<TFace, FacetedSurface?>? donor = null;
        if (ReuseFaceMeshes)
        {
            var donors = new Dictionary<string, FacetedSurface>();
            foreach (var f in DonorLoopsOff ? body.Faces : body.Faces.Concat(other.Faces))
                if (f.Surface is FacetedSurface dfs && dfs.Facets.Count > 0 && (dfs.Display != null || (dfs.FromLoops && !DonorLoopsOff)) && MeshDonorKey(f) is { } k) donors.TryAdd(k, dfs);
            if (donors.Count > 0) donor = f => MeshDonorKey(f) is { } k && donors.TryGetValue(k, out var d) ? d : null;
        }
        int unmeshed;
        var rawForLazy = LazyFine ? TopologyClone.Deep(r) : null;      // эксперимент r48: копия до завершения
        var meshWhy = AnalyticRouteDebug != null ? new List<string>() : null;
        using (Diagnostics.Prof.Time("аналитика (общая): завершение")) unmeshed = Analytic.PrismBuild.Finish(r, meshWhy, donor);
        if (unmeshed > 0) { AnalyticRouteDebug?.Invoke($"general invalid: {unmeshed} face(s) not meshed from their loops: {string.Join(" | ", meshWhy ?? new())}"); return null; }
        // Сферические грани размечаются иначе (кольцами к полюсу) — швы не согласованы по построению: полная проверка
        // (на корпусе — сетка с замечаниями у скругления вершин); прочие — B-rep (швы плоскостей и цилиндров — по рёбрам).
        // KRN-10 (g.13): ПРОВЕРКИ РЕЗУЛЬТАТА МАРШРУТА — одна функция. Обычный режим — сразу (отказ — многогранный путь);
        // режим предпросмотра (KernelSettings.Preview) — откладываются: тело помечено PendingAcceptance, принятие —
        // AcceptPreview (те же проверки на том же теле; отказ — вызывающий строит заново без предпросмотра).
        var gAtBuild = r.Guarantee;
        string? Checks()
        {
            if (ForceAcceptFailure) return "forced acceptance failure (test)";
            var gSaved = r.Guarantee; r.Guarantee = gAtBuild;
            try { using (Diagnostics.Overhead.Scope()) return ChecksCore(); } finally { r.Guarantee = gSaved; }
        }
        string? ChecksCore()
        {
            bool curved = r.Faces.Any(fc => Geometry.ExactEdges.ShapeOf(Topology.GeometryAudit.AuthoritativeSurface(fc)) is Geometry.Ssi.Sphere);
            // g.02: ПОЛНАЯ ПРОВЕРКА И У НЕСФЕРИЧЕСКИХ РЕЗУЛЬТАТОВ, если есть грани не плоскость/цилиндр (конус): объединение бруска
            // с повёрнутым конусом принималось с 46 сторонами сетки «в одну сторону» и объёмом на 351 мм³ меньше (разностное
            // сравнение с OpenCascade). Мутация P3D_FULLVAL=0 — прежняя проверка (только B-rep).
            // Сетка отображения хранит координаты во float: дальше ~1000 мм от начала её швы не сходятся по округлению (деталь у
            // 3e4 мм — «стороны без пары»), и проверка сетки там не о геометрии (KRN-09) — там прежняя проверка B-rep.
            var (blo, bhi) = BoxOf(r); double far = Math.Max(Math.Max(blo.Length, bhi.Length), 0);
            bool coneLike = !FullValidateOff && far < 1000 && r.Faces.Any(fc => Geometry.ExactEdges.ShapeOf(Topology.GeometryAudit.AuthoritativeSurface(fc)) is not (Geometry.Ssi.Plane or Geometry.Ssi.Cylinder));
            // g.05: p-КРИВЫЕ ПРОВЕРЯЮТСЯ КАК У ТОЧНОГО ТЕЛА. Прежде проверка шла до того, как результату присвоен уровень гарантии,
            // и несогласованность p-кривых (ошибка только у точного тела) не замечалась; у тел из плоскостей и цилиндров p-кривые
            // не проверялись вовсе: результат с p-кривой в 2e-6 мм от ребра (допуск 1e-7) принимался как ExactAnalytic.
            // Мутация P3D_ROUTEPCURVE=0.
            var g0 = r.Guarantee; if (!RoutePCurveOff) r.Guarantee = GuaranteeLevel.ExactAnalytic;
            Topology.ValidationReport v;
            using (Diagnostics.Prof.Time("аналитика (общая): проверка тела"))
            {
                v = curved || coneLike ? Topology.SolidValidator.Validate(r) : Topology.SolidValidator.ValidateBRep(r);
                if (!RoutePCurveOff && v.IsValid && !(curved || coneLike)) v.Merge(Topology.SolidValidator.ValidatePCurves(r));
            }
            r.Guarantee = g0;
            if (!v.IsValid) { GeneralInvalidProbe?.Invoke(r!); AnalyticRouteDebug?.Invoke("general invalid: " + v.Summary() + " :: " + string.Join(" / ", v.Issues.Where(i => i.Severity == Topology.ValidationSeverity.Error).Take(3))); GeneralDeclineProbe?.Invoke(body, other, kind, "invalid: " + v.Summary()); return "general route check failed"; }
            bool noVol;
            using (Diagnostics.Prof.Time("аналитика (общая): объём по формулам")) noVol = Topology.AnalyticVolume.Of(r) == null;
            if (noVol) { AnalyticRouteDebug?.Invoke("general: no analytic volume"); GeneralDeclineProbe?.Invoke(body, other, kind, "no analytic volume"); return "general route check failed"; }
            // g.05: ОБЪЁМ ПО ФОРМУЛАМ ГРАНЕЙ СОГЛАСУЕТСЯ С ОБЪЁМОМ ЕГО ЖЕ ФАСЕТ в пределах прогиба гранения. Точки CSG проверяют
            // геометрию тела, но не интеграл: брусок минус тор (серия 8, случай 79) принимался с объёмом 46 190 при фасетах
            // 42 871 и верном 39 560 (OpenCascade). Мутация P3D_VOLFACETS=0.
            (bool Agree, double Va, double Vf, double Bound)? vfr = null;
            using (Diagnostics.Prof.Time("аналитика (общая): объём и фасеты")) { if (!VolFacetsOff) vfr = VolumeVsFacets(r); }
            if (!VolFacetsOff && vfr is { } vf && !vf.Agree)
            {
                AnalyticRouteDebug?.Invoke($"general: volume {vf.Va:G8} disagrees with its facets {vf.Vf:G8} (bound {vf.Bound:G3})");
                GeneralDeclineProbe?.Invoke(body, other, kind, "volume vs facets"); return "general route check failed";
            }
            // согласие с CSG по точкам
            using (Diagnostics.Prof.Time("аналитика (общая): согласие с CSG"))
            {
                var cr = new Analytic.Classifier(r); var ca = new Analytic.Classifier(body); var cb = new Analytic.Classifier(other);
                if (cr.Unsupported != null && NurbsProxy.ProxyView(r, proxySwaps) is { } rView) cr = new Analytic.Classifier(rView);
                if (cr.Unsupported != null || ca.Unsupported != null || cb.Unsupported != null) { AnalyticRouteDebug?.Invoke("general: classifier unsupported"); return "general route check failed"; }
                // Точки — в основном в ПЕРЕСЕЧЕНИИ габаритов операндов (там результат меняется: снятые 268 мм³ в теле в десятки
                // мм проскакивали мимо 128 точек по общему габариту), четверть — по общему.
                var (lo, hi) = BoxOf(body); var (lo2, hi2) = BoxOf(other);
                var ilo = Vector3d.ComponentMax(lo, lo2); var ihi = Vector3d.ComponentMin(hi, hi2);
                bool hasI = ilo.X < ihi.X && ilo.Y < ihi.Y && ilo.Z < ihi.Z;
                lo = Vector3d.ComponentMin(lo, lo2); hi = Vector3d.ComponentMax(hi, hi2);
                var rnd = new Random(20261006); int decided = 0, disagree = 0;
                for (int i = 0; i < 800 && decided < 256; i++)
                {
                    var (bl, bh) = hasI && i % 4 != 0 ? (ilo, ihi) : (lo, hi);
                    var p = new Vector3d(bl.X + (bh.X - bl.X) * rnd.NextDouble(), bl.Y + (bh.Y - bl.Y) * rnd.NextDouble(), bl.Z + (bh.Z - bl.Z) * rnd.NextDouble());
                    int? a = ca.Classify(p), b = cb.Classify(p), c = cr.Classify(p);
                    if (a is not (1 or -1) || b is not (1 or -1) || c is not (1 or -1)) continue;
                    bool expect = op switch { Analytic.Bool2.Union => a == 1 || b == 1, Analytic.Bool2.Intersect => a == 1 && b == 1, _ => a == 1 && b != 1 };
                    decided++; if ((c == 1) != expect) { disagree++; if (disagree <= 4) CsgDisagreeProbe?.Invoke(p, a.Value, b.Value, c.Value, r); }
                }
                if (decided < 64 || disagree > 0) { AnalyticRouteDebug?.Invoke($"general: CSG agreement {decided - disagree}/{decided}"); return "general route check failed"; }
            }
            return null;
        }
        if (PreviewMode) r.PendingAcceptance = Checks;
        else if (Checks() != null) return null;
        // производная многогранная форма — лениво, из собранного тела (имена — текущие на момент построения)
        var derived = rawForLazy == null
            ? new Lazy<Exact.ExactBody>(() => Exact.ExactAdapter.ToExact(TopologyClone.Deep(r), new Exact.PlaneRegistry(), null), LazyThreadSafetyMode.ExecutionAndPublication)
            : new Lazy<Exact.ExactBody>(() =>
            {
                // ЭКСПЕРИМЕНТ r48: ленивая форма — разметкой в 10 раз мельче (хорда 0.002 мм, шаг угла 0.75°)
                using var _c = new KernelContext(KernelSettings.Capture() with { Chord = 0.006, ChordAngle = Math.PI / 60 }).Enter();
                Topology.TopologyConventions.Normalize(rawForLazy);
                Export.StepImport.MeshFacesFromLoops(rawForLazy, null, "LazyFine");
                return Exact.ExactAdapter.ToExact(rawForLazy, new Exact.PlaneRegistry(), null);
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        r.LazyDerivedExact = (derived, r.Revision);
        if (Analytic.GeneralBoolean.LastProvenance is { } pv) r.SetProvenance(FinalizeProvenance(r, pv, body, other));
        AnalyticRouteDebug?.Invoke("general accepted");
        GeneralAcceptProbe?.Invoke(body, other, kind, r);
        warn.Add($"Boolean: general analytic path ({r.Faces.Count()} faces, exact surfaces).");
        return r;
    }

    /// <summary>
    /// Происхождение, записанное сборкой, — к граням готового тела: доводка (разделение многообластных граней соглашением)
    /// могла заменить грань; новая грань без записи получает источник по имени среди граней операндов (разделение).
    /// </summary>
    internal static Topology.Provenance FinalizeProvenance(TSolid r, Topology.Provenance pv, TSolid a, TSolid b)
    {
        var faces = r.Faces.ToHashSet(ReferenceEqualityComparer.Instance);
        var res = new Topology.Provenance { Operation = pv.Operation };
        foreach (var fp in pv.Faces) if (faces.Contains(fp.Result)) res.Faces.Add(fp);
        foreach (var f in r.Faces)
        {
            if (res.Faces.Any(x => ReferenceEquals(x.Result, f))) continue;
            var src = a.Faces.Concat(b.Faces).FirstOrDefault(x => x.Tag == f.Tag && f.Tag.Length > 0);
            res.Faces.Add(new Topology.FaceProvenance(f, src, src == null ? Topology.FaceOrigin.Generated : Topology.FaceOrigin.Split, "face replaced after assembly"));
        }
        res.Deleted.AddRange(pv.Deleted); res.Merged.AddRange(pv.Merged);
        return res;
    }

    /// <summary>Разбор: решение аналитического маршрута по каждой булевой («accepted» или причина отказа). null — выключено.</summary>
    [ThreadStatic] public static Action<string>? AnalyticRouteDebug;

    /// <summary>Габарит тела по точкам рёбер (с ломаными кривых), расширенный на 1e-6 мм.</summary>
    private static (Vector3d Lo, Vector3d Hi) BoxOf(TSolid s)
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); var pts = new List<Vector3d>();
        foreach (var e in s.Edges) { pts.Clear(); e.Curve.Tessellate(pts, true); foreach (var p in pts) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); } }
        if (BoxOld) return (lo - new Vector3d(1e-6), hi + new Vector3d(1e-6));
        // g.02: ГАБАРИТ — ПО ГРАНЯМ, А НЕ ТОЛЬКО ПО РЁБРАМ. Кривая грань выходит за свои рёбра (шар без рёбер — габарит
        // пустой; шапка за окружностью края), и тело с шаром отбрасывалось как «не пересекающее» резец: вычитание шара из
        // результата прежней булевой не снимало ничего (5537 вместо 2993 мм³ — разностное сравнение с OpenCascade). Точки —
        // сетка граней, запас — 2% диагонали (сетка вписана в кривую поверхность). Мутация P3D_BOXFACES=0.
        foreach (var f in s.Faces)
        {
            if (f.Surface.IsPlanar) continue;
            IEnumerable<Vector3d> fp;
            if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0) fp = fs.Facets.ReadView.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 });
            else
            {
                var mb = new Tess.MeshBuilder();
                try { f.Surface.TessellateFace(f, mb); } catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { }
                fp = mb.PositionsD;
            }
            foreach (var p in fp) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
        }
        if (lo.X > hi.X) return (lo, hi);
        var m = new Vector3d(1e-6 + 0.02 * (hi - lo).Length);
        return (lo - m, hi + m);
    }
    static readonly bool BoxOld = Environment.GetEnvironmentVariable("P3D_BOXFACES") == "0";
    private static bool Overlap((Vector3d Lo, Vector3d Hi) a, (Vector3d Lo, Vector3d Hi) b) =>
        a.Lo.X <= b.Hi.X && b.Lo.X <= a.Hi.X && a.Lo.Y <= b.Hi.Y && b.Lo.Y <= a.Hi.Y && a.Lo.Z <= b.Hi.Z && b.Lo.Z <= a.Hi.Z;

    private static TSolid? TryAnalyticPrism(TSolid body, TSolid other, BooleanKind kind, List<string> warn)
    {
        var prev = Analytic.Region2Ops.EpsOverride;
        double tol = OperationTolerance(body, other); ToleranceDebug?.Invoke("prism", tol);
        Analytic.Region2Ops.EpsOverride = tol;
        try { return TryAnalyticPrismCore(body, other, kind, warn); }
        finally { Analytic.Region2Ops.EpsOverride = prev; }
    }

    private static TSolid? TryAnalyticPrismCore(TSolid body, TSolid other, BooleanKind kind, List<string> warn)
    {
        if (!PolyOperandOld && (IsPolyhedral(body) || IsPolyhedral(other))) { AnalyticRouteDebug?.Invoke("declined: polyhedral operand"); return null; }
        var op = kind == BooleanKind.Unite ? Analytic.Bool2.Union : kind == BooleanKind.Subtract ? Analytic.Bool2.Subtract : Analytic.Bool2.Intersect;
        TSolid? r;
        // Свой этап: отмена пробрасывается (KernelContext.Checkpoint), внутренний сбой маршрута — в том числе
        // внедрённый (FaultInjection, этап «boolean.analytic») — отказ маршрута и переход на многогранный путь.
        Topology.Provenance? prismProv = null;
        if (Environment.GetEnvironmentVariable("P3D_R2DBGAT") is string r2at && r2at.Split(',') is [var r2x, var r2y])
            Analytic.Region2Ops.DbgAt = new OpenTK.Mathematics.Vector2d(double.Parse(r2x, System.Globalization.CultureInfo.InvariantCulture), double.Parse(r2y, System.Globalization.CultureInfo.InvariantCulture));
        try { OperationTrace.Enter("boolean.analytic"); string why; using (Diagnostics.Prof.Time("аналитика: распознавание и сборка")) r = Analytic.PrismBuild.Boolean(body, other, op, out why);
            if (r == null && why == "the result is empty" && Analytic.GeneralBoolean.ConfirmEmpty(body, other, op)) { AnalyticRouteDebug?.Invoke("prism: empty result proved"); return ProvedEmpty; }
            if (r == null) { AnalyticRouteDebug?.Invoke("declined: " + why); return null; }
            prismProv = Analytic.PrismBuild.LastProvenance; }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { AnalyticRouteDebug?.Invoke("exception: " + ex.Message); return null; }
        // НЕЗАВИСИМАЯ СВЕРКА РАСПОЗНАВАНИЯ: объём каждого операнда по слоям — против аналитического объёма его
        // нормализованной копии. Самопроверка «объём результата = объём по слоям» неверное распознавание не ловит (обе
        // величины ошибаются одинаково: составной резец цекования был принят с ошибкой 804 мм³). Не подтверждено — отказ.
        // g.13: В ПРЕДПРОСМОТРЕ ПРОВЕРКИ МАРШРУТА (сверка операндов, B-rep, объём) ОТКЛАДЫВАЮТСЯ до принятия, как у общего
        // маршрута; разметка граней по контурам — нет (без неё нечего показать). Мутация P3D_PREVIEWPRISM=0.
        bool deferChecks = PreviewMode && !PreviewPrismOff;
        double pvA = Analytic.PrismBuild.LastVolumeA, pvB = Analytic.PrismBuild.LastVolumeB, pvR = Analytic.PrismBuild.LastVolume;
        string? OperandChecks()
        {
            foreach (var (operand, pv0, name) in new[] { (body, pvA, "first"), (other, pvB, "second") })
            {
                double? ov; using (Diagnostics.Overhead.Scope()) using (Diagnostics.Prof.Time("аналитика: сверка операнда")) ov = OperandVolume(operand);
                if (ov == null) { AnalyticRouteDebug?.Invoke($"recognition unconfirmed: {name} operand has no analytic volume"); return "recognition unconfirmed"; }
                if (Math.Abs(ov.Value - pv0) > 1e-6 * Math.Max(1, Math.Abs(ov.Value))) { AnalyticRouteDebug?.Invoke($"recognition check: {name} operand layers {pv0} vs analytic {ov}"); return "recognition check failed"; }
            }
            return null;
        }
        if (!deferChecks && OperandChecks() != null) return null;
        // Производная многогранная форма — из аналитического тела ДО разметки отображения, тем же гранением, что у сырых
        // резцов (TessellateFace точных поверхностей): иначе её многоугольники не совпадают с гранями следующего соосного
        // резца, и смешанная цепочка (аналитика → многогранная операция) даёт «гребёнку» щепок (зенковка двумя
        // вычитаниями: 40 граней, 48 кривых пересечения, ошибка объёма 0.0925 мм³).
        // Строится ЛЕНИВО (по требованию многогранного пути): построение дорогое (полное перестроение реалистичной детали
        // с маршрутом — 2 492 мс против 296, почти всё — эта форма).
        // Из тела ДО объединения граней: гранение выдавливания идёт полной полосой (контуров не учитывает), а там каждая
        // боковая грань — прямоугольная полоса (у L-образной грани после объединения форма выходила незамкнутой).
        var profLazy = Diagnostics.Prof.Time("аналитика: подготовка ленивой формы");
        var raw = Analytic.PrismBuild.LastPreUnify ?? Topology.TopologyClone.Deep(r);
        var map = Analytic.PrismBuild.LastPreUnify != null ? Analytic.PrismBuild.LastPreUnifyMap : null;
        var rawFaces = raw.Faces.ToList();
        var derived = new Lazy<Exact.ExactBody>(() =>
        {
            // всё дорогое — по требованию: соглашение копии и треугольники цилиндрических граней по точкам рёбер
            Topology.TopologyConventions.Normalize(raw);
            Analytic.PrismBuild.FacetCylinderSides(raw);
            // ТЕКУЩИЕ имена граней результата (деталь дописывает ключ операции уже после операции) — в копию.
            if (map != null) for (int i = 0; i < rawFaces.Count && i < map.Count; i++) rawFaces[i].Tag = map[i].Tag;
            return Exact.ExactAdapter.ToExact(raw, new Exact.PlaneRegistry(), null);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        profLazy.Dispose();
        var w = new List<string>();
        int unmeshed;
        // Доноры сетки — грани операнда-тела (правка последней операции: неизменённые грани не размечаются заново).
        Func<TFace, FacetedSurface?>? donor = null;
        if (ReuseFaceMeshes)
        {
            var donors = new Dictionary<string, FacetedSurface>();
            foreach (var f in DonorLoopsOff ? body.Faces : body.Faces.Concat(other.Faces))
                if (f.Surface is FacetedSurface dfs && dfs.Facets.Count > 0 && (dfs.Display != null || (dfs.FromLoops && !DonorLoopsOff)) && MeshDonorKey(f) is { } k) donors.TryAdd(k, dfs);
            if (donors.Count > 0) donor = f => MeshDonorKey(f) is { } k && donors.TryGetValue(k, out var d) ? d : null;
            if (DonorDbg) Console.Error.WriteLine($"DONORDBG prism: body faces {body.Faces.Count()}, with display {body.Faces.Count(f => f.Surface is FacetedSurface fs && fs.Display != null)}, donors {donors.Count}");
        }
        using (Diagnostics.Prof.Time("аналитика: завершение (соглашение, разметка)")) unmeshed = Analytic.PrismBuild.Finish(r, w, donor);
        // ПРОВЕРКА РЕЗУЛЬТАТА. Каждая грань размечается точным разметчиком по общим ломаным рёбер и проверяет себя сама:
        // если все грани построены, швы согласованы по построению, и проверка всей сетки (88 мс из 123 на реалистичной
        // детали) повторяет известное — проверяется B-rep. Не построенная грань — отказ маршрута (многогранный путь):
        // строже прежнего (результат проходил, если общая сетка всё же оказывалась замкнутой), зато без случайностей.
        if (unmeshed > 0) { AnalyticRouteDebug?.Invoke($"invalid: {unmeshed} face(s) not meshed from their loops"); return null; }
        var gAtBuild = r.Guarantee;
        string? ResultChecks()
        {
            var gSaved = r.Guarantee; r.Guarantee = gAtBuild;
            try
            {
                Topology.ValidationReport v;
                using (Diagnostics.Overhead.Scope()) using (Diagnostics.Prof.Time("аналитика: проверка B-rep")) v = Topology.SolidValidator.ValidateBRep(r);
                if (!v.IsValid) { AnalyticRouteDebug?.Invoke("invalid: " + v.Summary()); return "prism route: invalid B-rep"; }
                double? av; using (Diagnostics.Overhead.Scope()) using (Diagnostics.Prof.Time("аналитика: аналитический объём")) av = Topology.AnalyticVolume.Of(r);
                if (av == null || Math.Abs(av.Value - pvR) > 1e-9 * Math.Max(1, Math.Abs(pvR))) { AnalyticRouteDebug?.Invoke($"volume check: analytic {av} vs layers {pvR}"); return "prism route: volume check failed"; }
                return null;
            }
            finally { r.Guarantee = gSaved; }
        }
        if (deferChecks)
        {
            r.PendingAcceptance = () => ForceAcceptFailure ? "forced acceptance failure (test)" : OperandChecks() ?? ResultChecks();
            Interlocked.Increment(ref PrismChecksDeferred);
        }
        else if (ResultChecks() != null) return null;
        // Производная многогранная форма — лениво: следующие многогранные операции берут её через ExactAdapter.ToExact.
        r.LazyDerivedExact = (derived, r.Revision);
        if (prismProv != null) r.SetProvenance(FinalizeProvenance(r, prismProv, body, other));
        AnalyticRouteDebug?.Invoke("accepted");
        warn.Add($"Boolean: analytic 2.5D path ({r.Faces.Count()} faces, exact surfaces).");
        return r;
    }

    /// <summary>
    /// Строгий ключ донора сетки грани: поверхность по содержанию, обращение грани, контуры в порядке обхода — у каждого
    /// полуребра вид кривой, все её определяющие числа побитово и направление. null — поверхность не по содержанию.
    /// </summary>
    /// <summary>Мутация P3D_DONORLOOPS=0: донором — только грань с сеткой отображения (у точных маршрутов её нет — доноров нет).</summary>
    static readonly bool DonorLoopsOff = Environment.GetEnvironmentVariable("P3D_DONORLOOPS") == "0";
    static readonly bool DonorDbg = Environment.GetEnvironmentVariable("P3D_DONORDBG") == "1";
    internal static string? MeshDonorKey(TFace f)
    {
        var src = Topology.GeometryAudit.AuthoritativeSurface(f);
        if (src == null || SourceKey(src) is not string sk) return null;
        var b = new System.Text.StringBuilder(sk).Append(f.Reversed ? "|R" : "|F");
        // точность гранения — часть ключа: сетка той же грани при другой хорде другая
        b.Append("|c").Append(BitConverter.DoubleToInt64Bits(Core.Tol.Chord)).Append(',').Append(BitConverter.DoubleToInt64Bits(Core.Tol.ChordAngle));
        static void V(System.Text.StringBuilder sb, Vector3d v) => sb.Append(BitConverter.DoubleToInt64Bits(v.X)).Append(',').Append(BitConverter.DoubleToInt64Bits(v.Y)).Append(',').Append(BitConverter.DoubleToInt64Bits(v.Z)).Append(';');
        if (!DonorCanonOff) return CanonLoops(b, f);
        foreach (var l in f.Loops)
        {
            b.Append(l.IsOuter ? "|O" : "|I");
            foreach (var ce in l.CoEdges)
            {
                b.Append(ce.Reversed ? "<" : ">");
                switch (ce.Edge.Curve)
                {
                    case LineCurve3 ln: b.Append("L"); V(b, ln.A); V(b, ln.B); break;
                    case ArcCurve3 a: b.Append("A"); V(b, a.Frame.Origin); V(b, a.Frame.U); V(b, a.Frame.V); V(b, a.Frame.W);
                        b.Append(BitConverter.DoubleToInt64Bits(a.Radius)).Append(',').Append(BitConverter.DoubleToInt64Bits(a.A0)).Append(',').Append(BitConverter.DoubleToInt64Bits(a.A1)).Append(';'); break;
                    default: return null;
                }
                V(b, ce.Edge.V0.P); V(b, ce.Edge.V1.P);
            }
        }
        return b.ToString();
    }

    /// <summary>
    /// g.13: КЛЮЧ ДОНОРА БЕЗ ПРОИЗВОЛА ПОСТРОЕНИЯ. Разметчик видит контур как цикл точек рёбер в порядке обхода: у отрезка это
    /// его две вершины (обход), направление самой кривой ребра роли не играет; начало цикла — тоже (одна и та же грань,
    /// собранная заново, хранила отрезки в другую сторону — донор не находился, 1 680
    /// промахов на детали). Ключ: отрезок — концы по обходу; дуга — как прежде (её точки идут по параметру); начало цикла —
    /// как у грани. Что сетка при этом та же, проверяет KRN10-MESH-DONOR-FROM-LOOPS. Мутация P3D_DONORCANON=0.
    /// </summary>
    static string? CanonLoops(System.Text.StringBuilder b, TFace f)
    {
        static string V(Vector3d v) => $"{BitConverter.DoubleToInt64Bits(v.X)},{BitConverter.DoubleToInt64Bits(v.Y)},{BitConverter.DoubleToInt64Bits(v.Z)};";
        foreach (var l in f.Loops)
        {
            b.Append(l.IsOuter ? "|O" : "|I");
            var toks = new List<string>(l.CoEdges.Count);
            foreach (var ce in l.CoEdges)
            {
                switch (ce.Edge.Curve)
                {
                    case LineCurve3:
                        // первое ребро контура задаёт направление обхода у разметчика (цикл идёт от его V0) — его направление в ключе
                        toks.Add((toks.Count == 0 ? (ce.Reversed ? "<l" : ">l") : "l") + V(ce.Reversed ? ce.Edge.V1.P : ce.Edge.V0.P) + V(ce.Reversed ? ce.Edge.V0.P : ce.Edge.V1.P)); break;
                    case ArcCurve3 a:
                        toks.Add((ce.Reversed ? "<A" : ">A") + V(a.Frame.Origin) + V(a.Frame.U) + V(a.Frame.V) + V(a.Frame.W)
                                 + $"{BitConverter.DoubleToInt64Bits(a.Radius)},{BitConverter.DoubleToInt64Bits(a.A0)},{BitConverter.DoubleToInt64Bits(a.A1)};" + V(ce.Edge.V0.P) + V(ce.Edge.V1.P)
                                 + (ReferenceEquals(ce.Edge.V0, ce.Edge.V1) ? "c" : ""));
                        break;
                    default: return null;
                }
            }
            // начало цикла — как у грани: разметчик от него зависит (поворот цикла менял сетку 7 граней из 193)
            foreach (var t in toks) b.Append(t);
        }
        return b.ToString();
    }
    static readonly bool DonorCanonEnvOff = Environment.GetEnvironmentVariable("P3D_DONORCANON") == "0";
    [ThreadStatic] static bool _donorCanonOffForTests;
    static bool DonorCanonOff => DonorCanonEnvOff || _donorCanonOffForTests;
    /// <summary>Сравнение (KRN10-MESH-DONOR-FROM-LOOPS): прежний ключ донора в этом потоке.</summary>
    public static bool DonorCanonOffForTests { get => _donorCanonOffForTests; set => _donorCanonOffForTests = value; }

    /// <summary>Аналитический объём тела; не приведённое к соглашению — по нормализованной копии.</summary>
    private static double? OperandVolume(TSolid s)
    {
        if (s.TopologyConvention >= Topology.TopologyConventions.Version) return Topology.AnalyticVolume.Of(s);
        var copy = Topology.TopologyClone.Deep(s);
        Topology.TopologyConventions.Normalize(copy);
        return Topology.AnalyticVolume.Of(copy);
    }

    /// <summary>
    /// Разбор (переход на аналитическое ядро): вход каждой булевой — операнды и вид операции (замер покрытия
    /// «плоскость + квадрики»). null — выключено.
    /// </summary>
    [ThreadStatic] public static Action<IReadOnlyList<TSolid>, TSolid, BooleanKind>? EvaluateProbe;

    /// <summary>KRN-03: многотельная семантика (P3D_MULTIBODY=0 — прежнее: операция над первым/единственным задетым телом).</summary>
    static readonly bool MultiBodyEach = Environment.GetEnvironmentVariable("P3D_MULTIBODY") != "0";
    /// <summary>Мутация P3D_UNITELOOP=0: присоединение к слитому набору из нескольких тел — снова многотельным путём (прежде).</summary>
    static readonly bool UniteLoopOff = Environment.GetEnvironmentVariable("P3D_UNITELOOP") == "0";

    /// <summary>
    /// KRN-03: операция над набором тел сводится к операциям над одним телом. Вычитание — каждое тело, задетое резцом по
    /// габариту, отдельно (прочие — как есть); пересечение — каждое тело отдельно (не задетые и пустые исчезают);
    /// объединение — резец последовательно сливается со всеми задетыми телами в одно (не задел ни одного — отдельное тело).
    /// Отказ любой части — отказ всей операции; вход не меняется.
    /// </summary>
    static OperationResult EvaluateMany(IReadOnlyList<TSolid> bodies, TSolid other, BooleanKind kind, OperationPolicy policy,
                                        List<string> warn, OperationResult result)
    {
        var tb = BoxOf(other);
        var hit = bodies.Select(b => Overlap(BoxOf(b), tb)).ToArray();
        var outBodies = new List<TSolid>(); var levels = new List<GuaranteeLevel>();
        bool approx = false; AccuracyReport? acc = null;
        OperationResult? failed = null;
        OperationResult Sub(IReadOnlyList<TSolid> bs, TSolid t, BooleanKind k)
        {
            var r = Evaluate(bs, t, k, policy, warn);
            foreach (var d in r.Diagnostics) result.Diagnostics.Add(d with { Stage = "body/" + d.Stage });
            if (r.Status == OperationStatus.Failed) { failed = r; return r; }
            if (r.Bodies.Count > 0) levels.Add(r.Guarantee);
            approx |= r.Status == OperationStatus.SucceededApproximate; result.FallbackUsed |= r.FallbackUsed;
            if (r.Accuracy is { } a && (acc == null || a.EntityTolerance > acc.EntityTolerance || (a.Measured ?? 0) > (acc.Measured ?? 0))) acc = a;
            return r;
        }
        switch (kind)
        {
            case BooleanKind.Subtract:
                for (int i = 0; i < bodies.Count && failed == null; i++)
                {
                    if (!hit[i]) { outBodies.Add(bodies[i]); levels.Add(GuaranteeOf(bodies[i])); continue; }
                    var r = Sub(new[] { bodies[i] }, other, kind); if (failed != null) break;
                    // резец задел габарит, но не материал (регуляризованная разность: объём пересечения — ноль в бюджете) —
                    // тело то же: исходный объект с его состоянием, а не пересобранная копия
                    if (Unchanged(bodies[i], r.Bodies)) { outBodies.Add(bodies[i]); levels[^1] = GuaranteeOf(bodies[i]); continue; }
                    outBodies.AddRange(r.Bodies);
                }
                break;
            case BooleanKind.Intersect:
                for (int i = 0; i < bodies.Count && failed == null; i++)
                {
                    if (!hit[i]) continue;
                    var r = Sub(new[] { bodies[i] }, other, kind); if (failed != null) break;
                    outBodies.AddRange(r.Bodies);
                }
                break;
            default:
            {
                // первое задетое тело — первым операндом (как в однотельном пути: от порядка операндов зависит представление
                // результата — порядок граней, сетка), следующие — присоединяются к уже слитому
                List<TSolid>? merged = null; int at = -1;
                for (int i = 0; i < bodies.Count && failed == null; i++)
                {
                    if (!hit[i]) { outBodies.Add(bodies[i]); levels.Add(GuaranteeOf(bodies[i])); continue; }
                    if (at < 0) at = outBodies.Count;
                    if (merged == null || UniteLoopOff) { var r = merged == null ? Sub(new[] { bodies[i] }, other, kind) : Sub(merged, bodies[i], kind); if (failed != null) break; merged = r.Bodies.ToList(); continue; }
                    // KRN-05: присоединение следующего тела — к каждому телу слитого набора ПО ОДНОМУ (однотельные операции). Прежде
                    // слитый набор из нескольких тел (касание: тела остаются отдельными) шёл снова в многотельный путь, и тройка
                    // взаимно касающихся тел ходила по кругу (d9_0021: 2481 вызов точного маршрута за одну операцию, предел
                    // времени). Мутация P3D_UNITELOOP=0.
                    var cur = bodies[i]; var rest = new List<TSolid>();
                    foreach (var m in merged)
                    {
                        if (failed != null) break;
                        if (!Overlap(BoxOf(m), BoxOf(cur))) { rest.Add(m); continue; }
                        var r = Sub(new[] { m }, cur, kind); if (failed != null) break;
                        if (r.Bodies.Count == 1) { cur = r.Bodies[0]; continue; }
                        // тела не слились (касание, раздельные куски): последний — присоединяемое, прочие — остаются в наборе
                        for (int j = 0; j + 1 < r.Bodies.Count; j++) rest.Add(r.Bodies[j]);
                        if (r.Bodies.Count > 0) cur = r.Bodies[^1];
                    }
                    if (failed != null) break;
                    rest.Add(cur); merged = rest;
                }
                merged ??= new List<TSolid>();
                // резец не задел ни одного тела — отдельное тело через обычный путь (нормализация, проверка, уровень)
                if (failed == null && at < 0) { var r = Sub(Array.Empty<TSolid>(), other, kind); if (failed == null) merged = r.Bodies.ToList(); }
                if (failed == null) outBodies.InsertRange(at < 0 ? outBodies.Count : at, merged);
                break;
            }
        }
        if (failed is { } f)
        {
            result.Status = OperationStatus.Failed; result.FailedStage = "body/" + f.FailedStage; result.FailureCode = f.FailureCode;
            result.Bodies = Array.Empty<TSolid>(); result.Guarantee = GuaranteeLevel.Unspecified; result.Outcome = f.Outcome;
            EvaluateOutcomeProbe?.Invoke("Failed:" + f.FailureCode);
            return result;
        }
        result.Bodies = outBodies;
        result.Guarantee = approx || levels.Contains(GuaranteeLevel.Approximate) ? GuaranteeLevel.Approximate
            : levels.Count > 0 ? levels.Min() : GuaranteeLevel.Unspecified;
        result.Status = approx ? OperationStatus.SucceededApproximate : OperationStatus.Succeeded;
        result.Outcome = outBodies.Count == 0 ? OperationOutcome.Empty : OperationOutcome.Success;
        result.Accuracy = acc;
        EvaluateOutcomeProbe?.Invoke(result.Guarantee.ToString());
        return result;
    }

    /// <summary>
    /// KRN-03: результат над одним телом совпал с ним — одно тело, тот же объём в бюджете модельной точности (|ΔV| ≤ площадь ×
    /// точность). Для регуляризованной разности это и есть доказательство: удалённый объём — ноль.
    /// </summary>
    static bool Unchanged(TSolid input, IReadOnlyList<TSolid> result)
    {
        if (result.Count != 1) return false;
        double? v0 = Topology.AnalyticVolume.Of(input), v1 = Topology.AnalyticVolume.Of(result[0]);
        double? a0 = Topology.AnalyticVolume.AreaOf(input);
        if (v0 == null || v1 == null || a0 == null) return false;
        return Math.Abs(v1.Value - v0.Value) <= a0.Value * ToleranceContext.Current.ModelAccuracy;
    }

    static readonly bool TouchOff = Environment.GetEnvironmentVariable("P3D_TOUCHPOLICY") == "0";

    /// <summary>
    /// KRN-05 (05.4): ПОЛИТИКА КАСАНИЙ — РЕГУЛЯРИЗОВАННАЯ БУЛЕВА. Касание без общего объёма (точка, линия) материал не
    /// меняет: пересечение — Empty, вычитание — тело без изменений, объединение — тела остаются ОТДЕЛЬНЫМИ (неманифолдное
    /// тело «по ребру» не строится). Прежде объединение брусков, касающихся ребром, отказывало внутренним сбоем
    /// (invalid-result), а касание вершиной или цилиндра, лежащего на бруске, уходило в многогранный путь — точные тела
    /// становились гранёными, хотя материал не менялся. Теперь при отказе или понижении объединения проверяется
    /// пересечение: если оно доказано пусто точным маршрутом — результат — исходные тела, точные, с пометкой о касании.
    /// Мутация P3D_TOUCHPOLICY=0.
    /// </summary>
    public static OperationResult Evaluate(IReadOnlyList<TSolid> bodies, TSolid other, BooleanKind kind,
                                           OperationPolicy policy, List<string>? warnings = null)
    {
        OperationResult r; using (Diagnostics.Prof.Time("булева: операция целиком")) r = EvaluateCore(bodies, other, kind, policy, warnings);
        if (TouchOff || kind != BooleanKind.Unite || bodies.Count != 1) return r;
        if (r.Accepted && r.Bodies.Count == 1) return r;
        var inputs = new[] { bodies[0], other };
        bool exactInputs = inputs.All(b => b.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.Unspecified)
                           && inputs.All(b => { var f = GeometryAudit.Faceted(b); return f.Faces == 0 && f.Edges == 0; });
        if (r.Accepted && (!exactInputs || r.Guarantee == GuaranteeLevel.ExactAnalytic)) return r;
        var probe = EvaluateCore(bodies, other, BooleanKind.Intersect, policy, new List<string>());
        if (!(probe.Accepted && probe.Outcome == OperationOutcome.Empty && probe.Guarantee == GuaranteeLevel.ExactAnalytic)) return r;
        var res = new OperationResult { Operation = "Boolean." + kind, Policy = policy };
        res.EffectiveSettings = KernelSettings.Capture();
        res.RequestedGeometryPolicy = res.EffectiveSettings.GeometryPolicy;
        res.InputRevisions = inputs.Select(b => (b.Id, b.Revision)).ToList();
        res.Status = OperationStatus.Succeeded;
        res.Guarantee = exactInputs ? GuaranteeLevel.ExactAnalytic : r.Guarantee;
        res.Bodies = new List<TSolid> { bodies[0], other };
        res.Outcome = OperationOutcome.Success;
        res.Accuracy = new AccuracyReport(inputs.Max(b => b.Edges.Select(e => e.Tolerance).DefaultIfEmpty(0).Max()), 0,
                                          ToleranceContext.Current.MaxEntityTolerance, "exact B-rep (inputs unchanged)");
        res.Diagnostics.Add(new OperationDiagnostic("touch", "contact-without-volume",
            "the bodies touch without common volume (point or line contact); kept as separate bodies (regularized result)"));
        warnings?.Add("Boolean.Unite: the bodies touch without common volume (point or line contact) — kept as separate bodies (regularized result).");
        return res;
    }

    private static OperationResult EvaluateCore(IReadOnlyList<TSolid> bodies, TSolid other, BooleanKind kind,
                                           OperationPolicy policy, List<string>? warnings = null)
    {
        EvaluateProbe?.Invoke(bodies, other, kind);
        // g.04 (01.12): вся операция — с гранением CAD (ToleranceContext.FacetChord/FacetAngle), а не с хордой отображения:
        // фасеты результатов (по ним строится многогранная форма следующих операций) не зависят от настройки экрана.
        // Мутация P3D_FACETSCOPE=0.
        using var _fs = FacetingScope();
        var result = new OperationResult { Operation = "Boolean." + kind, Policy = policy };
        result.EffectiveSettings = KernelSettings.Capture();
        result.RequestedGeometryPolicy = result.EffectiveSettings.GeometryPolicy;
        result.InputRevisions = bodies.Append(other).Select(b => (b.Id, b.Revision)).ToList();
        var work = new List<TSolid>(bodies);
        var warn = warnings ?? new List<string>();

        OperationResult Fail(string stage, string code, string message, uint? element = null)
        {
            result.Diagnostics.Add(new OperationDiagnostic(stage, code, message, element));
            result.Status = OperationStatus.Failed;
            result.FailedStage = stage;
            result.FailureCode = code;
            result.Bodies = Array.Empty<TSolid>();
            result.Guarantee = GuaranteeLevel.Unspecified;
            result.Outcome = OperationResult.OutcomeOf(code);
            // KRN-01 (g.10): отказ приближённого пути после ИСКЛЮЧЕНИЯ точного (exact-failed) — сбой расчёта, а не неподдержанный
            // случай: следующий расчёт обязан попробовать снова (прежде — Unsupported, и сбой запоминался как итог).
            if (SplitFaceFailure && code == OperationCodes.FallbackRefused && result.Diagnostics.Any(x => x.Code == OperationCodes.ExactFailed))
                result.Outcome = OperationOutcome.InternalFailure;
            EvaluateOutcomeProbe?.Invoke("Failed:" + code);
            return result;
        }

        // KRN-03: НЕСКОЛЬКО ТЕЛ — КАЖДОЕ СВОЕЙ ОПЕРАЦИЕЙ. Многогранные ядра (UniteCore/SubtractCore/IntersectCore) работают с
        // bodies[0]: после разделения результата на тела вычитание через две части резало одну, объединение мостом
        // присоединяло мост к одному телу, пересечение оставляло второе тело целым — молча неверная форма.
        if (MultiBodyEach && work.Count > 1) return EvaluateMany(bodies, other, kind, policy, warn, result);

        bool approximateInput = other.Guarantee == GuaranteeLevel.Approximate ||
                                (work.Count > 0 && work[0].Guarantee == GuaranteeLevel.Approximate);
        if (approximateInput && policy == OperationPolicy.Strict)
            return Fail("input", OperationCodes.InputApproximate,
                        "an operand was produced by an approximate route; strict policy refuses to build on it");

        using (var trace = new OperationTrace { Policy = policy })
        {
            try
            {
                // АНАЛИТИЧЕСКИЙ МАРШРУТ (аналитическое ядро, этап 1) — перед многогранным: два 2.5-мерных тела с общим
                // направлением. Принимается, только если тело проходит ПОЛНУЮ проверку (B-rep и сетка) и его
                // аналитический объём совпадает с объёмом по слоям; иначе — многогранный путь, причина — в трассе.
                // Несколько тел (Unite, Subtract): если резец по габариту касается ровно одного тела, аналитическая
                // операция — над ним, остальные — как есть (скругление при удалённом втором теле иначе уходило
                // многогранным путём по ленивой форме: резец из точных вершин против формы на сетке — щепки 1e-6 мм).
                int target = -1;
                if (AnalyticPrism && work.Count == 1) target = 0;
                else if (AnalyticPrism && work.Count > 1 && kind != BooleanKind.Intersect)
                {
                    var tb = BoxOf(other);
                    var hits = Enumerable.Range(0, work.Count).Where(i => Overlap(BoxOf(work[i]), tb)).ToList();
                    if (hits.Count == 1) target = hits[0];
                }
                TSolid? prismR; using (Diagnostics.Prof.Time("булева: маршрут призмы целиком")) prismR = target >= 0 ? TryAnalyticPrism(work[target], other, kind, warn) : null;
                if (target >= 0 && (prismR ?? (AnalyticGeneral ? TryAnalyticGeneral(work[target], other, kind, warn) : null)) is { } exactResult)
                {
                    if (ReferenceEquals(exactResult, ProvedEmpty)) { work.RemoveAt(target); warn.Add("Boolean: general analytic path — the result is empty (proved)."); }
                    else work[target] = exactResult;
                    trace.RouteLevel = GuaranteeLevel.ExactAnalytic;
                }
                else
                {
                    switch (kind)
                    {
                        case BooleanKind.Unite: UniteCore(work, other, warn); break;
                        case BooleanKind.Subtract: SubtractCore(work, other, warn); break;
                        default: IntersectCore(work, other, warn); break;
                    }
                }
            }
            catch (OperationFailedException inner)
            {
                result.Diagnostics.AddRange(trace.Events);
                foreach (var d in inner.Result.Diagnostics) result.Diagnostics.Add(d with { Stage = "nested/" + d.Stage });
                return Fail("nested/" + inner.Result.FailedStage, inner.Result.FailureCode ?? OperationCodes.Exception, inner.Message);
            }
            catch (Exception ex) when (trace.FaceMissing && !KernelContext.IsCancellation(ex))
            {
                // Первопричина — неполный вход; следствие (отказ точного пути, исключение) вторично.
                result.Diagnostics.AddRange(trace.Events);
                var d = trace.Events.First(e => e.Code is OperationCodes.FaceMissing or OperationCodes.FaceTessellationFailed);
                return Fail(d.Stage, d.Code, "input body is incomplete: " + d.Message, d.Element);
            }
            catch (StrictRefusalException ex)
            {
                result.Diagnostics.AddRange(trace.Events);
                return Fail(trace.Stage, OperationCodes.FallbackRefused, ex.Message);
            }
            catch (Exception ex) when (!KernelContext.IsCancellation(ex))
            {
                result.Diagnostics.AddRange(trace.Events);
                return Fail(trace.Stage, OperationCodes.Exception, ex.Message);
            }

            result.Diagnostics.AddRange(trace.Events);

            if (trace.FaceMissing)
            {
                var d = trace.Events.First(e => e.Code is OperationCodes.FaceMissing or OperationCodes.FaceTessellationFailed);
                return Fail(d.Stage, d.Code, "input body is incomplete: " + d.Message, d.Element);
            }
            if (trace.Events.FirstOrDefault(e => e.Code == OperationCodes.OpenResult) is { } open)
                return Fail(open.Stage, OperationCodes.OpenResult, "result is not closed: " + open.Message);

            bool approximate = trace.Fallback || trace.Approximate || approximateInput;
            if (approximate && policy == OperationPolicy.Strict)
                return Fail(trace.Stage, OperationCodes.FallbackRefused,
                            "the exact route did not produce the result and strict policy refuses the approximate one");

            // Новые тела результата — те, которых не было среди входа.
            // K02: первое тело дерева (объединение с пустым списком) — это сам операнд; прежде он
            // проходил без проверки, и негодное тело выдавливания, вращения, протяжки или лофта
            // становилось основой детали.
            // KRN-03: НОВЫЕ ТЕЛА — ПО КОМПОНЕНТАМ. Раздельные куски результата — отдельные тела; полость — внутренняя оболочка
            // своего тела (прежде всё — одна оболочка одного тела). Входные тела не трогаются.
            if (SplitComponents)
                for (int i = work.Count - 1; i >= 0; i--)
                {
                    var b = work[i];
                    if (bodies.Any(x => ReferenceEquals(x, b)) || (ReferenceEquals(b, other) && bodies.Count > 0) || b.IsPublished) continue;
                    Topology.ShellComponents.Outcome split; using (Diagnostics.Prof.Time("булева: компоненты результата")) split = Topology.ShellComponents.Split(b);
                    if (split.Warning != null) result.Diagnostics.Add(new OperationDiagnostic("components", "components", split.Warning));
                    if (split.Bodies.Count == 1 && ReferenceEquals(split.Bodies[0], b)) continue;
                    work.RemoveAt(i); work.InsertRange(i, split.Bodies);
                }
            var fresh = work.Where(b => !bodies.Any(x => ReferenceEquals(x, b)) && (!ReferenceEquals(b, other) || bodies.Count == 0)).ToList();

            foreach (var b in fresh)
            {
                // K05: опубликованное тело приводится к соглашению о полурёбрах до проверки.
                if (b.TopologyConvention < Topology.TopologyConventions.Version) using (Diagnostics.Prof.Time("булева: соглашение результата")) Topology.TopologyConventions.Normalize(b);
                // g.13: тело предпросмотра (проверки маршрута отложены) — и эта проверка при принятии (P3D_PREVIEWPRISM=0 — сразу)
                if (PreviewMode && !PreviewPrismOff && b.PendingAcceptance is { } pending)
                {
                    var bb = b;
                    b.PendingAcceptance = () => pending() ?? (Topology.SolidValidator.ValidateBRep(bb) is { IsValid: false } bad ? "result body failed B-rep validation: " + bad.Summary() : null);
                    continue;
                }
                Topology.ValidationReport v; using (Diagnostics.Prof.Time("булева: проверка результата")) v = Topology.SolidValidator.ValidateBRep(b);
                if (!v.IsValid)
                {
                    var first = v.Issues.First(i => i.Severity == Topology.ValidationSeverity.Error);
                    LastInvalidResult = b;          // для разбора отказа (воспроизводитель, тесты)
                    return Fail("validate", OperationCodes.InvalidResult, "result body failed B-rep validation: " + first, b.Id);
                }
            }

            GuaranteeLevel level = approximate ? GuaranteeLevel.Approximate
                : trace.RouteLevel ?? (fresh.Count > 0 ? fresh.Min(b => b.Guarantee) : GuaranteeLevel.Unspecified);
            if (!approximate && level == GuaranteeLevel.Approximate) level = GuaranteeLevel.Unspecified;

            // KRN-01: ПОЛИТИКА ГЕОМЕТРИИ. PreserveBRep — результат только из точных поверхностей; многогранный (форма по
            // фасетам, точность от допуска хорды) или неизвестный — отказ Unsupported, вход не тронут (work — новый список,
            // опубликованные тела заморожены). AllowBoundedFaceting — понижение аналитического операнда до фасет
            // записывается в диагностику (явное событие вместо тихой подмены).
            var gpol = result.RequestedGeometryPolicy;
            if (gpol == GeometryPolicy.PreserveBRep && level is not (GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric))
                return Fail("policy", OperationCodes.GeometryDowngradeRefused,
                    $"geometry policy PreserveBRep: the result would be {level} (built from facets, chord {Core.Tol.Chord} mm); " +
                    "the analytic routes do not support this case");
            if (gpol == GeometryPolicy.AllowBoundedFaceting && level is GuaranteeLevel.ExactPolyhedral or GuaranteeLevel.Approximate
                && bodies.Append(other).Any(b => GuaranteeOf(b) is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric))
                result.Diagnostics.Add(new OperationDiagnostic("policy", OperationCodes.GeometryDowngrade,
                    $"an analytic operand was processed through its faceted form (chord {Core.Tol.Chord} mm); the result is {level}"));

            foreach (var b in fresh) b.Guarantee = level;
            // KRN-02: ДОПУСКИ СУЩНОСТЕЙ ОГРАНИЧЕНЫ БЮДЖЕТОМ. Точный результат (ExactAnalytic/ExactQuadric): хранимый допуск рёбер и
            // вершин и измеренное отклонение кривых и вершин от поверхностей граней — не больше MaxEntityTolerance, иначе отказ
            // AccuracyExceeded (вход не тронут). Многогранный — точность гранения, явно в отчёте (замер allB: допуски до 0.019 мм
            // у 24 из 129 таких результатов принимались молча).
            if (fresh.Count > 0)
            {
                var budget = ToleranceContext.Current;
                // KRN-03: допуски сущностей точного результата — измеренные (до отчёта и проверки бюджета)
                if (level is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric) using (Diagnostics.Prof.Time("булева: допуски сущностей")) foreach (var b in fresh) Analytic.EntityTolerance.Assign(b);
                double stored = fresh.Max(b => Math.Max(b.Edges.Select(e => e.Tolerance).DefaultIfEmpty(0).Max(), b.Vertices.Select(v => v.Tolerance).DefaultIfEmpty(0).Max()));
                bool exactLevel = level is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric;
                double? measured; using (Diagnostics.Prof.Time("булева: замер отклонений")) measured = exactLevel ? fresh.Max(b => Analytic.OperandTolerance.Measure(b)) : null;
                if (!exactLevel && !FacetMeasureOff)
                {
                    // g.04: МНОГОГРАННЫЙ РЕЗУЛЬТАТ — ИЗМЕРЕННОЕ отклонение фасет от аналитических источников граней (центры
                    // треугольников и середины сторон) против обещанного гранением: max(хорда, R(1 − cos(шаг угла / 2))).
                    // Прежде — только хорда как «бюджет», без замера: у тора с шагом 7.5° отклонение больше хорды.
                    var (dev, promised) = FacetDeviation(fresh);
                    result.Accuracy = new AccuracyReport(stored, dev, Math.Max(budget.MaxEntityTolerance, promised), "faceting (measured)");
                }
                else result.Accuracy = exactLevel
                    ? new AccuracyReport(stored, measured, budget.MaxEntityTolerance, "exact B-rep")
                    : new AccuracyReport(stored, null, Math.Max(budget.MaxEntityTolerance, Core.Tol.Chord), "faceting (chord)");
                if (TolStats) Console.Error.WriteLine($"TOLSTAT {level} {kind} {result.Accuracy}");
                if (exactLevel && Math.Max(stored, measured ?? 0) > budget.MaxEntityTolerance)
                    return Fail("accuracy", OperationCodes.AccuracyExceeded,
                        $"the result exceeds the accuracy budget: {result.Accuracy}");
            }

            result.FallbackUsed = trace.Fallback;
            // KRN-01 (01.5): карта происхождения — у единственного тела результата, если построитель её записал
            if (work.Count == 1 && work[0].Provenance is { } pvm) result.ProvenanceMap = pvm;
            result.Guarantee = level;
            result.Status = approximate ? OperationStatus.SucceededApproximate : OperationStatus.Succeeded;
            result.Bodies = work;
            result.Outcome = work.Count == 0 ? OperationOutcome.Empty : OperationOutcome.Success;
            EvaluateOutcomeProbe?.Invoke(level == GuaranteeLevel.Unspecified
                ? $"Unspecified (route {(trace.RouteLevel?.ToString() ?? "none")}, fresh {fresh.Count}, kind {kind}, fresh guarantees [{string.Join(",", fresh.Select(b => b.Guarantee).Distinct())}])"
                : level.ToString());
            return result;
        }
    }

    /// <summary>
    /// ПЕРЕСЕЧЕНИЕ ТЕЛ — общая часть двух тел. Прежде операции не было вовсе: её приходилось
    /// выражать через две другие, а это и дольше, и хуже по качеству.
    ///
    /// Считается по правилу де Моргана: A ∩ B = A − (A − B). Оба действия у нас есть и
    /// проверены, поэтому пересечение получает ту же устойчивость, что вычитание, — и ту же
    /// точность, включая случаи с квадриками.
    ///
    /// Пустое пересечение — не ошибка, а ответ: тела не перекрываются, и тело обнуляется с
    /// понятным сообщением.
    /// </summary>
    private static void IntersectCore(List<TSolid> bodies, TSolid other, List<string> warnings)
    {
        if (bodies.Count == 0)
        {
            warnings.Add("Intersect: there is no body yet.");
            return;
        }

        // K03-находка. ПЕРЕСЕЧЕНИЕ — ПРЯМОЙ ТОЧНОЙ ОПЕРАЦИЕЙ. Путь A − (A − B) сводит две булевы
        // с полностью совпадающими гранями (все грани A вне B есть и в A − B), и на совпавших
        // стенках цилиндра точное тело выходило с рёбрами кратности четыре — измерено единой
        // проверкой: 6 неманифолдных рёбер Exact у дерева «вырезы + пересечение». У ядра есть
        // прямое пересечение деревьев (ExactBsp.Intersect), совпадений оно не создаёт.
        if (UseExactKernel)
        {
            var before = new HashSet<TSolid>(bodies, ReferenceEqualityComparer.Instance) { other };
            if (TryExact(bodies, other, warnings, subtract: false, intersect: true))
            {
                OperationTrace.Route(GuaranteeLevel.ExactPolyhedral);
                foreach (var b in bodies)
                {
                    if (before.Contains(b)) continue;
                    if (SnapVertices) VertexSnap.Apply(b, warnings);
                    SnapEdgeCurves(b);
                    Topology.AnalyticEdges.Repair(b, warnings);
                    MeasureTolerances(b);
                }
                return;
            }
            OperationTrace.Report(OperationCodes.ExactDeclined, "direct exact intersection failed; using A − (A − B)");
        }

        // A − B: то, что есть только в первом теле.
        var onlyFirst = new List<TSolid> { CopyOf(bodies[0]) };
        Subtract(onlyFirst, other, warnings);

        if (onlyFirst.Count == 0)
        {
            // Первое тело целиком внутри второго: пересечение — само первое тело.
            warnings.Add("Intersect: the first body lies inside the second.");
            return;
        }

        // A − (A − B): общая часть.
        Subtract(bodies, onlyFirst[0], warnings);

        if (bodies.Count == 0)
            warnings.Add("Intersect: the bodies do not overlap.");
    }

    /// <summary>Копия тела: пересечение считает промежуточное и не должно портить исходное.</summary>
    private static TSolid CopyOf(TSolid body)
    {
        var polys = ToPolygons(body, out var sources, null);
        return SolidFromPolygons(polys, sources);
    }

    private static void SubtractCore(List<TSolid> bodies, TSolid tool, List<string> warnings)
    {
        if (bodies.Count == 0)
        {
            warnings.Add("Cut: there is no material to remove yet.");
            return;
        }

        // ЦИЛИНДРИЧЕСКИЙ РЕЗЕЦ — ТОЧНЫМ ПУТЁМ, если случай подходит. Прежде такой резец
        // гранился в 55-гранную призму, и отсюда шли все измеренные погрешности: объём выреза
        // больше расчётного на 0.05%, стенка отверстия отходила от цилиндра на 5·10⁻⁶ мм.
        // Здесь вычитание распознаёт цилиндр и отдаёт его построению по квадрике; если случай
        // не подходит, тот сам отказывается, и работает обычный путь.
        if (ExactCylinderCuts && bodies.Count == 1 && CylinderOf(tool) is { } cyl)
        {
            // ПРОТЯЖЁННОСТЬ РЕЗЦА РЕШАЕТ, СКВОЗНОЕ ОТВЕРСТИЕ ИЛИ ГЛУХОЕ. Прежде здесь всегда
            // строилось сквозное, и резец, входящий в тело лишь наполовину, давал дыру насквозь:
            // у бруска 0…40 с резцом 20…60 пропадало дно кармана, и проверка не находила кольца.
            var (bodyLow, bodyHigh) = Extent(bodies[0], cyl.Axis);
            var (toolLow, toolHigh) = Extent(tool, cyl.Axis);

            bool coversLow = toolLow <= bodyLow + Core.Tol.Weld;
            bool coversHigh = toolHigh >= bodyHigh - Core.Tol.Weld;

            TSolid? exact = null;
            if (coversLow && coversHigh)
                exact = QuadricCut.Cut(bodies[0], cyl.At, cyl.Radius, warnings, cyl.Axis);
            else if (coversHigh)
            {
                // Входит сверху, дно внутри тела.
                Vector3d entry = cyl.At - cyl.Axis * Vector3d.Dot(cyl.At, cyl.Axis) + cyl.Axis * bodyHigh;
                exact = QuadricCut.CutBlind(bodies[0], entry, cyl.Radius, bodyHigh - toolLow, warnings, cyl.Axis);
            }
            else if (coversLow)
            {
                Vector3d entry = cyl.At - cyl.Axis * Vector3d.Dot(cyl.At, cyl.Axis) + cyl.Axis * bodyLow;
                exact = QuadricCut.CutBlind(bodies[0], entry, cyl.Radius, toolHigh - bodyLow, warnings, cyl.Axis);
            }
            // Резец целиком внутри тела (кольцевая полость) точным путём пока не строится.

            if (exact != null) { bodies[0] = exact; OperationTrace.Route(GuaranteeLevel.ExactQuadric); return; }
        }

        var inputs = new HashSet<TSolid>(bodies, ReferenceEqualityComparer.Instance) { tool };
        Apply(bodies, tool, warnings, subtract: true);
        foreach (var b in bodies)
        {
            if (inputs.Contains(b)) continue;
            if (SnapVertices) VertexSnap.Apply(b, warnings);
            SnapEdgeCurves(b);
            Topology.AnalyticEdges.Repair(b, warnings);
            MeasureTolerances(b);
        }
    }

    /// <summary>
    /// Выполняет операцию над ПЕРВЫМ телом детали.
    ///
    /// Остальные тела не трогаются: несколько тел в детали — это отдельная
    /// сущность («multibody»), и решать за пользователя, к какому из них
    /// применить вырез, неправильно. Когда появится выбор тела в форме
    /// операции, здесь изменится только выбор индекса.
    /// </summary>
    private static void Apply(List<TSolid> bodies, TSolid other, List<string> warnings, bool subtract)
    {
        // Операнд запоминается ДО операции и остаётся годным: ToPolygons
        // только читает тело, а результат кладётся новым объектом. Без этой
        // ссылки сверять было бы не с чем — bodies[0] к тому времени уже
        // результат.
        TSolid before = bodies[0];

        if (UseExactKernel && TryExact(bodies, other, warnings, subtract))
        {
            OperationTrace.Route(GuaranteeLevel.ExactPolyhedral);
            if (Audit) RunAudit(before, other, bodies.Count > 0 ? bodies[0] : null, subtract, warnings);
            return;
        }

        // K02. ПРИБЛИЖЁННЫЙ ПУТЬ — ПОДМЕНА МАРШРУТА, И ОНА НАЗЫВАЕТСЯ. Прежде о ней знала только
        // строка предупреждения; теперь событие видит политика операции: строгая откажет,
        // явно приближённая выдаст тело с пометкой.
        OperationTrace.RefuseFallbackIfStrict(UseExactKernel
            ? "the exact route did not produce the result; strict policy refuses the approximate CSG path"
            : "the exact kernel is disabled; strict policy refuses the approximate CSG path");
        OperationTrace.Report(OperationCodes.Fallback, UseExactKernel
            ? "exact kernel did not produce the result; approximate CSG path used"
            : "exact kernel disabled; approximate CSG path used");
        OperationTrace.Route(GuaranteeLevel.Approximate);
        OperationTrace.Enter("boolean.approximate");
        var a = ToPolygons(bodies[0], out var sourceA, warnings);
        var b = ToPolygons(other, out var sourceB, warnings);

        if (a.Count == 0 || b.Count == 0)
        {
            warnings.Add("Boolean: one of the bodies has no geometry.");
            if (!subtract) bodies.Add(other);
            return;
        }

        var sources = new Dictionary<uint, ISurface3?>(sourceA);
        foreach (var kv in sourceB) sources[kv.Key] = kv.Value;

        List<CsgPolygon> result = subtract ? Csg.Subtract(a, b) : Csg.Unite(a, b);

        if (result.Count == 0)
        {
            // Так бывает законно: вырез съел деталь целиком.
            bodies.RemoveAt(0);
            warnings.Add(subtract
                ? "Cut removed the whole body."
                : "Boolean produced an empty result.");

            // Пустой результат сверяется наравне с непустым: «съело всё»
            // бывает и правдой, и ошибкой, и отличает их как раз сверка.
            if (Audit) RunAudit(before, other, null, subtract, warnings);
            return;
        }

        OperationTrace.Enter("boolean.topology");
        bodies[0] = FromPolygons(result, sources, warnings);

        if (Audit) RunAudit(before, other, bodies[0], subtract, warnings);
    }

    /// <summary>
    /// ТОЧНЫЙ ПУТЬ. Возвращает false, если точное ядро не справилось, —
    /// тогда операция идёт прежней дорогой.
    ///
    /// Реестр плоскостей берётся у первого тела, если оно уже точное;
    /// иначе заводится новый. Результат получает точную форму с собой,
    /// и следующая операция не округляет заново.
    /// </summary>
    /// <summary>Локальная булева включена (выключается для замеров и разбора).</summary>
    private static bool _gLocalizeBooleans = true;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool LocalizeBooleans
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.LocalizeBooleans : _gLocalizeBooleans;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gLocalizeBooleans, value, "BooleanOp.LocalizeBooleans");
    }

    /// <summary>
    /// УСКОРЕНИЕ: булева только в коробке вокруг резца (см. ExactBsp.LocalInBox).
    ///
    /// Резец — тор у края, клин вдоль ребра — занимает малую область, а полная булева
    /// проталкивала его через плоскости всего тела. Коробка — габарит резца с запасом;
    /// если грань тела лежит в плоскости коробки, коробка раздвигается. Null — если
    /// подобрать коробку не удалось: тогда полная булева.
    /// </summary>
    private static List<Exact.ExactPolygon>? LocalBoolean(Exact.PlaneRegistry R, IReadOnlyList<Exact.ExactPolygon> A,
                                                          IReadOnlyList<Exact.ExactPolygon> B, bool subtract, List<string> warnings)
    {
        if (!LocalizeBooleans || A.Count == 0 || B.Count == 0) return null;

        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var p in B)
            foreach (int v in p.Vertices(R))
            {
                var q = R.ToMm(v);
                x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); z0 = Math.Min(z0, q.Z);
                x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); z1 = Math.Max(z1, q.Z);
            }

        var aPlanes = new HashSet<int>(A.Select(p => p.Support.Id));
        double size = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
        double margin = Math.Max(1e-3, size * 1e-4);

        for (int attempt = 0; attempt < 4; attempt++, margin *= 7.3)
        {
            TSolid boxSolid = BoxSolid(x0 - margin, y0 - margin, z0 - margin, x1 + margin, y1 + margin, z1 + margin);
            var box = Exact.ExactAdapter.ToExact(boxSolid, R, warnings).Solid.Polygons;
            var boxPlanes = new HashSet<int>(box.Select(p => p.Support.Id));

            // Грань тела в плоскости коробки: крышку от грани не отличить — раздвинуть коробку.
            if (boxPlanes.Overlaps(aPlanes)) continue;

            if (Environment.GetEnvironmentVariable("P3D_NOBOX") != null)
                return subtract ? Exact.ExactBsp.Subtract(R, A, B) : Exact.ExactBsp.Union(R, A, B);
            return Exact.ExactBsp.LocalInBox(R, A, B, box, boxPlanes, subtract);
        }

        return null;
    }

    private static TSolid BoxSolid(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var plane = new Frame3(new Vector3d(0, 0, z0), Vector3d.UnitX, Vector3d.UnitY);
        Vector3d P(double x, double y) => plane.ToWorld(x, y);
        var c = new List<ICurve3>
        {
            new LineCurve3(P(x0, y0), P(x1, y0)), new LineCurve3(P(x1, y0), P(x1, y1)),
            new LineCurve3(P(x1, y1), P(x0, y1)), new LineCurve3(P(x0, y1), P(x0, y0))
        };
        return ExtrudeOp.Build(ProfileBuilder.Build(plane, c), new Vector3d(0, 0, z1 - z0));
    }

    private static bool TryExact(List<TSolid> bodies, TSolid other, List<string> warnings, bool subtract, bool intersect = false)
    {
        try
        {
            OperationTrace.Enter("boolean.exact");
            var registry = bodies[0].Exact?.Solid.Registry ?? new Exact.PlaneRegistry();

            Exact.ExactBody ea, eb;
            using (Diagnostics.Prof.Time("булева: вход тела в точную форму"))
                ea = Exact.ExactAdapter.ToExact(bodies[0], registry, warnings);
            using (Diagnostics.Prof.Time("булева: вход резца в точную форму"))
            {
                // A06: analytically coincident planar contacts of a tilted
                // extrusion share the body's Exact planes. Do not mutate the
                // caller's operand/cache; a declined candidate uses normal input.
                if(other.Exact == null && other.Faces.All(f=>f.Surface.IsPlanar) &&
                   other.Faces.Select(f=>f.Surface).OfType<ExtrudedSurface>().Any(s=>
                       (s.Dir.X!=0?1:0)+(s.Dir.Y!=0?1:0)+(s.Dir.Z!=0?1:0)>1) &&
                   Exact.ExactContactInput.TryBuild(other,ea,out var contact))
                    eb = new Exact.ExactBody(contact!.Solid.Import(registry),contact.Sources);
                else eb = Exact.ExactAdapter.ToExact(other, registry, warnings);
            }

            KernelContext.CheckPolygons(ea.Solid.Polygons.Count + eb.Solid.Polygons.Count, "boolean.input");
            if (ea.Solid.Polygons.Count == 0 || eb.Solid.Polygons.Count == 0)
            {
                OperationTrace.Report(OperationCodes.ExactDeclined, "an operand has no exact polygons");
                return false;
            }

            List<Exact.ExactPolygon> polys;
            SizeDebug?.Invoke($"многоугольников: тело {ea.Solid.Polygons.Count}, резец {eb.Solid.Polygons.Count}");
            using (Diagnostics.Prof.Time("булева: разбиение пространства"))
                polys = intersect
                        ? Exact.ExactBsp.Intersect(registry, ea.Solid.Polygons, eb.Solid.Polygons)
                        : LocalBoolean(registry, ea.Solid.Polygons, eb.Solid.Polygons, subtract, warnings)
                        ?? (subtract
                            ? Exact.ExactBsp.Subtract(registry, ea.Solid.Polygons, eb.Solid.Polygons)
                            : Exact.ExactBsp.Union(registry, ea.Solid.Polygons, eb.Solid.Polygons));

            KernelContext.CheckPolygons(polys.Count, "boolean.result");
            var sources = new Dictionary<uint, ISurface3?>(ea.Sources);
            foreach (var kv in eb.Sources) sources[kv.Key] = kv.Value;

            if (polys.Count == 0)
            {
                bodies.RemoveAt(0);
                warnings.Add(intersect ? "Intersect: the bodies do not overlap."
                             : subtract ? "Cut removed the whole body." : "Boolean produced an empty result.");
                return true;
            }

            // Куски, на которые булева разрезала грани, сливаются обратно,
            // где объединение выпукло: иначе дробление копится от операции
            // к операции.
            // СЧЁТЧИКИ ПЕРЕДАЮТСЯ ПРИРАЩЕНИЕМ, А НЕ ПОЛНЫМ ЗНАЧЕНИЕМ. Справочник ведёт счёт
            // нарастающим итогом за всё построение, а получатель складывает поданное — и вклад
            // ранних операций учитывался заново на каждой следующей. На восьми отверстиях профиль
            // показывал 2 321 333 обращения к дробному предикату при настоящих 558 006: вчетверо
            // больше. По таким числам легко выбрать не ту цель для ускорения, что со мной дважды
            // и случилось.
            if (Diagnostics.Prof.Enabled)
            {
                Diagnostics.Prof.Count("предикат: без арифметики", registry.ShortcutHits - _wasShortcut);
                Diagnostics.Prof.Count("предикат: double", registry.Tier1 - _wasTier1);
                Diagnostics.Prof.Count("предикат: Int128", registry.Tier2 - _wasTier2);
                Diagnostics.Prof.Count("предикат: длинная", registry.Tier3 - _wasTier3);
                Diagnostics.Prof.Count("вершин по трём плоскостям", registry.VertexCalls - _wasVertex);

                _wasShortcut = registry.ShortcutHits;
                _wasTier1 = registry.Tier1;
                _wasTier2 = registry.Tier2;
                _wasTier3 = registry.Tier3;
                _wasVertex = registry.VertexCalls;
                Diagnostics.Prof.Count("многоугольников: тело", ea.Solid.Polygons.Count);
                Diagnostics.Prof.Count("многоугольников: резец", eb.Solid.Polygons.Count);
                Diagnostics.Prof.Count("многоугольников: результат", polys.Count);
            }

            List<Exact.ExactPolygon> merged;
            using (Diagnostics.Prof.Time("булева: слияние кусков граней"))
                merged = Exact.ExactMerge.Fragments(registry, polys);

            // Осколки ничтожного объёма — артефакт гранения (см. DropSlivers).
            int slivers;
            List<int>[]? rings;
            using (Diagnostics.Prof.Time("булева: поиск осколков"))
                merged = Exact.ExactAdapter.DropSlivers(registry, merged, 1e-6, out slivers, out rings);
            if (slivers > 0)
                warnings?.Add($"Boolean: {slivers} detached sliver piece(s) of negligible volume removed.");

            var exactResult = new Exact.ExactSolid(registry, merged);

            List<CsgPolygon> csg;
            using (Diagnostics.Prof.Time("экран: кольца и слияние граней (ToCsg)"))
                csg = Exact.ExactAdapter.ToCsg(exactResult, sources, rings);
            TSolid result;
            OperationTrace.Enter("boolean.topology");
            using (Diagnostics.Prof.Time("экран: сборка тела (FromPolygons)"))
                { PathDebug?.Invoke("путь: точный"); result = FromPolygons(csg, sources, warnings, tJunctionFree: true); }
            result.Exact = new Exact.ExactBody(exactResult, sources);

            bodies[0] = result;
            return true;
        }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex))
        {
            warnings.Add($"Boolean: the exact kernel could not process this operation ({ex.Message}); " +
                         "the approximate kernel was used instead.");
            OperationTrace.Report(OperationCodes.ExactFailed, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Сверяет результат с независимым объёмным ответом и складывает вывод
    /// в предупреждения.
    ///
    /// Сверка не должна ронять перестроение: она средство разбора, а не
    /// часть построения. Любой её сбой — это сообщение, а не исключение
    /// наружу.
    /// </summary>
    private static void RunAudit(TSolid a, TSolid b, TSolid? result, bool subtract,
                                 List<string> warnings)
    {
        try
        {
            var audit = Voxels.BooleanAudit.Compare(
                new List<TSolid> { a },
                new List<TSolid> { b },
                result == null ? new List<TSolid>() : new List<TSolid> { result },
                subtract ? Voxels.SetOp.Subtract : Voxels.SetOp.Unite,
                AuditDepth);

            warnings.Add(audit.Describe());
        }
        catch (Exception ex)
        {
            warnings.Add("Boolean audit did not run: " + ex.Message);
        }
    }

    // ------------------------------------------------------ тело -> полигоны

    /// <summary>
    /// Разбивает тело на плоские многоугольники с метками исходных граней
    /// и СОГЛАСОВАННОЙ ориентацией.
    ///
    /// Согласование обязательно, и это была настоящая ошибка. Разбиение
    /// пространства классифицирует «внутри-снаружи» по направлению нормалей,
    /// поэтому тело, у которого часть граней намотана внутрь, оно разбирает
    /// неверно — на выходе получаются обрывки.
    ///
    /// А намотка граней «как есть» НЕ согласована. Порядок обхода задают
    /// операции построения, и после них Tessellator специально приводит
    /// её в порядок двумя шагами — значит на неё нельзя полагаться.
    /// Признака Reversed тоже недостаточно: он говорит о нормали
    /// поверхности, а не о фактическом обходе треугольников.
    ///
    /// Проверено на кубе: с вывернутыми боковыми гранями вычитание давало
    /// объём −154 667 вместо 336 000, после согласования — ровно 336 000
    /// при любом наборе вывернутых граней.
    /// </summary>
    /// <summary>Треугольники тела с согласованной ориентацией — для точного переходника.</summary>
    internal static List<CsgPolygon> ToPolygonsInternal(TSolid solid, out Dictionary<uint, ISurface3?> sources,
                                                        List<string>? warnings)
        => ToPolygons(solid, out sources, warnings, exactInput: true);

    private static List<CsgPolygon> ToPolygons(TSolid solid, out Dictionary<uint, ISurface3?> sources,
                                               List<string>? warnings = null, bool exactInput = false)
    {
        var raw = new List<CsgPolygon>();
        sources = new Dictionary<uint, ISurface3?>();

        // ТЕЛО ПО КВАДРИКЕ РАЗМЕЧАЕТСЯ С ОБЩИМИ РЁБРАМИ. Иначе каждая грань размечает кромку
        // по-своему, и булева видит тело дырявым: измерено «258 сторон без пары на 5 гранях».
        // Точки берутся те же, что и при показе, поэтому тело и его вид совпадают.
        var sharedPoints = new Dictionary<TFace, MeshBuilder>();
        if (Tess.QMesher.Enabled && Tess.QMesher.Fits(solid))
            Tess.QMesher.TessellatePerFace(solid, sharedPoints);

        // A05. Не дробим прямые образующие наклонной призмы на входе Boolean.
        // Разная частота делений соседних граней после округления в Exact
        // превращала общее ребро в две разные ломаные и открывала оболочку.
        // Даже одинаковая густая сетка создаёт ненужные микрозигзаги при
        // округлении. Достаточно двух концов образующей; дискретизация
        // кривой профиля и её допуск остаются прежними.
        // Осевые выдавливания и согласованный путь QMesher не меняются.
        var straightGenerators = !exactInput ? new HashSet<Vector3d>() :
            solid.Faces.Select(f => f.Surface).OfType<ExtrudedSurface>()
            .Where(s => (s.Dir.X != 0 ? 1 : 0) + (s.Dir.Y != 0 ? 1 : 0) + (s.Dir.Z != 0 ? 1 : 0) > 1)
            .GroupBy(s => s.Dir).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        foreach (var face in solid.Faces)
        {
            var mb = new MeshBuilder();

            // ГРАНЬ, КОТОРАЯ НЕ РАЗБИЛАСЬ, НЕ ИСЧЕЗАЕТ МОЛЧА.
            //
            // ИСПРАВЛЕНО. Здесь стояло `catch { continue; }` без единого
            // слова. Грань, чья поверхность не сумела выдать треугольники,
            // просто выпадала из тела — и вместо ошибки получалась ДЫРА
            // в оболочке. Дальше по ней считают объём, по ней же решают,
            // что внутри тела, а что снаружи; оба ответа становятся
            // неверными, и виноватой выглядит операция, которая шла следом.
            //
            // Тихо пропускать можно то, что не влияет на результат.
            // Пропущенная грань влияет на всё.
            try
            {
                KernelContext.Checkpoint("boolean.input.face");
                FaultInjection.Hit("boolean.input.face");
                if (sharedPoints.TryGetValue(face, out var ready)) mb = ready;
                else if (face.Surface is ExtrudedSurface extrusion && straightGenerators.Contains(extrusion.Dir))
                    extrusion.TessellateFace(face, mb, subdivideAxially: false);
                else face.Surface.TessellateFace(face, mb);
            }
            catch (Exception ex) when (!KernelContext.IsCancellation(ex))
            {
                warnings?.Add($"Boolean: face {face.Id} ({face.Surface.GetType().Name}) " +
                              $"could not be tessellated and is missing from the body — {ex.Message}");
                // K02: неполный вход не доходит до Boolean как обычное тело — операция откажет.
                OperationTrace.Report(SplitFaceFailure ? OperationCodes.FaceTessellationFailed : OperationCodes.FaceMissing, $"face could not be tessellated: {ex.Message}", face.Id);
                continue;
            }

            var md = new MeshData();
            mb.FillInto(md);

            if (md.Indices.Length < 3)
            {
                warnings?.Add($"Boolean: face {face.Id} ({face.Surface.GetType().Name}) " +
                              "produced no triangles and is missing from the body");
                OperationTrace.Report(OperationCodes.FaceMissing, "face produced no triangles", face.Id);
                continue;
            }

            // У гранёной поверхности исходная запоминается её собственная,
            // иначе после второй операции подряд аналитика была бы потеряна.
            sources[face.Id] = face.Surface is FacetedSurface fs ? fs.Source : face.Surface;
            // УСТОЙЧИВЫЕ ИМЕНА: подпись исходной грани переносится на её потомков.
            if (face.Tag.Length > 0) TagNames[face.Id] = face.Tag;

            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
            {
                // Координаты в double из MeshBuilder, а не float из MeshData.
                // Прежде здесь стоял At(md, …) — перевод из float, и булева
                // работала на 32-битных координатах.
                Vector3d p0 = mb.PositionsD[md.Indices[i]];
                Vector3d p1 = mb.PositionsD[md.Indices[i + 1]];
                Vector3d p2 = mb.PositionsD[md.Indices[i + 2]];

                if (Degenerate(p0, p1, p2)) continue;

                raw.Add(new CsgPolygon(new List<Vector3d> { p0, p1, p2 }, face.Id));
            }
        }

        return Orient(raw);
    }

    /// <summary>
    /// Приводит намотку всех треугольников к единой, наружу тела.
    ///
    /// Два шага, и порядок важен.
    ///
    /// 1. Обход по СМЕЖНОСТИ. У согласованных соседей общая сторона
    ///    проходится в противоположных направлениях; если они совпали,
    ///    сосед развёрнут. Так согласуется вся связная оболочка,
    ///    независимо от того, как её намотали операции построения.
    ///
    /// 2. Знак объёма. Он ловит случай, когда оболочка согласована,
    ///    но вывернута целиком. Раньше этого шага было бы недостаточно:
    ///    при ЧАСТИЧНО неверной намотке знаковый объём сам врёт
    ///    (на проверке он давал −133 333 вместо 400 000).
    /// </summary>
    private static List<CsgPolygon> Orient(List<CsgPolygon> polys)
    {
        if (polys.Count == 0) return polys;

        var weld = new VertexWeld(WeldTol);
        var tris = new List<(int A, int B, int C, uint Tag)>(polys.Count);

        foreach (var p in polys)
        {
            int i0 = weld.Add(p.V[0]);
            for (int i = 1; i + 1 < p.V.Count; i++)
                tris.Add((i0, weld.Add(p.V[i]), weld.Add(p.V[i + 1]), p.Tag));
        }

        // Смежность по неупорядоченным сторонам.
        var byEdge = new Dictionary<(int, int), List<(int Tri, int U, int V)>>();

        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c, _) = tris[t];
            Register(a, b); Register(b, c); Register(c, a);

            void Register(int u, int v)
            {
                var key = u < v ? (u, v) : (v, u);
                if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new();
                list.Add((t, u, v));
            }
        }

        var seen = new bool[tris.Count];
        var flip = new bool[tris.Count];
        var stack = new Stack<int>();

        for (int start = 0; start < tris.Count; start++)
        {
            if (seen[start]) continue;

            seen[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                int t = stack.Pop();
                var (a, b, c, _) = tris[t];

                Walk(a, b); Walk(b, c); Walk(c, a);

                void Walk(int u, int v)
                {
                    if (flip[t]) (u, v) = (v, u);

                    var key = u < v ? (u, v) : (v, u);
                    if (!byEdge.TryGetValue(key, out var list)) return;

                    foreach (var (t2, u2, v2) in list)
                    {
                        if (t2 == t || seen[t2]) continue;

                        int a2 = u2, b2 = v2;
                        if (flip[t2]) (a2, b2) = (b2, a2);

                        // Совпало направление — сосед намотан наоборот.
                        if (a2 == u && b2 == v) flip[t2] = !flip[t2];

                        seen[t2] = true;
                        stack.Push(t2);
                    }
                }
            }
        }

        var result = new List<CsgPolygon>(tris.Count);

        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c, tag) = tris[t];
            Vector3d p0 = weld.Points[a], p1 = weld.Points[b], p2 = weld.Points[c];

            if (flip[t]) (p1, p2) = (p2, p1);
            if (Degenerate(p0, p1, p2)) continue;

            result.Add(new CsgPolygon(new List<Vector3d> { p0, p1, p2 }, tag));
        }

        // A04. Малая деталь далеко от нуля: глобальные тройные произведения
        // теряли знак объёма (R0.5, H0.01, центр 60000,-40000,20000).
        // Для замкнутой оболочки перенос начала интегрирования не меняет
        // объём. Компенсируем накопление ошибки, не меняя допусков.
        double volume = 0, compensation = 0;
        Vector3d origin = result.Count > 0 ? result[0].V[0] : Vector3d.Zero;
        foreach (var p in result)
        {
            double term = Vector3d.Dot(p.V[0] - origin,
                Vector3d.Cross(p.V[1] - origin, p.V[2] - origin)) / 6;
            double y = term - compensation, sum = volume + y;
            compensation = (sum - volume) - y;
            volume = sum;
        }
        if (volume < 0)
            foreach (var p in result) p.Flip();

        return result;
    }

    private static Vector3d At(MeshData md, int i)
    {
        var p = md.Vertices[i].P;
        return new Vector3d(p.X, p.Y, p.Z);
    }

    private static bool Degenerate(Vector3d a, Vector3d b, Vector3d c) =>
        Vector3d.Cross(b - a, c - a).LengthSquared < 1e-24;

    // ------------------------------------------------------ полигоны -> тело

    /// <summary>
    /// Собирает тело из многоугольников результата.
    ///
    /// Три прохода, и порядок между ними обязателен.
    ///
    /// 1. Склейка вершин и разбиение на треугольники.
    /// 2. Устранение T-СТЫКОВ. Разбиение пространства режет соседние
    ///    многоугольники по-разному, поэтому вершина одного оказывается
    ///    посреди стороны другого. Как набор многоугольников тело при этом
    ///    замкнуто — объём верен, — но как треугольная сетка нет: на стыке
    ///    остаются щели, через которые видно внутренность, а на экране
    ///    рассыпаны лишние вершины. Проверено: до устранения 206 сторон
    ///    из 388 не имели парного треугольника, после — ни одной.
    /// 3. Границы групп, вершины и рёбра. Только ПОСЛЕ устранения стыков:
    ///    иначе границы соседних граней не совпадают, общими не считаются
    ///    и почти все отбрасываются.
    /// </summary>
    /// <summary>Тело из многоугольников (для резцов, собранных из сечений). Метки — группы граней.</summary>
    public static TSolid SolidFromPolygons(List<CsgPolygon> polys)
        => FromPolygons(polys, new Dictionary<uint, ISurface3?>(), null, tJunctionFree: false);

    /// <summary>
    /// Тело из многоугольников с точными поверхностями по меткам: грань метки получает
    /// свою исходную поверхность, и булева переносит её в результат (этап 2).
    /// </summary>
    /// Рёбра такого тела — прямые и дуги по точкам, без кривых пересечения: это резец,
    /// его рёбра никто не выбирает, а точное распознавание на нём стоило треть времени операции.
    public static TSolid SolidFromPolygons(List<CsgPolygon> polys, Dictionary<uint, ISurface3?> sources)
        => FromPolygons(polys, sources, null, tJunctionFree: false, exactEdges: false);

    /// <summary>
    /// То же, но с ПОДПИСЯМИ граней: устойчивые имена для параметрических ссылок. Подпись
    /// достаётся грани, собранной из многоугольников с этой меткой.
    /// </summary>
    public static TSolid SolidFromPolygons(List<CsgPolygon> polys, Dictionary<uint, ISurface3?> sources,
                                           Dictionary<uint, string> names)
    {
        foreach (var kv in names) TagNames[kv.Key] = kv.Value;
        return FromPolygons(polys, sources, null, tJunctionFree: false, exactEdges: false);
    }

    private static TSolid FromPolygons(List<CsgPolygon> polys, Dictionary<uint, ISurface3?> sources,
                                       List<string>? warnings = null, bool tJunctionFree = false,
                                       bool exactEdges = true)
    {
        // ПОВТОР ГРАНЕЙ — ИЗВЕСТНАЯ ПРИЧИНА НЕЗАМКНУТОСТИ, ЛЕЧЕНИЕ НЕ НАЙДЕНО. Булева
        // иногда выдаёт два одинаковых многоугольника с ОДИНАКОВЫМ обходом; из них выходят две
        // совпадающие грани (одна площадь, один центр, по шесть рёбер), и сетка тела
        // перестаёт быть замкнутой: общую сторону обе проходят вперёд. Пробовал убирать
        // повторы здесь — охват поднимался с 83 до 87 тел, но ломались фаски (11 тестов);
        // вариант «убирать только повтор между разными метками» ломал 2 теста. Значит, в
        // рациональной модели эти повторы для чего-то нужны, и разбираться надо в самой
        // булевой, а не затирать их на выходе.

        var solid = new TSolid();
        var shell = new TShell { Solid = solid };
        solid.Shells.Add(shell);

        // Вход точного ядра склеивается без допуска (см. VertexWeld.Exact).
        var weld = tJunctionFree ? VertexWeld.Exact() : new VertexWeld(WeldTol);
        var tris = new List<Tri>();
        var normals = new Dictionary<uint, Vector3d>();

        foreach (var p in polys)
        {
            normals[p.Tag] = p.N;

            int i0 = weld.Add(p.V[0]);
            for (int i = 1; i + 1 < p.V.Count; i++)
            {
                int i1 = weld.Add(p.V[i]), i2 = weld.Add(p.V[i + 1]);

                // Треугольник с совпавшими вершинами не нужен: у точного ядра две
                // разные вершины изредка переводятся в одни и те же числа double,
                // и такой треугольник давал ребро нулевой длины — скругление по нему
                // строило невесомый резец. Нашёл проверочный прогон (Parametric3D.Verify).
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;

                tris.Add(new Tri(i0, i1, i2, p.Tag));
            }
        }

        // ОДИН И ТОТ ЖЕ ТРЕУГОЛЬНИК НЕ МОЖЕТ ЛЕЖАТЬ В ОБОЛОЧКЕ ДВАЖДЫ.
        //
        // ДОБАВЛЕНО. Разбиение пространства при совпадающих плоскостях
        // изредка оставляет один и тот же кусок поверхности в результате
        // повторно. На вид это незаметно: второй экземпляр лежит ровно
        // на первом. Но замкнутой оболочки больше нет — у каждого из трёх
        // его рёбер оказывается по ТРИ треугольника вместо двух, — а по
        // такой оболочке считают объём и решают, что внутри тела.
        //
        // Отсюда и брались числа: после скругления девять рёбер кратности
        // три, то есть ровно три лишних треугольника, и объём уходил
        // на шесть единиц. После фаски полсотни рёбер и семь с половиной
        // тысяч объёма.
        //
        // Треугольник опознаётся по НАБОРУ вершин, без учёта того, с какой
        // вершины он начат: один и тот же кусок мог прийти дважды,
        // начавшись с разных углов. Направление обхода тоже не учитывается:
        // двух совпадающих кусков поверхности не бывает ни при каком
        // направлении.
        int duplicates = RemoveDuplicates(tris);

        if (duplicates > 0)
            warnings?.Add($"Boolean: {duplicates} duplicate triangle(s) dropped from the result");

        // Вход точного ядра уже без T-стыков: они сняты точно в ExactAdapter.ToCsg.
        if (!tJunctionFree) RemoveTJunctions(tris, weld);

        // Грани: по одной на метку исходной грани.
        var groups = new Dictionary<uint, List<Tri>>();
        foreach (var t in tris)
        {
            if (!groups.TryGetValue(t.Tag, out var list)) groups[t.Tag] = list = new List<Tri>();
            list.Add(t);
        }

        // ЩЕПКИ — К СОСЕДНЕЙ ГРАНИ. Булева у почти вырожденных пересечений оставляет метки
        // площадью в миллионные доли мм² (грань из трёх рёбер по шесть микрон). Отдельной
        // гранью такая щепка мешает: в сетке её вершины сливаются, треугольников не остаётся,
        // и характеристика Эйлера расходится с топологией (предупреждения SLIVER и TOPO).
        // Её треугольники приписываются той соседней метке, с которой у неё больше общих сторон.
        {
            double AreaOf(List<Tri> l)
            {
                double a = 0;
                foreach (var t in l) a += Vector3d.Cross(weld.Points[t.B] - weld.Points[t.A], weld.Points[t.C] - weld.Points[t.A]).Length / 2;
                return a;
            }
            var tiny = Environment.GetEnvironmentVariable("P3D_NOSLIVER") != null
                ? new List<uint>()
                : groups.Where(g => AreaOf(g.Value) < 1e-5).Select(g => g.Key).ToList();
            if (tiny.Count > 0 && tiny.Count < groups.Count)
            {
                // Сторона (пара вершин) → метки, которым она принадлежит.
                var side = new Dictionary<(int, int), List<uint>>();
                foreach (var (tag, list) in groups)
                    foreach (var t in list)
                        foreach (var (u, v) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                        {
                            var k = u < v ? (u, v) : (v, u);
                            if (!side.TryGetValue(k, out var l2)) side[k] = l2 = new List<uint>();
                            l2.Add(tag);
                        }
                foreach (uint tag in tiny)
                {
                    var counts = new Dictionary<uint, int>();
                    foreach (var t in groups[tag])
                        foreach (var (u, v) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                        {
                            var k = u < v ? (u, v) : (v, u);
                            foreach (uint other in side[k])
                                if (other != tag && !tiny.Contains(other)) counts[other] = counts.GetValueOrDefault(other) + 1;
                        }
                    if (counts.Count == 0) continue;
                    // Щепка уходит только к грани, на поверхности которой она лежит: иначе у
                    // хозяина ломаются нормали и гранение, и экспорт открывает стороны.
                    uint host = 0; bool found = false;
                    foreach (var cand in counts.OrderByDescending(c => c.Value).Select(c => c.Key))
                    {
                        if (!sources.TryGetValue(cand, out var hs) || hs == null || hs is FacetedSurface) continue;
                        double worst = 0;
                        foreach (var t in groups[tag])
                            foreach (int v in new[] { t.A, t.B, t.C })
                            {
                                var q = weld.Points[v];
                                worst = Math.Max(worst, (hs.PointAt(hs.ProjectToUV(q)) - q).Length);
                            }
                        if (worst < 1e-4) { host = cand; found = true; break; }
                    }
                    if (!found) continue;
                    groups[host].AddRange(groups[tag].Select(t => new Tri(t.A, t.B, t.C, host)));
                    groups.Remove(tag);
                    warnings?.Add($"Boolean: sliver face merged into a neighbour.");
                }
            }
        }

        var faces = new Dictionary<uint, TFace>();

        foreach (var (tag, list) in groups)
        {
            sources.TryGetValue(tag, out ISurface3? src);

            var surface = new FacetedSurface(src, normals.TryGetValue(tag, out var n) ? n : Vector3d.UnitZ);
            foreach (var t in list)
                surface.Facets.Add((weld.Points[t.A], weld.Points[t.B], weld.Points[t.C]));

            var face = new TFace { Surface = surface, Reversed = false, SourceTag = tag };
            if (TagNames.TryGetValue(tag, out string? nm)) face.Tag = nm;
            faces[tag] = face;
            shell.Add(face);
        }

        using (Diagnostics.Prof.Time("экран: рёбра (BuildEdges)"))
            BuildEdges(groups, faces, weld, solid, warnings, exactEdges);


        // ЗАЛЕЧИВАНИЕ ПОСЛЕ СБОРКИ. Булева оставляет рёбра длиной в доли микрона: на объём они
        // не влияют, но характеристика Эйлера тела расходится с характеристикой его сетки (на
        // прогоне 450 последовательностей — 44 предупреждения), а скругления и экспорт по таким
        // рёбрам дают брак. Включается переключателем, чтобы можно было сравнить с прежним
        // поведением.
        if (HealAfterBoolean)
            using (Diagnostics.Prof.Time("тело: залечивание"))
                Topology.TopologyHeal.Heal(solid, 0, warnings);

        if (exactEdges && ExactDisplay)
            using (Diagnostics.Prof.Time("экран: подгонка сетки (черновик, выключен)"))
                SnapDisplay(groups, faces, weld, solid);
        if (MeshDebug != null)
        {
            // Совпадающие грани: одна площадь и один центр — их не должно быть.
            var seenF = new Dictionary<string, int>();
            foreach (var f2 in solid.Faces)
            {
                if (f2.Surface is not FacetedSurface fs2 || fs2.Facets.Count == 0) continue;
                double ar2 = 0; Vector3d c2 = Vector3d.Zero;
                foreach (var (x, y, z) in fs2.Facets)
                {
                    double a2 = Vector3d.Cross(y - x, z - x).Length / 2;
                    ar2 += a2; c2 += (x + y + z) / 3 * a2;
                }
                if (ar2 <= 0) continue;
                c2 /= ar2;
                string k2 = $"{ar2:0.####}|{c2.X:0.###}|{c2.Y:0.###}|{c2.Z:0.###}";
                if (seenF.TryGetValue(k2, out int other))
                    MeshDebug($"совпадающие грани {other} и {f2.Id}: площадь {ar2:0.##}, треугольников {fs2.Facets.Count}, меток {groups.Count}");
                else seenF[k2] = (int)f2.Id;
            }
        }

        // РЁБРА НУЛЕВОЙ ДЛИНЫ УБИРАЮТСЯ ИЗ КОНТУРОВ. Такое ребро — точка: геометрии в нём нет,
        // а полурёбра есть, и у соседних граней стороны не сходятся. Убираются только ТОЧНО
        // нулевые (короче 1e-9 мм): схлопывание «микро-рёбер» с допуском 2e-4 уже пробовалось
        // и давало дыру в STL.
        if (exactEdges)
        {
            int removed = 0;
            foreach (var e10 in solid.Edges.ToList())
            {
                if ((e10.V1.P - e10.V0.P).Length > 1e-9) continue;   // порог 1e-6 пробовал: охват падал с 89 до 86
                foreach (var sh10 in solid.Shells)
                    foreach (var f10 in sh10.Faces)
                        foreach (var l10 in f10.Loops)
                            l10.CoEdges.RemoveAll(c => ReferenceEquals(c.Edge, e10));
                e10.CoEdges.Clear();
                removed++;
            }
            if (removed > 0) warnings?.Add($"Boolean: {removed} zero-length edge(s) removed.");
        }

        // СЛИЯНИЕ ПОЧТИ НУЛЕВЫХ РЁБЕР С ПЕРЕСБОРКОЙ КОНТУРОВ. Ребро короче микрона — это
        // фактически точка, но его вершины разные, и у соседних граней стороны не сходятся.
        // Просто убрать такое ребро мало (охват падал с 89 до 86): нужно СЛИТЬ его вершины и
        // пересобрать контуры затронутых граней, чтобы у всех соседей появилась общая точка.
        if (exactEdges && HealTinyEdges)
        {
            var touched = new HashSet<TFace>();
            int healed = 0;
            foreach (var e11 in solid.Edges.ToList())
            {
                double len11 = (e11.V1.P - e11.V0.P).Length;
                if (len11 < 1e-9 || len11 > 1e-6) continue;

                TVertex keep = e11.V0, drop = e11.V1;
                foreach (var other in solid.Edges)
                {
                    if (other.V0 == drop) other.V0 = keep;
                    if (other.V1 == drop) other.V1 = keep;
                }
                foreach (var sh11 in solid.Shells)
                    foreach (var f11 in sh11.Faces)
                        foreach (var l11 in f11.Loops)
                            if (l11.CoEdges.RemoveAll(c => ReferenceEquals(c.Edge, e11)) > 0) touched.Add(f11);
                e11.CoEdges.Clear();
                healed++;
            }

            if (healed > 0)
            {
                foreach (var f11 in touched)
                {
                    var cyc11 = Tess.ExactFaceMesher.Cycles(f11);
                    if (cyc11 == null || cyc11.Count == 0) continue;
                    foreach (var l11 in f11.Loops)
                        foreach (var c11 in l11.CoEdges)
                            c11.Edge.CoEdges.Remove(c11);
                    f11.Loops.Clear();
                    var lens11 = cyc11.Select(c => c.Sum(x => (x.E.V1.P - x.E.V0.P).Length)).ToList();
                    int outer11 = lens11.IndexOf(lens11.Max());
                    for (int i = 0; i < cyc11.Count; i++)
                    {
                        var loop11 = f11.AddLoop(i == outer11);
                        foreach (var (e12, rev12) in cyc11[i]) loop11.Add(e12, rev12);
                    }
                }
                warnings?.Add($"Boolean: {healed} tiny edge(s) merged, {touched.Count} face loop(s) rebuilt.");
            }
        }

        if (MeshDebug != null && exactEdges)
        {
            // Отросток: ребро, пройденное контуром дважды (туда и обратно) — след того, что
            // булева оставила в границе грани элемент, которого в её области нет.
            int spurs = 0, spurFaces = 0;
            foreach (var sh12 in solid.Shells)
                foreach (var f12 in sh12.Faces)
                {
                    var cyc12 = Tess.ExactFaceMesher.Cycles(f12);
                    if (cyc12 == null) continue;
                    int here = 0;
                    foreach (var c12 in cyc12)
                        for (int i = 0; i < c12.Count; i++)
                            if (ReferenceEquals(c12[i].E, c12[(i + 1) % c12.Count].E)) here++;
                    if (here > 0) { spurs += here; spurFaces++; }
                }

            int zeroFaces = 0, zeroEdges = 0;
            foreach (var f9 in solid.Faces)
                if (f9.Surface is FacetedSurface fs9 && fs9.Facets.Sum(t => Vector3d.Cross(t.B - t.A, t.C - t.A).Length / 2) < 1e-6) zeroFaces++;
            foreach (var e9 in solid.Edges) if ((e9.V1.P - e9.V0.P).Length < 1e-6) zeroEdges++;
            if (zeroFaces > 0 || zeroEdges > 0)
                MeshDebug($"вырождения топологии: граней нулевой площади {zeroFaces}, рёбер нулевой длины {zeroEdges}, отростков {spurs} у {spurFaces} граней, всего граней {solid.Faces.Count()}");
            else if (spurs > 0)
                MeshDebug($"вырождения топологии: отростков {spurs} у {spurFaces} граней, всего граней {solid.Faces.Count()}");
        }

        // РАЗМЕТКА ПРОМЕЖУТОЧНЫХ ТЕЛ ОТКЛАДЫВАЕТСЯ. Дерево из шести операций делает семь булевых,
        // и каждая размечала ВСЕ грани тела заново — на настоящей детали это 5.2 с из 8.0 с всего
        // перестроения. Промежуточные тела никто не видит: их сетка нужна лишь затем, чтобы
        // следующая операция имела гранёное представление, а для него хватает тех треугольников,
        // что дала сама булева.
        //
        // Точная разметка делается один раз, в конце перестроения дерева, — там, где тело
        // действительно пойдёт на экран.
        if (exactEdges && ExactMeshing && !DeferMeshing)
            using (Diagnostics.Prof.Time("экран: сетка граней по точной геометрии"))
                MeshExactlySolid(solid, warnings, PerFaceAdoption);
        return solid;
    }

    /// <summary>
    /// СХЛОПЫВАНИЕ МИКРО-РЁБЕР В ТОПОЛОГИИ. ВЫКЛЮЧЕНО: характеристику Эйлера не исправило
    /// (предупреждений TOPO осталось столько же), а в одной последовательности дало дыру в
    /// STL — грань теряла ребро, и её сетка расходилась с соседней. Убирать микро-рёбра надо
    /// вместе с пересборкой сетки соседних граней.
    /// ПРЕЖНЕЕ ОПИСАНИЕ: Булева оставляет рёбра длиной в доли микрона —
    /// след сетки точного ядра (1e-5 мм). Видимая сетка такие вершины сливает, и тогда
    /// характеристика Эйлера топологии расходится с сеткой (предупреждения TOPO: 4 без новой
    /// сетки против 22 с ней). Здесь ребро короче порога убирается: его вершины становятся
    /// одной, полурёбра выкидываются из контуров, само ребро — из тела.
    /// </summary>
    private static void CollapseMicroEdges(TSolid solid, List<string>? warnings)
    {
        const double micro = 2e-4;
        var dead = solid.Edges.Where(e => e.V0 != e.V1 && (e.V1.P - e.V0.P).Length < micro).ToList();
        if (dead.Count == 0) return;

        int done = 0;
        foreach (var e in dead)
        {
            if (e.V0 == e.V1) continue;
            TVertex keep = e.V0, drop = e.V1;
            // Все рёбра, ссылавшиеся на выкидываемую вершину, переводятся на оставшуюся.
            foreach (var other in solid.Edges)
            {
                if (ReferenceEquals(other, e)) continue;
                if (other.V0 == drop) other.V0 = keep;
                if (other.V1 == drop) other.V1 = keep;
            }
            e.V1 = keep;
            // Полурёбра схлопнувшегося ребра выкидываются из контуров.
            foreach (var shell in solid.Shells)
                foreach (var f in shell.Faces)
                    foreach (var loop in f.Loops)
                        loop.CoEdges.RemoveAll(c => ReferenceEquals(c.Edge, e));
            done++;
        }
        if (done > 0) warnings?.Add($"Boolean: {done} micro edge(s) collapsed.");
    }

    /// <summary>
    /// РАЗДЕЛЕНИЕ КОНТУРОВ ГРАНИ. ВЫКЛЮЧЕНО: даёт верную характеристику Эйлера (предупреждений
    /// TOPO 44 → 16), но ломает экспорт — 4 сбоя с открытыми сторонами. Значит, контуры
    /// используются ещё где-то и направление обхода, которое ставит эта сборка, тем местам не
    /// подходит. Возвращаться сюда после разбора, кто и как читает контуры граней.
    /// </summary>
    private static void SplitLoops(TSolid solid)
    {
        foreach (var shell in solid.Shells)
            foreach (var f in shell.Faces)
            {
                var cycles = Tess.ExactFaceMesher.Cycles(f);
                // Пересобираются и одноконтурные грани: в топологии рёбра лежали в произвольном
                // порядке, и контур нельзя было обойти.
                if (cycles == null || cycles.Count == 0) continue;
                // Только если циклы покрывают ровно все рёбра грани и каждое столько раз,
                // сколько у него полурёбер: иначе перестроение теряет полуребро, грань
                // гранится неверно, и экспорт открывает стороны.
                var have = new Dictionary<TEdge, int>();
                // Старые полурёбра снимаются и с самих рёбер: грани ребра определяются по его
                // первым двум полурёбрам, и оставленные от прежних контуров ломали всё
                // построение — отсюда дыры в экспорте при первой попытке.

                foreach (var loop0 in f.Loops)
                    foreach (var ce in loop0.CoEdges) have[ce.Edge] = have.GetValueOrDefault(ce.Edge) + 1;
                var want = new Dictionary<TEdge, int>();
                foreach (var c in cycles)
                    foreach (var (e, _) in c) want[e] = want.GetValueOrDefault(e) + 1;
                if (have.Count != want.Count || have.Any(kv => want.GetValueOrDefault(kv.Key) != kv.Value)) continue;

                var lens = cycles.Select(c => c.Sum(x => (x.E.V1.P - x.E.V0.P).Length)).ToList();
                int outer = lens.IndexOf(lens.Max());
                foreach (var loop1 in f.Loops)
                    foreach (var ce in loop1.CoEdges)
                        ce.Edge.CoEdges.Remove(ce);
                f.Loops.Clear();
                for (int i = 0; i < cycles.Count; i++)
                {
                    var loop = f.AddLoop(i == outer);
                    foreach (var (e, rev) in cycles[i]) loop.Add(e, rev);
                }
            }
    }

    /// <summary>Строить видимую сетку граней по точной геометрии (этап 3).</summary>
    private static bool _gExactMeshing = Environment.GetEnvironmentVariable("P3D_NOMESH") == null;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool ExactMeshing
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactMeshing : _gExactMeshing;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gExactMeshing, value, "BooleanOp.ExactMeshing");
    }

    /// <summary>
    /// Откладывать ли точную разметку граней. Ставится на время перестроения дерева: сетка
    /// промежуточных тел не нужна, а стоит она дороже всего остального вместе.
    /// </summary>
    private static bool _gDeferMeshing;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool DeferMeshing
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.DeferMeshing : _gDeferMeshing;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gDeferMeshing, value, "BooleanOp.DeferMeshing");
    }

    /// <summary>
    /// ВИДИМАЯ СЕТКА ТЕЛА — ПО ТОЧНОЙ ГЕОМЕТРИИ (этап 3), ВСЁ ТЕЛО ИЛИ НИЧЕГО.
    ///
    /// Каждая грань строится заново (ExactFaceMesher: точки рёбер по точным кривым, общие для
    /// соседних граней; триангуляция в параметрах точной поверхности). Если хоть одна грань не
    /// построилась или не прошла самопроверку, всё тело остаётся на треугольниках разбиения:
    /// новые и старые грани по-разному делят общий шов, и между ними были бы щели.
    /// Точное тело булевой не трогается — меняется только то, что видит пользователь.
    /// </summary>
    /// <summary>
    /// Построить видимую сетку граней по точной геометрии для ГОТОВОГО тела — например,
    /// прочитанного из обменного файла. Внутри булевой вызывается тот же расчёт.
    /// </summary>
    /// <summary>Вид поверхности грани — коротким именем, для сводки времени.</summary>
    private static string FaceKindName(TFace f)
    {
        var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
        return src?.GetType().Name.Replace("Surface", "") ?? "?";
    }

    /// <summary>Число точек — ступенями, иначе строк в сводке будет столько же, сколько граней.</summary>
    private static string Bucket(int n) =>
        n < 50 ? "<50" : n < 200 ? "50-200" : n < 800 ? "200-800" : n < 3000 ? "800-3000" : ">3000";

    static readonly bool MeshAgainDbg = Environment.GetEnvironmentVariable("P3D_MESHAGAIN") == "1";
    /// <summary>g.13: быстрые проверки сетки тела (грубый ключ повторов, стороны без владельцев); P3D_MESHCHECKFAST=0 — прежние.</summary>
    static readonly bool MeshChecksFastEnvOff = Environment.GetEnvironmentVariable("P3D_MESHCHECKFAST") == "0";
    [ThreadStatic] static bool _meshChecksOldForTests;
    internal static bool MeshChecksFastOff => MeshChecksFastEnvOff || _meshChecksOldForTests;
    /// <summary>Сравнение (KRN10-MESH-CHECKS): прежние проверки в этом потоке.</summary>
    public static bool MeshChecksFastOffForTests { get => _meshChecksOldForTests; set => _meshChecksOldForTests = value; }
    /// <summary>Замер проверок сетки (KRN10-MESH-CHECKS): разметка тела заново, без памяти о прежней разметке.</summary>
    public static void MeshExactlyForTest(TSolid solid) => MeshExactly(solid, null, new MeshRun());
    public static void MeshExactlySolid(TSolid solid, List<string>? warnings = null, bool perFace = false)
    {
        // K03: тело уже размечено с теми же настройками — повторная разметка дала бы ту же сетку
        // и переписала бы производное состояние опубликованного тела.
        //
        // Запоминается и ОТКАЗ разметки, если он детерминирован (проверка сетки отвергла её, а не
        // исключение): тело то же, настройки те же — ответ будет тем же. Измерено: тело со
        // сферическим вырезом отвергало точную сетку грани вращения при каждом перестроении,
        // 240 мс впустую, даже когда изменилось другое тело детали.
        var key = (Core.Tol.Chord, Core.Tol.ChordAngle, perFace);
        // Поставка 2: сетка — производное данной РЕВИЗИИ B-rep; правка формы после разметки делает её устаревшей.
        if (solid.DisplayMeshKey == key && solid.MeshRevision == solid.Revision) return;
        if (MeshAgainDbg) Console.Error.WriteLine($"MESHAGAIN key {solid.DisplayMeshKey?.ToString() ?? "null"} vs {key} rev {solid.MeshRevision} vs {solid.Revision}");

        bool prev = PerFaceAdoption;
        PerFaceAdoption = perFace;
        var run = new MeshRun();
        try { MeshExactly(solid, warnings, run); }
        catch { solid.DisplayMeshKey = null; throw; }
        finally { PerFaceAdoption = prev; }

        // Исключение при разметке (в том числе внедрённое) не запоминается: повторится попытка.
        solid.DisplayMeshKey = run.Faulted ? null : key;
        solid.MeshRevision = run.Faulted ? -1 : solid.Revision;
    }

    /// <summary>
    /// K08. НОРМАЛИ ОТТЕНЕНИЯ — В СТОРОНУ ТРЕУГОЛЬНИКОВ. Точный построитель берёт нормали у
    /// исходной поверхности грани, не учитывая, что после булевой грань может смотреть против
    /// этой поверхности (дно бруска: плоскость с W вверх, а грань наружу вниз). Треугольники
    /// при этом ориентированы верно, а нормали — внутрь; показная сетка затем разворачивала
    /// треугольники «по нормалям», и дно отверстия под наклоном выходило вывернутым (110–132
    /// сторон с одинаковым обходом; найдено проверкой авторитетной геометрии K08). Нормаль,
    /// противоречащая своему треугольнику, разворачивается; треугольники не трогаются.
    /// </summary>
    private static List<(Vector3d A, Vector3d B, Vector3d C)>? AgreeNormals(
        List<(Vector3d A, Vector3d B, Vector3d C)> tris, List<(Vector3d A, Vector3d B, Vector3d C)>? norms)
    {
        if (norms == null || norms.Count != tris.Count) return norms;
        // Знак решается для ГРАНИ ЦЕЛИКОМ, по большинству с весом площади: отдельные
        // треугольники построителя бывают намотаны не в ту сторону, и их потом чинит показная
        // сетка именно по нормалям — разворачивать нормали по каждому треугольнику нельзя.
        double vote = 0;
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            var g = Vector3d.Cross(b - a, c - a);
            var (na, nb, nc) = norms[i];
            vote += Vector3d.Dot(na + nb + nc, g);
        }
        if (vote >= 0) return norms;
        var res = new List<(Vector3d A, Vector3d B, Vector3d C)>(norms.Count);
        foreach (var (na, nb, nc) in norms) res.Add((-na, -nb, -nc));
        return res;
    }

    /// <summary>Ход одной разметки тела: был ли сбой (исключение), а не обычный отказ проверки.</summary>
    private sealed class MeshRun { public volatile bool Faulted; }

    /// <summary>
    /// Принимать сетку ПО ГРАНЯМ, а не по телу целиком. У тела из булевой правило «всё или
    /// ничего» разумно: частичная сетка дала бы разрыв между новыми и старыми гранями. У тела
    /// из обменного файла согласованной сетки нет и так, и 55 хороших граней из 56 лучше, чем
    /// ни одной.
    /// </summary>
    // K04: признак принадлежит логическому потоку вычисления, а не всему процессу.
    private static readonly AsyncLocal<bool> _perFaceAdoption = new();
    private static bool PerFaceAdoption { get => _perFaceAdoption.Value; set => _perFaceAdoption.Value = value; }

    private static bool _gReuseFaceMeshes = Environment.GetEnvironmentVariable("P3D_MESHREUSE") != "0";
    /// <summary>
    /// Поставка 3: ПЕРЕИСПОЛЬЗОВАНИЕ СЫРОЙ СЕТКИ НЕИЗМЕНЁННОЙ ГРАНИ при перестроении детали. Донор — грань прежнего
    /// опубликованного результата той же детали (MeshDonorScope) с тем же ТОЧНЫМ ключом: та же исходная поверхность
    /// (по ссылке), те же биты треугольников грани, рёбер и ОБРАЗОВ ВЕРШИН ПОСЛЕ ПРИВЯЗКИ ПО ВСЕМУ ТЕЛУ, те же
    /// настройки точности. Подставляется сырой результат грани ДО общего разбора тела — разбор идёт как прежде.
    /// Опыт перед включением (tests/k01 meshreuse): 309 повторов ключа на 24 перестроениях — 309 совпадений
    /// сетки побитово, 0 расхождений. Значение контекста (KernelSettings.ReuseFaceMeshes); P3D_MESHREUSE=0.
    /// </summary>
    public static bool ReuseFaceMeshes
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ReuseFaceMeshes : _gReuseFaceMeshes;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gReuseFaceMeshes, value, "BooleanOp.ReuseFaceMeshes");
    }

    private sealed class MeshDonors
    {
        public readonly Dictionary<object, Dictionary<string, ((List<Vector3d> P, List<int> T)? M, string? Why, List<Vector3d>? N)>> BySource
            = new();
        public int Reused;
    }
    private static readonly AsyncLocal<MeshDonors?> _donors = new();

    /// <summary>
    /// Доноры сетки на время окончательной разметки тел детали: сырые результаты граней её прежнего
    /// опубликованного результата. Возвращает область; Reused — сколько граней взято у доноров.
    /// </summary>
    public static MeshDonorScope UseMeshDonors(IEnumerable<TSolid> previous)
    {
        var d = new MeshDonors();
        foreach (var b in previous)
            foreach (var f in b.Faces)
                if (f.Surface is FacetedSurface { Source: { } src, RawMesh: { } raw })
                {
                    var sk = SourceKey(src);
                    if (!d.BySource.TryGetValue(sk, out var map)) d.BySource[sk] = map = new();
                    map.TryAdd(raw.Key, (raw.M, raw.Why, raw.N));
                }
        return new MeshDonorScope(d);
    }

    public readonly struct MeshDonorScope : IDisposable
    {
        private readonly MeshDonors? _prev, _mine;
        internal MeshDonorScope(object mine) { _prev = _donors.Value; _mine = (MeshDonors)mine; _donors.Value = _mine; }
        public int Reused => _mine?.Reused ?? 0;
        public void Dispose() { if (_mine != null) _donors.Value = _prev; }
    }

    /// <summary>
    /// u11: доноры сетки для прямых построителей (скругление, фаска, смещение): грани исходного тела с тем же строгим
    /// ключом (поверхность по содержанию, контуры побитово) отдают готовую сетку — неизменённые грани не размечаются
    /// заново (у тела из 40 граней — 0.25 с на каждое скругление). Мутация P3D_DIRECTDONOR=0.
    /// </summary>
    internal static Func<TFace, FacetedSurface?>? DonorFrom(TSolid body) => DonorFrom(body.Faces);
    internal static Func<TFace, FacetedSurface?>? DonorFrom(IEnumerable<TFace> faces)
    {
        if (!ReuseFaceMeshes || DirectDonorOff) return null;
        var donors = new Dictionary<string, FacetedSurface>();
        foreach (var f in faces)
            if (f.Surface is FacetedSurface dfs && dfs.Facets.Count > 0 && (dfs.Display != null || dfs.FromLoops) && MeshDonorKey(f) is { } k) donors.TryAdd(k, dfs);
        if (donors.Count == 0) return null;
        return f => MeshDonorKey(f) is { } k && donors.TryGetValue(k, out var d) ? d : null;
    }
    static readonly bool DirectDonorOff = Environment.GetEnvironmentVariable("P3D_DIRECTDONOR") == "0";

    /// <summary>
    /// Ключ исходной поверхности донора. Точные поверхности (плоскость, выдавливание и вращение отрезка или дуги) — ПО
    /// СОДЕРЖАНИЮ: вид и биты всех определяющих чисел (аналитический путь создаёт новые объекты поверхностей на каждом
    /// перестроении — по ссылке донор не находился бы, а разметчик видит те же входные данные). Прочие — по ссылке.
    /// </summary>
    internal static object SourceKey(ISurface3 s)
    {
        static void F(System.Text.StringBuilder b, Frame3 f) { V(b, f.Origin); V(b, f.U); V(b, f.V); V(b, f.W); }
        static void V(System.Text.StringBuilder b, Vector3d v) => b.Append(BitConverter.DoubleToInt64Bits(v.X)).Append(',').Append(BitConverter.DoubleToInt64Bits(v.Y)).Append(',').Append(BitConverter.DoubleToInt64Bits(v.Z)).Append(';');
        static void D(System.Text.StringBuilder b, double x) => b.Append(BitConverter.DoubleToInt64Bits(x)).Append(';');
        static bool C(System.Text.StringBuilder b, ICurve3 c)
        {
            switch (c)
            {
                case LineCurve3 l: b.Append("L:"); V(b, l.A); V(b, l.B); return true;
                case ArcCurve3 a: b.Append("A:"); F(b, a.Frame); D(b, a.Radius); D(b, a.A0); D(b, a.A1); return true;
                default: return false;
            }
        }
        var sb = new System.Text.StringBuilder();
        switch (s)
        {
            case PlaneSurface p: sb.Append("P:"); F(sb, p.F); return sb.ToString();
            case ExtrudedSurface e when C(sb.Append("E:"), e.Base): V(sb, e.Dir); return sb.ToString();
            case RevolvedSurface r when C(sb.Append("R:"), r.Base): F(sb, r.Axis); D(sb, r.A0); D(sb, r.A1); return sb.ToString();
            default: return s;                                   // по ссылке
        }
    }

    /// <summary>Точный ключ разметки грани (без исходной поверхности — она сравнивается отдельно, SourceKey).</summary>
    internal static string FaceMeshKey(TFace f, Func<Vector3d, Vector3d>? snap)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        void P(Vector3d p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        w.Write(Core.Tol.Chord); w.Write(Core.Tol.ChordAngle); w.Write(PerFaceAdoption);
        w.Write(f.Tag); w.Write(f.Reversed);
        var fs = (FacetedSurface)f.Surface;
        w.Write(fs.Facets.Count); foreach (var (a, b, c) in fs.Facets) { P(a); P(b); P(c); }
        w.Write(f.Loops.Count);
        foreach (var l in f.Loops)
        {
            w.Write(l.IsOuter); w.Write(l.CoEdges.Count);
            foreach (var ce in l.CoEdges)
            {
                var e = ce.Edge; w.Write(ce.Reversed); w.Write(e.Curve.GetType().Name);
                for (int i = 0; i <= 8; i++) P(e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 8));
                P(e.V0.P); P(e.V1.P);
                var s0 = snap?.Invoke(e.V0.P) ?? e.V0.P; var s1 = snap?.Invoke(e.V1.P) ?? e.V1.P; P(s0); P(s1);
            }
        }
        w.Flush();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
    }

    private static ((List<Vector3d> P, List<int> T)? M, string? Why, List<Vector3d>? N) CopyRaw(((List<Vector3d> P, List<int> T)? M, string? Why, List<Vector3d>? N) r) =>
        (r.M is { } m ? (new List<Vector3d>(m.P), new List<int>(m.T)) : null, r.Why, r.N == null ? null : new List<Vector3d>(r.N));

    /// <summary>
    /// Разбор (поставка 3): разметка тела сообщает тело и привязку вершин, посчитанную по ВСЕМУ телу, — для
    /// опыта «зависит ли сетка грани только от её точного ключа» (tests/k01 режим meshreuse). null — выключено.
    /// </summary>
    [ThreadStatic] internal static Action<TSolid, Func<Vector3d, Vector3d>?>? MeshSnapProbe;

    private static void MeshExactly(TSolid solid, List<string>? warnings, MeshRun run)
    {
        // СЛИЯНИЕ ВЕРШИН ЧЕРЕЗ РЁБРА В МИКРОНЫ (только в сетке). Такие рёбра — след сетки
        // точного ядра (1e-5 мм): экспорт склеивал их концы и открывал стороны, а сетка
        // тела из-за них отбраковывалась целиком.
        const double micro = 2e-4;   // 0.2 мкм: меньше допуска склейки экспорта (2.4e-5 мм ... 1e-5)
        var rep = new Dictionary<(double, double, double), Vector3d>();
        var parent = new Dictionary<(double, double, double), (double, double, double)>();
        (double, double, double) K(Vector3d p) => (p.X, p.Y, p.Z);
        (double, double, double) Find((double, double, double) x)
        {
            while (parent.TryGetValue(x, out var p) && !p.Equals(x)) { parent[x] = parent.TryGetValue(p, out var g) ? g : p; x = parent[x]; }
            return x;
        }
        // Любые вершины тела ближе допуска, не только концы коротких рёбер: рядом бывают
        // и несоединённые вершины (два угла в 3e-5 друг от друга).
        var cells = new Dictionary<(long, long, long), List<Vector3d>>();
        void Join(Vector3d u, Vector3d v)
        {
            var a = Find(K(u)); var b = Find(K(v));
            parent.TryAdd(a, a); parent.TryAdd(b, b);
            a = Find(a); b = Find(b);
            if (!a.Equals(b)) parent[b] = a;
        }
        foreach (var v in solid.Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).Distinct())
        {
            var c = ((long)Math.Floor(v.X / micro), (long)Math.Floor(v.Y / micro), (long)Math.Floor(v.Z / micro));
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var l))
                            foreach (var q in l)
                                if ((q - v).Length < micro) Join(q, v);
            if (!cells.TryGetValue(c, out var own)) cells[c] = own = new List<Vector3d>();
            own.Add(v);
        }
        Func<Vector3d, Vector3d>? snap = null;

        if (parent.Count > 0)
        {
            foreach (var v in solid.Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }))
            {
                var k = K(v);
                if (!parent.ContainsKey(k)) continue;
                var r = Find(k);
                rep[k] = new Vector3d(r.Item1, r.Item2, r.Item3);
            }
            snap = p => rep.TryGetValue(K(p), out var q) ? q : p;
        }

        // ПРИВЯЗКА СТАВИТСЯ ОДНОЙ ОБЛАСТЬЮ НА ВСЮ РАЗМЕТКУ и снимается сама. Прежде её ставили и
        // снимали вручную в восьми местах; забыть одну ветку выхода было делом времени, а цена —
        // изменённая сетка следующей грани.
        using var snapScope = new ExactFaceMesher.SnapScope(snap);
        MeshSnapProbe?.Invoke(solid, snap);

        var results = new List<(FacetedSurface Fs, List<(Vector3d, Vector3d, Vector3d)> Tris, List<(Vector3d, Vector3d, Vector3d)>? Norms)>();
        // СОВПАДАЮЩИЕ ГРАНИ С ОДИНАКОВЫМ ОБХОДОМ — ПОВТОР. Булева иногда выдаёт две грани с
        // одной площадью, одним центром и одним обходом. В сетке они обе проходят общую
        // сторону вперёд, и тело выходит незамкнутым. Треугольники берутся у первой, повтор
        // пропускается. Плёнка (обходы встречные) под это не подпадает: ключ учитывает обход.
        var faceKey = new HashSet<string>();
        var skip = new HashSet<TFace>();
        // g.13: полный ключ (строки по всем треугольникам) — только у граней с общим грубым ключом: число треугольников и
        // габарит округлённых центров (функции того же набора, что и полный ключ — равные ключи дают равный грубый).
        Dictionary<(int, double, double, double, double, double, double), int>? coarse = null;
        (int, double, double, double, double, double, double) Coarse(FacetedSurface fk)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var t in fk.Facets)
            {
                var c = (t.A + t.B + t.C) / 3; double cx = Math.Round(c.X, 5), cy = Math.Round(c.Y, 5), cz = Math.Round(c.Z, 5);
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); z0 = Math.Min(z0, cz); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy); z1 = Math.Max(z1, cz);
            }
            return (fk.Facets.Count, x0, y0, z0, x1, y1, z1);
        }
        if (!MeshChecksFastOff)
        {
            coarse = new();
            foreach (var f in solid.Faces)
                if (f.Surface is FacetedSurface fk0 && fk0.Facets.Count > 0) { var ck = Coarse(fk0); coarse[ck] = coarse.GetValueOrDefault(ck) + 1; }
        }
        using (Diagnostics.Prof.Time("сетка: повторы граней"))
        foreach (var f in solid.Faces)
        {
            if (f.Surface is not FacetedSurface fk || fk.Facets.Count == 0) continue;
            if (coarse != null && coarse[Coarse(fk)] < 2) continue;
            var parts = fk.Facets.Select(t =>
            {
                var c = (t.A + t.B + t.C) / 3;
                var n = Vector3d.Cross(t.B - t.A, t.C - t.A);
                return $"{Math.Round(c.X, 5)}|{Math.Round(c.Y, 5)}|{Math.Round(c.Z, 5)}|{Math.Round(n.X, 4)}|{Math.Round(n.Y, 4)}|{Math.Round(n.Z, 4)}";
            }).ToList();
            parts.Sort(StringComparer.Ordinal);
            if (!faceKey.Add(string.Join(";", parts))) skip.Add(f);
        }
        if (skip.Count > 0) warnings?.Add($"Mesh: {skip.Count} duplicate face(s) skipped.");

        // СЕТКИ ГРАНЕЙ СЧИТАЮТСЯ ЗАРАНЕЕ И В НЕСКОЛЬКО ПОТОКОВ. Грани независимы, но разбор
        // отказов ниже идёт последовательно (и с выходом), поэтому распараллеливается только сам
        // расчёт. Точки рёбер готовятся до раскладки: они считаются лениво в общий кэш, и без
        // подготовки потоки считают их наперегонки (измерено: вчетверо дороже).
        var prepared = new System.Collections.Concurrent.ConcurrentDictionary<TFace,
            ((List<Vector3d> P, List<int> T)? M, string? Why, List<Vector3d>? Norms)>();
        {
            var todo = solid.Faces.Where(f => !skip.Contains(f) && f.Surface is FacetedSurface).ToList();
            using (Diagnostics.Prof.Time("сетка: точки рёбер (подготовка)"))
                foreach (var f in todo)
                    foreach (var e in f.Edges) ExactFaceMesher.EdgePoints(e);

            // Поставка 3: точные ключи граней (только если переиспользование включено) — до расчёта, под привязкой.
            var donors = ReuseFaceMeshes ? _donors.Value : null;
            Dictionary<TFace, string>? keys = null;
            if (ReuseFaceMeshes)
                using (Diagnostics.Prof.Time("сетка: ключи граней"))
                {
                    keys = new Dictionary<TFace, string>(ReferenceEqualityComparer.Instance);
                    foreach (var f in todo) keys[f] = FaceMeshKey(f, snap);
                }
            using (Diagnostics.Prof.Time("сетка: расчёт граней"))
            {
                void One(TFace f)
                {
                    try
                    {
                        // ВРЕМЯ КАЖДОЙ ГРАНИ ОТДЕЛЬНО. Раскладка по потокам упирается в самую
                        // тяжёлую грань, и знать надо именно её, а не сумму: тринадцать граней на
                        // шестнадцати ядрах ждут самую медленную.
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        KernelContext.Checkpoint("mesh.face");
                        FaultInjection.Hit("mesh.face");
                        // Поставка 3: донор с тем же точным ключом — его сырой результат (копией) вместо расчёта.
                        string? key = keys != null && keys.TryGetValue(f, out var kk) ? kk : null;
                        if (key != null && donors != null && ((FacetedSurface)f.Surface).Source is { } src0 &&
                            donors.BySource.TryGetValue(SourceKey(src0), out var byKey) && byKey.TryGetValue(key, out var raw0))
                        {
                            var copy = CopyRaw(raw0);
                            ((FacetedSurface)f.Surface).RawMesh = (key, copy.M, copy.Why, copy.N);
                            prepared[f] = CopyRaw(copy);
                            Interlocked.Increment(ref donors.Reused);
                            return;
                        }
                        var mm = ExactFaceMesher.Mesh(f, out string? w2, out var n2);
                        if (key != null)
                        {
                            var c2 = CopyRaw((mm, w2, n2));
                            ((FacetedSurface)f.Surface).RawMesh = (key, c2.M, c2.Why, c2.N);
                        }
                        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                        // Запись идёт СРАЗУ, до любых других вычислений: прошлая попытка считала
                        // здесь число точек контура, падала на пустой ссылке, и перехват ниже это
                        // глушил — в отчёт не попадало ничего, а причина выглядела загадкой.
                        Diagnostics.Prof.Add($"грань {FaceKindName(f)}", dt);

                        prepared[f] = (mm, w2, n2);
                    }
                    catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { run.Faulted = true; prepared[f] = (null, ex.Message, null); }
                }

                if (Threads > 1 && Environment.ProcessorCount > 1)
                    System.Threading.Tasks.Parallel.ForEach(
                        todo,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Threads },
                        One);
                else
                    foreach (var f in todo) One(f);
            }
        }

        foreach (var f in solid.Faces)
        {
            if (skip.Contains(f)) continue;
            if (f.Surface is not FacetedSurface fs) return;
            // Грань нулевой площади (щепка из разбиения) остаётся со своими треугольниками:
            // строить для неё сетку нечего, а ронять из-за неё всё тело незачем.
            double fArea = 0;
            foreach (var (a, b, c) in fs.Facets) fArea += Vector3d.Cross(b - a, c - a).Length / 2;
            if (fArea < 1e-6)
            {
                // ПРОБОВАЛ выдавать для такой грани пустой список: охват падал с 89 до 88 —
                // её прежние треугольники всё же кому-то нужны для замыкания.
                results.Add((fs, fs.Facets.ToList(), null));
                continue;
            }
            (List<Vector3d> P, List<int> T)? m;
            string? why;
            List<Vector3d>? nrm = null;
            if (prepared.TryGetValue(f, out var got)) { m = got.M; why = got.Why; nrm = got.Norms; }
            else
            {
                try { m = ExactFaceMesher.Mesh(f, out why, out nrm); }
                catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { run.Faulted = true; m = null; why = ex.Message; }
            }
            if (m == null)
            {
                string extra = "";
                if (why != null && why.StartsWith("все контуры вырождены") && f.Surface is FacetedSurface fq10)
                {
                    // Рёбра тела, лежащие на этой грани по координатам, но не входящие в её контуры.
                    var mine = new HashSet<uint>(f.Edges.Select(e => (uint)e.Id));
                    int near = 0, nearMine = 0; double nearLen = 0;
                    foreach (var e10 in solid.Edges)
                    {
                        if (mine.Contains((uint)e10.Id)) continue;
                        bool onFace = fq10.Facets.Any(t =>
                            (t.A - e10.V0.P).Length < 1e-4 || (t.B - e10.V0.P).Length < 1e-4 || (t.C - e10.V0.P).Length < 1e-4);
                        if (!onFace) continue;
                        near++; nearLen += (e10.V1.P - e10.V0.P).Length;
                        if (ReferenceEquals(e10.FaceA, f) || ReferenceEquals(e10.FaceB, f)) nearMine++;
                    }
                    // Сколько граней сидит на ТОЙ ЖЕ точной поверхности и сколько у них рёбер.
                    var srcOf = (f.Surface as FacetedSurface)?.Source;
                    var sameSurf = solid.Faces.Where(z => ReferenceEquals((z.Surface as FacetedSurface)?.Source, srcOf)).ToList();
                    extra = $"; рёбер тела на этой грани вне её контуров {near} (ссылаются на неё {nearMine}), их длина {nearLen:0.##}" +
                            $"; граней на той же поверхности {sameSurf.Count} [{string.Join(",", sameSurf.Select(z => z.Id + ":рёбер" + z.Edges.Count()))}]";
                }
                MeshDebug?.Invoke($"[{OpTag}] грань {f.Id} ({(f.Surface is FacetedSurface fq9 ? fq9.Source?.GetType().Name : f.Surface.GetType().Name)}, рёбер {f.Edges.Count()}, контуров {f.Loops.Count}): {why}{extra} — {(PerFaceAdoption ? "грань пропущена" : "тело на прежней сетке")}");
                if (PerFaceAdoption) continue;      // остальные грани всё равно строим
                return;
            }
            var (P, T) = m.Value;
            var tris = new List<(Vector3d, Vector3d, Vector3d)>(T.Count / 3);
            for (int i = 0; i + 2 < T.Count; i += 3) tris.Add((P[T[i]], P[T[i + 1]], P[T[i + 2]]));
            List<(Vector3d, Vector3d, Vector3d)>? norms = null;
            if (nrm != null && nrm.Count == P.Count)
            {
                norms = new List<(Vector3d, Vector3d, Vector3d)>(T.Count / 3);
                for (int i = 0; i + 2 < T.Count; i += 3) norms.Add((nrm[T[i]], nrm[T[i + 1]], nrm[T[i + 2]]));
            }
            results.Add((fs, tris, norms));
        }
        // СЛИЯНИЕ БЛИЗКИХ ТОЧЕК ГОТОВОЙ СЕТКИ. Точки, поставленные уточнением, иногда
        // оказываются в десятках нанометров друг от друга: экспорт склеит их, треугольник
        // выродится, и в STL откроется сторона. Слияние идёт по всему телу одинаково, поэтому
        // щелей между гранями не появляется.
        using (Diagnostics.Prof.Time("сетка: слияние точек"))
        {
            var cellsM = new Dictionary<(long, long, long), List<Vector3d>>();
            // g.13: одна и та же точка (общая у соседних треугольников) — один ответ, без 27 поисков по ячейкам заново
            var memo = MeshChecksFastOff ? null : new Dictionary<Vector3d, Vector3d>();
            Vector3d Merge(Vector3d p)
            {
                if (memo == null) return Merge0(p);
                if (memo.TryGetValue(p, out var m)) return m;
                return memo[p] = Merge0(p);
            }
            Vector3d Merge0(Vector3d p)
            {
                var c = ((long)Math.Floor(p.X / micro), (long)Math.Floor(p.Y / micro), (long)Math.Floor(p.Z / micro));
                for (long ddx = -1; ddx <= 1; ddx++)
                    for (long ddy = -1; ddy <= 1; ddy++)
                        for (long ddz = -1; ddz <= 1; ddz++)
                            if (cellsM.TryGetValue((c.Item1 + ddx, c.Item2 + ddy, c.Item3 + ddz), out var l))
                                foreach (var q in l)
                                    if ((q - p).Length < micro) return q;
                if (!cellsM.TryGetValue(c, out var own2)) cellsM[c] = own2 = new List<Vector3d>();
                own2.Add(p);
                return p;
            }
            foreach (var r in results)
                for (int i = 0; i < r.Tris.Count; i++)
                {
                    var (a, b, c) = r.Tris[i];
                    r.Tris[i] = (Merge(a), Merge(b), Merge(c));
                }
        }

        // Т-ОБРАЗНЫЕ СТЫКИ здесь НЕ лечатся: деление непарных сторон по лежащим на них
        // вершинам охвата не прибавило (83 тела и с ним, и без), а построение подорожало с 55
        // до 81 секунды. Значит, на непарных сторонах чужих вершин нет — стык не Т-образный,
        // и дыра возникает по другой причине.

        // СТОРОНА МЕЛКОЙ ГРАНИ ПО МАТЕРИАЛУ — ПРОБОВАЛ, УБРАЛ. Замысел: для грани из двух-трёх
        // треугольников выбирать сторону не голосованием по прежней сетке, а проверкой «где
        // материал» (отступ по нормали в обе стороны). Проверка не сработала НИ РАЗУ — значит,
        // собственное голосование грани уже даёт тот же ответ. Вдобавок сам вызов гранения тела
        // изнутри построения менял состояние и ронял охват с 89 до 88 тел.

        // СОГЛАСОВАНИЕ ОРИЕНТАЦИИ ПО ТЕЛУ — ПРОБОВАЛ ТРИЖДЫ, ОТКАТИЛ. Обход соседей с
        // разворотом несогласованных граней поднимал охват с 89 до 90 тел, но ломал тест
        // «короткая кромка отверстия»: лучевая проверка «точка внутри тела» начинала считать
        // лишние точки. Не помогли ни страховка по знаку объёма тела, ни ограничение разворота
        // мелкими гранями. Значит, разворот граней рвёт согласие сетки с рациональной моделью:
        // менять надо не намотку постфактум, а сторону грани при её построении.

        // ПРЕЖНЯЯ ПОПЫТКА (разворот по одной стороне) — Обход соседей с разворотом
        // несогласованных граней уменьшал незамкнутость (13 → 11 случаев), но ломал тест
        // «короткая кромка отверстия»: три открытых ребра. Разворот по одной совпавшей стороне
        // ненадёжен — нужно решать большинством по всем общим сторонам пары граней.

        // ЗАМКНУТОСТЬ ВСЕЙ СЕТКИ: каждая сторона — ровно у двух треугольников, во встречных
        // направлениях (вырожденные треугольники не в счёт — их не рисуют). Иначе тело остаётся
        // на прежней сетке: вырожденное место топологии (два ребра друг на друге) новое
        // построение честно превращает в двойной слой — на прежней сетке оно пряталось в щепке.
        var tClosed = Diagnostics.Prof.Time("сетка: замкнутость");
        // g.13: ТА ЖЕ ПРОВЕРКА ПО НОМЕРАМ ТОЧЕК (без разбора): точки — номерами, сторона — парой номеров
        // вместо пары векторов; исход тот же (повтор вершины, сторона короче 2e-4 мм, равенство числа сторон в обе стороны).
        if (!MeshChecksFastOff && MeshDebug == null)
        {
            var idOf = new Dictionary<Vector3d, int>(); var pts = new List<Vector3d>();
            int Id(Vector3d p)
            {
                ref int slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(idOf, p, out bool exists);
                if (!exists) { slot = pts.Count; pts.Add(p); }
                return slot;
            }
            var dirI = new Dictionary<(int, int), int>();
            foreach (var (_, tris, _) in results)
                foreach (var (a, b, c) in tris)
                {
                    if (a == b || b == c || c == a) continue;
                    int ia = Id(a), ib = Id(b), ic = Id(c);
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dirI, (ia, ib), out _)++;
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dirI, (ib, ic), out _)++;
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dirI, (ic, ia), out _)++;
                }
            foreach (var (i, j) in dirI.Keys)
                if ((pts[i] - pts[j]).LengthSquared < 4e-8) { tClosed.Dispose(); return; }
            foreach (var ((i, j), n) in dirI)
                if (n == 0 || n != dirI.GetValueOrDefault((j, i)))
                {
                    tClosed.Dispose();
                    if (PerFaceAdoption) foreach (var (fs3, tris3, norms3) in results) fs3.SetDisplay(tris3, AgreeNormals(tris3, norms3));
                    return;
                }
        }
        else
        {
        var dir = new Dictionary<(Vector3d, Vector3d), int>();
        var owner = new Dictionary<(Vector3d, Vector3d), List<uint>>();
        // g.13: владельцы сторон — только для разбора (MeshDebug); проверке замкнутости они не нужны
        bool needOwner = MeshChecksFastOff || MeshDebug != null;
        var faceOf = solid.Faces.Where(x => x.Surface is FacetedSurface).ToDictionary(x => (FacetedSurface)x.Surface, x => x.Id);
        foreach (var (fs, tris, _) in results)
            foreach (var (a, b, c) in tris)
            {
                if (a == b || b == c || c == a) continue;
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                {
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dir, (p, q), out _)++;
                    if (needOwner)
                    {
                        if (!owner.TryGetValue((p, q), out var l)) owner[(p, q)] = l = new List<uint>();
                        l.Add(faceOf.GetValueOrDefault(fs));
                    }
                }
            }
        // Стороны короче 2e-4 мм: экспорт склеит их концы (его допуск 1e-5…2.4e-5), а в STL с
        // одинарной точностью такая щепка рвётся. Такое тело — на прежней сетке.
        foreach (var ((p, q), _) in dir)
            if ((p - q).LengthSquared < 4e-8)
            {
                MeshDebug?.Invoke($"сторона {(p - q).Length:E1} мм у ({p.X:0.###},{p.Y:0.###},{p.Z:0.###}) — тело на прежней сетке");
                return;
            }
        // ЗАМКНУТОСТЬ — ПО РАВЕНСТВУ НАПРАВЛЕНИЙ. Прежде требовалось строго по одному
        // треугольнику в каждую сторону; у тел, где две грани сходятся по общей стороне дважды
        // (шов или повторённое ребро), выходило «вперёд 2, назад 2» — сетка сбалансирована, а
        // проверка её отвергала. Объём тела проверяется отдельно и ловит настоящие ошибки.
        foreach (var ((p, q), n) in dir)
            if (n == 0 || n != dir.GetValueOrDefault((q, p)))
            {
                if (MeshDebug != null)
                    foreach (uint fid in owner[(p, q)].Distinct())
                    {
                        var ff = solid.Faces.FirstOrDefault(x => x.Id == fid);
                        if (ff == null) continue;
                        MeshDebug($"  грань {fid} ({(ff.Surface is FacetedSurface f2 ? f2.Source?.GetType().Name : ff.Surface.GetType().Name)}): рёбра " +
                                  string.Join("; ", ff.Edges.Select(e => $"{e.Id} {e.Curve.GetType().Name} ({e.V0.P.X:0.##},{e.V0.P.Y:0.##},{e.V0.P.Z:0.##})-({e.V1.P.X:0.##},{e.V1.P.Y:0.##},{e.V1.P.Z:0.##}) длина {(e.V1.P - e.V0.P).Length:0.##}×{e.CoEdges.Count(c => c.Loop?.Face == ff)}")));
                    }
                if (MeshDebug != null)
                {
                    // Сколько всего непарных сторон и у каких граней.
                    var perFace = new Dictionary<uint, int>();
                    int totalBad = 0;
                    foreach (var ((x, y), cnt) in dir)
                        if (cnt != dir.GetValueOrDefault((y, x)))
                        {
                            totalBad++;
                            foreach (uint fid5 in owner[(x, y)]) perFace[fid5] = perFace.GetValueOrDefault(fid5) + 1;
                        }
                    // Совпадает ли ориентация новой сетки грани с прежней.
                    foreach (var r6 in results)
                    {
                        var f6 = solid.Faces.FirstOrDefault(x => ReferenceEquals(x.Surface, r6.Fs));
                        if (f6 == null || r6.Fs.Facets.Count == 0 || r6.Tris.Count == 0) continue;
                        int agree = 0, disagree = 0;
                        foreach (var (x, y, z) in r6.Tris)
                        {
                            Vector3d n6 = Vector3d.Cross(y - x, z - x);
                            if (n6.LengthSquared < 1e-24) continue;
                            Vector3d g6 = (x + y + z) / 3;
                            double best6 = double.MaxValue; Vector3d nref6 = Vector3d.Zero;
                            foreach (var (a6, b6, c6) in r6.Fs.Facets)
                            {
                                double d6 = ((a6 + b6 + c6) / 3 - g6).LengthSquared;
                                if (d6 < best6) { best6 = d6; nref6 = Vector3d.Cross(b6 - a6, c6 - a6); }
                            }
                            if (Vector3d.Dot(n6, nref6) >= 0) agree++; else disagree++;
                        }
                        if (disagree > 0) MeshDebug($"  грань {f6.Id}: треугольников по прежней ориентации {agree}, против {disagree}");
                    }
                    {
                        // Точки ЛЮБЫХ граней строго внутри непарной стороны: если они есть,
                        // сторону делят соседи, а эта грань держит её целиком.
                        Vector3d d8 = q - p; double l8 = d8.LengthSquared;
                        var seen8 = new HashSet<(double, double, double)>();
                        var faces8 = new HashSet<uint>();
                        foreach (var r8 in results)
                        {
                            var f8 = solid.Faces.FirstOrDefault(x => ReferenceEquals(x.Surface, r8.Fs));
                            foreach (var t8 in r8.Tris)
                                foreach (var x8 in new[] { t8.Item1, t8.Item2, t8.Item3 })
                                {
                                    double s8 = Vector3d.Dot(x8 - p, d8) / l8;
                                    if (s8 <= 1e-6 || s8 >= 1 - 1e-6) continue;
                                    if ((p + d8 * s8 - x8).Length > 1e-6) continue;
                                    if (seen8.Add((Math.Round(x8.X, 6), Math.Round(x8.Y, 6), Math.Round(x8.Z, 6))) && f8 != null) faces8.Add((uint)f8.Id);
                                }
                        }
                        MeshDebug($"  внутри стороны {seen8.Count} точек, у граней: {string.Join(",", faces8)}");

                        // Входит ли сторона в контур грани-владельца: соседние точки контура.
                        foreach (uint fid10 in owner[(p, q)])
                        {
                            var ff10 = solid.Faces.FirstOrDefault(x => (uint)x.Id == fid10);
                            var cyc10 = ff10 == null ? null : Tess.ExactFaceMesher.Cycles(ff10);
                            if (cyc10 == null) continue;
                            bool inLoop = false;
                            foreach (var c10 in cyc10)
                            {
                                var pts10 = new List<Vector3d>();
                                foreach (var (e10b, rev10) in c10)
                                {
                                    var pe = Tess.ExactFaceMesher.EdgePoints(e10b);
                                    for (int i = 0; i + 1 < pe.Count; i++) pts10.Add(rev10 ? pe[pe.Count - 1 - i] : pe[i]);
                                }
                                for (int i = 0; i < pts10.Count && !inLoop; i++)
                                {
                                    var a10 = pts10[i]; var b10 = pts10[(i + 1) % pts10.Count];
                                    if (((a10 - p).Length < 1e-6 && (b10 - q).Length < 1e-6) ||
                                        ((a10 - q).Length < 1e-6 && (b10 - p).Length < 1e-6)) inLoop = true;
                                }
                            }
                            MeshDebug($"  сторона {(inLoop ? "ВХОДИТ" : "не входит")} в контур грани {fid10}");
                        }

                        // Чьи ещё контуры содержат эту сторону: они и должны её закрыть.
                        var holders = new List<string>();
                        foreach (var ff11 in solid.Faces)
                        {
                            var cyc11b = Tess.ExactFaceMesher.Cycles(ff11);
                            if (cyc11b == null) continue;
                            foreach (var c11 in cyc11b)
                            {
                                var pts11 = new List<Vector3d>();
                                foreach (var (e11b, rev11) in c11)
                                {
                                    var pe = Tess.ExactFaceMesher.EdgePoints(e11b);
                                    for (int i = 0; i + 1 < pe.Count; i++) pts11.Add(rev11 ? pe[pe.Count - 1 - i] : pe[i]);
                                }
                                for (int i = 0; i < pts11.Count; i++)
                                {
                                    var a11 = pts11[i]; var b11 = pts11[(i + 1) % pts11.Count];
                                    if (((a11 - p).Length < 1e-6 && (b11 - q).Length < 1e-6) ||
                                        ((a11 - q).Length < 1e-6 && (b11 - p).Length < 1e-6))
                                    { holders.Add(ff11.Id.ToString()); i = pts11.Count; }
                                }
                            }
                        }
                        MeshDebug($"  сторона есть в контурах граней: {(holders.Count == 0 ? "нет ни у кого" : string.Join(",", holders))}");
                        foreach (var hid in holders)
                        {
                            var ff12 = solid.Faces.FirstOrDefault(x => x.Id.ToString() == hid);
                            var r12 = results.FirstOrDefault(x => ReferenceEquals(x.Fs, ff12?.Surface));
                            if (r12.Tris == null) { MeshDebug($"    грань {hid}: сетки нет"); continue; }
                            bool hasP = r12.Tris.Any(t => (t.Item1 - p).Length < 1e-6 || (t.Item2 - p).Length < 1e-6 || (t.Item3 - p).Length < 1e-6);
                            bool hasQ = r12.Tris.Any(t => (t.Item1 - q).Length < 1e-6 || (t.Item2 - q).Length < 1e-6 || (t.Item3 - q).Length < 1e-6);
                            int side12 = 0;
                            foreach (var t in r12.Tris)
                                foreach (var (x, y) in new[] { (t.Item1, t.Item2), (t.Item2, t.Item3), (t.Item3, t.Item1) })
                                    if (((x - p).Length < 1e-6 && (y - q).Length < 1e-6) || ((x - q).Length < 1e-6 && (y - p).Length < 1e-6)) side12++;
                            MeshDebug($"    грань {hid}: концы в сетке {hasP}/{hasQ}, сторона встречается {side12} раз, треугольников {r12.Tris.Count}");
                        }

                        // Точки контура грани рядом со спорной стороной и граница её сетки там же.
                        foreach (uint fid14 in owner[(p, q)])
                        {
                            var ff14 = solid.Faces.FirstOrDefault(x => (uint)x.Id == fid14);
                            var cyc14 = ff14 == null ? null : Tess.ExactFaceMesher.Cycles(ff14);
                            if (cyc14 == null) continue;
                            var near14 = new List<string>();
                            foreach (var c14 in cyc14)
                                foreach (var (e14, rev14) in c14)
                                {
                                    var pe = Tess.ExactFaceMesher.EdgePoints(e14);
                                    for (int i = 0; i < pe.Count; i++)
                                        if ((pe[i] - p).Length < 3 || (pe[i] - q).Length < 3)
                                            near14.Add($"{e14.Id}:{i}({pe[i].X:0.##},{pe[i].Y:0.##},{pe[i].Z:0.##})");
                                }
                            MeshDebug($"  контур грани {fid14} рядом со стороной: {string.Join(" ", near14.Take(10))}");
                        }

                        // Как ту же сторону проходят ПРЕЖНИЕ треугольники (они заведомо верны).
                        foreach (uint fid13 in owner[(p, q)])
                        {
                            var ff13 = solid.Faces.FirstOrDefault(x => (uint)x.Id == fid13);
                            if (ff13?.Surface is not FacetedSurface fs13) continue;
                            int fwd13 = 0, bwd13 = 0, insideOld = 0;
                            Vector3d d13 = q - p; double l13 = d13.LengthSquared;
                            var seen13 = new HashSet<(double, double, double)>();
                            foreach (var (a13, b13, c13) in fs13.Facets)
                            {
                                foreach (var (x13, y13) in new[] { (a13, b13), (b13, c13), (c13, a13) })
                                {
                                    if ((x13 - p).Length < 1e-6 && (y13 - q).Length < 1e-6) fwd13++;
                                    if ((x13 - q).Length < 1e-6 && (y13 - p).Length < 1e-6) bwd13++;
                                }
                                foreach (var z13 in new[] { a13, b13, c13 })
                                {
                                    double t13 = Vector3d.Dot(z13 - p, d13) / l13;
                                    if (t13 <= 1e-6 || t13 >= 1 - 1e-6) continue;
                                    if ((p + d13 * t13 - z13).Length > 1e-6) continue;
                                    if (seen13.Add((Math.Round(z13.X, 6), Math.Round(z13.Y, 6), Math.Round(z13.Z, 6)))) insideOld++;
                                }
                            }
                            MeshDebug($"  прежняя сетка грани {fid13}: сторона вперёд {fwd13}, назад {bwd13}, точек внутри {insideOld}, треугольников {fs13.Facets.Count}");
                        }

                        // Чем являются концы стороны и какие рёбра их содержат.
                        foreach (var (name9, pt9) in new[] { ("начало", p), ("конец", q) })
                        {
                            bool vert9 = solid.Edges.Any(e9 => (e9.V0.P - pt9).Length < 1e-6 || (e9.V1.P - pt9).Length < 1e-6);
                            var on9 = solid.Edges.Where(e9 => Tess.ExactFaceMesher.EdgePoints(e9).Any(x => (x - pt9).Length < 1e-6))
                                                 .Select(e9 => $"{e9.Id}({e9.Curve.GetType().Name.Replace("Curve3", "")})").Take(4).ToList();
                            MeshDebug($"  {name9} стороны: {(vert9 ? "вершина тела" : "точка внутри ребра")}, лежит на рёбрах {string.Join(",", on9)}");
                        }
                    }
                    foreach (var r9 in results)
                    {
                        var f9 = solid.Faces.FirstOrDefault(x => ReferenceEquals(x.Surface, r9.Fs));
                        double aOld9 = r9.Fs.Facets.Sum(t => Vector3d.Cross(t.B - t.A, t.C - t.A).Length / 2);
                        double aNew9 = r9.Tris.Sum(t => Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length / 2);
                        MeshDebug($"    грань {f9?.Id}: прежних треугольников {r9.Fs.Facets.Count} (площадь {aOld9:0.##}), новых {r9.Tris.Count} (площадь {aNew9:0.##})");
                    }
                    MeshDebug($"  всего непарных сторон {totalBad}; по граням: {string.Join(", ", perFace.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key + ":" + kv.Value))}");

                    var stat = new Dictionary<string, int>();
                    int same = 0;
                    foreach (var f4 in solid.Faces)
                    {
                        if (f4.Surface is not FacetedSurface fs4 || fs4.Facets.Count == 0) continue;
                        double a4 = 0; Vector3d c4 = Vector3d.Zero;
                        foreach (var (x, y, z) in fs4.Facets)
                        {
                            double s4 = Vector3d.Cross(y - x, z - x).Length / 2;
                            a4 += s4; c4 += (x + y + z) / 3 * s4;
                        }
                        if (a4 <= 0) continue;
                        c4 /= a4;
                        string k4 = $"{a4:0.###}|{c4.X:0.##}|{c4.Y:0.##}|{c4.Z:0.##}";
                        if (!stat.TryAdd(k4, (int)f4.Id)) same++;
                    }
                    MeshDebug($"  в теле {solid.Faces.Count()} граней, совпадающих пар {same}, оболочек {solid.Shells.Count}");
                    var ids = new List<uint>(owner[(p, q)]);
                    if (owner.TryGetValue((q, p), out var ob2)) ids.AddRange(ob2);
                    foreach (uint fid in ids.Distinct())
                    {
                        var ff3 = solid.Faces.FirstOrDefault(x => (uint)x.Id == fid);
                        if (ff3?.Surface is not FacetedSurface fs3) continue;
                        double ar3 = fs3.Facets.Sum(t => Vector3d.Cross(t.B - t.A, t.C - t.A).Length / 2);
                        Vector3d c3 = Vector3d.Zero; double w3 = 0;
                        foreach (var (x, y, z) in fs3.Facets) { double a3 = Vector3d.Cross(y - x, z - x).Length / 2; c3 += (x + y + z) / 3 * a3; w3 += a3; }
                        if (w3 > 0) c3 /= w3;
                        int shellIdx = solid.Shells.FindIndex(sh => sh.Faces.Contains(ff3));
                        MeshDebug($"  грань {fid}: {fs3.Source?.GetType().Name}, площадь {ar3:0.##}, центр ({c3.X:0.##},{c3.Y:0.##},{c3.Z:0.##}), рёбер {ff3.Edges.Count()}, контуров {ff3.Loops.Count}, оболочка {shellIdx} из {solid.Shells.Count}");
                    }
                    // Рёбра, ЛЕЖАЩИЕ НА стороне (целиком внутри отрезка): если сторона покрывает
                    // несколько рёбер, значит, грань взяла её целиком там, где соседняя разбила.
                    {
                        Vector3d d5 = q - p;
                        double l5 = d5.LengthSquared;
                        foreach (var ff5 in solid.Faces)
                            foreach (var e5 in ff5.Edges)
                            {
                                bool On(Vector3d x)
                                {
                                    double t5 = Vector3d.Dot(x - p, d5) / l5;
                                    return t5 > -1e-6 && t5 < 1 + 1e-6 && (p + d5 * t5 - x).Length < 1e-4;
                                }
                                if (!On(e5.V0.P) || !On(e5.V1.P)) continue;
                                MeshDebug($"  на стороне лежит ребро {e5.Id} ({e5.Curve.GetType().Name}) длиной {(e5.V1.P - e5.V0.P).Length:0.###} грани {ff5.Id}, соседняя {(e5.FaceA == ff5 ? e5.FaceB : e5.FaceA)?.Id}");
                            }
                    }

                    // Кто ДОЛЖЕН был закрыть сторону: грани, у которых есть ребро, проходящее
                    // через оба её конца.
                    foreach (var ff2 in solid.Faces)
                        foreach (var e2 in ff2.Edges)
                        {
                            var pts2 = Tess.ExactFaceMesher.EdgePoints(e2);
                            bool hasP = pts2.Any(x => (x - p).Length < 1e-4);
                            bool hasQ = pts2.Any(x => (x - q).Length < 1e-4);
                            if (hasP && hasQ)
                            {
                                // У какой грани точки ребра есть в сетке, а у какой нет.
                                string Where(TFace? ff6)
                                {
                                    // Сверяемся с РАБОЧИМ списком сеток: присвоены они будут позже.
                                    var r6 = results.FirstOrDefault(x => ReferenceEquals(x.Fs, ff6?.Surface));
                                    if (r6.Tris == null) return "нет новой сетки";
                                    int have = 0;
                                    foreach (var q6 in pts2)
                                        if (r6.Tris.Any(t6 => (t6.Item1 - q6).Length < 1e-6 || (t6.Item2 - q6).Length < 1e-6 || (t6.Item3 - q6).Length < 1e-6)) have++;
                                    return $"{have} из {pts2.Count}";
                                }
                                // Точки ГРАНЕЙ, лежащие строго внутри непарной стороны.
                                string Inside(TFace? ff7)
                                {
                                    var r7 = results.FirstOrDefault(x => ReferenceEquals(x.Fs, ff7?.Surface));
                                    if (r7.Tris == null) return "—";
                                    Vector3d d7 = q - p;
                                    double l7 = d7.LengthSquared;
                                    int cnt = 0;
                                    var seen7 = new HashSet<(double, double, double)>();
                                    foreach (var t7 in r7.Tris)
                                        foreach (var x7 in new[] { t7.Item1, t7.Item2, t7.Item3 })
                                        {
                                            double s7 = Vector3d.Dot(x7 - p, d7) / l7;
                                            if (s7 <= 1e-6 || s7 >= 1 - 1e-6) continue;
                                            if ((p + d7 * s7 - x7).Length > 1e-6) continue;
                                            if (seen7.Add((Math.Round(x7.X, 6), Math.Round(x7.Y, 6), Math.Round(x7.Z, 6)))) cnt++;
                                        }
                                    return cnt.ToString();
                                }
                                var nb6 = e2.FaceA == ff2 ? e2.FaceB : e2.FaceA;
                                MeshDebug($"  точек внутри стороны: у грани {ff2.Id} — {Inside(ff2)}, у соседней {nb6?.Id} — {Inside(nb6)}");
                                MeshDebug($"  сторону несёт ребро {e2.Id} ({e2.Curve.GetType().Name}, точек {pts2.Count}): грань {ff2.Id} имеет {Where(ff2)}, соседняя {nb6?.Id} имеет {Where(nb6)}");
                            }
                        }
                }
                MeshDebug?.Invoke($"сетка не замкнута: сторона ({p.X:0.####},{p.Y:0.####},{p.Z:0.####})-({q.X:0.####},{q.Y:0.####},{q.Z:0.####}) длина {(p - q).Length:0.####}, вперёд {n} (грани {string.Join(",", owner[(p, q)])}), назад {dir.GetValueOrDefault((q, p))} (грани {(owner.TryGetValue((q, p), out var ob) ? string.Join(",", ob) : "—")}) — {(PerFaceAdoption ? "сетка принята по граням" : "тело на прежней сетке")}");
                if (PerFaceAdoption)
                {
                    foreach (var (fs3, tris3, norms3) in results) { fs3.SetDisplay(tris3, AgreeNormals(tris3, norms3)); }
                        return;
                }
                return;
            }
        // ОБЪЁМ ТЕЛА — НЕ ДАЛЬШЕ ДОПУСКА ГРАНЕНИЯ ОТ ПРЕЖНЕГО (та же мера, что у проверки
        // прогона: площадь × допуск, вдвое). Смещения соседних граней на общем шве гасят друг
        // друга, поэтому мерить надо тело целиком, а не грань по отдельности.
        }
        tClosed.Dispose();
        double vNew = 0, vOld = 0, area = 0;
        foreach (var (fs, tris, _) in results)
        {
            foreach (var (a, b, c) in tris) vNew += Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;
            foreach (var (a, b, c) in fs.Facets)
            {
                vOld += Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;
                area += Vector3d.Cross(b - a, c - a).Length / 2;
            }
        }
        if (Math.Abs(vNew - vOld) > 2 * area * Core.Tol.Chord)
        {
            if (MeshDebug != null)
            {
                var worst = results.Select(r =>
                {
                    double a1 = 0, a2 = 0, ar = 0;
                    foreach (var (a, b, c) in r.Tris) a1 += Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;
                    foreach (var (a, b, c) in r.Fs.Facets) { a2 += Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6; ar += Vector3d.Cross(b - a, c - a).Length / 2; }
                    return (D: a1 - a2, A: ar, Fs: r.Fs);
                }).OrderByDescending(x => Math.Abs(x.D)).Take(3);
                foreach (var w in worst)
                {
                    var fid = solid.Faces.FirstOrDefault(x => ReferenceEquals(x.Surface, w.Fs));
                    MeshDebug($"  грань {fid?.Id} ({w.Fs.Source?.GetType().Name}): объём {w.D:+0.##;-0.##} при площади {w.A:0.#} (среднее смещение {Math.Abs(w.D) / Math.Max(1, w.A):0.####} мм), треугольников {w.Fs.Facets.Count}");
                }
            }
            if (PerFaceAdoption)
            {
                foreach (var (fs2, tris2, norms2) in results) { fs2.SetDisplay(tris2, AgreeNormals(tris2, norms2)); }
                MeshDebug?.Invoke($"объём сетки {vNew:0.##} вместо {vOld:0.##} — сетка принята по граням ({results.Count})");
                return;
            }
            MeshDebug?.Invoke($"объём сетки {vNew:0.##} вместо {vOld:0.##} (площадь {area:0.#}) — тело на прежней сетке");
            return;
        }
        foreach (var (fs, tris, norms) in results) { fs.SetDisplay(tris, AgreeNormals(tris, norms)); }
        MeshDebug?.Invoke($"тело: {results.Count} граней по точной геометрии, {results.Sum(r => r.Tris.Count)} треугольников");
        ExactFaceMesher.Snap = null;
    }

    /// <summary>
    /// Лечить ли почти нулевые рёбра слиянием вершин. ВЫКЛЮЧЕНО: измерено, что становится
    /// хуже — охват падает с 89 до 87 тел, отказов по вырожденным треугольникам 9 → 13, по
    /// точкам контура 5 → 9. Эти рёбра держат стыки: слив их вершины, соседние грани теряют
    /// точку, по которой сходились. Оставлено под переключателем для следующего захода.
    /// </summary>
    private static bool _gHealTinyEdges;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool HealTinyEdges
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.HealTinyEdges : _gHealTinyEdges;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gHealTinyEdges, value, "BooleanOp.HealTinyEdges");
    }

    /// <summary>
    /// Подписи граней по меткам многоугольников: метка — это номер ИСХОДНОЙ грани, и по ней
    /// грань-потомок получает имя своего предка. Это и делает ссылки устойчивыми к изменению
    /// размеров: координаты уезжают, подпись остаётся.
    /// </summary>
    // K04: общий справочник подписей по номеру грани; номера уникальны (Ids атомарны), поэтому
    // справочник может быть общим, но обязан выдерживать одновременную запись.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> TagNames = new();

    /// <summary>
    /// K05. Переименование грани после операции (префикс ключа операции) — и для многоугольников
    /// точного тела, которые несут номер её ИСХОДНОЙ грани. Иначе следующая операция, взявшая
    /// точное тело из памяти, возвращала грани прежнее имя без префикса, и скругление ребра
    /// куба «переименовывало» стенку отверстия: имя менялось, история показывала удалённые
    /// и созданные кромки отверстия, которого операция не касалась.
    /// </summary>
    public static void RenameSourceTag(TFace face)
    {
        if (face.SourceTag != 0 && face.Tag.Length > 0) TagNames[face.SourceTag] = face.Tag;
    }

    internal static void RememberInputTag(TFace face)
    {
        if (face.Tag.Length > 0) TagNames[face.Id] = face.Tag;
    }

    /// <summary>
    /// Сколько потоков использовать на независимых расчётах (пока — гладкость рёбер).
    /// По умолчанию столько, сколько ядер: на одноядерной машине это единица, и раскладка не
    /// включается. Значение задаётся и переключателем P3D_THREADS.
    /// </summary>
    private static int _gThreads =
        int.TryParse(Environment.GetEnvironmentVariable("P3D_THREADS"), out int t) && t > 0
            ? t : Environment.ProcessorCount;
    /// <summary>K04: значение контекста вычисления, если он задан.</summary>
    public static int Threads
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.BooleanThreads : _gThreads;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gThreads, value, "BooleanOp.Threads");
    }

    /// <summary>Пометка текущей операции — для сводных измерений.</summary>
    private static string? _gOpTag;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static string? OpTag
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.OpTag : _gOpTag;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gOpTag, value, "BooleanOp.OpTag");
    }

    // Сколько было на прошлой операции: по разнице и считается вклад нынешней.
    [ThreadStatic] private static long _wasShortcut, _wasTier1, _wasTier2, _wasTier3, _wasVertex;

    /// <summary>Начать счёт заново: вызывается перед замером, иначе первая операция завысит вклад.</summary>
    public static void ResetCounters()
    {
        _wasShortcut = _wasTier1 = _wasTier2 = _wasTier3 = _wasVertex = 0;
    }

    /// <summary>ДИАГНОСТИКА: размер задачи для булевой.</summary>
    public static Action<string>? SizeDebug;

    /// <summary>ДИАГНОСТИКА: итог построения сетки тела.</summary>
    public static Action<string>? MeshDebug;

    /// <summary>ДИАГНОСТИКА: итог построения видимой сетки.</summary>
    public static Action<string>? SnapDebug;

    /// <summary>
    /// Подгонять видимую сетку к точной геометрии (черновик этапа 3). ВЫКЛЮЧЕНО: подгонка
    /// готовых треугольников убирает мелкие отклонения (дальше 0.01 мм: 2585 → 772 вершины на
    /// сцене пользователя), но не зубцы шва касания (дальше 0.05 мм: 659 → 528) — они в самой
    /// форме треугольников разбиения; а разрешённое схлопывание зубцов сводит вершины в одну
    /// точку, и защитная проверка скругления откатывает рез. Этап 3 делается перестроением
    /// сетки грани (точки рёбер + триангуляция в параметрах поверхности), не подгонкой.
    /// </summary>
    /// <summary>ДИАГНОСТИКА: каким путём собрано тело.</summary>
    public static Action<string>? PathDebug;

    private static bool _gExactDisplay = false;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool ExactDisplay
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactDisplay : _gExactDisplay;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gExactDisplay, value, "BooleanOp.ExactDisplay");
    }

    /// <summary>Залечивать тело после булевой (убирать рёбра короче допуска).</summary>
    /// <summary>
    /// Залечивать тело после булевой. ПО УМОЛЧАНИЮ ВЫКЛЮЧЕНО: измерено, что слияние коротких
    /// рёбер роняет охват сетки с 90 тел из 100 до 83 — построитель сетки опирается на те самые
    /// рёбра, которые залечивание убирает. Само залечивание проверено тестами и годится там, где
    /// тело готовят к выводу, а не к дальнейшему построению.
    /// </summary>
    /// <summary>
    /// Залечивать тело после булевой. ПО УМОЛЧАНИЮ ВЫКЛЮЧЕНО, и вот почему, по измерениям:
    /// с общим порогом в микрон охват сетки падал с 90 тел из 100 до 83; после перехода на
    /// СОБСТВЕННЫЕ допуски рёбер стало 88 — лучше, но две сборки всё равно теряются. Значит,
    /// построитель сетки опирается на рёбра, которые по геометрии неразличимы, и убирать их
    /// нельзя, пока он не научится обходиться без них.
    /// </summary>
    /// <summary>Ставить вершины на пересечение точных поверхностей (под переключателем P3D_SNAPV).</summary>
    /// <summary>
    /// Вычитать цилиндрический резец точным путём (без гранения). ПО УМОЛЧАНИЮ ВЫКЛЮЧЕНО, и вот
    /// почему, по измерениям.
    ///
    /// Сама геометрия выходит точной: стенка отверстия перестаёт отходить от цилиндра вовсе
    /// (0 против 5·10⁻⁶ мм прежнего пути). Но построение по квадрике даёт УПРОЩЁННУЮ топологию:
    /// при включении падало семь проверок, после того как кромки сделаны общими рёбрами грани и
    /// стенки — три. Оставшиеся требуют того, чего это построение не создаёт: вершин, делящих
    /// кромку, и полного набора смежностей, на которые опираются скругления и фаски по кромке
    /// отверстия.
    ///
    /// Поэтому общая булева по-прежнему идёт прежним путём, а точный используется там, где он
    /// проверен, — в операции «отверстие» (P3D_QHOLE). Включить здесь можно переключателем
    /// P3D_QCUT=1, помня про топологию.
    ///
    /// СОСТОЯНИЕ НА СЕЙЧАС: проверок точного ядра при широком пути проходят ВСЕ (было семь
    /// отказов, стало ноль). Но всплыли два в других местах, и вот что про них измерено.
    ///
    /// ОБОЛОЧКА. Разобрано по шагам построения полости: после всех плоскостей полость верна —
    /// 729 000 (это 90³). Ошибка появляется на следующем шаге: вычитание трубы R25 даёт
    /// 186 525.8 вместо расчётных 552 286.
    ///
    /// Причина не в оболочке: труба — цилиндр, и ЭТО ВЫЧИТАНИЕ ТОЖЕ УХОДИТ В ТОЧНЫЙ ПУТЬ,
    /// потому что переключатель действует на всю булеву, включая внутренние вызовы других
    /// операций. Там оно и ошибается — снимает 542 474 вместо 176 714.
    ///
    /// Отсюда вывод для следующего шага: точный путь нельзя включать «на всю булеву» одним
    /// переключателем. Его надо включать ТАМ, ГДЕ ИЗВЕСТЕН ЗАМЫСЕЛ, — в операции, которая
    /// сверлит отверстие, а не во всяком вычитании цилиндра, случившемся внутри другой операции.
    /// Именно так он и работает сейчас по умолчанию (в операции «отверстие»), и это оказалось
    /// не временной мерой, а верным устройством.
    ///
    /// ДОПУСКИ РЁБЕР. Проверка ждёт, что после булевой у рёбер появится измеренный допуск; у
    /// тела точного пути рёбра создаются построением, и допуск остаётся нулевым. Это скорее
    /// вопрос к проверке, чем к ядру: у точного тела расхождения кривой с вершинами нет вовсе.
    ///
    /// Пока это не разобрано, путь остаётся за переключателем.
    ///
    /// ЧТО УЖЕ ПРОВЕРЕНО И ОТВЕРГНУТО как причина прежних отказов:
    ///   — бедность кромки: исправлено, кромка из четырёх дуг с вершинами;
    ///   — общие рёбра и встречный обход: исправлено;
    ///   — вскрытие боковых граней: исправлено, случай отвергается;
    ///   — «стенка одной гранью на 360°»: измерено — в обычном пути тоже ОДНА грань стенки;
    ///   — несходящаяся сетка: исправлено общей разметкой рёбер, в том числе для булевой;
    ///   — сквозное вместо глухого: исправлено, протяжённость резца теперь решает;
    ///   — нет точного описания у тела: исправлено, у тела есть описание с квадриками.
    /// ПРИЧИНА НАЙДЕНА, И ОНА НЕ В СКРУГЛЕНИИ. Отказ приходит от булевой ПОСЛЕ построения резца:
    /// «258 сторон без пары на 5 гранях». Замер сетки тела, построенного по квадрике: 302
    /// треугольника, 316 сторон без пары из 611 — тело не сходится при переводе в треугольники.
    ///
    /// Отчего: каждая грань гранится сама по себе, и кромку отверстия крышка размечает 78
    /// точками, а стенка — 144. Точки не совпадают, стороны не находят пары. У обычного пути
    /// этого нет: там точки ребра считаются ОДИН раз и берутся обеими гранями.
    ///
    /// Значит, чтобы тела по квадрике пошли в общую булеву, их грани должны размечать общие
    /// рёбра согласованно — тем же способом, что уже работает в обычном пути. Это и есть
    /// следующий шаг, и он в гранении, а не в булевой и не в скруглении.
    /// </summary>
    private static bool _gExactCylinderCuts = Environment.GetEnvironmentVariable("P3D_QCUT") == "1";
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool ExactCylinderCuts
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactCylinderCuts : _gExactCylinderCuts;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gExactCylinderCuts, value, "BooleanOp.ExactCylinderCuts");
    }

    /// <summary>Протяжённость тела вдоль направления.</summary>
    private static (double Low, double High) Extent(TSolid body, Vector3d dir)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in body.Vertices)
        {
            double t = Vector3d.Dot(v.P, dir);
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }
        return (lo, hi);
    }

    /// <summary>
    /// Цилиндр ли это тело: одна цилиндрическая грань и два торца. Возвращает ось, точку на ней
    /// и радиус.
    /// </summary>
    private static (Vector3d At, Vector3d Axis, double Radius)? CylinderOf(TSolid body)
    {
        Surfaces.ExtrudedSurface? side = null;
        int planes = 0;

        foreach (var f in body.Faces)
        {
            var src = f.Surface is Surfaces.FacetedSurface fs ? fs.Source : f.Surface;
            if (src is Surfaces.ExtrudedSurface ex && ex.Base is ArcCurve3)
            {
                if (side != null) return null;
                side = ex;
            }
            else if (src is Surfaces.PlaneSurface
                     || (src is Surfaces.ExtrudedSurface exl && exl.Base is LineCurve3)) planes++;
            else return null;
        }

        if (side == null || planes != 2) return null;
        if (side.Base is not ArcCurve3 arc) return null;
        if (Math.Abs(arc.Sweep) < 2 * Math.PI - 1e-6) return null;
        if (side.Dir.LengthSquared < 1e-18) return null;

        return (arc.Frame.Origin, side.Dir.Normalized(), arc.Radius);
    }

    private static bool _gSnapVertices = Environment.GetEnvironmentVariable("P3D_SNAPV") == "1";
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool SnapVertices
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.SnapVertices : _gSnapVertices;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gSnapVertices, value, "BooleanOp.SnapVertices");
    }

    private static bool _gHealAfterBoolean = Environment.GetEnvironmentVariable("P3D_HEAL") == "1";
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool HealAfterBoolean
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.HealAfterBoolean : _gHealAfterBoolean;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gHealAfterBoolean, value, "BooleanOp.HealAfterBoolean");
    }

    /// <summary>
    /// Порог залечивания. Допуск склейки (около 10⁻⁵ мм) оставляет рёбра длиной в доли микрона,
    /// а расхождение характеристики дают именно они, поэтому порог берётся крупнее — микрон.
    /// </summary>
    private static double _gHealTolerance = double.TryParse(Environment.GetEnvironmentVariable("P3D_HEALTOL"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double t) && t > 0
            ? t : 1e-3;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static double HealTolerance
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.HealTolerance : _gHealTolerance;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gHealTolerance, value, "BooleanOp.HealTolerance");
    }

    /// <summary>
    /// ВИДИМАЯ СЕТКА — ПО ТОЧНОЙ ГЕОМЕТРИИ (этап 3, первый шаг).
    ///
    /// ДОБАВЛЕНО. Треугольники граней шли прямо из разбиения пространства — хорды резца. На
    /// касании скругления с гранью ошибка хорды δ даёт поперечный сдвиг √(2rδ), у R5 до
    /// полумиллиметра: край грани зубцами, точное ребро местами уходит под поверхность и
    /// видно кусками (снимок пользователя 22.09). Точное тело булевой не трогается; меняется
    /// сетка, которую видит пользователь (отрисовка, экспорт, проверки):
    ///   • вершина шва двух граней — на точную кривую их общего ребра (одна точка для обеих
    ///     граней — щелей нет по построению);
    ///   • вершина внутри кривой грани — на её точную поверхность;
    ///   • углы тела (вершины рёбер) — на месте.
    /// Сдвиг, выворачивающий хоть один примыкающий треугольник, уменьшается вдвое (до трёх
    /// раз), иначе вершина остаётся на месте.
    /// </summary>
    private static void SnapDisplay(Dictionary<uint, List<Tri>> groups, Dictionary<uint, TFace> faces,
                                    VertexWeld weld, TSolid solid)
    {
        var P = weld.Points;
        int n = P.Count;
        // Метки граней у каждой вершины и треугольники вокруг неё.
        var tagsAt = new List<uint>?[n];
        var around = new List<(int A, int B, int C)>?[n];
        foreach (var (tag, list) in groups)
            foreach (var t in list)
                foreach (int v in new[] { t.A, t.B, t.C })
                {
                    (tagsAt[v] ??= new List<uint>()).Add(tag);
                    (around[v] ??= new List<(int, int, int)>()).Add((t.A, t.B, t.C));
                }

        // Углы тела — вершины рёбер: не двигаются.
        var corner = new HashSet<(double, double, double)>();
        foreach (var e in solid.Edges)
        {
            corner.Add((e.V0.P.X, e.V0.P.Y, e.V0.P.Z));
            corner.Add((e.V1.P.X, e.V1.P.Y, e.V1.P.Z));
        }

        // Рёбра по паре граней.
        var tagOf = faces.ToDictionary(kv => kv.Value, kv => kv.Key);
        var edgesOf = new Dictionary<(uint, uint), List<TEdge>>();
        foreach (var e in solid.Edges)
        {
            if (e.FaceA == null || e.FaceB == null) continue;
            if (!tagOf.TryGetValue(e.FaceA, out uint ta) || !tagOf.TryGetValue(e.FaceB, out uint tb)) continue;
            var key = ta < tb ? (ta, tb) : (tb, ta);
            if (!edgesOf.TryGetValue(key, out var l)) edgesOf[key] = l = new List<TEdge>();
            l.Add(e);
        }

        static ISurface3? SourceOf(TFace f) => f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

        var target = new Vector3d[n];
        var moved = new bool[n];
        for (int v = 0; v < n; v++)
        {
            target[v] = P[v];
            var tags = tagsAt[v];
            if (tags == null) continue;
            var p = P[v];
            if (corner.Contains((p.X, p.Y, p.Z))) continue;
            var distinct = tags.Distinct().ToList();
            if (distinct.Count == 2)
            {
                var key = distinct[0] < distinct[1] ? (distinct[0], distinct[1]) : (distinct[1], distinct[0]);
                if (!edgesOf.TryGetValue(key, out var es)) continue;
                double best = 1.0;                              // дальше миллиметра — не своё ребро
                foreach (var e in es)
                {
                    if (e.Curve is LineCurve3) continue;        // хорда отрезка и есть отрезок
                    double t = CurveParam.Closest(e.Curve, p);
                    t = Math.Clamp(t, e.Curve.T0, e.Curve.T1);
                    Vector3d q = e.Curve.PointAt(t);
                    double d = (q - p).Length;
                    if (d < best) { best = d; target[v] = q; moved[v] = d > 1e-12; }
                }
            }
            else if (distinct.Count == 1 && faces.TryGetValue(distinct[0], out var f)
                     && SourceOf(f) is { } src && !src.IsPlanar && src is not FacetedSurface)
            {
                Vector3d q = src.PointAt(src.ProjectToUV(p));
                double d = (q - p).Length;
                if (d < 1.0 && d > 1e-12) { target[v] = q; moved[v] = true; }
            }
        }

        // Сдвиги — без выворота примыкающих треугольников.
        var pos = P.ToArray();
        int full = 0, part = 0, kept = 0; double left = 0;
        // Несколько проходов: вершина зубца упирается в ещё не сдвинутых соседей и доходит
        // до места, когда сдвинулись они.
        for (int pass = 0; pass < 8; pass++)
        {
        full = 0; part = 0; kept = 0; left = 0;
        bool progress = false;
        for (int v = 0; v < n; v++)
        {
            if (!moved[v] || around[v] == null) continue;
            if ((pos[v] - target[v]).LengthSquared < 1e-24) { full++; continue; }
            Vector3d from = pos[v], to = target[v];
            for (int h = 0; h < 4; h++)
            {
                pos[v] = to;
                bool ok = true;
                foreach (var (a, b, c) in around[v]!)
                {
                    Vector3d n0 = Vector3d.Cross(P[b] - P[a], P[c] - P[a]);
                    Vector3d n1 = Vector3d.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                    if (n0.LengthSquared < 1e-24) continue;
                    if (Vector3d.Dot(n0, n1) <= 0) { ok = false; break; }
                }
                if (ok) { if (h == 0) full++; else part++; progress = true; break; }
                to = (from + to) / 2;
                if (h == 3) { pos[v] = from; kept++; }
            }
            left = Math.Max(left, (pos[v] - target[v]).Length);
        }
        if (!progress) break;
        }
        if (SnapDebug != null)
        {
            int b05 = 0, a05 = 0, b01 = 0, a01 = 0, cnt = 0;
            for (int v = 0; v < n; v++)
            {
                if (!moved[v]) continue;
                cnt++;
                double db = (P[v] - target[v]).Length, da = (pos[v] - target[v]).Length;
                if (db > 0.05) b05++; if (da > 0.05) a05++;
                if (db > 0.01) b01++; if (da > 0.01) a01++;
            }
            SnapDebug($"сетка по точной геометрии: сдвигаемых вершин {cnt}; дальше 0.05 мм от точной геометрии: до {b05}, после {a05}; дальше 0.01: до {b01}, после {a01}; наибольший остаток {left:0.###} мм");
        }

        foreach (var (tag, list) in groups)
        {
            if (!faces.TryGetValue(tag, out var face) || face.Surface is not FacetedSurface fs) continue;
            var shown = new List<(Vector3d, Vector3d, Vector3d)>(list.Count);
            foreach (var t in list) shown.Add((pos[t.A], pos[t.B], pos[t.C]));
            // Прежде менялись только треугольники, нормали оставались прежними (читатель берёт их лишь при равном
            // числе) — та же семантика, но одной заменой.
            fs.SetDisplay(shown, fs.DisplayNormals);
        }
    }

    private readonly struct Tri
    {
        public readonly int A, B, C;
        public readonly uint Tag;
        public Tri(int a, int b, int c, uint tag) { A = a; B = b; C = c; Tag = tag; }
    }

    /// <summary>
    /// Устраняет T-стыки: сторона треугольника, посреди которой лежит чужая
    /// вершина, разрезается этой вершиной.
    ///
    /// Поиск ускорен решёткой: перебирать все точки для каждой стороны
    /// нельзя, это произведение двух больших чисел. Решётка сводит проверку
    /// к точкам, лежащим рядом со стороной.
    ///
    /// Разрез порождает новые стороны, поэтому проходы повторяются
    /// до устойчивости. Предел на число проходов — страховка: при
    /// вырожденной геометрии процесс мог бы не сойтись.
    /// </summary>
    /// <summary>
    /// Выбрасывает повторно пришедшие треугольники. Возвращает их число.
    /// </summary>
    private static int RemoveDuplicates(List<Tri> tris)
    {
        var seen = new HashSet<(int, int, int, uint)>();
        var kept = new List<Tri>(tris.Count);

        foreach (Tri t in tris)
        {
            int a = t.A, b = t.B, c = t.C;

            // Наименьший номер вперёд, остальные два по возрастанию:
            // так у одного и того же набора вершин ключ всегда один.
            if (b < a) (a, b) = (b, a);
            if (c < a) (a, c) = (c, a);
            if (c < b) (b, c) = (c, b);

            if (seen.Add((a, b, c, t.Tag))) kept.Add(t);
        }

        int dropped = tris.Count - kept.Count;

        if (dropped > 0)
        {
            tris.Clear();
            tris.AddRange(kept);
        }

        return dropped;
    }

    private static void RemoveTJunctions(List<Tri> tris, VertexWeld weld)
    {
        // ПРОВЕРЯЕТСЯ ТОЛЬКО ТО, ЧТО ИЗМЕНИЛОСЬ.
        //
        // ИСПРАВЛЕНО, И ЭТО НАСТОЯЩАЯ ПРИЧИНА ЗАВИСАНИЯ. Раньше каждый
        // проход перебирал ВЕСЬ набор заново и для каждого треугольника
        // снова искал точку на каждой из трёх сторон. Шестьдесят четыре
        // прохода по двадцати тысячам треугольников — это миллиарды
        // обращений к решётке, и ускорение самого поиска тут не спасало:
        // работы всё равно оставалось на часы.
        //
        // Между тем набор точек внутри этой процедуры НЕ МЕНЯЕТСЯ.
        // Треугольник, у которого точки на сторонах не нашлось, её уже
        // не получит — проверять его снова незачем. Новыми бывают только
        // половинки разрезанных.
        //
        // Отсюда очередь вместо проходов: взяли треугольник, разрезали —
        // обе половинки обратно в очередь, не разрезали — в результат.
        // Работа становится соразмерной числу разрезов, а не их квадрату.
        var grid = new PointGrid(weld.Points);

        var queue = new Stack<Tri>(tris);
        var done = new List<Tri>(tris.Count);

        // Запас разрезов: при геометрии, которая не сходится, очередь
        // росла бы бесконечно. Незакрытые T-стыки — беда меньшая, чем
        // зависание.
        // Запас скромный НАМЕРЕННО. На здоровой геометрии разрезов нужны
        // сотни; тысячи означают, что вход выродился, и дальнейшее дробление
        // ничего не спасёт, а время съест: при двадцати тысячах разрезов
        // перестроение уходило на минуты.
        int budget = Math.Max(4_000, tris.Count);

        while (queue.Count > 0)
        {
            Tri t = queue.Pop();

            if (budget > 0 && TrySplit(t, out Tri x, out Tri y))
            {
                budget--;
                queue.Push(x);
                queue.Push(y);
            }
            else done.Add(t);
        }

        tris.Clear();
        tris.AddRange(done);

        bool TrySplit(Tri t, out Tri first, out Tri second)
        {
            first = second = default;

            // ВЫРОЖДЕННЫЙ ТРЕУГОЛЬНИК НЕ РЕЖЕТСЯ.
            //
            // У треугольника нулевой площади все три вершины на одной
            // прямой, и точка, лежащая «на стороне», найдётся у любой
            // из сторон — сколько ни режь. Площади у него нет, в оболочку
            // он ничего не вносит, и делить его не за чем.
            Vector3d pa = weld.Points[t.A], pb = weld.Points[t.B], pc = weld.Points[t.C];

            if (Vector3d.Cross(pb - pa, pc - pa).LengthSquared < 1e-20) return false;

            // Стороны перебираются вместе с противоположной вершиной:
            // разрез делит треугольник на два через неё.
            if (Cut(t.A, t.B, t.C, out first, out second, t.Tag)) return true;
            if (Cut(t.B, t.C, t.A, out first, out second, t.Tag)) return true;
            if (Cut(t.C, t.A, t.B, out first, out second, t.Tag)) return true;
            return false;
        }

        bool Cut(int u, int v, int w, out Tri first, out Tri second, uint tag)
        {
            first = second = default;

            int m = grid.FindOnSegment(weld.Points[u], weld.Points[v], u, v, w);
            if (m < 0) return false;

            // РАЗРЕЗ НЕ ДОЛЖЕН ПОРОЖДАТЬ ИГОЛКИ.
            //
            // Точка, лежащая на стороне почти вплотную к её концу, даёт
            // треугольник-иголку: площадь у него не нулевая, проверку
            // на вырожденность он проходит, а у его сторон снова находятся
            // точки. Очередь на таких иголках растёт, не сходясь.
            //
            // Порог АБСОЛЮТНЫЙ и мелкий — чуть больше допуска склейки.
            //
            // ИСПРАВЛЕНО. Стояла тысячная доля длины стороны, и на ребре
            // длиной 100 это давало 0.1. Столько же — длина хорды гранёного
            // скругления, так что отвергались совершенно законные разрезы
            // у кромки скругления. Оболочка оставалась с трещинами, цепочки
            // хорд рвались, дуги переставали распознаваться, а за ними
            // перестал работать и выбор цепочки рёбер.
            //
            // От иголок защищает уже сам запас разрезов; здесь довольно
            // отсечь точки, которые от конца стороны практически
            // неотличимы.
            Vector3d pu = weld.Points[u], pv = weld.Points[v];

            double Gap = WeldTol * 10;

            if ((weld.Points[m] - pu).Length < Gap) return false;
            if ((weld.Points[m] - pv).Length < Gap) return false;

            first = new Tri(u, m, w, tag);
            second = new Tri(m, v, w, tag);
            return true;
        }
    }

    /// <summary>Решётка точек для поиска вершин, лежащих на отрезке.</summary>
    private sealed class PointGrid
    {
        private readonly List<Vector3d> _points;
        private readonly Dictionary<(long, long, long), List<int>> _cells = new();
        private readonly double _cell;

        public PointGrid(List<Vector3d> points)
        {
            _points = points;

            // Размер клетки — от габарита набора: слишком мелкая клетка даёт
            // много пустых, слишком крупная — обратно перебор всех точек.
            var min = new Vector3d(double.MaxValue);
            var max = new Vector3d(double.MinValue);

            foreach (var p in points)
            {
                min = Vector3d.ComponentMin(min, p);
                max = Vector3d.ComponentMax(max, p);
            }

            double diag = points.Count > 0 ? (max - min).Length : 1;
            _cell = Math.Max(diag / 32.0, 1e-6);

            for (int i = 0; i < points.Count; i++)
            {
                var key = Key(points[i]);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                list.Add(i);
            }
        }

        private (long, long, long) Key(Vector3d p) =>
            ((long)Math.Floor(p.X / _cell), (long)Math.Floor(p.Y / _cell), (long)Math.Floor(p.Z / _cell));

        /// <summary>
        /// Ближайшая к началу точка, лежащая СТРОГО ВНУТРИ отрезка.
        /// -1 — таких нет.
        /// </summary>
        public int FindOnSegment(Vector3d a, Vector3d b, int skip0, int skip1, int skip2)
        {
            Vector3d ab = b - a;
            double len2 = ab.LengthSquared;
            if (len2 < 1e-18) return -1;

            int best = -1;
            double bestT = double.MaxValue;

            // ИДЁМ ВДОЛЬ ОТРЕЗКА, А НЕ ПО ЕГО ГАБАРИТУ.
            //
            // ИСПРАВЛЕНО, И ИМЕННО ЗДЕСЬ БЫЛО ЗАВИСАНИЕ. Перебирался
            // габаритный ящик отрезка — а у диагонального ребра через всю
            // деталь это вся решётка, тридцать два куба по стороне,
            // тридцать пять тысяч клеток. На один вызов. Вызовов же —
            // три на треугольник, и так каждый проход.
            //
            // Пока треугольников были сотни, это терпелось. Стоило разбиению
            // начать их плодить — и работа пошла на миллиарды обращений
            // к словарю. Снаружи это выглядит как вечный цикл, хотя предел
            // проходов честно стоял: просто до него не доживали.
            //
            // Точки, лежащие НА отрезке, лежат и рядом с ним, поэтому
            // достаточно клеток вдоль самого отрезка. Их число растёт
            // с длиной, а не с объёмом: у ребра через всю деталь — десятки
            // вместо десятков тысяч.
            double len = Math.Sqrt(len2);
            int steps = (int)Math.Ceiling(len / (_cell * 0.5)) + 1;

            var seen = new HashSet<(long, long, long)>();

            for (int s = 0; s <= steps; s++)
            {
                Vector3d at = a + ab * (s / (double)steps);
                var k = Key(at);

                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        for (long dz = -1; dz <= 1; dz++)
                        {
                            var cell = (k.Item1 + dx, k.Item2 + dy, k.Item3 + dz);

                            if (!seen.Add(cell)) continue;
                            if (!_cells.TryGetValue(cell, out var bucket)) continue;

                            foreach (int i in bucket)
                            {
                                if (i == skip0 || i == skip1 || i == skip2) continue;

                                double t = Vector3d.Dot(_points[i] - a, ab) / len2;
                                if (t <= 1e-9 || t >= 1 - 1e-9) continue;

                                Vector3d q = a + ab * t;
                                double d2 = (_points[i] - q).LengthSquared;

                                // Допуск на «лежит на отрезке» относительный:
                                // у длинного ребра абсолютный оказался бы
                                // непомерно строгим.
                                if (d2 > OnSegmentTol * OnSegmentTol * Math.Max(1.0, len2)) continue;

                                if (t < bestT) { bestT = t; best = i; }
                            }
                        }
            }

            return best;
        }
    }

    /// <summary>Относительный допуск отнесения точки к отрезку.</summary>
    private const double OnSegmentTol = 1e-9;

    /// <summary>
    /// Восстанавливает рёбра и вершины по стыкам групп граней.
    ///
    /// Ребро группы — сторона треугольника, у которой внутри той же группы
    /// нет парной противоположной. Настоящее ребро замкнутого тела
    /// разделяет РОВНО ДВЕ грани, поэтому стороны с одним владельцем
    /// отбрасываются: рисовать их значит чертить линии посреди плоской грани.
    ///
    /// Вершина тела — точка, где сходятся ТРИ И БОЛЬШЕ граней. Раньше
    /// вершина создавалась на каждом конце каждого куска границы, и на теле
    /// высыпались сотни точек, которых там нет. У куба со сквозным
    /// цилиндрическим отверстием на 32 сегмента таких настоящих вершин 72,
    /// а точек на границах было 118.
    ///
    /// Коллинеарные куски одного ребра сливаются: разрезы дробят прямое
    /// ребро на отрезки, и без слияния каркас выглядит рваным. Слияние
    /// допускается только в точке, которая НЕ вершина тела, — иначе оно
    /// съедает настоящие углы. Именно на этом прошлая попытка и провалилась.
    /// </summary>
    private static void BuildEdges(Dictionary<uint, List<Tri>> groups,
                                   Dictionary<uint, TFace> faces,
                                   VertexWeld weld,
                                   TSolid solid,
                                   List<string>? warnings = null,
                                   bool exactEdges = true)
    {
        var smoothQueue = new List<TEdge>();
        using var profOwners = Diagnostics.Prof.Time("рёбра: владельцы сторон");

        // Кто из граней касается каждой неупорядоченной пары точек.
        // ПРОБОВАЛ ключ стороны одним 64-разрядным числом вместо пары: измерено в тишине трижды
        // — 1 204, 1 281, 1 234 мс против 635 мс у пары. Медленнее, потому что пара из двух int
        // хешируется и сравнивается без упаковки, а упаковка добавляет сдвиги на каждой стороне.
        // ВЛАДЕЛЬЦЫ СТОРОН — ПО ГРУППАМ, И ГРУППЫ НЕЗАВИСИМЫ. Каждая грань сама определяет свои
        // граничные стороны (те, у которых нет встречной внутри группы); общий словарь собирается
        // потом, уже из готовых списков. Такое разделение и позволяет считать группы в несколько
        // потоков: каждая пишет только в свой список.
        var perGroup = new List<(uint Tag, List<(int A, int B)> Sides)>();
        foreach (var (tag, _) in groups) perGroup.Add((tag, new List<(int, int)>()));

        void OwnersOf(int gi)
        {
            var (tag, tris) = (perGroup[gi].Tag, groups[perGroup[gi].Tag]);
            var directed = new HashSet<(int, int)>();
            foreach (var t in tris)
            {
                directed.Add((t.A, t.B));
                directed.Add((t.B, t.C));
                directed.Add((t.C, t.A));
            }

            var mine = perGroup[gi].Sides;
            foreach (var (a, b) in directed)
            {
                if (directed.Contains((b, a))) continue;   // сторона внутри группы
                mine.Add(a < b ? (a, b) : (b, a));
            }
        }

        if (Threads > 1 && Environment.ProcessorCount > 1)
            System.Threading.Tasks.Parallel.For(0, perGroup.Count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Threads }, OwnersOf);
        else
            for (int gi = 0; gi < perGroup.Count; gi++) OwnersOf(gi);

        var owners = new Dictionary<(int, int), HashSet<uint>>();
        foreach (var (tag, sides) in perGroup)
            foreach (var key in sides)
            {
                if (!owners.TryGetValue(key, out var set)) owners[key] = set = new HashSet<uint>();
                set.Add(tag);
            }

        profOwners.Dispose();
        using var profSeg = Diagnostics.Prof.Time("рёбра: сборка отрезков и цепочек");

        // Только стороны, разделяющие две грани.
        var segments = new List<(int A, int B, HashSet<uint> Owners)>();
        var lonelyByTag = new Dictionary<uint, (int Count, double Len)>();
        foreach (var (key, set) in owners)
        {
            if (set.Count >= 2) { segments.Add((key.Item1, key.Item2, set)); continue; }
            // Граничная сторона без пары: ребра из неё не выйдет, и грань останется без границы.
            double len = (weld.Points[key.Item1] - weld.Points[key.Item2]).Length;
            foreach (uint tg in set)
            {
                var cur = lonelyByTag.GetValueOrDefault(tg);
                lonelyByTag[tg] = (cur.Count + 1, cur.Len + len);
            }
        }
        if (warnings != null && lonelyByTag.Count > 0)
        {
            int tot = lonelyByTag.Sum(kv => kv.Value.Count);
            double totLen = lonelyByTag.Sum(kv => kv.Value.Len);
            warnings.Add($"Boolean: {tot} boundary side(s) without a partner on {lonelyByTag.Count} face(s), total length {totLen:0.##}.");
        }
        // K02/K08: результат с непарными граничными сторонами — открытое тело. Прежде это было
        // только предупреждение, и операция «успешно» теряла бобышку целиком (объём 320 000 вместо
        // 322 038 у реалистичной детали при прогибе 0.002 мм). Теперь это отказ операции.
        if (lonelyByTag.Count > 0)
            OperationTrace.Report(OperationCodes.OpenResult,
                $"{lonelyByTag.Sum(kv => kv.Value.Count)} boundary side(s) without a partner on {lonelyByTag.Count} face(s)");

        // Настоящие вершины: сходятся три и больше граней.
        var atPoint = new Dictionary<int, HashSet<uint>>();
        foreach (var s in segments)
        {
            Collect(s.A); Collect(s.B);
            void Collect(int i)
            {
                if (!atPoint.TryGetValue(i, out var set)) atPoint[i] = set = new HashSet<uint>();
                set.UnionWith(s.Owners);
            }
        }

        var corners = new HashSet<int>();
        foreach (var (i, set) in atPoint)
            if (set.Count >= 3) corners.Add(i);

        using (Diagnostics.Prof.Time("рёбра: слияние коллинеарных"))
            MergeCollinear(segments, weld, corners);

        // Дуги распознаются ПОСЛЕ прямых: слияние коллинеарных кусков
        // уже убрало лишние точки с прямых участков, и всё, что осталось
        // цепочками мелких звеньев, — кандидаты в дуги.
        int chordsBefore = segments.Count;

        // Дуги — по концам И граням. ИСПРАВЛЕНО: ключом были только концы, и прямое ребро
        // с теми же концами, что у дуги, получало её кривую: край глухого отверстия,
        // вскрывшего боковую грань, — дуга по дну и прямая по боковой грани с общими
        // концами; прямая становилась «дугой R8.74», и фаска резала не то (нашёл прогон).
        Dictionary<(int, int), List<(HashSet<uint> Owners, ICurve3 Curve)>> arcs;
        using (Diagnostics.Prof.Time("рёбра: кривые (MergeArcs)"))
            arcs = MergeArcs(segments, weld, corners, warnings, exactEdges ? faces : null);

        // СКОЛЬКО ДУГ УЗНАНО. Без этого «дуга осталась ломаной» и «дугу
        // узнать не удалось» выглядят одинаково: точки на экране.
        // Пар в словаре вдвое больше, чем дуг: каждая записана в обе
        // стороны.
        int arcCount = arcs.Values.Sum(l => l.Count) / 2;
        if (arcCount > 0)
            warnings?.Add($"Boolean: {arcCount} chord chain(s) restored to arcs " +
                          $"({chordsBefore - segments.Count + arcCount} segments collapsed).");

        var vertices = new Dictionary<int, TVertex>();

        TVertex Vertex(int i)
        {
            if (vertices.TryGetValue(i, out var v)) return v;
            v = new TVertex(weld.Points[i]);
            vertices[i] = v;
            return v;
        }

        foreach (var (a, b, tags) in segments)
        {
            var v0 = Vertex(a);
            var v1 = Vertex(b);

            ICurve3? arc = arcs.TryGetValue((a, b), out var candidates)
                ? candidates.FirstOrDefault(c => c.Owners.SetEquals(tags)).Curve
                : null;
            ICurve3 curve = arc ?? new LineCurve3(v0.P, v1.P);

            var edge = new TEdge { V0 = v0, V1 = v1, Curve = curve };

            // Ребро принадлежит всем граням, что на нём сходятся: только так
            // TSolid.Edges его увидит и каркас будет нарисован.
            foreach (uint tag in tags)
            {
                if (!faces.TryGetValue(tag, out var face)) continue;

                TLoop loop = face.Loops.Count > 0 ? face.Loops[0] : face.AddLoop(true);
                loop.Add(edge, false);
            }

            smoothQueue.Add(edge);       // гладкость считается после сборки, сразу для всех рёбер
        }

        profSeg.Dispose();

        // ГЛАДКОСТЬ РЁБЕР — В НЕСКОЛЬКО ПОТОКОВ, НО ПОСЛЕ ПОДГОТОВКИ ОБЩИХ ДАННЫХ. Расчёт
        // независим для каждого ребра, однако точки ребра считаются лениво и складываются в общий
        // кэш: при раскладке «в лоб» потоки считали их наперегонки, и всё становилось вчетверо
        // дороже. Поэтому точки рёбер сперва считаются последовательно, и лишь затем нормали
        // раскладываются по потокам.
        using (Diagnostics.Prof.Time("рёбра: точки (подготовка)"))
            foreach (var e in smoothQueue) Tess.ExactFaceMesher.EdgePoints(e);

        using (Diagnostics.Prof.Time("рёбра: гладкость (MarkSmooth)"))
        {
            // Раскладка по потокам включается, только если ядер больше одного И это разрешено
            // явно. На одном ядре она лишь добавляет расходы: измерено 528 → 1 376 мс даже с
            // подготовленными данными (а без подготовки 2 140).
            if (Threads > 1 && Environment.ProcessorCount > 1)
                System.Threading.Tasks.Parallel.ForEach(
                    smoothQueue,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Threads },
                    MarkSmooth);
            else
                foreach (var e in smoothQueue) MarkSmooth(e);
        }

        // ПРЕЖНЯЯ ПОПЫТКА без подготовки: Расчёт независим для каждого
        // ребра, но измерено, что при раскладке по потокам он становится вчетверо дороже
        // (528 → 2 140 мс), а вся сцена — вдвое (5 422 → 10 328 мс): потоки борются за общие
        // кэши точек рёбер и теряют подсказки поиска ближайшей точки.

        // НОМЕР КУСКА. Одно исходное ребро режется другими операциями на куски; у всех кусков
        // одна пара подписей граней, и ссылка на конкретный кусок падала обратно на координаты
        // (измерено: 48% имён неоднозначны). Куски одной пары упорядочиваются вдоль общего
        // направления от определённого конца и получают номер — он переживает изменение
        // размеров, пока число кусков не меняется.
        {
            var byPair = new Dictionary<string, List<TEdge>>();
            foreach (var e in solid.Edges)
            {
                string ta = e.FaceA?.Tag ?? "", tb = e.FaceB?.Tag ?? "";
                if (ta.Length == 0 || tb.Length == 0) continue;
                string key = string.CompareOrdinal(ta, tb) <= 0 ? ta + "|" + tb : tb + "|" + ta;
                if (!byPair.TryGetValue(key, out var l)) byPair[key] = l = new List<TEdge>();
                l.Add(e);
            }

            foreach (var (key, list) in byPair)
            {
                if (list.Count < 2) continue;
                // Начало отсчёта — самая «малая» точка по координатам: она не зависит от порядка
                // построения.
                static double Order(Vector3d p) => p.X * 1e6 + p.Y * 1e3 + p.Z;
                var sorted = list.OrderBy(e => Math.Min(Order(e.V0.P), Order(e.V1.P))).ToList();
                for (int i = 0; i < sorted.Count; i++) sorted[i].Tag = $"{key}#{i}";
            }
        }
    }

    /// <summary>
    /// ЦЕПОЧКА ХОРД — ОБРАТНО В ДУГУ.
    ///
    /// Разбиение пространства режет кривую грань на треугольники, и её
    /// граница выходит ломаной: у скругления R10 это шестнадцать хорд
    /// вместо одной дуги. В каркасе они видны точками, выбор ребра берёт
    /// одну хорду из шестнадцати, а фаска на такой хорде бессмысленна.
    ///
    /// Задача обратная слиянию коллинеарных кусков и решается так же:
    /// найти цепочку звеньев, у которых совпадает набор граней и в стыках
    /// не сходится ничего третьего, — и заменить её одним ребром.
    /// Разница в проверке: там прямая, здесь окружность.
    ///
    /// РАСПОЗНАЁТСЯ, А НЕ ПОДГОНЯЕТСЯ. Окружность строится по трём точкам
    /// цепочки — первой, средней и последней, — после чего проверяются
    /// ВСЕ остальные. Подгонка по методу наименьших квадратов дала бы
    /// ответ всегда, в том числе на ломаной, дугой не являющейся.
    /// Здесь же несовпадение хоть одной точки отменяет замену целиком.
    ///
    /// Возвращает соответствие «пара концов -> дуга». Пары, которых в нём
    /// нет, остаются отрезками.
    /// </summary>
    /// <summary>Исходные поверхности двух граней-владельцев (обе известны и не гранёные), иначе null.</summary>
    private static (ISurface3 A, ISurface3 B)? SourcesOf(HashSet<uint> owners, Dictionary<uint, TFace> faces)
    {
        if (owners.Count != 2) return null;
        var src = new List<ISurface3>();
        foreach (uint tag in owners)
        {
            if (!faces.TryGetValue(tag, out var f)) return null;
            ISurface3? s = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            if (s == null || s is FacetedSurface) return null;
            src.Add(s);
        }
        return (src[0], src[1]);
    }

    /// <summary>
    /// Точная кривая, которая оказалась окружностью, — дуга (точнее и дешевле, и
    /// специальные построители её берут): край фаски по скруглению на плоской грани.
    /// </summary>
    private static ICurve3? ArcIfRound(ICurve3? c)
    {
        if (c == null) return null;
        var pts = new List<Vector3d>();
        c.Tessellate(pts, true);
        return FitArc(pts) ?? c;
    }

    /// <summary>Кривая пересечения исходных поверхностей двух граней-владельцев цепочки, если она сходится.</summary>
    private static ICurve3? IntersectionOf(HashSet<uint> owners, List<Vector3d> points, Dictionary<uint, TFace> faces)
    {
        if (owners.Count != 2) return null;
        var src = new List<ISurface3>();
        foreach (uint tag in owners)
        {
            if (!faces.TryGetValue(tag, out var f)) return null;
            ISurface3? s = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            if (s == null || s is FacetedSurface) return null;
            src.Add(s);
        }
        if (src[0].IsPlanar && src[1].IsPlanar) return null;          // пересечение плоскостей — прямая
        // Край процедурной поверхности (линия касания скругления) — изолиния, а не пересечение:
        // у касающихся поверхностей пересечение не уточняется.
        return ArcIfRound(SectionIsoCurve.TryBuild(src[0], src[1], points))
               ?? IntersectionCurve3.TryBuild(src[0], src[1], points);
    }

    private static Dictionary<(int, int), List<(HashSet<uint> Owners, ICurve3 Curve)>> MergeArcs(
        List<(int A, int B, HashSet<uint> Owners)> segments,
        VertexWeld weld,
        HashSet<int> corners,
        List<string>? warnings = null,
        Dictionary<uint, TFace>? faces = null)
    {
        // ОКРУЖНОСТЬ С ИСХОДНОЙ ПОВЕРХНОСТИ ГРАНИ.
        //
        // ИСПРАВЛЕНО. Окружность подгонялась только по точкам. У короткой дуги
        // края выреза — три хорды, оба конца которых пересечения с хордами,
        // а не с окружностью, — внутренних точек для подгонки не хватало: дуга
        // не восстанавливалась, или восстанавливалась с ошибкой в полмиллиметра
        // (R39.442 вместо 40), и резец фаски строился по неверной окружности.
        // Если цепочка лежит на грани-цилиндре, окружность берётся с его исходной
        // поверхности — центр и радиус там точные, — а точки только проверяются.
        (Vector3d Centre, Vector3d Axis, double Radius)? KnownCircle(HashSet<uint> owners, Vector3d onPlane)
        {
            if (faces == null) return null;
            foreach (uint tag in owners)
            {
                if (!faces.TryGetValue(tag, out TFace? f)) continue;
                ISurface3? src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
                if (src is not ExtrudedSurface ex || ex.Base is not ArcCurve3 ba)
                {
                    // ПОСТАВКА 4: ПОВЕРХНОСТЬ ВРАЩЕНИЯ — цилиндр или конус из отрезка-образующей (зенковка прежнего пути —
                    // конус вращением; составной инструмент): окружность ⟂ оси на уровне цепочки. Прежде такие грани
                    // пропускались, и дуга подгонялась по точкам (R 6.999993…7.000014 вместо 7). Точки цепочки, как и
                    // для выдавливания, только проверяются по этой окружности.
                    if (src is RevolvedSurface && Geometry.ExactEdges.ShapeOf(src) is { } shape)
                    {
                        if (shape is Geometry.Ssi.Cylinder cy)
                        {
                            var ax = cy.Axis.Normalized();
                            return (cy.P + ax * Vector3d.Dot(onPlane - cy.P, ax), ax, cy.R);
                        }
                        if (shape is Geometry.Ssi.Cone co)
                        {
                            var ax = co.Axis.Normalized();
                            double h = Vector3d.Dot(onPlane - co.Apex, ax);
                            if (h > 1e-9 && double.IsFinite(h)) return (co.Apex + ax * h, ax, h * Math.Tan(co.HalfAngle));
                        }
                    }
                    continue;
                }

                Vector3d dir = ex.Dir;
                if (dir.LengthSquared < 1e-18) continue;
                dir = dir.Normalized();
                if (Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), dir)) < 1 - 1e-9) continue;

                Vector3d o = ba.Frame.Origin;
                return (o + dir * Vector3d.Dot(onPlane - o, dir), dir, ba.Radius);
            }
            return null;
        }

        var result = new Dictionary<(int, int), List<(HashSet<uint> Owners, ICurve3 Curve)>>();
        void Put(int a, int b, HashSet<uint> owners, ICurve3 c)
        {
            if (!result.TryGetValue((a, b), out var l)) result[(a, b)] = l = new List<(HashSet<uint>, ICurve3)>();
            l.Add((owners, c));
        }

        // Самая длинная цепочка и причина, по которой её отвергли, —
        // чтобы «дуг не найдено» не было немым ответом.
        int longest = 0;
        string reason = "цепочек длиннее двух звеньев не нашлось";

        // Куда входит каждое звено. Ключ включает набор граней: звенья
        // разных рёбер, сошедшихся в точке, в одну цепочку не собираются.
        var incident = new Dictionary<(int Point, string Key), List<int>>();

        for (int i = 0; i < segments.Count; i++)
        {
            string key = OwnersKey(segments[i].Owners);
            Add(segments[i].A, key, i);
            Add(segments[i].B, key, i);
        }

        void Add(int point, string key, int index)
        {
            if (!incident.TryGetValue((point, key), out var list))
                incident[(point, key)] = list = new List<int>();
            list.Add(index);
        }

        var used = new bool[segments.Count];
        var replaced = new List<int>();
        var additions = new List<(int A, int B, HashSet<uint> Owners)>();

        // Перебираются только ИСХОДНЫЕ звенья: список пополняется заменами,
        // и обход по растущему списку вышел бы за границы массива used.
        int original = segments.Count;

        // Звенья цепочек, уже разобранных без успеха. ИСПРАВЛЕНО: неудачная цепочка не
        // помечалась, и её собирали и пробовали заново от КАЖДОГО её звена — шов в 540
        // звеньев между процедурными поверхностями разбирался 540 раз (188 с на сборку тела).
        var tried = new bool[segments.Count];

        for (int i = 0; i < original; i++)
        {
            if (used[i] || tried[i]) continue;

            string key = OwnersKey(segments[i].Owners);

            // Цепочка растится в обе стороны от звена.
            var chain = new List<int> { i };
            int head = segments[i].A, tail = segments[i].B;

            Grow(ref head, chain, key, backwards: true);
            Grow(ref tail, chain, key, backwards: false);
            foreach (int k in chain) tried[k] = true;
            if (ChainDebug != null && chain.Count > 20)
                ChainDebug($"цепочка {chain.Count} звеньев, кольцо {head == tail}, конец в углу {corners.Contains(head)}/{corners.Contains(tail)}, развилка {(incident.TryGetValue((head, key), out var hh) ? hh.Count : 0)}/{(incident.TryGetValue((tail, key), out var tt) ? tt.Count : 0)}");

            if (chain.Count > longest) { longest = chain.Count; reason = "цепочка короче трёх звеньев"; }

            // Две хорды дугой не назовёшь — если окружность не известна заранее.
            if (chain.Count < 2) continue;
            if (chain.Count < 3 && KnownCircle(segments[chain[0]].Owners, weld.Points[head]) == null) continue;

            // ЗАМКНУТАЯ ОКРУЖНОСТЬ — ТРИ ДУГИ.
            //
            // Отверстие целиком внутри грани даёт цепочку, у которой начало
            // и конец — одна вершина. Одно ребро по полному кругу начиналось бы
            // и кончалось в одной точке, а выбор цепочек, поиск ребра и фаска
            // работают с двумя разными концами.
            //
            // Делится натрое, а не пополам: дуги хранятся по паре концевых
            // вершин, а у двух половин окружности концы одни и те же — вторая
            // половина затирала первую, и обе получали одну кривую.
            if (head == tail && chain.Count >= 9)
            {
                var order = ChainOrder(segments, chain, head);
                if (order != null)
                {
                    int m1 = chain.Count / 3, m2 = 2 * chain.Count / 3;
                    int v1 = order[m1], v2 = order[m2];

                    var parts = new[]
                    {
                        (From: head, To: v1, Links: chain.GetRange(0, m1)),
                        (From: v1, To: v2, Links: chain.GetRange(m1, m2 - m1)),
                        (From: v2, To: tail, Links: chain.GetRange(m2, chain.Count - m2)),
                    };

                    var fitted = new List<ICurve3>();
                    foreach (var part in parts)
                    {
                        var pp = ChainPoints(segments, part.Links, weld, part.From, part.To);
                        ICurve3? piece = pp == null ? null : FitArc(pp, out _, KnownCircle(segments[chain[0]].Owners, pp[0]));
                        if (piece == null) break;
                        fitted.Add(piece);
                    }

                    // ЗАМКНУТАЯ КРИВАЯ ПЕРЕСЕЧЕНИЯ — ТОЖЕ ТРИ РЕБРА (этап 2). Кольцо между
                    // гранями с точными поверхностями, не легшее на окружность, прежде
                    // становилось одним ребром из вершины в неё же: у такого ребра нулевая
                    // длина хорды, и скругление по нему откатывалось (прогон, 385:5).
                    if (fitted.Count < 3 && faces != null)
                    {
                        fitted.Clear();
                        foreach (var part in parts)
                        {
                            var pp = ChainPoints(segments, part.Links, weld, part.From, part.To);
                            ChainDebug?.Invoke($"  кольцо {chain.Count}: пробую часть {fitted.Count}");
                            ICurve3? piece = pp == null ? null : FitArc(pp) ?? IntersectionOf(segments[chain[0]].Owners, pp, faces);
                            ChainDebug?.Invoke($"  кольцо {chain.Count}: часть {fitted.Count}, точек {pp?.Count}, кривая {piece?.GetType().Name ?? "нет"}");
                            if (piece == null) break;
                            fitted.Add(piece);
                        }
                    }

                    if (fitted.Count == 3)
                    {
                        foreach (int k in chain) { used[k] = true; replaced.Add(k); }

                        for (int j = 0; j < 3; j++)
                        {
                            Put(parts[j].From, parts[j].To, segments[chain[0]].Owners, fitted[j]);
                            Put(parts[j].To, parts[j].From, segments[chain[0]].Owners, fitted[j]);
                            additions.Add((parts[j].From, parts[j].To, segments[chain[0]].Owners));
                        }

                        continue;
                    }
                }

                if (chain.Count >= longest) reason = "замкнутую окружность не удалось разделить на дуги";
                continue;
            }

            // Короткое кольцо (меньше девяти звеньев) не делится на три части и остаётся
            // хордами: одно ребро из вершины в неё же выбрать и скруглить нельзя.
            if (head == tail) continue;

            var points = ChainPoints(segments, chain, weld, head, tail);

            if (points == null)
            {
                if (chain.Count >= longest) reason = "звенья цепочки идут не подряд";
                continue;
            }

            ICurve3? arc = FitArc(points, out string why, KnownCircle(segments[chain[0]].Owners, points[0]));

            // КРИВАЯ ПЕРЕСЕЧЕНИЯ ДВУХ ИСХОДНЫХ ПОВЕРХНОСТЕЙ.
            //
            // ДОБАВЛЕНО. Цепочка хорд между двумя гранями, у которых есть исходные
            // поверхности (хотя бы одна кривая), и которая не складывается в дугу, —
            // одно ребро с процедурной кривой пересечения, а не полсотни отрезков. Кривая
            // принимается, только если каждая точка затравки сошлась на обе поверхности.
            // ПРЯМЫЕ И ДУГИ — РАНЬШЕ ОБЩЕЙ КРИВОЙ (этап 2).
            //
            // ДОБАВЛЕНО. Кромка протянутой фаски на плоской грани — «прямая — дуга — прямая»:
            // у грани фаски теперь есть точная поверхность, и вся цепочка становилась одной
            // кривой пересечения. Где пересечение распадается на прямые и дуги, они и
            // остаются: точнее и дешевле, и специальные построители фаски их берут.
            // Шов с краем процедурной поверхности (линия касания скругления) — изолиния целиком:
            // до разложения на прямые и кривые, которое на длинной цепочке дорого.
            // Только между двумя кривыми гранями: у шва с плоской гранью бывают прямые участки
            // (край фаски по «прямое — дуга — прямое»), их разбирает LinesAndCurves.
            if (arc == null && faces != null && SourcesOf(segments[chain[0]].Owners, faces) is { } isoSrc
                && !isoSrc.A.IsPlanar && !isoSrc.B.IsPlanar)
                arc = ArcIfRound(SectionIsoCurve.TryBuild(isoSrc.A, isoSrc.B, points));

            if (arc == null && faces != null)
            {
                var order = ChainOrder(segments, chain, head);
                var pieces = order == null ? null : LinesAndCurves(points, segments[chain[0]].Owners, faces);
                if (pieces != null)
                {
                    foreach (int k in chain) { used[k] = true; replaced.Add(k); }
                    var owners = segments[chain[0]].Owners;
                    foreach (var (from, to, curve) in pieces)
                    {
                        int a = order![from], b = order[to];
                        if (curve != null) { Put(a, b, owners, curve); Put(b, a, owners, curve); }
                        additions.Add((a, b, owners));
                    }
                    continue;
                }
                arc = IntersectionOf(segments[chain[0]].Owners, points, faces);
            }

            if (arc == null)
            {
                if (chain.Count >= longest) reason = why;
                continue;
            }

            foreach (int k in chain) { used[k] = true; replaced.Add(k); }

            Put(head, tail, segments[chain[0]].Owners, arc);
            Put(tail, head, segments[chain[0]].Owners, arc);

            // Цепочка заменяется одним звеном от начала к концу.
            additions.Add((head, tail, segments[chain[0]].Owners));
        }

        // Удаление идёт с конца: иначе индексы уехали бы.
        replaced.Sort();
        for (int k = replaced.Count - 1; k >= 0; k--) segments.RemoveAt(replaced[k]);

        segments.AddRange(additions);

        if (result.Count == 0 && longest > 1)
            warnings?.Add($"Boolean: no arcs recovered; longest chain of same-owner links " +
                          $"is {longest} — {reason}.");

        return result;

        void Grow(ref int end, List<int> chain, string key, bool backwards)
        {
            int guard = 0;

            while (guard++ < 4096)
            {
                // Настоящая вершина тела цепочку обрывает: через угол
                // дуга не продолжается.
                if (corners.Contains(end)) return;

                if (!incident.TryGetValue((end, key), out var here)) return;
                if (here.Count != 2) return;        // развилка или конец

                int next = here[0];
                if (chain.Contains(next)) next = here[1];
                if (chain.Contains(next)) return;

                if (used[next]) return;

                if (backwards) chain.Insert(0, next);
                else chain.Add(next);

                var seg = segments[next];
                end = seg.A == end ? seg.B : seg.A;
            }
        }
    }

    /// <summary>
    /// Цепочка точек как прямые и кривые: (номер первой точки, номер последней, кривая
    /// или null — прямая).
    ///
    /// ПРЯМАЯ — ЗВЕНО, ЛЕЖАЩЕЕ НА ОБЕИХ ТОЧНЫХ ПОВЕРХНОСТЯХ: его середина на каждой из них
    /// (хорда кривой отходит от поверхностей на стрелу прогиба и прямой не считается).
    /// Участки между прямыми — дуга, если точки ложатся на окружность, иначе кривая
    /// пересечения поверхностей; одно звено — остаётся отрезком.
    ///
    /// null — разложение не нужно или не удалось: прямых нет (пусть решает общая кривая),
    /// одни прямые, но не одна (излом без угла — пусть решает общая кривая), или участок
    /// не сошёлся. Вся цепочка одной прямой — один отрезок.
    /// </summary>
    private static List<(int From, int To, ICurve3? Curve)>? LinesAndCurves(
        List<Vector3d> pts, HashSet<uint> owners, Dictionary<uint, TFace> faces)
    {
        if (owners.Count != 2) return null;
        var src = new List<ISurface3>();
        foreach (uint tag in owners)
        {
            if (!faces.TryGetValue(tag, out var f)) return null;
            ISurface3? s = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            if (s == null || s is FacetedSurface) return null;
            src.Add(s);
        }

        const double onSurface = 2e-5;          // вершины точного ядра — на сетке 1e-5 (до 0.9e-5 от точки)
        // ПРЯМАЯ — НЕ КОРОЧЕ 0.05 ММ. Короткое звено лежит на обеих поверхностях при
        // любой кривизне (стрела прогиба L²/8R ничтожна), и звенья в микроны — стыки
        // вершины сетки с вычисленной точкой — признавались «прямыми» и рвали кривую
        // кромку на куски с рёбрами нулевой длины между ними (прогон, 385:5).
        const double minLine = 0.05;
        bool Straight(Vector3d a, Vector3d b)
        {
            if ((b - a).LengthSquared < minLine * minLine) return false;
            foreach (var q in new[] { (a + b) / 2, a + (b - a) * 0.25, a + (b - a) * 0.75 })
                foreach (var s in src)
                    if ((ExactSurfaces.Project(s, q).P - q).Length > onSurface) return false;
            return true;
        }

        // ПРЯМАЯ — НАИБОЛЬШИЙ ОТРЕЗОК ЦЕПОЧКИ, А НЕ ЗВЕНО. Точное ядро отдаёт прямой шов
        // несколькими звеньями, не строго коллинеарными (вершины на сетке), и прямая по
        // звеньям распадалась на короткие рёбра — скругление по ребру 0.63 мм давало
        // открытую оболочку (прогон, 425:3). Отрезок k..e — прямая, если он лежит на обеих
        // поверхностях и все промежуточные точки лежат на нём.
        int last = pts.Count - 1;
        bool OnSegment(int k, int e)
        {
            Vector3d a = pts[k], d = pts[e] - a;
            double l2 = d.LengthSquared;
            for (int i = k + 1; i < e; i++)
            {
                double t = Vector3d.Dot(pts[i] - a, d) / l2;
                if (t <= 0 || t >= 1 || (a + d * t - pts[i]).Length > onSurface) return false;
            }
            return true;
        }
        int LineFrom(int k)
        {
            int best = -1;
            for (int e = k + 1; e <= last; e++)
            {
                if (!OnSegment(k, e)) break;
                if (Straight(pts[k], pts[e])) best = e;
                else if (best >= 0) break;
            }
            return best;
        }

        var result = new List<(int, int, ICurve3?)>();
        bool anyLine = false, anyCurve = false;
        int k = 0, runStart = -1;
        bool FlushRun(int end)
        {
            if (runStart < 0) return true;
            int from = runStart; runStart = -1;
            if (end - from == 1) { result.Add((from, end, null)); return true; }
            var run = pts.GetRange(from, end - from + 1);

            // ПОДСКАЗКА ОКРУЖНОСТИ ЗДЕСЬ — ПРОБОВАЛ, ОТКАТИЛ. Замысел: взять окружность прямо с
            // цилиндра, раз обе поверхности под рукой. Расхождение «дуга против вершин» не
            // ушло, а охват сетки упал с 90 тел до 83: подбор по точкам здесь даёт дугу,
            // которая лучше ложится на уточнённую цепочку, чем точная окружность поверхности.

            // ПОДСКАЗКА ОКРУЖНОСТИ С ПОВЕРХНОСТИ — ПРОБОВАЛ ДВАЖДЫ, ЭФФЕКТА НЕТ. Замысел: брать
            // окружность прямо с цилиндра, а не подбирать по точкам. Измерено с меркой качества:
            // ни охват (90 тел из 100), ни расхождение (серединное 0.01286 мм) не меняются ни на
            // йоту — значит, дуги в этих телах рождаются не здесь.
            //
            // Причина расхождения установлена раньше и лежит глубже: вершина ребра — это конец
            // ХОРДЫ цепочки, а не точка окружности, и отстоит от неё на прогиб. Серединное
            // расхождение 0.0129 мм как раз отвечает прогибу резца (допуск 0.02). Значит,
            // лечится оно не подбором дуги, а мелкостью гранения резца — с той ценой по времени,
            // которая уже измерена у допуска.

            ICurve3? c = FitArc(run) ?? ArcIfRound(SectionIsoCurve.TryBuild(src[0], src[1], run));
            if (c == null && IntersectionCurve3.TryBuild(src[0], src[1], run) is { } ic)
            {
                // Уточнённые точки легли на окружность — ребро дуга (край фаски по скруглению
                // на плоской грани — точная окружность, а не кривая общего вида).
                var refined = new List<Vector3d>();
                ic.Tessellate(refined, true);
                c = FitArc(refined) ?? (ICurve3)ic;
            }
            if (c == null) return false;
            result.Add((from, end, c));
            anyCurve = true;
            return true;
        }
        while (k < last)
        {
            int e = LineFrom(k);
            if (e > k)
            {
                if (!FlushRun(k)) return null;
                result.Add((k, e, null)); anyLine = true;
                k = e;
                continue;
            }
            if (runStart < 0) runStart = k;
            k++;
        }
        if (!FlushRun(last)) return null;
        // Вся цепочка — одна прямая (шов стенки отверстия с плоской гранью): отрезок,
        // а не кривая пересечения общего вида.
        if (anyLine && !anyCurve && result.Count == 1) return result;
        if (!anyLine || !anyCurve) return null;
        return result;
    }

    /// <summary>
    /// Точки цепочки по порядку, от head к tail. null — цепочка рвётся.
    /// </summary>
    /// <summary>Номера точек цепочки по порядку, от head; null, если звенья идут не подряд.</summary>
    private static List<int>? ChainOrder(List<(int A, int B, HashSet<uint> Owners)> segments,
                                         List<int> chain, int head)
    {
        var order = new List<int> { head };
        int at = head;

        foreach (int index in chain)
        {
            var seg = segments[index];
            if (seg.A == at) at = seg.B;
            else if (seg.B == at) at = seg.A;
            else return null;
            order.Add(at);
        }

        return order;
    }

    private static List<Vector3d>? ChainPoints(
        List<(int A, int B, HashSet<uint> Owners)> segments,
        List<int> chain, VertexWeld weld, int head, int tail)
    {
        var order = new List<int> { head };
        int at = head;

        foreach (int index in chain)
        {
            var seg = segments[index];

            if (seg.A == at) at = seg.B;
            else if (seg.B == at) at = seg.A;
            else return null;                      // звенья идут не подряд

            order.Add(at);
        }

        if (at != tail) return null;

        var pts = new List<Vector3d>(order.Count);
        foreach (int i in order) pts.Add(weld.Points[i]);

        return pts;
    }

    /// <summary>
    /// Дуга через все точки либо null.
    ///
    /// Проверяется и плоскостность, и общий радиус, и монотонность обхода:
    /// точки, лежащие на окружности, но идущие туда-обратно, дугой
    /// не являются.
    /// </summary>
    private static ICurve3? FitArc(List<Vector3d> pts) => FitArc(pts, out _);

    private static ICurve3? FitArc(List<Vector3d> pts, out string why,
                                   (Vector3d Centre, Vector3d Axis, double Radius)? known = null)
    {
        why = "";

        if (pts.Count < (known != null ? 3 : 4)) { why = "звеньев меньше трёх"; return null; }

        // ЦЕНТР — ПО ВНУТРЕННИМ ТОЧКАМ ЦЕПОЧКИ.
        //
        // ИСПРАВЛЕНО. Центр строился по первой, средней и последней точкам.
        // Но конец дуги там, где она выходит на соседнюю грань, — пересечение
        // этой грани с ХОРДОЙ, а не с окружностью: он отстоит от окружности
        // на долю стрелы прогиба. Два таких конца уводили окружность, и честные
        // вершины хорд переставали в неё укладываться — край круглого выреза
        // оставался цепочкой прямых, и скругление по нему отказывало.
        // Внутренние вершины хорд лежат на окружности с точностью сетки.
        int count = pts.Count;
        Vector3d a = pts[0];
        Vector3d fa = count >= 7 ? pts[1] : pts[0];
        Vector3d b = pts[count / 2];
        Vector3d c = count >= 7 ? pts[count - 2] : pts[count - 1];

        Vector3d n, centre;
        double radius;

        if (known is { } kc)
        {
            // Окружность известна с исходной поверхности: точки лишь проверяются.
            n = kc.Axis;
            centre = kc.Centre;
            radius = kc.Radius;
        }
        else
        {
            Vector3d ab = b - fa, ac = c - fa;
            n = Vector3d.Cross(ab, ac);

            double span = Math.Max(ab.Length, ac.Length);
            if (span < WeldTol || n.Length < span * span * 1e-6)
            {
                why = "первая, средняя и последняя точки лежат на прямой";
                return null;
            }

            n = n.Normalized();

            // Центр: пересечение серединных перпендикуляров в плоскости точек.
            double ab2 = ab.LengthSquared, ac2 = ac.LengthSquared;
            Vector3d cross = Vector3d.Cross(ab, ac);
            double denom = 2 * cross.LengthSquared;

            if (denom < 1e-18) { why = "окружность по трём точкам не строится"; return null; }

            centre = fa + Vector3d.Cross(ac * ab2 - ab * ac2, cross) / denom;
            radius = (fa - centre).Length;

            if (radius < WeldTol) { why = "радиус вырожден"; return null; }
        }

        // Допуск связан с радиусом: у крупной дуги та же абсолютная
        // погрешность значит меньше.
        double slack = Math.Max(radius * 1e-4, WeldTol * 10);

        // Концам цепочки — ещё стрела прогиба самой длинной хорды: срезанная
        // гранью хорда кончается не на окружности, а на прямой между её точками.
        double longest = 0;
        for (int i = 1; i + 2 < count; i++) longest = Math.Max(longest, (pts[i + 1] - pts[i]).Length);
        double endSlack = slack + 1.5 * longest * longest / (8 * radius);

        // При известной окружности концам — полтора допуска хорды: разбиение
        // гарантирует, что стрела прогиба его не превышает, даже если у короткой
        // цепочки нет ни одной полной хорды, по которой её измерить.
        if (known != null) endSlack = Math.Max(endSlack, slack + 1.5 * Parametric3D.Core.Tol.Chord);

        var frame = new Frame3(centre, (a - centre).Normalized(),
                               Vector3d.Cross(n, (a - centre).Normalized()));

        double previous = 0;
        double total = 0;

        for (int i = 0; i < pts.Count; i++)
        {
            Vector3d d = pts[i] - centre;

            double allowed = (i == 0 || i == pts.Count - 1) ? endSlack : slack;

            if (Math.Abs(d.Length - radius) > allowed)
            {
                why = $"точка {i} отстоит от окружности на {Math.Abs(d.Length - radius):0.######} " +
                      $"при допуске {allowed:0.######}";
                return null;
            }

            if (Math.Abs(Vector3d.Dot(d, n)) > allowed)
            {
                why = $"точка {i} вне плоскости дуги на {Math.Abs(Vector3d.Dot(d, n)):0.######}";
                return null;
            }

            double angle = Math.Atan2(Vector3d.Dot(d, frame.V), Vector3d.Dot(d, frame.U));

            if (i == 0) { previous = angle; continue; }

            double step = angle - previous;
            while (step <= -Math.PI) step += 2 * Math.PI;
            while (step > Math.PI) step -= 2 * Math.PI;

            // Ход должен быть в одну сторону и мелкий: скачок через
            // половину оборота означает, что точки не по порядку.
            if (i > 1 && step * total < 0) { why = $"обход разворачивается на точке {i}"; return null; }

            if (Math.Abs(step) > Math.PI * 0.5)
            {
                why = $"шаг на точке {i} — {step * 180 / Math.PI:0.#}\u00b0, это не соседние точки дуги";
                return null;
            }

            total += step;
            previous = angle;
        }

        if (Math.Abs(total) < 1e-9) { why = "дуга нулевой длины"; return null; }

        // ПОЧТИ ПРЯМАЯ — НЕ ДУГА.
        //
        // ДОБАВЛЕНО по проверочному прогону (Parametric3D.Verify). Точные вершины
        // почти прямой цепочки не лежат строго на прямой, и подгонка находила
        // «дугу» радиусом в десятки километров с развёрткой 0°. Такое ребро шло
        // в скругление путём для дуг и сбивало выбор касательных цепочек.
        // Дуги из построений детали короче двух градусов не бывают: шаг
        // разбиения не больше 7.5°, а цепочка — не меньше двух звеньев.
        if (Math.Abs(total) < 2 * Math.PI / 180) { why = "цепочка почти прямая"; return null; }

        return new ArcCurve3(frame, radius, 0, total);
    }

    /// <summary>
    /// Насколько должны разойтись нормали смежных граней, чтобы стык
    /// считался видимым ребром, градусы.
    ///
    /// Гранёное скругление касается соседней грани не точно: последняя
    /// полоска отклоняется от неё на половину своего шага — около 2.8°
    /// при шестнадцати полосках на четверть окружности. Порог должен быть
    /// заметно больше, иначе тангенциальный стык всё равно рисуется.
    /// Настоящие рёбра деталей столь пологими почти не бывают.
    /// </summary>
    private const double SmoothJoinDegrees = 8.0;

    /// <summary>
    /// ТАНГЕНЦИАЛЬНЫЙ СТЫК ПРЯЧЕТСЯ.
    ///
    /// ДОБАВЛЕНО. Прежде Hidden после булевой не выставлялся НИКОГДА,
    /// и всякий стык граней рисовался чёрной линией — в том числе тот,
    /// где скругление плавно переходит в плоскость. На настоящей детали
    /// там нет ребра: поверхность продолжается без излома. Линия поперёк
    /// схода скругления сбивала с толку и выглядела как незаконченная
    /// геометрия.
    ///
    /// Из топологии ребро при этом никуда не девается: петли граней
    /// на него по-прежнему ссылаются, и тело остаётся замкнутым.
    /// Скрывается только отрисовка — и вместе с ней выбор мышью того,
    /// чего на детали нет.
    /// </summary>
    private static void MarkSmooth(TEdge edge)
    {
        TFace? a = edge.FaceA, b = edge.FaceB;
        if (a == null || b == null) return;

        // НОРМАЛИ — В ТОЧКАХ САМОЙ КРИВОЙ, А НЕ В СЕРЕДИНЕ ХОРДЫ. ИСПРАВЛЕНО: у кольцевого
        // ребра (шов скругления по краю кармана) середина хорды отстоит от кромки на
        // десятки миллиметров, нормали граней там случайны, и касательный шов (0°) не
        // признавался гладким. Гладкий — если во всех трёх точках угол меньше порога.
        double limit = Math.Cos(SmoothJoinDegrees * Math.PI / 180.0);

        // У ПЛОСКИХ ГРАНЕЙ НОРМАЛЬ ПОСТОЯННА — проекция не нужна. Приведение точки к параметрам
        // у протянутой поверхности идёт итерациями и стоит около 0.33 мс: на 272 рёбрах вся
        // проверка гладкости занимала 539 мс.
        static Vector3d? FlatNormal(TFace f)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            if (src is not PlaneSurface pl) return null;
            Vector3d n = pl.F.W;
            return f.Reversed ? -n : n;
        }

        if (FlatNormal(a) is { } fa && FlatNormal(b) is { } fb)
        {
            edge.Hidden = Vector3d.Dot(fa.Normalized(), fb.Normalized()) > limit;
            return;
        }

        bool smooth = true;
        // Точки берутся из уже посчитанного гранения ребра: у процедурной кривой (изолиния,
        // кривая пересечения) каждая точка считается итерациями, и повторный счёт стоил
        // 541 мс на 272 ребра — больше, чем вся остальная сборка рёбер.
        var poly = Tess.ExactFaceMesher.EdgePoints(edge);
        foreach (double t in new[] { 0.25, 0.5, 0.75 })
        {
            Vector3d q = poly.Count >= 2
                ? poly[Math.Clamp((int)Math.Round(t * (poly.Count - 1)), 0, poly.Count - 1)]
                : edge.Curve.PointAt(edge.Curve.T0 + (edge.Curve.T1 - edge.Curve.T0) * t);
            Vector3d na = a.NormalAt(q), nb = b.NormalAt(q);
            if (na.LengthSquared < 1e-18 || nb.LengthSquared < 1e-18) return;
            if (Vector3d.Dot(na.Normalized(), nb.Normalized()) <= limit) { smooth = false; break; }
        }
        edge.Hidden = smooth;
    }

    /// <summary>
    /// Сливает соседние коллинеарные куски одного ребра.
    ///
    /// Условия все три обязательны: у кусков совпадает набор граней,
    /// в точке стыка сходятся ровно два куска, и точка не является
    /// вершиной тела. Ослабь любое — и слияние перескочит через угол.
    /// </summary>
    private static void MergeCollinear(List<(int A, int B, HashSet<uint> Owners)> segments,
                                       VertexWeld weld,
                                       HashSet<int> corners)
    {
        // ЗА ОДИН ПРОХОД, А НЕ ПО ПАРЕ ЗА ПРОХОД.
        //
        // ИСПРАВЛЕНО. Прежде сливалась одна пара, после чего вся таблица
        // инцидентности строилась заново — со строковым ключом на каждый
        // отрезок. На теле в полторы тысячи треугольников это тысячи проходов
        // по тысячам отрезков: на серии из 64 отверстий 6 из 10 секунд уходили
        // сюда.
        //
        // Суть та же. Точка ВНУТРЕННЯЯ, если она не вершина тела, в неё входят
        // ровно два отрезка одной пары граней и они лежат на одной прямой.
        // Цепочка отрезков через внутренние точки сворачивается в один отрезок
        // от конца до конца. Концы цепочки — не внутренние точки, поэтому
        // обход всегда начинается и кончается.
        int n = segments.Count;
        var keys = new string[n];
        var incident = new Dictionary<(int Point, string Key), List<int>>();

        for (int i = 0; i < n; i++)
        {
            keys[i] = OwnersKey(segments[i].Owners);
            Add(segments[i].A, keys[i], i);
            Add(segments[i].B, keys[i], i);
        }

        void Add(int point, string key, int index)
        {
            if (!incident.TryGetValue((point, key), out var list))
                incident[(point, key)] = list = new List<int>(2);
            list.Add(index);
        }

        var inner = new HashSet<(int, string)>();

        foreach (var ((point, key), list) in incident)
        {
            if (list.Count != 2 || corners.Contains(point)) continue;

            var si = segments[list[0]];
            var sj = segments[list[1]];
            int e1 = si.A == point ? si.B : si.A;
            int e2 = sj.A == point ? sj.B : sj.A;

            if (e1 == e2) continue;
            if (!Collinear(weld.Points[e1], weld.Points[point], weld.Points[e2])) continue;

            inner.Add((point, key));
        }

        if (inner.Count == 0) return;

        var used = new bool[n];
        var result = new List<(int A, int B, HashSet<uint> Owners)>(n);

        for (int i = 0; i < n; i++)
        {
            if (used[i]) continue;

            var seg = segments[i];
            string key = keys[i];
            bool innerA = inner.Contains((seg.A, key));
            bool innerB = inner.Contains((seg.B, key));

            if (innerA && innerB) continue;          // середина цепочки: её подберёт обход от конца

            int start = innerA ? seg.B : seg.A;
            int cur = innerA ? seg.A : seg.B;
            int at = i;
            used[i] = true;

            while (inner.Contains((cur, key)))
            {
                var pair = incident[(cur, key)];
                int next = pair[0] == at ? pair[1] : pair[0];
                if (used[next]) break;

                used[next] = true;
                var ns = segments[next];
                cur = ns.A == cur ? ns.B : ns.A;
                at = next;
            }

            result.Add((start, cur, seg.Owners));
        }

        // Отрезки, не попавшие ни в одну цепочку от конца, остаются как были.
        for (int i = 0; i < n; i++)
            if (!used[i]) result.Add(segments[i]);

        segments.Clear();
        segments.AddRange(result);
    }


    private static string OwnersKey(HashSet<uint> owners)
    {
        var ids = new List<uint>(owners);
        ids.Sort();
        return string.Join(",", ids);
    }

    private static bool Collinear(Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d u = b - a, v = c - b;
        double lu = u.LengthSquared, lv = v.LengthSquared;
        if (lu < 1e-18 || lv < 1e-18) return true;

        return Vector3d.Cross(u, v).LengthSquared <= 1e-14 * lu * lv;
    }

    /// <summary>
    /// Склейка совпадающих вершин.
    ///
    /// Поиск идёт по 27 СОСЕДНИМ клеткам решётки, а не по одной.
    /// Простое округление координат в номер клетки ненадёжно: две точки,
    /// отличающиеся на 1e-9, но лежащие по разные стороны границы клетки,
    /// попадают в разные клетки и не склеиваются. После этого стыки граней
    /// не находятся, общих рёбер не остаётся, и каркас распадается
    /// на обрывки.
    /// </summary>
    private sealed class VertexWeld
    {
        public readonly List<Vector3d> Points = new();

        private readonly Dictionary<(long, long, long), List<int>> _cells = new();
        private readonly double _tol;

        public VertexWeld(double tol) => _tol = tol;

        // Точная склейка: только совпадающие до бита точки.
        private readonly Dictionary<(double, double, double), int>? _exact;

        /// <summary>
        /// Склейка без допуска — для входа из точного ядра.
        ///
        /// ИСПРАВЛЕНО. Там вершины канонические: одна и та же вершина всегда
        /// переводится в одни и те же числа double, а разные вершины бывают
        /// ближе допуска склейки 1e-6 мм — у пересечений наклонных плоскостей
        /// до 1e-10. Склейка по допуску сливала их, треугольники вырождались,
        /// и экранная сетка получала открытые рёбра при замкнутом теле.
        /// </summary>
        public static VertexWeld Exact() => new(0, exact: true);

        private VertexWeld(double tol, bool exact)
        {
            _tol = tol;
            if (exact) _exact = new Dictionary<(double, double, double), int>();
        }

        public int Add(Vector3d p)
        {
            if (_exact != null)
            {
                var k = (p.X, p.Y, p.Z);
                if (_exact.TryGetValue(k, out int at)) return at;
                at = Points.Count;
                Points.Add(p);
                _exact[k] = at;
                return at;
            }

            (long x, long y, long z) key = (Cell(p.X), Cell(p.Y), Cell(p.Z));

            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!_cells.TryGetValue((key.x + dx, key.y + dy, key.z + dz), out var bucket))
                            continue;

                        foreach (int i in bucket)
                            if ((Points[i] - p).LengthSquared <= _tol * _tol) return i;
                    }

            int index = Points.Count;
            Points.Add(p);

            if (!_cells.TryGetValue(key, out var own)) _cells[key] = own = new List<int>();
            own.Add(index);

            return index;
        }

        private long Cell(double v) => (long)Math.Round(v / _tol);
    }
}
