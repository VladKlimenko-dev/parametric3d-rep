using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;

static partial class Program
{
    /// <summary>Куб 100 со срезом угла 40×40 под 45° (вверху справа на главном виде): угол габарита главного вида пуст.</summary>
    static TSolid CornerCutCube()
    {
        var p = new Part("cut"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
        var cube = p.Bodies.Single();
        var mv = Parametric3D.Kernel.Tess.Tessellator.Tessellate(cube).Vertices.Select(v => (Vector3d)v.P).ToList();
        var mn = new Vector3d(mv.Min(v => v.X), mv.Min(v => v.Y), mv.Min(v => v.Z)); var mx = new Vector3d(mv.Max(v => v.X), mv.Max(v => v.Y), mv.Max(v => v.Z));
        var c = (mn + mx) / 2;
        var n = new Vector3d(1, 0, 1).Normalized();
        var cc = new Vector3d(mx.X - 20, c.Y, mx.Z - 20) + n * 50;
        var stretch = new Parametric3D.Kernel.Ops.TransformOp.Xform(q => new Vector3d(q.X, c.Y + (q.Y - c.Y) * 1.5, q.Z), d => d, false);
        var cutter = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.TransformOp.Copy(cube, stretch), Parametric3D.Kernel.Ops.TransformOp.Rotate(c, Vector3d.UnitY, 45));
        cutter = Parametric3D.Kernel.Ops.TransformOp.Copy(cutter, Parametric3D.Kernel.Ops.TransformOp.Move(cc - c));
        var bodies = new List<TSolid> { cube };
        Parametric3D.Kernel.Ops.BooleanOp.Subtract(bodies, cutter, new List<string>());
        return bodies.Single();
    }

    /// <summary>
    /// k159 (замечания 04.11, 23:51, п. 2.5, 2.9, 2.11–2.13):
    /// KRN-DRAW-DIMPOINTS-K159 — габаритные размеры от крайних точек контура: у куба со срезом угла концы всех автоматических
    /// линейных размеров лежат на точках видимых линий вида (угол габарита пуст — прежде выносная линия шла от пустого места);
    /// первая размерная линия — в 10 мм от габарита вида (ГОСТ 2.307). Мутация P3D_DIMEXTREME=0 — падает.
    /// KRN-DRAW-ARRANGE-K159 — деталь с отверстиями и скруглениями: тексты автоматических размеров на листе не накладываются
    /// друг на друга; ряды параллельных размеров — через 7 мм. Мутация P3D_DIMARRANGE=0 — падает.
    /// KRN-DRAW-ROTATE-K159 — поворот главного вида куба на 30°: габарит вида на листе 100·(cos 30° + sin 30°), лист → вид → лист —
    /// тождество, размерная линия горизонтального размера — под 30°.
    /// KRN-DRAW-UNITS-K159 — единицы модели «in»: размер 100 мм подписан «3,937»; единицы чертежа «mm» — «100». Мутация
    /// P3D_DRAWUNITS=0 — падает.
    /// KRN-DRAW-MOVEDIM-K159 — перенос размера: линейный — размерная линия сдвигается на 5 мм по нормали; диаметр — выноска под
    /// углом к точке переноса; место после переноса больше не меняется раскладкой.
    /// </summary>
    static List<SheetText> LastDimTexts = new();
    static void Drawing159Checks()
    {
        static List<Vector2d[]> DimTexts(DrawingDocument dd, DrawingScene sc)
        {
            dd.ShowDims = false; var g0 = dd.Compose(sc, new FieldSource());
            dd.ShowDims = true; var g1 = dd.Compose(sc, new FieldSource());
            // тексты размеров — после текстов листа и подписей видов, до обозначений (обозначений нет)
            LastDimTexts = g1.Texts.Skip(g0.Texts.Count).ToList();
            return LastDimTexts.Select(DrawingDocument.TextCorners).ToList();
        }
        static bool Ov(Vector2d[] a, Vector2d[] b) =>
            a.Min(q => q.X) < b.Max(q => q.X) && b.Min(q => q.X) < a.Max(q => q.X) && a.Min(q => q.Y) < b.Max(q => q.Y) && b.Min(q => q.Y) < a.Max(q => q.Y);

        Check("KRN-DRAW-DIMPOINTS-K159", () =>
        {
            var body = CornerCutCube();
            var dd = new DrawingDocument { Scale = 1 }; dd.EnsureViews();
            var sc = DrawingScene.Build(dd, new[] { body });
            dd.AutoDimensions(sc);
            var g = dd.Compose(sc, new FieldSource());
            var bad = new List<string>();
            foreach (var d in dd.Dims.Where(d => d.Kind is "h" or "v"))
            {
                var pts = sc.Lines[d.View].Where(l => l.Visible).SelectMany(l => l.Points).ToList();
                foreach (var e in new[] { new Vector2d(d.X1, d.Y1), new Vector2d(d.X2, d.Y2) })
                    if (pts.Min(q => (q - e).Length) > 1e-6) bad.Add($"{d.View} {d.Kind} ({e.X:0.##}, {e.Y:0.##})");
            }
            // первая размерная линия — 10 мм от габарита вида
            var fb = dd.Boxes["front"];
            var hd = dd.Dims.First(d => d.View == "front" && d.Kind == "h");
            var (_, _, _, _, _, _, L) = dd.DimFrame(hd);
            Log(new { test = "KRN-DRAW-DIMPOINTS-K159", dims = dd.Dims.Count, bad, line = Math.Round(L, 3), box = Math.Round(fb.Y0, 3) });
            Require(bad.Count == 0, "dimension ends off the contour: " + string.Join("; ", bad));
            Require(Math.Abs(L - (fb.Y0 - 10)) < 1e-6, $"first dimension line at {L:0.###}, view box bottom {fb.Y0:0.###}");
        });

        Check("KRN-DRAW-ARRANGE-K159", () =>
        {
            // автоматические размеры детали с отверстиями и скруглениями + размеры «как из эскиза»: два горизонтальных с одной
            // стороны вида (без раскладки — оба в 8 мм от точек: тексты один на другом) и три диаметра концентрических окружностей
            // (без раскладки — выноски под одним углом: тексты рядом друг на друге)
            var p = Parametric3D.App.Diagnostics.TestParts.Realistic(3, 10); p.Rebuild(null);
            var dd = new DrawingDocument(); dd.EnsureViews();
            var sc = DrawingScene.Build(dd, p.Bodies);
            dd.AutoDimensions(sc);
            var (mn, mx) = (new Vector2d(sc.Lines["top"].SelectMany(l => l.Points).Min(q => q.X), sc.Lines["top"].SelectMany(l => l.Points).Min(q => q.Y)),
                            new Vector2d(sc.Lines["top"].SelectMany(l => l.Points).Max(q => q.X), sc.Lines["top"].SelectMany(l => l.Points).Max(q => q.Y)));
            var cTop = (mn + mx) / 2;
            dd.Dims.Add(new DrawingDim { View = "top", Kind = "h", X1 = cTop.X - 30, Y1 = mx.Y - 5, X2 = cTop.X + 30, Y2 = mx.Y - 5, Offset = 8, Model = true, Arrange = true });
            dd.Dims.Add(new DrawingDim { View = "top", Kind = "h", X1 = cTop.X - 28, Y1 = mx.Y - 5, X2 = cTop.X + 26, Y2 = mx.Y - 5, Offset = 8, Model = true, Arrange = true });
            foreach (var r in new[] { 6.0, 7.0, 8.0 })
                dd.Dims.Add(new DrawingDim { View = "top", Kind = "dia", X1 = cTop.X, Y1 = cTop.Y, X2 = r, Y2 = 45, Model = true, Arrange = true });
            dd.ArrangeDims(sc);
            var texts = DimTexts(dd, sc);
            int over = 0; var pairs = new List<string>();
            static string Tx(Vector2d[] t) => $"({t[0].X:0.#},{t[0].Y:0.#})";
            for (int i = 0; i < texts.Count; i++) for (int j = i + 1; j < texts.Count; j++) if (Ov(texts[i], texts[j])) { over++; pairs.Add(Tx(texts[i]) + LastDimTexts[i].Text + "~" + Tx(texts[j]) + LastDimTexts[j].Text); }
            // ряды: на каждой стороне вида — отступы 10, 17, 24 …
            var rows = dd.Dims.Where(d => d.Arrange && !d.Hidden && d.Kind is "h" or "v").GroupBy(d => (d.View, d.Kind, Math.Sign(d.Offset)))
                .Select(gr => gr.Select(d => Math.Abs(d.Offset)).OrderBy(x => x).ToList()).ToList();
            bool rowsOk = rows.All(r => r.Select((x, i) => Math.Abs(x - (10 + 7 * i)) < 1e-9).All(b => b));
            Log(new { test = "KRN-DRAW-ARRANGE-K159", dims = dd.Dims.Count, texts = texts.Count, overlaps = over, pairs, dimsList = dd.Dims.Select(d => $"{d.View} {d.Kind} {d.Value:0.##} {d.Offset:0.#} {d.Y2:0}"), rows = rows.Select(r => string.Join("/", r)), angles = dd.Dims.Where(d => d.Kind == "dia" && d.Model).Select(d => d.Y2) });
            Require(texts.Count >= 10, $"dimension texts {texts.Count}");
            Require(over == 0, $"overlapping dimension texts: {over}");
            Require(rowsOk && rows.Any(r => r.Count >= 2), "rows of parallel dimensions are not 10 + 7k mm");
        });

        Check("KRN-DRAW-ROTATE-K159", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front", Angle = 30 });
            var sc = DrawingScene.Build(dd, new[] { body });
            dd.Dims.Add(new DrawingDim { View = "front", Kind = "h", X1 = c.X - 50, Y1 = c.Z - 50, X2 = c.X + 50, Y2 = c.Z - 50, Offset = -10 });
            var g = dd.Compose(sc, new FieldSource());
            var b = dd.Boxes["front"];
            double want = 100 * (Math.Cos(Math.PI / 6) + Math.Sin(Math.PI / 6));
            var pt = new Vector2d(c.X + 17, c.Z - 23); var sp = dd.ToSheet("front", pt); double back = (dd.ToView("front", sp.X, sp.Y) - pt).Length;
            var (P1, P2, U, _, _, _, _) = dd.DimFrame(dd.Dims[0]);
            double ang = Math.Atan2(U.Y, U.X) * 180 / Math.PI, angP = Math.Atan2(P2.Y - P1.Y, P2.X - P1.X) * 180 / Math.PI;
            Log(new { test = "KRN-DRAW-ROTATE-K159", w = Math.Round(b.X1 - b.X0, 6), h = Math.Round(b.Y1 - b.Y0, 6), want = Math.Round(want, 6), back, ang, angP });
            Require(Math.Abs(b.X1 - b.X0 - want) < 1e-6 && Math.Abs(b.Y1 - b.Y0 - want) < 1e-6, $"rotated box {b.X1 - b.X0:0.###}×{b.Y1 - b.Y0:0.###}, want {want:0.###}");
            Require(back < 1e-9, $"sheet↔view {back}");
            Require(Math.Abs(ang - 30) < 1e-9 && Math.Abs(angP - 30) < 1e-6, $"dimension direction {ang:0.###}°, points {angP:0.###}°");
            Require(g.InBounds(), "sheet bounds");
        });

        Check("KRN-DRAW-UNITS-K159", () =>
        {
            var dd = new DrawingDocument { ModelUnits = "in" };
            var dim = new DrawingDim { Kind = "h", X1 = 0, X2 = 100 };
            string a = dd.CaptionOf(dim);
            dd.Units = "mm"; string b = dd.CaptionOf(dim);
            dd.Units = "cm"; string c = dd.CaptionOf(dim);
            Log(new { test = "KRN-DRAW-UNITS-K159", inch = a, mm = b, cm = c });
            Require(a == "3,937" && b == "100" && c == "10", $"captions {a} / {b} / {c}");
        });

        Check("KRN-DRAW-MOVEDIM-K159", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument { Scale = 1 }; dd.EnsureViews();
            var sc = DrawingScene.Build(dd, new[] { body });
            dd.AutoDimensions(sc);
            dd.Compose(sc, new FieldSource());
            var h = dd.Dims.First(d => d.View == "front" && d.Kind == "h");
            double L0 = dd.DimFrame(h).L;
            dd.MoveDim(h, 3, -5, 0, 0);
            double L1 = dd.DimFrame(h).L;
            var dia = dd.Dims.First(d => d.Kind == "dia");
            var cs = dd.ToSheet(dia.View, new Vector2d(dia.X1, dia.Y1));
            dd.MoveDim(dia, 0, 0, cs.X - 10, cs.Y + 10);
            double a = dia.Y2;
            dd.Compose(sc, new FieldSource());   // раскладка не трогает перенесённые
            Log(new { test = "KRN-DRAW-MOVEDIM-K159", L0, L1, a, after = dia.Y2, arrange = h.Arrange || dia.Arrange });
            Require(Math.Abs(L1 - (L0 - 5)) < 1e-9, $"dimension line {L0:0.###} → {L1:0.###}, want −5");
            Require(Math.Abs(a - 135) < 1e-9 && Math.Abs(dia.Y2 - 135) < 1e-9 && !h.Arrange && !dia.Arrange, $"leader angle {a} / {dia.Y2}");
        });
    }
}
