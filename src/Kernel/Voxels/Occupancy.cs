using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Voxels;

/// <summary>Чем занята ячейка пространства.</summary>
public enum CellState : byte
{
    /// <summary>Пусто: ячейка целиком вне тела.</summary>
    Outside = 0,

    /// <summary>Материал: ячейка целиком внутри тела.</summary>
    Inside = 1,

    /// <summary>Через ячейку проходит поверхность — она и внутри, и снаружи.</summary>
    Boundary = 2
}

/// <summary>Куб заполнения: центр и половина ребра.</summary>
public readonly struct Cell
{
    public readonly Vector3d Centre;
    public readonly double Half;
    public readonly CellState State;

    public Cell(Vector3d centre, double half, CellState state)
    {
        Centre = centre;
        Half = half;
        State = state;
    }

    public double Volume => 8 * Half * Half * Half;
}

/// <summary>
/// ОБЪЁМНАЯ МОДЕЛЬ ТЕЛА: ЧЕМ ЗАНЯТА КАЖДАЯ ЯЧЕЙКА ПРОСТРАНСТВА.
///
/// ЗАЧЕМ ЭТО НУЖНО. Булева операция может ошибиться так, что результат
/// выглядит правдоподобно: тело замкнуто, грани на месте, а материала
/// не столько. Отличить «построилось» от «построилось верно» по самому
/// B-Rep нельзя — он и есть то, что проверяется. Нужен ВТОРОЙ, независимый
/// способ ответить на вопрос «есть ли здесь материал», устроенный иначе.
/// Тогда расхождение двух ответов указывает на ошибку И НА ЕЁ МЕСТО.
///
/// ПОЧЕМУ КУБЫ, А НЕ ШАРЫ. Кубы замощают пространство без остатка. Любой
/// конечный набор шаров оставляет пустоты — у плоской грани, ребра и угла
/// для полного заполнения нужен бесконечный ряд убывающих шаров. С кубами
/// «заполнить объём» и «поменьше элементов» не спорят друг с другом:
/// внутренние ячейки сливаются в крупные, и остаётся ровно та мелочь,
/// что лежит у поверхности.
///
/// КАК КЛАССИФИЦИРУЕТСЯ ЯЧЕЙКА. Сначала помечаются ячейки, через которые
/// проходит хоть один треугольник оболочки, — это граница. Остальные
/// заливаются снаружи внутрь: всё, до чего залив дошёл, — пустота,
/// до чего не дошёл — материал.
///
/// Залив выбран вместо броска луча из каждой ячейки не ради скорости,
/// а ради поведения при поломке. Луч, пущенный из точки, честно отвечает
/// и на НЕЗАМКНУТОЙ оболочке — отвечает случайно, и ошибка выглядит как
/// крапинка пустоты внутри материала. Залив на незамкнутой оболочке
/// вытекает через дыру и заполняет тело целиком; это видно сразу и
/// сообщается отдельно (<see cref="Leaked"/>). Дырявая оболочка — тоже
/// ошибка построения, и прятать её не надо.
/// </summary>
public sealed class Occupancy
{
    /// <summary>Сколько ячеек по стороне: 2^Depth.</summary>
    public readonly int Depth;
    public readonly int Side;

    /// <summary>Угол сетки и ребро одной ячейки.</summary>
    public readonly Vector3d Origin;
    public readonly double Step;

    private readonly CellState[] _state;

    /// <summary>Залив дошёл до всех ячеек: оболочка негерметична на этом разрешении.</summary>
    public bool Leaked { get; private set; }

    /// <summary>Сколько ячеек какого рода.</summary>
    public int InsideCount { get; private set; }
    public int BoundaryCount { get; private set; }

    private Occupancy(int depth, Vector3d origin, double step)
    {
        Depth = depth;
        Side = 1 << depth;
        Origin = origin;
        Step = step;
        _state = new CellState[Side * Side * Side];
    }

    public CellState At(int i, int j, int k) =>
        (uint)i < (uint)Side && (uint)j < (uint)Side && (uint)k < (uint)Side
            ? _state[i + Side * (j + Side * k)]
            : CellState.Outside;

    /// <summary>Центр ячейки в координатах модели.</summary>
    public Vector3d CentreOf(int i, int j, int k) =>
        Origin + new Vector3d((i + 0.5) * Step, (j + 0.5) * Step, (k + 0.5) * Step);

    /// <summary>Ячейка, в которую попадает точка. false — точка вне сетки.</summary>
    public bool Locate(Vector3d p, out int i, out int j, out int k)
    {
        i = (int)Math.Floor((p.X - Origin.X) / Step);
        j = (int)Math.Floor((p.Y - Origin.Y) / Step);
        k = (int)Math.Floor((p.Z - Origin.Z) / Step);

        return (uint)i < (uint)Side && (uint)j < (uint)Side && (uint)k < (uint)Side;
    }

    // ------------------------------------------------------------- объёмы

    /// <summary>Объём заведомо занятого материала: только целые ячейки.</summary>
    public double VolumeLow => InsideCount * CellVolume;

    /// <summary>Объём с учётом всей границы: истинный объём не больше этого.</summary>
    public double VolumeHigh => (InsideCount + BoundaryCount) * CellVolume;

    /// <summary>
    /// Оценка объёма: граничная ячейка считается наполовину занятой.
    ///
    /// Половина — не догадка, а середина вилки: истина заведомо между
    /// VolumeLow и VolumeHigh, и середина минимизирует худшую ошибку.
    /// Точнее можно только измельчая сетку, и это честнее, чем подбирать
    /// коэффициент под знакомые тела.
    /// </summary>
    public double VolumeEstimate => VolumeLow + BoundaryCount * CellVolume * 0.5;

    public double CellVolume => Step * Step * Step;

    /// <summary>Ширина вилки в долях оценки: насколько грубо разрешение.</summary>
    public double Uncertainty =>
        VolumeEstimate > 0 ? (VolumeHigh - VolumeLow) / VolumeEstimate : 0;

    /// <summary>
    /// ГЛАВНАЯ ПРОВЕРКА: ЛОЖИТСЯ ЛИ ТОЧНЫЙ ОБЪЁМ В ВИЛКУ.
    ///
    /// Точный объём тело считает по своей оболочке (теорема о дивергенции),
    /// и это число ровно настолько верно, насколько верна оболочка. Вилка
    /// же получена совсем иначе — заливом по ячейкам. Если точный объём
    /// в вилку НЕ попал, оболочка и заполнение описывают разные тела,
    /// и виновата не грубость сетки: вилка гарантированно содержит объём
    /// того тела, чьи треугольники ей скормили.
    ///
    /// Обратное неверно: попадание в вилку ничего не доказывает, оно лишь
    /// не опровергает. Поэтому здесь ищут ОПРОВЕРЖЕНИЕ, а не подтверждение.
    /// </summary>
    public bool Brackets(double volume) => volume >= VolumeLow - 1e-9 && volume <= VolumeHigh + 1e-9;

    /// <summary>Строка для отчёта: вилка, оценка и грубость.</summary>
    public string Describe() =>
        $"volume {VolumeLow:0.###} .. {VolumeHigh:0.###} " +
        $"({InsideCount} solid cells, {BoundaryCount} on the surface, " +
        $"resolution {Side}, spread {Uncertainty * 100:0.#}%)" +
        (Leaked ? "; the shell leaks at this resolution" : "");

    // ---------------------------------------------------------- построение

    /// <summary>Разрешение по умолчанию: 32 ячейки по стороне.</summary>
    public const int DefaultDepth = 5;

    public static Occupancy Build(MeshData mesh, int depth = DefaultDepth) =>
        Build(Triangles(mesh), mesh.Bounds, depth);

    public static Occupancy Build(IReadOnlyList<TSolid> bodies, int depth = DefaultDepth)
    {
        var tris = new List<(Vector3d, Vector3d, Vector3d)>();
        var bounds = Bounds3.Empty;

        foreach (var body in bodies)
        {
            MeshData md = Tessellator.Tessellate(body, facets: true);
            tris.AddRange(Triangles(md));
            bounds.Add(md.Bounds);
        }

        return Build(tris, bounds, depth);
    }

    /// <summary>
    /// СЕТКА СТРОИТСЯ НА ОБЩЕМ КУБЕ, А НЕ НА ГАБАРИТЕ ТЕЛА.
    ///
    /// Иначе у двух тел разной формы ячейки оказались бы разного размера
    /// и в разных местах, и сравнивать их поячеечно стало бы нельзя —
    /// а ради сравнения всё и затевается. Общий куб задаётся снаружи
    /// (<paramref name="frame"/>), когда сравниваются несколько тел.
    /// </summary>
    public static Occupancy Build(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris,
                                  Bounds3 bounds, int depth = DefaultDepth,
                                  Bounds3? frame = null)
    {
        depth = Math.Clamp(depth, 1, 8);

        Bounds3 box = frame ?? bounds;
        if (box.IsEmpty) box = bounds;

        if (box.IsEmpty)
            return new Occupancy(depth, Vector3d.Zero, 1);

        // ЗАПАС МИНИМАЛЬНЫЙ. Он нужен лишь затем, чтобы поверхность
        // не легла ровно на грань крайней ячейки: тогда принадлежность
        // этой ячейки решалась бы округлением. Большой запас не вреден
        // для классификации, но крайние ячейки вылезают за тело, и на
        // столько же завышается верхняя граница объёма.
        double span = Math.Max(box.Size.X, Math.Max(box.Size.Y, box.Size.Z));
        if (span <= 0) span = 1;

        span *= 1 + 1e-6;

        Vector3d centre = box.Center;
        Vector3d origin = centre - new Vector3d(span * 0.5);

        int side = 1 << depth;
        var grid = new Occupancy(depth, origin, span / side);

        grid.MarkBoundary(tris);
        grid.FloodOutside();
        grid.Count();

        return grid;
    }

    public static List<(Vector3d A, Vector3d B, Vector3d C)> Triangles(MeshData md)
    {
        var tris = new List<(Vector3d, Vector3d, Vector3d)>(md.Indices.Length / 3);

        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            Vector3 a = md.Vertices[md.Indices[i]].P;
            Vector3 b = md.Vertices[md.Indices[i + 1]].P;
            Vector3 c = md.Vertices[md.Indices[i + 2]].P;

            tris.Add((new Vector3d(a.X, a.Y, a.Z),
                      new Vector3d(b.X, b.Y, b.Z),
                      new Vector3d(c.X, c.Y, c.Z)));
        }

        return tris;
    }

    /// <summary>
    /// ГРАНИЧНЫЕ ЯЧЕЙКИ — ПО ТРЕУГОЛЬНИКАМ, А НЕ ПО ЯЧЕЙКАМ.
    ///
    /// Перебирать все ячейки и спрашивать каждую о каждом треугольнике —
    /// произведение двух больших чисел. Треугольник же задевает лишь те
    /// ячейки, что лежат в его габарите, и их обычно единицы.
    /// </summary>
    private void MarkBoundary(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris)
    {
        double half = Step * 0.5;

        foreach (var (a, b, c) in tris)
        {
            Vector3d lo = Vector3d.ComponentMin(a, Vector3d.ComponentMin(b, c));
            Vector3d hi = Vector3d.ComponentMax(a, Vector3d.ComponentMax(b, c));

            int i0 = Clamp((int)Math.Floor((lo.X - Origin.X) / Step));
            int j0 = Clamp((int)Math.Floor((lo.Y - Origin.Y) / Step));
            int k0 = Clamp((int)Math.Floor((lo.Z - Origin.Z) / Step));
            int i1 = Clamp((int)Math.Floor((hi.X - Origin.X) / Step));
            int j1 = Clamp((int)Math.Floor((hi.Y - Origin.Y) / Step));
            int k1 = Clamp((int)Math.Floor((hi.Z - Origin.Z) / Step));

            for (int k = k0; k <= k1; k++)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        int at = i + Side * (j + Side * k);
                        if (_state[at] == CellState.Boundary) continue;

                        if (TriangleHitsBox(a, b, c, CentreOf(i, j, k), half))
                            _state[at] = CellState.Boundary;
                    }
        }
    }

    private int Clamp(int v) => v < 0 ? 0 : v >= Side ? Side - 1 : v;

    /// <summary>
    /// ЗАЛИВ ПУСТОТЫ ОТ КРАЯ СЕТКИ.
    ///
    /// Край заведомо снаружи: сетка построена по габариту тела с запасом.
    /// Всё, куда залив прошёл, не встретив границы, — пустота. Что осталось
    /// непомеченным и не граница — материал.
    /// </summary>
    private void FloodOutside()
    {
        var queue = new Queue<int>();
        var seen = new bool[_state.Length];

        void Push(int i, int j, int k)
        {
            if ((uint)i >= (uint)Side || (uint)j >= (uint)Side || (uint)k >= (uint)Side) return;

            int at = i + Side * (j + Side * k);
            if (seen[at] || _state[at] == CellState.Boundary) return;

            seen[at] = true;
            queue.Enqueue(at);
        }

        for (int j = 0; j < Side; j++)
            for (int i = 0; i < Side; i++)
            {
                Push(i, j, 0);
                Push(i, j, Side - 1);
            }

        for (int k = 0; k < Side; k++)
            for (int i = 0; i < Side; i++)
            {
                Push(i, 0, k);
                Push(i, Side - 1, k);
            }

        for (int k = 0; k < Side; k++)
            for (int j = 0; j < Side; j++)
            {
                Push(0, j, k);
                Push(Side - 1, j, k);
            }

        int reached = 0;

        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            reached++;

            int i = at % Side;
            int j = (at / Side) % Side;
            int k = at / (Side * Side);

            Push(i - 1, j, k); Push(i + 1, j, k);
            Push(i, j - 1, k); Push(i, j + 1, k);
            Push(i, j, k - 1); Push(i, j, k + 1);
        }

        int solid = 0;

        for (int at = 0; at < _state.Length; at++)
        {
            if (_state[at] == CellState.Boundary) continue;

            if (seen[at]) _state[at] = CellState.Outside;
            else { _state[at] = CellState.Inside; solid++; }
        }

        // Ни одной внутренней ячейки при наличии границы — залив вытек
        // наружу через дыру в оболочке. Тонкая стенка, которая тоньше
        // ячейки, даёт тот же признак, поэтому сообщение об этом говорит
        // про РАЗРЕШЕНИЕ, а не утверждает, что тело сломано.
        Leaked = solid == 0 && reached < _state.Length;
    }

    private void Count()
    {
        InsideCount = 0;
        BoundaryCount = 0;

        foreach (CellState s in _state)
        {
            if (s == CellState.Inside) InsideCount++;
            else if (s == CellState.Boundary) BoundaryCount++;
        }
    }

    // ------------------------------------------------------------- слияние

    /// <summary>
    /// КРУПНЫЕ КУБЫ ВНУТРИ, МЕЛКИЕ У ПОВЕРХНОСТИ.
    ///
    /// Восемь одинаковых соседей сливаются в один вдвое больший, и так
    /// вверх до корня. Форма от этого не меняется — меняется только число
    /// кубов, а их рисовать. На кубе 32×32×32 внутренних ячеек 32768,
    /// после слияния остаются единицы крупных.
    /// </summary>
    public List<Cell> Merged(bool withBoundary = true)
    {
        var result = new List<Cell>();
        Collect(0, 0, 0, Side, result, withBoundary);
        return result;
    }

    private void Collect(int i0, int j0, int k0, int size, List<Cell> into, bool withBoundary)
    {
        CellState uniform = Uniform(i0, j0, k0, size);

        if (uniform != Mixed)
        {
            if (uniform == CellState.Outside) return;
            if (uniform == CellState.Boundary && !withBoundary) return;

            double half = size * Step * 0.5;
            Vector3d centre = Origin + new Vector3d(
                (i0 + size * 0.5) * Step, (j0 + size * 0.5) * Step, (k0 + size * 0.5) * Step);

            into.Add(new Cell(centre, half, uniform));
            return;
        }

        // Смешанный куб размера в одну ячейку невозможен: у ячейки
        // состояние одно. Проверка стоит на случай будущей правки.
        if (size <= 1) return;

        int h = size / 2;

        for (int dk = 0; dk < 2; dk++)
            for (int dj = 0; dj < 2; dj++)
                for (int di = 0; di < 2; di++)
                    Collect(i0 + di * h, j0 + dj * h, k0 + dk * h, h, into, withBoundary);
    }

    /// <summary>Состояния в кубе разные. Не настоящее состояние ячейки.</summary>
    private const CellState Mixed = (CellState)255;

    /// <summary>Общее состояние куба ячеек, либо <see cref="Mixed"/>.</summary>
    private CellState Uniform(int i0, int j0, int k0, int size)
    {
        CellState first = At(i0, j0, k0);

        for (int k = k0; k < k0 + size; k++)
            for (int j = j0; j < j0 + size; j++)
                for (int i = i0; i < i0 + size; i++)
                    if (At(i, j, k) != first) return Mixed;

        return first;
    }

    // ------------------------------------------ треугольник против ячейки

    /// <summary>
    /// ПЕРЕСЕКАЕТ ЛИ ТРЕУГОЛЬНИК ОСЕВОЙ КУБ (теорема о разделяющей оси).
    ///
    /// Проверяются тринадцать направлений: три оси куба, нормаль
    /// треугольника и девять попарных произведений его сторон на оси.
    /// Если хоть по одному проекции не пересекаются — не пересекает.
    ///
    /// Приблизительная проверка тут не годится: от неё зависит, какие
    /// ячейки объявлены границей, а значит и весь залив. Пропущенная
    /// граничная ячейка — это дыра, через которую вытечет пустота.
    /// </summary>
    public static bool TriangleHitsBox(Vector3d a, Vector3d b, Vector3d c, Vector3d centre, double half)
    {
        Vector3d v0 = a - centre, v1 = b - centre, v2 = c - centre;
        Vector3d e0 = v1 - v0, e1 = v2 - v1, e2 = v0 - v2;

        if (!AxisTest(v0, v1, v2, e0, half)) return false;
        if (!AxisTest(v0, v1, v2, e1, half)) return false;
        if (!AxisTest(v0, v1, v2, e2, half)) return false;

        // Габарит треугольника против габарита куба.
        if (Math.Min(v0.X, Math.Min(v1.X, v2.X)) > half ||
            Math.Max(v0.X, Math.Max(v1.X, v2.X)) < -half) return false;
        if (Math.Min(v0.Y, Math.Min(v1.Y, v2.Y)) > half ||
            Math.Max(v0.Y, Math.Max(v1.Y, v2.Y)) < -half) return false;
        if (Math.Min(v0.Z, Math.Min(v1.Z, v2.Z)) > half ||
            Math.Max(v0.Z, Math.Max(v1.Z, v2.Z)) < -half) return false;

        // Плоскость треугольника против куба.
        Vector3d n = Vector3d.Cross(e0, e1);
        double d = Vector3d.Dot(n, v0);
        double r = half * (Math.Abs(n.X) + Math.Abs(n.Y) + Math.Abs(n.Z));

        return Math.Abs(d) <= r;
    }

    /// <summary>Девять осей вида «ось куба x сторона треугольника» разом.</summary>
    private static bool AxisTest(Vector3d v0, Vector3d v1, Vector3d v2, Vector3d edge, double half)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3d dir = axis switch
            {
                0 => new Vector3d(0, -edge.Z, edge.Y),
                1 => new Vector3d(edge.Z, 0, -edge.X),
                _ => new Vector3d(-edge.Y, edge.X, 0)
            };

            double len = Math.Abs(dir.X) + Math.Abs(dir.Y) + Math.Abs(dir.Z);
            if (len < 1e-18) continue;          // сторона параллельна оси — ось вырождена

            double p0 = Vector3d.Dot(dir, v0);
            double p1 = Vector3d.Dot(dir, v1);
            double p2 = Vector3d.Dot(dir, v2);

            double lo = Math.Min(p0, Math.Min(p1, p2));
            double hi = Math.Max(p0, Math.Max(p1, p2));

            double r = half * len;

            if (lo > r || hi < -r) return false;
        }

        return true;
    }
}
