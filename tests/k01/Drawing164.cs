using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k164 (замечание 05.11, 10:33):
    /// KRN-DRAW-ARCSONCYL-K164 — вал R30 вдоль X, паз (дуга R8 с центром (0, 10) и стенки до y = −40) прорезан вдоль Z насквозь:
    /// верх паза — ребро на цилиндре паза, кривая ребра — линия пересечения с валом (не дуга). На главном виде — дуга R8 с центром
    /// (0, 10), автоматический радиус R8 и центр дуги отмечен осевыми. Мутация P3D_ARCSONCYL=0 — дуги нет.
    /// KRN-DRAW-CENTERPRIO-K164 — пластина с отверстиями ⌀20 и ⌀12 на разной высоте: выносные размеров от центров —
    /// штрихпунктирные (продолжение осевых), ни один сплошной отрезок не лежит на осевой. Мутация P3D_CENTERPRIO=0 — сплошные поверх.
    /// KRN-DRAW-CENTERNOS-K164 — у той же пластины центры пронумерованы (главный — 1), у диаметров под полкой «℄N»; выключено —
    /// номеров нет. Мутация P3D_CENTERNOS=0.
    /// </summary>
    static void Drawing164Checks()
    {
        Check("KRN-DRAW-ARCSONCYL-K164", () =>
        {
            var p = new Part("slot");
            var s1 = new Sketch("S1", Frame3.YZ); p.Add(s1);
            s1.AddEntity(new SkCircle(new SkPoint(0, 0), 30));
            var e1 = new ExtrudeFeature { SketchId = s1.Id, TwoSided = true }; e1.Side1.Distance = new ValueRef(30); e1.Side2.Distance = new ValueRef(30); p.AddFeature(e1);
            var s2 = new Sketch("S2", Frame3.XY); p.Add(s2);
            var a = new SkPoint(8, 10); var b = new SkPoint(-8, 10); var c = new SkPoint(-8, -40); var dd0 = new SkPoint(8, -40);
            s2.AddEntity(new SkArc(new SkPoint(0, 10), a, b, true)); s2.AddEntity(new SkLine(b, c)); s2.AddEntity(new SkLine(c, dd0)); s2.AddEntity(new SkLine(dd0, a));
            var e2 = new ExtrudeFeature { SketchId = s2.Id, Cut = true, TwoSided = true }; e2.Side1.Distance = new ValueRef(40); e2.Side2.Distance = new ValueRef(40); p.AddFeature(e2);
            p.Rebuild(null);
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front" });
            var sc = DrawingScene.Build(dd, p.Bodies);
            var arcs = sc.Arcs.GetValueOrDefault("front") ?? new();
            var slot = arcs.Where(x => Math.Abs(x.Radius - 8) < 1e-6 && (x.Center - new Vector2d(0, 10)).Length < 1e-6).ToList();
            int nonArcEdges = p.Bodies.Single().Edges.Count(e => e.Curve != null && e.Curve is not Parametric3D.Kernel.Curves.ArcCurve3 && e.Curve is not Parametric3D.Kernel.Curves.LineCurve3);
            dd.AutoDimensions(sc);
            var g = dd.Compose(sc, new FieldSource());
            bool rad8 = dd.Dims.Any(x => x.Kind == "rad" && Math.Abs(x.X2 - 8) < 1e-6);
            var cs = dd.ToSheet("front", new Vector2d(0, 10));
            int marks = dd.CenterSegs["front"].Count(sg => DistToSeg(new Vector2d(cs.X, cs.Y), sg.A, sg.B) < 1e-6);
            Log(new { test = "KRN-DRAW-ARCSONCYL-K164", err = e2.Error, nonArcEdges, slotArcs = slot.Count, sweep = slot.Select(x => Math.Round(((x.A1 - x.A0) * 180 / Math.PI + 360) % 360, 1)).ToArray(), rad8, marks });
            Require(e1.Error == null && e2.Error == null, "model built");
            Require(nonArcEdges > 0, "the slot top edge is an intersection curve (the case under test)");
            Require(slot.Count > 0 && slot.All(x => !x.Full), "slot top arc R8 at (0, 10) on the front view");
            Require(rad8, "automatic radius R8");
            Require(marks >= 2, $"slot arc centre marked by centre lines: {marks}");
        });

        // пластина 100×60 с отверстиями ⌀20 в (30, 30) и ⌀12 в (70, 42)
        Part Plate()
        {
            var p = new Part("plate");
            var sk = new Sketch("S", Frame3.XY); p.Add(sk);
            var q = new[] { new SkPoint(0, 0), new SkPoint(100, 0), new SkPoint(100, 60), new SkPoint(0, 60) };
            for (int k = 0; k < 4; k++) sk.AddEntity(new SkLine(q[k], q[(k + 1) % 4]));
            sk.AddEntity(new SkCircle(new SkPoint(30, 30), 10));
            sk.AddEntity(new SkCircle(new SkPoint(70, 42), 6));
            var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
            p.Rebuild(null);
            return p;
        }

        Check("KRN-DRAW-CENTERPRIO-K164", () =>
        {
            var p = Plate();
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front" });
            var sc = DrawingScene.Build(dd, p.Bodies);
            dd.AutoDimensions(sc);
            var g = dd.Compose(sc, new FieldSource());
            var centre = dd.Dims.Where(x => x.Role == "centre").ToList();
            var segs = dd.CenterSegs["front"];
            // сплошные тонкие отрезки, лежащие на осевой (общий участок длиннее 0.5 мм)
            int onAxis = g.Lines.Where(l => l.Kind == LineKind.Thin).Count(l => segs.Any(sg => Overlap(new(l.X1, l.Y1), new(l.X2, l.Y2), sg.A, sg.B) > 0.5));
            // выносные от центров — штрихпунктиром: отрезки Center, начинающиеся на продолжении осевой
            var pts = dd.CenterPts["front"];
            int extCentre = g.Polylines.Count(pl => pl.Kind == LineKind.Center && pl.Points.Count == 2 && !segs.Any(sg => Overlap(new(pl.Points[0].X, pl.Points[0].Y), new(pl.Points[1].X, pl.Points[1].Y), sg.A, sg.B) > 1e-6));
            Log(new { test = "KRN-DRAW-CENTERPRIO-K164", centreDims = centre.Count, onAxis, extCentre, centres = pts.Count });
            Require(centre.Count >= 1, "centre offset dimensions");
            Require(onAxis == 0, $"solid lines drawn over centre lines: {onAxis}");
            Require(extCentre >= 2, $"extension lines from centres are centre lines: {extCentre}");
        });

        Check("KRN-DRAW-CENTERNOS-K164", () =>
        {
            var p = Plate();
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front" });
            var sc = DrawingScene.Build(dd, p.Bodies);
            dd.AutoDimensions(sc);
            var g = dd.Compose(sc, new FieldSource());
            var nos = dd.CenterNos.ToList();
            var big = dd.ToSheet("front", new Vector2d(30, 30));
            var main = nos.FirstOrDefault(n => n.No == 1);
            var labels = g.Texts.Count(t => nos.Any(n => t.Text == n.No.ToString()));
            dd.CenterNumbers = false;
            var g2 = dd.Compose(sc, new FieldSource());
            Log(new { test = "KRN-DRAW-CENTERNOS-K164", numbers = nos.Select(n => n.No).ToArray(), main = $"{main.P.X:0.##},{main.P.Y:0.##}", bigHole = $"{big.X:0.##},{big.Y:0.##}", labels, off = dd.CenterNos.Count });
            Require(nos.Count >= 2 && nos.Select(n => n.No).SequenceEqual(Enumerable.Range(1, nos.Count)), "centres numbered 1..n");
            Require((main.P - new Vector2d(big.X, big.Y)).Length < 1e-6, "number 1 — the main centre (largest circle)");
            Require(labels >= 2 * nos.Count, $"centre labels and numbers under the shelves: {labels} texts");
            Require(dd.CenterNos.Count == 0, "numbers off");
        });
    }

    static double DistToSeg(Vector2d q, Vector2d a, Vector2d b)
    {
        var ab = b - a; double t = ab.LengthSquared < 1e-24 ? 0 : Math.Clamp(Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
        return (a + ab * t - q).Length;
    }

    /// <summary>Длина общего участка двух отрезков, лежащих на одной прямой (иначе 0).</summary>
    static double Overlap(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        var ab = b - a; double l = ab.Length;
        if (l < 1e-9) return 0;
        var u = ab / l;
        if (Math.Abs(u.X * (d - c).Y - u.Y * (d - c).X) > 1e-3 * Math.Max(1e-9, (d - c).Length)) return 0;
        var ac = c - a;
        if (Math.Abs(ac.X * u.Y - ac.Y * u.X) > 0.05) return 0;
        double t1 = Vector2d.Dot(c - a, u), t2 = Vector2d.Dot(d - a, u);
        return Math.Max(0, Math.Min(l, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2)));
    }
}
