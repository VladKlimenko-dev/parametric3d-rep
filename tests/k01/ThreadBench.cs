using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Threads;

static partial class Program
{
    static void ThreadBench(string[] args)
    {
        double length = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 2;
        string mode = args.Length > 1 ? args[1] : "cut";
        if (Environment.GetEnvironmentVariable("P3D_THREAD_DEBUG") == "1")
            DirectRound.Debug = m => Console.WriteLine(m);
        using var progress = Environment.GetEnvironmentVariable("P3D_THREAD_DEBUG") == "1"
            ? new System.Threading.Timer(_ => Console.Error.WriteLine("PROGRESS\n" + Parametric3D.Kernel.Diagnostics.Prof.Report()), null, 10000, 10000)
            : null;
        var spec = ThreadDb.All.First(t => t.Designation == "M6" && t.Series == "coarse");
        var watch = Stopwatch.StartNew();
        if (mode == "tools")
        {
            var tools = CosmeticThreadFeature.CutTools(spec, false, Vector3d.Zero, Vector3d.UnitZ,
                Vector3d.UnitX, length, false, false);
            Log(new { phase = "tools", length, ms = watch.Elapsed.TotalMilliseconds,
                faces = tools.Sum(t => t.Faces.Count()),
                surfaces = tools.SelectMany(t => t.Faces).Select(f => GeometryAudit.AuthoritativeSurface(f).GetType().Name).Distinct().ToArray() });
            return;
        }
        var body = PrimitiveOp.Cylinder(Vector3d.Zero, spec.DMajor / 2, length);
        var context = new BuildContext { Part = new Part("thread bench"), Parameters = new ParameterTable() };
        context.Bodies.Add(body);
        var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = length, ShowPreview = mode == "preview" };
        feature.OnFaces.Add(new Vector3d(spec.DMajor / 2, 0, length / 2));
        using var scope = new KernelContext(KernelSettings.Capture() with { Preview = mode == "preview" }).Enter();
        long cellsBefore = DirectRound.ExactSweeps;
        watch.Restart();
        feature.Execute(context);
        double firstMs = watch.Elapsed.TotalMilliseconds;
        var samples = new List<double>();
        if (mode == "preview")
            for (int i = 0; i < 10; i++) { watch.Restart(); feature.Execute(context); samples.Add(watch.Elapsed.TotalMilliseconds); }
        Log(new { phase = mode, length, ms = firstMs, warmMedianMs = samples.Count > 0 ? samples.Order().ElementAt(5) : (double?)null,
            route = feature.LastRoute, lines = feature.Lines?.Count,
            cellsBuilt = DirectRound.ExactSweeps - cellsBefore,
            unchanged = context.Bodies.Count == 1 && ReferenceEquals(body, context.Bodies[0]),
            faces = context.Bodies.Sum(t => t.Faces.Count()),
            surfaces = context.Bodies.SelectMany(t => t.Faces).Select(f => GeometryAudit.AuthoritativeSurface(f).GetType().Name).Distinct().ToArray(),
            warnings = context.Warnings });
    }
}
