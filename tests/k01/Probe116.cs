using OpenTK.Mathematics;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>k116: разбор файла пользователя — K01Checks probe116 файл вариант (none, stageN, meshinfo, cache, resave, sweep, mm, mmz, body, draft).</summary>
    static void Probe116(string path, string variant)
    {
        var (p, prm) = DocumentFile.Load(File.ReadAllText(path));
        var doc = new Document(); doc.Root.Add(p);
        Console.WriteLine($"cached {p.ShowsCached} rejected {DocumentFile.DisplayRejected}");
        if (variant == "cache") return;
        if (variant == "meshinfo")
        {
            if (p.Mesh == null || !p.ShowsCached && p.Bodies.Count == 0) p.Rebuild(prm);
            var md = p.Mesh!;
            Console.WriteLine($"verts {md.Vertices.Length} tris {md.Indices.Length / 3} faces {md.FaceRanges.Count} edges {md.EdgePolylines.Count} points {md.Points.Count}");
            foreach (var (id, pts) in md.EdgePolylines)
            {
                double len = 0; for (int i = 1; i < pts.Length; i++) len += (pts[i] - pts[i - 1]).Length;
                Console.WriteLine($"  edge {id}: n {pts.Length} len {len:0.##} from {pts[0]} to {pts[^1]}");
            }
            foreach (var (id, q) in md.Points) Console.WriteLine($"  pt {id} {q}");
            foreach (var (fid, r) in md.FaceRanges.OrderBy(k => k.Key))
            {
                var vs = md.Vertices.Skip(r.VertexStart).Take(r.VertexCount).ToList();
                if (vs.Count == 0) continue;
                var lo = vs.Aggregate(new Vector3(1e9f), (m, v) => Vector3.ComponentMin(m, v.P)); var hi = vs.Aggregate(new Vector3(-1e9f), (m, v) => Vector3.ComponentMax(m, v.P));
                var avgN = vs.Aggregate(Vector3.Zero, (m, v) => m + v.N) / vs.Count;
                var face = p.Bodies.SelectMany(b => b.Faces).FirstOrDefault(f => f.Id == fid);
                Console.WriteLine($"  face {fid} tris {r.IndexCount / 3} box {lo} .. {hi} avgN {avgN} surf {face?.Surface.GetType().Name}");
            }
            { double cs = 0; int dark = 0; foreach (var v in md.Vertices) cs += v.N.X * 1.1 + v.N.Y * 1.3 + v.N.Z * 1.7;
              for (int t = 0; t + 2 < md.Indices.Length; t += 3) { var a = md.Vertices[md.Indices[t]]; var b = md.Vertices[md.Indices[t + 1]]; var c = md.Vertices[md.Indices[t + 2]]; var g = Vector3.Cross(b.P - a.P, c.P - a.P); if (g.LengthSquared > 0) { g = g.Normalized(); foreach (var q in new[] { a, b, c }) if (Vector3.Dot(q.N, g) < 0.5) dark++; } }
              Console.WriteLine($"normal checksum {cs:0.####} vertex normals >60deg off facet {dark}"); }
            Console.WriteLine($"floatdefects {Parametric3D.Kernel.Tess.MeshData.FloatDefects(md)}");
            // открытые рёбра сетки (по позициям): трещины
            var dir = new Dictionary<(Vector3, Vector3), int>();
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
            {
                var a = md.Vertices[md.Indices[t]].P; var b = md.Vertices[md.Indices[t + 1]].P; var c = md.Vertices[md.Indices[t + 2]].P;
                foreach (var e in new[] { (a, b), (b, c), (c, a) }) dir[e] = dir.GetValueOrDefault(e) + 1;
            }
            var open = dir.Keys.Where(e => !dir.ContainsKey((e.Item2, e.Item1))).ToList();
            // разрывы нормалей внутри грани: одна позиция, одна грань — разные нормали
            var byPos = new Dictionary<(Vector3, uint), List<Vector3>>();
            foreach (var v in md.Vertices) { var k = (v.P, v.FaceId); if (!byPos.TryGetValue(k, out var l)) byPos[k] = l = new(); l.Add(v.N); }
            var perFace = new Dictionary<uint, (int n, double worst)>();
            foreach (var ((pp, fid), ns) in byPos)
            {
                double worst = 0; foreach (var a in ns) foreach (var b in ns) worst = Math.Max(worst, Vector3.CalculateAngle(a, b) * 180 / Math.PI);
                if (worst > 3) { var c = perFace.GetValueOrDefault(fid); perFace[fid] = (c.n + 1, Math.Max(c.worst, worst)); }
            }
            foreach (var kv in perFace.OrderByDescending(k => k.Value.n).Take(10)) Console.WriteLine($"   face {kv.Key}: {kv.Value.n} split-normal points, worst {kv.Value.worst:0.#}°");
            // тонкие треугольники
            int slivers = 0; double minAng = 180;
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
            {
                var a = md.Vertices[md.Indices[t]].P; var b = md.Vertices[md.Indices[t + 1]].P; var c = md.Vertices[md.Indices[t + 2]].P;
                var nn = Vector3.Cross(b - a, c - a); var g = nn.Length > 0 ? nn.Normalized() : Vector3.Zero;
                for (int k = 0; k < 3; k++) { var vn = md.Vertices[md.Indices[t + k]].N; double ang = Vector3.CalculateAngle(g, vn) * 180 / Math.PI; if (ang > 60) { slivers++; if (slivers <= 12) Console.WriteLine($"   bad tri face {md.Vertices[md.Indices[t]].FaceId} {a} {b} {c} area {nn.Length / 2:0.####} ang {ang:0}"); break; } }
            }
            Console.WriteLine($"triangles whose vertex normal deviates >60° from facet: {slivers}");
            {
                var flippedBy = new Dictionary<uint, int>(); int flipped = 0; double flipArea = 0;
                for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                {
                    var a = md.Vertices[md.Indices[t]]; var b = md.Vertices[md.Indices[t + 1]]; var c = md.Vertices[md.Indices[t + 2]];
                    var g = Vector3.Cross(b.P - a.P, c.P - a.P);
                    if (g.LengthSquared == 0) continue;
                    if (Vector3.Dot(g, a.N + b.N + c.N) < 0) { flipped++; flipArea += g.Length / 2; flippedBy[a.FaceId] = flippedBy.GetValueOrDefault(a.FaceId) + 1; }
                }
                Console.WriteLine($"flipped triangles (winding against normals): {flipped}, area {flipArea:G4}; by face {string.Join(", ", flippedBy.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}:{k.Value}"))}");
            }
            Console.WriteLine($"open mesh edges {open.Count} total len {open.Sum(e => (e.Item1 - e.Item2).Length):0.#}");
            foreach (var e in open.OrderByDescending(e => (e.Item1 - e.Item2).Length).Take(15)) Console.WriteLine($"   open {e.Item1} -> {e.Item2} len {(e.Item1 - e.Item2).Length:0.##}");
            return;
        }
        if (Environment.GetEnvironmentVariable("P3D_OPENDUMP") == "1")
            Parametric3D.Kernel.Ops.BooleanOp.GeneralInvalidProbe = r =>
            {
                foreach (var f in r.Faces) foreach (var l in f.Loops) foreach (var ce in l.CoEdges)
                    if (ce.Edge.CoEdges.Count == 1) Console.WriteLine($"OPEN edge {ce.Edge.Id} {ce.Edge.Curve.GetType().Name} {ce.Edge.V0.P:0.####} -> {ce.Edge.V1.P:0.####} face {f.Tag} {f.Surface.GetType().Name}");
                foreach (var f in r.Faces) foreach (var l in f.Loops)
                {
                    var cs = l.CoEdges.ToList();
                    for (int i = 0; i < cs.Count; i++) { var a = cs[i].End.P; var b = cs[(i + 1) % cs.Count].Start.P; if ((a - b).Length > 1e-6) Console.WriteLine($"GAP face {f.Tag} {a:0.####} .. {b:0.####} {(a - b).Length:G3}"); }
                }
            };
        if (variant.StartsWith("stage")) { int n0 = int.Parse(variant.Substring(5)); for (int i = n0; i < p.Features.Count; i++) p.Features[i].Suppressed = true; }
        if (Environment.GetEnvironmentVariable("P3D_PROF") == "1") Parametric3D.Kernel.Diagnostics.Prof.Enabled = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (Environment.GetEnvironmentVariable("P3D_ROUTEDBG") == "1") Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   ROUTE: " + m);
        p.Rebuild(prm);
        Console.WriteLine($"rebuild {sw.ElapsedMilliseconds} ms vol {p.Volume:0.###} bodies {p.Bodies.Count}");
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
        foreach (var f in p.Features) Console.WriteLine($"  {f.Key} {f.GetType().Name} {(p.LastFeatureMs.TryGetValue(f, out var ms) ? ms : -1):0} ms err={f.Error}");
        if (variant.StartsWith("stage"))
        {
            int n = int.Parse(variant.Substring(5));
            for (int i = n; i < p.Features.Count; i++) p.Features[i].Suppressed = true;
            if (Environment.GetEnvironmentVariable("P3D_ROUTEDBG") == "1") Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   ROUTE: " + m);
            foreach (var w in p.Warnings) Console.WriteLine("   W: " + w);
            foreach (var b in p.Bodies)
            {
                Console.WriteLine($"stage {n}: faces {b.Faces.Count()} edges {b.Edges.Count()}");
                Console.WriteLine($"   exact {b.Exact != null} auth {b.ExactAuthoritative} guarantee {b.Guarantee}");
                foreach (var g in b.Faces.GroupBy(f => f.Surface.GetType().Name + "/" + ((f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source?.GetType().Name ?? "-") + "/disp=" + ((f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.DisplayNormals != null))) Console.WriteLine($"   face {g.Key} x{g.Count()}");
                foreach (var g in b.Edges.GroupBy(e => e.Curve.GetType().Name)) Console.WriteLine($"   edge {g.Key} x{g.Count()} shortest {g.Min(e => (e.V0.P - e.V1.P).Length):0.####}");
            }
            return;
        }
        if (variant == "resave")
        {
            var t1 = DocumentFile.Save(p, prm, withDisplay: true);
            var t2 = DocumentFile.Save(p, prm, withDisplay: false);
            long r0 = DocumentFile.DisplayRejected;
            var (q, _) = DocumentFile.Load(t1);
            Console.WriteLine($"resave: cached {q.ShowsCached} rejected {DocumentFile.DisplayRejected - r0} dirty {p.IsDirty} mesh {p.Mesh?.Vertices.Length}");
            return;
        }
        if (variant == "none") return;
        var m = new MirrorFeature("M");
        if (variant == "sweep") { m.Sources.Add("51e5e5fd56"); m.At = new Vector3d(0, 0, -279.7175); m.Normal = Vector3d.UnitZ; }
        if (variant == "mm") { m.Sources.Add("a693ea6f6a"); m.At = Vector3d.Zero; m.Normal = Vector3d.UnitX; }
        if (variant == "mmz") { m.Sources.Add("f68ad4adc5"); m.At = new Vector3d(0, 0, -279.7175); m.Normal = Vector3d.UnitZ; }
        if (variant == "body") { m.At = new Vector3d(0, 0, -279.7175); m.Normal = Vector3d.UnitZ; }
        if (variant == "draft") { m.Sources.Add("4af2b180b5"); m.At = Vector3d.Zero; m.Normal = Vector3d.UnitX; }
        p.AddFeature(m);
        sw.Restart();
        p.Rebuild(prm);
        Console.WriteLine($"{variant}: {sw.ElapsedMilliseconds} ms vol {p.Volume:0.###} err={m.Error}");
    }
}
