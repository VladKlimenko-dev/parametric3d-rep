using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Threads;

static partial class Program
{
    static void ThreadRunoutChecks()
    {
        var spec = ThreadDb.All.First(t => t.Designation == "M6" && t.Series == "coarse");
        foreach (double length in new[] { 2.0, 20.0, 200.0 })
            Check("THREAD-EXIT-PREVIEW-" + length, () =>
            {
                var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, length);
                var ctx = new BuildContext { Part = new Part("exit preview"), Parameters = new ParameterTable() };
                ctx.Bodies.Add(body);
                var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false,
                    Length = length, ShowPreview = true, SmoothExit = true };
                feature.OnFaces.Add(new Vector3d(3, 0, length / 2));
                long cells = DirectRound.ExactThreadCells, sweeps = DirectRound.ExactSweeps;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) feature.Execute(ctx);
                Log(new { test = "THREAD-EXIT-PREVIEW-TIMING", length, meanMs = sw.Elapsed.TotalMilliseconds / 10,
                    lines = feature.Lines?.Count });
                Require(ReferenceEquals(body, ctx.Bodies.Single()) && feature.LastRoute == "overlay", "preview changed body");
                Require(DirectRound.ExactThreadCells == cells && DirectRound.ExactSweeps == sweeps, "preview built exact geometry");
                Require(feature.Lines is { Count: > 0 and <= 6100 }, "preview line budget");
            });
        Check("THREAD-EXIT-TOOL-LAW", () =>
        {
            var p = spec.ExtZr.OrderBy(v => v.Y).ThenBy(v => v.X).ToArray();
            double depth = 3 - p[0].Y, end = 2 - p[1].X, start = end - spec.Lead;
            double Offset(double t)
            {
                var frame = CosmeticThreadFeature.ToolFrame(spec, false, Vector3d.Zero,
                    Vector3d.UnitZ, Vector3d.UnitX, t, 2, false, true, 3);
                return (frame.O - Vector3d.UnitZ * t).Length;
            }
            Require(Offset(start) < 1e-12 && Math.Abs(Offset(end) - depth) < 1e-12, "wrong exit endpoints");
            Require(Math.Abs(Offset((start + end) / 2) - depth / 2) < 1e-12, "wrong radial displacement");
            double h = 1e-5 * spec.Lead;
            Require(Math.Abs((Offset(start + h) - Offset(start)) / h) < 1e-4
                && Math.Abs((Offset(end) - Offset(end - h)) / h) < 1e-4, "nonzero endpoint speed");
        });
        foreach (bool cancel in new[] { true, false })
            Check(cancel ? "THREAD-EXIT-CANCEL-ATOMIC" : "THREAD-EXIT-ACCEPTANCE-ATOMIC", () =>
            {
                var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, 2);
                var ctx = new BuildContext { Part = new Part("exit atomicity"), Parameters = new ParameterTable() };
                ctx.Bodies.Add(body);
                var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = 2, SmoothExit = true };
                feature.OnFaces.Add(new Vector3d(3, 0, 1));
                using var scope = new KernelContext(cancellation: new System.Threading.CancellationToken(cancel)).Enter();
                bool refused = false, old = BooleanOp.ForceAcceptFailure;
                try
                {
                    BooleanOp.ForceAcceptFailure = !cancel;
                    feature.Execute(ctx);
                }
                catch (OperationCanceledException) when (cancel) { refused = true; }
                catch (RebuildException) when (!cancel) { refused = true; }
                finally { BooleanOp.ForceAcceptFailure = old; }
                Require(refused && ReferenceEquals(body, ctx.Bodies.Single()), "partial run-out was published");
            });
        foreach (double radius in new[] { 3.0, 2.9 })
            Check("THREAD-EXIT-ROTATED-" + radius, () =>
            {
                var frame = Frame3.FromNormal(new Vector3d(7, -11, 13), new Vector3d(1, 2, 3).Normalized());
                using var scope = new KernelContext(KernelSettings.Capture() with
                    { GeometryPolicy = GeometryPolicy.PreserveBRep, Preview = false }).WithPolicy(OperationPolicy.Strict).Enter();
                var body = ThreadRunoutOp.Build(frame, spec.Lead, 2, 2, radius, spec.ExtZr, true);
                var report = SolidValidator.Validate(body);
                Require(report.IsValid, report.Summary());
                double volume = AnalyticVolume.OfUncached(body) ?? double.NaN;
                Require(double.IsFinite(volume) && Math.Abs(volume - ExitVolumeOracle(spec, radius, 2, 2)) < 1e-6,
                    "rotated/undersize blank volume");
            });
        foreach (var (left, length, height) in new[]
            { (false, 2.0, 2.0), (true, 2.0, 2.0), (false, 3.25, 3.25),
              (true, 3.25, 3.25), (false, 10.0, 10.0), (false, 20.0, 20.0), (false, 3.25, 5.0) })
            Check($"THREAD-EXIT-{left}-{length}-{height}", () =>
            {
                var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, height);
                var ctx = new BuildContext { Part = new Part("thread exit"), Parameters = new ParameterTable() };
                ctx.Bodies.Add(body);
                var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false,
                    Length = length, SmoothExit = true, LeftHand = left };
                feature.OnFaces.Add(new Vector3d(3, 0, height / 2));
                long cells = DirectRound.ExactThreadCells;
                var sw = Stopwatch.StartNew(); feature.Execute(ctx); double ms = sw.Elapsed.TotalMilliseconds;
                var result = ctx.Bodies.Single();
                Require(feature.LastRoute == "direct-runout" && result.Guarantee == GuaranteeLevel.ExactAnalytic, "not exact run-out");
                Require(DirectRound.ExactThreadCells - cells == (length >= 3 ? 2 : 1), "rebuilt repeated turns");
                Require(result.Edges.All(e => e.CoEdges.Count == 2), "open seam");
                Require(result.Faces.Count(f => f.Tag == "cap:start") == 1
                    && result.Faces.Count(f => f.Tag == "cap:end") == 1, "internal caps remain");
                var report = SolidValidator.Validate(result);
                Require(report.IsValid, report.Summary());
                Require(result.Faces.All(f => GeometryAudit.AuthoritativeSurface(f) is not FacetedSurface), "faceted authority");
                foreach (var face in result.Faces)
                    if (GeometryAudit.AuthoritativeSurface(face) is SectionSurface surface)
                    {
                        var p = surface.PointAt(new Vector2d((surface.UMin + surface.UMax) / 2, 0.5));
                        double r = new Vector2d(p.X, p.Y).Length;
                        Require(r <= 3 + 1e-8 && p.Z >= -1e-8 && p.Z <= height + 1e-8, "surface outside blank");
                        if (p.Z < height - length - 1e-8) Require(Math.Abs(r - 3) < 1e-8, "cut beyond requested ending");
                    }
                double expected = ExitVolumeOracle(spec, 3, height, length);
                double actual = AnalyticVolume.OfUncached(result) ?? double.NaN;
                Require(double.IsFinite(actual) && Math.Abs(actual - expected) < expected * 2e-6, "independent removed-volume integral");
                Log(new { test = "THREAD-EXIT-METRICS", left, length, height, ms, expected, actual,
                    cells = DirectRound.ExactThreadCells - cells, faces = result.Faces.Count() });
                // Root strip is tangent to the original cylinder at the precise withdrawal endpoint.
                double end = length - spec.ExtZr.Where(p => p.Y == spec.ExtZr.Min(x => x.Y)).Max(p => p.X);
                var rootFaces = result.Faces.Where(f => f.Tag?.Contains("thread.1]") == true).ToArray();
                bool tested = false;
                foreach (var face in rootFaces)
                    if (GeometryAudit.AuthoritativeSurface(face) is SectionSurface s && Math.Abs(s.UMax - end) < 1e-8)
                    {
                        var uv = new Vector2d(s.UMax, 0.5);
                        var point = s.PointAt(uv);
                        var radial = new Vector3d(point.X, point.Y, 0).Normalized();
                        Require(Math.Abs(new Vector2d(point.X, point.Y).Length - 3) < 1e-9, "root did not reach blank");
                        Require(Vector3d.Dot(face.NormalAt(point), radial) > 1 - 1e-9, "not tangent at exit");
                        tested = true;
                    }
                Require(tested, "exit tangent not checked");
                if (length >= 3)
                {
                    var seam = result.Edges.Where(e => Math.Abs(e.V0.P.Z - height + 1) < 1e-8
                        && Math.Abs(e.V1.P.Z - height + 1) < 1e-8).ToArray();
                    Require(seam.Length > 0, "no copied-cell seam checked");
                    foreach (var edge in seam)
                    {
                        var p = edge.Curve.PointAt((edge.Curve.T0 + edge.Curve.T1) / 2);
                        var adjacent = edge.CoEdges.Select(c => c.Loop!.Face!).ToArray();
                        Require(Vector3d.Dot(adjacent[0].NormalAt(p), adjacent[1].NormalAt(p)) > 1 - 1e-8,
                            "normal discontinuity at copied-cell seam");
                    }
                    bool previous = GeometryAudit.CacheOffForTests;
                    try { GeometryAudit.CacheOffForTests = true; Require(GeometryAudit.Of(result).FullyAnalytic, "uncached audit"); }
                    finally { GeometryAudit.CacheOffForTests = previous; }
                }
                if (length == 2)
                {
                    var data = BrepFile.Write(new[] { result });
                    var saved = BrepFile.ReadAll(data).Single().Body;
                    var valid = SolidValidator.Validate(saved);
                    Require(valid.IsValid, "run-out roundtrip: " + valid.Summary()
                        + " " + string.Join(" / ", valid.Issues.Take(20)));
                    double savedVolume = AnalyticVolume.OfUncached(saved) ?? double.NaN;
                    Require(double.IsFinite(savedVolume) && Math.Abs(savedVolume - expected) < expected * 2e-6, "saved exit volume");
                    Log(new { test = "THREAD-EXIT-ROUNDTRIP", left, bytes = data.Length, savedVolume });
                }
            });
    }

    // Independent volume oracle in cylindrical coordinates, not a boundary surface integral.
    // dV=(R²-r²)/2 d(theta) dz, theta=2pi*t/lead and z=t+q.
    static double ExitVolumeOracle(ThreadSpec spec, double radius, double height, double length)
    {
        var p = spec.ExtZr.OrderBy(v => v.Y).ThenBy(v => v.X).ToArray();
        double root = p[0].Y, q0 = p[0].X, q1 = p[1].X;
        double ml = (p[2].Y - root) / (p[2].X - q0), mr = (p[3].Y - root) / (p[3].X - q1);
        double depth = radius - root, lead = spec.Lead, end = length - q1;
        double span = Math.Min(lead, length + q0 + depth / ml - q1), start = end - span;
        double SectionLoss(double t)
        {
            double s = Math.Clamp((t - start) / span, 0, 1);
            double d = depth * s * s * (3 - 2 * s), loss = 0;
            double[] qs = { q0 + (depth - d) / ml, q0, q1, q1 + (depth - d) / mr };
            for (int k = 0; k < 3; k++)
            {
                double a = Math.Max(qs[k], -t), b = Math.Min(qs[k + 1], height - t);
                if (a >= b) continue;
                double slope = k == 0 ? ml : k == 2 ? mr : 0, corner = k == 0 ? q0 : q1;
                double ra = root + d + slope * (a - corner), rb = root + d + slope * (b - corner);
                loss += (b - a) * (radius * radius - (ra * ra + ra * rb + rb * rb) / 3);
            }
            return loss;
        }
        const int n = 32768;
        double a = -q1 - depth / mr, h = (end - a) / n, sum = SectionLoss(a) + SectionLoss(end);
        for (int i = 1; i < n; i++) sum += (i % 2 == 0 ? 2 : 4) * SectionLoss(a + h * i);
        return Math.PI * radius * radius * height - Math.PI / lead * h * sum / 3;
    }
}
