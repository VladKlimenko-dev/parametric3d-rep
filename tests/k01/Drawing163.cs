using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k163 (замечание 05.11, 09:22):
    /// KRN-DRAW-SIMPLIFY-K163 — «D» (дуга R40 от 30° до 150° и хорда), выдавленная вдоль Z, на виде слева: дуга «с ребра»
    /// (проекция идёт от y = 20 до 40 и обратно) — видимая линия от 20 до 40 (прежде упрощение ломаной выбрасывало точку поворота: оставался кусок).
    /// Мутация P3D_HLRSIMPLIFY=0 — падает.
    /// KRN-DRAW-CHECK-K163 — пластина 60×40 с отверстием ⌀16 (размеры эскиза: 60, 40, ⌀16, положение центра 20 и 15; угол
    /// скоса 30° второго эскиза), выдавливание 10: без размеров на листе — указано 0; после «Автоматических размеров» и
    /// «Размеров модели» — все (угол — угловым размером, 30°); скрытый размер не считается. Мутации P3D_MODELDIMS163=0 (без
    /// угловых и прочих размеров модели) и P3D_MOREDIMS=0 — проверка не полная.
    /// KRN-DRAW-CENTERS-K163 — центры дуг профиля, не отмеченные осями, — перекрестия штрихпунктиром. Мутация P3D_ARCCENTERS=0.
    /// </summary>
    static void Drawing163Checks()
    {
        Check("KRN-DRAW-SIMPLIFY-K163", () =>
        {
            // «D»: дуга R40 от 30° до 150° и хорда y = 20, выдавливание 10 вдоль Z; на виде слева плоскость эскиза видна с ребра,
            // проекция дуги идёт от y = 20 до 40 и обратно
            var p = new Part("d");
            var sk = new Sketch("S", Frame3.XY); p.Add(sk);
            var a0 = new SkPoint(40 * Math.Cos(Math.PI / 6), 20); var a1 = new SkPoint(-40 * Math.Cos(Math.PI / 6), 20);
            sk.AddEntity(new SkArc(new SkPoint(0, 0), a0, a1, true)); sk.AddEntity(new SkLine(a1, a0));
            var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
            p.Rebuild(null);
            var lines = ViewProjection.Project(p.Bodies.Single(), ViewFrame.Of("left"));
            // вид слева: X вида = z; торец z = 10 — линия x = 10 (ближний к наблюдателю край — видимый)
            var end = lines.Where(l => l.Visible && (l.Points.All(q => Math.Abs(q.X) < 1e-6) || l.Points.All(q => Math.Abs(q.X - 10) < 1e-6))).ToList();
            double lo = end.Count == 0 ? 0 : end.Min(l => l.Points.Min(q => q.Y)), hi = end.Count == 0 ? 0 : end.Max(l => l.Points.Max(q => q.Y));
            Log(new { test = "KRN-DRAW-SIMPLIFY-K163", lines = end.Count, lo = Math.Round(lo, 4), hi = Math.Round(hi, 4), err = ex.Error });
            Require(ex.Error == null && Math.Abs(lo - 20) < 1e-3 && Math.Abs(hi - 40) < 1e-3, $"arc seen edge-on: {lo:0.###}..{hi:0.###}, want 20..40");
            // упрощение ломаной, идущей по прямой туда и обратно (как проекция дуги торца трубы пользователя): вершина поворота
            var sp = ViewProjection.Simplify(new List<Vector2d> { new(0, 20), new(0, 30), new(0, 40), new(0, 35), new(0, 20) });
            Require(sp.Max(q => q.Y) > 39.999, $"turning point dropped: {string.Join(" ", sp.Select(q => q.Y))}");
        });

        Check("KRN-DRAW-CHECK-K163", () =>
        {
            var p = new Part("check");
            var sk = new Sketch("S1", Frame3.XY); p.Add(sk);
            var q = new[] { new SkPoint(0, 0), new SkPoint(60, 0), new SkPoint(60, 40), new SkPoint(0, 40) };
            var ls = new SkLine[4];
            for (int k = 0; k < 4; k++) ls[k] = sk.AddEntity(new SkLine(q[k], q[(k + 1) % 4]));
            var ci = sk.AddEntity(new SkCircle(new SkPoint(20, 15), 8));
            SkDimension Dim(DimensionType t, double v, params SkEntity[] it) { var d = new SkDimension(t, v, it); sk.Dimensions.Add(d); return d; }
            Dim(DimensionType.DistancePointPoint, 60, q[0], q[1]);
            Dim(DimensionType.DistancePointPoint, 40, q[1], q[2]);
            Dim(DimensionType.Diameter, 16, ci);
            Dim(DimensionType.DistanceHorizontal, 20, q[0], ci.Center);
            Dim(DimensionType.DistanceVertical, 15, q[0], ci.Center);
            // второй эскиз: угол 30° между двумя отрезками (на той же плоскости)
            var sk2 = new Sketch("S2", Frame3.XY); p.Add(sk2);
            var o = new SkPoint(0, 0); var l1 = sk2.AddEntity(new SkLine(o, new SkPoint(30, 0))); var l2 = sk2.AddEntity(new SkLine(o, new SkPoint(30 * Math.Cos(Math.PI / 6), 30 * Math.Sin(Math.PI / 6))));
            var ad = new SkDimension(DimensionType.AngleLineLine, 30, l1, l2); sk2.Dimensions.Add(ad);
            var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
            var ex2 = new ExtrudeFeature { SketchId = sk2.Id, Cut = false }; ex2.Side1.Distance = new ValueRef(10); p.AddFeature(ex2);
            p.Rebuild(null);
            var dd = new DrawingDocument(); dd.EnsureViews();
            var sc = DrawingScene.Build(dd, p.Bodies);
            var before = DrawingCheck.Run(dd, p);
            dd.AutoDimensions(sc); dd.ModelDimensions(p); dd.ArrangeDims(sc);
            var g = dd.Compose(sc, new FieldSource());
            var after = DrawingCheck.Run(dd, p);
            var ang = dd.Dims.FirstOrDefault(d => d.Kind == "ang");
            Log(new { test = "KRN-DRAW-CHECK-K163", items = after.Count, before = before.Count(i => i.Covered), after = after.Count(i => i.Covered), missing = after.Where(i => !i.Covered).Select(i => i.Label), ang = ang?.Value, angText = ang == null ? null : dd.CaptionOf(ang) });
            Require(before.Count >= 7 && before.Count(i => i.Covered) == 0, $"before: {before.Count(i => i.Covered)} of {before.Count} covered");
            Require(after.All(i => i.Covered), "missing: " + string.Join("; ", after.Where(i => !i.Covered).Select(i => i.Label)));
            Require(ang != null && Math.Abs(ang.Value - 30) < 1e-6 && g.Texts.Any(t => t.Text == "30°"), $"angle dimension {ang?.Value}");
            // скрытый размер не считается
            var dia = dd.Dims.First(d => d.Kind == "dia" && Math.Abs(d.X2 - 8) < 1e-6); dia.Hidden = true;
            foreach (var d in dd.Dims.Where(d => d.Kind is "rad" or "dia" && Math.Abs(d.X2 - 8) < 1e-6)) d.Hidden = true;
            Require(DrawingCheck.Run(dd, p).Any(i => !i.Covered && i.Kind == 'D'), "a hidden diameter still counted");
        });

        Check("KRN-DRAW-CENTERS-K163", () =>
        {
            // пластина с пазом (дуги R10 по концам): на виде спереди — центры дуг паза
            var p = new Part("slot");
            var sk = new Sketch("S", Frame3.XY); p.Add(sk);
            var a = new SkPoint(-20, -10); var b = new SkPoint(20, -10); var c = new SkPoint(20, 10); var d = new SkPoint(-20, 10);
            sk.AddEntity(new SkLine(a, b)); sk.AddEntity(new SkArc(new SkPoint(20, 0), b, c, true)); sk.AddEntity(new SkLine(c, d)); sk.AddEntity(new SkArc(new SkPoint(-20, 0), d, a, true));
            var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(5); p.AddFeature(ex);
            p.Rebuild(null);
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front" });
            var sc = DrawingScene.Build(dd, p.Bodies);
            var g = dd.Compose(sc, new FieldSource());
            int centre = g.Lines.Count(l => l.Kind == LineKind.Center) + g.Polylines.Count(l => l.Kind == LineKind.Center);   // k164: осевые — ломаными (со штрихами)
            Log(new { test = "KRN-DRAW-CENTERS-K163", centre, arcs = sc.Arcs["front"].Count, err = ex.Error });
            Require(ex.Error == null && centre >= 4, $"centre lines of the slot arcs: {centre}");
        });
    }
}
