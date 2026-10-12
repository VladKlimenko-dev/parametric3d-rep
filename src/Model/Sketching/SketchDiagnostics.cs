using OpenTK.Mathematics;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// Проверка эскиза на пригодность для построения тела.
///
/// Выдавливание, вращение и протягивание требуют от контура двух вещей:
/// он не должен пересекать сам себя и не должен иметь разрывов. Оба
/// нарушения дают либо ошибку построения, либо, что хуже, молча неверное
/// тело. Показать их надо ДО того, как пользователь нажмёт кнопку.
///
/// Проверяются только рабочие элементы. Вспомогательные, осевые и
/// ссылочные линии в контур не входят, пересекаться им никто не запрещает.
///
/// Результат складывается в сами элементы: рисующий код читает признаки,
/// а не повторяет вычисления.
/// </summary>
public static class SketchDiagnostics
{
    /// <summary>Пересчитывает признаки ошибок по всему эскизу.</summary>
    public static void Update(Sketch sk)
    {
        foreach (var e in sk.Entities)
        {
            e.SelfIntersects = false;
            e.CrossesOther = false;
        }
        foreach (var p in sk.AllPoints()) p.Dangling = false;

        if (!Core.Settings.Current.HighlightSketchErrors) return;

        var parts = Build(sk);

        // ---- пересечения

        for (int i = 0; i < parts.Count; i++)
        {
            if (SelfCrosses(parts[i].Points, parts[i].Closed))
                parts[i].Entity.SelfIntersects = true;

            for (int j = i + 1; j < parts.Count; j++)
            {
                // Габариты отсекают подавляющее большинство пар за две
                // проверки — без этого сравнение шло бы по всем звеньям.
                if (!Overlap(parts[i], parts[j])) continue;

                if (Crosses(parts[i], parts[j]))
                {
                    parts[i].Entity.CrossesOther = true;
                    parts[j].Entity.CrossesOther = true;
                }
            }
        }

        // ---- висящие концы

        double tol = Math.Max(sk.AutoTolerance, 1e-7);

        // Концы собираются вместе с их владельцами: один и тот же объект
        // точки попадает в список ДВАЖДЫ, если на нём сходятся два сегмента.
        // Именно это и есть замкнутый стык.
        var ends = new List<(SkPoint P, SkEntity Owner)>();
        foreach (var part in parts)
        {
            if (part.Closed) continue;                 // у замкнутых концов нет
            foreach (var p in Endpoints(part.Entity)) ends.Add((p, part.Entity));
        }

        // Явно заданные совпадения тоже смыкают контур, даже если решатель
        // ещё не свёл точки вплотную.
        var joined = new HashSet<(SkPoint, SkPoint)>();
        foreach (var c in sk.Constraints)
        {
            if (!c.Enabled || c.Type != ConstraintType.Coincident) continue;
            if (c.Items.Count < 2) continue;
            if (c.Items[0] is not SkPoint a || c.Items[1] is not SkPoint b) continue;
            joined.Add((a, b));
            joined.Add((b, a));
        }

        for (int i = 0; i < ends.Count; i++)
        {
            bool touching = false;

            for (int j = 0; j < ends.Count && !touching; j++)
            {
                // Сравниваются РАЗНЫЕ записи списка, а не разные объекты.
                //
                // Здесь и была ошибка: общий узел двух сегментов ломаной —
                // это один объект точки, встречающийся в списке дважды.
                // Проверка «не тот же объект» отбрасывала именно тот
                // случай, ради которого всё и затевалось, и каждый стык
                // объявлялся висящим.
                if (i == j) continue;

                // Два конца ОДНОГО элемента друг друга не смыкают.
                if (ReferenceEquals(ends[i].Owner, ends[j].Owner)) continue;

                SkPoint p = ends[i].P, q = ends[j].P;

                if (ReferenceEquals(p, q)) touching = true;
                else if (joined.Contains((p, q))) touching = true;
                else if ((p.P - q.P).Length < tol) touching = true;
            }

            if (!touching) ends[i].P.Dangling = true;
        }
    }

    // ------------------------------------------------------------ разбиение

    private sealed class Part
    {
        public SkEntity Entity = null!;
        public List<Vector2d> Points = new();
        public bool Closed;
        public Vector2d Min, Max;
    }

    private static List<Part> Build(Sketch sk)
    {
        var list = new List<Part>();

        foreach (var e in sk.Entities)
        {
            if (e.Role != SkRole.Object) continue;
            if (e is SkPoint) continue;

            var pts = new List<Vector2d>();
            e.Tessellate(pts, true);
            if (pts.Count < 2) continue;

            var part = new Part
            {
                Entity = e,
                Points = pts,
                Closed = e is SkCircle or SkEllipse
            };

            part.Min = part.Max = pts[0];
            foreach (var p in pts)
            {
                part.Min = Vector2d.ComponentMin(part.Min, p);
                part.Max = Vector2d.ComponentMax(part.Max, p);
            }

            list.Add(part);
        }

        return list;
    }

    private static IEnumerable<SkPoint> Endpoints(SkEntity e)
    {
        switch (e)
        {
            case SkLine l: yield return l.A; yield return l.B; break;
            case SkArc a: yield return a.Start; yield return a.End; break;
            case SkSpline s when s.Ctrl.Count >= 2:
                yield return s.Ctrl[0];
                yield return s.Ctrl[^1];
                break;
        }
    }

    // ------------------------------------------------------------ геометрия

    private static bool Overlap(Part a, Part b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X &&
        a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;

    private static bool Crosses(Part a, Part b)
    {
        int na = a.Closed ? a.Points.Count : a.Points.Count - 1;
        int nb = b.Closed ? b.Points.Count : b.Points.Count - 1;

        for (int i = 0; i < na; i++)
        {
            Vector2d a1 = a.Points[i], a2 = a.Points[(i + 1) % a.Points.Count];

            for (int j = 0; j < nb; j++)
            {
                Vector2d b1 = b.Points[j], b2 = b.Points[(j + 1) % b.Points.Count];
                if (SegmentsCross(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    private static bool SelfCrosses(List<Vector2d> pts, bool closed)
    {
        int n = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            Vector2d a1 = pts[i], a2 = pts[(i + 1) % pts.Count];

            for (int j = i + 2; j < n; j++)
            {
                if (closed && i == 0 && j == n - 1) continue;   // соседи по кольцу

                Vector2d b1 = pts[j], b2 = pts[(j + 1) % pts.Count];
                if (SegmentsCross(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Строгое пересечение во ВНУТРЕННИХ точках.
    ///
    /// Строгость здесь и есть смысл: соседние отрезки контура сходятся
    /// концами, и это не пересечение, а нормальный стык. Нестрогая проверка
    /// объявила бы ошибкой любой замкнутый контур.
    /// </summary>
    private static bool SegmentsCross(Vector2d a1, Vector2d a2, Vector2d b1, Vector2d b2)
    {
        double d1 = Cross(b2 - b1, a1 - b1);
        double d2 = Cross(b2 - b1, a2 - b1);
        double d3 = Cross(a2 - a1, b1 - a1);
        double d4 = Cross(a2 - a1, b2 - a1);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
