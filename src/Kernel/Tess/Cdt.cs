using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// ТРИАНГУЛЯЦИЯ ДЕЛОНЕ С ОГРАНИЧЕНИЯМИ.
///
/// ДОБАВЛЕНО (этап 3). Ушная триангуляция срывается там, где ряды точек контура лежат на
/// прямой: ухо из трёх таких точек вырождено, годных ушей не находится, и она режет
/// внахлёст — у грани-трубки площадь сетки выходила 511 мм² вместо 352, с треугольником
/// поперёк всей поверхности.
///
/// Здесь: точки вставляются в треугольник-обёртку с перекладкой по Делоне, затем стороны
/// контуров восстанавливаются принудительными перекладками, затем отсекается всё, что вне
/// внешнего контура или внутри отверстия.
/// </summary>
public static class Cdt
{
    /// <summary>ДИАГНОСТИКА: причина отказа.</summary>
    public static Action<string>? Debug;

    /// <param name="outer">Внешний контур (без повтора первой точки).</param>
    /// <param name="holes">Отверстия.</param>
    /// <param name="pts">Итоговые точки: внешний контур, затем отверстия.</param>
    /// <returns>Индексы треугольников по три; null — не удалось.</returns>
    /// <summary>
    /// Триангуляция с ДОБАВОЧНЫМИ ВНУТРЕННИМИ ТОЧКАМИ. Они нужны сетке для расчёта: без них
    /// широкая грань разбивается лоскутами от края до края, а расчёту нужны треугольники,
    /// близкие к равносторонним.
    /// </summary>
    public static List<int>? Triangulate(IReadOnlyList<Vector2d> outer, IReadOnlyList<IReadOnlyList<Vector2d>> holes,
                                         out List<Vector2d> pts, IReadOnlyList<Vector2d>? inner)
    {
        var tris = Triangulate(outer, holes, out pts);
        if (tris == null || inner == null || inner.Count == 0) return tris;

        // Точка вставляется в тот треугольник, внутри которого лежит: он делится на три.
        foreach (var p in inner)
        {
            int at = -1;
            for (int i = 0; i + 2 < tris.Count && at < 0; i += 3)
                if (InTriangle(p, pts[tris[i]], pts[tris[i + 1]], pts[tris[i + 2]])) at = i;

            if (at < 0) continue;

            int a = tris[at], b = tris[at + 1], c = tris[at + 2];
            int n = pts.Count;
            pts.Add(p);

            tris[at] = a; tris[at + 1] = b; tris[at + 2] = n;
            tris.Add(b); tris.Add(c); tris.Add(n);
            tris.Add(c); tris.Add(a); tris.Add(n);
        }

        // ПОСЛЕ ВСТАВКИ СЕТКУ НАДО ПЕРЕСТРОИТЬ ПО ДЕЛОНЕ. Деление треугольника натрое само по
        // себе форму ухудшает: измерено — при шаге 5 мм плохих выходило 667 из 884, хуже, чем у
        // сетки для показа. Перестановка сторон это исправляет: сторона, у которой соседний
        // угол попадает в описанную окружность, разворачивается.
        FlipToDelaunay(tris, pts);
        return tris;
    }

    /// <summary>
    /// Перестановка сторон до выполнения условия Делоне. Условие проверяется определителем: если
    /// четвёртая точка внутри описанной окружности треугольника, сторону надо развернуть.
    /// </summary>
    public static void FlipToDelaunay(List<int> tris, List<Vector2d> pts, int maxPasses = 12)
    {
        if (maxPasses <= 0 || tris.Count < 6) return;
        // Расчётный post-process, не Legalize основной булевой разметки.
        // Старый owners оставлял ссылки на стороны УЖЕ изменённого треугольника.
        // Следующий flip мог склеить несоседние треугольники и изменить площадь.
        // Поддерживаем направленные стороны после каждого flip; все треугольники CCW.
        for (int t = 0; t + 2 < tris.Count; t += 3)
            if (Cross(pts[tris[t]], pts[tris[t + 1]], pts[tris[t + 2]]) < 0)
                (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);

        var map = Sides(tris);
        var queue = new Queue<(int, int)>();
        var pending = new HashSet<(int, int)>();
        void Enqueue(int a, int b)
        {
            var key = Key(a, b);
            if (pending.Add(key)) queue.Enqueue(key);
        }
        foreach (var side in map.Keys) Enqueue(side.Item1, side.Item2);
        long budget = (long)maxPasses * tris.Count * 6;
        while (queue.Count > 0 && budget-- > 0)
        {
            var (a, b) = queue.Dequeue();
            pending.Remove((a, b));
            // Граничная сторона имеет одного владельца и никогда не меняется.
            if (!map.TryGetValue((a, b), out int t1) || !map.TryGetValue((b, a), out int t2)) continue;
            int c = Third(tris, t1, a, b), d = Third(tris, t2, b, a);
            if (c < 0 || d < 0 || c == d) continue;
            if (!CanFlip(pts, a, b, c, d)) continue;
            if (!InCircleForFlip(pts[a], pts[b], pts[c], pts[d])) continue;
            if (map.ContainsKey((c, d)) || map.ContainsKey((d, c))) continue;
            Forget(map, tris, t1); Forget(map, tris, t2);
            tris[t1] = c; tris[t1 + 1] = a; tris[t1 + 2] = d;
            tris[t2] = d; tris[t2 + 1] = b; tris[t2 + 2] = c;
            Remember(map, tris, t1); Remember(map, tris, t2);
            if (CheckSides) VerifySides(map, tris);
            Enqueue(c, a); Enqueue(a, d); Enqueue(d, b); Enqueue(b, c);
        }

        if (queue.Count > 0) Debug?.Invoke("расчётная сетка: достигнут предел улучшения Делоне");
    }

    private static bool Degenerate(Vector2d a, Vector2d b, Vector2d c) =>
        Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) < 1e-12;

    /// <summary>Лежит ли d внутри окружности, описанной около abc (для перестановки сторон).</summary>
    private static bool InCircleForFlip(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        double orient = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(orient) < 1e-15) return false;

        double ax = a.X - d.X, ay = a.Y - d.Y;
        double bx = b.X - d.X, by = b.Y - d.Y;
        double cx = c.X - d.X, cy = c.Y - d.Y;

        double det = (ax * ax + ay * ay) * (bx * cy - by * cx)
                   - (bx * bx + by * by) * (ax * cy - ay * cx)
                   + (cx * cx + cy * cy) * (ax * by - ay * bx);

        return orient > 0 ? det > 1e-12 : det < -1e-12;
    }

    /// <summary>
    /// СТРОГО ВНУТРИ, А НЕ НА ГРАНИЦЕ. Точка, легшая ровно на сторону, при делении даёт
    /// треугольники нулевой площади: измерено — выродившиеся находились почти на каждой грани,
    /// хотя сама расстановка точек была верной. Отступ берётся от размера треугольника, чтобы
    /// мерка не зависела от единиц.
    /// </summary>
    private static bool InTriangle(Vector2d p, Vector2d a, Vector2d b, Vector2d c)
    {
        double d1 = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        double d2 = (c.X - b.X) * (p.Y - b.Y) - (c.Y - b.Y) * (p.X - b.X);
        double d3 = (a.X - c.X) * (p.Y - c.Y) - (a.Y - c.Y) * (p.X - c.X);

        double scale = Math.Max(Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)), 1e-12);
        double margin = scale * 1e-3;

        bool neg = d1 < -margin || d2 < -margin || d3 < -margin;
        bool pos = d1 > margin || d2 > margin || d3 > margin;
        return !(neg && pos) && !(Math.Abs(d1) <= margin || Math.Abs(d2) <= margin || Math.Abs(d3) <= margin);
    }

    public static List<int>? Triangulate(IReadOnlyList<Vector2d> outer, IReadOnlyList<IReadOnlyList<Vector2d>> holes,
                                         out List<Vector2d> pts)
    {
        // СОВПАВШИЕ ПОДРЯД ТОЧКИ УБИРАЮТСЯ. Контур, в котором точка повторяет предыдущую, даёт
        // сторону нулевой длины: восстановить её нельзя, и область обрывается на ней. Измерено:
        // у квадрата со стороной 10 с одной повторённой точкой сетка покрывала площадь 50 вместо
        // 100 — половина квадрата пропадала молча.
        //
        // Точки сравниваются не на точное равенство, а по допуску склейки: совпадение «почти»
        // ломает построение так же, как точное.
        static List<Vector2d> WithoutRepeats(IReadOnlyList<Vector2d> loop, double eps)
        {
            var res = new List<Vector2d>(loop.Count);

            foreach (var q in loop)
                if (res.Count == 0 || (q - res[^1]).Length > eps) res.Add(q);

            while (res.Count > 1 && (res[^1] - res[0]).Length <= eps) res.RemoveAt(res.Count - 1);
            return res;
        }

        double span = 0;
        foreach (var q in outer.Concat(holes.SelectMany(h => h)))
            span = Math.Max(span, Math.Max(Math.Abs(q.X), Math.Abs(q.Y)));

        double same = Math.Max(1e-12, span * 1e-12);

        var cleanOuter = WithoutRepeats(outer, same);
        var cleanHoles = holes.Select(h => WithoutRepeats(h, same)).Where(h => h.Count >= 3).ToList();

        // ДЕЛЕНИЕ СТОРОНЫ ПО ЧУЖИМ ВЕРШИНАМ — ПРОБОВАЛ, ОТКАТИЛ.
        //
        // У отверстия, прижатого к краю, углы лежат на стороне внешнего контура, но её вершинами
        // не являются; казалось, что добавление их в сторону сделает границу общей по правде.
        // На деле площадь ушла в другую сторону и дальше от верной: 86 → 78 при верных 84, а два
        // касающихся отверстия остались как были (91 при верных 82).
        //
        // Значит, одной общей вершины мало: заливка с переключением принципиально не различает
        // пустоту отверстия и наружное пространство там, где они соприкасаются. Нужна не правка
        // подготовки, а другая основа отбора — по принадлежности треугольника области, считаемой
        // по всем контурам сразу, а не по переходам через стороны.
        //
        // Проверено и не помогло, по порядку: отсев совпавших точек (помог другому случаю), учёт
        // стороны, общей двум контурам, сведение номеров точек по месту, деление сторон.

        pts = new List<Vector2d>(cleanOuter);
        var loops = new List<(int From, int Count)> { (0, cleanOuter.Count) };
        foreach (var h in cleanHoles) { loops.Add((pts.Count, h.Count)); pts.AddRange(h); }

        if (cleanOuter.Count < 3) { Debug?.Invoke("во внешнем контуре меньше трёх точек"); return null; }

        // Обёртка: треугольник заведомо больше всех точек.
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double dx = Math.Max(maxX - minX, 1e-9), dy = Math.Max(maxY - minY, 1e-9), d = Math.Max(dx, dy) * 20;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        int n0 = pts.Count;
        // КРОШЕЧНОЕ СМЕЩЕНИЕ ТОЧЕК ВНУТРИ АЛГОРИТМА. Ряды точек контура лежат на прямых
        // (стороны развёртки), из-за чего четырёхугольники вырождены и допустимой перекладки
        // не находится — сторона контура не восстанавливается. Смещение (миллиардная доля
        // размера, по номеру точки) снимает вырождение; наружу идут исходные точки.
        double jit = 1e-9 * Math.Max(dx, dy);
        var work = new List<Vector2d>(pts.Count + 3);
        for (int i = 0; i < pts.Count; i++)
        {
            double h1 = ((i * 2654435761L) % 1000) / 1000.0 - 0.5;
            double h2 = ((i * 40503L) % 1000) / 1000.0 - 0.5;
            work.Add(new Vector2d(pts[i].X + jit * h1, pts[i].Y + jit * h2));
        }
        work.Add(new(cx - d, cy - d)); work.Add(new(cx + d, cy - d)); work.Add(new(cx, cy + d));

        var T = new List<int> { n0, n0 + 1, n0 + 2 };

        // СПРАВОЧНИК СТОРОН — ОДИН НА ВСЁ ПОСТРОЕНИЕ. Прежде каждый вызов строил его заново и
        // выбрасывал: приведение к Делоне своё, восстановление контуров своё. Теперь он ведётся
        // от начала до конца, и его же использует поиск треугольника — идти по сетке от соседа к
        // соседу можно только тогда, когда соседи известны.
        var map = Sides(T);

        // Вставка точек.
        for (int i = 0; i < n0; i++)
        {
            int t = Locate(work, T, work[i], map);
            if (t < 0) { Debug?.Invoke($"точка {i} не попала ни в один треугольник"); return null; }
            int a = T[t], b = T[t + 1], c = T[t + 2];

            Forget(map, T, t);

            T[t] = a; T[t + 1] = b; T[t + 2] = i;
            T.Add(b); T.Add(c); T.Add(i);
            T.Add(c); T.Add(a); T.Add(i);

            Remember(map, T, t);
            Remember(map, T, T.Count - 6);
            Remember(map, T, T.Count - 3);

            Legalize(work, T, new List<(int, int)> { (a, b), (b, c), (c, a) }, null, map);
        }

        // Восстановление сторон контуров.
        var constrained = new HashSet<(int, int)>();
        foreach (var (from, count) in loops)
            for (int k = 0; k < count; k++)
            {
                int a = from + k, b = from + (k + 1) % count;
                // K07: справочник сторон — тот же, что вёлся при вставке точек. Прежде каждая
                // сторона контура строила его заново (O(n) на сторону, O(n²) на грань): у
                // плоской грани с восемью отверстиями — 24 мс из 25 на один вызов.
                if (!ForceEdge(work, T, a, b, map)) { Debug?.Invoke($"не удалось восстановить сторону контура {a}–{b}"); return null; }
                constrained.Add(Key(a, b));
        // ВЫРОЖДЕННЫЙ ТРЕУГОЛЬНИК ИЗ ТРЁХ ТОЧЕК КОНТУРА — ПРОБОВАЛ, УБРАЛ. Если точка контура
        // почти лежит на прямой между соседями, рядом остаётся треугольник нулевой площади, а
        // его длинная сторона — лишней границей; в поиске пересечений её не находят, потому что
        // она делит концы с ограниченными сторонами. Слияние такого треугольника с соседом при
        // постоянном пороге площади (1e-12) не срабатывало ни разу, а при относительном
        // (1e-4 от средней) срабатывало 759 раз и роняло охват с 90 до 68 тел: под него
        // попадают законные тонкие треугольники. Нужен признак не по площади, а по тому, что
        // все три вершины — соседние точки одного контура.

            }

        // ТРОЙКА ПОДРЯД ИДУЩИХ ТОЧЕК КОНТУРА — ПРОБОВАЛ, УБРАЛ. Замысел: сливать вырожденный
        // треугольник (a, m, b) из трёх подряд идущих точек одного контура с соседом через
        // длинную сторону. Признак подобран измерением: по площади (порог 1e-12 — 0 срабатываний;
        // 1e-4 от средней — 759 срабатываний и охват 68) и по соседству без учёта прямизны (469
        // срабатываний, охват 72) не годятся; сочетание «соседние точки И средняя отстоит меньше
        // миллионной доли стороны» даёт 13 срабатываний и охват 90. Но дефект при этом только
        // ПЕРЕНОСИТСЯ: лишняя сторона была 76→74 (через одну точку), стала 76→73 (через две),
        // отказов «вне контура» стало 15 вместо 12. Нужно делить сторону по пропущенным точкам,
        // а не сливать треугольники.

        // ТОЧКА КОНТУРА НА СТОРОНЕ. Измерено: срабатывает 2 раза на 100 телах, отказов «вне
        // контура» 12 → 11, незамкнутость 5 → 6, охват тел 90 без изменений. Оставлено, потому
        // что это исправление НЕВЕРНОЙ триангуляции (вершина лежит на стороне чужого
        // треугольника), а не подгонка под показатель.
        // Если точка контура лежит на стороне треугольника (край кривой,
        // и соседние точки почти на одной прямой), сторона спрямляет контур и остаётся лишней
        // границей. Такая сторона делится этой точкой вместе с ОБОИМИ смежными треугольниками —
        // тогда граница совпадает с контуром. Слияние треугольников вместо деления уже
        // пробовалось и лишь переносило дефект (сторона перекрывала две точки вместо одной).
        {
            var posOf = new Dictionary<int, (int Loop, int Idx, int Count)>();
            for (int li = 0; li < loops.Count; li++)
                for (int k = 0; k < loops[li].Count; k++)
                    posOf[loops[li].From + k] = (li, k, loops[li].Count);

            for (int pass = 0; pass < 6; pass++)
            {
                // Здесь справочник строится заново нарочно: к этому месту сетка уже правилась
                // другими путями, и вести общий через них пока не через что.
                var passMap = Sides(T);
                bool changed = false;
                for (int t = 0; t + 2 < T.Count && !changed; t += 3)
                    for (int k = 0; k < 3 && !changed; k++)
                    {
                        int a = T[t + k], b = T[t + (k + 1) % 3], c = T[t + (k + 2) % 3];
                        if (!posOf.TryGetValue(a, out var pa) || !posOf.TryGetValue(b, out var pb)) continue;
                        if (pa.Loop != pb.Loop) continue;
                        int n = pa.Count;
                        int step = ((pb.Idx - pa.Idx) % n + n) % n;
                        if (step != 2) continue;                      // пропущена ровно одна точка
                        int m = loops[pa.Loop].From + (pa.Idx + 1) % n;
                        if (m == c) continue;

                        double baseLen = (work[b] - work[a]).Length;
                        if (baseLen < 1e-15) continue;
                        double height = Math.Abs(Cross(work[a], work[b], work[m])) / baseLen;
                        if (height > 1e-6 * baseLen) continue;        // точка не на стороне

                        if (!passMap.TryGetValue((b, a), out int t2)) continue;
                        int opp = Third(T, t2, b, a);
                        if (opp < 0 || opp == m) continue;

                        T[t] = a; T[t + 1] = m; T[t + 2] = c;
                        T[t2] = b; T[t2 + 1] = m; T[t2 + 2] = opp;
                        T.Add(m); T.Add(b); T.Add(c);
                        T.Add(m); T.Add(a); T.Add(opp);
                        changed = true; Merged++;
                    }
                if (!changed) break;
            }
        }

        // Отсечение: наружу — от обёртки, через несвязанные стороны.
        var keep = Classify(work, T, constrained, n0, pts, loops);
        var res = new List<int>();
        for (int t = 0, k = 0; t + 2 < T.Count; t += 3, k++)
            if (keep[k]) { res.Add(T[t]); res.Add(T[t + 1]); res.Add(T[t + 2]); }
        if (res.Count == 0) Debug?.Invoke("после отсечения не осталось треугольников");
        return res.Count == 0 ? null : res;
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>
    /// Перекладка стороны u–v на c–d допустима: четырёхугольник u, c, v, d строго выпуклый.
    /// Обязательны ОБА условия — c и d по разные стороны от u–v, и u, v по разные стороны от
    /// c–d. Прежде проверялось только первое: перекладки шли и там, где нельзя, сетка портилась,
    /// и восстановить стороны контура не удавалось.
    /// </summary>
    private static bool CanFlip(List<Vector2d> p, int u, int v, int c, int dd)
    {
        double s1 = Cross(p[u], p[v], p[c]), s2 = Cross(p[u], p[v], p[dd]);
        double s3 = Cross(p[c], p[dd], p[u]), s4 = Cross(p[c], p[dd], p[v]);
        return s1 * s2 < 0 && s3 * s4 < 0;
    }

    private static double Cross(Vector2d o, Vector2d a, Vector2d b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>
    /// ПОИСК ТРЕУГОЛЬНИКА, В КОТОРЫЙ ПОПАДАЕТ ТОЧКА.
    ///
    /// Перебор начинается с ТОГО ЖЕ МЕСТА, где нашлась предыдущая точка. Точки контура идут
    /// подряд вдоль границы, и соседняя почти всегда попадает в тот же треугольник или в
    /// ближайший — значит, перебор обрывается на первых шагах. Прежде он всякий раз начинался
    /// сначала и просматривал все треугольники: при вставке N точек это полный обход на каждую.
    ///
    /// Ответ от этого не меняется: порядок обхода круговой, и первым найденным остаётся тот же
    /// треугольник, если точка лежит строго внутри одного. Слепок сетки на всех трёх сценариях
    /// совпал.
    ///
    /// ВЫИГРЫШ НЕВЕЛИК: на восьми отверстиях медиана 1800 → 1610…1707 мс при разбросе замеров
    /// 1193…2287, то есть на грани различимости. Правка оставлена, потому что она верна по
    /// существу и ничего не стоит, но выдавать её за ускорение нельзя.
    ///
    /// СДЕЛАНО: справочник сторон ведётся на всё построение, и поиск идёт ХОДЬБОЙ ПО СЕТКЕ — из
    /// треугольника через ту сторону, за которой лежит точка. Перебор остался запасным путём на
    /// случай, когда ходьба упёрлась в край или замкнула круг.
    ///
    ///   отверстий   перебором, мс   ходьбой, мс   выделено перебором, МБ   ходьбой, МБ
    ///        1           64.4           62.4             13.2                11.8
    ///        4          531.8          461.1            188.8               152.0
    ///        8         1941.3         1492.0           1014.9               771.8
    /// </summary>
    [ThreadStatic] private static int _lastFound;

    private static int Locate(List<Vector2d> p, List<int> T, Vector2d q,
                              Dictionary<(int, int), int>? map = null)
    {
        int count = T.Count / 3;
        if (count == 0) return -1;

        int start = _lastFound >= 0 && _lastFound < count ? _lastFound : 0;

        // ШАГ ПО СЕТКЕ, ПОКА ИЗВЕСТНЫ СОСЕДИ. Из треугольника переходим через ту сторону, за
        // которой лежит точка, и так приходим к нужному за столько шагов, сколько треугольников
        // между ними, — вместо просмотра всех. Шагов не больше, чем треугольников: если ходьба
        // упёрлась (сторона на краю или замкнулся круг), ниже идёт обычный перебор.
        if (map != null)
        {
            int at = start * 3;

            for (int step = 0; step < count; step++)
            {
                Vector2d a = p[T[at]], b = p[T[at + 1]], c = p[T[at + 2]];
                double s2 = Math.Sign(Cross(a, b, c));
                if (s2 == 0) break;

                double d1 = Cross(a, b, q) * s2, d2 = Cross(b, c, q) * s2, d3 = Cross(c, a, q) * s2;

                if (d1 >= 0 && d2 >= 0 && d3 >= 0) { _lastFound = at / 3; return at; }

                // Переходим через ту сторону, по другую сторону которой оказалась точка.
                int u, v;
                if (d1 < 0) { u = T[at]; v = T[at + 1]; }
                else if (d2 < 0) { u = T[at + 1]; v = T[at + 2]; }
                else { u = T[at + 2]; v = T[at]; }

                if (!map.TryGetValue((v, u), out int next)) break;    // край сетки
                at = next;
            }
        }

        int best = -1; double bestD = double.MaxValue;

        for (int step = 0; step < count; step++)
        {
            int t = ((start + step) % count) * 3;
            Vector2d a = p[T[t]], b = p[T[t + 1]], c = p[T[t + 2]];
            double s = Math.Sign(Cross(a, b, c));
            if (s == 0) continue;
            double d1 = Cross(a, b, q) * s, d2 = Cross(b, c, q) * s, d3 = Cross(c, a, q) * s;
            if (d1 >= 0 && d2 >= 0 && d3 >= 0) { _lastFound = t / 3; return t; }
            double outside = -Math.Min(d1, Math.Min(d2, d3));
            if (outside < bestD) { bestD = outside; best = t; }
        }

        if (best >= 0) _lastFound = best / 3;
        return best;
    }

    /// <summary>Пара треугольников по стороне: (a, b) → треугольник, где сторона идёт от a к b.</summary>
    private static Dictionary<(int, int), int> Sides(List<int> T)
    {
        var map = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < T.Count; t += 3)
            for (int k = 0; k < 3; k++) map[(T[t + k], T[t + (k + 1) % 3])] = t;
        return map;
    }

    private static int Third(List<int> T, int t, int a, int b)
    {
        for (int k = 0; k < 3; k++)
            if (T[t + k] == a && T[t + (k + 1) % 3] == b) return T[t + (k + 2) % 3];
        return -1;
    }

    /// <summary>
    /// ПРИВЕДЕНИЕ К УСЛОВИЮ ДЕЛОНЕ ПЕРЕСТАНОВКОЙ СТОРОН.
    ///
    /// Справочник сторон строится ОДИН РАЗ перед очередью и правится по месту. Прежде он
    /// пересоздавался на каждый элемент очереди: полный обход всех треугольников и новый словарь
    /// ради двух изменяемых. Внешний разбор намерил на теле с восемью отверстиями 1903.7 мс
    /// против 432.8 мс и падение временных выделений с 3356 МБ до 417 МБ за построение.
    ///
    /// Предикаты, порядок очереди и условия перестановки не тронуты: правится только ведение
    /// справочника, поэтому сетка выходит та же.
    ///
    /// ЗАМЕРЕНО У СЕБЯ (одно ядро, медиана пяти прогонов, блок 60×40×20):
    ///
    ///   отверстий   прежде, мс   теперь, мс   выделено прежде, МБ   теперь, МБ
    ///        1          80.7         64.4            22.9             13.2
    ///        4        1697.5        531.8          1276.3            188.8
    ///        8        5785.1       1941.3          9652.0           1014.9
    ///
    /// То есть времени втрое меньше, а временных выделений — почти вдесятеро. Слепок координат и
    /// сторон совпал во всех трёх сценариях; сверка справочника (P3D_CDTCHECK=1) расхождений не
    /// нашла. Порог приёмки из внешнего задания — 2× по времени и 70% по выделениям — взят с
    /// запасом.
    /// </summary>
    private static void Legalize(List<Vector2d> p, List<int> T, List<(int, int)> seeds,
                                 HashSet<(int, int)>? fixedEdges, Dictionary<(int, int), int>? shared = null)
    {
        var map = shared ?? Sides(T);

        var q = new Queue<(int, int)>(seeds);
        int guard = 20000;
        while (q.Count > 0 && guard-- > 0)
        {
            var (a, b) = q.Dequeue();
            if (fixedEdges != null && fixedEdges.Contains(Key(a, b))) continue;

            // Прежнее поведение оставлено под переключателем: иначе выигрыш не измерить.
            if (OldLegalize) map = Sides(T);

            if (!map.TryGetValue((a, b), out int t1) || !map.TryGetValue((b, a), out int t2)) continue;
            int c = Third(T, t1, a, b), dd = Third(T, t2, b, a);
            if (c < 0 || dd < 0) continue;
            if (!InCircle(p[a], p[c], p[b], p[dd])) continue;
            if (!CanFlip(p, a, b, c, dd)) continue;

            Forget(map, T, t1);
            Forget(map, T, t2);

            T[t1] = c; T[t1 + 1] = a; T[t1 + 2] = dd;
            T[t2] = dd; T[t2 + 1] = b; T[t2 + 2] = c;

            Remember(map, T, t1);
            Remember(map, T, t2);

            if (CheckSides) VerifySides(map, T);

            q.Enqueue((a, dd)); q.Enqueue((dd, b)); q.Enqueue((b, c)); q.Enqueue((c, a));
        }
    }

    /// <summary>Убрать из справочника стороны треугольника — только те, что числятся за ним.</summary>
    private static void Forget(Dictionary<(int, int), int> map, List<int> T, int t)
    {
        for (int k = 0; k < 3; k++)
        {
            var side = (T[t + k], T[t + (k + 1) % 3]);
            if (map.TryGetValue(side, out int owner) && owner == t) map.Remove(side);
        }
    }

    /// <summary>Внести в справочник стороны треугольника.</summary>
    private static void Remember(Dictionary<(int, int), int> map, List<int> T, int t)
    {
        for (int k = 0; k < 3; k++) map[(T[t + k], T[t + (k + 1) % 3])] = t;
    }

    /// <summary>
    /// Сверять ли правленный по месту справочник с построенным заново. Расхождение означало бы,
    /// что перестановка испортила связность, и поймать это надо сразу, а не по кривой сетке
    /// через несколько шагов. Включается переключателем P3D_CDTCHECK=1.
    /// </summary>
    private static bool _gCheckSides = Environment.GetEnvironmentVariable("P3D_CDTCHECK") == "1";
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool CheckSides
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.CdtCheckSides : _gCheckSides;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gCheckSides, value, "Cdt.CheckSides");
    }

    /// <summary>Пересоздавать справочник на каждом шаге — прежнее поведение, для сравнения.</summary>
    private static bool _gOldLegalize = Environment.GetEnvironmentVariable("P3D_CDTOLD") == "1";
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool OldLegalize
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.CdtOldLegalize : _gOldLegalize;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gOldLegalize, value, "Cdt.OldLegalize");
    }

    private static void VerifySides(Dictionary<(int, int), int> map, List<int> T)
    {
        var fresh = Sides(T);
        if (fresh.Count == map.Count && fresh.All(kv => map.TryGetValue(kv.Key, out int v) && v == kv.Value))
            return;

        throw new InvalidOperationException(
            $"CDT: справочник сторон разошёлся с сеткой ({map.Count} против {fresh.Count}).");
    }

    /// <summary>Точка d внутри описанной окружности треугольника (a, b, c) (a, b, c против часовой).</summary>
    private static bool InCircle(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        if (Cross(a, b, c) < 0) (b, c) = (c, b);
        double ax = a.X - d.X, ay = a.Y - d.Y;
        double bx = b.X - d.X, by = b.Y - d.Y;
        double cx = c.X - d.X, cy = c.Y - d.Y;
        double det = (ax * ax + ay * ay) * (bx * cy - cx * by)
                   - (bx * bx + by * by) * (ax * cy - cx * ay)
                   + (cx * cx + cy * cy) * (ax * by - bx * ay);
        return det > 1e-18;
    }

    /// <summary>Сторона (a, b) появляется в сетке перекладкой мешающих сторон.</summary>
    /// <summary>
    /// ВОССТАНОВЛЕНИЕ СТОРОНЫ КОНТУРА: сторона a–b должна стать стороной сетки.
    ///
    /// Справочник сторон строится ОДИН РАЗ и правится по месту — так же, как это сделано в
    /// приведении к условию Делоне. Прежде он пересоздавался на каждом заходе цикла: полный
    /// обход всех треугольников ради двух изменяемых, и так до пятисот раз подряд.
    /// </summary>
    private static bool ForceEdge(List<Vector2d> p, List<int> T, int a, int b,
                                  Dictionary<(int, int), int>? shared = null)
    {
        var map = shared ?? Sides(T);

        for (int guard = 0; guard < 500; guard++)
        {
            if (map.ContainsKey((a, b)) || map.ContainsKey((b, a))) return true;

            // Сторона, пересекающая отрезок a–b.
            bool flipped = false;
            for (int t = 0; t + 2 < T.Count && !flipped; t += 3)
                for (int k = 0; k < 3 && !flipped; k++)
                {
                    int u = T[t + k], v = T[t + (k + 1) % 3];
                    if (u == a || u == b || v == a || v == b) continue;
                    if (!Crosses(p[a], p[b], p[u], p[v])) continue;
                    if (!map.TryGetValue((v, u), out int t2)) continue;
                    int c = Third(T, t, u, v), dd = Third(T, t2, v, u);
                    if (c < 0 || dd < 0) continue;
                    if (!CanFlip(p, u, v, c, dd)) continue;

                    Forget(map, T, t);
                    Forget(map, T, t2);

                    T[t] = c; T[t + 1] = u; T[t + 2] = dd;
                    T[t2] = dd; T[t2 + 1] = v; T[t2 + 2] = c;

                    Remember(map, T, t);
                    Remember(map, T, t2);

                    if (CheckSides) VerifySides(map, T);

                    flipped = true;
                }
            if (!flipped) return false;
        }
        return false;
    }

    /// <summary>ДИАГНОСТИКА: слияний вырожденных треугольников.</summary>
    public static long Merged;

    private static bool Crosses(Vector2d a, Vector2d b, Vector2d u, Vector2d v)
    {
        double d1 = Cross(a, b, u), d2 = Cross(a, b, v), d3 = Cross(u, v, a), d4 = Cross(u, v, b);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    /// <summary>Какие треугольники оставить: внутри внешнего контура и вне отверстий.</summary>
    private static bool[] Classify(List<Vector2d> work, List<int> T, HashSet<(int, int)> constrained, int n0,
                                   List<Vector2d> pts, List<(int From, int Count)> loops)
    {
        int count = T.Count / 3;
        var keep = new bool[count];

        // ЗАЛИВКА ОТ ОБЁРТКИ, А НЕ ПРОВЕРКА ЦЕНТРА. Прежде каждый треугольник проверялся
        // отдельно: лежит ли его центр внутри контура. У почти совпавших по прямой точек края
        // (в развёртке шаги 0.338 и 0.442 при хорде 0.78) рождается треугольник нулевой площади,
        // и такая проверка на нём ненадёжна: он отбрасывался, а длинная сторона оставалась
        // границей — тело потом не замыкалось. Заливка идёт от треугольников, держащихся за
        // обёртку, и не проходит через стороны контуров, поэтому вырожденные треугольники ей не
        // мешают.
        // СКОЛЬКИМ КОНТУРАМ ПРИНАДЛЕЖИТ СТОРОНА. Считать надо по МЕСТАМ точек, а не по их
        // номерам: у двух касающихся отверстий общая сторона записана дважды, разными номерами
        // с одинаковыми координатами, и по номерам она выглядит как две разные стороны.
        var sameAs = new Dictionary<(long, long), int>();
        var canon = new int[pts.Count];

        for (int i = 0; i < pts.Count; i++)
        {
            var key = ((long)Math.Round(pts[i].X * 1e9), (long)Math.Round(pts[i].Y * 1e9));
            if (!sameAs.TryGetValue(key, out int first)) sameAs[key] = first = i;
            canon[i] = first;
        }

        var owners = new Dictionary<(int, int), int>();

        foreach (var (from, cnt) in loops)
            for (int i = 0; i < cnt; i++)
            {
                int a = canon[from + i], b = canon[from + (i + 1) % cnt];
                var key = Key(a, b);
                owners[key] = owners.GetValueOrDefault(key) + 1;
            }

        int SharedBy(int a, int b) =>
            a < canon.Length && b < canon.Length ? owners.GetValueOrDefault(Key(canon[a], canon[b])) : 0;

        var outside = new bool[count];
        var side = new Dictionary<(int, int), int>();
        for (int t = 0, k = 0; t + 2 < T.Count; t += 3, k++)
            for (int j = 0; j < 3; j++) side[(T[t + j], T[t + (j + 1) % 3])] = k;

        // Заливка С ПЕРЕКЛЮЧЕНИЕМ: переход через сторону контура меняет «снаружи» на «внутри»
        // и обратно. Без переключения внутренности отверстий оставались незалитыми и
        // заполнялись треугольниками (охват падал с 90 до 53 тел).
        var known = new bool[count];
        var queue = new Queue<int>();
        for (int t = 0, k = 0; t + 2 < T.Count; t += 3, k++)
            if (T[t] >= n0 || T[t + 1] >= n0 || T[t + 2] >= n0) { outside[k] = true; known[k] = true; queue.Enqueue(k); }

        while (queue.Count > 0)
        {
            int k = queue.Dequeue();
            int t = k * 3;
            for (int j = 0; j < 3; j++)
            {
                int a = T[t + j], b = T[t + (j + 1) % 3];
                if (!side.TryGetValue((b, a), out int nb) || known[nb]) continue;
                // СТОРОНА, ОБЩАЯ ДВУМ КОНТУРАМ, НЕ ПЕРЕКЛЮЧАЕТ. Касающиеся отверстия делят одну
                // сторону; переход через неё ведёт из пустоты в пустоту, а переключение объявляло
                // второе отверстие материалом. Измерено: у квадрата 10×10 с двумя касающимися
                // отверстиями 3×3 сетка покрывала 91 вместо 82, то есть одно отверстие
                // оставалось незаполненным. То же и у отверстия, прижатого к краю: 86 вместо 84.
                bool cross = (constrained.Contains((a, b)) || constrained.Contains((b, a)))
                             && SharedBy(a, b) < 2;
                outside[nb] = cross ? !outside[k] : outside[k];
                known[nb] = true; queue.Enqueue(nb);
            }
        }

        // ОТБОР ПО ПРИНАДЛЕЖНОСТИ ОБЛАСТИ — ТАМ, ГДЕ ЭТО НАДЁЖНО.
        //
        // Заливка с переключением не различает пустоту отверстия и наружное пространство там, где
        // они соприкасаются: переход через общую сторону ведёт из пустоты в пустоту, а
        // переключение объявляет вторую пустоту материалом. Измерено: отверстие у края даёт
        // площадь 86 вместо 84, два касающихся отверстия — 91 вместо 82.
        //
        // Проверка середины треугольника по всем контурам сразу таких ошибок не делает, но она
        // ненадёжна на вырожденных треугольниках — ради этого заливка когда-то и вводилась.
        // Поэтому решают оба: заливка везде, а проверка середины — только там, где треугольник
        // заметной площади и ошибиться ей негде.
        double mean = 0;
        int counted = 0;

        for (int k = 0; k < count; k++)
        {
            int t = k * 3;
            if (T[t] >= n0 || T[t + 1] >= n0 || T[t + 2] >= n0) continue;

            mean += Math.Abs(Cross(work[T[t]], work[T[t + 1]], work[T[t + 2]])) / 2;
            counted++;
        }

        if (counted > 0) mean /= counted;

        (double Y0, double Y1, double X1)[]? lb = CdtBoxOff || BoxOffForTests ? null : new (double, double, double)[loops.Count];
        if (lb != null)
            for (int i = 0; i < loops.Count; i++)
            {
                double y0 = double.MaxValue, y1 = double.MinValue, x1 = double.MinValue;
                for (int j = 0; j < loops[i].Count; j++) { var q = pts[loops[i].From + j]; y0 = Math.Min(y0, q.Y); y1 = Math.Max(y1, q.Y); x1 = Math.Max(x1, q.X); }
                lb[i] = (y0, y1, x1);
            }

        for (int k = 0; k < count; k++)
        {
            int t = k * 3;
            if (T[t] >= n0 || T[t + 1] >= n0 || T[t + 2] >= n0) continue;

            keep[k] = known[k] && !outside[k];

            double area = Math.Abs(Cross(work[T[t]], work[T[t + 1]], work[T[t + 2]])) / 2;
            if (area < mean * 1e-3) continue;             // вырожденный — верим заливке

            Vector2d mid = (pts[T[t]] + pts[T[t + 1]] + pts[T[t + 2]]) / 3;

            bool inOuter = Inside(pts, loops[0], mid);
            bool inHole = false;
            for (int i = 1; i < loops.Count && !inHole; i++)
            {
                // g.13: точка вне габарита отверстия по Y (ни одна сторона не пересекает луч) или правее него (с запасом на
                // округление точки пересечения) — подсчёт пересечений дал бы «снаружи»: тот же ответ без обхода контура.
                // Грань со 100 отверстиями — треугольники × точки всех отверстий. Мутация P3D_CDTBOX=0.
                if (lb != null && (mid.Y < lb[i].Y0 || mid.Y > lb[i].Y1 || mid.X > lb[i].X1 + 1e-9 * (1 + Math.Abs(lb[i].X1)))) continue;
                inHole = Inside(pts, loops[i], mid);
            }

            keep[k] = inOuter && !inHole;
        }

        return keep;
    }

    static readonly bool CdtBoxOff = Environment.GetEnvironmentVariable("P3D_CDTBOX") == "0";
    /// <summary>Сравнение (KRN10-CDT-BOX-SAME): без отбора по габаритам в этом потоке; обходы контуров — счётчик.</summary>
    public static volatile bool BoxOffForTests;
    public static long LoopWalks;
    private static bool Inside(List<Vector2d> pts, (int From, int Count) loop, Vector2d q)
    {
        Interlocked.Increment(ref LoopWalks);
        bool inside = false;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            Vector2d pi = pts[loop.From + i], pj = pts[loop.From + j];
            if ((pi.Y > q.Y) != (pj.Y > q.Y) && q.X < (pj.X - pi.X) * (q.Y - pi.Y) / (pj.Y - pi.Y) + pi.X) inside = !inside;
        }
        return inside;
    }
}
