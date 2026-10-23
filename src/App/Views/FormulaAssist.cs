using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Parametric3D.Model;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Views;

/// <summary>
/// k103: ПОЛЕ ФОРМУЛЫ (правка размера эскиза, окно «Параметры»).
/// (замечание 3) Точка после имени в кавычках (s"Эскиз1".) — список того, что внутри: размеры «d1 = 51.3», эскизы, операции,
/// параметры; точка в начале имени (не после цифры) — размеры своего эскиза и имена детали; выбор — щелчком, Enter или Tab,
/// стрелки — по списку, Esc — закрыть. (замечание 4) Имя в закрытых кавычках, которого нет, — красным, найденное — синим:
/// поверх текста поля лежит такая же надпись с цветными частями (текст самого поля прозрачный, курсор и выделение — его).
/// Мутация P3D_FORMULAASSIST=0 — простое поле.
/// </summary>
public sealed class FormulaAssist
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_FORMULAASSIST") == "0";

    public readonly TextBox Box;
    /// <summary>Что ставить в разметку вместо поля: поле с надписью поверх.</summary>
    public readonly Control View;
    private readonly Func<ModelItem?> _context;
    private readonly Func<Sketch?> _local;
    private readonly Canvas _clip = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly TextBlock _overlay = new() { IsHitTestVisible = false, TextWrapping = TextWrapping.NoWrap };
    private readonly Popup _pop;
    private readonly ListBox _list = new() { MaxHeight = 220, MinWidth = 180 };
    private int _dotAt = -1;
    private bool _topLevel;
    private bool _inserting;

    /// <summary>Подсказки открытого списка (вставка, подпись).</summary>
    public List<(string Insert, string Label)> Items { get; private set; } = new();
    public bool ListOpen => _pop.IsOpen;
    /// <summary>Разбор: имена в кавычках последней раскраски — начало, длина, найдено.</summary>
    public List<(int Start, int Length, bool Ok)> Spans { get; private set; } = new();

    public static readonly Color Missing = Color.Parse("#E5484D");
    public static Color DimGreen => Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? Color.Parse("#4CC76A") : Color.Parse("#1E8A3A");
    /// <summary>Разбор: размеры (dN) последней раскраски — начало, длина, найден.</summary>
    public List<(int Start, int Length, bool Ok)> DimSpans { get; private set; } = new();
    public static readonly bool DimColorOff = Environment.GetEnvironmentVariable("P3D_DIMCOLOR") == "0";
    public static Color Found => Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? Color.Parse("#6F9BFF") : Color.Parse("#1A3A9E");

    public FormulaAssist(TextBox box, Func<ModelItem?> context, Func<Sketch?> local)
    {
        Box = box; _context = context; _local = local;
        _list.SelectionMode = SelectionMode.Single;
        _pop = new Popup
        {
            PlacementTarget = box, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true,
            Child = new Border { Child = _list, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) },
        };
        ((Border)_pop.Child).Bind(Border.BackgroundProperty, box.GetResourceObservable("P3.PanelBg"));
        ((Border)_pop.Child).Bind(Border.BorderBrushProperty, box.GetResourceObservable("P3.Border"));
        _list.PointerReleased += (_, _) => { if (_list.SelectedIndex >= 0) Choose(_list.SelectedIndex); };
        if (Off) { View = box; return; }
        _clip.Children.Add(_overlay);
        _overlay.Bind(TextBlock.ForegroundProperty, box.GetResourceObservable("P3.Text"));
        var grid = new Grid { Children = { box, _clip, _pop } };
        View = grid;
        box.Foreground = Brushes.Transparent;
        box.Bind(TextBox.CaretBrushProperty, box.GetResourceObservable("P3.Text"));
        // старое и новое значение — сразу при изменении (TextChanged приходит позже и может объединить правки)
        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OnText(e.OldValue as string ?? "", e.NewValue as string ?? ""); };
        box.LayoutUpdated += (_, _) => Place();
        box.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => { if (!_list.IsPointerOver) Close(); };
        Paint();
    }

    // ------------------------------------------------------------------ раскраска

    private void Paint()
    {
        string text = Box.Text ?? "";
        var ctx = _context();
        Spans = ctx == null ? new() : ModelRefs.QuotedNames(ctx, text);
        // 18:52: размеры (dN) — зелёным, несуществующие — красным
        DimSpans = ctx == null || DimColorOff ? new() : ModelRefs.DimNames(ctx, _local(), text);
        var all = Spans.Select(x => (x.Start, x.Length, Color: x.Ok ? Found : Missing))
            .Concat(DimSpans.Select(x => (x.Start, x.Length, Color: x.Ok ? DimGreen : Missing))).OrderBy(x => x.Start).ToList();
        _overlay.Inlines ??= new InlineCollection();
        _overlay.Inlines.Clear();
        int at = 0;
        foreach (var (start, len, color) in all)
        {
            if (start < at) continue;
            if (start > at) _overlay.Inlines.Add(new Run(text[at..start]));
            _overlay.Inlines.Add(new Run(text.Substring(start, len)) { Foreground = new SolidColorBrush(color) });
            at = start + len;
        }
        if (at < text.Length) _overlay.Inlines.Add(new Run(text[at..]));
        Place();
    }

    /// <summary>Надпись — точно над текстом поля (с учётом прокрутки), обрезана по области текста.</summary>
    private void Place()
    {
        if (Off || View is not Grid g) return;
        var tp = Box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (tp == null) return;
        var host = (Visual?)tp.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() ?? tp;
        if (host.TranslatePoint(new Point(0, 0), g) is not { } hp || tp.TranslatePoint(new Point(0, 0), g) is not { } pp) return;
        var m = new Thickness(hp.X, hp.Y, Math.Max(0, g.Bounds.Width - hp.X - host.Bounds.Width), Math.Max(0, g.Bounds.Height - hp.Y - host.Bounds.Height));
        if (_clip.Margin != m) _clip.Margin = m;
        double x = pp.X - hp.X, y = pp.Y - hp.Y;
        if (Canvas.GetLeft(_overlay) != x) Canvas.SetLeft(_overlay, x);
        if (Canvas.GetTop(_overlay) != y) Canvas.SetTop(_overlay, y);
        _overlay.FontFamily = Box.FontFamily; _overlay.FontSize = Box.FontSize; _overlay.FontWeight = Box.FontWeight; _overlay.FontStyle = Box.FontStyle;
        _overlay.LetterSpacing = Box.LetterSpacing;
        if (_overlay.Height != tp.Bounds.Height && tp.Bounds.Height > 0) { _overlay.Height = tp.Bounds.Height; _overlay.VerticalAlignment = VerticalAlignment.Center; }
    }

    // ------------------------------------------------------------------ подсказка после точки

    static readonly Regex Chain = new("(?:[apsf]\"[^\"]*\"\\.)*[apsf]\"[^\"]*\"$", RegexOptions.Compiled);

    private void OnText(string was, string text)
    {
        Paint();
        if (_inserting) return;
        // напечатан один знак «.» (место — по разнице текстов: курсор в этот момент может быть ещё прежним)
        int at = -1;
        if (text.Length == was.Length + 1)
        {
            int i = 0;
            while (i < was.Length && was[i] == text[i]) i++;
            if (text[i] == '.' && text.Remove(i, 1) == was) at = i;
        }
        if (at < 0) { if (ListOpen && (_dotAt >= text.Length || _dotAt < 0 || text[_dotAt] != '.')) Close(); return; }
        ShowFor(text, at);
    }

    /// <summary>Открыть список для точки в позиции dot (проверки зовут напрямую).</summary>
    public void ShowFor(string text, int dot)
    {
        var ctx = _context();
        if (ctx == null) return;
        string before = text[..dot];
        string prefix;
        var m = Chain.Match(before);
        if (m.Success && (m.Index == 0 || !char.IsLetterOrDigit(before[m.Index - 1]) && before[m.Index - 1] != '_')) { prefix = m.Value; _topLevel = false; }
        else if (dot > 0 && (char.IsLetterOrDigit(before[^1]) || before[^1] is '_' or '"' or ')')) return;   // десятичная точка, конец имени
        else { prefix = ""; _topLevel = true; }
        Items = ModelRefs.Suggest(ctx, _local(), prefix);
        if (Items.Count == 0) { Close(); return; }
        _dotAt = dot;
        _list.ItemsSource = Items.Select(i => i.Label).ToList();
        _list.SelectedIndex = 0;
        _pop.IsOpen = true;
    }

    public void Close() { _pop.IsOpen = false; _dotAt = -1; }

    /// <summary>Вставить подсказку i: после точки (путь) или вместо неё (начало имени).</summary>
    public void Choose(int i)
    {
        if (i < 0 || i >= Items.Count || _dotAt < 0) { Close(); return; }
        string text = Box.Text ?? "";
        if (_dotAt >= text.Length || text[_dotAt] != '.') { Close(); return; }
        string ins = Items[i].Insert;
        int from = _topLevel ? _dotAt : _dotAt + 1;
        // уже набранное после точки (начало имени) заменяется
        int to = _topLevel ? _dotAt + 1 : from;
        while (to < text.Length && (char.IsLetterOrDigit(text[to]) || text[to] == '_') && !_topLevel) to++;
        _inserting = true;
        try
        {
            Box.Text = text[..from] + ins + text[to..];
            Box.CaretIndex = Box.SelectionStart = Box.SelectionEnd = from + ins.Length;
        }
        finally { _inserting = false; }
        Close();
        Box.Focus();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (!ListOpen) return;
        switch (e.Key)
        {
            case Key.Down: _list.SelectedIndex = Math.Min(Items.Count - 1, _list.SelectedIndex + 1); _list.ScrollIntoView(_list.SelectedIndex); e.Handled = true; break;
            case Key.Up: _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); _list.ScrollIntoView(_list.SelectedIndex); e.Handled = true; break;
            case Key.Enter or Key.Tab: Choose(Math.Max(0, _list.SelectedIndex)); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }
}
