using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel;

/// <summary>Итог операции ядра.</summary>
public enum OperationStatus
{
    /// <summary>Результат получен заявленным маршрутом с его гарантиями.</summary>
    Succeeded,
    /// <summary>Результат получен приближённым маршрутом; допустимо только при явной политике.</summary>
    SucceededApproximate,
    /// <summary>Результата нет; исходное состояние вызывающего не изменено.</summary>
    Failed
}

/// <summary>
/// ПОЛИТИКА ОПЕРАЦИИ. Строгая — инженерный режим по умолчанию: подмена точного маршрута
/// приближённым считается отказом. Приближённая выбирается ЯВНО и даёт результат с честным
/// статусом и пометкой на теле.
/// </summary>
public enum OperationPolicy { Strict, AllowApproximate }

/// <summary>
/// KRN-01: политика ГЕОМЕТРИИ (отдельно от политики арифметики OperationPolicy). Strict запрещает приближённую арифметику,
/// но разрешает точный многогранный путь — он строит форму по фасетам (цилиндр — гранёный), и её точность зависит от
/// допуска хорды. PreserveBRep требует результат из точных поверхностей: многогранный и приближённый пути — отказ
/// (Unsupported, код geometry-downgrade-refused). AllowBoundedFaceting — прежнее поведение; понижение аналитического
/// операнда до фасет записывается в диагностику операции (код geometry-downgrade).
/// </summary>
public enum GeometryPolicy { AllowBoundedFaceting, PreserveBRep }

/// <summary>
/// KRN-01: типизированный исход операции. Пустой результат — штатный исход (Empty), а не отказ алгоритма; неподдержанный
/// случай (Unsupported) отличается от некорректного входа (InvalidInput) и внутреннего сбоя (InternalFailure).
/// Status сохраняется для совместимости; Outcome — то, на что опирается вызывающий.
/// </summary>
public enum OperationOutcome
{
    /// <summary>Результат получен, тела есть.</summary>
    Success,
    /// <summary>Результат получен и пуст (пересечение непересекающихся, вычитание охватывающим телом).</summary>
    Empty,
    /// <summary>Случай вне поддержанной области при данной политике (точный путь не справился, приближение запрещено).</summary>
    Unsupported,
    /// <summary>Некорректный или неполный вход (приближённый операнд при строгой политике, пропавшая грань).</summary>
    InvalidInput,
    /// <summary>Точность построения вышла за бюджет (KRN-02).</summary>
    AccuracyExceeded,
    /// <summary>Операция отменена.</summary>
    Cancelled,
    /// <summary>Внутренний сбой: исключение, незамкнутый или негодный результат.</summary>
    InternalFailure,
    /// <summary>KRN-08: ссылка на топологию указывает на несколько равноправных кандидатов — нужен выбор пользователя
    /// (кандидаты — в исключении AmbiguousReferenceException); похожая грань не подставляется.</summary>
    Ambiguous,
}

/// <summary>Уровень гарантий тела, которое вернула операция.</summary>
public enum GuaranteeLevel
{
    /// <summary>Не установлен: тело не проходило через операцию с контрактом (примитив, импорт).</summary>
    Unspecified,
    /// <summary>Рациональный полиэдральный Boolean (ExactSolid) после входного округления.</summary>
    ExactPolyhedral,
    /// <summary>Точный путь по квадрике (QuadricCut).</summary>
    ExactQuadric,
    /// <summary>Аналитический путь: тело из точных поверхностей (плоскости, цилиндры), без гранения (Kernel/Analytic).</summary>
    ExactAnalytic,
    /// <summary>Приближённый путь (CSG по гранению в double) или приближённый вход.</summary>
    Approximate
}

/// <summary>Одно сообщение о ходе операции: этап, код, текст и, если известно, элемент.</summary>
public sealed record OperationDiagnostic(string Stage, string Code, string Message, uint? Element = null, IReadOnlyList<string>? Entities = null)
{
    public override string ToString() => $"[{Stage}] {Code}: {Message}" + (Element is { } e ? $" (элемент {e})" : "")
                                         + (Entities is { Count: > 0 } es ? " — " + string.Join("; ", es.Take(4)) + (es.Count > 4 ? $" (+{es.Count - 4})" : "") : "");
}

/// <summary>Коды отказа и событий, на которые опирается политика.</summary>
public static class OperationCodes
{
    public const string FaceMissing = "face-missing";
    /// <summary>KRN-01 (g.10): грань входа не разбилась из-за ИСКЛЮЧЕНИЯ (сбой расчёта, а не неполный вход) — InternalFailure,
    /// результат с ним не окончателен (следующий расчёт пробует снова). Прежде — face-missing → InvalidInput.</summary>
    public const string FaceTessellationFailed = "face-tessellation-failed";
    public const string ExactFailed = "exact-failed";
    public const string ExactDeclined = "exact-declined";
    public const string Fallback = "fallback-approximate";
    public const string FallbackRefused = "fallback-refused";
    public const string InputApproximate = "input-approximate";
    public const string InvalidResult = "invalid-result";
    public const string Exception = "exception";
    public const string OpenResult = "open-result";
    /// <summary>KRN-01: политика PreserveBRep запретила понижение до фасет (результат был бы многогранным).</summary>
    public const string GeometryDowngradeRefused = "geometry-downgrade-refused";
    /// <summary>KRN-01: аналитический операнд обработан через фасетную производную форму (AllowBoundedFaceting).</summary>
    public const string GeometryDowngrade = "geometry-downgrade";
    /// <summary>KRN-02: допуск сущности точного результата больше предела бюджета (MaxEntityTolerance).</summary>
    public const string AccuracyExceeded = "accuracy-exceeded";
}

/// <summary>
/// KRN-02: ОТЧЁТ О ТОЧНОСТИ РЕЗУЛЬТАТА. EntityTolerance — наибольший хранимый допуск ребра/вершины новых тел; Measured — измеренное
/// отклонение кривых и вершин от поверхностей их граней (для точных тел, по замкнутым формулам); Budget — предел, против
/// которого результат принят; Basis — что ограничивает точность (бюджет точного тела или гранение).
/// </summary>
public sealed record AccuracyReport(double EntityTolerance, double? Measured, double Budget, string Basis)
{
    public override string ToString() =>
        $"entity tolerance {EntityTolerance:E2} mm" + (Measured is { } m ? $", measured {m:E2} mm" : "") + $", budget {Budget:E2} mm ({Basis})";
}

/// <summary>
/// K02. ТИПИЗИРОВАННЫЙ РЕЗУЛЬТАТ ОПЕРАЦИИ.
///
/// Прежде итог передавался строкой в списке предупреждений: точный путь при исключении писал
/// «использован приближённый», гранение при ошибке — «грань пропущена», и тело шло дальше как
/// обычное. Ни интерфейс, ни массовые свойства, ни следующая операция отличить такое тело не
/// могли. Теперь операция возвращает статус, маршрут, уровень гарантий, признак подмены
/// маршрута и диагностику по этапам; отказ не меняет состояние вызывающего.
/// </summary>
public sealed class OperationResult
{
    public string Operation { get; init; } = "";
    public OperationPolicy Policy { get; init; }
    public OperationStatus Status { get; internal set; }
    public GuaranteeLevel Guarantee { get; internal set; }
    /// <summary>Использован ли маршрут хуже заявленного (подмена точного приближённым).</summary>
    public bool FallbackUsed { get; internal set; }
    /// <summary>Тела результата: при отказе пусто, состояние вызывающего не изменено.</summary>
    public IReadOnlyList<TSolid> Bodies { get; internal set; } = Array.Empty<TSolid>();
    public List<OperationDiagnostic> Diagnostics { get; } = new();
    /// <summary>Этап, на котором операция отказала.</summary>
    public string? FailedStage { get; internal set; }
    public string? FailureCode { get; internal set; }

    public bool Accepted => Status != OperationStatus.Failed;
    /// <summary>KRN-01: типизированный исход (см. OperationOutcome).</summary>
    public OperationOutcome Outcome { get; internal set; }
    /// <summary>KRN-08: исключение, которым операция отказала (неоднозначная ссылка несёт кандидатов для выбора).</summary>
    public Exception? Cause { get; internal set; }
    /// <summary>KRN-01: запрошенная политика геометрии; достигнутый вид геометрии — Guarantee.</summary>
    public GeometryPolicy RequestedGeometryPolicy { get; internal set; }
    /// <summary>KRN-01: входные тела — (идентификатор, ревизия) на момент операции.</summary>
    public IReadOnlyList<(uint Id, long Revision)> InputRevisions { get; internal set; } = Array.Empty<(uint, long)>();
    /// <summary>KRN-01: эффективные настройки ядра, при которых выполнена операция.</summary>
    public KernelSettings? EffectiveSettings { get; internal set; }
    /// <summary>KRN-02: достигнутая точность новых тел результата (null — тел нет или операция отказала).</summary>
    public AccuracyReport? Accuracy { get; internal set; }
    /// <summary>KRN-01 (01.5): карта происхождения граней результата — выход операции (если построитель её записал; иначе null).
    /// Годна для ревизии тела, при которой записана (см. TSolid.Provenance).</summary>
    public Topology.Provenance? ProvenanceMap { get; internal set; }

    /// <summary>Исход отказа по его коду.</summary>
    public static OperationOutcome OutcomeOf(string? failureCode) => failureCode switch
    {
        OperationCodes.InputApproximate or OperationCodes.FaceMissing => OperationOutcome.InvalidInput,
        OperationCodes.FallbackRefused or OperationCodes.ExactDeclined or OperationCodes.GeometryDowngradeRefused => OperationOutcome.Unsupported,
        OperationCodes.AccuracyExceeded => OperationOutcome.AccuracyExceeded,
        _ => OperationOutcome.InternalFailure,
    };

    public string Describe() =>
        $"{Operation}: {Status}" + (Status == OperationStatus.Failed ? $" at {FailedStage} ({FailureCode})" : $", {Guarantee}") +
        (FallbackUsed ? ", fallback" : "") +
        (Diagnostics.Count > 0 ? "; " + string.Join("; ", Diagnostics.Take(4)) : "");
}

/// <summary>Отказ операции, переданный исключением в прежние void-вызовы (их ловит дерево построения).</summary>
public sealed class OperationFailedException : Core.RebuildException
{
    public OperationResult Result { get; }
    public OperationFailedException(OperationResult result) : base(result.Outcome, Compose(result)) => Result = result;

    private static string Compose(OperationResult r)
    {
        var first = r.Diagnostics.FirstOrDefault(d => d.Code == r.FailureCode) ?? r.Diagnostics.FirstOrDefault();
        return $"{r.Operation} refused ({r.FailureCode} at {r.FailedStage})" + (first != null ? $": {first.Message}" : "");
    }
}

/// <summary>
/// ПОЛИТИКА НА ВРЕМЯ РАСЧЁТА. Хранится в AsyncLocal: значение принадлежит логическому потоку
/// вызова и не протекает в параллельный расчёт другой детали, в отличие от статических
/// переключателей KernelOptions. Это промежуточный шаг: в K04 политика войдёт в KernelContext,
/// передаваемый явно.
/// </summary>
public static class OperationPolicyScope
{
    private static readonly AsyncLocal<OperationPolicy?> _current = new();

    /// <summary>Действующая политика; без области — строгая.</summary>
    public static OperationPolicy Current => _current.Value ?? KernelContext.Current?.Policy ?? OperationPolicy.Strict;

    public static IDisposable Use(OperationPolicy policy)
    {
        var prev = _current.Value;
        _current.Value = policy;
        return new Restore(() => _current.Value = prev);
    }

    private sealed class Restore(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }
}

/// <summary>
/// ХОД ОПЕРАЦИИ — сборщик событий этапов. Глубокие места (гранение входа, точный путь)
/// сообщают сюда о пропавшей грани или подмене маршрута, не меняя своих сигнатур. Вложенная
/// операция (скругление вызывает объединение) передаёт свои события наружу при завершении.
/// </summary>
internal sealed class OperationTrace : IDisposable
{
    private static readonly AsyncLocal<OperationTrace?> _current = new();
    public static OperationTrace? Current => _current.Value;

    private readonly OperationTrace? _parent;
    public readonly List<OperationDiagnostic> Events = new();
    public string Stage = "start";
    public bool Fallback;
    public bool FaceMissing;
    public bool Approximate;
    public OperationPolicy Policy = OperationPolicy.Strict;

    /// <summary>
    /// Строгая политика отказывает ДО приближённого пути, а не после: незачем строить тело,
    /// которое будет отвергнуто, и этап отказа должен называть место подмены маршрута.
    /// </summary>
    public static void RefuseFallbackIfStrict(string why)
    {
        if (_current.Value is { Policy: OperationPolicy.Strict }) throw new StrictRefusalException(why);
    }

    public OperationTrace()
    {
        _parent = _current.Value;
        _current.Value = this;
    }

    public static void Report(string code, string message, uint? element = null)
    {
        var t = _current.Value;
        if (t == null) return;
        t.Events.Add(new OperationDiagnostic(t.Stage, code, message, element));
        if (code is OperationCodes.FaceMissing or OperationCodes.FaceTessellationFailed) t.FaceMissing = true;
        if (code is OperationCodes.Fallback) t.Fallback = true;
    }

    /// <summary>Какой маршрут дал результат этой операции.</summary>
    public GuaranteeLevel? RouteLevel;

    public static void Route(GuaranteeLevel level)
    {
        if (_current.Value is not { } t) return;
        t.RouteLevel = level;
        if (level == GuaranteeLevel.Approximate) t.Approximate = true;
    }

    public static void Enter(string stage)
    {
        if (_current.Value is { } t) t.Stage = stage;
        KernelContext.Checkpoint(stage);
        FaultInjection.Hit(stage);
    }

    public void Dispose()
    {
        _current.Value = _parent;
        if (_parent == null) return;
        _parent.Events.AddRange(Events.Select(e => e with { Stage = "nested/" + e.Stage }));
        _parent.Fallback |= Fallback;
        _parent.FaceMissing |= FaceMissing;
        _parent.Approximate |= Approximate;
    }
}

/// <summary>
/// ИНЪЕКЦИЯ СБОЕВ ДЛЯ ПРОВЕРКИ КОНТРАКТА. Вооружается на время области и только в своём
/// логическом потоке; в обычной работе стоимость — одно чтение AsyncLocal на границе этапа.
/// Этапы: "boolean.input.face" (гранение грани входа), "boolean.exact" (точный путь),
/// "boolean.topology" (сборка тела из многоугольников), "mesh.face" (финальная сетка грани).
/// </summary>
public static class FaultInjection
{
    private static readonly AsyncLocal<(string Stage, int Skip)?> _armed = new();

    /// <summary>Бросить исключение на этапе stage (после skip благополучных прохождений).</summary>
    public static IDisposable Arm(string stage, int skip = 0)
    {
        var prev = _armed.Value;
        _armed.Value = (stage, skip);
        return new Disarm(() => _armed.Value = prev);
    }

    public static void Hit(string stage)
    {
        if (_armed.Value is not { } a || a.Stage != stage) return;
        if (a.Skip > 0) { _armed.Value = (a.Stage, a.Skip - 1); return; }
        throw new InjectedFaultException(stage);
    }

    private sealed class Disarm(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }
}

internal sealed class StrictRefusalException(string why) : Exception(why);

public sealed class InjectedFaultException(string stage) : Exception($"injected fault at {stage}")
{
    public string Stage { get; } = stage;
}

/// <summary>
/// K02. КОНТРАКТ ДЛЯ ОПЕРАЦИЙ КРОМЕ БУЛЕВОЙ (скругление, фаска, оболочка, уклон, ребро).
/// Операция выполняется над КОПИЕЙ списка тел вызывающего; новые тела проверяются (B-rep), и
/// только годный результат возвращается со статусом и уровнем гарантий. Отказ — исключение
/// внутри, сбой проверки, тело, помеченное приближённым при строгой политике, — даёт Failed со
/// стадией и кодом, а список вызывающего не меняется. Прежде такие операции писали прямо в
/// список вызывающего и о годности результата не сообщали.
/// </summary>
public static class OperationContract
{
    internal static readonly bool GeometryGuard = Environment.GetEnvironmentVariable("P3D_GEOMGUARD") != "0";
    static readonly bool OpAccuracyOff = Environment.GetEnvironmentVariable("P3D_OPACCURACY") == "0";
    /// <summary>Мутация P3D_DIAGENTITIES=0: диагностика без затронутых сущностей (прежде).</summary>
    internal static readonly bool DiagEntitiesOff = Environment.GetEnvironmentVariable("P3D_DIAGENTITIES") == "0";

    public static OperationResult Run(string name, IReadOnlyList<TSolid> bodies, Action<List<TSolid>> op,
                                      List<string>? warnings = null, OperationPolicy? policy = null)
    {
        var pol = policy ?? OperationPolicyScope.Current;
        var result = new OperationResult { Operation = name, Policy = pol };
        var work = bodies.ToList();
        var w = new List<string>();
        try { op(work); }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex))
        {
            result.Status = OperationStatus.Failed;
            result.FailedStage = name.ToLowerInvariant() + ".build";
            result.FailureCode = ex is OperationFailedException ofe && ofe.Result.FailureCode != null ? ofe.Result.FailureCode : OperationCodes.Exception;
            // KRN-01 (g.06): тип исхода — от исключения операции (некорректный вход, неподдержанный случай…), иначе по коду.
            // Прежде Outcome у отказа здесь не ставился вовсе и оставался Success (значение по умолчанию).
            result.Outcome = ex is Core.RebuildException rbe ? rbe.Outcome : OperationResult.OutcomeOf(result.FailureCode);
            result.Cause = ex is OperationFailedException inner && inner.Result.Cause != null ? inner.Result.Cause : ex;
            // KRN-01 (01.11): затронутые сущности — из отказа построителя (или вложенной операции). Мутация P3D_DIAGENTITIES=0.
            IReadOnlyList<string>? ents = DiagEntitiesOff ? null
                : ex is Core.RebuildException rbx && rbx.Entities.Count > 0 ? rbx.Entities
                : ex is OperationFailedException ofx ? ofx.Result.Diagnostics.SelectMany(d => d.Entities ?? Array.Empty<string>()).Distinct().ToList() : null;
            result.Diagnostics.Add(new OperationDiagnostic(result.FailedStage, result.FailureCode, ex.Message, null, ents));
            warnings?.AddRange(w);
            return result;
        }
        warnings?.AddRange(w);
        var fresh = work.Where(b => !bodies.Any(o => ReferenceEquals(o, b))).ToList();
        bool approximate = work.Any(b => b.Guarantee == GuaranteeLevel.Approximate);
        foreach (var b in fresh)
        {
            var v = SolidValidator.ValidateBRep(b);
            if (!v.IsValid)
            {
                result.Status = OperationStatus.Failed;
                result.FailedStage = name.ToLowerInvariant() + ".validate";
                result.FailureCode = OperationCodes.InvalidResult;
                result.Outcome = OperationOutcome.InternalFailure;
                var ents = DiagEntitiesOff ? null : Topology.EntityNames.Of(new[] { b }, v.Issues.Where(i => i.Severity == Topology.ValidationSeverity.Error && i.Element != null).Select(i => i.Element!.Value));
                result.Diagnostics.Add(new OperationDiagnostic(result.FailedStage, OperationCodes.InvalidResult, v.Summary(), null, ents));
                return result;
            }
        }
        // KRN-01 (ворота g.03): политика PreserveBRep — новые тела только из точных граней и рёбер (по самой B-rep).
        if (KernelSettings.Capture().GeometryPolicy == GeometryPolicy.PreserveBRep && GeometryGuard)
        {
            int nf = fresh.Sum(b => { var (f1, e1) = GeometryAudit.Faceted(b); return f1 + e1; });
            if (nf > 0)
            {
                result.Status = OperationStatus.Failed;
                result.FailedStage = name.ToLowerInvariant() + ".policy";
                result.FailureCode = OperationCodes.GeometryDowngradeRefused;
                result.Outcome = OperationOutcome.Unsupported;
                var ents = DiagEntitiesOff ? null : fresh.SelectMany(Topology.EntityNames.Faceted).Distinct().ToList();
                result.Diagnostics.Add(new OperationDiagnostic(result.FailedStage, OperationCodes.GeometryDowngradeRefused, $"geometry policy PreserveBRep: {nf} faceted face(s)/edge(s) in the result", null, ents));
                return result;
            }
        }
        if (approximate && pol == OperationPolicy.Strict)
        {
            result.Status = OperationStatus.Failed;
            result.FailedStage = name.ToLowerInvariant() + ".route";
            result.FailureCode = OperationCodes.FallbackRefused;
            result.Outcome = OperationOutcome.Unsupported;
            result.Diagnostics.Add(new OperationDiagnostic(result.FailedStage, OperationCodes.FallbackRefused, "an approximate body under the strict policy"));
            return result;
        }
        result.Status = approximate ? OperationStatus.SucceededApproximate : OperationStatus.Succeeded;
        result.Outcome = work.Count == 0 && bodies.Count > 0 ? OperationOutcome.Empty : OperationOutcome.Success;
        result.Guarantee = approximate ? GuaranteeLevel.Approximate
                         : fresh.Count > 0 ? fresh.Select(b => b.Guarantee).DefaultIfEmpty(GuaranteeLevel.Unspecified).Min() : GuaranteeLevel.Unspecified;
        result.Bodies = work;
        // KRN-01 (01.3): ОТЧЁТ О ТОЧНОСТИ У ВСЕХ ОПЕРАЦИЙ, а не только у булевой: допуск сущностей, измеренное отклонение (у
        // точного тела — кривых от поверхностей; у гранёного — фасет от источников граней) и бюджет. Отчёт, не отказ (отказ по
        // бюджету — у булевой, 02.10). Мутация P3D_OPACCURACY=0.
        if (!OpAccuracyOff && fresh.Count > 0)
        {
            var budget = ToleranceContext.Current;
            double stored = fresh.Max(b => Math.Max(b.Edges.Select(e => e.Tolerance).DefaultIfEmpty(0).Max(), b.Vertices.Select(v => v.Tolerance).DefaultIfEmpty(0).Max()));
            bool faceted = fresh.Any(b => { var (f1, e1) = GeometryAudit.Faceted(b); return f1 + e1 > 0; });
            if (!faceted) result.Accuracy = new AccuracyReport(stored, fresh.Max(b => Analytic.OperandTolerance.Measure(b)), budget.MaxEntityTolerance, "exact B-rep");
            else { var (dev, promised) = Ops.BooleanOp.FacetDeviation(fresh); result.Accuracy = new AccuracyReport(stored, dev, Math.Max(budget.MaxEntityTolerance, promised), "faceting (measured)"); }
            if (result.Accuracy.Measured is { } m && m > result.Accuracy.Budget)
                result.Diagnostics.Add(new OperationDiagnostic(name.ToLowerInvariant() + ".accuracy", "accuracy-over-budget", result.Accuracy.ToString()));
        }
        // KRN-01 (01.5): происхождение — если построитель записал его для единственного нового тела
        if (fresh.Count == 1 && fresh[0].Provenance is { } pv) result.ProvenanceMap = pv;
        return result;
    }

    /// <summary>Выполнить и применить к списку вызывающего; отказ — исключение (его ловит дерево).</summary>
    public static void Apply(string name, List<TSolid> bodies, Action<List<TSolid>> op, List<string>? warnings = null)
    {
        var r = Run(name, bodies, op, warnings);
        if (!r.Accepted) throw new OperationFailedException(r);
        bodies.Clear(); bodies.AddRange(r.Bodies);
    }
}
