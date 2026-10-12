using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// КАСАТЕЛЬНАЯ ЦЕПОЧКА РЁБЕР.
///
/// Скругление разрывает ребро на части: прямой участок — дуга — прямой
/// участок. Для геометрии это три разных ребра, для человека — одна
/// кромка, и фаску он назначает ей целиком. Выбирать три куска по одному,
/// помня, что между ними есть дуга, — работа не для человека.
///
/// ЧТО СЧИТАЕТСЯ ПРОДОЛЖЕНИЕМ. В общей вершине у каждого ребра есть
/// касательная, направленная ПРОЧЬ от вершины. Если у двух рёбер они
/// смотрят почти в противоположные стороны, кромка через вершину
/// проходит не изламываясь — это продолжение. Настоящий угол детали
/// даёт 90° и под порог не попадает.
///
/// ПОРОГ ШИРОКИЙ — И ЭТО ВЫНУЖДЕННО. Дуга скругления в теле не дуга,
/// а ломаная из хорд: соседние хорды сходятся под 174–178°, то есть
/// излом между ними 2–6°. Узкий порог остановил бы обход на первой же
/// хорде, и цепочка не прошла бы через скругление — ровно там, где она
/// и нужна.
///
/// ОБХОД ОСТАНАВЛИВАЕТСЯ НА РАЗВИЛКЕ. Если в вершине продолжений
/// несколько, ни одно не выбирается: угадывать за пользователя, какая
/// из двух кромок его, значит выделить лишнее, а лишнее в фаске — это
/// срезанный кусок детали.
/// </summary>
public static class EdgeChain
{
    /// <summary>
    /// Наибольший излом в вершине, при котором кромка считается продолжающейся.
    ///
    /// БЫЛО 20°, СТАЛО 10°. Широкий порог был вынужденной мерой: дуга
    /// скругления лежала в теле ломаной из хорд, соседние звенья сходились
    /// под 174–178°, и узкий порог остановил бы обход на первом же изломе.
    ///
    /// Теперь булева распознаёт цепочку хорд обратно в дугу, дуга —
    /// одно ребро, и касательная в её концах точна: излом ровно нулевой.
    /// Запас нужен только на погрешность счёта, и держать его широким
    /// вредно — под 20° попадают настоящие пологие рёбра деталей,
    /// и цепочка захватывает то, чего не просили. В фаске лишнее ребро
    /// означает срезанный кусок.
    /// </summary>
    public const double DefaultBreakDegrees = 10.0;

    /// <summary>
    /// Дополняет набор рёбер их касательными продолжениями.
    ///
    /// Исходные рёбра остаются в наборе всегда, даже если у них
    /// продолжений нет.
    /// </summary>
    public static List<TEdge> Grow(TSolid body, IEnumerable<TEdge> seeds,
                                   double breakDegrees = DefaultBreakDegrees) =>
        Grow(body, seeds, out _, breakDegrees);

    /// <summary>
    /// То же, но с объяснением.
    ///
    /// Объяснение нужно не для красоты: «цепочка не выросла» и «цепочка
    /// не запустилась» выглядят на экране одинаково — ничего не
    /// подсветилось, — а чинить их надо в разных местах. Здесь считается
    /// то, что различает эти два случая: сколько рёбер вообще сходилось
    /// в концах и какой излом оказался наименьшим.
    /// </summary>
    public static List<TEdge> Grow(TSolid body, IEnumerable<TEdge> seeds, out string why,
                                   double breakDegrees = DefaultBreakDegrees)
    {
        int neighbours = 0;
        double bestTurn = double.NaN;

        var found = new List<TEdge>();
        var seen = new HashSet<uint>();
        var queue = new Queue<TEdge>();

        foreach (var e in seeds)
            if (seen.Add(e.Id)) { found.Add(e); queue.Enqueue(e); }

        double cosLimit = Math.Cos((180.0 - breakDegrees) * Math.PI / 180.0);

        while (queue.Count > 0)
        {
            TEdge edge = queue.Dequeue();

            foreach (TVertex v in Ends(edge))
            {
                TEdge? next = Continuation(body, edge, v, cosLimit, ref neighbours, ref bestTurn);

                if (next != null && seen.Add(next.Id))
                {
                    found.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        why = found.Count > 1
            ? $"{found.Count} edges in the chain"
            : neighbours == 0
                ? "nothing shares the ends of this edge"
                : double.IsNaN(bestTurn)
                    ? $"{neighbours} edges meet at the ends, none of them continues it"
                    : $"{neighbours} edges meet at the ends; the straightest turns by " +
                      $"{bestTurn:0.#}\u00b0, and the limit is {breakDegrees:0.#}\u00b0";

        return found;
    }

    /// <summary>Концы ребра. У замкнутого он один, и продолжать нечего.</summary>
    private static IEnumerable<TVertex> Ends(TEdge e)
    {
        yield return e.V0;
        if (e.V1 != e.V0) yield return e.V1;
    }

    /// <summary>
    /// Единственное касательное продолжение в вершине, либо null.
    ///
    /// Возврат null означает и «продолжения нет», и «их несколько»:
    /// для обхода это одно и то же — дальше не идём.
    /// </summary>
    private static TEdge? Continuation(TSolid body, TEdge from, TVertex at, double cosLimit,
                                       ref int neighbours, ref double bestTurn)
    {
        // ОДНА КРОМКА — ПАРА ГРАНЕЙ.
        //
        // ИСПРАВЛЕНО. Кромка между двумя кривыми гранями (фаска и скругление) — ломаная
        // из хорд длиной до пары миллиметров, и на крутом изгибе соседние хорды
        // поворачиваются на 10–14°: порог по углу рвал её на куски. Если в вершине сходятся
        // ровно два ребра и оба разделяют одну и ту же пару граней, это куски одной линии
        // пересечения двух поверхностей — продолжение кромки при любом угле между хордами.
        // Там, где пара граней меняется, — прежний порог по углу.
        {
            var touching = new List<TEdge>();
            foreach (TEdge other in body.Edges)
                if (!ReferenceEquals(other, from) && Touches(other, at)) touching.Add(other);
            if (touching.Count == 1 && SameFaces(from, touching[0]))
            {
                neighbours++;
                return touching[0];
            }
        }

        Vector3d away = Away(from, at);
        if (away.LengthSquared < 1e-18) return null;

        TEdge? best = null;
        int count = 0;

        foreach (TEdge other in body.Edges)
        {
            if (ReferenceEquals(other, from)) continue;

            // Вершины сравниваются ПО ПОЛОЖЕНИЮ, а не по ссылке.
            //
            // После булевой тело собирается заново, и совпадающие точки
            // сшиваются с допуском. Обычно это даёт один объект вершины
            // на всех, но полагаться на это нельзя: достаточно одного
            // куска, пришедшего другим путём, и ссылки разойдутся при
            // совпадающих координатах. Сравнение по ссылке тогда молча
            // обрывает цепочку — ровно тот случай, когда «ничего
            // не выделяется» и непонятно почему.
            if (!Touches(other, at)) continue;

            neighbours++;

            Vector3d dir = Away(other, at);
            if (dir.LengthSquared < 1e-18) continue;

            // Обе касательные смотрят ПРОЧЬ от вершины, поэтому у гладкого
            // продолжения их скалярное произведение близко к −1.
            double dot = Math.Clamp(Vector3d.Dot(away, dir), -1, 1);
            double turn = 180.0 - Math.Acos(dot) * 180.0 / Math.PI;

            if (double.IsNaN(bestTurn) || turn < bestTurn) bestTurn = turn;

            if (dot > cosLimit) continue;

            count++;
            best = other;
        }

        return count == 1 ? best : null;
    }

    /// <summary>Разделяют ли два ребра одну и ту же пару граней.</summary>
    private static bool SameFaces(TEdge a, TEdge b)
    {
        if (a.FaceA == null || a.FaceB == null || b.FaceA == null || b.FaceB == null) return false;
        return (ReferenceEquals(a.FaceA, b.FaceA) && ReferenceEquals(a.FaceB, b.FaceB)) ||
               (ReferenceEquals(a.FaceA, b.FaceB) && ReferenceEquals(a.FaceB, b.FaceA));
    }

    /// <summary>Приходит ли ребро в эту вершину — по объекту либо по координатам.</summary>
    private static bool Touches(TEdge e, TVertex at)
    {
        if (e.V0 == at || e.V1 == at) return true;

        const double Tol = 1e-7;

        return (e.V0.P - at.P).LengthSquared < Tol * Tol
            || (e.V1.P - at.P).LengthSquared < Tol * Tol;
    }

    /// <summary>Единичная касательная ребра в вершине, направленная от неё.</summary>
    private static Vector3d Away(TEdge e, TVertex at)
    {
        // У замкнутого ребра оба конца — одна вершина; направление берём
        // от начала, иначе выбор стороны был бы произвольным.
        bool fromStart = e.V0 == at || (e.V0.P - at.P).LengthSquared <= (e.V1.P - at.P).LengthSquared;

        Vector3d t = e.Curve.TangentAt(fromStart ? 0.0 : 1.0);

        if (t.LengthSquared < 1e-18)
        {
            // Кривая не дала касательной — берём хорду.
            t = fromStart ? e.V1.P - e.V0.P : e.V0.P - e.V1.P;
            return t.LengthSquared < 1e-18 ? Vector3d.Zero : t.Normalized();
        }

        return (fromStart ? t : -t).Normalized();
    }
}
