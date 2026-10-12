using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void K05Survey()
    {
        int total = 0, consistent = 0, anchored = 0;
        var agg = new Dictionary<string, int>();
        void One(string id, TSolid s)
        {
            total++;
            var a = TopologyConventions.Normalize(s);
            var v = SolidValidator.ValidateBRep(s);
            if (a.Consistent) consistent++;
            if (a.Unanchored == 0) anchored++;
            Log(new { k05survey = id, report = a.ToString(), convention = s.TopologyConvention, sameDir = v.Metrics["brep.coedgesSameDirection"], problems = a.Problems });
        }
        foreach (var id in Scenarios) { var w = new List<string>(); foreach (var b in Scenario(id, w)) One(id, b); }
        for (int k = 0; k < 450; k += 9) { var w = new List<string>(); foreach (var b in Sequence(k, w)) One($"A01-SEQ-{k}", b); }
        One("PRIM-BOX", Box()); One("PRIM-CYL", PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10)); One("PRIM-SPHERE", PrimitiveOp.Sphere(Vector3d.Zero, 5));
        Log(new { k05survey = "total", total, consistent, anchored });
    }
}
static partial class Program
{
    static void SeqExact()
    {
        for (int k = 0; k < 450; k++)
        {
            var w = new List<string>(); var b = Sequence(k, w)[0];
            var m = Tessellator.Tessellate(b);
            var rep = SolidValidator.ValidateMesh(m);
            Log(new { seq = k, exact = b.Exact!.Solid.VolumeMm3().ToDouble(), mesh = MassProps.Volume(m), valid = rep.IsValid, conv = b.TopologyConvention });
        }
    }
}
static partial class Program
{
    internal sealed class CutBox(double x, double y, double z, double w, double d, double h) : Parametric3D.Model.Features.Feature
    {
        public double X = x, Y = y, Z = z, W = w, D = d, H = h;
        public override void Execute(Parametric3D.Model.Features.BuildContext c) =>
            BooleanOp.Subtract(c.Bodies, Box(X, Y, Z, W, D, H), c.Warnings);
    }

    static TEdge CornerEdge(Parametric3D.Model.Part p, double x, double y) =>
        p.Bodies[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 &&
            Math.Abs(e.V0.P.X - x) < 1e-6 && Math.Abs(e.V0.P.Y - y) < 1e-6 && Math.Abs(e.V1.P.X - x) < 1e-6 && Math.Abs(e.V1.P.Y - y) < 1e-6)
         .OrderByDescending(e => (e.V1.P - e.V0.P).Length).First();

    /// <summary>Part: cube 60, a far cut, then a round on the vertical corner edge at (30,30).</summary>
    static (Parametric3D.Model.Part P, Parametric3D.Model.Features.RoundFeature R, CutCyl Far) RoundTree(double r = 4)
    {
        var p = new Parametric3D.Model.Part("k05r");
        Parametric3D.Model.TestGeometry.AddCube(p, 60);
        var far = p.AddFeature(new CutCyl(-15, -15, 3));
        p.Rebuild(null);
        var rf = new Parametric3D.Model.Features.RoundFeature { Radius = r };
        rf.Edges.Add(EdgeRef.Of(CornerEdge(p, 30, 30)));
        p.AddFeature(rf);
        p.Rebuild(null);
        return (p, rf, far);
    }
    static double RoundLoss(double r, double len) => (r * r - Math.PI * r * r / 4) * len;

    static void K05()
    {
        Check("K05-NORMALIZE-IDEMPOTENT", () =>
        {
            foreach (var id in Scenarios)
            {
                var w = new List<string>();
                foreach (var b in Scenario(id, w))
                {
                    // The body as published (already normalized by Boolean.Evaluate, or raw) must be a fixed point.
                    if (b.TopologyConvention == 0) TopologyConventions.Normalize(b);
                    var again = TopologyConventions.Normalize(b);
                    Require(again.ReversedChanged == 0 && again.OrderChanged == 0 && again.SplitLoops == 0, $"{id}: second pass changed {again}");
                    Require(b.TopologyConvention == TopologyConventions.Version, $"{id}: not marked");
                    var v = SolidValidator.ValidateBRep(b);
                    Require(v.IsValid && v.Metrics["brep.coedgesSameDirection"] == 0, $"{id}: {v}");
                    foreach (var f in b.Faces)
                        foreach (var l in f.Loops)
                        {
                            var o = l.Ordered();
                            Require(o != null && o.Count == l.CoEdges.Count, $"{id}: loop of face {f.Id} is not one closed ring");
                            for (int i = 0; i < l.CoEdges.Count; i++)
                                Require(ReferenceEquals(l.CoEdges[i].End, l.CoEdges[(i + 1) % l.CoEdges.Count].Start) ||
                                        (l.CoEdges[i].End.P - l.CoEdges[(i + 1) % l.CoEdges.Count].Start.P).Length < 1e-6,
                                        $"{id}: loop of face {f.Id} not stored in traversal order");
                        }
                }
            }
        });

        Check("K05-VALIDATOR-ENFORCES-ORIENTATION", () =>
        {
            var w = new List<string>(); var b = Scenario("S-HOLE", w)[0];
            Require(b.TopologyConvention == TopologyConventions.Version, "Boolean result not normalized");
            var ce = b.Edges.First().CoEdges[0]; ce.Reversed = !ce.Reversed;
            var v = SolidValidator.ValidateBRep(b);
            Require(v.Issues.Any(i => i.Code == "coedge-orientation"), "flipped coedge not reported");
        });

        Check("K05-PART-BODIES-NORMALIZED", () =>
        {
            var (p, _, _) = RoundTree();
            Require(p.Bodies.All(b => b.TopologyConvention == TopologyConventions.Version), "part body not normalized");
            Require(p.Integrity == null, p.Integrity ?? "");
        });

        Check("K05-STEP-ONE-RING-PER-LOOP", () =>
        {
            var w = new List<string>(); var b = Scenario("S-HOLES4", w)[0];
            var sw = new StringWriter();
            bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "k05", w);
            string text = sw.ToString();
            int loops = text.Split("EDGE_LOOP(").Length - 1;
            int rings = b.Faces.Sum(f => f.Loops.Count);
            Log(new { test = "K05-STEP", ok, loops, rings });
            Require(ok, "STEP writer refused");
            Require(loops == rings, $"EDGE_LOOP count {loops} != rings {rings}");
            List<TSolid> back;
            try { back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(text, w); }
            catch (Exception ex) { File.WriteAllText("/tmp/k05.step", text); throw new Exception("import: " + ex.GetType().Name + " " + ex.Message + " " + ex.StackTrace?.Split('\n').FirstOrDefault()); }
            double v0 = MassProps.Volume(Tessellator.Tessellate(b)), v1 = back.Sum(s => MassProps.Volume(Tessellator.Tessellate(s)));
            Log(new { test = "K05-STEP-ROUNDTRIP", v0, v1, bodies = back.Count });
            // Round trip of this body is a known exchange defect (also before K05: 39908 vs 45744);
            // it belongs to K10 and is only logged here. A single-hole body round-trips exactly.
            var w2 = new List<string>(); var h = Scenario("S-HOLE", w2)[0]; var sw2 = new StringWriter();
            Parametric3D.Kernel.Export.StepExact.Write(sw2, new[] { h }, "k05h", w2);
            var hb = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw2.ToString(), w2);
            double h0 = MassProps.Volume(Tessellator.Tessellate(h)), h1 = hb.Sum(x => MassProps.Volume(Tessellator.Tessellate(x)));
            Require(hb.Count == 1 && Math.Abs(h1 - h0) < 1e-3 * h0, $"single-hole round trip {h1} vs {h0}");
            // K10: after fixing the importer's edge direction, plane+cylinder bodies round-trip.
            foreach (var id in new[] { "S-HOLES4", "S-HOLES8", "S-OVERLAP", "S-UNION", "S-ROUND", "S-SPHERE", "S-ROUNDHOLE" })
            {
                var w3 = new List<string>(); var b3 = Scenario(id, w3)[0]; var sw3 = new StringWriter();
                Parametric3D.Kernel.Export.StepExact.Write(sw3, new[] { b3 }, id, w3);
                var back3 = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw3.ToString(), w3);
                double a0 = MassProps.Volume(Tessellator.Tessellate(b3)), a1 = back3.Sum(x => MassProps.Volume(Tessellator.Tessellate(x)));
                Log(new { test = "K10-STEP-ROUNDTRIP", id, a0, a1 });
                Require(back3.Count == 1 && Math.Abs(a1 - a0) < 1e-3 * a0, $"{id}: round trip {a1} vs {a0}");
                var vb = SolidValidator.Validate(back3[0]);
                Require(vb.IsValid, $"{id}: imported body invalid: {vb.Summary()}");
            }
        });

        Check("K05-REF-UNRELATED-EDIT", () =>
        {
            var (p, rf, far) = RoundTree();
            double v0 = p.Volume;
            Require(rf.Error == null, rf.Error ?? "");
            far.R = 4; p.MarkDirtyFrom(far); p.Rebuild(null);
            Require(rf.Error == null, "round failed after unrelated edit: " + rf.Error);
            var (q, _, farQ) = RoundTree(); farQ.R = 4; q.MarkDirtyFrom(farQ);
            bool prev = Parametric3D.Model.Part.UseCheckpoints; Parametric3D.Model.Part.UseCheckpoints = false;
            try { q.Invalidate(); q.Rebuild(null); } finally { Parametric3D.Model.Part.UseCheckpoints = prev; }
            Require(PartGeometry(p) == PartGeometry(q), $"edited tree {p.Volume} differs from a fresh build {q.Volume}");
        });

        Check("K05-REF-RESIZE", () =>
        {
            var (p, rf, _) = RoundTree();
            var ex = p.Features.OfType<Parametric3D.Model.Features.ExtrudeFeature>().First();
            double vBefore = p.Volume;
            ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(70); p.MarkDirtyFrom(ex); p.Rebuild(null);
            Log(new { test = "K05-REF-RESIZE", rf.Error, p.Volume });
            Require(rf.Error == null, "round lost its edge after resize: " + rf.Error);
        });

        Check("K05-REF-SPLIT-KEEPS-PIECE", () =>
        {
            var (p, rf, _) = RoundTree();
            // A notch below the middle splits the edge; the referenced point (middle) stays on the upper piece.
            var notch = new CutBox(25, 25, -20, 10, 10, 6);
            p.Features.Remove(notch); // no-op guard
            p.AddFeature(notch); p.MoveFeature(notch, p.Features.IndexOf(rf)); p.Rebuild(null);
            Log(new { test = "K05-REF-SPLIT-KEEPS-PIECE", roundError = rf.Error, notchError = notch.Error, order = string.Join(",", p.Features.Select(f => f.GetType().Name)) });
            Require(notch.Error == null && rf.Error == null, "split edge reference failed: " + rf.Error);
        });

        Check("K05-REF-SPLIT-LOSES-POINT-IS-CONFLICT", () =>
        {
            var (p, rf, _) = RoundTree();
            var notch = new CutBox(25, 25, -3, 10, 10, 6);         // removes the referenced middle
            p.AddFeature(notch); p.MoveFeature(notch, p.Features.IndexOf(rf)); p.Rebuild(null);
            Log(new { test = "K05-REF-SPLIT-CONFLICT", rf.Error });
            Require(rf.Error != null && rf.Error.Contains("conflict"), "ambiguous split was resolved silently");
        });

        Check("K05-REF-EDGE-GONE-IS-CONFLICT", () =>
        {
            var (p, rf, _) = RoundTree();
            var corner = new CutBox(20, 20, -40, 20, 20, 80);       // removes the whole corner edge
            p.AddFeature(corner); p.MoveFeature(corner, p.Features.IndexOf(rf)); p.Rebuild(null);
            Log(new { test = "K05-REF-EDGE-GONE", rf.Error });
            Require(rf.Error != null && rf.Error.Contains("conflict"), "vanished edge silently redirected");
        });

        Check("K05-REF-SUPPRESS-RESTORE", () =>
        {
            var (p, rf, far) = RoundTree();
            string g0 = PartGeometry(p);
            far.Suppressed = true; p.MarkDirtyFrom(far); p.Rebuild(null);
            Require(rf.Error == null, "round failed with suppressed cut: " + rf.Error);
            far.Suppressed = false; p.MarkDirtyFrom(far); p.Rebuild(null);
            Require(rf.Error == null && PartGeometry(p) == g0, "restore did not return the same result");
        });

        Check("K05-REF-REORDER-INDEPENDENT", () =>
        {
            var (p, rf, far) = RoundTree();
            string g0 = PartGeometry(p);
            p.MoveFeature(far, p.Features.IndexOf(rf)); p.Rebuild(null);
            Log(new { test = "K05-REF-REORDER", order = string.Join(",", p.Features.Select(f => f.GetType().Name)), rf.Error });
            Require(rf.Error == null, "reorder broke the reference: " + rf.Error);
            Require(Math.Abs(p.Volume - double.Parse(g0.Split("V=")[1].Split('|')[0])) < 1e-3, "reorder changed the result volume");
        });
    }
}
static partial class Program
{
    static void StepProbe()
    {
        double RT(TSolid b, out string info)
        {
            var w = new List<string>(); var sw = new StringWriter();
            bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "p", w);
            var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), w);
            info = $"ok={ok} bodies={back.Count} faces={back.Sum(x => x.Faces.Count())} warn={string.Join(" | ", w.Take(3))}";
            return back.Sum(s => MassProps.Volume(Tessellator.Tessellate(s)));
        }
        var raw = Box(); double v = RT(raw, out var i1); Log(new { probe = "raw box", v, i1, sameDir = TopologyConventions.SameDirection(raw) });
        var nb = Box(); TopologyConventions.Normalize(nb); v = RT(nb, out var i2); Log(new { probe = "normalized box", v, i2 });
        var cyl = PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10); v = RT(cyl, out var i3); Log(new { probe = "raw cylinder", v, expect = Math.PI * 250, i3 });
        var nc = PrimitiveOp.Cylinder(Vector3d.Zero, 5, 10); TopologyConventions.Normalize(nc); v = RT(nc, out var i4); Log(new { probe = "normalized cylinder", v, i4 });
        foreach (var id in new[] { "S-HOLE", "S-OVERLAP", "S-SPHERE" })
        { var w = new List<string>(); var b = Scenario(id, w)[0]; v = RT(b, out var i5); Log(new { probe = id, v, expect = MassProps.Volume(Tessellator.Tessellate(b)), i5 }); }
    }
}
static partial class Program
{
    static void SplitFaceProbe()
    {
        var w = new List<string>();
        var b = new List<TSolid> { Box() };
        BooleanOp.Subtract(b, Box(25, -5, 10, 10, 50, 20), w);
        var s = b[0];
        foreach (var f in s.Faces) Log(new { face = f.Id, f.Tag, loops = f.Loops.Count, outer = f.Loops.Count(l => l.IsOuter), n = f.Loops.Select(l => l.CoEdges.Count) });
        var a = TopologyConventions.Analyze(s);
        foreach (var i in SolidValidator.Validate(s).Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(3)) Console.WriteLine("SLOTISSUE " + i);
        foreach (var f in s.Faces.Where(f => f.Tag.EndsWith("top"))) Console.WriteLine($"SLOTFACE {f.Id} facets {(f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Facets.Count} display {(f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Display?.Count}");
        Log(new { probe = "slot", conv = s.TopologyConvention, report = a.ToString(), a.Problems, valid = SolidValidator.Validate(s).Summary() });
    }
}
static partial class Program
{
    static void K05History()
    {
        Check("K05-NAMES-SCOPED-BY-FEATURE", () =>
        {
            var p = new Parametric3D.Model.Part("k05n"); Parametric3D.Model.TestGeometry.AddCube(p, 60);
            var cut = p.AddFeature(new CutBox(20, -40, -10, 30, 80, 20));   // tool built by the same extrude op
            p.Rebuild(null);
            var tags = p.Bodies[0].Faces.Select(f => f.Tag).ToList();
            var baseKey = p.Features[0].Key;
            Require(tags.All(t => t.StartsWith("#")), "unscoped face name: " + string.Join(",", tags.Where(t => !t.StartsWith("#"))));
            Require(tags.Any(t => t.StartsWith($"#{baseKey}/")) && tags.Any(t => t.StartsWith($"#{cut.Key}/")), "names not scoped by their features");
            // Tool faces named like base faces before scoping must now differ.
            var bare = tags.GroupBy(EdgeRef.Bare).Where(g => g.Select(x => x).Distinct().Count() > 1).ToList();
            Log(new { test = "K05-NAMES", tags, collisionsAvoided = bare.Count });
            Require(bare.Count > 0, "test does not exercise a former name collision");
        });

        Check("K05-HISTORY-HOLE", () =>
        {
            var p = new Parametric3D.Model.Part("k05h"); Parametric3D.Model.TestGeometry.AddCube(p, 60);
            var hole = p.AddFeature(new CutCyl(0, 0, 5));
            var r = p.Compute(null); p.Apply(r);
            var h = r.History[hole];
            Log(new { test = "K05-HISTORY-HOLE", h.Generated, h.Modified, h.Split, h.Deleted });
            Require(h.Generated.Count >= 1 && h.Generated.All(t => t.StartsWith($"#{hole.Key}/")), "hole wall not generated by the hole");
            Require(h.Modified.Count == 2 && h.Modified.All(t => t.EndsWith(":top") || t.EndsWith(":bottom")) &&
                    h.Deleted.Count == 0 && h.Split.Count == 0, "top and bottom must be modified only");
        });

        Check("K05-HISTORY-DELETE-AND-SPLIT", () =>
        {
            var p = new Parametric3D.Model.Part("k05d"); Parametric3D.Model.TestGeometry.AddCube(p, 60);
            var slice = p.AddFeature(new CutBox(20, -40, -40, 30, 80, 80));   // removes the x=30 side entirely
            var slot = p.AddFeature(new CutBox(-5, -40, 10, 10, 80, 30));     // splits the top face in two
            var r = p.Compute(null); p.Apply(r);
            var hs = r.History[slice]; var ht = r.History[slot];
            Log(new { test = "K05-HISTORY-DELETE-AND-SPLIT", slice = new { hs.Generated, hs.Modified, hs.Deleted }, slot = new { ht.Generated, ht.Split, ht.Modified } });
            Require(hs.Deleted.Count == 1, "the removed side face is not reported deleted");
            Require(ht.Split.Count == 1 && ht.Split[0].EndsWith(":top"), "the top face split is not reported");
        });

        Check("K05-KEYS-PERSIST", () =>
        {
            var p = new Parametric3D.Model.Part("k05k"); Parametric3D.Model.TestGeometry.AddCube(p, 60); p.Rebuild(null);
            var rf0 = new Parametric3D.Model.Features.RoundFeature { Radius = 4 }; rf0.Edges.Add(EdgeRef.Of(CornerEdge(p, 30, 30)));
            p.AddFeature(rf0); p.Rebuild(null);
            Require(rf0.Error == null, rf0.Error ?? "");
            string json = Parametric3D.Model.DocumentFile.Save(p);
            var (q, pars) = Parametric3D.Model.DocumentFile.Load(json);
            Require(q.Features.Select(f => f.Key).SequenceEqual(p.Features.Select(f => f.Key)), "feature keys not persisted");
            q.Rebuild(pars);
            Require(q.Features.All(f => f.Error == null), "reloaded tree failed: " + string.Join(";", q.Features.Select(f => f.Error)));
            Require(Math.Abs(q.Volume - p.Volume) < 1e-6, $"reloaded volume {q.Volume} vs {p.Volume}");
        });

        Check("K05-LEGACY-REFERENCE", () =>
        {
            // A document saved before K05: no keys, references with unscoped face names.
            var (p, rf, _) = RoundTree();
            var e = rf.Edges[0];
            var legacy = new EdgeRef(e.Start, e.End, e.Mid, EdgeRef.Bare(e.FaceA!), EdgeRef.Bare(e.FaceB!),
                                     e.End0 == null ? null : string.Join(";", e.End0.Split(';').Select(EdgeRef.Bare)),
                                     e.End1 == null ? null : string.Join(";", e.End1.Split(';').Select(EdgeRef.Bare)), e.Piece);
            rf.Edges[0] = legacy; p.MarkDirtyFrom(rf); p.Rebuild(null);
            Require(rf.Error == null, "legacy reference no longer resolves: " + rf.Error);
        });
    }
}
