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

    // ------------------------------------------------------------------ куб вида у изометрии (п. 8; k170 — п. 2 замечания 18:34)

    /// <summary>
    /// k170 (замечание 05.11, 18:34, п. 2): КУБ ВИДА — ТОТ ЖЕ, ЧТО В ОКНЕ ДЕТАЛИ (Viewport.ViewCube): надписи граней, рёбра и
    /// углы (щелчок — вид по ним), ведение — поворот, кольцо — вокруг вертикали; слева сверху «Домой», сверху — поворот на 90°
    /// против и по часовой. Куб стоит на месте листа: место (правый верхний угол рамки вида) задаётся, только когда изометрию
    /// выбирают щелчком, и не меняется при повороте (рамка вида при повороте меняется). Прежде (k168) — каркас куба по рамке
    /// вида, без надписей, и он «уезжал» при повороте. Мутация P3D_SHEETCUBE=0 — прежний каркас.
    /// </summary>
    sealed class SheetCubeHost : Viewport.IViewCubeHost
    {
        readonly SheetWindow _w; public DrawingView? View;
        public SheetCubeHost(SheetWindow w) => _w = w;
        public Quaternion Rotation => View == null ? Quaternion.Identity : ToQuat(ViewFrame.Of(_w.Drawing!.DirOf(View)));
        public void Live(Quaternion q) { if (View == null) return; _w.SetFrame(View, FromQuat(q), coarse: true); _w._cubeMoved = true; _w.RequestRefresh(); }
        public void DragEnd()
        {
            if (View == null || !_w._cubeMoved) return;
            _w._cubeMoved = false;
            if (_w._input != null) _w._scene?.Rebuild(_w.Drawing!, View.Name, _w._input.Bodies);
            _w.UpdateTitle(); _w.RefreshNow(); _w.BuildProps();
        }
        public void Orient(Quaternion q) { if (View == null) return; var v = View; _w.Change(() => _w.SetFrame(v, FromQuat(q), coarse: false)); }
        public void Home() { if (View != null) _w.HomeView(View); }
    }
    internal static readonly bool SheetCubeOff = Environment.GetEnvironmentVariable("P3D_SHEETCUBE") == "0";
    SheetCubeHost? _cubeHost; Viewport.ViewCube? _cube; (string View, double X, double Y)? _cubeAt; bool _cubeMoved;
    /// <summary>Куб вида листа (для проверок).</summary>
    public Viewport.ViewCube? SheetCube => _cube;
    public const double SheetCubePx = 120;

    /// <summary>Поворот «мир → камера» по рамке вида: строки — вправо, вверх, к наблюдателю (как ViewMath.FromForwardUp).</summary>
    public static Quaternion ToQuat(ViewFrame f)
    {
        var r = f.Right; var u = f.Up; var t = f.Toward;
        var m = new Matrix3((float)r.X, (float)r.Y, (float)r.Z, (float)u.X, (float)u.Y, (float)u.Z, (float)t.X, (float)t.Y, (float)t.Z);
        var q = Quaternion.FromMatrix(m); q.Normalize(); return q;
    }
    public static ViewFrame FromQuat(Quaternion q)
    {
        var inv = Quaternion.Invert(q);
        var r = Vector3.Transform(Vector3.UnitX, inv); var u = Vector3.Transform(Vector3.UnitY, inv);
        // округление float: оси к точным ±1 и 0, если ближе 1e-5 (виды по граням — точные, как у ViewFrame.Of)
        static Vector3d Clean(Vector3 a) { var v = new Vector3d(a.X, a.Y, a.Z); v = new(Snap(v.X), Snap(v.Y), Snap(v.Z)); return v.Normalized(); }
        static double Snap(double x) => Math.Abs(x) < 1e-5 ? 0 : Math.Abs(Math.Abs(x) - 1) < 1e-5 ? Math.Sign(x) : x;
        var rr = Clean(r); var uu = Clean(u); uu = (uu - rr * Vector3d.Dot(uu, rr)).Normalized();
        return new ViewFrame(rr, uu);
    }
    void SetFrame(DrawingView v, ViewFrame f, bool coarse)
    {
        v.Dir = f.Encode();
        EnsureScene();
        if (_input != null) { if (coarse) _scene!.RebuildFast(Drawing!, v.Name, _input.Bodies); else _scene!.Rebuild(Drawing!, v.Name, _input.Bodies); }
        Dirty = true;
    }

    /// <summary>Изометрию выбрали щелчком — куб на её правый верхний угол (место листа запоминается).</summary>
    void PlaceCube(DrawingView v)
    {
        if (SheetCubeOff || CubeOff || Drawing == null || !Drawing.Boxes.TryGetValue(v.Name, out var b)) return;
        _cubeAt = (v.Name, b.X1, b.Y1);
        UpdateCube();
    }
    /// <summary>Показать или спрятать куб: виден у выбранной изометрии, на запомненном месте листа (с переносом и увеличением листа).</summary>
    void UpdateCube()
    {
        bool show = !SheetCubeOff && !CubeOff && Kind == Mode.Drawing && _sel is DrawingView v && IsRotatable(v) && _cubeAt is { } at && at.View == v.Name;
        if (!show) { if (_cube != null) _cube.IsVisible = false; return; }
        var view = (DrawingView)_sel!;
        if (_cube == null)
        {
            _cubeHost = new SheetCubeHost(this);
            _cube = new Viewport.ViewCube { Host = _cubeHost, AlwaysRoll = true, Name = "dr_view_cube" };
            _editHost.Children.Add(_cube);
        }
        _cubeHost!.View = view;
        _cube.Width = _cube.Height = SheetCubePx;
        var p = _canvas.FromSheet(_cubeAt!.Value.X, _cubeAt.Value.Y);
        // у правого верхнего угла рамки вида, почти целиком снаружи (куб не закрывает сам вид)
        Avalonia.Controls.Canvas.SetLeft(_cube, p.X - SheetCubePx * 0.3);
        Avalonia.Controls.Canvas.SetTop(_cube, p.Y - SheetCubePx * 0.72);
        _cube.IsVisible = true;
        _cube.InvalidateVisual();
    }

    /// <summary>Вид куба: грань — взгляд по нормали (спереди, сверху…), угол — изометрия с этого угла.</summary>
    public void CubeOrient(DrawingView v, Vector3d? face, Vector3d? corner)
    {
        var d = face ?? corner; if (d is not { } dd) return;
        var up = Vector3.UnitY;
        if (_cubeHost != null) up = Vector3.Transform(Vector3.UnitY, Quaternion.Invert(ToQuat(ViewFrame.Of(Drawing!.DirOf(v)))));
        var q = Viewport.ViewMath.LookFrom(new Vector3((float)dd.X, (float)dd.Y, (float)dd.Z), up);
        Change(() => SetFrame(v, FromQuat(q), coarse: false));
        _status.Text = face != null ? L("Вид куба: грань") : L("Вид куба: изометрия с угла");
    }
    /// <summary>Положение куба (для проверок): место на листе и показан ли он.</summary>
    public (Vector2d C, bool Shown)? CubeOf(DrawingView v) => _cubeAt is { } a && a.View == v.Name ? (new Vector2d(a.X, a.Y), _cube?.IsVisible == true) : null;
}
