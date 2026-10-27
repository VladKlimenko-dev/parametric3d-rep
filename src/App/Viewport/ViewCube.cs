using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpenTK.Mathematics;
using Parametric3D.App.Icons;
using Parametric3D.Render;
using APoint = Avalonia.Point;

namespace Parametric3D.App.Viewport;

/// <summary>
/// КУБ ВИДА ПО ОБРАЗЦУ 3ds Max (ТЗ_UI, раздел 2.1, UI-45).
///
/// Куб [-1, 1]³ в осях модели (Y вверх, перед — +Z) рисуется в повороте камеры. Каждая грань делится на 3×3 области:
/// середина — грань, полосы у сторон — рёбра, углы — вершины куба. Область задаётся направлением D с компонентами
/// {-1, 0, 1} (26 направлений): клик — взгляд по −D (грань — по нормали, ребро — по биссектрисе, угол — изометрия).
/// Перетаскивание куба — свободное вращение; кольца компаса — вращение вокруг вертикали. Кнопка «Домой» — исходный вид;
/// в виде по нормали к грани — изогнутые стрелки (поворот на 90° вокруг взгляда) и треугольники у сторон (к соседней
/// грани). Переходы — плавные (ModelViewport.AnimateTo), центр и масштаб не меняются.
/// </summary>
public sealed class ViewCube : Control, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>Куб принимает мышь всей площадью кнопок и граней (не только по нарисованным линиям), п. 11.</summary>
    bool Avalonia.Rendering.ICustomHitTest.HitTest(APoint p)
    {
        var h = HitTest(p);
        return h.Extra != Extra.None || h.Region != default;
    }

    public ModelViewport? Viewport { get; set; }

    private const double Margin = 1.25;      // доля полуразмера контрола под куб с кольцом
    private const double Band = 0.26;        // ширина полосы ребра (доля стороны грани в [-1, 1] → 2·Band)
    private const double RingR = 1.85, RingW = 0.22;

    private Vector3i _hover;                 // наведённая область (0 — нет)
    private enum Extra { None, Home, RotCcw, RotCw, Up, Down, Left, Right, Ring }
    private Extra _hoverExtra;
    private bool _dragging, _pressed;
    private Extra _pressedExtra;
    private Vector3i _pressedRegion;
    private APoint _down, _last;

    public static readonly string[] FaceLabels = { "ПРАВО", "ЛЕВО", "ВЕРХ", "НИЗ", "ПЕРЕД", "ЗАД" };

    public ViewCube()
    {
        Width = 150; Height = 150;
        ClipToBounds = false;
        IconProvider.ThemeChanged += InvalidateVisual;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    // ------------------------------------------------------------------ геометрия

    private readonly record struct Face(Vector3 N, Vector3 U, Vector3 V, string Label);

    private static readonly Face[] Faces =
    {
        new(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY, FaceLabels[0]),
        new(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, FaceLabels[1]),
        new(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, FaceLabels[2]),
        new(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, FaceLabels[3]),
        new(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, FaceLabels[4]),
        new(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY, FaceLabels[5]),
    };

    private Quaternion Rot => Viewport?.Camera.State.Rotation ?? Quaternion.Identity;
    private double Scale => Math.Min(Bounds.Width, Bounds.Height) / 2 / (RingR + RingW + 0.15);
    private APoint Center => new(Bounds.Width / 2, Bounds.Height / 2 + 6);

    private Vector3 ToCam(Vector3 p) => Vector3.Transform(p, Rot);
    private APoint Proj(Vector3 p)
    {
        var c = ToCam(p);
        return new APoint(Center.X + c.X * Scale, Center.Y - c.Y * Scale);
    }

    /// <summary>Многоугольник области (i, j ∈ 0..2) грани в мировых координатах.</summary>
    private static Vector3[] Cell(Face f, int i, int j)
    {
        double[] t = { -1, -1 + 2 * Band, 1 - 2 * Band, 1 };
        float u0 = (float)t[i], u1 = (float)t[i + 1], v0 = (float)t[j], v1 = (float)t[j + 1];
        return new[] { f.N + f.U * u0 + f.V * v0, f.N + f.U * u1 + f.V * v0, f.N + f.U * u1 + f.V * v1, f.N + f.U * u0 + f.V * v1 };
    }

    private static Vector3i RegionOf(Face f, int i, int j)
    {
        var d = f.N + f.U * (i - 1) + f.V * (j - 1);
        return new Vector3i((int)MathF.Round(d.X), (int)MathF.Round(d.Y), (int)MathF.Round(d.Z));
    }

    private bool Visible(Face f) => ToCam(f.N).Z > 0.02f;

    /// <summary>Взгляд по нормали к грани: ось, к которой ближе всего направление взгляда.</summary>
    private Face? AlignedFace()
    {
        if (Viewport == null) return null;
        foreach (var f in Faces) if (ToCam(f.N).Z > 0.9995f) return f;
        return null;
    }

    // ------------------------------------------------------------------ рисование

    private IBrush Res(string key, string fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brush.Parse(fallback);

    public override void Render(DrawingContext dc)
    {
        if (Viewport == null) return;
        var faceBrush = Res("P3.CubeFace", "#C9CED6");
        var edgePen = new Pen(Res("P3.CubeEdge", "#5B6270"), 1);
        var textBrush = Res("P3.CubeText", "#2A2E36");
        var ringBrush = Res("P3.CubeRing", "#8C93A0");
        var accent = Res("P3.Accent", "#2F8CFF");
        var hoverFill = new SolidColorBrush(((ISolidColorBrush)accent).Color, 0.55);
        var dim = Res("P3.TextDim", "#9AA1AD");

        // кольцо компаса (под кубом, в плоскости y = −1)
        DrawRing(dc, ringBrush, _hoverExtra == Extra.Ring ? hoverFill : null);

        // грани: дальние раньше ближних
        var vis = Faces.Where(Visible).OrderBy(f => ToCam(f.N).Z).ToList();
        foreach (var f in vis)
        {
            float lit = Math.Clamp(ToCam(f.N).Z, 0, 1);
            var c = ((ISolidColorBrush)faceBrush).Color;
            double k = 0.72 + 0.28 * lit;
            var fill = new SolidColorBrush(Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k)));
            dc.DrawGeometry(fill, edgePen, Poly(new[] { f.N - f.U - f.V, f.N + f.U - f.V, f.N + f.U + f.V, f.N - f.U + f.V }));
            if (_hover != default)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        if (RegionOf(f, i, j) == _hover) dc.DrawGeometry(hoverFill, null, Poly(Cell(f, i, j)));
            DrawLabel(dc, f, textBrush);
        }

        DrawButtons(dc, dim, accent);
    }

    private StreamGeometry Poly(IReadOnlyList<Vector3> pts)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(Proj(pts[0]), true);
        for (int i = 1; i < pts.Count; i++) ctx.LineTo(Proj(pts[i]));
        ctx.EndFigure(true);
        return g;
    }

    private void DrawLabel(DrawingContext dc, Face f, IBrush brush)
    {
        // аффинное отображение плоскости грани (u, v) на экран; текст — в единицах грани
        var o = Proj(f.N); var pu = Proj(f.N + f.U); var pv = Proj(f.N + f.V);
        var ux = pu - o; var vy = pv - o;
        double len = 0.42;                 // высота строки в единицах грани
        var ft = new FormattedText(Loc.L(f.Label), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 100, brush);
        double sx = Math.Min(len, 1.62 / (ft.Width / 100)) / 100;          // высота len, ширина ≤ 1.62 единицы
        var m = new Matrix(ux.X * sx, ux.Y * sx, -vy.X * sx, -vy.Y * sx, o.X, o.Y);
        using (dc.PushTransform(m))
            dc.DrawText(ft, new APoint(-ft.Width / 2, -ft.Height / 2));
    }

    private void DrawRing(DrawingContext dc, IBrush brush, IBrush? hover)
    {
        const int n = 72;
        var outer = new APoint[n]; var inner = new APoint[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            var d = new Vector3((float)Math.Cos(a), 0, (float)Math.Sin(a));
            outer[i] = Proj(new Vector3(0, -1, 0) + d * (float)(RingR + RingW / 2));
            inner[i] = Proj(new Vector3(0, -1, 0) + d * (float)(RingR - RingW / 2));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);
            ctx.BeginFigure(outer[0], true); for (int i = 1; i < n; i++) ctx.LineTo(outer[i]); ctx.EndFigure(true);
            ctx.BeginFigure(inner[0], true); for (int i = 1; i < n; i++) ctx.LineTo(inner[i]); ctx.EndFigure(true);
        }
        dc.DrawGeometry(hover ?? new SolidColorBrush(((ISolidColorBrush)brush).Color, 0.45), null, g);
        foreach (var (lbl, dir) in new[] { ("N", -Vector3.UnitZ), ("S", Vector3.UnitZ), ("E", Vector3.UnitX), ("W", -Vector3.UnitX) })
        {
            var p = Proj(new Vector3(0, -1, 0) + dir * (float)RingR);
            var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 11, Res("P3.Text", "#E6E8EC"));
            dc.DrawText(ft, new APoint(p.X - ft.Width / 2, p.Y - ft.Height / 2));
        }
    }

    private Rect HomeRect => new(4, 4, 24, 24);
    private Rect CcwRect => new(Bounds.Width - 50, 2, 22, 22);
    private Rect CwRect => new(Bounds.Width - 26, 2, 22, 22);

    private (Extra E, APoint P)[] Triangles()
    {
        if (AlignedFace() is not { } f) return Array.Empty<(Extra, APoint)>();
        // треугольники у середин сторон грани (за её пределами)
        var c = Center; double r = 1.0 * Scale + 14;
        return new[] { (Extra.Up, new APoint(c.X, c.Y - r)), (Extra.Down, new APoint(c.X, c.Y + r)),
                       (Extra.Left, new APoint(c.X - r, c.Y)), (Extra.Right, new APoint(c.X + r, c.Y)) };
    }

    private void DrawButtons(DrawingContext dc, IBrush dim, IBrush accent)
    {
        // «Домой»
        DrawIconOr(dc, "viewcube_home_but.svg", HomeRect, _hoverExtra == Extra.Home ? accent : dim, HouseGeometry(HomeRect));
        if (AlignedFace() == null) return;
        DrawIconOr(dc, "viewcube_rot_ccw_but.svg", CcwRect, _hoverExtra == Extra.RotCcw ? accent : dim, ArcArrow(CcwRect, false));
        DrawIconOr(dc, "viewcube_rot_cw_but.svg", CwRect, _hoverExtra == Extra.RotCw ? accent : dim, ArcArrow(CwRect, true));
        foreach (var (e, p) in Triangles())
        {
            // u13h: значок стрелки (один, смотрит вверх) поворачивается кодом к своей стороне
            if (IconProvider.Get("viewcube_arrow_ico.svg") is { } arrow)
            {
                double deg = e switch { Extra.Up => 0, Extra.Right => 90, Extra.Down => 180, _ => 270 };
                var rr = new Rect(p.X - 8, p.Y - 8, 16, 16);
                using (dc.PushTransform(Matrix.CreateTranslation(-p.X, -p.Y) * Matrix.CreateRotation(deg * Math.PI / 180) * Matrix.CreateTranslation(p.X, p.Y)))
                using (dc.PushOpacity(_hoverExtra == e ? 1.0 : 0.7))
                    arrow.Draw(dc, new Rect(arrow.Size), IconProvider.Fit(arrow.Size, rr));
                continue;
            }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double s = 6;
                var (a, b, cc) = e switch
                {
                    Extra.Up => (new APoint(p.X, p.Y - s), new APoint(p.X - s, p.Y + s * 0.6), new APoint(p.X + s, p.Y + s * 0.6)),
                    Extra.Down => (new APoint(p.X, p.Y + s), new APoint(p.X + s, p.Y - s * 0.6), new APoint(p.X - s, p.Y - s * 0.6)),
                    Extra.Left => (new APoint(p.X - s, p.Y), new APoint(p.X + s * 0.6, p.Y + s), new APoint(p.X + s * 0.6, p.Y - s)),
                    _ => (new APoint(p.X + s, p.Y), new APoint(p.X - s * 0.6, p.Y - s), new APoint(p.X - s * 0.6, p.Y + s)),
                };
                ctx.BeginFigure(a, true); ctx.LineTo(b); ctx.LineTo(cc); ctx.EndFigure(true);
            }
            dc.DrawGeometry(_hoverExtra == e ? accent : dim, null, g);
        }
    }

    private static void DrawIconOr(DrawingContext dc, string icon, Rect r, IBrush brush, Geometry fallback)
    {
        if (IconProvider.Get(icon) is { } img) { img.Draw(dc, new Rect(img.Size), IconProvider.Fit(img.Size, r)); return; }
        dc.DrawGeometry(null, new Pen(brush, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), fallback);
    }

    private static Geometry HouseGeometry(Rect r)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        double x = r.X + 3, y = r.Y + 3, w = r.Width - 6, h = r.Height - 6;
        c.BeginFigure(new APoint(x, y + h * 0.45), false);
        c.LineTo(new APoint(x + w / 2, y)); c.LineTo(new APoint(x + w, y + h * 0.45));
        c.EndFigure(false);
        c.BeginFigure(new APoint(x + w * 0.18, y + h * 0.35), false);
        c.LineTo(new APoint(x + w * 0.18, y + h)); c.LineTo(new APoint(x + w * 0.82, y + h)); c.LineTo(new APoint(x + w * 0.82, y + h * 0.35));
        c.EndFigure(false);
        return g;
    }

    private static Geometry ArcArrow(Rect r, bool cw)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        var ctr = r.Center; double rad = r.Width / 2 - 4;
        double a0 = cw ? -150 : -30, a1 = cw ? -20 : -160;
        APoint P(double deg) => new(ctr.X + rad * Math.Cos(deg * Math.PI / 180), ctr.Y + rad * Math.Sin(deg * Math.PI / 180) + 3);
        c.BeginFigure(P(a0), false);
        c.ArcTo(P(a1), new Size(rad, rad), 0, false, cw ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
        var end = P(a1);
        double t = (a1 + (cw ? 90 : -90)) * Math.PI / 180;    // касательная
        var back = new APoint(-Math.Cos(t) * 5, -Math.Sin(t) * 5);
        var side = new APoint(-back.Y * 0.8, back.X * 0.8);
        c.EndFigure(false);
        c.BeginFigure(new APoint(end.X + back.X + side.X, end.Y + back.Y + side.Y), false);
        c.LineTo(end);
        c.LineTo(new APoint(end.X + back.X - side.X, end.Y + back.Y - side.Y));
        c.EndFigure(false);
        return g;
    }

    // ------------------------------------------------------------------ поиск под курсором

    private (Vector3i Region, Extra Extra) HitTest(APoint p)
    {
        if (HomeRect.Inflate(4).Contains(p)) return (default, Extra.Home);
        if (AlignedFace() != null)
        {
            if (CcwRect.Inflate(3).Contains(p)) return (default, Extra.RotCcw);
            if (CwRect.Inflate(3).Contains(p)) return (default, Extra.RotCw);
            foreach (var (e, tp) in Triangles())
                if (Math.Abs(p.X - tp.X) <= 9 && Math.Abs(p.Y - tp.Y) <= 9) return (default, e);
        }
        // ближняя видимая грань — первой
        foreach (var f in Faces.Where(Visible).OrderByDescending(f => ToCam(f.N).Z))
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (Inside(p, Cell(f, i, j).Select(Proj).ToArray())) return (RegionOf(f, i, j), Extra.None);
        if (OnRing(p)) return (default, Extra.Ring);
        return (default, Extra.None);
    }

    private static bool Inside(APoint p, APoint[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    private bool OnRing(APoint p)
    {
        // точка кольца: проекция в плоскость y = −1 по лучу взгляда (ортогонально)
        var inv = Quaternion.Invert(Rot);
        var right = Vector3.Transform(Vector3.UnitX, inv);
        var up = Vector3.Transform(Vector3.UnitY, inv);
        var fwd = Vector3.Transform(-Vector3.UnitZ, inv);
        float sx = (float)((p.X - Center.X) / Scale), sy = (float)(-(p.Y - Center.Y) / Scale);
        var o = right * sx + up * sy;                         // точка на плоскости экрана через центр куба
        if (Math.Abs(fwd.Y) < 1e-4) return false;
        float t = (-1 - o.Y) / fwd.Y;
        var q = o + fwd * t;
        double r = Math.Sqrt(q.X * q.X + q.Z * q.Z);
        return Math.Abs(r - RingR) <= RingW * 0.9;
    }

    // ------------------------------------------------------------------ мышь

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_pressed && Viewport != null)
        {
            if (!_dragging && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 3) _dragging = true;
            if (_dragging)
            {
                float dx = (float)(p.X - _last.X), dy = (float)(p.Y - _last.Y);
                var cam = Viewport.Camera;
                if (_pressedExtra == Extra.Ring)
                {
                    cam.State.Rotation = cam.State.Rotation * Quaternion.FromAxisAngle(Vector3.UnitY, dx * 0.012f);
                    cam.State.Rotation.Normalize();
                }
                else cam.Orbit(dx * 2.2f, dy * 2.2f);
                Viewport.Transition.Cancel();
                Viewport.Redraw();
                InvalidateVisual();
            }
            _last = p;
            return;
        }
        var (r, x) = HitTest(p);
        if (r != _hover || x != _hoverExtra) { _hover = r; _hoverExtra = x; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_pressed) { _hover = default; _hoverExtra = Extra.None; InvalidateVisual(); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _down = _last = e.GetPosition(this);
        (_pressedRegion, _pressedExtra) = HitTest(_down);
        if (_pressedRegion == default && _pressedExtra == Extra.None) return;
        _pressed = true; _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (_dragging) { _dragging = false; return; }
        Click(_pressedRegion, _pressedExtra);
        e.Handled = true;
    }

    /// <summary>Действие куба (для проверок — без мыши).</summary>
    public void Click(Vector3i region, int extra) => Click(region, (Extra)extra);

    private void Click(Vector3i region, Extra extra)
    {
        if (Viewport == null) return;
        var q = Viewport.Camera.State.Rotation;
        switch (extra)
        {
            case Extra.Home: Viewport.GoHome(); return;
            // k123: в эскизе поворот вида на 90° поворачивает и оси эскиза («горизонтально/вертикально» — по экрану)
            case Extra.RotCcw: if (Viewport.RollSketch(ccw: true)) return; Viewport.AnimateTo(Quaternion.FromAxisAngle(Vector3.UnitZ, MathHelper.PiOver2) * q); return;
            case Extra.RotCw: if (Viewport.RollSketch(ccw: false)) return; Viewport.AnimateTo(Quaternion.FromAxisAngle(Vector3.UnitZ, -MathHelper.PiOver2) * q); return;
            case Extra.Up: Viewport.AnimateTo(Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.PiOver2) * q); return;
            case Extra.Down: Viewport.AnimateTo(Quaternion.FromAxisAngle(Vector3.UnitX, -MathHelper.PiOver2) * q); return;
            case Extra.Left: Viewport.AnimateTo(Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.PiOver2) * q); return;
            case Extra.Right: Viewport.AnimateTo(Quaternion.FromAxisAngle(Vector3.UnitY, -MathHelper.PiOver2) * q); return;
            case Extra.Ring: return;
        }
        if (region == default) return;
        Viewport.AnimateTo(ViewMath.LookFrom(new Vector3(region.X, region.Y, region.Z), Viewport.Camera.Up));
    }

    /// <summary>Перерисовать вслед за камерой.</summary>
    public void Sync() => InvalidateVisual();
}

/// <summary>Повороты камеры по направлению взгляда.</summary>
public static class ViewMath
{
    /// <summary>
    /// Поворот камеры, смотрящей на центр со стороны d (взгляд — по −d). Верх — мировой Y, спроецированный на экран;
    /// при взгляде вдоль Y — ближайшая к текущему верху камеры ось ±X/±Z (вид не переворачивается рывком).
    /// </summary>
    public static Quaternion LookFrom(Vector3 d, Vector3 currentUp)
    {
        var fwd = -d.Normalized();
        Vector3 up;
        if (MathF.Abs(fwd.Y) > 0.999f)
        {
            var cands = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ };
            up = cands.OrderByDescending(c => Vector3.Dot(c, currentUp)).First();
        }
        else up = Vector3.UnitY;
        return FromForwardUp(fwd, up);
    }

    /// <summary>Поворот «мир → камера», при котором камера смотрит по fwd, а верх экрана — up (ортогонализуется).</summary>
    public static Quaternion FromForwardUp(Vector3 fwd, Vector3 up)
    {
        var f = fwd.Normalized();
        var r = Vector3.Cross(f, up).Normalized();
        var u = Vector3.Cross(r, f);
        // строки матрицы поворота «мир → камера»: r, u, −f (камера смотрит по −Z)
        var m = new Matrix3(r.X, r.Y, r.Z, u.X, u.Y, u.Z, -f.X, -f.Y, -f.Z);   // проверено: Top = Rx(+90°), как Camera.OrientationQuat
        var q = Quaternion.FromMatrix(m);
        q.Normalize();
        return q;
    }
}
