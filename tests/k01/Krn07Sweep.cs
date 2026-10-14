using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn07ExactSweepChecks()
    {
        Check("KRN07-SWEEP-EXACT-SMOOTH-PATH", () =>
        {
            // 07.3: ТОЧНАЯ ПРОТЯЖКА ПО ГЛАДКОЙ ТРАЕКТОРИИ (кривая Безье, сплайн): рамка без вращения, грани по формуле сечения.
            // Сечение перпендикулярно траектории в начале и переносится рамкой без вращения — тело есть «трубка», и объём по
            // теореме Паппа–Гюльдена: V = A·L, где L — длина пути центра тяжести сечения (сечение с центром на траектории:
            // L — длина траектории, интегралом Гаусса по её производной). Круг r1, квадрат 2×2, кольцо 1.5/0.5 — по кривой
            // Безье и по сплайну; тело точное и годное (ExactAnalytic), объём до 1e-8.
            var fails = new List<string>();
            double LenOf(ICurve3 c)
            {
                double[] gx = { -0.9061798459386640, -0.5384693101056831, 0, 0.5384693101056831, 0.9061798459386640 }, gw = { 0.2369268850561891, 0.4786286704993665, 0.5688888888888889, 0.4786286704993665, 0.2369268850561891 };
                double sum = 0; int n = 2000;
                for (int i = 0; i < n; i++)
                {
                    double a = c.T0 + (c.T1 - c.T0) * i / n, b = c.T0 + (c.T1 - c.T0) * (i + 1) / n;
                    for (int q = 0; q < 5; q++) { double t = (a + b) / 2 + (b - a) / 2 * gx[q], h = 1e-6 * (c.T1 - c.T0); sum += gw[q] * (b - a) / 2 * (c.PointAt(t + h) - c.PointAt(t - h)).Length / (2 * h); }
                }
                return sum;
            }
            var paths = new List<(string, ICurve3)>
            {
                ("bezier S", new BezierCurve3(new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 20), new Vector3d(20, 0, 20), new Vector3d(20, 0, 40) })),
                ("bezier 3D", new BezierCurve3(new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 15), new Vector3d(10, 12, 25), new Vector3d(25, 5, 30), new Vector3d(30, 20, 45) })),
            };
            var circle = new ArcCurve3(Frame3.XY, 1, 0, 2 * Math.PI);
            var profiles = new List<(string, Func<Profile>, double Area)>
            {
                ("circle r1", () => ProfileBuilder.Build(Frame3.XY, new ICurve3[] { new ArcCurve3(Frame3.XY, 1, 0, 2 * Math.PI) }), Math.PI),
                ("square 2", () => ProfileBuilder.Build(Frame3.XY, new ICurve3[] { new LineCurve3(new(-1, -1, 0), new(1, -1, 0)), new LineCurve3(new(1, -1, 0), new(1, 1, 0)), new LineCurve3(new(1, 1, 0), new(-1, 1, 0)), new LineCurve3(new(-1, 1, 0), new(-1, -1, 0)) }), 4),
                ("ring 1.5/0.5", () => ProfileBuilder.Build(Frame3.XY, new ICurve3[] { new ArcCurve3(Frame3.XY, 1.5, 0, 2 * Math.PI), new ArcCurve3(Frame3.XY, 0.5, 0, 2 * Math.PI) }), Math.PI * (2.25 - 0.25)),
            };
            foreach (var (pn, path) in paths)
            {
                double L = LenOf(path);
                foreach (var (fn, prof, area) in profiles)
                {
                    string name = $"{pn} × {fn}"; long c0 = DirectRound.ExactSweeps;
                    try
                    {
                        var b = SweepOp.Build(prof(), path);
                        double v = AnalyticVolume.Of(b) ?? double.NaN, want = area * L;
                        Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}");
                        if (DirectRound.ExactSweeps == c0) fails.Add($"{name}: not exact");
                        if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                        if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                    }
                    catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
                }
            }
            // ЗАКРУТКА: квадрат 2×2 по отрезку 40 с закруткой 90° и по кривой Безье с закруткой 180° — сечения ⟂ траектории
            // поворачиваются вокруг центра тяжести, объём тот же: V = 4·L (Кавальери / Папп)
            foreach (var (pn, path, tw) in new (string, ICurve3, double)[] { ("line 40", new LineCurve3(Vector3d.Zero, new Vector3d(0, 0, 40)), 90), ("bezier S", paths[0].Item2, 180) })
            {
                string name = $"{pn} × square 2, twist {tw}°"; long c0 = DirectRound.ExactSweeps;
                try
                {
                    var b = SweepOp.Build(profiles[1].Item2(), path, 1, tw);
                    double L = LenOf(path), v = AnalyticVolume.Of(b) ?? double.NaN, want = 4 * L;
                    // угол поворота сечения в конце: вершина квадрата (1, 1) — повёрнута на tw вокруг касательной
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}, {b.Faces.Count()} faces");
                    if (DirectRound.ExactSweeps == c0) fails.Add($"{name}: not exact");
                    if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                    // точный STEP: грани протяжки — B-сплайнами (отрезки — степень 2 по v), рельсы — сплайнами со строгим бюджетом;
                    // объём файла сверяет OpenCascade (G14: протяжка по кривой Безье и с закруткой)
                    var ws = new List<string>(); var sw = new StringWriter();
                    bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "sweep", ws);
                    if (!ok) fails.Add($"{name}: no exact STEP ({string.Join(" | ", ws.Where(x => x.Contains("not")).Take(2))})");
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
