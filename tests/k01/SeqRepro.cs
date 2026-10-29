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
        if (Environment.GetEnvironmentVariable("P3D_SEQOBJ") is { Length: > 0 } sobj) WriteObj(sobj, part.Bodies);
        foreach (var w in part.Warnings) Console.WriteLine("  W " + w);
        Console.WriteLine("  guarantee " + string.Join(",", part.Bodies.Select(b => b.Guarantee)));
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

static partial class Program
{
    /// <summary>Разбор: каждая операция файла по очереди (откат на k) — время, гарантия, годность, объём.</summary>
    static void StageEach(string path)
    {
        var (part0, _) = DocumentFile.Load(File.ReadAllText(path));
        int n = part0.Features.Count;
        for (int k = 1; k <= n; k++)
        {
            var (part, pars) = DocumentFile.Load(File.ReadAllText(path));
            part.StageIndex = k;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            part.Rebuild(pars);
            var f = part.Features[k - 1];
            string g = string.Join(",", part.Bodies.Select(b => b.Guarantee));
            bool valid = part.Bodies.All(b => Parametric3D.Kernel.Topology.SolidValidator.Validate(b).IsValid);
            int faceted = part.Bodies.Sum(b => b.Faces.Count(x => x.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface));
            Console.WriteLine($"STAGE {k} {f.GetType().Name} {f.Name} {sw.ElapsedMilliseconds} ms g={g} valid={valid} faceted={faceted} V={part.Volume:0.###} {(f.Error == null ? "ok" : "ERROR " + f.Error.Replace('\n', '#'))}");
            foreach (var w in part.Warnings.Where(x => x.Contains("downgrade") || x.Contains("unpatched") || x.Contains("polyhedral") || x.Contains("faceted"))) Console.WriteLine("   W " + w);
            if (Environment.GetEnvironmentVariable("P3D_STAGEFACES") == "1")
                foreach (var b in part.Bodies) foreach (var fz in b.Faces)
                {
                    var pts = new List<OpenTK.Mathematics.Vector3d>(); foreach (var ce in fz.Loops.SelectMany(l => l.CoEdges)) ce.Tessellate(pts, true);
                    if (pts.Count == 0) continue;
                    var lo = pts.Aggregate(OpenTK.Mathematics.Vector3d.ComponentMin); var hi = pts.Aggregate(OpenTK.Mathematics.Vector3d.ComponentMax);
                    Console.WriteLine($"   FACE {fz.Id} {fz.Tag} {fz.Surface.GetType().Name}/{Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(fz)?.GetType().Name} loops {fz.Loops.Count} box ({lo.X:0.#},{lo.Y:0.#},{lo.Z:0.#})..({hi.X:0.#},{hi.Y:0.#},{hi.Z:0.#})");
                }
            if (Environment.GetEnvironmentVariable("P3D_STAGESTOP") is { Length: > 0 } stop && int.Parse(stop) == k) return;
        }
    }
}

static partial class Program
{
    /// <summary>Разбор: проекция точки на NURBS грани у точки near — ProjectToUV и перебор по сетке.</summary>
    static void ProjProbe(string path, double[] near, double[] pt)
    {
        var (part, pars) = DocumentFile.Load(File.ReadAllText(path)); part.Rebuild(pars);
        var N = new OpenTK.Mathematics.Vector3d(near[0], near[1], near[2]); var P = new OpenTK.Mathematics.Vector3d(pt[0], pt[1], pt[2]);
        foreach (var f in part.Bodies.SelectMany(b => b.Faces))
        {
            if (Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) is not Parametric3D.Kernel.Surfaces.NurbsSurface ns) continue;
            var uvN = ns.ProjectToUV(N); double dn = (ns.PointAt(uvN) - N).Length;
            if (dn > 3) continue;
            var uv = ns.ProjectToUV(P); var q = ns.PointAt(uv); var nrm = ns.NormalAt(uv);
            double best = double.MaxValue; OpenTK.Mathematics.Vector2d buv = default;
            for (int i = 0; i <= 400; i++) for (int j = 0; j <= 400; j++) { var w = new OpenTK.Mathematics.Vector2d(i / 400.0, j / 400.0); double d = (ns.PointAt(w) - P).Length; if (d < best) { best = d; buv = w; } }
            Console.WriteLine($"face {f.Tag} near-dist {dn:G3}: proj uv {uv} q {q} dist {(q - P).Length:0.######} angle {Math.Acos(Math.Clamp(Math.Abs(OpenTK.Mathematics.Vector3d.Dot((P - q).Normalized(), nrm)), -1, 1)) * 180 / Math.PI:0.###}°; brute uv {buv} dist {best:0.######} pt {ns.PointAt(buv)}");
        }
    }
}
