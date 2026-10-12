using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Что делают следующие операции с телом, скруглённым прямым построителем цилиндр/цилиндр.</summary>
    static void CylCylPost()
    {
        TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
        List<TSolid> Rounded()
        {
            var b = new List<TSolid> { Shaft() };
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
            var e = b[0].Edges.Where(x => x.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && x.Curve.PointAt(0.5).Z > 5).ToList();
            FilletOp.RoundEdges(b, e.Select(EdgeRef.Of).ToList(), 1, new List<string>());
            return b;
        }
        void Report(string what, Func<List<TSolid>> f)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var b = f();
                var s = b[0];
                double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(s));
                Console.WriteLine($"POST {what}: bodies {b.Count} guarantee {s.Guarantee} valid {SolidValidator.Validate(s).IsValid} V {AnalyticVolume.Of(s):R} mesh {vm:R} faces {s.Faces.Count()} {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e) { Console.WriteLine($"POST {what}: EXCEPTION {e.GetType().Name}: {e.Message[..Math.Min(200, e.Message.Length)]} {sw.ElapsedMilliseconds} ms"); }
        }
        Report("rounded", Rounded);
        Report("then bottom rim round r1", () =>
        {
            var b = Rounded();
            var e = b[0].Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3 && x.Curve.PointAt(0.5).Z < -5).ToList();
            FilletOp.RoundEdges(b, e.Select(EdgeRef.Of).ToList(), 1, new List<string>());
            return b;
        });
        Report("then subtract box far away", () => { var b = Rounded(); BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(45, 0, -40), 10, 80), new List<string>()); return b; });
        Report("then subtract box through blend", () => { var b = Rounded(); BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(4.5, 0, -40), 2, 80), new List<string>()); return b; });
        Report("then unite cylinder elsewhere", () => { var b = Rounded(); BooleanOp.Unite(b, PrimitiveOp.Cylinder(new Vector3d(30, 0, 0), 5, 30), new List<string>()); return b; });
        Report("then mirror copy", () => { var b = Rounded(); return new List<TSolid> { TransformOp.Copy(b[0], TransformOp.Mirror(new Vector3d(3, 0, 0), new Vector3d(1, 0.2, 0))) }; });
        Report("then rotate copy", () => { var b = Rounded(); return new List<TSolid> { TransformOp.Copy(b[0], TransformOp.Rotate(new Vector3d(1, 2, 3), new Vector3d(1, 1, 0), 37)) }; });
        Report("cross hole (no round) mirror copy", () =>
        {
            var b = new List<TSolid> { Shaft() };
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
            Console.WriteLine($"   before: V {AnalyticVolume.Of(b[0]):R}");
            return new List<TSolid> { TransformOp.Copy(b[0], TransformOp.Mirror(new Vector3d(3, 0, 0), new Vector3d(1, 0.2, 0))) };
        });
        Report("then translate copy", () => { var b = Rounded(); return new List<TSolid> { TransformOp.Copy(b[0], TransformOp.Move(new Vector3d(5, 6, 7))) }; });
        {
            var b = Rounded(); var w = new List<string>(); var sw2 = new StringWriter();
            bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw2, b, "cc", w);
            Console.WriteLine($"POST step exact: {ok}; {string.Join(" | ", w.Take(3))}");
            {
                var pb = new List<TSolid> { Shaft() };
                BooleanOp.Subtract(pb, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
                var sw3 = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(sw3, pb, "plain", new List<string>());
                File.WriteAllText("/tmp/claude-0/cc_plain.step", sw3.ToString());
                Console.WriteLine($"POST plain: our V {AnalyticVolume.Of(pb[0]):R} area {AnalyticVolume.AreaOf(pb[0]):R}");
            }
            if (ok) { File.WriteAllText("/tmp/claude-0/cc_round.step", sw2.ToString()); Console.WriteLine($"POST step written; our V {AnalyticVolume.Of(b[0]):R} area {AnalyticVolume.AreaOf(b[0]):R} faces {b[0].Faces.Count()}"); }
        }
    }
}
