using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Model.Sketching;

public enum SkKind { Point, Line, Arc, Circle, Ellipse, Spline }

/// <summary>Роль элемента эскиза. Определяет и стиль линии, и участие в профиле.</summary>
public enum SkRole
{
    Object = 0,        // формирует контур тела
    Construction = 1,  // вспомогательная геометрия
    Centerline = 2,    // ось (для вращения)
    Reference = 3      // проекция внешней геометрии, не редактируется
}

/// <summary>
/// Элемент эскиза. Работает ТОЛЬКО в 2D — в координатах плоскости эскиза.
///
/// В старом коде Line2D/Circle2D наследовались от GLItem, имели transform,
/// кватернион, собственный шейдер и мировые координаты. Из-за этого «нарисовать
/// линию другим типом» означало пересобрать шейдер. Здесь элемент — это данные,
/// а его вид определяется полем Role, которое на отрисовке превращается
/// в индекс стиля LineStyle.
/// </summary>
public abstract class SkEntity
{
    public uint Id { get; internal set; } = Ids.New();
    public abstract SkKind Kind { get; }
    public SkRole Role = SkRole.Object;
    /// <summary>Элемент зафиксирован решателем/пользователем.</summary>
    public bool Fixed;

    /// <summary>
    /// k96: ВСТРОЕННАЯ ССЫЛКА ЭСКИЗА — нулевая точка или ось (горизонтальная, вертикальная). Не входит в Entities: не
    /// рисуется как геометрия эскиза, не сохраняется элементом, не удаляется; к ней привязываются связи и размеры.
    /// </summary>
    public bool Builtin;

    /// <summary>
    /// Элемент пересекает сам себя. Признак только отображения:
    /// его выставляет SketchDiagnostics, читает отрисовка.
    /// </summary>
    /// <summary>
    /// Откуда взята ссылочная геометрия: имя спроецированного элемента.
    /// Пусто у всего, что нарисовано вручную.
    /// </summary>
    public string ReferenceSource = "";

    public bool SelfIntersects;

    /// <summary>Элемент пересекается с другим рабочим элементом эскиза.</summary>
    public bool CrossesOther;

    /// <summary>Есть ли у элемента ошибка, мешающая построить по нему тело.</summary>
    public bool HasError => SelfIntersects || CrossesOther;
    public string Name = string.Empty;

    public LineStyleHint StyleHint => Role switch
    {
        SkRole.Construction => LineStyleHint.Construction,
        SkRole.Centerline => LineStyleHint.Centerline,
        SkRole.Reference => LineStyleHint.Reference,
        _ => LineStyleHint.Object
    };

    /// <summary>Дискретизация в 2D для отрисовки и построения профиля.</summary>
    public abstract void Tessellate(List<Vector2d> dst, bool includeFirst);

    /// <summary>Точки, которыми элемент управляется (для перетаскивания и привязок).</summary>
    public abstract IEnumerable<SkPoint> Handles { get; }

    /// <summary>Перевод в трёхмерную кривую на плоскости эскиза.</summary>
    public abstract ICurve3 ToCurve3(Frame3 plane);

    /// <summary>Расстояние от точки до элемента в координатах эскиза (для выбора мышью).</summary>
    public abstract double DistanceTo(Vector2d p);
}

/// <summary>Не зависящий от Render перечень типов линий — чтобы Model не тянул OpenGL.</summary>
public enum LineStyleHint { Object, Construction, Centerline, Reference, Dimension }

public sealed class SkPoint : SkEntity
{
    public override SkKind Kind => SkKind.Point;
    public double X, Y;
    /// <summary>Точка принадлежит другому элементу (конец отрезка, центр окружности) — не рисуется отдельно.</summary>
    public SkEntity? Owner;

    /// <summary>
    /// Свободный конец: к нему не примыкает ни один другой конец, значит
    /// контур в этом месте разорван и тело по нему не построить.
    /// </summary>
    public bool Dangling;

    public SkPoint(double x, double y) { X = x; Y = y; }
    public SkPoint(Vector2d p) { X = p.X; Y = p.Y; }

    public Vector2d P
    {
        get => new(X, Y);
        set { X = value.X; Y = value.Y; }
    }

    public override void Tessellate(List<Vector2d> dst, bool includeFirst) { dst.Add(P); }
    public override IEnumerable<SkPoint> Handles { get { yield return this; } }
    public override ICurve3 ToCurve3(Frame3 plane) => new LineCurve3(plane.ToWorld(P), plane.ToWorld(P));
    public override double DistanceTo(Vector2d p) => (p - P).Length;
    public override string ToString() => $"Pt({X:0.###}, {Y:0.###})";
}

public sealed class SkLine : SkEntity
{
    public override SkKind Kind => SkKind.Line;
    public SkPoint A, B;

    public SkLine(SkPoint a, SkPoint b) { A = a; B = b; a.Owner = this; b.Owner = this; }

    public double Length => (B.P - A.P).Length;
    public Vector2d Direction => (B.P - A.P).Normalized();

    public override void Tessellate(List<Vector2d> dst, bool includeFirst)
    {
        if (includeFirst) dst.Add(A.P);
        dst.Add(B.P);
    }

    public override IEnumerable<SkPoint> Handles { get { yield return A; yield return B; } }

    public override ICurve3 ToCurve3(Frame3 plane) => new LineCurve3(plane.ToWorld(A.P), plane.ToWorld(B.P));

    public override double DistanceTo(Vector2d p)
    {
        Vector2d ab = B.P - A.P;
        double l2 = ab.LengthSquared;
        if (l2 < 1e-18) return (p - A.P).Length;
        double t = Math.Clamp(Vector2d.Dot(p - A.P, ab) / l2, 0, 1);
        return (p - (A.P + ab * t)).Length;
    }
}

public sealed class SkCircle : SkEntity
{
    public override SkKind Kind => SkKind.Circle;
    public SkPoint Center;
    public double Radius;

    public SkCircle(SkPoint c, double r) { Center = c; Radius = r; c.Owner = this; }

    public override void Tessellate(List<Vector2d> dst, bool includeFirst)
    {
        int n = GeomUtil.ArcSegments(Radius, Math.PI * 2);
        for (int i = includeFirst ? 0 : 1; i <= n; i++)
        {
            double a = i / (double)n * Math.PI * 2;
            dst.Add(Center.P + new Vector2d(Math.Cos(a), Math.Sin(a)) * Radius);
        }
    }

    public override IEnumerable<SkPoint> Handles { get { yield return Center; } }

    public override ICurve3 ToCurve3(Frame3 plane)
    {
        Vector3d o = plane.ToWorld(Center.P);
        return ArcCurve3.FullCircle(new Frame3(o, plane.U, plane.V), Radius);
    }

    public override double DistanceTo(Vector2d p) => Math.Abs((p - Center.P).Length - Radius);
}

public sealed class SkArc : SkEntity
{
    public override SkKind Kind => SkKind.Arc;
    public SkPoint Center, Start, End;
    /// <summary>Направление обхода от Start к End.</summary>
    public bool CCW = true;

    public SkArc(SkPoint c, SkPoint start, SkPoint end, bool ccw = true)
    {
        Center = c; Start = start; End = end; CCW = ccw;
        c.Owner = this; start.Owner = this; end.Owner = this;
    }

    /// <summary>Радиус — расстояние от центра до начальной точки (решатель держит End на том же радиусе).</summary>
    public double Radius => (Start.P - Center.P).Length;
    public double A0 => Math.Atan2(Start.Y - Center.Y, Start.X - Center.X);
    public double A1
    {
        get
        {
            double a0 = A0;
            double ae = Math.Atan2(End.Y - Center.Y, End.X - Center.X);
            double sw = GeomUtil.Norm2Pi(ae - a0);
            if (!CCW) sw -= Math.PI * 2;
            if (Math.Abs(sw) < 1e-9) sw = CCW ? Math.PI * 2 : -Math.PI * 2;
            return a0 + sw;
        }
    }
    public double Sweep => A1 - A0;

    /// <summary>Дуга по трём точкам: начало, промежуточная, конец. Центр вычисляется.</summary>
    public static SkArc? FromThreePoints(Vector2d p1, Vector2d p2, Vector2d p3)
    {
        double d = 2 * (p1.X * (p2.Y - p3.Y) + p2.X * (p3.Y - p1.Y) + p3.X * (p1.Y - p2.Y));
        if (Math.Abs(d) < 1e-12) return null;
        double ux = (p1.LengthSquared * (p2.Y - p3.Y) + p2.LengthSquared * (p3.Y - p1.Y) + p3.LengthSquared * (p1.Y - p2.Y)) / d;
        double uy = (p1.LengthSquared * (p3.X - p2.X) + p2.LengthSquared * (p1.X - p3.X) + p3.LengthSquared * (p2.X - p1.X)) / d;
        var c = new Vector2d(ux, uy);

        double a0 = Math.Atan2(p1.Y - c.Y, p1.X - c.X);
        double am = Math.Atan2(p2.Y - c.Y, p2.X - c.X);
        double a1 = Math.Atan2(p3.Y - c.Y, p3.X - c.X);
        bool ccw = GeomUtil.Norm2Pi(am - a0) <= GeomUtil.Norm2Pi(a1 - a0);

        return new SkArc(new SkPoint(c), new SkPoint(p1), new SkPoint(p3), ccw);
    }

    /// <summary>Дуга «центр + начало + конец».</summary>
    public static SkArc FromCenterStartEnd(Vector2d c, Vector2d s, Vector2d e, bool ccw = true)
        => new SkArc(new SkPoint(c), new SkPoint(s), new SkPoint(e), ccw);

    public override void Tessellate(List<Vector2d> dst, bool includeFirst)
    {
        double r = Radius;
        double a0 = A0, sw = Sweep;
        int n = GeomUtil.ArcSegments(r, sw);
        for (int i = includeFirst ? 0 : 1; i <= n; i++)
        {
            double a = a0 + sw * (i / (double)n);
            dst.Add(Center.P + new Vector2d(Math.Cos(a), Math.Sin(a)) * r);
        }
    }

    public override IEnumerable<SkPoint> Handles
    {
        get { yield return Center; yield return Start; yield return End; }
    }

    public override ICurve3 ToCurve3(Frame3 plane)
    {
        Vector3d o = plane.ToWorld(Center.P);
        return new ArcCurve3(new Frame3(o, plane.U, plane.V), Radius, A0, A1);
    }

    public override double DistanceTo(Vector2d p)
    {
        Vector2d d = p - Center.P;
        double a = Math.Atan2(d.Y, d.X);
        double t = GeomUtil.Norm2Pi(a - A0);
        double sw = Sweep;
        bool inside = sw >= 0 ? t <= sw : (Math.PI * 2 - t) <= -sw;
        if (inside) return Math.Abs(d.Length - Radius);
        return Math.Min((p - Start.P).Length, (p - End.P).Length);
    }
}

public sealed class SkEllipse : SkEntity
{
    public override SkKind Kind => SkKind.Ellipse;
    public SkPoint Center;
    public double RU, RV;
    public double Rotation;
    public double A0, A1 = Math.PI * 2;

    public SkEllipse(SkPoint c, double ru, double rv, double rot = 0)
    { Center = c; RU = ru; RV = rv; Rotation = rot; c.Owner = this; }

    private Vector2d Eval(double a)
    {
        double x = Math.Cos(a) * RU, y = Math.Sin(a) * RV;
        double c = Math.Cos(Rotation), s = Math.Sin(Rotation);
        return Center.P + new Vector2d(x * c - y * s, x * s + y * c);
    }

    public override void Tessellate(List<Vector2d> dst, bool includeFirst)
    {
        int n = GeomUtil.ArcSegments(Math.Max(RU, RV), A1 - A0);
        for (int i = includeFirst ? 0 : 1; i <= n; i++)
            dst.Add(Eval(A0 + (A1 - A0) * (i / (double)n)));
    }

    public override IEnumerable<SkPoint> Handles { get { yield return Center; } }

    public override ICurve3 ToCurve3(Frame3 plane)
    {
        Vector3d o = plane.ToWorld(Center.P);
        Vector2d u2 = new(Math.Cos(Rotation), Math.Sin(Rotation));
        Vector2d v2 = new(-Math.Sin(Rotation), Math.Cos(Rotation));
        var f = new Frame3(o, plane.DirToWorld(u2), plane.DirToWorld(v2));
        return new EllipseCurve3(f, RU, RV, A0, A1);
    }

    public override double DistanceTo(Vector2d p)
    {
        double best = double.MaxValue;
        var pts = new List<Vector2d>();
        Tessellate(pts, true);
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2d a = pts[i - 1], b = pts[i], ab = b - a;
            double l2 = ab.LengthSquared;
            double t = l2 < 1e-18 ? 0 : Math.Clamp(Vector2d.Dot(p - a, ab) / l2, 0, 1);
            best = Math.Min(best, (p - (a + ab * t)).Length);
        }
        return best;
    }
}

public sealed class SkSpline : SkEntity
{
    public override SkKind Kind => SkKind.Spline;
    public readonly List<SkPoint> Ctrl = new();

    public SkSpline(IEnumerable<SkPoint> pts)
    {
        Ctrl.AddRange(pts);
        foreach (var p in Ctrl) p.Owner = this;
    }

    public override void Tessellate(List<Vector2d> dst, bool includeFirst)
    {
        if (Ctrl.Count < 2) return;
        double poly = 0;
        for (int i = 1; i < Ctrl.Count; i++) poly += (Ctrl[i].P - Ctrl[i - 1].P).Length;
        int n = Math.Clamp((int)(poly / Math.Max(Tol.Chord * 8, 1e-6)), 12, 200);
        for (int i = includeFirst ? 0 : 1; i <= n; i++) dst.Add(DeCasteljau(i / (double)n));
    }

    private Vector2d DeCasteljau(double t)
    {
        Span<Vector2d> tmp = Ctrl.Count <= 32 ? stackalloc Vector2d[Ctrl.Count] : new Vector2d[Ctrl.Count];
        for (int i = 0; i < Ctrl.Count; i++) tmp[i] = Ctrl[i].P;
        for (int k = Ctrl.Count - 1; k > 0; k--)
            for (int i = 0; i < k; i++)
                tmp[i] = tmp[i] * (1 - t) + tmp[i + 1] * t;
        return tmp[0];
    }

    public override IEnumerable<SkPoint> Handles => Ctrl;

    public override ICurve3 ToCurve3(Frame3 plane)
        => new BezierCurve3(Ctrl.Select(p => plane.ToWorld(p.P)));

    public override double DistanceTo(Vector2d p)
    {
        var pts = new List<Vector2d>();
        Tessellate(pts, true);
        double best = double.MaxValue;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2d a = pts[i - 1], b = pts[i], ab = b - a;
            double l2 = ab.LengthSquared;
            double t = l2 < 1e-18 ? 0 : Math.Clamp(Vector2d.Dot(p - a, ab) / l2, 0, 1);
            best = Math.Min(best, (p - (a + ab * t)).Length);
        }
        return best;
    }
}
