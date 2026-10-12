namespace Parametric3D.Kernel.Exact;

/// <summary>
/// СЛИЯНИЕ ВЫПУКЛЫХ КУСКОВ ОДНОЙ ГРАНИ — ТОЧНО.
///
/// Разбиение пространства режет грани на выпуклые куски и обратно их
/// не собирает; вход и вовсе приходит треугольниками. Каждый лишний кусок —
/// лишняя работа во всех фазах следующей операции, и от операции к операции
/// дробление копится.
///
/// ЧТО СЛИВАЕТСЯ. Два куска одной метки на одной опорной плоскости с общим
/// ребром — если объединение выпукло. Выпуклость нужна: разрез в булевой
/// держится на том, что прямая пересекает многоугольник не больше двух раз.
///
/// КРИТЕРИЙ ТОЧНЫЙ. Пусть H_P — пересечение полуплоскостей P без общей,
/// H_Q — то же для Q. Тогда H_P ∩ H_Q всегда содержится в P ∪ Q: точка
/// по одну сторону общей плоскости лежит в P, по другую — в Q. Обратное
/// включение выполнено, когда P ⊂ H_Q и Q ⊂ H_P, а для выпуклых кусков
/// это проверяется по вершинам. Значит объединение выпукло и в точности
/// равно H_P ∩ H_Q, если все вершины P внутри ограничивающих Q (кроме общей)
/// и наоборот. Только знаки целых чисел.
///
/// Объём и замкнутость от слияния не меняются: область та же.
/// </summary>
public static class ExactMerge
{
    public static List<ExactPolygon> Fragments(PlaneRegistry R, List<ExactPolygon> polys)
    {
        Diagnostics.Prof.Count("merge input polygons", polys.Count);
        var groups = new Dictionary<(uint Tag, OPlane S), List<ExactPolygon>>();
        foreach (var p in polys)
        {
            var key = (p.Tag, p.Support);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<ExactPolygon>();
            list.Add(p);
        }

        var result = new List<ExactPolygon>(polys.Count);
        foreach (var list in groups.Values)
            result.AddRange(list.Count == 1 ? list : MergeGroup(R, list));

        Diagnostics.Prof.Count("merge output polygons", result.Count);
        return result;
    }

    private static List<ExactPolygon> MergeGroup(PlaneRegistry R, List<ExactPolygon> group)
    {
        var alive = new List<ExactPolygon>(group);
        // A03. У группы одна опорная плоскость и один реестр. Каноническая
        // прямая для Bounds.Id неизменна; её BigInteger/GCD-расчёт выполняется
        // один раз за группу, а не заново при каждой проверке соседних сторон.
        // Кэш локален вызову и не переживает изменения геометрии.
        var lines = new Dictionary<int, (System.Numerics.BigInteger, System.Numerics.BigInteger,
            System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger,
            System.Numerics.BigInteger)>();
        bool merged = true;

        while (merged)
        {
            merged = false;

            // Рёбра по вершинам: (u, v) у одного и (v, u) у другого — общее ребро.
            var edges = new Dictionary<(int, int), (int Poly, int Edge)>();
            var verts = new int[alive.Count][];

            for (int k = 0; k < alive.Count; k++)
            {
                verts[k] = alive[k].Vertices(R).ToArray();
                int n = verts[k].Length;
                for (int i = 0; i < n; i++)
                    edges[(verts[k][i], verts[k][(i + 1) % n])] = (k, i);
            }

            var dead = new bool[alive.Count];
            var fresh = new List<ExactPolygon>();

            // УСКОРЕНИЕ. Прежде после каждого удачного слияния проход начинался
            // заново — со сборкой словаря рёбер по всей группе: квадратичная работа
            // на сотнях кусков. Теперь в одном проходе сливаются все найденные пары;
            // использованные куски помечены и больше не участвуют, новые идут
            // в следующий проход. Каждое слияние по-прежнему проверяется на выпуклость.
            for (int k = 0; k < alive.Count; k++)
            {
                if (dead[k]) continue;
                int n = verts[k].Length;

                for (int i = 0; i < n; i++)
                {
                    int u = verts[k][i], v = verts[k][(i + 1) % n];
                    if (!edges.TryGetValue((v, u), out var other) || other.Poly == k || dead[other.Poly]) continue;

                    ExactPolygon? m = TryMerge(R, alive[k], verts[k], i, alive[other.Poly], verts[other.Poly], other.Edge, lines);
                    if (m == null) continue;

                    dead[k] = dead[other.Poly] = true;
                    fresh.Add(m);
                    merged = true;
                    break;
                }
            }

            if (merged)
            {
                var next = new List<ExactPolygon>(alive.Count);
                for (int k = 0; k < alive.Count; k++) if (!dead[k]) next.Add(alive[k]);
                next.AddRange(fresh);
                alive = next;
            }
        }

        return alive;
    }

    /// <summary>Объединение P и Q по общему ребру iP / iQ, если оно выпукло; иначе null.</summary>
    private static ExactPolygon? TryMerge(PlaneRegistry R,
                                          ExactPolygon P, int[] vp, int iP,
                                          ExactPolygon Q, int[] vq, int iQ,
                                          Dictionary<int, (System.Numerics.BigInteger, System.Numerics.BigInteger,
                                              System.Numerics.BigInteger, System.Numerics.BigInteger,
                                              System.Numerics.BigInteger, System.Numerics.BigInteger)> lines)
    {
        // Критерий выпуклости: вершины каждого внутри ограничивающих другого, кроме общей.
        for (int j = 0; j < Q.Bounds.Length; j++)
        {
            if (j == iQ) continue;
            foreach (int v in vp) if (R.Side(v, Q.Bounds[j]) > 0) return null;
        }

        for (int j = 0; j < P.Bounds.Length; j++)
        {
            if (j == iP) continue;
            foreach (int v in vq) if (R.Side(v, P.Bounds[j]) > 0) return null;
        }

        // Обход объединения: от конца общего ребра по P до его начала,
        // затем по Q обратно. Ребро i лежит на Bounds[i] от вершины i к i+1.
        int np = P.Bounds.Length, nq = Q.Bounds.Length;
        var bounds = new List<OPlane>(np + nq - 2);

        for (int j = 1; j < np; j++) bounds.Add(P.Bounds[(iP + j) % np]);
        for (int j = 1; j < nq; j++) bounds.Add(Q.Bounds[(iQ + j) % nq]);

        // Соседние ограничивающие на одной прямой опорной плоскости не дают
        // вершины — три плоскости пересекаются по прямой. Такая пара сводится
        // к одной: ребро просто продолжается.
        bool changed = true;
        while (changed && bounds.Count >= 3)
        {
            changed = false;
            for (int j = 0; j < bounds.Count; j++)
            {
                OPlane a = bounds[j], b = bounds[(j + 1) % bounds.Count];
                if (a.Id == b.Id || Line(a.Id).Equals(Line(b.Id)))
                {
                    bounds.RemoveAt((j + 1) % bounds.Count);
                    changed = true;
                    break;
                }
            }
        }

        if (bounds.Count < 3) return null;

        var merged = new ExactPolygon(P.Support, bounds.ToArray(), P.Tag);

        // Каждая вершина объединения обязана существовать.
        try { merged.Vertices(R); }
        catch (ArgumentException) { return null; }

        return merged;

        (System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger,
            System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger) Line(int id)
        {
            if (!lines.TryGetValue(id, out var line))
                lines[id] = line = R.Line(P.Support.Id, id);
            return line;
        }
    }
}
