using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// m.q2: МАССОВЫЕ ХАРАКТЕРИСТИКИ С ГРАНИЦЕЙ ОШИБКИ — объём, центр масс, тензор инерции относительно центра масс
/// (плотность 1, мм⁵), каждая величина с границей ошибки.
///
/// Точный путь — потоки полей через грани по настоящим кривым рёбер (AnalyticFlux; объём — AnalyticVolume с его границей
/// g.04): граница = допуск сущностей × площадь × Rᵏ (сдвиг границы тела на допуск меняет ∫ yᵏ dV не больше, чем на
/// слой толщины допуска) + ОЦЕНКА КВАДРАТУРЫ (разность расчётов с одним и двумя кусками на звено — используется точный)
/// + округление. R — наибольшее расстояние точки тела от опорной точки (центр габарита).
///
/// Путь по сетке (у тела есть грань без потока: тор, кромочный лоскут, многогранная форма) — моменты многогранника по
/// тетраэдрам в double (разметка каждой грани отдельно, PositionsD) от опорной точки: граница = 2 × ИЗМЕРЕННЫЙ отход
/// треугольников от поверхности грани (центр и середины сторон каждого) × площадь × (R + δ)ᵏ; грань без точной
/// поверхности (гранёная) — граница бесконечна (ошибка неизвестна).
///
/// Центр масс и центральный тензор выводятся из сырых моментов; их границы — по правилам распространения ошибок
/// (первый порядок с запасом на второй). Мутация P3D_MASSBOUND=0 — без членов допуска и гранения (только квадратура).
/// </summary>
public static class MassMeasure
{
    public sealed record Result(double Volume, double VolumeError, Vector3d Centroid, double CentroidError,
                                Matrix3d Inertia, double InertiaError, string Route)
    {
        public override string ToString() =>
            $"V {Volume:G9} ± {VolumeError:G3}; c {Centroid} ± {CentroidError:G3}; J ± {InertiaError:G3} ({Route})";
    }

    static readonly bool BoundOff = Environment.GetEnvironmentVariable("P3D_MASSBOUND") == "0";
    /// <summary>Разбор: по граням (вид поверхности, площадь, отход, треугольники).</summary>
    public static Action<string>? Debug;

    /// <summary>Сырые моменты от опорной точки o: V, M1 = ∫ y dV, S = ∫ y yᵀ dV.</summary>
    sealed class Raw { public double V; public Vector3d M1; public Matrix3d S; public double ErrV, ErrM1, ErrS; }

    public static Result Of(TSolid solid)
    {
        // потоки и точный объём — по соглашению K05 (обход контуров); тело не по соглашению — его копия, приведённая к нему
        if (solid.TopologyConvention < TopologyConventions.Version)
        {
            var c = TopologyClone.Deep(solid);
            TopologyConventions.Normalize(c);
            solid = c;
        }
        var bb = Bounds3.Empty;
        foreach (var v in solid.Vertices) bb.Add(v.P);
        var meshes = FaceMeshes(solid, refine: false, out double area, out _, out bool faceted, out _);
        foreach (var fm in meshes) foreach (var p in fm.V) bb.Add(p);
        var o = bb.IsEmpty ? Vector3d.Zero : (bb.Min + bb.Max) / 2;
        double R = bb.IsEmpty ? 0 : (bb.Max - bb.Min).Length / 2 * (1 + 1e-9);

        Raw? raw = Analytic(solid, o, R, area);
        string route = "analytic boundary integral";
        if (raw == null)
        {
            // сетка, уточнённая по поверхностям (середины сторон — на поверхность грани, на краю — на кривую ребра)
            meshes = FaceMeshes(solid, refine: !faceted, out area, out _, out faceted, out int levels);
            raw = Mesh(meshes, o);
            route = faceted ? "mesh of faceted faces (bound unknown)" : $"refined mesh of analytic faces (×4^{levels})";
            if (!BoundOff)
            {
                foreach (var fm in meshes)
                {
                    double d = faceted ? double.PositiveInfinity : 2 * fm.Delta;
                    double rr = fm.RMax(o) + d;
                    raw.ErrV += d * fm.Area * 1.05;
                    raw.ErrM1 += d * fm.Area * rr * 1.05;
                    raw.ErrS += d * fm.Area * rr * rr * 1.05;
                }
            }
            // ТЕЛО С АВТОРИТЕТНОЙ МНОГОГРАННОЙ ФОРМОЙ (как у объёма, P3D_POLYBOUND): истина — в пределах гранения от
            // многогранника; к границе — расхождение моментов по поверхностям и по фасетам и отход фасет × площадь
            if (!BoundOff && solid.Exact != null && solid.ExactAuthoritative)
            {
                var poly = Polyhedron(solid, o, out double chordP, out double areaP);
                double rr = R + chordP;
                raw.ErrV += Math.Abs(raw.V - poly.V) + 2 * chordP * areaP;
                raw.ErrM1 += (raw.M1 - poly.M1).Length + 2 * chordP * areaP * rr;
                raw.ErrS += MaxDiff(raw.S, poly.S) + 2 * chordP * areaP * rr * rr;
                route += ", polyhedral form authoritative";
            }
            // округление double по тетраэдрам
            raw.ErrV += 1e-12 * Math.Abs(raw.V); raw.ErrM1 += 1e-12 * Math.Abs(raw.V) * R; raw.ErrS += 1e-12 * Math.Abs(raw.V) * R * R;
            // объём точнее у точного пути объёма, если он есть
            if (!faceted && AnalyticVolume.OfWithError(solid) is { } ve)
            {
                double tol = AnalyticMeasure.EntityBound(solid);
                double eV = (BoundOff ? 0 : tol * area) + ve.QuadratureError + 1e-9 * Math.Abs(ve.Volume);
                if (eV < raw.ErrV) { raw.V = ve.Volume; raw.ErrV = eV; }
            }
        }
        return Derive(raw, o, route);
    }

    static Result Derive(Raw r, Vector3d o, string route)
    {
        double V = r.V, eV = r.ErrV;
        double Vlow = Math.Abs(V) - eV;
        if (!(Vlow > 0)) return new Result(V, eV, o, double.PositiveInfinity, Matrix3d.Zero, double.PositiveInfinity, route);
        var g = r.M1 / V;
        double eC = (r.ErrM1 + g.Length * eV) / Vlow;
        // центральные вторые моменты: S' = S − M1 M1ᵀ / V
        var Sc = Sub(r.S, Outer(r.M1 / V, r.M1));
        double m1 = r.M1.Length;
        double eSc = r.ErrS + 2 * m1 * r.ErrM1 / Vlow + m1 * m1 * eV / (Vlow * Math.Abs(V)) + r.ErrM1 * r.ErrM1 / Vlow;
        double tr = Sc.M11 + Sc.M22 + Sc.M33;
        var J = Sub(new Matrix3d(tr, 0, 0, 0, tr, 0, 0, 0, tr), Sc);
        double eJ = 3 * eSc;
        return new Result(Math.Abs(V), eV, o + g, eC, J, eJ, route);
    }

    static Matrix3d Sub(Matrix3d a, Matrix3d b) =>
        new(a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13, a.M21 - b.M21, a.M22 - b.M22, a.M23 - b.M23, a.M31 - b.M31, a.M32 - b.M32, a.M33 - b.M33);

    static Matrix3d Outer(Vector3d a, Vector3d b) =>
        new(a.X * b.X, a.X * b.Y, a.X * b.Z, a.Y * b.X, a.Y * b.Y, a.Y * b.Z, a.Z * b.X, a.Z * b.Y, a.Z * b.Z);

    // ------------------------------------------------------------------ точный путь

    static Raw? Analytic(TSolid solid, Vector3d o, double R, double area)
    {
        // тело с авторитетной многогранной формой: как у объёма (P3D_POLYBOUND) — интегралы по источникам граней не годятся
        if (solid.Exact != null && solid.ExactAuthoritative) return null;
        var fields = new Func<Vector3d, Vector3d>[]
        {
            x => (x - o) / 3,
            x => { var y = x - o; return new Vector3d(y.X * y.X / 2, 0, 0); },
            x => { var y = x - o; return new Vector3d(0, y.Y * y.Y / 2, 0); },
            x => { var y = x - o; return new Vector3d(0, 0, y.Z * y.Z / 2); },
            x => { var y = x - o; return new Vector3d(y.X * y.X * y.X / 3, 0, 0); },
            x => { var y = x - o; return new Vector3d(0, y.Y * y.Y * y.Y / 3, 0); },
            x => { var y = x - o; return new Vector3d(0, 0, y.Z * y.Z * y.Z / 3); },
            x => { var y = x - o; return new Vector3d(y.X * y.X * y.Y / 2, 0, 0); },
            x => { var y = x - o; return new Vector3d(0, y.Y * y.Y * y.Z / 2, 0); },
            x => { var y = x - o; return new Vector3d(y.X * y.X * y.Z / 2, 0, 0); },
        };
        double[]? Sum(int scale)
        {
            int prev = AnalyticFlux._pieceScale;
            AnalyticFlux.PieceScale = scale;
            try
            {
                var res = new double[fields.Length];
                foreach (var f in solid.Faces)
                {
                    // целая замкнутая поверхность (шар, тор): моменты тела внутри неё — в замкнутом виде
                    if (WholeClosed(f) is { } closed) { for (int i = 0; i < res.Length; i++) res[i] += closed[i]; continue; }
                    for (int i = 0; i < fields.Length; i++)
                    {
                        var v = AnalyticFlux.FaceFluxOf(f, fields[i]);
                        if (v == null || !double.IsFinite(v.Value)) return null;
                        res[i] += v.Value;
                    }
                }
                return res;

                double[]? WholeClosed(TFace f)
                {
                    if (GeometryAudit.AuthoritativeSurface(f) is not RevolvedSurface rv || rv.Base is not ArcCurve3 arc) return null;
                    if (Math.Abs(Math.Abs(rv.A1 - rv.A0) - 2 * Math.PI) > 1e-9) return null;
                    // все рёбра контуров — швы (каждое ребро пройдено дважды): грань — вся поверхность
                    var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
                    foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
                    if (uses.Values.Any(n => n % 2 != 0)) return null;
                    var w = rv.Axis.W.Normalized(); var ax = rv.Axis.Origin;
                    var cen = arc.Frame.Origin; double r = arc.Radius;
                    var d = cen - ax; var rho = d - w * Vector3d.Dot(d, w);
                    double V; Vector3d c; Matrix3d Sc;
                    if (rho.Length < 1e-9 * Math.Max(1, r) && Math.Abs(Math.Abs(arc.Sweep) - Math.PI) < 1e-9)
                    {
                        V = 4.0 / 3 * Math.PI * r * r * r; c = cen;                    // шар
                        double k = V * r * r / 5; Sc = new Matrix3d(k, 0, 0, 0, k, 0, 0, 0, k);
                    }
                    else if (arc.IsClosed && rho.Length > r)
                    {
                        double Rt = rho.Length;                                        // тор
                        V = 2 * Math.PI * Math.PI * Rt * r * r; c = ax + w * Vector3d.Dot(d, w);
                        double a = V * (Rt * Rt / 2 + 3 * r * r / 8), b = V * r * r / 4;
                        Sc = new Matrix3d(a + (b - a) * w.X * w.X, (b - a) * w.X * w.Y, (b - a) * w.X * w.Z,
                                          (b - a) * w.Y * w.X, a + (b - a) * w.Y * w.Y, (b - a) * w.Y * w.Z,
                                          (b - a) * w.Z * w.X, (b - a) * w.Z * w.Y, a + (b - a) * w.Z * w.Z);
                    }
                    else return null;
                    // знак: наружная нормаль грани от центра трубки (шар — от центра) — тело внутри, иначе полость
                    var probe = rv.PointAt(new Vector2d(0.3, 0.4));
                    Vector3d radial;
                    { var pd = probe - c; var pr = pd - w * Vector3d.Dot(pd, w); var tubeC = rho.Length < 1e-9 * Math.Max(1, r) ? c : c + (pr.LengthSquared > 0 ? pr.Normalized() : Vector3d.UnitX) * rho.Length; radial = probe - tubeC; }
                    double sgn;
                    try { sgn = Vector3d.Dot(f.NormalAt(probe), radial) >= 0 ? 1 : -1; } catch { return null; }
                    var y = c - o;
                    var Sy = new Matrix3d(Sc.M11 + V * y.X * y.X, Sc.M12 + V * y.X * y.Y, Sc.M13 + V * y.X * y.Z,
                                          Sc.M21 + V * y.Y * y.X, Sc.M22 + V * y.Y * y.Y, Sc.M23 + V * y.Y * y.Z,
                                          Sc.M31 + V * y.Z * y.X, Sc.M32 + V * y.Z * y.Y, Sc.M33 + V * y.Z * y.Z);
                    return new[] { V, V * y.X, V * y.Y, V * y.Z, Sy.M11, Sy.M22, Sy.M33, Sy.M12, Sy.M23, Sy.M13 }.Select(v => sgn * v).ToArray();
                }
            }
            finally { AnalyticFlux._pieceScale = prev; }
        }
        var f1 = Sum(1);
        if (f1 == null) return null;
        var f2 = Sum(2) ?? f1;
        double q0 = Math.Abs(f2[0] - f1[0]), q1 = 0, q2 = 0;
        for (int i = 1; i < 4; i++) q1 = Math.Max(q1, Math.Abs(f2[i] - f1[i]));
        for (int i = 4; i < 10; i++) q2 = Math.Max(q2, Math.Abs(f2[i] - f1[i]));
        double tol = BoundOff ? 0 : AnalyticMeasure.EntityBound(solid);
        double V = f2[0];
        var ve = AnalyticVolume.OfWithError(solid);
        var r = new Raw
        {
            V = ve?.Volume ?? V,
            M1 = new Vector3d(f2[1], f2[2], f2[3]),
            S = new Matrix3d(f2[4], f2[7], f2[9], f2[7], f2[5], f2[8], f2[9], f2[8], f2[6]),
        };
        // граница объёма — как у AnalyticMeasure.Volume (допуск × площадь + квадратура + округление)
        r.ErrV = tol * area + (ve?.QuadratureError ?? q0) + 1e-9 * Math.Abs(r.V);
        r.ErrM1 = tol * area * R + q1 + 1e-9 * Math.Abs(r.V) * R;
        r.ErrS = tol * area * R * R + q2 + 1e-9 * Math.Abs(r.V) * R * R;
        return r;
    }

    // ------------------------------------------------------------------ путь по сетке

    sealed class FaceMesh
    {
        public readonly List<Vector3d> V = new();
        public readonly List<(int A, int B, int C)> T = new();
        public double Area, Delta;
        public double RMax(Vector3d o) => V.Count == 0 ? 0 : V.Max(p => (p - o).Length);
    }

    /// <summary>Наибольшее число треугольников после уточнения (на всё тело).</summary>
    public const int RefineBudget = 400_000;

    /// <summary>
    /// Разметка каждой грани в double; refine — уточнение делением треугольников на 4 (до 3 раз, в пределах бюджета):
    /// середина стороны на краю грани — точка кривой ребра (по параметрам концов: соседние грани получают ту же точку,
    /// сетка остаётся замкнутой), внутри — проекция на поверхность. Отход — измеренный (центр и середины сторон каждого
    /// треугольника до поверхности грани).
    /// </summary>
    static List<FaceMesh> FaceMeshes(TSolid solid, bool refine, out double area, out double delta, out bool faceted, out int levels)
    {
        area = 0; delta = 0; faceted = false; levels = 0;
        var res = new List<FaceMesh>();
        var srcs = new List<ISurface3?>();
        var paramCache = new Dictionary<(TEdge, Vector3d), double>();
        foreach (var f in solid.Faces)
        {
            var mb = new MeshBuilder();
            try { f.Surface.TessellateFace(f, mb); } catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { faceted = true; continue; }
            var md = new MeshData(); mb.FillInto(md);
            var fm = new FaceMesh();
            fm.V.AddRange(mb.PositionsD);
            for (int i = 0; i + 2 < md.Indices.Length; i += 3) fm.T.Add((md.Indices[i], md.Indices[i + 1], md.Indices[i + 2]));
            res.Add(fm);
            var src = GeometryAudit.AuthoritativeSurface(f);
            srcs.Add(src);
            if (src == null) faceted = true;
        }
        if (refine && !faceted)
        {
            int total = res.Sum(m => m.T.Count);
            while (levels < 3 && total * 4 <= RefineBudget)
            {
                int fi = 0;
                foreach (var f in solid.Faces)
                {
                    if (fi >= res.Count) break;
                    var src = srcs[fi]; var fm = res[fi]; fi++;
                    // плоские грани тоже: их край на кривых рёбрах уточняется вместе с соседями (сетка замкнута)
                    if (src == null) continue;
                    var edges = f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge).Distinct().ToList();
                    Subdivide(fm, src, edges, paramCache);
                }
                levels++;
                total = res.Sum(m => m.T.Count);
            }
        }
        for (int k = 0; k < res.Count; k++)
        {
            var fm = res[k]; var src = srcs[k];
            foreach (var (a, b, c) in fm.T)
            {
                var pa = fm.V[a]; var pb = fm.V[b]; var pc = fm.V[c];
                fm.Area += Vector3d.Cross(pb - pa, pc - pa).Length / 2;
                if (src == null || src.IsPlanar) continue;
                foreach (var p in new[] { (pa + pb + pc) / 3, (pa + pb) / 2, (pb + pc) / 2, (pc + pa) / 2 })
                {
                    double d;
                    try { d = (src.PointAt(src.ProjectToUV(p)) - p).Length; } catch { d = double.PositiveInfinity; }
                    if (!double.IsFinite(d)) { faceted = true; break; }
                    fm.Delta = Math.Max(fm.Delta, d);
                }
            }
            area += fm.Area; delta = Math.Max(delta, fm.Delta);
            Debug?.Invoke($"face {k}: {src?.GetType().Name ?? "faceted"} area {fm.Area:G9} delta {fm.Delta:G3} tris {fm.T.Count}");
        }
        return res;
    }

    static void Subdivide(FaceMesh fm, ISurface3 src, List<TEdge> edges, Dictionary<(TEdge, Vector3d), double> paramCache)
    {
        // стороны: сколько треугольников у каждой (1 — край грани)
        var use = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in fm.T) foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) }) { var k = p < q ? (p, q) : (q, p); use[k] = use.GetValueOrDefault(k) + 1; }
        var mid = new Dictionary<(int, int), int>();
        int Mid(int p, int q)
        {
            var k = p < q ? (p, q) : (q, p);
            if (mid.TryGetValue(k, out var m)) return m;
            var a = fm.V[p]; var b = fm.V[q]; var x = (a + b) / 2;
            Vector3d np;
            if (use[k] == 1 && OnEdge(a, b) is { } ep) np = ep;
            else { try { np = src.PointAt(src.ProjectToUV(x)); } catch { np = x; } if (!double.IsFinite(np.X)) np = x; }
            m = fm.V.Count; fm.V.Add(np); mid[k] = m;
            return m;
        }
        Vector3d? OnEdge(Vector3d a, Vector3d b)
        {
            foreach (var e in edges)
            {
                var c = e.Curve;
                double ta = Param(e, a), tb = Param(e, b);
                if ((c.PointAt(ta) - a).Length > 1e-7 * Math.Max(1, a.Length) || (c.PointAt(tb) - b).Length > 1e-7 * Math.Max(1, b.Length)) continue;
                double span = c.T1 - c.T0;
                bool closed = (c.PointAt(c.T0) - c.PointAt(c.T1)).Length < 1e-9;
                if (closed && Math.Abs(tb - ta) > span / 2) { if (tb > ta) tb -= span; else tb += span; }
                double tm = (ta + tb) / 2;
                if (closed) { while (tm < c.T0) tm += span; while (tm > c.T1) tm -= span; }
                var p = c.PointAt(tm);
                // середина — между концами (а не на другой ветви кривой)
                if ((p - (a + b) / 2).Length > (b - a).Length) continue;
                return p;
            }
            return null;
        }
        double Param(TEdge e, Vector3d p)
        {
            if (paramCache.TryGetValue((e, p), out var t)) return t;
            t = Parametric3D.Kernel.Analytic.FaceSplit.ClosestParam(e.Curve, p);
            paramCache[(e, p)] = t;
            return t;
        }
        var nt = new List<(int, int, int)>(fm.T.Count * 4);
        foreach (var (a, b, c) in fm.T)
        {
            int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
            nt.Add((a, ab, ca)); nt.Add((ab, b, bc)); nt.Add((ca, bc, c)); nt.Add((ab, bc, ca));
        }
        fm.T.Clear(); fm.T.AddRange(nt);
    }

    static double MaxDiff(Matrix3d a, Matrix3d b) =>
        new[] { a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13, a.M22 - b.M22, a.M23 - b.M23, a.M33 - b.M33 }.Max(Math.Abs);

    /// <summary>Моменты авторитетного многогранника (фасеты граней; грань без фасет — своя разметка) и наибольший отход фасет.</summary>
    static Raw Polyhedron(TSolid solid, Vector3d o, out double chord, out double area)
    {
        var aud = GeometryAudit.Of(solid);
        chord = aud.Faces.Count == 0 ? 0 : aud.Faces.Max(f => double.IsNaN(f.ChordError) ? 0 : f.ChordError);
        area = 0;
        var list = new List<FaceMesh>();
        foreach (var f in solid.Faces)
        {
            var fm = new FaceMesh();
            if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0)
            {
                foreach (var (a, b, c) in fs.Facets.ReadView)
                {
                    int i = fm.V.Count; fm.V.Add(a);
                    if (f.Reversed) { fm.V.Add(c); fm.V.Add(b); } else { fm.V.Add(b); fm.V.Add(c); }
                    fm.T.Add((i, i + 1, i + 2));
                }
            }
            else
            {
                var mb = new MeshBuilder();
                try { f.Surface.TessellateFace(f, mb); } catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { continue; }
                var md = new MeshData(); mb.FillInto(md);
                fm.V.AddRange(mb.PositionsD);
                for (int i = 0; i + 2 < md.Indices.Length; i += 3) fm.T.Add((md.Indices[i], md.Indices[i + 1], md.Indices[i + 2]));
            }
            foreach (var (a, b, c) in fm.T) area += Vector3d.Cross(fm.V[b] - fm.V[a], fm.V[c] - fm.V[a]).Length / 2;
            list.Add(fm);
        }
        return Mesh(list, o);
    }

    static Raw Mesh(List<FaceMesh> meshes, Vector3d o)
    {
        double vol = 0, xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        var acc = Vector3d.Zero;
        static double Sq(double a1, double b1, double c1) => a1 * a1 + b1 * b1 + c1 * c1 + a1 * b1 + a1 * c1 + b1 * c1;
        static double Pr(double a1, double b1, double c1, double a2, double b2, double c2) =>
            2 * (a1 * a2 + b1 * b2 + c1 * c2) + a1 * b2 + b1 * a2 + a1 * c2 + c1 * a2 + b1 * c2 + c1 * b2;
        foreach (var fm in meshes)
            foreach (var (ia, ib, ic) in fm.T)
            {
                var a = fm.V[ia] - o; var b = fm.V[ib] - o; var c = fm.V[ic] - o;
                double v = Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;
                vol += v; acc += (a + b + c) * (v / 4);
                xx += v * Sq(a.X, b.X, c.X) / 10; yy += v * Sq(a.Y, b.Y, c.Y) / 10; zz += v * Sq(a.Z, b.Z, c.Z) / 10;
                xy += v * Pr(a.X, b.X, c.X, a.Y, b.Y, c.Y) / 20; xz += v * Pr(a.X, b.X, c.X, a.Z, b.Z, c.Z) / 20; yz += v * Pr(a.Y, b.Y, c.Y, a.Z, b.Z, c.Z) / 20;
            }
        return new Raw { V = vol, M1 = acc, S = new Matrix3d(xx, xy, xz, xy, yy, yz, xz, yz, zz) };
    }
}
