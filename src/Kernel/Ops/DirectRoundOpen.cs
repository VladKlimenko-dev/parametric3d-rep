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
        // k152: КАСАТЕЛЬНОЕ ПРОДОЛЖЕНИЕ ГРАНЕЙ NURBS — ВТОРОЙ ПОПЫТКОЙ. Первая — как прежде (многочлен крайнего промежутка, C∞);
        // если лента не построена, а грани NURBS вычислялись за краем области, — повтор с касательным продолжением (Geometry.Ssi.TangentExt) и стыками
        // таблицы на краю области (P3D_DRTANBREAK). Глобально касательное продолжение ломало ленты, которым многочлен годился
        // (k145 лофт, k146, s111). Мутация P3D_DRTANRETRY=0 — касательное продолжение всегда (как в черновике k152).
        if (!TanRetry || Geometry.Ssi.TangentExt) return BuildOpen0(body, refs, r, ref why, chDist, varLaw);
        int ood = Geometry.Ssi.OutOfDomainEvals;
        var res = BuildOpen0(body, refs, r, ref why, chDist, varLaw);
        if (res != null || Geometry.Ssi.OutOfDomainEvals == ood) return res;
        string? w2 = null;
        Geometry.Ssi.TangentExt = true;
        try { res = BuildOpen0(body, refs, r, ref w2, chDist, varLaw); }
        finally { Geometry.Ssi.TangentExt = false; }
        if (res != null) { Interlocked.Increment(ref TanRetried); Debug?.Invoke("open: built with tangent extension of NURBS faces"); }
        return res;
    }
    static readonly bool TanRetry = Environment.GetEnvironmentVariable("P3D_DRTANRETRY") != "0";
    /// <summary>k152: ленты, построенные со второй попытки — с касательным продолжением граней NURBS.</summary>
    public static long TanRetried;

    static TSolid? BuildOpen0(TSolid body, IReadOnlyList<EdgeRef> refs, double r, ref string? why, Func<TEdge, TFace, double>? chDist = null, RadiusLaw? varLaw = null)
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
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;
        // k145: СТЫК КАСАТЕЛЬНОЙ ЦЕПОЧКИ (как Parasolid): два выбранных ребра сходятся в вершине касательно, третье ребро
        // вершины — гладкое (его грани касаются). Ленты соседних рёбер стыкуются по общему сечению — там, где линия касания
        // пересекает гладкое ребро (шар касается обеих граней одной точкой с общей нормалью). Мутация P3D_DRJOINT=0 — отказ.
        var jointPts = new List<Vector3d>();
        var selSet = chains.SelectMany(x => x).ToHashSet();
        foreach (var c in chains)
            foreach (var p in ChainEnds(c))
            {
                if (!chains.Any(o => o != c && o.Any(x => (x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7))) continue;
                string? jw = JointOn && (chDist == null || ChamferJointOn) && varLaw == null ? TangentJoint(p, c, chains, allE, byFace, chDist != null ? ChamferSwitch : RoundSwitch && varLaw == null) : "joints off";
                if (jw != null) { why = "open curved: selected edges share a vertex" + (JointOn ? $" ({jw})" : ""); return null; }
                if (!jointPts.Any(q => (q - p).Length < 1e-7)) jointPts.Add(p);
            }
        var blends = new List<FDesc>();
        var jctx = jointPts.Count > 0 ? new JointCtx { Pts = jointPts, Sel = selSet } : null;
        foreach (var c in chains)
            if (!OpenBlend(body, c, byFace, blends, r, ref why, chDist, varLaw, jctx)) return null;
        if (jctx != null) { Interlocked.Add(ref JointsBuilt, jctx.Arcs.Count); Debug?.Invoke($"open: {jctx.Arcs.Count} tangent joint(s)"); }
        return FinishDirect(faces, blends, chDist != null ? "chamfer.direct" : "round.direct", ref why);
    }

    static readonly bool Deg2 = Environment.GetEnvironmentVariable("P3D_DRDEG2") != "0";
    static readonly bool JointOn = Environment.GetEnvironmentVariable("P3D_DRJOINT") != "0";
    /// <summary>k145: стыков касательных цепочек построено (разбор).</summary>
    public static long JointsBuilt;
    /// <summary>Стыки касательных цепочек одного построения: вершины, выбранные рёбра, общие сечения (дуга и её концы).</summary>
    sealed class JointCtx
    {
        public List<Vector3d> Pts = new(); public HashSet<TEdge> Sel = new();
        public List<(Vector3d V, ICurve3 Arc, Vector3d P0, Vector3d P1, Vector3d C, Geometry.Ssi.Shape ShT, double OffT)> Arcs = new();
    }
    /// <summary>k147: стыков фасок с переходом боковой точки на соседнюю грань у наклонного шва (разбор).</summary>
    public static long ChamferSwitches;
    /// <summary>k148: стыков скруглений у шва с изломом (шар перекатывается через шов на соседнюю грань).</summary>
    public static long RoundSwitches;
    static readonly bool RoundSwitch = Environment.GetEnvironmentVariable("P3D_DRRDSWITCH") != "0";
    static readonly bool SeamClosest = Environment.GetEnvironmentVariable("P3D_DRSEAMCLOSEST") != "0";   // k149: точка шва — по нулю (c − x)·c'
    static readonly bool CreaseRev = Environment.GetEnvironmentVariable("P3D_DRCREASEREV") != "0";   // k149: излом с касанием T за швом у вершины
    static readonly bool Crease3On = Environment.GetEnvironmentVariable("P3D_DRCREASE3") != "0";   // k148h: по умолчанию (скос у излома шва); 0 — прежний отказ
    /// <summary>k148h: фикстуры, чья история записана до точного угла у излома шва (прежние операции шли резцом).</summary>
    [ThreadStatic] public static bool CreaseOffForTest;
    static bool Crease3 => Crease3On && !CreaseOffForTest;
    /// <summary>k148f: стыков у вогнутого излома шва через шар трёх граней.</summary>
    public static long CreaseSwitches;
    /// <summary>k148g: угол у вогнутого излома — скос по пересечению лент (митра + обрыв большей ленты гранью меньшей, как
    /// ChFi3d PerformTwoCornerbyInter и SMLib Nx2 bevel); P3D_DRCREASEBEVEL=0 — прежняя сборка (плоский обрыв).</summary>
    /// <summary>k148g: шар на шве до вершины (обе стороны стыка — до шва); P3D_DRSEAMTOV=0 — отказ, как прежде.</summary>
    static readonly bool SeamToVertex = Environment.GetEnvironmentVariable("P3D_DRSEAMTOV") == "1";   // в работе: у вершины шар касается конца шва (точки), см. BACKLOG
    public static long SeamToVertexHits;
    static readonly bool SideRef = Environment.GetEnvironmentVariable("P3D_DRSIDEREF") != "0";
    static readonly bool CreaseBevelOn = Environment.GetEnvironmentVariable("P3D_DRCREASEBEVEL") != "0";
    sealed class CreaseBevel
    {
        public bool BigT; public Vector3d B, Pm, C, QS, QT, QE; public double TS, TB, TPm, TX, TY, TXs, TYs;
        public Func<double, Vector3d?> SpT = null!, SpE = null!; public Geometry.Ssi.Shape ShT = null!, ShE = null!, ShS = null!;
        public ICurve3 Mitre = null!, RailT = null!, RailE = null!; public ICurve3? Cliff;
    }
    /// <summary>k147: стык фаски у наклонного шва — боковая точка звена переходит на соседнюю боковую грань. P3D_DRCHSWITCH=0 — отказ, как прежде.</summary>
    static readonly bool ChamferSwitchOn = Environment.GetEnvironmentVariable("P3D_DRCHSWITCH") != "0";
    /// <summary>k150: сечение стыка фаски — через вершину (конец кривой ребра в допуске от неё). Мутация P3D_DRJOINTV=0.</summary>
    static readonly bool CuspEnd = Environment.GetEnvironmentVariable("P3D_DRCUSPEND") != "0";
    public static long CuspEnds;
    // k151: проба таблицы ленты, где формула не определена (NaN), — отказ (прежде пропускалась). Мутация P3D_DRTABNAN=0.
    static readonly bool TabNan = Environment.GetEnvironmentVariable("P3D_DRTABNAN") != "0";
    static readonly bool ConvEpsOn = Environment.GetEnvironmentVariable("P3D_DRCONVEPS") != "0";
    static readonly bool JointThroughV = Environment.GetEnvironmentVariable("P3D_DRJOINTV") != "0";
    public static long JointThroughVHits;
    /// <summary>k147: закреплённые истории (фикстуры корпуса k140, записанные до перехода боковой точки) — воспроизведение без
    /// перехода в этом потоке: прежние операции идут резцом, как при записи, и последняя операция проверяет то, ради чего
    /// фикстура заведена.</summary>
    [ThreadStatic] public static bool ChamferSwitchOffForTest;
    static bool ChamferSwitch => ChamferSwitchOn && !ChamferSwitchOffForTest;
    /// <summary>
    /// Касательный стык в вершине p: ровно три ребра (конец цепочки c, конец другой выбранной цепочки и третье — гладкое),
    /// выбранные рёбра касательны, у них одна общая грань, третье ребро — между двумя другими гранями, касательными в p.
    /// null — стык годен, иначе причина.
    /// </summary>
    static string? TangentJoint(Vector3d p, List<TEdge> c, List<List<TEdge>> chains, List<TEdge> allE, Dictionary<TFace, FDesc> byFace, bool vertexOnly = false)
    {
        var at = allE.Where(x => (x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7).ToList();
        if (at.Count != 3) return $"joint vertex of degree {at.Count}";
        var mine = at.Where(c.Contains).ToList();
        var others = at.Where(x => !c.Contains(x) && chains.Any(o => o != c && o.Contains(x))).ToList();
        var rest = at.Where(x => !c.Contains(x) && !others.Contains(x)).ToList();
        if (mine.Count != 1 || others.Count != 1 || rest.Count != 1) return "joint: not two chain ends and one more edge";
        Vector3d Tan(TEdge e) { var cv = e.Curve; bool a0 = (cv.PointAt(cv.T0) - p).Length <= (cv.PointAt(cv.T1) - p).Length; return cv.TangentAt(a0 ? cv.T0 : cv.T1).Normalized(); }
        if (Math.Abs(Vector3d.Dot(Tan(mine[0]), Tan(others[0]))) < 0.9998) return "joint: chain edges not tangent";
        HashSet<TFace> F(TEdge x) => x.CoEdges.Select(k => k.Loop?.Face).OfType<TFace>().ToHashSet();
        var f1 = F(mine[0]); var f2 = F(others[0]); var f3 = F(rest[0]);
        if (f1.Count != 2 || f2.Count != 2 || f3.Count != 2) return "joint: edge not between two faces";
        var shared = f1.Intersect(f2).ToList();
        if (shared.Count != 1) return "joint: chain edges do not share exactly one face";
        var b = f1.First(x => x != shared[0]); var cc = f2.First(x => x != shared[0]);
        if (!f3.SetEquals(new[] { b, cc })) return "joint: third edge is not between the side faces";
        if (!byFace.TryGetValue(b, out var fb) || !byFace.TryGetValue(cc, out var fc)) return "joint: face not described";
        // гладкость третьего ребра — в вершине и вдоль него
        var cv3 = rest[0].Curve;
        // k147: у фаски — гладкость шва только в самой вершине (дальше боковая точка звена может перейти на соседнюю грань)
        bool p0 = (cv3.PointAt(cv3.T0) - p).Length <= (cv3.PointAt(cv3.T1) - p).Length;
        for (int i = 0; i <= (vertexOnly ? 0 : 8); i++)
        {
            var q = cv3.PointAt(vertexOnly ? (p0 ? cv3.T0 : cv3.T1) : cv3.T0 + (cv3.T1 - cv3.T0) * i / 8.0);
            if (Vector3d.Dot(NormalOf(fb, q), NormalOf(fc, q)) < 0.9998)
            {
                if (Debug != null)
                    for (int j = 0; j <= 8; j++)
                    {
                        double uu = cv3.T0 + (cv3.T1 - cv3.T0) * j / 8.0; var qq = cv3.PointAt(uu); var ts = cv3.TangentAt(uu).Normalized();
                        var nb = NormalOf(fb, qq); var nc = NormalOf(fc, qq); var w = Vector3d.Cross(nb, ts).Normalized();
                        var mm = mine[0].Curve.PointAt((mine[0].Curve.T0 + mine[0].Curve.T1) / 2);
                        if (Vector3d.Dot(mm - qq, w) < 0) w = -w;
                        double h = 0.05 * (cv3.PointAt(cv3.T1) - cv3.PointAt(cv3.T0)).Length;
                        var x = qq + w * h; var mB = fb.Surf.PointAt(fb.Surf.ProjectToUV(x)); var mC = fc.Surf.PointAt(fc.Surf.ProjectToUV(x));
                        Debug($"joint seam {j}/8: angle {Math.Acos(Math.Clamp(Vector3d.Dot(nb, nc), -1, 1)):E2} rad, B below C-ext {Vector3d.Dot(mB - mC, nc):E2} (neg = convex), dist p {(qq - p).Length:0.###}");
                    }
                return "joint: third edge is not smooth";
            }
        }
        return null;
    }
    /// <summary>Параметр вдоль опорной кривой, а не угол вокруг оси (повтор, когда продление по углу не выходит за торец).</summary>
    [ThreadStatic] static bool _forceGeneric;
    [ThreadStatic] internal static bool _endHole;
    static readonly bool ExactTangent = Environment.GetEnvironmentVariable("P3D_DRTANGENT") != "0";
    static readonly bool RootOrder = Environment.GetEnvironmentVariable("P3D_DRROOTORDER") != "0";
    static readonly bool EndSeed = Environment.GetEnvironmentVariable("P3D_DRENDSEED") != "0";
    static readonly bool EndTab = Environment.GetEnvironmentVariable("P3D_DRENDTAB") != "0";
    static readonly bool MemoOn = Environment.GetEnvironmentVariable("P3D_DRMEMO") != "0";
    const int MemoCap = 1 << 18;

    static readonly bool ExtendEnds = Environment.GetEnvironmentVariable("P3D_DREXTEND") != "0";
    static readonly bool StraightExt = Environment.GetEnvironmentVariable("P3D_DRSTRAIGHTEXT") != "0";
    public static long StraightExtends;
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
    static ICurve3? Reshape(ICurve3 c, Vector3d oldP, Vector3d q, out string? why, double lineTol = 0)
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
        // k148e: ПРЯМАЯ КРИВАЯ ОБЩЕГО ВИДА (кромка прежней фаски на плоскости — процедурная кривая ленты, но отрезок): точки
        // отходят от хорды не дальше 1e-9 длины — продление как у отрезка (Sweep_Blend: скругление R5.55 упирается в прежнюю
        // фаску за её концом). Мутация P3D_DRSTRAIGHTEXT=0.
        if (StraightExt && c is not LineCurve3 && c is not ArcCurve3 && c is not EllipseCurve3 && !c.IsClosed)
        {
            Vector3d a = c.PointAt(c.T0), b = c.PointAt(c.T1); var ab = b - a; double L = ab.Length;
            if (L > 1e-9)
            {
                var dd = ab / L; double dev = 0;
                for (int i = 1; i < 32; i++) { var w = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 32.0) - a; dev = Math.Max(dev, (w - dd * Vector3d.Dot(w, dd)).Length); }
                if (dev <= 1e-9 * Math.Max(1, L)) { Interlocked.Increment(ref StraightExtends); c = new LineCurve3(a, b); }
            }
        }
        switch (c)
        {
            case LineCurve3 ln:
            {
                var d = (ln.B - ln.A).Normalized(); var w = q - ln.A;
                double offL = (w - d * Vector3d.Dot(w, d)).Length;
                // k147: у неточного тела отрезок торца — звено ломаной (хорда кривой A ∩ E): точка касания на точных гранях
                // отходит от хорды в пределах допуска — звено перестраивается хордой до неё (P3D_DRTOLEND)
                if (offL > 1e-7 && offL <= lineTol)
                    return startIsOld ? new LineCurve3(q, ln.B) : new LineCurve3(ln.A, q);
                if (offL > 1e-7) { why = $"contact end is off the end edge line ({offL:E1} mm)"; return null; }
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
        why = $"contact end is not on the end edge (extension of {c.GetType().Name} not supported; off {(c.PointAt(tq) - q).Length:E1} mm)";
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

    static bool OpenBlend(TSolid body, List<TEdge> chain, Dictionary<TFace, FDesc> byFace, List<FDesc> blends, double r, ref string? why, Func<TEdge, TFace, double>? chDist = null, RadiusLaw? varLaw = null, JointCtx? jctx = null)
    {
        var e = chain[0];
        var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (fs.Count != 2) { why = "open curved: edge is not between two faces"; return false; }
        var FA = byFace[fs[0]]; var FB = byFace[fs[1]];
        var shA = BlendShapeOf(FA.Surf); var shB = BlendShapeOf(FB.Surf);
        if (shA == null || shB == null) { why = $"open curved: a face is not a plane, cylinder, cone, sphere or torus ({(shA == null ? SurfName(FA.Surf) : SurfName(FB.Surf))})"; return false; }
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
        var joint = new int[2];   // k145: 0 — торец; 1/2 — стык касательной цепочки, гладкое ребро на стороне A/B
        // Я2: ДВУХГРАННЫЙ ТОРЕЦ — в конечной вершине четыре грани: A, B и два торца E1 (у A) и E2 (у B), сходящиеся по ребру m
        // (s108 шаг 2: торцы — грани прежних правок под 74°). Край ленты — по E1 до точки, где m протыкает ленту, дальше по E2;
        // «за торцом» — снаружи обоих (выпуклый излом m: max) или любого (вогнутый: min) — как у Parasolid/OCCT, где край ленты
        // пересекается с гранями у вершины по очереди (ChFi3d PerformIntersectionAtEnd). Опыт: P3D_DRTWOEND=1.
        var two = new bool[2]; var fe2 = new FDesc?[2]; var sh2 = new Geometry.Ssi.Shape?[2]; var se2 = new double[2];
        var mEdge = new TEdge?[2]; var conv2 = new bool[2];
        // Я2: ПЕРЕПОЛНЕНИЕ НА СЛЕДУЮЩУЮ ГРАНЬ У КОНЦА. Ребро торца на стороне T короче ширины ленты: линия касания уходит за его
        // дальний конец q на соседнюю грань X (ребро T ∩ X = sg). Тогда край ленты — по X на стороне T и по торцу E на другой,
        // излом — ребро E ∩ X из q; ребро T ∩ E (от V до q) лента съедает целиком. Мутация P3D_DRENDOVER=0.
        var overDel = new TEdge?[2]; var cornerPt = new Vector3d[2];
        for (int k = 0; k < 2; k++)
        {
            var V = k == 0 ? P0 : P1;
            var at = allEdges.Where(x => !chainSet.Contains(x) && ((x.V0.P - V).Length < 1e-7 || (x.V1.P - V).Length < 1e-7)).ToList();
            if (at.Count == 3 && TwoEnd)
            {
                TFace? Oth(TEdge x, TFace f) { var xf = x.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList(); return xf.Count == 2 && xf.Contains(f) ? xf.First(y => y != f) : null; }
                var tA2 = at.FirstOrDefault(x => Oth(x, fs[0]) is TFace o && o != fs[1]);
                var tB2 = at.FirstOrDefault(x => x != tA2 && Oth(x, fs[1]) is TFace o && o != fs[0]);
                var tM = at.FirstOrDefault(x => x != tA2 && x != tB2);
                if (tA2 != null && tB2 != null && tM != null && (jctx == null || !jctx.Pts.Any(q => (q - V).Length < 1e-7)))
                {
                    var f1 = Oth(tA2, fs[0])!; var f2 = Oth(tB2, fs[1])!;
                    if (f1 != f2 && Oth(tM, f1) == f2 && byFace.TryGetValue(f1, out var FE1d) && byFace.TryGetValue(f2, out var FE2d)
                        && BlendShapeOf(FE1d.Surf) is { } s1 && BlendShapeOf(FE2d.Surf) is { } s2
                        && (tA2.V0.P - tA2.V1.P).Length > 1e-7 && (tB2.V0.P - tB2.V1.P).Length > 1e-7 && (tM.V0.P - tM.V1.P).Length > 1e-7)
                    {
                        double sE1 = Vector3d.Dot(NormalOf(FE1d, V), Geometry.Ssi.Implicit(s1, V).G) > 0 ? 1 : -1;
                        double sE2 = Vector3d.Dot(NormalOf(FE2d, V), Geometry.Ssi.Implicit(s2, V).G) > 0 ? 1 : -1;
                        var ca = tA2.Curve; bool a0 = (ca.PointAt(ca.T0) - V).Length <= (ca.PointAt(ca.T1) - V).Length;
                        var u1 = ca.TangentAt(a0 ? ca.T0 : ca.T1); if (!a0) u1 = -u1;
                        two[k] = true; fe2[k] = FE2d; sh2[k] = s2; se2[k] = sE2; mEdge[k] = tM; cornerPt[k] = V;
                        conv2[k] = Vector3d.Dot(NormalOf(FE2d, V), u1) < 0;
                        ends[k] = (FE1d, s1, tA2, tB2, V, sE1);
                        if (Debug != null)
                        {
                            var mA = ca.PointAt(0.5 * (ca.T0 + ca.T1)); var cb = tB2.Curve; var mB = cb.PointAt(0.5 * (cb.T0 + cb.T1));
                            Debug($"open: end {k}: two end faces {FE1d.Tag} (rev {FE1d.Rev}) / {FE2d.Tag} (rev {FE2d.Rev}), crease {(conv2[k] ? "convex" : "concave")}; sE {sE1}/{sE2}; n1 V {NormalOf(FE1d, V):0.###} mid {NormalOf(FE1d, mA):0.###} g1 V {Geometry.Ssi.Implicit(s1, V).G:0.###} mid {Geometry.Ssi.Implicit(s1, mA).G:0.###}; n2 V {NormalOf(FE2d, V):0.###} mid {NormalOf(FE2d, mB):0.###} g2 V {Geometry.Ssi.Implicit(s2, V).G:0.###} mid {Geometry.Ssi.Implicit(s2, mB).G:0.###}; f2 at mid eA {sE2 * Geometry.Ssi.Implicit(s2, mA).F:E2}, f1 at mid eB {sE1 * Geometry.Ssi.Implicit(s1, mB).F:E2}");
                        }
                        continue;
                    }
                }
            }
            if (at.Count != 2)
            {
                if (Debug != null)
                    foreach (var x in at)
                    {
                        var xf = x.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                        string nm(TFace f) => f == fs[0] ? "A" : f == fs[1] ? "B" : $"{f.Surface.GetType().Name}#{f.GetHashCode() % 10000}";
                        double ang = double.NaN;
                        if (xf.Count == 2 && byFace.TryGetValue(xf[0], out var d0) && byFace.TryGetValue(xf[1], out var d1))
                        {
                            var pm = x.Curve.PointAt(0.5 * (x.Curve.T0 + x.Curve.T1));
                            ang = Vector3d.CalculateAngle(NormalOf(d0, pm), NormalOf(d1, pm)) * 180 / Math.PI;
                        }
                        Debug($"open: end {k} vertex {V:0.###}: edge {x.Curve.GetType().Name} {x.V0.P:0.###}->{x.V1.P:0.###} between {string.Join("/", xf.Select(nm))}, normals at mid {ang:G4}°");
                    }
                why = $"open curved: end vertex of degree {at.Count + 1} (three faces expected)"; return false;
            }
            TFace? Other(TEdge x, TFace f) { var xf = x.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList(); return xf.Count == 2 && xf.Contains(f) ? xf.First(y => y != f) : null; }
            var eA = at.FirstOrDefault(x => Other(x, fs[0]) is TFace o && o != fs[1]);
            var eB = at.FirstOrDefault(x => x != eA && Other(x, fs[1]) is TFace o && o != fs[0]);
            if (eA == null || eB == null) { why = "open curved: end edges not found"; return false; }
            var fe = Other(eA, fs[0])!;
            if (Other(eB, fs[1]) != fe) { why = "open curved: the two end edges are on different faces"; return false; }
            if ((eA.V0.P - eA.V1.P).Length < 1e-7 || (eB.V0.P - eB.V1.P).Length < 1e-7) { why = "open curved: closed end edge"; return false; }
            var FE = byFace[fe]; var shE = BlendShapeOf(FE.Surf);
            // k145: стык касательной цепочки — торец не нужен (лента кончается сечением на гладком ребре)
            if (jctx != null && jctx.Pts.Any(q => (q - V).Length < 1e-7))
            {
                bool sharedA = jctx.Sel.Contains(eA), sharedB = jctx.Sel.Contains(eB);
                if (sharedA == sharedB) { why = "open curved: joint edges not recognised"; return false; }
                joint[k] = sharedA ? 2 : 1;     // 1 — гладкое ребро на стороне A, 2 — на стороне B
                ends[k] = (FE, shE!, eA, eB, V, 1);
                continue;
            }
            if (shE == null) { why = "open curved: end face is not a quadric"; return false; }
            double sE = Vector3d.Dot(NormalOf(FE, V), Geometry.Ssi.Implicit(shE, V).G) > 0 ? 1 : -1;
            ends[k] = (FE, shE, eA, eB, V, sE); cornerPt[k] = V;
            var hint = _overHints?.FirstOrDefault(h => (h.V - V).Length < 1e-7);
            if (hint is { Sg: not null } oh)
            {
                bool onA = oh.OnA; var eT = onA ? eA : eB; var fT = onA ? fs[0] : fs[1]; var q = oh.Q;
                var X = Other(oh.Sg, fT);
                var atQ = allEdges.Where(x => (x.V0.P - q).Length < 1e-7 || (x.V1.P - q).Length < 1e-7).ToList();
                var m = atQ.FirstOrDefault(x => x != eT && x != oh.Sg);
                if (X == null || atQ.Count != 3 || m == null || Other(m, fe) != X || !byFace.TryGetValue(X, out var FX) || BlendShapeOf(FX.Surf) is not { } shX)
                { why = "open curved: end overflow — the next face at the far end of the end edge not recognised"; return false; }
                double sX = Vector3d.Dot(NormalOf(FX, q), Geometry.Ssi.Implicit(shX, q).G) > 0 ? 1 : -1;
                // направление внутрь первой грани торца (по стороне A) от излома q: у стороны A — вдоль sg, у B — вдоль B ∩ E
                var cu = onA ? oh.Sg.Curve : eB.Curve; bool c0 = (cu.PointAt(cu.T0) - q).Length <= (cu.PointAt(cu.T1) - q).Length;
                var u1 = cu.TangentAt(c0 ? cu.T0 : cu.T1); if (!c0) u1 = -u1;
                if (onA) { ends[k] = (FX, shX, oh.Sg, eB, V, sX); fe2[k] = FE; sh2[k] = shE; se2[k] = sE; conv2[k] = Vector3d.Dot(NormalOf(FE, q), u1) < 0; }
                else { ends[k] = (FE, shE, eA, oh.Sg, V, sE); fe2[k] = FX; sh2[k] = shX; se2[k] = sX; conv2[k] = Vector3d.Dot(NormalOf(FX, q), u1) < 0; }
                two[k] = true; mEdge[k] = m; overDel[k] = eT; cornerPt[k] = q;
                Debug?.Invoke($"open: end {k}: overflow on {(onA ? "A" : "B")} past {q:0.###} onto {FX.Tag}, crease {(conv2[k] ? "convex" : "concave")}");
            }
        }
        if (ends[0].FE == FA || ends[0].FE == FB || ends[1].FE == FA || ends[1].FE == FB) { why = "open curved: end face is a face of the edge"; return false; }
        for (int k = 0; k < 2; k++) if (two[k] && (fe2[k] == FA || fe2[k] == FB || fe2[k] == ends[1 - k].FE || (two[1 - k] && fe2[k] == fe2[1 - k]))) { why = "open curved: two-face end shares a face"; return false; }

        var Pm = E.PointAt((E.T0 + E.T1) / 2);
        double sA = Vector3d.Dot(NormalOf(FA, Pm), Geometry.Ssi.Implicit(shA, Pm).G) > 0 ? 1 : -1;
        double sB = Vector3d.Dot(NormalOf(FB, Pm), Geometry.Ssi.Implicit(shB, Pm).G) > 0 ? 1 : -1;
        double sizeMin = 1e9;
        foreach (var sh in new[] { shA, shB })
            sizeMin = Math.Min(sizeMin, sh switch { Geometry.Ssi.Cylinder c => c.R, Geometry.Ssi.Sphere sp => sp.R, Geometry.Ssi.Torus to => to.r, _ => 1e9 });

        // выпуклость — пробой точки в теле (как у замкнутых рёбер)
        List<(Vector3d A, Vector3d B, Vector3d C)>? tris = null;
        int? sigmaVote = null;
        double edgeLen = 0; { var pq = E.PointAt(E.T0); for (int i = 1; i <= 32; i++) { var qq = E.PointAt(E.T0 + (E.T1 - E.T0) * i / 32.0); edgeLen += (qq - pq).Length; pq = qq; } }
        foreach (double f in new[] { 0.25, 0.5, 0.75 })
        {
            var p = E.PointAt(E.T0 + (E.T1 - E.T0) * f);
            var nA = NormalOf(FA, p); var nB = NormalOf(FB, p);
            tris ??= SolidTriangles(body);
            var d = nB - nA; if (d.Length < 1e-6) { why = "open curved: tangent faces"; return false; }
            double eps = Math.Min(r, 0.05 * Math.Min(sizeMin, 1e3)) / 2;
            // k150: ПРОБА ВЫПУКЛОСТИ — В ОКРЕСТНОСТИ РЕБРА. Отступ — половина размера ленты; у короткого ребра у острия (ребро
            // «стенка ∩ скругление», 10 мм, фаска 9.72 у Sweep_Blend) точка пробы уходила за соседнюю грань — «выпуклость
            // меняется вдоль ребра», хотя угол граней всюду ~90°. Отступ — не больше 1/20 длины ребра. Мутация P3D_DRCONVEPS=0.
            if (ConvEpsOn) eps = Math.Min(eps, 0.05 * edgeLen);
            bool? ins = InsideMesh(tris, p + d.Normalized() * eps);
            if (ins == null) { why = "open curved: convexity probe undecided"; return false; }
            int sg = ins.Value ? 1 : -1;
            Debug?.Invoke($"open: convexity probe at {f} p {p:0.###} nA {nA:0.###} nB {nB:0.###} inside {ins} (eps {eps:G3}), faces {FA.Tag}/{FB.Tag}");
            if (sigmaVote != null && sigmaVote != sg) { why = "open curved: convexity changes along the edge"; return false; }
            sigmaVote = sg;
        }
        double sigma = sigmaVote!.Value;
        double offA = sigma * sA * r, offB = sigma * sB * r;
        // ПЕРЕМЕННЫЙ РАДИУС (06.14): r(t) задаётся позже (по длине ребра); до того — постоянный r (наибольший — для проверок)
        Func<double, double> Rt = _ => r, dRt = _ => 0;
        Func<double, double>? fracOf = null;
        var knotT = new List<double>();                  // параметры хребта узлов закона радиуса
        // k148f: переход у вогнутого излома шва через шар трёх граней (данные конца k)
        var crOn = new bool[2]; var crT = new double[2]; var crC = new Vector3d[2]; var crQS = new Vector3d[2]; var crQT = new Vector3d[2]; var crQE = new Vector3d[2];
        var crSeamPt = new Vector3d[2]; var crShT = new Geometry.Ssi.Shape?[2]; var crShE = new Geometry.Ssi.Shape?[2]; var crSE = new double[2];
        var crBv = new CreaseBevel?[2];
        var swT = new[] { double.NaN, double.NaN }; var swT2 = new[] { double.NaN, double.NaN }; var swQ2 = new Vector3d[2]; var swOnA = new bool[2]; var swSh = new Geometry.Ssi.Shape?[2]; var swQ = new Vector3d[2];
        // k148: стык скругления у шва с изломом — режимы хребта у конца k: на T (обычный), на шве (шар касается общей грани и
        // ребра шва), на E (шар касается общей грани и соседней боковой E). rgT — переход T → шов, rgE — шов → E.
        var rgT = new[] { double.NaN, double.NaN }; var rgE = new[] { double.NaN, double.NaN }; var rgSeam = new ICurve3?[2];
        var rgShE = new Geometry.Ssi.Shape?[2]; var rgSE = new double[2]; var rgOnA = new bool[2];
        double OffA(double t) => sigma * sA * Rt(t);
        double OffB(double t) => sigma * sB * Rt(t);
        bool ch = chDist != null; double dA = ch ? chDist!(e, fs[0]) : r, dB = ch ? chDist!(e, fs[1]) : r;
        foreach (var (sh, off) in ch ? Array.Empty<(Geometry.Ssi.Shape, double)>() : new[] { (shA, offA), (shB, offB) })
            if (sh is Geometry.Ssi.Cylinder cy0 && cy0.R + off < 1e-6 || sh is Geometry.Ssi.Sphere sp0 && sp0.R + off < 1e-6 || sh is Geometry.Ssi.Torus to0 && to0.r + off < 1e-6)
            { why = "open curved: radius too large for a face"; return false; }

        // ПАРАМЕТР: угол вокруг оси формы, монотонный вдоль ребра; t = 0 — начало ребра, t = Δ — конец, вне [0, Δ] — продление
        Func<double, Vector3d>? edgeAt = null; double Delta = 0, rad0 = 0, rad1 = 0;
        foreach (var shW in _forceGeneric ? Array.Empty<Geometry.Ssi.Shape>() : new[] { shB, shA })
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
            // 17:31: ПРОДЛЕНИЕ ЗА КОНЦЫ — ХОДОМ ПО A ∩ B (шаг h, предиктор по касательной ∇A × ∇B, корректор Ньютоном в
            // плоскости поперёк неё), а опорная кривая — многочлен Чебышёва по ребру ВМЕСТЕ С ходом за концами: продолжение
            // многочлена ребра за конец уходит от кривой пересечения (цилиндр ∪ шар: у торца кривая поворачивает к оси цилиндра),
            // а одна гладкая опорная кривая на всём промежутке даёт гладкий параметр (таблица ленты вставляется пятой степенью).
            // Параметр t = 0 и t = Δ — у концов ребра (плоскость сечения через конец). Мутация P3D_DRMARCH=0 — многочлен ребра.
            double hM = Math.Clamp(0.02 * r, 0.005, 0.25), sExt = 2.5 * r + 3;
            Vector3d TanAB(Vector3d x)
            {
                var (_, g1) = Geometry.Ssi.Implicit(shA, x); var (_, g2) = Geometry.Ssi.Implicit(shB, x);
                var tg = Vector3d.Cross(g1, g2); double l = tg.Length; return l > 1e-300 ? tg / l : new Vector3d(double.NaN);
            }
            Vector3d? OnAB(Vector3d seed, Vector3d n, double reach)
            {
                var x = seed;
                for (int it = 0; it < 40; it++)
                {
                    var (f1, g1) = Geometry.Ssi.Implicit(shA, x); var (f2, g2) = Geometry.Ssi.Implicit(shB, x);
                    var dx = Solve3(new[] { g1, g2, n }, new[] { -f1, -f2, -Vector3d.Dot(x - seed, n) });
                    if (dx == null || !double.IsFinite(dx.Value.X)) return null;
                    x += dx.Value;
                    if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
                }
                var (e1, _) = Geometry.Ssi.Implicit(shA, x); var (e2, _) = Geometry.Ssi.Implicit(shB, x);
                return Math.Abs(e1) > 1e-10 || Math.Abs(e2) > 1e-10 || (x - seed).Length > reach ? null : x;
            }
            List<Vector3d> March(Vector3d start, Vector3d inner)
            {
                var pts = new List<Vector3d>();
                var x = start; var tg = TanAB(x); if (Vector3d.Dot(tg, start - inner) < 0) tg = -tg;
                for (double sAcc = 0; sAcc < sExt && double.IsFinite(tg.X);)
                {
                    var nx = OnAB(x + tg * hM, tg, 4 * hM); if (nx == null) break;
                    var t2 = TanAB(nx.Value); if (Vector3d.Dot(t2, tg) < 0) t2 = -t2;
                    if (Vector3d.Dot(t2, tg) < 0.9) break;          // излом или касание поверхностей — ход не продолжается
                    sAcc += (nx.Value - x).Length; x = nx.Value; tg = t2; pts.Add(x);
                }
                return pts;
            }
            ICurve3 refSrc = E;
            // только при повторе после отказа продления по углу (у прочих рёбер с общим параметром — прежний многочлен ребра)
            bool marchHere = MarchExt && _forceGeneric;
            if (marchHere)
            {
                const int Me = 400;
                var edgePts = Enumerable.Range(0, Me + 1).Select(i => E.PointAt(E.T0 + (E.T1 - E.T0) * i / Me)).ToList();
                var ext0 = March(edgePts[0], edgePts[Math.Min(4, Me)]); var ext1 = March(edgePts[^1], edgePts[Math.Max(0, Me - 4)]);
                var poly = new List<Vector3d>(); ext0.Reverse(); poly.AddRange(ext0); poly.AddRange(edgePts); poly.AddRange(ext1);
                var cum = new double[poly.Count]; for (int i = 1; i < poly.Count; i++) cum[i] = cum[i - 1] + (poly[i] - poly[i - 1]).Length;
                double Ltot = cum[^1];
                Vector3d PolyAt(double s)
                {
                    s = Math.Clamp(s, 0, Ltot); int lo = 0, hi = poly.Count - 1;
                    while (hi - lo > 1) { int md = (lo + hi) / 2; if (cum[md] <= s) lo = md; else hi = md; }
                    double f = cum[hi] > cum[lo] ? (s - cum[lo]) / (cum[hi] - cum[lo]) : 0;
                    return poly[lo] + (poly[hi] - poly[lo]) * f;
                }
                if (Ltot > 1e-9) refSrc = new ProcCurve3(PolyAt, 0, Ltot, false, "blend reference");
                Debug?.Invoke($"open: march extension {ext0.Count}/{ext1.Count} steps of {hM:G3} mm");
            }
            var rcFit = RefCurve.Fit(refSrc, 1e-3 * Math.Clamp(r, 0.1, 1));   // опорная кривая задаёт лишь плоскости сечений: точность — доли радиуса
            if (rcFit == null) Debug?.Invoke($"open: reference curve not fitted ({E.GetType().Name}, chain {chain.Count}: {string.Join(",", chain.Select(x => x.Curve.GetType().Name))})");
            if (rcFit is { } rc)
            {
                Vector3d AtCore(double t)
                {
                    var c = rc.At(t); var d = rc.D(t).Normalized();
                    return OnAB(c, d, 0.25 * Math.Max(r, 1e-3)) ?? new Vector3d(double.NaN);
                }
                // t опорной кривой, где плоскость сечения проходит через точку q (конец ребра): (q − c(t))·c'(t) = 0
                double TAt(Vector3d q)
                {
                    double best = 0, bd = double.MaxValue;
                    for (int i = 0; i <= 400; i++) { double t = rc.Length * i / 400; double d = (rc.At(t) - q).Length; if (d < bd) { bd = d; best = t; } }
                    double tt = best;
                    for (int it = 0; it < 50; it++)
                    {
                        double h = 1e-6 * Math.Max(1, rc.Length);
                        double g(double x) => Vector3d.Dot(q - rc.At(x), rc.D(x).Normalized());
                        double g0 = g(tt), d = (g(tt + h) - g(tt - h)) / (2 * h);
                        if (!(Math.Abs(d) > 1e-300)) break;
                        double step = g0 / d; tt -= step;
                        if (Math.Abs(step) < 1e-14 * Math.Max(1, rc.Length)) break;
                    }
                    return tt;
                }
                // k147: допуски на рёбрах (как в Parasolid): у неточного тела (после правки резцом) рёбра — ломаные и кривые с
                // отходом от точного A ∩ B, вершины — вне него. Ребро тогда — само пересечение точных граней, а отход вершин и
                // звеньев допускается до допуска «на поверхности» проверки геометрии (1e-4 мм, GeometryAudit.OnSurfaceTolerance).
                // Мутация P3D_DRTOLEND=0 — как прежде (1e-5·L у концов, 1e-6 по ребру).
                bool tolerant = TolEnds && body.Guarantee != GuaranteeLevel.ExactAnalytic;
                double endTol = Math.Max(1e-5 * Math.Max(1, rc.Length), tolerant ? 1e-4 : 0), runTol = tolerant ? 1e-4 : 1e-6;
                double tS = marchHere ? TAt(P0) : 0, tE = marchHere ? TAt(P1) : rc.Length;
                Vector3d AtG(double t) => AtCore(t + tS);
                double D = tE - tS;
                bool Fits()
                {
                    bool f = D > 0 && (AtG(0) - P0).Length < endTol && (AtG(D) - P1).Length < endTol;
                    for (int i = 0; i < 64 && f; i++)
                    {
                        var q = AtG(D * (i + 0.37) / 64);
                        if (double.IsNaN(q.X) || OffChain(q) > runTol) f = false;
                    }
                    return f;
                }
                bool ok = Fits();
                // k147: запасной путь — параметр концов там, где плоскость сечения проходит через вершину (TAt): подгонка
                // опорной кривой (1e-3·r) сдвигает плоскость вдоль ребра, и точка A ∩ B отходит от вершины на ошибку подгонки
                // (1e-5…3e-4 мм). Только когда концы опорной кривой не сошлись (иначе — прежний параметр: у призмы лофта
                // привязка меняла стыки цепочки скругления). Мутация P3D_DRENDSNAP=0.
                if (!ok && EndSnap && !marchHere)
                {
                    tS = TAt(P0); tE = TAt(P1); D = tE - tS; ok = Fits();
                    if (ok) Debug?.Invoke($"open: generic parameter ends snapped to the vertices (t {tS:G6} … {tE:G6} of {rc.Length:G6})");
                }
                if (ok) { edgeAt = AtG; Delta = D; rad0 = rad1 = 1; }
                else if (Debug != null)
                {
                    // Я2 (разбор): почему точка A ∩ B в плоскости сечения у конца не найдена — остатки Ньютона без ограничения хода
                    string Diag(double t)
                    {
                        var c = rc.At(t + tS); var n = rc.D(t + tS).Normalized(); var x = c; int it = 0; string stop = "iter";
                        for (; it < 40; it++)
                        {
                            var (f1, g1) = Geometry.Ssi.Implicit(shA, x); var (f2, g2) = Geometry.Ssi.Implicit(shB, x);
                            if (!double.IsFinite(f1)) { stop = "A NaN"; break; }
                            if (!double.IsFinite(f2)) { stop = "B NaN"; break; }
                            var dx = Solve3(new[] { g1, g2, n }, new[] { -f1, -f2, -Vector3d.Dot(x - c, n) });
                            if (dx == null || !double.IsFinite(dx.Value.X)) { stop = $"singular (angle {Vector3d.CalculateAngle(g1, g2) * 180 / Math.PI:G3}°)"; break; }
                            x += dx.Value;
                            if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) { stop = "conv"; break; }
                        }
                        double e1 = double.NaN, e2 = double.NaN;
                        try { e1 = Geometry.Ssi.Implicit(shA, x).F; e2 = Geometry.Ssi.Implicit(shB, x).F; } catch { }
                        return $"t {t:G5}: {stop} after {it}, |fA| {Math.Abs(e1):E1} |fB| {Math.Abs(e2):E1} move {(x - c).Length:E2} (reach {0.25 * Math.Max(r, 1e-3):G3}), vertex {Math.Min((x - P0).Length, (x - P1).Length):E2}";
                    }
                    double worstRun = 0; for (int i = 0; i < 64; i++) { var q = AtG(D * (i + 0.37) / 64); worstRun = Math.Max(worstRun, double.IsNaN(q.X) ? double.PositiveInfinity : OffChain(q)); }
                    Debug($"open: generic parameter ends: {Diag(0)}; {Diag(D)}; run off the chain {worstRun:E2} (tol {runTol:E1}), ends tol {endTol:E1}");
                }
                if (!ok) Debug?.Invoke($"open: generic parameter rejected: ends {(AtG(0) - P0).Length:E2} {(AtG(D) - P1).Length:E2}; tol edge {chain.Max(x => x.Curve is null ? 0 : x.Tolerance):E1} vertex {chain.SelectMany(x => new[] { x.V0, x.V1 }).Max(v => v.Tolerance):E1}; curve {string.Join(",", chain.Select(x => x.Curve.GetType().Name))}; faces {SurfName(FA.Surf)}/{SurfName(FB.Surf)}; guarantee {body.Guarantee}");
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
            for (int k = 0; k < 2; k++)
            {
                int rg = Regime(k, t);
                if (rg == 1) return SeamSpine(k, t);
                if (rg == 2) return rgOnA[k] ? SpineWith(t, rgShE[k]!, rgSE[k], shB, sB) : SpineWith(t, shA, sA, rgShE[k]!, rgSE[k]);
            }
            return SpineWith(t, shA, sA, shB, sB);
        }
        Vector3d? SpineWith(double t, Geometry.Ssi.Shape sh1, double s1, Geometry.Ssi.Shape sh2, double s2)
        {
            using var _ps = Diagnostics.Prof.Time("open: SpineCore");
            var P = Ed(t); if (double.IsNaN(P.X)) return null;
            var tg = Td(t);
            var nA = Geometry.Ssi.Implicit(sh1, P).G * s1; var nB = Geometry.Ssi.Implicit(sh2, P).G * s2; double c = Vector3d.Dot(nA, nB);
            double rr = Rt(t), oA = sigma * s1 * rr, oB = sigma * s2 * rr;
            var x = P + (nA + nB) * (sigma * rr / (1 + c));
            for (int it = 0; it < 60; it++)
            {
                var (f1, gA) = Geometry.Ssi.Implicit(sh1, x); var (f2, gB) = Geometry.Ssi.Implicit(sh2, x);
                double f3 = Vector3d.Dot(x - P, tg);
                var dx = Solve3(new[] { gA, gB, tg }, new[] { oA - f1, oB - f2, -f3 });
                if (dx == null) return null;
                x += dx.Value;
                if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
            }
            double ea = Geometry.Ssi.Implicit(sh1, x).F - oA, eb = Geometry.Ssi.Implicit(sh2, x).F - oB;
            if (Math.Abs(ea) > 1e-9 * Math.Max(1, rr) || Math.Abs(eb) > 1e-9 * Math.Max(1, rr) || Math.Abs(Vector3d.Dot(x - P, tg)) > 1e-9 * Math.Max(1, rr)) return null;
            return x;
        }
        // k148: режим хребта у конца k: 0 — обычный, 1 — на шве, 2 — на соседней грани E
        int Regime(int k, double t)
        {
            if (double.IsNaN(rgT[k])) return 0;
            bool Past(double ts) => k == 0 ? t < ts : t > ts;
            return !Past(rgT[k]) ? 0 : Past(rgE[k]) ? 2 : 1;
        }
        // k149: БЛИЖАЙШАЯ ТОЧКА ШВА — уточнение по нулю g(u) = (c(u) − x)·c'(u). Перебор с золотым сечением ищет минимум
        // квадрата расстояния, а он у минимума плоский: положение вдоль шва — с ошибкой ~√ε·r (1e-8…1e-7 мм), и точка касания
        // на шве «дрожала» (лента на шве: таблица против формулы 1.2e-7 мм, s106). Нуль g — трансверсальный. Мутация
        // P3D_DRSEAMCLOSEST=0.
        double SeamU(ICurve3 sc, Vector3d x)
        {
            double u = CurveParam.Closest(sc, x);
            if (!SeamClosest) return u;
            double G(double v) { var c = sc.PointAt(v); var tg = sc.TangentAt(v); return Vector3d.Dot(c - x, tg); }
            double hu = 1e-6 * Math.Max(1e-12, sc.T1 - sc.T0), g = G(u);
            for (int it = 0; it < 8 && double.IsFinite(g); it++)
            {
                double dg = (G(u + hu) - G(u - hu)) / (2 * hu);
                if (!(Math.Abs(dg) > 0)) break;
                double un = u - g / dg, gn = G(un);
                if (!(Math.Abs(gn) < Math.Abs(g))) break;
                u = un; g = gn;
                if (Math.Abs(g) < 1e-15) break;
            }
            return u;
        }
        // центр шара на шве: в плоскости сечения, на расстоянии r от общей грани S и от кривой шва (касание ребра)
        Vector3d? SeamSpine(int k, double t)
        {
            var P = Ed(t); if (double.IsNaN(P.X)) return null;
            var tg = Td(t); var sc = rgSeam[k]!;
            var shS = rgOnA[k] ? shB : shA; double oS = sigma * (rgOnA[k] ? sB : sA) * r;
            var x0 = SpineWith(t, shA, sA, shB, sB); if (x0 == null) return null;
            var x = x0.Value;
            for (int it = 0; it < 60; it++)
            {
                var (f1, g1) = Geometry.Ssi.Implicit(shS, x);
                var q = sc.PointAt(SeamU(sc, x)); var d = x - q; double L = d.Length;
                if (!(L > 1e-12)) return null;
                double f3 = Vector3d.Dot(x - P, tg);
                var dx = Solve3(new[] { g1, d / L, tg }, new[] { oS - f1, r - L, -f3 });
                if (dx == null) return null;
                x += dx.Value;
                if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
            }
            var qe = sc.PointAt(SeamU(sc, x));
            if (Math.Abs(Geometry.Ssi.Implicit(shS, x).F - oS) > 1e-9 * Math.Max(1, r) || Math.Abs((x - qe).Length - r) > 1e-9 * Math.Max(1, r) || Math.Abs(Vector3d.Dot(x - P, tg)) > 1e-9 * Math.Max(1, r)) return null;
            return x;
        }
        // точка касания на стороне (A или B) у центра x сечения t: у конца с изломом шва — на шве или на E
        Vector3d SideFoot(bool sideA, double t, Vector3d x)
        {
            for (int k = 0; k < 2; k++)
            {
                if (double.IsNaN(rgT[k]) || rgOnA[k] != sideA) continue;
                int rg = Regime(k, t);
                if (rg == 1) return rgSeam[k]!.PointAt(SeamU(rgSeam[k]!, x));
                if (rg == 2) return Foot(rgShE[k]!, x);
            }
            return Foot(sideA ? shA : shB, x);
        }
        Vector3d Foot(Geometry.Ssi.Shape sh, Vector3d x) { var (fv, g) = Geometry.Ssi.Implicit(sh, x); return x - g * fv; }
        Vector3d S(double t) => Spine(t) ?? new Vector3d(double.NaN);
        // k147: ПЕРЕХОД БОКОВОЙ ТОЧКИ ФАСКИ на соседнюю боковую грань E за точкой t* (стык у наклонного шва): за t* — та
        // же точка фаски (плоскость поперёк ребра, хорда d), но на E
        bool Beyond(int k, double t) => !double.IsNaN(swT[k]) && (k == 0 ? t < swT[k] : t > swT[k]);
        Vector3d ChA(double t)
        {
            for (int k = 0; k < 2; k++) if (swOnA[k] && Beyond(k, t)) return ChamferPoint(swSh[k]!, shB, sA, sB, sigma, Ed(t), Td(t), dA);
            return ChamferPoint(shA, shB, sA, sB, sigma, Ed(t), Td(t), dA);
        }
        Vector3d ChB(double t)
        {
            for (int k = 0; k < 2; k++) if (!swOnA[k] && Beyond(k, t)) return ChamferPoint(swSh[k]!, shA, sB, sA, sigma, Ed(t), Td(t), dB);
            return ChamferPoint(shB, shA, sB, sA, sigma, Ed(t), Td(t), dB);
        }
        Vector3d QA(double t) => ch ? ChA(t) : SideFoot(true, t, S(t));
        Vector3d QB(double t) => ch ? ChB(t) : SideFoot(false, t, S(t));
        SweepSection Gen(double t)
        {
            using var _pg = Diagnostics.Prof.Time("open: Gen");
            genCount++;
            if (ch) return SweepSection.Line(ChA(t), ChB(t));
            var x = S(t); var qa = SideFoot(true, t, x); var qb = SideFoot(false, t, x);
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
        // k150: КОНЕЦ У ОСТРИЯ — торец касается грани ребра вдоль шва (cusp[k] — направление «за шов» в этой грани). «За
        // торцом» — сторона материала у E или за продолжением шва: на стороне касания корень — пересечение контакта с
        // продолжением шва (поперёк), на остальной ленте — с самим торцом
        var cusp = new Vector3d?[2];
        double Fend(int k, Vector3d x) => two[k] ? endSign[k] * F2End(k, x)
            : cusp[k] is { } mc
            ? Math.Max(-ends[k].SE * Geometry.Ssi.Implicit(ends[k].ShE, x).F, Vector3d.Dot(x - ends[k].V, mc))
            : endSign[k] * ends[k].SE * Geometry.Ssi.Implicit(ends[k].ShE, x).F;
        double F2End(int k, Vector3d x)
        {
            double f1 = ends[k].SE * Geometry.Ssi.Implicit(ends[k].ShE, x).F, f2 = se2[k] * Geometry.Ssi.Implicit(sh2[k]!, x).F;
            return conv2[k] ? Math.Max(f1, f2) : Math.Min(f1, f2);
        }
        // направление «за шов»: торец E касается грани A (или B) в вершине, шов A ∩ E — гладкий; m — в грани касания поперёк
        // шва, по ходу ребра за конец
        Vector3d? CuspDir(int k, double tEnd, double dk)
        {
            var V = ends[k].V; var nE = NormalOf(ends[k].FE, V);
            foreach (var (F, seam, other) in new[] { (FA, ends[k].EA, ends[k].EB), (FB, ends[k].EB, ends[k].EA) })
            {
                var nF = NormalOf(F, V);
                if (Math.Abs(Vector3d.Dot(nF, nE)) < 1 - 1e-7) continue;
                var c = seam.Curve; bool s0 = (c.PointAt(c.T0) - V).Length <= (c.PointAt(c.T1) - V).Length;
                var sTan = c.TangentAt(s0 ? c.T0 : c.T1); if (!(sTan.LengthSquared > 0)) continue; sTan.Normalize();
                // шов гладкий: нормали F и E совпадают и в середине шва
                var mid = c.PointAt((c.T0 + c.T1) / 2);
                if (Math.Abs(Vector3d.Dot(NormalOf(F, mid), NormalOf(ends[k].FE, mid))) < 1 - 1e-6) continue;
                var dOut = (Ed(tEnd + dk * 1e-6 * Delta) - Ed(tEnd)); if (!(dOut.LengthSquared > 0)) continue; dOut.Normalize();
                var m = dOut - Vector3d.Dot(dOut, sTan) * sTan; m -= Vector3d.Dot(m, nF) * nF;
                if (m.Length < 0.5) continue;      // ребро уходит не поперёк шва
                // острие, а не гладкое продолжение: второе ребро торца (другая грань ∩ E) уходит из вершины НАЗАД, вдоль самого
                // ребра. Если оно продолжает ребро за вершину (касательная по ходу) — это гладкий переход в следующее ребро:
                // выбор дополняется по касательным (k150, P3D_DROPENTANPROP), а не конец у острия
                var oc = other.Curve; bool o0 = (oc.PointAt(oc.T0) - V).Length <= (oc.PointAt(oc.T1) - V).Length;
                var oTan = oc.TangentAt(o0 ? oc.T0 : oc.T1); if (!o0) oTan = -oTan;
                if (!(oTan.LengthSquared > 0) || Vector3d.Dot(oTan.Normalized(), dOut) > 0) continue;
                return m.Normalized();
            }
            return null;
        }
        bool SectionSide(int k, double t, int want)
        {
            if (!ch && Spine(t) == null) return false;
            var g = Gen(t);
            for (int i = 0; i <= 16; i++) { double f = Fend(k, g.Point(i / 16.0)); if (double.IsNaN(f) || Math.Sign(f) != want) return false; }
            return true;
        }
        IDisposable? _phase = null; void Phase(string n) { _phase?.Dispose(); _phase = Diagnostics.Prof.Time("open: этап " + n); }
        Phase("торцы");
        var outT = new double[2]; var inT = new double[2]; var tJ = new[] { double.NaN, double.NaN }; var noCut = new bool[2];
        var jC = new Vector3d[2]; var jShT = new Geometry.Ssi.Shape?[2]; var jOffT = new double[2];
        for (int k = 0; k < 2; k++)
        {
            double dk = k == 0 ? -1 : 1, tEnd = k == 0 ? 0 : Delta;
            double speed = (Ed(tEnd - dk * 1e-4 * Delta) - Ed(tEnd)).Length / (1e-4 * Delta);
            if (!(speed > 0)) { why = "open curved: edge parameter degenerate at an end"; return false; }
            double step = Math.Min(2 * r / speed, 0.45 * Delta);
            if (joint[k] != 0 && ch)
            {
                // k146: СТЫК ФАСКИ. Сечение фаски — в плоскости поперёк ребра; у касательной цепочки плоскости двух звеньев в
                // вершине совпадают (общая касательная), и точка на общей грани A — одна. Точка на боковой стороне — общая, если
                // она лежит на гладком шве s (на обеих боковых гранях): у выдавливания шов лежит в плоскости сечения, и это так
                // точно. Иначе (шов наклонён — лофт) следы боковых граней в плоскости сечения расходятся (разная кривизна):
                // отказ с причиной.
                bool onA = joint[k] == 1;
                var qT = onA ? ChA(tEnd) : ChB(tEnd);
                if (double.IsNaN(qT.X) || ends[k].ShE == null) { why = "open curved: chamfer joint — side point not found"; return false; }
                double off = Math.Abs(Geometry.Ssi.Implicit(ends[k].ShE, qT).F);
                if (!(off <= 1e-8 * Math.Max(1, r)))
                {
                    if (!ChamferSwitch) { why = $"open curved: chamfer joint — side points of the two chain edges differ (seam not in the section plane, {off:E1} mm)"; return false; }
                    // k147: ШОВ НАКЛОНЁН. Линия касания на стороне T пересекает шов (T ∩ E) до вершины в точке q = ChT(t*): за t*
                    // боковая точка звена — на соседней грани E (там уже не T). В плоскости стыка она — та же, что у соседнего
                    // звена (плоскость, грань E, расстояние — общие): ленты сходятся по общему отрезку. Звено, у которого
                    // линия касания шов не пересекает, шов не укорачивает (это сделает соседнее). Шов — гладкий в вершине.
                    // признак: шов протыкает плоскость сечения t в точке σ(t); линия касания на шве там, где |σ(t) − E(t)| = d
                    // (функция грани E вдоль линии касания не годится: B и C у шва почти касаются — нуль без смены знака)
                    var seamC = (onA ? ends[k].EA : ends[k].EB).Curve; double dSide = (qT - ends[k].V).Length;
                    bool sw0 = (seamC.PointAt(seamC.T0) - ends[k].V).Length <= (seamC.PointAt(seamC.T1) - ends[k].V).Length;
                    double Us(double f) => sw0 ? seamC.T0 + (seamC.T1 - seamC.T0) * f : seamC.T1 - (seamC.T1 - seamC.T0) * f;
                    Vector3d Sigma(double t)
                    {
                        var P = Ed(t); var T = Td(t); if (double.IsNaN(P.X)) return new Vector3d(double.NaN);
                        double Fp(double u) => Vector3d.Dot(seamC.PointAt(u) - P, T);
                        double fa = Fp(Us(0));
                        for (int i = 1; i <= 64; i++)
                        {
                            double fb = Fp(Us(i / 64.0));
                            if (fa == 0) return seamC.PointAt(Us((i - 1) / 64.0));
                            if (Math.Sign(fb) != Math.Sign(fa)) return seamC.PointAt(RootIn(Fp, Us((i - 1) / 64.0), Us(i / 64.0)));
                            fa = fb;
                        }
                        return new Vector3d(double.NaN);
                    }
                    double Gx(double t) { var sg = Sigma(t); return double.IsNaN(sg.X) ? double.NaN : (sg - Ed(t)).Length - dSide; }
                    // окно — не дальше двух боковых расстояний от вершины вдоль ребра (наклон шва до 60°): дальше нуль функции E —
                    // след продолжения грани E, а не шов
                    double win = Math.Min(0.45 * Delta, 2 * (qT - ends[k].V).Length / speed);
                    double tS = double.NaN, gPrev = Gx(tEnd);
                    const int nSw = 128;
                    for (int i = 1; i <= nSw && double.IsNaN(tS); i++)
                    {
                        double ta = tEnd - dk * win * (i - 1) / nSw, tb = tEnd - dk * win * i / nSw, gv = Gx(tb);
                        if (double.IsFinite(gPrev + gv) && Math.Sign(gv) != Math.Sign(gPrev)) tS = RootIn(Gx, ta, tb);
                        gPrev = gv;
                    }
                    if (double.IsNaN(tS)) { noCut[k] = true; Debug?.Invoke($"open: chamfer joint {k}: side point off the seam by {off:E1}, no crossing on this link (the other one switches)"); }
                    else
                    {
                        var qx = onA ? ChA(tS) : ChB(tS);
                        double offSeam = (seamC.PointAt(CurveParam.Closest(seamC, qx)) - qx).Length;
                        if (offSeam > 1e-6 * Math.Max(1, r)) { why = $"open curved: chamfer joint — the side point crosses the next face off the seam ({offSeam:E1} mm)"; return false; }
                        swT[k] = tS; swOnA[k] = onA; swSh[k] = ends[k].ShE; swQ[k] = qx;
                        var qe = onA ? ChA(tS + dk * 1e-9 * Delta) : ChB(tS + dk * 1e-9 * Delta);
                        if (double.IsNaN(qe.X) || (qe - qx).Length > 1e-6 * Math.Max(1, r)) { why = "open curved: chamfer joint — the side point does not pass onto the next face"; return false; }
                        knotT.Add(tS);
                        qT = onA ? ChA(tEnd) : ChB(tEnd);
                        Interlocked.Increment(ref ChamferSwitches);
                        Debug?.Invoke($"open: chamfer joint {k}: side point passes onto the next face at t {tS:G8} (end {tEnd:G6}), seam point {qx:0.####}");
                    }
                }
                // k150: СЕЧЕНИЕ СТЫКА — ЧЕРЕЗ САМУ ВЕРШИНУ. Конец кривой ребра может отстоять от вершины на допуск ребра (кривая
                // пересечения после прежней правки: 5e-7 мм вдоль хода у s107); сечения двух звеньев в их концах тогда
                // параллельны, но сдвинуты вдоль хода, и стык не сходится. Параметр стыка — тот, где плоскость сечения проходит
                // через вершину (у соседнего звена — тоже): сечения совпадают. Мутация P3D_DRJOINTV=0.
                double tJv = tEnd;
                if (JointThroughV)
                    for (int it = 0; it < 6; it++)
                    {
                        double dh = 1e-6 * Delta; var D = (Ed(tJv + dh) - Ed(tJv - dh)) / (2 * dh);
                        var Pj = Ed(tJv); if (double.IsNaN(Pj.X) || double.IsNaN(D.X) || !(D.LengthSquared > 0)) { tJv = tEnd; break; }
                        double dtj = Vector3d.Dot(ends[k].V - Pj, D) / D.LengthSquared; tJv += dtj;
                        if (Math.Abs(dtj) < 1e-15 * Math.Max(1, Delta)) break;
                    }
                if (!(Math.Abs(tJv - tEnd) <= 1e-5 * Math.Max(1, Delta))) tJv = tEnd;
                if (tJv != tEnd) Interlocked.Increment(ref JointThroughVHits);
                tJ[k] = tJv; inT[k] = tJv; jC[k] = ends[k].V; jShT[k] = ends[k].ShE; jOffT[k] = 0;
                double ext = step;
                while (ext > 1e-6 * step && (double.IsNaN(ChA(tEnd + dk * ext).X) || double.IsNaN(ChB(tEnd + dk * ext).X))) ext /= 2;
                outT[k] = tEnd + dk * ext;
                Debug?.Invoke($"open: chamfer joint {k} at the vertex, side point {qT:0.####} (off the other side {off:E1})");
                continue;
            }
            if (joint[k] != 0 && !ch && RoundSwitch && varLaw == null && ends[k].ShE != null)
            {
                // k148: СТЫК СКРУГЛЕНИЯ У ШВА С ИЗЛОМОМ (лофт: шов гладок только в вершине). Шар катится по общей грани S и по
                // стороне T до шва (точка q1), дальше перекатывается через ребро шва (касается S и кривой шва — край ленты
                // идёт по шву) до q2, где ложится на соседнюю грань E, и до плоскости вершины касается S и E — там он тот же,
                // что у соседнего звена (плоскость, S, E, r — общие). Звено, у которого линия касания шов не пересекает, его
                // не режет (это сделает соседнее). Выпуклый излом; иначе — отказ. Мутация P3D_DRRDSWITCH=0.
                bool onA = joint[k] == 1;
                var shT = onA ? shA : shB; var shS = onA ? shB : shA; double sT = onA ? sA : sB, sS = onA ? sB : sA;
                var sc = (onA ? ends[k].EA : ends[k].EB).Curve; var shE = ends[k].ShE!;
                bool smooth = true;
                for (int i = 0; i <= 8 && smooth; i++)
                {
                    var q = sc.PointAt(sc.T0 + (sc.T1 - sc.T0) * i / 8.0);
                    if (Math.Abs(Vector3d.Dot(Geometry.Ssi.Implicit(shT, q).G.Normalized(), Geometry.Ssi.Implicit(shE, q).G.Normalized())) < 0.9998) smooth = false;
                }
                if (!smooth)
                {
                    var V = ends[k].V;
                    double sEs = Vector3d.Dot(NormalOf(ends[k].FE, V), Geometry.Ssi.Implicit(shE, V).G) > 0 ? 1 : -1;
                    // сторона точки относительно шва на поверхности sh: > 0 — к ребру цепочки (на T), < 0 — за швом
                    // k148g: ориентация стороны — по точке ребра вдали от вершины (у вершины точка ребра совпадает с концом шва, и знак
                    // по Ed(t) — шум: «у самой вершины он неустойчив»; ложные корни HT у s106). Мутация P3D_DRSIDEREF=0.
                    var edRef = Ed(tEnd - dk * Math.Min(0.5 * Math.Min(0.45 * Delta, 6 * r / speed), 0.25 * Delta));
                    double Side(Vector3d Q, Geometry.Ssi.Shape sh, double t)
                    {
                        double u = CurveParam.Closest(sc, Q); var sg = sc.PointAt(u);
                        var w = Vector3d.Cross(Geometry.Ssi.Implicit(sh, sg).G, sc.TangentAt(u)).Normalized();
                        if (Vector3d.Dot((SideRef ? edRef : Ed(t)) - sg, w) < 0) w = -w;
                        return Vector3d.Dot(Q - sg, w);
                    }
                    double H(double t, Geometry.Ssi.Shape shX, double sX)
                    {
                        var x = onA ? SpineWith(t, shX, sX, shB, sB) : SpineWith(t, shA, sA, shX, sX);
                        return x == null ? double.NaN : Side(Foot(shX, x.Value), shX, t);
                    }
                    double HT(double t) => H(t, shT, sT);
                    double HE(double t) => H(t, shE, sEs);
                    double win = Math.Min(0.45 * Delta, 6 * r / speed);
                    const int nSw = 192;
                    double h0 = HT(tEnd);
                    if (Debug != null)
                        for (int i = 0; i <= 12; i++)
                        {
                            double tt = tEnd - dk * win * i / 12.0;
                            var xa = onA ? SpineWith(tt, shT, sT, shB, sB) : SpineWith(tt, shA, sA, shT, sT);
                            var xb = onA ? SpineWith(tt, shE, sEs, shB, sB) : SpineWith(tt, shA, sA, shE, sEs);
                            string dd = xa != null && xb != null ? $"{(xa.Value - xb.Value).Length:E2} fE(xT) {sigma * sEs * Geometry.Ssi.Implicit(shE, xa.Value).F / r - 1:E2} fT(xE) {sigma * sT * Geometry.Ssi.Implicit(shT, xb.Value).F / r - 1:E2}" : "-";
                            Debug($"open: round joint {k} scan t {tt:G6}: HT {HT(tt):E2} HE {HE(tt):E2} centres {dd}");
                        }
                    // k148f: ВОГНУТЫЙ ИЗЛОМ — ПЕРЕХОД ЧЕРЕЗ ШАР, КАСАЮЩИЙСЯ ТРЁХ ГРАНЕЙ. У вершины шар звена на S и T входит в соседнюю
                    // грань E (fE(xT) < 0), а шар на S и E — годен; вдали — наоборот. Где шар звена перестаёт помещаться, оба шара
                    // касаются S, T и E — это один шар (центры совпадают): хребет переходит с T на E без шва, линия касания стороны —
                    // с T на E в его точке; у вершины шар — тот же, что у соседнего звена (корпус s110, s106: шаг 0 — резцом,
                    // каскад). Проверки: центры в точке перехода совпадают (1e-7), шар на E за ней годен (касание на E, в T не
                    // входит). Мутация P3D_DRCREASE3=0.
                    double PenE(double t) { var x = onA ? SpineWith(t, shT, sT, shB, sB) : SpineWith(t, shA, sA, shT, sT); return x == null ? double.NaN : sigma * sEs * Geometry.Ssi.Implicit(shE, x.Value).F / r - 1; }
                    if (Crease3 && PenE(tEnd) < 0)
                    {
                        double tS = double.NaN, gp = PenE(tEnd - dk * win);
                        for (int i = 1; i <= nSw && double.IsNaN(tS); i++)
                        {
                            double ta = tEnd - dk * win * (nSw - i + 1) / nSw, tb = tEnd - dk * win * (nSw - i) / nSw, gv = PenE(tb);
                            if (double.IsFinite(gp + gv) && gp >= 0 && gv < 0) tS = RootIn(PenE, ta, tb);
                            gp = gv;
                        }
                        if (!double.IsNaN(tS))
                        {
                            var xT3 = onA ? SpineWith(tS, shT, sT, shB, sB) : SpineWith(tS, shA, sA, shT, sT);
                            var xE3 = onA ? SpineWith(tS, shE, sEs, shB, sB) : SpineWith(tS, shA, sA, shE, sEs);
                            double dC = xT3 != null && xE3 != null ? (xT3.Value - xE3.Value).Length : double.NaN;
                            bool eOk = true;
                            for (int i = 1; i <= 16 && eOk; i++)
                            {
                                double tq3 = tS + (tEnd - tS) * i / 16.0;
                                double he = HE(tq3); var xe = onA ? SpineWith(tq3, shE, sEs, shB, sB) : SpineWith(tq3, shA, sA, shE, sEs);
                                double pt = xe == null ? double.NaN : sigma * sT * Geometry.Ssi.Implicit(shT, xe.Value).F / r - 1;
                                if (!(he < 1e-9 * Math.Max(1, r)) || !(pt > -1e-9)) eOk = false;
                            }
                            Debug?.Invoke($"open: round joint {k}: concave crease — three-face ball at t {tS:G8} (centres {dC:E2}), ball on E valid to the vertex {eOk}");
                            if (Debug != null && xT3 != null)
                            {
                                var c3 = xT3.Value; var qS3 = Foot(shS, c3); var qT3 = Foot(shT, c3); var qE3 = Foot(shE, c3);
                                var sgT = sc.PointAt(CurveParam.Closest(sc, qT3)); var sgE = sc.PointAt(CurveParam.Closest(sc, qE3));
                                Debug($"open: crease ball centre {c3:0.####} r {r:G4}; qS {qS3:0.####} qT {qT3:0.####} qE {qE3:0.####}; |qT−qE| {(qT3 - qE3).Length:G4}; seam from qT {(qT3 - sgT).Length:G4} side {Side(qT3, shT, tS):G4}, from qE {(qE3 - sgE).Length:G4} side {Side(qE3, shE, tS):G4}; edge pt {Ed(tS):0.####} tangent {Td(tS):0.###}; vertex {ends[k].V:0.####}");
                            }
                            // точка шва в плоскости сечения t*
                            Vector3d? seamPt = null;
                            // k148f: СКОС ПО ПЕРЕСЕЧЕНИЮ (как ChFi3d PerformTwoCornerbyInter / SMLib SmFilletNx2Corner, bevel): лента T
                            // продолжается за t* до шва (край на T бьёт в шов в B1), лента E — назад до шва (B2). Дальняя от вершины —
                            // «большая»: она обрезается гранью меньшей (обрыв), ленты режут друг друга (митра от qS до PMil — точки,
                            // где край меньшей на её грани входит в большую). Шов укорачивается до точки большей.
                            if (CreaseBevelOn && dC <= 1e-7 * Math.Max(1, r) && eOk && xE3 != null)
                            {
                                Func<double, Vector3d?> spT = t => onA ? SpineWith(t, shT, sT, shB, sB) : SpineWith(t, shA, sA, shT, sT);
                                Func<double, Vector3d?> spE = t => onA ? SpineWith(t, shE, sEs, shB, sB) : SpineWith(t, shA, sA, shE, sEs);
                                var bv = CreaseBevelGeom(k, tS, dk, win, xT3!.Value, sc, shT, shE, shS, spT, spE);
                                if (bv == null) { why = "open curved: concave crease — bevel corner not built"; Debug?.Invoke(why); return false; }
                                crBv[k] = bv; seamPt = bv.B;
                            }
                            else if (dC <= 1e-7 * Math.Max(1, r) && eOk)
                            {
                                var Pp = Ed(tS); var Tp = Td(tS);
                                double G(double u) => Vector3d.Dot(sc.PointAt(u) - Pp, Tp);
                                double gPv = G(sc.T0);
                                for (int i = 1; i <= 256 && seamPt == null; i++)
                                {
                                    double ua = sc.T0 + (sc.T1 - sc.T0) * (i - 1) / 256.0, ub = sc.T0 + (sc.T1 - sc.T0) * i / 256.0, gv = G(ub);
                                    if (Math.Sign(gv) != Math.Sign(gPv) && double.IsFinite(gv + gPv)) { double ru = RootIn(G, ua, ub); if (double.IsFinite(ru)) seamPt = sc.PointAt(ru); }
                                    gPv = gv;
                                }
                                if (seamPt == null) Debug?.Invoke("open: concave crease — the seam does not cross the section plane");
                            }
                            if (dC <= 1e-7 * Math.Max(1, r) && eOk && seamPt != null)
                            {
                                var qE = Foot(shE, xE3!.Value);
                                crOn[k] = true; crT[k] = tS; crC[k] = xT3!.Value; crQS[k] = Foot(shS, xT3.Value); crQT[k] = Foot(shT, xT3.Value); crQE[k] = qE;
                                crSeamPt[k] = seamPt.Value; crShT[k] = shT; crShE[k] = shE; crSE[k] = sEs;
                                rgSeam[k] = sc; rgShE[k] = shE; rgSE[k] = sEs; rgOnA[k] = onA; rgT[k] = tS; rgE[k] = tS;
                                spMemo.Clear();
                                swT[k] = tS; swOnA[k] = onA; swSh[k] = shE; swQ[k] = crQT[k]; swQ2[k] = qE; swT2[k] = double.NaN;
                                knotT.Add(tS);
                                Interlocked.Increment(ref CreaseSwitches);
                                tJ[k] = tEnd; inT[k] = tEnd; jC[k] = S(tEnd); jShT[k] = shE; jOffT[k] = sigma * sEs * r;
                                if (double.IsNaN(jC[k].X)) { why = "open curved: round joint — the ball at the vertex not found"; Debug?.Invoke(why); return false; }
                                double extC = step;
                                while (extC > 1e-6 * step && Spine(tEnd + dk * extC) == null) extC /= 2;
                                outT[k] = tEnd + dk * extC;
                                continue;
                            }
                        }
                    }
                    // k148f: у вершины шар звена на S и T не входит в E, а шар на S и E входит в T — шар звена и есть шар стыка
                    // (соседнее звено перейдёт на него через шар трёх граней): шва не пересекаем, какой бы ни вышел знак стороны
                    // (у самой вершины он неустойчив)
                    bool ownBall = false;
                    if (Crease3 && h0 < 0)
                    {
                        var xe0 = onA ? SpineWith(tEnd, shE, sEs, shB, sB) : SpineWith(tEnd, shA, sA, shE, sEs);
                        double penTE = xe0 == null ? double.NaN : sigma * sT * Geometry.Ssi.Implicit(shT, xe0.Value).F / r - 1;
                        // k149: вхождение шара на E в поверхность T считается, только если его основание на T — по сторону грани T
                        // (s106: шов у вершины наклонён над T, основание — за швом, грани T там нет; шар звена негоден — касание за
                        // швом, и звено переходит через шов). Прежде — «свой шар», и шары звеньев расходились на 3.8e-4 мм.
                        // Мутация P3D_DRCREASEREV=0.
                        bool footOnT = !CreaseRev || xe0 == null || Side(Foot(shT, xe0.Value), shT, tEnd) > 0;
                        if (!footOnT) Debug?.Invoke($"open: round joint {k}: the ball on E enters the surface of T beyond the seam (no face T there) — not the own ball");
                        if (PenE(tEnd) >= 0 && penTE < 0 && footOnT) { ownBall = true; Debug?.Invoke($"open: round joint {k}: own ball at the vertex (clear of E; the ball on E enters T), no seam crossing"); }
                    }
                    if (!(h0 < 0) || ownBall)
                    {
                        if (double.IsNaN(h0)) { why = "open curved: round joint — the ball at the vertex not found"; Debug?.Invoke(why); return false; }
                        noCut[k] = true;
                        Debug?.Invoke($"open: round joint {k}: contact inside the side face at the vertex ({h0:E1}), no seam crossing on this link");
                    }
                    else
                    {
                        double t1 = double.NaN, gPrev = h0;
                        for (int i = 1; i <= nSw && double.IsNaN(t1); i++)
                        {
                            double ta = tEnd - dk * win * (i - 1) / nSw, tb = tEnd - dk * win * i / nSw, gv = HT(tb);
                            if (double.IsFinite(gPrev + gv) && Math.Sign(gv) != Math.Sign(gPrev)) t1 = RootIn(HT, ta, tb);
                            gPrev = gv;
                        }
                        if (double.IsNaN(t1)) { why = "open curved: round joint — the contact line does not reach the seam"; Debug?.Invoke(why); return false; }
                        double e1 = HE(t1), eEnd = HE(tEnd);
                        Debug?.Invoke($"open: round joint {k}: t1 {t1:G8} HE {e1:E2}..{eEnd:E2} h0 {h0:E2} win {win:G4}");
                        // k148g: ШАР НА ШВЕ ДО ВЕРШИНЫ. Точка касания на E по эту сторону шва до самой вершины (HE < 0 на всём [t1, конец]):
                        // шар не ложится на E, а катится по ребру шва до плоскости вершины; у соседнего звена — то же зеркально, и в
                        // плоскости вершины шар (касается S и кривой шва) — один. Край ленты от q1 — по шву до конца звена (как кусок
                        // на шве у переката). Прежде — отказ. Мутация P3D_DRSEAMTOV=0.
                        bool toV = false;
                        if (SeamToVertex && e1 < 0 && eEnd < 0)
                        {
                            toV = true;
                            for (int i = 1; i <= 16 && toV; i++) if (!(HE(t1 + (tEnd - t1) * i / 16.0) < 0)) toV = false;
                            if (toV) Debug?.Invoke($"open: round joint {k}: the ball stays on the seam to the vertex (E contact before the seam: {e1:E1}..{eEnd:E1})");
                        }
                        if (!(e1 > 0 && eEnd < 0) && !toV) { why = $"open curved: round joint — the ball does not pass onto the next face before the vertex ({e1:E1}/{eEnd:E1}, concave crease?)"; Debug?.Invoke(why); return false; }
                        double t2 = toV ? tEnd + dk * 10 * Delta : double.NaN; gPrev = e1;
                        for (int i = 1; i <= nSw && double.IsNaN(t2); i++)
                        {
                            double ta = t1 + (tEnd - t1) * (i - 1) / nSw, tb = t1 + (tEnd - t1) * i / nSw, gv = HE(tb);
                            if (double.IsFinite(gPrev + gv) && Math.Sign(gv) != Math.Sign(gPrev)) t2 = RootIn(HE, ta, tb);
                            gPrev = gv;
                        }
                        if (double.IsNaN(t2)) { why = "open curved: round joint — the ball does not pass onto the next face"; Debug?.Invoke(why); return false; }
                        rgSeam[k] = sc; rgShE[k] = shE; rgSE[k] = sEs; rgOnA[k] = onA; rgT[k] = t1; rgE[k] = t2;
                        spMemo.Clear();
                        // стыковка режимов: хребет и точка касания непрерывны в t1 и t2
                        var xT = onA ? SpineWith(t1, shT, sT, shB, sB) : SpineWith(t1, shA, sA, shT, sT);
                        double t2c = toV ? tEnd : t2;
                        var xS1 = SeamSpine(k, t1); var xS2 = SeamSpine(k, t2c);
                        var xE = toV ? xS2 : onA ? SpineWith(t2, shE, sEs, shB, sB) : SpineWith(t2, shA, sA, shE, sEs);
                        if (xT == null || xS1 == null || xS2 == null || xE == null) { rgT[k] = double.NaN; why = "open curved: round joint — the ball on the seam not found"; Debug?.Invoke(why); return false; }
                        double j1 = (xT.Value - xS1.Value).Length, j2 = (xE.Value - xS2.Value).Length;
                        var q1 = Foot(shT, xT.Value); var q2 = toV ? sc.PointAt(CurveParam.Closest(sc, xE.Value)) : Foot(shE, xE.Value);
                        double o1 = (sc.PointAt(CurveParam.Closest(sc, q1)) - q1).Length, o2 = (sc.PointAt(CurveParam.Closest(sc, q2)) - q2).Length;
                        if (j1 > 1e-7 * Math.Max(1, r) || j2 > 1e-7 * Math.Max(1, r) || o1 > 1e-7 || o2 > 1e-7)
                        { rgT[k] = double.NaN; why = $"open curved: round joint — regimes do not meet (centres {j1:E1}/{j2:E1}, contacts off the seam {o1:E1}/{o2:E1})"; Debug?.Invoke(why); return false; }
                        // шар на шве перекатывается через ребро: направление от шва к центру — между нормалями сторон (в сторону
                        // шара); сравнение с продолжениями граней за швом не годится (у выпуклого излома продолжение одной
                        // грани лежит над другой)
                        for (int i = 1; i < 8; i++)
                        {
                            double tmS = t1 + (t2c - t1) * i / 8.0; var xm = SeamSpine(k, tmS);
                            if (xm == null) { rgT[k] = double.NaN; why = "open curved: round joint — the ball on the seam not found"; Debug?.Invoke(why); return false; }
                            var sg = sc.PointAt(CurveParam.Closest(sc, xm.Value)); var d = (xm.Value - sg).Normalized();
                            var mT = Geometry.Ssi.Implicit(shT, sg).G.Normalized() * (sigma * sT); var mE = Geometry.Ssi.Implicit(shE, sg).G.Normalized() * (sigma * sEs);
                            var ax = Vector3d.Cross(mT, mE); double sn = ax.Length;
                            if (sn < 1e-6) continue;
                            double c1 = Vector3d.Dot(Vector3d.Cross(mT, d), ax) / sn, c2 = Vector3d.Dot(Vector3d.Cross(d, mE), ax) / sn;
                            if (c1 < -1e-9 * sn || c2 < -1e-9 * sn) { rgT[k] = double.NaN; why = $"open curved: round joint — the ball on the seam is outside the crease ({c1:E1}/{c2:E1})"; Debug?.Invoke(why); return false; }
                        }
                        swT[k] = t1; swOnA[k] = onA; swSh[k] = shE; swQ[k] = q1; swQ2[k] = q2; swT2[k] = toV ? double.NaN : t2;
                        knotT.Add(t1); if (!toV) knotT.Add(t2);
                        if (toV) Interlocked.Increment(ref SeamToVertexHits);
                        Interlocked.Increment(ref RoundSwitches);
                        Debug?.Invoke($"open: round joint {k}: onto the seam at t {t1:G8} ({q1:0.####}), onto the next face at t {t2:G8} ({q2:0.####}), end {tEnd:G6}");
                    }
                    tJ[k] = tEnd; inT[k] = tEnd; jC[k] = S(tEnd); jShT[k] = shE; jOffT[k] = sigma * sEs * r;
                    if (double.IsNaN(jC[k].X)) { why = "open curved: round joint — the ball at the vertex not found"; Debug?.Invoke(why); return false; }
                    double ext = step;
                    while (ext > 1e-6 * step && Spine(tEnd + dk * ext) == null) ext /= 2;
                    outT[k] = tEnd + dk * ext;
                    continue;
                }
            }
            if (joint[k] != 0)
            {
                // k145: СЕЧЕНИЕ СТЫКА. На гладком ребре s (сторона T) — точка q, где шар, касающийся T в q (центр q + ∇T·offT),
                // касается и общей грани S (F_S(центр) = offS); сечение ленты стыка — то, чей хребет проходит через этот центр.
                bool onA = joint[k] == 1;
                var shT = onA ? shA : shB; var shS = onA ? shB : shA; double offT = onA ? offA : offB, offS = onA ? offB : offA;
                var sc = (onA ? ends[k].EA : ends[k].EB).Curve;
                bool v0 = (sc.PointAt(sc.T0) - ends[k].V).Length <= (sc.PointAt(sc.T1) - ends[k].V).Length;
                Vector3d Centre(double u) { var q = sc.PointAt(u); var g = Geometry.Ssi.Implicit(shT, q).G; return q + g.Normalized() * offT; }
                double Gs(double u) => Geometry.Ssi.Implicit(shS, Centre(u)).F - offS;
                double U(double f) => v0 ? sc.T0 + (sc.T1 - sc.T0) * f : sc.T1 - (sc.T1 - sc.T0) * f;
                double uStar = double.NaN, gPrev = Gs(U(0));
                for (int i = 1; i <= 128 && double.IsNaN(uStar); i++)
                {
                    double g = Gs(U(i / 128.0));
                    if (double.IsFinite(gPrev + g) && Math.Sign(g) != Math.Sign(gPrev)) uStar = RootIn(Gs, U((i - 1) / 128.0), U(i / 128.0));
                    gPrev = g;
                }
                if (double.IsNaN(uStar)) { why = "open curved: joint — the blend does not cross the smooth edge"; return false; }
                var cJ = Centre(uStar);
                double Hs(double t) { var P = Ed(t); return double.IsNaN(P.X) ? double.NaN : Vector3d.Dot(cJ - P, Td(t)); }
                double tj = double.NaN, best = double.MaxValue;
                const int nJ = 96;
                double hPrev = Hs(tEnd - dk * 3 * step);
                for (int i = 1; i <= nJ; i++)
                {
                    double ta = tEnd - dk * 3 * step + dk * 6 * step * (i - 1) / nJ, tb = tEnd - dk * 3 * step + dk * 6 * step * i / nJ;
                    double hj = Hs(tb);
                    if (double.IsFinite(hPrev + hj) && Math.Sign(hj) != Math.Sign(hPrev))
                    {
                        double rt = RootIn(Hs, ta, tb);
                        if (double.IsFinite(rt) && Spine(rt) is { } sp && (sp - cJ).Length < best) { best = (sp - cJ).Length; tj = rt; }
                    }
                    hPrev = hj;
                }
                if (double.IsNaN(tj) || best > 1e-7 * Math.Max(1, r)) { why = $"open curved: joint section not found (centre off the spine {best:E1})"; return false; }
                var qT = Foot(shT, S(tj));
                if ((qT - sc.PointAt(uStar)).Length > 1e-7) { why = "open curved: joint contact is off the smooth edge"; return false; }
                tJ[k] = tj; inT[k] = tj; jC[k] = cJ; jShT[k] = shT; jOffT[k] = offT;
                double ext = step;
                while (ext > 1e-6 * step && Spine(tj + dk * ext) == null) ext /= 2;
                outT[k] = tj + dk * ext;
                Debug?.Invoke($"open: joint {k} at t {tj:G8} (end {tEnd:G6}), smooth edge on {(onA ? "A" : "B")}, contact {qT:0.####}");
                continue;
            }
            double fBeyond = Fend(k, Ed(tEnd + dk * 1e-4 * step)), fBefore = Fend(k, Ed(tEnd - dk * 1e-4 * step));
            if (!(fBeyond * fBefore < 0) && CuspEnd && !two[k] && CuspDir(k, tEnd, dk) is { } mcu)
            {
                // k150: ОСТРИЕ (карман со скруглённой кромкой, Sweep_Blend): ребро торец только касается — торец касается грани
                // ребра вдоль шва (A ∩ E), а лента торец пересекает целиком. Конец — по «за торцом или за швом». Мутация P3D_DRCUSPEND=0.
                cusp[k] = mcu;
                fBeyond = Fend(k, Ed(tEnd + dk * 1e-4 * step)); fBefore = Fend(k, Ed(tEnd - dk * 1e-4 * step));
                Interlocked.Increment(ref CuspEnds);
                Debug?.Invoke($"open: end {k} at a cusp: end face tangent to a face of the edge along the seam, beyond the seam {mcu:0.###}, f {fBefore:E2}/{fBeyond:E2}");
            }
            // Я2: у двухгранного торца само ребро идёт в вершину по ребру излома двух торцов (f1 и f2 у конца ребра меняют знак
            // навстречу друг другу, max(f1, f2) знака не меняет) — проверка «ребро пересекает торец» тут не о том; пересечение
            // торца сечениями ленты проверяется ниже (сечение целиком за торцом, скобка края)
            if (two[k]) { fBefore = -1; fBeyond = 1; }
            if (!(fBeyond * fBefore < 0))
            {
                if (two[k] && Debug != null)
                {
                    foreach (double sg in new[] { -1.0, 1.0 })
                    {
                        var xx = Ed(tEnd + sg * dk * 1e-4 * step);
                        Debug($"open: end {k} two-face {(sg < 0 ? "before" : "beyond")}: f1 {ends[k].SE * Geometry.Ssi.Implicit(ends[k].ShE, xx).F:E3} f2 {se2[k] * Geometry.Ssi.Implicit(sh2[k]!, xx).F:E3} (convex {conv2[k]}, endSign {endSign[k]}), at {xx:0.#####}, V {ends[k].V:0.#####}, tEnd {tEnd:G8} Δ {Delta:G8}");
                    }
                }
                why = $"open curved: the edge does not cross the end face (end {k} at {ends[k].V}, face {ends[k].FE.Surf.GetType().Name} {ends[k].FE.Tag}, f {fBefore:E2}/{fBeyond:E2})"; return false;
            }
            if (fBeyond < 0) endSign[k] = -1;
            // k144b: доли шага — только при гранях NURBS (стенки лофта и их продолжения): у аналитических граней отказ удвоением
            // уводит ребро на другой маршрут (цилиндр ∪ шар — фаска 3/5 на кривой пересечения: с долями лента строится здесь,
            // но вдвое-втрое большим числом точек и с отказом точности у фаски 5; KRN06-FF-CYL-SPHERE-BLENDS)
            bool fine = EndFine && (FA.Surf is NurbsSurface || FB.Surf is NurbsSurface || ends[k].FE.Surf is NurbsSurface);
            double h = step; bool found = false;
            bool holeOut = false;
            for (int it = 0; it < 8 && !found; it++, h *= 2)
            {
                if (SectionSide(k, tEnd + dk * h, 1)) { outT[k] = tEnd + dk * h; found = true; }
                else if (!ch && Spine(tEnd + dk * h) == null) holeOut = true;
            }
            // k148f: ХРЕБЕТ НЕ ОПРЕДЕЛЁН НА ПЕРВЫХ ШАГАХ — сечение за торцом ближе. Шаг — два радиуса по ходу ребра; у малого
            // радиуса на кромке лофта хребет (шар на продолжениях граней) за 1.4 мм от конца уже не находится, а удвоение шага
            // находит сечение «за торцом» в 46 мм — скобка торца с неопределёнными сечениями, отказ («section undefined in the end
            // bracket»; корпус s101 шаг 4, R1.43). Тогда — доли первого шага (сечение целиком за торцом уже в 0.36 мм).
            // Мутация P3D_DRENDHOLE=0.
            // только повтором после отказа «section undefined in the end bracket» (у прогрева ядра — цилиндр ∪ шар, R1 — ближнее
            // сечение вело к отказу таблицы ленты, а прежний выбор годен)
            if (found && holeOut && EndHole && _endHole)
                for (int j = 1; j < 8; j++)
                    if (SectionSide(k, tEnd + dk * step * j / 8, 1)) { if (EndScanDbg) Console.Error.WriteLine($"ENDHOLE end {k}: far {outT[k] - tEnd:G4} → {dk * step * j / 8:G4} (step {step:G4}, r {r:G4})"); outT[k] = tEnd + dk * step * j / 8; Interlocked.Increment(ref EndHoleHits); break; }
            if (Debug != null && EndScanDbg)
                for (double hh = step / 8; hh <= step * 300; hh *= 2)
                {
                    double tt = tEnd + dk * hh; var sp = Spine(tt);
                    string fsx = sp == null ? "" : string.Join(" ", Enumerable.Range(0, 9).Select(i => Fend(k, Gen(tt).Point(i / 8.0)).ToString("F3")));
                    Debug($"open end {k} scan: t {tt:F4} (end {tEnd:F4}, step {step:G4}) spine {(sp == null ? "null" : sp.Value.ToString("0.###"))} f [{fsx}]");
                }
            // k144: ПРОДЛЕНИЕ КОРОЧЕ ПЕРВОГО ШАГА. Шаг — два радиуса по ходу ребра; у грани NURBS, продолженной за край области,
            // точка ленты на таком удалении может не найтись (NaN), хотя сечение выходит за торец раньше (окно Sweep_Blend:
            // стенка лофта, фаска 3 — сечение целиком за торцом уже в 3–4 мм, а на 6 мм проекция на продолжение стенки не сходится).
            // Доли первого шага — только после отказа удвоением (прежние удачные случаи не меняются). Мутация P3D_DRENDFINE=0.
            for (int j = 1; j < 8 && !found && fine; j++) if (SectionSide(k, tEnd + dk * step * j / 8, 1)) { outT[k] = tEnd + dk * step * j / 8; found = true; Interlocked.Increment(ref EndFineHits); }
            if (!found && Debug != null)
                for (double hh = step; hh <= step * 300; hh *= 2)
                {
                    double tt = tEnd + dk * hh; var sp = Spine(tt);
                    string fsx = "";
                    if (sp != null) { var g = Gen(tt); fsx = string.Join(" ", Enumerable.Range(0, 17).Select(i => Fend(k, g.Point(i / 16.0)).ToString("F2"))); }
                    Debug($"open end {k}: t {tt:F4} (Δ {Delta:F4}) spine {(sp == null ? "null" : sp.Value.ToString("0.##"))} edge {Ed(tt):0.##} f [{fsx}]");
                }
            if (!found) { why = "open curved: the blend does not leave the end face (end face too oblique?)"; return false; }
            h = step; found = false;
            for (int it = 0; it < 8 && !found; it++, h *= 2) if (SectionSide(k, tEnd - dk * h, -1)) { inT[k] = tEnd - dk * h; found = true; }
            // k144: и внутрь — долями первого шага (у большого радиуса на коротком ребре первый шаг 0.45 ребра уже за серединой)
            for (int j = 7; j >= 1 && !found && fine; j--) if (SectionSide(k, tEnd - dk * step * j / 8, -1)) { inT[k] = tEnd - dk * step * j / 8; found = true; Interlocked.Increment(ref EndFineHits); }
            if (!found && Debug != null)
                for (double hh = step; hh <= step * 256; hh *= 2)
                {
                    double tt = tEnd - dk * hh; var sp = Spine(tt);
                    string fsx = "";
                    if (sp != null) { var g = Gen(tt); fsx = string.Join(" ", Enumerable.Range(0, 17).Select(i => Fend(k, g.Point(i / 16.0)).ToString("F2"))); }
                    Debug($"open end {k} inside: t {tt:F4} (Δ {Delta:F4}) spine {(sp == null ? "null" : sp.Value.ToString("0.##"))} edge {Ed(tt):0.##} f [{fsx}]");
                }
            if (!found) { why = "open curved: no blend section inside the end face"; return false; }
        }
        if (!(inT[0] < inT[1])) { Debug?.Invoke($"open: end brackets in {inT[0]:G6}/{inT[1]:G6} out {outT[0]:G6}/{outT[1]:G6} (Δ {Delta:G6}, two {two[0]}/{two[1]})"); why = "open curved: the edge is too short for the radius"; return false; }
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
        double TStar(int k, double v) => Root(k, t => { Interlocked.Increment(ref EndRootEvals); return Fend(k, Gen(t).Point(v)); });
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
            if (joint[k] != 0) return Gen(tJ[k]).Point(v);
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
        Phase("знак в скобке");
        // одна смена знака в скобке у каждой пробы v
        for (int k = 0; k < 2; k++)
            foreach (double v in joint[k] != 0 ? Array.Empty<double>() : new[] { 0, 0.2, 0.4, 0.6, 0.8, 1.0 })
            {
                int changes = 0; double prev = Fend(k, Gen(inT[k]).Point(v));
                // 20:17: сечение в скобке не находится (хребет не сошёлся) — отказ с причиной, а не исключение Math.Sign(NaN);
                // причина ведёт к повтору с параметром по опорной кривой
                if (double.IsNaN(prev)) { Debug?.Invoke($"open: end {k} bracket: NaN at the inner end t {inT[k]:G6} v {v} (point {Gen(inT[k]).Point(v):0.####}, end face {ends[k].FE.Surf.GetType().Name} {ends[k].FE.Tag})"); why = "open curved: no blend section inside the end face (section undefined in the end bracket)"; return false; }
                for (int i = 1; i <= 32; i++)
                {
                    double f = Fend(k, Gen(inT[k] + (outT[k] - inT[k]) * i / 32.0).Point(v));
                    if (double.IsNaN(f)) { Debug?.Invoke($"open: end {k} bracket: NaN at t {inT[k] + (outT[k] - inT[k]) * i / 32.0:G6} (in {inT[k]:G6}, out {outT[k]:G6}) v {v} (point {Gen(inT[k] + (outT[k] - inT[k]) * i / 32.0).Point(v):0.####}, end face {ends[k].FE.Surf.GetType().Name} {ends[k].FE.Tag})"); why = "open curved: no blend section inside the end face (section undefined in the end bracket)"; return false; }
                    if (Math.Sign(f) != Math.Sign(prev)) changes++; prev = f;
                }
                if (changes != 1) { why = $"open curved: blend section crosses end face {changes} times"; return false; }
            }
        double TS(int k, double v) => joint[k] != 0 ? tJ[k] : TStar(k, v);
        double tA0 = TS(0, 0), tB0 = TS(0, 1), tA1 = TS(1, 0), tB1 = TS(1, 1);
        tEndA[0] = tA0; tEndA[1] = tA1; tEndB[0] = tB0; tEndB[1] = tB1;
        endTab = new double[2][];
        // 20:17 (скорость): НЕЗАВИСИМЫЕ ПРОБЫ — В НЕСКОЛЬКО ПОТОКОВ (корни края на торцах, хребет и сечения сверок, сверка таблицы
        // с формулой): каждая проба — функция только своего параметра (память хребта и ребра — общая потокобезопасная), решения
        // принимаются по порядку проб, как прежде. Мутация P3D_DROPENPAR=0.
        int parThreads = OpenPar ? Math.Max(1, Math.Min(Tess.Tessellator.Threads, Environment.ProcessorCount)) : 1;
        void For(int n, Action<int> body)
        {
            if (parThreads > 1 && n >= 8) { System.Threading.Tasks.Parallel.For(0, n, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parThreads }, body); Interlocked.Increment(ref OpenParallelRuns); }
            else for (int i = 0; i < n; i++) body(i);
        }
        Phase("таблица края");
        endTab[0] = new double[65]; endTab[1] = new double[65];
        if (StarCont)
        {
            // 21.10 (скорость): КРАЙ ЛЕНТЫ ПРОДОЛЖЕНИЕМ. Корень t*(v) на торце k гладко зависит от v: соседний корень — затравка, скобка
            // ±(2·шаг соседей) около него (расширяется, пока знак не сменится); Иллинойс в узкой скобке — 6–8 сечений вместо 20–30
            // в полной [inT, outT]. Концы — те же корни, что у линий касания. Два торца — параллельно. Мутация P3D_DRSTARCONT=0.
            endTab[0][0] = tA0; endTab[0][64] = tB0; endTab[1][0] = tA1; endTab[1][64] = tB1;
            For(2, k =>
            {
                if (joint[k] != 0) { Array.Fill(endTab[k], tJ[k]); return; }
                double prev = endTab[k][0], prev2 = double.NaN;
                for (int i = 1; i < 64; i++)
                {
                    double v = i / 64.0;
                    double guess = double.IsNaN(prev2) ? prev : 2 * prev - prev2;
                    double w = Math.Max(1e-9 * Math.Max(1, Delta), double.IsNaN(prev2) ? Math.Abs(endTab[k][64] - endTab[k][0]) / 32 : 2 * Math.Abs(prev - prev2));
                    double r = double.NaN;
                    Func<double, double> g = tt => { Interlocked.Increment(ref EndRootEvals); return Fend(k, Gen(tt).Point(v)); };
                    double lo = Math.Min(inT[k], outT[k]), hi = Math.Max(inT[k], outT[k]);
                    for (int it = 0; it < 8 && double.IsNaN(r); it++, w *= 4)
                    {
                        double a = Math.Max(lo, guess - w), b = Math.Min(hi, guess + w);
                        double fa = g(a), fb = g(b);
                        if (double.IsNaN(fa + fb) || Math.Sign(fa) == Math.Sign(fb)) continue;
                        r = RootIn(g, a, b);
                    }
                    if (double.IsNaN(r)) { r = TStar(k, v); Interlocked.Increment(ref StarContFallbacks); }
                    endTab[k][i] = r; prev2 = prev; prev = r;
                }
            });
        }
        else For(130, j => { int k = j / 65, i = j % 65; endTab[k][i] = TS(k, i / 64.0); });
        for (int k = 0; k < 2; k++)
            if (endTab[k].Any(double.IsNaN))
            {
                if (Debug != null)
                {
                    int bad = Array.FindIndex(endTab[k], double.IsNaN); double v = bad / 64.0;
                    Debug($"open end curve {k}: NaN at v {v:G4} ({endTab[k].Count(double.IsNaN)} of 65), bracket [{inT[k]:G6}, {outT[k]:G6}] f {Fend(k, Gen(inT[k]).Point(v)):G4} / {Fend(k, Gen(outT[k]).Point(v)):G4}; tA {tA0:G6}/{tA1:G6} tB {tB0:G6}/{tB1:G6}");
                }
                why = "open curved: end curve not found"; return false;
            }
        if (double.IsNaN(tA0 + tB0 + tA1 + tB1) || !(tA0 < tA1) || !(tB0 < tB1)) { why = "open curved: contact lines do not span between the end faces"; return false; }

        Phase("промежуток");
        // промежуток поверхности — по краям ленты на торцах (пробы v) с запасом 2 % ребра
        double tLo = double.MaxValue, tHi = double.MinValue;
        var tsv = new double[34];
        if (StarCont) for (int j = 0; j < 34; j++) tsv[j] = endTab[j / 17][(j % 17) * 4];   // те же v (i/16 = 4i/64) — из таблицы края
        else For(34, j => tsv[j] = TS(j / 17, (j % 17) / 16.0));
        for (int i = 0; i <= 16; i++)
        {
            double t0s = tsv[i], t1s = tsv[17 + i];
            if (double.IsNaN(t0s + t1s)) { why = "open curved: end curve not found"; return false; }
            tLo = Math.Min(tLo, t0s); tHi = Math.Max(tHi, t1s);
        }
        // запас 2 % ребра, но не дальше продления за торец (малый радиус на длинном ребре: продление — пара радиусов, а 2 %
        // ребра длиннее — «blend range beyond the extension»). Мутация P3D_DRMARGINCLAMP=0.
        // запас — не больше половины пути до продления: вторая половина — узлы таблицы за используемым промежутком (у самого
        // края таблицы вставка отходит от формулы на 1e-7 мм)
        if (MarginClamp) { tLo -= Math.Min(0.02 * Delta, Math.Max(0, 0.5 * (tLo - outT[0]))); tHi += Math.Min(0.02 * Delta, Math.Max(0, 0.5 * (outT[1] - tHi))); }
        else { tLo -= 0.02 * Delta; tHi += 0.02 * Delta; }
        // поверхность — с запасом ещё 5 % за используемым промежутком: у краёв таблицы формулы вставка Катмулла–Рома опирается
        // на продолженный линейно узел и отходит от формулы на 1e-7 мм; сверка таблицы — на используемом промежутке
        double useLo = tLo, useHi = tHi;
        tLo = Math.Max(outT[0], tLo - 0.05 * Delta); tHi = Math.Min(outT[1], tHi + 0.05 * Delta);
        if (!(tLo <= useLo && tHi >= useHi)) { why = "open curved: blend range beyond the extension"; return false; }
        Phase("сверки");
        // СВЕРКИ по пробам на всём продлённом промежутке: хребет, касание на радиусе, ход вперёд, дуга меньше полуокружности
        const int N = 256;
        var us = new List<double>(); var secs = new List<SweepSection>(); var Ps = new List<Vector3d>(); var QAs = new List<Vector3d>(); var QBs = new List<Vector3d>();
        Vector3d? prevS = null, prevA = null, prevB = null; Vector3d prevT = default; double widthA = 0, widthB = 0;
        // хребет, точки фаски и сечения проб — заранее, параллельно (решения — ниже, по порядку)
        var preX = new Vector3d?[N + 1]; var preG = new SweepSection?[N + 1];
        For(N + 1, i =>
        {
            double t = tLo + (tHi - tLo) * i / N;
            if (!ch && !(Rt(t) > 1e-6)) return;
            var xx = ch ? Ed(t) : Spine(t);
            preX[i] = xx;
            if (xx == null) return;
            preG[i] = Gen(t);
        });
        for (int i = 0; i <= N; i++)
        {
            double t = tLo + (tHi - tLo) * i / N;
            if (!ch && !(Rt(t) > 1e-6)) { why = "variable radius: radius not positive along the blend (extension)"; return false; }
            var x = preX[i];
            if (x == null) { why = "open curved: ball centre not found (radius too large?)"; return false; }
            var P = Ed(t); Ps.Add(P);
            var qa = ch ? ChA(t) : SideFoot(true, t, x.Value); var qb = ch ? ChB(t) : SideFoot(false, t, x.Value);
            if (ch)
            {
                if (double.IsNaN(qa.X + qb.X) || Math.Abs((qa - P).Length - dA) > 1e-8 * Math.Max(1, dA) || Math.Abs((qb - P).Length - dB) > 1e-8 * Math.Max(1, dB) || (qa - qb).Length < 1e-6)
                { why = "open chamfer: chamfer point not found"; return false; }
            }
            else if (Math.Abs((qa - x.Value).Length - Rt(t)) > 1e-8 * Math.Max(1, r) || Math.Abs((qb - x.Value).Length - Rt(t)) > 1e-8 * Math.Max(1, r)) { why = "open curved: contact not at the ball radius"; return false; }
            var g = preG[i] ?? Gen(t);
            if (!ch && !(g.Phi > 1e-3 && g.Phi < Math.PI - 1e-3)) { why = "open curved: blend arc degenerate"; return false; }
            if (t >= 0 && t <= Delta) { widthA = Math.Max(widthA, (qa - P).Length); widthB = Math.Max(widthB, (qb - P).Length); }
            if (prevS != null && (Vector3d.Dot(x.Value - prevS.Value, prevT) <= 0 || Vector3d.Dot(qa - prevA!.Value, prevT) <= 0 || Vector3d.Dot(qb - prevB!.Value, prevT) <= 0))
            { why = "open curved: spine or contact line folds back"; return false; }
            prevS = x; prevA = qa; prevB = qb; prevT = Td(t);
            us.Add(t); secs.Add(g); QAs.Add(qa); QBs.Add(qb);
        }
        Phase("зазор");
        // зазор: прочие звенья граней A и B дальше полосы
        var keepA = new HashSet<TEdge>(chain) { ends[0].EA, ends[1].EA }; var keepB = new HashSet<TEdge>(chain) { ends[0].EB, ends[1].EB };
        for (int k = 0; k < 2; k++) if (overDel[k] != null) { keepA.Add(overDel[k]!); keepB.Add(overDel[k]!); if (mEdge[k] != null) { keepA.Add(mEdge[k]!); keepB.Add(mEdge[k]!); } }
        // k144: точка другого ребра — в полосе, если она у отрезка «ребро → линия касания» какого-либо сечения внутри
        // используемого промежутка линии касания (допуск — шаг сечений и 2 % ширины)
        bool InStrip(Vector3d q, List<Vector3d> Qs, double ta, double tb)
        {
            double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
            for (int i = 0; i < us.Count; i++)
            {
                if (us[i] < lo || us[i] > hi) continue;
                var a = Ps[i]; var b = Qs[i]; var ab = b - a; double L2 = ab.LengthSquared;
                double f = L2 > 0 ? Math.Clamp(Vector3d.Dot(q - a, ab) / L2, 0, 1) : 0;
                double step = Math.Max(i > 0 ? (Ps[i] - Ps[i - 1]).Length : 0, i + 1 < Ps.Count ? (Ps[i + 1] - Ps[i]).Length : 0);
                step = Math.Max(step, Math.Max(i > 0 ? (Qs[i] - Qs[i - 1]).Length : 0, i + 1 < Qs.Count ? (Qs[i + 1] - Qs[i]).Length : 0));
                if ((a + ab * f - q).Length <= step + 0.02 * Math.Sqrt(L2) + 1e-6) return true;
            }
            return false;
        }
        foreach (var (F, keep, w) in new[] { (FA, keepA, widthA), (FB, keepB, widthB) })
            foreach (var sg in F.Loops.SelectMany(l => l))
            {
                if (sg.Id != null && keep.Contains(sg.Id) || sg.JointPiece) continue;
                for (int k = 0; k <= 32; k++)
                {
                    var q = sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * k / 32.0);
                    if (StripTight ? InStrip(q, F == FA ? QAs : QBs, F == FA ? tA0 : tB0, F == FA ? tA1 : tB1) : Ps.Min(p => (p - q).Length) <= 1.5 * w + 1e-6)
                    {
                      if (EndOver && sg.Id != null && (k == 0 || k == 32))
                        for (int ke = 0; ke < 2; ke++)
                        {
                            if (joint[ke] != 0 || two[ke]) continue;
                            var eS = F == FA ? ends[ke].EA : ends[ke].EB; var cS = eS.Curve;
                            var pa = cS.PointAt(cS.T0); var pb = cS.PointAt(cS.T1); var far = (pa - ends[ke].V).Length < 1e-7 ? pb : pa;
                            if ((far - q).Length > 1e-7 || (_overHints?.Any(h => (h.V - ends[ke].V).Length < 1e-7) ?? false)) continue;
                            (_overHints ??= new()).Add((ends[ke].V, F == FA, sg.Id, q));
                            Interlocked.Increment(ref EndOvers);
                            Debug?.Invoke($"open: strip of {F.Tag} passes the far end {q:0.###} of the end edge {ke} — retry with overflow onto the next face");
                            try { return OpenBlend(body, chain, byFace, blends, r, ref why, chDist, varLaw, jctx); }
                            finally { _overHints!.RemoveAt(_overHints.Count - 1); }
                        }
                      if (Debug != null) { int im = 0; for (int ii = 1; ii < Ps.Count; ii++) if ((Ps[ii] - q).Length < (Ps[im] - q).Length) im = ii; string EndInfo(int kk) { var ea = F == FA ? ends[kk].EA : ends[kk].EB; return ea == null ? "-" : $"{(ea.Curve.PointAt(ea.Curve.T0)):0.###}->{(ea.Curve.PointAt(ea.Curve.T1)):0.###} V {ends[kk].V:0.###}"; } Debug($"open: strip of {F.Tag} (width {w:G4}) meets {sg.Curve.GetType().Name} {sg.A:0.###}->{sg.B:0.###} at {q:0.###} (k {k}/32), distance {Ps.Min(p => (p - q).Length):G4}; nearest sample t {us[im]:G6} (edge t 0…{Delta:G6}, use {(F == FA ? tA0 : tB0):G6}…{(F == FA ? tA1 : tB1):G6}), contact there {(F == FA ? QAs : QBs)[im]:0.###}; end edges on this face: 0: {EndInfo(0)}; 1: {EndInfo(1)}; end faces {ends[0].FE.Tag} {ends[0].FE.Surf.GetType().Name} / {ends[1].FE.Tag} {ends[1].FE.Surf.GetType().Name}; the met edge between {sg.Id?.FaceA?.Surface?.GetType().Name} and {sg.Id?.FaceB?.Surface?.GetType().Name} (faces {sg.Id?.FaceA?.GetHashCode()}/{sg.Id?.FaceB?.GetHashCode()}, end faces {ends[0].FE.Orig?.GetHashCode()}/{ends[1].FE.Orig?.GetHashCode()}, A {FA.Orig?.GetHashCode()} B {FB.Orig?.GetHashCode()})"); } why = "open curved: another edge of the face in the blend strip"; return false; }
                }
            }

        double Lp = 0; for (int i = 1; i < Ps.Count; i++) Lp += (Ps[i] - Ps[i - 1]).Length;
        double kU = Lp / (tHi - tLo);
        // стыки формулы: узлы закона радиуса (закон там гладок лишь первого порядка — таблица не вставляет через них)
        var breaks = new List<double>();
        if (LawBreaks) foreach (var kt in knotT) if (kt > tLo && kt < tHi) breaks.Add((kt - tLo) * kU);
        // k152: СТЫК У КРАЯ ОБЛАСТИ ГРАНИ NURBS. За краем область грани продолжена касательно (Ssi, P3D_SSITANEXT) — поверхность
        // там гладка лишь первого порядка, и формула ленты — тоже: таблица пятой степени через край не вставляет (s102: 4.6e-3 мм
        // у торца, куда край боковины выходит наискось). Параметр, где точка касания переходит край области, — стык таблицы.
        // Мутация P3D_DRTANBREAK=0.
        if (TanBreak && (Geometry.Ssi.TangentExt || !TanRetry))
            foreach (var (F, side) in new[] { (FA, 0.0), (FB, 1.0) })
            {
                if (F.Surf is not NurbsSurface) continue;
                double Out(double t)
                {
                    var q = Gen(t).Point(side); if (!double.IsFinite(q.X)) return double.NaN;
                    Geometry.Ssi.ParamFoot(F.Surf, q, out var uv); if (!double.IsFinite(uv.X)) return double.NaN;
                    return Math.Max(Math.Max(-uv.X, uv.X - 1), Math.Max(-uv.Y, uv.Y - 1));   // > 0 — за краем
                }
                const int Ns = 200; double tp = tLo, opB = Out(tp);
                for (int i = 1; i <= Ns; i++)
                {
                    double tc = tLo + (tHi - tLo) * i / Ns, ocB = Out(tc);
                    if (double.IsFinite(opB) && double.IsFinite(ocB) && (opB > 0) != (ocB > 0))
                    {
                        double a = tp, b = tc, fa = opB;
                        for (int it = 0; it < 60; it++) { double m = 0.5 * (a + b), fm = Out(m); if (!double.IsFinite(fm)) break; if ((fm > 0) == (fa > 0)) { a = m; fa = fm; } else b = m; }
                        double tb = 0.5 * (a + b);
                        if (tb > tLo && tb < tHi) { breaks.Add((tb - tLo) * kU); Debug?.Invoke($"open: table break where the contact leaves the domain of {F.Tag} at t {tb:G8}"); }
                    }
                    tp = tc; opB = ocB;
                }
            }
        // Я2: СТЫК ФОРМУЛЫ, ГДЕ КАСАНИЕ ПЕРЕХОДИТ СТЫК ЛЕНТЫ ПРЕЖНЕЙ ПРАВКИ. Грань — лента прежней фаски или скругления
        // (процедурная SectionSurface) со своими стыками кусков (вершины цепочки, веер у угла) и концами промежутка (дальше —
        // продолжение второго порядка): там лента гладка лишь нулевого или первого порядка, и формула новой ленты — тоже.
        // Таблица пятой степени через такой излом не вставляет (s110 шаг 2: вторая разность формулы 3e-4 → 2e-6 мм при переходе
        // касания B через стык ленты прежней фаски, отход таблицы 2.2e-4 мм — отказ). Параметр перехода — стык таблицы.
        // Опыт: P3D_DRSECBREAK=1 (по умолчанию выключен).
        if (SecBreak)
            foreach (var (F, side) in new[] { (FA, 0.0), (FB, 1.0) })
            {
                if (F.Surf is not SectionSurface { IsProcedural: true } ssF) continue;
                var marks = ssF.Breaks.ToList();
                if (!ssF.Closed) { marks.Add(ssF.UMin); marks.Add(ssF.UMax); }
                if (marks.Count == 0) continue;
                Debug?.Invoke($"open: section face {F.Tag}: u [{ssF.UMin:G9}, {ssF.UMax:G9}] breaks [{string.Join(", ", ssF.Breaks.Select(b => b.ToString("G9")))}]");
                // по касанию ленты и по самому ребру: формула фаски зависит от грани между ребром и линией касания, и излом
                // приходит и там, где стык прежней ленты пересекает ребро
                foreach (bool atEdge in new[] { false, true })
                {
                double U(double t)
                {
                    var q = atEdge ? Ed(t) : Gen(t).Point(side); if (!double.IsFinite(q.X)) return double.NaN;
                    Geometry.Ssi.ParamFoot(ssF, q, out var uv); return uv.X;
                }
                const int Ns = 200; var uPrev = U(tLo); double tp = tLo;
                for (int i = 1; i <= Ns; i++)
                {
                    double tc = tLo + (tHi - tLo) * i / Ns, uc = U(tc);
                    if (double.IsFinite(uPrev) && double.IsFinite(uc))
                        foreach (double mk in marks)
                        {
                            if (!((uPrev - mk) * (uc - mk) < 0)) continue;
                            // касание может идти по самой складке (выпуклый стык прежней ленты): u = стык на промежутке t, и формула
                            // изломана на обоих его концах — вход и выход ищутся порознь (s110 шаг 2: вход 51.606, выход ~51.614)
                            double sgn = uPrev < mk ? -1 : 1, epsU = 1e-9 * Math.Max(1, Math.Abs(mk));
                            foreach (bool exit in new[] { false, true })
                            {
                                // вход — граница «ещё по старую сторону стыка», выход — «уже по новую»
                                bool Old(double t) { double d = U(t) - mk; return double.IsFinite(d) && d * sgn > epsU; }
                                bool New(double t) { double d = U(t) - mk; return double.IsFinite(d) && -d * sgn > epsU; }
                                double a = tp, b = tc;
                                for (int it = 0; it < 60; it++) { double m = 0.5 * (a + b); if (exit ? !New(m) : Old(m)) a = m; else b = m; }
                                double tb = 0.5 * (a + b);
                                if (exit && breaks.Count > 0 && Math.Abs(breaks[^1] - (tb - tLo) * kU) < 1e-6 * kU) continue;   // выход совпал со входом
                                if (tb > tLo && tb < tHi) { breaks.Add((tb - tLo) * kU); Interlocked.Increment(ref SecBreaks); Debug?.Invoke($"open: table break where the {(atEdge ? "edge" : "contact")} {(exit ? "leaves" : "reaches")} a joint of {F.Tag} (u {mk:G8}) at t {tb:G8}"); }
                            }
                        }
                    tp = tc; uPrev = uc;
                }
                }
            }
        breaks.Sort();
        // почти совпавшие стыки (касание и ребро переходят стык прежней ленты в одном месте) — один стык: кусок короче
        // 1e-7 мм таблица не вставляет
        if (SecBreak) for (int i = breaks.Count - 1; i > 0; i--) if (breaks[i] - breaks[i - 1] < 1e-7) breaks.RemoveAt(i);
        // шаг таблицы: у закона с узлами — вчетверо мельче (кривизна сечений по u у узла выше)
        double tabStep = Math.Max(Lp / 20000, Math.Min(0.25, 0.4 * Math.Min(widthA, widthB)) / 32) / (knotT.Count > 0 ? 4 : 1);
        if (FastPreviewNow) tabStep *= 4;   // 21.10: быстрый предпросмотр — таблица реже (отход ~1e-6 мм, на экране не видно)
        Phase("таблица");
        SectionSurface MakeSurf() => new SectionSurface(U => Gen(tLo + U / kU), us.Select(t => (t - tLo) * kU).ToList(), secs, false, 0, Lp, breaks, tabStep) { ParallelTable = true };
        var surf = MakeSurf();
        for (int refine = 0; ; refine++)
        {
            double worstTab = 0, worstU = 0;
            int nChk = FastPreviewNow ? 100 : 1000;
            var devs = new double[1000];
            surf.SectionAt(((useLo - tLo) + (useHi - useLo) * 0.5) * kU);   // таблица — до проб (строится один раз)
            For(FastPreviewNow ? 100 : 1000, i =>
            {
                double U = ((useLo - tLo) + (useHi - useLo) * (i + 0.5) / nChk) * kU;
                var ta = surf.SectionAt(U); var ex = surf.ExactSectionAt(U);
                double dm = 0;
                foreach (double v in new[] { 0.0, 0.5, 1.0 }) dm = Math.Max(dm, (ta.Point(v) - ex.Point(v)).Length);
                devs[i] = dm;
            });
            for (int i = 0; i < nChk; i++) if (devs[i] > worstTab) { worstTab = devs[i]; worstU = ((useLo - tLo) + (useHi - useLo) * (i + 0.5) / nChk) * kU; }
            // k151: ФОРМУЛА НЕ ОПРЕДЕЛЕНА в пробе — отказ. Прежде NaN не попадал в худший отход (сравнение с NaN ложно), и таблица
            // в окрестности такой пробы шла без проверки (корпус s110 шаг 2: точка фаски не находилась у излома грани прежней
            // фаски; отказ ловила лишь проверка тела — «кривая отходит от поверхности на 3.6e-3 мм»). Мутация P3D_DRTABNAN=0.
            int nanChk = devs.Take(nChk).Count(double.IsNaN);
            if (TabNan && nanChk > 0)
            {
                int iNan = Array.FindIndex(devs, double.IsNaN);
                why = $"open curved: blend formula undefined at {nanChk} of {nChk} probes (first at t {tLo + ((useLo - tLo) + (useHi - useLo) * (iNan + 0.5) / nChk):G6})"; return false;
            }
            Debug?.Invoke($"open: table vs formula {worstTab:E2} at t {tLo + worstU / kU:G6} (Δ {Delta:G6}) widths {widthA:G4}/{widthB:G4} Lp {Lp:G5}");
            if (Debug != null && worstTab > 1e-8)
            {
                // k151: разбор отхода — точки формулы около худшего места (края, середина) и вторые разности по u
                double h = tabStep;
                for (int j = -4; j <= 4; j++)
                {
                    double U = worstU + j * h / 2;
                    var e0 = surf.ExactSectionAt(U - h); var e1 = surf.ExactSectionAt(U); var e2 = surf.ExactSectionAt(U + h); var ta = surf.SectionAt(U);
                    string s2 = string.Join(" ", new[] { 0.0, 0.5, 1.0 }.Select(v => $"{(e0.Point(v) - 2 * e1.Point(v) + e2.Point(v)).Length:E1}/{(ta.Point(v) - e1.Point(v)).Length:E1}"));
                    Debug($"open: table probe t {tLo + U / kU:G8} A {e1.Point(0):0.####} B {e1.Point(1):0.####} d2/dev(v=0,.5,1) {s2}; A {FA.Tag} {FA.Surf.GetType().Name} uv {FA.Surf.ProjectToUV(e1.Point(0)):0.#####}, B {FB.Tag} {FB.Surf.GetType().Name} uv {FB.Surf.ProjectToUV(e1.Point(1)):0.#####}");
                }
            }
            // таблица — представление формулы: её отход — в пятой доле точности модели (1e-7 → 2e-8 мм)
            if (worstTab > (FastPreviewNow ? 1e-4 : Math.Max(1e-8, 0.2 * ToleranceContext.Current.ModelAccuracy)))
            {
                // Я2: ТАБЛИЦА МЕЛЬЧЕ, ЕСЛИ ОТХОД ВЕЛИК. Ошибка вставки пятой степени ~h⁶: шаг вчетверо мельче — отход в ~4000 раз
                // меньше, если формула гладкая (излом формулы так не лечится — на то стыки таблицы). До двух раз. Мутация
                // P3D_DRTABREFINE=0.
                if (TabRefine && !FastPreviewNow && refine < 2 && double.IsFinite(worstTab))
                {
                    Debug?.Invoke($"open: table refined ×4 (deviation {worstTab:E1} mm, step {tabStep:E2})");
                    tabStep /= 4; surf = MakeSurf(); Interlocked.Increment(ref TabRefines); continue;
                }
                why = $"open curved: blend surface table deviates from the formula ({worstTab:E1} mm)"; return false;
            }
            break;
        }

        Phase("кривые");
        seedSurf = surf; seedLo = tLo; seedKU = kU;
        // КРИВЫЕ: линии касания, края ленты на торцах
        // 17:31: ЛИНИИ КАСАНИЯ И КРАЯ ЛЕНТЫ — ВСТАВКОЙ ЭРМИТА по точным точкам (отход ≤ 1e-9 мм, сверено в серединах всех
        // промежутков): точная точка — корень (хребет, касание, край на торце, 0.05–0.2 мс), а разметка, p-кривые, объём и
        // проверки спрашивают их тысячи раз (скругление ребра цилиндр ∪ шар: 30 тыс. сечений, 1.5 с). Мутация P3D_DRFITCURVES=0.
        Func<double, Vector3d> fQA = QA, fQB = QB, fE0 = v => EndPt(0, v), fE1 = v => EndPt(1, v);
        if (FitDbg) Console.Error.WriteLine($"FITPROC start");
        if (FitCurves)
        {
            // 21.10: быстрый предпросмотр операции — кривые ленты с отходом 1e-6 мм (на экране не видно; точные — при подтверждении)
            double fitTol = FastPreviewNow ? 1e-6 : FitTol;
            // четыре кривые — одновременно (P3D_DRFITPAR=0 — по очереди)
            Func<double, Vector3d>? rA = null, rB = null, r0 = null, r1 = null; var e0f = fE0; var e1f = fE1;
            if (!FitParOff && Environment.ProcessorCount > 1 && Tess.Tessellator.Threads > 1)
                System.Threading.Tasks.Parallel.Invoke(() => rA = FitProc(QA, tA0, tA1, fitTol), () => rB = FitProc(QB, tB0, tB1, fitTol),
                                                       () => r0 = FitProc(e0f, 0, 1, fitTol), () => r1 = FitProc(e1f, 0, 1, fitTol));
            else { rA = FitProc(QA, tA0, tA1, fitTol); rB = FitProc(QB, tB0, tB1, fitTol); r0 = FitProc(e0f, 0, 1, fitTol); r1 = FitProc(e1f, 0, 1, fitTol); }
            fQA = rA ?? fQA; fQB = rB ?? fQB; fE0 = r0 ?? fE0; fE1 = r1 ?? fE1;
        }
        var cA = new ProcCurve3(fQA, tA0, tA1, false, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch || varLaw != null ? null : (s, a, b) => NoEvents(true, s, a, b) };
        var cB = new ProcCurve3(fQB, tB0, tB1, false, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch || varLaw != null ? null : (s, a, b) => NoEvents(false, s, a, b) };
        var end0 = new ProcCurve3(fE0, 0, 1, false, ch ? "chamfer end" : "rolling-ball end");
        var end1 = new ProcCurve3(fE1, 0, 1, false, ch ? "chamfer end" : "rolling-ball end");
        if (FitDbg) Console.Error.WriteLine($"FITPROC curves made");
        Phase("концы и сборка");
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
        // k147: переход боковой точки фаски — линия касания стороны T из кусков: средний (на T) и у конца k с переходом —
        // после q (на соседней грани E_k)
        var swPart = new ICurve3?[2]; var swParts = new List<(ICurve3 C, Vector3d A, Vector3d B)>?[2];
        foreach (bool oA in new[] { true, false })
        {
            bool s0 = !double.IsNaN(swT[0]) && swOnA[0] == oA, s1 = !double.IsNaN(swT[1]) && swOnA[1] == oA;
            if (!s0 && !s1) continue;
            double t0 = oA ? tA0 : tB0, t1 = oA ? tA1 : tB1;
            double lo = s0 ? swT[0] : t0, hi = s1 ? swT[1] : t1;
            if (!(t0 < t1 && lo >= t0 && hi <= t1 && lo < hi)) { why = "open curved: joint — seam crossing outside the contact line"; return false; }
            Func<double, Vector3d> fT = oA ? QA : QB;
            var main = new ProcCurve3(fT, lo, hi, false, "chamfer contact");
            string nm = ch ? "chamfer contact" : "round contact";
            // куски по возрастанию t; у скругления с изломом шва — два: на шве и на E (k148)
            foreach (var (kk, on) in new[] { (0, s0), (1, s1) })
            {
                if (!on) continue;
                double ta = kk == 0 ? t0 : hi, tb = kk == 0 ? lo : t1, tm2 = swT2[kk];
                var lst = swParts[kk] = new();
                if (crOn[kk])
                {
                    // k148f: за шаром трёх граней — линия касания на E (шар на S и E), от его точки на E
                    int kc = kk; var shEc = crShE[kc]!; double sEc = crSE[kc];
                    Vector3d FE(double t) { var x = oA ? SpineWith(t, shEc, sEc, shB, sB) : SpineWith(t, shA, sA, shEc, sEc); return x == null ? new Vector3d(double.NaN) : Foot(shEc, x.Value); }
                    if (kk == 1) lst.Add((new ProcCurve3(t => t <= crT[kc] ? crQE[kc] : FE(t), ta, tb, false, nm), crQE[kc], fT(tb)));
                    else lst.Add((new ProcCurve3(t => t >= crT[kc] ? crQE[kc] : FE(t), ta, tb, false, nm), fT(ta), crQE[kc]));
                    swPart[kk] = lst[0].C;
                    continue;
                }
                if (!double.IsNaN(tm2))
                {
                    if (!(tm2 > ta && tm2 < tb)) { why = "open curved: round joint — the seam piece outside the contact line"; return false; }
                    lst.Add((new ProcCurve3(fT, ta, tm2, false, nm), fT(ta), swQ2[kk]));
                    lst.Add((new ProcCurve3(fT, tm2, tb, false, nm), swQ2[kk], fT(tb)));
                }
                else lst.Add((new ProcCurve3(fT, ta, tb, false, nm), fT(ta), fT(tb)));
                swPart[kk] = lst[0].C;
            }
            if (oA) bA = main; else bB = main;
        }
        bool anyJoint = joint[0] != 0 || joint[1] != 0 || two[0] || two[1];
        if (OpenCylOn && !anyJoint && !ch && varLaw == null && knotT.Count == 0 && chain.Count == 1 && chain[0].Curve is LineCurve3
            && shA is Geometry.Ssi.Plane && shB is Geometry.Ssi.Plane && ends[0].ShE is Geometry.Ssi.Plane pe0 && ends[1].ShE is Geometry.Ssi.Plane pe1
            && OpenCylinder(Spine, Gen, QA, QB, EndPt, r, tLo, tHi, tA0, tA1, tB0, tB1, pe0, pe1) is { } oc)
        {
            bSurf = oc.S; bA = oc.A; bB = oc.B; bE0 = oc.E0; bE1 = oc.E1;
            Debug?.Invoke("open: blend is an exact cylinder");
        }
        // u12: ФАСКА ПРЯМОГО РЕБРА МЕЖДУ ПЛОСКОСТЯМИ, концы на плоских торцах — лента точно плоскость, контакты и края на
        // торцах — отрезки (прежде прямые рёбра в незамкнутую фаску не принимались, и фаска шла резцом). Мутация P3D_OPENCYL=0.
        else if (OpenCylOn && !anyJoint && ch && varLaw == null && chain.Count == 1 && chain[0].Curve is LineCurve3
            && shA is Geometry.Ssi.Plane && shB is Geometry.Ssi.Plane && ends[0].ShE is Geometry.Ssi.Plane && ends[1].ShE is Geometry.Ssi.Plane
            && OpenChamferPlane(QA, QB, EndPt, tA0, tA1, tB0, tB1) is { } op)
        {
            bSurf = op.S; bA = op.A; bB = op.B; bE0 = op.E0; bE1 = op.E1;
            Debug?.Invoke("open: chamfer is an exact plane");
        }

        // УКОРОЧЕНИЕ РЁБЕР A∩E и B∩E до точек касания (одна кривая на обе грани ребра). Ребро может укорачиваться с обоих концов
        // (торец — общий у обоих концов, ребро B∩E идёт от одного конца до другого)
        // k145: СТЫК — край ленты есть дуга сечения (точно, окружность), общая с лентой соседнего звена цепочки: первая лента
        // заводит дугу, вторая берёт ту же (концы сверяются)
        for (int k = 0; k < 2; k++)
        {
            if (joint[k] == 0) continue;
            var g = Gen(tJ[k]); var qa = k == 0 ? qa0 : qa1; var qb = k == 0 ? qb0 : qb1;
            ICurve3 arc = ch ? new LineCurve3(g.Point(0), g.Point(1)) : new ArcCurve3(new Frame3(g.C, g.E1, g.E2), g.R, 0, g.Phi);
            if ((arc.PointAt(0) - qa).Length > 1e-8 * Math.Max(1, r) || (arc.PointAt(1) - qb).Length > 1e-8 * Math.Max(1, r)) { why = "open curved: joint arc does not meet the contact lines"; return false; }
            var V = ends[k].V;
            var prevArc = jctx!.Arcs.FirstOrDefault(a => (a.V - V).Length < 1e-7);
            if (prevArc.Arc != null)
            {
                bool same = (prevArc.P0 - qa).Length < 1e-7 && (prevArc.P1 - qb).Length < 1e-7 || (prevArc.P0 - qb).Length < 1e-7 && (prevArc.P1 - qa).Length < 1e-7;
                double dm = (prevArc.Arc.PointAt(0.5) - arc.PointAt(0.5)).Length;
                if (!same || dm > 1e-7) Debug?.Invoke($"open: joint mismatch at {V:0.###}: centres {(prevArc.C - jC[k]).Length:E2}, this centre vs other side {Geometry.Ssi.Implicit(prevArc.ShT, jC[k]).F - prevArc.OffT:E3} (off {prevArc.OffT:G4}), other centre vs this side {Geometry.Ssi.Implicit(jShT[k]!, prevArc.C).F - jOffT[k]:E3} (off {jOffT[k]:G4}); contacts {(prevArc.P1 - qb).Length:E2}/{(prevArc.P0 - qa).Length:E2}");
                if ((!same || dm > 1e-7) && Debug != null) { var tdj = Td(tJ[k]).Normalized(); var d0 = prevArc.P0 - qa; var d1 = prevArc.P1 - qb; if ((prevArc.P0 - qb).Length < (prevArc.P0 - qa).Length) { d0 = prevArc.P0 - qb; d1 = prevArc.P1 - qa; } Debug($"open: joint mismatch along the edge {Vector3d.Dot(d0, tdj):E2}/{Vector3d.Dot(d1, tdj):E2}, across {(d0 - Vector3d.Dot(d0, tdj) * tdj).Length:E2}/{(d1 - Vector3d.Dot(d1, tdj) * tdj).Length:E2}, tJ {tJ[k]:G10}"); }
                if (!same || dm > 1e-7) { why = $"open curved: joint sections of the two chain edges differ ({Math.Max(dm, Math.Min((prevArc.P0 - qa).Length, (prevArc.P1 - qa).Length)):E1} mm)"; return false; }
                arc = prevArc.Arc;
            }
            else jctx.Arcs.Add((V, arc, qa, qb, jC[k], jShT[k]!, jOffT[k]));
            if (k == 0) bE0 = arc; else bE1 = arc;
        }
        // Я2: двухгранный торец — точка, где ребро m протыкает ленту (край переходит с E1 на E2)
        var pStar = new Vector3d[2]; var vStar = new double[2];
        ICurve3?[] bEa = new ICurve3?[2], bEb = new ICurve3?[2];
        for (int k = 0; k < 2; k++)
        {
            if (!two[k]) continue;
            // переход шва на другом конце годен (его куски — на своей стороне ленты); на этом — нет
            if (swPart[k] != null || crOn[k] || joint[k] != 0 || (TwoSwitchOff && (swPart[1 - k] != null || crOn[1 - k]))) { why = "open curved: two-face end with a seam switch"; return false; }
            double H(double v) { var x = EndPt(k, v); return ends[k].SE * Geometry.Ssi.Implicit(ends[k].ShE, x).F - se2[k] * Geometry.Ssi.Implicit(sh2[k]!, x).F; }
            double h0 = H(0), h1 = H(1);
            // край на стороне A — на E1, на стороне B — на E2: у выпуклого излома f1 = 0 ≥ f2 у A (H ≥ 0), у вогнутого — наоборот
            if (!double.IsFinite(h0 + h1) || !(h0 * h1 < 0) || (h0 > 0) != conv2[k]) { why = $"open curved: two-face end — the end curve does not pass from E1 to E2 ({h0:E1}/{h1:E1})"; return false; }
            double va = 0, vb = 1, ha = h0;
            for (int it = 0; it < 64 && vb - va > 1e-15; it++) { double vm = 0.5 * (va + vb), hm = H(vm); if (!double.IsFinite(hm)) { why = "open curved: two-face end — end curve undefined"; return false; } if ((hm > 0) == (ha > 0)) { va = vm; ha = hm; } else vb = vm; }
            vStar[k] = 0.5 * (va + vb); pStar[k] = EndPt(k, vStar[k]);
            var mc = mEdge[k]!.Curve; double dm = (mc.PointAt(CurveParam.Closest(mc, pStar[k])) - pStar[k]).Length;
            if (dm > 1e-7) { why = $"open curved: two-face end — the crease edge misses the blend ({dm:E1} mm)"; return false; }
            var bE = k == 0 ? bE0 : bE1;
            bEa[k] = Analytic.GeneralBoolean.SubCurve(bE, bE.T0, bE.T0 + (bE.T1 - bE.T0) * vStar[k]);
            bEb[k] = Analytic.GeneralBoolean.SubCurve(bE, bE.T0 + (bE.T1 - bE.T0) * vStar[k], bE.T1);
            if ((bEa[k]!.PointAt(bEa[k]!.T1) - pStar[k]).Length > 1e-7) { why = "open curved: two-face end — end curve split misses the crease point"; return false; }
            Interlocked.Increment(ref TwoEnds);
            Debug?.Invoke($"open: end {k}: crease point {pStar[k]:0.####} at v {vStar[k]:G6}");
        }
        var cuts = new Dictionary<TEdge, List<(Vector3d Old, Vector3d New)>>();
        for (int k = 0; k < 2; k++) if (two[k]) cuts[mEdge[k]!] = new() { (cornerPt[k], pStar[k]) };
        foreach (var (x, oldP, q, kk, isA) in new[] { (ends[0].EA, P0, qa0, 0, true), (ends[0].EB, P0, qb0, 0, false), (ends[1].EA, P1, qa1, 1, true), (ends[1].EB, P1, qb1, 1, false) })
        {
            // стык: ребро общей стороны — соседнее звено цепочки (его заменит своя лента), укорачивается только гладкое
            if (joint[kk] != 0 && (joint[kk] == 1) != isA) continue;
            if (!cuts.TryGetValue(x, out var l)) cuts[x] = l = new();
            if (noCut[kk] && (joint[kk] == 1) == isA) continue;     // k147: шов укоротит звено с переходом
            var oldEnd = overDel[kk] != null && x != (isA ? ends[kk].EB : ends[kk].EA) && (cornerPt[kk] - oldP).Length > 1e-7 && ((x.V0.P - cornerPt[kk]).Length < 1e-7 || (x.V1.P - cornerPt[kk]).Length < 1e-7) ? cornerPt[kk] : oldP;
            l.Add((oldEnd, swT[kk] is double tsw && !double.IsNaN(tsw) && (joint[kk] == 1) == isA ? (crOn[kk] ? crSeamPt[kk] : swQ[kk]) : q));
        }
        var trimmed = new Dictionary<TEdge, (ICurve3 C, List<(Vector3d Old, Vector3d New)> Moves)>();
        foreach (var (x, moves) in cuts)
        {
            ICurve3 cur = x.Curve;
            foreach (var (oldP, q) in moves)
            {
                var nc = Reshape(cur, oldP, q, out var rwhy, TolEnds && body.Guarantee != GuaranteeLevel.ExactAnalytic ? 1e-4 : 0);
                if (nc == null) { Debug?.Invoke($"open: reshape failed: edge {cur.GetType().Name} {cur.PointAt(cur.T0):0.###}→{cur.PointAt(cur.T1):0.###}, old end {oldP:0.###}, contact {q:0.###}"); why = "open curved: " + rwhy; return false; }
                cur = nc;
            }
            foreach (var (oldP, q) in moves)
                if ((cur.PointAt(cur.T0) - q).Length > 1e-7 && (cur.PointAt(cur.T1) - q).Length > 1e-7) { why = "open curved: trimmed end edge misses the contact point"; return false; }
            trimmed[x] = (cur, moves);
        }
        // вставка края ленты в торец: между звеньями A∩E и B∩E у прежней вершины
        foreach (var (k, endC, qa, qb) in new[] { (0, bE0, qa0, qb0), (1, bE1, qa1, qb1) })
        {
            if (joint[k] != 0) continue;
            var (FEk, _, eA, eB, V, _) = ends[k];
            bool done = false;
            if (two[k])
            {
                // E1: между A∩E1 и m — край от qa до точки излома; E2: между m и B∩E2 — от точки излома до qb
                var ps = pStar[k];
                // переполнение: съеденное ребро T ∩ E — из всех граней (его концы V и q уходят)
                if (overDel[k] is { } del)
                    foreach (var F in new[] { FA, FB, ends[k].FE, fe2[k]! }.Distinct())
                        foreach (var loop in F.Loops) loop.RemoveAll(sx => sx.Id == del);
                foreach (var (F, e1x, e2x, c, a, b) in new[] { (FEk, eA, mEdge[k]!, bEa[k]!, qa, ps), (fe2[k]!, mEdge[k]!, eB, bEb[k]!, ps, qb) })
                {
                    bool d2 = false;
                    foreach (var loop in F.Loops)
                    {
                        int n2 = loop.Count;
                        for (int i = 0; i < n2 && !d2; i++)
                        {
                            var s1 = loop[i]; var s2 = loop[(i + 1) % n2];
                            // у переполнения между e1x и e2x в грани торца было съеденное ребро — стык по самим рёбрам
                            if (overDel[k] == null && ((s1.B - V).Length > 1e-7 || (s2.A - V).Length > 1e-7)) continue;
                            if (overDel[k] != null && (s1.Id != e1x || s2.Id != e2x) && (s1.Id != e2x || s2.Id != e1x)) continue;
                            if (s1.Id == e1x && s2.Id == e2x) loop.Insert(i + 1, new Seg { Curve = c, A = a, B = b });
                            else if (s1.Id == e2x && s2.Id == e1x) loop.Insert(i + 1, new Seg { Curve = c, A = b, B = a });
                            else continue;
                            d2 = true;
                        }
                        if (d2) break;
                    }
                    if (!d2) { why = "open curved: two-face end — corner not found in an end face"; return false; }
                    F.Touched = true;
                }
                continue;
            }
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
        foreach (var F in new[] { FA, FB, ends[0].FE, ends[1].FE, fe2[0], fe2[1] }.OfType<FDesc>().Distinct())
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
        foreach (var (F, cv, a0x, a1x) in new[] { (FA, bA, qa0, qa1), (FB, bB, qb0, qb1) })
        {
            var a0 = a0x; var a1 = a1x;
            if (swPart[0] != null && (F == FA) == swOnA[0]) a0 = swQ[0];
            if (swPart[1] != null && (F == FA) == swOnA[1]) a1 = swQ[1];
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
        bl.Loops.Add(new List<Seg> { new() { Curve = bA, A = qa0, B = qa1 } });
        if (two[1]) { bl.Loops[0].Add(new() { Curve = bEa[1]!, A = qa1, B = pStar[1] }); bl.Loops[0].Add(new() { Curve = bEb[1]!, A = pStar[1], B = qb1 }); }
        else bl.Loops[0].Add(new() { Curve = bE1, A = qa1, B = qb1 });
        bl.Loops[0].Add(new() { Curve = bB, A = qb1, B = qb0 });
        if (two[0]) { bl.Loops[0].Add(new() { Curve = bEb[0]!, A = qb0, B = pStar[0] }); bl.Loops[0].Add(new() { Curve = bEa[0]!, A = pStar[0], B = qa0 }); }
        else bl.Loops[0].Add(new() { Curve = bE0, A = qb0, B = qa0 });
        foreach (bool oA in new[] { true, false })
        {
            // сторона T ленты: куски по порядку от конца 0 к концу 1 (у стороны B обход ленты — обратный)
            bool s0 = swPart[0] != null && swOnA[0] == oA, s1 = swPart[1] != null && swOnA[1] == oA;
            if (!s0 && !s1) continue;
            var lp = bl.Loops[0]; int iT = oA ? 0 : lp.FindIndex(x => ReferenceEquals(x.Curve, bB)); var mainC = lp[iT].Curve;   // Я2: у двухгранного конца 1 сторона B — не третья
            Vector3d e0 = oA ? qa0 : qb0, e1 = oA ? qa1 : qb1, m0 = s0 ? swQ[0] : e0, m1 = s1 ? swQ[1] : e1;
            var pieces = new List<Seg>();
            if (s0) foreach (var pc in swParts[0]!) pieces.Add(new Seg { Curve = pc.C, A = pc.A, B = pc.B });
            pieces.Add(new Seg { Curve = mainC, A = m0, B = m1 });
            if (s1) foreach (var pc in swParts[1]!) pieces.Add(new Seg { Curve = pc.C, A = pc.A, B = pc.B });
            if (s0) { pieces[0].A = e0; pieces[swParts[0]!.Count - 1].B = m0; }
            if (s1) { pieces[^1].B = e1; pieces[^swParts[1]!.Count].A = m1; }
            if (!oA) { pieces.Reverse(); pieces = pieces.Select(x => new Seg { Curve = x.Curve, A = x.B, B = x.A }).ToList(); }
            lp.RemoveAt(iT); lp.InsertRange(iT, pieces);
        }
        for (int k = 0; k < 2; k++)
        {
            if (swPart[k] == null || crOn[k]) continue;
            // соседняя грань E: кусок после q — рядом с укороченным швом (конец шва — q)
            bool oA = swOnA[k]; var q = swQ[k]; var endP = k == 0 ? (oA ? qa0 : qb0) : (oA ? qa1 : qb1);
            var FE = ends[k].FE; var seamE = oA ? ends[k].EA : ends[k].EB; bool done = false;
            foreach (var loop in FE.Loops)
            {
                int n = loop.Count;
                for (int i = 0; i < n && !done; i++)
                {
                    if (loop[i].Id != seamE) continue;
                    // куски от q к концу (у конца 0 — по убыванию t)
                    var fromQ = (k == 0 ? Enumerable.Reverse(swParts[k]!) : swParts[k]!).Select(pc => k == 0 ? (pc.C, A: pc.B, B: pc.A) : (pc.C, pc.A, pc.B)).ToList();
                    fromQ[0] = (fromQ[0].C, q, fromQ[0].B); fromQ[^1] = (fromQ[^1].C, fromQ[^1].A, endP);
                    if ((loop[i].B - q).Length < 1e-7) { loop.InsertRange(i + 1, fromQ.Select(pc => new Seg { Curve = pc.C, A = pc.A, B = pc.B, JointPiece = true })); done = true; }
                    else if ((loop[i].A - q).Length < 1e-7) { loop.InsertRange(i, Enumerable.Reverse(fromQ).Select(pc => new Seg { Curve = pc.C, A = pc.B, B = pc.A, JointPiece = true })); done = true; }
                }
                if (done) break;
            }
            if (!done) { why = "open curved: chamfer joint — the seam end not found in the next face"; return false; }
            FE.Touched = true;
        }
        string? cwhy = null;
        if (crOn[0] || crOn[1])
        {
            if (!CreaseAssemble()) { why = cwhy; return false; }
        }
        else blends.Add(bl);
        // k148g: ГЕОМЕТРИЯ СКОСА У ВОГНУТОГО ИЗЛОМА (см. CreaseBevelOn). Трубки лент — шары радиуса r по хребтам режимов T и E
        // (по параметру ребра звена); расстояние до трубки — до ближайшей точки её хребта (огибающая постоянного радиуса).
        CreaseBevel? CreaseBevelGeom(int k, double tS, double dk, double win, Vector3d c, ICurve3 sc, Geometry.Ssi.Shape shT,
            Geometry.Ssi.Shape shE, Geometry.Ssi.Shape shS, Func<double, Vector3d?> spT, Func<double, Vector3d?> spE)
        {
            double hh = 1e-6 * Math.Max(Delta, 1e-9), rr = Math.Max(1, r);
            double tMin = tS - 3 * win, tMax = tS + 3 * win;
            // ТАБЛИЦА ХРЕБТА: точка хребта — Ньютон с проекциями на грани (дорого), а кривые митры и обрыва вычисляются в тысячах
            // точек (сетка, проверки). Хребет на окне вокруг t* — по таблице: Эрмит с производными четвёртого порядка
            // (погрешность ~1e-10 мм при шаге ~0.01 мм); вне окна — точно.
            Func<double, Vector3d?> Tab(Func<double, Vector3d?> sp, double a, double b, int n)
            {
                double h = (b - a) / n; var P = new Vector3d[n + 5]; bool ok = true;
                for (int i = 0; i < n + 5 && ok; i++) { var x = sp(a + (i - 2) * h); if (x == null) ok = false; else P[i] = x.Value; }
                if (!ok) return sp;
                var D = new Vector3d[n + 1];
                for (int i = 0; i <= n; i++) { int j = i + 2; D[i] = (P[j - 2] - P[j - 1] * 8 + P[j + 1] * 8 - P[j + 2]) / (12 * h); }
                return t =>
                {
                    if (!(t >= a && t <= b)) return sp(t);
                    double x = (t - a) / h; int i = Math.Clamp((int)Math.Floor(x), 0, n - 1); double u = x - i;
                    double h00 = 2 * u * u * u - 3 * u * u + 1, h10 = u * u * u - 2 * u * u + u, h01 = -2 * u * u * u + 3 * u * u, h11 = u * u * u - u * u;
                    return P[i + 2] * h00 + D[i] * (h10 * h) + P[i + 3] * h01 + D[i + 1] * (h11 * h);
                };
            }
            {
                double tEndV0 = dk > 0 ? Delta : 0, span0 = Math.Min(2 * win, 8 * Math.Abs(tEndV0 - tS) + 0.05 * win);
                double half = 1.5 * span0 + 4 * r / Math.Max(1e-9, ((Ed(tS + hh) - Ed(tS - hh)).Length / (2 * hh)));
                int n = Math.Clamp((int)Math.Ceiling(2 * half / Math.Max(1e-4, 0.01 * Math.Min(1, r))), 256, 8192);
                spT = Tab(spT, tS - half, tS + half, n); spE = Tab(spE, tS - half, tS + half, n);
            }
            Vector3d? D1(Func<double, Vector3d?> sp, double t) { var a = sp(t - hh); var b = sp(t + hh); return a == null || b == null ? null : (b.Value - a.Value) / (2 * hh); }
            (double D, Vector3d G, double T)? Tube(Func<double, Vector3d?> sp, Vector3d y)
            {
                double H(double t) { var cm = sp(t); var d1 = D1(sp, t); return cm == null || d1 == null ? double.NaN : Vector3d.Dot(y - cm.Value, d1.Value); }
                double t = tS;
                for (int it = 0; it < 40; it++)
                {
                    double h = H(t), hs = 1e-5 * Math.Max(Delta, 1e-9), dh = (H(t + hs) - H(t - hs)) / (2 * hs);
                    if (!double.IsFinite(h) || !double.IsFinite(dh) || dh == 0) return null;
                    double tn = Math.Clamp(t - h / dh, tMin, tMax);
                    bool fin = Math.Abs(tn - t) < 1e-13 * Math.Max(1, Delta); t = tn;
                    if (fin) break;
                }
                var cc = sp(t); var dd = D1(sp, t);
                // невязка — шум разностной производной хребта (1e-8 отн.); ошибка расстояния от неё — второго порядка
                if (cc == null || dd == null || Math.Abs(Vector3d.Dot(y - cc.Value, dd.Value.Normalized())) > 1e-6 * rr) return null;
                var w = y - cc.Value; double L = w.Length;
                return L > 0 ? (L, w / L, t) : null;
            }
            var edS = Ed(tS);
            double SideOn(Vector3d Q, Geometry.Ssi.Shape sh, double t)
            {
                double u = CurveParam.Closest(sc, Q); var sg = sc.PointAt(u);
                var w = Vector3d.Cross(Geometry.Ssi.Implicit(sh, sg).G, sc.TangentAt(u)).Normalized();
                // ориентация — по точке ребра в t* (за вершиной продолжение ребра уходит за шов, и знак по Ed(t) переворачивался:
                // ложный корень у s110, третий угол)
                if (Vector3d.Dot(edS - sg, w) < 0) w = -w;
                return Vector3d.Dot(Q - sg, w);
            }
            Vector3d? Rail(Func<double, Vector3d?> sp, Geometry.Ssi.Shape sh, double t) { var x = sp(t); return x == null ? null : Foot(sh, x.Value); }
            double Cross0(Func<double, double> g, double a, double b)
            {
                double g0 = g(a); if (!double.IsFinite(g0) || g0 == 0) return double.NaN;
                double prev = g0;
                for (int i = 1; i <= 256; i++)
                {
                    double ta = a + (b - a) * (i - 1) / 256.0, tb = a + (b - a) * i / 256.0, gv = g(tb);
                    if (!double.IsFinite(gv)) return double.NaN;
                    if (Math.Sign(gv) != Math.Sign(prev)) return prev < 0 ? RootIn(g, ta, tb) : RootIn(x => -g(x), ta, tb);
                    prev = gv;
                }
                return double.NaN;
            }
            var qS = Foot(shS, c); var qT = Foot(shT, c); var qE = Foot(shE, c);
            // для разбора: положение сечений относительно излома хребта (скос по продолженным лентам — по определению скругления
            // как объединения снимаемых объёмов рёбер, а не «открытия» тела шарами: у угла коробки с острым третьим ребром — то же)
            var d1 = D1(spT, tS); var d2 = D1(spE, tS);
            if (d1 == null || d2 == null) { Debug?.Invoke("open: crease bevel — spine derivative undefined"); return null; }
            var dd12 = d2.Value.Normalized() * dk - d1.Value.Normalized() * dk;
            double mT = Vector3d.Dot((qT - c) / r, dd12), mE = Vector3d.Dot((qE - c) / r, dd12);
            // окно поиска — у вершины: сторона по ближайшей точке шва за его концом (вершиной) теряет смысл, и грубый шаг
            // по 2·win (до 60 мм) перескакивал пересечение в 0.1 мм от t*
            double tEndV = dk > 0 ? Delta : 0, span = Math.Min(2 * win, 8 * Math.Abs(tEndV - tS) + 0.05 * win);
            double tB1 = Cross0(t => Rail(spT, shT, t) is { } q ? SideOn(q, shT, t) : double.NaN, tS, tS + dk * span);
            double tB2 = Cross0(t => Rail(spE, shE, t) is { } q ? SideOn(q, shE, t) : double.NaN, tS, tS - dk * span);
            Debug?.Invoke($"open: crease bevel {k}: mitre side T {mT:E2} E {mE:E2}; T rail hits the seam at t {tB1:G8}, E rail (back) at t {tB2:G8}");
            if (double.IsNaN(tB1) || double.IsNaN(tB2))
            {
                if (Debug != null)
                    for (int i = 0; i <= 8; i++)
                    {
                        double t1s = tS + dk * span * i / 8.0, t2s = tS - dk * span * i / 8.0;
                        var r1 = Rail(spT, shT, t1s); var r2 = Rail(spE, shE, t2s);
                        Debug($"open: crease bevel rails: T t {t1s:G8} side {(r1 is { } a1 ? SideOn(a1, shT, t1s) : double.NaN):E3} | E t {t2s:G8} side {(r2 is { } a2 ? SideOn(a2, shE, t2s) : double.NaN):E3}; vertex t {(dk > 0 ? Delta : 0):G6} win {win:G4}");
                    }
                Debug?.Invoke("open: crease bevel — a rail does not reach the seam"); return null;
            }
            var B1 = Rail(spT, shT, tB1)!.Value; var B2 = Rail(spE, shE, tB2)!.Value;
            double o1 = (sc.PointAt(CurveParam.Closest(sc, B1)) - B1).Length, o2 = (sc.PointAt(CurveParam.Closest(sc, B2)) - B2).Length;
            if (o1 > 1e-7 * rr || o2 > 1e-7 * rr)
            {
                if (Debug != null)
                {
                    var Vv = ends[k].V; var sa = sc.PointAt(sc.T0); var sb = sc.PointAt(sc.T1);
                    Debug($"open: crease bevel off-seam: V {Vv:0.#####} B1 {B1:0.#####} |B1-V| {(B1 - Vv).Length:E2} |B2-V| {(B2 - Vv).Length:E2}; seam ends {sa:0.####} / {sb:0.####}");
                    for (int i = 0; i <= 8; i++)
                    {
                        double tt = tS + (tB1 - tS) * i / 8.0; var q = Rail(spT, shT, tt); var xs = spT(tt);
                        if (q == null || xs == null) continue;
                        Debug($"open: crease bevel off-seam T rail t {tt:G8}: |q-V| {(q.Value - Vv).Length:E3} F_E(q) {Geometry.Ssi.Implicit(shE, q.Value).F:E3} F_E(c) {Geometry.Ssi.Implicit(shE, xs.Value).F:E3} side {SideOn(q.Value, shT, tt):E3}");
                    }
                }
                Debug?.Invoke($"open: crease bevel — rail ends off the seam ({o1:E1}/{o2:E1})"); return null;
            }
            var V = ends[k].V; bool bigT = (B1 - V).Length > (B2 - V).Length;
            var bv = new CreaseBevel { BigT = bigT, C = c, QS = qS, QT = qT, QE = qE, TS = tS, SpT = spT, SpE = spE, ShT = shT, ShE = shE, ShS = shS };
            bool same = (B1 - B2).Length < 10 * 1e-7 * rr;
            if (same) { bv.B = B1; bv.Pm = B1; bv.TB = bigT ? tB1 : tB2; bv.TPm = bigT ? tB2 : tB1; bv.TX = tB1; bv.TY = tB2; }
            else
            {
                // PMil: край меньшей ленты на её грани входит в трубку большей
                var bigSp = bigT ? spT : spE; var smSp = bigT ? spE : spT; var smSh = bigT ? shE : shT;
                double tEnd2 = bigT ? tB2 : tB1;
                double Phi(double t) => Rail(smSp, smSh, t) is { } q && Tube(bigSp, q) is { } tb ? tb.D - r : double.NaN;
                double p0 = Phi(tS);
                if (!(p0 < 0)) { Debug?.Invoke($"open: crease bevel — the smaller rail at t* is not inside the bigger tube ({p0:E2})"); return null; }
                double tPm = Cross0(Phi, tS, tEnd2);
                if (double.IsNaN(tPm)) { Debug?.Invoke("open: crease bevel — the smaller rail does not leave the bigger tube before the seam"); return null; }
                bv.B = bigT ? B1 : B2; bv.TB = bigT ? tB1 : tB2; bv.TPm = tPm; bv.Pm = Rail(smSp, smSh, tPm)!.Value;
                bv.TX = bigT ? tB1 : tPm; bv.TY = bigT ? tPm : tB2;
            }
            // кривые пересечения: точки на двух формах в плоскостях поперёк хорды, таблица от конца b (трансверсального)
            ICurve3? Inter(Func<Vector3d, (double F, Vector3d G)?> f1, Func<Vector3d, (double F, Vector3d G)?> f2, Vector3d a, Vector3d b, string nm)
            {
                var m = b - a; double Lm = m.Length; if (!(Lm > 1e-12)) return null; m /= Lm;
                Vector3d? Solve(double l, Vector3d x0)
                {
                    var p = a + (b - a) * l; var x = x0;
                    for (int it = 0; it < 80; it++)
                    {
                        var r1 = f1(x); var r2 = f2(x); if (r1 == null || r2 == null) return null;
                        double f3 = Vector3d.Dot(x - p, m);
                        var dx = Solve3(new[] { r1.Value.G, r2.Value.G, m }, new[] { -r1.Value.F, -r2.Value.F, -f3 });
                        if (dx == null) return null;
                        x += dx.Value;
                        if (dx.Value.Length < 1e-14 * Math.Max(1, x.Length)) break;
                    }
                    var e1 = f1(x); var e2 = f2(x);
                    if (e1 == null || e2 == null || Math.Abs(e1.Value.F) > 1e-9 * rr || Math.Abs(e2.Value.F) > 1e-9 * rr || Math.Abs(Vector3d.Dot(x - p, m)) > 1e-9 * rr) return null;
                    return x;
                }
                const int nTab = 32; var tab = new Vector3d[nTab + 1]; tab[0] = a; tab[nTab] = b;
                var prevX = b;
                for (int i = nTab - 1; i >= 1; i--)
                {
                    var s = Solve(i / (double)nTab, prevX + (a - b) / nTab);
                    if (s == null) { Debug?.Invoke($"open: crease bevel — {nm}: point {i}/{nTab} not found"); return null; }
                    tab[i] = s.Value; prevX = s.Value;
                }
                return new ProcCurve3(l =>
                {
                    if (l <= 0) return a; if (l >= 1) return b;
                    double x = l * nTab; int i = Math.Clamp((int)x, 0, nTab - 1); double fr = x - i;
                    var guess = tab[i] + (tab[i + 1] - tab[i]) * fr;
                    return Solve(l, guess) ?? new Vector3d(double.NaN);
                }, 0, 1, false, nm);
            }
            (double F, Vector3d G)? TubeF(Func<double, Vector3d?> sp, Vector3d x) => Tube(sp, x) is { } tb ? (tb.D - r, tb.G) : null;
            (double F, Vector3d G)? FaceF(Geometry.Ssi.Shape sh, Vector3d x) { var (f, g) = Geometry.Ssi.Implicit(sh, x); return (f, g); }
            var mit = (bv.Pm - qS).Length > 1e-9 * rr ? Inter(x => TubeF(spT, x), x => TubeF(spE, x), qS, bv.Pm, "crease mitre") : null;
            if (mit == null) return null;
            bv.Mitre = mit;
            if (!same)
            {
                var cl = Inter(x => TubeF(bigT ? spT : spE, x), x => FaceF(bigT ? shE : shT, x), bv.Pm, bv.B, "crease cliff");
                if (cl == null) return null;
                bv.Cliff = cl;
            }
            // промежутки частей покрывают параметры трубок у кривых угла (ближайшая точка хребта к точкам митры и обрыва может
            // лежать за TX/TY — грань там продолжалась бы за край своей области)
            {
                double txr = bv.TX, tyr = bv.TY;
                foreach (var cv in new[] { bv.Mitre, bv.Cliff })
                {
                    if (cv == null) continue;
                    for (int i = 0; i <= 32; i++)
                    {
                        var x = cv.PointAt(cv.T0 + (cv.T1 - cv.T0) * i / 32.0);
                        if (Tube(spT, x) is { } a1) txr = dk > 0 ? Math.Max(txr, a1.T) : Math.Min(txr, a1.T);
                        if (Tube(spE, x) is { } a2) tyr = dk > 0 ? Math.Min(tyr, a2.T) : Math.Max(tyr, a2.T);
                    }
                }
                double mg = 0.02 * Math.Abs(bv.TX - tS) + 1e-6 * Math.Max(1, Delta);
                bv.TXs = txr + dk * mg; bv.TYs = tyr - dk * mg;
            }
            double tx = bv.TX, ty = bv.TY;
            bv.RailT = new ProcCurve3(l => Rail(spT, shT, tS + (tx - tS) * l) ?? new Vector3d(double.NaN), 0, 1, false, "rolling-ball contact");
            bv.RailE = new ProcCurve3(l => Rail(spE, shE, ty + (tS - ty) * l) ?? new Vector3d(double.NaN), 0, 1, false, "rolling-ball contact");
            Debug?.Invoke($"open: crease bevel {k}: big {(bigT ? "T" : "E")}, seam point {bv.B:0.#####} (other {(bigT ? B2 : B1):0.#####}, apart {(B1 - B2).Length:E2}), PMil {bv.Pm:0.#####} t {bv.TPm:G8}; T part to t {tx:G8}, E part from t {ty:G8}");
            return bv;
        }
        bool CreaseAssemble()
        {
            // k148f: ЛЕНТА У ВОГНУТОГО ИЗЛОМА — ДВЕ ГРАНИ И ГРАНЬ-ОБРЫВ. Сечение шара трёх граней в плоскости t* — одна окружность:
            // дуга от точки на S до точки на T (часть ленты до t*) и до точки на E (часть после t*); общая дуга — до ближней из
            // двух, остаток (ближняя → дальняя) — край той части, чьё сечение длиннее, и грани-обрыва в плоскости t*. Грань-
            // обрыв ограничена этим остатком и следами T и E в плоскости до точки шва; шов укорочен до неё.
            if ((!crOn[0] || crBv[0] != null) && (!crOn[1] || crBv[1] != null)) return BevelAll();
            // k148g: СКОС У ОДНОГО ИЛИ ОБОИХ КОНЦОВ. Части ленты по t: E у конца 0 (до TY0), T (от TX0 до TX1), E у конца 1 (от TY1).
            // Край у излома каждой части: от qS митра до PMil, у большей — обрыв до точки шва B, дальше свой край на своей грани.
            // Контур части (сторона A — T): сторона a→b, конец b (T→S), S b→a, конец a (S→T); при T на B — обратный обход.
            bool BevelAll()
            {
                var bvs = new[] { crOn[0] ? crBv[0] : null, crOn[1] ? crBv[1] : null };
                int kk0 = bvs[0] != null ? 0 : 1;
                bool tAb = swOnA[kk0];
                for (int e = 0; e < 2; e++)
                {
                    if (bvs[e] != null && swOnA[e] != tAb) { cwhy = "open curved: concave crease — side faces differ at the two ends"; return false; }
                    // k149: у другого конца — перекат через шов (шар катится по ребру шва и ложится на соседнюю грань): часть T
                    // начинается с кусков края на шве и на соседней грани, лента там — общая (Gen по хребту с переходом)
                    if (bvs[e] == null && swPart[e] != null && (!CreaseRev || swOnA[e] != tAb)) { cwhy = "open curved: concave crease with a seam switch at the other end"; return false; }
                }
                ICurve3 mT = tAb ? bA : bB;
                double s0b = tAb ? tB0 : tA0, s1b = tAb ? tB1 : tA1;
                Func<double, Vector3d> fSb = tAb ? QB : QA;
                Vector3d pT0b = tAb ? qa0 : qb0, pT1b = tAb ? qa1 : qb1, pS0b = tAb ? qb0 : qa0, pS1b = tAb ? qb1 : qa1;
                foreach (var bv in bvs)
                {
                    if (bv == null) continue;
                    if (!(bv.TS > s0b && bv.TS < s1b)) { cwhy = "open curved: concave crease outside the contact line"; return false; }
                    if ((fSb(bv.TS) - bv.QS).Length > 1e-7) { cwhy = "open curved: concave crease — the S contact does not pass the three-face ball"; return false; }
                }
                List<Seg> Up(CreaseBevel bv, bool eSide)
                {
                    var l = new List<Seg> { new() { Curve = bv.Mitre, A = bv.QS, B = bv.Pm } };
                    bool big = eSide ? !bv.BigT : bv.BigT;
                    if (big && bv.Cliff != null) l.Add(new Seg { Curve = bv.Cliff, A = bv.Pm, B = bv.B });
                    var from = big ? bv.B : bv.Pm;
                    if (eSide) l.Add(new Seg { Curve = bv.RailE, A = from, B = bv.QE });
                    else l.Add(new Seg { Curve = bv.RailT, A = from, B = bv.QT });
                    return l;
                }
                List<Seg> Rev(List<Seg> l) => l.AsEnumerable().Reverse().Select(x => new Seg { Curve = x.Curve, A = x.B, B = x.A }).ToList();
                List<Seg> Down(CreaseBevel bv, bool eSide) => Rev(Up(bv, eSide));
                ICurve3 SC(double a, double b) => new ProcCurve3(fSb, a, b, false, "rolling-ball contact");
                SweepSection GenR(double t, bool eReg, CreaseBevel bv)
                {
                    var x = (eReg ? bv.SpE(t) : bv.SpT(t)) ?? new Vector3d(double.NaN);
                    var qSide = Foot(eReg ? bv.ShE : bv.ShT, x); var qs2 = Foot(bv.ShS, x);
                    var qa2 = tAb ? qSide : qs2; var qb2 = tAb ? qs2 : qSide;
                    var e1b = (qa2 - x).Normalized(); var v2b = (qb2 - x).Normalized();
                    var e2b = v2b - e1b * Vector3d.Dot(e1b, v2b);
                    return SweepSection.Arc(x, e1b, e2b.Normalized(), r, Math.Atan2(e2b.Length, Vector3d.Dot(e1b, v2b)));
                }
                double Uof(double t) => (t - tLo) * kU;
                int swE = bvs[0] == null && swPart[0] != null ? 0 : bvs[1] == null && swPart[1] != null ? 1 : -1;
                double tSwMid = swE < 0 ? double.NaN : 0.5 * (swT[swE] + (bvs[1 - swE]!).TS);
                SectionSurface PartR(double ua, double ub, bool eReg, CreaseBevel bv)
                {
                    Func<double, SweepSection> g = U =>
                    {
                        double t = tLo + U / kU;
                        if (!eReg && swE >= 0 && (swE == 0 ? t < tSwMid : t > tSwMid)) return Gen(t);
                        return GenR(t, eReg, bv);
                    };
                    var st = new List<double>(); var sc2 = new List<SweepSection>();
                    // сечения — те же, что у основной ленты (сгущены по кривизне), плюс концы части и 24 равных (у короткой части);
                    // 24 равных на всю часть давали отход кривых угла от грани 3–6 мкм (s110)
                    var Us = us.Select(t => (t - tLo) * kU).Where(U => U > ua + 1e-9 && U < ub - 1e-9).ToList();
                    for (int i = 0; i <= 24; i++) Us.Add(ua + (ub - ua) * i / 24);
                    Us.Sort(); var Uu = new List<double>(); foreach (var U in Us) if (Uu.Count == 0 || U - Uu[^1] > 1e-9 * Math.Max(1, ub - ua)) Uu.Add(U);
                    Uu[^1] = ub;
                    foreach (var U in Uu) { st.Add(U); sc2.Add(g(U)); }
                    var brk = surf.Breaks.Where(b => b > ua + 1e-9 && b < ub - 1e-9).ToList();
                    return new SectionSurface(g, st, sc2, false, ua, ub, brk, tabStep) { ParallelTable = true };
                }
                void AddPart(SectionSurface sf, bool eReg, CreaseBevel bv, double tm, List<Seg> side, List<Seg> endB, Seg sAB, List<Seg> endA)
                {
                    List<Seg> lp;
                    if (tAb) { lp = new(side); lp.AddRange(endB); lp.Add(new Seg { Curve = sAB.Curve, A = sAB.B, B = sAB.A }); lp.AddRange(endA); }
                    else { lp = new() { sAB }; lp.AddRange(Rev(endB)); lp.AddRange(Rev(side)); lp.AddRange(Rev(endA)); }
                    var gm2 = GenR(tm, eReg, bv); var mid2 = gm2.Point(0.5);
                    var want2 = (mid2 - gm2.C).Normalized() * (-sigma);
                    var n2 = sf.NormalAt(sf.ProjectToUV(mid2));
                    var fd = new FDesc { Surf = sf, Rev = Vector3d.Dot(n2, want2) < 0, Tag = "round:face", Touched = true, Hint = mid2 };
                    fd.Loops.Add(lp); blends.Add(fd);
                }
                var b0 = bvs[0]; var b1 = bvs[1]; var bAny = (b0 ?? b1)!;
                // линия касания на S делится в изломах; те же куски — и в контуре грани S (сборка сводит рёбра по кривой и концам)
                var sCuts = new List<(double T, Vector3d P)> { (s0b, pS0b) };
                if (b0 != null) sCuts.Add((b0.TS, b0.QS));
                if (b1 != null) sCuts.Add((b1.TS, b1.QS));
                sCuts.Add((s1b, pS1b));
                var sPieces = new List<Seg>();
                for (int i = 0; i + 1 < sCuts.Count; i++) sPieces.Add(new Seg { Curve = SC(sCuts[i].T, sCuts[i + 1].T), A = sCuts[i].P, B = sCuts[i + 1].P });
                {
                    var FSb = tAb ? FB : FA; var sMain = tAb ? bB : bA; bool dS = false;
                    foreach (var loop in FSb.Loops)
                    {
                        for (int i = 0; i < loop.Count && !dS; i++)
                        {
                            if (!ReferenceEquals(loop[i].Curve, sMain)) continue;
                            bool fwd = (loop[i].A - pS0b).Length < 1e-7 && (loop[i].B - pS1b).Length < 1e-7;
                            bool bwd = (loop[i].A - pS1b).Length < 1e-7 && (loop[i].B - pS0b).Length < 1e-7;
                            if (!fwd && !bwd) continue;
                            loop.RemoveAt(i);
                            loop.InsertRange(i, (fwd ? sPieces.Select(x => new Seg { Curve = x.Curve, A = x.A, B = x.B }) : Rev(sPieces)).Select(x => { x.JointPiece = true; return x; }));
                            dS = true;
                        }
                        if (dS) break;
                    }
                    if (!dS) { cwhy = "open curved: concave crease — the contact line on S not found in its face"; return false; }
                    FSb.Touched = true;
                }
                double uX0 = b0 != null ? Uof(b0.TXs) : 0, uX1 = b1 != null ? Uof(b1.TXs) : Lp;
                double uY0 = b0 != null ? Uof(b0.TYs) : 0, uY1 = b1 != null ? Uof(b1.TYs) : Lp;
                if (!(uX0 >= 0 && uX1 <= Lp && uX0 < uX1) || (b0 != null && !(uY0 > 0 && uY0 < Lp)) || (b1 != null && !(uY1 > 0 && uY1 < Lp)))
                { cwhy = "open curved: concave crease — bevel beyond the blend range"; return false; }
                // часть T (середина)
                {
                    double ta = b0 != null ? b0.TS : s0b, tb = b1 != null ? b1.TS : s1b;
                    var side = new List<Seg>();
                    Vector3d sa = b0 != null ? b0.QT : pT0b, sb = b1 != null ? b1.QT : pT1b;
                    if (swE == 0)
                    {
                        int n0 = side.Count;
                        foreach (var pc in swParts[0]!) side.Add(new Seg { Curve = pc.C, A = pc.A, B = pc.B });
                        side[n0].A = pT0b; side[^1].B = swQ[0]; sa = swQ[0];
                    }
                    side.Add(new Seg { Curve = mT, A = sa, B = swE == 1 ? swQ[1] : sb });
                    if (swE == 1)
                    {
                        int n1 = side.Count;
                        foreach (var pc in swParts[1]!) side.Add(new Seg { Curve = pc.C, A = pc.A, B = pc.B });
                        side[n1].A = swQ[1]; side[^1].B = pT1b;
                    }
                    var endB = b1 != null ? Down(b1, false) : new List<Seg> { new() { Curve = bE1, A = pT1b, B = pS1b } };
                    var sAB = sPieces[b0 != null ? 1 : 0];
                    var endA = b0 != null ? Up(b0, false) : new List<Seg> { new() { Curve = bE0, A = pS0b, B = pT0b } };
                    AddPart(PartR(uX0, uX1, false, bAny), false, bAny, 0.5 * (ta + tb), side, endB, sAB, endA);
                }
                if (b0 != null)
                {
                    var side = new Seg { Curve = swParts[0]![0].C, A = pT0b, B = b0.QE };
                    var sAB = sPieces[0];
                    AddPart(PartR(0, uY0, true, b0), true, b0, 0.5 * (tLo + b0.TS), new List<Seg> { side }, Down(b0, true), sAB, new List<Seg> { new() { Curve = bE0, A = pS0b, B = pT0b } });
                }
                if (b1 != null)
                {
                    var side = new Seg { Curve = swParts[1]![0].C, A = b1.QE, B = pT1b };
                    var sAB = sPieces[^1];
                    AddPart(PartR(uY1, Lp, true, b1), true, b1, 0.5 * (b1.TS + tHi), new List<Seg> { side }, new List<Seg> { new() { Curve = bE1, A = pT1b, B = pS1b } }, sAB, Up(b1, true));
                }
                // грани T и E у каждого излома
                for (int kk = 0; kk < 2; kk++)
                {
                    var bv = bvs[kk]; if (bv == null) continue;
                    var qT = bv.QT; var qE = bv.QE;
                    var xT = bv.BigT ? bv.B : bv.Pm;
                    var insT = new List<Seg> { new() { Curve = bv.RailT, A = qT, B = xT, JointPiece = true } };
                    if (!bv.BigT && bv.Cliff != null) insT.Add(new Seg { Curve = bv.Cliff, A = bv.Pm, B = bv.B, JointPiece = true });
                    var FTb = tAb ? FA : FB; bool dT = false;
                    foreach (var loop in FTb.Loops)
                    {
                        for (int i = 0; i < loop.Count && !dT; i++)
                        {
                            var s1g = loop[i]; var s2g = loop[(i + 1) % loop.Count];
                            if ((s1g.B - qT).Length < 1e-9 && (s2g.A - bv.B).Length < 1e-9) { loop.InsertRange(i + 1, insT); dT = true; }
                            else if ((s1g.B - bv.B).Length < 1e-9 && (s2g.A - qT).Length < 1e-9) { loop.InsertRange(i + 1, Rev(insT).Select(x => { x.JointPiece = true; return x; })); dT = true; }
                        }
                        if (dT) break;
                    }
                    if (!dT) { cwhy = "open curved: concave crease — the side face loop does not meet the seam point"; return false; }
                    var FEb = ends[kk].FE; var seamEb = tAb ? ends[kk].EA : ends[kk].EB; bool dE = false;
                    var endPb = kk == 0 ? pT0b : pT1b;
                    var insE = new List<Seg>();
                    if (bv.BigT && bv.Cliff != null) insE.Add(new Seg { Curve = bv.Cliff, A = bv.B, B = bv.Pm, JointPiece = true });
                    insE.Add(new Seg { Curve = bv.RailE, A = bv.BigT ? bv.Pm : bv.B, B = qE, JointPiece = true });
                    insE.Add(new Seg { Curve = swParts[kk]![0].C, A = qE, B = endPb, JointPiece = true });
                    foreach (var loop in FEb.Loops)
                    {
                        for (int i = 0; i < loop.Count && !dE; i++)
                        {
                            if (loop[i].Id != seamEb) continue;
                            if ((loop[i].B - bv.B).Length < 1e-9) { loop.InsertRange(i + 1, insE); dE = true; }
                            else if ((loop[i].A - bv.B).Length < 1e-9) { loop.InsertRange(i, Rev(insE).Select(x => { x.JointPiece = true; return x; })); dE = true; }
                        }
                        if (dE) break;
                    }
                    if (!dE) { cwhy = "open curved: concave crease — the seam end not found in the next face"; return false; }
                    FEb.Touched = true; FTb.Touched = true;
                    Debug?.Invoke($"open: concave crease bevel {kk} at t {bv.TS:G8}: big {(bv.BigT ? "T" : "E")}, mitre {(bv.Pm - bv.QS).Length:E2}, cliff {(bv.B - bv.Pm).Length:E2}");
                }
                return true;
            }
            if (crOn[0] && crOn[1]) { cwhy = "open curved: concave crease at both ends"; return false; }
            int k = crOn[0] ? 0 : 1; bool tA = swOnA[k];
            if (swPart[1 - k] != null) { cwhy = "open curved: concave crease with a seam switch at the other end"; return false; }
            double ts = crT[k]; var c = crC[k]; var qS = crQS[k]; var qT = crQT[k]; var qE = crQE[k]; var sP = crSeamPt[k];
            var shTk = crShT[k]!; var shEk = crShE[k]!;
            var Pp = Ed(ts); var Tp = Td(ts).Normalized();
            // окружность сечения: от qS, в сторону q-точек
            var e1 = (qS - c).Normalized(); var mT = qT - c; var e2 = (mT - e1 * Vector3d.Dot(mT, e1)); e2 -= Tp * Vector3d.Dot(e2, Tp); e2.Normalize();
            double Ang(Vector3d q) { var w = q - c; return Math.Atan2(Vector3d.Dot(w, e2), Vector3d.Dot(w, e1)); }
            {
                var nT = Vector3d.Cross(qS - c, qT - c).Normalized(); var nE = Vector3d.Cross(qS - c, qE - c).Normalized();
                Debug?.Invoke($"open: crease planes: T-section normal {nT:0.######} E-section normal {nE:0.######} angle {Math.Acos(Math.Clamp(Vector3d.Dot(nT, nE), -1, 1)):E2} rad; section plane normal {Tp:0.######}; qT off plane {Vector3d.Dot(qT - Pp, Tp):E2}, qE off {Vector3d.Dot(qE - Pp, Tp):E2}, qS off {Vector3d.Dot(qS - Pp, Tp):E2}");
            }
            double aT = Ang(qT), aE = Ang(qE);
            if (!(aT > 0 && aE > 0)) { cwhy = "open curved: concave crease — section points not on one side"; return false; }
            bool tNear = aT < aE; var near = tNear ? qT : qE; var far = tNear ? qE : qT; double aN = Math.Min(aT, aE), aF = Math.Max(aT, aE);
            var fr = new Frame3(c, e1, e2);
            var shared = new ArcCurve3(fr, r, 0, aN); var extra = new ArcCurve3(fr, r, aN, aF);
            // следы T и E в плоскости t*: от точки касания до точки шва
            Vector3d? OnSurfPlane(Geometry.Ssi.Shape sh, Vector3d x0)
            {
                var x = x0;
                for (int it = 0; it < 40; it++)
                {
                    var (f1, g1) = Geometry.Ssi.Implicit(sh, x); double f2 = Vector3d.Dot(x - Pp, Tp);
                    if (Math.Abs(f1) < 1e-14 && Math.Abs(f2) < 1e-14) break;
                    double a11 = Vector3d.Dot(g1, g1), a12 = Vector3d.Dot(g1, Tp), det = a11 - a12 * a12;
                    if (!(Math.Abs(det) > 1e-20)) { Debug?.Invoke($"open: crease trace: singular at {x:0.####} f {f1:E2} g {g1:0.###} ({sh.GetType().Name})"); return null; }
                    double l1 = (f1 - a12 * f2) / det, l2 = (a11 * f2 - a12 * f1) / det;
                    x -= g1 * l1 + Tp * l2;
                }
                double rf1 = Geometry.Ssi.Implicit(sh, x).F, rf2 = Vector3d.Dot(x - Pp, Tp);
                if (!(Math.Abs(rf1) < 1e-9 && Math.Abs(rf2) < 1e-9)) { Debug?.Invoke($"open: crease trace point failed at {x0:0.####}: f {rf1:E2} plane {rf2:E2}"); return null; }
                return x;
            }
            ICurve3 Wall(Geometry.Ssi.Shape sh, Vector3d a, Vector3d b, string nmw) =>
                new ProcCurve3(l => l <= 0 ? a : l >= 1 ? b : OnSurfPlane(sh, a + (b - a) * l) ?? new Vector3d(double.NaN), 0, 1, false, nmw);
            var wallT = Wall(shTk, qT, sP, "crease wall"); var wallE = Wall(shEk, sP, qE, "crease wall");
            for (int i = 1; i < 16; i++) if (double.IsNaN(wallT.PointAt(i / 16.0).X) || double.IsNaN(wallE.PointAt(i / 16.0).X)) { cwhy = "open curved: concave crease — face trace in the section plane not found"; return false; }
            // кривые сторон: T — главная (до t*) и кусок на E (после); S — делится в t*
            ICurve3 mainT = tA ? bA : bB, pieceE = swParts[k]![0].C, sideS = tA ? bB : bA;
            double s0 = tA ? tB0 : tA0, s1 = tA ? tB1 : tA1;
            Func<double, Vector3d> fS = tA ? QB : QA;
            if (!(ts > s0 && ts < s1)) { cwhy = "open curved: concave crease outside the contact line"; return false; }
            if ((fS(ts) - qS).Length > 1e-7) { cwhy = "open curved: concave crease — the S contact does not pass the three-face ball"; return false; }
            var S0 = new ProcCurve3(fS, s0, ts, false, ch ? "chamfer contact" : "rolling-ball contact"); var S1 = new ProcCurve3(fS, ts, s1, false, ch ? "chamfer contact" : "rolling-ball contact");
            Vector3d pT0 = tA ? qa0 : qb0, pT1 = tA ? qa1 : qb1, pS0 = tA ? qb0 : qa0, pS1 = tA ? qb1 : qa1;
            // концы частей у t*: у части конца k — qE, у другой — qT
            Vector3d endAtTs0 = k == 0 ? qE : qT, endAtTs1 = k == 0 ? qT : qE;
            ICurve3 side0 = k == 0 ? pieceE : mainT, side1 = k == 0 ? mainT : pieceE;
            List<Seg> SectionUp(Vector3d to) { var l = new List<Seg> { new() { Curve = shared, A = qS, B = near } }; if ((to - far).Length < 1e-12) l.Add(new Seg { Curve = extra, A = near, B = far }); return l; }
            List<Seg> SectionDown(Vector3d from) => SectionUp(from).AsEnumerable().Reverse().Select(x => new Seg { Curve = x.Curve, A = x.B, B = x.A }).ToList();
            List<Seg> L0, L1;
            if (tA)
            {
                L0 = new() { new() { Curve = side0, A = pT0, B = endAtTs0 } }; L0.AddRange(SectionDown(endAtTs0)); L0.Add(new Seg { Curve = S0, A = qS, B = pS0 }); L0.Add(new Seg { Curve = bE0, A = pS0, B = pT0 });
                L1 = SectionUp(endAtTs1); L1.Add(new Seg { Curve = side1, A = endAtTs1, B = pT1 }); L1.Add(new Seg { Curve = bE1, A = pT1, B = pS1 }); L1.Add(new Seg { Curve = S1, A = pS1, B = qS });
            }
            else
            {
                L0 = new() { new() { Curve = S0, A = pS0, B = qS } }; L0.AddRange(SectionUp(endAtTs0)); L0.Add(new Seg { Curve = side0, A = endAtTs0, B = pT0 }); L0.Add(new Seg { Curve = bE0, A = pT0, B = pS0 });
                L1 = new() { new() { Curve = S1, A = qS, B = pS1 }, new() { Curve = bE1, A = pS1, B = pT1 }, new() { Curve = side1, A = pT1, B = endAtTs1 } }; L1.AddRange(SectionDown(endAtTs1));
            }
            // поверхности частей: та же формула на своих промежутках; у части на E в t* — сечение режима E
            double Us = (ts - tLo) * kU, epsT = 1e-12 * Math.Max(1, Delta);
            var gA = surf; // формула по U
            SectionSurface Part(double ua, double ub, bool eSide)
            {
                Func<double, SweepSection> g = U => { double t = tLo + U / kU; if (eSide && Math.Abs(t - ts) < epsT * 4) t = ts + (k == 1 ? 1 : -1) * epsT * 4; return Gen(t); };
                var st = new List<double>(); var sc2 = new List<SweepSection>();
                int nst = 24; for (int i = 0; i <= nst; i++) { double U = ua + (ub - ua) * i / nst; st.Add(U); sc2.Add(g(U)); }
                var brk = surf.Breaks.Where(b => b > ua + 1e-9 && b < ub - 1e-9).ToList();
                return new SectionSurface(g, st, sc2, false, ua, ub, brk, tabStep) { ParallelTable = true };
            }
            var surf0 = Part(0, Us, k == 0); var surf1 = Part(Us, Lp, k == 1);
            FDesc Mk(SectionSurface sf, List<Seg> lp, double tm)
            {
                var gm2 = Gen(tm); var mid2 = gm2.Point(0.5);
                var want2 = (mid2 - gm2.C).Normalized() * (-sigma);
                var n2 = sf.NormalAt(sf.ProjectToUV(mid2));
                var fd = new FDesc { Surf = sf, Rev = Vector3d.Dot(n2, want2) < 0, Tag = "round:face", Touched = true, Hint = mid2 };
                fd.Loops.Add(lp); return fd;
            }
            blends.Add(Mk(surf0, L0, 0.5 * (tLo + ts))); blends.Add(Mk(surf1, L1, 0.5 * (ts + tHi)));
            // грань-обрыв: снаружи — сторона части с длинным сечением (дальняя точка)
            bool farIsPart1 = (far - endAtTs1).Length < 1e-12;
            var outN = Tp * (farIsPart1 ? 1 : -1);
            var nearWall = tNear ? wallT : wallE; var farWall = tNear ? wallE : wallT;
            var rLoop = new List<Seg> { new() { Curve = extra, A = near, B = far } };
            rLoop.Add(new Seg { Curve = farWall, A = far, B = sP });
            rLoop.Add(new Seg { Curve = nearWall, A = sP, B = near });
            var rf = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(c, outN)), Rev = false, Tag = "round:crease", Touched = true, N = outN, Hint = (near + far + sP) / 3 };
            rf.Loops.Add(rLoop); blends.Add(rf);
            // грань T: от конца линии касания (qT) — след T до точки шва
            var FT = tA ? FA : FB; bool doneT = false;
            foreach (var loop in FT.Loops)
            {
                for (int i = 0; i < loop.Count && !doneT; i++)
                {
                    var s1g = loop[i]; var s2g = loop[(i + 1) % loop.Count];
                    if ((s1g.B - qT).Length < 1e-9 && (s2g.A - sP).Length < 1e-9) { loop.Insert(i + 1, new Seg { Curve = wallT, A = qT, B = sP }); doneT = true; }
                    else if ((s1g.B - sP).Length < 1e-9 && (s2g.A - qT).Length < 1e-9) { loop.Insert(i + 1, new Seg { Curve = wallT, A = sP, B = qT }); doneT = true; }
                }
                if (doneT) break;
            }
            if (!doneT) { cwhy = "open curved: concave crease — the side face loop does not meet the seam point"; return false; }
            // грань E: у конца шва (sP) — след E до qE и кусок линии касания на E до конца звена
            var FEk = ends[k].FE; var seamE = tA ? ends[k].EA : ends[k].EB; bool doneE = false;
            var endP = k == 0 ? pT0 : pT1;
            foreach (var loop in FEk.Loops)
            {
                for (int i = 0; i < loop.Count && !doneE; i++)
                {
                    if (loop[i].Id != seamE) continue;
                    if ((loop[i].B - sP).Length < 1e-9) { loop.InsertRange(i + 1, new[] { new Seg { Curve = wallE, A = sP, B = qE, JointPiece = true }, new Seg { Curve = pieceE, A = qE, B = endP, JointPiece = true } }); doneE = true; }
                    else if ((loop[i].A - sP).Length < 1e-9) { loop.InsertRange(i, new[] { new Seg { Curve = pieceE, A = endP, B = qE, JointPiece = true }, new Seg { Curve = wallE, A = qE, B = sP, JointPiece = true } }); doneE = true; }
                }
                if (doneE) break;
            }
            if (!doneE) { cwhy = "open curved: concave crease — the seam end not found in the next face"; return false; }
            FEk.Touched = true; FT.Touched = true;
            Debug?.Invoke($"open: concave crease at t {ts:G8}: section {aN:G4}/{aF:G4} rad, near {(tNear ? "T" : "E")}, cliff chord {(far - near).Length:E2}");
            return true;
        }
        Debug?.Invoke($"open: sigma {sigma} shapes {shA.GetType().Name}/{shB.GetType().Name} ends {(joint[0] != 0 ? "joint" : ends[0].ShE.GetType().Name)}/{(joint[1] != 0 ? "joint" : ends[1].ShE.GetType().Name)} t [{tLo:G5}, {tHi:G5}] contacts A [{tA0:G6}, {tA1:G6}] B [{tB0:G6}, {tB1:G6}] Δ {Delta:G6}");
        return true;
    }

    static readonly bool OpenCylOn = Environment.GetEnvironmentVariable("P3D_OPENCYL") != "0";
    static readonly bool EndFine = Environment.GetEnvironmentVariable("P3D_DRENDFINE") != "0";
    /// <summary>
    /// k144: ЗАЗОР ДО ПРОЧИХ РЁБЕР ГРАНИ — по самой полосе (отрезки «ребро → линия касания» сечений на используемом промежутке), а не по полуторной ширине: скругление R10.29
    /// кромки окна Sweep_Blend — полоса на торце-плоскости шириной 13.5 мм, внешняя окружность в 15.4 мм (полоса её не
    /// задевает), а запас 1.5× отправлял скругление резцом. Годность контуров проверяется сборкой тела. Мутация P3D_DRSTRIPTIGHT=0.
    /// </summary>
    static readonly bool StripTight = Environment.GetEnvironmentVariable("P3D_DRSTRIPTIGHT") != "0";
    /// <summary>k144: торец найден долей первого шага продления.</summary>
    public static long EndFineHits;

    /// <summary>u12: плоская лента незамкнутой фаски плоскость/плоскость; null — точки не в одной плоскости или края не прямые.</summary>
    static (PlaneSurface S, ICurve3 A, ICurve3 B, ICurve3 E0, ICurve3 E1)? OpenChamferPlane(Func<double, Vector3d> qa, Func<double, Vector3d> qb,
        Func<int, double, Vector3d> endPt, double tA0, double tA1, double tB0, double tB1)
    {
        Vector3d a0 = qa(tA0), a1 = qa(tA1), b0 = qb(tB0), b1 = qb(tB1);
        var n = Vector3d.Cross(a1 - a0, b0 - a0);
        if (n.LengthSquared < 1e-18) return null;
        n.Normalize();
        double scale = Math.Max(1, Math.Max((a1 - a0).Length, (b0 - a0).Length)), tol = 1e-9 * scale;
        bool OnPl(Vector3d x) => Math.Abs(Vector3d.Dot(x - a0, n)) <= tol;
        bool OnSeg(Vector3d p, Vector3d q, Vector3d x) { var d = q - p; double u = Vector3d.Dot(x - p, d) / d.LengthSquared; return (p + d * u - x).Length <= tol; }
        if (!OnPl(b1)) return null;
        for (int i = 1; i < 8; i++)
        {
            double f = i / 8.0;
            if (!OnSeg(a0, a1, qa(tA0 + (tA1 - tA0) * f)) || !OnSeg(b0, b1, qb(tB0 + (tB1 - tB0) * f))) return null;
            if (!OnSeg(a0, b0, endPt(0, f)) || !OnSeg(a1, b1, endPt(1, f))) return null;
        }
        if ((endPt(0, 0) - a0).Length > tol || (endPt(0, 1) - b0).Length > tol || (endPt(1, 0) - a1).Length > tol || (endPt(1, 1) - b1).Length > tol) return null;
        var pl = new PlaneSurface(Frame3.FromNormal(a0, n));
        return (pl, new LineCurve3(a0, a1), new LineCurve3(b0, b1), new LineCurve3(a0, b0), new LineCurve3(a1, b1));
    }

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
        if (ExtendNurbsOn) ExtendNurbsFaces(faces);
        if (ExtendSectionOn) ExtendSectionFaces(faces);
        foreach (var fd in faces.Where(x => x.Touched)) fd.Hint = InteriorPoint(fd);
        foreach (var fd in blends.Where(x => x.Surf.IsPlanar && Scan)) fd.Hint = ScanlinePoint(fd);
        var all = faces.Concat(blends).ToList();
        var swF = System.Diagnostics.Stopwatch.StartNew();
        bool profWas = Diagnostics.Prof.Enabled; if (Debug != null && ProfDbg) { Diagnostics.Prof.Enabled = true; Diagnostics.Prof.Reset(); }
        try { return FinishDirectCore(all, op, ref why, swF); }
        finally { if (Debug != null && ProfDbg) { Debug(Diagnostics.Prof.Report()); Diagnostics.Prof.Enabled = profWas; } }
    }
    /// <summary>Я2, опыт (по умолчанию выключен, P3D_DRSECBREAK=1): стыки таблицы ленты там, где касание или ребро переходят
    /// стык ленты прежней правки. s110 шаг 2: отход таблицы 2.2e-4 → 2.7e-7 мм, но ещё больше допуска 2e-8 — фаска не принята;
    /// s109 шаг 5: «формула не определена» у стыка (шар катится через складку прежней ленты). BACKLOG, Я2.</summary>
    static readonly bool SecBreak = Environment.GetEnvironmentVariable("P3D_DRSECBREAK") == "1";
    /// <summary>Я2: стыков таблицы ленты там, где касание переходит стык ленты прежней правки.</summary>
    public static long SecBreaks;
    static readonly bool TabRefineOn = Environment.GetEnvironmentVariable("P3D_DRTABREFINE") != "0";
    static readonly AsyncLocal<bool> _tabRefineOff = new();
    /// <summary>Фикстуры: история, записанная до мелкой таблицы (прежние операции шли резцом), воспроизводится так же
    /// (метка «norefine» в manifest.tsv корпуса k140). Течёт и в параллельные пробы.</summary>
    public static bool TabRefineOffForTest { get => _tabRefineOff.Value; set => _tabRefineOff.Value = value; }
    static bool TabRefine => TabRefineOn && !_tabRefineOff.Value;
    /// <summary>Я2: таблиц ленты, перестроенных мельче из-за отхода от формулы.</summary>
    public static long TabRefines;
    /// <summary>Я2, опыт (по умолчанию выключен, P3D_DRTWOEND=1): двухгранный торец ленты. На корпусе единственный случай
    /// (s108 шаг 2) — переполнение: фаска 4.56 шире грани A у торца, «целиком внутри» сечения нет до t = −504; годного случая
    /// для проверки нет — BACKLOG, Я2.</summary>
    static readonly bool EndOver = Environment.GetEnvironmentVariable("P3D_DRENDOVER") != "0";
    [ThreadStatic] static List<(Vector3d V, bool OnA, TEdge? Sg, Vector3d Q)>? _overHints;
    /// <summary>Я2: повторов с переполнением на следующую грань у конца.</summary>
    public static long EndOvers;
    static readonly bool TwoSwitchOff = Environment.GetEnvironmentVariable("P3D_DRTWOSWITCH") == "0";
    static readonly bool TwoEnd = Environment.GetEnvironmentVariable("P3D_DRTWOEND") == "1";
    /// <summary>Я2: лент с двухгранным торцом (вершина степени 4).</summary>
    public static long TwoEnds;
    static readonly bool ProfDbg = Environment.GetEnvironmentVariable("P3D_OPENPROF") == "1";
    static readonly bool EndScanDbg = Environment.GetEnvironmentVariable("P3D_ENDSCAN") == "1";
    static readonly bool EndHole = Environment.GetEnvironmentVariable("P3D_DRENDHOLE") != "0";
    /// <summary>k148f: сечение за торцом найдено долями шага, потому что хребет на шаге не определён.</summary>
    public static long EndHoleHits;
    static readonly bool ExtendNurbsOn = Environment.GetEnvironmentVariable("P3D_DREXTENDNURBS") != "0";
    /// <summary>k144: граней NURBS, продлённых за край области (контур ушёл на продолжение поверхности).</summary>
    public static long NurbsFacesExtended;

    /// <summary>
    /// k144: ПРОДЛЕНИЕ ГРАНИ NURBS ПОД НОВЫЙ КОНТУР. У вогнутого конца ленты (упор в стенку) край ленты и продлённое ребро
    /// торца лежат на продолжении поверхности стенки за краем её области (стенка окна лофта кончается ровно в углу окна).
    /// Поверхность грани заменяется той же поверхностью на большей области (NurbsSurface.Extended — точное продолжение
    /// крайних промежутков) с запасом 5 %; прежде проверка находила ребро «в 3 мм от поверхности» (поверхность прижата к
    /// области) и прямой путь отказывал. Мутация P3D_DREXTENDNURBS=0.
    /// </summary>
    static void ExtendNurbsFaces(List<FDesc> faces)
    {
        foreach (var fd in faces)
        {
            if (!fd.Touched || fd.Surf is not NurbsSurface ns) continue;
            double u0 = 0, u1 = 1, v0 = 0, v1 = 1; bool bad = false;
            foreach (var sg in fd.Loops.SelectMany(l => l))
            {
                for (int i = 0; i <= 16 && !bad; i++)
                {
                    var q = sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * i / 16.0);
                    if (!double.IsFinite(q.X + q.Y + q.Z)) { bad = true; break; }
                    var pr = ns.ProjectExtended(q, 1.0);
                    if (pr is not { } x || (x.P - q).Length > 1e-6) continue;   // точка не на поверхности — не наш случай
                    u0 = Math.Min(u0, x.Uv.X); u1 = Math.Max(u1, x.Uv.X); v0 = Math.Min(v0, x.Uv.Y); v1 = Math.Max(v1, x.Uv.Y);
                }
                if (bad) break;
            }
            if (bad || (u0 > -1e-9 && u1 < 1 + 1e-9 && v0 > -1e-9 && v1 < 1 + 1e-9)) continue;
            double mu = 0.05 * (u1 - u0), mv = 0.05 * (v1 - v0);
            double eu0 = u0 < -1e-9 ? u0 - mu : 0, eu1 = u1 > 1 + 1e-9 ? u1 + mu : 1, ev0 = v0 < -1e-9 ? v0 - mv : 0, ev1 = v1 > 1 + 1e-9 ? v1 + mv : 1;
            if (ns.Extended(eu0, eu1, ev0, ev1) is not { } ext) continue;
            // сверка: продолжение совпадает с прежней поверхностью внутри области
            double dev = 0;
            for (int i = 0; i <= 8; i++) for (int j = 0; j <= 8; j++)
            {
                var uv = new Vector2d(i / 8.0, j / 8.0);
                var q = ns.PointAt(uv);
                var uv2 = new Vector2d((uv.X - eu0) / (eu1 - eu0), (uv.Y - ev0) / (ev1 - ev0));
                dev = Math.Max(dev, (ext.PointAt(uv2) - q).Length);
            }
            Debug?.Invoke($"open: NURBS face {fd.Tag} extended u [{eu0:G4}, {eu1:G4}] v [{ev0:G4}, {ev1:G4}], deviation inside {dev:E1}");
            if (dev > 1e-9) continue;
            fd.Surf = ext; Interlocked.Increment(ref NurbsFacesExtended);
        }
    }
    static readonly bool ExtendSectionOn = Environment.GetEnvironmentVariable("P3D_DREXTENDSECTION") != "0";
    /// <summary>k148e: граней-лент прежней правки, продлённых за конец по u.</summary>
    public static long SectionFacesExtended;

    /// <summary>
    /// k148e: ПРОДЛЕНИЕ ЛЕНТЫ ПРЕЖНЕЙ ПРАВКИ ПОД НОВЫЙ КОНТУР — как у граней NURBS (k144): лента прежней фаски или скругления
    /// (процедурная SectionSurface) — торец новой ленты, и контур её грани уходит за конец ленты по u (кромка прежней фаски
    /// продлена до точки касания нового скругления). Поверхность грани заменяется той же лентой на большем промежутке
    /// (SectionSurface.ExtendedRange) с запасом 5 %; сверка внутри прежнего промежутка — до 1e-8 мм. Мутация P3D_DREXTENDSECTION=0.
    /// </summary>
    static void ExtendSectionFaces(List<FDesc> faces)
    {
        foreach (var fd in faces)
        {
            if (!fd.Touched || fd.Surf is not SectionSurface ss || !ss.IsProcedural || ss.Closed) continue;
            double u0 = ss.UMin, u1 = ss.UMax, lo = u0, hi = u1; bool bad = false;
            foreach (var sg in fd.Loops.SelectMany(l => l))
            {
                for (int i = 0; i <= 16 && !bad; i++)
                {
                    var q = sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * i / 16.0);
                    if (!double.IsFinite(q.X + q.Y + q.Z)) { bad = true; break; }
                    var (P, _) = Geometry.Ssi.ParamFoot(ss, q, out var uv);
                    if (!double.IsFinite(uv.X) || (P - q).Length > 1e-6) continue;
                    lo = Math.Min(lo, uv.X); hi = Math.Max(hi, uv.X);
                }
                if (bad) break;
            }
            double span = u1 - u0;
            if (bad || (lo > u0 - 1e-9 * span && hi < u1 + 1e-9 * span)) continue;
            double m = 0.05 * (hi - lo);
            double a = lo < u0 - 1e-9 * span ? lo - m : u0, b = hi > u1 + 1e-9 * span ? hi + m : u1;
            if (ss.ExtendedRange(a, b) is not { } ext) { Debug?.Invoke($"open: section face {fd.Tag}: extension not supported (arc sections)"); continue; }
            double dev = 0;
            for (int i = 0; i <= 16; i++) for (int j = 0; j <= 4; j++)
            {
                var uv = new Vector2d(u0 + span * i / 16.0, j / 4.0);
                dev = Math.Max(dev, (ext.PointAt(uv) - ss.PointAt(uv)).Length);
            }
            Debug?.Invoke($"open: section face {fd.Tag} extended u [{a:G6}, {b:G6}] (was [{u0:G6}, {u1:G6}]), deviation inside {dev:E1}");
            if (dev > 1e-8) continue;
            fd.Surf = ext; Interlocked.Increment(ref SectionFacesExtended);
        }
    }
    static readonly bool MarginClamp = Environment.GetEnvironmentVariable("P3D_DRMARGINCLAMP") != "0";
    static readonly bool OpenPar = Environment.GetEnvironmentVariable("P3D_DROPENPAR") != "0";
    static readonly bool FitDbg = Environment.GetEnvironmentVariable("P3D_FITDBG") == "1";
    static readonly bool Fit5Off = Environment.GetEnvironmentVariable("P3D_DRFIT5") == "0";
    static readonly bool FitParOff = Environment.GetEnvironmentVariable("P3D_DRFITPAR") == "0";
    /// <summary>Сколько раз точки вставки кривых ленты считались в несколько потоков.</summary>
    public static long FitParallelRuns;
    /// <summary>Точных точек, взятых вставками кривых ленты; вычислений функции края на торцах при поиске корней.</summary>
    public static long FitEvals, EndRootEvals;
    static readonly bool StarCont = Environment.GetEnvironmentVariable("P3D_DRSTARCONT") != "0";
    /// <summary>Корни края ленты, найденные полным поиском (продолжение не нашло скобку).</summary>
    public static long StarContFallbacks;
    /// <summary>Сколько раз пробы открытого построителя считались в несколько потоков.</summary>
    public static long OpenParallelRuns;
    static readonly bool FitCurves = Environment.GetEnvironmentVariable("P3D_DRFITCURVES") != "0";
    const double FitTol = 1e-9;
    /// <summary>21.10: идёт быстрый предпросмотр операции (живой предпросмотр в контексте). P3D_FASTPREVIEW=0 — нет.</summary>
    static bool FastPreviewNow => FastPreviewOn && KernelContext.Current?.Settings.LivePreview == true;
    static readonly bool FastPreviewOn = Environment.GetEnvironmentVariable("P3D_FASTPREVIEW") != "0";

    /// <summary>
    /// Кубическая вставка Эрмита по точкам f на [a, b] (производные — разностью 4-го порядка по соседним узлам): число
    /// промежутков удваивается (32 … 4096; точки прежнего шага — узлы следующего), пока отход в серединах всех промежутков
    /// не станет ≤ tol. Не сошлось или точка не найдена (NaN) — null (вызывающий оставляет точную функцию).
    /// </summary>
    static Func<double, Vector3d>? FitProc(Func<double, Vector3d> f, double a, double b, double tol)
    {
        if (!(b > a)) return null;
        var swFit = System.Diagnostics.Stopwatch.StartNew();
        int n = Fit5Off ? 32 : 16;
        var pts = new Vector3d[n + 1];
        // 21.10 (скорость): точки кривой — в несколько потоков (каждая — функция своего параметра: хребет, касание, край на
        // торце; память — потокобезопасная); вставка та же. Мутация P3D_DRFITPAR=0.
        void Fill(Vector3d[] dst, int count, Func<int, double> at)
        {
            int thr = FitParOff ? 1 : Math.Min(Tess.Tessellator.Threads, Environment.ProcessorCount);
            if (thr > 1 && count >= 16) { System.Threading.Tasks.Parallel.For(0, count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = thr }, i => dst[i] = f(at(i))); Interlocked.Increment(ref FitParallelRuns); }
            else for (int i = 0; i < count; i++) dst[i] = f(at(i));
        }
        Fill(pts, n + 1, i => a + (b - a) * i / n);
        Interlocked.Add(ref FitEvals, n + 1);
        while (true)
        {
            if (pts.Any(p => !double.IsFinite(p.X + p.Y + p.Z))) return null;
            var mids = new Vector3d[n];
            int nn = n; Fill(mids, n, i => a + (b - a) * (i + 0.5) / nn);
            Interlocked.Add(ref FitEvals, n);
            if (mids.Any(p => !double.IsFinite(p.X + p.Y + p.Z))) return null;
            var interp = !Fit5Off && n >= 8 ? Hermite5(pts, a, b) : Hermite(pts, a, b);
            double worst = 0;
            for (int i = 0; i < n; i++) worst = Math.Max(worst, (interp(a + (b - a) * (i + 0.5) / n) - mids[i]).Length);
            if (worst <= tol) { if (FitDbg) Console.Error.WriteLine($"FITPROC n {n} worst {worst:E2} {swFit.Elapsed.TotalMilliseconds:0.0} ms"); return interp; }
            if (n >= 4096) { if (FitDbg) Console.Error.WriteLine($"FITPROC failed n {n} worst {worst:E2}"); return null; }
            var np = new Vector3d[2 * n + 1];
            for (int i = 0; i <= n; i++) np[2 * i] = pts[i];
            for (int i = 0; i < n; i++) np[2 * i + 1] = mids[i];
            pts = np; n *= 2;
        }
    }

    /// <summary>
    /// 21.10 (скорость): ВСТАВКА ПЯТОЙ СТЕПЕНИ — Эрмит по точкам, первым и вторым производным (разности шестого порядка по
    /// соседним точкам, у краёв — односторонние): отход ∝ h⁶ против h⁴ у кубической — линия касания с отходом 1e-9 мм — по
    /// ~100 точкам вместо 1024 (каждая точка — хребет катящегося шара). Отход сверяется в серединах всех промежутков, как прежде.
    /// Мутация P3D_DRFIT5=0.
    /// </summary>
    static Func<double, Vector3d> Hermite5(Vector3d[] p, double a, double b)
    {
        int n = p.Length - 1; double h = (b - a) / n;
        var d1 = new Vector3d[n + 1]; var d2 = new Vector3d[n + 1];
        for (int i = 0; i <= n; i++)
        {
            int j = Math.Clamp(i - 3, 0, n - 6);            // 7 точек j..j+6, i — внутри окна
            double x = i - j;                                // положение i в окне (0..6)
            // производные многочлена Лагранжа по 7 точкам в узле x (весовые коэффициенты считаются напрямую)
            Vector3d D1 = Vector3d.Zero, D2 = Vector3d.Zero;
            for (int m = 0; m <= 6; m++)
            {
                // L_m(x) = Π_{q≠m} (x−q)/(m−q); L'_m и L''_m — по правилу произведения
                double den = 1; for (int q = 0; q <= 6; q++) if (q != m) den *= m - q;
                double s1 = 0, s2 = 0;
                for (int q = 0; q <= 6; q++)
                {
                    if (q == m) continue;
                    double pr = 1; for (int r = 0; r <= 6; r++) if (r != m && r != q) pr *= x - r;
                    s1 += pr;
                    for (int r = 0; r <= 6; r++)
                    {
                        if (r == m || r == q) continue;
                        double pr2 = 1; for (int t = 0; t <= 6; t++) if (t != m && t != q && t != r) pr2 *= x - t;
                        s2 += pr2;
                    }
                }
                D1 += p[j + m] * (s1 / den); D2 += p[j + m] * (s2 / den);
            }
            d1[i] = D1 / h; d2[i] = D2 / (h * h);
        }
        return t =>
        {
            double x = (Math.Clamp(t, a, b) - a) / h; int i = Math.Min((int)Math.Floor(x), n - 1); double u = x - i;
            if (u <= 0) return p[i];
            if (u >= 1) return p[i + 1];
            double u2 = u * u, u3 = u2 * u, u4 = u3 * u, u5 = u4 * u;
            double h0 = 1 - 10 * u3 + 15 * u4 - 6 * u5, h1 = u - 6 * u3 + 8 * u4 - 3 * u5, h2 = 0.5 * u2 - 1.5 * u3 + 1.5 * u4 - 0.5 * u5;
            double g0 = 10 * u3 - 15 * u4 + 6 * u5, g1 = -4 * u3 + 7 * u4 - 3 * u5, g2 = 0.5 * u3 - u4 + 0.5 * u5;
            return p[i] * h0 + d1[i] * (h * h1) + d2[i] * (h * h * h2) + p[i + 1] * g0 + d1[i + 1] * (h * g1) + d2[i + 1] * (h * h * g2);
        };
    }

    static Func<double, Vector3d> Hermite(Vector3d[] p, double a, double b)
    {
        int n = p.Length - 1; double h = (b - a) / n;
        var d = new Vector3d[n + 1];
        for (int i = 0; i <= n; i++)
        {
            if (i >= 2 && i <= n - 2) d[i] = (p[i - 2] - p[i - 1] * 8 + p[i + 1] * 8 - p[i + 2]) / (12 * h);
            else if (i < 2) d[i] = (p[i] * (-25) + p[i + 1] * 48 - p[i + 2] * 36 + p[i + 3] * 16 - p[i + 4] * 3) / (12 * h);
            else d[i] = (p[i] * 25 - p[i - 1] * 48 + p[i - 2] * 36 - p[i - 3] * 16 + p[i - 4] * 3) / (12 * h);
        }
        return t =>
        {
            double x = (Math.Clamp(t, a, b) - a) / h; int i = Math.Min((int)Math.Floor(x), n - 1); double u = x - i;
            if (u <= 0) return p[i];
            if (u >= 1) return p[i + 1];
            double u2 = u * u, u3 = u2 * u;
            return p[i] * (2 * u3 - 3 * u2 + 1) + d[i] * (h * (u3 - 2 * u2 + u)) + p[i + 1] * (-2 * u3 + 3 * u2) + d[i + 1] * (h * (u3 - u2));
        };
    }
    static TSolid? FinishDirectCore(List<FDesc> all, string op, ref string? why, System.Diagnostics.Stopwatch swF)
    {
        void Stage(string n) { Debug?.Invoke($"finish: {n} {swF.Elapsed.TotalSeconds:F1}s"); }
        var solid = Assemble(all, ref why);
        Stage("assembled");
        if (solid == null) return null;
        TopologyConventions.Normalize(solid);
        var w = new List<string>();
        if (Analytic.PrismBuild.Finish(solid, w, KeepDonor(all, BooleanOp.DonorFrom(all.Where(d => d.Orig != null).Select(d => d.Orig!)))) > 0)
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
            // k152: ДОПУСК НОВОГО РЕБРА ЛЕНТЫ (как допуски рёбер у Parasolid). Если вся причина отказа — p-кривая новой кривой ленты
            // (ProcCurve3) на грани, отходящая от ребра не больше предельного допуска сущности (1e-5 мм), — у ребра допуск = отход
            // с запасом, и проверка повторяется. s102: край фаски на торце пересекает стыки таблицы у края области боковины,
            // p-кривая (проекция с вставкой) отходит на 6.8e-7 мм при допуске 1e-7. Мутация P3D_DRTOLEDGE=0.
            if (!v.IsValid && TolEdge)
            {
                var errs = v.Issues.Where(x => x.Severity == ValidationSeverity.Error).ToList();
                if (errs.All(x => x.Code == "pcurve-inconsistent"))
                {
                    var bad = new Dictionary<TEdge, double>();
                    foreach (var (ce, pc, dev) in PCurves.Measure(solid))
                        if (pc != null && dev > Math.Max(ToleranceContext.Current.ModelAccuracy, ce.Edge.Tolerance))
                            bad[ce.Edge] = Math.Max(bad.GetValueOrDefault(ce.Edge), dev);
                    double cap = ToleranceContext.Current.MaxEntityTolerance;
                    if (bad.Count > 0 && bad.All(kv => kv.Key.Curve is ProcCurve3 && kv.Value * 2 <= cap))
                    {
                        solid.BeginEdit("blend edge tolerance", EditKind.Layout);
                        foreach (var (ed, dev) in bad) { ed.Tolerance = Math.Max(ed.Tolerance, 2 * dev); Debug?.Invoke($"open: edge e{ed.Id} tolerance {ed.Tolerance:E2} (p-curve deviation {dev:E2})"); }
                        Interlocked.Increment(ref TolerantEdges);
                        v = SolidValidator.Validate(solid);
                    }
                }
            }
            if (!v.IsValid && Debug != null)
                foreach (var iss in v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(20))
                {
                    var ed = solid.Edges.FirstOrDefault(x => iss.ToString().Contains($"(элемент {x.Id})"));
                    Debug($"  invalid: {iss} | edge tol {ed?.Tolerance:E2} curve {ed?.Curve.GetType().Name} {ed?.V0.P:0.###}->{ed?.V1.P:0.###}");
                }
            Stage("validated");
            if (!v.IsValid && Debug != null) foreach (var iss in v.Issues.Where(x => x.Severity == ValidationSeverity.Error)) Debug("invalid: " + iss);
            if (!v.IsValid && Debug != null && Environment.GetEnvironmentVariable("P3D_DROPENDBG") == "1")
            {
                var md = Tess.Tessellator.Tessellate(solid);
                var cnt = new Dictionary<((float, float, float), (float, float, float)), List<uint>>();
                for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var a = md.Vertices[md.Indices[i + k]]; var b = md.Vertices[md.Indices[i + (k + 1) % 3]];
                        var ka = (a.P.X, a.P.Y, a.P.Z); var kb = (b.P.X, b.P.Y, b.P.Z);
                        var key = string.CompareOrdinal(ka.ToString(), kb.ToString()) < 0 ? (ka, kb) : (kb, ka);
                        if (!cnt.TryGetValue(key, out var l)) cnt[key] = l = new List<uint>();
                        l.Add(a.FaceId);
                    }
                var tagOf = solid.Faces.ToDictionary(f => (uint)f.Id, f => f.Tag);
                foreach (var kv in cnt.Where(kv => kv.Value.Count != 2).Take(40))
                    Debug($"open side {kv.Key.Item1} {kv.Key.Item2} faces {string.Join(",", kv.Value.Select(x => x + ":" + tagOf.GetValueOrDefault(x)))}");
            }
            if (!v.IsValid && Debug != null)
                foreach (var e in solid.Edges)
                    if (v.Issues.Any(x => x.Severity == ValidationSeverity.Error && x.Element == (uint)e.Id))
                        Debug($"open invalid edge e{e.Id} {e.Curve.GetType().Name}{(e.Curve is ProcCurve3 pk ? ":" + pk.Kind : "")} {e.V0.P:0.####}->{e.V1.P:0.####} faces {string.Join(" / ", e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Select(f => f.Tag + " " + GeometryAudit.AuthoritativeSurface(f)?.GetType().Name))}");
            if (!v.IsValid) return "result invalid: " + v.Summary() + " " + string.Join(" / ", v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(6));
            if (AnalyticVolume.Of(solid) == null)
            {
                if (Debug != null)
                    foreach (var f in solid.Faces)
                        if (AnalyticVolume.FaceVolumeAt(f, Vector3d.Zero) == null)
                            Debug($"  no volume: face {f.Tag} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}: " + string.Join(" | ", f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge.Curve.GetType().Name + (ce.Edge.Curve is ProcCurve3 pc ? ":" + pc.Kind : ""))));
                return "no analytic volume";
            }
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
            solid.LazyDerivedExact = (Exact.ExactAdapter.LazyDerived(() => Exact.ExactAdapter.DerivedOf(solid)), solid.Revision);
        return solid;
    }
}

public static partial class DirectRound
{
    static readonly bool CurvedChamfer = Environment.GetEnvironmentVariable("P3D_DRCURVCH") != "0";
    static string SurfName(ISurface3? s) => s is Surfaces.FacetedSurface fs ? "faceted/" + (fs.Source?.GetType().Name ?? "none") : s?.GetType().Name ?? "null";
    /// <summary>k146: стыки касательных цепочек фаски (сечение в вершине). P3D_DRCHJOINT=0 — отказ, как прежде.</summary>
    static readonly bool ChamferJointOn = Environment.GetEnvironmentVariable("P3D_DRCHJOINT") != "0";
    /// k147: концы общего параметра — по плоскости сечения через вершину. P3D_DRENDSNAP=0 — концы опорной кривой.
    static readonly bool EndSnap = Environment.GetEnvironmentVariable("P3D_DRENDSNAP") != "0";
    /// k147: допуск концов и звеньев общего параметра у неточного тела. P3D_DRTOLEND=0 — как прежде.
    static readonly bool TolEnds = Environment.GetEnvironmentVariable("P3D_DRTOLEND") != "0";
    static readonly bool StraightChamferOn = Environment.GetEnvironmentVariable("P3D_DRSTRAIGHTCH") != "0";
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
        // u12: одно прямое ребро между плоскостями (незамкнутое) — тем же построителем, лента — плоскость (OpenChamferPlane)
        bool closed = es.All(e => ReferenceEquals(e.V0, e.V1) || (e.V0.P - e.V1.P).Length < 1e-7);
        // k146: прямые рёбра незамкнутой фаски — тем же открытым построителем, что и у скругления (грани NURBS лофта, цепочки)
        if (es.Any(e => e.Curve is LineCurve3) && !(OpenCylOn && es.Count == 1) && !(StraightChamferOn && !closed)) { why = "straight edges are not in this family"; return null; }
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
    static readonly bool MarchExt = Environment.GetEnvironmentVariable("P3D_DRMARCH") != "0";
    /// <summary>Мутация P3D_DRNURBS=0: грани NURBS в построителе катящегося шара — отказ (как прежде).</summary>
    static readonly bool NurbsBlends = Environment.GetEnvironmentVariable("P3D_DRNURBS") != "0";

    /// <summary>Форма грани для катящегося шара: квадрика — точная неявная; NURBS — по основанию перпендикуляра (Ssi.ParamSurf).</summary>
    static Geometry.Ssi.Shape? BlendShapeOf(ISurface3? s) =>
        Geometry.ExactEdges.ShapeOf(s) ?? (NurbsBlends && s is NurbsSurface ? new Geometry.Ssi.ParamSurf(s) : null)
        ?? (SectionBlends && s is SectionSurface { IsProcedural: true } ? new Geometry.Ssi.ParamSurf(s) : null);
    /// <summary>k146: ленты прежних скруглений и фасок (процедурная SectionSurface) — гранями новой ленты (каскад). P3D_DRSECTION=0.</summary>
    static readonly bool SectionBlends = Environment.GetEnvironmentVariable("P3D_DRSECTION") != "0";

    /// <summary>
    /// Гладкая опорная кривая ребра для параметра хребта: многочлен Чебышёва по доле длины (интерполяция в узлах Чебышёва;
    /// степень растёт до невязки ≤ tol на 401 пробе). Параметр — миллиметры, t ∈ [0, Length]; вне — продолжение многочлена.
    /// </summary>
    static readonly bool TolEdge = Environment.GetEnvironmentVariable("P3D_DRTOLEDGE") != "0";
    /// <summary>k152: число лент, у которых новым рёбрам назначен допуск (P3D_DRTOLEDGE).</summary>
    public static long TolerantEdges;
    static readonly bool TanBreak = Environment.GetEnvironmentVariable("P3D_DRTANBREAK") != "0";
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
