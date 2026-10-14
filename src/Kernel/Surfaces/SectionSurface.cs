using System.Threading;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// Сечение поверхности по сечениям: отрезок A→B или дуга (центр, радиус, угол).
/// Параметр v ∈ [0, 1] — от начала к концу; за пределами — продолжение той же прямой
/// или окружности (проекция на поверхность неограниченная, как у плоскости и цилиндра).
/// </summary>
public readonly struct SweepSection
{
    public readonly bool IsArc;
    public readonly Vector3d A, B;                 // отрезок
    public readonly Vector3d C, E1, E2;            // дуга: C + (E1·cos φv + E2·sin φv)·R
    public readonly double R, Phi;

    private SweepSection(bool arc, Vector3d a, Vector3d b, Vector3d c, Vector3d e1, Vector3d e2, double r, double phi)
    { IsArc = arc; A = a; B = b; C = c; E1 = e1; E2 = e2; R = r; Phi = phi; }

    public static SweepSection Line(Vector3d a, Vector3d b) => new(false, a, b, default, default, default, 0, 0);

    public static SweepSection Arc(Vector3d centre, Vector3d e1, Vector3d e2, double r, double phi)
        => new(true, default, default, centre, e1, e2, r, phi);

    public Vector3d Point(double v)
    {
        if (!IsArc) return A + (B - A) * v;
        double t = Phi * v;
        return C + (E1 * Math.Cos(t) + E2 * Math.Sin(t)) * R;
    }

    /// <summary>Производная по v (не единичная).</summary>
    public Vector3d Deriv(double v)
    {
        if (!IsArc) return B - A;
        double t = Phi * v;
        return (E2 * Math.Cos(t) - E1 * Math.Sin(t)) * (R * Phi);
    }

    public SweepSection Shifted(Vector3d by) =>
        IsArc ? Arc(C + by, E1, E2, R, Phi) : Line(A + by, B + by);

    /// <summary>Кубическая интерполяция Катмулла — Рома между s1 и s2 (t ∈ [0, 1]).</summary>
    public static SweepSection CatmullRom(SweepSection s0, SweepSection s1, SweepSection s2, SweepSection s3, double t)
    {
        static Vector3d V(Vector3d a, Vector3d b, Vector3d c, Vector3d d, double t)
        {
            double t2 = t * t, t3 = t2 * t;
            return ((b * 2) + (c - a) * t + (a * 2 - b * 5 + c * 4 - d) * t2 + (b * 3 - a - c * 3 + d) * t3) * 0.5;
        }
        static double D(double a, double b, double c, double d, double t)
        {
            double t2 = t * t, t3 = t2 * t;
            return (2 * b + (c - a) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (3 * b - a - 3 * c + d) * t3) * 0.5;
        }
        if (s1.IsArc && s0.IsArc && s2.IsArc && s3.IsArc)
        {
            Vector3d e1 = V(s0.E1, s1.E1, s2.E1, s3.E1, t).Normalized();
            Vector3d e2 = V(s0.E2, s1.E2, s2.E2, s3.E2, t);
            e2 = (e2 - e1 * Vector3d.Dot(e2, e1)).Normalized();
            return Arc(V(s0.C, s1.C, s2.C, s3.C, t), e1, e2, D(s0.R, s1.R, s2.R, s3.R, t), D(s0.Phi, s1.Phi, s2.Phi, s3.Phi, t));
        }
        if (!s1.IsArc && !s0.IsArc && !s2.IsArc && !s3.IsArc)
            return Line(V(s0.A, s1.A, s2.A, s3.A, t), V(s0.B, s1.B, s2.B, s3.B, t));
        return t < 0.5 ? s1 : s2;
    }
}

/// <summary>
/// ПОВЕРХНОСТЬ ПО СЕЧЕНИЯМ — ТОЧНАЯ ПОВЕРХНОСТЬ ГРАНИ ПРОТЯНУТОГО РЕЗЦА (этап 2).
///
/// ДОБАВЛЕНО. Грань протянутой фаски или скругления приходила из булевой без исходной
/// поверхности: её знали только треугольники. Всё, что спрашивает грань — нормаль в точке,
/// проекция, кривая пересечения с соседом, — отвечало по треугольникам, с изломами на их
/// стыках. Теперь у грани есть хозяин: поверхность, по которой резец и строился.
///
/// Поверхность задана так же, как резец: сечения в плоскостях поперёк цепочки (отрезок
/// у фаски, дуга у скругления), между соседними сечениями — линейно:
///     S(u, v) = (1 − f)·Cᵢ(v) + f·Cᵢ₊₁(v),   i = ⌊u⌋, f = u − i.
/// Треугольники резца — вписанные в неё хорды: по u совпадают точно, по v отходят не
/// дальше допуска гранения дуги. Процедурная поверхность качения шара (этап 5) заменит
/// линейную вставку между сечениями, интерфейс останется тем же.
///
/// Проекция — точная: ближайшая точка находится по полосам (отсечка по габаритам),
/// в полосе — методом Гаусса — Ньютона на самой формуле поверхности.
/// </summary>
/// <remarks>
/// ГДЕ ОНА ВОЗНИКАЕТ — это пришлось выяснять отдельно, и вот что намерено:
///   — скругление ПРЯМОГО ребра между плоскостями даёт цилиндр (ExtrudedSurface);
///   — скругление КРОМКИ ОТВЕРСТИЯ даёт тор (RevolvedSurface);
///   — поверхность сечений остаётся для случаев, где ни то, ни другое не подходит, — на
///     проверочной сцене это три грани из одиннадцати, и они занимают 252 мс из 331 мс всей
///     разметки.
///
/// Попытка померить, что внутри неё дорого — опрос точек, нормалей или проекция, — не удалась:
/// счётчики обращений не попали в отчёт, а два подступа к воспроизводимому образцу дали не ту
/// поверхность. Прежде чем ускорять, нужен образец, который строит именно её, и замер по её
/// собственным вызовам.
/// </remarks>
public sealed class SectionSurface : ISurface3
{
    private readonly SweepSection[] _s;
    public readonly bool Closed;

    /// <summary>
    /// Сечение как функция параметра хребта — точное определение поверхности. null —
    /// поверхность линейна между сечениями (u — номер сечения).
    /// </summary>
    private readonly Func<double, SweepSection>? _gen;
    private readonly double[]? _stationU;          // параметр хребта у каждого сечения
    private readonly double _u0, _u1;              // промежуток u (у кольца — период [_u0, _u1))

    /// <summary>Поверхность задана формулой сечения (а не вставкой между сечениями).</summary>
    public bool IsProcedural => _gen != null;

    /// <summary>Запас по v за концами сечения при проекции (доля сечения).</summary>
    private const double VReach = 0.5;

    private readonly Vector3d[] _lo, _hi;          // габариты полос (с запасом по v)
    private readonly bool _planar;

    public IReadOnlyList<SweepSection> Sections => _s;

    /// <summary>Число полос: u ∈ [0, Strips].</summary>
    public int Strips => Closed ? _s.Length : _s.Length - 1;

    /// <summary>
    /// Поверхность по формуле сечения gen(u), u ∈ [u0, u1]. Сечения в stationU — пробы
    /// той же формулы: по ним ищется затравка проекции и строятся габариты.
    /// </summary>
    public SectionSurface(Func<double, SweepSection> gen, IReadOnlyList<double> stationU,
                          IReadOnlyList<SweepSection> stations, bool closed, double u0, double u1,
                          IReadOnlyList<double>? breaks = null, double tableStep = 0)
        : this(stations, closed)
    {
        _tableStep = tableStep;
        if (stationU.Count != stations.Count) throw new ArgumentException("one parameter per station");
        _gen = gen; _stationU = stationU.ToArray(); _u0 = u0; _u1 = u1;
        _breaks = (breaks ?? Array.Empty<double>()).Where(b => b > u0 + 1e-9 && b < u1 - 1e-9).OrderBy(b => b).ToArray();
    }

    /// <summary>Стыки рёбер хребта и продлений: там формула гладкая лишь первого порядка.</summary>
    private readonly double[] _breaks = Array.Empty<double>();
    /// <summary>Стыки формулы по u (квадратуры делят промежуток в них).</summary>
    public IReadOnlyList<double> Breaks => _breaks;

    public SectionSurface(IReadOnlyList<SweepSection> sections, bool closed)
    {
        if (sections.Count < 2) throw new ArgumentException("at least two sections are needed");
        _s = sections.ToArray();
        Closed = closed;

        int n = Strips;
        _lo = new Vector3d[n]; _hi = new Vector3d[n];
        for (int i = 0; i < n; i++)
        {
            var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
            foreach (var sec in new[] { _s[i], _s[(i + 1) % _s.Length] })
                for (int k = 0; k <= 16; k++)
                {
                    Vector3d p = sec.Point(-VReach + (1 + 2 * VReach) * k / 16.0);
                    lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p);
                }
            // Дуга между пробами выпирает не больше чем на R·(1 − cos(Δ/2)).
            double pad = 1e-9;
            foreach (var sec in new[] { _s[i], _s[(i + 1) % _s.Length] })
                if (sec.IsArc) pad = Math.Max(pad, sec.R * (1 - Math.Cos(Math.Abs(sec.Phi) * (1 + 2 * VReach) / 32)));
            _lo[i] = lo - new Vector3d(pad); _hi[i] = hi + new Vector3d(pad);
        }
        _planar = ComputePlanar();
    }

    public bool IsPlanar => _planar;

    // ------------------------------------------------------------ формула

    private (int I, double F) Split(double u)
    {
        int n = Strips;
        if (Closed)
        {
            u %= n; if (u < 0) u += n;
            int i = Math.Min((int)Math.Floor(u), n - 1);
            return (i, u - i);
        }
        u = Math.Clamp(u, 0, n);
        int j = Math.Min((int)Math.Floor(u), n - 1);
        return (j, u - j);
    }

    private SweepSection Next(int i) => _s[(i + 1) % _s.Length];

    private Vector3d Eval(int i, double f, double v) => _s[i].Point(v) * (1 - f) + Next(i).Point(v) * f;

    /// <summary>Сечение при параметре хребта u (процедурный режим).</summary>
    public SweepSection SectionAt(double u)
    {
        if (_gen == null)
        {
            var (i, f) = Split(u);
            var a = _s[i]; var b = Next(i);
            // Смесь двух сечений — не сечение одного вида; для вызывающих — ближайшее.
            return f < 0.5 ? a : b;
        }
        u = WrapU(u);
        var tab = Table();
        // Кусок таблицы между стыками хребта; интерполяция — только внутри куска.
        int piece = Array.BinarySearch(tab.Start, u);
        if (piece < 0) piece = ~piece - 1;
        piece = Math.Clamp(piece, 0, tab.Pieces.Length - 1);
        var (h, sec) = tab.Pieces[piece];
        double x = (u - tab.Start[piece]) / h;
        int ti = Math.Clamp((int)Math.Floor(x), 0, sec.Length - 2);
        double tf = Math.Clamp(x - ti, 0, 1);
        int n = sec.Length;
        bool wrap = Closed && tab.Pieces.Length == 1;
        SweepSection At(int k)
        {
            if (wrap) return sec[((k % (n - 1)) + (n - 1)) % (n - 1)];
            if (k >= 0 && k < n) return sec[k];
            // За концом куска — узел, продолженный по своей стороне (кривизна за стыком другая): квадратично по трём узлам
            // (KRN-06: у стыка закона радиуса линейный узел давал 2e-6 мм отхода таблицы от формулы в крайнем звене);
            // P3D_SECEXTRAP2=0 — линейно, как прежде.
            if (Quad && n >= 3) return k < 0 ? Extrap2(sec[0], sec[1], sec[2], -k) : Extrap2(sec[n - 1], sec[n - 2], sec[n - 3], k - (n - 1));
            return k < 0 ? Extrap(sec[0], sec[1], -k) : Extrap(sec[n - 1], sec[n - 2], k - (n - 1));
        }
        return SweepSection.CatmullRom(At(ti - 1), At(ti), At(ti + 1), At(ti + 2), tf);
    }

    static readonly bool Quad = Environment.GetEnvironmentVariable("P3D_SECEXTRAP2") != "0";

    /// <summary>Продолжение за конец по трём узлам (многочлен Ньютона второй степени): a − kΔ₁ + k(k+1)/2·Δ₂.</summary>
    private static SweepSection Extrap2(SweepSection a, SweepSection b, SweepSection c, int steps)
    {
        double k = steps, c1 = -k, c2 = k * (k + 1) / 2;
        Vector3d V(Vector3d pa, Vector3d pb, Vector3d pc) => pa + (pb - pa) * c1 + (pa - pb * 2 + pc) * c2;
        double D(double pa, double pb, double pc) => pa + (pb - pa) * c1 + (pa - 2 * pb + pc) * c2;
        if (a.IsArc && b.IsArc && c.IsArc)
            return SweepSection.Arc(V(a.C, b.C, c.C), V(a.E1, b.E1, c.E1).Normalized(), V(a.E2, b.E2, c.E2).Normalized(), D(a.R, b.R, c.R), D(a.Phi, b.Phi, c.Phi));
        if (!a.IsArc && !b.IsArc && !c.IsArc) return SweepSection.Line(V(a.A, b.A, c.A), V(a.B, b.B, c.B));
        return Extrap(a, b, steps);
    }

    private static SweepSection Extrap(SweepSection a, SweepSection b, int steps)
    {
        // a + (a − b)·steps, покомпонентно.
        double k = steps;
        if (a.IsArc && b.IsArc)
            return SweepSection.Arc(a.C + (a.C - b.C) * k, (a.E1 + (a.E1 - b.E1) * k).Normalized(),
                                    (a.E2 + (a.E2 - b.E2) * k).Normalized(), a.R + (a.R - b.R) * k, a.Phi + (a.Phi - b.Phi) * k);
        if (!a.IsArc && !b.IsArc) return SweepSection.Line(a.A + (a.A - b.A) * k, a.B + (a.B - b.B) * k);
        return a;
    }

    /// <summary>
    /// KRN-06 (06.12): ТА ЖЕ ПОВЕРХНОСТЬ ПОД ЖЁСТКИМ ПРЕОБРАЗОВАНИЕМ — формула сечения переносится (каждое сечение —
    /// отображением точек), параметры, пробы, стыки и шаг таблицы — те же. null — поверхность не по формуле.
    /// </summary>
    public SectionSurface? Mapped(Func<SweepSection, SweepSection> map)
    {
        if (_gen == null || _stationU == null) return null;
        var g = _gen;
        return new SectionSurface(u => map(g(u)), _stationU, _s.Select(map).ToList(), Closed, _u0, _u1, _breaks, _tableStep);
    }

    /// <summary>Сечение по самой формуле (без таблицы) — для проверок.</summary>
    public SweepSection ExactSectionAt(double u) => _gen != null ? _gen(WrapU(u)) : SectionAt(u);

    // ТАБЛИЦА ФОРМУЛЫ. Формула сечения скругления сама проецирует точки на процедурную
    // поверхность соседней грани — Ньютон внутри Ньютона; проекция на скругление стоила
    // сотни микросекунд, и сборка рёбер одной операции скругления шла 11 с, у пользователя в
    // отладочной сборке — минуты (снимок 22.09). Формула табулируется один раз с шагом не
    // больше 0.5 мм и десятой доли ширины сечения; между узлами — кубическая интерполяция
    // Катмулла — Рома (погрешность ~h⁴·кривизна³ — нанометры). Так работают промышленные ядра
    // с поверхностями качения шара: формула — определение, в работе — плотное приближение.
    private sealed record Tab(double[] Start, (double H, SweepSection[] S)[] Pieces);
    private Tab? _tab;
    /// <summary>KRN-06 (06.12): шаг таблицы формулы, заданный построителем (0 — по умолчанию: 0.25 мм и 1/20 ширины). Лента
    /// катящегося шара просит мельче: кубическая вставка с шагом 0.15 мм отходила от формулы на 1e-6 мм при допуске p-кривой 1e-7.</summary>
    private readonly double _tableStep;

    private Tab Table()
    {
        var t = _tab;
        if (t != null) return t;
        var mid = _s[_s.Length / 2];
        double width = Math.Max((mid.Point(1) - mid.Point(0)).Length, 1e-3);
        double h0 = _tableStep > 0 ? _tableStep : Math.Min(0.25, width / 20);
        var cuts = new List<double> { _u0 };
        cuts.AddRange(_breaks);
        cuts.Add(_u1);
        var starts = new List<double>();
        var pieces = new List<(double, SweepSection[])>();
        for (int p = 0; p + 1 < cuts.Count; p++)
        {
            double a = cuts[p], b = cuts[p + 1];
            if (b - a < 1e-12) continue;
            int n = Math.Clamp((int)Math.Ceiling((b - a) / h0), 2, 20000);
            double h = (b - a) / n;
            var arr = new SweepSection[n + 1];
            // Узлы на самом стыке — изнутри куска: формула на стыке берёт одно из рёбер.
            for (int k = 0; k <= n; k++)
            {
                double u = a + h * k;
                if (k == 0 && p > 0) u += 1e-9;
                if (k == n && p + 2 < cuts.Count) u -= 1e-9;
                arr[k] = _gen!(u);
            }
            starts.Add(a); pieces.Add((h, arr));
        }
        t = new Tab(starts.ToArray(), pieces.ToArray());
        _tab = t;
        return t;
    }

    private double WrapU(double u)
    {
        if (Closed)
        {
            double per = _u1 - _u0;
            u = (u - _u0) % per; if (u < 0) u += per;
            return _u0 + u;
        }
        return Math.Clamp(u, _u0, _u1);
    }

    /// <summary>Шаг разностной производной по u (u — длина дуги хребта, мм).</summary>
    private double H => 1e-7 * Math.Max(1, _u1 - _u0);

    /// <summary>Промежуток параметра хребта (процедурный режим) или номеров сечений.</summary>
    public double UMin => _gen != null ? _u0 : 0;
    public double UMax => _gen != null ? _u1 : Strips;

    private (Vector3d P, Vector3d Su, Vector3d Sv) Frame(double u, double v)
    {
        // Односторонняя разность: сечение в u обычно уже в памяти (его только что
        // спросили), и производная стоит одного вызова формулы, а не двух. Точности
        // O(h) хватает — нормаль и направление шага, а не сама точка.
        u = WrapU(u);
        var sec = SectionAt(u);
        double h = H;
        double ub = u + h;
        if (!Closed && ub > _u1) ub = u - h;
        Vector3d su = (SectionAt(ub).Point(v) - sec.Point(v)) / (ub - u);
        return (sec.Point(v), su, sec.Deriv(v));
    }

    public Vector3d PointAt(Vector2d uv)
    {
        if (_gen != null) return SectionAt(uv.X).Point(uv.Y);
        var (i, f) = Split(uv.X);
        return Eval(i, f, uv.Y);
    }

    public Vector3d NormalAt(Vector2d uv)
    {
        if (_gen != null)
        {
            var (_, su, sv) = Frame(uv.X, uv.Y);
            Vector3d c = Vector3d.Cross(su, sv);
            return c.LengthSquared < 1e-24 ? Vector3d.UnitZ : c.Normalized();
        }
        var (i, f) = Split(uv.X);
        Vector3d n = NormalIn(i, f, uv.Y);
        // Сечения совпали (вырожденная полоса) — нормаль соседней полосы.
        if (n.LengthSquared < 1e-24 && Strips > 1)
        {
            int j = f < 0.5 ? (Closed ? (i - 1 + Strips) % Strips : Math.Max(0, i - 1))
                            : (Closed ? (i + 1) % Strips : Math.Min(Strips - 1, i + 1));
            n = NormalIn(j, f < 0.5 ? 1 : 0, uv.Y);
        }
        return n.LengthSquared < 1e-24 ? Vector3d.UnitZ : n.Normalized();
    }

    private Vector3d NormalIn(int i, double f, double v)
    {
        Vector3d su = Next(i).Point(v) - _s[i].Point(v);
        Vector3d sv = _s[i].Deriv(v) * (1 - f) + Next(i).Deriv(v) * f;
        return Vector3d.Cross(su, sv);
    }

    // ------------------------------------------------------------ проекция

    public Vector2d ProjectToUV(Vector3d p) => Project(p, out _);

    /// <summary>Ближайшая точка поверхности: параметры и квадрат расстояния.</summary>
    public Vector2d Project(Vector3d p, out double dist2)
    {
        // ЗАТРАВКА — БЛИЖАЙШАЯ ТОЧКА СЕТКИ ПРОБ (сечения × GridV значений v).
        //
        // ИСПРАВЛЕНО (этап 2, скорость). Прежде уточнение шло в каждой полосе, чья
        // коробка ближе найденного: с запасом по v коробки перекрываются, и на грани
        // фаски в 20 полос проекция стоила 94 мкс, а проверочный прогон шёл в шесть раз
        // медленнее. Расстояние до ближайшей пробы — верхняя граница ответа: полосы
        // с коробкой дальше неё отсекаются сразу.
        var grid = Grid();
        int gs = 0, gk = 0; double gd = double.MaxValue;
        for (int i = 0; i < _s.Length; i++)
            for (int k = 0; k <= GridV; k++)
            {
                double d = (grid[i * (GridV + 1) + k] - p).LengthSquared;
                if (d < gd) { gd = d; gs = i; gk = k; }
            }
        double gv = -VReach + (1 + 2 * VReach) * gk / GridV;

        if (_gen != null) return ProjectProcedural(p, out dist2);

        int n = Strips;
        double best = double.MaxValue; Vector2d bu = new Vector2d(Math.Min(gs, n), gv);
        var order = new List<(double D, int I)>();
        for (int i = 0; i < n; i++)
        {
            double d = BoxDist2(p, _lo[i], _hi[i]);
            if (d <= gd) order.Add((d, i));
        }
        order.Sort((a, b) => a.D.CompareTo(b.D));
        foreach (var (d, i) in order)
        {
            if (d > best) break;
            var (f, v, q) = InStrip(i, p);
            if (q < best) { best = q; bu = new Vector2d(i + f, v); }
        }
        dist2 = best == double.MaxValue ? gd : best;
        return bu;
    }

    // ------------------------------------------------------------ проекция по формуле

    /// <summary>
    /// Плотная сетка проб формулы: ряды по u с шагом не больше четверти ширины сечения
    /// (и не меньше 0.5 мм), в каждом — GridV + 1 значений v.
    ///
    /// ИСПРАВЛЕНО. Сетка стояла только в сечениях резца, а на прямом участке цепочки их
    /// два — по концам, через 80 мм. Ближайшей пробой для точки в середине участка
    /// оказывалась проба на продолжении сечения у начала дуги (v = −0.5), уточнение
    /// упиралось в край области, и точка фаски вставала за 38 мм от нужной: протянутая
    /// фаска по краю кармана на этой грани «пересекала сама себя» (снимок 22.09).
    /// </summary>
    private (double[] U, Vector3d[] P)? _dense;

    private (double[] U, Vector3d[] P) Dense()
    {
        var d = _dense;
        if (d != null) return d.Value;
        var us = new List<double>();
        int ns = _s.Length;
        int strips = Closed ? ns : ns - 1;
        for (int i = 0; i < strips; i++)
        {
            double ua = _stationU![i];
            double ub = i + 1 < ns ? _stationU[i + 1] : _stationU[0] + (_u1 - _u0);
            double width = Math.Max((_s[i].Point(1) - _s[i].Point(0)).Length, 1e-6);
            double step = Math.Max(0.5, 0.25 * width);
            int m = Math.Clamp((int)Math.Ceiling((ub - ua) / step), 1, 400);
            for (int j = 0; j < m; j++) us.Add(ua + (ub - ua) * j / m);
        }
        if (!Closed) us.Add(_stationU![ns - 1]);
        var pts = new Vector3d[us.Count * (GridV + 1)];
        for (int r = 0; r < us.Count; r++)
        {
            var sec = SectionAt(us[r]);
            for (int k = 0; k <= GridV; k++)
                pts[r * (GridV + 1) + k] = sec.Point(-VReach + (1 + 2 * VReach) * k / GridV);
        }
        var res = (us.ToArray(), pts);
        _dense = res;
        return res;
    }

    // ТЁПЛЫЙ СТАРТ. Соседние запросы почти всегда рядом: вершины треугольников подряд,
    // точка хребта соседнего сечения. Формула сечения скругления сама проецирует точки на
    // процедурную поверхность соседней грани (Ньютон внутри Ньютона), и полный поиск по
    // плотной сетке на каждый запрос делал перестроение сцены пользователя дольше пяти
    // минут. Уточнение от прошлого ответа, сошедшееся ближе 0.05 мм (вершины треугольников отходят на стрелу прогиба), принимается
    // сразу; иначе — полный поиск.
    private sealed record Warm(double U, double V);
    // ПОДСКАЗКА — ПОТОКОВАЯ. Это начальное приближение для поиска ближайшей точки. При работе в
    // несколько потоков нити затирали подсказку друг друга, поиск стартовал не оттуда и сходился
    // к другому решению: радиус кромки скругления плавал между прогонами (26.2, 26.7, 25.8 при
    // заданных 26), и тест падал через раз.
    private readonly ThreadLocal<Warm?> _warmTl = new();

    private Warm? _warm
    {
        get => _warmTl.Value;
        set => _warmTl.Value = value;
    }
    private const double WarmAccept = 0.05;       // выше допуска хорд (Tol.Chord = 0.02)

    private Vector2d ProjectProcedural(Vector3d p, out double dist2)
    {
        var w = _warm;
        if (w != null)
        {
            var uvw = Refine(p, w.U, w.V, out double dw);
            if (dw < WarmAccept * WarmAccept && IsFoot(p, uvw))
            {
                _warm = new Warm(uvw.X, uvw.Y);
                dist2 = dw;
                return uvw;
            }
        }
        var res = ProjectFull(p, out dist2);
        _warm = new Warm(res.X, res.Y);
        return res;
    }

    /// <summary>
    /// Точка uv — основание перпендикуляра из p: отклонение p от поверхности направлено по
    /// нормали (касательные составляющие — на уровне округления). ИСПРАВЛЕНО: тёплый старт
    /// принимал любое уточнение ближе 0.05 мм, в том числе недосошедшееся; проекция была
    /// неточной, и кривая пересечения скругления R20 с фаской кармана не уточнялась — край
    /// фаски рассыпался на отрезки, и скругление R5 по нему отказывало (снимок 22.09).
    /// </summary>
    private bool IsFoot(Vector3d p, Vector2d uv)
    {
        var (x, su, sv) = Frame(uv.X, uv.Y);
        Vector3d r = p - x;
        double tol = 1e-9 + 1e-7 * r.Length;
        bool edgeU = !Closed && (uv.X <= _u0 + 1e-12 || uv.X >= _u1 - 1e-12);
        bool edgeV = uv.Y <= -VReach + 1e-12 || uv.Y >= 1 + VReach - 1e-12;
        double lu = su.Length, lv = sv.Length;
        bool okU = edgeU || lu < 1e-30 || Math.Abs(Vector3d.Dot(r, su)) / lu <= tol;
        bool okV = edgeV || lv < 1e-30 || Math.Abs(Vector3d.Dot(r, sv)) / lv <= tol;
        return okU && okV;
    }

    private Vector2d ProjectFull(Vector3d p, out double dist2)
    {
        var (us, pts) = Dense();
        int rows = us.Length, cols = GridV + 1;

        // Три лучшие пробы из разных мест (ряды дальше двух друг от друга).
        var cand = new (double D, int R, int K)[3];
        for (int c = 0; c < 3; c++) cand[c] = (double.MaxValue, -10, 0);
        for (int r = 0; r < rows; r++)
        {
            double br = double.MaxValue; int bk = 0;
            for (int k = 0; k < cols; k++)
            {
                double d = (pts[r * cols + k] - p).LengthSquared;
                if (d < br) { br = d; bk = k; }
            }
            // Ряд рядом с уже взятым — заменяет его, если лучше; иначе вытесняет худший.
            int near = -1;
            for (int c = 0; c < 3; c++) if (Math.Abs(cand[c].R - r) <= 2) { near = c; break; }
            if (near >= 0) { if (br < cand[near].D) cand[near] = (br, r, bk); }
            else
            {
                int worst = 0;
                for (int c = 1; c < 3; c++) if (cand[c].D > cand[worst].D) worst = c;
                if (br < cand[worst].D) cand[worst] = (br, r, bk);
            }
        }
        Array.Sort(cand, (a, b) => a.D.CompareTo(b.D));

        double best = double.MaxValue; Vector2d bu = Vector2d.Zero;
        foreach (var (gd, r, k) in cand)
        {
            if (r < 0) continue;
            // Доля вдоль соседних рядов: проекция на отрезки между пробами того же v.
            double su = us[r];
            double sd = gd;
            foreach (int j in new[] { r - 1, r })
            {
                int a = j, b = j + 1;
                if (Closed) { a = (a + rows) % rows; b = (b + rows) % rows; }
                else if (a < 0 || b >= rows) continue;
                Vector3d pa = pts[a * cols + k], pb = pts[b * cols + k];
                Vector3d dd = pb - pa;
                double l2 = dd.LengthSquared;
                if (l2 < 1e-30) continue;
                double f = Math.Clamp(Vector3d.Dot(p - pa, dd) / l2, 0, 1);
                double q = (pa + dd * f - p).LengthSquared;
                if (q < sd)
                {
                    double ua = us[a], ub = us[b];
                    if (Closed && b == 0) ub = us[0] + (_u1 - _u0);
                    sd = q; su = ua + (ub - ua) * f;
                }
            }
            double gv = -VReach + (1 + 2 * VReach) * k / GridV;
            var uv = Refine(p, su, gv, out double d2);
            if (d2 < best) { best = d2; bu = uv; }
            // Уточнение сошлось лучше пробы вдвое (или точка на поверхности) — ответ найден;
            // иначе затравка могла попасть в чужое место, и пробуются следующие.
            if (best <= 0.25 * gd || best < 1e-18) break;
        }
        dist2 = best;
        return bu;
    }

    private const int GridV = 8;
    private Vector3d[]? _grid;

    private Vector3d[] Grid()
    {
        var g = _grid;
        if (g != null) return g;
        g = new Vector3d[_s.Length * (GridV + 1)];
        for (int i = 0; i < _s.Length; i++)
            for (int k = 0; k <= GridV; k++)
                g[i * (GridV + 1) + k] = _s[i].Point(-VReach + (1 + 2 * VReach) * k / GridV);
        return _grid = g;
    }

    /// <summary>
    /// Уточнение по формуле (Гаусс — Ньютон) от затравки (u, v).
    /// </summary>
    private Vector2d Refine(Vector3d p, double u, double v, out double dist2)
    {
        u = WrapU(u);

        double bd = (SectionAt(u).Point(v) - p).LengthSquared;
        for (int it = 0; it < 30; it++)
        {
            var (x, su, sv) = Frame(u, v);
            Vector3d r = p - x;
            double a11 = Vector3d.Dot(su, su), a12 = Vector3d.Dot(su, sv), a22 = Vector3d.Dot(sv, sv);
            double b1 = Vector3d.Dot(su, r), b2 = Vector3d.Dot(sv, r);
            double det = a11 * a22 - a12 * a12;
            double du, dv;
            if (Math.Abs(det) > 1e-30 * Math.Max(1, a11 * a22)) { du = (b1 * a22 - b2 * a12) / det; dv = (a11 * b2 - a12 * b1) / det; }
            else { du = a11 > 1e-30 ? b1 / a11 : 0; dv = a22 > 1e-30 ? b2 / a22 : 0; }

            double step = 1; bool moved = false;
            for (int h = 0; h < 12; h++)
            {
                double nu = WrapU(u + du * step);
                double nv = Math.Clamp(v + dv * step, -VReach, 1 + VReach);
                Vector3d np = SectionAt(nu).Point(nv);
                double nd = (np - p).LengthSquared;
                if (nd <= bd)
                {
                    // Сдвиг точки поверхности меньше 1e-10 мм — сошлось.
                    moved = (np - x).LengthSquared > 1e-20;
                    u = nu; v = nv; bd = nd;
                    break;
                }
                step *= 0.5;
            }
            if (!moved) break;
        }
        dist2 = bd;
        return new Vector2d(u, v);
    }

    private static double BoxDist2(Vector3d p, Vector3d lo, Vector3d hi)
    {
        double dx = Math.Max(0, Math.Max(lo.X - p.X, p.X - hi.X));
        double dy = Math.Max(0, Math.Max(lo.Y - p.Y, p.Y - hi.Y));
        double dz = Math.Max(0, Math.Max(lo.Z - p.Z, p.Z - hi.Z));
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Ближайшая точка в полосе i: f ∈ [0, 1], v ∈ [−VReach, 1 + VReach].</summary>
    private (double F, double V, double D2) InStrip(int i, Vector3d p)
    {
        SweepSection s0 = _s[i], s1 = Next(i);

        // Затравка: лучшая из проб по сетке 3×9.
        double bf = 0.5, bv = 0.5, bd = double.MaxValue;
        for (int a = 0; a <= 2; a++)
            for (int k = 0; k <= 8; k++)
            {
                double f = a / 2.0, v = -VReach + (1 + 2 * VReach) * k / 8.0;
                double d = (Eval(i, f, v) - p).LengthSquared;
                if (d < bd) { bd = d; bf = f; bv = v; }
            }

        // Гаусс — Ньютон с ограничениями и дроблением шага.
        for (int it = 0; it < 40; it++)
        {
            Vector3d c0 = s0.Point(bv), c1 = s1.Point(bv);
            Vector3d x = c0 * (1 - bf) + c1 * bf;
            Vector3d su = c1 - c0;
            Vector3d sv = s0.Deriv(bv) * (1 - bf) + s1.Deriv(bv) * bf;
            Vector3d r = p - x;

            double a11 = Vector3d.Dot(su, su), a12 = Vector3d.Dot(su, sv), a22 = Vector3d.Dot(sv, sv);
            double b1 = Vector3d.Dot(su, r), b2 = Vector3d.Dot(sv, r);
            double det = a11 * a22 - a12 * a12;
            double df, dv;
            if (Math.Abs(det) > 1e-30 * Math.Max(1, a11 * a22))
            {
                df = (b1 * a22 - b2 * a12) / det;
                dv = (a11 * b2 - a12 * b1) / det;
            }
            else
            {
                // Вырожденная полоса — только по v (или только по f).
                df = a11 > 1e-30 ? b1 / a11 : 0;
                dv = a22 > 1e-30 ? b2 / a22 : 0;
            }

            double step = 1; bool moved = false;
            for (int h = 0; h < 12; h++)
            {
                double nf = Math.Clamp(bf + df * step, 0, 1);
                double nv = Math.Clamp(bv + dv * step, -VReach, 1 + VReach);
                Vector3d np = Eval(i, nf, nv);
                double nd = (np - p).LengthSquared;
                if (nd <= bd)
                {
                    moved = (np - x).LengthSquared > 1e-20;
                    bf = nf; bv = nv; bd = nd;
                    break;
                }
                step *= 0.5;
            }
            if (!moved) break;
        }
        return (bf, bv, bd);
    }

    // ------------------------------------------------------------ плоскость?

    private bool ComputePlanar()
    {
        if (_s.Any(x => x.IsArc)) return false;
        var pts = new List<Vector3d>();
        foreach (var x in _s) { pts.Add(x.A); pts.Add(x.B); }

        Vector3d o = pts[0];
        Vector3d far = pts.OrderByDescending(q => (q - o).LengthSquared).First();
        Vector3d d = far - o;
        if (d.LengthSquared < 1e-24) return false;
        d = d.Normalized();
        double scale = Math.Sqrt((far - o).LengthSquared);

        Vector3d? nrm = null; double bestArea = 0;
        foreach (var q in pts)
        {
            Vector3d c = Vector3d.Cross(d, q - o);
            if (c.LengthSquared > bestArea) { bestArea = c.LengthSquared; nrm = c; }
        }
        if (nrm == null || bestArea < 1e-18) return false;
        Vector3d nn = nrm.Value.Normalized();
        double tol = Math.Max(1e-9, 1e-9 * scale);
        return pts.All(q => Math.Abs(Vector3d.Dot(q - o, nn)) <= tol);
    }

    // ------------------------------------------------------------ гранение

    /// <summary>
    /// Прямое гранение (сетка сечения × полосы). Грани резца после булевой гранятся
    /// своими треугольниками (FacetedSurface), это — для грани, несущей поверхность прямо.
    /// </summary>
    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        int nv = 1;
        foreach (var x in _s)
            if (x.IsArc) nv = Math.Max(nv, (int)Math.Ceiling(Math.Abs(x.Phi) / Core.Tol.ChordAngle - 1e-9));
        int nu = Strips;
        int start = mb.VertexCount;
        for (int i = 0; i <= nu; i++)
            for (int j = 0; j <= nv; j++)
            {
                double v = j / (double)nv;
                int si = i == nu && Closed ? 0 : i;
                Vector2d uv = _gen != null ? new Vector2d(_stationU![si], v) : new Vector2d(si, v);
                Vector3d n = _gen != null ? NormalAt(uv) : NormalAt(new Vector2d(Math.Min(i, nu - 1e-9), v));
                if (face.Reversed) n = -n;
                mb.AddVertex(PointAt(uv), n);
            }
        int stride = nv + 1;
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                int a = start + i * stride + j, b = a + 1, c = a + stride, d = c + 1;
                if (face.Reversed) { mb.AddTriangle(a, c, b); mb.AddTriangle(b, c, d); }
                else { mb.AddTriangle(a, b, c); mb.AddTriangle(b, d, c); }
            }
    }
}

/// <summary>
/// ИЗОЛИНИЯ ПОВЕРХНОСТИ ПО СЕЧЕНИЯМ: v = const, u ∈ [U0, U1] — линия касания скругления
/// с гранью (v = 0 или 1) или край фаски.
///
/// ДОБАВЛЕНО. Скругление касается соседних граней, и кривая пересечения на шве не
/// уточняется: касательные плоскости обеих поверхностей почти совпадают. Шов оставался
/// хордами с рёбрами в микроны между ними (край кармана после фаски, R5 — снимок 22.09).
/// Шов — граница самой поверхности скругления, и точная кривая известна без пересечения.
///
/// Концы — точно вершины тела (как у кривой пересечения): каркас в углах не расходится.
/// Параметр t ∈ [0, 1] — доля промежутка u.
/// </summary>
public sealed class SectionIsoCurve : Curves.ICurve3
{
    public readonly SectionSurface Surface;
    public readonly double V, U0, U1;
    private readonly Vector3d _p0, _p1;
    private Vector3d[]? _pts;

    /// <summary>
    /// Затравка — вершины тела на ребре (от начала к концу). По ним ставятся сечения резца
    /// следующей фаски или скругления: стороны резца совпадают с хордами граней. Без неё
    /// сечения шли только по концам ребра, резец срезал хордой поперёк кармана и
    /// складывался (снимок пользователя 22.09, R5 по кромке фаски 10×5).
    /// </summary>
    public IReadOnlyList<Vector3d> Seed => _seed;
    private readonly Vector3d[] _seed;

    public SectionIsoCurve(SectionSurface s, double v, double u0, double u1, Vector3d p0, Vector3d p1,
                           IReadOnlyList<Vector3d>? seed = null)
    {
        Surface = s; V = v; U0 = u0; U1 = u1; _p0 = p0; _p1 = p1;
        _seed = seed?.ToArray() ?? new[] { p0, p1 };
        _d0 = p0 - s.PointAt(new Vector2d(u0, v));
        _d1 = p1 - s.PointAt(new Vector2d(u1, v));
    }

    // Отход вершин тела от изолинии (сетка ядра, хорды). Распределяется вдоль кривой
    // линейно: кривая проходит через вершины и остаётся гладкой. Прибитые только в самих
    // концах, они давали излом у вершины — направление «от вершины к точке через 1e-4»
    // смотрело куда попало, и цепочка по кромке рвалась.
    private readonly Vector3d _d0, _d1;

    private Vector3d Raw(double t) => Surface.PointAt(new Vector2d(U0 + (U1 - U0) * t, V)) + _d0 * (1 - t) + _d1 * t;

    public double T0 => 0.0;
    public double T1 => 1.0;
    public bool IsClosed => false;

    public Vector3d PointAt(double t)
    {
        if (t <= 0) return _p0;
        if (t >= 1) return _p1;
        return Raw(t);
    }

    public Vector3d TangentAt(double t)
    {
        const double h = 1e-6;
        double a = Math.Max(0, t - h), b = Math.Min(1, t + h);
        Vector3d d = Raw(b) - Raw(a);
        return d.LengthSquared > 0 ? d.Normalized() : (_p1 - _p0).Normalized();
    }

    private Vector3d[] Points()
    {
        if (_pts != null) return _pts;
        // Число точек — по повороту касательной (шаг гранения дуги) и не реже 2 мм.
        const int probe = 64;
        var q = new Vector3d[probe + 1];
        for (int i = 0; i <= probe; i++) q[i] = PointAt(i / (double)probe);
        double turn = 0, len = 0;
        for (int i = 1; i < probe; i++)
        {
            Vector3d a = q[i] - q[i - 1], b = q[i + 1] - q[i];
            if (a.LengthSquared > 0 && b.LengthSquared > 0)
                turn += Math.Acos(Math.Clamp(Vector3d.Dot(a.Normalized(), b.Normalized()), -1, 1));
        }
        for (int i = 1; i <= probe; i++) len += (q[i] - q[i - 1]).Length;
        int n = Math.Clamp(Math.Max((int)Math.Ceiling(turn / Core.Tol.ChordAngle), (int)Math.Ceiling(len / 2.0)), 1, 512);
        var r = new Vector3d[n + 1];
        for (int i = 0; i <= n; i++) r[i] = PointAt(i / (double)n);
        return _pts = r;
    }

    public double Length { get { var p = Points(); double l = 0; for (int i = 1; i < p.Length; i++) l += (p[i] - p[i - 1]).Length; return l; } }

    public Bounds3 Bounds { get { var b = Bounds3.Empty; foreach (var p in Points()) b.Add(p); return b; } }

    public Curves.ICurve3 Reversed() => new SectionIsoCurve(Surface, V, U1, U0, _p1, _p0, Enumerable.Reverse(_seed).ToArray());

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        var p = Points();
        for (int i = includeFirstPoint ? 0 : 1; i < p.Length; i++) dst.Add(p[i]);
    }

    /// <summary>
    /// Изолиния по цепочке точек, если все они лежат на краю v = 0 или v = 1 процедурной
    /// поверхности одной из граней (не дальше tol от линии); иначе null.
    /// </summary>
    public static SectionIsoCurve? TryBuild(ISurface3 a, ISurface3 b, IReadOnlyList<Vector3d> pts, double tol = 0.05)
    {
        if (pts.Count < 3) return null;
        double tol0 = tol;
        foreach (var s in new[] { a, b })
        {
            if (s is not SectionSurface ss || !ss.IsProcedural) continue;
            var other = ReferenceEquals(s, a) ? b : a;
            bool tg = Tangent(ss, other, pts);
            tol = tg ? Math.Max(tol0, TangentTol(ss)) : tol0;
            var uv = pts.Select(ss.ProjectToUV).ToList();
            foreach (double v0 in new[] { 0.0, 1.0 })
            {
                bool ok = true;
                var us = new double[pts.Count];
                double per = ss.UMax - ss.UMin;
                for (int i = 0; i < pts.Count && ok; i++)
                {
                    double u = uv[i].X;
                    // Кольцо: параметр разворачивается без скачка на период.
                    if (ss.Closed && i > 0)
                        while (Math.Abs(u - us[i - 1]) > per / 2) u += u < us[i - 1] ? per : -per;
                    us[i] = u;
                    if ((ss.PointAt(new Vector2d(u, v0)) - pts[i]).Length > tol)
                    {
                        ok = false;
                        Debug?.Invoke($"v={v0}: точка {i} в {(ss.PointAt(new Vector2d(u, v0)) - pts[i]).Length:0.###} от линии (допуск {tol:0.###}, касание {tg}), uv ({uv[i].X:0.###},{uv[i].Y:0.###})");
                    }
                }
                if (!ok) continue;
                // Параметр вдоль цепочки монотонен — иначе это не одна изолиния. Возврат в
                // пределах допуска (u — длина дуги, мм) — дрожание вершин шва на микроны, не
                // разворот: у шва, вдоль которого резец задел грань хордами, точки гуляют.
                int sign = Math.Sign(us[^1] - us[0]);
                if (sign == 0) continue;
                double run = us[0];
                for (int i = 1; i < us.Length && ok; i++)
                {
                    if (sign * (us[i] - run) > 0) run = us[i];
                    else if (sign * (run - us[i]) > tol) { ok = false; Debug?.Invoke($"v={v0}: возврат по u в точке {i}: {us[i]:0.###} после {run:0.###}"); }
                }
                if (!ok) continue;
                return new SectionIsoCurve(ss, v0, us[0], us[^1], pts[0], pts[^1], pts);
            }
        }
        return null;
    }

    /// <summary>
    /// ДОПУСК ШВА КАСАНИЯ. Резец режет хордами между сечениями; на касании ошибка хорды δ
    /// даёт поперечный сдвиг линии среза √(2rδ) — у R5 до полумиллиметра: шов выходит
    /// зубцами, и прежде он не признавался изолинией и рассыпался на сотни отрезков
    /// (снимок пользователя 22.09). Сам шов касания известен точно — изолиния; допуск —
    /// треть ширины сечения, но не меньше миллиметра.
    /// </summary>
    /// <summary>ДИАГНОСТИКА: причины отказа распознавания.</summary>
    public static Action<string>? Debug;

    private static double TangentTol(SectionSurface ss)
    {
        var sec = ss.Sections[ss.Sections.Count / 2];
        double width = (sec.Point(1) - sec.Point(0)).Length;
        return Math.Max(1.0, width / 3);
    }

    /// <summary>Поверхности касаются вдоль шва (нормали параллельны в пробных точках).</summary>
    private static bool Tangent(SectionSurface ss, ISurface3 other, IReadOnlyList<Vector3d> pts)
    {
        int n = pts.Count;
        foreach (int i in new[] { n / 4, n / 2, 3 * n / 4 })
        {
            var q = pts[Math.Clamp(i, 0, n - 1)];
            Vector3d na = ss.NormalAt(ss.ProjectToUV(q));
            var (_, nb) = Curves.ExactSurfaces.Project(other, q);
            // 15°: на зубце точка отходит от линии касания до 0.5 мм, у R5 это ~6° между нормалями.
            if (Vector3d.Cross(na.Normalized(), nb.Normalized()).Length > Math.Sin(15 * Math.PI / 180)) return false;
        }
        return true;
    }
}
