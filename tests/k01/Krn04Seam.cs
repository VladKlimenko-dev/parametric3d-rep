using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn04SeamChecks()
    {
        Check("KRN04-REFINE-AT-CLOSED-EDGE-SEAM", () =>
        {
            // 04.8: конец куска пересечения на ШВЕ ЗАМКНУТОГО РЕБРА (кромка поперечного отверстия — кривая пересечения с
            // одной вершиной) и рядом с ним по обе стороны. Вал R20 (ось X) с отверстием d8 (ось Z) режется плоскостью
            // x = x_шва + dx: точный маршрут, вершины на кромке — на обоих цилиндрах до 1e-12, объём = половина + ∫A(x)dx
            // (сечение A(x) = πR² − ∫ 2√(R² − y²) по ширине щели 2√(16 − x²)), уточнение конца куска не отказывает.
            var fails = new List<string>();
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            double A(double x)
            {
                double w = Math.Sqrt(16 - x * x), s = 0; int n = 2000;
                for (int i = 0; i < n; i++) { double y = -w + 2 * w * (i + 0.5) / n; s += 2 * Math.Sqrt(400 - y * y) * 2 * w / n; }
                return Math.PI * 400 - s;
            }
            double baseV = double.NaN;
            foreach (double dx in new[] { 0.0, 1e-4, -1e-4, 1e-2, -1e-2 })
            {
                var b = new List<TSolid> { Shaft() };
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
                if (double.IsNaN(baseV)) baseV = AnalyticVolume.Of(b[0]) ?? double.NaN;
                var top = b[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3 && e.Curve.PointAt(0.5).Z > 5);
                if (!ReferenceEquals(top.V0, top.V1)) { fails.Add("rim is not a closed edge"); break; }
                var seam = top.V0.P;
                var half = TransformOp.Copy(PrimitiveOp.Cylinder(Vector3d.Zero, 200, 200), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
                half = TransformOp.Copy(half, TransformOp.Move(new Vector3d(seam.X + dx, 0, 0)));
                long f0 = Parametric3D.Kernel.Analytic.FaceIntersect.RefineFails;
                var r = BooleanOp.Evaluate(b, half, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                long rf = Parametric3D.Kernel.Analytic.FaceIntersect.RefineFails - f0;
                var s = r.Bodies.FirstOrDefault();
                if (s == null || r.Bodies.Count != 1) { fails.Add($"dx {dx}: {r.Outcome}"); continue; }
                double worst = 0; int onRim = 0;
                foreach (var v in s.Vertices)
                {
                    double dS = Math.Abs(new Vector2d(v.P.Y, v.P.Z).Length - 20), dH = Math.Abs(new Vector2d(v.P.X, v.P.Y).Length - 4);
                    if (dS < 1e-3 && dH < 1e-3) { onRim++; worst = Math.Max(worst, Math.Max(dS, dH)); }
                }
                // объём: половина + ∫₀^dx A(x) dx (Симпсон)
                double want = baseV / 2 + dx / 6 * (A(0) + 4 * A(dx / 2) + A(dx));
                double v1 = AnalyticVolume.Of(s) ?? double.NaN;
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"dx {dx}: {s.Guarantee}");
                if (!(worst < 1e-12) || onRim < 2) fails.Add($"dx {dx}: rim vertices {onRim}, off {worst:E2}");
                if (!(Math.Abs(v1 - want) < 1e-6)) fails.Add($"dx {dx}: V {v1:R} vs {want:R}");
                if (rf != 0) fails.Add($"dx {dx}: {rf} refine failure(s)");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"dx {dx}: invalid");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
