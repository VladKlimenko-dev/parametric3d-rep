using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn07DraftTangentChecks()
    {
        Check("KRN07-DRAFT-TANGENT-ROUNDS", () =>
        {
            // 07.6: уклон стенок со скруглёнными вертикальными рёбрами — плоскости поворачиваются, касательные цилиндры
            // становятся конусами с тем же радиусом на нейтрали (касание сохраняется), выбор распространяется по касательным.
            // Объём — по сечениям: прямоугольник со скруглёнными углами, размеры и радиус линейны по высоте (Симпсон точен для
            // квадратичной площади). Брусок 60×40×20 R5: 4 стенки, нейтраль внизу и посередине, одна стенка (распространение),
            // уклон наружу; карман 30×20×10 R4 со скруглёнными углами.
            var fails = new List<string>();
            double k = 4 - Math.PI;
            TSolid BoxR5()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>());
                return bl[0];
            }
            TSolid Pocket()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                BooleanOp.Subtract(bl, Box(15, 10, 10, 30, 20, 11), new List<string>());
                var es = bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1 && e.V0.P.X > 1 && e.V0.P.X < 59 && e.V0.P.Y > 1 && e.V0.P.Y < 39).ToList();
                FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), 4, new List<string>());
                return bl[0];
            }
            static double Simpson(Func<double, double> f, double a, double b) => (b - a) / 6 * (f(a) + 4 * f((a + b) / 2) + f(b));
            var walls = new[] { new Vector3d(60, 20, 10), new Vector3d(30, 0, 10), new Vector3d(0, 20, 10), new Vector3d(30, 40, 10) };
            var pocketWalls = new[] { new Vector3d(45, 20, 15), new Vector3d(30, 10, 15), new Vector3d(15, 20, 15), new Vector3d(30, 30, 15) };
            double ta5 = Math.Tan(5 * Math.PI / 180), ta3 = Math.Tan(3 * Math.PI / 180);
            // наружные стенки: смещение по наружной нормали δ(z) = −tg a·s·(z − z0), s — сторона тела от нейтрали
            double Outer(double ta, double z0, double s) => Simpson(z => { double d = -ta * s * (z - z0); return (60 + 2 * d) * (40 + 2 * d) - k * (5 + d) * (5 + d); }, 0, 20);
            var cases = new (string Name, Func<TSolid> Make, Vector3d[] Probes, double Z0, double Deg, double Want)[]
            {
                ("box R5, 4 walls 5°, neutral at bottom", BoxR5, walls, 0, 5, Outer(ta5, 0, 1)),
                ("box R5, 4 walls 3°, neutral mid-height", BoxR5, walls, 10, 3, Outer(ta3, 10, 1)),
                ("box R5, one wall 5° (tangent propagation)", BoxR5, new[] { walls[0] }, 0, 5, Outer(ta5, 0, 1)),
                ("box R5, 4 walls −3° (outward)", BoxR5, walls, 0, -3, Outer(-ta3, 0, 1)),
                ("pocket R4, 4 walls 5°, neutral at top", Pocket, pocketWalls, 20, 5,
                    48000 - Simpson(z => { double d = -ta5 * -1 * (z - 20); return (30 - 2 * d) * (20 - 2 * d) - k * (4 - d) * (4 - d); }, 10, 20)),
            };
            foreach (var (name, make, probes, z0, deg, want) in cases)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>(); long d0 = DirectRound.DraftTangents;
                bool ok = DraftOp.Apply(bl, probes, new Vector3d(0, 0, z0), Vector3d.UnitZ, deg, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                Log(new { test = "KRN07-DRAFT-TANGENT", name, ok, volume = double.IsFinite(v) ? v : -1, want, guarantee = b.Guarantee.ToString(), n = DirectRound.DraftTangents - d0, w = string.Join(" | ", w.Take(3)) });
                if (!ok || DirectRound.DraftTangents == d0) fails.Add($"{name}: not the tangent draft ({string.Join(" | ", w.Take(3))})");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs sections {want:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
