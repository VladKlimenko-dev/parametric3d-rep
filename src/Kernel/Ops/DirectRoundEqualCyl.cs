using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_DREQUALCYL=0: стык цилиндров равного радиуса — отказ (как прежде).</summary>
    static readonly bool EqualCylOn = Environment.GetEnvironmentVariable("P3D_DREQUALCYL") != "0";
    public static long EqualCylBands;
    static readonly bool EqCylConicOff = Environment.GetEnvironmentVariable("P3D_EQCYLCONIC") == "0";

    /// <summary>
    /// KRN-06 (06.12): СТЫК ЦИЛИНДРОВ РАВНОГО РАДИУСА (тройник труб одного диаметра: оси перпендикулярны и пересекаются).
    /// Ребро стыка — две половины эллипса между «сёдлами» O ± R·c, где поверхности касаются (нормали совпадают), — ребро
    /// без обхода оси, и общий построитель его не брал. Скругление объединения (вогнутое) радиусом r: центр шара — на обоих
    /// цилиндрах, смещённых на ρ = R + r, т. е. снова на половине эллипса: C(θ) = O + ρ·e(θ) + s·ρ|cos θ|·b, e = cos θ·a +
    /// sin θ·c (a — ось основной трубы, b — отвода, c = a × b, s — сторона отвода). Касания: на отводе Q₂ = C − r·e, на
    /// основной Q₁ = C − r·n, n = sin θ·c + s|cos θ|·b; сечение — дуга радиуса r на угол φ = arccos(sin²θ), к сёдлам
    /// стягивается в точку (лента-«линза» между сёдлами). Второй орт сечения — по формуле без деления на cos θ. Лента — грань
    /// по формуле сечения, рёбра — Q₁, Q₂ (процедурные), сёдла остаются вершинами. null — не этот случай (причина в why).
    /// </summary>
    static TSolid? EqualCylTee(TSolid body, List<TEdge> sel, double r, ref string? why)
    {
        if (!EqualCylOn) { why = "equal cylinders: off"; return null; }
        var fs0 = sel[0].CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (fs0.Count != 2 || sel.Any(e => !e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().ToHashSet().SetEquals(fs0))) { why = "equal cylinders: edges are not between the same two faces"; return null; }
        var faces = Describe(body, out var byFace, ref why);
        if (faces == null) return null;
        var F1 = byFace[fs0[0]]; var F2 = byFace[fs0[1]];
        if (!F1.IsCyl || !F2.IsCyl) { why = "equal cylinders: not two cylinders"; return null; }
        double R = F1.AxR; if (Math.Abs(F2.AxR - R) > 1e-9 * Math.Max(1, R)) { why = "equal cylinders: radii differ"; return null; }
        var w1 = F1.AxW.Normalized(); var w2 = F2.AxW.Normalized();
        if (Math.Abs(Vector3d.Dot(w1, w2)) > 1e-12) { why = "equal cylinders: axes are not perpendicular"; return null; }
        // точка пересечения осей
        var n12 = Vector3d.Cross(w1, w2).Normalized(); var d0 = F2.AxO - F1.AxO;
        if (Math.Abs(Vector3d.Dot(d0, n12)) > 1e-9 * Math.Max(1, R)) { why = "equal cylinders: axes do not intersect"; return null; }
        // O = F1.AxO + w1·t1 = F2.AxO + w2·t2 (в плоскости осей)
        double t1 = Vector3d.Dot(d0, w1);                       // w1 ⟂ w2: проекция разности на w1
        var O = F1.AxO + w1 * t1;
        if (((O - F2.AxO) - w2 * Vector3d.Dot(O - F2.AxO, w2)).Length > 1e-9 * Math.Max(1, R)) { why = "equal cylinders: axes do not meet"; return null; }
        var tris = SolidTriangles(body);
        var res = new List<FDesc>();
        foreach (var e in sel)
        {
            // какая труба — отвод (b): ребро — половина эллипса; точки ребра по одну сторону оси основной трубы вдоль b
            var pm = e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2);
            Vector3d a = w1, b = w2; FDesc Fa = F1, Fb = F2;
            // сторона отвода s: знак (p − O)·b у середины ребра; если |…| мала — роли наоборот
            double sB = Vector3d.Dot(pm - O, b), sA = Vector3d.Dot(pm - O, a);
            if (Math.Abs(sB) < Math.Abs(sA)) { (a, b) = (b, a); (Fa, Fb) = (Fb, Fa); (sA, sB) = (sB, sA); }
            double s = Math.Sign(sB); if (s == 0) { why = "equal cylinders: edge on the axis plane"; return null; }
            var c = Vector3d.Cross(a, b).Normalized();
            Vector3d E(double th) => a * Math.Cos(th) + c * Math.Sin(th);
            double Th(Vector3d x) { var d = x - O; return Math.Atan2(Vector3d.Dot(d, c), Vector3d.Dot(d, a)); }
            // концы ребра — сёдла (sin θ = ±1, на обеих поверхностях касание)
            double thA = Th(e.V0.P), thB = Th(e.V1.P), thM = Th(pm);
            foreach (var v in new[] { e.V0.P, e.V1.P })
                if ((v - (O + c * (Vector3d.Dot(v - O, c) > 0 ? R : -R))).Length > 1e-7 * Math.Max(1, R)) { why = "equal cylinders: an edge end is not a saddle point"; return null; }
            // половина по cos θ: знак κ у середины; θ — непрерывно от thA через thM к thB
            double kap = Math.Sign(Math.Cos(thM)); if (kap == 0) { why = "equal cylinders: degenerate half"; return null; }
            double Unwrap(double t, double refT) { while (t - refT > Math.PI) t -= 2 * Math.PI; while (t - refT < -Math.PI) t += 2 * Math.PI; return t; }
            thA = Unwrap(thA, thM); thB = Unwrap(thB, thM);
            if (Math.Abs(Math.Abs(thB - thA) - Math.PI) > 1e-6) { why = "equal cylinders: the edge is not a half of the ellipse"; return null; }
            // точка ребра на эллипсе: O + R e + s R |cos| b
            for (int k = 1; k < 8; k++) { double th = thA + (thB - thA) * k / 8; var q = O + E(th) * R + b * (s * R * Math.Abs(Math.Cos(th))); double tq = FaceSplitClosest(e.Curve, q); if ((e.Curve.PointAt(tq) - q).Length > 1e-6 * Math.Max(1, R)) { why = "equal cylinders: the edge is not the expected half ellipse"; return null; } }
            // выпуклость: точка у ребра со стороны «между трубами» — в теле (объединение — вогнутое ребро)
            var nA = NormalOf(Fa, pm); var nB = NormalOf(Fb, pm); var dd = nB - nA;
            bool? ins = dd.Length < 1e-9 ? null : InsideMesh(tris, pm + (nA + nB).Normalized() * Math.Min(r, R) * 0.25);
            if (ins != false) { why = "equal cylinders: only the union (concave) junction is built"; return null; }
            double rho = R + r;
            Vector3d Cn(double th) => O + E(th) * rho + b * (s * rho * Math.Abs(Math.Cos(th)));
            Vector3d Nm(double th) => c * Math.Sin(th) + b * (s * Math.Abs(Math.Cos(th)));          // нормаль основной трубы у C
            Vector3d Q1(double th) => Cn(th) - Nm(th) * r;
            Vector3d Q2(double th) => Cn(th) - E(th) * r;
            Vector3d E2(double th)
            {
                double sn = Math.Sin(th), cs = Math.Cos(th), kc = Math.Sign(cs) == 0 ? kap : Math.Sign(cs);
                return -(a * kc + c * (sn * Math.Abs(cs)) - b * (s * sn * sn)) / Math.Sqrt(1 + sn * sn);
            }
            SweepSection Gen(double th)
            {
                double sn = Math.Sin(th); double phi = Math.Acos(Math.Clamp(sn * sn, -1, 1));
                return SweepSection.Arc(Cn(th), -Nm(th), E2(th), r, phi);
            }
            double u0 = Math.Min(thA, thB), u1 = Math.Max(thA, thB);
            var us = Enumerable.Range(0, 257).Select(i => u0 + (u1 - u0) * i / 256).ToList();
            var surf = new SectionSurface(Gen, us, us.Select(Gen).ToList(), false, u0, u1, null, (u1 - u0) / 32768);
            // формула и таблица — до 0.2 точности модели; конец сечения — касания
            double worst = 0, worstQ = 0;
            for (int i = 0; i < 400; i++)
            {
                double U = u0 + (u1 - u0) * (i + 0.5) / 400; var tb = surf.SectionAt(U); var ex = surf.ExactSectionAt(U);
                foreach (double v in new[] { 0.0, 0.5, 1.0 }) worst = Math.Max(worst, (tb.Point(v) - ex.Point(v)).Length);
                worstQ = Math.Max(worstQ, Math.Max((ex.Point(0) - Q1(U)).Length, (ex.Point(1) - Q2(U)).Length));
            }
            if (worst > Math.Max(1e-8, 0.2 * ToleranceContext.Current.ModelAccuracy) || worstQ > 1e-9 * Math.Max(1, R)) { why = $"equal cylinders: section table {worst:E1}, contacts {worstQ:E1}"; return null; }
            // линии касания — плоские эллипсы: при |cos θ| = κ cos θ Q₂ = O + R sin θ·c + cos θ·(R a + sρκ b),
            // Q₁ = O + R sin θ·c + cos θ·(ρ a + sRκ b) — точные коники (точный STEP); мутация P3D_EQCYLCONIC=0 — процедурные
            var cQ1 = new ProcCurve3(Q1, u0, u1, false, "contact"); var cQ2 = new ProcCurve3(Q2, u0, u1, false, "contact");
            if (!EqCylConicOff)
            {
                double L = Math.Sqrt(R * R + rho * rho);
                var W1 = (a * rho + b * (s * R * kap)) / L; var W2 = (a * R + b * (s * rho * kap)) / L;
                var e1 = new EllipseCurve3(new Frame3(O, W1, c), L, R, u0, u1); var e2 = new EllipseCurve3(new Frame3(O, W2, c), L, R, u0, u1);
                bool okC = true;
                for (int k = 0; k <= 16; k++)
                {
                    double U = u0 + (u1 - u0) * k / 16;
                    if ((e1.PointAt(k / 16.0) - Q1(U)).Length > 1e-9 * Math.Max(1, R) || (e2.PointAt(k / 16.0) - Q2(U)).Length > 1e-9 * Math.Max(1, R)) okC = false;
                }
                if (okC) { cQ1.ExactConic = e1; cQ2.ExactConic = e2; }
            }
            var A = cQ1.PointAt(cQ1.T0); var B = cQ1.PointAt(cQ1.T1);
            // грани труб: ребро → линия касания (концы — те же сёдла)
            foreach (var (F, cq) in new[] { (Fa, cQ1), (Fb, cQ2) })
            {
                bool done = false;
                foreach (var l in F.Loops)
                    for (int k = 0; k < l.Count && !done; k++)
                        if (l[k].Id == e)
                        {
                            var sA0 = l[k].A; var sB0 = l[k].B;
                            if ((sA0 - A).Length > 1e-7 && (sA0 - B).Length > 1e-7) { why = "equal cylinders: loop ends do not match"; return null; }
                            l[k] = new Seg { Curve = cq, A = sA0, B = sB0, Id = e };
                            done = true;
                        }
                if (!done) { why = "equal cylinders: the edge is not in its face loops"; return null; }
                F.Touched = true;
            }
            // лента
            double um = (u0 + u1) / 2; var mid = Gen(um).Point(0.5);
            var bl = new FDesc { Surf = surf, Tag = "round:face", Touched = true, Hint = mid };
            var want = (Cn(um) - mid).Normalized();                 // наружу (в пустоту) — к центру шара
            bl.Rev = Vector3d.Dot(surf.NormalAt(surf.ProjectToUV(mid)), want) < 0;
            bl.Loops.Add(new List<Seg> { new() { Curve = cQ1, A = A, B = B }, new() { Curve = cQ2, A = B, B = A } });
            res.Add(bl);
        }
        var solid = FinishDirect(faces, res, "round.direct", ref why);
        if (solid != null) Interlocked.Increment(ref EqualCylBands);
        return solid;
    }

    static double FaceSplitClosest(ICurve3 c, Vector3d q) => Analytic.FaceSplit.ClosestParam(c, q);
}
