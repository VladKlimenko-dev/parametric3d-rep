using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;

static partial class Program
{
    /// <summary>
    /// k162 (замечания 05.11, 01:23, п. 7–8, файл детали пользователя):
    /// KRN-DRAW-HLROWN-K162 — цилиндр R50 вдоль Z длиной 200 с круглым карманом R20 снизу (до y = −38, у z = 100): на виде сверху
    /// (взгляд вдоль −Y) карман — за цилиндром: видимых линий в круге кармана нет (рёбра кармана лежат на той же
    /// цилиндрической грани, что и её передняя сторона; прежде своя грань не закрывала ребро). Мутация P3D_HLRSKIPNEAR=0 —
    /// падает. Очерки цилиндра (x = ±50) — без разрывов: покрыто не меньше 99 % длины.
    /// </summary>
    static void Drawing162Checks()
    {
        Check("KRN-DRAW-HLROWN-K162", () =>
        {
            var cyl = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 50, 200);
            var bodies = new List<TSolid> { cyl };
            // круглый карман снизу: цилиндр R20 вдоль Y от −70 до −38 в z = 100
            var cutter = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 20, 32), Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitX, -90));
            cutter = Parametric3D.Kernel.Ops.TransformOp.Copy(cutter, Parametric3D.Kernel.Ops.TransformOp.Move(new Vector3d(0, -70, 100)));
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(bodies, cutter, new List<string>());
            var body = bodies.Single();
            var lines = ViewProjection.Project(body, ViewFrame.Of("top"));
            // вид сверху: X вправо, −Z вверх (координата Y вида = −z)
            var bad = lines.Where(l => l.Visible).SelectMany(l => l.Points).Where(q => Math.Abs(q.X) < 19 && -q.Y > 81 && -q.Y < 119).ToList();
            double Cover(double x)
            {
                var segs = lines.Where(l => l.Visible).SelectMany(l => l.Points.Zip(l.Points.Skip(1))).Where(s => Math.Abs(s.First.X - x) < 0.3 && Math.Abs(s.Second.X - x) < 0.3)
                    .Select(s => (Math.Min(-s.First.Y, -s.Second.Y), Math.Max(-s.First.Y, -s.Second.Y))).OrderBy(s => s.Item1).ToList();
                double cov = 0, end = double.MinValue;
                foreach (var (a, b) in segs) { double a2 = Math.Max(a, end); if (b > a2) cov += b - a2; end = Math.Max(end, b); }
                return cov / 200;
            }
            double cr = Cover(50), cl = Cover(-50);
            int hiddenPocket = lines.Where(l => !l.Visible).SelectMany(l => l.Points).Count(q => Math.Abs(q.X) < 21 && -q.Y > 79 && -q.Y < 121);
            Log(new { test = "KRN-DRAW-HLROWN-K162", hiddenPocket, kinds = lines.Select(l => $"{l.Kind}{(l.Visible ? "" : "-h")}:{l.Points.Count}"), visibleInsidePocket = bad.Count, outlineRight = Math.Round(cr, 4), outlineLeft = Math.Round(cl, 4), lines = lines.Count });
            Require(bad.Count == 0, $"pocket edges behind the cylinder drawn visible: {bad.Count} points");
            Require(hiddenPocket > 10, $"the pocket is not shown by hidden lines: {hiddenPocket}");
            Require(cr > 0.99 && cl > 0.99, $"outline coverage {cr:0.###} / {cl:0.###}");
        });
    }
}
