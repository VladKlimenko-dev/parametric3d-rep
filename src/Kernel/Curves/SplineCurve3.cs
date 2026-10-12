using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// СПЛАЙН (NURBS): кривая по опорным точкам с весами и узловым вектором.
///
/// Без него нельзя ни задать произвольный эскиз, ни прочитать STEP: там почти вся геометрия —
/// сплайны. Реализация — стандартная: точка считается алгоритмом де Бура, касательная —
/// разностью по параметру, гранение — по прогибу (пополам, пока хорда отходит дальше допуска).
///
/// Веса позволяют точно представить дугу окружности, поэтому «рациональная» часть NURBS нужна
/// не для красоты: без неё импортированная окружность перестаёт быть окружностью.
/// </summary>
public sealed class SplineCurve3 : ICurve3
{
    private readonly Vector3d[] _cp;        // опорные точки
    private readonly double[] _w;           // веса
    private readonly double[] _knots;       // узловой вектор
    private readonly int _degree;
    private double _length = -1;

    public SplineCurve3(IReadOnlyList<Vector3d> controlPoints, int degree = 3,
                        IReadOnlyList<double>? weights = null, IReadOnlyList<double>? knots = null)
    {
        if (controlPoints.Count < 2) throw new ArgumentException("at least two control points are needed");
        _degree = Math.Clamp(degree, 1, controlPoints.Count - 1);
        _cp = controlPoints.ToArray();
        _w = weights?.ToArray() ?? Enumerable.Repeat(1.0, _cp.Length).ToArray();
        if (_w.Length != _cp.Length) throw new ArgumentException("one weight per control point");
        _knots = knots?.ToArray() ?? Clamped(_cp.Length, _degree);
        if (_knots.Length != _cp.Length + _degree + 1) throw new ArgumentException("wrong knot count");
    }

    /// <summary>
    /// ВСТАВКА УЗЛА (по Бёму). Форма кривой не меняется — добавляется опорная точка, и кривая
    /// становится делимой в этом месте. Без вставки нет ни обрезки, ни сшивки: обрезать сплайн
    /// «по параметру» можно только подняв кратность узла до степени.
    ///
    /// Узел задаётся в тех же пределах, что и параметр кривой (0…1 у зажатого вектора).
    /// </summary>
    public SplineCurve3 InsertKnot(double t)
    {
        double lo = _knots[_degree], hi = _knots[^(_degree + 1)];
        if (t <= lo + 1e-12 || t >= hi - 1e-12)
            throw new ArgumentOutOfRangeException(nameof(t), "узел вне рабочих пределов кривой");

        // Куда попадает новый узел.
        int k = _degree;
        while (k < _knots.Length - _degree - 2 && _knots[k + 1] <= t) k++;

        int n = _cp.Length;
        var cp = new Vector3d[n + 1];
        var w = new double[n + 1];

        // Точки считаются в однородных координатах: иначе веса исказятся.
        for (int i = 0; i <= n; i++)
        {
            double a;
            if (i <= k - _degree) a = 1;
            else if (i > k) a = 0;
            else
            {
                double den = _knots[i + _degree] - _knots[i];
                a = den <= 0 ? 0 : (t - _knots[i]) / den;
            }

            Vector3d pPrev = i == 0 ? Vector3d.Zero : _cp[i - 1] * _w[i - 1];
            double wPrev = i == 0 ? 0 : _w[i - 1];
            Vector3d pCur = i >= n ? Vector3d.Zero : _cp[i] * _w[i];
            double wCur = i >= n ? 0 : _w[i];

            Vector3d hp = pPrev * (1 - a) + pCur * a;
            double hw = wPrev * (1 - a) + wCur * a;

            w[i] = hw;
            cp[i] = Math.Abs(hw) < 1e-15 ? Vector3d.Zero : hp / hw;
        }

        var knots = new List<double>(_knots);
        knots.Insert(k + 1, t);

        return new SplineCurve3(cp, _degree, w, knots);
    }

    /// <summary>
    /// Кратность узла в его текущем виде — сколько раз он уже вставлен.
    /// </summary>
    public int Multiplicity(double t)
    {
        int m = 0;
        foreach (double k in _knots) if (Math.Abs(k - t) < 1e-12) m++;
        return m;
    }

    /// <summary>
    /// РАЗБИЕНИЕ КРИВОЙ в точке параметра: узел поднимается до кратности степени, после чего
    /// опорные точки делятся на два набора. Обе части — самостоятельные сплайны, вместе дающие
    /// прежнюю кривую.
    /// </summary>
    public (SplineCurve3 A, SplineCurve3 B) SplitAt(double t)
    {
        SplineCurve3 c = this;
        while (c.Multiplicity(t) < c._degree) c = c.InsertKnot(t);

        // Место разреза: первый узел, равный t.
        int at = 0;
        while (at < c._knots.Length && c._knots[at] < t - 1e-12) at++;

        int d = c._degree;
        int left = at;                       // столько опорных точек уходит в первую часть
        var cpA = c._cp.Take(left).ToArray();
        var wA = c._w.Take(left).ToArray();
        var cpB = c._cp.Skip(left - 1).ToArray();
        var wB = c._w.Skip(left - 1).ToArray();

        var kA = c._knots.Take(left).ToList();
        kA.AddRange(Enumerable.Repeat(t, d + 1));
        var kB = new List<double>(Enumerable.Repeat(t, d + 1));
        kB.AddRange(c._knots.Skip(left + d));

        return (new SplineCurve3(cpA, d, wA, Normalize(kA)),
                new SplineCurve3(cpB, d, wB, Normalize(kB)));
    }

    /// <summary>Узловой вектор приводится к пределам 0…1: параметр кривой у нас всегда такой.</summary>
    private static double[] Normalize(IReadOnlyList<double> knots)
    {
        double lo = knots[0], hi = knots[^1];
        double span = hi - lo;
        if (span <= 0) return knots.ToArray();
        return knots.Select(x => (x - lo) / span).ToArray();
    }

    /// <summary>
    /// ПОВЫШЕНИЕ СТЕПЕНИ. Нужно для сшивки: две кривые соединяются в одну, только если у них
    /// одинаковая степень, а поднять степень можно всегда, понизить — нет.
    ///
    /// Форма кривой не меняется. Пока поддержан случай БЕЗ ВНУТРЕННИХ УЗЛОВ (кривая Безье):
    /// для него формула точная и короткая. Кривая с внутренними узлами отвергается прямым
    /// сообщением — разложить её на куски Безье, поднять каждый и сшить обратно можно, но это
    /// отдельная работа, и делать её наполовину хуже, чем честно отказать.
    /// </summary>
    public SplineCurve3 ElevateDegree()
    {
        int n = _cp.Length - 1;
        if (_knots.Length != 2 * (n + 1) || _knots.Skip(n + 1).Any(k => Math.Abs(k - 1) > 1e-12)
            || _knots.Take(n + 1).Any(k => Math.Abs(k) > 1e-12))
            throw new InvalidOperationException(
                "повышение степени пока только для кривой без внутренних узлов");

        // Опорные точки новой кривой: P'i = (i/(n+1))·P(i−1) + (1 − i/(n+1))·Pi, в однородных
        // координатах — иначе веса исказятся.
        int m = n + 1;
        var cp = new Vector3d[m + 1];
        var w = new double[m + 1];

        for (int i = 0; i <= m; i++)
        {
            double a = (double)i / m;

            Vector3d hPrev = i == 0 ? Vector3d.Zero : _cp[i - 1] * _w[i - 1];
            double wPrev = i == 0 ? 0 : _w[i - 1];
            Vector3d hCur = i > n ? Vector3d.Zero : _cp[i] * _w[i];
            double wCur = i > n ? 0 : _w[i];

            Vector3d hp = hPrev * a + hCur * (1 - a);
            double hw = wPrev * a + wCur * (1 - a);

            w[i] = hw;
            cp[i] = Math.Abs(hw) < 1e-15 ? Vector3d.Zero : hp / hw;
        }

        var knots = new List<double>();
        knots.AddRange(Enumerable.Repeat(0.0, m + 1));
        knots.AddRange(Enumerable.Repeat(1.0, m + 1));

        return new SplineCurve3(cp, m, w, knots);
    }

    /// <summary>
    /// РАЗЛОЖЕНИЕ НА КУСКИ БЕЗЬЕ. Каждый внутренний узел поднимается до кратности степени, и
    /// кривая распадается на куски без внутренних узлов. На них работают простые точные
    /// действия — повышение степени, пересечение, обрезка, — которые на общем сплайне
    /// пришлось бы делать приближённо.
    ///
    /// Возвращаются куски и границы их участков в параметре исходной кривой: без границ нельзя
    /// было бы сказать, какому месту кривой отвечает кусок.
    /// </summary>
    public List<(SplineCurve3 Piece, double From, double To)> ToBezierSegments()
    {
        var breaks = new List<double>();
        for (int i = _degree + 1; i < _knots.Length - _degree - 1; i++)
        {
            double k = _knots[i];
            if (k <= _knots[_degree] + 1e-12 || k >= _knots[^(_degree + 1)] - 1e-12) continue;
            if (breaks.Count == 0 || Math.Abs(breaks[^1] - k) > 1e-12) breaks.Add(k);
        }

        var res = new List<(SplineCurve3, double, double)>();
        if (breaks.Count == 0) { res.Add((this, 0, 1)); return res; }

        SplineCurve3 rest = this;
        double done = 0;
        foreach (double k in breaks)
        {
            // Узел пересчитывается в параметр оставшейся части: она уже приведена к 0…1.
            double local = (k - done) / (1 - done);
            if (local <= 1e-9 || local >= 1 - 1e-9) continue;

            var (a, b) = rest.SplitAt(local);
            res.Add((a, done, k));
            rest = b;
            done = k;
        }
        res.Add((rest, done, 1));
        return res;
    }

    /// <summary>
    /// Повышение степени для ЛЮБОЙ кривой: она раскладывается на куски Безье, и степень
    /// поднимается у каждого. Возвращаются куски с их участками — склеить их в один сплайн
    /// нельзя без смены параметризации, а менять её молча значит подсунуть другую кривую.
    /// </summary>
    public List<(SplineCurve3 Piece, double From, double To)> ElevateDegreeByPieces()
    {
        var res = new List<(SplineCurve3, double, double)>();
        foreach (var (piece, from, to) in ToBezierSegments())
            res.Add((piece.ElevateDegree(), from, to));
        return res;
    }

    /// <summary>
    /// СШИВКА ДВУХ КРИВЫХ В ОДНУ. Обратная разложению на куски: после обрезки, повышения
    /// степени или чтения по частям куски нужно собрать назад, иначе дальше с ними работают
    /// как с набором, а не как с кривой.
    ///
    /// Требуется, чтобы конец первой совпадал с началом второй; степени приводятся к общей
    /// повышением (понизить степень без потери формы нельзя). Параметр делится ПО ДЛИНАМ
    /// кусков: иначе кривая пробегалась бы неравномерно и точка при том же параметре уехала бы.
    ///
    /// В месте стыка узел получает кратность, равную степени: кривая там может иметь излом,
    /// и сглаживать его самовольно нельзя.
    ///
    /// ВАЖНО: у сплайна узлы влияют на ФОРМУ, а не только на пробег по кривой. Поэтому сшивка
    /// точна лишь тогда, когда деление параметра совпадает с тем, по которому кривую резали:
    /// для этого есть довод splitAt. Без него берётся деление по длинам кусков — у кусков Безье
    /// это даёт ту же форму, а у кусков с внутренними узлами форма уходит (измерено 0.171 мм на
    /// кривой из пяти точек степени 3, разрезанной в 0.4).
    /// </summary>
    public static SplineCurve3 Join(SplineCurve3 a, SplineCurve3 b, double tolerance = 0,
                                    double? splitAt = null)
    {
        double tol = tolerance > 0 ? tolerance : Core.Tol.Weld * 100;
        if ((a.PointAt(1) - b.PointAt(0)).Length > tol)
            throw new InvalidOperationException(
                $"сшивка: конец первой кривой и начало второй разошлись на {(a.PointAt(1) - b.PointAt(0)).Length:0.######} мм");

        // Степени приводятся к общей: поднимаем ту, что ниже.
        while (a._degree < b._degree) a = a.ElevateDegree();
        while (b._degree < a._degree) b = b.ElevateDegree();
        int d = a._degree;

        // ДОЛЯ ПАРАМЕТРА. У сплайна узлы влияют на ФОРМУ, а не только на пробег: сшив куски с
        // иным делением, чем было при разрезе, получишь другую кривую (измерено: расхождение
        // 0.171 мм). Поэтому, если место разреза известно, его нужно передать — тогда сшивка
        // точна. Без него берётся деление по длинам: разумная догадка, но лишь догадка.
        double la = a.Length, lb = b.Length;
        double split = splitAt ?? (la + lb <= 0 ? 0.5 : la / (la + lb));
        split = Math.Clamp(split, 1e-9, 1 - 1e-9);

        // Опорные точки: все от первой, затем все от второй без её первой (она же конец первой).
        var cp = new List<Vector3d>(a._cp);
        var w = new List<double>(a._w);
        for (int i = 1; i < b._cp.Length; i++) { cp.Add(b._cp[i]); w.Add(b._w[i]); }

        // Узлы: первая часть сжимается в [0, split], вторая — в [split, 1]; в стыке кратность d.
        var knots = new List<double>();
        for (int i = 0; i <= d; i++) knots.Add(0);
        for (int i = d + 1; i < a._knots.Length - d - 1; i++) knots.Add(a._knots[i] * split);
        for (int i = 0; i < d; i++) knots.Add(split);
        for (int i = d + 1; i < b._knots.Length - d - 1; i++) knots.Add(split + b._knots[i] * (1 - split));
        for (int i = 0; i <= d; i++) knots.Add(1);

        if (knots.Count != cp.Count + d + 1)
            throw new InvalidOperationException(
                $"сшивка: узлов {knots.Count} при {cp.Count} точках и степени {d} — ожидалось {cp.Count + d + 1}");

        return new SplineCurve3(cp, d, w, knots);
    }

    /// <summary>Зажатый узловой вектор: кривая проходит через первую и последнюю точки.</summary>
    private static double[] Clamped(int n, int p)
    {
        var k = new double[n + p + 1];
        int inner = n - p - 1;
        for (int i = 0; i <= p; i++) k[i] = 0;
        for (int i = 1; i <= inner; i++) k[p + i] = (double)i / (inner + 1);
        for (int i = 0; i <= p; i++) k[n + i] = 1;
        return k;
    }

    public double T0 => 0;
    public double T1 => 1;
    public bool IsClosed => (_cp[0] - _cp[^1]).LengthSquared < Tol.Weld * Tol.Weld;

    public IReadOnlyList<Vector3d> ControlPoints => _cp;
    public IReadOnlyList<double> Weights => _w;
    public int Degree => _degree;

    /// <summary>Узловой вектор — нужен при записи в обменные форматы.</summary>
    public IReadOnlyList<double> Knots => _knots;

    public Vector3d PointAt(double t)
    {
        double u = Math.Clamp(t, 0, 1) * (_knots[^1] - _knots[0]) + _knots[0];
        int span = FindSpan(u);

        // Де Бур в однородных координатах: так учитываются веса.
        var d = new Vector4d[_degree + 1];
        for (int j = 0; j <= _degree; j++)
        {
            int i = span - _degree + j;
            d[j] = new Vector4d(_cp[i].X * _w[i], _cp[i].Y * _w[i], _cp[i].Z * _w[i], _w[i]);
        }

        for (int r = 1; r <= _degree; r++)
            for (int j = _degree; j >= r; j--)
            {
                int i = span - _degree + j;
                double denom = _knots[i + _degree - r + 1] - _knots[i];
                double a = denom <= 0 ? 0 : (u - _knots[i]) / denom;
                d[j] = d[j - 1] * (1 - a) + d[j] * a;
            }

        Vector4d h = d[_degree];
        double w = Math.Abs(h.W) < 1e-18 ? 1 : h.W;
        return new Vector3d(h.X / w, h.Y / w, h.Z / w);
    }

    public Vector3d TangentAt(double t)
    {
        const double h = 1e-6;
        double a = Math.Clamp(t - h, 0, 1), b = Math.Clamp(t + h, 0, 1);
        Vector3d v = PointAt(b) - PointAt(a);
        return v.LengthSquared < 1e-30 ? Vector3d.UnitX : v.Normalized();
    }

    public double Length
    {
        get
        {
            if (_length >= 0) return _length;
            var pts = new List<Vector3d>();
            Tessellate(pts, true);
            double l = 0;
            for (int i = 1; i < pts.Count; i++) l += (pts[i] - pts[i - 1]).Length;
            return _length = l;
        }
    }

    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty;
            var pts = new List<Vector3d>();
            Tessellate(pts, true);
            foreach (var p in pts) b.Add(p);
            return b;
        }
    }

    public ICurve3 Reversed()
    {
        // РАЗВОРОТ ПИШЕТСЯ ЯВНО, БЕЗ Reverse(). У списка этот метод разворачивает на месте и
        // возвращает void, у массива — разворачивает перечисление: в одной сборке код
        // собирался, в другой давал ошибку «нельзя применить точку к void». Явный цикл
        // одинаков везде.
        var cp = new Vector3d[_cp.Length];
        var w = new double[_w.Length];
        for (int i = 0; i < cp.Length; i++) cp[i] = _cp[cp.Length - 1 - i];
        for (int i = 0; i < w.Length; i++) w[i] = _w[w.Length - 1 - i];

        double lo = _knots[0], hi = _knots[^1];
        var k = new double[_knots.Length];
        for (int i = 0; i < k.Length; i++) k[i] = lo + hi - _knots[k.Length - 1 - i];

        return new SplineCurve3(cp, _degree, w, k);
    }

    /// <summary>Гранение по прогибу: звено делится, пока середина отходит дальше допуска.</summary>
    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        if (includeFirstPoint) dst.Add(PointAt(0));
        Split(dst, 0, 1, PointAt(0), PointAt(1), 0);
    }

    private void Split(List<Vector3d> dst, double t0, double t1, Vector3d p0, Vector3d p1, int depth)
    {
        double tm = (t0 + t1) / 2;
        Vector3d pm = PointAt(tm);

        // Прогиб проверяется в трёх точках звена, а не только в середине: у несимметричного
        // участка середина может лежать на хорде, а четверти — отходить (кривая гранилась
        // втрое грубее допуска).
        bool flat = true;
        foreach (double f in new[] { 0.25, 0.5, 0.75 })
        {
            Vector3d q = PointAt(t0 + (t1 - t0) * f);
            if ((q - (p0 + (p1 - p0) * f)).Length > Tol.Chord) { flat = false; break; }
        }
        if (flat && (p1 - p0).Length <= 50 * Tol.Chord || depth > 18)
        {
            dst.Add(p1);
            return;
        }
        Split(dst, t0, tm, p0, pm, depth + 1);
        Split(dst, tm, t1, pm, p1, depth + 1);
    }

    private int FindSpan(double u)
    {
        int n = _cp.Length - 1;
        if (u >= _knots[n + 1]) return n;
        if (u <= _knots[_degree]) return _degree;
        int lo = _degree, hi = n + 1, mid = (lo + hi) / 2;
        while (u < _knots[mid] || u >= _knots[mid + 1])
        {
            if (u < _knots[mid]) hi = mid; else lo = mid;
            mid = (lo + hi) / 2;
        }
        return mid;
    }
}
