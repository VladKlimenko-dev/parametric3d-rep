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
static partial class Program
{
    static void CylFaceProbe()
    {
        var bl = new List<Parametric3D.Kernel.Topology.TSolid> { Box(0, 0, 0, 60, 40, 20) };
        Parametric3D.Kernel.Ops.BooleanOp.Unite(bl, Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, 20), 8, 15), new List<string>());
        foreach (var f in bl[0].Faces)
        {
            var src = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
            Console.WriteLine($"CF {f.Surface.GetType().Name}/{src?.GetType().Name} rev {f.Reversed} loops {f.Loops.Count} tag {f.Tag} G {bl[0].Guarantee}");
            foreach (var l in f.Loops) Console.WriteLine("   loop " + string.Join(", ", l.CoEdges.Select(ce => $"{ce.Edge.Curve.GetType().Name}{(ce.Edge.V0 == ce.Edge.V1 ? "(closed)" : "")} {ce.Start.P:0.##}")));
        }
    }
}
static partial class Program
{
    static void ProvProbe()
    {
        var bl = new List<Parametric3D.Kernel.Topology.TSolid> { Box(0, 0, 0, 40, 30, 20) };
        Parametric3D.Kernel.Ops.BooleanOp.Unite(bl, Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(20, 15, 20), 6, 10), new List<string>());
        var w = new List<string>();
        bool ok = Parametric3D.Kernel.Ops.DraftOp.Apply(bl, new[] { new Vector3d(26, 15, 25) }, new Vector3d(0, 0, 20), Vector3d.UnitZ, 4, w);
        Console.WriteLine($"PP ok {ok} prov {(bl[0].Provenance != null)} rev {bl[0].Revision} last {bl[0].LastEdit} | {string.Join(" | ", w)}");
    }
}
static partial class Program
{
    static void FaceCompProbe()
    {
        var (p, h) = HoleInCube(new Vector3d(0, 0, 30), new Vector3d(0, 0, -1), 8, true, kind: Parametric3D.Model.Features.HoleKind.Counterbore);
        foreach (var f in p.Bodies[0].Faces.Where(f => f.Loops.Count > 1))
        {
            var src = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
            Console.WriteLine($"FC {f.Tag} {f.Surface.GetType().Name}/{src?.GetType().Name} rev {f.Reversed} N {(src as Parametric3D.Kernel.Surfaces.PlaneSurface)?.F.W:0.##}");
            foreach (var l in f.Loops)
            {
                var pts = new List<Vector3d>(); foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                Vector3d acc = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) acc += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                Console.WriteLine($"   loop outer {l.IsOuter} n {l.CoEdges.Count} pts {pts.Count} area2 {acc:0.##} first {pts[0]:0.##} last {pts[^1]:0.##}");
            }
        }
    }
}
static partial class Program
{
    static void DrillCompProbe()
    {
        using var __poly = PolyhedralPath();
        var w = new List<string>();
        var body = new List<Parametric3D.Kernel.Topology.TSolid> { Box(0, 0, -10, 60, 40, 20) };
        Parametric3D.Kernel.Ops.BooleanOp.Unite(body, Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(50, 30, 10), 4, 6), w);
        var (x, y, r, h) = (44.4, 9.1, 4.2, 30.0);
        var at = new Vector3d(x, y, 10.05);
        var straight = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(at - Vector3d.UnitZ * h, r, h);
        var b = new List<Parametric3D.Kernel.Topology.TSolid>(body); Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, straight, w);
        foreach (var f in b[0].Faces.Where(f => f.Loops.Count > 1))
        {
            Console.WriteLine($"DC face {f.Id} {f.Tag} {f.Surface.GetType().Name} rev {f.Reversed} loops {f.Loops.Count}");
            foreach (var l in f.Loops)
            {
                var pts = new List<Vector3d>(); foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                Vector3d acc = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) acc += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                var lo = pts.Aggregate(new Vector3d(1e9), Vector3d.ComponentMin); var hi = pts.Aggregate(new Vector3d(-1e9), Vector3d.ComponentMax);
                Console.WriteLine($"   loop n {l.CoEdges.Count} area2 {acc:0.###} box {lo:0.##}..{hi:0.##} curves {string.Join(",", l.CoEdges.Select(c => c.Edge.Curve.GetType().Name[..3]).Distinct())}");
            }
        }
    }
}
