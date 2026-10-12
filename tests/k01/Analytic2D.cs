using OpenTK.Mathematics;
using Parametric3D.Kernel.Analytic;

static partial class Program
{
    // ПЕРЕХОД НА АНАЛИТИЧЕСКОЕ ЯДРО, ЭТАП 1: булева плоских областей с отрезками и дугами — против формул площадей.
    static void Analytic2D()
    {
        void Case(string name, Region2 a, Region2 b, Bool2 op, double expected, int? loops = null)
        {
            Check(name, () =>
            {
                var r = Region2Ops.Boolean(a, b, op);
                double area = r.Area;
                Log(new { test = name, area, expected, err = area - expected, loops = r.Loops.Count, segs = r.Loops.Sum(l => l.Count) });
                Require(Math.Abs(area - expected) < 1e-9 * Math.Max(1, Math.Abs(expected)), $"area {area} vs {expected}");
                if (loops != null) Require(r.Loops.Count == loops, $"loops {r.Loops.Count} vs {loops}");
            });
        }
        double PI = Math.PI;
        var sq = Region2.Rect(0, 0, 100, 60);
        // Отверстие внутри: квадрат минус круг.
        Case("G11-2D-HOLE", sq, Region2.Circle(new(30, 30), 8), Bool2.Subtract, 6000 - PI * 64, 2);
        // Паз с края: круг на стороне y = 0 (центр на стороне) — полукруг вырезан.
        Case("G11-2D-NOTCH-ON-EDGE", sq, Region2.Circle(new(50, 0), 10), Bool2.Subtract, 6000 - PI * 100 / 2, 1);
        // Круг, частично выходящий за сторону: центр (50, 4), R10 — сегмент выше y = 0.
        double h = 4, r = 10, seg = r * r * Math.Acos(h / r) - h * Math.Sqrt(r * r - h * h);
        Case("G11-2D-NOTCH-OFFSET", sq, Region2.Circle(new(50, 4), 10), Bool2.Subtract, 6000 - (PI * 100 - seg), 1);
        // Два круга: линза (пересечение) и объединение.
        double d = 12, R = 10, lens = 2 * R * R * Math.Acos(d / (2 * R)) - d / 2 * Math.Sqrt(4 * R * R - d * d);
        Case("G11-2D-LENS", Region2.Circle(new(0, 0), R), Region2.Circle(new(d, 0), R), Bool2.Intersect, lens, 1);
        Case("G11-2D-TWO-CIRCLES-UNION", Region2.Circle(new(0, 0), R), Region2.Circle(new(d, 0), R), Bool2.Union, 2 * PI * R * R - lens, 1);
        // Совпадающие стороны: два прямоугольника встык (объединение — один контур), пересечение по общей стороне — пусто.
        Case("G11-2D-ADJACENT-UNION", Region2.Rect(0, 0, 50, 60), Region2.Rect(50, 0, 100, 60), Bool2.Union, 6000, 1);
        Case("G11-2D-ADJACENT-SUBTRACT", sq, Region2.Rect(50, 0, 100, 60), Bool2.Subtract, 3000, 1);
        // Карман, касающийся стороны изнутри (общая сторона в одну сторону).
        Case("G11-2D-FLUSH-POCKET", sq, Region2.Rect(0, 20, 30, 40), Bool2.Subtract, 6000 - 600, 1);
        // Круг, касающийся стороны снаружи (касание в точке) — объединение без слияния контуров по площади.
        Case("G11-2D-TANGENT-CIRCLE", sq, Region2.Circle(new(50, -10), 10), Bool2.Union, 6000 + PI * 100);
        // Цепочка операций: квадрат − 3 отверстия − паз с края; затем пересечение с кругом, захватывающим отверстие.
        var part = sq;
        foreach (var c in new[] { new Vector2d(20, 20), new Vector2d(50, 30), new Vector2d(80, 40) })
            part = Region2Ops.Boolean(part, Region2.Circle(c, 5), Bool2.Subtract);
        part = Region2Ops.Boolean(part, Region2.Rect(90, 10, 110, 20), Bool2.Subtract);
        Case("G11-2D-CHAIN", part, Region2.Rect(-1, -1, 101, 61), Bool2.Intersect, 6000 - 3 * PI * 25 - 100);
        // Тождества на случайных наборах: |A∪B| = |A| + |B| − |A∩B|, |A−B| = |A| − |A∩B|.
        Check("G11-2D-IDENTITIES", () =>
        {
            var rnd = new Random(11); int n = 0; double worst = 0;
            for (int k = 0; k < 200; k++)
            {
                Region2 Shape()
                {
                    double x = rnd.NextDouble() * 60, y = rnd.NextDouble() * 40;
                    return rnd.Next(2) == 0 ? Region2.Rect(x, y, x + 5 + rnd.NextDouble() * 40, y + 5 + rnd.NextDouble() * 30)
                                            : Region2.Circle(new(x + 10, y + 10), 3 + rnd.NextDouble() * 15);
                }
                var a = Shape(); var b = Shape();
                double u = Region2Ops.Boolean(a, b, Bool2.Union).Area, i = Region2Ops.Boolean(a, b, Bool2.Intersect).Area;
                double s = Region2Ops.Boolean(a, b, Bool2.Subtract).Area;
                double e1 = Math.Abs(u - (a.Area + b.Area - i)), e2 = Math.Abs(s - (a.Area - i));
                worst = Math.Max(worst, Math.Max(e1, e2)); n++;
            }
            Log(new { test = "G11-2D-IDENTITIES", pairs = n, worst });
            Require(worst < 1e-8, $"worst identity error {worst}");
        });
    }
}

static partial class Program
{
    // Аналитическое ядро, этап 1: распознавание 2.5-мерных тел и их сечения по слоям.
    static void Prism25Checks()
    {
        void Body(string name, Func<Parametric3D.Kernel.Topology.TSolid> make, double? formula, bool expectHeal)
        {
            Check(name, () =>
            {
                var b = make();
                var pr = Parametric3D.Kernel.Analytic.Prism25.Recognize(b, out var why);
                Require(pr != null, "not recognised: " + why);
                double? av = Parametric3D.Kernel.Topology.AnalyticVolume.Of(b);
                Log(new { test = name, levels = pr!.Levels.Length, prism = pr.Volume, analytic = av, formula, heal = pr.HealShift,
                          errFormula = formula == null ? (double?)null : pr.Volume - formula.Value, errAnalytic = av == null ? (double?)null : pr.Volume - av.Value });
                if (formula != null) Require(Math.Abs(pr.Volume - formula.Value) < 1e-9 * formula.Value, $"prism {pr.Volume} vs formula {formula}");
                else if (av != null) Require(Math.Abs(pr.Volume - av.Value) < 1e-6 * Math.Abs(av.Value), $"prism {pr.Volume} vs analytic {av}");
                // Лечение гранения — сдвиг больше шага сетки точного ядра (1e-5 мм); округление до сетки — не лечение.
                Require(expectHeal ? pr.HealShift > 1e-5 : pr.HealShift <= 1e-5, $"heal shift {pr.HealShift}");
            });
        }
        // Вал R10 × 60 с двумя продольными пазами 4 × 4 на x = ±(8…12): вершины стыка паза с окружностью у тела из
        // многогранного пути — на гранях вписанного многогранника; по слоям с лечением — точная формула.
        double r = 10, x0 = 8;
        double F(double y) => 0.5 * (y * Math.Sqrt(r * r - y * y) + r * r * Math.Asin(y / r)) - x0 * y;
        // Предпосылка — тело из МНОГОГРАННОГО пути (вершины на гранях вписанного многогранника).
        Body("G12-PRISM-SLOTTED-SHAFT-HEALED", () => { using var __poly = PolyhedralPath(); return Slotted(new List<string>()); }, (Math.PI * r * r - 2 * (F(2) - F(-2))) * 60, expectHeal: true);
        // Вал с кольцевой проточкой: сечения слоёв — круги разных радиусов.
        Body("G12-PRISM-GROOVED-SHAFT", () => Grooved(new List<string>()), Math.PI * 100 * 60 - Math.PI * (100 - 64) * 4, expectHeal: false);
        // Реалистичные детали (бобышки, карманы, отверстия, цекования) — против аналитического объёма тела.
        foreach (int seed in new[] { 8001, 8002, 8003, 8004 })
            Body($"G12-PRISM-REALISTIC-{seed}", () => { var p = RealisticPart(seed, 20, out _); p.Rebuild(null); return p.Bodies[0]; }, null, expectHeal: false);
    }
}

static partial class Program
{
    // Аналитическое ядро, этап 1: булева 2.5-мерных тел со сборкой B-rep из слоёв — годность и объём против формулы.
    static void PrismBooleanChecks()
    {
        void Case(string name, Func<Parametric3D.Kernel.Topology.TSolid> a, Func<Parametric3D.Kernel.Topology.TSolid> b,
                  Parametric3D.Kernel.Analytic.Bool2 op, double formula)
        {
            Check(name, () =>
            {
                var w = new List<string>();
                var r = Parametric3D.Kernel.Analytic.PrismBuild.Boolean(a(), b(), op, out var why);
                Require(r != null, "declined: " + why);
                Parametric3D.Kernel.Analytic.PrismBuild.Finish(r!, w);
                var v = Parametric3D.Kernel.Topology.SolidValidator.Validate(r!);
                double? av = Parametric3D.Kernel.Topology.AnalyticVolume.Of(r!);
                var curves = r!.Edges.GroupBy(e => e.Curve.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
                var untagged = r.Faces.Count(fc => fc.Tag.StartsWith("analytic:"));
                Log(new { test = name, faces = r.Faces.Count(), edges = r.Edges.Count(), untagged, tags = r.Faces.Select(fc => fc.Tag).OrderBy(t => t).ToArray(), curves, verdict = v.Summary(), analytic = av, formula,
                          err = av == null ? (double?)null : av.Value - formula, warnings = w.Where(x => !x.Contains("meshed from their loops")).Take(3) });
                Require(v.IsValid, v.Summary() + " | " + string.Join(" | ", v.Issues.Where(i => i.Severity == Parametric3D.Kernel.Topology.ValidationSeverity.Error).Select(i => i.ToString()).Distinct().Take(3)));
                Require(av != null && Math.Abs(av.Value - formula) < 1e-9 * formula, $"volume {av} vs formula {formula}");
            });
        }
        var sub = Parametric3D.Kernel.Analytic.Bool2.Subtract; var uni = Parametric3D.Kernel.Analytic.Bool2.Union;
        double PI = Math.PI;
        // Брусок 60×40×20 и сквозное отверстие R5 вдоль Z.
        Case("G13-PRISM-THROUGH-HOLE", () => Box(), () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30), sub, 48000 - PI * 25 * 20);
        // Глухое отверстие R5 глубиной 8 сверху.
        Case("G13-PRISM-BLIND-HOLE", () => Box(), () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, 12), 5, 20), sub, 48000 - PI * 25 * 8);
        // Бобышка R8 высотой 10 сверху.
        Case("G13-PRISM-BOSS", () => Box(), () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, 20), 8, 10), uni, 48000 + PI * 64 * 10);
        // Карман 20×10 глубиной 5.
        Case("G13-PRISM-POCKET", () => Box(), () => Box(10, 10, 15, 20, 10, 10), sub, 48000 - 20 * 10 * 5);
        // Вал R10 × 60 минус два продольных паза.
        double rr = 10, x0 = 8;
        double F(double y) => 0.5 * (y * Math.Sqrt(rr * rr - y * y) + rr * rr * Math.Asin(y / rr)) - x0 * y;
        Case("G13-PRISM-SLOTTED-SHAFT", () =>
        {
            var w = new List<string>(); var s = new List<Parametric3D.Kernel.Topology.TSolid> { Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60) };
            var r1 = Parametric3D.Kernel.Analytic.PrismBuild.Boolean(s[0], Box(8, -2, -1, 4, 4, 62), sub, out var why1)
                     ?? throw new Exception("first slot declined: " + why1);
            Parametric3D.Kernel.Analytic.PrismBuild.Finish(r1, w);
            return r1;
        }, () => Box(-12, -2, -1, 4, 4, 62), sub, (PI * rr * rr - 2 * (F(2) - F(-2))) * 60);
        // Вал минус кольцевая проточка (резец — результат булевой «цилиндр R12 − цилиндр R8»).
        Case("G13-PRISM-GROOVED-SHAFT", () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60), () =>
        {
            var w = new List<string>();
            var ring = Parametric3D.Kernel.Analytic.PrismBuild.Boolean(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, 28), 12, 4),
                                                                      Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, 27), 8, 6), sub, out _)!;
            Parametric3D.Kernel.Analytic.PrismBuild.Finish(ring, w);
            return ring;
        }, sub, PI * 100 * 60 - PI * (100 - 64) * 4);
    }
}

static partial class Program
{
    /// <summary>
    /// Закрепить многогранный путь булевой на время проверки: проверки его этапов (внедрение отказов, отмена), его
    /// точной формы и тождеств по многогранным объёмам аналитический маршрут законно обходит.
    /// </summary>
    static IDisposable PolyhedralPath()
    {
        // Вне контекста — временная смена глобального значения по умолчанию (проверки отмены требуют, чтобы после
        // них не оставалось активного контекста, а некоторые сами меняют глобальные настройки, что внутри контекста
        // запрещено); внутри контекста — свой контекст.
        if (Parametric3D.Kernel.KernelContext.Current == null)
        {
            bool prev = Parametric3D.Kernel.Ops.BooleanOp.AnalyticPrism;
            Parametric3D.Kernel.Ops.BooleanOp.AnalyticPrism = false;
            return new Restore(() => Parametric3D.Kernel.Ops.BooleanOp.AnalyticPrism = prev);
        }
        return new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticPrism = false }).Enter();
    }
    sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>
    /// Сферы — многогранным путём (как до 2026-10-09, ворота g.14): для проверок, написанных на многогранный маршрут шара
    /// (этапы boolean.exact / boolean.topology, точная многогранная форма, лимит многоугольников).
    /// </summary>
    static IDisposable SpheresPolyhedral() =>
        new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticSpheres = false }).Enter();

    static IDisposable AnalyticPath() =>
        new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticPrism = true }).Enter();

    /// <summary>Контракт аналитического маршрута: внедрённый сбой, отмена, состояние представлений, тождество объёмов.</summary>
    static void AnalyticRouteContract()
    {
        var sub = Parametric3D.Kernel.Ops.BooleanOp.BooleanKind.Subtract;
        Parametric3D.Kernel.Topology.TSolid Hole() => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30);

        Check("G14-ANALYTIC-ROUTE-USED", () =>
        {
            using var _a = AnalyticPath();
            var r = Parametric3D.Kernel.Ops.BooleanOp.Evaluate(new[] { Box() }, Hole(), sub, Parametric3D.Kernel.OperationPolicy.Strict);
            Log(new { test = "G14-ANALYTIC-ROUTE-USED", r.Status, r.Guarantee, faces = r.Bodies.Single().Faces.Count() });
            Require(r.Status == Parametric3D.Kernel.OperationStatus.Succeeded && r.Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic, $"{r.Status} {r.Guarantee}");
        });

        Check("G14-ANALYTIC-FAULT-FALLS-BACK", () =>
        {
            // Внутренний сбой маршрута (внедрён на этапе «boolean.analytic») — отказ маршрута, результат многогранного пути.
            // проверяется маршрут ЭТАПА 1: общая булева (этап 2) отключена — иначе отказ уходил бы на неё, а не на многогранный путь
            using var _a = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticPrism = true, AnalyticGeneral = false }).Enter();
            using var _f = Parametric3D.Kernel.FaultInjection.Arm("boolean.analytic");
            var r = Parametric3D.Kernel.Ops.BooleanOp.Evaluate(new[] { Box() }, Hole(), sub, Parametric3D.Kernel.OperationPolicy.Strict);
            Log(new { test = "G14-ANALYTIC-FAULT-FALLS-BACK", r.Status, r.Guarantee });
            Require(r.Status == Parametric3D.Kernel.OperationStatus.Succeeded && r.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic, $"{r.Status} {r.Guarantee}");
        });

        Check("G14-ANALYTIC-CANCEL", () =>
        {
            // Отмена на этапе «boolean.analytic» пробрасывается — операция отменена, а не перешла на многогранный путь.
            using var cts = new CancellationTokenSource(); cts.Cancel();
            using var _c = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticPrism = true }, cancellation: cts.Token).Enter();
            bool cancelled = false;
            try { Parametric3D.Kernel.Ops.BooleanOp.Evaluate(new[] { Box() }, Hole(), sub, Parametric3D.Kernel.OperationPolicy.Strict); }
            catch (Exception ex) when (Parametric3D.Kernel.KernelContext.IsCancellation(ex)) { cancelled = true; }
            Require(cancelled, "the operation was not cancelled");
        });

        Check("G14-ANALYTIC-STATE", () =>
        {
            // У результата аналитического пути авторитетен B-rep, точная форма — производная.
            using var _a = AnalyticPath();
            var r = Parametric3D.Kernel.Ops.BooleanOp.Evaluate(new[] { Box() }, Hole(), sub, Parametric3D.Kernel.OperationPolicy.Strict).Bodies.Single();
            var st = r.Representations;
            Log(new { test = "G14-ANALYTIC-STATE", state = st.ToString() });
            // Производная многогранная форма — ленивая: у тела её нет, по требованию многогранного пути она строится и
            // согласована с аналитическим объёмом (до погрешности гранения).
            Require(r.Exact == null && !r.ExactAuthoritative && r.LazyDerivedExact != null, st.ToString());
            var ex = Parametric3D.Kernel.Exact.ExactAdapter.ToExact(r, new Parametric3D.Kernel.Exact.PlaneRegistry(), null);
            double vex = ex.Solid.VolumeMm3().ToDouble(), van = Parametric3D.Kernel.Topology.AnalyticVolume.Of(r)!.Value;
            Log(new { test = "G14-ANALYTIC-STATE-LAZY-EXACT", vex, van, rel = Math.Abs(vex - van) / van });
            Require(Math.Abs(vex - van) < 1e-3 * van, $"lazy exact form volume {vex} vs analytic {van}");
        });

        Check("G14-ANALYTIC-NAMES-AND-VOLUME", () =>
        {
            // Деталь «куб → уступ → глухое отверстие поперёк уступа»: с маршрутом уступ — аналитически, отверстие — многогранным
            // путём по ленивой форме. Имена граней (ключ операции — её номер в дереве) и объём — как без маршрута.
            (string[] Names, double Volume) Build(bool on)
            {
                using var _c = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticPrism = on }).Enter();
                var p = new Parametric3D.Model.Part("step"); Parametric3D.Model.TestGeometry.AddCube(p, 60);
                var cut = new Parametric3D.Model.Features.ExtrudeFeature("Step") { SketchId = RectSketch(p, 30, 0, -30, 30, 60).Id, Cut = true };
                cut.Side1.Distance = new Parametric3D.Model.Features.ValueRef(2); cut.Side1.Flip = true; p.AddFeature(cut);
                var h = new Parametric3D.Model.Features.HoleFeature("H") { Direction = new Vector3d(0, 0, -1), ThroughAll = false, Diameter = new Parametric3D.Model.Features.ValueRef(6), Depth = new Parametric3D.Model.Features.ValueRef(30) };
                h.Points.Add(new Vector3d(-1.3, 0.7, 30)); p.AddFeature(h); p.Rebuild(null);
                var keys = p.Features.Select((f, i) => (f.Key, i)).ToList();
                string Norm(string t) { foreach (var (k, i) in keys) t = t.Replace("#" + k + "/", $"#F{i}/"); return t; }
                return (p.Bodies.Single().Faces.Select(f => Norm(f.Tag)).OrderBy(x => x, StringComparer.Ordinal).ToArray(), p.Volume);
            }
            var off = Build(false); var on = Build(true);
            Log(new { test = "G14-ANALYTIC-NAMES-AND-VOLUME", namesOff = off.Names, namesOn = on.Names, volOff = off.Volume, volOn = on.Volume });
            Require(off.Names.SequenceEqual(on.Names), "face names differ with the analytic route");
            Require(Math.Abs(off.Volume - on.Volume) < 1e-6 * off.Volume, $"volume {on.Volume} with the route vs {off.Volume} without");
        });

        Check("G14-ANALYTIC-INTERSECT-IDENTITY", () =>
        {
            // vol(A ∩ B) = vol(A) − vol(A − B) по аналитическим объёмам: A — брусок с отверстием аналитическим путём.
            using var _a = AnalyticPath();
            var w = new List<string>();
            var A = new List<Parametric3D.Kernel.Topology.TSolid> { Box() }; Parametric3D.Kernel.Ops.BooleanOp.Subtract(A, Hole(), w);
            var B = Box(20, 10, 5, 70, 20, 30);
            var i = new List<Parametric3D.Kernel.Topology.TSolid> { A[0] }; Parametric3D.Kernel.Ops.BooleanOp.Intersect(i, B, w);
            var d = new List<Parametric3D.Kernel.Topology.TSolid> { A[0] }; Parametric3D.Kernel.Ops.BooleanOp.Subtract(d, B, w);
            double va = Parametric3D.Kernel.Topology.AnalyticVolume.Of(A[0])!.Value, vi = Parametric3D.Kernel.Topology.AnalyticVolume.Of(i[0])!.Value, vd = Parametric3D.Kernel.Topology.AnalyticVolume.Of(d[0])!.Value;
            Log(new { test = "G14-ANALYTIC-INTERSECT-IDENTITY", va, vi, vd, err = vi - (va - vd), guarantees = new[] { A[0].Guarantee, i[0].Guarantee, d[0].Guarantee } });
            Require(Math.Abs(vi - (va - vd)) < 1e-9 * va, $"{vi} vs {va - vd}");
        });
    }
}

static partial class Program
{
    // Аналитическое ядро, этап 2, блок 1: классификатор точки по точным поверхностям — против истины по формулам CSG.
    static void ClassifierChecks()
    {
        void Case(string name, Func<Parametric3D.Kernel.Topology.TSolid> make, Func<Vector3d, double> sdf, Vector3d lo, Vector3d hi)
        {
            Check(name, () =>
            {
                var body = make();
                var cl = new Parametric3D.Kernel.Analytic.Classifier(body);
                Require(cl.Unsupported == null, "unsupported: " + cl.Unsupported);
                var rnd = new Random(15); int n = 0, skipped = 0, wrong = 0, unknown = 0; string? first = null;
                for (int i = 0; i < 10000; i++)
                {
                    var p = new Vector3d(lo.X + (hi.X - lo.X) * rnd.NextDouble(), lo.Y + (hi.Y - lo.Y) * rnd.NextDouble(), lo.Z + (hi.Z - lo.Z) * rnd.NextDouble());
                    double s = sdf(p);                         // < 0 внутри; |s| — расстояние до ближайшей поверхности (оценка снизу)
                    if (Math.Abs(s) < 1e-4) { skipped++; continue; }
                    var c = cl.Classify(p); n++;
                    if (c == null) { unknown++; continue; }
                    if ((c == 1) != (s < 0)) { wrong++; first ??= $"{p} sdf {s} got {c}"; }
                }
                Log(new { test = name, faces = body.Faces.Count(), points = n, skipped, wrong, unknown, first });
                Require(wrong == 0 && unknown == 0, $"{wrong} wrong, {unknown} unknown; first {first}");
            });
        }
        // Знаковые расстояния примитивов (точно для бруска и цилиндра; объединение — min, разность — max(a, −b)).
        static double BoxS(Vector3d p, Vector3d lo, Vector3d hi) { var q = Vector3d.ComponentMax(lo - p, p - hi); return Math.Max(q.X, Math.Max(q.Y, q.Z)) <= 0 ? Math.Max(q.X, Math.Max(q.Y, q.Z)) : Vector3d.ComponentMax(q, Vector3d.Zero).Length; }
        static double CylS(Vector3d p, Vector3d c, double r, double z0, double z1) { double radial = (p.Xy - c.Xy).Length - r, axial = Math.Max(z0 - p.Z, p.Z - z1); return radial <= 0 && axial <= 0 ? Math.Max(radial, axial) : new Vector2d(Math.Max(radial, 0), Math.Max(axial, 0)).Length; }
        var sub = Parametric3D.Kernel.Analytic.Bool2.Subtract; var uni = Parametric3D.Kernel.Analytic.Bool2.Union;
        Parametric3D.Kernel.Topology.TSolid Prism(Parametric3D.Kernel.Topology.TSolid a, Parametric3D.Kernel.Topology.TSolid b, Parametric3D.Kernel.Analytic.Bool2 op)
        { var r = Parametric3D.Kernel.Analytic.PrismBuild.Boolean(a, b, op, out var why) ?? throw new Exception(why); Parametric3D.Kernel.Analytic.PrismBuild.Finish(r, null); return r; }
        Case("G15-CLASSIFY-BOX", () => Box(), p => BoxS(p, Vector3d.Zero, new(60, 40, 20)), new(-5, -5, -5), new(65, 45, 25));
        Case("G15-CLASSIFY-THROUGH-HOLE", () => Prism(Box(), Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30), sub),
             p => Math.Max(BoxS(p, Vector3d.Zero, new(60, 40, 20)), -CylS(p, new(30, 20, 0), 5, -5, 25)), new(-5, -5, -5), new(65, 45, 25));
        Case("G15-CLASSIFY-BOSS", () => Prism(Box(), Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, 20), 8, 10), uni),
             p => Math.Min(BoxS(p, Vector3d.Zero, new(60, 40, 20)), CylS(p, new(30, 20, 0), 8, 20, 30)), new(-5, -5, -5), new(65, 45, 35));
        Case("G15-CLASSIFY-BLIND-HOLE", () => Prism(Box(), Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, 12), 5, 20), sub),
             p => Math.Max(BoxS(p, Vector3d.Zero, new(60, 40, 20)), -CylS(p, new(30, 20, 0), 5, 12, 32)), new(-5, -5, -5), new(65, 45, 25));
        Case("G15-CLASSIFY-SHAFT", () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60),
             p => CylS(p, Vector3d.Zero, 10, 0, 60), new(-12, -12, -2), new(12, 12, 62));
        // Честный отказ: тело со сплайновой гранью (поверхность вне классификатора) — «не знаю», а не догадка. Прежде здесь
        // было скругление кромки отверстия (тор): с 2026-10-11 тор классифицируется (P3D_ANALYTICTORUS), а скругление строится
        // прямым построителем с точной лентой-тором — его классификация проверяется ниже по знаковому расстоянию.
        Check("G15-CLASSIFY-UNSUPPORTED", () =>
        {
            var b = SplinePrism(new Vector3d(0, 0, 0), 12, 7, 10);
            var cl = new Parametric3D.Kernel.Analytic.Classifier(b);
            Log(new { test = "G15-CLASSIFY-UNSUPPORTED", unsupported = cl.Unsupported });
            Require(cl.Unsupported != null && cl.Classify(new Vector3d(1, 2, 3)) == null, "a spline body was classified");
        });
        // Кромка сквозного отверстия R8 скруглена R2 (тор): материал — брусок без цилиндра и без «щеки» у кромки.
        Case("G15-CLASSIFY-ROUND-HOLE-TORUS", () => Scenario("S-ROUNDHOLE", new List<string>())[0], p =>
        {
            double R = 8, r = 2, H = 20; var c = new Vector3d(30, 20, 0);
            double rho = (p.Xy - c.Xy).Length, sBox = BoxS(p, Vector3d.Zero, new(60, 40, 20)), sHole = R - rho;   // > 0 — в отверстии
            // щека: ρ ∈ [R, R+r], z ∈ [H−r, H], вне окружности центра (R+r, H−r)
            double dTor = new Vector2d(rho - (R + r), p.Z - (H - r)).Length - r;
            bool inCheek = rho >= R && rho <= R + r && p.Z >= H - r && p.Z <= H && dTor > 0;
            bool inside = sBox < 0 && sHole < 0 && !inCheek;
            double dist = Math.Min(Math.Abs(sBox), Math.Min(Math.Abs(sHole), Math.Abs(dTor)));
            return inside ? -dist : dist;
        }, new(-5, -5, -5), new(65, 45, 25));
        // Этап 2б: брусок со сферическим вырезом R8 (центр на верхней грани) — сферическая грань (стереографические параметры).
        Case("G15-CLASSIFY-SPHERE-CUT", () =>
        {
            var w = new List<string>(); var b = new List<Parametric3D.Kernel.Topology.TSolid> { Box() };
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, Parametric3D.Kernel.Ops.PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), w); return b[0];
        }, p => Math.Max(BoxS(p, Vector3d.Zero, new(60, 40, 20)), -((p - new Vector3d(30, 20, 20)).Length - 8)), new(-5, -5, -5), new(65, 45, 25));
        // Этап 2б: брусок с коническим вырезом (вершина (30, 20, 10), угол 45°, через верхнюю грань).
        Case("G15-CLASSIFY-CONE-CUT", () =>
        {
            var w = new List<string>(); var b = new List<Parametric3D.Kernel.Topology.TSolid> { Box() };
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, Parametric3D.Kernel.Ops.PrimitiveOp.Cone(new Vector3d(30, 20, 10), 0, 15, 15), w); return b[0];
        }, p =>
        {
            double radial = (p.Xy - new Vector2d(30, 20)).Length, z = p.Z;
            double cone = Math.Max(Math.Max((radial - (z - 10)) / Math.Sqrt(2), 10 - z), z - 25);
            return Math.Max(BoxS(p, Vector3d.Zero, new(60, 40, 20)), -cone);
        }, new(-5, -5, -5), new(65, 45, 25));
        // Вал с двумя продольными пазами: частичные цилиндрические грани с вертикальными рёбрами.
        Case("G15-CLASSIFY-SLOTTED-SHAFT", () => Slotted(new List<string>()),
             p => Math.Max(Math.Max(CylS(p, Vector3d.Zero, 10, 0, 60), -BoxS(p, new(8, -2, -1), new(12, 2, 61))), -BoxS(p, new(-12, -2, -1), new(-8, 2, 61))),
             new(-12, -12, -2), new(12, 12, 62));
        // Повёрнутое тело: брусок со сквозным отверстием, повёрнутый вокруг (1, 1, 1) на 0.37 рад (истина — в исходных координатах).
        {
            var axis = new Vector3d(1, 1, 1).Normalized(); double ang = 0.37;
            var xf = Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, axis, ang * 180 / Math.PI);
            var inv = Matrix3d.CreateFromAxisAngle(axis, ang);   // поворот назад: R(ang)ᵀ = R(−ang) для точек-строк
            Vector3d Back(Vector3d p) => new Vector3d(Vector3d.Dot(new Vector3d(inv.Row0), p), Vector3d.Dot(new Vector3d(inv.Row1), p), Vector3d.Dot(new Vector3d(inv.Row2), p));
            Case("G15-CLASSIFY-ROTATED", () => Parametric3D.Kernel.Ops.TransformOp.Copy(Prism(Box(), Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30), sub), xf),
                 p => { var q = Back(p); return Math.Max(BoxS(q, Vector3d.Zero, new(60, 40, 20)), -CylS(q, new(30, 20, 0), 5, -5, 25)); },
                 new(-40, -40, -40), new(80, 80, 80));
        }
        // Наклонное отверстие многогранным путём: на гранях — кривые пересечения и восстановленные дуги (запасные ломаные).
        {
            var dirH = new Vector3d(0.3, 0.2, 1).Normalized();
            Case("G15-CLASSIFY-TILTED-HOLE", () =>
            {
                var w = new List<string>(); var b = new List<Parametric3D.Kernel.Topology.TSolid> { Box() };
                var z = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, -20), 4, 40);
                var ax = Vector3d.Cross(Vector3d.UnitZ, dirH).Normalized(); double an = Math.Acos(dirH.Z) * 180 / Math.PI;
                var rot = Parametric3D.Kernel.Ops.TransformOp.Copy(z, Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, ax, an));
                var tool = Parametric3D.Kernel.Ops.TransformOp.Copy(rot, Parametric3D.Kernel.Ops.TransformOp.Move(new Vector3d(30, 20, 10)));
                Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, tool, w); return b[0];
            }, p =>
            {
                var c0 = new Vector3d(30, 20, 10); var q = p - c0; double h = Vector3d.Dot(q, dirH); double radial = (q - dirH * h).Length - 4;
                return Math.Max(BoxS(p, Vector3d.Zero, new(60, 40, 20)), -Math.Max(radial, Math.Abs(h) - 20));
            }, new(-5, -5, -5), new(65, 45, 25));
        }
    }
}

static partial class Program
{
    // Аналитическое ядро, этап 2, блок 2: пересечение двух граней — куски кривой внутри обеих, против точной истины.
    static void FaceIntersectChecks()
    {
        Parametric3D.Kernel.Analytic.AnalyticFace AF(Parametric3D.Kernel.Topology.TFace f) => Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out var why) ?? throw new Exception(why);
        Parametric3D.Kernel.Topology.TFace FaceWhere(Parametric3D.Kernel.Topology.TSolid s, Func<Parametric3D.Kernel.Geometry.Ssi.Shape, bool> pred) =>
            s.Faces.First(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)) is { } sh && pred(sh));
        static double Len(Parametric3D.Kernel.Analytic.FaceIntersect.Piece p)
        { double l = 0; var prev = p.Curve.PointAt(p.T0); for (int i = 1; i <= 200000; i++) { var q = p.Curve.PointAt(p.T0 + (p.T1 - p.T0) * i / 200000); l += (q - prev).Length; prev = q; } return l; }
        bool TopPlane(Parametric3D.Kernel.Geometry.Ssi.Shape sh, double z) => sh is Parametric3D.Kernel.Geometry.Ssi.Plane pl && Math.Abs(Math.Abs(pl.N.Normalized().Z) - 1) < 1e-12 && Math.Abs(pl.P.Z - z) < 1e-9;
        bool IsCyl(Parametric3D.Kernel.Geometry.Ssi.Shape sh) => sh is Parametric3D.Kernel.Geometry.Ssi.Cylinder;

        void Case(string name, Parametric3D.Kernel.Topology.TFace fa, Parametric3D.Kernel.Topology.TFace fb, int pieces, double length, Func<Vector3d, bool>? endOk = null, bool? closed = null)
        {
            Check(name, () =>
            {
                var r = Parametric3D.Kernel.Analytic.FaceIntersect.Of(AF(fa), AF(fb));
                double L = r.Pieces.Sum(Len);
                Log(new { test = name, kind = r.Kind.ToString(), pieces = r.Pieces.Count, length = L, expected = length, err = L - length,
                          ends = r.Pieces.Select(p => $"{p.P0} {p.P1}").ToArray(), closed = r.Pieces.Select(p => p.Closed).ToArray() });
                Require(r.Pieces.Count == pieces, $"pieces {r.Pieces.Count} vs {pieces}");
                if (pieces > 0) Require(Math.Abs(L - length) < 1e-6 * Math.Max(1, length), $"length {L} vs {length}");
                if (endOk != null) Require(r.Pieces.All(p => p.Closed || (endOk(p.P0) && endOk(p.P1))), "an end is not on the expected boundary");
                if (closed != null) Require(r.Pieces.All(p => p.Closed == closed), "closedness");
            });
        }
        var box = Box();
        var top = FaceWhere(box, sh => TopPlane(sh, 20));
        // 1. Верх бруска × стенка отверстия R5 в середине — полная окружность.
        var hole = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30);
        Case("G16-FACEX-TOP-HOLE-CIRCLE", top, FaceWhere(hole, IsCyl), 1, 2 * Math.PI * 5, closed: true);
        // 2. Отверстие у края x = 60 (центр x = 58): дуга внутри грани, концы — на x = 60.
        var edgeHole = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(58, 20, -5), 5, 30);
        Case("G16-FACEX-TOP-EDGE-HOLE-ARC", top, FaceWhere(edgeHole, IsCyl), 1, 5 * (2 * Math.PI - 2 * Math.Acos(0.4)), p => Math.Abs(p.X - 60) < 1e-9, closed: false);
        // 3. Верх × наклонный цилиндр R4 (ось (0.3, 0.2, 1)) — эллипс целиком в грани; периметр — численно.
        {
            var dirH = new Vector3d(0.3, 0.2, 1).Normalized();
            var z = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, -20), 4, 40);
            var ax = Vector3d.Cross(Vector3d.UnitZ, dirH).Normalized(); double an = Math.Acos(dirH.Z) * 180 / Math.PI;
            var tilted = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.TransformOp.Copy(z, Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, ax, an)), Parametric3D.Kernel.Ops.TransformOp.Move(new Vector3d(30, 20, 10)));
            double A = 4 / dirH.Z, B = 4, per = 0; int N = 2000000;
            for (int i = 0; i < N; i++) { double t = 2 * Math.PI * (i + 0.5) / N; per += Math.Sqrt(A * A * Math.Sin(t) * Math.Sin(t) + B * B * Math.Cos(t) * Math.Cos(t)) * 2 * Math.PI / N; }
            Case("G16-FACEX-TOP-TILTED-ELLIPSE", top, FaceWhere(tilted, IsCyl), 1, per, closed: true);
        }
        // 4. Плоскость × плоскость: верх (z = 20, y 0..40) × грань x = 10 бруска-резца (y −5..50, z 15..30) — отрезок длиной 40.
        {
            var tool = Box(10, -5, 15, 10, 55, 15);
            var side = FaceWhere(tool, sh => sh is Parametric3D.Kernel.Geometry.Ssi.Plane pl && Math.Abs(Math.Abs(pl.N.Normalized().X) - 1) < 1e-12 && Math.Abs(pl.P.X - 10) < 1e-9);
            Case("G16-FACEX-PLANE-PLANE-SEGMENT", top, side, 1, 40, p => Math.Abs(p.Y) < 1e-9 || Math.Abs(p.Y - 40) < 1e-9, closed: false);
        }
        // 6. Вал R10 (ось Z) × поперечное отверстие R3 (ось X, z = 30): кривая 4-й степени (трассировка Ssi) — два
        // замкнутых куска (вход и выход), каждая точка — на обеих поверхностях и внутри обеих граней.
        Check("G16-FACEX-CROSS-HOLE-QUARTIC", () =>
        {
            var shaft = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60);
            var z = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, -15), 3, 30);
            var cross = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.TransformOp.Copy(z, Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)), Parametric3D.Kernel.Ops.TransformOp.Move(new Vector3d(0, 0, 30)));
            var fa = AF(FaceWhere(shaft, IsCyl)); var fb = AF(FaceWhere(cross, IsCyl));
            var r = Parametric3D.Kernel.Analytic.FaceIntersect.Of(fa, fb);
            double worstSurf = 0; int outside = 0, samples = 0;
            foreach (var p in r.Pieces)
                for (int i = 0; i <= 400; i++)
                {
                    var x = p.Curve.PointAt(p.T0 + (p.T1 - p.T0) * i / 400); samples++;
                    worstSurf = Math.Max(worstSurf, Math.Max(fa.SurfaceDistance(x), fb.SurfaceDistance(x)));
                    if (fa.SignedBoundaryDistance(x) < 0 || fb.SignedBoundaryDistance(x) < 0) outside++;
                }
            var ssiR = Parametric3D.Kernel.Geometry.Ssi.Intersect(fa.Shape, fb.Shape, new Vector3d(0, 0, 30), 40);
            var gaps = ssiR.Curves.Select(c => (c.PointAt(c.T0) - c.PointAt(c.T1)).Length).ToArray();
            Log(new { test = "G16-FACEX-CROSS-HOLE-QUARTIC", gaps, kind = r.Kind.ToString(), pieces = r.Pieces.Count, closed = r.Pieces.Select(p => p.Closed).ToArray(), curves = r.Pieces.Select(p => p.Curve.GetType().Name).ToArray(), worstSurf, outside, samples });
            Require(r.Pieces.Count == 2 && r.Pieces.All(p => p.Closed), $"pieces {r.Pieces.Count}");
            Require(worstSurf < 1e-9 && outside == 0, $"off surface {worstSurf}, outside {outside}");
        });
        // 5. Непересекающиеся грани — пусто.
        Case("G16-FACEX-DISJOINT", top, FaceWhere(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(200, 20, -5), 5, 30), IsCyl), 0, 0);
    }
}

static partial class Program
{
    // Аналитическое ядро, этап 2, блок 3: разбиение грани кусками пересечения — площади областей против точной истины.
    static void FaceSplitChecks()
    {
        Parametric3D.Kernel.Analytic.AnalyticFace AF(Parametric3D.Kernel.Topology.TFace f) => Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out var why) ?? throw new Exception(why);
        Parametric3D.Kernel.Topology.TFace FaceWhere(Parametric3D.Kernel.Topology.TSolid s, Func<Parametric3D.Kernel.Geometry.Ssi.Shape, bool> pred) =>
            s.Faces.First(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)) is { } sh && pred(sh));
        bool PlaneZ(Parametric3D.Kernel.Geometry.Ssi.Shape sh, double z) => sh is Parametric3D.Kernel.Geometry.Ssi.Plane pl && Math.Abs(Math.Abs(pl.N.Normalized().Z) - 1) < 1e-12 && Math.Abs(pl.P.Z - z) < 1e-9;
        bool PlaneX(Parametric3D.Kernel.Geometry.Ssi.Shape sh, double x) => sh is Parametric3D.Kernel.Geometry.Ssi.Plane pl && Math.Abs(Math.Abs(pl.N.Normalized().X) - 1) < 1e-12 && Math.Abs(pl.P.X - x) < 1e-9;
        bool IsCyl(Parametric3D.Kernel.Geometry.Ssi.Shape sh) => sh is Parametric3D.Kernel.Geometry.Ssi.Cylinder;

        void Case(string name, Parametric3D.Kernel.Topology.TFace face, IEnumerable<Parametric3D.Kernel.Topology.TFace> cutters, double[] areas, double tol = 1e-6)
        {
            Check(name, () =>
            {
                var af = AF(face);
                var pieces = cutters.SelectMany(c => Parametric3D.Kernel.Analytic.FaceIntersect.Of(af, AF(c)).Pieces).ToList();
                var regs = Parametric3D.Kernel.Analytic.FaceSplit.Split(af, pieces, out var why);
                Require(regs != null, "split failed: " + why);
                var got = regs!.Select(r => r.Area).OrderBy(x => x).ToArray(); var exp = areas.OrderBy(x => x).ToArray();
                bool insideOk = regs.All(r => af.SurfaceDistance(r.Inside) < 1e-9 && af.Contains(r.Inside) == true);
                Log(new { test = name, pieces = pieces.Count, regions = got, expected = exp, loops = regs.Select(r => r.Loops.Count).ToArray(), insideOk });
                Require(got.Length == exp.Length, $"regions {got.Length} vs {exp.Length}");
                for (int i = 0; i < got.Length; i++) Require(Math.Abs(got[i] - exp[i]) < tol * Math.Max(1, exp[i]), $"area {got[i]} vs {exp[i]}");
                Require(insideOk, "an interior point is off the face");
            });
        }
        var box = Box(); var top = FaceWhere(box, sh => PlaneZ(sh, 20));
        double PI = Math.PI;
        // Площади по ломаной (шаг — 4 на мм, не реже 16 на кусок): допуск на прогиб ломаной у дуг — 1e-3 относительно.
        Case("G17-SPLIT-TOP-CIRCLE", top, new[] { FaceWhere(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30), IsCyl) }, new[] { 2400 - 25 * PI, 25 * PI }, 1e-3);
        double seg = 25 * Math.Acos(0.4) - 2 * Math.Sqrt(21), inner = 25 * PI - seg;
        Case("G17-SPLIT-TOP-EDGE-ARC", top, new[] { FaceWhere(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(58, 20, -5), 5, 30), IsCyl) }, new[] { 2400 - inner, inner }, 1e-3);
        {
            var t1 = Box(10, -5, 15, 10, 55, 15);
            Case("G17-SPLIT-TOP-TWO-LINES", top, new[] { FaceWhere(t1, sh => PlaneX(sh, 10)), FaceWhere(t1, sh => PlaneX(sh, 20)) }, new[] { 400.0, 400, 1600 });
        }
        var shaft = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60);
        var wall = FaceWhere(shaft, IsCyl);
        Case("G17-SPLIT-SHAFT-BAND", wall, new[] { FaceWhere(Box(-20, -20, 30, 40, 40, 10), sh => PlaneZ(sh, 30)) }, new[] { 2 * PI * 10 * 30, 2 * PI * 10 * 30 }, 1e-3);
        Check("G17-SPLIT-SHAFT-CROSS-HOLE", () =>
        {
            var z = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, -15), 3, 30);
            var cross = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.TransformOp.Copy(z, Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)), Parametric3D.Kernel.Ops.TransformOp.Move(new Vector3d(0, 0, 30)));
            var af = AF(wall);
            var pieces = Parametric3D.Kernel.Analytic.FaceIntersect.Of(af, AF(FaceWhere(cross, IsCyl))).Pieces;
            var regs = Parametric3D.Kernel.Analytic.FaceSplit.Split(af, pieces, out var why);
            Require(regs != null, "split failed: " + why);
            var a = regs!.Select(r => r.Area).OrderBy(x => x).ToArray();
            Log(new { test = "G17-SPLIT-SHAFT-CROSS-HOLE", regions = a, total = a.Sum(), expectedTotal = 2 * PI * 10 * 60 });
            Require(a.Length == 3 && Math.Abs(a.Sum() - 2 * PI * 10 * 60) < 1e-3 * 2 * PI * 10 * 60 && Math.Abs(a[0] - a[1]) < 1e-3 * a[0], $"regions {string.Join(",", a)}");
        });
    }
}

static partial class Program
{
    // Аналитическое ядро, этап 2, блок 4: общая аналитическая булева — годность и объём против формулы.
    static void GeneralBooleanChecks()
    {
        Parametric3D.Kernel.Topology.TSolid Tilted(Vector3d at, Vector3d dir, double r, double half)
        {
            var z = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, -half), r, 2 * half);
            var ax = Vector3d.Cross(Vector3d.UnitZ, dir); double an = Math.Acos(Math.Clamp(dir.Normalized().Z, -1, 1)) * 180 / Math.PI;
            var rot = ax.Length < 1e-12 ? z : Parametric3D.Kernel.Ops.TransformOp.Copy(z, Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, ax.Normalized(), an));
            return Parametric3D.Kernel.Ops.TransformOp.Copy(rot, Parametric3D.Kernel.Ops.TransformOp.Move(at));
        }
        void Case(string name, Func<Parametric3D.Kernel.Topology.TSolid> a, Func<Parametric3D.Kernel.Topology.TSolid> b, Parametric3D.Kernel.Analytic.Bool2 op, double formula, double tol = 1e-9)
        {
            Check(name, () =>
            {
                var r = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(a(), b(), op, out var why);
                Require(r != null, "declined: " + why);
                int failed = Parametric3D.Kernel.Analytic.PrismBuild.Finish(r!, null);
                var v = Parametric3D.Kernel.Topology.SolidValidator.Validate(r!);
                double? av = Parametric3D.Kernel.Topology.AnalyticVolume.Of(r!);
                Log(new { test = name, faces = r!.Faces.Count(), edges = r.Edges.Count(), curves = r.Edges.GroupBy(e => e.Curve.GetType().Name).ToDictionary(g => g.Key, g => g.Count()),
                          unmeshed = failed, verdict = v.Summary(), analytic = av, formula, err = av - formula });
                Require(failed == 0, $"{failed} face(s) not meshed");
                Require(v.IsValid, v.Summary() + " | " + string.Join(" | ", v.Issues.Where(i => i.Severity == Parametric3D.Kernel.Topology.ValidationSeverity.Error).Select(i => i.ToString()).Distinct().Take(3)));
                Require(av != null && Math.Abs(av.Value - formula) < tol * formula, $"volume {av} vs formula {formula}");
            });
        }
        var sub = Parametric3D.Kernel.Analytic.Bool2.Subtract;
        // 1. Наклонное сквозное отверстие R4 (ось (0.3, 0.2, 1)) через брусок 60×40×20: вырез πr²·20/cos α.
        var dirH = new Vector3d(0.3, 0.2, 1).Normalized();
        Case("G18-GENERAL-TILTED-HOLE", () => Box(), () => Tilted(new Vector3d(30, 20, 10), dirH, 4, 25), sub, 48000 - Math.PI * 16 * 20 / dirH.Z);
        // 2. Поперечное отверстие R3 (ось X, z = 30) в валу R10 × 60: вырез ∫ 2√(100 − y²)·2√(9 − y²) dy.
        double cross = 0; int N = 2000000;
        for (int i = 0; i < N; i++) { double y = -3 + 6 * (i + 0.5) / N; cross += 2 * Math.Sqrt(100 - y * y) * 2 * Math.Sqrt(9 - y * y) * 6.0 / N; }
        Case("G18-GENERAL-CROSS-HOLE", () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60), () => Tilted(new Vector3d(0, 0, 30), Vector3d.UnitX, 3, 15), sub, Math.PI * 100 * 60 - cross, 1e-8);
        // Совпадающие поверхности (копланарные грани).
        var uni = Parametric3D.Kernel.Analytic.Bool2.Union;
        Case("G18-GENERAL-COPLANAR-BOSS", () => Box(), () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, 20), 8, 10), uni, 48000 + Math.PI * 64 * 10);
        Case("G18-GENERAL-COPLANAR-FLUSH-POCKET", () => Box(), () => Box(10, 10, 15, 20, 10, 5), sub, 48000 - 20 * 10 * 5);
        Case("G18-GENERAL-COPLANAR-FLUSH-BLIND-HOLE", () => Box(), () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(30, 20, 12), 5, 8), sub, 48000 - Math.PI * 25 * 8);
        Case("G18-GENERAL-COPLANAR-STACK", () => Box(), () => Box(0, 0, 20, 60, 40, 20), uni, 96000);
        Case("G18-GENERAL-COPLANAR-CORNER-NOTCH", () => Box(), () => Box(50, -1, 10, 10, 42, 10), sub, 48000 - 10 * 40 * 10);
        // Этап 2б: сферы.
        Func<Parametric3D.Kernel.Topology.TSolid> Sph(Vector3d c, double r) => () => Parametric3D.Kernel.Ops.PrimitiveOp.Sphere(c, r);
        Case("G18-GENERAL-SPHERE-DIMPLE", () => Box(), Sph(new Vector3d(30, 20, 20), 8), sub, 48000 - 2.0 / 3 * Math.PI * 512, 1e-7);
        Case("G18-GENERAL-SPHERE-DOME", () => Box(), Sph(new Vector3d(30, 20, 20), 8), uni, 48000 + 2.0 / 3 * Math.PI * 512, 1e-7);
        Case("G18-GENERAL-SPHERE-CORNER", () => Box(), Sph(new Vector3d(60, 40, 20), 6), sub, 48000 - 4.0 / 3 * Math.PI * 216 / 8, 1e-7);
        Case("G18-GENERAL-SPHERE-SHAFT-END", () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60), Sph(new Vector3d(0, 0, 60), 5), sub, Math.PI * 100 * 60 - 2.0 / 3 * Math.PI * 125, 1e-7);
        // Этап 2б: конус — зенковка (вершина внутри бруска, раскрытие через верхнюю грань): вырез — конус h = 10, r = 10.
        Case("G18-GENERAL-CONE-COUNTERSINK", () => Box(), () => Parametric3D.Kernel.Ops.PrimitiveOp.Cone(new Vector3d(30, 20, 10), 0, 15, 15), sub, 48000 - Math.PI * 100 * 10 / 3, 1e-7);
        // Усечённый конус-бобышка на верхней грани (объединение): r 8 → 4 на высоте 6.
        Case("G18-GENERAL-CONE-FRUSTUM-BOSS", () => Box(), () => Parametric3D.Kernel.Ops.PrimitiveOp.Cone(new Vector3d(30, 20, 20), 8, 4, 6), uni, 48000 + Math.PI * 6 / 3 * (64 + 32 + 16), 1e-7);
        // 3. Брусок со срезанным ребром (наклонная грань) и вертикальное отверстие вдали от среза.
        Case("G18-GENERAL-CHAMFERED-HOLE", () =>
        {
            var w = new List<string>(); var b = new List<Parametric3D.Kernel.Topology.TSolid> { Box() };
            // срез ребра x = 60, z = 20: брусок, повёрнутый на 45° вокруг Y, с центром на ребре
            var cut = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.TransformOp.Copy(Box(-5, -10, -5, 10, 60, 10), Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 45)), Parametric3D.Kernel.Ops.TransformOp.Move(new Vector3d(60, 0, 20)));
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, cut, w); return b[0];
        }, () => Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(20, 20, -5), 5, 30), sub, 48000 - 1000 /* срез: треугольник с катетами 5√2 (площадь 25) на длине 40 */ - Math.PI * 25 * 20);
    }
}
