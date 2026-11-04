using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;

static partial class Program
{
    /// <summary>
    /// k158 (D5): вспомогательный вид и вид с разрывом.
    /// KRN-DRAW-AUX-K158 — куб 100 со срезом угла 40×40 под 45° (плоскость x + z = const): вспомогательный вид по линии среза на
    /// главном виде смотрит по нормали среза ±(1, 0, 1)/√2; наклонная грань — в натуральную величину: видимые рёбра откоса
    /// длиной 40√2 = 56.57 идут вдоль оси листа вида (на главном виде — под 45°, на прочих видах таких горизонтальных отрезков
    /// нет), рёбра по Y — длиной 100; объём тела 920000 (эталон по построению); на виде-родителе — стрелка и буква, над видом —
    /// буква; запись чертежа JSON — та же рамка. Мутация P3D_AUXFRAME=0 — вид смотрит как вид-родитель, проверка падает.
    /// KRN-DRAW-BREAK-K158 — главный вид куба с разрывом по полосе 50 мм: ширина вида на листе 50·s + промежуток 8 мм (без разрыва
    /// — 100·s), толстых линий в промежутке нет, две волнистые тонкие линии обрыва; размер ширины — 100 (по модели), его стрелки — на
    /// краях вида с разрывом; лист → вид → лист — тождество слева и справа от полосы. Мутация P3D_VIEWBREAK=0 — падает. Вертикальный габаритный размер —
    /// справа за контуром вида (мутация P3D_VDIMSIDE=0 — внутри, как в k156).
    /// </summary>
    static void Drawing158Checks()
    {
        static double Len(List<Vector2d> p) { double s = 0; for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length; return s; }

        Check("KRN-DRAW-AUX-K158", () =>
        {
            var p = new Part("aux"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
            var cube = p.Bodies.Single();
            var mv = Parametric3D.Kernel.Tess.Tessellator.Tessellate(cube).Vertices.Select(v => (Vector3d)v.P).ToList();
            var mn = new Vector3d(mv.Min(v => v.X), mv.Min(v => v.Y), mv.Min(v => v.Z)); var mx = new Vector3d(mv.Max(v => v.X), mv.Max(v => v.Y), mv.Max(v => v.Z));
            var c = (mn + mx) / 2;
            // резец — куб, повёрнутый на 45° вокруг Y, растянутый по Y (чтобы не было общих граней), грань — на плоскости среза
            var n = new Vector3d(1, 0, 1).Normalized();
            var onPlane = new Vector3d(mx.X - 20, c.Y, mx.Z - 20);
            var cc = onPlane + n * 50;
            var rot = Parametric3D.Kernel.Ops.TransformOp.Rotate(c, Vector3d.UnitY, 45);
            var stretch = new Parametric3D.Kernel.Ops.TransformOp.Xform(q => new Vector3d(q.X, c.Y + (q.Y - c.Y) * 1.5, q.Z), d => d, false);
            var cutter = Parametric3D.Kernel.Ops.TransformOp.Copy(Parametric3D.Kernel.Ops.TransformOp.Copy(cube, stretch), rot);
            cutter = Parametric3D.Kernel.Ops.TransformOp.Copy(cutter, Parametric3D.Kernel.Ops.TransformOp.Move(cc - c));
            var bodies = new List<TSolid> { cube };
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(bodies, cutter, new List<string>());
            var body = Zup(bodies.Single());
            n = new Vector3d(n.X, n.Z, -n.Y);   // k161: ось Y вверх
            double vol = AnalyticVolume.Of(body) ?? double.NaN;
            Require(Math.Abs(vol - 920000) < 1, $"body volume {vol:0.###}, want 920000");

            var dd = new DrawingDocument(); dd.Views.Add(new DrawingView { Name = "front" });
            var sc0 = DrawingScene.Build(dd, new[] { body });
            // линия среза на главном виде: видимый отрезок под 45° длиной 56.57
            var slope = sc0.Lines["front"].Where(l => l.Visible).SelectMany(l => l.Points.Zip(l.Points.Skip(1)))
                .First(s => Math.Abs((s.Second - s.First).Length - 40 * Math.Sqrt(2)) < 1e-3 && Math.Abs(Math.Abs(s.Second.X - s.First.X) - Math.Abs(s.Second.Y - s.First.Y)) < 1e-3);
            var av = dd.AddAuxiliary(sc0, "front", slope.First, slope.Second);
            var f = ViewFrame.Of(dd.DirOf(av));
            var sc = DrawingScene.Build(dd, new[] { body });
            var lines = sc.Lines[av.Name].Where(l => l.Visible).ToList();
            var segs = lines.SelectMany(l => l.Points.Zip(l.Points.Skip(1))).ToList();
            int trueLen = segs.Count(s => Math.Abs((s.Second - s.First).Length - 40 * Math.Sqrt(2)) < 1e-3 && Math.Abs(s.Second.Y - s.First.Y) < 1e-6);
            int yEdges = segs.Count(s => Math.Abs((s.Second - s.First).Length - 100) < 1e-3);
            Log(new { test = "KRN-DRAW-AUX-K158", av.Name, toward = new[] { f.Toward.X, f.Toward.Y, f.Toward.Z }, vol = Math.Round(vol, 3), trueLen, yEdges, lines = lines.Count });
            Require(Math.Abs(Math.Abs(Vector3d.Dot(f.Toward, n)) - 1) < 1e-9, $"aux view direction {f.Toward}, want ±{n}");
            Require(trueLen >= 2, $"true-length slope edges in the aux view: {trueLen}, want 2");
            Require(yEdges >= 2, $"edges along Y in the aux view: {yEdges}");
            var g = dd.Compose(sc, new FieldSource());
            Require(g.Texts.Count(t => t.Text == av.Label) == 2, $"letters {g.Texts.Count(t => t.Text == av.Label)} (arrow and caption)");
            var back = DrawingDocument.FromJson(dd.ToJson());
            var bf = ViewFrame.Of(back.DirOf(back.Views.Single(v => v.IsAux)));
            Require((bf.Right - f.Right).Length < 1e-12 && (bf.Up - f.Up).Length < 1e-12, "frame after JSON");
        });

        Check("KRN-DRAW-BREAK-K158", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument { Scale = 1 }; dd.Views.Add(new DrawingView { Name = "front" });
            var sc = DrawingScene.Build(dd, new[] { body });
            double a = c.X - 40, b = c.X + 10;
            dd.AddBreak("front", a, b);
            dd.AutoDimensions(sc);
            var g = dd.Compose(sc, new FieldSource());
            var (x0, y0, x1, y1) = dd.Boxes["front"];
            double gap = dd.Views[0].BreakGap;
            double xa = dd.ToSheet("front", new Vector2d(a, c.Z)).X;
            int thickInGap = g.Polylines.Where(q => q.Kind == LineKind.Thick).SelectMany(q => q.Points).Count(q => q.X > xa + 1e-6 && q.X < xa + gap - 1e-6);
            int wavy = g.Polylines.Count(q => q.Kind == LineKind.Thin && q.Points.Count > 8 && q.Points.Max(t => t.X) - q.Points.Min(t => t.X) is > 1 and < 3 && q.Points.Max(t => t.Y) - q.Points.Min(t => t.Y) >= y1 - y0);
            var w = dd.Dims.FirstOrDefault(d => d.View == "front" && d.Kind == "h");
            // лист → вид → лист
            var pl = new Vector2d(c.X - 45, c.Z); var pr = new Vector2d(c.X + 30, c.Z);
            var sl = dd.ToSheet("front", pl); var sr = dd.ToSheet("front", pr);
            double rl = (dd.ToView("front", sl.X, sl.Y) - pl).Length, rr = (dd.ToView("front", sr.X, sr.Y) - pr).Length;
            Log(new { test = "KRN-DRAW-BREAK-K158", width = Math.Round(x1 - x0, 3), want = 50 + gap, thickInGap, wavy, dim = w?.Value, rl, rr });
            Require(Math.Abs(x1 - x0 - (50 + gap)) < 1e-6, $"view width {x1 - x0:0.###}, want {50 + gap}");
            Require(thickInGap == 0, $"thick points inside the break gap: {thickInGap}");
            Require(wavy == 2, $"break lines {wavy}");
            Require(w != null && Math.Abs(w.Value - 100) < 1e-6, $"width dimension {w?.Value}");
            Require(rl < 1e-9 && rr < 1e-9, $"sheet↔view {rl} {rr}");
            Require(Math.Abs(sr.X - sl.X - (75 - 50 + gap)) < 1e-6, $"distance across the break {sr.X - sl.X:0.###}");
            // k158: габаритный вертикальный размер главного вида — справа за контуром (в k156 — внутри вида). Мутация P3D_VDIMSIDE=0.
            var vline = g.Lines.Where(l => Math.Abs(l.X1 - l.X2) < 1e-9 && Math.Abs(Math.Abs(l.Y2 - l.Y1) - (y1 - y0)) < 1e-6).Select(l => l.X1).ToList();
            Require(vline.Count == 1 && vline[0] > x1 + 5, $"vertical dimension line at x = {string.Join(",", vline.Select(x => x.ToString("0.#")))}, view right edge {x1:0.#}");
        });
    }
}
