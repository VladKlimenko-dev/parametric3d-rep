using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// G1. КРИВАЯ В ПАРАМЕТРАХ ПОВЕРХНОСТИ (pcurve): точка (u, v) по параметру t ∈ [T0, T1].
/// Вместе с поверхностью грани она задаёт ребро на этой грани — основа точного B-rep: обрезка
/// грани по контуру в её параметрах, разметка в (u, v), запись PCURVE в STEP.
/// </summary>
public interface ICurve2
{
    double T0 { get; }
    double T1 { get; }
    Vector2d PointAt(double t);
    /// <summary>Точная ли кривая (прямая или дуга в параметрах), а не выборка.</summary>
    bool IsExact { get; }
}

/// <summary>Отрезок в параметрах: у ребра-прямой на плоскости и у изолиний (образующая, параллель).</summary>
public sealed class Line2 : ICurve2
{
    public readonly Vector2d A, B;
    public Line2(Vector2d a, Vector2d b) { A = a; B = b; }
    public double T0 => 0;
    public double T1 => 1;
    public bool IsExact => true;
    public Vector2d PointAt(double t) => A + (B - A) * t;
}

/// <summary>Дуга окружности в параметрах плоскости (координаты плоскости — длины).</summary>
public sealed class Arc2 : ICurve2
{
    public readonly Vector2d Centre;
    public readonly double Radius, A0, A1;
    public Arc2(Vector2d c, double r, double a0, double a1) { Centre = c; Radius = r; A0 = a0; A1 = a1; }
    public double T0 => 0;
    public double T1 => 1;
    public bool IsExact => true;
    public Vector2d PointAt(double t) { double a = A0 + (A1 - A0) * t; return Centre + Radius * new Vector2d(Math.Cos(a), Math.Sin(a)); }
}

/// <summary>Ломаная в параметрах (выборка с проверенным отклонением): у кривых пересечения и прочих.</summary>
/// <summary>g.05: дуга эллипса в параметрах (аффинный образ дуги окружности или эллипса на плоской грани): C + cos a·P + sin a·Q.</summary>
public sealed class Conic2 : ICurve2
{
    public readonly Vector2d C, P, Q; public readonly double A0, A1;
    public Conic2(Vector2d c, Vector2d p, Vector2d q, double a0, double a1) { C = c; P = p; Q = q; A0 = a0; A1 = a1; }
    public double T0 => 0;
    public double T1 => 1;
    public Vector2d PointAt(double t) { double a = A0 + (A1 - A0) * t; return C + P * Math.Cos(a) + Q * Math.Sin(a); }
    public bool IsExact => true;
}

public sealed class Polyline2 : ICurve2
{
    public readonly Vector2d[] Pts;
    public Polyline2(IEnumerable<Vector2d> pts) { Pts = pts.ToArray(); }
    public double T0 => 0;
    public double T1 => 1;
    public bool IsExact => false;
    public Vector2d PointAt(double t)
    {
        if (Pts.Length == 1) return Pts[0];
        double x = Math.Clamp(t, 0, 1) * (Pts.Length - 1);
        int i = Math.Min((int)x, Pts.Length - 2);
        return Pts[i] + (Pts[i + 1] - Pts[i]) * (x - i);
    }
}

/// <summary>
/// KRN-03: КУСОЧНО-КУБИЧЕСКАЯ КРИВАЯ ЭРМИТА в параметрах — контролируемое численное представление p-кривой: узлы по t
/// (равномерные и изломы параметризации 3D-кривой), значения и ОДНОСТОРОННИЕ производные по t в узлах (у излома левая и
/// правая различны). Погрешность ∝ h⁴ (у ломаной ∝ h²: при 2048 точках она упиралась в ~1e-5 мм); уплотнение — до
/// модельной точности.
/// </summary>
public sealed class Hermite2 : ICurve2
{
    public readonly double[] T;
    public readonly Vector2d[] P, Dl, Dr;
    public Hermite2(double[] t, Vector2d[] p, Vector2d[] dl, Vector2d[] dr) { T = t; P = p; Dl = dl; Dr = dr; }
    public double T0 => 0;
    public double T1 => 1;
    public bool IsExact => false;
    public Vector2d PointAt(double t)
    {
        t = Math.Clamp(t, 0, 1);
        int i = Array.BinarySearch(T, t); if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, T.Length - 2);
        double h = T[i + 1] - T[i]; double s = h > 0 ? (t - T[i]) / h : 0;
        double s2 = s * s, s3 = s2 * s;
        double h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
        return P[i] * h00 + Dr[i] * (h10 * h) + P[i + 1] * h01 + Dl[i + 1] * (h11 * h);
    }
}

/// <summary>
/// KRN-03: ПРОЦЕДУРНАЯ p-КРИВАЯ кривой пересечения: (u, v) = проекция точки самой 3D-кривой на поверхность грани, с
/// развёрткой периода по опорной выборке. Согласована по параметру по построению (отклонение — точность проекции), как
/// сама кривая пересечения (intcurve) — определением «пересечение A и B». Аппроксимация (ломаная, Эрмита) по тысячам
/// изломов параметризации затравки не сходилась: 4.4e-6…1e-3 мм на детали ×1000.
/// </summary>
public sealed class ProjectedCurve2 : ICurve2
{
    readonly Func<double, Vector2d> _project;
    readonly Vector2d[] _ref; readonly double _perU, _perV;
    public ProjectedCurve2(Func<double, Vector2d> project, Vector2d[] reference, double perU, double perV)
    { _project = project; _ref = reference; _perU = perU; _perV = perV; }
    public double T0 => 0;
    public double T1 => 1;
    /// <summary>Точная по построению (проекция самой кривой), не выборка.</summary>
    public bool IsExact => true;
    public Vector2d PointAt(double t)
    {
        var q = _project(Math.Clamp(t, 0, 1));
        var r = _ref[(int)Math.Round(Math.Clamp(t, 0, 1) * (_ref.Length - 1))];
        if (_perU > 0) q.X += _perU * Math.Round((r.X - q.X) / _perU);
        if (_perV > 0) q.Y += _perV * Math.Round((r.Y - q.Y) / _perV);
        return q;
    }
}

