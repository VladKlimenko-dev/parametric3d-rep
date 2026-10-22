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
    public static IImage? Get(string? file) => Get(file, Foreground);

    /// <summary>
    /// k96 (п. 12): иконка, перекрашенная под заданный цвет текста (подложка значков связей в виде — своей темы, не темы окна:
    /// при светлом окне и тёмном виде чёрная основная черта значка «Горизонтально» пропадала на тёмной подложке, видна
    /// оставалась серая вспомогательная — вертикальная, значок читался повёрнутым на 90°).
    /// </summary>
    public static IImage? Get(string? file, Color foreground)
    {
        if (string.IsNullOrEmpty(file)) return null;
        string fg = $"#{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}";
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
                string raw = r.ReadToEnd();
                string text = Recolor(FitToContent(file, raw), fg);
                img = new SvgImage { Source = SvgSource.LoadFromSvg(text) };
            }
        }
        catch (Exception ex) { Core.AppLog.Write("icon " + file, ex); img = null; }
        _cache[key] = img;
        return img;
    }

    /// <summary>
    /// k98 (замечание 13): ИКОНКА ВПИСЫВАЕТСЯ ПО РИСУНКУ, а не по полю листа. У части иконок рисунок занимал малую долю
    /// листа (поля вокруг), и на кнопке они были заметно мельче соседних. Рисунок растрируется один раз (256 px), по
    /// непрозрачным пикселям находится его рамка, лист обрезается до неё с полем 4 % (viewBox). P3D_ICONFIT=0 — по листу.
    /// </summary>
    public static readonly bool FitOff = Environment.GetEnvironmentVariable("P3D_ICONFIT") == "0";
    private static readonly Dictionary<string, (double X, double Y, double W, double H)?> _boxes = new();
    /// <summary>Разбор: итоговый лист иконки (рамка рисунка с полем) в единицах её файла; null — лист не менялся.</summary>
    public static (double X, double Y, double W, double H)? ContentBox(string file) => _boxes.TryGetValue(file, out var b) ? b : null;

    public static string FitToContent(string file, string svg)
    {
        if (FitOff) return svg;
        var root = RootTag.Match(svg);
        if (!root.Success) return svg;
        string tag = root.Value;
        static double? Attr(string t, string n) { var m = Regex.Match(t, n + @"\s*=\s*""\s*([-\d.eE]+)"); return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null; }
        double vx = 0, vy = 0, vw, vh;
        var vb = Regex.Match(tag, @"viewBox\s*=\s*""([^""]+)""");
        if (vb.Success)
        {
            var n = vb.Groups[1].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (n.Length != 4) return svg;
            (vx, vy, vw, vh) = (n[0], n[1], n[2], n[3]);
        }
        else { vw = Attr(tag, "width") ?? 0; vh = Attr(tag, "height") ?? 0; }
        if (vw <= 0 || vh <= 0) return svg;
        (double X, double Y, double W, double H)? box;
        lock (_boxes)
            if (!_boxes.TryGetValue(file, out box)) { box = Measure(svg, vx, vy, vw, vh); _boxes[file] = box; }
        if (box is not { } b) return svg;
        string inv(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        string nt = Regex.Replace(tag, @"\s(width|height|viewBox|preserveAspectRatio)\s*=\s*""[^""]*""", "");
        // b — итоговый лист (рамка с полем; целые ширина и высота в пикселях листа 1000 — пропорции рисунка = пропорциям файла)
        int iw = (int)Math.Round(b.W * 1000 / Math.Max(b.W, b.H)), ih = (int)Math.Round(b.H * 1000 / Math.Max(b.W, b.H));
        nt = nt.Insert(4, $" width=\"{iw}\" height=\"{ih}\" viewBox=\"{inv(b.X)} {inv(b.Y)} {inv(b.W)} {inv(b.H)}\" preserveAspectRatio=\"xMidYMid meet\"");
        return svg[..root.Index] + nt + svg[(root.Index + root.Length)..];
    }

    private static (double, double, double, double)? Measure(string svg, double vx, double vy, double vw, double vh)
    {
        try
        {
            // лист — без полей и с теми же единицами: растр 256 по большей стороне
            var root = RootTag.Match(svg);
            string tag = Regex.Replace(root.Value, @"\s(width|height|viewBox|preserveAspectRatio)\s*=\s*""[^""]*""", "");
            string inv(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            double k = 256.0 / Math.Max(vw, vh);
            int W = Math.Max(1, (int)Math.Ceiling(vw * k)), H = Math.Max(1, (int)Math.Ceiling(vh * k));
            tag = tag.Insert(4, $" width=\"{W}\" height=\"{H}\" viewBox=\"{inv(vx)} {inv(vy)} {inv(vw)} {inv(vh)}\" preserveAspectRatio=\"none\"");
            string norm = svg[..root.Index] + tag + svg[(root.Index + root.Length)..];
            using var sk = new Svg.Skia.SKSvg();
            var pic = sk.FromSvg(norm);
            if (pic == null) return null;
            using var bmp = new SkiaSharp.SKBitmap(W, H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using (var cv = new SkiaSharp.SKCanvas(bmp)) { cv.Clear(SkiaSharp.SKColors.Transparent); cv.DrawPicture(pic); }
            int x0 = W, y0 = H, x1 = -1, y1 = -1;
            var px = bmp.Pixels;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (px[y * W + x].Alpha > 10) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 < x0 || y1 < y0) return null;
            double bx = vx + x0 / k, by = vy + y0 / k, bw = (x1 + 1 - x0) / k, bh = (y1 + 1 - y0) / k;
            // рисунок и так во весь лист — без изменений
            if (bw >= vw * 0.97 && bh >= vh * 0.97) return null;
            // поле — 4 % большей стороны; лист — целые 1000 × n (пропорции рисунка совпадают с шириной и высотой файла)
            double pad = Math.Max(bw, bh) * 0.04;
            bx -= pad; by -= pad; bw += 2 * pad; bh += 2 * pad;
            double m = Math.Max(bw, bh);
            double iw = Math.Round(bw * 1000 / m), ih = Math.Round(bh * 1000 / m);
            double nw = iw * m / 1000, nh = ih * m / 1000;
            bx -= (nw - bw) / 2; by -= (nh - bh) / 2;
            return (bx, by, nw, nh);
        }
        catch { return null; }
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
