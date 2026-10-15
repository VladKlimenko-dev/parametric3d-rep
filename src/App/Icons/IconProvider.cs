using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;

namespace Parametric3D.App.Icons;

/// <summary>
/// ИКОНКИ ПОЛЬЗОВАТЕЛЯ (UI_ICONS.md): SVG из App/Assets/Icons, вшитые в сборку. Чистый чёрный в иконке перекрашивается
/// в цвет текста темы (светлый на тёмной теме, тёмный на светлой); цветные акценты остаются. Нет файла — команда
/// показывается текстом (рисовать иконки можно постепенно).
/// </summary>
public static class IconProvider
{
    private static readonly Dictionary<(string, string), IImage?> _cache = new();
    private static readonly Regex Black = new(@"(?<=(fill|stroke|color|stop-color)\s*[:=]\s*[""']?)\s*(#000000|#000|black|rgb\(\s*0\s*,\s*0\s*,\s*0\s*\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Цвет, в который перекрашивается чёрный (задаётся при смене темы).</summary>
    public static Color Foreground { get; private set; } = Color.Parse("#E6E8EC");

    public static void SetForeground(Color c)
    {
        if (c == Foreground) return;
        Foreground = c;
        ThemeChanged?.Invoke();
    }

    public static event Action? ThemeChanged;

    public static Uri UriOf(string file) => new($"avares://Parametric3D/App/Assets/Icons/{file}");

    public static bool Exists(string? file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        try { return AssetLoader.Exists(UriOf(file)); } catch { return false; }
    }

    /// <summary>Иконка по имени файла (например, «save_but.svg»), null — файла нет или он не читается.</summary>
    public static IImage? Get(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        string fg = $"#{Foreground.R:X2}{Foreground.G:X2}{Foreground.B:X2}";
        var key = (file, fg);
        if (_cache.TryGetValue(key, out var img)) return img;
        img = null;
        try
        {
            var uri = UriOf(file);
            if (AssetLoader.Exists(uri))
            {
                using var s = AssetLoader.Open(uri);
                using var r = new StreamReader(s);
                string text = Recolor(r.ReadToEnd(), fg);
                img = new SvgImage { Source = SvgSource.LoadFromSvg(text) };
            }
        }
        catch (Exception ex) { Core.AppLog.Write("icon " + file, ex); img = null; }
        _cache[key] = img;
        return img;
    }

    /// <summary>Перекрасить чистый чёрный (fill, stroke, color, stop-color) в цвет темы.</summary>
    public static string Recolor(string svg, string hex) => Black.Replace(svg, hex);

    /// <summary>
    /// Содержимое кнопки: иконка заданного размера или, если её нет, короткая подпись. Подпись — первые буквы слов
    /// (до трёх), полное имя — во всплывающей подсказке кнопки.
    /// </summary>
    public static Control Content(string? file, string label, double size = 20, bool withLabel = false)
    {
        var img = Get(file);
        Control icon = img != null
            ? new Image { Source = img, Width = size, Height = size, Stretch = Stretch.Uniform }
            : new TextBlock { Text = Abbrev(label), FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (!withLabel) return icon;
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(img != null ? icon : new Border { Width = size, Height = size });
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    public static string Abbrev(string label)
    {
        if (label.Length <= 10 && !label.Contains(' ')) return label;
        var words = label.Split(new[] { ' ', '-', '/', '«', '»', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1) return words[0].Length <= 4 ? words[0] : words[0][..3];
        return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
    }
}
