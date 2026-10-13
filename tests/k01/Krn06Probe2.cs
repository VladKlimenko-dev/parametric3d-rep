using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Правильная n-угольная пирамида (вершина степени n над (20, 20, 40)): брусок минус n резцов, плоскости
    /// x' = 40 − z·tg — повёрнуты вокруг вертикали через (20, 20) на 360°/n; slope2 ≠ 0.5 — вторая грань с другим уклоном
    /// (шара, касающегося всех граней, нет).</summary>
    internal static TSolid PyramidBody(int n, double slope2 = 0.5)
    {
        var bl = new List<TSolid> { Box(-30, -30, 0, 100, 100, 60) };
        for (int q = 0; q < n; q++)
        {
            double sl = q == 1 ? slope2 : 0.5, deg = Math.Atan(sl) * 180 / Math.PI;
            // плоскость через (20 + 20·0.5/sl, ·, 0)… проходит через вершину (20, 20, 40): x = 20 + (40 − z)·sl
            var c = TransformOp.Copy(Box(0, -100, -50, 200, 300, 200), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, -deg));
            c = TransformOp.Copy(c, TransformOp.Move(new Vector3d(20 + 40 * sl, -20, 0)));
            BooleanOp.Subtract(bl, TransformOp.Copy(c, TransformOp.Rotate(new Vector3d(20, 20, 0), Vector3d.UnitZ, 360.0 * q / n)), new List<string>());
        }
        return bl[0];
    }
    /// <summary>Объём правильной пирамиды (основание — n-угольник с вписанной окружностью a = 20, высота H = 40) со скруглением
    /// ВСЕХ рёбер радиусом r — по формуле Штейнера: тело = Q ⊕ шар r, Q — внутреннее параллельное тело (у тела со вписанной
    /// сферой — гомотетия с коэффициентом (ρ − r)/ρ от её центра): V = V(Q) + S(Q)·r + M(Q)·r² + 4πr³/3,
    /// M = ½ Σ L_e·(угол между наружными нормалями граней ребра).</summary>
    internal static double PyramidSteiner(int n, double r, double a0 = 20, double H0 = 40)
    {
        double k = a0 / H0, rho = a0 / (k + Math.Sqrt(1 + k * k)), lam = (rho - r) / rho;
        double a = a0 * lam, H = H0 * lam, tn = Math.Tan(Math.PI / n);
        double Ab = n * a * a * tn, V = Ab * H / 3, S = Ab + Ab * Math.Sqrt(a * a + H * H) / a;
        // нормали: основание (0,0,−1); боковая i — (cos θi, sin θi, a/H) / |·|
        Vector3d Ns(double th) => new Vector3d(Math.Cos(th), Math.Sin(th), a / H).Normalized();
        double extBase = Math.Acos(Vector3d.Dot(new Vector3d(0, 0, -1), Ns(0)));
        double extSide = Math.Acos(Vector3d.Dot(Ns(0), Ns(2 * Math.PI / n)));
        double Lb = 2 * a * tn, Ls = Math.Sqrt(a * a / (Math.Cos(Math.PI / n) * Math.Cos(Math.PI / n)) + H * H);
        double M = 0.5 * n * (Lb * extBase + Ls * extSide);
        return V + S * r + M * r * r + 4 * Math.PI * r * r * r / 3;
    }
    static void Krn06Probe2()
    {
        void Show(string nm, List<TSolid> bl, Func<TSolid, List<TEdge>> sel, double r, bool ch = false)
        {
            var s = bl[0]; var es = sel(s);
            Console.WriteLine($"{nm}: body {s.Guarantee} V {AnalyticVolume.Of(s):R} edges sel {es.Count}");
            var w = new List<string>(); long d0 = DirectRound.Declined;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { if (ch) FilletOp.ChamferEdges(bl, es.Select(EdgeRef.Of).ToList(), r, r, w); else FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), r, w); }
            catch (Exception ex) { Console.WriteLine("  threw " + ex.Message); return; }
            var b = bl[0];
            if (Environment.GetEnvironmentVariable("P3D_PROBEMESH") == "1") BooleanOp.MeshExactlySolid(b, w);
            if (nm.StartsWith("inclined"))
            {
                var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
                double mv = 0; for (int i = 0; i < md.Indices.Length; i += 3) { Vector3d V3(int j) { var q = md.Vertices[md.Indices[j]].P; return new Vector3d(q.X, q.Y, q.Z); } mv += Vector3d.Dot(V3(i), Vector3d.Cross(V3(i + 1), V3(i + 2))) / 6; }
                Console.WriteLine($"  mesh volume {mv:R}");
                var rng = new Random(1); var ax = new Vector3d(0, -Math.Sin(25 * Math.PI / 180), Math.Cos(25 * Math.PI / 180)); var ao = new Vector3d(30, 20, 10);
                foreach (var f in b.Faces.Where(f => f.Tag.Contains("round")))
                {
                    var su = GeometryAudit.AuthoritativeSurface(f); double worst = 0; int nOk = 0;
                    var pts = f.Loops.SelectMany(l => l.CoEdges).SelectMany(ce => Enumerable.Range(0, 9).Select(k => ce.Edge.Curve.PointAt(ce.Edge.Curve.T0 + (ce.Edge.Curve.T1 - ce.Edge.Curve.T0) * (k + 0.5) / 9))).ToList();
                    // внутренние точки: середины между точками контуров
                    var inner = new List<Vector3d>();
                    for (int k = 0; k < 400; k++) { var p1 = pts[rng.Next(pts.Count)]; var p2 = pts[rng.Next(pts.Count)]; var m = (p1 + p2) / 2; inner.Add(su.PointAt(su.ProjectToUV(m))); }
                    foreach (var p in inner)
                    {
                        var uv = su.ProjectToUV(p); var nn = su.NormalAt(uv).Normalized();
                        foreach (var sgn in new[] { 1.0, -1.0 })
                        {
                            var cc = p + nn * (1.5 * sgn);
                            var rel = cc - ao; double dax = (rel - ax * Vector3d.Dot(rel, ax)).Length;
                            double e1 = Math.Abs(cc.Z - 18.5), e2 = Math.Abs(dax - 6.5);
                            if (e1 < 0.1 && e2 < 0.1) { worst = Math.Max(worst, Math.Max(e1, e2)); nOk++; }
                        }
                    }
                    Console.WriteLine($"  face {f.Tag} {su.GetType().Name}: centre deviation max {worst:E3} over {nOk} points");
                }
            }
            Console.WriteLine($"  {sw.ElapsedMilliseconds} ms {b.Guarantee} V {(AnalyticVolume.Of(b) ?? double.NaN):R} faces {b.Faces.Count()} valid {SolidValidator.Validate(b).IsValid} [{SolidValidator.Validate(b).Summary()}]; declined {DirectRound.Declined - d0}; {string.Join(" | ", w.Take(3))}");
            foreach (var iss in SolidValidator.Validate(b).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4)) Console.WriteLine($"    issue {iss.Where} {iss}");
        }
        DirectRound.Debug = x => { if (x.StartsWith("declined")) Console.WriteLine("    " + x); };
        {   // наклонное отверстие
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
            var cyl = TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(30, 20, -20), 5, 60), TransformOp.Rotate(new Vector3d(30, 20, 10), Vector3d.UnitX, 25));
            BooleanOp.Subtract(bl, cyl, new List<string>());
            DirectRound.Debug = null;
            Show("inclined hole top r1.5", bl, s => s.Edges.Where(e => e.V0.P.Z > 19.999 && e.V1.P.Z > 19.999 && e.Curve is not Parametric3D.Kernel.Curves.LineCurve3).ToList(), 1.5);
        }
        {   // ступенька: L-профиль в XZ (60×20, уступ 30×10), выдавлен по Y на 40
            TSolid Step() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, Box(30, -1, 10, 31, 42, 11), new List<string>()); return bl[0]; }
            bool Front(TEdge e) => Math.Abs(e.V0.P.Y) < 1e-9 && Math.Abs(e.V1.P.Y) < 1e-9;
            bool At(TEdge e, double x, double z) => (Math.Abs(e.V0.P.X - x) < 1e-9 && Math.Abs(e.V0.P.Z - z) < 1e-9) || (Math.Abs(e.V1.P.X - x) < 1e-9 && Math.Abs(e.V1.P.Z - z) < 1e-9);
            Show("step: front outline at inner corner (2 convex edges, concave 3rd)", new List<TSolid> { Step() }, s => s.Edges.Where(e => Front(e) && At(e, 30, 10)).ToList(), 2);
            Show("step: whole front outline r2", new List<TSolid> { Step() }, s => s.Edges.Where(Front).ToList(), 2);
            Show("step: concave step edge + front edges at its end (mixed corner)", new List<TSolid> { Step() }, s => s.Edges.Where(e => (Front(e) && At(e, 30, 10)) || (Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V1.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9)).ToList(), 2);
            {
                var bl = new List<TSolid> { Step() }; var w0 = new List<string>();
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V1.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9).Select(EdgeRef.Of).ToList(), 2, w0);
                Console.WriteLine($"step concave edge first: V {AnalyticVolume.Of(bl[0]):R} {w0.FirstOrDefault()}");
                Show("step: then front line-arc-line at the corner", bl, s => s.Edges.Where(e => Front(e) && (e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 || ((e.V0.P - new Vector3d(32, 0, 10)).Length < 1e-9 || (e.V1.P - new Vector3d(32, 0, 10)).Length < 1e-9 || (e.V0.P - new Vector3d(30, 0, 12)).Length < 1e-9 || (e.V1.P - new Vector3d(30, 0, 12)).Length < 1e-9))).ToList(), 2);
            }
            DirectRound.Debug = x => { if (x.StartsWith("declined")) Console.WriteLine("    " + x); };
            Show("step: mixed both ends", new List<TSolid> { Step() }, s => s.Edges.Where(e => ((Front(e) || (Math.Abs(e.V0.P.Y - 40) < 1e-9 && Math.Abs(e.V1.P.Y - 40) < 1e-9)) && At(e, 30, 10)) || (Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V1.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9)).ToList(), 2);
            Show("step: mixed + whole front", new List<TSolid> { Step() }, s => s.Edges.Where(e => Front(e) || (Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V1.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9)).ToList(), 2);
            DirectRound.Debug = null;
            Show("step: concave step edge + convex riser top edge (mixed chain? no — parallel)", new List<TSolid> { Step() }, s => s.Edges.Where(e => Math.Abs(e.V0.P.Y - e.V1.P.Y) > 1 && ((Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9) || (Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 20) < 1e-9))).ToList(), 2);
        }
        foreach (var (n, slope2) in new[] { (4, 0.5), (5, 0.5), (6, 0.5), (4, 0.7) })
        {
            var bl = new List<TSolid> { PyramidBody(n, slope2) };
            Console.WriteLine($"pyramid {n} slope2 {slope2}: faces {bl[0].Faces.Count()} V {AnalyticVolume.Of(bl[0]):R}");
            Show($"pyramid {n} apex r2", bl, s => s.Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).ToList(), 2);
            var bl2 = new List<TSolid> { PyramidBody(n, slope2) };
            Show($"pyramid {n} all edges r2 (Steiner {PyramidSteiner(n, 2):R})", bl2, s => s.Edges.ToList(), 2);
        }
        DirectRound.Debug = null;
    }
    static void Krn06NCornerChecks()
    {
        Check("KRN06-DIRECT-ROUND-N-CORNER", () =>
        {
            // 06.13: угол из n > 3 скруглений равного радиуса — шар, касающийся всех n граней, угол — сферический n-угольник.
            // Правильные пирамиды n = 4, 5, 6 со скруглением ВСЕХ рёбер r = 2: тело = Q ⊕ шар (Q — внутреннее параллельное
            // тело), объём — формула Штейнера (независимо от построителя), ≤ 1e-9 отн. Только рёбра вершины: годное точное тело
            // с гранью «round:corner». Пирамида с разными уклонами (общего шара нет): прямой построитель отказывает; обходной
            // путь — либо годное тело, либо отказ итоговой проверки (P3D_FILLETFINALCHECK), но не негодное тело.
            var fails = new List<string>();
            foreach (int n in new[] { 4, 5, 6 })
            {
                var bl = new List<TSolid> { PyramidBody(n) }; var w = new List<string>(); long c0 = DirectRound.NCorners;
                FilletOp.RoundEdges(bl, bl[0].Edges.Select(EdgeRef.Of).ToList(), 2, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN, want = PyramidSteiner(n, 2);
                Log(new { test = "KRN06-N-CORNER", n, all = true, volume = v, want, guarantee = b.Guarantee.ToString(), corners = DirectRound.NCorners - c0 });
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"n {n} all: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"n {n} all: V {v:R} vs Steiner {want:R}");
                if (DirectRound.NCorners == c0) fails.Add($"n {n} all: no n-corner");
            }
            {
                var bl = new List<TSolid> { PyramidBody(4) }; var w = new List<string>(); long c0 = DirectRound.NCorners;
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 2, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                // OpenCascade строит угол заполнением (B-сплайн) — 21230.4886, сфера даёт 21230.2924 (сверка — по формуле выше)
                Log(new { test = "KRN06-N-CORNER", n = 4, all = false, volume = v, occtFilling = 21230.48860214654, guarantee = b.Guarantee.ToString() });
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid || DirectRound.NCorners == c0) fails.Add($"apex only: {b.Guarantee} {SolidValidator.Validate(b).Summary()} corners {DirectRound.NCorners - c0}");
                if (b.Faces.Count(f => f.Tag == "round:corner") != 1) fails.Add("apex only: no single corner face");
                if (!(v < 21333.333333 && v > 21230)) fails.Add($"apex only: V {v:R}");
            }
            {
                var bl = new List<TSolid> { PyramidBody(4, 0.7) }; var w = new List<string>(); long c0 = DirectRound.NCorners;
                try
                {
                    FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 2, w);
                    if (!SolidValidator.Validate(bl[0]).IsValid) fails.Add($"irregular apex: an invalid body was accepted: {SolidValidator.Validate(bl[0]).Summary()}");
                }
                catch (Parametric3D.Core.RebuildException ex) { Log(new { test = "KRN06-N-CORNER", irregular = "refused", ex.Message }); }
                if (DirectRound.NCorners != c0) fails.Add("irregular apex: the direct builder took a corner without a common ball");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
    static void Krn06InclinedHoleChecks()
    {
        Check("KRN06-INCLINED-HOLE-ROLLING-BALL", () =>
        {
            // 06.11/06.14: кромка НАКЛОННОГО отверстия (ось ⟂ плоскости не перпендикулярна; кромка — эллипс) — прямой построитель
            // замкнутых рёбер между квадриками. Эталон снятого объёма — независимый расчёт канала катящегося шара
            // (tools/ref_canal_inclined_hole.py: теорема Гаусса по трём кускам границы, при a = 0 — формула Паппа до 7e-8).
            // Допуск 5e-7 мм³ (точность эталона). OpenCascade (BRepFilletAPI_MakeFillet) на a = 25°, r = 1.5 снимает 20.75499 —
            // на 0.058 мм³ (0.28 %) меньше эталона при любых допусках SetParams; прямой построитель — 20.8129563.
            var fails = new List<string>();
            foreach (var (a, rr, want) in new[] { (10.0, 1.5, 16.882074680907408), (25.0, 1.5, 20.812956441127426), (25.0, 2.0, 37.73630634894312), (40.0, 1.0, 12.925939254939294) })
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                var cyl = TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(30, 20, -20), 5, 60), TransformOp.Rotate(new Vector3d(30, 20, 10), Vector3d.UnitX, a));
                BooleanOp.Subtract(bl, cyl, new List<string>());
                double v0 = AnalyticVolume.Of(bl[0]) ?? double.NaN; var w = new List<string>();
                var es = bl[0].Edges.Where(e => e.V0.P.Z > 19.999 && e.V1.P.Z > 19.999 && e.Curve is not Parametric3D.Kernel.Curves.LineCurve3).ToList();
                FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), rr, w);
                var b = bl[0]; double rem = v0 - (AnalyticVolume.Of(b) ?? double.NaN);
                Log(new { test = "KRN06-INCLINED-HOLE", a, r = rr, removed = double.IsFinite(rem) ? rem : -1, want, guarantee = b.Guarantee.ToString(), direct = w.Any(x => x.Contains("direct blend")) });
                if (es.Count != 1) fails.Add($"a {a}: {es.Count} rim edges");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"a {a} r {rr}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(rem - want) <= 5e-7)) fails.Add($"a {a} r {rr}: removed {rem:R} vs reference {want:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
    static void Krn06MixedChecks()
    {
        Check("KRN06-DIRECT-ROUND-MIXED-OPEN-CHAIN", () =>
        {
            // 06.2/06.11: смешанный угол (вогнутое ребро уступа и выпуклые рёбра передней грани в одной вершине) — сначала
            // вогнутые, затем выпуклые с дугой вогнутой ленты: открытая касательная цепочка отрезок–дуга–отрезок (цилиндр–тор–
            // цилиндр); открытая цепочка верха бруска со скруглённым вертикальным ребром. Эталон — OpenCascade
            // (BRepFilletAPI_MakeFillet одной операцией; те же тела), допуск 1e-9 отн.; прямой построитель, ExactAnalytic.
            var fails = new List<string>();
            TSolid Step() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, Box(30, -1, 10, 31, 42, 11), new List<string>()); return bl[0]; }
            static bool Y(TEdge e, double y) => Math.Abs(e.V0.P.Y - y) < 1e-9 && Math.Abs(e.V1.P.Y - y) < 1e-9;
            static bool At(TEdge e) => (Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9) || (Math.Abs(e.V1.P.X - 30) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9);
            static bool StepE(TEdge e) => Math.Abs(e.V0.P.X - 30) < 1e-9 && Math.Abs(e.V1.P.X - 30) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9;
            TSolid BoxR5()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(EdgeAt(bl[0], new(60, 40, 0), new(60, 40, 20))) }, 5, new List<string>());
                return bl[0];
            }
            static bool Top(TEdge e) => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9;
            static Vector3d Mid(TEdge e) => e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2);
            var cases = new (string Name, Func<TSolid> Make, Func<TEdge, bool> Sel, double R, double Occt, bool Mixed)[]
            {
                ("mixed corner r2", Step, e => (Y(e, 0) && At(e)) || StepE(e), 2, 36000.134492061494, true),
                ("mixed corner r1.5", Step, e => (Y(e, 0) && At(e)) || StepE(e), 1.5, 36000.056738838444, true),
                ("mixed corners at both ends r2", Step, e => ((Y(e, 0) || Y(e, 40)) && At(e)) || StepE(e), 2, 35965.93269026658, true),
                ("open chain line-arc-line on top r2", BoxR5, e => Top(e) && (Math.Abs(Mid(e).X - 60) < 1e-9 || Math.Abs(Mid(e).Y - 40) < 1e-9 || e.Curve is Parametric3D.Kernel.Curves.ArcCurve3), 2, 47809.302876099515, false),
            };
            foreach (var (name, make, selF, rr, occt, mixed) in cases)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>();
                long m0 = DirectRound.MixedCorners, o0 = DirectRound.TanOpenChains;
                var es = bl[0].Edges.Where(selF).ToList();
                FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), rr, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                bool direct = w.Any(x => x.Contains("direct blend"));
                Log(new { test = "KRN06-MIXED-OPEN", name, n = es.Count, volume = double.IsFinite(v) ? v : -1, occt, direct, mixedN = DirectRound.MixedCorners - m0, openN = DirectRound.TanOpenChains - o0 });
                if (!direct || b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: direct {direct} {b.Guarantee} {SolidValidator.Validate(b).Summary()} | {string.Join(" | ", w.Take(2))}");
                if (!(Math.Abs(v - occt) <= 1e-9 * occt)) fails.Add($"{name}: V {v:R} vs OCCT {occt:R}");
                if (mixed && DirectRound.MixedCorners == m0) fails.Add($"{name}: not staged");
                if (DirectRound.TanOpenChains == o0) fails.Add($"{name}: no open tangent chain");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
    static void Krn06TanPropChecks()
    {
        Check("KRN06-DIRECT-ROUND-TANGENT-PROPAGATION", () =>
        {
            // 06.9/06.11: выбор, кончающийся у касательного продолжения (отрезок верха бруска у скруглённого вертикального ребра,
            // одна дуга), дополняется звеньями до острых стыков — как у OpenCascade (BRepFilletAPI_MakeFillet продолжает
            // скругление по касательным сам; эталон — тот же выбор одним ребром), допуск 1e-9 отн.; предупреждение о добавленных.
            var fails = new List<string>();
            TSolid BoxR5(bool all4)
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                var es = all4 ? bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).ToList() : new List<TEdge> { EdgeAt(bl[0], new(60, 40, 0), new(60, 40, 20)) };
                FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), 5, new List<string>());
                return bl[0];
            }
            static Vector3d Mid(TEdge e) => e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2);
            static bool Z(TEdge e, double z) => Math.Abs(e.V0.P.Z - z) < 1e-9 && Math.Abs(e.V1.P.Z - z) < 1e-9;
            var cases = new (string Name, Func<TSolid> Make, Func<TEdge, bool> Sel, double R, double Occt)[]
            {
                ("one R5: top line x = 60 only", () => BoxR5(false), e => Z(e, 20) && Math.Abs(Mid(e).X - 60) < 1e-9, 2, 47809.302876099515),
                ("one R5: top arc only", () => BoxR5(false), e => Z(e, 20) && e.Curve is Parametric3D.Kernel.Curves.ArcCurve3, 2, 47809.302876099515),
                ("four R5: top line x = 60 only (whole loop)", () => BoxR5(true), e => Z(e, 20) && Math.Abs(Mid(e).X - 60) < 1e-9, 2, 47408.892973680064),
                ("four R5: one bottom arc r1.5 (whole loop)", () => BoxR5(true), e => Z(e, 0) && e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 && Mid(e).X < 30 && Mid(e).Y < 20, 1.5, 47479.38685691795),
            };
            foreach (var (name, make, selF, rr, occt) in cases)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>(); long t0 = DirectRound.TanPropagations;
                var es = bl[0].Edges.Where(selF).ToList();
                FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), rr, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                bool direct = w.Any(x => x.Contains("direct blend")), told = w.Any(x => x.Contains("tangent propagation added"));
                Log(new { test = "KRN06-TANPROP", name, n = es.Count, volume = double.IsFinite(v) ? v : -1, occt, direct, told });
                if (es.Count != 1) fails.Add($"{name}: {es.Count} edges selected");
                if (!direct || !told || DirectRound.TanPropagations == t0 || b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: direct {direct} told {told} {b.Guarantee} {SolidValidator.Validate(b).Summary()} | {string.Join(" | ", w.Take(2))}");
                if (!(Math.Abs(v - occt) <= 1e-9 * occt)) fails.Add($"{name}: V {v:R} vs OCCT {occt:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
