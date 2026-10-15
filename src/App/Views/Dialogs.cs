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
}
