using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;

static partial class Program
{
    /// <summary>Куб 100 со сквозным отверстием Ø30 по Z через центр (эталон проверок чертежа).</summary>
    static (TSolid Body, Vector3d Centre) CubeWithHole()
    {
        var p = new Part("hlr"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
        var cube = p.Bodies.Single();
        var mv = Parametric3D.Kernel.Tess.Tessellator.Tessellate(cube).Vertices.Select(v => (Vector3d)v.P).ToList();
        var mn = new Vector3d(mv.Min(v => v.X), mv.Min(v => v.Y), mv.Min(v => v.Z)); var mx = new Vector3d(mv.Max(v => v.X), mv.Max(v => v.Y), mv.Max(v => v.Z));
        var c = (mn + mx) / 2;
        var bodies = new List<TSolid> { cube };
        Parametric3D.Kernel.Ops.BooleanOp.Subtract(bodies, Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(c.X, c.Y, mn.Z - 5), 15, 110), new List<string>());
        return (bodies.Single(), c);
    }

    /// <summary>
    /// k156 (D2–D5):
    /// KRN-DRAW-DIMS-K156 — автоматические размеры куба с отверстием: ширина и высота на главном 100 и 100, глубина на виде
    /// сверху 100, «Ø30» на виде сверху; размеры модели: эскиз прямоугольника 60×40 с окружностью R8 на плоскости XY — на виде
    /// сверху 60 (h), 40 (v), Ø16 / R8; подписи на листе («100», «Ø30»), стрелки (залитые треугольники).
    /// KRN-DRAW-SECTION-K156 — разрез А-А через ось отверстия (взгляд спереди, плоскость Y = середина): фигура сечения —
    /// прямоугольник 100×100 без полосы 30×100: площадь штриховки 7000 мм² (до 1 %); на виде линии отверстия видимые (за
    /// плоскостью — стенка отверстия), невидимых нет; линия разреза на виде сверху — 2 толстых штриха, 2 стрелки, буквы «А»;
    /// подпись «А-А».
    /// KRN-DRAW-DETAIL-K156 — выносной элемент: все точки линий внутри круга, масштаб вида 2:1 подписан «Б (2:1)», круг на
    /// виде-родителе.
    /// KRN-DRAW-ASM-K156 — сборка из двух тел (куб и цилиндр перед ним): общая сцена скрытия — рёбра куба за цилиндром на
    /// главном виде невидимы (мутация P3D_HLRSHARED=0 — видимы, проверка падает); позиции — по одной на деталь, номера 1 и 2.
    /// KRN-DRAW-NOTES-K156 — шероховатость, рамка допуска с базой, знак базы, таблица отверстий: примитивы на листе; DXF —
    /// линии, тексты, слои ГОСТ, штриховые; запись чертежа JSON без потерь.
    /// </summary>
    static void Drawing156Checks()
    {
        static double Len(ViewLine l) { double s = 0; for (int i = 1; i < l.Points.Count; i++) s += (l.Points[i] - l.Points[i - 1]).Length; return s; }

        Check("KRN-SAVE-AREA-K156", () =>
        {
            // k156 (замечание 04.11, 19:20 — долгая работа с файлами): запись тела не считает площадь детали (у HelSweep — 75 с из
            // 76 с записи); после открытия площадь — по запросу, та же. Мутация P3D_AREAATSAVE=0 — считается при записи.
            var p = new Part("area"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
            Require(p.AreaPending, "the area is not lazy after the build");
            string text = DocumentFile.Save(p, null, withDisplay: true);
            Log(new { test = "KRN-SAVE-AREA-K156", pendingAfterSave = p.AreaPending, brep = text.Contains("\"Brep\"") });
            Require(p.AreaPending, "saving computed the area");
            var (q, _) = DocumentFile.Load(text);
            Require(q.RestoredFromFile && q.LoadNote == LoadNoteKind.Restored, "restored");
            Require(Math.Abs(q.Area - 60000) < 1e-6 && Math.Abs(p.Area - 60000) < 1e-6, $"area after open {q.Area}");
            // файл без тела — отметка «сетка без тел»
            var stripped = System.Text.RegularExpressions.Regex.Replace(text, "\"Brep\":\\s*\"[^\"]*\",?", "");
            var (r, _) = DocumentFile.Load(stripped);
            Require(!r.RestoredFromFile && r.LoadNote == LoadNoteKind.CachedNoBodies, $"load note {r.LoadNote}");
        });

        Check("KRN-FILE-CHECKPOINT-K156", () =>
        {
            // k156: СОСТОЯНИЕ ПЕРЕД ПОСЛЕДНЕЙ ОПЕРАЦИЕЙ В ДОКУМЕНТЕ. Деталь из 10 операций (выдавливания, вырезы, отверстия,
            // скругления) записана и открыта (тело из файла); правка последней операции перестраивает только её (LastStartedAt =
            // номер последней), объём — как при полном перестроении той же правки с начала. Мутация P3D_SAVECHECKPOINT=0 — с начала.
            var p = Parametric3D.App.Diagnostics.TestParts.Realistic(3, 10); p.Rebuild(null);
            string text = DocumentFile.Save(p, null, withDisplay: true);
            var (q, qp) = DocumentFile.Load(text);
            Require(q.RestoredFromFile, "not restored");
            int last = q.Features.Count - 1;
            while (last > 0 && q.Features[last].Suppressed) last--;
            var lf = q.Features[last];
            Log(new { test = "KRN-FILE-CHECKPOINT-K156", features = q.Features.Count, last = lf.GetType().Name, checkpoints = q.CheckpointCount, sizeMb = Math.Round(text.Length / 1e6, 2) });
            // правка последней операции: подавить и вернуть — тот же итог; иначе — изменение числа
            bool Edit(Parametric3D.Model.Features.Feature f)
            {
                switch (f)
                {
                    case Parametric3D.Model.Features.RoundFeature r: r.Radius = new Parametric3D.Model.Features.ValueRef(r.Radius.Value * 0.8); return true;
                    case Parametric3D.Model.Features.ChamferFeature c: c.D1 = new Parametric3D.Model.Features.ValueRef(c.D1.Value * 0.8); c.D2 = new Parametric3D.Model.Features.ValueRef(c.D2.Value * 0.8); return true;
                    case Parametric3D.Model.Features.ExtrudeFeature e: e.Side1.Distance = new Parametric3D.Model.Features.ValueRef(e.Side1.Distance.Value * 0.8); return true;
                    case Parametric3D.Model.Features.HoleFeature h: h.Diameter = new Parametric3D.Model.Features.ValueRef(h.Diameter.Value * 0.8); return true;
                }
                return false;
            }
            Require(Edit(lf), "the last feature type is not edited by the check: " + lf.GetType().Name);
            q.MarkDirtyFrom(lf); q.Rebuild(qp);
            Log(new { test = "KRN-FILE-CHECKPOINT-K156", startedAt = q.LastStartedAt, executed = q.LastExecuted, volume = Math.Round(q.Volume, 4) });
            Require(q.LastStartedAt == last, $"the edit rebuilt from feature {q.LastStartedAt}, want {last}");
            // эталон: та же правка на детали, перестроенной с начала
            var (r0, rp) = DocumentFile.Load(text);
            Edit(r0.Features[last]); r0.MarkDirtyFrom(r0.Features[0]); r0.Rebuild(rp);
            Require(Math.Abs(q.Volume - r0.Volume) < 1e-6 * Math.Max(1, r0.Volume) && q.Features.Count(f => f.Error != null) == r0.Features.Count(f => f.Error != null), $"volume {q.Volume} vs full {r0.Volume}");
            Require(q.Bodies.Count == r0.Bodies.Count && q.Bodies.All(b => SolidValidator.Validate(b).IsValid), "bodies");
        });

        Check("KRN-DRAW-DIMS-K156", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument(); dd.EnsureViews();
            var sc = DrawingScene.Build(dd, new[] { body });
            dd.AutoDimensions(sc);
            Log(new { test = "KRN-DRAW-DIMS-K156", dims = dd.Dims.Select(d => $"{d.View} {d.Kind} {d.Caption(true)}") });
            Require(dd.Dims.Count(d => d.View == "front" && d.Kind == "h" && Math.Abs(d.Value - 100) < 1e-6) == 1, "front width 100");
            Require(dd.Dims.Count(d => d.View == "front" && d.Kind == "v" && Math.Abs(d.Value - 100) < 1e-6) == 1, "front height 100");
            Require(dd.Dims.Count(d => d.View == "top" && d.Kind == "v" && Math.Abs(d.Value - 100) < 1e-6) == 1, "top depth 100");
            Require(dd.Dims.Count(d => d.Kind == "dia" && Math.Abs(d.Value - 30) < 1e-6 && d.Caption(true) == DrawingDocument.Dia + "30") == 1, "hole Ø30");
            var g = dd.Compose(sc, new FieldSource());
            Require(g.Texts.Count(t => t.Text == "100") == 3 && g.Texts.Any(t => t.Text == DrawingDocument.Dia + "30"), "dimension texts on the sheet");
            Require(g.Fills.Count >= 7 && g.InBounds(), $"arrows: {g.Fills.Count}");
            // вертикальный размер — текст повёрнут на 90°
            Require(g.Texts.Any(t => t.Text == "100" && Math.Abs(t.Angle - 90) < 1e-9), "vertical dimension text rotated");
            // размеры модели: эскиз на XY — вид сверху
            var part = new Part("md");
            var sk = new Parametric3D.Model.Sketching.Sketch("s", Parametric3D.Kernel.Frame3.XY);
            var a = new Parametric3D.Model.Sketching.SkPoint(new Vector2d(0, 0)); var b = new Parametric3D.Model.Sketching.SkPoint(new Vector2d(60, 0)); var e = new Parametric3D.Model.Sketching.SkPoint(new Vector2d(60, 40));
            var circ = new Parametric3D.Model.Sketching.SkCircle(new Parametric3D.Model.Sketching.SkPoint(new Vector2d(30, 20)), 8);
            var d1 = new Parametric3D.Model.Sketching.SkDimension(Parametric3D.Model.Sketching.DimensionType.DistanceHorizontal, 60, a, b);
            var d2 = new Parametric3D.Model.Sketching.SkDimension(Parametric3D.Model.Sketching.DimensionType.DistanceVertical, 40, b, e);
            var d3 = new Parametric3D.Model.Sketching.SkDimension(Parametric3D.Model.Sketching.DimensionType.Diameter, 16, circ);
            var d4 = new Parametric3D.Model.Sketching.SkDimension(Parametric3D.Model.Sketching.DimensionType.Radius, 8, circ) { Reference = true };
            sk.Dimensions.Add(d1); sk.Dimensions.Add(d2); sk.Dimensions.Add(d3); sk.Dimensions.Add(d4);
            part.Add(sk);
            int n = dd.ModelDimensions(part);
            var md = dd.Dims.Where(x => x.Model).ToList();
            Log(new { test = "KRN-DRAW-DIMS-K156", model = md.Select(x => $"{x.View} {x.Kind} {x.Value}") });
            Require(n == 3 && md.All(x => x.View == "top"), "model dims: 3 on the top view (reference one skipped)");
            Require(md.Any(x => x.Kind == "h" && Math.Abs(x.Value - 60) < 1e-9) && md.Any(x => x.Kind == "v" && Math.Abs(x.Value - 40) < 1e-9) && md.Any(x => x.Kind == "dia" && Math.Abs(x.Value - 16) < 1e-9), "model dim values");
        });

        Check("KRN-DRAW-SECTION-K156", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument(); dd.Views.Add(new DrawingView { Name = "top" });
            var sc0 = DrawingScene.Build(dd, new[] { body });
            var sv = dd.AddSection(sc0, "top", "front");
            var sc = DrawingScene.Build(dd, new[] { body });
            var bnd = sc.SectionBoundary[sv.Name];
            double area = SectionCut.Area(bnd, 0.02);
            var lines = sc.Lines[sv.Name];
            Log(new { test = "KRN-DRAW-SECTION-K156", sv.Name, sv.CutDepth, area = Math.Round(area, 1), lines = lines.Count, hidden = lines.Count(l => !l.Visible), problems = sc.Problems });
            Require(Math.Abs(area - 7000) < 70, $"section area {area:0.#}, want 7000");
            Require(Math.Abs(sv.CutDepth + c.Y) < 1e-6, $"cut depth {sv.CutDepth} (plane Y = {c.Y})");
            Require(lines.Count(l => !l.Visible) == 0 || !sv.ShowHidden, "hidden lines in the section");
            var g = dd.Compose(sc, new FieldSource());
            int thickCut = g.Lines.Count(l => l.Kind == LineKind.Thick && Math.Abs(Math.Sqrt((l.X2 - l.X1) * (l.X2 - l.X1) + (l.Y2 - l.Y1) * (l.Y2 - l.Y1)) - 10) < 1e-6);
            Require(thickCut == 2 && g.Texts.Count(t => t.Text == sv.Label) == 2 && g.Texts.Any(t => t.Text == $"{sv.Label}-{sv.Label}"), $"cutting line: strokes {thickCut}, letters {g.Texts.Count(t => t.Text == sv.Label)}");
            int hatch = g.Polylines.Count(q => q.Kind == LineKind.Thin && q.Points.Count == 2 && Math.Abs(Math.Abs(q.Points[1].X - q.Points[0].X) - Math.Abs(q.Points[1].Y - q.Points[0].Y)) < 1e-6);
            Require(hatch > 10, $"hatch lines at 45°: {hatch}");
        });

        Check("KRN-DRAW-DETAIL-K156", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument(); dd.Views.Add(new DrawingView { Name = "top" });
            var sc0 = DrawingScene.Build(dd, new[] { body });
            dd.Compose(sc0, new FieldSource());
            // круг на кромке отверстия
            var centre = new Vector2d(c.X + 15, c.Y); double r = 8;
            var dv = dd.AddDetail("top", centre, r, 2);
            var sc = DrawingScene.Build(dd, new[] { body });
            var lines = sc.Lines[dv.Name];
            Require(lines.Count > 0 && lines.All(l => l.Points.All(q => (q - centre).Length <= r + 1e-6)), "detail lines inside the circle");
            var g = dd.Compose(sc, new FieldSource());
            Require(g.Texts.Any(t => t.Text == $"{dv.Label} (2:1)") && g.Circles.Count(ci => Math.Abs(ci.R - r * dd.Xf["top"].S) < 1e-6) == 1, "detail caption and circle on the parent");
            Require(Math.Abs(dd.Xf[dv.Name].S - 2) < 1e-12, "detail scale 2:1");
        });

        Check("KRN-DRAW-ASM-K156", () =>
        {
            var p = new Part("a"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
            var cube = p.Bodies.Single();
            var mv = Parametric3D.Kernel.Tess.Tessellator.Tessellate(cube).Vertices.Select(v => (Vector3d)v.P).ToList();
            var mn = new Vector3d(mv.Min(v => v.X), mv.Min(v => v.Y), mv.Min(v => v.Z)); var mx = new Vector3d(mv.Max(v => v.X), mv.Max(v => v.Y), mv.Max(v => v.Z));
            // цилиндр Ø200 перед кубом (ближе к наблюдателю вида спереди: Y меньше) — закрывает куб целиком
            var cyl = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d((mn.X + mx.X) / 2, mn.Y - 150, mn.Z - 10), 100, 120);
            var dd = new DrawingDocument(); dd.Views.Add(new DrawingView { Name = "front" });
            var sc = DrawingScene.Build(dd, new[] { cube, cyl }, new[] { "Корпус", "Втулка" });
            var cubeVisible = sc.Lines["front"].Where(l => l.Body == 0 && l.Visible).Sum(Len);
            Log(new { test = "KRN-DRAW-ASM-K156", cubeVisible = Math.Round(cubeVisible, 2), cylVisible = Math.Round(sc.Lines["front"].Where(l => l.Body == 1 && l.Visible).Sum(Len), 2) });
            Require(cubeVisible < 1, $"cube edges behind the cylinder are visible: {cubeVisible:0.##}");
            dd.Compose(sc, new FieldSource());
            dd.AutoBalloons(sc, new Dictionary<string, int> { ["Корпус"] = 1, ["Втулка"] = 2 });
            var balloons = dd.Notes.Where(n => n.Kind == "balloon").ToList();
            Require(balloons.Count == 1 && balloons[0].Position == 2, $"balloons: {string.Join(",", balloons.Select(b => b.Position))} (the hidden part has no visible line)");
            // куб сбоку от цилиндра: обе видимы — две позиции
            var cyl2 = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new Vector3d(mx.X + 60, (mn.Y + mx.Y) / 2, mn.Z), 20, 50);
            var sc2 = DrawingScene.Build(dd, new[] { cube, cyl2 }, new[] { "Корпус", "Втулка" });
            dd.Compose(sc2, new FieldSource());
            dd.AutoBalloons(sc2, new Dictionary<string, int> { ["Корпус"] = 1, ["Втулка"] = 2 });
            var b2 = dd.Notes.Where(n => n.Kind == "balloon").OrderBy(n => n.Position).ToList();
            Require(b2.Count == 2 && b2[0].Position == 1 && b2[1].Position == 2 && Math.Abs(b2[0].Y - b2[1].Y) >= 9 - 1e-9, "two balloons, 9 mm apart");
        });

        Check("KRN-DRAW-NOTES-K156", () =>
        {
            var (body, c) = CubeWithHole();
            var dd = new DrawingDocument(); dd.EnsureViews();
            var sc = DrawingScene.Build(dd, new[] { body });
            dd.Compose(sc, new FieldSource());
            int f0 = 0;
            dd.Notes.Add(new DrawingNote { Kind = "rough", Value = "Ra 6,3" });
            dd.Notes.Add(new DrawingNote { Kind = "gdt", Symbol = "perp", Value = "0,05", Datums = "А", X = 60, Y = 80, View = "front", HasTarget = true, TX = c.X + 50, TY = c.Z });
            dd.Notes.Add(new DrawingNote { Kind = "datum", Text = "А", X = 60, Y = 70, View = "front", HasTarget = true, TX = c.X, TY = c.Z - 50 });
            dd.Notes.Add(new DrawingNote { Kind = "holes", View = "top", X = 30, Y = 100 });
            var g = dd.Compose(sc, new FieldSource());
            Log(new { test = "KRN-DRAW-NOTES-K156", texts = g.Texts.Count, fills = g.Fills.Count, circles = g.Circles.Count });
            Require(g.Texts.Any(t => t.Text == "Ra 6,3"), "roughness value");
            Require(g.Texts.Any(t => t.Text == "0,05") && g.Texts.Count(t => t.Text == "А") >= 2, "tolerance frame and datum letters");
            Require(g.Texts.Any(t => t.Text == "Отв.") && g.Texts.Any(t => t.Text == "30"), "hole table: header and Ø30");
            Require(g.Fills.Count > f0 && g.InBounds(), "fills / bounds");
            dd.ShowGdt = false;
            var g2 = dd.Compose(sc, new FieldSource());
            Require(!g2.Texts.Any(t => t.Text == "0,05"), "GD&T layer off");
            // DXF
            var dxf = DxfWriter.Write(new[] { g });
            int lines = System.Text.RegularExpressions.Regex.Matches(dxf, "(^|\n)0\nLINE\n").Count, texts = System.Text.RegularExpressions.Regex.Matches(dxf, "(^|\n)0\nTEXT\n").Count;
            Log(new { test = "KRN-DRAW-NOTES-K156", dxfLines = lines, dxfTexts = texts, size = dxf.Length });
            Require(dxf.StartsWith("0\nSECTION") && dxf.TrimEnd().EndsWith("EOF") && lines > g.Lines.Count && texts == g.Texts.Count(t => t.Text.Length > 0), "DXF entities");
            Require(dxf.Contains("\nHIDDEN\n") && dxf.Contains("\nCENTER\n") && dxf.Contains("\nTHICK\n"), "DXF layers and line types");
            var back = DrawingDocument.FromJson(dd.ToJson());
            Require(back.ToJson() == dd.ToJson() && back.Notes.Count == 4 && back.Dims.Count == dd.Dims.Count, "drawing JSON round trip");
        });
    }
}
