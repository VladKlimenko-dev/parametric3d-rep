using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// Триангуляция плоского многоугольника с отверстиями (ear clipping + мосты к дыркам).
///
/// Это то, чего не хватало для «построения 3D тел»: любая плоская крышка
/// выдавливания, любая грань с отверстием проходит через этот класс.
/// Вход — контуры в 2D (UV поверхности), выход — индексы треугольников.
/// </summary>
public static class Triangulator
{
    /// <param name="outer">Внешний контур, без повторения первой точки.</param>
    /// <param name="holes">Внутренние контуры (отверстия).</param>
    /// <param name="pts">Итоговый список точек (внешний контур + дырки, возможно с дублями-мостами).</param>
    /// <returns>Индексы треугольников (по 3) в системе <paramref name="pts"/>. Ориентация CCW.</returns>
    public static List<int> Triangulate(IReadOnlyList<Vector2d> outer,
                                        IReadOnlyList<IReadOnlyList<Vector2d>> holes,
                                        out List<Vector2d> pts)
    {
        pts = new List<Vector2d>();
        var tris = new List<int>();
        if (outer == null || outer.Count < 3) return tris;

        // 1. Внешний контур приводим к CCW.
        var outerList = new List<Vector2d>(outer);
        if (GeomUtil.SignedArea(outerList) < 0) outerList.Reverse();

        // 2. Дырки приводим к CW и сортируем по убыванию максимального X.
        var holeList = new List<List<Vector2d>>();
        if (holes != null)
        {
            foreach (var h in holes)
            {
                if (h == null || h.Count < 3) continue;
                var hl = new List<Vector2d>(h);
                if (GeomUtil.SignedArea(hl) > 0) hl.Reverse();
                holeList.Add(hl);
            }
            holeList.Sort((a, b) => MaxX(b).CompareTo(MaxX(a)));
        }

        // 3. Вшиваем дырки мостами во внешний контур -> получаем простой многоугольник.
        var poly = new List<Vector2d>(outerList);
        foreach (var h in holeList)
            BridgeHole(poly, h);

        pts = poly;

        // 4. Ear clipping. Допуски задаются от размера самого многоугольника:
        //    сравнения идут по векторным произведениям, то есть по площадям,
        //    и абсолютная константа для миллиметров и для метров означала бы
        //    совершенно разную строгость.
        EarClip(poly, tris, Extent(poly));
        return tris;
    }

    /// <summary>Наибольший размер габарита — им масштабируются все допуски.</summary>
    private static double Extent(List<Vector2d> p)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var v in p)
        {
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }
        return Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
    }

    private static double MaxX(List<Vector2d> p)
    {
        double m = double.MinValue;
        foreach (var v in p) if (v.X > m) m = v.X;
        return m;
    }

    /// <summary>Алгоритм Эберли: соединяем самую правую точку дырки с видимой точкой контура.</summary>
    private static void BridgeHole(List<Vector2d> poly, List<Vector2d> hole)
    {
        // M — самая правая точка дырки
        int mi = 0;
        for (int i = 1; i < hole.Count; i++)
            if (hole[i].X > hole[mi].X) mi = i;
        Vector2d M = hole[mi];

        // Луч +X из M: ищем ближайшее пересечение с ребром контура
        double bestT = double.MaxValue;
        int bestEdge = -1;
        Vector2d hitPoint = default;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2d a = poly[i], b = poly[(i + 1) % poly.Count];
            if ((a.Y > M.Y) == (b.Y > M.Y)) continue;      // ребро не пересекает горизонталь
            double t = (M.Y - a.Y) / (b.Y - a.Y);
            double x = a.X + (b.X - a.X) * t;
            double dx = x - M.X;
            if (dx < -Tol.Linear) continue;                 // слева от M
            if (dx < bestT) { bestT = dx; bestEdge = i; hitPoint = new Vector2d(x, M.Y); }
        }

        int bridgeIndex;
        if (bestEdge < 0)
        {
            // Вырожденный случай — берём просто ближайшую точку контура.
            bridgeIndex = 0;
            double bd = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                double d = (poly[i] - M).LengthSquared;
                if (d < bd) { bd = d; bridgeIndex = i; }
            }
        }
        else
        {
            // P — конец пересечённого ребра с большим X
            int i0 = bestEdge, i1 = (bestEdge + 1) % poly.Count;
            bridgeIndex = poly[i0].X > poly[i1].X ? i0 : i1;

            // Проверяем вогнутые вершины внутри треугольника (M, hitPoint, P)
            Vector2d P = poly[bridgeIndex];
            double bestAngle = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                if (i == bridgeIndex) continue;
                Vector2d R = poly[i];
                if (!IsReflex(poly, i)) continue;
                if (!PointInTriangleInclusive(R, M, hitPoint, P)) continue;
                Vector2d d = R - M;
                double ang = Math.Abs(Math.Atan2(d.Y, d.X));
                if (ang < bestAngle) { bestAngle = ang; bridgeIndex = i; }
            }
        }

        // ПОСТАВКА 4: ВЕРШИНА МОСТА С ДУБЛИКАТАМИ. После вшивания отверстий конец каждого моста повторён в многоугольнике;
        // если следующее отверстие выбирает мостом такую вершину, нужен экземпляр, во ВНУТРЕННИЙ УГОЛ которого смотрит
        // направление на отверстие. Прежде брался первый найденный индекс: неверный экземпляр делал многоугольник
        // самоперекрывающимся, ушная триангуляция давала вывернутые треугольники (плоская грань реалистичной детали с 7
        // отверстиями: покрытие 19 222 против 15 112 мм²). Без дубликатов или без подходящего угла — выбор прежний.
        {
            var P0 = poly[bridgeIndex];
            var same = new List<int>();
            for (int i = 0; i < poly.Count; i++) if ((poly[i] - P0).LengthSquared < 1e-24) same.Add(i);
            if (same.Count > 1)
            {
                foreach (int j in same)
                    if (InWedge(poly, j, M)) { bridgeIndex = j; break; }
            }
        }
        // Вшиваем: poly[..bridgeIndex] + hole(от mi по кругу) + hole[mi] + poly[bridgeIndex..]
        var inserted = new List<Vector2d>();
        for (int k = 0; k <= hole.Count; k++)
            inserted.Add(hole[(mi + k) % hole.Count]);
        inserted.Add(poly[bridgeIndex]);

        poly.InsertRange(bridgeIndex + 1, inserted);
    }

    /// <summary>
    /// Лежит ли направление из вершины j на точку M внутри её внутреннего угла (многоугольник против часовой стрелки:
    /// угол — против часовой от направления на следующую вершину до направления на предыдущую).
    /// </summary>
    private static bool InWedge(List<Vector2d> p, int j, Vector2d M)
    {
        int n = p.Count;
        var P = p[j];
        Vector2d next = p[(j + 1) % n] - P, prev = p[(j + n - 1) % n] - P, d = M - P;
        if (next.LengthSquared < 1e-24 || prev.LengthSquared < 1e-24 || d.LengthSquared < 1e-24) return false;
        static double Ang(Vector2d from, Vector2d to)
        {
            double a = Math.Atan2(from.X * to.Y - from.Y * to.X, from.X * to.X + from.Y * to.Y);
            return a <= 0 ? a + 2 * Math.PI : a;                 // (0, 2π] против часовой от from до to
        }
        double toPrev = Ang(next, prev), toD = Ang(next, d);
        return toD < toPrev;
    }

    private static bool IsReflex(List<Vector2d> p, int i)
    {
        Vector2d a = p[(i - 1 + p.Count) % p.Count], b = p[i], c = p[(i + 1) % p.Count];
        return Cross(b - a, c - b) < 0;
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Точка внутри треугольника ИЛИ на его границе. Для поиска видимой вершины при вшивании дырки.</summary>
    private static bool PointInTriangleInclusive(Vector2d p, Vector2d a, Vector2d b, Vector2d c)
    {
        double d1 = Cross(b - a, p - a);
        double d2 = Cross(c - b, p - b);
        double d3 = Cross(a - c, p - c);
        bool neg = (d1 < -Tol.Linear) || (d2 < -Tol.Linear) || (d3 < -Tol.Linear);
        bool pos = (d1 > Tol.Linear) || (d2 > Tol.Linear) || (d3 > Tol.Linear);
        return !(neg && pos);
    }

    /// <summary>
    /// Точка СТРОГО внутри треугольника. Именно этот вариант нужен при отсечении
    /// ушей: вершина, лежащая на границе, ухо не портит.
    /// </summary>
    private static bool PointStrictlyInTriangle(Vector2d p, Vector2d a, Vector2d b, Vector2d c, double eps)
    {
        return Cross(b - a, p - a) > eps
            && Cross(c - b, p - b) > eps
            && Cross(a - c, p - c) > eps;
    }

    /// <summary>
    /// Отсечение ушей.
    ///
    /// ГЛАВНАЯ ТОНКОСТЬ — вершины-дубликаты. Вшивание дырки мостом намеренно
    /// повторяет две точки: конец моста на внешнем контуре и начальную точку
    /// дырки. Эти дубликаты лежат ТОЧНО на рёбрах соседних треугольников.
    ///
    /// Прежняя проверка «точка внутри ИЛИ на границе» считала их помехой,
    /// поэтому подходящего уха не находилось ни разу, и срабатывал аварийный
    /// путь «отрезать что попало». Треугольники при этом накладывались друг
    /// на друга и вылезали за контур — на экране это выглядело как клинья
    /// поверх заливки, а отверстие оставалось закрашенным.
    ///
    /// Теперь проверка строгая, а вершины, совпадающие с углами уха
    /// по координатам, пропускаются явно.
    /// </summary>
    private static void EarClip(List<Vector2d> poly, List<int> tris, double extent)
    {
        int n = poly.Count;
        if (n < 3) return;

        double epsArea = 1e-12 * extent * extent;
        double epsDist = 1e-9 * extent;

        bool Same(Vector2d a, Vector2d b) =>
            Math.Abs(a.X - b.X) < epsDist && Math.Abs(a.Y - b.Y) < epsDist;

        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(i);

        int guard = 0;
        int maxGuard = n * n + 16;

        while (idx.Count > 3 && guard++ < maxGuard)
        {
            bool clipped = false;
            for (int k = 0; k < idx.Count; k++)
            {
                int ip = idx[(k - 1 + idx.Count) % idx.Count];
                int ic = idx[k];
                int inx = idx[(k + 1) % idx.Count];

                Vector2d a = poly[ip], b = poly[ic], c = poly[inx];
                if (Cross(b - a, c - b) <= epsArea) continue;   // вогнутая или вырожденная

                bool ok = true;
                for (int m = 0; m < idx.Count; m++)
                {
                    int im = idx[m];
                    if (im == ip || im == ic || im == inx) continue;

                    Vector2d p = poly[im];
                    if (Same(p, a) || Same(p, b) || Same(p, c)) continue;   // дубликат моста
                    if (PointStrictlyInTriangle(p, a, b, c, epsArea)) { ok = false; break; }
                    // ВЫРОЖДЕННЫЕ РАСКЛАДКИ (r22): вершина ТОЧНО на открытой диагонали a–c (отверстия в целых координатах на
                    // одних прямых) строгой проверкой не видна — ухо принималось и резало многоугольник через неё. Стороны
                    // ab и bc — рёбра самого многоугольника (на них лежат дубликаты мостов), их это не касается.
                    if (Math.Abs(Cross(c - a, p - a)) <= epsArea)
                    {
                        double t = Vector2d.Dot(p - a, c - a) / Math.Max(1e-300, (c - a).LengthSquared);
                        if (t > 0 && t < 1) { ok = false; break; }
                    }
                }
                if (!ok) continue;

                tris.Add(ip); tris.Add(ic); tris.Add(inx);
                idx.RemoveAt(k);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                // Сюда попадают только по-настоящему негодные входные данные:
                // самопересечения, дырка, вылезающая за внешний контур.
                // Отрезаем принудительно, лишь бы не зациклиться.
                tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
                idx.RemoveAt(1);
            }
        }

        if (idx.Count == 3)
        {
            tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
        }
    }
}
