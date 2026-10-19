using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Query;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// KRN-02 (02.6): ПАМЯТЬ ЯДРА СЛЕДУЕТ ЗА БЮДЖЕТОМ ТОЧНОСТИ. Смена единиц или масштаба меняет модельную точность; проверка
    /// тела из памяти, подготовленное тело и объём не должны отвечать по прежнему бюджету. Призма с ребром 5e-5 мм: при
    /// модельной точности 1e-7 — годна, при 1e-4 — вырожденное ребро (ошибка); точка в 5e-7 от грани — на границе при
    /// допуске классификации 1e-6, внутри при 1e-8; подобие ×1000 вместе с бюджетом ×1000 — те же ответы.
    /// </summary>
    static void Krn02CacheChecks()
    {
        Check("KRN02-CACHES-FOLLOW-TOLERANCE", () =>
        {
            var fails = new List<string>();
            TSolid Prism(double k)
            {
                var f = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
                var p = new[] { f.ToWorld(0, 0), f.ToWorld(10 * k, 0), f.ToWorld(10 * k, 10 * k), f.ToWorld(4e-5 * k, (10 + 3e-5) * k), f.ToWorld(0, 10 * k) };
                return ExtrudeOp.Build(ProfileBuilder.Build(f, Enumerable.Range(0, 5).Select(i => (ICurve3)new LineCurve3(p[i], p[(i + 1) % 5])).ToList()), new Vector3d(0, 0, 10 * k));
            }
            KernelContext Fine(double acc) => new(KernelSettings.Capture() with { Tolerance = ToleranceContext.Default with { ModelAccuracy = acc } });
            bool Degenerate(ValidationReport r) => r.Issues.Any(i => i.Code == "degenerate-edge" && i.Severity == ValidationSeverity.Error);
            var body = Prism(1);
            if (Degenerate(SolidValidator.Validate(body))) fails.Add("default budget: the 5e-5 edge is reported degenerate");
            using (Fine(1e-4).Enter())
                if (!Degenerate(SolidValidator.Validate(body))) fails.Add("budget 1e-4: the validation answer came from the 1e-7 budget");
            if (Degenerate(SolidValidator.Validate(body))) fails.Add("default budget again: the answer of budget 1e-4 is kept");
            // подготовленное тело: допуск классификации — по действующему бюджету
            var box = Box(0, 0, 0, 10, 10, 10); var pb = PreparedBody.For(box); var q = new Vector3d(5, 5, 10 - 5e-7);
            var c0 = pb.Classify(q);
            PointClass c1; using (Fine(1e-9).Enter()) c1 = PreparedBody.For(box).Classify(q);
            if (c0 != PointClass.OnBoundary || c1 != PointClass.Inside) fails.Add($"prepared classify near the face: {c0} (want OnBoundary) / {c1} under 1e-9 (want Inside)");
            // объём (запомненный по настройкам, в т. ч. бюджету): значение — то же
            var cyl = PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10);
            var m0 = AnalyticMeasure.Volume(cyl); Measured m1; using (Fine(1e-5).Enter()) m1 = AnalyticMeasure.Volume(cyl);
            if (m0.Value != m1.Value) fails.Add($"volume differs between budgets: {m0.Value:R} vs {m1.Value:R}");
            // подобие ×1000 с бюджетом ×1000: те же ответы
            var big = Prism(1000);
            using (Fine(1e-4).Enter())
            {
                if (Degenerate(SolidValidator.Validate(big))) fails.Add("×1000 with budget ×1000: degenerate edge reported");
                var bb = Box(0, 0, 0, 1e4, 1e4, 1e4);
                var cb = PreparedBody.For(bb).Classify(new Vector3d(5e3, 5e3, 1e4 - 5e-4));
                if (cb != PointClass.OnBoundary) fails.Add($"×1000: classify near the face {cb}");
            }
            using (Fine(1e-1).Enter()) if (!Degenerate(SolidValidator.Validate(big))) fails.Add("×1000 with budget 1e-1: degenerate edge not reported");
            Log(new { test = "KRN02-CACHES-FOLLOW-TOLERANCE", fails = fails.Count, err0 = m0.Error, err1 = m1.Error });
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
