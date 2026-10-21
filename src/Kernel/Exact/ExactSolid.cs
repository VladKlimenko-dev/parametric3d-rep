using System.Numerics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// ТЕЛО В ТОЧНОМ ПРЕДСТАВЛЕНИИ: многоугольники на плоскостях общего реестра.
///
/// Реестр общий у всех тел, участвующих в операциях подряд: номера плоскостей
/// сравнимы только внутри одного реестра. Тело из чужого реестра сначала
/// переводится в свой (Import) — это точная операция, плоскости переносятся
/// по коэффициентам.
/// </summary>
public sealed class ExactSolid
{
    public readonly PlaneRegistry Registry;
    /// <summary>
    /// Многоугольники тела — НЕИЗМЕНЯЕМЫЕ (поставка 2, остаток: внутреннее состояние точного тела). Хранится копия
    /// в обёртке только для чтения над массивом: ни приведение типа, ни правка исходного списка создателем после
    /// передачи её не меняют. Прежде это был открытый изменяемый List — опубликованное тело защищала только проверка
    /// отпечатком (VerifyPublishedBodies). Реестр плоскостей общий для цепочки операций и только пополняется:
    /// смысл уже выданных номеров плоскостей и вершин не меняется.
    /// </summary>
    public readonly IReadOnlyList<ExactPolygon> Polygons;

    public ExactSolid(PlaneRegistry registry, IEnumerable<ExactPolygon> polygons)
    {
        Registry = registry; Polygons = Array.AsReadOnly(polygons.ToArray());
    }

    /// <summary>То же тело, переписанное в другой реестр. Точно: плоскости переносятся по коэффициентам.</summary>
    public ExactSolid Import(PlaneRegistry target)
    {
        if (ReferenceEquals(target, Registry)) return this;
        foreach (var h in Registry.FaceHints) target.AddFaceHint(h.A, h.B, h.C, h.D, h.N, h.W);

        var map = new Dictionary<int, int>();

        OPlane Map(OPlane p)
        {
            if (!map.TryGetValue(p.Id, out int id))
            {
                var (a, b, c, d) = Registry.Coefficients(p.Id);
                OPlane q = target.Plane(a, b, c, d);
                id = q.Id;
                map[p.Id] = id;
                // Запись канонична в обоих реестрах, сторона совпадает.
            }
            return new OPlane(id, p.Side);
        }

        var polys = new List<ExactPolygon>(Polygons.Count);
        foreach (var p in Polygons)
        {
            var b = new OPlane[p.Bounds.Length];
            for (int i = 0; i < b.Length; i++) b[i] = Map(p.Bounds[i]);
            polys.Add(new ExactPolygon(Map(p.Support), b, p.Tag));
        }

        return new ExactSolid(target, polys);
    }

    // ======================================================== измерения
    /// <summary>Объём в кубических миллиметрах — точной дробью.</summary>
    // 21.10 (скорость): объём неизменяемого тела — один раз (его спрашивают отпечаток, объём детали, граница ошибки, проверка
    // знака: тор ∪ брусок с касанием — 276 мс на каждый вопрос). Мутация P3D_EXACTVOLMEMO=0.
    static readonly bool VolMemoOff = Environment.GetEnvironmentVariable("P3D_EXACTVOLMEMO") == "0";
    private BigRational? _volume;
    public static long VolumeMemoHits;

    public BigRational VolumeMm3()
    {
        if (!VolMemoOff && _volume is { } known) { Interlocked.Increment(ref VolumeMemoHits); return known; }
        var v = VolumeMm3Core();
        if (!VolMemoOff) _volume = v;
        return v;
    }

    private BigRational VolumeMm3Core()
    {
        BigRational vol = BigRational.Zero;

        foreach (var p in Polygons)
        {
            var P = Points(p);
            for (int i = 1; i + 1 < P.Length; i++)
                vol += Triple(P[0], P[i], P[i + 1]);
        }

        BigInteger unitsPerMm = new(ExactGrid.UnitsPerMmInt);
        return vol / new BigRational(6 * unitsPerMm * unitsPerMm * unitsPerMm, 1);
    }

    /// <summary>
    /// Векторная площадь. У замкнутой оболочки ровно ноль — необходимое
    /// условие замкнутости, но не достаточное; строгая проверка — Watertight.
    /// </summary>
    public (BigRational X, BigRational Y, BigRational Z) VectorArea()
    {
        BigRational x = BigRational.Zero, y = BigRational.Zero, z = BigRational.Zero;

        foreach (var p in Polygons)
        {
            var P = Points(p);
            for (int i = 0; i < P.Length; i++)
            {
                var u = P[i]; var w = P[(i + 1) % P.Length];
                x += u.Y * w.Z - u.Z * w.Y;
                y += u.Z * w.X - u.X * w.Z;
                z += u.X * w.Y - u.Y * w.X;
            }
        }

        return (x, y, z);
    }

    /// <summary>
    /// СТРОГАЯ ПРОВЕРКА ЗАМКНУТОСТИ. На каждой прямой рёбер покрытие в одну
    /// сторону обязано точно гаситься покрытием в другую, с учётом T-стыков.
    /// Прямая опознаётся координатами Плюккера, а не парой плоскостей:
    /// одна прямая бывает задана разными парами (этап 4).
    ///
    /// Возвращает число непогашенных отрезков; у замкнутого тела — ноль.
    /// </summary>
    public int UncancelledSegments()
    {
        var lines = new Dictionary<(BigInteger, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger),
                                   List<(BigRational A, BigRational B)>>();

        foreach (var p in Polygons)
        {
            var V = p.Vertices(Registry);
            var P = Points(p);
            int n = V.Length;

            for (int i = 0; i < n; i++)
            {
                var key = Registry.Line(p.Support.Id, p.Bounds[i].Id);
                // Вершина i — начало ребра i: ребро идёт от V[i] к V[i+1],
                // ограничивающая Bounds[i] лежит между ними.
                var u = P[i]; var w = P[(i + 1) % n];
                BigRational ta = u.X * key.Item1 + u.Y * key.Item2 + u.Z * key.Item3;
                BigRational tb = w.X * key.Item1 + w.Y * key.Item2 + w.Z * key.Item3;

                if (!lines.TryGetValue(key, out var list)) lines[key] = list = new();
                list.Add((ta, tb));
            }
        }

        int bad = 0;

        foreach (var segs in lines.Values)
        {
            var pts = new SortedSet<BigRational>();
            foreach (var (a, b) in segs) { pts.Add(a); pts.Add(b); }

            BigRational? prev = null;
            foreach (var t in pts)
            {
                if (prev != null)
                {
                    BigRational mid = (prev.Value + t) / new BigRational(2, 1);
                    int cover = 0;
                    foreach (var (a, b) in segs)
                    {
                        bool fwd = a < b;
                        BigRational lo = fwd ? a : b, hi = fwd ? b : a;
                        if (lo < mid && mid < hi) cover += fwd ? 1 : -1;
                    }
                    if (cover != 0) bad++;
                }
                prev = t;
            }
        }

        return bad;
    }

    private readonly record struct RPoint(BigRational X, BigRational Y, BigRational Z);

    private RPoint[] Points(ExactPolygon p)
    {
        var V = p.Vertices(Registry);
        var P = new RPoint[V.Length];
        for (int i = 0; i < V.Length; i++)
        {
            var (X, Y, Z, W) = Registry.Homogeneous(V[i]);
            P[i] = new RPoint(new BigRational(X, W), new BigRational(Y, W), new BigRational(Z, W));
        }
        return P;
    }

    private static BigRational Triple(RPoint a, RPoint b, RPoint c)
        => a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
}
