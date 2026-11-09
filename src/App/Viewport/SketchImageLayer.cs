using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using OpenTK.Mathematics;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Viewport;

/// <summary>
/// k184 (замечание 09.11, 06:35, п. 3): ИЗОБРАЖЕНИЯ АКТИВНОГО ЭСКИЗА поверх вида (полупрозрачно — линии эскиза видны сквозь
/// него). Видны только при правке своего эскиза. Выбранное изображение — с ручками: углы — пропорциональный размер, середины
/// вертикальных сторон — ширина, горизонтальных — высота, ручка за правым верхним углом — поворот; перетаскивание внутри —
/// перенос. Слой ловит мышь только над ручками и над изображением (над изображением — когда не выбран инструмент рисования:
/// иначе щелчок идёт инструменту, обрисовка поверх картинки не мешает).
/// </summary>
public sealed class SketchImageLayer : Control, ICustomHitTest
{
    private readonly ModelViewport _view;
    private static readonly ConditionalWeakTable<byte[], Bitmap?> _bitmaps = new();

    /// <summary>Выбранное изображение (ручки показаны); null — нет.</summary>
    public SkImage? Selected { get; private set; }
    /// <summary>Выбор изменился (свойства в панели).</summary>
    public event Action? SelectionChanged;
    /// <summary>Правка перетаскиванием закончена (шаг отмены эскиза).</summary>
    public event Action? Edited;
    /// <summary>Идёт перетаскивание (свойства обновляются).</summary>
    public event Action? Dragging;

    public const double HandlePx = 8, RotateOffsetPx = 26;
    /// <summary>k185: прилипание поворота к прямым углам (градусы); P3D_SKIMGSNAP=0 — без прилипания.</summary>
    public const double RightSnapDeg = 5;
    static readonly bool RightSnapOff = Environment.GetEnvironmentVariable("P3D_SKIMGSNAP") == "0";

    public SketchImageLayer(ModelViewport view)
    {
        _view = view;
        ClipToBounds = true;
        view.FrameDrawn += InvalidateVisual;
    }

    public void Select(SkImage? im)
    {
        if (ReferenceEquals(im, Selected)) return;
        Selected = im; InvalidateVisual(); SelectionChanged?.Invoke();
    }

    public static Bitmap? BitmapOf(SkImage im)
    {
        if (im.Data.Length == 0) return null;
        return _bitmaps.GetValue(im.Data, d => { try { using var ms = new MemoryStream(d); return new Bitmap(ms); } catch { return null; } });
    }

    private static readonly ConditionalWeakTable<ModelViewport.ImagePixels, Bitmap?> _inverted = new();

    /// <summary>k185: инвертированная картинка для показа без OpenGL.</summary>
    public static Bitmap? InvertedOf(SkImage im)
    {
        if (ModelViewport.PixelsOf(im) is not { } px) return null;
        return _inverted.GetValue(px, p =>
        {
            try
            {
                var wb = new WriteableBitmap(new PixelSize(p.W, p.H), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormats.Rgba8888, Avalonia.Platform.AlphaFormat.Premul);
                var inv = (byte[])p.Rgba.Clone();
                for (int i = 0; i < inv.Length; i += 4) { byte a = inv[i + 3]; inv[i] = (byte)(a - inv[i]); inv[i + 1] = (byte)(a - inv[i + 1]); inv[i + 2] = (byte)(a - inv[i + 2]); }
                using (var fb = wb.Lock())
                    for (int y = 0; y < p.H; y++) System.Runtime.InteropServices.Marshal.Copy(inv, y * p.W * 4, fb.Address + y * fb.RowBytes, p.W * 4);
                return wb;
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// k185 (замечание 09.11, 12:02, п. 1): КОЛЕСО МЫШИ НАД ИЗОБРАЖЕНИЕМ — масштаб вида: слой ловит мышь над картинкой (для
    /// переноса), и колесо до вида не доходило. Передаётся виду в той же точке (слой и вид совпадают по месту).
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _view.WheelAt(e.GetPosition(_view), e.Delta, e.KeyModifiers);
        e.Handled = true;
        InvalidateVisual();
    }

    private Sketch? Sk => _view.ActiveSketch;

    private Point? Scr(Sketch sk, Vector2d p) => _view.Project(sk.Plane.ToWorld(p));

    /// <summary>Точки ручек выбранного изображения на экране: 8 ручек размера (fu, fv) и ручка поворота.</summary>
    public List<(int Fu, int Fv, Point P)> Handles(out Point? rotate)
    {
        rotate = null;
        var res = new List<(int, int, Point)>();
        if (Sk is not { } sk || Selected is not { } im || !sk.Images.Contains(im)) return res;
        foreach (var (fu, fv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1), (0, -1), (1, 0), (0, 1), (-1, 0) })
            if (Scr(sk, im.At(fu, fv)) is { } q) res.Add((fu, fv, q));
        if (Scr(sk, im.At(1, 1)) is { } c && Scr(sk, im.Center) is { } o)
        {
            var d = c - o; double l = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (l > 1e-6) rotate = c + d / l * RotateOffsetPx;
        }
        return res;
    }

    private bool Inside(Sketch sk, SkImage im, Point p)
    {
        if (_view.ScreenToSketch(p) is not { } q) return false;
        var l = im.Local(q);
        return Math.Abs(l.X) <= 1 && Math.Abs(l.Y) <= 1;
    }

    private enum Mode { None, Move, Size, Rotate }
    private Mode _mode; private int _fu, _fv;
    private SkImage? _orig; private Vector2d _start;

    private (Mode M, int Fu, int Fv, SkImage? Im) HitWhat(Point p)
    {
        if (Sk is not { } sk) return (Mode.None, 0, 0, null);
        if (Selected != null && sk.Images.Contains(Selected))
        {
            var hs = Handles(out var rot);
            if (rot is { } r && Dist(r, p) <= HandlePx) return (Mode.Rotate, 0, 0, Selected);
            foreach (var (fu, fv, q) in hs) if (Dist(q, p) <= HandlePx * 0.75) return (Mode.Size, fu, fv, Selected);
        }
        if (_view.Tool != null) return (Mode.None, 0, 0, null);
        // k185 (замечание 09.11, 15:48): картинка — фон: размер, значок связи, точка и линия эскиза над ней выбираются и
        // подсвечиваются первыми (слой мыши не берёт). Мутация P3D_SKIMGBELOW=0.
        if (!BelowOff && SketchUnder(p)) return (Mode.None, 0, 0, null);
        for (int i = sk.Images.Count - 1; i >= 0; i--)
            if (Inside(sk, sk.Images[i], p)) return (Mode.Move, 0, 0, sk.Images[i]);
        return (Mode.None, 0, 0, null);
    }

    static readonly bool BelowOff = Environment.GetEnvironmentVariable("P3D_SKIMGBELOW") == "0";
    /// <summary>Под точкой — что-то из эскиза (размер, значок связи, точка, линия).</summary>
    bool SketchUnder(Point p)
    {
        try
        {
            if (_view.DimensionAt(p) != null) return true;
            if (_view.ConstraintGlyphAt(p) is { IsEmpty: false }) return true;
            var r = _view.PickForTest(p);
            return !r.IsEmpty && r.Owner is Sketch && r.Kind is Parametric3D.Core.PickKind.SketchPoint or Parametric3D.Core.PickKind.SketchCurve or Parametric3D.Core.PickKind.SketchDimension or Parametric3D.Core.PickKind.SketchConstraint;
        }
        catch { return false; }
    }

    private static double Dist(Point a, Point b) { var d = a - b; return Math.Sqrt(d.X * d.X + d.Y * d.Y); }

    public bool HitTest(Point point) => _mode != Mode.None || HitWhat(point).M != Mode.None;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        var (m, fu, fv, im) = HitWhat(p);
        if (m == Mode.None || im == null || _view.ScreenToSketch(p) is not { } q) return;
        Select(im);
        _mode = m; _fu = fu; _fv = fv; _orig = im.Copy(); _start = q;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_mode == Mode.None)
        {
            var (m, fu, fv, _) = HitWhat(p);
            Cursor = m switch
            {
                Mode.Move => new Cursor(StandardCursorType.SizeAll),
                Mode.Rotate => new Cursor(StandardCursorType.Hand),
                Mode.Size => new Cursor(fu != 0 && fv != 0 ? StandardCursorType.TopLeftCorner : fu != 0 ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth),
                _ => Cursor.Default,
            };
            return;
        }
        if (Selected is not { } im || _orig is not { } o || _view.ScreenToSketch(p) is not { } q) return;
        Apply(im, o, _mode, _fu, _fv, _start, q, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        InvalidateVisual(); _view.InvalidateOverlay();
        Dragging?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == Mode.None) return;
        _mode = Mode.None; _orig = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        Edited?.Invoke();
    }

    /// <summary>
    /// Правка ручкой: перенос; угол — пропорционально от противоположного угла; середина стороны — один размер от
    /// противоположной стороны; поворот — вокруг центра (Shift — шагом 15°). Размер не меньше 0.01 мм.
    /// </summary>
    public static void Apply(SkImage im, SkImage o, int mode, int fu, int fv, Vector2d start, Vector2d cur, bool snap)
        => Apply(im, o, (Mode)mode, fu, fv, start, cur, snap);

    private static void Apply(SkImage im, SkImage o, Mode mode, int fu, int fv, Vector2d start, Vector2d cur, bool snap)
    {
        var (u, v) = o.Axes;
        const double min = 0.01;
        switch (mode)
        {
            case Mode.Move:
                im.Center = o.Center + (cur - start);
                break;
            case Mode.Size when fu != 0 && fv != 0:
            {
                var opp = o.At(-fu, -fv);
                var diag = o.At(fu, fv) - opp; double dl = diag.Length;
                double s = dl > 0 ? Vector2d.Dot(cur - opp, diag) / (dl * dl) : 1;
                s = Math.Max(s, min / Math.Max(1e-9, Math.Min(o.Width, o.Height)));
                im.Width = o.Width * s; im.Height = o.Height * s;
                im.Center = opp + u * (fu * im.Width / 2) + v * (fv * im.Height / 2);
                break;
            }
            case Mode.Size when fu != 0:
            {
                var opp = o.At(-fu, 0);
                im.Width = Math.Max(min, Vector2d.Dot(cur - opp, u) * fu);
                im.Center = opp + u * (fu * im.Width / 2);
                break;
            }
            case Mode.Size when fv != 0:
            {
                var opp = o.At(0, -fv);
                im.Height = Math.Max(min, Vector2d.Dot(cur - opp, v) * fv);
                im.Center = opp + v * (fv * im.Height / 2);
                break;
            }
            case Mode.Rotate:
            {
                double a0 = Math.Atan2(start.Y - o.Center.Y, start.X - o.Center.X), a1 = Math.Atan2(cur.Y - o.Center.Y, cur.X - o.Center.X);
                double a = o.Angle + (a1 - a0) * 180 / Math.PI;
                if (snap) a = Math.Round(a / 15) * 15;
                // k185 (замечание 09.11, 15:15, п. 2): прилипание к 0°, 90°, 180°, 270° в пределах 5°
                else if (!RightSnapOff) { double m = Math.Round(a / 90) * 90; if (Math.Abs(a - m) <= RightSnapDeg) a = m; }
                a %= 360; if (a > 180) a -= 360; if (a <= -180) a += 360;
                im.Angle = a;
                break;
            }
        }
    }

    /// <summary>Разбор: сколько изображений нарисовано в последнем кадре.</summary>
    public int LastDrawn { get; private set; }

    public override void Render(DrawingContext ctx)
    {
        LastDrawn = 0;
        if (Sk is not { } sk || sk.Images.Count == 0)
        {
            if (Selected != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Selected != null && (_view.ActiveSketch is not { } s0 || !s0.Images.Contains(Selected))) Select(null); });
            return;
        }
        // k185: выбор, которого нет в эскизе (отмена, удаление), снимается ПОСЛЕ кадра: смена панели свойств во время отрисовки
        // давала «Visual was invalidated during rendering pass», и окно переставало обновляться
        bool stale = Selected != null && !sk.Images.Contains(Selected);
        if (stale) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Selected != null && _view.ActiveSketch is { } s2 && !s2.Images.Contains(Selected)) Select(null); });
        foreach (var im in sk.Images)
        {
            if (Scr(sk, im.At(-1, 1)) is not { } tl || Scr(sk, im.At(1, 1)) is not { } tr || Scr(sk, im.At(-1, -1)) is not { } bl) continue;
            // k185 (п. 3): с OpenGL картинку рисует кадр вида — под линиями и точками эскиза; здесь — рамка и ручки
            var bmp = _view.GlSketchImages ? null : im.Invert ? InvertedOf(im) : BitmapOf(im);
            if (bmp != null && im.PixelWidth > 0 && im.PixelHeight > 0)
            {
                double pw = bmp.Size.Width, ph = bmp.Size.Height;
                var m = new Matrix((tr.X - tl.X) / pw, (tr.Y - tl.Y) / pw, (bl.X - tl.X) / ph, (bl.Y - tl.Y) / ph, tl.X, tl.Y);
                using (ctx.PushOpacity(Math.Clamp(im.Opacity, 0.05, 1)))
                using (ctx.PushTransform(m))
                    ctx.DrawImage(bmp, new Rect(0, 0, pw, ph));
            }
            if (Scr(sk, im.At(1, -1)) is { } br && (ReferenceEquals(im, Selected) || !_view.GlSketchImages))
            {
                var g = new StreamGeometry();
                using (var gc = g.Open()) { gc.BeginFigure(tl, false); gc.LineTo(tr); gc.LineTo(br); gc.LineTo(bl); gc.EndFigure(true); }
                var col = ReferenceEquals(im, Selected) ? Color.FromRgb(255, 140, 0) : Color.FromArgb(120, 128, 128, 128);
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(col), ReferenceEquals(im, Selected) ? 1.5 : 1, ReferenceEquals(im, Selected) ? null : DashStyle.Dash), g);
            }
            LastDrawn++;
        }
        if (Selected == null || stale) return;
        var hs = Handles(out var rot);
        var fill = new SolidColorBrush(Colors.White); var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 140, 0)), 1.5);
        foreach (var (_, _, q) in hs) ctx.DrawRectangle(fill, pen, new Rect(q.X - HandlePx / 2, q.Y - HandlePx / 2, HandlePx, HandlePx));
        if (rot is { } r && hs.Count > 2)
        {
            ctx.DrawLine(pen, hs[2].P, r);
            ctx.DrawEllipse(fill, pen, r, HandlePx / 2 + 1, HandlePx / 2 + 1);
        }
    }
}
