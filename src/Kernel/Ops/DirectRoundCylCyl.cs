using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_DRCYLCYL=0: замкнутое ребро цилиндр/цилиндр — отказ, прежний построитель (протянутый резец).</summary>
    static readonly bool CylCyl = Environment.GetEnvironmentVariable("P3D_DRCYLCYL") != "0";
    /// <summary>Мутация P3D_DRCYLSIDE=0: выпуклость — по знаку (n1 × n2)·t, без пробы точки в теле (у замкнутой кривой обход
    /// задаёт знак произвольно).</summary>
    static readonly bool CylSide = Environment.GetEnvironmentVariable("P3D_DRCYLSIDE") != "0";
    /// <summary>Мутация P3D_DRCYLHALF=0: лента — один пояс без шва (прежде; у r ≥ 2 разметка отказывала).</summary>
    static readonly bool CylHalf = Environment.GetEnvironmentVariable("P3D_DRCYLHALF") != "0";
    /// <summary>Мутация P3D_DRCYLPARAM=0: параметр хребта — параметр кривой пересечения (со скачками скорости в точках затравки).</summary>
    static readonly bool CylParam = Environment.GetEnvironmentVariable("P3D_DRCYLPARAM") != "0";
    /// <summary>Мутация P3D_DRCYLTABLE=0: шаг таблицы формулы ленты — по умолчанию SectionSurface (0.25 мм, 1/20 ширины).</summary>
    static readonly bool CylTable = Environment.GetEnvironmentVariable("P3D_DRCYLTABLE") != "0";

    /// <summary>Число проб хребта для сверок (непрерывность, зазоры) и сечений-затравок поверхности.</summary>
    const int CylSamples = 256;

    /// <summary>Мутация P3D_DRQUADRIC=0: замкнутые рёбра — только цилиндр/цилиндр (без конусов, шаров, плоскостей, торов).</summary>
    static readonly bool QuadricBlendsOn = Environment.GetEnvironmentVariable("P3D_DRQUADRIC") != "0";
    static readonly bool SeamShift = Environment.GetEnvironmentVariable("P3D_DRSEAMSHIFT") != "0";

    /// <summary>Ось формы (цилиндр, конус, тор), вокруг которой может обходить ребро; null — у формы оси нет.</summary>
    static (Vector3d O, Vector3d W)? AxisOf(Geometry.Ssi.Shape s) => s switch
    {
        Geometry.Ssi.Cylinder c => (c.P, c.Axis.Normalized()),
        Geometry.Ssi.Cone k => (k.Apex, k.Axis.Normalized()),
        Geometry.Ssi.Torus t => (t.C, t.Axis.Normalized()),
        _ => null
    };

    /// <summary>
    /// KRN-06 (06.12, 06.14 частично): ЗАМКНУТОЕ РЕБРО МЕЖДУ ДВУМЯ ГРАНЯМИ-КВАДРИКАМИ (плоскость, цилиндр, конус, шар, тор):
    /// кромка поперечного отверстия в валу, стык бобышки с валом, кромка усечённого конуса, стык конуса с цилиндром и т. п.
    /// Ребро — отдельный контур каждой из двух граней и обходит один раз ось одной из них. Катящийся шар: центр (хребет)
    /// — точка, где знаковое расстояние до каждой поверхности (знак — по наружной нормали грани) равно σ·r (σ = −1 —
    /// выпуклое ребро), в плоскости поперёк ребра через его точку; уточнение Ньютоном при каждом обращении. Линии касания
    /// — основания перпендикуляров из хребта на поверхности, лента — дуга радиуса r между ними (поверхность по формуле
    /// сечения, SectionSurface), контуры граней — линии касания вместо ребра. Цилиндр/цилиндр — частный случай.
    /// </summary>
    static bool CylCylBlends(TSolid body, List<TEdge> loopsE, Dictionary<TFace, FDesc> byFace, List<FDesc> blends, double r, ref string? why, Func<TEdge, TFace, double>? chDist = null)
    {
        List<(Vector3d A, Vector3d B, Vector3d C)>? tris = null;
        foreach (var e in loopsE)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count != 2) { why = "closed blend: edge is not between two faces"; return false; }
            var FA = byFace[fs[0]]; var FB = byFace[fs[1]];
            if (!QuadricBlendsOn && (!FA.IsCyl || !FB.IsCyl)) { why = "cyl/cyl: a face of the closed edge is not a right circular cylinder"; return false; }
            var shA = BlendShapeOf(FA.Surf); var shB = BlendShapeOf(FB.Surf);
            if (shA == null || shB == null) { why = "closed blend: a face is not a plane, cylinder, cone, sphere or torus"; return false; }
            var E = e.Curve;
            // ребро — отдельный контур каждой из граней
            List<Seg>? LoopOf(FDesc F) => F.Loops.FirstOrDefault(l => l.Count == 1 && l[0].Id == e);
            var lA = LoopOf(FA); var lB = LoopOf(FB);
            if (lA == null || lB == null) { why = "closed blend: the edge is not a loop of its own"; return false; }
            double t0 = E.T0, t1 = E.T1;
            if ((E.PointAt(t0) - E.PointAt(t1)).Length > 1e-7) { why = "closed blend: edge curve is not closed"; return false; }

            // знак наружной стороны: s = +1 — наружная нормаль грани по градиенту знакового расстояния формы
            var Pm = E.PointAt((t0 + t1) / 2);
            double sA = Vector3d.Dot(NormalOf(FA, Pm), Geometry.Ssi.Implicit(shA, Pm).G) > 0 ? 1 : -1;
            double sB = Vector3d.Dot(NormalOf(FB, Pm), Geometry.Ssi.Implicit(shB, Pm).G) > 0 ? 1 : -1;
            double sizeMin = Math.Min(FA.IsCyl ? FA.AxR : 1e9, FB.IsCyl ? FB.AxR : 1e9);
            foreach (var sh in new[] { shA, shB })
                sizeMin = Math.Min(sizeMin, sh switch { Geometry.Ssi.Sphere sp => sp.R, Geometry.Ssi.Torus to => to.r, _ => 1e9 });

            // ВЫПУКЛОСТЬ — пробой точки в теле: x = p + ε(n_B − n_A) лежит вне полупространства грани B; у выпуклого ребра
            // (материал — пересечение) она снаружи, у вогнутого (объединение) — внутри. Три точки ребра должны согласиться.
            int? sigmaVote = null;
            foreach (double f in new[] { 0.13, 0.47, 0.81 })
            {
                double tt = t0 + (t1 - t0) * f; var p = E.PointAt(tt);
                var nA = NormalOf(FA, p); var nB = NormalOf(FB, p);
                int sg;
                if (CylSide)
                {
                    tris ??= SolidTriangles(body);
                    var d = nB - nA; if (d.Length < 1e-6) { why = "closed blend: tangent faces"; return false; }
                    double eps = Math.Min(r, 0.05 * Math.Min(sizeMin, 1e3)) / 2;
                    bool? ins = InsideMesh(tris, p + d.Normalized() * eps);
                    if (ins == null) { why = "closed blend: convexity probe undecided"; return false; }
                    sg = ins.Value ? 1 : -1;
                }
                else sg = Vector3d.Dot(Vector3d.Cross(nA, nB), E.TangentAt(tt)) > 0 ? -1 : 1;
                if (sigmaVote != null && sigmaVote != sg) { why = "closed blend: convexity changes along the edge"; return false; }
                sigmaVote = sg;
            }
            double sigma = sigmaVote!.Value;
            // знаковые расстояния хребта до форм (в знаке формы)
            double offA = sigma * sA * r, offB = sigma * sB * r;
            bool ch = chDist != null; double dA = ch ? chDist!(e, fs[0]) : r, dB = ch ? chDist!(e, fs[1]) : r;
            foreach (var (sh, off) in ch ? Array.Empty<(Geometry.Ssi.Shape, double)>() : new[] { (shA, offA), (shB, offB) })
                if (sh is Geometry.Ssi.Cylinder cy0 && cy0.R + off < 1e-6 || sh is Geometry.Ssi.Sphere sp0 && sp0.R + off < 1e-6 || sh is Geometry.Ssi.Torus to0 && to0.r + off < 1e-6)
                { why = "closed blend: radius too large for a face"; return false; }

            // ГЛАДКИЙ ПАРАМЕТР РЕБРА: угол вокруг оси формы, которую ребро обходит один раз. Параметр кривой пересечения —
            // доля длины по затравке, со скачками скорости в её точках, и таблица формулы ленты (SectionSurface, кубическая
            // между узлами) расходилась с самой формулой на 1e-4 мм. Точка ребра при угле φ — Ньютон на «на обеих
            // поверхностях и в полуплоскости угла φ через ось», затравка — из таблицы исходного ребра по углу (корень тот же).
            Func<double, Vector3d>? edgeAt = null;
            foreach (var shW in new[] { shB, shA })
            {
                if (AxisOf(shW) is not { } ax) continue;
                var W = ax.W; var helper = Math.Abs(W.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                var U = Vector3d.Cross(helper, W).Normalized(); var V = Vector3d.Cross(W, U); var O = ax.O;
                double Ang(Vector3d q) { var d = q - O; return Math.Atan2(Vector3d.Dot(d, V), Vector3d.Dot(d, U)); }
                const int m = 512;
                var smp = Enumerable.Range(0, m + 1).Select(i => E.PointAt(t0 + (t1 - t0) * i / m)).ToList();
                var ang = new double[m + 1]; ang[0] = Ang(smp[0]);
                int sgn = 0; bool mono = true;
                for (int i = 1; i <= m; i++)
                {
                    double dphi = Ang(smp[i]) - Ang(smp[i - 1]); while (dphi > Math.PI) dphi -= 2 * Math.PI; while (dphi < -Math.PI) dphi += 2 * Math.PI;
                    int si = Math.Sign(dphi); if (si == 0 || (sgn != 0 && si != sgn)) { mono = false; break; }
                    sgn = si; ang[i] = ang[i - 1] + dphi;
                }
                if (!mono || Math.Abs(Math.Abs(ang[m] - ang[0]) - 2 * Math.PI) > 1e-6) { Debug?.Invoke($"closed blend: axis {W:0.###} mono {mono} span {Math.Abs(ang[m] - ang[0]):R}"); continue; }
                // НАЧАЛО ПАРАМЕТРА СДВИНУТО на 0.5 рад от начала исходного ребра (P3D_DRSEAMSHIFT=0 — без сдвига): со швами ленты в
                // начале ребра разметка ленты у отверстия d30 в валу R20 давала по треугольнику обратной стороны в каждой половине
                // (проверка стороны отвергает такую сетку — грань не размечалась)
                double ph0 = ang[0] + (SeamShift ? sgn * 0.5 : 0); double dir = sgn;
                // затравка по углу: линейная вставка между пробами исходного ребра
                Vector3d Seed(double t)
                {
                    double tt = (ph0 - ang[0]) * dir + t; tt -= Math.Floor(tt / (2 * Math.PI)) * 2 * Math.PI;
                    double a = ang[0] + dir * tt;
                    int lo = 0, hi = m;
                    while (hi - lo > 1) { int mid = (lo + hi) / 2; if ((ang[mid] - a) * dir <= 0) lo = mid; else hi = mid; }
                    double den = ang[hi] - ang[lo]; double f = Math.Abs(den) < 1e-300 ? 0 : Math.Clamp((a - ang[lo]) / den, 0, 1);
                    return smp[lo] + (smp[hi] - smp[lo]) * f;
                }
                Vector3d At(double t)
                {
                    double phi = ph0 + dir * t;
                    var nphi = -U * Math.Sin(phi) + V * Math.Cos(phi); var uphi = U * Math.Cos(phi) + V * Math.Sin(phi);
                    var x = Seed(t);
                    for (int it = 0; it < 40; it++)
                    {
                        var (f1, g1) = Geometry.Ssi.Implicit(shA, x); var (f2, g2) = Geometry.Ssi.Implicit(shB, x);
                        double f3 = Vector3d.Dot(x - O, nphi);
                        var dx = Solve3(new[] { g1, g2, nphi }, new[] { -f1, -f2, -f3 });
                        if (dx == null) { Debug?.Invoke($"closed blend: At({t:R}) it {it} x {x:0.######} f1 {f1:E2} g1 {g1:0.###} f2 {f2:E2} g2 {g2:0.###}"); return new Vector3d(double.NaN); }
                        x += dx.Value;
                        if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
                    }
                    var (e1, _) = Geometry.Ssi.Implicit(shA, x); var (e2, _) = Geometry.Ssi.Implicit(shB, x);
                    if (!(Math.Abs(e1) <= 1e-10) || !(Math.Abs(e2) <= 1e-10) || !(Vector3d.Dot(x - O, uphi) >= 0)) { Debug?.Invoke($"closed blend: At({t:R}) seed {Seed(t):0.######} x {x:0.######} e1 {e1:E2} e2 {e2:E2} side {Vector3d.Dot(x - O, uphi):E2}"); return new Vector3d(double.NaN); }
                    return x;
                }
                // та же кривая: точки по углу лежат на исходном ребре
                bool ok = true;
                for (int i = 0; i < 64 && ok; i++)
                {
                    var q = At(2 * Math.PI * (i + 0.37) / 64);
                    if (double.IsNaN(q.X) || (E.PointAt(CurveParam.Closest(E, q)) - q).Length > 1e-6) { ok = false; Debug?.Invoke($"closed blend: axis {W:0.###} point {i} {q:0.######} off the edge by {(double.IsNaN(q.X) ? double.NaN : (E.PointAt(CurveParam.Closest(E, q)) - q).Length):E2}"); }
                }
                if (!ok) continue;
                edgeAt = At;
                break;
            }
            if (edgeAt == null) { why = "closed blend: the edge does not wind once around an axis of its faces"; return false; }
            if (CylParam) { t0 = 0; t1 = 2 * Math.PI; }
            var edMemo = new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d>();
            var spMemo = new System.Collections.Concurrent.ConcurrentDictionary<double, Vector3d?>();
            var edF = edgeAt;
            Vector3d Ed(double t) => !CylParam ? E.PointAt(t) : MemoOn && edMemo.Count < MemoCap ? edMemo.GetOrAdd(t, edF) : edF(t);
            Vector3d Td(double t) { if (!CylParam) return E.TangentAt(t); const double dl = 1e-5; return (edgeAt(t + dl) - edgeAt(t - dl)).Normalized(); }

            // хребет в точке ребра: Ньютон на «знаковое расстояние до A = offA, до B = offB, (x − P)·t = 0»
            Vector3d? Spine(double t) => MemoOn && spMemo.Count < MemoCap ? spMemo.GetOrAdd(t, SpineCore) : SpineCore(t);
            Vector3d? SpineCore(double t)
            {
                var P = Ed(t); var tg = Td(t);
                var nA = NormalOf(FA, P); var nB = NormalOf(FB, P); double c = Vector3d.Dot(nA, nB);
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
                {
                    double ea = Geometry.Ssi.Implicit(shA, x).F - offA, eb = Geometry.Ssi.Implicit(shB, x).F - offB;
                    if (Math.Abs(ea) > 1e-9 * Math.Max(1, r) || Math.Abs(eb) > 1e-9 * Math.Max(1, r) || Math.Abs(Vector3d.Dot(x - P, tg)) > 1e-9 * Math.Max(1, r)) return null;
                }
                return x;
            }
            // основание перпендикуляра на форму (знаковое расстояние вдоль единичного градиента)
            Vector3d Foot(FDesc F, Vector3d x) { var (fv, g) = Geometry.Ssi.Implicit(F == FA ? shA : shB, x); return x - g * fv; }
            // формулы для поверхности и линий касания (сбой уточнения в обращении — последняя годная точка не берётся:
            // все пробы ниже проверены заранее, и сбой вне них даёт NaN, что отвергнет сетка)
            Vector3d S(double t) => Spine(t) ?? new Vector3d(double.NaN);
            // 06.15: фаска — точки граней в плоскости поперёк ребра на хордовом расстоянии d от точки ребра
            Vector3d ChA(double t) => ChamferPoint(shA, shB, sA, sB, sigma, Ed(t), Td(t), dA);
            Vector3d ChB(double t) => ChamferPoint(shB, shA, sB, sA, sigma, Ed(t), Td(t), dB);
            Vector3d QA(double t) => ch ? ChA(t) : Foot(FA, S(t));
            Vector3d QB(double t) => ch ? ChB(t) : Foot(FB, S(t));
            // 04.2: ЛИНИЯ КАСАНИЯ НЕ ЗАДЕВАЕТ ПОВЕРХНОСТЬ s — строго. Хребет — пересечение уровней F_A = offA, F_B = offB;
            // звено хребта между соседними точками лежит в шаре, где ветвь одна (Ssi.Transversal для полей градиентов F_A,
            // F_B — те же нормали уровней): хребет на звене — в шаре (m, ρ). Точка касания Q = x − offA·∇F_A(x) отходит от
            // Q(m) не больше ρ(1 + |offA|·κ_A) (κ_A — поворот ∇F_A по шару, NormalLip). |F_s(Q(m))| больше этого — на звене
            // событий нет; иначе звено делится (до 1e-6 мм — не доказано).
            bool NoEvents(bool onA, Geometry.Ssi.Shape s, double ta, double tb)
            {
                var sh = onA ? shA : shB; double off = onA ? offA : offB;
                bool Seg(double u0, double u1, Vector3d x0, Vector3d x1, int depth)
                {
                    var m = (x0 + x1) / 2; double rho = (x1 - x0).Length / 2 * 1.01 + 1e-12;
                    bool tr = Geometry.Ssi.Transversal(shA, shB, m, rho);
                    if (tr && Geometry.Ssi.NormalLip(sh, m, rho) is double kap)
                    {
                        var (fm, gm) = Geometry.Ssi.Implicit(sh, m);
                        var qm = m - gm * off; double R = rho * (1 + Math.Abs(off) * kap) + Math.Abs(fm - off);
                        if (Math.Abs(Geometry.Ssi.Implicit(s, qm).F) > R * (1 + 1e-12) + 1e-12) return true;
                    }
                    if (rho < 1e-6 || depth > 30) return false;
                    double um = (u0 + u1) / 2; var xm = Spine(um); if (xm == null) return false;
                    if (tr && (xm.Value - m).Length > rho) return false;                 // точка хребта вне шара звена
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
            SweepSection Gen(double t)
            {
                if (ch) return SweepSection.Line(ChA(t), ChB(t));
                var x = S(t); var qa = Foot(FA, x); var qb = Foot(FB, x);
                var e1 = (qa - x).Normalized(); var v2 = (qb - x).Normalized();
                var e2 = v2 - e1 * Vector3d.Dot(e1, v2);
                double phi = Math.Atan2(e2.Length, Vector3d.Dot(e1, v2));
                return SweepSection.Arc(x, e1, e2.Normalized(), r, phi);
            }

            // сверки по пробам: хребет есть всюду, идёт вперёд вместе с ребром, контакты тоже; дуга — меньше полуокружности
            var us = new List<double>(); var secs = new List<SweepSection>();
            Vector3d? prevS = null, prevA = null, prevB = null; Vector3d prevT = default;
            double widthA = 0, widthB = 0;
            var Ps = new List<Vector3d>();
            for (int i = 0; i <= CylSamples; i++)
            {
                double t = t0 + (t1 - t0) * i / CylSamples;
                var x = ch ? Ed(t) : Spine(t);
                if (x == null) { why = "cyl/cyl: ball centre not found (radius too large?)"; return false; }
                var P = Ed(t); Ps.Add(P);
                var qa = ch ? ChA(t) : Foot(FA, x.Value); var qb = ch ? ChB(t) : Foot(FB, x.Value);
                if (ch)
                {
                    if (double.IsNaN(qa.X + qb.X) || Math.Abs((qa - P).Length - dA) > 1e-8 * Math.Max(1, dA) || Math.Abs((qb - P).Length - dB) > 1e-8 * Math.Max(1, dB) || (qa - qb).Length < 1e-6)
                    { why = "chamfer: chamfer point not found"; return false; }
                }
                else if (Math.Abs((qa - x.Value).Length - r) > 1e-8 * Math.Max(1, r) || Math.Abs((qb - x.Value).Length - r) > 1e-8 * Math.Max(1, r))
                { why = "cyl/cyl: contact not at the ball radius"; return false; }
                var g = Gen(t);
                if (!ch && !(g.Phi > 1e-3 && g.Phi < Math.PI - 1e-3)) { why = "cyl/cyl: blend arc degenerate"; return false; }
                widthA = Math.Max(widthA, (qa - P).Length); widthB = Math.Max(widthB, (qb - P).Length);
                if (prevS != null)
                {
                    if (Vector3d.Dot(x.Value - prevS.Value, prevT) <= 0 || Vector3d.Dot(qa - prevA!.Value, prevT) <= 0 || Vector3d.Dot(qb - prevB!.Value, prevT) <= 0)
                    { why = "cyl/cyl: spine or contact line folds back (radius too large for the curvature)"; return false; }
                }
                prevS = x; prevA = qa; prevB = qb; prevT = Td(t);
                if (i < CylSamples) { us.Add(t); secs.Add(g); }
            }
            // зазор: другие контуры граней дальше полосы сопряжения
            foreach (var (F, l, w) in new[] { (FA, lA, widthA), (FB, lB, widthB) })
                foreach (var ol in F.Loops.Where(x => x != l))
                    foreach (var sg in ol)
                        for (int k = 0; k <= 32; k++)
                        {
                            var q = sg.Curve.PointAt(sg.Curve.T0 + (sg.Curve.T1 - sg.Curve.T0) * k / 32.0);
                            if (Ps.Min(p => (p - q).Length) <= 1.5 * w + 1e-6) { why = "cyl/cyl: another loop of the face in the blend strip"; return false; }
                        }

            // параметр поверхности — в миллиметрах вдоль ребра (u = k·(t − t0)): таблица формулы в SectionSurface шагает
            // по u не больше 0.25 мм и 1/20 ширины сечения — с параметром ребра [0, 1] шаг был бы в десятки мм
            double Lp = 0; for (int i = 1; i < Ps.Count; i++) Lp += (Ps[i] - Ps[i - 1]).Length;
            double kU = Lp / (t1 - t0);
                        var surf = new SectionSurface(U => Gen(t0 + U / kU), us.Select(t => (t - t0) * kU).ToList(), secs, true, 0, Lp, null, CylTable ? Math.Max(Lp / 20000, Math.Min(0.25, 0.4 * Math.Min(widthA, widthB)) / 32) : 0) { ParallelTable = true };
            // таблица формулы против самой формулы: расхождение — в пределах допуска p-кривой
            {
                double worstTab = 0;
                for (int i = 0; i < 1000; i++)
                {
                    double U = Lp * (i + 0.5) / 1000;
                    var ta = surf.SectionAt(U); var ex = surf.ExactSectionAt(U);
                    foreach (double v in new[] { 0.0, 0.5, 1.0 }) worstTab = Math.Max(worstTab, (ta.Point(v) - ex.Point(v)).Length);
                }
                Debug?.Invoke($"cyl/cyl: blend surface table vs formula {worstTab:E2} mm");
                if (worstTab > 1e-8) { why = $"cyl/cyl: blend surface table deviates from the formula ({worstTab:E1} mm)"; return false; }
            }
            if (Debug != null)
            {
                double worst = 0, wt = 0, jump = 0, jt = 0;
                for (int i = 0; i <= 512; i++)
                {
                    double t = t0 + (t1 - t0) * i / 512.0;
                    foreach (var q in new[] { QA(t), QB(t) })
                    {
                        var uv = surf.ProjectToUV(q); double d = (surf.PointAt(uv) - q).Length;
                        if (d > worst) { worst = d; wt = t; }
                    }
                    if (i < 512) { double j = (S(t + 1e-9) - S(t)).Length; if (j > jump) { jump = j; jt = t; } }
                }
                Debug($"cyl/cyl: contact projection worst {worst:E2} at t {wt:R}; spine jump per 1e-9 {jump:E2} at {jt:R}");
            }
            // точки граней: за линией касания на ширину полосы
            double tm = (t0 + t1) / 2; var Em = Ed(tm); var qam = QA(tm); var qbm = QB(tm);
            FA.Touched = true; FA.Hint = Foot(FA, qam + (qam - Em));
            FB.Touched = true; FB.Hint = Foot(FB, qbm + (qbm - Em));
            // лента: наружу — от хребта у выпуклого, к нему у вогнутого
            var gm = Gen(tm); var mid = gm.Point(0.5);
            var want = ch ? (Geometry.Ssi.Implicit(shA, Em).G * sA + Geometry.Ssi.Implicit(shB, Em).G * sB) : (mid - gm.C).Normalized() * (-sigma);
            var nS = surf.NormalAt(new Vector2d((tm - t0) * kU, 0.5));
            bool rev = Vector3d.Dot(nS, want) < 0;
            if (!CylHalf)
            {
                // прежде: лента — один пояс без шва (сетка пояса режет его по шву хордой поперёк дуги: у r ≥ 2 прогиб хорды
                // больше допуска, и разметка отказывала)
                var cA = new ProcCurve3(QA, t0, t1, true, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch ? null : (s, a, b) => NoEvents(true, s, a, b) };
                var cB = new ProcCurve3(QB, t0, t1, true, ch ? "chamfer contact" : "rolling-ball contact") { NoEventsOn = ch ? null : (s, a, b) => NoEvents(false, s, a, b) };
                var pA = cA.PointAt(t0); var pB = cB.PointAt(t0);
                lA.Clear(); lA.Add(new Seg { Curve = cA, A = pA, B = pA });
                lB.Clear(); lB.Add(new Seg { Curve = cB, A = pB, B = pB });
                var bl = new FDesc { Surf = surf, Rev = rev, Tag = ch ? "chamfer:face" : "round:face", Touched = true, Hint = mid };
                bl.Loops.Add(new List<Seg> { new() { Curve = cA, A = pA, B = pA } });
                bl.Loops.Add(new List<Seg> { new() { Curve = cB, A = pB, B = pB } });
                blends.Add(bl);
            }
            else
            {
                // ЛЕНТА — ДВЕ ГРАНИ (t ∈ [0, π] и [π, 2π]) НА ОДНОЙ ПОВЕРХНОСТИ, швы — дуги сечений (окружности радиуса r вокруг
                // точки хребта, точные): у каждой грани контур не обходит период, и разметка идёт обычным путём (дуги-швы
                // гранятся по прогибу). Линии касания — по две половины.
                double th = (t0 + t1) / 2;
                ICurve3 SecArc(double t) { var g = Gen(t); return ch ? new LineCurve3(g.A, g.B) : new ArcCurve3(new Frame3(g.C, g.E1, g.E2), r, 0, g.Phi); }
                var a0 = SecArc(t0); var ah = SecArc(th);
                string kind = ch ? "chamfer contact" : "rolling-ball contact";
                Func<Geometry.Ssi.Shape, double, double, bool>? evA = ch ? null : (s, a, b) => NoEvents(true, s, a, b), evB = ch ? null : (s, a, b) => NoEvents(false, s, a, b);
                var cA1 = new ProcCurve3(QA, t0, th, false, kind) { NoEventsOn = evA }; var cA2 = new ProcCurve3(QA, th, t1, false, kind) { NoEventsOn = evA };
                var cB1 = new ProcCurve3(QB, t0, th, false, kind) { NoEventsOn = evB }; var cB2 = new ProcCurve3(QB, th, t1, false, kind) { NoEventsOn = evB };
                Vector3d qa0 = QA(t0), qah = QA(th), qb0 = QB(t0), qbh = QB(th);
                if ((a0.PointAt(0) - qa0).Length > 1e-9 || (a0.PointAt(1) - qb0).Length > 1e-9 || (ah.PointAt(0) - qah).Length > 1e-9 || (ah.PointAt(1) - qbh).Length > 1e-9
                    || (QA(t1) - qa0).Length > 1e-9 || (QB(t1) - qb0).Length > 1e-9)
                { why = "cyl/cyl: seam arcs do not meet the contact lines"; return false; }
                lA.Clear(); lA.Add(new Seg { Curve = cA1, A = qa0, B = qah }); lA.Add(new Seg { Curve = cA2, A = qah, B = qa0 });
                lB.Clear(); lB.Add(new Seg { Curve = cB1, A = qb0, B = qbh }); lB.Add(new Seg { Curve = cB2, A = qbh, B = qb0 });
                foreach (var (ta, tb, cA, cB, arcA, arcB) in new[] { (t0, th, cA1, cB1, a0, ah), (th, t1, cA2, cB2, ah, a0) })
                {
                    var qa1 = QA(ta); var qa2 = QA(tb); var qb1 = QB(ta); var qb2 = QB(tb);
                    var hint = Gen((ta + tb) / 2).Point(0.5);
                    var bl = new FDesc { Surf = surf, Rev = rev, Tag = ch ? "chamfer:face" : "round:face", Touched = true, Hint = hint };
                    bl.Loops.Add(new List<Seg>
                    {
                        new() { Curve = cA, A = qa1, B = qa2 },
                        new() { Curve = arcB, A = qa2, B = qb2 },
                        new() { Curve = cB, A = qb2, B = qb1 },
                        new() { Curve = arcA, A = qb1, B = qa1 },
                    });
                    blends.Add(bl);
                }
            }
            Debug?.Invoke($"cyl/cyl: sigma {sigma} sA {sA} sB {sB} offsets {offA:R}/{offB:R} shapes {shA.GetType().Name}/{shB.GetType().Name} strip widths {widthA:G4}/{widthB:G4}");
        }
        return true;
    }

    /// <summary>
    /// 06.15: ТОЧКА ФАСКИ на форме sh у точки ребра P: в плоскости поперёк ребра (касательная tg), на хордовом расстоянии d от P,
    /// по ту сторону, где грань уходит от ребра (направление вдоль грани, противоположное наружной нормали другой грани у
    /// выпуклого ребра и сонаправленное у вогнутого). Ньютон на F_sh = 0, (x − P)·tg = 0, |x − P|² = d². NaN — не найдена.
    /// </summary>
    static Vector3d ChamferPoint(Geometry.Ssi.Shape sh, Geometry.Ssi.Shape other, double sSh, double sOther, double sigma, Vector3d P, Vector3d tg, double d)
    {
        // 17:31: точка ребра за концом продления может не найтись (NaN) — фаска отказывает, а не падает (Math.Sign(NaN))
        if (!double.IsFinite(P.X + P.Y + P.Z + tg.X + tg.Y + tg.Z + d)) return new Vector3d(double.NaN);
        var n = Geometry.Ssi.Implicit(sh, P).G; var nO = Geometry.Ssi.Implicit(other, P).G * sOther;
        var u = Vector3d.Cross(tg, n); if (!(u.Length >= 1e-12)) return new Vector3d(double.NaN);
        u.Normalize(); if (!double.IsFinite(Vector3d.Dot(u, nO))) return new Vector3d(double.NaN);
        if (Math.Sign(Vector3d.Dot(u, nO)) != Math.Sign(sigma)) u = -u;
        var x = P + u * d;
        for (int it = 0; it < 60; it++)
        {
            var (f, g) = Geometry.Ssi.Implicit(sh, x); var w = x - P;
            var dx = Solve3(new[] { g, tg, w * 2 }, new[] { -f, -Vector3d.Dot(w, tg), d * d - w.LengthSquared });
            if (dx == null) return new Vector3d(double.NaN);
            x += dx.Value;
            if (dx.Value.Length < 1e-15 * Math.Max(1, x.Length)) break;
        }
        var wf = x - P;
        if (!double.IsFinite(x.X + x.Y + x.Z)) return new Vector3d(double.NaN);
        if (Math.Abs(Geometry.Ssi.Implicit(sh, x).F) > 1e-10 || Math.Abs(wf.Length - d) > 1e-10 * Math.Max(1, d) || Math.Abs(Vector3d.Dot(wf, tg)) > 1e-10 * Math.Max(1, d)
            || Math.Sign(Vector3d.Dot(wf, nO)) != Math.Sign(sigma)) return new Vector3d(double.NaN);
        return x;
    }

    /// <summary>Треугольники тела (сетки граней) — для пробы точки.</summary>
    static List<(Vector3d A, Vector3d B, Vector3d C)> SolidTriangles(TSolid body)
    {
        var md = Tess.Tessellator.Tessellate(body, facets: true);
        var l = new List<(Vector3d, Vector3d, Vector3d)>(md.Indices.Length / 3);
        Vector3d V(int i) { var p = md.Vertices[md.Indices[i]].P; return new Vector3d(p.X, p.Y, p.Z); }
        for (int t = 0; t + 2 < md.Indices.Length; t += 3) l.Add((V(t), V(t + 1), V(t + 2)));
        return l;
    }

    /// <summary>Точка внутри сетки: чётность пересечений лучей трёх направлений (касания лучом — направление не в счёт).</summary>
    static bool? InsideMesh(List<(Vector3d A, Vector3d B, Vector3d C)> tris, Vector3d p)
    {
        var dirs = new[] { new Vector3d(0.5383, 0.6190, 0.5719), new Vector3d(-0.7131, 0.2213, 0.6653), new Vector3d(0.1213, -0.8722, 0.4739) };
        int ins = 0, clean = 0;
        foreach (var d0 in dirs)
        {
            var d = d0.Normalized(); int hits = 0; bool dirty = false;
            foreach (var (a, b, c) in tris)
            {
                var e1 = b - a; var e2 = c - a; var h = Vector3d.Cross(d, e2); double det = Vector3d.Dot(e1, h);
                if (Math.Abs(det) < 1e-18) continue;
                var s = p - a; double u = Vector3d.Dot(s, h) / det; if (u < -1e-9 || u > 1 + 1e-9) continue;
                var q = Vector3d.Cross(s, e1); double v = Vector3d.Dot(d, q) / det; if (v < -1e-9 || u + v > 1 + 1e-9) continue;
                double tt = Vector3d.Dot(e2, q) / det; if (tt <= 1e-12) continue;
                if (u < 1e-9 || v < 1e-9 || u + v > 1 - 1e-9) { dirty = true; break; }
                hits++;
            }
            if (dirty) continue;
            clean++; if ((hits & 1) == 1) ins++;
        }
        if (clean == 0) return null;
        if (ins * 2 == clean) return null;
        return ins * 2 > clean;
    }
}
