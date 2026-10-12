using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void TopoDump(TSolid b)
    {
        foreach (var f in b.Faces)
        {
            Console.WriteLine($"{f} rev={f.Reversed} loops={f.Loops.Count}");
            foreach (var l in f.Loops)
                Console.WriteLine("   " + (l.IsOuter ? "outer" : "inner") + ": " + string.Join("  ", l.CoEdges.Select(c => $"{c.Edge}{(c.Reversed ? "-" : "+")}[{c.Edge.Curve.GetType().Name} {c.Start.P.X:F2},{c.Start.P.Y:F2},{c.Start.P.Z:F2}]")));
        }
    }
    static void TopoProbe()
    {
        TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
        var b = new List<TSolid> { Shaft() };
        BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -30), 4, 60), new List<string>());
        Console.WriteLine($"HOLE guarantee {b[0].Guarantee}"); TopoDump(b[0]);
        var c = new List<TSolid> { Shaft() };
        BooleanOp.Unite(c, PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 8, 40), new List<string>());
        Console.WriteLine($"BOSS guarantee {c[0].Guarantee}"); TopoDump(c[0]);
    }
}
