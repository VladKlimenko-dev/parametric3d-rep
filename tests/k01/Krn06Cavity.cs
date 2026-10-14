using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// ∫₀ʳ h(y)·c(y) dy, h(y) = r − √(r² − (r − y)²) — глубина снятого уголка скругления на расстоянии y от ребра, c(y) — длина
    /// сечения полости прямой y = const (составная квадратура Гаусса, 20 узлов на 400 кусках, отдельно до и после изломов c).
    /// </summary>
    static double CornerOverlap(double r, Func<double, double> chord, params double[] cuts)
    {
        double[] gx = { -0.9931285991850949, -0.9639719272779138, -0.9122344282513259, -0.8391169718222188, -0.7463319064601508, -0.6360536807265150, -0.5108670019508271, -0.3737060887154195, -0.2277858511416451, -0.0765265211334973, 0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150, 0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949 };
        double[] gw = { 0.0176140071391521, 0.0406014298003869, 0.0626720483341091, 0.0832767415767048, 0.1019301198172404, 0.1181945319615184, 0.1316886384491766, 0.1420961093183820, 0.1491729864726037, 0.1527533871307258, 0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184, 0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521 };
        var pts = new List<double> { 0, r }; pts.AddRange(cuts.Where(c => c > 0 && c < r)); pts.Sort();
        double sum = 0;
        for (int k = 0; k + 1 < pts.Count; k++)
        {
            double a = pts[k], b = pts[k + 1]; const int n = 400;
            for (int i = 0; i < n; i++)
            {
                double lo = a + (b - a) * i / n, hi = a + (b - a) * (i + 1) / n, m = (lo + hi) / 2, h = (hi - lo) / 2;
                for (int q = 0; q < gx.Length; q++) { double y = m + h * gx[q]; sum += gw[q] * h * (r - Math.Sqrt(Math.Max(0, r * r - (r - y) * (r - y)))) * chord(y); }
            }
        }
        return sum;
    }

    static void Krn06CavityChecks()
    {
        Check("KRN06-EVENT-RADIUS-TOO-LARGE", () =>
        {
            // 06.5: СОБЫТИЕ «РАДИУС БОЛЬШЕ ГРАНИ»: верхнее переднее ребро плиты высотой 10 со скруглением r 12 (полоса не помещается
            // на переднюю грань), фаска 12; ребро короче радиуса (выступ 3×40×3 у плиты, его верхнее ребро скругляется r 5).
            // Прямой построитель отказывает с причиной; итог операции — отказ, а не негодное или «не то» тело (как у OpenCascade).
            var fails = new List<string>();
            foreach (var (name, make, pick, rr, ch) in new (string, Func<TSolid>, Func<TEdge, bool>, double, bool)[]
            {
                ("plate h10, round r12", () => Box(0, 0, 0, 60, 40, 10), e => Math.Abs(e.V0.P.Y) < 1e-9 && Math.Abs(e.V1.P.Y) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9, 12.0, false),
                ("plate h10, chamfer 12", () => Box(0, 0, 0, 60, 40, 10), e => Math.Abs(e.V0.P.Y) < 1e-9 && Math.Abs(e.V1.P.Y) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9, 12.0, true),
                ("rib 3×3, round r5", () => { var b = new List<TSolid> { Box(0, 0, 0, 60, 40, 10) }; BooleanOp.Unite(b, Box(10, 10, 10, 3, 20, 3), new List<string>()); return b[0]; }, e => Math.Abs(e.V0.P.X - 13) < 1e-9 && Math.Abs(e.V1.P.X - 13) < 1e-9 && Math.Abs(e.V0.P.Z - 13) < 1e-9 && Math.Abs(e.V1.P.Z - 13) < 1e-9, 5.0, false),
            })
            {
                var bl = new List<TSolid> { make() }; var es = bl[0].Edges.Where(pick).ToList();
                if (es.Count != 1) { fails.Add($"{name}: edges {es.Count}"); continue; }
                string? reason = null; var prevDbg = DirectRound.Debug; DirectRound.Debug = m => { if (m.StartsWith("declined")) reason = m; };
                try
                {
                    if (ch) FilletOp.ChamferEdges(bl, es.Select(EdgeRef.Of).ToList(), rr, rr, new List<string>());
                    else FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), rr, new List<string>());
                    fails.Add($"{name}: accepted ({bl[0].Guarantee}, valid {SolidValidator.Validate(bl[0]).IsValid})");
                }
                catch (Exception ex) { Console.WriteLine($"  {name}: refused ({ex.GetType().Name}); direct: {reason}"); }
                finally { DirectRound.Debug = prevDbg; }
                if (reason == null || !(reason.Contains("too large") || reason.Contains("radius") || reason.Contains("does not fit"))) fails.Add($"{name}: the direct builder's reason is not about the size: {reason}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN06-CAVITY-IN-STRIP", () =>
        {
            // 06.5/06.9: ОТВЕРСТИЕ ИЛИ КАРМАН В ПОЛОСЕ СКРУГЛЕНИЯ. Плита 60×40×10, верхнее переднее ребро (y = 0, z = 10), r = 3;
            // у ребра — сквозное отверстие R3 (центр y = 4.5: контур заходит в полосу y < 3 до 1.5), глухое отверстие (глубина 5)
            // и прямоугольный карман (x 20…40, y ≥ 1.5, глубина 4). Прямой построитель: полость вынимается, ребро скругляется,
            // полость вычитается обратно. Эталон: V = V₀ − (1 − π/4)r²·60 + V∩, V∩ — объём уголка внутри полости (её там уже
            // нет): ∫ h(y)·c(y) dy (квадратура; у кармана — замкнутая форма). Тело точное и годное, лента пересечена стенкой.
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("P3D_CAVDBG") == "1") DirectRound.Debug = m => { if (m.StartsWith("declined") || m.StartsWith("cavity")) Console.WriteLine("    dr " + m); };
            const double r = 3, R = 3, yc = 4.5;
            double Chord(double y) => Math.Abs(y - yc) < R ? 2 * Math.Sqrt(R * R - (y - yc) * (y - yc)) : 0;
            double vHole = CornerOverlap(r, Chord, yc - R);
            double t1 = r - 1.5; double vPocket = 20 * (r * t1 - (t1 / 2 * Math.Sqrt(r * r - t1 * t1) + r * r / 2 * Math.Asin(t1 / r)));
            foreach (var (name, make, inter) in new (string, Func<TSolid>, double)[]
            {
                ("through hole R3", () => { var b = new List<TSolid> { Box(0, 0, 0, 60, 40, 10) }; BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, yc, -5), R, 20), new List<string>()); return b[0]; }, vHole),
                ("blind hole R3 depth 5", () => { var b = new List<TSolid> { Box(0, 0, 0, 60, 40, 10) }; BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, yc, 5), R, 10), new List<string>()); return b[0]; }, vHole),
                ("pocket 20×8.5 depth 4", () => { var b = new List<TSolid> { Box(0, 0, 0, 60, 40, 10) }; BooleanOp.Subtract(b, Box(20, 1.5, 6, 20, 8.5, 10), new List<string>()); return b[0]; }, vPocket),
            })
            {
                var src = make(); double v0 = AnalyticVolume.Of(src) ?? double.NaN;
                var bl = new List<TSolid> { src }; long c0 = DirectRound.CavityDetours; var w = new List<string>();
                var es = src.Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Y) < 1e-9 && Math.Abs(e.V1.P.Y) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9).ToList();
                if (es.Count != 1) { fails.Add($"{name}: edges {es.Count}"); continue; }
                try { FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), r, w); }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                double want = v0 - (1 - Math.PI / 4) * r * r * 60 + inter;
                Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {w.LastOrDefault()}");
                Log(new { test = "KRN06-CAVITY-IN-STRIP", name, volume = v, want, rel = (v - want) / want });
                if (DirectRound.CavityDetours == c0) fails.Add($"{name}: no cavity detour");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
            }
            // ФАСКА 2 того же ребра с тем же сквозным отверстием: снятое — треугольник d²/2 по длине, без его части в отверстии
            // (∫₀^d (d − y)·c(y) dy — клин глубиной d − y)
            {
                const double d = 3;
                var src = new List<TSolid> { Box(0, 0, 0, 60, 40, 10) }; BooleanOp.Subtract(src, PrimitiveOp.Cylinder(new Vector3d(30, yc, -5), R, 20), new List<string>());
                double v0 = AnalyticVolume.Of(src[0]) ?? double.NaN; long c0 = DirectRound.CavityDetours;
                var es = src[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Y) < 1e-9 && Math.Abs(e.V1.P.Y) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9).ToList();
                double inter = 0; { int n = 4000; for (int i = 0; i < n; i++) { double y0 = d * i / n, y1 = d * (i + 1) / n; double[] gx = { -0.7745966692414834, 0, 0.7745966692414834 }, gw = { 5.0 / 9, 8.0 / 9, 5.0 / 9 }; for (int q = 0; q < 3; q++) { double y = (y0 + y1) / 2 + (y1 - y0) / 2 * gx[q]; inter += gw[q] * (y1 - y0) / 2 * (d - y) * Chord(y); } } }
                try
                {
                    FilletOp.ChamferEdges(src, es.Select(EdgeRef.Of).ToList(), d, d, new List<string>());
                    double v = AnalyticVolume.Of(src[0]) ?? double.NaN, want = v0 - d * d / 2 * 60 + inter;
                    Console.WriteLine($"  chamfer 3, through hole: V {v:R} want {want:R} ({(v - want) / want:E1})");
                    if (DirectRound.CavityDetours == c0) fails.Add("chamfer: no cavity detour");
                    if (src[0].Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(src[0]).IsValid) fails.Add($"chamfer: {src[0].Guarantee}");
                    if (!(Math.Abs(v - want) <= 1e-7 * want)) fails.Add($"chamfer: V {v:R} vs {want:R}");
                }
                catch (Exception ex) { fails.Add($"chamfer: {ex.GetType().Name} {ex.Message}"); }
            }
            // ВЫСТУП (бобышка R2 на верхней грани, заходит в полосу): шар упёрся бы в стенку — обход полости не берётся; итог
            // прежнего пути — годное тело или отказ, но не негодное
            {
                var src = new List<TSolid> { Box(0, 0, 0, 60, 40, 10) }; BooleanOp.Unite(src, PrimitiveOp.Cylinder(new Vector3d(30, 4, 10), 2, 5), new List<string>());
                long c0 = DirectRound.CavityDetours;
                var es = src[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Y) < 1e-9 && Math.Abs(e.V1.P.Y) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9).ToList();
                try
                {
                    FilletOp.RoundEdges(src, es.Select(EdgeRef.Of).ToList(), r, new List<string>());
                    if (!SolidValidator.Validate(src[0]).IsValid) fails.Add("boss: an invalid body was accepted");
                    Console.WriteLine($"  boss in the strip: {src[0].Guarantee} (previous path)");
                }
                catch (Exception ex) { Console.WriteLine($"  boss in the strip: refused ({ex.GetType().Name})"); }
                if (DirectRound.CavityDetours != c0) fails.Add("boss: taken as a cavity");
            }
            DirectRound.Debug = null;
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
