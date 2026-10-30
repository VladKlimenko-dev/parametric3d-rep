using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// ПОВЕРХНОСТЬ ПО СЕТКЕ ОПОРНЫХ ТОЧЕК (NURBS). Без неё не прочитать большинство чужих моделей:
/// в обменных файлах поверхности двойной кривизны записаны именно так. Вращение и протяжка
/// сплайна, которые у нас были, покрывают лишь частные случаи.
///
/// Точка считается по алгоритму де Бура в однородных координатах — как у нашей сплайновой
/// кривой, только дважды: сперва по одному направлению, затем по другому. Веса поддержаны, и
/// поэтому точно представимы цилиндр, сфера и тор, а не только свободные формы.
/// </summary>
public sealed class NurbsSurface : ISurface3
{
    private readonly Vector3d[,] _cp;        // опорные точки
    private readonly double[,] _w;           // веса
    private readonly double[] _ku, _kv;      // узловые векторы
    private readonly int _du, _dv;           // степени

    /// <summary>Мутация P3D_NURBSDERIVS=0: производные разностями (прежнее; у края параметров — односторонние, ошибка O(h)).</summary>
    public static readonly bool DerivsOff = Environment.GetEnvironmentVariable("P3D_NURBSDERIVS") == "0";
    public int DegreeU => _du;
    public int DegreeV => _dv;
    public int CountU => _cp.GetLength(0);
    public int CountV => _cp.GetLength(1);
    public IReadOnlyList<double> KnotsU => _ku;
    public IReadOnlyList<double> KnotsV => _kv;

    public Vector3d ControlPoint(int i, int j) => _cp[i, j];
    public double Weight(int i, int j) => _w[i, j];

    public NurbsSurface(Vector3d[,] controlPoints, int degreeU = 3, int degreeV = 3,
                        double[,]? weights = null, double[]? knotsU = null, double[]? knotsV = null)
    {
        int nu = controlPoints.GetLength(0), nv = controlPoints.GetLength(1);
        if (nu < 2 || nv < 2) throw new ArgumentException("сетка опорных точек меньше двух на два");

        _cp = controlPoints;
        _du = Math.Clamp(degreeU, 1, nu - 1);
        _dv = Math.Clamp(degreeV, 1, nv - 1);

        _w = weights ?? Ones(nu, nv);
        if (_w.GetLength(0) != nu || _w.GetLength(1) != nv)
            throw new ArgumentException("веса не совпадают по размеру с опорными точками");

        _ku = knotsU ?? Clamped(nu, _du);
        _kv = knotsV ?? Clamped(nv, _dv);
        if (_ku.Length != nu + _du + 1 || _kv.Length != nv + _dv + 1)
            throw new ArgumentException("узловой вектор не той длины");
    }

    private static double[,] Ones(int nu, int nv)
    {
        var w = new double[nu, nv];
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++) w[i, j] = 1;
        return w;
    }

    /// <summary>Узловой вектор с закреплёнными концами: поверхность проходит через углы сетки.</summary>
    private static double[] Clamped(int count, int degree)
    {
        var k = new double[count + degree + 1];
        int inner = count - degree - 1;
        for (int i = 0; i <= degree; i++) k[i] = 0;
        for (int i = 1; i <= inner; i++) k[degree + i] = (double)i / (inner + 1);
        for (int i = 0; i <= degree; i++) k[count + i] = 1;
        return k;
    }

    // ------------------------------------------------------------------ точка

    public Vector3d PointAt(Vector2d uv)
    {
        double u = Math.Clamp(uv.X, 0, 1), v = Math.Clamp(uv.Y, 0, 1);
        if (!FastEvalOff)
        {
            // KRN-07 (07.8): базис на промежутке (Пигль — Тиллер A2.2) без выделений памяти — та же точка, что у де Бура
            int nu0 = CountU, nv0 = CountV;
            int su = Span(_ku, nu0, _du, u), sv = Span(_kv, nv0, _dv, v);
            Span<double> Nu = stackalloc double[_du + 1]; Span<double> Nv = stackalloc double[_dv + 1];
            Basis(_ku, su, u, _du, Nu); Basis(_kv, sv, v, _dv, Nv);
            Vector3d A = Vector3d.Zero; double w = 0;
            for (int a = 0; a <= _du; a++)
            {
                int i = su - _du + a; if (Nu[a] == 0) continue;
                for (int b = 0; b <= _dv; b++)
                {
                    int j = sv - _dv + b; double n = Nu[a] * Nv[b] * _w[i, j];
                    A += _cp[i, j] * n; w += n;
                }
            }
            return A / w;
        }

        // Сперва по второму направлению: каждая строка сетки сворачивается в одну точку,
        // затем полученная строка — в искомую. Счёт идёт в однородных координатах, поэтому
        // веса работают.
        int nu = CountU;
        var row = new Vector3d[nu];
        var rw = new double[nu];

        for (int i = 0; i < nu; i++)
        {
            var (p, w) = DeBoor(j => (_cp[i, j], _w[i, j]), CountV, _dv, _kv, v);
            row[i] = p; rw[i] = w;
        }

        // Точка после первой свёртки УЖЕ умножена на вес, поэтому во вторую она передаётся
        // поделённой: иначе веса возводятся в квадрат, и точный цилиндр выходит радиусом 23.9
        // вместо 25.
        var (res, rwv) = DeBoor(i => (Math.Abs(rw[i]) < 1e-15 ? row[i] : row[i] / rw[i], rw[i]),
                                nu, _du, _ku, u);
        return Math.Abs(rwv) < 1e-15 ? res : res / rwv;
    }

    /// <summary>
    /// Де Бур в однородных координатах: точки берутся умноженными на вес, и в конце делятся на
    /// накопленный вес. Возвращает пару «точка·вес, вес», чтобы можно было свернуть дважды.
    /// </summary>
    private static (Vector3d P, double W) DeBoor(Func<int, (Vector3d P, double W)> get,
                                                 int count, int degree, double[] knots, double t)
    {
        int span = degree;
        while (span < count - 1 && knots[span + 1] <= t) span++;

        var d = new Vector3d[degree + 1];
        var dw = new double[degree + 1];
        for (int j = 0; j <= degree; j++)
        {
            var (p, w) = get(span - degree + j);
            d[j] = p * w; dw[j] = w;
        }

        for (int r = 1; r <= degree; r++)
            for (int j = degree; j >= r; j--)
            {
                int i = span - degree + j;
                double den = knots[i + degree - r + 1] - knots[i];
                double a = den <= 0 ? 0 : (t - knots[i]) / den;
                d[j] = d[j - 1] * (1 - a) + d[j] * a;
                dw[j] = dw[j - 1] * (1 - a) + dw[j] * a;
            }

        return (d[degree], dw[degree]);
    }

    // ------------------------------------------------------------------ узлы

    /// <summary>
    /// ВСТАВКА УЗЛА ПО НАПРАВЛЕНИЮ. Форма поверхности не меняется — добавляется ряд опорных
    /// точек. Нужна для обрезки: вырезать кусок поверхности «по параметру» можно, только подняв
    /// кратность узла до степени, и без этого обрезанные грани чужих моделей не разложить.
    ///
    /// По каждому направлению это та же вставка, что у кривой, применённая к рядам сетки:
    /// поверхность — это набор кривых, связанных вторым параметром.
    /// </summary>
    public NurbsSurface InsertKnotU(double t) => Insert(t, alongU: true);

    /// <summary>Вставка узла по второму направлению.</summary>
    public NurbsSurface InsertKnotV(double t) => Insert(t, alongU: false);

    private NurbsSurface Insert(double t, bool alongU)
    {
        double[] knots = alongU ? _ku : _kv;
        int degree = alongU ? _du : _dv;
        int count = alongU ? CountU : CountV;

        double lo = knots[degree], hi = knots[^(degree + 1)];
        if (t <= lo + 1e-12 || t >= hi - 1e-12)
            throw new ArgumentOutOfRangeException(nameof(t), "узел вне рабочих пределов поверхности");

        int k = degree;
        while (k < knots.Length - degree - 2 && knots[k + 1] <= t) k++;

        int other = alongU ? CountV : CountU;
        var cp = alongU ? new Vector3d[count + 1, other] : new Vector3d[other, count + 1];
        var w = alongU ? new double[count + 1, other] : new double[other, count + 1];

        for (int o = 0; o < other; o++)
            for (int i = 0; i <= count; i++)
            {
                double a;
                if (i <= k - degree) a = 1;
                else if (i > k) a = 0;
                else
                {
                    double den = knots[i + degree] - knots[i];
                    a = den <= 0 ? 0 : (t - knots[i]) / den;
                }

                // Счёт в однородных координатах: иначе веса исказятся, и точный цилиндр
                // перестанет быть точным.
                (Vector3d P, double W) Get(int idx)
                {
                    if (idx < 0 || idx >= count) return (Vector3d.Zero, 0);
                    return alongU ? (_cp[idx, o] * _w[idx, o], _w[idx, o])
                                  : (_cp[o, idx] * _w[o, idx], _w[o, idx]);
                }

                var (pPrev, wPrev) = Get(i - 1);
                var (pCur, wCur) = Get(i);

                Vector3d hp = pPrev * (1 - a) + pCur * a;
                double hw = wPrev * (1 - a) + wCur * a;
                Vector3d res = Math.Abs(hw) < 1e-15 ? Vector3d.Zero : hp / hw;

                if (alongU) { cp[i, o] = res; w[i, o] = hw; }
                else { cp[o, i] = res; w[o, i] = hw; }
            }

        var nk = new List<double>(knots);
        nk.Insert(k + 1, t);

        return alongU
            ? new NurbsSurface(cp, _du, _dv, w, nk.ToArray(), _kv)
            : new NurbsSurface(cp, _du, _dv, w, _ku, nk.ToArray());
    }

    /// <summary>Кратность узла по направлению — сколько раз он уже вставлен.</summary>
    public int MultiplicityU(double t) => _ku.Count(k => Math.Abs(k - t) < 1e-12);
    public int MultiplicityV(double t) => _kv.Count(k => Math.Abs(k - t) < 1e-12);

    // ------------------------------------------------------------------ нормаль и проекция

    public Vector3d NormalAt(Vector2d uv)
    {
        if (!DerivsOff)
        {
            Derivs(uv, out _, out var dsu, out var dsv);
            var nn = Vector3d.Cross(dsu, dsv);
            if (nn.LengthSquared > 1e-24) return nn.Normalized();
        }
        // Производные берутся разностями: аналитические для рациональной поверхности громоздки,
        // а шаг подбирается так, чтобы не терять точность у краёв.
        const double h = 1e-5;
        double u = Math.Clamp(uv.X, 0, 1), v = Math.Clamp(uv.Y, 0, 1);

        Vector3d du = PointAt(new Vector2d(Math.Min(1, u + h), v)) - PointAt(new Vector2d(Math.Max(0, u - h), v));
        Vector3d dv = PointAt(new Vector2d(u, Math.Min(1, v + h))) - PointAt(new Vector2d(u, Math.Max(0, v - h)));

        Vector3d n = Vector3d.Cross(du, dv);
        return n.LengthSquared < 1e-24 ? Vector3d.UnitZ : n.Normalized();
    }

    public Vector2d ProjectToUV(Vector3d p)
    {
        // Ближайшая точка ищется в два приёма: грубо по сетке значений, затем спуском по
        // параметрам. Для поверхности общего вида точного решения нет.
        const int grid = 12;
        double bestU = 0, bestV = 0, best = double.MaxValue;
        // KRN-07 (07.8): точки сетки — один раз на поверхность (прежде 169 вычислений на каждую проекцию)
        var g = _grid ??= BuildGrid(grid);
        for (int i = 0; i <= grid; i++)
            for (int j = 0; j <= grid; j++)
            {
                double d = (g[i, j] - p).LengthSquared;
                if (d < best) { best = d; bestU = i / (double)grid; bestV = j / (double)grid; }
            }

        // k140 (скорость): СРАЗУ НЬЮТОН. Гаусс — Ньютон с точными производными (Derivs) от лучшей точки сетки, шаг делится
        // пополам, пока расстояние не уменьшится; сходится за несколько шагов. Прежде — спуск по сетке (до 160 вычислений
        // точки) и Ньютон по разностям: проекция на NURBS была главной ценой проверок и объёма граней лофта. Не сошёлся —
        // прежний путь. Мутация P3D_NURBSPROJNEWTON=0.
        if (!ProjNewtonOff && !DerivsOff)
        {
            double u = bestU, v = bestV, bd = best; bool ok = false;
            for (int it = 0; it < 50; it++)
            {
                DerivsCore(new Vector2d(u, v), false, out var S, out var Su, out var Sv);
                var r = S - p;
                double f1 = Vector3d.Dot(r, Su), f2 = Vector3d.Dot(r, Sv);
                double a11 = Vector3d.Dot(Su, Su), a12 = Vector3d.Dot(Su, Sv), a22 = Vector3d.Dot(Sv, Sv), det = a11 * a22 - a12 * a12;
                if (Math.Abs(det) < 1e-30) break;
                double du = -(f1 * a22 - f2 * a12) / det, dv = -(f2 * a11 - f1 * a12) / det;
                double fr = 1; bool moved = false;
                for (int hh = 0; hh < 30; hh++, fr *= 0.5)
                {
                    double nu = Math.Clamp(u + du * fr, 0, 1), nv = Math.Clamp(v + dv * fr, 0, 1);
                    double nd = (PointAt(new Vector2d(nu, nv)) - p).LengthSquared;
                    if (nd < bd || (nd <= bd && (Math.Abs(nu - u) + Math.Abs(nv - v)) == 0)) { moved = nd < bd; u = nu; v = nv; bd = Math.Min(nd, bd); break; }
                }
                if (!moved) { ok = true; break; }
                if (Math.Abs(du * fr) + Math.Abs(dv * fr) < 1e-14) { ok = true; break; }
            }
            if (ok) { Interlocked.Increment(ref ProjNewtonHits); return new Vector2d(u, v); }
            bestU = u; bestV = v; best = bd;
        }

        double step = 1.0 / grid;
        for (int pass = 0; pass < 40 && step > 1e-6; pass++)
        {
            bool moved = false;
            foreach (var (du, dv) in new[] { (step, 0.0), (-step, 0.0), (0.0, step), (0.0, -step) })
            {
                double u = Math.Clamp(bestU + du, 0, 1), v = Math.Clamp(bestV + dv, 0, 1);
                double d = (PointAt(new Vector2d(u, v)) - p).LengthSquared;
                if (d >= best) continue;
                best = d; bestU = u; bestV = v; moved = true;
            }
            if (!moved) step /= 2;
        }

        // УТОЧНЕНИЕ ПО НЬЮТОНУ. Спуск по сетке даёт около 10⁻³ мм, а кривая пересечения требует
        // 10⁻⁷: без этого ребро между сплайновой поверхностью и другой не строится вовсе.
        // Решается условие «вектор до точки перпендикулярен обеим касательным».
        const double h = 1e-6;
        for (int it = 0; it < 30; it++)
        {
            var uv = new Vector2d(bestU, bestV);
            Vector3d q = PointAt(uv), r = q - p;

            Vector3d su = (PointAt(new Vector2d(Math.Min(1, bestU + h), bestV))
                         - PointAt(new Vector2d(Math.Max(0, bestU - h), bestV))) / (2 * h);
            Vector3d sv = (PointAt(new Vector2d(bestU, Math.Min(1, bestV + h)))
                         - PointAt(new Vector2d(bestU, Math.Max(0, bestV - h)))) / (2 * h);

            double f1 = Vector3d.Dot(r, su), f2 = Vector3d.Dot(r, sv);
            if (Math.Abs(f1) < 1e-12 && Math.Abs(f2) < 1e-12) break;

            // Матрица вторых производных приближается произведениями касательных: для наших
            // целей этого достаточно, а счёт вдвое дешевле.
            double a11 = Vector3d.Dot(su, su), a12 = Vector3d.Dot(su, sv), a22 = Vector3d.Dot(sv, sv);
            double det = a11 * a22 - a12 * a12;
            if (Math.Abs(det) < 1e-18) break;

            double du2 = -(f1 * a22 - f2 * a12) / det;
            double dv2 = -(f2 * a11 - f1 * a12) / det;

            double nu = Math.Clamp(bestU + du2, 0, 1), nv2 = Math.Clamp(bestV + dv2, 0, 1);
            double nd = (PointAt(new Vector2d(nu, nv2)) - p).LengthSquared;
            // k139: шаг Гаусса — Ньютона, не уменьшивший расстояния, — делится пополам (прежде — конец уточнения: проекция
            // оставалась на точности спуска по сетке, у изогнутой стенки лофта точка касания шара уходила на 0.7 мм)
            if (nd >= best && !ProjHalvingOff)
            {
                double fr = 0.5; bool ok = false;
                for (int h2 = 0; h2 < 12 && !ok; h2++, fr *= 0.5)
                {
                    nu = Math.Clamp(bestU + du2 * fr, 0, 1); nv2 = Math.Clamp(bestV + dv2 * fr, 0, 1);
                    nd = (PointAt(new Vector2d(nu, nv2)) - p).LengthSquared;
                    ok = nd < best;
                }
                if (!ok) break;
            }
            else if (nd >= best) break;
            bestU = nu; bestV = nv2; best = nd;
        }

        return new Vector2d(bestU, bestV);
    }

    // ------------------------------------------------------------------ производные (KRN-07 N1b)

    /// <summary>Номер промежутка узлов для параметра t (последний промежуток включает правый конец).</summary>
    private static int Span(double[] knots, int count, int degree, double t)
    {
        if (t >= knots[count]) return count - 1;
        if (t <= knots[degree]) return degree;
        int lo = degree, hi = count;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (t < knots[mid]) hi = mid; else lo = mid; }
        return lo;
    }

    static readonly bool ProjNewtonOff = Environment.GetEnvironmentVariable("P3D_NURBSPROJNEWTON") == "0";
    public static long ProjNewtonHits;
    static readonly bool ProjHalvingOff = Environment.GetEnvironmentVariable("P3D_NURBSPROJHALF") == "0";
    /// <summary>Мутация P3D_NURBSFAST=0: точка — прежним путём (де Бур с выделениями памяти).</summary>
    public static readonly bool FastEvalOff = Environment.GetEnvironmentVariable("P3D_NURBSFAST") == "0";

    /// <summary>Ненулевые базисные функции на промежутке i (Пигль — Тиллер, A2.2).</summary>
    private static void Basis(double[] U, int i, double u, int p, Span<double> N)
    {
        Span<double> left = stackalloc double[p + 1]; Span<double> right = stackalloc double[p + 1];
        N[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - U[i + 1 - j]; right[j] = U[i + j] - u; double saved = 0;
            for (int r = 0; r < j; r++)
            {
                double den = right[r + 1] + left[j - r];
                double temp = den == 0 ? 0 : N[r] / den;
                N[r] = saved + right[r + 1] * temp; saved = left[j - r] * temp;
            }
            N[j] = saved;
        }
    }

    /// <summary>Базисные функции и их первые производные на промежутке (Пигль — Тиллер, A2.3, n = 1).</summary>
    private static void BasisDers(double[] U, int i, double u, int p, Span<double> N, Span<double> dN)
    {
        // k139: без выделений памяти (stackalloc): производные звались миллионы раз (объём и проекции граней NURBS) —
        // 25 ГБ выделений на одно скругление по лофту
        int P1 = p + 1;
        Span<double> ndu = stackalloc double[P1 * P1]; Span<double> left = stackalloc double[P1]; Span<double> right = stackalloc double[P1];
        ndu[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - U[i + 1 - j]; right[j] = U[i + j] - u; double saved = 0;
            for (int r = 0; r < j; r++)
            {
                ndu[j * P1 + r] = right[r + 1] + left[j - r];
                double temp = ndu[j * P1 + r] == 0 ? 0 : ndu[r * P1 + j - 1] / ndu[j * P1 + r];
                ndu[r * P1 + j] = saved + right[r + 1] * temp; saved = left[j - r] * temp;
            }
            ndu[j * P1 + j] = saved;
        }
        for (int j = 0; j <= p; j++) N[j] = ndu[j * P1 + p];
        if (p == 0) { dN[0] = 0; return; }
        for (int r = 0; r <= p; r++)
        {
            // первая производная: p·(N_{r,p−1}/(U_{i+r}−U_{i+r−p}) − N_{r+1,p−1}/(U_{i+r+1}−U_{i+r+1−p}))
            double d = 0;
            if (r >= 1) { double den = ndu[p * P1 + r - 1]; if (den != 0) d += ndu[(r - 1) * P1 + p - 1] / den; }
            if (r <= p - 1) { double den = ndu[p * P1 + r]; if (den != 0) d -= ndu[r * P1 + p - 1] / den; }
            dN[r] = d * p;
        }
    }

    /// <summary>Точка и первые производные рациональной поверхности (точно, без разностей): S_u = (A_u − w_u S)/w.</summary>
    public void Derivs(Vector2d uv, out Vector3d S, out Vector3d Su, out Vector3d Sv) => DerivsCore(uv, false, out S, out Su, out Sv);

    /// <summary>
    /// KRN-06 (06.14): точка и производные ПРОДОЛЖЕНИЯ поверхности за край области параметров — многочлены крайних
    /// промежутков (рациональное продолжение того же куска: внутри области — та же точка, у края — гладко). Нужна построителю
    /// сопряжений: лента у конца ребра продлевается за торец и обрезается им, а грань NURBS часто кончается ровно на торце.
    /// </summary>
    public void DerivsExtended(Vector2d uv, out Vector3d S, out Vector3d Su, out Vector3d Sv) => DerivsCore(uv, true, out S, out Su, out Sv);

    void DerivsCore(Vector2d uv, bool extend, out Vector3d S, out Vector3d Su, out Vector3d Sv)
    {
        double uc = Math.Clamp(uv.X, 0, 1), vc = Math.Clamp(uv.Y, 0, 1);
        double u = extend ? uv.X : uc, v = extend ? uv.Y : vc;
        int nu = CountU, nv = CountV;
        int su = Span(_ku, nu, _du, uc), sv = Span(_kv, nv, _dv, vc);
        Span<double> Nu = stackalloc double[_du + 1]; Span<double> dNu = stackalloc double[_du + 1]; Span<double> Nv = stackalloc double[_dv + 1]; Span<double> dNv = stackalloc double[_dv + 1];
        BasisDers(_ku, su, u, _du, Nu, dNu); BasisDers(_kv, sv, v, _dv, Nv, dNv);
        Vector3d A = Vector3d.Zero, Au = Vector3d.Zero, Av = Vector3d.Zero; double w = 0, wu = 0, wv = 0;
        for (int a = 0; a <= _du; a++)
            for (int b = 0; b <= _dv; b++)
            {
                int i = su - _du + a, j = sv - _dv + b; double ww = _w[i, j]; var pw = _cp[i, j] * ww;
                double n = Nu[a] * Nv[b], nuu = dNu[a] * Nv[b], nvv = Nu[a] * dNv[b];
                A += pw * n; Au += pw * nuu; Av += pw * nvv; w += ww * n; wu += ww * nuu; wv += ww * nvv;
            }
        S = A / w; Su = (Au - S * wu) / w; Sv = (Av - S * wv) / w;
    }

    /// <summary>Образ при аффинном отображении точек (перенос, поворот, зеркало): опорные точки отображаются, веса и узлы те же —
    /// рациональная поверхность переходит в рациональную точно.</summary>
    /// <summary>
    /// k139: ближайшая точка ПРОДОЛЖЕНИЯ поверхности за край области (DerivsExtended): Гаусс — Ньютон от обычной проекции.
    /// null — не сошлось или ушло дальше maxOut по параметру за край.
    /// </summary>
    public (Vector2d Uv, Vector3d P, Vector3d N)? ProjectExtended(Vector3d p, double maxOut = 0.5)
    {
        var uv = ProjectToUV(p);
        for (int it = 0; it < 40; it++)
        {
            DerivsExtended(uv, out var S, out var Su, out var Sv);
            var r = S - p;
            double f1 = Vector3d.Dot(r, Su), f2 = Vector3d.Dot(r, Sv);
            double a11 = Vector3d.Dot(Su, Su), a12 = Vector3d.Dot(Su, Sv), a22 = Vector3d.Dot(Sv, Sv), det = a11 * a22 - a12 * a12;
            if (Math.Abs(det) < 1e-24) return null;
            double du = -(f1 * a22 - f2 * a12) / det, dv = -(f2 * a11 - f1 * a12) / det;
            uv += new Vector2d(du, dv);
            if (uv.X < -maxOut || uv.X > 1 + maxOut || uv.Y < -maxOut || uv.Y > 1 + maxOut) return null;
            if (Math.Abs(du) + Math.Abs(dv) < 1e-13)
            {
                DerivsExtended(uv, out var S2, out var Su2, out var Sv2);
                var n = Vector3d.Cross(Su2, Sv2); if (n.LengthSquared < 1e-30) return null;
                return (uv, S2, n.Normalized());
            }
        }
        return null;
    }

    /// <summary>k139: та же поверхность с обратным направлением u (нормаль — в другую сторону): опорные точки и веса в обратном
    /// порядке по u, узлы u → 1 − u.</summary>
    public NurbsSurface ReversedU()
    {
        int nu = CountU, nv = CountV; var cp = new Vector3d[nu, nv]; var w = new double[nu, nv];
        for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) { cp[i, j] = _cp[nu - 1 - i, j]; w[i, j] = _w[nu - 1 - i, j]; }
        var ku = _ku.Select(k => 1 - k).Reverse().ToArray();
        return new NurbsSurface(cp, _du, _dv, w, ku, (double[])_kv.Clone());
    }

    public NurbsSurface Mapped(Func<Vector3d, Vector3d> map)
    {
        int nu = CountU, nv = CountV; var cp = new Vector3d[nu, nv]; var w = new double[nu, nv];
        for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) { cp[i, j] = map(_cp[i, j]); w[i, j] = _w[i, j]; }
        return new NurbsSurface(cp, _du, _dv, w, (double[])_ku.Clone(), (double[])_kv.Clone());
    }

    private Vector3d[,]? _grid;
    private Vector3d[,] BuildGrid(int n)
    {
        var g = new Vector3d[n + 1, n + 1];
        for (int i = 0; i <= n; i++) for (int j = 0; j <= n; j++) g[i, j] = PointAt(new Vector2d(i / (double)n, j / (double)n));
        return g;
    }

    /// <summary>Проекция с начальным приближением (ряд близких точек): Ньютон из подсказки; не сошлось — общая проекция.</summary>
    public Vector2d ProjectToUV(Vector3d p, Vector2d hint)
    {
        double u = Math.Clamp(hint.X, 0, 1), v = Math.Clamp(hint.Y, 0, 1);
        const double h = 1e-6;
        double best = (PointAt(new Vector2d(u, v)) - p).LengthSquared;
        for (int it = 0; it < 20; it++)
        {
            var q = PointAt(new Vector2d(u, v)); var r = q - p;
            double ua = Math.Max(0, u - h), ub = Math.Min(1, u + h), va = Math.Max(0, v - h), vb = Math.Min(1, v + h);
            var su = (PointAt(new Vector2d(ub, v)) - PointAt(new Vector2d(ua, v))) / (ub - ua);
            var sv = (PointAt(new Vector2d(u, vb)) - PointAt(new Vector2d(u, va))) / (vb - va);
            double f1 = Vector3d.Dot(r, su), f2 = Vector3d.Dot(r, sv);
            double a11 = Vector3d.Dot(su, su), a12 = Vector3d.Dot(su, sv), a22 = Vector3d.Dot(sv, sv), det = a11 * a22 - a12 * a12;
            if (Math.Abs(det) < 1e-18) break;
            double nu = Math.Clamp(u - (f1 * a22 - f2 * a12) / det, 0, 1), nv = Math.Clamp(v - (f2 * a11 - f1 * a12) / det, 0, 1);
            double nd = (PointAt(new Vector2d(nu, nv)) - p).LengthSquared;
            if (nd > best * (1 + 1e-12) + 1e-30) break;
            bool small = Math.Abs(nu - u) < 1e-14 && Math.Abs(nv - v) < 1e-14;
            u = nu; v = nv; best = nd;
            if (small) break;
        }
        // годится, если невязка перпендикулярна поверхности (или точка на ней); иначе — общая проекция
        var qq = PointAt(new Vector2d(u, v)); var rr = qq - p;
        if (rr.Length < 1e-9) return new Vector2d(u, v);
        var nn = NormalAt(new Vector2d(u, v));
        if (Math.Abs(Vector3d.Dot(rr.Normalized(), nn)) > 1 - 1e-8 && u > 0 && u < 1 && v > 0 && v < 1) return new Vector2d(u, v);
        return ProjectToUV(p);
    }

    public bool IsPlanar => false;

    // ------------------------------------------------------------------ гранение

    // k78: ГРАНЬ — ВСЯ ОБЛАСТЬ ПАРАМЕТРОВ ЗАМКНУТОГО ЛОСКУТА (шар, тор, конус после неравномерного масштаба): рёбра лежат на
    // краю области, шов рёбер не имеет. Признак нужен объёму и площади (AnalyticVolume.WholeNurbs).
    public (double U0, double U1, double V0, double V1) Domain => (_ku[_du], _ku[_ku.Length - 1 - _du], _kv[_dv], _kv[_kv.Length - 1 - _dv]);

    /// <summary>
    /// Все рёбра грани лежат на краю области параметров (и на поверхности); отдаёт точки ломаных контуров в параметрах.
    /// Грань без рёбер — не сюда (вся область и так). null — не вся область.
    /// </summary>
    public List<Vector2d>? WholeDomainBoundary(TFace face)
    {
        if (face.Loops.All(l => l.CoEdges.Count == 0)) return null;
        var (u0, u1, v0, v1) = Domain;
        double tu = 1e-7 * (u1 - u0), tv = 1e-7 * (v1 - v0);
        var res = new List<Vector2d>();
        foreach (var l in face.Loops)
        {
            if (l.CoEdges.Count == 0) continue;
            foreach (var p in l.TessellatePolyline())
            {
                var uv = ProjectToUV(p);
                if ((PointAt(uv) - p).Length > Math.Max(1e-7, Tol.Weld)) return null;
                bool onU = Math.Abs(uv.X - u0) < tu || Math.Abs(uv.X - u1) < tu, onV = Math.Abs(uv.Y - v0) < tv || Math.Abs(uv.Y - v1) < tv;
                if (!onU && !onV) return null;
                res.Add(uv);
            }
        }
        // хотя бы одно направление замкнуто (иначе это обычный лоскут с краями — общий путь)
        bool closedV = Enumerable.Range(0, 5).All(k => { double u = u0 + (u1 - u0) * k / 4; return (PointAt(new Vector2d(u, v0)) - PointAt(new Vector2d(u, v1))).Length < 1e-9 * Math.Max(1, PointAt(new Vector2d(u, v0)).Length); });
        bool closedU = Enumerable.Range(0, 5).All(k => { double v = v0 + (v1 - v0) * k / 4; return (PointAt(new Vector2d(u0, v)) - PointAt(new Vector2d(u1, v))).Length < 1e-9 * Math.Max(1, PointAt(new Vector2d(u0, v)).Length); });
        return closedU || closedV ? res : null;
    }

    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        // ГРАНЕНИЕ ИДЁТ ПО КОНТУРУ ГРАНИ, а не по всей области параметров: иначе у вырезанного
        // куска поверхности сетка накрывает лишнее — ровно та беда, что была у прочитанных из
        // обменного файла цилиндров.
        var loops = FaceLoopsUV(face);
        if (loops.Count == 0) { FullDomain(face, mb); return; }

        var outer = loops[0];
        var holes = loops.Skip(1).Cast<IReadOnlyList<Vector2d>>().ToList();
        var tris = Cdt.Triangulate(outer, holes, out _);
        if (tris == null) { FullDomain(face, mb); return; }

        // Точки контура в развёртке — вершины сетки; их положение в пространстве берётся с
        // поверхности, чтобы грань и её рёбра сходились.
        var all = new List<Vector2d>(outer);
        foreach (var h in loops.Skip(1)) all.AddRange(h);

        int start = mb.VertexCount;
        foreach (var uv in all)
        {
            Vector3d nor = NormalAt(uv);
            if (face.Reversed) nor = -nor;
            mb.AddVertex(PointAt(uv), nor);
        }

        for (int i = 0; i + 2 < tris.Count; i += 3)
            if (face.Reversed) mb.AddTriangle(start + tris[i], start + tris[i + 2], start + tris[i + 1]);
            else mb.AddTriangle(start + tris[i], start + tris[i + 1], start + tris[i + 2]);
    }

    /// <summary>Контуры грани в параметрах поверхности.</summary>
    private List<List<Vector2d>> FaceLoopsUV(TFace face)
    {
        var res = new List<List<Vector2d>>();
        foreach (var loop in face.Loops)
        {
            var pts = new List<Vector2d>();
            foreach (var ce in loop.CoEdges)
            {
                var c = ce.Edge.Curve;
                int n = Math.Max(2, (int)Math.Ceiling(c.Length / Math.Max(Tol.Chord * 20, 0.5)));
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)n;
                    double tt = ce.Reversed ? c.T1 + (c.T0 - c.T1) * t : c.T0 + (c.T1 - c.T0) * t;
                    pts.Add(ProjectToUV(c.PointAt(tt)));
                }
            }
            if (pts.Count >= 3) res.Add(pts);
        }
        return res;
    }

    /// <summary>Сетка по всей области — запасной путь, когда контура нет или он не лёг.</summary>
    private void FullDomain(TFace face, MeshBuilder mb)
    {
        int n = Steps();
        int start = mb.VertexCount;

        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                var uv = new Vector2d(i / (double)n, j / (double)n);
                Vector3d nor = NormalAt(uv);
                if (face.Reversed) nor = -nor;
                mb.AddVertex(PointAt(uv), nor);
            }

        int stride = n + 1;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int a = start + i * stride + j, b = a + stride, c = b + 1, d = a + 1;
                mb.AddTriangle(a, b, c);
                mb.AddTriangle(a, c, d);
            }
    }

    /// <summary>Сколько клеток по каждому направлению нужно, чтобы уложиться в допуск.</summary>
    public int Steps()
    {
        for (int n = 4; n <= 128; n *= 2)
        {
            double worst = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var a = PointAt(new Vector2d(i / (double)n, j / (double)n));
                    var b = PointAt(new Vector2d((i + 1) / (double)n, (j + 1) / (double)n));
                    var mid = PointAt(new Vector2d((i + 0.5) / n, (j + 0.5) / n));
                    worst = Math.Max(worst, (mid - (a + b) * 0.5).Length);
                }
            if (worst <= Tol.Chord) return n;
        }
        return 128;
    }
}
