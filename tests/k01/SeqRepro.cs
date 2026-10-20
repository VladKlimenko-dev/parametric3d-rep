using Parametric3D.Model;

static partial class Program
{
    /// <summary>Загрузить деталь (.p3d) и перестроить: ошибки операций и годность тел (воспроизведение сценариев приложения).</summary>
    static void LoadRebuild(string path)
    {
        var (part, pars) = DocumentFile.Load(File.ReadAllText(path));
        if (Environment.GetEnvironmentVariable("P3D_ROUTEDBG") == "1") Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   route: " + m);
        if (Environment.GetEnvironmentVariable("P3D_DRDBG") == "1") Parametric3D.Kernel.Ops.DirectRound.Debug = m => Console.WriteLine("   dr: " + m);
        if (Environment.GetEnvironmentVariable("P3D_SPLITDBG") == "1") Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => { if (m.StartsWith("FAIL") || m.StartsWith("DEAD") || m.StartsWith("  node")) Console.WriteLine("   split: " + m); };
        if (Environment.GetEnvironmentVariable("P3D_SEQSTAGE") is { Length: > 0 } st && int.TryParse(st, out int stg)) part.StageIndex = stg;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        part.Rebuild(null);
        if (Environment.GetEnvironmentVariable("P3D_SEQTAGS") == "1")
            foreach (var b in part.Bodies) foreach (var f in b.Faces)
                {
                    string extra = "";
                    if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs)
                    {
                        double ar = 0; var c = OpenTK.Mathematics.Vector3d.Zero; var nn = OpenTK.Mathematics.Vector3d.Zero;
                        foreach (var (A, B, C) in fs.Facets) { var cr = OpenTK.Mathematics.Vector3d.Cross(B - A, C - A); double a = cr.Length / 2; ar += a; c += (A + B + C) / 3 * a; nn += cr; }
                        if (ar > 0) c /= ar;
                        extra = $" src={fs.Source?.GetType().Name} A={ar:0.###} c=({c.X:0.##},{c.Y:0.##},{c.Z:0.##}) n=({nn.Normalized().X:0.##},{nn.Normalized().Y:0.##},{nn.Normalized().Z:0.##})";
                    }
                    Console.WriteLine($"  tag {f.Tag} {f.Surface.GetType().Name}{extra}");
                }
        if (part.InInsertMode)
            foreach (var b in part.Bodies)
                foreach (var e in b.Edges.Where(e => (e.V0.P - e.V1.P).Length < 0.5).Take(20))
                    Console.WriteLine($"  short edge {(e.V0.P - e.V1.P).Length:G4} {e.Curve?.GetType().Name} {e.V0.P:0.####} -> {e.V1.P:0.####} faces {string.Join(" | ", b.Faces.Where(f => f.Edges.Contains(e)).Select(f => f.Tag + ":" + f.Surface.GetType().Name))}");
        Console.WriteLine($"rebuild {sw.ElapsedMilliseconds} ms, bodies {part.Bodies.Count}, volume {part.Volume:R}");
        int i = 0;
        foreach (var f in part.Features)
            Console.WriteLine($"  {i++,2} {f.GetType().Name,-22} {f.Name,-28} {f.Summary} {(f.Error == null ? "ok" : "ERROR: " + f.Error)}");
        if (Environment.GetEnvironmentVariable("P3D_SEQEDGES") is { Length: > 0 } fe && int.TryParse(fe, out int fi))
            foreach (var e in (part.Features[fi] as Parametric3D.Model.Features.RoundFeature)?.Edges ?? (part.Features[fi] as Parametric3D.Model.Features.ChamferFeature)?.Edges ?? new()) Console.WriteLine($"  ref {e.Start:0.###} -> {e.End:0.###} mid {e.Mid:0.###} faces {e.FaceA} | {e.FaceB}");
        // согласие сетки показа с телом: треугольники с номером грани лежат на её поверхности
        if (part.Mesh is { } md)
        {
            var faces = part.Bodies.SelectMany(b => b.Faces).ToDictionary(f => f.Id);
            var worst = new Dictionary<uint, double>();
            foreach (var v in md.Vertices)
            {
                if (!faces.TryGetValue(v.FaceId, out var f)) { worst[v.FaceId] = double.PositiveInfinity; continue; }
                var q = new OpenTK.Mathematics.Vector3d(v.P.X, v.P.Y, v.P.Z);
                var src = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) ?? f.Surface;
                double d = (src.PointAt(src.ProjectToUV(q)) - q).Length;
                if (!worst.TryGetValue(v.FaceId, out var w0) || d > w0) worst[v.FaceId] = d;
            }
            foreach (var (id, d) in worst.Where(kv => kv.Value > 0.05).Take(10))
                Console.WriteLine($"  MESH face {id} ({(faces.TryGetValue(id, out var ff) ? ff.Tag : "no such face")}): vertex off the face by {d:G4}");
            Console.WriteLine($"  mesh faces {worst.Count}, body faces {faces.Count}, off {worst.Count(kv => kv.Value > 0.05)}");
        }
        if (Environment.GetEnvironmentVariable("P3D_SEQTOOL") is { Length: > 0 } tf && int.TryParse(tf, out int ti) && part.Features[ti] is Parametric3D.Model.Features.ExtrudeFeature xf)
        {
            var sk = part.Sketches.First(k => k.Id == xf.SketchId);
            var prof = sk.ToProfile();
            var tool = Parametric3D.Kernel.Ops.ExtrudeOp.Build(prof, sk.Plane.W.Normalized() * xf.Side1.Distance.Value);
            Console.WriteLine($"  tool volume (exact) {Parametric3D.Kernel.Topology.AnalyticVolume.Of(tool):R} op {OpVolume(tool):R} sketch area {prof.Loops.Sum(l => l.Area):R}");
        }
        foreach (var w in part.Warnings.Take(40)) Console.WriteLine("  warn: " + w);
        foreach (var b in part.Bodies) Console.WriteLine($"  body: {Parametric3D.Kernel.Topology.SolidValidator.Validate(b).Summary()} {b.Guarantee}");
    }
}
