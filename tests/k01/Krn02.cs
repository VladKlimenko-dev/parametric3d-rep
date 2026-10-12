using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel.Analytic;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>KRN-02, замер: брусок с отверстием и скруглённой выемкой, сдвинутый на d по каждой оси; объём относительно d = 0.</summary>
    static void Krn02Measure()
    {
        double? v0 = null;
        foreach (double d in new[] { 0.0, 1e3, 1e4, 1e5, 4.99e5 })
        {
            var off = new Vector3d(d, -d, d);
            var b = new List<TSolid> { Box(off.X, off.Y, off.Z) };
            var w = new List<string>();
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(off + new Vector3d(30, 20, -2), 8, 24), w);
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(off + new Vector3d(10, 10, 10), 4, 20), w);
            double v = AnalyticVolume.Of(b[0]) ?? double.NaN;
            v0 ??= v;
            Console.WriteLine($"{{\"probe\":\"KRN02-DOMAIN\",\"d\":{d},\"guarantee\":\"{b[0].Guarantee}\",\"vol\":{v:R},\"rel\":{Math.Abs(v - v0.Value) / v0.Value:R}}}");
        }
    }

    static void Krn02MeasureRevolved()
    {
        foreach (var (R, r) in new[] { (20.0, 2.0), (20.0, 8.0), (300.0, 0.5) })
        {
            var t = PrimitiveOp.Torus(new Vector3d(3, -2, 7), R, r); TopologyConventions.Normalize(t);
            double exact = 2 * Math.PI * Math.PI * r * r * R, v = t.Faces.Sum(fc => AnalyticVolume.FaceIntegral(fc) ?? double.NaN) / 3;
            Console.WriteLine($"{{\"probe\":\"KRN02-TORUS\",\"R\":{R},\"r\":{r},\"faces\":{t.Faces.Count()},\"vol\":{v:R},\"rel\":{(v - exact) / exact:R}}}");
        }
        foreach (var (r0, r1, h) in new[] { (10.0, 4.0, 20.0), (10.0, 0.0, 20.0), (5.0, 5.0001, 30.0) })
        {
            var c = PrimitiveOp.Cone(new Vector3d(3, -2, 7), r0, r1, h); TopologyConventions.Normalize(c);
            double exact = Math.PI * h * (r0 * r0 + r0 * r1 + r1 * r1) / 3, v = AnalyticVolume.Of(c) ?? double.NaN;
            Console.WriteLine($"{{\"probe\":\"KRN02-CONE\",\"r0\":{r0},\"r1\":{r1},\"vol\":{v:R},\"rel\":{(v - exact) / exact:R}}}");
        }
    }

    static void Krn02()
    {
        // KRN-02: бюджет допусков — единый, переопределяется контекстом; вне контекста — значения по умолчанию.
        // Аудит точного тела — по модельной точности: ребро с допуском больше предельного — не «аналитика».
        Check("KRN02-AUDIT-BUDGET", () =>
        {
            Require(ToleranceContext.Current.ModelAccuracy == 1e-7 && ToleranceContext.Current.MaxEntityTolerance == 1e-5, "default budget");
            using (new KernelContext(KernelSettings.Capture() with { Tolerance = ToleranceContext.Default with { ModelAccuracy = 1e-9 } }).Enter())
                Require(ToleranceContext.Current.ModelAccuracy == 1e-9, "the context does not override the budget");
            var b = new List<TSolid> { Box() }; var w = new List<string>();
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), w);
            Require(b[0].Guarantee == GuaranteeLevel.ExactAnalytic, $"guarantee {b[0].Guarantee}");
            var a0 = GeometryAudit.Of(b[0]);
            Require(a0.FullyAnalytic, $"exact body not fully analytic: edges {a0.AnalyticEdges}/{a0.Edges.Count}");
            var e = b[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3);
            double t0 = e.Tolerance; e.Tolerance = 1e-4;
            try { var a1 = GeometryAudit.Of(b[0]); Require(!a1.FullyAnalytic, "an edge above MaxEntityTolerance is accepted as analytic"); }
            finally { e.Tolerance = t0; }
        });
        // KRN-02: допуск операции — max(модельная точность, допуск операндов); вне операции — прежние 2e-5 мм.
        Check("KRN02-OPERATION-TOLERANCE", () =>
        {
            var seen = new List<double>();
            BooleanOp.ToleranceDebug = (_, t) => seen.Add(t);
            try
            {
                var b = new List<TSolid> { Box() }; var w = new List<string>();
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), w);
            }
            finally { BooleanOp.ToleranceDebug = null; }
            Require(seen.Count > 0 && seen.All(t => t >= 1e-7 && t < 2e-5), $"operation tolerances {string.Join(",", seen)}");
            Require(Region2Ops.Eps == Region2Ops.LegacyEps, "the operation tolerance leaked out of the operation");
        });
        // KRN-02: касание в сечении — одна вершина (двойной корень не порождает щепку), при допуске операции 1e-7.
        Check("KRN02-SECTION-TANGENCY-VERTEX", () =>
        {
            var prev = Region2Ops.EpsOverrideForTests; Region2Ops.EpsOverrideForTests = 1e-7;
            try
            {
                var circle = Seg2.Arc(new Vector2d(0, 0), 10, 0, 2 * Math.PI);
                var t1 = Region2Ops.Intersections(Seg2.Line(new Vector2d(-20, 10 - 1e-13), new Vector2d(20, 10 - 1e-13)), circle);
                Require(t1.Count == 1 && (t1[0] - new Vector2d(0, 10)).Length < 1e-7, $"line tangent: {t1.Count} point(s)");
                var t2 = Region2Ops.Intersections(Seg2.Line(new Vector2d(-20, 10 - 1e-3), new Vector2d(20, 10 - 1e-3)), circle);
                Require(t2.Count == 2, $"near-tangent secant: {t2.Count} point(s)");
                var t3 = Region2Ops.Intersections(Seg2.Arc(new Vector2d(0, 0), 5, 0, 2 * Math.PI), Seg2.Arc(new Vector2d(10, 0), 5, 0, 2 * Math.PI));
                Require(t3.Count == 1 && (t3[0] - new Vector2d(5, 0)).Length < 1e-7, $"circles tangent: {t3.Count} point(s)");
            }
            finally { Region2Ops.EpsOverrideForTests = prev; }
        });
        // KRN-02: вырожденный контур — по средней ширине (площадь ≤ Eps × периметр), а не по площади в мм².
        Check("KRN02-DEGENERATE-CONTOUR", () =>
        {
            var sliver = Region2Ops.Boolean(Region2.Rect(0, 0, 10, 10), Region2.Rect(-1, -1, 11, 10 - 1e-5), Bool2.Subtract);
            Require(sliver.Loops.Count == 0, $"a 1e-5 mm strip (below the 2e-5 tolerance) survives: area {sliver.Area:G3}");
            var thin = Region2Ops.Boolean(Region2.Rect(0, 0, 10, 10), Region2.Rect(-1, -1, 11, 10 - 1e-3), Bool2.Subtract);
            Require(thin.Loops.Count == 1, "a 1e-3 mm strip (above tolerance) is dropped");
        });
        // KRN-02: аналитический объём не теряет точности на краю рабочей области (±500 м).
        Check("KRN02-ANALYTIC-ACCURACY-ACROSS-DOMAIN", () =>
        {
            double? v0 = null;
            foreach (double d in new[] { 0.0, 1e5, 4.99e5 })
            {
                var off = new Vector3d(d, -d, d);
                var b = new List<TSolid> { Box(off.X, off.Y, off.Z) }; var w = new List<string>();
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(off + new Vector3d(30, 20, -2), 8, 24), w);
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(off + new Vector3d(10, 10, 10), 4, 20), w);
                Require(b[0].Guarantee == GuaranteeLevel.ExactAnalytic, $"d={d}: {b[0].Guarantee}");
                double v = AnalyticVolume.Of(b[0]) ?? double.NaN; v0 ??= v;
                Require(Math.Abs(v - v0.Value) <= 1e-13 * v0.Value, $"d={d}: rel {Math.Abs(v - v0.Value) / v0.Value:G3}");
            }
        });
        // KRN-02 (приёмка): ОДНА ЗАДАЧА В РАЗНЫХ ПОЛОЖЕНИЯХ, ОРИЕНТАЦИЯХ И МАСШТАБАХ — ОДИН ТОПОЛОГИЧЕСКИЙ РЕЗУЛЬТАТ. Деталь
        // (отверстия, карман, бобышка, наклонное отверстие, зенковка вращением) строится точными операциями в рамке.
        // Найдено и исправлено: ось конуса из образующей с концом на оси (поворот), сдвиг на период в развёртке (масштаб ×100),
        // конус с вершиной вверху развёртки (часть ориентаций), объём сетки во float (сдвиг 1e5 мм → вывернутая сетка).
        Check("KRN02-METAMORPHIC", () =>
        {
            var rnd = new Random(7);
            Vector3d RU() { while (true) { var v = new Vector3d(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1); if (v.Length > 0.2 && v.Length < 1) return v.Normalized(); } }
            var cases = new List<(string N, Vector3d O, Vector3d U, Vector3d V, double K)>
            {
                ("identity", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1),
                ("move", new Vector3d(1e3, -2e3, 5e2), Vector3d.UnitX, Vector3d.UnitY, 1),
                ("rotate", new Vector3d(7, -3, 11), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1),
                ("scale0.01", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 0.01),
                ("scale100", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 100),
                ("far-rotate-scale1000", new Vector3d(4e5, -4e5, 3e5), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1000),
            };
            foreach (double k in new[] { 1.0, 0.3, 1, 30 })
            {
                var u = RU(); var v = Vector3d.Cross(u, RU()).Normalized();
                var o = new Vector3d(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * (2e5 * rnd.NextDouble());
                cases.Add(($"rand-k{k}-{cases.Count}", o, u, v, k));
            }
            bool prevHard = Krn02Hard; Krn02Hard = true;
            try
            {
                string? fev0 = null; double v0 = 0;
                foreach (var c in cases)
                {
                    var s = Krn02Scene(c.O, c.U, c.V, c.K, out var g);
                    string fev = $"{s.Faces.Count()}/{s.Edges.Count()}/{s.Vertices.Count()}";
                    double v = (AnalyticVolume.Of(s) ?? double.NaN) / (c.K * c.K * c.K);
                    fev0 ??= fev; if (c.N == "identity") v0 = v;
                    var rep = SolidValidator.Validate(s);
                    Require(g == GuaranteeLevel.ExactAnalytic, $"{c.N}: {g}");
                    Require(fev == fev0, $"{c.N}: topology {fev} vs {fev0}");
                    // бюджет объёма: площадь × модельная точность (8800 мм² × 1e-7 ≈ 1e-3 мм³ на 4.3e4 мм³ — 2e-8)
                    Require(Math.Abs(v - v0) <= 1e-9 * v0, $"{c.N}: volume rel {(v - v0) / v0:G3}");
                    Require(rep.IsValid, $"{c.N}: {rep.Summary()}");
                }
            }
            finally { Krn02Hard = prevHard; }
        });
        // KRN-02: МАЛЕНЬКАЯ ДЕТАЛЬ ДАЛЕКО ОТ НАЧАЛА (3 мм у 3e4…3e5 мм, с поворотом) — B-rep точный, одна топология. Найдено и
        // исправлено: точка внутри области — с наибольшим зазором (абсолютный сдвиг 1 мм попадал на кромку полосы высотой
        // 1 мм), вырождения образующей — по расстоянию, а не по косинусу (диск у 3e4 мм распознавался конусом).
        // Сетка отображения (float) у таких координат — вне проверки: KRN-09.
        Check("KRN02-SMALL-FAR", () => Krn02SmallFar());
        // KRN-02: отчёт о точности результата; превышение бюджета — отказ AccuracyExceeded, вход не меняется.
        Check("KRN02-ACCURACY-REPORT", () =>
        {
            var r = BooleanOp.Evaluate(new List<TSolid> { Box() }, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            Require(r.Outcome == OperationOutcome.Success && r.Accuracy is { Basis: "exact B-rep" } a && a.Measured <= 1e-12 && a.Budget == 1e-5, $"{r.Accuracy}");
            var input = new List<TSolid> { Box() }; string fp0 = Fingerprint(input[0]);
            using (new KernelContext(KernelSettings.Capture() with { Tolerance = ToleranceContext.Default with { MaxEntityTolerance = 1e-16 } }).Enter())
            {
                var r2 = BooleanOp.Evaluate(input, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                Require(r2.Outcome == OperationOutcome.AccuracyExceeded && r2.FailureCode == OperationCodes.AccuracyExceeded, r2.Describe());
            }
            Require(input.Count == 1 && Fingerprint(input[0]) == fp0, "the input changed after a refused operation");
            // многогранный результат — вырез призмой над сплайнами (шар с g.14, тор с 2026-10-11 — точный маршрут)
            var r3 = BooleanOp.Evaluate(new List<TSolid> { Box() }, SplinePrism(new Vector3d(30, 20, 20), 8, 5, 8), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            Require(r3.Accuracy is { Basis: "faceting (measured)", Measured: { } fm } fa && fm > 0 && fm <= fa.Budget, $"polyhedral result: {r3.Accuracy}");   // g.04: отклонение фасет измеряется
        });
        // KRN-02: ось конуса из образующей, конец которой лежит на оси, — по раскрытию, а не по шуму направления (поворот).
        Check("KRN02-CONE-AXIS", () =>
        {
            foreach (var (u, v) in new[] { (Vector3d.UnitX, Vector3d.UnitY), (new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1)), (new Vector3d(-0.3, 0.2, 0.93), new Vector3d(0.9, 0.4, 0.2)) })
            {
                var U = u.Normalized(); var V = (v - U * Vector3d.Dot(v, U)).Normalized(); var W = Vector3d.Cross(U, V);
                var c = new Vector3d(12.3, -4.5, 6.7);
                Vector3d P(double x, double z) => c + U * x + W * z;
                var p = new[] { P(0, 8), P(14, 22), P(0, 22) };
                var prof = ProfileBuilder.Build(new Parametric3D.Kernel.Frame3(c, U, W), Enumerable.Range(0, 3)
                    .Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 3])).ToList());
                var body = RevolveOp.Build(prof, new Parametric3D.Kernel.Frame3(c, U, V), 0, 2 * Math.PI);
                int cones = 0;
                foreach (var fc in body.Faces)
                    if (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(fc)!) is Parametric3D.Kernel.Geometry.Ssi.Cone k)
                    {
                        cones++;
                        Require((k.Apex - P(0, 8)).Length < 1e-9, $"apex {k.Apex} vs {P(0, 8)}");
                        Require(Vector3d.Dot(k.Axis.Normalized(), W) > 1 - 1e-12, $"axis {k.Axis} opens away from the base (frame {U})");
                    }
                Require(cones == 1, $"cones {cones}");
            }
        });
        // KRN-02: объём и ориентация сетки — от опорной точки в double (во float деталь у 1e5 мм получала случайный знак).
        Check("KRN02-MESH-VOLUME-FAR", () =>
        {
            foreach (double d in new[] { 0.0, 1e5, 4.9e5 })
            {
                var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(Box(d, -d, d));
                double v = Parametric3D.Kernel.Tess.MassProps.SignedVolume(m);
                Require(Math.Abs(v - 48000) < 1, $"d={d}: mesh volume {v}");
                var g = Parametric3D.Kernel.Tess.MassProps.Centroid(m);
                Require((g - new Vector3d(d + 30, -d + 20, d + 10)).Length < 0.1, $"d={d}: centroid {g}");
            }
        });
        // KRN-02: тор — замкнутая первообразная по образующей-дуге и целая поверхность (только шов) — 2π·H.
        Check("KRN02-TORUS-VOLUME", () =>
        {
            foreach (var (R, r) in new[] { (20.0, 2.0), (20.0, 8.0), (300.0, 0.5) })
            {
                var t = PrimitiveOp.Torus(new Vector3d(3, -2, 7), R, r);
                double exact = 2 * Math.PI * Math.PI * r * r * R, v = t.Faces.Sum(fc => AnalyticVolume.FaceIntegral(fc) ?? double.NaN) / 3;
                Require(Math.Abs(v - exact) <= 1e-13 * exact, $"torus R{R} r{r}: {v:R} vs {exact:R}");
            }
        });
    }
}

static partial class Program
{
    /// <summary>KRN-02: одна деталь в произвольной рамке (начало, U, V) и масштабе k — точными построениями (не Copy).</summary>
    static TSolid Krn02Scene(Vector3d o, Vector3d U, Vector3d V, double k, out GuaranteeLevel g)
    {
        U = U.Normalized(); V = (V - U * Vector3d.Dot(V, U)).Normalized(); var W = Vector3d.Cross(U, V);
        Parametric3D.Kernel.Frame3 At(double x, double y, double z) => new(o + (U * x + V * y + W * z) * k, U, V);
        TSolid Rect(double x0, double y0, double x1, double y1, double z0, double h)
        {
            var f = At(0, 0, z0); Vector3d P(double x, double y) => f.Origin + (U * x + V * y) * k;
            var p = new[] { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };
            return Parametric3D.Kernel.Ops.ExtrudeOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(f, Enumerable.Range(0, 4)
                .Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 4])).ToList()), W * h * k);
        }
        TSolid Disk(double cx, double cy, double r, double z0, double h)
        {
            var f = At(cx, cy, z0);
            return Parametric3D.Kernel.Ops.ExtrudeOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(f,
                new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(f, r * k, 0, 2 * Math.PI) }), W * h * k);
        }
        TSolid Oblique(double cx, double cy, double r, double deg)
        {
            // ось — W, повёрнутая вокруг U на deg; диск начинается под деталью
            double a = deg * Math.PI / 180; var W2 = W * Math.Cos(a) + V * Math.Sin(a); var V2 = Vector3d.Cross(W2, U);
            var c = o + (U * cx + V * cy) * k - W2 * (15 * k);
            var f = new Parametric3D.Kernel.Frame3(c, U, V2);
            return Parametric3D.Kernel.Ops.ExtrudeOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(f,
                new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(f, r * k, 0, 2 * Math.PI) }), W2 * 60 * k);
        }
        TSolid Countersink(double cx, double cy)
        {
            var c = o + (U * cx + V * cy) * k;
            var plane = new Parametric3D.Kernel.Frame3(c, U, W);
            Vector3d P(double x, double z) => c + (U * x + W * z) * k;
            var p = new[] { P(0, 8), P(14, 22), P(0, 22) };
            var prof = Parametric3D.Kernel.Ops.ProfileBuilder.Build(plane, Enumerable.Range(0, 3)
                .Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 3])).ToList());
            return Parametric3D.Kernel.Ops.RevolveOp.Build(prof, new Parametric3D.Kernel.Frame3(c, U, V), 0, 2 * Math.PI);
        }
        var b = new List<TSolid> { Rect(0, 0, 60, 40, 0, 20) }; var w = new List<string>();
        Krn02Op = "Disk"; BooleanOp.Subtract(b, Disk(30, 20, 8, -2, 24), w);
        Krn02Op = "Disk"; BooleanOp.Subtract(b, Disk(10, 10, 4, 10, 20), w);
        Krn02Op = "Rect"; BooleanOp.Subtract(b, Rect(40, 5, 55, 15, 12, 20), w);
        Krn02Op = "Disk+"; BooleanOp.Unite(b, Disk(50, 30, 5, 20, 6), w);
        if (Krn02Hard)
        {
            Krn02Op = "Oblique"; BooleanOp.Subtract(b, Oblique(15, 30, 3, 25), w);
            var cs = Countersink(30, 20);
            if (Krn02DumpSsi)
                foreach (var fa in b[0].Faces)
                    foreach (var fb in cs.Faces)
                    {
                        var sa = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(fa)!);
                        var sb = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(fb)!);
                        if (sa == null || sb == null) { Console.WriteLine($"    ssi: no shape {fa.Tag}/{fb.Tag}"); continue; }
                        var r = Parametric3D.Kernel.Geometry.Ssi.Intersect(sa, sb, o, 1e3 * k);
                        Vector3d Lc(Vector3d x) => new(Vector3d.Dot(x - o, U) / k, Vector3d.Dot(x - o, V) / k, Vector3d.Dot(x - o, W) / k);
                        Vector3d Ld(Vector3d x) => new(Vector3d.Dot(x, U), Vector3d.Dot(x, V), Vector3d.Dot(x, W));
                        if (sb is Parametric3D.Kernel.Geometry.Ssi.Cone kc && sa is Parametric3D.Kernel.Geometry.Ssi.Plane pc && fa.Tag.EndsWith("top"))
                            Console.WriteLine($"    cone apex {Lc(kc.Apex)} axis {Ld(kc.Axis.Normalized())} half {kc.HalfAngle:R}; plane P {Lc(pc.P)} N {Ld(pc.N.Normalized())}");
                        if (sa is not Parametric3D.Kernel.Geometry.Ssi.Plane || sb is not Parametric3D.Kernel.Geometry.Ssi.Plane)
                            Console.WriteLine($"    ssi {fa.Tag}×{fb.Tag} {sa.GetType().Name}×{sb.GetType().Name}: {r}");
                    }
            Krn02Op = "Countersink"; BooleanOp.Subtract(b, cs, w);
        }
        g = b[0].Guarantee;
        return b[0];
    }

    static bool Krn02Hard, Krn02DumpSsi; static string Krn02Op = "";
    static readonly bool Krn02FarOld = Environment.GetEnvironmentVariable("P3D_KRN02FAROLD") == "1";
    static void Krn02MeasureMetamorphic()
    {
      foreach (bool hard in new[] { false, true })
      {
        Krn02Hard = hard; Krn02DumpSsi = hard && Environment.GetEnvironmentVariable("P3D_DUMPSSI") == "1"; Console.WriteLine($"-- hard {hard}");
        var cases = new (string Name, Vector3d O, Vector3d U, Vector3d V, double K)[]
        {
            ("identity", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1),
            ("move", new Vector3d(1e3, -2e3, 5e2), Vector3d.UnitX, Vector3d.UnitY, 1),
            ("rotate", new Vector3d(7, -3, 11), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1),
            ("rotate-z37", Vector3d.Zero, new Vector3d(Math.Cos(0.6458), Math.Sin(0.6458), 0), new Vector3d(-Math.Sin(0.6458), Math.Cos(0.6458), 0), 1),
            ("scale0.01", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 0.01),
            ("scale100", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 100),
            ("far-rotate-scale1000", new Vector3d(4e5, -4e5, 3e5), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1000),
        };
        var rnd = new Random(20261008);
        Vector3d RandUnit() { while (true) { var v = new Vector3d(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1); if (v.Length > 0.2 && v.Length < 1) return v.Normalized(); } }
        var all = cases.ToList();
        if (Environment.GetEnvironmentVariable("P3D_METARANDOM") == "1")
            foreach (double k in new[] { 1.0, 1, 1, 1, 0.05, 0.3, 3, 30, 300, 1, 1, 1 })
                for (int i = 0; i < 2; i++)
                {
                    var u = RandUnit(); var v = Vector3d.Cross(u, RandUnit()).Normalized();
                    var o = new Vector3d(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * (2e5 * rnd.NextDouble());
                    if (Environment.GetEnvironmentVariable("P3D_METANOOFFSET") == "1") o = Vector3d.Zero;
                    all.Add(($"rand-k{k}-{all.Count}", o, u, v, k));
                }
        double? v0 = null;
        foreach (var c in all)
        {
            string err = ""; TSolid? s = null; GuaranteeLevel g = default;
            if (Krn02DumpSsi) Console.WriteLine($"  case {c.Name}");
            var why = new List<string>();
            BooleanOp.AnalyticRouteDebug = m => why.Add(Krn02Op + ": " + m);
            try { s = Krn02Scene(c.O, c.U, c.V, c.K, out g); } catch (Exception ex) { err = ex.Message; }
            finally { BooleanOp.AnalyticRouteDebug = null; }
            bool allMsg = Environment.GetEnvironmentVariable("P3D_METAALLMSG") == "1";
            foreach (var m in why.Where(m => allMsg || (hard && (m.Contains("declin") || m.Contains("exception"))))) Console.WriteLine($"   {c.Name}: {m}");
            double v = s == null ? double.NaN : (AnalyticVolume.Of(s) ?? double.NaN) / (c.K * c.K * c.K);
            v0 ??= v;
            string topo = s == null ? "-" : $"{s.Faces.Count()}/{s.Edges.Count()}/{s.Vertices.Count()}";
            string surf = s == null ? "-" : string.Join(",", s.Faces.GroupBy(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name ?? "none").OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Count()}"));
            var rep = s == null ? null : SolidValidator.Validate(s);
            var verdict = s == null ? err : rep!.Summary();
            double facetVol = 0;
            if (s != null && rep != null && !rep.IsValid && Environment.GetEnvironmentVariable("P3D_METAFACES") == "1")
                foreach (var fc in s.Faces)
                {
                    var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); fc.Surface.TessellateFace(fc, mb);
                    var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
                    int good = 0, bad = 0;
                    for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                    {
                        var a = mb.PositionsD[md.Indices[i]]; var b2 = mb.PositionsD[md.Indices[i + 1]]; var c2 = mb.PositionsD[md.Indices[i + 2]];
                        var n = Vector3d.Cross(b2 - a, c2 - a); if (n.Length < 1e-12) continue;
                        if (Vector3d.Dot(n, fc.NormalAt((a + b2 + c2) / 3)) > 0) good++; else bad++;
                    }
                    for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                    {
                        var a = mb.PositionsD[md.Indices[i]] - c.O; var b2 = mb.PositionsD[md.Indices[i + 1]] - c.O; var c2 = mb.PositionsD[md.Indices[i + 2]] - c.O;
                        facetVol += Vector3d.Dot(a, Vector3d.Cross(b2, c2)) / 6;
                    }
                }
            if (s != null && rep != null && !rep.IsValid && Environment.GetEnvironmentVariable("P3D_METAFACES") == "1")
                Console.WriteLine($"    vol {c.Name}: facets(double, rel. offset) {facetVol:R}; validator mesh.volume {rep.Metrics.GetValueOrDefault("mesh.volume"):R}");
            if (rep != null && Environment.GetEnvironmentVariable("P3D_METAISSUES") == "1")
                foreach (var iss in rep.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3)) Console.WriteLine($"    issue {c.Name}: {iss}");
            Console.WriteLine($"{{\"probe\":\"KRN02-META\",\"case\":\"{c.Name}\",\"guarantee\":\"{g}\",\"fev\":\"{topo}\",\"surfaces\":\"{surf}\",\"relVol\":{(v - v0.Value) / v0.Value:R},\"verdict\":\"{verdict}\"}}");
        }
    }
      }
}

static partial class Program
{
    /// <summary>KRN-02, замер: маленькая деталь (масштаб k) при сдвигах d — без поворота и с поворотом.</summary>
    static void Krn02MeasureFar()
    {
        bool prev = Krn02Hard; Krn02Hard = Environment.GetEnvironmentVariable("P3D_FARHARD") != "0";
        try
        {
            foreach (double k in new[] { 0.05, 1.0 })
            foreach (bool rot in new[] { false, true })
            foreach (double d in new[] { 0.0, 1e3, 1e4, 3e4, 1e5, 3e5 })
            {
                var U = rot ? new Vector3d(0.8, 0.5, 0.33) : Vector3d.UnitX; var V = rot ? new Vector3d(-0.4, 0.9, 0.1) : Vector3d.UnitY;
                var o = new Vector3d(d, -0.8 * d, 0.35 * d);
                var why = new List<string>();
                BooleanOp.AnalyticRouteDebug = m => why.Add(Krn02Op + ": " + m);
                string res;
                try
                {
                    var s = Krn02Scene(o, U, V, k, out var g);
                    var rep = SolidValidator.Validate(s);
                    res = $"{g} fev {s.Faces.Count()}/{s.Edges.Count()}/{s.Vertices.Count()} {rep.Summary()}";
                }
                catch (Exception ex) { res = "EXC " + ex.Message; }
                finally { BooleanOp.AnalyticRouteDebug = null; }
                Console.WriteLine($"FAR k {k} rot {rot} d {d:G3}: {res}");
                foreach (var m in why.Where(m => !m.Contains("cylinder not along") && !m.Contains("volume check") && !m.Contains("general accepted"))) Console.WriteLine($"      {m}");
            }
        }
        finally { Krn02Hard = prev; }
    }
}

static partial class Program
{
    /// <summary>KRN-02, разбор: открытые рёбра негодного результата общей булевой (маленькая повёрнутая деталь далеко).</summary>
    static void Krn02MeasureFarOpen()
    {
        double d = double.Parse(Environment.GetEnvironmentVariable("P3D_FARD") ?? "3e4", System.Globalization.CultureInfo.InvariantCulture);
        var U = new Vector3d(0.8, 0.5, 0.33).Normalized(); var V0 = new Vector3d(-0.4, 0.9, 0.1);
        var V = (V0 - U * Vector3d.Dot(V0, U)).Normalized(); var W = Vector3d.Cross(U, V);
        var o = new Vector3d(d, -0.8 * d, 0.35 * d); double k = 0.05;
        Vector3d L(Vector3d x) => new(Vector3d.Dot(x - o, U) / k, Vector3d.Dot(x - o, V) / k, Vector3d.Dot(x - o, W) / k);
        BooleanOp.GeneralInvalidProbe = r =>
        {
            Console.WriteLine($"  INVALID ({Krn02Op}): faces {r.Faces.Count()} edges {r.Edges.Count()}");
            foreach (var fc in r.Faces.Where(x => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(x)!) is Parametric3D.Kernel.Geometry.Ssi.Cylinder))
            {
                var cy = (Parametric3D.Kernel.Geometry.Ssi.Cylinder)Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(fc)!)!;
                var nl = AnalyticVolume.NormalIntegral(fc);
                var nf = Vector3d.Zero; if (fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fz) foreach (var (a, b, c) in fz.Facets) nf += Vector3d.Cross(b - a, c - a) / 2;
                Console.WriteLine($"    cyl face {fc.Tag} R {cy.R / k:0.###} rev {fc.Reversed} loops {fc.Loops.Count} Nl {nl:0.#####} Nf {nf:0.#####}");
                foreach (var l in fc.Loops)
                    Console.WriteLine($"       loop: {string.Join(" ", l.CoEdges.Select(ce => $"{ce.Edge.Curve.GetType().Name}(rev {ce.Reversed}, start {L(PCurves.At(ce, 0)):0.##})"))}");
            }
            var edges = r.Edges.ToList();
            foreach (var e in edges.Where(e => e.CoEdges.Count != 2))
            {
                Console.WriteLine($"   open edge {e.Id}: {e.Curve.GetType().Name} coedges {e.CoEdges.Count} V0 {L(e.V0.P)} V1 {L(e.V1.P)} len {(e.V1.P - e.V0.P).Length:E3} faces [{string.Join(",", e.CoEdges.Select(c => c.Loop?.Face?.Tag))}]");
                foreach (var e2 in edges.Where(x => x != e && (Near(x.V0.P, e.V0.P) || Near(x.V1.P, e.V0.P) || Near(x.V0.P, e.V1.P) || Near(x.V1.P, e.V1.P))))
                    Console.WriteLine($"      near {e2.Id}: {e2.Curve.GetType().Name} coedges {e2.CoEdges.Count} V0 {L(e2.V0.P)} V1 {L(e2.V1.P)} d00 {(e2.V0.P - e.V0.P).Length:E2} d01 {(e2.V0.P - e.V1.P).Length:E2} d10 {(e2.V1.P - e.V0.P).Length:E2} d11 {(e2.V1.P - e.V1.P).Length:E2}");
            }
            bool Near(Vector3d a, Vector3d b) => (a - b).Length < 1e-3;
        };
        bool prev = Krn02Hard; Krn02Hard = true;
        var why = new List<string>(); BooleanOp.AnalyticRouteDebug = m => why.Add(Krn02Op + ": " + m);
        Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => { if (Krn02Op == "Oblique" && (m.StartsWith("side probe") || m.StartsWith("region inside"))) Console.WriteLine($"   {Krn02Op}: {m}"); };
        try { Krn02Scene(o, U, V, k, out var g); Console.WriteLine($"guarantee {g}"); }
        finally { Krn02Hard = prev; BooleanOp.GeneralInvalidProbe = null; BooleanOp.AnalyticRouteDebug = null; Parametric3D.Kernel.Analytic.FaceSplit.Debug = null; }
        foreach (var m in why.Where(m => !m.Contains("cylinder not along") && !m.Contains("accepted"))) Console.WriteLine("   " + m[..Math.Min(m.Length, 200)]);
    }
}

static partial class Program
{
    static void Krn02MeasureFarClassify()
    {
        double d = 3e4, k = 0.05;
        var U = new Vector3d(0.8, 0.5, 0.33).Normalized(); var V0 = new Vector3d(-0.4, 0.9, 0.1);
        var V = (V0 - U * Vector3d.Dot(V0, U)).Normalized(); var W = Vector3d.Cross(U, V);
        var o = new Vector3d(d, -0.8 * d, 0.35 * d);
        var c = o + (U * 30 + V * 20) * k;
        Vector3d P(double x, double z) => c + (U * x + W * z) * k;
        var p = new[] { P(0, 8), P(14, 22), P(0, 22) };
        var prof = ProfileBuilder.Build(new Parametric3D.Kernel.Frame3(c, U, W), Enumerable.Range(0, 3)
            .Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 3])).ToList());
        var tool = RevolveOp.Build(prof, new Parametric3D.Kernel.Frame3(c, U, V), 0, 2 * Math.PI);
        TopologyConventions.Normalize(tool);
        var cl = new Parametric3D.Kernel.Analytic.Classifier(tool);
        Vector3d L(Vector3d x) => new(Vector3d.Dot(x - o, U) / k, Vector3d.Dot(x - o, V) / k, Vector3d.Dot(x - o, W) / k);
        var q = new Vector3d(29999.489238788443, -23999.51594224949, 10500.504788163613);
        Console.WriteLine($"tool faces: {string.Join(", ", tool.Faces.Select(f => f.Tag + ":" + Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)!)?.GetType().Name))}");
        foreach (var x in new[] { q, o + (U * 30 + V * 20 + W * 18) * k, o + (U * 30 + V * 20 + W * 30) * k, o + (U * 5 + V * 5 + W * 10) * k })
        {
            var log = new List<string>();
            Parametric3D.Kernel.Analytic.Classifier.Debug = m => log.Add(m);
            var r = cl.Classify(x);
            Parametric3D.Kernel.Analytic.Classifier.Debug = null;
            Console.WriteLine($"point local {L(x)}: class {r}");
            foreach (var m in log) Console.WriteLine("   " + m);
        }
    }
}

static partial class Program
{
    static void Krn02SmallFar()
    {
        bool prev = Krn02Hard; Krn02Hard = true;
        try
        {
            string? fev0 = null;
            double v0 = 0;
            foreach (double d in new[] { 0.0, 3e4, 3e5 })
            {
                var U = new Vector3d(0.8, 0.5, 0.33); var V = new Vector3d(-0.4, 0.9, 0.1);
                var s = Krn02Scene(new Vector3d(d, -0.8 * d, 0.35 * d), U, V, 0.05, out var g);
                string fev = $"{s.Faces.Count()}/{s.Edges.Count()}/{s.Vertices.Count()}"; fev0 ??= fev;
                var rep = SolidValidator.ValidateBRep(s);
                double vol = (AnalyticVolume.Of(s) ?? SolidValidator.Validate(s).Metrics.GetValueOrDefault("mesh.volume", double.NaN)) / (0.05 * 0.05 * 0.05);
                if (d == 0) v0 = vol;
                // 2026-10-12: деталь на 3e5 мм — тоже точно и с той же топологией (локальная система слоёв P3D_PRISMLOCAL и
                // объём фасет P3D_VOLFACETSLOCAL); прежний «известный дефект» ниже оставлен для мутаций
                if (d < 1e5 || !Krn02FarOld)
                    Require(g == GuaranteeLevel.ExactAnalytic && fev == fev0 && rep.IsValid && Math.Abs(vol - v0) <= 1e-6 * v0, $"d={d}: {g} {fev} vs {fev0} {rep.Summary()} vol {vol:G10} vs {v0:G10}");
                else
                {
                    // ИЗВЕСТНЫЙ ДЕФЕКТ (KRN-03, воспроизводитель: K01Checks krn02far1, P3D_FARD=3e5): стенка наклонного отверстия
                    // собирается с контурами против нормали; проверка loop-orientation не пропускает такой точный результат —
                    // операция уходит в многогранный путь. Требуется: годный B-rep, ни одной несогласованной грани, объём в
                    // пределах гранения. Не ExactAnalytic — это блокер в матрице, а не принятое поведение.
                    // 2026-10-12: после объёма фасет в локальной системе (P3D_VOLFACETSLOCAL) четыре первые операции на 3e5 мм —
                    // точный маршрут; остаётся наклонное отверстие: у результата общей булевой боковые грани бруска остаются
                    // ExtrudedSurface (на d=0 — PlaneSurface), и их фасеты намотаны внутрь (P3D_VOLDBG=1: объём фасет −5.28 при
                    // точном 5.27) — проверка «объём против фасет» отвергает маршрут.
                    Require(rep.IsValid && rep.Metrics.GetValueOrDefault("brep.loopOrientationErrors") == 0 && Math.Abs(vol - v0) <= 2e-3 * v0,
                            $"d={d} (known defect): {g} {rep.Summary()} loopOrientationErrors {rep.Metrics.GetValueOrDefault("brep.loopOrientationErrors")} vol {vol:G8} vs {v0:G8}");
                    Log(new { test = "KRN02-SMALL-FAR", knownDefect = "d=3e5: oblique hole wall assembled against its normal; analytic route refused", guarantee = g.ToString() });
                }
            }
            // KRN-02 (02.9): без наклонного отверстия и зенковки та же деталь на 3e5 мм — точный маршрут целиком (объём фасет
            // считается в локальной системе; прежде фасеты тела 5.497 давали 0.064, и цепочка уходила в многогранный путь)
            Krn02Hard = false;
            {
                var U = new Vector3d(0.8, 0.5, 0.33); var V = new Vector3d(-0.4, 0.9, 0.1);
                // маршрут — тот же, что у начала: слоистый (2.5D) там, где он у начала (локальная система слоёв, P3D_PRISMLOCAL)
                var routes0 = new List<string>(); var routesF = new List<string>();
                BooleanOp.AnalyticRouteDebug = m => { if (m.EndsWith("accepted")) routes0.Add(m); };
                var s0 = Krn02Scene(Vector3d.Zero, U, V, 0.05, out var g0);
                BooleanOp.AnalyticRouteDebug = m => { if (m.EndsWith("accepted")) routesF.Add(m); };
                var sf = Krn02Scene(new Vector3d(3e5, -0.8 * 3e5, 0.35 * 3e5), U, V, 0.05, out var gf);
                BooleanOp.AnalyticRouteDebug = null;
                Require(string.Join(",", routes0) == string.Join(",", routesF), $"d=3e5: routes [{string.Join(",", routesF)}] vs at the origin [{string.Join(",", routes0)}]");
                double a0 = AnalyticVolume.Of(s0) ?? double.NaN, af = AnalyticVolume.Of(sf) ?? double.NaN;
                Require(gf == GuaranteeLevel.ExactAnalytic && g0 == GuaranteeLevel.ExactAnalytic && Math.Abs(af - a0) <= 1e-6 * a0,
                        $"d=3e5 without the oblique hole: {gf} (at the origin {g0}), V {af:G10} vs {a0:G10}");
            }
        }
        finally { Krn02Hard = prev; }
    }
}

