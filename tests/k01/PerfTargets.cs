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
        }
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
