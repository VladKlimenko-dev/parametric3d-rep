using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>«Стадион» с концами-кривыми Безье (G1): отрезки y = ±b, x ∈ [−c, c], концы — кубические Безье с выносом k.</summary>
    static (List<ICurve3> Loop, double A, double L) BezierStadium(double c, double b, double k)
    {
        var right = new BezierCurve3(new[] { new Vector3d(c, -b, 0), new Vector3d(c + k, -b, 0), new Vector3d(c + k, b, 0), new Vector3d(c, b, 0) });
        var left = new BezierCurve3(new[] { new Vector3d(-c, b, 0), new Vector3d(-c - k, b, 0), new Vector3d(-c - k, -b, 0), new Vector3d(-c, -b, 0) });
        var loop = new List<ICurve3> { new LineCurve3(new(-c, -b, 0), new(c, -b, 0)), right, new LineCurve3(new(c, b, 0), new(-c, b, 0)), left };
        // площадь — ∮ x dy (у отрезков dy = 0; у кривой Безье x·y′ — многочлен 5-й степени, Гаусс по 3 узлам точен)
        double[] g3 = { -0.7745966692414834, 0, 0.7745966692414834 }, w3 = { 5.0 / 9, 8.0 / 9, 5.0 / 9 };
        double A = 0, Lb = 0;
        foreach (var bz in new[] { right, left })
        {
            for (int q = 0; q < 3; q++)
            {
                double t = 0.5 + 0.5 * g3[q];
                var d = 3 * ((bz.P[1] - bz.P[0]) * (1 - t) * (1 - t) + (bz.P[2] - bz.P[1]) * (2 * t * (1 - t)) + (bz.P[3] - bz.P[2]) * (t * t));
                A += 0.5 * w3[q] * bz.PointAt(t).X * d.Y;
            }
            // длина — составной Гаусс (64 куска по 8 узлов): подынтегральное гладкое, ошибка ниже 1e-15
            double[] g8 = { -0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498, 0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363 };
            double[] w8 = { 0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620, 0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763 };
            const int P = 64;
            for (int pi = 0; pi < P; pi++)
                for (int q = 0; q < 8; q++)
                {
                    double t = (pi + 0.5 + 0.5 * g8[q]) / P;
                    var d = 3 * ((bz.P[1] - bz.P[0]) * (1 - t) * (1 - t) + (bz.P[2] - bz.P[1]) * (2 * t * (1 - t)) + (bz.P[3] - bz.P[2]) * (t * t));
                    Lb += 0.5 * w8[q] / P * d.Length;
                }
        }
        return (loop, Math.Abs(A), 4 * c + Lb);
    }

    static void Krn07DraftSplineChecks()
    {
        Check("KRN07-DRAFT-BEZIER-WALLS", () =>
        {
            // 07.6: УКЛОН СТЕНОК-ВЫДАВЛИВАНИЙ КРИВЫХ БЕЗЬЕ. «Стадион» c = 10, b = 8, концы — кубические Безье с выносом 8 (касание
            // с отрезками), выдавлен на H = 20. Стенки наклоняются вокруг нейтрали; сечение на высоте z — эквидистанта контура
            // на δ(z) (контур гладкий, поворот касательной 2π): площадь A − L·δ + π·δ² (точно, пока δ меньше наименьшего радиуса
            // кривизны 6). V = ∫ (A − Lδ + πδ²) dz. Выбор одной стенки Безье распространяется на все по касательным рёбрам.
            var fails = new List<string>();
            var (loop, A, L) = BezierStadium(10, 8, 8);
            const double H = 20;
            TSolid Body() => ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), loop), new Vector3d(0, 0, H));
            double Want(double deg, double z0, double s) { double t = Math.Tan(deg * Math.PI / 180); double V = 0;
                // δ(z) = t·s·(z − z0) — многочлен, Симпсон точен для квадратичной площади
                double Ar(double z) { double d = t * s * (z - z0); return A - L * d + Math.PI * d * d; }
                V = H / 6 * (Ar(0) + 4 * Ar(H / 2) + Ar(H)); return V; }
            var rightMid = new Vector3d(10 + 6, 0, H / 2);       // середина правой кривой Безье: x = c + 0.75k
            var cases = new (string Name, Vector3d[] Probes, double Z0, double Deg, double S)[]
            {
                ("one Bezier wall 5°, neutral at bottom (propagation)", new[] { rightMid }, 0, 5, 1),
                ("one Bezier wall 4°, neutral at top", new[] { rightMid }, H, 4, -1),
                ("one Bezier wall −3° (outward)", new[] { rightMid }, 0, -3, 1),
            };
            foreach (var (name, probes, z0, deg, s) in cases)
            {
                var body0 = Body();
                if (!SolidValidator.Validate(body0).IsValid) { fails.Add($"{name}: the extruded body is invalid"); continue; }
                var bl = new List<TSolid> { body0 }; var w = new List<string>(); long d0 = DirectRound.DraftSplineWalls;
                bool ok = DraftOp.Apply(bl, probes, new Vector3d(0, 0, z0), Vector3d.UnitZ, deg, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN, want = Want(deg, z0, s);
                Console.WriteLine($"  {name}: ok {ok}, V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}, {b.Faces.Count()} faces | {string.Join(" | ", w.Take(2))}");
                if (!ok || DirectRound.DraftSplineWalls == d0) fails.Add($"{name}: not the Bezier-wall draft ({string.Join(" | ", w.Take(3))})");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                var sw = new StringWriter(); var ws = new List<string>();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "dr", ws) || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Take(2))})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
