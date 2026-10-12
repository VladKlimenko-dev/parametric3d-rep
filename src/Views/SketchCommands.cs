using OpenTK.Mathematics;
using Parametric3D.App;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Views;

/// <summary>
/// Команды эскиза, работающие с текущим выделением: наложение ограничений,
/// скругление и фаска в углу, удаление сегмента.
///
/// Отделены от инструментов рисования: инструмент — это автомат с состоянием
/// «жду следующий щелчок», команда — одно действие над тем, что уже выбрано.
/// </summary>
public static class SketchCommands
{
    private static IEnumerable<SkEntity> Selected(GL3DView view)
    {
        var sk = view.ActiveSketch;
        if (sk == null) yield break;
        foreach (var i in view.Selection.Items)
            if (i.Target is SkEntity e) yield return e;
    }

    /// <summary>Возвращает текст ошибки или null, если ограничение наложено.</summary>
    public static string? ApplyConstraint(GL3DView view, ConstraintType type)
        => ApplyConstraint(view, type, Selected(view).ToArray());

    /// <summary>Накладывает ограничение на заданный набор элементов.</summary>
    public static string? ApplyConstraint(GL3DView view, ConstraintType type, SkEntity[] items)
    {
        var sk = view.ActiveSketch;
        if (sk == null) return "No active sketch.";

        // «Точка на окружности» — это то же совпадение, только точки
        // с кривой. Пользователь жмёт Coincident, а ограничение ставится
        // подходящее по смыслу.
        if (type == ConstraintType.Coincident && items.Length == 2)
        {
            if (items[0] is SkPoint && items[1] is not SkPoint) type = ConstraintType.PointOnEntity;
            else if (items[1] is SkPoint && items[0] is not SkPoint)
            {
                type = ConstraintType.PointOnEntity;
                items = new[] { items[1], items[0] };
            }
        }

        // Узел не может быть участником ограничений, говорящих о форме
        // и направлении. Иначе указание на узел линии рядом с окружностью
        // превращается в касание к её ЦЕНТРУ — и смысл команды теряется.
        if (!AcceptsPoints(type) && items.Any(i => i is SkPoint))
            return $"{type} cannot be applied to a point. Select the line or the curve itself.";

        string? err = Validate(type, items);
        if (err != null) return err;

        // Что мешает наложить ограничение: дубликат, спорящее ограничение
        // или заданный размер.
        var conflicts = ConstraintConflicts.Find(sk, type, items);

        if (conflicts.Any(c => c.Kind == ConflictKind.Duplicate))
            return $"{type} is already applied to these items.";

        if (conflicts.Count > 0)
        {
            // Решение принимает пользователь: приложение показывает список
            // и ждёт выбора. Молча отменить или молча наложить одинаково плохо.
            var added = sk.AddConstraint(type, items);
            view.RaiseConstraintConflict(added, conflicts);
            sk.Solve(view.Document.Solver);
            view.MarkDirty();
            return null;
        }

        sk.AddConstraint(type, items);
        var status = sk.Solve(view.Document.Solver);
        view.MarkDirty();

        return status switch
        {
            SolveStatus.Overconstrained => "Constraint added, but it is redundant: the sketch is now over-constrained.",
            SolveStatus.Inconsistent => "Constraint added, but it contradicts the existing constraints and dimensions.",
            SolveStatus.DidNotConverge => "The solver could not satisfy this constraint.",
            SolveStatus.Singular => "Constraint added, but it is satisfied only by degenerate geometry (zero length or radius): the sketch is singular.",
            _ => null
        };
    }

    /// <summary>
    /// Сколько элементов нужно ограничению. По этому числу режим
    /// последовательного выбора понимает, когда пора применять.
    /// </summary>
    public static int Arity(ConstraintType t) => t switch
    {
        ConstraintType.Horizontal => 1,
        ConstraintType.Vertical => 1,
        ConstraintType.Symmetric => 3,
        _ => 2
    };

    /// <summary>
    /// Можно ли назначать ограничение цепочкой: первый выбранный элемент
    /// остаётся образцом, каждый следующий приравнивается к нему.
    /// </summary>
    public static bool IsChainable(ConstraintType t) =>
        t == ConstraintType.Parallel || t == ConstraintType.Equal;

    /// <summary>
    /// Можно ли накладывать это ограничение на УЗЕЛ.
    ///
    /// Узел — это точка, у неё нет ни направления, ни размера. Требовать
    /// от неё касания, параллельности, равенства, перпендикулярности,
    /// горизонтальности или вертикальности бессмысленно: решателю нечего
    /// с ней сопоставить, и он молча берёт то, что подвернулось, —
    /// например центр окружности вместо самой окружности. Точке доступны
    /// только те ограничения, которые говорят о её ПОЛОЖЕНИИ.
    /// </summary>
    public static bool AcceptsPoints(ConstraintType t) => t is
        ConstraintType.Coincident or ConstraintType.MidPoint or
        ConstraintType.PointOnEntity or ConstraintType.Symmetric or
        ConstraintType.Concentric;

    private static string? Validate(ConstraintType t, SkEntity[] it) => t switch
    {
        ConstraintType.Coincident when it.Length != 2 || it.Any(x => x is not SkPoint)
            => "Select two points, or a point and a curve.",
        ConstraintType.PointOnEntity when it.Length != 2 || it[0] is not SkPoint || it[1] is SkPoint
            => "Select a point and a curve.",
        ConstraintType.Horizontal or ConstraintType.Vertical when it.Length == 0
            => "Select a line, or two points.",
        ConstraintType.Parallel or ConstraintType.Perpendicular
            when it.Length != 2 || it.Any(x => x is not SkLine)
            => "Select two lines.",
        ConstraintType.Tangent when it.Length != 2
            => "Select a line and an arc, or two arcs.",
        ConstraintType.Equal when it.Length != 2
            => "Select two items of the same kind.",
        ConstraintType.MidPoint when it.Length != 2 || it[0] is not SkPoint || it[1] is not SkLine
            => "Select a point and a line.",
        ConstraintType.Symmetric when it.Length != 3 || it[2] is not SkLine
            => "Select two points and a symmetry axis.",
        ConstraintType.Concentric when it.Length != 2
            => "Select two circles or arcs.",
        _ => null
    };

    /// <summary>Простановка размера по текущему выделению.</summary>
    public static string? AddDimension(GL3DView view, double? value = null)
        => AddDimension(view, Selected(view).ToArray(), value);

    /// <summary>Простановка размера по заданному набору элементов.</summary>
    public static string? AddDimension(GL3DView view, SkEntity[] it, double? value = null)
        => AddDimension(view, it, value, out _);

    /// <summary>
    /// Простановка размера с возвратом созданного объекта.
    ///
    /// Размер становится СПРАВОЧНЫМ в двух случаях: такой размер уже есть
    /// на тех же элементах, либо он переопределяет эскиз. И то и другое —
    /// не ошибка пользователя: он вправе хотеть видеть величину, которой
    /// уже управляют другие ограничения.
    /// </summary>
    public static string? AddDimension(GL3DView view, SkEntity[] it, double? value, out SkDimension? created)
    {
        created = null;
        var sk = view.ActiveSketch;
        if (sk == null) return "No active sketch.";

        DimensionType type;
        double v;

        switch (it.Length)
        {
            case 1 when it[0] is SkCircle c:
                type = DimensionType.Diameter; v = c.Radius * 2; break;
            case 1 when it[0] is SkArc a:
                type = DimensionType.Radius; v = a.Radius; break;
            case 1 when it[0] is SkLine l:
                created = Create(view, sk, DimensionType.DistancePointPoint, value ?? l.Length, l.A, l.B);
                return null;
            case 2 when it[0] is SkPoint p && it[1] is SkPoint q:
                type = DimensionType.DistancePointPoint; v = (p.P - q.P).Length; break;
            case 2 when it[0] is SkPoint pp && it[1] is SkLine ll:
                type = DimensionType.DistancePointLine;
                v = Math.Abs(ll.DistanceTo(pp.P)); break;
            case 2 when it[0] is SkLine l1 && it[1] is SkLine l2:
                {
                    Vector2d d1 = (l1.B.P - l1.A.P).Normalized();
                    Vector2d d2 = (l2.B.P - l2.A.P).Normalized();
                    double sin = d1.X * d2.Y - d1.Y * d2.X;
                    double cos = Vector2d.Dot(d1, d2);

                    // Между параллельными линиями измеряется расстояние —
                    // угол там всегда нулевой и смысла не несёт. Во всех
                    // остальных случаях замер переключается на угловой.
                    if (Math.Abs(sin) < 1e-6)
                    {
                        type = DimensionType.DistanceLineLine;
                        v = Math.Abs(PointToLine(l2.A.P, l1));
                    }
                    else
                    {
                        type = DimensionType.AngleLineLine;

                        // Единая формула угла: она же используется решателем.
                        v = DimensionMeasure.LineAngleDegrees(l1, l2);
                    }
                    break;
                }
            default:
                return "Select a line, a circle, two points, or two lines.";
        }

        created = Create(view, sk, type, value ?? v, it);
        return null;
    }

    /// <summary>
    /// Создаёт размер и решает, должен ли он быть справочным.
    ///
    /// Проверка переопределения делается по факту: размер добавляется,
    /// эскиз решается, и если решатель сообщает о переопределении —
    /// размер переводится в справочные и эскиз решается заново. Это
    /// надёжнее, чем пытаться предсказать конфликт по типам ограничений.
    /// </summary>
    private static SkDimension Create(GL3DView view, Sketch sk, DimensionType type,
                                      double value, params SkEntity[] items)
    {
        bool duplicate = sk.Dimensions.Any(
            x => x.Type == type && x.Items.Count == items.Length &&
                 x.Items.All(items.Contains));

        var d = sk.AddDimension(type, value, items);
        d.Reference = duplicate;
        // Размер, поставленный пользователем, по умолчанию управляет
        // эскизом: он затем и ставится. Настройкой это можно изменить.
        d.Locked = Core.Settings.Current.SketchDefaultLockedDimension;

        var status = sk.Solve(view.Document.Solver);
        // K09: решатель теперь различает лишний размер (Overconstrained) и противоречащий
        // (Inconsistent); оба делаются справочными, как прежде делался любой «пересчитанный».
        if (!d.Reference && status is SolveStatus.Overconstrained or SolveStatus.Inconsistent)
        {
            d.Reference = true;
            sk.Solve(view.Document.Solver);
        }

        view.MarkDirty();
        return d;
    }

    /// <summary>Удаляет размер из эскиза.</summary>
    public static void DeleteDimension(GL3DView view, SkDimension d)
    {
        var sk = view.ActiveSketch;
        if (sk == null) return;
        sk.Dimensions.Remove(d);
        Finish(view, sk);
    }

    /// <summary>Меняет значение размера и пересчитывает эскиз.</summary>
    public static string? ModifyDimension(GL3DView view, SkDimension d, double value)
    {
        var sk = view.ActiveSketch;
        if (sk == null) return "No active sketch.";

        double old = d.Value;
        d.Value = value;

        // Введённое число делает размер управляющим: раз пользователь его
        // задал, эскиз обязан его держать.
        d.IsDefault = false;
        d.Locked = true;

        var status = sk.Solve(view.Document.Solver);
        if (status is SolveStatus.DidNotConverge or SolveStatus.Inconsistent)
        {
            d.Value = old;
            sk.Solve(view.Document.Solver);
            view.MarkDirty();
            return "The sketch cannot be solved with this value.";
        }

        view.MarkDirty();
        return null;
    }

    private static void Finish(GL3DView view, Sketch sk)
    {
        sk.Solve(view.Document.Solver);
        view.MarkDirty();
    }

    /// <summary>Удаляет выбранные элементы эскиза вместе со связанными ограничениями.</summary>
    public static void DeleteSelected(GL3DView view)
    {
        var sk = view.ActiveSketch;
        if (sk == null) return;
        foreach (var e in Selected(view).ToArray()) sk.RemoveEntity(e);
        view.Selection.Clear();
        view.MarkDirty();
    }

    /// <summary>
    /// Скругление угла между двумя выбранными линиями: линии подрезаются,
    /// между ними вставляется дуга, ставятся касания.
    /// </summary>
    public static string? Fillet(GL3DView view, double radius)
    {
        var sk = view.ActiveSketch;
        if (sk == null) return "No active sketch.";

        var lines = Selected(view).OfType<SkLine>().ToArray();
        if (lines.Length != 2) return "Select two lines.";
        if (radius <= Tol.Weld) return "Radius must be greater than zero.";

        var (a, b) = (lines[0], lines[1]);
        if (!Corner(a, b, out SkPoint cornerA, out SkPoint cornerB, out SkPoint farA, out SkPoint farB))
            return "The lines do not share a corner.";

        string? err = CornerGeometry(cornerA.P, farA.P, farB.P, radius, true,
                                     out Vector2d ta, out Vector2d tb, out Vector2d center);
        if (err != null) return err;

        // Каждой линии — СВОЙ новый конец.
        var pa = ReplaceCorner(a, cornerA, ta);
        var pb = ReplaceCorner(b, cornerB, tb);

        // Короткая дуга: направление обхода определяется знаком векторного
        // произведения радиус-векторов точек касания.
        bool ccw = Cross(ta - center, tb - center) > 0;

        var arc = sk.AddEntity(new SkArc(new SkPoint(center), pa, pb, ccw));
        sk.AddConstraint(ConstraintType.Tangent, a, arc);
        sk.AddConstraint(ConstraintType.Tangent, b, arc);

        Finish(view, sk);
        return null;
    }

    /// <summary>Фаска между двумя линиями: угол срезается прямым отрезком.</summary>
    public static string? Chamfer(GL3DView view, double d1, double d2)
    {
        var sk = view.ActiveSketch;
        if (sk == null) return "No active sketch.";

        var lines = Selected(view).OfType<SkLine>().ToArray();
        if (lines.Length != 2) return "Select two lines.";
        if (d1 <= Tol.Weld || d2 <= Tol.Weld) return "Chamfer distances must be greater than zero.";

        var (a, b) = (lines[0], lines[1]);
        if (!Corner(a, b, out SkPoint cornerA, out SkPoint cornerB, out SkPoint farA, out SkPoint farB))
            return "The lines do not share a corner.";

        Vector2d corner = cornerA.P;
        Vector2d da = (farA.P - corner).Normalized();
        Vector2d db = (farB.P - corner).Normalized();

        if (d1 > (farA.P - corner).Length || d2 > (farB.P - corner).Length)
            return "Chamfer is too large for these lines.";

        var pa = ReplaceCorner(a, cornerA, corner + da * d1);
        var pb = ReplaceCorner(b, cornerB, corner + db * d2);

        sk.AddEntity(new SkLine(pa, pb));
        Finish(view, sk);
        return null;
    }

    /// <summary>
    /// Точки касания и центр скругления для угла, образованного лучами
    /// corner-&gt;farA и corner-&gt;farB.
    /// </summary>
    private static string? CornerGeometry(Vector2d corner, Vector2d farA, Vector2d farB,
                                          double radius, bool checkReach,
                                          out Vector2d ta, out Vector2d tb, out Vector2d center)
    {
        ta = tb = center = corner;

        Vector2d da = (farA - corner).Normalized();
        Vector2d db = (farB - corner).Normalized();

        double half = Math.Acos(Math.Clamp(Vector2d.Dot(da, db), -1, 1)) * 0.5;
        if (half < 1e-6 || half > Math.PI / 2 - 1e-6) return "This corner angle cannot be filleted.";

        double tanDist = radius / Math.Tan(half);
        if (checkReach && (tanDist > (farA - corner).Length || tanDist > (farB - corner).Length))
            return "Radius is too large for these lines.";

        ta = corner + da * tanDist;
        tb = corner + db * tanDist;
        center = corner + (da + db).Normalized() * (radius / Math.Sin(half));
        return null;
    }

    /// <summary>
    /// Находит общий угол двух линий: концы, которые совпадают, и два дальних конца.
    /// Возвращает концы КАЖДОЙ линии по отдельности — у соседних отрезков
    /// это может быть один и тот же объект точки.
    /// </summary>
    private static bool Corner(SkLine a, SkLine b,
                               out SkPoint cornerA, out SkPoint cornerB,
                               out SkPoint farA, out SkPoint farB)
    {
        const double eps = 1e-7;
        SkPoint[] aEnds = { a.A, a.B };
        SkPoint[] aFar = { a.B, a.A };
        SkPoint[] bEnds = { b.A, b.B };
        SkPoint[] bFar = { b.B, b.A };

        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                if (ReferenceEquals(aEnds[i], bEnds[j]) || (aEnds[i].P - bEnds[j].P).Length < eps)
                {
                    cornerA = aEnds[i]; cornerB = bEnds[j];
                    farA = aFar[i]; farB = bFar[j];
                    return true;
                }

        cornerA = a.A; cornerB = b.A; farA = a.B; farB = b.B;
        return false;
    }

    /// <summary>
    /// Заменяет угловой конец линии НОВОЙ точкой.
    ///
    /// Именно заменяет, а не двигает. Соседние отрезки, построенные
    /// прямоугольником или ломаной, делят один и тот же объект точки:
    /// сдвинуть его — значит утащить за собой обе линии сразу, а вторая
    /// подрезка тут же перезапишет первую. Раньше из-за этого угол уезжал
    /// в сторону, а дуга строилась не от того места.
    ///
    /// Ограничения, ссылавшиеся на старую точку, после замены на неё уже
    /// не влияют — это цена операции, разрывающей угол.
    /// </summary>
    private static SkPoint ReplaceCorner(SkLine l, SkPoint corner, Vector2d to)
    {
        var fresh = new SkPoint(to) { Owner = l };
        if (ReferenceEquals(l.A, corner)) l.A = fresh;
        else l.B = fresh;
        return fresh;
    }

    /// <summary>
    /// Расстояние от точки до ПРЯМОЙ, содержащей отрезок, со знаком.
    ///
    /// Именно до прямой, а не до отрезка: расстояние между параллельными
    /// линиями не зависит от того, перекрываются ли они по длине.
    /// </summary>
    private static double PointToLine(Vector2d p, SkLine l)
    {
        Vector2d d = l.B.P - l.A.P;
        double len = d.Length;
        if (len < 1e-12) return (p - l.A.P).Length;
        return ((p.X - l.A.P.X) * d.Y - (p.Y - l.A.P.Y) * d.X) / len;
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Меняет роль выбранных элементов (объект / вспомогательная / осевая).</summary>
    public static void SetRole(GL3DView view, SkRole role)
    {
        foreach (var e in Selected(view)) e.Role = role;
        view.MarkDirty();
    }
}
