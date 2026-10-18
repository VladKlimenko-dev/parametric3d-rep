using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// K08. ПОТОК ВЕКТОРНОГО ПОЛЯ ЧЕРЕЗ ГРАНИЦУ ТЕЛА — ПО НАСТОЯЩИМ КРИВЫМ РЁБЕР.
///
/// Для грани с параметризацией P(α, β) (α — «круговая» координата: угол у цилиндра и тела
/// вращения, первая координата у плоскости; β — вторая) поток ∫∫ F·n dA переводится Стоксом в
/// интеграл по краю:
///     ∫∫ g dα dβ = ∮ −G(α, β) dα,  g = F(P)·(P_α × P_β),  G(α, β) = ∫_{β₀}^{β} g(α, t) dt.
/// G периодична по α (g периодична), поэтому шов и обход оси поправок не требуют. Знак даёт
/// соглашение об обходе контуров (K05). Внутренний интеграл G — квадратура Гаусса (для
/// многочленов по β, как у плоскости и цилиндра, она точна); внешний — по точкам края.
///
/// Через поток выражаются массовые характеристики: объём — поток x/3, статический момент
/// ∫ xᵢ dV — поток (xᵢ²/2)·eᵢ. Поддержаны плоскость, круговой цилиндр и поверхность вращения
/// (в том числе сфера, конус, тор с краем); грань другого вида — null, вызывающий переходит на
/// сетку с её границей ошибки.
/// </summary>
public static class AnalyticFlux
{
    public const int Steps = 1024;
    /// <summary>m.q2: множитель числа кусков квадратуры (оценка ошибки — разность расчётов с 1 и 2).</summary>
    [ThreadStatic] public static int _pieceScale;
    public static int PieceScale { get => _pieceScale <= 0 ? 1 : _pieceScale; set => _pieceScale = value; }

    private sealed class Param
    {
        public Func<Vector3d, (double A, double B)> Coords = null!;
        public Func<double, double, Vector3d> Point = null!;
        public Func<double, double, Vector3d> Normal = null!;   // P_α × P_β
        public bool PeriodicA;
        public double B0;                                     // нижний предел внутреннего интеграла
    }

    /// <summary>Поток поля через все грани тела; null — есть неподдержанная грань.</summary>
    public static double? Flux(TSolid solid, Func<Vector3d, Vector3d> F)
    {
        if (solid.TopologyConvention < TopologyConventions.Version) return null;
        double sum = 0;
        foreach (var f in solid.Faces)
        {
            var p = ParamOf(f);
            if (p == null) return null;
            double v = FaceFlux(f, p, F);
            if (double.IsNaN(v)) return null;
            sum += v;
        }
        return sum;
    }

    /// <summary>Объём и центр масс тела по аналитической геометрии; null — не поддержано.</summary>
    public static (double Volume, Vector3d Centroid)? MassProperties(TSolid solid)
    {
        var v = Flux(solid, x => x / 3);
        if (v == null || Math.Abs(v.Value) < 1e-12) return null;
        var mx = Flux(solid, x => new Vector3d(x.X * x.X / 2, 0, 0));
        var my = Flux(solid, x => new Vector3d(0, x.Y * x.Y / 2, 0));
        var mz = Flux(solid, x => new Vector3d(0, 0, x.Z * x.Z / 2));
        if (mx == null || my == null || mz == null) return null;
        return (v.Value, new Vector3d(mx.Value, my.Value, mz.Value) / v.Value);
    }

    /// <summary>
    /// K09. МОМЕНТЫ ИНЕРЦИИ (плотность 1) относительно центра масс: тензор
    /// J = ∫ (|y|² I − y yᵀ) dV, y = x − x̄. Вторые моменты ∫ xᵢ xⱼ dV — потоки полей
    /// (xᵢ³/3)·eᵢ и (xᵢ² xⱼ / 2)·eᵢ; затем перенос к центру масс (теорема Штейнера).
    /// null — есть неподдержанная грань.
    /// </summary>
    public static (double Volume, Vector3d Centroid, Matrix3d Inertia)? InertiaAtCentroid(TSolid solid)
    {
        var mp = MassProperties(solid);
        if (mp == null) return null;
        var (V, cg) = mp.Value;
        double? Sxx = Flux(solid, x => new Vector3d(x.X * x.X * x.X / 3, 0, 0));
        double? Syy = Flux(solid, x => new Vector3d(0, x.Y * x.Y * x.Y / 3, 0));
        double? Szz = Flux(solid, x => new Vector3d(0, 0, x.Z * x.Z * x.Z / 3));
        double? Sxy = Flux(solid, x => new Vector3d(x.X * x.X * x.Y / 2, 0, 0));
        double? Syz = Flux(solid, x => new Vector3d(0, x.Y * x.Y * x.Z / 2, 0));
        double? Sxz = Flux(solid, x => new Vector3d(x.X * x.X * x.Z / 2, 0, 0));
        if (Sxx == null || Syy == null || Szz == null || Sxy == null || Syz == null || Sxz == null) return null;
        // Вторые моменты относительно центра масс.
        double xx = Sxx.Value - V * cg.X * cg.X, yy = Syy.Value - V * cg.Y * cg.Y, zz = Szz.Value - V * cg.Z * cg.Z;
        double xy = Sxy.Value - V * cg.X * cg.Y, yz = Syz.Value - V * cg.Y * cg.Z, xz = Sxz.Value - V * cg.X * cg.Z;
        var J = new Matrix3d(yy + zz, -xy, -xz,
                             -xy, xx + zz, -yz,
                             -xz, -yz, xx + yy);
        return (V, cg, J);
    }

    /// <summary>
    /// Главные моменты и оси симметричного тензора 3×3 (метод Якоби): моменты по возрастанию,
    /// оси — столбцы, правая тройка.
    /// </summary>
    public static (Vector3d Moments, Vector3d[] Axes) PrincipalAxes(Matrix3d J)
    {
        double[,] a = { { J.M11, J.M12, J.M13 }, { J.M21, J.M22, J.M23 }, { J.M31, J.M32, J.M33 } };
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            double scale = Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]);
            if (off <= 1e-15 * Math.Max(scale, 1e-300)) break;
            foreach (var (p0, q0) in new[] { (0, 1), (0, 2), (1, 2) })
            {
                if (Math.Abs(a[p0, q0]) < 1e-300) continue;
                double theta = (a[q0, q0] - a[p0, p0]) / (2 * a[p0, q0]);
                double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;
                for (int k = 0; k < 3; k++)
                {
                    double akp = a[k, p0], akq = a[k, q0];
                    a[k, p0] = c * akp - sn * akq; a[k, q0] = sn * akp + c * akq;
                }
                for (int k = 0; k < 3; k++)
                {
                    double apk = a[p0, k], aqk = a[q0, k];
                    a[p0, k] = c * apk - sn * aqk; a[q0, k] = sn * apk + c * aqk;
                }
                for (int k = 0; k < 3; k++)
                {
                    double vkp = v[k, p0], vkq = v[k, q0];
                    v[k, p0] = c * vkp - sn * vkq; v[k, q0] = sn * vkp + c * vkq;
                }
            }
        }
        var idx = new[] { 0, 1, 2 }.OrderBy(i => a[i, i]).ToArray();
        var axes = idx.Select(i => new Vector3d(v[0, i], v[1, i], v[2, i]).Normalized()).ToArray();
        if (Vector3d.Dot(Vector3d.Cross(axes[0], axes[1]), axes[2]) < 0) axes[2] = -axes[2];
        return (new Vector3d(a[idx[0], idx[0]], a[idx[1], idx[1]], a[idx[2], idx[2]]), axes);
    }

    // --------------------------------------------------------------------- параметризации

    private static Param? ParamOf(TFace f)
    {
        var s = GeometryAudit.AuthoritativeSurface(f);
        switch (s)
        {
            case PlaneSurface pl: return Plane(pl.F.Origin, pl.F.U, pl.F.V);
            case ExtrudedSurface exl when exl.Base is LineCurve3 ln:
            {
                var u = (ln.B - ln.A).Normalized();
                var n = Vector3d.Cross(u, exl.Dir.Normalized());
                if (n.LengthSquared < 1e-18) return null;
                return Plane(ln.A, u, Vector3d.Cross(n.Normalized(), u));
            }
            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc && ex.Dir.LengthSquared > 1e-18
                                      && Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), ex.Dir.Normalized())) > 1 - 1e-12:
                return Cylinder(arc.Frame.Origin, ex.Dir.Normalized(), arc.Frame.U, arc.Radius);
            case CylinderSurface cs:
                return Cylinder(cs.Axis.Origin, cs.Axis.W.Normalized(), cs.Axis.U, cs.Radius);
            case RevolvedSurface rv:
                return Revolved(f, rv);
            case NurbsSurface ns:
                return General(ns);
            default:
                return null;
        }
    }

    private static Param Plane(Vector3d p0, Vector3d u0, Vector3d v0)
    {
        var u = u0.Normalized(); var v = (v0 - u * Vector3d.Dot(v0, u)).Normalized(); var n = Vector3d.Cross(u, v);
        return new Param
        {
            Coords = x => (Vector3d.Dot(x - p0, u), Vector3d.Dot(x - p0, v)),
            Point = (a, b) => p0 + u * a + v * b,
            Normal = (a, b) => n,
            PeriodicA = false,
            B0 = 0
        };
    }

    private static Param Cylinder(Vector3d o, Vector3d w, Vector3d u0, double R)
    {
        var u = (u0 - w * Vector3d.Dot(u0, w)).Normalized(); var v = Vector3d.Cross(w, u);
        return new Param
        {
            Coords = x => { var d = x - o; return (Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)), Vector3d.Dot(d, w)); },
            Point = (t, z) => o + R * (Math.Cos(t) * u + Math.Sin(t) * v) + z * w,
            Normal = (t, z) => R * (Math.Cos(t) * u + Math.Sin(t) * v),       // P_θ × P_z = R r̂
            PeriodicA = true,
            B0 = 0
        };
    }

    /// <summary>
    /// Поверхность общего вида с параметрами (u, v) без периода (NURBS-лоскут): α = u, β = v,
    /// P_u × P_v — разностями. Точки края переводятся в параметры проекцией на поверхность.
    /// </summary>
    private static Param General(ISurface3 s)
    {
        const double h = 1e-6;
        return new Param
        {
            Coords = x => { var uv = s.ProjectToUV(x); return (uv.X, uv.Y); },
            Point = (u, v) => s.PointAt(new Vector2d(u, v)),
            Normal = (u, v) =>
            {
                if (s is NurbsSurface nsd && !NurbsSurface.DerivsOff) { nsd.Derivs(new Vector2d(u, v), out _, out var a1, out var b1); return Vector3d.Cross(a1, b1); }
                double u0 = Math.Max(0, u - h), u1 = Math.Min(1, u + h), v0 = Math.Max(0, v - h), v1 = Math.Min(1, v + h);
                var su = (s.PointAt(new Vector2d(u1, v)) - s.PointAt(new Vector2d(u0, v))) / (u1 - u0);
                var sv = (s.PointAt(new Vector2d(u, v1)) - s.PointAt(new Vector2d(u, v0))) / (v1 - v0);
                return Vector3d.Cross(su, sv);
            },
            PeriodicA = false,
            B0 = 0
        };
    }

    /// <summary>
    /// Поверхность вращения: P(θ, s) = o + z(s)w + ρ(s)r̂(θ), s — параметр образующей.
    /// P_θ × P_s = ρ(z′ r̂ − ρ′ w). Нижний предел — полюс образующей, если он на грани
    /// (тогда G там 0 и форма гладкая); грань с двумя полюсами (целая сфера) не поддержана.
    /// </summary>
    private static Param? Revolved(TFace f, RevolvedSurface rv)
    {
        if (f.Loops.Count == 0 || f.Loops.All(l => l.CoEdges.Count == 0)) return null;
        var o = rv.Axis.Origin; var w = rv.Axis.W.Normalized();
        var u = rv.Axis.U.Normalized(); var v = Vector3d.Cross(w, u);
        var c = rv.Base;
        (double Z, double R) Meridian(double t) { var d = c.PointAt(t) - o; double z = Vector3d.Dot(d, w); return (z, (d - w * z).Length); }
        bool pole0 = Meridian(c.T0).R < 1e-9, pole1 = Meridian(c.T1).R < 1e-9;
        double b0 = pole1 ? c.T1 : c.T0;
        if (pole0 && pole1)
        {
            // Образующая от полюса до полюса (сфера): нижний предел — полюс, ближайший к грани
            // (грань-шапка содержит его; дальний полюс на грань не попадает).
            var mean = Vector3d.Zero; double wsum = 0;
            if (f.Surface is FacetedSurface fsm)
                foreach (var (x, y, z) in fsm.Facets) { double a = Vector3d.Cross(y - x, z - x).Length; mean += (x + y + z) / 3 * a; wsum += a; }
            if (wsum <= 0) return null;
            mean /= wsum;
            b0 = (c.PointAt(c.T0) - mean).Length <= (c.PointAt(c.T1) - mean).Length ? c.T0 : c.T1;
        }
        bool closed = (c.PointAt(c.T0) - c.PointAt(c.T1)).Length < 1e-9;
        if (closed) return null;   // тор: образующая замкнута, нужен другой разрез (см. AnalyticVolume)
        double h = 1e-6 * (c.T1 - c.T0);
        return new Param
        {
            Coords = x => { var d = x - o; return (Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)), rv.ProjectToUV(x).X); },
            Point = (t, s) => { var (z, r) = Meridian(s); return o + w * z + r * (Math.Cos(t) * u + Math.Sin(t) * v); },
            Normal = (t, s) =>
            {
                var (z, r) = Meridian(s);
                double s1 = Math.Min(c.T1, s + h), s0 = Math.Max(c.T0, s - h);
                var (za, ra) = Meridian(s0); var (zb, rb) = Meridian(s1);
                double dz = (zb - za) / (s1 - s0), dr = (rb - ra) / (s1 - s0);
                var rh = Math.Cos(t) * u + Math.Sin(t) * v;
                return r * (dz * rh - dr * w);
            },
            PeriodicA = true,
            B0 = b0
        };
    }

    // --------------------------------------------------------------------- интеграл по краю

    private static readonly double[] GX = { -0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498,
                                             0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363 };
    private static readonly double[] GW = { 0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620,
                                            0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763 };

    /// <summary>G(α, β) = ∫_{β₀}^{β} g(α, t) dt — составная квадратура Гаусса, 8 узлов на кусок.</summary>
    private static double G(Param p, Func<Vector3d, Vector3d> F, double a, double b) =>
        GI(p, (aa, t) => Vector3d.Dot(F(p.Point(aa, t)), p.Normal(aa, t)), a, b);

    private static double GI(Param p, Func<double, double, double> g, double a, double b)
    {
        double lo = p.B0, hi = b;
        if (hi == lo) return 0;
        int pieces = 4 * PieceScale;
        double sum = 0, step = (hi - lo) / pieces;
        for (int k = 0; k < pieces; k++)
        {
            double m = lo + step * (k + 0.5), r = step / 2;
            for (int i = 0; i < GX.Length; i++)
            {
                double t = m + r * GX[i];
                sum += GW[i] * r * g(a, t);
            }
        }
        return sum;
    }

    /// <summary>KRN-07 N1b: поток поля через одну грань (общая параметризация — для граней NURBS в AnalyticVolume); null — не поддержано.</summary>
    internal static double? FaceFluxOf(TFace f, Func<Vector3d, Vector3d> F)
    {
        var p = ParamOf(f); if (p == null) return null;
        double v = FaceFlux(f, p, F); return double.IsNaN(v) ? null : v;
    }

    /// <summary>KRN-07 N1b: площадь грани с общей параметризацией (NURBS) — тот же интеграл по краю с |P_α × P_β|; null — нет.</summary>
    internal static double? FaceAreaOf(TFace f)
    {
        var p = ParamOf(f); if (p == null) return null;
        double v = FaceIntegral(f, p, (a, t) => p.Normal(a, t).Length); return double.IsNaN(v) ? null : Math.Abs(v);
    }

    static readonly bool BreaksOff = Environment.GetEnvironmentVariable("P3D_FLUXBREAKS") == "0";
    static readonly bool StencilOff = Environment.GetEnvironmentVariable("P3D_FLUXSTENCIL") == "0";

    private static double FaceFlux(TFace f, Param p, Func<Vector3d, Vector3d> F) =>
        FaceIntegral(f, p, (a, t) => Vector3d.Dot(F(p.Point(a, t)), p.Normal(a, t)));

    private static double FaceIntegral(TFace f, Param p, Func<double, double, double> g)
    {
        // Интеграл по краю ∮ −G dα — квадратурой Гаусса по параметру каждой кривой: у отрезка
        // плоскости подынтегральное — многочлен, и 8 узлов дают его точно; у кривых — 16 кусков.
        double sum = 0;
        foreach (var loop in f.Loops)
            foreach (var ce in loop.CoEdges)
            {
                var e = ce.Edge; var c = e.Curve;
                bool curveForward = (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared || ReferenceEquals(e.V0, e.V1);
                bool forward = ce.Reversed ? !curveForward : curveForward;
                int pieces = c is LineCurve3 ? PieceScale : 16 * PieceScale;
                double t0 = forward ? c.T0 : c.T1, t1 = forward ? c.T1 : c.T0;
                double span = (t1 - t0) / pieces, eps = 1e-7 * Math.Abs(c.T1 - c.T0);
                // KRN-07 N1b: кривая с изломами параметризации (точки затравки кривой пересечения) — куски между изломами
                // (квадратура Гаусса через излом теряет порядок: объём среза грани NURBS отходил на 2e-5 отн.)
                var cuts = new List<double>();
                if (!BreaksOff && c is Geometry.NurbsImplicitCurve3 nic) { cuts.Add(c.T0); foreach (var b in nic.Breaks) cuts.Add(c.T0 + (c.T1 - c.T0) * b); cuts.Add(c.T1); }
                else if (!BreaksOff && c is Geometry.NurbsNurbsCurve3 nnc) { cuts.Add(c.T0); foreach (var b in nnc.Breaks) cuts.Add(c.T0 + (c.T1 - c.T0) * b); cuts.Add(c.T1); }
                else for (int k = 0; k <= pieces; k++) cuts.Add(c.T0 + (c.T1 - c.T0) * k / pieces);
                if (PieceScale > 1 && (c is Geometry.NurbsImplicitCurve3 || c is Geometry.NurbsNurbsCurve3) && !BreaksOff)
                {
                    var fine = new List<double> { cuts[0] };
                    for (int k = 0; k + 1 < cuts.Count; k++) for (int j = 1; j <= PieceScale; j++) fine.Add(cuts[k] + (cuts[k + 1] - cuts[k]) * j / PieceScale);
                    cuts = fine;
                }
                if (!forward) cuts.Reverse();
                double minPiece = Enumerable.Range(0, cuts.Count - 1).Min(k => Math.Abs(cuts[k + 1] - cuts[k]));
                eps = Math.Min(eps, 1e-3 * minPiece);
                // KRN-06 (06.14): производная координаты по параметру кривой — разностью 4-го порядка с шагом 0.004 куска (узлы
                // Гаусса отстоят от концов на 0.02 куска — шаблон не выходит за кусок). Прежде — центральная разность с шагом
                // 1e-7 длины кривой: шум проекции (1e-12) делился на шаг, у края грани NURBS процедурной кривой ошибка объёма
                // была 2e-3 мм³ на теле 1.2e5. Мутация P3D_FLUXSTENCIL=0.
                double h4 = StencilOff ? 0 : 0.004 * minPiece;
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    double m = (cuts[k] + cuts[k + 1]) / 2, r = (cuts[k + 1] - cuts[k]) / 2;
                    for (int i = 0; i < GX.Length; i++)
                    {
                        double t = m + r * GX[i];
                        var (a, bq) = p.Coords(c.PointAt(t));
                        double Wrap(double d) { if (p.PeriodicA) { while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; } return d; }
                        double dadt;
                        if (h4 > 0)
                        {
                            double A(double tt) => Wrap(p.Coords(c.PointAt(tt)).A - a);
                            dadt = (-A(t + 2 * h4) + 8 * A(t + h4) - 8 * A(t - h4) + A(t - 2 * h4)) / (12 * h4);
                        }
                        else
                        {
                            var (aPlus, _) = p.Coords(c.PointAt(t + eps));
                            var (aMinus, _) = p.Coords(c.PointAt(t - eps));
                            dadt = Wrap(aPlus - aMinus) / (2 * eps);
                        }
                        sum += GW[i] * r * (-GI(p, g, a, bq)) * dadt;
                    }
                }
            }
        return sum;
    }
}
