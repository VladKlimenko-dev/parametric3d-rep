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
    static void ThreadChecks()
    {
        var spec = ThreadDb.All.First(t => t.Designation == "M6" && t.Series == "coarse");
        Check("THREAD-TABLES", () =>
        {
            Require(ThreadDb.LoadError == null && ThreadDb.All.Count == 308, "supplied catalog missing or changed");
            Require(ThreadDb.All.Select(t => t.Family).Distinct().Count() == 9, "families");
            foreach (var t in ThreadDb.All)
                Require(t.P > 0 && t.ExtZr.Length >= 3 && t.IntZr.Length >= 3, t.Id);
        });
        Check("THREAD-ENTRY", () =>
        {
            foreach (bool inner in new[] { false, true })
            {
                var zr = inner ? spec.IntZr : spec.ExtZr;
                double entry = CosmeticThreadFeature.LeadInDistance(spec, inner);
                var a = CosmeticThreadFeature.ToolFrame(spec, inner, Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX, -entry, 2, false, false);
                Require(zr.All(p => (a.O + a.U * p.X + a.V * p.Y).Z < 0), "tool starts on/inside entrance");
                double full = -zr.Max(p => p.X);
                var b = CosmeticThreadFeature.ToolFrame(spec, inner, Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX, full, 2, false, false);
                Require((b.O - Vector3d.UnitZ * full).Length < 1e-12, "not full depth before first contact");
                var c = CosmeticThreadFeature.NonOverlappingProfile(spec, inner);
                Require(c.Max(p => p.X) - c.Min(p => p.X) < spec.P, "adjacent turns overlap");
                Require(Math.Abs((inner ? c.Max(p => p.Y) : c.Min(p => p.Y)) -
                    (inner ? zr.Max(p => p.Y) : zr.Min(p => p.Y))) < 1e-12, "groove depth changed");
            }
        });
        Check("THREAD-CSV-BOM", () =>
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "threads_table.csv"));
            lines[0] = "\uFEFF" + lines[0].TrimStart('\uFEFF');
            var rows = ThreadDb.ParseCsv(lines);
            Require(rows.Count == 308 && rows.All(t => !string.IsNullOrEmpty(t.Family)), "CSV BOM/header/catalog");
        });
        Check("THREAD-CATALOG-PREVIEW", () =>
        {
            long before = DirectRound.ExactSweeps + DirectRound.ExactThreadCells;
            foreach (var row in ThreadDb.All)
                foreach (bool inner in new[] { false, true })
                {
                    var lines = CosmeticThreadFeature.ViewLines(Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX,
                        inner ? row.IntStartR : row.ExtStartR, row.P, row.Starts, 10 * row.P, false, row, inner, true);
                    Require(lines.Count > 0 && lines.Count <= 6100, "catalog preview line budget: " + row.Id);
                    Require(lines.All(l => double.IsFinite(l.A.Length + l.B.Length)), "non-finite preview: " + row.Id);
                }
            Require(DirectRound.ExactSweeps + DirectRound.ExactThreadCells == before, "preview built exact cells");
        });
        Check("THREAD-SHORT-EXIT-REFUSAL-ATOMIC", () =>
        {
            var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, 2);
            var ctx = new BuildContext { Part = new Part("exit refusal"), Parameters = new ParameterTable() };
            ctx.Bodies.Add(body);
            var f = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = 0.4, SmoothExit = true };
            f.OnFaces.Add(new Vector3d(3, 0, 1));
            bool refused = false;
            try { f.Execute(ctx); } catch (RebuildException) { refused = true; }
            Require(refused && ReferenceEquals(body, ctx.Bodies.Single()), "unsupported exit changed body");
        });
        Check("THREAD-CANCEL-ATOMIC", () =>
        {
            var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, 2);
            var ctx = new BuildContext { Part = new Part("thread cancelled"), Parameters = new ParameterTable() };
            ctx.Bodies.Add(body);
            var f = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = 1 };
            f.OnFaces.Add(new Vector3d(3, 0, 1));
            using var scope = new KernelContext(cancellation: new System.Threading.CancellationToken(true)).Enter();
            bool cancelled = false;
            try { f.Execute(ctx); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && ReferenceEquals(body, ctx.Bodies.Single()), "cancel published a partial body");
        });
        foreach (bool left in new[] { false, true })
            Check("THREAD-SECTION-" + (left ? "LH" : "RH"), () =>
            {
                var section = CosmeticThreadFeature.ThreadSection(spec, 3, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), left);
                Require(section.Loops.Count == 1 && !section.HasSelfIntersections, "section not a simple contour");
                double worst = 0;
                // Independent radial equation for the supplied M6 trapezoid, including its rounded decimal coordinates.
                var bottom = spec.ExtZr.OrderBy(p => p.Y).ThenByDescending(p => p.X).First();
                var top = spec.ExtZr.OrderByDescending(p => p.Y).ThenByDescending(p => p.X).First();
                double slope = (top.Y - bottom.Y) / (top.X - bottom.X);
                foreach (var curve in section.Loops[0].Curves)
                    for (int i = 0; i <= 100; i++)
                    {
                        var p = curve.PointAt(curve.T0 + (curve.T1 - curve.T0) * i / 100);
                        double x = Math.Abs(Math.Atan2(p.Y, p.X)) * spec.P / (2 * Math.PI);
                        double expected = Math.Min(3, bottom.Y + slope * Math.Max(0, x - bottom.X));
                        worst = Math.Max(worst, Math.Abs(new Vector2d(p.X, p.Y).Length - expected));
                    }
                Log(new { test = "THREAD-SECTION-ERROR", left, worst });
                Require(worst <= 2e-8, "section radial error " + worst);
            });
        foreach (double length in new[] { 2.0, 20.0, 200.0 })
            Check("THREAD-PREVIEW-" + length, () =>
            {
                var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, length);
                var ctx = new BuildContext { Part = new Part("thread test"), Parameters = new ParameterTable() };
                ctx.Bodies.Add(body);
                var f = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = length, ShowPreview = true };
                f.OnFaces.Add(new Vector3d(3, 0, length / 2));
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) f.Execute(ctx);
                Log(new { test = "THREAD-PREVIEW-TIMING", length, meanMs = sw.Elapsed.TotalMilliseconds / 10, lines = f.Lines?.Count });
                Require(ReferenceEquals(body, ctx.Bodies.Single()) && f.LastRoute == "overlay", "preview changed solid");
                Require(f.Lines is { Count: > 0 and <= 6100 }, "unbounded overlay");
            });
        foreach (var (left, length) in new[] { (false, 0.4), (false, 2.0), (true, 2.0), (false, 2.25), (true, 2.25), (false, 10.0) })
            Check($"THREAD-SOLID-{left}-{length}", () =>
            {
                var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, length);
                var ctx = new BuildContext { Part = new Part("thread test"), Parameters = new ParameterTable() };
                ctx.Bodies.Add(body);
                var f = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = length, LeftHand = left };
                f.OnFaces.Add(new Vector3d(3, 0, length / 2));
                long before = DirectRound.ExactSweeps + DirectRound.ExactThreadCells;
                var sw = Stopwatch.StartNew(); f.Execute(ctx); double ms = sw.Elapsed.TotalMilliseconds;
                long built = DirectRound.ExactSweeps + DirectRound.ExactThreadCells - before;
                Require(built == (length > spec.Lead && length % spec.Lead > 1e-10 ? 2 : 1), "periodic cells rebuilt instead of copied");
                var result = ctx.Bodies.Single();
                Require(f.LastRoute == "direct-screw" && result.Guarantee == GuaranteeLevel.ExactAnalytic, "not exact direct solid");
                Require(result.Faces.All(face => GeometryAudit.AuthoritativeSurface(face) is not FacetedSurface), "faceted modelling surface");
                var valid = SolidValidator.Validate(result);
                Require(valid.IsValid, valid.Summary());
                Require(result.Shells.Count == 1 && result.Edges.All(e => e.CoEdges.Count == 2), "unsewn period boundary");
                Require(result.Faces.Count(face => face.Tag == "cap:start") == 1
                    && result.Faces.Count(face => face.Tag == "cap:end") == 1, "internal caps survived");
                var bottom = spec.ExtZr.OrderBy(p => p.Y).ThenByDescending(p => p.X).First();
                var top = spec.ExtZr.OrderByDescending(p => p.Y).ThenByDescending(p => p.X).First();
                double root = bottom.Y, xb = bottom.X, xc = xb + (3 - root) * (top.X - xb) / (top.Y - root);
                double area = 2 * Math.PI / spec.P * (root * root * xb +
                    (xc - xb) * (root * root + 3 * root + 9) / 3 + 9 * (spec.P / 2 - xc));
                double volume = AnalyticVolume.Of(result) ?? double.NaN, expected = area * length;
                Log(new { test = "THREAD-SOLID-METRICS", left, length, ms, volume, expected, built, faces = result.Faces.Count() });
                Require(Math.Abs(volume - expected) < expected * 1e-6, "volume against independent section integral");
                if (length == 0.4)
                {
                    var data = BrepFile.Write(new[] { result });
                    var restored = BrepFile.ReadAll(data).Single().Body;
                    var restoredReport = SolidValidator.Validate(restored);
                    Require(restoredReport.IsValid, "saved B-rep: " + restoredReport.Summary());
                    double restoredVolume = AnalyticVolume.Of(restored) ?? double.NaN;
                    Require(Math.Abs(restoredVolume - expected) < expected * 1e-6, "saved B-rep volume");
                    Log(new { test = "THREAD-BREP-ROUNDTRIP", bytes = data.Length, volume = restoredVolume });
                }
                if (length == 2.25)
                {
                    double independent = AnalyticVolume.OfUncached(result) ?? double.NaN;
                    Require(Math.Abs(independent - expected) < expected * 1e-6, "uncached surface volume");
                    bool previousCacheSetting = GeometryAudit.CacheOffForTests;
                    try
                    {
                        GeometryAudit.CacheOffForTests = true;
                        var audit = GeometryAudit.Of(result);
                        Require(audit.FullyAnalytic, "uncached audit of copied geometry");
                    }
                    finally { GeometryAudit.CacheOffForTests = previousCacheSetting; }
                }
            });
        Check("THREAD-ROTATED", () =>
        {
            var x = TransformOp.Rotate(new Vector3d(1, 2, 3), new Vector3d(1, 1, 1), 37);
            var body = TopologyTransform.Apply(PrimitiveOp.Cylinder(Vector3d.Zero, 3, 1.25), x)!;
            var ctx = new BuildContext { Part = new Part("rotated thread"), Parameters = new ParameterTable() };
            ctx.Bodies.Add(body);
            var f = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = 1.25 };
            f.OnFaces.Add(x.Point(new Vector3d(3, 0, 0.6)));
            f.Execute(ctx);
            Require(f.LastRoute == "direct-screw" && SolidValidator.Validate(ctx.Bodies.Single()).IsValid, "rotated periodic solid");
        });
        Check("THREAD-ACCEPTANCE-FAILURE-ATOMIC", () =>
        {
            var body = PrimitiveOp.Cylinder(Vector3d.Zero, 3, 0.5);
            var ctx = new BuildContext { Part = new Part("acceptance refusal"), Parameters = new ParameterTable() };
            ctx.Bodies.Add(body);
            var f = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, Length = 0.5 };
            f.OnFaces.Add(new Vector3d(3, 0, 0.25));
            bool refused = false;
            try
            {
                BooleanOp.ForceAcceptFailure = true;
                try { f.Execute(ctx); } catch (RebuildException) { refused = true; }
            }
            finally { BooleanOp.ForceAcceptFailure = false; }
            Require(refused && ReferenceEquals(body, ctx.Bodies.Single()), "failed acceptance changed body");
        });
    }
}
