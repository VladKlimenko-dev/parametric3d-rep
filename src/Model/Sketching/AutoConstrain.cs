using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// Автоматическая привязка новых элементов эскиза.
///
/// Задача простая по формулировке и коварная по последствиям: только что
/// нарисованный отрезок не должен висеть в воздухе. Если он почти
/// горизонтален — вероятно, пользователь хотел горизонтальный. Если его
/// конец лёг на другую линию — вероятно, он хотел совпадения.
///
/// Три правила, которых стоит держаться, иначе автопривязка начинает
/// мешать больше, чем помогать:
///
///   1. Никогда не создавать ограничение, которое конфликтует
///      с существующими. Проверка та же, что и при ручном наложении.
///   2. Никогда не создавать дубликат.
///   3. Допуски задаются в ПИКСЕЛЯХ экрана, а не в единицах модели:
///      «почти горизонтально» — это про то, как оно выглядит, и на любом
///      масштабе должно означать одно и то же.
/// </summary>
public static class AutoConstrain
{
    /// <summary>Разбирает только что добавленный элемент и вешает на него привязки.</summary>
    public static void Apply(Sketch sk, SkEntity added)
    {
        var s = Settings.Current;
        if (!s.AutoConstrainEnabled) return;
        if (added.Role == SkRole.Reference) return;      // проекции не привязываем

        double tol = Math.Max(1e-9, sk.AutoTolerance);
        double angleTol = s.AutoConstrainAngleTolDeg * Math.PI / 180.0;

        switch (added)
        {
            case SkLine line:
                ApplyToLine(sk, line, tol, angleTol);
                break;

            case SkCircle c:
                ApplyToPoint(sk, c.Center, c, tol);
                break;

            case SkArc arc:
                ApplyToPoint(sk, arc.Center, arc, tol);
                ApplyToPoint(sk, arc.Start, arc, tol);
                ApplyToPoint(sk, arc.End, arc, tol);
                break;

            case SkEllipse el:
                ApplyToPoint(sk, el.Center, el, tol);
                break;

            case SkPoint p:
                ApplyToPoint(sk, p, added, tol);
                break;
        }

        // Размеры здесь не ставятся. Ими занимается DefaultDimensions:
        // он видит эскиз целиком и подбирает МИНИМАЛЬНЫЙ набор, а разбор
        // по одному элементу неизбежно наставил бы лишних.
    }

    // ------------------------------------------------------------- линии

    private static void ApplyToLine(Sketch sk, SkLine line, double tol, double angleTol)
    {
        ApplyToPoint(sk, line.A, line, tol);
        ApplyToPoint(sk, line.B, line, tol);

        Vector2d d = line.B.P - line.A.P;
        if (d.Length < tol) return;
        d = d.Normalized();

        // Горизонталь и вертикаль важнее прочего: они читаются с первого
        // взгляда и почти всегда именно то, чего хотел пользователь.
        double angle = Math.Atan2(d.Y, d.X);
        if (Near(angle, 0, angleTol) || Near(Math.Abs(angle), Math.PI, angleTol))
        {
            TryAdd(sk, ConstraintType.Horizontal, line);
            return;
        }
        if (Near(Math.Abs(angle), Math.PI / 2, angleTol))
        {
            TryAdd(sk, ConstraintType.Vertical, line);
            return;
        }

        // Параллельность и перпендикулярность назначаются только к линиям,
        // имеющим с новой ОБЩИЙ УЗЕЛ.
        //
        // Раньше искалась любая подходящая по направлению линия во всём
        // эскизе. На ломаной это оборачивалось тем, что третий сегмент
        // объявлялся перпендикулярным первому, решатель разворачивал уже
        // нарисованное, и ломаная переставала повторять точки, по которым
        // щёлкали. Смежность — то самое «ближайшее», о котором есть смысл
        // догадываться; всё остальное пользователь задаёт сам.
        foreach (var other in sk.Entities)
        {
            if (ReferenceEquals(other, line) || other is not SkLine l2) continue;
            if (l2.Role == SkRole.Reference) continue;
            if (!SharesEndpoint(line, l2)) continue;

            Vector2d d2 = l2.B.P - l2.A.P;
            if (d2.Length < tol) continue;
            d2 = d2.Normalized();

            double cos = Math.Abs(Vector2d.Dot(d, d2));
            if (cos > Math.Cos(angleTol)) { TryAdd(sk, ConstraintType.Parallel, line, l2); return; }
            if (cos < Math.Sin(angleTol)) { TryAdd(sk, ConstraintType.Perpendicular, line, l2); return; }
        }
    }

    // ------------------------------------------------------------- точки

    private static void ApplyToPoint(Sketch sk, SkPoint p, SkEntity owner, double tol)
    {
        // Совпадение с уже существующей точкой.
        foreach (var q in sk.AllPoints())
        {
            if (ReferenceEquals(q, p)) continue;
            if (ReferenceEquals(q.Owner, owner)) continue;      // свои же концы не связываем
            if ((q.P - p.P).Length > tol) continue;

            TryAdd(sk, ConstraintType.Coincident, p, q);
            return;
        }

        // Точка на кривой.
        foreach (var e in sk.Entities)
        {
            if (ReferenceEquals(e, owner) || e is SkPoint) continue;
            if (e.Role == SkRole.Reference) continue;
            if (e.DistanceTo(p.P) > tol) continue;

            TryAdd(sk, ConstraintType.PointOnEntity, p, e);
            return;
        }
    }

    // ------------------------------------------------------------ общее

    /// <summary>
    /// Добавляет ограничение, если оно не дубликат и ни с чем не спорит.
    ///
    /// Автопривязка не имеет права ставить конфликтующее ограничение:
    /// пользователь его не запрашивал и не поймёт, откуда взялся красный
    /// значок и почему эскиз перестал решаться.
    /// </summary>
    private static void TryAdd(Sketch sk, ConstraintType type, params SkEntity[] items)
    {
        if (ConstraintConflicts.Find(sk, type, items).Count > 0) return;
        sk.AddConstraint(type, items);
    }

    private static bool Near(double a, double b, double tol) => Math.Abs(a - b) < tol;

    private static bool SharesEndpoint(SkLine a, SkLine b) =>
        Same(a.A, b.A) || Same(a.A, b.B) || Same(a.B, b.A) || Same(a.B, b.B);

    private static bool Same(SkPoint p, SkPoint q) =>
        ReferenceEquals(p, q) || (p.P - q.P).Length < 1e-7;
}
