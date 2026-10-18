using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using System.Diagnostics;
using Parametric3D.Kernel.Ops;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    static void GeometryAuditRun()
    {
        void One(string id, TSolid b)
        {
            var a = GeometryAudit.Of(b);
            Log(new { audit = id, summary = a.Summary(),
                      faceted = a.Edges.Where(e => e.Level == GeometryLevel.Faceted).GroupBy(e => e.Curve).Select(g => $"{g.Key}×{g.Count()}"),
                      inconsistent = a.Edges.Where(e => e.Level == GeometryLevel.Inconsistent).Take(4).Select(e => $"{e.Curve} {e.OnA:E1}/{e.OnB:E1}"),
                      facetedFaces = a.Faces.Where(f => f.Level != GeometryLevel.Analytic).Count() });
        }
        foreach (var id in Scenarios) { var w = new List<string>(); foreach (var b in Scenario(id, w)) One(id, b); }
        foreach (var seed in new[] { 8001, 8002, 8003 })
        {
            var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
            foreach (var b in p.Bodies) One($"REAL-{seed}", b);
        }
    }
}
static partial class Program
{
    static void K08Analytic()
    {
        Check("K08-AUDIT-CORPUS-CONSISTENT", () =>
        {
            int faceted = 0, inconsistent = 0, total = 0;
            foreach (var id in Scenarios) { var w = new List<string>(); foreach (var b in Scenario(id, w)) { var a = GeometryAudit.Of(b); total += a.Edges.Count; faceted += a.Edges.Count(e => e.Level == GeometryLevel.Faceted); inconsistent += a.InconsistentEdges; } }
            Log(new { test = "K08-AUDIT", total, faceted, inconsistent });
            Require(inconsistent == 0, $"{inconsistent} edges contradict their surfaces");
        });
        Check("K08-AUDIT-DETECTS-WRONG-CURVE", () =>
        {
            var w = new List<string>(); var b = Scenario("S-HOLE", w)[0];
            var e = b.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3);
            var arc = (Parametric3D.Kernel.Curves.ArcCurve3)e.Curve;
            e.Curve = new Parametric3D.Kernel.Curves.ArcCurve3(arc.Frame, arc.Radius + 0.2, arc.A0, arc.A1);
            var a = GeometryAudit.Of(b);
            Require(a.InconsistentEdges == 1, "wrong arc not detected");
            Require(SolidValidator.ValidateBRep(b).Issues.Any(i => i.Code == "curve-off-surface" && i.Severity == ValidationSeverity.Error), "validator does not report it");
        });
        Check("K08-AUDIT-POLYLINE-IS-FACETED", () =>
        {
            var w = new List<string>(); var b = Scenario("S-HOLE", w)[0];
            var e = b.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3);
            var pts = new List<Vector3d>(); e.Curve.Tessellate(pts, true);
            e.Curve = new Parametric3D.Kernel.Curves.PolylineCurve3(pts);
            Require(GeometryAudit.Of(b).Edges.Count(x => x.Level == GeometryLevel.Faceted) == 1, "polyline edge not marked faceted");
        });
        Check("K08-MEASURE-ANALYTIC", () =>
        {
            var w = new List<string>(); var b = Scenario("S-HOLE", w)[0];
            var wall = b.Faces.First(f => AnalyticMeasure.Radius(f) != null);
            var r = AnalyticMeasure.Radius(wall)!.Value;
            var planes = b.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.PlaneSurface).ToList();
            var top = planes.OrderByDescending(f => f.Edges.Max(e => e.V0.P.Z)).First(f => f.Edges.All(e => Math.Abs(e.V0.P.Z - 20) < 1e-9));
            var bottom = planes.First(f => f.Edges.All(e => Math.Abs(e.V0.P.Z) < 1e-9));
            var t = AnalyticMeasure.PlaneDistance(top, bottom)!.Value;
            var side = b.Faces.First(f => AnalyticMeasure.Angle(f, top) is { } ang && Math.Abs(ang.Value - 90) < 1e-9);
            // The top rim may be stored as several arcs; together they are the full circle.
            var rims = b.Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-9).ToList();
            var parts = rims.Select(AnalyticMeasure.Length).ToList();
            var len = new Measured(parts.Sum(x => x.Value), parts.All(x => x.Exact) ? GeometryLevel.Analytic : GeometryLevel.Faceted, 0, $"{parts.Count} arcs");
            Log(new { test = "K08-MEASURE", radius = r.ToString(), thickness = t.ToString(), rim = len.ToString() });
            Require(r.Exact && r.Value == 8, $"radius {r}");
            Require(t.Exact && Math.Abs(t.Value - 20) < 1e-12, $"thickness {t}");
            Require(len.Exact && Math.Abs(len.Value - 2 * Math.PI * 8) < 1e-9, $"rim {len}");
            // Mesh-based radius for comparison: chord vertices lie on the circle, chord midpoints inside.
        });
        Check("K08-VOLUME-BOUND-HOLDS", () =>
        {
            foreach (var (id, exact) in new[] { ("S-HOLE", 48000 - Math.PI * 64 * 20), ("S-HOLES8", 48000 - 8 * Math.PI * 9 * 20),
                                                  ("S-SPHERE", 48000 - 2.0 / 3 * Math.PI * 512), ("S-BOX", 48000.0) })
            {
                var w = new List<string>(); var b = Scenario(id, w)[0];
                var v = AnalyticMeasure.Volume(b);
                Log(new { test = "K08-VOLUME", id, v = v.ToString(), analytic = exact, deviation = Math.Abs(v.Value - exact) });
                Require(Math.Abs(v.Value - exact) <= v.Error + 1e-6, $"{id}: |{v.Value} - {exact}| exceeds declared bound {v.Error}");
                if (id == "S-BOX") Require(v.Level == GeometryLevel.Analytic, "planar body volume must be analytic");
                Require(v.Level == GeometryLevel.Analytic && v.Error < 1, $"{id}: analytic volume expected with a tight bound, got {v}");
            }
        });
    }
}
static partial class Program
{
    static void K08Edges()
    {
        Check("K08-TILTED-HOLE-EDGES-ANALYTIC", () =>
        {
            foreach (double tilt in new[] { 0.2, 0.45, 0.8 })
            {
                var w = new List<string>();
                var b = new List<TSolid> { Box() };
                var dir = new Vector3d(tilt, 0.1, 1).Normalized();
                var tool = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 5, 60),
                    RotateZTo(dir, new Vector3d(30, 20, 10) - dir * 30));
                Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, tool, w);
                var a = GeometryAudit.Of(b[0]);
                var kinds = a.Edges.GroupBy(e => e.Curve).Select(g => $"{g.Key}×{g.Count()}");
                Log(new { test = "K08-TILTED", tilt, summary = a.Summary(), kinds, warnings = w.Where(x => x.StartsWith("Edges")) });
                Require(a.InconsistentEdges == 0, $"tilt {tilt}: {a.InconsistentEdges} edges contradict their surfaces");
                var vr = SolidValidator.Validate(b[0]); Require(vr.IsValid, "invalid body: " + vr.ToString());
                if (!vr.IsValid) Log(new { test = "K08-TILTED-MESH", tilt, verdict = vr.Summary(), first = vr.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3).Select(i => i.ToString()) });
            }
        });
    }

    static Parametric3D.Kernel.Ops.TransformOp.Xform RotateZTo(Vector3d dir, Vector3d at)
    {
        var z = Vector3d.UnitZ; var axis = Vector3d.Cross(z, dir); double ang = Math.Acos(Math.Clamp(Vector3d.Dot(z, dir), -1, 1));
        Vector3d Rot(Vector3d v)
        {
            if (axis.LengthSquared < 1e-18) return v;
            var k = axis.Normalized();
            return v * Math.Cos(ang) + Vector3d.Cross(k, v) * Math.Sin(ang) + k * Vector3d.Dot(k, v) * (1 - Math.Cos(ang));
        }
        return new Parametric3D.Kernel.Ops.TransformOp.Xform(p => Rot(p) + at, Rot, false);
    }
}
static partial class Program
{
    static void TiltProbe()
    {
        var w = new List<string>();
        var b = new List<TSolid> { Box() };
        var dir = new Vector3d(0.2, 0.1, 1).Normalized();
        var tool = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 5, 60), RotateZTo(dir, new Vector3d(30, 20, 10) - dir * 30));
        Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, tool, w);
        foreach (var f in b[0].Faces)
        {
            if (f.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface fs) continue;
            Vector3d Sum(IEnumerable<(Vector3d A, Vector3d B, Vector3d C)> t) => t.Aggregate(Vector3d.Zero, (acc, x) => acc + Vector3d.Cross(x.B - x.A, x.C - x.A));
            var nf = Sum(fs.Facets); var nd = fs.Display == null ? Vector3d.Zero : Sum(fs.Display);
            var nn = fs.DisplayNormals == null ? Vector3d.Zero : fs.DisplayNormals.Aggregate(Vector3d.Zero, (a, x) => a + x.A + x.B + x.C);
            int disagree = 0, sameDir = 0;
            if (fs.Display != null && fs.DisplayNormals != null)
            {
                var de = new Dictionary<(Vector3d, Vector3d), int>();
                for (int i = 0; i < fs.Display.Count; i++)
                {
                    var (a0, b0, c0) = fs.Display[i]; var g = Vector3d.Cross(b0 - a0, c0 - a0);
                    var (na, nb, nc) = fs.DisplayNormals[i];
                    if (Vector3d.Dot(na + nb + nc, g) < 0) disagree++;
                    foreach (var k in new[] { (a0, b0), (b0, c0), (c0, a0) }) de[k] = de.GetValueOrDefault(k) + 1;
                }
                sameDir = de.Count(kv => kv.Value > 1);
            }
            int outD = 0, inD = 0, outF = 0, inF = 0;
            if (f.Id % 1 == 0 && fs.Source is Parametric3D.Kernel.Surfaces.ExtrudedSurface { Base: Parametric3D.Kernel.Curves.ArcCurve3 })
            {
                var axisAt = new Vector3d(30, 20, 10);
                void Count(List<(Vector3d A, Vector3d B, Vector3d C)> L, ref int o, ref int ii)
                {
                    foreach (var (a0, b0, c0) in L)
                    {
                        var g = Vector3d.Cross(b0 - a0, c0 - a0); var m0 = (a0 + b0 + c0) / 3; var d0 = m0 - axisAt; var rad = d0 - dir * Vector3d.Dot(d0, dir);
                        if (Vector3d.Dot(g, rad) > 0) o++; else ii++;
                    }
                }
                Count(fs.Facets.ReadView, ref outF, ref inF); if (fs.Display != null) Count(fs.Display, ref outD, ref inD);
            }
            Log(new { face = f.Id, outF, inF, outD, inD, disagree, sameDir, tris = fs.Display?.Count, src = fs.Source?.GetType().Name, rev = f.Reversed, loops = f.Loops.Count,
                      facets = $"{nf.X:0.#},{nf.Y:0.#},{nf.Z:0.#}", display = $"{nd.X:0.#},{nd.Y:0.#},{nd.Z:0.#}", normals = $"{nn.X:0.#},{nn.Y:0.#},{nn.Z:0.#}" });
        }
        var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b[0]);
        var dirE = new Dictionary<((float,float,float),(float,float,float)), List<uint>>();
        for (int i = 0; i + 2 < m.Indices.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                var pa = m.Vertices[m.Indices[i + k]].P; var pb = m.Vertices[m.Indices[i + (k + 1) % 3]].P;
                var key = ((pa.X, pa.Y, pa.Z), (pb.X, pb.Y, pb.Z));
                if (!dirE.TryGetValue(key, out var l)) dirE[key] = l = new List<uint>();
                l.Add(m.Vertices[m.Indices[i]].FaceId);
            }
        var dup = dirE.Where(kv => kv.Value.Count > 1).SelectMany(kv => kv.Value).GroupBy(x => x).Select(g => $"face {g.Key}: {g.Count()}");
        Log(new { sameDirectionByFace = dup });
        {
            int o = 0, ii = 0, zdown = 0, zup = 0;
            for (int i = 0; i + 2 < m.Indices.Length; i += 3)
            {
                var va = m.Vertices[m.Indices[i]]; var a0 = new Vector3d(va.P.X, va.P.Y, va.P.Z);
                var b0 = new Vector3d(m.Vertices[m.Indices[i + 1]].P.X, m.Vertices[m.Indices[i + 1]].P.Y, m.Vertices[m.Indices[i + 1]].P.Z);
                var c0 = new Vector3d(m.Vertices[m.Indices[i + 2]].P.X, m.Vertices[m.Indices[i + 2]].P.Y, m.Vertices[m.Indices[i + 2]].P.Z);
                var g = Vector3d.Cross(b0 - a0, c0 - a0);
                if (va.FaceId == 110) { var mm = (a0 + b0 + c0) / 3 - new Vector3d(30, 20, 10); var rad = mm - dir * Vector3d.Dot(mm, dir); if (Vector3d.Dot(g, rad) > 0) o++; else ii++; }
                if (va.FaceId == 107) { if (g.Z < 0) zdown++; else zup++; }
            }
            Log(new { finalWall = new { o, ii }, finalBottom = new { zdown, zup } });
        }
        var vm = SolidValidator.ValidateMesh(m); Log(new { mesh = vm.Summary(), issues = vm.Issues.Take(8).Select(i => i.ToString()) });
    }
}
static partial class Program
{
    static void K08Shaft()
    {
        Check("K08-SHAFT-CROSS-HOLE", () =>
        {
            foreach (double d in new[] { 4.0, 8.0, 12.0 })
            {
                var p = new Part("shaft");
                var s = CircleSketch(p, 0, 0, 0, 10);
                var ex = new Parametric3D.Model.Features.ExtrudeFeature("Shaft") { SketchId = s.Id };
                ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60);
                p.AddFeature(ex);
                var h = new Parametric3D.Model.Features.HoleFeature("Cross") { Direction = -Vector3d.UnitY, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(d) };
                h.Points.Add(new Vector3d(0, 15, 30));
                p.AddFeature(h);
                var r = p.Compute(null); p.Apply(r);
                var b = p.Bodies[0];
                var faces = b.Faces.ToList();
                bool exactPath = faces.Any(f => f.Loops.Any(l => !l.IsOuter && l.CoEdges.Any(c => c.Edge.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3)));
                var v = SolidValidator.Validate(b, null, new ValidationOptions { CheckSelfIntersections = true });
                double exact = Math.PI * 100 * 60 - CrossHoleVolume(10, d / 2);
                Log(new { test = "K08-SHAFT", d, features = p.Features.Select(f => f.Error ?? "ok"), exactPath, verdict = v.Summary(), volume = p.Volume, analytic = exact,
                          geometry = GeometryAudit.Of(b).Summary(), warnings = r.Warnings.Take(3) });
                Require(p.Features.All(f => f.Error == null) && v.IsValid, $"d={d}: {v}");
                Require(Math.Abs(p.Volume - exact) < 2e-3 * exact, $"d={d}: volume {p.Volume} vs {exact}");
            }
        });
    }

    /// <summary>Volume removed by a through cross hole of radius r in a shaft of radius R (axes perpendicular, intersecting).</summary>
    static double CrossHoleVolume(double R, double r)
    {
        // ∫_{-r}^{r} 2·sqrt(r²−x²) · 2·sqrt(R²−x²) dx  (numerically, Simpson)
        int n = 20000; double h = 2 * r / n, sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double x = -r + i * h, f = 4 * Math.Sqrt(Math.Max(0, r * r - x * x)) * Math.Sqrt(Math.Max(0, R * R - x * x));
            sum += f * (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2);
        }
        return sum * h / 3;
    }
}
static partial class Program
{
    static void K08Volume()
    {
        Check("K08-ANALYTIC-VOLUME", () =>
        {
            var cases = new List<(string Id, TSolid Body, double Exact)>();
            foreach (var (id, exact) in new[] { ("S-BOX", 48000.0), ("S-HOLE", 48000 - Math.PI * 64 * 20), ("S-HOLES4", 48000 - 4 * Math.PI * 9 * 20),
                                                  ("S-HOLES8", 48000 - 8 * Math.PI * 9 * 20), ("S-SPHERE", 48000 - 2.0 / 3 * Math.PI * 512),
                                                  ("S-OVERLAP", 24000.0) })
            { var w = new List<string>(); cases.Add((id, Scenario(id, w)[0], exact)); }
            foreach (double d in new[] { 4.0, 8.0 })
            {
                var p = new Part("shaft"); var s = CircleSketch(p, 0, 0, 0, 10);
                var ex = new Parametric3D.Model.Features.ExtrudeFeature("Shaft") { SketchId = s.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); p.AddFeature(ex);
                var h = new Parametric3D.Model.Features.HoleFeature("Cross") { Direction = -Vector3d.UnitY, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(d) };
                h.Points.Add(new Vector3d(0, 15, 30)); p.AddFeature(h); p.Rebuild(null);
                cases.Add(($"SHAFT-d{d}", p.Bodies[0], Math.PI * 100 * 60 - CrossHoleVolume(10, d / 2)));
            }
            foreach (var (id, b, exact) in cases)
            {
                if (b.TopologyConvention == 0) { var nr = TopologyConventions.Normalize(b); if (b.TopologyConvention == 0) Log(new { notNormalized = id, report = nr.ToString(), nr.Problems }); }
                var va = AnalyticVolume.Of(b);
                if (id == "S-HOLE") foreach (var f in b.Faces.Where(x => x.Loops.Count == 2 && GeometryAudit.AuthoritativeSurface(x) is Parametric3D.Kernel.Surfaces.PlaneSurface))
                    foreach (var l in f.Loops)
                        foreach (var ce in l.CoEdges)
                        {
                            var e = ce.Edge; var c = e.Curve;
                            Log(new { dbgFace = f.Id, outer = l.IsOuter, rev = ce.Reversed, curve = c.GetType().Name, closed = c.IsClosed,
                                      c0v0 = (c.PointAt(c.T0) - e.V0.P).Length, c1v1 = (c.PointAt(c.T1) - e.V1.P).Length, c0v1 = (c.PointAt(c.T0) - e.V1.P).Length,
                                      sweep = c is Parametric3D.Kernel.Curves.ArcCurve3 a ? a.Sweep : 0, v0 = $"{e.V0.P.X:0.###},{e.V0.P.Y:0.###}", v1 = $"{e.V1.P.X:0.###},{e.V1.P.Y:0.###}" });
                        }
                if (id == "S-HOLE") foreach (var f in b.Faces) if (GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.PlaneSurface pl) Log(new { areas = f.Id, a = AnalyticVolume.LoopAreas(f, pl.F.Origin, pl.F.U, pl.F.V), origin = pl.F.Origin.ToString() });
                if (id == "S-HOLE") foreach (var f in b.Faces) Log(new { face = f.Id, src = GeometryAudit.AuthoritativeSurface(f)?.GetType().Name, integral = AnalyticVolume.FaceIntegral(f), loops = f.Loops.Select(l => $"{(l.IsOuter ? "O" : "I")}{l.CoEdges.Count}:" + string.Join("", l.CoEdges.Select(c => c.Reversed ? "-" : "+"))) });
                double vm = AnalyticMeasure.Volume(b).Value;
                Log(new { test = "K08-AV", id, analytic = va, mesh = vm, exact, errAnalytic = va == null ? -1 : Math.Abs(va.Value - exact), errMesh = Math.Abs(vm - exact) });
                if (va == null) foreach (var f in b.Faces) { var sf = GeometryAudit.AuthoritativeSurface(f); var fi = AnalyticVolume.FaceIntegral(f);
                    if (fi == null || double.IsNaN(fi.Value)) Log(new { unsupported = id, face = f.Id, surface = sf?.GetType().Name,
                        detail = sf is Parametric3D.Kernel.Surfaces.ExtrudedSurface ex2 ? $"base {ex2.Base.GetType().Name} dir {ex2.Dir} W·dir {(ex2.Base is Parametric3D.Kernel.Curves.ArcCurve3 ac ? Vector3d.Dot(ac.Frame.W.Normalized(), ex2.Dir.Normalized()) : 0):R}" : "", conv = b.TopologyConvention }); }
                Require(va != null, $"{id}: analytic volume not available");
                Require(Math.Abs(va!.Value - exact) <= 2e-6 * exact + 1e-3, $"{id}: analytic {va} vs exact {exact}");
            }
        });
    }
}
static partial class Program
{
    static void K08Area()
    {
        Check("K08-ANALYTIC-AREA-AND-PART", () =>
        {
            foreach (var (id, exactA) in new[] { ("S-BOX", 2 * (60 * 40 + 60 * 20 + 40 * 20.0)),
                                                  ("S-HOLE", 2 * (60 * 40 + 60 * 20 + 40 * 20.0) - 2 * Math.PI * 64 + 2 * Math.PI * 8 * 20),
                                                  ("S-SPHERE", 2 * (60 * 40 + 60 * 20 + 40 * 20.0) - Math.PI * 64 + 2 * Math.PI * 64) })
            {
                var w = new List<string>(); var b = Scenario(id, w)[0];
                if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
                var a = AnalyticVolume.AreaOf(b);
                Log(new { test = "K08-AREA", id, analytic = a, exact = exactA, mesh = Parametric3D.Kernel.Tess.MassProps.Area(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b)) });
                Require(a != null && Math.Abs(a.Value - exactA) < 1e-5 * exactA, $"{id}: area {a} vs {exactA}");
            }
            // Part: hole through a cube, volume from the analytic path.
            var p = new Part("k08area"); TestGeometry.AddCube(p, 60);
            p.AddFeature(new CutCyl(0, 0, 10)); var r = p.Compute(null); p.Apply(r);
            double expect = 216000 - Math.PI * 100 * 60;
            Log(new { test = "K08-PART-VOLUME", p.Volume, expect, r.MassFromMesh });
            Require(!r.MassFromMesh && Math.Abs(p.Volume - expect) < 0.01, $"part volume {p.Volume} vs {expect}, mesh={r.MassFromMesh}");
        });
    }
}
static partial class Program
{
    static void K08Revolved()
    {
        AnalyticVolume.Debug = m => Console.WriteLine("AVDBG " + m);
        Check("K08-ANALYTIC-VOLUME-REVOLVED", () =>
        {
            var rows = new List<(string, TSolid, double)>();
            // A closed torus face has no boundary: Stokes through the edge cannot apply (documented fallback).
            Require(AnalyticVolume.Of(PrimitiveOp.Torus(Vector3d.Zero, 20, 5)) == null, "closed torus must fall back");
            var half = new List<TSolid> { PrimitiveOp.Torus(Vector3d.Zero, 20, 5) };
            BooleanOp.Intersect(half, Box(-40, 0, -10, 80, 40, 20), new List<string>());
            rows.Add(("half torus", half[0], Math.PI * Math.PI * 20 * 25));
            var cone = PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 15);
            rows.Add(("cone frustum", cone, Math.PI * 15 / 3 * (100 + 40 + 16)));
            // Countersunk through hole in a 20 mm plate (exact quadric path).
            double r = 3, Rh = 6, T = 20, h = Rh - r;
            var p = new Part("csk"); var sk = RectSketch(p, -10, -30, -30, 60, 60);
            var ex = new Parametric3D.Model.Features.ExtrudeFeature("Plate") { SketchId = sk.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(T); p.AddFeature(ex);
            var hole = new Parametric3D.Model.Features.HoleFeature("Csk") { Kind = Parametric3D.Model.Features.HoleKind.Countersink, ThroughAll = true,
                Diameter = new Parametric3D.Model.Features.ValueRef(2 * r), HeadDiameter = new Parametric3D.Model.Features.ValueRef(2 * Rh), HeadAngle = new Parametric3D.Model.Features.ValueRef(90) };
            hole.Points.Add(new Vector3d(0, 0, 10)); p.AddFeature(hole); p.Rebuild(null);
            rows.Add(("countersink", p.Bodies[0], 3600 * T - (Math.PI * r * r * (T - h) + Math.PI * h / 3 * (Rh * Rh + Rh * r + r * r))));
            foreach (var (id, b, exact) in rows)
            {
                if (b.TopologyConvention == 0) { var nr = TopologyConventions.Normalize(b); Log(new { normalize = id, report = nr.ToString(), nr.Problems }); }
                var va = AnalyticVolume.Of(b);
                double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
                Log(new { test = "K08-AV-REV", id, analytic = va ?? -1, mesh = vm, exact, conv = b.TopologyConvention,
                          faces = b.Faces.Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name + ":" + (AnalyticVolume.FaceIntegral(f)?.ToString("0.###") ?? "-")) });
                Require(va != null && Math.Abs(va.Value - exact) < 1e-5 * exact + 1e-3, $"{id}: {va} vs {exact}");
            }
        });
    }
}
static partial class Program
{
    static void FaceNormalProbe()
    {
        foreach (var (id, b) in new[] { ("cone", PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 15)), ("cyl", PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10)), ("sphere", PrimitiveOp.Sphere(Vector3d.Zero, 5)), ("box", Box()) })
        {
            var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            foreach (var f in b.Faces)
            {
                int agree = 0, disagree = 0;
                for (int i = 0; i + 2 < m.Indices.Length; i += 3)
                {
                    var va = m.Vertices[m.Indices[i]]; if (va.FaceId != f.Id) continue;
                    var a = new Vector3d(va.P.X, va.P.Y, va.P.Z); var bb = m.Vertices[m.Indices[i + 1]].P; var cc = m.Vertices[m.Indices[i + 2]].P;
                    var g = Vector3d.Cross(new Vector3d(bb.X, bb.Y, bb.Z) - a, new Vector3d(cc.X, cc.Y, cc.Z) - a);
                    var mid = (a + new Vector3d(bb.X, bb.Y, bb.Z) + new Vector3d(cc.X, cc.Y, cc.Z)) / 3;
                    if (Vector3d.Dot(g, f.NormalAt(mid)) > 0) agree++; else disagree++;
                }
                Log(new { normals = id, face = f.Id, surface = f.Surface.GetType().Name, f.Reversed, agree, disagree });
            }
        }
    }
}
static partial class Program
{
    static void AnalyticCost()
    {
        foreach (var seed in new[] { 8001, 8002, 8003, 8004 })
        {
            var p = RealisticPart(seed, 20, out _);
            var r = p.Compute(null); p.Apply(r);
            var b = p.Bodies[0];
            var sw = Stopwatch.StartNew(); var va = AnalyticVolume.Of(b); double tv = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); var aa = AnalyticVolume.AreaOf(b); double ta = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); var mp = AnalyticFlux.MassProperties(b); double tc = sw.Elapsed.TotalMilliseconds;
            Log(new { comCost = seed, ms = tc, fluxVolume = mp?.Volume, analytic = va, com = mp?.Centroid.ToString() });
            sw.Restart(); var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b); double vm = Parametric3D.Kernel.Tess.MassProps.Volume(m); double tm = sw.Elapsed.TotalMilliseconds;
            var unsupported = b.Faces.Where(f => AnalyticVolume.FaceIntegral(f) is not { } x || double.IsNaN(x)).Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name ?? "null").GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}");
            // Independent check: the mesh volume converges to the analytic value as the chord shrinks.
            var conv = new List<string>();
            foreach (double deg in new[] { 7.5, 3.0, 1.0 })
                using (new KernelContext(KernelSettings.Capture() with { ChordAngle = deg * Math.PI / 180, Chord = 0.002 }).Enter())
                {
                    var q = RealisticPart(seed, 20, out _); q.Rebuild(null);
                    var mq = Parametric3D.Kernel.Tess.Tessellator.Tessellate(q.Bodies[0], facets: true);
                    conv.Add($"{deg}°: facets {Parametric3D.Kernel.Tess.MassProps.Volume(mq):0.###} analytic {AnalyticVolume.Of(q.Bodies[0]):0.###}");
                }
            Log(new { cost = seed, conv, faces = b.Faces.Count(), edges = b.Edges.Count(), analytic = va, mesh = vm, diff = va == null ? -1 : va.Value - vm, volumeMs = tv, areaMs = ta, meshMs = tm,
                      partVolume = p.Volume, r.MassFromMesh, unsupported });
        }
    }
}
static partial class Program
{
    static void K08DetailInline(int seed)
    {
        var p = RealisticPart(seed, 20, out _);
        foreach (var f in p.Features.Take(4))
        {
            string d = f switch
            {
                Parametric3D.Model.Features.HoleFeature h => $"hole at {h.Points[0]} d={h.Diameter.Value:0.###} kind={h.Kind} through={h.ThroughAll} depth={h.Depth.Value:0.###} head={h.HeadDiameter.Value:0.###}/{h.HeadDepth.Value:0.###}",
                Parametric3D.Model.Features.ExtrudeFeature e => $"extrude cut={e.Cut} dist={e.Side1.Distance.Value:0.###}",
                _ => f.GetType().Name
            };
            Log(new { detail8001 = f.Name, d });
        }
    }

    static void ChordDependence()
    {
        K08DetailInline(8001);
        foreach (var seed in new[] { 8001 })
        {
            var runs = new List<(string Name, List<string> Recipe, List<string> Feats)>();
            foreach (double deg in new[] { 7.5, 3.0 })
                using (new KernelContext(KernelSettings.Capture() with { ChordAngle = deg * Math.PI / 180, Chord = 0.002 }).Enter())
                {
                    var p = RealisticPart(seed, 20, out var recipe); p.Rebuild(null);
                    runs.Add(($"{deg}", recipe, p.Features.Select(f => f.Name + (f.Error != null ? " ERR:" + f.Error[..Math.Min(140, f.Error.Length)] : "")).ToList()));
                }
            var vols = new List<List<double>>();
            foreach (double deg in new[] { 7.5, 3.0 })
                using (new KernelContext(KernelSettings.Capture() with { ChordAngle = deg * Math.PI / 180, Chord = 0.002 }).Enter())
                {
                    var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
                    var row = new List<double>();
                    foreach (var (after, bodies) in Checkpoints(p).OrderBy(c => c.After))
                        row.Add(bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN));
                    vols.Add(row);
                }
            var deltas = vols[0].Zip(vols[1], (x, y) => y - x).Select((d, i) => (i, d)).Where(t => Math.Abs(t.d) > 0.5).Take(5).Select(t => $"after {t.i}: {t.d:0.###}");
            Log(new { chordVol = seed, deltas, a = vols[0].Take(4).Select(x => x.ToString("0.###")), b = vols[1].Take(4).Select(x => x.ToString("0.###")) });
            int k = 0; while (k < Math.Min(runs[0].Recipe.Count, runs[1].Recipe.Count) && runs[0].Recipe[k] == runs[1].Recipe[k]) k++;
            Log(new { chordDep = seed, firstRecipeDiff = k, a = runs[0].Recipe.Skip(Math.Max(0, k - 1)).Take(3), b = runs[1].Recipe.Skip(Math.Max(0, k - 1)).Take(3),
                      errorsA = runs[0].Feats.Where(x => x.Contains("ERR")), errorsB = runs[1].Feats.Where(x => x.Contains("ERR")) });
        }
    }
}
static partial class Program
{
    static void BossRepro()
    {
        var ctx = new KernelContext(KernelSettings.Capture() with { ChordAngle = 7.5 * Math.PI / 180, Chord = 0.002 });
        using var scope = ctx.Enter();
        var p = RealisticPart(8001, 20, out var recipeIgnored); p.Rebuild(null);
        var cp = Checkpoints(p).ToDictionary(c => c.After, c => c.Bodies);
        var boss2 = (Parametric3D.Model.Features.ExtrudeFeature)p.Features[2];
        var circ = p.Sketches.First(s => s.Id == boss2.SketchId).Entities.OfType<Parametric3D.Model.Sketching.SkCircle>().Single();
        var boss1 = (Parametric3D.Model.Features.ExtrudeFeature)p.Features[1];
        var c1 = p.Sketches.First(s => s.Id == boss1.SketchId).Entities.OfType<Parametric3D.Model.Sketching.SkCircle>().Single();
        Log(new { boss1 = $"({c1.Center.X:0.###},{c1.Center.Y:0.###}) r{c1.Radius:0.###} h{boss1.Side1.Distance.Value:0.###}", boss2 = $"({circ.Center.X:0.###},{circ.Center.Y:0.###}) r{circ.Radius:0.###} h{boss2.Side1.Distance.Value:0.###}" });
        var before = cp[1];
        {
            // Input closure: tessellate the input body as the Boolean does and count unmatched sides.
            Parametric3D.Kernel.Tess.QMesher.Debug = m => Console.WriteLine("QMESH " + m);
            Console.WriteLine("QMESH fits=" + Parametric3D.Kernel.Tess.QMesher.Fits(before[0]));
            var shown = Parametric3D.Kernel.Tess.Tessellator.Tessellate(before[0]);
            Console.WriteLine("QMESH shown mesh: " + SolidValidator.ValidateMesh(shown).Summary());
            var mi = Parametric3D.Kernel.Tess.Tessellator.Tessellate(before[0], facets: true);
            var vm = SolidValidator.ValidateMesh(mi);
            Log(new { inputBody = "after boss1", faces = before[0].Faces.Select(f => f.Surface.GetType().Name + (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? "(" + fs.Source?.GetType().Name + ")" : "")),
                      verdict = SolidValidator.Validate(before[0]).Summary(), facetsMesh = vm.Summary(), qexact = before[0].QExact != null });
        }
        var tool = PrimitiveOp.Cylinder(new Vector3d(circ.Center.X, circ.Center.Y, 10), circ.Radius, boss2.Side1.Distance.Value);
        var w = new List<string>();
        var r = BooleanOp.Evaluate(before, tool, BooleanOp.BooleanKind.Unite, OperationPolicy.Strict, w);
        Log(new { repro = "boss2", status = r.Status.ToString(), r.Guarantee, vBefore = AnalyticVolume.Of(before[0]), vAfter = r.Bodies.Count > 0 ? AnalyticVolume.Of(r.Bodies[0]) : null,
                  exactBefore = before[0].Exact?.Solid.VolumeMm3().ToDouble(), exactAfter = r.Bodies.Count > 0 ? r.Bodies[0].Exact?.Solid.VolumeMm3().ToDouble() : null,
                  toolExact = Parametric3D.Kernel.Exact.ExactAdapter.ToExact(tool, new Parametric3D.Kernel.Exact.PlaneRegistry(), null).Solid.VolumeMm3().ToDouble(),
                  warnings = w, diags = r.Diagnostics.Select(d => d.ToString()) });
    }
}
static partial class Program
{
    static void K08ChordIndependence()
    {
        Check("K08-BUILD-INDEPENDENT-OF-CHORD", () =>
        {
            foreach (var seed in new[] { 8001, 8002 })
            {
                var rows = new List<List<double>>();
                foreach (var (deg, chord) in new[] { (7.5, 0.02), (7.5, 0.002), (3.0, 0.002) })
                    using (new KernelContext(KernelSettings.Capture() with { ChordAngle = deg * Math.PI / 180, Chord = chord }).Enter())
                    {
                        var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
                        Require(p.Features.All(f => f.Error == null), $"{seed} @{deg}°/{chord}: " + string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error)));
                        rows.Add(Checkpoints(p).OrderBy(c => c.After).Select(c => c.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN)).ToList());
                    }
                for (int k = 1; k < rows.Count; k++)
                    for (int i = 0; i < rows[0].Count; i++)
                        Require(Math.Abs(rows[k][i] - rows[0][i]) < 0.05, $"{seed}: after feature {i} volume {rows[k][i]:0.###} vs {rows[0][i]:0.###} under setting {k}");
            }
        });
    }
}
static partial class Program
{
    static void Real8001Mesh()
    {
        var p = RealisticPart(8001, 20, out _); p.Rebuild(null);
        var holes = p.Features.OfType<Parametric3D.Model.Features.HoleFeature>().ToList();
        holes[^1].Diameter = new Parametric3D.Model.Features.ValueRef(holes[^1].Diameter.Value * 0.9); p.MarkDirtyFrom(holes[^1]); p.Rebuild(null);
        if (holes.Count > 1) { var mh = holes[holes.Count / 2]; mh.Diameter = new Parametric3D.Model.Features.ValueRef(mh.Diameter.Value * 0.9); p.MarkDirtyFrom(mh); p.Rebuild(null); }
        var b = p.Bodies[0];
        foreach (var f in p.Features) Console.WriteLine($"FEAT {f.Name} {f.GetType().Name}");
        Parametric3D.Kernel.Tess.QMesher.Debug = m => Console.WriteLine("QMESH " + m);
        var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
        var v = SolidValidator.ValidateMesh(m);
        var dirE = new Dictionary<((float,float,float),(float,float,float)), List<uint>>();
        for (int i = 0; i + 2 < m.Indices.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                var pa = m.Vertices[m.Indices[i + k]].P; var pb = m.Vertices[m.Indices[i + (k + 1) % 3]].P;
                var key = ((pa.X, pa.Y, pa.Z), (pb.X, pb.Y, pb.Z));
                if (!dirE.TryGetValue(key, out var l)) dirE[key] = l = new List<uint>();
                l.Add(m.Vertices[m.Indices[i]].FaceId);
            }
        Console.WriteLine("SAMEDIR " + string.Join(",", dirE.Where(kv => kv.Value.Count > 1).SelectMany(kv => kv.Value).GroupBy(x => x).Select(g => $"face {g.Key}({b.Faces.First(f => f.Id == g.Key).Surface.GetType().Name}/{(b.Faces.First(f => f.Id == g.Key).Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? fs.Source?.GetType().Name : "")}/{EdgeRef.Bare(b.Faces.First(f => f.Id == g.Key).Tag)}/loops {b.Faces.First(f => f.Id == g.Key).Loops.Count}):{g.Count()}")));
        Log(new { mesh8001 = v.Summary(), issues = v.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(5).Select(i => i.ToString()),
                  quadricFaces = b.Faces.Count(f => EdgeRef.Bare(f.Tag).StartsWith("quadric:")),
                  walls = b.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface { Base: Parametric3D.Kernel.Curves.ArcCurve3 }).Select(f => $"{f.Id}:{EdgeRef.Bare(f.Tag)}:loops{f.Loops.Count}") });
    }
}
static partial class Program
{
    static Part BossRoundBoss()
    {
        var p = new Part("brb");
        var plate = RectSketch(p, -10, -80, -50, 160, 100);
        var ex = new Parametric3D.Model.Features.ExtrudeFeature("Plate") { SketchId = plate.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(20); p.AddFeature(ex);
        var s1 = CircleSketch(p, 10, -5.241, -9.035, 4.575);
        var b1 = new Parametric3D.Model.Features.ExtrudeFeature("Boss1") { SketchId = s1.Id }; b1.Side1.Distance = new Parametric3D.Model.Features.ValueRef(8.347); p.AddFeature(b1);
        p.Rebuild(null);
        var corner = p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.X - 80) < 1e-6 && Math.Abs(e.V0.P.Y - 50) < 1e-6 && Math.Abs(e.V1.P.X - 80) < 1e-6 && Math.Abs(e.V1.P.Y - 50) < 1e-6);
        var rf = new Parametric3D.Model.Features.RoundFeature("Round") { Radius = new Parametric3D.Model.Features.ValueRef(4) }; rf.Edges.Add(EdgeRef.Of(corner)); p.AddFeature(rf);
        var s2 = CircleSketch(p, 10, 39.996, -5.664, 7.174);
        var b2 = new Parametric3D.Model.Features.ExtrudeFeature("Boss2") { SketchId = s2.Id }; b2.Side1.Distance = new Parametric3D.Model.Features.ValueRef(9.21); p.AddFeature(b2);
        return p;
    }

    static void BrbProbe()
    {
        foreach (var (deg, chord) in new[] { (7.5, 0.02), (7.5, 0.002), (3.0, 0.002), (1.0, 0.001) })
            using (new KernelContext(KernelSettings.Capture() with { ChordAngle = deg * Math.PI / 180, Chord = chord }).Enter())
            {
                var p = BossRoundBoss(); p.Rebuild(null);
                Log(new { brb = $"{deg}/{chord}", errors = p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error![..Math.Min(150, f.Error!.Length)]),
                          vol = p.Volume, analytic = AnalyticVolume.Of(p.Bodies[0]), p.Integrity });
            }
    }
}
static partial class Program
{
    static void K08Centroid()
    {
        Check("K08-ANALYTIC-CENTROID", () =>
        {
            var cases = new List<(string, TSolid, double, Vector3d)>();
            var w = new List<string>();
            // Box 60×40×20 at origin with a through hole R8 at (30,20): centroid stays at the middle.
            cases.Add(("hole centred", Scenario("S-HOLE", w)[0], 48000 - Math.PI * 64 * 20, new Vector3d(30, 20, 10)));
            // Off-centre hole at (15,20) R6: x̄ = (48000·30 − π36·20·15)/V.
            var b = new List<TSolid> { Box() }; BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(15, 20, -2), 6, 24), w);
            double vh = 48000 - Math.PI * 36 * 20;
            cases.Add(("hole off-centre", b[0], vh, new Vector3d((48000 * 30 - Math.PI * 36 * 20 * 15) / vh, 20, 10)));
            // Sphere cap removed at the top: hemisphere R8 cut at z=20, centre (30,20,20).
            double vs = 48000 - 2.0 / 3 * Math.PI * 512, mz = (48000 * 10 - 2.0 / 3 * Math.PI * 512 * (20 - 3.0 * 8 / 8)) / vs;
            cases.Add(("sphere cut", Scenario("S-SPHERE", w)[0], vs, new Vector3d(30, 20, mz)));
            // Cone frustum r 10→4 over 15 along z.
            var cone = PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 15);
            double R1 = 10, R2 = 4, Hh = 15, vc = Math.PI * Hh / 3 * (R1 * R1 + R1 * R2 + R2 * R2);
            double zc = Hh * (R1 * R1 + 2 * R1 * R2 + 3 * R2 * R2) / (4 * (R1 * R1 + R1 * R2 + R2 * R2));
            cases.Add(("cone", cone, vc, new Vector3d(0, 0, zc)));
            {
                var p = new Part("com"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutCyl(10, 0, 5)); p.Rebuild(null);
                var com = p.CenterOfMass();
                double vh2 = 216000 - Math.PI * 25 * 60; double xc = -Math.PI * 25 * 60 * 10 / vh2;
                Log(new { test = "K08-PART-COM", com = com?.Centroid.ToString(), analytic = com?.Analytic, expect = xc });
                Require(com != null && com.Value.Analytic && Math.Abs(com.Value.Centroid.X - xc) < 1e-6 && Math.Abs(com.Value.Centroid.Y) < 1e-6, "part centre of mass");
            }
            foreach (var (id, body, ev, ec) in cases)
            {
                if (body.TopologyConvention == 0) TopologyConventions.Normalize(body);
                var mp = AnalyticFlux.MassProperties(body);
                var mesh = Parametric3D.Kernel.Tess.Tessellator.Tessellate(body);
                Log(new { test = "K08-COM", id, volume = mp?.Volume, exactV = ev, com = mp?.Centroid.ToString(), exactCom = ec.ToString(), err = mp == null ? -1 : (mp.Value.Centroid - ec).Length });
                Require(mp != null && Math.Abs(mp.Value.Volume - ev) < 1e-4 * ev && (mp.Value.Centroid - ec).Length < 1e-4, $"{id}: {mp}");
            }
        });
    }
}
static partial class Program
{
    static void StepFaces()
    {
        var w = new List<string>(); var b = Scenario(Environment.GetEnvironmentVariable("SCN") ?? "S-HOLES4", w)[0];
        var sw = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "p", w);
        File.WriteAllText("/tmp/holes4.step", sw.ToString());
        Log(new { writeWarnings = w.Where(x => x.StartsWith("STEP")), faces = b.Faces.Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name + "/" + (GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.RevolvedSurface rq ? rq.Base.GetType().Name : "")) });
        var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), w)[0];
        double FaceArea(TSolid s, TFace f)
        {
            var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb);
            var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md); return Parametric3D.Kernel.Tess.MassProps.Area(md);
        }
        Log(new { orig = b.Faces.Select(f => $"{GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}:{f.Loops.Count}:{FaceArea(b, f):0.#}") });
        Log(new { back = back.Faces.Select(f => $"{f.Surface.GetType().Name}:{f.Loops.Count}:{FaceArea(back, f):0.#}:rev{f.Reversed}"), verdict = SolidValidator.Validate(back).Summary(), brep = SolidValidator.ValidateBRep(back).Issues.Take(3).Select(i => i.ToString()), warnings = w.Where(x => x.StartsWith("STEP")) });
    }
}
static partial class Program
{
    static void StepRealistic()
    {
        Check("K10-STEP-ROUNDTRIP-REALISTIC", () =>
        {
            var bad = new List<string>();
            foreach (var seed in new[] { 8001, 8002, 8003, 8004 })
            {
                var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
                var b = p.Bodies[0];
                var w = new List<string>(); var sw = new StringWriter();
                bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, $"r{seed}", w);
                List<TSolid> back = ok ? Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), w) : new();
                double v0 = AnalyticVolume.Of(b) ?? -1;
                double v1 = back.Sum(x => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(x)));
                var verdict = back.Count == 1 ? SolidValidator.Validate(back[0]).Summary() : $"bodies {back.Count}";
                double? va1 = null;
                if (back.Count == 1) { var nrep = TopologyConventions.Normalize(back[0]); va1 = AnalyticVolume.Of(back[0]); if (back[0].TopologyConvention == 0) Log(new { normFail = seed, report = nrep.ToString(), nrep.Problems }); }
                Log(new { test = "K10-STEP-REAL-ANALYTIC", seed, before = v0, after = va1, diff = va1 == null ? -1 : va1.Value - v0, conv = back.Count == 1 ? back[0].TopologyConvention : -1 });
                if (va1 == null || Math.Abs(va1.Value - v0) > 1e-6 * v0) bad.Add($"{seed}: analytic volume after import {va1} vs {v0}");
                Log(new { test = "K10-STEP-REAL", seed, ok, faces = b.Faces.Count(), v0, v1, rel = Math.Abs(v1 - v0) / v0, verdict,
                          warnings = w.Where(x => x.StartsWith("STEP")).GroupBy(x => x.Length > 60 ? x[..60] : x).Select(g => $"{g.Key}×{g.Count()}") });
                if (!ok || back.Count != 1 || Math.Abs(v1 - v0) > 1e-3 * v0 || verdict.Contains("error")) bad.Add($"{seed}: {verdict}, {v1:0.#} vs {v0:0.#}");
            }
            Require(bad.Count == 0, string.Join("; ", bad));
        });
    }
}
static partial class Program
{
    static void StepShaft()
    {
        Check("K10-STEP-ROUNDTRIP-CROSS-HOLE", () =>
        {
            foreach (double d in new[] { 4.0, 8.0 })
            {
                var p = new Part("shaft"); var s = CircleSketch(p, 0, 0, 0, 10);
                var ex = new Parametric3D.Model.Features.ExtrudeFeature("Shaft") { SketchId = s.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); p.AddFeature(ex);
                var h = new Parametric3D.Model.Features.HoleFeature("Cross") { Direction = -Vector3d.UnitY, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(d) };
                h.Points.Add(new Vector3d(0, 15, 30)); p.AddFeature(h); p.Rebuild(null);
                var b = p.Bodies[0];
                var w = new List<string>(); var sw = new StringWriter();
                bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "shaft", w);
                var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), w);
                double v0 = AnalyticVolume.Of(b) ?? -1, vm = back.Count == 1 ? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(back[0])) : -1;
                var verdict = back.Count == 1 ? SolidValidator.Validate(back[0]).Summary() : $"bodies {back.Count}";
                double? va = null;
                if (back.Count == 1) { TopologyConventions.Normalize(back[0]); va = AnalyticVolume.Of(back[0]); }
                Log(new { test = "K10-STEP-SHAFT", d, ok, v0, vm, va, verdict, warnings = w.Where(x => x.StartsWith("STEP")).Select(x => x.Length > 120 ? x[..120] : x).Distinct() });
                Require(ok && back.Count == 1 && !verdict.Contains("error"), $"d={d}: {verdict}");
                // Rim vertices are snapped onto the true intersection (AnalyticEdges): the file matches the body.
                Require(va != null && Math.Abs(va.Value - v0) < 1e-7 * v0, $"d={d}: analytic volume after import {va} vs {v0}");
                double truth = Math.PI * 100 * 60 - CrossHoleVolume(10, d / 2);
                Require(Math.Abs(v0 - truth) < 1e-7 * truth, $"d={d}: body volume {v0} vs analytic truth {truth}");
            }
        });
    }
}
static partial class Program
{
    static void FitProbe()
    {
        var p = new Part("shaft"); var s = CircleSketch(p, 0, 0, 0, 10);
        var ex = new Parametric3D.Model.Features.ExtrudeFeature("Shaft") { SketchId = s.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); p.AddFeature(ex);
        var h = new Parametric3D.Model.Features.HoleFeature("Cross") { Direction = -Vector3d.UnitY, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(4) };
        h.Points.Add(new Vector3d(0, 15, 30)); p.AddFeature(h); p.Rebuild(null);
        foreach (var e in p.Bodies[0].Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3))
        {
            var c = e.Curve;
            Log(new { fit = e.Id, closed = (c.PointAt(c.T0) - c.PointAt(c.T1)).Length, len = (e.V0.P - e.V1.P).Length,
                      devs = new[] { 16, 32, 64, 128 }.Select(n => { var f = Parametric3D.Kernel.Curves.SplineFit.Fit(c, 1e9, n); return f == null ? "null" : f.Value.Deviation.ToString("E2"); }) });
        }
    }
}
static partial class Program
{
    static void VertexOff()
    {
        void One(string id, TSolid b)
        {
            double worst = 0; int n3 = 0, n2 = 0; string? at = null;
            foreach (var v in b.Vertices)
            {
                var faces = b.Edges.Where(e => e.V0 == v || e.V1 == v).SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                var surfs = faces.Select(GeometryAudit.AuthoritativeSurface).Where(x => x != null).Distinct().ToList();
                if (surfs.Count >= 3) n3++; else n2++;
                foreach (var s in surfs)
                {
                    double d = (s!.PointAt(s.ProjectToUV(v.P)) - v.P).Length;
                    if (d > worst) { worst = d; at = $"{surfs.Count} surfaces at ({v.P.X:0.###},{v.P.Y:0.###},{v.P.Z:0.###}) off {s.GetType().Name}"; }
                }
            }
            Log(new { vertexOff = id, worst, n2, n3, at });
        }
        foreach (var id in Scenarios) { var w = new List<string>(); foreach (var b in Scenario(id, w)) One(id, b); }
        foreach (var seed in new[] { 8001, 8003 }) { var p = RealisticPart(seed, 20, out _); p.Rebuild(null); One($"REAL-{seed}", p.Bodies[0]); }
    }
}
static partial class Program
{
    static void ForeignStep()
    {
        foreach (var unit in new[] { "none", "mm", "inch", "m" })
        Check($"K10-STEP-FOREIGN-BOX-HOLE {unit}", () =>
        {
            string text = File.ReadAllText(Fixture($"box_hole_{unit}.step"));
            var w = new List<string>();
            var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(text, w);
            Require(back.Count == 1, $"bodies {back.Count}; {string.Join(" | ", w)}");
            var b = back[0];
            foreach (var f in b.Faces)
                foreach (var l in f.Loops)
                    foreach (var ce in l.CoEdges)
                        if (ce.Edge.Curve is Parametric3D.Kernel.Curves.ArcCurve3 ac)
                        {
                            var ep = Parametric3D.Kernel.Tess.ExactFaceMesher.EdgePoints(ce.Edge).ToList(); if (ce.Reversed) ep.Reverse();
                            var nz = 0.0; for (int i = 0; i + 1 < ep.Count; i++) nz += Vector3d.Cross(ep[i], ep[i + 1]).Z / 2;
                            var tp = new List<Vector3d>(); ce.Edge.Curve.Tessellate(tp, true);
                            var nz2 = 0.0; for (int i = 0; i + 1 < tp.Count; i++) nz2 += Vector3d.Cross(tp[i], tp[i + 1]).Z / 2;
                            Log(new { dbgArc = f.Id, curveTess = nz2, same = ReferenceEquals(ce.Edge.Curve, ac), cacheCount = ce.Edge.PolylineCache?.Count, src = GeometryAudit.AuthoritativeSurface(f)?.GetType().Name, z = ac.Frame.Origin.Z, sweep = ac.Sweep, frameWz = ac.Frame.W.Z, ce.Reversed, newellZ = nz, faceRev = f.Reversed });
                        }
            double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
            var nr = TopologyConventions.Normalize(b);
            double? va = AnalyticVolume.Of(b);
            foreach (var f in b.Faces) Log(new { fi = f.Id, src = GeometryAudit.AuthoritativeSurface(f)?.GetType().Name, integral = AnalyticVolume.FaceIntegral(f), area = AnalyticVolume.FaceArea(f), loops = f.Loops.Select(l => (l.IsOuter ? "O" : "I") + string.Join("", l.CoEdges.Select(c => (c.Edge.Curve is Parametric3D.Kernel.Curves.ArcCurve3 ? "a" : "l") + (c.Reversed ? "-" : "+")))) });
            double exact = 48000 - Math.PI * 64 * 20;
            var v = SolidValidator.Validate(b);
            Log(new { test = "K10-FOREIGN", faces = b.Faces.Count(), vm, va, exact, verdict = v.Summary(), conv = b.TopologyConvention, norm = nr.ToString(),
                      issues = v.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4).Select(i => i.ToString()), warnings = w.Take(6) });
            Require(v.IsValid, v.ToString());
            Require(va != null && Math.Abs(va.Value - exact) < 1e-6 * exact, $"analytic volume {va} vs {exact}");
            Require(Math.Abs(vm - exact) < 1e-3 * exact, $"mesh volume {vm} vs {exact}");
        });
    }
}
static partial class Program
{
    static void ForeignNurbs()
    {
        Check("K10-STEP-FOREIGN-RATIONAL-NURBS", () =>
        {
            var w = new List<string>();
            var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(File.ReadAllText(Fixture("quarter_cyl.step")), w);
            Require(back.Count == 1, $"bodies {back.Count}: {string.Join(" | ", w)}");
            var b = back[0];
            double exact = Math.PI * 100 * 20 / 4;
            double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
            var v = SolidValidator.Validate(b);
            // On-surface check of the NURBS face: points of the face mesh lie on the cylinder R=10.
            double worst = 0;
            foreach (var f in b.Faces)
                if (GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface ns)
                    for (double u = 0; u <= 1.0001; u += 0.05)
                        for (double vv = 0; vv <= 1.0001; vv += 0.25)
                        {
                            var q = ns.PointAt(new Vector2d(u, vv));
                            worst = Math.Max(worst, Math.Abs(Math.Sqrt(q.X * q.X + q.Y * q.Y) - 10));
                        }
            Log(new { test = "K10-NURBS", faces = b.Faces.Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name), vm, exact, rel = Math.Abs(vm - exact) / exact,
                      nurbsOffCylinder = worst, verdict = v.Summary(), warnings = w.Take(5) });
            Require(worst < 1e-9, $"rational weights not applied: surface off the cylinder by {worst}");
            Require(v.IsValid, v.ToString());
            Require(Math.Abs(vm - exact) < 2e-3 * exact, $"volume {vm} vs {exact}");
            TopologyConventions.Normalize(b);
            var mp = AnalyticFlux.MassProperties(b);
            Log(new { test = "K10-NURBS-ANALYTIC", volume = mp?.Volume, exact, err = mp == null ? -1 : Math.Abs(mp.Value.Volume - exact), com = mp?.Centroid.ToString(), comExact = 4 * 10 / (3 * Math.PI) });
            Require(mp != null && Math.Abs(mp.Value.Volume - exact) < 1e-6 * exact, $"analytic volume of the NURBS body {mp?.Volume} vs {exact}");
            Require(Math.Abs(mp!.Value.Centroid.X - 40 / (3 * Math.PI)) < 1e-5 && Math.Abs(mp.Value.Centroid.Z - 10) < 1e-6, $"centroid {mp.Value.Centroid}");
        });
    }
}
static partial class Program
{
    static void K10Storage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p3d-k10-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Part Cube()
        {
            var p = new Part("k10"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
            var rf = new Parametric3D.Model.Features.RoundFeature { Radius = new Parametric3D.Model.Features.ValueRef(4) };
            rf.Edges.Add(EdgeRef.Of(CornerEdge(p, 30, 30))); p.AddFeature(rf); p.Rebuild(null);
            return p;
        }
        Check("K10-SAVE-LOAD-ATOMIC", () =>
        {
            var p = Cube(); string path = Path.Combine(dir, "part.p3d");
            DocumentStorage.Save(path, p);
            var (q, pars) = DocumentStorage.Load(path); q.Rebuild(pars);
            Require(Math.Abs(q.Volume - p.Volume) < 1e-6 && q.Features.All(f => f.Error == null), $"reloaded {q.Volume} vs {p.Volume}");
            Require(File.ReadAllText(path).Contains("\"Version\": 2"), "format version 2 expected");
            // Second save keeps the previous file as .bak; no temporary files remain.
            DocumentStorage.Save(path, p);
            Require(File.Exists(path + ".bak"), "no backup of the previous file");
            Require(!Directory.GetFiles(dir, "*.tmp").Any(), "temporary file left behind");
        });
        Check("K10-SAVE-FAILURE-LEAVES-OLD-FILE", () =>
        {
            var p = Cube(); string path = Path.Combine(dir, "keep.p3d");
            DocumentStorage.Save(path, p);
            string before = File.ReadAllText(path);
            p.AddFeature(new CutCyl(0, 0, 5));     // a feature the document format cannot store
            bool threw = false;
            try { DocumentStorage.Save(path, p); } catch (InvalidOperationException) { threw = true; }
            Require(threw, "saving an unsaveable feature must fail, not drop it");
            Require(File.ReadAllText(path) == before, "the previous file was changed by a failed save");
            bool threw2 = false;
            try { DocumentStorage.WriteAtomic(Path.Combine(dir, "no-such-dir", "x.p3d"), "{}"); } catch (Exception) { threw2 = true; }
            Require(threw2 && !Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories).Any(), "failed write left a temporary file");
        });
        Check("K10-LOAD-REFUSES-DAMAGED", () =>
        {
            var p = Cube(); string json = DocumentFile.Save(p);
            void MustFail(string what, string text)
            {
                bool threw = false;
                try { DocumentFile.Load(text); } catch (InvalidDataException) { threw = true; }
                Require(threw, $"{what}: damaged document accepted");
            }
            MustFail("truncated", json[..(json.Length / 2)]);
            MustFail("newer version", json.Replace("\"Version\": 2", "\"Version\": 99"));
            MustFail("unknown feature", json.Replace("\"Kind\": \"round\"", "\"Kind\": \"teleport\""));
            MustFail("line without ends", System.Text.RegularExpressions.Regex.Replace(json, "\"Kind\": \"point\"", "\"Kind\": \"pointx\""));
            var (_, _) = DocumentFile.LoadLenient(json.Replace("\"Kind\": \"round\"", "\"Kind\": \"teleport\""), out var lost);
            Require(lost.Count == 1 && lost[0].Contains("teleport"), "lenient load must report the loss: " + string.Join(";", lost));
            // A version-1 document (before K05 keys) still loads.
            var (v1, v1p) = DocumentFile.Load(json.Replace("\"Version\": 2", "\"Version\": 1"));
            v1.Rebuild(v1p);
            Require(v1.Features.All(f => f.Error == null), "version-1 document no longer loads");
        });
        try { Directory.Delete(dir, true); } catch { }
    }
}
static partial class Program
{
    static void K10Reproducer()
    {
        Check("K10-REPRODUCER-REPLAYS-FAILURE", () =>
        {
            using var __poly = PolyhedralPath();   // отказ внедряется на этапе многогранного пути
            var w = new List<string>();
            var bodies = new List<TSolid> { Box() };
            var tool = PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24);
            OperationResult failed;
            using (FaultInjection.Arm("boolean.exact")) failed = BooleanOp.Evaluate(bodies, tool, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            Require(!failed.Accepted, "injected fault did not fail the operation");
            string rep = Parametric3D.Kernel.Diagnostics.Reproducer.Capture(bodies, tool, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, failed, "boolean.exact");
            var (again, same) = Parametric3D.Kernel.Diagnostics.Reproducer.Replay(rep);
            Log(new { test = "K10-REPRO", size = rep.Length, failed.FailureCode, failed.FailedStage, againCode = again.FailureCode, same });
            Require(same, $"replay gave {again.FailureCode}@{again.FailedStage}, recorded {failed.FailureCode}@{failed.FailedStage}");
            Require(!rep.Contains("k10") && !rep.Contains("Extrude"), "reproducer must not carry document data");
        });
        Check("K10-REPRODUCER-REPLAYS-SUCCESS", () =>
        {
            var bodies = new List<TSolid> { Box() };
            var tool = PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24);
            var ok = BooleanOp.Evaluate(bodies, tool, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            string rep = Parametric3D.Kernel.Diagnostics.Reproducer.Capture(bodies, tool, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, ok);
            var (again, same) = Parametric3D.Kernel.Diagnostics.Reproducer.Replay(rep);
            Require(same && again.Bodies.Count == 1, "successful operation not replayed");
            double v0 = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(ok.Bodies[0]));
            double v1 = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(again.Bodies[0]));
            Log(new { test = "K10-REPRO-OK", v0, v1 });
            Require(Math.Abs(v0 - v1) < 1e-3 * v0, $"replayed result {v1} vs {v0}");
        });
    }
}
static partial class Program
{
    static void K09Inertia()
    {
        Check("K09-ANALYTIC-INERTIA", () =>
        {
            var rows = new List<string>();
            void Cmp(string name, TSolid b, double vExp, Vector3d cgExp, Vector3d diagExp, double offExp = 0)
            {
                if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
                var r = AnalyticFlux.InertiaAtCentroid(b);
                if (r == null) { rows.Add($"FAIL {name}: not available"); return; }
                var (V, cg, J) = r.Value;
                double eD = Math.Max(Math.Abs(J.M11 - diagExp.X), Math.Max(Math.Abs(J.M22 - diagExp.Y), Math.Abs(J.M33 - diagExp.Z))) / Math.Max(diagExp.X, Math.Max(diagExp.Y, diagExp.Z));
                double eO = Math.Max(Math.Abs(J.M12 - offExp), Math.Max(Math.Abs(J.M13), Math.Abs(J.M23))) / Math.Max(diagExp.X, 1);
                bool ok = Math.Abs(V - vExp) < 1e-7 * vExp && (cg - cgExp).Length < 1e-6 && eD < 1e-6 && eO < 1e-6;
                Log(new { inertia = name, V, cg = cg.ToString(), J = $"{J.M11:0.###} {J.M22:0.###} {J.M33:0.###} | {J.M12:0.###} {J.M13:0.###} {J.M23:0.###}", expect = diagExp.ToString(), eD, eO });
                rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: diag rel {eD:E1}, off {eO:E1}");
            }
            var box = Box(); TopologyConventions.Normalize(box);
            double m = 48000;
            Cmp("box 60×40×20", box, m, new Vector3d(30, 20, 10), new Vector3d(m * (1600 + 400) / 12, m * (3600 + 400) / 12, m * (3600 + 1600) / 12));
            var cyl = PrimitiveOp.Cylinder(new Vector3d(1, 2, 3), 5, 20);
            double mc = Math.PI * 25 * 20;
            Cmp("cylinder r5 h20", cyl, mc, new Vector3d(1, 2, 13), new Vector3d(mc * (3 * 25 + 400) / 12, mc * (3 * 25 + 400) / 12, mc * 25 / 2));
            // Box with a centred through hole R8: subtract the cylinder's moments.
            var w = new List<string>(); var hb = Scenario("S-HOLE", w)[0];
            double mh = Math.PI * 64 * 20;
            Cmp("box with a hole R8", hb, m - mh, new Vector3d(30, 20, 10),
                new Vector3d(m * 2000 / 12 - mh * (3 * 64 + 400) / 12, m * 4000 / 12 - mh * (3 * 64 + 400) / 12, m * 5200 / 12 - mh * 64 / 2));
            {
                // Part with two separate bodies (linear pattern of boxes): parallel-axis transfer.
                var p = new Part("two"); var s = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (0, 0), (10, 0), (10, 10), (0, 10));
                var ex = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(10); p.AddFeature(ex);
                p.AddFeature(new Parametric3D.Model.Features.LinearPatternFeature { Step = new Vector3d(30, 0, 0), Count = new Parametric3D.Model.Features.ValueRef(2) }); p.Rebuild(null);
                var r = p.InertiaAtCentroid();
                // Two cubes 10 at x-centres 5 and 35: centroid x = 20; Iyy = 2·(1000·200/12 + 1000·15²).
                double iyy = 2 * (1000 * 200 / 12.0 + 1000 * 225), ixx = 2 * 1000 * 200 / 12.0;
                bool ok = r != null && Math.Abs(r.Value.Centroid.X - 20) < 1e-6 && Math.Abs(r.Value.Inertia.M22 - iyy) < 1e-6 * iyy && Math.Abs(r.Value.Inertia.M11 - ixx) < 1e-6 * ixx;
                rows.Add($"{(ok ? "OK  " : "FAIL")} part of two bodies: Iyy {r?.Inertia.M22:0.###} vs {iyy:0.###}");
            }
            {
                // Principal axes of a box rotated by 30° about z: moments as for the axis-aligned box, axes rotated.
                var b = Parametric3D.Kernel.Ops.TransformOp.Copy(Box(), Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitZ, 30));
                TopologyConventions.Normalize(b);
                var r = AnalyticFlux.InertiaAtCentroid(b)!.Value;
                var (mom, axes) = AnalyticFlux.PrincipalAxes(r.Inertia);
                double mm = 48000; var want = new[] { mm * 2000 / 12, mm * 4000 / 12, mm * 5200 / 12 };
                double ang = Math.Atan2(axes[0].Y, axes[0].X) * 180 / Math.PI;   // smallest moment ↔ long side (x before rotation)
                bool ok = Math.Abs(mom.X - want[0]) < 1e-6 * want[2] && Math.Abs(mom.Y - want[1]) < 1e-6 * want[2] && Math.Abs(mom.Z - want[2]) < 1e-6 * want[2]
                          && (Math.Abs(((ang % 180) + 180) % 180 - 30) < 1e-6);
                rows.Add($"{(ok ? "OK  " : "FAIL")} principal axes of a rotated box: moments {mom}, first axis at {ang:0.######}°");
            }
            Console.WriteLine(string.Join("\n", rows));
            Require(rows.All(x => x.StartsWith("OK")), string.Join("; ", rows.Where(x => !x.StartsWith("OK"))));
        });
    }
}
static partial class Program
{
    static void ScaleProbe()
    {
        foreach (int n in new[] { 40, 80 })
        {
            var p = RealisticPart(9001, n, out var recipe);
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
            long a0 = GC.GetAllocatedBytesForCurrentThread(); var sw = Stopwatch.StartNew();
            p.Invalidate(); p.Rebuild(null);
            double ms = sw.Elapsed.TotalMilliseconds; double mb = (GC.GetAllocatedBytesForCurrentThread() - a0) / 1048576.0;
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = false;
            var last = p.Features.OfType<Parametric3D.Model.Features.HoleFeature>().Last();
            last.Diameter = new Parametric3D.Model.Features.ValueRef(last.Diameter.Value * 0.9); p.MarkDirtyFrom(last);
            sw.Restart(); p.Rebuild(null); double editMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"SCALE features={p.Features.Count} faces={p.Bodies[0].Faces.Count()} full={ms:0} ms alloc={mb:0} MB editLastHole={editMs:0} ms errors={p.Features.Count(f => f.Error != null)} valid={SolidValidator.Validate(p.Bodies[0]).Summary()}");
            Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(12)));
        }
    }
}
static partial class Program
{
    static void IgesRoundTrip()
    {
        var rows = new List<string>();
        foreach (var id in new[] { "S-BOX", "S-HOLE", "S-HOLES4", "S-UNION", "S-ROUND", "S-SPHERE", "S-ROUNDHOLE" })
        {
            var w = new List<string>(); var b = Scenario(id, w)[0];
            if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
            double v0 = AnalyticVolume.Of(b) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
            string text; TSolid? back;
            try { text = Parametric3D.Kernel.Export.IgesSolid.Write(b, w); back = Parametric3D.Kernel.Export.IgesImport.Read(text, w); }
            catch (Exception ex) { rows.Add($"FAIL {id}: exception {ex.Message}"); continue; }
            if (back == null) { rows.Add($"FAIL {id}: not read back ({string.Join(" | ", w.Where(x => x.Contains("IGES")).Take(2))})"); continue; }
            double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(back));
            var verdict = SolidValidator.Validate(back).Summary();
            if (id == "S-SPHERE")
                foreach (var f in back.Faces.Where(f => f.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface))
                {
                    var vs = f.Loops.SelectMany(l => l.CoEdges).SelectMany(ce => new[] { ce.Edge.V0, ce.Edge.V1 }).ToList();
                    Console.WriteLine($"DBGV face {f.Id}: vertex objects {vs.Distinct().Count()}, distinct positions {vs.Select(v => v.P.ToString()).Distinct().Count()}, edges {f.Edges.Count()}, closed edges {f.Edges.Count(e => e.V0 == e.V1)}");
                    foreach (var l in f.Loops)
                        Console.WriteLine($"DBGL face {f.Id} {f.Surface.GetType().Name} loop: " + string.Join(" -> ", l.CoEdges.Select(ce => $"[{ce.Edge.Curve.GetType().Name} {(ce.Reversed ? ce.Edge.V1.P : ce.Edge.V0.P):0.##}→{(ce.Reversed ? ce.Edge.V0.P : ce.Edge.V1.P):0.##}]")));
                }
            if (id == "S-BOX" || id == "S-HOLE") foreach (var i in SolidValidator.Validate(back).Issues.Where(x => x.Severity == ValidationSeverity.Error).GroupBy(x => x.Code).Select(g => g.First()).Take(4)) Console.WriteLine($"  {id} {i}");
            bool ok = Math.Abs(vm - v0) < 2e-3 * v0 && !verdict.Contains("error");
            rows.Add($"{(ok ? "OK  " : "FAIL")} {id}: {vm:0.###} vs {v0:0.###} (rel {Math.Abs(vm - v0) / v0:E1}) {verdict} | {string.Join(" | ", w.Where(x => x.Contains("IGES")).Take(6))}");
        }
        foreach (var seed in new[] { 8001, 8002, 8003, 8004 })
        {
            var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
            var b = p.Bodies[0]; var w = new List<string>();
            double v0 = AnalyticVolume.Of(b) ?? 0;
            var back = Parametric3D.Kernel.Export.IgesImport.Read(Parametric3D.Kernel.Export.IgesSolid.Write(b, w), w);
            if (back == null) { rows.Add($"FAIL REAL-{seed}: not read back"); continue; }
            double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(back));
            var verdict = SolidValidator.Validate(back).Summary();
            var nrep = TopologyConventions.Normalize(back);
            double? va = AnalyticVolume.Of(back);
            Console.WriteLine("IGESNORM " + nrep + " | " + string.Join(" ; ", nrep.Problems.Take(3)));
            Console.WriteLine($"IGESANALYTIC REAL-{seed}: analytic {va} vs {v0} diff {(va == null ? double.NaN : va.Value - v0)} conv {back.TopologyConvention}");
            bool ok = Math.Abs(vm - v0) < 2e-3 * v0 && !verdict.Contains("error") && va != null && Math.Abs(va.Value - v0) < 1e-8 * v0;
            rows.Add($"{(ok ? "OK  " : "FAIL")} REAL-{seed}: {vm:0.###} vs {v0:0.###} (rel {Math.Abs(vm - v0) / v0:E1}) {verdict}");
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K10-IGES-ROUNDTRIP", () => Require(rows.All(x => x.StartsWith("OK")), string.Join("; ", rows.Where(x => !x.StartsWith("OK")).Select(x => x.Length > 160 ? x[..160] : x))));
    }
}
static partial class Program
{
    /// <summary>
    /// m.x2: ФАЙЛЫ OPENCASCADE (tests/stepfiles/occt_iges, написаны tools/occt_iges_write.py в режиме граней 144): шар, клин
    /// шара, пояс шара, конус, тор и его часть, цилиндр по наклонной оси, вычитания, скругление всех рёбер, вращение и
    /// выдавливание. Наш импорт — объём как у OpenCascade (1e-6). OpenCascade ставит матрицу 124 у самой поверхности 120
    /// (и зеркальную), начальный угол не 0, ось — от начала к концу.
    /// </summary>
    static void OcctIgesFiles()
    {
        Check("K10-IGES-OCCT-FILES", () =>
        {
            var dir = Path.GetDirectoryName(Fixture("occt_iges/manifest.tsv"))!;
            var bad = new List<string>(); int n = 0;
            foreach (var row in File.ReadAllLines(Path.Combine(dir, "manifest.tsv")).Skip(1))
            {
                var c = row.Split('\t'); n++; double V = double.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture);
                double back = double.NaN;
                try { var b = Parametric3D.Kernel.Export.IgesImport.Read(File.ReadAllText(Path.Combine(dir, c[0] + ".igs")), new List<string>()); if (b != null) back = OpVolume(b); }
                catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { }
                if (!(Math.Abs(back - V) <= 1e-6 * Math.Max(1, V))) bad.Add($"{c[0]} {c[1]}: {back:G9} vs {V:G9}");
            }
            Console.WriteLine($"IGES OpenCascade files: {n - bad.Count}/{n}");
            Require(n >= 17 && bad.Count == 0, string.Join("; ", bad));
        });
    }

    static void ForeignIges()
    {
        Check("K10-IGES-FOREIGN-BOX-HOLE", () =>
        {
            var w = new List<string>();
            var b = Parametric3D.Kernel.Export.IgesImport.Read(File.ReadAllText(Fixture("box_hole.igs")), w);
            Require(b != null, "not read: " + string.Join(" | ", w));
            double exact = 48000 - Math.PI * 64 * 20;
            double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b!));
            var v = SolidValidator.Validate(b!);
            TopologyConventions.Normalize(b!);
            var va = AnalyticVolume.Of(b!);
            Log(new { test = "K10-IGES-FOREIGN", faces = b!.Faces.Count(), vm, va, exact, verdict = v.Summary(), warnings = w.Take(4) });
            Require(v.IsValid, v.ToString());
            Require(va != null && Math.Abs(va.Value - exact) < 1e-8 * exact, $"analytic volume {va} vs {exact}");
        });
    }
}
static partial class Program
{
    /// <summary>
    /// K10. ДЛИТЕЛЬНЫЙ ПРОГОН: сотня правок подряд на одной детали. Проверяются рост памяти после
    /// сборки мусора, рост времени правки и совпадение результата с чистым построением.
    /// </summary>
    static void SoakTest()
    {
        int seed = Enumerable.Range(8001, 20).First(sd => RealisticPart(sd, 20, out _).Features.OfType<Parametric3D.Model.Features.HoleFeature>().Count() >= 3);
        var p = RealisticPart(seed, 20, out var recipe0); p.Rebuild(null);
        var holes = p.Features.OfType<Parametric3D.Model.Features.HoleFeature>().ToList();
        Console.WriteLine($"SOAKSTART holes {holes.Count} features {p.Features.Count} recipe {string.Join(",", recipe0)}");
        var rnd = new Random(77);
        var mem = new List<double>(); var times = new List<double>();
        var baseD = holes.Select(h => h.Diameter.Value).ToList();
        int cycles = 100;
        for (int i = 0; i < cycles; i++)
        {
            int k = rnd.Next(holes.Count);
            holes[k].Diameter = new Parametric3D.Model.Features.ValueRef(baseD[k] * (0.8 + 0.2 * rnd.NextDouble()));
            var sw = Stopwatch.StartNew();
            p.MarkDirtyFrom(holes[k]); p.Rebuild(null);
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (i % 10 == 9)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                mem.Add(GC.GetTotalMemory(true) / 1048576.0);
            }
        }
        // Fresh build with the final parameters must give the same geometry.
        var q = RealisticPart(seed, 20, out var recipeQ);
        var qh = q.Features.OfType<Parametric3D.Model.Features.HoleFeature>().ToList();
        var p0 = RealisticPart(seed, 20, out var recipeP0);
        Console.WriteLine($"DETERMINISM holes p={holes.Count} q={qh.Count} p0={p0.Features.OfType<Parametric3D.Model.Features.HoleFeature>().Count()} recipeQ={string.Join(",", recipeQ)} recipeP0={string.Join(",", recipeP0)}");
        Console.WriteLine($"DETERMINISM features p={string.Join(",", p.Features.Select(f => f.Name))}");
        if (qh.Count != holes.Count) { Check("K10-SOAK-100-EDITS", () => Require(false, $"the same tree generated twice differs: {holes.Count} vs {qh.Count} holes")); return; }
        for (int i = 0; i < holes.Count; i++) qh[i].Diameter = new Parametric3D.Model.Features.ValueRef(holes[i].Diameter.Value);
        q.Invalidate(); q.Rebuild(null);    // a direct field change needs an explicit invalidation
        bool same = PartGeometry(p) == PartGeometry(q);
        Console.WriteLine($"SOAKDIFF vol p={p.Volume:R} q={q.Volume:R} analytic p={AnalyticVolume.Of(p.Bodies[0]):R} q={AnalyticVolume.Of(q.Bodies[0]):R} faces {p.Bodies[0].Faces.Count()}/{q.Bodies[0].Faces.Count()} geo {SolidFingerprint.Geometry(p.Bodies[0]) == SolidFingerprint.Geometry(q.Bodies[0])}");
        // Also: the same edits replayed incrementally from scratch on a fresh copy.
        double first10 = times.Take(10).Average(), last10 = times.Skip(cycles - 10).Average();
        Log(new { soak = cycles, memMB = mem.Select(x => Math.Round(x, 1)), first10, last10, same, errors = p.Features.Count(f => f.Error != null) });
        Check("K10-SOAK-100-EDITS", () =>
        {
            Require(same, "geometry after 100 edits differs from a fresh build");
            Require(p.Features.All(f => f.Error == null), "feature errors appeared");
            Require(mem[^1] < mem[0] * 1.5 + 20, $"managed memory grows: {string.Join(", ", mem.Select(x => x.ToString("0")))} MB");
            Require(last10 < first10 * 2 + 50, $"edit time grows: {first10:0} → {last10:0} ms");
        });
    }
}
static partial class Program
{
    static void GenDeterminism()
    {
        for (int t = 0; t < 3; t++)
        {
            var p = RealisticPart(8003, 20, out var r);
            Console.WriteLine($"GEN {t}: {string.Join(",", r)} | vol {AnalyticVolume.Of(p.Bodies[0]):0.######}");
        }
    }
}
static partial class Program
{
    static void SoakSameEdit()
    {
        int seed = Enumerable.Range(8001, 20).First(sd => RealisticPart(sd, 20, out _).Features.OfType<Parametric3D.Model.Features.HoleFeature>().Count() >= 3);
        var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
        var h = p.Features.OfType<Parametric3D.Model.Features.HoleFeature>().First();
        double d0 = h.Diameter.Value; var times = new List<double>();
        for (int i = 0; i < 80; i++)
        {
            h.Diameter = new Parametric3D.Model.Features.ValueRef(d0 * (i % 2 == 0 ? 0.9 : 1.0));
            var sw = Stopwatch.StartNew(); p.MarkDirtyFrom(h); p.Rebuild(null); times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"SAMEEDIT first10 {times.Skip(2).Take(10).Average():0} mid {times.Skip(35).Take(10).Average():0} last10 {times.Skip(70).Average():0} ms");
    }
}
static partial class Program
{
    static void K02Contract()
    {
        Check("K02-CONTRACT-NON-BOOLEAN-OPS", () =>
        {
            var start = new List<TSolid> { Box() };
            var before = start.ToList();
            // 1. The operation throws: Failed at the build stage, caller's list untouched.
            var r1 = OperationContract.Run("Round", start, b => throw new Parametric3D.Core.RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "cannot round"));
            Require(!r1.Accepted && r1.FailedStage == "round.build" && start.SequenceEqual(before), "exception not contained: " + r1.Describe());
            // 2. The operation returns an invalid body (a box missing a face): Failed at validation.
            var r2 = OperationContract.Run("Shell", start, b => { var broken = Box(); var f = broken.Faces.First(); broken.Shells[0].Faces.Remove(f);
                foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) ce.Edge.CoEdges.Remove(ce); b[0] = broken; });
            Require(!r2.Accepted && r2.FailureCode == OperationCodes.InvalidResult && start.SequenceEqual(before), "invalid result accepted: " + r2.Describe());
            // 3. A good operation: Succeeded, bodies replaced.
            var r3 = OperationContract.Run("Shell", start, b => { b[0] = Box(0, 0, 0, 10, 10, 10); });
            Require(r3.Accepted && r3.Bodies.Count == 1 && start.SequenceEqual(before), "good result: " + r3.Describe());
            // 4. Through the tree: a shell that cannot be made leaves the earlier body as it was.
            var p = new Part("contract"); TestGeometry.AddCube(p, 10);
            var sh = new Parametric3D.Model.Features.ShellFeature { Thickness = new Parametric3D.Model.Features.ValueRef(6) }; sh.OpenFaces.Add(new Vector3d(0, 0, 5));
            p.AddFeature(sh); p.Rebuild(null);
            Require(sh.Error != null && Math.Abs(p.Volume - 1000) < 1e-9, $"failed shell changed the body: {p.Volume} {sh.Error}");
            // 4b. Per-feature policy is executed and persisted.
            {
                var pp = new Part("pol"); TestGeometry.AddCube(pp, 20);
                var cut = new CutCyl(0, 0, 3) { Policy = OperationPolicy.AllowApproximate };
                pp.AddFeature(cut);
                var seen = new List<OperationPolicy>();
                pp.AddFeature(new PolicyProbe(seen));
                pp.Rebuild(null);
                Require(seen.Count == 1 && seen[0] == OperationPolicy.Strict, "a feature without its own policy must run under the part's (strict)");
                var probe2 = new PolicyProbe(seen) { Policy = OperationPolicy.AllowApproximate }; pp.AddFeature(probe2); pp.Rebuild(null);
                Require(seen.Last() == OperationPolicy.AllowApproximate, "the feature's own policy was not applied");
                var p2 = new Part("pol2"); TestGeometry.AddCube(p2, 20);
                var rf = new Parametric3D.Model.Features.RoundFeature { Radius = new Parametric3D.Model.Features.ValueRef(2), Policy = OperationPolicy.AllowApproximate };
                p2.Rebuild(null); rf.Edges.Add(EdgeRef.Of(CornerEdge(p2, 10, 10))); p2.AddFeature(rf); p2.Rebuild(null);
                var (back, _) = DocumentFile.Load(DocumentFile.Save(p2));
                Require(back.Features.OfType<Parametric3D.Model.Features.RoundFeature>().Single().Policy == OperationPolicy.AllowApproximate, "feature policy not persisted");
            }
            // 5. The guarantee level reaches the exchange file.
            var approx = Box(); approx.Guarantee = GuaranteeLevel.Approximate;
            var we = new List<string>(); var sw2 = new StringWriter();
            Parametric3D.Kernel.Export.StepExact.Write(sw2, new[] { approx }, "a", we);
            Parametric3D.Kernel.Export.IgesSolid.Write(new[] { approx }, we);
            Require(sw2.ToString().Contains("approximate brep") && we.Count(x => x.Contains("approximate body")) == 2, "approximate body exported silently");
        });
    }
}

sealed class PolicyProbe : Parametric3D.Model.Features.Feature
{
    private readonly List<OperationPolicy> _seen;
    public PolicyProbe(List<OperationPolicy> seen) { _seen = seen; Name = "probe"; }
    public override void Execute(Parametric3D.Model.Features.BuildContext c) => _seen.Add(OperationPolicyScope.Current);
}
static partial class Program
{
    static void K05FaceRefs()
    {
        Check("K05-FACE-REFS-SHELL-DRAFT", () =>
        {
            // Cube 60 with a height parameter; shell open at the top; draft on a side.
            var t = new Parametric3D.Model.Params.ParameterTable(); t.Add("H", 60);
            var p = new Part("frefs");
            var ex = TestGeometry.AddCube(p, 60); ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60) { Formula = "H" };
            var sh = new Parametric3D.Model.Features.ShellFeature { Thickness = new Parametric3D.Model.Features.ValueRef(2) };
            sh.OpenFaces.Add(new Vector3d(0, 0, 30));                 // a point on the top face at H = 60
            p.AddFeature(sh); p.Rebuild(t);
            Require(sh.Error == null && sh.OpenFaceNames.Count == 1 && sh.OpenFaceNames[0] != null, "top face name not captured: " + sh.Error);
            double v60 = p.Volume;
            // Taller cube: the stored point (z = 30) is now inside the side faces' span, not on the top.
            t.Find("H")!.Number = 80; t.Recalculate(); p.ParametersChanged();
            var rr = p.Compute(t); p.Apply(rr);
            Console.WriteLine("FREFWARN started " + rr.StartedAt + " executed " + rr.Executed + " | " + string.Join(" | ", rr.Warnings.Where(w => w.StartsWith("Shell"))));
            double expect80 = 60 * 60 * 80 - 56.0 * 56 * 78;
            {
                var baseBody = Checkpoints(p).OrderBy(c => c.After).First().Bodies[0];
                var names = sh.OpenFaceNames.ToList();
                var pts = Parametric3D.Model.Features.FaceRefs.Resolve(baseBody, sh.OpenFaces, names, "dbg");
                Console.WriteLine($"FREFDBG resolved {pts[0]} tags {string.Join(",", baseBody.Faces.Select(f => f.Tag))} zmax {baseBody.Vertices.Max(v => v.P.Z)}");
            }
            Log(new { test = "K05-FACEREF", v60, v80 = p.Volume, expect80, err = sh.Error, name = sh.OpenFaceNames[0] });
            Require(sh.Error == null && Math.Abs(p.Volume - expect80) < 1e-6 * expect80, $"after resize the shell opened the wrong face: {p.Volume} vs {expect80} {sh.Error}");
            // Persisted and still resolved after reload.
            var (q, tq) = DocumentFile.Load(DocumentFile.Save(p, t)); q.Rebuild(tq);
            Require(Math.Abs(q.Volume - expect80) < 1e-6 * expect80, $"reloaded {q.Volume}");
            // The referenced face disappears: explicit conflict, not a silent substitute.
            var p2 = new Part("frefs2"); TestGeometry.AddCube(p2, 60);
            var sh2 = new Parametric3D.Model.Features.ShellFeature { Thickness = new Parametric3D.Model.Features.ValueRef(2) };
            sh2.OpenFaces.Add(new Vector3d(0, 0, 30)); sh2.OpenFaceNames.Add("#nosuch/extrude[0]:top");
            p2.AddFeature(sh2); p2.Rebuild(null);
            Require(sh2.Error != null && sh2.Error.Contains("no longer exists"), "missing face not reported: " + sh2.Error);
        });
    }
}
static partial class Program
{
    static void K05SplitFaces()
    {
        Check("K05-MULTI-REGION-FACE-SPLIT", () =>
        {
            var w = new List<string>();
            var b = new List<TSolid> { Box() };
            BooleanOp.Subtract(b, Box(25, -5, 10, 10, 50, 20), w);
            var s = b[0];
            var tops = s.Faces.Where(f => f.Tag.EndsWith(":top")).ToList();
            Require(tops.Count == 2 && tops.All(f => f.Loops.Count(l => l.IsOuter) == 1), $"top split: {tops.Count} faces");
            Require(SolidValidator.Validate(s).IsValid, "invalid after split");
            double? va = AnalyticVolume.Of(s);
            Require(va != null && Math.Abs(va.Value - (48000 - 10 * 40 * 10)) < 1e-6, $"volume {va}");
            var sw = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, "slot", w);
            int outerBounds = sw.ToString().Split('\n').Count(l => l.Contains("FACE_OUTER_BOUND"));
            Require(outerBounds == s.Faces.Count(), $"each face must have exactly one outer bound: {outerBounds} vs {s.Faces.Count()} faces");
        });
    }
}
static partial class Program
{
    static void K05EdgeHistory()
    {
        Check("K05-EDGE-HISTORY", () =>
        {
            var p = new Part("eh"); TestGeometry.AddCube(p, 60);
            var hole = p.AddFeature(new CutCyl(0, 0, 5));
            var r = p.Compute(null); p.Apply(r);
            var h = r.History[hole];
            Log(new { test = "K05-EDGE-HISTORY", h.EdgesGenerated, h.EdgesModified, h.EdgesDeleted });
            // A through hole adds two rims (wall|top, wall|bottom) and changes no cube edge.
            Require(h.EdgesGenerated.Count == 2 && h.EdgesGenerated.All(n => n.Contains($"#{hole.Key}/")), "rims not reported as generated edges");
            Require(h.EdgesModified.Count == 0 && h.EdgesDeleted.Count == 0, "cube edges must stay unchanged");
            // A round on a vertical edge: that edge is deleted, two contact edges are generated.
            p.Rebuild(null);
            var rf = new Parametric3D.Model.Features.RoundFeature { Radius = new Parametric3D.Model.Features.ValueRef(4) };
            rf.Edges.Add(EdgeRef.Of(CornerEdge(p, 30, 30))); p.AddFeature(rf);
            var r2 = p.Compute(null); p.Apply(r2);
            var h2 = r2.History[rf];
            Log(new { test = "K05-EDGE-HISTORY-ROUND", h2.EdgesGenerated, h2.EdgesModified, h2.EdgesDeleted });
            Require(h2.EdgesDeleted.Count == 1 && h2.EdgesGenerated.Count >= 2, $"round edge history: {h2}");
        });
    }
}
static partial class Program
{
    static void G1PCurves()
    {
        var rows = new List<string>();
        void Probe(string name, TSolid b, int minExact, double maxSampledDev)
        {
            if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
            var st = PCurves.Build(b);
            // Exact pcurves lie in the surface parameters exactly; their deviation is the distance of the
            // EDGE itself from the surface — the 1e-5 grid of the exact polyhedron for fitted arcs.
            bool ok = st.Skipped == 0 && st.Exact >= minExact && st.MaxDeviationExact < 2e-5 && st.MaxDeviationSampled <= 2e-5;
            Log(new { g1 = name, stats = st.ToString() });
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {st}");
        }
        var w = new List<string>();
        Probe("box", Box(), 24, 0);
        Probe("box with a hole", Scenario("S-HOLE", w)[0], 1, 0);
        Probe("4 holes", Scenario("S-HOLES4", w)[0], 1, 0);
        Probe("spherical cut", Scenario("S-SPHERE", w)[0], 1, 1e-6);
        Probe("rounded edge", Scenario("S-ROUND", w)[0], 1, 1e-6);
        Probe("hole with a rounded rim (torus)", Scenario("S-ROUNDHOLE", w)[0], 1, 1e-6);
        {
            var p = new Part("shaft"); var s = CircleSketch(p, 0, 0, 0, 10);
            var ex = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); p.AddFeature(ex);
            var h = new Parametric3D.Model.Features.HoleFeature("Cross") { Direction = -Vector3d.UnitY, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(4) };
            h.Points.Add(new Vector3d(0, 15, 30)); p.AddFeature(h); p.Rebuild(null);
            Probe("shaft with a cross hole (intersection curves)", p.Bodies[0], 1, 1e-4);
        }
        foreach (var seed in new[] { 8001, 8002 }) { var p = RealisticPart(seed, 20, out _); p.Rebuild(null); Probe($"realistic {seed}", p.Bodies[0], 20, 1e-4); }
        Console.WriteLine(string.Join("\n", rows));
        Check("G1-PCURVES", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void G2UvMesh()
    {
        var rows = new List<string>();
        void Probe(string name, TSolid b, double minCoverage)
        {
            if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
            int handled = 0, total = 0; double worstArea = 0; var reasons = new Dictionary<string, int>();
            var mb = new Parametric3D.Kernel.Tess.MeshBuilder();
            foreach (var f in b.Faces)
            {
                total++;
                var r = Parametric3D.Kernel.Tess.UvMesher.Mesh(f, 0.01, out var why);
                if (r == null)
                {
                    reasons[why ?? "?"] = reasons.GetValueOrDefault(why ?? "?") + 1;
                    // Fallback: the loop mesher (same edge points as the (u,v) mesher), else the face's own mesh.
                    var em = Parametric3D.Kernel.Tess.ExactFaceMesher.Mesh(f, out _, out _);
                    mb.BeginFace(f.Id);
                    if (em is { } emv) { int bi = mb.VertexCount; foreach (var q in emv.Item1) mb.AddVertex(q, Vector3d.UnitZ); for (int t = 0; t < emv.Item2.Count; t += 3) mb.AddTriangle(bi + emv.Item2[t], bi + emv.Item2[t + 1], bi + emv.Item2[t + 2]); }
                    else f.Surface.TessellateFace(f, mb);
                    mb.EndFace();
                    continue;
                }
                handled++;
                var (P, T) = r.Value;
                double area = 0; for (int t = 0; t < T.Count; t += 3) area += Vector3d.Cross(P[T[t + 1]] - P[T[t]], P[T[t + 2]] - P[T[t]]).Length / 2;
                double? exact = AnalyticVolume.FaceArea(f);
                if (name == "4 holes")
                {
                    double vU = 0; for (int t = 0; t < T.Count; t += 3) vU += Vector3d.Dot(P[T[t]], Vector3d.Cross(P[T[t + 1]], P[T[t + 2]])) / 6;
                    var mbo = new Parametric3D.Kernel.Tess.MeshBuilder(); mbo.BeginFace(f.Id); f.Surface.TessellateFace(f, mbo); mbo.EndFace(); var mdo = new Parametric3D.Kernel.Tess.MeshData(); mbo.FillInto(mdo);
                    double vO = 0; for (int t = 0; t + 2 < mdo.Indices.Length; t += 3) { var A = mbo.PositionsD[mdo.Indices[t]]; var B = mbo.PositionsD[mdo.Indices[t + 1]]; var C = mbo.PositionsD[mdo.Indices[t + 2]]; vO += Vector3d.Dot(A, Vector3d.Cross(B, C)) / 6; }
                    Console.WriteLine($"G2VOL {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count} uv {vU:0.###} old {vO:0.###} area {area:0.###} exact {exact:0.###}");
                }
                if (name == "box with a hole") Console.WriteLine($"G2DBG {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} mesh {area:0.###} exact {exact:0.###} tris {T.Count / 3} pts {P.Count}");
                if (exact is { } ex && Math.Abs(ex) > 1e-9) worstArea = Math.Max(worstArea, Math.Abs(area - Math.Abs(ex)) / Math.Abs(ex));
                if (exact is { } ex2 && Math.Abs(ex2) > 1e-9 && Math.Abs(area - Math.Abs(ex2)) / Math.Abs(ex2) > 1e-2)
                    Console.WriteLine($"G2BAD {name} face {f.Id} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count} outer {f.Loops.Count(l => l.IsOuter)} area {area:0.###} exact {ex2:0.###} seams {f.Loops.SelectMany(l => l.CoEdges).GroupBy(c => c.Edge).Count(g => g.Count() > 1)}");
                mb.BeginFace(f.Id);
                int baseIdx = mb.VertexCount;
                foreach (var q in P) mb.AddVertex(q, Vector3d.UnitZ);
                for (int t = 0; t < T.Count; t += 3) mb.AddTriangle(baseIdx + T[t], baseIdx + T[t + 1], baseIdx + T[t + 2]);
                mb.EndFace();
            }
            var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
            var mv = SolidValidator.ValidateMesh(md);
            foreach (var i in mv.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)) Console.WriteLine($"G2ISSUE {name}: {i}");
            if (!mv.IsValid && name.Contains("torus"))
            {
                var cnt = new Dictionary<((float, float, float), (float, float, float)), List<(uint F, int T)>>();
                for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var u = md.Vertices[md.Indices[t + k]].P; var v = md.Vertices[md.Indices[t + (k + 1) % 3]].P;
                        var ku = (u.X, u.Y, u.Z); var kv = (v.X, v.Y, v.Z); var key = ku.CompareTo(kv) < 0 ? (ku, kv) : (kv, ku);
                        if (!cnt.TryGetValue(key, out var l)) cnt[key] = l = new List<(uint, int)>();
                        l.Add((md.Vertices[md.Indices[t]].FaceId, t));
                    }
                foreach (var kv in cnt.Where(kv => kv.Value.Count > 2).Take(2))
                    Console.WriteLine($"G2NM {kv.Key}: " + string.Join(", ", kv.Value.Select(x => { var f = b.Faces.First(ff => ff.Id == x.F); return $"{x.F}:{GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}/{f.Loops.Count}"; })));
            }
            double vol = Parametric3D.Kernel.Tess.MassProps.Volume(md), va = AnalyticVolume.Of(b) ?? vol;
            double cov = (double)handled / total;
            bool ok = cov >= minCoverage && mv.IsValid && worstArea < 5e-3 && Math.Abs(vol - va) < 2e-3 * va;
            Log(new { g2 = name, handled, total, worstArea, vol, va, mesh = mv.Summary(), reasons });
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: faces {handled}/{total} in (u,v), worst area error {worstArea:E1}, volume {vol:0.###} vs {va:0.###}, {mv.Summary()} {string.Join(", ", reasons.Select(kv => kv.Key + "×" + kv.Value))}");
        }
        var w = new List<string>();
        Probe("box", Box(), 1);
        Parametric3D.Kernel.Tess.UvMesher.Debug = m => Console.WriteLine("UVDBG " + m);
        Probe("box with a hole", Scenario("S-HOLE", w)[0], 0.8);
        Parametric3D.Kernel.Tess.UvMesher.Debug = null;
        Probe("4 holes", Scenario("S-HOLES4", w)[0], 0.6);
        Parametric3D.Kernel.Tess.UvMesher.Debug = m => { if (m.StartsWith("consistency") || m.StartsWith("pass 0")) Console.WriteLine("RDBG " + m); };
        Probe("rounded edge", Scenario("S-ROUND", w)[0], 0.8);
        Parametric3D.Kernel.Tess.UvMesher.Debug = null;
        Parametric3D.Kernel.Tess.UvMesher.Debug = m => { if (m.StartsWith("final") || m.StartsWith("strips:")) Console.WriteLine("TDBG " + m); };
        Probe("hole with a rounded rim (torus)", Scenario("S-ROUNDHOLE", w)[0], 0.7);
        Parametric3D.Kernel.Tess.UvMesher.Debug = null;
        Probe("spherical cut (pole)", Scenario("S-SPHERE", w)[0], 0.8);
        {
            var p = new Part("shaft"); var s = CircleSketch(p, 0, 0, 0, 10);
            var ex = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); p.AddFeature(ex);
            var h = new Parametric3D.Model.Features.HoleFeature("Cross") { Direction = -Vector3d.UnitY, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(4) };
            h.Points.Add(new Vector3d(0, 15, 30)); p.AddFeature(h); p.Rebuild(null);
            Probe("shaft with a cross hole", p.Bodies[0], 0.5);
        }
        foreach (var seed in new[] { 8001, 8002 }) { var p = RealisticPart(seed, 20, out _); p.Rebuild(null); Probe($"realistic {seed}", p.Bodies[0], 0.7); }
        Console.WriteLine(string.Join("\n", rows));
        Check("G2-UV-MESH", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")).Select(r => r.Length > 220 ? r[..220] : r))));
    }
}
static partial class Program
{
    static void UvKernelProbe()
    {
        Parametric3D.Kernel.Tess.ExactFaceMesher.UseUvMesher = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var p = RealisticPart(8001, 20, out _); p.Invalidate(); sw.Restart(); p.Rebuild(null);
            Console.WriteLine($"UVK build {sw.ElapsedMilliseconds} ms; uv delaunay {Parametric3D.Kernel.Tess.UvMesher.PhaseMs[2]:0} ms");
            Console.WriteLine("UVK after build: " + SolidValidator.Validate(p.Bodies[0]).Summary());
            var holes = p.Features.OfType<Parametric3D.Model.Features.HoleFeature>().ToList();
            var lh = holes.Last(); lh.Diameter = new Parametric3D.Model.Features.ValueRef(lh.Diameter.Value * 0.9); p.MarkDirtyFrom(lh); p.Rebuild(null);
            Console.WriteLine("UVK after edit last: " + SolidValidator.Validate(p.Bodies[0]).Summary());
            var mh = holes[holes.Count / 2]; mh.Diameter = new Parametric3D.Model.Features.ValueRef(mh.Diameter.Value * 0.9); p.MarkDirtyFrom(mh); p.Rebuild(null);
            Console.WriteLine("UVK after edit mid: " + SolidValidator.Validate(p.Bodies[0]).Summary());
            var b = p.Bodies[0];
            var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            var mv = SolidValidator.ValidateMesh(md);
            Console.WriteLine("UVK " + mv.Summary());
            foreach (var i in mv.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(3))
            {
                Console.WriteLine("UVK issue " + i);
                if (i.At is { } at)
                {
                    var near = new HashSet<uint>();
                    for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                        for (int k = 0; k < 3; k++) { var q = md.Vertices[md.Indices[t + k]].P; if ((new Vector3d(q.X, q.Y, q.Z) - at).Length < 1.0) near.Add(md.Vertices[md.Indices[t]].FaceId); }
                    foreach (var fid in near)
                    {
                        var f = b.Faces.First(x => x.Id == fid);
                        var uv = Parametric3D.Kernel.Tess.UvMesher.Mesh(f, Parametric3D.Core.Tol.Chord, out var why);
                        Console.WriteLine($"   face {fid} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count} display {(f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Display?.Count} uv {(uv == null ? "no: " + why : "yes")}");
                        if (uv is { } um)
                        {
                            var cnt = new Dictionary<(Vector3d, Vector3d), int>();
                            for (int t = 0; t < um.T.Count; t += 3) for (int k = 0; k < 3; k++) { var a = um.P[um.T[t + k]]; var c = um.P[um.T[t + (k + 1) % 3]]; var key = string.CompareOrdinal(a.ToString("R"), c.ToString("R")) < 0 ? (a, c) : (c, a); cnt[key] = cnt.GetValueOrDefault(key) + 1; }
                            var once = cnt.Where(kv => kv.Value == 1).ToList();
                            int rimPts = f.Loops.Sum(l => l.CoEdges.Sum(ce => Parametric3D.Kernel.Tess.ExactFaceMesher.EdgePoints(ce.Edge).Count() - 1));
                            if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsd && fsd.Display != null)
                            {
                                var mb2 = new Parametric3D.Kernel.Tess.MeshBuilder(); mb2.BeginFace(f.Id); f.Surface.TessellateFace(f, mb2); mb2.EndFace();
                                var md2 = new Parametric3D.Kernel.Tess.MeshData(); mb2.FillInto(md2);
                                var c2 = new Dictionary<((float, float, float), (float, float, float)), int>();
                                for (int t = 0; t + 2 < md2.Indices.Length; t += 3) for (int k = 0; k < 3; k++) { var a = md2.Vertices[md2.Indices[t + k]].P; var c = md2.Vertices[md2.Indices[t + (k + 1) % 3]].P; var ka = (a.X, a.Y, a.Z); var kc = (c.X, c.Y, c.Z); var key = ka.CompareTo(kc) < 0 ? (ka, kc) : (kc, ka); c2[key] = c2.GetValueOrDefault(key) + 1; }
                                Console.WriteLine($"     display path: tris {md2.Indices.Length / 3}, single-use edges {c2.Count(kv => kv.Value == 1)}, display tris {fsd.Display.Count}, normals {fsd.DisplayNormals?.Count}");
                            }
                            int degen = 0; for (int t = 0; t < um.T.Count; t += 3) { var A = um.P[um.T[t]]; var B = um.P[um.T[t + 1]]; var C = um.P[um.T[t + 2]]; if ((A - B).Length < 1e-9 || (B - C).Length < 1e-9 || (A - C).Length < 1e-9) degen++; }
                            int small = 0; for (int t = 0; t < um.T.Count; t += 3) { var A = um.P[um.T[t]]; var B = um.P[um.T[t + 1]]; var C = um.P[um.T[t + 2]]; if (Vector3d.Cross(B - A, C - A).Length < 1e-10) small++; }
                            Console.WriteLine($"     degenerate (coincident vertices) {degen}, zero-area {small}");
                            Console.WriteLine($"     uv mesh: tris {um.T.Count / 3}, single-use edges {once.Count} (rim segments {rimPts}), sample {string.Join(" ", once.Take(3).Select(kv => kv.Key.Item1.ToString("0.###") + "-" + kv.Key.Item2.ToString("0.###")))}");
                        }
                    }
                }
            }
        }
        finally { Parametric3D.Kernel.Tess.ExactFaceMesher.UseUvMesher = false; }
    }
}
static partial class Program
{
    static void G3Tolerances()
    {
        var rows = new List<string>();
        void Probe(string name, TSolid b, double maxTol)
        {
            if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
            var before = Tolerances.Check(b);
            var rep = Tolerances.Compute(b);
            var after = Tolerances.Check(b);
            bool ok = after.Count == 0 && rep.MaxEdge <= maxTol && rep.MaxVertex <= maxTol;
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {rep}; inconsistencies before {before.Count}, after {after.Count}{(after.Count > 0 ? " e.g. " + after[0] : "")}");
        }
        var w = new List<string>();
        Probe("box", Box(), 1e-6);
        Probe("4 holes", Scenario("S-HOLES4", w)[0], 1e-4);
        Probe("spherical cut", Scenario("S-SPHERE", w)[0], 1e-4);
        Probe("hole with a rounded rim", Scenario("S-ROUNDHOLE", w)[0], 1e-4);
        foreach (var seed in new[] { 8001, 8002 }) { var p = RealisticPart(seed, 20, out _); p.Rebuild(null); Probe($"realistic {seed}", p.Bodies[0], 1e-4); }
        {
            // Import: tolerances absorb the file's vertex gaps (IGES arcs fitted to points).
            var w2 = new List<string>(); var src = Scenario("S-SPHERE", w2)[0];
            var back = Parametric3D.Kernel.Export.IgesImport.Read(Parametric3D.Kernel.Export.IgesSolid.Write(src, w2), w2)!;
            Probe("IGES import (sphere cut)", back, 1e-3);
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("G3-TOLERANCES", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")).Select(r => r.Length > 220 ? r[..220] : r))));
    }
}
static partial class Program
{
    static void G4Ssi()
    {
        var rows = new List<string>();
        double OnPlane(Vector3d x, Ssi.Plane p) => Math.Abs(Vector3d.Dot(p.N.Normalized(), x - p.P));
        double OnSphere(Vector3d x, Ssi.Sphere s) => Math.Abs((x - s.C).Length - s.R);
        double OnCyl(Vector3d x, Ssi.Cylinder c) { var w = c.Axis.Normalized(); var d = x - c.P; return Math.Abs((d - w * Vector3d.Dot(d, w)).Length - c.R); }
        double OnCone(Vector3d x, Ssi.Cone k) { var w = k.Axis.Normalized(); var d = x - k.Apex; double h = Vector3d.Dot(d, w); double rho = (d - w * h).Length; return Math.Abs(rho * Math.Cos(k.HalfAngle) - h * Math.Sin(k.HalfAngle)); }
        double OnTorus(Vector3d x, Ssi.Torus t) { var w = t.Axis.Normalized(); var d = x - t.C; double h = Vector3d.Dot(d, w); double rho = (d - w * h).Length; return Math.Abs(Math.Sqrt((rho - t.R) * (rho - t.R) + h * h) - t.r); }
        double On(Vector3d x, Ssi.Shape sh) => sh switch { Ssi.Plane p => OnPlane(x, p), Ssi.Sphere s => OnSphere(x, s), Ssi.Cylinder c => OnCyl(x, c), Ssi.Cone k => OnCone(x, k), Ssi.Torus t => OnTorus(x, t), _ => double.NaN };
        void Case(string name, Ssi.Shape a, Ssi.Shape b, Ssi.Kind kind, int curves, Func<Ssi.Result, string?>? extra = null)
        {
            var sw0 = Stopwatch.StartNew();
            var r = Ssi.Intersect(a, b, Vector3d.Zero, 100);
            if (sw0.ElapsedMilliseconds > 200) name += $" [{sw0.ElapsedMilliseconds} ms]";
            double worst = 0;
            foreach (var c in r.Curves)
                for (int i = 0; i <= 64; i++) { var x = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 64); worst = Math.Max(worst, Math.Max(On(x, a), On(x, b))); }
            string? why = extra?.Invoke(r);
            bool ok = r.Kind == kind && (curves < 0 ? r.Curves.Count >= -curves : r.Curves.Count == curves) && worst < 1e-9 && why == null;
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {r}, on both surfaces within {worst:E1} mm {why}");
        }
        var z = Vector3d.UnitZ; var x0 = Vector3d.UnitX; var y0 = Vector3d.UnitY;
        Case("plane × plane", new Ssi.Plane(Vector3d.Zero, z), new Ssi.Plane(new Vector3d(0, 0, 3), new Vector3d(1, 0, 1)), Ssi.Kind.Curves, 1);
        Case("parallel planes", new Ssi.Plane(Vector3d.Zero, z), new Ssi.Plane(new Vector3d(0, 0, 3), z), Ssi.Kind.Empty, 0);
        Case("plane × sphere", new Ssi.Plane(new Vector3d(0, 0, 3), z), new Ssi.Sphere(Vector3d.Zero, 5), Ssi.Kind.Curves, 1,
            r => Math.Abs(((ArcCurve3)r.Curves[0]).Radius - 4) < 1e-12 ? null : "radius ≠ 4");
        Case("plane touching a sphere", new Ssi.Plane(new Vector3d(0, 0, 5), z), new Ssi.Sphere(Vector3d.Zero, 5), Ssi.Kind.Tangent, 0);
        Case("plane × cylinder at 30°", new Ssi.Plane(Vector3d.Zero, new Vector3d(0, Math.Sin(Math.PI / 6), Math.Cos(Math.PI / 6))), new Ssi.Cylinder(Vector3d.Zero, z, 4), Ssi.Kind.Curves, 1,
            r => r.Curves[0] is EllipseCurve3 e && Math.Abs(e.RU - 4 / Math.Cos(Math.PI / 6)) < 1e-12 && Math.Abs(e.RV - 4) < 1e-12 ? null : "ellipse axes wrong");
        Case("plane parallel to the axis at d = 3", new Ssi.Plane(new Vector3d(3, 0, 0), x0), new Ssi.Cylinder(Vector3d.Zero, z, 5), Ssi.Kind.Curves, 2,
            r => Math.Abs(((LineCurve3)r.Curves[0]).A.Y - ((LineCurve3)r.Curves[1]).A.Y) is var g && Math.Abs(Math.Abs(g) - 8) < 1e-9 ? null : "line spacing ≠ 8");
        Case("plane across the axis", new Ssi.Plane(new Vector3d(0, 0, 7), z), new Ssi.Cylinder(Vector3d.Zero, z, 5), Ssi.Kind.Curves, 1, r => r.Curves[0] is ArcCurve3 ? null : "not a circle");
        Case("sphere × sphere", new Ssi.Sphere(Vector3d.Zero, 5), new Ssi.Sphere(new Vector3d(6, 0, 0), 5), Ssi.Kind.Curves, 1,
            r => Math.Abs(((ArcCurve3)r.Curves[0]).Radius - 4) < 1e-12 ? null : "radius ≠ 4");
        Case("parallel cylinders", new Ssi.Cylinder(Vector3d.Zero, z, 5), new Ssi.Cylinder(new Vector3d(6, 0, 0), z, 5), Ssi.Kind.Curves, 2);
        Case("cylinder × coaxial sphere", new Ssi.Cylinder(Vector3d.Zero, z, 3), new Ssi.Sphere(new Vector3d(0, 0, 2), 5), Ssi.Kind.Curves, 2,
            r => r.Curves.All(c => c is ArcCurve3 a && Math.Abs(a.Radius - 3) < 1e-12) ? null : "circles wrong");
        Case("cross hole R2 through a shaft R10", new Ssi.Cylinder(Vector3d.Zero, z, 10), new Ssi.Cylinder(new Vector3d(0, 0, 30), y0, 2), Ssi.Kind.Curves, 2);
        Case("offset cross hole R4, axis 8 from the shaft axis (partial)", new Ssi.Cylinder(Vector3d.Zero, z, 10), new Ssi.Cylinder(new Vector3d(8, 0, 30), y0, 4), Ssi.Kind.Curves, 1);
        Case("oblique cross hole at 60°", new Ssi.Cylinder(Vector3d.Zero, z, 10), new Ssi.Cylinder(new Vector3d(0, 0, 30), new Vector3d(0, 1, 0.5774), 3), Ssi.Kind.Curves, 2);
        // Cone and torus (G4, second step).
        var cone45 = new Ssi.Cone(new Vector3d(0, 0, 10), -z, Math.PI / 4);           // opening downwards, half-angle 45°
        Case("plane across the cone axis", new Ssi.Plane(new Vector3d(0, 0, 4), z), cone45, Ssi.Kind.Curves, 1, r => Math.Abs(((ArcCurve3)r.Curves[0]).Radius - 6) < 1e-12 ? null : "radius ≠ 6");
        Case("oblique plane × cone (ellipse)", new Ssi.Plane(new Vector3d(0, 0, 4), new Vector3d(0.3, 0, 1)), cone45, Ssi.Kind.Curves, 1);
        Case("plane parallel to a generator (parabola)", new Ssi.Plane(new Vector3d(0, 0, 4), new Vector3d(1, 0, 1)), cone45, Ssi.Kind.Curves, 1);
        Case("plane through the apex and the axis (two generators)", new Ssi.Plane(new Vector3d(0, 0, 10), y0), cone45, Ssi.Kind.Curves, 2);
        Case("cone × coaxial cylinder", cone45, new Ssi.Cylinder(Vector3d.Zero, z, 3), Ssi.Kind.Curves, 1, r => Math.Abs(((ArcCurve3)r.Curves[0]).Radius - 3) < 1e-12 ? null : "radius ≠ 3");
        Case("cone × coaxial sphere", cone45, new Ssi.Sphere(new Vector3d(0, 0, 10), 5), Ssi.Kind.Curves, 1, r => Math.Abs(((ArcCurve3)r.Curves[0]).Radius - 5 / Math.Sqrt(2)) < 1e-12 ? null : "radius wrong");
        var torus = new Ssi.Torus(Vector3d.Zero, z, 10, 3);
        Case("plane across the torus axis", new Ssi.Plane(new Vector3d(0, 0, 1.5), z), torus, Ssi.Kind.Curves, 2,
            r => r.Curves.Select(c => ((ArcCurve3)c).Radius).OrderBy(x => x).ToArray() is var rr && Math.Abs(rr[0] - (10 - Math.Sqrt(9 - 2.25))) < 1e-12 && Math.Abs(rr[1] - (10 + Math.Sqrt(9 - 2.25))) < 1e-12 ? null : "radii wrong");
        Case("plane through the torus axis", new Ssi.Plane(Vector3d.Zero, y0), torus, Ssi.Kind.Curves, 2);
        Case("torus × coaxial cylinder", torus, new Ssi.Cylinder(Vector3d.Zero, z, 11), Ssi.Kind.Curves, 2);
        Case("torus × coaxial sphere", torus, new Ssi.Sphere(Vector3d.Zero, 10), Ssi.Kind.Curves, 2);
        Case("cone × coaxial torus", new Ssi.Cone(new Vector3d(0, 0, -10), z, Math.PI / 4), torus, Ssi.Kind.Curves, 2);
        // General position — tracing. curves < 0: at least |n| curves.
        double beta = Math.Asin(3.0 / 10);
        Case("Villarceau: bitangent plane × torus (circles of radius R)", new Ssi.Plane(Vector3d.Zero, new Vector3d(-Math.Sin(beta), 0, Math.Cos(beta))), torus, Ssi.Kind.Curves, -2,
            r => r.Curves.All(c => Enumerable.Range(0, 33).Select(i => c.PointAt(c.T0 + (c.T1 - c.T0) * i / 32)).All(q => Math.Abs((q - new Vector3d(0, 3, 0)).Length - 10) < 1e-6 || Math.Abs((q - new Vector3d(0, -3, 0)).Length - 10) < 1e-6)) ? null : "not on the Villarceau circles");
        Case("slightly tilted plane × torus (inner and outer loop)", new Ssi.Plane(new Vector3d(0, 0, 1), new Vector3d(0.05, 0.02, 1)), torus, Ssi.Kind.Curves, 2);
        Case("tilted plane leaving the tube on one side (one loop)", new Ssi.Plane(new Vector3d(0, 0, 1), new Vector3d(0.2, 0.1, 1)), torus, Ssi.Kind.Curves, 1);
        Case("cylinder × sphere off the axis", new Ssi.Cylinder(new Vector3d(4, 0, 0), z, 3), new Ssi.Sphere(Vector3d.Zero, 5), Ssi.Kind.Curves, -1);
        Case("cylinder × cone, axes crossing", new Ssi.Cylinder(new Vector3d(0, 0, 3), x0, 2), new Ssi.Cone(new Vector3d(0, 0, 10), -z, Math.PI / 6), Ssi.Kind.Curves, -1);
        Case("cone × cone, axes crossing", new Ssi.Cone(new Vector3d(0, 0, 10), -z, Math.PI / 6), new Ssi.Cone(new Vector3d(-10, 0, 3), x0, Math.PI / 8), Ssi.Kind.Curves, -1);
        Case("torus × torus, perpendicular ring through the tube", torus, new Ssi.Torus(new Vector3d(10, 0, 0), y0, 4, 2), Ssi.Kind.Curves, -1);
        Console.WriteLine(string.Join("\n", rows));
        Check("G4-SSI-QUADRICS", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void G4ExactEdges()
    {
        var rows = new List<string>();
        void Probe(string name, TSolid published)
        {
            // Поставка 2: результат детали опубликован и неизменяем — уточнение рёбер делается на структурной
            // копии (TopologyClone.Deep), а не на оригинале.
            var b = TopologyClone.Deep(published);
            if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
            double v0 = AnalyticVolume.Of(b) ?? double.NaN;
            var tBefore = Tolerances.Compute(b);
            // Fresh tolerances after the change: reset to the floor first.
            var rep = ExactEdges.Apply(b);
            foreach (var e in b.Edges) e.Tolerance = 0;
            foreach (var v in b.Edges.SelectMany(x => new[] { x.V0, x.V1 })) v.Tolerance = 0;
            var tAfter = Tolerances.Compute(b);
            double v1 = AnalyticVolume.Of(b) ?? double.NaN;
            var verdict = SolidValidator.Validate(b).Summary();
            bool ok = !verdict.Contains("error") && tAfter.MaxEdge <= tBefore.MaxEdge + 1e-12 && Math.Abs(v1 - v0) < 1e-6 * Math.Abs(v0) && Tolerances.Check(b).Count == 0;
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {rep}; max edge tolerance {tBefore.MaxEdge:E1} → {tAfter.MaxEdge:E1}, vertex {tBefore.MaxVertex:E1} → {tAfter.MaxVertex:E1}; volume {v0:0.######} → {v1:0.######}; {verdict}");
        }
        var w = new List<string>();
        Probe("4 holes", Scenario("S-HOLES4", w)[0]);
        Probe("spherical cut", Scenario("S-SPHERE", w)[0]);
        Probe("hole with a rounded rim", Scenario("S-ROUNDHOLE", w)[0]);
        foreach (var seed in new[] { 8001, 8002 }) { var p = RealisticPart(seed, 20, out _); p.Rebuild(null); Probe($"realistic {seed}", p.Bodies[0]); }
        Console.WriteLine(string.Join("\n", rows));
        Check("G4-EXACT-EDGES", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")).Select(r => r.Length > 260 ? r[..260] : r))));
    }
}
static partial class Program
{
    static void G4ExactBoolean()
    {
        var rows = new List<string>();
        void Case(string name, TSolid body, Ssi.Cylinder cyl, double expectVol, bool expectExact = true, Vector3d? bottom = null)
        {
            var w = new List<string>();
            if (body.TopologyConvention == 0) TopologyConventions.Normalize(body);
            var ex = bottom is { } bt ? ExactBoolean.SubtractBlindCylinder(body, cyl, bt, w) : ExactBoolean.SubtractThroughCylinder(body, cyl, w);
            if (!expectExact) { rows.Add($"{(ex == null ? "OK  " : "FAIL")} {name}: refused as expected — {w.LastOrDefault()}"); return; }
            if (ex != null && name.Contains("shaft")) foreach (var ff in ex.Faces) Console.WriteLine($"EXFACE {ff.Id} {ff.Surface.GetType().Name} loops {ff.Loops.Count} {string.Join("/", ff.Loops.Select(l => l.CoEdges.Count))} tag {ff.Tag}");
            if (ex == null) { foreach (var m in w.Where(m => m.StartsWith("DBG"))) Console.WriteLine(m); rows.Add($"FAIL {name}: refused — {string.Join(" | ", w.Where(m => !m.StartsWith("DBG")))}"); return; }
            var v = SolidValidator.Validate(ex);
            foreach (var i in v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)) Console.WriteLine($"EXB {name}: {i}");
            foreach (var f in ex.Faces.Where(ff => ff.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface))
            {
                var um = Parametric3D.Kernel.Tess.UvMesher.Mesh(f, Parametric3D.Core.Tol.Chord, out var whyU);
                var em = Parametric3D.Kernel.Tess.ExactFaceMesher.Mesh(f, out var whyE, out _);
                Console.WriteLine($"EXB   wall: uv {(um == null ? "no: " + whyU : "ok")} | exact mesher {(em == null ? "no: " + whyE : "ok")}");
            }
            foreach (var f in ex.Faces) Console.WriteLine($"EXB   face {f.Id} {f.Surface.GetType().Name} display {(f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Display?.Count} facets {(f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Facets.Count} src {(f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source?.GetType().Name}");
            double? va = AnalyticVolume.Of(ex);
            Console.WriteLine($"EXV {name}: mesh volume {Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(ex)):0.######}; per-face: ");
            var tol = Tolerances.Compute(ex);
            // The polyhedral boolean for comparison.
            var poly = new List<TSolid> { body };
            var fd = Frame3.FromNormal(cyl.P, cyl.Axis.Normalized());
            double toolLen = bottom is { } b2 ? Vector3d.Dot(b2 - cyl.P, cyl.Axis.Normalized()) + 100 : 200;
            var tool = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, cyl.R, toolLen);
            var xf = new Parametric3D.Kernel.Ops.TransformOp.Xform(q => fd.Origin + fd.U * q.X + fd.V * q.Y + fd.W * (q.Z - 100), d => fd.U * d.X + fd.V * d.Y + fd.W * d.Z, false);
            tool = Parametric3D.Kernel.Ops.TransformOp.Copy(tool, xf);
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(poly, tool, new List<string>());
            double? vp = AnalyticVolume.Of(poly[0]);
            var tolP = Tolerances.Compute(poly[0]);
            // The shaft rims are traced curves: the analytic volume integrates them as polylines (1 024 steps, O(h²)),
            // 5e-4 mm³ on this body — the limit of the volume integral, not of the geometry.
            double relTol = 1e-9;
            bool ok = v.IsValid && va != null && Math.Abs(va.Value - expectVol) < relTol * expectVol && ex.Faces.Count() == poly[0].Faces.Count();
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: exact volume {va:0.#########} vs formula {expectVol:0.#########} (polyhedral {vp:0.#########}); faces {ex.Faces.Count()} vs {poly[0].Faces.Count()}; max tolerance exact {tol.MaxEdge:E1} vs polyhedral {tolP.MaxEdge:E1}; {v.Summary()}");
        }
        double box = 60 * 40 * 20;
        Case("vertical hole R8 through a box", Box(), new Ssi.Cylinder(new Vector3d(30, 20, 0), Vector3d.UnitZ, 8), box - Math.PI * 64 * 20);
        // Oblique hole at 30° from the vertical in the xz-plane: the cut prism of an oblique cylinder has volume π R² · h / cos θ.
        double th = Math.PI / 6;
        Case("hole R5 tilted 30° through a box", Box(), new Ssi.Cylinder(new Vector3d(30, 20, 10), new Vector3d(Math.Sin(th), 0, Math.Cos(th)), 5), box - Math.PI * 25 * 20 / Math.Cos(th));
        // Blind holes: entry at the top (z = 20), axis downwards, flat bottom at depth 12.
        Case("blind hole R6, depth 12", Box(), new Ssi.Cylinder(new Vector3d(30, 20, 30), -Vector3d.UnitZ, 6), box - Math.PI * 36 * 12, bottom: new Vector3d(30, 20, 8));
        // Oblique entry face is not in a box; tilted blind hole: axis 20° from the vertical, bottom ⟂ axis.
        // Removed volume = πR² × (axial distance from the bottom plane to the rim centre on the entry plane).
        {
            double a = Math.PI / 9; var ax = new Vector3d(Math.Sin(a), 0, -Math.Cos(a));
            var top = new Vector3d(30, 20, 20);                               // where the axis meets the top face
            var bottomPt = top + ax * 10;
            Case("blind hole R4 tilted 20°, axial depth 10", Box(), new Ssi.Cylinder(top - ax * 10, ax, 4), box - Math.PI * 16 * 10, bottom: bottomPt);
        }
        {
            // Cross hole through a shaft: the shaft wall is a cylinder, rims are 4th-order curves (tracing).
            var p = new Part("shaft"); var sk = CircleSketch(p, 0, 0, 0, 10);
            var exf = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = sk.Id }; exf.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); p.AddFeature(exf);
            p.Rebuild(null);
            // Removed volume of two perpendicular cylinders (R = 10, r = 2) with intersecting axes:
            // V = 8 ∫₀ʳ √(r² − x²) √(R² − x²) dx (Simpson, 200 000 intervals).
            double r = 2, Rr = 10; int N = 200000; double hh = r / N, acc = 0;
            for (int i = 0; i <= N; i++) { double x = i * hh; double fx = Math.Sqrt(Math.Max(0, r * r - x * x)) * Math.Sqrt(Rr * Rr - x * x); acc += (i == 0 || i == N ? 1 : i % 2 == 1 ? 4 : 2) * fx; }
            double removed = 8 * acc * hh / 3;
            foreach (var ff in p.Bodies[0].Faces) Console.WriteLine($"SHAFTFACE {ff.Id} {ff.Surface.GetType().Name} loops {ff.Loops.Count} tag {ff.Tag}");
            Case("cross hole R2 through a shaft R10 (curved face)", p.Bodies[0], new Ssi.Cylinder(new Vector3d(0, -20, 30), Vector3d.UnitY, 2), Math.PI * 100 * 60 - removed);
        }
        Case("hole at the box edge is refused (rim crosses a face boundary)", Box(), new Ssi.Cylinder(new Vector3d(2, 20, 0), Vector3d.UnitZ, 5), 0, expectExact: false);
        Console.WriteLine(string.Join("\n", rows));
        Check("G4-EXACT-BOOLEAN-THROUGH-HOLE", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")).Select(r => r.Length > 300 ? r[..300] : r))));
    }
}
static partial class Program
{
    static void G4HoleFeature()
    {
        var rows = new List<string>();
        void Case(string name, bool through, double depth, double tiltDeg, double expectRemoved)
        {
            var p = new Part("oblique"); TestGeometry.AddCube(p, 60);          // cube [-30,30]³
            double a = tiltDeg * Math.PI / 180;
            var dir = new Vector3d(Math.Sin(a), 0, -Math.Cos(a));
            var h = new Parametric3D.Model.Features.HoleFeature("Oblique") { Direction = dir, ThroughAll = through, Diameter = new Parametric3D.Model.Features.ValueRef(8) };
            if (!through) h.Depth = new Parametric3D.Model.Features.ValueRef(depth);
            h.Points.Add(new Vector3d(0, 0, 30));
            p.AddFeature(h);
            var r = p.Compute(null); p.Apply(r);
            double? va = AnalyticVolume.Of(p.Bodies[0]);
            if (name.Contains("25°"))
            {
                Parametric3D.Model.Features.HoleFeature.ExactHoles = false;
                var p2 = new Part("o2"); TestGeometry.AddCube(p2, 60);
                var h2 = new Parametric3D.Model.Features.HoleFeature("O") { Direction = dir, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(8) }; h2.Points.Add(new Vector3d(0, 0, 30));
                p2.AddFeature(h2); p2.Rebuild(null);
                Parametric3D.Model.Features.HoleFeature.ExactHoles = true;
                Console.WriteLine($"POLY25 {AnalyticVolume.Of(p2.Bodies[0]):0.######} mesh {Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(p2.Bodies[0])):0.###}; exact mesh {Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(p.Bodies[0])):0.###}");
            }
            bool exactPath = r.Warnings.Any(x => x.Contains("ExactBoolean: through hole") || x.Contains("exact surface intersections"));
            double expect = 216000 - expectRemoved;
            exactPath |= r.Warnings.Any(x => x.Contains("ExactCylinderCut:") && x.Contains("rim arc"));
            double tolV = name.Contains("25°") ? 0.15 : 1e-6;             // quadrature reference (N = 2000: converging, 0.56 → 0.09 mm³ from N = 600)
            bool ok = h.Error == null && exactPath && va != null && Math.Abs(va.Value - expect) < tolV && SolidValidator.Validate(p.Bodies[0]).IsValid;
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: exact path {exactPath}, volume {va:0.#########} vs {expect:0.#########} {h.Error}");
        }
        // Through hole Ø8 tilted 25° through a 60 mm cube (enters the top, leaves the bottom): πR²·60/cos θ.
        Case("oblique through hole Ø8 at 15°", true, 0, 15, Math.PI * 16 * 60 / Math.Cos(15 * Math.PI / 180));
        // Blind hole Ø8 at 15°, axial depth 20 from the axis point on the top face: πR²·20.
        Case("oblique blind hole Ø8 at 15°, depth 20", false, 20, 15, Math.PI * 16 * 20);
        // At 25° the exit ellipse reaches the side face: the face-splitting cut takes it.
        {
            double a = 25 * Math.PI / 180, R = 4;
            // Removed = ∫ over the cube of the oblique cylinder: computed by 2-D quadrature over the cylinder cross-section.
            var dir = new Vector3d(Math.Sin(a), 0, -Math.Cos(a)); var top = new Vector3d(0, 0, 30);
            var f = Frame3.FromNormal(top, dir); int N = 2000; double acc = 0, hs = 2 * R / N;
            for (int i = 0; i < N; i++) for (int j = 0; j < N; j++)
            {
                double x = -R + (i + 0.5) * hs, y = -R + (j + 0.5) * hs; if (x * x + y * y > R * R) continue;
                var o = top + f.U * x + f.V * y;                  // line o + t·dir: length inside the cube
                double t0 = double.MinValue, t1 = double.MaxValue;
                for (int k = 0; k < 3; k++) { double oc = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dc = k == 0 ? dir.X : k == 1 ? dir.Y : dir.Z;
                    if (Math.Abs(dc) < 1e-15) { if (oc < -30 || oc > 30) { t0 = 1; t1 = 0; } continue; }
                    double ta = (-30 - oc) / dc, tb = (30 - oc) / dc; t0 = Math.Max(t0, Math.Min(ta, tb)); t1 = Math.Min(t1, Math.Max(ta, tb)); }
                if (t1 > t0) acc += (t1 - t0) * hs * hs;
            }
            Case("oblique through hole Ø8 at 25° (exit reaches the side face)", true, 0, 25, acc);
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("G4-HOLE-FEATURE-OBLIQUE-EXACT", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void G4FaceSplit()
    {
        var rows = new List<string>();
        void Case(string name, Ssi.Cylinder cyl, double removed)
        {
            var body = Box(); TopologyConventions.Normalize(body);
            var w = new List<string>();
            var ex = ExactCylinderCut.Cut(body, cyl, w);
            if (ex == null) { rows.Add($"FAIL {name}: {string.Join(" | ", w)}"); return; }
            var v = SolidValidator.Validate(ex); double? va = AnalyticVolume.Of(ex);
            var poly = new List<TSolid> { Box() };
            var fd = Frame3.FromNormal(cyl.P, cyl.Axis.Normalized());
            var tool = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, cyl.R, 200),
                new Parametric3D.Kernel.Ops.TransformOp.Xform(q => fd.Origin + fd.U * q.X + fd.V * q.Y + fd.W * (q.Z - 100), d => fd.U * d.X + fd.V * d.Y + fd.W * d.Z, false));
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(poly, tool, new List<string>());
            double expect = 48000 - removed;
            bool ok = v.IsValid && va != null && Math.Abs(va.Value - expect) < 1e-6 * expect && ex.Faces.Count() == poly[0].Faces.Count();
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: exact {va:0.#########} vs formula {expect:0.#########} (polyhedral {AnalyticVolume.Of(poly[0]):0.#########}); faces {ex.Faces.Count()} vs {poly[0].Faces.Count()}; {v.Summary()}");
        }
        double seg = 25 * Math.Acos(0.4) - 2 * Math.Sqrt(21);
        Case("notch at an edge: vertical R5, axis 2 mm inside the face x = 0", new Ssi.Cylinder(new Vector3d(2, 20, 0), Vector3d.UnitZ, 5), (Math.PI * 25 - seg) * 20);
        Case("corner cut: vertical R5 on the corner edge", new Ssi.Cylinder(new Vector3d(0, 0, 0), Vector3d.UnitZ, 5), Math.PI * 25 / 4 * 20);
        Case("through hole inside the faces (no splitting)", new Ssi.Cylinder(new Vector3d(30, 20, 0), Vector3d.UnitZ, 8), Math.PI * 64 * 20);
        Case("slot across the whole width: horizontal R5 along y through the top", new Ssi.Cylinder(new Vector3d(30, 0, 20), Vector3d.UnitY, 5), Math.PI * 25 / 2 * 40);
        // Curved faces: a shaft R10 × 60 (wall = cylinder, ends = planes) and a cross hole r = 4 along y at
        // height 2 — the hole cuts the shaft wall AND the bottom end edge (rim crosses the end circle).
        {
            var ps = new Part("shaftEnd"); var sk = CircleSketch(ps, 0, 0, 0, 10);
            var exf = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = sk.Id }; exf.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); ps.AddFeature(exf); ps.Rebuild(null);
            var body = ps.Bodies[0];
            double r = 4, zc = 2, Rr = 10;
            // removed = ∫ 2√(R² − x²)·L(x) dx, L(x) = [max(0, zc − √(r² − x²)), zc + √(r² − x²)]; Simpson split at the kink.
            double L(double x) { double h = Math.Sqrt(Math.Max(0, r * r - x * x)); return zc + h - Math.Max(0, zc - h); }
            double F(double x) => 2 * Math.Sqrt(Rr * Rr - x * x) * L(x);
            double Simpson(double a, double b, int n) { double hh = (b - a) / n, acc = F(a) + F(b); for (int i = 1; i < n; i++) acc += F(a + i * hh) * (i % 2 == 1 ? 4 : 2); return acc * hh / 3; }
            double k = Math.Sqrt(r * r - zc * zc);
            double removed = 2 * (Simpson(0, k, 200000) + Simpson(k, r, 200000));
            var w2 = new List<string>();
            var ex = ExactCylinderCut.Cut(body, new Ssi.Cylinder(new Vector3d(0, -20, zc), Vector3d.UnitY, r), w2);
            if (ex == null) rows.Add($"FAIL cross hole at the shaft end: {string.Join(" | ", w2)}");
            else
            {
                var v = SolidValidator.Validate(ex); double? va = AnalyticVolume.Of(ex);
                double expect = Math.PI * 100 * 60 - removed;
                bool ok = v.IsValid && va != null && Math.Abs(va.Value - expect) < 1e-8 * expect;
                rows.Add($"{(ok ? "OK  " : "FAIL")} cross hole at the shaft end (rims cross the end circle): exact {va:0.#########} vs integral {expect:0.#########}; faces {ex.Faces.Count()}; {v.Summary()}");
            }
        }
        // Blind cuts (flat bottom ⟂ axis, bottom inside the material).
        {
            // (a) Oblique blind hole R4 entering the top near the edge x = 0: the entry ellipse crosses the edge.
            double a = Math.PI / 6; var ax = new Vector3d(Math.Sin(a), 0, -Math.Cos(a));
            var top = new Vector3d(3, 20, 20); double depth = 14; var bot = top + ax * depth;
            var cylA = new Ssi.Cylinder(top - ax * 30, ax, 4);
            var body = Box(); TopologyConventions.Normalize(body);
            var w3 = new List<string>();
            var ex = ExactCylinderCut.Cut(body, cylA, bot, w3);
            // Reference: lines along the axis through the cross-section disk, clipped by the box and the bottom.
            var fr = Frame3.FromNormal(top, ax); int N = 2000; double R = 4, hs = 2 * R / N, acc = 0;
            for (int i = 0; i < N; i++) for (int j = 0; j < N; j++)
            {
                double x = -R + (i + 0.5) * hs, y = -R + (j + 0.5) * hs; if (x * x + y * y > R * R) continue;
                var o = top + fr.U * x + fr.V * y; double t0 = double.MinValue, t1 = depth;
                var lo = new Vector3d(0, 0, 0); var hi = new Vector3d(60, 40, 20);
                for (int k = 0; k < 3; k++) { double oc = o[k], dc = ax[k]; if (Math.Abs(dc) < 1e-15) { if (oc < lo[k] || oc > hi[k]) { t0 = 1; t1 = 0; } continue; }
                    double ta = (lo[k] - oc) / dc, tb = (hi[k] - oc) / dc; t0 = Math.Max(t0, Math.Min(ta, tb)); t1 = Math.Min(t1, Math.Max(ta, tb)); }
                if (t1 > t0) acc += (t1 - t0) * hs * hs;
            }
            if (ex == null) rows.Add($"FAIL oblique blind hole across the top edge: {string.Join(" | ", w3)}");
            else
            {
                var v = SolidValidator.Validate(ex); double? va = AnalyticVolume.Of(ex); double expect = 48000 - acc;
                bool ok = v.IsValid && va != null && Math.Abs(va.Value - expect) < 0.15;
                rows.Add($"{(ok ? "OK  " : "FAIL")} oblique blind hole across the top edge: exact {va:0.######} vs quadrature {expect:0.######}; faces {ex.Faces.Count()}; {v.Summary()}");
            }
        }
        {
            // (b) Blind cross hole r = 4 into a shaft R10 from the side, bottom on the shaft axis plane y = 0.
            var ps2 = new Part("shaftBlind"); var sk2 = CircleSketch(ps2, 0, 0, 0, 10);
            var ex2 = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = sk2.Id }; ex2.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); ps2.AddFeature(ex2); ps2.Rebuild(null);
            double r = 4, Rr = 10; int Nn = 200000; double hh = r / Nn, acc2 = 0;
            for (int i = 0; i <= Nn; i++) { double x = i * hh; double fx = Math.Sqrt(Math.Max(0, r * r - x * x)) * Math.Sqrt(Rr * Rr - x * x); acc2 += (i == 0 || i == Nn ? 1 : i % 2 == 1 ? 4 : 2) * fx; }
            double removedHalf = 8 * acc2 * hh / 3 / 2;
            var w4 = new List<string>();
            var ex = ExactCylinderCut.Cut(ps2.Bodies[0], new Ssi.Cylinder(new Vector3d(0, -20, 30), Vector3d.UnitY, r), new Vector3d(0, 0, 30), w4);
            // Обязательный успех (не «успешный отказ»): тело без обходов проверки.
            if (ex == null) rows.Add($"FAIL blind cross hole into a shaft: refused — {string.Join(" | ", w4)}");
            else
            {
                var v = SolidValidator.Validate(ex); double? va = AnalyticVolume.Of(ex); double expect = Math.PI * 100 * 60 - removedHalf;
                bool ok = v.IsValid && va != null && Math.Abs(va.Value - expect) < 1e-8 * expect;
                foreach (var i in v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)) Console.WriteLine("BLINDISSUE " + i);
                foreach (var ff in ex.Faces.Where(q => q.Tag.Contains("side"))) foreach (var l in ff.Loops) Console.WriteLine($"BLINDLOOP outer {l.IsOuter} " + string.Join(" ", l.CoEdges.Select(c => $"{c.Edge.Curve.GetType().Name[0]}{(c.Reversed ? "-" : "+")}[{c.Edge.V0.P:0.#}>{c.Edge.V1.P:0.#}]")));
                foreach (var ff in ex.Faces) Console.WriteLine($"BLINDFACE {ff.Tag} {ff.Surface.GetType().Name} src {(ff.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source?.GetType().Name} loops {string.Join("/", ff.Loops.Select(l => l.CoEdges.Count))} display {(ff.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Display?.Count}");
                rows.Add($"{(ok ? "OK  " : "FAIL")} blind cross hole into a shaft (bottom on the axis plane): exact {va:0.#########} vs integral {expect:0.#########}; faces {ex.Faces.Count()}; {v.Summary()}");
            }
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("G4-FACE-SPLIT-CYLINDER", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")).Select(r => r.Length > 300 ? r[..300] : r))));
    }
}
static partial class Program
{
    static void ShaftBlindDbg()
    {
        var ps2 = new Part("shaftBlind"); var sk2 = CircleSketch(ps2, 0, 0, 0, 10);
        var ex2 = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = sk2.Id }; ex2.Side1.Distance = new Parametric3D.Model.Features.ValueRef(60); ps2.AddFeature(ex2); ps2.Rebuild(null);
        ExactCylinderCut.DebugKeepInvalid = true;
        ExactCylinderCut.DebugNoOrient = Environment.GetEnvironmentVariable("NOORIENT") == "1";
        var w = new List<string>();
        var ex = ExactCylinderCut.Cut(ps2.Bodies[0], new Ssi.Cylinder(new Vector3d(0, -20, 30), Vector3d.UnitY, 4), new Vector3d(0, 0, 30), w);
        ExactCylinderCut.DebugKeepInvalid = false;
        foreach (var m in w) Console.WriteLine("SBD " + m);
        if (ex == null) return;
        Console.WriteLine("SBD validate: " + SolidValidator.Validate(ex).Summary());
        {
            var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(ex);
            var owner = new Dictionary<((float, float, float), (float, float, float)), List<uint>>();
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    var a = md.Vertices[md.Indices[t + k]].P; var b = md.Vertices[md.Indices[t + (k + 1) % 3]].P;
                    var key = ((a.X, a.Y, a.Z), (b.X, b.Y, b.Z));
                    if (!owner.TryGetValue(key, out var l)) owner[key] = l = new List<uint>(); l.Add(md.Vertices[md.Indices[t]].FaceId);
                }
            foreach (var f in ex.Faces)
            {
                int cnt = 0; for (int t = 0; t + 2 < md.Indices.Length; t += 3) if (md.Vertices[md.Indices[t]].FaceId == f.Id) cnt++;
                var fs = f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface;
                Console.WriteLine($"SBD tess {f.Tag}: {cnt} triangles; facets {fs?.Facets.Count}, display {fs?.Display?.Count}");
            }
            {
                // Points on the top rim (z = 60) used by the side face and by the top cap.
                List<Vector3d> RimPts(string tag) => ex.Faces.First(f => f.Tag.Contains(tag)).Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx
                    ? fsx.Facets.SelectMany(t => new[] { t.A, t.B, t.C }).Where(q => Math.Abs(q.Z - 60) < 1e-6).Distinct().OrderBy(q => Math.Atan2(q.Y, q.X)).ToList() : new();
                var sp = RimPts("side"); var tp = RimPts("top");
                int unmatched = sp.Count(q => !tp.Any(r => (r - q).Length < 1e-9));
                Console.WriteLine($"SBD top rim: side {sp.Count} pts, top {tp.Count} pts, side pts not in top {unmatched}; e.g. side {string.Join(" ", sp.Take(3).Select(q => q.ToString("0.######")))} top {string.Join(" ", tp.Take(3).Select(q => q.ToString("0.######")))}");
                foreach (var f in ex.Faces) if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fz)
                {
                    var ns = fz.Facets.Aggregate(Vector3d.Zero, (acc, t) => acc + Vector3d.Cross(t.B - t.A, t.C - t.A));
                    Console.WriteLine($"SBD normal sum {f.Tag}: {ns.Normalized():0.###} (|{ns.Length:0.#}|)");
                }
                var topEdge = ex.Edges.Where(e => Math.Abs(e.V0.P.Z - 60) < 1e-6 && Math.Abs(e.V1.P.Z - 60) < 1e-6).ToList();
                Console.WriteLine($"SBD top rim edges: {topEdge.Count} " + string.Join(" ", topEdge.Select(e => $"{e.Curve.GetType().Name} {e.V0.P:0.##}>{e.V1.P:0.##} pts {Parametric3D.Kernel.Tess.ExactFaceMesher.EdgePoints(e).Count()}")));
            }
            var pairs = new Dictionary<string, int>();
            foreach (var (k, l) in owner.Where(kv => kv.Value.Count > 1)) { var tags = string.Join("|", l.Select(id => ex.Faces.First(f => f.Id == id).Tag).OrderBy(x => x)); pairs[tags] = pairs.GetValueOrDefault(tags) + 1; }
            foreach (var (k, n) in pairs) Console.WriteLine($"SBD same-direction pair {k}: {n}");
            foreach (var (k, l) in owner.Where(kv => kv.Value.Count > 1).Take(4))
                Console.WriteLine($"SBD conflict edge {k.Item1}->{k.Item2} owners {string.Join(",", l.Select(id => ex.Faces.First(f => f.Id == id).Tag.Split('/').Last()))}");
        }
        foreach (var f in ex.Faces)
        {
            var fs = f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface;
            // Internal consistency of this face's facets: edges used twice in the same direction.
            var dir = new Dictionary<(Vector3d, Vector3d), int>();
            int bad = 0;
            if (fs != null)
                foreach (var (a, b, c) in fs.Facets)
                    foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) }) { if (dir.ContainsKey((x, y))) bad++; dir[(x, y)] = 1; }
            if (f.Tag.Contains("side")) Parametric3D.Kernel.Tess.UvMesher.Debug = m => { if (m.StartsWith("classify") || m.StartsWith("strip") || m.StartsWith("area3") || m.StartsWith("rims")) Console.WriteLine("SBDUV " + m); };
            Parametric3D.Kernel.Tess.UvMesher.Mesh(f, Parametric3D.Core.Tol.Chord, out var why);
            Parametric3D.Kernel.Tess.UvMesher.Debug = null;
            if (fs != null && f.Tag.Contains("side"))
            {
                int outw = 0, inw = 0; var inPts = new List<Vector3d>();
                foreach (var (a, b, c) in fs.Facets) { var n = Vector3d.Cross(b - a, c - a); var m = (a + b + c) / 3; var rad = new Vector3d(m.X, m.Y, 0); if (Vector3d.Dot(n, rad) > 0) outw++; else { inw++; if (inPts.Count < 3) inPts.Add(m); } }
                double ar = fs.Facets.Sum(t => Vector3d.Cross(t.B - t.A, t.C - t.A).Length / 2);
                Console.WriteLine($"SBD side area {ar:0.###} (full band {2 * Math.PI * 10 * 60:0.###})");
                Console.WriteLine($"SBD side facets outward {outw} inward {inw} e.g. {string.Join(" ", inPts.Select(q => q.ToString("0.##")))}");
            }
            Console.WriteLine($"SBD face {f.Tag} facets {fs?.Facets.Count} same-direction edges {bad}; uv: {why ?? "ok"}");
        }
    }
}

static partial class Program
{
    /// <summary>Файл из tests/stepfiles: копируется в выходной каталог сборкой; путь можно переопределить P3D_FIXTURES.</summary>
    static string Fixture(string name)
    {
        var dir = Environment.GetEnvironmentVariable("P3D_FIXTURES") ?? Path.Combine(AppContext.BaseDirectory, "stepfiles");
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) throw new FileNotFoundException($"fixture {name} not found in {dir}");
        return p;
    }
}
