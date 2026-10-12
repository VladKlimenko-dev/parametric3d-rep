using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    // ПОСТАВКА 4. КОНТРАКТ ПРИМЕНИМОСТИ ТОЧНЫХ ПУТЕЙ ОТВЕРСТИЯ: на каждый маршрут — случай, где он принят, и трасса
    // решений по предшествующим маршрутам (неприменим / отказался / негоден — с причиной).
    static (Part P, HoleFeature H) HoleInCube(Vector3d at, Vector3d dir, double d, bool through, double depth = 20,
                                             HoleKind kind = HoleKind.Simple)
    {
        var p = new Part("route"); TestGeometry.AddCube(p, 60);            // куб [-30, 30]³
        var h = new HoleFeature("H") { Direction = dir, ThroughAll = through, Diameter = new ValueRef(d), Kind = kind };
        if (!through) h.Depth = new ValueRef(depth);
        if (kind == HoleKind.Counterbore) { h.HeadDiameter = new ValueRef(d + 6); h.HeadDepth = new ValueRef(4); }
        if (kind == HoleKind.Countersink) { h.HeadDiameter = new ValueRef(d + 6); h.HeadAngle = new ValueRef(90); }
        h.Points.Add(at); p.AddFeature(h); p.Rebuild(null);
        return (p, h);
    }
    static string Trace(HoleFeature h) => string.Join(" → ", h.LastRoutes[0].Select(x => $"{x.Route}:{x.Verdict}"));

    static void ExpectRoute(string name, Func<(Part P, HoleFeature H)> build, string expectedTrace, Func<Part, string?>? extra = null)
    {
        Check(name, () =>
        {
            var (p, h) = build();
            Require(h.Error == null && p.Integrity == null, $"build failed: {h.Error} {p.Integrity}");
            string got = Trace(h);
            Log(new { test = name, trace = h.LastRoutes[0].Select(x => x.ToString()), volume = p.Volume });
            Require(got == expectedTrace, $"route trace\n  expected {expectedTrace}\n  got      {got}");
            Require(h.LastRoutes[0].Where(x => x.Verdict != HoleRouteVerdict.Accepted).All(x => !string.IsNullOrEmpty(x.Reason)), "a decision without a reason");
            if (extra?.Invoke(p) is { } why) Require(false, why);
        });
    }

    static void HoleRoutes()
    {
        var down = new Vector3d(0, 0, -1);
        var top = new Vector3d(0, 0, 30);
        double a15 = 15 * Math.PI / 180; var tilt = new Vector3d(Math.Sin(a15), 0, -Math.Cos(a15));
        const string NA = "NotApplicable";

        ExpectRoute("G8-ROUTE-QUADRIC-THROUGH", () => HoleInCube(top, down, 8, true), "quadric-through:Accepted");
        ExpectRoute("G8-ROUTE-QUADRIC-BLIND", () => HoleInCube(top, down, 8, false), "quadric-blind:Accepted");
        // Точные цекование и зенковка (поставка 4): прежде давали негодное B-rep (кромки не были общими рёбрами) и
        // молча уходили в общую булеву — вскрыто трассой маршрутов; исправлено в QuadricCut.AddRecess/AddCone.
        // Объём — по формулам: цекование R7 на 4 мм; зенковка 90° от R7 до R4 (высота конуса 3 мм).
        double cbExact = 216000 - Math.PI * (16 * 60 + 33 * 4);
        double csExact = 216000 - Math.PI * 16 * 60 - (Math.PI * 3 / 3 * (49 + 28 + 16) - Math.PI * 16 * 3);
        ExpectRoute("G8-ROUTE-QUADRIC-COUNTERBORE", () => HoleInCube(top, down, 8, true, kind: HoleKind.Counterbore), "quadric-counterbore:Accepted",
                    p => Math.Abs(p.Volume - cbExact) < 1e-9 * 216000 ? null : $"volume {p.Volume} vs exact {cbExact}");
        ExpectRoute("G8-ROUTE-QUADRIC-COUNTERSINK", () => HoleInCube(top, down, 8, true, kind: HoleKind.Countersink), "quadric-countersink:Accepted",
                    p => Math.Abs(p.Volume - csExact) < 1e-9 * 216000 ? null : $"volume {p.Volume} vs exact {csExact}");
        ExpectRoute("G8-ROUTE-EXACT-THROUGH", () => HoleInCube(top, tilt, 8, true), "quadric-through:Declined → exact-through:Accepted");
        ExpectRoute("G8-ROUTE-EXACT-BLIND", () => HoleInCube(top, tilt, 8, false), "quadric-blind:Declined → exact-blind:Accepted");
        // Ось в 2 мм внутри грани x = −30, R5: кромка задевает край грани.
        var edge = new Vector3d(-28, 0, 30);
        ExpectRoute("G8-ROUTE-EXACT-CUT-EDGE", () => HoleInCube(edge, down, 10, true),
                    "quadric-through:Declined → exact-through:Declined → exact-cut-edge:Accepted");
        // НОВЫЙ маршрут: глухое отверстие поперёк уступа. Куб минус {x > 0, z > 30 − step}; ось (−1.3, 0.7), R4, от
        // z = 30 на глубину 30 (дно z = 0 — в материале целиком). Кромка задевает край грани (стенка уступа x = 0).
        // Убранный объём: z ∈ [30 − step, 30] — круг без сегмента за x > 0; ниже — круг целиком.
        double r = 3, hc = 1.3, seg = r * r * Math.Acos(hc / r) - hc * Math.Sqrt(r * r - hc * hc);
        double Removed(double step) => (Math.PI * r * r - seg) * step + Math.PI * r * r * (30 - step);
        double StepVolume(double step) => 216000 - 30 * 60 * step;
        (Part, HoleFeature) StepHole(double step, double diameter = 6)
        {
            var p = new Part("step"); TestGeometry.AddCube(p, 60);
            var cut = new ExtrudeFeature("Step") { SketchId = RectSketch(p, 30, 0, -30, 30, 60).Id, Cut = true };
            cut.Side1.Distance = new ValueRef(step); cut.Side1.Flip = true; p.AddFeature(cut);
            var h = new HoleFeature("H") { Direction = down, ThroughAll = false, Diameter = new ValueRef(diameter), Depth = new ValueRef(30) };
            h.Points.Add(new Vector3d(-1.3, 0.7, 30)); p.AddFeature(h); p.Rebuild(null);
            return (p, h);
        }
        ExpectRoute("G8-ROUTE-EXACT-CUT-EDGE-BLIND", () => StepHole(2),
                    "quadric-blind:Declined → exact-blind:Declined → exact-cut-edge-blind:Accepted",
                    p => Math.Abs(p.Volume - (StepVolume(2) - Removed(2))) < 1e-6 * StepVolume(2) ? null : $"volume {p.Volume} vs exact {StepVolume(2) - Removed(2)}");
        // R4 у того же уступа: прежде стенку отверстия разметчик не строил — разрез полосы (кольца, разрезанного по шву)
        // пересекал вертикальный ряд кромки на стенке уступа, многоугольник полосы самопересекался, и «молния» оставляла
        // непарную диагональ (разбор — K01 walldbg). Исправлено выбором начала второго края полосы (ExactFaceMesher).
        double rr = 4, segR4 = rr * rr * Math.Acos(hc / rr) - hc * Math.Sqrt(rr * rr - hc * hc);
        double removedR4 = (Math.PI * rr * rr - segR4) * 2 + Math.PI * rr * rr * 28;
        ExpectRoute("G8-ROUTE-EXACT-CUT-EDGE-BLIND-R4", () => StepHole(2, 8),
                    "quadric-blind:Declined → exact-blind:Declined → exact-cut-edge-blind:Accepted",
                    p => Math.Abs(p.Volume - (StepVolume(2) - removedR4)) < 1e-6 * StepVolume(2) ? null : $"volume {p.Volume} vs exact {StepVolume(2) - removedR4}");
        ExpectRoute("G8-ROUTE-GENERAL-COUNTERBORE-BLIND", () => HoleInCube(top, down, 8, false, kind: HoleKind.Counterbore),
                    $"quadric-counterbore:{NA} → exact-blind:{NA} → exact-cut-edge-blind:{NA} → cross-hole:{NA} → general-boolean:Accepted");
        ExpectRoute("G8-ROUTE-CROSS-HOLE", () =>
        {
            var p = new Part("shaft"); var sk = CircleSketch(p, 0, 0, 0, 10);
            var exf = new ExtrudeFeature { SketchId = sk.Id }; exf.Side1.Distance = new ValueRef(60); p.AddFeature(exf);
            var h = new HoleFeature("Cross") { Direction = Vector3d.UnitY, ThroughAll = true, Diameter = new ValueRef(4) };
            h.Points.Add(new Vector3d(0, -20, 30)); p.AddFeature(h); p.Rebuild(null);
            return (p, h);
        // Поставка 4: путь поперечного отверстия прежде давал негодное B-rep (торцевые окружности — не общие рёбра,
        // кривая стыка обходилась одинаково с двух сторон) и сетку (грани гранились без вырезов) — исправлено в
        // CrossHole. Объём — против интеграла (K08A, 18599.491509845) с запасом на ошибку интеграла.
        }, $"quadric-through:Declined → exact-through:{NA} → exact-cut-edge:{NA} → cross-hole:Accepted",
        p => Math.Abs(p.Volume - 18599.491509845) < 1e-9 * 18599.49 ? null : $"volume {p.Volume} vs 18599.491509845");

        Check("G8-ROUTE-SWITCHED-OFF", () =>
        {
            // Выключенные точные пути — все маршруты «неприменимы» с причиной, построение общей булевой.
            using var _ = new KernelContext().WithExtension(ModelSettings.Capture() with { ExactHoles = false }).Enter();
            var (p, h) = HoleInCube(top, down, 8, true);
            string got = Trace(h);
            Require(got == $"quadric-through:{NA} → exact-through:{NA} → exact-cut-edge:{NA} → cross-hole:{NA} → general-boolean:Accepted", got);
            Require(h.LastRoutes[0][0].Reason!.Contains("switched off"), h.LastRoutes[0][0].ToString());
        });

        // Составной инструмент (поставка 4): глухие цекование и зенковка в кубе идут общей булевой; объём — по формуле.
        // Поставка 4, обновление 17: разница в точности была не в инструменте, а в восстановлении кромок с поверхностей
        // вращения (KnownCircle) — теперь оба пути точны; составная зенковка — известный дефект сетки, поэтому выключен.
        // Цекование: Ø8 на 20 + головка Ø14 на 4; зенковка 90°: Ø8 на 20 + конус Ø14→Ø8 (высота 3).
        foreach (var (kind, removedF) in new[] { (HoleKind.Counterbore, Math.PI * (16 * 20 + 33 * 4)), (HoleKind.Countersink, Math.PI * (16 * 20 + 45)) })
            Check($"G8-COMPOSITE-{kind.ToString().ToUpperInvariant()}-BLIND", () =>
            {
                // Составной инструмент выключен по умолчанию (замер ниже): здесь он включается явно.
                double exact = 216000 - removedF;
                Part pc; HoleFeature hc;
                using (new KernelContext().WithExtension(ModelSettings.Capture() with { CompositeTool = true }).Enter())
                    (pc, hc) = HoleInCube(top, down, 8, false, kind: kind);
                var (po, _) = HoleInCube(top, down, 8, false, kind: kind); double vo = po.Volume;
                Require(po.Integrity == null, "old path: " + po.Integrity);
                Log(new { test = $"G8-COMPOSITE-{kind}-BLIND", exact, composite = pc.Volume, twoCuts = vo,
                          compositeError = Math.Abs(pc.Volume - exact), twoCutsError = Math.Abs(vo - exact), reason = hc.LastRoutes[0][^1].Reason });
                // Прежний путь (два вычитания) — точен для обоих видов: окружности с поверхностей вращения (поставка 4).
                Require(Math.Abs(vo - exact) < 1e-9 * 216000, $"two cuts volume {vo} vs exact {exact}");
                Require(hc.LastRoutes[0][^1].Reason == "composite tool", "composite tool not used: " + hc.LastRoutes[0][^1]);
                // Составной инструмент — годен и точен для обоих видов (обновление 18: дно — диск вращением с полюсом —
                // размечается как плоскость; прежде его отказ уводил тело на прежнюю сетку, и она не сходилась).
                Require(pc.Integrity == null && hc.Error == null, $"composite: {hc.Error} {pc.Integrity}");
                Require(Math.Abs(pc.Volume - exact) < 1e-9 * 216000, $"composite volume {pc.Volume} vs exact {exact}");
            });

        // Разметка в (u, v) граней вала с поперечным отверстием (поставка 4): прежде кромка лежала на шве полосы, и боковая
        // грань уходила к прежнему построителю (сетка грубее: прогиб 0.0295 против 0.0247 мм). Теперь — все четыре.
        Check("G8-CROSS-HOLE-UV-ALL-FACES", () =>
        {
            var r = Parametric3D.Kernel.Ops.CrossHole.Cut(Vector3d.Zero, Vector3d.UnitZ, 10, 60, new Vector3d(0, -20, 30), Vector3d.UnitY, 2, new List<string>());
            Require(r != null, "CrossHole declined");
            var fails = new List<string>();
            foreach (var f in r!.Faces)
                if (Parametric3D.Kernel.Tess.UvMesher.Mesh(f, 0.02, out var why) == null) fails.Add($"{f.Tag}: {why}");
            Require(fails.Count == 0, string.Join(" | ", fails));
        });

        Check("G8-BLIND-EDGE-VS-GENERAL", () =>
        {
            // Новый маршрут против прежнего общего на неглубоком уступе: точный объём против гранёного.
            double exact = StepVolume(2) - Removed(2);
            var (pe, _) = StepHole(2);
            double vg; bool generalAnalytic;
            using (new KernelContext().WithExtension(ModelSettings.Capture() with { BlindEdgeCut = false }).Enter())
            {
                var (pg, hg) = StepHole(2); vg = pg.Volume; Require(Trace(hg).EndsWith("general-boolean:Accepted"), Trace(hg));
                generalAnalytic = pg.Bodies.All(b => b.Guarantee == GuaranteeLevel.ExactAnalytic);
            }
            Log(new { test = "G8-BLIND-EDGE-VS-GENERAL", exact, newRoute = pe.Volume, general = vg,
                      newError = Math.Abs(pe.Volume - exact), generalError = Math.Abs(vg - exact) });
            // Общая булева аналитическим путём (P3D_ANALYTIC) точна сама: тогда оба пути — точны; иначе новый маршрут ближе.
            if (generalAnalytic)
                Require(Math.Abs(pe.Volume - exact) < 1e-9 * exact && Math.Abs(vg - exact) < 1e-9 * exact, "an exact route is not exact");
            else
                Require(Math.Abs(pe.Volume - exact) < Math.Abs(vg - exact), "the exact route is not closer to the formula than the general one");
        });
    }
}
