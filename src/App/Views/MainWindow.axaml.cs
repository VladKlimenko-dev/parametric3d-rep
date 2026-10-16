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
    public bool ConstructionMode { get; private set; }
    private bool _sketchIsNew;
    private Func<Sketch, Operation>? _afterSketch;
    private bool _propsOn;
    public bool PropertiesVisible => _propsOn;

    public MainWindow() : this(DocumentSession.NewPart()) { }

    public MainWindow(DocumentSession session)
    {
        InitializeComponent();
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
        s.Replaced += OnReplaced;
        s.StateChanged += OnStateChanged;
        s.Document.BuildStarted += OnBuild;
        s.Document.BuildFinished += OnBuild;
        s.Document.Root.Changed += _ => ScheduleTree();
        _expand.Clear();
        View.Document = s.Document;
        View.ActivePart = s.Part;
        View.DarkTheme = _dark;
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
        RebuildTree();
        UpdateContextBar();
    }

    private void OnBuild() => Dispatcher.UIThread.Post(() => { UpdateStatus(); ScheduleTree(); View.MarkDirty(); });

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
                Title = L("Сохранить деталь"),
                SuggestedFileName = Session.Title + DocumentSession.PartExtension,
                DefaultExtension = DocumentSession.PartExtension,
                FileTypeChoices = new[] { new FilePickerFileType(L("Деталь Parametric3D")) { Patterns = new[] { "*" + DocumentSession.PartExtension } } },
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
                FileTypeFilter = new[] { new FilePickerFileType(L("Деталь Parametric3D")) { Patterns = new[] { "*" + DocumentSession.PartExtension } } },
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
        foreach (var id in new[] { "edit.undo", "edit.redo", "edit.history" }) EditPanel.Children.Add(CmdButton(_reg[id]));

        RightPanel.Children.Clear();
        RightPanel.Children.Add(CmdButton(_reg["app.selftest"]));
        RightPanel.Children.Add(CmdButton(_reg["app.parameters"]));
        RightPanel.Children.Add(CmdButton(_reg["app.properties"]));
        RightPanel.Children.Add(Separator(true));
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
        _projButton = new Button { Classes = { "tool" }, MinWidth = 64 };
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
        _datumsButton = new ToggleButton { Classes = { "tool" }, Content = new TextBlock { Text = L("Опорные"), FontSize = 12 }, Padding = new Thickness(8, 4, 4, 4) };
        _datumsButton.Click += (_, _) => { ToggleDatums(); };
        ToolTip.SetTip(_datumsButton, _reg["view.datums"].Tooltip);
        var arrow = new Button { Classes = { "tool" }, Content = new TextBlock { Text = "▾", FontSize = 12 }, MinWidth = 18, Padding = new Thickness(3, 4) };
        ToolTip.SetTip(arrow, L("Что показывать"));
        arrow.Click += (_, _) => OpenShowFlyout(arrow);
        ViewTools.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { _datumsButton, arrow } });
        _datumsButton.IsChecked = _reg["view.datums"].IsChecked!();
    }

    private void UpdateProjectionButton(bool perspective)
    {
        if (_projButton == null) return;
        _projButton.Content = new TextBlock { Text = perspective ? L("Персп.") : L("Орто"), FontSize = 12 };
    }

    private static readonly (DisplayStyle Style, string Id)[] Styles =
    {
        (DisplayStyle.Shaded, "style.shaded"), (DisplayStyle.ShadedWithEdges, "style.shaded_edges"), (DisplayStyle.Wireframe, "style.wireframe"),
        (DisplayStyle.HiddenLines, "style.hidden_lines"), (DisplayStyle.Transparent, "style.transparent"),
    };

    private void UpdateStyleButton()
    {
        if (_styleButton == null) return;
        var c = _reg[Styles.First(s => s.Style == View.Style).Id];
        _styleButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { new TextBlock { Text = c.Text, FontSize = 12 }, new TextBlock { Text = "▾", FontSize = 11, Classes = { "dim" } } },
        };
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

    /// <summary>Полоса команд слева: операции модели или инструменты эскиза (п. 3, 9).</summary>
    private void BuildCommandBar()
    {
        CommandStack.Children.Clear();
        string[][] groups = InSketch
            ? new[]
            {
                new[] { "sk.line", "sk.rect", "sk.circle", "sk.circle3", "sk.arc3", "sk.arc_center", "sk.ellipse", "sk.spline", "sk.point", "sk.centerline" },
                new[] { "sk.construction", "sk.grid", "sk.references" },
                new[] { "sk.finish", "sk.cancel" },
            }
            : new[]
            {
                new[] { "op.sketch" },
                new[] { "op.extrude", "op.cut", "op.revolve" },
                new[] { "op.hole", "op.round", "op.chamfer", "op.vertex_round", "op.shell" },
                new[] { "op.plane" },
                new[] { "op.mirror", "op.pattern_linear", "op.pattern_circular" },
            };
        CommandStack.Children.Add(new TextBlock
        {
            Text = InSketch ? L("ЭСКИЗ") : L("ОПЕРАЦИИ"), Classes = { "header" }, Margin = new Thickness(6, 2, 6, 4),
        });
        for (int g = 0; g < groups.Length; g++)
        {
            if (g > 0) CommandStack.Children.Add(Separator(false));
            foreach (var id in groups[g]) CommandStack.Children.Add(CmdButton(_reg[id], 20, text: true, minWidth: 128));
        }
    }

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
        foreach (var id in new[] { "file.new_part", "file.sample", "file.open" }) items.Add(Item(_reg[id]));
        var recent = new MenuItem { Header = L("Последние файлы") };
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
        foreach (var id in new[] { "file.save", "file.save_as" }) items.Add(Item(_reg[id]));
        items.Add(new Separator());
        foreach (var id in new[] { "file.import", "file.export" }) items.Add(Item(_reg[id]));
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
        View.ContextRequested += _ => OpenViewContextMenu();
        View.SceneRebuilt += () => { CurrentOperation?.RefreshStatus(); UpdateStatus(); if (_propsOn && CurrentOperation == null) ShowProperties(); };
        View.ToolChanged += () => { ShowHint(View.Tool != null ? L(View.Tool.Hint) : InSketch ? L("Выберите инструмент слева; Ctrl+Enter — закончить эскиз") : null); RefreshToolStates(); };
        View.SketchEdited += () => { ScheduleTree(); UpdateStatus(); };
        View.ExpandEdge = r => r.Owner is Part p ? Topo.ChainRefs(p, r) : new List<PickRef> { r };
        View.ReferencePicked += AddSketchReference;
        View.DimensionEditRequested += EditDimension;
        View.HandleDragStarted += OnHandleDragStarted;
        View.HandleDragged += v => CurrentOperation?.SetDragValue(v);
        View.GlReady += info => Dispatcher.UIThread.Post(() =>
        {
            StatusGl.Text = ShortGl(info);
            ToolTip.SetTip(StatusGl, info);
        });
        Dispatcher.UIThread.Post(() => { if (View.GlError != null) ShowToast("OpenGL: " + View.GlError); }, DispatcherPriority.Background);
    }

    private PickRef _lastPrimary;

    private void OnSelectionChanged()
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: выбор изменился");
        CurrentOperation?.SelectionChanged(View.Selection.Items);
        var prim = View.Selection.Primary;
        if (prim.Id != _lastPrimary.Id || View.Selection.IsEmpty)
        {
            _lastPrimary = prim;
            SyncTreeFromSelection();
        }
        UpdateStatus();
        UpdateContextBar();
        if (CurrentOperation == null) UpdateHandle();
        if (_propsOn && CurrentOperation == null) ShowProperties();
    }

    // ================================================================ ручки операций (п. 6)

    private string _handleKind = "";

    /// <summary>Ручка у выбранного: ребро (с цепочкой), вершина, плоская грань, поверхность.</summary>
    private void UpdateHandle()
    {
        View.Handle = null;
        _handleKind = "";
        if (InSketch) return;
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
                ShowHint(L("Ручка: наружу — фаска, внутрь — скругление"));
                break;
            case PickKind.Vertex when sel.Count == 1 && Topo.VertexHandle(part, sel[0].Id) is { } h:
                View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
                _handleKind = "vertex";
                ShowHint(L("Ручка: наружу — фаска угла, внутрь — скругление вершины"));
                break;
            case PickKind.Face when sel.Count == 1 && Topo.FaceHandle(part, sel[0].Id) is { } h:
                View.Handle = new OpHandle(F(h.At), F(h.Out), h.Planar, true);
                _handleKind = h.Planar ? "face" : "surface";
                ShowHint(h.Planar ? L("Ручка: наружу — выдавить грань, внутрь — вырез") : L("Ручка: внутрь — оболочка с открытой гранью"));
                break;
            default:
                ShowHint(null);
                break;
        }
    }

    private void OnHandleDragStarted(int sign)
    {
        var sel = View.Selection.Items.ToList();
        if (sel.Count == 0 || sel[0].Owner is not Part part) return;
        uint id = sel[0].Id;
        Operation? op = (_handleKind, sign) switch
        {
            ("edge", > 0) => new ChamferOp(),
            ("edge", < 0) => new RoundOp(),
            ("vertex", > 0) => new VertexChamferOp(),
            ("vertex", < 0) => new VertexRoundOp(),
            ("face", var sg) => new ExtrudeOp
            {
                StartCut = sg < 0, FlipCut = sg < 0,
                PreSketch = p => Topo.FaceOutlineSketch(p, id, L("Эскиз{0}", p.Sketches.Count() + 1)),
            },
            ("surface", < 0) => new ShellOp(),
            _ => null,
        };
        if (op == null) return;
        var keep = View.Handle;
        StartOperation(op);
        View.Handle = keep;
        ShowHint(null);
    }

    // ================================================================ ссылки и размеры эскиза (п. 2.2–2.4)

    private bool _refsPanel;

    private void AddSketchReference(PickRef r)
    {
        if (View.ActiveSketch is not { } sk || r.Owner is not Part part) return;
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
        bool planar = sel.Any(Refs.IsPlanar), face = sel.Any(r => r.Kind == PickKind.Face);
        bool edge = sel.Any(r => r.Kind == PickKind.Edge), vertex = sel.Any(r => r.Kind == PickKind.Vertex);
        bool sketch = sel.Any(Refs.IsSketch), linear = sel.Any(Refs.IsLinear), axis = sel.Any(r => r.Kind == PickKind.DatumAxis);
        bool feature = sel.Any(r => r.Target is Feature), part = sel.Any(r => r.Kind == PickKind.Body);
        if (sketch) res.AddRange(new[] { "op.extrude", "op.cut", "op.revolve", "edit.feature" });
        if (planar) res.AddRange(new[] { "op.sketch", "op.extrude", "op.plane", "op.mirror" });
        if (face) res.AddRange(new[] { "op.hole", "op.shell" });
        if (edge) res.AddRange(new[] { "op.round", "op.chamfer" });
        if (vertex) res.AddRange(new[] { "op.vertex_round", "op.vertex_chamfer" });
        if (linear && !sketch) res.Add("op.pattern_linear");
        if (axis || linear && !sketch) res.Add("op.pattern_circular");
        if (feature) res.AddRange(new[] { "edit.feature", "edit.suppress", "edit.delete" });
        if (part) res.AddRange(new[] { "view.isolate", "app.parameters", "app.properties" });
        return res.Distinct().ToList();
    }

    private void UpdateContextBar()
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: полоса выбора");
        ContextOps.Children.Clear();
        if (InSketch && View.ActiveSketch is { } sk)
        {
            SelChipText.Text = L("Эскиз «{0}»", sk.Name);
            ContextOps.Children.Add(CmdButton(_reg["sk.finish"], 18, text: true));
            ContextOps.Children.Add(CmdButton(_reg["sk.cancel"], 18, text: true));
            ContextBar.IsVisible = true;
            return;
        }
        if (CurrentOperation is { } op)
        {
            SelChipText.Text = L(op.Title);
            var ok = new Button { Content = L(op.OkLabel), Classes = { "accent" }, MinWidth = 70 };
            ok.Click += (_, _) => CommitOperation();
            var cancel = new Button { Content = L("Отмена"), MinWidth = 70 };
            cancel.Click += (_, _) => CancelOperation();
            ContextOps.Children.Add(ok);
            ContextOps.Children.Add(cancel);
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

    public void StartOperation(Operation op)
    {
        if (CurrentOperation != null) CancelOperation();
        var part = Session.Part;
        if (part == null) { ShowToast(L("Нет детали")); return; }
        bool panel;
        try { panel = op.Begin(this, part); }
        catch (Exception ex) { AppLog.Write("operation start", ex); ShowToast(L(op.Title) + ": " + ex.Message); return; }
        if (!panel) { RefreshAfterOp(); return; }
        CurrentOperation = op;
        PropsPanel.IsVisible = true;
        PropsTitle.Text = L(op.Title).ToUpperInvariant();
        PropsBody.Children.Clear();
        PropsButtons.Children.Clear();
        var p = new OpPanel(PropsBody, op);
        try
        {
            op.Build(p);
            p.Finish();
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
        };
        Tree.ContextRequested += (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is not TreeNode n) return;
            SelectItem(n.Item);
            var ids = new List<string>();
            if (n.Item is Feature or Sketch) ids.Add("edit.feature");
            if (n.Item is Feature) ids.Add("edit.suppress");
            ids.AddRange(SuggestedOps().Where(i => i != "edit.feature" && i != "edit.suppress"));
            ids.AddRange(new[] { "view.hide", "view.isolate", "edit.delete" });
            var items = ids.Distinct().Select(id => (object)Item(_reg[id])).ToList();
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
        _roots = TreeNode.Build(Session.Document);
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
            Row("Материал", part.Material?.Name ?? "—");
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
        StatusUnits.Text = L("мм");
        EnvText.Text = InSketch ? L("Эскиз") : CurrentOperation != null ? L("Операция") : L("Деталь");
        if (Session == null) return;
        int errors = Session.Document.Root.Descendants().Count(i => i.Error != null);
        if (Session.Document.IsBuilding) { StatusBuild.Text = L("Перестроение…"); StatusBuild.Foreground = Res("P3.Warning"); }
        else if (errors > 0) { StatusBuild.Text = L("Требуют внимания: {0}", errors); StatusBuild.Foreground = Res("P3.Error"); }
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
