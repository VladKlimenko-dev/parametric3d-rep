using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;

namespace Parametric3D.Views;

/// <summary>
/// Инструмент эскиза — конечный автомат построения одного примитива.
///
/// В старом коде вся эта логика жила ветвлениями внутри обработчиков мыши
/// вида, и добавление нового примитива означало правку большого switch.
/// Здесь каждый инструмент — отдельный небольшой класс; вид ничего о них
/// не знает, кроме интерфейса.
/// </summary>
public interface ISketchTool
{
    string Title { get; }
    /// <returns>true — событие поглощено инструментом.</returns>
    bool OnMouseDown(GL3DView view, MouseEventArgs e);
    bool OnMouseDrag(GL3DView view, MouseEventArgs e) => false;
    void OnMouseUp(GL3DView view, MouseEventArgs e, bool wasDrag) { }
    bool OnMouseHover(GL3DView view, MouseEventArgs e);
    void Cancel(GL3DView view);
    /// <summary>Превью текущего построения.</summary>
    void DrawPreview(GL3DView view, LineBatch batch, PointBatch points);
    /// <summary>Инструмент завершил построение и должен быть снят.</summary>
    bool Finished { get; }
    /// <summary>Инструмент продолжает строить цепочку (LineLoop) после каждого сегмента.</summary>
    bool Continuous { get; }
    /// <summary>Свежий экземпляр того же инструмента — для цепочного режима.</summary>
    ISketchTool Restart();
}

/// <summary>Общая часть: захват точки с привязкой к сетке и существующим точкам.</summary>
public abstract class SketchToolBase : ISketchTool
{
    public abstract string Title { get; }
    public bool Finished { get; protected set; }
    public virtual bool Continuous => false;

    protected readonly List<Vector2d> Pts = new();
    protected Vector2d Cursor;
    /// <summary>Точка эскиза под курсором — к ней автоматически ставится совпадение.</summary>
    protected SkPoint? SnappedPoint;

    protected SkRole Role = SkRole.Object;
    public SkRole DrawRole { get => Role; set => Role = value; }

    public abstract bool OnMouseDown(GL3DView view, MouseEventArgs e);

    public virtual ISketchTool Restart() => this;

    public virtual bool OnMouseHover(GL3DView view, MouseEventArgs e)
    {
        Cursor = Capture(view, e);
        return true;
    }

    public virtual void Cancel(GL3DView view) { Pts.Clear(); Finished = true; }

    public virtual void DrawPreview(GL3DView view, LineBatch batch, PointBatch points) { }

    /// <summary>Точка под курсором с учётом привязок.</summary>
    protected Vector2d Capture(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch;
        Vector2d p = view.ScreenToSketch(e.X, e.Y);
        SnappedPoint = null;
        if (sk == null) return p;

        // 1) привязка к существующим точкам (в пределах 8 пикселей)
        if (view.SnapToItems)
        {
            double tol = view.PixelSize * Core.Settings.Current.SnapTolerancePx;
            double best = tol;
            foreach (var q in sk.AllPoints())
            {
                double d = (q.P - p).Length;
                if (d < best) { best = d; SnappedPoint = q; }
            }
            if (SnappedPoint != null) return SnappedPoint.P;
        }

        // 2) привязка к сетке
        return sk.SnapToGrid ? sk.SnapPoint(p) : p;
    }

    /// <summary>Ставит совпадение, если точка была поймана привязкой.</summary>
    protected static void LinkIfSnapped(Sketch sk, SkPoint created, SkPoint? snapped)
    {
        if (snapped != null && !ReferenceEquals(snapped, created))
            sk.AddConstraint(ConstraintType.Coincident, created, snapped);
    }

    protected static Vector3 W(GL3DView v, Vector2d p)
    {
        var pl = v.ActiveSketch!.Plane;
        Vector3d w = pl.ToWorld(p);
        return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
    }

    protected static void PreviewPolyline(GL3DView v, LineBatch b, IEnumerable<Vector2d> pts, bool closed = false)
    {
        var list = pts.Select(p => W(v, p)).ToList();
        if (list.Count >= 2) b.AddPolyline(list, LineStyle.Preview, 0, closed);
    }
}

// ------------------------------------------------------------------ точка

public sealed class PointTool : SketchToolBase
{
    public override string Title => "Point";
    public override bool Continuous => true;
    public override ISketchTool Restart() => new PointTool { DrawRole = DrawRole };

    public override bool OnMouseDown(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch; if (sk == null) return false;
        Vector2d p = Capture(view, e);
        var pt = sk.AddEntity(new SkPoint(p) { Role = Role }, autoConstrain: true);
        LinkIfSnapped(sk, pt, SnappedPoint);
        view.MarkDirty();
        return true;
    }

    public override void DrawPreview(GL3DView v, LineBatch b, PointBatch pts)
    {
        if (v.ActiveSketch != null) pts.Add(W(v, Cursor), PointStyle.Preview, 0);
    }
}

// ------------------------------------------------------------------ линия

public sealed class LineTool : SketchToolBase
{
    private readonly bool _loop;
    private SkPoint? _prevEnd;
    private SkPoint? _first;

    public LineTool(bool loop) { _loop = loop; }
    /// <summary>
    /// Заголовок зависит от роли: по нему панель узнаёт, какая кнопка
    /// сейчас нажата, и осевая линия должна отличаться от обычной.
    /// </summary>
    public override string Title => Role == SkRole.Centerline
        ? "Centerline"
        : (_loop ? "Line loop" : "Line");
    public override bool Continuous => true;
    public override ISketchTool Restart() => new LineTool(_loop) { DrawRole = DrawRole };

    public override bool OnMouseDown(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch; if (sk == null) return false;
        Vector2d p = Capture(view, e);
        var snapped = SnappedPoint;

        if (Pts.Count == 0)
        {
            Pts.Add(p);
            _prevEnd = null;
            _first = snapped;
            return true;
        }

        var a = _prevEnd ?? new SkPoint(Pts[^1]);
        var b = new SkPoint(p);
        var line = sk.AddEntity(new SkLine(a, b) { Role = Role }, autoConstrain: true);

        if (_prevEnd == null && _first != null) LinkIfSnapped(sk, a, _first);
        LinkIfSnapped(sk, b, snapped);

        _prevEnd = b;
        Pts.Add(p);
        view.MarkDirty();

        if (!_loop) { Finished = true; return true; }

        // Замыкание: щёлкнули по первой точке цепочки.
        if (snapped != null && Pts.Count > 2 && (p - Pts[0]).Length < 1e-9)
            Finished = true;
        return true;
    }

    public override void DrawPreview(GL3DView v, LineBatch b, PointBatch pts)
    {
        if (v.ActiveSketch == null || Pts.Count == 0) return;
        PreviewPolyline(v, b, new[] { Pts[^1], Cursor });
        pts.Add(W(v, Cursor), PointStyle.Preview, 0);
    }

    public override void Cancel(GL3DView view) { Pts.Clear(); _prevEnd = null; Finished = true; }
}

// --------------------------------------------------------------- прямоугольник

public sealed class RectangleTool : SketchToolBase
{
    public override string Title => "Rectangle";

    public override bool OnMouseDown(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch; if (sk == null) return false;
        Vector2d p = Capture(view, e);
        if (Pts.Count == 0) { Pts.Add(p); return true; }

        Vector2d a = Pts[0], c = p;
        var p0 = new SkPoint(a);
        var p1 = new SkPoint(new Vector2d(c.X, a.Y));
        var p2 = new SkPoint(c);
        var p3 = new SkPoint(new Vector2d(a.X, c.Y));

        var l0 = sk.AddEntity(new SkLine(p0, p1) { Role = Role });
        var l1 = sk.AddEntity(new SkLine(p1, p2) { Role = Role });
        var l2 = sk.AddEntity(new SkLine(p2, p3) { Role = Role });
        var l3 = sk.AddEntity(new SkLine(p3, p0) { Role = Role });

        // Прямоугольник задаётся ограничениями, а не координатами:
        // после этого его можно тянуть за угол и он останется прямоугольником.
        sk.AddConstraint(ConstraintType.Horizontal, l0);
        sk.AddConstraint(ConstraintType.Horizontal, l2);
        sk.AddConstraint(ConstraintType.Vertical, l1);
        sk.AddConstraint(ConstraintType.Vertical, l3);

        Finished = true;
        view.MarkDirty();
        return true;
    }

    public override void DrawPreview(GL3DView v, LineBatch b, PointBatch pts)
    {
        if (v.ActiveSketch == null || Pts.Count == 0) return;
        Vector2d a = Pts[0], c = Cursor;
        PreviewPolyline(v, b, new[]
        {
            a, new Vector2d(c.X, a.Y), c, new Vector2d(a.X, c.Y)
        }, closed: true);
    }
}

// ---------------------------------------------------------------- окружность

public sealed class CircleTool : SketchToolBase
{
    private readonly bool _threePoints;
    public CircleTool(bool threePoints) { _threePoints = threePoints; }
    public override string Title => _threePoints ? "Circle by 3 points" : "Circle by center";

    public override bool OnMouseDown(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch; if (sk == null) return false;
        Pts.Add(Capture(view, e));

        if (!_threePoints && Pts.Count == 2)
        {
            double r = (Pts[1] - Pts[0]).Length;
            if (r > Tol.Weld)
                sk.AddEntity(new SkCircle(new SkPoint(Pts[0]), r) { Role = Role }, autoConstrain: true);
            Finished = true;
            view.MarkDirty();
        }
        else if (_threePoints && Pts.Count == 3)
        {
            var arc = SkArc.FromThreePoints(Pts[0], Pts[1], Pts[2]);
            if (arc != null)
                sk.AddEntity(new SkCircle(new SkPoint(arc.Center.P), arc.Radius) { Role = Role }, autoConstrain: true);
            Finished = true;
            view.MarkDirty();
        }
        return true;
    }

    public override void DrawPreview(GL3DView v, LineBatch b, PointBatch pts)
    {
        if (v.ActiveSketch == null || Pts.Count == 0) return;
        Vector2d c; double r;
        if (!_threePoints) { c = Pts[0]; r = (Cursor - Pts[0]).Length; }
        else
        {
            if (Pts.Count == 1) { PreviewPolyline(v, b, new[] { Pts[0], Cursor }); return; }
            var arc = SkArc.FromThreePoints(Pts[0], Pts[1], Cursor);
            if (arc == null) return;
            c = arc.Center.P; r = arc.Radius;
        }
        if (r < Tol.Weld) return;

        var poly = new List<Vector2d>();
        int n = GeomUtilSegments(r);
        for (int i = 0; i <= n; i++)
        {
            double a = i * 2 * Math.PI / n;
            poly.Add(c + new Vector2d(Math.Cos(a), Math.Sin(a)) * r);
        }
        PreviewPolyline(v, b, poly);
    }

    internal static int GeomUtilSegments(double r) => Math.Clamp((int)(Math.Sqrt(r) * 12), 24, 180);
}

// --------------------------------------------------------------------- дуга

public sealed class ArcTool : SketchToolBase
{
    private readonly bool _threePoints;
    public ArcTool(bool threePoints) { _threePoints = threePoints; }
    public override string Title => _threePoints ? "Arc by 3 points" : "Arc by center";

    public override bool OnMouseDown(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch; if (sk == null) return false;
        Pts.Add(Capture(view, e));
        if (Pts.Count < 3) return true;

        SkArc? arc = _threePoints
            ? SkArc.FromThreePoints(Pts[0], Pts[1], Pts[2])
            : SkArc.FromCenterStartEnd(Pts[0], Pts[1], Pts[2]);

        if (arc != null) { arc.Role = Role; sk.AddEntity(arc, autoConstrain: true); }
        Finished = true;
        view.MarkDirty();
        return true;
    }

    public override void DrawPreview(GL3DView v, LineBatch b, PointBatch pts)
    {
        if (v.ActiveSketch == null || Pts.Count == 0) return;
        if (Pts.Count == 1) { PreviewPolyline(v, b, new[] { Pts[0], Cursor }); return; }

        SkArc? arc = _threePoints
            ? SkArc.FromThreePoints(Pts[0], Pts[1], Cursor)
            : SkArc.FromCenterStartEnd(Pts[0], Pts[1], Cursor);
        if (arc == null) return;

        var poly = new List<Vector2d>();
        arc.Tessellate(poly, true);
        PreviewPolyline(v, b, poly);
    }
}

// ------------------------------------------------------------------- эллипс

public sealed class EllipseTool : SketchToolBase
{
    public override string Title => "Ellipse";

    public override bool OnMouseDown(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch; if (sk == null) return false;
        Pts.Add(Capture(view, e));
        if (Pts.Count < 3) return true;

        Vector2d c = Pts[0];
        Vector2d major = Pts[1] - c;
        double a = major.Length;
        double rot = Math.Atan2(major.Y, major.X);
        Vector2d rel = Pts[2] - c;
        double b = Math.Abs(-rel.X * Math.Sin(rot) + rel.Y * Math.Cos(rot));

        if (a > Tol.Weld && b > Tol.Weld)
            sk.AddEntity(new SkEllipse(new SkPoint(c), a, b, rot) { Role = Role }, autoConstrain: true);

        Finished = true;
        view.MarkDirty();
        return true;
    }

    public override void DrawPreview(GL3DView v, LineBatch b, PointBatch pts)
    {
        if (v.ActiveSketch == null || Pts.Count == 0) return;
        if (Pts.Count == 1) { PreviewPolyline(v, b, new[] { Pts[0], Cursor }); return; }

        Vector2d c = Pts[0], major = Pts[1] - c;
        double ra = major.Length, rot = Math.Atan2(major.Y, major.X);
        Vector2d rel = Cursor - c;
        double rb = Math.Abs(-rel.X * Math.Sin(rot) + rel.Y * Math.Cos(rot));
        if (ra < Tol.Weld || rb < Tol.Weld) return;

        var el = new SkEllipse(new SkPoint(c), ra, rb, rot);
        var poly = new List<Vector2d>();
        el.Tessellate(poly, true);
        PreviewPolyline(v, b, poly);
    }
}

// ------------------------------------------------------------------- сплайн

public sealed class SplineTool : SketchToolBase
{
    public override string Title => "Spline";
    public override bool Continuous => true;
    public override ISketchTool Restart() => new SplineTool { DrawRole = DrawRole };

    public override bool OnMouseDown(GL3DView view, MouseEventArgs e)
    {
        var sk = view.ActiveSketch; if (sk == null) return false;

        if (e.Button == MouseButtons.Right || (e.Clicks > 1 && Pts.Count >= 2))
        {
            Commit(sk, view);
            return true;
        }

        Pts.Add(Capture(view, e));
        return true;
    }

    private void Commit(Sketch sk, GL3DView view)
    {
        if (Pts.Count >= 2)
        {
            var sp = new SkSpline(Pts.Select(p => new SkPoint(p))) { Role = Role };
            sk.AddEntity(sp, autoConstrain: true);
            view.MarkDirty();
        }
        Pts.Clear();
        Finished = true;
    }

    public override void DrawPreview(GL3DView v, LineBatch b, PointBatch pts)
    {
        if (v.ActiveSketch == null || Pts.Count == 0) return;
        var all = new List<Vector2d>(Pts) { Cursor };
        PreviewPolyline(v, b, all);
        foreach (var p in Pts) pts.Add(W(v, p), PointStyle.Preview, 0);
    }
}
