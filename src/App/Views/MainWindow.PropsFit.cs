using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Parametric3D.App.Views;

/// <summary>
/// k185 (замечание 10.11, 08:19, а4): КОЛОНКА НАЗВАНИЙ ПАНЕЛИ СВОЙСТВ ПО ТЕКСТУ. Строки панели — сетка «название | поле» с
/// колонкой названий постоянной ширины (108–110 px), из‑за чего длинные названия переносились по словам даже в широкой панели.
/// Теперь ширина колонки — по самому длинному названию в одну строку (одна на все строки, чтобы поля стояли ровно), но не меньше
/// прежней и не больше, чем оставляет место полю (<see cref="PropsEditorMinPx"/>). Не хватает ширины — названия переносятся, как
/// раньше. Пересчёт — при смене содержимого панели и её ширины. Мутация P3D_PROPSLABELFIT=0 — постоянная колонка.
/// </summary>
public partial class MainWindow
{
    public static readonly bool PropsLabelFitOff = Environment.GetEnvironmentVariable("P3D_PROPSLABELFIT") == "0";
    public const double PropsEditorMinPx = 110;
    static readonly ConditionalWeakTable<Grid, object> LabelBase = new();
    bool _propsFitQueued, _propsFitHooked;

    void HookPropsLabelFit()
    {
        if (_propsFitHooked || PropsLabelFitOff) return;
        _propsFitHooked = true;
        PropsBody.Children.CollectionChanged += (_, _) => QueuePropsLabelFit();
        PropsBody.SizeChanged += (s, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) QueuePropsLabelFit(); };
    }

    void QueuePropsLabelFit()
    {
        if (_propsFitQueued) return;
        _propsFitQueued = true;
        Dispatcher.UIThread.Post(() => { _propsFitQueued = false; FitPropsLabels(); }, DispatcherPriority.Loaded);
    }

    /// <summary>Строка «название | поле»: первая колонка задана в пикселях, вторая — «*», в первой — надпись.</summary>
    static TextBlock? LabelOf(Grid g)
    {
        if (g.ColumnDefinitions.Count < 2) return null;
        var c0 = g.ColumnDefinitions[0].Width; var c1 = g.ColumnDefinitions[1].Width;
        if (!c1.IsStar) return null;
        double basePx;
        if (LabelBase.TryGetValue(g, out var b)) basePx = (double)b;
        else
        {
            if (!c0.IsAbsolute || c0.Value < 60 || c0.Value > 200) return null;
            basePx = c0.Value;
        }
        foreach (var ch in g.Children)
            if (ch is TextBlock tb && tb.Classes.Contains("dim") && Grid.GetColumn(tb) == 0 && Grid.GetColumnSpan(tb) == 1 && tb.IsVisible)
            {
                if (!LabelBase.TryGetValue(g, out _)) LabelBase.Add(g, basePx);
                return tb;
            }
        return null;
    }

    /// <summary>Подогнать колонку названий; ширина колонки после подгонки (разбор и проверки), 0 — строк нет.</summary>
    public double FitPropsLabels()
    {
        if (PropsLabelFitOff || PropsCollapsed) return 0;
        var rows = new List<(Grid G, TextBlock L, double Base)>();
        foreach (var g in PropsBody.GetVisualDescendants().OfType<Grid>().Concat(PropsBody.Children.OfType<Grid>()).Distinct())
            if (LabelOf(g) is { } tb && LabelBase.TryGetValue(g, out var b)) rows.Add((g, tb, (double)b));
        if (rows.Count == 0) return 0;
        double need = 0, basePx = rows.Max(r => r.Base);
        foreach (var r in rows) need = Math.Max(need, NaturalWidth(r.L));
        double avail = PropsBody.Bounds.Width - PropsBody.Margin.Left - PropsBody.Margin.Right;
        if (avail <= 0) avail = (double.IsNaN(PropsPanel.Width) ? PropsPanel.Bounds.Width : PropsPanel.Width) - 24;
        double w = Math.Max(basePx, Math.Min(Math.Ceiling(need) + 8, avail - PropsEditorMinPx));
        foreach (var r in rows)
            if (Math.Abs(r.G.ColumnDefinitions[0].Width.Value - w) > 0.5 || !r.G.ColumnDefinitions[0].Width.IsAbsolute)
                r.G.ColumnDefinitions[0].Width = new GridLength(w);
        PropsLabelColumnPx = w;
        return w;
    }

    /// <summary>Ширина колонки названий после последней подгонки.</summary>
    public double PropsLabelColumnPx { get; private set; }

    /// <summary>Ширина надписи в одну строку (тем же шрифтом).</summary>
    static double NaturalWidth(TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text)) return 0;
        var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, null);
        return ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right + tb.Margin.Left + tb.Margin.Right;
    }
}
