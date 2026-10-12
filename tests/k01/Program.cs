// K01/K02 checks: unified validator, sphere reproducer, negative cases, corpus survey, fault injection.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static int Passed, Failed;
    static void Log(object o) => Console.WriteLine(JsonSerializer.Serialize(o));
    static void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
    static string? CurrentCheck;
    static void Check(string id, Action a)
    {
        CurrentCheck = id;
        try { a(); Passed++; Log(new { test = id, ok = true }); }
        catch (Exception e) { Failed++; Log(new { test = id, ok = false, error = e.Message }); if (Environment.GetEnvironmentVariable("CHECKSTACK") == "1") Console.WriteLine(e.StackTrace); }
    }
    internal static TSolid Box(double x = 0, double y = 0, double z = 0, double w = 60, double d = 40, double h = 20)
    {
        var f = new Frame3(new Vector3d(x, y, z), Vector3d.UnitX, Vector3d.UnitY);
        var p = new[] { f.ToWorld(0, 0), f.ToWorld(w, 0), f.ToWorld(w, d), f.ToWorld(0, d) };
        return ExtrudeOp.Build(ProfileBuilder.Build(f, Enumerable.Range(0, 4)
            .Select(i => (ICurve3)new LineCurve3(p[i], p[(i + 1) % 4])).ToList()), new Vector3d(0, 0, h));
    }
    // speed/Program.cs metric, kept verbatim for comparability with archived logs
    static int SpeedOpenEdges(MeshData mesh)
    {
        var ids = new Dictionary<(long, long, long), int>();
        int Id(int i) { var p = mesh.Vertices[i].P; var k = ((long)Math.Round(p.X * 1e5), (long)Math.Round(p.Y * 1e5), (long)Math.Round(p.Z * 1e5));
            if (!ids.TryGetValue(k, out int n)) { n = ids.Count; ids[k] = n; } return n; }
        var edges = new Dictionary<(int, int), int>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
            for (int j = 0; j < 3; j++) { int a = Id(mesh.Indices[i + j]), b = Id(mesh.Indices[i + (j + 1) % 3]); if (a == b) continue;
                var k = a < b ? (a, b) : (b, a); edges[k] = edges.GetValueOrDefault(k) + 1; }
        return edges.Count(e => e.Value != 2);
    }
    static string MeshHash(MeshData m)
    {
        using var s = new MemoryStream();
        using (var w = new BinaryWriter(s, System.Text.Encoding.UTF8, true))
        { w.Write(m.Vertices.Length); w.Write(m.Indices.Length);
          foreach (var v in m.Vertices) { w.Write(v.P.X); w.Write(v.P.Y); w.Write(v.P.Z); }
          foreach (int i in m.Indices) w.Write(i); }
        return Convert.ToHexString(SHA256.HashData(s.ToArray()))[..16];
    }

    // ------------------------------------------------------------ scenarios (stable IDs)
    internal static List<TSolid> Scenario(string id, List<string> w)
    {
        var b = new List<TSolid> { Box() };
        BooleanOp.DeferMeshing = true;
        try
        {
            switch (id)
            {
                case "S-BOX": break;
                case "S-HOLE": BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), w); break;
                case "S-HOLES4": case "S-HOLES8":
                    int n = id == "S-HOLES4" ? 4 : 8;
                    for (int i = 0; i < n; i++) BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(8 + 12 * (i % 4), 10 + 20 * (i / 4), -2), 3, 24), w);
                    break;
                case "S-SPHERE": BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), w); break;
                case "S-SPHERE-IN": BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 5), w); break;
                case "S-OVERLAP": BooleanOp.Subtract(b, Box(30, 0), w); break;
                case "S-UNION": BooleanOp.Unite(b, Box(30, 20, 10), w); break;
                case "S-DISJOINT-UNION": BooleanOp.Unite(b, Box(100, 0), w); break;
                case "S-ROUNDHOLE":
                    BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), w);
                    var e = b[0].Edges.Where(x => !x.Hidden && x.Curve is ArcCurve3).OrderByDescending(x => x.V0.P.Z).First();
                    FilletOp.RoundEdges(b, new[] { EdgeRef.Of(e) }, 2, w); break;
                case "S-ROUND":
                    var e2 = b[0].Edges.First(x => (x.V0.P - Vector3d.Zero).Length < 1e-8 && (x.V1.P - new Vector3d(0, 0, 20)).Length < 1e-8 ||
                                                  (x.V1.P - Vector3d.Zero).Length < 1e-8 && (x.V0.P - new Vector3d(0, 0, 20)).Length < 1e-8);
                    FilletOp.RoundEdges(b, new[] { EdgeRef.Of(e2) }, 3, w); break;
                default: throw new ArgumentException(id);
            }
        }
        finally { BooleanOp.DeferMeshing = false; }
        foreach (var s in b) BooleanOp.MeshExactlySolid(s, w);
        return b;
    }
    internal static readonly string[] Scenarios = { "S-BOX", "S-HOLE", "S-HOLES4", "S-HOLES8", "S-SPHERE", "S-SPHERE-IN",
        "S-OVERLAP", "S-UNION", "S-DISJOINT-UNION", "S-ROUNDHOLE", "S-ROUND" };

    // A01 sequence corpus generator (same seeds as tests/a01 "sequences")
    internal static List<TSolid> Sequence(int k, List<string> warn)
    {
        var r = new Random(91000 + k); double w = 50 + r.NextDouble() * 15, d = 35 + r.NextDouble() * 10, h = 12 + r.NextDouble() * 10;
        var b = new List<TSolid> { Box(w: w, d: d, h: h) };
        BooleanOp.DeferMeshing = true;
        int count = 1 + k % 3;
        try
        {
            for (int j = 0; j < count; j++)
            { double radius = 1.5 + r.NextDouble(); BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(10 + j * 14, 12, -2), radius, h + 4), warn); }
            BooleanOp.Subtract(b, Box(w + 100, 0, 0, 5, 5, h), warn);
        }
        finally { BooleanOp.DeferMeshing = false; }
        foreach (var s in b) BooleanOp.MeshExactlySolid(s, warn);
        return b;
    }

    static void Sphere()
    {
        var w = new List<string>();
        var b = Scenario("S-SPHERE", w);
        var m = Tessellator.Tessellate(b[0]);
        var rep = SolidValidator.Validate(b[0], m, new ValidationOptions { CheckSelfIntersections = true });
        Log(new { scenario = "S-SPHERE", speedOpenEdges = SpeedOpenEdges(m), triangles = m.Indices.Length / 3, hash = MeshHash(m),
                  volume = MassProps.Volume(m), verdict = rep.Summary(), metrics = rep.Metrics, issues = rep.Issues.Select(i => i.ToString()) });
        var (pairs, min) = ClosePairs(m, 1e-5);
        Log(new { scenario = "S-SPHERE", distinctVertexPairsCloserThan1e5 = pairs, minDistance = min });
        // The archived metric rounds coordinates to a 1e-5 grid and thereby merges distinct vertices;
        // closure is judged on output-coordinate identity (validator), the residual must be explained by close pairs.
        Check("K01-SPHERE-OPEN-EDGES", () => Require(rep.Metrics["mesh.openEdges"] == 0 && (SpeedOpenEdges(m) == 0 || pairs > 0),
            $"speed metric open edges {SpeedOpenEdges(m)} without close pairs"));
        Check("K01-SPHERE-VALIDATOR", () => Require(rep.IsValid, rep.ToString()));
        Check("K01-SPHERE-VOLUME", () => { double exp = 48000 - 2.0 / 3 * Math.PI * 512; Require(Math.Abs(MassProps.Volume(m) - exp) / exp < 3e-4, "volume"); });
    }

    static void Survey(bool selfX)
    {
        int bad = 0, total = 0;
        void One(string id, List<TSolid> bodies)
        {
            for (int bi = 0; bi < bodies.Count; bi++)
            {
                total++;
                var m = Tessellator.Tessellate(bodies[bi]);
                var sw = Stopwatch.StartNew();
                var rep = SolidValidator.Validate(bodies[bi], m, new ValidationOptions { CheckSelfIntersections = selfX });
                sw.Stop();
                if (!rep.IsValid) bad++;
                Log(new { survey = id, body = bi, valid = rep.IsValid, verdict = rep.Summary(), validateMs = sw.Elapsed.TotalMilliseconds,
                          speedOpenEdges = SpeedOpenEdges(m), triangles = m.Indices.Length / 3, hash = MeshHash(m),
                          zeroArea = rep.Metrics.GetValueOrDefault("mesh.zeroAreaTriangles"),
                          sameDirCoedges = rep.Metrics.GetValueOrDefault("brep.coedgesSameDirection"),
                          sx = selfX ? new { tr = rep.Metrics.GetValueOrDefault("geometry.pairs.transversal"),
                                             cop = rep.Metrics.GetValueOrDefault("geometry.pairs.coplanarOverlap"),
                                             tj = rep.Metrics.GetValueOrDefault("geometry.pairs.tJunction"),
                                             dup = rep.Metrics.GetValueOrDefault("geometry.pairs.duplicate"),
                                             pairs = rep.Metrics.GetValueOrDefault("geometry.pairs.tested") } : null,
                          errors = rep.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4).Select(i => i.ToString()) });
            }
        }
        foreach (var id in Scenarios) { var w = new List<string>(); One(id, Scenario(id, w)); }
        int nseq = selfX ? 60 : 450;
        for (int k = 0; k < nseq; k++) { var w = new List<string>(); One($"A01-SEQ-{k}", Sequence(k, w)); }
        Log(new { survey = "total", bodies = total, invalid = bad, selfIntersections = selfX });
        Check(selfX ? "K01-SURVEY-SELFX" : "K01-SURVEY", () => Require(bad == 0, $"{bad} of {total} bodies invalid"));
    }

    static void Main(string[] args)
    {
        Log(new { runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, cpu = Environment.ProcessorCount,
                  os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, args });
        BooleanOp.Threads = Tessellator.Threads = 1;
        string mode = args.FirstOrDefault() ?? "allA";
        // Переход на аналитическое ядро: из каких поверхностей состоят операнды каждой булевой (ANSURVEY=1).
        var anPairs = new Dictionary<string, int>();
        var anOps = new Dictionary<string, int>(); var anFaces = new Dictionary<string, int>(); var anOther = new Dictionary<string, int>();
        if (Environment.GetEnvironmentVariable("ANSURVEY") == "1")
            BooleanOp.EvaluateProbe = (bodies, other, kind) =>
            {
                string Kind(TFace f)
                {
                    var au = GeometryAudit.AuthoritativeSurface(f);
                    var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(au);
                    if (sh != null) return sh.GetType().Name;
                    string k = au == null ? "none" : au.GetType().Name;
                    if (au is Parametric3D.Kernel.Surfaces.RevolvedSurface rv) k += "(" + rv.Base.GetType().Name + ")";
                    if (au is Parametric3D.Kernel.Surfaces.ExtrudedSurface ex) k += "(" + ex.Base.GetType().Name + ")";
                    return k;
                }
                var kinds = new HashSet<string>();
                foreach (var b in bodies.Append(other))
                    foreach (var f in b.Faces) { var k = Kind(f); kinds.Add(k); anFaces[k] = anFaces.GetValueOrDefault(k) + 1; }
                string cat = kinds.All(k => k == "Plane") ? "1 plane only"
                           : kinds.All(k => k is "Plane" or "Cylinder" or "Cone" or "Sphere") ? "2 plane + quadrics"
                           : kinds.All(k => k is "Plane" or "Cylinder" or "Cone" or "Sphere" or "Torus") ? "3 + torus"
                           : "4 other";
                anOps[cat] = anOps.GetValueOrDefault(cat) + 1;
                // Пары граней «тело × резец» с пересекающимися габаритами — какие пересечения поверхностей понадобятся.
                (Vector3d Lo, Vector3d Hi)? Box(TFace f)
                {
                    var pts = new List<Vector3d>();
                    foreach (var l in f.Loops) foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                    if (pts.Count == 0) return null;
                    var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
                    foreach (var q in pts) { lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q); }
                    return (lo - new Vector3d(1e-6), hi + new Vector3d(1e-6));
                }
                string PairKind(Parametric3D.Kernel.Geometry.Ssi.Shape? x, Parametric3D.Kernel.Geometry.Ssi.Shape? y)
                {
                    string N(object? o) => o?.GetType().Name ?? "other";
                    var names = new[] { N(x), N(y) }.OrderBy(t => t).ToArray();
                    string k = names[0] + "×" + names[1];
                    Vector3d? Ax(object? o) => o switch { Parametric3D.Kernel.Geometry.Ssi.Cylinder c => c.Axis.Normalized(), Parametric3D.Kernel.Geometry.Ssi.Cone c => c.Axis.Normalized(), Parametric3D.Kernel.Geometry.Ssi.Torus t => t.Axis.Normalized(), _ => null };
                    Vector3d? Pt(object? o) => o switch { Parametric3D.Kernel.Geometry.Ssi.Cylinder c => c.P, Parametric3D.Kernel.Geometry.Ssi.Cone c => c.Apex, Parametric3D.Kernel.Geometry.Ssi.Torus t => t.C, Parametric3D.Kernel.Geometry.Ssi.Sphere sp => sp.C, _ => null };
                    var ax = Ax(x); var ay = Ax(y);
                    if (ax is { } u && ay is { } v)
                    {
                        bool par = Math.Abs(Math.Abs(Vector3d.Dot(u, v)) - 1) < 1e-9;
                        if (par)
                        {
                            var d = Pt(y)!.Value - Pt(x)!.Value; d -= u * Vector3d.Dot(d, u);
                            k += d.Length < 1e-7 ? " coaxial" : " parallel";
                        }
                        else k += Math.Abs(Vector3d.Dot(u, v)) < 1e-9 ? " perpendicular axes" : " skew axes";
                    }
                    else if (x is Parametric3D.Kernel.Geometry.Ssi.Plane pl && ay is { } a2 || y is Parametric3D.Kernel.Geometry.Ssi.Plane && ax != null)
                    {
                        var plane = (x as Parametric3D.Kernel.Geometry.Ssi.Plane) ?? (Parametric3D.Kernel.Geometry.Ssi.Plane)y!;
                        var axis = (ax ?? ay)!.Value; double c = Math.Abs(Vector3d.Dot(plane.N.Normalized(), axis));
                        k += c > 1 - 1e-9 ? " ⟂ axis" : c < 1e-9 ? " ∥ axis" : " oblique";
                    }
                    else if ((x is Parametric3D.Kernel.Geometry.Ssi.Sphere && ay != null) || (y is Parametric3D.Kernel.Geometry.Ssi.Sphere && ax != null))
                    {
                        var c0 = (x as Parametric3D.Kernel.Geometry.Ssi.Sphere ?? y as Parametric3D.Kernel.Geometry.Ssi.Sphere)!.C;
                        var axis = (ax ?? ay)!.Value; var p0 = (ax != null ? Pt(x) : Pt(y))!.Value;
                        var d = c0 - p0; d -= axis * Vector3d.Dot(d, axis);
                        k += d.Length < 1e-7 ? " centre on axis" : " off axis";
                    }
                    return k;
                }
                var toolFaces = other.Faces.Select(f => (F: f, B: Box(f), S: Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)))).Where(t => t.B != null).ToList();
                foreach (var b in bodies)
                    foreach (var f in b.Faces)
                    {
                        var bf = Box(f); if (bf == null) continue;
                        var sf = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
                        foreach (var t in toolFaces)
                        {
                            var (lo, hi) = bf.Value; var (lo2, hi2) = t.B!.Value;
                            if (lo.X > hi2.X || lo2.X > hi.X || lo.Y > hi2.Y || lo2.Y > hi.Y || lo.Z > hi2.Z || lo2.Z > hi.Z) continue;
                            var pk = PairKind(sf, t.S); anPairs[pk] = anPairs.GetValueOrDefault(pk) + 1;
                        }
                    }
                if (cat == "4 other") foreach (var k in kinds.Where(k => k is not ("Plane" or "Cylinder" or "Cone" or "Sphere" or "Torus"))) anOther[k] = anOther.GetValueOrDefault(k) + 1;
            };
        // Аналитическое ядро, этап 1: распознавание 2.5-мерных операндов и их объём по слоям против аналитического (PRISMSURVEY=1).
        var prSeen = new HashSet<TSolid>(ReferenceEqualityComparer.Instance);
        var prWhy = new Dictionary<string, int>(); int prOk = 0, prBad = 0, prNoAv = 0, prNoRef = 0, prHealed = 0, prHealedDiff = 0; double prHealMax = 0; var prBadCases = new List<string>(); var prErrs = new List<double>(); double prWorst = 0, prWorstMesh = 0; string prWorstCase = "", prWorstMeshCase = "";
        if (Environment.GetEnvironmentVariable("PRISMSURVEY") == "1")
            BooleanOp.EvaluateProbe = (bodies, other, kind) =>
            {
                foreach (var b in bodies.Append(other))
                {
                    if (!prSeen.Add(b)) continue;
                    var pr = Parametric3D.Kernel.Analytic.Prism25.Recognize(b, out var why);
                    if (pr == null) { var key = System.Text.RegularExpressions.Regex.Replace(why, @"\([^)]*\)", "(…)"); prWhy[key] = prWhy.GetValueOrDefault(key) + 1; continue; }
                    double? av = AnalyticVolume.Of(b);
                    if (av == null)
                    {
                        prNoAv++;
                        double vm;
                        if (b.Exact == null) { prNoRef++; continue; }        // сырой примитив: готовой точной формы нет
                        vm = b.Exact.Solid.VolumeMm3().ToDouble();
                        if (vm <= 0) { prNoRef++; continue; }
                        double em = Math.Abs(pr.Volume - vm) / Math.Max(1, Math.Abs(vm));
                        if (em > prWorstMesh) { prWorstMesh = em; prWorstMeshCase = $"faces {b.Faces.Count()}, levels {pr.Levels.Length} [{string.Join(",", pr.Levels.Select(x => x.ToString("0.###")))}], sections {string.Join(",", pr.Sections.Select(x => x.Area.ToString("0.###")))}, box {string.Join(" ", b.Vertices.Select(v => v.P).Aggregate((a, c) => Vector3d.ComponentMin(a, c)))} .. {b.Vertices.Select(v => v.P).Aggregate((a, c) => Vector3d.ComponentMax(a, c))}, prism {pr.Volume:R}, polyhedral {vm:R}, D {pr.D}"; }
                        continue;
                    }
                    double err = Math.Abs(pr.Volume - av.Value) / Math.Max(1, Math.Abs(av.Value));
                    if (err > prWorst) { prWorst = err; prWorstCase = $"faces {b.Faces.Count()}, levels {pr.Levels.Length}, prism {pr.Volume:R}, analytic {av.Value:R}"; }
                    if (pr.HealShift > 1e-6) { prHealed++; prHealMax = Math.Max(prHealMax, pr.HealShift); if (err >= 1e-6) prHealedDiff++; continue; }
                    prErrs.Add(err);
                    // Порог 1e-6: вершины входа в узлах сетки точного ядра (1e-5 мм) — на деталях ~100 мм это ~1e-7 относительно.
                    if (err < 1e-6) prOk++; else
                    {
                        prBad++;
                        if (prBadCases.Count < 4)
                        {
                            var caps = b.Faces.Select(f => (F: f, Sh: Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))))
                                .Where(t => t.Sh is Parametric3D.Kernel.Geometry.Ssi.Plane pl && Math.Abs(Math.Abs(Vector3d.Dot(pl.N.Normalized(), pr.D)) - 1) < 1e-9)
                                .Select(t => { var r0 = pr.FaceRegion(t.F, out _); var p0 = t.F.Loops[0].CoEdges[0].Start.P;
                                               return $"[h {pr.H(p0):0.###} {(Vector3d.Dot(t.F.NormalAt(p0), pr.D) > 0 ? "up" : "down")} area {r0?.Area:0.###} loops {t.F.Loops.Count} surf {t.F.Surface.GetType().Name} rev {t.F.Reversed}]"; });
                            prBadCases.Add($"faces {b.Faces.Count()} D {pr.D} levels [{string.Join(",", pr.Levels.Select(x => x.ToString("0.###")))}] sections [{string.Join(",", pr.Sections.Select(x => x.Area.ToString("0.###")))}] analytic {av.Value:0.###} caps {string.Join(" ", caps)}");
                        }
                    }
                }
            };
        // Разбор общей булевой: первый отказ с подстрокой GENCAPTURE — операнды и граф грани в конце прогона.
        TSolid? capA = null, capB = null; BooleanOp.BooleanKind capK = default; string? capWhy = null; string? capTest = null;
        var capWanted = Environment.GetEnvironmentVariable("GENCAPTURE");
        if (capWanted != null)
            BooleanOp.GeneralDeclineProbe = (x, y, k, w) => { if (capA == null && w.Contains(capWanted)) { capA = TopologyClone.Deep(x); capB = TopologyClone.Deep(y); capK = k; capWhy = w; capTest = CurrentCheck; } };
        Action<TSolid, TSolid, BooleanOp.BooleanKind, TSolid>? probeSelf = null;
        if (Environment.GetEnvironmentVariable("GENVERIFY") == "1")
            BooleanOp.GeneralAcceptProbe = probeSelf = (x, y, k, r) =>
            {
                // тот же вызов многогранным путём (оба аналитических маршрута выключены) — сравнение объёмов
                var ax = TopologyClone.Deep(x); var ay = TopologyClone.Deep(y);
                BooleanOp.GeneralAcceptProbe = null;
                try
                {
                    using var _c = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticPrism = false, AnalyticGeneral = false }).Enter();
                    var w = new List<string>(); var lst = new List<TSolid> { ax };
                    if (k == BooleanOp.BooleanKind.Subtract) BooleanOp.Subtract(lst, ay, w); else if (k == BooleanOp.BooleanKind.Unite) BooleanOp.Unite(lst, ay, w); else BooleanOp.Intersect(lst, ay, w);
                    double vp = lst.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), vg = AnalyticVolume.Of(r) ?? double.NaN;
                    if (Math.Abs(vp - vg) > 1e-5 * Math.Max(1, Math.Abs(vp)))
                        Console.WriteLine($"GV MISMATCH in {CurrentCheck}: {k} general {vg:R} vs polyhedral {vp:R} (diff {vg - vp:G4}); A faces {x.Faces.Count()} B faces {y.Faces.Count()} B surfaces [{string.Join(",", y.Faces.Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name))}]");
                }
                catch (Exception e) { Console.WriteLine("GV polyhedral failed: " + e.Message.Substring(0, Math.Min(120, e.Message.Length))); }
                finally { BooleanOp.GeneralAcceptProbe = probeSelf; }
            };
        var tangentSeen = new HashSet<string>();
        if (Environment.GetEnvironmentVariable("TANGENTDEBUG") == "1") Parametric3D.Kernel.Analytic.GeneralBoolean.TangentDebug = m => { if (tangentSeen.Add(m)) Console.WriteLine("TD " + m); };
        var localStats = new Dictionary<string, int>();
        if (Environment.GetEnvironmentVariable("LOCALSURVEY") == "1")
            BooleanOp.EvaluateProbe = (bodies, other, kind) =>
            {
                if (bodies.Count != 1) return;
                var body = bodies[0];
                if (Parametric3D.Kernel.Analytic.Prism25.Recognize(other, out _) is not { } pt) { localStats["tool not 2.5D"] = localStats.GetValueOrDefault("tool not 2.5D") + 1; return; }
                if (Parametric3D.Kernel.Analytic.Prism25.RecognizeAlong(body, pt.D, pt.U, pt.V, out _) != null) { localStats["body 2.5D along tool (stage 1)"] = localStats.GetValueOrDefault("body 2.5D along tool (stage 1)") + 1; return; }
                bool local = Parametric3D.Kernel.Analytic.Prism25.LocallyPrismatic(body, other, pt.D, pt.U, pt.V, out int blocking);
                var k = local ? "body locally 2.5D around the tool" : "blocked near the tool";
                localStats[k] = localStats.GetValueOrDefault(k) + 1;
            };
        var routeStats = new Dictionary<string, int>();
        if (Environment.GetEnvironmentVariable("ANROUTE") == "1")
            BooleanOp.AnalyticRouteDebug = m =>
            {
                var key = System.Text.RegularExpressions.Regex.Replace(m, @"[-0-9.]+(E[-+]?\d+)?", "#");
                routeStats[key] = routeStats.GetValueOrDefault(key) + 1;
            };
        // KRN-00: итог каждой булевой (один вызов Evaluate — одна запись) — общий знаменатель долей маршрутов.
        var outcomeStats = new Dictionary<string, int>();
        if (Environment.GetEnvironmentVariable("ANROUTE") == "1")
            BooleanOp.EvaluateOutcomeProbe = m => { var key = System.Text.RegularExpressions.Regex.Replace(m, @"[-0-9.]+", "#"); outcomeStats[key] = outcomeStats.GetValueOrDefault(key) + 1; };
        var histStats = new Dictionary<string, int>(); var histDisagree = new List<string>();
        if (Environment.GetEnvironmentVariable("HISTDBG") == "1")
            Parametric3D.Model.FeatureHistory.CompareDebug = m =>
            {
                string kind = m.Split(' ')[0]; histStats[kind] = histStats.GetValueOrDefault(kind) + 1;
                if (kind == "DISAGREE" && histDisagree.Count < 20) histDisagree.Add(m);
            };
        // Воспроизводимые журналы: ключи операций по счётчику (иначе устойчивые имена случайны от запуска к запуску).
        if (Environment.GetEnvironmentVariable("P3D_DETERMINISTIC_KEYS") != "0") Parametric3D.Model.Features.Feature.DeterministicKeys = true;
        if (Environment.GetEnvironmentVariable("P3D_EXV") == "0") Parametric3D.Kernel.Geometry.ExactEdges.SnapVertices = false;
        if (Environment.GetEnvironmentVariable("P3D_EXC") == "0") Parametric3D.Kernel.Geometry.ExactEdges.ReplaceCurves = false;
        if (Environment.GetEnvironmentVariable("P3D_UVMESH") == "1") Parametric3D.Kernel.Tess.ExactFaceMesher.UseUvMesher = true;
        // Полный прогон разделён на две части: целиком он перестал укладываться в предел одной
        // команды среды (300 с). allA — K01–K06, allB — K07–K10 и G.
        if (mode is "one")   // диагностика: один сценарий по идентификатору, трасса нового входа по PLANARDBG=1
        {
            if (Environment.GetEnvironmentVariable("PLANARDBG") == "1")
                Parametric3D.Kernel.Exact.ExactPlanarInput.Trace = m => Console.WriteLine("PLANAR " + m);
            var ww = new List<string>();
            try { var bb = Scenario(args[1], ww); Console.WriteLine($"ok volume {bb[0].Exact?.Solid.VolumeMm3().ToDouble()}"); }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); }
            Console.WriteLine(string.Join(" | ", ww));
            return;
        }
        if (mode is "sphere" or "allA") Sphere();
        if (mode is "negative" or "allA") Negative();
        if (mode is "selfx2" or "negative" or "allA") SelfXChecks();
        if (mode is "perftargets") { PerfTargets(args[1]); return; }
        if (mode is "perf") { Perf(args); return; }
        if (mode is "meshreuse") { MeshReuseExperiment(); MeshReuseIdentity(); }
        if (mode is "globals") { foreach (var g in UnboundGlobals()) Console.WriteLine(g); return; }
        if (mode is "guarantees" or "allA") Guarantees();
        if (mode is "guarantees" or "allA") Representations();
        if (mode is "survey" or "allS") Survey(false);
        if (mode is "selfx" or "allS") Survey(true);
        if (mode is "krn11long" or "allS") Krn11LongSessionChecks();
        if (mode is "krn02" or "allA") Krn02();
        if (mode is "krn03" or "allA") Krn03();
        if (mode is "krn05" or "allA") Krn05();
        if (mode is "selfid") SelfIdentities();
        if (mode is "krn07" or "allA") Krn07();
        if (mode is "krn07" or "krn07b" or "allA") Krn07bChecks();
        if (mode is "krn07" or "krn07c" or "allA") Krn07cChecks();
        if (mode is "krn09" or "allA") Krn09Checks();
        if (mode is "krn01b" or "allA") Krn01bChecks();
        if (mode is "krn04seam" or "allA") Krn04SeamChecks();
        if (mode is "krn02uv" or "allA") Krn02UvMetricChecks();
        if (mode is "krn01ent" or "allA") Krn01EntitiesChecks();
        if (mode is "krn09d" or "allA") Krn09DisplayFaultChecks();
        if (mode is "krn05t" or "allA") Krn05TouchChecks();
        if (mode is "prov" or "allA") ProvenanceChecks();
        if (mode is "query" or "allA") QueryChecks();
        if (mode is "selfone")
        {
            var rnd = new Random(20261011); TSolid a = null!; string d = "";
            if (args[1].StartsWith("S-")) { a = Scenario(args[1], new List<string>())[0]; d = args[1]; }
            else for (int i = 0; i <= int.Parse(args[1]); i++) a = IdentityPrim(rnd, out d);
            var k = args[2] == "u" ? BooleanOp.BooleanKind.Unite : args[2] == "i" ? BooleanOp.BooleanKind.Intersect : BooleanOp.BooleanKind.Subtract;
            BooleanOp.AnalyticRouteDebug = m => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} route: " + m);
            Parametric3D.Kernel.Analytic.GeneralBoolean.EmptyDebug = m => Console.WriteLine("empty: " + m);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = BooleanOp.Evaluate(new List<TSolid> { a }, TransformOp.Copy(a, TransformOp.Move(Vector3d.Zero)), k, OperationPolicy.Strict);
            Console.WriteLine($"{d} {k}: {r.Outcome} {r.Describe()} in {sw.Elapsed.TotalSeconds:F2} s");
        }
        if (mode is "torus" or "allA") TorusRouteChecks();
        if (mode is "longchain" or "allA") LongChainChecks();
        if (mode is "longhist" or "allA") LongHistoryChecks();
        if (mode is "dround" or "allA") DirectRoundChecksRun();
        if (mode is "dround" or "krn06acc" or "allA") Krn06AcceptanceChecks();
        if (mode is "krn06guard" or "allB") Krn06FallbackGuardChecks();
        if (mode is "krn06cc" or "allB") Krn06CylCylChecks();
        if (mode is "krn06q" or "allB") Krn06QuadricChecks();
        if (mode is "krn06ccpart" or "allB") Krn06CylCylPartChecks();
        if (mode is "krn06step" or "allB") Krn06BlendStepChecks();
        if (mode is "krn05blend" or "allB") Krn05AfterBlendChecks();
        if (mode is "krn11hist" or "allB") Krn11HistoryChecks();
        if (mode is "krn05unite" or "allB") Krn05UniteChainChecks();
        if (mode is "krn06var" or "allB") Krn06VariableChecks();
        if (mode is "krn06uneq" or "allA") Krn06UnequalChecks();
        if (mode is "krn06tan" or "allA") Krn06TanLoopChecks();
        if (mode is "krn10prev" or "allA") Krn10PreviewChecks();
        if (mode is "krn03comp" or "allA") Krn03FaceCompChecks();
        if (mode is "krn07hole" or "allA") Krn07HoleContractChecks();
        if (mode is "krn07inst" or "allA") Krn07InstanceChecks();
        if (mode is "krn10pairs" or "allA") Krn10PairChecks();
        if (mode is "krn09heal" or "allA") Krn09ImportHealChecks();
        if (mode == "swprobe") { SweepLoftProbe(); return; }
        if (mode == "patprobe") { PatternProbe(); return; }
        if (mode == "serprobe") { SerProbe(); return; }
        if (mode == "thinprobe") { ThinProbe(); return; }
        if (mode == "touchprobe") { TouchProbe(); return; }
        if (mode == "avfar") { AvFarProbe(); AvFarScene(); return; }
        if (mode == "cylcyl") { CylCylProbe(); return; }
        if (mode == "rbvol") { RollingBallProbe(); return; }
        if (mode == "topo") { TopoProbe(); return; }
        if (mode == "ccvar") { CylCylVariants(); return; }
        if (mode == "ccpost") { CylCylPost(); return; }
        if (mode == "seamprobe") { SeamProbe(); return; }
        if (mode == "qvar") { QuadricVariants(); return; }
        if (mode == "conemetric") { ConeMetricProbe(); return; }
        if (mode == "holecontract") { HoleContractProbe(); return; }
        if (mode == "instprobe") { InstanceProbe(); return; }
        if (mode == "tweakprobe") { TweakProbe(); return; }
        if (mode == "provprobe") { ProvProbe(); return; }
        if (mode == "fcprobe") { FaceCompProbe(); return; }
        if (mode == "ihprobe") { ImportHealProbe(); return; }
        if (mode == "vrprobe") { VarRoundProbe(); return; }
        if (mode == "uqprobe") { UneqProbe(); return; }
        if (mode == "dcprobe") { DrillCompProbe(); return; }
        if (mode == "cylfaceprobe") { CylFaceProbe(); return; }
        if (mode == "igesprobe")
        {
            var rot = TransformOp.Rotate(new Vector3d(3, -1, 2), new Vector3d(0.2, 0.9, -0.4).Normalized(), 53);
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), new List<string>());
            foreach (var b0 in new[] { bl[0], TransformOp.Copy(bl[0], rot) })
            {
                var wi = new List<string>(); var bi = Parametric3D.Kernel.Export.IgesImport.Read(Parametric3D.Kernel.Export.IgesSolid.Write(b0, wi), wi);
                var vr = bi == null ? null : SolidValidator.Validate(bi);
                Console.WriteLine($"v0 {AnalyticVolume.Of(b0)} back {(bi == null ? "null" : (AnalyticVolume.Of(bi)?.ToString() ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bi)).ToString()))} valid {vr?.IsValid} {vr?.Summary()}");
                if (vr != null) foreach (var x in vr.Issues.Take(4)) Console.WriteLine("  " + x);
                foreach (var x in wi) Console.WriteLine("  W " + x);
            }
        }
        if (mode == "topodump")
        {
            var wd = new List<string>(); TSolid bd;
            if (args.Length > 1 && args[1] == "shaft") { var bl0 = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Unite(bl0, TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 20, 20), 8, 60), TransformOp.Rotate(new Vector3d(0, 20, 20), Vector3d.UnitY, 90)), wd); bd = bl0[0]; }
            else bd = Scenario(args.Length > 1 ? args[1] : "S-HOLE", wd)[0];
            foreach (var f in bd.Faces)
            {
                Console.WriteLine($"F {f.Tag} {f.Surface.GetType().Name}/{GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} rev {f.Reversed} loops {f.Loops.Count}");
                foreach (var l in f.Loops)
                    foreach (var ce in l.CoEdges)
                        Console.WriteLine($"   ce {ce.Edge.Curve.GetType().Name} {ce.Edge.V0.P:0.###}->{ce.Edge.V1.P:0.###} closed {ReferenceEquals(ce.Edge.V0, ce.Edge.V1)} rev {ce.Reversed} uses {ce.Edge.CoEdges.Count} tag {ce.Edge.Tag}");
            }
        }
        if (mode == "drstep")
        {
            var w3 = new List<string>(); var b3 = Scenario(args.Length > 1 ? args[1] : "S-ROUND", w3)[0]; var sw3 = new StringWriter();
            Parametric3D.Kernel.Export.StepExact.Write(sw3, new[] { b3 }, "x", w3);
            File.WriteAllText("/tmp/drstep.step", sw3.ToString());
            var back3 = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw3.ToString(), w3);
            Console.WriteLine($"faces {b3.Faces.Count()} back {back3.Count}");
            foreach (var x in w3) Console.WriteLine("W " + x);
            foreach (var f in b3.Faces) Console.WriteLine($"F {f.Tag} {f.Surface.GetType().Name} {(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name)}");
        }
        if (mode is "krn05lens") Krn05Lens();
        if (mode is "krn04probe") Krn04Probe();
        if (mode is "krn04" or "allA") Krn04Checks();
        if (mode is "krn08" or "allA") Krn08Checks();
        if (mode is "krn10" or "allA") Krn10Checks();
        if (mode is "g11poly") G11PolyChecks();
        if (mode is "krn04one") Krn04One(int.Parse(args[1]));
        if (mode is "krn04ring") Krn04Ring();
        if (mode is "krn04raw") Krn04Raw(args.Length > 1 ? int.Parse(args[1]) : -1);
        if (mode is "krn04poly") Krn04Poly(int.Parse(args[1]));
        if (mode is "stepop") { StepOp(args); return; }
        if (mode is "steprt") { StepRoundTrip(); return; }
        if (mode is "conetip") { ConeTipMesh(); return; }
        if (mode is "coneprobe") { ConeProbe(); return; }
        if (mode is "metaprobe") { MetaProbe(); return; }
        if (mode is "krn04dump") Krn04Dump(args.Length > 1 ? args[1] : "krn04");
        if (mode is "g04cs") { G04ConeSphere(); return; }
        if (mode is "g11" or "allA") G11Checks();
        if (mode is "g04" or "allA") G04Checks();
        if (mode is "g11far") { var ff = G11Display(double.Parse(args[1]), out int nb); Console.WriteLine($"bodies {nb}, fails {ff.Count}"); foreach (var x in ff.Take(20)) Console.WriteLine("  " + x); return; }
        if (mode is "krn05id") { var fl = BooleanIdentities(int.Parse(args[1]), int.Parse(args[2]), out int np); Console.WriteLine($"pairs {np}, broken {fl.Count}"); foreach (var x in fl) Console.WriteLine("  " + x); return; }
        if (mode is "krn01g" or "allA") Krn01Gates();
        if (mode is "krn01g03") Krn01MeasureG03();
        if (mode is "primcheck") { PrimCheck(args[1]); return; }
        if (mode is "diffdump") { DiffDump(args[1], int.Parse(args[2]), int.Parse(args[3])); return; }
        if (mode is "g14s") { G14Scenario(args[1], args[2]); return; }
        if (mode is "g14x") { G14Explain(args[1]); return; }
        if (mode is "g14dump") { G14Dump(args.Length > 1 ? args[1] : "g14out"); return; }
        if (mode is "krn03m") Krn03Measure();
        if (mode is "krn03p") Krn03MeasurePCurves();
        if (mode is "krn03v") Krn03MeasurePCurveValidation();
        if (mode is "krn03bf") Krn03MeasureBigFar();
        if (mode is "krn03t") Krn03MeasureConeTip();
        if (mode is "krn03dt") Krn03MeasureDrillTopology();
        if (mode is "krn03dv") Krn03MeasureDrillVolume();
        if (mode is "krn03fc") Krn03MeasureFarClassifier();
        if (mode is "krn03ot") Krn03MeasureObliqueTool();
        if (mode is "krn03s") Krn03MeasureSpheres();
        if (mode is "krn03vc") Krn03MeasureConcaveRound();
        if (mode is "krn03tor") Krn03MeasureTorus();
        if (mode is "krn03c") Krn03MeasureConnectivity();
        if (mode is "krn03mb") Krn03MeasureMultibody();
        if (mode is "krn02m") { Krn02Measure(); Krn02MeasureRevolved(); }
        if (mode is "krn02meta") Krn02MeasureMetamorphic();
        if (mode is "krn02far") Krn02MeasureFar();
        if (mode is "krn02far1") Krn02MeasureFarOpen();
        if (mode is "krn02farc") Krn02MeasureFarClassify();
        if (mode is "krn02s") Check("KRN02-SMALL-FAR", () => Krn02SmallFar());
        if (mode is "contract" or "allA") Contract();
        if (mode is "bench") Bench();
        if (mode is "k03" or "allA") K03();
        if (mode is "k04" or "allA") K04();
        if (mode is "k05survey") K05Survey();
        if (mode is "k05" or "allA") { K05(); K05History(); }
        if (mode is "stepprobe") StepProbe();
        if (mode is "k06" or "allA") { K06(); K06Datums(); }
        if (mode is "k06bench") K06Bench();
        if (mode is "drill" or "allB") { K07Drill(); K07Boss(); }
        if (mode is "meshreuse2" or "allB") MeshReuseIdentity();
        if (mode is "histexact" or "allB") HistoryExact();
        if (mode is "holeroutes" or "allB") HoleRoutes();
        if (mode is "curvedregions" or "allB") CurvedRegions();
        if (mode is "triangulator" or "allB") TriangulatorBridges();
        if (mode is "analytic2d" or "allA") Analytic2D();
        if (mode is "prism25" or "allB") Prism25Checks();
        if (mode is "prismbool" or "allB") PrismBooleanChecks();
        if (mode is "analyticroute" or "allB") AnalyticRouteContract();
        if (mode is "classifier" or "allB") ClassifierChecks();
        if (mode is "faceintersect" or "allB") FaceIntersectChecks();
        if (mode is "facesplit" or "allB") FaceSplitChecks();
        if (mode is "generalbool" or "allB") GeneralBooleanChecks();
        if (mode is "racedbg")
        {
            var p = RealisticPart(8001, 6, out _); p.Rebuild(null);
            var before = p.Bodies.ToList();
            double chord0 = Parametric3D.Core.Tol.Chord;
            Parametric3D.Core.Tol.Chord = chord0 * 0.5;
            try
            {
                p.Rebuild(null);
                Console.WriteLine($"RACE same objects after chord change: {string.Join(",", p.Bodies.Select((b, i) => i < before.Count && ReferenceEquals(b, before[i])))}; published before {string.Join(",", before.Select(b => b.IsPublished))}");
                p.MarkDirtyFrom(p.Features[^1]); p.Rebuild(null);
                var before2 = p.Bodies.ToList();
                Parametric3D.Core.Tol.Chord = chord0;
                p.Rebuild(null);
                Console.WriteLine($"RACE same objects after second change: {string.Join(",", p.Bodies.Select((b, i) => i < before2.Count && ReferenceEquals(b, before2[i])))}");
            }
            finally { Parametric3D.Core.Tol.Chord = chord0; }
            return;
        }
        if (mode is "emptydbg")
        {
            foreach (var (name, a, b, k) in new (string, TSolid, TSolid, BooleanOp.BooleanKind)[] {
                ("intersect disjoint", Box(), Box(100, 0, 0), BooleanOp.BooleanKind.Intersect),
                ("subtract by enclosing", Box(), Box(-10, -10, -10, 80, 60, 40), BooleanOp.BooleanKind.Subtract),
                ("unite disjoint", Box(), Box(100, 0, 0), BooleanOp.BooleanKind.Unite),
                ("subtract disjoint", Box(), Box(100, 0, 0), BooleanOp.BooleanKind.Subtract) })
            {
                var r = BooleanOp.Evaluate(new List<TSolid> { a }, b, k, OperationPolicy.Strict);
                Console.WriteLine($"ED {name}: status {r.Status} guarantee {r.Guarantee} bodies {r.Bodies.Count} vol [{string.Join(",", r.Bodies.Select(x => (AnalyticVolume.Of(x) ?? double.NaN).ToString("0.###")))}] fail {r.FailureCode} | {string.Join(" | ", r.Diagnostics.Take(2)).Replace("\n", " ")}");
            }
            return;
        }
        if (mode is "displaydep")
        {
            // KRN-01: воспроизводитель — меняет ли допуск хорды ОТОБРАЖЕНИЯ геометрию следующей CAD-операции.
            foreach (double chord in new[] { 0.02, 0.005 })
            {
                using var _c = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { Chord = chord }).Enter();
                var w = new List<string>();
                var tilt = TransformOp.Copy(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, -20), 5, 60), TransformOp.Rotate(new Vector3d(30, 20, 10), Vector3d.UnitY, 17));
                var r1 = BooleanOp.Evaluate(new List<TSolid> { Box() }, tilt, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w);
                var r2 = BooleanOp.Evaluate(r1.Bodies.ToList(), Parametric3D.Kernel.Ops.PrimitiveOp.Sphere(new Vector3d(55, 35, 20), 8), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w);
                double v1 = r1.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), v2 = r2.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN);
                Console.WriteLine($"DD chord {chord}: op1 {r1.Outcome} {r1.Guarantee} vol {v1:R}; op2 {r2.Outcome} {r2.Guarantee} vol {v2:R} faces {r2.Bodies.Sum(b => b.Faces.Count())}");
            }
            return;
        }
        if (mode is "settings")
        {
            // KRN-00: эффективные настройки ядра для манифеста выпуска — снимок контекста, неизменяемые флаги экспериментов
            // и заданные переменные окружения P3D_*.
            var snap = Parametric3D.Kernel.KernelSettings.Capture();
            var settings = new SortedDictionary<string, object?>();
            foreach (var pr in snap.GetType().GetProperties().OrderBy(x => x.Name)) settings[pr.Name] = pr.GetValue(snap)?.ToString();
            var flags = new SortedDictionary<string, object?>
            {
                ["BooleanOp.AnalyticSpheres"] = BooleanOp.AnalyticSpheres,
                ["BooleanOp.LazyFine"] = BooleanOp.LazyFine,
                ["GeneralBoolean.AllowTangentAlongEdge"] = Parametric3D.Kernel.Analytic.GeneralBoolean.AllowTangentAlongEdge,
                ["FilletOp.NoClaimedOverlap"] = Parametric3D.Kernel.Ops.FilletOp.NoClaimedOverlap,
            };
            var env = new SortedDictionary<string, string?>();
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                if (e.Key is string k && k.StartsWith("P3D_")) env[k] = e.Value?.ToString();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                cpus = Environment.ProcessorCount,
                kernelSettings = settings, experimentFlags = flags, environment = env,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (mode is "gendbg")
        {
            TSolid? ba = null, bb = null; BooleanOp.BooleanKind kk = default; string? reason = null;
            BooleanOp.GeneralDeclineProbe = (x, y, k, w) => { if (ba == null && w.Contains(args.Length > 1 ? args[1] : "used piece")) { ba = TopologyClone.Deep(x); bb = TopologyClone.Deep(y); kk = k; reason = w; } };
            using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticGeneral = true }).Enter())
            { var p = RealisticPart(int.Parse(args.Length > 2 ? args[2] : "8001"), 20, out _); p.Rebuild(null); }
            BooleanOp.GeneralDeclineProbe = null;
            Console.WriteLine($"GD captured: {reason}; kind {kk}; A faces {ba?.Faces.Count()} B faces {bb?.Faces.Count()}");
            if (ba == null) return;
            foreach (var f in bb!.Faces) Console.WriteLine($"GD  B face {f.Tag} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))}");
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("GD " + m);
            var r = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(ba, bb, kk == BooleanOp.BooleanKind.Unite ? Parametric3D.Kernel.Analytic.Bool2.Union : kk == BooleanOp.BooleanKind.Subtract ? Parametric3D.Kernel.Analytic.Bool2.Subtract : Parametric3D.Kernel.Analytic.Bool2.Intersect, out var why2);
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = null;
            Console.WriteLine("GD rerun: " + (r == null ? why2 : "ok"));
            return;
        }
        if (mode is "k09dbg")
        {
            foreach (bool gen in new[] { false, true })
            {
                using var _c = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticGeneral = gen }).Enter();
                var rl = new List<string>(); BooleanOp.AnalyticRouteDebug = m => rl.Add(m);
                if (gen) BooleanOp.EvaluateProbe = (bs, oth, kd) =>
                {
                    var near = oth.Vertices.Where(v => (v.P - new Vector3d(0, 0, 9)).Length < 1e-3).Select(v => v.P.ToString()).ToArray();
                    var nearB = bs.SelectMany(x => x.Vertices).Where(v => (v.P - new Vector3d(0, 0, 9)).Length < 1e-3).Select(v => v.P.ToString()).ToArray();
                    Console.WriteLine($"K9   boolean {kd}: tool vertices near (0,0,9): [{string.Join(" ", near)}] body: [{string.Join(" ", nearB)}] tool faces {oth.Faces.Count()}");
                    if (kd == BooleanOp.BooleanKind.Unite)
                    {
                        var A = bs[0]; var fa = A.Faces.Select(f => Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out _)).ToList(); var fb = oth.Faces.Select(f => Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out _)).ToList();
                        foreach (var x in fa) foreach (var y in fb)
                        {
                            if (x == null || y == null) continue;
                            var r = Parametric3D.Kernel.Analytic.FaceIntersect.Of(x, y);
                            foreach (var pc in r.Pieces) foreach (var e in new[] { pc.P0, pc.P1 })
                                if ((e - new Vector3d(0, 0, 9)).Length < 1e-3) Console.WriteLine($"K9     piece end {e} (off {(e - new Vector3d(0, 0, 9)).Length:G3}) faces {x.Face.Tag} × {y.Face.Tag} curve {pc.Curve.GetType().Name}");
                        }
                    }
                };
                var b = new List<TSolid> { Box(-30, -30, -30, 60, 60, 60) }; var w = new List<string>();
                BooleanOp.Subtract(b, Box(0, 0, 0, 40, 40, 40), w);
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
                var c = Parametric3D.Kernel.Ops.VertexOp.Find(b[0], Vector3d.Zero);
                var d = Parametric3D.Kernel.Ops.VertexOp.MatchDistances(c, new List<(Vector3d, double)> { (new Vector3d(2, 0, 0), 4), (new Vector3d(0, 2, 0), 6), (new Vector3d(0, 0, 2), 9) });
                if (gen) Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = (m, q) => { if ((q - new Vector3d(0, 0, 9)).Length < 1e-3) Console.WriteLine($"K9     {m}: {q.X:R},{q.Y:R},{q.Z:R}"); };
                Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, Vector3d.Zero, d, w);
                Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = null;
                BooleanOp.AnalyticRouteDebug = null; BooleanOp.EvaluateProbe = null;
                double v1 = AnalyticVolume.Of(b[0]) ?? double.NaN;
                Console.WriteLine($"K9 general={gen}: added {v1 - v0:R} route [{string.Join(" | ", rl)}] guarantee {b[0].Guarantee}");
                // каждая вершина — на плоскостях всех своих граней
                double worst = 0; string? where = null;
                foreach (var f in b[0].Faces)
                {
                    if (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is not Parametric3D.Kernel.Geometry.Ssi.Plane pl) continue;
                    var n = pl.N.Normalized();
                    foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
                    {
                        double dd = Math.Abs(Vector3d.Dot(ce.Start.P - pl.P, n));
                        if (dd > worst) { worst = dd; where = $"vertex {ce.Start.P} off plane n {n} of face {f.Tag}"; }
                    }
                }
                Console.WriteLine($"K9   worst vertex off its plane: {worst:G3} {where}");
            }
            return;
        }
        if (mode is "sphdbg")
        {
            var sph = Parametric3D.Kernel.Ops.PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8);
            Console.WriteLine($"SD sphere faces {sph.Faces.Count()} convention {sph.TopologyConvention}");
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("SD   " + m);
            foreach (var op in new[] { Parametric3D.Kernel.Analytic.Bool2.Subtract, Parametric3D.Kernel.Analytic.Bool2.Union })
            {
                Console.WriteLine($"SD op {op}");
                var rr = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(Box(), Parametric3D.Kernel.Ops.PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), op, out var wy);
                if (rr != null)
                {
                    foreach (var f in rr.Faces.Where(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Sphere))
                    {
                        var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0);
                        var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                        Console.WriteLine($"SD   result sphere face rev {f.Reversed} loop normal z {nw.Normalized().Z:0.###} normalAt top {f.NormalAt(new Vector3d(30, 20, 28)).Z:0.###} bottom {f.NormalAt(new Vector3d(30, 20, 12)).Z:0.###}");
                    }
                    var norm = Parametric3D.Kernel.Topology.TopologyConventions.Normalize(rr);
                    Console.WriteLine($"SD   normalize: consistent {norm.Consistent} problems [{string.Join(" | ", norm.Problems.Take(3))}] unanchored {norm.Unanchored} flipped? see below");
                    foreach (var f in rr.Faces.Where(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Sphere))
                    {
                        var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0);
                        var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                        Console.WriteLine($"SD   after normalize: rev {f.Reversed} loop normal z {nw.Normalized().Z:0.###}");
                    }
                    double CapZ() { var f = rr.Faces.First(x => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(x)) is Parametric3D.Kernel.Geometry.Ssi.Sphere); return f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs && fs.Facets.Count > 0 ? fs.Facets.ReadView.Average(t => (t.A.Z + t.B.Z + t.C.Z) / 3) : double.NaN; }
                    Parametric3D.Kernel.Export.StepImport.MeshFacesFromLoops(rr, null, "dbg");
                    Console.WriteLine($"SD   after mesh: cap mean z {CapZ():0.###}");
                    Parametric3D.Kernel.Export.StepImport.OrientShellPublic(rr);
                    Console.WriteLine($"SD   after orient: cap mean z {CapZ():0.###} sphere rev {rr.Faces.First(x => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(x)) is Parametric3D.Kernel.Geometry.Ssi.Sphere).Reversed}");
                    Console.WriteLine($"SD   volume after Finish {AnalyticVolume.Of(rr)}");
                    foreach (var f in rr.Faces) Console.WriteLine($"SD     face {f.Tag,-28} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name,-16} rev {f.Reversed,-5} integral {AnalyticVolume.FaceIntegral(f):R}");
                }
            }
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = null;
            return;
            foreach (var f in sph.Faces)
            {
                var af = Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out var why);
                Console.WriteLine($"SD  face {f.Tag} shape {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))} surf {f.Surface.GetType().Name} loops {f.Loops.Count} coedges {string.Join(",", f.Loops.Select(l => l.CoEdges.Count))} af {(af == null ? why : $"sph {af.Sph} full {af.FullSurface} bounds {af.Bounds.Count}")}");
                var top = Box().Faces.First(g => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(g)) is Parametric3D.Kernel.Geometry.Ssi.Plane pl && pl.N.Normalized().Z > 0.99 && Math.Abs(pl.P.Z - 20) < 1e-9);
                var at = Parametric3D.Kernel.Analytic.AnalyticFace.Of(top, out _)!;
                if (af != null)
                {
                    var ssi = Parametric3D.Kernel.Geometry.Ssi.Intersect(at.Shape, af.Shape, new Vector3d(30, 20, 20), 50);
                    Console.WriteLine($"SD   ssi {ssi}");
                    var r = Parametric3D.Kernel.Analytic.FaceIntersect.Of(at, af);
                    Console.WriteLine($"SD   faceintersect {r}; contains sample {af.Contains(new Vector3d(38, 20, 20))} bd {af.SignedBoundaryDistance(new Vector3d(38, 20, 20))}");
                }
            }
            return;
        }
        if (mode is "r6dbg")
        {
            var rl = new List<string>(); BooleanOp.AnalyticRouteDebug = m => rl.Add(m);
            TSolid? ca = null, cb = null; BooleanOp.BooleanKind ck = default;
            BooleanOp.GeneralDeclineProbe = (x, y, k, w) => { if (ca == null && w.StartsWith("invalid")) { ca = TopologyClone.Deep(x); cb = TopologyClone.Deep(y); ck = k; } };
            var p = new Parametric3D.Model.Part("v"); Parametric3D.Model.TestGeometry.AddCube(p, 60); p.Rebuild(null);
            rl.Clear();
            p.AddFeature(new Parametric3D.Model.Features.VertexRoundFeature { Vertex = new Vector3d(30, 30, 30), Radius = new Parametric3D.Model.Features.ValueRef(6) });
            var r = p.Compute(null); p.Apply(r);
            BooleanOp.AnalyticRouteDebug = null;
            Console.WriteLine("R6 routes: " + string.Join(" | ", rl));
            var b = p.Bodies[0];
            var v = SolidValidator.Validate(b);
            Console.WriteLine($"R6 verdict {v.Summary()} guarantee {b.Guarantee} faces {b.Faces.Count()}");
            foreach (var i in v.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(6)) Console.WriteLine($"R6   issue {i}");
            BooleanOp.GeneralDeclineProbe = null;
            if (ca != null)
            {
                Console.WriteLine($"R6 captured invalid op: {ck}; A faces [{string.Join(",", ca.Faces.Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name))}] B faces [{string.Join(",", cb!.Faces.Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name))}]");
                var rr = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(ca, cb, ck == BooleanOp.BooleanKind.Unite ? Parametric3D.Kernel.Analytic.Bool2.Union : ck == BooleanOp.BooleanKind.Subtract ? Parametric3D.Kernel.Analytic.Bool2.Subtract : Parametric3D.Kernel.Analytic.Bool2.Intersect, out var wy);
                if (rr != null)
                {
                    foreach (var f in rr.Faces)
                    {
                        var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0);
                        var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                        var mid = pts.Aggregate(Vector3d.Zero, (a, q) => a + q) / pts.Count;
                        var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
                        var at = sh is Parametric3D.Kernel.Geometry.Ssi.Sphere sp ? sp.C + (mid - sp.C).Normalized() * sp.R : mid;
                        Console.WriteLine($"R6     pre-finish {f.Tag} {sh?.GetType().Name}: loop·normal {Vector3d.Dot(nw.Normalized(), f.NormalAt(at)):0.###} rev {f.Reversed} radial·normal {(sh is Parametric3D.Kernel.Geometry.Ssi.Sphere sp2 ? Vector3d.Dot((at - sp2.C).Normalized(), f.NormalAt(at)) : 0):0.###}");
                    }
                    var nrep = Parametric3D.Kernel.Topology.TopologyConventions.Normalize(rr);
                    foreach (var f in rr.Faces.Where(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Sphere))
                    {
                        var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0);
                        var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                        var sp = (Parametric3D.Kernel.Geometry.Ssi.Sphere)Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))!;
                        var mid = pts.Aggregate(Vector3d.Zero, (a, q) => a + q) / pts.Count; var at = sp.C + (mid - sp.C).Normalized() * sp.R;
                        Console.WriteLine($"R6     after normalize (consistent {nrep.Consistent}): loop·normal {Vector3d.Dot(nw.Normalized(), f.NormalAt(at)):0.###} rev {f.Reversed}");
                        var m = Parametric3D.Kernel.Tess.ExactFaceMesher.Mesh(f, out var mw, out _);
                        if (m is { } mm)
                        {
                            int agree = 0, dis = 0;
                            for (int i = 0; i + 2 < mm.T.Count; i += 3)
                            {
                                var A = mm.P[mm.T[i]]; var B = mm.P[mm.T[i + 1]]; var C = mm.P[mm.T[i + 2]];
                                var nt = Vector3d.Cross(B - A, C - A); var cc = (A + B + C) / 3;
                                if (Vector3d.Dot(nt, f.NormalAt(sp.C + (cc - sp.C).Normalized() * sp.R)) > 0) agree++; else dis++;
                            }
                            Console.WriteLine($"R6     mesher: triangles {mm.T.Count / 3}, agree with face normal {agree}, against {dis}");
                        }
                        else Console.WriteLine("R6     mesher failed: " + mw);
                    }
                    int un = Parametric3D.Kernel.Analytic.PrismBuild.Finish(rr, null);
                    var vv = SolidValidator.Validate(rr);
                    Console.WriteLine($"R6   rerun: unmeshed {un} verdict {vv.Summary()}");
                    foreach (var i in vv.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4)) Console.WriteLine($"R6     issue {i}");
                    foreach (var f in rr.Faces) Console.WriteLine($"R6     face {f.Tag} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name} loops [{string.Join(",", f.Loops.Select(l => l.CoEdges.Count + ":" + string.Join("", l.CoEdges.Select(c => c.Edge.Curve.GetType().Name[0]))))}] tris {((f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Display?.Count ?? -1)}");
                }
                else Console.WriteLine("R6   rerun declined: " + wy);
            }
            foreach (var f in b.Faces) Console.WriteLine($"R6   face {f.Tag} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name} loops [{string.Join(",", f.Loops.Select(l => l.CoEdges.Count))}] display tris {((f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Display?.Count ?? -1)}");
            return;
        }
        if (mode is "walldbg")
        {
            double hr = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            var p = new Parametric3D.Model.Part("step"); Parametric3D.Model.TestGeometry.AddCube(p, 60);
            var cut = new Parametric3D.Model.Features.ExtrudeFeature("Step") { SketchId = RectSketch(p, 30, 0, -30, 30, 60).Id, Cut = true };
            cut.Side1.Distance = new Parametric3D.Model.Features.ValueRef(2); cut.Side1.Flip = true; p.AddFeature(cut); p.Rebuild(null);
            var body = Parametric3D.Kernel.Topology.TopologyClone.Deep(p.Bodies[0]);
            var dir = new Vector3d(0, 0, -1); var at = new Vector3d(-1.3, 0.7, 30);
            var cyl = new Parametric3D.Kernel.Geometry.Ssi.Cylinder(at - dir * 0.08, dir, hr);
            Parametric3D.Kernel.Geometry.ExactCylinderCut.DebugKeepInvalid = true;
            var w = new List<string>();
            var r = Parametric3D.Kernel.Geometry.ExactCylinderCut.Cut(body, cyl, at + dir * 30, w);
            Parametric3D.Kernel.Geometry.ExactCylinderCut.DebugKeepInvalid = false;
            var wall = r!.Faces.First(f => f.Tag.Contains("hole-wall"));
            Parametric3D.Kernel.Tess.ExactFaceMesher.TryDebug = m => Console.WriteLine("TRY " + m);
            Parametric3D.Kernel.Tess.ExactFaceMesher.StrayDebug = m => Console.WriteLine("STRAY " + m);
            Parametric3D.Kernel.Tess.ExactFaceMesher.ZipDebug = m => Console.WriteLine("ZIP " + m);
            Parametric3D.Kernel.Tess.ExactFaceMesher.DumpUV = (f, uv, idx, why) =>
            {
                Console.WriteLine($"UVDUMP {why} points {uv.Count}");
                foreach (var q in uv) Console.WriteLine($"  UV {q.X:R} {q.Y:R}");
            };
            var m = Parametric3D.Kernel.Tess.ExactFaceMesher.Mesh(wall, out var why2, out _);
            Console.WriteLine("RESULT " + (m == null ? "null: " + why2 : "ok " + m.Value.T.Count / 3));
            foreach (var l in wall.Loops) Console.WriteLine($"LOOP outer {l.IsOuter} coedges {l.CoEdges.Count}: " + string.Join(" | ", l.CoEdges.Select(c => $"{c.Edge.Curve.GetType().Name} rev {c.Reversed} ({c.Start.P.X:0.###},{c.Start.P.Y:0.###},{c.Start.P.Z:0.###})")));
            return;
        }
        if (mode is "k08" or "allB") K08();
        if (mode is "k08bisect") K08Bisect(8002, 30);
        if (mode is "k08detail") K08Detail(8002, 30, "Boss17");
        if (mode is "k08repro") K08Repro();
        if (mode is "memprof") MemProfile(8003, 30);
        if (mode is "audit") GeometryAuditRun();
        if (mode is "tilt") TiltProbe();
        if (mode is "k08a" or "allB") { K08Analytic(); K08Edges(); K08Shaft(); K08Volume(); K08Area(); K08Revolved(); K08ChordIndependence(); K08Centroid(); K09Inertia(); }
        if (mode is "inertia") K09Inertia();
        if (mode is "scale") ScaleProbe();
        if (mode is "iges") IgesRoundTrip();
        if (mode is "com") K08Centroid();
        if (mode is "rev") K08Revolved();
        if (mode is "fnormal") FaceNormalProbe();
        if (mode is "acost") AnalyticCost();
        if (mode is "chorddep") ChordDependence();
        if (mode is "bossrepro") BossRepro();
        if (mode is "fit") FitProbe();
        if (mode is "voff") VertexOff();
        if (mode is "m8001") Real8001Mesh();
        if (mode is "brb") BrbProbe();
        if (mode is "stepfaces") StepFaces();
        if (mode is "stepreal" or "allB") { StepRealistic(); StepShaft(); ForeignStep(); ForeignNurbs(); IgesRoundTrip(); ForeignIges(); }
        if (mode is "figes") ForeignIges();
        if (mode is "soak") SoakTest();
        if (mode is "gendet") GenDeterminism();
        if (mode is "sameedit") SoakSameEdit();
        if (mode is "contract" or "allA") K02Contract();
        if (mode is "facerefs" or "allB") { K05FaceRefs(); K05SplitFaces(); K05EdgeHistory(); }
        if (mode is "nurbs") ForeignNurbs();
        if (mode is "k09" or "allB") { K09Matrix(); K09Solver(); K09Solver2(); GridSolve(); FilletMatrix(); PrismCorners(); K09Constraints(); VertexOps(); VertexOpsN(); VertexConcave(); }
        if (mode is "solver") { K09Solver(); K09Solver2(); }
        if (mode is "vopsn") VertexOpsN();
        if (mode is "triprobe") { var tr = TriProbe.Run(true); Console.WriteLine($"TRIPROBE cases={tr.Cases} bad={tr.Bad} first={tr.FirstBad}"); return; }
        if (mode is "tanchain") { TanChainProbe(); return; }
        if (mode is "partholes") { SpeedPartHoles(int.Parse(args[1])); return; }
        if (mode is "facetdev") { FacetDevProbe(); return; }
        if (mode is "sphcap") { SphCapProbe(); return; }
        if (mode is "histnames") { HistNamesProbe(); return; }
        if (mode is "torbool") { TorusBooleanProbe(args.Length > 1 ? args[1] : null); return; }
        if (mode is "torcls") { Console.WriteLine("TORCLS " + TorusClassifierProbe(int.Parse(args[1]))); return; }
        if (mode is "holes") { SpeedHoles(int.Parse(args[1]), args.Length > 2 ? args[2] : "cyl"); return; }
        if (mode is "speed") { SpeedScaling(args.Skip(2).Select(int.Parse).ToArray(), int.Parse(args[1])); return; }
        if (mode is "scendump")
        {
            var w = new List<string>(); var bodies = Scenario(args[1], w);
            for (int bi = 0; bi < bodies.Count; bi++)
            {
                var m = Tessellator.Tessellate(bodies[bi]);
                var rep = SolidValidator.Validate(bodies[bi], m, new ValidationOptions { CheckSelfIntersections = true });
                Console.WriteLine($"body {bi}: {rep.Summary()}");
                foreach (var iss in rep.Issues.Where(i => i.Severity == ValidationSeverity.Error)) Console.WriteLine("  " + iss);
                var sw2 = new StringWriter();
                foreach (var v in m.Vertices) sw2.WriteLine(FormattableString.Invariant($"v {v.P.X:R} {v.P.Y:R} {v.P.Z:R}"));
                for (int t = 0; t + 2 < m.Indices.Length; t += 3) sw2.WriteLine($"f {m.Indices[t] + 1} {m.Indices[t + 1] + 1} {m.Indices[t + 2] + 1}");
                File.WriteAllText(Path.Combine(args.Length > 2 ? args[2] : ".", $"{args[1]}_{bi}.obj"), sw2.ToString());
            }
            return;
        }
        if (mode is "grid") GridSolve();
        if (mode is "constraints") K09Constraints();
        if (mode is "vertex") VertexOps();
        if (mode is "vdbg") VertexDbg();
        if (mode is "vdbg2") VertexDbg2();
        if (mode is "vmargin") VertexMargin();
        if (mode is "vertexn") VertexOpsN();
        if (mode is "pyrdbg") PyrDbg();
        if (mode is "vconcave") VertexConcave();
        if (mode is "g1" or "allB") G1PCurves();
        if (mode is "g2" or "allB") G2UvMesh();
        if (mode is "g3" or "allB") G3Tolerances();
        if (mode is "g4" or "allB") { G4Ssi(); G4ExactEdges(); G4ExactBoolean(); G4HoleFeature(); G4FaceSplit(); }
        if (mode is "g4h") G4HoleFeature();
        if (mode is "g4s") G4FaceSplit();
        if (mode is "sbd") ShaftBlindDbg();
        if (mode is "g4b") G4ExactBoolean();
        if (mode is "uvk") UvKernelProbe();
        if (mode is "fmatrix") FilletMatrix();
        if (mode is "loftprobe") LoftProbe();
        if (mode is "prism") PrismCorners();
        if (mode is "sweepprobe") SweepProbe();
        if (mode is "shellcrash") ShellCrash();
        if (mode is "sweeparc") SweepArcCrash();
        if (mode is "revcircle") RevolveCircleProbe();
        if (mode is "storage" or "allB") { K10Storage(); K10Reproducer(); }
        if (mode is "foreign") ForeignStep();
        if (mode is "av") K08Volume();
        if (mode is "splitface") SplitFaceProbe();
        if (mode is "seqexact") SeqExact();
        Log(new { staleExactUses = Parametric3D.Kernel.Exact.ExactAdapter.StaleExactUses });   // поставка 2: замер
        if (histStats.Count > 0) Log(new { historyCompare = histStats, disagreements = histDisagree });
        if (capA != null)
        {
            BooleanOp.GeneralDeclineProbe = null;
            Console.WriteLine($"GD captured in {capTest}: {capWhy}; kind {capK}; A faces {capA.Faces.Count()} B faces {capB!.Faces.Count()}");
            foreach (var f in capB.Faces) Console.WriteLine($"GD  B face {f.Tag} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))}");
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("GD " + m);
            var rr = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(capA, capB, capK == BooleanOp.BooleanKind.Unite ? Parametric3D.Kernel.Analytic.Bool2.Union : capK == BooleanOp.BooleanKind.Subtract ? Parametric3D.Kernel.Analytic.Bool2.Subtract : Parametric3D.Kernel.Analytic.Bool2.Intersect, out var why2);
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = null;
            Console.WriteLine("GD rerun: " + (rr == null ? why2 : "ok"));
            if (rr != null)
            {
                foreach (var e in rr.Edges)
                {
                    var ces = rr.Faces.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges.Select(c => (F: f, C: c)))).Where(x => ReferenceEquals(x.C.Edge, e)).ToList();
                    if (ces.Count == 2 && ces[0].C.Reversed == ces[1].C.Reversed)
                        Console.WriteLine($"GD   SAME-DIRECTION edge {e.Curve.GetType().Name} {e.V0.P}->{e.V1.P}: faces {ces[0].F.Tag} (rev {ces[0].F.Reversed}) & {ces[1].F.Tag} (rev {ces[1].F.Reversed})");
                    if (ces.Count != 2) Console.WriteLine($"GD   edge used {ces.Count} times");
                }
                foreach (var f in rr.Faces)
                    foreach (var l in f.Loops)
                    {
                        var pts = new List<Vector3d>(); foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                        var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                        var n = f.NormalAt(pts[0]);
                        Console.WriteLine($"GD   ring of {f.Tag} rev {f.Reversed} outer {l.IsOuter}: area·n {Vector3d.Dot(nw, n) / 2:0.###} (n {n}) src {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}");
                    }
                var norm = Parametric3D.Kernel.Topology.TopologyConventions.Normalize(rr);
                Console.WriteLine($"GD normalize: consistent {norm.Consistent} unanchored {norm.Unanchored} problems [{string.Join(" | ", norm.Problems.Take(4))}] convention {rr.TopologyConvention}");
                Console.WriteLine($"GD faces {rr.Faces.Count()} edges {rr.Edges.Count()} brep {Parametric3D.Kernel.Topology.SolidValidator.ValidateBRep(rr).Summary()}");
                foreach (var f in rr.Faces) Console.WriteLine($"GD   face {f.Tag} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name} loops [{string.Join(",", f.Loops.Select(l => (l.IsOuter ? "O" : "I") + l.CoEdges.Count))}] rev {f.Reversed}");
            }
        }
        if (localStats.Count > 0) Log(new { localSurvey = localStats });
        if (routeStats.Count > 0) Log(new { analyticRoute = routeStats.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value) });
        if (outcomeStats.Count > 0) Log(new { booleanOutcomes = outcomeStats.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value), booleanTotal = outcomeStats.Values.Sum() });
        if (prSeen.Count > 0) Log(new { prismSurvey = new { operands = prSeen.Count, recognized = prOk + prBad + prNoAv, volumeAgrees = prOk, volumeDisagrees = prBad, healed = prHealed, healedDiffer = prHealedDiff, healMaxMm = prHealMax, noAnalyticVolume = prNoAv, worstRelError = prWorst, worstCase = prWorstCase, noReference = prNoRef, worstVsPolyhedral = prWorstMesh, worstVsPolyhedralCase = prWorstMeshCase, badCases = prBadCases, errQuantiles = prErrs.Count == 0 ? null : new[] { 0.5, 0.9, 0.97, 0.99, 1.0 }.Select(q => prErrs.OrderBy(x => x).ElementAt(Math.Min(prErrs.Count - 1, (int)(q * prErrs.Count)))).ToArray(), above1e6 = prErrs.Count(x => x > 1e-6) }, rejected = prWhy.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value) });
        if (anOps.Count > 0) Log(new { analyticSurvey = anOps.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value), faces = anFaces.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value), otherKinds = anOther.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value), pairs = anPairs.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value) });
        Log(new { passed = Passed, failed = Failed });
        Environment.ExitCode = Failed == 0 ? 0 : 1;
    }
}
static partial class Program
{
    // Explain the residual of the 1e-5 rounding metric: distinct output vertices closer than its grid.
    internal static (int Pairs, double Min) ClosePairs(MeshData m, double eps)
    {
        var pts = m.Vertices.Select(v => (v.P.X, v.P.Y, v.P.Z)).Distinct().Select(p => new Vector3d(p.X, p.Y, p.Z)).ToList();
        var grid = new Dictionary<(long, long, long), List<int>>();
        int pairs = 0; double min = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            var c = ((long)Math.Floor(pts[i].X / eps), (long)Math.Floor(pts[i].Y / eps), (long)Math.Floor(pts[i].Z / eps));
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var l))
                    foreach (int j in l) { double d = (pts[i] - pts[j]).Length; if (d < eps) { pairs++; min = Math.Min(min, d); } }
            if (!grid.TryGetValue(c, out var own)) grid[c] = own = new List<int>(); own.Add(i);
        }
        return (pairs, pairs == 0 ? 0 : min);
    }
}
