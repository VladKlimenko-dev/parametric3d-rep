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

    /// <summary>Прямоугольник внутри dst с пропорциями исходного рисунка, по центру (сохранение пропорций при рисовании).</summary>
    public static Rect Fit(Size src, Rect dst)
    {
        if (src.Width <= 0 || src.Height <= 0) return dst;
        double k = Math.Min(dst.Width / src.Width, dst.Height / src.Height);
        double w = src.Width * k, h = src.Height * k;
        return new Rect(dst.X + (dst.Width - w) / 2, dst.Y + (dst.Height - h) / 2, w, h);
    }

    /// <summary>Перекрасить чистый чёрный (fill, stroke, color, stop-color) в цвет темы.</summary>
    /// <summary>
    /// Перекраска под тему. Иконки нарисованы для СВЕТЛОЙ темы. Светлая тема (цвет текста тёмный): чистый чёрный — в цвет
    /// текста, прочее как нарисовано. ТЁМНАЯ тема (цвет текста светлый): серые тона обращаются по яркости — чёрный (и почти
    /// чёрный, и контур без fill — по умолчанию чёрный) становится цветом текста (белым), белый — чёрным, светло-серый —
    /// тёмно-серым (L → 255 − L); цветные акценты (оранжевый, синий, красный…) не трогаются.
    /// </summary>
    public static string Recolor(string svg, string hex)
    {
        var fg = Color.Parse(hex);
        bool dark = fg.R * 0.299 + fg.G * 0.587 + fg.B * 0.114 > 128;
        string r = ColorToken.Replace(svg, m =>
        {
            var c = ParseColor(m.Groups[2].Value);
            if (c == null) return m.Value;
            var (cr, cg, cb) = c.Value;
            bool gray = Math.Abs(cr - cg) <= 12 && Math.Abs(cg - cb) <= 12 && Math.Abs(cr - cb) <= 12;
            if (!gray) return m.Value;
            int l = (cr + cg + cb) / 3;
            if (l <= 0x20) return m.Groups[1].Value + hex;                         // чёрный — цвет текста
            if (!dark) return m.Value;                                             // светлая тема — как нарисовано
            int inv = 255 - l;
            return m.Groups[1].Value + $"#{inv:X2}{inv:X2}{inv:X2}";
        });
        // контур без fill по умолчанию чёрный (SVG): заливка по умолчанию у корня — цвет текста; явные заливки не трогаются
        var root = RootTag.Match(r);
        if (root.Success && !root.Value.Contains(" fill=")) r = r.Insert(root.Index + 4, $" fill=\"{hex}\"");
        return r;
    }

    private static readonly Regex ColorToken = new(@"((?:fill|stroke|color|stop-color)\s*[:=]\s*[""']?\s*)(#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b|black\b|white\b|rgb\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (int R, int G, int B)? ParseColor(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v == "black") return (0, 0, 0);
        if (v == "white") return (255, 255, 255);
        if (v.StartsWith("rgb"))
        {
            var n = Regex.Matches(v, @"\d+").Select(x => int.Parse(x.Value)).ToArray();
            return n.Length == 3 ? (n[0], n[1], n[2]) : null;
        }
        if (v.StartsWith('#'))
        {
            string h = v[1..];
            if (h.Length == 3) h = string.Concat(h.Select(ch => $"{ch}{ch}"));
            if (h.Length != 6) return null;
            int x = Convert.ToInt32(h, 16);
            return ((x >> 16) & 255, (x >> 8) & 255, x & 255);
        }
        return null;
    }
    private static readonly Regex RootTag = new(@"<svg\b[^>]*>", RegexOptions.Compiled);

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
