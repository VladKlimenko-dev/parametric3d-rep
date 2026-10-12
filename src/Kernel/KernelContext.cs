namespace Parametric3D.Kernel;

/// <summary>
/// K04. НАСТРОЙКИ ЯДРА, ВЛИЯЮЩИЕ НА РЕЗУЛЬТАТ И НА РАСКЛАДКУ РАБОТЫ, — ОДНИМ НЕИЗМЕНЯЕМЫМ ЗНАЧЕНИЕМ.
///
/// Прежде каждая настройка была общим изменяемым полем своего класса, и два расчёта, идущих
/// одновременно, меняли их друг другу посреди работы (KernelOptions.Scope лишь возвращал прежние
/// значения по выходе). Теперь расчёт получает снимок настроек в своём контексте; общие поля
/// остаются только значениями по умолчанию для кода, работающего вне контекста.
/// </summary>
public sealed record KernelSettings
{
    // точность гранения
    public double Chord { get; init; }
    public double ChordAngle { get; init; }
    // маршруты булевой
    public bool UseExactKernel { get; init; }
    public bool LocalizeBooleans { get; init; }
    public bool ExactCylinderCuts { get; init; }
    public bool SnapVertices { get; init; }
    public bool HealAfterBoolean { get; init; }
    public double HealTolerance { get; init; }
    public bool HealTinyEdges { get; init; }
    public bool Audit { get; init; }
    public int AuditDepth { get; init; }
    public bool VerifyEveryAnswer { get; init; }
    public bool ExactSectionsByFacets { get; init; }
    // сетка
    public bool ExactMeshing { get; init; }
    public bool DeferMeshing { get; init; }
    public bool ExactDisplay { get; init; }
    public bool QMeshEnabled { get; init; }
    public bool FaceCache { get; init; }
    public bool CdtCheckSides { get; init; }
    public bool CdtOldLegalize { get; init; }
    // поставка 2: прежде общие изменяемые поля, обходившие контекст
    public bool ExactEdgesEnabled { get; init; }
    public bool ExactEdgesSnapVertices { get; init; }
    public bool ExactEdgesReplaceCurves { get; init; }
    public bool SplitRings { get; init; }
    public double ToleranceFloor { get; init; }
    public double VertexBallMargin { get; init; }
    public bool ExactEdgesOnExport { get; init; }
    public bool UseUvMesher { get; init; }
    public bool DeclinePoles { get; init; }
    public bool UvAllowBands { get; init; }
    public bool PlanarInput { get; init; }
    public bool OwnPlanesInGeneralPath { get; init; }
    public Topology.ValidationSeverity TJunctionSeverity { get; init; }
    public bool ReuseFaceMeshes { get; init; }
    public bool SplitCurvedRegions { get; init; }
    public bool AnalyticPrism { get; init; }
    public bool AnalyticGeneral { get; init; }
    /// <summary>KRN-03: сферы в маршруте общей булевой (по умолчанию — из P3D_ANALYTICSPHERES).</summary>
    public bool AnalyticSpheres { get; init; }
    /// <summary>KRN-01: политика геометрии (сохранять B-rep или разрешить ограниченное гранение).</summary>
    public GeometryPolicy GeometryPolicy { get; init; }
    // раскладка и пометки
    public int BooleanThreads { get; init; }
    public int MeshThreads { get; init; }
    public string? OpTag { get; init; }
    /// <summary>KRN-02: бюджет точности (утверждённая числовая область).</summary>
    public ToleranceContext? Tolerance { get; init; } = ToleranceContext.Default;

    /// <summary>Действующие настройки: контекста, если он задан, иначе общие по умолчанию.</summary>
    public static KernelSettings Capture() => KernelContext.Current?.Settings ?? new KernelSettings
    {
        Chord = Core.Tol.Chord,
        ChordAngle = Core.Tol.ChordAngle,
        UseExactKernel = Ops.BooleanOp.UseExactKernel,
        LocalizeBooleans = Ops.BooleanOp.LocalizeBooleans,
        ExactCylinderCuts = Ops.BooleanOp.ExactCylinderCuts,
        SnapVertices = Ops.BooleanOp.SnapVertices,
        HealAfterBoolean = Ops.BooleanOp.HealAfterBoolean,
        HealTolerance = Ops.BooleanOp.HealTolerance,
        HealTinyEdges = Ops.BooleanOp.HealTinyEdges,
        Audit = Ops.BooleanOp.Audit,
        AuditDepth = Ops.BooleanOp.AuditDepth,
        VerifyEveryAnswer = Exact.PlaneRegistry.VerifyEveryAnswer,
        ExactSectionsByFacets = Ops.FilletOp.ExactSectionsByFacets,
        ExactMeshing = Ops.BooleanOp.ExactMeshing,
        DeferMeshing = Ops.BooleanOp.DeferMeshing,
        ExactDisplay = Ops.BooleanOp.ExactDisplay,
        QMeshEnabled = Tess.QMesher.Enabled,
        FaceCache = Tess.ExactFaceMesher.Remember,
        CdtCheckSides = Tess.Cdt.CheckSides,
        CdtOldLegalize = Tess.Cdt.OldLegalize,
        ExactEdgesEnabled = Geometry.ExactEdges.Enabled,
        ExactEdgesSnapVertices = Geometry.ExactEdges.SnapVertices,
        ExactEdgesReplaceCurves = Geometry.ExactEdges.ReplaceCurves,
        SplitRings = Topology.TopologyConventions.SplitRings,
        ToleranceFloor = Topology.Tolerances.Floor,
        VertexBallMargin = Ops.VertexOp.BallMargin,
        ExactEdgesOnExport = Export.StepExact.ExactEdgesOnExport,
        UseUvMesher = Tess.ExactFaceMesher.UseUvMesher,
        DeclinePoles = Tess.ExactFaceMesher.DeclinePoles,
        UvAllowBands = Tess.UvMesher.AllowBands,
        PlanarInput = Exact.ExactPlanarInput.Enabled,
        OwnPlanesInGeneralPath = Exact.ExactContactInput.OwnPlanesInGeneralPath,
        TJunctionSeverity = Topology.SolidValidator.TJunctionSeverity,
        ReuseFaceMeshes = Ops.BooleanOp.ReuseFaceMeshes,
        SplitCurvedRegions = Topology.TopologyConventions.SplitCurvedRegions,
        AnalyticPrism = Ops.BooleanOp.AnalyticPrism,
        AnalyticGeneral = Ops.BooleanOp.AnalyticGeneral,
        AnalyticSpheres = Ops.BooleanOp.AnalyticSpheresDefault,
        GeometryPolicy = Ops.BooleanOp.GeometryPolicy,
        BooleanThreads = Ops.BooleanOp.Threads,
        MeshThreads = Tess.Tessellator.Threads,
        OpTag = Ops.BooleanOp.OpTag,
        Tolerance = ToleranceContext.Default,
    };
}

/// <summary>Пределы ресурсов одного вычисления. Ноль — без предела.</summary>
public sealed record KernelLimits
{
    /// <summary>Наибольшее число многоугольников точного тела на входе или выходе булевой.</summary>
    public int MaxExactPolygons { get; init; }
    /// <summary>Крайний срок вычисления (UTC); превышение — отмена с причиной «время вышло».</summary>
    public DateTime? Deadline { get; init; }

    public static readonly KernelLimits None = new();
}

/// <summary>Вычисление остановлено пределом ресурсов (не отменой пользователя и не ошибкой геометрии).</summary>
public sealed class KernelLimitException(string what) : OperationCanceledException($"kernel limit exceeded: {what}")
{
    public string What { get; } = what;
}

/// <summary>
/// K04. КОНТЕКСТ ВЫЧИСЛЕНИЯ: неизменяемые настройки, политика операций, пределы ресурсов и
/// токен отмены. Принадлежит одному логическому потоку вычисления (AsyncLocal): параллельный
/// расчёт другой детали видит свой контекст, а задачи Parallel.For внутри расчёта — тот же.
///
/// Вход: <c>using var _ = context.Enter();</c>. Внутри контекста общие поля настроек ядра
/// менять нельзя — попытка бросает исключение: правка общего значения не подействовала бы на
/// идущий расчёт и молча разошлась бы с ожиданием вызывающего. Для изменения на время работы
/// служат <see cref="With"/> и <see cref="KernelOptions.Apply"/>.
/// </summary>
public sealed class KernelContext
{
    private static readonly AsyncLocal<KernelContext?> _current = new();

    public static KernelContext? Current => _current.Value;

    public KernelSettings Settings { get; }
    public OperationPolicy Policy { get; }
    public KernelLimits Limits { get; }
    public CancellationToken Cancellation { get; }

    /// <summary>Диагностика: вызывается на каждой границе этапа до проверки отмены (проверки, профили).</summary>
    public Action<string>? StageHook { get; init; }

    /// <summary>
    /// РАСШИРЕНИЯ КОНТЕКСТА — неизменяемые снимки настроек верхних слоёв (модель: ModelSettings), по типу.
    /// Ядро не знает этих типов: так снимок модели едет вместе с расчётом, не заводя ссылок ядра на модель.
    /// </summary>
    private System.Collections.Immutable.ImmutableDictionary<Type, object> _ext =
        System.Collections.Immutable.ImmutableDictionary<Type, object>.Empty;

    /// <summary>Снимок слоя типа T, если он положен в контекст.</summary>
    public T? Extension<T>() where T : class => _ext.TryGetValue(typeof(T), out var v) ? (T)v : null;

    /// <summary>Контекст с положенным (заменённым) снимком слоя; остальное наследуется.</summary>
    public KernelContext WithExtension<T>(T value) where T : class =>
        Copy(Settings, Policy, Limits, Cancellation, StageHook, _ext.SetItem(typeof(T), value));

    private static KernelContext Copy(KernelSettings s, OperationPolicy p, KernelLimits l, CancellationToken c,
                                      Action<string>? hook, System.Collections.Immutable.ImmutableDictionary<Type, object> ext) =>
        new(s, p, l, c) { StageHook = hook, _ext = ext };

    /// <summary>Расширения другого контекста (наследование слоёв при вложенном расчёте).</summary>
    public KernelContext WithExtensionsOf(KernelContext? other) =>
        other == null ? this : Copy(Settings, Policy, Limits, Cancellation, StageHook, other._ext.SetItems(_ext));

    public KernelContext(KernelSettings? settings = null, OperationPolicy policy = OperationPolicy.Strict,
                         KernelLimits? limits = null, CancellationToken cancellation = default)
    {
        Settings = settings ?? KernelSettings.Capture();
        Policy = policy;
        Limits = limits ?? KernelLimits.None;
        Cancellation = cancellation;
    }

    /// <summary>Контекст с изменёнными настройками; остальное наследуется.</summary>
    public KernelContext With(Func<KernelSettings, KernelSettings> change) =>
        Copy(change(Settings), Policy, Limits, Cancellation, StageHook, _ext);

    public KernelContext WithPolicy(OperationPolicy policy) =>
        Copy(Settings, policy, Limits, Cancellation, StageHook, _ext);

    public KernelContext WithCancellation(CancellationToken token) =>
        Copy(Settings, Policy, Limits, token, StageHook, _ext);

    public KernelContext WithStageHook(Action<string>? hook) =>
        Copy(Settings, Policy, Limits, Cancellation, hook, _ext);

    /// <summary>Сделать контекст действующим до освобождения возвращённого значения.</summary>
    public Scope Enter() => new(this);

    public readonly struct Scope : IDisposable
    {
        private readonly KernelContext? _prev;
        private readonly KernelContext _mine;
        internal Scope(KernelContext ctx)
        {
            _prev = _current.Value; _mine = ctx; _current.Value = ctx;
            if (ctx.Settings.VerifyEveryAnswer) Interlocked.Increment(ref _verifying);
        }
        public void Dispose()
        {
            if (_mine == null) return;
            _current.Value = _prev;
            if (_mine.Settings.VerifyEveryAnswer) Interlocked.Decrement(ref _verifying);
        }
    }

    // СКОРОСТЬ ПРОВЕРОЧНЫХ ПРИЗНАКОВ. VerifyEveryAnswer спрашивается в точных предикатах миллионы
    // раз за булеву; чтение AsyncLocal на каждый вопрос стоило +7% времени сферического выреза.
    // Пока ни один действующий контекст не включил проверку (обычная работа), ответ «нет»
    // получается без чтения контекста.
    private static int _verifying;
    internal static bool AnyVerifying => Volatile.Read(ref _verifying) != 0;

    /// <summary>
    /// Граница этапа: проверка отмены и крайнего срока. Дёшево (одно чтение AsyncLocal) и
    /// вызывается только на границах этапов, не в горячих циклах.
    /// </summary>
    public static void Checkpoint(string stage)
    {
        var c = _current.Value;
        if (c == null) return;
        c.StageHook?.Invoke(stage);
        c.Cancellation.ThrowIfCancellationRequested();
        if (c.Limits.Deadline is { } d && DateTime.UtcNow > d) throw new KernelLimitException($"deadline at {stage}");
    }

    /// <summary>Проверка размера точного тела против предела контекста.</summary>
    public static void CheckPolygons(int count, string stage)
    {
        var c = _current.Value;
        if (c == null || c.Limits.MaxExactPolygons <= 0) return;
        if (count > c.Limits.MaxExactPolygons)
            throw new KernelLimitException($"{count} exact polygons at {stage} (limit {c.Limits.MaxExactPolygons})");
    }

    /// <summary>Запись общего значения настройки; внутри контекста запрещена.</summary>
    public static void SetGlobal<T>(ref T field, T value, string name)
    {
        if (_current.Value != null)
            throw new InvalidOperationException(
                $"{name} is a global default and cannot be changed inside a kernel context; use KernelContext.With or KernelOptions.Apply.");
        field = value;
    }

    /// <summary>Отмена или предел — не отказ геометрии: её нельзя превращать в результат операции.</summary>
    public static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException ||
        ex is AggregateException a && a.Flatten().InnerExceptions.Count > 0 &&
        a.Flatten().InnerExceptions.All(i => i is OperationCanceledException);

    /// <summary>Отмена, пришедшая из Parallel.For обёрнутой, — развернуть в исходное исключение.</summary>
    public static Exception Unwrap(Exception ex) =>
        ex is AggregateException a && IsCancellation(a) ? a.Flatten().InnerExceptions[0] : ex;
}
