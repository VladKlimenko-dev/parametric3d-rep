using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>KRN-06: брусок со скруглёнными вертикальными рёбрами R, затем скругление всего верхнего контура r (касательная
    /// цепочка: отрезки плоскость/плоскость и четверти дуг плоскость/цилиндр). Объём — по Паппу.</summary>
    static void TanChainProbe()
    {
        DirectRound.Debug = m => { if (m.StartsWith("declined")) Console.WriteLine("TC DBG " + m); };
        foreach (var (R, r) in new[] { (5.0, 2.0), (5.0, 5.0), (8.0, 3.0) })
        {
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
            var vert = bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList();
            var w = new List<string>();
            FilletOp.RoundEdges(bl, vert, R, w);
            double v1 = AnalyticVolume.Of(bl[0]) ?? double.NaN;
            var top = bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9).Select(EdgeRef.Of).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            w.Clear();
            FilletOp.RoundEdges(bl, top, r, w);
            sw.Stop();
            var b = bl[0];
            double k = 1 - Math.PI / 4, c = (10 - 3 * Math.PI) / (12 - 3 * Math.PI);
            double want = v1 - k * r * r * (2 * (60 - 2 * R) + 2 * (40 - 2 * R) + 2 * Math.PI * (R - c * r));
            double? v = AnalyticVolume.Of(b);
            Console.WriteLine($"TC R{R} r{r}: top edges {top.Count} V1 {v1:F4} V {(v ?? double.NaN):F6} want {want:F6} {b.Guarantee} {SolidValidator.Validate(b).Summary()} {sw.ElapsedMilliseconds}ms | {string.Join(" | ", w.Take(3))}");
        }
    }
}
