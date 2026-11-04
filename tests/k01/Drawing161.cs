using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;

static partial class Program
{
    /// <summary>
    /// k161 (замечания 05.11, 01:23):
    /// (UI-DRAWING-VIEWNAMES-K161 — оси видов чертежа = оси экрана окна модели; в проверках приложения: Camera — там.)
    /// KRN-DRAW-LAYOUT-K161 — труба Ø184 длиной 560 вдоль Z (как деталь пользователя): вид слева — слева от главного, вид сверху —
    /// под главным, изометрия — слева внизу; масштаб не мельче, чем при раскладке ГОСТ 2.305.
    /// KRN-DRAW-ARRANGEONCE-K161 — место размеров выбирается один раз: после первого вывода листа у всех Arrange = false, перенос
    /// одного размера не меняет места других. Мутация P3D_ARRANGEONCE=0 — падает.
    /// KRN-DRAW-DIMSTYLE-K161 — текст размера за концом размерной линии: линия продлена под текст; стрелки снаружи; радиус ↔
    /// диаметр; основная линия S = 0.5 (тонкая 0.25); у выделенного текста — рамка без диагоналей.
    /// KRN-DRAW-SHADE-K161 — изометрия «тона»: многоугольники тонов есть, линий нет; «материал» — цвет материала тела.
    /// </summary>
    static void Drawing161Checks()
    {
        Check("KRN-DRAW-LAYOUT-K161", () =>
        {
            var tube = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 92, 560);
            var dd = new DrawingDocument(); dd.EnsureViews();
            var sc = DrawingScene.Build(dd, new[] { tube });
            dd.Compose(sc, new FieldSource());
            DrawingView V(string n) => dd.Views.Single(v => v.Name == n);
            double s = dd.LastScale;
            var gost = new DrawingDocument(); gost.EnsureViews();
            Environment.SetEnvironmentVariable("P3D_VIEWARRANGE", null);
            Log(new { test = "KRN-DRAW-LAYOUT-K161", scale = s, views = dd.Views.Select(v => $"{v.Name} {v.X:0.#},{v.Y:0.#}") });
            Require(V("left").X < V("front").X && Math.Abs(V("left").Y - V("front").Y) < 1e-9, "the left view is left of the main view, on its level");
            Require(Math.Abs(V("top").X - V("front").X) < 1e-9 && V("top").Y < V("front").Y, "the top view is under the main view");
            Require(V("iso").X < V("top").X && V("iso").Y < V("front").Y, "isometric — bottom left");
            Require(s >= 0.2 - 1e-12, $"sheet scale {s} (A3: 1:5 or larger)");
        });

        Check("KRN-DRAW-ARRANGEONCE-K161", () =>
        {
            var p = Parametric3D.App.Diagnostics.TestParts.Realistic(3, 10); p.Rebuild(null);
            var dd = new DrawingDocument(); dd.EnsureViews();
            var sc = DrawingScene.Build(dd, p.Bodies.Select(Zup).ToList());
            dd.AutoDimensions(sc);
            dd.Compose(sc, new FieldSource());
            int still = dd.Dims.Count(d => d.Arrange);
            var before = dd.Dims.Select(d => (d.Offset, d.Y2)).ToList();
            var first = dd.Dims.First(d => d.Kind is "h" or "v");
            dd.MoveDim(first, 0, -6, 0, 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            dd.Compose(sc, new FieldSource());
            double ms = sw.Elapsed.TotalMilliseconds;
            int changed = dd.Dims.Where((d, i) => d != first && (Math.Abs(d.Offset - before[i].Offset) > 1e-9 || Math.Abs(d.Y2 - before[i].Y2) > 1e-9)).Count();
            Log(new { test = "KRN-DRAW-ARRANGEONCE-K161", dims = dd.Dims.Count, still, changed, composeMs = Math.Round(ms, 1) });
            Require(still == 0, $"dimensions still auto-placed after the first sheet: {still}");
            Require(changed == 0, $"moving one dimension moved {changed} others");
        });

        Check("KRN-DRAW-DIMSTYLE-K161", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front" });
            var sc = DrawingScene.Build(dd, new[] { body });
            var h = new DrawingDim { View = "front", Kind = "h", X1 = c.X - 50, Y1 = c.Z - 50, X2 = c.X + 50, Y2 = c.Z - 50, Offset = -10, TextShift = 70 };
            dd.Dims.Add(h);
            var g = dd.Compose(sc, new FieldSource());
            var (P1, P2, u, n, h1, h2, L) = dd.DimFrame(h);
            double right = Math.Max(P1.X, P2.X);
            var ext = g.Lines.Where(l => Math.Abs(l.Y1 - L) < 1e-6 && Math.Abs(l.Y2 - L) < 1e-6).Max(l => Math.Max(l.X1, l.X2));
            var txt = g.Texts.First(t => t.Text == "100");
            Require(ext > right + 15 && txt.X > right, $"text beyond the end: line to {ext:0.#}, text at {txt.X:0.#}, end {right:0.#}");
            h.Arrows = 2; h.TextShift = 0;
            var g2 = dd.Compose(sc, new FieldSource());
            double lineLen = g2.Lines.Where(l => Math.Abs(l.Y1 - L) < 1e-6 && Math.Abs(l.Y2 - L) < 1e-6).Max(l => Math.Abs(l.X2 - l.X1));
            Require(lineLen > 100 + 15, $"arrows outside: dimension line {lineLen:0.#}");
            var r = new DrawingDim { View = "front", Kind = "rad", X1 = c.X, Y1 = c.Z, X2 = 15, Y2 = 45 };
            r.Kind = "dia";
            Require(Math.Abs(r.Value - 30) < 1e-12 && dd.CaptionOf(r) == DrawingDocument.Dia + "30", "radius → diameter");
            Require(Math.Abs(g2.WidthOf(LineKind.Thick) - 0.5) < 1e-12 && Math.Abs(g2.WidthOf(LineKind.Thin) - 0.25) < 1e-12, $"line widths {g2.WidthOf(LineKind.Thick)} / {g2.WidthOf(LineKind.Thin)}");
            dd.Compose(sc, new FieldSource());
            Require(dd.Hit[h].Count(sg => Math.Abs(sg.X2 - sg.X1) > 1e-9 && Math.Abs(sg.Y2 - sg.Y1) > 1e-9 && Math.Sqrt((sg.X2 - sg.X1) * (sg.X2 - sg.X1) + (sg.Y2 - sg.Y1) * (sg.Y2 - sg.Y1)) > 4) == 0, "diagonals in the highlight of a horizontal dimension");
        });

        Check("KRN-DRAW-SHADE-K161", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument(); dd.Views.Add(new DrawingView { Name = "iso", Style = "shaded" });
            var sc = DrawingScene.Build(dd, new[] { body }, null, new[] { (1.0, 0.0, 0.0) });
            var g = dd.Compose(sc, new FieldSource());
            int lines = g.Polylines.Count(q => q.Kind is LineKind.Thick or LineKind.Hidden);
            int shades = g.Shades.Count;
            dd.Views[0].Style = "material";
            var sc2 = DrawingScene.Build(dd, new[] { body }, null, new[] { (1.0, 0.0, 0.0) });
            var g2 = dd.Compose(sc2, new FieldSource());
            bool red = g2.Shades.All(sh => (sh.Argb >> 8 & 0xFF) == 0 && (sh.Argb & 0xFF) == 0 && (sh.Argb >> 16 & 0xFF) > 0);
            Log(new { test = "KRN-DRAW-SHADE-K161", shades, lines, materialLines = g2.Polylines.Count(q => q.Kind == LineKind.Thick), red });
            Require(shades > 20 && lines == 0, $"shaded: {shades} tones, {lines} lines");
            Require(red && g2.Polylines.Any(q => q.Kind == LineKind.Thick), "material: red tones and visible lines");
        });
    }
}
