using System.Diagnostics;
using Parametric3D.Kernel;
using Parametric3D.Model;

/// <summary>
/// KRN-10: ЗАМЕР МАСШТАБИРОВАНИЯ. Пластины K08 из n операций (n = 10…160): полное построение, время на одну операцию в
/// конце истории (растёт ли с размером тела), перестроение после правки последней операции, число граней, доли этапов.
/// </summary>
static partial class Program
{
    static void SpeedScaling(int[] sizes, int seed)
    {
        foreach (int n in sizes)
        {
            var sw0 = Stopwatch.StartNew();
            var p = RealisticPart(seed, n, out var recipe);
            double build = sw0.Elapsed.TotalMilliseconds;
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
            p.MarkDirtyFrom(p.Features[0]);
            var sw = Stopwatch.StartNew();
            // по одной операции: время каждой — перестроение с подавленными хвостами недоступно, поэтому меряем через
            // прирост: построение первых k операций (k = n/2 и n) — разность на операцию
            var r = p.Compute(null); p.Apply(r);
            double full = sw.Elapsed.TotalMilliseconds;
            string prof = string.Join(" ;; ", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains(" мс")).Take(10).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " ")));
            int faces = p.Bodies.Sum(b => b.Faces.Count());
            var g = string.Join(",", p.Bodies.Select(b => b.Guarantee).Distinct());
            int errs = p.Features.Count(f => f.Error != null);
            // правка последней операции: перестроение только хвоста
            Parametric3D.Kernel.Diagnostics.Prof.Reset();
            var last = p.Features[^1]; p.MarkDirtyFrom(last);
            sw.Restart(); var r2 = p.Compute(null); p.Apply(r2); double tail = sw.Elapsed.TotalMilliseconds;
            string prof2 = string.Join(" ;; ", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains(" мс")).Take(6).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " ")));
            Console.WriteLine($"SPEED n={n} interactiveBuild={build:0}ms features={p.Features.Count} faces={faces} guarantee={g} errors={errs} full={full:0}ms perOp={full / Math.Max(1, p.Features.Count):0}ms lastEdit={tail:0}ms");
            Console.WriteLine($"   full: {prof}");
            Console.WriteLine($"   last: {prof2}");
        }
    }
}
static partial class Program
{
    /// <summary>Пластина 200×200×20 и N сквозных отверстий по сетке, по одному вычитанию: время операции от числа граней тела.</summary>
    static void SpeedHoles(int n, string kind)
    {
        var bodies = new List<Parametric3D.Kernel.Topology.TSolid> { Box(0, 0, 0, 200, 200, 20) };
        int side = (int)Math.Ceiling(Math.Sqrt(n));
        double step = 200.0 / (side + 1);
        var w = new List<string>();
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = true;
        for (int i = 0; i < n; i++)
        {
            double x = step * (1 + i % side), y = step * (1 + i / side);
            var tool = kind switch
            {
                "box" => Box(x - 2, y - 2, -5, 4, 4, 30),
                "pocket" => Box(x - 2, y - 2, 15, 4, 4, 10),
                "sphere" => Parametric3D.Kernel.Ops.PrimitiveOp.Sphere(new OpenTK.Mathematics.Vector3d(x, y, 20), 3),
                _ => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new OpenTK.Mathematics.Vector3d(x, y, -5), 2, 30),
            };
            Parametric3D.Kernel.Diagnostics.Prof.Reset();
            var sw = Stopwatch.StartNew();
            var r = Parametric3D.Kernel.Ops.BooleanOp.Evaluate(bodies, tool, Parametric3D.Kernel.Ops.BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, w);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (r.Outcome != OperationOutcome.Success) { Console.WriteLine($"HOLES {kind} i={i} {r.Describe()}"); break; }
            bodies = r.Bodies.ToList();
            int faces = bodies.Sum(b => b.Faces.Count());
            sw.Restart(); var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(bodies[0]); double tess = sw.Elapsed.TotalMilliseconds;
            if (i < 3 || (i + 1) % Math.Max(1, n / 8) == 0)
            {
                string prof = string.Join(" ;; ", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains(" мс")).Take(7).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " ")));
                Console.WriteLine($"HOLES {kind} i={i + 1} faces={faces} op={ms:0}ms tess={tess:0}ms tris={md.Indices.Length / 3} {r.Guarantee} | {prof}");
            }
        }
    }
}
static partial class Program
{
    /// <summary>Деталь: пластина (выдавливание) и N операций «отверстие» по одной точке — время добавления каждой операции.</summary>
    static void SpeedPartHoles(int n)
    {
        var p = new Part("speedholes");
        var ex = Parametric3D.Model.TestGeometry.AddCube(p, 200);
        p.Rebuild(null);
        int side = (int)Math.Ceiling(Math.Sqrt(n)); double step = 200.0 / (side + 1);
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = true;
        for (int i = 0; i < n; i++)
        {
            double x = -100 + step * (1 + i % side), y = -100 + step * (1 + i / side);
            var h = new Parametric3D.Model.Features.HoleFeature($"H{i}") { Direction = -OpenTK.Mathematics.Vector3d.UnitZ, Diameter = new Parametric3D.Model.Features.ValueRef(4), ThroughAll = true };
            h.Points.Add(new OpenTK.Mathematics.Vector3d(x, y, 100));
            p.AddFeature(h);
            Parametric3D.Kernel.Diagnostics.Prof.Reset();
            var sw = Stopwatch.StartNew(); var r = p.Compute(null); double comp = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); p.Apply(r); double apply = sw.Elapsed.TotalMilliseconds;
            if (h.Error != null) { Console.WriteLine($"PARTHOLES i={i} error {h.Error}"); break; }
            if (i < 2 || (i + 1) % Math.Max(1, n / 8) == 0)
            {
                string prof = string.Join(" ;; ", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains(" мс")).Take(8).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " ")));
                Console.WriteLine($"PARTHOLES i={i + 1} faces={p.Bodies.Sum(b => b.Faces.Count())} compute={comp:0}ms apply={apply:0}ms {string.Join(",", p.Bodies.Select(b => b.Guarantee))} | {prof}");
            }
        }
        Parametric3D.Kernel.Diagnostics.Prof.Reset();
        p.MarkDirtyFrom(p.Features[0]);
        var sw2 = Stopwatch.StartNew(); var rr = p.Compute(null); p.Apply(rr);
        Console.WriteLine($"PARTHOLES full rebuild {n} holes: {sw2.Elapsed.TotalMilliseconds:0}ms | " + string.Join(" ;; ", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains(" мс")).Take(30).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " "))));
    }
}
static partial class Program
{
    /// <summary>
    /// KRN-10: накладные расходы дерева на операцию не растут с размером тела сверх линейного: история решается точно (без
    /// сравнений по сетке), нормализация размечает каждое кольцо один раз за проход. Счётчики детерминированы (не время).
    /// </summary>
    static void Krn10Checks()
    {
        Check("KRN10-TREE-OVERHEAD-NOT-QUADRATIC", () =>
        {
            (long Mesh, long Rings, int Faces, long SigAsked, long SigComputed) Run(int n)
            {
                var p = new Part("k10"); Parametric3D.Model.TestGeometry.AddCube(p, 200); p.Rebuild(null);
                int side = (int)Math.Ceiling(Math.Sqrt(n)); double step = 200.0 / (side + 1);
                for (int i = 0; i < n; i++)
                {
                    var h = new Parametric3D.Model.Features.HoleFeature($"H{i}") { Direction = -OpenTK.Mathematics.Vector3d.UnitZ, Diameter = new Parametric3D.Model.Features.ValueRef(4), ThroughAll = true };
                    h.Points.Add(new OpenTK.Mathematics.Vector3d(-100 + step * (1 + i % side), -100 + step * (1 + i / side), 100));
                    p.AddFeature(h);
                }
                bool was = Parametric3D.Kernel.Diagnostics.Prof.Enabled;
                Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
                try
                {
                    p.Rebuild(null);
                    Require(p.Features.All(f => f.Error == null), "holes must build: " + string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Error)));
                    return (Parametric3D.Kernel.Diagnostics.Prof.CountOf("история: сравнений по сетке"), Parametric3D.Kernel.Diagnostics.Prof.CountOf("соглашение: разметок колец"), p.Bodies.Sum(b => b.Faces.Count()),
                            Parametric3D.Kernel.Diagnostics.Prof.CountOf("история: подписей запрошено"), Parametric3D.Kernel.Diagnostics.Prof.CountOf("история: подписей вычислено"));
                }
                finally { Parametric3D.Kernel.Diagnostics.Prof.Enabled = was; }
            }
            var a = Run(16); var b = Run(48);
            Log(new { test = "KRN10-TREE-OVERHEAD-NOT-QUADRATIC", mesh16 = a.Mesh, rings16 = a.Rings, faces16 = a.Faces, mesh48 = b.Mesh, rings48 = b.Rings, faces48 = b.Faces, sigAsked = b.SigAsked, sigComputed = b.SigComputed });
            Require(a.Mesh == 0 && b.Mesh == 0, $"history must be decided exactly, without mesh comparisons: {a.Mesh}, {b.Mesh}");
            // кольца: на операцию — O(колец тела), за перестроение — O(N²) с малым множителем; квадратичная разметка колец в каждой
            // компоненте давала O(N³): 16→48 отверстий — рост в ~27 раз; допускается рост не больше чем в 12 раз (N² — 9)
            double ratio = (double)b.Rings / Math.Max(1, a.Rings);
            Require(ratio <= 12, $"ring tessellations grow {ratio:0.0}× for 3× holes ({a.Rings} → {b.Rings})");
            // подпись грани считается один раз на объект грани: грани результата операции — вход следующей
            Require(b.SigComputed <= 0.6 * b.SigAsked, $"face signatures recomputed: {b.SigComputed} of {b.SigAsked} requests");
        });
    }
}
