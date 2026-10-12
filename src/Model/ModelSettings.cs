namespace Parametric3D.Model;

/// <summary>
/// ПОСТАВКА 2. НАСТРОЙКИ СЛОЯ МОДЕЛИ, ВЛИЯЮЩИЕ НА РЕЗУЛЬТАТ, — ОДНИМ НЕИЗМЕНЯЕМЫМ СНИМКОМ.
///
/// Как KernelSettings у ядра: расчёт детали кладёт снимок в свой контекст (KernelContext.WithExtension), и
/// внутри расчёта флаги читаются из снимка; общие поля — только значения по умолчанию вне контекста, а их
/// правка внутри контекста — исключение. Прежде это были общие изменяемые поля классов модели: правка из
/// интерфейса посреди фонового перестроения действовала на него с середины, и два расчёта видели правки
/// друг друга.
/// </summary>
public sealed record ModelSettings
{
    public bool ExactCircularCuts { get; init; }
    public bool ExactHoles { get; init; }
    public bool BlindEdgeCut { get; init; }
    public bool CompositeTool { get; init; }
    public bool UseCheckpoints { get; init; }
    public bool VerifyPublishedBodies { get; init; }
    public bool ValidateResults { get; init; }
    public bool DeterministicKeys { get; init; }

    /// <summary>Действующий снимок: из контекста, если положен, иначе общие значения по умолчанию.</summary>
    public static ModelSettings Capture() => Current ?? new ModelSettings
    {
        ExactCircularCuts = Features.ExtrudeFeature.ExactCircularCuts,
        ExactHoles = Features.HoleFeature.ExactHoles,
        BlindEdgeCut = Features.HoleFeature.BlindEdgeCut,
        CompositeTool = Features.HoleFeature.CompositeTool,
        UseCheckpoints = Part.UseCheckpoints,
        VerifyPublishedBodies = Part.VerifyPublishedBodies,
        ValidateResults = Part.ValidateResults,
        DeterministicKeys = Features.Feature.DeterministicKeys,
    };

    /// <summary>Снимок действующего контекста (null — вне контекста или контекст без слоя модели).</summary>
    public static ModelSettings? Current => Kernel.KernelContext.Current?.Extension<ModelSettings>();
}
