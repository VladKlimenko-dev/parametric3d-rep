using OpenTK.Mathematics;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

static partial class Program
{
    /// <summary>
    /// KRN-11 (11.5): БОЛЬШАЯ МОДЕЛЬ. Плита 400×300×20 и 200 операций дерева (сквозные отверстия разных диаметров, карманы):
    /// полная сборка — годное точное тело, объём по формуле; правка последней операции — перестроение только хвоста (с
    /// контрольной точки), заметно быстрее полной сборки; 10 правок подряд — тот же результат, что сборка с нуля, память после
    /// сборки мусора не растёт больше 64 МБ.
    /// </summary>
    static void Krn11BigModelChecks()
    {
        Check("KRN11-BIG-MODEL", () =>
        {
            var fails = new List<string>();
            const int nx = 20, ny = 10, KL = nx * ny - 2;
            Part Build(double lastD)
            {
                var p = new Part("big");
                var plate = RectSketch(p, -10, -200, -150, 400, 300);
                var ex = new ExtrudeFeature("Plate") { SketchId = plate.Id }; ex.Side1.Distance = new ValueRef(20); p.AddFeature(ex);
                int k = 0;
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < ny; j++, k++)
                    {
                        double x = -190 + 20 * i, y = -135 + 30 * j;
                        if ((i + j) % 4 == 0)
                        {
                            var s = RectSketch(p, 10, x - 4, y - 4, 8, 8);
                            var cut = new ExtrudeFeature($"Pocket{k}") { SketchId = s.Id, Cut = true }; cut.Side1.Distance = new ValueRef(5); cut.Side1.Flip = true; p.AddFeature(cut);
                        }
                        else
                        {
                            var h = new HoleFeature($"Hole{k}") { Direction = -Vector3d.UnitZ, ThroughAll = true };
                            h.Points.Add(new Vector3d(x, y, 10)); h.Diameter = new ValueRef(k == KL ? lastD : 3 + (k % 3));
                            p.AddFeature(h);
                        }
                    }
                return p;
            }
            double Want(double lastD)
            {
                double v = 400 * 300 * 20; int k = 0;
                for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++, k++)
                    if ((i + j) % 4 == 0) v -= 8 * 8 * 5; else { double d = k == KL ? lastD : 3 + (k % 3); v -= Math.PI * d * d / 4 * 20; }
                return v;
            }
            long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            var part = Build(4);
            bool prof = Environment.GetEnvironmentVariable("P3D_BIGPROF") == "1";
            if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
            var sw = System.Diagnostics.Stopwatch.StartNew(); part.Rebuild(null); double tFull = sw.Elapsed.TotalMilliseconds;
            if (prof) { foreach (var l in Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(30)) Console.WriteLine("  P " + l); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; return; }
            if (part.Bodies.Count != 1) fails.Add($"{part.Bodies.Count} bodies");
            else
            {
                double v = OpVolume(part.Bodies[0]);
                if (!(Math.Abs(v - Want(4)) <= 1e-9 * Want(4))) fails.Add($"volume {v:R} want {Want(4):R}");
                var val = Parametric3D.Kernel.Topology.SolidValidator.Validate(part.Bodies[0]); if (!val.IsValid) fails.Add("invalid: " + val.Summary());
                Console.WriteLine($"  big model: {part.Features.Count} features, {part.Bodies[0].Faces.Count()} faces, full build {tFull:F0} ms, {part.Bodies[0].Guarantee}");
            }
            long m0 = Mem(); var times = new List<double>(); double d = 4;
            for (int e = 0; e < 10; e++)
            {
                d = e % 2 == 0 ? 5 : 4;
                var last = part.Features.OfType<HoleFeature>().Last(); last.Diameter = new ValueRef(d); part.ParametersChanged(); part.MarkDirtyFrom(last);
                sw.Restart(); part.Rebuild(null); times.Add(sw.Elapsed.TotalMilliseconds);
                double v = part.Bodies.Sum(OpVolume);
                if (!(Math.Abs(v - Want(d)) <= 1e-9 * Want(d))) fails.Add($"edit {e}: volume {v:R} want {Want(d):R}");
            }
            long m1 = Mem();
            var clean = Build(d); clean.Rebuild(null);
            if (Math.Abs(clean.Bodies.Sum(OpVolume) - part.Bodies.Sum(OpVolume)) > 1e-9 * Want(d) || clean.Bodies[0].Faces.Count() != part.Bodies[0].Faces.Count())
                fails.Add("after 10 edits the part differs from a clean build");
            double tEdit = times.OrderBy(x => x).ElementAt(times.Count / 2);
            Log(new { test = "KRN11-BIG-MODEL", features = part.Features.Count, fullMs = Math.Round(tFull), editMedianMs = Math.Round(tEdit), editMaxMs = Math.Round(times.Max()), memGrowthMB = Math.Round((m1 - m0) / 1048576.0, 1) });
            if (tEdit * 5 > tFull) fails.Add($"tail edit {tEdit:F0} ms vs full {tFull:F0} ms — not incremental");
            if (m1 - m0 > 64L << 20) fails.Add($"memory grew {(m1 - m0) >> 20} MB over 10 edits");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
