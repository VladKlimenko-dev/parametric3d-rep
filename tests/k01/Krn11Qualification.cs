using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Analytic;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// KRN-11 (11.2): КВАЛИФИКАЦИОННЫЙ НАБОР булевой операции. Не «пример — ответ», а тождества, которые обязаны выполняться
    /// на любых входах: включения-исключения V(A∪B)+V(A∩B)=V(A)+V(B), разности V(A−B)=V(A)−V(A∩B), перестановочность ∪ и ∩,
    /// неизменность объёма при жёстком движении и зеркале. Входы — случайные примитивы с поворотами (зерно фиксировано).
    /// Каждый результат: принят честно (тело валидно, гарантия названа) или отказ с кодом и пустыми телами; приближённый
    /// результат обязан нести границу погрешности, и тождество проверяется в пределах этой границы.
    /// Плюс вырождения (совпадающие грани, касание по ребру и в вершине, касающиеся и соосные цилиндры).
    /// </summary>
    static void Krn11Qualification()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("P3D_QUALN"), out var qn) ? qn : 60;
        int seed = int.TryParse(Environment.GetEnvironmentVariable("P3D_QUALSEED"), out var qs) ? qs : 1102;
        bool verbose = Environment.GetEnvironmentVariable("P3D_QUALV") == "1";
        int only = int.TryParse(Environment.GetEnvironmentVariable("P3D_QUALONLY"), out var qo) ? qo : -1;
        if (only >= 0) { verbose = true; BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("      route: " + m); }
        var rnd = new Random(seed);
        double U(double a, double b) => a + (b - a) * rnd.NextDouble();

        (TSolid S, string D) Prim()
        {
            int k = rnd.Next(5);
            var at = new Vector3d(U(-8, 8), U(-8, 8), U(-8, 8));
            TSolid s; string d;
            switch (k)
            {
                case 0: { double w = U(8, 30), dd = U(8, 30), h = U(8, 30); s = Box(at.X - w / 2, at.Y - dd / 2, at.Z - h / 2, w, dd, h); d = $"box {w:0.##}x{dd:0.##}x{h:0.##}"; break; }
                case 1: { double r = U(2, 10), h = U(8, 30); s = PrimitiveOp.Cylinder(at - new Vector3d(0, 0, h / 2), r, h); d = $"cyl r{r:0.##} h{h:0.##}"; break; }
                case 2: { double r0 = U(3, 10), r1 = U(0, 8), h = U(8, 25); if (r1 < 0.5) r1 = 0; s = PrimitiveOp.Cone(at - new Vector3d(0, 0, h / 2), r0, r1, h); d = $"cone r{r0:0.##}/{r1:0.##} h{h:0.##}"; break; }
                case 3: { double r = U(3, 12); s = PrimitiveOp.Sphere(at, r); d = $"sphere r{r:0.##}"; break; }
                default: { double tube = U(1.5, 4), ring = tube + U(2, 9); s = PrimitiveOp.Torus(at, ring, tube); d = $"torus {ring:0.##}/{tube:0.##}"; break; }
            }
            d += $" at {at.X:0.#},{at.Y:0.#},{at.Z:0.#}";
            if (rnd.Next(2) == 0)
            {
                var axis = new Vector3d(U(-1, 1), U(-1, 1), U(-1, 1)); if (axis.Length < 0.1) axis = Vector3d.UnitX;
                double deg = U(0, 180);
                s = TransformOp.Copy(s, TransformOp.Rotate(at, axis.Normalized(), deg)); d += $" rot{deg:0}";
            }
            return (s, d);
        }

        // Объём результата и его граница: точный — по аналитике (граница ~0), приближённый — граница из AccuracyReport/предупреждения.
        (double V, double Bound, string G, string? Why) Run(TSolid a, TSolid b, BooleanOp.BooleanKind k)
        {
            var w = new List<string>();
            var r = BooleanOp.Evaluate(new[] { a }, b, k, OperationPolicy.AllowApproximate, w);
            if (!r.Accepted)
            {
                if (r.Bodies.Count != 0) throw new Exception($"refusal with bodies: {r.FailureCode}");
                if (r.FailureCode == null) throw new Exception("refusal without a code");
                return (double.NaN, double.NaN, "refused", r.FailureCode);
            }
            double v = 0;
            foreach (var body in r.Bodies)
            {
                var val = SolidValidator.Validate(body);
                if (!val.IsValid) throw new Exception($"{k}: invalid result: {val.Summary()}");
                v += OpVolume(body);
            }
            // Граница объёма многогранного результата: отклонение фасет от поверхностей (расстояние, AccuracyReport) × площадь.
            double bound = 0;
            if (r.Bodies.Count > 0 && r.Guarantee is not (GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric))
            {
                double dev = Math.Max(r.Accuracy?.Measured ?? 0, r.Accuracy?.Budget ?? double.PositiveInfinity);
                bound = dev * r.Bodies.Sum(body => Parametric3D.Kernel.Tess.MassProps.Area(Parametric3D.Kernel.Tess.Tessellator.Tessellate(body)));
                if (verbose) Console.WriteLine($"    {k} {r.Guarantee} acc {r.Accuracy} bound {bound:G4} diag {string.Join(" | ", r.Diagnostics.Select(d => d.ToString()))} warn {string.Join(" | ", w)}");
            }
            return (v, bound, r.Guarantee.ToString(), null);
        }

        // ---- метаморфные тождества ----
        int exact = 0, bounded = 0, refused = 0;
        var failures = new List<string>();
        for (int i = 0; i < n; i++)
        {
            rnd = new Random(seed * 7919 + i);                 // своё зерно у пары: P3D_QUALONLY повторяет её точно
            if (only >= 0 && i != only) continue;
            var (a, da) = Prim(); var (b, db) = Prim();
            double va = OpVolume(a), vb = OpVolume(b);
            string tag = $"#{i} [{da}] · [{db}]";
            if (only >= 0) Console.WriteLine($"  {tag}: V(A) {va:R} V(B) {vb:R}");
            try
            {
                var u = Run(a, b, BooleanOp.BooleanKind.Unite);
                var x = Run(a, b, BooleanOp.BooleanKind.Intersect);
                var s = Run(a, b, BooleanOp.BooleanKind.Subtract);
                var u2 = Run(b, a, BooleanOp.BooleanKind.Unite);
                var x2 = Run(b, a, BooleanOp.BooleanKind.Intersect);
                var all = new[] { u, x, s, u2, x2 };
                if (all.Any(t => t.G == "refused")) { refused++; if (verbose) Console.WriteLine($"  refused {tag}: {string.Join(",", all.Select(t => t.Why))}"); continue; }
                double scale = Math.Max(va, vb), tolE = 1e-7 * scale;
                double B(params (double V, double Bound, string G, string? Why)[] t) => tolE + t.Sum(q => q.Bound);
                if (all.All(t => t.Bound == 0)) exact++; else bounded++;
                void Id(string name, double lhs, double rhs, double tol)
                {
                    if (!(Math.Abs(lhs - rhs) <= tol))
                        failures.Add($"{tag}: {name} {lhs:R} vs {rhs:R} (tol {tol:G3}; {string.Join(" ", all.Select(t => t.G))})");
                }
                Id("V(A∪B)+V(A∩B)=V(A)+V(B)", u.V + x.V, va + vb, B(u, x));
                Id("V(A−B)=V(A)−V(A∩B)", s.V, va - x.V, B(s, x));
                Id("A∪B=B∪A", u.V, u2.V, B(u, u2));
                Id("A∩B=B∩A", x.V, x2.V, B(x, x2));
                // жёсткое движение обоих операндов не меняет объём пересечения
                if (i % 3 == 0)
                {
                    var axis = new Vector3d(U(-1, 1), U(-1, 1), U(-1, 1)); if (axis.Length < 0.1) axis = Vector3d.UnitY;
                    var m = TransformOp.Rotate(new Vector3d(U(-5, 5), U(-5, 5), U(-5, 5)), axis.Normalized(), U(10, 170));
                    var t = TransformOp.Move(new Vector3d(U(-20, 20), U(-20, 20), U(-20, 20)));
                    var am = TransformOp.Copy(TransformOp.Copy(a, m), t); var bm = TransformOp.Copy(TransformOp.Copy(b, m), t);
                    var xm = Run(am, bm, BooleanOp.BooleanKind.Intersect);
                    if (xm.G != "refused") Id("V(T(A)∩T(B))=V(A∩B)", xm.V, x.V, B(xm, x));
                    var mi = TransformOp.Mirror(Vector3d.Zero, new Vector3d(1, 0.3, -0.2).Normalized());
                    var sm = Run(TransformOp.Copy(a, mi), TransformOp.Copy(b, mi), BooleanOp.BooleanKind.Subtract);
                    if (sm.G != "refused") Id("V(M(A)−M(B))=V(A−B)", sm.V, s.V, B(sm, s));
                }
            }
            catch (Exception e) { failures.Add($"{tag}: {e.Message}"); }
        }
        Log(new { test = "KRN11-QUAL-METAMORPHIC", n, seed, exact, bounded, refused, failures = failures.Count });
        foreach (var f in failures.Take(verbose ? 1000 : 20)) Console.WriteLine("  " + f);
        Check("KRN11-QUAL-METAMORPHIC", () =>
        {
            Require(failures.Count == 0, $"{failures.Count} identity failures; first: {failures.FirstOrDefault()}");
            Require(exact + bounded >= n * 9 / 10, $"too many refusals: {refused} of {n}");
        });

        // ---- вырождения: совпадающие грани, касание по грани/ребру/вершине, касающиеся и соосные цилиндры, тело с собой ----
        // Ожидание: результат принят и объём верен (точно — до 1e-7 относительно, многогранный — в своей границе), либо честный
        // отказ с кодом без тел. Неверный объём, невалидное тело или исключение — провал.
        void Degenerate(string id, Func<TSolid> fa, Func<TSolid> fb, double vUnite, double vInter, double vSub)
        {
            Check(id, () =>
            {
                var a = fa(); var b = fb();
                foreach (var (k, want) in new[] { (BooleanOp.BooleanKind.Unite, vUnite), (BooleanOp.BooleanKind.Intersect, vInter), (BooleanOp.BooleanKind.Subtract, vSub) })
                {
                    var r = Run(a, b, k);
                    Log(new { test = id, kind = k.ToString(), r.G, r.V, want, r.Bound, r.Why });
                    if (r.G == "refused") continue;
                    Require(Math.Abs(r.V - want) <= 1e-7 * Math.Max(1, Math.Max(vUnite, 1)) + r.Bound, $"{k}: volume {r.V:R} vs {want:R} (bound {r.Bound:G3}, {r.G})");
                }
            });
        }
        double Cyl(double r, double h) => Math.PI * r * r * h;
        Degenerate("KRN11-QUAL-DEGEN-SHARED-FACE", () => Box(0, 0, 0, 10, 10, 10), () => Box(10, 0, 0, 10, 10, 10), 2000, 0, 1000);
        Degenerate("KRN11-QUAL-DEGEN-SHARED-FACE-PARTIAL", () => Box(0, 0, 0, 10, 10, 10), () => Box(10, 3, 4, 10, 4, 3), 1120, 0, 1000);
        Degenerate("KRN11-QUAL-DEGEN-EDGE-TOUCH", () => Box(0, 0, 0, 10, 10, 10), () => Box(10, 10, 0, 10, 10, 10), 2000, 0, 1000);
        Degenerate("KRN11-QUAL-DEGEN-VERTEX-TOUCH", () => Box(0, 0, 0, 10, 10, 10), () => Box(10, 10, 10, 10, 10, 10), 2000, 0, 1000);
        Degenerate("KRN11-QUAL-DEGEN-COPLANAR-OVERLAP", () => Box(0, 0, 0, 10, 10, 10), () => Box(5, 5, 0, 10, 10, 10), 1750, 250, 750);
        Degenerate("KRN11-QUAL-DEGEN-SAME-BODY", () => Box(0, 0, 0, 10, 10, 10), () => Box(0, 0, 0, 10, 10, 10), 1000, 1000, 0);
        Degenerate("KRN11-QUAL-DEGEN-SAME-CYLINDER", () => PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10), () => PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10),
                   Cyl(5, 10), Cyl(5, 10), 0);
        Degenerate("KRN11-QUAL-DEGEN-COAXIAL-EQUAL-R", () => PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10), () => PrimitiveOp.Cylinder(new Vector3d(0, 0, 6), 5, 10),
                   Cyl(5, 16), Cyl(5, 4), Cyl(5, 6));
        Degenerate("KRN11-QUAL-DEGEN-CYL-TANGENT-BOX", () => Box(0, 0, 0, 20, 20, 10), () => PrimitiveOp.Cylinder(new Vector3d(25, 10, 0), 5, 10),
                   4000 + Cyl(5, 10), 0, 4000);
        Degenerate("KRN11-QUAL-DEGEN-CYL-INSCRIBED", () => Box(-5, -5, 0, 10, 10, 10), () => PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10),
                   1000, Cyl(5, 10), 1000 - Cyl(5, 10));
        Degenerate("KRN11-QUAL-DEGEN-CYLS-TANGENT", () => PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10), () => PrimitiveOp.Cylinder(new Vector3d(10, 0, 0), 5, 10),
                   2 * Cyl(5, 10), 0, Cyl(5, 10));
        Degenerate("KRN11-QUAL-DEGEN-SPHERE-ON-FACE", () => Box(-10, -10, -10, 20, 20, 10), () => PrimitiveOp.Sphere(new Vector3d(0, 0, 5), 5),
                   4000 + 4.0 / 3 * Math.PI * 125, 0, 4000);
        Degenerate("KRN11-QUAL-DEGEN-CONCENTRIC-SPHERES", () => PrimitiveOp.Sphere(Vector3d.Zero, 8), () => PrimitiveOp.Sphere(Vector3d.Zero, 5),
                   4.0 / 3 * Math.PI * 512, 4.0 / 3 * Math.PI * 125, 4.0 / 3 * Math.PI * (512 - 125));
        Degenerate("KRN11-QUAL-DEGEN-CYL-CAP-ON-FACE", () => Box(-10, -10, 0, 20, 20, 10), () => PrimitiveOp.Cylinder(new Vector3d(0, 0, 10), 4, 10),
                   4000 + Cyl(4, 10), 0, 4000);
        Degenerate("KRN11-QUAL-DEGEN-HOLE-FLUSH", () => Box(-10, -10, 0, 20, 20, 10), () => PrimitiveOp.Cylinder(Vector3d.Zero, 4, 10),
                   4000, Cyl(4, 10), 4000 - Cyl(4, 10));

        // ---- негативные входы операций детали: отказ с исходом, без «внутреннего сбоя», тело — как до операции ----
        void Negative(string id, Func<Part, Feature> add, params OperationOutcome[] allowed)
        {
            Check(id, () =>
            {
                var p = new Part("neg"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
                double v0 = p.Volume;
                var f = add(p); p.AddFeature(f);
                var r = p.Compute(null);
                p.Apply(r);
                r.Outcomes.TryGetValue(f, out var o);
                Log(new { test = id, outcome = o.ToString(), err = f.Error, vol = p.Volume, v0 });
                Require(f.Error != null, "accepted a bad input");
                Require(System.Linq.Enumerable.Contains(allowed, o), $"outcome {o}, expected {string.Join("/", allowed)}");
                Require(Math.Abs(p.Volume - v0) <= 1e-9 * v0, $"body changed: {p.Volume} vs {v0}");
                Require(p.Bodies.Count == 1 && SolidValidator.Validate(p.Bodies[0]).IsValid, "body not kept valid");
            });
        }
        var bad = new[] { OperationOutcome.InvalidInput, OperationOutcome.Unsupported, OperationOutcome.Empty };
        Negative("KRN11-QUAL-NEG-ZERO-DEPTH", p => { var c = new ExtrudeFeature("C") { SketchId = RectSketch(p, 30, -10, -10, 20, 20).Id, Cut = true }; c.Side1.Distance = new ValueRef(0); c.Side1.Flip = true; return c; }, bad);
        Negative("KRN11-QUAL-NEG-ROUND-TOO-BIG", p =>
        {
            var e = p.Bodies[0].Edges.Single(x => ((x.V0.P + x.V1.P) / 2 - new Vector3d(-30, 0, 30)).Length < 1e-6);
            var rf = new RoundFeature("R") { Radius = new ValueRef(70) }; rf.Edges.Add(EdgeRef.Of(e)); return rf;
        }, bad);
        Negative("KRN11-QUAL-NEG-HOLE-MISSES", p =>
        {
            var h = new HoleFeature("H") { Direction = new Vector3d(0, 0, -1), ThroughAll = true, Diameter = new ValueRef(8) };
            h.Points.Add(new Vector3d(100, 0, 30)); return h;
        }, bad);
        Negative("KRN11-QUAL-NEG-SHELL-TOO-THICK", p => new ShellFeature("Sh") { Thickness = new ValueRef(35) }, bad);
        Negative("KRN11-QUAL-NEG-EMPTY-SKETCH", p =>
        {
            var s = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY)); p.Add(s);
            var c = new ExtrudeFeature("C") { SketchId = s.Id }; c.Side1.Distance = new ValueRef(10); return c;
        }, bad);
        Negative("KRN11-QUAL-NEG-BOWTIE-SKETCH", p =>
        {
            var s = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY)); p.Add(s);
            var a = new SkPoint(-10, -10); var b = new SkPoint(10, 10); var c2 = new SkPoint(10, -10); var d = new SkPoint(-10, 10);
            s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkLine(b, c2)); s.AddEntity(new SkLine(c2, d)); s.AddEntity(new SkLine(d, a));
            var c = new ExtrudeFeature("C") { SketchId = s.Id }; c.Side1.Distance = new ValueRef(10); return c;
        }, bad);
    }
}
