using OpenTK.Mathematics;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// Точная проекция на поверхность для частых случаев: плоскость, цилиндр (выдавливание
/// дуги), выдавливание отрезка — неограниченные. Прочие — через параметры поверхности.
/// Возвращает точку на поверхности и единичную нормаль в ней (знак — как у поверхности).
/// </summary>
public static class ExactSurfaces
{
    /// <summary>
    /// Проекция без согласования знака нормали с поверхностью (знак произвольный) —
    /// для тех, кто выставляет знак сам: у цилиндра это экономит проекцию в параметры.
    /// </summary>
    public static (Vector3d P, Vector3d N) ProjectUnsigned(ISurface3 s, Vector3d p)
    {
        switch (s)
        {
            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc:
            {
                Vector3d axis = ex.Dir.Normalized();
                Vector3d rel = p - arc.Frame.Origin;
                Vector3d along = axis * Vector3d.Dot(rel, axis);
                Vector3d radial = rel - along;
                if (radial.LengthSquared > 1e-24)
                {
                    radial = radial.Normalized();
                    return (arc.Frame.Origin + along + radial * arc.Radius, radial);
                }
                break;
            }
            case ExtrudedSurface ex when ex.Base is LineCurve3 line:
            {
                Vector3d n = Vector3d.Cross(line.B - line.A, ex.Dir);
                if (n.LengthSquared > 1e-24)
                {
                    n = n.Normalized();
                    return (p - n * Vector3d.Dot(p - line.A, n), n);
                }
                break;
            }
        }
        return Project(s, p);
    }

    public static (Vector3d P, Vector3d N) Project(ISurface3 s, Vector3d p)
    {
        switch (s)
        {
            case PlaneSurface pl:
            {
                Vector3d n = pl.F.W.Normalized();
                return (p - n * Vector3d.Dot(p - pl.F.Origin, n), n);
            }
            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc:
            {
                Vector3d axis = ex.Dir.Normalized();
                Vector3d rel = p - arc.Frame.Origin;
                Vector3d along = axis * Vector3d.Dot(rel, axis);
                Vector3d radial = rel - along;
                if (radial.LengthSquared > 1e-24)
                {
                    radial = radial.Normalized();
                    Vector3d q = arc.Frame.Origin + along + radial * arc.Radius;
                    Vector3d ns = s.NormalAt(s.ProjectToUV(q));
                    return (q, Vector3d.Dot(ns, radial) < 0 ? -radial : radial);
                }
                break;
            }
            case ExtrudedSurface ex when ex.Base is LineCurve3 line:
            {
                Vector3d n = Vector3d.Cross(line.B - line.A, ex.Dir);
                if (n.LengthSquared > 1e-24)
                {
                    n = n.Normalized();
                    Vector3d q = p - n * Vector3d.Dot(p - line.A, n);
                    Vector3d ns = s.NormalAt(s.ProjectToUV(q));
                    return (q, Vector3d.Dot(ns, n) < 0 ? -n : n);
                }
                break;
            }
        }
        Vector2d uv = s.ProjectToUV(p);
        Vector3d nn = s.NormalAt(uv);
        return (s.PointAt(uv), nn.LengthSquared > 1e-24 ? nn.Normalized() : Vector3d.Zero);
    }
}

/// <summary>
/// ПРОЦЕДУРНАЯ КРИВАЯ ПЕРЕСЕЧЕНИЯ ДВУХ ПОВЕРХНОСТЕЙ.
///
/// ДОБАВЛЕНО. Кривая пересечения цилиндра со скруглением — кривая четвёртого порядка,
/// и хранить её формулой незачем: достаточно определения «пересечение A и B» и затравки
/// (ломаной из вершин граней). Точка вычисляется уточнением: касательные плоскости обеих
/// поверхностей пересекаются с плоскостью, поперечной к кривой, и так до сходимости —
/// точка ложится на обе ТОЧНЫЕ поверхности. Касательная — nA × nB. Так хранят кривые
/// пересечения промышленные ядра (intcurve у ACIS, intersection curve у Parasolid).
///
/// Концы — точно вершины тела (не уточняются): каркас в углах не должен расходиться.
/// Параметр — доля длины затравки.
/// </summary>
public sealed class IntersectionCurve3 : ICurve3
{
    public readonly ISurface3 A, B;
    private readonly Vector3d[] _seed;
    private readonly double[] _s;          // накопленная длина затравки
    private Vector3d[]? _refined;

    /// <summary>Затравка — вершины граней на ребре (от начала к концу).</summary>
    public IReadOnlyList<Vector3d> Seed => _seed;

    /// <summary>
    /// KRN-03: изломы параметризации (доли t точек затравки): между ними скорость по t постоянна по затравке, в них —
    /// скачок производной. p-кривая, согласованная по параметру, ставит узлы сюда.
    /// </summary>
    public IEnumerable<double> Breaks { get { double tot = _s[^1]; if (tot <= 0) yield break; for (int i = 1; i < _s.Length - 1; i++) yield return _s[i] / tot; } }

    public IntersectionCurve3(ISurface3 a, ISurface3 b, IReadOnlyList<Vector3d> seed)
    {
        A = a; B = b;
        _seed = seed.ToArray();
        _s = new double[_seed.Length];
        for (int i = 1; i < _seed.Length; i++) _s[i] = _s[i - 1] + (_seed[i] - _seed[i - 1]).Length;
    }

    public double T0 => 0.0;
    public double T1 => 1.0;
    public bool IsClosed => false;

    /// <summary>Уточнить точку на пересечение: поперечная плоскость — через x0 с нормалью tS.</summary>
    public Vector3d Refine(Vector3d x0, Vector3d tS, out double residual)
    {
        Vector3d x = x0;
        residual = double.MaxValue;
        for (int it = 0; it < 12; it++)
        {
            var (pa, na) = ProjA(x);
            var (pb, nb) = ProjB(x);
            residual = Math.Max((pa - x).Length, (pb - x).Length);
            if (residual < 1e-12) break;
            // Решить: na·y = na·pa, nb·y = nb·pb, tS·y = tS·x0.
            // Правило Крамера: y = (ra·(nb×tS) + rb·(tS×na) + rt·(na×nb)) / (na·(nb×tS)).
            Vector3d bc = Vector3d.Cross(nb, tS), ca = Vector3d.Cross(tS, na), ab = Vector3d.Cross(na, nb);
            double det = Vector3d.Dot(na, bc);
            if (Math.Abs(det) < 1e-12) break;             // поверхности касаются — уточнять нечего
            double ra = Vector3d.Dot(na, pa), rb = Vector3d.Dot(nb, pb), rt = Vector3d.Dot(tS, x0);
            Vector3d y = (bc * ra + ca * rb + ab * rt) / det;
            bool done = (y - x).LengthSquared < 1e-24;
            x = y;
            if (done) break;
        }
        var (qa, _) = ProjA(x);
        var (qb, _) = ProjB(x);
        residual = Math.Max((qa - x).Length, (qb - x).Length);
        return x;
    }

    /// <summary>
    /// g.13: ПРОЕКЦИЯ НА ПОВЕРХНОСТЬ ПО ЕЁ АНАЛИТИЧЕСКОЙ ФОРМЕ (плоскость, цилиндр, конус, шар, тор): q = x − F·∇F, где F —
    /// расстояние со знаком, |∇F| = 1 (Ssi.Implicit) — вместо проекции в параметры поверхности (у тела вращения — atan2,
    /// синусы и ньютон по образующей; замер: уточнение точек кривых пересечения — половина времени общей булевой). Для
    /// уточнения знак нормали не важен (плоскость n·y = n·q). Поверхность без аналитической формы — прежняя проекция.
    /// Мутация P3D_REFINESHAPE=0.
    /// </summary>
    (Vector3d P, Vector3d N) ProjA(Vector3d x) { if (!_shapesSet) SetShapes(); return _sa != null ? ShapeProj(_sa, x) : ExactSurfaces.Project(A, x); }
    (Vector3d P, Vector3d N) ProjB(Vector3d x) { if (!_shapesSet) SetShapes(); return _sb != null ? ShapeProj(_sb, x) : ExactSurfaces.Project(B, x); }
    Geometry.Ssi.Shape? _sa, _sb; volatile bool _shapesSet;
    void SetShapes()
    {
        if (!RefineShapeOff && !_oldForTests) { _sa = Geometry.ExactEdges.ShapeOf(A); _sb = Geometry.ExactEdges.ShapeOf(B); }
        _shapesSet = true;
    }
    static (Vector3d P, Vector3d N) ShapeProj(Geometry.Ssi.Shape s, Vector3d x)
    {
        Interlocked.Increment(ref ShapeProjections);
        var (f, g) = Geometry.Ssi.Implicit(s, x);
        return (x - g * f, g);
    }
    internal static readonly bool RefineShapeOff = Environment.GetEnvironmentVariable("P3D_REFINESHAPE") == "0";
    [ThreadStatic] static bool _oldForTests;
    /// <summary>Сравнение (KRN10-REFINE-BY-SHAPE): кривые, созданные в этом потоке, уточняются прежней проекцией.</summary>
    public static bool OldProjectionForTests { get => _oldForTests; set => _oldForTests = value; }
    /// <summary>Счётчик проекций по аналитической форме (проверка KRN10-REFINE-BY-SHAPE).</summary>
    public static long ShapeProjections;

    /// <summary>
    /// Затравка точки при параметре t. Положение — между УТОЧНЁННЫМИ точками затравки
    /// (они уже на кривой; отход — лишь стрела прогиба между ними), поэтому уточнение
    /// сходится за один-два шага, а не за дюжину от хорды. Направление поперечной
    /// плоскости и доля — по исходной затравке: параметризация от этого не меняется.
    /// </summary>
    private (Vector3d X0, Vector3d T) SeedAt(double t)
    {
        double total = _s[^1];
        if (total <= 0) return (_seed[0], Vector3d.UnitX);
        double at = Math.Clamp(t, 0, 1) * total;
        int i = Array.BinarySearch(_s, at);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, _seed.Length - 2);
        double len = _s[i + 1] - _s[i];
        double f = len > 0 ? (at - _s[i]) / len : 0;
        Vector3d d = _seed[i + 1] - _seed[i];
        var r = Refined();
        return (r[i] + (r[i + 1] - r[i]) * f, d.LengthSquared > 0 ? d.Normalized() : Vector3d.UnitX);
    }

    private sealed record Memo(double T, Vector3d P);
    private Memo? _memo;

    public Vector3d PointAt(double t)
    {
        if (t <= 0) return _seed[0];
        if (t >= 1) return _seed[^1];
        var m = _memo;
        if (m != null && m.T == t) return m.P;
        var (x0, tS) = SeedAt(t);
        Vector3d x = Refine(x0, tS, out _);
        _memo = new Memo(t, x);
        return x;
    }

    public Vector3d TangentAt(double t)
    {
        var (x0, tS) = SeedAt(t);
        Vector3d x = t <= 0 || t >= 1 ? x0 : PointAt(t);
        var (_, na) = ProjA(x);
        var (_, nb) = ProjB(x);
        Vector3d c = Vector3d.Cross(na, nb);
        if (c.LengthSquared < 1e-18) return tS;
        c = c.Normalized();
        return Vector3d.Dot(c, tS) < 0 ? -c : c;
    }

    /// <summary>Уточнённые точки затравки (концы — как есть).</summary>
    // Невязку концов (вершина тела лежит в сотых от точного пересечения) распределять вдоль
    // кривой нельзя: кривая обязана лежать на обеих поверхностях, иначе её точки сходят с
    // цилиндра. Излом у самого конца остаётся; он порядка отхода вершины, сотые доли.

    /// <summary>Уточнённые точки затравки и их доли параметра (для строгих событий на кривой).</summary>
    internal (Vector3d[] P, double[] T) RefinedWithParams()
    {
        var r = Refined(); double total = _s[^1]; var t = new double[r.Length];
        for (int i = 0; i < r.Length; i++) t[i] = total > 0 ? _s[i] / total : i / (double)Math.Max(1, r.Length - 1);
        t[^1] = 1;
        return (r, t);
    }

    private Vector3d[] Refined()
    {
        if (_refined != null) return _refined;
        var r = new Vector3d[_seed.Length];
        for (int i = 0; i < _seed.Length; i++)
        {
            if (i == 0 || i == _seed.Length - 1) { r[i] = _seed[i]; continue; }
            Vector3d tS = (_seed[i + 1] - _seed[i - 1]);
            r[i] = tS.LengthSquared > 0 ? Refine(_seed[i], tS.Normalized(), out _) : _seed[i];
        }
        return _refined = r;
    }

    public double Length
    {
        get { var r = Refined(); double l = 0; for (int i = 1; i < r.Length; i++) l += (r[i] - r[i - 1]).Length; return l; }
    }

    public Bounds3 Bounds
    {
        get { var b = Bounds3.Empty; foreach (var p in Refined()) b.Add(p); return b; }
    }

    // Enumerable.Reverse, а не _seed.Reverse(): в новых версиях C# для массива вызов
    // берётся из MemoryExtensions (разворот на месте, void) — см. BezierCurve3.Reversed.
    public ICurve3 Reversed() => new IntersectionCurve3(A, B, Enumerable.Reverse(_seed).ToArray());

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        var pts = Dense();
        for (int i = includeFirstPoint ? 0 : 1; i < pts.Count; i++) dst.Add(pts[i]);
    }

    /// <summary>
    /// ГРАНЕНИЕ ПО ПРОГИБУ. Кривая гранилась точками своей затравки — их ставила булева, и для
    /// самой кривой они бывают редки: хорда отходила от кривой на 0.14 мм при допуске 0.02, и
    /// сетка грани по такому ребру уходила от поверхности (прогон, 10:5). Где хорда отходит
    /// дальше допуска, вставляется точка кривой; параметр — доля длины по затравке.
    /// </summary>
    private List<Vector3d>? _dense;

    private List<Vector3d> Dense()
    {
        if (_dense != null) return _dense;
        var r = Refined();
        double total = _s[^1];
        var ts = new List<double>(r.Length);
        for (int i = 0; i < r.Length; i++) ts.Add(total > 0 ? _s[i] / total : i / (double)Math.Max(1, r.Length - 1));
        var pts = new List<Vector3d>(r);
        for (int pass = 0; pass < 6; pass++)
        {
            var nt = new List<double> { ts[0] };
            var np = new List<Vector3d> { pts[0] };
            bool added = false;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double tm = (ts[i] + ts[i + 1]) / 2;
                Vector3d m = PointAt(tm);
                double chord = (pts[i + 1] - pts[i]).Length;
                double bulge = (m - (pts[i] + pts[i + 1]) / 2).Length;
                // Выпячивание больше половины хорды — кривая в этом месте не обусловлена
                // (у концов, где вершина тела лежит на сетке ядра в сотых от обеих
                // поверхностей, уточнение уводит точку в сторону). Такая точка не вставляется.
                if (bulge > Core.Tol.Chord && chord > 8 * Core.Tol.Chord && bulge < 0.5 * chord)
                {
                    nt.Add(tm); np.Add(m); added = true;
                }
                nt.Add(ts[i + 1]); np.Add(pts[i + 1]);
            }
            ts = nt; pts = np;
            if (!added) break;
        }
        return _dense = pts;
    }

    /// <summary>
    /// Кривая по цепочке, если каждая внутренняя точка затравки сходится на обе поверхности
    /// (невязка не больше tol) и сдвигается не больше maxShift; иначе null.
    /// </summary>
    public static IntersectionCurve3? TryBuild(ISurface3 a, ISurface3 b, IReadOnlyList<Vector3d> seed,
                                               double tol = 1e-7, double maxShift = 0.5)
    {
        if (seed.Count < 3) return null;
        var c = new IntersectionCurve3(a, b, seed);
        for (int i = 1; i + 1 < seed.Count; i++)
        {
            Vector3d tS = seed[i + 1] - seed[i - 1];
            if (tS.LengthSquared < 1e-24) return null;
            Vector3d x = c.Refine(seed[i], tS.Normalized(), out double res);
            if (res > tol || (x - seed[i]).Length > maxShift) return null;
        }
        return c;
    }
}
