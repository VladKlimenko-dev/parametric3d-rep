namespace Parametric3D.Core;

/// <summary>Единые допуски ядра. Все сравнения геометрии идут только через них.</summary>
public static class Tol
{
    /// <summary>Линейный допуск совпадения точек (мм).</summary>
    public const double Linear = 1e-7;
    /// <summary>Угловой допуск (рад).</summary>
    public const double Angular = 1e-9;
    /// <summary>Допуск склейки вершин при построении топологии.</summary>
    public const double Weld = 1e-6;
    /// <summary>
    /// Максимальная стрелка прогиба при тесселяции (мм). ИЗМЕРЕНО, чем она оборачивается для
    /// точности тела: вырез цилиндра Ø24 в бруске даёт объём больше расчётного на 19.7 мм³
    /// (0.05%) при 0.02, на 5.0 мм³ (0.013%) при 0.005 и на 2.0 мм³ (0.005%) при 0.002 — то
    /// есть ошибка падает пропорционально допуску. Но время растёт куда быстрее: сцена со
    /// скруглением идёт 5 189 мс при 0.02 и 20 561 мс при 0.005, вчетверо дольше.
    ///
    /// Поэтому умолчание оставлено прежним, а допуск сделан настраиваемым (P3D_CHORD): там, где
    /// нужна точность детали, его стоит ужесточить сознательно, зная цену.
    ///
    /// ПОГРЕШНОСТЬ ПО ВИДАМ РЕЗЦОВ (снятый объём против расчётного):
    ///
    ///   резец                     допуск 0.02      допуск 0.005
    ///   цилиндр Ø24               +0.051%          +0.013%
    ///   сфера Ø30                 −0.433%          −0.110%
    ///   тор R12/4                 −0.451%          −0.207%
    ///   скругление R6 (ребро 20)  +1.044%          +0.376%
    ///
    /// Знак разный не случайно: гранёный цилиндр ОПИСАН вокруг настоящего (снимает меньше, тело
    /// выходит больше), а сфера и тор ВПИСАНЫ (снимают меньше расчётного). Наибольшая
    /// погрешность у скруглений — там гранится и сечение, и путь.
    ///
    /// ЧТО ИМЕННО ДАЁТ ОШИБКУ СКРУГЛЕНИЯ — измерено раздельно:
    ///
    ///   угол 7.5°, прогиб 0.02   → 1.044%      (как сейчас)
    ///   угол 3°,   прогиб 0.02   → 0.167%      сечение мельче — ошибка вшестеро меньше
    ///   угол 7.5°, прогиб 0.005  → 0.376%      прогиб мельче — втрое меньше
    ///
    /// То есть у скруглений главное — УГОЛ ГРАНЕНИЯ СЕЧЕНИЯ, а не прогиб. Цена: сцена со
    /// скруглением идёт 4 997 мс при 7.5° и 9 612 мс при 3°, резец растёт с 2 190 до 6 980
    /// многоугольников. При 1.5° расчёт падает по памяти — у дробности есть практический предел.
    /// </summary>
    private static double _gChord = double.TryParse(Environment.GetEnvironmentVariable("P3D_CHORD"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double c) && c > 0
            ? c : 0.02;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static double Chord
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.Chord : _gChord;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gChord, value, "Tol.Chord");
    }
    /// <summary>
    /// Предельный шаг дуги при разбиении, радианы: 7.5°.
    ///
    /// ИЗМЕНЕНО с 0.20 (11.46°). Большая дуга делится по допуску хорды, и шаг
    /// у неё мельче; маленькая — по этому пределу. Касательная цепочка рёбер
    /// рвётся на изломе больше 10° (EdgeChain), и там, где дуга малого радиуса
    /// не восстановилась в кривую, цепочка обрывалась на первой же хорде.
    /// Шаг 7.5° оставляет запас ниже порога.
    /// </summary>
    /// <summary>
    /// Угловой шаг гранения. Задаётся и переключателем P3D_CHORDANG (в градусах) — чтобы можно
    /// было измерять, чем обходится дробность: от неё зависит число многоугольников резца, а
    /// значит и время булевой.
    /// </summary>
    private static double _gChordAngle = double.TryParse(Environment.GetEnvironmentVariable("P3D_CHORDANG"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double a) && a > 0
            ? a * Math.PI / 180
            : 7.5 * Math.PI / 180;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static double ChordAngle
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ChordAngle : _gChordAngle;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gChordAngle, value, "Tol.ChordAngle");
    }

    public static bool Eq(double a, double b, double t = Linear) => Math.Abs(a - b) <= t;
    public static bool IsZero(double a, double t = Linear) => Math.Abs(a) <= t;
}
