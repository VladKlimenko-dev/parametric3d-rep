using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Parametric3D.App.Icons;
using Parametric3D.Core;
using Parametric3D.Render;

namespace Parametric3D.App.Viewport;

/// <summary>
/// Значки ограничений активного эскиза — иконки con_* поверх вида.
///
/// Где стоят — <see cref="ConstraintLayout"/> (правила прежнего приложения:
/// у линии — строкой вдоль неё, у середины, снаружи замкнутого контура;
/// у узла, окружности, дуги, эллипса — строкой под узлом/центром).
/// Каждый значок — иконка на подложке с рамкой цвета состояния:
/// обычное — голубая (как зафиксированный размер), конфликт — красная,
/// под курсором — цвет наведения, выбранное — цвет выбора.
/// Сам слой мышь не ловит: попадание считает вид (<see cref="ModelViewport.ConstraintGlyphAt"/>).
/// </summary>
public sealed class ConstraintGlyphLayer : Control
{
    private readonly ModelViewport _view;

    /// <summary>Сколько значков нарисовано в последний раз (для проверок).</summary>
    public int LastDrawn { get; private set; }

    public ConstraintGlyphLayer(ModelViewport view)
    {
        _view = view;
        IsHitTestVisible = false;
        ClipToBounds = true;
        view.FrameDrawn += InvalidateVisual;
        IconProvider.ThemeChanged += InvalidateVisual;
    }

    private static Color C(OpenTK.Mathematics.Vector4 v, double a = -1) =>
        Color.FromArgb((byte)Math.Round(255 * (a < 0 ? v.W : a)), (byte)Math.Round(255 * v.X), (byte)Math.Round(255 * v.Y), (byte)Math.Round(255 * v.Z));

    public override void Render(DrawingContext ctx)
    {
        LastDrawn = 0;
        var sk = _view.ActiveSketch;
        if (sk == null || !Settings.Current.ShowConstraintGlyphs) return;

        List<ConstraintLayout.Placement> list;
        try { list = ConstraintLayout.Build(sk, _view.PixelSize); }
        catch (Exception ex) { AppLog.Write("constraint glyphs", ex); return; }
        if (list.Count == 0) return;

        var th = _view.Theme;
        var bg = new SolidColorBrush(th.Dark ? Color.FromArgb(215, 30, 34, 41) : Color.FromArgb(225, 250, 251, 253));
        var normal = new Pen(new SolidColorBrush(C(th.DimLocked, 0.9)), 1);
        var conflict = new Pen(new SolidColorBrush(C(th.Error, 1)), 1.6);
        var hover = new Pen(new SolidColorBrush(C(th.Hover, 1)), 2);
        var select = new Pen(new SolidColorBrush(C(th.Select, 1)), 2);
        var hov = _view.Selection.Hover;

        foreach (var p in list)
        {
            if (_view.Project(sk.Plane.ToWorld(p.Center)) is not { } q) continue;
            var c = p.Constraint;
            double s = ConstraintLayout.SizePx(c.Type);
            var cell = new Rect(q.X - s / 2 - 2, q.Y - s / 2 - 2, s + 4, s + 4);
            if (!cell.Intersects(Bounds.WithX(0).WithY(0))) continue;

            Pen pen = c.Conflict || c.Flagged ? conflict
                : hov.Id == c.Id ? hover
                : _view.Selection.Contains(c.Id) ? select
                : normal;
            ctx.DrawRectangle(bg, pen, cell, 3, 3);

            var img = IconProvider.Get(ConstraintLayout.IconOf(c.Type), th.Dark ? Color.FromRgb(232, 232, 232) : Color.FromRgb(30, 30, 30));
            if (img != null)
            {
                var dst = IconProvider.Fit(img.Size, new Rect(q.X - s / 2, q.Y - s / 2, s, s));
                ctx.DrawImage(img, new Rect(img.Size), dst);
            }
            LastDrawn++;
        }
    }
}
