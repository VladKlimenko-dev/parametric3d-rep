using OpenTK.Mathematics;
using Parametric3D.Kernel.Analytic;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Query;

/// <summary>
/// ЗАПРОСЫ К ТЕЛУ ПО ТОЧНОЙ ГЕОМЕТРИИ (матрица m.q3): ближайшая точка и расстояние, точный габарит.
/// <para>
/// Ближайшая точка тела к точке p лежит либо внутри грани (ортогональная проекция на поверхность — замкнутой формулой для
/// плоскости, цилиндра, конуса, шара, тора — и проверка «точка внутри грани»), либо на ребре (ближайшие точки кривой), либо
/// в вершине: минимум по этим кандидатам точен. Грань без замкнутой проекции (сплайн) — отказ (null), не догадка.
/// </para>
/// <para>
/// Габарит по направлению d (опорная функция) достигается в вершине, во внутренней точке ребра, где касательная ⟂ d, или
/// во внутренней точке грани, где нормаль ∥ d (у шара — точка, у тора — точка или окружность, у цилиндра и конуса —
/// образующая при d ⟂ оси); кандидаты граней проверяются принадлежностью грани. Габарит по ±X, ±Y, ±Z.
/// </para>
/// </summary>
public static partial class BodyQuery
{
    public readonly record struct ClosestResult(double Distance, Vector3d Point, string Where);

    /// <summary>Ближайшая точка тела (его границы) к p; null — у тела есть грань без точной проекции.</summary>
    public static ClosestResult? Closest(TSolid body, Vector3d p)
    {
        double best = double.MaxValue; Vector3d bp = default; string where = "";
        void Try(Vector3d q, string w) { double d = (q - p).Length; if (d < best) { best = d; bp = q; where = w; } }
        foreach (var v in body.Vertices) Try(v.P, "vertex");
        foreach (var e in body.Edges)
        {
            var c = e.Curve;
            switch (c)
            {
                case LineCurve3 ln:
                {
                    var ab = ln.B - ln.A; double t = Math.Clamp(Vector3d.Dot(p - ln.A, ab) / Math.Max(1e-300, ab.LengthSquared), 0, 1);
                    Try(ln.A + ab * t, "edge"); break;
                }
                case ArcCurve3 a:
                {
                    // проекция на плоскость окружности, угол — в пределах дуги, иначе концы
                    var u = a.Frame.U.Normalized(); var w = a.Frame.W.Normalized(); var vv = Vector3d.Cross(w, u);
                    var d = p - a.Frame.Origin; double th = Math.Atan2(Vector3d.Dot(d, vv), Vector3d.Dot(d, u));
                    double tau = (th - a.A0) / a.Sweep, per = 2 * Math.PI / Math.Abs(a.Sweep);
                    while (tau < 0) tau += per; while (tau > 1) tau -= per;
                    if (tau >= 0 && tau <= 1) Try(a.PointAt(tau), "edge");
                    Try(a.PointAt(0), "edge"); Try(a.PointAt(1), "edge");
                    break;
                }
                default:
                    Try(c.PointAt(CurveParam.Closest(c, p)), "edge"); Try(c.PointAt(c.T0), "edge"); Try(c.PointAt(c.T1), "edge");
                    break;
            }
        }
        foreach (var f in body.Faces)
        {
            var af = AnalyticFace.Of(f, out _);
            if (af == null) return null;
            // m.q3: грань NURBS — подножие перпендикуляра Ньютоном из ближайших треугольников грани
            if (af.Param) { if (NurbsFoot(af, p) is { } qn) Try(qn, "face"); continue; }
            if (af.Shape is not (Ssi.Plane or Ssi.Cylinder or Ssi.Sphere or Ssi.Cone or Ssi.Torus)) return null;
            // точка на оси (центре): ближайших точек — окружность (сфера целиком); берутся 16 направлений, годна любая
            // из них внутри грани (расстояние у всех одно)
            foreach (var dir in Dirs(af.Shape, p))
            {
                Vector3d? q = af.Shape switch
                {
                    Ssi.Plane pl => p - pl.N.Normalized() * Vector3d.Dot(p - pl.P, pl.N.Normalized()),
                    Ssi.Cylinder cy => OnCylinder(cy, p, dir),
                    Ssi.Sphere sp => sp.C + ((p - sp.C).Length < 1e-15 ? dir : (p - sp.C).Normalized()) * sp.R,
                    Ssi.Cone k => OnCone(k, p, dir),
                    Ssi.Torus t => OnTorus(t, p, dir),
                    _ => null,
                };
                if (q is { } qq && af.Contains(qq) == true) { Try(qq, "face"); break; }
            }
        }
        return best < double.MaxValue ? new ClosestResult(best, bp, where) : null;
    }

    /// <summary>Направления ⟂ оси (или любые — для шара) на случай точки на оси: 16 штук; иначе одно (не используется).</summary>
    static IEnumerable<Vector3d> Dirs(Ssi.Shape sh, Vector3d p)
    {
        Vector3d w = sh switch { Ssi.Cylinder c => c.Axis, Ssi.Cone k => k.Axis, Ssi.Torus t => t.Axis, _ => Vector3d.UnitZ };
        w.Normalize();
        var u = Math.Abs(w.X) < 0.9 ? Vector3d.Cross(w, Vector3d.UnitX).Normalized() : Vector3d.Cross(w, Vector3d.UnitY).Normalized(); var v = Vector3d.Cross(w, u);
        for (int i = 0; i < 16; i++) { double a = 2 * Math.PI * i / 16; yield return u * Math.Cos(a) + v * Math.Sin(a); }
        if (sh is Ssi.Sphere) { yield return w; yield return -w; }
    }

    static Vector3d Radial(Vector3d rad, Vector3d dir) => rad.Length < 1e-15 ? dir : rad.Normalized();

    static Vector3d? OnCylinder(Ssi.Cylinder cy, Vector3d p, Vector3d dir)
    {
        var w = cy.Axis.Normalized(); var d = p - cy.P; var rad = d - w * Vector3d.Dot(d, w);
        return cy.P + w * Vector3d.Dot(d, w) + Radial(rad, dir) * cy.R;
    }

    static Vector3d? OnCone(Ssi.Cone k, Vector3d p, Vector3d dir)
    {
        // в меридиане через p: ближайшая точка луча-образующей от вершины
        var w = k.Axis.Normalized(); var d = p - k.Apex; double z = Vector3d.Dot(d, w); var rad = d - w * z;
        var g = (w * Math.Cos(k.HalfAngle) + Radial(rad, dir) * Math.Sin(k.HalfAngle)).Normalized();
        double s = Math.Max(0, Vector3d.Dot(d, g));
        return k.Apex + g * s;
    }

    static Vector3d? OnTorus(Ssi.Torus t, Vector3d p, Vector3d dir)
    {
        var w = t.Axis.Normalized(); var d = p - t.C; double z = Vector3d.Dot(d, w); var rad = d - w * z;
        var c = t.C + Radial(rad, dir) * t.R;               // центр образующей окружности в меридиане p
        var e = p - c; if (e.Length < 1e-15) return null;
        return c + e.Normalized() * t.r;
    }

    /// <summary>Опорная функция тела по направлению d: max d·x по телу; null — грань без точной формы.</summary>
    public static double? Support(TSolid body, Vector3d d)
    {
        d = d.Normalized();
        double best = double.MinValue;
        void Try(Vector3d q) { double s = Vector3d.Dot(q, d); if (s > best) best = s; }
        foreach (var v in body.Vertices) Try(v.P);
        foreach (var e in body.Edges)
        {
            switch (e.Curve)
            {
                case LineCurve3: break;                         // концы — вершины
                case ArcCurve3 a:
                {
                    var u = a.Frame.U.Normalized(); var vv = Vector3d.Cross(a.Frame.W.Normalized(), u);
                    double th = Math.Atan2(Vector3d.Dot(d, vv), Vector3d.Dot(d, u));
                    double tau = (th - a.A0) / a.Sweep, per = 2 * Math.PI / Math.Abs(a.Sweep);
                    while (tau < 0) tau += per; while (tau > 1) tau -= per;
                    if (tau >= 0 && tau <= 1) Try(a.PointAt(tau));
                    Try(a.PointAt(0)); Try(a.PointAt(1));
                    break;
                }
                case EllipseCurve3 el:
                {
                    var u = el.Frame.U.Normalized(); var vv = el.Frame.V.Normalized();
                    double th = Math.Atan2(el.RV * Vector3d.Dot(d, vv), el.RU * Vector3d.Dot(d, u));
                    double tau = (th - el.A0) / el.Sweep, per = 2 * Math.PI / Math.Abs(el.Sweep);
                    while (tau < 0) tau += per; while (tau > 1) tau -= per;
                    if (tau >= 0 && tau <= 1) Try(el.PointAt(tau));
                    Try(el.PointAt(0)); Try(el.PointAt(1));
                    break;
                }
                default:
                {
                    // m.q3: общая кривая (сплайн, кривая пересечения) — максимум d·c(t): выборка 128 и золотое сечение у пиков
                    var c = e.Curve; double T0 = c.T0, T1 = c.T1; const int n = 128;
                    double F(double t) => Vector3d.Dot(c.PointAt(t), d);
                    var vals = Enumerable.Range(0, n + 1).Select(i => F(T0 + (T1 - T0) * i / n)).ToArray();
                    for (int i = 0; i <= n; i++)
                    {
                        if ((i > 0 && vals[i - 1] > vals[i]) || (i < n && vals[i + 1] > vals[i])) continue;
                        double lo = T0 + (T1 - T0) * Math.Max(0, i - 1) / n, hi = T0 + (T1 - T0) * Math.Min(n, i + 1) / n;
                        const double g = 0.6180339887498949;
                        double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = F(x1), f2 = F(x2);
                        for (int it = 0; it < 90; it++)
                        {
                            if (f1 > f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = F(x1); }
                            else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = F(x2); }
                        }
                        Try(c.PointAt(f1 > f2 ? x1 : x2));
                    }
                    Try(c.PointAt(T0)); Try(c.PointAt(T1));
                    break;
                }
            }
        }
        foreach (var f in body.Faces)
        {
            var af = AnalyticFace.Of(f, out _);
            if (af == null) return null;
            // m.q3: грань NURBS — внутренний максимум d·S(u, v) подъёмом по параметрам из лучших вершин треугольников
            if (af.Param) { foreach (var q in NurbsSupportCandidates(af, d)) if (af.Contains(q) == true) Try(q); continue; }
            var cands = new List<Vector3d>();
            switch (af.Shape)
            {
                case Ssi.Plane: break;                          // линейная функция — максимум на границе
                case Ssi.Sphere sp: cands.Add(sp.C + d * sp.R); break;
                case Ssi.Cylinder cy:
                {
                    var w = cy.Axis.Normalized(); var dp = d - w * Vector3d.Dot(d, w);
                    if (Math.Abs(Vector3d.Dot(d, w)) < 1e-12 && dp.Length > 1e-12)
                        foreach (var h in Heights(f, cy.P, w)) cands.Add(cy.P + w * h + dp.Normalized() * cy.R);
                    break;
                }
                case Ssi.Cone k:
                {
                    // максимум внутри грани — только если d ⟂ образующей (функция вдоль образующей постоянна)
                    var w = k.Axis.Normalized(); var dp = d - w * Vector3d.Dot(d, w); if (dp.Length < 1e-12) break;
                    var g = (w * Math.Cos(k.HalfAngle) + dp.Normalized() * Math.Sin(k.HalfAngle)).Normalized();
                    if (Math.Abs(Vector3d.Dot(d, g)) < 1e-12)
                        foreach (var h in Heights(f, k.Apex, w)) if (h > 0) cands.Add(k.Apex + g * (h / Math.Cos(k.HalfAngle)));
                    break;
                }
                case Ssi.Torus t:
                {
                    var w = t.Axis.Normalized(); double dw = Vector3d.Dot(d, w); var dp = d - w * dw;
                    if (dp.Length < 1e-12)
                    {
                        // d по оси: максимум — окружность z = r на радиусе R; проверяется по точкам окружности
                        var u = Math.Abs(w.X) < 0.9 ? Vector3d.Cross(w, Vector3d.UnitX).Normalized() : Vector3d.Cross(w, Vector3d.UnitY).Normalized(); var v2 = Vector3d.Cross(w, u);
                        for (int i = 0; i < 64; i++) { double a = 2 * Math.PI * i / 64; cands.Add(t.C + (u * Math.Cos(a) + v2 * Math.Sin(a)) * t.R + w * (t.r * Math.Sign(dw))); }
                    }
                    else
                    {
                        var e = dp.Normalized(); double phi = Math.Atan2(dw, dp.Length);
                        cands.Add(t.C + e * (t.R + t.r * Math.Cos(phi)) + w * (t.r * Math.Sin(phi)));
                    }
                    break;
                }
                default: return null;
            }
            foreach (var q in cands) if (af.Contains(q) == true) Try(q);
        }
        return best;
    }

    /// <summary>Высоты вдоль оси, на которых грань имеет точки (по точкам рёбер): середина и 15 промежуточных — кандидаты
    /// для образующей при d ⟂ оси (значение опорной функции там одно — достаточно одной точки внутри грани).</summary>
    static IEnumerable<double> Heights(TFace f, Vector3d o, Vector3d w)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var e in f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge))
            for (int k = 0; k <= 8; k++) { double h = Vector3d.Dot(e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 8.0) - o, w); lo = Math.Min(lo, h); hi = Math.Max(hi, h); }
        if (lo > hi) yield break;
        for (int k = 1; k < 16; k++) yield return lo + (hi - lo) * k / 16.0;
    }

    /// <summary>Точный габарит тела (по опорной функции вдоль ±X, ±Y, ±Z); null — нет точной формы.</summary>
    public static Bounds3? ExactBounds(TSolid body)
    {
        var mx = new double[6]; var dirs = new[] { Vector3d.UnitX, -Vector3d.UnitX, Vector3d.UnitY, -Vector3d.UnitY, Vector3d.UnitZ, -Vector3d.UnitZ };
        for (int i = 0; i < 6; i++) { if (Support(body, dirs[i]) is not { } s) return null; mx[i] = s; }
        var b = Bounds3.Empty; b.Add(new Vector3d(-mx[1], -mx[3], -mx[5])); b.Add(new Vector3d(mx[0], mx[2], mx[4]));
        return b;
    }
}
