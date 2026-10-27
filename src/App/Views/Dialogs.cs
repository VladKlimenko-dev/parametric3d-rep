using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Parametric3D.App.Views;

/// <summary>Простые окна вопросов и сообщений (в Avalonia нет MessageBox).</summary>
public static class Dialogs
{
    /// <summary>Вопрос с кнопками; возвращает текст нажатой кнопки или null (окно закрыто).</summary>
    public static async Task<string?> Ask(Window owner, string title, string text, params string[] buttons)
    {
        string? result = null;
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 360,
            MaxWidth = 640,
        };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("P3.PanelBg"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            string b = buttons[i];
            var btn = new Button { Content = b, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) btn.Classes.Add("accent");
            btn.Click += (_, _) => { result = b; w.Close(); };
            row.Children.Add(btn);
        }
        w.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 },
                row,
            }
        };
        await w.ShowDialog(owner);
        return result;
    }

    public static Task Info(Window owner, string title, string text) => Ask(owner, title, text, "OK");

    /// <summary>k119: настройки импорта STL; null — отмена.</summary>
    public sealed record StlOptions(bool Exact, bool Snap, double TolScale, double Scale);
    public static readonly (string Name, double Scale)[] StlUnits = { ("мм", 1), ("см", 10), ("м", 1000), ("дюйм", 25.4) };

    public static async Task<StlOptions?> StlImport(Window owner, string file, bool exact = true, bool snap = true, double tol = 1, double scale = 1)
    {
        StlOptions? result = null;
        var w = new Window { Title = Loc.L("Импорт STL"), SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, MinWidth = 420, MaxWidth = 640 };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("P3.PanelBg"));
        var cbExact = new CheckBox { Content = Loc.L("Преобразовать в точную геометрию"), IsChecked = exact, Name = "stl_exact" };
        var exactNote = new TextBlock { Text = Loc.L("Плоскости, цилиндры, конусы, сферы и торы находятся на сетке; тело — точное (резать, сверлить, ставить эскиз на грань). Без галочки — тело из плоских граней сетки."), TextWrapping = TextWrapping.Wrap, MaxWidth = 560, FontSize = 12, Opacity = 0.75 };
        var cbSnap = new CheckBox { Content = Loc.L("Выравнивать по замыслу (оси, соосность, круглые размеры, касания)"), IsChecked = snap, Name = "stl_snap" };
        var tolBox = new TextBox { Text = tol.ToString(System.Globalization.CultureInfo.InvariantCulture), MinWidth = 80, Name = "stl_tol" };
        var units = new ComboBox { ItemsSource = StlUnits.Select(u => Loc.L(u.Name)).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(StlUnits, u => Math.Abs(u.Scale - scale) < 1e-12)), MinWidth = 100, Name = "stl_units" };
        cbExact.IsCheckedChanged += (_, _) => cbSnap.IsEnabled = cbExact.IsChecked == true;
        cbSnap.IsEnabled = exact;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        void Add(Control c, int r, int col) { Grid.SetRow(c, r); Grid.SetColumn(c, col); c.Margin = new Thickness(0, 2, 12, 2); grid.Children.Add(c); }
        Add(new TextBlock { Text = Loc.L("Единицы файла"), VerticalAlignment = VerticalAlignment.Center }, 0, 0); Add(units, 0, 1);
        Add(new TextBlock { Text = Loc.L("Допуск, × (1 — сетка из САПР; скан — 10…100)"), VerticalAlignment = VerticalAlignment.Center }, 1, 0); Add(tolBox, 1, 1);
        var ok = new Button { Content = Loc.L("Импортировать"), MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center }; ok.Classes.Add("accent");
        var cancel = new Button { Content = Loc.L("Отмена"), MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            double t = double.TryParse((tolBox.Text ?? "1").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tv) && tv > 0 ? tv : 1;
            result = new StlOptions(cbExact.IsChecked == true, cbSnap.IsChecked == true, t, StlUnits[Math.Max(0, units.SelectedIndex)].Scale);
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = file, FontWeight = FontWeight.SemiBold },
                cbExact, exactNote, cbSnap, grid,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            }
        };
        LastStlWindow = w;
        await w.ShowDialog(owner);
        return result;
    }
    /// <summary>Разбор (проверки): окно настроек импорта STL.</summary>
    public static Window? LastStlWindow;

    /// <summary>u13: строка ввода (ОК — текст, Отмена или закрытие — null).</summary>
    public static async Task<string?> Prompt(Window owner, string title, string text, string initial = "")
    {
        string? result = null;
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, MinWidth = 360, MaxWidth = 640 };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("P3.PanelBg"));
        var tb = new TextBox { Text = initial, MinWidth = 300 };
        var ok = new Button { Content = Loc.L("ОК"), MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center }; ok.Classes.Add("accent");
        var cancel = new Button { Content = Loc.L("Отмена"), MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { result = tb.Text; w.Close(); } else if (e.Key == Avalonia.Input.Key.Escape) w.Close(); };
        w.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 }, tb,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } };
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }
}
