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
        if (variant.StartsWith("faces"))
        {
            int n = int.Parse(variant.Substring(5));
            for (int i = n; i < p.Features.Count; i++) p.Features[i].Suppressed = true;
            p.Rebuild(prm);
            foreach (var f in p.Bodies.SelectMany(b => b.Faces))
            {
                var src = (f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source ?? f.Surface;
                var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(src);
                string d = sh switch
                {
                    Parametric3D.Kernel.Geometry.Ssi.Cylinder cy => $"cyl P {cy.P:0.###} axis {cy.Axis:0.###} R {cy.R:0.#####}",
                    Parametric3D.Kernel.Geometry.Ssi.Plane pl => $"plane P {pl.P:0.###} N {pl.N:0.###}",
                    Parametric3D.Kernel.Geometry.Ssi.Cone k => $"cone apex {k.Apex:0.###}",
                    _ => sh?.GetType().Name ?? "?"
                };
                var vs = f.Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).ToList();
                Console.WriteLine($"face {f.Id} {f.Tag}: {d}; z {vs.Min(v => v.Z):0.###}..{vs.Max(v => v.Z):0.###}, edges {f.Edges.Count()}");
                if (Environment.GetEnvironmentVariable("P3D_FACELOOPS") is { } fl && f.Tag.Contains(fl))
                    foreach (var l in f.Loops) { Console.WriteLine("   loop:"); foreach (var ce in l.CoEdges) Console.WriteLine($"     {ce.Edge.Curve.GetType().Name} {ce.Start.P:0.####} -> {ce.End.P:0.####}"); }
            }
            return;
        }
        if (variant.StartsWith("vtx"))
        {
            // vtxN:x,y,z — вершины тела после N операций у точки (разбор точности вершин)
            var parts = variant.Substring(3).Split(':'); int n = int.Parse(parts[0]);
            var q = parts[1].Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var at = new Vector3d(q[0], q[1], q[2]);
            for (int i = n; i < p.Features.Count; i++) p.Features[i].Suppressed = true;
            p.Rebuild(prm);
            foreach (var v in p.Bodies.SelectMany(b => b.Faces).SelectMany(f => f.Edges).SelectMany(e => new[] { e.V0, e.V1 }).Distinct())
                if ((v.P - at).Length < 1e-3) Console.WriteLine($"vertex after {n}: {v.P.X:R} {v.P.Y:R} {v.P.Z:R} (off {(v.P - at).Length:G3})");
            return;
        }
        if (variant.StartsWith("ids"))
        {
            // ids[:twice] — треугольники сетки показа лежат ли на поверхности грани своего номера
            p.Rebuild(prm);
            void Report(string when)
            {
                var md = p.Mesh!;
                var bodyFaces = p.Bodies.SelectMany(b => b.Faces).ToDictionary(f => f.Id);
                var per = new Dictionary<uint, (int N, int Off, double Worst)>();
                for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                {
                    var a = md.Vertices[md.Indices[t]]; var b = md.Vertices[md.Indices[t + 1]]; var c = md.Vertices[md.Indices[t + 2]];
                    var cur = per.GetValueOrDefault(a.FaceId);
                    double d = -1;
                    if (bodyFaces.TryGetValue(a.FaceId, out var bf) && cur.N % 7 == 0)
                    {
                        var src = (bf.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source ?? bf.Surface;
                        var cen = new Vector3d((a.P.X + b.P.X + c.P.X) / 3, (a.P.Y + b.P.Y + c.P.Y) / 3, (a.P.Z + b.P.Z + c.P.Z) / 3);
                        d = (src.PointAt(src.ProjectToUV(cen)) - cen).Length;
                    }
                    per[a.FaceId] = (cur.N + 1, cur.Off + (d > 0.5 ? 1 : 0), Math.Max(cur.Worst, d));
                }
                Console.WriteLine($"{when}: mesh faces {per.Count}, body faces {bodyFaces.Count}, missing in body {per.Keys.Count(k => !bodyFaces.ContainsKey(k))}");
                foreach (var bf in bodyFaces.Values) if (!per.ContainsKey(bf.Id)) Console.WriteLine($"   body face without mesh {bf.Id} {bf.Tag} {(bf.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source?.GetType().Name} facets {(bf.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Facets.Count}");
                foreach (var (fid, v) in per.OrderBy(k => k.Key)) Console.WriteLine($"   mesh face {fid} {bodyFaces.GetValueOrDefault(fid)?.Tag} tris {v.N}");
                foreach (var (fid, v) in per.OrderBy(k => k.Key)) if (v.Off > 0 || !bodyFaces.ContainsKey(fid)) Console.WriteLine($"   face {fid} {bodyFaces.GetValueOrDefault(fid)?.Tag}: tris {v.N}, off-surface samples {v.Off}, worst {v.Worst:0.###}");
            }
            Report("first");
            if (variant == "ids:twice")
            {
                var last = p.Features[^1]; last.Suppressed = true; p.NotifyChanged(last); p.Rebuild(prm); Report("suppressed last");
                last.Suppressed = false; p.NotifyChanged(last); p.Rebuild(prm); Report("restored last");
            }
            return;
        }
        if (variant.StartsWith("fold"))
        {
            // foldN: после N операций — у каждой грани: площадь сетки, треугольники против нормали поверхности, площадь по формулам
            int n = int.Parse(variant.Substring(4));
            for (int i = n; i < p.Features.Count; i++) p.Features[i].Suppressed = true;
            p.Rebuild(prm);
            var md = p.Mesh!;
            var bodyFaces = p.Bodies.SelectMany(b => b.Faces).ToDictionary(f => f.Id);
            var per = new Dictionary<uint, (int Tris, int Against, double Area, double AgainstArea)>();
            var badN = new Dictionary<uint, int>();
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
            {
                var a = md.Vertices[md.Indices[t]]; var b = md.Vertices[md.Indices[t + 1]]; var c = md.Vertices[md.Indices[t + 2]];
                var g = Vector3.Cross(b.P - a.P, c.P - a.P); double ar = g.Length / 2;
                bool against = false;
                if (bodyFaces.TryGetValue(a.FaceId, out var bf))
                {
                    var src = (bf.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source ?? bf.Surface;
                    var cen = new Vector3d((a.P.X + b.P.X + c.P.X) / 3, (a.P.Y + b.P.Y + c.P.Y) / 3, (a.P.Z + b.P.Z + c.P.Z) / 3);
                    var sn = src.NormalAt(src.ProjectToUV(cen)); if (bf.Reversed) sn = -sn;
                    against = Vector3d.Dot(sn, new Vector3d(g.X, g.Y, g.Z)) < 0;
                }
                var cur = per.GetValueOrDefault(a.FaceId);
                if (bodyFaces.TryGetValue(a.FaceId, out var bf2))
                {
                    var src2 = (bf2.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source ?? bf2.Surface;
                    foreach (var q in new[] { a, b, c })
                    {
                        var sn = src2.NormalAt(src2.ProjectToUV(new Vector3d(q.P.X, q.P.Y, q.P.Z)));
                        double cosv = Math.Abs(Vector3d.Dot(sn.Normalized(), new Vector3d(q.N.X, q.N.Y, q.N.Z).Normalized()));
                        if (cosv < 0.9) badN[a.FaceId] = badN.GetValueOrDefault(a.FaceId) + 1;
                    }
                }
                per[a.FaceId] = (cur.Tris + 1, cur.Against + (against ? 1 : 0), cur.Area + ar, cur.AgainstArea + (against ? ar : 0));
            }
            foreach (var (fid, v) in per.OrderBy(k => k.Key))
            {
                var bf = bodyFaces.GetValueOrDefault(fid);
                double? exactA = null; try { exactA = bf == null ? null : Parametric3D.Kernel.Topology.AnalyticVolume.FaceArea(bf); } catch { }
                Console.WriteLine($"face {fid} {bf?.Tag}: tris {v.Tris}, normals off >25deg {badN.GetValueOrDefault(fid)}, against {v.Against} (area {v.AgainstArea:0.###}), mesh area {v.Area:0.###}, exact {exactA:0.###}");
            }
            return;
        }
        if (variant.StartsWith("overlap"))
        {
            // overlapN: после N операций — грани, чьи треугольники лежат на треугольниках другой грани (двойная поверхность)
            int n = int.Parse(variant.Substring(7));
            for (int i = n; i < p.Features.Count; i++) p.Features[i].Suppressed = true;
            p.Rebuild(prm);
            var md = p.Mesh!;
            var tris = new List<(Vector3 A, Vector3 B, Vector3 C, uint F)>();
            for (int t = 0; t + 2 < md.Indices.Length; t += 3) tris.Add((md.Vertices[md.Indices[t]].P, md.Vertices[md.Indices[t + 1]].P, md.Vertices[md.Indices[t + 2]].P, md.Vertices[md.Indices[t]].FaceId));
            static float DistPT(Vector3 p0, Vector3 a, Vector3 b, Vector3 c)
            {
                var n0 = Vector3.Cross(b - a, c - a); if (n0.LengthSquared < 1e-20f) return 1e9f; n0 = n0.Normalized();
                float d = Vector3.Dot(p0 - a, n0); var q = p0 - n0 * d;
                bool In(Vector3 x, Vector3 y) => Vector3.Dot(Vector3.Cross(y - x, q - x), n0) >= -1e-4f;
                return In(a, b) && In(b, c) && In(c, a) ? Math.Abs(d) : 1e9f;
            }
            var faces = tris.Select(t => t.F).Distinct().ToList();
            var bodyFaces = p.Bodies.SelectMany(b => b.Faces).ToDictionary(f => f.Id);
            foreach (var fa in faces)
            {
                var mine = tris.Where(t => t.F == fa).ToList();
                var hits = new Dictionary<uint, int>(); int samples = 0;
                var big = mine.Where(t => Vector3.Cross(t.B - t.A, t.C - t.A).Length > 0.2f).ToList();
                foreach (var t in big.Where((_, i) => i % Math.Max(1, big.Count / 300) == 0))
                {
                    samples++;
                    var c = (t.A + t.B + t.C) / 3; var na = Vector3.Cross(t.B - t.A, t.C - t.A).Normalized();
                    foreach (var u in tris)
                    {
                        if (u.F == fa) continue;
                        var nb = Vector3.Cross(u.B - u.A, u.C - u.A); if (nb.LengthSquared < 1e-12f) continue;
                        if (Math.Abs(Vector3.Dot(na, nb.Normalized())) < 0.95f) continue;
                        if (DistPT(c, u.A, u.B, u.C) < (Environment.GetEnvironmentVariable("P3D_OVTOL") is { } ot ? float.Parse(ot) : 0.02f)) { hits[u.F] = hits.GetValueOrDefault(u.F) + 1; break; }
                    }
                }
                var tag = bodyFaces.TryGetValue(fa, out var bf) ? bf.Tag + " " + ((bf.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source?.GetType().Name ?? bf.Surface.GetType().Name) : "?";
                Console.WriteLine($"face {fa} {tag}: tris {mine.Count}, samples {samples}, on other faces: {string.Join(", ", hits.Select(kv => kv.Key + "x" + kv.Value))}");
            }
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
