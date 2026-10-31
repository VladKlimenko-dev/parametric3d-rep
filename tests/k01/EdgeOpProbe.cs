using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// k144 (замер): одна операция на ребре файла — edgeop file keep(0,2) x1,y1,z1,x2,y2,z2 R|C size [save.p3d]. Печатает путь
    /// (прямой построитель или резец), причину отказа прямого, объём, годность и время.
    /// </summary>
    static void EdgeOpProbe(string[] a)
    {
        var text = File.ReadAllText(a[1]);
        var keep = a[2].Split(',').Select(int.Parse).ToArray();
        var c = a[3].Split(',').Select(double.Parse).ToArray();
        bool round = a[4] == "R"; double size = double.Parse(a[5]);
        var (p, pars) = DocumentFile.Load(text);
        foreach (var f0 in p.Features.Where((f, i) => !keep.Contains(i)).ToList()) p.RemoveFeature(f0);
        p.Rebuild(pars);
        double v0 = p.Volume;
        static double MeshVol(TSolid t) => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(t));
        double m0 = MeshVol(p.Bodies.Single());
        var A = new Vector3d(c[0], c[1], c[2]); var B = new Vector3d(c[3], c[4], c[5]);
        var e = p.Bodies.SelectMany(b => b.Edges).OrderBy(q => Math.Min((q.V0.P - A).Length + (q.V1.P - B).Length, (q.V0.P - B).Length + (q.V1.P - A).Length)).First();
        Console.WriteLine($"edge {e.Curve.GetType().Name} {e.V0.P:0.###}->{e.V1.P:0.###} faces {string.Join(" | ", e.CoEdges.Select(x => x.Loop?.Face?.Tag + ":" + GeometryAudit.AuthoritativeSurface(x.Loop!.Face!)?.GetType().Name))}");
        Feature f = round ? new RoundFeature("R") { Radius = new ValueRef(size) } : new ChamferFeature("C") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
        if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(e)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(e));
        p.AddFeature(f);
        var prev = Parametric3D.Kernel.Ops.DirectRound.Debug;
        if (Environment.GetEnvironmentVariable("DRDBG") != "1") Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined") || m.Contains("declined:")) Console.WriteLine("DR " + m); };
        else Parametric3D.Kernel.Ops.DirectRound.Debug = m => Console.WriteLine("DR " + m);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(null);
        Parametric3D.Kernel.Ops.DirectRound.Debug = prev;
        Console.WriteLine($"time {sw.ElapsedMilliseconds} ms error {f.Error?.Split('\n').Last()}");
        foreach (var w in p.Warnings.Where(x => x.Contains("Round") || x.Contains("Chamfer"))) Console.WriteLine("WARN " + w);
        if (f.Error == null)
        {
            var b = p.Bodies.Single();
            Console.WriteLine($"guarantee {b.Guarantee} valid {SolidValidator.Validate(b).IsValid} volume {p.Volume:0.####} removed {v0 - p.Volume:0.####} faces {b.Faces.Count()} meshRemoved {m0 - MeshVol(b):0.####}");
            if (a.Length > 6) File.WriteAllText(a[6], DocumentFile.Save(p));
        }
    }
}
