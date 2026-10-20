using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// СЕТКА ГРАНИ ПО ТОЧНОЙ ГЕОМЕТРИИ (этап 3).
///
/// Треугольники граней до сих пор шли прямо из разбиения пространства — хорды резца. На
/// касании скругления с гранью ошибка хорды даёт зубцы до полумиллиметра, точное ребро
/// уходит под поверхность и видно кусками (снимки пользователя 22.09). Подгонка готовых
/// треугольников к точной геометрии зубцы не убирает: они в самой форме треугольников.
///
/// Здесь сетка грани строится заново:
///   1. контуры грани — упорядоченные циклы её рёбер;
///   2. точки ребра — по его точной кривой, один раз на ребро, общие для обеих граней;
///   3. контуры переводятся в параметры (u, v) точной поверхности; у поверхности, замкнутой
///      по u (кольцо), контур, обходящий период, разрезается по шву — выходит полоса;
///   4. многоугольник с отверстиями триангулируется в (u, v);
///   5. внутренние рёбра треугольников дробятся, пока середина на поверхности отходит от
///      середины хорды больше допуска гранения; рёбра на границе не дробятся.
/// </summary>
public static partial class ExactFaceMesher
{
    /// <summary>ДИАГНОСТИКА: неудачные попытки триангуляции.</summary>
    public static Action<string>? TryDebug;

    /// <summary>ДИАГНОСТИКА: разбор контуров последней несвязной грани.</summary>
    // ПОТОКОВЫЕ: построение сетки идёт в несколько потоков (в тестах — несколько граней
    // одновременно), и общие статические поля путались между собой: тест радиуса кромки падал
    // через раз только в полном наборе.
    [ThreadStatic] public static string? LastGrouping;
    internal static void ReleaseThreadState() { LastGrouping = null; TwinOf = null; }

    /// <summary>Порог уточнения последней грани (диагностика).</summary>
    [ThreadStatic] public static double LastTol;

    /// <summary>Номер способа триангуляции, давшего последний результат.</summary>
    [ThreadStatic] public static int LastCandidate;

    /// <summary>ДИАГНОСТИКА: куски грани при группировке контуров.</summary>
    public static Action<string>? GroupDebug;

    /// <summary>ДИАГНОСТИКА: работа уточнения на последней грани.</summary>
    public static int RefinePasses, RefineBad, RefineAdded;
    /// <summary>KRN-10: сколько раз уточнение мерило отход треугольника (проверка «годный не перепроверяется»).</summary>
    public static long RefineEvals;

    /// <summary>ДИАГНОСТИКА: отклонённые вставки уточнения.</summary>
    public static long SkipRecheck, SkipMicro, SkipLost, SkipTiny;

    /// <summary>ДИАГНОСТИКА: работа делителя хорд.</summary>
    public static long ChordSeen, ChordNoPos, ChordHavePos, ChordSplit, ChordStep, ChordOtherLoop, ChordAdj;

    /// <summary>ДИАГНОСТИКА: концы лишней стороны в выгруженной грани.</summary>
    public static (int, int)? StrayDump;

    /// <summary>ДИАГНОСТИКА: путь для выгрузки грани в OBJ.</summary>
    public static string? ObjDumpPath;

    /// <summary>ДИАГНОСТИКА: путь для выгрузки развёртки отказавшей грани.</summary>
    public static string? UVDumpPath;

    /// <summary>ДИАГНОСТИКА: выгрузка точек и треугольников мелкой грани.</summary>
    public static Func<List<Vector3d>, List<int>, int, string>? UVDump;

    /// <summary>ДИАГНОСТИКА: невставленные точки контура.</summary>
    public static Action<string>? MissDebug;

    /// <summary>ДИАГНОСТИКА: перестройка ушей.</summary>
    public static Action<string>? RepairDebug;

    /// <summary>
    /// Вставка пропущенных точек контура: точка, лежащая на стороне треугольника, делит его
    /// надвое. Возвращает, сколько точек вставить не удалось.
    /// </summary>
    private static int InsertUnusedBoundary(List<Vector3d> P, List<int> T)
    {
        var used = new bool[P.Count];
        foreach (int idx in T) if (idx < used.Length) used[idx] = true;

        // ПРОБОВАЛ и ОТКАТИЛ: делить и сторону, внутри которой лежит точка контура, даже
        // если сама точка уже используется. Охват падал с 89 до 88 тел, а построение сетки
        // дорожало с 70 до 88 секунд. Т-образный стык тут лечится не так.

        // ПРОБОВАЛ делить сторону, перескочившую через точку контура (между граничными
        // точками 50 и 52 существует 51). Охват падал с 89 до 84: номера точек не отражают
        // порядок обхода, когда у грани несколько контуров, и деление задевало верные
        // треугольники. Нужен признак соседства по контуру, а не по номеру.

        int left = 0;
        for (int v = 0; v < Math.Min(BoundaryCount, P.Count); v++)
        {
            if (used[v]) continue;
            bool done = false;
            for (int i = 0; i + 2 < T.Count && !done; i += 3)
                for (int k = 0; k < 3 && !done; k++)
                {
                    int a = T[i + k], b = T[i + (k + 1) % 3], c = T[i + (k + 2) % 3];
                    Vector3d d = P[b] - P[a];
                    double l2 = d.LengthSquared;
                    if (l2 < 1e-18) continue;
                    double t = Vector3d.Dot(P[v] - P[a], d) / l2;
                    if (t <= 1e-9 || t >= 1 - 1e-9) continue;
                    // ДОПУСК ВСТАВКИ СЧИТАЕТСЯ ОТ ПРОГИБА, А НЕ ОТ НУЛЯ. Треугольник — хорда
                    // кривой поверхности, а точка контура лежит на самой поверхности: измерено,
                    // что она отстоит от плоскости треугольника на 0.00191 мм при допуске 1e-6,
                    // и потому не вставлялась — одна точка из 159 оставалась вне сетки, давая
                    // непарную сторону.
                    if ((P[a] + d * t - P[v]).Length > InsertTol) continue;
                    T[i] = a; T[i + 1] = v; T[i + 2] = c;
                    T.Add(v); T.Add(b); T.Add(c);
                    used[v] = true; done = true;
                }
            // Точка может лежать и ВНУТРИ треугольника — тогда он делится на три.
            if (!done)
                for (int i = 0; i + 2 < T.Count && !done; i += 3)
                {
                    Vector3d a = P[T[i]], b = P[T[i + 1]], c = P[T[i + 2]];
                    Vector3d n = Vector3d.Cross(b - a, c - a);
                    if (n.LengthSquared < 1e-24) continue;
                    n = n.Normalized();
                    if (Math.Abs(Vector3d.Dot(P[v] - a, n)) > InsertTol) continue;
                    if (Vector3d.Dot(Vector3d.Cross(b - a, P[v] - a), n) < -1e-9) continue;
                    if (Vector3d.Dot(Vector3d.Cross(c - b, P[v] - b), n) < -1e-9) continue;
                    if (Vector3d.Dot(Vector3d.Cross(a - c, P[v] - c), n) < -1e-9) continue;
                    int ia = T[i], ib = T[i + 1], ic = T[i + 2];
                    T[i] = ia; T[i + 1] = ib; T[i + 2] = v;
                    T.Add(ib); T.Add(ic); T.Add(v);
                    T.Add(ic); T.Add(ia); T.Add(v);
                    used[v] = true; done = true;
                }

            if (!done)
            {
                // Насколько точка вообще далека от сетки: если далеко — она вне накрытой области.
                double near = double.MaxValue;
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    Vector3d a = P[T[i]], b = P[T[i + 1]], c = P[T[i + 2]];
                    near = Math.Min(near, Math.Min((P[v] - a).Length, Math.Min((P[v] - b).Length, (P[v] - c).Length)));
                }
                // Расстояние до ближайшего ТРЕУГОЛЬНИКА, а не вершины: если и оно велико, в
                // этом месте сетки просто нет — значит, дело не во вставке точки, а в том, что
                // способ триангуляции не накрыл часть грани.
                double nearTri = double.MaxValue;
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    Vector3d a = P[T[i]], b = P[T[i + 1]], c = P[T[i + 2]];
                    Vector3d nrm = Vector3d.Cross(b - a, c - a);
                    if (nrm.LengthSquared < 1e-24) continue;
                    nrm = nrm.Normalized();
                    Vector3d proj = P[v] - nrm * Vector3d.Dot(P[v] - a, nrm);
                    bool inside = Vector3d.Dot(Vector3d.Cross(b - a, proj - a), nrm) >= -1e-9
                               && Vector3d.Dot(Vector3d.Cross(c - b, proj - b), nrm) >= -1e-9
                               && Vector3d.Dot(Vector3d.Cross(a - c, proj - c), nrm) >= -1e-9;
                    if (inside) nearTri = Math.Min(nearTri, Math.Abs(Vector3d.Dot(P[v] - a, nrm)));
                }

                // Есть ли ДРУГАЯ точка набора в том же месте: тогда это просто дубликат.
                int twin = -1;
                for (int i = 0; i < P.Count; i++)
                    if (i != v && (P[i] - P[v]).Length < 1e-6) { twin = i; break; }
                MissDebug?.Invoke($"точка контура {v} из {BoundaryCount} не вставлена: ближайшая вершина сетки в {near:0.#####} мм; " +
                                  $"до ближайшего треугольника {(nearTri == double.MaxValue ? "сетки нет над точкой" : nearTri.ToString("0.#####") + " мм")}; " +
                                  $"треугольников {T.Count / 3}, точек всего {P.Count}; " +
                                  (BoundaryCount <= 12 && UVDump != null ? UVDump(P, T, BoundaryCount) : "") + $"двойник {(twin >= 0 ? twin.ToString() + (used[twin] ? " (используется)" : " (тоже нет)") : "нет")}");
                left++;
            }
        }
        return left;
    }

    /// <summary>ДИАГНОСТИКА: на каком шаге появляется сторона вне контура.</summary>
    public static Action<string>? StrayDebug;

    /// <summary>Непарные стороны, не являющиеся звеньями контуров.</summary>
    private static int StrayCount(List<Vector3d> P, List<int> T, List<Vector3d> outer3, List<List<Vector3d>> holes3)
    {
        var linkSet = new HashSet<(double, double, double, double, double, double)>();
        void AddLink(Vector3d a, Vector3d b)
        {
            linkSet.Add((Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6),
                         Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6)));
            linkSet.Add((Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6),
                         Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6)));
        }
        for (int i = 0; i < outer3.Count; i++) AddLink(outer3[i], outer3[(i + 1) % outer3.Count]);
        foreach (var h in holes3)
            for (int i = 0; i < h.Count; i++) AddLink(h[i], h[(i + 1) % h.Count]);

        // Двойники вершин у шва считаются своей исходной вершиной: иначе починенный шов даёт
        // мнимые непарные стороны (у трубы сторона выглядела непарной, хотя по координатам
        // проходилась 4 раза вперёд и 4 назад).
        var dirF = new Dictionary<(int, int), int>();
        for (int i = 0; i + 2 < T.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                var key = (Same(T[i + k]), Same(T[i + (k + 1) % 3]));
                dirF[key] = dirF.GetValueOrDefault(key) + 1;
            }
        int stray = 0;
        foreach (var ((x, y), n) in dirF)
            if (n != dirF.GetValueOrDefault((y, x)))
            {
                var key = (Math.Round(P[x].X, 6), Math.Round(P[x].Y, 6), Math.Round(P[x].Z, 6),
                           Math.Round(P[y].X, 6), Math.Round(P[y].Y, 6), Math.Round(P[y].Z, 6));
                if (!linkSet.Contains(key)) stray++;
            }
        return stray;
    }

    /// <summary>
    /// Деление сторон, перескочивших через точку контура. НЕ ВЫЗЫВАЕТСЯ: измерено, что
    /// срабатывает 152 раза на 100 телах, но ни охват (89), ни расклад причин не меняет —
    /// чинит не те стороны, что мешают замыканию, а время построения растёт на 3 секунды.
    /// Соседство здесь берётся ПО КОНТУРУ (пары соседних точек), а не по номерам: у грани из нескольких кусков номера порядка обхода не
    /// отражают, и деление по номерам задевало верные треугольники (охват падал с 89 до 84).
    /// </summary>
    private static void SplitSkippingSides(List<Vector3d> P, List<int> T, List<Vector3d> outer3, List<List<Vector3d>> holes3)
    {
        static (double, double, double) Key(Vector3d p) => (Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6));

        var links = new HashSet<((double, double, double), (double, double, double))>();
        var loopPts = new List<Vector3d>();
        void AddLoop(List<Vector3d> loop)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                links.Add((Key(loop[i]), Key(loop[(i + 1) % loop.Count])));
                links.Add((Key(loop[(i + 1) % loop.Count]), Key(loop[i])));
                loopPts.Add(loop[i]);
            }
        }
        AddLoop(outer3);
        foreach (var h in holes3) AddLoop(h);
        if (loopPts.Count == 0) return;

        var onLoop = new HashSet<(double, double, double)>(loopPts.Select(Key));

        for (int pass = 0; pass < 3; pass++)
        {
            bool split = false;
            for (int i = 0; i + 2 < T.Count && !split; i += 3)
                for (int k = 0; k < 3 && !split; k++)
                {
                    int a = T[i + k], b = T[i + (k + 1) % 3], c = T[i + (k + 2) % 3];
                    if (!onLoop.Contains(Key(P[a])) || !onLoop.Contains(Key(P[b]))) continue;
                    if (links.Contains((Key(P[a]), Key(P[b])))) continue;     // это звено контура

                    Vector3d d = P[b] - P[a];
                    double l2 = d.LengthSquared;
                    if (l2 < 1e-18) continue;

                    // Точки контура, лежащие строго внутри стороны.
                    var mids = new List<int>();
                    for (int m = 0; m < P.Count; m++)
                    {
                        if (m == a || m == b || !onLoop.Contains(Key(P[m]))) continue;
                        double t = Vector3d.Dot(P[m] - P[a], d) / l2;
                        if (t <= 1e-9 || t >= 1 - 1e-9) continue;
                        if ((P[a] + d * t - P[m]).Length > 1e-6) continue;
                        mids.Add(m);
                    }
                    if (mids.Count == 0) continue;
                    mids.Sort((p1, p2) => Vector3d.Dot(P[p1] - P[a], d).CompareTo(Vector3d.Dot(P[p2] - P[a], d)));

                    T.RemoveAt(i + 2); T.RemoveAt(i + 1); T.RemoveAt(i);
                    int prev = a;
                    foreach (int m in mids) { T.Add(prev); T.Add(m); T.Add(c); prev = m; }
                    T.Add(prev); T.Add(b); T.Add(c);
                    split = true;
                    SplitCount++;
                }
            if (!split) break;
        }
    }

    /// <summary>ДИАГНОСТИКА: сколько раз поделена перескочившая сторона.</summary>
    public static long SplitCount;

    /// <summary>Непарные стороны в наборе треугольников.</summary>
    private static int UnmatchedSides(List<int> T)
    {
        var dirS = new Dictionary<(int, int), int>();
        for (int i = 0; i + 2 < T.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                var key = (T[i + k], T[i + (k + 1) % 3]);
                dirS[key] = dirS.GetValueOrDefault(key) + 1;
            }
        int bad = 0;
        foreach (var ((x, y), n) in dirS) if (n != dirS.GetValueOrDefault((y, x))) bad++;
        return bad;
    }

    /// <summary>
    /// Перестройка вырожденных треугольников: ухо сливается с соседом по длинной стороне и
    /// четырёхугольник делится по другой диагонали.
    /// </summary>
    private static void RepairSlivers(List<Vector3d> P, List<int> T, List<Vector2d> UV)
    {
        double total = 0;
        for (int i = 0; i + 2 < T.Count; i += 3)
            total += Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]).Length / 2;
        if (total <= 0) return;
        double tiny = 1e-9 * total;

        for (int pass = 0; pass < 4; pass++)
        {
            var side = new Dictionary<(int, int), int>();
            for (int i = 0; i + 2 < T.Count; i += 3)
                for (int k = 0; k < 3; k++) side[(T[i + k], T[i + (k + 1) % 3])] = i;

            bool changed = false;
            for (int i = 0; i + 2 < T.Count && !changed; i += 3)
            {
                Vector3d a = P[T[i]], b = P[T[i + 1]], c = P[T[i + 2]];
                if (Vector3d.Cross(b - a, c - a).Length / 2 > tiny) continue;

                // Сосед ищется у ЛЮБОЙ из трёх сторон: у вырожденного уха сосед обычно один,
                // и нередко не у самой длинной стороны.
                for (int k0 = 0; k0 < 3 && !changed; k0++)
                {
                    int u = T[i + k0], v = T[i + (k0 + 1) % 3], w = T[i + (k0 + 2) % 3];
                    if (!side.TryGetValue((v, u), out int j) || j == i) continue;

                    int d = -1;
                    for (int k = 0; k < 3; k++) if (T[j + k] != u && T[j + k] != v) d = T[j + k];
                    if (d < 0) continue;

                    // Четырёхугольник по кругу: w, u, d, v. Другая диагональ — w—d, значит
                    // новые треугольники (w, u, d) и (w, d, v). ПОРЯДОК ВАЖЕН: при (u, w, d)
                    // обход выворачивается, и связность рвётся (177 случаев).
                    Vector3d n1 = Vector3d.Cross(P[u] - P[w], P[d] - P[w]);
                    Vector3d n2 = Vector3d.Cross(P[d] - P[w], P[v] - P[w]);
                    double a1 = n1.Length / 2, a2 = n2.Length / 2;
                    if (a1 <= tiny || a2 <= tiny) continue;

                    // ВЫПУКЛОСТЬ ПРОВЕРЯЕТСЯ В РАЗВЁРТКЕ ГРАНИ. Проверка по знаку нормалей в
                    // пространстве пропускала невыпуклые четырёхугольники: новая диагональ
                    // выходила наружу, получалась складка — область накрыта дважды, один слой
                    // вывернут, площадь грани больше на удвоенную его долю.
                    if (UV.Count > Math.Max(Math.Max(u, v), Math.Max(w, d)))
                    {
                        double Cr(Vector2d p1, Vector2d p2, Vector2d p3) => (p2.X - p1.X) * (p3.Y - p1.Y) - (p2.Y - p1.Y) * (p3.X - p1.X);
                        // Четырёхугольник по кругу: w, u, d, v — все повороты одного знака.
                        double s1 = Cr(UV[w], UV[u], UV[d]), s2 = Cr(UV[u], UV[d], UV[v]);
                        double s3 = Cr(UV[d], UV[v], UV[w]), s4 = Cr(UV[v], UV[w], UV[u]);
                        bool convex = (s1 > 0 && s2 > 0 && s3 > 0 && s4 > 0) || (s1 < 0 && s2 < 0 && s3 < 0 && s4 < 0);
                        if (!convex) continue;
                    }

                    T[i] = w; T[i + 1] = u; T[i + 2] = d;
                    T[j] = w; T[j + 1] = d; T[j + 2] = v;
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    /// <summary>Сколько точек пришло с контуров (остальные вставлены уточнением).</summary>
    [ThreadStatic] private static int BoundaryCount;

    /// <summary>ДИАГНОСТИКА: стороны шва в готовой сетке грани.</summary>
    public static Action<string>? SeamDebug;

    /// <summary>ДИАГНОСТИКА: контур в параметрах до развёртки.</summary>
    public static Action<TFace, List<Vector2d>>? RawLoopDebug;

    /// <summary>ДИАГНОСТИКА: контур грани в (u, v).</summary>
    public static Action<TFace, List<TEdge>, List<Vector2d>, int>? LoopDebug;

    /// <summary>ДИАГНОСТИКА: развёртка грани в (u, v).</summary>
    public static Action<TFace, List<Vector2d>, List<int>, string>? DumpUV;

    /// <summary>
    /// Слияние вершин в сетке: вершины, соединённые рёбрами короче допуска, дают одну точку.
    /// Топологию и точное тело не трогает — только точки сетки, и одинаково у обеих соседних
    /// граней, поэтому щелей не появляется. Рёбра в микроны — след сетки точного ядра (1e-5):
    /// из-за них экспорт склеивал вершины и открывал стороны, а сетка грани отбраковывалась.
    /// </summary>
    /// <summary>
    /// ПРИВЯЗКА ТОЧЕК — подстановка, которой разметка сводит точки соседних граней к общим
    /// местам. Она МЕНЯЕТ РЕЗУЛЬТАТ, а не только скорость, и потому опаснее прочих общих полей:
    /// забытая включённой, она меняет сетку следующей грани; забытая выключенной — оставляет
    /// щели на стыках.
    ///
    /// Прежде её ставили и снимали вручную в восьми местах, и каждое снятие было отдельным
    /// присваиванием `null` — ровно та форма, где легко потерять одну ветку выхода.
    ///
    /// Задавать её следует через <see cref="SnapScope"/>: он возвращает прежнее значение сам,
    /// что бы ни случилось внутри.
    /// </summary>
    // K04: привязка принадлежит логическому потоку вычисления (AsyncLocal доходит и до задач
    // Parallel.For этого же вычисления), а не всему процессу: разметка двух тел одновременно
    // подменяла друг другу привязку.
    private static readonly AsyncLocal<Func<Vector3d, Vector3d>?> _snap = new();
    public static Func<Vector3d, Vector3d>? Snap { get => _snap.Value; set => _snap.Value = value; }

    /// <summary>Привязка на время работы, с возвратом прежней.</summary>
    public readonly struct SnapScope : IDisposable
    {
        private readonly Func<Vector3d, Vector3d>? _was;

        public SnapScope(Func<Vector3d, Vector3d>? snap)
        {
            _was = Snap;
            Snap = snap;
        }

        public void Dispose() => Snap = _was;
    }

    /// <summary>Точки ребра от V0 к V1 по точной кривой, со слиянием вершин (Snap).</summary>
    public static List<Vector3d> EdgePoints(TEdge e)
    {
        var raw = RawEdgePoints(e);
        if (Snap == null) return raw;
        // Точки ближе 5e-5 мм друг к другу экспорт склеит, а сетка грани из-за них
        // отбраковывается: гранение кривой иногда ставит соседние точки в микроны.
        const double micro = 2e-4;
        var snapped = new List<Vector3d>(raw.Count);
        foreach (var q in raw)
        {
            var t = Snap(q);
            if (snapped.Count == 0 || (t - snapped[^1]).Length > micro) snapped.Add(t);
        }
        // ТОЧКИ, ПОЧТИ ЛЕЖАЩИЕ НА ХОРДЕ СОСЕДЕЙ, УБИРАЮТСЯ. Они ничего не добавляют к форме
        // (отход меньше десятой доли допуска гранения), но дают треугольники в доли микрона
        // площадью: самопроверка отвергает такую грань, а в STL с одинарной точностью такая
        // щепка рвётся и открывает сторону. Отсев — в самом ребре, поэтому обе соседние грани
        // видят один и тот же контур.
        double flat = Core.Tol.Chord / 10;
        for (int pass = 0; pass < 4; pass++)
        {
            bool dropped = false;
            for (int i = 1; i + 1 < snapped.Count; i++)
            {
                Vector3d a = snapped[i - 1], b = snapped[i + 1], q = snapped[i];
                Vector3d d = b - a;
                double l2 = d.LengthSquared;
                if (l2 < 1e-18) continue;
                double t = Vector3d.Dot(q - a, d) / l2;
                if (t <= 0 || t >= 1) continue;
                if ((a + d * t - q).Length > flat) continue;
                snapped.RemoveAt(i); dropped = true; i--;
            }
            if (!dropped) break;
        }

        // Концы — точно слитые вершины; вырожденное ребро даёт одну точку.
        if (snapped.Count >= 2)
        {
            snapped[0] = Snap(e.V0.P);
            snapped[^1] = Snap(e.V1.P);
            // После пришивания концов соседняя точка может оказаться слишком близко.
            while (snapped.Count > 2 && (snapped[1] - snapped[0]).Length <= micro) snapped.RemoveAt(1);
            while (snapped.Count > 2 && (snapped[^1] - snapped[^2]).Length <= micro) snapped.RemoveAt(snapped.Count - 2);
            if ((snapped[0] - snapped[^1]).LengthSquared < 1e-18 && snapped.Count == 2) snapped.RemoveAt(1);
        }
        return snapped;
    }

    static readonly bool CuspOff = Environment.GetEnvironmentVariable("P3D_CUSPDENSE") == "0";

    /// <summary>
    /// KRN-06 (06.5): ВЕРШИНА-КЛЮВ — два ребра выходят из вершины в одну сторону, касаясь друг друга (край ленты катящегося
    /// шара на стенке касается окружности цилиндра в точке касания: у грани стенки угол 0°). Зазор между кривыми растёт как
    /// квадрат расстояния до вершины, а прогиб хорд — как квадрат длины звена: при обычном гранении ломаные у клюва
    /// пересекаются, и сетка грани не строится. Звенья ребра у такой вершины дробятся к ней в геометрической прогрессии
    /// (половинами параметра до 1e-6 мм): прогиб становится меньше зазора на всех масштабах. Ребро одно для обеих граней —
    /// стык остаётся общим. Мутация P3D_CUSPDENSE=0.
    /// </summary>
    static List<Vector3d> DensifyAtCusps(TEdge e, List<Vector3d> pts)
    {
        if (pts.Count < 2) return pts;
        var c = e.Curve;
        bool fwd = (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared;
        Vector3d Leave(TEdge x, TVertex v, out double tEnd, out double tIn)
        {
            var cx = x.Curve; bool atStart = (cx.PointAt(cx.T0) - v.P).LengthSquared <= (cx.PointAt(cx.T1) - v.P).LengthSquared;
            tEnd = atStart ? cx.T0 : cx.T1; tIn = atStart ? cx.T1 : cx.T0;
            var q = cx.PointAt(tEnd + (tIn - tEnd) * 1e-4); var d = q - v.P;
            return d.LengthSquared > 0 ? d.Normalized() : Vector3d.Zero;
        }
        TEdge? Cusp(TVertex v)
        {
            var de = Leave(e, v, out _, out _);
            if (de == Vector3d.Zero) return null;
            foreach (var o in v.Edges)
            {
                if (ReferenceEquals(o, e) || ReferenceEquals(o.V0, o.V1)) continue;
                var d = Leave(o, v, out _, out _);
                if (Vector3d.Dot(d, de) > Math.Cos(3 * Math.PI / 180)) return o;
            }
            return null;
        }
        var res = pts;
        foreach (bool atV0 in new[] { true, false })
        {
            var v = atV0 ? e.V0 : e.V1;
            if (Cusp(v) is not { } partner) continue;
            Leave(e, v, out double tEnd, out double tIn);
            // параметр первой внутренней точки ломаной со стороны v
            var nb = atV0 ? res[1] : res[^2];
            double t1 = CurveParam.Closest(c, nb);
            var extra = new List<Vector3d>();
            for (int k = 1; k < 60; k++)
            {
                double t = tEnd + (t1 - tEnd) * Math.Pow(0.5, k);
                var q = c.PointAt(t);
                if ((q - v.P).Length < 1e-6) break;
                // ближе к клюву, где зазор до соседней кривой меньше 2e-5 мм, точек не ставить: их склеит сварка сетки, и
                // контуры пересекутся; последнее звено до вершины — общая с соседом узкая щель
                var pc = partner.Curve; double gap = (pc.PointAt(CurveParam.Closest(pc, q)) - q).Length;
                if (gap < 2e-5) break;
                extra.Add(q);
            }
            if (extra.Count == 0) continue;
            extra.Reverse();                                    // от вершины наружу: ближние первыми
            var copy = new List<Vector3d>(res);
            if (atV0) copy.InsertRange(1, extra);
            else { var rev = new List<Vector3d>(extra); rev.Reverse(); copy.InsertRange(copy.Count - 1, rev); }
            res = copy;
            Interlocked.Increment(ref CuspDensified);
        }
        return res;
    }
    public static long CuspDensified;

    private static List<Vector3d> RawEdgePoints(TEdge e)
    {
        // ПОСЧИТАННОЕ ГОДИТСЯ, ТОЛЬКО ЕСЛИ ТОЧНОСТЬ ТА ЖЕ. Прежде сравнения не было, и смена
        // прогиба или углового шага не действовала: ребро отдавало старые точки, а соседняя
        // грань, посчитанная заново, получала другие — отсюда и щели на стыке.
        var want = (Core.Tol.Chord, Core.Tol.ChordAngle);

        var c = e.PolylineCache;
        var source = (e.Curve, e.V0.P, e.V1.P);
        if (c != null && e.PolylineQuality == want && ReferenceEquals(e.PolylineSource.Curve, e.Curve) &&
            e.PolylineSource.P0 == source.Item2 && e.PolylineSource.P1 == source.Item3) return c;

        var pts = new List<Vector3d>();
        e.Curve.Tessellate(pts, true);
        if (pts.Count < 2) pts = new List<Vector3d> { e.V0.P, e.V1.P };
        // У замкнутого ребра (V0 = V1) оба конца кривой — одна точка, и сравнение решало
        // округление косинуса: обход окружности мог перевернуться (найдено на прочитанном
        // STEP-файле: верхняя кромка отверстия шла не туда, проверка площади грани отвергала
        // верную сетку). Замкнутое ребро идёт по параметру своей кривой.
        if (!ReferenceEquals(e.V0, e.V1) && (pts[0] - e.V0.P).LengthSquared > (pts[^1] - e.V0.P).LengthSquared) pts.Reverse();
        pts[0] = e.V0.P; pts[^1] = e.V1.P;                    // концы — точно вершины тела
        if (!CuspOff && !ReferenceEquals(e.V0, e.V1)) pts = DensifyAtCusps(e, pts);
        e.PolylineCache = pts;
        e.PolylineQuality = want;
        e.PolylineSource = source;
        return pts;
    }

    /// <summary>
    /// Упорядоченные замкнутые контуры грани: списки (ребро, обратно ли). null — рёбра не
    /// складываются в циклы (развилка, обрыв).
    /// </summary>
    public static List<List<(TEdge E, bool Rev)>>? Cycles(TFace f)
    {
        // Готовые контуры грани (f.Loops) здесь НЕ используются: флаг направления полуребра
        // в топологии означает не то же, что направление обхода при сборке сетки, и при
        // попытке брать циклы прямо оттуда охват падал с 80 тел до 75.
        var edges = f.Edges.ToList();
        var byVertex = new Dictionary<TVertex, List<TEdge>>();
        foreach (var e in edges)
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (!byVertex.TryGetValue(v, out var l)) byVertex[v] = l = new List<TEdge>();
                l.Add(e);
            }
        // Ребро можно пройти столько раз, сколько у него полурёбер в этой грани: шов внутри
        // грани (разрез) обходится дважды, в разные стороны. Прежде такое ребро бралось один
        // раз, контур выходил незамкнутым, и всё тело оставалось на прежней сетке.
        var left = new Dictionary<TEdge, int>();
        foreach (var e in edges)
        {
            int cap = e.CoEdges.Count(c => c.Loop?.Face == f);
            left[e] = Math.Max(1, cap);
        }
        var cycles = new List<List<(TEdge, bool)>>();
        foreach (var start in edges)
        {
            if (left[start] <= 0) continue;
            var cyc = new List<(TEdge, bool)>();
            TEdge cur = start; bool rev = false;
            TVertex begin = start.V0;
            for (int guard = 0; guard < 2 * edges.Count + 2; guard++)
            {
                left[cur]--;
                cyc.Add((cur, rev));
                TVertex end = rev ? cur.V0 : cur.V1;
                if (end == begin) break;
                // Следующее ребро в этой вершине; ребро с двумя полурёбрами — второй раз назад.
                TEdge? next = byVertex[end].FirstOrDefault(x => left[x] > 0 && (x != cur || x.V0 == end || x.V1 == end));
                if (next == null) return null;
                rev = next.V1 == end && next.V0 != end;
                cur = next;
            }
            if ((cyc[^1].Item2 ? cyc[^1].Item1.V0 : cyc[^1].Item1.V1) != begin) return null;
            cycles.Add(cyc);
        }
        return cycles;
    }

    /// <summary>Период поверхности по u (0 — не замкнута).</summary>
    /// <summary>
    /// Поверхность с переставленными параметрами: (u, v) → (v, u). Нужна поверхностям,
    /// замкнутым по второму параметру (вращение): вся развёртка по периоду написана для
    /// первого параметра, и без перестановки контур, пересекающий шов оборота, рвался —
    /// сетка грани уходила от поверхности на миллиметры.
    /// </summary>
    private sealed class SwapUv : ISurface3
    {
        private readonly ISurface3 _s;
        public SwapUv(ISurface3 s) => _s = s;
        public ISurface3 Inner => _s;
        public bool IsPlanar => _s.IsPlanar;
        public Vector3d PointAt(Vector2d uv) => _s.PointAt(new Vector2d(uv.Y, uv.X));
        public Vector3d NormalAt(Vector2d uv) => _s.NormalAt(new Vector2d(uv.Y, uv.X));
        public Vector2d ProjectToUV(Vector3d p) { var q = _s.ProjectToUV(p); return new Vector2d(q.Y, q.X); }
        public void TessellateFace(TFace face, MeshBuilder mb) => _s.TessellateFace(face, mb);
    }

    public static double PeriodU(ISurface3 s) => s switch
    {
        SectionSurface ss when ss.Closed => ss.UMax - ss.UMin,
        ExtrudedSurface ex when ex.Base is ArcCurve3 a && a.IsClosed => 1.0,
        SwapUv sw when sw.Inner is RevolvedSurface r && Math.Abs(Math.Abs(r.Sweep) - 2 * Math.PI) < 1e-9 => 1.0,
        // Трубка вращения с замкнутым основанием (тор): замкнута по первому параметру.
        RevolvedSurface rv when rv.Base is ArcCurve3 ba && ba.IsClosed => 1.0,
        _ => 0
    };

    /// <summary>
    /// Период параметра u для развёртки контура: у дуги любого размаха угол определён с
    /// точностью до оборота (2π/|размах| в единицах u) — без развёртки контур части цилиндра
    /// рвался в (u, v), и сетка уходила от поверхности на миллиметры (прогон, 7).
    /// </summary>
    private static double UnwrapPeriod(ISurface3 s) => s switch
    {
        ExtrudedSurface ex when ex.Base is ArcCurve3 a && Math.Abs(a.Sweep) > 1e-12 => 2 * Math.PI / Math.Abs(a.Sweep),
        _ => PeriodU(s)
    };

    /// <summary>
    /// Сетка грани: точки и треугольники (индексы), ориентация — наружу, как у треугольников
    /// разбиения. null — грань построить не удалось (остаётся прежняя сетка).
    /// </summary>
    public static (List<Vector3d> P, List<int> T)? Mesh(TFace f, out string? why) => Mesh(f, out why, out _);

    /// <summary>
    /// Сетка грани БЕЗ ПРИДИРОК: проверки качества отключаются, и берётся первый способ, давший
    /// треугольники. Нужна для тел из обменных файлов, где отказ означает, что грань останется
    /// со своей отдельной сеткой и соседи разойдутся.
    /// </summary>
    public static (List<Vector3d> P, List<int> T)? MeshBestEffort(TFace f, out List<Vector3d>? normals)
    {
        bool prev = Lenient;
        Lenient = true;
        try { return Mesh(f, out _, out normals); }
        finally { Lenient = prev; }
    }

    /// <summary>Не отвергать сетку по проверкам качества (для обменных файлов).</summary>
    [ThreadStatic] public static bool Lenient;

    /// <summary>
    /// Двойники вершин у шва: новый номер → исходная вершина. При подсчёте сторон двойник
    /// считается той же вершиной, иначе шов даёт мнимые непарные стороны (у трубы шесть).
    /// </summary>
    [ThreadStatic] private static Dictionary<int, int>? TwinOf;

    private static int Same(int v) => TwinOf != null && TwinOf.TryGetValue(v, out int o) ? o : v;


    /// <summary>
    /// То же, плюс нормали точной поверхности в точках сетки: при построении параметры каждой
    /// точки известны, и нормаль берётся прямо из поверхности. Прежде гранение считало её
    /// проекцией каждой вершины — самая дорогая часть отрисовки.
    /// </summary>
    /// <summary>
    /// ПАМЯТЬ О ПОСЧИТАННЫХ ГРАНЯХ. Дерево из шести операций делает семь булевых, и каждая
    /// размечает ВСЕ грани тела заново — хотя от операции к операции меняются единицы из
    /// десятков. Измерено на кубе со скруглениями и вырезом: разметка граней 5.2 с из 8.0 с
    /// всего перестроения, 74 грани по семь раз.
    ///
    /// Ключ — сама геометрия грани: вид поверхности и концы рёбер её контуров. Грань, у которой
    /// это совпало, уже посчитана: её сетка та же. Ключ считается дёшево, без гранения.
    ///
    /// ПОСЛЕ ОТКЛАДЫВАНИЯ РАЗМЕТКИ памяти почти нечего запоминать: тело размечается один раз за
    /// перестроение, и повторов внутри одного раза не бывает. Память остаётся полезной при
    /// правке дерева — когда та же грань считается снова при следующем перестроении, — но и
    /// тогда её цена (одно тело из ста в охвате) не окупается. Поэтому выключена.
    /// </summary>
    private static readonly Dictionary<string, ((List<Vector3d> P, List<int> T)? M, string? Why, List<Vector3d>? N)>
        Remembered = new();

    /// <summary>Сколько раз сетка грани взялась из памяти — для замеров.</summary>
    public static int Reused;

    /// <summary>
    /// ПОЛЬЗОВАТЬСЯ ЛИ ПАМЯТЬЮ. ПО УМОЛЧАНИЮ НЕТ, и вот числа, на которых это решено.
    ///
    ///   без памяти   охват 90 тел из ста, разметка ста тел 82.7 с
    ///   с памятью    охват 89,            разметка 68.1 с (на 18% быстрее)
    ///
    /// То есть память экономит пятую часть времени, но одно тело из ста перестаёт размечаться
    /// целиком. Почему — пока не установлено: ключ усилен до пяти точек на ребро с видом
    /// поверхности, разворотом и числом контуров, а из памяти отдаются копии списков, не сами
    /// списки. Ни то, ни другое дела не изменило.
    ///
    /// ТРИ ПРИЧИНЫ НЕГОДНОСТИ НАЙДЕНЫ И УСТРАНЕНЫ, каждая измерением:
    ///   — ВЛАДЕНИЕ: в память шли те же списки, что отдавались наружу, и правка полученной сетки
    ///     портила память. Теперь кладутся и отдаются копии.
    ///   — ТОЧНОСТЬ: в ключ не входили прогиб и угловой шаг, и сетка грубого качества выдавалась
    ///     при мелком. Теперь входят.
    ///   — КЛЮЧ ПО ТОЧКАМ ГРАНИЦЫ: вид поверхности и пять отсчётов на ребро не задают поверхность
    ///     под гранью — охват падал с 89 тел из ста до 87. Теперь в ключ входит ПОДПИСЬ самой
    ///     поверхности (у плоскости нормаль и отступ, у выдавливания образующая и вектор, у
    ///     вращения ось и углы), а поверхность без подписи не запоминается вовсе.
    ///   — ПРИВЯЗКА ТОЧЕК: подстановка Snap меняет сетку, но в ключ войти не может — это
    ///     переданная снаружи работа, а не число. Пока привязка задана, память не действует.
    ///
    /// После всех четырёх охват с памятью тот же, что без неё (89 тел из ста), а разметка ста тел
    /// идёт 76.7 с против 80.0 с. Выигрыш невелик: при отложенной разметке тело считается один
    /// раз за перестроение, и повторов внутри одного раза почти нет. Память окупается при правке
    /// дерева, когда та же грань считается снова.
    ///
    /// По умолчанию остаётся выключенной: выигрыш мал, а проверок на правку дерева пока нет.
    /// Включается переключателем P3D_FCACHE=1.
    /// </summary>
    private static bool _gRemember = Environment.GetEnvironmentVariable("P3D_FCACHE") == "1";
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool Remember
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.FaceCache : _gRemember;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gRemember, value, "ExactFaceMesher.Remember");
    }

    /// <summary>Забыть посчитанное: нужно, когда допуски или настройки сетки поменялись.</summary>
    public static void Forget() { lock (Remembered) Remembered.Clear(); }

    /// <summary>
    /// Подпись поверхности её собственными числами. null означает, что подписи для такого вида
    /// нет: такую грань запоминать нельзя — две разные поверхности дали бы одну подпись.
    /// </summary>
    private static string? SurfaceSignature(Surfaces.ISurface3 s)
    {
        static string N(double v) => Math.Round(v, 9).ToString("R");
        static string P(Vector3d p) => $"{N(p.X)},{N(p.Y)},{N(p.Z)}";

        switch (s)
        {
            case Surfaces.PlaneSurface pl:
                return $"pl[{P(pl.F.Origin)}|{P(pl.F.W)}|{P(pl.F.U)}]";

            case Surfaces.ExtrudedSurface ex:
            {
                string? b = CurveSignature(ex.Base);
                return b == null ? null : $"ex[{b}|{P(ex.Dir)}]";
            }

            case Surfaces.RevolvedSurface rv:
            {
                string? b = CurveSignature(rv.Base);
                return b == null ? null : $"rv[{b}|{P(rv.Axis.Origin)}|{P(rv.Axis.W)}|{N(rv.A0)}:{N(rv.A1)}]";
            }

            // угловой лоскут — экземпляр (формулу по числам не подписать)
            case Surfaces.CornerPatchSurface cp:
                return $"cp#{cp.Id}";

            default:
                return null;
        }
    }

    /// <summary>Подпись кривой: нужна поверхностям, построенным на кривой.</summary>
    private static string? CurveSignature(Curves.ICurve3? c)
    {
        static string N(double v) => Math.Round(v, 9).ToString("R");
        static string P(Vector3d p) => $"{N(p.X)},{N(p.Y)},{N(p.Z)}";

        switch (c)
        {
            case null: return null;

            case Curves.LineCurve3 ln:
                return $"ln[{P(ln.A)}|{P(ln.B)}]";

            case Curves.ArcCurve3 arc:
                return $"arc[{P(arc.Frame.Origin)}|{P(arc.Frame.W)}|{P(arc.Frame.U)}|" +
                       $"{N(arc.Radius)}|{N(arc.A0)}:{N(arc.A1)}]";

            default:
                return null;
        }
    }

    private static string? KeyOf(TFace f)
    {
        var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
        if (src == null) return null;

        // КЛЮЧ ОПИРАЕТСЯ НА САМУ ПОВЕРХНОСТЬ, А НЕ НА НЕСКОЛЬКО ТОЧЕК ЕЁ ГРАНИЦЫ.
        //
        // Прежние попытки брали вид поверхности и отсчёты на рёбрах: сперва три точки на ребро,
        // потом пять. Обе не различали грани — охват сетки падал с 90 тел из ста до 89, а с
        // пятью точками до 87. Это и понятно: граница грани не задаёт поверхность под ней, и две
        // грани с одинаковым обводом, но разной поверхностью, получали один ключ.
        //
        // Теперь в ключ входит ПОДПИСЬ ПОВЕРХНОСТИ — её собственные числа: у плоскости нормаль и
        // отступ, у выдавливания образующая и вектор, у вращения ось и углы. Поверхность, для
        // которой подписи нет, не запоминается вовсе: лучше считать заново, чем выдать чужое.
        //
        // В ключ входит и точность: сетка, посчитанная при грубом прогибе, не годится при мелком.
        string? surface = SurfaceSignature(src);
        if (surface == null) return null;             // подписи нет — не запоминаем

        var sb = new System.Text.StringBuilder();
        sb.Append(surface).Append(f.Reversed ? '-' : '+').Append(f.Loops.Count)
          .Append('@').Append(Core.Tol.Chord.ToString("R")).Append(':')
          .Append(Core.Tol.ChordAngle.ToString("R")).Append('@');

        foreach (var loop in f.Loops)
        {
            sb.Append('|').Append(loop.IsOuter ? 'o' : 'h').Append(loop.CoEdges.Count);

            foreach (var ce in loop.CoEdges)
            {
                var c = ce.Edge.Curve;
                if (c == null) return null;

                sb.Append(ce.Reversed ? '<' : '>');

                for (int i = 0; i <= 4; i++)
                {
                    Vector3d p = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 4.0);
                    sb.Append(Math.Round(p.X, 7)).Append(',')
                      .Append(Math.Round(p.Y, 7)).Append(',')
                      .Append(Math.Round(p.Z, 7)).Append(';');
                }

                sb.Append('/');
            }
        }

        return sb.ToString();
    }

    /// <summary>Сетка грани для разбора: без памяти и без побочных действий.</summary>
    public static (List<Vector3d> P, List<int> T)? BuildFacePreview(TFace f)
    {
        try { return MeshUncached(f, out _, out _); }
        catch { return null; }
    }

    public static (List<Vector3d> P, List<int> T)? Mesh(TFace f, out string? why, out List<Vector3d>? normals)
    {
        // ПАМЯТЬ НЕ ДЕЙСТВУЕТ, ПОКА ЗАДАНА ПРИВЯЗКА ТОЧЕК. Привязка (Snap) меняет саму сетку, а в
        // ключ не входит и войти не может: это переданная снаружи подстановка, а не число.
        // Выдача из памяти при включённой привязке вернула бы сетку, посчитанную без неё.
        string? key = Remember && Snap == null ? KeyOf(f) : null;
        if (key != null)
            lock (Remembered)
                if (Remembered.TryGetValue(key, out var known))
                {
                    // ОТДАЮТСЯ КОПИИ, А НЕ САМИ СПИСКИ. Тот, кто получил сетку, вправе её менять —
                    // сливать точки, переставлять стороны, — и общий список после этого испортил
                    // бы все последующие обращения. Измерено: без копий охват сетки падал с 90
                    // тел из ста до 89.
                    Reused++;
                    why = known.Why;
                    normals = known.N == null ? null : new List<Vector3d>(known.N);

                    return known.M == null
                        ? null
                        : (new List<Vector3d>(known.M.Value.P), new List<int>(known.M.Value.T));
                }

        var made = MeshUncached(f, out why, out normals);
        if (MeshTrace && made is { } mtr && Topology.GeometryAudit.AuthoritativeSurface(f) is { } fsr2) Console.Error.WriteLine($"MESHTRACE   result folded {Folded(fsr2, mtr.P, mtr.T)} tris {mtr.T.Count / 3}");

        // САМОПРОВЕРКА ПО ТРЕУГОЛЬНИКАМ ГРАНИ. Сетка по контурам принимается, только если её
        // площадь сходится с площадью треугольников гранения самой грани (они — область грани по
        // построению). Контуры повёрнутой ступеньки со скруглением (ось Y, угол 0.9) охватывали
        // соседнюю область: сетка по ним накрывала чужие грани, и видимая сетка тела была
        // негодна, хотя треугольники гранения верны. Тогда — отказ, и грань рисуется по ним.
        if (made is { } mm && f.Surface is Surfaces.FacetedSurface fsc && fsc.Facets.Count > 0)
        {
            double aMesh = 0, aFacets = 0, perim = 0;
            for (int i = 0; i + 2 < mm.T.Count; i += 3) aMesh += Vector3d.Cross(mm.P[mm.T[i + 1]] - mm.P[mm.T[i]], mm.P[mm.T[i + 2]] - mm.P[mm.T[i]]).Length / 2;
            foreach (var (a, b, c) in fsc.Facets) aFacets += Vector3d.Cross(b - a, c - a).Length / 2;
            // Кривые рёбра две сетки могут резать разными хордами — полоса вдоль них; прямые — нет.
            foreach (var l in f.Loops) foreach (var ce in l.CoEdges)
                if (ce.Edge.Curve is not Curves.LineCurve3) perim += (ce.End.P - ce.Start.P).Length + (ReferenceEquals(ce.Edge.V0, ce.Edge.V1) ? 2 * Math.PI * 1 : 0);
            bool planar = (Topology.GeometryAudit.AuthoritativeSurface(f)?.IsPlanar ?? false);
            double tol = (planar ? 1e-6 : 2e-3) * Math.Max(aMesh, aFacets) + perim * Core.Tol.Chord * 2 + 1e-9;
            AreaDebug?.Invoke($"{f.Tag} mesh {aMesh:0.######} facets {aFacets:0.######} tol {tol:0.######}");
            if (Math.Abs(aMesh - aFacets) > tol)
            {
                why = $"the loop mesh area {aMesh:0.###} disagrees with the face's own triangles {aFacets:0.###}";
                made = null; normals = null;
                RejectedByArea++;
            }
        }

        // В ПАМЯТЬ КЛАДУТСЯ КОПИИ, И ОТДАЮТСЯ ТОЖЕ КОПИИ. Прежде при первом счёте в память шли
        // ТЕ ЖЕ списки, что уходили вызывающему: сдвиг одной точки на 123 мм в полученной сетке
        // обнаруживался потом в памяти, и следующая грань получала испорченное.
        if (key != null)
            lock (Remembered)
                Remembered[key] = (
                    made == null ? null : (new List<Vector3d>(made.Value.P), new List<int>(made.Value.T)),
                    why,
                    normals == null ? null : new List<Vector3d>(normals));

        return made;
    }

    /// <summary>
    /// G2: сначала — разметка в параметрах поверхности (UvMesher); она принимается, только если
    /// прошла свою самопроверку (покрытие области и площадь против точной), иначе — прежние способы.
    /// </summary>
    private static bool _gUseUvMesher = true;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.UseUvMesher), если он задан; иначе общее по умолчанию.</summary>
    public static bool UseUvMesher
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.UseUvMesher : _gUseUvMesher;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gUseUvMesher, value, "ExactFaceMesher.UseUvMesher");
    }

    /// <summary>Сколько сеток по контурам отвергнуто самопроверкой площади (диагностика).</summary>
    public static int RejectedByArea;
    public static Action<string>? AreaDebug;

    static readonly bool MeshTrace = Environment.GetEnvironmentVariable("P3D_MESHTRACE") == "1";
    static readonly bool SplitDbg = Environment.GetEnvironmentVariable("P3D_MESHWHY") == "1";
    static readonly bool ConeSplitOff = Environment.GetEnvironmentVariable("P3D_CONESPLIT") == "0";

    private static (List<Vector3d> P, List<int> T)? MeshConeApexSplit(TFace f, RevolvedSurface rv, List<List<(TEdge E, bool Rev)>> cycles)
    {
        if (rv.Base is not LineCurve3 bc) return null;
        var w = rv.Axis.W.Normalized(); var o = rv.Axis.Origin;
        double Rad(Vector3d q) { var d = q - o; return (d - w * Vector3d.Dot(d, w)).Length; }
        bool apexIsA = Rad(bc.A) < Rad(bc.B);
        var apex = apexIsA ? bc.A : bc.B; var far = apexIsA ? bc.B : bc.A;
        if (Rad(apex) > 1e-9 * Math.Max(1, (far - apex).Length)) return null;
        double lmin = double.MaxValue;
        foreach (var cyc in cycles) foreach (var (e, _) in cyc) foreach (var p in EdgePoints(e)) lmin = Math.Min(lmin, (p - apex).Length);
        if (!(lmin > 1e-6)) return null;
        var gdir = (far - apex).Normalized();
        var pc = apex + gdir * (lmin * 0.5);
        var cc = o + w * Vector3d.Dot(pc - o, w); double rc = Rad(pc);
        var fr = Frame3.FromNormal(cc, w);
        var circle = new ArcCurve3(fr, rc, 0, 2 * Math.PI);
        var cv = new TVertex(circle.PointAt(0));
        var ce = new TEdge { V0 = cv, V1 = cv, Curve = circle };
        // обход контура, обходящего ось (по θ вокруг w в базисе окружности)
        double Theta(Vector3d q) { var d = q - cc; return Math.Atan2(Vector3d.Dot(d, fr.V), Vector3d.Dot(d, fr.U)); }
        double windOuter = 0;
        foreach (var cyc in cycles)
        {
            double tot = 0, prev = double.NaN;
            foreach (var (e, rev) in cyc)
            {
                var pts = EdgePoints(e).ToList(); if (rev) pts.Reverse();
                foreach (var q in pts)
                {
                    double t = Theta(q);
                    if (!double.IsNaN(prev)) { double dt = t - prev; while (dt > Math.PI) dt -= 2 * Math.PI; while (dt < -Math.PI) dt += 2 * Math.PI; tot += dt; }
                    prev = t;
                }
            }
            if (Math.Abs(tot) > Math.PI) { windOuter = tot; break; }
        }
        if (windOuter == 0) return null;
        // направление окружности (вторая граница пояса) — то, при котором разметка пояса проходит самопроверку (покрытие, площадь,
        // отход от поверхности): обход контуров по θ не определяет его надёжно (петля отверстия может обходить ось)
        var bandLine = apexIsA ? new LineCurve3(pc, bc.B) : new LineCurve3(bc.A, pc);
        (List<Vector3d> P, List<int> T)? bandMesh = null; bool circleRev = false; string? bandWhy = null;
        foreach (bool cr in new[] { windOuter < 0, windOuter > 0 })
        {
            var band = new TFace { Surface = new RevolvedSurface(bandLine, rv.Axis, rv.A0, rv.A1), Reversed = f.Reversed };
            bool first = true;
            foreach (var cyc in cycles)
            {
                var l = band.AddLoop(first); first = false;
                foreach (var (e, rev) in cyc) l.CoEdges.Add(new TCoEdge { Edge = e, Reversed = rev, Loop = l });
            }
            var lc = band.AddLoop(false); lc.CoEdges.Add(new TCoEdge { Edge = ce, Reversed = cr, Loop = lc });
            bandMesh = MeshUncached(band, out bandWhy, out _);
            if (bandMesh != null) { circleRev = cr; break; }
        }
        if (SplitDbg) Console.Error.WriteLine($"CONESPLIT band {(bandMesh == null ? "null: " + bandWhy : "ok")} lmin {lmin:G4} windOuter {windOuter:G4} loops {cycles.Count}");
        if (bandMesh == null) return null;
        var rvCap = new RevolvedSurface(new LineCurve3(apex, pc), rv.Axis, rv.A0, rv.A1);
        var cap = MeshPoleCap(f, rvCap, new List<(TEdge E, bool Rev)> { (ce, !circleRev) });
        if (SplitDbg) Console.Error.WriteLine($"CONESPLIT cap {(cap == null ? "null" : "ok")}");
        if (cap == null) return null;
        var P = new List<Vector3d>(bandMesh.Value.P); var T = new List<int>(bandMesh.Value.T);
        Orient(f, P, T);
        var cp = new List<Vector3d>(cap.Value.P); var ct = new List<int>(cap.Value.T);
        Orient(f, cp, ct);
        int off = P.Count; P.AddRange(cp); foreach (var i in ct) T.Add(i + off);
        return (P, T);
    }

    static readonly bool PoleCapFirstOff = Environment.GetEnvironmentVariable("P3D_POLECAPFIRST") == "0";
    static readonly bool FoldCheckOff = Environment.GetEnvironmentVariable("P3D_FOLDCHECK") == "0";
    public static long FoldRejected;
    /// <summary>Есть ли в сетке треугольник, повёрнутый против нормали поверхности (угол > 120°) при общей стороне сетки.</summary>
    internal static bool Folded(ISurface3 src, List<Vector3d> P, List<int> T)
    {
        double pos = 0, neg = 0; int nPos = 0, nNeg = 0;
        for (int t = 0; t + 2 < T.Count; t += 3)
        {
            var n = Vector3d.Cross(P[T[t + 1]] - P[T[t]], P[T[t + 2]] - P[T[t]]); double a = n.Length; if (a < 1e-18) continue;
            var c = (P[T[t]] + P[T[t + 1]] + P[T[t + 2]]) / 3;
            Vector3d ns; try { ns = src.NormalAt(src.ProjectToUV(c)); } catch { continue; }
            if (ns.LengthSquared < 1e-24) continue;                    // особая точка (вершина конуса, полюс)
            double cs = Vector3d.Dot(n / a, ns.Normalized());
            if (cs > 0.5) { pos += a; nPos++; } else if (cs < -0.5) { neg += a; nNeg++; }
        }
        return nPos > 0 && nNeg > 0;
    }

    private static (List<Vector3d> P, List<int> T)? MeshUncached(TFace f, out string? why, out List<Vector3d>? normals)
    {
        if (MeshTrace) Console.Error.WriteLine($"MESHTRACE {DateTime.Now:HH:mm:ss.fff} face {f.Tag} {Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count} ces {f.Loops.Sum(l => l.CoEdges.Count)} faceted {f.Surface is FacetedSurface}");
        // g.05: ОСТРЫЙ КОНУС С ОДНИМ КОНТУРОМ — кольцами к вершине ДО разметки в параметрах (только образующая-отрезок: у сферы
        // ось карты не обязана проходить через центр шапки — кольца по меридианам складываются, отказ маршрута). У вершины
        // конуса UvMesher давал треугольники обратного обхода (3 стороны сетки), а его самопроверка (покрытие, площадь) их не
        // видит. Мутация P3D_POLECAPFIRST=0.
        if (!PoleCapFirstOff && f.Surface is not FacetedSurface && Topology.GeometryAudit.AuthoritativeSurface(f) is RevolvedSurface { Base: LineCurve3 } rvFirst
            && ContainsPole(f, rvFirst) && Cycles(f) is { Count: 1 } cyc1 && LoopWindsAxis(rvFirst, cyc1[0]) && MeshPoleCap(f, rvFirst, cyc1[0]) is { } capFirst)
        {
            normals = null; why = null;
            Orient(f, capFirst.P, capFirst.T);
            var chk = Validate(f, rvFirst, capFirst.P, capFirst.T, out why, checkArea: false);
            // складка (контур шапки — не окружность вокруг вершины: эллипс и дуга, серия 8, случай 82) — следующий способ
            if (chk != null && !FoldCheckOff && Folded(rvFirst, chk.Value.P, chk.Value.T)) { Interlocked.Increment(ref FoldRejected); if (MeshTrace) Console.Error.WriteLine("MESHTRACE   pole cap folded — rejected"); chk = null; }
            if (chk != null) return chk;
        }
        if (UseUvMesher && Snap == null && !Lenient)
        {
            if (MeshTrace) Console.Error.WriteLine($"MESHTRACE {DateTime.Now:HH:mm:ss.fff}   uv start");
            var uvm = UvMesher.Mesh(f, Tol.Chord, out _);
            if (MeshTrace) Console.Error.WriteLine($"MESHTRACE {DateTime.Now:HH:mm:ss.fff}   uv done {(uvm == null ? "null" : "ok")}");
            // g.05: СКЛАДКА — треугольники против нормали поверхности при большинстве по ней (конусная грань у края: полоса
            // перевёрнутых треугольников, сетка «обходится в одну сторону» с соседом — 9 отказов маршрута на серии 8). Такая
            // разметка не берётся, грань размечается следующим способом. Мутация P3D_FOLDCHECK=0.
            if (uvm is { } uvF && !FoldCheckOff && Topology.GeometryAudit.AuthoritativeSurface(f) is { } fsrc && Folded(fsrc, uvF.P, uvF.T))
            {
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE {DateTime.Now:HH:mm:ss.fff}   uv folded — rejected");
                Interlocked.Increment(ref FoldRejected);
                uvm = null;
            }
            if (uvm is { } uvr)
            {
                var uvSrc = Topology.GeometryAudit.AuthoritativeSurface(f)!;
                // Нормали показа — по поверхности, со стороной треугольников.
                var nTri = Vector3d.Cross(uvr.P[uvr.T[1]] - uvr.P[uvr.T[0]], uvr.P[uvr.T[2]] - uvr.P[uvr.T[0]]);
                var nS0 = uvSrc.NormalAt(uvSrc.ProjectToUV(uvr.P[uvr.T[0]]));
                double sg = Vector3d.Dot(nTri, nS0) < 0 ? -1 : 1;
                normals = uvr.P.Select(q => { var nn = uvSrc.NormalAt(uvSrc.ProjectToUV(q)); return nn.LengthSquared > 0 ? nn.Normalized() * sg : nn; }).ToList();
                why = null;
                return (uvr.P, uvr.T);
            }
        }
        normals = null;
        why = null;
        ISurface3? src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
        if (src == null || src is FacetedSurface) { why = "нет точной поверхности"; return null; }
        var cycles = Cycles(f);
        if (cycles == null || cycles.Count == 0) { why = "рёбра не складываются в контуры"; return null; }

        // K07. ПОЛЮС ПОВЕРХНОСТИ ВРАЩЕНИЯ ВНУТРИ ГРАНИ — РАЗВЁРТКА НЕ ЗАМКНУТА. У чаши сферы
        // единственный контур — окружность; в (u, v) это отрезок, а не многоугольник: вся линия
        // полюса сжата в точку, шва нет. Построитель всё равно строил сетку и затем отвергал её
        // самопроверкой («отходит от поверхности на 7 мм») — 230 мс впустую на каждом сферическом
        // вырезе при том же итоге: грань остаётся на прежней сетке. Отказ теперь сразу.
        // K10: чаша поверхности вращения без прежней сетки (прочитанная из файла) размечается
        // кольцами от кромки к полюсу; точки кромки — общие с соседней гранью.
        if (f.Surface is not FacetedSurface && src is RevolvedSurface rvCap && ContainsPole(f, src) && cycles.Count == 1
            && MeshPoleCap(f, rvCap, cycles[0]) is { } cap)
        {
            Orient(f, cap.P, cap.T);
            var checkedCap = Validate(f, src, cap.P, cap.T, out why, checkArea: false);
            if (checkedCap != null) return checkedCap;
        }
        // ПОСТАВКА 4: ПЛОСКИЙ ДИСК ВРАЩЕНИЕМ (образующая ⟂ оси доходит до оси — торец тела вращения, дно составного
        // инструмента). Полюс внутри, но поверхность — плоскость: размечается как плоскость с нормалью, направленной как у
        // исходной поверхности. Прежде отказ «полюс внутри грани» уводил всё тело на прежнюю сетку, а она с точными
        // кромками не сходилась (составная зенковка: 16 замечаний по периметру верхней грани).
        if (src is RevolvedSurface rvDisk && ContainsPole(f, src) && Geometry.ExactEdges.ShapeOf(src) is Geometry.Ssi.Plane diskPl)
        {
            var w0 = diskPl.N.Normalized();
            var rim = cycles[0].Count > 0 ? cycles[0][0].E.V0.P : diskPl.P;
            var nRv = rvDisk.NormalAt(rvDisk.ProjectToUV(rim));
            var nDisk = Vector3d.Dot(nRv, w0) < 0 ? -w0 : w0;
            src = new PlaneSurface(Frame3.FromNormal(diskPl.P, nDisk));
        }
        // g.05: ГРАНЬ ОСТРОГО КОНУСА С ВЕРШИНОЙ И ОТВЕРСТИЯМИ — мнимый разрез по окружности между вершиной и ближайшим контуром:
        // пояс (без полюса) — обычной разметкой, шапка — кольцами к вершине; точки окружности общие. Прежде — отказ «полюс внутри
        // грани», и булева острого конуса с шаром через бок уходила в многогранный путь. Мутация P3D_CONESPLIT=0.
        if (!ConeSplitOff && f.Surface is not FacetedSurface && src is RevolvedSurface { Base: LineCurve3 } rvSplit && ContainsPole(f, src)
            && cycles.Count >= 2 && MeshConeApexSplit(f, rvSplit, cycles) is { } split)
        {
            var chk = Validate(f, src, split.P, split.T, out why, checkArea: false);
            if (SplitDbg) Console.Error.WriteLine($"CONESPLIT validate {(chk == null ? "fail: " + why : "ok")}");
            if (chk != null) return chk;
        }
        if (DeclinePoles && ContainsPole(f, src))
        {
            // KRN-11: полюс внутри — конформная плоскость (полюс — внутренняя точка 0), иначе отказ, как прежде
            if (MeshConformal(f, src, cycles, out var cwhy) is { } conf && Validate(f, src, conf.P, conf.T, out cwhy) is { } confOk) { ConformalMeshed++; return confOk; }
            ConformalFailed++;
            why = "полюс поверхности вращения внутри грани: развёртка не замкнута; " + cwhy;
            PoleDeclined++;
            return null;
        }
        using var _pf = Diagnostics.Prof.Time("сетка: грань целиком");
        if (MeshTrace) Console.Error.WriteLine($"MESHTRACE {DateTime.Now:HH:mm:ss.fff}   loops start");
        var r = MeshLoops(f, src, cycles, out why);
        if (MeshTrace) Console.Error.WriteLine($"MESHTRACE {DateTime.Now:HH:mm:ss.fff}   loops done {(r == null ? "null " + why : "ok")}");
        string? cwhy2 = null;
        (List<Vector3d> P, List<int> T)? Conformal()
        {
            if (MeshConformal(f, src, cycles, out cwhy2) is { } conf && Validate(f, src, conf.P, conf.T, out cwhy2) is { } confOk) { ConformalMeshed++; return confOk; }
            ConformalFailed++; return null;
        }
        if (r == null)
        {
            if (Conformal() is { } c1) { why = null; normals = null; return c1; }
            why += "; " + cwhy2; return null;
        }
        normals = r.Value.N;
        // Вложенная MeshLoops уже проверила результат для несвязной грани.
        var checkedR = r.Value.Validated ? (r.Value.P, r.Value.T) : Validate(f, src, r.Value.P, r.Value.T, out why);
        if (checkedR != null) return checkedR;
        if (Conformal() is { } c2) { why = null; normals = null; return c2; }
        why += "; " + cwhy2; return null;
    }

    /// <summary>
    /// Чаша с полюсом: кольца по широте от точек кромки к полюсу, на каждом — столько же точек,
    /// сколько на кромке (по их углам), полюс — одна точка. null — если кромку не удаётся
    /// поставить в соответствие параметрам поверхности.
    /// </summary>
    private static (List<Vector3d> P, List<int> T)? MeshPoleCap(TFace f, RevolvedSurface rv, List<(TEdge E, bool Rev)> cycle)
    {
        var rim = new List<Vector3d>();
        foreach (var (e, rev) in cycle)
        {
            var ep = EdgePoints(e).ToList();
            if (rev) ep.Reverse();
            if (rim.Count > 0) ep.RemoveAt(0);
            rim.AddRange(ep);
        }
        // g.05/KRN-09: замыкание кромки — с допуском сварки: у окружности, прочитанной из STEP (9 знаков), концы расходятся на
        // ~1e-9 мм, двойник оставался, и полоса у шва давала щепки обратного обхода (3 стороны сетки острого конуса).
        while (rim.Count > 1 && (rim[0] - rim[^1]).Length < (RimWeldOff ? 1e-9 : Tol.Weld)) rim.RemoveAt(rim.Count - 1);
        if (rim.Count < 3) return null;
        var bc = rv.Base;
        Vector3d w = rv.Axis.W.Normalized();
        double Rad(Vector3d q) { var d = q - rv.Axis.Origin; return (d - w * Vector3d.Dot(d, w)).Length; }
        double tPole = Rad(bc.PointAt(bc.T0)) < Rad(bc.PointAt(bc.T1)) ? bc.T0 : bc.T1;
        // KRN-03. ОБА КОНЦА ОБРАЗУЮЩЕЙ — ПОЛЮСЫ (сфера): шапка неоднозначна, и прежде выбирался всегда второй конец
        // меридиана — сторона зависела от того, как построена ось поверхности, а не от грани (булева операция
        // переворачивала ось по готовой сетке). Теперь — по обходу контура: материал слева, значит контур идёт против
        // часовой стрелки вокруг наружной нормали шапки; векторная площадь контура смотрит в сторону нормали у её полюса.
        double eps = 1e-9 * Math.Max(1, Rad(bc.PointAt((bc.T0 + bc.T1) / 2)));
        if (CapByLoop && Rad(bc.PointAt(bc.T0)) <= eps && Rad(bc.PointAt(bc.T1)) <= eps && LoopVectorArea(f) is { } la)
        {
            double Side(double tp)
            {
                double tn = tp + (tp == bc.T0 ? 1 : -1) * 1e-3 * (bc.T1 - bc.T0);
                var q = rv.PointAt(new Vector2d(tn, rv.A0));
                return Vector3d.Dot(la, f.NormalAt(q));
            }
            double s0 = Side(bc.T0), s1 = Side(bc.T1);
            if (s0 > 0 && s1 < 0) tPole = bc.T0;
            else if (s1 > 0 && s0 < 0) tPole = bc.T1;
            else CapLoopUndecided++;
            CapByLoopPicks++;
        }
        var uv = rim.Select(rv.ProjectToUV).ToList();
        double maxDt = uv.Max(q => Math.Abs(q.X - tPole));
        double sweepRad = Math.Abs(rv.Sweep);
        int levels = Math.Max(2, (int)Math.Ceiling(maxDt * Math.Abs(bc is Curves.ArcCurve3 a ? a.Sweep : 1) / Math.Max(1e-3, Tol.ChordAngle)));
        var P = new List<Vector3d>(rim);
        int n = rim.Count;
        for (int j = 1; j < levels; j++)
            for (int i = 0; i < n; i++)
            {
                double t = uv[i].X + (tPole - uv[i].X) * j / levels;
                P.Add(rv.PointAt(new Vector2d(t, uv[i].Y)));
            }
        int pole = P.Count;
        P.Add(bc.PointAt(tPole));
        var T = new List<int>();
        for (int j = 0; j < levels - 1; j++)
            for (int i = 0; i < n; i++)
            {
                int a0 = j * n + i, a1 = j * n + (i + 1) % n, b0 = (j + 1) * n + i, b1 = (j + 1) * n + (i + 1) % n;
                T.AddRange(new[] { a0, a1, b1, a0, b1, b0 });
            }
        int last = (levels - 1) * n;
        for (int i = 0; i < n; i++) T.AddRange(new[] { last + i, last + (i + 1) % n, pole });
        return (P, T);
    }

    /// <summary>KRN-03: шапку сферы выбирать по обходу контура (P3D_CAPBYLOOP=0 — прежний выбор, для мутационной проверки).</summary>
    static readonly bool RimWeldOff = Environment.GetEnvironmentVariable("P3D_RIMWELD") == "0";
    internal static readonly bool CapByLoop = Environment.GetEnvironmentVariable("P3D_CAPBYLOOP") != "0";
    public static int CapByLoopPicks, CapLoopUndecided;

    /// <summary>Векторная площадь ½∮x×dx контуров грани по полурёбрам (направление полуребра — обход с материалом слева).</summary>
    private static Vector3d? LoopVectorArea(TFace f)
    {
        var a = Vector3d.Zero; int n = 0; Vector3d? o = null;
        foreach (var l in f.Loops)
            foreach (var ce in l.CoEdges)
            {
                var ep = EdgePoints(ce.Edge).ToList();
                if (ce.Reversed) ep.Reverse();
                o ??= ep.Count > 0 ? ep[0] : null;
                for (int i = 0; i + 1 < ep.Count; i++) { a += Vector3d.Cross(ep[i] - o!.Value, ep[i + 1] - o.Value) * 0.5; n++; }
            }
        return n == 0 ? null : a;
    }

    /// <summary>Отказывать ли сразу граням, содержащим полюс поверхности вращения.</summary>
    private static bool _gDeclinePoles = Environment.GetEnvironmentVariable("P3D_POLE_DECLINE") != "0";
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.DeclinePoles), если он задан; иначе общее по умолчанию.</summary>
    public static bool DeclinePoles
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.DeclinePoles : _gDeclinePoles;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gDeclinePoles, value, "ExactFaceMesher.DeclinePoles");
    }
    public static int PoleDeclined;

    /// <summary>
    /// Содержит ли грань полюс: точку основания на оси полного вращения. Грань без фасетов
    /// (исходная грань тела вращения) содержит всю поверхность; у фасетной грани полюс
    /// внутри, если на нём стоит вершина её треугольников и он не лежит на рёбрах грани.
    /// </summary>
    /// <summary>Контур обходит ось поверхности вращения (полный оборот угла) — грань с одним таким контуром содержит полюс.</summary>
    private static bool LoopWindsAxis(RevolvedSurface rv, List<(TEdge E, bool Rev)> cycle)
    {
        var w = rv.Axis.W.Normalized(); var u = rv.Axis.U.Normalized(); var v = Vector3d.Cross(w, u);
        double tot = 0, prev = double.NaN;
        foreach (var (e, rev) in cycle)
        {
            var pts = EdgePoints(e).ToList(); if (rev) pts.Reverse();
            foreach (var p in pts)
            {
                var d = p - rv.Axis.Origin; double th = Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u));
                if (!double.IsNaN(prev)) { double dt = th - prev; while (dt > Math.PI) dt -= 2 * Math.PI; while (dt < -Math.PI) dt += 2 * Math.PI; tot += dt; }
                prev = th;
            }
        }
        return Math.Abs(tot) > Math.PI;
    }

    private static bool ContainsPole(TFace f, ISurface3 src)
    {
        if (src is not RevolvedSurface rv || Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) > 1e-9) return false;
        var poles = new List<Vector3d>();
        foreach (double t in new[] { rv.Base.T0, rv.Base.T1 })
        {
            var p = rv.Base.PointAt(t);
            var d = p - rv.Axis.Origin;
            var off = d - rv.Axis.W * Vector3d.Dot(d, rv.Axis.W);
            if (off.Length < 1e-9) poles.Add(p);
        }
        if (poles.Count == 0) return false;
        if (f.Surface is not FacetedSurface fs) return true;
        double tol = 1e-4;   // вершины точного тела стоят на сетке ExactGrid (шаг 1e-5 мм)
        foreach (var pole in poles)
        {
            if (f.Edges.Any(e => (e.V0.P - pole).Length < tol || (e.V1.P - pole).Length < tol)) continue;
            foreach (var (a, b, c) in fs.Facets)
                if ((a - pole).Length < tol || (b - pole).Length < tol || (c - pole).Length < tol) return true;
        }
        return false;
    }

    /// <summary>
    /// САМОПРОВЕРКА. Новая сетка отвергается (грань остаётся на прежней), если её площадь
    /// расходится с треугольниками разбиения больше чем на 1% + периметр × допуск, или центр
    /// какого-либо треугольника отходит от точной поверхности больше трёх допусков гранения.
    /// </summary>
    private static (List<Vector3d> P, List<int> T)? Validate(TFace f, ISurface3 src, List<Vector3d> P, List<int> T, out string? why, bool checkArea = true, double boundaryDev = 0)
    {
        using var _pv = Diagnostics.Prof.Time("сетка: самопроверка");
        why = null;
        // СЕТКА НЕ БЫВАЕТ ТОЧНЕЕ СВОИХ РЁБЕР. У шва касания ребро — изолиния скругления, но
        // концы прибиты к вершинам тела, смещённым зубцами резца до полумиллиметра; на
        // поверхности соседней грани эта кривая лежит с тем же отходом. Порог — по отходу
        // собственной границы грани, но не строже трёх допусков гранения.
        double limit = Math.Max(3 * Math.Max(Tol.Chord, 0.05), 1.2 * boundaryDev);
        double aNew = 0, dev = 0, minArea = double.MaxValue;
        int minAt = 0, devAt = 0;
        int facingPos = 0, facingNeg = 0, negAt = -1;
        for (int i = 0; i + 2 < T.Count; i += 3)
        {
            Vector3d a = P[T[i]], b = P[T[i + 1]], c = P[T[i + 2]];
            double ar = Vector3d.Cross(b - a, c - a).Length / 2;
            if (ar < minArea) { minArea = ar; minAt = i; }
            aNew += ar;
            var g = (a + b + c) / 3;
            var guv = src.ProjectToUV(g);
            double dv = (src.PointAt(guv) - g).Length;
            if (dv > dev) { dev = dv; devAt = i; }
            if (!FlipCheckOff && ar > 1e-12)
            {
                double sd = Vector3d.Dot(Vector3d.Cross(b - a, c - a), src.NormalAt(guv));
                if (sd > 0) facingPos++; else if (sd < 0) { facingNeg++; negAt = i; }
            }
        }
        if (dev > limit)
        {
            double aOldDbg = 0;
            if (f.Surface is FacetedSurface fdbg) foreach (var (a, b, c) in fdbg.Facets) aOldDbg += Vector3d.Cross(b - a, c - a).Length / 2;
            Vector3d da = P[T[devAt]], db = P[T[devAt + 1]], dc = P[T[devAt + 2]];
            why = $"сетка отходит от поверхности на {dev:0.###} мм (граница {boundaryDev:0.###}), площадь {aNew:0.#} против {aOldDbg:0.#}, треугольников {T.Count / 3}; " +
                  $"худший треугольник: площадь {Vector3d.Cross(db - da, dc - da).Length / 2:0.####}, стороны {(db - da).Length:0.##}/{(dc - db).Length:0.##}/{(da - dc).Length:0.##}";
            return null;
        }
        // Вырожденный треугольник (три точки на одной прямой) не рисуется и объёма не даёт, но
        // рвёт проверку замкнутости всего тела: его сторона-хорда идёт через промежуточную
        // точку. Способ триангуляции с такими треугольниками отвергается — сработает следующий.
        // ПРОВЕРЕНО и ОСТАВЛЕНО КАК ЕСТЬ: пропускать тонкие, но длинные треугольники (площадь
        // 4e-6 при сторонах 12 и 92 мм) нельзя. Охват поднимался с 89 до 91 тела, но два теста
        // ловили открытые стороны в экспорте: высота такого треугольника около 1e-7 мм, то есть
        // ниже разрешения STL с одинарной точностью, и он там схлопывается.
        if (aNew > 0 && minArea < 1e-9 * aNew)
        {
            Vector3d a0 = P[T[minAt]], b0 = P[T[minAt + 1]], c0 = P[T[minAt + 2]];
            // Откуда вершины: индексы меньше числа точек контура — с границы, больше — вставлены
            // уточнением.
            string origin = string.Join("/", new[] { T[minAt], T[minAt + 1], T[minAt + 2] }
                .Select(i => i < BoundaryCount ? "контур" : "вставка"))
                + "; индексы " + string.Join(",", new[] { T[minAt], T[minAt + 1], T[minAt + 2] })
                + $" из {BoundaryCount} точек контура"
                + "; середина — " + (f.Edges.Any(e => (e.V0.P - P[T[minAt + 1]]).Length < 1e-6 || (e.V1.P - P[T[minAt + 1]]).Length < 1e-6)
                                     ? "вершина тела" : "точка внутри ребра");
            {
                var have = new HashSet<(int, int)>();
                for (int i = 0; i + 2 < T.Count; i += 3)
                    for (int k = 0; k < 3; k++) have.Add((T[i + k], T[i + (k + 1) % 3]));
                int withNeighbour = 0;
                for (int k = 0; k < 3; k++)
                    if (have.Contains((T[minAt + (k + 1) % 3], T[minAt + k]))) withNeighbour++;
                origin += $"; сторон с соседом {withNeighbour} из 3";
            }
            why = $"вырожденный треугольник [{origin}] (площадь {minArea:E1}) стороны {(b0 - a0).Length:0.####}/{(c0 - b0).Length:0.####}/{(a0 - c0).Length:0.####} " +
                  $"у ({a0.X:0.###},{a0.Y:0.###},{a0.Z:0.###})";
            return null;
        }
        // Одна и та же сторона дважды в одну сторону — треугольники грани наложились друг на
        // друга. Способ отвергается, сработает следующий.
        var used = new HashSet<(int, int)>();
        for (int i = 0; i + 2 < T.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                var e = (T[i + k], T[i + (k + 1) % 3]);
                if (!used.Add(e)) { why = "треугольники грани наложились"; return null; }
            }
        // KRN-06 (06.12): ТРЕУГОЛЬНИКИ ОБРАТНОЙ СТОРОНЫ. Ушная триангуляция брала «ухо» из трёх точек одного ребра-дуги,
        // прямого в развёртке (шов ленты катящегося шара — дуга сечения при постоянном u): в (u, v) треугольник вырожден, в
        // пространстве — нет, и сторона у него случайная; самопроверка (отход, площадь, наложение) его пропускала, а
        // проверка тела находила «стороны, обходимые в одну сторону» (кромка усечённого конуса). Сторона каждого
        // треугольника — как у большинства (по нормали поверхности в его центре); иначе способ отвергается.
        // Мутация P3D_MESHFLIPCHECK=0.
        if (facingPos > 0 && facingNeg > 0)
        {
            int minority = Math.Min(facingPos, facingNeg);
            why = $"треугольники обратной стороны: {minority} из {facingPos + facingNeg}";
            return null;
        }
        // K10. У ГРАНИ БЕЗ ПРЕЖНЕЙ СЕТКИ (прочитанной из обменного файла) сверять площадь было не с
        // чем, и неверная триангуляция Г-образной грани (1 200 мм² вместо 900: накрыт вырез)
        // принималась. У плоской грани площадь известна точно — по её контурам.
        if (checkArea && f.Surface is not FacetedSurface && src.IsPlanar)
        {
            // Контуры — как записаны в грани (направление полуребра — из файла): построенные по
            // связности циклы своё направление выбирают сами, и площади отверстий складывались.
            var nrm = Vector3d.Zero;
            foreach (var loop in f.Loops)
            {
                var pts = new List<Vector3d>();
                foreach (var ce in loop.CoEdges)
                {
                    var ep = EdgePoints(ce.Edge).ToList();
                    if (ce.Reversed) ep.Reverse();
                    if (pts.Count > 0) ep.RemoveAt(0);
                    pts.AddRange(ep);
                }
                for (int i = 0; i < pts.Count; i++) nrm += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]) / 2;
            }
            double aLoops = nrm.Length;
            double perim = 0; foreach (var e in f.Edges) perim += (e.V1.P - e.V0.P).Length;
            if (aLoops > 0 && Math.Abs(aNew - aLoops) > 0.01 * aLoops + perim * Tol.Chord * 4)
            {
                why = $"площадь сетки {aNew:0.###} не сходится с площадью по контурам {aLoops:0.###}";
                return null;
            }
        }
        if (checkArea && f.Surface is FacetedSurface fo && fo.Facets.Count > 0)
        {
            // СРЕДНЕЕ СМЕЩЕНИЕ ПОВЕРХНОСТИ: изменение объёма под гранью, делённое на её
            // площадь. У грани со швом касания это сотые доли миллиметра (вершины смещены, но
            // объём почти тот же), у грани, чьё ребро идёт хордой поперёк поверхности
            // (дефект топологии), — миллиметры: такая грань остаётся на прежней сетке.
            double vNew = 0, vOld = 0, aFac = 0;
            for (int i = 0; i + 2 < T.Count; i += 3)
                vNew += Vector3d.Dot(P[T[i]], Vector3d.Cross(P[T[i + 1]], P[T[i + 2]])) / 6;
            foreach (var (a, b, c) in fo.Facets)
            {
                vOld += Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;
                aFac += Vector3d.Cross(b - a, c - a).Length / 2;
            }
            // У грани с ничтожной площадью (щепка разбиения) деление на площадь бессмысленно:
            // мерка давала «смещение 12 678 мм» и роняла всё тело.
            if (aFac > 1e-3 && Math.Abs(vNew - vOld) / aFac > 1.0)
            {
                why = $"поверхность грани смещена в среднем на {Math.Abs(vNew - vOld) / aFac:0.###} мм";
                return null;
            }

            double aOld = 0; foreach (var (a, b, c) in fo.Facets) aOld += Vector3d.Cross(b - a, c - a).Length / 2;
            double per = 0; foreach (var e in f.Edges) per += (e.V1.P - e.V0.P).Length;
            // ПЛОЩАДЬ СХОДИТСЯ ВСЕГДА. Послабление по прогибу границы нужно для отхода от
            // поверхности (у шва касания вершины смещены), но не для площади: им прикрывалась
            // недостача покрытия — сетка не накрывала грань на 1.4%, а проверка молчала.
            if (Math.Abs(aNew - aOld) > 0.01 * aOld + per * Tol.Chord * 4)
            {
                var cy = Cycles(f);
                // Самокасание контура: одна и та же точка встречается дважды.
                int repeats = 0;
                if (cy != null)
                    foreach (var c0 in cy)
                    {
                        var pts0 = new List<Vector3d>();
                        foreach (var (e0, rev0) in c0)
                        {
                            var pe = EdgePoints(e0);
                            for (int i = 0; i + 1 < pe.Count; i++) pts0.Add(rev0 ? pe[pe.Count - 1 - i] : pe[i]);
                        }
                        for (int i = 0; i < pts0.Count; i++)
                            for (int j = i + 1; j < pts0.Count; j++)
                                if ((pts0[i] - pts0[j]).Length < 1e-6) repeats++;
                    }
                // Площадь по самому контуру (в пространстве, через проекцию на среднюю нормаль).
                double aLoop = 0;
                if (cy != null && cy.Count > 0)
                {
                    var pts1 = new List<Vector3d>();
                    foreach (var (e1, rev1) in cy[0])
                    {
                        var pe = EdgePoints(e1);
                        for (int i = 0; i + 1 < pe.Count; i++) pts1.Add(rev1 ? pe[pe.Count - 1 - i] : pe[i]);
                    }
                    Vector3d n1 = Vector3d.Zero, c1 = Vector3d.Zero;
                    foreach (var q in pts1) c1 += q;
                    c1 /= Math.Max(1, pts1.Count);
                    for (int i = 0; i < pts1.Count; i++)
                        n1 += Vector3d.Cross(pts1[i] - c1, pts1[(i + 1) % pts1.Count] - c1);
                    aLoop = n1.Length / 2;
                }
                // Самопересечение контура: звенья пересекаются, хотя точки не повторяются.
                int crosses = 0;
                if (cy != null && cy.Count > 0)
                {
                    var pts2 = new List<Vector3d>();
                    foreach (var (e2, rev2) in cy[0])
                    {
                        var pe = EdgePoints(e2);
                        for (int i = 0; i + 1 < pe.Count; i++) pts2.Add(rev2 ? pe[pe.Count - 1 - i] : pe[i]);
                    }
                    var uv2 = pts2.Select(src.ProjectToUV).ToList();
                    int n2 = uv2.Count;
                    for (int i = 0; i < n2; i++)
                        for (int j = i + 2; j < n2; j++)
                        {
                            if (i == 0 && j == n2 - 1) continue;
                            Vector2d a2 = uv2[i], b2 = uv2[(i + 1) % n2], c2 = uv2[j], d2 = uv2[(j + 1) % n2];
                            double D(Vector2d p1, Vector2d p2, Vector2d p3) => (p2.X - p1.X) * (p3.Y - p1.Y) - (p2.Y - p1.Y) * (p3.X - p1.X);
                            if (D(a2, b2, c2) * D(a2, b2, d2) < 0 && D(c2, d2, a2) * D(c2, d2, b2) < 0) crosses++;
                        }
                }
                // Площадь со знаком (по средней нормали грани): если она меньше площади без
                // знака, часть треугольников вывернута.
                Vector3d nAvg = Vector3d.Zero;
                for (int i = 0; i + 2 < T.Count; i += 3)
                    nAvg += Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]);
                double aSigned = nAvg.Length / 2;
                // Сколько треугольников смотрит против средней нормали и какова их площадь.
                int flipped = 0; double flippedArea = 0, maxFlipped = 0;
                Vector3d nDir = nAvg.LengthSquared > 1e-18 ? nAvg.Normalized() : Vector3d.UnitZ;
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    Vector3d nt = Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]);
                    if (Vector3d.Dot(nt, nDir) >= 0) continue;
                    flipped++; flippedArea += nt.Length / 2; maxFlipped = Math.Max(maxFlipped, nt.Length / 2);
                }
                why = $"вывернутых треугольников {flipped} из {T.Count / 3}, их площадь {flippedArea:0.##}, наибольший {maxFlipped:0.##}; " +
                      $"способ {LastCandidate}: площадь {aNew:0.#} (со знаком {aSigned:0.#}) вместо {aOld:0.#}, по контуру {aLoop:0.#}, самопересечений {crosses}; повторов точек в контуре {repeats}; [{LastGrouping}]; контуров {cy?.Count} ({(cy == null ? "" : string.Join(",", cy.Select(c => c.Count)))}), рёбер {f.Edges.Count()}, поверхность {src.GetType().Name}";
                return null;
            }
        }
        return (P, T);
    }

    /// <summary>
    /// Контур inner лежит внутри poly — по большинству проб. Одна точка ненадёжна: вершины
    /// соседних контуров бывают на общей границе (контуры касаются), и вложенность
    /// определялась неверно — площадь отверстия прибавлялась вместо вычитания.
    /// </summary>
    private static bool InsideMost(List<Vector2d> poly, List<Vector2d> inner)
    {
        int yes = 0, no = 0;
        int step = Math.Max(1, inner.Count / 7);
        for (int i = 0; i < inner.Count; i += step)
        {
            // Проба — середина звена: вершины могут лежать ровно на границе соседнего контура.
            Vector2d q = (inner[i] + inner[(i + 1) % inner.Count]) / 2;
            if (Inside(poly, q)) yes++; else no++;
        }
        return yes > no;
    }

    private static bool Inside(List<Vector2d> poly, Vector2d q)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if (((poly[i].Y > q.Y) != (poly[j].Y > q.Y)) &&
                (q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X))
                inside = !inside;
        }
        return inside;
    }

    private static (List<Vector3d> P, List<int> T, bool Validated, List<Vector3d>? N)? MeshLoops(TFace f, ISurface3 src, List<List<(TEdge E, bool Rev)>> cycles, out string? why, bool grouped = false)
    {
        why = null;

        // Контуры в 3D (без повтора первой точки) и в (u, v) с развёрткой по периоду.
        double per = UnwrapPeriod(src);
        var loops3 = new List<List<Vector3d>>();
        var loopsUV = new List<List<Vector2d>>();
        var winding = new List<int>();
        // НОМЕРА ЦИКЛОВ. Вырожденные циклы при сборке пропускаются, поэтому номера в loopsUV и в
        // cycles расходятся: куску доставался ЧУЖОЙ цикл, и он объявлялся вырожденным.
        var loopCycle = new List<int>();
        for (int ci = 0; ci < cycles.Count; ci++)
        {
            var cyc = cycles[ci];
            var p3 = new List<Vector3d>();
            foreach (var (e, rev) in cyc)
            {
                var ep = EdgePoints(e);
                if (ep.Count < 2) continue;                        // ребро схлопнулось слиянием вершин
                var seq = rev ? Enumerable.Reverse(ep).ToList() : ep;
                for (int i = 0; i + 1 < seq.Count; i++)
                    if (p3.Count == 0 || (seq[i] - p3[^1]).LengthSquared > 1e-18) p3.Add(seq[i]);
            }
            while (p3.Count > 1 && (p3[0] - p3[^1]).LengthSquared < 1e-18) p3.RemoveAt(p3.Count - 1);
            // Контур, схлопнувшийся при слиянии близких вершин (крошечное отверстие меньше
            // десятой доли микрона), пропускается: его площадь ничтожна, а грань без него
            // строится. Прежде такая грань отвергалась целиком.
            if (p3.Count < 3) continue;
            var uv = new List<Vector2d>(p3.Count);
            using (Diagnostics.Prof.Time("сетка: контур в параметры"))
                foreach (var q in p3) uv.Add(src.ProjectToUV(q));
            RawLoopDebug?.Invoke(f, uv);
            int wind = 0;
            if (per > 0)
            {
                for (int i = 1; i < uv.Count; i++)
                {
                    double du = uv[i].X - uv[i - 1].X;
                    double k = Math.Round(du / per);
                    uv[i] = new Vector2d(uv[i].X - k * per, uv[i].Y);
                }
                // Замыкание: последний → первый.
                double close = uv[0].X - uv[^1].X;
                // Сколько периодов набежало при обходе (со знаком).
                wind = -(int)Math.Round(close / per);
                if (Math.Abs(close + wind * per) > per / 2) wind = 0;
            }
            loops3.Add(p3); loopsUV.Add(uv); winding.Add(wind); loopCycle.Add(ci);
            LoopDebug?.Invoke(f, cyc.Select(x => x.E).ToList(), uv, wind);
        }

        // ЗАМКНУТАЯ ГРАНЬ БЕЗ ГРАНИЦЫ — РАЗОБРАНО, НЕ ЛЕЧИТСЯ ЗДЕСЬ. У полного цилиндра или тела
        // вращения на 360° контура нет: есть шов, пройденный дважды, и вырожденное ребро на оси.
        // Область определения поверхности известна точно (выдавливание: u ∈ [Base.T0, Base.T1],
        // v ∈ [0,1]; вращение: та же u, v ∈ [0,1]), и сетка по ней строится. Но измерено, что
        // такие грани занимают НЕ ВСЮ область, а её часть: построенная по всей области сетка
        // отходит от грани на 349 мм. Границы части в топологии нет — контур вырожден. Значит,
        // нужен другой источник пределов: параметры концов рёбер грани или разметка поверхности
        // при её создании.

        if (loops3.Count == 0)
        {
            // Числа по отброшенным циклам: сколько точек набралось и какова длина.
            var parts = new List<string>();
            foreach (var cyc in cycles)
            {
                int pts = 0; double len = 0;
                Vector3d? prev = null;
                foreach (var (e, rev) in cyc)
                {
                    var ep = EdgePoints(e);
                    pts += Math.Max(0, ep.Count - 1);
                    for (int i = 0; i + 1 < ep.Count; i++) len += (ep[i + 1] - ep[i]).Length;
                    prev = ep.Count > 0 ? ep[^1] : prev;
                }
                parts.Add($"рёбер {cyc.Count}, точек {pts}, длина {len:0.####}, " +
                          $"виды {string.Join("/", cyc.Select(z => z.E.Curve.GetType().Name.Replace("Curve3", "")))}, " +
                          $"длины рёбер {string.Join("/", cyc.Select(z => (z.E.V1.P - z.E.V0.P).Length.ToString("0.###")))}");
            }
            // Площадь всей области поверхности — для сравнения с площадью грани.
            double aFull = 0;
            {
                ICurve3? bc = src switch
                {
                    Surfaces.ExtrudedSurface ex2 => ex2.Base,
                    Surfaces.RevolvedSurface rv2 => rv2.Base,
                    _ => null
                };
                if (bc != null)
                {
                    var bp = new List<Vector3d>();
                    bc.Tessellate(bp, true);
                    int nu2 = Math.Max(1, bp.Count - 1), nv2 = 24;
                    for (int j = 0; j < nv2; j++)
                        for (int i = 0; i < nu2; i++)
                        {
                            Vector3d p00 = src.PointAt(new Vector2d(bc.T0 + (bc.T1 - bc.T0) * i / nu2, (double)j / nv2));
                            Vector3d p10 = src.PointAt(new Vector2d(bc.T0 + (bc.T1 - bc.T0) * (i + 1) / nu2, (double)j / nv2));
                            Vector3d p01 = src.PointAt(new Vector2d(bc.T0 + (bc.T1 - bc.T0) * i / nu2, (double)(j + 1) / nv2));
                            Vector3d p11 = src.PointAt(new Vector2d(bc.T0 + (bc.T1 - bc.T0) * (i + 1) / nu2, (double)(j + 1) / nv2));
                            aFull += Vector3d.Cross(p10 - p00, p11 - p00).Length / 2 + Vector3d.Cross(p11 - p00, p01 - p00).Length / 2;
                        }
                }
            }
            double aOldDeg = f.Surface is FacetedSurface fsd
                ? fsd.Facets.Sum(t => Vector3d.Cross(t.B - t.A, t.C - t.A).Length / 2) : 0;
            why = $"все контуры вырождены: циклов {cycles.Count}, площадь грани {aOldDeg:0.####}, площадь всей области {aFull:0.####}, период {per:0.#####}, " +
                  $"гранёная {(f.Surface is FacetedSurface)}, треугольников {(f.Surface as FacetedSurface)?.Facets.Count ?? 0} [{string.Join("; ", parts)}]";
            return null;
        }

        // ОБЩЕЕ ОКНО ПО u. Каждый контур разворачивается по периоду сам по себе, и его u
        // может оказаться сдвинутым на целый период относительно соседнего: тогда отверстие
        // не опознаётся как вложенное, и его площадь прибавляется вместо вычитания.
        if (per > 0 && loopsUV.Count > 1)
        {
            int big = 0; double bigLen = -1;
            for (int i = 0; i < loopsUV.Count; i++)
            {
                double len = loopsUV[i].Max(q => q.X) - loopsUV[i].Min(q => q.X);
                if (loops3[i].Count > 2 && Math.Abs(Area(loopsUV[i])) > bigLen) { bigLen = Math.Abs(Area(loopsUV[i])); big = i; }
            }
            double baseU = loopsUV[big].Average(q => q.X);
            for (int i = 0; i < loopsUV.Count; i++)
            {
                if (i == big || winding[i] != 0) continue;
                double c = loopsUV[i].Average(q => q.X);
                double k = Math.Round((c - baseU) / per);
                if (k != 0) loopsUV[i] = loopsUV[i].Select(q => new Vector2d(q.X - k * per, q.Y)).ToList();
            }
        }

        // Многоугольник в (u, v).
        var outer3 = new List<Vector3d>(); var outerUV = new List<Vector2d>();
        int stripA = 0, stripB = 0;                                // длины краёв полосы (кольцо)
        var holes3 = new List<List<Vector3d>>(); var holesUV = new List<List<Vector2d>>();
        int wrapping = winding.Count(w => w != 0);
        if (wrapping > 0 && PeriodU(src) == 0) { why = "контур обходит незамкнутую поверхность"; return null; }
        if (wrapping == 0 && loopsUV.Count > 1 && !grouped)
        {
            // НЕСКОЛЬКО КУСКОВ ОДНОЙ ГРАНИ. Грань — всё, что пришло из разбиения с одной
            // меткой, и она бывает несвязной; второй контур — отверстие, только если лежит
            // внутри другого. Прежде он всегда считался отверстием: площадь сетки вдвое.
            // Глубина вложенности: чётная — внешний контур куска, нечётная — отверстие.
            int nl = loopsUV.Count;
            var depth = new int[nl]; var parent = new int[nl];
            for (int i = 0; i < nl; i++)
            {
                parent[i] = -1; double best = double.MaxValue;
                for (int j = 0; j < nl; j++)
                {
                    if (i == j || !InsideMost(loopsUV[j], loopsUV[i])) continue;
                    double aj = Math.Abs(Area(loopsUV[j]));
                    if (aj < best) { best = aj; parent[i] = j; }
                }
            }
            for (int i = 0; i < nl; i++) { int d = 0, k = parent[i]; while (k >= 0 && d < nl) { d++; k = parent[k]; } depth[i] = d; }
            LastGrouping = string.Join(", ", Enumerable.Range(0, nl).Select(i => $"контур {i}: точек {loopsUV[i].Count}, площадь {Area(loopsUV[i]):0.##}, внутри {parent[i]}, глубина {depth[i]}"));
            var allP = new List<Vector3d>(); var allT = new List<int>(); var allN = new List<Vector3d>();
            for (int i = 0; i < nl; i++)
            {
                if (depth[i] % 2 != 0) continue;
                var sub = new List<List<(TEdge E, bool Rev)>> { cycles[loopCycle[i]] };
                for (int j = 0; j < nl; j++) if (parent[j] == i && depth[j] % 2 == 1) sub.Add(cycles[loopCycle[j]]);
                var part = MeshLoops(f, src, sub, out why, grouped: true);
                // ВЫРОЖДЕННЫЙ КУСОК ПРОПУСКАЕТСЯ. У грани с несколькими контурами один кусок
                // бывает вырожденным (шов плюс ребро нулевой длины — площади в нём нет), и
                // из-за него отвергалась вся грань, хотя остальные куски строятся.
                // Измерено: отказов «вырожденные контуры» стало 2 вместо 5, но незамкнутость
                // выросла с 4 до 6, а расхождение площади с 1 до 2 — пропуск куска оставляет в
                // грани незакрытое место. Охват тел одинаков (90), тесты и прогон чисты, поэтому
                // оставлено: отказ переносится с грани на тело, где он виден полнее.
                if (part == null && why != null && why.StartsWith("все контуры вырождены")) continue;
                if (part == null) return null;
                if (GroupDebug != null)
                {
                    double ap = 0;
                    for (int t = 0; t + 2 < part.Value.T.Count; t += 3)
                        ap += Vector3d.Cross(part.Value.P[part.Value.T[t + 1]] - part.Value.P[part.Value.T[t]],
                                             part.Value.P[part.Value.T[t + 2]] - part.Value.P[part.Value.T[t]]).Length / 2;
                    GroupDebug($"грань {f.Id}: кусок по контуру {i}, отверстий {sub.Count - 1}, площадь {ap:0.##}");
                }
                int off = allP.Count;
                allP.AddRange(part.Value.P);
                allN.AddRange(part.Value.N ?? part.Value.P.Select(_ => Vector3d.Zero));
                allT.AddRange(part.Value.T.Select(x => x + off));
            }
            var vr = Validate(f, src, allP, allT, out why);
            return vr == null ? null : (vr.Value.P, vr.Value.T, true, allN);
        }
        if (wrapping == 0)
        {
            // Первый контур — внешний (кусок уже разобран по вложенности), остальные — отверстия.
            outer3 = loops3[0]; outerUV = loopsUV[0];
            for (int i = 1; i < loopsUV.Count; i++) { holes3.Add(loops3[i]); holesUV.Add(loopsUV[i]); }
        }
        else if (wrapping == 2 && per > 0)
        {
            // Кольцо: два контура обходят период навстречу. Разрез по шву в начале первого.
            int a = winding.FindIndex(w => w != 0), b = winding.FindLastIndex(w => w != 0);
            // Края кольца обходят период навстречу друг другу; контуры собраны без учёта
            // направления — второй разворачивается, если обходит в ту же сторону.
            if (Math.Sign(winding[a]) == Math.Sign(winding[b]))
            {
                loops3[b].Reverse(); loopsUV[b].Reverse();
                loopsUV[b] = Unwrapped(loopsUV[b], per);
                winding[b] = -winding[b];
            }
            // ШОВ РАЗРЕЗА — ТАМ, ГДЕ НЕТ ОТВЕРСТИЙ. Разрез кольца по шву превращает грань в
            // полосу; если шов проходит через отверстие, оно вылезает за полосу и площадь
            // выходит больше на его долю (стенка цилиндра с боковым вырезом).
            var holeSpans = new List<(double Lo, double Hi)>();
            for (int i = 0; i < loopsUV.Count; i++)
                if (i != a && i != b) holeSpans.Add((loopsUV[i].Min(q => q.X), loopsUV[i].Max(q => q.X)));
            int startA = 0;
            if (holeSpans.Count > 0)
            {
                double best = -1;
                for (int i = 0; i < loopsUV[a].Count; i++)
                {
                    double u = loopsUV[a][i].X, score = double.MaxValue;
                    foreach (var (lo, hi) in holeSpans)
                    {
                        // Расстояние до промежутка отверстия по кругу периода.
                        double d = double.MaxValue;
                        for (int k = -1; k <= 1; k++)
                        {
                            double l = lo + k * per, h = hi + k * per;
                            d = Math.Min(d, u < l ? l - u : u > h ? u - h : 0);
                        }
                        score = Math.Min(score, d);
                    }
                    if (score > best) { best = score; startA = i; }
                }
            }
            var (a3, aUV) = Rotate(loops3[a], loopsUV[a], startA);
            // Второй контур — с точки, ближайшей по u к началу первого (по модулю периода).
            double u0 = aUV[0].X;
            int bs = 0; double bd = double.MaxValue;
            for (int i = 0; i < loopsUV[b].Count; i++)
            {
                double d = Math.Abs(Wrap(loopsUV[b][i].X - u0, per));
                if (d < bd) { bd = d; bs = i; }
            }
            aUV = Unwrapped(aUV, per);
            (List<Vector3d> P3, List<Vector2d> UV2) BuildStrip(int bStart)
            {
                var (b3x, bUVx) = Rotate(loops3[b], loopsUV[b], bStart);
                // Первый: от u0 до u0 ± per. Второй — развёрнут так, чтобы начинаться у u0 ± per
                // и идти обратно к u0.
                bUVx = Unwrapped(bUVx, per);
                double aEnd = aUV[0].X + winding[a] * per;
                double shift = aEnd - bUVx[0].X;
                shift = per * Math.Round(shift / per);
                bUVx = bUVx.Select(x => new Vector2d(x.X + shift, x.Y)).ToList();
                // Полоса: A (u0 → aEnd), точка A в конце (копия начала со сдвигом), B (aEnd → u0), точка B в конце.
                var p3 = new List<Vector3d>(); var uv2 = new List<Vector2d>();
                p3.AddRange(a3); uv2.AddRange(aUV);
                p3.Add(a3[0]); uv2.Add(new Vector2d(aUV[0].X + winding[a] * per, aUV[0].Y));
                p3.AddRange(b3x); uv2.AddRange(bUVx);
                p3.Add(b3x[0]); uv2.Add(new Vector2d(bUVx[0].X + winding[b] * per, bUVx[0].Y));
                return (p3, uv2);
            }
            var (stripP3, stripUV) = BuildStrip(bs);
            // ПОСТАВКА 4: РАЗРЕЗ ПОЛОСЫ НЕ ДОЛЖЕН ПЕРЕСЕКАТЬ ЕЁ КРАЯ. Начало второго края берётся ближайшим по u к
            // началу первого; если у первого края у шва вертикальный ряд (кромка идёт по стенке уступа: две точки с
            // одним u), а ближайшая точка второго края — по другую сторону ряда, наклонный разрез пересекает край:
            // многоугольник полосы самопересекается, «молния» строит складку, и её диагональ остаётся непарной
            // (стенка глухого отверстия R4 поперёк уступа, K01 walldbg). Тогда — ближайшая точка с другой стороны
            // от u0. Без пересечения выбор прежний, и сетки не меняются.
            if (StripSeamCrosses(stripUV, a3.Count + 1))
            {
                int alt = -1; double ad = double.MaxValue;
                double side0 = Wrap(loopsUV[b][bs].X - u0, per);
                for (int i = 0; i < loopsUV[b].Count; i++)
                {
                    double d = Wrap(loopsUV[b][i].X - u0, per);
                    if (i == bs || Math.Sign(d) == Math.Sign(side0) && side0 != 0) continue;
                    if (Math.Abs(d) < ad) { ad = Math.Abs(d); alt = i; }
                }
                if (alt >= 0)
                {
                    var (p3b, uvb) = BuildStrip(alt);
                    if (!StripSeamCrosses(uvb, a3.Count + 1)) { (stripP3, stripUV) = (p3b, uvb); bs = alt; }
                }
            }
            var b3 = Rotate(loops3[b], loopsUV[b], bs).Item1;
            stripA = a3.Count + 1; stripB = b3.Count + 1;
            outer3.AddRange(stripP3); outerUV.AddRange(stripUV);
            // ОКНО ПОЛОСЫ ПРОБОВАЛ ПРИВОДИТЬ РОВНО К ПЕРИОДУ — ОТКАТИЛ. Измерено: окно по u
            // выходит 1.00081 при периоде 1, то есть полоса перехлёстывает себя за швом на
            // 0.08%. Лёгкое сжатие убирало перехлёст (окно стало ровно 1), но двойное накрытие
            // осталось (треугольники 633.8 при площади многоугольника 307.1), охват не вырос, а
            // проверка скругления по кромке отверстия перестала строить тело. Значит, двойное
            // накрытие идёт не от перехлёста окна, а от самой полосы: её края с обходами −1 и +1
            // дают самопересекающийся многоугольник.

            for (int i = 0; i < loopsUV.Count; i++) if (i != a && i != b) { holes3.Add(loops3[i]); holesUV.Add(loopsUV[i]); }
            // ОТВЕРСТИЯ — В ОКНО ПОЛОСЫ. Полоса после разреза по шву покрывает свой промежуток
            // u; отверстие развёрнуто само по себе и может оказаться сдвинутым на период —
            // тогда оно вылезает за полосу, и площадь выходит больше на его долю.
            if (holesUV.Count > 0)
            {
                double lo = outerUV.Min(q => q.X), hi = outerUV.Max(q => q.X), mid = (lo + hi) / 2;
                for (int i = 0; i < holesUV.Count; i++)
                {
                    double c = holesUV[i].Average(q => q.X);
                    double k = Math.Round((c - mid) / per);
                    if (k != 0) holesUV[i] = holesUV[i].Select(q => new Vector2d(q.X - k * per, q.Y)).ToList();
                }
            }
            LastGrouping = $"кольцо: полоса u {outerUV.Min(q => q.X):0.###}…{outerUV.Max(q => q.X):0.###} ({outerUV.Count} точек), " +
                           string.Join("; ", holesUV.Select((h, i) => $"отверстие {i}: u {h.Min(q => q.X):0.###}…{h.Max(q => q.X):0.###}, v {h.Min(q => q.Y):0.##}…{h.Max(q => q.Y):0.##}")) + $"; края {a} и {b}, обходы [{string.Join(",", winding)}], отверстий {holes3.Count}, " +
                           string.Join("; ", Enumerable.Range(0, loopsUV.Count).Select(i => $"контур {i}: точек {loopsUV[i].Count}, u {loopsUV[i].Min(q => q.X):0.##}…{loopsUV[i].Max(q => q.X):0.##}, v {loopsUV[i].Min(q => q.Y):0.##}…{loopsUV[i].Max(q => q.Y):0.##}"));
        }
        else { why = $"контуров, обходящих период: {wrapping}"; return null; }

        // МАСШТАБ (u, v) ПО МЕТРИКЕ ПОВЕРХНОСТИ: у цилиндра u — доля угла, v — доля высоты,
        // в миллиметрах несопоставимы; без масштаба треугольники выходят длинными иглами.
        Vector2d cUV = Vector2d.Zero; foreach (var q in outerUV) cUV += q; cUV /= outerUV.Count;
        const double hd = 1e-4;
        double su = ((src.PointAt(cUV + new Vector2d(hd, 0)) - src.PointAt(cUV - new Vector2d(hd, 0))) / (2 * hd)).Length;
        double sv = ((src.PointAt(cUV + new Vector2d(0, hd)) - src.PointAt(cUV - new Vector2d(0, hd))) / (2 * hd)).Length;
        if (!(su > 1e-12) || !(sv > 1e-12)) { su = 1; sv = 1; }
        // МЕТРИКА ПО КРИВИЗНЕ: прогиб хорды длины h при кривизне κ — κh²/8. Направления
        // растягиваются в √κ раз: у цилиндра вдоль образующей κ = 0, и треугольники
        // вытягиваются вдоль неё, а не дробятся без пользы. Нормальная кривизна по u и v —
        // наибольшая в нескольких точках контура; к ней добавлена 1/размер грани.
        {
            double ku = 0, kv = 0;
            var probes = new List<Vector2d> { cUV };
            for (int i = 0; i < outerUV.Count; i += Math.Max(1, outerUV.Count / 8)) probes.Add(outerUV[i]);
            foreach (var q in probes)
            {
                Vector3d p0 = src.PointAt(q);
                Vector3d nq = src.NormalAt(q);
                if (nq.LengthSquared < 1e-18) continue;
                nq = nq.Normalized();
                double h2 = 1e-3;
                Vector3d suu = (src.PointAt(q + new Vector2d(h2, 0)) - 2 * p0 + src.PointAt(q - new Vector2d(h2, 0))) / (h2 * h2);
                Vector3d svv = (src.PointAt(q + new Vector2d(0, h2)) - 2 * p0 + src.PointAt(q - new Vector2d(0, h2))) / (h2 * h2);
                ku = Math.Max(ku, Math.Abs(Vector3d.Dot(suu, nq)) / (su * su));
                kv = Math.Max(kv, Math.Abs(Vector3d.Dot(svv, nq)) / (sv * sv));
            }
            double size = 0;
            foreach (var q in outer3) size = Math.Max(size, (q - outer3[0]).Length);
            double eps = 1.0 / Math.Max(size, 1e-3);
            su *= Math.Sqrt(ku + eps); sv *= Math.Sqrt(kv + eps);
        }
        var scale = new Vector2d(su, sv);
        List<Vector2d> Sc(List<Vector2d> l) => l.Select(q => new Vector2d(q.X * su, q.Y * sv)).ToList();
        var outerS = Sc(outerUV); var holesS = holesUV.Select(Sc).ToList();

        // Точки: 3D берётся с контуров (точки рёбер), не из (u, v) — чтобы шов совпадал точно.
        // СПОСОБЫ ТРИАНГУЛЯЦИИ — ПО ОЧЕРЕДИ, пока результат не пройдёт самопроверку:
        // молния кольца, молния вдоль u, молния вдоль v, ушная триангуляция. Выбор одного
        // способа по признаку был хрупким: у скругления R20 длинной в растянутых координатах
        // выходила образующая, и цепочками становились короткие прямые стороны.
        var lookup = new Dictionary<(double, double), Vector3d>();
        void Put(List<Vector2d> uv, List<Vector3d> p3) { for (int i = 0; i < uv.Count; i++) lookup[(uv[i].X, uv[i].Y)] = p3[i]; }
        Put(outerS, outer3);
        for (int i = 0; i < holesS.Count; i++) Put(holesS[i], holes3[i]);

        var candidates = new List<Func<(List<int>, List<Vector2d>)?>>();

        if (stripA > 0 && holesS.Count == 0) candidates.Add(() => Zipper(outerS, stripA, stripB));
        if (holesS.Count == 0)
        {
            candidates.Add(() => MonotoneZipper(outerS, true) is { } z ? (z, outerS) : null);
            candidates.Add(() => MonotoneZipper(outerS, false) is { } z ? (z, outerS) : null);
        }
        candidates.Add(() =>
        {
            KernelContext.Checkpoint("mesh.triangulate");
            var t = Triangulator.Triangulate(outerS, holesS.Select(h => (IReadOnlyList<Vector2d>)h).ToList(), out var p2);
            return t.Count == 0 ? null : (t.ToList(), p2.ToList());
        });
        // ИЗОГНУТАЯ РАЗВЁРТКА. Края граней в развёртке — прямые ряды точек, и ушная
        // триангуляция на них даёт веера и вынужденные вырожденные треугольники через всю
        // грань. Для триангуляции развёртка чуть изгибается: v' = v + ε·(u − u₀)², ε —
        // тысячная доля размера; ряды становятся строго выпуклыми. Точки на поверхности
        // ставятся по параметрам — изгиб лишь сдвигает их вдоль самой поверхности.
        {
            double x0 = outerS.Min(q => q.X), x1 = outerS.Max(q => q.X), y0 = outerS.Min(q => q.Y), y1 = outerS.Max(q => q.Y);
            double lx = Math.Max(x1 - x0, 1e-9), ly = Math.Max(y1 - y0, 1e-9);
            double amp = 1e-3 * Math.Max(lx, ly);
            Vector2d Warp(Vector2d q) { double t = (q.X - x0) / lx; return new Vector2d(q.X, q.Y + amp * t * t); }
            var wOuter = outerS.Select(Warp).ToList();
            var wHoles = holesS.Select(h => h.Select(Warp).ToList()).ToList();
            // Ключи lookup — исходные параметры: точки возвращаются обратным преобразованием.
            // Изгиб — только для триангуляции: полученные точки возвращаются в исходные
            // параметры обратным преобразованием. Иначе по изогнутым параметрам ставились точки
            // на поверхности, и при растяжении параметров по кривизне сетка уезжала (площадь
            // выходила на 45% больше, чем у самой поверхности).
            Vector2d Unwarp(Vector2d q) { double t = (q.X - x0) / lx; return new Vector2d(q.X, q.Y - amp * t * t); }
            candidates.Add(() =>
            {
                var t = Triangulator.Triangulate(wOuter, wHoles.Select(h => (IReadOnlyList<Vector2d>)h).ToList(), out var p2);
                return t.Count == 0 ? null : (t.ToList(), p2.Select(Unwarp).ToList());
            });
        }

        // Последней — триангуляция Делоне с ограничениями: она не боится рядов точек на прямой
        // и невыпуклых контуров, на которых ушная срывается в аварийный режим. Ставится в конец,
        // потому что на простых гранях проверенные способы дают сетку не хуже и быстрее.
        candidates.Add(() =>
        {
            KernelContext.Checkpoint("mesh.cdt");
            var t = Cdt.Triangulate(outerS, holesS.Select(h => (IReadOnlyList<Vector2d>)h).ToList(), out var p2);
            return t == null ? null : (t, p2);
        });

        // ПОРЯДОК СПОСОБОВ — «СПЕРВА ПРОСТЫЕ», И МЕНЯТЬ ЕГО НЕ СТОИТ. Пробовал подсказывать:
        // запоминать номер способа, сработавшего на поверхности такого же вида, и пробовать его
        // первым. Пустых попыток стало вдвое меньше (24 против 12), а время выросло — расчёт
        // граней 326 мс против 390.
        //
        // Причина: грани одного вида поверхности требуют разных способов, и запомненный
        // оказывается тяжёлым. Отказ простого способа стоит дешевле, чем лишняя работа сложного.
        string? lastWhy = "триангуляция в (u, v) не удалась";
        int candIdx = -1;

        foreach (var cand in candidates)
        {
            if (candIdx >= 0) TryDebug?.Invoke($"грань {f.Id}: способ {candIdx}: {lastWhy}");
            candIdx++;
            (List<int>, List<Vector2d>)? init;
            using (Diagnostics.Prof.Time($"сетка: способ {candIdx}")) init = cand();
            if (init == null) { TryDebug?.Invoke($"грань {f.Id}: способ {candIdx} не дал результата"); continue; }
            var (tris, pts2) = init.Value;
            var P = new List<Vector3d>(); var UV = new List<Vector2d>();
            foreach (var q in pts2)
            {
                UV.Add(q);                                           // масштабированные (u, v)
                P.Add(lookup.TryGetValue((q.X, q.Y), out var p3) ? p3 : src.PointAt(new Vector2d(q.X / su, q.Y / sv)));
            }
            var T = new List<int>(tris);
            // ПРИВЕДЕНИЕ ОБХОДА ТРЕУГОЛЬНИКОВ К ОДНОМУ ЗНАКУ — ПРОБОВАЛ, ОТКАТИЛ. Измерено:
            // триангулятор иногда возвращает треугольник, намотанный в другую сторону (у
            // пятиугольника вышло [4,2,3] [4,2,0] [0,2,1] — сторона 4→2 дважды в одну сторону).
            // Но разворот по общему знаку площади в развёртке сделал хуже: охват 89 → 84, а с
            // ограничением на непериодические грани — 86. Значит, знак развёртки не годится в
            // мерило: надо чинить сам триангулятор, чтобы он не выдавал такие треугольники.

            int strayAfterTri = StrayCount(P, T, outer3, holes3);
            if (strayAfterTri > 0 && StrayDebug != null)
            {
                // Отрезки контура, пройденные дважды (одни и те же концы, любое направление).
                var seg = new Dictionary<(double, double, double, double, double, double), int>();
                void Count(Vector3d a, Vector3d b)
                {
                    var k1 = (Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6), Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6));
                    var k2 = (Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6), Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6));
                    var k = string.CompareOrdinal(k1.ToString(), k2.ToString()) <= 0 ? k1 : k2;
                    seg[k] = seg.GetValueOrDefault(k) + 1;
                }
                for (int i = 0; i < outer3.Count; i++) Count(outer3[i], outer3[(i + 1) % outer3.Count]);
                foreach (var h in holes3) for (int i = 0; i < h.Count; i++) Count(h[i], h[(i + 1) % h.Count]);
                int twice = seg.Count(kv => kv.Value > 1);

                var msg = new System.Text.StringBuilder($"отростков в контуре {twice}; способ {candIdx}: лишних сторон {strayAfterTri}, кольцо {(per > 0 ? "да" : "нет")}, полоса {stripA}/{stripB}; ");
                var strayList = new List<(int X, int Y, int Fwd, int Bwd)>();
                var dirS = new Dictionary<(int, int), int>();
                for (int i = 0; i + 2 < T.Count; i += 3)
                    for (int k = 0; k < 3; k++) { var key = (T[i + k], T[i + (k + 1) % 3]); dirS[key] = dirS.GetValueOrDefault(key) + 1; }
                var linkD = new HashSet<(double, double, double, double, double, double)>();
                void AddL(Vector3d a, Vector3d b)
                {
                    linkD.Add((Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6), Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6)));
                    linkD.Add((Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6), Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6)));
                }
                for (int i = 0; i < outer3.Count; i++) AddL(outer3[i], outer3[(i + 1) % outer3.Count]);
                foreach (var h in holes3) for (int i = 0; i < h.Count; i++) AddL(h[i], h[(i + 1) % h.Count]);
                foreach (var ((x, y), n) in dirS)
                    if (n != dirS.GetValueOrDefault((y, x)))
                    {
                        var key = (Math.Round(P[x].X, 6), Math.Round(P[x].Y, 6), Math.Round(P[x].Z, 6), Math.Round(P[y].X, 6), Math.Round(P[y].Y, 6), Math.Round(P[y].Z, 6));
                        if (!linkD.Contains(key)) strayList.Add((x, y, n, dirS.GetValueOrDefault((y, x))));
                    }
                // Совпадают ли лишние стороны по КОНЦАМ (один отрезок, пройденный дважды).
                int samePairs = 0;
                for (int i = 0; i < strayList.Count; i++)
                    for (int j = i + 1; j < strayList.Count; j++)
                        if ((P[strayList[i].X] - P[strayList[j].X]).Length < 1e-6 && (P[strayList[i].Y] - P[strayList[j].Y]).Length < 1e-6) samePairs++;
                int dupUV = 0;
                for (int i = 0; i < outerS.Count; i++)
                    for (int j = i + 1; j < outerS.Count; j++)
                        if ((outerS[i] - outerS[j]).Length < 1e-9) dupUV++;
                if (stripA > 0 && stripA < outer3.Count)
                {
                    double seamLen = (outer3[0] - outer3[stripA]).Length;
                    msg.Append($"шов {seamLen:0.###}, лишние [{string.Join(",", strayList.Select(t => (P[t.X] - P[t.Y]).Length.ToString("0.###")))}]; ");
                }
                msg.Append($"точек контура {outer3.Count}, передано {outerS.Count}, слипшихся в развёртке {dupUV}; совпадающих пар {samePairs}; направления {string.Join(",", strayList.Select(t => t.Fwd + "/" + t.Bwd))}");
                if (outer3.Count == 4)
                {
                    msg.Append(" | контур: ");
                    for (int i = 0; i < outer3.Count; i++) msg.Append($"{i}({outer3[i].X:0.##},{outer3[i].Y:0.##},{outer3[i].Z:0.##}) ");
                    msg.Append("| треугольники: ");
                    for (int i = 0; i + 2 < T.Count; i += 3) msg.Append($"[{T[i]},{T[i + 1]},{T[i + 2]}] ");
                    msg.Append("| лишние: ");
                    foreach (var t in strayList) msg.Append($"{t.X}->{t.Y} ");
                }
                StrayDebug(msg.ToString());
            }
            if (strayAfterTri == 1 && outer3.Count <= 12 && holes3.Count == 0 && StrayDebug != null)
            {
                var sb = new System.Text.StringBuilder($"грань {f.Id}: способ {candIdx}, контур ({outer3.Count} точек) ");
                for (int i = 0; i < outer3.Count; i++) sb.Append($"{i}({outer3[i].X:0.##},{outer3[i].Y:0.##},{outer3[i].Z:0.##}) ");
                sb.Append("| точки сетки ");
                for (int i = 0; i < P.Count; i++) sb.Append($"{i}({P[i].X:0.##},{P[i].Y:0.##},{P[i].Z:0.##}) ");
                sb.Append("| треугольники ");
                for (int i = 0; i + 2 < T.Count; i += 3) sb.Append($"[{T[i]},{T[i + 1]},{T[i + 2]}] ");
                StrayDebug(sb.ToString());
            }
            using (Diagnostics.Prof.Time("сетка: перекладка")) Flip(UV, T, P);
            int strayAfterFlip = StrayCount(P, T, outer3, holes3);

            DumpUV?.Invoke(f, UV, T, "до");
            BoundaryCount = P.Count;
            KernelContext.Checkpoint("mesh.refine");
            using (Diagnostics.Prof.Time("сетка: уточнение")) Refine(src, P, UV, T, scale, seamU: per * su);
            if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   stage after-refine {DateTime.Now:HH:mm:ss.fff} T {T.Count}");
            int strayAfterRefine = StrayCount(P, T, outer3, holes3);
            // ПРОВЕРКА СВЯЗНОСТИ СЕТКИ ГРАНИ: непарных сторон должно быть ровно столько, сколько
            // звеньев в её контурах. Лишние — щели внутри грани.
            if (SeamDebug != null)
            {
                // Стороны считаются ПО КООРДИНАТАМ: у разрезанного кольца точка шва имеет два
                // номера, и сверка по номерам оставляла сторону без пары.
                var dirF = new Dictionary<((double, double, double), (double, double, double)), int>();
                var seen = new Dictionary<((double, double, double), (double, double, double)), (int, int)>();
                static (double, double, double) K(Vector3d p) => (Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6));
                for (int i = 0; i + 2 < T.Count; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var key = (K(P[T[i + k]]), K(P[T[i + (k + 1) % 3]]));
                        dirF[key] = dirF.GetValueOrDefault(key) + 1;
                        seen[key] = (T[i + k], T[i + (k + 1) % 3]);
                    }
                int unmatched = 0; double worstLen = 0;
                foreach (var ((ka2, kb2), n) in dirF)
                    if (n != dirF.GetValueOrDefault((kb2, ka2)))
                    {
                        unmatched++;
                        var (ia2, ib2) = seen[(ka2, kb2)];
                        worstLen = Math.Max(worstLen, (P[ia2] - P[ib2]).Length);
                    }
                int boundary = outer3.Count + holes3.Sum(h => h.Count);
                SeamDebug($"грань {f.Id}: непарных сторон {unmatched}, звеньев контуров {boundary}, самая длинная непарная {worstLen:0.###}");
            }

            // ПРОВЕРКА ШВА: обе его стороны должны быть в сетке, по одной в каждую сторону.
            if (SeamDebug != null && stripA > 0 && stripB > 0)
            {
                Vector3d sa = outer3[0], sb = outer3[stripA];              // A0 и её копия
                Vector3d sc = outer3[stripA + 1 - 1], sd = outer3[^1];
                int Count(Vector3d u, Vector3d v)
                {
                    int n = 0;
                    for (int i = 0; i + 2 < T.Count; i += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            Vector3d x = P[T[i + k]], y = P[T[i + (k + 1) % 3]];
                            if ((x - u).Length < 1e-6 && (y - v).Length < 1e-6) n++;
                        }
                    return n;
                }
                Vector3d p0 = outer3[stripA], p1 = outer3[stripA + 1];      // копия A0 → начало B
                Vector3d q0 = outer3[^1], q1 = outer3[0];                   // копия B0 → начало A
                SeamDebug($"грань {f.Id}: обходы [{string.Join(",", winding)}]; шов {(p0 - p1).Length:0.###} мм; сторона A→B {Count(p0, p1)}/{Count(p1, p0)}, сторона B→A {Count(q0, q1)}/{Count(q1, q0)}; точек полосы {outer3.Count}");
            }
            DumpUV?.Invoke(f, UV, T, "после");
            Orient(f, P, T);
            if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   stage after-orient {DateTime.Now:HH:mm:ss.fff}");
            // Кусок несвязной грани сверяется только по отходу: площадь — у грани в сборе.
            // Треугольник, перекинутый через шов периодической поверхности (соединяет края
            // развёртки), в пространстве идёт сквозь тело: способ отвергается, берётся следующий.
            // ТРЕУГОЛЬНИК ЧЕРЕЗ ШОВ ЧИНИТСЯ ДВОЙНИКОМ ВЕРШИНЫ, СТОРОНА — ПО СОСЕДЯМ. Треугольник,
            // охвативший период, замыкается вокруг цилиндра: часть его вершин стоит у одного края
            // развёртки, часть у другого. Опора берётся у СОСЕДНИХ нормальных треугольников (они
            // уже лежат по одну сторону шва): по собственной середине выбрать нельзя — у
            // треугольника, охватившего ровно период, обе стороны равноудалены, и сдвиги гасят
            // друг друга. Прежде грань отвергалась целиком (у трубы 31 треугольник из 6 237).
            if (per > 0)
            {
                double period = per * su;

                bool Spanning(int t) =>
                    Math.Max(UV[T[t]].X, Math.Max(UV[T[t + 1]].X, UV[T[t + 2]].X)) -
                    Math.Min(UV[T[t]].X, Math.Min(UV[T[t + 1]].X, UV[T[t + 2]].X)) > 0.5 * period;

                // Сторона треугольника → его номер, для поиска соседей.
                var sideOwner = new Dictionary<(int, int), int>();
                for (int t = 0; t + 2 < T.Count; t += 3)
                    for (int k = 0; k < 3; k++)
                        sideOwner[(T[t + k], T[t + (k + 1) % 3])] = t;

                // Середина грани по параметру: берётся по всем точкам, кроме пересекающих шов.
                var us = new List<double>();
                for (int t = 0; t + 2 < T.Count; t += 3)
                    if (!Spanning(t))
                        for (int k = 0; k < 3; k++) us.Add(UV[T[t + k]].X);
                if (us.Count == 0) return null;
                us.Sort();
                double faceAnchor = us[us.Count / 2];

                TwinOf ??= new Dictionary<int, int>();
                TwinOf.Clear();

                var twin = new Dictionary<(int, int), int>();
                // ПОВТОРЯЕМ ДО СХОЖДЕНИЯ. За один проход чинятся не все: у части треугольников
                // после первого сдвига размах всё ещё больше половины периода (у трубы оставалось
                // 17 из 31). Проходы идут, пока есть что чинить.
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   stage pass8 {DateTime.Now:HH:mm:ss.fff} T {T.Count}");
                for (int pass = 0; pass < 8; pass++)
                {
                bool fixedAny = false;
                for (int t = 0; t + 2 < T.Count; t += 3)
                {
                    if (!Spanning(t)) continue;

                    // ОПОРА — ВЕРШИНА САМОГО ТРЕУГОЛЬНИКА, ближайшая к середине грани, а остальные
                    // сводятся к ней. Сравнение с серединой грани не помогало тем треугольникам,
                    // чьи вершины лежат по разные стороны от неё: каждая близка к середине, а
                    // размах всё равно больше половины периода (у трубы таких оставалось 17).
                    int baseK = 0;
                    double bestD = double.MaxValue;
                    for (int k = 0; k < 3; k++)
                    {
                        double d = Math.Abs(UV[T[t + k]].X - faceAnchor);
                        if (d < bestD) { bestD = d; baseK = k; }
                    }
                    double anchor = UV[T[t + baseK]].X;

                    for (int k = 0; k < 3; k++)
                    {
                        if (k == baseK) continue;
                        int v = T[t + k];
                        double u = UV[v].X;
                        if (Math.Abs(u - anchor) <= 0.5 * period) continue;    // и так рядом с опорой

                        int shift = u < anchor ? +1 : -1;
                        if (!twin.TryGetValue((v, shift), out int t2))
                        {
                            t2 = P.Count;
                            P.Add(P[v]);
                            UV.Add(new Vector2d(u + shift * period, UV[v].Y));
                            twin[(v, shift)] = t2;
                            TwinOf[t2] = v;          // при счёте сторон двойник равен своей вершине
                        }
                        T[t + k] = t2;
                        fixedAny = true;
                    }
                }
                if (!fixedAny) break;
                }

                // ЧТО НЕ СВЕЛОСЬ СДВИГОМ — ДЕЛИТСЯ. Остаются крупные треугольники, охватившие
                // период целиком (у трубы три, площадями 1500 и 729 при обычных долях мм²): у них
                // ни одна вершина не сводится сдвигом. Такой треугольник делится серединой
                // длинной стороны, пока охват не исчезнет.
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   stage pass12 {DateTime.Now:HH:mm:ss.fff} T {T.Count}");
                for (int pass = 0; pass < 12; pass++)
                {
                    int at = -1;
                    for (int t = 0; t + 2 < T.Count; t += 3)
                        if (Spanning(t)) { at = t; break; }
                    if (at < 0) break;

                    int k0 = 0; double best = -1;
                    for (int k = 0; k < 3; k++)
                    {
                        double len = (UV[T[at + (k + 1) % 3]] - UV[T[at + k]]).LengthSquared;
                        if (len > best) { best = len; k0 = k; }
                    }
                    int a0 = T[at + k0], b0 = T[at + (k0 + 1) % 3], c0 = T[at + (k0 + 2) % 3];

                    Vector2d mUV = (UV[a0] + UV[b0]) / 2;
                    Vector3d mP = src.PointAt(new Vector2d(mUV.X / scale.X, mUV.Y / scale.Y));
                    int mid = P.Count;
                    P.Add(mP); UV.Add(mUV);

                    T[at] = a0; T[at + 1] = mid; T[at + 2] = c0;
                    T.Add(mid); T.Add(b0); T.Add(c0);
                }
            }

            bool spans = false;
            int spanCount = 0; double spanMax = 0;
            if (per > 0)
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    double uMin = Math.Min(UV[T[i]].X, Math.Min(UV[T[i + 1]].X, UV[T[i + 2]].X));
                    double uMax = Math.Max(UV[T[i]].X, Math.Max(UV[T[i + 1]].X, UV[T[i + 2]].X));
                    spanMax = Math.Max(spanMax, uMax - uMin);
                    if (uMax - uMin > 0.5 * per * su) { spans = true; spanCount++; }
                }
            // ВЫРОЖДЕННОЕ УХО ПЕРЕСТРАИВАЕТСЯ. В контуре встречается угол нулевой площади: две
            // грани стыкуются почти по прямой, и вершина стыка с двумя соседями по контуру даёт
            // треугольник высотой в доли микрона. Отбрасывать его нельзя (рвётся замкнутость),
            // отвергать грань — жалко. Такое ухо сливается с соседом через общую сторону, и
            // четырёхугольник разбивается по другой диагонали: вершина связывается с внутренней
            // точкой, площади становятся нормальными.
            int badBefore = UnmatchedSides(T);
            RepairSlivers(P, T, UV);

            // ХОРДА ВДОЛЬ КОНТУРА ДЕЛИТСЯ ПО ПОРЯДКУ ОБХОДА. Измерено: остаётся сторона между
            // точками края, перескакивающая через одну (52→50 через 51). Раньше деление шло по
            // геометрии («точка лежит на стороне») и не срабатывало: край кривой, и пропущенная
            // точка отстоит от хорды на прогиб.
            {
                var pos = new Dictionary<int, (int Loop, int Idx, int Count)>();
                void Mark(List<Vector3d> loop, int loopId)
                {
                    for (int i = 0; i < loop.Count; i++)
                        for (int j = 0; j < P.Count; j++)
                            if ((P[j] - loop[i]).Length < 1e-9 && !pos.ContainsKey(j)) { pos[j] = (loopId, i, loop.Count); break; }
                }
                Mark(outer3, 0);
                for (int h = 0; h < holes3.Count; h++) Mark(holes3[h], h + 1);

                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   stage pass3 {DateTime.Now:HH:mm:ss.fff} T {T.Count}");
                for (int pass = 0; pass < 3; pass++)
                {
                    // Делятся только НЕПАРНЫЕ стороны: соединять несоседние точки контура —
                    // обычная работа триангуляции, и деление всех подряд рушило сетку.
                    var dirR = new Dictionary<(int, int), int>();
                    for (int i = 0; i + 2 < T.Count; i += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            var kk = (T[i + k], T[i + (k + 1) % 3]);
                            dirR[kk] = dirR.GetValueOrDefault(kk) + 1;
                        }

                    bool split = false;
                    for (int i = 0; i + 2 < T.Count && !split; i += 3)
                        for (int k = 0; k < 3 && !split; k++)
                        {
                            int a = T[i + k], b = T[i + (k + 1) % 3], c = T[i + (k + 2) % 3];
                            if (dirR.GetValueOrDefault((a, b)) == dirR.GetValueOrDefault((b, a))) continue;
                            ChordSeen++;
                            if (!pos.TryGetValue(a, out var pa) || !pos.TryGetValue(b, out var pb)) { ChordNoPos++; continue; }
                            ChordHavePos++;
                            if (pa.Loop != pb.Loop) { ChordOtherLoop++; continue; }
                            int n = pa.Count;
                            int step = ((pb.Idx - pa.Idx) % n + n) % n;
                            // Соседние по контуру — законная граница; далёкие — не наш случай.
                            if (step < 2 || step > 3) { ChordStep++; continue; }
                            if (((pa.Idx - pb.Idx) % n + n) % n == 1) { ChordAdj++; continue; }   // обратное соседство

                            // Точки контура между ними, в порядке обхода.
                            var mids = new List<int>();
                            for (int m = 1; m < step; m++)
                            {
                                int want = (pa.Idx + m) % n;
                                int found = -1;
                                foreach (var (idx, pp) in pos)
                                    if (pp.Loop == pa.Loop && pp.Idx == want) { found = idx; break; }
                                if (found < 0) { mids.Clear(); break; }
                                mids.Add(found);
                            }
                            if (mids.Count == 0) continue;

                            T.RemoveAt(i + 2); T.RemoveAt(i + 1); T.RemoveAt(i);
                            int prev = a;
                            foreach (int m in mids) { T.Add(prev); T.Add(m); T.Add(c); prev = m; }
                            T.Add(prev); T.Add(b); T.Add(c);
                            split = true;
                            ChordSplit++;
                        }
                    if (!split) break;
                }
            }

            // ПРОБОВАЛ сводить совпадающие по координатам точки к одному номеру: у
            // разрезанного кольца точка шва попадает в набор дважды. Охват рухнул с 89 до 70:
            // у копий шва РАЗНЫЕ координаты в развёртке, и слияние номеров схлопывает полосу.

            if (StrayDebug != null)
            {
                int strayEnd = StrayCount(P, T, outer3, holes3);
                if (strayAfterTri + strayAfterFlip + strayAfterRefine + strayEnd > 0)
                    StrayDebug($"грань {f.Id}: лишних сторон после триангуляции {strayAfterTri}, после перекладки {strayAfterFlip}, после уточнения {strayAfterRefine}, после перестройки {strayEnd}");
            }
            int badAfter = UnmatchedSides(T);
            if (badAfter > badBefore) RepairDebug?.Invoke($"грань {f.Id}: перестройка ушей добавила непарных сторон {badBefore} → {badAfter}");

            // СВЯЗНОСТЬ СЕТКИ ГРАНИ: непарных сторон должно быть ровно столько, сколько звеньев
            // в её контурах. Лишняя непарная сторона — щель внутри грани: тело с такой сеткой
            // не замыкается, а способ триангуляции при этом проходил все прочие проверки
            // (площадь и отход в порядке — щель узкая).
            // СОСТАВ, А НЕ ТОЛЬКО ЧИСЛО. Непарные стороны сетки должны быть ровно звеньями
            // контуров. Прежде сверялось лишь количество, и грань с 159 непарными при 159
            // звеньях проходила, хотя одна сторона была лишней диагональю, а одно звено
            // контура — потеряно (тело потом не замыкалось).
            int gaps = 0, strayGaps = 0;
            HashSet<(double, double, double, double, double, double)> linkSet;
            {
                // Стороны считаются ПО КООРДИНАТАМ: у разрезанного кольца точка шва имеет два
                // номера, и две стороны шва — копии одного отрезка — по номерам не гасятся.
                static (double, double, double) Kc(Vector3d p) => (Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6));
                var dirC = new Dictionary<((double, double, double), (double, double, double)), int>();
                for (int i = 0; i + 2 < T.Count; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var kc = (Kc(P[T[i + k]]), Kc(P[T[i + (k + 1) % 3]]));
                        dirC[kc] = dirC.GetValueOrDefault(kc) + 1;
                    }
                var dirF = new Dictionary<(int, int), int>();
                for (int i = 0; i + 2 < T.Count; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var key = (Same(T[i + k]), Same(T[i + (k + 1) % 3]));
                        dirF[key] = dirF.GetValueOrDefault(key) + 1;
                    }

                // Звенья берутся по ВСЕМ контурам грани: у грани бывает несколько кусков, точки
                // и треугольники у них общие, и сверка только с контуром текущего куска давала
                // ложные «лишние стороны» (у контура из 4 точек треугольники ссылались на
                // точку 54 из соседнего куска).
                linkSet = new HashSet<(double, double, double, double, double, double)>();
                void AddLink(Vector3d a, Vector3d b)
                {
                    linkSet.Add((Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6),
                                 Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6)));
                    linkSet.Add((Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Z, 6),
                                 Math.Round(a.X, 6), Math.Round(a.Y, 6), Math.Round(a.Z, 6)));
                }
                var allCycles = Cycles(f);
                if (allCycles != null)
                    foreach (var cyc in allCycles)
                    {
                        var pts = new List<Vector3d>();
                        foreach (var (e, rev) in cyc)
                        {
                            var pe = EdgePoints(e);
                            for (int i = 0; i + 1 < pe.Count; i++) pts.Add(rev ? pe[pe.Count - 1 - i] : pe[i]);
                        }
                        for (int i = 0; i < pts.Count; i++) AddLink(pts[i], pts[(i + 1) % pts.Count]);
                    }
                for (int i = 0; i < outer3.Count; i++) AddLink(outer3[i], outer3[(i + 1) % outer3.Count]);
                foreach (var h in holes3)
                    for (int i = 0; i < h.Count; i++) AddLink(h[i], h[(i + 1) % h.Count]);

                // ПРОБОВАЛ разрешать стороны, лежащие на шве разрезанного кольца (уточнение
                // дробит шов на части, и они не совпадают со звеньями контура). Измерено:
                // расклад причин не изменился, а построение сетки подорожало с 50.7 до 61.2 с.

                // Сторона шва в контур не входит и потому считается лишней. Пробовал
                // разрешать такие стороны двумя способами: по принадлежности прямой шва в
                // пространстве и по краю окна u в развёртке. Ни один не совпал: точки
                // уточнения лежат на поверхности, а не на прямой, и координаты в рабочем
                // наборе масштабированы и изогнуты. Расклад причин от обеих попыток не
                // изменился (вне контура 13).

                foreach (var ((x, y), n) in dirF)
                    if (n != dirF.GetValueOrDefault((y, x)))
                    {
                        gaps++;
                        var key = (Math.Round(P[x].X, 6), Math.Round(P[x].Y, 6), Math.Round(P[x].Z, 6),
                                   Math.Round(P[y].X, 6), Math.Round(P[y].Y, 6), Math.Round(P[y].Z, 6));
                        // Сторона, у которой есть встречная ПО КООРДИНАТАМ, — это пара копий шва,
                        // и она законна.
                        var kc1 = (Kc(P[x]), Kc(P[y]));
                        var kc2 = (Kc(P[y]), Kc(P[x]));
                        if (dirC.GetValueOrDefault(kc1) == dirC.GetValueOrDefault(kc2)) continue;
                        if (!linkSet.Contains(key)) strayGaps++;
                    }
            }
            int links = outer3.Count + holes3.Sum(h => h.Count);

            // ВСЕ ТОЧКИ КОНТУРА ДОЛЖНЫ БЫТЬ ИСПОЛЬЗОВАНЫ. Триангуляция иногда пропускает точку,
            // лежащую на прямой между соседями: грань берёт ребро одной хордой, а соседняя —
            // по всем точкам, и между ними остаётся щель (Т-образный стык).
            // Пропущенная точка контура ВСТАВЛЯЕТСЯ в ту сторону, на которой лежит: грань берёт
            // ребро одной хордой, а соседняя — по всем точкам, и между ними остаётся щель.
            int unused = InsertUnusedBoundary(P, T);

            // Невставленная точка — НЕ отказ. Измерено: в контуре встречается отросток к
            // отрезанному углу (точка в 8 мм от накрытой области, без двойника), и
            // триангуляция правильно его не накрывает.
            // Без придирок (обменные файлы): сетка берётся как есть — иначе грань останется
            // со своей отдельной сеткой и разойдётся с соседями.
            // Ветка про невставленные точки контура когда-то отключалась через «else if (false)»
            // — это давало предупреждение о недостижимом коде. Условие убрано: проверка по
            // непарным сторонам ниже покрывает тот же случай.
            if (Lenient) lastWhy = null;
            else if (strayGaps > 0)
            {
                // Выгрузка ИТОГОВОЙ сетки грани в OBJ (после уточнения) — для разглядывания.
                if (ObjDumpPath != null && !System.IO.File.Exists(ObjDumpPath) && !src.IsPlanar)
                {
                    using var w9 = new System.IO.StreamWriter(ObjDumpPath);
                    w9.WriteLine($"# грань {f.Id}: итоговая сетка, {T.Count / 3} треугольников");
                    foreach (var q9 in P) w9.WriteLine($"v {q9.X:0.######} {q9.Y:0.######} {q9.Z:0.######}");
                    // Отход треугольников от поверхности против допуска уточнения.
                    double worstDev = 0, worstArea = 0;
                    for (int i9 = 0; i9 + 2 < T.Count; i9 += 3)
                    {
                        Vector3d g9 = (P[T[i9]] + P[T[i9 + 1]] + P[T[i9 + 2]]) / 3;
                        double d9 = (src.PointAt(src.ProjectToUV(g9)) - g9).Length;
                        double a9 = Vector3d.Cross(P[T[i9 + 1]] - P[T[i9]], P[T[i9 + 2]] - P[T[i9]]).Length / 2;
                        if (d9 > worstDev) { worstDev = d9; worstArea = a9; }
                    }
                    SeamDebug?.Invoke($"грань {f.Id}: наибольший отход {worstDev:0.####} мм при площади треугольника {worstArea:0.##}, допуск уточнения {LastTol:0.####}, проходов {RefinePasses}, осталось негодных {RefineBad}, вставлено {RefineAdded}, отклонено повторной проверкой {SkipRecheck}, отклонено по близости {SkipMicro}, не найдено заново {SkipLost}, пропущено как мелкое {SkipTiny}");
                    w9.WriteLine("g mesh");
                    for (int i9 = 0; i9 + 2 < T.Count; i9 += 3)
                        w9.WriteLine($"f {T[i9] + 1} {T[i9 + 1] + 1} {T[i9 + 2] + 1}");
                    // Найти лишнюю сторону заново: пара без встречной и не звено контура.
                    var dirD = new Dictionary<(int, int), int>();
                    for (int i9 = 0; i9 + 2 < T.Count; i9 += 3)
                        for (int k9 = 0; k9 < 3; k9++)
                        {
                            var kk = (T[i9 + k9], T[i9 + (k9 + 1) % 3]);
                            dirD[kk] = dirD.GetValueOrDefault(kk) + 1;
                        }
                    w9.WriteLine("g straySide");
                    foreach (var ((xa, xb), nn) in dirD)
                    {
                        if (nn == dirD.GetValueOrDefault((xb, xa))) continue;
                        var kk2 = (Math.Round(P[xa].X, 6), Math.Round(P[xa].Y, 6), Math.Round(P[xa].Z, 6),
                                   Math.Round(P[xb].X, 6), Math.Round(P[xb].Y, 6), Math.Round(P[xb].Z, 6));
                        if (linkSet.Contains(kk2)) continue;
                        w9.WriteLine($"l {xa + 1} {xb + 1}");
                    }
                }

                // Где лежит лишняя сторона: расстояния её концов до углов полосы.
                string where = "";
                if (stripA > 0 && stripA < outer3.Count)
                {
                    var dirW = new Dictionary<(int, int), int>();
                    for (int i = 0; i + 2 < T.Count; i += 3)
                        for (int k = 0; k < 3; k++) { var key = (T[i + k], T[i + (k + 1) % 3]); dirW[key] = dirW.GetValueOrDefault(key) + 1; }
                    foreach (var ((x, y), n) in dirW)
                    {
                        if (n == dirW.GetValueOrDefault((y, x))) continue;
                        var key = (Math.Round(P[x].X, 6), Math.Round(P[x].Y, 6), Math.Round(P[x].Z, 6),
                                   Math.Round(P[y].X, 6), Math.Round(P[y].Y, 6), Math.Round(P[y].Z, 6));
                        if (linkSet.Contains(key)) continue;
                        double[] d =
                        {
                            (P[x] - outer3[0]).Length, (P[x] - outer3[stripA - 1]).Length,
                            (P[x] - outer3[stripA]).Length, (P[x] - outer3[^1]).Length
                        };
                        double[] e =
                        {
                            (P[y] - outer3[0]).Length, (P[y] - outer3[stripA - 1]).Length,
                            (P[y] - outer3[stripA]).Length, (P[y] - outer3[^1]).Length
                        };
                        // Треугольники, содержащие концы лишней стороны, и их площади.
                        var around = new List<string>();
                        for (int i = 0; i + 2 < T.Count; i += 3)
                        {
                            if (T[i] != x && T[i + 1] != x && T[i + 2] != x && T[i] != y && T[i + 1] != y && T[i + 2] != y) continue;
                            double ar = Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]).Length / 2;
                            around.Add($"[{T[i]},{T[i + 1]},{T[i + 2]}]:{ar:0.###}");
                            if (around.Count >= 6) break;
                        }
                        // Используются ли граничные точки между концами лишней стороны.
                        string gapPts = "";
                        if (x < BoundaryCount && y < BoundaryCount)
                        {
                            int lo = Math.Min(x, y), hi = Math.Max(x, y);
                            var used = new bool[P.Count];
                            foreach (int idx in T) if (idx < used.Length) used[idx] = true;
                            var between = new List<string>();
                            for (int i = lo + 1; i < hi && i < BoundaryCount && between.Count < 6; i++)
                                between.Add($"{i}:{(used[i] ? "есть" : "нет")}");
                            gapPts = $"; между концами точки {string.Join(",", between)}";
                        }
                        // На одном ли контуре лежат концы лишней стороны.
                        int loopOf(Vector3d q)
                        {
                            for (int li = 0; li < loops3.Count; li++)
                                foreach (var z in loops3[li])
                                    if ((z - q).Length < 1e-6) return li;
                            return -1;
                        }
                        // Встречаемость отрезка ПО КООРДИНАТАМ (номера точек могут двоиться).
                        int fwdXY = 0, bwdXY = 0;
                        for (int i2 = 0; i2 + 2 < T.Count; i2 += 3)
                            for (int k2 = 0; k2 < 3; k2++)
                            {
                                Vector3d u2 = P[T[i2 + k2]], v2 = P[T[i2 + (k2 + 1) % 3]];
                                if ((u2 - P[x]).Length < 1e-6 && (v2 - P[y]).Length < 1e-6) fwdXY++;
                                if ((u2 - P[y]).Length < 1e-6 && (v2 - P[x]).Length < 1e-6) bwdXY++;
                            }
                        int la = loopOf(P[x]), lb = loopOf(P[y]);
                        // Площадь сетки против площади по контурам (в развёртке).
                        double aMesh = 0;
                        for (int i3 = 0; i3 + 2 < T.Count; i3 += 3)
                            aMesh += Vector3d.Cross(P[T[i3 + 1]] - P[T[i3]], P[T[i3 + 2]] - P[T[i3]]).Length / 2;
                        // Правильная мерка: площадь новой сетки против площади ПРЕЖНИХ
                        // треугольников грани, и средний размер треугольника — чтобы понять,
                        // потерян один треугольник или целый кусок.
                        double aOldF = 0;
                        int oldCount = 0;
                        if (f.Surface is FacetedSurface fsOld)
                        {
                            foreach (var (x4, y4, z4) in fsOld.Facets) aOldF += Vector3d.Cross(y4 - x4, z4 - x4).Length / 2;
                            oldCount = fsOld.Facets.Count;
                        }
                        double avgNew = T.Count >= 3 ? aMesh / (T.Count / 3) : 0;
                        // Складки в развёртке: пары треугольников, перекрывающихся по площади.
                        int overlaps = 0;
                        double overlapArea = 0;
                        if (UV.Count >= P.Count)
                        {
                            static double Cr(Vector2d p1, Vector2d p2, Vector2d p3) => (p2.X - p1.X) * (p3.Y - p1.Y) - (p2.Y - p1.Y) * (p3.X - p1.X);
                            var cen = new List<(Vector2d C, double R, int I)>();
                            for (int i5 = 0; i5 + 2 < T.Count; i5 += 3)
                            {
                                Vector2d a5 = UV[T[i5]], b5 = UV[T[i5 + 1]], c5 = UV[T[i5 + 2]];
                                Vector2d cc = new((a5.X + b5.X + c5.X) / 3, (a5.Y + b5.Y + c5.Y) / 3);
                                double r5 = Math.Max((a5 - cc).Length, Math.Max((b5 - cc).Length, (c5 - cc).Length));
                                cen.Add((cc, r5, i5));
                            }
                            for (int i5 = 0; i5 < cen.Count && overlaps < 40; i5++)
                                for (int j5 = i5 + 1; j5 < cen.Count && overlaps < 40; j5++)
                                {
                                    if ((cen[i5].C - cen[j5].C).Length > cen[i5].R + cen[j5].R) continue;
                                    // Центр одного внутри другого — признак наложения.
                                    Vector2d g5 = cen[i5].C;
                                    Vector2d x5 = UV[T[cen[j5].I]], y5 = UV[T[cen[j5].I + 1]], z5 = UV[T[cen[j5].I + 2]];
                                    double s1 = Cr(x5, y5, g5), s2 = Cr(y5, z5, g5), s3 = Cr(z5, x5, g5);
                                    if ((s1 > 0 && s2 > 0 && s3 > 0) || (s1 < 0 && s2 < 0 && s3 < 0))
                                    {
                                        overlaps++;
                                        overlapArea += Math.Abs(Cr(x5, y5, z5)) / 2;
                                    }
                                }
                        }

                        // Площади В РАЗВЁРТКЕ: треугольники против многоугольника (внешний
                        // контур минус отверстия). Здесь величины сопоставимы, и дыра видна.
                        double uvTris = 0, uvPoly = 0;
                        if (UV.Count >= P.Count)
                        {
                            for (int i6 = 0; i6 + 2 < T.Count; i6 += 3)
                            {
                                Vector2d a6 = UV[T[i6]], b6 = UV[T[i6 + 1]], c6 = UV[T[i6 + 2]];
                                uvTris += Math.Abs((b6.X - a6.X) * (c6.Y - a6.Y) - (b6.Y - a6.Y) * (c6.X - a6.X)) / 2;
                            }
                            uvPoly = Math.Abs(Area(outerS));
                            foreach (var h6 in holesS) uvPoly -= Math.Abs(Area(h6));
                        }

                        // Длина полосы по параметру против периода поверхности.
                        string period = "";
                        if (outerS.Count > 0)
                        {
                            double uMin = outerS.Min(z => z.X), uMax = outerS.Max(z => z.X);
                            period = $"; окно по u {(uMax - uMin) / (su <= 0 ? 1 : su):0.#####}, период {per:0.#####}, обходы [{string.Join(",", winding)}]";
                        }

                        // Треугольники, примыкающие к лишней стороне с обеих сторон, и их вершины
                        // в развёртке и в пространстве.
                        {
                            var touch = new List<string>();
                            for (int i8 = 0; i8 + 2 < T.Count && touch.Count < 4; i8 += 3)
                            {
                                bool hasX = T[i8] == x || T[i8 + 1] == x || T[i8 + 2] == x;
                                bool hasY = T[i8] == y || T[i8 + 1] == y || T[i8 + 2] == y;
                                if (!hasX || !hasY) continue;
                                touch.Add($"[{T[i8]},{T[i8 + 1]},{T[i8 + 2]}]");
                            }
                            // Точки, совпадающие по координатам с концами (копии).
                            int copiesX = 0, copiesY = 0;
                            for (int i8 = 0; i8 < P.Count; i8++)
                            {
                                if (i8 != x && (P[i8] - P[x]).Length < 1e-6) copiesX++;
                                if (i8 != y && (P[i8] - P[y]).Length < 1e-6) copiesY++;
                            }
                            gapPts = $"; у стороны треугольников {touch.Count}: {string.Join(" ", touch)}; копий начала {copiesX}, копий конца {copiesY}" + gapPts;
                        }

                        // Шаг по контуру между концами лишней стороны.
                        {
                            int PosOf(int idx, out int loopId, out int cnt)
                            {
                                loopId = -1; cnt = 0;
                                for (int li = 0; li < loops3.Count; li++)
                                    for (int i9 = 0; i9 < loops3[li].Count; i9++)
                                        if ((loops3[li][i9] - P[idx]).Length < 1e-9) { loopId = li; cnt = loops3[li].Count; return i9; }
                                return -1;
                            }
                            int ia = PosOf(x, out int la9, out int na9), ib = PosOf(y, out int lb9, out int nb9);
                            string stepInfo = la9 >= 0 && la9 == lb9 && na9 > 0
                                ? $"шаг по контуру {Math.Min(((ib - ia) % na9 + na9) % na9, ((ia - ib) % na9 + na9) % na9)} из {na9}"
                                : $"концы на разных контурах ({la9}/{lb9})";
                            gapPts = $"; {stepInfo}" + gapPts;
                        }

                        // Какие точки краёв полосы использованы в треугольниках.
                        if (stripA > 0 && stripA < outer3.Count)
                        {
                            var used9 = new bool[P.Count];
                            foreach (int idx in T) if (idx < used9.Length) used9[idx] = true;
                            int usedA = 0, usedB = 0;
                            for (int i9 = 0; i9 < stripA && i9 < P.Count; i9++) if (used9[i9]) usedA++;
                            for (int i9 = stripA; i9 < outer3.Count && i9 < P.Count; i9++) if (used9[i9]) usedB++;
                            // Расстояния между пропущенной точкой и её соседями по краю.
                            string near3 = "";
                            if (x < outer3.Count && y < outer3.Count && Math.Abs(x - y) == 2)
                            {
                                int mid = (x + y) / 2;
                                near3 = $"; пропущена точка {mid}: в пространстве до {x} {(P[mid] - P[x]).Length:0.#####}, до {y} {(P[mid] - P[y]).Length:0.#####}; " +
                                        $"в развёртке до {x} {(UV[mid] - UV[x]).Length:0.#####}, до {y} {(UV[mid] - UV[y]).Length:0.#####}";
                            }
                            gapPts = near3 + $"; край A использовано {usedA} из {stripA}, край B {usedB} из {outer3.Count - stripA}; " +
                                     $"начало стороны {x} (край {(x < stripA ? "A" : x < outer3.Count ? "B" : "вставка")}), " +
                                     $"конец {y} (край {(y < stripA ? "A" : y < outer3.Count ? "B" : "вставка")})" + gapPts;
                        }

                        // Выгрузка развёртки для разглядывания (один раз).
                        if (UVDumpPath != null && !System.IO.File.Exists(UVDumpPath) && UV.Count >= P.Count)
                        {
                            using var w7 = new System.IO.StreamWriter(UVDumpPath);
                            w7.WriteLine($"# stray {x} {y}");
                            for (int i7 = 0; i7 + 2 < T.Count; i7 += 3)
                                w7.WriteLine($"{UV[T[i7]].X} {UV[T[i7]].Y} {UV[T[i7 + 1]].X} {UV[T[i7 + 1]].Y} {UV[T[i7 + 2]].X} {UV[T[i7 + 2]].Y}");
                            w7.WriteLine("# side");
                            w7.WriteLine($"{UV[x].X} {UV[x].Y} {UV[y].X} {UV[y].Y}");
                        }

                        Vector3d nSum = Vector3d.Zero;
                        int flippedN = 0;
                        for (int i4 = 0; i4 + 2 < T.Count; i4 += 3) nSum += Vector3d.Cross(P[T[i4 + 1]] - P[T[i4]], P[T[i4 + 2]] - P[T[i4]]);
                        Vector3d nAvgF = nSum.LengthSquared > 1e-18 ? nSum.Normalized() : Vector3d.UnitZ;
                        double aFlipped = 0;
                        for (int i4 = 0; i4 + 2 < T.Count; i4 += 3)
                        {
                            Vector3d nt = Vector3d.Cross(P[T[i4 + 1]] - P[T[i4]], P[T[i4 + 2]] - P[T[i4]]);
                            if (Vector3d.Dot(nt, nAvgF) >= 0) continue;
                            flippedN++; aFlipped += nt.Length / 2;
                        }
                        gapPts = $"; по координатам вперёд {fwdXY}, назад {bwdXY}; площадь новая {aMesh:0.##}, прежняя {aOldF:0.##}, " +
                                 $"в развёртке треугольники {uvTris:0.####}, многоугольник {uvPoly:0.####}, разница {uvTris - uvPoly:0.######}{period}" + gapPts;
                        string loopInfo = $"; концы на контурах {la}/{lb} из {loops3.Count}";
                        where = $"{loopInfo}{gapPts}; треугольники у стороны {string.Join(" ", around)}; длина {(P[x] - P[y]).Length:0.###}, начало до углов [{string.Join(",", d.Select(v => v.ToString("0.##")))}], конец [{string.Join(",", e.Select(v => v.ToString("0.##")))}]";
                        break;
                    }
                }
                // Устройство полосы: длины краёв, число точек, расстояния между началами.
                string strip = "";
                if (stripA > 0 && stripA < outer3.Count)
                {
                    double lenA = 0, lenB = 0;
                    for (int i = 0; i + 1 < stripA; i++) lenA += (outer3[i + 1] - outer3[i]).Length;
                    for (int i = stripA; i + 1 < outer3.Count; i++) lenB += (outer3[i + 1] - outer3[i]).Length;
                    strip = $"; край A точек {stripA} длиной {lenA:0.###}, край B точек {outer3.Count - stripA} длиной {lenB:0.###}, " +
                            $"начала краёв на {(outer3[0] - outer3[stripA]).Length:0.####}, концы на {(outer3[stripA - 1] - outer3[^1]).Length:0.####}";
                }
                lastWhy = $"непарная сторона вне контура: {strayGaps} из {gaps}; способ {candIdx}; кольцо {(per > 0 ? "да" : "нет")}, полоса {stripA}/{stripB}, контуров {loops3.Count}{strip}{where}";
            }
            else if (gaps > links) lastWhy = $"щель в грани: непарных сторон {gaps} при {links} звеньях контуров";
            else if (spans)
            {
                var det = new List<string>();
                for (int i = 0; i + 2 < T.Count && det.Count < 3; i += 3)
                {
                    double uMin = Math.Min(UV[T[i]].X, Math.Min(UV[T[i + 1]].X, UV[T[i + 2]].X));
                    double uMax = Math.Max(UV[T[i]].X, Math.Max(UV[T[i + 1]].X, UV[T[i + 2]].X));
                    if (uMax - uMin <= 0.5 * per * su) continue;
                    double ar = Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]).Length / 2;
                    det.Add($"площадь {ar:0.#####}, u ({UV[T[i]].X:0.##},{UV[T[i + 1]].X:0.##},{UV[T[i + 2]].X:0.##})");
                }
                lastWhy = $"треугольник через шов периода: таких {spanCount} из {T.Count / 3}, " +
                          $"наибольший размах {spanMax:0.####} при периоде {per * su:0.####}; " + string.Join("; ", det);
            }
            else if (Validate(f, src, P, T, out lastWhy, checkArea: !grouped, boundaryDev: LastTol / 1.05) != null)
                {
                    // Нормали — из точной поверхности по параметрам точек.
                    var N = new List<Vector3d>(P.Count);
                    for (int i = 0; i < P.Count; i++)
                    {
                        Vector3d n = src.NormalAt(new Vector2d(UV[i].X / su, UV[i].Y / sv));
                        N.Add(n.LengthSquared > 1e-18 ? n.Normalized() : Vector3d.Zero);
                    }
                    return (P, T, true, N);
                }
            TryDebug?.Invoke($"грань {f.Id}: способ дал {T.Count / 3} тр., порог уточнения {LastTol:0.####}, отказ: {lastWhy}");
        }
        why = lastWhy;
        return null;
    }

    /// <summary>
    /// Ориентация — по треугольникам разбиения: ближайший к центру первого треугольника;
    /// у грани без них — по нормали грани (с её флагом Reversed).
    /// </summary>
    private static void Orient(TFace f, List<Vector3d> P, List<int> T)
    {
        using var _po = Diagnostics.Prof.Time("сетка: ориентация");
        // Решает самый крупный треугольник (первый бывает вырожденным).
        int big = 0; double bigA = -1;
        for (int i = 0; i + 2 < T.Count; i += 3)
        {
            double ar = Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]).LengthSquared;
            if (ar > bigA) { bigA = ar; big = i; }
        }
        if (big != 0) for (int k = 0; k < 3; k++) (T[k], T[big + k]) = (T[big + k], T[k]);
        if (f.Surface is not FacetedSurface && T.Count >= 3)
        {
            Vector3d a0 = P[T[0]], b0 = P[T[1]], c0 = P[T[2]];
            Vector3d nMine = Vector3d.Cross(b0 - a0, c0 - a0);
            if (Vector3d.Dot(nMine, f.NormalAt((a0 + b0 + c0) / 3)) < 0)
                for (int i = 0; i + 2 < T.Count; i += 3) (T[i + 1], T[i + 2]) = (T[i + 2], T[i + 1]);
        }
        if (f.Surface is FacetedSurface fsa && fsa.Facets.Count > 0 && T.Count >= 3)
        {
            // СТОРОНА — ПО БОЛЬШИНСТВУ ТРЕУГОЛЬНИКОВ, С ВЕСОМ ПЛОЩАДИ. Решал один самый крупный
            // треугольник по ближайшему прежнему; на мелкой грани из трёх треугольников это
            // промахивалось (один за, два против), грань выходила вывернутой, и её стороны
            // конфликтовали с соседними — тело не замыкалось.
            double vote = 0;
            // K07: ближайший прежний треугольник ищется по сетке ячеек, а не перебором: перебор
            // «каждый новый × каждый прежний» стоил 22 мс из 72 у плоской грани с восемью
            // отверстиями. Результат тот же: то же расстояние, при равенстве — меньший номер.
            // K07: у плоской грани, все прежние треугольники которой смотрят в одну сторону,
            // ближайший прежний треугольник всегда даёт одну и ту же нормаль — искать его незачем.
            Vector3d? common = null;
            if (fsa.Source?.IsPlanar == true)
            {
                Vector3d n0 = Vector3d.Zero; double best0 = -1;
                foreach (var (x0, y0, z0) in fsa.Facets)
                {
                    var nn = Vector3d.Cross(y0 - x0, z0 - x0);
                    if (nn.LengthSquared > best0) { best0 = nn.LengthSquared; n0 = nn; }
                }
                bool same = best0 > 1e-24;
                if (same)
                {
                    var u0 = n0.Normalized();
                    foreach (var (x0, y0, z0) in fsa.Facets)
                    {
                        var nn = Vector3d.Cross(y0 - x0, z0 - x0);
                        if (nn.LengthSquared < 1e-24) continue;
                        // Совпадение направлений с точностью округления (плоская грань).
                        if (Vector3d.Dot(nn.Normalized(), u0) < 1 - 1e-9) { same = false; break; }
                    }
                }
                if (same) common = n0;
            }
            var near = common == null ? new NearestCentroid(fsa.Facets.ReadView) { MaxRings = 6 } : null;
            for (int i = 0; i + 2 < T.Count; i += 3)
            {
                if ((i & 3071) == 0) KernelContext.Checkpoint("mesh.orient");   // прерываемо: у целого тора — минуты
                Vector3d a0 = P[T[i]], b0 = P[T[i + 1]], c0 = P[T[i + 2]];
                Vector3d nMine = Vector3d.Cross(b0 - a0, c0 - a0);
                double area = nMine.Length / 2;
                if (area < 1e-12) continue;
                Vector3d mid = (a0 + b0 + c0) / 3;
                Vector3d nRef;
                if (common is { } cn) nRef = cn;
                else
                {
                    int at = near!.Find(mid);
                    if (at < 0) continue;                        // прежних треугольников рядом нет — голос не подаётся
                    var (x, y, z) = fsa.Facets[at];
                    nRef = Vector3d.Cross(y - x, z - x);
                }
                if (nRef.LengthSquared < 1e-24) continue;
                vote += Vector3d.Dot(nMine.Normalized(), nRef.Normalized()) * area;
            }
            if (vote < 0)
                for (int i = 0; i + 2 < T.Count; i += 3) (T[i + 1], T[i + 2]) = (T[i + 2], T[i + 1]);
        }
    }


    /// <summary>
    /// Поиск ближайшего центра треугольника по равномерной сетке ячеек. Отвечает тем же, что
    /// перебор: наименьший квадрат расстояния, при равенстве — наименьший номер.
    /// </summary>
    private sealed class NearestCentroid
    {
        private readonly Vector3d[] _c;
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();
        private readonly Vector3d _lo;
        private readonly double _h;
        private readonly (int X, int Y, int Z) _n;
        /// <summary>Предел колец поиска, если в них ничего нет (0 — без предела): ответ −1 — «рядом прежних нет».</summary>
        public int MaxRings;

        public NearestCentroid(List<(Vector3d A, Vector3d B, Vector3d C)> tris)
        {
            _c = new Vector3d[tris.Count];
            var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
            for (int i = 0; i < tris.Count; i++)
            {
                var (x, y, z) = tris[i];
                _c[i] = (x + y + z) / 3;
                lo = Vector3d.ComponentMin(lo, _c[i]); hi = Vector3d.ComponentMax(hi, _c[i]);
            }
            _lo = lo;
            var ext = hi - lo;
            double diag = Math.Max(ext.Length, 1e-9);
            _h = Math.Max(diag / Math.Max(1, Math.Cbrt(tris.Count) * 2), 1e-9);
            _n = ((int)Math.Ceiling(ext.X / _h) + 1, (int)Math.Ceiling(ext.Y / _h) + 1, (int)Math.Ceiling(ext.Z / _h) + 1);
            for (int i = 0; i < _c.Length; i++)
            {
                var k = Cell(_c[i]);
                if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>();
                l.Add(i);
            }
        }

        private (int, int, int) Cell(Vector3d p) =>
            ((int)Math.Floor((p.X - _lo.X) / _h), (int)Math.Floor((p.Y - _lo.Y) / _h), (int)Math.Floor((p.Z - _lo.Z) / _h));

        public int Find(Vector3d q)
        {
            var (cx, cy, cz) = Cell(q);
            int best = -1; double bestD = double.MaxValue;
            // g.05/KRN-10: кольца — не дальше границ сетки ячеек. Прежде предел был «число ячеек + 66» колец (O(r³) ячеек на
            // запрос): точка вне прежней сетки (целый тор против прежнего куска) искалась минутами — разностная серия 8, случай 96.
            int rmax = Math.Max(Math.Max(Math.Max(Math.Abs(cx), Math.Abs(cx - _n.X)), Math.Max(Math.Abs(cy), Math.Abs(cy - _n.Y))), Math.Max(Math.Abs(cz), Math.Abs(cz - _n.Z))) + 1;
            for (int r = 0; ; r++)
            {
                if (r > rmax || (MaxRings > 0 && r > MaxRings && best < 0)) return best >= 0 ? best : -1;
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                        for (int dz = -r; dz <= r; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                            if (!_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                            foreach (int i in l)
                            {
                                double d = (_c[i] - q).LengthSquared;
                                if (d < bestD || (d == bestD && i < best)) { bestD = d; best = i; }
                            }
                        }
                // Всё, что дальше кольца r, отстоит не меньше чем на r·h от ячейки запроса.
                if (best >= 0 && bestD < (r * _h) * (r * _h)) return best;
                if (r > 2 + _cells.Count + 64) return best >= 0 ? best : 0;
            }
        }
    }

    /// <summary>
    /// Дробление внутренних рёбер: середина ребра в (u, v) на поверхности дальше допуска
    /// гранения от середины хорды — ребро делится, с ним оба треугольника. Рёбра границы
    /// (принадлежащие одному треугольнику) не делятся: на них точки ребра тела.
    /// </summary>
    static readonly bool DupInsOff = Environment.GetEnvironmentVariable("P3D_MESHDUPINS") == "0";
    static readonly bool FlipCheckOff = Environment.GetEnvironmentVariable("P3D_MESHFLIPCHECK") == "0";
    static readonly bool RefineCacheOff = Environment.GetEnvironmentVariable("P3D_REFINECACHE") == "0";

    private static void Refine(ISurface3 src, List<Vector3d> P, List<Vector2d> UV, List<int> T, Vector2d scale, double seamU = 0)
    {
        // ПОРОГ — НЕ СТРОЖЕ, ЧЕМ НА КРАЯХ ГРАНИ. Края гранятся по шагу угла (у окружности R20
        // прогиб хорды 0.043 мм при допуске 0.02); диагональ с тем же шагом по углу отходит
        // так же, и деление пополам вдоль образующей его не уменьшает — дробление шло до
        // предела длины (17 тысяч треугольников на стенке отверстия).
        double tol = Tol.Chord;
        {
            var cnt = new Dictionary<(int, int), int>();
            for (int t = 0; t + 2 < T.Count; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = T[t + k], b = T[t + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    cnt[key] = cnt.GetValueOrDefault(key) + 1;
                }
            foreach (var (key, c) in cnt)
            {
                if (c != 1) continue;
                var (a, b) = key;
                Vector2d m = (UV[a] + UV[b]) / 2;
                // Стороны шва периода (скачок u почти на период) — не хорды грани, их прогиб
                // не считается. Прежде отбрасывались все стороны с прогибом больше миллиметра,
                // и прямое ребро, идущее по цилиндру хордой (дефект топологии), не попадало в
                // оценку: порог оставался мелким, и грань отвергалась целиком.
                if (seamU > 0 && Math.Abs(UV[a].X - UV[b].X) > 0.9 * seamU) continue;
                double d = (src.PointAt(new Vector2d(m.X / scale.X, m.Y / scale.Y)) - (P[a] + P[b]) / 2).Length;
                // ПОТОЛОК ПОСЛАБЛЕНИЯ. Измерено: на стенке цилиндра допуск раздувался до 8.1 мм
                // при заданных 0.02 — уточнение переставало работать вовсе, и грань оставалась
                // из полусотни плоских треугольников вместо гранёной поверхности. Послабление
                // нужно для краёв, чей прогиб законно больше допуска, но не в сотни раз.
                tol = Math.Max(tol, Math.Min(1.05 * d, 20 * Tol.Chord));
            }
        }
        // ВСТАВКА ТОЧЕК С ПЕРЕКЛАДКОЙ ДЕЛОНЕ. Треугольник, у которого центр на поверхности
        // отходит от центра треугольника дальше порога, получает точку в центре (делится на
        // три), затем стороны перекладываются по Делоне. Деление рёбер пополам плодило иглы:
        // у полосы между краями кольца оно не уменьшало шаг по углу (тысячи треугольников).
        // Граничные стороны не делятся и не перекладываются: на них точки рёбер тела.
        var map = new Dictionary<(int, int), int>();
        void Add(int t) { for (int k = 0; k < 3; k++) map[(T[t + k], T[t + (k + 1) % 3])] = t; }
        void Remove(int t) { for (int k = 0; k < 3; k++) map.Remove((T[t + k], T[t + (k + 1) % 3])); }
        for (int t = 0; t + 2 < T.Count; t += 3) Add(t);

        void Legalize(Queue<(int, int)> q)
        {
            int guard = 200000;
            while (q.Count > 0 && guard-- > 0)
            {
                if ((guard & 1023) == 0) KernelContext.Checkpoint("mesh.refine.legalize");
                var (a, b) = q.Dequeue();
                if (!map.TryGetValue((a, b), out int t1) || !map.TryGetValue((b, a), out int t2) || t1 == t2) continue;
                int c = Third(T, t1, a, b), d = Third(T, t2, b, a);
                if (c < 0 || d < 0 || c == d) continue;
                if (!Convex(UV[a], UV[d], UV[b], UV[c])) continue;
                if (Angle(UV[a], UV[c], UV[b]) + Angle(UV[b], UV[d], UV[a]) <= Math.PI + 1e-6) continue;
                // Не удлинять диагональ в пространстве вдвое: иначе на изогнутой грани
                // появляется треугольник поперёк всей поверхности.
                if ((P[c] - P[d]).Length > 2 * (P[a] - P[b]).Length) continue;
                Remove(t1); Remove(t2);
                T[t1] = c; T[t1 + 1] = a; T[t1 + 2] = d;
                T[t2] = d; T[t2 + 1] = b; T[t2 + 2] = c;
                Add(t1); Add(t2);
                q.Enqueue((a, d)); q.Enqueue((d, b)); q.Enqueue((b, c)); q.Enqueue((c, a));
            }
        }

        double faceArea = 0;
        for (int t = 0; t + 2 < T.Count; t += 3)
            faceArea += Vector3d.Cross(P[T[t + 1]] - P[T[t]], P[T[t + 2]] - P[T[t]]).Length / 2;
        double tiny = Math.Max(1e-9, 1e-6 * faceArea);
        const int maxPoints = 3000;
        int added = 0;
        LastTol = tol;
        // KRN-10: ГОДНЫЙ ТРЕУГОЛЬНИК НЕ ПРОВЕРЯЕТСЯ ЗАНОВО. Проход заново мерил отход у всех треугольников (проекция на
        // поверхность у каждого), хотя меняются лишь соседи вставок: лента катящегося шара у отверстия d30 размечалась 19 с,
        // из них 18.7 — уточнение. Треугольник — по своим вершинам (те же точки и параметры — тот же ответ); сетка та же.
        // Мутация P3D_REFINECACHE=0.
        var good = RefineCacheOff ? null : new HashSet<(int, int, int)>();
        static (int, int, int) Key(int a, int b, int c) => a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);
        for (int pass = 0; pass < 200 && added < maxPoints; pass++)   // предел 40 достигался: оставалось 6 негодных треугольников
        {
            KernelContext.Checkpoint("mesh.refine.pass");
            // НЕГОДНЫЕ ЗАПОМИНАЮТСЯ ПО ВЕРШИНАМ, А НЕ ПО НОМЕРАМ. Перекладка диагоналей сдвигает
            // треугольники, и к моменту вставки по запомненному номеру стоял уже другой, годный:
            // 1357 вставок отклонялось повторной проверкой, а негодные оставались навсегда.
            var bad = new List<(int A, int B, int C)>();
            for (int t = 0; t + 2 < T.Count; t += 3)
            {
                Vector2d m = (UV[T[t]] + UV[T[t + 1]] + UV[T[t + 2]]) / 3;
                Vector3d c3 = (P[T[t]] + P[T[t + 1]] + P[T[t + 2]]) / 3;
                // Площадь — в пространстве и относительно грани: узкие веера в развёртке имеют
                // ничтожную площадь по параметрам, и уточнение их пропускало целиком.
                double area = Vector3d.Cross(P[T[t + 1]] - P[T[t]], P[T[t + 2]] - P[T[t]]).Length / 2;
                if (area < tiny) { SkipTiny++; continue; }
                // Отход меряется двумя способами: по точке с теми же параметрами и по ближайшей
                // точке поверхности. Первый не видит треугольник, перекинутый через шов
                // периодической поверхности (у трубки тора он соединяет края развёртки и в
                // пространстве идёт сквозь трубку), второй — видит.
                var key = Key(T[t], T[t + 1], T[t + 2]);
                if (good != null && good.Contains(key)) continue;
                Interlocked.Increment(ref RefineEvals);
                double dPar = (src.PointAt(new Vector2d(m.X / scale.X, m.Y / scale.Y)) - c3).Length;
                double dev3 = dPar > tol ? dPar : (src.PointAt(src.ProjectToUV(c3)) - c3).Length;
                if (dev3 > tol) bad.Add((T[t], T[t + 1], T[t + 2]));
                else good?.Add(key);
            }
            RefinePasses = pass + 1;
            RefineBad = bad.Count;
            if (bad.Count == 0) break;
            foreach (var (ba, bb, bc) in bad)
            {
                if (added >= maxPoints) break;

                // Найти этот треугольник заново: после перекладок его номер мог измениться. По карте сторон (направленная
                // сторона → треугольник), а не перебором: перебор всех треугольников для каждого негодного — O(n²): целый тор при
                // гранении CAD (многогранный путь) размечался минутами без точек прерывания (разностная серия 8, случай 96).
                KernelContext.Checkpoint("mesh.refine.insert");
                int t = -1;
                if (map.TryGetValue((ba, bb), out var tm) && tm + 2 < T.Count &&
                    ((T[tm] == ba && T[tm + 1] == bb && T[tm + 2] == bc) || (T[tm] == bb && T[tm + 1] == bc && T[tm + 2] == ba) || (T[tm] == bc && T[tm + 1] == ba && T[tm + 2] == bb)))
                    t = tm;
                if (t < 0) { SkipLost++; continue; }

                int a = T[t], b = T[t + 1], c = T[t + 2];
                // Треугольник мог измениться перекладкой в этом же проходе — проверить заново.
                Vector2d m = (UV[a] + UV[b] + UV[c]) / 3;
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   insert m {m} scale {scale} src {src.GetType().Name}");
                Vector3d onS = src.PointAt(new Vector2d(m.X / scale.X, m.Y / scale.Y));
                Vector3d c3b = (P[a] + P[b] + P[c]) / 3;
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   insert onS {onS} c3b {c3b}");
                if ((onS - c3b).Length <= tol && (src.PointAt(src.ProjectToUV(c3b)) - c3b).Length <= tol) { SkipRecheck++; continue; }
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   insert recheck done");
                // Точка в микронах от вершины узкого треугольника даёт сторону, которую
                // экспорт склеит, — такую вставку пропускаем.
                const double micro = 2e-4;
                if ((onS - P[a]).Length < micro || (onS - P[b]).Length < micro || (onS - P[c]).Length < micro)
                {
                    // ЦЕНТР ПОПАЛ В ВЕРШИНУ — ДЕЛИМ ПО ДЛИННОЙ СТОРОНЕ. Веер в развёртке — тонкая
                    // щепка: его центр по параметрам совпадает с вершиной, вставка отбрасывалась,
                    // и треугольник оставался с отходом в миллиметры при допуске 0.4.
                    int la = a, lb = b;
                    double best = (P[b] - P[a]).LengthSquared;
                    if ((P[c] - P[b]).LengthSquared > best) { best = (P[c] - P[b]).LengthSquared; la = b; lb = c; }
                    if ((P[a] - P[c]).LengthSquared > best) { la = c; lb = a; }
                    Vector2d mSide = (UV[la] + UV[lb]) / 2;
                    Vector3d onSide = src.PointAt(new Vector2d(mSide.X / scale.X, mSide.Y / scale.Y));
                    if ((onSide - P[la]).Length < micro || (onSide - P[lb]).Length < micro) { SkipMicro++; continue; }
                    onS = onSide; m = mSide;
                }
                // KRN-06 (06.12): ТОЧКА УЖЕ ЕСТЬ. Деление по длинной стороне у двух соседних щепок ставило одну и ту же середину
                // их общей стороны дважды — вырожденный треугольник (сторона 0), грань отвергалась (лента катящегося шара у
                // отверстия d30 в валу R20). Мутация P3D_MESHDUPINS=0.
                if (!DupInsOff && P.Any(x => (x - onS).LengthSquared < micro * micro)) { SkipMicro++; continue; }
                int v = P.Count; P.Add(onS); UV.Add(m); added++; RefineAdded = added;
                Remove(t);
                T[t] = a; T[t + 1] = b; T[t + 2] = v; Add(t);
                int t2 = T.Count; T.Add(b); T.Add(c); T.Add(v); Add(t2);
                int t3 = T.Count; T.Add(c); T.Add(a); T.Add(v); Add(t3);
                var q = new Queue<(int, int)>();
                q.Enqueue((a, b)); q.Enqueue((b, c)); q.Enqueue((c, a));
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   legalize start T {T.Count}");
                Legalize(q);
                if (MeshTrace) Console.Error.WriteLine($"MESHTRACE   legalize done");
            }
        }
    }

    /// <summary>
    /// ПЕРЕКЛАДКА ДИАГОНАЛЕЙ ДЕЛОНЕ в (u, v): внутренняя диагональ меняется на другую, если
    /// сумма противолежащих углов больше π и четырёхугольник выпуклый. Ушная триангуляция
    /// тянет диагонали через всю грань; после перекладки рёбра встают к ближайшим точкам —
    /// у цилиндра вдоль образующих, где прогиба нет.
    /// </summary>
    private static void Flip(List<Vector2d> UV, List<int> T, List<Vector3d>? P = null)
    {
        // Направленная сторона (a, b) → (треугольник, номер стороны).
        var map = new Dictionary<(int, int), int>();
        void Add(int t) { for (int k = 0; k < 3; k++) map[(T[t + k], T[t + (k + 1) % 3])] = t; }
        void Remove(int t) { for (int k = 0; k < 3; k++) map.Remove((T[t + k], T[t + (k + 1) % 3])); }
        for (int t = 0; t + 2 < T.Count; t += 3) Add(t);
        var queue = new Queue<(int, int)>(map.Keys);
        int guard = 50 * T.Count + 1000;
        int flips = 0, notConvex = 0, delaunay = 0, noPair = 0;
        while (queue.Count > 0 && guard-- > 0)
        {
            var (a, b) = queue.Dequeue();
            if (!map.TryGetValue((a, b), out int t1) || !map.TryGetValue((b, a), out int t2) || t1 == t2) { noPair++; continue; }
            int c = Third(T, t1, a, b), d = Third(T, t2, b, a);
            if (c < 0 || d < 0 || c == d) continue;
            if (!Convex(UV[a], UV[d], UV[b], UV[c])) { notConvex++; continue; }
            double ac = Angle(UV[a], UV[c], UV[b]), ad = Angle(UV[b], UV[d], UV[a]);
            // Порог 1e-6: точки краёв кольца стоят одна под другой, четырёхугольники между ними
            // почти прямоугольники (сумма углов ≈ π), и округление acos перекладывало диагональ
            // туда и обратно без конца.
            if (ac + ad <= Math.PI + 1e-6) { delaunay++; continue; }
            flips++;
            Remove(t1); Remove(t2);
            T[t1] = c; T[t1 + 1] = a; T[t1 + 2] = d;
            T[t2] = d; T[t2 + 1] = b; T[t2 + 2] = c;
            Add(t1); Add(t2);
            queue.Enqueue((a, d)); queue.Enqueue((d, b)); queue.Enqueue((b, c)); queue.Enqueue((c, a));
        }
        FlipDebug?.Invoke($"перекладки: {flips}, невыпуклых {notConvex}, уже Делоне {delaunay}, без пары {noPair}, треугольников {T.Count / 3}");
    }

    /// <summary>ДИАГНОСТИКА: итог перекладок.</summary>
    public static Action<string>? FlipDebug;

    /// <summary>Третья вершина треугольника t со стороной a→b (−1, если такой стороны нет).</summary>
    private static int Third(List<int> T, int t, int a, int b)
    {
        for (int k = 0; k < 3; k++)
            if (T[t + k] == a && T[t + (k + 1) % 3] == b) return T[t + (k + 2) % 3];
        return -1;
    }

    /// <summary>
    /// Пересекает ли один из двух разрезов полосы (конец края A → начало края B; конец B → начало A) какую-либо сторону
    /// многоугольника полосы, не смежную с ним (собственное пересечение отрезков в развёртке).
    /// </summary>
    private static bool StripSeamCrosses(List<Vector2d> poly, int na)
    {
        int n = poly.Count;
        static double Cr(Vector2d o, Vector2d p, Vector2d q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        bool Cross(int i0, int i1, int j0, int j1)
        {
            Vector2d a = poly[i0], b = poly[i1], c = poly[j0], d = poly[j1];
            double s1 = Cr(a, b, c), s2 = Cr(a, b, d), s3 = Cr(c, d, a), s4 = Cr(c, d, b);
            return s1 * s2 < 0 && s3 * s4 < 0;
        }
        foreach (var (s0, s1) in new[] { (na - 1, na % n), (n - 1, 0) })
            for (int k = 0; k < n; k++)
            {
                int k1 = (k + 1) % n;
                if (k == s0 || k == s1 || k1 == s0 || k1 == s1) continue;
                if (Cross(s0, s1, k, k1)) return true;
            }
        return false;
    }

    private static double Angle(Vector2d p, Vector2d at, Vector2d q)
    {
        Vector2d u = p - at, v = q - at;
        double l = u.Length * v.Length;
        return l < 1e-30 ? 0 : Math.Acos(Math.Clamp(Vector2d.Dot(u, v) / l, -1, 1));
    }

    /// <summary>Четырёхугольник a–d–b–c строго выпуклый (обе диагонали внутри).</summary>
    private static bool Convex(Vector2d a, Vector2d d, Vector2d b, Vector2d c)
    {
        static double Cr(Vector2d o, Vector2d p, Vector2d q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        double s1 = Cr(c, d, a), s2 = Cr(c, d, b), s3 = Cr(a, b, c), s4 = Cr(a, b, d);
        return s1 * s2 < 0 && s3 * s4 < 0;
    }

    /// <summary>
    /// «МОЛНИЯ» ДЛЯ ПОЛОСЫ МЕЖДУ ДВУМЯ КРАЯМИ (кольцо, разрезанное по шву): край A — первые
    /// na точек многоугольника, край B — следующие nb (идёт навстречу). Идём по обоим краям,
    /// каждый раз добавляя треугольник с более короткой диагональю. Точки каждого края лежат
    /// в развёртке на прямой, и ушная триангуляция давала веера из одной точки к целому ряду,
    /// которые перекладка не исправляет (соседние четырёхугольники с углом 180°).
    /// </summary>
    /// <summary>
    /// Допуск вставки точки контура в сетку: доля прогиба хорды. Ноль здесь не годится —
    /// точка лежит на кривой поверхности, а треугольник её хорда.
    /// </summary>
    private static double InsertTol => Math.Max(1e-6, Core.Tol.Chord * 0.25);

    private static (List<int>, List<Vector2d>) Zipper(List<Vector2d> poly, int na, int nb)
    {
        // A: 0..na−1 (по возрастанию u), B: na..na+nb−1 (навстречу) — развернуть.
        var ia = Enumerable.Range(0, na).ToList();
        var ib = Enumerable.Range(na, nb).Reverse().ToList();
        // ЗАСТЁЖКА ПО ДОЛЕ ПРОЙДЕННОГО — ПРОБОВАЛ, ОТКАТИЛ. Замысел: у кольцевой грани края
        // имеют разную длину (в разобранном случае 192.4 и 138.8 при 77 и 82 точках), и
        // сравнение диагоналей уводит застёжку — под конец приходится перекидывать сторону
        // поперёк кольца (измерена непарная сторона длиной 27.15 мм при 157 сторонах).
        // Замена правила на «идти тем краем, который отстал по доле пройденного» поднимала
        // охват сетки с 90 тел из 100 до 92, но в проверке реза вдоль цилиндра появлялось
        // открытое ребро на экране. Различитель не нашёлся: у чинимых и у ломающегося случая
        // близки и число точек (пороги 1.05…1.4 не разделяют), и длины краёв (порог 1.2…1.35
        // тоже). Нужно смотреть не на соотношение краёв, а на само место, где застёжка
        // перекидывает сторону.

        var T = new List<int>();
        int i = 0, j = 0;
        while (i < na - 1 || j < nb - 1)
        {
            bool advA;
            if (i == na - 1) advA = false;
            else if (j == nb - 1) advA = true;
            // ВЫБОР КРАЯ — ПО ДОЛЕ ПРОЙДЕННОГО, А НЕ ПО ДЛИНЕ ДИАГОНАЛИ. Сравнение диагоналей
            // у кольцевой грани с краями разной длины (77 и 82 точки) уводит застёжку: один
            // край «обгоняет» другой, и под конец приходится перекидывать длинную сторону
            // поперёк кольца — она и остаётся непарной.
            else advA = (poly[ia[i + 1]] - poly[ib[j]]).LengthSquared < (poly[ia[i]] - poly[ib[j + 1]]).LengthSquared;
            if (advA) { T.Add(ia[i]); T.Add(ia[i + 1]); T.Add(ib[j]); i++; }
            else { T.Add(ia[i]); T.Add(ib[j + 1]); T.Add(ib[j]); j++; }

            // Отладка: как идёт застёжка и где она перекидывает длинную сторону.
            if (ZipDebug != null)
            {
                double diag = (poly[ia[Math.Min(i, na - 1)]] - poly[ib[Math.Min(j, nb - 1)]]).Length;
                ZipDebug($"шаг {(advA ? "A" : "B")}: i={i}/{na - 1}, j={j}/{nb - 1}, диагональ {diag:0.###}");
            }
        }
        // Ориентация в (u, v) — против часовой (как у триангулятора).
        for (int t = 0; t + 2 < T.Count; t += 3)
        {
            Vector2d a = poly[T[t]], b = poly[T[t + 1]], c = poly[T[t + 2]];
            if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) (T[t + 1], T[t + 2]) = (T[t + 2], T[t + 1]);
        }
        return (T, poly);
    }

    /// <summary>
    /// МОЛНИЯ ДЛЯ ОДИНОЧНОГО КОНТУРА: контур делится в точках с наименьшей и наибольшей
    /// координатой вдоль длинной оси на две цепочки; если обе монотонны, между ними строится
    /// полоса. Большинство граней — «две дуги и два отрезка», в развёртке прямоугольник с
    /// рядами точек на одной прямой; ушная триангуляция отрезала из них вырожденные «уши»
    /// через всю грань — в пространстве плоский треугольник поперёк цилиндра (прогон, 7).
    /// null — цепочки не монотонны или вышел вывернутый треугольник.
    /// </summary>
    /// <summary>ДИАГНОСТИКА: почему молния не сработала.</summary>
    public static Action<string>? ZipDebug;

    private static List<int>? MonotoneZipper(List<Vector2d> poly, bool alongX)
    {
        int n = poly.Count;
        if (n < 4) return null;
        double minX = poly.Min(q => q.X), maxX = poly.Max(q => q.X), minY = poly.Min(q => q.Y), maxY = poly.Max(q => q.Y);
        double Key(int i) => alongX ? poly[i].X : poly[i].Y;
        double Side(int i) => alongX ? poly[i].Y : poly[i].X;
        int lo = 0, hi = 0;
        for (int i = 1; i < n; i++)
        {
            if (Key(i) < Key(lo) - 1e-12 || (Math.Abs(Key(i) - Key(lo)) <= 1e-12 && Side(i) < Side(lo))) lo = i;
            if (Key(i) > Key(hi) + 1e-12 || (Math.Abs(Key(i) - Key(hi)) <= 1e-12 && Side(i) > Side(hi))) hi = i;
        }
        if (lo == hi) { ZipDebug?.Invoke("минимум и максимум совпали"); return null; }
        // Контур с точки минимума: A — до точки максимума, B — остальные точки (идут обратно).
        var order = new List<int>(); for (int k = 0; k < n; k++) order.Add((lo + k) % n);
        int na = order.IndexOf(hi) + 1, nb = n - na;
        if (na < 2 || nb < 1) { ZipDebug?.Invoke("одна из цепочек пуста"); return null; }
        double eps = 1e-9 * Math.Max(1, (alongX ? maxX - minX : maxY - minY));
        for (int k = 1; k < na; k++) if (Key(order[k]) < Key(order[k - 1]) - eps) { ZipDebug?.Invoke($"первая цепочка не монотонна в точке {k} из {na}"); return null; }
        for (int k = na + 1; k < n; k++) if (Key(order[k]) > Key(order[k - 1]) + eps) { ZipDebug?.Invoke($"вторая цепочка не монотонна в точке {k} из {n}"); return null; }
        if (nb > 0 && Key(order[na]) > Key(order[na - 1]) + eps) return null;
        var rotated = order.Select(i => poly[i]).ToList();
        var (T0, _) = Zipper(rotated, na, nb);
        // Без нахлёстов: сумма площадей треугольников равна площади контура.
        double sum = 0;
        for (int t = 0; t + 2 < T0.Count; t += 3)
        {
            Vector2d p0 = rotated[T0[t]], p1 = rotated[T0[t + 1]], p2 = rotated[T0[t + 2]];
            // Площадь СО ЗНАКОМ: по модулю перевёрнутый треугольник неотличим от правильного,
            // и молния выдавала сетку, где сторона проходится дважды в одну сторону (у
            // пятиугольника — [4,2,3] [4,2,0] [0,2,1]).
            sum += ((p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X)) / 2;
        }
        double total = Area(rotated);
        if (Math.Abs(sum - total) > 1e-9 * Math.Max(1, total)) { ZipDebug?.Invoke($"нахлёст: площадь треугольников {sum:0.####} против контура {total:0.####}"); return null; }
        return T0.Select(i => order[i]).ToList();
    }

    private static double Area(List<Vector2d> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += a.X * b.Y - a.Y * b.X; }
        return s / 2;
    }

    private static double Wrap(double x, double per) => x - per * Math.Round(x / per);

    private static (List<Vector3d>, List<Vector2d>) Rotate(List<Vector3d> p3, List<Vector2d> uv, int start)
    {
        var a = new List<Vector3d>(); var b = new List<Vector2d>();
        for (int i = 0; i < p3.Count; i++) { a.Add(p3[(start + i) % p3.Count]); b.Add(uv[(start + i) % uv.Count]); }
        return (a, b);
    }

    private static List<Vector2d> Unwrapped(List<Vector2d> uv, double per)
    {
        var r = new List<Vector2d> { uv[0] };
        for (int i = 1; i < uv.Count; i++)
        {
            double du = uv[i].X - r[i - 1].X;
            r.Add(new Vector2d(uv[i].X - per * Math.Round(du / per), uv[i].Y));
        }
        return r;
    }
}
