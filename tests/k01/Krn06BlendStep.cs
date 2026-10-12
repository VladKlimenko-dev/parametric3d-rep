using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn06BlendStepChecks()
    {
        Check("KRN06-BLEND-STEP-EXPORT", () =>
        {
            // ТОЧНЫЙ STEP ТЕЛА С ЛЕНТОЙ КАТЯЩЕГОСЯ ШАРА: кромка поперечного отверстия r1 и кромка усечённого конуса r1.
            // Запись удаётся (прежде — честный отказ «surface SectionSurface is not written exactly yet»), лента — рациональный
            // B-сплайн; его отход от ленты по густой сетке (40 × 11 точек на грань, расстояние до самой поверхности ленты)
            // ≤ 1/10 бюджета экспорта; наш импорт читает одно замкнутое годное тело с тем же числом граней и объёмом сетки
            // в пределах гранения.
            var fails = new List<string>();
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            var cases = new (string Name, Func<TSolid> Make)[]
            {
                ("cross-hole rim", () =>
                {
                    var b = new List<TSolid> { Shaft() };
                    BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
                    var e = b[0].Edges.Where(x => x.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && x.Curve.PointAt(0.5).Z > 5).ToList();
                    FilletOp.RoundEdges(b, e.Select(EdgeRef.Of).ToList(), 1, new List<string>());
                    return b[0];
                }),
                ("frustum rim", () =>
                {
                    var b = new List<TSolid> { RevolveProfile(Poly((0, 0), (20, 0), (12, 20), (0, 20))) };
                    var e = b[0].Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 12) < 1e-9).ToList();
                    FilletOp.RoundEdges(b, e.Select(EdgeRef.Of).ToList(), 1, new List<string>());
                    return b[0];
                }),
            };
            double budget = ToleranceContext.Current.ExportFit / 10;
            foreach (var (name, make) in cases)
            {
                var s = make();
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name}: not built exactly ({s.Guarantee})"); continue; }
                // отход сплайна от ленты — независимо от внутренней меры подгонки
                foreach (var f in s.Faces)
                {
                    if (GeometryAudit.AuthoritativeSurface(f) is not Parametric3D.Kernel.Surfaces.SectionSurface ss) continue;
                    var rg = AnalyticVolume.BandRange(f, ss);
                    if (rg == null) { fails.Add($"{name}: blend face range not found"); continue; }
                    var fit = Parametric3D.Kernel.Surfaces.BandNurbs.Fit(ss, rg.Value.Ua, rg.Value.Ub, budget);
                    if (fit == null) { fails.Add($"{name}: no B-spline within {budget:E1}"); continue; }
                    double worst = 0;
                    for (int i = 0; i < 40; i++) for (int k = 0; k <= 10; k++)
                    {
                        var q = fit.Value.S.PointAt(new Vector2d((i + 0.31) / 40, k / 10.0));
                        worst = Math.Max(worst, (ss.PointAt(ss.ProjectToUV(q)) - q).Length);
                    }
                    if (!(worst <= budget)) fails.Add($"{name}: B-spline off the blend by {worst:E2} (budget {budget:E1})");
                }
                var w = new List<string>(); var sw = new StringWriter();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, "blend", w)) { fails.Add($"{name}: STEP refused: {string.Join(" | ", w.Where(x => x.Contains("not written")))}"); continue; }
                string text = sw.ToString();
                if (!text.Contains("RATIONAL_B_SPLINE_SURFACE")) fails.Add($"{name}: no rational B-spline surface in the file");
                var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(text, new List<string>());
                if (back.Count != 1) { fails.Add($"{name}: read back {back.Count} solid(s)"); continue; }
                var r = back[0];
                if (r.Faces.Count() != s.Faces.Count()) fails.Add($"{name}: faces {r.Faces.Count()} vs {s.Faces.Count()}");
                if (!SolidValidator.ValidateBRep(r).IsValid) fails.Add($"{name}: read-back B-rep invalid");
                double vr = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(r)), vs = AnalyticVolume.Of(s) ?? double.NaN;
                if (!(Math.Abs(vr - vs) < 2e-3 * vs)) fails.Add($"{name}: read-back volume {vr:R} vs {vs:R}");
                Console.WriteLine($"  {name}: STEP {text.Length} chars, read back faces {r.Faces.Count()}, V {vr:R} vs {vs:R}");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
