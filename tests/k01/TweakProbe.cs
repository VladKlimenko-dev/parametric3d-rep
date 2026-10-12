using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void TweakProbe()
    {
        var rot = TransformOp.Rotate(new Vector3d(4, 1, -2), new Vector3d(0.3, -0.5, 0.8).Normalized(), 41);
        foreach (var b in new[] { Box(0, 0, 0, 60, 40, 20), TransformOp.Copy(Box(0, 0, 0, 60, 40, 20), rot) })
        {
            var c = b.Vertices.Aggregate(Vector3d.Zero, (s, v) => s + v.P) / b.Vertices.Count();
            foreach (var f in b.Faces)
            {
                var src = GeometryAudit.AuthoritativeSurface(f);
                var p = f.Loops[0].CoEdges[0].Start.P;
                var no = PlanarTweak.OutwardNormal(f);
                Console.WriteLine($"TW {f.Surface.GetType().Name}/{src?.GetType().Name} rev {f.Reversed} out {no:0.###} true {(Vector3d.Dot(no ?? Vector3d.Zero, p - c) > 0 ? "ok" : "WRONG")} faceN {f.NormalAt(p):0.###}");
            }
        }
    }
}
