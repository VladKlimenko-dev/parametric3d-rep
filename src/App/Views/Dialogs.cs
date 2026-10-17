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
