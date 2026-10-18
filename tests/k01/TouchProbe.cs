using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
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
            {   // 05.5: целые замкнутые грани — шар, пересекающий верх бруска, и тор, пересекающий его грань: мера целой известна
                // (шар 4πR, тор 0 по двум мерам); область у полюса — с поправкой по Стоксу. Мутация P3D_PARTPOLE=0.
                foreach (var (name, tool) in new (string, Func<TSolid>)[] { ("sphere", () => PrimitiveOp.Sphere(new Vector3d(20, 15, 18), 6)), ("torus", () => PrimitiveOp.Torus(new Vector3d(20, 15, 19), 9, 2.5)) })
                {
                    var wc = new List<string>();
                    var rc = BooleanOp.Evaluate(new List<TSolid> { A() }, tool(), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict, wc);
                    if (!rc.Accepted || rc.Guarantee != GuaranteeLevel.ExactAnalytic || wc.Any(x => x.Contains("partition check"))) fails.Add($"{name} clean: {rc.Outcome} {rc.Guarantee} {string.Join(" | ", wc.Where(x => x.Contains("partition")))}");
                    var wd = new List<string>();
                    Parametric3D.Kernel.Analytic.GeneralBoolean.DropClosedRegionForTest = true;
                    OperationResult rd;
                    try { rd = BooleanOp.Evaluate(new List<TSolid> { A() }, tool(), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict, wd); }
                    finally { Parametric3D.Kernel.Analytic.GeneralBoolean.DropClosedRegionForTest = false; }
                    bool namedC = wd.Any(x => x.Contains("partition check")) || rd.Diagnostics.Any(d => d.Message.Contains("partition check"));
                    Log(new { test = "KRN05-PARTITION", closed = name, named = namedC, outcome = rd.Outcome.ToString(), guarantee = rd.Guarantee.ToString() });
                    if (!namedC || (rd.Accepted && rd.Guarantee == GuaranteeLevel.ExactAnalytic)) fails.Add($"lost region of a closed {name} face: named {namedC}, {rd.Outcome} {rd.Guarantee}");
                }
                // область конуса до вершины (вершина над бруском, вниз — тоже): мера с началом развёртки в вершине
                foreach (var (name, tool) in new (string, Func<TSolid>)[] { ("cone apex up", () => PrimitiveOp.Cone(new Vector3d(20, 15, -5), 6, 0, 30)), ("cone apex down", () => Rotated(PrimitiveOp.Cone(new Vector3d(0, 0, -15), 6, 0, 30), Vector3d.Zero, Vector3d.UnitX, 180, new Vector3d(20, 15, 10))) })
                {
                    var wc = new List<string>();
                    var rc = BooleanOp.Evaluate(new List<TSolid> { A() }, tool(), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict, wc);
                    if (!rc.Accepted || rc.Guarantee != GuaranteeLevel.ExactAnalytic || wc.Any(x => x.Contains("partition check"))) fails.Add($"{name} clean: {rc.Outcome} {rc.Guarantee} {string.Join(" | ", wc.Where(x => x.Contains("partition")))}");
                    var wd = new List<string>();
                    Parametric3D.Kernel.Analytic.GeneralBoolean.DropCurvedRegionForTest = true;
                    OperationResult rd;
                    try { rd = BooleanOp.Evaluate(new List<TSolid> { A() }, tool(), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict, wd); }
                    finally { Parametric3D.Kernel.Analytic.GeneralBoolean.DropCurvedRegionForTest = false; }
                    bool namedC = wd.Any(x => x.Contains("partition check")) || rd.Diagnostics.Any(d => d.Message.Contains("partition check"));
                    Log(new { test = "KRN05-PARTITION", cone = name, named = namedC, outcome = rd.Outcome.ToString(), guarantee = rd.Guarantee.ToString() });
                    if (!namedC || (rd.Accepted && rd.Guarantee == GuaranteeLevel.ExactAnalytic)) fails.Add($"lost region of a {name} face: named {namedC}, {rd.Outcome} {rd.Guarantee}");
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN05-SHARED-BY-CONSTRUCTION", () =>
        {
            // 05.6: общие границы — общие сущности ПО ПОСТРОЕНИЮ: совпадающие куски (отрезки вдоль общей грани двух брусков,
            // половинки эллипса стыка «на ус» протяжки по ломаной) отождествляются до сборки граней — ребро создаётся один
            // раз; сшивка после сборки ничего не делает. Мутация P3D_SHAREBUILD=0 (работу делает поздняя сшивка).
            var fails = new List<string>();
            long B() => Parametric3D.Kernel.Analytic.GeneralBoolean.BuildSplits + Parametric3D.Kernel.Analytic.GeneralBoolean.BuildMerges;
            long L() => Parametric3D.Kernel.Analytic.GeneralBoolean.SewSplits + Parametric3D.Kernel.Analytic.GeneralBoolean.SewMerges;
            long b0 = B(), l0 = L();
            var u = new List<TSolid> { Box(0, 0, 0, 20, 10, 10) };
            BooleanOp.Unite(u, Box(20, 0, 0, 10, 10, 10), new List<string>());
            if (u.Count != 1 || u[0].Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(u[0]).IsValid) fails.Add($"boxes: {u.Count} body(ies) {u.FirstOrDefault()?.Guarantee}");
            var p = new Parametric3D.Model.Part("sweepL"); var sec = CircleSketch(p, 0, 0, 0, 3);
            var path = new Parametric3D.Model.Sketching.Sketch("path", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ)); p.Add(path);
            path.AddEntity(new Parametric3D.Model.Sketching.SkLine(new Parametric3D.Model.Sketching.SkPoint(0, 0), new Parametric3D.Model.Sketching.SkPoint(0, 40)));
            path.AddEntity(new Parametric3D.Model.Sketching.SkLine(new Parametric3D.Model.Sketching.SkPoint(0, 40), new Parametric3D.Model.Sketching.SkPoint(30, 40)));
            p.AddFeature(new Parametric3D.Model.Features.SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
            // стык «на ус» — цилиндры равного радиуса: точный распад на половины эллипсов (06.12) совпадающих кусков не даёт;
            // механизм отождествления проверяется на кусках общего способа (петли «−»/«+» через сёдла)
            Parametric3D.Kernel.Geometry.Ssi.EqualCylOffForTest = true;
            try { p.Rebuild(null); } finally { Parametric3D.Kernel.Geometry.Ssi.EqualCylOffForTest = false; }
            if (p.Features.Any(f => f.Error != null) || p.Bodies.Count != 1) fails.Add("sweep L failed");
            long built = B() - b0, late = L() - l0;
            Log(new { test = "KRN05-SHARED", built, late });
            if (built == 0) fails.Add("nothing identified at construction");
            if (late != 0) fails.Add($"late sewing split/merged {late} edge(s)");
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
            Case("sphere on a box", Box(-10, -10, -10, 20, 20, 10), PrimitiveOp.Sphere(new Vector3d(0, 0, 5), 5), new[] { 4000.0, 4.0 / 3 * Math.PI * 125 }, true, null, 4000);
            Case("cylinder inside, tangent to a face", Box(-10, -10, -10, 20, 20, 20), PrimitiveOp.Cylinder(new Vector3d(5, 0, -5), 5, 10), new[] { 8000.0 }, true, Math.PI * 250, 8000 - Math.PI * 250);
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void PointTangencyChecks()
    {
        Check("KRN05-POINT-TANGENCY-EXACT", () =>
        {
            // m.b3: КАСАНИЕ В ТОЧКЕ — ТОЧНО. Шар на грани бруска снаружи и изнутри (в том числе повёрнуто), шар о шар снаружи,
            // шар в шаре изнутри: точный маршрут (ExactAnalytic), тела годны, тождества ∪ + ∩ = A + B, − + ∩ = A до 1e-9
            // (объёмы — замкнутыми формулами); касание снаружи: ∩ — пусто, ∪ — два тела. Почти касание (зазор и наложение
            // 1e-6, 1e-3) — годно и с теми же тождествами. Полость, касающаяся грани, — следующая булева (отверстие) годна.
            // Касание по окружности (тор на плоскости и в бруске, тор вокруг цилиндра, шар в цилиндре того же радиуса) — так же.
            // Мутации P3D_POINTTOUCH=0, P3D_CIRCLETOUCH=0 — касание снова многогранно (ловится).
            var fails = new List<string>();
            double Ball(double r) => 4.0 / 3 * Math.PI * r * r * r;
            double V(TSolid x) => AnalyticVolume.Of(x) ?? double.NaN;
            var rot = (Vector3d.Zero, new Vector3d(1, 2, 3), 33.0, new Vector3d(7, -3, 2));
            TSolid R(TSolid x) => Rotated(x, rot.Item1, rot.Item2, rot.Item3, rot.Item4);
            var cases = new List<(string Name, Func<TSolid> A, Func<TSolid> B, double VA, double VB, double? Inter, bool MustExact)>
            {
                ("sphere on a box", () => Box(-10, -10, -10, 20, 20, 10), () => PrimitiveOp.Sphere(new Vector3d(1, 2, 5), 5), 4000, Ball(5), 0, true),
                ("sphere on a box, rotated", () => R(Box(-10, -10, -10, 20, 20, 10)), () => R(PrimitiveOp.Sphere(new Vector3d(1, 2, 5), 5)), 4000, Ball(5), 0, true),
                ("sphere under a box", () => Box(-10, -10, 0, 20, 20, 10), () => PrimitiveOp.Sphere(new Vector3d(-2, 3, -4), 4), 4000, Ball(4), 0, true),
                ("sphere inside a box, touching the top", () => Box(-10, -10, -10, 20, 20, 20), () => PrimitiveOp.Sphere(new Vector3d(1, -2, 5), 5), 8000, Ball(5), Ball(5), true),
                ("sphere inside a box, touching the top, rotated", () => R(Box(-10, -10, -10, 20, 20, 20)), () => R(PrimitiveOp.Sphere(new Vector3d(1, -2, 5), 5)), 8000, Ball(5), Ball(5), true),
                ("sphere on a sphere", () => PrimitiveOp.Sphere(Vector3d.Zero, 6), () => PrimitiveOp.Sphere(new Vector3d(6, 8, 0) * 0.9, 3), Ball(6), Ball(3), 0, true),
                ("sphere inside a sphere, touching", () => PrimitiveOp.Sphere(Vector3d.Zero, 6), () => PrimitiveOp.Sphere(new Vector3d(0.6, 0.8, 0) * 3, 3), Ball(6), Ball(3), Ball(3), true),
            };
            // m.b3: касание ПО ОКРУЖНОСТИ (кратный корень профилей соосных тел)
            double Tor(double R0, double r0) => 2 * Math.PI * Math.PI * R0 * r0 * r0;
            cases.Add(("torus lying on a box (circle)", () => Box(-20, -20, -10, 40, 40, 10), () => PrimitiveOp.Torus(new Vector3d(1, 2, 3), 8, 3), 16000, Tor(8, 3), 0, true));
            cases.Add(("torus inside a box touching the bottom (circle)", () => Box(-20, -20, -10, 40, 40, 20), () => PrimitiveOp.Torus(new Vector3d(1, 2, -7), 8, 3), 32000, Tor(8, 3), Tor(8, 3), true));
            cases.Add(("torus inside a box touching the bottom, rotated", () => R(Box(-20, -20, -10, 40, 40, 20)), () => R(PrimitiveOp.Torus(new Vector3d(1, 2, -7), 8, 3)), 32000, Tor(8, 3), Tor(8, 3), true));
            cases.Add(("torus hugging a cylinder (inner circle)", () => PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 5, 20), () => PrimitiveOp.Torus(Vector3d.Zero, 8, 3), Math.PI * 25 * 20, Tor(8, 3), 0, true));
            cases.Add(("sphere in a cylinder of the same radius (circle)", () => PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 5, 20), () => PrimitiveOp.Sphere(new Vector3d(0, 0, 1), 5), Math.PI * 25 * 20, Ball(5), Ball(5), true));
            // почти касание: зазор и наложение (без формулы ∩ у наложения — проверяются тождества)
            foreach (double d in new[] { 1e-6, -1e-6, 1e-3, -1e-3 })
                cases.Add(($"sphere on a box, offset {d:G2}", () => Box(-10, -10, -10, 20, 20, 10), () => PrimitiveOp.Sphere(new Vector3d(1, 2, 5 + d), 5), 4000, Ball(5), d > 0 ? 0 : null, false));
            foreach (var (name, A, B, va, vb, inter, must) in cases)
            {
                var v = new Dictionary<BooleanOp.BooleanKind, double>(); var err = new Dictionary<BooleanOp.BooleanKind, double>();
                foreach (var kind in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
                {
                    var r = BooleanOp.Evaluate(new List<TSolid> { A() }, B(), kind, OperationPolicy.Strict, new List<string>());
                    if (!r.Accepted) { fails.Add($"{name} {kind}: {r.Outcome} {r.FailureCode}"); continue; }
                    if (must && r.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name} {kind}: {r.Guarantee}");
                    if (!must) Console.WriteLine($"    {name} {kind}: {r.Guarantee} bodies {r.Bodies.Count}");
                    var bad = r.Bodies.Select(b => SolidValidator.Validate(b)).FirstOrDefault(x => !x.IsValid);
                    if (bad != null) { fails.Add($"{name} {kind}: {bad.Summary()}"); continue; }
                    // многогранный результат — объём с его границей (P3D_POLYBOUND): тождества проверяются в пределах сумм границ
                    v[kind] = r.Bodies.Sum(b => r.Guarantee == GuaranteeLevel.ExactAnalytic ? V(b) : AnalyticMeasure.Volume(b).Value);
                    err[kind] = r.Guarantee == GuaranteeLevel.ExactAnalytic ? 0 : r.Bodies.Sum(b => AnalyticMeasure.Volume(b).Error);
                    if (kind == BooleanOp.BooleanKind.Intersect && inter == 0 && r.Outcome != OperationOutcome.Empty) fails.Add($"{name} ∩: {r.Outcome}, want empty");
                    if (kind == BooleanOp.BooleanKind.Unite && must && inter == 0 && r.Bodies.Count != 2) fails.Add($"{name} ∪: {r.Bodies.Count} bodies, want 2");
                }
                if (v.Count < 3) continue;
                double tol = (must ? 1e-9 : 1e-6) * (va + vb) + err.Values.Sum();
                double vi = v[BooleanOp.BooleanKind.Intersect];
                if (inter is { } iw && !(Math.Abs(vi - iw) <= tol)) fails.Add($"{name}: ∩ {vi:R} vs {iw:R}");
                if (!(Math.Abs(v[BooleanOp.BooleanKind.Unite] + vi - va - vb) <= tol)) fails.Add($"{name}: ∪ + ∩ {v[BooleanOp.BooleanKind.Unite] + vi:R} vs {va + vb:R}");
                if (!(Math.Abs(v[BooleanOp.BooleanKind.Subtract] + vi - va) <= tol)) fails.Add($"{name}: − + ∩ {v[BooleanOp.BooleanKind.Subtract] + vi:R} vs {va:R}");
                Console.WriteLine($"  {name}: ∪ {v[BooleanOp.BooleanKind.Unite]:R} ∩ {vi:R} − {v[BooleanOp.BooleanKind.Subtract]:R}");
            }
            // полость, касающаяся грани, — дальше отверстие в стороне: тело годно, объём = 8000 − шар − цилиндр
            {
                var r = BooleanOp.Evaluate(new List<TSolid> { Box(-10, -10, -10, 20, 20, 20) }, PrimitiveOp.Sphere(new Vector3d(1, -2, 5), 5), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                var r2 = r.Accepted ? BooleanOp.Evaluate(r.Bodies.ToList(), PrimitiveOp.Cylinder(new Vector3d(-6, 6, -12), 2, 24), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict) : r;
                double want = 8000 - Ball(5) - Math.PI * 4 * 20;
                if (!r2.Accepted || r2.Guarantee != GuaranteeLevel.ExactAnalytic || r2.Bodies.Count != 1 || !SolidValidator.Validate(r2.Bodies[0]).IsValid || !(Math.Abs(V(r2.Bodies[0]) - want) <= 1e-9 * 8000))
                    fails.Add($"cavity touching the top, then a hole: {r2.Outcome} {r2.Guarantee} {r2.Bodies.Count} V {(r2.Bodies.Count > 0 ? V(r2.Bodies[0]) : double.NaN):R} vs {want:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
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
        Run("sphere inside a box tangent to the top", Box(-10, -10, -10, 20, 20, 20), PrimitiveOp.Sphere(new Vector3d(0, 0, 5), 5));
        Run("sphere under a box (point contact, rotated)", Rotated(Box(-10, -10, -10, 20, 20, 10), Vector3d.Zero, new Vector3d(1, 2, 3), 33, Vector3d.Zero), Rotated(PrimitiveOp.Sphere(new Vector3d(0, 0, 5), 5), Vector3d.Zero, new Vector3d(1, 2, 3), 33, Vector3d.Zero));
        Run("cylinder inside a cylinder, tangent along a line", PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 10, 20), PrimitiveOp.Cylinder(new Vector3d(6, 0, -5), 4, 10));
        Run("cylinder beside a cylinder, line contact", PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 10, 20), PrimitiveOp.Cylinder(new Vector3d(14, 0, -5), 4, 10));
        Run("torus lying on a box (circle contact)", Box(-20, -20, -10, 40, 40, 10), PrimitiveOp.Torus(new Vector3d(0, 0, 3), 8, 3));
        Run("torus inside a box touching the bottom", Box(-20, -20, -10, 40, 40, 20), PrimitiveOp.Torus(new Vector3d(0, 0, -7), 8, 3));
        Run("sphere in a cylinder, same radius (circle contact)", PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 5, 20), PrimitiveOp.Sphere(new Vector3d(0, 0, 1), 5));
        Run("cone on its side on a box (line contact)", Box(-20, -20, -10, 40, 40, 10), Rotated(PrimitiveOp.Cone(Vector3d.Zero, 4, 0, 10), Vector3d.Zero, Vector3d.UnitY, 90 - Math.Atan(0.4) * 180 / Math.PI, new Vector3d(0, 0, 0)));
        Run("sphere touching a box edge", Box(-10, -10, -10, 20, 20, 10), PrimitiveOp.Sphere(new Vector3d(10 + 3, 0, 3), Math.Sqrt(18)));
        Run("sphere touching a box vertex", Box(-10, -10, -10, 20, 20, 10), PrimitiveOp.Sphere(new Vector3d(13, 13, 3), Math.Sqrt(27)));
        Run("sphere cut by a plane through its centre", Box(-10, -10, -10, 20, 20, 10), PrimitiveOp.Sphere(new Vector3d(0, 0, 0), 5));
    }

    static TSolid Rotated(TSolid s, Vector3d at, Vector3d axis, double deg, Vector3d move)
    {
        var r = TransformOp.Copy(s, TransformOp.Rotate(at, axis, deg));
        return TransformOp.Copy(r, TransformOp.Move(move));
    }
}

static partial class Program
{
    internal static TSolid Lemon(double cx)
    {
        var axis = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        var pl = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
        double R = Math.Sqrt(cx * cx + 16), a0 = Math.Atan2(4, -cx);
        var arc = new ArcCurve3(new Frame3(new Vector3d(cx, 0, 0), Vector3d.UnitX, Vector3d.UnitZ), R, a0, -a0);
        var seg = new LineCurve3(new Vector3d(0, 0, -4), new Vector3d(0, 0, 4));
        return RevolveOp.Build(ProfileBuilder.Build(pl, new ICurve3[] { arc, seg }), axis, 0, 2 * Math.PI);
    }

    static void LemonDebug()
    {
        foreach (double cx in new[] { -2.0, 1.0 })
        {
            var b = Lemon(cx);
            Console.WriteLine($"cx {cx}: faces {b.Faces.Count()} edges {b.Edges.Count()} vertices {b.Vertices.Count()} conv {b.TopologyConvention}");
            foreach (var f in b.Faces)
            {
                Console.WriteLine($"  face {f.Id} {f.Surface.GetType().Name} rev {f.Reversed} loops {f.Loops.Count} [{string.Join(",", f.Loops.Select(l => l.CoEdges.Count))}] shape {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(f.Surface)}");
                foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) Console.WriteLine($"    coedge {ce.Edge.Curve.GetType().Name} {ce.Edge.Curve.PointAt(ce.Edge.Curve.T0):0.###} → {ce.Edge.Curve.PointAt(ce.Edge.Curve.T1):0.###} rev {ce.Reversed}");
            }
            Console.WriteLine($"  volume {AnalyticVolume.Of(b)}  withErr {AnalyticVolume.OfWithError(b)}");
            var prevD = BooleanOp.AnalyticRouteDebug; BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("    route: " + m);
            var r = BooleanOp.Evaluate(new List<TSolid> { b }, Box(0, -10, -10, 10, 20, 20), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>());
            BooleanOp.AnalyticRouteDebug = prevD;
            Console.WriteLine($"  half: {r.Guarantee} {string.Join(" | ", r.Diagnostics.Select(d => d.Message).Take(3))}");
        }
    }

    static void TorusPoleChecks()
    {
        Check("KRN03-TORUS-POLE-LEMON-APPLE", () =>
        {
            // m.t3: ПОЛЮС ТОРА — законное вырождение. Вращение области «отрезок оси + дуга с центром вне оси» даёт грань тора
            // (веретенообразного) с вершинами-полюсами на оси: «лимон» (центр дуги по ту сторону оси — малая дуга) и «яблоко»
            // (центр по эту сторону — большая дуга, полюса во впадинах). Тело точное и годное (вершина в полюсе — не микроребро),
            // объём V = π∮x² dz (Гаусс по дуге), разрез пополам плоскостью через ось и сквозное отверстие ⟂ оси — точно, с
            // тождеством − + ∩ = V; точный STEP пишется.
            var fails = new List<string>();
            var axis = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
            var pl = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
            foreach (var (name, cx) in new[] { ("lemon", -2.0), ("apple", 1.0) })
            {
                try
                {
                    double R = Math.Sqrt(cx * cx + 16);
                    // дуга в плоскости XZ (локально u = x, v = z) от (0, 4) до (0, −4) через x > 0
                    double a0 = Math.Atan2(4, -cx), a1 = -a0;            // углы концов от центра (cx, 0)
                    if (a1 < 0) a1 += 2 * Math.PI;
                    // от a0 по часовой (через угол 0 — точка cx + R > 0)
                    var arc = new ArcCurve3(new Frame3(new Vector3d(cx, 0, 0), Vector3d.UnitX, Vector3d.UnitZ), R, a0, a1 - 2 * Math.PI);
                    var seg = new LineCurve3(new Vector3d(0, 0, -4), new Vector3d(0, 0, 4));
                    var prof = ProfileBuilder.Build(pl, new ICurve3[] { arc, seg });
                    var b = RevolveOp.Build(prof, axis, 0, 2 * Math.PI);
                    // V = π ∮ x² dz по дуге (отрезок на оси вклада не даёт), знак — по обходу
                    double V = 0; int n = 4000;
                    for (int i = 0; i < n; i++)
                    {
                        double t0 = arc.T0 + (arc.T1 - arc.T0) * i / n, t1 = arc.T0 + (arc.T1 - arc.T0) * (i + 1) / n;
                        double[] gx = { -0.7745966692414834, 0, 0.7745966692414834 }, gw = { 5.0 / 9, 8.0 / 9, 5.0 / 9 };
                        for (int q = 0; q < 3; q++) { double t = (t0 + t1) / 2 + (t1 - t0) / 2 * gx[q], h = 1e-7; var p = arc.PointAt(t); double dz = (arc.PointAt(t + h).Z - arc.PointAt(t - h).Z) / (2 * h); V += gw[q] * (t1 - t0) / 2 * Math.PI * p.X * p.X * dz; }
                    }
                    V = Math.Abs(V);
                    double v = AnalyticVolume.Of(b) ?? double.NaN;
                    var vr = SolidValidator.Validate(b);
                    Console.WriteLine($"  {name}: V {v:R} want {V:R} ({(v - V) / V:E1}) {b.Guarantee}, faces {b.Faces.Count()}, valid {vr.IsValid}");
                    if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !vr.IsValid) fails.Add($"{name}: {b.Guarantee} {vr.Summary()}");
                    if (!(Math.Abs(v - V) <= 1e-8 * V)) fails.Add($"{name}: V {v:R} vs {V:R}");
                    var sw = new StringWriter(); if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, name, new List<string>())) fails.Add($"{name}: no exact STEP");
                    foreach (var (tn, tool) in new (string, Func<TSolid>)[] { ("half", () => Box(0, -10, -10, 10, 20, 20)), ("hole", () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 0.8, 20), TransformOp.Rotate(new Vector3d(0, 0, 1), Vector3d.UnitY, 90))) })
                    {
                        var rs = BooleanOp.Evaluate(new List<TSolid> { b }, tool(), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                        var ri = BooleanOp.Evaluate(new List<TSolid> { b }, tool(), BooleanOp.BooleanKind.Intersect, OperationPolicy.Strict);
                        if (rs.Guarantee != GuaranteeLevel.ExactAnalytic || ri.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name} {tn}: {rs.Guarantee}/{ri.Guarantee}"); continue; }
                        if (rs.Bodies.Concat(ri.Bodies).Any(x => !SolidValidator.Validate(x).IsValid)) { fails.Add($"{name} {tn}: invalid"); continue; }
                        double vs = rs.Bodies.Sum(x => AnalyticVolume.Of(x) ?? double.NaN), vi = ri.Bodies.Sum(x => AnalyticVolume.Of(x) ?? double.NaN);
                        Console.WriteLine($"    {tn}: − {vs:R} ∩ {vi:R}");
                        if (!(Math.Abs(vs + vi - V) <= 1e-8 * V)) fails.Add($"{name} {tn}: − + ∩ {vs + vi:R} vs {V:R}");
                        if (tn == "half" && !(Math.Abs(vs - V / 2) <= 1e-8 * V)) fails.Add($"{name} half: {vs:R} vs {V / 2:R}");
                    }
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
