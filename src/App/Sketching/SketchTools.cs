using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Sketching;

/// <summary>Точка курсора в эскизе: координаты на плоскости (с привязкой) и пойманная точка эскиза, если есть.</summary>
public readonly record struct SketchInput(Sketch Sketch, Vector2d P, SkPoint? Snap);

/// <summary>
/// ИНСТРУМЕНТ ЭСКИЗА — конечный автомат построения одного примитива (перенесён из прежнего приложения). Вид передаёт
/// ему щелчки и движение мыши уже в координатах плоскости эскиза с привязкой к точкам и сетке; инструмент добавляет
/// примитивы в эскиз и отдаёт предпросмотр (ломаные и точки на плоскости), который вид рисует поверх модели.
/// </summary>
public interface ISketchTool
{
    /// <summary>Имя (русское; перевод — Loc.L).</summary>
    string Title { get; }
    /// <summary>Подсказка, что делать дальше.</summary>
    string Hint { get; }
    /// <summary>Роль новых кривых: основная, вспомогательная, осевая.</summary>
    SkRole Role { get; set; }
    void Move(SketchInput i);
    /// <returns>true — эскиз изменён (нужно решить и перерисовать).</returns>
    bool Click(SketchInput i);
    /// <summary>Enter, правая кнопка или двойной щелчок: закончить начатое (сплайн, цепочку).</summary>
    bool Finish(Sketch sk);
    void Preview(List<Vector2d[]> lines, List<Vector2d> points);
    bool Finished { get; }
    /// <summary>После готового примитива инструмент остаётся (следующий начинается сразу).</summary>
    bool Continuous { get; }
    ISketchTool Restart();
}

public abstract class SketchToolBase : ISketchTool
{
    public abstract string Title { get; }
    public virtual string Hint => "Щёлкните первую точку";
    public SkRole Role { get; set; } = SkRole.Object;
    public bool Finished { get; protected set; }
    public virtual bool Continuous => true;
    public abstract ISketchTool Restart();

    protected readonly List<Vector2d> Pts = new();
    protected readonly List<SkPoint?> Snaps = new();
    protected Vector2d Cursor;

    public virtual void Move(SketchInput i) => Cursor = i.P;
    public abstract bool Click(SketchInput i);
    public virtual bool Finish(Sketch sk) { Pts.Clear(); Snaps.Clear(); Finished = true; return false; }
    public virtual void Preview(List<Vector2d[]> lines, List<Vector2d> points) { points.Add(Cursor); }

    protected T Restarted<T>(T t) where T : SketchToolBase { t.Role = Role; return t; }

    /// <summary>Совпадение с пойманной точкой (точка, созданная примитивом, сливается с существующей).</summary>
    protected static void Link(Sketch sk, SkPoint created, SkPoint? snapped)
    {
        if (snapped != null && !ReferenceEquals(snapped, created))
            sk.AddConstraint(ConstraintType.Coincident, created, snapped);
    }

    protected static Vector2d[] Circle(Vector2d c, double r)
    {
        int n = Math.Clamp((int)(Math.Sqrt(r) * 12), 32, 180);
        var a = new Vector2d[n + 1];
        for (int i = 0; i <= n; i++) { double t = i * 2 * Math.PI / n; a[i] = c + new Vector2d(Math.Cos(t), Math.Sin(t)) * r; }
        return a;
    }

    protected static Vector2d[] Tess(SkEntity e)
    {
        var l = new List<Vector2d>();
        e.Tessellate(l, true);
        return l.ToArray();
    }
}

// ------------------------------------------------------------------ точка

public sealed class PointTool : SketchToolBase
{
    public override string Title => "Точка";
    public override string Hint => "Щёлкните, чтобы поставить точку";
    public override ISketchTool Restart() => Restarted(new PointTool());

    public override bool Click(SketchInput i)
    {
        var pt = i.Sketch.AddEntity(new SkPoint(i.P) { Role = Role }, autoConstrain: true);
        Link(i.Sketch, pt, i.Snap);
        return true;
    }
}

// ------------------------------------------------------------------ отрезок и цепочка

public sealed class LineTool : SketchToolBase
{
    private SkPoint? _prevEnd, _first;
    private readonly bool _chain;
    public LineTool(bool chain = true) { _chain = chain; }

    public override string Title => Role == SkRole.Centerline ? "Осевая линия" : "Отрезок";
    public override string Hint => Pts.Count == 0 ? "Щёлкните начало отрезка" : "Щёлкните конец; Enter или правая кнопка — закончить цепочку";
    public override ISketchTool Restart() => Restarted(new LineTool(_chain));

    public override bool Click(SketchInput i)
    {
        var sk = i.Sketch;
        if (Pts.Count == 0) { Pts.Add(i.P); _first = i.Snap; _prevEnd = null; return false; }
        if ((i.P - Pts[^1]).Length < Tol.Weld) return false;
        var a = _prevEnd ?? new SkPoint(Pts[^1]);
        var b = new SkPoint(i.P);
        sk.AddEntity(new SkLine(a, b) { Role = Role }, autoConstrain: true);
        if (_prevEnd == null) Link(sk, a, _first);
        Link(sk, b, i.Snap);
        _prevEnd = b;
        Pts.Add(i.P);
        // цепочка замкнулась на первую точку — готово
        if (!_chain || Role == SkRole.Centerline || (Pts.Count > 2 && (i.P - Pts[0]).Length < 1e-9)) Finished = true;
        return true;
    }

    public override bool Finish(Sketch sk) { Pts.Clear(); _prevEnd = null; Finished = true; return false; }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (Pts.Count > 0) lines.Add(new[] { Pts[^1], Cursor });
    }
}

// --------------------------------------------------------------- прямоугольник

public sealed class RectangleTool : SketchToolBase
{
    public override string Title => "Прямоугольник";
    public override string Hint => Pts.Count == 0 ? "Щёлкните первый угол" : "Щёлкните противоположный угол";
    public override ISketchTool Restart() => Restarted(new RectangleTool());

    public override bool Click(SketchInput i)
    {
        var sk = i.Sketch;
        if (Pts.Count == 0) { Pts.Add(i.P); Snaps.Add(i.Snap); return false; }
        Vector2d a = Pts[0], c = i.P;
        if (Math.Abs(c.X - a.X) < Tol.Weld || Math.Abs(c.Y - a.Y) < Tol.Weld) return false;
        var p0 = new SkPoint(a);
        var p1 = new SkPoint(new Vector2d(c.X, a.Y));
        var p2 = new SkPoint(c);
        var p3 = new SkPoint(new Vector2d(a.X, c.Y));
        var l0 = sk.AddEntity(new SkLine(p0, p1) { Role = Role });
        var l1 = sk.AddEntity(new SkLine(p1, p2) { Role = Role });
        var l2 = sk.AddEntity(new SkLine(p2, p3) { Role = Role });
        var l3 = sk.AddEntity(new SkLine(p3, p0) { Role = Role });
        // углы общие: концы соседних сторон совпадают
        sk.AddConstraint(ConstraintType.Coincident, l0.B, l1.A);
        sk.AddConstraint(ConstraintType.Coincident, l1.B, l2.A);
        sk.AddConstraint(ConstraintType.Coincident, l2.B, l3.A);
        sk.AddConstraint(ConstraintType.Coincident, l3.B, l0.A);
        sk.AddConstraint(ConstraintType.Horizontal, l0);
        sk.AddConstraint(ConstraintType.Horizontal, l2);
        sk.AddConstraint(ConstraintType.Vertical, l1);
        sk.AddConstraint(ConstraintType.Vertical, l3);
        Link(sk, p0, Snaps[0]);
        Link(sk, p2, i.Snap);
        Finished = true;
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (Pts.Count == 0) return;
        Vector2d a = Pts[0], c = Cursor;
        lines.Add(new[] { a, new Vector2d(c.X, a.Y), c, new Vector2d(a.X, c.Y), a });
    }
}

// ---------------------------------------------------------------- окружность

public sealed class CircleTool : SketchToolBase
{
    private readonly bool _three;
    public CircleTool(bool threePoints = false) { _three = threePoints; }
    public override string Title => _three ? "Окружность по 3 точкам" : "Окружность";
    public override string Hint => _three ? Loc.L("Щёлкните точку {0} из 3", Pts.Count + 1) : Pts.Count == 0 ? "Щёлкните центр" : "Щёлкните точку на окружности";
    public override ISketchTool Restart() => Restarted(new CircleTool(_three));

    public override bool Click(SketchInput i)
    {
        var sk = i.Sketch;
        Pts.Add(i.P); Snaps.Add(i.Snap);
        if (!_three && Pts.Count == 2)
        {
            double r = (Pts[1] - Pts[0]).Length;
            Finished = true;
            if (r <= Tol.Weld) return false;
            var c = sk.AddEntity(new SkCircle(new SkPoint(Pts[0]), r) { Role = Role }, autoConstrain: true);
            Link(sk, c.Center, Snaps[0]);
            return true;
        }
        if (_three && Pts.Count == 3)
        {
            Finished = true;
            var arc = SkArc.FromThreePoints(Pts[0], Pts[1], Pts[2]);
            if (arc == null) return false;
            sk.AddEntity(new SkCircle(new SkPoint(arc.Center.P), arc.Radius) { Role = Role }, autoConstrain: true);
            return true;
        }
        return false;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (Pts.Count == 0) return;
        if (!_three) { lines.Add(Circle(Pts[0], (Cursor - Pts[0]).Length)); points.Add(Pts[0]); return; }
        if (Pts.Count == 1) { lines.Add(new[] { Pts[0], Cursor }); return; }
        if (SkArc.FromThreePoints(Pts[0], Pts[1], Cursor) is { } a) lines.Add(Circle(a.Center.P, a.Radius));
    }
}

// --------------------------------------------------------------------- дуга

public sealed class ArcTool : SketchToolBase
{
    private readonly bool _three;
    public ArcTool(bool threePoints = true) { _three = threePoints; }
    public override string Title => _three ? "Дуга по 3 точкам" : "Дуга от центра";
    public override string Hint => _three
        ? Pts.Count switch { 0 => "Щёлкните начало дуги", 1 => "Щёлкните конец дуги", _ => "Щёлкните точку на дуге" }
        : Pts.Count switch { 0 => "Щёлкните центр", 1 => "Щёлкните начало дуги", _ => "Щёлкните конец дуги" };
    public override ISketchTool Restart() => Restarted(new ArcTool(_three));

    private SkArc? Make(Vector2d third) => _three
        ? SkArc.FromThreePoints(Pts[0], third, Pts[1])
        : SkArc.FromCenterStartEnd(Pts[0], Pts[1], third);

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); Snaps.Add(i.Snap);
        if (Pts.Count < 3) return false;
        Finished = true;
        var arc = Make(Pts[2]);
        if (arc == null) return false;
        arc.Role = Role;
        i.Sketch.AddEntity(arc, autoConstrain: true);
        if (_three) { Link(i.Sketch, arc.Start, Snaps[0]); Link(i.Sketch, arc.End, Snaps[1]); }
        else { Link(i.Sketch, arc.Center, Snaps[0]); Link(i.Sketch, arc.Start, Snaps[1]); }
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (Pts.Count == 0) return;
        if (Pts.Count == 1) { lines.Add(new[] { Pts[0], Cursor }); return; }
        if (Make(Cursor) is { } a) lines.Add(Tess(a));
    }
}

// ------------------------------------------------------------------- эллипс

public sealed class EllipseTool : SketchToolBase
{
    public override string Title => "Эллипс";
    public override string Hint => Pts.Count switch { 0 => "Щёлкните центр", 1 => "Щёлкните конец большой оси", _ => "Щёлкните точку малой оси" };
    public override ISketchTool Restart() => Restarted(new EllipseTool());

    private SkEllipse? Make(Vector2d third)
    {
        Vector2d c = Pts[0], major = Pts[1] - c;
        double a = major.Length, rot = Math.Atan2(major.Y, major.X);
        Vector2d rel = third - c;
        double b = Math.Abs(-rel.X * Math.Sin(rot) + rel.Y * Math.Cos(rot));
        return a > Tol.Weld && b > Tol.Weld ? new SkEllipse(new SkPoint(c), a, b, rot) : null;
    }

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P);
        if (Pts.Count < 3) return false;
        Finished = true;
        if (Make(Pts[2]) is not { } e) return false;
        e.Role = Role;
        i.Sketch.AddEntity(e, autoConstrain: true);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (Pts.Count == 0) return;
        if (Pts.Count == 1) { lines.Add(new[] { Pts[0], Cursor }); return; }
        if (Make(Cursor) is { } e) lines.Add(Tess(e));
    }
}

// ------------------------------------------------------------------- сплайн

public sealed class SplineTool : SketchToolBase
{
    public override string Title => "Сплайн";
    public override string Hint => "Щёлкайте точки сплайна; Enter или правая кнопка — готово";
    public override ISketchTool Restart() => Restarted(new SplineTool());

    public override bool Click(SketchInput i) { Pts.Add(i.P); return false; }

    public override bool Finish(Sketch sk)
    {
        bool made = false;
        if (Pts.Count >= 2)
        {
            sk.AddEntity(new SkSpline(Pts.Select(p => new SkPoint(p))) { Role = Role }, autoConstrain: true);
            made = true;
        }
        Pts.Clear();
        Finished = true;
        return made;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (Pts.Count == 0) return;
        lines.Add(Pts.Append(Cursor).ToArray());
        points.AddRange(Pts);
    }
}
