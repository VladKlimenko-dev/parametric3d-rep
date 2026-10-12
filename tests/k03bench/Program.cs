using System.Diagnostics;
using System.Text.Json;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static class P
{
    sealed class CutSphere : Feature { public override void Execute(BuildContext c) =>
        BooleanOp.Subtract(c.Bodies, PrimitiveOp.Sphere(new Vector3d(10, 10, 30), 8), c.Warnings); }
    sealed class Second : Feature { public override void Execute(BuildContext c) => c.Bodies.Add(PrimitiveOp.Cylinder(new Vector3d(300, 0, 0), 20, 40)); }
    sealed class CutLast(double r) : Feature { public double R = r; public override void Execute(BuildContext c)
        { var l = new List<TSolid> { c.Bodies[^1] }; BooleanOp.Subtract(l, PrimitiveOp.Cylinder(new Vector3d(300, 0, -10), R, 60), c.Warnings);
          c.Bodies[^1] = l[0]; } }
    sealed class Fails : Feature { public bool On; public override void Execute(BuildContext c) { if (On) throw new Parametric3D.Core.RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "off"); } }
    static void Main(string[] args)
    {
        BooleanOp.Threads = Tessellator.Threads = 1;
        Console.WriteLine(JsonSerializer.Serialize(new { runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, cpu = Environment.ProcessorCount }));
        for (int round = 0; round < 6; round++)
        {
            var p = new Part("b"); TestGeometry.AddCube(p, 60);
            p.AddFeature(new CutSphere()); p.AddFeature(new Second()); var cut = p.AddFeature(new CutLast(5)); var fail = p.AddFeature(new Fails());
            double T(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
            double cold = T(() => p.Rebuild(null));
            cut.R = 5 + 0.5 * (round + 1); p.MarkDirtyFrom(cut);
            double editOtherBody = T(() => p.Rebuild(null));
            fail.On = true; p.MarkDirtyFrom(fail);
            double failingLast = T(() => p.Rebuild(null));
            fail.On = false; p.MarkDirtyFrom(fail);
            double recoverLast = T(() => p.Rebuild(null));
            Console.WriteLine(JsonSerializer.Serialize(new { round, cold, editOtherBody, failingLast, recoverLast, volume = p.Volume }));
        }
    }
}
