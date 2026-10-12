using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

static partial class Program
{
    internal sealed class CutCylP : Feature
    {
        public ValueRef X = new(15), Y = new(-15), R = new(3);
        public override void Execute(BuildContext c) =>
            BooleanOp.Subtract(c.Bodies, PrimitiveOp.Cylinder(new Vector3d(X.Resolve(c.Parameters), Y.Resolve(c.Parameters), -100), R.Resolve(c.Parameters), 300), c.Warnings);
    }

    static (Part P, ParameterTable T, ExtrudeFeature Ex, CutCylP Early, CutCylP Last) ParamTree()
    {
        var t = new ParameterTable();
        t.Add("H", 60); t.Add("A", 3); t.Add("Unused", 1); t.AddFormula("B", "A*1.5"); t.Add("X0", -15);
        var p = new Part("k06");
        var ex = TestGeometry.AddCube(p, 60); ex.Side1.Distance = new ValueRef(60) { Formula = "H" };
        var early = p.AddFeature(new CutCylP { X = new ValueRef(-15) { Formula = "X0" }, Y = new(-10), R = new(4) });
        p.AddFeature(new CutSphere(10, 10, 30, 8));
        var last = p.AddFeature(new CutCylP { R = new ValueRef(3) { Formula = "B" } });
        p.Rebuild(t);
        return (p, t, ex, early, last);
    }

    static int Booleans(Part p, ParameterTable? t, out Part.BuildResult r)
    {
        int n = 0;
        using (new KernelContext().WithStageHook(s => { if (s == "boolean.exact") n++; }).Enter())
            r = p.Compute(t);
        p.Apply(r);
        return n;
    }

    static string FullBuild(Func<(Part P, ParameterTable T, ExtrudeFeature Ex, CutCylP Early, CutCylP Last)> make, Action<ParameterTable> edit)
    {
        var x = make(); edit(x.T); x.T.Recalculate();
        bool prev = Part.UseCheckpoints; Part.UseCheckpoints = false;
        try { x.P.Invalidate(); x.P.Rebuild(x.T); } finally { Part.UseCheckpoints = prev; }
        return PartGeometry(x.P);
    }

    static void K06()
    {
        Check("K06-NOOP-REBUILD", () =>
        {
            var (p, t, _, _, _) = ParamTree();
            string g = PartGeometry(p);
            p.Invalidate(); p.Rebuild(t);                       // full, establishes the baseline
            var sw = Stopwatch.StartNew();
            int b = Booleans(p, t, out var r);
            sw.Stop();
            Log(new { test = "K06-NOOP", r.Executed, booleans = b, ms = sw.Elapsed.TotalMilliseconds });
            Require(r.Executed == 0 && b == 0, $"no-op executed {r.Executed} features, {b} booleans");
            Require(PartGeometry(p) == g, "no-op changed the result");
        });

        Check("K06-APPEARANCE-ONLY", () =>
        {
            var (p, t, _, _, _) = ParamTree();
            p.AppearanceChanged();
            Require(p.IsDirty, "appearance change must refresh the part");
            int b = Booleans(p, t, out var r);
            Require(r.Executed == 0 && b == 0, $"appearance change executed {r.Executed} features");
        });

        foreach (var (name, param, value, expectFrom) in new (string, string, double, int)[]
        {
            ("last", "A", 2.5, 3), ("chain-formula", "A", 3.5, 3), ("early", "X0", -12, 1), ("base", "H", 70, 0), ("unused", "Unused", 5, -1),
        })
            Check($"K06-PARAM {name}", () =>
            {
                var (p, t, _, _, _) = ParamTree();
                t.Find(param)!.Number = value; t.Recalculate(); p.ParametersChanged();
                Booleans(p, t, out var r);
                string full = FullBuild(ParamTree, tt => tt.Find(param)!.Number = value);
                Log(new { test = "K06-PARAM", name, r.Executed, r.StartedAt, equal = PartGeometry(p) == full });
                if (expectFrom < 0) Require(r.Executed == 0, $"unused parameter executed {r.Executed}");
                else Require(r.StartedAt == expectFrom, $"started at {r.StartedAt}, expected {expectFrom}");
                Require(PartGeometry(p) == full, "incremental differs from full rebuild");
            });

        Check("K06-SETTINGS-CHANGE-REBUILDS", () =>
        {
            var (p, t, _, _, _) = ParamTree();
            Part.BuildResult r;
            using (new KernelContext(KernelSettings.Capture() with { ChordAngle = 12 * Math.PI / 180 }).Enter()) r = p.Compute(t);
            Require(r.StartedAt == 0 && r.Executed == p.Features.Count, $"settings change executed {r.Executed} from {r.StartedAt}");
        });

        Check("K06-SKETCH-EDITS", () =>
        {
            var (p, t, ex, _, _) = ParamTree();
            var loose = new Sketch("loose", Parametric3D.Kernel.Frame3.XY); p.Add(loose);
            p.Rebuild(t);
            loose.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(5, 5)));
            Booleans(p, t, out var r1);
            Require(r1.Executed == 0, $"unused sketch edit executed {r1.Executed}");
            var used = p.Sketches.First(s => s.Id == ex.SketchId);
            used.AddEntity(new SkLine(new SkPoint(100, 100), new SkPoint(105, 105)));   // stray line, profile unchanged
            Booleans(p, t, out var r2);
            Require(r2.StartedAt == 0, $"edit of the base sketch must rebuild from the extrude (started {r2.StartedAt})");
        });

        Check("K06-RANDOM-EDIT-SERIES", () =>
        {
            var rnd = new Random(606);
            var (p, t, _, early, last) = ParamTree();
            var sph = p.Features.OfType<CutSphere>().Single();
            for (int step = 0; step < 14; step++)
            {
                int kind = rnd.Next(5);
                double v = Math.Round(rnd.NextDouble(), 3);
                Action<ParameterTable> paramEdit = _ => { };
                switch (kind)
                {
                    case 0: paramEdit = tt => tt.Find("A")!.Number = 2 + v * 2; break;
                    case 1: paramEdit = tt => tt.Find("X0")!.Number = -18 + v * 6; break;
                    case 2: sph.R = 6 + v * 3; p.MarkDirtyFrom(sph); break;
                    case 3: sph.Suppressed = !sph.Suppressed; break;
                    case 4: p.AppearanceChanged(); break;
                }
                paramEdit(t); t.Recalculate(); if (kind <= 1) p.ParametersChanged();
                Booleans(p, t, out var r);
                // Full reference with the same state.
                var (q, tq, _, _, _) = ParamTree();
                foreach (var prm in t.All.Where(x => x.Kind != ParamKind.Formula)) tq.Find(prm.Name)!.Number = prm.Number;
                tq.Recalculate();
                var sq = q.Features.OfType<CutSphere>().Single(); sq.R = sph.R; sq.Suppressed = sph.Suppressed;
                bool prev = Part.UseCheckpoints; Part.UseCheckpoints = false;
                try { q.Invalidate(); q.Rebuild(tq); } finally { Part.UseCheckpoints = prev; }
                Log(new { test = "K06-SERIES", step, kind, r.Executed, r.StartedAt });
                Require(PartGeometry(p) == PartGeometry(q), $"step {step} (edit {kind}): incremental differs from full");
            }
        });
    }
}
static partial class Program
{
    static void K06Bench()
    {
        double T(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
        var rows = new List<object>();
        for (int round = 0; round < 6; round++)
        {
            var (p, t, _, _, _) = ParamTree();
            double cold = T(() => { p.Invalidate(); p.Rebuild(t); });
            double noopNew = T(() => { p.AppearanceChanged(); p.Rebuild(t); });
            double noopOld = T(() => { p.Invalidate(); p.Rebuild(t); });          // previous behavior: full recompute
            t.Find("A")!.Number = 2.5 + 0.1 * round; t.Recalculate();
            double lastNew = T(() => { p.ParametersChanged(); p.Rebuild(t); });
            t.Find("A")!.Number = 3; t.Recalculate();
            double lastOld = T(() => { p.Invalidate(); p.Rebuild(t); });          // previous: InvalidateAll
            t.Find("Unused")!.Number = round; t.Recalculate();
            double unusedNew = T(() => { p.ParametersChanged(); p.Rebuild(t); });
            if (round > 0) rows.Add(new { cold, noopNew, noopOld, lastNew, lastOld, unusedNew });
            Log(new { k06bench = round, cold, noopNew, noopOld, lastNew, lastOld, unusedNew });
        }
    }
}
static partial class Program
{
    static void K06Datums()
    {
        Check("K06-DATUM-CHANGES", () =>
        {
            var p = new Part("datum");
            TestGeometry.AddCube(p, 60);
            var used = new DatumPlane("Used", new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY)); p.Add(used);
            var free = new DatumPlane("Free", new Frame3(new Vector3d(0, 0, 50), Vector3d.UnitX, Vector3d.UnitY)); p.Add(free);
            p.AddFeature(new CutCyl(-15, -15, 4));
            var sk = new Sketch("onUsed", used.Frame); p.Add(sk); sk.SetBasePlane(used.Frame, used.Id, "Used");
            var a = new SkPoint(5, 5); var b = new SkPoint(15, 5); var c = new SkPoint(15, 15); var d = new SkPoint(5, 15);
            foreach (var e in new SkEntity[] { new SkLine(a, b), new SkLine(b, c), new SkLine(c, d), new SkLine(d, a) }) sk.AddEntity(e);
            var pocket = new ExtrudeFeature { SketchId = sk.Id, Cut = true }; pocket.Side1.Distance = new ValueRef(10); pocket.Side1.Flip = true;
            p.AddFeature(pocket);
            p.AddFeature(new CutCyl(15, -15, 3));
            p.Rebuild(null);
            Require(p.Features.All(f => f.Error == null), string.Join(";", p.Features.Select(f => f.Error)));
            // Unused datum: nothing to rebuild.
            free.Offset = 3; p.NotifyChanged(free);
            var r1 = p.Compute(null); p.Apply(r1);
            // Used datum: rebuild from the pocket (index 2: cube, cut, pocket, cut).
            used.Offset = 1; p.NotifyChanged(used);
            var r2 = p.Compute(null); p.Apply(r2);
            Log(new { test = "K06-DATUM", freeExecuted = r1.Executed, usedStartedAt = r2.StartedAt, pocketIndex = p.Features.IndexOf(pocket) });
            Require(r1.Executed == 0, $"unused datum change executed {r1.Executed} features");
            Require(r2.StartedAt == p.Features.IndexOf(pocket), $"used datum change started at {r2.StartedAt}, expected {p.Features.IndexOf(pocket)}");
        });
    }
}
