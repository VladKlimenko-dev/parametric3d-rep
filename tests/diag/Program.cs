using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static class P
{
    static TSolid Box(double lo, double hi)
    {
        var frame = new Frame3(new Vector3d(lo, lo, lo), Vector3d.UnitX, Vector3d.UnitY);
        var pts = new[] { new Vector3d(lo, lo, lo), new Vector3d(hi, lo, lo), new Vector3d(hi, hi, lo), new Vector3d(lo, hi, lo) };
        var curves = new List<ICurve3> { new LineCurve3(pts[0], pts[1]), new LineCurve3(pts[1], pts[2]), new LineCurve3(pts[2], pts[3]), new LineCurve3(pts[3], pts[0]) };
        return ExtrudeOp.Build(ProfileBuilder.Build(frame, curves), new Vector3d(0, 0, hi - lo));
    }

    static TSolid HalfSpace(Vector3d at, Vector3d n, double size)
    {
        Vector3d u = Math.Abs(n.X) < 0.9 ? Vector3d.Cross(n, Vector3d.UnitX) : Vector3d.Cross(n, Vector3d.UnitY);
        u = u.Normalized(); Vector3d v = Vector3d.Cross(n, u).Normalized();
        var frame = new Frame3(at, u, v);
        var pts = new[] { at - u * size - v * size, at + u * size - v * size, at + u * size + v * size, at - u * size + v * size };
        var curves = new List<ICurve3> { new LineCurve3(pts[0], pts[1]), new LineCurve3(pts[1], pts[2]), new LineCurve3(pts[2], pts[3]), new LineCurve3(pts[3], pts[0]) };
        return ExtrudeOp.Build(ProfileBuilder.Build(frame, curves), n * size);
    }

    static void Main()
    {
        BooleanOp.Threads = 1;
        var w = new List<string>();
        double t = 2.2;

        var part = new Part("vr");
        TestGeometry.AddCube(part, 100);
        part.AddFeature(new VertexRoundFeature("VR") { Vertex = new Vector3d(50, 50, 50), Radius = new ValueRef(6) });
        part.Rebuild(null);
        var body = part.Bodies[0];

        Vector3d C = new(45.757359312880716, 45.757359312880716, 45.757359312880716);
        double R = 6;

        var inner = Box(-50 + t, 50 - t);
        var cav = new List<TSolid> { inner };

        // Sector: offset corner cone x>=47.8, y>=47.8, z>=47.8.
        var sector = new List<TSolid> { Box(-200, 200) };
        BooleanOp.Intersect(sector, HalfSpace(new Vector3d(50 - t, 0, 0), -Vector3d.UnitX, 400), w);
        BooleanOp.Intersect(sector, HalfSpace(new Vector3d(0, 50 - t, 0), -Vector3d.UnitY, 400), w);
        BooleanOp.Intersect(sector, HalfSpace(new Vector3d(0, 0, 50 - t), -Vector3d.UnitZ, 400), w);

        var wedge = new List<TSolid>(sector);
        BooleanOp.Subtract(wedge, PrimitiveOp.Sphere(C, R - t), w);

        foreach (var piece in wedge.ToList()) BooleanOp.Subtract(cav, piece, w);

        var shell = new List<TSolid> { TopologyClone.Deep(body) };
        BooleanOp.Subtract(shell, cav[0], w);

        double bv = AnalyticVolume.Of(body) ?? double.NaN;
        double sv = shell.Sum(x => AnalyticVolume.Of(x) ?? double.NaN);
        Console.WriteLine($"body vol={bv}");
        Console.WriteLine($"shell vol={sv}");
        Console.WriteLine($"cavity vol={AnalyticVolume.Of(cav[0])}");
        Console.WriteLine($"expected shell vol (body - cavity) = {bv - (AnalyticVolume.Of(cav[0]) ?? 0)}");
        Console.WriteLine("warnings: " + string.Join(" || ", w));
        if (shell.Count > 0)
        {
            var v = SolidValidator.Validate(shell[0]);
            Console.WriteLine("shell validator: " + v.Summary());
        }
    }
}
