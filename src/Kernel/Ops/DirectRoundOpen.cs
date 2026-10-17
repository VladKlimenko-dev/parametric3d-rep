using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_DROPEN=0: незамкнутое ребро между кривыми гранями — отказ прямого построителя (прежний путь резцов).</summary>
    static readonly bool OpenOn = Environment.GetEnvironmentVariable("P3D_DROPEN") != "0";
    public static long OpenBuilt, OpenDeclined;
    /// <summary>KRN-06: точки края ленты на торцах и точные сечения на них (цена точки края — сечений на точку).</summary>
    public static long OpenEndPoints, OpenEndSections;
    [ThreadStatic] static long genCount;

    /// <summary>
    /// KRN-06 (06.12, 06.14): СКРУГЛЕНИЕ НЕЗАМКНУТОГО РЕБРА МЕЖДУ ГРАНЯМИ-КВАДРИКАМИ (плоскость, цилиндр, конус, шар, тор; хотя бы
    /// одна грань кривая): кромка отверстия в половине вала, стык бобышки у торца, ребро цилиндр/конус, кончающееся на плоскости.
    /// Катящийся шар — как у замкнутых рёбер (CylCylBlends): хребет — точка на расстояниях σr от обеих поверхностей в плоскости
    /// поперёк ребра, линии касания — основания перпендикуляров, сечение ленты — дуга радиуса r. Новое — ОКОНЧАНИЯ: в каждом
    /// конце ребра сходятся три грани (A, B и торец E, тоже квадрика); лента продлевается за конец ребра (те же поверхности A и B
    /// продолжены, хребет — тот же) и обрезается поверхностью торца, как у OpenCascade: край ленты на торце — кривая
    /// «лента ∩ E» (точка сечения v — корень f_E(сечение(t)(v)) = 0 по параметру хребта, строго в скобке со сменой знака),
    /// линии касания кончаются на рёбрах A∩E и B∩E — те укорачиваются, у торца угол заменяется краем ленты.
    /// Параметр — угол вокруг оси одной из форм (монотонный вдоль ребра), точки ребра и продления — Ньютоном на A ∩ B в
    /// полуплоскости угла. Мутация P3D_DROPEN=0.
    /// </summary>
    static TSolid? BuildOpen(TSolid body, IReadOnlyList<EdgeRef> refs, double r, ref string? why, Func<TEdge, TFace, double>? chDist = null, RadiusLaw? varLaw = null)
    {
        if (!OpenOn) { why = "open curved edges: off"; return null; }
        var sel = new List<TEdge>();
        var whole = FilletOp.WholeNamedEdges(new List<TSolid> { body }, refs);
        foreach (var rf in refs)
        {
            var e = FilletOp.Resolve(new List<TSolid> { body }, rf, out _) ?? (whole.TryGetValue(rf, out var we) ? we : null);
            if (e == null) { why = "edge not resolved"; return null; }
            if ((e.V0.P - e.V1.P).Length < 1e-7) { why = "open curved: closed edge"; return null; }
            if (!sel.Contains(e)) sel.Add(e);
        }
        // 06.12: СТЫК ЦИЛИНДРОВ РАВНОГО РАДИУСА — половины эллипса между сёдлами (DirectRoundEqualCyl)
        if (EqualCylOn && chDist == null && varLaw == null)
        {
            string? wq = null;
            var eq = EqualCylTee(body, sel, r, ref wq);
            if (eq != null) return eq;
            Debug?.Invoke("open: equal cylinders declined: " + wq); if (Environment.GetEnvironmentVariable("P3D_EQCYLDBG") == "1") Console.WriteLine("   eqcyl: " + wq);
        }
        // ЦЕПОЧКИ ЧЕРЕЗ ВЕРШИНЫ СТЕПЕНИ 2: булева делит кривую пересечения точками, где сходятся лишь те же две грани (точка
        // затравки, край куска NURBS) — такое ребро одно по смыслу; звенья сливаются в одно (кривая — подряд по звеньям).
        // Мутация P3D_DRDEG2=0 — отказ, как прежде.
        var allE = body.Edges.ToList();
        List<TEdge> At(Vector3d p) => allE.Where(x => (x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7).ToList();
        HashSet<TFace> FacesOf(TEdge x) => x.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().ToHashSet();
        var chains = new List<List<TEdge>>(); var used = new HashSet<TEdge>();
        foreach (var e in sel)
        {
            if (used.Contains(e)) continue;
            var ch = new List<TEdge> { e }; used.Add(e);
            var fe = FacesOf(e);
            foreach (bool front in new[] { false, true })
                for (int guard = 0; guard < 64; guard++)
                {
                    var endE = front ? ch[0] : ch[^1];
                    var other = ChainEnds(ch)[front ? 0 : 1];
                    var at = At(other);
                    if (!Deg2 || at.Count != 2) break;
                    var nx = at.First(x => x != endE);
                    if (used.Contains(nx) || !FacesOf(nx).SetEquals(fe)) break;
                    if ((nx.V0.P - nx.V1.P).Length < 1e-7) break;
                    if (front) ch.Insert(0, nx); else ch.Add(nx);
                    used.Add(nx);
                }
            chains.Add(ch);
        }
        foreach (var c in chains)
            foreach (var p in ChainEnds(c))
                if (chains.Any(o => o != c && o.Any(x => (x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7))) { why = "open curved: selected edges share a vertex"; return null; }
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;
        var blends = new List<FDesc>();
        foreach (var c in chains)
            if (!OpenBlend(body, c, byFace, blends, r, ref why, chDist, varLaw)) return null;
        return FinishDirect(faces, blends, chDist != null ? "chamfer.direct" : "round.direct", ref why);
    }

    static readonly bool Deg2 = Environment.GetEnvironmentVariable("P3D_DRDEG2") != "0";
    static readonly bool ExactTangent = Environment.GetEnvironmentVariable("P3D_DRTANGENT") != "0";
    static readonly bool RootOrder = Environment.GetEnvironmentVariable("P3D_DRROOTORDER") != "0";
    static readonly bool EndSeed = Environment.GetEnvironmentVariable("P3D_DRENDSEED") != "0";
    static readonly bool EndTab = Environment.GetEnvironmentVariable("P3D_DRENDTAB") != "0";
    static readonly bool MemoOn = Environment.GetEnvironmentVariable("P3D_DRMEMO") != "0";
    const int MemoCap = 1 << 18;

    static readonly bool ExtendEnds = Environment.GetEnvironmentVariable("P3D_DREXTEND") != "0";
    static readonly bool OpenPlanePlane = Environment.GetEnvironmentVariable("P3D_DROPENPP") != "0";
    static readonly bool LawBreaks = Environment.GetEnvironmentVariable("P3D_DRLAWBREAKS") != "0";

    /// <summary>
    /// ЗАКОН РАДИУСА вдоль ребра (06.2, 06.14): r(σ), σ — доля длины ребра от конца S ссылки (0) до конца E (1); за концами —
    /// продолжение (у кусочного закона — по крайней касательной). Линейный — две точки; общий — точки (σ, r), сплайн
    /// Эрмита с касательными по соседям (без выбросов между точками: Фритч — Карлсон).
    /// </summary>
    public sealed class RadiusLaw
    {
        public Vector3d S, E; public Func<double, double> R = null!, dR = null!;
        /// <summary>Внутренние узлы закона (доли длины): там закон гладок лишь первого порядка.</summary>
        public double[] Knots = Array.Empty<double>();
        public static RadiusLaw Linear(Vector3d s, double r0, Vector3d e, double r1) =>
            new() { S = s, E = e, R = f => r0 + (r1 - r0) * f, dR = _ => r1 - r0 };
        public static RadiusLaw? Points(Vector3d s, Vector3d e, IReadOnlyList<(double F, double R)> pts)
        {
            var p = pts.OrderBy(x => x.F).ToArray(); int n = p.Length;
            if (n < 2 || p[0].F > 1e-12 || p[^1].F < 1 - 1e-12) return null;
            for (int i = 1; i < n; i++) if (!(p[i].F > p[i - 1].F)) return null;
            var d = new double[n]; var m = new double[n];
            for (int i = 0; i + 1 < n; i++) d[i] = (p[i + 1].R - p[i].R) / (p[i + 1].F - p[i].F);
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int i = 1; i + 1 < n; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            for (int i = 0; i + 1 < n; i++)
            {
                if (d[i] == 0) { m[i] = m[i + 1] = 0; continue; }
                double a = m[i] / d[i], b = m[i + 1] / d[i], h = a * a + b * b;
                if (h > 9) { double tt = 3 / Math.Sqrt(h); m[i] = tt * a * d[i]; m[i + 1] = tt * b * d[i]; }
            }
            (double V, double D) Ev(double f)
            {
                if (f <= p[0].F) return (p[0].R + m[0] * (f - p[0].F), m[0]);
                if (f >= p[^1].F) return (p[^1].R + m[n - 1] * (f - p[^1].F), m[n - 1]);
                int i = 0; while (i + 2 < n && f > p[i + 1].F) i++;
                double h = p[i + 1].F - p[i].F, u = (f - p[i].F) / h;
                double h00 = 2 * u * u * u - 3 * u * u + 1, h10 = u * u * u - 2 * u * u + u, h01 = -2 * u * u * u + 3 * u * u, h11 = u * u * u - u * u;
                double d00 = 6 * u * u - 6 * u, d10 = 3 * u * u - 4 * u + 1, d01 = -6 * u * u + 6 * u, d11 = 3 * u * u - 2 * u;
                return (h00 * p[i].R + h10 * h * m[i] + h01 * p[i + 1].R + h11 * h * m[i + 1],
                        (d00 * p[i].R + d10 * h * m[i] + d01 * p[i + 1].R + d11 * h * m[i + 1]) / h);
            }
            // узлы — все точки, концы тоже: за концами закон продолжается касательной, и там он гладок лишь первого порядка
            return new() { S = s, E = e, R = f => Ev(f).V, dR = f => Ev(f).D, Knots = p.Select(x => x.F).ToArray() };
        }
    }

    /// <summary>
    /// Конец кривой ребра торца у прежней вершины oldP переносится в точку касания q: укорочение (q на кривой) или ПРОДЛЕНИЕ
    /// (упор в стенку: q — на продолжении кривой за вершиной; отрезок, дуга, эллипс — по своей формуле; мутация
    /// P3D_DREXTEND=0 — отказ). Другие кривые — только укорочение.
    /// </summary>
    static ICurve3? Reshape(ICurve3 c, Vector3d oldP, Vector3d q, out string? why)
    {
        why = null;
        bool startIsOld = (c.PointAt(c.T0) - oldP).Length < (c.PointAt(c.T1) - oldP).Length;
        double tq = CurveParam.Closest(c, q);
        bool interior = (c.PointAt(tq) - q).Length <= 1e-7 && tq > Math.Min(c.T0, c.T1) + 1e-12 && tq < Math.Max(c.T0, c.T1) - 1e-12;
        if (interior)
        {
            double lo = startIsOld ? tq : c.T0, hi = startIsOld ? c.T1 : tq;
            if (!(hi - lo > 1e-9 * Math.Max(1, Math.Abs(c.T1 - c.T0)))) { why = "blend wider than the end edge"; return null; }
            return Analytic.GeneralBoolean.SubCurve(c, lo, hi);
        }
        if (!ExtendEnds) { why = "contact end is not on the end edge"; return null; }
        switch (c)
        {
            case LineCurve3 ln:
            {
                var d = (ln.B - ln.A).Normalized(); var w = q - ln.A;
                if ((w - d * Vector3d.Dot(w, d)).Length > 1e-7) { why = "contact end is off the end edge line"; return null; }
                return startIsOld ? new LineCurve3(q, ln.B) : new LineCurve3(ln.A, q);
            }
            case ArcCurve3 arc:
            {
                var f = arc.Frame; var w = q - f.Origin;
                if (Math.Abs(Vector3d.Dot(w, f.W.Normalized())) > 1e-7 || Math.Abs(w.Length - arc.Radius) > 1e-7) { why = "contact end is off the end edge circle"; return null; }
                double th = Math.Atan2(Vector3d.Dot(w, f.V.Normalized()), Vector3d.Dot(w, f.U.Normalized()));
                double aOld = startIsOld ? arc.A0 : arc.A1;
                th += 2 * Math.PI * Math.Round((aOld - th) / (2 * Math.PI));
                return startIsOld ? new ArcCurve3(f, arc.Radius, th, arc.A1) : new ArcCurve3(f, arc.Radius, arc.A0, th);
            }
            case EllipseCurve3 el:
            {
                var f = el.Frame; var w = q - f.Origin;
                double th = Math.Atan2(Vector3d.Dot(w, f.V.Normalized()) / el.RV, Vector3d.Dot(w, f.U.Normalized()) / el.RU);
                double aOld = startIsOld ? el.A0 : el.A1;
                th += 2 * Math.PI * Math.Round((aOld - th) / (2 * Math.PI));
                var ne = startIsOld ? new EllipseCurve3(f, el.RU, el.RV, th, el.A1) : new EllipseCurve3(f, el.RU, el.RV, el.A0, th);
                if ((ne.PointAt(startIsOld ? ne.T0 : ne.T1) - q).Length > 1e-7) { why = "contact end is off the end edge ellipse"; return null; }
                return ne;
            }
        }
        why = "contact end is not on the end edge (extension of this curve type not supported)";
        return null;
    }

    /// <summary>Концы цепочки рёбер (по порядку звеньев).</summary>
    static Vector3d[] ChainEnds(List<TEdge> c)
    {
        if (c.Count == 1) return new[] { c[0].V0.P, c[0].V1.P };
        static bool Touch(TEdge x, Vector3d p) => (x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7;
        var a = Touch(c[1], c[0].V0.P) ? c[0].V1.P : c[0].V0.P;
        var b = Touch(c[^2], c[^1].V0.P) ? c[^1].V1.P : c[^1].V0.P;
        return new[] { a, b };
    }

    /// <summary>Кривая цепочки: звенья подряд от начала P0; параметр t ∈ [0, n], кусок k — звено k (по его параметру, в сторону цепочки).</summary>
    static ICurve3 ChainCurve(List<TEdge> c, Vector3d p0)
    {
        if (c.Count == 1) return c[0].Curve;
        var pieces = new List<(ICurve3 C, bool Fwd)>(); var at = p0;
        foreach (var x in c)
        {
            var cv = x.Curve; bool fwd = (cv.PointAt(cv.T0) - at).Length <= (cv.PointAt(cv.T1) - at).Length;
            pieces.Add((cv, fwd)); at = fwd ? cv.PointAt(cv.T1) : cv.PointAt(cv.T0);
        }
        Vector3d F(double t)
        {
            int k = Math.Clamp((int)Math.Floor(t), 0, pieces.Count - 1); double f = Math.Clamp(t - k, 0, 1);
            var (cv, fwd) = pieces[k]; return cv.PointAt(fwd ? cv.T0 + (cv.T1 - cv.T0) * f : cv.T1 - (cv.T1 - cv.T0) * f);
        }
        return new ProcCurve3(F, 0, pieces.Count, false, "edge chain");
    }

    static bool OpenBlend(TSolid body, List<TEdge> chain, Dictionary<TFace, FDesc> byFace, List<FDesc> blends, double r, ref string? why, Func<TEdge, TFace, double>? chDist = null, RadiusLaw? varLaw = null)
    {
        var e = chain[0];
        var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (fs.Count != 2) { why = "open curved: edge is not between two faces"; return false; }
        var FA = byFace[fs[0]]; var FB = byFace[fs[1]];
        var shA = BlendShapeOf(FA.Surf); var shB = BlendShapeOf(FB.Surf);
        if (shA == null || shB == null) { why = "open curved: a face is not a plane, cylinder, cone, sphere or torus"; return false; }
        // плоскость/плоскость: обычно — свой построитель (DirectRound.Build); сюда — когда он отказал (кривые торцы, закон
        // радиуса); P3D_DROPENPP=0 — отказ, как прежде
        if (shA is Geometry.Ssi.Plane && shB is Geometry.Ssi.Plane && !OpenPlanePlane) { why = "open curved: plane/plane edge"; return false; }
        var chEnds = ChainEnds(chain);
        var E = ChainCurve(chain, chEnds[0]);
        bool fwd = (E.PointAt(E.T0) - chEnds[0]).Length <= (E.PointAt(E.T0) - chEnds[1]).Length;
        Vector3d P0 = fwd ? chEnds[0] : chEnds[1], P1 = fwd ? chEnds[1] : chEnds[0];    // начало и конец по параметру кривой
        var chainSet = chain.ToHashSet();
        double OffChain(Vector3d q) => chain.Min(x => (x.Curve.PointAt(CurveParam.Closest(x.Curve, q)) - q).Length);

        // ОКОНЧАНИЯ: в каждой вершине — ровно три ребра (e, A∩E, B∩E), торец E — квадрика
        var allEdges = body.Edges.ToList();
        var ends = new (FDesc FE, Geometry.Ssi.Shape ShE, TEdge EA, TEdge EB, Vector3d V, double SE)[2];
        for (int k = 0; k < 2; k++)
        {
            var V = k == 0 ? P0 : P1;
            var at = allEdges.Where(x => !chainSet.Contains(x) && ((x.V0.P - V).Length < 1e-7 || (x.V1.P - V).Length < 1e-7)).ToList();
            if (at.Count != 2) { why = $"open curved: end vertex of degree {at.Count + 1} (three faces expected)"; return false; }
            TFace? Other(TEdge x, TFace f) { var xf = x.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList(); return xf.Count == 2 && xf.Contains(f) ? xf.First(y => y != f) : null; }
            var eA = at.FirstOrDefault(x => Other(x, fs[0]) is TFace o && o != fs[1]);
            var eB = at.FirstOrDefault(x => x != eA && Other(x, fs[1]) is TFace o && o != fs[0]);
            if (eA == null || eB == null) { why = "open curved: end edges not found"; return false; }
            var fe = Other(eA, fs[0])!;
            if (Other(eB, fs[1]) != fe) { why = "open curved: the two end edges are on different faces"; return false; }
            if ((eA.V0.P - eA.V1.P).Length < 1e-7 || (eB.V0.P - eB.V1.P).Length < 1e-7) { why = "open curved: closed end edge"; return false; }
            var FE = byFace[fe]; var shE = BlendShapeOf(FE.Surf);
            if (shE == null) { why = "open curved: end face is not a quadric"; return false; }
            double sE = Vector3d.Dot(NormalOf(FE, V), Geometry.Ssi.Implicit(shE, V).G) > 0 ? 1 : -1;
            ends[k] = (FE, shE, eA, eB, V, sE);
        }
        if (ends[0].FE == FA || ends[0].FE == FB || ends[1].FE == FA || ends[1].FE == FB) { why = "open curved: end face is a face of the edge"; return false; }

        var Pm = E.PointAt((E.T0 + E.T1) / 2);
        double sA = Vector3d.Dot(NormalOf(FA, Pm), Geometry.Ssi.Implicit(shA, Pm).G) > 0 ? 1 : -1;
        double sB = Vector3d.Dot(NormalOf(FB, Pm), Geometry.Ssi.Implicit(shB, Pm).G) > 0 ? 1 : -1;
        double sizeMin = 1e9;
        foreach (var sh in new[] { shA, shB })
            sizeMin = Math.Min(sizeMin, sh switch { Geometry.Ssi.Cylinder c => c.R, Geometry.Ssi.Sphere sp => sp.R, Geometry.Ssi.Torus to => to.r, _ => 1e9 });

        // выпуклость — пробой точки в теле (как у замкнутых рёбер)
        List<(Vector3d A, Vector3d B, Vector3d C)>? tris = null;
        int? sigmaVote = null;
        foreach (double f in new[] { 0.25, 0.5, 0.75 })
        {
            var p = E.PointAt(E.T0 + (E.T1 - E.T0) * f);
            var nA = NormalOf(FA, p); var nB = NormalOf(FB, p);
            tris ??= SolidTriangles(body);
            var d = nB - nA; if (d.Length < 1e-6) { why = "open curved: tangent faces"; return false; }
            double eps = Math.Min(r, 0.05 * Math.Min(sizeMin, 1e3)) / 2;
            bool? ins = InsideMesh(tris, p + d.Normalized() * eps);
            if (ins == null) { why = "open curved: convexity probe undecided"; return false; }
            int sg = ins.Value ? 1 : -1;
            if (sigmaVote != null && sigmaVote != sg) { why = "open curved: convexity changes along the edge"; return false; }
            sigmaVote = sg;
        }
        double sigma = sigmaVote!.Value;
        double offA = sigma * sA * r, offB = sigma * sB * r;
        // ПЕРЕМЕННЫЙ РАДИУС (06.14): r(t) задаётся позже (по длине ребра); до того — постоянный r (наибольший — для проверок)
        Func<double, double> Rt = _ => r, dRt = _ => 0;
        Func<double, double>? fracOf = null;
        var knotT = new List<double>();                  // параметры хребта узлов закона радиуса
        double OffA(double t) => sigma * sA * Rt(t);
        double OffB(double t) => sigma * sB * Rt(t);
        bool ch = chDist != null; double dA = ch ? chDist!(e, fs[0]) : r, dB = ch ? chDist!(e, fs[1]) : r;
        foreach (var (sh, off) in ch ? Array.Empty<(Geometry.Ssi.Shape, double)>() : new[] { (shA, offA), (shB, offB) })
            if (sh is Geometry.Ssi.Cylinder cy0 && cy0.R + off < 1e-6 || sh is Geometry.Ssi.Sphere sp0 && sp0.R + off < 1e-6 || sh is Geometry.Ssi.Torus to0 && to0.r + off < 1e-6)
            { why = "open curved: radius too large for a face"; return false; }

        // ПАРАМЕТР: угол вокруг оси формы, монотонный вдоль ребра; t = 0 — начало ребра, t = Δ — конец, вне [0, Δ] — продление
        Func<double, Vector3d>? edgeAt = null; double Delta = 0, rad0 = 0, rad1 = 0;
        foreach (var shW in new[] { shB, shA })
        {
            if (AxisOf(shW) is not { } ax) continue;
            var W = ax.W; var helper = Math.Abs(W.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
            var U = Vector3d.Cross(helper, W).Normalized(); var Vv = Vector3d.Cross(W, U); var O = ax.O;
            double Ang(Vector3d q) { var d = q - O; return Math.Atan2(Vector3d.Dot(d, Vv), Vector3d.Dot(d, U)); }
            double Rad(Vector3d q) { var d = q - O; return (d - W * Vector3d.Dot(d, W)).Length; }
            const int m = 512;
            var smp = Enumerable.Range(0, m + 1).Select(i => E.PointAt(E.T0 + (E.T1 - E.T0) * i / m)).ToList();
            var ang = new double[m + 1]; ang[0] = Ang(smp[0]);
            int sgn = 0; bool mono = true;
            for (int i = 1; i <= m; i++)
            {
                double dphi = Ang(smp[i]) - Ang(smp[i - 1]); while (dphi > Math.PI) dphi -= 2 * Math.PI; while (dphi < -Math.PI) dphi += 2 * Math.PI;
                int si = Math.Sign(dphi); if (si == 0 || (sgn != 0 && si != sgn)) { mono = false; break; }
                sgn = si; ang[i] = ang[i - 1] + dphi;
            }
            double span = Math.Abs(ang[m] - ang[0]);
            if (!mono || span < 1e-6 || span > 2 * Math.PI - 1e-3) continue;
            // обусловленность: касательная ребра пересекает полуплоскость угла (ребро вдоль оси — почти в ней: Ньютон плох)
            {
                double worstCross = 1;
                for (int i = 0; i < m; i += 8)
                {
                    var tg = (smp[i + 1] - smp[i]).Normalized(); var d = smp[i] - O; var rad = (d - W * Vector3d.Dot(d, W)).Normalized();
                    worstCross = Math.Min(worstCross, Math.Abs(Vector3d.Dot(tg, Vector3d.Cross(W, rad))));
                }
                if (worstCross < 0.3) { Debug?.Invoke($"open: axis {W:0.###} ill-conditioned (tangent across the angle half-plane {worstCross:G3})"); continue; }
            }
            if (Rad(smp[0]) < 1e-6 || Rad(smp[m]) < 1e-6) continue;
            double ph0 = ang[0]; double dir = sgn;
            var tan0 = (smp[1] - smp[0]).Normalized(); var tan1 = (smp[m] - smp[m - 1]).Normalized();
            double r0 = Rad(smp[0]), r1 = Rad(smp[m]);
            Vector3d Seed(double t)
            {
                if (t <= 0) return smp[0] + tan0 * (t * r0);
                if (t >= span) return smp[m] + tan1 * ((t - span) * r1);
                double a = ph0 + dir * t;
                int lo = 0, hi = m;
                while (hi - lo > 1) { int mid = (lo + hi) / 2; if ((ang[mid] - a) * dir <= 0) lo = mid; else hi = mid; }
                double den = ang[hi] - ang[lo]; double fr = Math.Abs(den) < 1e-300 ? 0 : Math.Clamp((a - ang[lo]) / den, 0, 1);
                return smp[lo] + (smp[hi] - smp[lo]) * fr;
            }
            Vector3d At(double t)
            {
                double phi = ph0 + dir * t;
                var nphi = -U * Math.Sin(phi) + Vv * Math.Cos(phi); var uphi = U * Math.Cos(phi) + Vv * Math.Sin(phi);
                var x = Seed(t);
                for (int it = 0; it < 40; it++)
                {
                    var (f1, g1) = Geometry.Ssi.Implicit(shA, x); var (f2, g2) = Geometry.Ssi.Implicit(shB, x);
                    double f3 = Vector3d.Dot(x - O, nphi);
                    var dx = Solve3(new[] { g1, g2, nphi }, new[] { -f1, -f2, -f3 });
                    if (dx == null) return new Vector3d(double.NaN);
                    x += dx.Value;
                    if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
                }
                var (e1, _) = Geometry.Ssi.Implicit(shA, x); var (e2, _) = Geometry.Ssi.Implicit(shB, x);
                if (Math.Abs(e1) > 1e-10 || Math.Abs(e2) > 1e-10 || Vector3d.Dot(x - O, uphi) < 0) return new Vector3d(double.NaN);
                return x;
            }
            bool ok = (At(0) - smp[0]).Length < 1e-7 && (At(span) - smp[m]).Length < 1e-7;
            for (int i = 0; i < 64 && ok; i++)
            {
                var q = At(span * (i + 0.37) / 64);
                if (double.IsNaN(q.X) || OffChain(q) > 1e-6) ok = false;
            }
            if (!ok) continue;
            edgeAt = At; Delta = span; rad0 = r0; rad1 = r1;
            break;
        }
        if (edgeAt == null && GenericParam)
        {
            // ОБЩИЙ ПАРАМЕТР (у граней нет подходящей оси — NURBS, ребро без обхода оси): t — миллиметры вдоль гладкой опорной
            // кривой (многочлен Чебышёва по доле длины ребра); точка ребра — Ньютон на A ∩ B в плоскости поперёк опорной кривой.
            // Продление за концы — тот же многочлен. Мутация P3D_DRGENPARAM=0 — отказ, как прежде.
            var rcFit = RefCurve.Fit(E, 1e-3 * Math.Clamp(r, 0.1, 1));   // опорная кривая задаёт лишь плоскости сечений: точность — доли радиуса
            if (rcFit == null) Debug?.Invoke("open: reference curve not fitted");
            if (rcFit is { } rc)
            {
                Vector3d AtG(double t)
                {
                    var c = rc.At(t); var d = rc.D(t).Normalized(); var x = c;
                    for (int it = 0; it < 40; it++)
                    {
                        var (f1, g1) = Geometry.Ssi.Implicit(shA, x); var (f2, g2) = Geometry.Ssi.Implicit(shB, x);
                        var dx = Solve3(new[] { g1, g2, d }, new[] { -f1, -f2, -Vector3d.Dot(x - c, d) });
                        if (dx == null || !double.IsFinite(dx.Value.X)) return new Vector3d(double.NaN);
                        x += dx.Value;
                        if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
                    }
                    var (e1, _) = Geometry.Ssi.Implicit(shA, x); var (e2, _) = Geometry.Ssi.Implicit(shB, x);
                    if (Math.Abs(e1) > 1e-10 || Math.Abs(e2) > 1e-10 || (x - c).Length > 0.25 * Math.Max(r, 1e-3)) return new Vector3d(double.NaN);
                    return x;
                }
                bool ok = (AtG(0) - P0).Length < 1e-5 * Math.Max(1, rc.Length) && (AtG(rc.Length) - P1).Length < 1e-5 * Math.Max(1, rc.Length);
                for (int i = 0; i < 64 && ok; i++)
                {
                    var q = AtG(rc.Length * (i + 0.37) / 64);
                    if (double.IsNaN(q.X) || OffChain(q) > 1e-6) ok = false;
                }
                if (ok) { edgeAt = AtG; Delta = rc.Length; rad0 = rad1 = 1; }
                else Debug?.Invoke($"open: generic parameter rejected: ends {(AtG(0) - P0).Length:E2} {(AtG(rc.Length) - P1).Length:E2}");
            }
        }
        if (edgeAt == null) { why = "open curved: no smooth parameter for the edge (no axis, reference curve not fitted)"; return false; }
        // ПАМЯТЬ ВЫЧИСЛЕНИЙ: интегралы объёма и площади, разметка и p-кривые многократно спрашивают те же параметры (узлы
        // Гаусса — при каждом пересчёте объёма), а каждая точка — Ньютон на A ∩ B и хребет (у грани NURBS — основание
        // проекцией). Мутация P3D_DRMEMO=0.
        var edMemo = new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d>();
        var spMemo = new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d?>();
        var edF = edgeAt;
        Vector3d Ed(double t) => MemoOn && edMemo.Count < MemoCap ? edMemo.GetOrAdd(t, edF) : edF(t);
        // КАСАТЕЛЬНАЯ РЕБРА — точно: ∇A × ∇B в точке ребра, знак — по ходу t (один раз). Прежде — разность точек ребра с шагом
        // 1e-5: шум Ньютона 1e-15 давал ~1e-9 в направлении, плоскость хребта качалась, и через разность хребта c' (закон радиуса)
        // шум сечения ~1e-9 мм — корень края ленты не сходился (секущие, Иллинойс — по 20 сечений на точку края). Две лишние
        // точки ребра на вызов — тоже прочь. Мутация P3D_DRTANGENT=0 — прежняя разность.
        double tdSign = 0;
        Vector3d TdFd(double t) { double dl = 1e-5; return (edgeAt(t + dl) - edgeAt(t - dl)).Normalized(); }
        Vector3d Td(double t)
        {
            if (!ExactTangent) return TdFd(t);
            var P = Ed(t);
            var c = Vector3d.Cross(Geometry.Ssi.Implicit(shA, P).G, Geometry.Ssi.Implicit(shB, P).G);
            if (!(c.Length > 1e-9)) return TdFd(t);
            if (tdSign == 0) { var fd = TdFd(0.5 * Delta); var Pm = Ed(0.5 * Delta); var cm = Vector3d.Cross(Geometry.Ssi.Implicit(shA, Pm).G, Geometry.Ssi.Implicit(shB, Pm).G); tdSign = Vector3d.Dot(cm, fd) >= 0 ? 1 : -1; }
            return c.Normalized() * tdSign;
        }

        if (varLaw is { } vl)
        {
            if (ch) { why = "variable chamfer: not in this family"; return false; }
            // ЗАКОН — линейно по длине ребра от одного конца к другому, за концами — продолжение той же прямой. s(t) — таблица
            // длин на [−Δ/2, 3Δ/2] (Гаусс по кускам) и кубическая Эрмита с точными производными |E'(t)| в узлах (гладко).
            bool p0IsS = (P0 - vl.S).Length <= (P0 - vl.E).Length;
            const int NS = 1024; double ta = -0.5 * Delta, tb = 1.5 * Delta, hs = (tb - ta) / NS;
            double Speed(double t) { double d = 1e-6 * Math.Max(1e-9, Delta); var a = Ed(t + d); var b = Ed(t - d); return (a - b).Length / (2 * d); }
            var sv = new double[NS + 1]; var dv = new double[NS + 1];
            double[] gx3 = { -0.7745966692414834, 0, 0.7745966692414834 }, gw3 = { 5.0 / 9, 8.0 / 9, 5.0 / 9 };
            for (int i = 0; i <= NS; i++)
            {
                dv[i] = Speed(ta + hs * i);
                if (i > 0) { double acc = 0; for (int q = 0; q < 3; q++) acc += gw3[q] / 2 * Speed(ta + hs * (i - 0.5 + gx3[q] / 2)); sv[i] = sv[i - 1] + acc * hs; }
            }
            if (sv.Any(double.IsNaN) || dv.Any(double.IsNaN)) { why = "variable radius: edge length along the extension not computed"; return false; }
            double Sof(double t, out double ds)
            {
                double x = (t - ta) / hs; int i = Math.Clamp((int)Math.Floor(x), 0, NS - 1); double u = x - i;
                double h00 = 2 * u * u * u - 3 * u * u + 1, h10 = u * u * u - 2 * u * u + u, h01 = -2 * u * u * u + 3 * u * u, h11 = u * u * u - u * u;
                double d00 = 6 * u * u - 6 * u, d10 = 3 * u * u - 4 * u + 1, d01 = -6 * u * u + 6 * u, d11 = 3 * u * u - 2 * u;
                ds = (d00 * sv[i] + d10 * hs * dv[i] + d01 * sv[i + 1] + d11 * hs * dv[i + 1]) / hs;
                return h00 * sv[i] + h10 * hs * dv[i] + h01 * sv[i + 1] + h11 * hs * dv[i + 1];
            }
            double s0 = Sof(0, out _), L = Sof(Delta, out _) - s0;
            if (!(L > 0)) { why = "variable radius: zero edge length"; return false; }
            // доля длины от конца S ссылки: σ = (s − s0)/L от P0, если P0 — S, иначе 1 − (s − s0)/L
            double Frac(double t, out double dFrac) { double sv = Sof(t, out var ds); dFrac = (p0IsS ? 1 : -1) * ds / L; return p0IsS ? (sv - s0) / L : 1 - (sv - s0) / L; }
            Rt = t => vl.R(Frac(t, out _));
            fracOf = t => Frac(t, out _);
            foreach (var kf in vl.Knots)
            {
                double a = -0.5 * Delta, b = 1.5 * Delta, fa = fracOf(a) - kf, fb = fracOf(b) - kf;
                if (fa * fb > 0) continue;
                for (int it = 0; it < 100; it++) { double m = (a + b) / 2, fm = fracOf(m) - kf; if (fa * fm <= 0) { b = m; fb = fm; } else { a = m; fa = fm; } }
                knotT.Add((a + b) / 2);
            }
            dRt = t => { double f = Frac(t, out var df); return vl.dR(f) * df; };
            for (int i = 0; i <= 64; i++) if (!(vl.R(i / 64.0) > 0)) { why = "variable radius: non-positive radius on the edge"; return false; }
        }
        Vector3d? Spine(double t) => MemoOn && spMemo.Count < MemoCap ? spMemo.GetOrAdd(t, SpineCore) : SpineCore(t);
        Vector3d? SpineCore(double t)
        {
            using var _ps = Diagnostics.Prof.Time("open: SpineCore");
            var P = Ed(t); if (double.IsNaN(P.X)) return null;
            var tg = Td(t);
            var nA = Geometry.Ssi.Implicit(shA, P).G * sA; var nB = Geometry.Ssi.Implicit(shB, P).G * sB; double c = Vector3d.Dot(nA, nB);
            double rr = Rt(t), oA = OffA(t), oB = OffB(t);
            var x = P + (nA + nB) * (sigma * rr / (1 + c));
            for (int it = 0; it < 60; it++)
            {
                var (f1, gA) = Geometry.Ssi.Implicit(shA, x); var (f2, gB) = Geometry.Ssi.Implicit(shB, x);
                double f3 = Vector3d.Dot(x - P, tg);
                var dx = Solve3(new[] { gA, gB, tg }, new[] { oA - f1, oB - f2, -f3 });
                if (dx == null) return null;
                x += dx.Value;
                if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
            }
            double ea = Geometry.Ssi.Implicit(shA, x).F - oA, eb = Geometry.Ssi.Implicit(shB, x).F - oB;
            if (Math.Abs(ea) > 1e-9 * Math.Max(1, rr) || Math.Abs(eb) > 1e-9 * Math.Max(1, rr) || Math.Abs(Vector3d.Dot(x - P, tg)) > 1e-9 * Math.Max(1, rr)) return null;
            return x;
        }
        Vector3d Foot(Geometry.Ssi.Shape sh, Vector3d x) { var (fv, g) = Geometry.Ssi.Implicit(sh, x); return x - g * fv; }
        Vector3d S(double t) => Spine(t) ?? new Vector3d(double.NaN);
        Vector3d ChA(double t) => ChamferPoint(shA, shB, sA, sB, sigma, Ed(t), Td(t), dA);
        Vector3d ChB(double t) => ChamferPoint(shB, shA, sB, sA, sigma, Ed(t), Td(t), dB);
        Vector3d QA(double t) => ch ? ChA(t) : Foot(shA, S(t));
        Vector3d QB(double t) => ch ? ChB(t) : Foot(shB, S(t));
        SweepSection Gen(double t)
        {
            using var _pg = Diagnostics.Prof.Time("open: Gen");
            genCount++;
            if (ch) return SweepSection.Line(ChA(t), ChB(t));
            var x = S(t); var qa = Foot(shA, x); var qb = Foot(shB, x);
            // характеристическая окружность огибающей шаров: в плоскости (y − c)·c' = −r r', центр c − (r r'/|c'|²) c',
            // радиус √(r² − (r r')²/|c'|²); линии касания на ней лежат (d/dt dist(c, A) = r'). Постоянный радиус — большой круг.
            var cc = x; double rad = Rt(t);
            if (varLaw != null)
            {
                // c'(t): центральная разность, у узла закона — односторонняя второго порядка по своей стороне (центральная через
                // узел сглаживает излом r' и портит формулу сечения на ±h)
                double hh = 1e-4 * Math.Max(1e-9, Delta);
                Vector3d dc;
                double? kn = knotT.Count == 0 ? null : knotT.Select(k => (double?)k).OrderBy(k => Math.Abs(k!.Value - t)).First();
                if (kn is double kt && Math.Abs(kt - t) < 2.5 * hh)
                {
                    double sg = t >= kt ? 1 : -1;
                    dc = (S(t) * (-3) + S(t + sg * hh) * 4 - S(t + sg * 2 * hh)) / (2 * hh) * sg;
                }
                else { var x1 = S(t + hh); var x0 = S(t - hh); dc = (x1 - x0) / (2 * hh); }
                double k = rad * dRt(t) / dc.LengthSquared;
                cc = x - dc * k; rad = Math.Sqrt(Math.Max(0, rad * rad - k * k * dc.LengthSquared));
            }
            var e1 = (qa - cc).Normalized(); var v2 = (qb - cc).Normalized();
            var e2 = v2 - e1 * Vector3d.Dot(e1, v2);
            double phi = Math.Atan2(e2.Length, Vector3d.Dot(e1, v2));
            return SweepSection.Arc(cc, e1, e2.Normalized(), rad, phi);
        }
        bool NoEvents(bool onA, Geometry.Ssi.Shape s, double ta, double tb)
        {
            var sh = onA ? shA : shB; double off = onA ? offA : offB;
            bool Seg(double u0, double u1, Vector3d x0, Vector3d x1, int depth)
            {
                var mm = (x0 + x1) / 2; double rho = (x1 - x0).Length / 2 * 1.01 + 1e-12;
                bool tr = Geometry.Ssi.Transversal(shA, shB, mm, rho);
                if (tr && Geometry.Ssi.NormalLip(sh, mm, rho) is double kap)
                {
                    var (fm, gm) = Geometry.Ssi.Implicit(sh, mm);
                    var qm = mm - gm * off; double R = rho * (1 + Math.Abs(off) * kap) + Math.Abs(fm - off);
                    if (Math.Abs(Geometry.Ssi.Implicit(s, qm).F) > R * (1 + 1e-12) + 1e-12) return true;
                }
                if (rho < 1e-6 || depth > 30) return false;
                double um = (u0 + u1) / 2; var xm = Spine(um); if (xm == null) return false;
                if (tr && (xm.Value - mm).Length > rho) return false;
                return Seg(u0, um, x0, xm.Value, depth + 1) && Seg(um, u1, xm.Value, x1, depth + 1);
            }
            int n = 64;
            for (int i = 0; i < n; i++)
            {
                double u0 = ta + (tb - ta) * i / n, u1 = ta + (tb - ta) * (i + 1) / n;
                var x0 = Spine(u0); var x1 = Spine(u1); if (x0 == null || x1 == null) return false;
                if (!Seg(u0, u1, x0.Value, x1.Value, 0)) return false;
            }
            return true;
        }

        // ПРОДЛЕНИЕ И ОБРЕЗКА ТОРЦАМИ. f_k > 0 — снаружи торца k. За концом k сечение целиком снаружи, внутри — целиком внутри.
        // знак «за торцом»: обычно снаружи тела (ребро уходит сквозь торец); у упора в стенку (вогнутый конец — материал
        // за торцом) — внутри. Обрезка та же, функция ориентируется так, чтобы за концом было > 0.
        var endSign = new double[] { 1, 1 };
        double Fend(int k, Vector3d x) => endSign[k] * ends[k].SE * Geometry.Ssi.Implicit(ends[k].ShE, x).F;
        bool SectionSide(int k, double t, int want)
        {
            if (!ch && Spine(t) == null) return false;
            var g = Gen(t);
            for (int i = 0; i <= 16; i++) { double f = Fend(k, g.Point(i / 16.0)); if (double.IsNaN(f) || Math.Sign(f) != want) return false; }
            return true;
        }
        var outT = new double[2]; var inT = new double[2];
        for (int k = 0; k < 2; k++)
        {
            double dk = k == 0 ? -1 : 1, tEnd = k == 0 ? 0 : Delta;
            double speed = (Ed(tEnd - dk * 1e-4 * Delta) - Ed(tEnd)).Length / (1e-4 * Delta);
            if (!(speed > 0)) { why = "open curved: edge parameter degenerate at an end"; return false; }
            double step = Math.Min(2 * r / speed, 0.45 * Delta);
            double fBeyond = Fend(k, Ed(tEnd + dk * 1e-4 * step)), fBefore = Fend(k, Ed(tEnd - dk * 1e-4 * step));
            if (!(fBeyond * fBefore < 0)) { why = $"open curved: the edge does not cross the end face (end {k} at {ends[k].V}, face {ends[k].FE.Surf.GetType().Name} {ends[k].FE.Tag}, f {fBefore:E2}/{fBeyond:E2})"; return false; }
            if (fBeyond < 0) endSign[k] = -1;
            double h = step; bool found = false;
            for (int it = 0; it < 8 && !found; it++, h *= 2) if (SectionSide(k, tEnd + dk * h, 1)) { outT[k] = tEnd + dk * h; found = true; }
            if (!found) { why = "open curved: the blend does not leave the end face (end face too oblique?)"; return false; }
            h = step; found = false;
            for (int it = 0; it < 8 && !found; it++, h *= 2) if (SectionSide(k, tEnd - dk * h, -1)) { inT[k] = tEnd - dk * h; found = true; }
            if (!found) { why = "open curved: no blend section inside the end face"; return false; }
        }
        if (!(inT[0] < inT[1])) { why = "open curved: the edge is too short for the radius"; return false; }
        // корень f_k(сечение(t)(v)) по t в скобке [inT, outT] (Иллинойс), одна смена знака
        double Root(int k, Func<double, double> g) => RootIn(g, inT[k], outT[k]);
        double RootIn(Func<double, double> g, double a, double b)
        {
            double fa = g(a), fb = g(b);
            // скобка в любом порядке (у начального торца функция убывает по t): прежде узкая скобка по таблице края у него
            // всегда отказывала и точка искалась полным поиском. Мутация P3D_DRROOTORDER=0.
            if (RootOrder && fa > 0 && fb < 0) { (a, b) = (b, a); (fa, fb) = (fb, fa); }
            if (!(fa < 0 && fb > 0)) return double.NaN;
            int side = 0;
            for (int it = 0; it < 200; it++)
            {
                double c = (a * fb - b * fa) / (fb - fa), fc = g(c);
                if (double.IsNaN(fc)) return double.NaN;
                if (fc == 0 || Math.Abs(b - a) < 1e-15 * Math.Max(1, Math.Abs(c))) return c;
                if (fc < 0) { a = c; fa = fc; if (side == -1) fb /= 2; side = -1; }
                else { b = c; fb = fc; if (side == 1) fa /= 2; side = 1; }
                if (Math.Abs(b - a) < 1e-14 * Math.Max(1, Math.Abs(b))) return (a + b) / 2;
            }
            return (a + b) / 2;
        }
        double TStar(int k, double v) => Root(k, t => Fend(k, Gen(t).Point(v)));
        // край ленты: корень по таблице (65 проб v) как затравка и узкая скобка — несколько вычислений сечения вместо полусотни
        // (каждая точка края — в разметке, в объёме, в p-кривой). Мутация P3D_DRENDTAB=0 — полный поиск в каждой точке.
        double[][]? endTab = null; SectionSurface? seedSurf = null; double seedLo = 0, seedKU = 1;
        var endMemo = new[] { new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d>(), new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d>() };
        Vector3d EndPt(int k, double v) => MemoOn && endTab != null && endMemo[k].Count < MemoCap ? endMemo[k].GetOrAdd(v, vv => EndPtCore(k, vv)) : EndPtCore(k, v);
        double[] tEndA = { double.NaN, double.NaN }, tEndB = { double.NaN, double.NaN };
        Vector3d EndPtCore(int k, double v)
        {
            using var _pe = Diagnostics.Prof.Time("open: EndPtCore");
            long g0 = genCount;
            try { return EndPtSolve(k, v); }
            finally { Interlocked.Increment(ref OpenEndPoints); Interlocked.Add(ref OpenEndSections, genCount - g0); }
        }
        Vector3d EndPtSolve(int k, double v)
        {
            double t;
            // концы края — те же корни, что у линий касания (точка в точку)
            if (v <= 0 && !double.IsNaN(tEndA[k])) return QA(tEndA[k]);
            if (v >= 1 && !double.IsNaN(tEndB[k])) return QB(tEndB[k]);
            if (endTab != null && EndTab)
            {
                var tab = endTab[k]; int nt = tab.Length - 1; double x = Math.Clamp(v, 0, 1) * nt; int i = Math.Min((int)x, nt - 1);
                double t0 = tab[i] + (tab[i + 1] - tab[i]) * (x - i);
                double w = 2 * Math.Abs(tab[i + 1] - tab[i]) + 1e-9 * Math.Max(1, Delta);
                Func<double, double> g = tt => Fend(k, Gen(tt).Point(v));
                t = double.NaN;
                // затравка по таблице поверхности (сечения таблицы — дёшево, отход от формулы ≤ 2e-8 мм): корень по таблице, затем
                // точный корень в скобке ±1e-6·Δ около него — 5–7 точных сечений вместо ~20. Мутация P3D_DRENDSEED=0.
                if (seedSurf != null && EndSeed)
                {
                    double ts = RootIn(tt => Fend(k, seedSurf.SectionAt((tt - seedLo) * seedKU).Point(v)), t0 - w, t0 + w);
                    if (!double.IsNaN(ts))
                    {
                        // точный корень — секущими от корня таблицы (сходимость сверхлинейная, 3–4 сечения); не сошлось или ушло
                        // дальше ±1e-6·Δ — Иллинойс в этой скобке
                        double d = 1e-6 * Math.Max(1, Delta), sc = Math.Max(1, Math.Abs(ts));
                        double x0 = ts, f0 = g(x0), x1 = ts + 1e-9 * sc, f1 = g(x1);
                        for (int it = 0; it < 12 && double.IsFinite(f0 + f1) && f1 != f0; it++)
                        {
                            double x2 = x1 - f1 * (x1 - x0) / (f1 - f0);
                            if (!(Math.Abs(x2 - ts) <= d)) break;
                            // шум сечения (разность c' по хребту) — ~1e-12 по t: точнее не сойтись; ход точки ≤ 1e-11 мм
                            if (Math.Abs(x2 - x1) < 1e-12 * sc) { t = x2; break; }
                            x0 = x1; f0 = f1; x1 = x2; f1 = g(x1);
                            if (f1 == 0) { t = x1; break; }
                        }
                        if (double.IsNaN(t)) t = RootIn(g, ts - d, ts + d);
                    }
                }
                if (double.IsNaN(t)) t = RootIn(g, t0 - w, t0 + w);
                if (double.IsNaN(t)) t = TStar(k, v);
            }
            else t = TStar(k, v);
            return double.IsNaN(t) ? new Vector3d(double.NaN) : Gen(t).Point(v);
        }
        // одна смена знака в скобке у каждой пробы v
        for (int k = 0; k < 2; k++)
            foreach (double v in new[] { 0, 0.2, 0.4, 0.6, 0.8, 1.0 })
            {
                int changes = 0; double prev = Fend(k, Gen(inT[k]).Point(v));
                for (int i = 1; i <= 32; i++) { double f = Fend(k, Gen(inT[k] + (outT[k] - inT[k]) * i / 32.0).Point(v)); if (Math.Sign(f) != Math.Sign(prev)) changes++; prev = f; }
                if (changes != 1) { why = $"open curved: blend section crosses end face {changes} times"; return false; }
            }
        double tA0 = TStar(0, 0), tB0 = TStar(0, 1), tA1 = TStar(1, 0), tB1 = TStar(1, 1);
        tEndA[0] = tA0; tEndA[1] = tA1; tEndB[0] = tB0; tEndB[1] = tB1;
        endTab = new double[2][];
        for (int k = 0; k < 2; k++)
        {
            endTab[k] = new double[65];
            for (int i = 0; i <= 64; i++) endTab[k][i] = TStar(k, i / 64.0);
            if (endTab[k].Any(double.IsNaN)) { why = "open curved: end curve not found"; return false; }
        }
        if (double.IsNaN(tA0 + tB0 + tA1 + tB1) || !(tA0 < tA1) || !(tB0 < tB1)) { why = "open curved: contact lines do not span between the end faces"; return false; }

        // промежуток поверхности — по краям ленты на торцах (пробы v) с запасом 2 % ребра
        double tLo = double.MaxValue, tHi = double.MinValue;
        for (int i = 0; i <= 16; i++)
        {
            double v = i / 16.0, t0s = TStar(0, v), t1s = TStar(1, v);
            if (double.IsNaN(t0s + t1s)) { why = "open curved: end curve not found"; return false; }
            tLo = Math.Min(tLo, t0s); tHi = Math.Max(tHi, t1s);
        }
        tLo -= 0.02 * Delta; tHi += 0.02 * Delta;
        // поверхность — с запасом ещё 5 % за используемым промежутком: у краёв таблицы формулы вставка Катмулла–Рома опирается
        // на продолженный линейно узел и отходит от формулы на 1e-7 мм; сверка таблицы — на используемом промежутке
        double useLo = tLo, useHi = tHi;
        tLo = Math.Max(outT[0], tLo - 0.05 * Delta); tHi = Math.Min(outT[1], tHi + 0.05 * Delta);
        if (!(tLo <= useLo && tHi >= useHi)) { why = "open curved: blend range beyond the extension"; return false; }
        // СВЕРКИ по пробам на всём продлённом промежутке: хребет, касание на радиусе, ход вперёд, дуга меньше полуокружности
        const int N = 256;
        var us = new List<double>(); var secs = new List<SweepSection>(); var Ps = new List<Vector3d>();
        Vector3d? prevS = null, prevA = null, prevB = null; Vector3d prevT = default; double widthA = 0, widthB = 0;
        for (int i = 0; i <= N; i++)
        {
            double t = tLo + (tHi - tLo) * i / N;
            if (!ch && !(Rt(t) > 1e-6)) { why = "variable radius: radius not positive along the blend (extension)"; return false; }
            var x = ch ? Ed(t) : Spine(t);
            if (x == null) { why = "open curved: ball centre not found (radius too large?)"; return false; }
            var P = Ed(t); Ps.Add(P);
            var qa = ch ? ChA(t) : Foot(shA, x.Value); var qb = ch ? ChB(t) : Foot(shB, x.Value);
            if (ch)
            {
                if (double.IsNaN(qa.X + qb.X) || Math.Abs((qa - P).Length - dA) > 1e-8 * Math.Max(1, dA) || Math.Abs((qb - P).Length - dB) > 1e-8 * Math.Max(1, dB) || (qa - qb).Length < 1e-6)
                { why = "open chamfer: chamfer point not found"; return false; }
            }
            else if (Math.Abs((qa - x.Value).Length - Rt(t)) > 1e-8 * Math.Max(1, r) || Math.Abs((qb - x.Value).Length - Rt(t)) > 1e-8 * Math.Max(1, r)) { why = "open curved: contact not at the ball radius"; return false; }
            var g = Gen(t);
            if (!ch && !(g.Phi > 1e-3 && g.Phi < Math.PI - 1e-3)) { why = "open curved: blend arc degenerate"; return false; }
            if (t >= 0 && t <= Delta) { widthA = Math.Max(widthA, (qa - P).Length); widthB = Math.Max(widthB, (qb - P).Length); }
            if (prevS != null && (Vector3d.Dot(x.Value - prevS.Value, prevT) <= 0 || Vector3d.Dot(qa - prevA!.Value, prevT) <= 0 || Vector3d.Dot(qb - prevB!.Value, prevT) <= 0))
            { why = "open curved: spine or contact line folds back"; return false; }
            prevS = x; prevA = qa; prevB = qb; prevT = Td(t);
            us.Add(t); secs.Add(g);
        }
        // зазор: прочие звенья граней A и B дальше полосы
        var keepA = new HashSet<TEdge>(chain) { ends[0].EA, ends[1].EA }; var keepB = new HashSet<TEdge>(chain) { ends[0].EB, ends[1].EB };
        foreach (var (F, keep, w) in new[] { (FA, keepA, widthA), (FB, keepB, widthB) })
            foreach (var sg in F.Loops.SelectMany(l => l))
            {
                if (sg.Id != null && keep.Contains(sg.Id)) continue;
                for (int k = 0; k <= 32; k++)
                {
                    var q = sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * k / 32.0);
                    if (Ps.Min(p => (p - q).Length) <= 1.5 * w + 1e-6) { why = "open curved: another edge of the face in the blend strip"; return false; }
                }
            }

        double Lp = 0; for (int i = 1; i < Ps.Count; i++) Lp += (Ps[i] - Ps[i - 1]).Length;
        double kU = Lp / (tHi - tLo);
        // стыки формулы: узлы закона радиуса (закон там гладок лишь первого порядка — таблица не вставляет через них)
        var breaks = new List<double>();
        if (LawBreaks) foreach (var kt in knotT) if (kt > tLo && kt < tHi) breaks.Add((kt - tLo) * kU);
        // шаг таблицы: у закона с узлами — вчетверо мельче (кривизна сечений по u у узла выше)
        double tabStep = Math.Max(Lp / 20000, Math.Min(0.25, 0.4 * Math.Min(widthA, widthB)) / 32) / (knotT.Count > 0 ? 4 : 1);
        var surf = new SectionSurface(U => Gen(tLo + U / kU), us.Select(t => (t - tLo) * kU).ToList(), secs, false, 0, Lp, breaks, tabStep);
        {
            double worstTab = 0, worstU = 0;
            for (int i = 0; i < 1000; i++)
            {
                double U = ((useLo - tLo) + (useHi - useLo) * (i + 0.5) / 1000) * kU;
                var ta = surf.SectionAt(U); var ex = surf.ExactSectionAt(U);
                foreach (double v in new[] { 0.0, 0.5, 1.0 }) { double d = (ta.Point(v) - ex.Point(v)).Length; if (d > worstTab) { worstTab = d; worstU = U; } }
            }
            Debug?.Invoke($"open: table vs formula {worstTab:E2} at t {tLo + worstU / kU:G6} (Δ {Delta:G6}) widths {widthA:G4}/{widthB:G4} Lp {Lp:G5}");
            // таблица — представление формулы: её отход — в пятой доле точности модели (1e-7 → 2e-8 мм)
            if (worstTab > Math.Max(1e-8, 0.2 * ToleranceContext.Current.ModelAccuracy)) { why = $"open curved: blend surface table deviates from the formula ({worstTab:E1} mm)"; return false; }
        }

        seedSurf = surf; seedLo = tLo; seedKU = kU;
        // КРИВЫЕ: линии касания, края ленты на торцах
        var cA = new ProcCurve3(QA, tA0, tA1, false, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch || varLaw != null ? null : (s, a, b) => NoEvents(true, s, a, b) };
        var cB = new ProcCurve3(QB, tB0, tB1, false, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch || varLaw != null ? null : (s, a, b) => NoEvents(false, s, a, b) };
        var end0 = new ProcCurve3(v => EndPt(0, v), 0, 1, false, ch ? "chamfer end" : "rolling-ball end");
        var end1 = new ProcCurve3(v => EndPt(1, v), 0, 1, false, ch ? "chamfer end" : "rolling-ball end");
        Vector3d qa0 = QA(tA0), qa1 = QA(tA1), qb0 = QB(tB0), qb1 = QB(tB1);
        if ((end0.PointAt(0) - qa0).Length > 1e-9 || (end0.PointAt(1) - qb0).Length > 1e-9 || (end1.PointAt(0) - qa1).Length > 1e-9 || (end1.PointAt(1) - qb1).Length > 1e-9)
        { why = "open curved: end curves do not meet the contact lines"; return false; }
        for (int i = 0; i <= 16; i++)
            foreach (var ec in new[] { end0, end1 }) if (double.IsNaN(ec.PointAt(i / 16.0).X)) { why = "open curved: end curve not found"; return false; }
        // u11: ПЛОСКОСТЬ/ПЛОСКОСТЬ, постоянный радиус, прямое ребро — лента точно цилиндр, линии касания — прямые, края на
        // плоских торцах — дуги эллипсов (окружностей). Аналитика вместо сечений: разметка, p-кривые, проверки и объём —
        // формулами (у тела из 40 граней 2 с → доли секунды). Всё сверяется с формулой сечений; иначе — прежний путь.
        // Мутация P3D_OPENCYL=0.
        ISurface3 bSurf = surf; ICurve3 bA = cA, bB = cB, bE0 = end0, bE1 = end1;
        if (OpenCylOn && !ch && varLaw == null && knotT.Count == 0 && chain.Count == 1 && chain[0].Curve is LineCurve3
            && shA is Geometry.Ssi.Plane && shB is Geometry.Ssi.Plane && ends[0].ShE is Geometry.Ssi.Plane pe0 && ends[1].ShE is Geometry.Ssi.Plane pe1
            && OpenCylinder(Spine, Gen, QA, QB, EndPt, r, tLo, tHi, tA0, tA1, tB0, tB1, pe0, pe1) is { } oc)
        {
            bSurf = oc.S; bA = oc.A; bB = oc.B; bE0 = oc.E0; bE1 = oc.E1;
            Debug?.Invoke("open: blend is an exact cylinder");
        }

        // УКОРОЧЕНИЕ РЁБЕР A∩E и B∩E до точек касания (одна кривая на обе грани ребра). Ребро может укорачиваться с обоих концов
        // (торец — общий у обоих концов, ребро B∩E идёт от одного конца до другого)
        var cuts = new Dictionary<TEdge, List<(Vector3d Old, Vector3d New)>>();
        foreach (var (x, oldP, q) in new[] { (ends[0].EA, P0, qa0), (ends[0].EB, P0, qb0), (ends[1].EA, P1, qa1), (ends[1].EB, P1, qb1) })
        {
            if (!cuts.TryGetValue(x, out var l)) cuts[x] = l = new();
            l.Add((oldP, q));
        }
        var trimmed = new Dictionary<TEdge, (ICurve3 C, List<(Vector3d Old, Vector3d New)> Moves)>();
        foreach (var (x, moves) in cuts)
        {
            ICurve3 cur = x.Curve;
            foreach (var (oldP, q) in moves)
            {
                var nc = Reshape(cur, oldP, q, out var rwhy);
                if (nc == null) { why = "open curved: " + rwhy; return false; }
                cur = nc;
            }
            foreach (var (oldP, q) in moves)
                if ((cur.PointAt(cur.T0) - q).Length > 1e-7 && (cur.PointAt(cur.T1) - q).Length > 1e-7) { why = "open curved: trimmed end edge misses the contact point"; return false; }
            trimmed[x] = (cur, moves);
        }
        // вставка края ленты в торец: между звеньями A∩E и B∩E у прежней вершины
        foreach (var (k, endC, qa, qb) in new[] { (0, bE0, qa0, qb0), (1, bE1, qa1, qb1) })
        {
            var (FEk, _, eA, eB, V, _) = ends[k];
            bool done = false;
            foreach (var loop in FEk.Loops)
            {
                int n = loop.Count;
                for (int i = 0; i < n && !done; i++)
                {
                    var s1 = loop[i]; var s2 = loop[(i + 1) % n];
                    if ((s1.B - V).Length > 1e-7 || (s2.A - V).Length > 1e-7) continue;
                    if (s1.Id == eA && s2.Id == eB) loop.Insert(i + 1, new Seg { Curve = endC, A = qa, B = qb });
                    else if (s1.Id == eB && s2.Id == eA) loop.Insert(i + 1, new Seg { Curve = endC, A = qb, B = qa });
                    else continue;
                    done = true;
                }
                if (done) break;
            }
            if (!done) { why = "open curved: corner not found in the end face"; return false; }
            FEk.Touched = true;
        }
        // звенья с укороченными рёбрами — во всех гранях
        foreach (var F in new[] { FA, FB, ends[0].FE, ends[1].FE }.Distinct())
            foreach (var loop in F.Loops)
                foreach (var sg in loop)
                    if (sg.Id != null && trimmed.TryGetValue(sg.Id, out var tr))
                    {
                        bool moved = false;
                        foreach (var (oldP, q) in tr.Moves)
                        {
                            if ((sg.A - oldP).Length < 1e-7) { sg.A = q; moved = true; }
                            else if ((sg.B - oldP).Length < 1e-7) { sg.B = q; moved = true; }
                        }
                        if (moved) { sg.Curve = tr.C; sg.TrimOf = tr.C; sg.Orig = null; }
                    }
        // само ребро — линии касания
        foreach (var (F, cv, a0, a1) in new[] { (FA, bA, qa0, qa1), (FB, bB, qb0, qb1) })
        {
            // звенья цепочки в контуре грани — подряд; заменяются одним звеном (линией касания)
            var loop = F.Loops.FirstOrDefault(l => l.Any(x => x.Id != null && chainSet.Contains(x.Id)));
            var idx = loop == null ? new List<int>() : Enumerable.Range(0, loop.Count).Where(i => loop[i].Id != null && chainSet.Contains(loop[i].Id!)).ToList();
            if (loop == null || idx.Count != chain.Count) { why = "open curved: edge segment not found"; return false; }
            int n = loop.Count, first = idx.First(i => !idx.Contains((i - 1 + n) % n));
            for (int k = 0; k < idx.Count; k++) if (!idx.Contains((first + k) % n)) { why = "open curved: chain is not contiguous in the face loop"; return false; }
            var la = loop[first].A; var lb = loop[(first + idx.Count - 1) % n].B;
            bool same = (la - P0).Length < 1e-7 && (lb - P1).Length < 1e-7, rev = (la - P1).Length < 1e-7 && (lb - P0).Length < 1e-7;
            if (!same && !rev) { why = "open curved: chain ends do not match the face loop"; return false; }
            var ns = new Seg { Curve = cv, Orig = null, Id = e, A = same ? a0 : a1, B = same ? a1 : a0 };
            var keep = new List<Seg>();
            for (int k = 0; k < n; k++) { int i = (first + k) % n; if (k == 0) keep.Add(ns); else if (k >= idx.Count) keep.Add(loop[i]); }
            loop.Clear(); loop.AddRange(keep);
            F.Touched = true;
        }
        double tm = Delta / 2; var Em = Ed(tm); var qam = QA(tm); var qbm = QB(tm);
        FA.Hint = Foot(shA, qam + (qam - Em)); FB.Hint = Foot(shB, qbm + (qbm - Em));
        // лента
        var gm = Gen(tm); var mid = gm.Point(0.5);
        var want = ch ? (Geometry.Ssi.Implicit(shA, Em).G * sA + Geometry.Ssi.Implicit(shB, Em).G * sB) : (mid - gm.C).Normalized() * (-sigma);
        var nS = ReferenceEquals(bSurf, surf) ? surf.NormalAt(new Vector2d((tm - tLo) * kU, 0.5)) : bSurf.NormalAt(bSurf.ProjectToUV(mid));
        var bl = new FDesc { Surf = bSurf, Rev = Vector3d.Dot(nS, want) < 0, Tag = ch ? "chamfer:face" : "round:face", Touched = true, Hint = mid };
        bl.Loops.Add(new List<Seg>
        {
            new() { Curve = bA, A = qa0, B = qa1 },
            new() { Curve = bE1, A = qa1, B = qb1 },
            new() { Curve = bB, A = qb1, B = qb0 },
            new() { Curve = bE0, A = qb0, B = qa0 },
        });
        blends.Add(bl);
        Debug?.Invoke($"open: sigma {sigma} shapes {shA.GetType().Name}/{shB.GetType().Name} ends {ends[0].ShE.GetType().Name}/{ends[1].ShE.GetType().Name} t [{tLo:G5}, {tHi:G5}] contacts A [{tA0:G6}, {tA1:G6}] B [{tB0:G6}, {tB1:G6}] Δ {Delta:G6}");
        return true;
    }

    static readonly bool OpenCylOn = Environment.GetEnvironmentVariable("P3D_OPENCYL") != "0";

    /// <summary>u11: точная цилиндрическая лента открытого скругления плоскость/плоскость (см. вызов); null — не сошлось с сечениями.</summary>
    static (CylinderSurface S, ICurve3 A, ICurve3 B, ICurve3 E0, ICurve3 E1)? OpenCylinder(Func<double, Vector3d?> spine, Func<double, SweepSection> gen,
        Func<double, Vector3d> qa, Func<double, Vector3d> qb, Func<int, double, Vector3d> endPt, double r, double tLo, double tHi,
        double tA0, double tA1, double tB0, double tB1, Geometry.Ssi.Plane pe0, Geometry.Ssi.Plane pe1)
    {
        if (spine(tLo) is not { } s0 || spine(tHi) is not { } s1) return null;
        var W = s1 - s0; double len = W.Length; if (!(len > 1e-9)) return null; W /= len;
        double tol = 1e-9 * Math.Max(1, r);
        double DAx(Vector3d p) { var d = p - s0; return (d - W * Vector3d.Dot(d, W)).Length; }
        // хребет — прямая, сечения — на радиусе от неё
        for (int i = 0; i <= 16; i++)
        {
            double t = tLo + (tHi - tLo) * i / 16.0;
            if (spine(t) is not { } x || DAx(x) > tol) return null;
            var g = gen(t);
            foreach (double v in new[] { 0, 0.25, 0.5, 0.75, 1.0 }) if (Math.Abs(DAx(g.Point(v)) - r) > tol) return null;
        }
        var e1 = qa(0.5 * (tA0 + tA1)) - s0; e1 -= W * Vector3d.Dot(e1, W); if (!(e1.Length > 1e-9)) return null; e1 = e1.Normalized();
        var cyl = new CylinderSurface(new Frame3(s0, e1, Vector3d.Cross(W, e1).Normalized()), r);
        if ((cyl.Axis.W - W).Length > 1e-9) return null;
        // линии касания — прямые
        LineCurve3? Line(Func<double, Vector3d> f, double t0, double t1)
        {
            var a = f(t0); var b = f(t1); if (!((b - a).Length > 1e-9)) return null;
            var ln = new LineCurve3(a, b);
            for (int i = 1; i < 8; i++) { var p = f(t0 + (t1 - t0) * i / 8.0); var d = p - a; var u = (b - a).Normalized(); if ((d - u * Vector3d.Dot(d, u)).Length > tol) return null; }
            return ln;
        }
        var la = Line(qa, tA0, tA1); var lb = Line(qb, tB0, tB1);
        if (la == null || lb == null) return null;
        // края на торцах: плоскость ∩ цилиндр — эллипс (окружность у прямого торца)
        ICurve3? End(int k, Geometry.Ssi.Plane pl)
        {
            var n = pl.N.Normalized(); double cosT = Vector3d.Dot(n, W);
            if (!(Math.Abs(cosT) > 1e-3)) return null;
            var c = s0 + W * (Vector3d.Dot(pl.P - s0, n) / cosT);
            var minor = Vector3d.Cross(n, W);
            Vector3d major;
            if (minor.Length < 1e-12) { major = e1 - n * Vector3d.Dot(e1, n); major = major.Normalized(); minor = Vector3d.Cross(n, major).Normalized(); }
            else { minor = minor.Normalized(); major = Vector3d.Cross(minor, n).Normalized(); }
            double ru = r / Math.Abs(cosT), rv = r;
            var fr = new Frame3(c, major, minor);
            double Ang(Vector3d p) { var d = p - c; return Math.Atan2(Vector3d.Dot(d, minor) / rv, Vector3d.Dot(d, major) / ru); }
            var pa = endPt(k, 0); var pb = endPt(k, 1); var pm = endPt(k, 0.5);
            double a0 = Ang(pa), a1 = Ang(pb), am = Ang(pm);
            double Norm(double x) { while (x < a0) x += 2 * Math.PI; while (x >= a0 + 2 * Math.PI) x -= 2 * Math.PI; return x; }
            double a1p = Norm(a1), amp = Norm(am);
            if (amp > a1p) a1p -= 2 * Math.PI;   // ход в обратную сторону
            if (!(Math.Abs(a1p - a0) > 1e-9 && Math.Abs(a1p - a0) < Math.PI)) return null;
            var el = new EllipseCurve3(fr, ru, rv, a0, a1p);
            if ((el.PointAt(0) - pa).Length > 1e-9 || (el.PointAt(1) - pb).Length > 1e-9) return null;
            // точки края — на эллипсе (по плоскости и по оси)
            for (int i = 1; i < 8; i++)
            {
                var p = endPt(k, i / 8.0);
                if (Math.Abs(Vector3d.Dot(p - c, n)) > tol || Math.Abs(DAx(p) - r) > tol) return null;
            }
            if (ru == rv && minor.Length > 0) return new ArcCurve3(new Frame3(c, major, minor), r, a0, a1p);
            return el;
        }
        var e0c = End(0, pe0); var e1c = End(1, pe1);
        if (e0c == null || e1c == null) return null;
        return (cyl, la, lb, e0c, e1c);
    }

    /// <summary>Сборка, нормализация, разметка, проверки, происхождение — общий конец прямых построителей.</summary>
    static TSolid? FinishDirect(List<FDesc> faces, List<FDesc> blends, string op, ref string? why)
    {
        foreach (var fd in faces.Where(x => x.Touched)) fd.Hint = InteriorPoint(fd);
        foreach (var fd in blends.Where(x => x.Surf.IsPlanar && Scan)) fd.Hint = ScanlinePoint(fd);
        var all = faces.Concat(blends).ToList();
        var swF = System.Diagnostics.Stopwatch.StartNew();
        bool profWas = Diagnostics.Prof.Enabled; if (Debug != null && ProfDbg) { Diagnostics.Prof.Enabled = true; Diagnostics.Prof.Reset(); }
        try { return FinishDirectCore(all, op, ref why, swF); }
        finally { if (Debug != null && ProfDbg) { Debug(Diagnostics.Prof.Report()); Diagnostics.Prof.Enabled = profWas; } }
    }
    static readonly bool ProfDbg = Environment.GetEnvironmentVariable("P3D_OPENPROF") == "1";
    static TSolid? FinishDirectCore(List<FDesc> all, string op, ref string? why, System.Diagnostics.Stopwatch swF)
    {
        void Stage(string n) { Debug?.Invoke($"finish: {n} {swF.Elapsed.TotalSeconds:F1}s"); }
        var solid = Assemble(all, ref why);
        Stage("assembled");
        if (solid == null) return null;
        TopologyConventions.Normalize(solid);
        var w = new List<string>();
        if (Analytic.PrismBuild.Finish(solid, w, BooleanOp.DonorFrom(all.Where(d => d.Orig != null).Select(d => d.Orig!))) > 0)
        {
            if (Debug != null)
                foreach (var f in solid.Faces)
                    foreach (var lp in f.Loops)
                        Debug($"  face {f.Id} {f.Tag} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}: " + string.Join(" | ", lp.CoEdges.Select(ce => $"{ce.Edge.Curve.GetType().Name}{(ce.Edge.Curve is ProcCurve3 pc ? ":" + pc.Kind : "")} {ce.Start.P:0.###}->{ce.End.P:0.###}")));
            why = "faces not meshed: " + string.Join(" / ", w.Take(3)); return null;
        }
        Stage("meshed");
        solid.Guarantee = GuaranteeLevel.ExactAnalytic;
        string? Checks()
        {
            if (BooleanOp.ForceAcceptFailure) return "forced acceptance failure (test)";
            var v = SolidValidator.Validate(solid);
            if (!v.IsValid && Debug != null)
                foreach (var iss in v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(20))
                {
                    var ed = solid.Edges.FirstOrDefault(x => iss.ToString().Contains($"(элемент {x.Id})"));
                    Debug($"  invalid: {iss} | edge tol {ed?.Tolerance:E2} curve {ed?.Curve.GetType().Name} {ed?.V0.P:0.###}->{ed?.V1.P:0.###}");
                }
            Stage("validated");
            if (!v.IsValid) return "result invalid: " + v.Summary() + " " + string.Join(" / ", v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2));
            if (AnalyticVolume.Of(solid) == null) return "no analytic volume";
            Stage("volume");
            if (BooleanOp.VolumeVsFacets(solid) is { } vf && !vf.Agree) return $"volume {vf.Va:G8} vs facets {vf.Vf:G8}";
            return null;
        }
        if (BooleanOp.PreviewMode) solid.PendingAcceptance = Checks;
        else if (Checks() is { } cw) { why = cw; return null; }
        Stage("checked");
        Analytic.EntityTolerance.Assign(solid);
        if (Prov)
        {
            var pv = new Provenance { Operation = op };
            foreach (var fd in all)
                if (fd.Made != null && solid.Faces.Contains(fd.Made))
                    pv.Faces.Add(new FaceProvenance(fd.Made, fd.Orig, fd.Orig == null ? FaceOrigin.Generated : fd.Touched ? FaceOrigin.Modified : FaceOrigin.Kept, fd.Orig == null ? fd.Tag : null));
            solid.SetProvenance(pv);
        }
        if (Lazy)
            solid.LazyDerivedExact = (new Lazy<Exact.ExactBody>(() => Exact.ExactAdapter.ToExact(TopologyClone.Deep(solid), new Exact.PlaneRegistry(), null),
                LazyThreadSafetyMode.ExecutionAndPublication), solid.Revision);
        return solid;
    }
}

public static partial class DirectRound
{
    static readonly bool CurvedChamfer = Environment.GetEnvironmentVariable("P3D_DRCURVCH") != "0";
    public static long CurvedChamfers;

    /// <summary>06.15: фаска рёбер между кривыми гранями — замкнутых (CylCylBlends) или незамкнутых (OpenBlend); все рёбра одного вида.</summary>
    static TSolid? CurvedChamferBuild(TSolid body, IReadOnlyList<EdgeRef> refs, double d, Func<TEdge, TFace, double> dist, ref string? why)
    {
        var one = new List<TSolid> { body }; var whole = FilletOp.WholeNamedEdges(one, refs);
        var es = new List<TEdge>();
        foreach (var rf in refs)
        {
            var e = FilletOp.Resolve(one, rf, out _) ?? (whole.TryGetValue(rf, out var we) ? we : null);
            if (e == null) { why = "edge not resolved"; return null; }
            if (!es.Contains(e)) es.Add(e);
        }
        if (es.Any(e => e.Curve is LineCurve3)) { why = "straight edges are not in this family"; return null; }
        bool closed = es.All(e => ReferenceEquals(e.V0, e.V1) || (e.V0.P - e.V1.P).Length < 1e-7);
        if (!closed) return BuildOpen(body, refs, d, ref why, dist);
        if (es.Any(e => !(ReferenceEquals(e.V0, e.V1) || (e.V0.P - e.V1.P).Length < 1e-7))) { why = "closed and open edges in one chamfer"; return null; }
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;
        var blends = new List<FDesc>();
        if (!CylCylBlends(body, es, byFace, blends, d, ref why, dist)) return null;
        return FinishDirect(faces, blends, "chamfer.direct", ref why);
    }
}

public static partial class DirectRound
{
    static readonly bool GenericParam = Environment.GetEnvironmentVariable("P3D_DRGENPARAM") != "0";
    /// <summary>Мутация P3D_DRNURBS=0: грани NURBS в построителе катящегося шара — отказ (как прежде).</summary>
    static readonly bool NurbsBlends = Environment.GetEnvironmentVariable("P3D_DRNURBS") != "0";

    /// <summary>Форма грани для катящегося шара: квадрика — точная неявная; NURBS — по основанию перпендикуляра (Ssi.ParamSurf).</summary>
    static Geometry.Ssi.Shape? BlendShapeOf(ISurface3? s) =>
        Geometry.ExactEdges.ShapeOf(s) ?? (NurbsBlends && s is NurbsSurface ? new Geometry.Ssi.ParamSurf(s) : null);

    /// <summary>
    /// Гладкая опорная кривая ребра для параметра хребта: многочлен Чебышёва по доле длины (интерполяция в узлах Чебышёва;
    /// степень растёт до невязки ≤ tol на 401 пробе). Параметр — миллиметры, t ∈ [0, Length]; вне — продолжение многочлена.
    /// </summary>
    sealed class RefCurve
    {
        double[,] _c = null!; int _n; public double Length;
        public static RefCurve? Fit(ICurve3 E, double tol)
        {
            const int M = 400;
            var pts = new Vector3d[M + 1]; var len = new double[M + 1];
            for (int i = 0; i <= M; i++) { pts[i] = E.PointAt(E.T0 + (E.T1 - E.T0) * i / M); if (i > 0) len[i] = len[i - 1] + (pts[i] - pts[i - 1]).Length; }
            double L = len[M]; if (!(L > 1e-9)) return null;
            // точка ребра при доле длины σ ∈ [0, 1] — вставкой по таблице и уточнением по параметру кривой
            Vector3d AtFrac(double sg)
            {
                double target = sg * L; int lo = 0, hi = M;
                while (hi - lo > 1) { int md = (lo + hi) / 2; if (len[md] <= target) lo = md; else hi = md; }
                double f = len[hi] > len[lo] ? (target - len[lo]) / (len[hi] - len[lo]) : 0;
                return E.PointAt(E.T0 + (E.T1 - E.T0) * (lo + f) / M);
            }
            foreach (int n in new[] { 8, 12, 16, 24, 32, 48 })
            {
                // узлы Чебышёва x_k = cos(π(k + ½)/(n+1)), σ = (1 − x)/2
                int m = n + 1; var vals = new Vector3d[m];
                for (int k = 0; k < m; k++) vals[k] = AtFrac((1 - Math.Cos(Math.PI * (k + 0.5) / m)) / 2);
                var c = new double[m, 3];
                for (int j = 0; j < m; j++)
                {
                    Vector3d acc = Vector3d.Zero;
                    for (int k = 0; k < m; k++) acc += vals[k] * Math.Cos(Math.PI * j * (k + 0.5) / m);
                    acc *= 2.0 / m; c[j, 0] = acc.X; c[j, 1] = acc.Y; c[j, 2] = acc.Z;
                }
                var rc = new RefCurve { _c = c, _n = n, Length = L };
                double worst = 0;
                for (int i = 0; i <= M; i++) worst = Math.Max(worst, (rc.At(len[i]) - pts[i]).Length);
                if (worst <= tol) return rc;
                Debug?.Invoke($"open: reference fit degree {n} worst {worst:E2} (tol {tol:E1}) length {L:G6}");
            }
            return null;
        }
        // x = 1 − 2σ (узел k=0 — у σ≈0); Кленшоу
        (Vector3d P, Vector3d D) Eval(double t)
        {
            double x = 1 - 2 * t / Length;
            Vector3d b1 = Vector3d.Zero, b2 = Vector3d.Zero, d1 = Vector3d.Zero, d2 = Vector3d.Zero;
            for (int j = _n; j >= 1; j--)
            {
                var cj = new Vector3d(_c[j, 0], _c[j, 1], _c[j, 2]);
                var b0 = cj + 2 * x * b1 - b2;
                var dd0 = 2 * b1 + 2 * x * d1 - d2;
                b2 = b1; b1 = b0; d2 = d1; d1 = dd0;
            }
            var c0 = new Vector3d(_c[0, 0], _c[0, 1], _c[0, 2]);
            var P = c0 / 2 + x * b1 - b2;
            var Dx = b1 + x * d1 - d2;              // dP/dx
            return (P, Dx * (-2 / Length));
        }
        public Vector3d At(double t) => Eval(t).P;
        public Vector3d D(double t) => Eval(t).D;
    }
}
