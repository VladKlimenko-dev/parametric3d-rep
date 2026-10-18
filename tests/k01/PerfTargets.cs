using System.Diagnostics;
using Parametric3D.Kernel;
using Parametric3D.Model;

/// <summary>
/// g.13 / m.v1 / m.v2: ЗАМЕРЫ ПО ЦЕЛЯМ ПОЛЬЗОВАТЕЛЯ (согласованы 2026-10-12). Пишет строки JSON в файл; сравнение с целями —
/// tools/perf_targets.py (вместе с временами серии 8 и OpenCascade на тех же случаях). Замер, не проверка: годность — там.
///   деталь K08 (6 пластин, ≤ 25 операций): полное перестроение, правка последней и средней операции (медиана из 3),
///   правка последней в предпросмотре (медиана из 5), выделения памяти на полное перестроение;
///   деталь со 100 отверстиями — полное перестроение;
///   долгая сессия: 200 правок на пластине — рост управляемой памяти после сборки мусора.
/// </summary>
static partial class Program
{
    static void PerfTargets(string outPath)
    {
        using var w = new StreamWriter(outPath);
        void Emit(object o) { var s = System.Text.Json.JsonSerializer.Serialize(o); w.WriteLine(s); w.Flush(); Console.WriteLine(s); }
        static double Med(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        { var p0 = RealisticPart(99, 20, out _); p0.MarkDirtyFrom(p0.Features[^1]); p0.Rebuild(null); }   // прогрев
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var p = RealisticPart(seed, 40, out _);
            var sw = new Stopwatch();
            GC.Collect(); long a0 = GC.GetTotalAllocatedBytes(true);
            p.MarkDirtyFrom(p.Features[0]); sw.Restart(); p.Rebuild(null); double full = sw.Elapsed.TotalMilliseconds;
            double allocMb = (GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0;
            var lastT = new List<double>(); var midT = new List<double>(); var prevT = new List<double>();
            var last = p.Features[^1]; var mid = p.Features[p.Features.Count / 2];
            for (int i = 0; i < 3; i++) { p.MarkDirtyFrom(last); sw.Restart(); p.Rebuild(null); lastT.Add(sw.Elapsed.TotalMilliseconds); }
            for (int i = 0; i < 3; i++) { p.MarkDirtyFrom(mid); sw.Restart(); p.Rebuild(null); midT.Add(sw.Elapsed.TotalMilliseconds); }
            for (int i = 0; i < 5; i++)
            {
                p.MarkDirtyFrom(last); sw.Restart();
                using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) { var r = p.Compute(null); p.Apply(r); }
                prevT.Add(sw.Elapsed.TotalMilliseconds);
            }
            p.Rebuild(null);   // принятие тел предпросмотра
            Emit(new { kind = "part", seed, features = p.Features.Count, faces = p.Bodies.Sum(b => b.Faces.Count()), errors = p.Features.Count(f => f.Error != null),
                       fullMs = full, editLastMs = Med(lastT), editMidMs = Med(midT), previewLastMs = Med(prevT), allocMb });
        }
        {
            var p = new Part("perfholes");
            TestGeometry.AddCube(p, 200);
            int side = 10; double step = 200.0 / (side + 1);
            for (int i = 0; i < 100; i++)
            {
                double x = -100 + step * (1 + i % side), y = -100 + step * (1 + i / side);
                var h = new Parametric3D.Model.Features.HoleFeature($"H{i}") { Direction = -OpenTK.Mathematics.Vector3d.UnitZ, Diameter = new Parametric3D.Model.Features.ValueRef(4), ThroughAll = true };
                h.Points.Add(new OpenTK.Mathematics.Vector3d(x, y, 100));
                p.AddFeature(h);
            }
            p.Rebuild(null);
            p.MarkDirtyFrom(p.Features[0]);
            var sw = Stopwatch.StartNew(); p.Rebuild(null); double full = sw.Elapsed.TotalMilliseconds;
            Emit(new { kind = "holes100", fullMs = full, errors = p.Features.Count(f => f.Error != null), faces = p.Bodies.Sum(b => b.Faces.Count()) });
            IoTimes(Emit, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "io"), "holes100", p.Bodies.ToList());
        }
        { var p = RealisticPart(1, 40, out _); p.Rebuild(null); IoTimes(Emit, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "io"), "part1", p.Bodies.ToList()); }
        {
            var p = RealisticPart(7, 40, out _);
            var last = p.Features[^1]; var mid = p.Features[p.Features.Count / 2];
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long m0 = GC.GetTotalMemory(true);
            for (int i = 0; i < 100; i++) { p.MarkDirtyFrom(last); p.Rebuild(null); p.MarkDirtyFrom(mid); p.Rebuild(null); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long m1 = GC.GetTotalMemory(true);
            Emit(new { kind = "session", edits = 200, startMb = m0 / 1048576.0, endMb = m1 / 1048576.0, growthMb = (m1 - m0) / 1048576.0,
                       workingSetMb = Environment.WorkingSet / 1048576.0 });
        }
    }
}

static partial class Program
{
    /// <summary>
    /// m.v2: ИМПОРТ/ЭКСПОРТ — ВРЕМЯ И ПАМЯТЬ. Точный STEP (запись, чтение обратно с проверкой объёма), IGES (запись, чтение),
    /// STL (разметка + двоичная запись); медиана из 3, выделения на чтение STEP. Файлы STEP — в каталог io (их же читает и пишет
    /// OpenCascade: tools/occt_io_time.py — для сравнения в perf_targets.py).
    /// </summary>
    static void IoTimes(Action<object> emit, string dir, string name, List<Parametric3D.Kernel.Topology.TSolid> bodies)
    {
        Directory.CreateDirectory(dir);
        static double Med(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        var sw = new Stopwatch(); var tW = new List<double>(); var tR = new List<double>(); var tIw = new List<double>(); var tIr = new List<double>(); var tS = new List<double>();
        string step = ""; string iges = ""; int back = 0; double vIn = bodies.Sum(OpVolume), vBack = double.NaN; double allocMb = 0;
        for (int i = 0; i < 3; i++)
        {
            sw.Restart(); var tw = new StringWriter(); bool ok = Parametric3D.Kernel.Export.StepExact.Write(tw, bodies, name, new List<string>()); tW.Add(sw.Elapsed.TotalMilliseconds);
            if (!ok) { emit(new { kind = "io", name, error = "no exact STEP" }); return; }
            step = tw.ToString();
            GC.Collect(); long a0 = GC.GetTotalAllocatedBytes(true);
            sw.Restart(); var rb = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(step); tR.Add(sw.Elapsed.TotalMilliseconds);
            allocMb = (GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0;
            back = rb.Count; vBack = rb.Sum(OpVolume);
            sw.Restart(); iges = Parametric3D.Kernel.Export.IgesSolid.Write(bodies); tIw.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart(); Parametric3D.Kernel.Export.IgesImport.Read(iges); tIr.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            var tris = Parametric3D.Kernel.Export.MeshExport.Triangles(bodies);
            using (var ms = new MemoryStream()) Parametric3D.Kernel.Export.MeshExport.WriteStlBinary(ms, tris);
            tS.Add(sw.Elapsed.TotalMilliseconds);
        }
        File.WriteAllText(Path.Combine(dir, name + ".step"), step);
        static double F(double x) => double.IsFinite(x) ? x : -1;      // JSON: не число → −1
        Console.WriteLine($"io {name}: volume in {vIn:R}, back {vBack:R} ({back} bodies)");
        vIn = F(vIn); vBack = F(vBack);
        emit(new { kind = "io", name, faces = bodies.Sum(b => b.Faces.Count()), stepKb = step.Length / 1024.0, stepWriteMs = Med(tW), stepReadMs = Med(tR), stepReadAllocMb = allocMb,
                   bodiesBack = back, volumeIn = vIn, volumeBack = vBack, igesWriteMs = Med(tIw), igesReadMs = Med(tIr), stlMs = Med(tS) });
    }

    /// <summary>Разбор g.13: профиль предпросмотра правки последней операции и полного перестроения детали (PERFSEED).</summary>
    static void PerfProfile()
    {
        if (Environment.GetEnvironmentVariable("PERFHOLES") is string ph)
        {
            // для внешнего профилировщика: деталь со 100 отверстиями, полное перестроение ph раз (метка — HolesLoop)
            var ph0 = new Part("perfholes");
            TestGeometry.AddCube(ph0, 200);
            int side = 10; double step = 200.0 / (side + 1);
            for (int i = 0; i < 100; i++)
            {
                double x = -100 + step * (1 + i % side), y = -100 + step * (1 + i / side);
                var h = new Parametric3D.Model.Features.HoleFeature($"H{i}") { Direction = -OpenTK.Mathematics.Vector3d.UnitZ, Diameter = new Parametric3D.Model.Features.ValueRef(4), ThroughAll = true };
                h.Points.Add(new OpenTK.Mathematics.Vector3d(x, y, 100));
                ph0.AddFeature(h);
            }
            ph0.Rebuild(null);
            void HolesLoop() { for (int k = 0; k < int.Parse(ph); k++) { ph0.MarkDirtyFrom(ph0.Features[0]); var sw0 = Stopwatch.StartNew(); ph0.Rebuild(null); Console.WriteLine($"holes100 full: {sw0.ElapsedMilliseconds} ms"); } }
            HolesLoop();
            return;
        }
        int seed = int.Parse(Environment.GetEnvironmentVariable("PERFSEED") ?? "6");
        { var p0 = RealisticPart(99, 20, out _); p0.MarkDirtyFrom(p0.Features[0]); p0.Rebuild(null); }
        var p = RealisticPart(seed, 40, out _);
        p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null);
        var last = p.Features[^1];
        Console.WriteLine($"last feature: {last.GetType().Name} {last.Name}");
        Console.WriteLine($"integrity: {p.Integrity ?? "ok"}; errors: {string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error))}");
        for (int i = 0; i < 3; i++) { p.MarkDirtyFrom(last); using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) { var r = p.Compute(null); p.Apply(r); } }
        if (Environment.GetEnvironmentVariable("ROUTEDBG") == "1")
        {
            Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("  ROUTE " + m);
            p.MarkDirtyFrom(last); using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) { var r = p.Compute(null); p.Apply(r); }
            Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = null;
            if (last is Parametric3D.Model.Features.HoleFeature hf && hf.LastRoutes != null)
                foreach (var tr in hf.LastRoutes) Console.WriteLine("  HOLE " + string.Join(" | ", tr.Select(d => $"{d.Route}:{d.Verdict}:{d.Reason}")));
        }
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) { p.MarkDirtyFrom(last); using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) { var r = p.Compute(null); p.Apply(r); } }
        Console.WriteLine($"preview ×5: {sw.ElapsedMilliseconds} ms");
        if (int.TryParse(Environment.GetEnvironmentVariable("PERFLOOP"), out int loops))
        {
            // для внешнего профилировщика: тот же предпросмотр loops раз (метка в стеке — PreviewLoop)
            void PreviewLoop() { for (int i = 0; i < loops; i++) { p.MarkDirtyFrom(last); using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) { var r = p.Compute(null); p.Apply(r); } } }
            sw.Restart(); PreviewLoop(); Console.WriteLine($"preview ×{loops}: {sw.ElapsedMilliseconds} ms");
        }
        Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
        p.Rebuild(null);
        Parametric3D.Kernel.Diagnostics.Prof.Reset();
        GC.Collect(); long a0 = GC.GetTotalAllocatedBytes(true);
        p.MarkDirtyFrom(p.Features[0]); sw.Restart(); p.Rebuild(null);
        Console.WriteLine($"full rebuild: {sw.ElapsedMilliseconds} ms, allocated {(GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0:F0} MB");
        Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
    }
}
