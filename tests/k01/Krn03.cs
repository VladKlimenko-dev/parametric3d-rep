using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static string Krn03Describe(OperationResult r)
    {
        var parts = r.Bodies.Select(b =>
        {
            var rep = SolidValidator.Validate(b);
            double v = AnalyticVolume.Of(b) ?? double.NaN;
            string mv = rep.Metrics.TryGetValue("mesh.volume", out var x) ? $"{x:G8}" : "-";
            return $"[exact {(b.Exact == null ? "-" : b.ExactAuthoritative ? "auth" : "derived")} {b.Exact?.Solid.Polygons.Count} shells {b.Shells.Count} comps {rep.Metrics.GetValueOrDefault("brep.faceComponents")} meshComps {rep.Metrics.GetValueOrDefault("mesh.components")} cavities {rep.Metrics.GetValueOrDefault("mesh.cavities")} vol {v:G8} meshVol {mv} {b.Guarantee} {rep.Summary()}]";
        });
        return $"{r.Outcome} bodies {r.Bodies.Count}: " + string.Join(" ", parts);
    }

    static void Krn03Measure()
    {
        void Op(string name, List<TSolid> bodies, TSolid tool, BooleanOp.BooleanKind kind)
        {
            var r = BooleanOp.Evaluate(bodies, tool, kind, OperationPolicy.Strict);
            Console.WriteLine($"KRN03 {name}: {Krn03Describe(r)}");
            if (name.StartsWith("micro"))
                foreach (var b in r.Bodies)
                {
                    var v = SolidValidator.Validate(b);
                    var es = b.Edges.Select(e => (e, l: (e.V1.P - e.V0.P).Length)).OrderBy(x => x.l).Take(3);
                    Console.WriteLine($"      shortest edges: {string.Join("; ", es.Select(x => $"{x.e.Curve.GetType().Name} {x.l:E2}"))}");
                    foreach (var i in v.Issues.Where(i => i.Severity != ValidationSeverity.Info).Take(4)) Console.WriteLine($"      {i}");
                }
        }
        Op("disjoint-union", new List<TSolid> { Box() }, Box(100, 0, 0), BooleanOp.BooleanKind.Unite);
        Op("cavity", new List<TSolid> { Box() }, Box(10, 10, 5, 20, 10, 10), BooleanOp.BooleanKind.Subtract);
        Op("split-in-two", new List<TSolid> { Box() }, Box(25, -5, -5, 10, 50, 30), BooleanOp.BooleanKind.Subtract);
        Op("touching-union", new List<TSolid> { Box() }, Box(60, 0, 0), BooleanOp.BooleanKind.Unite);
        Op("micro-step 5e-5", new List<TSolid> { Box() }, Box(30, 0, 0, 30, 40, 20 + 5e-5), BooleanOp.BooleanKind.Unite);
        Op("micro-step 5e-8", new List<TSolid> { Box() }, Box(30, 0, 0, 30, 40, 20 + 5e-8), BooleanOp.BooleanKind.Unite);
        var diag = new List<string>(); ShellComponents.Debug = m => diag.Add(m);
        Op("poly: disjoint spheres", new List<TSolid> { PrimitiveOp.Sphere(new Vector3d(0, 0, 0), 5) }, PrimitiveOp.Sphere(new Vector3d(30, 0, 0), 5), BooleanOp.BooleanKind.Unite);
        Op("poly: sphere cavity", new List<TSolid> { Box() }, PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 5), BooleanOp.BooleanKind.Subtract);
        {
            var b = new List<TSolid> { Box() }; var w = new List<string>();
            BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(10, 20, 20), 6), w);
            Op("poly: split sphere-cut box", b, Box(25, -5, -5, 10, 50, 30), BooleanOp.BooleanKind.Subtract);
        }
        ShellComponents.Debug = null;
        foreach (var m in diag) Console.WriteLine("      " + m);
        foreach (var (name, s) in new[] { ("torus", PrimitiveOp.Torus(new Vector3d(3, -2, 7), 20, 2)), ("sphere", PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 5)),
                                          ("cone-apex", PrimitiveOp.Cone(new Vector3d(0, 0, 0), 10, 0, 20)), ("cylinder", PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10)) })
        {
            var rep0 = TopologyConventions.Normalize(s);
            var v = SolidValidator.Validate(s);
            Console.WriteLine($"KRN03 normalize {name}: conv {s.TopologyConvention} report [{rep0}] vol {AnalyticVolume.Of(s)?.ToString("G10") ?? "null"} {v.Summary()} short {v.Metrics.GetValueOrDefault("brep.shortestEdge", -1):G3}");
            foreach (var i in v.Issues.Where(i => i.Severity != ValidationSeverity.Info).Take(3)) Console.WriteLine($"      {i}");
        }
    }

    static void Krn03MeasureConnectivity()
    {
        int Comps(List<TFace> faces, Func<TFace, IEnumerable<object>> keys)
        {
            var idx = new Dictionary<TFace, int>(ReferenceEqualityComparer.Instance); for (int i = 0; i < faces.Count; i++) idx[faces[i]] = i;
            var parent = Enumerable.Range(0, faces.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var owner = new Dictionary<object, int>();
            for (int i = 0; i < faces.Count; i++)
                foreach (var k in keys(faces[i]))
                    if (owner.TryGetValue(k, out int j)) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; } else owner[k] = i;
            return Enumerable.Range(0, faces.Count).Select(Find).Distinct().Count();
        }
        void Report(string name, TSolid s)
        {
            var faces = s.Faces.ToList();
            int byEdge = Comps(faces, f => f.Edges.Cast<object>());
            int byVertex = Comps(faces, f => f.Edges.SelectMany(e => new object[] { e.V0, e.V1 }));
            int byPos = Comps(faces, f => f.Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).Select(p => (object)(Math.Round(p.X * 1e5), Math.Round(p.Y * 1e5), Math.Round(p.Z * 1e5))));
            var v = SolidValidator.Validate(s);
            Console.WriteLine($"KRN03C {name}: faces {faces.Count} byEdge {byEdge} byVertexRef {byVertex} byPosition {byPos} validator faceComps {v.Metrics.GetValueOrDefault("brep.faceComponents")} meshComps {v.Metrics.GetValueOrDefault("mesh.components")} shells {s.Shells.Count} faceted {faces.Count(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface)}");
        }
        var env = "P3D_SPLITCOMPONENTS";
        Environment.SetEnvironmentVariable(env, "0");
        {
            var b = new List<TSolid> { Box() }; var w = new List<string>();
            BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(10, 20, 20), 6), w);
            Report("sphere-cut box", b[0]);
            var r = BooleanOp.Evaluate(b, Box(25, -5, -5, 10, 50, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            foreach (var x in r.Bodies) Report("split sphere-cut box (result body)", x);
            var r2 = BooleanOp.Evaluate(new List<TSolid> { Box() }, PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 5), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            foreach (var x in r2.Bodies) Report("sphere cavity (result body)", x);
        }
    }

    static void Krn03MeasureMultibody()
    {
        List<TSolid> Two() { var r = BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(25, -5, -5, 10, 50, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict); return r.Bodies.ToList(); }
        void Op(string name, List<TSolid> b, TSolid tool, BooleanOp.BooleanKind kind)
        {
            var r = BooleanOp.Evaluate(b, tool, kind, OperationPolicy.Strict);
            Console.WriteLine($"KRN03M {name}: {r.Outcome} bodies {r.Bodies.Count} vols [{string.Join(", ", r.Bodies.Select(x => (AnalyticVolume.Of(x) ?? double.NaN).ToString("G8")))}] total {r.Bodies.Sum(x => AnalyticVolume.Of(x) ?? double.NaN):G8}");
        }
        var two = Two(); Console.WriteLine($"KRN03M start: bodies {two.Count} vols [{string.Join(", ", two.Select(x => (AnalyticVolume.Of(x) ?? double.NaN).ToString("G8")))}]");
        // сквозное отверстие через обе части: −2·π·4²·... по каждой части
        Op("subtract slot-crossing bar (hits both)", Two(), Box(10, 15, -5, 40, 10, 30), BooleanOp.BooleanKind.Subtract);
        Op("subtract hits right only", Two(), Box(45, 15, -5, 5, 10, 30), BooleanOp.BooleanKind.Subtract);
        Op("unite bridge (joins both)", Two(), Box(20, 15, 0, 20, 10, 20), BooleanOp.BooleanKind.Unite);
        Op("unite far", Two(), Box(200, 0, 0), BooleanOp.BooleanKind.Unite);
        Op("intersect spanning both", Two(), Box(10, 10, 5, 40, 20, 10), BooleanOp.BooleanKind.Intersect);
        Op("intersect left only", Two(), Box(0, 0, 0, 20, 40, 20), BooleanOp.BooleanKind.Intersect);
    }

    static void Krn03()
    {
        double Vol(TSolid b) => AnalyticVolume.Of(b) ?? SolidValidator.Validate(b).Metrics.GetValueOrDefault("mesh.volume", double.NaN);
        // KRN-03: раздельные куски результата — отдельные тела; полость — внутренняя оболочка своего тела (прежде всё — одна
        // оболочка одного тела). Аналитический и многогранный (точная форма делится по многоугольникам) пути.
        Check("KRN03-BODIES-AND-CAVITIES", () =>
        {
            void Expect(string name, OperationResult r, int bodies, int[] shells, double[] vols, double tol)
            {
                Require(r.Outcome == OperationOutcome.Success && r.Bodies.Count == bodies, $"{name}: {r.Outcome} bodies {r.Bodies.Count}");
                var got = r.Bodies.Select(b => (S: b.Shells.Count, V: Vol(b))).OrderBy(x => x.V).ToList();
                var want = shells.Zip(vols).Select(z => (S: z.First, V: z.Second)).OrderBy(x => x.V).ToList();
                for (int i = 0; i < bodies; i++)
                    Require(got[i].S == want[i].S && Math.Abs(got[i].V - want[i].V) <= tol * want[i].V, $"{name}: body {i} shells {got[i].S} vol {got[i].V:G8} vs {want[i].S} / {want[i].V:G8}");
                foreach (var b in r.Bodies)
                {
                    var v = SolidValidator.Validate(b);
                    Require(v.IsValid && v.Metrics.GetValueOrDefault("brep.faceComponents") == b.Shells.Count, $"{name}: {v.Summary()} components {v.Metrics.GetValueOrDefault("brep.faceComponents")} shells {b.Shells.Count}");
                }
            }
            var S = BooleanOp.BooleanKind.Subtract; var U = BooleanOp.BooleanKind.Unite;
            Expect("disjoint union", BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(100, 0, 0), U, OperationPolicy.Strict), 2, new[] { 1, 1 }, new[] { 48000.0, 48000 }, 1e-12);
            Expect("cavity", BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(10, 10, 5, 20, 10, 10), S, OperationPolicy.Strict), 1, new[] { 2 }, new[] { 46000.0 }, 1e-12);
            Expect("split in two", BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(25, -5, -5, 10, 50, 30), S, OperationPolicy.Strict), 2, new[] { 1, 1 }, new[] { 20000.0, 20000 }, 1e-12);
            Expect("touching union", BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(60, 0, 0), U, OperationPolicy.Strict), 1, new[] { 1 }, new[] { 96000.0 }, 1e-12);
            // те же сцены со сферами — точным маршрутом (g.14: вложенные и раздельные тела в общей булевой)
            Expect("analytic: disjoint spheres", BooleanOp.Evaluate(new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 5) }, PrimitiveOp.Sphere(new Vector3d(30, 0, 0), 5), U, OperationPolicy.Strict),
                2, new[] { 1, 1 }, new[] { 4.0 / 3 * Math.PI * 125, 4.0 / 3 * Math.PI * 125 }, 1e-12);
            {
                var ra = BooleanOp.Evaluate(new List<TSolid> { Box() }, PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 5), S, OperationPolicy.Strict);
                Expect("analytic: sphere cavity", ra, 1, new[] { 2 }, new[] { 48000 - 4.0 / 3 * Math.PI * 125 }, 1e-12);
                Require(ra.Guarantee == GuaranteeLevel.ExactAnalytic, $"sphere cavity: {ra.Guarantee}");
            }
            using var __sph = SpheresPolyhedral();   // далее — многогранный путь (точная форма делится по многоугольникам)
            var rs = BooleanOp.Evaluate(new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 5) }, PrimitiveOp.Sphere(new Vector3d(30, 0, 0), 5), U, OperationPolicy.Strict);
            Expect("polyhedral: disjoint spheres", rs, 2, new[] { 1, 1 }, new[] { 4.0 / 3 * Math.PI * 125, 4.0 / 3 * Math.PI * 125 }, 1e-9);
            Require(rs.Bodies.All(b => b.Exact != null && b.ExactAuthoritative) && rs.Bodies[0].Exact!.Solid.Polygons.Count + rs.Bodies[1].Exact!.Solid.Polygons.Count > 0, "exact form not split");
            Expect("polyhedral: sphere cavity", BooleanOp.Evaluate(new List<TSolid> { Box() }, PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 5), S, OperationPolicy.Strict), 1, new[] { 2 }, new[] { 48000 - 4.0 / 3 * Math.PI * 125 }, 1e-9);
            var b0 = new List<TSolid> { Box() }; var w = new List<string>();
            BooleanOp.Subtract(b0, PrimitiveOp.Sphere(new Vector3d(10, 20, 20), 6), w);
            var r3 = BooleanOp.Evaluate(b0, Box(25, -5, -5, 10, 50, 30), S, OperationPolicy.Strict);
            Expect("polyhedral: split sphere-cut box", r3, 2, new[] { 1, 1 }, new[] { 20000.0, 19547.61 }, 2e-4);
            Require(r3.Bodies.All(b => b.Exact != null && b.ExactAuthoritative), "exact form not split (sphere-cut box)");
        });
        // KRN-03: ШВЫ И ЦЕЛЫЕ ПОВЕРХНОСТИ. Тор (кольца-швы одной грани) нормализуется — швы не граница материала; целая сфера
        // (грань без края) — объём ±4πR³/3 по наружной нормали. Прежде: соглашение тора 0 (unanchored), объёмы — null.
        Check("KRN03-SEAMS", () =>
        {
            var t = PrimitiveOp.Torus(new Vector3d(3, -2, 7), 20, 2); var rt = TopologyConventions.Normalize(t);
            double vt = AnalyticVolume.Of(t) ?? double.NaN, et = 2 * Math.PI * Math.PI * 4 * 20;
            Require(t.TopologyConvention == TopologyConventions.Version && rt.Unanchored == 0 && Math.Abs(vt - et) <= 1e-12 * et, $"torus: conv {t.TopologyConvention} {rt} vol {vt:R} vs {et:R}");
            var sp = PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 5); TopologyConventions.Normalize(sp);
            double vs = AnalyticVolume.Of(sp) ?? double.NaN, es = 4.0 / 3 * Math.PI * 125;
            Require(Math.Abs(vs - es) <= 1e-12 * es, $"sphere vol {vs:R} vs {es:R}");
            // полость-сфера внутри бруска: объём тела = брусок − шар, внутренняя оболочка
            var r = BooleanOp.Evaluate(new List<TSolid> { Box() }, PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 5), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            Require(r.Bodies.Count == 1 && r.Bodies[0].Shells.Count == 2, $"sphere cavity: bodies {r.Bodies.Count} shells {r.Bodies.FirstOrDefault()?.Shells.Count}");
        });
        // KRN-03: микрорёбра и вырождения. Ступенька 5e-5 мм (меньше минимального элемента) — предупреждение micro-edge;
        // ступенька 5e-8 мм (меньше модельной точности) — годный результат с измеренными допусками рёбер (прежде — Success
        // с тремя ошибками curve-off-surface при полной проверке).
        Check("KRN03-SHORT-EDGES", () =>
        {
            var r1 = BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(30, 0, 0, 30, 40, 20 + 5e-5), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
            var v1 = SolidValidator.Validate(r1.Bodies[0]);
            Require(v1.IsValid && v1.Issues.Count(i => i.Code == "micro-edge") >= 2 && v1.Metrics["brep.shortestEdge"] < 1e-4, $"5e-5: {v1.Summary()} micro {v1.Metrics.GetValueOrDefault("brep.microEdges")}");
            var r2 = BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(30, 0, 0, 30, 40, 20 + 5e-8), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
            var v2 = SolidValidator.Validate(r2.Bodies[0]);
            Require(r2.Outcome == OperationOutcome.Success && v2.IsValid && v2.Issues.All(i => i.Code != "micro-edge"), $"5e-8: {r2.Outcome} {v2.Summary()} {string.Join(" / ", v2.Issues.Take(2))}");
            Require(r2.Accuracy is { } a && a.EntityTolerance > 5e-8 && a.EntityTolerance <= 1e-5, $"5e-8: accuracy {r2.Accuracy}");
            // вершина конуса — не микроребро и не ошибка
            var cone = PrimitiveOp.Cone(Vector3d.Zero, 10, 0, 20); TopologyConventions.Normalize(cone);
            var v3 = SolidValidator.ValidateBRep(cone);
            Require(v3.IsValid && v3.Issues.All(i => i.Code is not ("micro-edge" or "degenerate-edge")), $"cone apex: {v3.Summary()}");
        });
        // KRN-03: p-КРИВЫЕ каждого использования ребра согласованы с 3D-кривой и поверхностью в бюджете (модельная точность или
        // допуск ребра). Не точные — кубическая Эрмита (прежде ломаная до 1e-5 мм: замер — 9.6e-6 мм у эллипсов наклонного
        // отверстия при модельной точности 1e-7); испорченная кривая ребра — ошибка pcurve-inconsistent.
        Check("KRN03-PCURVES", () =>
        {
            bool prev = Krn02Hard; Krn02Hard = true; TSolid s;
            try { s = Krn02Scene(new Vector3d(7, -3, 11), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1, out _); }
            finally { Krn02Hard = prev; }
            var v = SolidValidator.ValidatePCurves(s);
            Require(v.IsValid && v.Metrics["pcurve.numeric"] > 0 && v.Metrics["pcurve.maxDeviation"] <= 1e-7 && v.Metrics["pcurve.missing"] == 0,
                    $"scene: {v.Summary()} numeric {v.Metrics["pcurve.numeric"]} max {v.Metrics["pcurve.maxDeviation"]:E2}");
            var p = RealisticPart(8003, 20, out _); p.Rebuild(null);
            var vr = SolidValidator.ValidatePCurves(p.Bodies[0]);
            Require(vr.IsValid && vr.Metrics["pcurve.numeric"] == 0 && vr.Metrics["pcurve.maxDeviation"] < 1e-11, $"realistic: {vr.Summary()} max {vr.Metrics["pcurve.maxDeviation"]:E2}");
            // порча: дуга ребра с радиусом +1e-5 мм на копии
            var bad = TopologyClone.Deep(s);
            var e = bad.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3);
            var a = (Parametric3D.Kernel.Curves.ArcCurve3)e.Curve;
            e.Curve = new Parametric3D.Kernel.Curves.ArcCurve3(a.Frame, a.Radius + 1e-5, a.A0, a.A1);
            bad.Guarantee = GuaranteeLevel.ExactAnalytic;
            var vb = SolidValidator.ValidatePCurves(bad);
            Require(vb.Issues.Any(i => i.Code == "pcurve-inconsistent"), $"a corrupted edge is accepted: {vb.Summary()} max {vb.Metrics["pcurve.maxDeviation"]:E2}");
        });
        // KRN-03: СВЕРЛЁНОЕ ГЛУХОЕ ОТВЕРСТИЕ (конус 118°) — аналитически, один результат при повороте и масштабе. Прежде — ни
        // разу аналитически: (1) знак компоненты «низ цилиндра + конус» без плоского кольца — правило «отверстие» вместо
        // «материал слева»; (2) после исправления — НЕВЕРНЫЙ объём при «BRep: ok»: точка пробы стороны за вершиной конуса
        // (выбор наибольшего зазора KRN-02 не знал, что H ≥ 0) переворачивала грань; проверка loop-orientation теперь это
        // ловит (P3D_CONEHALF=0 — результат отвергнут, верный многогранный).
        // KRN-03: ШАПКА СФЕРЫ — ПО ОБХОДУ КРАЯ, А НЕ ПО ОСИ ПОВЕРХНОСТИ. Купол (объединение, край — экватор) прежде
        // размечался к «первому» полюсу; булева переворачивала ось по готовой сетке, при повороте — отказ (16 ошибок сетки).
        // Теперь общая булева не смотрит на сетку; разметчик выбирает полюс по «материалу слева» (P3D_CAPBYLOOP=0 — прежний
        // выбор: ошибки сетки, проверка падает).
        Check("KRN03-SPHERE-CAP-BY-LOOP", () =>
        {
            var rx = TransformOp.Rotate(new Vector3d(30, 20, 10), new Vector3d(0.3, 0.7, 0.2), 37);
            foreach (var rot in new[] { false, true })
            foreach (var (name, op, c, r) in new[] { ("dome", Parametric3D.Kernel.Analytic.Bool2.Union, new Vector3d(30, 20, 20), 8.0), ("dome side", Parametric3D.Kernel.Analytic.Bool2.Union, new Vector3d(60, 20, 10), 6.0),
                                                     ("cut top", Parametric3D.Kernel.Analytic.Bool2.Subtract, new Vector3d(30, 20, 20), 8.0), ("cut bottom", Parametric3D.Kernel.Analytic.Bool2.Subtract, new Vector3d(20, 15, 0), 7.0),
                                                     ("intersect", Parametric3D.Kernel.Analytic.Bool2.Intersect, new Vector3d(30, 20, 20), 15.0) })
            {
                TSolid body = Box(), tool = PrimitiveOp.Sphere(c, r);
                if (rot) { body = TransformOp.Copy(body, rx); tool = PrimitiveOp.Sphere(rx.Point(c), r); }
                var res = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(body, tool, op, out var why);
                Require(res != null, $"{(rot ? "rot " : "")}{name}: declined ({why})");
                Require(Parametric3D.Kernel.Analytic.PrismBuild.Finish(res!, null) == 0, $"{(rot ? "rot " : "")}{name}: faces not meshed");
                var rep = SolidValidator.Validate(res!);
                double av = AnalyticVolume.Of(res!) ?? double.NaN, mv = rep.Metrics.GetValueOrDefault("mesh.volume", double.NaN);
                Require(rep.IsValid, $"{(rot ? "rot " : "")}{name}: {rep.Summary()}");
                Require(Math.Abs(av - mv) < ChordVolumeBound(res!), $"{(rot ? "rot " : "")}{name}: analytic {av:G8} vs mesh {mv:G8} (bound {ChordVolumeBound(res!):G3})");
            }
        });
        // KRN-03: СКРУГЛЕНИЕ ВОГНУТОГО УГЛА R4 (пирамидальный карман) СО СФЕРОЙ В ОБЩЕЙ БУЛЕВОЙ. Прежде — отказ (vertex-pinch):
        // (1) дуга «сфера × плоскость», кончающаяся на ребре кармана, давала кусок разреза с параметром за концом кривой и
        // без точки разреза — две грани кармана не делились (P3D_BNDCUT=0); (2) разрез области шапки к полюсу шёл от вершины-
        // излома, где край заходит по углу дальше вершины — многоугольник самопересекался (P3D_POLECUT=0); (3) треугольник
        // с вершиной в полюсе считался «через шов» — разметка шапки отвергалась (P3D_POLESLIVER=0).
        Check("KRN03-CONCAVE-ROUND-SPHERE", () =>
        {
            using var _c = new KernelContext(KernelSettings.Capture() with { AnalyticSpheres = true }).Enter();
            var pyr = Pyramid(4, 20, 20);
            var flipped = TransformOp.Copy(pyr, TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitX, 180));
            var moved = TransformOp.Copy(flipped, TransformOp.Move(new Vector3d(0, 0, 30.5)));
            var b = new List<TSolid> { Box(-30, -30, -30, 60, 60, 60) }; var w = new List<string>();
            BooleanOp.Subtract(b, moved, w);
            double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
            // m.f3: прямой построитель вершины берёт этот угол первым; здесь проверяется путь резцов и булевой, а прямой
            // результат сверяется с ним по объёму
            var direct = new List<TSolid> { TransformOp.Copy(b[0], TransformOp.Move(Vector3d.Zero)) };
            VertexOp.Round(direct, new Vector3d(0, 0, 10.5), 4, w);
            string last = "";
            BooleanOp.EvaluateOutcomeProbe = m => last = m;
            DirectRound.OffForTest = true;
            try { VertexOp.Round(b, new Vector3d(0, 0, 10.5), 4, w); }
            finally { BooleanOp.EvaluateOutcomeProbe = null; DirectRound.OffForTest = false; }
            double dv = AnalyticVolume.Of(direct[0]) ?? double.NaN;
            var rep = SolidValidator.Validate(b[0]);
            double av = AnalyticVolume.Of(b[0]) ?? double.NaN, mv = rep.Metrics.GetValueOrDefault("mesh.volume", double.NaN);
            Require(rep.IsValid, rep.Summary());
            Require(last == "ExactAnalytic", $"the union is {last}, not analytic");
            Require(av > v0 && Math.Abs(av - mv) < 1e-4 * mv, $"volume {v0:G10} → {av:G10}, mesh {mv:G10}");
            Require(SolidValidator.Validate(direct[0]).IsValid && Math.Abs(dv - av) < 1e-4 * av, $"direct {dv:G12} vs boolean {av:G12}");
        });
        // KRN-03 (03.5, 03.6): ИНВАРИАНТ ОБОЛОЧЕК ПОСЛЕ ЛЮБОЙ ОПЕРАЦИИ ДЕТАЛИ. Замер корпуса (P3D_SHELLSTAT=1): 2105 результатов
        // 28 видов операций — все с одной компонентой на оболочку, но полостей среди них нет; здесь — полость через операцию
        // детали и два нарушения, которые операция могла бы оставить (части в одной оболочке; полость слита с наружной
        // оболочкой). С P3D_SHELLFIX=0 нарушения доходят до результата — проверка падает.
        Check("KRN03-SHELL-INVARIANT-ALL-OPS", () =>
        {
            Parametric3D.Model.Part Cube(out Vector3d c)
            {
                var p = new Parametric3D.Model.Part("shells"); Parametric3D.Model.TestGeometry.AddCube(p, 60); p.Rebuild(null);
                var vs = p.Bodies[0].Vertices.Select(v => v.P).ToList();
                c = new Vector3d(vs.Average(v => v.X), vs.Average(v => v.Y), vs.Average(v => v.Z));
                return p;
            }
            // (1) полость операцией детали
            var p1 = Cube(out var c1);
            p1.AddFeature(new CutBox(c1.X - 10, c1.Y - 10, c1.Z - 10, 20, 20, 20)); p1.Rebuild(null);
            Require(p1.Bodies.Count == 1 && p1.Bodies[0].Shells.Count == 2, $"cavity: {p1.Bodies.Count} bodies, {p1.Bodies.Sum(b => b.Shells.Count)} shells");
            Require(ShellComponents.Invariant(p1.Bodies[0]) is null, "cavity: " + ShellComponents.Invariant(p1.Bodies[0]));
            Require(Math.Abs(p1.Volume - 208000) < 1e-6, $"cavity volume {p1.Volume}");
            // (2) операция слила полость с наружной оболочкой
            var p2 = Cube(out var c2);
            p2.AddFeature(new CutBox(c2.X - 10, c2.Y - 10, c2.Z - 10, 20, 20, 20)); p2.AddFeature(new MergeShells()); p2.Rebuild(null);
            Require(p2.Bodies.Count == 1 && p2.Bodies[0].Shells.Count == 2 && ShellComponents.Invariant(p2.Bodies[0]) is null,
                $"merged cavity: {p2.Bodies.Count} bodies, shells {string.Join(",", p2.Bodies.Select(b => b.Shells.Count))}, {ShellComponents.Invariant(p2.Bodies[0])}");
            Require(Math.Abs(p2.Volume - 208000) < 1e-6, $"merged cavity volume {p2.Volume}");
            // (3) операция положила два раздельных куска в одну оболочку (тела: куб и два куска)
            var p3 = Cube(out var c3);
            p3.AddFeature(new TwoPiecesOneShell(c3)); p3.Rebuild(null);
            Require(p3.Bodies.Count == 3 && p3.Bodies.All(b => b.Shells.Count == 1 && ShellComponents.Invariant(b) is null),
                $"two pieces: {p3.Bodies.Count} bodies, {string.Join("; ", p3.Bodies.Select(b => ShellComponents.Invariant(b) ?? "ok"))}");
        });
        // KRN-03 (03.7, 03.10): КОНТРАКТ КАРТЫ СФЕРЫ И НЕЗАВИСИМОСТЬ ОТ НЕЁ. (а) Общая булева оставляет карту операнда, пока полюс
        // не на ребре, — у всех сферических граней результата полюс карты дальше MinFeature от рёбер; (б) нарушение (карта
        // операнда с полюсом на ребре) ловит проверка B-rep pole-on-boundary; (в) сетка сферической грани не зависит от карты
        // грани (разметка в своей стереографической карте); (г) полюс проекции классификатора не зависит от хорды гранения.
        Check("KRN03-SPHERE-CHART-CONTRACT", () =>
        {
            using var _c = new KernelContext(KernelSettings.Capture() with { AnalyticSpheres = true }).Enter();
            double mf = ToleranceContext.Current.MinFeature;
            var rx = TransformOp.Rotate(new Vector3d(30, 20, 10), new Vector3d(0.3, 0.7, 0.2), 37);
            int changed0 = Parametric3D.Kernel.Analytic.GeneralBoolean.SphereChartsChanged, sphereFaces = 0;
            TSolid? domeSide = null; Vector3d domeSideC = default;
            foreach (var rot in new[] { false, true })
            foreach (var (name, op, c, r) in new[] { ("cut top", Parametric3D.Kernel.Analytic.Bool2.Subtract, new Vector3d(30, 20, 20), 8.0), ("cut corner", Parametric3D.Kernel.Analytic.Bool2.Subtract, new Vector3d(60, 40, 20), 10.0),
                                                     ("dome", Parametric3D.Kernel.Analytic.Bool2.Union, new Vector3d(30, 20, 20), 8.0), ("dome side", Parametric3D.Kernel.Analytic.Bool2.Union, new Vector3d(60, 20, 10), 6.0),
                                                     ("intersect", Parametric3D.Kernel.Analytic.Bool2.Intersect, new Vector3d(30, 20, 20), 15.0), ("cut bottom", Parametric3D.Kernel.Analytic.Bool2.Subtract, new Vector3d(20, 15, 0), 7.0) })
            {
                TSolid body = Box(), tool = PrimitiveOp.Sphere(c, r);
                if (rot) { body = TransformOp.Copy(body, rx); tool = PrimitiveOp.Sphere(rx.Point(c), r); }
                var res = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(body, tool, op, out var why);
                Require(res != null, $"{(rot ? "rot " : "")}{name}: declined ({why})");
                Require(Parametric3D.Kernel.Analytic.PrismBuild.Finish(res!, null) == 0, $"{(rot ? "rot " : "")}{name}: faces not meshed");
                foreach (var f in res!.Faces.Where(f => SurfaceDomain.Poles(GeometryAudit.AuthoritativeSurface(f)).Count > 0))
                {
                    sphereFaces++;
                    double cl = SurfaceDomain.PoleClearance(f);
                    Require(cl >= mf, $"{(rot ? "rot " : "")}{name}: chart pole {cl:E2} mm from an edge");
                }
                if (!rot && name == "dome side") { domeSide = res; domeSideC = c; }
            }
            Require(sphereFaces >= 12, $"sphere faces {sphereFaces}");
            Require(Parametric3D.Kernel.Analytic.GeneralBoolean.SphereChartsChanged > changed0, "no chart had to change: the contract case is not exercised");
            // (б) карта операнда на грани «купол сбоку» (полюс на ребре) — проверка B-rep её отвергает
            var sf = domeSide!.Faces.First(f => SurfaceDomain.Poles(GeometryAudit.AuthoritativeSurface(f)).Count > 0);
            var keepSurf = sf.Surface; bool keepRev = sf.Reversed;
            var own = PrimitiveOp.Sphere(domeSideC, 6.0).Faces.Select(GeometryAudit.AuthoritativeSurface).First(x => SurfaceDomain.Poles(x).Count > 0)!;
            var probe = SurfaceDomain.Poles(own)[0];
            sf.Surface = own; sf.Reversed = false;
            var rep = SolidValidator.Validate(domeSide);
            bool caught = rep.Issues.Any(i => i.Code == "pole-on-boundary");
            sf.Surface = keepSurf; sf.Reversed = keepRev;
            Require(caught, $"operand chart with the pole on an edge is accepted: {rep.Summary()} (pole {probe})");
            // (в) сетка сферической грани в двух разных картах — одна и та же
            var m1 = Parametric3D.Kernel.Tess.UvMesher.Mesh(sf, Parametric3D.Core.Tol.Chord, out var w1);
            var n0 = sf.NormalAt(sf.Edges.First().V0.P);
            sf.Surface = SurfaceDomain.SphereChart(domeSideC, 6.0, new Vector3d(0.6, -0.48, 0.64)); sf.Reversed = false;
            if (Vector3d.Dot(sf.NormalAt(sf.Edges.First().V0.P), n0) < 0) sf.Reversed = true;
            var m2 = Parametric3D.Kernel.Tess.UvMesher.Mesh(sf, Parametric3D.Core.Tol.Chord, out var w2);
            sf.Surface = keepSurf; sf.Reversed = keepRev;
            Require(m1 != null && m2 != null, $"mesh: {w1} / {w2}");
            double dmax = m1!.Value.P.Zip(m2!.Value.P, (a, b) => (a - b).Length).DefaultIfEmpty(0).Max();
            Require(m1.Value.P.Count == m2.Value.P.Count && m1.Value.T.SequenceEqual(m2.Value.T) && dmax < 1e-9,
                $"the mesh depends on the face chart: {m1.Value.P.Count}/{m2.Value.P.Count} points, max shift {dmax:E2}");
            // (г) полюс проекции классификатора — не от гранения: та же грань с полным и с урезанным набором фасетов. Грань больше
            // полусферы (шар над бруском, центр выше верха на 4 мм): у неё наиболее удалённое от фасетов направление зависит от них.
            var big = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(Box(), PrimitiveOp.Sphere(new Vector3d(30, 20, 24), 8), Parametric3D.Kernel.Analytic.Bool2.Union, out var whyBig);
            Require(big != null && Parametric3D.Kernel.Analytic.PrismBuild.Finish(big, null) == 0, "big cap: " + whyBig);
            sf = big!.Faces.First(f => SurfaceDomain.Poles(GeometryAudit.AuthoritativeSurface(f)).Count > 0);
            m1 = Parametric3D.Kernel.Tess.UvMesher.Mesh(sf, Parametric3D.Core.Tol.Chord, out w1);
            Require(m1 != null, "big cap mesh: " + w1);
            n0 = sf.NormalAt(sf.Edges.First().V0.P);
            var srcSurf = GeometryAudit.AuthoritativeSurface(sf)!;
            Vector3d W(int share)
            {
                var fs = new Parametric3D.Kernel.Surfaces.FacetedSurface(srcSurf, n0);
                var T = m1.Value.T; var P = m1.Value.P;
                for (int t = 0; t + 2 < T.Count * share / 4; t += 3) fs.Facets.Add((P[T[t]], P[T[t + 1]], P[T[t + 2]]));
                var was = sf.Surface; bool wasRev = sf.Reversed; var p0 = sf.Edges.First().V0.P;
                sf.Surface = fs; if (Vector3d.Dot(sf.NormalAt(p0), n0) < 0) sf.Reversed = !sf.Reversed;   // сторона грани — та же
                try { return Parametric3D.Kernel.Analytic.AnalyticFace.Of(sf, out _)!.W; } finally { sf.Surface = was; sf.Reversed = wasRev; }
            }
            var wa = W(4); var wb = W(1);
            Require((wa - wb).Length < 1e-12, $"classifier pole depends on the facets: {wa} vs {wb}");
            // и он вне грани: точка сферы в направлении полюса — вне результата-шапки (ниже верха бруска, внутри бруска)
            var polePt = new Vector3d(30, 20, 24) + wa * 8;
            Require(polePt.Z < 20 + 1e-6, $"classifier pole is on the face: {polePt}");
        });
        // KRN-03 (03.8): ДВОЯКОПЕРИОДИЧЕСКИЙ ДОМЕН ТОРА. Семейство граней тора: целый тор (края — одни швы), частичный оборот
        // 90/180/270°, пояс между двумя окружностями (D-профиль 360°) и его часть (120°); по осям, перенос с поворотом, ×0.01 и
        // ×100. Требования: годный B-rep; контуры — связные циклы; p-кривые в бюджете; точный объём и площадь каждой торической
        // грани = Паппу; сетка грани в параметрах — с площадью в пределах хорды (кроме ×0.01: хорда 0.02 мм больше половины
        // трубки 0.04 мм — дефект хорды отображения, KRN-09). Мутации: P3D_MATSIDEMID=0 (пояс вывернут: объём −3426 мм³ при
        // «BRep: ok» — теперь ловит inverted-solid), P3D_FULLTORUS=0 (целый тор не размечается в параметрах).
        Check("KRN03-TORUS-DOMAIN", () =>
        {
            foreach (var (name, body, vol) in TorusFamily())
            {
                if (body.TopologyConvention < TopologyConventions.Version) TopologyConventions.Normalize(body);
                var rep = SolidValidator.Validate(body);
                Require(rep.IsValid, $"{name}: {rep.Summary()} {string.Join(" / ", rep.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(2))}");
                Require(rep.Metrics.GetValueOrDefault("brep.disconnectedLoops", 0) == 0, $"{name}: a loop is not one cycle");
                double av = AnalyticVolume.Of(body) ?? double.NaN;
                Require(Math.Abs(av - vol) <= 1e-12 * vol, $"{name}: exact volume {av:G17} vs Pappus {vol:G17}");
                bool small = name.StartsWith("small");
                double k = name.StartsWith("small") ? 0.01 : name.StartsWith("large") ? 100 : 1, R = 20 * k, r = 4 * k;
                double deg = double.Parse(name.Split(' ')[^1].TrimEnd('°'));
                bool d = name.Contains(" D ");
                double expArea = d ? Math.PI * r * 2 * Math.PI * (R + 2 * r / Math.PI) * deg / 360 : 4 * Math.PI * Math.PI * R * r * deg / 360;
                var pcs = PCurves.Measure(body);
                foreach (var f in body.Faces.Where(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Torus))
                {
                    double dev = pcs.Where(x => x.Ce.Loop?.Face == f).Select(x => x.PC == null ? double.PositiveInfinity : x.Dev).DefaultIfEmpty(0).Max();
                    Require(dev <= ToleranceContext.Current.ModelAccuracy * Math.Max(1, k), $"{name}: p-curve deviation {dev:E2}");
                    double ea = AnalyticVolume.FaceArea(f) ?? double.NaN;
                    Require(Math.Abs(ea - expArea) <= 1e-9 * expArea, $"{name}: exact torus face area {ea:G12} vs Pappus {expArea:G12}");
                    if (small) continue;
                    var m = Parametric3D.Kernel.Tess.UvMesher.Mesh(f, Parametric3D.Core.Tol.Chord, out var why);
                    Require(m != null, $"{name}: torus face not meshed in its parameters: {why}");
                    double ma = Enumerable.Range(0, m!.Value.T.Count / 3).Sum(t => Vector3d.Cross(m.Value.P[m.Value.T[3 * t + 1]] - m.Value.P[m.Value.T[3 * t]], m.Value.P[m.Value.T[3 * t + 2]] - m.Value.P[m.Value.T[3 * t]]).Length / 2);
                    Require(Math.Abs(ma - expArea) <= 5e-3 * expArea, $"{name}: mesh area {ma:G8} vs {expArea:G8}");
                }
            }
        });
        Check("KRN03-DRILL-TIP", () =>
        {
            double h = 4 / Math.Tan(59 * Math.PI / 180), v0 = 48000 - Math.PI * 16 * 10 - Math.PI * 16 * h / 3;
            foreach (var (name, o, u, v, k) in new[] { ("axis", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.0),
                                                     ("rotated", new Vector3d(7, -3, 11), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1.0),
                                                     ("small", Vector3d.Zero, new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 0.05) })
            {
                var U = u.Normalized(); var V = (v - U * Vector3d.Dot(v, U)).Normalized(); var W = Vector3d.Cross(U, V);
                var f0 = new Parametric3D.Kernel.Frame3(o, U, V);
                Vector3d Q(double x, double y) => o + (U * x + V * y) * k;
                var p = new[] { Q(0, 0), Q(60, 0), Q(60, 40), Q(0, 40) };
                var b = new List<TSolid> { ExtrudeOp.Build(ProfileBuilder.Build(f0, Enumerable.Range(0, 4).Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 4])).ToList()), W * 20 * k) };
                var r = BooleanOp.Evaluate(b, Krn03Drill(o, u, v, 30, 20, 20, 4, 10, k), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                var s = r.Bodies[0]; double vol = (AnalyticVolume.Of(s) ?? double.NaN) / (k * k * k);
                var rep = SolidValidator.ValidateBRep(s);
                Require(r.Guarantee == GuaranteeLevel.ExactAnalytic && rep.IsValid && Math.Abs(vol - v0) <= 1e-9 * v0 && s.Faces.Count() == 8,
                        $"{name}: {r.Guarantee} vol {vol:R} vs {v0:R} faces {s.Faces.Count()} {rep.Summary()}");
            }
        });
        // KRN-03: операция над несколькими телами — каждое тело своей операцией (прежде многогранные ядра брали bodies[0]:
        // вычитание через две части резало одну, мост присоединялся к одному телу, пересечение оставляло второе целым).
        Check("KRN03-MULTIBODY-OPS", () =>
        {
            List<TSolid> Two() => BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(25, -5, -5, 10, 50, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict).Bodies.ToList();
            void Expect(string name, BooleanOp.BooleanKind k, TSolid tool, double[] vols)
            {
                var r = BooleanOp.Evaluate(Two(), tool, k, OperationPolicy.Strict);
                var got = r.Bodies.Select(Vol).OrderBy(v => v).ToArray(); var want = vols.OrderBy(v => v).ToArray();
                Require(r.Outcome == (want.Length == 0 ? OperationOutcome.Empty : OperationOutcome.Success) && got.Length == want.Length && got.Zip(want).All(z => Math.Abs(z.First - z.Second) < 1e-6),
                        $"{name}: {r.Outcome} [{string.Join(", ", got)}] vs [{string.Join(", ", want)}]");
                Require(r.Guarantee == GuaranteeLevel.ExactAnalytic || want.Length == 0, $"{name}: {r.Guarantee}");
            }
            Expect("subtract through both", BooleanOp.BooleanKind.Subtract, Box(10, 15, -5, 40, 10, 30), new[] { 17000.0, 17000 });
            Expect("subtract right only", BooleanOp.BooleanKind.Subtract, Box(45, 15, -5, 5, 10, 30), new[] { 19000.0, 20000 });
            Expect("unite bridge", BooleanOp.BooleanKind.Unite, Box(20, 15, 0, 20, 10, 20), new[] { 42000.0 });
            Expect("unite far", BooleanOp.BooleanKind.Unite, Box(200, 0, 0), new[] { 20000.0, 20000, 48000 });
            Expect("intersect both", BooleanOp.BooleanKind.Intersect, Box(10, 10, 5, 40, 20, 10), new[] { 3000.0, 3000 });
            Expect("intersect left only", BooleanOp.BooleanKind.Intersect, Box(0, 0, 0, 20, 40, 20), new[] { 16000.0 });
            Expect("intersect none", BooleanOp.BooleanKind.Intersect, Box(200, 0, 0), new double[0]);
        });
    }
}


static partial class Program
{
    /// <summary>KRN-03, замер: p-кривые полурёбер — точные/по выборке/пропущенные, отклонения по типам поверхностей.</summary>
    static void Krn03MeasurePCurves()
    {
        void Report(string name, TSolid s)
        {
            var st = PCurves.Build(s);
            var byKind = new Dictionary<string, (int N, int Exact, int Skipped, double MaxDev)>();
            foreach (var f in s.Faces)
            {
                var src = GeometryAudit.AuthoritativeSurface(f);
                string kind = src == null ? "none" : (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(src)?.GetType().Name ?? src.GetType().Name);
                foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
                {
                    var (n, ex, sk, md) = byKind.GetValueOrDefault(kind);
                    byKind[kind] = (n + 1, ex + (ce.PCurve?.IsExact == true ? 1 : 0), sk + (ce.PCurve == null ? 1 : 0), Math.Max(md, ce.PCurveDeviation));
                }
            }
            Console.WriteLine($"KRN03P {name}: {st}");
            foreach (var f in s.Faces)
                foreach (var ce in f.Loops.SelectMany(l => l.CoEdges).Where(c => c.PCurve != null && !c.PCurve.IsExact))
                {
                    var src = GeometryAudit.AuthoritativeSurface(f);
                    if (ce.Edge.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3)
                    {
                        var fs = ce.Edge.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Select(g => { var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(g)!); return sh switch { Parametric3D.Kernel.Geometry.Ssi.Cone k => $"Cone(apex {k.Apex:0.###} axis {k.Axis:0.###})", Parametric3D.Kernel.Geometry.Ssi.Cylinder c => $"Cyl(P {c.P:0.###} axis {c.Axis:0.###} R {c.R})", Parametric3D.Kernel.Geometry.Ssi.Plane p => $"Plane(P {p.P:0.###} N {p.N:0.###})", _ => sh?.GetType().Name ?? "-" }; });
                        Console.WriteLine($"      traced edge between: {string.Join(" × ", fs)} ends {ce.Edge.V0.P:0.###} {ce.Edge.V1.P:0.###}");
                    }
                    Console.WriteLine($"      sampled: surface {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(src!)?.GetType().Name ?? src?.GetType().Name} curve {ce.Edge.Curve.GetType().Name} dev {ce.PCurveDeviation:E2} pcurve {ce.PCurve!.GetType().Name}");
                }
            foreach (var (k, v) in byKind.OrderBy(x => x.Key)) Console.WriteLine($"      {k}: coedges {v.N} exact {v.Exact} skipped {v.Skipped} maxDev {v.MaxDev:E2}");
        }
        bool prev = Krn02Hard; Krn02Hard = true;
        try
        {
            Report("scene axis", Krn02Scene(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1, out _));
            Report("scene rotated", Krn02Scene(new Vector3d(7, -3, 11), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1, out _));
        }
        finally { Krn02Hard = prev; }
        { var b = new List<TSolid> { Box() }; var w = new List<string>(); BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), w); Report("sphere cut (polyhedral)", b[0]); }
        { var t = PrimitiveOp.Torus(new Vector3d(3, -2, 7), 20, 2); TopologyConventions.Normalize(t); Report("torus", t); }
        { var b = new List<TSolid> { Box() }; var w = new List<string>(); BooleanOp.Subtract(b, TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(30, 20, -20), 5, 60), TransformOp.Rotate(new Vector3d(30, 20, 10), Vector3d.UnitY, 17)), w); Report("oblique hole (general)", b[0]); }
        foreach (int seed in new[] { 8001, 8003 })
        {
            var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
            int i = 0; foreach (var b in p.Bodies) Report($"realistic {seed} body {i++}", b);
        }
    }
}

static partial class Program
{
    static void Krn03MeasurePCurveValidation()
    {
        void R(string name, TSolid s)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var v = SolidValidator.ValidatePCurves(s);
            Console.WriteLine($"KRN03V {name}: {s.Guarantee} {v.Summary()} exact {v.Metrics["pcurve.exact"]} numeric {v.Metrics["pcurve.numeric"]} missing {v.Metrics["pcurve.missing"]} max {v.Metrics["pcurve.maxDeviation"]:E2} inconsistent {v.Metrics["pcurve.inconsistent"]} [{sw.ElapsedMilliseconds} ms]");
            foreach (var i in v.Issues.Take(2)) Console.WriteLine("      " + i);
        }
        bool prev = Krn02Hard; Krn02Hard = true;
        try { R("scene rotated", Krn02Scene(new Vector3d(7, -3, 11), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1, out _)); }
        finally { Krn02Hard = prev; }
        { var b = new List<TSolid> { Box() }; var w = new List<string>(); BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), w); R("sphere cut (polyhedral)", b[0]); }
        foreach (int seed in new[] { 8001, 8003, 8004 }) { var p = RealisticPart(seed, 20, out _); p.Rebuild(null); R($"realistic {seed}", p.Bodies[0]); }
    }
}

static partial class Program
{
    static void Krn03MeasureBigFar()
    {
        bool prev = Krn02Hard; Krn02Hard = true;
        try
        {
            foreach (var (o, k) in new[] { (new Vector3d(4e5, -4e5, 3e5), 1000.0), (Vector3d.Zero, 1000.0), (new Vector3d(4e5, -4e5, 3e5), 1.0) })
            {
                var s = Krn02Scene(o, new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), k, out var g);
                var v = SolidValidator.ValidatePCurves(s);
                Console.WriteLine($"KRN03B o {o} k {k}: {g} {v.Summary()} numeric {v.Metrics["pcurve.numeric"]} max {v.Metrics["pcurve.maxDeviation"]:E2}");
                foreach (var (ce, pc, dev) in PCurves.Measure(s).Where(x => x.Dev > 1e-8).OrderByDescending(x => x.Dev).Take(4))
                {
                    var c = ce.Edge.Curve; double off = 0;
                    var shapes = ce.Edge.CoEdges.Select(x => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(x.Loop!.Face!)!)).OfType<Parametric3D.Kernel.Geometry.Ssi.Shape>().ToList();
                    for (int i = 0; i <= 20000; i++) { var p = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 20000.0); foreach (var sh in shapes) off = Math.Max(off, Parametric3D.Kernel.Analytic.OperandTolerance.Distance(sh, p)); }
                    double len = 0; { var q = c.PointAt(c.T0); for (int i = 1; i <= 200; i++) { var p = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 200.0); len += (p - q).Length; q = p; } }
                    Console.WriteLine($"      {pc?.GetType().Name} nodes {(pc as Parametric3D.Kernel.Curves.Hermite2)?.P.Length} on {GeometryAudit.AuthoritativeSurface(ce.Loop!.Face!)?.GetType().Name} curve {c.GetType().Name} len {len:G4} pcurveDev {dev:E2} curveOffSurfaces(20000 pts) {off:E2} edgeTol {ce.Edge.Tolerance:E2}");
                }
            }
        }
        finally { Krn02Hard = prev; }
    }
}

static partial class Program
{
    /// <summary>Сверло: цилиндр r на глубину d от top и конус 118° (вершина вниз), ось −W рамки, вращением профиля.</summary>
    static TSolid Krn03Drill(Vector3d o, Vector3d U, Vector3d V, double cx, double cy, double top, double r, double d, double k = 1)
    {
        U = U.Normalized(); V = (V - U * Vector3d.Dot(V, U)).Normalized(); var W = Vector3d.Cross(U, V);
        var c = o + (U * cx + V * cy) * k;
        Vector3d P(double x, double z) => c + (U * x + W * z) * k;
        double tip = top - d - r / Math.Tan(59 * Math.PI / 180);
        var p = new[] { P(0, top + 2), P(r, top + 2), P(r, top - d), P(0, tip) };
        var prof = ProfileBuilder.Build(new Parametric3D.Kernel.Frame3(c, U, W), Enumerable.Range(0, 4)
            .Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 4])).ToList());
        var t = RevolveOp.Build(prof, new Parametric3D.Kernel.Frame3(c, U, V), 0, 2 * Math.PI);
        if (Environment.GetEnvironmentVariable("P3D_DRILLNORM") == "1") TopologyConventions.Normalize(t);
        return t;
    }

    static void Krn03MeasureConeTip()
    {
        var cases = new (string N, Vector3d O, Vector3d U, Vector3d V, double K)[]
        {
            ("axis", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1),
            ("rotated", new Vector3d(7, -3, 11), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1),
            ("rotated far", new Vector3d(3e4, -2e4, 1e4), new Vector3d(-0.3, 0.2, 0.93), new Vector3d(0.9, 0.4, 0.2), 1),
            ("small", Vector3d.Zero, new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 0.05),
        };
        foreach (var c in cases)
        foreach (int variant in new[] { 0, 1, 2 })
        {
            var why = new List<string>(); BooleanOp.AnalyticRouteDebug = m => why.Add(m);
            string res;
            try
            {
                var U = c.U.Normalized(); var V = (c.V - U * Vector3d.Dot(c.V, U)).Normalized(); var W = Vector3d.Cross(U, V);
                TSolid Rect(double x0, double y0, double z0, double w, double dd, double h)
                {
                    var f = new Parametric3D.Kernel.Frame3(c.O + (U * x0 + V * y0 + W * z0) * c.K, U, V);
                    Vector3d Q(double x, double y) => f.Origin + (U * x + V * y) * c.K;
                    var p = new[] { Q(0, 0), Q(w, 0), Q(w, dd), Q(0, dd) };
                    return ExtrudeOp.Build(ProfileBuilder.Build(f, Enumerable.Range(0, 4).Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 4])).ToList()), W * h * c.K);
                }
                var b = new List<TSolid> { Rect(0, 0, 0, 60, 40, 20) }; var w = new List<string>();
                BooleanOp.Subtract(b, Krn03Drill(c.O, c.U, c.V, 30, 20, 20, 4, 10, c.K), w);          // острие на z ≈ 7.6
                if (variant >= 1)
                {
                    // поперечное отверстие r1 вдоль V через область острия (z = 8.5: пересекает конус у вершины)
                    var f = new Parametric3D.Kernel.Frame3(c.O + (U * 30 + V * -5 + W * 8.5) * c.K, W, U);
                    var hole = ExtrudeOp.Build(ProfileBuilder.Build(f, new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(f, 1 * c.K, 0, 2 * Math.PI) }), V * 50 * c.K);
                    BooleanOp.Subtract(b, hole, w);
                }
                if (variant >= 2)
                    BooleanOp.Subtract(b, Krn03Drill(c.O, c.U, c.V, 31.5, 20, 8, 0.6, 0.2, c.K), w);   // маленькое сверло в стенку острия
                var rep = SolidValidator.Validate(b[0]);
                res = $"{b[0].Guarantee} fev {b[0].Faces.Count()}/{b[0].Edges.Count()}/{b[0].Vertices.Count()} vol {(AnalyticVolume.Of(b[0]) ?? double.NaN) / (c.K * c.K * c.K):G10} {rep.Summary()}";
            }
            catch (Exception ex) { res = "EXC " + ex.Message; }
            finally { BooleanOp.AnalyticRouteDebug = null; }
            Console.WriteLine($"KRN03T {c.N} v{variant}: {res}");
            foreach (var m in why.Where(m => m.Contains("declin") || m.Contains("invalid") || m.Contains("exception")).Where(m => !m.Contains("not along"))) Console.WriteLine("      " + m[..Math.Min(250, m.Length)]);
        }
    }
}

static partial class Program
{
    static void Krn03MeasureDrillTopology()
    {
        var t = Krn03Drill(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 30, 20, 20, 4, 10);
        foreach (bool norm in new[] { false, true })
        {
            if (norm) { var rep = TopologyConventions.Normalize(t); Console.WriteLine($"normalized: {rep}"); }
            foreach (var f in t.Faces)
            {
                var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)!);
                Console.WriteLine($"DRILL face {f.Tag} {sh?.GetType().Name} reversed {f.Reversed} loops {f.Loops.Count}");
                foreach (var l in f.Loops)
                {
                    Console.WriteLine($"   loop outer={l == f.Outer} coedges {l.CoEdges.Count}");
                    foreach (var ce in l.CoEdges)
                    {
                        var a = PCurves.At(ce, 0); var m = PCurves.At(ce, 0.25);
                        double th(Vector3d p) => Math.Atan2(p.Y - 20, p.X - 30);
                        double dth = th(m) - th(a); while (dth > Math.PI) dth -= 2 * Math.PI; while (dth < -Math.PI) dth += 2 * Math.PI;
                        Console.WriteLine($"      {ce.Edge.Curve.GetType().Name} rev {ce.Reversed} start ({a.X:0.##},{a.Y:0.##},{a.Z:0.##}) z→{m.Z:0.##} dθ {dth:+0.###;-0.###} uses {ce.Edge.CoEdges.Count} closed {ReferenceEquals(ce.Edge.V0, ce.Edge.V1)}");
                    }
                }
            }
        }
    }
}

static partial class Program
{
    static void Krn03MeasureDrillVolume()
    {
        TopologyConventions.AnchorDebug = m => Console.WriteLine("   ANCHOR " + m);
        Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => { if (m.StartsWith("region dart") || m.StartsWith("assembled") || m.StartsWith("side probe")) Console.WriteLine("   GB " + m); };
        var b = new List<TSolid> { Box() }; var w = new List<string>();
        BooleanOp.Subtract(b, Krn03Drill(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 30, 20, 20, 4, 10), w);
        var s = b[0]; var rep = SolidValidator.Validate(s);
        double h = 4 / Math.Tan(59 * Math.PI / 180), exact = 48000 - Math.PI * 16 * 10 - Math.PI * 16 * h / 3;
        Console.WriteLine($"DRILLV {s.Guarantee} analytic {AnalyticVolume.Of(s):R} mesh {rep.Metrics.GetValueOrDefault("mesh.volume"):R} formula {exact:R}");
        foreach (var f in s.Faces)
        {
            var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)!);
            Console.WriteLine($"   face {f.Tag} {sh?.GetType().Name} rev {f.Reversed} loops {f.Loops.Count} I={AnalyticVolume.FaceIntegral(f):G8} area {AnalyticVolume.FaceArea(f):G8} surf {f.Surface.GetType().Name}/{GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}");
            if (f.Tag.StartsWith("revolve"))
                foreach (var l in f.Loops)
                    foreach (var ce in l.CoEdges)
                    {
                        var a = PCurves.At(ce, 0); var m = PCurves.At(ce, 0.25);
                        double th(Vector3d p) => Math.Atan2(p.Y - 20, p.X - 30);
                        double dth = th(m) - th(a); while (dth > Math.PI) dth -= 2 * Math.PI; while (dth < -Math.PI) dth += 2 * Math.PI;
                        var n = f.NormalAt(a + new Vector3d(-0.01 * (a.X - 30), -0.01 * (a.Y - 20), f.Tag.EndsWith("0.1]") ? -0.01 : 0.5));
                        Console.WriteLine($"      {ce.Edge.Curve.GetType().Name} rev {ce.Reversed} z {a.Z:0.###} dθ {dth:+0.###;-0.###} normalAt {n:0.###} faces [{string.Join(",", ce.Edge.CoEdges.Select(c => c.Loop?.Face?.Tag))}]");
                    }
        }
    }
}

static partial class Program
{
    static void Krn03MeasureFarClassifier()
    {
        var U = new Vector3d(0.8, 0.5, 0.33).Normalized(); var V0 = new Vector3d(-0.4, 0.9, 0.1);
        var V = (V0 - U * Vector3d.Dot(V0, U)).Normalized(); var W = Vector3d.Cross(U, V); double k = 0.05;
        TSolid Build(Vector3d o)
        {
            bool prev = Krn02Hard; Krn02Hard = false;
            try { return Krn02Scene(o, U, V, k, out _); } finally { Krn02Hard = prev; }
        }
        var oFar = new Vector3d(3e5, -0.8 * 3e5, 0.35 * 3e5);
        var a0 = Build(Vector3d.Zero); var a1 = Build(oFar);
        Console.WriteLine($"FARCL bodies: near {AnalyticVolume.Of(a0) / (k * k * k):R} far {AnalyticVolume.Of(a1) / (k * k * k):R} faces {a0.Faces.Count()}/{a1.Faces.Count()} near {SolidValidator.ValidateBRep(a0).Summary()} far {SolidValidator.ValidateBRep(a1).Summary()}");
        {
            var f0 = a0.Faces.ToList(); var f1 = a1.Faces.ToList(); int bad = 0;
            for (int i = 0; i < f0.Count; i++)
            {
                if (f0[i].Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface fs0 || fs0.Facets.Count == 0) continue;
                var (ta, tb, tc) = fs0.Facets[fs0.Facets.Count / 2]; var p = (ta + tb + tc) / 3;
                var tri = Vector3d.Cross(tb - ta, tc - ta).Normalized();
                var q = p + oFar;
                var g = f1.Where(x => x.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fz && fz.Facets.Count > 0)
                          .OrderBy(x => ((Parametric3D.Kernel.Surfaces.FacetedSurface)x.Surface).Facets.Min(t => ((t.A + t.B + t.C) / 3 - q).LengthSquared)).First();
                var n0 = f0[i].NormalAt(p); var n1 = g.NormalAt(q);
                bool flip0 = Vector3d.Dot(n0, tri) < 0;
                if ((n0 - n1).Length > 1e-6 || flip0) { bad++; Console.WriteLine($"   NORMAL face {i} {f0[i].Tag} {GeometryAudit.AuthoritativeSurface(f0[i])?.GetType().Name} near {n0:0.###} far {n1:0.###} facet {tri:0.###}"); }
            }
            Console.WriteLine($"FARCL normals compared: differing {bad}");
        }
        var c0 = new Parametric3D.Kernel.Analytic.Classifier(a0); var c1 = new Parametric3D.Kernel.Analytic.Classifier(a1);
        var rnd = new Random(5); int mism = 0, und0 = 0, und1 = 0, n = 0;
        var pts = a0.Faces.SelectMany(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? fs.Facets.Select(t => (t.A + t.B + t.C) / 3) : Enumerable.Empty<Vector3d>()).ToList();
        foreach (var p in pts.OrderBy(_ => rnd.Next()).Take(1000))
            foreach (double off in new[] { 2.5e-5, -2.5e-5 })
            {
                var dir = new Vector3d(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5).Normalized();
                var q0 = p + dir * Math.Abs(off) * (off > 0 ? 1 : -1);
                var q1 = q0 + oFar;
                var r0 = c0.Classify(q0); var r1 = c1.Classify(q1); n++;
                if (r0 is null or 0) und0++; if (r1 is null or 0) und1++;
                if (r0 is 1 or -1 && r1 is 1 or -1 && r0 != r1) { mism++; if (mism <= 5) Console.WriteLine($"   mismatch at local {(q0.X):0.######},{q0.Y:0.######},{q0.Z:0.######}: near {r0} far {r1}"); }
            }
        Console.WriteLine($"FARCL probes {n}: mismatches {mism}, undecided near {und0} far {und1}");
    }
}

static partial class Program
{
    static void Krn03MeasureObliqueTool()
    {
        var U = new Vector3d(0.8, 0.5, 0.33).Normalized(); var V0 = new Vector3d(-0.4, 0.9, 0.1);
        var V = (V0 - U * Vector3d.Dot(V0, U)).Normalized(); var W = Vector3d.Cross(U, V); double k = 0.05;
        foreach (double d in new[] { 0.0, 3e4, 3e5 })
        {
            var o = new Vector3d(d, -0.8 * d, 0.35 * d);
            double a = 25 * Math.PI / 180; var W2 = W * Math.Cos(a) + V * Math.Sin(a); var V2 = Vector3d.Cross(W2, U);
            var c = o + (U * 15 + V * 30) * k - W2 * (15 * k);
            var f = new Parametric3D.Kernel.Frame3(c, U, V2);
            var tool = ExtrudeOp.Build(ProfileBuilder.Build(f, new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(f, 3 * k, 0, 2 * Math.PI) }), W2 * 60 * k);
            var copy = TopologyClone.Deep(tool); var rep = TopologyConventions.Normalize(copy);
            var mesh = Parametric3D.Kernel.Tess.Tessellator.Tessellate(copy);
            foreach (var fc in copy.Faces)
            {
                var nl = AnalyticVolume.NormalIntegral(fc);
                var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); fc.Surface.TessellateFace(fc, mb); var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
                var nf = Vector3d.Zero; for (int i = 0; i + 2 < md.Indices.Length; i += 3) { var A = mb.PositionsD[md.Indices[i]]; var B = mb.PositionsD[md.Indices[i + 1]]; var C = mb.PositionsD[md.Indices[i + 2]]; nf += Vector3d.Cross(B - A, C - A) / 2; }
                Console.WriteLine($"OBLT d {d:G3} face {fc.Tag} {fc.Surface.GetType().Name} rev {fc.Reversed} Nl {nl:0.#####} Nmesh {nf:0.#####}");
            }
            Console.WriteLine($"OBLT d {d:G3} normalize: {rep}  vol {AnalyticVolume.Of(copy) / (k * k * k):G8}");
        }
    }
}

static partial class Program
{
    static void Krn03MeasureSpheres()
    {
        var GB = typeof(Parametric3D.Kernel.Analytic.GeneralBoolean);
        int total = 0, analytic = 0, bad = 0;
        foreach (var rot in new[] { false, true })
        foreach (var (name, kind, c, r) in new[] {
            ("cut top", BooleanOp.BooleanKind.Subtract, new Vector3d(30, 20, 20), 8.0),
            ("cut corner", BooleanOp.BooleanKind.Subtract, new Vector3d(60, 40, 20), 10.0),
            ("cavity", BooleanOp.BooleanKind.Subtract, new Vector3d(30, 20, 10), 5.0),
            ("dome", BooleanOp.BooleanKind.Unite, new Vector3d(30, 20, 20), 8.0),
            ("dome side", BooleanOp.BooleanKind.Unite, new Vector3d(60, 20, 10), 6.0),
            ("intersect", BooleanOp.BooleanKind.Intersect, new Vector3d(30, 20, 20), 15.0),
            ("cut bottom", BooleanOp.BooleanKind.Subtract, new Vector3d(20, 15, 0), 7.0) })
        {
            if (Environment.GetEnvironmentVariable("P3D_KRN03S_ONLY") is { } only && only != (rot ? "rot " : "") + name) continue;
            var uvLog = Environment.GetEnvironmentVariable("P3D_KRN03S_UV") == "1";
            if (uvLog) Parametric3D.Kernel.Tess.UvMesher.Debug = m => Console.WriteLine("UV " + m[..Math.Min(m.StartsWith("poledomain") ? 200000 : 300, m.Length)]);
            if (Environment.GetEnvironmentVariable("P3D_KRN03S_FACETS") == "1")
                BooleanOp.GeneralInvalidProbe = rb =>
                {
                    foreach (var f in rb.Faces)
                        if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx && Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)!) is Parametric3D.Kernel.Geometry.Ssi.Sphere)
                        {
                            var badf = fsx.Facets.Where(t => Vector3d.Dot(Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1), f.NormalAt((t.Item1 + t.Item2 + t.Item3) / 3)) <= 0).ToList();
                            Console.WriteLine($"      sphere face {f.Id} facets {fsx.Facets.Count} against-normal {badf.Count} loops {f.Loops.Count} edges {f.Edges.Count()}");
                            foreach (var (a, b, c) in badf.Take(6)) Console.WriteLine($"        bad {a} | {b} | {c} area {Vector3d.Cross(b - a, c - a).Length / 2:E2}");
                        }
                };
            var why = new List<string>(); BooleanOp.AnalyticRouteDebug = m => why.Add(m);
            TSolid body = Box(), tool = PrimitiveOp.Sphere(c, r);
            if (rot) { var rx = TransformOp.Rotate(new Vector3d(30, 20, 10), new Vector3d(0.3, 0.7, 0.2), 37); body = TransformOp.Copy(body, rx); tool = PrimitiveOp.Sphere(rx.Point(c), r); }
            var res = BooleanOp.Evaluate(new List<TSolid> { body }, tool, kind, OperationPolicy.Strict);
            BooleanOp.AnalyticRouteDebug = null; Parametric3D.Kernel.Tess.UvMesher.Debug = null; BooleanOp.GeneralInvalidProbe = null;
            total++;
            string vs = "-";
            if (res.Bodies.Count > 0)
            {
                var b = res.Bodies[0]; var rep = SolidValidator.Validate(b);
                double av = AnalyticVolume.Of(b) ?? double.NaN, mv = rep.Metrics.GetValueOrDefault("mesh.volume", double.NaN);
                if (res.Guarantee == GuaranteeLevel.ExactAnalytic) analytic++;
                bool ok = rep.IsValid && Math.Abs(av - mv) < ChordVolumeBound(b);
                if (!ok) bad++;
                vs = $"{(ok ? "" : "BAD ")}{res.Guarantee} analytic {av:G8} mesh {mv:G8} bound {ChordVolumeBound(b):G3} {rep.Summary()}";
            }
            if (res.Bodies.Count > 0 && Environment.GetEnvironmentVariable("P3D_KRN03S_ISSUES") == "1")
                foreach (var iss in SolidValidator.Validate(res.Bodies[0]).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4)) Console.WriteLine("      issue " + iss.ToString()[..Math.Min(300, iss.ToString().Length)]);
            if (res.Bodies.Count > 0)
            {
                double worst = 0; string where = "";
                foreach (var f in res.Bodies[0].Faces)
                {
                    var sf = GeometryAudit.AuthoritativeSurface(f); if (sf == null) continue;
                    var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(sf);
                    foreach (var v in f.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct())
                    {
                        double d = sh switch
                        {
                            Parametric3D.Kernel.Geometry.Ssi.Sphere sp => Math.Abs((v.P - sp.C).Length - sp.R),
                            Parametric3D.Kernel.Geometry.Ssi.Plane pl => Math.Abs(Vector3d.Dot(v.P - pl.P, pl.N.Normalized())),
                            _ => (sf.PointAt(sf.ProjectToUV(v.P)) - v.P).Length,
                        };
                        if (d > worst) { worst = d; where = $"{sh?.GetType().Name} at {v.P}"; }
                    }
                }
                vs += $" vertexOff {worst:E2} {where}";
            }
            Console.WriteLine($"KRN03S {(rot ? "rot " : "")}{name}: {res.Outcome} {vs} uvPole {Parametric3D.Kernel.Tess.UvMesher.PolePicksByLoop}/{Parametric3D.Kernel.Tess.UvMesher.PoleUndecided} cut {Parametric3D.Kernel.Tess.UvMesher.PoleCutsMoved} cap {Parametric3D.Kernel.Tess.ExactFaceMesher.CapByLoopPicks}/{Parametric3D.Kernel.Tess.ExactFaceMesher.CapLoopUndecided}");
            foreach (var m in why.Where(m => m.Contains("general") || Environment.GetEnvironmentVariable("P3D_KRN03S_ALLROUTE") == "1")) Console.WriteLine("      " + m[..Math.Min(200, m.Length)]);
        }
        Console.WriteLine($"KRN03S total {total} analytic {analytic} bad {bad}");
    }
}

static partial class Program
{
    /// <summary>KRN-03 замер: скругление вогнутого угла R4 (пирамидальный карман) — каждая булева, маршрут, проверка.</summary>
    static void Krn03MeasureConcaveRound()
    {
        var pyr = Pyramid(4, 20, 20);
        var flipped = TransformOp.Copy(pyr, TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitX, 180));
        var moved = TransformOp.Copy(flipped, TransformOp.Move(new Vector3d(0, 0, 30.5)));
        var b = new List<TSolid> { Box(-30, -30, -30, 60, 60, 60) }; var w = new List<string>();
        BooleanOp.Subtract(b, moved, w);
        int step = 0;
        BooleanOp.AnalyticRouteDebug = m => Console.WriteLine($"  [{step}] route: {(m.Contains("general invalid") ? m : m[..Math.Min(220, m.Length)])}");
        BooleanOp.EvaluateProbe = (bs, other, kind) =>
        {
            step++;
            bool last = Environment.GetEnvironmentVariable("P3D_VCSTEP") == step.ToString();
            Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = last ? (m, p) => Console.WriteLine($"    pt {m} ({p.X:0.####}, {p.Y:0.####}, {p.Z:0.####})") : null;
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = last ? m => Console.WriteLine("    fs " + m) : null;
            Console.WriteLine($"STEP {step} {kind}: operand faces {bs[0].Faces.Count()} ({SolidValidator.Validate(bs[0]).Summary()}), tool faces {other.Faces.Count()} ({SolidValidator.Validate(other).Summary()})");
        };
        BooleanOp.EvaluateOutcomeProbe = m => Console.WriteLine($"  [{step}] outcome: {m[..Math.Min(300, m.Length)]}");
        BooleanOp.GeneralInvalidProbe = r =>
        {
            foreach (var f in r.Faces)
            {
                var surf = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
                string mo = "-";
                if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx && fsx.Facets.Count > 0)
                {
                    double agree = 0, tot = 0;
                    foreach (var (a0, b0, c0) in fsx.Facets) { var nn = Vector3d.Cross(b0 - a0, c0 - a0); double ar = nn.Length; if (ar < 1e-14) continue; tot += ar; if (Vector3d.Dot(nn, f.NormalAt((a0 + b0 + c0) / 3)) > 0) agree += ar; }
                    mo = $"facets {fsx.Facets.Count} agree {agree / Math.Max(tot, 1e-30):0.###} area {tot / 2:0.####}";
                    if (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(surf!) is Parametric3D.Kernel.Geometry.Ssi.Sphere sp)
                    {
                        var (a1, b1, c1) = fsx.Facets[fsx.Facets.Count / 2]; var m1 = (a1 + b1 + c1) / 3;
                        var lo = fsx.Facets.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).MinBy(q => q.Z);
                        var hi = fsx.Facets.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).MaxBy(q => q.Z);
                        foreach (var (a2, b2, c2) in fsx.Facets.Where(t => Vector3d.Dot(Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1), f.NormalAt((t.Item1 + t.Item2 + t.Item3) / 3)) <= 0).Take(12))
                            Console.WriteLine($"      bad facet {a2.X:0.####},{a2.Y:0.####},{a2.Z:0.####} | {b2.X:0.####},{b2.Y:0.####},{b2.Z:0.####} | {c2.X:0.####},{c2.Y:0.####},{c2.Z:0.####} area {Vector3d.Cross(b2 - a2, c2 - a2).Length / 2:E2}");
                        var um = Parametric3D.Kernel.Tess.UvMesher.Mesh(f, Parametric3D.Core.Tol.Chord, out var uwhy);
                        Console.WriteLine($"      uvmesher: {(um == null ? "null: " + uwhy : $"{um.Value.T.Count / 3} tris")}");
                        var em = Parametric3D.Kernel.Tess.ExactFaceMesher.Mesh(f, out var ewhy);
                        Console.WriteLine($"      exactmesher: {(em == null ? "null: " + ewhy : $"{em.Value.T.Count / 3} tris")}");
                        mo += $" centre {sp.C.X:0.###},{sp.C.Y:0.###},{sp.C.Z:0.###} R {sp.R:0.####} normal·(C−p) {Vector3d.Dot(f.NormalAt(m1), sp.C - m1):0.###} zrange {lo.Z:0.###}..{hi.Z:0.###}";
                    }
                }
                Console.WriteLine($"    face {f.Tag} {surf?.GetType().Name} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(surf!)?.GetType().Name} rev {f.Reversed} loops {f.Loops.Count} mesh {mo}");
                foreach (var l in f.Loops)
                    foreach (var ce in l.CoEdges)
                    {
                        int uses = ce.Edge.CoEdges.Count;
                        Console.WriteLine($"       ce {(ce.Reversed ? "-" : "+")}{ce.Edge.Tag} {ce.Edge.Curve?.GetType().Name} {ce.Edge.V0.P.X:0.###},{ce.Edge.V0.P.Y:0.###},{ce.Edge.V0.P.Z:0.###} -> {ce.Edge.V1.P.X:0.###},{ce.Edge.V1.P.Y:0.###},{ce.Edge.V1.P.Z:0.###} uses {uses}{(uses != 2 ? " <<<" : "")}");
                    }
            }
        };
        if (Environment.GetEnvironmentVariable("P3D_VCUV") == "1") Parametric3D.Kernel.Tess.UvMesher.Debug = m => Console.WriteLine("UV " + (m.StartsWith("poledomain") ? "poledomain" : m));
        try { VertexOp.Round(b, new Vector3d(0, 0, 10.5), 4, w); Console.WriteLine("RESULT " + SolidValidator.Validate(b[0]).Summary()); }
        catch (Exception ex) { Console.WriteLine("RESULT error: " + ex.Message[..Math.Min(400, ex.Message.Length)]); }
        finally { BooleanOp.AnalyticRouteDebug = null; BooleanOp.EvaluateProbe = null; BooleanOp.EvaluateOutcomeProbe = null; BooleanOp.GeneralInvalidProbe = null; Parametric3D.Kernel.Tess.UvMesher.Debug = null; }
    }
}

static partial class Program
{
    /// <summary>KRN-03 проверка: операция, сливающая все оболочки тела в первую (нарушение инварианта).</summary>
    internal sealed class MergeShells : Parametric3D.Model.Features.Feature
    {
        public override void Execute(Parametric3D.Model.Features.BuildContext c)
        {
            var b = TopologyClone.Deep(c.Bodies[0]);
            var outer = b.Shells[0];
            foreach (var sh in b.Shells.Skip(1).ToList()) { foreach (var f in sh.Faces.ToList()) outer.Add(f); b.Shells.Remove(sh); }
            c.Bodies[0] = b;
        }
    }

    /// <summary>KRN-03 проверка: операция, кладущая два раздельных бруска в одну оболочку одного тела.</summary>
    internal sealed class TwoPiecesOneShell(Vector3d c0) : Parametric3D.Model.Features.Feature
    {
        public override void Execute(Parametric3D.Model.Features.BuildContext c)
        {
            var a = Box(c0.X + 40, c0.Y, c0.Z, 10, 10, 10); var d = Box(c0.X + 60, c0.Y, c0.Z, 10, 10, 10);
            var b = new TSolid(); var sh = b.AddShell();
            foreach (var f in a.Faces.Concat(d.Faces).ToList()) sh.Add(f);
            c.Bodies.Add(b);
        }
    }
}

static partial class Program
{
    /// <summary>
    /// KRN-03: допустимое расхождение объёма сетки и точного объёма — площадь поверхности × хорда (сетка в пределах хорды от
    /// поверхности отклоняет объём не больше). Прежний порог 2e-3 объёма не был связан с хордой: сетка в конформной карте
    /// сферы (честно в пределах хорды) у тела площадью ~2000 мм² отклоняет объём на 3.3e-3.
    /// </summary>
    internal static double ChordVolumeBound(TSolid b) =>
        b.Faces.Sum(f => AnalyticVolume.FaceArea(f) is { } a && double.IsFinite(a) ? Math.Abs(a)
            : f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? fs.Facets.Sum(t => Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length / 2) : 0)
        * Parametric3D.Core.Tol.Chord;
}

static partial class Program
{
    /// <summary>KRN-03 замер (03.8): семейство торических граней — полный тор, частичный оборот, пояс, четверть пояса; перенос,
    /// поворот, масштаб. По каждой: проверка B-rep (с p-кривыми), отклонение p-кривых, разметчик, площадь, объём против Паппа.</summary>
    internal static List<(string Name, TSolid Body, double Volume)> TorusFamily()
    {
        var res = new List<(string, TSolid, double)>();
        foreach (var (tag, o, U, V, k) in new[] {
            ("axis", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.0),
            ("moved+rotated", new Vector3d(130, -70, 40), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1.0),
            ("small", new Vector3d(3, 1, 2), new Vector3d(0.6, -0.48, 0.64), new Vector3d(0.8, 0.36, -0.48), 0.01),
            ("large", new Vector3d(-2000, 500, 300), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 100.0) })
        {
            var u = U.Normalized(); var v = (V - u * Vector3d.Dot(V, u)).Normalized(); var w = Vector3d.Cross(u, v);
            double R = 20 * k, r = 4 * k;
            var axis = new Parametric3D.Kernel.Frame3(o, u, v);                 // ось вращения — w
            var plane = new Parametric3D.Kernel.Frame3(o + u * R, u, w);         // плоскость профиля (u, w)
            var circle = new Parametric3D.Kernel.Curves.ArcCurve3(plane, r, 0, 2 * Math.PI);
            double tubeArea = Math.PI * r * r, full = 2 * Math.PI * R * tubeArea;
            foreach (var deg in new[] { 360.0, 90.0, 180.0, 270.0 })
            {
                var prof = ProfileBuilder.Build(plane, new List<Parametric3D.Kernel.Curves.ICurve3> { circle });
                res.Add(($"{tag} circle {deg}°", RevolveOp.Build(prof, axis, 0, deg * Math.PI / 180), full * deg / 360));
            }
            // D-профиль: внешняя половина окружности + хорда (пояс тора между двумя окружностями и цилиндр)
            var pTop = o + u * R + w * r; var pBot = o + u * R - w * r;
            var arc = new Parametric3D.Kernel.Curves.ArcCurve3(plane, r, -Math.PI / 2, Math.PI / 2);
            var chord = new Parametric3D.Kernel.Curves.LineCurve3(pTop, pBot);
            double dArea = Math.PI * r * r / 2, dCentroid = R + 4 * r / (3 * Math.PI);
            foreach (var deg in new[] { 360.0, 120.0 })
            {
                var prof = ProfileBuilder.Build(plane, new List<Parametric3D.Kernel.Curves.ICurve3> { arc, chord });
                res.Add(($"{tag} D {deg}°", RevolveOp.Build(prof, axis, 0, deg * Math.PI / 180), 2 * Math.PI * dCentroid * dArea * deg / 360));
            }
        }
        return res;
    }

    static void Krn03MeasureTorus()
    {
        foreach (var (name, body, vol) in TorusFamily())
        {
            if (body.TopologyConvention < TopologyConventions.Version) TopologyConventions.Normalize(body);
            var rep = SolidValidator.Validate(body);
            double av = AnalyticVolume.Of(body) ?? double.NaN, mv = rep.Metrics.GetValueOrDefault("mesh.volume", double.NaN);
            var parts = new List<string>();
            var pcs = PCurves.Measure(body);
            foreach (var f in body.Faces)
            {
                if (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is not Parametric3D.Kernel.Geometry.Ssi.Torus) continue;
                double dev = pcs.Where(x => x.Ce.Loop?.Face == f).Select(x => x.PC == null ? double.PositiveInfinity : x.Dev).DefaultIfEmpty(0).Max();
                var m = Parametric3D.Kernel.Tess.UvMesher.Mesh(f, Parametric3D.Core.Tol.Chord, out var why);
                double ma = m is { } mm ? Enumerable.Range(0, mm.T.Count / 3).Sum(t => Vector3d.Cross(mm.P[mm.T[3 * t + 1]] - mm.P[mm.T[3 * t]], mm.P[mm.T[3 * t + 2]] - mm.P[mm.T[3 * t]]).Length / 2) : double.NaN;
                double ea = AnalyticVolume.FaceArea(f) ?? double.NaN;
                parts.Add($"[loops {f.Loops.Count} ce {f.Loops.Sum(l => l.CoEdges.Count)} pc {dev:E1} uv {(m == null ? "null: " + why : "ok")} area {ea:G8}/{ma:G8}]");
            }
            if (Environment.GetEnvironmentVariable("P3D_TORTOPO") == "1" && name.StartsWith("axis"))
                foreach (var f in body.Faces)
                {
                    Console.WriteLine($"   face {f.Tag} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name} rev {f.Reversed}");
                    foreach (var l in f.Loops)
                    {
                        Console.WriteLine($"     loop outer {l == f.Loops[0]}");
                        foreach (var ce in l.CoEdges)
                            Console.WriteLine($"       ce {(ce.Reversed ? "-" : "+")}{ce.Edge.Id} {ce.Edge.Curve?.GetType().Name} {ce.Edge.V0.P:0.##} -> {ce.Edge.V1.P:0.##} closed {ReferenceEquals(ce.Edge.V0, ce.Edge.V1)} uses {ce.Edge.CoEdges.Count}");
                    }
                }
            if (Environment.GetEnvironmentVariable("P3D_TORBASE") == "1")
                foreach (var f in body.Faces)
                    if (GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.RevolvedSurface rvs && rvs.Base is Parametric3D.Kernel.Curves.ArcCurve3 ba)
                        Console.WriteLine($"   base arc T {ba.T0:G6}..{ba.T1:G6} R {ba.Radius} centre {ba.Frame.Origin} U {ba.Frame.U} V {ba.Frame.V} W {ba.Frame.W} | axis {rvs.Axis.Origin} W {rvs.Axis.W} A {rvs.A0}..{rvs.A1}");
            Console.WriteLine($"KRN03T {name}: {rep.Summary()} vol {av:G10} formula {vol:G10} rel {(av - vol) / vol:E1} mesh {mv:G8} {string.Join(" ", parts)}");
        }
    }
}

static partial class Program
{
    static void SeamChecks()
    {
        Check("KRN03-SEAM-NOT-A-CUT", () =>
        {
            // 03.3 / m.t2: ШОВ — ДВА ИСПОЛЬЗОВАНИЯ РЕБРА, НЕ ФИЗИЧЕСКИЙ РАЗРЕЗ. Тело вращения (вал-цилиндр, шар, конус, тор)
            // поворачивается вокруг своей оси на угол α — шов (меридиан) переезжает; один и тот же резец (поперечное отверстие,
            // паз, шар) в одном месте пространства. Результат не зависит от α: объём (до 1e-9), число граней и рёбер, площадь
            // (до 1e-9), годность; шов не делит грань, сквозь которую проходит разрез (граней столько же, как при шве вдали),
            // сетка показа замкнута (без трещины по шву).
            var fails = new List<string>();
            var cases = new (string Name, Func<TSolid> Body, Vector3d O, Vector3d W, Func<TSolid> Tool, BooleanOp.BooleanKind Kind)[]
            {
                ("shaft × cross hole", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60), Vector3d.Zero, Vector3d.UnitZ,
                    () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 1, 10), 3, 40), TransformOp.Rotate(new Vector3d(0, 1, 30), Vector3d.UnitY, 90)), BooleanOp.BooleanKind.Subtract),
                ("shaft × slot", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60), Vector3d.Zero, Vector3d.UnitZ, () => Box(6, -3, 20, 10, 6, 20), BooleanOp.BooleanKind.Subtract),
                ("sphere × hole", () => PrimitiveOp.Sphere(Vector3d.Zero, 10), Vector3d.Zero, Vector3d.UnitZ,
                    () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 2, -20), 3, 40), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)), BooleanOp.BooleanKind.Subtract),
                ("cone × ball", () => PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 20), Vector3d.Zero, Vector3d.UnitZ, () => PrimitiveOp.Sphere(new Vector3d(7.5, 0, 8), 3), BooleanOp.BooleanKind.Subtract),
                ("torus × box", () => PrimitiveOp.Torus(Vector3d.Zero, 20, 5), Vector3d.Zero, Vector3d.UnitZ, () => Box(17, -4, -10, 15, 8, 20), BooleanOp.BooleanKind.Subtract),
                ("shaft ∩ slab", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60), Vector3d.Zero, Vector3d.UnitZ, () => Box(-20, -4, 10, 40, 8, 40), BooleanOp.BooleanKind.Intersect),
            };
            foreach (var (name, body, o, w, tool, kind) in cases)
            {
                (double V, double A, int F, int E)? first = null;
                foreach (double deg in new[] { 0.0, 0.7, 90, 180, 211 })
                {
                    try
                    {
                        var b = deg == 0 ? body() : TransformOp.Copy(body(), TransformOp.Rotate(o, w, deg));
                        var r = BooleanOp.Evaluate(new List<TSolid> { b }, tool(), kind, OperationPolicy.Strict);
                        if (!r.Accepted || r.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name} α={deg}: {r.Outcome} {r.Guarantee}"); continue; }
                        var bad = r.Bodies.Select(x => SolidValidator.Validate(x)).FirstOrDefault(x => !x.IsValid);
                        if (bad != null) { fails.Add($"{name} α={deg}: {bad.Summary()}"); continue; }
                        double V = r.Bodies.Sum(OpVolume), A = r.Bodies.Sum(x => AnalyticVolume.AreaOf(x) ?? double.NaN);
                        int F = r.Bodies.Sum(x => x.Faces.Count()), E = r.Bodies.Sum(x => x.Edges.Count());
                        // сетка показа замкнута (без трещины по шву) — проверка Mesh в Validate (выше)
                        if (first is not { } f0) { first = (V, A, F, E); Console.WriteLine($"  {name}: V {V:R} A {A:R} faces {F} edges {E}"); continue; }
                        if (!(Math.Abs(V - f0.V) <= 1e-9 * Math.Max(1, f0.V))) fails.Add($"{name} α={deg}: V {V:R} vs {f0.V:R}");
                        if (!(Math.Abs(A - f0.A) <= 1e-9 * Math.Max(1, f0.A))) fails.Add($"{name} α={deg}: area {A:R} vs {f0.A:R}");
                        if (F != f0.F || E != f0.E) fails.Add($"{name} α={deg}: faces/edges {F}/{E} vs {f0.F}/{f0.E}");
                    }
                    catch (Exception ex) { fails.Add($"{name} α={deg}: {ex.GetType().Name} {ex.Message}"); }
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
        });
    }
}
