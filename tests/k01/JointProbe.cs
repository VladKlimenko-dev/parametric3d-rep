using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>k144 (замер): у цепочки последней операции файла — стыки звеньев: грани у вершины, угол между гранями третьего ребра.</summary>
    static void JointProbe(string file)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        var last = p.Features[^1]; p.RemoveFeature(last); p.Rebuild(pars);
        var body = p.Bodies.Single();
        var refs = last switch { RoundFeature rf => rf.Edges, ChamferFeature cf => cf.Edges, _ => new List<EdgeRef>() };
        var es = refs.Select(r => Parametric3D.Kernel.Ops.FilletOp.Resolve(new List<TSolid> { body }, r, out _)).OfType<TEdge>().ToList();
        Console.WriteLine($"op {last.GetType().Name} edges {es.Count}");
        Vector3d Nf(TFace f, Vector3d q) { var s = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface; var n = s.NormalAt(s.ProjectToUV(q)).Normalized(); return f.Reversed ? -n : n; }
        foreach (var e in es)
            Console.WriteLine($" edge {e.Curve.GetType().Name} {e.V0.P:0.###}->{e.V1.P:0.###} faces {string.Join(" | ", e.CoEdges.Select(c => c.Loop?.Face?.Tag + ":" + (GeometryAudit.AuthoritativeSurface(c.Loop!.Face!)?.GetType().Name)))}");
        var verts = es.SelectMany(e => new[] { e.V0.P, e.V1.P }).ToList();
        foreach (var v in verts.Distinct())
        {
            int inChain = es.Count(e => (e.V0.P - v).Length < 1e-7 || (e.V1.P - v).Length < 1e-7);
            var others = body.Edges.Where(x => !es.Contains(x) && ((x.V0.P - v).Length < 1e-7 || (x.V1.P - v).Length < 1e-7)).ToList();
            Console.WriteLine($" vertex {v:0.###} chain-edges {inChain} other {others.Count}");
            foreach (var o in others)
            {
                var fs = o.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                var mid = o.Curve.PointAt((o.Curve.T0 + o.Curve.T1) / 2);
                string ang = fs.Count == 2 ? $"{Math.Acos(Math.Clamp(Vector3d.Dot(Nf(fs[0], v), Nf(fs[1], v)), -1, 1)) * 180 / Math.PI:0.####}° at vertex, {Math.Acos(Math.Clamp(Vector3d.Dot(Nf(fs[0], mid), Nf(fs[1], mid)), -1, 1)) * 180 / Math.PI:0.####}° mid" : "?";
                Console.WriteLine($"   other {o.Curve.GetType().Name} len {(o.V1.P - o.V0.P).Length:0.###} faces {string.Join(" | ", fs.Select(f => f.Tag))} crease {ang}");
            }
        }
    }
}
