using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn06BoolBandChecks()
    {
        Check("KRN06-BOOLEAN-OVER-FORMULA-FACES", () =>
        {
            // 06.12/g.08: БУЛЕВА НАД ГРАНЯМИ ПО ФОРМУЛЕ — лента катящегося шара (вал R20 с отверстием d8, кромка r1), грани точной
            // протяжки, грани NURBS-лофта, касающегося плиты. Каждая булева — точная (ExactAnalytic), тело годно; эталон —
            // тождество включения-исключения на тех же точных операциях: V(A ∪ B) + V(A ∩ B) = V(A) + V(B), V(A − B) =
            // V(A) − V(A ∩ B) (грани по формуле представлены NURBS в пределах 0.2 точности модели — до 1e-8 отн.).
            var fails = new List<string>();
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            TSolid HoleFillet()
            {
                var b = new List<TSolid> { Shaft() };
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
                var edges = b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt(0.5).Z > 5).ToList();
                FilletOp.RoundEdges(b, edges.Select(EdgeRef.Of).ToList(), 1, new List<string>());
                return b[0];
            }
            TSolid Sweep()
            {
                var path = new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(10, 15, 5), new Vector3d(10, 15, 40), new Vector3d(50, 15, 40), new Vector3d(50, 15, 5) });
                var sec = ProfileBuilder.Build(new Frame3(new Vector3d(10, 15, 5), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(10, 15, 5), Vector3d.UnitX, Vector3d.UnitY), 3, 0, 2 * Math.PI) });
                return SweepOp.Build(sec, path, 1);
            }
            TSolid Loft()
            {
                Profile Sq(double s, double z) { Vector3d P(double x, double y) => new(x, y, z); return ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.LineCurve3(P(-s, -s), P(s, -s)), new Parametric3D.Kernel.Curves.LineCurve3(P(s, -s), P(s, s)), new Parametric3D.Kernel.Curves.LineCurve3(P(s, s), P(-s, s)), new Parametric3D.Kernel.Curves.LineCurve3(P(-s, s), P(-s, -s)) }); }
                Profile Ci(double r, double z) => ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), r, 0, 2 * Math.PI) });
                return LoftOp.Build(new[] { Sq(15, 5), Ci(10, 35) }, 64, false, null, 0.5, 0.5);
            }
            double V(TSolid s) => AnalyticVolume.Of(s) ?? double.NaN;
            List<TSolid>? Op(TSolid a, TSolid b, string kind, string name)
            {
                var l = new List<TSolid> { TopologyClone.Deep(a) }; var w = new List<string>();
                if (kind == "u") BooleanOp.Unite(l, TopologyClone.Deep(b), w); else if (kind == "s") BooleanOp.Subtract(l, TopologyClone.Deep(b), w); else BooleanOp.Intersect(l, TopologyClone.Deep(b), w);
                if (l.Count == 0) { fails.Add($"{name} ({kind}): empty"); return null; }
                foreach (var x in l)
                    if (x.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(x).IsValid) { fails.Add($"{name} ({kind}): {x.Guarantee} {SolidValidator.Validate(x).Summary()} | {string.Join(" | ", w.Take(2))}"); return null; }
                return l;
            }
            double VS(List<TSolid> l) => l.Sum(V);
            void Check2(string name, TSolid a, TSolid b, bool touching, double vaBox = double.NaN, double vbBox = double.NaN)
            {
                // бруски вспомогательной функции Box — без точного объёма по граням: их объём — по размерам
                double va = double.IsNaN(vaBox) ? V(a) : vaBox, vb = double.IsNaN(vbBox) ? V(b) : vbBox;
                var u = Op(a, b, "u", name); if (u == null) return;
                double vu = VS(u);
                if (touching)
                {
                    Console.WriteLine($"  {name}: V(A∪B) {vu:R}, V(A)+V(B) {va + vb:R} ({(vu - va - vb) / (va + vb):E1})");
                    if (!(Math.Abs(vu - va - vb) <= 1e-8 * (va + vb))) fails.Add($"{name}: V(A∪B) {vu:R} vs {va + vb:R}");
                    return;
                }
                var i = Op(a, b, "i", name); if (i == null) return;
                var s = Op(a, b, "s", name); if (s == null) return;
                double vi = VS(i), vs = VS(s);
                Console.WriteLine($"  {name}: ∪ {vu:R} ∩ {vi:R} − {vs:R}; incl-excl {(vu + vi - va - vb) / (va + vb):E1}, difference {(vs - va + vi) / va:E1}");
                if (!(Math.Abs(vu + vi - va - vb) <= 1e-8 * (va + vb))) fails.Add($"{name}: V(A∪B) + V(A∩B) − V(A) − V(B) = {vu + vi - va - vb:E2}");
                if (!(Math.Abs(vs - (va - vi)) <= 1e-8 * va)) fails.Add($"{name}: V(A−B) − (V(A) − V(A∩B)) = {vs - va + vi:E2}");
            }
            Check2("rolling-ball band (hole d8 r1 in shaft R20) and a slot block", HoleFillet(), Box(-1, -30, 15, 2, 60, 10), false, double.NaN, 2 * 60 * 10);
            Check2("exact sweep (Bezier arch, circle r3) and a plate", Box(0, 0, 0, 60, 30, 10), Sweep(), false, 60 * 30 * 10);
            { var lf = Loft(); Console.WriteLine($"  loft alone: V {V(lf):R} {lf.Guarantee} {SolidValidator.Validate(lf).Summary()}"); }
            Check2("NURBS loft (square → circle, tangents) standing on a plate", Box(-25, -25, 0, 50, 50, 5), Loft(), true, 50 * 50 * 5);
            {
                // зеркальные копии плиты с ручкой — касаются по плоскости x = 0 (рёбра верха — кривые «NURBS × плоскость»)
                var l = new List<TSolid> { Box(0, 0, 0, 60, 30, 10) }; BooleanOp.Unite(l, Sweep(), new List<string>());
                var a = l[0]; var b = TransformOp.Copy(a, TransformOp.Mirror(Vector3d.Zero, Vector3d.UnitX));
                Check2("plate with a sweep handle and its mirror copy (touching)", a, b, true);
            }
            {
                // крышка с переменным скруглением верхнего контура (спад радиуса через стык отрезка с дугой — шов лент; рельсы-сплайны
                // у края плоского верха) и цилиндр отверстия, чей верх совпадает с верхом крышки: события дуги с краем-сплайном
                // доказываются разделением оболочек (P3D_BNDSPLINE)
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 8, new List<string>());
                var top = bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - 20) < 1e-7).Select(EdgeRef.Of).ToList();
                FilletOp.RoundEdgesLaw(bl, top, new[] { (0.0, 3.0), (0.1, 3.0), (0.15, 3.5), (0.2, 3.5), (0.27, 3.0), (1.0, 3.0) }, new List<string>());
                if (Environment.GetEnvironmentVariable("P3D_ROUTEDBG") == "1") BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   route: " + m);
                Check2("lid with a radius-law top loop and a hole cylinder flush with the top", bl[0], PrimitiveOp.Cylinder(new Vector3d(30, 20, 0), 5, 20), false, double.NaN, Math.PI * 25 * 20);
                BooleanOp.AnalyticRouteDebug = null;
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    static void BezierDecompositionChecks()
    {
        Check("KRN04-BEZIER-DECOMPOSITION-LONG-SPLINE", () =>
        {
            // Разложение сплайна в тысячи узлов на куски Безье (события кривой — по кускам): число кусков = числу промежутков,
            // у каждого — степень + 1 опорных точек, кусок в своих границах совпадает с кривой до 1e-12 отн.; быстро.
            var rnd = new Random(7); int n = 3000;
            var cps = new List<Vector3d>(); double x = 0;
            for (int i = 0; i < n; i++) { x += 0.5 + rnd.NextDouble(); cps.Add(new Vector3d(x, Math.Sin(0.01 * i) * 20 + rnd.NextDouble(), Math.Cos(0.013 * i) * 10)); }
            var knots = new List<double> { 0, 0, 0, 0 }; double k = 0;
            for (int i = 0; i < n - 4; i++) { k += 0.2 + rnd.NextDouble(); knots.Add(k); }
            k += 1; knots.AddRange(new[] { k, k, k, k });
            var sp = new Parametric3D.Kernel.Curves.SplineCurve3(cps, 3, null, knots).WithUnitKnots();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var segs = sp.ToBezierSegments(); long ms = sw.ElapsedMilliseconds;
            var fails = new List<string>();
            if (segs.Count != n - 3) fails.Add($"pieces {segs.Count} (want {n - 3})");
            if (segs.Any(sg => sg.Piece.ControlPoints.Count != 4)) fails.Add("a piece is not a single Bezier span");
            double worst = 0, sc = cps.Max(q => q.Length);
            foreach (var (pc, from, to) in segs.Where((_, i) => i % 37 == 0))
                foreach (double f in new[] { 0.0, 0.31, 0.77, 1.0 })
                    worst = Math.Max(worst, (pc.PointAt(f) - sp.PointAt(from + (to - from) * f)).Length / sc);
            Console.WriteLine($"  {segs.Count} pieces in {ms} ms, worst deviation {worst:E1} (relative)");
            if (!(worst < 1e-12)) fails.Add($"deviation {worst:E1}");
            if (ms > 2000) fails.Add($"slow: {ms} ms");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    static void Krn06EqualCylChecks()
    {
        Check("KRN06-EQUAL-RADIUS-TEE", () =>
        {
            // 06.12: ТРОЙНИК ТРУБ ОДНОГО РАДИУСА — вал R20 (ось X) и бобышка R20 (ось Z): ребро стыка — две половины эллипса между
            // сёдлами (поверхности там касаются). Скругление r — две ленты-«линзы», стянутые в сёдлах. Эталон объёма —
            // независимая квадратура «уголка» катящегося шара (RollingBallCusp: хребет |(y,z)| = R + r, |(x,y)| = ρ + r при
            // ρ = R). ExactAnalytic, годность, точный STEP.
            var fails = new List<string>();
            foreach (double r in new[] { 2.0, 4.0 })
            {
                string name = $"tee R20/R20 r{r}";
                var b = new List<TSolid> { TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)) };
                if (Environment.GetEnvironmentVariable("P3D_ROUTEDBG") == "1") { BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   route: " + m); Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("    [fs] " + m); }
                var wu = new List<string>(); BooleanOp.Unite(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 20, 40), wu);
                Console.WriteLine($"  union: {b[0].Guarantee}, {b[0].Faces.Count()} faces | {string.Join(" | ", wu.Take(3))}");
                if (b[0].Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name}: the union is {b[0].Guarantee} ({string.Join(" | ", wu.Take(2))})"); continue; }
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
                var edges = b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt(0.5).Z > 1).ToList();
                var w = new List<string>(); long c0 = DirectRound.EqualCylBands;
                try { FilletOp.RoundEdges(b, edges.Select(EdgeRef.Of).ToList(), r, w); }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                var s = b[0]; double v = AnalyticVolume.Of(s) ?? double.NaN, want = RollingBallCusp(20, 20, r, false);
                Console.WriteLine($"  {name}: {edges.Count} edges, dV {v - v0:R} want {want:R} ({(v - v0 - want) / want:E1}) {s.Guarantee} | {string.Join(" | ", w.Take(2))}");
                if (DirectRound.EqualCylBands == c0) fails.Add($"{name}: not the equal-radius builder");
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(s).IsValid) fails.Add($"{name}: {s.Guarantee} {SolidValidator.Validate(s).Summary()}");
                if (!(Math.Abs(v - v0 - want) <= 2e-6 * want)) fails.Add($"{name}: dV {v - v0:R} vs {want:R}");
                if (Environment.GetEnvironmentVariable("P3D_EQCYLDBG") == "1")
                    foreach (var f in s.Faces) if (Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface band)
                    {
                        var sp = Parametric3D.Kernel.Export.StepExact.BandSpan(f, band);
                        var fit = sp is { } q ? Parametric3D.Kernel.Surfaces.BandNurbs.Fit(band, q.Ua, q.Ub, 1e-6) : null;
                        Console.WriteLine($"   band proc {band.IsProcedural} span {sp} fit {(fit == null ? "null" : fit.Value.Deviation.ToString("E1"))} range {band.UMin}..{band.UMax}");
                    }
                var sw = new StringWriter(); var ws = new List<string>();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, "tee", ws) || ws.Any(x => x.Contains("not written exactly") || x.Contains("intersection curve is written"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Take(2))})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
