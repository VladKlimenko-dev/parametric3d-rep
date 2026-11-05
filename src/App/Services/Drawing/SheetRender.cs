using Parametric3D.Model.Drawing;
using SkiaSharp;

namespace Parametric3D.App.Services.Drawing;

/// <summary>
/// k155 (D1): ВЫВОД ЛИСТА — одни и те же примитивы (SheetGraphics) рисуются в PDF (вектор, SKDocument) и в картинку для окна
/// листа. Шрифт — ГОСТ тип А (GOST_A.TTF, встроен в сборку); высота шрифта по ГОСТ 2.304 — высота прописной буквы, размер
/// шрифта подбирается по метрике CapHeight. Толщины линий — ГОСТ 2.303 (0.7 и 0.25 мм).
/// </summary>
public static class SheetRender
{
    public const float PtPerMm = 72f / 25.4f;
    static SKTypeface? _gost;
    /// <summary>Разбор: страниц PDF записано.</summary>
    public static long PdfPages;

    public static SKTypeface Gost
    {
        get
        {
            if (_gost != null) return _gost;
            var asm = typeof(SheetRender).Assembly;
            using var s = asm.GetManifestResourceStream("GOST_A.TTF");
            _gost = s != null ? SKTypeface.FromStream(s) : null;
            _gost ??= SKTypeface.Default;
            return _gost;
        }
    }

    /// <summary>Отношение высоты прописной к размеру шрифта (ГОСТ тип А — около 0.7).</summary>
    static float CapRatio()
    {
        using var f = new SKFont(Gost, 100);
        float cap = f.Metrics.CapHeight;
        return cap > 1 ? cap / 100f : 0.7f;
    }

    /// <summary>Рисует лист на холсте: масштаб — единиц холста на мм; начало холста — левый верхний угол листа.</summary>
    static readonly bool ShadeSeamOff = Environment.GetEnvironmentVariable("P3D_SHADESEAM") == "0";
    public static void Draw(SKCanvas c, SheetGraphics g, float scale, bool white = true)
    {
        if (white) c.DrawRect(0, 0, (float)g.Width * scale, (float)g.Height * scale, new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill });
        float Y(double y) => (float)((g.Height - y) * scale);
        // k161: закраска (изометрия «с тонами»): многоугольники граней под линиями
        if (g.Shades.Count > 0)
            // k166 (замечание 05.11, 15:03, п. 2–4): треугольники тонов — заливка и обводка тем же цветом в 1 точку: прежде
            // сглаживание краёв оставляло светлые щели между соседними треугольниками — на виде «Тона» была видна сетка
            using (var sp = new SKPaint { Style = ShadeSeamOff ? SKPaintStyle.Fill : SKPaintStyle.StrokeAndFill, StrokeWidth = 1f, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true })
                foreach (var (pts, argb) in g.Shades)
                {
                    if (pts.Count < 3) continue;
                    sp.Color = new SKColor(argb);
                    using var path = new SKPath();
                    path.MoveTo((float)pts[0].X * scale, Y(pts[0].Y));
                    for (int i = 1; i < pts.Count; i++) path.LineTo((float)pts[i].X * scale, Y(pts[i].Y));
                    path.Close();
                    c.DrawPath(path, sp);
                }
        using var thick = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = (float)g.WidthOf(LineKind.Thick) * scale, IsAntialias = true, StrokeCap = SKStrokeCap.Square };
        using var thin = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = (float)g.WidthOf(LineKind.Thin) * scale, IsAntialias = true, StrokeCap = SKStrokeCap.Butt };
        // k164: отрезок штриховой и штрихпунктирной линии — со штрихами (прежде все отрезки — сплошные)
        foreach (var l in g.Lines.OrderBy(l => l.Kind))
        {
            if (LineWidths.Dash(l.Kind) is { } ld)
            {
                using var dp = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = (float)g.WidthOf(l.Kind) * scale, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(ld.Select(d => (float)d * scale).ToArray(), 0) };
                c.DrawLine((float)l.X1 * scale, Y(l.Y1), (float)l.X2 * scale, Y(l.Y2), dp);
            }
            else c.DrawLine((float)l.X1 * scale, Y(l.Y1), (float)l.X2 * scale, Y(l.Y2), l.Kind == LineKind.Thick ? thick : thin);
        }
        // ломаные видов: штрихи — по всей ломаной (ГОСТ 2.303)
        foreach (var grp in g.Polylines.GroupBy(p => p.Kind))
        {
            var dash = LineWidths.Dash(grp.Key);
            using var paint = new SKPaint
            {
                Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = (float)g.WidthOf(grp.Key) * scale, IsAntialias = true,
                StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                PathEffect = dash != null ? SKPathEffect.CreateDash(dash.Select(d => (float)d * scale).ToArray(), 0) : null,
            };
            foreach (var pl in grp)
            {
                if (pl.Points.Count < 2) continue;
                using var path = new SKPath();
                path.MoveTo((float)pl.Points[0].X * scale, Y(pl.Points[0].Y));
                for (int i = 1; i < pl.Points.Count; i++) path.LineTo((float)pl.Points[i].X * scale, Y(pl.Points[i].Y));
                c.DrawPath(path, paint);
            }
        }
        using (var fill = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = true })
            foreach (var poly in g.Fills)
            {
                if (poly.Count < 3) continue;
                using var path = new SKPath();
                path.MoveTo((float)poly[0].X * scale, Y(poly[0].Y));
                for (int i = 1; i < poly.Count; i++) path.LineTo((float)poly[i].X * scale, Y(poly[i].Y));
                path.Close();
                c.DrawPath(path, fill);
            }
        foreach (var (cx, cy, r, k) in g.Circles)
        {
            using var cp = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = (float)g.WidthOf(k) * scale, IsAntialias = true,
                PathEffect = LineWidths.Dash(k) is { } dsh ? SKPathEffect.CreateDash(dsh.Select(d => (float)d * scale).ToArray(), 0) : null };
            c.DrawCircle((float)cx * scale, Y(cy), (float)r * scale, cp);
        }
        float ratio = CapRatio();
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        foreach (var t in g.Texts)
        {
            if (string.IsNullOrEmpty(t.Text)) continue;
            using var font = new SKFont(Gost, (float)(t.Height / ratio) * scale);
            var al = t.Align switch { TextAlign.Center => SKTextAlign.Center, TextAlign.Right => SKTextAlign.Right, _ => SKTextAlign.Left };
            c.Save();
            c.Translate((float)t.X * scale, Y(t.Y));
            if (t.Angle != 0) c.RotateDegrees((float)-t.Angle);
            c.DrawText(t.Text, 0, 0, al, font, ink);
            c.Restore();
        }
    }

    /// <summary>Векторный PDF: страница на лист, размер страницы — размер листа.</summary>
    public static void WritePdf(Stream output, IEnumerable<SheetGraphics> pages, string title = "")
    {
        var meta = new SKDocumentPdfMetadata { Title = title, Creator = "Parametric3D", Producer = "Parametric3D", RasterDpi = 300 };
        using var doc = SKDocument.CreatePdf(output, meta) ?? throw new InvalidOperationException("PDF backend is not available");
        foreach (var g in pages)
        {
            var c = doc.BeginPage((float)g.Width * PtPerMm, (float)g.Height * PtPerMm);
            Draw(c, g, PtPerMm, white: false);
            doc.EndPage();
            Interlocked.Increment(ref PdfPages);
        }
        doc.Close();
    }

    public static void WritePdf(string path, IEnumerable<SheetGraphics> pages, string title = "")
    {
        using var fs = File.Create(path);
        WritePdf(fs, pages, title);
    }

    /// <summary>Картинка листа (PNG) для окна: pxPerMm точек на мм.</summary>
    public static byte[] RenderPng(SheetGraphics g, float pxPerMm)
    {
        int w = Math.Max(1, (int)Math.Ceiling(g.Width * pxPerMm)), h = Math.Max(1, (int)Math.Ceiling(g.Height * pxPerMm));
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) Draw(c, g, pxPerMm);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
