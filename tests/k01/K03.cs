using System.Reflection;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    // ---- features with editable parameters --------------------------------------------------
    internal sealed class CutCyl(double x, double y, double r) : Feature
    {
        public double X = x, Y = y, R = r;
        public override void Execute(BuildContext c) =>
            BooleanOp.Subtract(c.Bodies, PrimitiveOp.Cylinder(new Vector3d(X, Y, -100), R, 300), c.Warnings);
    }
    internal sealed class CutSphere(double x, double y, double z, double r) : Feature
    {
        public double X = x, Y = y, Z = z, R = r;
        public override void Execute(BuildContext c) =>
            BooleanOp.Subtract(c.Bodies, PrimitiveOp.Sphere(new Vector3d(X, Y, Z), R), c.Warnings);
    }
    internal sealed class KeepBox(double x0, double y0, double z0, double s) : Feature
    {
        public double X0 = x0, Y0 = y0, Z0 = z0, S = s;
        public override void Execute(BuildContext c) => BooleanOp.Intersect(c.Bodies, Box(X0, Y0, Z0, S, S, S), c.Warnings);
    }
    internal sealed class AddBlock(double x, double y, double z) : Feature
    {
        public double X = x, Y = y, Z = z;
        public override void Execute(BuildContext c) => BooleanOp.Unite(c.Bodies, Box(X, Y, Z, 20, 20, 20), c.Warnings);
    }
    internal sealed class SecondBody : Feature
    {
        public override void Execute(BuildContext c) => c.Bodies.Add(Box(300, 0, 0, 30, 30, 30));
    }
    /// <summary>Executes a Boolean, then fails: the partial result must not survive the rollback.</summary>
    internal sealed class CutThenFail : Feature
    {
        public override void Execute(BuildContext c)
        {
            BooleanOp.Subtract(c.Bodies, PrimitiveOp.Cylinder(new Vector3d(5, 5, -100), 2, 300), c.Warnings);
            throw new Parametric3D.Core.RebuildException(Parametric3D.Kernel.OperationOutcome.InternalFailure, "deliberate failure after a successful cut");
        }
    }

    /// <summary>Deliberately violates the contract: moves a vertex of the incoming body in place.</summary>
    internal sealed class MutateInPlace : Feature
    {
        public override void Execute(BuildContext c)
        {
            var v = c.Bodies[0].Vertices.First(); v.P += new Vector3d(0.5, 0, 0);
        }
    }

    /// <summary>Поставка 2: правит ТОПОЛОГИЮ опубликованного тела (удаляет грань) — отпечатком прежде ловилось только под флагом.</summary>
    internal sealed class RemoveFaceInPlace : Feature
    {
        public override void Execute(BuildContext c)
        {
            var sh = c.Bodies[0].Shells[0]; sh.Faces.RemoveAt(sh.Faces.Count - 1);
        }
    }

    static IEnumerable<(int After, List<TSolid> Bodies)> Checkpoints(Part p)
    {
        var f = typeof(Part).GetField("_checkpoints", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var c in (System.Collections.IList)f.GetValue(p)!)
        {
            int after = (int)c.GetType().GetField("After")!.GetValue(c)!;
            var state = (BuildContext.Snapshot)c.GetType().GetField("State")!.GetValue(c)!;
            yield return (after, state.Bodies);
        }
    }
    static Dictionary<(int, int), (string A, string D)> Prints(Part p) =>
        Checkpoints(p).SelectMany(c => c.Bodies.Select((b, i) => (Key: (c.After, i), V: (SolidFingerprint.Authoritative(b), SolidFingerprint.Derived(b)))))
                      .ToDictionary(x => x.Key, x => x.V);

    static Part Tree(bool withIntersect)
    {
        var p = new Part("k03");
        TestGeometry.AddCube(p, 60);
        p.AddFeature(new CutCyl(-15, -10, 4));
        p.AddFeature(new CutSphere(10, 10, 30, 8));
        p.AddFeature(new AddBlock(20, 20, 20));
        if (withIntersect) p.AddFeature(new KeepBox(-25, -25, -25, 70));
        p.AddFeature(new CutCyl(15, -15, 3));
        return p;
    }
    static string PartGeometry(Part p) => string.Join("|", p.Bodies.Select(SolidFingerprint.Geometry)) + $"|V={p.Volume:R}";

    static void K03()
    {
        Check("K03-CHECKPOINTS-IMMUTABLE", () =>
        {
            var p = Tree(true); p.Rebuild(null);
            Require(p.Features.All(f => f.Error == null), "tree failed: " + string.Join(";", p.Features.Select(f => f.Error)));
            var before = Prints(p);
            var last = p.Features.OfType<CutCyl>().Last();
            last.R = 5; p.MarkDirtyFrom(last); p.Rebuild(null);
            var after = Prints(p);
            int lastIndex = p.Features.IndexOf(last);
            var changedA = before.Where(kv => kv.Key.Item1 < lastIndex && after.TryGetValue(kv.Key, out var a) && a.A != kv.Value.A).Select(kv => kv.Key).ToList();
            var changedD = before.Where(kv => kv.Key.Item1 < lastIndex && after.TryGetValue(kv.Key, out var a) && a.D != kv.Value.D).Select(kv => kv.Key).ToList();
            Log(new { test = "K03-CHECKPOINTS-IMMUTABLE", checkpoints = before.Count, authoritativeChanged = changedA.Count, derivedChanged = changedD.Count,
                      derivedAt = changedD.Select(k => k.ToString()) });
            Require(changedA.Count == 0, $"authoritative state of {changedA.Count} checkpoint bodies changed");
            Require(changedD.Count == 0, $"derived state of {changedD.Count} checkpoint bodies changed");
        });

        foreach (bool withIntersect in new[] { false, true })
            Check($"K03-INCREMENTAL-EQUALS-FULL intersect={withIntersect}", () =>
            {
                // Edit early, middle and last feature in turn; after each edit compare with a full rebuild.
                var inc = Tree(withIntersect); inc.Rebuild(null);
                var cuts = inc.Features.OfType<CutCyl>().ToList();
                var sph = inc.Features.OfType<CutSphere>().Single();
                var edits = new List<(string, Action<Part>)>
                {
                    ("last", q => { var c = q.Features.OfType<CutCyl>().Last(); c.R = 2.5; q.MarkDirtyFrom(c); }),
                    ("middle", q => { var s = q.Features.OfType<CutSphere>().Single(); s.R = 9; q.MarkDirtyFrom(s); }),
                    ("early", q => { var c = q.Features.OfType<CutCyl>().First(); c.X = -12; q.MarkDirtyFrom(c); }),
                    ("noop-last", q => { var c = q.Features.OfType<CutCyl>().Last(); q.MarkDirtyFrom(c); }),
                };
                var full = Tree(withIntersect);
                foreach (var (name, edit) in edits)
                {
                    edit(inc); var r = inc.Compute(null); inc.Apply(r);
                    edit(full); bool prev = Part.UseCheckpoints; Part.UseCheckpoints = false;
                    try { full.Invalidate(); full.Rebuild(null); } finally { Part.UseCheckpoints = prev; }
                    string gi = PartGeometry(inc), gf = PartGeometry(full);
                    Log(new { test = "K03-INCREMENTAL-EQUALS-FULL", withIntersect, edit = name, executed = r.Executed, startedAt = r.StartedAt,
                              equal = gi == gf, inc = inc.Volume, full = full.Volume });
                    Require(gi == gf, $"{name}: incremental {inc.Volume:R} != full {full.Volume:R}");
                    Require(inc.Features.All(f => f.Error == null), "feature error");
                }
            });

        foreach (string kind in new[] { "intersect", "cut", "unite" })
            Check($"K03-APPEND-AFTER-PUBLISHED {kind}", () =>
            {
                // The final body of a rebuild gets its exact display mesh written in place, and that
                // same body is the last checkpoint. A feature appended afterwards starts from it.
                var inc = Tree(false); inc.Rebuild(null);
                var lastBody = inc.Bodies[0];
                string a0 = SolidFingerprint.Authoritative(lastBody), d0 = SolidFingerprint.Derived(lastBody);
                Feature Make() => kind == "intersect" ? new KeepBox(-25, -25, -25, 70) : kind == "cut" ? new CutCyl(0, 0, 6) : new AddBlock(-40, 0, 0);
                inc.AddFeature(Make()); var r = inc.Compute(null); inc.Apply(r);
                var full = Tree(false); full.AddFeature(Make());
                bool prev = Part.UseCheckpoints; Part.UseCheckpoints = false;
                try { full.Rebuild(null); } finally { Part.UseCheckpoints = prev; }
                Log(new { test = "K03-APPEND-AFTER-PUBLISHED", kind, executed = r.Executed, inc = inc.Volume, full = full.Volume,
                          equal = PartGeometry(inc) == PartGeometry(full),
                          publishedAuthoritativeSame = SolidFingerprint.Authoritative(lastBody) == a0, publishedDerivedSame = SolidFingerprint.Derived(lastBody) == d0 });
                Require(r.Executed == 1, $"executed {r.Executed}");
                Require(PartGeometry(inc) == PartGeometry(full), $"incremental {inc.Volume:R} != full {full.Volume:R}");
                Require(SolidFingerprint.Authoritative(lastBody) == a0, "published body changed");
            });

        Check("K03-UNTOUCHED-BODY-NOT-MUTATED", () =>
        {
            // Two bodies; a Boolean touches only the first. The second and the tool keep their state,
            // including caches (no re-snapping, no tolerance re-measuring of foreign bodies).
            // KRN-03: прежняя сцена — два бруска В ОДНОМ МЕСТЕ, резец сквозь оба — закрепляла старую семантику «режется
            // первое тело». Теперь второе тело L-образное, резец проходит через его вырез: габарит задет, материал — нет.
            var w = new List<string>();
            var a = new List<TSolid> { Box() };
            BooleanOp.Subtract(a, Box(40, 25, -5, 30, 20, 30), w);            // L-образный брусок (вырез угла)
            var b2 = a[0];
            var list = new List<TSolid> { Box(45, 28, -40, 10, 10, 30), b2 }; // первое тело — под вырезом
            string pa = SolidFingerprint.Authoritative(b2), pd = SolidFingerprint.Derived(b2);
            var tool = PrimitiveOp.Cylinder(new Vector3d(50, 32, -45), 3, 80); string pt = SolidFingerprint.Authoritative(tool);
            BooleanOp.Subtract(list, tool, w);
            Require(list.Count == 2 && (AnalyticVolume.Of(list[0]) ?? 0) < 3000 - 1, "the first body was not cut");
            Require(ReferenceEquals(list[1], b2), "second body replaced");
            Require(SolidFingerprint.Authoritative(b2) == pa && SolidFingerprint.Derived(b2) == pd, "untouched body mutated");
            Require(SolidFingerprint.Authoritative(tool) == pt, "tool mutated");
        });

        Check("K03-FAILED-FEATURE-LEAVES-NO-TRACE", () =>
        {
            var p = new Part("k03f"); TestGeometry.AddCube(p, 60);
            p.AddFeature(new CutCyl(-15, -10, 4));
            p.Rebuild(null);
            var cp = Prints(p); string g0 = PartGeometry(p); double v0 = p.Volume;
            var bodies0 = p.Bodies.ToList(); var auth0 = bodies0.Select(SolidFingerprint.Authoritative).ToList();
            p.AddFeature(new CutThenFail());
            p.AddFeature(new CutCyl(15, 15, 3));
            p.Rebuild(null);
            Require(p.Features.OfType<CutThenFail>().Single().Error != null, "failure not reported");
            Require(bodies0.Select(SolidFingerprint.Authoritative).SequenceEqual(auth0), "previously published bodies mutated");
            var cp2 = Prints(p);
            Require(cp.All(kv => cp2.TryGetValue(kv.Key, out var v) && v.A == kv.Value.A), "earlier checkpoint changed");
            var reference = new Part("k03ref"); TestGeometry.AddCube(reference, 60);
            reference.AddFeature(new CutCyl(-15, -10, 4)); reference.AddFeature(new CutCyl(15, 15, 3)); reference.Rebuild(null);
            Require(PartGeometry(p) == PartGeometry(reference), "result after failed feature differs from tree without it");
        });

        foreach (var stage in new[] { "boolean.input.face", "boolean.exact", "boolean.topology", "mesh.face" })
            Check($"K03-FAULT-PART {stage}", () =>
            {
                using var __sph = SpheresPolyhedral();   // дерево K03 написано на многогранный путь шара (этапы boolean.*)
                var p = Tree(false); p.Rebuild(null);
                string g0 = PartGeometry(p); var cp0 = Prints(p); var published = p.Bodies.ToList();
                var auth0 = published.Select(SolidFingerprint.Authoritative).ToList();
                var mesh0 = p.Mesh; double vol0 = p.Volume; int ver0 = p.Version;
                var last = p.Features.OfType<CutCyl>().Last(); last.R = 4; p.MarkDirtyFrom(last);
                Part.BuildResult r;
                using (FaultInjection.Arm(stage)) r = p.Compute(null);
                // The computed (possibly failed) result is not yet applied: published state intact.
                Require(ReferenceEquals(p.Mesh, mesh0) && p.Volume == vol0 && p.Bodies.SequenceEqual(published), "compute changed published part state");
                Require(published.Select(SolidFingerprint.Authoritative).SequenceEqual(auth0), "published bodies mutated by failed compute");
                var cp1 = Prints(p);
                Require(cp0.Where(kv => kv.Key.Item1 < p.Features.IndexOf(last)).All(kv => cp1.TryGetValue(kv.Key, out var v) && v.A == kv.Value.A),
                        "checkpoints before the edited feature changed");
                p.Apply(r);
                bool failed = r.Errors.Values.Any(e => e != null) || r.IntegrityError != null;
                Log(new { test = "K03-FAULT-PART", stage, failed, errors = r.Errors.Values.Where(e => e != null), r.IntegrityError });
                Require(failed, "injected fault produced no visible failure");
                // Recovery: without the fault the next rebuild equals a fresh full rebuild.
                p.MarkDirtyFrom(last); p.Rebuild(null);
                var fresh = Tree(false); fresh.Features.OfType<CutCyl>().Last().R = 4; fresh.Rebuild(null);
                Require(PartGeometry(p) == PartGeometry(fresh), "state after recovery differs from a fresh rebuild");
            });

        Check("K03-GUARD-DETECTS-IN-PLACE-MUTATION", () =>
        {
            // Поставка 2: правка формы опубликованного тела на месте ЗАПРЕЩЕНА всегда (заморозка), а не
            // обнаруживается отпечатком под флагом. Тело не портится, сохранённые состояния остаются годными.
            var reference = new Part("k03ref"); TestGeometry.AddCube(reference, 60);
            reference.AddFeature(new CutCyl(-15, -10, 4)); reference.Rebuild(null);
            bool prev = Part.VerifyPublishedBodies;
            try
            {
                foreach (bool verify in new[] { false, true })
                {
                    Part.VerifyPublishedBodies = verify;
                    var p = new Part("k03g"); TestGeometry.AddCube(p, 60);
                    p.AddFeature(new CutCyl(-15, -10, 4));
                    Feature bad = verify ? p.AddFeature(new RemoveFaceInPlace()) : p.AddFeature(new MutateInPlace());
                    p.Rebuild(null);
                    Log(new { test = "K03-GUARD", verify, error = bad.Error, p.Integrity, volume = p.Volume, reference = reference.Volume });
                    Require(bad.Error != null && bad.Error.Contains("immutable") && bad.Error.Contains("in place"),
                        $"in-place mutation not refused (verify={verify}): {bad.Error}");
                    Require(p.Integrity == null, "integrity damaged: " + p.Integrity);
                    Require(Math.Abs(p.Volume - reference.Volume) < 1e-9, $"published body changed: {p.Volume} vs {reference.Volume}");
                    Require(Checkpoints(p).Any(), "checkpoints discarded although nothing was damaged");
                    Require(p.Bodies.All(b => b.IsPublished), "result bodies not published");
                }
            }
            finally { Part.VerifyPublishedBodies = prev; }
        });

        Check("K03-DIAG-TREE-INTEGRITY", () =>
        {
            foreach (bool wi in new[] { false, true })
            {
                var p = Tree(wi); p.Rebuild(null);
                foreach (var b in p.Bodies)
                {
                    var rep = SolidValidator.Validate(b, null, new ValidationOptions { CheckSelfIntersections = true });
                    Log(new { test = "K03-DIAG-TREE-INTEGRITY", withIntersect = wi, verdict = rep.Summary(), m = rep.Metrics.Where(k => k.Key.StartsWith("mesh.") || k.Key.StartsWith("geometry")).ToDictionary(k => k.Key, k => k.Value),
                              issues = rep.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(6).Select(i => i.ToString()) });
                }
            }
        });

        Check("K03-INTERSECT-EXACT-VALID", () =>
        {
            // Known Boolean defect (A06 and later): Intersect = A − (A − B) leaves 6 non-manifold
            // exact edges where coincident cylinder walls meet. Until fixed, the defect must be
            // reported by Part.Integrity, never accepted silently.
            using var __sph = SpheresPolyhedral();   // проверяется точная многогранная форма (её у аналитического результата нет)
            var p = Tree(true); p.Rebuild(null);
            var ex = SolidValidator.ValidateExact(p.Bodies[0].Exact!.Solid);
            Log(new { test = "KNOWN-INTERSECT-DEFECT", exactNonManifold = ex.Metrics["exact.nonManifoldEdges"], p.Integrity });
            Log(new { test = "INTERSECT-AFTER-FIX", valid = ex.IsValid });
            Require(ex.IsValid && p.Integrity == null, "intersect result still invalid: " + ex.Summary() + " / " + p.Integrity);
        });

        Check("K03-INTERSECT-INDEPENDENT-VOLUME", () =>
        {
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (его этапы, формы, тождества)
            // vol(A ∩ B) = vol(A) − vol(A − B), each side by the exact rational volume.
            var w = new List<string>();
            foreach (var (name, B) in new (string, TSolid)[] {
                ("box", Box(20, 10, 5, 70, 20, 30)), ("sphere", PrimitiveOp.Sphere(new Vector3d(55, 35, 15), 12)),
                ("cyl", PrimitiveOp.Cylinder(new Vector3d(10, 10, -5), 9, 40)), ("disjoint", Box(500, 0, 0, 5, 5, 5)) })
            {
                var A = Scenario("S-HOLES4", w)[0];
                var i = new List<TSolid> { A }; BooleanOp.Intersect(i, B, w);
                var d = new List<TSolid> { A }; BooleanOp.Subtract(d, B, w);
                double va = A.Exact!.Solid.VolumeMm3().ToDouble();
                double vd = d.Count == 0 ? 0 : d[0].Exact!.Solid.VolumeMm3().ToDouble();
                double vi = i.Count == 0 ? 0 : i[0].Exact!.Solid.VolumeMm3().ToDouble();
                var rep = i.Count == 0 ? null : SolidValidator.Validate(i[0], null, new ValidationOptions { CheckSelfIntersections = true });
                var repD = d.Count == 0 ? null : SolidValidator.Validate(d[0]);
                string dbl = "";
                if (i.Count > 0) {
                    var E = new Dictionary<(int,int),int>(); var ids = new Dictionary<(double,double,double),int>();
                    int K(Vector3d q){ var k=(q.X,q.Y,q.Z); if(!ids.TryGetValue(k,out int n)){n=ids.Count;ids[k]=n;} return n; }
                    foreach (var f in i[0].Faces) if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs)
                        foreach (var (a,b,c) in fs.Display ?? fs.Facets.ReadView) { int[] t={K(a),K(b),K(c)}; if(t[0]==t[1]||t[1]==t[2]||t[0]==t[2]) continue;
                            for(int z=0;z<3;z++){int u=t[z],v=t[(z+1)%3]; var key=u<v?(u,v):(v,u); E[key]=E.GetValueOrDefault(key)+1;} }
                    dbl = $"double display: open {E.Values.Count(x=>x==1)} non {E.Values.Count(x=>x>2)}";
                }
                Log(new { test = "K03-INTERSECT-INDEPENDENT-VOLUME", subtractVerdict = repD?.Summary(), dbl, name, vi, expected = va - vd, valid = rep?.IsValid, verdict = rep?.Summary() });
                Require(Math.Abs(vi - (va - vd)) <= 1e-6 * va, $"{name}: {vi} vs {va - vd}");
                // Exact and B-rep must be valid. The float mesh of the sphere case has the known
                // double→float defect (K10): the same defect appears for the subtraction.
                Require(rep == null || (rep.IsValidFor(Representation.Exact) && rep.IsValidFor(Representation.BRep)), $"{name}: {rep}");
                Require(name != "disjoint" || i.Count == 0, "disjoint intersect must be empty");
            }
        });

        Check("K03-GUARD-CLEAN-TREE", () =>
        {
            bool prev = Part.VerifyPublishedBodies; Part.VerifyPublishedBodies = true;
            try
            {
                var p = Tree(false); p.Rebuild(null);
                Require(p.Features.All(f => f.Error == null) && p.Integrity == null, "guard rejected a clean tree: " +
                        string.Join(";", p.Features.Select(f => f.Error)) + p.Integrity);
                var c = p.Features.OfType<CutSphere>().Single(); c.R = 7; p.MarkDirtyFrom(c); p.Rebuild(null);
                Require(p.Features.All(f => f.Error == null), "guard rejected an incremental rebuild");
            }
            finally { Part.VerifyPublishedBodies = prev; }
        });

        Check("K03-STALE-RESULT-REJECTED", () =>
        {
            var p = Tree(false); p.Rebuild(null);
            var mesh0 = p.Mesh; var bodies0 = p.Bodies.ToList();
            var c = p.Features.OfType<CutCyl>().Last(); c.R = 2; p.MarkDirtyFrom(c);
            var r = p.Compute(null);
            p.MarkDirtyFrom(c);                         // the document moved on while computing
            Require(!p.Apply(r), "stale result applied");
            Require(ReferenceEquals(p.Mesh, mesh0) && p.Bodies.SequenceEqual(bodies0), "stale result changed the part");
        });

        Check("K03-EDGE-CACHE-FOLLOWS-CURVE", () =>
        {
            var cyl = PrimitiveOp.Cylinder(Vector3d.Zero, 10, 20);
            var e = cyl.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3);
            var first = ExactFaceMesher.EdgePoints(e).ToList();
            var arc = (Parametric3D.Kernel.Curves.ArcCurve3)e.Curve;
            e.Curve = new Parametric3D.Kernel.Curves.ArcCurve3(arc.Frame, 12, arc.A0, arc.A1);   // different radius
            var second = ExactFaceMesher.EdgePoints(e);
            double r2 = second.Skip(1).Take(second.Count - 2).Select(q => (q - arc.Frame.Origin).Length).DefaultIfEmpty(12).Max();
            Require(Math.Abs(r2 - 12) < 1e-6 || second.Count == 2, $"edge polyline still from the old curve (radius {r2:0.###})");
        });

        Check("K03-TRANSFORM-COPY-INDEPENDENT", () =>
        {
            var w = new List<string>();
            var a = new List<TSolid> { Box() };
            BooleanOp.Subtract(a, PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), w);
            string pa = SolidFingerprint.Authoritative(a[0]);
            var moved = TransformOp.Copy(a[0], TransformOp.Move(new Vector3d(100, 0, 0)));
            var list = new List<TSolid> { moved };
            BooleanOp.Subtract(list, PrimitiveOp.Cylinder(new Vector3d(110, 10, -2), 2, 24), w);
            Require(SolidFingerprint.Authoritative(a[0]) == pa, "transforming and cutting a copy changed the original");
        });
    }
}
