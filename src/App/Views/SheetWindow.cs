using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Parametric3D.App.Services.Drawing;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k155 (D1): ОКНО ЛИСТА — шаблон листа (.p3dtpl) или перечень (.p3dbom). Лента из иконок DRAWING_MODULE.md (шаблон: формат,
/// ориентация, стандарт, основная надпись, текст, линия, таблица; перечень: сборка-источник, строки, позиции, раскрытие,
/// разделы, обновить по модели), слева — лист (картинка из тех же примитивов, что и PDF), справа — свойства: поля надписи
/// у шаблона, строки у перечня (правка в ячейках). Вывод — PDF; печать — через PDF в системной программе просмотра.
/// </summary>
public sealed class SheetWindow : Window
{
    public enum Mode { Template, Bom, Drawing }
    public readonly Mode Kind;
    public SheetTemplate Template;
    public BomDocument? Bom;
    /// <summary>k155 (D2): чертёж — виды детали на листе.</summary>
    public DrawingDocument? Drawing;
    /// <summary>Тела детали чертежа (открытая деталь); у чертежа с файлом детали — из файла.</summary>
    public Func<IReadOnlyList<Kernel.Topology.TSolid>>? DrawingBodies;
    Dictionary<string, List<ViewLine>>? _proj;
    Dictionary<string, List<ViewAxis>>? _axes;
    public string? FilePath;
    public bool Dirty;
    /// <summary>Сборка-источник открытого документа (перечень «из сборки»), если перечень делается из открытой сборки.</summary>
    public Func<Assembly?>? OpenAssembly;
    /// <summary>Свойства детали для полей надписи шаблона.</summary>
    public Func<Part?>? FieldPart;

    readonly Image _sheet = new() { Stretch = Stretch.Uniform };
    readonly StackPanel _props = new() { Spacing = 6, Margin = new Thickness(10) };
    readonly WrapPanel _ribbon = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4) };
    readonly TextBlock _status = new() { Margin = new Thickness(8, 2), Opacity = 0.75 };
    int _page;
    int _selRow = -1;
    double _zoom = 2.2;
    /// <summary>Последние листы (проверки).</summary>
    public List<SheetGraphics> Pages { get; private set; } = new();
    public static SheetWindow? Last;

    public SheetWindow(Mode kind, SheetTemplate? tpl = null, BomDocument? bom = null, string? path = null, DrawingDocument? drawing = null)
    {
        Kind = kind; Bom = bom; FilePath = path; Drawing = drawing;
        if (kind == Mode.Drawing) Drawing ??= new DrawingDocument();
        Template = tpl ?? bom?.Template ?? Drawing?.Template ?? SheetLayout.Default();
        if (Drawing != null) { Drawing.Template = Template; Drawing.EnsureViews(); }
        if (Bom != null) Bom.Template ??= Template;
        Width = 1200; Height = 820;
        this.Bind(BackgroundProperty, this.GetResourceObservable("P3.PanelBg"));
        var scroll = new ScrollViewer { Content = _sheet, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.Parse("#5A5F66")) };
        var right = new ScrollViewer { Content = _props, Width = kind == Mode.Bom ? 560 : 340 };
        var dock = new DockPanel();
        DockPanel.SetDock(_ribbon, Dock.Top); dock.Children.Add(_ribbon);
        DockPanel.SetDock(_status, Dock.Bottom); dock.Children.Add(_status);
        DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right);
        dock.Children.Add(scroll);
        Content = dock;
        BuildRibbon();
        BuildProps();
        if (kind == Mode.Drawing)
        {
            HookDrag();
            // ассоциативность: при возврате в окно — проверка, не изменилась ли деталь (виды — по «Обновить по модели»)
            Activated += (_, _) => { if (Outdated) _status.Text = L("Деталь изменилась — виды устарели: «Обновить по модели»"); };
        }
        Refresh();
        UpdateTitle();
        Last = this;
    }

    void UpdateTitle() => Title = (Kind == Mode.Bom ? L("Перечень") : Kind == Mode.Drawing ? L("Чертёж") : L("Шаблон листа")) + " — " + (FilePath != null ? Path.GetFileName(FilePath) : L("новый")) + (Dirty ? " *" : "");

    // ------------------------------------------------------------------ лента

    Button Btn(string icon, string tip, Action run)
    {
        var b = new Button { Content = Icons.IconProvider.Content(icon, L(tip), 26), Padding = new Thickness(4), Margin = new Thickness(0, 0, 2, 2) };
        ToolTip.SetTip(b, L(tip));
        b.Click += (_, _) => { try { run(); } catch (Exception ex) { _status.Text = ex.Message; Core.AppLog.Write("sheet: " + tip, ex); } };
        _ribbon.Children.Add(b);
        return b;
    }
    void Sep() => _ribbon.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4), Background = Brushes.Gray, Height = 26 });

    void BuildRibbon()
    {
        Btn("save_but.svg", "Сохранить", () => _ = SaveAsync(false));
        Btn("export_pdf_but.svg", "Вывод в PDF", () => _ = ExportPdfAsync());
        Btn("print_but.svg", "Печать (через PDF)", () => _ = PrintAsync());
        Sep();
        if (Kind == Mode.Drawing)
        {
            Btn("dr_update_but.svg", "Обновить по модели", () => { _proj = null; _axes = null; Refresh(); });
            Btn("dr_base_view_but.svg", "Главный вид", () => ToggleView("front"));
            Btn("dr_projected_view_but.svg", "Проекционные виды (сверху, слева)", () => { ToggleView("top"); ToggleView("left"); });
            Btn("dr_3d_view_but.svg", "3D-вид (изометрия ГОСТ 2.317)", () => ToggleView("iso"));
            Btn("dr_hidden_lines_but.svg", "Невидимые линии", () => Change(() => { bool on = !Drawing!.Views.All(v => v.ShowHidden); foreach (var v in Drawing.Views) v.ShowHidden = on; }));
            Btn("dr_tangent_edges_but.svg", "Линии перехода", () => Change(() => { bool on = !Drawing!.Views.All(v => v.ShowTangent); foreach (var v in Drawing.Views) v.ShowTangent = on; }));
            Btn("dr_centerline_but.svg", "Осевые линии", () => Change(() => { bool on = !Drawing!.Views.All(v => v.CenterLines); foreach (var v in Drawing.Views) v.CenterLines = on; }));
            Btn("dr_view_scale_but.svg", "Масштаб листа (ряд ГОСТ 2.302)", () => Change(() =>
            {
                // авто → 1:1 → уменьшения → увеличения → авто
                double[] order = { 0, 1, 0.5, 0.4, 0.25, 0.2, 0.1, 0.05, 0.02, 0.01, 2, 2.5, 4, 5, 10 };
                var d = Drawing!;
                int i = Array.IndexOf(order, d.Scale);
                d.Scale = order[(i + 1) % order.Length];
                Relayout();
            }));
            Sep();
        }
        if (Kind is Mode.Template or Mode.Drawing)
        {
            Btn("tpl_sheet_format_but.svg", "Формат листа (A0–A4, кратные)", () => { _formatBox?.Focus(); });
            Btn("tpl_orient_portrait_but.svg", "Книжная ориентация", () => Change(() => { Template.Landscape = false; Relayout(); }));
            Btn("tpl_orient_landscape_but.svg", "Альбомная ориентация", () => Change(() => { Template.Landscape = true; Relayout(); }));
            Btn("tpl_standard_but.svg", "Стандарт оформления (ЕСКД / ISO)", () => Change(() =>
            {
                Template.Standard = Template.Standard == SheetStandard.Eskd ? SheetStandard.Iso : SheetStandard.Eskd;
                Template.TitleBlock = Template.Standard == SheetStandard.Iso ? TitleBlockForm.Iso7200 : TitleBlockForm.Form1;
            }));
            Btn("tpl_title_block_but.svg", "Основная надпись (форма 1, 2, 2а; ISO 7200)", () => Change(() =>
                Template.TitleBlock = Template.Standard == SheetStandard.Iso ? TitleBlockForm.Iso7200 : Template.TitleBlock switch { TitleBlockForm.Form1 => TitleBlockForm.Form2, TitleBlockForm.Form2 => TitleBlockForm.Form2a, _ => TitleBlockForm.Form1 }));
            Btn("tpl_field_but.svg", "Поле автозаполнения (обозначение, масса…)", () => _status.Text = L("Пустое поле берётся из свойств детали; заполненное — как введено"));
            Btn("tpl_text_but.svg", "Текст / технические требования", () => Change(() =>
            {
                var (x0, _, _, y1) = SheetLayout.FrameRect(Template);
                Template.Items.Add(new TemplateItem { Kind = "text", X = x0 + 10, Y = y1 - 15, Height = 5, Text = L("1. Размеры для справок.") });
            }));
            Btn("tpl_line_but.svg", "Линия шаблона", () => Change(() =>
            {
                var (x0, y0, x1, _) = SheetLayout.FrameRect(Template);
                Template.Items.Add(new TemplateItem { Kind = "line", X = x0 + 10, Y = y0 + 80, X2 = x1 - 10, Y2 = y0 + 80 });
            }));
            Btn("tpl_table_but.svg", "Таблица на листе", () => Change(() =>
            {
                var (_, _, x1, y1) = SheetLayout.FrameRect(Template);
                Template.Items.Add(new TemplateItem { Kind = "table", X = x1 - 130, Y = y1, Height = 8, Rows = 4, Columns = new() { 20, 70, 40 }, Cells = new() { new() { L("Изм."), L("Содержание"), L("Дата") } } });
            }));
        }
        else
        {
            Btn("bom_source_but.svg", "Сборка-источник (выбор файла)", () => _ = ChooseSourceAsync());
            Btn("dr_update_but.svg", "Обновить по модели", UpdateFromModel);
            Btn("bom_row_add_but.svg", "Добавить строку", () => Change(() =>
            {
                var r = new BomRow { Section = BomSections.Other, Name = L("Новая строка"), Manual = true };
                Bom!.Rows.Insert(Math.Clamp(_selRow + 1, 0, Bom.Rows.Count), r); _selRow = Bom.Rows.IndexOf(r);
            }, rows: true));
            Btn("bom_row_delete_but.svg", "Удалить строку", () => Change(() =>
            {
                if (_selRow < 0 || _selRow >= Bom!.Rows.Count) return;
                var r = Bom.Rows[_selRow];
                if (r.Key.Length > 0) r.Hidden = true; else Bom.Rows.RemoveAt(_selRow);   // строка модели — скрыта (не вернётся при обновлении)
            }, rows: true));
            Btn("bom_row_up_but.svg", "Строку выше", () => Move(-1));
            Btn("bom_row_down_but.svg", "Строку ниже", () => Move(+1));
            Btn("bom_renumber_but.svg", "Перенумеровать позиции", () => Change(() => Bom!.Renumber(), rows: true));
            Btn("bom_expand_but.svg", "Раскрыть подсборки", () => { Bom!.Expand = !Bom.Expand; UpdateFromModel(); });
            Btn("bom_sections_but.svg", "Разделы спецификации ГОСТ 2.106 / ISO 7573", () => Change(() => Bom!.Standard = Bom.Standard == SheetStandard.Eskd ? SheetStandard.Iso : SheetStandard.Eskd));
            Btn("bom_header_but.svg", "Шапка (название, обозначение, изменение, дата)", () => _headerBox?.Focus());
            Btn("bom_columns_but.svg", "Выгрузка CSV", () => _ = ExportCsvAsync());
        }
        Sep();
        Btn("view_zoom_sel_but.svg", "Крупнее", () => { _zoom = Math.Min(8, _zoom * 1.25); Refresh(); });
        Btn("view_fit_but.svg", "Лист целиком", () => { _zoom = 2.2; Refresh(); });
        Btn("dr_sheet_ico.svg", "Следующий лист", () => { _page = Pages.Count == 0 ? 0 : (_page + 1) % Pages.Count; Refresh(); });
    }

    /// <summary>Раскладка видов заново (формат, ориентация, масштаб, набор видов).</summary>
    void Relayout() { if (Drawing != null) foreach (var v in Drawing.Views) { v.X = 0; v.Y = 0; } }

    void ToggleView(string name) => Change(() =>
    {
        var d = Drawing!;
        var v = d.Views.FirstOrDefault(x => x.Name == name);
        if (v != null) d.Views.Remove(v); else d.Views.Add(new DrawingView { Name = name, ShowHidden = name != "iso" });
        Relayout();
    });

    // ------------------------------------------------------------------ перенос вида мышью (чертёж)

    string? _dragView; Point _dragAt;
    (double X, double Y) SheetMm(Point p) => (p.X / _zoom, (Pages.Count > 0 ? Pages[_page].Height : 0) - p.Y / _zoom);

    void HookDrag()
    {
        _sheet.PointerPressed += (_, e) =>
        {
            var p = e.GetPosition(_sheet); var (x, y) = SheetMm(p);
            _dragView = Drawing!.ViewAt(x, y); _dragAt = p;
            if (_dragView != null) { e.Pointer.Capture(_sheet); _status.Text = L("Вид: {0}", _dragView); }
        };
        _sheet.PointerMoved += (_, e) =>
        {
            if (_dragView == null) return;
            var p = e.GetPosition(_sheet);
            double dx = (p.X - _dragAt.X) / _zoom, dy = -(p.Y - _dragAt.Y) / _zoom;
            if (Math.Abs(dx) + Math.Abs(dy) < 0.5) return;
            Drawing!.MoveView(_dragView, dx, dy); _dragAt = p;
            Dirty = true; UpdateTitle(); Refresh();
        };
        _sheet.PointerReleased += (_, e) => { _dragView = null; e.Pointer.Capture(null); };
    }

    void Move(int d) => Change(() =>
    {
        if (_selRow < 0 || _selRow >= Bom!.Rows.Count) return;
        int j = _selRow + d; if (j < 0 || j >= Bom.Rows.Count) return;
        (Bom.Rows[_selRow], Bom.Rows[j]) = (Bom.Rows[j], Bom.Rows[_selRow]); _selRow = j;
    }, rows: true);

    /// <summary>Правка: действие, отметка «изменён», пересчёт листа (rows — пересобрать и таблицу строк).</summary>
    public void Change(Action a, bool rows = false)
    {
        a();
        Dirty = true; UpdateTitle();
        if (rows || Kind != Mode.Bom) BuildProps();
        Refresh();
    }

    // ------------------------------------------------------------------ свойства

    ComboBox? _formatBox;
    TextBox? _headerBox;

    static TextBox Field(string text, Action<string> set, double width = double.NaN)
    {
        var tb = new TextBox { Text = text, MinWidth = 40, Width = width, Padding = new Thickness(4, 2), FontSize = 12 };
        tb.LostFocus += (_, _) => set(tb.Text ?? "");
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) set(tb.Text ?? ""); };
        return tb;
    }

    static readonly (string Field, string Label)[] FieldLabels =
    {
        (Model.Drawing.Fields.Designation, "Обозначение"), (Model.Drawing.Fields.Name, "Наименование"), (Model.Drawing.Fields.Material, "Материал"), (Model.Drawing.Fields.Mass, "Масса, кг"),
        (Model.Drawing.Fields.Scale, "Масштаб"), (Model.Drawing.Fields.Litera, "Литера"), (Model.Drawing.Fields.Developer, "Разработал"), (Model.Drawing.Fields.Checker, "Проверил"),
        (Model.Drawing.Fields.TechControl, "Т. контроль"), (Model.Drawing.Fields.NormControl, "Н. контроль"), (Model.Drawing.Fields.Approver, "Утвердил"),
        (Model.Drawing.Fields.Organization, "Организация"), (Model.Drawing.Fields.Revision, "Изменение"), (Model.Drawing.Fields.Date, "Дата"),
    };

    void BuildProps()
    {
        _props.Children.Clear();
        if (Kind is Mode.Template or Mode.Drawing)
        {
            if (Kind == Mode.Drawing)
            {
                var d = Drawing!;
                _props.Children.Add(new TextBlock { Text = L("Виды"), FontWeight = FontWeight.SemiBold });
                foreach (var (n, lab) in new[] { ("front", "Главный (спереди)"), ("top", "Сверху"), ("left", "Слева"), ("right", "Справа"), ("bottom", "Снизу"), ("back", "Сзади"), ("iso", "Изометрия") })
                {
                    var cb = new CheckBox { Content = L(lab), IsChecked = d.Views.Any(v => v.Name == n), MinWidth = 150 };
                    cb.IsCheckedChanged += (_, _) => { bool on = cb.IsChecked == true; if (on != d.Views.Any(v => v.Name == n)) ToggleView(n); };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { cb } };
                    // масштаб вида (ГОСТ 2.302): «как лист» или свой — подписывается над видом в скобках
                    if (d.Views.FirstOrDefault(v => v.Name == n) is { } dv)
                    {
                        var items = new List<string> { L("как лист") }; items.AddRange(DrawingDocument.GostScales.Select(DrawingDocument.ScaleText));
                        var sc = new ComboBox { ItemsSource = items, SelectedIndex = dv.Scale > 0 ? 1 + Array.IndexOf(DrawingDocument.GostScales, dv.Scale) : 0, FontSize = 11, MinWidth = 90 };
                        sc.SelectionChanged += (_, _) =>
                        {
                            double ns = sc.SelectedIndex <= 0 ? 0 : DrawingDocument.GostScales[sc.SelectedIndex - 1];
                            if (ns != dv.Scale) Change(() => { dv.Scale = ns; Relayout(); });
                        };
                        row.Children.Add(sc);
                    }
                    _props.Children.Add(row);
                }
                _props.Children.Add(new TextBlock { Text = L("Масштаб листа: {0}", d.Scale > 0 ? DrawingDocument.ScaleText(d.Scale) : L("подбирается")), Opacity = 0.8 });
                _props.Children.Add(new TextBlock { Text = d.SourcePath != null ? L("Деталь: {0}", Path.GetFileName(d.SourcePath)) : L("Деталь: открытый документ"), Opacity = 0.8 });
            }
            _props.Children.Add(new TextBlock { Text = L("Лист"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            _formatBox = new ComboBox { ItemsSource = SheetFormats.All.Select(f => f.Code).ToList(), SelectedItem = Template.Format, MinWidth = 120 };
            _formatBox.SelectionChanged += (_, _) => { if (_formatBox.SelectedItem is string s && s != Template.Format) Change(() => { Template.Format = s; Relayout(); }); };
            _props.Children.Add(Row(L("Формат"), _formatBox));
            _props.Children.Add(new TextBlock { Text = $"{L(Template.Landscape ? "Альбомная" : "Книжная")} · {(Template.Standard == SheetStandard.Eskd ? L("ЕСКД") : "ISO")} · {FormName(Template.TitleBlock)}", Opacity = 0.8 });
            _props.Children.Add(Row(L("Имя шаблона"), Field(Template.Name, v => Change(() => Template.Name = v))));
            _props.Children.Add(new TextBlock { Text = L("Поля основной надписи"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            var src = FieldValues();
            foreach (var (f, lab) in FieldLabels)
            {
                var tb = Field(Template.Values.GetValueOrDefault(f) ?? "", v => Change(() => { if (v.Length == 0) Template.Values.Remove(f); else Template.Values[f] = v; }));
                tb.Watermark = src.Auto.GetValueOrDefault(f) ?? "";
                _props.Children.Add(Row(L(lab), tb));
            }
            if (Template.Items.Count > 0)
            {
                _props.Children.Add(new TextBlock { Text = L("Элементы шаблона"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                foreach (var it in Template.Items.ToList())
                {
                    var del = new Button { Content = "×", Padding = new Thickness(6, 0) };
                    del.Click += (_, _) => Change(() => Template.Items.Remove(it));
                    Control body = it.Kind == "text"
                        ? new TextBox { Text = it.Text, AcceptsReturn = true, MinHeight = 44, Width = 230, FontSize = 12 }
                        : new TextBlock { Text = it.Kind == "line" ? L("Линия") : L("Таблица {0}×{1}", it.Rows, it.Columns?.Count ?? 0), VerticalAlignment = VerticalAlignment.Center };
                    if (body is TextBox tb) tb.LostFocus += (_, _) => Change(() => it.Text = tb.Text ?? "");
                    _props.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { body, del } });
                }
            }
            return;
        }
        var bom = Bom!;
        _props.Children.Add(new TextBlock { Text = L("Шапка"), FontWeight = FontWeight.SemiBold });
        _headerBox = Field(bom.Title, v => Change(() => bom.Title = v));
        _props.Children.Add(Row(L("Название"), _headerBox));
        _props.Children.Add(Row(L("Обозначение"), Field(bom.Designation, v => Change(() => bom.Designation = v))));
        _props.Children.Add(Row(L("Изменение"), Field(bom.Revision, v => Change(() => bom.Revision = v))));
        _props.Children.Add(Row(L("Дата"), Field(bom.Date, v => Change(() => bom.Date = v))));
        _props.Children.Add(new TextBlock { Text = (bom.SourcePath != null ? L("Сборка: {0}", Path.GetFileName(bom.SourcePath)) : L("Сборка: открытый документ")) + (bom.Expand ? " · " + L("подсборки раскрыты") : ""), Opacity = 0.8 });
        _props.Children.Add(new TextBlock { Text = L("Строки"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,110,150,130,40,70") };
        void Head(int c, string t) { var h = new TextBlock { Text = L(t), FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(2) }; Grid.SetColumn(h, c); grid.Children.Add(h); }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Head(0, "Поз."); Head(1, "Раздел"); Head(2, "Обозначение"); Head(3, "Наименование"); Head(4, "Кол."); Head(5, "Примечание");
        int gr = 1;
        for (int i = 0; i < bom.Rows.Count; i++)
        {
            var r = bom.Rows[i];
            if (r.Hidden) continue;
            int idx = i;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void Put(int c, Control ctl) { Grid.SetRow(ctl, gr); Grid.SetColumn(ctl, c); ctl.Margin = new Thickness(1); grid.Children.Add(ctl); }
            void Edit(Action a) => Change(() => { a(); r.Manual = true; });
            var pos = Field(r.Position > 0 ? r.Position.ToString(CultureInfo.InvariantCulture) : "", v => Edit(() => r.Position = int.TryParse(v, out var n) ? n : 0));
            pos.GotFocus += (_, _) => _selRow = idx;
            Put(0, pos);
            var sec = new ComboBox { ItemsSource = BomSections.Order, SelectedItem = r.Section, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
            sec.SelectionChanged += (_, _) => { if (sec.SelectedItem is string s && s != r.Section) Edit(() => r.Section = s); };
            sec.GotFocus += (_, _) => _selRow = idx;
            Put(1, sec);
            var des = Field(r.Designation, v => { if (v != r.Designation) Edit(() => r.Designation = v); }); des.GotFocus += (_, _) => _selRow = idx; Put(2, des);
            var nm = Field(r.Name, v => { if (v != r.Name) Edit(() => r.Name = v); }); nm.GotFocus += (_, _) => _selRow = idx; Put(3, nm);
            var q = Field(r.Quantity.ToString(CultureInfo.InvariantCulture), v => { if (int.TryParse(v, out var n) && n != r.Quantity) Edit(() => r.Quantity = n); }); q.GotFocus += (_, _) => _selRow = idx; Put(4, q);
            var note = Field(r.Note, v => { if (v != r.Note) Edit(() => r.Note = v); }); note.GotFocus += (_, _) => _selRow = idx; Put(5, note);
            if (i == _selRow) { var mark = new Border { Background = new SolidColorBrush(Color.FromArgb(50, 80, 160, 255)), IsHitTestVisible = false }; Grid.SetRow(mark, gr); Grid.SetColumnSpan(mark, 6); grid.Children.Insert(0, mark); }
            gr++;
        }
        _props.Children.Add(grid);
        int hidden = bom.Rows.Count(r => r.Hidden);
        if (hidden > 0)
        {
            var back = new Button { Content = L("Вернуть удалённые строки ({0})", hidden) };
            back.Click += (_, _) => Change(() => { foreach (var r in bom.Rows) r.Hidden = false; }, rows: true);
            _props.Children.Add(back);
        }
    }

    static string FormName(TitleBlockForm f) => f switch { TitleBlockForm.Form1 => L("форма 1"), TitleBlockForm.Form2 => L("форма 2"), TitleBlockForm.Form2a => L("форма 2а"), _ => "ISO 7200" };

    static Control Row(string label, Control c)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(c, 1); g.Children.Add(t); g.Children.Add(c);
        return g;
    }

    // ------------------------------------------------------------------ лист

    FieldSource FieldValues() => FieldSource.FromPart(FieldPart?.Invoke());

    /// <summary>Тела детали чертежа: файл детали или открытая деталь.</summary>
    IReadOnlyList<Kernel.Topology.TSolid> Bodies()
    {
        if (Drawing?.SourcePath is { Length: > 0 } sp && File.Exists(sp))
        {
            var (p, pars) = DocumentFile.Load(File.ReadAllText(sp));
            if (!p.RestoredFromFile) p.Rebuild(pars);
            _sourcePart = p;
            return p.Bodies.ToList();
        }
        return DrawingBodies?.Invoke() ?? Array.Empty<Kernel.Topology.TSolid>();
    }
    Part? _sourcePart;
    string _projSig = "";
    /// <summary>Отпечаток тел детали: число граней и рёбер, объём сетки до 1e-6 — для метки «устарел».</summary>
    static string Signature(IReadOnlyList<Kernel.Topology.TSolid> bodies) =>
        string.Join(";", bodies.Select(b => $"{b.Faces.Count()}/{b.Edges.Count()}/{b.Id}"));
    /// <summary>Чертёж открытой детали устарел: тела детали изменились после построения видов.</summary>
    public bool Outdated => Kind == Mode.Drawing && _proj != null && DrawingBodies != null && Drawing?.SourcePath == null
                            && Signature(DrawingBodies()) != _projSig;
    /// <summary>Виды построить заново (сменилась деталь или источник тел).</summary>
    public void InvalidateViews() { _proj = null; _axes = null; }

    public List<SheetGraphics> BuildPages()
    {
        if (Kind == Mode.Bom) { Bom!.Template = Template; return Bom.Pages(); }
        if (Kind == Mode.Drawing)
        {
            var d = Drawing!; d.Template = Template;
            if (_proj == null || _axes == null || d.Views.Any(v => !_proj.ContainsKey(v.Name)))
            {
                var bodies = Bodies();
                _projSig = Signature(bodies);
                _proj = new(); _axes = new();
                foreach (var v in d.Views) { v.X = 0; v.Y = 0; }   // габариты видов новые — раскладка заново
                foreach (var n in new[] { "front", "top", "left", "right", "bottom", "back", "iso" })
                    if (d.Views.Any(v => v.Name == n)) { _proj[n] = DrawingDocument.ProjectBodies(bodies, n); _axes[n] = DrawingDocument.AxesOf(bodies, n); }
            }
            var fs = _sourcePart != null ? FieldSource.FromPart(_sourcePart) : FieldValues();
            return new List<SheetGraphics> { d.Compose(_proj, fs, _axes) };
        }
        return new List<SheetGraphics> { SheetLayout.Compose(Template, FieldValues()) };
    }

    public void Refresh()
    {
        try
        {
            Pages = BuildPages();
            _page = Math.Clamp(_page, 0, Math.Max(0, Pages.Count - 1));
            var g = Pages[_page];
            using var ms = new MemoryStream(SheetRender.RenderPng(g, (float)_zoom * 2));
            _sheet.Source = new Bitmap(ms);
            _sheet.Width = g.Width * _zoom; _sheet.Height = g.Height * _zoom;
            _status.Text = $"{Template.Format} {(Template.Landscape ? L("альбомная") : L("книжная"))} · {L("лист {0} из {1}", _page + 1, Pages.Count)}" +
                           (Kind == Mode.Bom ? " · " + L("строк: {0}", Bom!.Visible.Count()) : "");
        }
        catch (Exception ex) { _status.Text = ex.Message; Core.AppLog.Write("sheet: refresh", ex); }
    }

    // ------------------------------------------------------------------ перечень из сборки

    /// <summary>Обновить перечень по сборке: файл-источник или открытая сборка.</summary>
    public void UpdateFromModel()
    {
        if (Kind != Mode.Bom) { Refresh(); return; }
        var asm = SourceAssembly();
        if (asm == null) { _status.Text = L("Нет сборки-источника: выберите файл сборки"); return; }
        Change(() => Bom!.UpdateFrom(asm), rows: true);
    }

    Assembly? SourceAssembly()
    {
        if (Bom?.SourcePath is { Length: > 0 } sp && File.Exists(sp))
            return AssemblyFile.Load(File.ReadAllText(sp)).Item1;
        return OpenAssembly?.Invoke();
    }

    async Task ChooseSourceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Сборка-источник"), AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(L("Сборка")) { Patterns = new[] { "*" + Services.DocumentSession.AssemblyExtension } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        Bom!.SourcePath = path;
        UpdateFromModel();
    }

    // ------------------------------------------------------------------ файлы

    public const string TemplateExt = ".p3dtpl", BomExt = ".p3dbom", DrawingExt = ".p3drw";

    public void SaveTo(string path)
    {
        if (Kind == Mode.Bom) { Bom!.Template = Template; Bom.Save(path); }
        else if (Kind == Mode.Drawing) { Drawing!.Template = Template; File.WriteAllText(path, Drawing.ToJson()); }
        else Template.Save(path);
        FilePath = path; Dirty = false; UpdateTitle();
        _status.Text = L("Сохранено: {0}", Path.GetFileName(path));
    }

    async Task SaveAsync(bool saveAs)
    {
        string? path = saveAs ? null : FilePath;
        if (path == null)
        {
            string ext = Kind == Mode.Bom ? BomExt : Kind == Mode.Drawing ? DrawingExt : TemplateExt;
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L("Сохранить"), DefaultExtension = ext, SuggestedFileName = (Kind == Mode.Bom ? (Bom!.Title.Length > 0 ? Bom.Title : L("Перечень")) : Template.Name) + ext,
                FileTypeChoices = new[] { new FilePickerFileType(Kind == Mode.Bom ? L("Перечень") : Kind == Mode.Drawing ? L("Чертёж") : L("Шаблон листа")) { Patterns = new[] { "*" + ext } } },
            });
            path = f?.TryGetLocalPath();
        }
        if (path != null) SaveTo(path);
    }

    async Task ExportPdfAsync()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("Вывод в PDF"), DefaultExtension = ".pdf",
            SuggestedFileName = Path.GetFileNameWithoutExtension(FilePath ?? (Kind == Mode.Bom ? L("Перечень") : Template.Name)) + ".pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        SheetRender.WritePdf(path, BuildPages(), Kind == Mode.Bom ? Bom!.Title : Template.Name);
        _status.Text = L("PDF записан: {0}", Path.GetFileName(path));
    }

    async Task ExportCsvAsync()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("Выгрузка CSV"), DefaultExtension = ".csv", SuggestedFileName = (Bom!.Title.Length > 0 ? Bom.Title : L("Перечень")) + ".csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        File.WriteAllText(path, Bom.ToCsv(), new System.Text.UTF8Encoding(true));
        _status.Text = L("CSV записан: {0}", Path.GetFileName(path));
    }

    /// <summary>Печать: PDF во временную папку и открытие в системной программе (там — печать с выбором принтера).</summary>
    async Task PrintAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "Parametric3D_print_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".pdf");
        SheetRender.WritePdf(path, BuildPages(), Kind == Mode.Bom ? Bom!.Title : Template.Name);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); _status.Text = L("Открыт PDF для печати"); }
        catch (Exception ex) { await Dialogs.Info(this, L("Печать"), L("PDF записан: {0}\n{1}", path, ex.Message)); }
    }

    // ------------------------------------------------------------------ открытие

    public static SheetWindow OpenFile(string path)
    {
        if (path.EndsWith(DrawingExt, StringComparison.OrdinalIgnoreCase))
        {
            var dr = DrawingDocument.FromJson(File.ReadAllText(path));
            return new SheetWindow(Mode.Drawing, dr.Template, null, path, dr);
        }
        if (path.EndsWith(BomExt, StringComparison.OrdinalIgnoreCase))
        {
            var bom = BomDocument.Load(path);
            return new SheetWindow(Mode.Bom, bom.Template, bom, path);
        }
        return new SheetWindow(Mode.Template, SheetTemplate.Load(path), null, path);
    }
}
