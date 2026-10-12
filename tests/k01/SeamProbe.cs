using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>04.8: конец куска на шве замкнутого ребра — вал с поперечным отверстием, разрезанный плоскостью через точку шва
    /// кромки отверстия. Объём — по формуле (половина), вершины — на ребре.</summary>
    static void SeamProbe()
    {
        TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
        foreach (double dx in new[] { 0.0, 1e-6, -1e-6, 1e-4, -1e-4, 1e-2, -1e-2 })
        {
            var b = new List<TSolid> { Shaft() };
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
            var top = b[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3 && e.Curve.PointAt(0.5).Z > 5);
            var seam = top.V0.P;
            // полупространство за плоскостью через шов, поперёк оси X... плоскость x = seam.X (нормаль X)
            var half = TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 200, 200), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            half = TransformOp.Copy(half, TransformOp.Move(new Vector3d(seam.X + dx, 0, 0)));
            long r0 = Parametric3D.Kernel.Analytic.FaceIntersect.RefineFails;
            var w = new List<string>();
            var why = new List<string>(); BooleanOp.AnalyticRouteDebug = m => why.Add(m);
            var rr = BooleanOp.Evaluate(b, half, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w); b = rr.Bodies.ToList();
            Console.WriteLine($"   outcome {rr.Outcome} {rr.Guarantee}; {string.Join(" | ", w.Take(3))}");
            BooleanOp.AnalyticRouteDebug = null;
            if (b[0].Guarantee != GuaranteeLevel.ExactAnalytic) foreach (var m in why.Take(8)) Console.WriteLine("   route: " + m[..Math.Min(300, m.Length)]);
            var s = b[0];
            double worst = 0;
            foreach (var v in s.Vertices)
            {
                // вершины на кромке: расстояние до обоих цилиндров
                double dS = Math.Abs(new Vector2d(v.P.Y, v.P.Z).Length - 20), dH = Math.Abs(new Vector2d(v.P.X, v.P.Y).Length - 4);
                if (dS < 1e-3 && dH < 1e-3) worst = Math.Max(worst, Math.Max(dS, dH));
            }
            Console.WriteLine($"SEAM dx {dx}: seam {seam:0.######} guarantee {s.Guarantee} valid {SolidValidator.Validate(s).IsValid} V {AnalyticVolume.Of(s):R} rim vertex off {worst:E2} refine fails {Parametric3D.Kernel.Analytic.FaceIntersect.RefineFails - r0}");
        }
    }
}
