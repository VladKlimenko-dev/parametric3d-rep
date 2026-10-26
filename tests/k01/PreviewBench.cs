using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

/// <summary>
/// k113: ЗАМЕР ПРЕДПРОСМОТРА КАЖДОЙ ОПЕРАЦИИ (цель пользователя 21:47 — ≤ 20 мс на предпросмотр любой операции).
/// Деталь — пластина K08 (20 операций: отверстия, карманы, бобышки, скругления); последней ставится операция замера;
/// предпросмотр — как в окне (Preview, LivePreview, хорда ×4): MarkDirtyFrom(операция) → Compute → Apply, медиана из 7.
/// Разбивка: время самой операции (Execute), остальное в Compute (сетка показа, масса, история), Apply.
/// Запуск: K01Checks previewbench [фильтр]; P3D_BENCHPROF=1 — этапы ядра на каждый случай.
/// </summary>
static partial class Program
{
    static Part? CubeSwap;
    static void PreviewBench(string? filter)
    {
        bool prof = Environment.GetEnvironmentVariable("P3D_BENCHPROF") == "1";
        int reps = int.TryParse(Environment.GetEnvironmentVariable("P3D_BENCHREPS"), out var rr) ? rr : 7;
        static double Med(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        bool exact = Environment.GetEnvironmentVariable("P3D_BENCHEXACT") == "1";
        KernelSettings PreviewSettings() { var ks = KernelSettings.Capture(); return exact ? ks : ks with { Preview = true, LivePreview = true, Chord = ks.Chord * 4 }; }

        Part Base() { var p = RealisticPart(3, 20, out _); p.Rebuild(null); return p; }
        static TEdge EdgeWhere(Part p, Func<TEdge, bool> f) => p.Bodies.SelectMany(b => b.Edges).First(f);
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        static bool Vertical(TEdge e, double x, double y) => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Near(e.V0.P.X, x) && Near(e.V1.P.X, x) && Near(e.V0.P.Y, y) && Near(e.V1.P.Y, y);
        static bool AlongY(TEdge e, double x, double z) => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Near(e.V0.P.X, x) && Near(e.V1.P.X, x) && Near(e.V0.P.Z, z) && Near(e.V1.P.Z, z);

        // случаи: имя, как добавить операцию, как изменить её (шаг предпросмотра)
        var cases = new List<(string Name, Func<Part, Feature> Add, Action<Feature, int> Change)>
        {
            ("extrude-boss", p => { var s = CircleSketch(p, 10, 30, 20, 6); var f = new ExtrudeFeature("BBoss") { SketchId = s.Id }; f.Side1.Distance = new ValueRef(8); p.AddFeature(f); return f; },
                (f, i) => ((ExtrudeFeature)f).Side1.Distance = new ValueRef(8 + 0.5 * i)),
            ("extrude-cut", p => { var s = RectSketch(p, 10, -60, -40, 20, 12); var f = new ExtrudeFeature("BCut") { SketchId = s.Id, Cut = true }; f.Side1.Distance = new ValueRef(4); f.Side1.Flip = true; p.AddFeature(f); return f; },
                (f, i) => ((ExtrudeFeature)f).Side1.Distance = new ValueRef(4 + 0.3 * i)),
            ("hole", p => { var f = new HoleFeature("BHole") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(5), ThroughAll = true }; f.Points.Add(new Vector3d(-40, 35, 10)); p.AddFeature(f); return f; },
                (f, i) => ((HoleFeature)f).Diameter = new ValueRef(5 + 0.2 * i)),
            ("round-edge", p => { var f = new RoundFeature("BRound") { Radius = new ValueRef(3) }; f.Edges.Add(EdgeRef.Of(EdgeWhere(p, e => AlongY(e, 80, 10) || AlongY(e, -80, 10)))); p.AddFeature(f); return f; },
                (f, i) => ((RoundFeature)f).Radius = new ValueRef(3 + 0.2 * i)),
            ("chamfer-edge", p => { var f = new ChamferFeature("BChamfer") { D1 = new ValueRef(2), D2 = new ValueRef(2) }; f.Edges.Add(EdgeRef.Of(EdgeWhere(p, e => AlongY(e, 80, -10) || AlongY(e, -80, -10)))); p.AddFeature(f); return f; },
                (f, i) => { ((ChamferFeature)f).D1 = new ValueRef(2 + 0.1 * i); ((ChamferFeature)f).D2 = new ValueRef(2 + 0.1 * i); }),
            ("shell", p => { var f = new ShellFeature("BShell") { Thickness = new ValueRef(2) }; f.OpenFaces.Add(new Vector3d(0, 0, -10)); p.AddFeature(f); return f; },
                (f, i) => ((ShellFeature)f).Thickness = new ValueRef(2 + 0.1 * i)),
            ("draft-edge", p => { var f = new DraftFeature("BDraft") { Angle = new ValueRef(3), Hinge = EdgeRef.Of(EdgeWhere(p, e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Near(e.V0.P.Y, -50) && Near(e.V1.P.Y, -50) && Near(e.V0.P.Z, -10) && Near(e.V1.P.Z, -10))) };
                    f.OnFaces.Add(new Vector3d(0, -50, 0)); p.AddFeature(f); return f; },
                (f, i) => ((DraftFeature)f).Angle = new ValueRef(3 + 0.2 * i)),
            ("linear-pattern", p => { var f = new LinearPatternFeature("BPat") { Step = new Vector3d(0, -12, 0), Count = new ValueRef(4) }; f.Sources.Add(p.Features.OfType<HoleFeature>().Last().Key); p.AddFeature(f); return f; },
                (f, i) => ((LinearPatternFeature)f).Step = new Vector3d(0, -12 - 0.2 * i, 0)),
            ("revolve", p =>
                {
                    var fr = new Frame3(new Vector3d(0, 0, 0), Vector3d.UnitX, Vector3d.UnitZ);
                    var s = new Sketch("Rev", fr); p.Add(s);
                    var a = new SkPoint(50, 10); var b = new SkPoint(58, 10); var c = new SkPoint(58, 20); var d = new SkPoint(50, 20);
                    s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkLine(b, c)); s.AddEntity(new SkLine(c, d)); s.AddEntity(new SkLine(d, a));
                    var ax = new SkLine(new SkPoint(40, 0), new SkPoint(40, 30)) { Role = SkRole.Construction }; s.AddEntity(ax);
                    var f = new RevolveFeature("BRev") { SketchId = s.Id, AxisEntityId = ax.Id, EndAngle = new ValueRef(360) }; p.AddFeature(f); return f;
                },
                (f, i) => ((RevolveFeature)f).EndAngle = new ValueRef(360)),
        };

        // простая деталь (куб 100): прямой построитель скругления и фаски
        cases.Add(("cube-round", p => { var c = new Part("cube"); TestGeometry.AddCube(c, 100); c.Rebuild(null); CubeSwap = c; var f = new RoundFeature("CR") { Radius = new ValueRef(3) };
                f.Edges.Add(EdgeRef.Of(c.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3))); c.AddFeature(f); return f; }, (f, i) => ((RoundFeature)f).Radius = new ValueRef(3 + 0.25 * i)));
        cases.Add(("cube-chamfer", p => { var c = new Part("cube"); TestGeometry.AddCube(c, 100); c.Rebuild(null); CubeSwap = c; var f = new ChamferFeature("CC") { D1 = new ValueRef(3), D2 = new ValueRef(3) };
                f.Edges.Add(EdgeRef.Of(c.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3))); c.AddFeature(f); return f; }, (f, i) => { ((ChamferFeature)f).D1 = new ValueRef(3 + 0.25 * i); ((ChamferFeature)f).D2 = new ValueRef(3 + 0.25 * i); }));
        cases.Add(("cube-shell", p => { var c = new Part("cube"); TestGeometry.AddCube(c, 100); c.Rebuild(null); CubeSwap = c; var f = new ShellFeature("CS") { Thickness = new ValueRef(3) };
                double zt = c.Bodies[0].Vertices.Max(v => v.P.Z); f.OpenFaces.Add(new Vector3d(0, 0, zt)); c.AddFeature(f); return f; }, (f, i) => ((ShellFeature)f).Thickness = new ValueRef(3 + 0.25 * i)));
        cases.Add(("cube-draft", p => { var c = new Part("cube"); TestGeometry.AddCube(c, 100); c.Rebuild(null); CubeSwap = c; double zb = c.Bodies[0].Vertices.Min(v => v.P.Z), xm = c.Bodies[0].Vertices.Max(v => v.P.X);
                var f = new DraftFeature("CD") { Angle = new ValueRef(3), Hinge = EdgeRef.Of(c.Bodies[0].Edges.First(e => Near(e.V0.P.X, xm) && Near(e.V1.P.X, xm) && Near(e.V0.P.Z, zb) && Near(e.V1.P.Z, zb))) };
                f.OnFaces.Add(new Vector3d(xm, 0, 0)); c.AddFeature(f); return f; }, (f, i) => ((DraftFeature)f).Angle = new ValueRef(3 + 0.25 * i)));
        Console.WriteLine($"{"case",-16} {"faces",6} {"median",8} {"op",8} {"compute",8} {"apply",7} {"max",8}  volume, error");
        foreach (var (name, add, change) in cases)
        {
            if (filter != null && !name.Contains(filter)) continue;
            var p = Base();
            Feature f;
            try { CubeSwap = null; f = add(p); if (CubeSwap != null) p = CubeSwap; p.Rebuild(null); }
            catch (Exception ex) { Console.WriteLine($"{name,-16} setup failed: {ex.Message}"); continue; }
            var tot = new List<double>(); var opT = new List<double>(); var comp = new List<double>(); var app = new List<double>();
            if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
            for (int i = 0; i < reps + 1; i++)
            {
                change(f, i + 1);
                var sw = Stopwatch.StartNew();
                p.MarkDirtyFrom(f);
                Part.BuildResult r;
                using (new KernelContext(PreviewSettings()).Enter()) r = p.Compute(null);
                double c = sw.Elapsed.TotalMilliseconds;
                p.Apply(r);
                double all = sw.Elapsed.TotalMilliseconds;
                if (i == 0) continue;                       // прогрев
                tot.Add(all); comp.Add(c); app.Add(all - c);
                opT.Add(r.FeatureMs.TryGetValue(f, out var fm) ? fm : double.NaN);
            }
            int faces = p.Bodies.Sum(b => b.Faces.Count());
            // объём предпросмотра против точного (то же значение операции)
            double vPrev = p.Volume; string? prevErr = f.Error;
            p.MarkDirtyFrom(f); p.Rebuild(null);
            double vExact = p.Volume;
            double dv = Math.Abs(vPrev - vExact) / Math.Max(1e-9, Math.Abs(vExact)) * 100;
            Console.WriteLine($"{name,-16} {faces,6} {Med(tot),8:0.0} {Med(opT),8:0.0} {Med(comp),8:0.0} {Med(app),7:0.0} {tot.Max(),8:0.0}  dV {dv:0.###}% (preview {vPrev:0.#}, exact {vExact:0.#}) {prevErr} {(f.Error != prevErr ? "| exact: " + f.Error : "")}");
            if (prof)
            {
                Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(25).Select(l => "    " + l)));
                Parametric3D.Kernel.Diagnostics.Prof.Enabled = false;
            }
        }
        // правка средней операции (всё, что после неё, пересчитывается)
        if (filter == null || "mid-edit".Contains(filter))
        {
            var p = Base();
            var mid = p.Features.OfType<HoleFeature>().ElementAt(p.Features.OfType<HoleFeature>().Count() / 2);
            int idx = p.Features.IndexOf(mid);
            var tot = new List<double>();
            for (int i = 0; i < reps + 1; i++)
            {
                mid.Diameter = new ValueRef(mid.Diameter.Value * (i % 2 == 0 ? 0.97 : 1 / 0.97));
                var sw = Stopwatch.StartNew();
                p.MarkDirtyFrom(mid);
                Part.BuildResult r;
                using (new KernelContext(PreviewSettings()).Enter()) r = p.Compute(null);
                p.Apply(r);
                if (i > 0) tot.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"{"mid-edit",-16} {p.Bodies.Sum(b => b.Faces.Count()),6} {Med(tot),8:0.0} {"",8} {"",8} {"",7} {tot.Max(),8:0.0}  (operation {idx + 1} of {p.Features.Count})");
        }
    }
}

static partial class Program
{
    /// <summary>k113: составляющие предпросмотра по сеткам (бобышка на пластине K08): сетка тела, резец, рез, тело из кусков.</summary>
    static void MeshPreviewBench()
    {
        var p = RealisticPart(3, 20, out _); p.Rebuild(null);
        var body = p.Bodies[0];
        var fr = new Frame3(new Vector3d(0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        var circ = new List<Parametric3D.Kernel.Curves.ICurve3> { Parametric3D.Kernel.Curves.ArcCurve3.FullCircle(new Frame3(new Vector3d(30, 20, 10), Vector3d.UnitX, Vector3d.UnitY), 6) };
        var sw = new Stopwatch();
        for (int rep = 0; rep < 4; rep++)
        {
            sw.Restart();
            var prof = Parametric3D.Kernel.Ops.ProfileBuilder.Build(fr, circ);
            var tool = Parametric3D.Kernel.Ops.ExtrudeOp.Build(prof, new Vector3d(0, 0, 8 + rep));
            double tTool = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var src = new Dictionary<uint, Parametric3D.Kernel.Surfaces.ISurface3?>();
            var pa = Parametric3D.Kernel.Ops.MeshCut.MeshPolys(body, 0, src);
            double tA = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var pt = Parametric3D.Kernel.Ops.MeshCut.MeshPolys(tool, 1u << 30, src);
            double tT = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var r = Parametric3D.Kernel.Ops.MeshCut.Run(pa, pt, false)!;
            double tRun = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var solid = Parametric3D.Kernel.Ops.BooleanOp.SolidFromPolygons(r, src);
            double tSolid = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(solid);
            double tMesh = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"tool {tTool:0.0} ms, body polys {pa.Count} in {tA:0.0} ms, tool polys {pt.Count} in {tT:0.0} ms, cut {tRun:0.0} ms -> {r.Count} polys, solid {tSolid:0.0} ms, mesh {tMesh:0.0} ms");
        }
    }
}
