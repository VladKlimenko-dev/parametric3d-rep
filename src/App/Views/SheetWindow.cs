using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
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
public sealed partial class SheetWindow : Window
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
    /// <summary>Панель свойств (проверки: ширина).</summary>
    public Control PropsHost { get; private set; } = null!;
    /// <summary>Поле листа (проверки).</summary>
    public SheetCanvas Canvas => _canvas;
    readonly StackPanel _props = new() { Spacing = 6, Margin = new Thickness(10) };
    readonly WrapPanel _ribbon = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4) };
    readonly TextBlock _status = new() { Margin = new Thickness(8, 2), Opacity = 0.75 };
    readonly Canvas _editHost = new();
    static readonly bool TableToggleOff = Environment.GetEnvironmentVariable("P3D_TABLETOGGLE") == "0";
    static readonly bool CubeOff = Environment.GetEnvironmentVariable("P3D_VIEWCUBE") == "0";
    readonly TextBlock _sheetInfo = new() { Margin = new Thickness(8, 2), Opacity = 0.6 };
    public string SheetInfo => _sheetInfo.Text ?? "";
    readonly ProgressBar _progress = new() { Width = 220, Height = 10, Minimum = 0, Maximum = 1, IsVisible = false, Margin = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center };
    /// <summary>k167: виды строятся в фоне (false — сразу, на месте; проверки).</summary>
    public static bool SyncViews = Environment.GetEnvironmentVariable("P3D_ASYNCVIEWS") == "0";
    readonly HashSet<string> _building = new();
    /// <summary>k167: виды, которые строятся в фоне.</summary>
    public bool BuildingViews => _building.Count > 0;
    public double ProgressValue => _progress.IsVisible ? _progress.Value : -1;
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
        if (Drawing != null) { Drawing.Template = Template; Drawing.EnsureViews(); _root = Drawing; }
        if (Bom != null) Bom.Template ??= Template;
        Width = 1200; Height = 820;
        this.Bind(BackgroundProperty, this.GetResourceObservable("P3.PanelBg"));
        // k168: поверх поля листа — место для поля ввода ячейки таблицы (правка на листе)
        _canvas.ViewChanged += UpdateCube; _editHost.ClipToBounds = true;
        Grid.SetIsSharedSizeScope(_props, true);
        // k170: курсор ввода ушёл из полей панели — отложенное перестроение панели
        _props.AddHandler(Avalonia.Input.InputElement.LostFocusEvent, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_propsStale && !TextInProps()) BuildProps(); }), handledEventsToo: true);
        var scroll = new Border { Child = new Panel { Children = { _canvas, _editHost } }, Background = new SolidColorBrush(Color.Parse("#5A5F66")) };
        // k162: ширина панели свойств — перетаскиванием левого края
        var right = new Border { Child = new ScrollViewer { Content = _props }, Width = kind == Mode.Bom ? 560 : 340, BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)), Background = Brushes.Transparent, Padding = new Thickness(PanelResize.Grip - 1, 0, 0, 0) };
        PropsHost = right;
        PanelResize.Attach(right, "sheet." + kind, 240, 1000);
        var dock = new DockPanel();
        DockPanel.SetDock(_ribbon, Dock.Top); dock.Children.Add(_ribbon);
        // k167 (замечание 05.11, 16:17, п. 5): строка состояния — текст и полоса хода построения видов
        var statusRow = new DockPanel();
        DockPanel.SetDock(_sheetInfo, Dock.Right); statusRow.Children.Add(_sheetInfo);
        DockPanel.SetDock(_progress, Dock.Right); statusRow.Children.Add(_progress); statusRow.Children.Add(_status);
        DockPanel.SetDock(statusRow, Dock.Bottom); dock.Children.Add(statusRow);
        // k169 (п. 16): ярлыки листов над строкой состояния
        if (kind == Mode.Drawing) { DockPanel.SetDock(_tabs, Dock.Bottom); dock.Children.Add(_tabs); }
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
        UpdateTabs();
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
    // k167 (замечание 05.11, 16:17, п. 7, 10): кнопки-переключатели — включённая подсвечена
    readonly List<(Button B, Func<bool> On)> _toggles = new();
    Button Toggle(string icon, string tip, Action run, Func<bool> on) { var b = Btn(icon, tip, run); _toggles.Add((b, on)); return b; }
    static readonly IBrush ToggleOnBrush = new SolidColorBrush(Color.FromArgb(110, 60, 140, 230));
    public void UpdateToggles()
    {
        foreach (var (b, on) in _toggles)
        {
            bool v; try { v = on(); } catch { v = false; }
            b.Background = v ? ToggleOnBrush : null;
            b.Tag = v;
        }
    }
    /// <summary>k167: состояние кнопки-переключателя по подсказке (для проверок).</summary>
    public bool? ToggleState(string tip) => _toggles.FirstOrDefault(t => ToolTip.GetTip(t.B) as string == L(tip)).B?.Tag as bool?;
    /// <summary>k167: вид, к которому относятся кнопки видов: выбранный, иначе — все.</summary>
    IEnumerable<DrawingView> TargetViews() => _sel is DrawingView sv ? new[] { sv } : Drawing!.Views;
    /// <summary>k167: нажать кнопку ленты по подсказке (для проверок).</summary>
    public void Press(string tip) { foreach (var c in _ribbon.Children.OfType<Button>()) if (ToolTip.GetTip(c) as string == L(tip)) { c.Command?.Execute(null); c.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); return; } throw new InvalidOperationException("no button " + tip); }

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
            Toggle("dr_hidden_lines_but.svg", "Невидимые линии", () => Change(() => { bool on = !TargetViews().All(v => v.ShowHidden); foreach (var v in TargetViews()) v.ShowHidden = on; }), () => TargetViews().All(v => v.ShowHidden));
            Toggle("dr_tangent_edges_but.svg", "Линии перехода", () => Change(() => { bool on = !TargetViews().All(v => v.ShowTangent); foreach (var v in TargetViews()) v.ShowTangent = on; }), () => TargetViews().All(v => v.ShowTangent));
            // k167 (п. 7): переключатель; выбран вид — только для него (в том числе изометрия)
            Toggle("dr_centerline_but.svg", "Осевые линии", () => Change(() => { bool on = !TargetViews().All(v => v.CenterLines); foreach (var v in TargetViews()) v.CenterLines = on; }), () => TargetViews().All(v => v.CenterLines));
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
            // k167 (п. 6): выбран вид — размеры только на нём
            Btn("dr_auto_dims_but.svg", "Автоматические размеры (габариты, отверстия, радиусы)", () => Change(() =>
            {
                EnsureScene();
                var only = _sel is DrawingView sv ? sv.Name : null;
                Drawing!.AutoDimensions(_scene!, only);
                _status.Text = only != null ? L("Автоматические размеры: вид «{0}»", ViewLabel(only)) : L("Автоматические размеры: все виды");
            }));
            Btn("dr_show_model_dims_but.svg", "Размеры модели (из эскизов)", () => Change(() =>
            {
                EnsureScene();
                int n = _input?.Part != null ? Drawing!.ModelDimensions(_input.Part) : 0;
                Drawing!.ArrangeDims(_scene!);
                _status.Text = _input?.Part == null ? L("Размеры модели — только у чертежа детали") : L("Перенесено размеров эскизов: {0}", n);
            }));
            Toggle("dr_dims_toggle_but.svg", "Показать / скрыть размеры", () => Change(() => Drawing!.ShowDims = !Drawing.ShowDims), () => Drawing!.ShowDims);
            Btn("dr_dim_linear_but.svg", "Линейный размер: два щелчка по точкам вида", () => StartPick("dim1", L("Размер: щёлкните первую точку на виде")));
            // k165 (замечание 05.11, 13:27, п. 3–4): размер с подбором типа по элементам и угловой размер
            Btn("dr_dim_auto_but.svg", "Размер (авто): тип по выбранным элементам — две параллельные линии (расстояние между ними), точка и линия, две линии под углом (угол), окружность (диаметр), дуга (радиус), две точки, несколько — цепочка. Элементы выбираются щелчком до или после нажатия (Ctrl или Shift — ещё), Enter или средняя кнопка — поставить", () => AutoDimButton());
            Btn("dr_dim_angle_but.svg", "Угловой размер: щелчки по двум линиям", () => StartPick("ang1", L("Угол: щёлкните первую линию")));
            Btn("dr_dim_diameter_but.svg", "Диаметр: щелчок по окружности", () => StartPick("dia", L("Диаметр: щёлкните окружность")));
            Btn("dr_dim_radial_but.svg", "Радиус: щелчок по дуге", () => StartPick("rad", L("Радиус: щёлкните дугу")));
            Sep();
            // D4: обозначения
            Btn("dr_roughness_but.svg", "Шероховатость: щелчок по контуру (Esc — неуказанная в углу)", () => StartPick("rough", L("Шероховатость: щёлкните поверхность на виде; Esc — неуказанная шероховатость в углу листа")));
            Btn("dr_gdt_frame_but.svg", "Допуск формы и расположения: щелчок по контуру", () => StartPick("gdt", L("Допуск: щёлкните поверхность на виде")));
            Btn("dr_datum_but.svg", "База: щелчок по контуру", () => StartPick("datum", L("База: щёлкните поверхность на виде")));
            Toggle("dr_gdt_toggle_but.svg", "Допуски формы и расположения вкл/выкл", () => Change(() => Drawing!.ShowGdt = !Drawing.ShowGdt), () => Drawing!.ShowGdt);
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
            // k163 (замечание 05.11, 09:22, п. 3): проверка полноты размеров — значок «внимание» или «годно», подсказка — что не
            // указано; щелчок — показать на листе элементы детали без размеров
            _checkBtn = Toggle("state_warning_ico.svg", "Проверка размеров чертежа", () => { ShowMissing = !ShowMissing; UpdateOverlay(); }, () => ShowMissing);
            InitCheckList(_checkBtn);
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
            // k168 (замечание 05.11, 16:17, п. 13): линия — двумя щелчками (в окне шаблона — как прежде, сразу)
            Btn("tpl_line_but.svg", "Линия шаблона", () =>
            {
                if (Kind == Mode.Drawing) { StartPick("tline1", L("Линия шаблона: щёлкните первую точку")); return; }
                Change(() =>
                {
                    var (x0, y0, x1, _) = SheetLayout.FrameRect(Template);
                    Template.Items.Add(new TemplateItem { Kind = "line", X = x0 + 10, Y = y0 + 80, X2 = x1 - 10, Y2 = y0 + 80 });
                });
            });
            // k168 (п. 10): переключатель видимости таблицы (нет таблицы — новая, у правого верхнего угла рамки)
            Toggle("tpl_table_but.svg", "Таблица на листе", () => Change(() =>
            {
                var tables = Template.Items.Where(i => i.Kind == "table").ToList();
                if (tables.Count == 0 || TableToggleOff)
                {
                    var (_, _, x1, y1) = SheetLayout.FrameRect(Template);
                    bool en = Template.Lang == "en";
                    Template.Items.Add(new TemplateItem { Kind = "table", X = x1 - 130, Y = y1, Height = 8, Rows = 4, Columns = new() { 20, 70, 40 }, Snap = "tr", Cells = new() { new() { en ? "Rev." : "Изм.", en ? "Description" : "Содержание", en ? "Date" : "Дата" } } });
                    return;
                }
                bool show = tables.All(t => t.Hidden);
                foreach (var t in tables) t.Hidden = !show;
            }), () => Template.Items.Any(i => i.Kind == "table" && !i.Hidden));
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
        Btn("dr_sheet_ico.svg", "Следующий лист", () => { if (Kind == Mode.Drawing && Sheets.Count > 1) { SwitchSheet((SheetIndex + 1) % Sheets.Count); return; } _page = Pages.Count == 0 ? 0 : (_page + 1) % Pages.Count; Refresh(); });
        // k169 (п. 15): новый лист того же формата, что и первый
        if (Kind == Mode.Drawing) Btn("dr_add_sheet_but.svg", "Добавить лист (формат первого листа)", () => AddSheet());
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
    static readonly bool NotesWithViewOff = Environment.GetEnvironmentVariable("P3D_NOTESWITHVIEW") == "0";
    Point? _panAt; Point _panStart; bool _panRight;

    /// <summary>
    /// k161 (п. 12): меню размера — стрелки внутрь / наружу / по месту, радиус ↔ диаметр, текст к середине, место — заново,
    /// удалить; меню обозначения — удалить.
    /// </summary>
    public List<(string Text, Action Run)> ContextItems(double x, double y)
    {
        var d = Drawing!;
        var hit = d.HitAt(x, y);
        // k168 (п. 11): строка и столбец таблицы, линия и таблица шаблона
        if (hit == null && TemplateAt(x, y) is { } tpc) { _sel = tpc.It; UpdateOverlay(); return TableMenu(tpc.It, tpc.Part, tpc.Row, tpc.Col); }
        var items = new List<(string, Action)>();
        if (hit is DrawingDim dm)
        {
            _sel = dm; UpdateOverlay();
            items.Add((L("Стрелки внутри"), () => Change(() => dm.Arrows = 1)));
            items.Add((L("Стрелки снаружи"), () => Change(() => dm.Arrows = 2)));
            items.Add((L("Стрелки по месту"), () => Change(() => dm.Arrows = 0)));
            if (dm.Kind is "rad" or "dia") items.Add((dm.Kind == "rad" ? L("Диаметр вместо радиуса") : L("Радиус вместо диаметра"), () => Change(() => dm.Kind = dm.Kind == "rad" ? "dia" : "rad")));
            // k170 (замечание 05.11, 18:34, п. 6.2): угловой — внутренний ↔ внешний, смежный
            if (dm.Kind == "ang")
            {
                items.Add((dm.Value > 180 ? L("Внутренний угол") : L("Внешний угол (360° − угол)"), () => Change(() => DrawingDocument.AngleOuter(dm))));
                items.Add((L("Смежный угол (180° − угол)"), () => Change(() => DrawingDocument.AngleAdjacent(dm))));
            }
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
        _canvas.PointerWheelChanged += (_, e) => { EndCellEdit(true); _canvas.ZoomAt(e.GetPosition(_canvas), Math.Pow(1.2, e.Delta.Y)); e.Handled = true; };
        _canvas.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(_canvas);
            _canvas.Focus();
            AddModifier = (e.KeyModifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift | Avalonia.Input.KeyModifiers.Meta)) != 0;
            // k166 (п. 11): средняя кнопка в режиме «Размер (авто)» — поставить размер
            if (pt.Properties.IsMiddleButtonPressed && _pick == "auto") { FinishAuto(); e.Handled = true; return; }
            if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed) { _panAt = pt.Position; _panStart = pt.Position; _panRight = pt.Properties.IsRightButtonPressed; e.Pointer.Capture(_canvas); e.Handled = true; return; }
            if (!pt.Properties.IsLeftButtonPressed || Kind != Mode.Drawing) return;
            var (x, y) = _canvas.ToSheet(pt.Position);
            if (e.ClickCount >= 2 && _pick == null) { DoubleClick(x, y); e.Handled = true; return; }
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
            if (e.Key == Avalonia.Input.Key.Enter && _pick == "auto") { FinishAuto(); e.Handled = true; return; }
            if (e.Key != Avalonia.Input.Key.Escape) return;
            if (_pick == null) { _sel = null; _elSel.Clear(); UpdateOverlay(); return; }
            // Esc в режиме шероховатости — неуказанная шероховатость в углу листа
            if (_pick == "rough") Change(() => { Drawing!.Notes.RemoveAll(n => n.Kind == "rough" && !n.HasTarget); Drawing.Notes.Add(new DrawingNote { Kind = "rough", Value = "Ra 6,3" }); });
            _pick = null; _elSel.Clear(); _status.Text = ""; _hoverSegs = null; UpdateOverlay();
        };
    }

    /// <summary>Нажатие левой кнопки в точке листа (мм): щелчок режима, иначе выбор элемента или вида и начало переноса.</summary>
    public void Down(double x, double y)
    {
        if (_pick != null) { try { PickAt(x, y); } catch (Exception ex) { _status.Text = ex.Message; Core.AppLog.Write("sheet: pick", ex); } _hoverSegs = null; UpdateOverlay(); return; }
        var d = Drawing!;
        _guides.Clear();
        // k161 (п. 5): у выбранной изометрии — кнопка «Домой» и кольцо поворота
        // k168 (замечание 05.11, 16:17, п. 8), k170 (18:34, п. 2): у выбранной изометрии — куб вида окна детали (свой элемент
        // поверх листа, мышь принимает сам). Мутации P3D_VIEWCUBE=0, P3D_SHEETCUBE=0 — кольцо поворота и домик (k161).
        if ((CubeOff || SheetCubeOff) && _sel is DrawingView sv && IsRotatable(sv) && d.Boxes.TryGetValue(sv.Name, out var sb))
        {
            var (hx, hy) = HomeAt(sb);
            if (Math.Abs(x - hx) < 3.5 && Math.Abs(y - hy) < 3.5) { HomeView(sv); return; }
            {
                double rr = RingR(sb), dc = Math.Sqrt((x - sv.X) * (x - sv.X) + (y - sv.Y) * (y - sv.Y));
                if (Math.Abs(dc - rr) < 2.5) { _drag = new RotateDrag(sv); _lastX = x; _lastY = y; _moved = false; _status.Text = L("Поворот вида: ведите мышью"); return; }
            }
        }
        // k161 (п. 10): точку привязки обозначения (база, допуск, шероховатость, позиция) можно перенести
        foreach (var n in d.Notes.Where(n => n.HasTarget && n.View != null && d.Xf.ContainsKey(n.View)))
        {
            var t = d.ToSheet(n.View!, new OpenTK.Mathematics.Vector2d(n.TX, n.TY));
            if (Math.Abs(t.X - x) < 2.5 && Math.Abs(t.Y - y) < 2.5) { _sel = n; _drag = new TargetDrag(n); _lastX = x; _lastY = y; _moved = false; _status.Text = L("Точка привязки: перенос по контуру"); UpdateOverlay(); return; }
        }
        var hitDn = d.HitAt(x, y);
        // k168: элементы шаблона — линия (перенос, концы), таблица (перенос за шапку)
        if (hitDn == null && TemplateAt(x, y) is { } tpd)
        {
            _sel = tpd.It; _lastX = x; _lastY = y; _moved = false; _pendingEl = null; _elSel.Clear();
            _drag = tpd.Part switch { "end0" => new LineEndDrag(tpd.It, 0), "end1" => new LineEndDrag(tpd.It, 1), "line" => tpd.It, "head" => new TableDrag(tpd.It), _ => null };
            if (_drag is TableDrag) _free = (tpd.It.X, tpd.It.Y);
            _status.Text = DescribeTpl(tpd.It, tpd.Part, tpd.Row, tpd.Col);
            UpdateOverlay(); return;
        }
        // k166 (п. 10): щелчок по элементу детали (без переноса) — выбор элемента; перенос мышью — по-прежнему перенос вида
        _pendingEl = hitDn == null ? ElementUnder(x, y) : null;
        if (_pendingEl == null && !AddModifier && _elSel.Count > 0) _elSel.Clear();
        _sel = hitDn ?? (object?)(d.ViewAt(x, y) is { } vn ? d.Views.FirstOrDefault(v => v.Name == vn) : null);
        _drag = _sel; _lastX = x; _lastY = y; _moved = false;
        if (_sel is DrawingView dv0) { _free = (dv0.X, dv0.Y); if (IsRotatable(dv0)) PlaceCube(dv0); }
        _status.Text = _sel switch
        {
            DrawingDim dm => L("Размер {0}: перенос мышью, Delete — удалить", d.CaptionOf(dm)),
            DrawingNote => L("Обозначение: перенос мышью, Delete — удалить"),
            CentreMark => L("Номер центра: перенос мышью"),
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
                // k165 (п. 2): подпись центра «℄N» — перенос мышью (смещение выноски от центра)
                case CentreMark cm: cm.DX += dx; cm.DY += dy; cm.Placed = true; break;
                case TargetDrag td when td.Note.View is { } tv && _scene != null && _scene.Lines.ContainsKey(tv):
                    {
                        var q = Snap(tv, d.ToView(tv, x, y), 5, false);
                        td.Note.TX = q.X; td.Note.TY = q.Y;
                        break;
                    }
                case RotateDrag rd: RotateView(rd.View, dx, dy, coarse: true); break;
                case LineEndDrag le: if (le.End == 0) { le.It.X += dx; le.It.Y += dy; } else { le.It.X2 += dx; le.It.Y2 += dy; } break;
                case TemplateItem ti when ti.Kind == "line": ti.X += dx; ti.Y += dy; ti.X2 += dx; ti.Y2 += dy; break;
                case TableDrag td:
                    {
                        _free = (_free.X + dx, _free.Y + dy);
                        var (nx, ny, code) = SnapTable(td.It, _free.X, _free.Y);
                        td.It.X = nx; td.It.Y = ny; td.It.Snap = code;
                        break;
                    }
                case DrawingView v:
                    {
                        // k161 (п. 6): свободный перенос с притяжением к центрам других видов (линии-подсказки)
                        _free = (_free.X + dx, _free.Y + dy);
                        double ox = v.X, oy = v.Y;
                        v.X = _free.X; v.Y = _free.Y;
                        _guides = d.SnapView(v.Name);
                        // k165 (замечание 05.11, 13:27, п. 6): обозначения вида (допуски, базы, шероховатость, позиции) — вместе с
                        // видом; прежде у изометрии рамки допусков оставались на месте. Мутация P3D_NOTESWITHVIEW=0.
                        if (!NotesWithViewOff)
                            foreach (var n in d.Notes.Where(n => n.View == v.Name)) { n.X += v.X - ox; n.Y += v.Y - oy; }
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
        if (_drag is DrawingView mv && _moved && IsRotatable(mv)) PlaceCube(mv);
        else if (_pendingEl is { } pe)
        {
            if (!AddModifier || _elSel.Count > 0 && _elSel[0].View != pe.View) { bool again = _elSel.Count == 1 && SameEl(_elSel[0].El, pe.El); _elSel.Clear(); if (!again) _elSel.Add(pe); }
            else ToggleEl(pe);
            _sel = null;
            _status.Text = _elSel.Count == 0 ? "" : L("Выбрано элементов: {0} — «Размер (авто)» поставит размер; Ctrl или Shift — ещё", _elSel.Count);
        }
        _pendingEl = null;
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
        // k162: при ведении мышью — быстрый вид (рёбра без скрытия), точный — после отпускания (Up)
        if (_input != null) { if (coarse) _scene!.RebuildFast(d, v.Name, _input.Bodies); else _scene!.Rebuild(d, v.Name, _input.Bodies); }
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
            case TemplateItem ti: Change(() => Template.Items.Remove(ti)); break;
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

    string ViewLabel(string name) { var v = Drawing?.Views.FirstOrDefault(x => x.Name == name); return v == null ? name : v.IsSection ? $"{v.Label}-{v.Label}" : v.Label.Length > 0 ? v.Label : L(ViewTitle(name)); }

    /// <summary>k167 (замечание 05.11, 16:17, п. 9): имя элемента листа для строки состояния.</summary>
    public string Describe(object? o) => o switch
    {
        DrawingDim dm => L("Размер {0} ({1})", Drawing!.CaptionOf(dm), ViewLabel(dm.View)),
        DrawingNote n => n.Kind switch { "rough" => L("Шероховатость {0}", n.Value), "gdt" => L("Допуск формы {0}", n.Value), "datum" => L("База {0}", n.Text), "balloon" => L("Позиция {0}", n.Position), "holes" => L("Таблица отверстий"), _ => L("Текст") },
        DrawingView v => L("Вид «{0}»", ViewLabel(v.Name)),
        CentreMark cm => Drawing!.CenterNos.FirstOrDefault(c => c.View == cm.View && Math.Abs(c.PV.X - cm.CX) < 1e-9 && Math.Abs(c.PV.Y - cm.CY) < 1e-9) is { No: > 0 } cn ? L("Номер центра ℄{0}", cn.No) : L("Номер центра"),
        _ => "",
    };
    string DescribeEl(string view, PickEl e) => e.Kind switch
    {
        'C' => (e.Full ? L("Окружность ⌀{0}", Fmt(2 * e.R)) : L("Дуга R{0}", Fmt(e.R))) + $" ({ViewLabel(view)})",
        'L' => L(e.What) + $" {Fmt((e.B - e.A).Length)} ({ViewLabel(view)})",
        _ => L(e.What) + $" ({Fmt(e.P.X)}; {Fmt(e.P.Y)}) ({ViewLabel(view)})",
    };
    static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

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
    /// <summary>k166 (п. 8): радиус кружка вершины — 4 точки экрана (мм листа), прежде 1.2 мм листа при любом увеличении.</summary>
    double VertexR => Math.Clamp(4 / Math.Max(1e-6, _canvas.Zoom), 0.1, 1.2);

    List<(double, double, double, double)> Ring(double x, double y, double r)
    {
        var res = new List<(double, double, double, double)>();
        // k166 (замечание 05.11, 15:03, п. 7): окружность подсветки — гладкая: отрезок не длиннее 2 точек экрана (прежде 16
        // отрезков на любую окружность — гранёная)
        int nseg = Math.Clamp((int)Math.Ceiling(2 * Math.PI * r * _canvas.Zoom / 2), 24, 1440);
        for (int k = 0; k < nseg; k++) { double a0 = k * 2 * Math.PI / nseg, a1 = (k + 1) * 2 * Math.PI / nseg; res.Add((x + r * Math.Cos(a0), y + r * Math.Sin(a0), x + r * Math.Cos(a1), y + r * Math.Sin(a1))); }
        return res;
    }

    /// <summary>
    /// Подсветка под курсором: в режиме щелчка — цель привязки (точка размера, окружность, линия контура, отрезок, деталь,
    /// место разреза), иначе — размер или обозначение, иначе — габарит вида.
    /// </summary>
    public void Hover(double x, double y)
    {
        var d = Drawing!;
        _hoverSegs = null; _hover = null; _hoverTpl = null;
        _hoverEl = null;
        if (_pick != null) _hoverSegs = PickPreview(x, y);
        else
        {
            _hover = d.HitAt(x, y);
            _hoverTpl = _hover == null ? TemplateAt(x, y) : null;
            // k166 (замечание 05.11, 15:03, п. 10): элемент детали под курсором — подсветка (щелчок — выбор, с Ctrl или Shift — ещё)
            if (_hover == null && _hoverTpl == null && ElementUnder(x, y) is { } he) _hoverEl = (he.View, he.El);
            // вид под курсором — и при подсветке элемента детали (габарит вида тогда не рисуется)
            if (_hover == null && _hoverTpl == null && d.ViewAt(x, y) is { } vn) _hover = d.Views.FirstOrDefault(v => v.Name == vn);
            // k167 (п. 9): в строке состояния — что под курсором, иначе — что выбрано
            string hs = _hoverTpl is { } ht ? DescribeTpl(ht.It, ht.Part, ht.Row, ht.Col) : _hoverEl is { } he2 ? DescribeEl(he2.View, he2.El) : Describe(_hover);
            if (hs.Length == 0) hs = SelectionText();
            // k170 (п. 7.4): курсор на красной линии проверки полноты — подсказка: какого размера не хватает
            var mi = MissingAt(x, y);
            if (mi != null) hs = MissingTip(mi);
            ShowMissingTip(mi);
            if (hs.Length > 0) _status.Text = hs;
        }
        UpdateOverlay();
    }

    string SelectionText()
    {
        if (_elSel.Count == 1) return L("Выбрано: {0}", DescribeEl(_elSel[0].View, _elSel[0].El));
        if (_elSel.Count > 1) return L("Выбрано элементов: {0}", _elSel.Count);
        return _sel != null ? L("Выбрано: {0}", Describe(_sel)) : "";
    }

    (string View, PickEl El, OpenTK.Mathematics.Vector2d Q)? ElementUnder(double x, double y)
    {
        var d = Drawing!;
        if (_scene == null || d.ViewAt(x, y) is not { } vn || vn == "iso" || !d.Xf.ContainsKey(vn) || !_scene.Lines.ContainsKey(vn)) return null;
        var q = d.ToView(vn, x, y);
        return ElementAt(vn, q) is { } el ? (vn, el, q) : null;
    }

    /// <summary>Отрезки подсветки цели щелчка в режиме pick (мм листа); null — цели нет.</summary>
    public List<(double, double, double, double)>? PickPreview(double x, double y)
    {
        var d = Drawing!;
        if (TemplatePreview(x, y) is { } tpv) return tpv;
        var view = d.ViewAt(x, y);
        if (view == null || !d.Xf.ContainsKey(view) || _scene == null || !_scene.Lines.ContainsKey(view)) return null;
        var q = d.ToView(view, x, y); double s = d.Xf[view].S;
        switch (_pick)
        {
            case "dim1" or "dim2":
                {
                    // k164 (замечание 05.11, 10:33, п. 1): подсвечивается элемент детали под курсором — линия контура, окружность
                    // или дуга с центром, осевая — и точка, к которой привяжется размер
                    var (t, what, geo) = SnapTarget(view, q, 2);
                    var ts = d.ToSheet(view, t);
                    var res = Ring(ts.X, ts.Y, VertexR);
                    foreach (var gl in geo) res.AddRange(SheetSegs(view, gl));
                    _status.Text = what.Length > 0 ? L(_pick == "dim1" ? "Размер: первая точка — {0}" : "Размер: вторая точка — {0}", L(what)) : _status.Text;
                    if (_pick == "dim2" && _first is { } f && f.View == view) { var a = d.ToSheet(view, f.P); res.Add((a.X, a.Y, ts.X, ts.Y)); }
                    return res;
                }
            case "auto":
                {
                    var res = new List<(double, double, double, double)>();
                    foreach (var se in _elSel) if (se.View == view) res.AddRange(ElementSegs(view, se.El));
                    if (ElementAt(view, q) is { } el) { res.AddRange(ElementSegs(view, el)); _status.Text = L("Размер (авто): {0}", L(el.What)) + (_elSel.Count > 0 ? " · " + L("выбрано {0}", _elSel.Count) : ""); }
                    return res.Count == 0 ? null : res;
                }
            case "auto1" or "auto2" or "ang1" or "ang2":
                {
                    // k165: подсветка элемента под курсором и уже выбранного первого
                    bool ang = _pick.StartsWith("ang");
                    var res = new List<(double, double, double, double)>();
                    if (_firstEl is { } fe && fe.View == view) res.AddRange(ElementSegs(view, fe.El));
                    if (ElementAt(view, q, linesOnly: ang) is { } el)
                    {
                        res.AddRange(ElementSegs(view, el));
                        _status.Text = L(ang ? "Угол: {0}" : "Размер (авто): {0}", L(el.What));
                    }
                    return res.Count == 0 ? null : res;
                }
            case "dia" or "rad":
                {
                    var c = FindCircle(view, q, _pick);
                    if (c == null || Math.Abs((q - c.Value.C).Length - c.Value.R) * s > 2.5) return null;
                    var cs = d.ToSheet(view, c.Value.C);
                    return Ring(cs.X, cs.Y, c.Value.R * s);
                }
            case "rough" or "gdt" or "datum" or "aux":
                {
                    var seg = NearestSegment(view, q, 2.5 / s);
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

    readonly Dictionary<DrawingDim, Panel> _dimRows = new(ReferenceEqualityComparer.Instance);
    DrawingDim? _listHover;
    /// <summary>Подсвеченная строка списка размеров (для проверок): размер под курсором на листе или наведённая строка.</summary>
    public DrawingDim? ListHighlighted { get; private set; }
    public DrawingDim? ListHover { get => _listHover; set { _listHover = value; UpdateOverlay(); } }
    static readonly bool DimLinkOff = Environment.GetEnvironmentVariable("P3D_DIMLINK") == "0";
    static readonly IBrush RowHover = new SolidColorBrush(Color.FromArgb(70, 30, 140, 255)), RowSel = new SolidColorBrush(Color.FromArgb(60, 255, 120, 0));
    void UpdateDimRows()
    {
        ListHighlighted = DimLinkOff ? null : _hover as DrawingDim ?? _listHover;
        foreach (var (dm, row) in _dimRows)
            row.Background = !DimLinkOff && ReferenceEquals(dm, ListHighlighted) ? RowHover : ReferenceEquals(dm, _sel) ? RowSel : Brushes.Transparent;
    }

    void UpdateOverlay()
    {
        UpdateToggles();
        UpdateCube();
        UpdateDimRows();
        _canvas.Overlay.Clear();
        if (Kind == Mode.Drawing && Drawing != null)
        {
            if (_hover != null && _hover != _sel && !(_hover is DrawingView && _hoverEl != null)) _canvas.Overlay.Add((SegsOf(_hover), HoverColor, _hover is DrawingView ? 1.2f : 3f));
            if (_sel != null) _canvas.Overlay.Add((SegsOf(_sel), SelColor, _sel is DrawingView ? 1.6f : 3f));
            if (!DimLinkOff && _listHover is { } lh && lh != _sel && Drawing.Dims.Contains(lh)) _canvas.Overlay.Add((SegsOf(lh), HoverColor, 3f));
            if (_hoverSegs != null) _canvas.Overlay.Add((_hoverSegs, TargetColor, 3f));
            if (_pick == null)
            {
                foreach (var se in _elSel) if (Drawing.Xf.ContainsKey(se.View)) _canvas.Overlay.Add((ElementSegs(se.View, se.El), SelColor, 3f));
                if (_hoverEl is { } hel && Drawing.Xf.ContainsKey(hel.View)) _canvas.Overlay.Add((ElementSegs(hel.View, hel.El), HoverColor, 3f));
            }
            MissingOverlay();
            if (_guides.Count > 0) _canvas.Overlay.Add((_guides.Select(gl => { var (x1, y1, x2, y2) = gl; return x1 == x2 ? (x1, y1 - 30, x2, y2 + 30) : (x1 - 30, y1, x2 + 30, y2); }).ToList(), TargetColor, 1.2f));
            if (_hoverTpl is { } htp) _canvas.Overlay.Add((TemplateSegs(htp.It, htp.Part, htp.Row, htp.Col), HoverColor, 2.5f));
            if (_sel is TemplateItem sti) _canvas.Overlay.Add((TemplateSegs(sti, sti.Kind == "line" ? "line" : "table", -1, -1), SelColor, 2.5f));
            if ((CubeOff || SheetCubeOff) && _sel is DrawingView gv && IsRotatable(gv) && Drawing.Boxes.TryGetValue(gv.Name, out var gb))
            {
                // k161: кольцо поворота и кнопка «Домой» (домик); k170 — куб вида окна детали (UpdateCube)
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
    /// <summary>k168: двойной щелчок: ячейка таблицы — правка на листе, поле надписи — фокус в панели.</summary>
    public void DoubleClick(double x, double y)
    {
        if (TemplateAt(x, y) is { Part: "cell" or "head" } tc) { _drag = null; BeginCellEdit(tc.It, tc.Row, tc.Col); return; }
        FocusFieldAt(x, y);
    }

    public string? FocusFieldAt(double x, double y)
    {
        if (Pages.Count == 0) return null;
        var t = Pages[_page].Texts.Where(t => t.Field != null).OrderBy(t => Math.Abs(t.X - x) + Math.Abs(t.Y + t.Height / 2 - y)).FirstOrDefault();
        if (t == null || Math.Abs(t.X - x) > 25 || Math.Abs(t.Y + t.Height / 2 - y) > 6) return null;
        // k167 (замечание 05.11, 16:17, п. 4): фокус — и после перестроения панели. Прежде после ввода значения панель
        // перестраивалась уже после двойного щелчка: фокус оставался у удалённого поля, повторный двойной щелчок не начинал правку.
        _pendingFocus = t.Field;
        FocusPending();
        Avalonia.Threading.Dispatcher.UIThread.Post(FocusPending, Avalonia.Threading.DispatcherPriority.Background);
        _status.Text = L("Поле надписи: {0}", t.Field!);
        return t.Field;
    }
    string? _pendingFocus;
    void FocusPending()
    {
        if (_pendingFocus is not { } f || !_fieldControls.TryGetValue(f, out var c)) return;
        for (var p = c.Parent; p != null; p = p.Parent) if (p is Expander ex) ex.IsExpanded = true;
        c.Focus(); c.BringIntoView();
        if (c is TextBox tb) tb.SelectAll();
        if (c.IsFocused) _pendingFocus = null;
    }
    readonly Dictionary<string, Control> _fieldControls = new();
    /// <summary>Поле основной надписи в панели (для проверок).</summary>
    public Control? TitleField(string field) => _fieldControls.GetValueOrDefault(field);

    // ------------------------------------------------------------------ щелчки по виду: размеры, обозначения, разрезы

    string? _pick; (string View, OpenTK.Mathematics.Vector2d P)? _first;
    /// <summary>k165: первый элемент размера (авто) и углового: вид, элемент, точка щелчка (вид).</summary>
    (string View, PickEl El, OpenTK.Mathematics.Vector2d Q)? _firstEl;

    /// <summary>
    /// k165: элемент вида под курсором для размера (авто) и углового: точка (конец линии, центр), окружность или дуга,
    /// прямая линия контура или осевая. Точки и окружности — в пределах 1.5 мм листа, линии — 4 мм.
    /// </summary>
    public readonly record struct PickEl(char Kind, OpenTK.Mathematics.Vector2d P, OpenTK.Mathematics.Vector2d A, OpenTK.Mathematics.Vector2d B, double R, bool Full, string What);

    public PickEl? ElementAt(string view, OpenTK.Mathematics.Vector2d q, bool linesOnly = false)
    {
        double s = Drawing!.Xf[view].S;
        static OpenTK.Mathematics.Vector2d V(double x, double y) => new(x, y);
        // k166 (замечание 05.11, 15:03, п. 6): радиусы нацеливания — вдвое меньше (точки и окружности 0.75 мм листа, линии 2 мм):
        // мелкий элемент между двумя крупными выбирается
        PickEl? best = null; double bd = 0.75 / s;
        if (!linesOnly)
        {
            // шов полной окружности (концы рёбер-половинок) — не точка: щелчок по окружности — окружность
            var fulls = (_scene!.Arcs.GetValueOrDefault(view) ?? new()).Where(a => a.Full).Select(a => (a.Center, a.Radius))
                .Concat((_scene.Axes.GetValueOrDefault(view) ?? new()).Where(a => a.Cross).Select(a => (a.Center, a.Radius))).ToList();
            bool Seam(OpenTK.Mathematics.Vector2d pt) => fulls.Any(c => Math.Abs((pt - c.Center).Length - c.Radius) < 1e-6 * Math.Max(1, c.Radius));
            foreach (var l in _scene!.Lines[view])
                if (l.Visible && l.Points.Count >= 2)
                    foreach (var pt in new[] { l.Points[0], l.Points[^1] })
                        if ((pt - q).Length < bd && !Seam(pt)) { bd = (pt - q).Length; best = new PickEl('P', pt, pt, pt, 0, false, "конец линии"); }
            foreach (var a in _scene.Arcs.GetValueOrDefault(view) ?? new())
                if ((a.Center - q).Length < bd) { bd = (a.Center - q).Length; best = new PickEl('P', a.Center, a.Center, a.Center, 0, false, a.Full ? "центр окружности" : "центр дуги"); }
            foreach (var a in _scene.Axes.GetValueOrDefault(view) ?? new())
                if (a.Cross && (a.Center - q).Length < bd) { bd = (a.Center - q).Length; best = new PickEl('P', a.Center, a.Center, a.Center, 0, false, "центр окружности"); }
            if (best != null) return best;
            bd = 0.75 / s;
            foreach (var a in _scene.Arcs.GetValueOrDefault(view) ?? new())
            {
                double dd = Math.Abs((a.Center - q).Length - a.Radius);
                if (dd < bd) { bd = dd; best = new PickEl('C', a.Center, a.Center, a.Center, a.Radius, a.Full, a.Full ? "окружность" : "дуга"); }
            }
            foreach (var a in _scene.Axes.GetValueOrDefault(view) ?? new())
            {
                double dd = Math.Abs((a.Center - q).Length - a.Radius);
                if (a.Cross && dd < bd) { bd = dd; best = new PickEl('C', a.Center, a.Center, a.Center, a.Radius, true, "окружность"); }
            }
            if (best != null) return best;
        }
        // прямые линии контура (ломаная по прямой) и осевые
        bd = 2 / s;
        static double SegD(OpenTK.Mathematics.Vector2d q, OpenTK.Mathematics.Vector2d a, OpenTK.Mathematics.Vector2d b)
        {
            var ab = b - a; double t = Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - a, ab) / Math.Max(1e-18, ab.LengthSquared), 0, 1);
            return (a + ab * t - q).Length;
        }
        foreach (var l in _scene!.Lines[view])
        {
            if (!l.Visible || l.Points.Count < 2) continue;
            var a = l.Points[0]; var b = l.Points[^1]; var ab = b - a; double len = ab.Length;
            if (len < 1e-9) continue;
            var n = new OpenTK.Mathematics.Vector2d(-ab.Y, ab.X) / len;
            if (l.Points.Any(p => Math.Abs(OpenTK.Mathematics.Vector2d.Dot(p - a, n)) > 1e-6 * Math.Max(1, len))) continue;   // не прямая
            double dd = SegD(q, a, b);
            if (dd < bd) { bd = dd; best = new PickEl('L', a, a, b, 0, false, "линия"); }
        }
        foreach (var a in _scene.Axes.GetValueOrDefault(view) ?? new())
        {
            var lines = a.Cross
                ? new[] { (A: a.Center - V(a.Radius + 3 / s, 0), B: a.Center + V(a.Radius + 3 / s, 0)), (A: a.Center - V(0, a.Radius + 3 / s), B: a.Center + V(0, a.Radius + 3 / s)) }
                : new[] { (A: a.Center + a.Dir * (a.Lo - 3 / s), B: a.Center + a.Dir * (a.Hi + 3 / s)) };
            foreach (var (A, B) in lines) { double dd = SegD(q, A, B) + 0.25 / s; if (dd < bd) { bd = dd; best = new PickEl('L', A, A, B, 0, false, "осевая линия"); } }
        }
        return best;
    }

    List<(double, double, double, double)> ElementSegs(string view, PickEl e)
    {
        var d = Drawing!;
        double s = d.Xf[view].S;
        switch (e.Kind)
        {
            case 'P': { var t = d.ToSheet(view, e.P); return Ring(t.X, t.Y, VertexR); }
            case 'C': { var t = d.ToSheet(view, e.P); var r = Ring(t.X, t.Y, e.R * s); r.AddRange(Ring(t.X, t.Y, VertexR * 0.6)); return r; }
            default: return SheetSegs(view, new[] { e.A, e.B });
        }
    }

    /// <summary>
    /// k165: размер по двум элементам. Две параллельные линии — расстояние между ними (размерная линия поперёк, выносные —
    /// вдоль линий; у линий по осям вида — горизонтальный или вертикальный); точка и линия — расстояние до линии; две линии
    /// под углом — угловой размер; точки — как «Линейный размер»; одна и та же линия — её длина.
    /// </summary>
    DrawingDim? AutoDim(string view, PickEl e1, OpenTK.Mathematics.Vector2d q1, PickEl e2, OpenTK.Mathematics.Vector2d q2, bool angleOnly)
    {
        var d = Drawing!;
        var mid = d.Xf[view].Mid; double s = d.Xf[view].S;
        OpenTK.Mathematics.Vector2d Pt(PickEl e) => e.P;
        DrawingDim Linear(OpenTK.Mathematics.Vector2d a, OpenTK.Mathematics.Vector2d b, OpenTK.Mathematics.Vector2d? along = null)
        {
            var ab = b - a;
            double dx = Math.Abs(ab.X), dy = Math.Abs(ab.Y);
            string kind = dx > 1e3 * dy ? "h" : dy > 1e3 * dx ? "v" : along == null && dx > 3 * dy ? "h" : along == null && dy > 3 * dx ? "v" : "al";
            double off = kind == "h" ? ((a.Y + b.Y) / 2 >= mid.Y ? 10 : -10) : kind == "v" ? ((a.X + b.X) / 2 >= mid.X ? -10 : 10) : 8;
            return new DrawingDim { View = view, Kind = kind, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = off };
        }
        static OpenTK.Mathematics.Vector2d Foot(OpenTK.Mathematics.Vector2d p, OpenTK.Mathematics.Vector2d a, OpenTK.Mathematics.Vector2d b)
        { var u = (b - a).Normalized(); return a + u * OpenTK.Mathematics.Vector2d.Dot(p - a, u); }
        static OpenTK.Mathematics.Vector2d Near(OpenTK.Mathematics.Vector2d q, OpenTK.Mathematics.Vector2d a, OpenTK.Mathematics.Vector2d b)
        { var ab = b - a; double t = Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - a, ab) / Math.Max(1e-18, ab.LengthSquared), 0, 1); return a + ab * t; }
        if (e1.Kind == 'L' && e2.Kind == 'L')
        {
            var u1 = (e1.B - e1.A).Normalized(); var u2 = (e2.B - e2.A).Normalized();
            double cr = u1.X * u2.Y - u1.Y * u2.X;
            bool same = (e1.A - e2.A).Length < 1e-9 && (e1.B - e2.B).Length < 1e-9;
            if (same && !angleOnly) return Linear(e1.A, e1.B, e1.B - e1.A);
            if (Math.Abs(cr) < Math.Sin(0.5 * Math.PI / 180))
            {
                if (angleOnly) return null;
                // параллельные: от точки первой линии у щелчка — перпендикуляр на вторую
                var p = Near(q1, e1.A, e1.B); var f = Foot(p, e2.A, e2.B);
                return Linear(p, f, f - p);
            }
            // угол: вершина — пересечение; стороны — к щелчкам
            var w = e2.A - e1.A;
            double t1 = (w.X * u2.Y - w.Y * u2.X) / cr;
            var vx = e1.A + u1 * t1;
            OpenTK.Mathematics.Vector2d Side(OpenTK.Mathematics.Vector2d u, OpenTK.Mathematics.Vector2d q, PickEl e)
            {
                var r = q - vx; if (r.Length < 1e-9) r = (e.A + e.B) / 2 - vx;
                return OpenTK.Mathematics.Vector2d.Dot(r, u) >= 0 ? u : -u;
            }
            var s1 = Side(u1, q1, e1); var s2 = Side(u2, q2, e2);
            double a1 = Math.Atan2(s1.Y, s1.X) * 180 / Math.PI, a2 = Math.Atan2(s2.Y, s2.X) * 180 / Math.PI;
            double sweep = ((a2 - a1) % 360 + 360) % 360;
            if (sweep > 180) { (a1, a2) = (a2, a1); sweep = 360 - sweep; }
            var qm = d.ToSheet(view, (q1 + q2) / 2); var vs = d.ToSheet(view, vx);
            double rad = Math.Clamp(Math.Sqrt((qm.X - vs.X) * (qm.X - vs.X) + (qm.Y - vs.Y) * (qm.Y - vs.Y)), 8, 60);
            return new DrawingDim { View = view, Kind = "ang", X1 = vx.X, Y1 = vx.Y, X2 = a1, Y2 = a1 + sweep, Offset = rad };
        }
        if (angleOnly) return null;
        if (e1.Kind == 'L' && e2.Kind != 'L') return Linear(Pt(e2), Foot(Pt(e2), e1.A, e1.B), Foot(Pt(e2), e1.A, e1.B) - Pt(e2));
        if (e2.Kind == 'L' && e1.Kind != 'L') return Linear(Pt(e1), Foot(Pt(e1), e2.A, e2.B), Foot(Pt(e1), e2.A, e2.B) - Pt(e1));
        return Linear(Pt(e1), Pt(e2));
    }

    public string StatusText => _status.Text ?? "";
    public void StartPick(string mode, string hint) { EnsureScene(); _pick = mode == "auto1" ? "auto" : mode; _first = null; _firstEl = null; if (_pick == "auto") _elSel.Clear(); _status.Text = hint; }

    // ------------------------------------------------------------------ k166: выбор элементов детали и «Размер (авто)»

    readonly List<(string View, PickEl El, OpenTK.Mathematics.Vector2d Q)> _elSel = new();
    (string View, PickEl El)? _hoverEl;
    (string View, PickEl El, OpenTK.Mathematics.Vector2d Q)? _pendingEl;
    /// <summary>k166: щелчок с Ctrl или Shift — добавить элемент к выбранным.</summary>
    public bool AddModifier { get; set; }
    /// <summary>k166: выбранные элементы детали (вид, элемент, точка щелчка).</summary>
    public IReadOnlyList<(string View, PickEl El, OpenTK.Mathematics.Vector2d Q)> SelectedElements => _elSel;
    public (string View, PickEl El)? HoveredElement => _hoverEl;

    static bool SameEl(PickEl a, PickEl b) => a.Kind == b.Kind && (a.A - b.A).Length < 1e-9 && (a.B - b.B).Length < 1e-9 && (a.P - b.P).Length < 1e-9;
    void ToggleEl((string View, PickEl El, OpenTK.Mathematics.Vector2d Q) e)
    {
        int i = _elSel.FindIndex(x => x.View == e.View && SameEl(x.El, e.El));
        if (i >= 0) _elSel.RemoveAt(i); else _elSel.Add(e);
    }

    /// <summary>
    /// k166 (п. 10): кнопка «Размер (авто)»: выбранные элементы — размер сразу; ничего не выбрано — режим выбора элементов
    /// (п. 11): щелчки (Ctrl или Shift — так же) на одном виде, Enter или средняя кнопка — поставить.
    /// </summary>
    public void AutoDimButton()
    {
        if (_elSel.Count > 0) { CreateAutoDims(_elSel.ToList()); _elSel.Clear(); UpdateOverlay(); return; }
        StartPick("auto", L("Размер (авто): щёлкните элементы (Ctrl или Shift — ещё), Enter или средняя кнопка — поставить размер"));
    }

    /// <summary>k166 (п. 11): завершить выбор элементов в режиме «Размер (авто)» — поставить размер или цепочку.</summary>
    public void FinishAuto()
    {
        if (_pick != "auto") return;
        var list = _elSel.ToList();
        _pick = null; _elSel.Clear(); _hoverSegs = null;
        if (list.Count > 0) CreateAutoDims(list); else _status.Text = "";
        UpdateOverlay();
    }

    /// <summary>
    /// k166: размеры по элементам: один элемент — диаметр окружности, радиус дуги, длина линии; два — по паре (AutoDim);
    /// три и больше — цепочка по соседним парам, размерные линии цепочки — на одной высоте.
    /// </summary>
    void CreateAutoDims(List<(string View, PickEl El, OpenTK.Mathematics.Vector2d Q)> list)
    {
        var d = Drawing!;
        var view = list[0].View;
        var es = list.Where(e => e.View == view).ToList();
        var made = new List<DrawingDim>();
        if (es.Count == 1)
        {
            var (_, e, q) = es[0];
            if (e.Kind == 'C') made.Add(new DrawingDim { View = view, Kind = e.Full ? "dia" : "rad", X1 = e.P.X, Y1 = e.P.Y, X2 = e.R, Y2 = Math.Atan2(q.Y - e.P.Y, q.X - e.P.X) * 180 / Math.PI });
            else if (e.Kind == 'L' && AutoDim(view, e, q, e, q, false) is { } dl) made.Add(dl);
        }
        else
            for (int i = 0; i + 1 < es.Count; i++)
                if (AutoDim(view, es[i].El, es[i].Q, es[i + 1].El, es[i + 1].Q, false) is { } dm) made.Add(dm);
        if (made.Count == 0) { _status.Text = L("Размер по этим элементам не поставить"); return; }
        Change(() => d.Dims.AddRange(made));
        // цепочка: размерные линии того же вида (горизонтальные, вертикальные) — на высоте первой
        if (made.Count > 1 && made[0].Kind is "h" or "v")
        {
            double L0 = d.DimFrame(made[0]).L;
            foreach (var dm in made.Skip(1).Where(x => x.Kind == made[0].Kind))
            {
                var (_, _, _, _, h1, h2, _) = d.DimFrame(dm);
                double hi = Math.Max(h1, h2), lo = Math.Min(h1, h2);
                dm.FromBox = false;
                dm.Offset = L0 >= hi ? L0 - hi : L0 <= lo ? L0 - lo : dm.Offset;
                dm.Arrange = false;
            }
            Change(() => { });
        }
        _status.Text = made.Count == 1 ? L("Размер {0}", d.CaptionOf(made[0])) : L("Цепочка из {0} размеров", made.Count);
    }
    /// <summary>k164: выход из режима щелчка (как Esc).</summary>
    public void CancelPick() { _pick = null; _first = null; _status.Text = ""; _hoverSegs = null; UpdateOverlay(); }

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

    /// <summary>
    /// k164: цель привязки размера: конец линии контура, центр окружности или дуги, точка осевой линии (размер между осями).
    /// Ближайшая в пределах snapMm мм листа; центры и концы — с запасом 1 мм против точек на осевой. Возвращает точку (вида),
    /// что это и геометрию элемента для подсветки (вид). Ничего рядом — точка курсора.
    /// </summary>
    public (OpenTK.Mathematics.Vector2d P, string What, List<List<OpenTK.Mathematics.Vector2d>> Geo) SnapTarget(string view, OpenTK.Mathematics.Vector2d q, double snapMm)
    {
        double s = Drawing!.Xf[view].S, best = snapMm / s;
        var res = (P: q, What: "", Geo: new List<List<OpenTK.Mathematics.Vector2d>>());
        static OpenTK.Mathematics.Vector2d V(double x, double y) => new(x, y);
        // линия контура: конец (вершина)
        foreach (var l in _scene!.Lines[view])
        {
            if (!l.Visible || l.Points.Count < 2) continue;
            foreach (var pt in new[] { l.Points[0], l.Points[^1] })
            {
                double dd = (pt - q).Length;
                if (dd < best) { best = dd; res = (pt, "конец линии", new() { l.Points }); }
            }
        }
        // центры окружностей и дуг
        foreach (var a in _scene.Arcs.GetValueOrDefault(view) ?? new())
        {
            double dd = (a.Center - q).Length;
            // по самой дуге — тоже центр (щелчок по окружности — размер от её центра)
            double dr = Math.Abs((a.Center - q).Length - a.Radius) + 0.5 / s;
            double dm = Math.Min(dd, dr);
            if (dm < best)
            {
                best = dm;
                int n = a.Full ? 48 : Math.Max(8, (int)(Math.Abs(a.A1 - a.A0) / (2 * Math.PI) * 48));
                double a0 = a.A0, a1 = a.A1; if (!a.Full && a1 < a0) a1 += 2 * Math.PI; if (a.Full) { a0 = 0; a1 = 2 * Math.PI; }
                var arc = Enumerable.Range(0, n + 1).Select(k => a.Center + a.Radius * V(Math.Cos(a0 + (a1 - a0) * k / n), Math.Sin(a0 + (a1 - a0) * k / n))).ToList();
                double hl = 2 / s;
                res = (a.Center, a.Full ? "центр окружности" : "центр дуги", new() { arc, new() { a.Center - V(hl, 0), a.Center + V(hl, 0) }, new() { a.Center - V(0, hl), a.Center + V(0, hl) } });
            }
        }
        foreach (var a in _scene.Axes.GetValueOrDefault(view) ?? new())
        {
            if (a.Cross)
            {
                double dd = (a.Center - q).Length;
                if (dd < best) { best = dd; double hl = a.Radius + 3 / s; res = (a.Center, "центр окружности", new() { new() { a.Center - V(hl, 0), a.Center + V(hl, 0) }, new() { a.Center - V(0, hl), a.Center + V(0, hl) } }); }
            }
        }
        // точка на осевой линии (размер между осями): с запасом 1 мм — центры и концы ближе
        foreach (var a in _scene.Axes.GetValueOrDefault(view) ?? new())
        {
            var lines = a.Cross
                ? new[] { (A: a.Center - V(a.Radius + 3 / s, 0), B: a.Center + V(a.Radius + 3 / s, 0)), (A: a.Center - V(0, a.Radius + 3 / s), B: a.Center + V(0, a.Radius + 3 / s)) }
                : new[] { (A: a.Center + a.Dir * (a.Lo - 3 / s), B: a.Center + a.Dir * (a.Hi + 3 / s)) };
            foreach (var (A, B) in lines)
            {
                var ab = B - A; double t = Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - A, ab) / Math.Max(1e-18, ab.LengthSquared), 0, 1);
                var c = A + ab * t; double dd = (c - q).Length + 0.5 / s;
                if (dd < best) { best = dd; res = (c, "осевая линия", new() { new() { A, B } }); }
            }
        }
        // осевые центров дуг (перекрестие: полудлина — радиус + 3 мм, не больше 15 мм листа, как на листе)
        foreach (var a in _scene.Arcs.GetValueOrDefault(view) ?? new())
        {
            double hl = Math.Min(a.Radius * s + 3, 15) / s;
            foreach (var (A, B) in new[] { (a.Center - V(hl, 0), a.Center + V(hl, 0)), (a.Center - V(0, hl), a.Center + V(0, hl)) })
            {
                var ab = B - A; double t = Math.Clamp(OpenTK.Mathematics.Vector2d.Dot(q - A, ab) / Math.Max(1e-18, ab.LengthSquared), 0, 1);
                var c = A + ab * t; double dd = (c - q).Length + 0.5 / s;
                if (dd < best) { best = dd; res = (c, "осевая линия", new() { new() { A, B } }); }
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
        if (TemplatePick(x, y)) return;
        FreezeScale();
        var view = d.ViewAt(x, y);
        if (view == null || !d.Xf.ContainsKey(view)) { _status.Text = L("Щёлкните на виде"); return; }
        var q = d.ToView(view, x, y);
        double s = d.Xf[view].S;
        string mode = _pick!;
        switch (mode)
        {
            case "dim1":
                _first = (view, SnapTarget(view, q, 2).P); _pick = "dim2"; _status.Text = L("Размер: щёлкните вторую точку"); return;
            case "auto":
                {
                    // k166 (замечание 05.11, 15:03, п. 11): элементы — щелчками (Ctrl или Shift — так же), на одном виде; размер
                    // или цепочка — по Enter или средней кнопке
                    if (ElementAt(view, q) is not { } ea) { _status.Text = L("Элемент не найден — щёлкните ближе к линии, точке или окружности"); return; }
                    if (_elSel.Count > 0 && _elSel[0].View != view) { _status.Text = L("Элементы размера — на одном виде"); return; }
                    ToggleEl((view, ea, q));
                    _status.Text = L("Размер (авто): выбрано {0}; Enter или средняя кнопка — поставить", _elSel.Count);
                    return;
                }
            case "auto1" or "ang1":
                {
                    bool ang = mode == "ang1";
                    if (ElementAt(view, q, linesOnly: ang) is not { } e1) { _status.Text = ang ? L("Линия не найдена — щёлкните ближе к ней") : L("Элемент не найден — щёлкните ближе к линии, точке или окружности"); return; }
                    if (e1.Kind == 'C' && !ang)
                    {
                        // окружность — диаметр, дуга — радиус
                        double am = Math.Atan2(q.Y - e1.P.Y, q.X - e1.P.X) * 180 / Math.PI;
                        Change(() => d.Dims.Add(new DrawingDim { View = view, Kind = e1.Full ? "dia" : "rad", X1 = e1.P.X, Y1 = e1.P.Y, X2 = e1.R, Y2 = am }));
                        _pick = null; return;
                    }
                    _firstEl = (view, e1, q); _pick = ang ? "ang2" : "auto2";
                    _status.Text = ang ? L("Угол: щёлкните вторую линию") : L("Размер (авто): щёлкните второй элемент");
                    return;
                }
            case "auto2" or "ang2":
                {
                    bool ang = mode == "ang2";
                    if (_firstEl is not { } f1 || f1.View != view) { _status.Text = L("Второй элемент — на том же виде"); return; }
                    if (ElementAt(view, q, linesOnly: ang) is not { } e2) { _status.Text = L("Элемент не найден — щёлкните ближе к нему"); return; }
                    var dm = AutoDim(view, f1.El, f1.Q, e2, q, ang);
                    if (dm == null) { _status.Text = L("Линии параллельны — угла нет"); return; }
                    Change(() => d.Dims.Add(dm));
                    _pick = null; _firstEl = null; return;
                }
            case "dim2":
                {
                    if (_first is not { } f || f.View != view) { _status.Text = L("Вторая точка — на том же виде"); return; }
                    var b = SnapTarget(view, q, 2).P;
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
                    if (cands.Count == 0 || Math.Abs((q - cands[0].C).Length - cands[0].R) * s > 2.5) { _status.Text = mode == "dia" ? L("Окружность не найдена — щёлкните ближе к ней") : L("Дуга не найдена — щёлкните ближе к ней"); return; }
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
                    Change(() => { d.AddSection(_scene!, view, dir, depth); });   // k167: строится только новый вид (в фоне)
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
                    Change(() => { d.AddAuxiliary(_scene!, view, sg.A, sg.B); });
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
                    Change(() => { d.AddDetail(view, q, r, ds); });
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
        // k170 (замечание 05.11, 18:34, п. 3): значение применяется, только если изменено (прежде — при каждой потере фокуса, и
        // панель перестраивалась под щелчком по соседнему полю: поле, по которому щёлкнули, пропадало)
        string last = text;
        void Commit() { var t = tb.Text ?? ""; if (t == last && !PropsGuardOff) return; last = t; set(t); }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
        return tb;
    }
    internal static readonly bool PropsGuardOff = Environment.GetEnvironmentVariable("P3D_PROPSGUARD") == "0";

    /// <summary>Курсор ввода — в текстовом поле панели свойств.</summary>
    public bool TextInProps()
    {
        if (FocusManager?.GetFocusedElement() is not Control c) return false;
        for (Control? p = c; p != null; p = p.Parent as Control) if (ReferenceEquals(p, _props)) return c is TextBox || c.FindAncestorOfType<TextBox>() != null;
        return false;
    }
    bool _propsStale;
    /// <summary>Перестроение панели ждало ухода курсора ввода из полей панели (для проверок).</summary>
    public bool PropsStale => _propsStale;

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
        // k170 (замечание 05.11, 18:34, п. 3): пока курсор ввода в текстовом поле панели (поля основной надписи, допуски…),
        // панель не перестраивается: перестроение — когда курсор уйдёт из полей панели. Прежде каждое применённое значение и
        // каждый пересчёт видов перестраивали панель под курсором: поле теряло фокус и набранный текст. Мутация P3D_PROPSGUARD=0.
        if (!PropsGuardOff && _pendingFocus == null && TextInProps()) { _propsStale = true; return; }
        _propsStale = false;
        BuildPropsCore();
        if (_pendingFocus != null) Avalonia.Threading.Dispatcher.UIThread.Post(FocusPending, Avalonia.Threading.DispatcherPriority.Background);
    }

    void BuildPropsCore()
    {
        _props.Children.Clear();
        _fieldControls.Clear(); _dimRows.Clear();
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
                // k168 (замечание 05.11, 16:17, п. 12): язык надписей листа
                string[] langs = { "", "ru", "en" };
                var lang = new ComboBox { ItemsSource = new[] { L("по стандарту ({0})", Template.Lang == "en" ? "English" : "Русский"), "Русский", "English" }, SelectedIndex = Math.Max(0, Array.IndexOf(langs, Template.Language)), MinWidth = 140, Name = "dr_language" };
                lang.SelectionChanged += (_, _) => { var l2 = langs[Math.Max(0, lang.SelectedIndex)]; if (l2 != Template.Language) Change(() => Template.Language = l2); };
                dp.Children.Add(Row(L("Язык чертежа"), lang));
                // k161 (п. 4): толщина основной линии S, ГОСТ 2.303
                double[] sv = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.0, 1.4 };   // k165 (п. 5): и 0,1…0,4 мм
                var lineS = new ComboBox { ItemsSource = sv.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture) + L(" мм")).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(sv, x => Math.Abs(x - d.LineS) < 1e-9)), MinWidth = 140 };
                lineS.SelectionChanged += (_, _) => { double ns = sv[Math.Max(0, lineS.SelectedIndex)]; if (Math.Abs(ns - d.LineS) > 1e-9) Change(() => d.LineS = ns); };
                ToolTip.SetTip(lineS, L("Толщина сплошной основной линии S (ГОСТ 2.303: 0,5…1,4 мм; 0,1…0,4 — тонкие стенки и мелкие элементы); тонкие — S/2"));
                dp.Children.Add(Row(L("Основная линия"), lineS));
                // k164 (п. 5): номера центров «℄N»
                var cnos = new CheckBox { Content = L("Номера центров (℄)"), IsChecked = d.CenterNumbers, Name = "dr_center_numbers" };
                cnos.IsCheckedChanged += (_, _) => { bool nv = cnos.IsChecked == true; if (nv != d.CenterNumbers) Change(() => d.CenterNumbers = nv); };
                ToolTip.SetTip(cnos, L("Нумерация центров окружностей и дуг, к которым относятся размеры: подпись у центра и номер под полкой диаметра или радиуса"));
                dp.Children.Add(cnos);
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
                    // k168: видимость элемента
                    var vis = new CheckBox { IsChecked = !it.Hidden, MinWidth = 0 };
                    vis.IsCheckedChanged += (_, _) => { bool h = vis.IsChecked != true; if (h != it.Hidden) Change(() => it.Hidden = h); };
                    if (it.Kind == "table")
                    {
                        // k168 (замечание 05.11, 16:17, п. 11): название, число строк и столбцов
                        var box = new StackPanel { Spacing = 3 };
                        if (it == _sel) box.Background = new SolidColorBrush(Color.FromArgb(60, 255, 120, 0));
                        var title = Field(it.Title, t => { if (t != it.Title) Change(() => it.Title = t); }, 200); title.Watermark = L("Название таблицы"); title.Name = "dr_table_title";
                        box.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { vis, title, del } });
                        var rows = new NumericUpDown { Minimum = 1, Maximum = 200, Increment = 1, Value = Math.Max(1, it.Rows), FormatString = "0", Width = 110, Name = "dr_table_rows" };
                        rows.ValueChanged += (_, e) => { int n = (int)(e.NewValue ?? 1); if (n != it.Rows) Change(() => { it.NormalizeCells(); while (it.Rows < n) it.InsertRow(it.Rows); while (it.Rows > n && it.Rows > 1) it.DeleteRow(it.Rows - 1); }); };
                        var cols = new NumericUpDown { Minimum = 1, Maximum = 50, Increment = 1, Value = Math.Max(1, it.Columns?.Count ?? 1), FormatString = "0", Width = 110, Name = "dr_table_cols" };
                        cols.ValueChanged += (_, e) => { int n = (int)(e.NewValue ?? 1); if (n != (it.Columns?.Count ?? 1)) Change(() => { it.NormalizeCells(); while (it.Columns!.Count < n) it.InsertColumn(it.Columns.Count); while (it.Columns.Count > n && it.Columns.Count > 1) it.DeleteColumn(it.Columns.Count - 1); }); };
                        box.Children.Add(Row(L("Строк"), rows));
                        box.Children.Add(Row(L("Столбцов"), cols));
                        box.Children.Add(new TextBlock { Text = L("Двойной щелчок по ячейке на листе — правка; левее строки и выше столбца — правый щелчок: добавить, копировать, вставить, удалить"), Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
                        ip.Children.Add(box);
                        continue;
                    }
                    Control body = it.Kind == "text"
                        ? new TextBox { Text = it.Text, AcceptsReturn = true, MinHeight = 44, Width = 230, FontSize = 12 }
                        : new TextBlock { Text = L("Линия {0}, {1} — {2}, {3}", Math.Round(it.X, 1), Math.Round(it.Y, 1), Math.Round(it.X2, 1), Math.Round(it.Y2, 1)), VerticalAlignment = VerticalAlignment.Center };
                    if (body is TextBox tb) tb.LostFocus += (_, _) => Change(() => it.Text = tb.Text ?? "");
                    ip.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { vis, body, del } });
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
                if (v.IsSection) row.Children.Add(Field(v.CutDepth.ToString("0.###", CultureInfo.InvariantCulture), t => { double n = Num(t, v.CutDepth); if (n != v.CutDepth) Change(() => { v.CutDepth = n; DropView(v.Name); }); }, 64));
                else if (v.IsDetail) row.Children.Add(Field(v.DetailR.ToString("0.###", CultureInfo.InvariantCulture), t => { double n = Num(t, v.DetailR); if (n > 0 && n != v.DetailR) Change(() => { v.DetailR = n; DropView(v.Name); }); }, 64));
                if (!v.IsAux) row.Children.Add(new TextBlock { Text = v.IsSection ? L("глуб.") : L("R"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                AddViewControls(row, v);
                row.Children.Add(Del(() => { d.Views.RemoveAll(x => x == v || x.Parent == v.Name); d.Dims.RemoveAll(x => x.View == v.Name); InvalidateViews(); }));
                ep.Children.Add(row);
            }
        }
        if (d.Dims.Count > 0)
        {
            var dp = Section(L("Размеры ({0})", d.Dims.Count));
            // k167 (замечание 05.11, 16:17, п. 1–3): отклонения — два поля (Upper / Lower) с заголовками; поля текста и
            // смещения размерной линии убраны (смещение — мышью, текст — значение модели)
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            head.Children.Add(new TextBlock { Text = "", MinWidth = 24 });
            head.Children.Add(new TextBlock { Text = L("Размер"), MinWidth = 110, Opacity = 0.7, FontSize = 11 });
            head.Children.Add(new TextBlock { Text = L("Верхнее (+)"), Width = 64, Opacity = 0.7, FontSize = 11 });
            head.Children.Add(new TextBlock { Text = L("Нижнее (−)"), Width = 64, Opacity = 0.7, FontSize = 11 });
            dp.Children.Add(head);
            foreach (var dim in d.Dims.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var show = new CheckBox { IsChecked = !dim.Hidden, MinWidth = 0 };
                show.IsCheckedChanged += (_, _) => { bool h = show.IsChecked != true; if (h != dim.Hidden) Change(() => dim.Hidden = h); };
                row.Children.Add(show);
                row.Children.Add(new TextBlock { Text = $"{dim.View}: {d.CaptionOf(dim)}", MinWidth = 110, VerticalAlignment = VerticalAlignment.Center });
                var tup = Field(dim.TolUp, t => { if (t != dim.TolUp) Change(() => { dim.TolUp = t; dim.Tol = ""; }); }, 64); tup.Watermark = "+0,1"; tup.Name = "dr_tol_up";
                var tlo = Field(dim.TolLo, t => { if (t != dim.TolLo) Change(() => { dim.TolLo = t; dim.Tol = ""; }); }, 64); tlo.Watermark = "−0,1"; tlo.Name = "dr_tol_lo";
                ToolTip.SetTip(tup, L("Верхнее отклонение (Upper): +0,015"));
                ToolTip.SetTip(tlo, L("Нижнее отклонение (Lower): 0,010 или −0,010; равные верхнему по величине — «±»"));
                row.Children.Add(tup); row.Children.Add(tlo);
                if (dim.Kind == "ang")
                {
                    var outer = new Button { Content = dim.Value > 180 ? "∠ " + L("внеш.") : "∠ " + L("внутр."), Padding = new Thickness(4, 0), FontSize = 11, Name = "dr_ang_outer" };
                    ToolTip.SetTip(outer, L("Угол: внутренний ↔ внешний (360° − угол); смежный — в меню размера"));
                    outer.Click += (_, _) => Change(() => DrawingDocument.AngleOuter(dim));
                    row.Children.Add(outer);
                }
                row.Children.Add(Del(() => d.Dims.Remove(dim)));
                // k170 (замечание 05.11, 18:34, п. 8): строка списка ↔ размер на листе: курсор над строкой — размер подсвечен на
                // листе, курсор над размером на листе — строка подсвечена; щелчок по строке — размер выбран
                row.Background = Brushes.Transparent;
                row.PointerEntered += (_, _) => { _listHover = dim; UpdateOverlay(); };
                row.PointerExited += (_, _) => { if (_listHover == dim) { _listHover = null; UpdateOverlay(); } };
                row.PointerPressed += (_, e) => { if (e.Source is TextBlock or StackPanel) { _sel = dim; UpdateOverlay(); _status.Text = Describe(dim); } };
                _dimRows[dim] = row;
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

    internal static readonly bool LabelsOff = Environment.GetEnvironmentVariable("P3D_PROPSLABELS") == "0";
    /// <summary>Строка панели «подпись — поле» (для проверок).</summary>
    public static Control PropRow(string label, Control c) => Row(label, c);
    static Control Row(string label, Control c)
    {
        // k170 (замечание 05.11, 18:34, п. 5): столбец подписей — по самой длинной подписи панели (общая ширина у всех строк,
        // 110…170), поля — правее; прежде 110 точек, и «Язык чертежа» уходил под поле. Мутация P3D_PROPSLABELS=0.
        var g = new Grid();
        if (LabelsOff) g.ColumnDefinitions = new ColumnDefinitions("110,*");
        else { g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 110, SharedSizeGroup = "plabel" }); g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); }
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = LabelsOff ? default : new Thickness(0, 0, 8, 0), MaxWidth = LabelsOff ? double.PositiveInfinity : 170, TextWrapping = LabelsOff ? TextWrapping.NoWrap : TextWrapping.Wrap };
        Grid.SetColumn(c, 1); g.Children.Add(t); g.Children.Add(c);
        return g;
    }

    // ------------------------------------------------------------------ лист

    FieldSource FieldValues() => FieldSource.FromPart(FieldPart?.Invoke());

    /// <summary>Источник чертежа: файл модели (деталь или сборка) или открытый документ.</summary>
    DrawingInput Input()
    {
        // открытый документ — его модель (файл может быть старее правок); иначе — файл модели
        // k169: лист с другой моделью — её файл (открытый документ — у листов с моделью листа 1)
        bool ownModel = Drawing != null && _root != null && !ReferenceEquals(Drawing, _root) && Drawing.SourcePath is { Length: > 0 } && Drawing.SourcePath != _root.SourcePath;
        if (!ownModel && DrawingSource?.Invoke() is { } open && open.Bodies.Count > 0) return open;
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
    /// <summary>k167: вид устарел — перестроить только его (в фоне).</summary>
    void DropView(string name) { if (_scene != null) { _scene.Lines.Remove(name); _scene.Shades.Remove(name); } }
    public void InvalidateViews() { _scene = null; _fileInput = _fileInput is { } fi && Drawing?.SourcePath is { } sp && File.Exists(sp) && fi.Stamp != File.GetLastWriteTimeUtc(sp) ? null : _fileInput; }

    /// <summary>Проекции видов: построить, если их нет или набор видов изменился.</summary>
    void EnsureScene()
    {
        var d = Drawing!;
        if (_scene != null && d.Views.All(v => _scene.Lines.ContainsKey(v.Name) || _building.Contains(v.Name))) return;
        // k167 (замечание 05.11, 16:17, п. 5): новый вид (разрез, выносной, вспомогательный, включённый) — строится только он,
        // в фоне, с ходом в строке состояния; прежде — все виды заново, около 10 с без отклика окна
        if (_scene != null && _input != null && !IncrementalOff)
        {
            var missing = d.Views.Where(v => !_scene.Lines.ContainsKey(v.Name) && !_building.Contains(v.Name)).ToList();
            if (missing.All(v => !v.IsDetail || _scene.Lines.ContainsKey(v.Parent ?? "")))
            {
                BuildViewsAsync(missing);
                return;
            }
        }
        // k159: положения видов из файла чертежа сохраняются; место получают только виды с X = Y = 0, и только когда у них
        // есть линии (Layout) — прежде раскладка сбрасывалась при каждом построении после пустого (перенос пользователя терялся)
        _input = Input();
        _partFields = null;
        _projSig = DrawingInput.Signature(_input.Bodies);
        d.ModelUnits = _input.Part?.Units.Length ?? "mm";
        _scene = DrawingScene.Build(d, _input.Bodies, _input.Items, _input.Colors);
        if (_scene.Problems.Count > 0) _status.Text = string.Join("; ", _scene.Problems.Take(2));
    }

    static readonly bool IncrementalOff = Environment.GetEnvironmentVariable("P3D_VIEWSINCR") == "0";

    void BuildViewsAsync(List<DrawingView> views)
    {
        var d = Drawing!; var scene = _scene!; var bodies = _input!.Bodies;
        if (views.Count == 0) return;
        string what = string.Join(", ", views.Select(v => ViewLabel(v.Name)));
        if (SyncViews)
        {
            var part = DrawingScene.BuildSome(d, bodies, views, null);
            scene.Merge(d, part);
            return;
        }
        foreach (var v in views) _building.Add(v.Name);
        _status.Text = L("Строится вид: {0}…", what);
        _progress.IsVisible = true; _progress.Value = 0;
        var names = views.Select(v => v.Name).ToList();
        System.Threading.Tasks.Task.Run(() => DrawingScene.BuildSome(d, bodies, views, (f, stage) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { _progress.Value = f; _status.Text = L("Строится вид: {0} — {1}", what, L(stage)); })))
            .ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var n in names) _building.Remove(n);
                _progress.IsVisible = _building.Count > 0;
                if (t.IsFaulted) { _status.Text = L("Вид не построен: {0}", t.Exception?.GetBaseException().Message ?? ""); Core.AppLog.Write("sheet: view build", t.Exception!); return; }
                if (!ReferenceEquals(_scene, scene)) return;   // сцена перестроена целиком
                scene.Merge(d, t.Result);
                _status.Text = L("Вид построен: {0}", what);
                RefreshNow(); BuildProps();
            }));
    }

    /// <summary>k167: дождаться построения видов в фоне (проверки).</summary>
    public void WaitViews(int ms = 120000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_building.Count > 0 && sw.ElapsedMilliseconds < ms) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    /// <summary>Разбор скорости: время последнего вывода листа — виды, поля надписи, сборка листа (мс).</summary>
    public (double Scene, double Fields, double Compose) LastTimes { get; private set; }
    FieldSource? _partFields;

    public List<SheetGraphics> BuildPages()
    {
        ReSnap();   // k168 (п. 14): таблицы, прилипшие к рамке, — на месте после смены формата и стандарта
        if (Kind == Mode.Bom) { Bom!.Template = Template; return Bom.Pages(); }
        if (Kind == Mode.Drawing)
        {
            var d = Drawing!; d.Template = Template;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EnsureScene();
            double tScene = sw.Elapsed.TotalMilliseconds;
            // k162: поля надписи модели (масса — объём тел по сетке, около 400 мс у детали пользователя) — один раз на вход
            // чертежа; прежде — при каждом выводе листа, то есть на каждом шаге переноса (около 3 кадров в секунду)
            var fs = _input?.Part != null ? (_partFields ??= FieldSource.FromPart(_input.Part)) : FieldValues();
            // k169: номер листа и число листов
            fs.Auto[Model.Drawing.Fields.Sheet] = (SheetIndex + 1).ToString(CultureInfo.InvariantCulture);
            fs.Auto[Model.Drawing.Fields.Sheets] = Math.Max(1, Sheets.Count).ToString(CultureInfo.InvariantCulture);
            double tFields = sw.Elapsed.TotalMilliseconds - tScene;
            var g = d.Compose(_scene!, fs);
            LastTimes = (tScene, tFields, sw.Elapsed.TotalMilliseconds - tScene - tFields);
            return new List<SheetGraphics> { g };
        }
        return new List<SheetGraphics> { SheetLayout.Compose(Template, FieldValues()) };
    }

    Button? _checkBtn;
    /// <summary>k163: показывать на листе элементы детали, чьи размеры не указаны.</summary>
    public bool ShowMissing { get; set; }
    /// <summary>k163: итог последней проверки полноты размеров.</summary>
    public List<DrawingCheck.Item> CheckItems { get; private set; } = new();
    bool _checkOk;
    void UpdateCheck()
    {
        if (Kind != Mode.Drawing || _checkBtn == null || _input?.Part is not { } part) return;
        try { CheckItems = DrawingCheck.Run(Drawing!, part); } catch (Exception ex) { Core.AppLog.Write("sheet: check", ex); return; }
        var miss = CheckItems.Where(i => !i.Covered).ToList();
        bool ok = miss.Count == 0;
        string tip = ok
            ? L("Размеры чертежа полные: указаны все {0} размеров детали.", CheckItems.Count)
            : L("Размеры чертежа неполные: указано {0} из {1}. Не указаны:", CheckItems.Count - miss.Count, CheckItems.Count) + "\n" +
              string.Join("\n", miss.Take(25).Select(i => "• " + i.Label)) + (miss.Count > 25 ? "\n…" : "") +
              "\n" + L("Щелчок — показать на чертеже элементы без размеров (красным).");
        if (ok != _checkOk || _checkBtn.Content == null) _checkBtn.Content = Icons.IconProvider.Content(ok ? "state_ok_ico.svg" : "state_warning_ico.svg", tip, 26);
        _checkOk = ok;
        // k170 (п. 7.3): вместо подсказки — список под кнопкой (наведение — серый размер на листе, щелчок — размер поставлен)
        if (CheckListOff) ToolTip.SetTip(_checkBtn, tip); else { ToolTip.SetTip(_checkBtn, null); FillCheckList(miss); }
    }
    static readonly SkiaSharp.SKColor MissingColor = new(220, 30, 30, 200);

    public void Refresh()
    {
        try
        {
            Pages = BuildPages();
            if (_drag == null) UpdateCheck();
            _page = Math.Clamp(_page, 0, Math.Max(0, Pages.Count - 1));
            var g = Pages[_page];
            _canvas.Graphics = g;
            if (_sel != null && Kind == Mode.Drawing && !(Drawing!.Dims.Contains(_sel) || Drawing.Notes.Contains(_sel) || Drawing.Views.Contains(_sel) || _sel is CentreMark cm0 && Drawing.CentreMarks.Contains(cm0) || _sel is TemplateItem ti0 && Template.Items.Contains(ti0))) _sel = null;
            UpdateOverlay();
            // k167: формат и номер листа — справа в строке состояния (прежде затирали сообщения и ход построения видов)
            _sheetInfo.Text = $"{Template.Format} {(Template.Landscape ? L("альбомная") : L("книжная"))} · {L("лист {0} из {1}", _page + 1, Pages.Count)}" +
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
        else if (Kind == Mode.Drawing) { Drawing!.Template = Template; File.WriteAllText(path, (_root ?? Drawing).ToJson()); }   // k169: все листы
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
                Title = L("Сохранить"), DefaultExtension = ext, SuggestedFileName = (SuggestedName ?? (Kind == Mode.Bom ? (Bom!.Title.Length > 0 ? Bom.Title : L("Перечень")) : Template.Name)) + ext,
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

    /// <summary>k169: имя файла из окна создания документа (предлагается при первой записи).</summary>
    public string? SuggestedName { get; set; }

    /// <summary>k169 (п. 17): шаблон из окна создания документа — лист и единицы чертежа.</summary>
    public void ApplyTemplate(SheetTemplate t)
    {
        Template = t;
        if (Drawing != null)
        {
            Drawing.Template = t;
            var u = t.Units.Split('/')[0];
            if (u is "mm" or "cm" or "m" or "in" or "ft") Drawing.Units = u;
        }
        if (Bom != null) Bom.Template = t;
        Refresh(); BuildProps(); UpdateTabs();
    }

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
