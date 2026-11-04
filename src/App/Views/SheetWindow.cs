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
    /// <summary>Источник чертежа (открытая деталь или сборка); у чертежа с файлом — из файла.</summary>
    public Func<DrawingInput?>? DrawingSource;
    /// <summary>Совместимость (k155): тела открытой детали.</summary>
    public Func<IReadOnlyList<Kernel.Topology.TSolid>>? DrawingBodies
    {
        set => DrawingSource = value == null ? null : () => { var d = new DrawingInput(); d.Bodies.AddRange(value()); return d; };
    }
    DrawingScene? _scene;
    DrawingInput? _input;
    /// <summary>Проекции видов последнего построения (проверки).</summary>
    public DrawingScene? Scene => _scene;
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
            Btn("dr_update_but.svg", "Обновить по модели", () => { InvalidateViews(); Refresh(); BuildProps(); });
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
            // D3: размеры
            Btn("dr_auto_dims_but.svg", "Автоматические размеры (габариты, отверстия, радиусы)", () => Change(() => { EnsureScene(); Drawing!.AutoDimensions(_scene!); }));
            Btn("dr_show_model_dims_but.svg", "Размеры модели (из эскизов)", () => Change(() =>
            {
                EnsureScene();
                int n = _input?.Part != null ? Drawing!.ModelDimensions(_input.Part) : 0;
                _status.Text = _input?.Part == null ? L("Размеры модели — только у чертежа детали") : L("Перенесено размеров эскизов: {0}", n);
            }));
            Btn("dr_dims_toggle_but.svg", "Показать / скрыть размеры", () => Change(() => Drawing!.ShowDims = !Drawing.ShowDims));
            Btn("dr_dim_linear_but.svg", "Линейный размер: два щелчка по точкам вида", () => StartPick("dim1", L("Размер: щёлкните первую точку на виде")));
            Btn("dr_dim_diameter_but.svg", "Диаметр: щелчок по окружности", () => StartPick("dia", L("Диаметр: щёлкните окружность")));
            Btn("dr_dim_radial_but.svg", "Радиус: щелчок по дуге", () => StartPick("rad", L("Радиус: щёлкните дугу")));
            Sep();
            // D4: обозначения
            Btn("dr_roughness_but.svg", "Шероховатость: щелчок по контуру (Esc — неуказанная в углу)", () => StartPick("rough", L("Шероховатость: щёлкните поверхность на виде; Esc — неуказанная шероховатость в углу листа")));
            Btn("dr_gdt_frame_but.svg", "Допуск формы и расположения: щелчок по контуру", () => StartPick("gdt", L("Допуск: щёлкните поверхность на виде")));
            Btn("dr_datum_but.svg", "База: щелчок по контуру", () => StartPick("datum", L("База: щёлкните поверхность на виде")));
            Btn("dr_gdt_toggle_but.svg", "Допуски формы и расположения вкл/выкл", () => Change(() => Drawing!.ShowGdt = !Drawing.ShowGdt));
            Btn("dr_note_but.svg", "Технические требования (текст над надписью)", () => Change(() =>
            {
                var (x0, y0, x1, _) = SheetLayout.FrameRect(Template);
                double y = y0 + SheetLayout.TitleHeight(Template.TitleBlock) + 40;
                Drawing!.Notes.Add(new DrawingNote { Kind = "text", X = x1 - 185, Y = y, Text = L("1. * Размеры для справок.\n2. Неуказанные предельные отклонения размеров: H14, h14, ±IT14/2.") });
            }));
            Btn("dr_balloon_but.svg", "Позиция: щелчок по детали", () => StartPick("balloon", L("Позиция: щёлкните деталь на виде")));
            Btn("dr_auto_balloon_but.svg", "Позиции сборки (по перечню)", () => Change(() => { EnsureScene(); Drawing!.AutoBalloons(_scene!, _input?.Positions, Drawing.Views.Any(v => v.Name == "front") ? "front" : Drawing.Views[0].Name); }));
            Sep();
            // D5: разрезы, выносные, таблицы, DXF
            Btn("dr_section_view_but.svg", "Разрез: щелчок на виде — место секущей плоскости", () => StartPick("section", L("Разрез: щёлкните на виде место секущей плоскости")));
            Btn("dr_detail_view_but.svg", "Выносной элемент: щелчок — центр круга", () => StartPick("detail", L("Выносной элемент: щёлкните центр")));
            Btn("dr_aux_view_but.svg", "Вспомогательный вид: щелчок по наклонной линии вида", () => StartPick("aux", L("Вспомогательный вид: щёлкните наклонную линию на виде")));
            Btn("dr_break_view_but.svg", "Разрыв вида: два щелчка — края удаляемой полосы (два щелчка в одной точке — убрать разрыв)", () => StartPick("break1", L("Разрыв: щёлкните левый край полосы")));
            Btn("dr_hole_table_but.svg", "Таблица отверстий", () => Change(() =>
            {
                EnsureScene();
                string view = Drawing!.Views.Where(v => !v.IsDetail && _scene!.Axes.ContainsKey(v.Name)).OrderByDescending(v => _scene!.Axes[v.Name].Count(a => a.Cross)).Select(v => v.Name).FirstOrDefault() ?? "top";
                var (x0, _, _, y1) = SheetLayout.FrameRect(Template);
                Drawing.Notes.RemoveAll(n => n.Kind == "holes");
                Drawing.Notes.Add(new DrawingNote { Kind = "holes", View = view, X = x0 + 5, Y = y1 - 5 });
            }));
            Btn("export_dxf_but.svg", "Вывод в DXF", () => _ = ExportDxfAsync());
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

    DrawingNote? _dragNote;

    void HookDrag()
    {
        _sheet.PointerPressed += (_, e) =>
        {
            var p = e.GetPosition(_sheet); var (x, y) = SheetMm(p);
            if (_pick != null) { try { PickAt(x, y); } catch (Exception ex) { _status.Text = ex.Message; Core.AppLog.Write("sheet: pick", ex); } return; }
            _dragAt = p;
            // обозначение под курсором (место на листе ближе 5 мм) — переносится оно, иначе — вид
            _dragNote = Drawing!.Notes.Where(n => n.Kind != "rough" || n.HasTarget || n.X != 0).OrderBy(n => Math.Abs(n.X - x) + Math.Abs(n.Y - y)).FirstOrDefault(n => Math.Abs(n.X - x) < 8 && Math.Abs(n.Y - y) < 8);
            _dragView = _dragNote == null ? Drawing.ViewAt(x, y) : null;
            if (_dragView != null || _dragNote != null) { e.Pointer.Capture(_sheet); _status.Text = _dragNote != null ? L("Обозначение") : L("Вид: {0}", _dragView!); }
        };
        _sheet.PointerMoved += (_, e) =>
        {
            if (_dragView == null && _dragNote == null) return;
            var p = e.GetPosition(_sheet);
            double dx = (p.X - _dragAt.X) / _zoom, dy = -(p.Y - _dragAt.Y) / _zoom;
            if (Math.Abs(dx) + Math.Abs(dy) < 0.5) return;
            if (_dragNote != null) { _dragNote.X += dx; _dragNote.Y += dy; }
            else Drawing!.MoveView(_dragView!, dx, dy);
            _dragAt = p;
            Dirty = true; UpdateTitle(); Refresh();
        };
        _sheet.PointerReleased += (_, e) => { _dragView = null; _dragNote = null; e.Pointer.Capture(null); };
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || _pick == null) return;
            // Esc в режиме шероховатости — неуказанная шероховатость в углу листа
            if (_pick == "rough") Change(() => { Drawing!.Notes.RemoveAll(n => n.Kind == "rough" && !n.HasTarget); Drawing.Notes.Add(new DrawingNote { Kind = "rough", Value = "Ra 6,3" }); });
            _pick = null; _status.Text = "";
        };
    }

    // ------------------------------------------------------------------ щелчки по виду: размеры, обозначения, разрезы

    string? _pick; (string View, OpenTK.Mathematics.Vector2d P)? _first;

    public string StatusText => _status.Text ?? "";
    public void StartPick(string mode, string hint) { EnsureScene(); _pick = mode; _first = null; _status.Text = hint; }

    /// <summary>Ближайшая точка линий вида (вершины ломаных; snap — в мм листа) или сама точка щелчка.</summary>
    OpenTK.Mathematics.Vector2d Snap(string view, OpenTK.Mathematics.Vector2d q, double snapMm, bool vertices)
    {
        double s = Drawing!.Xf[view].S, best = snapMm / s; var res = q;
        foreach (var l in _scene!.Lines[view])
        {
            if (!l.Visible) continue;
            for (int i = 0; i < l.Points.Count; i++)
            {
                if (vertices && i != 0 && i != l.Points.Count - 1) continue;
                double dd = (l.Points[i] - q).Length;
                if (dd < best) { best = dd; res = l.Points[i]; }
                if (!vertices && i + 1 < l.Points.Count)
                {
                    var a = l.Points[i]; var ab = l.Points[i + 1] - a;
                    double t = ab.LengthSquared < 1e-24 ? 0 : Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
                    var c = a + ab * t; double dc = (c - q).Length;
                    if (dc < best) { best = dc; res = c; }
                }
            }
        }
        return res;
    }

    /// <summary>Тело (деталь) у ближайшей к точке видимой линии вида.</summary>
    int BodyAt(string view, OpenTK.Mathematics.Vector2d q)
    {
        int body = -1; double best = double.MaxValue;
        foreach (var l in _scene!.Lines[view])
            for (int i = 0; i + 1 < l.Points.Count; i++)
            {
                var a = l.Points[i]; var ab = l.Points[i + 1] - a;
                double t = ab.LengthSquared < 1e-24 ? 0 : Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
                double d = (a + ab * t - q).Length;
                if (d < best) { best = d; body = l.Body; }
            }
        return body;
    }

    public void PickAt(double x, double y)
    {
        var d = Drawing!;
        var view = d.ViewAt(x, y);
        if (view == null || !d.Xf.ContainsKey(view)) { _status.Text = L("Щёлкните на виде"); return; }
        var q = d.ToView(view, x, y);
        double s = d.Xf[view].S;
        string mode = _pick!;
        switch (mode)
        {
            case "dim1":
                _first = (view, Snap(view, q, 4, true)); _pick = "dim2"; _status.Text = L("Размер: щёлкните вторую точку"); return;
            case "dim2":
                {
                    if (_first is not { } f || f.View != view) { _status.Text = L("Вторая точка — на том же виде"); return; }
                    var b = Snap(view, q, 4, true);
                    double dx = Math.Abs(b.X - f.P.X), dy = Math.Abs(b.Y - f.P.Y);
                    string kind = dx > 3 * dy ? "h" : dy > 3 * dx ? "v" : "al";
                    var mid = d.Xf[view].Mid;
                    // размерная линия — снаружи: от середины вида в сторону точек
                    double off = kind == "h" ? ((f.P.Y + b.Y) / 2 >= mid.Y ? 10 : -10) : kind == "v" ? ((f.P.X + b.X) / 2 >= mid.X ? -10 : 10) : 8;
                    Change(() => d.Dims.Add(new DrawingDim { View = view, Kind = kind, X1 = f.P.X, Y1 = f.P.Y, X2 = b.X, Y2 = b.Y, Offset = off }));
                    _pick = null; return;
                }
            case "dia" or "rad":
                {
                    // окружность — по осям (перекрестие) или дуге ребра; ближайшая к щелчку по расстоянию до линии окружности
                    var arcs = _scene!.Arcs.GetValueOrDefault(view) ?? new();
                    var cands = arcs.Where(a => mode == "rad" ? !a.Full : true).Select(a => (C: a.Center, R: a.Radius, A: Math.Atan2(q.Y - a.Center.Y, q.X - a.Center.X)))
                        .Concat(mode == "dia" ? (_scene.Axes.GetValueOrDefault(view) ?? new()).Where(a => a.Cross).Select(a => (C: a.Center, R: a.Radius, A: Math.Atan2(q.Y - a.Center.Y, q.X - a.Center.X))) : Enumerable.Empty<(OpenTK.Mathematics.Vector2d C, double R, double A)>())
                        .OrderBy(c => Math.Abs((q - c.C).Length - c.R)).ToList();
                    if (cands.Count == 0 || Math.Abs((q - cands[0].C).Length - cands[0].R) * s > 5) { _status.Text = mode == "dia" ? L("Окружность не найдена — щёлкните ближе к ней") : L("Дуга не найдена — щёлкните ближе к ней"); return; }
                    var c0 = cands[0];
                    Change(() => d.Dims.Add(new DrawingDim { View = view, Kind = mode, X1 = c0.C.X, Y1 = c0.C.Y, X2 = c0.R, Y2 = c0.A * 180 / Math.PI }));
                    _pick = null; return;
                }
            case "rough" or "gdt" or "datum" or "balloon":
                {
                    var t = Snap(view, q, 5, false);
                    var note = new DrawingNote { Kind = mode, View = view, HasTarget = true, TX = t.X, TY = t.Y };
                    var ts = d.ToSheet(view, t);
                    switch (mode)
                    {
                        case "rough": note.Value = "Ra 3,2"; note.HasTarget = true; note.X = ts.X; note.Y = ts.Y; break;
                        case "gdt": note.Symbol = "perp"; note.Value = "0,05"; note.Datums = d.Notes.Where(n => n.Kind == "datum").Select(n => n.Text).FirstOrDefault() ?? ""; note.X = ts.X + 12; note.Y = ts.Y + 10; break;
                        case "datum": note.Text = d.NextLetter(); note.X = ts.X; note.Y = ts.Y - 15; break;
                        default:
                            {
                                int body = BodyAt(view, q);
                                string item = body >= 0 && body < _scene!.BodyItems.Count ? _scene.BodyItems[body] : "";
                                note.Position = _input?.Positions != null && _input.Positions.TryGetValue(item, out var pp) ? pp : d.Notes.Where(n => n.Kind == "balloon").Select(n => n.Position).DefaultIfEmpty(0).Max() + 1;
                                note.Text = item; note.X = d.Boxes[view].X1 + 12; note.Y = ts.Y;
                                break;
                            }
                    }
                    Change(() => d.Notes.Add(note));
                    _pick = null; return;
                }
            case "section":
                {
                    var parent = d.Views.First(v => v.Name == view);
                    if (parent.IsSection || parent.IsDetail || parent.IsAux || d.DirOf(parent) == "iso") { _status.Text = L("Разрез — на основном виде"); return; }
                    string dir = d.DirOf(parent) switch { "top" or "bottom" => "front", "front" or "back" => "left", _ => "front" };
                    var pf = ViewFrame.Of(d.DirOf(parent)); var sf = ViewFrame.Of(dir);
                    double depth = OpenTK.Mathematics.Vector3d.Dot(pf.Right * q.X + pf.Up * q.Y, sf.Toward);
                    Change(() => { d.AddSection(_scene!, view, dir, depth); InvalidateViews(); });
                    _pick = null; return;
                }
            case "aux":
                {
                    var parent = d.Views.First(v => v.Name == view);
                    if (parent.IsDetail || d.DirOf(parent) == "iso") { _status.Text = L("Вспомогательный вид — на основном виде или разрезе"); return; }
                    // ближайший видимый отрезок линий вида (в пределах 5 мм листа)
                    (OpenTK.Mathematics.Vector2d A, OpenTK.Mathematics.Vector2d B)? seg = null; double best = 5 / s;
                    foreach (var l in _scene!.Lines[view])
                        for (int i = 0; l.Visible && i + 1 < l.Points.Count; i++)
                        {
                            var a = l.Points[i]; var ab = l.Points[i + 1] - a;
                            if (ab.Length < 1e-6) continue;
                            double t = Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
                            double dd = (a + ab * t - q).Length;
                            if (dd < best) { best = dd; seg = (a, l.Points[i + 1]); }
                        }
                    if (seg is not { } sg) { _status.Text = L("Вспомогательный вид: щёлкните ближе к линии"); return; }
                    Change(() => { d.AddAuxiliary(_scene!, view, sg.A, sg.B); InvalidateViews(); });
                    _pick = null; return;
                }
            case "break1":
                {
                    var parent = d.Views.First(v => v.Name == view);
                    if (parent.IsDetail || parent.Name == "iso") { _status.Text = L("Разрыв — на основном виде или разрезе"); return; }
                    _first = (view, q); _pick = "break2"; _status.Text = L("Разрыв: щёлкните правый край полосы"); return;
                }
            case "break2":
                {
                    if (_first is not { } f || f.View != view) { _status.Text = L("Второй край — на том же виде"); return; }
                    var bv = d.Views.First(v => v.Name == view);
                    if (Math.Abs(q.X - f.P.X) * s < 1) Change(() => { bv.BreakA = bv.BreakB = 0; });
                    else Change(() => d.AddBreak(view, f.P.X, q.X));
                    _pick = null; return;
                }
            case "detail":
                {
                    double r = 12 / s;
                    double ds = DrawingDocument.GostScales.Where(g => g >= 2 * s - 1e-12).DefaultIfEmpty(2 * s).Min();
                    Change(() => { d.AddDetail(view, q, r, ds); InvalidateViews(); });
                    _pick = null; return;
                }
        }
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
                _props.Children.Add(new TextBlock { Text = d.SourcePath != null ? L("Модель: {0}", Path.GetFileName(d.SourcePath)) : L("Модель: открытый документ"), Opacity = 0.8 });
                DrawingProps(d);
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

    /// <summary>Разрезы и выносные, размеры, обозначения — списком с правкой.</summary>
    void DrawingProps(DrawingDocument d)
    {
        TextBlock Head(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        Button Del(Action a) { var b = new Button { Content = "×", Padding = new Thickness(6, 0) }; b.Click += (_, _) => Change(a); return b; }
        static double Num(string v, double old) => double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : old;
        var extra = d.Views.Where(v => v.IsSection || v.IsDetail).ToList();
        if (extra.Count > 0)
        {
            _props.Children.Add(Head(L("Разрезы и выносные элементы")));
            foreach (var v in extra)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new TextBlock { Text = v.IsSection ? $"{v.Label}-{v.Label}" : v.Label, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center });
                if (v.IsSection) row.Children.Add(Field(v.CutDepth.ToString("0.###", CultureInfo.InvariantCulture), t => { double n = Num(t, v.CutDepth); if (n != v.CutDepth) Change(() => { v.CutDepth = n; InvalidateViews(); }); }, 80));
                else row.Children.Add(Field(v.DetailR.ToString("0.###", CultureInfo.InvariantCulture), t => { double n = Num(t, v.DetailR); if (n > 0 && n != v.DetailR) Change(() => { v.DetailR = n; InvalidateViews(); }); }, 80));
                row.Children.Add(new TextBlock { Text = v.IsSection ? L("глубина, мм") : L("радиус, мм"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(Del(() => { d.Views.Remove(v); InvalidateViews(); }));
                _props.Children.Add(row);
            }
        }
        if (d.Dims.Count > 0)
        {
            _props.Children.Add(Head(L("Размеры")));
            foreach (var dim in d.Dims.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var show = new CheckBox { IsChecked = !dim.Hidden, MinWidth = 0 };
                show.IsCheckedChanged += (_, _) => { bool h = show.IsChecked != true; if (h != dim.Hidden) Change(() => dim.Hidden = h); };
                row.Children.Add(show);
                row.Children.Add(new TextBlock { Text = $"{dim.View}: {dim.Caption(Template.Standard == SheetStandard.Eskd)}", MinWidth = 120, VerticalAlignment = VerticalAlignment.Center });
                var tol = Field(dim.Tol, t => { if (t != dim.Tol) Change(() => dim.Tol = t); }, 60); tol.Watermark = L("допуск");
                var txt = Field(dim.Text, t => { if (t != dim.Text) Change(() => dim.Text = t); }, 60); txt.Watermark = L("текст");
                var off = Field(dim.Offset.ToString("0.#", CultureInfo.InvariantCulture), t => { double n = Num(t, dim.Offset); if (n != dim.Offset) Change(() => dim.Offset = n); }, 44);
                row.Children.Add(tol); row.Children.Add(txt); row.Children.Add(off); row.Children.Add(Del(() => d.Dims.Remove(dim)));
                _props.Children.Add(row);
            }
        }
        if (d.Notes.Count > 0)
        {
            _props.Children.Add(Head(L("Обозначения")));
            foreach (var n in d.Notes.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                string kind = n.Kind switch { "rough" => L("Шероховатость"), "gdt" => L("Допуск"), "datum" => L("База"), "balloon" => L("Позиция"), "holes" => L("Таблица отверстий"), _ => L("Текст") };
                row.Children.Add(new TextBlock { Text = kind, MinWidth = 90, VerticalAlignment = VerticalAlignment.Center });
                switch (n.Kind)
                {
                    case "rough": row.Children.Add(Field(n.Value, t => { if (t != n.Value) Change(() => n.Value = t); }, 90)); break;
                    case "gdt":
                        {
                            var sym = new ComboBox { ItemsSource = DrawingDocument.GdtSymbols.Select(x => L(x.Name)).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(DrawingDocument.GdtSymbols, x => x.Key == n.Symbol)), FontSize = 11, MinWidth = 120 };
                            sym.SelectionChanged += (_, _) => { var k = DrawingDocument.GdtSymbols[Math.Max(0, sym.SelectedIndex)].Key; if (k != n.Symbol) Change(() => n.Symbol = k); };
                            row.Children.Add(sym);
                            row.Children.Add(Field(n.Value, t => { if (t != n.Value) Change(() => n.Value = t); }, 54));
                            var db = Field(n.Datums, t => { if (t != n.Datums) Change(() => n.Datums = t); }, 40); db.Watermark = L("базы");
                            row.Children.Add(db);
                            break;
                        }
                    case "datum": row.Children.Add(Field(n.Text, t => { if (t != n.Text) Change(() => n.Text = t); }, 40)); break;
                    case "balloon": row.Children.Add(Field(n.Position.ToString(CultureInfo.InvariantCulture), t => { if (int.TryParse(t, out var k) && k != n.Position) Change(() => n.Position = k); }, 40)); break;
                    case "holes": break;
                    default:
                        {
                            var tb = new TextBox { Text = n.Text, AcceptsReturn = true, MinHeight = 44, Width = 220, FontSize = 12 };
                            tb.LostFocus += (_, _) => { if (tb.Text != n.Text) Change(() => n.Text = tb.Text ?? ""); };
                            row.Children.Add(tb);
                            break;
                        }
                }
                row.Children.Add(Del(() => d.Notes.Remove(n)));
                _props.Children.Add(row);
            }
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

    /// <summary>Источник чертежа: файл модели (деталь или сборка) или открытый документ.</summary>
    DrawingInput Input()
    {
        // открытый документ — его модель (файл может быть старее правок); иначе — файл модели
        if (DrawingSource?.Invoke() is { } open && open.Bodies.Count > 0) return open;
        if (Drawing?.SourcePath is { Length: > 0 } sp && File.Exists(sp))
        {
            if (_fileInput is { } fi && fi.Path == sp && fi.Stamp == File.GetLastWriteTimeUtc(sp)) return fi.Input;
            var inp = DrawingInput.FromFile(sp);
            _fileInput = (sp, File.GetLastWriteTimeUtc(sp), inp);
            return inp;
        }
        return DrawingSource?.Invoke() ?? new DrawingInput();
    }
    (string Path, DateTime Stamp, DrawingInput Input)? _fileInput;
    string _projSig = "";
    /// <summary>Чертёж устарел: тела модели изменились после построения видов (открытый документ — по отпечатку тел; файл — по
    /// времени записи файла).</summary>
    public bool Outdated
    {
        get
        {
            if (Kind != Mode.Drawing || _scene == null) return false;
            if (DrawingSource != null) return DrawingInput.Signature(DrawingSource()?.Bodies ?? new()) != _projSig;
            if (Drawing?.SourcePath is { Length: > 0 } sp) return File.Exists(sp) && _fileInput is { } fi && fi.Stamp != File.GetLastWriteTimeUtc(sp);
            return false;
        }
    }
    /// <summary>Виды построить заново (сменилась модель, разрез, выносной элемент).</summary>
    public void InvalidateViews() { _scene = null; _fileInput = _fileInput is { } fi && Drawing?.SourcePath is { } sp && File.Exists(sp) && fi.Stamp != File.GetLastWriteTimeUtc(sp) ? null : _fileInput; }

    /// <summary>Проекции видов: построить, если их нет или набор видов изменился.</summary>
    void EnsureScene()
    {
        var d = Drawing!;
        if (_scene != null && d.Views.All(v => _scene.Lines.ContainsKey(v.Name))) return;
        bool wasEmpty = _scene == null || _scene.Lines.Values.All(l => l.Count == 0);
        _input = Input();
        _projSig = DrawingInput.Signature(_input.Bodies);
        _scene = DrawingScene.Build(d, _input.Bodies, _input.Items);
        if (wasEmpty) foreach (var v in d.Views) { v.X = 0; v.Y = 0; }   // габариты видов новые — раскладка заново
        if (_scene.Problems.Count > 0) _status.Text = string.Join("; ", _scene.Problems.Take(2));
    }

    public List<SheetGraphics> BuildPages()
    {
        if (Kind == Mode.Bom) { Bom!.Template = Template; return Bom.Pages(); }
        if (Kind == Mode.Drawing)
        {
            var d = Drawing!; d.Template = Template;
            EnsureScene();
            var fs = _input?.Part != null ? FieldSource.FromPart(_input.Part) : FieldValues();
            return new List<SheetGraphics> { d.Compose(_scene!, fs) };
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

    async Task ExportDxfAsync()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("Вывод в DXF"), DefaultExtension = ".dxf",
            SuggestedFileName = Path.GetFileNameWithoutExtension(FilePath ?? L("Чертёж")) + ".dxf",
            FileTypeChoices = new[] { new FilePickerFileType("DXF") { Patterns = new[] { "*.dxf" } } },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        DxfWriter.Save(path, BuildPages());
        _status.Text = L("DXF записан: {0}", Path.GetFileName(path));
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
