using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>
/// Ворота g.02: ПОИСК НЕВЕРНОГО ПРИНЯТИЯ РАЗНОСТНЫМ СРАВНЕНИЕМ С ДРУГИМ ЯДРОМ. Случайные булевы над примитивами (брусок,
/// цилиндр, конус, шар, тор; случайные размеры, сдвиги, повороты; цепочки из двух операций). Наше ядро решает (успех /
/// отказ); операнды (точные примитивы) пишутся в STEP, и OpenCascade (tools/occt_diff.py) строит ту же цепочку своей булевой
/// от тех же примитивов. Неверное принятие — наш УСПЕХ с объёмом, не совпадающим с независимым, сверх допуска уровня гарантии.
/// Здесь — только выгрузка: операнды, наш исход, гарантия, объёмы (аналитический, сетки), допуск.
/// </summary>
static partial class Program
{
    /// <summary>Точный объём операнда: заготовки PrimitiveOp — до соглашения о контурах; объём — по нормализованной копии.</summary>
    static double OpVolume(TSolid o)
    {
        if (o.Exact != null && o.ExactAuthoritative) return o.Exact.Solid.VolumeMm3().ToDouble();
        if (o.TopologyConvention >= TopologyConventions.Version) return AnalyticVolume.Of(o) ?? double.NaN;
        var c = TopologyClone.Deep(o); TopologyConventions.Normalize(c); return AnalyticVolume.Of(c) ?? double.NaN;
    }

    static void PrimCheck(string dir)
    {
        Directory.CreateDirectory(dir);
        var prims = new (string, Func<TSolid>)[] {
            ("box", () => Box(-5, -5, -5, 10, 12, 14)),
            ("cyl", () => PrimitiveOp.Cylinder(new Vector3d(1, 2, 3), 5, 10)),
            ("cone", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 6, 3, 10)),
            ("cone-tip", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 6, 0, 10)),
            ("sphere", () => PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 7)),
            ("torus", () => PrimitiveOp.Torus(new Vector3d(1, 2, 3), 8, 3)),
        };
        foreach (var (name, mk) in prims)
            foreach (bool rot in new[] { false, true })
            {
                var s = mk(); if (rot) s = TransformOp.Copy(s, TransformOp.Rotate(new Vector3d(1, 2, 3), new Vector3d(0.3, 0.5, 0.8).Normalized(), 37));
                var v = SolidValidator.Validate(s);
                var sw = new StringWriter(); var w = new List<string>();
                bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, name, w);
                File.WriteAllText(Path.Combine(dir, $"{name}{(rot ? "_rot" : "")}.step"), sw.ToString());
                Console.WriteLine($"{name}{(rot ? " rot" : "")}: guarantee {s.Guarantee} conv {s.TopologyConvention} faces {s.Faces.Count()} loops {string.Join(",", s.Faces.Select(f => f.Loops.Count + ":" + f.Loops.Sum(l => l.CoEdges.Count)))} exactVol {AnalyticVolume.Of(s)} valid {v.Summary()} step {ok}");
            }
    }

    static void DiffDump(string dir, int seed, int n)
    {
        Directory.CreateDirectory(dir);
        var man = new List<string> { "id\tops\tkinds\toutcome\tguarantee\tbodies\tvolAnalytic\tvolMesh\tarea\tchord\tdetail\topVolumes\tbound\tvalid" };
        var rnd = new Random(seed);
        double U(double a, double b) => a + (b - a) * rnd.NextDouble();
        (TSolid S, string Desc) Prim()
        {
            int k = rnd.Next(5);
            var at = new Vector3d(U(-10, 10), U(-10, 10), U(-10, 10));
            TSolid s; string d;
            switch (k)
            {
                case 0: { double w = U(8, 40), dd = U(8, 40), h = U(8, 40); s = Box(at.X - w / 2, at.Y - dd / 2, at.Z - h / 2, w, dd, h); d = $"box {w:0.##}x{dd:0.##}x{h:0.##}"; break; }
                case 1: { double r = U(2, 12), h = U(8, 40); s = PrimitiveOp.Cylinder(at - new Vector3d(0, 0, h / 2), r, h); d = $"cyl r{r:0.##} h{h:0.##}"; break; }
                case 2: { double r0 = U(2, 12), r1 = U(0, 10), h = U(8, 30); if (r1 < 0.5) r1 = 0; s = PrimitiveOp.Cone(at - new Vector3d(0, 0, h / 2), r0, r1, h); d = $"cone r{r0:0.##}/{r1:0.##} h{h:0.##}"; break; }
                case 3: { double r = U(3, 15); s = PrimitiveOp.Sphere(at, r); d = $"sphere r{r:0.##}"; break; }
                default: { double tube = U(1.5, 5), ring = tube + U(2, 12); s = PrimitiveOp.Torus(at, ring, tube); d = $"torus {ring:0.##}/{tube:0.##}"; break; }
            }
            if (rnd.Next(2) == 0)
            {
                var axis = new Vector3d(U(-1, 1), U(-1, 1), U(-1, 1)); if (axis.Length < 0.1) axis = Vector3d.UnitX;
                double deg = U(0, 180);
                s = TransformOp.Copy(s, TransformOp.Rotate(at, axis.Normalized(), deg)); d += $" rot{deg:0}";
            }
            return (s, d);
        }
        string OpName(BooleanOp.BooleanKind k) => k switch { BooleanOp.BooleanKind.Subtract => "cut", BooleanOp.BooleanKind.Unite => "fuse", _ => "common" };
        // РАЗБОР ОДНОГО СЛУЧАЯ БЕЗ ПРОГОНА ВСЕЙ СЕРИИ: DIFFREPLAY=<manifest.tsv прежнего прогона того же зерна>, DIFFONLY=<id>.
        // Прочие случаи не вычисляются — только те же случайные выборы (число выполненных операций — из манифеста).
        string? only = Environment.GetEnvironmentVariable("DIFFONLY");
        string? profPath = Environment.GetEnvironmentVariable("DIFFPROF");
        var replayOps = new Dictionary<string, int>();
        if (Environment.GetEnvironmentVariable("DIFFREPLAY") is { } rp && File.Exists(rp))
            foreach (var line in File.ReadAllLines(rp).Skip(1)) { var f = line.Split('\t'); if (f.Length > 1) replayOps[f[0]] = f[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Length; }
        for (int i = 0; i < n; i++)
        {
            string id = $"d{seed}_{i:D4}";
            if (only != null && id != only)
            {
                int st = 1 + rnd.Next(2); Prim();
                int done = replayOps.TryGetValue(id, out var dn) ? dn : st;
                for (int s2 = 0; s2 < Math.Min(st, done); s2++) { Prim(); rnd.Next(4); }
                continue;
            }
            var ops = new List<string>(); var kinds = new List<string>();
            var caseSw = System.Diagnostics.Stopwatch.StartNew(); var opMs = new List<string>();
            if (profPath != null) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
            void Timing(string outcome)
            {
                if (profPath == null) return;
                var top = Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains(" мс")).Take(8).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " "));
                File.AppendAllText(profPath, $"{id}\t{caseSw.Elapsed.TotalMilliseconds:0}\t{string.Join(",", opMs)}\t{outcome}\t{string.Join(" | ", kinds)}\t{string.Join(" ;; ", top)}\n");
            }
            try
            {
                int steps = 1 + rnd.Next(2);
                var (a, da) = Prim(); var operands = new List<TSolid> { a }; kinds.Add(da);
                var cur = new List<TSolid> { a };
                OperationResult? r = null;
                for (int s = 0; s < steps; s++)
                {
                    var (b, db) = Prim(); operands.Add(b); kinds.Add(db);
                    if (only != null)
                        for (int j = 0; j < operands.Count; j++)
                        {
                            var swj = new StringWriter();
                            if (Parametric3D.Kernel.Export.StepExact.Write(swj, new[] { operands[j] }, $"{id}_{j}", new List<string>())) File.WriteAllText(Path.Combine(dir, $"{id}_op{j}.step"), swj.ToString());
                        }
                    var k = (BooleanOp.BooleanKind)new[] { BooleanOp.BooleanKind.Subtract, BooleanOp.BooleanKind.Subtract, BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect }[rnd.Next(4)];
                    ops.Add(OpName(k));
                    bool dbg = Environment.GetEnvironmentVariable("DIFFDBG") == id;
                    if (dbg) BooleanOp.CsgDisagreeProbe = (pt, ca, cb, cr, rb) =>
                    {
                        Console.WriteLine($"    CSG disagree at {pt:0.###}: A {ca} B {cb} result {cr}");
                        foreach (var fc in rb.Faces) { var af0 = Parametric3D.Kernel.Analytic.AnalyticFace.Of(fc, out _);
                            if (af0 != null && af0.Sph && fc.InteriorHint is { } hh)
                            {
                                var pts = new List<Vector3d>(); foreach (var ce in fc.Loops.SelectMany(l => l.CoEdges)) ce.Tessellate(pts, true);
                                var h = (hh - af0.O).Normalized(); var r = af0.W; var nAB = Vector3d.Cross(h, r); int ncr = 0;
                                for (int i = 0; i + 1 < pts.Count; i++) { var P = (pts[i] - af0.O).Normalized(); var Q = (pts[i + 1] - af0.O).Normalized(); if ((Vector3d.Dot(nAB, P) > 0) == (Vector3d.Dot(nAB, Q) > 0)) continue; var nPQ = Vector3d.Cross(P, Q); if ((Vector3d.Dot(nPQ, h) > 0) == (Vector3d.Dot(nPQ, r) > 0)) continue; var x = Vector3d.Cross(nAB, nPQ).Normalized(); if (Vector3d.Dot(x, h + r) < 0) x = -x; if (Vector3d.Dot(x, P + Q) < 0) continue; cr++; }
                                var hu = af0.PlaneUV(hh); Console.WriteLine($"      sph face {fc.Id}: crossings hint→pole {ncr} (odd = pole outside); hint uv {hu:0.###} contains {af0.Contains(hh)}");
                            }
                        }
                        foreach (var fc in rb.Faces) { var af = Parametric3D.Kernel.Analytic.AnalyticFace.Of(fc, out _); Console.WriteLine($"      face {fc.Id} {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} rev {fc.Reversed} loops {fc.Loops.Count} hint {fc.InteriorHint:0.###} area {AnalyticVolume.FaceArea(fc):G6} int {AnalyticVolume.FaceIntegral(fc):G6} normalAtHint {(fc.InteriorHint is { } h ? fc.NormalAt(h) : Vector3d.Zero):0.###} containsHint {(fc.InteriorHint is { } h2 && af != null ? af.Contains(h2) : null)} pole {(af != null && af.Sph ? af.O + af.W * af.R : Vector3d.Zero):0.###} containsPole {(af != null && af.Sph ? af.Contains(af.O + af.W * af.R * 0.999999 + af.U * 1e-3) : null)} why {(af == null ? "?" : "")}"); }
                    };
                    if (dbg) BooleanOp.GeneralInvalidProbe = rb =>
                    {
                        foreach (var fc in rb.Faces)
                        {
                            var nf = Vector3d.Zero; int nt = 0;
                            if (fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx) foreach (var (ta, tb, tc) in fsx.Facets.ReadView) { nf += Vector3d.Cross(tb - ta, tc - ta) / 2; nt++; }
                            Vector3d nl; try { nl = AnalyticVolume.NormalIntegral(fc); } catch { nl = new Vector3d(double.NaN); }
                            int agreeN = 0, disN = 0; var srcF = fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsy ? fsy.Source : null;
                            if (srcF != null && fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsz)
                                foreach (var (ta, tb, tc) in fsz.Facets.ReadView) { var cc = (ta + tb + tc) / 3; var ns = srcF.NormalAt(srcF.ProjectToUV(cc)); if (Vector3d.Dot(Vector3d.Cross(tb - ta, tc - ta), ns) >= 0) { agreeN++; if (agreeN % 50 == 1) Console.WriteLine($"      plus face {fc.Id} at {cc:0.###} uv {srcF.ProjectToUV(cc):0.####} area {Vector3d.Cross(tb - ta, tc - ta).Length / 2:G3}"); } else disN++; }
                            if (srcF != null) foreach (var lp in fc.Loops) foreach (var ce in lp.CoEdges) { var cv = ce.Edge.Curve; var uvs = Enumerable.Range(0, 9).Select(k => srcF.ProjectToUV(cv.PointAt(cv.T0 + (cv.T1 - cv.T0) * k / 8.0))).ToList(); Console.WriteLine($"      ce face {fc.Id} {cv.GetType().Name} rev {ce.Reversed} u {uvs.Min(q => q.X):0.###}..{uvs.Max(q => q.X):0.###} v {uvs.Min(q => q.Y):0.###}..{uvs.Max(q => q.Y):0.###} v0 {ce.Edge.V0.P:0.##} v1 {ce.Edge.V1.P:0.##}"); }
                            Console.WriteLine($"    INV face {fc.Id} srcN+ {agreeN} srcN- {disN} {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} loops {fc.Loops.Count} ces {string.Join("/", fc.Loops.Select(l => l.CoEdges.Count))} facets {nt} vecLoops {nl:0.##} vecFacets {nf:0.##} dot {Vector3d.Dot(nl, nf):G3} hint {fc.InteriorHint:0.##}");
                        }
                    };
                    if (dbg && Environment.GetEnvironmentVariable("SPLITDBG") == "1") Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("    split: " + m);
                    if (dbg) { BooleanOp.AnalyticRouteDebug = m => Console.WriteLine($"  {id} step {s} route: {m}"); TopologyConventions.AnchorDebug = m => Console.WriteLine("    anchor: " + m); }
                    // причины отказа точного маршрута по всей серии (DIFFWHY=<файл>): строка на операцию
                    var whyPath = Environment.GetEnvironmentVariable("DIFFWHY"); var whyL = new List<string>();
                    if (whyPath != null && !dbg) BooleanOp.AnalyticRouteDebug = m => whyL.Add(m);
                    var dw = new List<string>();
                    // предел времени одной операции — 120 с: «зависание» — тоже находка (исход Cancelled, в отчёт)
                    string lastStage = ""; var lastAt = DateTime.UtcNow;
                    var perThread = new System.Collections.Concurrent.ConcurrentDictionary<int, (string S, DateTime At)>();
                    System.Threading.Timer? watch = null;
                    if (only != null) watch = new System.Threading.Timer(_ => Console.WriteLine($"  WATCH {(DateTime.UtcNow - lastAt).TotalSeconds:F0}s since stage '{lastStage}'; threads: " + string.Join(", ", perThread.Select(kv => $"{kv.Key}:{kv.Value.S}@{(DateTime.UtcNow - kv.Value.At).TotalSeconds:F0}s"))), null, 10000, 10000);
                    using (new KernelContext(KernelSettings.Capture(), OperationPolicy.Strict, new KernelLimits { Deadline = DateTime.UtcNow.AddSeconds(120) }) { StageHook = st => { lastStage = st; lastAt = DateTime.UtcNow; perThread[Environment.CurrentManagedThreadId] = (st, DateTime.UtcNow); } }.Enter())
                    {
                        var osw = System.Diagnostics.Stopwatch.StartNew();
                        r = BooleanOp.Evaluate(cur, b, k, OperationPolicy.Strict, dw);
                        opMs.Add($"{OpName(k)}:{osw.Elapsed.TotalMilliseconds:0}:{r.Outcome}/{r.Guarantee}");
                    }
                    if (whyPath != null && !dbg) { BooleanOp.AnalyticRouteDebug = null; File.AppendAllText(whyPath, $"{id}\t{s}\t{OpName(k)}\t{r.Guarantee}\t{string.Join(" | ", whyL.Where(x => !x.StartsWith("declined: first operand: a face that is neither") && !x.StartsWith("declined: second operand: a face that is neither")))}\n"); }
                    if (dbg)
                    {
                        BooleanOp.AnalyticRouteDebug = null; TopologyConventions.AnchorDebug = null;
                        Console.WriteLine($"  {id} step {s} {OpName(k)}: {r.Describe()}");
                        foreach (var bb in r.Bodies) foreach (var iss in SolidValidator.Validate(bb).Issues.Take(6)) Console.WriteLine("    issue: " + iss);
                        foreach (var k2 in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
                        {
                            BooleanOp.AnalyticRouteDebug = m => Console.WriteLine($"      route {OpName(k2)}: {m}");
                            var r2 = BooleanOp.Evaluate(cur, b, k2, OperationPolicy.Strict);
                            BooleanOp.AnalyticRouteDebug = null;
                            Console.WriteLine($"    identity {OpName(k2)}: {r2.Outcome} {r2.Guarantee} vol {r2.Bodies.Sum(x => OpVolume(x)):R} mesh {r2.Bodies.Sum(x => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(x))):R}");
                            if (k2 == BooleanOp.BooleanKind.Intersect) foreach (var bb in r2.Bodies) foreach (var fc in bb.Faces) Console.WriteLine($"      face {fc.Id} {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} ces {string.Join("/", fc.Loops.Select(l => l.CoEdges.Count))} curves {string.Join(",", fc.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve.GetType().Name))} int {AnalyticVolume.FaceIntegral(fc):0.###} meshInt {(fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx ? fsx.Facets.ToList().Sum(t => Vector3d.Dot(t.Item1, Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1)) / 2) : double.NaN):0.###} area {AnalyticVolume.FaceArea(fc):0.###}");
                        }
                        Console.WriteLine($"    operands {cur.Sum(x => OpVolume(x)):R} {OpVolume(b):R}");
                        { var bn = TopologyClone.Deep(b); TopologyConventions.Normalize(bn); var r3 = BooleanOp.Evaluate(cur, bn, BooleanOp.BooleanKind.Intersect, OperationPolicy.Strict); Console.WriteLine($"    common with normalized tool: {r3.Outcome} {r3.Guarantee} vol {r3.Bodies.Sum(x => OpVolume(x)):R} conv {b.TopologyConvention}"); }
                        {
                            var ca = new Parametric3D.Kernel.Analytic.Classifier(cur[0]); var cb = new Parametric3D.Kernel.Analytic.Classifier(b);
                            var bx = cur[0].Vertices.Select(v => v.P).Concat(b.Vertices.Select(v => v.P)).ToList();
                            var lo = bx.Aggregate((x, y) => Vector3d.ComponentMin(x, y)) - new Vector3d(15); var hi = bx.Aggregate((x, y) => Vector3d.ComponentMax(x, y)) + new Vector3d(15);
                            var rr = new Random(3); int inA = 0, inB = 0, inAB = 0, nn = 0;
                            for (int q = 0; q < 200000; q++)
                            {
                                var pt = new Vector3d(lo.X + (hi.X - lo.X) * rr.NextDouble(), lo.Y + (hi.Y - lo.Y) * rr.NextDouble(), lo.Z + (hi.Z - lo.Z) * rr.NextDouble());
                                var x1 = ca.Classify(pt); var x2 = cb.Classify(pt); if (x1 is not (1 or -1) || x2 is not (1 or -1)) continue;
                                nn++; if (x1 == 1) inA++; if (x2 == 1) inB++; if (x1 == 1 && x2 == 1) inAB++;
                            }
                            double bv = (hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z);
                            Console.WriteLine($"    our-classifier MC: A {bv * inA / nn:0} B {bv * inB / nn:0} A∩B {bv * inAB / nn:0}");
                        }
                        foreach (var bb in r.Bodies) foreach (var fc in bb.Faces)
                            if (fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsd && fsd.Source is { } srcd)
                            {
                                int agree = 0, dis = 0;
                                foreach (var (ta, tb, tc) in fsd.Facets.ReadView) { var nn = Vector3d.Cross(tb - ta, tc - ta); var cc = (ta + tb + tc) / 3; var ns = srcd.NormalAt(srcd.ProjectToUV(cc)) * (fc.Reversed ? -1 : 1); if (Vector3d.Dot(nn, ns) >= 0) agree++; else dis++; }
                                Console.WriteLine($"    orient face {fc.Id} rev {fc.Reversed} facets agree {agree} disagree {dis}");
                            }
                        foreach (var bb in r.Bodies) foreach (var fc in bb.Faces) Console.WriteLine($"    face {fc.Id} {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} loops {fc.Loops.Count} ces {string.Join("/", fc.Loops.Select(l => l.CoEdges.Count))} int {AnalyticVolume.FaceIntegral(fc):0.##} area {AnalyticVolume.FaceArea(fc):0.##}");
                        foreach (var x in dw) Console.WriteLine("    warn: " + x);
                        for (int bi = 0; bi < r.Bodies.Count; bi++)
                        {
                            var bb = r.Bodies[bi];
                            if (bb.Exact != null)
                            {
                                var R = bb.Exact.Solid.Registry; var sw = new StringWriter(); var vid = new Dictionary<int, int>();
                                foreach (var pg in bb.Exact.Solid.Polygons)
                                {
                                    var ids = pg.Vertices(R).ToArray();
                                    foreach (var q in ids) if (!vid.ContainsKey(q)) { vid[q] = vid.Count + 1; var (x, y, z) = R.ToMm(q); sw.WriteLine(FormattableString.Invariant($"v {x:R} {y:R} {z:R}")); }
                                    sw.WriteLine("f " + string.Join(" ", ids.Select(q => vid[q])));
                                }
                                File.WriteAllText(Path.Combine(dir, $"{id}_s{s}_b{bi}_exact.obj"), sw.ToString());
                            }
                            var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(bb); var sw2 = new StringWriter();
                            foreach (var v in md.Vertices) sw2.WriteLine(FormattableString.Invariant($"v {v.P.X:R} {v.P.Y:R} {v.P.Z:R}"));
                            for (int t = 0; t + 2 < md.Indices.Length; t += 3) sw2.WriteLine($"f {md.Indices[t] + 1} {md.Indices[t + 1] + 1} {md.Indices[t + 2] + 1}");
                            File.WriteAllText(Path.Combine(dir, $"{id}_s{s}_b{bi}_mesh.obj"), sw2.ToString());
                        }
                        foreach (var bb in r.Bodies) Console.WriteLine($"    body shells {bb.Shells.Count} faces {bb.Faces.Count()} vol {OpVolume(bb):R} guarantee {bb.Guarantee} valid {SolidValidator.Validate(bb).Summary()}");
                    }
                    if (r.Outcome != OperationOutcome.Success) break;
                    cur = r.Bodies.ToList();
                }
                // операнды — точным STEP (примитивы пишутся точно); отказ записи — случай пропускается (не наш результат)
                for (int j = 0; j < operands.Count; j++)
                {
                    var sw = new StringWriter(); var w = new List<string>();
                    if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { operands[j] }, $"{id}_{j}", w)) throw new Exception("operand not exportable: " + string.Join("; ", w));
                    File.WriteAllText(Path.Combine(dir, $"{id}_op{j}.step"), sw.ToString());
                }
                // независимая проверка спорных случаев: Монте-Карло по точным классификаторам ИСХОДНЫХ операндов (DIFFMC=<число точек>)
                if (only != null && int.TryParse(Environment.GetEnvironmentVariable("DIFFMC"), out int mcN) && mcN > 0)
                {
                    var cls = operands.Select(o => new Parametric3D.Kernel.Analytic.Classifier(o)).ToList();
                    var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
                    foreach (var o in operands) foreach (var vv in Parametric3D.Kernel.Tess.Tessellator.Tessellate(o).Vertices) { var q = new Vector3d(vv.P.X, vv.P.Y, vv.P.Z); lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q); }
                    lo -= new Vector3d(0.5); hi += new Vector3d(0.5);
                    var rr = new Random(11); long nin = 0, nund = 0;
                    var resCls = cur.Select(b => new Parametric3D.Kernel.Analytic.Classifier(b)).ToList(); long nres = 0, nmis = 0;
                    for (int q = 0; q < mcN; q++)
                    {
                        var pt = new Vector3d(lo.X + (hi.X - lo.X) * rr.NextDouble(), lo.Y + (hi.Y - lo.Y) * rr.NextDouble(), lo.Z + (hi.Z - lo.Z) * rr.NextDouble());
                        var cs = cls.Select(c => c.Classify(pt)).ToList();
                        if (cs.Any(x => x is not (1 or -1))) { nund++; continue; }
                        bool inside = cs[0] == 1;
                        for (int j = 1; j < cs.Count; j++) inside = ops[j - 1] switch { "fuse" => inside || cs[j] == 1, "common" => inside && cs[j] == 1, _ => inside && cs[j] != 1 };
                        if (inside) nin++;
                        bool ir = resCls.Any(c => c.Classify(pt) == 1); if (ir) nres++; if (ir != inside) nmis++;
                    }
                    double bv = (hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z), pp = (double)nin / mcN;
                    Console.WriteLine($"  DIFFMC {id}: N {mcN} CSG-of-operands {bv * pp:0.###} ± {bv * Math.Sqrt(pp * (1 - pp) / mcN):0.###} (undecided {nund}); result-classifier {bv * nres / mcN:0.###}; points where result ≠ CSG {nmis}");
                }
                string outcome = r!.Outcome.ToString();
                double va = double.NaN, vm = double.NaN, area = 0;
                if (r.Outcome == OperationOutcome.Success)
                {
                    va = 0; vm = 0;
                    foreach (var b in cur)
                    {
                        va += AnalyticVolume.Of(b) ?? double.NaN;
                        vm += Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
                        area += AnalyticVolume.AreaOf(b) ?? double.NaN;
                    }
                }
                else if (r.Outcome == OperationOutcome.Empty) { va = 0; vm = 0; }
                string guar = r.Outcome == OperationOutcome.Success ? string.Join(",", cur.Select(b => b.Guarantee.ToString()).Distinct()) : "";
                man.Add($"{id}\t{string.Join(",", ops)}\t{string.Join(" | ", kinds)}\t{outcome}\t{guar}\t{(r.Outcome == OperationOutcome.Success ? cur.Count : 0)}\t{va:R}\t{vm:R}\t{area:R}\t{Parametric3D.Core.Tol.Chord:R}\t{(r.Outcome == OperationOutcome.Success ? "" : r.Describe().Replace('\t', ' '))}\t{string.Join(",", operands.Select(o => (OpVolume(o)).ToString("R")))}\t{(r.Outcome == OperationOutcome.Success ? cur.Sum(bb => AnalyticMeasure.Volume(bb).Error) : 0):R}\t{(r.Outcome == OperationOutcome.Success ? string.Join(" ", cur.Select(bb => SolidValidator.Validate(bb)).Where(v => !v.IsValid).Select(v => v.Summary().Replace('\t', ' '))) is var bad && bad.Length > 0 ? "INVALID " + bad : "ok" : "")}");
                Timing(outcome);
            }
            catch (Exception ex) { Timing("exception " + ex.GetType().Name); man.Add($"{id}\t{string.Join(",", ops)}\t{string.Join(" | ", kinds)}\texception\t\t0\tNaN\tNaN\t0\t0\t{ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}"); }
            // манифест — после каждого случая: прерывание серии (предел времени) не теряет готовые случаи
            File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), man);
        }
        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), man);
        Console.WriteLine($"diff dump: {n} cases → {dir}");
    }
}
