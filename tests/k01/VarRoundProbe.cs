using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void UneqProbe()
    {
        Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined")) Console.WriteLine("UQ DBG " + m); };
        foreach (var corner in new[] { false, true })
        {
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
            var refs = corner ? bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && ((e.V0.P - new Vector3d(60, 40, 20)).Length < 1e-9 || (e.V1.P - new Vector3d(60, 40, 20)).Length < 1e-9)).Select(EdgeRef.Of).ToList()
                              : bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9).Select(EdgeRef.Of).ToList();
            var w = new List<string>();
            Console.WriteLine($"UQ corner {corner}: direct {DirectRound.TryChamfer(bl, refs, 2, 3, w)} V {AnalyticVolume.Of(bl[0])}");
        }
    }

    static void VarRoundProbe()
    {
        Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined")) Console.WriteLine("VR DBG " + m); };
        foreach (var (name, r0, r1, concave) in new[] { ("convex 2→4", 2.0, 4.0, false), ("convex 4→1", 4.0, 1.0, false), ("concave 2→4", 2.0, 4.0, true) })
        {
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
            TEdge e;
            if (!concave) e = bl[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.X - 60) < 1e-9 && Math.Abs(x.V1.P.X - 60) < 1e-9 && Math.Abs(x.V0.P.Z - 20) < 1e-9 && Math.Abs(x.V1.P.Z - 20) < 1e-9);
            else
            {
                BooleanOp.Subtract(bl, Box(30, -1, 10, 31, 42, 11), new List<string>());
                e = bl[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.X - 30) < 1e-9 && Math.Abs(x.V1.P.X - 30) < 1e-9 && Math.Abs(x.V0.P.Z - 10) < 1e-9 && Math.Abs(x.V1.P.Z - 10) < 1e-9);
            }
            var er = EdgeRef.Of(e); double v0 = AnalyticVolume.Of(bl[0]) ?? double.NaN;
            var w = new List<string>();
            FilletOp.RoundEdgesVariable(bl, new List<EdgeRef> { er }, r0, r1, w);
            var b = bl[0];
            Console.WriteLine($"VR {name}: start {er.Start:0.#} V {AnalyticVolume.Of(b):R} (before {v0:R}) {b.Guarantee} {SolidValidator.Validate(b).Summary()} | {string.Join(" | ", w.Take(3))}");
        }
    }
}
