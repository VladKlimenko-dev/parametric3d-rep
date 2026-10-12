using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    static Part TreeVariant(int k)
    {
        var p = new Part("k04-" + k);
        TestGeometry.AddCube(p, 60);
        p.AddFeature(new CutCyl(-15 + k, -10, 3 + 0.25 * k));
        p.AddFeature(new CutSphere(10, 10 - k, 30, 6 + 0.5 * (k % 3)));
        if (k % 2 == 0) p.AddFeature(new AddBlock(20, 20, 20));
        if (k % 3 == 0) p.AddFeature(new KeepBox(-25, -25, -25, 70));
        p.AddFeature(new CutCyl(15, -15 + k, 2.5));
        return p;
    }
    // Chord alone does not change these models (the 7.5° angular step dominates at their radii),
    // so the variants differ by the angular step.
    // g.04: с 2026-10-10 геометрию операций задаёт гранение CAD (ToleranceContext.FacetChord/FacetAngle), а не хорда
    // отображения; проверки K04 (контекст соблюдается) варьируют оба.
    static KernelSettings S(double chord, int threads = 1) => KernelSettings.Capture() with
        { Chord = chord, ChordAngle = (chord > 0.03 ? 12.0 : 7.5) * Math.PI / 180, BooleanThreads = threads, MeshThreads = threads,
          Tolerance = ToleranceContext.Default with { FacetChord = chord, FacetAngle = (chord > 0.03 ? 12.0 : 7.5) * Math.PI / 180 } };

    static string BuildIn(Part p, KernelSettings s, CancellationToken token = default)
    {
        using (new KernelContext(s).Enter())
        {
            var r = p.Compute(null, null, token);
            p.Apply(r);
        }
        return PartGeometry(p) + "|err=" + string.Join(",", p.Features.Select(f => f.Error ?? "")) + "|int=" + p.Integrity;
    }

    static void K04()
    {
        var globals0 = KernelSettings.Capture();

        Check("K04-DIAG-CHORD", () =>
        {
            using var __sph = SpheresPolyhedral();   // диагностика точной многогранной формы выреза шаром
            foreach (double ch in new[] { 0.02, 0.05, 0.2 })
            {
                var p = TreeVariant(0); using (new KernelContext(S(ch)).Enter()) { p.Apply(p.Compute(null)); }
                var w = new List<string>(); double vs;
                using (new KernelContext(S(ch)).Enter()) { var b = new List<TSolid> { Box() }; BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), w); vs = b[0].Exact!.Solid.VolumeMm3().ToDouble(); }
                Log(new { test = "K04-DIAG-CHORD", ch, partVolume = p.Volume, sphereCutExact = vs, faces = p.Bodies[0].Faces.Count() });
            }
        });

        Check("K04-DIAG-ID-GAPS", () =>
        {
            var res = new List<string>();
            foreach (int gap in new[] { 0, 1, 7, 1000, 12345 })
            {
                for (int i = 0; i < gap; i++) Parametric3D.Core.Ids.New();
                res.Add(BuildIn(TreeVariant(0), S(0.02)));
            }
            Log(new { test = "K04-DIAG-ID-GAPS", distinct = res.Distinct().Count(), res = res.Select(r => r.Length > 60 ? r[^60..] : r) });
        });

        Check("K04-GLOBAL-SETTER-REFUSED-IN-CONTEXT", () =>
        {
            using (new KernelContext().Enter())
            {
                bool threw = false;
                try { BooleanOp.DeferMeshing = true; } catch (InvalidOperationException) { threw = true; }
                Require(threw, "global setter accepted inside a context");
                threw = false;
                try { Parametric3D.Core.Tol.Chord = 0.1; } catch (InvalidOperationException) { threw = true; }
                Require(threw, "Tol.Chord setter accepted inside a context");
            }
            Require(KernelSettings.Capture() == globals0, "globals changed");
        });

        Check("K04-OPTIONS-SCOPE-DOES-NOT-LEAK", () =>
        {
            // One logical flow opens an options scope; another flow running at the same time sees defaults.
            var inside = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
            bool seenInside = false, seenOutside = true;
            var t = Task.Run(() =>
            {
                using (new KernelOptions { DeferMeshing = true, LocalizeBooleans = false }.Apply())
                {
                    seenInside = BooleanOp.DeferMeshing && !BooleanOp.LocalizeBooleans;
                    inside.Set(); release.Wait();
                }
            });
            inside.Wait();
            seenOutside = BooleanOp.DeferMeshing == globals0.DeferMeshing && BooleanOp.LocalizeBooleans == globals0.LocalizeBooleans;
            release.Set(); t.Wait();
            Require(seenInside && seenOutside, $"inside {seenInside}, outside {seenOutside}");
            Require(KernelSettings.Capture() == globals0, "globals changed after scope");
        });

        // Sequential references, then the same work in parallel with different tolerances.
        var cases = Enumerable.Range(0, 6).Select(k => (k, chord: k % 2 == 0 ? 0.02 : 0.05)).ToList();
        var reference = new Dictionary<int, string>();
        Check("K04-SEQUENTIAL-REFERENCE", () =>
        {
            foreach (var (k, chord) in cases) reference[k] = BuildIn(TreeVariant(k), S(chord));
            Require(reference.Values.Distinct().Count() == cases.Count, "variants should differ");
            // Tolerance really matters: the same tree at the other chord differs.
            Require(BuildIn(TreeVariant(0), S(0.05)) != reference[0], "chord had no effect");
        });

        Check("K04-PARALLEL-EQUALS-SEQUENTIAL", () =>
        {
            for (int round = 0; round < 3; round++)
            {
                var got = new ConcurrentDictionary<int, string>();
                // Settings are captured before the flipper starts: Capture() outside a context reads globals.
                var settings = cases.ToDictionary(c => c.k, c => S(c.chord));
                // Meanwhile another thread keeps flipping global defaults; contexts must not see it.
                using var stop = new CancellationTokenSource();
                var flipper = Task.Run(() =>
                {
                    bool v = false;
                    while (!stop.IsCancellationRequested) { BooleanOp.LocalizeBooleans = v; BooleanOp.DeferMeshing = !v; v = !v; Thread.Yield(); }
                });
                Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                                 c => got[c.k] = BuildIn(TreeVariant(c.k), settings[c.k]));
                stop.Cancel(); flipper.Wait();
                BooleanOp.LocalizeBooleans = globals0.LocalizeBooleans; BooleanOp.DeferMeshing = globals0.DeferMeshing;
                var bad = cases.Where(c => got[c.k] != reference[c.k]).Select(c => c.k).ToList();
                foreach (var k in bad.Take(2)) Log(new { test = "K04-PARALLEL-DIFF", k, got = got[k][^Math.Min(400, got[k].Length)..], want = reference[k][^Math.Min(400, reference[k].Length)..] });
                Log(new { test = "K04-PARALLEL-EQUALS-SEQUENTIAL", round, mismatched = bad });
                Require(bad.Count == 0, $"round {round}: variants {string.Join(",", bad)} differ from sequential");
            }
        });

        Check("K04-THREADS-INSIDE-COMPUTE-DETERMINISTIC", () =>
        {
            foreach (int k in new[] { 0, 3 })
            {
                string one = BuildIn(TreeVariant(k), S(0.02, 1));
                string four = BuildIn(TreeVariant(k), S(0.02, 4));
                Require(one == four, $"variant {k}: 4 threads differ from 1 thread");
            }
        });

        foreach (string stage in new[] { "feature", "boolean.exact", "boolean.input.face", "boolean.topology", "mesh.face", "final mesh" })
            foreach (int skip in stage == "final mesh" ? new[] { 0 } : new[] { 0, 2 })
                Check($"K04-CANCEL {stage} skip={skip}", () =>
                {
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (его этапы, формы, тождества)
                    var globalsHere = KernelSettings.Capture();          // глобальные — после закрепления пути
                    var p = TreeVariant(1); BuildIn(p, S(0.02));
                    string before = PartGeometry(p); var mesh0 = p.Mesh; var bodies0 = p.Bodies.ToList();
                    var auth0 = bodies0.Select(SolidFingerprint.Authoritative).ToList();
                    var cp0 = Prints(p);
                    var last = p.Features.OfType<CutCyl>().First(); last.R = 4.5; p.MarkDirtyFrom(last);

                    using var cts = new CancellationTokenSource();
                    int hits = 0; bool cancelled = false;
                    var ctx = new KernelContext(S(0.02), cancellation: cts.Token)
                        .WithStageHook(s => { if (s == stage && hits++ == skip) cts.Cancel(); });
                    try { using (ctx.Enter()) p.Compute(null, null, cts.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Require(cancelled, $"not cancelled (stage hits {hits})");
                    Require(ReferenceEquals(p.Mesh, mesh0) && p.Bodies.SequenceEqual(bodies0) && PartGeometry(p) == before, "cancel changed the part");
                    Require(bodies0.Select(SolidFingerprint.Authoritative).SequenceEqual(auth0), "cancel mutated published bodies");
                    Require(KernelContext.Current == null && KernelSettings.Capture() == globalsHere, "cancel left a context or changed globals");
                    var cp1 = Prints(p); int edited = p.Features.IndexOf(last);
                    Require(cp0.Where(kv => kv.Key.Item1 < edited).All(kv => cp1.TryGetValue(kv.Key, out var v) && v.A == kv.Value.A),
                            "checkpoint before the edited feature changed or vanished");
                    // Re-run: equals a fresh build of the edited tree.
                    string again = BuildIn(p, S(0.02));
                    var fresh = TreeVariant(1); fresh.Features.OfType<CutCyl>().First().R = 4.5;
                    Require(again == BuildIn(fresh, S(0.02)), "rerun after cancel differs from fresh build");
                });

        Check("K04-DEADLINE", () =>
        {
            var p = TreeVariant(2);
            bool limited = false;
            p.Limits = new KernelLimits { Deadline = DateTime.UtcNow.AddMilliseconds(-1) };
            try { p.Compute(null); } catch (KernelLimitException) { limited = true; }
            Require(limited, "deadline not enforced");
            p.Limits = KernelLimits.None;
            Require(BuildIn(p, S(0.02)) == BuildIn(TreeVariant(2), S(0.02)), "rebuild after deadline differs");
        });

        Check("K04-POLYGON-LIMIT", () =>
        {
            using var __sph = SpheresPolyhedral();   // лимит многоугольников точной многогранной формы
            var p = TreeVariant(0);
            p.Limits = new KernelLimits { MaxExactPolygons = 50 };
            bool limited = false;
            try { p.Compute(null); } catch (KernelLimitException ex) { limited = true; Log(new { test = "K04-POLYGON-LIMIT", ex.What }); }
            Require(limited, "polygon limit not enforced");
        });

        Check("K04-GLOBALS-RESTORED", () => Require(KernelSettings.Capture() == globals0, "global defaults changed by the K04 checks"));
    }
}
