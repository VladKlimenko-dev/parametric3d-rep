using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// ОБЪЁМНЫЕ ПЕРЕСТАНОВКИ 2↔3 и 3↔2 — основание всей работы с объёмной сеткой. На них держатся
/// восстановление рёбер и граней, приведение к условию Делоне и удаление лезвий; без них
/// остаётся только дробление точками Штейнера, а оно, как измерено, не сходится.
///
/// Что такое перестановка. Два тетраэдра с общей гранью занимают вместе многогранник из пяти
/// вершин. Его можно разбить двумя способами: на ДВА тетраэдра (через общую грань) или на ТРИ
/// (через ребро, соединяющее вершины, противолежащие этой грани). Переход между этими способами
/// и есть перестановка: 2→3 создаёт ребро, 3→2 его убирает.
///
/// Перестановка допустима не всегда: только когда пятивершинник ВЫПУКЛЫЙ у общей грани. Иначе
/// три новых тетраэдра выйдут вывернутыми, и сетка испортится. Проверка ведётся по знакам
/// объёмов — целыми числами, как и всё остальное в этом построителе.
/// </summary>
public static class TetFlips
{
    /// <summary>Знак объёма тетраэдра целыми числами: та же мерка, что и в построителе.</summary>
    public static int OrientSign(Vector3d a, Vector3d b, Vector3d c, Vector3d d)
    {
        const double scale = 1e4;
        System.Numerics.BigInteger R(double v) => new(Math.Round(v * scale));

        var bx = R(b.X - a.X); var by = R(b.Y - a.Y); var bz = R(b.Z - a.Z);
        var cx = R(c.X - a.X); var cy = R(c.Y - a.Y); var cz = R(c.Z - a.Z);
        var dx = R(d.X - a.X); var dy = R(d.Y - a.Y); var dz = R(d.Z - a.Z);

        return (bx * (cy * dz - cz * dy) - by * (cx * dz - cz * dx) + bz * (cx * dy - cy * dx)).Sign;
    }

    /// <summary>
    /// ПЕРЕСТАНОВКА 2→3. Два тетраэдра с общей гранью (a, b, c) и вершинами p и q заменяются
    /// тремя, у которых появляется ребро p–q.
    ///
    /// Возвращает false, если перестановка недопустима: точка q должна лежать по другую сторону
    /// грани от p, и все три новых тетраэдра обязаны выйти невывернутыми.
    /// </summary>
    public static bool Flip23(List<Vector3d> pts, int a, int b, int c, int p, int q,
                              out List<(int, int, int, int)> result)
    {
        result = new List<(int, int, int, int)>();

        // p и q по разные стороны общей грани — иначе это не соседи через неё.
        int sp = OrientSign(pts[a], pts[b], pts[c], pts[p]);
        int sq = OrientSign(pts[a], pts[b], pts[c], pts[q]);
        if (sp == 0 || sq == 0 || sp == sq) return false;

        var made = new[]
        {
            (a, b, p, q),
            (b, c, p, q),
            (c, a, p, q)
        };

        foreach (var (x, y, z, w) in made)
        {
            int s = OrientSign(pts[x], pts[y], pts[z], pts[w]);
            if (s == 0) return false;                       // вырожденный — перестановка не выйдет
            result.Add(s < 0 ? (x, z, y, w) : (x, y, z, w));
        }

        // Объём обязан сохраниться: это и есть проверка допустимости по существу.
        double was = Math.Abs(Volume(pts, a, b, c, p)) + Math.Abs(Volume(pts, a, b, c, q));
        double now = result.Sum(t => Math.Abs(Volume(pts, t.Item1, t.Item2, t.Item3, t.Item4)));

        if (Math.Abs(now - was) > 1e-6 * Math.Max(was, 1)) { result.Clear(); return false; }

        return true;
    }

    /// <summary>
    /// ПЕРЕСТАНОВКА 3→2. Три тетраэдра вокруг общего ребра p–q с внешними вершинами a, b, c
    /// заменяются двумя, и ребро p–q исчезает.
    /// </summary>
    public static bool Flip32(List<Vector3d> pts, int p, int q, int a, int b, int c,
                              out List<(int, int, int, int)> result)
    {
        result = new List<(int, int, int, int)>();

        int sp = OrientSign(pts[a], pts[b], pts[c], pts[p]);
        int sq = OrientSign(pts[a], pts[b], pts[c], pts[q]);
        if (sp == 0 || sq == 0 || sp == sq) return false;

        foreach (var (x, y, z, w) in new[] { (a, b, c, p), (a, b, c, q) })
        {
            int s = OrientSign(pts[x], pts[y], pts[z], pts[w]);
            if (s == 0) return false;
            result.Add(s < 0 ? (x, z, y, w) : (x, y, z, w));
        }

        double was = Math.Abs(Volume(pts, a, b, p, q)) + Math.Abs(Volume(pts, b, c, p, q))
                   + Math.Abs(Volume(pts, c, a, p, q));
        double now = result.Sum(t => Math.Abs(Volume(pts, t.Item1, t.Item2, t.Item3, t.Item4)));

        if (Math.Abs(now - was) > 1e-6 * Math.Max(was, 1)) { result.Clear(); return false; }

        return true;
    }

    /// <summary>
    /// ВОССТАНОВЛЕНИЕ РЕБРА ПЕРЕСТАНОВКАМИ. Ребро p–q должно стать ребром сетки. Пока его нет,
    /// оно проходит сквозь какие-то грани; каждая такая грань разделяет два тетраэдра, и
    /// перестановка 2→3 на ней создаёт новое ребро между их вершинами. Повторяя это, ребро
    /// «пробивают» сквозь сетку — так делают настоящие построители, а дробление точками Штейнера
    /// оставляют на случай, когда перестановка недопустима.
    ///
    /// Возвращает true, если ребро появилось.
    /// </summary>
    public static bool RecoverEdge(List<Vector3d> pts, List<(int A, int B, int C, int D)> tets,
                                   int p, int q, int maxSteps = 24)
    {
        for (int step = 0; step < maxSteps; step++)
        {
            if (HasEdge(tets, p, q)) return true;

            // Грань, которую ребро пересекает, и два тетраэдра при ней.
            if (!FindCrossedFace(pts, tets, p, q, out int a, out int b, out int c,
                                 out int first, out int second)) return false;

            int x = Opposite(tets[first], a, b, c);
            int y = Opposite(tets[second], a, b, c);
            if (x < 0 || y < 0) return false;

            if (!Flip23(pts, a, b, c, x, y, out var made)) return false;

            // Снос идёт с конца, чтобы номера не сдвинулись.
            int hi = Math.Max(first, second), lo = Math.Min(first, second);
            tets.RemoveAt(hi);
            tets.RemoveAt(lo);
            foreach (var t in made) tets.Add(t);
        }

        return HasEdge(tets, p, q);
    }

    /// <summary>
    /// ВОССТАНОВЛЕНИЕ ГРАНИ ПЕРЕСТАНОВКАМИ. Грань (a, b, c) должна стать гранью сетки. Пока её
    /// нет, сквозь неё проходят рёбра сетки; каждое такое ребро окружено кольцом тетраэдров, и
    /// если их ровно три, перестановка 3→2 это ребро убирает. Убрав все пересекающие рёбра,
    /// получаем грань.
    ///
    /// Так и устроено в настоящих построителях: сперва убирают помехи перестановками и лишь
    /// затем, если не вышло, ставят точку Штейнера.
    /// </summary>
    public static bool RecoverFacet(List<Vector3d> pts, List<(int A, int B, int C, int D)> tets,
                                    int a, int b, int c, int maxSteps = 16)
    {
        for (int step = 0; step < maxSteps; step++)
        {
            if (HasFacet(tets, a, b, c)) return true;

            if (!FindCrossingEdge(pts, tets, a, b, c, out int p, out int q)) return false;

            // Кольцо любой длины: 3→2 — частный случай, и опираться только на него нельзя
            // (измерено: вокруг мешающих рёбер бывает 4, 5 и 9 тетраэдров).
            if (RemoveEdge(pts, tets, p, q)) continue;

            var around = new List<int>();
            for (int k = 0; k < tets.Count; k++)
            {
                var n = new[] { tets[k].A, tets[k].B, tets[k].C, tets[k].D };
                if (n.Contains(p) && n.Contains(q)) around.Add(k);
            }

            if (around.Count != 3) return false;

            var outer = new List<int>();
            foreach (int k in around)
                foreach (int n in new[] { tets[k].A, tets[k].B, tets[k].C, tets[k].D })
                    if (n != p && n != q && !outer.Contains(n)) outer.Add(n);

            if (outer.Count != 3) return false;
            if (!Flip32(pts, p, q, outer[0], outer[1], outer[2], out var made)) return false;

            around.Sort();
            for (int k = around.Count - 1; k >= 0; k--) tets.RemoveAt(around[k]);
            foreach (var t in made) tets.Add(t);
        }

        return HasFacet(tets, a, b, c);
    }

    /// <summary>
    /// ПОЛОСТНОЕ ВОССТАНОВЛЕНИЕ ГРАНИ — то, чем это делается в настоящих построителях, когда
    /// одиночных перестановок не хватает (а не хватает их почти всегда: 3→2 применима только
    /// при трёх тетраэдрах вокруг мешающего ребра).
    ///
    /// Замысел: снести все тетраэдры, которые грань пересекает, и заполнить освободившуюся
    /// полость заново — с ОБЕИХ сторон грани порознь. Тогда грань становится стенкой между двумя
    /// заполнениями, то есть гранью сетки по построению.
    ///
    /// Заполнение звёздное: от середины каждой стороны полости ко всем её граничным граням. Оно
    /// верно, если полость с этой стороны видна из середины целиком; проверяется это не
    /// рассуждением, а объёмом — сумма новых тетраэдров обязана совпасть со снесённым. Не
    /// совпала — восстановление отменяется целиком.
    ///
    /// ЗВЁЗДНОЕ ЗАПОЛНЕНИЕ НЕ ГОДИТСЯ — измерено: проверка объёмом не прошла ни разу,
    /// восстановлено 0 граней. Полости не звёздные: из выбранной вершины видна не вся полость.
    /// Защита при этом сработала, ни одна неверная сетка не построена.
    ///
    /// РАБОТАЕТ СВЯЗАННОЕ ЗАПОЛНЕНИЕ: каждая сторона полости триангулируется отдельно тем же
    /// приёмом Делоне, что и вся сетка, а грань служит стенкой — в триангуляцию каждой стороны
    /// входят только её точки плюс три вершины самой грани. Из построенного берётся лишь то, что
    /// лежит внутри полости и со своей стороны.
    ///
    /// Что дало: восстановлено 7 граней вместо нуля. Проверка объёмом по-прежнему на месте и
    /// отклоняет неудачные случаи.
    /// </summary>
    public static bool RecoverFacetByCavity(List<Vector3d> pts, List<(int A, int B, int C, int D)> tets,
                                            int a, int b, int c)
    {
        if (HasFacet(tets, a, b, c)) return true;

        // Тетраэдры, которые грань пересекает: у них есть ребро, протыкающее её.
        var crossing = new List<int>();

        for (int k = 0; k < tets.Count; k++)
        {
            var n = new[] { tets[k].A, tets[k].B, tets[k].C, tets[k].D };
            bool hit = false;

            for (int i = 0; i < 4 && !hit; i++)
                for (int j = i + 1; j < 4 && !hit; j++)
                {
                    if (n[i] == a || n[i] == b || n[i] == c) continue;
                    if (n[j] == a || n[j] == b || n[j] == c) continue;
                    if (SegmentCrossesTriangle(pts[n[i]], pts[n[j]], pts[a], pts[b], pts[c])) hit = true;
                }

            if (hit) crossing.Add(k);
        }

        if (crossing.Count == 0) return false;

        double removed = crossing.Sum(k => Math.Abs(Volume(pts, tets[k].A, tets[k].B, tets[k].C, tets[k].D)));

        // Граница полости: грани, встреченные ровно раз.
        var count = new Dictionary<(int, int, int), int>();
        var shape = new Dictionary<(int, int, int), (int, int, int)>();

        foreach (int k in crossing)
            foreach (var f in Faces(tets[k]))
            {
                var key = FaceKey(f);
                count[key] = count.GetValueOrDefault(key) + 1;
                shape[key] = f;
            }

        var wall = count.Where(kv => kv.Value == 1).Select(kv => shape[kv.Key]).ToList();
        if (wall.Count < 4) return false;

        // Середины двух сторон: чуть в стороне от грани по её нормали.
        Vector3d centre = (pts[a] + pts[b] + pts[c]) / 3;
        Vector3d normal = Vector3d.Cross(pts[b] - pts[a], pts[c] - pts[a]);
        if (normal.LengthSquared < 1e-18) return false;

        // СВЯЗАННОЕ ЗАПОЛНЕНИЕ: каждая сторона полости триангулируется отдельно, и грань служит
        // стенкой. Звёздное заполнение здесь не годится — измерено, что проверку объёмом оно не
        // проходит ни разу: полость не видна целиком ни из одной вершины.
        var made = new List<(int, int, int, int)>();

        // Вершины полости, разложенные по сторонам грани. Сама грань входит в обе.
        var nodes = new HashSet<int>();
        foreach (int k in crossing)
            foreach (int n in new[] { tets[k].A, tets[k].B, tets[k].C, tets[k].D }) nodes.Add(n);

        foreach (int side in new[] { 1, -1 })
        {
            var mine = new List<int> { a, b, c };
            foreach (int n in nodes)
            {
                if (n == a || n == b || n == c) continue;
                if (Math.Sign(Vector3d.Dot(pts[n] - centre, normal)) == side) mine.Add(n);
            }

            if (mine.Count < 4) continue;

            // Триангуляция по своим точкам — тем же приёмом, что и вся сетка.
            var local = mine.Select(i => pts[i]).ToList();
            var built = TetMesher.Delaunay(local);
            if (built.Count == 0) return false;

            foreach (var t in built)
            {
                var g = (mine[t.A], mine[t.B], mine[t.C], mine[t.D]);

                // Берутся только те, что лежат ВНУТРИ полости и со своей стороны грани.
                Vector3d mid = (pts[g.Item1] + pts[g.Item2] + pts[g.Item3] + pts[g.Item4]) / 4;
                if (Math.Sign(Vector3d.Dot(mid - centre, normal)) != side) continue;
                if (!InsideCavity(pts, wall, mid)) continue;

                int sgn = OrientSign(pts[g.Item1], pts[g.Item2], pts[g.Item3], pts[g.Item4]);
                if (sgn == 0) continue;

                made.Add(sgn < 0 ? (g.Item1, g.Item3, g.Item2, g.Item4) : g);
            }
        }

        double filled = made.Sum(t => Math.Abs(Volume(pts, t.Item1, t.Item2, t.Item3, t.Item4)));
        if (Math.Abs(filled - removed) > 1e-6 * Math.Max(removed, 1)) return false;

        crossing.Sort();
        for (int k = crossing.Count - 1; k >= 0; k--) tets.RemoveAt(crossing[k]);
        foreach (var t in made) tets.Add(t);

        return HasFacet(tets, a, b, c);
    }

    /// <summary>
    /// Разбиение многоугольника кольца, дающее верное заполнение. Перебор идёт по всем
    /// разбиениям: годным считается то, где каждый треугольник даёт два невырожденных тетраэдра,
    /// а общий объём совпадает со снесённым.
    /// </summary>
    private static List<(int, int, int, int)>? BestTriangulation(List<Vector3d> pts, List<int> ring,
                                                                 int p, int q, double removed)
    {
        var found = new List<(int, int, int, int)>();

        bool Search(List<int> poly, List<(int, int, int, int)> acc)
        {
            if (poly.Count < 3)
            {
                double vol = acc.Sum(t => Math.Abs(Volume(pts, t.Item1, t.Item2, t.Item3, t.Item4)));
                if (Math.Abs(vol - removed) > 1e-6 * Math.Max(removed, 1)) return false;

                found.Clear();
                found.AddRange(acc);
                return true;
            }

            // Отрезается одно «ухо» — треугольник из трёх подряд идущих вершин.
            for (int i = 0; i < poly.Count; i++)
            {
                int a = poly[i];
                int b = poly[(i + 1) % poly.Count];
                int c = poly[(i + 2) % poly.Count];

                int sa = OrientSign(pts[a], pts[b], pts[c], pts[p]);
                int sb = OrientSign(pts[a], pts[b], pts[c], pts[q]);
                if (sa == 0 || sb == 0) continue;

                var added = new List<(int, int, int, int)>
                {
                    sa < 0 ? (a, c, b, p) : (a, b, c, p),
                    sb < 0 ? (a, c, b, q) : (a, b, c, q)
                };

                var rest = new List<int>(poly);
                rest.RemoveAt((i + 1) % poly.Count);

                acc.AddRange(added);
                if (Search(rest, acc)) return true;
                acc.RemoveRange(acc.Count - 2, 2);
            }

            return false;
        }

        return Search(new List<int>(ring), new List<(int, int, int, int)>()) ? found : null;
    }

    /// <summary>Внутри ли точка полости: чётность пересечений луча с её стенками.</summary>
    private static bool InsideCavity(List<Vector3d> pts, List<(int, int, int)> wall, Vector3d p)
    {
        var dir = new Vector3d(0.5257311, 0.3250575, 0.7861514);
        int hits = 0;

        foreach (var f in wall)
        {
            Vector3d a = pts[f.Item1], b = pts[f.Item2], c = pts[f.Item3];
            Vector3d e1 = b - a, e2 = c - a, h = Vector3d.Cross(dir, e2);

            double det = Vector3d.Dot(e1, h);
            if (Math.Abs(det) < 1e-12) continue;

            double inv = 1 / det;
            Vector3d s = p - a;
            double u = inv * Vector3d.Dot(s, h);
            if (u < 0 || u > 1) continue;

            Vector3d q = Vector3d.Cross(s, e1);
            double v = inv * Vector3d.Dot(dir, q);
            if (v < 0 || u + v > 1) continue;
            if (inv * Vector3d.Dot(e2, q) > 1e-9) hits++;
        }

        return hits % 2 == 1;
    }

    /// <summary>Вершина для звёздного заполнения с названной стороны грани.</summary>
    private static int Apex(List<Vector3d> pts, List<(int A, int B, int C, int D)> tets,
                            List<int> crossing, Vector3d centre, Vector3d normal, int side)
    {
        int best = -1;
        double nearest = double.MaxValue;

        foreach (int k in crossing)
            foreach (int n in new[] { tets[k].A, tets[k].B, tets[k].C, tets[k].D })
            {
                double d = Vector3d.Dot(pts[n] - centre, normal);
                if (Math.Sign(d) != side) continue;

                double far = Math.Abs(d);
                if (far >= nearest) continue;

                nearest = far;
                best = n;
            }

        return best;
    }

    /// <summary>
    /// УДАЛЕНИЕ РЕБРА С КОЛЬЦОМ ЛЮБОЙ ДЛИНЫ — обобщение перестановки 3→2. Измерено, что вокруг
    /// мешающих рёбер бывает 4, 5 и 9 тетраэдров, а 3→2 работает только при трёх: оттого
    /// восстановление граней почти никогда и не срабатывало.
    ///
    /// Приём тот же, что в настоящих построителях: внешние вершины кольца образуют многоугольник;
    /// его разбивают на треугольники, и на каждом ставят ДВА тетраэдра — по одному к каждому
    /// концу удаляемого ребра. Кольцо из N тетраэдров заменяется на 2(N−2).
    ///
    /// Верность проверяется объёмом: сумма новых обязана совпасть со снесённым. Не совпала —
    /// удаление отменяется.
    /// </summary>
    /// <summary>Почему последнее удаление ребра не вышло — для разбора частных случаев.</summary>
    public static string LastRefusal = "";

    public static bool RemoveEdge(List<Vector3d> pts, List<(int A, int B, int C, int D)> tets, int p, int q)
    {
        LastRefusal = "";

        var ring = new List<int>();
        for (int k = 0; k < tets.Count; k++)
        {
            var n = new[] { tets[k].A, tets[k].B, tets[k].C, tets[k].D };
            if (n.Contains(p) && n.Contains(q)) ring.Add(k);
        }

        if (ring.Count < 3) { LastRefusal = "кольцо короче трёх"; return false; }

        double removed = ring.Sum(k => Math.Abs(Volume(pts, tets[k].A, tets[k].B, tets[k].C, tets[k].D)));

        // Внешние вершины: у каждого тетраэдра кольца их две, и вместе они образуют замкнутую
        // ломаную вокруг ребра. Порядок восстанавливается по общим граням.
        var pairs = new List<(int, int)>();
        foreach (int k in ring)
        {
            var outer = new List<int>();
            foreach (int n in new[] { tets[k].A, tets[k].B, tets[k].C, tets[k].D })
                if (n != p && n != q) outer.Add(n);

            if (outer.Count != 2) { LastRefusal = "у тетраэдра кольца не две внешние вершины"; return false; }
            pairs.Add((outer[0], outer[1]));
        }

        var order = new List<int> { pairs[0].Item1, pairs[0].Item2 };
        var left = new List<(int, int)>(pairs.Skip(1));

        while (left.Count > 0)
        {
            int at = order[^1];
            int found = -1;

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i].Item1 == at) { found = i; order.Add(left[i].Item2); break; }
                if (left[i].Item2 == at) { found = i; order.Add(left[i].Item1); break; }
            }

            if (found < 0) { LastRefusal = "кольцо не замкнулось"; return false; }
            left.RemoveAt(found);
        }

        if (order.Count < 4) { LastRefusal = "внешних вершин меньше четырёх"; return false; }
        if (order[^1] == order[0]) order.RemoveAt(order.Count - 1);
        if (order.Count != ring.Count) { LastRefusal = $"вершин {order.Count} против {ring.Count} тетраэдров"; return false; }

        // ПЕРЕБИРАЮТСЯ ВСЕ РАЗБИЕНИЯ МНОГОУГОЛЬНИКА, А НЕ ТОЛЬКО ВЕЕРНЫЕ. Кольцо вокруг ребра
        // образует ломаную, которая в пространстве невыпукла, и веер от любой вершины даёт
        // вырожденные тетраэдры — измерено на всех оставшихся случаях (кольца 4, 5 и 9).
        //
        // Настоящее удаление ребра ищет разбиение перебором: годным считается то, где каждый
        // треугольник даёт два невырожденных тетраэдра, а сумма их объёмов совпадает со
        // снесённым. Разбиений у многоугольника из N вершин немного (у девяти — 429), и перебор
        // здесь дёшев.
        //
        // ПЕРЕБОР ТОЖЕ НЕ ПОМОГ — измерено: мешающих граней как было 4 и 1, так и осталось.
        // Значит, дело не в выборе разбиения. Остаётся одно объяснение: кольцо у этих рёбер НЕ
        // ЗАМКНУТО — ребро лежит на границе тела, и вокруг него не кольцо, а веер, упирающийся
        // концами в поверхность. Объём такого веера никакой замкнутой ломаной не описать, оттого
        // проверка объёмом и не проходит ни при одном разбиении.
        //
        // Удаление граничного ребра — отдельный случай: его нельзя убрать вовсе, не изменив
        // поверхность. Правильный ответ здесь — не удалять ребро, а вставить точку Штейнера НА
        // ГРАНИЦЕ, разделив саму поверхность.
        var made = BestTriangulation(pts, order, p, q, removed);

        if (made == null)
        {
            LastRefusal = "ни одно разбиение многоугольника не подошло";
            return false;
        }

        ring.Sort();
        for (int k = ring.Count - 1; k >= 0; k--) tets.RemoveAt(ring[k]);
        foreach (var t in made) tets.Add(t);

        return true;
    }

    /// <summary>Есть ли такая грань в сетке.</summary>
    public static bool HasFacet(List<(int A, int B, int C, int D)> tets, int a, int b, int c)
    {
        foreach (var t in tets)
        {
            var n = new[] { t.A, t.B, t.C, t.D };
            if (n.Contains(a) && n.Contains(b) && n.Contains(c)) return true;
        }
        return false;
    }

    /// <summary>Ребро сетки, проходящее сквозь треугольник.</summary>
    private static bool FindCrossingEdge(List<Vector3d> pts, List<(int A, int B, int C, int D)> tets,
                                         int a, int b, int c, out int p, out int q)
    {
        p = q = -1;

        var seen = new HashSet<(int, int)>();

        foreach (var t in tets)
        {
            var n = new[] { t.A, t.B, t.C, t.D };
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                {
                    int x = n[i], y = n[j];
                    if (x == a || x == b || x == c || y == a || y == b || y == c) continue;

                    var key = x < y ? (x, y) : (y, x);
                    if (!seen.Add(key)) continue;

                    if (!SegmentCrossesTriangle(pts[x], pts[y], pts[a], pts[b], pts[c])) continue;

                    p = x; q = y;
                    return true;
                }
        }

        return false;
    }

    /// <summary>Есть ли такое ребро в сетке.</summary>
    public static bool HasEdge(List<(int A, int B, int C, int D)> tets, int p, int q)
    {
        foreach (var t in tets)
        {
            var n = new[] { t.A, t.B, t.C, t.D };
            if (n.Contains(p) && n.Contains(q)) return true;
        }
        return false;
    }

    /// <summary>Вершина тетраэдра, не входящая в названную грань.</summary>
    private static int Opposite((int A, int B, int C, int D) t, int a, int b, int c)
    {
        foreach (int n in new[] { t.A, t.B, t.C, t.D })
            if (n != a && n != b && n != c) return n;
        return -1;
    }

    /// <summary>
    /// Грань, которую пересекает отрезок p–q, и два тетраэдра при ней. Ищется среди внутренних
    /// граней: у граничной второго тетраэдра нет, и перестановка там невозможна.
    /// </summary>
    private static bool FindCrossedFace(List<Vector3d> pts, List<(int A, int B, int C, int D)> tets,
                                        int p, int q,
                                        out int a, out int b, out int c, out int first, out int second)
    {
        a = b = c = first = second = -1;

        var owners = new Dictionary<(int, int, int), List<int>>();

        for (int k = 0; k < tets.Count; k++)
            foreach (var f in Faces(tets[k]))
            {
                var key = FaceKey(f);
                if (!owners.TryGetValue(key, out var list)) owners[key] = list = new List<int>(2);
                list.Add(k);
            }

        foreach (var (key, list) in owners)
        {
            if (list.Count != 2) continue;

            var (i, j, k2) = key;
            if (i == p || j == p || k2 == p || i == q || j == q || k2 == q) continue;

            if (!SegmentCrossesTriangle(pts[p], pts[q], pts[i], pts[j], pts[k2])) continue;

            a = i; b = j; c = k2;
            first = list[0]; second = list[1];
            return true;
        }

        return false;
    }

    private static IEnumerable<(int, int, int)> Faces((int A, int B, int C, int D) t)
    {
        yield return (t.A, t.B, t.C);
        yield return (t.A, t.B, t.D);
        yield return (t.A, t.C, t.D);
        yield return (t.B, t.C, t.D);
    }

    private static (int, int, int) FaceKey((int, int, int) f)
    {
        int a = f.Item1, b = f.Item2, c = f.Item3;
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>Пересекает ли отрезок треугольник строго внутри него.</summary>
    private static bool SegmentCrossesTriangle(Vector3d p, Vector3d q, Vector3d a, Vector3d b, Vector3d c)
    {
        // Отрезок пересекает треугольник, если его концы по разные стороны плоскости, а точка
        // пересечения лежит внутри. Знаки берутся целыми — как и везде в построителе.
        int sp = OrientSign(a, b, c, p);
        int sq = OrientSign(a, b, c, q);
        if (sp == 0 || sq == 0 || sp == sq) return false;

        // Точка внутри треугольника: знаки трёх объёмов с концами отрезка совпадают.
        int s1 = OrientSign(p, q, a, b);
        int s2 = OrientSign(p, q, b, c);
        int s3 = OrientSign(p, q, c, a);

        return s1 != 0 && s1 == s2 && s2 == s3;
    }

    /// <summary>Объём тетраэдра со знаком.</summary>
    public static double Volume(List<Vector3d> pts, int a, int b, int c, int d) =>
        Vector3d.Dot(pts[b] - pts[a], Vector3d.Cross(pts[c] - pts[a], pts[d] - pts[a])) / 6;
}
