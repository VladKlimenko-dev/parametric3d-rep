using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// Разбор: скругление ребра, конец которого лежит на прежнем скруглении (сначала нижнее ребро бруска R1, затем
    /// вертикальное ребро в том же углу R2). Перебор R1, R2: проходит ли, каким маршрутом, объём против ожидаемого.
    /// </summary>
    static void SeqRoundProbe()
    {
        foreach (var (r1, r2) in new[] { (2.0, 1.0), (2.0, 2.0), (2.0, 3.0), (1.0, 4.0), (3.0, 1.5) })
            foreach (bool convexFirst in new[] { true })
            {
                var w = new List<string>();
                var bl = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) };
                TEdge Find(Func<TEdge, bool> p) => bl[0].Edges.First(p);
                // нижнее ребро вдоль X у y=30, z=0
                var e1 = Find(e => Math.Abs(e.V0.P.Y - 30) < 1e-9 && Math.Abs(e.V1.P.Y - 30) < 1e-9 && Math.Abs(e.V0.P.Z) < 1e-9 && Math.Abs(e.V1.P.Z) < 1e-9);
                FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(e1) }, r1, w);
                double v1 = AnalyticVolume.Of(bl[0]) ?? double.NaN;
                // вертикальное ребро x=40, y=30
                var e2 = Find(e => Math.Abs(e.V0.P.X - 40) < 1e-9 && Math.Abs(e.V1.P.X - 40) < 1e-9 && Math.Abs(e.V0.P.Y - 30) < 1e-9 && Math.Abs(e.V1.P.Y - 30) < 1e-9);
                string res;
                try
                {
                    var w2 = new List<string>();
                    FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(e2) }, r2, w2);
                    var v = SolidValidator.Validate(bl[0]);
                    res = $"ok V {AnalyticVolume.Of(bl[0]):G9} removed {v1 - (AnalyticVolume.Of(bl[0]) ?? double.NaN):G6} {bl[0].Guarantee} {v.Summary()} | {string.Join(" / ", w2.Take(2))}";
                }
                catch (Exception ex) { res = "FAIL " + ex.Message.Split('\n')[0][..Math.Min(220, ex.Message.Split('\n')[0].Length)]; }
                Console.WriteLine($"SEQROUND R1 {r1} R2 {r2}: {res}");
            }
    }
}
