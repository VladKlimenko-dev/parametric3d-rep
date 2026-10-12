using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Diagnostics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    // ПОСТАВКА 3. ЗАМЕРЫ «ДО/ПОСЛЕ»: по сценарию — прогрев, N замеров, медиана, p95 (по рангу), выделенная память;
    // затем один прогон под профилировщиком ядра (Prof) — куда уходит время. Один поток, многоуровневая JIT
    // выключена снаружи (DOTNET_TieredCompilation=0), как во всех проверках.
    static readonly (string Name, Func<object> Run)[] PerfScenarios =
    {
        ("holes8", () => { var w = new List<string>(); return Scenario("S-HOLES8", w); }),
        ("sphere", () => { var w = new List<string>(); return Scenario("S-SPHERE", w); }),
        ("roundhole", () => { var w = new List<string>(); return Scenario("S-ROUNDHOLE", w); }),
        ("tilted-step-fillet", () => TiltedStepFillet()),
        // Деталь строится один раз (генератор сам перестраивает её после каждой операции); меряется полное
        // перестроение готового дерева и правка последней операции (приращение с сохранённых состояний).
        ("realistic-8001", () => { var p = PerfPart.Value; p.Invalidate(); p.Rebuild(null); return p.Volume; }),
        ("realistic-8001-edit-last", () => { var p = PerfPart.Value; p.Rebuild(null); p.MarkDirtyFrom(p.Features[^1]); p.Rebuild(null); return p.Volume; }),
        ("edge-notch", () =>
        {
            // Сквозное отверстие R5, ось в 2 мм внутри грани x = −30: кромка пересекает край грани — ExactCylinderCut.
            var p = new Part("notch"); TestGeometry.AddCube(p, 60);
            var h = new HoleFeature("Notch") { Direction = new Vector3d(0, 0, -1), ThroughAll = true, Diameter = new ValueRef(10) };
            h.Points.Add(new Vector3d(-28, 0, 30)); p.AddFeature(h); p.Rebuild(null);
            if (!p.Warnings.Any(w => w.Contains("ExactCylinderCut"))) throw new Exception("edge-notch did not take ExactCylinderCut: " + string.Join(" | ", p.Warnings));
            return p.Volume;
        }),
        ("cylcut-pockets", () =>
        {
            // Лестница из 12 ступеней и глухое отверстие R3 поперёк границы ступеней — ExactCylinderCut с дном:
            // проверка дна (Inside) и точек кромок на гранях (OnFace).
            var b = PerfPockets.Value;
            var cyl = new Parametric3D.Kernel.Geometry.Ssi.Cylinder(new Vector3d(11.3, -20, 30), new Vector3d(0, 0, -1), 3);
            var w = new List<string>();
            var r = Parametric3D.Kernel.Geometry.ExactCylinderCut.Cut(b, cyl, new Vector3d(11.3, -20, 4), w);
            if (r == null) throw new Exception("cylcut-pockets: ExactCylinderCut declined: " + string.Join(" | ", w));
            return r;
        }),
        // Чтение STEP и IGES реалистичной детали (файлы пишутся один раз): ориентация оболочки, разметка по контурам.
        ("step-read", () => Parametric3D.Kernel.Export.StepImport.ReadExactSolids(PerfFiles.Value.Step, new List<string>())),
        ("iges-read", () => Parametric3D.Kernel.Export.IgesImport.Read(PerfFiles.Value.Iges, new List<string>()) ?? throw new Exception("IGES read failed")),
        ("oblique-hole", () =>
        {
            var p = new Part("oblique"); TestGeometry.AddCube(p, 60);
            double a = 15 * Math.PI / 180;
            var h = new HoleFeature("Oblique") { Direction = new Vector3d(Math.Sin(a), 0, -Math.Cos(a)), ThroughAll = false, Diameter = new ValueRef(8), Depth = new ValueRef(20) };
            h.Points.Add(new Vector3d(0, 0, 30)); p.AddFeature(h); p.Rebuild(null); return p.Volume;
        }),
    };

    /// <summary>«Лестница»: профиль из 12 ступеней, выдавленный на 40 мм — около 40 плоских граней без отверстий.</summary>
    static readonly Lazy<TSolid> PerfPockets = new(() =>
    {
        var pts = new List<Vector2d> { new(0, 0), new(60, 0) };
        for (int i = 11; i >= 0; i--) { double z = 10 + 0.8 * i; pts.Add(new(5 * (i + 1), z)); pts.Add(new(5 * i, z)); }
        var f = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);       // W = −Y
        var w = pts.Select(p => f.ToWorld(p.X, p.Y)).ToList();
        var prof = ProfileBuilder.Build(f, Enumerable.Range(0, w.Count).Select(i => (ICurve3)new LineCurve3(w[i], w[(i + 1) % w.Count])).ToList());
        var b = new List<TSolid> { ExtrudeOp.Build(prof, new Vector3d(0, -40, 0)) };
        var warn = new List<string>();
        BooleanOp.Subtract(b, Box(100, 100, 100, 1, 1, 1), warn);        // точная форма тела (резец вдали)
        return b[0];
    });
    static readonly Lazy<(string Step, string Iges)> PerfFiles = new(() =>
    {
        var p = PerfPart.Value; var sw = new StringWriter();
        Parametric3D.Kernel.Export.StepExact.Write(sw, p.Bodies, "perf", new List<string>());
        return (sw.ToString(), Parametric3D.Kernel.Export.IgesSolid.Write(p.Bodies, new List<string>()));
    });
    static readonly Lazy<Part> PerfPart = new(() => { var p = RealisticPart(8001, 20, out _); p.Rebuild(null); return p; });

    /// <summary>Повёрнутая ступенька (брусок минус брусок) и вогнутое скругление R2 её внутреннего ребра — путь FProbe.</summary>
    static object TiltedStepFillet()
    {
        var axis = new Vector3d(.2, .7, .4).Normalized(); double angle = 0.9;
        Vector3d R(Vector3d v) => Parametric3D.Kernel.Surfaces.RevolvedSurface.RotateAbout(v, Vector3d.Zero, axis, angle);
        Vector3d U = R(Vector3d.UnitX), Vv = R(Vector3d.UnitY), W = R(Vector3d.UnitZ), O = new(12, -8, 3);
        Vector3d Map(double x, double y, double z) => O + U * x + Vv * y + W * z;
        TSolid B(double x, double y, double z, double w, double d, double h)
        {
            var f = new Frame3(Map(x, y, z), U, Vv);
            var p = new[] { f.ToWorld(0, 0), f.ToWorld(w, 0), f.ToWorld(w, d), f.ToWorld(0, d) };
            return ExtrudeOp.Build(ProfileBuilder.Build(f, Enumerable.Range(0, 4).Select(i => (ICurve3)new LineCurve3(p[i], p[(i + 1) % 4])).ToList()), W * h);
        }
        var warn = new List<string>();
        var bodies = new List<TSolid> { B(0, 0, 0, 60, 40, 20) };
        BooleanOp.Subtract(bodies, B(30, 0, 10, 30, 40, 10), warn);
        var e = bodies[0].Edges.Single(x => (x.V0.P - Map(30, 0, 10)).Length + (x.V1.P - Map(30, 40, 10)).Length < 1e-4 ||
                                            (x.V1.P - Map(30, 0, 10)).Length + (x.V0.P - Map(30, 40, 10)).Length < 1e-4);
        FilletOp.RoundEdges(bodies, new[] { EdgeRef.Of(e) }, 2, warn);
        foreach (var b in bodies) BooleanOp.MeshExactlySolid(b, warn);
        return bodies;
    }

    static void Perf(string[] args)
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("PERF_RUNS"), out var r) ? r : 11;
        string? only = args.Length > 1 ? args[1] : null;
        foreach (var (name, run) in PerfScenarios)
        {
            if (only != null && only != name) continue;
            for (int i = 0; i < 2; i++) run();                                   // прогрев
            var ms = new List<double>(); var mb = new List<double>();
            for (int i = 0; i < n; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew(); run(); sw.Stop();
                ms.Add(sw.Elapsed.TotalMilliseconds); mb.Add((GC.GetAllocatedBytesForCurrentThread() - a0) / 1e6);
            }
            var order = ms.Select(x => Math.Round(x, 1)).ToArray();
            ms.Sort(); mb.Sort();
            double Rank(List<double> xs, double q) => xs[Math.Min(xs.Count - 1, (int)Math.Ceiling(q * xs.Count) - 1)];
            // Разбор: один прогон под профилировщиком; счётчики гранения на этот прогон.
            Tessellator.Calls = Tessellator.Repeats = 0;
            Prof.Reset(); Prof.Enabled = true;
            try { run(); } finally { Prof.Enabled = false; }
            long tessCalls = Tessellator.Calls, tessRepeats = Tessellator.Repeats;
            var top = Prof.Report().Split('\n').Where(l => l.Trim().Length > 0).Take(int.TryParse(Environment.GetEnvironmentVariable("PERF_TOP"), out var tk) ? tk : 14).ToList();
            Log(new { perf = name, runs = n, medianMs = Math.Round(Rank(ms, .5), 2), p95Ms = Math.Round(Rank(ms, .95), 2),
                      minMs = Math.Round(ms[0], 2), samples = order, medianAllocMB = Math.Round(Rank(mb, .5), 1), tessCalls, tessRepeats, profile = top });
        }
    }
}
