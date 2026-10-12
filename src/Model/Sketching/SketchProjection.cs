using OpenTK.Mathematics;
using Parametric3D.Kernel;

namespace Parametric3D.Model.Sketching;

/// <summary>Во что превращается элемент модели при проекции на плоскость эскиза.</summary>
public enum ProjectedKind { Point, Polyline, Circle, Ellipse, Arc }

/// <summary>
/// Проекция чужой геометрии на плоскость эскиза.
///
/// Готовый кандидат: уже посчитанная двумерная геометрия в системе
/// координат эскиза плюс описание источника.
/// </summary>
public sealed class ProjectedRef
{
    public ProjectedKind Kind;

    /// <summary>Точка, центр окружности или дуги.</summary>
    public Vector2d P;

    /// <summary>Ломаная: отрезок, сплайн, линия пересечения.</summary>
    public List<Vector2d> Points = new();

    /// <summary>Окружность и дуга.</summary>
    public double Radius;
    /// <summary>Эллипс: полуоси и поворот.</summary>
    public double RU, RV, Rotation;
    /// <summary>Дуга: углы начала и конца.</summary>
    public double A0, A1;

    public string Source = "";

    /// <summary>Расстояние от точки эскиза до этой проекции.</summary>
    public double DistanceTo(Vector2d q)
    {
        switch (Kind)
        {
            case ProjectedKind.Point:
                return (q - P).Length;

            case ProjectedKind.Circle:
                return Math.Abs((q - P).Length - Radius);

            case ProjectedKind.Arc:
                {
                    Vector2d d = q - P;
                    double a = GeomUtil.Norm2Pi(Math.Atan2(d.Y, d.X));
                    double s = GeomUtil.Norm2Pi(A0);
                    double sweep = GeomUtil.Norm2Pi(A1 - A0);
                    double rel = GeomUtil.Norm2Pi(a - s);

                    // Вне углового размаха ближайшая точка — конец дуги.
                    if (rel > sweep)
                    {
                        Vector2d e0 = P + Dir(A0) * Radius;
                        Vector2d e1 = P + Dir(A1) * Radius;
                        return Math.Min((q - e0).Length, (q - e1).Length);
                    }
                    return Math.Abs(d.Length - Radius);
                }

            case ProjectedKind.Ellipse:
                {
                    // Достаточная для наведения оценка: точка приводится
                    // к окружности единичного радиуса сжатием по полуосям.
                    Vector2d d = q - P;
                    double c = Math.Cos(-Rotation), s2 = Math.Sin(-Rotation);
                    double u = (d.X * c - d.Y * s2) / Math.Max(RU, 1e-9);
                    double v = (d.X * s2 + d.Y * c) / Math.Max(RV, 1e-9);
                    double r = Math.Sqrt(u * u + v * v);
                    return Math.Abs(r - 1) * Math.Min(RU, RV);
                }

            default:
                {
                    double best = double.MaxValue;
                    for (int i = 1; i < Points.Count; i++)
                        best = Math.Min(best, SegmentDistance(q, Points[i - 1], Points[i]));
                    return best;
                }
        }
    }

    private static Vector2d Dir(double a) => new(Math.Cos(a), Math.Sin(a));

    private static double SegmentDistance(Vector2d p, Vector2d a, Vector2d b)
    {
        Vector2d ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-18) return (p - a).Length;
        double t = Math.Clamp(Vector2d.Dot(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}

/// <summary>
/// Сбор проекций чужой геометрии на плоскость эскиза.
///
/// ЧТО ВО ЧТО ПЕРЕХОДИТ. Проекция сохраняет форму только тогда, когда
/// элемент лежит в плоскости, ПАРАЛЛЕЛЬНОЙ плоскости эскиза: окружность
/// остаётся окружностью, дуга дугой, эллипс эллипсом. Наклонная окружность
/// спроецировалась бы в эллипс, а наклонный эллипс — в другой эллипс;
/// такие проекции здесь не предлагаются, потому что построение по ним
/// читается плохо и почти всегда означает ошибку выбора.
///
/// Отдельные случаи:
///   * ось, ПЕРПЕНДИКУЛЯРНАЯ плоскости эскиза, проецируется в точку;
///   * плоскость, перпендикулярная плоскости эскиза, — в линию
///     их пересечения;
///   * точки проецируются всегда, независимо от ориентации.
/// </summary>
public static class SketchProjection
{
    /// <summary>Допуск на параллельность и перпендикулярность, радиан.</summary>
    private const double AngleTol = 1e-4;

    public static List<ProjectedRef> Collect(Document doc, Sketch sketch)
    {
        var result = new List<ProjectedRef>();
        Frame3 plane = sketch.Plane;

        foreach (var item in doc.Root.Descendants())
        {
            // Сам эскиз в проекции не участвует: проецировать его на себя
            // бессмысленно, а нулевую точку нельзя брать из своей же точки.
            if (ReferenceEquals(item, sketch)) continue;

            // На то, что создано ПОЗЖЕ эскиза, опираться нельзя: получилась
            // бы петля в дереве построения.
            if (item.Id >= sketch.Id || !item.Visible) continue;

            switch (item)
            {
                case DatumPoint dp:
                    Add(result, Point(plane, dp.P, dp.Name));
                    break;

                case DatumAxis ax:
                    AddAxis(result, plane, ax);
                    break;

                case DatumPlane pl:
                    AddPlane(result, plane, pl);
                    break;

                case Line3DItem ln:
                    AddSegment(result, plane, ln.A, ln.B, ln.Name);
                    break;

                case Sketch other:
                    AddSketch(result, plane, other);
                    break;

                case Part part:
                    AddPart(result, plane, part);
                    break;
            }
        }

        return result;
    }

    // ------------------------------------------------------------ источники

    private static void AddPart(List<ProjectedRef> dst, Frame3 plane, Part part)
    {
        foreach (var body in part.Bodies)
        {
            foreach (var v in body.Vertices)
                Add(dst, Point(plane, v.P, $"{part.Name} vertex"));

            // Ось цилиндрической грани, перпендикулярной плоскости эскиза,
            // видна с торца — точкой. Это привычный способ указать центр
            // отверстия или вала, не имея там ни вершины, ни базовой точки.
            foreach (var f in body.Faces)
            {
                if (f.Surface is not Kernel.Surfaces.CylinderSurface cyl) continue;
                if (!IsParallel(cyl.Axis.W, plane.W)) continue;

                Add(dst, Point(plane, cyl.Axis.Origin, $"{part.Name} cylinder axis"));
            }

            foreach (var e in body.Edges)
            {
                var pts = new List<Vector3d>();
                e.Curve?.Tessellate(pts, true);
                if (pts.Count < 2) continue;

                // Ребро годится, только если целиком лежит в плоскости,
                // параллельной плоскости эскиза: иначе его проекция
                // искажает форму.
                if (!AllAtSameHeight(plane, pts)) continue;

                var pr = new ProjectedRef { Kind = ProjectedKind.Polyline, Source = $"{part.Name} edge" };
                foreach (var p in pts) pr.Points.Add(plane.ToLocal2(p));
                Add(dst, pr);
            }
        }
    }

    private static void AddSketch(List<ProjectedRef> dst, Frame3 plane, Sketch other)
    {
        bool parallel = IsParallel(plane.W, other.Plane.W);

        foreach (var e in other.Entities)
        {
            if (e.Role == SkRole.Reference) continue;

            switch (e)
            {
                case SkPoint p:
                    Add(dst, Point(plane, other.Plane.ToWorld(p.P), $"{other.Name} point"));
                    continue;

                case SkCircle c when parallel:
                    Add(dst, new ProjectedRef
                    {
                        Kind = ProjectedKind.Circle,
                        P = plane.ToLocal2(other.Plane.ToWorld(c.Center.P)),
                        Radius = c.Radius,
                        Source = $"{other.Name} circle"
                    });
                    continue;

                case SkEllipse el when parallel:
                    Add(dst, new ProjectedRef
                    {
                        Kind = ProjectedKind.Ellipse,
                        P = plane.ToLocal2(other.Plane.ToWorld(el.Center.P)),
                        RU = el.RU, RV = el.RV,
                        Rotation = el.Rotation + AngleBetweenAxes(other.Plane, plane),
                        Source = $"{other.Name} ellipse"
                    });
                    continue;

                case SkArc a when parallel:
                    {
                        Vector2d center = plane.ToLocal2(other.Plane.ToWorld(a.Center.P));
                        Vector2d s = plane.ToLocal2(other.Plane.ToWorld(a.Start.P));
                        Vector2d t = plane.ToLocal2(other.Plane.ToWorld(a.End.P));

                        Add(dst, new ProjectedRef
                        {
                            Kind = ProjectedKind.Arc,
                            P = center,
                            Radius = (s - center).Length,
                            A0 = Math.Atan2(s.Y - center.Y, s.X - center.X),
                            A1 = Math.Atan2(t.Y - center.Y, t.X - center.X),
                            Source = $"{other.Name} arc"
                        });
                        continue;
                    }
            }

            // Всё прочее — отрезки, сплайны — ломаной, и только если лежит
            // в параллельной плоскости.
            if (!parallel) continue;

            var pts2 = new List<Vector2d>();
            e.Tessellate(pts2, true);
            if (pts2.Count < 2) continue;

            var pr = new ProjectedRef
            {
                Kind = ProjectedKind.Polyline,
                Source = $"{other.Name} {e.Kind}"
            };
            foreach (var q in pts2) pr.Points.Add(plane.ToLocal2(other.Plane.ToWorld(q)));
            Add(dst, pr);
        }
    }

    private static void AddAxis(List<ProjectedRef> dst, Frame3 plane, DatumAxis ax)
    {
        // Перпендикулярная ось видна с торца — точкой.
        if (IsParallel(ax.Direction, plane.W))
        {
            Add(dst, Point(plane, ax.Origin, ax.Name));
            return;
        }

        // Ось, лежащая в параллельной плоскости, — линией.
        if (Math.Abs(Vector3d.Dot(ax.Direction.Normalized(), plane.W)) < AngleTol)
        {
            Vector3d d = ax.Direction.Normalized() * ax.DisplayLength;
            AddSegment(dst, plane, ax.Origin - d, ax.Origin + d, ax.Name);
        }
    }

    private static void AddPlane(List<ProjectedRef> dst, Frame3 plane, DatumPlane pl)
    {
        // Перпендикулярная плоскость даёт линию пересечения.
        if (Math.Abs(Vector3d.Dot(pl.Frame.W.Normalized(), plane.W)) > AngleTol) return;

        Vector3d dir = Vector3d.Cross(plane.W, pl.Frame.W);
        if (dir.LengthSquared < 1e-18) return;
        dir = dir.Normalized();

        // Точка на линии пересечения: начало чужой плоскости, спроецированное
        // на плоскость эскиза.
        Vector2d uv = plane.ToLocal2(pl.Frame.Origin);
        Vector3d onPlane = plane.ToWorld(uv);

        double half = Math.Max(pl.EffectiveSize, 50);
        AddSegment(dst, plane, onPlane - dir * half, onPlane + dir * half, pl.Name);
    }

    private static void AddSegment(List<ProjectedRef> dst, Frame3 plane,
                                   Vector3d a, Vector3d b, string source)
    {
        var pr = new ProjectedRef { Kind = ProjectedKind.Polyline, Source = source };
        pr.Points.Add(plane.ToLocal2(a));
        pr.Points.Add(plane.ToLocal2(b));
        Add(dst, pr);
    }

    private static ProjectedRef Point(Frame3 plane, Vector3d p, string source) => new()
    {
        Kind = ProjectedKind.Point,
        P = plane.ToLocal2(p),
        Source = source
    };

    // ------------------------------------------------------------- служебное

    private static void Add(List<ProjectedRef> dst, ProjectedRef pr)
    {
        // Совпадающие проекции не дублируются: у соседних тел вершины
        // сходятся, и без этого под курсором оказывался бы десяток
        // одинаковых кандидатов.
        foreach (var e in dst)
        {
            if (e.Kind != pr.Kind) continue;
            if (e.Kind == ProjectedKind.Point && (e.P - pr.P).Length < 1e-7) return;
        }
        dst.Add(pr);
    }

    private static bool IsParallel(Vector3d a, Vector3d b)
    {
        if (a.LengthSquared < 1e-18 || b.LengthSquared < 1e-18) return false;
        return Vector3d.Cross(a.Normalized(), b.Normalized()).Length < AngleTol;
    }

    /// <summary>Лежат ли все точки на одной высоте над плоскостью эскиза.</summary>
    private static bool AllAtSameHeight(Frame3 plane, List<Vector3d> pts)
    {
        double h0 = Vector3d.Dot(pts[0] - plane.Origin, plane.W);
        foreach (var p in pts)
            if (Math.Abs(Vector3d.Dot(p - plane.Origin, plane.W) - h0) > 1e-6) return false;
        return true;
    }

    /// <summary>Разворот осей одной параллельной плоскости относительно другой.</summary>
    private static double AngleBetweenAxes(Frame3 from, Frame3 to)
    {
        double x = Vector3d.Dot(from.U, to.U);
        double y = Vector3d.Dot(from.U, to.V);
        return Math.Atan2(y, x);
    }
}
