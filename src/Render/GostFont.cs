using OpenTK.Mathematics;
using SkiaSharp;

namespace Parametric3D.Render;

/// <summary>
/// ШРИФТ ГОСТ 2.304 ТИП А ДЛЯ ТЕКСТА В ОКНЕ ВИДА. Глифы шрифта пользователя (App/Assets/Fonts/GOST_A.TTF) — контурами, в тех же
/// единицах, что штриховой шрифт: высота прописной буквы — 1, базовая линия — y = 0. Контур глифа рисуется ломаными тем же
/// пакетом линий (у шрифта ГОСТ штрих узкий — контур читается как штрих). Нет файла или знака — штриховой шрифт (StrokeFont).
/// </summary>
public static class GostFont
{
    public const string Family = "GOST type A";
    /// <summary>Шрифт для Avalonia (подписи куба вида, всплывающий текст поверх вида).</summary>
    public const string AvaloniaUri = "avares://Parametric3D/App/Assets/Fonts#GOST type A";

    private static readonly Lazy<(SKTypeface Face, double Cap)?> _face = new(Load);
    private static readonly Dictionary<char, (List<Vector2d[]> Contours, double Advance)?> _glyphs = new();
    private static readonly object _lock = new();

    public static bool Available => _face.Value != null;

    private static (SKTypeface, double)? Load()
    {
        try
        {
            using var s = typeof(GostFont).Assembly.GetManifestResourceStream("GOST_A.TTF");
            if (s == null) return null;
            var face = SKTypeface.FromStream(s);
            if (face == null) return null;
            using var font = new SKFont(face, 1000);
            // высота прописной — по глифу «H» (метрики шрифта её не всегда несут)
            using var path = font.GetGlyphPath(font.GetGlyph('H'));
            double cap = path != null && path.Bounds.Height > 1 ? path.Bounds.Height : 700;
            return (face, cap);
        }
        catch { return null; }
    }

    /// <summary>Контуры и шаг знака (высота прописной — 1); null — знака в шрифте нет.</summary>
    public static (List<Vector2d[]> Contours, double Advance)? Glyph(char c)
    {
        if (_face.Value is not { } f) return null;
        lock (_lock)
        {
            if (_glyphs.TryGetValue(c, out var g)) return g;
            g = null;
            using var font = new SKFont(f.Face, 1000);
            ushort id = font.GetGlyph(c);
            if (id != 0 || c == ' ')
            {
                var contours = new List<Vector2d[]>();
                using var path = font.GetGlyphPath(id);
                if (path != null && !path.IsEmpty)
                {
                    using var measure = new SKPathMeasure(path, false, 1);
                    do
                    {
                        float len = measure.Length; if (len <= 0) continue;
                        int n = Math.Clamp((int)(len / 12), 4, 200);
                        var pts = new Vector2d[n + 1];
                        for (int i = 0; i <= n; i++)
                        {
                            measure.GetPosition(len * i / n, out var p);
                            pts[i] = new Vector2d(p.X / f.Cap, -p.Y / f.Cap);     // y Skia — вниз
                        }
                        contours.Add(pts);
                    } while (measure.NextContour());
                }
                float adv = font.GetGlyphWidths(new[] { id }, out _)[0];
                g = (contours, adv / f.Cap);
            }
            _glyphs[c] = g;
            return g;
        }
    }
}
