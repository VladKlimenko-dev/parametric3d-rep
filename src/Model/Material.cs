using OpenTK.Mathematics;

namespace Parametric3D.Model;

/// <summary>
/// МАТЕРИАЛ ДЕТАЛИ. Плотность нужна не для красоты: без неё масса детали и центр тяжести узла
/// считаются «в единицах объёма», а не в граммах, и сравнить их с чертежом нельзя.
///
/// Цвет и название хранятся рядом с плотностью, потому что в работе они всегда идут вместе:
/// сталь серая, латунь жёлтая, и по цвету в сборке узнают материал раньше, чем откроют свойства.
/// </summary>
public sealed class Material
{
    public string Name;

    /// <summary>
    /// Плотность в кг/м³ — в этих единицах её хранит ядро и считает массу. Справочники приводят
    /// г/см³, поэтому рядом есть пересчёт: путать их нельзя, разница в тысячу раз.
    /// </summary>
    public double Density;

    /// <summary>Та же плотность в г/см³ — как в справочниках.</summary>
    public double DensityGPerCm3
    {
        get => Density / 1000;
        set => Density = value * 1000;
    }

    /// <summary>Цвет для показа: доли от нуля до единицы.</summary>
    public double R = 0.75, G = 0.75, B = 0.78;

    // ------------------------------------------------------------------ показ
    //
    // Эти свойства нужны не расчёту, а глазу: ими окрашивается деталь на экране. Они были в
    // материале с самого начала; цвет хранится и разложенным на доли (R, G, B — так его удобно
    // писать в файл), и одним значением с прозрачностью — так его ждёт показ.

    /// <summary>Цвет с прозрачностью — в том виде, в каком его берёт показ.</summary>
    public Vector4 Color
    {
        get => new((float)R, (float)G, (float)B, (float)Opacity);
        set { R = value.X; G = value.Y; B = value.Z; Opacity = value.W; }
    }

    /// <summary>Сила блика.</summary>
    public float Specular = 0.25f;

    /// <summary>Резкость блика: чем больше, тем мельче пятно.</summary>
    public float Shininess = 48f;

    /// <summary>u10: металл — блик цвета материала и отражение окружения (стиль показа «Материал»).</summary>
    public bool Metallic;

    /// <summary>u10: группа в списке материалов (Сталь, Алюминий, Пластик…).</summary>
    public string Group = "";

    /// <summary>Непрозрачность: единица — сплошной, ноль — невидимый.</summary>
    public double Opacity = 1.0;

    /// <summary>Обозначение по стандарту, если оно есть: «Ст3», «АМг6», «12Х18Н10Т».</summary>
    public string Designation = "";

    // ------------------------------------------------------------------ упругость
    //
    // Эти свойства не нужны самому ядру геометрии, но без них невозможен расчёт: модуль Юнга и
    // коэффициент Пуассона задают поведение вещества в упругой области, а модуль сдвига через
    // них выражается. Он хранится отдельно, потому что у неизотропных веществ связь не
    // выполняется, и подменять измеренное значение расчётным нельзя.

    /// <summary>Модуль Юнга (модуль продольной упругости), Па.</summary>
    public double YoungModulus;

    /// <summary>Коэффициент Пуассона: поперечная деформация при продольной.</summary>
    public double PoissonRatio;

    /// <summary>Модуль сдвига, Па. Ноль означает «считать по Юнгу и Пуассону».</summary>
    public double ShearModulus;

    /// <summary>Модуль сдвига: заданный или выведенный из двух других.</summary>
    public double EffectiveShearModulus =>
        ShearModulus > 0 ? ShearModulus
                         : PoissonRatio > -1 && PoissonRatio < 0.5
                           ? YoungModulus / (2 * (1 + PoissonRatio))
                           : 0;

    // ------------------------------------------------------------------ прочность

    /// <summary>Предел текучести, Па: с него начинаются необратимые деформации.</summary>
    public double YieldStrength;

    /// <summary>Предел прочности (временное сопротивление), Па: разрушение.</summary>
    public double UltimateStrength;

    /// <summary>Касательный модуль, Па: жёсткость уже за пределом текучести.</summary>
    public double TangentModulus;

    /// <summary>
    /// Кривая пластичности: истинные деформация и напряжение. Пустая означает, что расчёт
    /// ограничен упругой областью, а не что вещество идеально пластично.
    /// </summary>
    public readonly List<(double Strain, double Stress)> StressStrain = new();

    /// <summary>
    /// Кривая усталости: число циклов и амплитуда напряжений. Нужна для валов, пружин и всего,
    /// что нагружено циклически.
    /// </summary>
    public readonly List<(double Cycles, double Stress)> FatigueCurve = new();

    // ------------------------------------------------------------------ тепло

    /// <summary>Коэффициент теплового расширения, 1/К.</summary>
    public double ThermalExpansion;

    /// <summary>Теплопроводность, Вт/(м·К).</summary>
    public double ThermalConductivity;

    /// <summary>Удельная теплоёмкость, Дж/(кг·К).</summary>
    public double SpecificHeat;

    /// <summary>Достаточно ли сведений для прочностного расчёта в упругой области.</summary>
    public bool ReadyForElasticAnalysis =>
        Density > 0 && YoungModulus > 0 && PoissonRatio > -1 && PoissonRatio < 0.5;

    /// <summary>Чего не хватает для расчёта — списком, чтобы не гадать.</summary>
    public List<string> MissingForAnalysis()
    {
        var miss = new List<string>();
        if (Density <= 0) miss.Add("плотность");
        if (YoungModulus <= 0) miss.Add("модуль Юнга");
        if (PoissonRatio <= -1 || PoissonRatio >= 0.5) miss.Add("коэффициент Пуассона");
        if (YieldStrength <= 0) miss.Add("предел текучести");
        return miss;
    }

    public Material(string name, double density) { Name = name; Density = density; }

    /// <summary>Материал по одному имени: плотность задаётся потом, из списка или вручную.</summary>
    public Material(string name) : this(name, 0) { }

    /// <summary>Плотность в кг/мм³ — в этих единицах ядро считает массу.</summary>
    public double DensityKgPerMm3 => Density * 1e-9;

    public override string ToString() =>
        $"{Name}" + (string.IsNullOrEmpty(Designation) ? "" : $" ({Designation})") +
        $", {DensityGPerCm3:0.###} г/см³";

    // ------------------------------------------------------------------ обиходные

    /// <summary>
    /// Ходовые материалы. Список нарочно короткий: это не справочник, а разумные умолчания,
    /// чтобы масса считалась сразу, а не после заполнения свойств.
    /// </summary>
    // Вызываются как методы: каждый раз выдаётся СВОЙ материал, а не общий на всех. Иначе смена
    // цвета у одной детали меняла бы его у всех, кому достался тот же материал.
    public static Material Steel() => new("Сталь", 7850) { YoungModulus = 2.0e11, PoissonRatio = 0.3, YieldStrength = 2.35e8, UltimateStrength = 3.7e8, TangentModulus = 1.2e9, ThermalExpansion = 1.2e-5, ThermalConductivity = 52, SpecificHeat = 470, Designation = "Ст3", R = 0.62, G = 0.64, B = 0.67, Group = "Сталь", Metallic = true, Specular = 0.6f, Shininess = 64 };
    public static Material StainlessSteel() => new("Сталь нержавеющая", 7900) { YoungModulus = 1.93e11, PoissonRatio = 0.3, YieldStrength = 2.05e8, UltimateStrength = 5.2e8, TangentModulus = 1.5e9, ThermalExpansion = 1.6e-5, ThermalConductivity = 15, SpecificHeat = 500, Designation = "12Х18Н10Т", R = 0.72, G = 0.74, B = 0.76, Group = "Сталь нержавеющая", Metallic = true, Specular = 0.6f, Shininess = 64 };
    public static Material Aluminium() => new("Алюминий", 2700) { YoungModulus = 7.1e10, PoissonRatio = 0.33, YieldStrength = 1.5e8, UltimateStrength = 3.1e8, TangentModulus = 5.0e8, ThermalExpansion = 2.36e-5, ThermalConductivity = 122, SpecificHeat = 922, Designation = "АМг6", R = 0.82, G = 0.83, B = 0.85, Group = "Алюминий", Metallic = true, Specular = 0.6f, Shininess = 64 };
    public static Material Brass() => new("Латунь", 8500) { YoungModulus = 1.0e11, PoissonRatio = 0.35, YieldStrength = 2.0e8, UltimateStrength = 3.8e8, TangentModulus = 9.0e8, ThermalExpansion = 2.0e-5, ThermalConductivity = 110, SpecificHeat = 380, Designation = "Л63", R = 0.85, G = 0.72, B = 0.35, Group = "Медь и сплавы", Metallic = true, Specular = 0.6f, Shininess = 64 };
    public static Material Copper() => new("Медь", 8940) { YoungModulus = 1.1e11, PoissonRatio = 0.34, YieldStrength = 7.0e7, UltimateStrength = 2.2e8, TangentModulus = 6.0e8, ThermalExpansion = 1.7e-5, ThermalConductivity = 390, SpecificHeat = 385, Designation = "М1", R = 0.78, G = 0.50, B = 0.30, Group = "Медь и сплавы", Metallic = true, Specular = 0.6f, Shininess = 64 };
    public static Material Titanium() => new("Титан", 4500) { YoungModulus = 1.12e11, PoissonRatio = 0.32, YieldStrength = 2.5e8, UltimateStrength = 3.5e8, TangentModulus = 1.0e9, ThermalExpansion = 8.2e-6, ThermalConductivity = 18, SpecificHeat = 540, Designation = "ВТ1-0", R = 0.70, G = 0.70, B = 0.72, Group = "Титан", Metallic = true, Specular = 0.6f, Shininess = 64 };
    public static Material Plastic() => new("Пластик", 1200) { YoungModulus = 2.0e9, PoissonRatio = 0.4, YieldStrength = 4.5e7, UltimateStrength = 7.5e7, TangentModulus = 1.0e8, ThermalExpansion = 8.0e-5, ThermalConductivity = 0.25, SpecificHeat = 1700, Designation = "ПА6", R = 0.90, G = 0.90, B = 0.86, Group = "Пластик", Metallic = false };

    public static IReadOnlyList<Material> Common() => Library();

    // ------------------------------------------------------------------ u10: библиотека
    //
    // СПРАВОЧНЫЕ ЗНАЧЕНИЯ — ТИПОВЫЕ, ДЛЯ ОЦЕНКИ. Плотность, модуль Юнга, Пуассон, пределы текучести и прочности,
    // теплопроводность, расширение и теплоёмкость — средние по ГОСТ/ASTM и справочникам (MatWeb, «Марочник сталей и
    // сплавов»); для ответственного расчёта значения берутся из сертификата на конкретную партию.

    private static Material M(string group, string name, string desig, double rho, double eGPa, double nu, double yMPa, double uMPa,
                              double cte, double k, double cp, double r, double g, double b, bool metal, float spec = 0.6f, float shine = 64)
        => new(name, rho)
        {
            Group = group, Designation = desig, YoungModulus = eGPa * 1e9, PoissonRatio = nu, YieldStrength = yMPa * 1e6, UltimateStrength = uMPa * 1e6,
            TangentModulus = eGPa * 1e9 * 0.01, ThermalExpansion = cte, ThermalConductivity = k, SpecificHeat = cp,
            R = r, G = g, B = b, Metallic = metal, Specular = spec, Shininess = shine,
        };

    /// <summary>Библиотека материалов (каждый вызов — свои копии).</summary>
    public static IReadOnlyList<Material> Library() => new List<Material>
    {
        Steel().With("Сталь", true),
        M("Сталь", "Сталь 20", "20 (AISI 1020)", 7850, 206, 0.30, 245, 410, 1.21e-5, 52, 486, 0.60, 0.62, 0.65, true),
        M("Сталь", "Сталь 45", "45 (AISI 1045)", 7826, 200, 0.30, 355, 600, 1.16e-5, 48, 486, 0.58, 0.60, 0.63, true),
        M("Сталь", "Сталь 40Х", "40Х (AISI 5140)", 7820, 214, 0.29, 785, 980, 1.17e-5, 46, 466, 0.56, 0.58, 0.62, true),
        M("Сталь", "Сталь 09Г2С", "09Г2С", 7850, 206, 0.30, 345, 490, 1.14e-5, 40, 480, 0.59, 0.61, 0.64, true),
        StainlessSteel().With("Сталь нержавеющая", true),
        M("Сталь нержавеющая", "AISI 304", "08Х18Н10 (AISI 304)", 7930, 193, 0.29, 215, 505, 1.73e-5, 16, 500, 0.74, 0.75, 0.77, true, 0.8f, 96),
        M("Сталь нержавеющая", "AISI 316", "10Х17Н13М2Т (AISI 316)", 8000, 193, 0.30, 205, 515, 1.6e-5, 16, 500, 0.73, 0.74, 0.76, true, 0.8f, 96),
        M("Чугун", "Чугун СЧ20", "СЧ20 (EN-GJL-200)", 7100, 110, 0.26, 0, 200, 1.1e-5, 50, 460, 0.38, 0.39, 0.41, true, 0.25f, 24),
        M("Чугун", "Чугун ВЧ50", "ВЧ50 (EN-GJS-500)", 7100, 169, 0.28, 320, 500, 1.1e-5, 36, 460, 0.42, 0.43, 0.45, true, 0.3f, 28),
        Aluminium().With("Алюминий", true),
        M("Алюминий", "Алюминий 6061-T6", "АД33 (6061-T6)", 2700, 68.9, 0.33, 276, 310, 2.36e-5, 167, 896, 0.80, 0.82, 0.85, true, 0.7f, 80),
        M("Алюминий", "Алюминий 7075-T6", "В95 (7075-T6)", 2810, 71.7, 0.33, 503, 572, 2.34e-5, 130, 960, 0.78, 0.80, 0.83, true, 0.7f, 80),
        M("Алюминий", "Алюминий Д16Т", "Д16Т (2024-T3)", 2780, 73.1, 0.33, 345, 483, 2.32e-5, 121, 875, 0.79, 0.80, 0.82, true, 0.7f, 80),
        M("Алюминий", "Алюминий АК7 (литьё)", "АК7 (A356)", 2680, 72.4, 0.33, 165, 230, 2.14e-5, 151, 963, 0.74, 0.75, 0.77, true, 0.4f, 32),
        Titanium().With("Титан", true),
        M("Титан", "Титан ВТ6", "ВТ6 (Ti-6Al-4V)", 4430, 113.8, 0.34, 880, 950, 8.6e-6, 6.7, 526, 0.66, 0.66, 0.68, true, 0.6f, 64),
        Copper().With("Медь и сплавы", true),
        Brass().With("Медь и сплавы", true),
        M("Медь и сплавы", "Бронза БрАЖ9-4", "БрАЖ9-4", 7600, 115, 0.34, 250, 600, 1.7e-5, 59, 420, 0.80, 0.60, 0.32, true, 0.7f, 72),
        M("Магний", "Магний AZ31B", "МА2-1 (AZ31B)", 1770, 45, 0.35, 200, 260, 2.6e-5, 96, 1000, 0.78, 0.79, 0.80, true, 0.5f, 48),
        Plastic().With("Пластик", false),
        M("Пластик", "ABS", "ABS", 1050, 2.3, 0.35, 40, 44, 9e-5, 0.17, 1400, 0.92, 0.92, 0.90, false, 0.25f, 24),
        M("Пластик", "PLA", "PLA", 1240, 3.5, 0.36, 60, 65, 6.8e-5, 0.13, 1800, 0.95, 0.95, 0.95, false, 0.3f, 32),
        M("Пластик", "PETG", "PETG", 1270, 2.1, 0.38, 50, 53, 6.8e-5, 0.2, 1200, 0.85, 0.92, 0.95, false, 0.5f, 64),
        M("Пластик", "Полиамид PA12", "PA12", 1010, 1.6, 0.39, 46, 50, 1.0e-4, 0.24, 1700, 0.88, 0.88, 0.86, false, 0.2f, 16),
        M("Пластик", "Полиацеталь POM", "POM-C", 1410, 2.9, 0.35, 65, 67, 1.1e-4, 0.31, 1500, 0.94, 0.94, 0.93, false, 0.35f, 40),
        M("Пластик", "Поликарбонат PC", "PC", 1200, 2.35, 0.37, 62, 66, 6.5e-5, 0.2, 1200, 0.80, 0.86, 0.92, false, 0.7f, 96),
        M("Пластик", "Капролон (PA6 литой)", "ПА6 блочный", 1150, 3.0, 0.40, 75, 85, 8e-5, 0.29, 1700, 0.93, 0.90, 0.78, false, 0.25f, 24),
        M("Пластик", "Фторопласт PTFE", "Ф-4 (PTFE)", 2200, 0.5, 0.46, 12, 25, 1.2e-4, 0.25, 1000, 0.97, 0.97, 0.97, false, 0.15f, 12),
        M("Прочее", "Резина (NBR 70)", "NBR 70 Shore A", 1300, 0.006, 0.49, 0, 15, 2e-4, 0.25, 1500, 0.12, 0.12, 0.13, false, 0.1f, 8),
        M("Прочее", "Дерево (сосна)", "Сосна", 520, 10, 0.3, 0, 40, 5e-6, 0.12, 1700, 0.80, 0.62, 0.38, false, 0.1f, 8),
        M("Прочее", "Стекло", "Стекло натриевое", 2500, 70, 0.22, 0, 50, 9e-6, 1.0, 840, 0.80, 0.90, 0.92, false, 0.9f, 128),
        M("Прочее", "Бетон", "B25", 2400, 30, 0.2, 0, 3, 1e-5, 1.7, 880, 0.62, 0.62, 0.60, false, 0.05f, 4),
    };

    /// <summary>Материал из библиотеки по имени (копия) или null.</summary>
    public static Material? FromLibrary(string name) => Library().FirstOrDefault(m => m.Name == name);

    private Material With(string group, bool metal) { Group = group; Metallic = metal; return this; }

    public Material Copy()
    {
        var m = new Material(Name, Density)
        {
            R = R, G = G, B = B, Designation = Designation,
            Specular = Specular, Shininess = Shininess, Opacity = Opacity, Metallic = Metallic, Group = Group,
            YoungModulus = YoungModulus, PoissonRatio = PoissonRatio, ShearModulus = ShearModulus,
            YieldStrength = YieldStrength, UltimateStrength = UltimateStrength,
            TangentModulus = TangentModulus,
            ThermalExpansion = ThermalExpansion, ThermalConductivity = ThermalConductivity,
            SpecificHeat = SpecificHeat
        };
        m.StressStrain.AddRange(StressStrain);
        m.FatigueCurve.AddRange(FatigueCurve);
        return m;
    }
}

/// <summary>
/// СВЕДЕНИЯ О ДОКУМЕНТЕ: то, что попадает в основную надпись чертежа и в перечень. Без них
/// деталь безымянна: в сборке из сорока деталей отличить их можно только по обозначению.
/// </summary>
public sealed class DocumentInfo
{
    /// <summary>Обозначение: то, по чему деталь ищут в перечне.</summary>
    public string Code = "";

    /// <summary>Наименование для основной надписи.</summary>
    public string Title = "";

    public string Author = "";
    public string Checker = "";
    public string Company = "";

    /// <summary>Когда создан и когда правился в последний раз.</summary>
    public DateTime Created = DateTime.UtcNow;
    public DateTime Modified = DateTime.UtcNow;

    /// <summary>Изменение и примечание — то, что ведут по ходу работы.</summary>
    public string Revision = "";
    public string Note = "";

    public DocumentInfo Copy() => new()
    {
        Code = Code, Title = Title, Author = Author, Checker = Checker, Company = Company,
        Created = Created, Modified = Modified, Revision = Revision, Note = Note
    };

    public override string ToString() =>
        string.IsNullOrEmpty(Code) ? Title : $"{Code} — {Title}";
}
