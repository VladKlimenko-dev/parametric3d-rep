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
}
