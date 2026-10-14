using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// Независимый эталон: объём «уголка» катящегося шара у кромки отверстия ρ в валу R (ось X; отверстие — ось Z), отсечённого
    /// полупространством H = {n·p ≥ off}, n = (−sin a, cos a, 0) — вертикальная плоскость. Формула Гаусса–Остроградского по
    /// трём полосам границы уголка (как RollingBallCusp) с началом p0 на плоскости H — поток через срез плоскостью равен нулю;
    /// на каждой линии λ = const промежутки φ внутри H — корнями n·P(φ, λ) = off (бисекция), квадратура Гаусса на кусках.
    /// </summary>
    static double RollingBallCuspCut(double R, double rho, double r, double a, double off, int nL = 8, int nSub = 48)
    {
        double sR = R - r, sRho = rho + r;
        var n = new Vector3d(-Math.Sin(a), Math.Cos(a), 0); var p0 = n * off + Vector3d.UnitZ * (R - 0.5);   // начало у уголка (меньше сокращения)
        Vector3d Spine(double phi) { double x = sRho * Math.Cos(phi), y = sRho * Math.Sin(phi); return new(x, y, Math.Sqrt(sR * sR - y * y)); }
        Vector3d C1(double phi) { var s = Spine(phi); var yz = new Vector2d(s.Y, s.Z); var k = R / yz.Length; return new(s.X, s.Y * k, s.Z * k); }
        Vector3d C2(double phi) { var s = Spine(phi); return new(rho * Math.Cos(phi), rho * Math.Sin(phi), s.Z); }
        Vector3d E(double phi) { double x = rho * Math.Cos(phi), y = rho * Math.Sin(phi); return new(x, y, Math.Sqrt(R * R - y * y)); }
        double[] gx = { -0.9739065285171717, -0.8650633666889845, -0.6794095682990244, -0.4333953941292472, -0.1488743389816312, 0.1488743389816312, 0.4333953941292472, 0.6794095682990244, 0.8650633666889845, 0.9739065285171717 };
        double[] gw = { 0.0666713443086881, 0.1494513491505806, 0.2190863625159820, 0.2692667193099963, 0.2955242247147529, 0.2955242247147529, 0.2692667193099963, 0.2190863625159820, 0.1494513491505806, 0.0666713443086881 };
        double total = 0;
        void Patch(Func<double, double, Vector3d> P, Func<Vector3d, double, Vector3d> outDir)
        {
            double h = 1e-6; int? orient = null;
            for (int j = 0; j < nL; j++)
                for (int b = 0; b < gx.Length; b++)
                {
                    double lam = (j + (gx[b] + 1) / 2) / nL, wl = gw[b] / 2 / nL;
                    double G(double phi) => Vector3d.Dot(n, P(phi, lam)) - off;
                    // промежутки φ ∈ [0, 2π) с G ≥ 0
                    const int M = 256; var cuts = new List<double>();
                    double gPrev = G(0);
                    for (int i = 1; i <= M; i++)
                    {
                        double x1 = 2 * Math.PI * i / M, g1 = G(x1);
                        if (Math.Sign(g1) != Math.Sign(gPrev))
                        {
                            double lo = 2 * Math.PI * (i - 1) / M, hi = x1, glo = gPrev;
                            for (int it = 0; it < 80; it++) { double md = (lo + hi) / 2, gm = G(md); if (Math.Sign(gm) == Math.Sign(glo)) { lo = md; glo = gm; } else hi = md; }
                            cuts.Add((lo + hi) / 2);
                        }
                        gPrev = g1;
                    }
                    var ivs = new List<(double A, double B)>();
                    var pts = new List<double> { 0 }; pts.AddRange(cuts); pts.Add(2 * Math.PI);
                    for (int i = 0; i + 1 < pts.Count; i++) if (G((pts[i] + pts[i + 1]) / 2) > 0) ivs.Add((pts[i], pts[i + 1]));
                    foreach (var (A, B) in ivs)
                        for (int sIdx = 0; sIdx < nSub; sIdx++)
                            for (int q = 0; q < gx.Length; q++)
                            {
                                double phi = A + (B - A) * (sIdx + (gx[q] + 1) / 2) / nSub, wphi = gw[q] / 2 * (B - A) / nSub;
                                var p = P(phi, lam);
                                var du = (P(phi + h, lam) - P(phi - h, lam)) / (2 * h);
                                var dv = (P(phi, Math.Min(1, lam + h)) - P(phi, Math.Max(0, lam - h))) / (Math.Min(1, lam + h) - Math.Max(0, lam - h));
                                var nn = Vector3d.Cross(du, dv);
                                // ориентация — одна на полосу (по первой точке): знаковый интеграл верен и при складке параметризации
                                orient ??= Vector3d.Dot(nn, outDir(p, phi)) < 0 ? -1 : 1;
                                total += Vector3d.Dot(p - p0, nn) * orient.Value / 3 * wphi * wl;
                            }
                }
        }
        Patch((phi, lam) =>
        {
            var s = Spine(phi); var aa = (C1(phi) - s).Normalized(); var bb = (C2(phi) - s).Normalized();
            double ang = Math.Acos(Math.Clamp(Vector3d.Dot(aa, bb), -1, 1)); var bp = (bb - aa * Vector3d.Dot(aa, bb)).Normalized();
            return s + (aa * Math.Cos(ang * lam) + bp * Math.Sin(ang * lam)) * r;
        }, (p, phi) => Spine(phi) - p);
        Patch((phi, lam) =>
        {
            var e = E(phi); var c = C1(phi);
            double te = Math.Atan2(e.Z, e.Y), tc = Math.Atan2(c.Z, c.Y), t = te + (tc - te) * lam, x = e.X + (c.X - e.X) * lam;
            return new Vector3d(x, R * Math.Cos(t), R * Math.Sin(t));
        }, (p, phi) => new Vector3d(0, p.Y, p.Z));
        Patch((phi, lam) => { var e = E(phi); var c = C2(phi); return e + (c - e) * lam; }, (p, phi) => new Vector3d(-p.X, -p.Y, 0));
        return Math.Abs(total);
    }

    static void Krn06OpenChecks()
    {
        Check("KRN06-OPEN-QUADRIC-ROLLING-BALL", () =>
        {
            // НЕЗАМКНУТОЕ РЕБРО ЦИЛИНДР/ЦИЛИНДР (06.12, 06.14): вал R20 (ось X), отсечённый вертикальной плоскостью (поворот a вокруг
            // Z, сдвиг off), с отверстием ρ по оси Z — кромка отверстия на валу кончается на плоском срезе. Скругление — катящийся
            // шар с обрезкой плоскостью среза. Эталон — RollingBallCuspCut (независимая квадратура); при a = 0, off = 0 ещё и
            // половина замкнутого уголка (симметрия). OpenCascade (BRepFilletAPI_MakeFillet) — справочно, его точность ~1e-4.
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("P3D_OPENDBG") == "1") DirectRound.Debug = m => Console.WriteLine("    dr: " + m);
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            foreach (var (name, aDeg, off, rho, r, occ) in new[]
            {
                ("half d8 r1", 0.0, 0.0, 4.0, 1.0, 3.8743577014), ("half d8 r2", 0.0, 0.0, 4.0, 2.0, 17.4728044852),
                ("offset 1.5 d8 r1", 0.0, 1.5, 4.0, 1.0, 3.1861342498), ("offset 1.5 d8 r2", 0.0, 1.5, 4.0, 2.0, 14.7641345461),
                ("rot 20 d8 r1", 20.0, 0.0, 4.0, 1.0, 3.8800825800), ("rot 20 d8 r2", 20.0, 0.0, 4.0, 2.0, 17.4634399856),
                ("rot 35 off 0.8 d8 r1.5", 35.0, 0.8, 4.0, 1.5, 8.3460258936), ("half d30 r1", 0.0, 0.0, 15.0, 1.0, 38.6491731674),
                ("rot 20 d30 r1", 20.0, 0.0, 15.0, 1.0, 38.6682224875),
            })
            {
                double a = aDeg * Math.PI / 180;
                var b = new List<TSolid> { Shaft() }; var w = new List<string>();
                var cutter = TransformOp.Copy(Box(-60, off - 60, -60, 120, 60, 120), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitZ, aDeg));
                BooleanOp.Subtract(b, cutter, w);
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), rho, 80), w);
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN; int f0 = b[0].Faces.Count();
                var edges = b[0].Edges.Where(e => e.Curve is not Parametric3D.Kernel.Curves.LineCurve3 && e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) is var m && m.Z > 5 && Math.Abs(Math.Sqrt(m.X * m.X + m.Y * m.Y) - rho) < 1e-6).ToList();
                if (edges.Count != 1) { fails.Add($"{name}: edges {edges.Count}"); continue; }
                long built0 = DirectRound.OpenBuilt;
                try { FilletOp.RoundEdges(b, edges.Select(EdgeRef.Of).ToList(), r, new List<string>()); }
                catch (Exception e) { fails.Add($"{name}: {e.GetType().Name} {e.Message}"); continue; }
                if (DirectRound.OpenBuilt == built0) { fails.Add($"{name}: open builder not used"); continue; }
                var s = b[0];
                double want = RollingBallCuspCut(20, rho, r, a, off), dv = v0 - (AnalyticVolume.Of(s) ?? double.NaN);
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {s.Guarantee}");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"{name}: invalid");
                var fac = GeometryAudit.Faceted(s); if (fac != (0, 0)) fails.Add($"{name}: faceted {fac}");
                if (!(Math.Abs(dv - want) <= 2e-6 * want + 1e-7)) fails.Add($"{name}: dV {dv:R} vs reference {want:R}");
                if (aDeg == 0 && off == 0)
                {
                    double half = RollingBallCusp(20, rho, r, true) / 2;
                    if (!(Math.Abs(want - half) <= 2e-6 * half)) fails.Add($"{name}: reference {want:R} vs half closed {half:R}");
                }
                if (s.Faces.Count() != f0 + 1) fails.Add($"{name}: faces {f0}->{s.Faces.Count()}");
                double worst = 0;
                foreach (var bf in s.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface))
                    foreach (var e in bf.Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.ProcCurve3 { Kind: "rolling-ball contact" }))
                    {
                        var other = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().First(f => f != bf);
                        var sa = GeometryAudit.AuthoritativeSurface(bf)!; var sb = GeometryAudit.AuthoritativeSurface(other)!;
                        for (int k = 0; k <= 16; k++)
                        {
                            var p = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 16.0);
                            double c = Math.Abs(Vector3d.Dot(sa.NormalAt(sa.ProjectToUV(p)).Normalized(), sb.NormalAt(sb.ProjectToUV(p)).Normalized()));
                            worst = Math.Max(worst, Math.Acos(Math.Min(1, c)));
                        }
                    }
                if (!(worst < 1e-5)) fails.Add($"{name}: tangency off by {worst:E2} rad");
                Console.WriteLine($"  {name}: dV {dv:R} reference {want:R} OpenCascade {occ:R} ({(dv - occ) / occ:E1}) tangency {worst:E1}");
            }
            DirectRound.Debug = null;
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
    /// <summary>
    /// Независимый эталон фаски кромки отверстия (convex) или стыка бобышки (concave) ρ в валу R: точки фаски — на валу и на
    /// стенке в плоскости поперёк ребра на хорде d от точки ребра (Ньютон по своим параметрам поверхностей, свой код), объём
    /// «уголка» между ребром и линейчатой гранью фаски — Гаусс–Остроградский по трём полосам, отсечение полупространством
    /// {n·p ≥ off}, n = (−sin a, cos a, 0) — как у RollingBallCuspCut. null (a = NaN) — без отсечения.
    /// </summary>
    static double ChamferCuspCut(double R, double rho, double dA, double dB, bool convex, double a, double off, int nL = 8, int nSub = 48)
    {
        bool cut = !double.IsNaN(a);
        if (!cut) nSub *= 4;
        var n = cut ? new Vector3d(-Math.Sin(a), Math.Cos(a), 0) : Vector3d.Zero; var p0 = n * off + Vector3d.UnitZ * (R - 0.5);
        Vector3d E(double phi) { double x = rho * Math.Cos(phi), y = rho * Math.Sin(phi); return new(x, y, Math.Sqrt(R * R - y * y)); }
        // касательная ребра — аналитически (разностная внутри точки фаски, продифференцированной разностью ещё раз, давала шум 1e-4)
        Vector3d T(double phi) { double y = rho * Math.Sin(phi), yp = rho * Math.Cos(phi); return new Vector3d(-rho * Math.Sin(phi), yp, -y * yp / Math.Sqrt(R * R - y * y)).Normalized(); }
        // точка на валу: (x, R cos θ, R sin θ); на стенке: (ρ cos ψ, ρ sin ψ, z)
        Vector3d Shaft(double phi)
        {
            var e = E(phi); var t = T(phi); var rad = new Vector3d(e.X, e.Y, 0).Normalized();
            var g = e + rad * dA; double x = g.X, th = Math.Atan2(g.Z, g.Y);
            for (int it = 0; it < 100; it++)
            {
                Vector3d P(double xx, double tt) => new(xx, R * Math.Cos(tt), R * Math.Sin(tt));
                var p = P(x, th); var w = p - e;
                double f1 = Vector3d.Dot(w, t), f2 = w.LengthSquared - dA * dA;
                var px = new Vector3d(1, 0, 0); var pt = new Vector3d(0, -R * Math.Sin(th), R * Math.Cos(th));
                double a11 = Vector3d.Dot(px, t), a12 = Vector3d.Dot(pt, t), a21 = 2 * Vector3d.Dot(w, px), a22 = 2 * Vector3d.Dot(w, pt);
                double det = a11 * a22 - a12 * a21; double dx = (-f1 * a22 + f2 * a12) / det, dt = (-a11 * f2 + a21 * f1) / det;
                x += dx; th += dt; if (Math.Abs(dx) + Math.Abs(dt) < 1e-16) break;
            }
            return new(x, R * Math.Cos(th), R * Math.Sin(th));
        }
        Vector3d Wall(double phi)
        {
            var e = E(phi); var t = T(phi);
            double ps = phi, z = e.Z + (convex ? -dB : dB);
            for (int it = 0; it < 100; it++)
            {
                var p = new Vector3d(rho * Math.Cos(ps), rho * Math.Sin(ps), z); var w = p - e;
                double f1 = Vector3d.Dot(w, t), f2 = w.LengthSquared - dB * dB;
                var pp = new Vector3d(-rho * Math.Sin(ps), rho * Math.Cos(ps), 0); var pz = Vector3d.UnitZ;
                double a11 = Vector3d.Dot(pp, t), a12 = Vector3d.Dot(pz, t), a21 = 2 * Vector3d.Dot(w, pp), a22 = 2 * Vector3d.Dot(w, pz);
                double det = a11 * a22 - a12 * a21; double dps = (-f1 * a22 + f2 * a12) / det, dz = (-a11 * f2 + a21 * f1) / det;
                ps += dps; z += dz; if (Math.Abs(dps) + Math.Abs(dz) < 1e-16) break;
            }
            return new(rho * Math.Cos(ps), rho * Math.Sin(ps), z);
        }
        double[] gx = { -0.9739065285171717, -0.8650633666889845, -0.6794095682990244, -0.4333953941292472, -0.1488743389816312, 0.1488743389816312, 0.4333953941292472, 0.6794095682990244, 0.8650633666889845, 0.9739065285171717 };
        double[] gw = { 0.0666713443086881, 0.1494513491505806, 0.2190863625159820, 0.2692667193099963, 0.2955242247147529, 0.2955242247147529, 0.2692667193099963, 0.2190863625159820, 0.1494513491505806, 0.0666713443086881 };
        double total = 0;
        void Patch(Func<double, double, Vector3d> P, Func<Vector3d, double, Vector3d> outDir)
        {
            double h = 1e-6; int? orient = null;
            for (int j = 0; j < nL; j++)
                for (int b = 0; b < gx.Length; b++)
                {
                    double lam = (j + (gx[b] + 1) / 2) / nL, wl = gw[b] / 2 / nL;
                    var ivs = new List<(double A, double B)>();
                    if (!cut) ivs.Add((0, 2 * Math.PI));
                    else
                    {
                        double G(double phi) => Vector3d.Dot(n, P(phi, lam)) - off;
                        const int M = 256; var cuts = new List<double>(); double gPrev = G(0);
                        for (int i = 1; i <= M; i++)
                        {
                            double x1 = 2 * Math.PI * i / M, g1 = G(x1);
                            if (Math.Sign(g1) != Math.Sign(gPrev))
                            {
                                double lo = 2 * Math.PI * (i - 1) / M, hi = x1, glo = gPrev;
                                for (int it = 0; it < 80; it++) { double md = (lo + hi) / 2, gm = G(md); if (Math.Sign(gm) == Math.Sign(glo)) { lo = md; glo = gm; } else hi = md; }
                                cuts.Add((lo + hi) / 2);
                            }
                            gPrev = g1;
                        }
                        var pts = new List<double> { 0 }; pts.AddRange(cuts); pts.Add(2 * Math.PI);
                        for (int i = 0; i + 1 < pts.Count; i++) if (G((pts[i] + pts[i + 1]) / 2) > 0) ivs.Add((pts[i], pts[i + 1]));
                    }
                    foreach (var (A, B) in ivs)
                        for (int sIdx = 0; sIdx < nSub; sIdx++)
                            for (int q = 0; q < gx.Length; q++)
                            {
                                double phi = A + (B - A) * (sIdx + (gx[q] + 1) / 2) / nSub, wphi = gw[q] / 2 * (B - A) / nSub;
                                var p = P(phi, lam);
                                var du = (P(phi + h, lam) - P(phi - h, lam)) / (2 * h);
                                var dv = (P(phi, Math.Min(1, lam + h)) - P(phi, Math.Max(0, lam - h))) / (Math.Min(1, lam + h) - Math.Max(0, lam - h));
                                var nn = Vector3d.Cross(du, dv);
                                // ориентация — одна на полосу (по первой точке): знаковый интеграл верен и при складке параметризации
                                orient ??= Vector3d.Dot(nn, outDir(p, phi)) < 0 ? -1 : 1;
                                total += Vector3d.Dot(p - p0, nn) * orient.Value / 3 * wphi * wl;
                            }
                }
        }
        // грань фаски: наружу из уголка — от ребра
        Patch((phi, lam) => Shaft(phi) + (Wall(phi) - Shaft(phi)) * lam, (p, phi) => p - E(phi));
        // вал: от ребра к точке фаски (угол вокруг X и x — линейно); наружу — от оси X у отверстия, к оси у бобышки
        Patch((phi, lam) =>
        {
            var e = E(phi); var c = Shaft(phi);
            double te = Math.Atan2(e.Z, e.Y), tc = Math.Atan2(c.Z, c.Y), t = te + (tc - te) * lam, x = e.X + (c.X - e.X) * lam;
            return new Vector3d(x, R * Math.Cos(t), R * Math.Sin(t));
        }, (p, phi) => convex ? new Vector3d(0, p.Y, p.Z) : new Vector3d(0, -p.Y, -p.Z));
        // стенка: к оси Z (уголок снаружи отверстия и снаружи бобышки)
        Patch((phi, lam) =>
        {
            var e = E(phi); var c = Wall(phi);
            double pe = Math.Atan2(e.Y, e.X), pc = Math.Atan2(c.Y, c.X); double dp = pc - pe; while (dp > Math.PI) dp -= 2 * Math.PI; while (dp < -Math.PI) dp += 2 * Math.PI;
            double ps = pe + dp * lam, z = e.Z + (c.Z - e.Z) * lam;
            return new Vector3d(rho * Math.Cos(ps), rho * Math.Sin(ps), z);
        }, (p, phi) => new Vector3d(-p.X, -p.Y, 0));
        return Math.Abs(total);
    }

    static void Krn06CurvedChamferChecks()
    {
        Check("KRN06-CURVED-CHAMFER-EXACT", () =>
        {
            // ФАСКА РЁБЕР МЕЖДУ КРИВЫМИ ГРАНЯМИ (06.15): кромка отверстия в валу (выпуклая), стык бобышки (вогнутая) — замкнутые;
            // кромка в половине вала — незамкнутая с обрезкой плоскостью среза. Точки фаски — хорда d в плоскости поперёк ребра,
            // грань — линейчатая. Эталон — ChamferCuspCut (свой Ньютон на параметрах поверхностей). OpenCascade (MakeChamfer) — справочно.
            var fails = new List<string>();
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            foreach (var (name, cutv, rho, d1, d2, aDeg, off, occ) in new[]
            {
                ("closed hole d8 c1", true, 4.0, 1.0, 1.0, double.NaN, 0.0, 13.5137227429), ("closed hole d8 c2", true, 4.0, 2.0, 2.0, double.NaN, 0.0, 58.6322087325),
                ("closed boss R8 c2", false, 8.0, 2.0, 2.0, double.NaN, 0.0, 104.5419840614), ("closed hole d30 c1", true, 15.0, 1.0, 1.0, double.NaN, 0.0, 44.5249632579),
                ("closed hole d8 c1x2", true, 4.0, 1.0, 2.0, double.NaN, 0.0, double.NaN),
                ("half d8 c1", true, 4.0, 1.0, 1.0, 0.0, 0.0, 6.7584015445), ("offset 1.5 d8 c1", true, 4.0, 1.0, 1.0, 0.0, 1.5, 5.2503133580),
                ("rot 35 off 0.8 d8 c1.5", true, 4.0, 1.5, 1.5, 35.0, 0.8, double.NaN),
            })
            {
                var b = new List<TSolid> { Shaft() }; var w = new List<string>();
                bool open = !double.IsNaN(aDeg);
                if (open) BooleanOp.Subtract(b, TransformOp.Copy(Box(-60, off - 60, -60, 120, 60, 120), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitZ, aDeg)), w);
                if (cutv) BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), rho, 80), w);
                else BooleanOp.Unite(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), rho, 40), w);
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
                var edges = b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) is var m && m.Z > 5 && Math.Abs(Math.Sqrt(m.X * m.X + m.Y * m.Y) - rho) < 1e-6).ToList();
                if (edges.Count != 1) { fails.Add($"{name}: edges {edges.Count}"); continue; }
                long n0 = DirectRound.CurvedChamfers;
                try
                {
                    FilletOp.ChamferEdges(b, edges.Select(EdgeRef.Of).ToList(), d1, d2, new List<string>());
                }
                catch (Exception e) { fails.Add($"{name}: {e.GetType().Name} {e.Message}"); continue; }
                if (DirectRound.CurvedChamfers == n0) { fails.Add($"{name}: curved chamfer builder not used"); continue; }
                var s = b[0];
                // какая грань получила первый размер — по построению (правило резцов); эталон считается для обоих назначений
                double dv = Math.Abs(v0 - (AnalyticVolume.Of(s) ?? double.NaN));
                double want = ChamferCuspCut(20, rho, d1, d2, cutv, open ? aDeg * Math.PI / 180 : double.NaN, off);
                double alt = Math.Abs(d1 - d2) < 1e-12 ? want : ChamferCuspCut(20, rho, d2, d1, cutv, open ? aDeg * Math.PI / 180 : double.NaN, off);
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {s.Guarantee}");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"{name}: invalid");
                var fac = GeometryAudit.Faceted(s); if (fac != (0, 0)) fails.Add($"{name}: faceted {fac}");
                double best = Math.Abs(dv - want) <= Math.Abs(dv - alt) ? want : alt;
                if (!(Math.Abs(dv - best) <= 2e-6 * best + 1e-7)) fails.Add($"{name}: dV {dv:R} vs reference {want:R} / {alt:R}");
                if (Math.Abs(d1 - d2) > 1e-12 && Math.Abs(want - alt) < 1e-3 * want) fails.Add($"{name}: unequal sizes not distinguishable");
                Console.WriteLine($"  {name}: dV {dv:R} reference {best:R} OpenCascade {occ:R} ({(dv - occ) / occ:E1})");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    static void Krn06NurbsBlendChecks()
    {
        Check("KRN06-NURBS-ROLLING-BALL-INVARIANT", () =>
        {
            // ГРАНИ NURBS В ПОСТРОИТЕЛЕ КАТЯЩЕГОСЯ ШАРА (06.3, 06.4, 06.14): вал R20 — точный NURBS-лофт двух окружностей (грань
            // вала — рациональная квадратичная, швы под 45°), то же тело аналитически. Неявная форма грани NURBS — по основанию
            // перпендикуляра (Ssi.ParamSurf), продолжение за край области — многочлены крайних промежутков. Требуется: кромка
            // отверстия (замкнутая и в половине вала), стык бобышки, фаска — как у формул (RollingBallCusp, -Cut, ChamferCuspCut);
            // кромка косого торца в половине вала (ребро плоскость/NURBS без оси — параметр по опорной кривой Чебышёва) — как у
            // того же тела с аналитическим цилиндром (параметр по углу вокруг оси вала).
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("P3D_OPENDBG") == "1") { DirectRound.Debug = m => Console.WriteLine("    dr: " + m); Parametric3D.Kernel.Geometry.Ssi.FootDebug = m => Console.WriteLine("    foot: " + m); }
            double a45 = Math.Sqrt(0.5);
            Parametric3D.Kernel.Ops.Profile Circle(double x)
            {
                var U = new Vector3d(0, a45, a45); var V = new Vector3d(0, -a45, a45);
                var fr = new Frame3(new Vector3d(x, 0, 0), U, V);
                // окружность — четыре четверти (у лофта — по грани NURBS на четверть; швы — под 45° к вертикали)
                return ProfileBuilder.Build(fr, Enumerable.Range(0, 4).Select(k => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.ArcCurve3(fr, 20, k * Math.PI / 2, (k + 1) * Math.PI / 2)).ToList());
            }
            // три сечения, гладкий лофт (две соосные окружности строятся вращением — цилиндром; линейчатый по трём — шов пролётов)
            TSolid NShaft() => LoftOp.Build(new List<Parametric3D.Kernel.Ops.Profile> { Circle(-50), Circle(0), Circle(50) }, 64, true);
            TSolid AShaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            {
                var s0 = NShaft();
                int nn = s0.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface);
                if (nn == 0 || Math.Abs((AnalyticVolume.Of(s0) ?? 0) - Math.PI * 400 * 100) > 1e-9 * Math.PI * 400 * 100) fails.Add($"NURBS shaft: {nn} NURBS faces, V {AnalyticVolume.Of(s0):R}");
            }
            List<TEdge> RimEdges(TSolid b, double rho) => b.Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) is var m && m.Z > 5 && Math.Abs(Math.Sqrt(m.X * m.X + m.Y * m.Y) - rho) < 1e-6).ToList();
            double Run(TSolid shaft, string name, Action<List<TSolid>> prep, Func<TSolid, List<TEdge>> pick, Action<List<TSolid>, List<EdgeRef>> op)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var b = new List<TSolid> { shaft }; prep(b);
                double tPrep = sw.Elapsed.TotalSeconds; long f0c = Parametric3D.Kernel.Geometry.Ssi.ParamFootCalls, f0g = Parametric3D.Kernel.Geometry.Ssi.ParamFootGlobal;
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN; var b0copy = b[0];
                if (b[0].Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name}: input {b[0].Guarantee}"); return double.NaN; }
                if (Environment.GetEnvironmentVariable("P3D_OPENDBG") == "1")
                    Console.WriteLine($"    {name}: INPUT {b[0].Guarantee} faces {string.Join(" ", b[0].Faces.Select(f => f.Surface.GetType().Name[..4] + "/" + GeometryAudit.AuthoritativeSurface(f)?.GetType().Name[..4]))} seams tol {string.Join(",", b[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Y - 14.142) < 0.01).Select(e => e.Tolerance.ToString("E1")))}");
                var es = pick(b[0]);
                // кромка может быть разбита булевой точкой, где сходятся лишь те же две грани: выбирается одно звено — построитель
                // сам сливает цепочку через вершины степени 2
                if (es.Count == 0) { fails.Add($"{name}: no edges"); return double.NaN; }
                es = es.Take(1).ToList();
                var fac0 = GeometryAudit.Faceted(b[0]);
                long d0 = DirectRound.Built;
                try { op(b, es.Select(EdgeRef.Of).ToList()); } catch (Exception e) { fails.Add($"{name}: {e.GetType().Name} {e.Message}"); return double.NaN; }
                if (DirectRound.Built == d0) { fails.Add($"{name}: direct builder not used"); return double.NaN; }
                var s = b[0];
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {s.Guarantee}");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"{name}: invalid");
                var fac = GeometryAudit.Faceted(s); if (fac.Item1 > fac0.Item1 || fac.Item2 > fac0.Item2) fails.Add($"{name}: faceted {fac} (input {fac0})");
                var e0 = AnalyticVolume.OfWithError(b0copy);
                if (Environment.GetEnvironmentVariable("P3D_OPENDBG") == "1") AnalyticVolume.QuadDebug = (f, fi, co) => { if (Math.Abs(fi - co) > 1e-6) Console.WriteLine($"      quad face {f.Id} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} tag {f.Tag} fine {fi:R} coarse {co:R} edges {string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve.GetType().Name + (c.Edge.Curve is Parametric3D.Kernel.Curves.ProcCurve3 pc ? ":" + pc.Kind : "")))}"); };
                var e1 = AnalyticVolume.OfWithError(s);
                AnalyticVolume.QuadDebug = null;
                Console.WriteLine($"    {name}: V0 {e0?.Volume:R} ±{e0?.QuadratureError:E1}, V1 {e1?.Volume:R} ±{e1?.QuadratureError:E1}");
                Console.WriteLine($"    {name}: prep {tPrep:F1} s, total {sw.Elapsed.TotalSeconds:F1} s, feet {Parametric3D.Kernel.Geometry.Ssi.ParamFootCalls - f0c} (global {Parametric3D.Kernel.Geometry.Ssi.ParamFootGlobal - f0g})");
                return Math.Abs(v0 - (AnalyticVolume.Of(s) ?? double.NaN));
            }
            var w = new List<string>();
            void Hole(List<TSolid> b, double rho) => BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), rho, 80), w);
            void Half(List<TSolid> b) => BooleanOp.Subtract(b, Box(-60, -60, -60, 120, 60, 120), w);
            void Round(List<TSolid> b, List<EdgeRef> e, double r) => FilletOp.RoundEdges(b, e, r, new List<string>());
            void Chamf(List<TSolid> b, List<EdgeRef> e, double d) => FilletOp.ChamferEdges(b, e, d, d, new List<string>());
            void Cmp(string name, double got, double want, double tol)
            {
                if (!(Math.Abs(got - want) <= tol * want)) fails.Add($"{name}: dV {got:R} vs {want:R}");
                Console.WriteLine($"  {name}: dV {got:R} reference {want:R} ({(got - want) / want:E1})");
            }
            Cmp("NURBS closed hole d8 r1", Run(NShaft(), "NURBS closed hole d8 r1", b => Hole(b, 4), b => RimEdges(b, 4), (b, e) => Round(b, e, 1)), RollingBallCusp(20, 4, 1, true), 2e-6);
            Cmp("NURBS half hole d8 r2", Run(NShaft(), "NURBS half hole d8 r2", b => { Hole(b, 4); Half(b); }, b => RimEdges(b, 4), (b, e) => Round(b, e, 2)), RollingBallCuspCut(20, 4, 2, 0, 0), 2e-6);
            Cmp("NURBS closed boss R8 r3", Run(NShaft(), "NURBS closed boss R8 r3", b => BooleanOp.Unite(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 8, 40), w), b => RimEdges(b, 8), (b, e) => Round(b, e, 3)), RollingBallCusp(20, 8, 3, false), 2e-6);
            Cmp("NURBS closed hole d8 chamfer 1", Run(NShaft(), "NURBS closed hole d8 chamfer 1", b => Hole(b, 4), b => RimEdges(b, 4), (b, e) => Chamf(b, e, 1)), ChamferCuspCut(20, 4, 1, 1, true, double.NaN, 0), 2e-6);
            // косой плоский срез верха вала (z = 18 + 0.02x): ребро плоскость/вал — дуга эллипса от торца до торца, в одной грани
            // NURBS (|y| ≤ 10.5 < z); у NURBS нет оси — общий параметр по опорной кривой, у цилиндра — угол вокруг оси вала
            void Oblique(List<TSolid> b) => BooleanOp.Subtract(b, TransformOp.Copy(Box(-60, -30, 18, 120, 60, 30), TransformOp.Rotate(new Vector3d(0, 0, 18), Vector3d.UnitY, -Math.Atan(0.02) * 180 / Math.PI)), w);
            List<TEdge> ObliqueEdge(TSolid b) => b.Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) is var m && m.Z > 15 && m.Y > 3).ToList();
            double an = Run(AShaft(), "analytic oblique end r2", Oblique, ObliqueEdge, (b, e) => Round(b, e, 2));
            double nu = Run(NShaft(), "NURBS oblique end r2", Oblique, ObliqueEdge, (b, e) => Round(b, e, 2));
            Cmp("NURBS oblique end r2 vs analytic", nu, an, 1e-5);
            double anc = Run(AShaft(), "analytic oblique end chamfer 1.5", Oblique, ObliqueEdge, (b, e) => Chamf(b, e, 1.5));
            double nuc = Run(NShaft(), "NURBS oblique end chamfer 1.5", Oblique, ObliqueEdge, (b, e) => Chamf(b, e, 1.5));
            Cmp("NURBS oblique end chamfer 1.5 vs analytic", nuc, anc, 1e-5);
            DirectRound.Debug = null;
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    /// <summary>
    /// Независимый эталон скругления ПЕРЕМЕННОГО радиуса кромки отверстия ρ в валу R (вал — ось X, отверстие — ось Z), отсечённого
    /// полупространством {n·p ≥ off}: закон r = rS + (rE − rS)·s/L по длине ребра s от конца S (φ = φS) до другого конца,
    /// за концами — продолжение. Центр шара — в плоскости поперёк ребра (своя аналитическая касательная), на расстояниях
    /// R − r от оси вала и ρ + r от оси отверстия (свой Ньютон); сечение — характеристическая окружность огибающей
    /// (центр c − (r r'/|c'|²) c'). Объём — Гаусс–Остроградский по трём полосам, начало на плоскости среза.
    /// </summary>
    static double RollingBallVarCut(double R, double rho, double rS, double rE, double phiS, double phiEnd, double a, double off, int nL = 8, int nSub = 48)
    {
        var n = new Vector3d(-Math.Sin(a), Math.Cos(a), 0); var p0 = n * off + Vector3d.UnitZ * (R - 0.5);
        Vector3d Ep(double phi) { double x = rho * Math.Cos(phi), y = rho * Math.Sin(phi); return new(x, y, Math.Sqrt(R * R - y * y)); }
        Vector3d dE(double phi) { double y = rho * Math.Sin(phi), yp = rho * Math.Cos(phi); return new Vector3d(-rho * Math.Sin(phi), yp, -y * yp / Math.Sqrt(R * R - y * y)); }
        double[] g20x = { -0.9931285991850949, -0.9639719272779138, -0.9122344282513259, -0.8391169718222188, -0.7463319064601508, -0.6360536807265150, -0.5108670019508271, -0.3737060887154195, -0.2277858511416451, -0.0765265211334973, 0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150, 0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949 };
        double[] g20w = { 0.0176140071391521, 0.0406014298003869, 0.0626720483341091, 0.0832767415767048, 0.1019301198172404, 0.1181945319615184, 0.1316886384491766, 0.1420961093183820, 0.1491729864726037, 0.1527533871307258, 0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184, 0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521 };
        double ArcLen(double f0, double f1)
        {
            double sum = 0; int pieces = 8;
            for (int k = 0; k < pieces; k++)
            {
                double a0 = f0 + (f1 - f0) * k / pieces, a1 = f0 + (f1 - f0) * (k + 1) / pieces;
                for (int i = 0; i < 20; i++) sum += g20w[i] / 2 * (a1 - a0) * dE((a0 + a1) / 2 + (a1 - a0) / 2 * g20x[i]).Length;
            }
            return sum;
        }
        double L = Math.Abs(ArcLen(phiS, phiEnd)); double dir = Math.Sign(phiEnd - phiS);
        double Rad(double phi) => rS + (rE - rS) * dir * ArcLen(phiS, phi) / L;
        Vector3d C(double phi)
        {
            var e = Ep(phi); var t = dE(phi).Normalized(); double r = Rad(phi);
            var nS = new Vector3d(0, e.Y, e.Z) / R; var nH = -new Vector3d(e.X, e.Y, 0) / rho;   // наружу из материала
            var c = e - (nS + nH).Normalized() * r * Math.Sqrt(2);
            for (int it = 0; it < 60; it++)
            {
                double yz = Math.Sqrt(c.Y * c.Y + c.Z * c.Z), xy = Math.Sqrt(c.X * c.X + c.Y * c.Y);
                double f1 = yz - (R - r), f2 = xy - (rho + r), f3 = Vector3d.Dot(c - e, t);
                var g1 = new Vector3d(0, c.Y / yz, c.Z / yz); var g2 = new Vector3d(c.X / xy, c.Y / xy, 0);
                // 3×3 по правилу Крамера
                var m0 = g1; var m1 = g2; var m2 = t;
                double det = Vector3d.Dot(m0, Vector3d.Cross(m1, m2));
                var b = new Vector3d(-f1, -f2, -f3);
                var dx = (Vector3d.Cross(m1, m2) * b.X + Vector3d.Cross(m2, m0) * b.Y + Vector3d.Cross(m0, m1) * b.Z) / det;
                c += dx; if (dx.Length < 1e-15) break;
            }
            return c;
        }
        (Vector3d CC, double Rc, Vector3d QA, Vector3d QB) Sec(double phi)
        {
            var c = C(phi); double h = 1e-5;
            var dc = (-C(phi + 2 * h) + 8 * C(phi + h) - 8 * C(phi - h) + C(phi - 2 * h)) / (12 * h);
            // dr/dφ: закон по длине s от конца S; s растёт по φ при dir > 0
            double r = Rad(phi), k = r * ((rE - rS) / L * dE(phi).Length * dir) / dc.LengthSquared;
            var cc = c - dc * k; double rc = Math.Sqrt(r * r - k * k * dc.LengthSquared);
            double yz = Math.Sqrt(c.Y * c.Y + c.Z * c.Z); var qa = new Vector3d(c.X, c.Y * R / yz, c.Z * R / yz);
            double ang = Math.Atan2(c.Y, c.X); var qb = new Vector3d(rho * Math.Cos(ang), rho * Math.Sin(ang), c.Z);
            return (cc, rc, qa, qb);
        }
        double[] gx = { -0.9739065285171717, -0.8650633666889845, -0.6794095682990244, -0.4333953941292472, -0.1488743389816312, 0.1488743389816312, 0.4333953941292472, 0.6794095682990244, 0.8650633666889845, 0.9739065285171717 };
        double[] gw = { 0.0666713443086881, 0.1494513491505806, 0.2190863625159820, 0.2692667193099963, 0.2955242247147529, 0.2955242247147529, 0.2692667193099963, 0.2190863625159820, 0.1494513491505806, 0.0666713443086881 };
        double total = 0;
        void Patch(Func<double, double, Vector3d> P, Func<Vector3d, double, Vector3d> outDir)
        {
            double h = 1e-6; int? orient = null;
            for (int j = 0; j < nL; j++)
                for (int b = 0; b < gx.Length; b++)
                {
                    double lam = (j + (gx[b] + 1) / 2) / nL, wl = gw[b] / 2 / nL;
                    double G(double phi) => Vector3d.Dot(n, P(phi, lam)) - off;
                    // промежутки φ у ребра (его концы ± 0.6 рад: продление ленты за торец) с G ≥ 0; дальше от ребра продолженный закон
                    // и центры шаров не определены
                    const int M = 256; double lo0 = Math.Min(phiS, phiEnd) - 0.6, span = Math.Abs(phiEnd - phiS) + 1.2; var cuts = new List<double>(); double gPrev = G(lo0);
                    for (int i = 1; i <= M; i++)
                    {
                        double x1 = lo0 + span * i / M, g1 = G(x1);
                        if (Math.Sign(g1) != Math.Sign(gPrev))
                        {
                            double lo = lo0 + span * (i - 1) / M, hi = x1, glo = gPrev;
                            for (int it = 0; it < 80; it++) { double md = (lo + hi) / 2, gm = G(md); if (Math.Sign(gm) == Math.Sign(glo)) { lo = md; glo = gm; } else hi = md; }
                            cuts.Add((lo + hi) / 2);
                        }
                        gPrev = g1;
                    }
                    var pts = new List<double> { lo0 }; pts.AddRange(cuts); pts.Add(lo0 + span);
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        double A = pts[i], B = pts[i + 1];
                        if (!(G((A + B) / 2) > 0)) continue;
                        for (int sIdx = 0; sIdx < nSub; sIdx++)
                            for (int q = 0; q < gx.Length; q++)
                            {
                                double phi = A + (B - A) * (sIdx + (gx[q] + 1) / 2) / nSub, wphi = gw[q] / 2 * (B - A) / nSub;
                                var p = P(phi, lam);
                                var du = (P(phi + h, lam) - P(phi - h, lam)) / (2 * h);
                                var dv = (P(phi, Math.Min(1, lam + h)) - P(phi, Math.Max(0, lam - h))) / (Math.Min(1, lam + h) - Math.Max(0, lam - h));
                                var nn = Vector3d.Cross(du, dv);
                                orient ??= Vector3d.Dot(nn, outDir(p, phi)) < 0 ? -1 : 1;
                                total += Vector3d.Dot(p - p0, nn) * orient.Value / 3 * wphi * wl;
                            }
                    }
                }
        }
        Patch((phi, lam) =>
        {
            var (cc, rc, qa, qb) = Sec(phi);
            var aa = (qa - cc).Normalized(); var bb = (qb - cc).Normalized();
            double ang = Math.Acos(Math.Clamp(Vector3d.Dot(aa, bb), -1, 1)); var bp = (bb - aa * Vector3d.Dot(aa, bb)).Normalized();
            return cc + (aa * Math.Cos(ang * lam) + bp * Math.Sin(ang * lam)) * rc;
        }, (p, phi) => C(phi) - p);
        Patch((phi, lam) =>
        {
            var e = Ep(phi); var c = Sec(phi).QA;
            double te = Math.Atan2(e.Z, e.Y), tc = Math.Atan2(c.Z, c.Y), t = te + (tc - te) * lam, x = e.X + (c.X - e.X) * lam;
            return new Vector3d(x, R * Math.Cos(t), R * Math.Sin(t));
        }, (p, phi) => new Vector3d(0, p.Y, p.Z));
        Patch((phi, lam) =>
        {
            var e = Ep(phi); var c = Sec(phi).QB;
            double pe = Math.Atan2(e.Y, e.X), pc = Math.Atan2(c.Y, c.X); double dp = pc - pe; while (dp > Math.PI) dp -= 2 * Math.PI; while (dp < -Math.PI) dp += 2 * Math.PI;
            double ps = pe + dp * lam, z = e.Z + (c.Z - e.Z) * lam;
            return new Vector3d(rho * Math.Cos(ps), rho * Math.Sin(ps), z);
        }, (p, phi) => new Vector3d(-p.X, -p.Y, 0));
        return Math.Abs(total);
    }

    static void Krn06VariableCurvedChecks()
    {
        Check("KRN06-VARIABLE-RADIUS-CURVED", () =>
        {
            // ПЕРЕМЕННЫЙ РАДИУС НА РЕБРЕ МЕЖДУ КРИВЫМИ ГРАНЯМИ (06.14): кромка отверстия в половине вала (концы на плоском срезе),
            // r от r0 у начала ссылки до r1 у конца, линейно по длине ребра. Сечения — характеристические окружности огибающей
            // шаров. Эталон — RollingBallVarCut (свой Ньютон центров, своя длина дуги, своя окружность огибающей). Постоянный
            // закон (r0 = r1 ≈ 1) — проверка самого эталона против RollingBallCuspCut.
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("P3D_OPENDBG") == "1") DirectRound.Debug = m => Console.WriteLine("    dr: " + m);
            {
                double a = RollingBallVarCut(20, 4, 1, 1, 0, Math.PI, 0, 0), b = RollingBallCuspCut(20, 4, 1, 0, 0);
                if (!(Math.Abs(a - b) <= 1e-8 * b)) fails.Add($"reference: constant law {a:R} vs {b:R}");
                Console.WriteLine($"  reference self-check: {a:R} vs {b:R}");
            }
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            foreach (var (name, rho, r0, r1, off) in new[] { ("half d8 r1→2", 4.0, 1.0, 2.0, 0.0), ("half d8 r2→0.8", 4.0, 2.0, 0.8, 0.0), ("offset 1.5 d8 r0.7→1.6", 4.0, 0.7, 1.6, 1.5), ("half d30 r1→2.5", 15.0, 1.0, 2.5, 0.0) })
            {
                var b = new List<TSolid> { Shaft() }; var w = new List<string>();
                BooleanOp.Subtract(b, Box(-60, off - 60, -60, 120, 60, 120), w);
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), rho, 80), w);
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
                var edges = b[0].Edges.Where(e => e.Curve is not Parametric3D.Kernel.Curves.LineCurve3 && e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) is var m && m.Z > 5 && Math.Abs(Math.Sqrt(m.X * m.X + m.Y * m.Y) - rho) < 1e-6).ToList();
                if (edges.Count != 1) { fails.Add($"{name}: edges {edges.Count}"); continue; }
                var rf = EdgeRef.Of(edges[0]);
                long built0 = DirectRound.OpenBuilt;
                try { FilletOp.RoundEdgesVariable(b, new List<EdgeRef> { rf }, r0, r1, new List<string>()); }
                catch (Exception e) { fails.Add($"{name}: {e.GetType().Name} {e.Message}"); continue; }
                if (DirectRound.OpenBuilt == built0) { fails.Add($"{name}: curved builder not used"); continue; }
                var s = b[0];
                // концы ребра — при y = off: φ ∈ (φa, π − φa), φa = asin(off/ρ); S — конец ссылки
                double phiA = Math.Asin(off / rho), phiB = Math.PI - phiA;
                var Ea = new Vector3d(rho * Math.Cos(phiA), off, Math.Sqrt(400 - off * off));
                bool sAtA = (rf.Start - Ea).Length < (rf.End - Ea).Length;
                double want = RollingBallVarCut(20, rho, r0, r1, sAtA ? phiA : phiB, sAtA ? phiB : phiA, 0, off);
                double dv = v0 - (AnalyticVolume.Of(s) ?? double.NaN);
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {s.Guarantee}");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"{name}: invalid");
                var fac = GeometryAudit.Faceted(s); if (fac != (0, 0)) fails.Add($"{name}: faceted {fac}");
                if (!(Math.Abs(dv - want) <= 2e-6 * want + 1e-7)) fails.Add($"{name}: dV {dv:R} vs reference {want:R}");
                Console.WriteLine($"  {name}: dV {dv:R} reference {want:R} ({(dv - want) / want:E1})");
            }
            DirectRound.Debug = null;
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    static void Krn06BlockedEndChecks()
    {
        Check("KRN06-BLOCKED-END-ADDITIVE", () =>
        {
            // УПОР В СТЕНКУ (06.5): ребро плоского среза верха вала упирается в стенку уступа (за концом — материал). Лента
            // обрезается стенкой, ребро стенки и цилиндра ПРОДЛЕВАЕТСЯ до точки касания. Тождество: срез x ≥ −25 (упор слева) +
            // срез x ≤ −25 (упор справа) снимают ровно столько, сколько срез по всей длине (лента — та же огибающая на тех же
            // поверхностях). Скругление r2 и фаска 1.5.
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("P3D_OPENDBG") == "1") DirectRound.Debug = m => Console.WriteLine("    dr: " + m);
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            double ang = -Math.Atan(0.02) * 180 / Math.PI;
            (double dV, double err)? Case(string name, double x0, double len, bool round)
            {
                var b = new List<TSolid> { Shaft() }; var w = new List<string>();
                BooleanOp.Subtract(b, TransformOp.Copy(Box(x0, -30, 18, len, 60, 30), TransformOp.Rotate(new Vector3d(0, 0, 18), Vector3d.UnitY, ang)), w);
                var e0 = AnalyticVolume.OfWithError(b[0]);
                var es = b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) is var m && m.Z > 15 && m.Y > 3).ToList();
                if (Environment.GetEnvironmentVariable("P3D_OPENDBG") == "1" && es.Count == 1)
                    foreach (var vp in new[] { es[0].V0.P, es[0].V1.P })
                        foreach (var ed in b[0].Edges.Where(x => (x.V0.P - vp).Length < 1e-7 || (x.V1.P - vp).Length < 1e-7))
                            Console.WriteLine($"    {name} at {vp:0.###}: {ed.Curve.GetType().Name} {ed.V0.P:0.###}->{ed.V1.P:0.###} faces {string.Join(",", ed.CoEdges.Select(c => c.Loop?.Face?.Id + ":" + c.Loop?.Face?.Tag))}");
                if (es.Count != 1) { fails.Add($"{name}: edges {es.Count}"); return null; }
                long n0 = DirectRound.OpenBuilt, c0 = DirectRound.CurvedChamfers;
                try
                {
                    if (round) FilletOp.RoundEdges(b, es.Select(EdgeRef.Of).ToList(), 2, new List<string>());
                    else FilletOp.ChamferEdges(b, es.Select(EdgeRef.Of).ToList(), 1.5, 1.5, new List<string>());
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); return null; }
                if (DirectRound.OpenBuilt == n0 && DirectRound.CurvedChamfers == c0) { fails.Add($"{name}: direct builder not used"); return null; }
                var s = b[0];
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(s).IsValid) fails.Add($"{name}: {s.Guarantee} {SolidValidator.Validate(s).Summary()}");
                var e1 = AnalyticVolume.OfWithError(s);
                if (e0 == null || e1 == null) { fails.Add($"{name}: no volume"); return null; }
                return (e0.Value.Volume - e1.Value.Volume, e0.Value.QuadratureError + e1.Value.QuadratureError);
            }
            foreach (bool round in new[] { true, false })
            {
                string k = round ? "round r2" : "chamfer 1.5";
                var full = Case($"{k} full", -60, 120, round); var left = Case($"{k} blocked left", -25, 85, round); var right = Case($"{k} blocked right", -60, 35, round);
                if (full == null || left == null || right == null) continue;
                double sum = left.Value.dV + right.Value.dV, tol = 1e-7 * full.Value.dV + full.Value.err + left.Value.err + right.Value.err;
                if (!(Math.Abs(sum - full.Value.dV) <= tol)) fails.Add($"{k}: left {left.Value.dV:R} + right {right.Value.dV:R} = {sum:R} vs full {full.Value.dV:R} (tol {tol:E1})");
                Console.WriteLine($"  {k}: left {left.Value.dV:R} + right {right.Value.dV:R} = {sum:R}, full {full.Value.dV:R} ({(sum - full.Value.dV) / full.Value.dV:E1}, tol {tol:E1})");
            }
            DirectRound.Debug = null;
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
