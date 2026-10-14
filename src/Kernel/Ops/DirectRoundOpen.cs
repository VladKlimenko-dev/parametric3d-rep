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
    static TSolid? BuildOpen(TSolid body, IReadOnlyList<EdgeRef> refs, double r, ref string? why, Func<TEdge, TFace, double>? chDist = null)
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
        foreach (var e in sel)
            foreach (var p in new[] { e.V0.P, e.V1.P })
                if (sel.Count(x => x != e && ((x.V0.P - p).Length < 1e-7 || (x.V1.P - p).Length < 1e-7)) > 0) { why = "open curved: selected edges share a vertex"; return null; }
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;
        var blends = new List<FDesc>();
        foreach (var e in sel)
            if (!OpenBlend(body, e, byFace, blends, r, ref why, chDist)) return null;
        return FinishDirect(faces, blends, chDist != null ? "chamfer.direct" : "round.direct", ref why);
    }

    static bool OpenBlend(TSolid body, TEdge e, Dictionary<TFace, FDesc> byFace, List<FDesc> blends, double r, ref string? why, Func<TEdge, TFace, double>? chDist = null)
    {
        var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (fs.Count != 2) { why = "open curved: edge is not between two faces"; return false; }
        var FA = byFace[fs[0]]; var FB = byFace[fs[1]];
        var shA = Geometry.ExactEdges.ShapeOf(FA.Surf); var shB = Geometry.ExactEdges.ShapeOf(FB.Surf);
        if (shA == null || shB == null) { why = "open curved: a face is not a plane, cylinder, cone, sphere or torus"; return false; }
        if (shA is Geometry.Ssi.Plane && shB is Geometry.Ssi.Plane) { why = "open curved: plane/plane edge"; return false; }
        var E = e.Curve;
        bool fwd = (E.PointAt(E.T0) - e.V0.P).Length <= (E.PointAt(E.T0) - e.V1.P).Length;
        Vector3d P0 = fwd ? e.V0.P : e.V1.P, P1 = fwd ? e.V1.P : e.V0.P;    // начало и конец по параметру кривой

        // ОКОНЧАНИЯ: в каждой вершине — ровно три ребра (e, A∩E, B∩E), торец E — квадрика
        var allEdges = body.Edges.ToList();
        var ends = new (FDesc FE, Geometry.Ssi.Shape ShE, TEdge EA, TEdge EB, Vector3d V, double SE)[2];
        for (int k = 0; k < 2; k++)
        {
            var V = k == 0 ? P0 : P1;
            var at = allEdges.Where(x => x != e && ((x.V0.P - V).Length < 1e-7 || (x.V1.P - V).Length < 1e-7)).ToList();
            if (at.Count != 2) { why = $"open curved: end vertex of degree {at.Count + 1} (three faces expected)"; return false; }
            TFace? Other(TEdge x, TFace f) { var xf = x.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList(); return xf.Count == 2 && xf.Contains(f) ? xf.First(y => y != f) : null; }
            var eA = at.FirstOrDefault(x => Other(x, fs[0]) is TFace o && o != fs[1]);
            var eB = at.FirstOrDefault(x => x != eA && Other(x, fs[1]) is TFace o && o != fs[0]);
            if (eA == null || eB == null) { why = "open curved: end edges not found"; return false; }
            var fe = Other(eA, fs[0])!;
            if (Other(eB, fs[1]) != fe) { why = "open curved: the two end edges are on different faces"; return false; }
            if ((eA.V0.P - eA.V1.P).Length < 1e-7 || (eB.V0.P - eB.V1.P).Length < 1e-7) { why = "open curved: closed end edge"; return false; }
            var FE = byFace[fe]; var shE = Geometry.ExactEdges.ShapeOf(FE.Surf);
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
                if (double.IsNaN(q.X) || (E.PointAt(CurveParam.Closest(E, q)) - q).Length > 1e-6) ok = false;
            }
            if (!ok) continue;
            edgeAt = At; Delta = span; rad0 = r0; rad1 = r1;
            break;
        }
        if (edgeAt == null) { why = "open curved: the edge does not turn monotonically around an axis of its faces"; return false; }
        Vector3d Ed(double t) => edgeAt(t);
        Vector3d Td(double t) { double dl = 1e-5; return (edgeAt(t + dl) - edgeAt(t - dl)).Normalized(); }

        Vector3d? Spine(double t)
        {
            var P = Ed(t); if (double.IsNaN(P.X)) return null;
            var tg = Td(t);
            var nA = Geometry.Ssi.Implicit(shA, P).G * sA; var nB = Geometry.Ssi.Implicit(shB, P).G * sB; double c = Vector3d.Dot(nA, nB);
            var x = P + (nA + nB) * (sigma * r / (1 + c));
            for (int it = 0; it < 60; it++)
            {
                var (f1, gA) = Geometry.Ssi.Implicit(shA, x); var (f2, gB) = Geometry.Ssi.Implicit(shB, x);
                double f3 = Vector3d.Dot(x - P, tg);
                var dx = Solve3(new[] { gA, gB, tg }, new[] { offA - f1, offB - f2, -f3 });
                if (dx == null) return null;
                x += dx.Value;
                if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
            }
            double ea = Geometry.Ssi.Implicit(shA, x).F - offA, eb = Geometry.Ssi.Implicit(shB, x).F - offB;
            if (Math.Abs(ea) > 1e-9 * Math.Max(1, r) || Math.Abs(eb) > 1e-9 * Math.Max(1, r) || Math.Abs(Vector3d.Dot(x - P, tg)) > 1e-9 * Math.Max(1, r)) return null;
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
            if (ch) return SweepSection.Line(ChA(t), ChB(t));
            var x = S(t); var qa = Foot(shA, x); var qb = Foot(shB, x);
            var e1 = (qa - x).Normalized(); var v2 = (qb - x).Normalized();
            var e2 = v2 - e1 * Vector3d.Dot(e1, v2);
            double phi = Math.Atan2(e2.Length, Vector3d.Dot(e1, v2));
            return SweepSection.Arc(x, e1, e2.Normalized(), r, phi);
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
        double Fend(int k, Vector3d x) => ends[k].SE * Geometry.Ssi.Implicit(ends[k].ShE, x).F;
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
            double radk = k == 0 ? rad0 : rad1, step = 2 * r / radk, dk = k == 0 ? -1 : 1, tEnd = k == 0 ? 0 : Delta;
            if (!(Fend(k, Ed(tEnd + dk * 1e-4 * step)) > 0)) { why = "open curved: the edge does not leave the body through the end face"; return false; }
            double h = step; bool found = false;
            for (int it = 0; it < 8 && !found; it++, h *= 2) if (SectionSide(k, tEnd + dk * h, 1)) { outT[k] = tEnd + dk * h; found = true; }
            if (!found) { why = "open curved: the blend does not leave the end face (end face too oblique?)"; return false; }
            h = step; found = false;
            for (int it = 0; it < 8 && !found; it++, h *= 2) if (SectionSide(k, tEnd - dk * h, -1)) { inT[k] = tEnd - dk * h; found = true; }
            if (!found) { why = "open curved: no blend section inside the end face"; return false; }
        }
        if (!(inT[0] < inT[1])) { why = "open curved: the edge is too short for the radius"; return false; }
        // корень f_k(сечение(t)(v)) по t в скобке [inT, outT] (Иллинойс), одна смена знака
        double Root(int k, Func<double, double> g)
        {
            double a = inT[k], b = outT[k], fa = g(a), fb = g(b);
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
        Vector3d EndPt(int k, double v) { double t = TStar(k, v); return double.IsNaN(t) ? new Vector3d(double.NaN) : Gen(t).Point(v); }
        // одна смена знака в скобке у каждой пробы v
        for (int k = 0; k < 2; k++)
            foreach (double v in new[] { 0, 0.2, 0.4, 0.6, 0.8, 1.0 })
            {
                int changes = 0; double prev = Fend(k, Gen(inT[k]).Point(v));
                for (int i = 1; i <= 32; i++) { double f = Fend(k, Gen(inT[k] + (outT[k] - inT[k]) * i / 32.0).Point(v)); if (Math.Sign(f) != Math.Sign(prev)) changes++; prev = f; }
                if (changes != 1) { why = $"open curved: blend section crosses end face {changes} times"; return false; }
            }
        double tA0 = TStar(0, 0), tB0 = TStar(0, 1), tA1 = TStar(1, 0), tB1 = TStar(1, 1);
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
            var x = ch ? Ed(t) : Spine(t);
            if (x == null) { why = "open curved: ball centre not found (radius too large?)"; return false; }
            var P = Ed(t); Ps.Add(P);
            var qa = ch ? ChA(t) : Foot(shA, x.Value); var qb = ch ? ChB(t) : Foot(shB, x.Value);
            if (ch)
            {
                if (double.IsNaN(qa.X + qb.X) || Math.Abs((qa - P).Length - dA) > 1e-8 * Math.Max(1, dA) || Math.Abs((qb - P).Length - dB) > 1e-8 * Math.Max(1, dB) || (qa - qb).Length < 1e-6)
                { why = "open chamfer: chamfer point not found"; return false; }
            }
            else if (Math.Abs((qa - x.Value).Length - r) > 1e-8 * Math.Max(1, r) || Math.Abs((qb - x.Value).Length - r) > 1e-8 * Math.Max(1, r)) { why = "open curved: contact not at the ball radius"; return false; }
            var g = Gen(t);
            if (!ch && !(g.Phi > 1e-3 && g.Phi < Math.PI - 1e-3)) { why = "open curved: blend arc degenerate"; return false; }
            if (t >= 0 && t <= Delta) { widthA = Math.Max(widthA, (qa - P).Length); widthB = Math.Max(widthB, (qb - P).Length); }
            if (prevS != null && (Vector3d.Dot(x.Value - prevS.Value, prevT) <= 0 || Vector3d.Dot(qa - prevA!.Value, prevT) <= 0 || Vector3d.Dot(qb - prevB!.Value, prevT) <= 0))
            { why = "open curved: spine or contact line folds back"; return false; }
            prevS = x; prevA = qa; prevB = qb; prevT = Td(t);
            us.Add(t); secs.Add(g);
        }
        // зазор: прочие звенья граней A и B дальше полосы
        var keepA = new HashSet<TEdge> { e, ends[0].EA, ends[1].EA }; var keepB = new HashSet<TEdge> { e, ends[0].EB, ends[1].EB };
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
        var surf = new SectionSurface(U => Gen(tLo + U / kU), us.Select(t => (t - tLo) * kU).ToList(), secs, false, 0, Lp, null, Math.Max(Lp / 20000, Math.Min(0.25, 0.4 * Math.Min(widthA, widthB)) / 32));
        {
            double worstTab = 0, worstU = 0;
            for (int i = 0; i < 1000; i++)
            {
                double U = ((useLo - tLo) + (useHi - useLo) * (i + 0.5) / 1000) * kU;
                var ta = surf.SectionAt(U); var ex = surf.ExactSectionAt(U);
                foreach (double v in new[] { 0.0, 0.5, 1.0 }) { double d = (ta.Point(v) - ex.Point(v)).Length; if (d > worstTab) { worstTab = d; worstU = U; } }
            }
            Debug?.Invoke($"open: table vs formula {worstTab:E2} at t {tLo + worstU / kU:G6} (Δ {Delta:G6}) widths {widthA:G4}/{widthB:G4} Lp {Lp:G5}");
            if (worstTab > 1e-8) { why = $"open curved: blend surface table deviates from the formula ({worstTab:E1} mm)"; return false; }
        }

        // КРИВЫЕ: линии касания, края ленты на торцах
        var cA = new ProcCurve3(QA, tA0, tA1, false, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch ? null : (s, a, b) => NoEvents(true, s, a, b) };
        var cB = new ProcCurve3(QB, tB0, tB1, false, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch ? null : (s, a, b) => NoEvents(false, s, a, b) };
        var end0 = new ProcCurve3(v => EndPt(0, v), 0, 1, false, ch ? "chamfer end" : "rolling-ball end");
        var end1 = new ProcCurve3(v => EndPt(1, v), 0, 1, false, ch ? "chamfer end" : "rolling-ball end");
        Vector3d qa0 = QA(tA0), qa1 = QA(tA1), qb0 = QB(tB0), qb1 = QB(tB1);
        if ((end0.PointAt(0) - qa0).Length > 1e-9 || (end0.PointAt(1) - qb0).Length > 1e-9 || (end1.PointAt(0) - qa1).Length > 1e-9 || (end1.PointAt(1) - qb1).Length > 1e-9)
        { why = "open curved: end curves do not meet the contact lines"; return false; }
        for (int i = 0; i <= 16; i++)
            foreach (var ec in new[] { end0, end1 }) if (double.IsNaN(ec.PointAt(i / 16.0).X)) { why = "open curved: end curve not found"; return false; }

        // УКОРОЧЕНИЕ РЁБЕР A∩E и B∩E до точек касания (одна кривая на обе грани ребра)
        var trimmed = new Dictionary<TEdge, (ICurve3 C, Vector3d Old, Vector3d New)>();
        foreach (var (x, oldP, q) in new[] { (ends[0].EA, P0, qa0), (ends[0].EB, P0, qb0), (ends[1].EA, P1, qa1), (ends[1].EB, P1, qb1) })
        {
            var c = x.Curve; double tq = CurveParam.Closest(c, q);
            if ((c.PointAt(tq) - q).Length > 1e-7) { why = "open curved: contact end is not on the end edge"; return false; }
            bool startIsOld = (c.PointAt(c.T0) - oldP).Length < (c.PointAt(c.T1) - oldP).Length;
            double lo = startIsOld ? tq : c.T0, hi = startIsOld ? c.T1 : tq;
            if (!(Math.Abs(hi - lo) > 1e-9 * Math.Max(1, Math.Abs(c.T1 - c.T0))) || (startIsOld ? tq <= c.T0 : tq >= c.T1)) { why = "open curved: blend wider than the end edge"; return false; }
            var sub = Analytic.GeneralBoolean.SubCurve(c, lo, hi);
            if ((sub.PointAt(startIsOld ? sub.T0 : sub.T1) - q).Length > 1e-7) { why = "open curved: trimmed end edge misses the contact point"; return false; }
            trimmed[x] = (sub, oldP, q);
        }
        // вставка края ленты в торец: между звеньями A∩E и B∩E у прежней вершины
        foreach (var (k, endC, qa, qb) in new[] { (0, (ICurve3)end0, qa0, qb0), (1, end1, qa1, qb1) })
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
                    if (sg.Id != null && trimmed.TryGetValue(sg.Id, out var tr) && (sg.Orig != null || (sg.A - tr.Old).Length < 1e-7 || (sg.B - tr.Old).Length < 1e-7))
                    {
                        if ((sg.A - tr.Old).Length < 1e-7) sg.A = tr.New; else if ((sg.B - tr.Old).Length < 1e-7) sg.B = tr.New; else continue;
                        sg.Curve = tr.C; sg.Orig = null;
                    }
        // само ребро — линии касания
        foreach (var (F, cv, a0, a1) in new[] { (FA, (ICurve3)cA, qa0, qa1), (FB, cB, qb0, qb1) })
        {
            var sg = F.Loops.SelectMany(l => l).Where(x => x.Id == e).ToList();
            if (sg.Count != 1) { why = "open curved: edge segment not found"; return false; }
            bool same = (sg[0].A - P0).Length < 1e-7;
            sg[0].Curve = cv; sg[0].Orig = null;
            if (same) { sg[0].A = a0; sg[0].B = a1; } else { sg[0].A = a1; sg[0].B = a0; }
            F.Touched = true;
        }
        double tm = Delta / 2; var Em = Ed(tm); var qam = QA(tm); var qbm = QB(tm);
        FA.Hint = Foot(shA, qam + (qam - Em)); FB.Hint = Foot(shB, qbm + (qbm - Em));
        // лента
        var gm = Gen(tm); var mid = gm.Point(0.5);
        var want = ch ? (Geometry.Ssi.Implicit(shA, Em).G * sA + Geometry.Ssi.Implicit(shB, Em).G * sB) : (mid - gm.C).Normalized() * (-sigma);
        var nS = surf.NormalAt(new Vector2d((tm - tLo) * kU, 0.5));
        var bl = new FDesc { Surf = surf, Rev = Vector3d.Dot(nS, want) < 0, Tag = ch ? "chamfer:face" : "round:face", Touched = true, Hint = mid };
        bl.Loops.Add(new List<Seg>
        {
            new() { Curve = cA, A = qa0, B = qa1 },
            new() { Curve = end1, A = qa1, B = qb1 },
            new() { Curve = cB, A = qb1, B = qb0 },
            new() { Curve = end0, A = qb0, B = qa0 },
        });
        blends.Add(bl);
        Debug?.Invoke($"open: sigma {sigma} shapes {shA.GetType().Name}/{shB.GetType().Name} ends {ends[0].ShE.GetType().Name}/{ends[1].ShE.GetType().Name} t [{tLo:G5}, {tHi:G5}] contacts A [{tA0:G6}, {tA1:G6}] B [{tB0:G6}, {tB1:G6}] Δ {Delta:G6}");
        return true;
    }

    /// <summary>Сборка, нормализация, разметка, проверки, происхождение — общий конец прямых построителей.</summary>
    static TSolid? FinishDirect(List<FDesc> faces, List<FDesc> blends, string op, ref string? why)
    {
        foreach (var fd in faces.Where(x => x.Touched)) fd.Hint = InteriorPoint(fd);
        foreach (var fd in blends.Where(x => x.Surf.IsPlanar && Scan)) fd.Hint = ScanlinePoint(fd);
        var all = faces.Concat(blends).ToList();
        var solid = Assemble(all, ref why);
        if (solid == null) return null;
        TopologyConventions.Normalize(solid);
        var w = new List<string>();
        if (Analytic.PrismBuild.Finish(solid, w) > 0) { why = "faces not meshed: " + string.Join(" / ", w.Take(3)); return null; }
        solid.Guarantee = GuaranteeLevel.ExactAnalytic;
        string? Checks()
        {
            if (BooleanOp.ForceAcceptFailure) return "forced acceptance failure (test)";
            var v = SolidValidator.Validate(solid);
            if (!v.IsValid) return "result invalid: " + v.Summary() + " " + string.Join(" / ", v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2));
            if (AnalyticVolume.Of(solid) == null) return "no analytic volume";
            if (BooleanOp.VolumeVsFacets(solid) is { } vf && !vf.Agree) return $"volume {vf.Va:G8} vs facets {vf.Vf:G8}";
            return null;
        }
        if (BooleanOp.PreviewMode) solid.PendingAcceptance = Checks;
        else if (Checks() is { } cw) { why = cw; return null; }
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
