using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// 04.2: СТРОГИЕ СОБЫТИЯ «РЕБРО ∩ ПОВЕРХНОСТЬ» для рёбер и поверхностей без алгебраического решения — отделение корней по
/// Кравчику (Geometry.Krawczyk) на кусках Безье (кривых и поверхностей) со строгими интервалами производных:
///   кривая (отрезок, дуга, эллипс, сплайн, Безье) × неявная поверхность — по s куска (1 неизвестная);
///   кривая × поверхность NURBS — (s, u, v);
///   поверхность NURBS × две неявные — (u, v) — рёбра «NURBS ∩ неявная» с третьей неявной и рёбра «неявная ∩ неявная»
///   (кривая пересечения) с поверхностью NURBS;
///   две NURBS × неявная на первой — (u₁, v₁, u₂, v₂) — рёбра «NURBS ∩ NURBS» с неявной и рёбра «NURBS ∩ неявная» с NURBS.
/// Ответ — точки; null — не доказано (касание, кратный корень, предел ящиков).
/// Куски берутся с перекрытием (продолжение многочлена за край): корень на общей стороне кусков находится, корни вне
/// своего куска отбрасываются.
/// </summary>
public static class RigorousEvents
{
    const double Ext = 1e-6;          // перекрытие кусков (доля их ширины)

    static (double, double) Grow(double a, double b) { double e = (b - a) * Ext; return (a - e, b + e); }

    // ---------------------------------------------------------------- кривая × неявная

    public static List<Vector3d>? CurveImplicit(ICurve3 c, Ssi.Shape s)
    {
        var pieces = RBezierCurve.Of(c); if (pieces == null) return null;
        var res = new List<Vector3d>();
        foreach (var pc in pieces)
        {
            var (a0, a1) = Grow(0, 1);
            var k = new Krawczyk
            {
                N = 1,
                F = x => new[] { Ssi.Implicit(s, pc.Point(x[0])).F },
                J = (lo, hi) =>
                {
                    var sub = pc.Sub(lo[0], hi[0]);
                    if (sub.DerivBounds() is not { } db) return null;
                    var c3 = (db.Lo + db.Hi) / 2; double rb = (db.Hi - db.Lo).Length / 2;
                    if (Ssi.NormalLip(s, c3, rb) is not double kap) return null;
                    var g0 = Ssi.Implicit(s, c3).G; double del = Math.Min(2, kap * rb);
                    if (sub.DirBound(g0) is not { } d) return null;
                    double cm = Mag(db.DLo, db.DHi);
                    return new[,] { { new Iv(d.Lo - del * cm, d.Hi + del * cm) } };
                },
                Excl = (lo, hi) =>
                {
                    var (bl, bh) = pc.Sub(lo[0], hi[0]).Box(); var c3 = (bl + bh) / 2; double rb = (bh - bl).Length / 2;
                    return Math.Abs(Ssi.Implicit(s, c3).F) > rb * (1 + 1e-12) + 1e-13;
                },
                Scale = new[] { Math.Max(1e-9, (pc.Box().Hi - pc.Box().Lo).Length) },
            };
            var roots = k.Solve(new[] { (new[] { a0 }, new[] { a1 }) });
            if (roots == null) return null;
            foreach (var r in roots) if (r[0] >= -1e-12 && r[0] <= 1 + 1e-12) res.Add(pc.Point(Math.Clamp(r[0], 0, 1)));
        }
        return res;
    }

    static double Mag(Vector3d lo, Vector3d hi)
    {
        double a = 0; for (int m = 0; m < 3; m++) { double x = Math.Max(Math.Abs(lo[m]), Math.Abs(hi[m])); a += x * x; }
        return Math.Sqrt(a);
    }

    static Iv Comp(Vector3d lo, Vector3d hi, int m) => new(lo[m], hi[m]);

    /// <summary>Куски Безье поверхности, задевающие прямоугольник, — с перекрытием, обрезанные по нему (тоже с запасом).</summary>
    static List<(BezierPatch P, double U0, double U1, double V0, double V1)> PatchesIn(List<BezierPatch> bez, double ua, double ub, double va, double vb)
    {
        var res = new List<(BezierPatch, double, double, double, double)>();
        var (gua, gub) = Grow(ua, ub); var (gva, gvb) = Grow(va, vb);
        foreach (var bp in bez)
        {
            var (pu0, pu1) = Grow(bp.U0, bp.U1); var (pv0, pv1) = Grow(bp.V0, bp.V1);
            double u0 = Math.Max(pu0, gua), u1 = Math.Min(pu1, gub), v0 = Math.Max(pv0, gva), v1 = Math.Min(pv1, gvb);
            if (u0 < u1 && v0 < v1) res.Add((bp, u0, u1, v0, v1));
        }
        return res;
    }

    static bool InPatch(BezierPatch bp, double u, double v) =>
        u >= bp.U0 - 1e-12 * (bp.U1 - bp.U0) && u <= bp.U1 + 1e-12 * (bp.U1 - bp.U0) && v >= bp.V0 - 1e-12 * (bp.V1 - bp.V0) && v <= bp.V1 + 1e-12 * (bp.V1 - bp.V0);

    // ---------------------------------------------------------------- кривая × NURBS

    public static List<Vector3d>? CurveNurbs(ICurve3 c, NurbsSurface S, double ua, double ub, double va, double vb)
    {
        var pieces = RBezierCurve.Of(c); if (pieces == null) return null;
        var patches = PatchesIn(NurbsTools.Beziers(S), ua, ub, va, vb);
        var res = new List<Vector3d>();
        foreach (var pc in pieces)
        {
            var (cl, ch) = pc.Box();
            foreach (var (bp, u0, u1, v0, v1) in patches)
            {
                var (pl, ph) = bp.Box();
                if (cl.X > ph.X || pl.X > ch.X || cl.Y > ph.Y || pl.Y > ch.Y || cl.Z > ph.Z || pl.Z > ch.Z) continue;
                double su = Math.Max(1e-9, (bp.Eval((u0 + u1) / 2 + 1e-6 * (u1 - u0), (v0 + v1) / 2) - bp.Eval((u0 + u1) / 2, (v0 + v1) / 2)).Length / (1e-6 * (u1 - u0)));
                double sv = Math.Max(1e-9, (bp.Eval((u0 + u1) / 2, (v0 + v1) / 2 + 1e-6 * (v1 - v0)) - bp.Eval((u0 + u1) / 2, (v0 + v1) / 2)).Length / (1e-6 * (v1 - v0)));
                var k = new Krawczyk
                {
                    N = 3,
                    F = x => { var d = pc.Point(x[0]) - bp.Eval(x[1], x[2]); return new[] { d.X, d.Y, d.Z }; },
                    J = (lo, hi) =>
                    {
                        if (pc.Sub(lo[0], hi[0]).DerivBounds() is not { } dc) return null;
                        if (bp.Sub(lo[1], hi[1], lo[2], hi[2]).DerivBounds() is not { } ds) return null;
                        var J = new Iv[3, 3];
                        for (int m = 0; m < 3; m++) { J[m, 0] = Comp(dc.DLo, dc.DHi, m); J[m, 1] = -Comp(ds.SuLo, ds.SuHi, m); J[m, 2] = -Comp(ds.SvLo, ds.SvHi, m); }
                        return J;
                    },
                    Excl = (lo, hi) =>
                    {
                        var (a, b) = pc.Sub(lo[0], hi[0]).Box(); var (p, q) = bp.Sub(lo[1], hi[1], lo[2], hi[2]).Box();
                        return a.X > q.X || p.X > b.X || a.Y > q.Y || p.Y > b.Y || a.Z > q.Z || p.Z > b.Z;
                    },
                    Scale = new[] { Math.Max(1e-9, (ch - cl).Length), su, sv },
                };
                var (s0, s1) = Grow(0, 1);
                var roots = k.Solve(new[] { (new[] { s0, u0, v0 }, new[] { s1, u1, v1 }) });
                if (roots == null) return null;
                foreach (var r in roots)
                    if (r[0] >= -1e-12 && r[0] <= 1 + 1e-12 && InPatch(bp, r[1], r[2]) && r[1] >= ua - 1e-12 && r[1] <= ub + 1e-12 && r[2] >= va - 1e-12 && r[2] <= vb + 1e-12)
                        res.Add(pc.Point(Math.Clamp(r[0], 0, 1)));
            }
        }
        return Dedupe(res);
    }

    static List<Vector3d> Dedupe(List<Vector3d> pts)
    {
        var o = new List<Vector3d>();
        foreach (var p in pts) if (!o.Any(q => (q - p).Length < 1e-8)) o.Add(p);
        return o;
    }

    // ---------------------------------------------------------------- NURBS × две неявные

    public static List<Vector3d>? SurfaceTwoImplicit(NurbsSurface S, double ua, double ub, double va, double vb, Ssi.Shape o1, Ssi.Shape o2)
    {
        var res = new List<Vector3d>();
        foreach (var (bp, u0, u1, v0, v1) in PatchesIn(NurbsTools.Beziers(S), ua, ub, va, vb))
        {
            var (pl, ph) = bp.Box(); var pc = (pl + ph) / 2; double pr = (ph - pl).Length / 2;
            if (Math.Abs(Ssi.Implicit(o1, pc).F) > pr || Math.Abs(Ssi.Implicit(o2, pc).F) > pr) continue;
            double su = Math.Max(1e-9, (bp.Eval(u1, (v0 + v1) / 2) - bp.Eval(u0, (v0 + v1) / 2)).Length / (u1 - u0));
            double sv = Math.Max(1e-9, (bp.Eval((u0 + u1) / 2, v1) - bp.Eval((u0 + u1) / 2, v0)).Length / (v1 - v0));
            var k = new Krawczyk
            {
                N = 2,
                F = x => { var p = bp.Eval(x[0], x[1]); return new[] { Ssi.Implicit(o1, p).F, Ssi.Implicit(o2, p).F }; },
                J = (lo, hi) =>
                {
                    var sub = bp.Sub(lo[0], hi[0], lo[1], hi[1]);
                    if (sub.DerivBounds() is not { } db) return null;
                    var c3 = (db.Lo + db.Hi) / 2; double rb = (db.Hi - db.Lo).Length / 2;
                    var J = new Iv[2, 2]; int row = 0;
                    foreach (var o in new[] { o1, o2 })
                    {
                        if (Ssi.NormalLip(o, c3, rb) is not double kap) return null;
                        var g0 = Ssi.Implicit(o, c3).G; double del = Math.Min(2, kap * rb);
                        if (sub.DirBound(g0, new Vector2d(1, 0)) is not { } du || sub.DirBound(g0, new Vector2d(0, 1)) is not { } dv) return null;
                        double mu = Mag(db.SuLo, db.SuHi) * del, mv = Mag(db.SvLo, db.SvHi) * del;
                        J[row, 0] = new Iv(du.Lo - mu, du.Hi + mu); J[row, 1] = new Iv(dv.Lo - mv, dv.Hi + mv); row++;
                    }
                    return J;
                },
                Excl = (lo, hi) =>
                {
                    var (a, b) = bp.Sub(lo[0], hi[0], lo[1], hi[1]).Box(); var c3 = (a + b) / 2; double rb = (b - a).Length / 2 * (1 + 1e-12) + 1e-13;
                    return Math.Abs(Ssi.Implicit(o1, c3).F) > rb || Math.Abs(Ssi.Implicit(o2, c3).F) > rb;
                },
                Scale = new[] { su, sv },
            };
            var roots = k.Solve(new[] { (new[] { u0, v0 }, new[] { u1, v1 }) });
            if (roots == null) return null;
            foreach (var r in roots)
                if (InPatch(bp, r[0], r[1]) && r[0] >= ua - 1e-12 && r[0] <= ub + 1e-12 && r[1] >= va - 1e-12 && r[1] <= vb + 1e-12)
                    res.Add(bp.Eval(r[0], r[1]));
        }
        return Dedupe(res);
    }

    // ---------------------------------------------------------------- две NURBS × неявная на первой

    public static List<Vector3d>? TwoSurfacesImplicit(NurbsSurface A, (double U0, double U1, double V0, double V1) ra, NurbsSurface B, (double U0, double U1, double V0, double V1) rb, Ssi.Shape o)
    {
        var pa = PatchesIn(NurbsTools.Beziers(A), ra.U0, ra.U1, ra.V0, ra.V1);
        var pb = PatchesIn(NurbsTools.Beziers(B), rb.U0, rb.U1, rb.V0, rb.V1);
        var res = new List<Vector3d>();
        foreach (var (P, a0, a1, b0, b1) in pa)
        {
            var (l1, h1) = P.Sub(a0, a1, b0, b1).Box(); var c1 = (l1 + h1) / 2;
            if (Math.Abs(Ssi.Implicit(o, c1).F) > (h1 - l1).Length / 2) continue;
            foreach (var (Q, c0, cc1, d0, d1) in pb)
            {
                var (l2, h2) = Q.Sub(c0, cc1, d0, d1).Box();
                if (l1.X > h2.X || l2.X > h1.X || l1.Y > h2.Y || l2.Y > h1.Y || l1.Z > h2.Z || l2.Z > h1.Z) continue;
                double S(BezierPatch bp, double u0, double u1, double v0, double v1, bool alongU) =>
                    Math.Max(1e-9, alongU ? (bp.Eval(u1, (v0 + v1) / 2) - bp.Eval(u0, (v0 + v1) / 2)).Length / (u1 - u0) : (bp.Eval((u0 + u1) / 2, v1) - bp.Eval((u0 + u1) / 2, v0)).Length / (v1 - v0));
                var k = new Krawczyk
                {
                    N = 4,
                    F = x => { var p = P.Eval(x[0], x[1]); var d = p - Q.Eval(x[2], x[3]); return new[] { d.X, d.Y, d.Z, Ssi.Implicit(o, p).F }; },
                    J = (lo, hi) =>
                    {
                        var sa = P.Sub(lo[0], hi[0], lo[1], hi[1]); var sb = Q.Sub(lo[2], hi[2], lo[3], hi[3]);
                        if (sa.DerivBounds() is not { } da || sb.DerivBounds() is not { } db) return null;
                        var J = new Iv[4, 4];
                        for (int m = 0; m < 3; m++)
                        {
                            J[m, 0] = Comp(da.SuLo, da.SuHi, m); J[m, 1] = Comp(da.SvLo, da.SvHi, m);
                            J[m, 2] = -Comp(db.SuLo, db.SuHi, m); J[m, 3] = -Comp(db.SvLo, db.SvHi, m);
                        }
                        var c3 = (da.Lo + da.Hi) / 2; double r3 = (da.Hi - da.Lo).Length / 2;
                        if (Ssi.NormalLip(o, c3, r3) is not double kap) return null;
                        var g0 = Ssi.Implicit(o, c3).G; double del = Math.Min(2, kap * r3);
                        if (sa.DirBound(g0, new Vector2d(1, 0)) is not { } du || sa.DirBound(g0, new Vector2d(0, 1)) is not { } dv) return null;
                        double mu = Mag(da.SuLo, da.SuHi) * del, mv = Mag(da.SvLo, da.SvHi) * del;
                        J[3, 0] = new Iv(du.Lo - mu, du.Hi + mu); J[3, 1] = new Iv(dv.Lo - mv, dv.Hi + mv); J[3, 2] = Iv.Pt(0); J[3, 3] = Iv.Pt(0);
                        return J;
                    },
                    Excl = (lo, hi) =>
                    {
                        var (x1, y1) = P.Sub(lo[0], hi[0], lo[1], hi[1]).Box(); var (x2, y2) = Q.Sub(lo[2], hi[2], lo[3], hi[3]).Box();
                        if (x1.X > y2.X || x2.X > y1.X || x1.Y > y2.Y || x2.Y > y1.Y || x1.Z > y2.Z || x2.Z > y1.Z) return true;
                        var c3 = (x1 + y1) / 2; return Math.Abs(Ssi.Implicit(o, c3).F) > (y1 - x1).Length / 2 * (1 + 1e-12) + 1e-13;
                    },
                    Scale = new[] { S(P, a0, a1, b0, b1, true), S(P, a0, a1, b0, b1, false), S(Q, c0, cc1, d0, d1, true), S(Q, c0, cc1, d0, d1, false) },
                };
                var roots = k.Solve(new[] { (new[] { a0, b0, c0, d0 }, new[] { a1, b1, cc1, d1 }) });
                if (roots == null) return null;
                foreach (var r in roots)
                    if (InPatch(P, r[0], r[1]) && InPatch(Q, r[2], r[3])
                        && r[0] >= ra.U0 - 1e-12 && r[0] <= ra.U1 + 1e-12 && r[1] >= ra.V0 - 1e-12 && r[1] <= ra.V1 + 1e-12
                        && r[2] >= rb.U0 - 1e-12 && r[2] <= rb.U1 + 1e-12 && r[3] >= rb.V0 - 1e-12 && r[3] <= rb.V1 + 1e-12)
                        res.Add(P.Eval(r[0], r[1]));
            }
        }
        return Dedupe(res);
    }

    // ---------------------------------------------------------------- разбор

    /// <summary>Мутация P3D_RIGOREVENTS=0: события на сплайнах, рёбрах NURBS и с поверхностью NURBS — выборкой (прежнее).</summary>
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_RIGOREVENTS") == "0";
    public static long Certified, Uncertain;

    /// <summary>Прямоугольник параметров, в котором лежит кривая по затравке: звено — в круге единственности своего начала
    /// (NurbsSsi), запас — 1.2·длина звена / |S_u| (|S_v|) в его начале.</summary>
    public static (double U0, double U1, double V0, double V1) SeedRegion(NurbsSurface S, IReadOnlyList<Vector2d> uv)
    {
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue, mu = 1e-12, mv = 1e-12;
        for (int i = 0; i < uv.Count; i++)
        {
            u0 = Math.Min(u0, uv[i].X); u1 = Math.Max(u1, uv[i].X); v0 = Math.Min(v0, uv[i].Y); v1 = Math.Max(v1, uv[i].Y);
            if (i + 1 < uv.Count)
            {
                S.Derivs(uv[i], out var p0, out var su, out var sv); double len = (S.PointAt(uv[i + 1]) - p0).Length;
                mu = Math.Max(mu, 1.2 * len / Math.Max(su.Length, 1e-12)); mv = Math.Max(mv, 1.2 * len / Math.Max(sv.Length, 1e-12));
            }
        }
        return (Math.Max(S.KnotsU[0], u0 - mu), Math.Min(S.KnotsU[^1], u1 + mu), Math.Max(S.KnotsV[0], v0 - mv), Math.Min(S.KnotsV[^1], v1 + mv));
    }

    /// <summary>Параметры точек на кривой (ближайшая точка не дальше 1e-8 мм — иначе точка не на этом ребре).</summary>
    public static List<double> ParamsOn(ICurve3 c, List<Vector3d> pts)
    {
        var res = new List<double>();
        double span = c.T1 - c.T0, lo = Math.Min(c.T0, c.T1), hi = Math.Max(c.T0, c.T1);
        foreach (var p in pts)
        {
            double t = FaceSplit.ClosestParam(c, p);
            // ближайшая точка по квадрату расстояния — лишь до ~1e-8 параметра (минимум плоский): уточнить по Ньютону
            // корень g(t) = (C(t) − p)·C'(t) (простой: g' ≈ |C'|²)
            for (int it = 0; it < 8; it++)
            {
                double h = 1e-7 * Math.Abs(span);
                var d1 = (c.PointAt(Math.Min(hi, t + h)) - c.PointAt(Math.Max(lo, t - h))) / (Math.Min(hi, t + h) - Math.Max(lo, t - h));
                double g = Vector3d.Dot(c.PointAt(t) - p, d1), gp = d1.LengthSquared;
                if (gp < 1e-300) break;
                double nt = Math.Clamp(t - g / gp, lo, hi);
                if ((c.PointAt(nt) - p).Length > (c.PointAt(t) - p).Length) break;
                if (Math.Abs(nt - t) < 1e-17 * Math.Max(1, Math.Abs(span))) { t = nt; break; }
                t = nt;
            }
            if ((c.PointAt(t) - p).Length <= 1e-8) res.Add(t);
        }
        res.Sort(); return res;
    }

    /// <summary>События кривой с неявной поверхностью для кривых без алгебраического решения; null — не доказано.</summary>
    static readonly bool ImplOffDbg = Environment.GetEnvironmentVariable("P3D_RIGORIMPL") == "0", NurbsOffDbg = Environment.GetEnvironmentVariable("P3D_RIGORNURBS") == "0";
    public static List<double>? OnImplicit(ICurve3 c, Ssi.Shape s)
    {
        if (Off || ImplOffDbg) return null;
        List<Vector3d>? pts = c switch
        {
            SplineCurve3 or BezierCurve3 => CurveImplicit(c, s),
            NurbsImplicitCurve3 nc => SurfTwo(nc, s),
            NurbsNurbsCurve3 nn => TwoSurfacesImplicit(nn.A, SeedRegion(nn.A, nn.Seed.Select(q => new Vector2d(q.X, q.Y)).ToList()), nn.B, SeedRegion(nn.B, nn.Seed.Select(q => new Vector2d(q.Z, q.W)).ToList()), s),
            _ => null,
        };
        if (pts == null) { Interlocked.Increment(ref Uncertain); return null; }
        Interlocked.Increment(ref Certified);
        return ParamsOn(c, pts);
    }

    static List<Vector3d>? SurfTwo(NurbsImplicitCurve3 nc, Ssi.Shape s) { var r = SeedRegion(nc.S, nc.SeedUV); return SurfaceTwoImplicit(nc.S, r.U0, r.U1, r.V0, r.V1, nc.Other, s); }

    /// <summary>События кривой с поверхностью NURBS (прямоугольник грани); null — не доказано.</summary>
    public static List<double>? OnNurbs(ICurve3 c, NurbsSurface T, double ua, double ub, double va, double vb)
    {
        if (Off || NurbsOffDbg) return null;
        List<Vector3d>? pts = c switch
        {
            LineCurve3 or ArcCurve3 or EllipseCurve3 or SplineCurve3 or BezierCurve3 => CurveNurbs(c, T, ua, ub, va, vb),
            IntersectionCurve3 ic when ExactEdges.ShapeOf(ic.A) is { } sa && ExactEdges.ShapeOf(ic.B) is { } sb => SurfaceTwoImplicit(T, ua, ub, va, vb, sa, sb),
            NurbsImplicitCurve3 nc => TwoSurfacesImplicit(nc.S, SeedRegion(nc.S, nc.SeedUV), T, (ua, ub, va, vb), nc.Other),
            _ => null,
        };
        if (pts == null) { Interlocked.Increment(ref Uncertain); return null; }
        Interlocked.Increment(ref Certified);
        return ParamsOn(c, pts);
    }
}
