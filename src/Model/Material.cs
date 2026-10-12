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
    public static Material Steel() => new("Сталь", 7850) { YoungModulus = 2.0e11, PoissonRatio = 0.3, YieldStrength = 2.35e8, UltimateStrength = 3.7e8, TangentModulus = 1.2e9, ThermalExpansion = 1.2e-5, ThermalConductivity = 52, SpecificHeat = 470, Designation = "Ст3", R = 0.62, G = 0.64, B = 0.67 };
    public static Material StainlessSteel() => new("Сталь нержавеющая", 7900) { YoungModulus = 1.93e11, PoissonRatio = 0.3, YieldStrength = 2.05e8, UltimateStrength = 5.2e8, TangentModulus = 1.5e9, ThermalExpansion = 1.6e-5, ThermalConductivity = 15, SpecificHeat = 500, Designation = "12Х18Н10Т", R = 0.72, G = 0.74, B = 0.76 };
    public static Material Aluminium() => new("Алюминий", 2700) { YoungModulus = 7.1e10, PoissonRatio = 0.33, YieldStrength = 1.5e8, UltimateStrength = 3.1e8, TangentModulus = 5.0e8, ThermalExpansion = 2.36e-5, ThermalConductivity = 122, SpecificHeat = 922, Designation = "АМг6", R = 0.82, G = 0.83, B = 0.85 };
    public static Material Brass() => new("Латунь", 8500) { YoungModulus = 1.0e11, PoissonRatio = 0.35, YieldStrength = 2.0e8, UltimateStrength = 3.8e8, TangentModulus = 9.0e8, ThermalExpansion = 2.0e-5, ThermalConductivity = 110, SpecificHeat = 380, Designation = "Л63", R = 0.85, G = 0.72, B = 0.35 };
    public static Material Copper() => new("Медь", 8940) { YoungModulus = 1.1e11, PoissonRatio = 0.34, YieldStrength = 7.0e7, UltimateStrength = 2.2e8, TangentModulus = 6.0e8, ThermalExpansion = 1.7e-5, ThermalConductivity = 390, SpecificHeat = 385, Designation = "М1", R = 0.78, G = 0.50, B = 0.30 };
    public static Material Titanium() => new("Титан", 4500) { YoungModulus = 1.12e11, PoissonRatio = 0.32, YieldStrength = 2.5e8, UltimateStrength = 3.5e8, TangentModulus = 1.0e9, ThermalExpansion = 8.2e-6, ThermalConductivity = 18, SpecificHeat = 540, Designation = "ВТ1-0", R = 0.70, G = 0.70, B = 0.72 };
    public static Material Plastic() => new("Пластик", 1200) { YoungModulus = 2.0e9, PoissonRatio = 0.4, YieldStrength = 4.5e7, UltimateStrength = 7.5e7, TangentModulus = 1.0e8, ThermalExpansion = 8.0e-5, ThermalConductivity = 0.25, SpecificHeat = 1700, Designation = "ПА6", R = 0.90, G = 0.90, B = 0.86 };

    public static IReadOnlyList<Material> Common() => new[]
    {
        Steel(), StainlessSteel(), Aluminium(), Brass(), Copper(), Titanium(), Plastic()
    };

    public Material Copy()
    {
        var m = new Material(Name, Density)
        {
            R = R, G = G, B = B, Designation = Designation,
            Specular = Specular, Shininess = Shininess, Opacity = Opacity,
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
