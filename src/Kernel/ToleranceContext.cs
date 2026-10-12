namespace Parametric3D.Kernel;

/// <summary>
/// KRN-02: БЮДЖЕТ ТОЧНОСТИ — утверждённая числовая область ядра (KERNEL_SUPPORT_MATRIX.md). Неизменяемая запись в снимке
/// настроек ядра; `Current` — без выделения памяти.
///
/// ЕДИНИЦЫ ЯДРА (решение пользователя 2026-10-08): длина — мм, угол — радианы, прочие величины — СИ. Ядро не знает систем
/// единиц: перевод из единиц документа, пользователя и обменных файлов — на границе (приложение, импорт), ядру нужен только
/// масштаб числом.
///
/// Отдельные бюджеты построения (02.2): сшивка (Sewing); аппроксимация сплайном при экспорте (ExportFit — единственное место,
/// где кривая заменяется сплайном); кривые пересечения поверхностей (SsiAccuracy — измеряется как допуск сущности, превышение —
/// AccuracyExceeded); p-кривые (их отклонение пишется в CoEdge.PCurveDeviation и ограничено допуском ребра); интегрирование
/// объёма (IntegrationRelative — превышение — предупреждение детали); гранение CAD-операций (FacetChord/FacetAngle).
/// </summary>
public sealed record ToleranceContext
{
    /// <summary>Модельная точность: две точки ближе — одна точка (мм).</summary>
    public double ModelAccuracy { get; init; } = 1e-7;
    /// <summary>Угловая точность (рад).</summary>
    public double AngularAccuracy { get; init; } = 1e-12;
    /// <summary>Минимальный элемент: рёбра, зазоры, стенки меньше — вне поддержанной области (мм).</summary>
    public double MinFeature { get; init; } = 1e-4;
    /// <summary>Половина габаритного куба модели (мм): ±500 м.</summary>
    public double HalfSizeBox { get; init; } = 5e5;
    /// <summary>Предел измеренного допуска сущности (мм): больше — точность превышена.</summary>
    public double MaxEntityTolerance { get; init; } = 1e-5;
    /// <summary>Сшивка многогранного пути: вершины ближе — одна вершина (мм). Прежде `BooleanOp.WeldTol`.</summary>
    public double Sewing { get; init; } = 1e-6;
    /// <summary>Аппроксимация кривых сплайном при экспорте (мм). Прежде 1e-4 в `StepExact`; в 1000 раз грубее модельной
    /// точности — ужесточение после замера (KRN-09).</summary>
    public double ExportFit { get; init; } = 1e-4;
    /// <summary>KRN-09 (09.5): лечение импорта — наибольший зазор вершины до поверхностей её граней и неплоскостность
    /// закрываемого контура, которые устраняются (мм). Больше — тело читается как есть и сообщается негодным.</summary>
    public double ImportHeal { get; init; } = 1e-4;
    /// <summary>
    /// g.04 (02.2): бюджет ИНТЕГРИРОВАНИЯ — допустимая относительная граница ошибки объёма (допуски сущностей × площадь +
    /// измеренная ошибка квадратуры, AnalyticMeasure.Volume). Больше — деталь сообщает «точность объёма превышена».
    /// </summary>
    public double IntegrationRelative { get; init; } = 1e-5;
    /// <summary>g.04 (02.2): бюджет кривых пересечения поверхностей (SSI) — отклонение точек кривой от обеих поверхностей (мм);
    /// измеряется как допуск сущности результата (Analytic.EntityTolerance) и ограничен MaxEntityTolerance.</summary>
    public double SsiAccuracy => MaxEntityTolerance;
    /// <summary>
    /// g.04 (01.12): ГРАНЕНИЕ CAD-ОПЕРАЦИЙ (многогранный путь) — свой допуск, не допуск отображения. Прежде операнд булевой
    /// гранился по хорде отображения: смена хорды экрана меняла геометрию следующей многогранной операции.
    /// </summary>
    public double FacetChord { get; init; } = 0.02;
    /// <summary>Угловой шаг гранения CAD-операций (рад).</summary>
    public double FacetAngle { get; init; } = 7.5 * Math.PI / 180;

    public static readonly ToleranceContext Default = new();

    /// <summary>Действующий бюджет: снимка контекста ядра, иначе по умолчанию.</summary>
    public static ToleranceContext Current => KernelContext.Current?.Settings.Tolerance ?? Default;
}
