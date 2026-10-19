using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Parametric3D.App.Commands;
using Parametric3D.App.Icons;
using Parametric3D.App.Operations;
using OpenTK.Mathematics;
using Parametric3D.App.Services;
using Parametric3D.App.Sketching;
using Parametric3D.App.ViewModels;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;
using Parametric3D.Render.Engine;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// ГЛАВНОЕ ОКНО: верхняя полоса (файл, документ, отмена, поиск команды, параметры, свойства, тема, язык), дерево модели,
/// 3D-вид с полосой команд слева (операции или инструменты эскиза), панелью вида сверху (вписать, проекция, стиль,
/// «Опорные» со списком), кубом вида и полосой выбора снизу (что выбрано и какие операции к нему подходят — как в
/// Plasticity), правая панель — параметры операции или свойства, строка состояния, палитра команд (F).
/// Все кнопки — из реестра команд (StandardCommands): те же команды есть в палитре и на клавишах.
/// </summary>
public partial class MainWindow : Window
{
    public DocumentSession Session { get; private set; } = null!;
    public ModelViewport ViewPort => View;
    private readonly CommandRegistry _reg = CommandRegistry.Instance;
    private List<TreeNode> _roots = new();
    private bool _syncingTree, _treePending;
    private DispatcherTimer? _toastTimer;
    private readonly Dictionary<uint, bool> _expand = new();

    public Operation? CurrentOperation { get; private set; }
    public bool InSketch => View.ActiveSketch != null;
    /// <summary>u13h: открыто окружение FreeForm (узел FreeForm правится; у FreeForm-документа — всегда).</summary>
    public bool InFreeForm => !InSketch && Session?.Part?.EditingFreeForm != null && !(Session.IsAssembly && Session.ActivePart == null);
    public bool ConstructionMode { get; private set; }
    private bool _sketchIsNew;
    private Func<Sketch, Operation>? _afterSketch;
    private bool _propsOn;
    public bool PropertiesVisible => _propsOn;
    public bool OperationPanelVisible => PropsPanel.IsVisible;

    public MainWindow() : this(DocumentSession.NewPart()) { }

    public MainWindow(DocumentSession session)
    {
        InitializeComponent();
        // шрифт ГОСТ тип А — только у объектов вида (размеры эскиза, подписи куба вида); меню и панели — шрифтом интерфейса
        Loc.Set(UserPrefs.Current.Language == "en");
        StandardCommands.Register(this);
        WireView();
        WireTree();
        WirePalette();
        ApplyTheme(UserPrefs.Current.Theme != "Light");
        SetSession(session);
        if (App.PreviousRunCrashed)
            Dispatcher.UIThread.Post(() => ShowToast(L("Прошлый запуск завершился аварийно. Журнал: {0}", AppLog.Path)), DispatcherPriority.Background);

        _reg.Failed += (c, ex) => ShowToast($"{c.Text}: {ex.Message}");
        _reg.Executed += c => { if (c.Id != "palette") UserPrefs.Current.AddRecentCommand(c.Id); RefreshToolStates(); };
        KeyDown += OnWindowKeyDown;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Write("unhandled", e.Exception);
            ShowToast(L("Внутренняя ошибка: ") + e.Exception.Message);
            e.Handled = true;
        };
        Closing += OnClosing;
        SearchButton.Click += (_, _) => OpenPalette();
        EnvPill.PointerPressed += (_, _) => Commands.ContextCommands.EnvMenu(this, EnvPill);
        TreeNode.VisibilityToggled += _ => { View.MarkDirty(); Session.MarkModified(); };
        TreeNode.ExpandedChanged += n => _expand[n.Item.Id] = n.IsExpanded;
    }

    // ================================================================ документ

    public void SetSession(DocumentSession s)
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: открыть документ");
        if (CurrentOperation != null) { CurrentOperation = null; HideOpPanel(); }
        if (InSketch) View.ExitSketch(false);
        if (Session != null) { Session.Replaced -= OnReplaced; Session.StateChanged -= OnStateChanged; Session.Document.BuildStarted -= OnBuild; Session.Document.BuildFinished -= OnBuild; }
        Session = s;
        View.ActivePart = s.ActivePart;
        Commands.DisplayUnits.Inch = s.Part?.Units.Length == "in";
        s.Replaced += OnReplaced;
        s.StateChanged += OnStateChanged;
        s.Document.BuildStarted += OnBuild;
        s.Document.BuildFinished += OnBuild;
        s.Document.Root.Changed += _ => ScheduleTree();
        _expand.Clear();
        View.Document = s.Document;
        View.ActivePart = s.Part;
        View.DarkTheme = _dark;
        // u13h: FreeForm-документ открывается сразу в окружении FreeForm
        if (s.Part is { FreeFormDoc: true } ffp && !s.IsAssembly) Commands.FreeFormCommands.EnterDocument(this, ffp, rebuildUi: false);
        RebuildTree();
        RebuildUi();
        OnStateChanged();
        Dispatcher.UIThread.Post(() => View.ZoomToFit(false), DispatcherPriority.Background);
    }

    private void OnReplaced()
    {
        // отмена и повтор меняют деталь целиком: подписка на изменения — у нового корня та же, деталь — новая
        View.HoldSceneUntilBuilt();
        View.Document = null;
        View.Document = Session.Document;
        View.ActivePart = Session.Part;
        View.DarkTheme = _dark;
        // u13h: отмена заменила деталь — открытый узел FreeForm остаётся открытым (если он ещё есть)
        if (FreeFormKey is { } ffk && Session.Part is { } np)
        {
            if (np.Features.Any(f => f is FreeFormFeature && f.Key == ffk)) np.EditingFreeForm = ffk;
            else { FreeFormKey = null; RebuildUi(); }
        }
        RebuildTree();
        UpdateContextBar();
    }

    /// <summary>u13h: ключ открытого узла FreeForm (переживает замену детали при отмене).</summary>
    public string? FreeFormKey;

    private void OnBuild() => Dispatcher.UIThread.Post(() => { UpdateStatus(); ScheduleTree(); UpdateCages(); View.MarkDirty(); });

    /// <summary>u13k: клетки SubD тел детали — в окружении FreeForm поверх модели.</summary>
    public void UpdateCages()
    {
        var part = Session?.Part;
        if (!InFreeForm || part == null || part.CagesByBody.Count == 0) { if (View.CageLines.Count > 0) View.SetCages(Array.Empty<(Vector3d, Vector3d, bool)>()); return; }
        var lines = new List<(Vector3d, Vector3d, bool)>();
        foreach (var c in part.CagesByBody.Values)
            foreach (var (a, b) in c.EdgeFaces().Keys) lines.Add((c.V[a], c.V[b], c.Creases.Contains((a, b))));
        View.SetCages(lines);
    }

    private void OnStateChanged()
    {
        Title = $"{Session.Title}{(Session.Modified ? " •" : "")} — Parametric3D";
        DocTitle.Text = Session.Title + (Session.Modified ? " •" : "");
        _reg.RefreshAll();
        RefreshToolStates();
        UpdateStatus();
    }

    public async Task<bool> ConfirmDiscard()
    {
        if (!Session.Modified) return true;
        string save = L("Сохранить"), discard = L("Не сохранять"), cancel = L("Отмена");
        var r = await Dialogs.Ask(this, L("Несохранённые изменения"), L("Сохранить изменения в «{0}»?", Session.Title), save, discard, cancel);
        if (r == save) return await SaveAsync(false);
        return r == discard;
    }

    private bool _closingConfirmed;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || !Session.Modified || AppStartup.Flag("--shot")) return;
        e.Cancel = true;
        if (await ConfirmDiscard()) { _closingConfirmed = true; Close(); }
    }

    public async Task<bool> SaveAsync(bool saveAs)
    {
        string? path = saveAs ? null : Session.FilePath;
        if (path == null)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L(Session.IsAssembly ? "Сохранить сборку" : "Сохранить деталь"),
                SuggestedFileName = Session.Title + (Session.IsAssembly ? DocumentSession.AssemblyExtension : DocumentSession.PartExtension),
                DefaultExtension = Session.IsAssembly ? DocumentSession.AssemblyExtension : DocumentSession.PartExtension,
                FileTypeChoices = new[] { Session.IsAssembly ? new FilePickerFileType(L("Сборка Parametric3D")) { Patterns = new[] { "*" + DocumentSession.AssemblyExtension } } : new FilePickerFileType(L("Деталь Parametric3D")) { Patterns = new[] { "*" + DocumentSession.PartExtension } } },
            });
            path = f?.TryGetLocalPath();
            if (path == null) return false;
        }
        Session.SaveAs(path);
        UserPrefs.Current.AddRecentFile(path);
        ShowToast(L("Сохранено: {0}", path), error: false);
        return true;
    }

    public async Task OpenAsync(string? path = null)
    {
        if (!await ConfirmDiscard()) return;
        if (path == null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Открыть"),
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(L("Деталь или сборка Parametric3D")) { Patterns = new[] { "*" + DocumentSession.PartExtension, "*" + DocumentSession.AssemblyExtension } } },
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
        }
        SetSession(DocumentSession.Open(path));
        UserPrefs.Current.AddRecentFile(path);
    }

    public async Task ImportAsync()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Импорт"),
            FileTypeFilter = new[] { new FilePickerFileType("STEP, IGES") { Patterns = new[] { "*.step", "*.stp", "*.iges", "*.igs" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        var warnings = new List<string>();
        SetSession(DocumentSession.Import(path, warnings));
        if (warnings.Count > 0) ShowToast(string.Join("\n", warnings.Take(6)), error: false);
    }

    public async Task ExportAsync()
    {
        var part = Session.Part;
        if (part == null || part.Bodies.Count == 0) { ShowToast(L("Экспорт: в детали ещё нет тела.")); return; }
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("Экспорт"),
            SuggestedFileName = Session.Title + ".step",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(L("STEP точный (B-rep)")) { Patterns = new[] { "*.step", "*.stp" } },
                new FilePickerFileType("IGES") { Patterns = new[] { "*.igs", "*.iges" } },
                new FilePickerFileType("STL") { Patterns = new[] { "*.stl" } },
                new FilePickerFileType("OBJ") { Patterns = new[] { "*.obj" } },
            },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        string msg = ExportService.Export(Session, path);
        ShowToast(msg, error: msg.StartsWith("Не"));
    }

    // ================================================================ панели и кнопки

    private readonly List<(Control Button, AppCommand Cmd)> _toggles = new();
    private Button? _projButton, _styleButton;
    private ToggleButton? _datumsButton;

    private Control CmdButton(AppCommand c, double icon = 20, bool text = false, double minWidth = 0)
    {
        bool hasIcon = IconProvider.Exists(c.Icon);
        Control content = hasIcon && !text
            ? IconProvider.Content(c.Icon, c.ShortText, icon)
            : new TextBlock { Text = c.ShortText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (hasIcon && text)
            content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { IconProvider.Content(c.Icon, c.ShortText, icon), content } };
        Control b;
        if (c.IsChecked != null)
        {
            var t = new ToggleButton { Classes = { "tool" }, Content = content, IsChecked = c.IsChecked(), MinWidth = minWidth };
            t.Click += (_, _) => { c.Execute(); RefreshToolStates(); };
            _toggles.Add((t, c));
            b = t;
        }
        else b = new Button { Classes = { "tool" }, Command = c, Content = content, MinWidth = minWidth };
        if (b is ContentControl cc) cc.HorizontalContentAlignment = text ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        ToolTip.SetTip(b, c.Tooltip + (c.Description != null ? "\n" + L(c.Description) : ""));
        b.Tag = c;
        return b;
    }

    private static Border Separator(bool vertical) => vertical
        ? new Border { Width = 1, Margin = new Thickness(4, 6), Background = Brushes.Gray, Opacity = 0.35 }
        : new Border { Height = 1, Margin = new Thickness(4, 4), Background = Brushes.Gray, Opacity = 0.35 };

    /// <summary>Пересобрать все панели (смена языка, темы, режима эскиза).</summary>
    public void RebuildUi()
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: пересобрать панели");
        _toggles.Clear();
        BuildTopBar();
        BuildViewTools();
        if (_docks.TryGetValue("view", out var vd) && !vd.Horizontal) DockablePanel.Reorient(ViewTools, false);
        BuildCommandBar();
        TreeTools.Children.Clear();
        TreeTools.Children.Add(CmdButton(_reg["tree.collapse"], 16));
        TreeTools.Children.Add(CmdButton(_reg["tree.expand"], 16));
        TreeHeader.Text = L("ДЕРЕВО МОДЕЛИ");
        SearchText.Text = L("Найти команду…");
        PaletteHelp.Text = L("↑↓ — выбор, Enter — выполнить, Esc — закрыть. Поиск по-русски и по-английски.");
        UpdateContextBar();
        UpdateStatus();
        if (CurrentOperation == null && _propsOn) ShowProperties();
        RefreshToolStates();
    }

    private void BuildTopBar()
    {
        FilePanel.Children.Clear();
        var fileBtn = new Button { Classes = { "tool" }, Content = new TextBlock { Text = "☰", FontSize = 16 } };
        ToolTip.SetTip(fileBtn, L("Файл"));
        fileBtn.Click += (_, _) => OpenFileMenu(fileBtn);
        FilePanel.Children.Add(fileBtn);
        foreach (var id in new[] { "file.new_part", "file.open", "file.save" }) FilePanel.Children.Add(CmdButton(_reg[id]));

        EditPanel.Children.Clear();
        foreach (var id in new[] { "edit.undo", "edit.history", "edit.redo" }) EditPanel.Children.Add(CmdButton(_reg[id]));   // u10: журнал — стрелкой справа от «Отменить»
        foreach (var id in new[] { "edit.copy", "edit.paste" }) EditPanel.Children.Add(CmdButton(_reg[id]));

        RightPanel.Children.Clear();
        RightPanel.Children.Add(CmdButton(_reg["app.selftest"]));
        RightPanel.Children.Add(CmdButton(_reg["app.parameters"]));
        RightPanel.Children.Add(CmdButton(_reg["app.properties"]));
        RightPanel.Children.Add(Separator(true));
        RightPanel.Children.Add(CmdButton(_reg["app.settings"]));
        RightPanel.Children.Add(CmdButton(_reg["app.help"]));
        RightPanel.Children.Add(CmdButton(_reg["app.theme"]));
        // язык — рядом с темой (п. 2): на кнопке — язык, на который переключит
        var lang = new Button { Classes = { "tool" }, Content = new TextBlock { Text = Loc.En ? "RU" : "EN", FontWeight = FontWeight.SemiBold, FontSize = 12 } };
        ToolTip.SetTip(lang, Loc.En ? "Переключить на русский" : "Switch to English");
        lang.Click += (_, _) => ToggleLanguage();
        RightPanel.Children.Add(lang);
    }

    private void BuildViewTools()
    {
        ViewTools.Children.Clear();
        ViewTools.Children.Add(CmdButton(_reg["view.fit"], 18));
        // проекция: одна кнопка, щелчок переключает (п. 7)
        _projButton = new ToggleButton { Classes = { "tool" } };
        _projButton.Click += (_, _) => { View.ToggleProjection(); UpdateProjectionButton(!View.IsPerspective); };
        ToolTip.SetTip(_projButton, _reg["view.projection"].Tooltip);
        ViewTools.Children.Add(_projButton);
        UpdateProjectionButton(View.IsPerspective);
        // стиль — выпадающий список
        _styleButton = new Button { Classes = { "tool" } };
        ToolTip.SetTip(_styleButton, L("Стиль отображения"));
        _styleButton.Click += (_, _) => OpenStyleMenu(_styleButton);
        ViewTools.Children.Add(_styleButton);
        UpdateStyleButton();
        ViewTools.Children.Add(Separator(true));
        // «Опорные»: кнопка показать/скрыть и стрелка со списком (п. 12)
        _datumsButton = new ToggleButton { Classes = { "tool" }, Content = IconProvider.Content(_reg["view.datums"].Icon, L("Опорные"), 18) };
        _datumsButton.Click += (_, _) => { ToggleDatums(); };
        ToolTip.SetTip(_datumsButton, _reg["view.datums"].Tooltip);
        var arrow = new Button { Classes = { "tool" }, Content = Chevron(), MinWidth = 14, Padding = new Thickness(2, 4) };
        ToolTip.SetTip(arrow, L("Что показывать"));
        arrow.Click += (_, _) => OpenShowFlyout(arrow);
        ViewTools.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { _datumsButton, arrow } });
        _datumsButton.IsChecked = _reg["view.datums"].IsChecked!();
        // u13: виды, выбор, анализ — списки кнопок таблицы иконок
        ViewTools.Children.Add(Separator(true));
        ViewTools.Children.Add(ListButton("Виды ▾", "view_iso_but.svg", new[] { "view.front", "view.back", "view.left", "view.right", "view.top", "view.bottom", "view.normal", "view.zoom_sel", "view.section", "view.grid" }));
        ViewTools.Children.Add(ListButton("Выбор ▾", "sel_filter_but.svg", new[] { "sel.vertex", "sel.edge", "sel.face", "sel.body", "sel.feature", "sel.filter", "sel.box", "sel.lasso", "sel.tangent_chain", "sel.face_loop" }));
        ViewTools.Children.Add(ListButton("Анализ ▾", null, new[] { "an.measure", "an.area", "an.perimeter", "an.mass", "an.zebra", "an.curvature", "an.check_body", "an.heal" }));
    }

    /// <summary>u13: кнопка со списком команд (вид, выбор, анализ).</summary>
    private Button ListButton(string text, string? icon, string[] ids)
    {
        // с иконкой — только иконка и стрелка списка (подпись — в подсказке); без иконки (пока «Анализ») — текст
        var b = new Button { Classes = { "tool" }, Content = icon != null && IconProvider.Exists(icon) ? IconWithChevron(icon, L(text)) : new TextBlock { Text = L(text), FontSize = 12 }, Padding = new Thickness(icon != null ? 4 : 8, 4) };
        if (icon != null) ToolTip.SetTip(b, L(text).TrimEnd('▾', ' '));
        b.Click += (_, _) =>
        {
            var sp = ListColumns();
            foreach (var id in ids) if (_reg.Find(id) is { } c) sp.Children.Add(CmdButton(c, 18, text: true, minWidth: 220));
            new Flyout { Content = sp, Placement = PlacementMode.BottomEdgeAlignedLeft }.ShowAt(b);
        };
        return b;
    }

    /// <summary>u13: рамка или аркан выбора поверх вида.</summary>
    private void ShowRegion(IReadOnlyList<Point>? poly)
    {
        RegionCanvas.Children.Clear();
        if (poly == null || poly.Count < 2) return;
        bool crossing = View.RegionMode == ModelViewport.RegionKind.Box && poly.Count == 4 && poly[2].X < poly[0].X;
        var pg = new Avalonia.Controls.Shapes.Polygon
        {
            Points = new Avalonia.Collections.AvaloniaList<Point>(poly),
            Stroke = Res("P3.Accent"), StrokeThickness = 1,
            StrokeDashArray = crossing ? new Avalonia.Collections.AvaloniaList<double> { 4, 3 } : null,
            Fill = new SolidColorBrush(Color.FromArgb(28, 80, 150, 255)),
        };
        RegionCanvas.Children.Add(pg);
    }

    /// <summary>Слой значков ограничений поверх вида.</summary>
    internal ConstraintGlyphLayer GlyphLayer { get; private set; } = null!;

    private void UpdateProjectionButton(bool perspective)
    {
        if (_projButton == null) return;
        // только иконка: нажата — перспектива, отжата — ортогональ; режим — в подсказке
        _projButton.Content = IconProvider.Content("view_perspective_but.svg", perspective ? L("Персп.") : L("Орто"), 18);
        if (_projButton is ToggleButton tp) tp.IsChecked = perspective;
        ToolTip.SetTip(_projButton, _reg["view.projection"].Tooltip + " — " + (perspective ? L("сейчас перспектива") : L("сейчас ортогональ")));
    }

    private static readonly (DisplayStyle Style, string Id)[] Styles =
    {
        (DisplayStyle.Shaded, "style.shaded"), (DisplayStyle.ShadedWithEdges, "style.shaded_edges"), (DisplayStyle.Wireframe, "style.wireframe"),
        (DisplayStyle.HiddenLines, "style.hidden_lines"), (DisplayStyle.Transparent, "style.transparent"), (DisplayStyle.Material, "style.material"),
        (DisplayStyle.ViewMesh, "style.view_mesh"), (DisplayStyle.FeaMesh, "style.fea_mesh"),
    };

    private void UpdateStyleButton()
    {
        if (_styleButton == null) return;
        var c = _reg[Styles.First(s => s.Style == View.Style).Id];
        _styleButton.Content = IconWithChevron(c.Icon, c.Text);
        ToolTip.SetTip(_styleButton, L("Стиль отображения") + ": " + c.Text);
    }

    public void SetStyle(DisplayStyle s)
    {
        View.Style = s;
        UpdateStyleButton();
        RefreshToolStates();
    }

    private void OpenStyleMenu(Control anchor)
    {
        var items = new List<object>();
        foreach (var (_, id) in Styles)
        {
            var mi = Item(_reg[id]);
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = _reg[id].IsChecked?.Invoke() == true;
            items.Add(mi);
        }
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft }.Open(anchor);
    }

    /// <summary>Кнопка «Опорные»: если показано что-то из опорных — скрыть все, иначе показать все.</summary>
    public void ToggleDatums()
    {
        var s = View.Show;
        bool any = s.Planes || s.Axes || s.Points || s.Origin;
        s.Planes = s.Axes = s.Points = s.Origin = !any;
        View.MarkDirty();
        RefreshToolStates();
    }

    private void OpenShowFlyout(Control anchor)
    {
        var sp = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        sp.Children.Add(new TextBlock { Text = L("ПОКАЗЫВАТЬ"), Classes = { "header" }, Margin = new Thickness(2, 0, 0, 4) });
        foreach (var (key, label) in StandardCommands.ShowItems)
        {
            var cb = new CheckBox { Content = L(label), IsChecked = StandardCommands.GetShow(View.Show, key) };
            cb.IsCheckedChanged += (_, _) => { StandardCommands.SetShow(View.Show, key, cb.IsChecked == true); View.MarkDirty(); RefreshToolStates(); };
            sp.Children.Add(cb);
        }
        var fl = new Flyout { Content = sp, Placement = PlacementMode.BottomEdgeAlignedLeft };
        fl.ShowAt(anchor);
    }

    /// <summary>Полоса команд слева: операции модели или инструменты эскиза (п. 3, 9). u13: у группы — «Ещё…» (кнопки таблицы).</summary>
    /// <summary>Высота столбца панели команд — по высоте вида (за вычетом полей): лишнее уходит в следующий столбец.</summary>
    private void FitCommandBar()
    {
        if (_cmdHorizontal)
        {
            CommandStack.MaxHeight = double.PositiveInfinity;
            CommandStack.MaxWidth = Math.Max(300, ViewArea.Bounds.Width - 60);
            return;
        }
        CommandStack.MaxWidth = double.PositiveInfinity;
        double h = ViewArea.Bounds.Height - CommandBar.Margin.Top - CommandBar.Padding.Top - CommandBar.Padding.Bottom - 10;
        if (_docks.TryGetValue("commands", out var d) && d.UserPlaced) h = ViewArea.Bounds.Height - 40;
        if (h > 100) CommandStack.MaxHeight = h;
    }

    // ================================================================ п. 3 (21:04): панели перемещаются и прилипают к сторонам вида

    private readonly Dictionary<string, DockablePanel> _docks = new();
    private bool _cmdHorizontal;
    internal IReadOnlyDictionary<string, DockablePanel> Docks => _docks;

    private void SetupDocks()
    {
        void Add(DockablePanel d) { _docks[d.Key] = d; d.Moved += () => { if (d.Key is "commands" or "shapes") PlaceSketchBars(); }; }
        Add(new DockablePanel("commands", CommandBar, ViewArea, false, h => { _cmdHorizontal = h; CommandStack.Orientation = h ? Orientation.Horizontal : Orientation.Vertical; BuildCommandBar(); }));
        Add(new DockablePanel("shapes", ShapeBar, ViewArea, false, h =>
        {
            if (ShapeStack.Parent is StackPanel outer) outer.Orientation = h ? Orientation.Horizontal : Orientation.Vertical;
            ShapeStack.Width = h ? double.NaN : 120;
            ShapeStack.MaxWidth = h ? Math.Max(300, ViewArea.Bounds.Width - 120) : double.PositiveInfinity;
        }));
        Add(new DockablePanel("view", ViewBar, ViewArea, true, h => DockablePanel.Reorient(ViewTools, h)));
        Add(new DockablePanel("sketch", SketchBar, ViewArea, true, h => DockablePanel.Reorient(SketchBarStack, h)));
        Add(new DockablePanel("context", ContextBar, ViewArea, true, h =>
        {
            if (SelChip.Parent is StackPanel inner) DockablePanel.Reorient(inner, h);
            DockablePanel.Reorient(ContextOps, h);
            SelChipText.MaxWidth = h ? double.PositiveInfinity : 200;
            SelChipText.TextWrapping = h ? TextWrapping.NoWrap : TextWrapping.Wrap;
        }));
        bool restored = false;
        ViewArea.SizeChanged += (_, _) =>
        {
            if (!restored && ViewArea.Bounds.Width > 50) { restored = true; foreach (var d in _docks.Values) d.Restore(); }
            foreach (var d in _docks.Values) d.Relayout();
        };
        foreach (var d in _docks.Values) d.Panel.SizeChanged += (_, _) => d.Relayout();
    }

    /// <summary>Маленькая стрелка «список» (рисунок, не текст).</summary>
    private static Control Chevron() => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse("M0,0 L4,4 L8,0"), StrokeThickness = 1.4, Width = 8, Height = 5, Stretch = Stretch.Uniform,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
        Stroke = new SolidColorBrush(IconProvider.Foreground),
    };

    /// <summary>Иконка и стрелка списка; нет иконки — подпись.</summary>
    private static Control IconWithChevron(string? icon, string label) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 3,
        Children = { IconProvider.Content(icon, label, 18), Chevron() },
    };

    /// <summary>Список команд всплывающего меню: все пункты видны — не помещаются по высоте окна, следующий столбец (без прокрутки).</summary>
    private WrapPanel ListColumns() => new() { Orientation = Orientation.Vertical, MaxHeight = Math.Max(200, Bounds.Height - 120) };

    private bool _fitHooked;
    private void BuildCommandBar()
    {
        if (!_fitHooked) { ViewArea.SizeChanged += (_, _) => FitCommandBar(); _fitHooked = true; }
        FitCommandBar();
        CommandStack.Children.Clear();
        bool asmMode = !InSketch && Session?.IsAssembly == true && Session.ActivePart == null;
        bool ffMode = !asmMode && InFreeForm;
        (string[] Main, string[] More)[] groups = ffMode
            ? new[]
            {
                (new[] { "ff.box", "ff.cylinder", "ff.cplane" }, new[] { "ff.cone", "ff.sphere", "ff.torus", "subd.box", "ff.curve_nurbs", "ff.curve_spiral" }),
                (new[] { "ff.extrude", "ff.revolve", "ff.loft" }, new[] { "ff.sweep", "ff.pipe", "ff.thicken", "ff.shell" }),
                (new[] { "ff.push_pull", "ff.move", "ff.rotate", "ff.scale" }, new[] { "ff.align_face", "ff.replace_face", "ff.delete_face", "ff.draft_face" }),
                (new[] { "ff.fillet", "ff.chamfer" }, Array.Empty<string>()),
                (new[] { "ff.union", "ff.subtract", "ff.intersect", "ff.split" }, Array.Empty<string>()),
                (new[] { "ff.mirror", "ff.symmetry", "ff.array_linear" }, new[] { "ff.array_circular", "ff.array_curve" }),
                (Array.Empty<string>(), new[] { "ff.curve_offset", "ff.curve_fillet", "ff.curve_join", "ff.curve_extend", "ff.curve_bridge", "ff.curve_rebuild", "ff.curve_extract", "ff.curve_isoparm", "ff.project", "ff.imprint", "ff.trim_curve" }),
                (Array.Empty<string>(), new[] { "ff.surf_ruled", "ff.surf_network", "ff.surf_patch", "ff.surf_planar", "ff.surf_blend", "ff.surf_extend", "ff.surf_flip", "ff.surf_stitch", "ff.cv_edit", "ff.cv_weight", "ff.insert_knot" }),
                (Array.Empty<string>(), new[] { "subd.extrude", "subd.edge_loop", "subd.crease", "subd.smooth_level", "subd.to_nurbs" }),
                (new[] { "ff.step_undo_to", "ff.finish" }, new[] { "ff.update_refs", "an.measure", "an.zebra", "an.curvature", "an.check_body" }),
            }
            : asmMode
            ? new[]
            {
                (new[] { "as.insert_component", "as.create_component", "as.edit_in_context" }, new[] { "as.insert_freeform", "as.skeleton", "as.open_component", "as.replace", "file.insert_part" }),
                (new[] { "mate.coincident", "mate.concentric", "mate.distance" }, new[] { "mate.angle", "mate.parallel", "mate.perpendicular", "mate.tangent", "mate.insert", "mate.point_on", "mate.rigid", "as.flip" }),
                (new[] { "joint.fixed", "joint.pin", "joint.slider" }, new[] { "joint.cylindrical", "joint.bearing", "joint.planar", "joint.ball", "joint.slot", "joint.weld", "joint.gimbal" }),
                (new[] { "as.fix", "as.drag" }, new[] { "as.pattern", "as.pattern_ref", "as.mirror", "as.subassembly" }),
                (new[] { "as.explode", "as.bom" }, Array.Empty<string>()),
            }
            : InSketch
            ? new[]
            {
                // фигуры — своя панель (п. 7); размеры и правка фигур — в полосе предложений снизу (пп. 9–10);
                // «Готово / Отмена» — вверху у куба (п. 11)
                (new[] { "dim.smart", "con.coincident", "con.horizontal", "con.vertical", "con.tangent" },
                 new[] { "con.point_on", "con.midpoint", "con.parallel", "con.perpendicular", "con.collinear", "con.equal", "con.concentric", "con.symmetric", "con.fix" }),
                (new[] { "sk.construction", "sk.grid", "sk.references" },
                 new[] { "sk.snap_items", "sk.show_open_ends", "sk.show_filled", "sk.show_conflicts", "sk.setup" }),
            }
            : new[]
            {
                (new[] { "op.sketch" }, Array.Empty<string>()),
                (new[] { "op.extrude", "op.cut", "op.revolve" }, new[] { "op.extrude_thin", "op.sweep", "op.helical_sweep", "op.loft", "op.boundary_blend", "op.swept_blend", "op.rotational_blend", "op.rib", "op.insert_body" }),
                (Array.Empty<string>(), new[] { "op.surface_extrude", "op.surface_revolve", "op.fill", "op.surf_copy", "op.surf_offset", "op.surf_merge", "op.surf_trim", "op.surf_extend", "op.thicken", "op.surf_solidify", "op.replace_face" }),
                (new[] { "op.hole", "op.round", "op.chamfer", "op.shell" }, new[] { "op.round_variable", "op.full_round", "op.vertex_round", "op.vertex_chamfer", "op.draft", "op.hole_coaxial", "op.hole_radial", "op.thread" }),
                (Array.Empty<string>(), new[] { "op.face_offset", "op.face_move", "op.face_radius", "op.face_delete", "op.move_copy" }),
                (Array.Empty<string>(), new[] { "an.measure", "an.area", "an.perimeter", "an.mass", "an.zebra", "an.curvature", "an.check_body", "an.heal" }),
                (new[] { "op.plane" }, new[] { "op.plane_offset", "op.plane_mid", "op.plane_3pt", "op.plane_axis", "op.plane_normal_curve", "op.plane_tangent", "op.axis", "op.axis_face", "op.axis_intersect", "op.point", "op.point_center", "op.point_intersect", "op.csys", "op.curve_points", "op.curve_equation", "op.curve_project" }),
                (new[] { "op.mirror", "op.pattern_linear", "op.pattern_circular" }, new[] { "op.pattern_points", "op.pattern_curve", "op.pattern_fill", "op.move_copy" }),
                (Array.Empty<string>(), new[] { "tree.rebuild", "tree.needs_attention", "tree.resume", "tree.insert_here", "tree.reorder_up", "tree.reorder_down", "tree.edit_dims", "tree.group", "tree.parents_children", "tree.replace_refs" }),
            };
        CommandStack.Children.Add(new TextBlock
        {
            Text = InSketch ? L("ЭСКИЗ") : asmMode ? L("СБОРКА") : ffMode ? L("FREEFORM") : L("ОПЕРАЦИИ"), Classes = { "header" }, Margin = new Thickness(6, 2, 6, 4),
        });
        for (int g = 0; g < groups.Length; g++)
        {
            if (g > 0)
            {
                var sep = Separator(_cmdHorizontal);
                if (_cmdHorizontal) sep.Height = 28; else sep.Width = 128;
                CommandStack.Children.Add(sep);
            }
            foreach (var id in groups[g].Main) if (_reg.Find(id) is { } c) CommandStack.Children.Add(CmdButton(c, 20, text: true, minWidth: _cmdHorizontal ? 0 : 128));
            if (groups[g].More.Length > 0)
            {
                var more = groups[g].More.Where(id => _reg.Find(id) != null).ToArray();
                // u13: группа без основных кнопок — подпись группы вместо «Ещё…»
                string moreLabel = groups[g].Main.Length > 0 ? "Ещё…" : groups[g].More[0] switch
                {
                    "op.face_offset" => "Прямая правка ▸", "an.measure" => "Анализ ▸", "ff.curve_offset" => "Кривые ▸", "ff.surf_ruled" => "Поверхности ▸", "subd.extrude" => "SubD ▸", "tree.rebuild" => "Дерево ▸", "op.surface_extrude" => "Поверхности ▸", _ => "Ещё…",
                };
                var b = new Button { Content = L(moreLabel), Classes = { "tool" }, MinWidth = _cmdHorizontal ? 0 : 128, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2) };
                b.Click += (_, _) =>
                {
                    var sp = ListColumns();
                    foreach (var id in more) sp.Children.Add(CmdButton(_reg[id], 18, text: true, minWidth: 220));
                    new Flyout { Content = sp, Placement = PlacementMode.RightEdgeAlignedTop }.ShowAt(b);
                };
                CommandStack.Children.Add(b);
            }
        }
        BuildSketchBars();
    }

    /// <summary>Инструменты построения на панели фигур (п. 7) — все, без «Ещё…».</summary>
    internal static readonly string[] ShapeTools =
    {
        "sk.line", "sk.centerline", "sk.rect", "sk.rect_center", "sk.rect_3p", "sk.circle", "sk.circle_2p", "sk.circle3",
        "sk.arc3", "sk.arc_center", "sk.arc_tangent", "sk.ellipse", "sk.ellipse_5p", "sk.spline_through", "sk.spline_cv",
        "sk.slot", "sk.polygon", "sk.point", "sk.text", "sk.csys",
    };

    private void BuildSketchBars()
    {
        ShapeStack.Children.Clear();
        SketchBarStack.Children.Clear();
        ShapeBar.IsVisible = SketchBar.IsVisible = InSketch;
        if (!InSketch) return;
        ShapeHeader.Text = L("ФИГУРЫ");
        foreach (var id in ShapeTools)
            if (_reg.Find(id) is { } c) { var b = CmdButton(c, 22); b.Width = 38; b.Height = 36; ShapeStack.Children.Add(b); }
        foreach (var id in new[] { "sk.finish", "sk.cancel" })
            if (_reg.Find(id) is { } c) SketchBarStack.Children.Add(CmdButton(c, 18, text: true));
        PlaceSketchBars();
        CommandBar.SizeChanged -= OnCommandBarSize; CommandBar.SizeChanged += OnCommandBarSize;
        Cube.SizeChanged -= OnCommandBarSize; Cube.SizeChanged += OnCommandBarSize;
    }

    private void OnCommandBarSize(object? s, SizeChangedEventArgs e) => PlaceSketchBars();

    /// <summary>Панель фигур — справа от панели команд; «Готово / Отмена» — слева от куба вида.</summary>
    private void PlaceSketchBars()
    {
        // по умолчанию: фигуры — справа от панели команд (если та не перенесена), «Готово / Отмена» — слева от куба;
        // перенесённые пользователем панели не трогаются
        if (!(_docks.TryGetValue("shapes", out var sd) && sd.UserPlaced))
        {
            bool cmdDefault = !(_docks.TryGetValue("commands", out var cd) && cd.UserPlaced);
            double cmdW = cmdDefault && CommandBar.Bounds.Width > 0 ? CommandBar.Bounds.Width : cmdDefault ? 140 : 0;
            ShapeBar.Margin = new Thickness(10 + cmdW + (cmdDefault ? 6 : 0), 10, 0, 0);
        }
        if (!(_docks.TryGetValue("sketch", out var kd) && kd.UserPlaced))
        {
            double cubeW = Cube.Bounds.Width > 0 ? Cube.Bounds.Width : 130;
            SketchBar.Margin = new Thickness(0, 10, 10 + cubeW + 8, 0);
        }
    }

    /// <summary>Правка значения размера (из полосы предложений).</summary>
    public void EditDimensionValue(SkDimension d) => Dispatcher.UIThread.Post(() => EditDimension(d, default));

    public void RefreshToolStates()
    {
        foreach (var (b, c) in _toggles) if (b is ToggleButton t) t.IsChecked = c.IsChecked?.Invoke();
        if (_datumsButton != null) _datumsButton.IsChecked = _reg["view.datums"].IsChecked!();
        UpdateStyleButton();
    }

    private MenuItem Item(AppCommand c) => new()
    {
        Header = c.Text,
        Command = c,
        InputGesture = c.Gesture,
        Icon = IconProvider.Get(c.Icon) is { } img ? new Image { Source = img, Width = 16, Height = 16 } : null,
    };

    private void OpenFileMenu(Control anchor)
    {
        var items = new List<object>();
        foreach (var id in new[] { "file.new_doc", "file.new_part", "file.new_assembly", "file.new_freeform", "file.sample", "file.open", "file.insert_part" }) if (_reg.Find(id) is { } fc) items.Add(Item(fc));
        var recent = new MenuItem { Header = L("Последние файлы"), Icon = null };
        var rec = UserPrefs.Current.RecentFiles.Where(File.Exists).ToList();
        foreach (var p in rec)
        {
            var mi = new MenuItem { Header = Path.GetFileName(p) };
            ToolTip.SetTip(mi, p);
            mi.Click += async (_, _) => { try { await OpenAsync(p); } catch (Exception ex) { ShowToast(L("Открыть: ") + ex.Message); } };
            recent.Items.Add(mi);
        }
        recent.IsEnabled = rec.Count > 0;
        items.Add(recent);
        items.Add(new Separator());
        foreach (var id in new[] { "file.save", "file.save_as", "file.close" }) items.Add(Item(_reg[id]));
        items.Add(new Separator());
        foreach (var id in new[] { "file.import", "file.export" }) items.Add(Item(_reg[id]));
        items.Add(new Separator());
        foreach (var id in new[] { "part.material", "part.units", "part.configurations", "app.settings", "app.all_commands", "app.help" }) items.Add(Item(_reg[id]));
        items.Add(new Separator());
        var gfx = new MenuItem { Header = L("Графика") };
        foreach (var mode in new[] { "auto", "angle", "wgl", "software" })
        {
            var mi = Item(_reg["app.gl_" + mode]);
            mi.Header = L(_reg["app.gl_" + mode].Label).Split(':', 2)[1].Trim();
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = _reg["app.gl_" + mode].IsChecked?.Invoke() == true;
            gfx.Items.Add(mi);
        }
        items.Add(gfx);
        var pv = new MenuItem { Header = L("Предпросмотр операций") };
        foreach (var mode in new[] { "full", "contour" })
        {
            var c = _reg["app.preview_" + mode];
            var mi = Item(c);
            mi.Header = L(c.Label).Split(':', 2)[1].Trim();
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = c.IsChecked?.Invoke() == true;
            pv.Items.Add(mi);
        }
        items.Add(pv);
        foreach (var id in new[] { "app.selftest", "app.parameters", "app.theme", "app.language", "app.exit" }) items.Add(Item(_reg[id]));
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft }.Open(anchor);
    }

    // ================================================================ тема и язык

    private bool _dark = true;
    public bool IsDark => _dark;

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        if (Application.Current is { } app) app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        IconProvider.SetForeground(Color.Parse(dark ? "#E6E8EC" : "#1C1E22"));
        View.DarkTheme = dark;
        UserPrefs.Current.Theme = dark ? "Dark" : "Light";
        UserPrefs.Current.Save();
        if (Session != null) { RebuildUi(); RebuildTree(); }
        Cube.InvalidateVisual();
    }

    public void ToggleLanguage()
    {
        Loc.Set(!Loc.En);
        UserPrefs.Current.Language = Loc.En ? "en" : "ru";
        UserPrefs.Current.Save();
        RebuildUi();
        RebuildTree();
        if (CurrentOperation != null) PropsTitle.Text = L(CurrentOperation.Title).ToUpperInvariant();
        ShowHint(View.Tool != null ? L(View.Tool.Hint) : null);
        Cube.InvalidateVisual();
    }

    // ================================================================ вид

    private void WireView()
    {
        Cube.Viewport = View;
        View.CameraChanged += () => { Cube.Sync(); UpdateProjectionButton(View.IsPerspective); };
        View.HoverChanged += h => UpdateStatus(h);
        View.Selection.Changed += OnSelectionChanged;
        View.ContextRequested += p => { if (InSketch && View.DimensionAt(p) is { } dm) OpenDimensionMenu(dm); else OpenViewContextMenu(); };
        View.SceneRebuilt += () => { CurrentOperation?.RefreshStatus(); UpdateStatus(); if (_propsOn && CurrentOperation == null) ShowProperties(); };
        View.ToolChanged += () => { ShowHint(View.Tool != null ? L(View.Tool.Hint) : InSketch ? L("Выберите инструмент слева; Ctrl+Enter — закончить эскиз") : null); RefreshToolStates(); };
        View.RegionChanged += ShowRegion;
        // значки ограничений эскиза — поверх вида, под панелями
        GlyphLayer = new ConstraintGlyphLayer(View);
        ViewArea.Children.Insert(ViewArea.Children.IndexOf(RegionCanvas) + 1, GlyphLayer);
        SetupDocks();
        View.ComponentDragStarted += _ => Session.BeginEdit(L("Перетащить компонент"));
        View.ComponentDragEnded += _ => { Session.CommitEdit(); Commands.AssemblyCommands.Solve(this); };
        View.SketchEdited += () => { ScheduleTree(); UpdateStatus(); UpdateContextBar(); };
        View.ExpandEdge = r => r.Owner is Part p ? Topo.ChainRefs(p, r) : new List<PickRef> { r };
        View.ReferencePicked += AddSketchReference;
        View.DimensionEditRequested += EditDimension;
        View.HandleDragStarted += OnHandleDragStarted;
        View.HandleDragged += OnHandleDragged;
        View.HandleDragEnded += OnHandleDragEnded;
        View.SceneRebuilt += HideGhostWhenBuilt;
        View.GlReady += info => Dispatcher.UIThread.Post(() =>
        {
            StatusGl.Text = ShortGl(info);
            ToolTip.SetTip(StatusGl, info);
        });
        Dispatcher.UIThread.Post(() => { if (View.GlError != null) ShowToast("OpenGL: " + View.GlError); }, DispatcherPriority.Background);
    }

    private PickRef _lastPrimary;
    private List<(uint, PickKind)> _lastSelIds = new();

    private void OnSelectionChanged()
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: выбор изменился");
        // u12: наведение тоже извещает об изменении выбора — операции передаётся только изменившийся выбор
        var ids = View.Selection.Items.Select(r => (r.Id, r.Kind)).ToList();
        bool selChanged = !ids.SequenceEqual(_lastSelIds);
        _lastSelIds = ids;
        if (selChanged) CurrentOperation?.SelectionChanged(View.Selection.Items);
        if (CurrentOperation is ShellOp && !View.HandleDragging) AttachOpHandle();
        else if (CurrentOperation is RoundOp or ChamferOp && !View.HandleDragging && !_startingHandleOp && (_handleOpFixed || !ReferenceEquals(CurrentOperation, _handleOp))) AttachOpHandle();
        var prim = View.Selection.Primary;
        if (prim.Id != _lastPrimary.Id || View.Selection.IsEmpty)
        {
            _lastPrimary = prim;
            SyncTreeFromSelection();
        }
        UpdateStatus();
        UpdateContextBar();
        RefreshToolStates();     // u12: кнопки операций — по выбранному
        if (CurrentOperation == null && !_switchingHandle) UpdateHandle();
        if (_propsOn && CurrentOperation == null) ShowProperties();
    }

    // ================================================================ ручки операций (п. 6)

    private string _handleKind = "";
    /// <summary>Контур предпросмотра по величине ручки — наружу и внутрь; снят с геометрии до операции.</summary>
    private Func<double, List<(OpenTK.Mathematics.Vector3d A, OpenTK.Mathematics.Vector3d B)>>? _ghostOut, _ghostIn;
    /// <summary>u10: выйдет ли операция стороны «наружу»/«внутрь» при величине (контур красный, если нет).</summary>
    private Func<double, bool>? _validOut, _validIn;
    /// <summary>Ссылки, снятые до операции (после предпросмотра одной операции номера рёбер другие).</summary>
    private Dictionary<uint, Parametric3D.Kernel.Topology.EdgeRef>? _presetEdges;
    private Dictionary<uint, OpenTK.Mathematics.Vector3d>? _presetPoints;
    private Sketch? _presetOutline;
    /// <summary>Операция, начатая ручкой, и её сторона: в другую сторону — другая операция.</summary>
    private Operation? _handleOp;
    private int _handleOpSign;
    /// <summary>Операция идёт без панели (начата ручкой при закрытой панели свойств).</summary>
    private bool _opQuiet;
    private bool _switchingHandle, _startingHandleOp;

    /// <summary>u09: полный предпросмотр (деталь считается при перетаскивании) или контуром (настройка в меню «Файл»).</summary>
    public bool FullPreview => (UserPrefs.Current.PreviewMode ?? "full") != "contour";

    /// <summary>Ручка у выбранного: ребро (с цепочкой), вершина, плоская грань, поверхность.</summary>
    private void UpdateHandle()
    {
        View.Handle = null;
        _handleKind = "";
        _ghostOut = _ghostIn = null;
        _validOut = _validIn = null;
        _presetEdges = null; _presetPoints = null; _presetOutline = null;
        if (InSketch) return;
        try { UpdateHandleCore(); }
        catch (Exception ex) { AppLog.Write("handle", ex); }
    }

    private void UpdateHandleCore()
    {
        var sel = View.Selection.Items;
        if (sel.Count == 0 || sel[0].Owner is not Part part) { ShowHint(null); return; }
        var k = sel[0].Kind;
        if (sel.Any(r => r.Kind != k)) return;
        static Vector3 F(OpenTK.Mathematics.Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        switch (k)
        {
            case PickKind.Edge when Topo.EdgeHandle(part, sel[0].Id) is { } h:
                View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
                _handleKind = "edge";
                {
                    var ids = sel.Select(r => r.Id).ToList();
                    _ghostOut = Topo.EdgeGhost(part, ids, round: false) is { } go ? v => go(v) : null;
                    _ghostIn = Topo.EdgeGhost(part, ids, round: true) is { } gi ? v => gi(v) : null;
                    _validOut = Topo.EdgeGhostValid(part, ids, round: false);
                    _validIn = Topo.EdgeGhostValid(part, ids, round: true);
                    _presetEdges = new();
                    foreach (var id in ids) if (part.FindEdge(id) is { } e) _presetEdges[id] = Parametric3D.Kernel.Topology.EdgeRef.Of(e);
                }
                ShowHint(L("Ручка: потяните внутрь — скругление, наружу — фаска"));
                break;
            case PickKind.Vertex when sel.Count == 1 && Topo.VertexHandle(part, sel[0].Id) is { } h:
                View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
                _handleKind = "vertex";
                _ghostOut = Topo.VertexGhost(part, sel[0].Id, round: false) is { } vo ? v => vo(v) : null;
                _ghostIn = Topo.VertexGhost(part, sel[0].Id, round: true) is { } vi ? v => vi(v) : null;
                _validOut = _validIn = Topo.VertexGhostValid(part, sel[0].Id);
                if (Refs.PointOf(sel[0]) is { } vp) _presetPoints = new() { [sel[0].Id] = vp };
                ShowHint(L("Ручка: потяните внутрь — скругление вершины, наружу — фаска угла"));
                break;
            case PickKind.Face when sel.Count == 1 && Topo.FaceHandle(part, sel[0].Id) is { } h:
                View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
                _handleKind = h.Planar ? "face" : "surface";
                if (h.Planar)
                {
                    _ghostOut = Topo.FaceGhost(part, sel[0].Id, 1) is { } fo ? v => fo(v) : null;
                    _presetOutline = Topo.FaceOutlineSketch(part, sel[0].Id, L("Эскиз{0}", part.Sketches.Count() + 1));
                }
                else _ghostOut = Topo.FaceGhost(part, sel[0].Id, 1) is { } so ? v => so(v) : null;
                _ghostIn = Topo.FaceGhost(part, sel[0].Id, -1) is { } fi ? v => fi(v) : null;
                if (!h.Planar) { var sv = Topo.ShellValid(part); _validIn = sv; }
                ShowHint(h.Planar ? L("Ручка: потяните наружу — выдавить грань, внутрь — вырез") : L("Ручка: оболочка с открытой гранью — внутрь стенка внутрь, наружу — наружу"));
                break;
            default:
                ShowHint(null);
                break;
        }
    }

    /// <summary>Контур по сдвигу ручки со знаком: наружу — своя операция, внутрь — своя.</summary>
    private Func<double, List<(OpenTK.Mathematics.Vector3d A, OpenTK.Mathematics.Vector3d B)>>? SignedGhost()
    {
        if (FullPreview) return null;
        var (o, i) = (_ghostOut, _ghostIn);
        return s => (s > 0 ? o?.Invoke(s) : i?.Invoke(-s)) ?? new();
    }

    private Func<double, bool>? SignedValid()
    {
        var (o, i) = (_validOut, _validIn);
        if (o == null && i == null) return null;
        return s => s > 0 ? o?.Invoke(s) ?? true : i?.Invoke(-s) ?? true;
    }

    /// <summary>
    /// Ручку потянули (п. 6): одна ручка, сторона выбирает операцию. Панель не открывается (если панель свойств закрыта);
    /// пока тянут — полный предпросмотр (деталь считается по ходу) или контур (настройка); величина — у курсора.
    /// </summary>
    private void OnHandleDragStarted()
    {
        if (CurrentOperation is { } cur && ReferenceEquals(cur, _handleOp))
        {
            cur.LivePreview = FullPreview;
            cur.BeginDrag();
        }
        View.SetHandleGhost(SignedGhost(), SignedValid());
        ShowHint(null);
    }

    private Operation? MakeHandleOp(int sign)
    {
        Operation? op = (_handleKind, sign) switch
        {
            ("edge", < 0) => new RoundOp { PresetEdges = _presetEdges },
            ("edge", > 0) => new ChamferOp { PresetEdges = _presetEdges },
            ("vertex", < 0) => new VertexRoundOp { PresetPoints = _presetPoints },
            ("vertex", > 0) => new VertexChamferOp { PresetPoints = _presetPoints },
            ("face", var sg) when _presetOutline is { } outline => new ExtrudeOp
            {
                StartCut = sg < 0, FlipCut = sg < 0,
                PreSketch = _ => outline,
            },
            ("surface", _) => new ShellOp(),
            _ => null,
        };
        return op;
    }

    /// <summary>Начать операцию ручки этой стороны; ручка, её сдвиг и ссылки — прежние.</summary>
    private bool StartHandleOp(int sign)
    {
        if (MakeHandleOp(sign) is not { } op) return false;
        var keepH = View.Handle;
        double hv = View.HandleValue;
        var saved = (_ghostOut, _ghostIn, _presetEdges, _presetPoints, _presetOutline, _handleKind);
        var savedValid = (_validOut, _validIn);
        op.LivePreview = FullPreview;
        op.BeginDrag();
        _startingHandleOp = true;
        try { StartOperation(op, showPanel: false); }
        finally { _startingHandleOp = false; }
        if (!ReferenceEquals(CurrentOperation, op)) return false;
        View.Handle = keepH;
        View.SetHandleValue(hv);
        (_ghostOut, _ghostIn, _presetEdges, _presetPoints, _presetOutline, _handleKind) = saved;
        (_validOut, _validIn) = savedValid;
        View.SetHandleGhost(SignedGhost(), SignedValid());
        View.ShowGhost(hv);
        _handleOp = op;
        _handleOpSign = sign;
        return true;
    }

    /// <summary>Ручку перевели через начальное место: прежняя операция отменяется (выбор и ручка остаются).</summary>
    private void DropHandleOp()
    {
        if (CurrentOperation is not { } old) return;
        _switchingHandle = true;
        try
        {
            CurrentOperation = null;
            _handleOp = null;
            try { old.Cancel(); }
            catch (Exception ex) { AppLog.Write("handle switch", ex); }
            View.Selection.Filter = PickFilter.All;
        }
        finally { _switchingHandle = false; }
    }

    private void OnHandleDragged(int sign, double v)
    {
        if (_handleOpFixed && CurrentOperation is { } fop0 && ReferenceEquals(fop0, _handleOp))
        {
            // операция, начатая командой: сторона ручки не выбирает операцию; со знаком — наружу плюс (плоскость)
            double val = fop0.SignedDrag ? (sign > 0 ? v : -v) : Math.Abs(v);
            fop0.SetDragValue(val);
            View.Redraw();
            ShowDragLabel(fop0, val);
            return;
        }
        if (CurrentOperation is { SignedDrag: true } sop && ReferenceEquals(sop, _handleOp))
        {
            // одна операция на обе стороны: внутрь (знак −) — положительная величина
            sop.SetDragValue(sign < 0 ? v : -v);
            View.Redraw();
            ShowDragLabel(sop, sign < 0 ? v : -v);
            return;
        }
        if (CurrentOperation is { } cur && ReferenceEquals(cur, _handleOp) && _handleOpSign != sign) DropHandleOp();
        if (CurrentOperation == null || !ReferenceEquals(CurrentOperation, _handleOp))
        {
            if (CurrentOperation != null) CancelOperation();
            if (!StartHandleOp(sign)) return;
        }
        var op = CurrentOperation!;
        if (op.SignedDrag) { op.SetDragValue(sign < 0 ? v : -v); ShowDragLabel(op, sign < 0 ? v : -v); View.Redraw(); return; }
        op.SetDragValue(v);
        View.Redraw();
        ShowDragLabel(op, v);
    }

    private void ShowDragLabel(Operation op, double v)
    {
        string label = $"{(op.DragLabel.Length > 0 ? op.DragLabel + " " : "")}{Math.Sign(v) * Math.Round(Math.Max(0.01, Math.Abs(v)), 2):0.##} {L("мм")}";
        SelChipText.Text = L(op.Title) + " · " + label;
        bool bad = View.GhostBad || op.HasBuildError;
        DragValueText.Foreground = bad ? Res("P3.Error") : Res("P3.Text");
        if (bad) label += "  — " + L("не выйдет");
        // панель закрыта — величина у курсора
        if (!PropsPanel.IsVisible)
        {
            var c = View.HandleCursor;
            DragValueText.Text = L(op.Title) + "  " + label;
            DragValueBox.Margin = new Thickness(c.X + 18, c.Y + 14, 0, 0);
            DragValueBox.IsVisible = true;
        }
    }

    /// <summary>
    /// u10: ручка у операции, начатой командой (оболочка): у первой выбранной открытой грани; тянут внутрь — стенка внутрь,
    /// наружу — наружу. Выбрали другую грань — ручка переезжает.
    /// </summary>
    private void AttachOpHandle()
    {
        if (InSketch) return;
        if (CurrentOperation is RoundOp or ChamferOp) { AttachEdgeOpHandle(); return; }
        if (CurrentOperation is PlaneOp) { RefreshPlaneHandle(); return; }
        if (CurrentOperation is not ShellOp sh) return;
        var r = sh.FaceRefs.FirstOrDefault();
        if (r.Owner is not Part part || Topo.FaceHandle(part, r.Id) is not { } h) return;
        static Vector3 F(OpenTK.Mathematics.Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
        _ghostOut = Topo.FaceGhost(part, r.Id, 1) is { } go ? v => go(v) : null;
        _ghostIn = Topo.FaceGhost(part, r.Id, -1) is { } gi ? v => gi(v) : null;
        _validOut = null; _validIn = Topo.ShellValid(part);
        _handleKind = "shell";
        _handleOp = sh;
        View.SetHandleValue(-(sh.DragCurrent ?? 0));
        View.SetHandleGhost(SignedGhost(), SignedValid());
    }

    /// <summary>
    /// u12 (замечание 7): ручка у скругления и фаски, начатых командой — у первого выбранного ребра поля; тянут в любую
    /// сторону — величина этой операции (операция не меняется). Выбрали другие рёбра — ручка переезжает.
    /// </summary>
    private bool _handleOpFixed;

    /// <summary>u12 (замечание 14): ручка плоскости — у способов со смещением и углом (по точкам — без ручки).</summary>
    public void RefreshPlaneHandle()
    {
        if (CurrentOperation is not PlaneOp op || View.HandleDragging) return;
        View.Handle = null; _handleOp = null; _handleOpFixed = false;
        if (op.HandleAt() is { } h)
        {
            static Vector3 F(OpenTK.Mathematics.Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
            View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
            _ghostOut = _ghostIn = null; _validOut = _validIn = null;
            _handleKind = "plane"; _handleOp = op; _handleOpFixed = true;
            View.SetHandleValue(op.DragCurrent ?? 0);
            View.SetHandleGhost(null, null);
        }
        View.MarkDirty();
    }
    private void AttachEdgeOpHandle()
    {
        var op = CurrentOperation!;
        bool round = op is RoundOp;
        var refs = op is RoundOp ro ? ro.EdgeRefs : ((ChamferOp)op).EdgeRefs;
        View.Handle = null; _handleOp = null; _handleOpFixed = false;
        var r = refs.FirstOrDefault();
        if (r.Owner is not Part part || Topo.EdgeHandle(part, r.Id) is not { } h) { View.MarkDirty(); return; }
        static Vector3 F(OpenTK.Mathematics.Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
        var ids = refs.Where(x => x.Owner == part).Select(x => x.Id).ToList();
        var g = Topo.EdgeGhost(part, ids, round) is { } gg ? (Func<double, List<(OpenTK.Mathematics.Vector3d A, OpenTK.Mathematics.Vector3d B)>>)(v => gg(v)) : null;
        _ghostOut = _ghostIn = g;
        _validOut = _validIn = Topo.EdgeGhostValid(part, ids, round);
        _handleKind = "edge-op";
        _handleOp = op; _handleOpFixed = true;
        double cur = op.DragCurrent ?? 0;
        View.SetHandleValue(round ? -cur : cur);
        View.SetHandleGhost(SignedGhost(), SignedValid());
        View.MarkDirty();
    }

    private void OnHandleDragEnded()
    {
        DragValueBox.IsVisible = false;
        if (CurrentOperation is not { } op || !op.Dragging) return;
        // окончательный расчёт детали — с отпущенной величиной; контур — пока он не готов
        try { op.EndDrag(); }
        catch (Exception ex) { AppLog.Write("handle drag end", ex); }
        UpdateContextBar();
    }

    /// <summary>Деталь посчитана — контур предпросмотра больше не нужен; живой предпросмотр — следующая величина.</summary>
    private void HideGhostWhenBuilt()
    {
        CurrentOperation?.FlushLive();
        // полный предпросмотр: расчёт отказал — подпись красная
        if (CurrentOperation is { } cop && ReferenceEquals(cop, _handleOp) && DragValueBox.IsVisible)
            DragValueText.Foreground = cop.HasBuildError || View.GhostBad ? Res("P3.Error") : Res("P3.Text");
        if (!View.GhostShown || View.HandleDragging || CurrentOperation is { Dragging: true }) return;
        if (Session.Document.IsBuilding || Session.Document.AllParts().Any(p => p.IsDirty)) return;
        View.ShowGhost(0, show: false);
    }

    // ================================================================ ссылки и размеры эскиза (п. 2.2–2.4)

    private bool _refsPanel;

    private void AddSketchReference(PickRef r)
    {
        if (View.ActiveSketch is not { } sk || r.Owner is not Part part) return;
        // u13: режим «использовать / сместить / утолщить ребро» — ребро становится основной геометрией эскиза
        if (Commands.SketchEditCommands.OnModelEdge(this, sk, part, r)) { ScheduleTree(); return; }
        var err = SketchReferences.Add(sk, part, r);
        if (err != null) { ShowToast(err, error: false); return; }
        try { sk.Solve(Session.Document.Solver); } catch (Exception ex) { AppLog.Write("sketch solve", ex); }
        View.RefreshDefaultDimensions();
        View.MarkDirty();
        ScheduleTree();
        if (_refsPanel) ShowReferencesPanel();
        else ShowToast(L("Ссылка добавлена: {0}", L(SketchReferences.SourceOf(r).Split(' ')[0]) + " " + SketchReferences.SourceOf(r).Split(' ')[1]), error: false);
    }

    public void ToggleReferencesPanel()
    {
        if (_refsPanel) { _refsPanel = false; PropsPanel.IsVisible = _propsOn; if (_propsOn) ShowProperties(); RefreshToolStates(); return; }
        View.Tool = null;
        ShowReferencesPanel();
    }

    private void ShowReferencesPanel()
    {
        if (View.ActiveSketch is not { } sk) return;
        _refsPanel = true;
        PropsPanel.IsVisible = true;
        PropsTitle.Text = L("ССЫЛКИ ЭСКИЗА");
        PropsBody.Children.Clear();
        PropsButtons.Children.Clear();
        PropsBody.Children.Add(new TextBlock { Text = L("Щёлкайте рёбра и вершины детали (подсвечиваются под курсором): они станут ссылками — к ним привязываются точки и размеры эскиза. Начало координат эскиза — ссылка всегда."), TextWrapping = TextWrapping.Wrap, Classes = { "dim" }, FontSize = 12 });
        var list = SketchReferences.Sources(sk).ToList();
        PropsBody.Children.Add(new TextBlock { Text = L("Начало координат эскиза"), Margin = new Thickness(0, 8, 0, 0) });
        foreach (var src in list)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2) };
            var del = new Button { Content = "×", Classes = { "tool" }, Padding = new Thickness(6, 0), MinHeight = 22 };
            ToolTip.SetTip(del, L("Убрать ссылку"));
            string s0 = src;
            del.Click += (_, _) =>
            {
                SketchReferences.Remove(sk, s0);
                sk.NotifyChanged(sk);
                try { sk.Solve(Session.Document.Solver); } catch { }
                View.RefreshDefaultDimensions();
                View.MarkDirty();
                ShowReferencesPanel();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var parts = src.Split(' ');
            row.Children.Add(new TextBlock { Text = parts.Length == 2 ? L(parts[0]) + " " + parts[1] : src, VerticalAlignment = VerticalAlignment.Center });
            PropsBody.Children.Add(row);
        }
        var close = new Button { Content = L("Готово"), Classes = { "accent" }, MinWidth = 90 };
        close.Click += (_, _) => ToggleReferencesPanel();
        PropsButtons.Children.Add(close);
        RefreshToolStates();
    }

    public bool ReferencesPanelOpen => _refsPanel;

    /// <summary>Двойной щелчок по размеру: новое значение (число или формула) — размер становится управляющим.</summary>
    private void EditDimension(SkDimension d, Point at)
    {
        if (View.ActiveSketch is not { } sk) return;
        if (d.UserLocked) { ShowToast(L("Размер заблокирован — снимите замок (правая кнопка → «Разблокировать»)"), error: false); return; }
        bool angle = d.Type == DimensionType.AngleLineLine;
        var tb = new TextBox { Text = d.Formula ?? d.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), MinWidth = 110 };
        var ok = new Button { Content = L("ОК"), Classes = { "accent" } };
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(4),
            Children = { new TextBlock { Text = angle ? L("Угол, °") : L("Размер, мм"), Classes = { "dim" }, FontSize = 12 }, tb, ok } };
        var fl = new Flyout { Content = panel, Placement = PlacementMode.Pointer };
        void Apply()
        {
            string t = (tb.Text ?? "").Trim();
            double v; string? formula = null;
            if (!double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                var table = Session.Part != null ? Session.Document.ScopeFor(Session.Part) : Session.Document.Parameters;
                if (!table.TryEvaluate(t, out v, out _)) { ShowToast(L("Не число и не формула: {0}", t)); return; }
                formula = t;
            }
            if (v <= 0 && !angle) { ShowToast(L("Размер должен быть больше нуля")); return; }
            double old = d.Value; bool oldLocked = d.Locked, oldDefault = d.IsDefault; string? oldFormula = d.Formula;
            d.Value = v; d.Formula = formula; d.Locked = true; d.IsDefault = false; d.Reference = false;
            var st = sk.Solve(Session.Document.Solver);
            if (st is SolveStatus.Overconstrained or SolveStatus.Inconsistent)
            {
                d.Value = old; d.Formula = oldFormula; d.Locked = oldLocked; d.IsDefault = oldDefault;
                sk.Solve(Session.Document.Solver);
                ShowToast(L("Размер противоречит другим связям эскиза"));
            }
            sk.BumpStructure();
            View.RefreshDefaultDimensions();
            sk.NotifyChanged(sk);
            View.MarkDirty();
            fl.Hide();
        }
        ok.Click += (_, _) => Apply();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } else if (e.Key == Key.Escape) { fl.Hide(); e.Handled = true; } };
        fl.ShowAt(View, showAtPointer: true);
        Dispatcher.UIThread.Post(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Input);
    }

    private static string ShortGl(string info)
    {
        var parts = info.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return info;
        var w = parts[2].Split(' ');
        return w[0] == "OpenGL" && w.Length > 2 && w[1] == "ES" ? $"OpenGL ES {w[2]}" : string.Join(" ", w.Take(2));
    }

    /// <summary>
    /// u12 (замечание 17): меню размера эскиза (правая кнопка): сделать сильным, замок, справочный, стрелки внутри/снаружи;
    /// у радиуса и диаметра — одна стрелка, радиус ↔ диаметр, диаметр как обычный размер.
    /// </summary>
    private void OpenDimensionMenu(SkDimension d)
    {
        if (View.ActiveSketch is not { } sk) return;
        var items = new List<object>();
        void Add(string label, bool enabled, Action act, string? icon = null, bool? check = null)
        {
            var mi = new MenuItem { Header = (check == true ? "✓ " : "") + L(label), IsEnabled = enabled };
            if (icon != null && IconProvider.Get(icon) is { } img) mi.Icon = new Image { Source = img, Width = 16, Height = 16 };
            mi.Click += (_, _) =>
            {
                var before = Model.Sketching.SketchState.Capture(sk);
                act();
                var st = sk.Solve(Session.Document.Solver);
                if (st is SolveStatus.Overconstrained or SolveStatus.Inconsistent)
                {
                    before.RestoreInto(sk); sk.Solve(Session.Document.Solver);
                    ShowToast(L("Размер противоречит другим связям эскиза"));
                }
                sk.BumpStructure();
                View.RefreshDefaultDimensions();
                sk.NotifyChanged(sk);
                View.MarkDirty();
            };
            items.Add(mi);
        }
        bool radial = d.Type is DimensionType.Radius or DimensionType.Diameter;
        Add("Сделать сильным", d.IsDefault || !d.Locked, () => { d.IsDefault = false; d.Locked = true; d.Reference = false; });
        Add(d.UserLocked ? "Разблокировать" : "Заблокировать", !d.Reference, () =>
        {
            d.UserLocked = !d.UserLocked;
            if (d.UserLocked) { d.IsDefault = false; d.Locked = true; }
        }, d.UserLocked ? "dim_lock_ico.svg" : "dim_unlock_ico.svg");
        Add(d.Reference ? "Сделать управляющим" : "Сделать справочным", !d.UserLocked, () =>
        {
            d.Reference = !d.Reference;
            if (d.Reference) { d.Locked = false; d.IsDefault = false; } else { d.Locked = true; }
        }, "dim_reference_but.svg");
        items.Add(new Separator());
        Add("Стрелки снаружи (наружный размер)", true, () => d.Arrows = ArrowPlacement.Outside, d.Type == DimensionType.Diameter ? "dim_diameter_outside_ico.svg" : d.Type == DimensionType.Radius ? "dim_radius_outside_ico.svg" : d.Type == DimensionType.AngleLineLine ? "dim_angle_outside_ico.svg" : "dim_arrows_outside_but.svg", check: d.Arrows == ArrowPlacement.Outside);
        Add("Стрелки внутри (внутренний размер)", true, () => d.Arrows = ArrowPlacement.Inside, check: d.Arrows == ArrowPlacement.Inside);
        Add("Стрелки — автоматически", true, () => d.Arrows = ArrowPlacement.Auto, check: d.Arrows == ArrowPlacement.Auto);
        if (radial)
        {
            items.Add(new Separator());
            Add("Одна стрелка", true, () => d.SingleArrow = !d.SingleArrow, d.Type == DimensionType.Diameter ? "dim_diameter_arrow_ico.svg" : "dim_radius_arrow_ico.svg", check: d.SingleArrow);
            if (d.Type == DimensionType.Radius)
                Add("Сделать диаметром", !d.UserLocked, () => { d.Type = DimensionType.Diameter; d.Value *= 2; d.Formula = d.Formula == null ? null : $"({d.Formula})*2"; });
            else
            {
                Add("Сделать радиусом", !d.UserLocked, () => { d.Type = DimensionType.Radius; d.Value /= 2; d.Formula = d.Formula == null ? null : $"({d.Formula})/2"; d.DiameterAsLinear = false; });
                Add("Показать как размер", true, () => d.DiameterAsLinear = !d.DiameterAsLinear, "dim_diameter_size_ico.svg", check: d.DiameterAsLinear);
            }
        }
        items.Add(new Separator());
        Add("Изменить значение…", !d.UserLocked, () => Dispatcher.UIThread.Post(() => EditDimension(d, default)));
        new ContextMenu { ItemsSource = items }.Open(View);
    }

    private void OpenViewContextMenu()
    {
        var items = new List<object>();
        var ids = View.Selection.IsEmpty
            ? new[] { "view.fit", "view.home", "view.show_all", "op.sketch", "app.parameters" }
            : SuggestedOps().Concat(new[] { "view.hide", "view.isolate", "edit.delete", "app.properties" }).Distinct().ToArray();
        foreach (var id in ids) items.Add(Item(_reg[id]));
        new ContextMenu { ItemsSource = items }.Open(View);
    }

    // ================================================================ полоса выбора и операций (как в Plasticity)

    /// <summary>Операции, подходящие к выбранному (п. 17).</summary>
    public List<string> SuggestedOps()
    {
        var res = new List<string>();
        var sel = View.Selection.Items;
        if (InSketch || sel.Count == 0) return res;
        if (Session.IsAssembly && Session.ActivePart == null)
        {
            // u13: сборка — ограничения и соединения по двум ссылкам, команды компонента
            int geo = sel.Count(r => Commands.AssemblyCommands.GeoOf(r) != null);
            bool axes = sel.Count(r => Commands.AssemblyCommands.GeoOf(r) is { Kind: Commands.AssemblyCommands.GeoKind.Axis }) >= 2;
            bool twoPlanes = sel.Count(r => Commands.AssemblyCommands.GeoOf(r) is { Kind: Commands.AssemblyCommands.GeoKind.Plane }) >= 2;
            if (geo >= 2 && twoPlanes) res.AddRange(new[] { "mate.coincident", "mate.distance", "mate.parallel", "mate.angle", "joint.planar" });
            if (geo >= 2 && axes) res.AddRange(new[] { "mate.concentric", "mate.insert", "joint.pin", "joint.cylindrical", "joint.slider" });
            if (geo >= 2 && !twoPlanes && !axes) res.AddRange(new[] { "mate.coincident", "mate.tangent", "mate.point_on" });
            if (Commands.AssemblyCommands.SelectedComponents(this).Count > 0) res.AddRange(new[] { "as.edit_in_context", "as.fix", "as.open_component", "as.pattern", "as.mirror", "as.replace" });
            return res.Distinct().ToList();
        }
        // u13h: окружение FreeForm — команды FreeForm по выбору
        if (InFreeForm)
        {
            int bodies = sel.Where(r => r.Kind == PickKind.Face && r.Owner is Part bp).Select(r => ((Part)r.Owner!).BodyNameOf(r.Id)).Where(n => n != null).Distinct().Count();
            if (sel.Any(r => r.Target is Feature { FreeForm: not null } and not FreeFormFeature)) res.AddRange(new[] { "ff.step_undo_to", "edit.feature" });
            if (sel.Any(r => r.Kind == PickKind.Face)) res.AddRange(new[] { "ff.push_pull", "ff.move", "ff.delete_face", "ff.draft_face", "ff.shell", "ff.split" });
            if (sel.Any(Refs.IsPlanar)) res.AddRange(new[] { "ff.cplane", "ff.extrude", "ff.align_face" });
            if (bodies >= 2) res.InsertRange(0, new[] { "ff.union", "ff.subtract", "ff.intersect" });
            if (sel.Any(r => r.Kind == PickKind.Edge)) res.AddRange(new[] { "ff.fillet", "ff.chamfer" });
            if (sel.Any(Refs.IsSketch)) res.AddRange(new[] { "ff.extrude", "ff.revolve", "ff.loft" });
            return res.Distinct().ToList();
        }
        bool planar = sel.Any(Refs.IsPlanar), face = sel.Any(r => r.Kind == PickKind.Face);
        bool edge = sel.Any(r => r.Kind == PickKind.Edge), vertex = sel.Any(r => r.Kind == PickKind.Vertex);
        bool sketch = sel.Any(Refs.IsSketch), linear = sel.Any(Refs.IsLinear), axis = sel.Any(r => r.Kind == PickKind.DatumAxis);
        bool feature = sel.Any(r => r.Target is Feature), part = sel.Any(r => r.Kind == PickKind.Body);
        bool curved = sel.Any(r => r.Kind == PickKind.Face && !Refs.IsPlanar(r));
        int planes = sel.Count(Refs.IsPlanar), sketches = sel.Where(Refs.IsSketch).Select(r => Refs.SketchOf(r)!.Id).Distinct().Count();
        bool arc = sel.Any(r => r.Kind == PickKind.Edge && Session.Part?.FindEdge(r.Id)?.Curve is Kernel.Curves.ArcCurve3);
        if (sketch) res.AddRange(new[] { "op.extrude", "op.cut", "op.revolve", "edit.feature" });
        // u13: эскизы — протяжка и переход (два и больше), ребро жёсткости
        if (sketches >= 2) res.AddRange(new[] { "op.loft", "op.sweep" });
        if (sketch) res.Add("op.rib");
        if (planar) res.AddRange(new[] { "op.sketch", "op.extrude", "op.plane", "op.mirror" });
        if (planes >= 2) res.AddRange(new[] { "op.plane_mid", "op.axis_intersect" });
        if (face) res.AddRange(new[] { "op.hole", "op.shell", "op.draft", "op.face_offset" });
        if (curved) res.AddRange(new[] { "op.axis_face", "op.plane_tangent", "op.face_radius", "op.thread" });
        if (edge) res.AddRange(new[] { "op.round", "op.chamfer", "op.round_variable" });
        if (arc) res.Add("op.point_center");
        if (vertex) res.AddRange(new[] { "op.vertex_round", "op.vertex_chamfer" });
        if (linear && !sketch) res.Add("op.pattern_linear");
        if (axis || linear && !sketch) res.Add("op.pattern_circular");
        if (feature) res.AddRange(new[] { "edit.feature", "edit.suppress", "edit.delete" });
        // u13h: операция с отказом — предложенное исправление; узел FreeForm — правка и обновление ссылок
        if (sel.Any(r => r.Target is Feature { Error: not null })) res.Insert(0, "tree.fix");
        if (sel.Any(r => r.Target is FreeFormFeature)) res.InsertRange(0, new[] { "edit.feature", "ff.update_refs" });
        if (face || edge) res.Add("op.edit_in_freeform");
        if (feature && sketch) res.AddRange(new[] { "op.pattern_points", "op.pattern_curve", "op.pattern_fill" });
        if (part) res.AddRange(new[] { "view.isolate", "app.parameters", "app.properties" });
        return res.Distinct().ToList();
    }

    /// <summary>u13: узлы детали без своего элемента — параметры, материал, исполнения, тело.</summary>
    private void OpenInfo(InfoItem ii)
    {
        switch (ii.Role)
        {
            case "params": _reg["app.parameters"].Execute(); break;
            case "material": Commands.DocCommands.MaterialMenu(this, null); break;
            case "configs": _reg["part.configurations"].Execute(); break;
            case "body": View.Selection.Select(new PickRef(ii.Owner.Id, PickKind.Body, ii.Owner.Parent, ii.Owner), additive: false); break;
            case "rollback": Session.Edit(L("Строка отката — в конец"), () => ii.Owner.EndInsertMode(resume: true)); View.MarkDirty(); RebuildTree(); break;
            case "group":
                View.Selection.Clear();
                foreach (var f in ii.Owner.Features.Where(x => x.Group == ii.Name)) View.Selection.Select(new PickRef(f.Id, PickKind.Feature, f.Parent, f), additive: true);
                break;
        }
    }

    /// <summary>u13: ограничение или соединение, выбранное в дереве сборки.</summary>
    public (Part Part, int Index)? SelectedTreeConstraint() =>
        Tree.SelectedItem is TreeNode { Item: InfoItem { Role: "constraint" or "joint" } ii } ? (ii.Owner, ii.Index) : null;

    /// <summary>u13: обновить контекстную панель (после закрепления команд).</summary>
    public void RefreshContext() => UpdateContextBar();

    private void UpdateContextBar()
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: полоса выбора");
        ContextOps.Children.Clear();
        if (InSketch && View.ActiveSketch is { } sk)
        {
            // пп. 9–10: полоса предложений эскиза — только подходящие к выбранному операции (иконками, подпись в подсказке)
            var ops = Commands.SketchSuggest.For(this);
            bool any = !View.Selection.IsEmpty;
            SelChip.Opacity = any ? 1 : 0.75;
            SelChipText.Text = any ? View.Selection.Describe() : L("Эскиз «{0}»: выберите точки, кривые или размер — здесь появятся подходящие операции", sk.Name);
            foreach (var id in ops)
            {
                var b = CmdButton(_reg[id], 20);
                if (b is Layoutable lb) lb.MinWidth = 34;
                ContextOps.Children.Add(b);
            }
            if (any) ContextOps.Children.Add(CmdButton(_reg["edit.clear_selection"], 18));
            ContextBar.IsVisible = true;
            return;
        }
        if (CurrentOperation is { } op)
        {
            SelChipText.Text = L(op.Title) + (ReferenceEquals(op, _handleOp) && op.DragCurrent is { } dv
                ? $" · {(op.DragLabel.Length > 0 ? op.DragLabel + " " : "")}{dv:0.##} {L("мм")}" : "");
            SelChip.Opacity = 1;
            var ok = new Button { Content = L(op.OkLabel), Classes = { "accent" }, MinWidth = 70 };
            ok.Click += (_, _) => CommitOperation();
            var cancel = new Button { Content = L("Отмена"), MinWidth = 70 };
            cancel.Click += (_, _) => CancelOperation();
            ContextOps.Children.Add(ok);
            ContextOps.Children.Add(cancel);
            // u13: режим и направление операции, следующее поле
            foreach (var id in new[] { "ctx.mode_add", "ctx.mode_cut", "ctx.flip", "ctx.both_sides", "ctx.next" })
                if (_reg.Find(id) is { Enabled: true } oc) ContextOps.Children.Add(CmdButton(oc, 18, text: true));
            ContextBar.IsVisible = true;
            return;
        }
        var sel = View.Selection;
        if (sel.IsEmpty)
        {
            SelChipText.Text = L("Выберите грань, ребро, вершину, плоскость или эскиз — здесь появятся подходящие операции");
            SelChip.Opacity = 0.75;
            ContextBar.IsVisible = Session?.Part != null;
            return;
        }
        SelChip.Opacity = 1;
        SelChipText.Text = sel.Describe();
        foreach (var id in SuggestedOps())
        {
            var c = _reg[id];
            var b = CmdButton(c, 18, text: true);
            if (id == "edit.feature" && sel.Items.Any(Refs.IsSketch)) ((ContentControl)b).Content = new TextBlock { Text = L("Изменить эскиз"), FontSize = 12 };
            ContextOps.Children.Add(b);
        }
        // u13: закреплённые и недавние для набора, повысить, похожие, ещё, поиск
        var have = SuggestedOps().ToHashSet();
        foreach (var id in UserPrefs.Current.PinnedCommands)
            if (!have.Contains(id) && _reg.Find(id) is { Enabled: true } pc) { ContextOps.Children.Add(CmdButton(pc, 18, text: true)); have.Add(id); }
        var recentOps = UserPrefs.Current.RecentCommands.Where(id => id.StartsWith("op.") && !have.Contains(id)).Select(_reg.Find).OfType<AppCommand>().Where(c => c.Enabled).Take(3).ToList();
        if (recentOps.Count > 0)
        {
            ContextOps.Children.Add(IconProvider.Get("recent_ops_ico.svg") is { } ri
                ? new Image { Source = ri, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) }
                : new TextBlock { Text = "⟲", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), Classes = { "dim" } });
            ToolTip.SetTip(ContextOps.Children[^1], L("Недавние для набора"));
            foreach (var c in recentOps) ContextOps.Children.Add(CmdButton(c, 18, text: true));
        }
        foreach (var id in new[] { "ctx.up", "ctx.similar", "ctx.more", "ctx.search", "ctx.pin" })
            if (_reg.Find(id) is { Enabled: true } xc)
            {
                var xb = CmdButton(xc, 18, text: id == "ctx.more");
                ContextOps.Children.Add(xb);
            }
        if (_reg["view.hide"].Enabled) ContextOps.Children.Add(CmdButton(_reg["view.hide"], 18, text: true));
        var clear = CmdButton(_reg["edit.clear_selection"], 18);
        ContextOps.Children.Add(clear);
        ContextBar.IsVisible = true;
    }

    // ================================================================ операции

    /// <summary>Выдавливание или вращение без эскиза, но с выбранной плоскостью — сначала эскиз на ней, потом операция.</summary>
    public Operation SketchFirst(Operation op)
    {
        var sel = View.Selection.Items;
        if (sel.Any(Refs.IsSketch) || !sel.Any(Refs.IsPlanar)) return op;
        return op switch
        {
            ExtrudeOp e => new SketchOp { Then = sk => new ExtrudeOp { SeedSketch = sk, StartCut = e.StartCut } },
            RevolveOp => new SketchOp { Then = sk => new RevolveOp { SeedSketch = sk } },
            _ => op,
        };
    }

    public void StartOperation(Operation op) => StartOperation(op, showPanel: true);

    /// <summary>showPanel=false — панель только если панель свойств уже открыта (операция ручкой, п. 6).</summary>
    public void StartOperation(Operation op, bool showPanel)
    {
        if (CurrentOperation != null) CancelOperation();
        var part = Session.Part;
        if (part == null) { ShowToast(L("Нет детали")); return; }
        bool panel;
        try { panel = op.Begin(this, part); }
        catch (Exception ex) { AppLog.Write("operation start", ex); ShowToast(L(op.Title) + ": " + ex.Message); return; }
        if (!panel) { RefreshAfterOp(); return; }
        CurrentOperation = op;
        _opQuiet = !showPanel;
        // операция не от ручки — ручка у выбора убирается (её операция — другая)
        if (!_startingHandleOp) { View.Handle = null; _handleOp = null; _handleOpFixed = false; }
        PropsPanel.IsVisible = showPanel || _propsOn;
        PropsTitle.Text = L(op.Title).ToUpperInvariant();
        PropsBody.Children.Clear();
        PropsButtons.Children.Clear();
        var p = new OpPanel(PropsBody, op);
        try
        {
            op.Build(p);
            p.Finish();
            op.BindPanel(p);
            op.Prime();
            op.AttachPanel(p);
        }
        catch (Exception ex)
        {
            AppLog.Write("operation " + op.Title, ex);
            ShowToast(L(op.Title) + ": " + ex.Message);
        }
        var ok = new Button { Content = L(op.OkLabel), Classes = { "accent" }, MinWidth = 90 };
        ok.Click += (_, _) => CommitOperation();
        var cancel = new Button { Content = L("Отмена"), MinWidth = 90 };
        cancel.Click += (_, _) => CancelOperation();
        PropsButtons.Children.Add(ok);
        PropsButtons.Children.Add(cancel);
        View.MarkDirty();
        RefreshAfterOp();
        if (!_startingHandleOp) AttachOpHandle();
    }

    public void CommitOperation()
    {
        if (CurrentOperation is not { } op) return;
        bool done;
        try { done = op.Commit(); }
        catch (Exception ex) { AppLog.Write("operation commit", ex); ShowToast(L(op.Title) + ": " + ex.Message); return; }
        if (!done) return;
        if (ReferenceEquals(CurrentOperation, op)) EndOperation();
    }

    public void CancelOperation()
    {
        if (CurrentOperation is not { } op) return;
        CurrentOperation = null;
        try { op.Cancel(); }
        catch (Exception ex) { AppLog.Write("operation cancel", ex); }
        EndOperation();
    }

    private void EndOperation()
    {
        CurrentOperation = null;
        _handleOp = null; _handleOpFixed = false;
        _opQuiet = false;
        DragValueBox.IsVisible = false;
        View.Handle = null;
        View.Selection.Filter = InSketch ? PickFilter.Sketch : PickFilter.All;
        if (!InSketch) View.Selection.Clear();
        HideOpPanel();
        View.MarkDirty();
        RefreshAfterOp();
    }

    private void HideOpPanel()
    {
        PropsButtons.Children.Clear();
        PropsPanel.IsVisible = _propsOn;
        if (_propsOn) ShowProperties();
    }

    private void RefreshAfterOp()
    {
        _reg.RefreshAll();
        BuildCommandBar();
        UpdateContextBar();
        UpdateStatus();
        RefreshToolStates();
    }

    /// <summary>Изменить выбранное: операцию — в панели, эскиз — в режиме эскиза.</summary>
    public void EditSelected()
    {
        var item = SelectedItems().FirstOrDefault(i => i is Feature or Sketch);
        if (item == null)
        {
            var sk = View.Selection.Items.Select(Refs.SketchOf).FirstOrDefault(s => s != null);
            if (sk != null) item = sk;
        }
        EditItem(item);
    }

    private void EditItem(ModelItem? item)
    {
        switch (item)
        {
            case Sketch sk: EditSketch(sk); break;
            case FreeFormFeature ffn when Session.Part is { } ffp: Commands.FreeFormCommands.Enter(this, ffp, ffn); break;
            case Feature { Frozen: true }:
                ShowToast(L("Шаг FreeForm зафиксирован: параметры вернуть — «Отменить до этого шага»"), error: false);
                break;
            case Feature f:
                if (OperationFactory.ForEdit(f) is { } op) StartOperation(op);
                else ShowToast(L("Правка этой операции в панели пока не поддерживается"), error: false);
                break;
        }
    }

    // ================================================================ эскиз

    public void BeginNewSketch(Part part, Frame3 frame, uint sourceId, string planeName, Func<Sketch, Operation>? then)
    {
        Session.BeginEdit(L("Эскиз"));
        var sk = new Sketch(L("Эскиз{0}", part.Sketches.Count() + 1), frame);
        sk.SetBasePlane(frame, sourceId, planeName);
        SketchFaceRef.AttachById(sk, part.Bodies, sourceId);
        part.Add(sk);
        _sketchIsNew = true;
        _afterSketch = then;
        EnterSketchUi(sk);
    }

    public void EditSketch(Sketch sk)
    {
        if (CurrentOperation != null) CancelOperation();
        if (InSketch) FinishSketch();
        Session.BeginEdit(L("Изменить эскиз «{0}»", sk.Name));
        _sketchIsNew = false;
        _afterSketch = null;
        EnterSketchUi(sk);
    }

    private void EnterSketchUi(Sketch sk)
    {
        ConstructionMode = false;
        View.EnterSketch(sk);
        View.Tool = new LineTool();
        RebuildUi();
        ScheduleTree();
    }

    public void SetTool(ISketchTool t)
    {
        if (!InSketch) return;
        Commands.SketchEditCommands.Mode = Commands.SketchEditCommands.EdgeMode.None;
        if (ConstructionMode && t.Role == SkRole.Object) t.Role = SkRole.Construction;
        View.Tool = t;
        RefreshToolStates();
    }

    public void ToggleConstruction()
    {
        ConstructionMode = !ConstructionMode;
        if (View.Tool is { } t && t.Role != SkRole.Centerline) t.Role = ConstructionMode ? SkRole.Construction : SkRole.Object;
        RefreshToolStates();
    }

    public void FinishSketch()
    {
        Commands.SketchEditCommands.Mode = Commands.SketchEditCommands.EdgeMode.None;
        if (View.ActiveSketch is not { } sk) return;
        View.ToolFinish();
        View.ExitSketch();
        _refsPanel = false;
        bool empty = sk.Entities.All(e => e.Role == SkRole.Reference);
        if (empty && _sketchIsNew && sk.Parent is Part owner) { owner.Remove(sk); Session.DiscardEdit(); }
        else
        {
            sk.NotifyChanged(sk);
            Session.CommitEdit();
        }
        ShowHint(null);
        RebuildUi();
        ScheduleTree();
        var then = _afterSketch;
        _afterSketch = null;
        if (!empty && then != null)
        {
            // эскиз из снимка после отмены — тот же объект в детали (правка не отменялась)
            View.Selection.Clear();
            StartOperation(then(sk));
        }
    }

    public void CancelSketch()
    {
        if (!InSketch) return;
        var sk = View.ActiveSketch!;
        _refsPanel = false;
        View.ExitSketch();
        // новый эскиз — просто убрать (деталь не заменяется); правка — снимок до неё (вид держит прежнюю деталь)
        if (_sketchIsNew && sk.Parent is Part owner) { owner.Remove(sk); Session.DiscardEdit(); }
        else Session.CancelEdit();
        _afterSketch = null;
        ShowHint(null);
        RebuildUi();
        ScheduleTree();
    }

    // ================================================================ дерево

    private void WireTree()
    {
        Tree.SelectionChanged += (_, _) =>
        {
            if (_syncingTree) return;
            if (Tree.SelectedItem is TreeNode n) SelectItem(n.Item);
        };
        Tree.AddHandler(PointerMovedEvent, (_, e) =>
        {
            var src = e.Source as Visual;
            var node = (src as StyledElement)?.DataContext as TreeNode;
            View.HighlightFromTree(node?.Item);
        }, RoutingStrategies.Tunnel);
        Tree.PointerExited += (_, _) => View.HighlightFromTree(null);
        Tree.DoubleTapped += (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is not TreeNode n) return;
            if (n.Item is Feature or Sketch) EditItem(n.Item);
            else if (n.Item is Part) View.ZoomToFit();
            else if (n.Item is InfoItem ii) OpenInfo(ii);
        };
        Tree.ContextRequested += (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is not TreeNode n) return;
            SelectItem(n.Item);
            var ids = new List<string>();
            if (n.Item is Feature or Sketch) ids.Add("edit.feature");
            if (n.Item is Feature) ids.AddRange(new[] { "edit.suppress", "tree.resume", "tree.edit_dims", "tree.insert_here", "tree.reorder_up", "tree.reorder_down", "tree.group", "tree.parents_children", "tree.replace_refs" });
            if (n.Item is Sketch) ids.Add("tree.replace_refs");
            ids.AddRange(SuggestedOps().Where(i => i != "edit.feature" && i != "edit.suppress"));
            ids.AddRange(new[] { "edit.rename", "edit.copy", "edit.paste", "view.hide", "view.isolate", "edit.delete" });
            if (n.Item is Part) ids.AddRange(new[] { "part.material", "part.units", "part.configurations", "an.mass", "an.check_body" });
            var items = ids.Distinct().Where(id => _reg.Find(id) != null).Select(id => (object)Item(_reg[id])).ToList();
            new ContextMenu { ItemsSource = items }.Open((Control)e.Source!);
            e.Handled = true;
        };
    }

    private void ScheduleTree()
    {
        if (_treePending) return;
        _treePending = true;
        Dispatcher.UIThread.Post(() => { _treePending = false; RebuildTree(); }, DispatcherPriority.Background);
    }

    public void RebuildTree()
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: дерево");
        // состояние «развёрнут» живёт между перестройками (п. 13): глаз эскиза не сворачивает ветку
        foreach (var n in AllNodes) _expand[n.Item.Id] = n.IsExpanded;
        _roots = TreeNode.Build(Session.Document, Session.IsAssembly);
        foreach (var n in _roots.SelectMany(r => r.SelfAndDescendants()))
            if (_expand.TryGetValue(n.Item.Id, out bool ex)) n.RestoreExpanded(ex);
        _syncingTree = true;
        Tree.ItemsSource = _roots;
        _syncingTree = false;
        SyncTreeFromSelection();
    }

    public IEnumerable<TreeNode> AllNodes => _roots.SelectMany(r => r.SelfAndDescendants());

    /// <summary>Свернуть или развернуть всё, включая деталь (п. 5).</summary>
    public void SetTreeExpanded(bool expanded)
    {
        foreach (var n in AllNodes.ToList())
            if (n.Children.Count > 0) { n.IsExpanded = expanded; _expand[n.Item.Id] = expanded; }
    }

    /// <summary>Выбор элемента модели (из дерева): в выделение вида.</summary>
    public void SelectItem(ModelItem item)
    {
        PickRef r = item switch
        {
            Assembly a => new PickRef(a.Id, PickKind.Assembly, a.Parent, a),
            Part p => new PickRef(p.Id, PickKind.Body, p.Parent, p),
            Sketch sk => new PickRef(sk.Id, PickKind.SketchCurve, sk.Parent, sk),
            Feature f => new PickRef(f.Id, PickKind.Feature, f.Parent, f),
            DatumPlane dp => new PickRef(dp.Id, PickKind.DatumPlane, dp.Parent, dp),
            DatumAxis ax => new PickRef(ax.Id, PickKind.DatumAxis, ax.Parent, ax),
            DatumPoint pt => new PickRef(pt.Id, PickKind.DatumPoint, pt.Parent, pt),
            Line3DItem ln => new PickRef(ln.Id, PickKind.DatumAxis, ln.Parent, ln),
            _ => PickRef.Empty
        };
        if (r.IsEmpty) { View.Selection.Clear(); return; }
        // в операции выбор из дерева добавляется в активное поле (если подходит)
        if (CurrentOperation?.ActiveRef is { } f2 && !f2.Single && f2.Accept(r)) View.Selection.Select(r, additive: true);
        else View.Selection.Select(r, additive: false);
    }

    private void SyncTreeFromSelection()
    {
        var prim = View.Selection.Primary;
        TreeNode? node = prim.IsEmpty ? null : AllNodes.FirstOrDefault(n => n.Item.Id == prim.Id || ReferenceEquals(n.Item, prim.Target));
        _syncingTree = true;
        Tree.SelectedItem = node;
        _syncingTree = false;
    }

    /// <summary>Выбранные элементы модели (из выделения вида).</summary>
    public List<ModelItem> SelectedItems()
    {
        var res = new List<ModelItem>();
        foreach (var r in View.Selection.Items)
        {
            if (r.Target is ModelItem mi) { res.Add(mi); continue; }
            if (r.Kind is PickKind.Face or PickKind.Edge or PickKind.Vertex) continue;
            var it = Session.Document.Root.Descendants().FirstOrDefault(x => x.Id == r.Id);
            if (it != null) res.Add(it);
        }
        return res;
    }

    // ================================================================ свойства (повторный щелчок — скрыть, п. 16)

    public void ToggleProperties()
    {
        _propsOn = !_propsOn;
        if (CurrentOperation == null)
        {
            PropsPanel.IsVisible = _propsOn;
            if (_propsOn) ShowProperties();
        }
        else
        {
            // u12 (замечание 6): во время операции кнопка «Свойства» прячет и показывает панель операции (прежде — только у
            // операции ручкой; у начатой командой панель не уходила)
            _propsOn = !PropsPanel.IsVisible;
            PropsPanel.IsVisible = _propsOn;
            if (!_propsOn) DragValueBox.IsVisible = false;
        }
        RefreshToolStates();
    }

    public void ShowProperties()
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: свойства");
        PropsBody.Children.Clear();
        PropsButtons.Children.Clear();
        var sel = View.Selection;
        var part = Session.Part;
        if (sel.IsEmpty && part != null)
        {
            PropsTitle.Text = L("ДЕТАЛЬ");
            Row("Имя", part.Name);
            MaterialRow(part);
            Row("Тел", part.Bodies.Count.ToString());
            if (part.Bodies.Count > 0)
            {
                Row("Объём", $"{part.Volume / 1000.0:0.###} " + L("см³"));
                Row("Площадь", $"{part.Area / 100.0:0.###} " + L("см²"));
                Row("Масса", $"{part.Mass:0.####} " + L("кг"));
            }
            Row("Операций", part.Children.OfType<Feature>().Count().ToString());
            Row("Параметров", $"{Session.Document.Parameters.Count} + {part.LocalParameters.Count}");
            return;
        }
        PropsTitle.Text = L("ВЫБРАНО");
        Row("Выбрано", sel.Count.ToString());
        foreach (var r in sel.Items.Take(12))
        {
            Row(Selection.KindName(r), Selection.Label(r));
            if (r.Target is Feature f)
            {
                Row("Описание", f.Summary);
                if (f.Error != null) Row("Ошибка", f.Error);
            }
        }
    }

    /// <summary>u10: материал детали — выбор из библиотеки (группы), свойства для массы и расчёта — подсказкой.</summary>
    private void MaterialRow(Part part)
    {
        var lib = Material.Library();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        g.Children.Add(new TextBlock { Text = L("Материал"), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420 };
        var items = new List<object>();
        string? grp = null;
        int sel = -1;
        foreach (var m in lib)
        {
            if (m.Group != grp) { grp = m.Group; items.Add(new ComboBoxItem { Content = L(grp), IsEnabled = false, FontWeight = FontWeight.SemiBold }); }
            if (part.Material?.Name == m.Name) sel = items.Count;
            items.Add(new ComboBoxItem { Content = L(m.Name), Tag = m.Name });
        }
        if (sel < 0 && part.Material != null) { sel = items.Count; items.Add(new ComboBoxItem { Content = part.Material.Name, Tag = part.Material.Name }); }
        cb.ItemsSource = items;
        cb.SelectedIndex = sel;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is not ComboBoxItem { Tag: string name } || Material.FromLibrary(name) is not { } m || part.Material?.Name == name) return;
            part.Material = m;
            Session.MarkModified();
            View.MarkDirty();
            Dispatcher.UIThread.Post(ShowProperties);
        };
        if (part.Material is { } pm)
            ToolTip.SetTip(cb, $"{pm.Designation}\n" + L("Плотность") + $" {pm.DensityGPerCm3:0.###} " + L("г/см³") +
                               $"\nE {pm.YoungModulus / 1e9:0.#} GPa, ν {pm.PoissonRatio:0.##}" +
                               (pm.YieldStrength > 0 ? $"\nσт {pm.YieldStrength / 1e6:0} MPa" : "") + (pm.UltimateStrength > 0 ? $", σв {pm.UltimateStrength / 1e6:0} MPa" : ""));
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        PropsBody.Children.Add(g);
        if (part.Material is { } mm)
        {
            Row("Плотность", $"{mm.DensityGPerCm3:0.###} " + L("г/см³"));
            if (mm.YoungModulus > 0) Row("Модуль Юнга", $"{mm.YoungModulus / 1e9:0.#} " + L("ГПа") + $", ν {mm.PoissonRatio:0.##}");
            if (mm.YieldStrength > 0 || mm.UltimateStrength > 0) Row("Прочность", (mm.YieldStrength > 0 ? $"σт {mm.YieldStrength / 1e6:0} " : "") + (mm.UltimateStrength > 0 ? $"σв {mm.UltimateStrength / 1e6:0} " : "") + L("МПа"));
        }
    }

    private void Row(string k, string v)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        g.Children.Add(new TextBlock { Text = L(k), Classes = { "dim" }, TextWrapping = TextWrapping.Wrap });
        var t = new SelectableTextBlock { Text = v, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        PropsBody.Children.Add(g);
    }

    // ================================================================ параметры (п. 10)

    public async Task OpenParameters()
    {
        ModelItem? start = SelectedItems().FirstOrDefault(i => i is Part or Assembly) ?? (ModelItem?)Session.Part;
        var dlg = new ParametersDialog(Session, start);
        await dlg.ShowDialog(this);
        if (dlg.Applied) { View.MarkDirty(); UpdateStatus(); }
    }

    // ================================================================ тест (замер скорости и отчёт)

    public async Task OpenSelfTest()
    {
        if (CurrentOperation != null) CancelOperation();
        if (InSketch) CancelSketch();
        await new SelfTestWindow(this).ShowDialog(this);
    }

    // ================================================================ строка состояния и сообщения

    private string _hoverText = "";

    public void UpdateStatus(string? hover = null)
    {
        if (hover != null) _hoverText = hover;
        var part = Session?.Part;
        StatusMode.Text = InSketch ? L("Эскиз «{0}»", View.ActiveSketch!.Name)
            : CurrentOperation != null ? L(CurrentOperation.Title)
            : part != null ? L("Деталь «{0}»", part.Name) : L("Сборка");
        string sel = View.Selection.IsEmpty ? "" : L("Выбрано: ") + View.Selection.Describe();
        StatusSel.Text = string.IsNullOrEmpty(_hoverText) ? sel : (sel.Length > 0 ? sel + "   ·   " : "") + L("Под курсором: ") + _hoverText;
        StatusUnits.Text = L(Commands.DisplayUnits.LengthUnit);
        EnvText.Text = InSketch ? L("Эскиз") : CurrentOperation != null ? L("Операция") : InFreeForm ? L("FreeForm") : Session?.IsAssembly == true ? (Session.ActivePart != null ? L("Компонент «{0}»", Session.ActivePart.Name) : L("Сборка")) : L("Деталь");
        if (Session == null) return;
        int errors = Session.Document.Root.Descendants().Count(i => i.Error != null);
        // u13: значки состояния — идёт перестроение, требуют внимания, годно
        if (Session.Document.IsBuilding) { StatusBuild.Text = "⟳ " + L("Перестроение…"); StatusBuild.Foreground = Res("P3.Warning"); }
        else if (errors > 0) { StatusBuild.Text = "! " + L("Требуют внимания: {0}", errors); StatusBuild.Foreground = Res("P3.Error"); }
        else if (part != null && part.Bodies.Count > 0) { StatusBuild.Text = "✓ " + L("Построено"); StatusBuild.Foreground = Res("P3.TextDim"); }
        else StatusBuild.Text = "";
    }

    public IBrush Res(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    public void ShowToast(string text, bool error = true)
    {
        ToastText.Text = text;
        Toast.BorderBrush = Res(error ? "P3.Error" : "P3.Border");
        Toast.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 8 : 4) };
        _toastTimer.Tick += (_, _) => { Toast.IsVisible = false; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    public void ShowHint(string? text)
    {
        HintBox.IsVisible = !string.IsNullOrEmpty(text);
        HintText.Text = text ?? "";
    }

    // ================================================================ журнал отмены (UI-37)

    public void OpenHistory(Control? anchor = null)
    {
        var items = new List<object>();
        var names = Session.UndoNames;
        if (names.Count == 0) items.Add(new MenuItem { Header = L("Журнал пуст"), IsEnabled = false });
        for (int i = names.Count - 1; i >= 0; i--)
        {
            int steps = names.Count - i;
            var mi = new MenuItem { Header = $"{i + 1}. {names[i]}" };
            ToolTip.SetTip(mi, steps == 1 ? L("Отменить этот шаг") : L("Отменить {0} шагов — до состояния перед этим", steps));
            mi.Click += (_, _) => Session.UndoTo(steps);
            items.Add(mi);
        }
        if (Session.RedoNames.Count > 0)
        {
            items.Add(new Separator());
            for (int i = Session.RedoNames.Count - 1; i >= 0; i--)
                items.Add(new MenuItem { Header = "↷ " + Session.RedoNames[i], IsEnabled = false });
        }
        var target = anchor ?? EditPanel.Children.OfType<Control>().LastOrDefault() ?? (Control)this;
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight }.Open(target);
    }

    // ================================================================ клавиатура

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (PaletteShade.IsVisible) return;
        bool inText = FocusManager?.GetFocusedElement() is TextBox;
        if (!inText && e.KeyModifiers == KeyModifiers.None && e.Key == Key.F) { OpenPalette(); e.Handled = true; return; }
        if (e.Key == Key.Escape && !inText)
        {
            e.Handled = true;
            if (Toast.IsVisible) { Toast.IsVisible = false; return; }
            if (InSketch && Commands.SketchEditCommands.Mode != Commands.SketchEditCommands.EdgeMode.None) { Commands.SketchEditCommands.Mode = Commands.SketchEditCommands.EdgeMode.None; return; }
            if (InSketch && View.Tool != null) { View.Tool = null; return; }
            if (CurrentOperation != null) { CancelOperation(); return; }
            if (!View.Selection.IsEmpty) { View.Selection.Clear(); return; }
            if (InSketch) return;
            if (_propsOn) { ToggleProperties(); return; }
            return;
        }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && !inText)
        {
            if (InSketch && View.Tool != null) { View.ToolFinish(); e.Handled = true; return; }
            if (CurrentOperation != null) { CommitOperation(); e.Handled = true; return; }
        }
        if (inText) return;
        var c = _reg.ByGesture(e);
        if (c != null) { c.Execute(); e.Handled = true; }
    }

    // ================================================================ палитра команд (F)

    private void WirePalette()
    {
        PaletteInput.TextChanged += (_, _) => FillPalette(PaletteInput.Text ?? "");
        PaletteInput.KeyDown += (_, e) =>
        {
            int n = PaletteList.ItemCount;
            if (e.Key == Key.Down) { PaletteList.SelectedIndex = Math.Min(n - 1, PaletteList.SelectedIndex + 1); PaletteList.ScrollIntoView(PaletteList.SelectedIndex); e.Handled = true; }
            else if (e.Key == Key.Up) { PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1); PaletteList.ScrollIntoView(PaletteList.SelectedIndex); e.Handled = true; }
            else if (e.Key == Key.Enter) { RunPaletteSelection(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ClosePalette(); e.Handled = true; }
        };
        PaletteList.DoubleTapped += (_, _) => RunPaletteSelection();
        PaletteShade.PointerPressed += (_, e) => { if (e.Source == PaletteShade) ClosePalette(); };
    }

    public void OpenPalette()
    {
        PaletteShade.IsVisible = true;
        PaletteInput.Text = "";
        FillPalette("");
        Dispatcher.UIThread.Post(() => PaletteInput.Focus(), DispatcherPriority.Input);
    }

    public void ClosePalette()
    {
        PaletteShade.IsVisible = false;
        View.Focus();
    }

    private List<AppCommand> _paletteItems = new();

    private void FillPalette(string q)
    {
        var found = _reg.Search(q).Where(c => c.Id != "palette").ToList();
        if (string.IsNullOrWhiteSpace(q))
        {
            var recent = UserPrefs.Current.RecentCommands.Select(_reg.Find).OfType<AppCommand>().ToList();
            found = recent.Concat(found.Except(recent)).ToList();
        }
        _paletteItems = found;
        PaletteList.ItemsSource = found.Select(PaletteRow).ToList();
        PaletteList.SelectedIndex = found.Count > 0 ? 0 : -1;
    }

    private Control PaletteRow(AppCommand c)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto,Auto"), Opacity = c.Enabled ? 1 : 0.45 };
        if (IconProvider.Get(c.Icon) is { } img) g.Children.Add(new Image { Source = img, Width = 18, Height = 18 });
        var t = new TextBlock { Text = c.Text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 1); g.Children.Add(t);
        var grp = new TextBlock { Text = L(c.Group), Classes = { "dim" }, FontSize = 11, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(grp, 2); g.Children.Add(grp);
        if (c.Gesture != null)
        {
            var k = new TextBlock { Text = AppCommand.Format(c.Gesture), Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(k, 3); g.Children.Add(k);
        }
        ToolTip.SetTip(g, c.Description != null ? L(c.Description) : c.Text);
        return g;
    }

    private void RunPaletteSelection()
    {
        int i = PaletteList.SelectedIndex;
        if (i < 0 || i >= _paletteItems.Count) return;
        var c = _paletteItems[i];
        ClosePalette();
        c.Execute();
    }
}
