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
        for (int i = 0; i <= grid; i++)
            for (int j = 0; j <= grid; j++)
            {
                double u = i / (double)grid, v = j / (double)grid;
                double d = (PointAt(new Vector2d(u, v)) - p).LengthSquared;
                if (d < best) { best = d; bestU = u; bestV = v; }
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
            if (nd >= best) break;
            bestU = nu; bestV = nv2; best = nd;
        }

        return new Vector2d(bestU, bestV);
    }

    public bool IsPlanar => false;

    // ------------------------------------------------------------------ гранение

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
