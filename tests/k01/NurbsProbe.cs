using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>KRN-07 (07.8) замер: булевы с телом, у которого грань — NURBS (четверть цилиндра R10 h20 из чужого STEP).</summary>
    static void NurbsProbe()
    {
        BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   ROUTE " + m);
        var w0 = new List<string>();
        var src = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(File.ReadAllText(Fixture("quarter_cyl.step")), w0)[0];
        var bb = src.Vertices.Aggregate((lo: new Vector3d(1e9), hi: new Vector3d(-1e9)), (a, v) => (Vector3d.ComponentMin(a.lo, v.P), Vector3d.ComponentMax(a.hi, v.P)));
        double V0 = Math.PI * 100 * 20 / 4;
        Console.WriteLine($"NURBS body box {bb.lo:0.##}..{bb.hi:0.##} guarantee {src.Guarantee} faces {src.Faces.Count()}");
        foreach (var (name, tool, kind, want) in new (string, TSolid, int, double)[]
        {
            ("cut top quarter (box z>15)", Box(-20, -20, 15, 40, 40, 20), 0, V0 * 15 / 20),
            ("cut slab x<3", Box(-20, -20, -5, 23, 40, 40), 0, double.NaN),
            ("unite box below", Box(0, 0, -5, 10, 10, 5), 1, V0 + 500),
            ("cut hole r2 along z", PrimitiveOp.Cylinder(new Vector3d(5, 5, -5), 2, 40), 0, V0 - Math.PI * 4 * 20),
        })
        {
            var bl = new List<TSolid> { TopologyClone.Deep(src) }; var w = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (kind == 0) BooleanOp.Subtract(bl, tool, w); else BooleanOp.Unite(bl, tool, w);
            sw.Stop();
            var b = bl.FirstOrDefault();
            double? va = b == null ? null : AnalyticVolume.Of(b);
            double vm = b == null ? double.NaN : Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
            if (b != null) foreach (var f in b.Faces) Console.WriteLine($"   face {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} area {AnalyticVolume.FaceArea(f):R} edges {string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve.GetType().Name))}");
            Console.WriteLine($"NP {name}: bodies {bl.Count} {b?.Guarantee} Va {va:F6} Vmesh {vm:F4} want {want:F6} {sw.ElapsedMilliseconds}ms {(b == null ? "" : SolidValidator.Validate(b).Summary())} | {string.Join(" | ", w.Take(3))}");
        }
    }
}
