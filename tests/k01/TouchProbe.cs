using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn05TouchChecks()
    {
        Check("KRN05-SAME-DOMAIN-MERGE-PROVEN", () =>
        {
            // 05.7: грани на одной плоскости сливаются только при доказанной эквивалентности области (точная площадь
            // слитой = сумме) и с происхождением Merged; испорченное слияние (потерян контур отверстия) — отвергается,
            // тело остаётся точным и годным
            var fails = new List<string>();
            TSolid A()
            {
                var r = BooleanOp.Evaluate(new List<TSolid> { Box(0, 0, 0, 20, 10, 10) }, Rotated(PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 2, 20), Vector3d.Zero, Vector3d.UnitX, 20, new Vector3d(10, 5, 5)), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>());
                return r.Bodies[0];
            }
            double want = 4000 - Math.PI * 4 * 10 / Math.Cos(20 * Math.PI / 180);   // наклонный цилиндр: сечение плоскостью z — эллипс площади πr²/cos20° (Кавальери)
            OperationResult U(bool corrupt)
            {
                Parametric3D.Kernel.Analytic.GeneralBoolean.CorruptMergeForTest = corrupt;
                try { return BooleanOp.Evaluate(new List<TSolid> { A() }, Box(20, 0, 0, 20, 10, 10), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict, new List<string>()); }
                finally { Parametric3D.Kernel.Analytic.GeneralBoolean.CorruptMergeForTest = false; }
            }
            var r0 = U(false);
            if (!(r0.Accepted && r0.Bodies.Count == 1 && r0.Guarantee == GuaranteeLevel.ExactAnalytic)) fails.Add($"clean: {r0.Outcome} {r0.Guarantee}");
            else
            {
                var b = r0.Bodies[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                int planes = b.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is { IsPlanar: true });
                if (Math.Abs(v - want) > 1e-9 * want || planes != 6 || !SolidValidator.Validate(b).IsValid) fails.Add($"clean: V={v:R}, planar faces {planes} (want 6)");
                if (b.Provenance == null || b.Provenance.Merged.Count == 0) fails.Add("clean: no Merged provenance");
            }
            int refused0 = Parametric3D.Kernel.Analytic.GeneralBoolean.MergesRefused;
            var r1 = U(true);
            if (!(r1.Accepted && r1.Bodies.Count == 1 && r1.Guarantee == GuaranteeLevel.ExactAnalytic)) fails.Add($"corrupted merge: {r1.Outcome} {r1.Guarantee}");
            else
            {
                var b = r1.Bodies[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                if (Math.Abs(v - want) > 1e-9 * want || !SolidValidator.Validate(b).IsValid) fails.Add($"corrupted merge accepted: V={v:R} valid {SolidValidator.Validate(b).IsValid}");
                if (Parametric3D.Kernel.Analytic.GeneralBoolean.MergesRefused <= refused0) fails.Add("corrupted merge: the proof did not refuse it");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN05-PARTITION-CHECKED", () =>
        {
            // 05.5: разбиение каждой входной грани проверяется — площади областей (точно) = площадь грани. Без внесённой
            // потери: точный маршрут; с потерей области: маршрут отказывает с названной причиной (partition check), а
            // результат — верный объём другим маршрутом или явный отказ, но не принятая неверная форма
            var fails = new List<string>();
            TSolid A() => Box(0, 0, 0, 40, 30, 20);
            TSolid T() => Rotated(PrimitiveOp.Cylinder(new Vector3d(0, 0, -30), 6, 60), Vector3d.Zero, new Vector3d(1, 1, 0).Normalized(), 35, new Vector3d(20, 15, 10));
            var w0 = new List<string>();
            var r0 = BooleanOp.Evaluate(new List<TSolid> { A() }, T(), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w0);
            double v0 = r0.Bodies.Count == 1 ? (AnalyticVolume.Of(r0.Bodies[0]) ?? double.NaN) : double.NaN;
            if (!r0.Accepted || r0.Guarantee != GuaranteeLevel.ExactAnalytic || w0.Any(x => x.Contains("partition check"))) fails.Add($"clean: {r0.Outcome} {r0.Guarantee} {string.Join(" | ", w0.Where(x => x.Contains("partition")))}");
            var w1 = new List<string>();
            Parametric3D.Kernel.Analytic.GeneralBoolean.DropRegionForTest = true;
            OperationResult r1;
            try { r1 = BooleanOp.Evaluate(new List<TSolid> { A() }, T(), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w1); }
            finally { Parametric3D.Kernel.Analytic.GeneralBoolean.DropRegionForTest = false; }
            bool named = w1.Any(x => x.Contains("partition check")) || r1.Diagnostics.Any(d => d.Message.Contains("partition check"));
            double v1 = r1.Bodies.Count == 1 ? (AnalyticVolume.Of(r1.Bodies[0]) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(r1.Bodies[0]))) : double.NaN;
            bool wrongAccepted = r1.Accepted && r1.Guarantee == GuaranteeLevel.ExactAnalytic;
            if (!named || wrongAccepted || (r1.Accepted && !(Math.Abs(v1 - v0) <= 2e-3 * v0)))
                fails.Add($"with a lost region: named {named}, {r1.Outcome} {r1.Guarantee} V={v1:G8} vs {v0:G8}; {string.Join(" | ", w1.Take(3))}");
            {   // потеря области цилиндрической грани (стенки резца): мера dh∧dθ
                var w2 = new List<string>();
                Parametric3D.Kernel.Analytic.GeneralBoolean.DropCurvedRegionForTest = true;
                OperationResult r2;
                try { r2 = BooleanOp.Evaluate(new List<TSolid> { A() }, T(), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w2); }
                finally { Parametric3D.Kernel.Analytic.GeneralBoolean.DropCurvedRegionForTest = false; }
                bool named2 = w2.Any(x => x.Contains("partition check"));
                if (!named2 || (r2.Accepted && r2.Guarantee == GuaranteeLevel.ExactAnalytic)) fails.Add($"lost cylinder region: named {named2}, {r2.Outcome} {r2.Guarantee}");
            }
            {   // потеря области грани с контуром из кривой пересечения (тор − вертикальный цилиндр через трубку): мера по участкам
                // между изломами параметризации (P3D_PARTBREAKS; прежде такие грани разбиение не проверяли)
                var c = new Vector3d(1, 2, 3);
                OperationResult Run(bool drop, List<string> w)
                {
                    var r = BooleanOp.Evaluate(new List<TSolid> { PrimitiveOp.Torus(c, 10, 3) }, PrimitiveOp.Cylinder(c + new Vector3d(10, 0, -10), 1.5, 20), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>());
                    Parametric3D.Kernel.Analytic.GeneralBoolean.DropTracedRegionForTest = drop;
                    try
                    {
                        // вторая операция режет грани с контуром-кривой пересечения
                        return BooleanOp.Evaluate(r.Bodies.ToList(), Box(c.X + 9.6, c.Y - 20, c.Z - 10, 0.8, 40, 20), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w);
                    }
                    finally { Parametric3D.Kernel.Analytic.GeneralBoolean.DropTracedRegionForTest = false; }
                }
                var w3 = new List<string>(); var r3 = Run(false, w3);
                if (!r3.Accepted || r3.Guarantee != GuaranteeLevel.ExactAnalytic || w3.Any(x => x.Contains("partition check"))) fails.Add($"traced clean: {r3.Outcome} {r3.Guarantee} {string.Join(" | ", w3.Where(x => x.Contains("partition")))}");
                var w4 = new List<string>(); var r4 = Run(true, w4);
                bool named4 = w4.Any(x => x.Contains("partition check")) || r4.Diagnostics.Any(d => d.Message.Contains("partition check"));
                Log(new { test = "KRN05-PARTITION", traced = true, named = named4, outcome = r4.Outcome.ToString(), guarantee = r4.Guarantee.ToString() });
                if (!named4 || (r4.Accepted && r4.Guarantee == GuaranteeLevel.ExactAnalytic)) fails.Add($"lost region bounded by an intersection curve: named {named4}, {r4.Outcome} {r4.Guarantee}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN05-TOUCH-POLICY-REGULARIZED", () =>
        {
            // 05.4: касание без общего объёма материал не меняет — пересечение Empty, вычитание — A, объединение — отдельные
            // тела (точные, если точны входы); касание гранью — слияние; внутреннее касание — обычная булева
            var fails = new List<string>();
            double Vol(TSolid x) => AnalyticVolume.Of(x) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(x));
            void Case(string name, TSolid a, TSolid b, double[] unite, bool exactUnite, double? inter, double sub)
            {
                foreach (var kind in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
                {
                    var r = BooleanOp.Evaluate(new List<TSolid> { a }, b, kind, OperationPolicy.Strict, new List<string>());
                    var vols = r.Bodies.Select(Vol).OrderBy(x => x).ToArray();
                    double[] want = kind switch { BooleanOp.BooleanKind.Unite => unite.OrderBy(x => x).ToArray(), BooleanOp.BooleanKind.Intersect => inter is { } iv ? new[] { iv } : Array.Empty<double>(), _ => new[] { sub } };
                    bool ok = r.Accepted && vols.Length == want.Length && vols.Zip(want).All(p => Math.Abs(p.First - p.Second) <= 1e-6 * p.Second)
                              && r.Bodies.All(x => SolidValidator.Validate(x).IsValid)
                              && (kind != BooleanOp.BooleanKind.Unite || !exactUnite || r.Guarantee == GuaranteeLevel.ExactAnalytic)
                              && (kind != BooleanOp.BooleanKind.Intersect || inter != null || r.Outcome == OperationOutcome.Empty);
                    if (!ok) fails.Add($"{name} {kind}: {r.Outcome} {r.Guarantee} [{string.Join(",", vols.Select(v => v.ToString("G8")))}] want [{string.Join(",", want)}] {r.FailureCode}");
                }
            }
            Case("face contact", Box(0, 0, 0, 10, 10, 10), Box(10, 0, 0, 10, 10, 10), new[] { 2000.0 }, true, null, 1000);
            Case("edge contact", Box(0, 0, 0, 10, 10, 10), Box(10, 10, 0, 10, 10, 10), new[] { 1000.0, 1000 }, true, null, 1000);
            Case("vertex contact", Box(0, 0, 0, 10, 10, 10), Box(10, 10, 10, 10, 10, 10), new[] { 1000.0, 1000 }, true, null, 1000);
            Case("partial edge contact", Box(0, 0, 0, 10, 10, 10), Box(10, 10, 5, 10, 10, 10), new[] { 1000.0, 1000 }, true, null, 1000);
            Case("cylinder lying on a box", Box(-10, -10, -10, 20, 20, 10), Rotated(PrimitiveOp.Cylinder(new Vector3d(0, 0, -8), 5, 16), Vector3d.Zero, Vector3d.UnitX, 90, new Vector3d(0, 8, 5)),
                 new[] { 4000.0, Math.PI * 25 * 16 }, true, null, 4000);
            Case("sphere on a box", Box(-10, -10, -10, 20, 20, 10), PrimitiveOp.Sphere(new Vector3d(0, 0, 5), 5), new[] { 4000.0, 4.0 / 3 * Math.PI * 125 }, false, null, 4000);
            Case("cylinder inside, tangent to a face", Box(-10, -10, -10, 20, 20, 20), PrimitiveOp.Cylinder(new Vector3d(5, 0, -5), 5, 10), new[] { 8000.0 }, true, Math.PI * 250, 8000 - Math.PI * 250);
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void TouchProbe()
    {
        void Run(string name, TSolid a, TSolid b)
        {
            foreach (var kind in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
            {
                try
                {
                    var w = new List<string>();
                    var r = BooleanOp.Evaluate(new List<TSolid> { a }, b, kind, OperationPolicy.Strict, w);
                    string vols = string.Join(",", r.Bodies.Select(x => (AnalyticVolume.Of(x) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(x))).ToString("G8")));
                    string valid = string.Join(",", r.Bodies.Select(x => SolidValidator.Validate(x).IsValid ? "ok" : "INVALID:" + SolidValidator.Validate(x).Summary()));
                    Console.WriteLine($"{name} {kind}: {r.Outcome} {r.Guarantee} bodies {r.Bodies.Count} V=[{vols}] valid=[{valid}] code={r.FailureCode} {string.Join(" | ", w.Where(x => x.Contains("touch") || x.Contains("contact") || x.Contains("manifold")).Take(2))}");
                }
                catch (Exception e) { Console.WriteLine($"{name} {kind}: EXCEPTION {e.GetType().Name} {e.Message[..Math.Min(150, e.Message.Length)]}"); }
            }
        }
        Run("boxes sharing a face", Box(0, 0, 0, 10, 10, 10), Box(10, 0, 0, 10, 10, 10));
        Run("boxes sharing an edge (line contact)", Box(0, 0, 0, 10, 10, 10), Box(10, 10, 0, 10, 10, 10));
        Run("boxes sharing a vertex (point contact)", Box(0, 0, 0, 10, 10, 10), Box(10, 10, 10, 10, 10, 10));
        Run("boxes partial edge contact", Box(0, 0, 0, 10, 10, 10), Box(10, 10, 5, 10, 10, 10));
        Run("sphere on a box (point contact)", Box(-10, -10, -10, 20, 20, 10), PrimitiveOp.Sphere(new Vector3d(0, 0, 5), 5));
        Run("cylinder lying on a box (line contact)", Box(-10, -10, -10, 20, 20, 10), Rotated(PrimitiveOp.Cylinder(new Vector3d(0, 0, -8), 5, 16), new Vector3d(0, 0, 0), Vector3d.UnitX, 90, new Vector3d(0, 8, 5)));
        Run("cylinder inside a box tangent to a face", Box(-10, -10, -10, 20, 20, 20), PrimitiveOp.Cylinder(new Vector3d(5, 0, -5), 5, 10));
    }

    static TSolid Rotated(TSolid s, Vector3d at, Vector3d axis, double deg, Vector3d move)
    {
        var r = TransformOp.Copy(s, TransformOp.Rotate(at, axis, deg));
        return TransformOp.Copy(r, TransformOp.Move(move));
    }
}
