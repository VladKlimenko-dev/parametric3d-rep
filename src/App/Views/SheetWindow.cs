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

    readonly SheetCanvas _canvas = new();
    /// <summary>Поле листа (проверки).</summary>
    public SheetCanvas Canvas => _canvas;
    readonly StackPanel _props = new() { Spacing = 6, Margin = new Thickness(10) };
    readonly WrapPanel _ribbon = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4) };
    readonly TextBlock _status = new() { Margin = new Thickness(8, 2), Opacity = 0.75 };
    int _page;
    int _selRow = -1;
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
        var scroll = new Border { Child = _canvas, Background = new SolidColorBrush(Color.Parse("#5A5F66")) };
        var right = new ScrollViewer { Content = _props, Width = kind == Mode.Bom ? 560 : 340 };
        var dock = new DockPanel();
        DockPanel.SetDock(_ribbon, Dock.Top); dock.Children.Add(_ribbon);
        DockPanel.SetDock(_status, Dock.Bottom); dock.Children.Add(_status);
        DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right);
        dock.Children.Add(scroll);
        Content = dock;
        BuildRibbon();
        BuildProps();
        HookPointer();
        if (kind == Mode.Drawing)
        {
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
            }));
            Sep();
            // D3: размеры
            Btn("dr_auto_dims_but.svg", "Автоматические размеры (габариты, отверстия, радиусы)", () => Change(() => { EnsureScene(); Drawing!.AutoDimensions(_scene!); }));
            Btn("dr_show_model_dims_but.svg", "Размеры модели (из эскизов)", () => Change(() =>
            {
                EnsureScene();
                int n = _input?.Part != null ? Drawing!.ModelDimensions(_input.Part) : 0;
                Drawing!.ArrangeDims(_scene!);
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
        Btn("view_zoom_sel_but.svg", "Крупнее (колесо мыши — в точке под курсором, правая кнопка — сдвиг листа)", () => _canvas.ZoomAt(new Point(_canvas.Bounds.Width / 2, _canvas.Bounds.Height / 2), 1.25));
        Btn("view_fit_but.svg", "Лист целиком", () => _canvas.Fit());
        Btn("dr_sheet_ico.svg", "Следующий лист", () => { _page = Pages.Count == 0 ? 0 : (_page + 1) % Pages.Count; Refresh(); });
    }

    /// <summary>Раскладка видов заново (формат, ориентация, масштаб, набор видов).</summary>
    void Relayout() { if (Drawing != null) foreach (var v in Drawing.Views) { v.X = 0; v.Y = 0; } }

    /// <summary>
    /// k159: вкл/выкл основного вида. Положения остальных видов не меняются (прежде — раскладка заново, перенос пользователя
    /// терялся); выключенный вид запоминает место, масштаб и поворот (Parked) и при включении встаёт туда же.
    /// </summary>
    void ToggleView(string name) => Change(() =>
    {
        var d = Drawing!;
        FreezeScale();
        var v = d.Views.FirstOrDefault(x => x.Name == name);
        if (v != null) { d.Parked[name] = new[] { v.X, v.Y, v.Scale, v.Angle, v.ShowHidden ? 1 : 0 }; d.Views.Remove(v); if (_sel == v) _sel = null; }
        else
        {
            var nv = new DrawingView { Name = name, ShowHidden = name != "iso" };
            if (d.Parked.TryGetValue(name, out var pk) && pk.Length >= 5) { nv.X = pk[0]; nv.Y = pk[1]; nv.Scale = pk[2]; nv.Angle = pk[3]; nv.ShowHidden = pk[4] != 0; }
            d.Views.Add(nv);
        }
    });

    /// <summary>k159: подобранный масштаб листа закрепляется, когда пользователь начинает расставлять виды — иначе при смене
    /// набора видов масштаб подбирался бы заново и виды на своих местах меняли бы размер.</summary>
    void FreezeScale() { if (Drawing is { Scale: <= 0, LastScale: > 0 } d) d.Scale = d.LastScale; }

    // ------------------------------------------------------------------ мышь: увеличение, сдвиг, подсветка, выбор, перенос (k159)

    /// <summary>Выделенный элемент: размер, обозначение или вид.</summary>
    object? _sel;
    /// <summary>Элемент под курсором (подсветка).</summary>
    object? _hover;
    public object? Selected => _sel;
    public object? Hovered => _hover;
    object? _drag; double _lastX, _lastY; bool _moved;
    Point? _panAt; Point _panStart; bool _panRight;

    /// <summary>
    /// k161 (п. 12): меню размера — стрелки внутрь / наружу / по месту, радиус ↔ диаметр, текст к середине, место — заново,
    /// удалить; меню обозначения — удалить.
    /// </summary>
    public List<(string Text, Action Run)> ContextItems(double x, double y)
    {
        var d = Drawing!;
        var hit = d.HitAt(x, y);
        var items = new List<(string, Action)>();
        if (hit is DrawingDim dm)
        {
            _sel = dm; UpdateOverlay();
            items.Add((L("Стрелки внутри"), () => Change(() => dm.Arrows = 1)));
            items.Add((L("Стрелки снаружи"), () => Change(() => dm.Arrows = 2)));
            items.Add((L("Стрелки по месту"), () => Change(() => dm.Arrows = 0)));
            if (dm.Kind is "rad" or "dia") items.Add((dm.Kind == "rad" ? L("Диаметр вместо радиуса") : L("Радиус вместо диаметра"), () => Change(() => dm.Kind = dm.Kind == "rad" ? "dia" : "rad")));
            if (dm.TextShift != 0) items.Add((L("Текст к середине"), () => Change(() => dm.TextShift = 0)));
            items.Add((L("Место — автоматически"), () => Change(() => { dm.Arrange = true; dm.TextShift = 0; })));
            items.Add((L("Удалить"), () => Change(() => { d.Dims.Remove(dm); _sel = null; })));
        }
        else if (hit is DrawingNote n)
        {
            _sel = n; UpdateOverlay();
            items.Add((L("Удалить"), () => Change(() => { d.Notes.Remove(n); _sel = null; })));
        }
        return items;
    }
    void OpenContextMenu(double x, double y)
    {
        var items = ContextItems(x, y);
        if (items.Count == 0) return;
        var cm = new ContextMenu { ItemsSource = items.Select(i => { var mi = new MenuItem { Header = i.Text }; mi.Click += (_, _) => i.Run(); return mi; }).ToList() };
        cm.Open(_canvas);
    }

    void HookPointer()
    {
        _canvas.PointerWheelChanged += (_, e) => { _canvas.ZoomAt(e.GetPosition(_canvas), Math.Pow(1.2, e.Delta.Y)); e.Handled = true; };
        _canvas.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(_canvas);
            _canvas.Focus();
            if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed) { _panAt = pt.Position; _panStart = pt.Position; _panRight = pt.Properties.IsRightButtonPressed; e.Pointer.Capture(_canvas); e.Handled = true; return; }
            if (!pt.Properties.IsLeftButtonPressed || Kind != Mode.Drawing) return;
            var (x, y) = _canvas.ToSheet(pt.Position);
            if (e.ClickCount >= 2 && _pick == null) { FocusFieldAt(x, y); e.Handled = true; return; }
            Down(x, y);
            if (_drag != null) e.Pointer.Capture(_canvas);
        };
        _canvas.PointerMoved += (_, e) =>
        {
            var pos = e.GetPosition(_canvas);
            if (_panAt is { } pa) { _canvas.PanBy(pos.X - pa.X, pos.Y - pa.Y); _panAt = pos; return; }
            if (Kind != Mode.Drawing) return;
            var (x, y) = _canvas.ToSheet(pos);
            MoveTo(x, y);
        };
        _canvas.PointerReleased += (_, e) =>
        {
            // k161 (п. 12): правый щелчок без сдвига листа — меню элемента (размер, обозначение)
            if (_panAt is { } pa && _panRight && Kind == Mode.Drawing && Math.Abs(pa.X - _panStart.X) + Math.Abs(pa.Y - _panStart.Y) < 4)
            {
                var (x, y) = _canvas.ToSheet(e.GetPosition(_canvas));
                _panAt = null; e.Pointer.Capture(null);
                OpenContextMenu(x, y);
                return;
            }
            _panAt = null; Up(); e.Pointer.Capture(null);
        };
        _canvas.PointerExited += (_, _) => { if (_drag == null && _hover != null) { _hover = null; _hoverSegs = null; UpdateOverlay(); } };
        KeyDown += (_, e) =>
        {
            if (Kind != Mode.Drawing) return;
            if (e.Key == Avalonia.Input.Key.Delete && _pick == null && _sel != null && FocusManager?.GetFocusedElement() is not TextBox) { DeleteSelected(); e.Handled = true; return; }
            if (e.Key != Avalonia.Input.Key.Escape) return;
            if (_pick == null) { _sel = null; UpdateOverlay(); return; }
            // Esc в режиме шероховатости — неуказанная шероховатость в углу листа
            if (_pick == "rough") Change(() => { Drawing!.Notes.RemoveAll(n => n.Kind == "rough" && !n.HasTarget); Drawing.Notes.Add(new DrawingNote { Kind = "rough", Value = "Ra 6,3" }); });
            _pick = null; _status.Text = ""; _hoverSegs = null; UpdateOverlay();
        };
    }

    /// <summary>Нажатие левой кнопки в точке листа (мм): щелчок режима, иначе выбор элемента или вида и начало переноса.</summary>
    public void Down(double x, double y)
    {
        if (_pick != null) { try { PickAt(x, y); } catch (Exception ex) { _status.Text = ex.Message; Core.AppLog.Write("sheet: pick", ex); } _hoverSegs = null; UpdateOverlay(); return; }
        var d = Drawing!;
        _guides.Clear();
        // k161 (п. 5): у выбранной изометрии — кнопка «Домой» и кольцо поворота
        if (_sel is DrawingView sv && IsRotatable(sv) && d.Boxes.TryGetValue(sv.Name, out var sb))
        {
            var (hx, hy) = HomeAt(sb);
            if (Math.Abs(x - hx) < 3.5 && Math.Abs(y - hy) < 3.5) { HomeView(sv); return; }
            double rr = RingR(sb), dc = Math.Sqrt((x - sv.X) * (x - sv.X) + (y - sv.Y) * (y - sv.Y));
            if (Math.Abs(dc - rr) < 2.5) { _drag = new RotateDrag(sv); _lastX = x; _lastY = y; _moved = false; _status.Text = L("Поворот вида: ведите мышью"); return; }
        }
        // k161 (п. 10): точку привязки обозначения (база, допуск, шероховатость, позиция) можно перенести
        foreach (var n in d.Notes.Where(n => n.HasTarget && n.View != null && d.Xf.ContainsKey(n.View)))
        {
            var t = d.ToSheet(n.View!, new OpenTK.Mathematics.Vector2d(n.TX, n.TY));
            if (Math.Abs(t.X - x) < 2.5 && Math.Abs(t.Y - y) < 2.5) { _sel = n; _drag = new TargetDrag(n); _lastX = x; _lastY = y; _moved = false; _status.Text = L("Точка привязки: перенос по контуру"); UpdateOverlay(); return; }
        }
        _sel = d.HitAt(x, y) ?? (object?)(d.ViewAt(x, y) is { } vn ? d.Views.FirstOrDefault(v => v.Name == vn) : null);
        _drag = _sel; _lastX = x; _lastY = y; _moved = false;
        if (_sel is DrawingView dv0) _free = (dv0.X, dv0.Y);
        _status.Text = _sel switch
        {
            DrawingDim dm => L("Размер {0}: перенос мышью, Delete — удалить", d.CaptionOf(dm)),
            DrawingNote => L("Обозначение: перенос мышью, Delete — удалить"),
            DrawingView v => L("Вид: {0}", v.Label.Length > 0 ? v.Label : L(ViewTitle(v.Name))),
            _ => "",
        };
        UpdateOverlay();
    }

    /// <summary>Курсор в точке листа: перенос выбранного или подсветка того, что под курсором.</summary>
    public void MoveTo(double x, double y)
    {
        var d = Drawing!;
        if (_drag != null)
        {
            double dx = x - _lastX, dy = y - _lastY;
            if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 0.3) return;
            if (!_moved) FreezeScale();
            _moved = true;
            switch (_drag)
            {
                case DrawingDim dm: d.MoveDim(dm, dx, dy, x, y); break;
                case DrawingNote n: n.X += dx; n.Y += dy; break;
                case TargetDrag td when td.Note.View is { } tv && _scene != null && _scene.Lines.ContainsKey(tv):
                    {
                        var q = Snap(tv, d.ToView(tv, x, y), 5, false);
                        td.Note.TX = q.X; td.Note.TY = q.Y;
                        break;
                    }
                case RotateDrag rd: RotateView(rd.View, dx, dy, coarse: true); break;
                case DrawingView v:
                    {
                        // k161 (п. 6): свободный перенос с притяжением к центрам других видов (линии-подсказки)
                        _free = (_free.X + dx, _free.Y + dy);
                        v.X = _free.X; v.Y = _free.Y;
                        _guides = d.SnapView(v.Name);
                        break;
                    }
            }
            _lastX = x; _lastY = y;
            Dirty = true;
            RequestRefresh();
            return;
        }
        Hover(x, y);
    }

    public void Up()
    {
        if (_drag is RotateDrag rd && _moved && _input != null) { _scene?.Rebuild(Drawing!, rd.View.Name, _input.Bodies); }
        if (_drag != null && _moved) { UpdateTitle(); RefreshNow(); BuildProps(); }
        _drag = null; _guides.Clear(); UpdateOverlay();
    }

    sealed record TargetDrag(DrawingNote Note);
    sealed record RotateDrag(DrawingView View);
    (double X, double Y) _free;
    List<(double, double, double, double)> _guides = new();

    static bool IsRotatable(DrawingView v) => v.Name == "iso";
    static double RingR((double X0, double Y0, double X1, double Y1) b) => Math.Max(b.X1 - b.X0, b.Y1 - b.Y0) / 2 + 4;
    static (double X, double Y) HomeAt((double X0, double Y0, double X1, double Y1) b) => (b.X1 + 6, b.Y1 + 6);

    /// <summary>
    /// k161 (п. 5): поворот вида в пространстве мышью: сдвиг по листу на 1 мм — 1° вокруг вертикали вида (влево-вправо) и
    /// горизонтали (вверх-вниз). Рамка вида — своя («v:…»); при переносе — грубая сетка, после отпускания — точная.
    /// </summary>
    public void RotateView(DrawingView v, double dx, double dy, bool coarse = false)
    {
        var d = Drawing!;
        var f = ViewFrame.Of(d.DirOf(v));
        var qy = OpenTK.Mathematics.Quaterniond.FromAxisAngle(f.Up, dx * Math.PI / 180);
        var qx = OpenTK.Mathematics.Quaterniond.FromAxisAngle(f.Right, -dy * Math.PI / 180);
        var q = qx * qy;
        var r = OpenTK.Mathematics.Vector3d.Transform(f.Right, q); var u = OpenTK.Mathematics.Vector3d.Transform(f.Up, q);
        v.Dir = new ViewFrame(r.Normalized(), u.Normalized()).Encode();
        EnsureScene();
        if (_input != null) _scene!.Rebuild(d, v.Name, _input.Bodies, coarse ? 0.2 : 0.02);
        Dirty = true;
    }

    /// <summary>k161: изометрия ГОСТ 2.317 — вернуть вид в положение по умолчанию («Домой»).</summary>
    public void HomeView(DrawingView v)
    {
        Change(() => { v.Dir = ""; EnsureScene(); if (_input != null) _scene!.Rebuild(Drawing!, v.Name, _input.Bodies); });
        _status.Text = L("Изометрия — положение по умолчанию");
    }

    /// <summary>Удалить выделенное: размер, обозначение, разрез / выносной / вспомогательный вид; основной вид — выключить.</summary>
    public void DeleteSelected()
    {
        var d = Drawing!;
        var sel = _sel; _sel = null;
        switch (sel)
        {
            case DrawingDim dm: Change(() => d.Dims.Remove(dm)); break;
            case DrawingNote n: Change(() => d.Notes.Remove(n)); break;
            case DrawingView v when v.IsSection || v.IsDetail || v.IsAux: Change(() => { d.Views.RemoveAll(x => x == v || x.Parent == v.Name); d.Dims.RemoveAll(x => x.View == v.Name); InvalidateViews(); }); break;
            case DrawingView v: ToggleView(v.Name); break;
        }
    }

    // перерисовка при переносе — не чаще кадра
    bool _refreshPending;
    void RequestRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _refreshPending = false; RefreshNow(); }, Avalonia.Threading.DispatcherPriority.Render);
    }
    void RefreshNow() { Refresh(); }

    static string ViewTitle(string n) => n switch { "front" => "Главный (спереди)", "top" => "Сверху", "left" => "Слева", "right" => "Справа", "bottom" => "Снизу", "back" => "Сзади", "iso" => "Изометрия", _ => n };

    List<(double, double, double, double)>? _hoverSegs;
    static readonly SkiaSharp.SKColor HoverColor = new(30, 140, 255, 170), SelColor = new(255, 120, 0, 200), TargetColor = new(0, 170, 90, 210), GizmoColor = new(200, 40, 160, 220);

    static List<(double, double, double, double)> RectSegs((double X0, double Y0, double X1, double Y1) b, double pad = 1)
    {
        double x0 = b.X0 - pad, y0 = b.Y0 - pad, x1 = b.X1 + pad, y1 = b.Y1 + pad;
        return new() { (x0, y0, x1, y0), (x1, y0, x1, y1), (x1, y1, x0, y1), (x0, y1, x0, y0) };
    }
    List<(double, double, double, double)> SegsOf(object o)
    {
        var d = Drawing!;
        if (o is DrawingView v) return d.Boxes.TryGetValue(v.Name, out var b) ? RectSegs(b, 2) : new();
        return d.Hit.TryGetValue(o, out var s) ? s : new();
    }
    /// <summary>Ломаная вида → отрезки листа.</summary>
    List<(double, double, double, double)> SheetSegs(string view, IEnumerable<OpenTK.Mathematics.Vector2d> pts)
    {
        var d = Drawing!; var res = new List<(double, double, double, double)>();
        (double X, double Y)? prev = null;
        foreach (var p in pts) { var q = d.ToSheet(view, p); if (prev is { } a) res.Add((a.X, a.Y, q.X, q.Y)); prev = q; }
        return res;
    }
    List<(double, double, double, double)> Ring(double x, double y, double r)
    {
        var res = new List<(double, double, double, double)>();
        for (int k = 0; k < 16; k++) { double a0 = k * Math.PI / 8, a1 = (k + 1) * Math.PI / 8; res.Add((x + r * Math.Cos(a0), y + r * Math.Sin(a0), x + r * Math.Cos(a1), y + r * Math.Sin(a1))); }
        return res;
    }

    /// <summary>
    /// Подсветка под курсором: в режиме щелчка — цель привязки (точка размера, окружность, линия контура, отрезок, деталь,
    /// место разреза), иначе — размер или обозначение, иначе — габарит вида.
    /// </summary>
    public void Hover(double x, double y)
    {
        var d = Drawing!;
        _hoverSegs = null; _hover = null;
        if (_pick != null) _hoverSegs = PickPreview(x, y);
        else
        {
            _hover = d.HitAt(x, y) ?? (object?)(d.ViewAt(x, y) is { } vn ? d.Views.FirstOrDefault(v => v.Name == vn) : null);
        }
        UpdateOverlay();
    }

    /// <summary>Отрезки подсветки цели щелчка в режиме pick (мм листа); null — цели нет.</summary>
    public List<(double, double, double, double)>? PickPreview(double x, double y)
    {
        var d = Drawing!;
        var view = d.ViewAt(x, y);
        if (view == null || !d.Xf.ContainsKey(view) || _scene == null || !_scene.Lines.ContainsKey(view)) return null;
        var q = d.ToView(view, x, y); double s = d.Xf[view].S;
        switch (_pick)
        {
            case "dim1" or "dim2":
                {
                    var t = Snap(view, q, 4, true);
                    var ts = d.ToSheet(view, t);
                    var res = Ring(ts.X, ts.Y, 1.2);
                    if (_pick == "dim2" && _first is { } f && f.View == view) { var a = d.ToSheet(view, f.P); res.Add((a.X, a.Y, ts.X, ts.Y)); }
                    return res;
                }
            case "dia" or "rad":
                {
                    var c = FindCircle(view, q, _pick);
                    if (c == null || Math.Abs((q - c.Value.C).Length - c.Value.R) * s > 5) return null;
                    var cs = d.ToSheet(view, c.Value.C);
                    return Ring(cs.X, cs.Y, c.Value.R * s);
                }
            case "rough" or "gdt" or "datum" or "aux":
                {
                    var seg = NearestSegment(view, q, 5 / s);
                    return seg == null ? null : SheetSegs(view, new[] { seg.Value.A, seg.Value.B });
                }
            case "balloon":
                {
                    int body = BodyAt(view, q);
                    return _scene.Lines[view].Where(l => l.Visible && l.Body == body).SelectMany(l => SheetSegs(view, l.Points)).ToList();
                }
            case "section" or "break1" or "break2":
                {
                    var b = d.Boxes[view];
                    return _pick == "section" && (d.DirOf(d.Views.First(v => v.Name == view)) is "top" or "bottom")
                        ? new() { (b.X0 - 3, y, b.X1 + 3, y) } : new() { (x, b.Y0 - 3, x, b.Y1 + 3) };
                }
            case "detail": return Ring(x, y, 12);
        }
        return null;
    }

    (OpenTK.Mathematics.Vector2d A, OpenTK.Mathematics.Vector2d B)? NearestSegment(string view, OpenTK.Mathematics.Vector2d q, double maxDist)
    {
        (OpenTK.Mathematics.Vector2d, OpenTK.Mathematics.Vector2d)? seg = null; double best = maxDist;
        foreach (var l in _scene!.Lines[view])
            for (int i = 0; l.Visible && i + 1 < l.Points.Count; i++)
            {
                var a = l.Points[i]; var ab = l.Points[i + 1] - a;
                if (ab.Length < 1e-9) continue;
                double t = Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
                double dd = (a + ab * t - q).Length;
                if (dd < best) { best = dd; seg = (a, l.Points[i + 1]); }
            }
        return seg;
    }

    (OpenTK.Mathematics.Vector2d C, double R, double A)? FindCircle(string view, OpenTK.Mathematics.Vector2d q, string mode)
    {
        var arcs = _scene!.Arcs.GetValueOrDefault(view) ?? new();
        var cands = arcs.Where(a => mode == "rad" ? !a.Full : true).Select(a => (C: a.Center, R: a.Radius, A: Math.Atan2(q.Y - a.Center.Y, q.X - a.Center.X)))
            .Concat(mode == "dia" ? (_scene.Axes.GetValueOrDefault(view) ?? new()).Where(a => a.Cross).Select(a => (C: a.Center, R: a.Radius, A: Math.Atan2(q.Y - a.Center.Y, q.X - a.Center.X))) : Enumerable.Empty<(OpenTK.Mathematics.Vector2d C, double R, double A)>())
            .OrderBy(c => Math.Abs((q - c.C).Length - c.R)).ToList();
        return cands.Count == 0 ? null : cands[0];
    }

    void UpdateOverlay()
    {
        _canvas.Overlay.Clear();
        if (Kind == Mode.Drawing && Drawing != null)
        {
            if (_hover != null && _hover != _sel) _canvas.Overlay.Add((SegsOf(_hover), HoverColor, _hover is DrawingView ? 1.2f : 3f));
            if (_sel != null) _canvas.Overlay.Add((SegsOf(_sel), SelColor, _sel is DrawingView ? 1.6f : 3f));
            if (_hoverSegs != null) _canvas.Overlay.Add((_hoverSegs, TargetColor, 3f));
            if (_guides.Count > 0) _canvas.Overlay.Add((_guides.Select(gl => { var (x1, y1, x2, y2) = gl; return x1 == x2 ? (x1, y1 - 30, x2, y2 + 30) : (x1 - 30, y1, x2 + 30, y2); }).ToList(), TargetColor, 1.2f));
            if (_sel is DrawingView gv && IsRotatable(gv) && Drawing.Boxes.TryGetValue(gv.Name, out var gb))
            {
                // кольцо поворота и кнопка «Домой» (домик)
                _canvas.Overlay.Add((Ring(gv.X, gv.Y, RingR(gb)), GizmoColor, 2f));
                var (hx, hy) = HomeAt(gb);
                _canvas.Overlay.Add((new List<(double, double, double, double)>
                {
                    (hx - 3, hy - 3, hx + 3, hy - 3), (hx + 3, hy - 3, hx + 3, hy + 0.5), (hx - 3, hy - 3, hx - 3, hy + 0.5),
                    (hx - 4, hy + 0.5, hx, hy + 3.5), (hx, hy + 3.5, hx + 4, hy + 0.5), (hx - 1, hy - 3, hx - 1, hy - 0.5), (hx - 1, hy - 0.5, hx + 1, hy - 0.5), (hx + 1, hy - 0.5, hx + 1, hy - 3),
                }, GizmoColor, 2f));
            }
        }
        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// Двойной щелчок по полю основной надписи — фокус на поле в панели свойств (раздел раскрывается). Возвращает имя поля.
    /// </summary>
    public string? FocusFieldAt(double x, double y)
    {
        if (Pages.Count == 0) return null;
        var t = Pages[_page].Texts.Where(t => t.Field != null).OrderBy(t => Math.Abs(t.X - x) + Math.Abs(t.Y + t.Height / 2 - y)).FirstOrDefault();
        if (t == null || Math.Abs(t.X - x) > 25 || Math.Abs(t.Y + t.Height / 2 - y) > 6) return null;
        if (_fieldControls.TryGetValue(t.Field!, out var c))
        {
            for (var p = c.Parent; p != null; p = p.Parent) if (p is Expander ex) ex.IsExpanded = true;
            c.Focus(); c.BringIntoView();
            _status.Text = L("Поле надписи: {0}", t.Field!);
        }
        return t.Field;
    }
    readonly Dictionary<string, Control> _fieldControls = new();

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
        // k161: новый вид (разрез, выносной, вспомогательный) не меняет подобранный масштаб — иначе прежние виды на своих
        // местах меняли бы размер и заходили друг на друга
        FreezeScale();
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

    /// <summary>k159: раскрыт ли раздел панели свойств (по заголовку) — сохраняется между перестроениями панели.</summary>
    readonly Dictionary<string, bool> _expanded = new();
    /// <summary>Разделы панели (проверки).</summary>
    public IEnumerable<Expander> Sections => _props.Children.OfType<Expander>();

    /// <summary>Сворачиваемый раздел панели свойств.</summary>
    StackPanel Section(string title, bool open = true)
    {
        var body = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
        var ex = new Expander { Header = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, Content = body, IsExpanded = _expanded.GetValueOrDefault(title, open), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = title };
        ex.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) _expanded[title] = ex.IsExpanded; };
        _props.Children.Add(ex);
        return body;
    }

    static readonly string[] LengthUnits = { "", "mm", "cm", "m", "in", "ft" };

    void BuildProps()
    {
        _props.Children.Clear();
        _fieldControls.Clear();
        if (Kind is Mode.Template or Mode.Drawing)
        {
            if (Kind == Mode.Drawing)
            {
                var d = Drawing!;
                // чертёж: масштаб листа (ряд ГОСТ 2.302), единицы, модель
                var dp = Section(L("Чертёж"));
                var scales = new List<string> { L("подбирать ({0})", d.LastScale > 0 ? DrawingDocument.ScaleText(d.LastScale) : "—") };
                scales.AddRange(DrawingDocument.GostScales.Select(DrawingDocument.ScaleText));
                var sheetScale = new ComboBox { ItemsSource = scales, SelectedIndex = d.Scale > 0 ? 1 + Array.IndexOf(DrawingDocument.GostScales, d.Scale) : 0, MinWidth = 140, Name = "dr_sheet_scale" };
                sheetScale.SelectionChanged += (_, _) =>
                {
                    double ns = sheetScale.SelectedIndex <= 0 ? 0 : DrawingDocument.GostScales[sheetScale.SelectedIndex - 1];
                    if (ns != d.Scale) Change(() => d.Scale = ns);
                };
                _fieldControls[Model.Drawing.Fields.Scale] = sheetScale;
                dp.Children.Add(Row(L("Масштаб листа"), sheetScale));
                var unitNames = LengthUnits.Select(u => u.Length == 0 ? L("как в модели ({0})", Commands.DisplayUnits.Symbol(d.ModelUnits)) : Commands.DisplayUnits.Symbol(u)).ToList();
                var units = new ComboBox { ItemsSource = unitNames, SelectedIndex = Math.Max(0, Array.IndexOf(LengthUnits, d.Units)), MinWidth = 140, Name = "dr_units" };
                units.SelectionChanged += (_, _) => { var u = LengthUnits[Math.Max(0, units.SelectedIndex)]; if (u != d.Units) Change(() => d.Units = u); };
                dp.Children.Add(Row(L("Единицы длины"), units));
                // k161 (п. 4): толщина основной линии S, ГОСТ 2.303
                double[] sv = { 0.5, 0.6, 0.7, 0.8, 1.0, 1.4 };
                var lineS = new ComboBox { ItemsSource = sv.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture) + L(" мм")).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(sv, x => Math.Abs(x - d.LineS) < 1e-9)), MinWidth = 140 };
                lineS.SelectionChanged += (_, _) => { double ns = sv[Math.Max(0, lineS.SelectedIndex)]; if (Math.Abs(ns - d.LineS) > 1e-9) Change(() => d.LineS = ns); };
                ToolTip.SetTip(lineS, L("Толщина сплошной основной линии S (ГОСТ 2.303: 0,5…1,4 мм); тонкие — S/2"));
                dp.Children.Add(Row(L("Основная линия"), lineS));
                dp.Children.Add(new TextBlock { Text = d.SourcePath != null ? L("Модель: {0}", Path.GetFileName(d.SourcePath)) : L("Модель: открытый документ"), Opacity = 0.8, TextWrapping = TextWrapping.Wrap });

                var vp = Section(L("Виды"));
                foreach (var (n, lab) in new[] { ("front", "Главный (спереди)"), ("top", "Сверху"), ("left", "Слева"), ("right", "Справа"), ("bottom", "Снизу"), ("back", "Сзади"), ("iso", "Изометрия") })
                {
                    var cb = new CheckBox { Content = L(lab), IsChecked = d.Views.Any(v => v.Name == n), MinWidth = 130 };
                    cb.IsCheckedChanged += (_, _) => { bool on = cb.IsChecked == true; if (on != d.Views.Any(v => v.Name == n)) ToggleView(n); };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { cb } };
                    if (d.Views.FirstOrDefault(v => v.Name == n) is { } dv) AddViewControls(row, dv);
                    vp.Children.Add(row);
                }
                DrawingProps(d);
            }
            var sp = Section(L("Лист"));
            _formatBox = new ComboBox { ItemsSource = SheetFormats.All.Select(f => f.Code).ToList(), SelectedItem = Template.Format, MinWidth = 120 };
            _formatBox.SelectionChanged += (_, _) => { if (_formatBox.SelectedItem is string s && s != Template.Format) Change(() => { Template.Format = s; Relayout(); }); };
            sp.Children.Add(Row(L("Формат"), _formatBox));
            sp.Children.Add(new TextBlock { Text = $"{L(Template.Landscape ? "Альбомная" : "Книжная")} · {(Template.Standard == SheetStandard.Eskd ? L("ЕСКД") : "ISO")} · {FormName(Template.TitleBlock)}", Opacity = 0.8 });
            sp.Children.Add(Row(L("Имя шаблона"), Field(Template.Name, v => Change(() => Template.Name = v))));
            var fp = Section(L("Поля основной надписи"));
            var src = FieldValues();
            foreach (var (f, lab) in FieldLabels)
            {
                // k159: масштаб чертежа — не текст, а выбор из ряда ГОСТ (раздел «Чертёж»); в надписи — масштаб листа
                if (Kind == Mode.Drawing && f == Model.Drawing.Fields.Scale)
                {
                    var link = new Button { Content = L("масштаб листа: {0} — выбор в разделе «Чертёж»", Drawing!.LastScale > 0 ? DrawingDocument.ScaleText(Drawing.LastScale) : "—"), FontSize = 11, Padding = new Thickness(4, 1) };
                    link.Click += (_, _) => { if (_fieldControls.TryGetValue(f, out var c)) { for (var p = c.Parent; p != null; p = p.Parent) if (p is Expander ex) ex.IsExpanded = true; c.Focus(); } };
                    fp.Children.Add(Row(L(lab), link));
                    continue;
                }
                var tb = Field(Template.Values.GetValueOrDefault(f) ?? "", v => Change(() => { if (v.Length == 0) Template.Values.Remove(f); else Template.Values[f] = v; }));
                tb.Watermark = src.Auto.GetValueOrDefault(f) ?? "";
                _fieldControls[f] = tb;
                fp.Children.Add(Row(L(lab), tb));
            }
            if (Template.Items.Count > 0)
            {
                var ip = Section(L("Элементы шаблона"));
                foreach (var it in Template.Items.ToList())
                {
                    var del = new Button { Content = "×", Padding = new Thickness(6, 0) };
                    del.Click += (_, _) => Change(() => Template.Items.Remove(it));
                    Control body = it.Kind == "text"
                        ? new TextBox { Text = it.Text, AcceptsReturn = true, MinHeight = 44, Width = 230, FontSize = 12 }
                        : new TextBlock { Text = it.Kind == "line" ? L("Линия") : L("Таблица {0}×{1}", it.Rows, it.Columns?.Count ?? 0), VerticalAlignment = VerticalAlignment.Center };
                    if (body is TextBox tb) tb.LostFocus += (_, _) => Change(() => it.Text = tb.Text ?? "");
                    ip.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { body, del } });
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

    /// <summary>
    /// k159: масштаб вида (ряд ГОСТ 2.302: «как лист» или свой — подписывается над видом в скобках), поворот на листе (градусы,
    /// против часовой), у вида с разрывом — снять разрыв.
    /// </summary>
    void AddViewControls(StackPanel row, DrawingView dv)
    {
        var items = new List<string> { L("как лист") }; items.AddRange(DrawingDocument.GostScales.Select(DrawingDocument.ScaleText));
        var sc = new ComboBox { ItemsSource = items, SelectedIndex = dv.Scale > 0 ? 1 + Array.IndexOf(DrawingDocument.GostScales, dv.Scale) : 0, FontSize = 11, MinWidth = 86 };
        sc.SelectionChanged += (_, _) =>
        {
            double ns = sc.SelectedIndex <= 0 ? 0 : DrawingDocument.GostScales[sc.SelectedIndex - 1];
            if (ns != dv.Scale) Change(() => { FreezeScale(); dv.Scale = ns; });
        };
        ToolTip.SetTip(sc, L("Масштаб вида"));
        row.Children.Add(sc);
        var ang = Field(dv.Angle.ToString("0.##", CultureInfo.InvariantCulture), t =>
        {
            if (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && Math.Abs(a - dv.Angle) > 1e-9)
                Change(() => { FreezeScale(); dv.Angle = Math.IEEERemainder(a, 360); });
        }, 46);
        ToolTip.SetTip(ang, L("Поворот вида на листе, °"));
        row.Children.Add(ang);
        row.Children.Add(new TextBlock { Text = "°", VerticalAlignment = VerticalAlignment.Center });
        // k161 (п. 9): отображение вида
        var styles = new[] { ("", "Видимые линии"), ("hidden", "С невидимыми линиями"), ("wire", "Каркас"), ("shaded", "Тона"), ("shadedlines", "Тона с линиями"), ("material", "Материал") };
        var st = new ComboBox { ItemsSource = styles.Select(x => L(x.Item2)).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(styles, x => x.Item1 == dv.Style)), FontSize = 11, MinWidth = 96 };
        st.SelectionChanged += (_, _) =>
        {
            var ns = styles[Math.Max(0, st.SelectedIndex)].Item1;
            if (ns != dv.Style) Change(() => { dv.Style = ns; EnsureScene(); if (_input != null) _scene!.Rebuild(Drawing!, dv.Name, _input.Bodies); });
        };
        ToolTip.SetTip(st, L("Отображение вида"));
        row.Children.Add(st);
        if (IsRotatable(dv))
        {
            var home = new Button { Content = L("Домой"), FontSize = 11, Padding = new Thickness(4, 1) };
            ToolTip.SetTip(home, L("Изометрия — положение по умолчанию (поворот — кольцо вокруг выбранного вида)"));
            home.Click += (_, _) => HomeView(dv);
            row.Children.Add(home);
        }
        if (dv.HasBreak)
        {
            var nb = new Button { Content = L("без разрыва"), FontSize = 11, Padding = new Thickness(4, 1) };
            nb.Click += (_, _) => Change(() => { dv.BreakA = dv.BreakB = 0; });
            row.Children.Add(nb);
        }
    }

    /// <summary>Разрезы и выносные, размеры, обозначения — списком с правкой.</summary>
    void DrawingProps(DrawingDocument d)
    {
        Button Del(Action a) { var b = new Button { Content = "×", Padding = new Thickness(6, 0) }; b.Click += (_, _) => Change(a); return b; }
        static double Num(string v, double old) => double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : old;
        var extra = d.Views.Where(v => v.IsSection || v.IsDetail || v.IsAux).ToList();
        if (extra.Count > 0)
        {
            var ep = Section(L("Разрезы, выносные и вспомогательные виды"));
            foreach (var v in extra)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(new TextBlock { Text = v.IsSection ? $"{v.Label}-{v.Label}" : v.Label, MinWidth = 34, VerticalAlignment = VerticalAlignment.Center });
                if (v.IsSection) row.Children.Add(Field(v.CutDepth.ToString("0.###", CultureInfo.InvariantCulture), t => { double n = Num(t, v.CutDepth); if (n != v.CutDepth) Change(() => { v.CutDepth = n; InvalidateViews(); }); }, 64));
                else if (v.IsDetail) row.Children.Add(Field(v.DetailR.ToString("0.###", CultureInfo.InvariantCulture), t => { double n = Num(t, v.DetailR); if (n > 0 && n != v.DetailR) Change(() => { v.DetailR = n; InvalidateViews(); }); }, 64));
                if (!v.IsAux) row.Children.Add(new TextBlock { Text = v.IsSection ? L("глуб.") : L("R"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                AddViewControls(row, v);
                row.Children.Add(Del(() => { d.Views.RemoveAll(x => x == v || x.Parent == v.Name); d.Dims.RemoveAll(x => x.View == v.Name); InvalidateViews(); }));
                ep.Children.Add(row);
            }
        }
        if (d.Dims.Count > 0)
        {
            var dp = Section(L("Размеры ({0})", d.Dims.Count));
            foreach (var dim in d.Dims.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var show = new CheckBox { IsChecked = !dim.Hidden, MinWidth = 0 };
                show.IsCheckedChanged += (_, _) => { bool h = show.IsChecked != true; if (h != dim.Hidden) Change(() => dim.Hidden = h); };
                row.Children.Add(show);
                row.Children.Add(new TextBlock { Text = $"{dim.View}: {d.CaptionOf(dim)}", MinWidth = 110, VerticalAlignment = VerticalAlignment.Center });
                var tol = Field(dim.Tol, t => { if (t != dim.Tol) Change(() => dim.Tol = t); }, 60); tol.Watermark = L("допуск");
                var txt = Field(dim.Text, t => { if (t != dim.Text) Change(() => dim.Text = t); }, 60); txt.Watermark = L("текст");
                var off = Field(dim.Offset.ToString("0.#", CultureInfo.InvariantCulture), t => { double n = Num(t, dim.Offset); if (n != dim.Offset) Change(() => { dim.Offset = n; dim.Arrange = false; }); }, 44);
                row.Children.Add(tol); row.Children.Add(txt); row.Children.Add(off); row.Children.Add(Del(() => d.Dims.Remove(dim)));
                if (dim == _sel) row.Background = new SolidColorBrush(Color.FromArgb(60, 255, 120, 0));
                dp.Children.Add(row);
            }
        }
        if (d.Notes.Count > 0)
        {
            var np = Section(L("Обозначения ({0})", d.Notes.Count));
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
                if (n == _sel) row.Background = new SolidColorBrush(Color.FromArgb(60, 255, 120, 0));
                np.Children.Add(row);
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
        // k159: положения видов из файла чертежа сохраняются; место получают только виды с X = Y = 0, и только когда у них
        // есть линии (Layout) — прежде раскладка сбрасывалась при каждом построении после пустого (перенос пользователя терялся)
        _input = Input();
        _projSig = DrawingInput.Signature(_input.Bodies);
        d.ModelUnits = _input.Part?.Units.Length ?? "mm";
        _scene = DrawingScene.Build(d, _input.Bodies, _input.Items, _input.Colors);
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
            _canvas.Graphics = g;
            if (_sel != null && Kind == Mode.Drawing && !(Drawing!.Dims.Contains(_sel) || Drawing.Notes.Contains(_sel) || Drawing.Views.Contains(_sel))) _sel = null;
            UpdateOverlay();
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
