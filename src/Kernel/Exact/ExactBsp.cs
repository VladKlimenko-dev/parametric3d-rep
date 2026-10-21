using OpenTK.Mathematics;
namespace Parametric3D.Kernel.Exact;

/// <summary>
/// МНОГОУГОЛЬНИК, ЗАДАННЫЙ ПЛОСКОСТЯМИ.
///
/// Опорная плоскость со стороной и циклический список ограничивающих.
/// Ребро i лежит на ограничивающей Bounds[i]; вершина i — пересечение
/// опорной с Bounds[i-1] и Bounds[i]. Координаты не хранятся нигде:
/// при разрезе новая вершина не вычисляется, а плоскость разреза просто
/// встаёт в список. Поэтому резать можно сколько угодно раз без потери
/// точности (этап 4).
///
/// Tag — происхождение: номер грани, из которой многоугольник получен.
/// </summary>
public sealed class ExactPolygon
{
    public readonly OPlane Support;
    /// <summary>
    /// Ограничивающие плоскости — ТОЛЬКО ДЛЯ ЧТЕНИЯ (поставка 2, остаток): массив закрыт, наружу — ReadOnlySpan
    /// (индекс, длина, перебор — с той же скоростью, что у массива). Прежде открытый массив позволял переписать
    /// границы опубликованного многоугольника.
    /// </summary>
    private readonly OPlane[] _bounds;
    public ReadOnlySpan<OPlane> Bounds => _bounds;
    public readonly uint Tag;

    /// <summary>
    /// ПРИБЛИЖЁННЫЙ ГАБАРИТ — для быстрого отсева. Точный предикат зовётся на каждую вершину:
    /// на одно скругление это 9.57 млн вызовов, а 98.4% разрезаний ничего не режут. Габарит
    /// позволяет отнести многоугольник к стороне ОДНОЙ проверкой вместо перебора вершин.
    /// Считается лениво и хранится в самом многоугольнике.
    /// </summary>
    internal OpenTK.Mathematics.Vector3d BoxLo, BoxHi;
    internal bool BoxReady;

    public ExactPolygon(OPlane support, OPlane[] bounds, uint tag)
    {
        Support = support; _bounds = bounds; Tag = tag;
    }

    public ExactPolygon Flipped()
    {
        var b = (OPlane[])_bounds.Clone();
        Array.Reverse(b);
        return new ExactPolygon(Support.Flipped, b, Tag);
    }

    // Вершины многоугольника — один раз на реестр.
    //
    // УСКОРЕНИЕ. Многоугольник хранит плоскости, а вершины искал заново при каждом
    // запросе — по тройкам плоскостей через словарь реестра. Сама арифметика была
    // закэширована, но в дереве разбиения многоугольник спрашивают о вершинах на
    // каждом узле, и за одно скругление по кругу отверстия выходило 9 миллионов
    // поисков — больше, чем вызовов самого предиката. Многоугольник неизменяем,
    // поэтому номера вершин хранятся при нём, с реестром, которым вычислены.
    // Массив только для чтения: вызывающие его не меняют.
    private PlaneRegistry? _verticesOf;
    private int[]? _vertices;
    private PlaneRegistry? _inputRegistry;
    private GridPoint[]? _inputVertices;

    // Только ExactAdapter: невырожденный треугольник из точек сетки,
    // Bounds[i] проходит через точки i и i+1. Вершины регистрируются
    // лениво, в прежнем порядке, поэтому нумерация реестра не меняется.
    internal ExactPolygon(PlaneRegistry registry, OPlane support, OPlane[] bounds, uint tag,
                          GridPoint a, GridPoint b, GridPoint c) : this(support, bounds, tag)
    {
        if (bounds.Length != 3) throw new ArgumentException("input triangle must have three bounds");
        _inputRegistry = registry;
        _inputVertices = new[] { a, b, c };
    }

    public ReadOnlySpan<int> Vertices(PlaneRegistry R)
    {
        if (_vertices != null && ReferenceEquals(_verticesOf, R)) return _vertices;

        int n = Bounds.Length;
        var v = new int[n];
        for (int i = 0; i < n; i++)
            v[i] = ReferenceEquals(_inputRegistry, R) && _inputVertices != null
                ? R.Vertex(Support.Id, Bounds[(i + n - 1) % n].Id, Bounds[i].Id, _inputVertices[i])
                : R.Vertex(Support.Id, Bounds[(i + n - 1) % n].Id, Bounds[i].Id);

        _vertices = v;
        _verticesOf = R;
        _inputRegistry = null;
        _inputVertices = null;
        return v;
    }
}

/// <summary>
/// ТОЧНАЯ БУЛЕВА: дерево разбиения на номерах плоскостей.
///
/// Алгоритм тот же, что у нынешнего Csg (csg.js), но ни одно решение
/// не принимается сравнением с допуском. Совпадение плоскостей — сравнение
/// номеров; сторона вершины — знак целого числа. Булева не создаёт новых
/// плоскостей: каждая грань результата лежит в плоскости одного из операндов.
/// </summary>
public static class ExactBsp
{
    /// <summary>ДИАГНОСТИКА: вызовов разрезания и настоящих разрезов.</summary>
    public static long Splits, Cuts;

    /// <summary>ДИАГНОСТИКА: время в разрезании при отсечении и при построении дерева.</summary>
    public static long ClipSplitTicks, BuildSplitTicks, ClipNodes, BuildNodes, ChooseTicks;

    /// <summary>Сколько разрезаний решено габаритом, а сколько дошло до точного предиката.</summary>
    public static long BoxHits, BoxMiss;

    /// <summary>ДИАГНОСТИКА: наибольшая глубина дерева.</summary>
    public static int MaxDepth;

    /// <summary>ДИАГНОСТИКА: сколько раз встретилась бесконечно удалённая вершина.</summary>
    public static long InfiniteVertices;

    /// <summary>ДИАГНОСТИКА: повторяющиеся многоугольники в результате.</summary>
    public static Action<string>? DupDebug;

    private static void ReportDuplicates(PlaneRegistry R, List<ExactPolygon> res, List<ExactPolygon> fromB)
    {
        static string Key(PlaneRegistry r, ExactPolygon p)
        {
            var v = p.Vertices(r).ToArray().Select(x => x.ToString()).ToList();
            if (v.Count < 3) return "";
            int at = 0;
            for (int i = 1; i < v.Count; i++) if (string.CompareOrdinal(v[i], v[at]) < 0) at = i;
            return string.Join(",", Enumerable.Range(0, v.Count).Select(i => v[(at + i) % v.Count]));
        }
        var ofB = new HashSet<string>(fromB.Select(x => Key(R, x)));
        var seen = new Dictionary<string, int>();
        int dup = 0, dupFromB = 0;
        foreach (var p in res)
        {
            string k = Key(R, p);
            if (k.Length == 0) continue;
            if (seen.ContainsKey(k)) { dup++; if (ofB.Contains(k)) dupFromB++; }
            seen[k] = 1;
        }
        // Вызов через ?. — DupDebug может быть не задан, и обращение к нему без проверки давало
        // предупреждение о возможном обращении к пустой ссылке.
        if (dup > 0)
            DupDebug?.Invoke($"повторов в результате {dup}, из них пришедших от резца {dupFromB}; " +
                             $"всего многоугольников {res.Count}");
    }

    /// <summary>ДИАГНОСТИКА: совпадающие пары в результате локальной булевой.</summary>
    public static Action<string>? FilmDebug;

    // ЛОКАЛИЗАЦИЯ ПО ГАБАРИТУ РЕЗЦА ПРОБОВАЛАСЬ И ОТВЕРГНУТА.
    //
    // Дерево разбиения классифицирует точки по БЕСКОНЕЧНЫМ плоскостям граней:
    // без дальних граней тела точка материала внутри габарита резца оказывалась
    // «перед» плоскостью стенки отверстия, хотя сама стенка далеко, — и тело
    // выходило незамкнутым (нашёл проверочный прогон: туннель, пересекающий
    // глухое отверстие). Корректная локализация — разрезать тело по коробке
    // габарита на внешнюю часть и ЗАМКНУТЫЙ кусок с крышками, вычитать резец
    // только из куска и склеить обратно без крышек.

    public static List<ExactPolygon> Subtract(PlaneRegistry R, IReadOnlyList<ExactPolygon> A, IReadOnlyList<ExactPolygon> B)
        => SubtractAll(R, A, B);

    /// <summary>
    /// ЛОКАЛЬНАЯ БУЛЕВА ВНУТРИ КОРОБКИ.
    ///
    /// box — замкнутая коробка (наружные нормали), в которой резец B лежит СТРОГО
    /// внутри, и ни одна грань тела A не лежит в плоскости её граней.
    ///   1. Многоугольники A режутся коробкой на внешние и внутренние части —
    ///      нужны только деревья из шести плоскостей коробки.
    ///   2. Крышки — части граней коробки внутри A (через дерево A проходят
    ///      всего шесть многоугольников).
    ///   3. Внутренние части плюс крышки — ЗАМКНУТОЕ тело: полная булева с B
    ///      корректна, а многоугольников в ней мало.
    ///   4. Крышки выбрасываются (B их не касается — он строго внутри), внешние
    ///      части добавляются как есть.
    /// Полость выходит сама: если B внутри A и грани A в коробку не заходят,
    /// крышки — все шесть граней коробки, кусок — сама коробка.
    /// </summary>
    /// <summary>P3D_LAZYCAPS=0 — прежнее (дерево всего тела); проверка KRN сравнивает оба пути.</summary>
    public static bool LazyCapsOff = Environment.GetEnvironmentVariable("P3D_LAZYCAPS") == "0";
    public static long LazyCapsRuns;

    public static List<ExactPolygon> LocalInBox(PlaneRegistry R, IReadOnlyList<ExactPolygon> A, IReadOnlyList<ExactPolygon> B,
                                                IReadOnlyList<ExactPolygon> box, HashSet<int> boxPlanes, bool subtract)
    {
        List<ExactPolygon> outside, inside, caps;

        using (Diagnostics.Prof.Time("  бсп: разрез коробкой"))
        {
            var boxNode = new Node(R, box);
            outside = boxNode.ClipPolygons(A);          // части A снаружи коробки

            var boxInv = new Node(R, box);
            boxInv.Invert();
            inside = boxInv.ClipPolygons(A);            // части A внутри коробки
        }

        using (Diagnostics.Prof.Time("  бсп: крышки"))
        {
            if (!LazyCapsOff) { caps = Node.InsideLazy(R, A, box); Interlocked.Increment(ref LazyCapsRuns); }   // грани коробки внутри A (дерево A — только где нужно)
            else
            {
                var aInv = new Node(R, A);
                aInv.Invert();
                caps = aInv.ClipPolygons(box);              // грани коробки внутри A
            }
        }

        var piece = new List<ExactPolygon>(inside.Count + caps.Count);
        piece.AddRange(inside);
        piece.AddRange(caps);

        List<ExactPolygon> done = subtract ? SubtractAll(R, piece, B) : UnionAll(R, piece, B);

        // Разрезы по границе коробки срастаются позже, слиянием граней (ExactMerge).
        // Возврат «нетронутой» грани целиком пробовался и отвергнут: у фаски и
        // скругления стороны резца лежат прямо на гранях тела, и кусок мог выйти
        // «нетронутым» по значению при изменившейся рядом геометрии (откаты в прогоне).
        var result = new List<ExactPolygon>(outside.Count + done.Count);
        result.AddRange(outside);
        foreach (var p in done)
            if (!boxPlanes.Contains(p.Support.Id)) result.Add(p);

        if (DupDebug != null) ReportDuplicates(R, result, new List<ExactPolygon>());
        if (FilmDebug != null)
        {
            // Пары совпадающих многоугольников с противоположной намоткой в результате.
            var seen = new Dictionary<string, (int Src, int Plane)>();
            for (int i = 0; i < result.Count; i++)
            {
                var v = result[i].Vertices(R).ToArray();          // отладка плёнок: копия для локальной функции
                if (v.Length < 3) continue;
                string Key(bool flip)
                {
                    var l = v.Select(x => x.ToString()).ToList();
                    if (flip) l.Reverse();
                    int at = 0;
                    for (int k = 1; k < l.Count; k++) if (string.CompareOrdinal(l[k], l[at]) < 0) at = k;
                    return string.Join(",", Enumerable.Range(0, l.Count).Select(k => l[(at + k) % l.Count]));
                }
                int src = i < outside.Count ? 0 : 1;
                if (seen.TryGetValue(Key(true), out var other))
                    FilmDebug($"плёнка: многоугольник из {(other.Src == 0 ? "снаружи коробки" : "результата внутри")} и из " +
                              $"{(src == 0 ? "снаружи коробки" : "результата внутри")}, плоскости {other.Plane}/{result[i].Support.Id}, " +
                              $"в плоскостях коробки: {boxPlanes.Contains(other.Plane)}/{boxPlanes.Contains(result[i].Support.Id)}");
                else seen[Key(false)] = (src, result[i].Support.Id);
            }
        }
        return result;
    }

    public static List<ExactPolygon> Union(PlaneRegistry R, IReadOnlyList<ExactPolygon> A, IReadOnlyList<ExactPolygon> B)
        => UnionAll(R, A, B);

    private static List<ExactPolygon> SubtractAll(PlaneRegistry R, IReadOnlyList<ExactPolygon> A, IReadOnlyList<ExactPolygon> B)
    {
        if (A.Count == 0) return new List<ExactPolygon>();

        Node a, b;
        using (Diagnostics.Prof.Time("  бсп: дерево тела")) a = new(R, A);
        using (Diagnostics.Prof.Time("  бсп: дерево резца")) b = Caged(R, B);
        // ОТСЕЧЕНИЕ ПО ГАБАРИТАМ — ПО ПЕРЕКЛЮЧАТЕЛЮ, ВЫКЛЮЧЕНО. Работает правильно (фаски и
        // скругления проходят), но на реальных сценах выигрыша нет: резец скругления идёт
        // кольцом вокруг отверстия и по габаритам перекрывает почти всё тело, так что отсекать
        // нечего, а подсчёт габаритов сам стоит времени (778 → 802 мс). Пригодится, когда резец
        // мал по сравнению с телом — например, при сверлении отверстий в крупной детали.
        bool cull = Environment.GetEnvironmentVariable("P3D_CULL") != null;
        var (blo, bhi) = cull ? Node.ExtentOf(R, B) : (new Vector3d(double.MinValue), new Vector3d(double.MaxValue));
        var (alo, ahi) = cull ? Node.ExtentOf(R, A) : (new Vector3d(double.MinValue), new Vector3d(double.MaxValue));
        a.Invert();
        using (Diagnostics.Prof.Time("  бсп: тело по резцу")) a.ClipTo(b, blo, bhi);
        // Резец режется телом БЕЗ отсечения: он обёрнут в клетку, её многоугольники лежат
        // далеко от тела, и пропуск их разрезания оставлял клетку в результате.
        using (Diagnostics.Prof.Time("  бсп: резец по телу")) { b.ClipTo(a); b.Invert(); b.ClipTo(a); b.Invert(); }
        List<ExactPolygon> fromB;
        using (Diagnostics.Prof.Time("  бсп: сборка результата")) { fromB = b.All(); a.Build(fromB); }
        a.Invert();
        using (Diagnostics.Prof.Time("  бсп: выдача многоугольников"))
        {
            var res = a.All();
            if (DupDebug != null) ReportDuplicates(R, res, fromB);
            return res;
        }
    }

    private static List<ExactPolygon> UnionAll(PlaneRegistry R, IReadOnlyList<ExactPolygon> A, IReadOnlyList<ExactPolygon> B)
    {
        if (A.Count == 0) return new List<ExactPolygon>(B);
        Node a, b;
        using (Diagnostics.Prof.Time("  бсп: дерево тела")) a = new(R, A);
        using (Diagnostics.Prof.Time("  бсп: дерево резца")) b = Caged(R, B);
        using (Diagnostics.Prof.Time("  бсп: тело по резцу")) a.ClipTo(b);
        using (Diagnostics.Prof.Time("  бсп: резец по телу")) { b.ClipTo(a); b.Invert(); b.ClipTo(a); b.Invert(); }
        using (Diagnostics.Prof.Time("  бсп: сборка результата")) a.Build(b.All());
        using (Diagnostics.Prof.Time("  бсп: выдача многоугольников")) {
            var res = a.All();
            if (DupDebug != null) ReportDuplicates(R, res, new List<ExactPolygon>());
            return res;
        }
    }

    public static List<ExactPolygon> Intersect(PlaneRegistry R, IReadOnlyList<ExactPolygon> A, IReadOnlyList<ExactPolygon> B)
    {
        Node a = new(R, A), b = Caged(R, B);
        a.Invert(); b.ClipTo(a); b.Invert(); a.ClipTo(b); b.ClipTo(a);
        a.Build(b.All()); a.Invert();
        return a.All();
    }

    /// <summary>
    /// ДЕРЕВО ВТОРОГО ТЕЛА В КЛЕТКЕ ИЗ ШЕСТИ ПЛОСКОСТЕЙ ЕГО ГАБАРИТА.
    ///
    /// Плоскость каждой грани бесконечна и режет всё, что встречает. У резца-тора
    /// граней сотни, и их плоскости кромсали большую верхнюю грань детали далеко
    /// от самого резца: из 217 и 538 многоугольников выходило 26 763 куска.
    ///
    /// Клетка стоит над деревом: всё, что снаружи габарита, сразу признаётся
    /// «вне тела» и дальше не режется. Представляемое деревом тело от этого
    /// не меняется — тело целиком внутри своего габарита, — а при обращении
    /// дерева (Invert) узлы клетки обращаются вместе с остальными и дают
    /// дополнение правильно. Плоскости клетки — точные, по сетке; граней они
    /// не добавляют: в узлах клетки нет многоугольников.
    /// </summary>
    private static Node Caged(PlaneRegistry R, IReadOnlyList<ExactPolygon> polys)
    {
        var inner = new Node(R, polys);
        if (polys.Count == 0) return inner;

        System.Numerics.BigInteger?[] lo = new System.Numerics.BigInteger?[3], hi = new System.Numerics.BigInteger?[3];

        foreach (var p in polys)
            foreach (int v in p.Vertices(R))
            {
                var (X, Y, Z, W) = R.Homogeneous(v);
                var c = new[] { X, Y, Z };
                for (int k = 0; k < 3; k++)
                {
                    // Пол и потолок координаты X/W в единицах сетки, W > 0.
                    var fl = System.Numerics.BigInteger.Divide(c[k] - (c[k].Sign < 0 ? W - 1 : 0), W);
                    var ce = fl * W == c[k] ? fl : fl + 1;
                    if (lo[k] == null || fl < lo[k]) lo[k] = fl;
                    if (hi[k] == null || ce > hi[k]) hi[k] = ce;
                }
            }

        Node node = inner;
        for (int k = 2; k >= 0; k--)
        {
            var e = new System.Numerics.BigInteger[3];
            e[k] = 1;
            // Ровно по габариту, без запаса: пол и потолок координат и так
            // охватывают все вершины. Запас в шаг сетки давал у бруска-резца
            // плоскости в 10 нм от его граней, и они резали деталь вдоль лишних
            // линий; без запаса плоскости клетки совпадают с гранями бруска.
            var min = lo[k]!.Value;
            var max = hi[k]!.Value;

            // x ≤ max  и  x ≥ min: внутренность — где значение не больше нуля.
            node = new Node(R, R.Plane(e[0], e[1], e[2], -max), node);
            node = new Node(R, R.Plane(-e[0], -e[1], -e[2], min), node);
        }

        return node;
    }

    /// <summary>
    /// Разрез многоугольника плоскостью Q.
    ///
    /// Совпадающая плоскость распознаётся сравнением номеров, без арифметики.
    /// При настоящем разрезе переднему куску достаётся ограничивающая −Q,
    /// заднему — Q; остальные ограничивающие переходят к тому куску,
    /// с которым у их ребра есть общая часть.
    /// </summary>
    [ThreadStatic] private static int[]? _signsStore;

    private static int[] _signs
    {
        get => _signsStore ??= new int[16];
        set => _signsStore = value;
    }

    internal static void Split(PlaneRegistry R, ExactPolygon p, OPlane Q,
                               List<ExactPolygon> coFront, List<ExactPolygon> coBack,
                               List<ExactPolygon> front, List<ExactPolygon> back)
    {
        Splits++;
        if (p.Support.Id == Q.Id)
        {
            (p.Support.Side == Q.Side ? coFront : coBack).Add(p);
            return;
        }

        var V = p.Vertices(R);
        int n = V.Length;

        // БЫСТРЫЙ ОТСЕВ ПО ГАБАРИТУ. Если весь габарит многоугольника с запасом лежит по одну
        // сторону плоскости, перебирать вершины точным предикатом незачем.
        if (!p.BoxReady)
        {
            var lo = new OpenTK.Mathematics.Vector3d(double.MaxValue);
            var hi = new OpenTK.Mathematics.Vector3d(double.MinValue);
            for (int i = 0; i < n; i++)
            {
                var q = R.Approx(V[i]);
                lo = OpenTK.Mathematics.Vector3d.ComponentMin(lo, q);
                hi = OpenTK.Mathematics.Vector3d.ComponentMax(hi, q);
            }
            p.BoxLo = lo; p.BoxHi = hi; p.BoxReady = true;
        }

        int quick = R.SideOfBox(p.BoxLo, p.BoxHi, Q);
        if(PlaneRegistry.VerifyEveryAnswer && quick!=0)
            foreach(int v in V)if(R.Side(v,Q)!=quick)
                throw new InvalidOperationException("box predicate disagrees with exact vertex classification");
        if (quick != 0) BoxHits++; else BoxMiss++;
        if (quick > 0) { front.Add(p); return; }
        if (quick < 0) { back.Add(p); return; }

        // ЗНАКИ — В ПЕРЕИСПОЛЬЗУЕМЫЙ БУФЕР. Массив создавался на каждый вызов, а вызовов за
        // одно скругление восемь миллионов: столько же мелких выделений, и почти все впустую —
        // многоугольник чаще всего целиком по одну сторону.
        if (_signs.Length < n) _signs = new int[Math.Max(n, _signs.Length * 2)];
        int[] t = _signs;
        bool anyPos = false, anyNeg = false;

        for (int i = 0; i < n; i++)
        {
            t[i] = R.Side(V[i], Q);
            if (t[i] > 0) anyPos = true;
            else if (t[i] < 0) anyNeg = true;
        }

        if (!anyNeg)
        {
            // Все вершины на плоскости при несовпадающей опорной — невозможно
            // для невырожденного многоугольника; попадает в «спереди».
            (anyPos ? front : coFront).Add(p);
            return;
        }

        if (!anyPos) { back.Add(p); return; }

        Cuts++;
        var fb = new List<OPlane>(n + 1);
        var bb = new List<OPlane>(n + 1);
        OPlane qf = Q.Flipped, qb = Q;

        for (int i = 0; i < n; i++)
        {
            int a = t[i], b = t[(i + 1) % n];

            // Bounds[i] несёт ребро от вершины i к вершине i+1.
            if (a > 0 || b > 0)
            {
                fb.Add(p.Bounds[i]);
                if (b <= 0) fb.Add(qf);
            }

            if (a < 0 || b < 0)
            {
                bb.Add(p.Bounds[i]);
                if (b >= 0) bb.Add(qb);
            }
        }

        front.Add(new ExactPolygon(p.Support, fb.ToArray(), p.Tag));
        back.Add(new ExactPolygon(p.Support, bb.ToArray(), p.Tag));
    }

    private sealed class Node
    {
        private readonly PlaneRegistry _r;
        private OPlane? _plane;
        private Node? _front, _back;
        private List<ExactPolygon> _polys = new();

        public Node(PlaneRegistry r, IReadOnlyList<ExactPolygon>? polys = null)
        {
            _r = r;
            if (polys != null && polys.Count > 0) Build(polys);
        }

        /// <summary>Узел клетки: только плоскость, внутри — дальнейшее дерево, снаружи — пусто.</summary>
        public Node(PlaneRegistry r, OPlane plane, Node back)
        {
            _r = r;
            _plane = plane;
            _back = back;
            _cell = true;
        }

        /// <summary>
        /// Узел-клетка: плоскость есть, многоугольников нет. Область такого узла не описывается
        /// габаритом его многоугольников, поэтому отсечение по габаритам к такому дереву
        /// неприменимо — иначе далёкий многоугольник, лежащий внутри полупространства, остаётся
        /// нетронутым, и фаска перестаёт строиться.
        /// </summary>
        private bool _cell;

        /// <summary>Список из запаса: ёмкость выдаётся с запасом, содержимое очищается.</summary>
        private static List<ExactPolygon> Rent(Stack<List<ExactPolygon>> pool, int capacity)
        {
            if (pool.Count == 0) return new List<ExactPolygon>(Math.Max(8, capacity));
            var l = pool.Pop();
            l.Clear();
            return l;
        }

        private static void Return(Stack<List<ExactPolygon>> pool, List<ExactPolygon> list)
        {
            // Слишком большие списки в запас не кладём: они держали бы память узла целиком.
            if (list.Capacity <= 4096 && pool.Count < 256) pool.Push(list);
        }

        public void Build(IReadOnlyList<ExactPolygon> polys0)
        {
            // Вход только читается; стеку обхода нужен List — корневой берётся как есть или копируется один раз.
            var polys = polys0 as List<ExactPolygon> ?? new List<ExactPolygon>(polys0);
            // Обход без рекурсии по глубине дерева: у большой детали
            // дерево бывает глубиной в тысячи уровней, и стек потока
            // этого не выдерживает.
            var work = new Stack<(Node node, List<ExactPolygon> list, int depth)>();
            var pool = new Stack<List<ExactPolygon>>();
            work.Push((this, polys, 0));

            while (work.Count > 0)
            {
                var (node, list, depth) = work.Pop();
                if (list.Count == 0) continue;
                if (depth > MaxDepth) MaxDepth = depth;

                node._plane ??= ChooseSplitter(node._r, list);

                // СПИСКИ ПЕРЕИСПОЛЬЗУЮТСЯ. На каждый узел создавалось два новых списка ёмкостью
                // во весь входной список, а узлов на одно скругление — 17 221. Отработавший
                // список возвращается в запас и выдаётся следующему узлу.
                var f = Rent(pool, list.Count);
                var b = Rent(pool, list.Count);

                foreach (var p in list)
                    Split(node._r, p, node._plane.Value, node._polys, node._polys, f, b);
                BuildNodes++;

                // Корневой список принадлежит вызывающему — его в запас не забираем.
                if (depth > 0) Return(pool, list);

                if (f.Count > 0) { node._front ??= new Node(node._r); work.Push((node._front, f, depth + 1)); }
                else Return(pool, f);
                if (b.Count > 0) { node._back ??= new Node(node._r); work.Push((node._back, b, depth + 1)); }
                else Return(pool, b);
            }
        }

        /// <summary>
        /// Секущая плоскость узла — та, что из нескольких кандидатов разрезает
        /// меньше всего многоугольников. Прежде брался первый в списке, и дерево
        /// из граней тора резало само себя и чужие грани без нужды.
        /// </summary>
        private static OPlane ChooseSplitter(PlaneRegistry r, List<ExactPolygon> list)
        {
            if (list.Count <= 3) return list[0].Support;

            // Кандидатов больше на длинных списках, и берутся они ВРАЗБРОС: многоугольники в
            // списке лежат в порядке построения, то есть соседние — это соседние грани, и
            // подряд идущие кандидаты почти одинаковы. При глубине дерева в 333 уровня обход
            // стоит дороже любой арифметики.
            int candidates = list.Count > 200 ? 24 : 8;
            const int Sample = 48;
            int stride = Math.Max(1, list.Count / candidates);
            int skew = Math.Max(1, list.Count / 7 | 1);        // взаимно простой шаг вразброс
            int sampleStride = Math.Max(1, list.Count / Sample);
            OPlane best = list[0].Support;
            int bestSplits = int.MaxValue;   // здесь — лучшая ОЦЕНКА (разрезы и перекос)

            // ПРОБОВАЛ и ОТКАТИЛ: добавлять кандидатами плоскости, выровненные по осям, через
            // середину габарита. У кольцевого резца все опорные плоскости касательные и почти
            // всё оставляют по одну сторону, поэтому дерево глубиной в 330 уровней. Но чужая
            // плоскость режет ВСЕ пересекающие её многоугольники, их число взрывается, и сцена
            // перестаёт считаться вовсе. Секущая плоскость обязана быть опорной.
            // ПОСТАВКА 3: ВЫБОРКА УЗЛА — ОДИН РАЗ. Прежде для каждой пары «кандидат — образец» заново проверялись
            // готовность и конечность габарита, а коэффициенты плоскости брались из реестра внутри SideOfBox. Теперь
            // образцы (индексы, опорные, габариты, конечность) собираются в массивы один раз на узел, коэффициенты
            // кандидата — один раз; проверка — SideOfBoxCore, та же арифметика, что в SideOfBox: выбор тот же побитово.
            // Образцов не больше сотни (шаг выборки — список / 48): массивы — на стеке, без выделений на каждый узел.
            int ns = (list.Count + sampleStride - 1) / sampleStride;
            Span<int> sIx = stackalloc int[ns];
            Span<int> sId = stackalloc int[ns];
            Span<OpenTK.Mathematics.Vector3d> sLo = stackalloc OpenTK.Mathematics.Vector3d[ns];
            Span<OpenTK.Mathematics.Vector3d> sHi = stackalloc OpenTK.Mathematics.Vector3d[ns];
            Span<bool> sFin = stackalloc bool[ns];
            {
                int t = 0;
                for (int si = 0; si < list.Count; si += sampleStride, t++)
                {
                    var p = list[si];
                    if (!p.BoxReady)
                    {
                        var lo2 = new OpenTK.Mathematics.Vector3d(double.MaxValue);
                        var hi2 = new OpenTK.Mathematics.Vector3d(double.MinValue);
                        foreach (int v in p.Vertices(r))
                        {
                            var qq = r.Approx(v);
                            lo2 = OpenTK.Mathematics.Vector3d.ComponentMin(lo2, qq);
                            hi2 = OpenTK.Mathematics.Vector3d.ComponentMax(hi2, qq);
                        }
                        p.BoxLo = lo2; p.BoxHi = hi2; p.BoxReady = true;
                    }
                    sIx[t] = si; sId[t] = p.Support.Id; sLo[t] = p.BoxLo; sHi[t] = p.BoxHi;
                    sFin[t] = double.IsFinite(p.BoxLo.X) && double.IsFinite(p.BoxLo.Y) && double.IsFinite(p.BoxLo.Z) &&
                              double.IsFinite(p.BoxHi.X) && double.IsFinite(p.BoxHi.Y) && double.IsFinite(p.BoxHi.Z);
                }
            }

            for (int k = 0; k < candidates; k++)
            {
                OPlane q = list[(int)(((long)k * skew) % list.Count)].Support;
                var (qa, qb, qc, qd) = r.PlaneDoubles(q);
                int splits = 0;

                // Разрезы считаются на выборке: у большой детали перебор всех
                // многоугольников для каждого кандидата в каждом узле стоил дороже,
                // чем экономил.
                // Вместе с разрезами считается ПЕРЕКОС: сколько многоугольников уходит вперёд,
                // сколько назад. Прежде учитывались только разрезы, и дерево выходило
                // перекошенным — на трёх тысячах многоугольников путь до листа доходил до двух
                // сотен уровней вместо десятка, и обход стоил дороже самой работы.
                int fwd = 0, bwd = 0;
                for (int t = 0; t < ns; t++)
                {
                    if (sId[t] == q.Id) continue;
                    // СНАЧАЛА ГАБАРИТ (негодное приближение — «не знаю», как в SideOfBox).
                    int side = sFin[t] ? PlaneRegistry.SideOfBoxCore(sLo[t], sHi[t], qa, qb, qc, qd, q.Side) : 0;
                    if (side > 0) { fwd++; continue; }
                    if (side < 0) { bwd++; continue; }

                    bool pos = false, neg = false;
                    foreach (int v in list[sIx[t]].Vertices(r))
                    {
                        int sd = r.Side(v, q);
                        if (sd > 0) pos = true; else if (sd < 0) neg = true;
                        if (pos && neg) break;
                    }
                    if (pos && neg) splits++;
                    else if (pos) fwd++;
                    else bwd++;
                }
                // ВЕС ПЕРЕКОСА ИЗМЕРЕН. Казалось, что виновата глубина дерева (329 уровней при
                // 17 221 узле), но усиление веса перекоса её уменьшает ценой числа узлов, и
                // время растёт: вес 1 — глубина 329, узлов 17 221, 5 629 мс; вес 4 — 147, 23 420,
                // 6 159 мс; вес 16 — 142, 26 651, 6 732 мс. Глубина сама по себе не узкое место.
                int score = splits * 4 + Math.Abs(fwd - bwd);

                if (score < bestSplits) { bestSplits = score; best = q; if (score == 0) break; }
            }

            return best;
        }

        /// <summary>
        /// Отсечение многоугольников деревом: остаётся то, что снаружи тела.
        ///
        /// ПЕРЕПИСАНО НА ОБХОД СТЕКОМ. Рекурсивный вариант складывал результат каждого узла в
        /// свой список и копировал его вверх через все уровни: при дереве в тысячи узлов на
        /// сами посещения уходило около секунды из двух. Теперь выжившие складываются сразу в
        /// один список, а спуск идёт по стеку.
        /// </summary>
        private sealed class Lazy
        {
            public OPlane? Plane; public List<ExactPolygon>? Pending; public Lazy? Front, Back; public bool Built;
        }

        /// <summary>
        /// 21.10 (скорость): ЧАСТИ МНОГОУГОЛЬНИКОВ query ВНУТРИ ТЕЛА A — по дереву A, которое строится лишь там, куда спускаются
        /// запросы (те же секущие, тот же порядок разбиения, что у Build: ответ тот же, что у Node(A).Invert().ClipPolygons).
        /// Крышки локальной булевой (шесть граней коробки) прежде строили всё дерево тела: тор ∪ брусок — 0.5 с.
        /// </summary>
        public static List<ExactPolygon> InsideLazy(PlaneRegistry r, IReadOnlyList<ExactPolygon> A, IReadOnlyList<ExactPolygon> query)
        {
            var outp = new List<ExactPolygon>();
            if (A.Count == 0) return outp;
            var root = new Lazy { Pending = new List<ExactPolygon>(A) };
            var dump = new List<ExactPolygon>();
            void Expand(Lazy n)
            {
                if (n.Built) return;
                var list = n.Pending!;
                n.Plane = ChooseSplitter(r, list);
                var f = new List<ExactPolygon>(); var b = new List<ExactPolygon>();
                foreach (var p in list) Split(r, p, n.Plane.Value, dump, dump, f, b);
                dump.Clear();
                BuildNodes++;
                if (f.Count > 0) n.Front = new Lazy { Pending = f };
                if (b.Count > 0) n.Back = new Lazy { Pending = b };
                n.Pending = null; n.Built = true;
            }
            var work = new Stack<(Lazy Node, List<ExactPolygon> List)>();
            work.Push((root, new List<ExactPolygon>(query)));
            while (work.Count > 0)
            {
                var (node, list) = work.Pop();
                if (list.Count == 0) continue;
                Expand(node);
                var f = new List<ExactPolygon>(list.Count);
                var b = new List<ExactPolygon>(list.Count);
                foreach (var p in list) Split(r, p, node.Plane!.Value, f, b, f, b);
                ClipNodes++;
                if (f.Count > 0 && node.Front != null) work.Push((node.Front, f));       // спереди без ветви — снаружи A
                if (b.Count > 0) { if (node.Back != null) work.Push((node.Back, b)); else outp.AddRange(b); }   // сзади без ветви — внутри
            }
            return outp;
        }

        public List<ExactPolygon> ClipPolygons(IReadOnlyList<ExactPolygon> polys0)
        {
            var polys = polys0 as List<ExactPolygon> ?? new List<ExactPolygon>(polys0);
            if (polys.Count == 0) return polys;
            if (_plane == null) return new List<ExactPolygon>(polys);

            var outp = new List<ExactPolygon>(polys.Count);
            var work = new Stack<(Node Node, List<ExactPolygon> List)>();
            work.Push((this, polys));

            while (work.Count > 0)
            {
                var (node, list) = work.Pop();
                if (list.Count == 0) continue;

                if (node._plane == null) { outp.AddRange(list); continue; }

                var f = new List<ExactPolygon>(list.Count);
                var b = new List<ExactPolygon>(list.Count);
                foreach (var p in list) Split(node._r, p, node._plane.Value, f, b, f, b);
                ClipNodes++;

                // Спереди от плоскости — снаружи тела, если дальше ветви нет.
                if (f.Count > 0) { if (node._front != null) work.Push((node._front, f)); else outp.AddRange(f); }
                // Сзади и без ветви — внутри тела: такие многоугольники убираются.
                if (b.Count > 0 && node._back != null) work.Push((node._back, b));
            }

            return outp;
        }

        public void ClipTo(Node other)
        {
            foreach (Node n in Nodes()) n._polys = other.ClipPolygons(n._polys);
        }

        /// <summary>
        /// НЕ ПРИМЕНЯЕТСЯ. Отсечение по габаритам (не резать деревом то, что не достаёт до
        /// другого тела) ломало построение фаски — и с габаритом дерева, и с габаритом
        /// исходного набора многоугольников. Причина не установлена; подозрение на вершины с
        /// нулевым весом, для которых приближённые координаты бессмысленны. Оставлено как
        /// заготовка: сначала нужно понять, откуда берётся расхождение.
        /// </summary>
        public void ClipTo(Node other, Vector3d lo, Vector3d hi)
        {
            foreach (Node n in Nodes())
            {
                if (n._polys.Count == 0) continue;
                var near = new List<ExactPolygon>(n._polys.Count);
                List<ExactPolygon>? far = null;
                foreach (var p in n._polys)
                {
                    var (plo, phi) = Extent(_r, p);
                    if (phi.X < lo.X || plo.X > hi.X || phi.Y < lo.Y || plo.Y > hi.Y || phi.Z < lo.Z || plo.Z > hi.Z)
                        (far ??= new List<ExactPolygon>()).Add(p);
                    else near.Add(p);
                }
                if (far == null) { n._polys = other.ClipPolygons(n._polys); continue; }
                var kept = other.ClipPolygons(near);
                kept.AddRange(far);
                n._polys = kept;
            }
        }

        /// <summary>Габарит набора многоугольников (приближённо, с запасом).</summary>
        public static (Vector3d Lo, Vector3d Hi) ExtentOf(PlaneRegistry r, IReadOnlyList<ExactPolygon> polys)
        {
            Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
            foreach (var p in polys)
            {
                var (a, b) = Extent(r, p);
                lo = Vector3d.ComponentMin(lo, a);
                hi = Vector3d.ComponentMax(hi, b);
            }
            if (lo.X > hi.X) return (new Vector3d(double.MinValue), new Vector3d(double.MaxValue));
            var pad = new Vector3d(0.01, 0.01, 0.01);
            return (lo - pad, hi + pad);
        }

        private bool HasCell()
        {
            foreach (Node n in Nodes()) if (n._cell) return true;
            return false;
        }

        private (Vector3d Lo, Vector3d Hi)? _extent;

        /// <summary>Габарит поддерева (приближённо).</summary>
        public (Vector3d Lo, Vector3d Hi) Extent()
        {
            if (_extent != null) return _extent.Value;
            Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
            foreach (Node n in Nodes())
                foreach (var p in n._polys)
                {
                    var (a, b) = Extent(_r, p);
                    lo = Vector3d.ComponentMin(lo, a);
                    hi = Vector3d.ComponentMax(hi, b);
                }
            _extent = (lo, hi);
            return _extent.Value;
        }

        private static (Vector3d Lo, Vector3d Hi) Extent(PlaneRegistry r, ExactPolygon p)
        {
            Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
            foreach (int v in p.Vertices(r))
            {
                Vector3d q = r.Approx(v);
                if (!double.IsFinite(q.X) || !double.IsFinite(q.Y) || !double.IsFinite(q.Z) || q.Length > 1e9)
                    InfiniteVertices++;
                lo = Vector3d.ComponentMin(lo, q);
                hi = Vector3d.ComponentMax(hi, q);
            }
            return (lo, hi);
        }

        public void Invert()
        {
            foreach (Node n in Nodes())
            {
                for (int i = 0; i < n._polys.Count; i++) n._polys[i] = n._polys[i].Flipped();
                if (n._plane != null) n._plane = n._plane.Value.Flipped;
                (n._front, n._back) = (n._back, n._front);
            }
        }

        public List<ExactPolygon> All()
        {
            var all = new List<ExactPolygon>();
            foreach (Node n in Nodes()) all.AddRange(n._polys);
            return all;
        }

        /// <summary>Все узлы дерева без рекурсии.</summary>
        private IEnumerable<Node> Nodes()
        {
            var stack = new Stack<Node>();
            stack.Push(this);

            while (stack.Count > 0)
            {
                Node n = stack.Pop();
                yield return n;
                if (n._front != null) stack.Push(n._front);
                if (n._back != null) stack.Push(n._back);
            }
        }
    }
}
