using System.Drawing;
using OpenTK.Mathematics;

namespace Parametric3D.Core;

/// <summary>Единицы измерения длины. Внутри модель всегда в миллиметрах.</summary>
public enum UnitOfMeasure { Millimeter, Centimeter, Meter, Inch }

/// <summary>
/// Все настройки внешнего вида и документа в одном месте.
///
/// ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. Цвета и размеры раньше были рассыпаны по
/// RenderStyles, GridRenderer, DimensionRenderer и ProjectTree — чтобы
/// поменять оттенок подсветки, приходилось искать его в четырёх местах
/// и надеяться, что нигде не осталось пятого. Здесь они объявлены один
/// раз, а рисующий код читает их отсюда.
///
/// Экземпляр принадлежит документу (Document.Settings) и сохраняется
/// вместе с ним. Settings.Current указывает на настройки активного
/// документа: рисующему коду не нужно тащить документ через пять слоёв
/// ради цвета линии.
///
/// Все длины в пикселях экрана, если не сказано иное. Все цвета — RGBA
/// в диапазоне 0..1.
/// </summary>
public sealed class Settings
{
    /// <summary>Настройки активного документа. Их читает вся отрисовка.</summary>
    public static Settings Current { get; set; } = new();

    /// <summary>Поднимается при изменении настроек: виды перестраивают таблицы стилей.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    // ==================================================== документ

    /// <summary>Единицы, в которых показываются длины. Модель всегда в миллиметрах.</summary>
    public UnitOfMeasure Units = UnitOfMeasure.Millimeter;

    /// <summary>
    /// Новые размеры эскиза сразу управляют геометрией (заблокированы).
    /// Выключите — и поставленный размер будет только измерять.
    /// </summary>
    public bool SketchDefaultLockedDimension = true;


    /// <summary>Знаков после запятой в подписях размеров.</summary>
    public int LengthDecimals = 2;
    public int AngleDecimals = 2;

    public double UnitFactor => Units switch
    {
        UnitOfMeasure.Centimeter => 0.1,
        UnitOfMeasure.Meter => 0.001,
        UnitOfMeasure.Inch => 1.0 / 25.4,
        _ => 1.0
    };

    public string UnitSuffix => Units switch
    {
        UnitOfMeasure.Centimeter => "cm",
        UnitOfMeasure.Meter => "m",
        UnitOfMeasure.Inch => "in",
        _ => "mm"
    };

    /// <summary>Длина из миллиметров модели в текст для подписи.</summary>
    public string FormatLength(double millimetres, bool withSuffix = false)
    {
        double v = millimetres * UnitFactor;
        string s = v.ToString("F" + LengthDecimals, System.Globalization.CultureInfo.InvariantCulture);
        return withSuffix ? s + " " + UnitSuffix : s;
    }

    public string FormatAngle(double degrees) =>
        degrees.ToString("F" + AngleDecimals, System.Globalization.CultureInfo.InvariantCulture) + "\u00b0";

    // ==================================================== фон

    public Vector4 BackgroundTop = new(0.16f, 0.18f, 0.22f, 1f);
    public Vector4 BackgroundBottom = new(0.07f, 0.08f, 0.10f, 1f);

    // ==================================================== состояния

    /// <summary>Цвет объекта под курсором. Общий для линий, точек и граней.</summary>
    public Vector4 HoverColor = new(1.00f, 0.72f, 0.35f, 1f);
    /// <summary>Цвет выбранного объекта.</summary>
    public Vector4 SelectColor = new(0.35f, 0.70f, 1.00f, 1f);
    /// <summary>Насколько толще становится линия под курсором и при выборе, пикселей.</summary>
    public float HighlightWidthBonus = 1.4f;
    /// <summary>Насколько крупнее становится точка под курсором и при выборе, пикселей.</summary>
    public float HighlightSizeBonus = 3.0f;

    // ==================================================== трёхмерный вид

    public Vector4 EdgeColor = new(0.10f, 0.11f, 0.13f, 1f);
    public float EdgeWidth = 1.6f;

    /// <summary>Рёбра за поверхностью в полупрозрачном режиме.</summary>
    public Vector4 HiddenEdgeColor = new(0.55f, 0.58f, 0.64f, 0.85f);
    public float HiddenEdgeWidth = 1.1f;

    public Vector4 VertexColor = new(0.15f, 0.16f, 0.18f, 1f);
    public float VertexSize = 6f;

    /// <summary>Непрозрачность тел в режиме Transparent.</summary>
    public float TransparentOpacity = 0.30f;

    public Vector4 DatumPlaneColor = new(0.45f, 0.58f, 0.80f, 0.14f);
    public Vector4 DatumBorderColor = new(0.45f, 0.62f, 0.90f, 0.85f);
    public float DatumBorderWidth = 1.2f;
    public Vector4 DatumAxisColor = new(0.55f, 0.70f, 0.95f, 0.80f);
    public float DatumAxisWidth = 1.2f;
    public Vector4 OriginColor = new(0.95f, 0.85f, 0.25f, 1f);
    public float OriginSize = 9f;

    // ==================================================== эскиз

    public Vector4 SketchObjectColor = new(1.00f, 0.85f, 0.20f, 1f);
    public float SketchObjectWidth = 1.8f;

    public Vector4 SketchConstructionColor = new(0.55f, 0.70f, 0.95f, 0.9f);
    public float SketchConstructionWidth = 1.1f;

    public Vector4 SketchCenterlineColor = new(0.95f, 0.55f, 0.85f, 0.9f);
    public float SketchCenterlineWidth = 1.1f;

    public Vector4 SketchReferenceColor = new(0.60f, 0.85f, 0.95f, 0.8f);
    public float SketchReferenceWidth = 1.1f;

    /// <summary>Полупрозрачная заливка замкнутых областей эскиза.</summary>
    public Vector4 SketchFillColor = new(0.62f, 0.36f, 0.86f, 0.22f);

    /// <summary>Размер обычного узла эскиза, пикселей.</summary>
    public float SketchPointSize = 7f;
    /// <summary>Размер конца отрезка или дуги.</summary>
    public float SketchEndpointSize = 7f;
    /// <summary>Размер отметки центра окружности, дуги и эллипса.</summary>
    public float SketchCenterSize = 9f;

    public Vector4 SketchPointColor = new(1.00f, 0.85f, 0.20f, 1f);
    public Vector4 SketchCenterColor = new(1.00f, 0.55f, 0.85f, 1f);

    /// <summary>Радиус захвата мышью, пикселей.</summary>
    public float PickTolerancePx = 7f;
    /// <summary>Радиус привязки к существующим точкам, пикселей.</summary>
    public float SnapTolerancePx = 8f;

    // ==================================================== сетка

    public Vector4 GridMinorColor = new(1f, 1f, 1f, 0.55f);
    public float GridMinorWidth = 0.6f;
    public Vector4 GridMajorColor = new(1f, 1f, 1f, 0.75f);
    public float GridMajorWidth = 1.2f;
    public Vector4 GridAxisXColor = new(0.85f, 0.30f, 0.30f, 0.85f);
    public Vector4 GridAxisYColor = new(0.35f, 0.75f, 0.35f, 0.85f);
    public float GridAxisWidth = 1.5f;

    /// <summary>Экранный шаг мелкой клетки, ниже которого сетка меняет масштаб.</summary>
    public float GridMinCellPx = 6f;

    // ==================================================== размеры

    public Vector4 DimensionColor = new(0.35f, 0.85f, 0.45f, 1f);
    public Vector4 DimensionLockedColor = new(0.35f, 0.75f, 1.00f, 1f);
    public Vector4 DimensionReferenceColor = new(0.72f, 0.45f, 0.95f, 1f);
    public float DimensionLineWidth = 1.2f;

    /// <summary>
    /// Длина наконечника стрелки. Ширина получается делением на
    /// ArrowLengthToWidth, поэтому пропорция сохраняется при любой длине.
    /// </summary>
    public float ArrowLengthPx = 12f;
    /// <summary>Отношение длины наконечника к его ширине. По чертёжной норме — 3:1.</summary>
    public float ArrowLengthToWidth = 3f;

    // ---------------------------------------------------------- шрифты

    /// <summary>
    /// ЧЕРТЁЖНЫЙ ШРИФТ — тот, которым подписаны размеры на эскизе.
    ///
    /// Он векторный: каждый знак составлен из ломаных (см. Render/StrokeFont).
    /// Поэтому у него нет ни начертания, ни гарнитуры — вместо них высота
    /// прописного знака, ширина и толщина штриха. Зато он не зависит от
    /// шрифтов системы и одинаково выглядит на любой машине.
    /// </summary>
    /// <summary>
    /// Зазор между острием стрелки и выносной линией. Вплотную остриё
    /// читается как продолжение выносной линии, а не как стрелка.
    /// </summary>
    public float ArrowTipGapPx = 2.5f;

    public float DimensionTextHeightPx = 12f;

    /// <summary>Сжатие или растяжение знаков по ширине. 1 — как нарисовано.</summary>
    public float DimensionTextWidthScale = 1.0f;

    /// <summary>Межбуквенный просвет. 1 — обычный, больше — разрядка.</summary>
    public float DimensionTextSpacing = 1.0f;

    /// <summary>Толщина штриха цифр. Задаётся отдельно от размерных линий.</summary>
    public float DimensionTextStrokeWidth = 1.1f;

    /// <summary>Показывать единицы измерения в подписи размера.</summary>
    public bool DimensionShowUnits = false;
    /// <summary>Просвет между измеряемой геометрией и началом выносной линии.</summary>
    public float DimensionGapPx = 4f;
    /// <summary>Насколько выносная линия выходит за размерную.</summary>
    public float DimensionOverrunPx = 5f;

    // ==================================================== автопривязка

    /// <summary>
    /// Вешать очевидные ограничения на новые элементы эскиза:
    /// горизонтальность, вертикальность, параллельность, совпадение
    /// концов, точку на кривой.
    /// </summary>
    public bool AutoConstrainEnabled = true;

    /// <summary>
    /// Насколько линия может отклоняться от горизонтали, вертикали или
    /// от направления соседней, чтобы считаться «такой же», градусов.
    /// </summary>
    public float AutoConstrainAngleTolDeg = 2.5f;

    /// <summary>
    /// Координировать новый элемент размерами от нуля эскиза, если
    /// привязаться было не к чему.
    ///
    /// Так новая геометрия не висит в воздухе. Обратная сторона — на
    /// каждый несвязанный элемент приходится два размера, поэтому
    /// координирование включается только когда ограничений не нашлось.
    /// </summary>
    public bool AutoDimensionNewEntities = true;

    /// <summary>
    /// Ставить размер на саму фигуру при её создании: длину отрезка,
    /// радиус окружности и дуги, обе полуоси эллипса.
    /// </summary>
    public bool AutoDimensionSize = true;

    /// <summary>Окружности размеряются диаметром, а не радиусом.</summary>
    public bool PreferDiameter = false;

    // ==================================================== значки ограничений

    /// <summary>Показывать значки ограничений на эскизе.</summary>
    public bool ShowConstraintGlyphs = true;

    public Vector4 ConstraintGlyphColor = new(0.55f, 0.85f, 1.00f, 0.95f);
    public float ConstraintGlyphWidth = 1.1f;

    /// <summary>Сторона клетки значка, пикселей.</summary>
    public float ConstraintGlyphSizePx = 10f;
    /// <summary>Просвет между соседними значками в строке.</summary>
    public float ConstraintGlyphSpacingPx = 4f;
    /// <summary>Вынос строки значков от линии или узла.</summary>
    public float ConstraintGlyphOffsetPx = 16f;

    /// <summary>
    /// Цвет конфликтующих ограничений и размеров. Они исключены
    /// из решателя, и это видно сразу.
    /// </summary>
    public Vector4 ConflictColor = new(0.95f, 0.30f, 0.28f, 1f);

    /// <summary>
    /// Цвет элементов эскиза, по которым нельзя построить тело:
    /// пересекающихся и с висящими концами.
    /// </summary>
    public Vector4 SketchErrorColor = new(0.95f, 0.30f, 0.28f, 1f);

    /// <summary>Подсвечивать такие элементы красным.</summary>
    public bool HighlightSketchErrors = true;

    /// <summary>
    /// Показывать размеры эскизов вне режима эскиза.
    ///
    /// По умолчанию выключено: в трёхмерном виде размеры всех эскизов
    /// детали загромождают экран и мешают смотреть на тело. Когда включено,
    /// показываются размеры ВЫБРАННОГО эскиза.
    /// </summary>
    public bool ShowSelectedItemDimensions = false;

    /// <summary>
    /// В режиме черчения скрывать всё, кроме активного эскиза: тела,
    /// базовые элементы и чужие эскизы.
    /// </summary>
    public bool HideOthersInSketch = true;

    /// <summary>
    /// Подсвечивать проекции чужой геометрии на плоскость эскиза
    /// и позволять переносить их щелчком как ссылочные.
    /// </summary>
    public bool ShowProjections = true;

    /// <summary>
    /// Показывать стрелку направления взгляда у выбранного эскиза.
    /// Щелчок по ней переворачивает эскиз.
    /// </summary>
    public bool ShowSketchViewArrow = true;

    public Vector4 SketchViewArrowColor = new(1.00f, 0.85f, 0.20f, 1f);
    public float SketchViewArrowWidth = 3.0f;
    /// <summary>Длина стрелки в пикселях экрана.</summary>
    public float SketchViewArrowPx = 50f;

    /// <summary>
    /// Зона у нулевой точки, где стрелка щелчок НЕ перехватывает:
    /// там стоит сама точка, и выбирать надо её.
    /// </summary>
    public float SketchViewArrowOriginGapPx = 12f;

    /// <summary>Цвет размеров, поставленных приложением по умолчанию.</summary>
    public Vector4 DefaultDimensionColor = new(0.62f, 0.65f, 0.70f, 0.95f);

    /// <summary>
    /// Достраивать эскиз размерами по умолчанию, пока он не определён
    /// полностью.
    /// </summary>
    public bool ShowDefaultDimensions = true;

    // ==================================================== шрифты интерфейса

    /// <summary>
    /// ШРИФТ ИНТЕРФЕЙСА — обычный системный, для дерева, панелей и строки
    /// состояния. С чертёжным он не связан: тот рисуется в кадре OpenGL,
    /// этот — средствами Windows.
    /// </summary>
    public string UiFontFamily = "Segoe UI";
    public float UiFontSize = 9f;

    /// <summary>Шрифт дерева проекта. Пустая гарнитура — берётся UiFontFamily.</summary>
    public string TreeFontFamily = "";
    public float TreeFontSize = 9f;

    /// <summary>Шрифт строки состояния.</summary>
    public string StatusFontFamily = "";
    public float StatusFontSize = 9f;
    /// <summary>Название текущего режима выделяется полужирным.</summary>
    public bool StatusModeBold = true;

    /// <summary>Собирает шрифт по заданной гарнитуре с откатом на общую.</summary>
    public Font MakeFont(string family, float size, FontStyle style = FontStyle.Regular)
    {
        string name = string.IsNullOrWhiteSpace(family) ? UiFontFamily : family;
        try { return new Font(name, size, style); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style); }
    }

    public Font TreeFont() => MakeFont(TreeFontFamily, TreeFontSize);
    public Font StatusFont() => MakeFont(StatusFontFamily, StatusFontSize);
    public Font StatusModeFont() =>
        MakeFont(StatusFontFamily, StatusFontSize, StatusModeBold ? FontStyle.Bold : FontStyle.Regular);

    // ==================================================== дерево проекта

    public Color TreeBackColor = Color.FromArgb(250, 250, 252);
    public Color TreeLinesColor = Color.FromArgb(225, 227, 232);
    public Color TreeHeaderBackColor = Color.FromArgb(238, 240, 245);
    public Color TreeTextColor = Color.FromArgb(28, 30, 34);
    public Color TreeDimTextColor = Color.FromArgb(140, 144, 152);
    public Color TreeErrorColor = Color.FromArgb(190, 55, 45);
    public Color TreeHoverBackColor = Color.FromArgb(232, 240, 252);
    public Color TreeSelectBackColor = Color.FromArgb(205, 226, 250);
    public int TreeNodeHeight = 22;
    public int TreeIconSize = 16;

    /// <summary>Полная копия — на случай отката настроек или сравнения.</summary>
    public Settings Clone() => (Settings)MemberwiseClone();
}
