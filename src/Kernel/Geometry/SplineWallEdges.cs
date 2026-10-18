using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Geometry;

/// <summary>
/// m.x2: СТЕНКА-ВЫДАВЛИВАНИЕ СПЛАЙНА — ТОЧНЫЕ ВЕРШИНЫ И КРИВЫЕ. Булева с призмой по сплайну давала вершины стенки из точек
/// многогранника (до 2.4 мкм от стенки: вырез сплайновой призмой из куба) и рёбра «плоскость поперёк выдавливания × стенка»
/// кривыми пересечения по затравке. Здесь:
/// • вершина, где сходятся стенка и не меньше двух плоскостей, — на их точное пересечение (x = B(t) + Dir·h, Ньютон по (t, h)
///   по уравнениям плоскостей), сдвиг не больше 0.01 мм;
/// • ребро «стенка × плоскость поперёк Dir» — сам сплайн, сдвинутый в плоскость, от параметра одной вершины до другой
///   (точный кусок, SplitAt);
/// • отрезки, чьи концы-вершины сдвинуты, — заново по вершинам.
/// Мутация P3D_SPLINEWALL=0.
/// </summary>
public static class SplineWallEdges
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_SPLINEWALL") == "0";
    public static int VerticesSnapped, CurvesReplaced;

    static (Vector3d N, Vector3d O)? PlaneOf(ISurface3? s) => s switch
    {
        PlaneSurface p => (p.F.W.Normalized(), p.F.Origin),
        ExtrudedSurface { Base: LineCurve3 l } e when Vector3d.Cross(l.B - l.A, e.Dir).LengthSquared > 1e-18 => (Vector3d.Cross(l.B - l.A, e.Dir).Normalized(), l.A),
        _ => null
    };

    /// <summary>Стенка-выдавливание: выдавливание сплайна или кривой Безье, или поверхность смещения такой стенки; направление.</summary>
    static (ISurface3 S, Vector3d Dir)? Wall(ISurface3? s) => s switch
    {
        ExtrudedSurface { Base: SplineCurve3 or BezierCurve3 } ex when ex.Dir.LengthSquared > 1e-18 => (ex, ex.Dir),
        OffsetSurface { Base: ExtrudedSurface { Base: SplineCurve3 or BezierCurve3 } bx } os when !OffsetOff && bx.Dir.LengthSquared > 1e-18 => (os, bx.Dir),
        _ => null
    };
    static readonly bool OffsetOff = Environment.GetEnvironmentVariable("P3D_SPLINEWALLOFFSET") == "0";

    static SplineCurve3? AsSpline(ICurve3 c) => c switch
    {
        SplineCurve3 sp => sp,
        BezierCurve3 bz => new SplineCurve3(bz.P, bz.P.Length - 1),   // Безье — B-сплайн с одним пролётом (тот же параметр)
        _ => null
    };

    public static int Apply(TSolid solid)
    {
        if (Off) return 0;
        var walls = solid.Faces.Where(f => Wall(GeometryAudit.AuthoritativeSurface(f)) != null).ToList();
        if (walls.Count == 0) return 0;
        solid.BeginEdit("SplineWallEdges.Apply", EditKind.Derive);
        int changed = 0;
        var facesOf = new Dictionary<TVertex, HashSet<TFace>>(ReferenceEqualityComparer.Instance);
        foreach (var e in solid.Edges)
            foreach (var v in new[] { e.V0, e.V1 }.Distinct())
            {
                if (!facesOf.TryGetValue(v, out var set)) facesOf[v] = set = new HashSet<TFace>(ReferenceEqualityComparer.Instance);
                foreach (var ce in e.CoEdges) if (ce.Loop?.Face is { } f) set.Add(f);
            }
        var moved = new Dictionary<TVertex, Vector3d>(ReferenceEqualityComparer.Instance);
        foreach (var (v, faces) in facesOf)
        {
            var wl = faces.Where(f => walls.Contains(f)).ToList();
            if (wl.Count != 1) continue;
            var ws = GeometryAudit.AuthoritativeSurface(wl[0])!; var (sw, dir) = Wall(ws)!.Value;
            var planes = faces.Where(f => f != wl[0]).Select(f => PlaneOf(GeometryAudit.AuthoritativeSurface(f))).ToList();
            if (planes.Count < 2 || planes.Any(p => p == null)) continue;
            var pls = planes.Select(p => p!.Value).ToList();
            var uv = sw.ProjectToUV(v.P); double t = uv.X, h = uv.Y;
            // стенка, касательная к плоскости вершины (гладкий шов), — положение вдоль шва не определено: не трогать
            var nw0 = sw.NormalAt(uv); if (nw0.LengthSquared < 1e-24) continue; nw0.Normalize();
            if (pls.Any(pq => Math.Abs(Vector3d.Dot(pq.N, nw0)) > 1 - 1e-6)) continue;
            for (int it = 0; it < 40; it++)
            {
                var x = sw.PointAt(new Vector2d(t, h));
                double dt = 1e-7; double ta = Math.Max(0, t - dt), tb = Math.Min(1, t + dt);
                var xt = (sw.PointAt(new Vector2d(tb, h)) - sw.PointAt(new Vector2d(ta, h))) / (tb - ta);
                // наименьшие квадраты: Σ (n·xt, n·Dir)ᵀ(…) Δ = −Σ (…) f
                double a11 = 0, a12 = 0, a22 = 0, r1 = 0, r2 = 0, worst = 0;
                foreach (var (n, o) in pls)
                {
                    double fv = Vector3d.Dot(n, x - o), g1 = Vector3d.Dot(n, xt), g2 = Vector3d.Dot(n, dir);
                    worst = Math.Max(worst, Math.Abs(fv));
                    a11 += g1 * g1; a12 += g1 * g2; a22 += g2 * g2; r1 -= g1 * fv; r2 -= g2 * fv;
                }
                if (worst < 1e-14) break;
                double det = a11 * a22 - a12 * a12; if (Math.Abs(det) < 1e-18) break;
                t = Math.Clamp(t + (a22 * r1 - a12 * r2) / det, 0, 1); h += (-a12 * r1 + a11 * r2) / det;
            }
            var xn = sw.PointAt(new Vector2d(t, h));
            double res = pls.Max(p => Math.Abs(Vector3d.Dot(p.N, xn - p.O)));
            double shift = (xn - v.P).Length;
            if (res > 1e-10 || shift > 1e-2 || shift < 1e-13) continue;
            moved[v] = v.P; v.P = xn; VerticesSnapped++; changed++;
        }
        foreach (var e in solid.Edges)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count == 2 && fs.Count(walls.Contains) == 1 && !ReferenceEquals(e.V0, e.V1))
            {
                var wall = fs.First(walls.Contains); var other = fs.First(f => f != wall);
                var ws = GeometryAudit.AuthoritativeSurface(wall)!; var (sw, dir) = Wall(ws)!.Value;
                if (PlaneOf(GeometryAudit.AuthoritativeSurface(other)) is { } pl && e.Curve is not LineCurve3
                    && Math.Abs(Math.Abs(Vector3d.Dot(pl.N, dir.Normalized())) - 1) < 1e-12)
                {
                    // кривая стенки, сдвинутая в плоскость, от вершины до вершины
                    var u0 = sw.ProjectToUV(e.V0.P); var u1 = sw.ProjectToUV(e.V1.P);
                    double h = u0.Y + Vector3d.Dot(pl.O - sw.PointAt(u0), pl.N) / Vector3d.Dot(dir, pl.N);
                    double ta = Math.Min(u0.X, u1.X), tb = Math.Max(u0.X, u1.X);
                    if (tb - ta < 1e-9) continue;
                    ICurve3 shifted;
                    if (sw is ExtrudedSurface ex && AsSpline(ex.Base) is { } sp)
                    {
                        SplineCurve3 piece = sp;
                        if (tb < 1 - 1e-12) piece = piece.SplitAt(tb).A;
                        if (ta > 1e-12) piece = piece.SplitAt(ta / tb).B;
                        var sh = new SplineCurve3(piece.ControlPoints.Select(p => p + ex.Dir * h).ToList(), piece.Degree, piece.Weights.ToList(), piece.Knots.ToList());
                        shifted = u0.X > u1.X ? sh.Reversed() : sh;
                    }
                    else
                    {
                        // поверхность смещения стенки: кривая по формуле S(t, h) (точна по построению)
                        double hh = h; var sws = sw;
                        shifted = u0.X <= u1.X ? new ProcCurve3(t => sws.PointAt(new Vector2d(t, hh)), ta, tb, false, "wall section")
                                               : new ProcCurve3(t => sws.PointAt(new Vector2d(ta + tb - t, hh)), ta, tb, false, "wall section");
                    }
                    // проверка: концы у вершин, середина близко к прежней кривой
                    double gap = Math.Max((shifted.PointAt(shifted.T0) - e.V0.P).Length, (shifted.PointAt(shifted.T1) - e.V1.P).Length);
                    double dev = 0;
                    for (int k = 1; k < 8; k++) { var q = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 8); dev = Math.Max(dev, (shifted.PointAt(CurveParam.Closest(shifted, q)) - q).Length); }
                    if (gap > 1e-7 || dev > 1e-3) continue;
                    e.Curve = shifted; e.PolylineCache = null; e.Tolerance = Math.Max(e.Tolerance, gap);
                    CurvesReplaced++; changed++;
                    continue;
                }
            }
            // отрезок со сдвинутой вершиной — по вершинам
            if (e.Curve is LineCurve3 && (moved.ContainsKey(e.V0) || moved.ContainsKey(e.V1)))
            {
                var l = (LineCurve3)e.Curve;
                bool fwd = (l.A - (moved.TryGetValue(e.V0, out var p0) ? p0 : e.V0.P)).Length <= (l.B - (moved.TryGetValue(e.V0, out var p0b) ? p0b : e.V0.P)).Length;
                e.Curve = fwd ? new LineCurve3(e.V0.P, e.V1.P) : new LineCurve3(e.V1.P, e.V0.P);
                e.PolylineCache = null; changed++;
            }
        }
        if (changed > 0)
        {
            foreach (var f in solid.Faces) if (f.Surface is FacetedSurface fs) fs.SetDisplay(null, null);
            solid.DisplayMeshKey = null;
        }
        return changed;
    }
}
