using Parametric3D.Kernel.Ops;
using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    // ----------------------------------------------------------------------- realistic trees
    static Sketch RectSketch(Part p, double z, double x0, double y0, double w, double d)
    {
        var f = new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s);
        var a = new SkPoint(x0, y0); var b = new SkPoint(x0 + w, y0); var c = new SkPoint(x0 + w, y0 + d); var e = new SkPoint(x0, y0 + d);
        s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkLine(b, c)); s.AddEntity(new SkLine(c, e)); s.AddEntity(new SkLine(e, a));
        return s;
    }
    static Sketch CircleSketch(Part p, double z, double x, double y, double r)
    {
        var f = new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s);
        s.AddEntity(new SkCircle(new SkPoint(x, y), r));
        return s;
    }

    /// <summary>
    /// A part like the ones drawn by hand: a plate, pockets and bosses from sketches, holes
    /// (simple, through, counterbored), rounds and chamfers on vertical corner edges.
    /// Seeded; feature mix and sizes vary, positions stay inside the plate.
    /// </summary>
    internal static Part RealisticPart(int seed, int features, out List<string> recipe)
    {
        var rnd = new Random(seed);
        recipe = new List<string>();
        var p = new Part($"real{seed}");
        double W = 160, D = 100, H = 20;
        var plate = RectSketch(p, -H / 2, -W / 2, -D / 2, W, D);
        var ex = new ExtrudeFeature("Plate") { SketchId = plate.Id };
        ex.Side1.Distance = new ValueRef(H);
        p.AddFeature(ex);
        double top = H / 2;
        var used = new List<(double X, double Y, double R)>();
        bool Free(double x, double y, double r) =>
            Math.Abs(x) + r < W / 2 - 4 && Math.Abs(y) + r < D / 2 - 4 && used.All(u => (new Vector2d(x - u.X, y - u.Y)).Length > r + u.R + 3);

        for (int i = 1; i < features; i++)
        {
            int kind = rnd.Next(10);
            double x = (rnd.NextDouble() - 0.5) * (W - 30), y = (rnd.NextDouble() - 0.5) * (D - 30);
            switch (kind)
            {
                case 0: case 1: case 2:   // hole
                {
                    double r = 1.5 + rnd.NextDouble() * 3;
                    if (!Free(x, y, r + 3)) { i--; continue; }
                    used.Add((x, y, r + 3));
                    var h = new HoleFeature($"Hole{i}") { Direction = -Vector3d.UnitZ };
                    h.Points.Add(new Vector3d(x, y, top));
                    h.Diameter = new ValueRef(2 * r);
                    if (rnd.Next(3) == 0) { h.Kind = HoleKind.Counterbore; h.ThroughAll = true; h.HeadDiameter = new ValueRef(2 * r + 4); h.HeadDepth = new ValueRef(3); }
                    else if (rnd.Next(2) == 0) h.ThroughAll = true; else h.Depth = new ValueRef(6 + rnd.NextDouble() * 8);
                    p.AddFeature(h); recipe.Add($"hole {h.Kind} d{2 * r:0.0}");
                    break;
                }
                case 3: case 4:           // rectangular pocket
                {
                    double w = 8 + rnd.NextDouble() * 20, d = 6 + rnd.NextDouble() * 14;
                    if (!Free(x, y, Math.Max(w, d) / 2 + 2)) { i--; continue; }
                    used.Add((x, y, Math.Max(w, d) / 2 + 2));
                    var s = RectSketch(p, top, x - w / 2, y - d / 2, w, d);
                    var cut = new ExtrudeFeature($"Pocket{i}") { SketchId = s.Id, Cut = true };
                    cut.Side1.Distance = new ValueRef(3 + rnd.NextDouble() * 6); cut.Side1.Flip = true;
                    p.AddFeature(cut); recipe.Add("pocket");
                    break;
                }
                case 5: case 6:           // round boss
                {
                    double r = 3 + rnd.NextDouble() * 5;
                    if (!Free(x, y, r + 2)) { i--; continue; }
                    used.Add((x, y, r + 2));
                    var s = CircleSketch(p, top, x, y, r);
                    var boss = new ExtrudeFeature($"Boss{i}") { SketchId = s.Id };
                    boss.Side1.Distance = new ValueRef(4 + rnd.NextDouble() * 8);
                    p.AddFeature(boss); recipe.Add("boss");
                    break;
                }
                default:                  // round or chamfer on a vertical plate corner edge
                {
                    p.Rebuild(null);
                    var corners = new[] { (80.0, 50.0), (-80.0, 50.0), (-80.0, -50.0), (80.0, -50.0) };
                    TEdge? e = null;
                    foreach (var (cx, cy) in corners.OrderBy(_ => rnd.Next()))
                    {
                        e = p.Bodies.SelectMany(bb => bb.Edges).FirstOrDefault(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 &&
                            Math.Abs(x.V0.P.X - cx) < 1e-6 && Math.Abs(x.V0.P.Y - cy) < 1e-6 && Math.Abs(x.V1.P.X - cx) < 1e-6 && Math.Abs(x.V1.P.Y - cy) < 1e-6);
                        if (e != null) break;
                    }
                    if (e == null) { recipe.Add("(no free corner)"); continue; }
                    Feature f;
                    if (rnd.Next(2) == 0) { var rf = new RoundFeature($"Round{i}") { Radius = new ValueRef(2 + rnd.NextDouble() * 4) }; rf.Edges.Add(EdgeRef.Of(e)); f = rf; }
                    else { var cf = new ChamferFeature($"Chamfer{i}") { D1 = new ValueRef(1 + rnd.NextDouble() * 2), D2 = new ValueRef(1 + rnd.NextDouble() * 2) }; cf.Edges.Add(EdgeRef.Of(e)); f = cf; }
                    p.AddFeature(f); recipe.Add(f is RoundFeature ? "round" : "chamfer");
                    break;
                }
            }
        }
        return p;
    }

    static void K08()
    {
        var rows = new List<(int Seed, int N, double Full, double NoOp, double EditLast, double EditMid, double AllocMB, bool IncEqFull, int Errors, string Verdict)>();
        foreach (var (seed, n) in new[] { (8001, 20), (8002, 30), (8003, 40), (8004, 60) })
        {
            var sw = Stopwatch.StartNew();
            var p = RealisticPart(seed, n, out var recipe);
            double gen = sw.Elapsed.TotalMilliseconds;
            long a0 = GC.GetTotalAllocatedBytes(true);
            sw.Restart(); p.Invalidate(); p.Rebuild(null); double full = sw.Elapsed.TotalMilliseconds;
            double alloc = (GC.GetTotalAllocatedBytes(true) - a0) / 1e6;
            sw.Restart(); p.AppearanceChanged(); p.Rebuild(null); double noop = sw.Elapsed.TotalMilliseconds;
            var holes = p.Features.OfType<HoleFeature>().ToList();
            var lastHole = holes.LastOrDefault(); var midHole = holes.Count > 1 ? holes[holes.Count / 2] : null;
            double editLast = 0, editMid = 0;
            if (lastHole != null) { lastHole.Diameter = new ValueRef(lastHole.Diameter.Value * 0.9); sw.Restart(); p.MarkDirtyFrom(lastHole); p.Rebuild(null); editLast = sw.Elapsed.TotalMilliseconds; }
            if (midHole != null) { midHole.Diameter = new ValueRef(midHole.Diameter.Value * 0.9); sw.Restart(); p.MarkDirtyFrom(midHole); p.Rebuild(null); editMid = sw.Elapsed.TotalMilliseconds; }
            int errors = p.Features.Count(f => f.Error != null);
            // Reference: the same tree, full rebuild without checkpoints.
            var q = RealisticPart(seed, n, out _);
            var qh = q.Features.OfType<HoleFeature>().ToList();
            if (qh.Count > 0) qh[^1].Diameter = new ValueRef(qh[^1].Diameter.Value * 0.9);
            if (qh.Count > 1) qh[qh.Count / 2].Diameter = new ValueRef(qh[qh.Count / 2].Diameter.Value * 0.9);
            bool prev = Part.UseCheckpoints; Part.UseCheckpoints = false;
            try { q.Invalidate(); q.Rebuild(null); } finally { Part.UseCheckpoints = prev; }
            bool eq = PartGeometry(p) == PartGeometry(q);
            var v = p.Bodies.Select(b => SolidValidator.Validate(b)).ToList();
            string verdict = string.Join(" | ", v.Select(x => x.Summary()));
            Log(new { k08 = seed, features = p.Features.Count, recipe = string.Join(",", recipe.GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}")),
                      genMs = gen, fullMs = full, noopMs = noop, editLastMs = editLast, editMidMs = editMid, allocMB = alloc,
                      errors, errorList = p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error).Take(4),
                      incEqFull = eq, verdict, p.Integrity, volume = p.Volume });
            rows.Add((seed, n, full, noop, editLast, editMid, alloc, eq, errors, verdict));
        }
        Check("K08-REALISTIC-INCREMENTAL-EQUALS-FULL", () => Require(rows.All(r => r.IncEqFull), "incremental differs from full on " + string.Join(",", rows.Where(r => !r.IncEqFull).Select(r => r.Seed))));
        Check("K08-REALISTIC-NO-FEATURE-ERRORS", () => Require(rows.All(r => r.Errors == 0), "feature errors: " + string.Join(",", rows.Where(r => r.Errors > 0).Select(r => $"{r.Seed}:{r.Errors}"))));
        Check("K08-REALISTIC-VALID", () => Require(rows.All(r => !r.Verdict.Contains("error")), "invalid results: " + string.Join(" ; ", rows.Where(r => r.Verdict.Contains("error")).Select(r => $"{r.Seed}: {r.Verdict}"))));
    }
}
static partial class Program
{
    static void K08Bisect(int seed, int n)
    {
        var p = RealisticPart(seed, n, out _);
        p.Invalidate(); p.Rebuild(null);
        foreach (var (after, bodies) in Checkpoints(p).OrderBy(c => c.After))
        {
            var f = p.Features[after];
            var reps = bodies.Select(b => b.Exact == null ? null : SolidValidator.ValidateExact(b.Exact.Solid)).ToList();
            bool bad = reps.Any(r => r != null && !r.IsValid);
            Log(new { bisect = seed, after, feature = f.Name, kind = f.GetType().Name, bodies = bodies.Count, exactOk = !bad,
                      first = reps.Where(r => r != null && !r.IsValid).SelectMany(r => r!.Issues).Take(3).Select(i => i.ToString()) });
            if (bad) break;
        }
    }
}
static partial class Program
{
    static void K08Detail(int seed, int n, string name)
    {
        var p = RealisticPart(seed, n, out _);
        foreach (var f in p.Features)
        {
            string d = f switch
            {
                HoleFeature h => $"hole at {h.Points[0]} d={h.Diameter.Value:0.###} kind={h.Kind} through={h.ThroughAll} depth={h.Depth.Value:0.###} head={h.HeadDiameter.Value:0.###}/{h.HeadDepth.Value:0.###}",
                ExtrudeFeature e => $"extrude cut={e.Cut} dist={e.Side1.Distance.Value:0.###} flip={e.Side1.Flip} sketch={string.Join(";", p.Sketches.First(s => s.Id == e.SketchId).Entities.Select(x => x is SkCircle c ? $"circle({c.Center.X:0.###},{c.Center.Y:0.###}) r{c.Radius:0.###}" : x.GetType().Name))} z={p.Sketches.First(s => s.Id == e.SketchId).Plane.Origin.Z}",
                _ => f.GetType().Name
            };
            Log(new { detail = f.Name, d });
            if (f.Name == name) break;
        }
    }
}
static partial class Program
{
    static void K08Repro()
    {
        var p = RealisticPart(8002, 30, out _);
        p.Invalidate(); p.Rebuild(null);
        foreach (var (after, bodies) in Checkpoints(p).OrderBy(c => c.After))
        {
            var b0 = bodies[0];
            var rep = SolidValidator.Validate(b0, null, new ValidationOptions { CheckSelfIntersections = true });
            var faces = b0.Faces.ToList();
            double zmax = b0.Vertices.Max(v => v.P.Z);
            Log(new { repro = after, feature = p.Features[after].Name, exact = b0.Exact != null, qexact = b0.QExact != null, guarantee = b0.Guarantee.ToString(),
                      verdict = rep.Summary(), vol = rep.Metrics.GetValueOrDefault("mesh.volume"), zmax, faces = faces.Count,
                      cyl = faces.Where(f => f.Surface is Parametric3D.Kernel.Surfaces.ExtrudedSurface).Select(f => $"{f.Id}:z[{f.Edges.SelectMany(e => new[] { e.V0.P.Z, e.V1.P.Z }).Min():0.##},{f.Edges.SelectMany(e => new[] { e.V0.P.Z, e.V1.P.Z }).Max():0.##}]").Take(8) });
            if (after >= 12) break;
        }
    }
}
static partial class Program
{
    static void MemProfile(int seed, int n)
    {
        var p = RealisticPart(seed, n, out _);
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        long a0 = GC.GetAllocatedBytesForCurrentThread(); var sw = Stopwatch.StartNew();
        p.Invalidate(); p.Rebuild(null);
        Console.WriteLine($"full build {sw.Elapsed.TotalMilliseconds:0} ms, allocated {(GC.GetAllocatedBytesForCurrentThread() - a0) / 1048576.0:0} MB, features {p.Features.Count}");
        Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where((l, i) => i < 30 || l.Contains('#'))));
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = false;
    }
}
static partial class Program
{
    static void K07Drill()
    {
        Check("K07-DRILL-STRAIGHT-EQUALS-ROTATED", () =>
        {
            using var __poly = PolyhedralPath();   // сравнение двух путей одного (многогранного) ядра
            // Body with a boss so the quadric hole path is declined and the general Boolean path runs.
            var w = new List<string>();
            var body = new List<TSolid> { Box(0, 0, -10, 60, 40, 20) };
            BooleanOp.Unite(body, PrimitiveOp.Cylinder(new Vector3d(50, 30, 10), 4, 6), w);
            foreach (var (x, y, r, h) in new[] { (15.3, 12.7, 3.1, 30.0), (30.0, 20.0, 2.0, 8.0), (44.4, 9.1, 4.2, 30.0) })
            {
                var at = new Vector3d(x, y, 10.05);
                var rotated = TransformOp.Copy(PrimitiveOp.Cylinder(Vector3d.Zero, r, h), new TransformOp.Xform(p => new Vector3d(p.X, -p.Y, -p.Z) + at, v => new Vector3d(v.X, -v.Y, -v.Z), false));
                var straight = PrimitiveOp.Cylinder(at - Vector3d.UnitZ * h, r, h);
                var a = new List<TSolid>(body); BooleanOp.Subtract(a, rotated, w);
                var b = new List<TSolid>(body); BooleanOp.Subtract(b, straight, w);
                var va = a[0].Exact!.Solid.VolumeMm3(); var vb = b[0].Exact!.Solid.VolumeMm3();
                double rel = Math.Abs((va.ToDouble() - vb.ToDouble()) / va.ToDouble());
                Log(new { test = "K07-DRILL", x, r, h, rotated = va.ToDouble(), straight = vb.ToDouble(), rel, exactEqual = va.Equals(vb),
                          validA = SolidValidator.Validate(a[0]).IsValid, validB = SolidValidator.Validate(b[0]).IsValid });
                // С 2026-10-10 поворот резца — ТОЧНЫЙ перенос (TopologyTransform), а не пересборка из треугольников прямого
                // резца: шов и гранение повёрнутого цилиндра — свои, и многогранные результаты совпадают с точностью гранения
                // (замер: 3e-5), а не побитово.
                Require(rel < 1e-4 && SolidValidator.Validate(b[0]).IsValid, $"drill at ({x},{y}): {va.ToDouble()} vs {vb.ToDouble()}");
                body = b;
            }
        });
    }
}
static partial class Program
{
    static void K07Boss()
    {
        Check("K07-BOSS-DIRECT-EQUALS-GENERAL", () =>
        {
            using var __poly = PolyhedralPath();   // сравнение двух способов МНОГОГРАННОГО ввода цилиндра (A04 и общий)
            foreach (var (x, y, r, h) in new[] { (12.3, 7.7, 3.3, 6.0), (40.0, 25.0, 5.1, 9.7) })
            {
                // Like ExtrudeFeature: profile plane origin at the sketch origin, not at the circle centre.
                var plane = new Frame3(new Vector3d(0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
                var circle = new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(x, y, 10), Vector3d.UnitX, Vector3d.UnitY), r, 0, 2 * Math.PI);
                TSolid Tool() => ExtrudeOp.Build(ProfileBuilder.Build(plane, new List<Parametric3D.Kernel.Curves.ICurve3> { circle }), new Vector3d(0, 0, h));
                var w = new List<string>();
                var a = new List<TSolid> { Box(0, 0, -10, 60, 40, 20) }; BooleanOp.Unite(a, Tool(), w);
                Environment.SetEnvironmentVariable("P3D_CYLINDERINPUT", "0");
                List<TSolid> b;
                try { b = new List<TSolid> { Box(0, 0, -10, 60, 40, 20) }; BooleanOp.Unite(b, Tool(), w); }
                finally { Environment.SetEnvironmentVariable("P3D_CYLINDERINPUT", null); }
                var va = a[0].Exact!.Solid.VolumeMm3().ToDouble(); var vb = b[0].Exact!.Solid.VolumeMm3().ToDouble();
                Log(new { test = "K07-BOSS", x, r, direct = va, general = vb, rel = Math.Abs(va - vb) / vb, valid = SolidValidator.Validate(a[0]).IsValid });
                Require(Math.Abs(va - vb) <= 1e-9 * vb && SolidValidator.Validate(a[0]).IsValid, $"boss {x}: {va} vs {vb}");
            }
        });
    }
}
