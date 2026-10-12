using System.Numerics;

namespace Parametric3D.Kernel.Exact;

/// <summary>Ориентированная ссылка на плоскость реестра. Внутренность — где значение не больше нуля.</summary>
public readonly record struct OPlane(int Id, int Side)
{
    public OPlane Flipped => new(Id, -Side);
}

/// <summary>
/// РЕЕСТР ПЛОСКОСТЕЙ И ВЕРШИН — единственное хранилище геометрии.
///
/// ПЛОСКОСТЬ хранится без ориентации: четыре целых, сокращённых на НОД,
/// первый ненулевой из A, B, C положителен. Совпадающие плоскости дают
/// одну запись; сторона грани — бит в ссылке OPlane.
///
/// ВЕРШИНА хранится однородными координатами (X, Y, Z, W), сокращёнными
/// на НОД, с W больше нуля. Одна точка — одна запись, какими бы тройками
/// плоскостей её ни получили (этап 2).
///
/// Реестр только растёт: записи не пересчитываются и не удаляются.
/// </summary>
public sealed class PlaneRegistry
{
    private readonly record struct PlaneKey(BigInteger A, BigInteger B, BigInteger C, BigInteger D);
    private readonly record struct VertexKey(BigInteger X, BigInteger Y, BigInteger Z, BigInteger W);
    private readonly record struct TripleKey(int P, int Q, int R);

    private sealed class PlaneRec
    {
        public BigInteger A, B, C, D;
        public double Ad, Bd, Cd, Dd;
        public long Bits;
    }

    private sealed class VertexRec
    {
        public BigInteger X, Y, Z, W;
        public double Xd, Yd, Zd, Wd;
        public long Bits;
        public readonly HashSet<int> On = new();
    }

    // ПЛОСКИЕ МАССИВЫ ДЛЯ БЫСТРОГО ФИЛЬТРА. Записи о плоскостях и вершинах — отдельные объекты
    // в куче, и восемь миллионов обращений вразнобой за одно скругление бьют по кэшу: на
    // предикат уходило 250 наносекунд при цене самой арифметики в единицы. Коэффициенты в
    // double продублированы в непрерывных массивах — фильтр читает только их.
    // Коэффициенты лежат ЧЕТВЁРКАМИ ПОДРЯД: у реальной детали сотни тысяч вершин и плоскостей,
    // и предикат, читающий восемь разных массивов вразнобой, платит восемью промахами кэша.
    // При плотной раскладке их два.
    private double[] _p4 = new double[256];
    private double[] _v4 = new double[256];

    private static void Grow(ref double[] a, int need)
    {
        if (need < a.Length) return;
        Array.Resize(ref a, Math.Max(need + 1, a.Length * 2));
    }

    private readonly List<PlaneRec> _planes = new();
    private readonly Dictionary<PlaneKey, int> _planeIndex = new();
    private readonly List<VertexRec> _vertices = new();
    private readonly Dictionary<VertexKey, int> _vertexIndex = new();
    private readonly Dictionary<TripleKey, int> _tripleCache = new();

    // ------------------------------------------------------------ статистика
    /// <summary>Сколько раз ответ дал реестр без арифметики (вершина заведомо на плоскости).</summary>
    public long ShortcutHits { get; private set; }

    /// <summary>Счётчики по всем реестрам — для разбора: чем занята точная арифметика.</summary>
    public static long TotalTier1, TotalTier2, TotalTier3, TotalShortcut;
    /// <summary>Ответы по ступеням: 1 — double с оценкой, 2 — Int128, 3 — многословно.</summary>
    public long Tier1 { get; private set; }
    public long Tier2 { get; private set; }
    public long Tier3 { get; private set; }

    /// <summary>
    /// СВЕРКА КАЖДОГО ОТВЕТА С ТОЧНЫМ. Для тестов и разбора: каждая ступень
    /// проверяется многословной арифметикой, расхождение — исключение.
    /// Медленно; в работе выключено.
    /// </summary>
    private static bool _gVerifyEveryAnswer;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool VerifyEveryAnswer
    {
        get => !Parametric3D.Kernel.KernelContext.AnyVerifying
            ? _gVerifyEveryAnswer && Parametric3D.Kernel.KernelContext.Current == null
            : Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.VerifyEveryAnswer : _gVerifyEveryAnswer;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gVerifyEveryAnswer, value, "PlaneRegistry.VerifyEveryAnswer");
    }

    /// <summary>
    /// ИСТИННЫЕ ПЛОСКОСТИ ГРАНЕЙ (ExactPlanarInput). Точная плоскость плоской грани проходит через
    /// округлённый центр и отстоит от плоскости модели на долю шага сетки; здесь хранится, какой
    /// плоскости модели (нормаль и смещение в double) она соответствует. По этим записям грань другого
    /// операнда, аналитически лежащая в той же плоскости, получает ту же точную плоскость.
    /// Не влияет ни на один предикат; переносится при Import.
    /// </summary>
    internal readonly List<(BigInteger A, BigInteger B, BigInteger C, BigInteger D, OpenTK.Mathematics.Vector3d N, double W)> FaceHints = new();

    internal void AddFaceHint(BigInteger a, BigInteger b, BigInteger c, BigInteger d, OpenTK.Mathematics.Vector3d n, double w)
    {
        foreach (var h in FaceHints)
            if (h.A == a && h.B == b && h.C == c && h.D == d) return;
        FaceHints.Add((a, b, c, d, n, w));
    }

    public int PlaneCount => _planes.Count;
    public int VertexCount => _vertices.Count;

    // ============================================================ плоскости
    /// <summary>Плоскость по коэффициентам. Возвращает номер и сторону относительно канонической записи.</summary>
    public OPlane Plane(BigInteger a, BigInteger b, BigInteger c, BigInteger d)
    {
        if (a.IsZero && b.IsZero && c.IsZero)
            throw new ArgumentException("degenerate plane: zero normal");

        BigInteger g = Gcd(Gcd(a, b), Gcd(c, d));
        if (g > BigInteger.One) { a /= g; b /= g; c /= g; d /= g; }

        int side = 1;
        BigInteger first = !a.IsZero ? a : !b.IsZero ? b : c;
        if (first.Sign < 0) { a = -a; b = -b; c = -c; d = -d; side = -1; }

        var key = new PlaneKey(a, b, c, d);

        if (!_planeIndex.TryGetValue(key, out int id))
        {
            id = _planes.Count;
            _planes.Add(new PlaneRec
            {
                A = a, B = b, C = c, D = d,
                Ad = (double)a, Bd = (double)b, Cd = (double)c, Dd = (double)d,
                Bits = Math.Max(Math.Max(Bl(a), Bl(b)), Math.Max(Bl(c), Bl(d)))
            });
            Grow(ref _p4, id * 4 + 3);
            _p4[id * 4] = (double)a; _p4[id * 4 + 1] = (double)b; _p4[id * 4 + 2] = (double)c; _p4[id * 4 + 3] = (double)d;
            _planeIndex[key] = id;
        }

        return new OPlane(id, side);
    }

    /// <summary>Плоскость через три точки сетки. Точки на одной прямой — исключение.</summary>
    public OPlane PlaneThrough(GridPoint p, GridPoint q, GridPoint r)
    {
        BigInteger ux = q.X - p.X, uy = q.Y - p.Y, uz = q.Z - p.Z;
        BigInteger vx = r.X - p.X, vy = r.Y - p.Y, vz = r.Z - p.Z;

        BigInteger a = uy * vz - uz * vy;
        BigInteger b = uz * vx - ux * vz;
        BigInteger c = ux * vy - uy * vx;

        if (a.IsZero && b.IsZero && c.IsZero)
            throw new ArgumentException("three points on one line");

        return Plane(a, b, c, -(a * p.X + b * p.Y + c * p.Z));
    }

    /// <summary>
    /// Плоскость, содержащая отрезок u–v и перпендикулярная плоскости S.
    /// Нормаль n × (v − u) целая, перпендикулярность точная (этап 2).
    /// </summary>
    public OPlane EdgePlane(OPlane support, GridPoint u, GridPoint v)
    {
        PlaneRec s = _planes[support.Id];
        BigInteger nx = s.A * support.Side, ny = s.B * support.Side, nz = s.C * support.Side;
        BigInteger ex = v.X - u.X, ey = v.Y - u.Y, ez = v.Z - u.Z;

        BigInteger a = ny * ez - nz * ey;
        BigInteger b = nz * ex - nx * ez;
        BigInteger c = nx * ey - ny * ex;

        return Plane(a, b, c, -(a * u.X + b * u.Y + c * u.Z));
    }

    public (BigInteger A, BigInteger B, BigInteger C, BigInteger D) Coefficients(int id)
    {
        PlaneRec p = _planes[id];
        return (p.A, p.B, p.C, p.D);
    }

    /// <summary>Единичная нормаль в double — только для вывода.</summary>
    public (double X, double Y, double Z) UnitNormal(OPlane p)
    {
        PlaneRec r = _planes[p.Id];
        double x = r.Ad * p.Side, y = r.Bd * p.Side, z = r.Cd * p.Side;
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(len))
        {
            // Squaring a finite coefficient can overflow too. Reduce integer
            // bit length first, then normalize without overflowing squares.
            int shift = checked((int)Math.Max(0, Math.Max(Bl(r.A), Math.Max(Bl(r.B), Bl(r.C))) - 500));
            x = (double)(r.A >> shift) * p.Side;
            y = (double)(r.B >> shift) * p.Side;
            z = (double)(r.C >> shift) * p.Side;
            double scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
            x /= scale; y /= scale; z /= scale;
            len = Math.Sqrt(x * x + y * y + z * z);
        }
        return (x / len, y / len, z / len);
    }

    // ============================================================= вершины
    /// <summary>
    /// Вершина — пересечение трёх плоскостей. Одна точка — один номер,
    /// независимо от тройки. Плоскости без общей точки — исключение.
    /// </summary>
    /// <summary>Сколько раз строилась вершина пересечением трёх плоскостей (для замеров).</summary>
    public long VertexCalls { get; private set; }

    public int Vertex(int p1, int p2, int p3)
    {
        VertexCalls++;
        var tk = SortTriple(p1, p2, p3);
        if (_tripleCache.TryGetValue(tk, out int cached)) return cached;

        PlaneRec a = _planes[p1], b = _planes[p2], c = _planes[p3];

        BigInteger W = Det3(a.A, a.B, a.C, b.A, b.B, b.C, c.A, c.B, c.C);
        if (W.IsZero) throw new ArgumentException("three planes do not meet in a point");

        BigInteger X = Det3(-a.D, a.B, a.C, -b.D, b.B, b.C, -c.D, c.B, c.C);
        BigInteger Y = Det3(a.A, -a.D, a.C, b.A, -b.D, b.C, c.A, -c.D, c.C);
        BigInteger Z = Det3(a.A, a.B, -a.D, b.A, b.B, -b.D, c.A, c.B, -c.D);

        if (W.Sign < 0) { X = -X; Y = -Y; Z = -Z; W = -W; }

        BigInteger g = Gcd(Gcd(X, Y), Gcd(Z, W));
        if (g > BigInteger.One) { X /= g; Y /= g; Z /= g; W /= g; }

        int id = Intern(X, Y, Z, W);
        _vertices[id].On.Add(p1);
        _vertices[id].On.Add(p2);
        _vertices[id].On.Add(p3);

        _tripleCache[tk] = id;
        return id;
    }

    /// <summary>
    /// A03. Входной треугольник уже знает точную вершину сетки. Не решаем
    /// заново систему трёх плоскостей, но проверяем подсказку целой арифметикой.
    /// Неверная подсказка не меняет ответ: используется обычное пересечение.
    /// </summary>
    internal int Vertex(int p1, int p2, int p3, GridPoint known)
    {
        var tk = SortTriple(p1, p2, p3);
        if (_tripleCache.TryGetValue(tk, out int cached)) { VertexCalls++; return cached; }
        bool Contains(int p)
        {
            var plane = _planes[p];
            return (plane.A * known.X + plane.B * known.Y + plane.C * known.Z + plane.D).IsZero;
        }
        if (!Contains(p1) || !Contains(p2) || !Contains(p3))
            return Vertex(p1, p2, p3);
        // Bounds публично доступны: даже изменённый/повреждённый ввод не
        // должен превратить пересечение по прямой в допустимую вершину.
        var a = _planes[p1]; var b = _planes[p2]; var c = _planes[p3];
        if (Det3(a.A, a.B, a.C, b.A, b.B, b.C, c.A, c.B, c.C).IsZero)
            return Vertex(p1, p2, p3);
        VertexCalls++;
        int id = Intern(known.X, known.Y, known.Z, BigInteger.One);
        _vertices[id].On.Add(p1);
        _vertices[id].On.Add(p2);
        _vertices[id].On.Add(p3);
        _tripleCache[tk] = id;
        return id;
    }

    /// <summary>
    /// Приближённые координаты вершины: нужны для отсечения по габаритам, где точность не
    /// важна, а точная арифметика слишком дорога.
    /// </summary>
    public OpenTK.Mathematics.Vector3d Approx(int id)
    {
        var v = _vertices[id];
        double w = v.Wd == 0 ? 1 : v.Wd;
        double x=v.Xd/w, y=v.Yd/w, z=v.Zd/w;
        // Inf/Inf previously yielded NaN; ComponentMin/Max could discard that
        // vertex and cache a falsely small bounding box for exact clipping.
        if(!double.IsFinite(x)) x=new BigRational(v.X,v.W).ToDouble();
        if(!double.IsFinite(y)) y=new BigRational(v.Y,v.W).ToDouble();
        if(!double.IsFinite(z)) z=new BigRational(v.Z,v.W).ToDouble();
        return new OpenTK.Mathematics.Vector3d(x,y,z);
    }

    /// <summary>Вершина в точке сетки — для ввода.</summary>
    public int Vertex(GridPoint p) => Intern(p.X, p.Y, p.Z, BigInteger.One);

    private int Intern(BigInteger X, BigInteger Y, BigInteger Z, BigInteger W)
    {
        var key = new VertexKey(X, Y, Z, W);
        if (_vertexIndex.TryGetValue(key, out int id)) return id;

        id = _vertices.Count;
        _vertices.Add(new VertexRec
        {
            X = X, Y = Y, Z = Z, W = W,
            Xd = (double)X, Yd = (double)Y, Zd = (double)Z, Wd = (double)W,
            Bits = Math.Max(Math.Max(Bl(X), Bl(Y)), Math.Max(Bl(Z), Bl(W)))
        });
        Grow(ref _v4, id * 4 + 3);
        _v4[id * 4] = (double)X; _v4[id * 4 + 1] = (double)Y; _v4[id * 4 + 2] = (double)Z; _v4[id * 4 + 3] = (double)W;
        _vertexIndex[key] = id;
        return id;
    }

    /// <summary>Однородные координаты вершины, в единицах сетки.</summary>
    public (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) Homogeneous(int v)
    {
        VertexRec r = _vertices[v];
        return (r.X, r.Y, r.Z, r.W);
    }

    /// <summary>Координаты вершины в миллиметрах. Только для вывода.</summary>
    public (double X, double Y, double Z) ToMm(int v)
    {
        VertexRec r = _vertices[v];
        return (ExactGrid.ToMm(new BigRational(r.X, r.W).ToDouble()),
                ExactGrid.ToMm(new BigRational(r.Y, r.W).ToDouble()),
                ExactGrid.ToMm(new BigRational(r.Z, r.W).ToDouble()));
    }

    // ============================================================ предикат
    /// <summary>
    /// ЗНАК ПЛОСКОСТИ В ВЕРШИНЕ: −1, 0, +1. Ответ точный всегда.
    ///
    /// Сначала реестр: если вершина заведомо лежит на плоскости, ноль известен
    /// без арифметики. Дальше три ступени: double с оценкой погрешности,
    /// Int128 при достаточно коротких числах, многословная арифметика.
    /// Найденный арифметикой ноль запоминается — каждое совпадение
    /// оплачивается один раз.
    /// </summary>
    /// <summary>
    /// Сторона ГАБАРИТА относительно плоскости: +1 — весь снаружи, −1 — весь внутри, 0 — «не
    /// знаю» (тогда считает точный предикат по вершинам). Запас берётся с большим избытком:
    /// ошибиться можно только в сторону «не знаю».
    /// </summary>
    public int SideOfBox(OpenTK.Mathematics.Vector3d lo, OpenTK.Mathematics.Vector3d hi, OPlane plane)
    {
        // An unusable approximation means "unknown", never a clipping answer.
        if(!double.IsFinite(lo.X)||!double.IsFinite(lo.Y)||!double.IsFinite(lo.Z)||
           !double.IsFinite(hi.X)||!double.IsFinite(hi.Y)||!double.IsFinite(hi.Z))return 0;
        int pi = plane.Id * 4;
        return SideOfBoxCore(lo, hi, _p4[pi], _p4[pi + 1], _p4[pi + 2], _p4[pi + 3], plane.Side);
    }

    /// <summary>Коэффициенты плоскости в double (те же, что берёт SideOfBox).</summary>
    internal (double A, double B, double C, double D) PlaneDoubles(OPlane plane)
    {
        int pi = plane.Id * 4;
        return (_p4[pi], _p4[pi + 1], _p4[pi + 2], _p4[pi + 3]);
    }

    /// <summary>
    /// Тело SideOfBox для конечного габарита и готовых коэффициентов (поставка 3: выбор секущей зовёт его в плотном цикле;
    /// арифметика — та же, операция в операцию, поэтому ответы совпадают побитово).
    /// </summary>
    internal static int SideOfBoxCore(OpenTK.Mathematics.Vector3d lo, OpenTK.Mathematics.Vector3d hi,
                                      double a, double b, double c, double d, int side)
    {
        // Наименьшее и наибольшее значение a·x + b·y + c·z + d по габариту.
        double lowSum = d, highSum = d;
        lowSum += a > 0 ? a * lo.X : a * hi.X;
        highSum += a > 0 ? a * hi.X : a * lo.X;
        lowSum += b > 0 ? b * lo.Y : b * hi.Y;
        highSum += b > 0 ? b * hi.Y : b * lo.Y;
        lowSum += c > 0 ? c * lo.Z : c * hi.Z;
        highSum += c > 0 ? c * hi.Z : c * lo.Z;

        double scale = (Math.Abs(a) * Math.Max(Math.Abs(lo.X), Math.Abs(hi.X))
                      + Math.Abs(b) * Math.Max(Math.Abs(lo.Y), Math.Abs(hi.Y))
                      + Math.Abs(c) * Math.Max(Math.Abs(lo.Z), Math.Abs(hi.Z))
                      + Math.Abs(d));
        double margin = scale * 1e-9 + 1e-12;

        if (lowSum > margin) return side;
        if (highSum < -margin) return -side;
        return 0;
    }

    public int Side(int vertex, int plane)
    {
        // ---- ступень 1: double с оценкой погрешности — по плоским массивам.
        // Каждый множитель переведён в double с относительной ошибкой не больше
        // eps, произведение добавляет eps, сумма четырёх слагаемых — ещё три.
        // Множитель 16 — запас: фильтр обязан ошибаться только в сторону «не знаю».
        int pi = plane * 4, vi = vertex * 4;
        double t0 = _p4[pi] * _v4[vi], t1 = _p4[pi + 1] * _v4[vi + 1],
               t2 = _p4[pi + 2] * _v4[vi + 2], t3 = _p4[pi + 3] * _v4[vi + 3];
        double val = t0 + t1 + t2 + t3;
        double mag = Math.Abs(t0) + Math.Abs(t1) + Math.Abs(t2) + Math.Abs(t3);
        const double Eps = 1.1102230246251565e-16;

        if (!double.IsInfinity(mag) && Math.Abs(val) > 16 * Eps * mag)
        {
            Tier1++; TotalTier1++;
            int f = val > 0 ? 1 : -1;
            if (VerifyEveryAnswer) Verify(_planes[plane], _vertices[vertex], f, 1);
            return f;
        }

        VertexRec v = _vertices[vertex];
        PlaneRec p = _planes[plane];

        // Реестр известных совпадений — только там, где фильтр не уверен.
        //
        // УСКОРЕНИЕ. Прежде это множество спрашивали на каждом вызове, до всякой
        // арифметики: 8 миллионов поисков за одно скругление. Уверенный ответ
        // фильтра в нём не нуждается — вершина на плоскости дала бы значение около
        // нуля, и фильтр ответил бы «не знаю». Точность не меняется.
        if (v.On.Contains(plane)) { ShortcutHits++; TotalShortcut++; return 0; }

        int s;

        // ---- ступень 2: Int128, если произведения заведомо помещаются.
        if (p.Bits + v.Bits + 3 <= 126)
        {
            Tier2++; TotalTier2++;
            Int128 r = (Int128)p.A * (Int128)v.X + (Int128)p.B * (Int128)v.Y
                     + (Int128)p.C * (Int128)v.Z + (Int128)p.D * (Int128)v.W;
            s = Int128.Sign(r);
        }
        else
        {
            // ---- ступень 3: многословно.
            Tier3++; TotalTier3++;
            BigInteger r = p.A * v.X + p.B * v.Y + p.C * v.Z + p.D * v.W;
            s = r.Sign;
        }

        if (VerifyEveryAnswer) Verify(p, v, s, p.Bits + v.Bits + 3 <= 126 ? 2 : 3);
        if (s == 0) v.On.Add(plane);
        return s;
    }

    private static void Verify(PlaneRec p, VertexRec v, int got, int tier)
    {
        int exact = (p.A * v.X + p.B * v.Y + p.C * v.Z + p.D * v.W).Sign;
        if (exact != got)
            throw new InvalidOperationException($"predicate tier {tier} answered {got}, exact is {exact}");
    }

    /// <summary>Знак плоскости в точке сетки, ещё не заведённой как вершина.</summary>
    public int Side(GridPoint pt, OPlane plane)
    {
        PlaneRec p = _planes[plane.Id];
        BigInteger r = p.A * pt.X + p.B * pt.Y + p.C * pt.Z + p.D;
        return r.Sign * plane.Side;
    }

    public int Side(int vertex, OPlane plane) => plane.Side * Side(vertex, plane.Id);

    // ============================================================= прямые
    /// <summary>
    /// Каноническая запись прямой пересечения двух плоскостей — координаты
    /// Плюккера. Одна прямая — одна запись, какой бы парой её ни задали (этап 4).
    /// </summary>
    public (BigInteger, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger) Line(int p1, int p2)
    {
        PlaneRec a = _planes[p1], b = _planes[p2];

        BigInteger ux = a.B * b.C - a.C * b.B;
        BigInteger uy = a.C * b.A - a.A * b.C;
        BigInteger uz = a.A * b.B - a.B * b.A;
        BigInteger mx = a.D * b.A - b.D * a.A;
        BigInteger my = a.D * b.B - b.D * a.B;
        BigInteger mz = a.D * b.C - b.D * a.C;

        BigInteger g = Gcd(Gcd(Gcd(ux, uy), Gcd(uz, mx)), Gcd(my, mz));
        if (g > BigInteger.One) { ux /= g; uy /= g; uz /= g; mx /= g; my /= g; mz /= g; }

        BigInteger first = !ux.IsZero ? ux : !uy.IsZero ? uy : !uz.IsZero ? uz : !mx.IsZero ? mx : !my.IsZero ? my : mz;
        if (first.Sign < 0) { ux = -ux; uy = -uy; uz = -uz; mx = -mx; my = -my; mz = -mz; }

        return (ux, uy, uz, mx, my, mz);
    }

    // ============================================================ служебное
    private static long Bl(BigInteger x) => x.IsZero ? 0 : (long)BigInteger.Abs(x).GetBitLength();

    /// <summary>
    /// Наибольший общий делитель; для двух нулей — НОЛЬ.
    ///
    /// ИСПРАВЛЕНО. Прежде для двух нулей возвращалась единица, и в цепочке
    /// Gcd(Gcd(0, 0), …) она отравляла весь результат: плоскость (0, 0, 3, 6)
    /// не сводилась к (0, 0, 1, 2) и получала вторую запись, а одна прямая —
    /// две разные записи Плюккера. Ноль здесь — правильный ответ: он нейтрален
    /// для следующего НОД в цепочке.
    /// </summary>
    private static BigInteger Gcd(BigInteger a, BigInteger b)
        => BigInteger.GreatestCommonDivisor(a, b);

    private static BigInteger Det3(BigInteger a, BigInteger b, BigInteger c,
                                   BigInteger d, BigInteger e, BigInteger f,
                                   BigInteger g, BigInteger h, BigInteger i)
        => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

    private static TripleKey SortTriple(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return new TripleKey(a, b, c);
    }
}

/// <summary>Точка на сетке документа, в целых единицах сетки.</summary>
public readonly record struct GridPoint(long X, long Y, long Z)
{
    public static GridPoint FromMm(double x, double y, double z)
        => new(ExactGrid.Snap(x), ExactGrid.Snap(y), ExactGrid.Snap(z));
}
