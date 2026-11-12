using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;
using Parametric3D.Core;

static partial class Program
{
    // User repro: a sketch circle and the actual Extrude feature, not a supplied finished solid.
    static void ThreadCylinderChecks()
    {
        foreach (var (diameter, height) in new[] { (10.0, 200.0), (20.0, 200.0), (20.0, 20.0) })
            foreach (bool exit in new[] { false, true })
            {
                string id = $"THREAD-CYLINDER-M{diameter}-H{height}-EXIT{exit}";
                if (Environment.GetEnvironmentVariable("P3D_THREADCASE") is { } filter && !id.Contains(filter)) continue;
                Check(id, () =>
                {
                    var part = new Part("cylinder thread reproduction");
                    var sketch = new Sketch("circle", Frame3.XY); part.Add(sketch);
                    sketch.AddEntity(new SkCircle(new SkPoint(0, 0), diameter / 2));
                    var ctx = new BuildContext { Part = part, Parameters = new ParameterTable() };
                    var extrusion = new ExtrudeFeature { SketchId = sketch.Id };
                    extrusion.Side1.Distance = height;
                    extrusion.Execute(ctx);
                    var blank = ctx.Bodies.Single();
                    var spec = Parametric3D.Model.Threads.ThreadDb.All.First(t => t.DMajor == diameter
                        && t.Designation == $"M{diameter:0}" && t.Series == "coarse");
                    var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, SmoothExit = exit };
                    // UI selection stores a point on a displayed triangle, not an arbitrary
                    // point of the ideal cylinder (which may be outside its display chord).
                    feature.OnFaces.Add(FaceRefs.Inside(blank.Faces.Single(f =>
                        GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface)));
                    long before = DirectRound.ExactThreadCells + DirectRound.ExactSweeps;
                    Log(new { test = "THREAD-CYLINDER-START", diameter, height, exit, spec.P,
                        inputFaces = blank.Faces.Count(),
                        surfaces = blank.Faces.Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name).ToArray() });
                    var watch = Stopwatch.StartNew();
                    feature.Execute(ctx);
                    double ms = watch.Elapsed.TotalMilliseconds;
                    var body = ctx.Bodies.Single();
                    var valid = SolidValidator.Validate(body);
                    Require(valid.IsValid, valid.Summary());
                    Require(body.Guarantee == GuaranteeLevel.ExactAnalytic, "thread is not exact");
                    Require(feature.LastRoute == (exit ? "direct-runout" : "direct-screw"), "wrong route: " + feature.LastRoute);
                    Require(Math.Abs(feature.ThreadLen - height) < 1e-8 && !feature.Internal, "face/length detection");
                    double volume = AnalyticVolume.Of(body) ?? double.NaN;
                    Require(double.IsFinite(volume) && volume > 0 && volume < Math.PI * diameter * diameter / 4 * height, "invalid volume");
                    var p = spec.ExtZr.OrderBy(v => v.Y).ThenBy(v => v.X).ToArray();
                    double radius = diameter / 2, root = p[0].Y;
                    double ml = (p[2].Y - root) / (p[2].X - p[0].X);
                    double mr = (p[3].Y - root) / (p[3].X - p[1].X);
                    double x0 = p[0].X + (radius - root) / ml, x1 = p[1].X + (radius - root) / mr;
                    double area = Math.PI / spec.P * (radius * radius * (spec.P - x1 + x0)
                        + root * root * (p[1].X - p[0].X)
                        + (Math.Pow(radius, 3) - Math.Pow(root, 3)) / 3 * (1 / mr - 1 / ml));
                    double expected = exit ? ExitVolumeOracle(spec, radius, height, height) : area * height;
                    Require(Math.Abs(volume - expected) < expected * 2e-6, "independent volume integral");
                    if (height == 20)
                    {
                        double uncached = AnalyticVolume.OfUncached(body) ?? double.NaN;
                        Require(double.IsFinite(uncached) && Math.Abs(uncached - expected) < expected * 2e-6,
                            "uncached boundary volume against independent integral");
                    }
                    Require(body.Edges.All(e => e.CoEdges.Count == 2), "unsewn B-rep boundary");
                    Require(body.Faces.Count(f => f.Tag == "cap:start") == 1
                        && body.Faces.Count(f => f.Tag == "cap:end") == 1, "internal cell caps remain");
                    long built = DirectRound.ExactThreadCells + DirectRound.ExactSweeps - before;
                    Require(built is >= 1 and <= 2, "repeated turns were rebuilt");
                    Log(new { test = "THREAD-CYLINDER-METRICS", diameter, height, exit, spec.P, ms,
                        feature.LastRoute, volume, expected, built, faces = body.Faces.Count() });
                });
            }
        // User repro: thread only half of the blank; the rest must stay one cylinder face.
        if (Environment.GetEnvironmentVariable("P3D_THREADCASE") is not { } halfFilter || "THREAD-CYLINDER-HALF".Contains(halfFilter))
            foreach (bool exit in new[] { true, false })
            Check("THREAD-CYLINDER-HALF-M10-H200-L100-EXIT" + exit, () =>
            {
                var part = new Part("half thread");
                var sketch = new Sketch("circle", Frame3.XY); part.Add(sketch);
                sketch.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
                var ctx = new BuildContext { Part = part, Parameters = new ParameterTable() };
                var extrusion = new ExtrudeFeature { SketchId = sketch.Id };
                extrusion.Side1.Distance = 200;
                extrusion.Execute(ctx);
                var blank = ctx.Bodies.Single();
                var spec = Parametric3D.Model.Threads.ThreadDb.All.First(t => t.Designation == "M10" && t.Series == "coarse");
                var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, SmoothExit = exit };
                feature.Length = new(100);
                feature.OnFaces.Add(FaceRefs.Inside(blank.Faces.Single(f =>
                    GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface)));
                long before = DirectRound.ExactThreadCells + DirectRound.ExactSweeps;
                var watch = Stopwatch.StartNew();
                feature.Execute(ctx);
                double ms = watch.Elapsed.TotalMilliseconds;
                var body = ctx.Bodies.Single();
                var valid = SolidValidator.Validate(body);
                Require(valid.IsValid, valid.Summary());
                Require(body.Guarantee == GuaranteeLevel.ExactAnalytic, "thread is not exact");
                Require(feature.LastRoute == (exit ? "direct-runout" : "direct-screw"), "wrong route: " + feature.LastRoute);
                Require(Math.Abs(feature.ThreadLen - 100) < 1e-8, "length");
                // Faces lying wholly in the unthreaded part z > 100 (beyond the cap at z = 200).
                // The thread may start from either end; count faces wholly on each side of z = 100.
                int Side(int sign) => body.Faces.Count(f => !f.Tag.StartsWith("cap:") && f.Tag != "thread:stop" && f.Loops.SelectMany(l => l.CoEdges)
                    .All(c => sign * (c.Edge.V0.P.Z - 100) > -1e-6 && sign * (c.Edge.V1.P.Z - 100) > -1e-6));
                int tail = Math.Min(Side(1), Side(-1));
                Require(tail == 1, "unthreaded blank split into " + tail + " faces");
                Require(body.Faces.Any(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface), "no plain cylinder");
                double volume = AnalyticVolume.Of(body) ?? double.NaN;
                double expected = exit ? ExitVolumeOracle(spec, 5, 200, 100) : HalfAbruptVolume(spec, 5, 200, 100);
                if (!exit) Require(body.Faces.Count(f => f.Tag == "thread:stop") == 1, "no flat thread stop face");
                Require(Math.Abs(volume - expected) < expected * 2e-6, $"independent volume integral {volume} vs {expected}");
                Require(body.Edges.All(e => e.CoEdges.Count == 2), "unsewn B-rep boundary");
                long built = DirectRound.ExactThreadCells + DirectRound.ExactSweeps - before;
                Require(built is >= 1 and <= 2, "repeated turns were rebuilt");
                Log(new { test = "THREAD-CYLINDER-HALF-METRICS", exit, ms, volume, expected, built, faces = body.Faces.Count(), tail });
            });
        // User repro: 1/2-14 NPT (taper 1:16) on a plain Ø21 cylinder, built directly as one tapered cell.
        foreach (var (exit, length, rad) in new[] { (true, 30.0, 10.5), (false, 30.0, 10.5), (false, 15.0, 10.5), (true, 30.0, 11.5), (false, 30.0, 11.5), (true, 15.0, 11.5), (false, 15.0, 11.5) })
        {
            string tid = $"THREAD-CYLINDER-NPT-D{2 * rad:0}-H30-L{length}-EXIT{exit}";
            if (Environment.GetEnvironmentVariable("P3D_THREADCASE") is { } tf && !tid.Contains(tf)) continue;
            Check(tid, () =>
            {
                var part = new Part("npt");
                var sketch = new Sketch("circle", Frame3.XY); part.Add(sketch);
                sketch.AddEntity(new SkCircle(new SkPoint(0, 0), rad));
                var ctx = new BuildContext { Part = part, Parameters = new ParameterTable() };
                var extrusion = new ExtrudeFeature { SketchId = sketch.Id };
                extrusion.Side1.Distance = 30;
                extrusion.Execute(ctx);
                var blank = ctx.Bodies.Single();
                var spec = Parametric3D.Model.Threads.ThreadDb.All.First(t => t.Designation == "1/2-14 NPT");
                var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, SmoothExit = exit };
                if (length < 30) feature.Length = new(length);
                feature.OnFaces.Add(FaceRefs.Inside(blank.Faces.Single(f =>
                    GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface)));
                var watch = Stopwatch.StartNew();
                feature.Execute(ctx);
                double ms = watch.Elapsed.TotalMilliseconds;
                var body = ctx.Bodies.Single();
                var valid = SolidValidator.Validate(body);
                Require(valid.IsValid, valid.Summary());
                Require(body.Guarantee == GuaranteeLevel.ExactAnalytic, "thread is not exact");
                Require(feature.LastRoute == (exit ? "direct-runout" : "direct-screw"), "wrong route: " + feature.LastRoute);
                Require(body.Edges.All(e => e.CoEdges.Count == 2), "unsewn B-rep boundary");
                double volume = AnalyticVolume.Of(body) ?? double.NaN;
                double uncached = AnalyticVolume.OfUncached(body) ?? double.NaN;
                double taper = Math.Tan(spec.HalfConeDeg * Math.PI / 180);
                double q1 = spec.ExtZr.OrderBy(v => v.Y).ThenBy(v => v.X).Skip(1).First().X;
                double turned = exit ? Math.Min(rad, spec.DMajor / 2 + taper * (length - q1 - spec.Lead) - 1e-6) : rad;
                double expected = TaperVolumeOracle(spec, turned, 30, length, exit, taper) + Math.PI * (rad * rad - turned * turned) * (30 - length);
                Require(Math.Abs(volume - expected) < expected * 2e-6 && Math.Abs(uncached - expected) < expected * 2e-6,
                    $"independent volume integral {volume} / {uncached} vs {expected}");
                Require(BooleanOp.VolumeVsFacets(body) is not { Agree: false }, "display/boundary volume mismatch");
                // Picking a thread edge selects its tangent chain (same rule as App Topo.TangentChain):
                // it must be quick and run along the whole thread.
                var helical = body.Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.ProcCurve3 && e.V0 != null && e.V1 != null).ToList();
                var mid = helical.OrderBy(e => Math.Abs(e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2).Z - (30 - length / 2))).First();
                var cw = Stopwatch.StartNew();
                var chain = EdgeChain(mid);
                double chainMs = cw.Elapsed.TotalMilliseconds;
                double zLo = chain.Min(e => Math.Min(e.V0!.P.Z, e.V1!.P.Z)), zHi = chain.Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
                // Saving the document must not tabulate the thread (it rebuilds on open).
                var saveWatch = Stopwatch.StartNew();
                bool skipped = false; try { BrepFile.Write(new[] { body }); } catch (NotSupportedException) { skipped = true; }
                Require(skipped && saveWatch.Elapsed.TotalMilliseconds < 500, $"thread body cache: skipped={skipped}, {saveWatch.Elapsed.TotalMilliseconds:0} ms");
                if (Environment.GetEnvironmentVariable("P3D_PICKPROBE") == "1")
                {
                    var sw = Stopwatch.StartNew(); int seams = chain.Count(e => EdgeClass.IsTangentSeam(e));
                    double seamMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
                    double lenSum = chain.Sum(e => e.Curve.Length); double lenMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
                    int bytes = -1; try { bytes = BrepFile.Write(new[] { body }).Length; } catch (Exception ex) { Console.WriteLine("brep: " + ex.Message); }
                    Log(new { probe = tid, chain = chain.Count, seams, seamMs, lenMs, brepBytes = bytes, brepMs = sw.Elapsed.TotalMilliseconds });
                }
                Require(chainMs < 200, $"edge chain took {chainMs:0} ms");
                Require(zHi - zLo > length - 3, $"edge chain covers z {zLo:0.###}..{zHi:0.###}, thread length {length}");
                Log(new { test = "THREAD-CYLINDER-NPT-METRICS", exit, length, ms, volume, expected, faces = body.Faces.Count(), chain = chain.Count, chainMs, zLo, zHi });
            });
        }
        // User document (P3D_USERDOC=path): rebuild, save with display cache, reopen — no huge thread tables.
        if (Environment.GetEnvironmentVariable("P3D_USERDOC") is { Length: > 0 } doc && File.Exists(doc))
            Check("THREAD-USERDOC-SAVE", () =>
            {
                var (part, pars) = DocumentFile.Load(File.ReadAllText(doc));
                if (Environment.GetEnvironmentVariable("P3D_STAGEPROBE") == "2")
                {
                    // Same thread built directly: Ø23 x 100, 1/2-14 NPT, 50 mm from the top, smooth exit.
                    var sk = new Sketch("c", Frame3.XY); var pp = new Part("probe"); pp.Add(sk);
                    sk.AddEntity(new SkCircle(new SkPoint(0, 0), 11.5));
                    var cx = new BuildContext { Part = pp, Parameters = new ParameterTable() };
                    var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = 100; ex.Execute(cx);
                    var sp = Parametric3D.Model.Threads.ThreadDb.All.First(t => t.Designation == "1/2-14 NPT");
                    var th = new CosmeticThreadFeature { TypeId = sp.Id, Cosmetic = false, SmoothExit = true }; th.Length = new(50);
                    th.OnFaces.Add(FaceRefs.Inside(cx.Bodies.Single().Faces.Single(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface)));
                    var sw = Stopwatch.StartNew(); th.Execute(cx); double execMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
                    var vb = SolidValidator.Validate(cx.Bodies[0]); double validMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
                    double vol = AnalyticVolume.Of(cx.Bodies[0]) ?? 0; double volMs = sw.Elapsed.TotalMilliseconds;
                    Log(new { probe = "stages", execMs, validMs, volMs, valid = vb.IsValid, faces = cx.Bodies[0].Faces.Count(), edges = cx.Bodies[0].Edges.Count(), th.LastRoute });
                }
                bool prof = Environment.GetEnvironmentVariable("P3D_STAGEPROBE") == "1";
                if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.ResetAll(); }
                var w = Stopwatch.StartNew(); part.Rebuild(pars); double rebuildMs = w.Elapsed.TotalMilliseconds;
                if (prof)
                {
                    Parametric3D.Kernel.Diagnostics.Prof.Enabled = false;
                    Console.WriteLine("PROF " + Parametric3D.Kernel.Diagnostics.Prof.Report());
                    foreach (var kv in part.LastFeatureMs) Console.WriteLine($"PROF feature {kv.Key.Name}: {kv.Value:0} ms");
                }
                Require(part.Features.All(f => f.Error == null), "rebuild: " + string.Join("; ", part.Features.Where(f => f.Error != null).Select(f => f.Error)));
                w.Restart(); var text = DocumentFile.Save(part, pars, withDisplay: true); double saveMs = w.Elapsed.TotalMilliseconds;
                string? note = DocumentFile.LastSaveNote;
                Require(text.Length < 4 << 20, $"saved document is {text.Length} chars");
                w.Restart(); var (again, ap) = DocumentFile.Load(text); again.Rebuild(ap); double reopenMs = w.Elapsed.TotalMilliseconds;
                Require(again.Bodies.Count == part.Bodies.Count && again.Features.All(f => f.Error == null), "reopen");
                Log(new { test = "THREAD-USERDOC-METRICS", rebuildMs, saveMs, chars = text.Length, note, reopenMs, mesh = part.Mesh != null });
            });
        // Keep the general periodic sweep covered now that standard trapezoids use ruled strips.
        foreach (bool left in new[] { false, true })
            Check("THREAD-PERIODIC-GENERAL-" + left, () =>
            {
                var spec = Parametric3D.Model.Threads.ThreadDb.All.First(t => t.Designation == "M6" && t.Series == "coarse");
                var section = CosmeticThreadFeature.ThreadSection(spec, 3, Frame3.XY, left);
                var body = PeriodicSweepOp.Build(section, spec.Lead, 2.25, left);
                var report = SolidValidator.Validate(body);
                Require(report.IsValid, report.ToString());
                Require(body.Guarantee == GuaranteeLevel.ExactAnalytic, "general periodic sweep lost exactness");
                double cached = AnalyticVolume.Of(body) ?? double.NaN;
                double uncached = AnalyticVolume.OfUncached(body) ?? double.NaN;
                Require(double.IsFinite(cached) && double.IsFinite(uncached)
                    && Math.Abs(cached - uncached) < Math.Abs(cached) * 1e-8, "periodic composed volume");
            });
    }

    /// <summary>Constant helical-groove section over [0, length], untouched blank above.</summary>
    static double HalfAbruptVolume(Parametric3D.Model.Threads.ThreadSpec spec, double radius, double height, double length)
    {
        var p = spec.ExtZr.OrderBy(v => v.Y).ThenBy(v => v.X).ToArray();
        double root = p[0].Y, ml = (p[2].Y - root) / (p[2].X - p[0].X), mr = (p[3].Y - root) / (p[3].X - p[1].X);
        double x0 = p[0].X + (radius - root) / ml, x1 = p[1].X + (radius - root) / mr;
        double area = Math.PI / spec.P * (radius * radius * (spec.P - x1 + x0) + root * root * (p[1].X - p[0].X)
            + (Math.Pow(radius, 3) - Math.Pow(root, 3)) / 3 * (1 / mr - 1 / ml));
        return area * length + Math.PI * radius * radius * (height - length);
    }

    /// <summary>Mirror of App Topo.TangentChain: edges continuing the start edge G1 through vertices.</summary>
    static List<TEdge> EdgeChain(TEdge start)
    {
        static Vector3d? Dir(TEdge e, TVertex v)
        {
            var c = e.Curve; double t = (c.PointAt(c.T0) - v.P).LengthSquared <= (c.PointAt(c.T1) - v.P).LengthSquared ? c.T0 : c.T1;
            var d = c.TangentAt(t); return d.LengthSquared > 1e-24 ? d.Normalized() : null;
        }
        var res = new List<TEdge> { start }; var seen = new HashSet<TEdge> { start }; var queue = new Queue<TEdge>(res);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (v == null || Dir(e, v) is not { } d) continue;
                foreach (var o in v.Edges)
                    if (!seen.Contains(o) && o.V0 != null && o.V1 != null && Dir(o, v) is { } d2 && Math.Abs(Vector3d.Dot(d, d2)) >= 0.99985)
                    { seen.Add(o); res.Add(o); queue.Enqueue(o); }
            }
        }
        return res;
    }

    /// <summary>Volume oracle for a tool section raised by smooth exit plus taper·t, clipped at the blank.</summary>
    static double TaperVolumeOracle(Parametric3D.Model.Threads.ThreadSpec spec, double radius, double height, double length,
        bool exit, double taper)
    {
        var p = spec.ExtZr.OrderBy(v => v.Y).ThenBy(v => v.X).ToArray();
        double root = p[0].Y, q0 = p[0].X, q1 = p[1].X;
        double ml = (p[2].Y - root) / (p[2].X - q0), mr = (p[3].Y - root) / (p[3].X - q1);
        double depth = radius - root, lead = spec.Lead, end = length - q1;
        double span = Math.Min(lead, length + q0 + depth / ml - q1), start = end - span;
        double clip = exit ? height : length, crest0 = spec.DMajor / 2;
        double D(double t)
        {
            double s = exit ? Math.Clamp((t - start) / span, 0, 1) : 0;
            double amp = taper > 0 ? Math.Max(0, depth - taper * end) : depth;
            return Math.Min(depth, amp * s * s * (3 - 2 * s) + taper * t);
        }
        double C(double t) => Math.Min(radius, crest0 + D(t));
        double SectionLoss(double t)
        {
            double d = D(t), c = C(t), loss = 0;
            double[] qs = { q0 + (C(t - lead) - root - d) / ml, q0, q1, q1 + (c - root - d) / mr,
                lead + q0 + (c - root - D(t + lead)) / ml };
            for (int k = 0; k < 4; k++)
            {
                double a = Math.Max(qs[k], -t), b = Math.Min(qs[k + 1], clip - t);
                if (a >= b) continue;
                double slope = k == 0 ? ml : k == 2 ? mr : 0, corner = k == 0 ? q0 : q1;
                double ra = k == 3 ? c : root + d + slope * (a - corner), rb = k == 3 ? c : root + d + slope * (b - corner);
                loss += (b - a) * (radius * radius - (ra * ra + ra * rb + rb * rb) / 3);
            }
            return loss;
        }
        const int n = 65536;
        double a0 = -q1 - depth / mr - lead, a1 = clip - q0 - depth / ml, h = (a1 - a0) / n, sum = SectionLoss(a0) + SectionLoss(a1);
        for (int i = 1; i < n; i++) sum += (i % 2 == 0 ? 2 : 4) * SectionLoss(a0 + h * i);
        return Math.PI * radius * radius * height - Math.PI / lead * h * sum / 3;
    }
}
