using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// KRN-06 (06.12): КРИВАЯ ПО ФОРМУЛЕ — точка вычисляется при каждом обращении (линия касания катящегося шара: проекция
/// точки хребта на поверхность грани, хребет — уточнением Ньютона до машинной точности). Параметр — параметр исходного
/// ребра. Гранение — по прогибу и угловому шагу текущего допуска, с запоминанием на эту точность.
/// </summary>
public sealed class ProcCurve3 : ICurve3
{
    private readonly Func<double, Vector3d> _f;
    private readonly double _t0, _t1;
    private readonly bool _closed;
    /// <summary>Что это за кривая (для разбора и подписи).</summary>
    public readonly string Kind;

    public ProcCurve3(Func<double, Vector3d> f, double t0, double t1, bool closed, string kind)
    { _f = f; _t0 = t0; _t1 = t1; _closed = closed; Kind = kind; }

    /// <summary>
    /// 04.2: строгое доказательство «событий с поверхностью нет» (поставляет построитель кривой): true — кривая на
    /// [T0, T1] не задевает поверхность; false — не доказано (события ищутся выборкой, маршрут отказывает).
    /// </summary>
    public Func<Geometry.Ssi.Shape, double, double, bool>? NoEventsOn;

    /// <summary>
    /// 06.12: ТА ЖЕ КРИВАЯ КАК ТОЧНАЯ КОНИКА (линия касания скругления тройника равных труб — плоский эллипс), с тем же
    /// началом и направлением. Ядро работает с процедурной (параметр — как у ленты), точный STEP пишет конику.
    /// </summary>
    public EllipseCurve3? ExactConic;

    public double T0 => _t0;
    public double T1 => _t1;
    public bool IsClosed => _closed;
    public Vector3d PointAt(double t) => _f(Math.Clamp(t, Math.Min(_t0, _t1), Math.Max(_t0, _t1)));

    public Vector3d TangentAt(double t)
    {
        double h = 1e-6 * Math.Abs(_t1 - _t0);
        double a = t - h, b = t + h;
        if (!_closed) { a = Math.Max(Math.Min(_t0, _t1), a); b = Math.Min(Math.Max(_t0, _t1), b); }
        Vector3d d = Wrap(b) - Wrap(a);
        if (_t1 < _t0) d = -d;
        return d.LengthSquared < 1e-30 ? Vector3d.UnitX : d.Normalized();
    }

    /// <summary>Замкнутая: параметр за концами — по периоду.</summary>
    private Vector3d Wrap(double t)
    {
        if (!_closed) return _f(t);
        double lo = Math.Min(_t0, _t1), per = Math.Abs(_t1 - _t0);
        double u = (t - lo) % per; if (u < 0) u += per;
        return _f(lo + u);
    }

    public double Length { get { var p = Dense(); double L = 0; for (int i = 1; i < p.Count; i++) L += (p[i] - p[i - 1]).Length; return L; } }
    public Bounds3 Bounds { get { var b = Bounds3.Empty; foreach (var v in Dense()) b.Add(v); return b; } }
    /// <summary>Часть кривой на [lo, hi] (параметр тот же); весь промежуток замкнутой — замкнутая.</summary>
    public ProcCurve3 Sub(double lo, double hi)
    {
        bool whole = Math.Abs(lo - Math.Min(_t0, _t1)) < 1e-15 && Math.Abs(hi - Math.Max(_t0, _t1)) < 1e-15;
        return new ProcCurve3(_f, lo, hi, whole && _closed, Kind) { NoEventsOn = NoEventsOn };
    }

    /// <summary>Та же кривая под отображением точек (жёсткое преобразование тела): параметр — тот же.</summary>
    public ProcCurve3 Mapped(Func<Vector3d, Vector3d> x) { var f = _f; return new ProcCurve3(t => x(f(t)), _t0, _t1, _closed, Kind); }

    public ICurve3 Reversed() => new ProcCurve3(_f, _t1, _t0, _closed, Kind) { NoEventsOn = NoEventsOn };

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        var pts = Dense();
        for (int i = includeFirstPoint ? 0 : 1; i < pts.Count; i++) dst.Add(pts[i]);
    }

    private sealed record DenseCache(double Chord, double Angle, List<Vector3d> P);
    private volatile DenseCache? _dense;   // одной ссылкой: гранение идёт из нескольких потоков

    private List<Vector3d> Dense()
    {
        double chord = Core.Tol.Chord, ang = Core.Tol.ChordAngle;
        if (_dense is { } d && d.Chord == chord && d.Angle == ang) return d.P;
        var ts = new List<double>(); int n0 = 16;
        for (int i = 0; i <= n0; i++) ts.Add(_t0 + (_t1 - _t0) * i / n0);
        var ps = ts.Select(_f).ToList();
        for (int pass = 0; pass < 14; pass++)
        {
            var nt = new List<double> { ts[0] }; var np = new List<Vector3d> { ps[0] }; bool added = false;
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                double tm = (ts[i] + ts[i + 1]) / 2; var m = _f(tm);
                var a = ps[i]; var b = ps[i + 1]; var ab = b - a; double l2 = ab.LengthSquared;
                double dev = l2 < 1e-30 ? (m - a).Length : (a + ab * Math.Clamp(Vector3d.Dot(m - a, ab) / l2, 0, 1) - m).Length;
                // угловой шаг: поворот касательной между соседними хордами половинок
                var c1 = m - a; var c2 = b - m; double turn = c1.LengthSquared < 1e-30 || c2.LengthSquared < 1e-30 ? 0
                    : Math.Acos(Math.Clamp(Vector3d.Dot(c1.Normalized(), c2.Normalized()), -1, 1));
                if (dev > chord || (ang > 0 && turn > ang))
                { nt.Add(tm); np.Add(m); added = true; }
                nt.Add(ts[i + 1]); np.Add(ps[i + 1]);
            }
            ts = nt; ps = np;
            if (!added) break;
        }
        _dense = new DenseCache(chord, ang, ps);
        return ps;
    }
}
