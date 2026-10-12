using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>g.04, разбор: малое пересечение конуса и шара (серия 6, d6_0059), эталон — интеграл площадей сечений (scipy) 1.333272779813495.</summary>
    static void G04ConeSphere()
    {
        var cone = PrimitiveOp.Cone(Vector3d.Zero, 2.705859196, 0.5794894802746886, 11.432783677280014);
        var sph = PrimitiveOp.Sphere(new Vector3d(14.402752196419272, 0, 0.21567902364975844), 12.298942291);
        BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("  route: " + m);
        double ch = double.Parse(Environment.GetEnvironmentVariable("CH") ?? "0.02", System.Globalization.CultureInfo.InvariantCulture);
        using var _k = new KernelContext(KernelSettings.Capture() with { Chord = ch, ChordAngle = Math.Min(7.5, ch * 100) * Math.PI / 180 }).Enter();
        var r = BooleanOp.Evaluate(new List<TSolid> { cone }, sph, BooleanOp.BooleanKind.Intersect, OperationPolicy.Strict);
        BooleanOp.AnalyticRouteDebug = null;
        Console.WriteLine($"{r.Describe()} accuracy {r.Accuracy}");
        foreach (var b in r.Bodies)
        {
            Console.WriteLine($"  convention {b.TopologyConvention}; normalize report: {TopologyConventions.Normalize(TopologyClone.Deep(b))}");
            foreach (var f in b.Faces.Where(x => GeometryAudit.AuthoritativeSurface(x) is Parametric3D.Kernel.Surfaces.RevolvedSurface rv0 && rv0.Base is Parametric3D.Kernel.Curves.ArcCurve3))
            {
                var t = typeof(AnalyticVolume);
                var loopsM = t.GetMethod("Loops", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                var loops = ((IEnumerable<List<Vector3d>>)loopsM.Invoke(null, new object[] { f })!).ToList();
                var c = new Vector3d(14.402752196419272, 0, 0.21567902364975844); double R = 12.298942291;
                foreach (var pts in loops)
                {
                    double gap = (pts[0] - pts[^1]).Length; double maxSeg = 0; double offS = 0;
                    for (int i = 0; i + 1 < pts.Count; i++) maxSeg = Math.Max(maxSeg, (pts[i + 1] - pts[i]).Length);
                    foreach (var q in pts) offS = Math.Max(offS, Math.Abs((q - c).Length - R));
                    File.WriteAllLines("/home/claude/out/sphloop.txt", pts.Select(q => $"{q.X:R} {q.Y:R} {q.Z:R}"));
                    Console.WriteLine($"   sph loop pts {pts.Count} closure gap {gap:E2} max seg {maxSeg:E2} max off-sphere {offS:E2}");
                }
            }
            var m = AnalyticMeasure.Volume(b);
            Console.WriteLine($"  volume {m} exact 1.333272779813495 err {m.Value - 1.333272779813495:E3}");
            foreach (var f in b.Faces)
            {
                double mi = f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx ? fsx.Facets.ToList().Sum(t => Vector3d.Dot(t.Item1, Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1)) / 2) : double.NaN;
                Console.WriteLine($"   face {f.Id} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {string.Join("/", f.Loops.Select(l => l.CoEdges.Count))} curves {string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve.GetType().Name.Replace("Curve3", "")))} int {AnalyticVolume.FaceIntegral(f):R} mesh {mi:R} area {AnalyticVolume.FaceArea(f):R} meshArea {(f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsa ? fsa.Facets.ToList().Sum(t => Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length / 2) : double.NaN):R} normalInt {AnalyticVolume.NormalIntegral(f)}");
                if (Environment.GetEnvironmentVariable("DUMPLOOPS") == "1")
                    foreach (var l in f.Loops) foreach (var ce in l.CoEdges) { var c = ce.Edge.Curve; Console.WriteLine($"     ce rev {ce.Reversed} {c.GetType().Name} T {c.T0}..{c.T1} p0 {c.PointAt(c.T0)} p1 {c.PointAt(c.T1)} V0 {ce.Edge.V0.P} V1 {ce.Edge.V1.P}"); }
            }
        }
    }
}
