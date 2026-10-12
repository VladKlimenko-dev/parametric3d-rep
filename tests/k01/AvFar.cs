using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void AvFarProbe()
    {
        foreach (double d in new[] { 0.0, 3e4, 3e5 })
        {
            var off = new Vector3d(d, -0.8 * d, 0.35 * d);
            var rot = TransformOp.Rotate(Vector3d.Zero, new Vector3d(0.3, 0.8, 0.5).Normalized(), 37);
            var b = new List<TSolid> { TransformOp.Copy(TransformOp.Copy(Box(0, 0, 0, 3, 2, 1), rot), TransformOp.Move(off)) };
            var cyl = TransformOp.Copy(TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(1.5, 1, -0.1), 0.4, 1.2), rot), TransformOp.Move(off));
            var w = new List<string>();
            BooleanOp.Subtract(b, cyl, w);
            double want = 6 - Math.PI * 0.16;
            Console.WriteLine($"d={d}: guarantee {b[0].Guarantee} AV {AnalyticVolume.Of(b[0]):R} want {want:R} rel {((AnalyticVolume.Of(b[0]) ?? double.NaN) - want) / want:E2}");
            foreach (var f in b[0].Faces) Console.WriteLine($"    {f.Tag} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}");
        }
    }
}
static partial class Program
{
    static void AvFarScene()
    {
        foreach (double d in new[] { 0.0, 3e5 })
        {
            var o = new Vector3d(d, -0.8 * d, 0.35 * d); double k = 0.05;
            var U = new Vector3d(0.8, 0.5, 0.33).Normalized(); var V0 = new Vector3d(-0.4, 0.9, 0.1);
            var V = (V0 - U * Vector3d.Dot(V0, U)).Normalized(); var W = Vector3d.Cross(U, V);
            Frame3 At(double x, double y, double z) => new(o + (U * x + V * y + W * z) * k, U, V);
            var f = At(0, 0, 0); Vector3d P(double x, double y) => f.Origin + (U * x + V * y) * k;
            var p = new[] { P(0, 0), P(60, 0), P(60, 40), P(0, 40) };
            var box = ExtrudeOp.Build(ProfileBuilder.Build(f, Enumerable.Range(0, 4).Select(i => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.LineCurve3(p[i], p[(i + 1) % 4])).ToList()), W * 20 * k);
            var fd = At(30, 20, -2);
            var disk = ExtrudeOp.Build(ProfileBuilder.Build(fd, new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(fd, 8 * k, 0, 2 * Math.PI) }), W * 24 * k);
            Console.WriteLine($"d={d}: box AV {AnalyticVolume.Of(box):R} want {60 * 40 * 20 * k * k * k:R}; disk AV {AnalyticVolume.Of(disk):R} want {Math.PI * 64 * 24 * k * k * k:R}");
            try { Parametric3D.Kernel.Analytic.PrismBuild.Boolean(box, disk, Parametric3D.Kernel.Analytic.Bool2.Subtract, out var wy); Console.WriteLine("direct prism: " + wy); }
            catch (Exception e) { Console.WriteLine("direct prism exception: " + e); }
            BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   route: " + m);
            var b = new List<TSolid> { box }; BooleanOp.Subtract(b, disk, new List<string>());
            BooleanOp.AnalyticRouteDebug = null;
            Console.WriteLine($"   result {b[0].Guarantee} AV {AnalyticVolume.Of(b[0]):R} want {(48000 - Math.PI * 64 * 20) * k * k * k:R}");
        }
    }
}
