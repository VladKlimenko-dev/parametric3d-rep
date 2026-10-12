using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// Точная форма тела вместе с аналитическими поверхностями его граней.
///
/// Поверхности держатся отдельно от ExactSolid: само точное ядро ничего
/// не знает о цилиндрах и сплайнах, оно работает только с плоскостями.
/// Здесь же — какая исходная поверхность у каждого номера происхождения.
/// </summary>
public sealed class ExactBody
{
    public readonly ExactSolid Solid;
    public readonly Dictionary<uint, ISurface3?> Sources;

    public ExactBody(ExactSolid solid, Dictionary<uint, ISurface3?> sources)
    {
        Solid = solid; Sources = sources;
    }
}

/// <summary>
/// ПЕРЕХОДНИК между TSolid приложения и точным ядром.
///
/// ОКРУГЛЕНИЕ НА СЕТКУ — ОДИН РАЗ. Тело, пришедшее из выдавливания или
/// вращения, переводится в точную форму при первой булевой. Результат
/// булевой несёт свою точную форму дальше (TSolid.Exact), и следующая
/// операция берёт её, а не переводит тело заново. Иначе каждая операция
/// округляла бы заново, и точность таяла бы от шага к шагу.
/// </summary>
public static class ExactAdapter
{
    /// <summary>Сколько раз вход отверг устаревшую точную форму и перевёл тело заново (статистика поставки 2).</summary>
    public static long StaleExactUses;
    [ThreadStatic] public static Action<string>? StaleExactTrace;

    /// <summary>Точная форма тела в указанном реестре.</summary>
    public static ExactBody ToExact(TSolid solid, PlaneRegistry registry, List<string>? warnings)
    {
        if (solid.Exact is { } cached)
        {
            // Поставка 2: УСТАРЕВШАЯ ТОЧНАЯ ФОРМА НЕ ГОДИТСЯ — тело переводится заново из B-rep. Устаревает она
            // от правки формы после её получения (TSolid.BeginEdit). Замер перед решением: на allA, allB, allS
            // и FProbe устаревшая форма не встречалась ни разу — на корпусе поведение то же, а неверный ответ
            // в будущем исключён. Счётчик остаётся: сколько раз форма отвергнута.
            if (solid.ExactIsCurrent) return new ExactBody(cached.Solid.Import(registry), cached.Sources);
            Interlocked.Increment(ref StaleExactUses);
            StaleExactTrace?.Invoke($"stale exact ignored on solid {solid.Id}: revision {solid.Revision}, exact {solid.ExactRevision}, authoritative {solid.ExactAuthoritative}, last edit {solid.LastEdit}");
        }

        // Ленивая производная форма аналитического тела (гранением точных поверхностей до разметки отображения).
        if (solid.Exact == null && solid.LazyDerivedExact is { } lz && lz.Revision == solid.Revision)
        {
            var form = lz.Form.Value;
            return new ExactBody(form.Solid.Import(registry), form.Sources);
        }

        // A04. Только целый цилиндр вдоль +Z со строго проверенной топологией.
        // Отключение для сравнения с общим путём: P3D_CYLINDERINPUT=0.
        if (Environment.GetEnvironmentVariable("P3D_CYLINDERINPUT") != "0" &&
            ExactCylinderInput.TryBuild(solid, registry, out var direct))
            return direct!;

        // Треугольники берутся той же дорогой, что у прежней булевой, —
        // с согласованной ориентацией, — но в двойной точности.
        List<CsgPolygon> tris;
        Dictionary<uint, ISurface3?> sources;
        using (Diagnostics.Prof.Time("Exact input: triangles"))
            tris = BooleanOp.ToPolygonsInternal(solid, out sources, warnings);

        // Одна плоская грань — одна точная плоскость (ExactPlanarInput). Null — прежний вход даёт то же самое
        // (все вершины на своих узлах сетки) или решение не удалось; тогда — прежняя дорога, побитово.
        if (ExactPlanarInput.Enabled)
            using (Diagnostics.Prof.Time("Exact input: planar faces"))
            {
                var points = ExactPlanarInput.Solve(new PlaneRegistry(), tris, sources, out var corners, out var hints, target: registry);
                if (points != null)
                {
                    var planar = new List<ExactPolygon>(tris.Count);
                    for (int t = 0; t < tris.Count; t++)
                    {
                        var a = points[corners[t][0]]; var b = points[corners[t][1]]; var c = points[corners[t][2]];
                        if (a.IsGrid && b.IsGrid && c.IsGrid)
                        {
                            // Треугольник из узлов сетки — прежней дорогой (те же плоскости, та же регистрация).
                            GridPoint g0 = a.Grid, g1 = b.Grid, g2 = c.Grid;
                            if (Collinear(g0, g1, g2)) continue;
                            OPlane S0 = registry.PlaneThrough(g0, g1, g2);
                            OPlane? c0 = Bound(registry, S0, g0, g1, g2), c1 = Bound(registry, S0, g1, g2, g0), c2 = Bound(registry, S0, g2, g0, g1);
                            if (c0 == null || c1 == null || c2 == null) continue;
                            planar.Add(new ExactPolygon(registry, S0, new[] { c0.Value, c1.Value, c2.Value }, tris[t].Tag, g0, g1, g2));
                        }
                        else planar.AddRange(ExactPlanarInput.Triangles(registry, new List<CsgPolygon> { tris[t] }, points, new[] { corners[t] }));
                    }
                    var built = new ExactSolid(registry, ExactMerge.Fragments(registry, planar));
                    if (built.UncancelledSegments() == 0 && built.VolumeMm3().Sign > 0)
                    {
                        foreach (var h in hints) registry.AddFaceHint(h.A, h.B, h.C, h.D, h.N, h.W);
                        return new ExactBody(built, sources);
                    }
                    ExactPlanarInput.Trace?.Invoke("planar input rejected: open or inverted shell");
                }
            }

        var polys = new List<ExactPolygon>(tris.Count);

        // ОДНА ТОЧКА — ОДИН УЗЕЛ СЕТКИ, ИЗ КАКОЙ БЫ ГРАНИ ОНА НИ ПРИШЛА.
        //
        // ИСПРАВЛЕНО. Каждая грань разбивается на треугольники сама, и одна и та же
        // точка приходит от соседних граней с расхождением: у резца скругления
        // точки дуги на торце и на цилиндрической стороне расходились на 8.5e-7 мм.
        // Каждая округлялась на сетку отдельно; попав по разные стороны границы
        // округления, они давали щель — резец входил в ядро дырявым, вычитание им
        // рвало тело, и срабатывал откат. Это нашёл проверочный прогон
        // (Parametric3D.Verify). Теперь точки ближе половины шага сетки
        // склеиваются в одного представителя ДО округления: такие точки легли бы
        // в один или соседние узлы и так, и правило «округлять один раз» не
        // нарушается — выбор узла лишь становится единым для всех граней.
        double tau = 0.5 / ExactGrid.UnitsPerMm;
        var cells = new Dictionary<(long, long, long), List<Vector3d>>();

        Vector3d Canon(Vector3d p)
        {
            long cx = (long)Math.Floor(p.X / tau), cy = (long)Math.Floor(p.Y / tau), cz = (long)Math.Floor(p.Z / tau);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (var q in list)
                                if ((q - p).LengthSquared <= tau * tau) return q;

            if (!cells.TryGetValue((cx, cy, cz), out var own)) cells[(cx, cy, cz)] = own = new List<Vector3d>();
            own.Add(p);
            return p;
        }

        using (Diagnostics.Prof.Time("Exact input: grid and planes"))
        foreach (CsgPolygon t in tris)
        {
            if (t.V.Count != 3) throw new InvalidOperationException("expected triangles");

            GridPoint g0 = Snap(Canon(t.V[0])), g1 = Snap(Canon(t.V[1])), g2 = Snap(Canon(t.V[2]));

            // Треугольник, выродившийся при округлении, пропускается:
            // его стороны лежат на одной прямой и взаимно гасятся, так что
            // замкнутость от этого не страдает.
            if (Collinear(g0, g1, g2)) continue;

            OPlane S = registry.PlaneThrough(g0, g1, g2);

            OPlane? b0 = Bound(registry, S, g0, g1, g2);
            OPlane? b1 = Bound(registry, S, g1, g2, g0);
            OPlane? b2 = Bound(registry, S, g2, g0, g1);

            if (b0 == null || b1 == null || b2 == null) continue;

            polys.Add(new ExactPolygon(registry, S, new[] { b0.Value, b1.Value, b2.Value }, t.Tag, g0, g1, g2));
        }

        // Треугольники одной грани сливаются обратно в выпуклые куски:
        // прямоугольная грань — один многоугольник, а не два.
        using (Diagnostics.Prof.Time("Exact input: merge"))
            return new ExactBody(new ExactSolid(registry, ExactMerge.Fragments(registry, polys)), sources);
    }

    /// <summary>
    /// Треугольники результата в double — для сборки TSolid прежней дорогой
    /// (FromPolygons): рёбра, распознавание дуг, гладкие стыки работают как раньше.
    ///
    /// T-СТЫКИ СНИМАЮТСЯ ЗДЕСЬ, ТОЧНО. Разбиение пространства оставляет вершину
    /// одного куска посреди ребра соседнего. Прежде это чинилось поиском на
    /// double по всей сетке — на серии из 64 отверстий 3.9 из 4.6 секунды
    /// сборки. Здесь T-стык — вершина, лежащая на канонической прямой ребра
    /// между его концами: вершины каждой прямой упорядочиваются по точному
    /// параметру и вставляются в рёбра. Ни одного сравнения с допуском.
    ///
    /// ВЕЕР ИЗ ЦЕНТРА. Веер из первой вершины даёт вырожденные треугольники
    /// вдоль её собственных рёбер, и вставленные там точки выпадают — трещина
    /// возвращается. Центр выпуклого многоугольника всегда внутри, и все
    /// граничные вершины попадают в треугольники.
    /// </summary>
    /// <summary>
    /// Кольца вершин каждого многоугольника со вставленными T-стыками: все вершины,
    /// лежащие на прямой ребра между его концами, — по порядку. У соседних
    /// многоугольников общие отрезки колец совпадают точно, по номерам вершин.
    /// </summary>
    public static List<int>[] Rings(PlaneRegistry R, IReadOnlyList<ExactPolygon> polys)
    {
        var V = new int[polys.Count][];
        var L = new LineKey[polys.Count][];
        var onLine = new Dictionary<LineKey, Dictionary<int, LinePar>>();
        // K07: прямая пары плоскостей считается один раз. Соседние многоугольники делят стороны,
        // и одна и та же пара (опорная, ограничивающая) встречается многократно; каждый расчёт —
        // шесть произведений больших целых и НОД.
        var lines = new Dictionary<(int, int), LineKey>();

        var _p1 = Diagnostics.Prof.Time("кольца: прямые и параметры");
        for (int k = 0; k < polys.Count; k++)
        {
            ExactPolygon p = polys[k];
            V[k] = p.Vertices(R).ToArray();
            int n = V[k].Length;
            L[k] = new LineKey[n];

            for (int i = 0; i < n; i++)
            {
                int s0 = p.Support.Id, s1 = p.Bounds[i].Id;
                var pair = s0 < s1 ? (s0, s1) : (s1, s0);
                if (!lines.TryGetValue(pair, out var key)) lines[pair] = key = new LineKey(R.Line(s0, s1));
                L[k][i] = key;

                if (!onLine.TryGetValue(key, out var set)) onLine[key] = set = new();
                foreach (int v in new[] { V[k][i], V[k][(i + 1) % n] })
                    if (!set.ContainsKey(v)) set[v] = LinePar.Of(R, v, key);
            }
        }

        _p1.Dispose();
        var _p2 = Diagnostics.Prof.Time("кольца: сортировка");
        var sorted = new Dictionary<LineKey, (LinePar T, int V)[]>(onLine.Count);
        // K07: место каждой вершины в упорядоченном ряду своей прямой. Точки между концами
        // стороны — ровно элементы ряда между их местами (разные точки прямой имеют разные
        // параметры), поэтому перебор всего ряда со сравнением дробей на каждую сторону не нужен.
        var place = new Dictionary<LineKey, Dictionary<int, int>>();
        foreach (var (key, set) in onLine)
        {
            var arr = LinePar.Sort(set);
            sorted[key] = arr;
            if (arr.Length > 16)
            {
                var at = new Dictionary<int, int>(arr.Length);
                for (int j = 0; j < arr.Length; j++) at[arr[j].Item2] = j;
                place[key] = at;
            }
        }

        // Место вершины в ряду: у короткого ряда — перебором номеров, у длинного — по справочнику.
        int Place(LineKey key, (LinePar T, int V)[] arr, int v)
        {
            if (arr.Length <= 16) { for (int j = 0; j < arr.Length; j++) if (arr[j].V == v) return j; return -1; }
            return place[key][v];
        }

        _p2.Dispose();
        using var _p3 = Diagnostics.Prof.Time("кольца: сборка");
        // Кольца вершин со вставленными T-стыками — сначала все, потом треугольники:
        // слиянию граней нужно знать соседство, а оно видно только по кольцам.
        var rings = new List<int>[polys.Count];

        for (int k = 0; k < polys.Count; k++)
        {
            int n = V[k].Length;
            var ring = new List<int>(n * 2);

            for (int i = 0; i < n; i++)
            {
                int u = V[k][i], w = V[k][(i + 1) % n];
                ring.Add(u);

                var line = sorted[L[k][i]];
                int iu = Place(L[k][i], line, u), iw = Place(L[k][i], line, w);

                if (iu < iw)
                    for (int j = iu + 1; j < iw; j++) ring.Add(line[j].V);
                else
                    for (int j = iu - 1; j > iw; j--) ring.Add(line[j].V);
            }

            rings[k] = ring;
        }

        return rings;
    }

    /// <summary>
    /// ОСКОЛКИ НИЧТОЖНОГО ОБЪЁМА ВЫБРАСЫВАЮТСЯ.
    ///
    /// ДОБАВЛЕНО по проверочному прогону (Parametric3D.Verify). Там, где стенка
    /// отверстия прорезает плоскую грань под острым углом, гранёная стенка
    /// пересекает плоскость чуть иначе, чем настоящая окружность, и между хордой
    /// и плоскостью остаются тончайшие клинья материала, отделённые от тела.
    /// Их объём — 1e-10 мм³, геометрически это почти линия; формально оболочка
    /// замкнута, но это артефакт гранения, и в теле ему не место.
    ///
    /// Куски ищутся по общим отрезкам колец (Rings): по вершинам осколок слился бы
    /// с телом, которого касается в точке, а по целым рёбрам T-стыки разорвали бы
    /// само тело на ложные куски. Объём куска — точный. Если ничтожно всё тело,
    /// не выбрасывается ничего.
    /// </summary>
    public static List<ExactPolygon> DropSlivers(PlaneRegistry R, List<ExactPolygon> polys,
                                                 double minVolumeMm3, out int dropped)
        => DropSlivers(R, polys, minVolumeMm3, out dropped, out _);

    /// <summary>
    /// То же, с кольцами: если ничего не выброшено, возвращается тот же список, и кольца годны
    /// для него — ToCsg не считает их второй раз (K07). Иначе rings = null.
    /// </summary>
    public static List<ExactPolygon> DropSlivers(PlaneRegistry R, List<ExactPolygon> polys,
                                                 double minVolumeMm3, out int dropped, out List<int>[]? ringsOut)
    {
        dropped = 0;
        ringsOut = null;
        if (polys.Count < 2) return polys;

        List<int>[] rings = Rings(R, polys);
        ringsOut = rings;

        var parent = new int[polys.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        var first = new Dictionary<(int, int), int>();
        for (int k = 0; k < polys.Count; k++)
        {
            var ring = rings[k];
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i], b = ring[(i + 1) % ring.Count];
                var key = a < b ? (a, b) : (b, a);
                if (first.TryGetValue(key, out int other)) parent[Find(k)] = Find(other);
                else first[key] = k;
            }
        }

        var groups = Enumerable.Range(0, polys.Count).GroupBy(Find).ToList();
        if (groups.Count < 2) return polys;
        ringsOut = null;

        var keep = new List<ExactPolygon>(polys.Count);
        foreach (var g in groups)
        {
            var part = g.Select(i => polys[i]).ToList();
            double v = Math.Abs(new ExactSolid(R, part).VolumeMm3().ToDouble());

            // Мера осколка — не только объём, но и ТОЛЩИНА: объём, делённый на
            // площадь. Длинная щепка между двумя почти совпадающими плоскостями
            // разных фасок весит 4.5e-4 мм³ — больше порога объёма, — но толщиной
            // меньше микрона. Деталь тоньше микрона — всегда артефакт.
            double area = 0;
            foreach (var poly in part)
            {
                var pts = poly.Vertices(R).ToArray().Select(R.ToMm).Select(q => new Vector3d(q.X, q.Y, q.Z)).ToList();
                for (int i = 1; i + 1 < pts.Count; i++)
                    area += Vector3d.Cross(pts[i] - pts[0], pts[i + 1] - pts[0]).Length / 2;
            }

            bool sliver = v <= minVolumeMm3 || (area > 0 && v / area <= 1e-3);
            if (!sliver) keep.AddRange(part);
            else dropped++;
        }

        if (keep.Count == 0) { dropped = 0; ringsOut = rings; return polys; }
        if (dropped == 0) { ringsOut = rings; return polys; }
        return keep;
    }

    public static List<CsgPolygon> ToCsg(ExactSolid solid, IReadOnlyDictionary<uint, ISurface3?>? sources = null,
                                         List<int>[]? knownRings = null)
    {
        var R = solid.Registry;
        var polys = solid.Polygons;

        List<int>[] rings = knownRings != null && knownRings.Length == polys.Count ? knownRings : Rings(R, polys);

        uint[] display = MergeCoplanarFaces(polys, rings, sources);

        var result = new List<CsgPolygon>(polys.Count * 4);
        var mm = new Dictionary<int, Vector3d>();

        Vector3d At(int v)
        {
            if (!mm.TryGetValue(v, out var q))
            {
                var (x, y, z) = R.ToMm(v);
                mm[v] = q = new Vector3d(x, y, z);
            }
            return q;
        }

        var owners = new Dictionary<Vector3d,int>();
        var collapsed = new HashSet<int>();
        // Distinct rational vertices can have the same double representation.
        // Collapse only bit-equal consecutive ring points BEFORE adding a fan
        // centre. Removing degenerate triangles afterwards leaves broken fans.
        // The rational solid and all geometric tolerances remain unchanged.
        foreach(var ring in rings)
            foreach(int v in ring)
            {
                var q=At(v);
                if(owners.TryGetValue(q,out int other) && other!=v)
                    {collapsed.Add(other);collapsed.Add(v);}
                else owners[q]=v;
            }

        for (int k = 0; k < polys.Count; k++)
        {
            var ring = rings[k];

            var (nx, ny, nz) = R.UnitNormal(polys[k].Support);
            var normal = new Vector3d(nx, ny, nz);

            if(ring.Any(collapsed.Contains))
            {
                var clean=new List<Vector3d>();
                foreach(int v in ring)
                {
                    var q=At(v);
                    if(clean.Count==0||clean[^1]!=q)clean.Add(q);
                }
                if(clean.Count>1&&clean[0]==clean[^1])clean.RemoveAt(clean.Count-1);
                if(clean.Count<3)continue;
                if(clean.Count==3)
                {
                    result.Add(new CsgPolygon(clean,display[k]){N=normal,W=Vector3d.Dot(normal,clean[0])});
                    continue;
                }
                Triangulate(clean, normal, display[k], result);
                continue;
            }

            var pts = ring.Select(At).ToList();
            Triangulate(pts, normal, display[k], result);
        }

        return result;
    }

    /// <summary>
    /// Треугольники кольца. Веер из центра верен только для кольца, звёздного относительно центра
    /// (выпуклое кольцо после деления BSP). После слияния компланарных кусков кольцо бывает
    /// невыпуклым (край плоской грани у скругления повёрнутой ступеньки): веер из центра давал
    /// треугольники вне кольца, они накрывали соседние грани, и сетка тела выходила
    /// неманифолдной. Тогда — разбиение «отрезанием ушей» в плоскости кольца.
    /// </summary>
    private static void Triangulate(List<Vector3d> pts, Vector3d normal, uint display, List<CsgPolygon> result)
    {
        var origin = pts[0]; var sum = Vector3d.Zero;
        foreach (var q in pts) sum += q - origin;
        var centre = origin + sum / pts.Count;
        bool star = true;
        double scale = 0; foreach (var q in pts) scale = Math.Max(scale, (q - centre).LengthSquared);
        for (int i = 0; i < pts.Count && star; i++)
            if (Vector3d.Dot(Vector3d.Cross(pts[i] - centre, pts[(i + 1) % pts.Count] - centre), normal) < -1e-12 * scale) star = false;   // вывернутый треугольник веера (нулевые прежде тоже были и годны)
        if (star)
        {
            for (int i = 0; i < pts.Count; i++)
                result.Add(new CsgPolygon(new() { centre, pts[i], pts[(i + 1) % pts.Count] }, display) { N = normal, W = Vector3d.Dot(normal, centre) });
            return;
        }
        var f = Frame3.FromNormal(centre, normal);
        var uv = pts.Select(q => new Vector2d(Vector3d.Dot(q - centre, f.U), Vector3d.Dot(q - centre, f.V))).ToList();
        var tris = Tess.Triangulator.Triangulate(uv, new List<IReadOnlyList<Vector2d>>(), out var outPts);
        var map = new Dictionary<Vector2d, Vector3d>();
        for (int i = 0; i < uv.Count; i++) map[uv[i]] = pts[i];
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            var a = map[outPts[tris[i]]]; var b = map[outPts[tris[i + 1]]]; var c = map[outPts[tris[i + 2]]];
            // Обход — как у кольца (вокруг нормали).
            if (Vector3d.Dot(Vector3d.Cross(b - a, c - a), normal) < 0) (b, c) = (c, b);
            result.Add(new CsgPolygon(new() { a, b, c }, display) { N = normal, W = Vector3d.Dot(normal, a) });
        }
    }

    /// <summary>
    /// СЛИЯНИЕ СОВПАДАЮЩИХ ГРАНЕЙ РАЗНОГО ПРОИСХОЖДЕНИЯ — ДЛЯ ЭКРАНА.
    ///
    /// Два куба вплотную — брусок с шестью гранями, а не десятью. Две метки
    /// происхождения объединяются, если обе грани плоские по исходной
    /// поверхности, лежат в одной плоскости с одной стороной и делят ребро.
    /// Объединение транзитивно; представитель — меньшая метка.
    ///
    /// Сливается только вид: точная форма хранит исходную метку каждого куска,
    /// и происхождение для устойчивых имён не теряется.
    ///
    /// Кривые грани не участвуют. У полосок скругления плоскости разные,
    /// но это одна грань — одна метка, и делить её нечего; а сливать кривую
    /// с плоской по случайно совпавшей полоске нельзя.
    /// </summary>
    private static uint[] MergeCoplanarFaces(IReadOnlyList<ExactPolygon> polys, List<int>[] rings,
                                             IReadOnlyDictionary<uint, ISurface3?>? sources)
    {
        var display = new uint[polys.Count];
        for (int k = 0; k < polys.Count; k++) display[k] = polys[k].Tag;

        if (sources == null) return display;

        var parent = new Dictionary<uint, uint>();

        uint Find(uint t)
        {
            while (parent.TryGetValue(t, out uint p) && p != t) { parent[t] = parent.GetValueOrDefault(p, p); t = p; }
            return t;
        }

        void Union(uint a, uint b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (a < b) parent[b] = a; else parent[a] = b;
        }

        bool Planar(uint tag) => sources.TryGetValue(tag, out var src) && src != null && src.IsPlanar;

        // У СТОРОНЫ НЕ БОЛЬШЕ ДВУХ ХОЗЯЕВ, И СПИСОК ЕЙ НЕ НУЖЕН. Прежде на каждую сторону
        // заводился список, то есть на каждую сторону тела — своё выделение памяти; слияние
        // кусков граней занимало 395 мс из 744 мс всего разбиения. Теперь хранится номер первого
        // хозяина, а второй обрабатывается сразу при встрече: пар всё равно ровно столько же.
        //
        // Сторона, встреченная третий раз, — не наш случай (тело негодное), и такие стороны
        // помечаются номером −1, чтобы не сливать по ним ничего.
        var firstOwner = new Dictionary<(int, int), int>(polys.Count * 2);

        for (int k = 0; k < polys.Count; k++)
        {
            var ring = rings[k];
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i], b = ring[(i + 1) % ring.Count];
                var key = a < b ? (a, b) : (b, a);

                if (!firstOwner.TryGetValue(key, out int other))
                {
                    firstOwner[key] = k;
                    continue;
                }

                if (other < 0) continue;              // сторона уже была парной
                firstOwner[key] = -1;

                ExactPolygon p = polys[other], q = polys[k];

                if (p.Tag == q.Tag) continue;
                if (p.Support != q.Support) continue;
                if (!Planar(p.Tag) || !Planar(q.Tag)) continue;

                Union(p.Tag, q.Tag);
            }
        }

        for (int k = 0; k < polys.Count; k++) display[k] = Find(polys[k].Tag);
        return display;
    }

    /// <summary>Каноническая прямая как ключ словаря.</summary>
    private readonly record struct LineKey(
        (System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger,
         System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger) Plucker)
    {
        public System.Numerics.BigInteger Ux => Plucker.Item1;
        public System.Numerics.BigInteger Uy => Plucker.Item2;
        public System.Numerics.BigInteger Uz => Plucker.Item3;
    }

    /// <summary>
    /// K07. ПАРАМЕТР ВЕРШИНЫ НА ПРЯМОЙ БЕЗ СОКРАЩЕНИЯ ДРОБИ. BigRational сокращает дробь при
    /// создании (НОД больших целых) — на каждую пару «вершина, прямая». Для упорядочения
    /// сокращение не нужно: сравнение — перекрёстным умножением. Сортировка сперва по
    /// приближённому значению; если соседние приближения неразличимы, ряд сортируется точно.
    /// </summary>
    private readonly struct LinePar
    {
        public readonly System.Numerics.BigInteger N, D;
        public readonly double A;
        private LinePar(System.Numerics.BigInteger n, System.Numerics.BigInteger d)
        {
            if (d.Sign < 0) { n = -n; d = -d; }
            N = n; D = d; A = (double)n / (double)d;
        }

        public static LinePar Of(PlaneRegistry R, int v, LineKey line)
        {
            var (X, Y, Z, W) = R.Homogeneous(v);
            return new LinePar(X * line.Ux + Y * line.Uy + Z * line.Uz, W);
        }

        public static int Compare(in LinePar a, in LinePar b) => (a.N * b.D).CompareTo(b.N * a.D);

        public static (LinePar T, int V)[] Sort(Dictionary<int, LinePar> set)
        {
            var arr = new (LinePar, int)[set.Count];
            int n0 = 0;
            foreach (var kv in set) arr[n0++] = (kv.Value, kv.Key);
            if (arr.Length == 2)
            {
                // Самый частый случай: на прямой только концы одной стороны.
                if (Compare(arr[1].Item1, arr[0].Item1) < 0) (arr[0], arr[1]) = (arr[1], arr[0]);
                return arr;
            }
            if (arr.Length < 2) return arr;
            Array.Sort(arr, (x, y) => x.Item1.A.CompareTo(y.Item1.A));
            bool safe = true;
            for (int i = 0; i + 1 < arr.Length && safe; i++)
            {
                double a = arr[i].Item1.A, b = arr[i + 1].Item1.A;
                if (!(b - a > 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))))) safe = false;
            }
            if (!safe)
            {
                arr = set.Select(kv => (kv.Value, kv.Key)).ToArray();
                // Устойчивая точная сортировка — тот же порядок, что давал OrderBy по дроби.
                arr = arr.OrderBy(x => x.Item1, Comparer<LinePar>.Create((p, q) => Compare(p, q))).ToArray();
            }
            return arr;
        }
    }

    /// <summary>Точный параметр вершины вдоль прямой: скалярное произведение с её направлением.</summary>
    private static BigRational Param(PlaneRegistry R, int v, LineKey line)
    {
        var (X, Y, Z, W) = R.Homogeneous(v);
        return new BigRational(X * line.Ux + Y * line.Uy + Z * line.Uz, W);
    }

    private static GridPoint Snap(Vector3d p) => GridPoint.FromMm(p.X, p.Y, p.Z);

    private static bool Collinear(GridPoint a, GridPoint b, GridPoint c)
    {
        Int128 ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        Int128 vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return uy * vz - uz * vy == 0 && uz * vx - ux * vz == 0 && ux * vy - uy * vx == 0;
    }

    /// <summary>
    /// Ограничивающая ребра u→v: плоскость через ребро, перпендикулярная опорной,
    /// повёрнутая так, что третья вершина внутри. Вершины многоугольника тогда
    /// совпадают с округлёнными точками ТОЧНО, и у соседнего треугольника те же
    /// точки — замкнутость входа сохраняется без всякой сварки.
    /// </summary>
    private static OPlane? Bound(PlaneRegistry R, OPlane S, GridPoint u, GridPoint v, GridPoint w)
    {
        OPlane e = R.EdgePlane(S, u, v);
        int s = R.Side(w, e);
        if (s == 0) return null;
        return s < 0 ? e : e.Flipped;
    }
}
