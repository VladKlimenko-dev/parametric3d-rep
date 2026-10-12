namespace Parametric3D.Core;

public class P3DException : Exception
{
    public P3DException(string msg) : base(msg) { }
    public P3DException(string msg, Exception inner) : base(msg, inner) { }
}

/// <summary>
/// Ошибка перестроения фичи: не роняет приложение, помечает фичу как Failed.
/// KRN-01 (ворота g.06): у каждого отказа — ТИП ИСХОДА (некорректный вход, неподдержанный случай, сбой…). Конструктор без типа
/// запрещён компилятором: ни одно место отказа не может остаться без классификации.
/// </summary>
public class RebuildException : P3DException
{
    public Kernel.OperationOutcome Outcome { get; }
    /// <summary>KRN-01 (01.11): затронутые сущности — устойчивыми именами (Topology.EntityNames); пусто — не названы.</summary>
    public IReadOnlyList<string> Entities { get; init; } = Array.Empty<string>();
    [Obsolete("KRN-01: укажите тип исхода — RebuildException(OperationOutcome, string)", error: true)]
    public RebuildException(string msg) : base(msg) { Outcome = Kernel.OperationOutcome.InternalFailure; }
    public RebuildException(Kernel.OperationOutcome outcome, string msg) : base(msg) { Outcome = outcome; }
}

/// <summary>
/// KRN-08: НЕОДНОЗНАЧНАЯ ССЫЛКА — запрос разрешения, а не молчаливый выбор. Кандидаты: имя сущности и точка на ней
/// (для показа и для выбора пользователем: выбранная точка передаётся обратно в ссылку).
/// </summary>
public sealed class AmbiguousReferenceException : RebuildException
{
    public IReadOnlyList<(string Name, OpenTK.Mathematics.Vector3d At)> Candidates { get; }
    public string What { get; }
    public AmbiguousReferenceException(string what, IReadOnlyList<(string Name, OpenTK.Mathematics.Vector3d At)> candidates, string why)
        : base(Kernel.OperationOutcome.Ambiguous, $"{what}: ambiguous reference — {why}; {candidates.Count} candidates, a choice is required")
    { What = what; Candidates = candidates; }
}
