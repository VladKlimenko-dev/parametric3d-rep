using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация: P3D_DRTANLOOP=0 — касательные контуры из отрезков и дуг не строятся прямо (прежний отказ).</summary>
    static readonly bool TanLoops = Environment.GetEnvironmentVariable("P3D_DRTANLOOP") != "0";
    /// <summary>06.11: ОТКРЫТЫЕ касательные цепочки (концы — отрезки, торец — плоскость). Мутация P3D_DRTANOPEN=0 — отказ, как прежде.</summary>
    static readonly bool TanOpen = Environment.GetEnvironmentVariable("P3D_DRTANOPEN") != "0";
    public static long TanOpenChains;
    /// <summary>06.11: r = R у выпуклой дуги контура — лента над дугой — часть шара (фаска — конус до вершины). Мутация P3D_DRPOLEARC=0 — отказ.</summary>
    static readonly bool PoleArcs = Environment.GetEnvironmentVariable("P3D_DRPOLEARC") != "0";
    public static long PoleArcBands;
    /// <summary>06.11: НАКЛОННЫЕ СТЕНКИ (после уклона): плоскости и соосные конусы под одним углом к Fp. Мутация P3D_DRTANTILT=0 — отказ.</summary>
    static readonly bool TanTilt = Environment.GetEnvironmentVariable("P3D_DRTANTILT") != "0";
    public static long TiltedLoops;

    sealed class TPiece
    {
        public Seg S = null!; public FDesc W = null!; public ArcCurve3? Arc; public double SgnRad;
        public ICurve3 Q1C = null!, Q2C = null!;
        public bool Pole;
        public double Sb;                           // синус наклона стенки к Fp (нормаль стенки · n_p)                           // r = R выпуклой дуги: смещённая дуга на Fp — точка (лента — часть шара или конус до вершины)
        public Vector3d? QA1, QA2, QB1, QB2;        // контакты на свободном конце цепочки (иначе — Q1/Q2 в вершине)
        public ICurve3? EndA, EndB;                 // сечение ленты плоскостью торца на свободном конце (от Q1 к Q2)
    }
    sealed class TFree { public Vector3d V; public TPiece P = null!; public bool AtA; public FDesc We = null!; public Vector3d Q1, Q2; public ICurve3 Sec = null!; }

    /// <summary>
    /// KRN-06 (06.10/06.11): КАСАТЕЛЬНЫЙ КОНТУР — целый контур плоской грани Fp из отрезков (стенка — плоскость ⟂ Fp) и дуг
    /// (стенка — прямой круговой цилиндр с осью ⟂ Fp), соседние звенья касаются (контур выдавленного профиля после
    /// скругления вертикальных рёбер). Стенки перпендикулярны Fp, поэтому в каждой точке P контура центр шара
    /// C = P + σr(n_p + n_2), контакты Q1 = P + σr n_2 (на Fp: смещённый профиль — те же прямые и концентрические дуги радиуса
    /// R + σ r s) и Q2 = P + σr n_p (на стенке: тот же профиль, сдвинутый на σr по нормали Fp). Лента: над отрезком — цилиндр,
    /// над дугой — тор (ось — ось цилиндра стенки); фаска — плоскость и конус. Стык звеньев — четверть окружности (отрезок у
    /// фаски) в плоскости поперёк контура: общая кривая двух лент, без срезов и угловых патчей. Вертикальные рёбра стыка стенок
    /// укорачиваются на r. Открытые цепочки, наклонные стенки, разные размеры — не здесь (отказ, прежний путь).
    /// </summary>
    static bool TangentLoopBlends(List<TEdge> sel, Dictionary<TFace, FDesc> byFace, double r, bool ch, List<FDesc> blends, ref string? why)
    {
        if (_chamfer != null || _law != null) { why = "tangent loop: unequal chamfer or variable radius"; return false; }
        var faceSets = sel.Select(e => e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList()).ToList();
        if (faceSets.Any(s => s.Count != 2)) { why = "tangent loop: an edge is not between two faces"; return false; }
        var common = faceSets[0].Where(f => faceSets.All(s => s.Contains(f)) && byFace[f].Surf.IsPlanar).ToList();
        if (common.Count != 1) { why = "tangent loop: the edges do not share one planar face"; return false; }
        var Fp = byFace[common[0]];
        if (Fp.N.Length < 0.5) { why = "tangent loop: plane normal undefined"; return false; }
        var np = Fp.N.Normalized();
        var selSet = new HashSet<TEdge>(sel);
        var loops = Fp.Loops.Where(l => l.Any(s => s.Id != null && selSet.Contains(s.Id))).ToList();
        bool IsSel(Seg s) => s.Id != null && selSet.Contains(s.Id);
        // ОТКРЫТАЯ ЦЕПОЧКА: в контуре — одна непрерывная (по кругу) серия выбранных звеньев; звенья — от её начала
        var runs = new List<List<Seg>>(); var openRun = new List<bool>();
        foreach (var l in loops)
        {
            if (l.All(IsSel)) { runs.Add(l.ToList()); openRun.Add(false); continue; }
            if (!TanOpen) { why = "tangent loop: only whole loops of the face (open chains not in this family)"; return false; }
            int n0 = l.Count, st = -1;
            for (int k = 0; k < n0; k++) if (IsSel(l[k]) && !IsSel(l[(k - 1 + n0) % n0])) { if (st >= 0) { why = "tangent chain: several runs in one loop"; return false; } st = k; }
            var run = new List<Seg>(); for (int k = st; IsSel(l[k % n0]); k++) run.Add(l[k % n0]);
            runs.Add(run); openRun.Add(true);
        }
        if (runs.Sum(l => l.Count) != sel.Count) { why = "tangent loop: selected edges outside the face loops"; return false; }

        // звенья: стенка и её нормаль
        var pieces = new List<List<TPiece>>();
        foreach (var l in runs)
        {
            var pl = new List<TPiece>();
            foreach (var s in l)
            {
                var wf = s.Id!.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().First(f => byFace[f] != Fp);
                var W = byFace[wf]; var p = new TPiece { S = s, W = W };
                if (s.Curve is LineCurve3)
                {
                    if (!W.Surf.IsPlanar || W.N.Length < 0.5) { why = "tangent loop: a wall is not planar"; return false; }
                    p.Sb = Vector3d.Dot(W.N.Normalized(), np);
                    if (Math.Abs(p.Sb) > 1e-9 && !TanTilt) { why = "tangent loop: a wall is not perpendicular to the face"; return false; }
                }
                else if (s.Curve is ArcCurve3 a && !a.IsClosed)
                {
                    double sc = Math.Max(1, a.Radius);
                    if (W.IsCyl)
                    {
                        if (Math.Abs(Math.Abs(Vector3d.Dot(W.AxW, np)) - 1) > 1e-12) { why = "tangent loop: an arc's wall is not a cylinder along the face normal"; return false; }
                        var dO = a.Frame.Origin - W.AxO;
                        if ((dO - W.AxW * Vector3d.Dot(dO, W.AxW)).Length > 1e-9 * sc || Math.Abs(a.Radius - W.AxR) > 1e-9 * sc) { why = "tangent loop: an arc is not the cylinder's section"; return false; }
                    }
                    else if (TanTilt && W.Surf is RevolvedSurface { Base: LineCurve3 } cone)
                    {
                        // конус, соосный дуге (ось ∥ n_p через центр дуги), — стенка после уклона; дуга — его сечение
                        var cw = cone.Axis.W.Normalized(); var dO = a.Frame.Origin - cone.Axis.Origin;
                        if (Math.Abs(Math.Abs(Vector3d.Dot(cw, np)) - 1) > 1e-12 || (dO - cw * Vector3d.Dot(dO, cw)).Length > 1e-9 * sc) { why = "tangent loop: an arc's cone is not coaxial"; return false; }
                        for (int q = 0; q <= 4; q++) { var x = a.PointAt(a.T0 + (a.T1 - a.T0) * q / 4); if ((cone.PointAt(cone.ProjectToUV(x)) - x).Length > 1e-9 * sc) { why = "tangent loop: an arc is not the cone's section"; return false; } }
                    }
                    else { why = "tangent loop: an arc's wall is not a cylinder along the face normal"; return false; }
                    p.Arc = a;
                    var m = a.PointAt(0.5); var rad = Radial(a, np, m);
                    p.SgnRad = Vector3d.Dot(NormalOf(W, m), rad) > 0 ? 1 : -1;
                    p.Sb = Vector3d.Dot(NormalOf(W, m), np);
                }
                else { why = "tangent loop: an edge is neither a line nor an open arc"; return false; }
                pl.Add(p);
            }
            pieces.Add(pl);
        }
        static Vector3d Radial(ArcCurve3 a, Vector3d np, Vector3d x) { var d = x - a.Frame.Origin; d -= np * Vector3d.Dot(d, np); return d.Normalized(); }
        // n_2 — горизонтальная (в плоскости Fp) наружная нормаль стенки; у наклонной стенки нормаль = cos β·n_2 + sin β·n_p
        Vector3d N2(TPiece p, Vector3d x) => p.Arc is { } a ? Radial(a, np, x) * p.SgnRad : (p.W.N.Normalized() - np * p.Sb).Normalized();
        // НАКЛОН: у всех стенок один (касание на стыках); сечение поперёк контура — одно и то же. Шар: центр C = P + σr(n_p + k·n_2),
        // k = (1 − sin β)/cos β; контакт на Fp — P + σrk·n_2, на стенке — P + σr((1 − sin β)n_p − k sin β·n_2); у фаски — по d от
        // ребра вдоль каждой грани. При β = 0 — прежние формулы.
        var allP = pieces.SelectMany(x => x).ToList();
        double sb = allP[0].Sb;
        if (allP.Any(q => Math.Abs(q.Sb - sb) > 1e-9)) { why = "tangent loop: the walls are inclined differently"; return false; }
        if (Math.Abs(sb) <= 1e-12) sb = 0;
        if (Math.Abs(sb) > 0.95) { why = "tangent loop: the walls are too inclined"; return false; }
        double cb = Math.Sqrt(1 - sb * sb), kT = ch ? 1 : (1 - sb) / cb;
        if (sb != 0 && runs.Count != openRun.Count(o => !o)) { why = "tangent chain: inclined walls with an open chain"; return false; }
        if (sb != 0) Interlocked.Increment(ref TiltedLoops);
        TPiece PieceAt(Vector3d x) => allP.OrderBy(q => DistToCurve(q.S.Curve, x)).First();

        // сторона (выпуклость) — по геометрии: направление от ребра внутрь Fp — ±n_2; выпукло, если против n_2
        double? sigma = null;
        foreach (var p in pieces.SelectMany(x => x))
        {
            var m = p.S.Curve.PointAt((p.S.Curve.T0 + p.S.Curve.T1) / 2); var n2 = N2(p, m);
            var d = IntoPlanarFace(Fp, m, n2);
            if (d == null) { why = "tangent loop: convexity undecided"; return false; }
            double sg = Vector3d.Dot(d.Value, n2) < 0 ? -1 : 1;
            if (sigma == null) sigma = sg; else if (sigma != sg) { why = "tangent loop: convex and concave edges"; return false; }
        }
        double sgm = sigma!.Value;
        Vector3d Q1(TPiece p, Vector3d x) => x + N2(p, x) * (sgm * r * kT);
        Vector3d Q2(Vector3d x)
        {
            if (sb == 0) return x + np * (sgm * r);
            var h = N2(PieceAt(x), x);
            return ch ? x + (np * cb - h * sb) * (sgm * r) : x + (np * (1 - sb) - h * (kT * sb)) * (sgm * r);
        }
        Vector3d Cc(TPiece p, Vector3d x) => x + (np + N2(p, x) * kT) * (sgm * r);

        // стыки: касание и непрерывность нормали стенки
        static Vector3d Dir(Seg s, bool atEnd)
        {
            bool fw = (s.Curve.PointAt(s.Curve.T0) - s.A).Length <= (s.Curve.PointAt(s.Curve.T0) - s.B).Length;
            double t = atEnd == fw ? s.Curve.T1 : s.Curve.T0;
            var tg = s.Curve.TangentAt(t).Normalized();
            return fw ? tg : -tg;
        }
        var joints = new List<(Vector3d P, TPiece Before, TPiece After)>();
        for (int ri = 0; ri < pieces.Count; ri++)
            for (int k = 0; k < pieces[ri].Count - (openRun[ri] ? 1 : 0); k++)
            {
                var pl = pieces[ri];
                var a = pl[k]; var b = pl[(k + 1) % pl.Count];
                if ((a.S.B - b.S.A).Length > 1e-7) { why = "tangent loop: loop not chained"; return false; }
                var P = a.S.B;
                if (Vector3d.Dot(Dir(a.S, true), Dir(b.S, false)) < 1 - 1e-9) { why = "tangent loop: a joint is not tangent"; return false; }
                if ((N2(a, P) - N2(b, P)).Length > 1e-7) { why = "tangent loop: wall normals differ at a joint"; return false; }
                joints.Add((P, a, b));
            }

        // смещённые звенья
        foreach (var p in pieces.SelectMany(x => x))
        {
            if (p.Arc is { } a)
            {
                double rho = a.Radius + sgm * r * kT * p.SgnRad;
                // r = R (до 1e-9 отн.): смещённая дуга вырождается в центр дуги — полюс шара (у фаски — вершина конуса)
                if (PoleArcs && Math.Abs(rho) <= 1e-9 * Math.Max(1, a.Radius)) { rho = 0; p.Pole = true; }
                else if (!(rho > 1e-6 * Math.Max(1, a.Radius))) { why = "tangent loop: radius too large for an arc of the loop"; return false; }
                p.Q1C = new ArcCurve3(new Frame3(a.Frame.Origin, a.Frame.U, a.Frame.V), rho, a.A0, a.A1);
                double up2 = ch ? sgm * r * cb : sgm * r * (1 - sb), rho2 = ch ? a.Radius - sgm * r * sb * p.SgnRad : a.Radius - sgm * r * kT * sb * p.SgnRad;
                p.Q2C = new ArcCurve3(new Frame3(a.Frame.Origin + np * up2, a.Frame.U, a.Frame.V), rho2, a.A0, a.A1);
            }
            else
            {
                var l = (LineCurve3)p.S.Curve;
                p.Q1C = new LineCurve3(Q1(p, l.A), Q1(p, l.B)); p.Q2C = new LineCurve3(Q2(l.A), Q2(l.B));
            }
            foreach (var x in new[] { p.S.A, p.S.B })
            {
                double e1 = Math.Min((p.Q1C.PointAt(p.Q1C.T0) - Q1(p, x)).Length, (p.Q1C.PointAt(p.Q1C.T1) - Q1(p, x)).Length);
                double e2 = Math.Min((p.Q2C.PointAt(p.Q2C.T0) - Q2(x)).Length, (p.Q2C.PointAt(p.Q2C.T1) - Q2(x)).Length);
                if (e1 > 1e-7 || e2 > 1e-7) { why = "tangent loop: offset curve does not meet the contact points"; return false; }
            }
        }

        // СВОБОДНЫЕ КОНЦЫ открытой цепочки: конец — отрезок, торец We — плоскость (грань соседнего невыбранного звена Fp);
        // контакты — пересечения смещённых прямых с We, сечение цилиндра ленты плоскостью We — дуга (эллипс) от Q1 к Q2
        var frees = new List<TFree>();
        for (int ri = 0; ri < pieces.Count; ri++)
        {
            if (!openRun[ri]) continue;
            var l = loops[ri]; int n0 = l.Count;
            foreach (bool atA in new[] { true, false })
            {
                var p = atA ? pieces[ri][0] : pieces[ri][^1];
                if (p.Arc != null) { why = "tangent chain: an end of the chain is an arc"; return false; }
                int k = l.IndexOf(p.S);
                var nb = atA ? l[(k - 1 + n0) % n0] : l[(k + 1) % n0];
                var V = atA ? p.S.A : p.S.B;
                var weF = nb.Id?.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().FirstOrDefault(f => byFace[f] != Fp);
                if (weF == null || !byFace[weF].Surf.IsPlanar || byFace[weF].N.Length < 0.5) { why = "tangent chain: the end face is not planar"; return false; }
                var We = byFace[weF]; var ne = We.N.Normalized();
                var t = (p.S.B - p.S.A).Normalized(); double tn = Vector3d.Dot(t, ne);
                if (Math.Abs(tn) < 0.1) { why = "tangent chain: end face too oblique"; return false; }
                Vector3d Hit(Vector3d x0) => x0 + t * (Vector3d.Dot(V - x0, ne) / tn);
                var q1 = Hit(Q1(p, p.S.A)); var q2 = Hit(Q2(p.S.A));
                var C0 = Cc(p, p.S.A); var e1 = (Q1(p, p.S.A) - C0) / r; var e2 = Vector3d.Cross(t, e1); var d2 = (Q2(p.S.A) - C0) / r;
                double dth = Math.Atan2(Vector3d.Dot(d2, e2), Vector3d.Dot(d2, e1));
                ICurve3 sec = ch ? new LineCurve3(q1, q2) : Section(C0, t, e1, e2, r, V, ne, 0, dth);
                if ((sec.PointAt(sec.T0) - q1).Length > 1e-7 || (sec.PointAt(sec.T1) - q2).Length > 1e-7) { why = "tangent chain: end section does not meet the contacts"; return false; }
                // q1 — внутри соседнего звена Fp
                {
                    var a0 = nb.A; var b0 = nb.B; double len = (b0 - a0).Length;
                    if (nb.Curve is not LineCurve3) { why = "tangent chain: the neighbour at the end is not a line"; return false; }
                    double u = Vector3d.Dot(q1 - a0, b0 - a0) / (len * len), dist = (a0 + (b0 - a0) * u - q1).Length;
                    if (dist > 1e-7 * Math.Max(1, len) || u <= 1e-6 || u >= 1 - 1e-6) { why = $"tangent chain: radius too large for the end neighbour (end {(atA ? "A" : "B")} at {V:0.###}, neighbour {a0:0.###}→{b0:0.###}, u {u:G4}, off {dist:G3})"; return false; }
                }
                if (atA) { p.QA1 = q1; p.QA2 = q2; p.EndA = sec; } else { p.QB1 = q1; p.QB2 = q2; p.EndB = sec; }
                var l1 = p.Q1C; var l2 = p.Q2C;
                p.Q1C = new LineCurve3(p.QA1 ?? Q1(p, p.S.A), p.QB1 ?? Q1(p, p.S.B)); p.Q2C = new LineCurve3(p.QA2 ?? Q2(p.S.A), p.QB2 ?? Q2(p.S.B));
                frees.Add(new TFree { V = V, P = p, AtA = atA, We = We, Q1 = q1, Q2 = q2, Sec = sec });
            }
        }
        Vector3d CA1(TPiece p) => p.QA1 ?? Q1(p, p.S.A); Vector3d CB1(TPiece p) => p.QB1 ?? Q1(p, p.S.B);
        Vector3d CA2(TPiece p) => p.QA2 ?? Q2(p.S.A); Vector3d CB2(TPiece p) => p.QB2 ?? Q2(p.S.B);
        if (frees.Count > 0) Interlocked.Increment(ref TanOpenChains);

        // место: смещённый контур не ближе r к другим звеньям (узкие пазы), другие контуры Fp — не в полосе
        var all = pieces.SelectMany(x => x).ToList();
        foreach (var p in all)
            for (int k = 1; k < 16; k++)
            {
                var x = p.Q1C.PointAt(p.Q1C.T0 + (p.Q1C.T1 - p.Q1C.T0) * k / 16.0);
                foreach (var o in all) if (DistToCurve(o.S.Curve, x) < r * kT * (1 - 1e-6)) { why = "tangent loop: the offset loop runs into itself (radius too large)"; return false; }
            }
        var freeNb = new HashSet<Seg>();
        for (int ri = 0; ri < runs.Count; ri++)
            if (openRun[ri]) { var l = loops[ri]; int n0 = l.Count; freeNb.Add(l[(l.IndexOf(runs[ri][0]) - 1 + n0) % n0]); freeNb.Add(l[(l.IndexOf(runs[ri][^1]) + 1) % n0]); }
        foreach (var l in Fp.Loops)
            foreach (var s in l.Where(x => !IsSel(x) && !freeNb.Contains(x)))
                for (int k = 0; k < 32; k++)
                {
                    var x = s.Curve.PointAt(s.Curve.T0 + (s.Curve.T1 - s.Curve.T0) * k / 32.0);
                    foreach (var o in all) if (DistToCurve(o.S.Curve, x) <= r * Math.Max(1, kT) + 1e-7) { why = "tangent loop: another loop of the face lies in the blend strip"; return false; }
                }

        // правка Fp: контур → смещённый (то же направление)
        for (int ri = 0; ri < runs.Count; ri++)
        {
            var l = loops[ri]; var pl = pieces[ri]; int n0 = l.Count;
            if (openRun[ri])
            {
                int ia = (l.IndexOf(runs[ri][0]) - 1 + n0) % n0, ib = (l.IndexOf(runs[ri][^1]) + 1) % n0;
                var sa = Shorten(l[ia], null, CA1(pl[0])); var sbS = Shorten(l[ib], CB1(pl[^1]), null);
                if (sa == null || sbS == null) { why = "tangent chain: an end neighbour cannot be shortened"; return false; }
                l[ia] = sa; l[ib] = sbS;
            }
            foreach (var p in pl)
            {
                int k = l.IndexOf(p.S);
                if (p.Pole) { l.RemoveAt(k); continue; }       // соседние смещённые отрезки сходятся в центре дуги
                l[k] = new Seg { Curve = p.Q1C, A = CA1(p), B = CB1(p), Id = p.S.Id };
            }
            if (l.Count < 3) { why = "tangent loop: the offset loop collapses"; return false; }
        }
        Fp.Touched = true;

        // правка стенок: ребро → сдвинутое на σr по n_p; рёбра стыков вдоль n_p — укоротить на r
        var jointPts = joints.Select(j => j.P).ToList();
        var freePts = frees.Select(f => f.V).ToList();
        var wallHeight = new Dictionary<FDesc, double>();
        foreach (var p in all)
        {
            var W = p.W; bool done = false;
            foreach (var l in W.Loops)
                for (int k = 0; k < l.Count && !done; k++)
                    if (l[k].Id == p.S.Id && l[k].Orig != null)
                    {
                        Vector3d C2(Vector3d x) => (x - p.S.A).Length < 1e-7 ? CA2(p) : CB2(p);
                        l[k] = new Seg { Curve = p.Q2C, A = C2(l[k].A), B = C2(l[k].B), Id = p.S.Id };
                        done = true;
                    }
            if (!done) { why = "tangent loop: the edge is not in its wall's loops"; return false; }
            W.Touched = true;
        }
        foreach (var W in all.Select(p => p.W).Distinct())
            foreach (var l in W.Loops)
                for (int k = 0; k < l.Count; k++)
                {
                    var s = l[k]; if (s.Id == null || selSet.Contains(s.Id) || (s.Orig == null && !TanGroups)) continue;
                    int ia = jointPts.FindIndex(P => (P - s.A).Length < 1e-7), ib = jointPts.FindIndex(P => (P - s.B).Length < 1e-7);
                    int fa = freePts.FindIndex(P => (P - s.A).Length < 1e-7), fb = freePts.FindIndex(P => (P - s.B).Length < 1e-7);
                    if (ia < 0 && ib < 0 && (fa >= 0 || fb >= 0))
                    {
                        // ребро стенки и торца на свободном конце — укоротить до Q2 (точка лежит на нём)
                        if (fa >= 0 && fb >= 0) { why = "tangent chain: a wall edge joins two free ends"; return false; }
                        var fr = frees[fa >= 0 ? fa : fb]; if (fr.P.W != W) continue;
                        var Pf = fa >= 0 ? s.A : s.B; var farf = fa >= 0 ? s.B : s.A;
                        double lenf = (farf - Pf).Length, uf = Vector3d.Dot(fr.Q2 - Pf, farf - Pf) / (lenf * lenf);
                        if (s.Curve is not LineCurve3 || !(uf > 1e-9 && uf < 1 - 1e-9) || (Pf + (farf - Pf) * uf - fr.Q2).Length > 1e-7 * Math.Max(1, lenf)) { why = "tangent chain: the end edge of the wall does not carry the contact"; return false; }
                        var nsf = Shorten(s, fa >= 0 ? fr.Q2 : null, fb >= 0 ? fr.Q2 : null);
                        if (nsf == null) { why = "tangent chain: an end wall edge cannot be shortened"; return false; }
                        l[k] = nsf; continue;
                    }
                    if (ia < 0 && ib < 0) continue;
                    if (ia >= 0 && ib >= 0) { why = "tangent loop: a wall edge joins two joints"; return false; }
                    var P = ia >= 0 ? s.A : s.B;
                    var gdir = sb == 0 ? np : (np * cb - N2(PieceAt(P), P) * sb);
                    if (s.Curve is not LineCurve3 || Math.Abs(Math.Abs(Vector3d.Dot((s.B - s.A).Normalized(), gdir)) - 1) > 1e-9) { why = "tangent loop: a joint edge of the walls is not along the wall generator"; return false; } var far = ia >= 0 ? s.B : s.A; var nP = Q2(P);
                    double len = (far - P).Length, u = Vector3d.Dot(nP - P, far - P) / (len * len);
                    if (!(u > 1e-9 && u < 1 - 1e-9)) { why = "tangent loop: a wall is not taller than the blend"; return false; }
                    var ns = Shorten(s, ia >= 0 ? nP : null, ib >= 0 ? nP : null);
                    if (ns == null) { why = "tangent loop: a joint edge cannot be shortened"; return false; }
                    l[k] = ns;
                    wallHeight[W] = Math.Min(wallHeight.TryGetValue(W, out var h0) ? h0 : double.MaxValue, len);
                }
        // точка цилиндрической стенки — середина оставшейся полосы под сдвинутой дугой
        foreach (var p in all.Where(p => p.Arc != null))
        {
            if (!wallHeight.TryGetValue(p.W, out var h)) { why = "tangent loop: wall height unknown"; return false; }
            var q2m = p.Q2C.PointAt((p.Q2C.T0 + p.Q2C.T1) / 2); var pm = p.S.Curve.PointAt((p.S.Curve.T0 + p.S.Curve.T1) / 2);
            var u2 = (np * cb - N2(p, pm) * sb) * sgm; double dq = ch ? r : r * kT;
            p.W.Hint = q2m + u2 * ((h - dq) / 2);
        }

        // торцы свободных концов: в вершине V — укоротить звено Fp и ребро стенки, вставить сечение ленты
        foreach (var fr in frees)
        {
            bool done = false;
            foreach (var loop in fr.We.Loops)
                for (int k = 0; k < loop.Count && !done; k++)
                {
                    var s0 = loop[k];
                    if ((s0.B - fr.V).Length >= 1e-7) continue;
                    var nx = loop[(k + 1) % loop.Count];
                    // звено на Fp — то, на чьей прямой лежит Q1
                    bool sOnFp = DistToCurve(s0.Curve, fr.Q1) < 1e-7;
                    var qa = sOnFp ? fr.Q1 : fr.Q2; var qb = sOnFp ? fr.Q2 : fr.Q1;
                    var sNew = Shorten(s0, null, qa); var nNew = Shorten(nx, qb, null);
                    if (sNew == null || nNew == null) { why = "tangent chain: end face neighbours cannot be shortened"; return false; }
                    loop[k] = sNew; loop[(k + 1) % loop.Count] = nNew;
                    loop.Insert(k + 1, new Seg { Curve = fr.Sec, A = qa, B = qb });
                    fr.We.Touched = true; done = true;
                }
            if (!done) { why = "tangent chain: the end vertex is not in the end face"; return false; }
        }

        // ленты и стыки
        var jointCurve = new Dictionary<int, ICurve3>();
        ICurve3 Joint(TPiece p, Vector3d P)
        {
            int ji = jointPts.FindIndex(x => (x - P).Length < 1e-7);
            if (jointCurve.TryGetValue(ji, out var c)) return c;
            var q1 = Q1(p, P); var q2 = Q2(P); var C = Cc(p, P);
            if (ch) c = new LineCurve3(q1, q2);
            else
            {
                // дуга сечения шара от Q1 к Q2: угол π/2 − β (между нормалями граней)
                var e1 = (q1 - C) / r; var d2 = (q2 - C) / r; var e2 = (d2 - e1 * Vector3d.Dot(d2, e1)).Normalized();
                c = new ArcCurve3(new Frame3(C, e1, e2), r, 0, Math.Atan2(Vector3d.Dot(d2, e2), Vector3d.Dot(d2, e1)));
            }
            jointCurve[ji] = c; return c;
        }
        foreach (var p in all)
        {
            var A = p.S.A; var B = p.S.B;
            var jA = p.EndA ?? Joint(p, A); var jB = p.EndB ?? Joint(p, B);
            if ((jA.PointAt(jA.T0) - CA1(p)).Length > 1e-7 || (jA.PointAt(jA.T1) - CA2(p)).Length > 1e-7) { why = "tangent loop: joint arc does not meet the contacts"; return false; }
            var loopB = new List<Seg>
            {
                new() { Curve = p.Q1C, A = CB1(p), B = CA1(p) },
                new() { Curve = jA, A = CA1(p), B = CA2(p) },
                new() { Curve = p.Q2C, A = CA2(p), B = CB2(p) },
                new() { Curve = jB, A = CB2(p), B = CB1(p) },
            };
            if (p.Pole) { loopB.RemoveAt(0); Interlocked.Increment(ref PoleArcBands); }    // треугольник: полюс, две четверти, дуга стенки
            var xm = p.EndA != null || p.EndB != null ? ((CA1(p) + CB1(p)) / 2 - N2(p, CA1(p)) * (sgm * r)) : p.S.Curve.PointAt((p.S.Curve.T0 + p.S.Curve.T1) / 2);
            var hint = ch ? (Q1(p, xm) + Q2(xm)) / 2 : Cc(p, xm) - (np * (1 + sb) + N2(p, xm) * cb).Normalized() * (sgm * r);
            ISurface3 surf; bool rev;
            if (p.Arc is { } a)
            {
                var O = a.Frame.Origin; var U = Radial(a, np, A); var fr = Frame3.FromNormal(O, np);
                var Ct = O + np * (sgm * r); double rho = a.Radius + sgm * r * kT * p.SgnRad; var tube = Ct + U * rho;
                var qP = O + U * rho; var qC = p.Q2C.PointAt(p.Q2C.T0) is var q2a ? O + np * Vector3d.Dot(q2a - O, np) + U * (q2a - O - np * Vector3d.Dot(q2a - O, np)).Length : Ct;
                ICurve3 gen; Vector3d mid, want;
                if (ch) { gen = new LineCurve3(qP, qC); mid = (qP + qC) / 2; want = (np + U * p.SgnRad).Normalized(); }
                else
                {
                    var mf = new Frame3(tube, U, np);
                    double a0 = Math.Atan2(Vector3d.Dot(qP - tube, np), Vector3d.Dot(qP - tube, U));
                    double a1 = Math.Atan2(Vector3d.Dot(qC - tube, np), Vector3d.Dot(qC - tube, U));
                    double da = a1 - a0; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
                    gen = new ArcCurve3(mf, r, a0, a0 + da);
                    mid = gen.PointAt(0.5); want = (mid - tube).Normalized() * (-sgm);
                }
                if ((gen.PointAt(gen.T0) - qP).Length > 1e-9 * Math.Max(1, rho) || (gen.PointAt(gen.T1) - qC).Length > 1e-9 * Math.Max(1, rho)) { why = "tangent loop: torus generator does not meet the contacts"; return false; }
                var rs = new RevolvedSurface(gen, new Frame3(Ct, fr.U, fr.V), 0, 2 * Math.PI);
                surf = rs; rev = Vector3d.Dot(rs.NormalAt(rs.ProjectToUV(mid)), want) < 0;
            }
            else
            {
                var t = (B - A).Normalized(); var C0 = Cc(p, A);
                if (ch)
                {
                    var nc = Vector3d.Cross(t, Q2(A) - Q1(p, A)).Normalized(); if (Vector3d.Dot(nc, np + N2(p, A)) < 0) nc = -nc;
                    surf = new PlaneSurface(Frame3.FromNormal(Q1(p, A), nc)); rev = false;
                }
                else
                {
                    var e1 = (Q1(p, A) - C0) / r; var e2 = Vector3d.Cross(t, e1);
                    surf = new CylinderSurface(new Frame3(C0, e1, e2), r); rev = sgm > 0;
                }
            }
            var bl = new FDesc { Surf = surf, Rev = rev, Tag = ch ? "chamfer:face" : "round:face", Touched = true, Hint = hint };
            if (surf is PlaneSurface) bl.N = (surf.NormalAt(surf.ProjectToUV(hint)) * (rev ? -1 : 1)).Normalized();
            bl.Loops.Add(loopB);
            blends.Add(bl);
        }
        return true;
    }

    /// <summary>Расстояние от точки до отрезка или дуги (иначе — по 64 точкам кривой).</summary>
    static double DistToCurve(ICurve3 c, Vector3d x)
    {
        if (c is LineCurve3 l)
        {
            var ab = l.B - l.A; double u = ab.LengthSquared < 1e-30 ? 0 : Math.Clamp(Vector3d.Dot(x - l.A, ab) / ab.LengthSquared, 0, 1);
            return (l.A + ab * u - x).Length;
        }
        if (c is ArcCurve3 a)
        {
            double best = Math.Min((a.PointAt(a.T0) - x).Length, (a.PointAt(a.T1) - x).Length);
            var W = a.Frame.W.Normalized(); var d = x - a.Frame.Origin; var dp = d - W * Vector3d.Dot(d, W);
            if (dp.Length > 1e-12)
            {
                double th = Math.Atan2(Vector3d.Dot(dp, a.Frame.V.Normalized()), Vector3d.Dot(dp, a.Frame.U.Normalized()));
                double tau = (th - a.A0) / a.Sweep, per = 2 * Math.PI / Math.Abs(a.Sweep);
                while (tau < 0) tau += per; while (tau > per) tau -= per;
                if (tau <= 1 || a.IsClosed) best = Math.Min(best, (a.Frame.Origin + dp.Normalized() * a.Radius - x).Length);
            }
            return best;
        }
        double m = double.MaxValue;
        for (int k = 0; k <= 64; k++) m = Math.Min(m, (c.PointAt(c.T0 + (c.T1 - c.T0) * k / 64.0) - x).Length);
        return m;
    }
}
