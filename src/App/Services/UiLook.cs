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

    public LookPrefs Clone() => (LookPrefs)MemberwiseClone();
}

/// <summary>Действующие визуальные настройки (чтение — отовсюду, где строятся панели и вид).</summary>
public static class UiLook
{
    static LookPrefs L => UserPrefs.Current.Look;
    public static double StripButtonPx => Math.Clamp(L.StripButtonPx, 22, 64);
    public static double StripIconPx => Math.Clamp(L.StripIconPx, 12, StripButtonPx - 6);
    public static double PanelIconPx => Math.Clamp(L.PanelIconPx, 12, 40);
    public static double UiFontSize => Math.Clamp(L.UiFontSize, 9, 22);

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
        th.SectionHatchColor = ParseColor(L.SectionHatchColor) is { } hc ? V(hc) : default;
        th.SectionFillColor = ParseColor(L.SectionFillColor) is { } fc ? V(fc) : default;
    }
}
