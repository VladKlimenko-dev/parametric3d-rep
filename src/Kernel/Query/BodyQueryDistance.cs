using OpenTK.Mathematics;
using Parametric3D.Kernel.Analytic;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Query;

/// <summary>Класс точки относительно тела (матрица m.q1).</summary>
public enum PointClass { Outside = -1, OnBoundary = 0, Inside = 1, Unknown = 2 }

/// <summary>
/// m.q1, m.q3: КЛАССИФИКАЦИЯ ТОЧКИ, ПРОЕКЦИИ НА ГРАНЬ И РЕБРО, РАССТОЯНИЕ МЕЖДУ ТЕЛАМИ.
/// <para>
/// Проекция на грань — подножие перпендикуляра внутри грани (плоскость, квадрики, тор — формулой; NURBS — Ньютоном из
/// ближайших треугольников), иначе ближайшая точка её рёбер. Проекция на ребро — ближайшая точка кривой (отрезок, дуга —
/// формулой; прочие — CurveParam).
/// </para>
/// <para>
/// Расстояние тело–тело: 0, если тела пересекаются (точка одного внутри другого — вершины, точки рёбер, точки граней);
/// иначе минимум по кандидатам — вершина–тело (точно, Closest), ребро–ребро (выборка и попеременные проекции), ребро–грань
/// (одномерный спуск по параметру ребра с точным подножием на грани), грань–грань (попеременные проекции из точек выборки
/// с проверкой, что обе точки внутри своих граней). Ответ — пара точек и где они (вершина, ребро, грань).
/// </para>
/// </summary>
public static partial class BodyQuery
{
    /// <summary>Класс точки; Unknown — у тела есть грань, которую классификатор не берёт.</summary>
    public static PointClass Classify(TSolid body, Vector3d p) => Of(new Classifier(body).Classify(p));

    /// <summary>Классификатор для многих точек одного тела (строится один раз).</summary>
    public static Func<Vector3d, PointClass> Classifier(TSolid body) { var c = new Classifier(body); return p => Of(c.Classify(p)); }

    internal static PointClass Of(int? c) => c switch { 1 => PointClass.Inside, -1 => PointClass.Outside, 0 => PointClass.OnBoundary, _ => PointClass.Unknown };

    /// <summary>Подножие перпендикуляра из p на грань NURBS, если оно внутри грани (иначе null).</summary>
    static readonly bool NurbsFootOff = Environment.GetEnvironmentVariable("P3D_QUERYNURBS") == "0";
    internal static Vector3d? NurbsFoot(AnalyticFace af, Vector3d p)
    {
        if (NurbsFootOff) return null;
        var S = af.ParamS!; Vector3d? best = null; double bd = double.MaxValue;
        // затравки — 6 ближайших точек треугольников грани
        var seeds = af.ParamTris!.Select(t => ClosestOnTri(p, t.A, t.B, t.C)).OrderBy(q => (q - p).LengthSquared).Take(6).ToList();
        foreach (var sd in seeds)
        {
            var uv = S.ProjectToUV(p, S.ProjectToUV(sd));
            var q = S.PointAt(uv);
            // перпендикуляр: (p − q) ∥ нормали (иначе подножие — на краю параметров, не внутри грани)
            var n = S.NormalAt(uv); var r = p - q;
            if (r.Length > 1e-9 && n.LengthSquared > 0 && (r - n.Normalized() * Vector3d.Dot(r, n.Normalized())).Length > 1e-6 * Math.Max(1, r.Length)) continue;
            if (af.Contains(q) != true) continue;
            double d = (q - p).Length; if (d < bd) { bd = d; best = q; }
        }
        return best;
    }

    /// <summary>Точки грани NURBS, где d·S(u, v) локально максимальна (подъём Ньютона по параметрам из лучших вершин).</summary>
    internal static IEnumerable<Vector3d> NurbsSupportCandidates(AnalyticFace af, Vector3d d)
    {
        if (NurbsFootOff) yield break;
        var S = af.ParamS!; const double h = 1e-5;
        var seeds = af.ParamTris!.SelectMany(t => new[] { t.A, t.B, t.C }).OrderByDescending(q => Vector3d.Dot(q, d)).Take(5).ToList();
        foreach (var sd in seeds)
        {
            var uv = S.ProjectToUV(sd); double u = uv.X, v = uv.Y;
            for (int it = 0; it < 60; it++)
            {
                double F(double a, double b) => Vector3d.Dot(S.PointAt(new Vector2d(Math.Clamp(a, 0, 1), Math.Clamp(b, 0, 1))), d);
                double f0 = F(u, v), fu = (F(u + h, v) - F(u - h, v)) / (2 * h), fv = (F(u, v + h) - F(u, v - h)) / (2 * h);
                double fuu = (F(u + h, v) - 2 * f0 + F(u - h, v)) / (h * h), fvv = (F(u, v + h) - 2 * f0 + F(u, v - h)) / (h * h);
                double fuv = (F(u + h, v + h) - F(u + h, v - h) - F(u - h, v + h) + F(u - h, v - h)) / (4 * h * h);
                double det = fuu * fvv - fuv * fuv, du, dv;
                if (det > 0 && fuu < 0) { du = -(fvv * fu - fuv * fv) / det; dv = -(fuu * fv - fuv * fu) / det; }
                else { du = fu * 1e-3; dv = fv * 1e-3; }                                   // не вогнуто — шаг по градиенту
                double un = Math.Clamp(u + du, 0, 1), vn = Math.Clamp(v + dv, 0, 1);
                if (F(un, vn) < f0 - 1e-15) { du /= 4; dv /= 4; un = Math.Clamp(u + du, 0, 1); vn = Math.Clamp(v + dv, 0, 1); }
                bool done = Math.Abs(un - u) < 1e-14 && Math.Abs(vn - v) < 1e-14; u = un; v = vn; if (done) break;
            }
            // только внутренний максимум (на краю параметров — рёбра, их уже учли)
            if (u > 1e-9 && u < 1 - 1e-9 && v > 1e-9 && v < 1 - 1e-9) yield return S.PointAt(new Vector2d(u, v));
        }
    }

    static Vector3d ClosestOnTri(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        // Эриксон, «Real-Time Collision Detection», 5.1.5
        var ab = b - a; var ac = c - a; var ap = p - a;
        double d1 = Vector3d.Dot(ab, ap), d2 = Vector3d.Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; double d3 = Vector3d.Dot(ab, bp), d4 = Vector3d.Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; double d5 = Vector3d.Dot(ab, cp), d6 = Vector3d.Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
        double den = 1 / (va + vb + vc); return a + ab * (vb * den) + ac * (vc * den);
    }

    /// <summary>Подножие перпендикуляра из p на поверхность грани, лежащее внутри грани (или null).</summary>
    static Vector3d? Foot(AnalyticFace af, Vector3d p)
    {
        if (af.Param) return NurbsFoot(af, p);
        Vector3d? res = null; double bd = double.MaxValue;
        foreach (var dir in FootDirs(af.Shape, p))
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
            if (q is { } qq && af.Contains(qq) == true) { double d = (qq - p).Length; if (d < bd) { bd = d; res = qq; } }
        }
        return res;
    }

    static IEnumerable<Vector3d> FootDirs(Ssi.Shape sh, Vector3d p)
    {
        if (sh is Ssi.Plane) { yield return Vector3d.UnitZ; yield break; }
        foreach (var d in Dirs(sh, p)) yield return d;
    }

    static Vector3d OnEdge(TEdge e, Vector3d p)
    {
        var c = e.Curve;
        switch (c)
        {
            case LineCurve3 ln:
            {
                var ab = ln.B - ln.A; double t = Math.Clamp(Vector3d.Dot(p - ln.A, ab) / Math.Max(1e-300, ab.LengthSquared), 0, 1);
                return ln.A + ab * t;
            }
            case ArcCurve3 a:
            {
                var u = a.Frame.U.Normalized(); var w = a.Frame.W.Normalized(); var vv = Vector3d.Cross(w, u);
                var d = p - a.Frame.Origin; double th = Math.Atan2(Vector3d.Dot(d, vv), Vector3d.Dot(d, u));
                double tau = (th - a.A0) / a.Sweep, per = 2 * Math.PI / Math.Abs(a.Sweep);
                while (tau < 0) tau += per; while (tau > 1) tau -= per;
                var best = a.PointAt(0); if ((a.PointAt(1) - p).Length < (best - p).Length) best = a.PointAt(1);
                if (tau >= 0 && tau <= 1 && (a.PointAt(tau) - p).Length < (best - p).Length) best = a.PointAt(tau);
                return best;
            }
            default:
            {
                var q = c.PointAt(CurveParam.Closest(c, p)); var q0 = c.PointAt(c.T0); var q1 = c.PointAt(c.T1);
                if ((q0 - p).Length < (q - p).Length) q = q0; if ((q1 - p).Length < (q - p).Length) q = q1;
                return q;
            }
        }
    }

    /// <summary>Ближайшая к p точка грани (внутри или на её рёбрах); null — грань не берётся классификатором.</summary>
    public static Vector3d? ProjectToFace(TFace f, Vector3d p)
    {
        var af = AnalyticFace.Of(f, out _); if (af == null) return null;
        Vector3d? best = Foot(af, p); double bd = best is { } b0 ? (b0 - p).Length : double.MaxValue;
        foreach (var e in f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct())
        {
            var q = OnEdge(e, p); double d = (q - p).Length; if (d < bd) { bd = d; best = q; }
        }
        return best;
    }

    /// <summary>Ближайшая к p точка ребра.</summary>
    public static Vector3d ProjectToEdge(TEdge e, Vector3d p) => OnEdge(e, p);

    public readonly record struct DistanceResult(double Distance, Vector3d A, Vector3d B, string WhereA, string WhereB);

    /// <summary>Расстояние между телами (0 — пересекаются); null — у тела грань, которую не берёт классификатор.</summary>
    public static DistanceResult? Distance(TSolid a, TSolid b)
    {
        var fa = a.Faces.Select(f => AnalyticFace.Of(f, out _)).ToList(); var fb = b.Faces.Select(f => AnalyticFace.Of(f, out _)).ToList();
        if (fa.Any(x => x == null) || fb.Any(x => x == null)) return null;
        var ca = new Classifier(a); var cb = new Classifier(b);
        if (ca.Unsupported != null || cb.Unsupported != null) return null;

        // 1. пересечение: точка одного тела внутри другого (вершины, точки рёбер, точки треугольников граней)
        foreach (var (src, srcFaces, cls, wa) in new[] { (a, fa, cb, true), (b, fb, ca, false) })
            foreach (var q in Samples(src, srcFaces!))
                if (cls.Classify(q) is 1 or 0) return new DistanceResult(0, q, q, wa ? "inside" : "contact", wa ? "contact" : "inside");

        double best = double.MaxValue; Vector3d pa = default, pb = default; string wA = "", wB = "";
        void Take(Vector3d x, Vector3d y, string sx, string sy) { double d = (x - y).Length; if (d < best) { best = d; pa = x; pb = y; wA = sx; wB = sy; } }

        // 2. вершина–тело (точно)
        foreach (var v in a.Vertices) if (Closest(b, v.P) is { } r) Take(v.P, r.Point, "vertex", r.Where);
        foreach (var v in b.Vertices) if (Closest(a, v.P) is { } r) Take(r.Point, v.P, r.Where, "vertex");

        // 3. ребро–ребро: выборка 64×64, уточнение попеременными проекциями
        foreach (var ea in a.Edges)
            foreach (var eb in b.Edges)
            {
                var sa = Sample(ea.Curve, 64); var sb = Sample(eb.Curve, 64);
                double bd = double.MaxValue; Vector3d x = default, y = default;
                foreach (var p1 in sa) foreach (var p2 in sb) { double d = (p1 - p2).LengthSquared; if (d < bd) { bd = d; x = p1; y = p2; } }
                for (int it = 0; it < 60; it++) { var y2 = OnEdge(eb, x); var x2 = OnEdge(ea, y2); if ((x2 - x).Length < 1e-13 && (y2 - y).Length < 1e-13) { x = x2; y = y2; break; } x = x2; y = y2; }
                Take(x, y, "edge", "edge");
            }

        // 4. ребро–грань: минимум по параметру ребра расстояния до подножия на грани
        void EdgeFace(TEdge e, AnalyticFace f, bool edgeOnA)
        {
            var c = e.Curve; double T0 = c.T0, T1 = c.T1; const int n = 48;
            double Dist(double t) { var p = c.PointAt(t); return Foot(f, p) is { } q ? (q - p).Length : double.MaxValue; }
            var vals = Enumerable.Range(0, n + 1).Select(i => Dist(T0 + (T1 - T0) * i / n)).ToArray();
            for (int i = 0; i <= n; i++)
            {
                if (vals[i] == double.MaxValue) continue;
                if (i > 0 && vals[i - 1] < vals[i]) continue;
                if (i < n && vals[i + 1] < vals[i]) continue;
                double lo = T0 + (T1 - T0) * Math.Max(0, i - 1) / n, hi = T0 + (T1 - T0) * Math.Min(n, i + 1) / n;
                const double g = 0.6180339887498949;
                double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = Dist(x1), f2 = Dist(x2);
                for (int it = 0; it < 80 && hi - lo > 1e-15 * Math.Max(1, Math.Abs(hi)); it++)
                {
                    if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = Dist(x1); }
                    else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = Dist(x2); }
                }
                double tb = f1 < f2 ? x1 : x2; var pe = c.PointAt(tb);
                if (Foot(f, pe) is { } q) { if (edgeOnA) Take(pe, q, "edge", "face"); else Take(q, pe, "face", "edge"); }
            }
        }
        foreach (var e in a.Edges) foreach (var f in fb) EdgeFace(e, f!, true);
        foreach (var e in b.Edges) foreach (var f in fa) EdgeFace(e, f!, false);

        // 5. грань–грань: попеременные проекции из точек выборки грани A
        foreach (var f1 in fa)
            foreach (var f2 in fb)
                foreach (var seed in FaceSamples(f1!.Face, 24))
                {
                    var x = seed; Vector3d? y = null;
                    for (int it = 0; it < 200; it++)
                    {
                        if (Foot(f2!, x) is not { } y2) { y = null; break; }
                        if (Foot(f1, y2) is not { } x2) { y = null; break; }
                        bool done = (x2 - x).Length < 1e-13 * Math.Max(1, x.Length); x = x2; y = y2; if (done) break;
                    }
                    if (y is { } yy) Take(x, yy, "face", "face");
                }
        return best < double.MaxValue ? new DistanceResult(best, pa, pb, wA, wB) : null;
    }

    static List<Vector3d> Sample(ICurve3 c, int n) => Enumerable.Range(0, n + 1).Select(i => c.PointAt(c.T0 + (c.T1 - c.T0) * i / n)).ToList();

    static IEnumerable<Vector3d> Samples(TSolid s, List<AnalyticFace?> faces)
    {
        foreach (var v in s.Vertices) yield return v.P;
        foreach (var e in s.Edges) foreach (var p in Sample(e.Curve, 16)) yield return p;
        // точки граней — на самой поверхности (центр треугольника у кривой грани отходит на прогиб)
        foreach (var af in faces) foreach (var p in FaceSamples(af!.Face, 64)) yield return af.ProjectTo(p);
    }

    /// <summary>Точки грани: центры треугольников её разметки (не больше max, равномерно по списку).</summary>
    static List<Vector3d> FaceSamples(TFace f, int max)
    {
        var tris = new List<Vector3d>();
        try
        {
            if (f.Surface is Surfaces.FacetedSurface fs && fs.Facets.Count > 0) foreach (var (a, b, c) in fs.Facets.ReadView) tris.Add((a + b + c) / 3);
            else
            {
                var mb = new Tess.MeshBuilder(); GeometryAudit.AuthoritativeSurface(f)!.TessellateFace(f, mb); var md = new Tess.MeshData(); mb.FillInto(md);
                for (int i = 0; i + 2 < md.Indices.Length; i += 3) tris.Add((mb.PositionsD[md.Indices[i]] + mb.PositionsD[md.Indices[i + 1]] + mb.PositionsD[md.Indices[i + 2]]) / 3);
            }
        }
        catch { }
        if (tris.Count <= max) return tris;
        var res = new List<Vector3d>(max); for (int i = 0; i < max; i++) res.Add(tris[(int)((long)i * tris.Count / max)]);
        return res;
    }
}
