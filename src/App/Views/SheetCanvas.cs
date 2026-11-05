using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Parametric3D.App.Services.Drawing;
using Parametric3D.Model.Drawing;
using SkiaSharp;

namespace Parametric3D.App.Views;

/// <summary>
/// k159: ПОЛЕ ЛИСТА — лист рисуется вектором (те же примитивы и та же отрисовка Skia, что и в PDF) прямо в кадр окна размером с
/// поле, с масштабом (точек на мм) и сдвигом. Прежде (k155–k158) лист каждый раз кодировался в PNG размером со весь лист при
/// увеличении ×2 (около 3700×2600 точек на A3) и раскодировался — перенос вида шёл около 1 кадра в секунду.
/// Поверх листа — подсветка (габарит вида под курсором, размер, цель привязки, выделенный элемент).
/// </summary>
public sealed class SheetCanvas : Control
{
    SheetGraphics? _g;
    WriteableBitmap? _bmp;
    /// <summary>Точек экрана (DIP) на мм листа.</summary>
    public double Zoom { get; private set; } = 2.2;
    /// <summary>Положение левого верхнего угла листа в поле (DIP).</summary>
    public Point Pan { get; private set; } = new(20, 20);
    bool _fitted;

    /// <summary>Подсветка: отрезки (мм листа), цвет, толщина (DIP).</summary>
    public readonly List<(List<(double X1, double Y1, double X2, double Y2)> Segs, SKColor Color, float Width)> Overlay = new();

    /// <summary>Разбор: кадров нарисовано, время последнего кадра (мс).</summary>
    public long Frames { get; private set; }
    public double LastFrameMs { get; private set; }

    public SheetGraphics? Graphics
    {
        get => _g;
        set { _g = value; if (!_fitted && value != null && Bounds.Width > 10) Fit(); InvalidateVisual(); }
    }

    /// <summary>k170: сменились перенос или увеличение листа.</summary>
    public event Action? ViewChanged;
    Point _lastPan; double _lastZoom;
    public SheetCanvas() { ClipToBounds = true; Focusable = true; }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_fitted && _g != null && e.NewSize.Width > 10) Fit();
        InvalidateVisual();
    }

    /// <summary>Лист целиком в поле.</summary>
    public void Fit()
    {
        if (_g == null || Bounds.Width < 10 || Bounds.Height < 10) return;
        double z = Math.Min((Bounds.Width - 40) / _g.Width, (Bounds.Height - 40) / _g.Height);
        Zoom = Math.Clamp(z, 0.2, 60);
        Pan = new Point((Bounds.Width - _g.Width * Zoom) / 2, (Bounds.Height - _g.Height * Zoom) / 2);
        _fitted = true;
        InvalidateVisual();
    }

    /// <summary>Увеличение в точке поля: точка листа под курсором остаётся на месте.</summary>
    public void ZoomAt(Point at, double factor)
    {
        double z = Math.Clamp(Zoom * factor, 0.2, 60);
        double k = z / Zoom;
        Pan = new Point(at.X - (at.X - Pan.X) * k, at.Y - (at.Y - Pan.Y) * k);
        Zoom = z; _fitted = true;
        InvalidateVisual();
    }

    public void PanBy(double dx, double dy) { Pan = new Point(Pan.X + dx, Pan.Y + dy); _fitted = true; InvalidateVisual(); }

    /// <summary>Поле (DIP) → лист (мм, Y вверх).</summary>
    public (double X, double Y) ToSheet(Point p) => ((p.X - Pan.X) / Zoom, (_g?.Height ?? 0) - (p.Y - Pan.Y) / Zoom);
    /// <summary>Лист → поле.</summary>
    public Point FromSheet(double x, double y) => new(Pan.X + x * Zoom, Pan.Y + ((_g?.Height ?? 0) - y) * Zoom);

    public override void Render(DrawingContext context)
    {
        // k170: перенос или увеличение листа — владелец переставляет элементы поверх листа (куб вида)
        if (Pan != _lastPan || Zoom != _lastZoom) { _lastPan = Pan; _lastZoom = Zoom; Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewChanged?.Invoke()); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double rs = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Ceiling(Bounds.Width * rs)), h = Math.Max(1, (int)Math.Ceiling(Bounds.Height * rs));
        if (_bmp == null || _bmp.PixelSize.Width != w || _bmp.PixelSize.Height != h)
        {
            _bmp?.Dispose();
            _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96 * rs, 96 * rs), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var fb = _bmp.Lock())
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surf = SKSurface.Create(info, fb.Address, fb.RowBytes);
            var c = surf.Canvas;
            c.Clear(new SKColor(0x5A, 0x5F, 0x66));
            if (_g != null)
            {
                c.Save();
                c.Scale((float)rs);
                c.Translate((float)Pan.X, (float)Pan.Y);
                using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 60), Style = SKPaintStyle.Fill })
                    c.DrawRect(4, 4, (float)(_g.Width * Zoom), (float)(_g.Height * Zoom), shadow);
                SheetRender.Draw(c, _g, (float)Zoom);
                float Y(double y) => (float)((_g.Height - y) * Zoom);
                foreach (var (segs, color, width) in Overlay)
                {
                    using var p = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
                    foreach (var (x1, y1, x2, y2) in segs) c.DrawLine((float)(x1 * Zoom), Y(y1), (float)(x2 * Zoom), Y(y2), p);
                }
                c.Restore();
            }
            c.Flush();
        }
        context.DrawImage(_bmp, new Rect(0, 0, w, h), new Rect(Bounds.Size));
        Frames++; LastFrameMs = sw.Elapsed.TotalMilliseconds;
    }
}
