using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Render;

/// <summary>Индекс стиля линии. Значение = индекс в таблице униформов шейдера.</summary>
public enum LineStyle
{
    Edge = 0,               // видимое ребро тела
    EdgeHidden = 1,         // ребро за телом (штрих)
    SketchObject = 2,       // основная линия эскиза
    SketchConstruction = 3, // вспомогательная (тонкий штрих)
    SketchCenterline = 4,   // осевая (штрих-пунктир)
    SketchReference = 5,    // спроецированная референсная геометрия
    Dimension = 6,          // выноски и размерные линии
    DatumBorder = 7,        // рамка базовой плоскости
    DatumAxis = 8,          // базовая ось (штрих-пунктир)
    GridMinor = 9,
    GridMajor = 10,
    GridAxisX = 11,
    GridAxisY = 12,
    Preview = 13,           // «резиновая» линия при построении
    Overlay = 14,           // экранная графика поверх всего
    Gizmo = 15,

    // Размеры. Цвет говорит о состоянии значения:
    // зелёный — обычный, голубой — заблокированный, фиолетовый — справочный.
    DimensionLocked = 16,
    DimensionReference = 17,

    // Цифры размеров. Отдельные стили нужны потому, что толщина штриха
    // текста настраивается независимо от толщины размерных линий.
    DimensionText = 18,
    DimensionTextLocked = 19,
    DimensionTextReference = 20,

    /// <summary>Значки ограничений на эскизе.</summary>
    ConstraintGlyph = 21,

    // Красным рисуется всё, что исключено из решателя из-за конфликта.
    ConstraintConflict = 22,
    DimensionConflict = 23,
    DimensionTextConflict = 24,

    // Размеры, поставленные приложением: серые, но полноценные.
    DimensionDefault = 25,
    DimensionTextDefault = 26,

    /// <summary>Элемент эскиза, по которому нельзя построить тело.</summary>
    SketchError = 27,

    /// <summary>Стрелка, показывающая направление взгляда эскиза.</summary>
    SketchViewArrow = 28,
    SketchViewArrowHover = 29,

    /// <summary>Рёбра треугольников в виде Mesh.</summary>
    MeshWire = 30,
    /// <summary>
    /// Рамка плоскости и ось ЧУЖОГО контекста (детали — в режиме сборки, сборки и других
    /// деталей — в режиме детали): бледнее своих. Последний свободный слот из 32, поэтому
    /// один стиль на рамки и оси.
    /// </summary>
    DatumDim = 31
}

/// <summary>Индекс стиля точки.</summary>
public enum PointStyle
{
    SolidVertex = 0,        // вершина тела
    SketchPoint = 1,        // точка эскиза
    SketchEndpoint = 2,     // конец отрезка
    SketchCenter = 3,       // центр окружности (крест)
    Reference = 4,
    Origin = 5,
    Preview = 6,
    Snap = 7,
    /// <summary>Свободный конец контура.</summary>
    Error = 8,
    /// <summary>Точка начала чужого контекста — бледнее своей.</summary>
    OriginDim = 9
}

/// <summary>
/// Таблица стилей. Загружается в шейдер одним набором униформов,
/// после чего ЛЮБАЯ линия проекта рисуется той же программой — меняется
/// только индекс стиля в атрибуте инстанса.
///
/// Именно это заменяет старую схему «у каждой линии свой ShaderProgram и
/// свой VAO», из-за которой эскиз на 300 отрезков компилировал 300 программ.
/// </summary>
public sealed class RenderStyles
{
    public const int N = Shaders.MaxStyles;

    public readonly float[] LineWidth = new float[N];
    public readonly float[] LineWidthHover = new float[N];
    public readonly float[] LineWidthSel = new float[N];
    public readonly float[] LineColor = new float[N * 4];
    public readonly float[] LineColorHover = new float[N * 4];
    public readonly float[] LineColorSel = new float[N * 4];
    public readonly int[] LinePattern = new int[N];
    public readonly float[] LinePatternPx = new float[N];

    public readonly float[] PointSize = new float[N];
    public readonly float[] PointSizeHover = new float[N];
    public readonly float[] PointSizeSel = new float[N];
    public readonly float[] PointColor = new float[N * 4];
    public readonly float[] PointColorHover = new float[N * 4];
    public readonly float[] PointColorSel = new float[N * 4];
    public readonly int[] PointShape = new int[N];

    public Vector4 BackgroundTop = new(0.16f, 0.18f, 0.21f, 1f);
    public Vector4 BackgroundBottom = new(0.07f, 0.08f, 0.10f, 1f);

    public Vector4 BodyColor = new(0.72f, 0.74f, 0.78f, 1f);
    public Vector4 BodyHover = new(0.85f, 0.88f, 0.95f, 1f);
    public Vector4 BodySelect = new(0.35f, 0.62f, 0.95f, 1f);
    public Vector4 DatumColor = new(0.45f, 0.58f, 0.80f, 0.16f);
    public Vector4 DatumHover = new(0.55f, 0.70f, 0.95f, 0.30f);
    public Vector4 DatumSelect = new(0.35f, 0.75f, 0.95f, 0.40f);

    // Маски штриха: 16 бит на период, бит 0 — начало периода, 1 = штрих.
    private const int Solid = 0xFFFF;
    private const int Dash = 0b0000_0111_1111_1111;   // ~ 11/16 штрих
    private const int FineDash = 0b0011_0011_0011_0011;   // мелкий пунктир
    private const int DashDot = 0b0001_0000_0011_1111;   // штрих-пунктир
    private const int Phantom = 0b0100_0100_0011_1111;   // штрих-два-пунктира

    public RenderStyles()
    {
        Reload();
        Settings.Changed += Reload;
    }

    /// <summary>
    /// Пересобирает таблицу из Core/Settings.
    ///
    /// Раньше цвета и толщины были вписаны сюда числами. Теперь здесь
    /// остались только штриховые маски и формы точек: они описывают
    /// НАЗНАЧЕНИЕ стиля, а не вкус, и настройками не управляются.
    /// </summary>
    public void Reload()
    {
        var c = Settings.Current;

        BackgroundTop = c.BackgroundTop;
        BackgroundBottom = c.BackgroundBottom;

        BodyColor = new Vector4(0.72f, 0.74f, 0.78f, 1f);   // перекрывается материалом детали
        BodyHover = c.HoverColor;
        BodySelect = c.SelectColor;
        DatumColor = c.DatumPlaneColor;
        DatumHover = new Vector4(c.HoverColor.Xyz, Math.Max(c.DatumPlaneColor.W * 2f, 0.25f));
        DatumSelect = new Vector4(c.SelectColor.Xyz, Math.Max(c.DatumPlaneColor.W * 2.5f, 0.30f));

        SetLine(LineStyle.Edge, c.EdgeColor, c.EdgeWidth, Solid, 0);
        SetLine(LineStyle.EdgeHidden, c.HiddenEdgeColor, c.HiddenEdgeWidth, Dash, 9);

        SetLine(LineStyle.SketchObject, c.SketchObjectColor, c.SketchObjectWidth, Solid, 0);
        SetLine(LineStyle.SketchConstruction, c.SketchConstructionColor, c.SketchConstructionWidth, FineDash, 8);
        SetLine(LineStyle.SketchCenterline, c.SketchCenterlineColor, c.SketchCenterlineWidth, DashDot, 22);
        SetLine(LineStyle.SketchReference, c.SketchReferenceColor, c.SketchReferenceWidth, Dash, 12);

        SetLine(LineStyle.Dimension, c.DimensionColor, c.DimensionLineWidth, Solid, 0);
        SetLine(LineStyle.DimensionLocked, c.DimensionLockedColor, c.DimensionLineWidth, Solid, 0);
        SetLine(LineStyle.DimensionReference, c.DimensionReferenceColor, c.DimensionLineWidth, Solid, 0);

        SetLine(LineStyle.DimensionText, c.DimensionColor, c.DimensionTextStrokeWidth, Solid, 0);
        SetLine(LineStyle.DimensionTextLocked, c.DimensionLockedColor, c.DimensionTextStrokeWidth, Solid, 0);
        SetLine(LineStyle.DimensionTextReference, c.DimensionReferenceColor, c.DimensionTextStrokeWidth, Solid, 0);

        SetLine(LineStyle.SketchError, c.SketchErrorColor, c.SketchObjectWidth + 0.4f, Solid, 0);
        SetLine(LineStyle.SketchViewArrow, c.SketchViewArrowColor, c.SketchViewArrowWidth, Solid, 0);
        SetLine(LineStyle.SketchViewArrowHover, c.HoverColor, c.SketchViewArrowWidth + 1.0f, Solid, 0);
        SetLine(LineStyle.ConstraintGlyph, c.ConstraintGlyphColor, c.ConstraintGlyphWidth, Solid, 0);
        SetLine(LineStyle.ConstraintConflict, c.ConflictColor, c.ConstraintGlyphWidth + 0.3f, Solid, 0);
        SetLine(LineStyle.DimensionConflict, c.ConflictColor, c.DimensionLineWidth, Solid, 0);
        SetLine(LineStyle.DimensionDefault, c.DefaultDimensionColor, c.DimensionLineWidth, Solid, 0);
        SetLine(LineStyle.DimensionTextDefault, c.DefaultDimensionColor, c.DimensionTextStrokeWidth, Solid, 0);
        SetLine(LineStyle.DimensionTextConflict, c.ConflictColor, c.DimensionTextStrokeWidth, Solid, 0);

        SetLine(LineStyle.DatumBorder, c.DatumBorderColor, c.DatumBorderWidth, Solid, 0);
        SetLine(LineStyle.DatumAxis, c.DatumAxisColor, c.DatumAxisWidth, DashDot, 22);
        SetLine(LineStyle.DatumDim, Dim(c.DatumBorderColor), c.DatumBorderWidth, Solid, 0);

        SetLine(LineStyle.GridMinor, c.GridMinorColor, c.GridMinorWidth, Solid, 0);
        SetLine(LineStyle.GridMajor, c.GridMajorColor, c.GridMajorWidth, Solid, 0);
        SetLine(LineStyle.GridAxisX, c.GridAxisXColor, c.GridAxisWidth, Solid, 0);
        SetLine(LineStyle.GridAxisY, c.GridAxisYColor, c.GridAxisWidth, Solid, 0);

        SetLine(LineStyle.Preview, new Vector4(1f, 1f, 1f, 0.85f), 1.4f, Phantom, 14);
        SetLine(LineStyle.Overlay, new Vector4(0.90f, 0.92f, 0.95f, 1f), 1.4f, Solid, 0);
        SetLine(LineStyle.Gizmo, new Vector4(1f, 1f, 1f, 1f), 2.4f, Solid, 0);

        // Сетка — тонкая и неяркая: треугольники не должны спорить с рёбрами модели.
        SetLine(LineStyle.MeshWire, new Vector4(0.62f, 0.66f, 0.72f, 0.55f), 1.0f, Solid, 0);

        // Форма точки — последний аргумент: 0 заливка, 1 окружность,
        // 2 крест, 3 квадрат, 4 ромб.
        SetPoint(PointStyle.SolidVertex, c.VertexColor, c.VertexSize, 0);
        SetPoint(PointStyle.SketchPoint, c.SketchPointColor, c.SketchPointSize, 0);
        SetPoint(PointStyle.SketchEndpoint, c.SketchPointColor, c.SketchEndpointSize, 1);
        SetPoint(PointStyle.SketchCenter, c.SketchCenterColor, c.SketchCenterSize, 2);
        SetPoint(PointStyle.Reference, c.SketchReferenceColor, c.SketchEndpointSize, 1);
        SetPoint(PointStyle.Origin, c.OriginColor, c.OriginSize, 4);
        SetPoint(PointStyle.OriginDim, Dim(c.OriginColor), c.OriginSize, 4);
        SetPoint(PointStyle.Preview, new Vector4(1f, 1f, 1f, 0.9f), c.SketchPointSize, 1);
        SetPoint(PointStyle.Error, c.SketchErrorColor, c.SketchEndpointSize + 2f, 1);
        SetPoint(PointStyle.Snap, new Vector4(0.40f, 0.95f, 1.00f, 1f), c.SketchCenterSize + 2f, 3);
    }

    /// <summary>Бледный вариант цвета для элементов чужого контекста.</summary>
    private static Vector4 Dim(Vector4 c) => new(c.X, c.Y, c.Z, c.W * 0.35f);

    public void SetLine(LineStyle s, Vector4 color, float width, int pattern, float periodPx)
    {
        int i = (int)s;
        var c = Settings.Current;
        LineWidth[i] = width;
        LineWidthHover[i] = width + c.HighlightWidthBonus;
        LineWidthSel[i] = width + c.HighlightWidthBonus;
        LinePattern[i] = pattern;
        LinePatternPx[i] = periodPx;
        Put(LineColor, i, color);

        // Прозрачность берётся у самого стиля: подсветка не должна делать
        // непрозрачной линию, которая задумана полупрозрачной.
        Put(LineColorHover, i, new Vector4(c.HoverColor.Xyz, color.W));
        Put(LineColorSel, i, new Vector4(c.SelectColor.Xyz, color.W));
    }

    /// <summary>
    /// Меняет только прозрачность базового цвета стиля.
    ///
    /// Нужно сетке: её мелкие линии растворяются по мере отдаления, и
    /// прозрачность должна попасть именно в тот массив uColor, который
    /// заведомо доходит до шейдера. Через общий множитель непрозрачности
    /// пакета этого добиться не удалось.
    /// </summary>
    public void SetLineAlpha(LineStyle s, float alpha)
    {
        LineColor[(int)s * 4 + 3] = alpha;
    }

    public float GetLineAlpha(LineStyle s) => LineColor[(int)s * 4 + 3];

    /// <summary>Полностью заменяет базовый цвет стиля линии.</summary>
    public void SetLineColor(LineStyle s, Vector4 color) => Put(LineColor, (int)s, color);

    public Vector4 GetLineColor(LineStyle s)
    {
        int i = (int)s * 4;
        return new Vector4(LineColor[i], LineColor[i + 1], LineColor[i + 2], LineColor[i + 3]);
    }

    public void SetPoint(PointStyle s, Vector4 color, float size, int shape)
    {
        var c = Settings.Current;
        int i = (int)s;
        PointSize[i] = size;
        PointSizeHover[i] = size + c.HighlightSizeBonus;
        PointSizeSel[i] = size + c.HighlightSizeBonus;
        PointShape[i] = shape;
        Put(PointColor, i, color);
        Put(PointColorHover, i, new Vector4(c.HoverColor.Xyz, 1f));
        Put(PointColorSel, i, new Vector4(c.SelectColor.Xyz, 1f));
    }

    private static void Put(float[] a, int i, Vector4 c)
    {
        a[i * 4 + 0] = c.X; a[i * 4 + 1] = c.Y; a[i * 4 + 2] = c.Z; a[i * 4 + 3] = c.W;
    }

    public void UploadLine(ShaderProgram p)
    {
        p.SetArray("uWidth", LineWidth);
        p.SetArray("uWidthHover", LineWidthHover);
        p.SetArray("uWidthSel", LineWidthSel);
        p.SetArray4("uColor", LineColor);
        p.SetArray4("uColorHover", LineColorHover);
        p.SetArray4("uColorSel", LineColorSel);
        p.SetArray("uPattern", LinePattern);
        p.SetArray("uPatternPx", LinePatternPx);
    }

    public void UploadPoint(ShaderProgram p)
    {
        p.SetArray("uSize", PointSize);
        p.SetArray("uSizeHover", PointSizeHover);
        p.SetArray("uSizeSel", PointSizeSel);
        p.SetArray4("uColor", PointColor);
        p.SetArray4("uColorHover", PointColorHover);
        p.SetArray4("uColorSel", PointColorSel);
        p.SetArray("uShape", PointShape);
    }
}
