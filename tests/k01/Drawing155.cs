using Parametric3D.Model;
using Parametric3D.Model.Drawing;

static partial class Program
{
    /// <summary>
    /// k155 (D1): ЛИСТ И ПЕРЕЧЕНЬ.
    /// KRN-DRAW-SHEET-K155 — раскладка по ГОСТ 2.104/2.301 и ISO 5457/7200 (эталон — размеры стандарта): лист A4 книжный 210×297,
    /// A3 альбомный 420×297; рамка ЕСКД — поля 20/5 мм, ISO — 20/10; основная надпись формы 1 — 185×55 в правом нижнем углу
    /// рамки, формы 2 — 185×40, 2а — 185×15, ISO 7200 — 180×36; все линии на листе; поля надписи — внутри надписи; шаблон —
    /// запись и чтение без потерь.
    /// KRN-DRAW-BOM-K155 — перечень из сборки: 3 одинаковые детали — одна строка «3», 2 болта — «Стандартные изделия», подсборка —
    /// строка «Сборочные единицы» (раскрытие — её детали); позиции по разделам; правка и удаление пользователя переживают
    /// обновление по сборке; строка без модели остаётся; JSON и CSV; 45 строк — два листа (форма 2, затем 2а; «Листов» = 2).
    /// Мутация P3D_BOMGROUP=0 — одинаковые детали не объединяются (проверка падает).
    /// </summary>
    static void Drawing155Checks()
    {
        Check("KRN-DRAW-SHEET-K155", () =>
        {
            var src = new FieldSource();
            src.Auto[Fields.Designation] = "АБВГ.123456.001"; src.Auto[Fields.Name] = "Корпус"; src.Auto[Fields.Mass] = "1.25";
            src.Auto[Fields.Sheet] = "1"; src.Auto[Fields.Sheets] = "1"; src.Auto[Fields.Developer] = "Иванов";
            foreach (var (fmt, land, std, form, w, h, bw, bh) in new[]
            {
                ("A4", false, SheetStandard.Eskd, TitleBlockForm.Form1, 210.0, 297.0, 185.0, 55.0),
                ("A3", true, SheetStandard.Eskd, TitleBlockForm.Form1, 420.0, 297.0, 185.0, 55.0),
                ("A4", false, SheetStandard.Eskd, TitleBlockForm.Form2, 210.0, 297.0, 185.0, 40.0),
                ("A4", false, SheetStandard.Eskd, TitleBlockForm.Form2a, 210.0, 297.0, 185.0, 15.0),
                ("A3", true, SheetStandard.Iso, TitleBlockForm.Iso7200, 420.0, 297.0, 180.0, 36.0),
            })
            {
                var t = new SheetTemplate { Format = fmt, Landscape = land, Standard = std, TitleBlock = form };
                var g = SheetLayout.Compose(t, src);
                Require(Math.Abs(g.Width - w) < 1e-9 && Math.Abs(g.Height - h) < 1e-9, $"{fmt}: sheet {g.Width}x{g.Height}");
                Require(g.InBounds(), $"{fmt}: a line outside the sheet");
                double m = std == SheetStandard.Eskd ? 5 : 10;
                var (x0, y0, x1, y1) = SheetLayout.FrameRect(t);
                Require(x0 == 20 && y0 == m && x1 == w - m && y1 == h - m, $"{fmt}: frame {x0},{y0},{x1},{y1}");
                // толстые линии рамки
                Require(g.Lines.Count(l => l.Kind == LineKind.Thick && Math.Abs(l.X1 - 20) < 1e-9 && Math.Abs(l.X2 - 20) < 1e-9 && Math.Min(l.Y1, l.Y2) <= m + 1e-9 && Math.Max(l.Y1, l.Y2) >= h - m - 1e-9) == 1, $"{fmt}: left frame line");
                // основная надпись: габарит линий справа-снизу
                var block = g.Lines.Where(l => Math.Min(l.X1, l.X2) >= x1 - bw - 1e-9 && Math.Max(l.Y1, l.Y2) <= y0 + bh + 1e-9 && Math.Min(l.Y1, l.Y2) >= y0 - 1e-9).ToList();
                double bx0 = block.Min(l => Math.Min(l.X1, l.X2)), by1 = block.Max(l => Math.Max(l.Y1, l.Y2));
                Require(Math.Abs(bx0 - (x1 - bw)) < 1e-9 && Math.Abs(by1 - (y0 + bh)) < 1e-9, $"{fmt} {form}: title block {x1 - bx0}x{by1 - y0}, want {bw}x{bh}");
                var des = g.Texts.FirstOrDefault(x => x.Field == Fields.Designation);
                Require(des != null && des.Text == "АБВГ.123456.001" && des.X > x1 - bw && des.Y < y0 + bh, $"{fmt} {form}: designation field");
                if (form == TitleBlockForm.Form1)
                {
                    Require(g.Texts.Any(x => x.Field == Fields.Mass && x.Text == "1.25") && g.Texts.Any(x => x.Field == Fields.Name && x.Text == "Корпус"), "form 1: mass or name");
                    Require(g.Texts.Any(x => x.Text == "Разраб.") && g.Texts.Any(x => x.Field == Fields.Developer && x.Text == "Иванов"), "form 1: developer row");
                    Require(g.Texts.Count(x => x.Angle == 90) == 5, "ESKD: five side columns on the binding margin");
                }
                Log(new { test = "KRN-DRAW-SHEET-K155", fmt, form = form.ToString(), lines = g.Lines.Count, texts = g.Texts.Count });
            }
            // ручное значение поля — поверх автоматического; шаблон — запись и чтение
            var tpl = SheetLayout.Default("A3", true);
            tpl.Values[Fields.Designation] = "РУЧН.001";
            tpl.Items.Add(new TemplateItem { Kind = "text", X = 30, Y = 100, Height = 5, Text = "1. Размеры для справок.\n2. Острые кромки притупить." });
            tpl.Items.Add(new TemplateItem { Kind = "table", X = 30, Y = 250, Height = 8, Rows = 3, Columns = new() { 20, 40 }, Cells = new() { new() { "A", "B" } } });
            var back = SheetTemplate.FromJson(tpl.ToJson());
            Require(back.ToJson() == tpl.ToJson() && back.Values[Fields.Designation] == "РУЧН.001" && back.Items.Count == 2, "template round trip");
            var g2 = SheetLayout.Compose(back, src);
            Require(g2.Texts.Any(x => x.Field == Fields.Designation && x.Text == "РУЧН.001"), "manual field value");
            Require(g2.Texts.Any(x => x.Text == "2. Острые кромки притупить." && Math.Abs(x.Y - (100 - 8)) < 1e-9), "template text lines");
        });

        Check("KRN-DRAW-HLR-K155", () =>
        {
            // куб 100 со сквозным отверстием Ø30 по Z (эталон — размеры модели): вид спереди — контур 4×100 видим, отверстие —
            // две невидимые линии длиной 100 на x = cx ± 15; вид сверху — окружность Ø30 видима (длина 94.25), нижняя
            // окружность под ней и задние рёбра — не рисуются; изометрия — видимые и невидимые линии есть
            var p = new Part("hlr"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
            var cube = p.Bodies.Single();
            var mv = Parametric3D.Kernel.Tess.Tessellator.Tessellate(cube).Vertices.Select(v => (OpenTK.Mathematics.Vector3d)v.P).ToList();
            var bb = (Min: new OpenTK.Mathematics.Vector3d(mv.Min(v => v.X), mv.Min(v => v.Y), mv.Min(v => v.Z)), Max: new OpenTK.Mathematics.Vector3d(mv.Max(v => v.X), mv.Max(v => v.Y), mv.Max(v => v.Z)));
            var c = (bb.Min + bb.Max) / 2;
            var bodies = new List<Parametric3D.Kernel.Topology.TSolid> { cube };
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(bodies, Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(new OpenTK.Mathematics.Vector3d(c.X, c.Y, bb.Min.Z - 5), 15, 110), new List<string>());
            var body = bodies.Single();
            Log(new { test = "KRN-DRAW-HLR-K155", surfaces = body.Faces.Select(f => (Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name ?? "-") + "/" + f.Surface?.GetType().Name).Distinct() });
            double Len(ViewLine l) { double s = 0; for (int i = 1; i < l.Points.Count; i++) s += (l.Points[i] - l.Points[i - 1]).Length; return s; }
            var front = ViewProjection.Project(body, ViewFrame.Of("front"));
            double visF = front.Where(l => l.Visible).Sum(Len);
            var hidF = front.Where(l => !l.Visible && l.Kind != ViewLineKind.Tangent).ToList();
            Log(new { test = "KRN-DRAW-HLR-K155", view = "front", lines = front.Count, visible = Math.Round(visF, 3), hidden = hidF.Select(l => $"{l.Kind} {Math.Round(Len(l), 2)} x={Math.Round(l.Points[0].X - c.X, 2)}") });
            Require(Math.Abs(visF - 400) < 0.5, $"front: visible length {visF:0.###}, want 400");
            var vert = hidF.Where(l => Len(l) > 99 && Math.Abs(l.Points[0].X - l.Points[^1].X) < 0.05).ToList();
            Require(vert.Count == 2 && vert.All(l => Math.Abs(Math.Abs(l.Points[0].X - c.X) - 15) < 0.3), "front: two hidden hole lines at ±15");
            Require(hidF.Sum(Len) < 2 * 100 + 1, $"front: extra hidden lines {hidF.Sum(Len):0.###}");
            var top = ViewProjection.Project(body, ViewFrame.Of("top"));
            double visT = top.Where(l => l.Visible).Sum(Len), hidT = top.Where(l => !l.Visible).Sum(Len);
            Log(new { test = "KRN-DRAW-HLR-K155", view = "top", lines = top.Count, visible = Math.Round(visT, 3), hidden = Math.Round(hidT, 3), all = top.Select(l => $"{l.Kind} {(l.Visible ? "v" : "h")} {Math.Round(Len(l), 2)} {l.Points[0].X - c.X:0.##},{l.Points[0].Y - c.Y:0.##}") });
            Require(Math.Abs(visT - (400 + Math.PI * 30)) < 0.5, $"top: visible length {visT:0.###}, want {400 + Math.PI * 30:0.###}");
            Require(hidT < 0.5, $"top: hidden length {hidT:0.###}, want 0 (all behind visible lines)");
            var iso = ViewProjection.Project(body, ViewFrame.Of("iso"));
            Log(new { test = "KRN-DRAW-HLR-K155", view = "iso", lines = iso.Count, visible = Math.Round(iso.Where(l => l.Visible).Sum(Len), 2), hidden = Math.Round(iso.Where(l => !l.Visible).Sum(Len), 2) });
            Require(iso.Any(l => l.Visible) && iso.Any(l => !l.Visible), "iso: visible and hidden lines");
            // лист чертежа A3: четыре вида, масштаб из ряда ГОСТ 2.302 (1:1 не помещается по высоте: 100 + 20 + 100 > 192 —
            // значит 1:2), проекционная связь (главный и сверху — одна вертикаль, главный и слева — одна высота), невидимые
            // линии — штриховые, осевые: перекрестие на виде сверху, ось на главном и на виде слева
            var dd = new DrawingDocument(); dd.EnsureViews();
            var proj = dd.Views.ToDictionary(v => v.Name, v => DrawingDocument.ProjectBodies(new[] { body }, v.Name));
            var axes = dd.Views.ToDictionary(v => v.Name, v => DrawingDocument.AxesOf(new[] { body }, v.Name));
            var sheet = dd.Compose(proj, new FieldSource(), axes);
            DrawingView V(string n) => dd.Views.Single(v => v.Name == n);
            Log(new { test = "KRN-DRAW-HLR-K155", sheetScale = sheet.Texts.FirstOrDefault(t => t.Field == Fields.Scale)?.Text, views = dd.Views.Select(v => $"{v.Name} {v.X:0.#},{v.Y:0.#}"), polylines = sheet.Polylines.GroupBy(q => q.Kind).Select(q => $"{q.Key} {q.Count()}") });
            Require(sheet.Texts.Any(t => t.Field == Fields.Scale && t.Text == "1:2"), "sheet scale 1:2");
            Require(Math.Abs(V("front").X - V("top").X) < 1e-9 && Math.Abs(V("front").Y - V("left").Y) < 1e-9 && V("top").Y < V("front").Y && V("left").X > V("front").X, "projection relation of views (GOST 2.305)");
            Require(sheet.InBounds() && sheet.Polylines.All(q => q.Points.All(pt => pt.X >= 20 && pt.X <= 415 && pt.Y >= 5 + 55 && pt.Y <= 292)), "views inside the frame above the title block");
            Require(sheet.Polylines.Count(q => q.Kind == LineKind.Hidden) >= 4, "hidden lines (front and left: 2 each)");
            Require(sheet.Polylines.Count(q => q.Kind == LineKind.Center) == 4, $"centre lines: {sheet.Polylines.Count(q => q.Kind == LineKind.Center)}, want 4 (cross on top, axis on front and left)");
            // оси — только у полного оборота: на виде сверху детали с отверстиями и скруглёнными углами у каждого перекрестия есть
            // окружность вокруг (отверстие), у скруглений углов перекрестий нет (P3D_AXISFULLTURN=0 — есть, проверка падает)
            var real = Parametric3D.App.Diagnostics.TestParts.Realistic(3, 10); real.Rebuild(null);
            var rb = real.Bodies.Single();
            var topLines = ViewProjection.Project(rb, ViewFrame.Of("top"));
            var crosses = ViewProjection.Axes(rb, ViewFrame.Of("top")).Where(a => a.Cross).ToList();
            bool Ring(ViewAxis a)
            {
                double len = 0;
                foreach (var l in topLines)
                    if (l.Points.All(q => Math.Abs((q - a.Center).Length - a.Radius) < 0.05)) len += Len(l);
                return len > 0.95 * 2 * Math.PI * a.Radius;
            }
            Log(new { test = "KRN-DRAW-HLR-K155", realCrosses = crosses.Count, withRing = crosses.Count(Ring) });
            Require(crosses.Count > 0 && crosses.All(Ring), $"a centre cross without a circle: {crosses.Count(a => !Ring(a))} of {crosses.Count}");
            var dback = DrawingDocument.FromJson(dd.ToJson());
            Require(dback.ToJson() == dd.ToJson() && dback.Views.Count == 4, "drawing JSON round trip");
            // перенос с проекционной связью: главный — вместе с проекционными; вид сверху — только по вертикали
            double fx = V("front").X, fy = V("front").Y, tx = V("top").X, ty = V("top").Y, ix = V("iso").X;
            Require(dd.ViewAt(fx, fy) == "front" && dd.ViewAt(tx, ty) == "top", "view under the cursor");
            dd.MoveView("front", 10, 5);
            Require(V("front").X == fx + 10 && V("top").X == tx + 10 && V("left").Y == fy + 5 && V("iso").X == ix, "moving the main view moves its projections");
            dd.MoveView("top", 7, -3);
            Require(V("top").X == tx + 10 && V("top").Y == ty + 5 - 3, "the top view moves only vertically");
        });

        Check("KRN-DRAW-BOM-K155", () =>
        {
            var asm = new Assembly("Узел");
            Part P(string name, string code, string? sub = null) { var p = new Part(name) { SubAssembly = sub }; p.Info.Code = code; asm.Add(p); return p; }
            P("Корпус", "АБВГ.001"); P("Крышка [1]", "АБВГ.002"); P("Крышка [2]", "АБВГ.002"); P("Крышка [3]", "АБВГ.002");
            P("Болт М8x20", "ГОСТ 7798-70"); P("Болт М8x20 (2)", "ГОСТ 7798-70");
            P("Вал", "АБВГ.010", "Привод"); P("Шкив", "АБВГ.011", "Привод");
            var bom = new BomDocument();
            bom.UpdateFrom(asm);
            var vis = bom.Visible.ToList();
            Log(new { test = "KRN-DRAW-BOM-K155", rows = vis.Select(r => $"{r.Section}|{r.Position}|{r.Designation}|{r.Name}|{r.Quantity}") });
            Require(vis.Count == 4, $"rows: {vis.Count}, want 4 (unit, 2 parts, standard)");
            Require(vis.Single(r => r.Designation == "АБВГ.002").Quantity == 3, "three equal covers — one row with 3");
            Require(vis.Single(r => r.Designation.StartsWith("ГОСТ")) is { Section: BomSections.Standard, Quantity: 2 }, "bolts — standard items, 2");
            Require(vis.Single(r => r.Section == BomSections.Units) is { Name: "Привод", Quantity: 1 }, "sub-assembly row");
            Require(vis.Select(r => r.Section).SequenceEqual(new[] { BomSections.Units, BomSections.Parts, BomSections.Parts, BomSections.Standard }), "section order");
            Require(vis.Select(r => r.Position).SequenceEqual(new[] { 1, 2, 3, 4 }), "positions");
            // правки пользователя
            var cover = bom.Rows.Single(r => r.Designation == "АБВГ.002");
            cover.Name = "Крышка торцевая"; cover.Note = "покупн."; cover.Manual = true;
            bom.Rows.Single(r => r.Designation == "АБВГ.001").Hidden = true;
            bom.Rows.Add(new BomRow { Section = BomSections.Materials, Name = "Герметик", Quantity = 1, Unit = "кг", Manual = true });
            P("Крышка [4]", "АБВГ.002");
            bom.UpdateFrom(asm);
            var c2 = bom.Rows.Single(r => r.Designation == "АБВГ.002");
            Require(c2.Name == "Крышка торцевая" && c2.Note == "покупн." && c2.Quantity == 4, $"manual edit after update: {c2.Name}/{c2.Quantity}");
            Require(bom.Rows.Single(r => r.Designation == "АБВГ.001").Hidden && bom.Rows.Any(r => r.Name == "Герметик"), "hidden row or user row lost");
            // раскрытие подсборки
            var ex = new BomDocument { Expand = true }; ex.UpdateFrom(asm);
            Require(ex.Visible.Count(r => r.Section == BomSections.Units) == 0 && ex.Visible.Any(r => r.Designation == "АБВГ.011"), "expanded sub-assembly");
            // запись и чтение
            var back = BomDocument.FromJson(bom.ToJson());
            Require(back.ToJson() == bom.ToJson(), "BoM JSON round trip");
            var csv = bom.ToCsv();
            Require(csv.Contains("Крышка торцевая") && csv.Contains("Герметик") && !csv.Contains("АБВГ.001"), "CSV");
            // листы: 45 строк — 2 листа
            var big = new BomDocument { Title = "Узел", Designation = "АБВГ.000 СП" };
            for (int i = 0; i < 45; i++) big.Rows.Add(new BomRow { Section = BomSections.Parts, Designation = $"АБВГ.{i + 100}", Name = $"Деталь {i}", Quantity = 1 });
            big.Renumber();
            var pages = big.Pages();
            Log(new { test = "KRN-DRAW-BOM-K155", pages = pages.Count, lines = big.TableLines().Count });
            Require(pages.Count == 2, $"pages: {pages.Count}");
            Require(pages.All(p => p.InBounds()), "a line outside the sheet");
            Require(pages[0].Texts.Any(x => x.Field == Fields.Sheets && x.Text == "2") && pages[1].Texts.Any(x => x.Field == Fields.Sheet && x.Text == "2"), "sheet numbers");
            Require(pages[0].Texts.Any(x => x.Text == "Деталь 0") && pages[1].Texts.Any(x => x.Text == "Деталь 44"), "rows across pages");
            Require(pages[0].Texts.Any(x => x.Text == "Разраб.") && !pages[1].Texts.Any(x => x.Text == "Разраб."), "form 2 first, 2a next");
            var iso = new BomDocument { Standard = SheetStandard.Iso }; iso.UpdateFrom(asm);
            var ip = iso.Pages();
            Require(ip.Count == 1 && ip[0].Texts.Any(x => x.Text == "Item") && ip[0].InBounds(), "ISO 7573 parts list");
        });
    }
}
