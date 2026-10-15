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
            // ЗАКОН ОРИЕНТАЦИИ «НЕ ПОВОРАЧИВАТЬ»: сечение в плоскости XY переносится параллельно по кривой Безье S (z монотонно
            // от 0 до 40) — по Кавальери горизонтальное сечение тела на любой высоте — та же фигура: V = A·40
            foreach (var (fn, prof, area) in new (string, Func<Profile>, double)[] { ("circle r1", profiles[0].Item2, Math.PI), ("square 2", profiles[1].Item2, 4) })
            {
                string name = $"bezier S × {fn}, kept orientation"; long c0 = DirectRound.ExactSweeps;
                try
                {
                    var b = SweepOp.Build(prof(), paths[0].Item2, 1, 0, true);
                    double v = AnalyticVolume.Of(b) ?? double.NaN, want = area * 40;
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}");
                    if (DirectRound.ExactSweeps == c0) fails.Add($"{name}: not exact");
                    if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            // ЗАМКНУТАЯ траектория — эллипс 20×12 (плоскость XZ): трубка круга r1 и квадрат 2×2 с закруткой 360° — без торцов,
            // рамка замкнута поправкой; V = A·L (L — периметр эллипса квадратурой)
            {
                var ell = new EllipseCurve3(new Frame3(new Vector3d(0, 0, 0), Vector3d.UnitX, Vector3d.UnitZ), 20, 12, 0, 2 * Math.PI);
                double L = LenOf(ell);
                foreach (var (fn, prof, area, tw) in new (string, Func<Profile>, double, double)[] { ("circle r1", profiles[0].Item2, Math.PI, 0), ("square 2, twist 360°", profiles[1].Item2, 4, 360) })
                {
                    string name = $"closed ellipse × {fn}"; long c0 = DirectRound.ExactSweeps;
                    if (Environment.GetEnvironmentVariable("P3D_DRDBG") == "1") { DirectRound.Debug = m => Console.WriteLine("   [dr] " + m); TopologyConventions.AnchorDebug = m => Console.WriteLine("   [an] " + m); }
                    try
                    {
                        var b = SweepOp.Build(prof(), ell, 1, tw);
                        double v = AnalyticVolume.Of(b) ?? double.NaN, want = area * L;
                        Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}, {b.Faces.Count()} faces");
                        if (DirectRound.ExactSweeps == c0) fails.Add($"{name}: not exact");
                        if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                        if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                    }
                    catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    static void Krn07SplineLoftChecks()
    {
        Check("KRN07-LOFT-SPLINE-SECTIONS", () =>
        {
            // 07.4: ЛОФТ СО СПЛАЙНОВЫМИ ЗВЕНЬЯМИ СЕЧЕНИЙ (кривая Безье эскиза): «D» — отрезок (−5,0)→(5,0) и кубическая Безье
            // (5,0)→(5,8)→(−5,8)→(−5,0). Сечения на высотах — подобные копии с центром на оси (λ = 1, 0.5 у линейчатого; 1, 0.75,
            // 0.5 у гладкого): соответствующие точки лежат на прямых через вершину конуса — тело есть усечённый конус над «D»,
            // V = h·A·(1 + λ + λ²)/3 (A — площадь «D» по формуле Грина квадратурой). Тело точное (грани NURBS степени 3).
            var fails = new List<string>();
            Profile D(double lam, double z)
            {
                var pl = new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
                Vector3d P(double x, double y) => new(x * lam, y * lam, z);
                return ProfileBuilder.Build(pl, new ICurve3[] { new LineCurve3(P(-5, 0), P(5, 0)), new BezierCurve3(new[] { P(5, 0), P(5, 8), P(-5, 8), P(-5, 0) }) });
            }
            // площадь «D»: ∮ x dy по кривой Безье (отрезок по оси x вклада не даёт)
            double area = 0;
            {
                // x(t)·y'(t) — многочлен 5-й степени: Гаусс по 3 узлам точен; x, y' — по формулам Бернштейна
                double[] X = { 5, 5, -5, -5 }, Y = { 0, 8, 8, 0 };
                double Xt(double t) => X[0] * (1 - t) * (1 - t) * (1 - t) + 3 * X[1] * t * (1 - t) * (1 - t) + 3 * X[2] * t * t * (1 - t) + X[3] * t * t * t;
                double Yd(double t) => 3 * ((Y[1] - Y[0]) * (1 - t) * (1 - t) + 2 * (Y[2] - Y[1]) * t * (1 - t) + (Y[3] - Y[2]) * t * t);
                double[] gx = { -0.7745966692414834, 0, 0.7745966692414834 }, gw = { 5.0 / 9, 8.0 / 9, 5.0 / 9 };
                for (int q = 0; q < 3; q++) { double t = 0.5 + 0.5 * gx[q]; area += 0.5 * gw[q] * Xt(t) * Yd(t); }
                area = Math.Abs(area);
            }
            foreach (var (name, secs, smooth, lam) in new (string, Profile[], bool, double)[]
            {
                ("ruled D, λ 0.5", new[] { D(1, 0), D(0.5, 20) }, false, 0.5),
                ("smooth D, λ 1→0.75→0.5", new[] { D(1, 0), D(0.75, 10), D(0.5, 20) }, true, 0.5),
            })
            {
                long c0 = DirectRound.NurbsLofts;
                try
                {
                    var b = LoftOp.Build(secs, 64, smooth);
                    double v = AnalyticVolume.Of(b) ?? double.NaN, want = 20 * area * (1 + lam + lam * lam) / 3;
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}");
                    if (DirectRound.NurbsLofts == c0) fails.Add($"{name}: not the exact NURBS loft");
                    if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                    // точный STEP: звенья Безье — B-сплайном (без отказа и без приближения кривой)
                    var sw = new StringWriter(); var ws = new List<string>();
                    bool stepOk = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "loft", ws);
                    if (!stepOk || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Take(2))})");
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
