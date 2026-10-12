using Parametric3D.Kernel;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>g.05: тор в точном маршруте — проверки по шагам.</summary>
static partial class Program
{
    /// <summary>Классификатор на торе (в т. ч. повёрнутом) против формулы «внутри трубки».</summary>
    static string TorusClassifierProbe(int n)
    {
        var rnd = new Random(5); int bad = 0, undecided = 0;
        foreach (var rot in new[] { false, true })
        {
            var c = new Vector3d(1, 2, 3); double R = 10, r = 3;
            var t = PrimitiveOp.Torus(c, R, r);
            var axis = new Vector3d(0, 0, 1); var center = c;
            if (rot) { var ax = new Vector3d(0.3, 0.5, 0.8).Normalized(); t = TransformOp.Copy(t, TransformOp.Rotate(c, ax, 37)); axis = TransformOp.Rotate(c, ax, 37).Direction(axis).Normalized(); }
            var cl = new Parametric3D.Kernel.Analytic.Classifier(t);
            if (cl.Unsupported != null) return "unsupported: " + cl.Unsupported;
            for (int i = 0; i < n; i++)
            {
                var p = center + new Vector3d(rnd.NextDouble() * 30 - 15, rnd.NextDouble() * 30 - 15, rnd.NextDouble() * 10 - 5);
                var q = p - center; double z = Vector3d.Dot(q, axis); double rho = (q - axis * z).Length;
                double dd = Math.Sqrt((rho - R) * (rho - R) + z * z) - r;
                if (Math.Abs(dd) < 1e-6) continue;
                var k = cl.Classify(p);
                if (k == null) { undecided++; continue; }
                if ((k == 1) != (dd < 0)) bad++;
            }
        }
        return $"bad {bad}, undecided {undecided} of {2 * n}";
    }
}
static partial class Program
{
    static void TorusBooleanProbe(string? only)
    {
        var c = new Vector3d(1, 2, 3); double R = 10, r = 3;
        bool InTorus(Vector3d p) { var q = p - c; double z = q.Z; double rho = Math.Sqrt(q.X * q.X + q.Y * q.Y); return (rho - R) * (rho - R) + z * z < r * r; }
        var cases = new List<(string Name, Func<TSolid> B, Func<Vector3d, bool> InB, BooleanOp.BooleanKind K)>
        {
            ("upper half (latitudes)", () => Box(c.X - 20, c.Y - 20, c.Z + 1, 40, 40, 10), p => p.X > c.X - 20 && p.X < c.X + 20 && p.Y > c.Y - 20 && p.Y < c.Y + 20 && p.Z > c.Z + 1 && p.Z < c.Z + 11, BooleanOp.BooleanKind.Intersect),
            ("slab through tube (meridians)", () => Box(c.X - 2, c.Y - 20, c.Z - 10, 4, 40, 20), p => p.X > c.X - 2 && p.X < c.X + 2 && p.Y > c.Y - 20 && p.Y < c.Y + 20 && p.Z > c.Z - 10 && p.Z < c.Z + 10, BooleanOp.BooleanKind.Subtract),
            ("sphere on equator", () => PrimitiveOp.Sphere(c + new Vector3d(13, 0, 0), 2), p => (p - (c + new Vector3d(13, 0, 0))).Length < 2, BooleanOp.BooleanKind.Unite),
            ("coaxial cylinder", () => PrimitiveOp.Cylinder(c - new Vector3d(0, 0, 10), 9, 20), p => { var q = p - c; return q.X * q.X + q.Y * q.Y < 81 && Math.Abs(q.Z) < 10; }, BooleanOp.BooleanKind.Subtract),
            ("vertical cylinder through tube", () => PrimitiveOp.Cylinder(c + new Vector3d(10, 0, -10), 1.5, 20), p => { var q = p - (c + new Vector3d(10, 0, 0)); return q.X * q.X + q.Y * q.Y < 2.25 && Math.Abs(q.Z) < 10; }, BooleanOp.BooleanKind.Subtract),
            ("box minus torus (A=box)", () => Box(c.X - 5, c.Y - 5, c.Z - 2, 20, 10, 4), p => p.X > c.X - 5 && p.X < c.X + 15 && p.Y > c.Y - 5 && p.Y < c.Y + 5 && p.Z > c.Z - 2 && p.Z < c.Z + 2, BooleanOp.BooleanKind.Subtract),
        };
        foreach (var (name, mkB, inB, k) in cases)
        {
            if (only != null && !name.Contains(only)) continue;
            bool boxFirst = name.Contains("A=box");
            var t = PrimitiveOp.Torus(c, R, r); var b = mkB();
            var A = boxFirst ? b : t; var B = boxFirst ? t : b;
            Func<Vector3d, bool> inA = boxFirst ? inB : InTorus, inBB = boxFirst ? InTorus : inB;
            var route = new List<string>();
            BooleanOp.AnalyticRouteDebug = m => route.Add(m);
            if (Environment.GetEnvironmentVariable("SPLITDBG") == "1") Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => { if (m.Contains("torus") || m.Contains("region class") || m.Contains("side probe")) Console.WriteLine("SPLIT " + m); };
            if (Environment.GetEnvironmentVariable("ANCHORDBG") == "1") TopologyConventions.AnchorDebug = m => Console.WriteLine("ANCHOR " + m);
            if (Environment.GetEnvironmentVariable("UVDBG") == "1") Parametric3D.Kernel.Tess.UvMesher.Debug = m => { if (!m.StartsWith("loop point")) Console.WriteLine("UV " + m); };
            if (Environment.GetEnvironmentVariable("FIDBG") == "1") Parametric3D.Kernel.Analytic.FaceIntersect.Debug = m => Console.WriteLine("FI " + m);
            if (Environment.GetEnvironmentVariable("GINV") == "1") BooleanOp.GeneralInvalidProbe = rb =>
            {
                var uses = new Dictionary<TEdge, int>();
                foreach (var fc in rb.Faces) foreach (var l in fc.Loops) foreach (var ce in l.CoEdges) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
                foreach (var fc in rb.Faces)
                {
                    Console.WriteLine($"GINV face {fc.Id} {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} rev {fc.Reversed} hint {fc.InteriorHint} loops {fc.Loops.Count}");
                    foreach (var l in fc.Loops)
                        Console.WriteLine("   loop: " + string.Join(", ", l.CoEdges.Select(ce => { var cv = ce.Edge.Curve; var a = cv.PointAt(cv.T0); var m = cv.PointAt((cv.T0 + cv.T1) / 2); var b = cv.PointAt(cv.T1); return $"e{ce.Edge.Id}{(ce.Reversed ? "-" : "+")}x{uses[ce.Edge]} {cv.GetType().Name} ({a.X:0.###},{a.Y:0.###},{a.Z:0.###})~({m.X:0.##},{m.Y:0.##},{m.Z:0.##})~({b.X:0.###},{b.Y:0.###},{b.Z:0.###})"; })));
                }
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var warnL = new List<string>();
            var res = BooleanOp.Evaluate(new List<TSolid> { A }, B, k, Parametric3D.Kernel.OperationPolicy.Strict, warnL);
            if (Environment.GetEnvironmentVariable("TORISSUES") == "1") { foreach (var wl in warnL) Console.WriteLine("   warn: " + wl); Console.WriteLine("   describe: " + res.Describe()); }
            double ms = sw.Elapsed.TotalMilliseconds;
            BooleanOp.AnalyticRouteDebug = null;
            double vol = res.Bodies.Sum(x => AnalyticVolume.Of(x) ?? double.NaN);
            // Монте-Карло по формулам
            var rnd = new Random(7); var lo = c - new Vector3d(16, 16, 12); var hi = c + new Vector3d(16, 16, 12); int nIn = 0, N = 400000;
            for (int i = 0; i < N; i++)
            {
                var p = new Vector3d(lo.X + (hi.X - lo.X) * rnd.NextDouble(), lo.Y + (hi.Y - lo.Y) * rnd.NextDouble(), lo.Z + (hi.Z - lo.Z) * rnd.NextDouble());
                bool a = inA(p), bb = inBB(p);
                bool inn = k switch { BooleanOp.BooleanKind.Unite => a || bb, BooleanOp.BooleanKind.Intersect => a && bb, _ => a && !bb };
                if (inn) nIn++;
            }
            double box = (hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z); double mc = box * nIn / N, sigma = box * Math.Sqrt((double)nIn / N * (1 - (double)nIn / N) / N);
            var v = res.Bodies.Select(x => SolidValidator.Validate(x).Summary());
            Console.WriteLine($"TORBOOL {name}: {res.Outcome} {res.Guarantee} {ms:0}ms vol {vol:0.###} MC {mc:0.#}±{sigma:0.#} ({(vol - mc) / sigma:+0.0;-0.0}σ) valid {string.Join(" | ", v)}");
            Console.WriteLine("   route: " + string.Join(" / ", route.Where(x => x.StartsWith("general")).Take(3)));
            foreach (var bd in res.Bodies)
            {
                if (Environment.GetEnvironmentVariable("TORDUMP") is { } dumpDir)
                {
                    int bi = res.Bodies.ToList().IndexOf(bd);
                    var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(bd); var sw2 = new StringWriter();
                    foreach (var vv in md.Vertices) sw2.WriteLine(FormattableString.Invariant($"v {vv.P.X:R} {vv.P.Y:R} {vv.P.Z:R}"));
                    for (int ti = 0; ti + 2 < md.Indices.Length; ti += 3) sw2.WriteLine($"f {md.Indices[ti] + 1} {md.Indices[ti + 1] + 1} {md.Indices[ti + 2] + 1}");
                    File.WriteAllText(Path.Combine(dumpDir, $"tor_b{bi}_mesh.obj"), sw2.ToString());
                    Console.WriteLine($"   dump body {bi}: exact {(bd.Exact != null)} auth {bd.ExactAuthoritative} faces {bd.Faces.Count()} srcTags {string.Join(",", bd.Faces.Select(fc => fc.SourceTag))} surf {string.Join(",", bd.Faces.Select(fc => (GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name ?? "?").Substring(0, 3) + ":" + fc.Loops.Count))}");
                }
                if (Environment.GetEnvironmentVariable("TORISSUES") == "1") foreach (var iss in SolidValidator.Validate(bd).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(6)) Console.WriteLine("   issue: " + iss);
                double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bd));
                Console.WriteLine($"   body: mesh volume {vm:0.###}; faces " + string.Join("; ", bd.Faces.Select(fc => $"{GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} loops {fc.Loops.Count} int {AnalyticVolume.FaceIntegral(fc):0.###} meshInt {(fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx ? fsx.Facets.ToList().Sum(t => Vector3d.Dot(t.Item1, Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1)) / 2) : double.NaN):0.###}")));
                foreach (var fc in bd.Faces)
                    foreach (var l in fc.Loops)
                        Console.WriteLine($"     face {fc.Id} rev {fc.Reversed} hint {fc.InteriorHint} loop: " + string.Join(", ", l.CoEdges.Select(ce => { var m = ce.Edge.Curve.PointAt((ce.Edge.Curve.T0 + ce.Edge.Curve.T1) / 2); return $"{ce.Edge.Curve.GetType().Name}{(ce.Reversed ? "-" : "+")} mid ({m.X:0.##},{m.Y:0.##},{m.Z:0.##}) e{ce.Edge.Id}"; })));
            }
        }
    }
}

static partial class Program
{
    /// <summary>g.05: тор в точном маршруте — проверки (в allA).</summary>
    static void TorusRouteChecks()
    {
        // Шесть построек пробы: точный маршрут, годное тело, объём — точно (где есть формула) или Монте-Карло в пределах 5σ.
        Check("G05-TORUS-EXACT-ROUTE", () =>
        {
            var fails = new List<string>();
            foreach (var (name, res, vol, mc, sigma) in TorusBooleanCases())
            {
                if (res.Outcome != OperationOutcome.Success || res.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name}: {res.Outcome} {res.Guarantee}"); continue; }
                foreach (var bd in res.Bodies) { var v = SolidValidator.Validate(bd); if (v.Issues.Any(i => i.Severity == ValidationSeverity.Error)) fails.Add($"{name}: {v.Summary()}"); }
                if (!(Math.Abs(vol - mc) <= 5 * sigma)) fails.Add($"{name}: volume {vol:0.###} vs MC {mc:0.#}±{sigma:0.#}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        // Случаи, где OpenCascade нашёл неверное принятие тора в точном маршруте (до включения по умолчанию): брусок минус тор
        // (сторона остатка тора — объём 46 190 вместо 39 559.6), тор ∪ цилиндр поперёк трубки (косой пояс — 547 вместо 1076.98),
        // брусок минус тор (серия 8, случай 91: звено через разрез — ошибка 0.025 мм³ первого порядка по шагу).
        Check("G05-TORUS-OCCT-CASES", () =>
        {
            var fails = new List<string>();
            // объём OpenCascade — полная точность (r.json серий 8u, 10u); допуск 1e-4 мм³ (расхождение после правок — до 3e-6)
            // + точка внутри области тора с ломаной (тор × тор, тор − цилиндр: 31, 39), сторона сферической грани по точке грани
            // (шар − конус, тор − шар, тор ∪ шар: 16, 24, 47) — прежде многогранный путь
            foreach (var (seed, idx, ops, occt, tol) in new[] { (8, 79, TorOps8, 39559.57183092048, 1e-4), (8, 91, TorOps8, 3185.922376304301, 1e-4), (10, 22, TorOps10, 1076.9780051052962, 1e-4),
                                                                 (8, 31, TorOps8, 39844.41710601375, 1e-4), (8, 39, TorOps8, 4665.203535844117, 1e-4),
                                                                 (8, 16, TorOps8, 96.1999750358464, 1e-4), (8, 24, TorOps8, 3437.0376020244057, 1e-4), (8, 47, TorOps8, 14078.069526613497, 1e-4),
                                                                 (8, 61, TorOps8, 2093.476598520505, 1e-4), (8, 158, TorOps8Long, 6030.193526282073, 1e-4),
                                                                 // складка разметки конусной грани (P3D_FOLDCHECK); 82 — эталон независимым интегрированием по
                                                                 // сечениям (OpenCascade ошибается на 0.0064 мм³ у вершины конуса)
                                                                 (8, 43, TorOps8, 2738.0153758088986, 1e-4), (8, 45, TorOps8, 4938.151078048367, 1e-4),
                                                                 (8, 74, TorOps8, 1936.7669641351463, 1e-4), (8, 82, TorOps8, 7789.6928045241475, 1e-4),
                                                                 // отверстие между несколькими поясами тора (P3D_TORMULTIBAND)
                                                                 (8, 9, TorOps8, 14348.707596306114, 1e-4), (8, 199, TorOps8Long, 999.5510631615823, 1e-4) })
            {
                var row = ReplayDiffCase(seed, idx, ops);
                string outcome = row[3], level = row[4]; double vol = double.Parse(row[6], System.Globalization.CultureInfo.InvariantCulture);
                if (outcome != "Success" || level != "ExactAnalytic" || !(Math.Abs(vol - occt) <= tol)) fails.Add($"d{seed}_{idx:D4}: {outcome} {level} {vol:R} vs OCCT {occt}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        // Волнистый пояс: тор ∪/∩ конус под углом (тождества, случай 40) — точный маршрут и V∪ + V∩ = VA + VB. Прежде разрез
        // пояса ложился в саму грань (объединение 1634 вместо 2750), а сетка пояса — на дополнение.
        Check("G05-TORUS-WAVY-BAND", () =>
        {
            var (a, da, b, db) = IdentityOperands(20261010, 40);
            Require(da.StartsWith("torus") && db.StartsWith("cone"), $"operands {da} / {db}");
            var u = BooleanOp.Evaluate(new List<TSolid> { a }, b, BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
            var x = BooleanOp.Evaluate(new List<TSolid> { a }, b, BooleanOp.BooleanKind.Intersect, OperationPolicy.Strict);
            Require(u.Guarantee == GuaranteeLevel.ExactAnalytic && x.Guarantee == GuaranteeLevel.ExactAnalytic, $"∪ {u.Guarantee}, ∩ {x.Guarantee}");
            double va = OpVolume(a), vb = OpVolume(b);
            double e = u.Bodies.Sum(t => AnalyticVolume.Of(t) ?? double.NaN) + x.Bodies.Sum(t => AnalyticVolume.Of(t) ?? double.NaN) - va - vb;
            Require(Math.Abs(e) <= 1e-8 * (va + vb), $"V∪ + V∩ − VA − VB = {e:G4}");
        });
        // Полнота прослеживания (исключение по Липшицу): плоскость почти касается тора снаружи — овал пересечения в доли мм,
        // меньше шага сетки затравок (0.22 мм): без удостоверения ветвь терялась молча.
        Check("G05-TRACE-CERTIFICATE-SMALL-LOOP", () =>
        {
            var c = new Vector3d(1, 2, 3); double R = 10, r = 3;
            var w = new Vector3d(0.13, 0.21, 1).Normalized();
            var tor = new Parametric3D.Kernel.Geometry.Ssi.Torus(c, w, R, r);
            double xmax = c.X + R * Math.Sqrt(1 - w.X * w.X) + r;
            var fails = new List<string>();
            foreach (double delta in new[] { 1e-3, 1e-4, 2e-5 })
            {
                var pl = new Parametric3D.Kernel.Geometry.Ssi.Plane(new Vector3d(xmax - delta, 0, 0), Vector3d.UnitX);
                var res = Parametric3D.Kernel.Geometry.Ssi.Intersect(pl, tor, c + new Vector3d(0.0137, 0.0291, 0.0173), R + r + 1);
                // ожидается одна замкнутая кривая (или честный отказ удостоверения), но не «пусто»
                if (res.Uncertified == null && res.Curves.Count == 0) fails.Add($"δ {delta}: the small oval was missed ({res})");
                // найденная кривая — на обеих поверхностях и замкнута (шаг по кривизне: точки не дальше 1e-9 от поверхностей)
                foreach (var cv in res.Curves)
                {
                    double worst = 0; int ns = 64;
                    for (int k = 0; k <= ns; k++) { var q = cv.PointAt(cv.T0 + (cv.T1 - cv.T0) * k / ns); worst = Math.Max(worst, Math.Max(Math.Abs(Parametric3D.Kernel.Geometry.Ssi.Implicit(pl, q).F), Math.Abs(Parametric3D.Kernel.Geometry.Ssi.Implicit(tor, q).F))); }
                    var e0 = cv.PointAt(cv.T0); var e1 = cv.PointAt(cv.T1);
                    if (worst > 1e-7 || (e0 - e1).Length > 1e-9) fails.Add($"δ {delta}: curve off the surfaces {worst:G3} or open {(e0 - e1).Length:G3}");
                    // размер овала: большая полуось √(2(R+r)δ) (кривизна тора вдоль экватора) — кривая не «срезает» овал
                    var sp = Enumerable.Range(0, 129).Select(k => cv.PointAt(cv.T0 + (cv.T1 - cv.T0) * k / 128)).ToList();
                    double ext = sp.SelectMany(p1 => sp.Select(p2 => (p1 - p2).Length)).Max(), want = 2 * Math.Sqrt(2 * (R + r) * delta);
                    if (Math.Abs(ext - want) > 0.05 * want) fails.Add($"δ {delta}: oval extent {ext:G4} vs {want:G4}");
                }
                Log(new { test = "G05-TRACE-CERTIFICATE-SMALL-LOOP", delta, curves = res.Curves.Count, res.Uncertified });
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        // Строгость исключения: оценка радиуса образа ячейки карты (по константам Липшица) не меньше истинного — плотная
        // выборка случайных ячеек всех форм.
        Check("G05-TRACE-CELL-BOUND-RIGOROUS", () =>
        {
            var rnd = new Random(9); var bc = new Vector3d(1, -2, 3); double br = 17;
            var shapes = new Parametric3D.Kernel.Geometry.Ssi.Shape[]
            {
                new Parametric3D.Kernel.Geometry.Ssi.Plane(bc, new Vector3d(0.3, 0.4, 0.86)),
                new Parametric3D.Kernel.Geometry.Ssi.Cylinder(bc, new Vector3d(0.1, 1, 0.2), 6),
                new Parametric3D.Kernel.Geometry.Ssi.Sphere(bc, 9),
                new Parametric3D.Kernel.Geometry.Ssi.Cone(bc - new Vector3d(0, 0, 8), new Vector3d(0, 0.2, 1), 0.5),
                new Parametric3D.Kernel.Geometry.Ssi.Torus(bc, new Vector3d(0.2, 0.1, 1), 10, 3),
            };
            var fails = new List<string>();
            foreach (var sh in shapes)
                for (int k = 0; k < 300; k++)
                {
                    double w = Math.Pow(10, -3 * rnd.NextDouble()) * 0.25, u0 = rnd.NextDouble() * (1 - w), v0 = rnd.NextDouble() * (1 - w), u1 = u0 + w, v1 = v0 + w * (0.3 + rnd.NextDouble());
                    v1 = Math.Min(1, v1);
                    var c = Parametric3D.Kernel.Geometry.Ssi.ChartPoint(sh, bc, br, (u0 + u1) / 2, (v0 + v1) / 2);
                    double bound = Parametric3D.Kernel.Geometry.Ssi.CellRadiusBound(sh, bc, br, u0, v0, u1, v1) ?? double.NaN, worst = 0;
                    for (int i = 0; i <= 40; i++) for (int j = 0; j <= 40; j++) worst = Math.Max(worst, (Parametric3D.Kernel.Geometry.Ssi.ChartPoint(sh, bc, br, u0 + (u1 - u0) * i / 40, v0 + (v1 - v0) * j / 40) - c).Length);
                    if (!(worst <= bound * (1 + 1e-12))) { fails.Add($"{sh.GetType().Name}: cell {u0:G3}..{u1:G3}×{v0:G3}..{v1:G3} radius {worst:G4} > bound {bound:G4}"); break; }
                }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        // Защита маршрута: объём по формулам граней против объёма фасет. Внесённая ошибка интеграла (+2%) — маршрут отказывает
        // (многогранный путь), без неё — точный.
        Check("G05-VOLUME-AGREES-WITH-FACETS", () =>
        {
            var c = new Vector3d(1, 2, 3);
            OperationResult Run() => BooleanOp.Evaluate(new List<TSolid> { Box(c.X - 5, c.Y - 5, c.Z - 2, 20, 10, 4) }, PrimitiveOp.Torus(c, 10, 3), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>());
            var ok = Run();
            Require(ok.Guarantee == GuaranteeLevel.ExactAnalytic, $"without a fault: {ok.Guarantee}");
            BooleanOp.VolumeFaultForTest = v => v * 1.02;
            try { var bad = Run(); Require(bad.Guarantee != GuaranteeLevel.ExactAnalytic, "a 2% integral error was accepted by the exact route"); }
            finally { BooleanOp.VolumeFaultForTest = null; }
        });
        // Полнота на границе: точка ребра на поверхности другой грани обязана лежать на найденной кривой. Набор кривых без
        // ветви у угла бруска (её терял поиск ближайшей точки у шва) — отказ; полный набор — нет.
        Check("G05-EDGE-EVENT-CERTIFICATE", () =>
        {
            var c = new Vector3d(1, 2, 3);
            var box = Box(c.X - 5, c.Y - 5, c.Z - 2, 20, 10, 4); TopologyConventions.Normalize(box);
            var tor = PrimitiveOp.Torus(c, 10, 3); TopologyConventions.Normalize(tor);
            var tf = Parametric3D.Kernel.Analytic.AnalyticFace.Of(tor.Faces.First(), out var w1) ?? throw new Exception("torus face: " + w1);
            var pf = box.Faces.Select(f => Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out _)).First(f => f != null && f.Shape is Parametric3D.Kernel.Geometry.Ssi.Plane pl && Math.Abs(Math.Abs(pl.N.Normalized().X) - 1) < 1e-12 && Math.Abs(pl.P.X - (c.X - 5)) < 1e-9)!;
            var ssi = Parametric3D.Kernel.Geometry.Ssi.Intersect(pf.Shape, tf.Shape, c, 20);
            Require(ssi.Curves.Count == 2, $"plane x=-4 ∩ torus: {ssi}");
            Require(Parametric3D.Kernel.Analytic.FaceIntersect.CertifyAgainst(pf, tf, ssi) == null, "the full set of curves was refused");
            int refused = 0;
            for (int drop = 0; drop < 2; drop++)
            {
                var part = new Parametric3D.Kernel.Geometry.Ssi.Result { Kind = ssi.Kind }; part.Curves.Add(ssi.Curves[1 - drop]);
                if (Parametric3D.Kernel.Analytic.FaceIntersect.CertifyAgainst(pf, tf, part) != null) refused++;
            }
            Require(refused == 2, $"a set without a branch crossing the face boundary was accepted ({refused}/2 refused)");
        });
    }

    const string TorOps8 = "2221222222221212221112211121211111111111112212111211222211112111112221112221111112111121222122122122";
    const string TorOps8Long = "22212222222212122211122111212111111111111122121112112222111121111122211122211111121111212221221221222112211222211212112112111211121212211121122211121121122111121111212122121111212112111122121212121112";
    const string TorOps10 = "21122111221121121211221211111121222221221211221222122112112222111211122121121211";

    /// <summary>Случай разностной серии повтором по зерну и номеру: строка манифеста (исход, уровень, объём…).</summary>
    internal static string[] ReplayDiffCase(int seed, int idx, string ops)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rp_{seed}_{idx}_{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        var rp = Path.Combine(dir, "replay.tsv");
        var lines = new List<string> { "id\tops" };
        for (int i = 0; i < idx; i++) lines.Add($"d{seed}_{i:D4}\t{string.Join(",", Enumerable.Repeat("x", ops[i] - '0'))}");
        File.WriteAllLines(rp, lines);
        Environment.SetEnvironmentVariable("DIFFREPLAY", rp);
        Environment.SetEnvironmentVariable("DIFFONLY", $"d{seed}_{idx:D4}");
        try { DiffDump(dir, seed, idx + 1); }
        finally { Environment.SetEnvironmentVariable("DIFFREPLAY", null); Environment.SetEnvironmentVariable("DIFFONLY", null); }
        var row = File.ReadAllLines(Path.Combine(dir, "manifest.tsv")).Last().Split('\t');
        try { Directory.Delete(dir, true); } catch { }
        return row;
    }

    internal static List<(string Name, OperationResult Res, double Vol, double Mc, double Sigma)> TorusBooleanCases()
    {
        var c = new Vector3d(1, 2, 3); double R = 10, r = 3;
        bool InTorus(Vector3d p) { var q = p - c; double z = q.Z; double rho = Math.Sqrt(q.X * q.X + q.Y * q.Y); return (rho - R) * (rho - R) + z * z < r * r; }
        var cases = new List<(string Name, Func<TSolid> B, Func<Vector3d, bool> InB, BooleanOp.BooleanKind K)>
        {
            ("upper half", () => Box(c.X - 20, c.Y - 20, c.Z + 1, 40, 40, 10), p => p.X > c.X - 20 && p.X < c.X + 20 && p.Y > c.Y - 20 && p.Y < c.Y + 20 && p.Z > c.Z + 1 && p.Z < c.Z + 11, BooleanOp.BooleanKind.Intersect),
            ("slab through tube", () => Box(c.X - 2, c.Y - 20, c.Z - 10, 4, 40, 20), p => p.X > c.X - 2 && p.X < c.X + 2 && p.Y > c.Y - 20 && p.Y < c.Y + 20 && p.Z > c.Z - 10 && p.Z < c.Z + 10, BooleanOp.BooleanKind.Subtract),
            ("sphere on equator", () => PrimitiveOp.Sphere(c + new Vector3d(13, 0, 0), 2), p => (p - (c + new Vector3d(13, 0, 0))).Length < 2, BooleanOp.BooleanKind.Unite),
            ("coaxial cylinder", () => PrimitiveOp.Cylinder(c - new Vector3d(0, 0, 10), 9, 20), p => { var q = p - c; return q.X * q.X + q.Y * q.Y < 81 && Math.Abs(q.Z) < 10; }, BooleanOp.BooleanKind.Subtract),
            ("vertical cylinder through tube", () => PrimitiveOp.Cylinder(c + new Vector3d(10, 0, -10), 1.5, 20), p => { var q = p - (c + new Vector3d(10, 0, 0)); return q.X * q.X + q.Y * q.Y < 2.25 && Math.Abs(q.Z) < 10; }, BooleanOp.BooleanKind.Subtract),
            ("box minus torus (A=box)", () => Box(c.X - 5, c.Y - 5, c.Z - 2, 20, 10, 4), p => p.X > c.X - 5 && p.X < c.X + 15 && p.Y > c.Y - 5 && p.Y < c.Y + 5 && p.Z > c.Z - 2 && p.Z < c.Z + 2, BooleanOp.BooleanKind.Subtract),
        };
        var outL = new List<(string, OperationResult, double, double, double)>();
        foreach (var (name, mkB, inB, k) in cases)
        {
            bool boxFirst = name.Contains("A=box");
            var t = PrimitiveOp.Torus(c, R, r); var b = mkB();
            var A = boxFirst ? b : t; var B = boxFirst ? t : b;
            Func<Vector3d, bool> inA = boxFirst ? inB : InTorus, inBB = boxFirst ? InTorus : inB;
            var res = BooleanOp.Evaluate(new List<TSolid> { A }, B, k, OperationPolicy.Strict, new List<string>());
            double vol = res.Bodies.Sum(x => AnalyticVolume.Of(x) ?? double.NaN);
            var rnd = new Random(7); var lo = c - new Vector3d(16, 16, 12); var hi = c + new Vector3d(16, 16, 12); int nIn = 0, N = 400000;
            for (int i = 0; i < N; i++)
            {
                var p = new Vector3d(lo.X + (hi.X - lo.X) * rnd.NextDouble(), lo.Y + (hi.Y - lo.Y) * rnd.NextDouble(), lo.Z + (hi.Z - lo.Z) * rnd.NextDouble());
                bool a = inA(p), bb = inBB(p);
                if (k switch { BooleanOp.BooleanKind.Unite => a || bb, BooleanOp.BooleanKind.Intersect => a && bb, _ => a && !bb }) nIn++;
            }
            double bx = (hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z); double fr = (double)nIn / N;
            outL.Add((name, res, vol, bx * fr, bx * Math.Sqrt(fr * (1 - fr) / N)));
        }
        return outL;
    }
}
