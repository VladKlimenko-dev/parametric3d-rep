using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenTK.Mathematics;
using Parametric3D.Model.Drawing;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k168 (замечание 05.11, 16:17, п. 8, 10, 11, 13, 14): элементы шаблона на листе чертежа — линия (построение двумя щелчками,
/// перенос, перенос концов, удаление), таблица (переключатель видимости, перенос за шапку с притяжением к углам рамки, к
/// основной надписи и линиям шаблона, подсветка ячейки, строки и столбца, правка ячейки на листе, меню строк и столбцов);
/// куб вида у выбранной изометрии.
/// </summary>
public sealed partial class SheetWindow
{
    sealed record LineEndDrag(TemplateItem It, int End);
    sealed record TableDrag(TemplateItem It);
    sealed record CubeDrag(DrawingView View, Vector3d? Face, Vector3d? Corner);

    /// <summary>Под курсором — элемент шаблона и что именно: end0, end1, line, head, cell, row, col.</summary>
    public (TemplateItem It, string Part, int Row, int Col)? TemplateAt(double x, double y)
    {
        foreach (var it in Template.Items.Where(i => !i.Hidden).Reverse())
        {
            if (it.Kind == "line")
            {
                if (Dist(x, y, it.X, it.Y) < 1.5) return (it, "end0", -1, -1);
                if (Dist(x, y, it.X2, it.Y2) < 1.5) return (it, "end1", -1, -1);
                if (SegDist(x, y, it.X, it.Y, it.X2, it.Y2) < 1.2) return (it, "line", -1, -1);
            }
            else if (it.Kind == "table")
            {
                double w = it.TableW, h = it.TableH;
                bool inX = x >= it.X && x <= it.X + w, inY = y <= it.Y && y >= it.Y - h;
                if (inY && x < it.X && x >= it.X - 5) { var (r, _) = it.CellAt(it.X + 0.01, y); return (it, "row", r, -1); }
                if (inX && y > it.Y && y <= it.Y + 5) { var (_, c) = it.CellAt(x, it.Y - 0.01); return (it, "col", -1, c); }
                if (inX && inY) { var (r, c) = it.CellAt(x, y); return (it, r == 0 ? "head" : "cell", r, c); }
            }
        }
        return null;
    }
    static double Dist(double x, double y, double ax, double ay) => Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));
    static double SegDist(double x, double y, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-18 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1);
        return Dist(x, y, ax + dx * t, ay + dy * t);
    }

    (TemplateItem It, string Part, int Row, int Col)? _hoverTpl;
    public (TemplateItem It, string Part, int Row, int Col)? HoveredTemplate => _hoverTpl;

    List<(double, double, double, double)> RectSegsXY(double x0, double y0, double x1, double y1) =>
        new() { (x0, y0, x1, y0), (x1, y0, x1, y1), (x1, y1, x0, y1), (x0, y1, x0, y0) };

    /// <summary>Подсветка элемента шаблона: линия, таблица, ячейка, строка, столбец.</summary>
    List<(double, double, double, double)> TemplateSegs(TemplateItem it, string part, int row, int col)
    {
        if (it.Kind == "line") return part is "end0" or "end1" ? Ring(part == "end0" ? it.X : it.X2, part == "end0" ? it.Y : it.Y2, VertexR) .Concat(new[] { (it.X, it.Y, it.X2, it.Y2) }).ToList() : new() { (it.X, it.Y, it.X2, it.Y2) };
        double w = it.TableW, h = it.TableH, rh = it.RowH;
        return part switch
        {
            "row" => RectSegsXY(it.X, it.Y - (row + 1) * rh, it.X + w, it.Y - row * rh),
            "col" => RectSegsXY(it.ColX(col), it.Y - h, it.ColX(col + 1), it.Y),
            "cell" or "head" when row >= 0 && col >= 0 && part == "cell" => RectSegsXY(it.ColX(col), it.Y - (row + 1) * rh, it.ColX(col + 1), it.Y - row * rh),
            "head" => RectSegsXY(it.X, it.Y - rh, it.X + w, it.Y),
            _ => RectSegsXY(it.X, it.Y - h, it.X + w, it.Y),
        };
    }

    string DescribeTpl(TemplateItem it, string part, int row, int col) => it.Kind == "line"
        ? (part is "end0" or "end1" ? L("Линия шаблона: конец — перенос мышью") : L("Линия шаблона: перенос мышью, Delete — удалить"))
        : part switch
        {
            "row" => L("Таблица: строка {0} — правый щелчок: добавить, копировать, вставить, удалить", row + 1),
            "col" => L("Таблица: столбец {0} — правый щелчок: добавить, копировать, вставить, удалить", col + 1),
            "head" => L("Таблица{0}: шапка — перенос мышью (притяжение к рамке), двойной щелчок — правка ячейки", it.Title.Length > 0 ? " «" + it.Title + "»" : ""),
            _ => L("Таблица: ячейка {0}, {1} — двойной щелчок — правка", row + 1, col + 1),
        };

    // ------------------------------------------------------------------ линия шаблона двумя щелчками (п. 13)

    (double X, double Y)? _tline;
    bool TemplatePick(double x, double y)
    {
        if (_pick == "tline1") { _tline = (x, y); _pick = "tline2"; _status.Text = L("Линия шаблона: щёлкните вторую точку"); return true; }
        if (_pick == "tline2" && _tline is { } a)
        {
            var it = new TemplateItem { Kind = "line", X = a.X, Y = a.Y, X2 = x, Y2 = y };
            Change(() => Template.Items.Add(it));
            _pick = null; _tline = null; _sel = it; _status.Text = L("Линия шаблона: перенос мышью, Delete — удалить");
            return true;
        }
        return false;
    }
    List<(double, double, double, double)>? TemplatePreview(double x, double y)
    {
        if (_pick == "tline1") return Ring(x, y, VertexR);
        if (_pick == "tline2" && _tline is { } a) { var r = Ring(a.X, a.Y, VertexR); r.Add((a.X, a.Y, x, y)); return r; }
        return null;
    }

    // ------------------------------------------------------------------ таблица: перенос за шапку, притяжение (п. 14)

    /// <summary>Место таблицы с притяжением (порог 3 мм) к углам рамки, к основной надписи и к линиям шаблона.</summary>
    public (double X, double Y, string Snap) SnapTable(TemplateItem it, double x, double y)
    {
        var (x0, y0, x1, y1) = SheetLayout.FrameRect(Template);
        double w = it.TableW, h = it.TableH, tbH = SheetLayout.TitleHeight(Template.TitleBlock), tbW = Template.Standard == SheetStandard.Iso ? 180 : 185;
        var xs = new List<(double V, string C)> { (x0, "l"), (x1 - w, "r"), (x1 - tbW, "") };
        var ys = new List<(double V, string C)> { (y1, "t"), (y0 + h, "b"), (y0 + tbH + h, "title") };
        foreach (var l in Template.Items.Where(i => i.Kind == "line" && !i.Hidden && !ReferenceEquals(i, it)))
        {
            if (Math.Abs(l.Y - l.Y2) < 1e-6) { ys.Add((l.Y, "")); ys.Add((l.Y + h, "")); }
            if (Math.Abs(l.X - l.X2) < 1e-6) { xs.Add((l.X, "")); xs.Add((l.X - w, "")); }
        }
        var bx = xs.OrderBy(c => Math.Abs(c.V - x)).First(); var by = ys.OrderBy(c => Math.Abs(c.V - y)).First();
        bool sx = Math.Abs(bx.V - x) < 3, sy = Math.Abs(by.V - y) < 3;
        double nx = sx ? bx.V : x, ny = sy ? by.V : y;
        string code = sx && sy && bx.C.Length > 0 && by.C.Length > 0 ? (by.C == "title" ? (bx.C == "r" ? "title" : "") : by.C + bx.C) : "";
        _guides.Clear();
        if (sx) _guides.Add((nx, ny - h, nx, ny));
        if (sy) _guides.Add((nx, ny, nx + w, ny));
        return (nx, ny, code);
    }

    /// <summary>Таблицы, прилипшие к рамке, — на место после смены формата, ориентации, стандарта, основной надписи.</summary>
    void ReSnap()
    {
        if (ReSnapOff) return;
        var (x0, y0, x1, y1) = SheetLayout.FrameRect(Template);
        double tbH = SheetLayout.TitleHeight(Template.TitleBlock);
        foreach (var it in Template.Items.Where(i => i.Kind == "table" && i.Snap.Length > 0))
        {
            double w = it.TableW, h = it.TableH;
            switch (it.Snap)
            {
                case "tr": it.X = x1 - w; it.Y = y1; break;
                case "tl": it.X = x0; it.Y = y1; break;
                case "bl": it.X = x0; it.Y = y0 + h; break;
                case "br": it.X = x1 - w; it.Y = y0 + h; break;
                case "title": it.X = x1 - w; it.Y = y0 + tbH + h; break;
            }
        }
    }
    static readonly bool ReSnapOff = Environment.GetEnvironmentVariable("P3D_TABLESNAP") == "0";

    // ------------------------------------------------------------------ правка ячейки на листе (п. 11)

    TextBox? _cellEditor; (TemplateItem It, int Row, int Col)? _editCell;
    public TextBox? CellEditor => _cellEditor;

    /// <summary>Правка ячейки прямо на листе: поле ввода поверх ячейки (выделение мышью и клавишами, копирование, вставка).</summary>
    public void BeginCellEdit(TemplateItem it, int row, int col)
    {
        EndCellEdit(true);
        it.NormalizeCells();
        _editCell = (it, row, col);
        var p0 = _canvas.FromSheet(it.ColX(col), it.Y - row * it.RowH); var p1 = _canvas.FromSheet(it.ColX(col + 1), it.Y - (row + 1) * it.RowH);
        var tb = new TextBox
        {
            Text = it.Cells![row][col], Width = Math.Max(30, p1.X - p0.X), Height = Math.Max(18, p1.Y - p0.Y), FontSize = Math.Clamp((p1.Y - p0.Y) * 0.55, 9, 28),
            Padding = new Thickness(2, 0), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, Name = "dr_cell_editor",
        };
        Avalonia.Controls.Canvas.SetLeft(tb, p0.X); Avalonia.Controls.Canvas.SetTop(tb, p0.Y);
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { EndCellEdit(true); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { EndCellEdit(false); e.Handled = true; }
        };
        tb.LostFocus += (_, _) => EndCellEdit(true);
        _editHost.Children.Add(tb);
        _cellEditor = tb;
        tb.Focus(); tb.SelectAll();
        _status.Text = L("Ячейка {0}, {1}: Enter — готово, Esc — отмена", row + 1, col + 1);
    }

    public void EndCellEdit(bool commit)
    {
        if (_cellEditor is not { } tb || _editCell is not { } ec) return;
        _cellEditor = null; _editCell = null;
        _editHost.Children.Remove(tb);
        string text = tb.Text ?? "";
        if (commit && ec.It.Cells![ec.Row][ec.Col] != text) Change(() => { ec.It.NormalizeCells(); ec.It.Cells![ec.Row][ec.Col] = text; });
        _canvas.Focus();
    }

    // ------------------------------------------------------------------ меню строк и столбцов (п. 11)

    List<string>? _rowClip, _colClip;
    List<(string Text, Action Run)> TableMenu(TemplateItem it, string part, int row, int col)
    {
        var items = new List<(string, Action)>();
        if (part == "row")
        {
            items.Add((L("Добавить строку выше"), () => Change(() => it.InsertRow(row))));
            items.Add((L("Добавить строку ниже"), () => Change(() => it.InsertRow(row + 1))));
            items.Add((L("Копировать строку"), new Action(() => { it.NormalizeCells(); _rowClip = it.Cells![row].ToList(); if (Clipboard is { } cb) _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, string.Join("\t", _rowClip)); })));
            items.Add((L("Вставить строку из буфера"), () => _ = PasteRow(it, row + 1)));
            items.Add((L("Удалить строку"), () => Change(() => it.DeleteRow(row))));
        }
        else if (part == "col")
        {
            items.Add((L("Добавить столбец слева"), () => Change(() => it.InsertColumn(col))));
            items.Add((L("Добавить столбец справа"), () => Change(() => it.InsertColumn(col + 1))));
            items.Add((L("Копировать столбец"), new Action(() => { it.NormalizeCells(); _colClip = it.Cells!.Select(r => r[col]).ToList(); if (Clipboard is { } cb) _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, string.Join("\n", _colClip)); })));
            items.Add((L("Вставить столбец из буфера"), () => _ = PasteColumn(it, col + 1)));
            items.Add((L("Удалить столбец"), () => Change(() => it.DeleteColumn(col))));
        }
        else items.Add((L("Удалить"), () => Change(() => { Template.Items.Remove(it); if (_sel == it) _sel = null; })));
        return items;
    }

    async System.Threading.Tasks.Task PasteRow(TemplateItem it, int at)
    {
        var cells = _rowClip;
        try { if (Clipboard is { } cb && await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb) is { Length: > 0 } t && !t.Contains('\n')) cells = t.Split('\t').ToList(); } catch { }
        Change(() => it.InsertRow(at, cells));
    }
    async System.Threading.Tasks.Task PasteColumn(TemplateItem it, int at)
    {
        var cells = _colClip;
        try { if (Clipboard is { } cb && await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb) is { Length: > 0 } t && !t.Contains('\t')) cells = t.Replace("\r", "").Split('\n').ToList(); } catch { }
        Change(() => it.InsertColumn(at, cells));
    }
    /// <summary>Пункты меню строки или столбца таблицы в точке (для проверок).</summary>
    public List<(string Text, Action Run)> TableMenuAt(double x, double y) => TemplateAt(x, y) is { } t ? TableMenu(t.It, t.Part, t.Row, t.Col) : new();

    // ------------------------------------------------------------------ куб вида у изометрии (п. 8)

    /// <summary>Куб вида: центр, ребро (мм листа), углы на листе и видимые грани (нормаль, углы).</summary>
    (Vector2d C, double S, Vector2d[] P, List<(Vector3d N, int[] Q)> Faces) Cube(DrawingView v, (double X0, double Y0, double X1, double Y1) b)
    {
        var f = ViewFrame.Of(Drawing!.DirOf(v));
        double s = 4.5;
        var c = new Vector2d(b.X1 - 7, b.Y1 - 7);
        var corners = new Vector3d[8];
        for (int i = 0; i < 8; i++) corners[i] = new Vector3d((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
        var P = corners.Select(q => c + new Vector2d(Vector3d.Dot(q, f.Right), Vector3d.Dot(q, f.Up)) * (s / 2)).ToArray();
        var faces = new List<(Vector3d, int[])>
        {
            (Vector3d.UnitX, new[] { 1, 3, 7, 5 }), (-Vector3d.UnitX, new[] { 0, 4, 6, 2 }),
            (Vector3d.UnitY, new[] { 2, 6, 7, 3 }), (-Vector3d.UnitY, new[] { 0, 1, 5, 4 }),
            (Vector3d.UnitZ, new[] { 4, 5, 7, 6 }), (-Vector3d.UnitZ, new[] { 0, 2, 3, 1 }),
        };
        return (c, s, P, faces.Where(fc => Vector3d.Dot(fc.Item1, f.Toward) > 1e-6).ToList());
    }

    /// <summary>Куб под курсором: угол (направление) или грань (нормаль); null — мимо.</summary>
    (Vector3d? Face, Vector3d? Corner)? CubeHit(DrawingView v, (double, double, double, double) b, double x, double y)
    {
        var (c, s, P, faces) = Cube(v, b);
        var q = new Vector2d(x, y);
        var vis = faces.SelectMany(f => f.Q).Distinct().ToList();
        foreach (int i in vis)
            if ((P[i] - q).Length < 0.9) return (null, new Vector3d((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        foreach (var (n, quad) in faces)
        {
            bool inside = true; double sign = 0;
            for (int k = 0; k < 4 && inside; k++)
            {
                var a = P[quad[k]]; var bb = P[quad[(k + 1) % 4]];
                double cr = (bb.X - a.X) * (q.Y - a.Y) - (bb.Y - a.Y) * (q.X - a.X);
                if (sign == 0) sign = Math.Sign(cr); else if (Math.Sign(cr) != sign && Math.Abs(cr) > 1e-12) inside = false;
            }
            if (inside) return (n, null);
        }
        return null;
    }

    List<(double, double, double, double)> CubeSegs(DrawingView v, (double X0, double Y0, double X1, double Y1) b, Vector3d? hot = null)
    {
        var (c, s, P, faces) = Cube(v, b);
        var segs = new List<(double, double, double, double)>();
        foreach (var (n, q) in faces)
        {
            for (int k = 0; k < 4; k++) { var a = P[q[k]]; var e = P[q[(k + 1) % 4]]; segs.Add((a.X, a.Y, e.X, e.Y)); }
            // буква грани — короткий штрих по нормали (какая грань — видно по положению)
        }
        return segs;
    }

    /// <summary>Вид куба: грань — взгляд по нормали (спереди, сверху…), угол — изометрия с этого угла.</summary>
    public void CubeOrient(DrawingView v, Vector3d? face, Vector3d? corner)
    {
        ViewFrame f;
        if (face is { } n)
        {
            string name = n.X > 0.5 ? "right" : n.X < -0.5 ? "left" : n.Y > 0.5 ? "top" : n.Y < -0.5 ? "bottom" : n.Z > 0.5 ? "front" : "back";
            f = ViewFrame.Of(name);
        }
        else if (corner is { } cn)
        {
            var t = cn.Normalized();
            var r = Vector3d.Cross(Vector3d.UnitY, t).Normalized();
            f = new ViewFrame(r, Vector3d.Cross(t, r).Normalized());
        }
        else return;
        Change(() => { v.Dir = f.Encode(); EnsureScene(); if (_input != null) _scene!.Rebuild(Drawing!, v.Name, _input.Bodies); });
        _status.Text = face != null ? L("Вид куба: грань") : L("Вид куба: изометрия с угла");
    }
    /// <summary>Точка внутри первой видимой грани куба и её нормаль (для проверок).</summary>
    public (Vector2d P, Vector3d N)? FirstCubeFace(DrawingView v)
    {
        if (!Drawing!.Boxes.TryGetValue(v.Name, out var b)) return null;
        var (_, _, P, faces) = Cube(v, b);
        if (faces.Count == 0) return null;
        var (n, q) = faces[0];
        var c = (P[q[0]] + P[q[1]] + P[q[2]] + P[q[3]]) / 4;
        return (c, n);
    }
    /// <summary>Положение куба (для проверок): центр и видимые грани.</summary>
    public (Vector2d C, int Faces)? CubeOf(DrawingView v) => Drawing!.Boxes.TryGetValue(v.Name, out var b) ? (Cube(v, b).C, Cube(v, b).Faces.Count) : null;
}
