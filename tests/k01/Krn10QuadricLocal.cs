using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// KRN-10 (g.13): МЕСТНАЯ ПЛИТА ТОЧНОГО ПУТИ ОТВЕРСТИЯ. Прежде QuadricCut требовал, чтобы ВСЁ тело было плитой вдоль оси
    /// и состояло из плоскостей и прежних отверстий: ступенька или скругление где угодно уводили цекование на общую булеву
    /// (два вычитания). Теперь проверяется только то, что задевает цилиндр отверстия. Здесь: тело с уступом (вход на двух
    /// уровнях, несколько точек в одной операции) и тело со скруглением; объём снятого против формулы, маршрут — точный.
    /// Мутации: P3D_QUADRICLOCAL=0 (прежнее правило плиты) и P3D_QUADRICROUNDS=0 (цилиндрические грани — отказ).
    /// </summary>
    static void Krn10QuadricLocal()
    {
        var down = new Vector3d(0, 0, -1);

        Part StepBody()
        {
            var p = new Part("step"); TestGeometry.AddCube(p, 60);                 // [-30, 30]³
            var cut = new ExtrudeFeature("Step") { SketchId = RectSketch(p, 30, 0, -30, 30, 60).Id, Cut = true };
            cut.Side1.Distance = new ValueRef(10); cut.Side1.Flip = true; p.AddFeature(cut);   // x > 0: верх на z = 20
            p.Rebuild(null);
            return p;
        }
        Part RoundBody()
        {
            var p = new Part("round"); TestGeometry.AddCube(p, 60);
            p.Rebuild(null);
            var e = p.Bodies[0].Edges.Single(x => ((x.V0.P + x.V1.P) / 2 - new Vector3d(-30, 0, 30)).Length < 1e-6);
            var rf = new RoundFeature("R") { Radius = new ValueRef(5) }; rf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(rf);
            p.Rebuild(null);
            return p;
        }

        // снятый объём: сквозное R4 на высоте h, цекование R7 на 4, зенковка 90° от R7 до R4 (конус высотой 3)
        double Through(double h) => Math.PI * 16 * h;
        double Counterbore(double h) => Math.PI * (16 * h + 33 * 4);
        double Countersink(double h) => Math.PI * 16 * h + (Math.PI * 3 / 3 * (49 + 28 + 16) - Math.PI * 16 * 3);

        void Case(string id, Func<Part> body, HoleKind kind, bool through, Vector3d[] at, double removed, string route)
        {
            Check(id, () =>
            {
                var p = body();
                double v0 = p.Volume;
                var h = new HoleFeature("H") { Direction = down, ThroughAll = through, Diameter = new ValueRef(8), Kind = kind };
                if (!through) h.Depth = new ValueRef(20);
                if (kind == HoleKind.Counterbore) { h.HeadDiameter = new ValueRef(14); h.HeadDepth = new ValueRef(4); }
                if (kind == HoleKind.Countersink) { h.HeadDiameter = new ValueRef(14); h.HeadAngle = new ValueRef(90); }
                h.Points.AddRange(at); p.AddFeature(h); p.Rebuild(null);
                Require(h.Error == null && p.Integrity == null, $"build failed: {h.Error} {p.Integrity}");
                var traces = h.LastRoutes.Select(t => string.Join(" → ", t.Select(x => $"{x.Route}:{x.Verdict}"))).ToList();
                Log(new { test = id, traces, removed = v0 - p.Volume, expected = removed });
                Require(traces.All(t => t == route), $"routes: {string.Join(" ; ", traces)}");
                Require(Math.Abs(v0 - p.Volume - removed) < 1e-9 * v0, $"removed {v0 - p.Volume} vs exact {removed}");
                var v = SolidValidator.Validate(p.Bodies[0]);
                Require(v.IsValid, "invalid: " + v.Summary());
            });
        }

        var top1 = new Vector3d(-15, -10, 30); var top2 = new Vector3d(-15, 10, 30); var low = new Vector3d(15, 0, 20);
        Case("KRN10-QUADRIC-LOCAL-COUNTERBORE", StepBody, HoleKind.Counterbore, true, new[] { top1, top2, low },
             2 * Counterbore(60) + Counterbore(50), "quadric-counterbore:Accepted");
        Case("KRN10-QUADRIC-LOCAL-COUNTERSINK", StepBody, HoleKind.Countersink, true, new[] { top1, low },
             Countersink(60) + Countersink(50), "quadric-countersink:Accepted");
        Case("KRN10-QUADRIC-LOCAL-THROUGH", StepBody, HoleKind.Simple, true, new[] { top1, low },
             Through(60) + Through(50), "quadric-through:Accepted");
        Case("KRN10-QUADRIC-LOCAL-BLIND", StepBody, HoleKind.Simple, false, new[] { low },
             Through(20), "quadric-blind:Accepted");
        Case("KRN10-QUADRIC-ROUND-THROUGH", RoundBody, HoleKind.Simple, true, new[] { new Vector3d(0, 0, 30) },
             Through(60), "quadric-through:Accepted");
        Case("KRN10-QUADRIC-ROUND-COUNTERBORE", RoundBody, HoleKind.Counterbore, true, new[] { new Vector3d(0, 0, 30), new Vector3d(-15, 15, 30) },
             2 * Counterbore(60), "quadric-counterbore:Accepted");
        // Отказ по делу: головка R7 у оси x = −22 задевает скругление (оно занимает x < −25 у верха) — общая булева.
        Check("KRN10-QUADRIC-ROUND-NEAR-DECLINES", () =>
        {
            var p = RoundBody();
            var h = new HoleFeature("H") { Direction = down, ThroughAll = true, Diameter = new ValueRef(8), Kind = HoleKind.Counterbore,
                                           HeadDiameter = new ValueRef(14), HeadDepth = new ValueRef(4) };
            h.Points.Add(new Vector3d(-22, 0, 30)); p.AddFeature(h); p.Rebuild(null);
            string t = string.Join(" → ", h.LastRoutes[0].Select(x => $"{x.Route}:{x.Verdict}"));
            Log(new { test = "KRN10-QUADRIC-ROUND-NEAR-DECLINES", t, err = h.Error });
            Require(t.StartsWith("quadric-counterbore:Declined"), "route: " + t);
        });
    }
}
