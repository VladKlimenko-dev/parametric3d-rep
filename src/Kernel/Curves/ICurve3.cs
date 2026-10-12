using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// Аналитическая кривая в пространстве. Никаких вершинных буферов внутри —
/// только математика. Тесселяция выдаётся по запросу тесселятору/рендеру.
/// </summary>
public interface ICurve3
{
    double T0 { get; }
    double T1 { get; }
    bool IsClosed { get; }
    Vector3d PointAt(double t);
    Vector3d TangentAt(double t);
    double Length { get; }
    Bounds3 Bounds { get; }
    ICurve3 Reversed();
    /// <summary>Разбивает кривую на ломаную по текущему допуску Tol.Chord/ChordAngle.</summary>
    void Tessellate(List<Vector3d> dst, bool includeFirstPoint);
}

public sealed class LineCurve3 : ICurve3
{
    public readonly Vector3d A, B;
    public LineCurve3(Vector3d a, Vector3d b) { A = a; B = b; }

    public double T0 => 0.0;
    public double T1 => 1.0;
    public bool IsClosed => false;
    public Vector3d PointAt(double t) => A + (B - A) * t;
    public Vector3d TangentAt(double t) => (B - A).Normalized();
    public double Length => (B - A).Length;
    public Bounds3 Bounds { get { var b = Bounds3.Empty; b.Add(A); b.Add(B); return b; } }
    public ICurve3 Reversed() => new LineCurve3(B, A);

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        if (includeFirstPoint) dst.Add(A);
        dst.Add(B);
    }
}

/// <summary>Дуга/окружность: центр + локальный фрейм + радиус + углы (рад).</summary>
public sealed class ArcCurve3 : ICurve3
{
    public readonly Frame3 Frame;   // Origin = центр, W = ось дуги
    public readonly double Radius;
    public readonly double A0, A1;  // A1 может быть меньше A0 (обратное направление)

    public ArcCurve3(Frame3 frame, double radius, double a0, double a1)
    { Frame = frame; Radius = radius; A0 = a0; A1 = a1; }

    public static ArcCurve3 FullCircle(Frame3 f, double r) => new ArcCurve3(f, r, 0, Math.PI * 2);

    public double T0 => 0.0;
    public double T1 => 1.0;
    public double Sweep => A1 - A0;
    public bool IsClosed => Math.Abs(Math.Abs(Sweep) - Math.PI * 2) < 1e-9;

    public Vector3d PointAt(double t)
    {
        double a = A0 + Sweep * t;
        return Frame.ToWorld(Math.Cos(a) * Radius, Math.Sin(a) * Radius);
    }

    public Vector3d TangentAt(double t)
    {
        double a = A0 + Sweep * t;
        double s = Math.Sign(Sweep) == 0 ? 1 : Math.Sign(Sweep);
        return Frame.DirToWorld(new Vector2d(-Math.Sin(a) * s, Math.Cos(a) * s)).Normalized();
    }

    public double Length => Math.Abs(Sweep) * Radius;

    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty;
            int n = GeomUtil.ArcSegments(Radius, Sweep);
            for (int i = 0; i <= n; i++) b.Add(PointAt(i / (double)n));
            return b;
        }
    }

    public ICurve3 Reversed() => new ArcCurve3(Frame, Radius, A1, A0);

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        int n = GeomUtil.ArcSegments(Radius, Sweep);
        for (int i = includeFirstPoint ? 0 : 1; i <= n; i++)
            dst.Add(PointAt(i / (double)n));
    }
}

/// <summary>Эллипс/дуга эллипса: полуоси вдоль U и V фрейма.</summary>
public sealed class EllipseCurve3 : ICurve3
{
    public readonly Frame3 Frame;
    public readonly double RU, RV;
    public readonly double A0, A1;

    public EllipseCurve3(Frame3 frame, double ru, double rv, double a0, double a1)
    { Frame = frame; RU = ru; RV = rv; A0 = a0; A1 = a1; }

    public double T0 => 0.0;
    public double T1 => 1.0;
    public double Sweep => A1 - A0;
    public bool IsClosed => Math.Abs(Math.Abs(Sweep) - Math.PI * 2) < 1e-9;

    public Vector3d PointAt(double t)
    {
        double a = A0 + Sweep * t;
        return Frame.ToWorld(Math.Cos(a) * RU, Math.Sin(a) * RV);
    }

    public Vector3d TangentAt(double t)
    {
        double a = A0 + Sweep * t;
        double s = Math.Sign(Sweep) == 0 ? 1 : Math.Sign(Sweep);
        return Frame.DirToWorld(new Vector2d(-Math.Sin(a) * RU * s, Math.Cos(a) * RV * s)).Normalized();
    }

    public double Length
    {
        get
        {
            // численно, по тесселяции — точности достаточно для масс-свойств и UI
            var pts = new List<Vector3d>();
            Tessellate(pts, true);
            double L = 0;
            for (int i = 1; i < pts.Count; i++) L += (pts[i] - pts[i - 1]).Length;
            return L;
        }
    }

    public Bounds3 Bounds
    {
        get { var b = Bounds3.Empty; var p = new List<Vector3d>(); Tessellate(p, true); foreach (var v in p) b.Add(v); return b; }
    }

    public ICurve3 Reversed() => new EllipseCurve3(Frame, RU, RV, A1, A0);

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        int n = GeomUtil.ArcSegments(Math.Max(RU, RV), Sweep);
        for (int i = includeFirstPoint ? 0 : 1; i <= n; i++)
            dst.Add(PointAt(i / (double)n));
    }
}

/// <summary>Кубическая (или произвольной степени) кривая Безье по контрольным точкам.</summary>
public sealed class BezierCurve3 : ICurve3
{
    public readonly Vector3d[] P;
    public BezierCurve3(IEnumerable<Vector3d> ctrl) { P = ctrl.ToArray(); }

    public double T0 => 0.0;
    public double T1 => 1.0;
    public bool IsClosed => (P[0] - P[^1]).LengthSquared < Core.Tol.Weld * Core.Tol.Weld;

    public Vector3d PointAt(double t)
    {
        // де Кастельжо
        Span<Vector3d> tmp = P.Length <= 16 ? stackalloc Vector3d[P.Length] : new Vector3d[P.Length];
        for (int i = 0; i < P.Length; i++) tmp[i] = P[i];
        for (int k = P.Length - 1; k > 0; k--)
            for (int i = 0; i < k; i++)
                tmp[i] = tmp[i] * (1 - t) + tmp[i + 1] * t;
        return tmp[0];
    }

    public Vector3d TangentAt(double t)
    {
        const double h = 1e-6;
        double a = Math.Max(0, t - h), b = Math.Min(1, t + h);
        Vector3d d = PointAt(b) - PointAt(a);
        return d.LengthSquared < 1e-24 ? Vector3d.UnitX : d.Normalized();
    }

    public double Length
    {
        get { var p = new List<Vector3d>(); Tessellate(p, true); double L = 0; for (int i = 1; i < p.Count; i++) L += (p[i] - p[i - 1]).Length; return L; }
    }

    public Bounds3 Bounds { get { var b = Bounds3.Empty; foreach (var v in P) b.Add(v); return b; } }
    // Enumerable.Reverse, а не P.Reverse(): для массива вызов P.Reverse()
    // разрешается в MemoryExtensions.Reverse(Span<T>), который переворачивает
    // массив НА МЕСТЕ и возвращает void.
    public ICurve3 Reversed() => new BezierCurve3(Enumerable.Reverse(P));

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        // адаптивно: оцениваем длину контрольного полигона
        double poly = 0;
        for (int i = 1; i < P.Length; i++) poly += (P[i] - P[i - 1]).Length;
        int n = Math.Clamp((int)Math.Ceiling(poly / Math.Max(Core.Tol.Chord * 8, 1e-6)), 8, 256);
        for (int i = includeFirstPoint ? 0 : 1; i <= n; i++)
            dst.Add(PointAt(i / (double)n));
    }
}

/// <summary>Ломаная (результат импорта, проекций, спрямлённых кривых).</summary>
public sealed class PolylineCurve3 : ICurve3
{
    public readonly Vector3d[] Pts;
    private readonly double[] _acc;

    public PolylineCurve3(IEnumerable<Vector3d> pts)
    {
        Pts = pts.ToArray();
        _acc = new double[Pts.Length];
        for (int i = 1; i < Pts.Length; i++) _acc[i] = _acc[i - 1] + (Pts[i] - Pts[i - 1]).Length;
    }

    public double T0 => 0.0;
    public double T1 => 1.0;
    public bool IsClosed => (Pts[0] - Pts[^1]).LengthSquared < Core.Tol.Weld * Core.Tol.Weld;
    public double Length => _acc[^1];

    public Vector3d PointAt(double t)
    {
        double s = Math.Clamp(t, 0, 1) * Length;
        int i = Array.BinarySearch(_acc, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, Pts.Length - 2);
        double seg = _acc[i + 1] - _acc[i];
        double u = seg < 1e-12 ? 0 : (s - _acc[i]) / seg;
        return Pts[i] + (Pts[i + 1] - Pts[i]) * u;
    }

    public Vector3d TangentAt(double t)
    {
        double s = Math.Clamp(t, 0, 1) * Length;
        int i = Array.BinarySearch(_acc, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, Pts.Length - 2);
        return (Pts[i + 1] - Pts[i]).Normalized();
    }

    public Bounds3 Bounds { get { var b = Bounds3.Empty; foreach (var v in Pts) b.Add(v); return b; } }
    // См. примечание в BezierCurve3.Reversed: P.Reverse() для массива —
    // это переворот на месте с возвратом void, а нам нужна новая копия.
    public ICurve3 Reversed() => new PolylineCurve3(Enumerable.Reverse(Pts));

    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        for (int i = includeFirstPoint ? 0 : 1; i < Pts.Length; i++) dst.Add(Pts[i]);
    }
}
