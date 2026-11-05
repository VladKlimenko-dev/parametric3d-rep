using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k165 (замечание 05.11, 13:27):
    /// KRN-DRAW-CENTRENOS3D-K165 — пластина с отверстиями ⌀20 и ⌀12 на видах спереди и сзади: у одного отверстия на обоих видах
    /// один номер (номер — у оси в пространстве). Мутация P3D_CENTERNOS3D=0 — по видам: 1, 2 и 3, 4.
    /// KRN-DRAW-MARKSONCE-K165 — подпись центра, перенесённая мышью (смещение выноски), остаётся на месте после переноса
    /// размера и нового вывода листа; «Автоматические размеры» подбирают места заново. Мутация P3D_CENTERMARKSONCE=0.
    /// KRN-DRAW-LINES-K165 — основная линия 0.1 мм: S = 0.1, штриховая не толще основной.
    /// </summary>
    static void Drawing165Checks()
    {
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

        Check("KRN-DRAW-CENTRENOS3D-K165", () =>
        {
            var p = Plate();
            var dd = new DrawingDocument { Scale = 1 };
            dd.Views.Add(new DrawingView { Name = "front", X = 120, Y = 150 }); dd.Views.Add(new DrawingView { Name = "back", X = 300, Y = 150 });
            var sc = DrawingScene.Build(dd, p.Bodies);
            dd.AutoDimensions(sc);
            // размеры отверстий и на виде сзади
            foreach (var (c, r) in new[] { (new Vector2d(-30, 30), 10.0), (new Vector2d(-70, 42), 6.0) })
                dd.Dims.Add(new DrawingDim { View = "back", Kind = "dia", X1 = c.X, Y1 = c.Y, X2 = r, Y2 = 45 });
            dd.Compose(sc, new FieldSource());
            int? No(string view, Vector2d pv) => dd.CenterNos.Where(n => n.View == view && (n.PV - pv).Length < 1e-6).Select(n => (int?)n.No).FirstOrDefault();
            var f1 = No("front", new(30, 30)); var b1 = No("back", new(-30, 30));
            var f2 = No("front", new(70, 42)); var b2 = No("back", new(-70, 42));
            Log(new { test = "KRN-DRAW-CENTRENOS3D-K165", front = new[] { f1, f2 }, back = new[] { b1, b2 }, all = dd.CenterNos.Select(n => $"{n.View}:{n.No}").ToArray() });
            Require(f1 != null && f2 != null && b1 != null && b2 != null, "centres numbered on both views");
            Require(f1 == b1 && f2 == b2 && f1 != f2, $"same hole — same number: front {f1},{f2}, back {b1},{b2}");
            Require(dd.CenterNos.Select(n => n.No).Distinct().Count() == 2, "two axes — two numbers");
        });

        Check("KRN-DRAW-MARKSONCE-K165", () =>
        {
            var p = Plate();
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front", X = 200, Y = 150 });
            var sc = DrawingScene.Build(dd, p.Bodies);
            dd.AutoDimensions(sc);
            dd.Compose(sc, new FieldSource());
            Require(dd.CentreMarks.Count >= 2 && dd.CentreMarks.All(m => m.Placed), "label places chosen");
            var m0 = dd.CentreMarks[0];
            // «перенос мышью»: новое смещение выноски
            m0.DX = 31.5; m0.DY = -27.25;
            var dim = dd.Dims.First(x => x.Kind is "h" or "v");
            dd.MoveDim(dim, 0, -4, 0, 0);
            var g = dd.Compose(sc, new FieldSource());
            var c = dd.CenterNos.First(n => n.View == m0.View && (n.PV - new Vector2d(m0.CX, m0.CY)).Length < 1e-9).P;
            var e = c + new Vector2d(31.5, -27.25);
            bool shelf = dd.Hit[m0].Any(sg => Math.Abs(sg.Item1 - e.X) < 1e-6 && Math.Abs(sg.Item2 - e.Y) < 1e-6 || Math.Abs(sg.Item3 - e.X) < 1e-6 && Math.Abs(sg.Item4 - e.Y) < 1e-6);
            Log(new { test = "KRN-DRAW-MARKSONCE-K165", marks = dd.CentreMarks.Count, dx = m0.DX, dy = m0.DY, shelf });
            Require(Math.Abs(m0.DX - 31.5) < 1e-12 && Math.Abs(m0.DY + 27.25) < 1e-12 && shelf, "the moved label stays where it was put");
            dd.AutoDimensions(sc); dd.Compose(sc, new FieldSource());
            var m1 = dd.CentreMarks.First(m => m.View == m0.View && Math.Abs(m.CX - m0.CX) < 1e-9 && Math.Abs(m.CY - m0.CY) < 1e-9);
            Require(Math.Abs(m1.DX - 31.5) > 1e-6 || Math.Abs(m1.DY + 27.25) > 1e-6, "automatic dimensions place the labels again");
        });

        Check("KRN-DRAW-LINES-K165", () =>
        {
            var p = Plate();
            var dd = new DrawingDocument { Scale = 1, LineS = 0.1 }; dd.Views.Add(new DrawingView { Name = "front", X = 200, Y = 150 });
            var sc = DrawingScene.Build(dd, p.Bodies);
            var g = dd.Compose(sc, new FieldSource());
            Log(new { test = "KRN-DRAW-LINES-K165", thick = g.ThickW, hidden = g.WidthOf(LineKind.Hidden), thin = g.WidthOf(LineKind.Thin) });
            Require(Math.Abs(g.ThickW - 0.1) < 1e-12 && g.WidthOf(LineKind.Hidden) <= 0.1 + 1e-12 && g.WidthOf(LineKind.Thin) <= 0.1, "S = 0.1 mm, thin and hidden not thicker");
        });
    }
}
