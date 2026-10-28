using Avalonia.Media;

namespace Parametric3D.App.Services;

/// <summary>
/// ВИЗУАЛЬНЫЕ НАСТРОЙКИ ПРИЛОЖЕНИЯ (вкладка «Вид» настроек): размеры текста и кнопок, цвета вида и эскиза, размер подписей
/// размеров и значков связей. Хранятся в настройках пользователя (UserPrefs.Look, файл ui.json) — общие для всех документов;
/// применяются при запуске, при открытии документа и по «ОК» настроек.
/// </summary>
public sealed class LookPrefs
{
    /// <summary>Размер шрифта интерфейса (меню, панели, дерево).</summary>
    public double UiFontSize { get; set; } = 13;
    /// <summary>Кнопка полосы у стороны вида (квадрат), пикселей; все полосы — одной толщины.</summary>
    public double StripButtonPx { get; set; } = 34;
    /// <summary>Иконка на кнопке полосы.</summary>
    public double StripIconPx { get; set; } = 20;
    /// <summary>Иконка на кнопках панелей с подписями.</summary>
    public double PanelIconPx { get; set; } = 20;
    /// <summary>Высота цифр размеров эскиза, пикселей.</summary>
    public double DimensionTextPx { get; set; } = 12;
    /// <summary>Толщина линии цифр размеров (контуры шрифта ГОСТ).</summary>
    public double DimensionTextLine { get; set; } = 0.75;
    /// <summary>Значки связей на эскизе, пикселей.</summary>
    public double ConstraintIconPx { get; set; } = 16;
    /// <summary>Цвет линий эскиза (тёмная тема); пусто — по теме.</summary>
    public string SketchColorDark { get; set; } = "";
    /// <summary>Цвет линий эскиза (светлая тема); пусто — по теме.</summary>
    public string SketchColorLight { get; set; } = "";
    /// <summary>Фон вида сверху и снизу (тёмная тема); пусто — по теме.</summary>
    public string BackgroundTopDark { get; set; } = "";
    public string BackgroundBottomDark { get; set; } = "";
    /// <summary>Фон вида сверху и снизу (светлая тема); пусто — по теме.</summary>
    public string BackgroundTopLight { get; set; } = "";
    public string BackgroundBottomLight { get; set; } = "";
    /// <summary>k103: штриховать сечение деталей и сборок плоскостью сечения.</summary>
    public bool SectionHatch { get; set; } = true;
    /// <summary>k103: цвет штриховки и заливки сечения; пусто — по материалу детали.</summary>
    public string SectionHatchColor { get; set; } = "";
    public string SectionFillColor { get; set; } = "";

    // ---------------------------------------------------------------- k132 (замечание 28.10, 23:42, пп. 8–10): вид модели
    /// <summary>Пол под деталью, тень на полу, отражение на полу; зазор между полом и нижней точкой модели (мм).</summary>
    public bool FloorOn { get; set; } = true;
    public bool FloorShadow { get; set; } = true;
    public bool FloorReflection { get; set; } = true;
    public double FloorGapMm { get; set; } = 0;
    /// <summary>Доля отражения, %.</summary>
    public double FloorReflectPct { get; set; } = 25;
    /// <summary>Свет 1 и 2 в осях вида (X вправо, Y вверх, Z на зрителя): положение, направление (0;0;0 — на центр), цвет, сила.</summary>
    public double Light1X { get; set; } = -0.45;
    public double Light1Y { get; set; } = 0.65;
    public double Light1Z { get; set; } = 0.62;
    public double Light1DX { get; set; }
    public double Light1DY { get; set; }
    public double Light1DZ { get; set; }
    public string Light1Color { get; set; } = "#FFFAF2";
    public double Light1Power { get; set; } = 0.80;
    public double Light2X { get; set; } = 0.75;
    public double Light2Y { get; set; } = 0.10;
    public double Light2Z { get; set; } = 0.45;
    public double Light2DX { get; set; }
    public double Light2DY { get; set; }
    public double Light2DZ { get; set; }
    public string Light2Color { get; set; } = "#D9E6FF";
    public double Light2Power { get; set; } = 0.28;
    /// <summary>Рассеянный свет (Ambient): цвет и сила.</summary>
    public string AmbientColor { get; set; } = "#DEE9FF";
    public double AmbientPower { get; set; } = 0.46;
    /// <summary>Опорные плоскости: цвет (по теме; пусто — по умолчанию), непрозрачность %, контур.</summary>
    public string DatumPlaneColorDark { get; set; } = "";
    public string DatumPlaneColorLight { get; set; } = "";
    public double DatumPlaneOpacityPct { get; set; } = 9;
    public bool DatumPlaneOutline { get; set; } = true;
    /// <summary>Размер куба вида вместе с кнопкой «Домой», пикселей.</summary>
    public double ViewCubePx { get; set; } = 150;
    /// <summary>Цвет пола (по теме; пусто — по умолчанию); толщина рёбер модели.</summary>
    public string FloorColorDark { get; set; } = "";
    public string FloorColorLight { get; set; } = "";
    public double ModelEdgeWidth { get; set; } = 1.5;

    // ---------------------------------------------------------------- k132 (п. 12): разделы как в QymCAD
    /// <summary>Дерево модели: текст (0 — как интерфейс), значки, высота строки, ширина панели.</summary>
    public double TreeFontSize { get; set; } = 0;
    public double TreeIconPx { get; set; } = 16;
    public double TreeRowPx { get; set; } = 24;
    public double TreePanelPx { get; set; } = 270;
    /// <summary>Панель свойств: ширина, текст (0 — как интерфейс).</summary>
    public double PropsPanelPx { get; set; } = 300;
    public double PropsFontSize { get; set; } = 0;
    /// <summary>Управление операциями (стрелка-ручка): длина стрелки и шарик, пикселей; толщина линии; цвета (по теме).</summary>
    public double HandleLengthPx { get; set; } = 70;
    public double HandleKnobPx { get; set; } = 13;
    public double HandleLineWidth { get; set; } = 1.6;
    public string HandleColorDark { get; set; } = "";
    public string HandleColorLight { get; set; } = "";
    public string HandleAltColorDark { get; set; } = "";
    public string HandleAltColorLight { get; set; } = "";
    /// <summary>Эскиз: толщина линий, размер точек, автосвязи, повтор инструмента.</summary>
    public double SketchLineWidth { get; set; } = 1.8;
    public double SketchPointPx { get; set; } = 6;
    public bool SketchAutoConstrain { get; set; } = true;
    public bool SketchToolRepeat { get; set; } = false;

    public LookPrefs Clone() => (LookPrefs)MemberwiseClone();

    /// <summary>Сбросить настройки вида модели (раздел «Вид модели») к значениям по умолчанию.</summary>
    public void ResetModelView()
    {
        var d = new LookPrefs();
        foreach (var p in typeof(LookPrefs).GetProperties())
            if (ModelViewKeys.Contains(p.Name)) p.SetValue(this, p.GetValue(d));
    }
    public static readonly HashSet<string> ModelViewKeys = new()
    {
        nameof(FloorOn), nameof(FloorShadow), nameof(FloorReflection), nameof(FloorGapMm), nameof(FloorReflectPct),
        nameof(Light1X), nameof(Light1Y), nameof(Light1Z), nameof(Light1DX), nameof(Light1DY), nameof(Light1DZ), nameof(Light1Color), nameof(Light1Power),
        nameof(Light2X), nameof(Light2Y), nameof(Light2Z), nameof(Light2DX), nameof(Light2DY), nameof(Light2DZ), nameof(Light2Color), nameof(Light2Power),
        nameof(AmbientColor), nameof(AmbientPower), nameof(DatumPlaneColorDark), nameof(DatumPlaneColorLight), nameof(DatumPlaneOpacityPct),
        nameof(DatumPlaneOutline), nameof(ViewCubePx), nameof(FloorColorDark), nameof(FloorColorLight), nameof(ModelEdgeWidth),
    };
}

/// <summary>Действующие визуальные настройки (чтение — отовсюду, где строятся панели и вид).</summary>
public static class UiLook
{
    static LookPrefs L => UserPrefs.Current.Look;
    public static double StripButtonPx => Math.Clamp(L.StripButtonPx, 22, 64);
    public static double StripIconPx => Math.Clamp(L.StripIconPx, 12, StripButtonPx - 6);
    public static double PanelIconPx => Math.Clamp(L.PanelIconPx, 12, 40);
    public static double UiFontSize => Math.Clamp(L.UiFontSize, 9, 22);
    public static double ViewCubePx => Math.Clamp(L.ViewCubePx, 90, 300);
    public static readonly bool LightPrefsOff = Environment.GetEnvironmentVariable("P3D_LIGHTPREFS") == "0";

    /// <summary>
    /// k132: настройки «Вид модели» → отрисовка: пол (вкл., тень, отражение, зазор, доля отражения), два источника света
    /// (направление — заданное, а нулевое — из положения на центр; цвет × сила), рассеянный свет, цвет и непрозрачность
    /// опорных плоскостей (пустой цвет — по теме), их контур.
    /// </summary>
    public static void ApplyModelView(Parametric3D.Render.Engine.RenderTheme th, LookPrefs l)
    {
        static OpenTK.Mathematics.Vector3 C3(string s, OpenTK.Mathematics.Vector3 dflt)
            => ParseColor(s) is { } c ? new(c.R / 255f, c.G / 255f, c.B / 255f) : dflt;
        var d = th.Dark ? Parametric3D.Render.Engine.RenderTheme.DarkTheme() : Parametric3D.Render.Engine.RenderTheme.LightTheme();
        th.FloorOn = l.FloorOn; th.FloorShadow = l.FloorShadow; th.FloorReflection = l.FloorReflection;
        th.FloorGap = (float)Math.Clamp(l.FloorGapMm, 0, 1e5);
        th.FloorReflect = (float)Math.Clamp(l.FloorReflectPct / 100, 0, 0.7);
        OpenTK.Mathematics.Vector3 V3(double x, double y, double z) => new((float)x, (float)y, (float)z);
        void Light(double x, double y, double z, double dx, double dy, double dz, out OpenTK.Mathematics.Vector3 pos, out OpenTK.Mathematics.Vector3 target)
        {
            pos = V3(x, y, z);
            var dir = V3(dx, dy, dz);
            target = dir.LengthSquared > 1e-12f ? pos + dir : OpenTK.Mathematics.Vector3.Zero;
            if ((pos - target).LengthSquared < 1e-12f) { pos = new(0, 0, 1); target = OpenTK.Mathematics.Vector3.Zero; }
        }
        Light(l.Light1X, l.Light1Y, l.Light1Z, l.Light1DX, l.Light1DY, l.Light1DZ, out th.Light1Pos, out th.Light1Target);
        Light(l.Light2X, l.Light2Y, l.Light2Z, l.Light2DX, l.Light2DY, l.Light2DZ, out th.Light2Pos, out th.Light2Target);
        th.Light1Color = C3(l.Light1Color, d.Light1Color); th.Light1Power = (float)Math.Clamp(l.Light1Power, 0, 3);
        th.Light2Color = C3(l.Light2Color, d.Light2Color); th.Light2Power = (float)Math.Clamp(l.Light2Power, 0, 3);
        th.AmbientColor = C3(l.AmbientColor, d.AmbientColor); th.AmbientPower = (float)Math.Clamp(l.AmbientPower, 0, 3);
        var pc = C3(th.Dark ? l.DatumPlaneColorDark : l.DatumPlaneColorLight, d.DatumPlane.Xyz);
        th.DatumPlane = new(pc, (float)Math.Clamp(l.DatumPlaneOpacityPct / 100, 0, 1));
        th.DatumOutline = l.DatumPlaneOutline;
        var fl = C3(th.Dark ? l.FloorColorDark : l.FloorColorLight, d.FloorColor.Xyz);
        th.FloorColor = new(fl, d.FloorColor.W);
        th.EdgeWidth = (float)Math.Clamp(l.ModelEdgeWidth, 0.5, 5);
        th.Handle = new(C3(th.Dark ? l.HandleColorDark : l.HandleColorLight, d.Handle.Xyz), d.Handle.W);
        th.HandleAlt = new(C3(th.Dark ? l.HandleAltColorDark : l.HandleAltColorLight, d.HandleAlt.Xyz), d.HandleAlt.W);
        th.SketchWidth = (float)Math.Clamp(l.SketchLineWidth, 0.5, 6);
        th.SketchPointPx = (float)Math.Clamp(l.SketchPointPx, 3, 16);
        var cs = Core.Settings.Current;
        cs.AutoConstrainEnabled = l.SketchAutoConstrain; cs.SketchToolRepeat = l.SketchToolRepeat;
    }
    public static double TreeFontSize => L.TreeFontSize > 0 ? Math.Clamp(L.TreeFontSize, 9, 22) : UiFontSize;
    public static double TreeIconPx => Math.Clamp(L.TreeIconPx, 10, 32);
    public static double TreeRowPx => Math.Clamp(L.TreeRowPx, 16, 48);
    public static double TreePanelPx => Math.Clamp(L.TreePanelPx, 160, 600);
    public static double PropsPanelPx => Math.Clamp(L.PropsPanelPx, 220, 600);
    public static double PropsFontSize => L.PropsFontSize > 0 ? Math.Clamp(L.PropsFontSize, 9, 22) : UiFontSize;
    public static double HandleLengthPx => Math.Clamp(L.HandleLengthPx, 30, 200);
    public static double HandleKnobPx => Math.Clamp(L.HandleKnobPx, 6, 32);
    public static double HandleLineWidth => Math.Clamp(L.HandleLineWidth, 0.5, 5);

    /// <summary>Цвет из строки «#RRGGBB»; пусто или ошибка — null.</summary>
    public static Color? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Color.TryParse(s.Trim(), out var c) ? c : null;
    }

    /// <summary>Применить к настройкам отрисовки (размеры подписей, значков) и к цветам вида.</summary>
    public static void ApplyToRender(Parametric3D.Render.Engine.RenderTheme th)
    {
        var s = Core.Settings.Current;
        s.DimensionTextHeightPx = (float)Math.Clamp(L.DimensionTextPx, 6, 40);
        s.DimensionTextOutlineWidth = (float)Math.Clamp(L.DimensionTextLine, 0.3, 3);
        s.ConstraintIconSizePx = (float)Math.Clamp(L.ConstraintIconPx, 8, 48);
        OpenTK.Mathematics.Vector4 V(Color c, float a = 1) => new(c.R / 255f, c.G / 255f, c.B / 255f, a);
        if (ParseColor(th.Dark ? L.SketchColorDark : L.SketchColorLight) is { } sc) { th.SketchCurve = V(sc); th.SketchPoint = V(sc); }
        if (ParseColor(th.Dark ? L.BackgroundTopDark : L.BackgroundTopLight) is { } bt) th.BackgroundTop = V(bt);
        if (ParseColor(th.Dark ? L.BackgroundBottomDark : L.BackgroundBottomLight) is { } bb) th.BackgroundBottom = V(bb);
        th.SectionHatch = L.SectionHatch;
        if (!LightPrefsOff) ApplyModelView(th, L);
        th.SectionHatchColor = ParseColor(L.SectionHatchColor) is { } hc ? V(hc) : default;
        th.SectionFillColor = ParseColor(L.SectionFillColor) is { } fc ? V(fc) : default;
    }
}
