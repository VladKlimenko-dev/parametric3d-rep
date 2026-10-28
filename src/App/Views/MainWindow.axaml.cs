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
using Avalonia.VisualTree;
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
        FontSize = UiLook.UiFontSize;
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
        // k102 (замечание 4): TAB в эскизе — значения размеров ↔ их идентификаторы (до обхода фокуса — туннелем)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None || PaletteShade.IsVisible) return;
            if (FocusManager?.GetFocusedElement() is TextBox) return;
            if (!InSketch && CurrentOperation == null) return;
            ToggleDimensionIds();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
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
        TreeNode.ExpandedChanged += n => RememberExpanded(n);
        Tree.LayoutUpdated += (_, _) => FitTreeToScrollBar();
    }

    // k116 (замечание 16:00, п. 5): ПОЛОСА ПРОКРУТКИ ДЕРЕВА НЕ ЗАКРЫВАЕТ ГЛАЗА. Полоса прокрутки темы ложится поверх
    // содержимого, и столбец значков видимости у правого края уходил под неё. Когда полоса есть, содержимое дерева
    // сужается на её ширину. Мутация P3D_TREESCROLLGAP=0.
    static readonly bool TreeScrollGapOff = Environment.GetEnvironmentVariable("P3D_TREESCROLLGAP") == "0";
    public const double TreeScrollGap = 14;
    private ScrollViewer? _treeScroll;
    /// <summary>Отступ содержимого дерева справа под полосу прокрутки (проверка).</summary>
    public double TreeContentRightGap => (_treeScroll?.Content as Control)?.Margin.Right ?? 0;
    public bool TreeScrollBarShown => _treeScroll is { } sv && sv.Extent.Height > sv.Viewport.Height + 0.5;
    private void FitTreeToScrollBar()
    {
        if (TreeScrollGapOff) return;
        _treeScroll ??= Tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_treeScroll?.Content is not Control content) return;
        double want = TreeScrollBarShown ? TreeScrollGap : 0;
        if (Math.Abs(content.Margin.Right - want) > 0.1) content.Margin = new Thickness(content.Margin.Left, content.Margin.Top, want, content.Margin.Bottom);
    }

    // ================================================================ документ

    public void SetSession(DocumentSession s)
    {
        using var _ui = Diagnostics.UiTimes.Measure("окно: открыть документ");
        if (CurrentOperation != null) { CurrentOperation = null; HideOpPanel(); }
        if (InSketch) View.ExitSketch(false);
        if (Session != null) { Session.Replaced -= OnReplaced; Session.StateChanged -= OnStateChanged; Session.Document.BuildStarted -= OnBuild; Session.Document.BuildFinished -= OnBuild; Session.Document.VerifyStarted -= OnBuild; Session.Document.VerifyFinished -= OnBuild; }
        Session = s;
        UiLook.ApplyToRender(View.Theme);                // настройки отрисовки — у документа свои; визуальные — пользователя
        View.ActivePart = s.ActivePart;
        Commands.DisplayUnits.From(s.Part?.Units);
        s.Replaced += OnReplaced;
        s.StateChanged += OnStateChanged;
        s.Document.BuildStarted += OnBuild;
        s.Document.BuildFinished += OnBuild;
        s.Document.VerifyStarted += OnBuild; s.Document.VerifyFinished += OnBuild;
        s.Document.Root.Changed += _ => ScheduleTree();
        _expand.Clear(); _nodeKeys.Clear();
        View.Document = s.Document;
        View.ActivePart = s.Part;
        View.DarkTheme = _dark;
        // u13h: FreeForm-документ открывается сразу в окружении FreeForm
        if (s.Part is { FreeFormDoc: true } ffp && !s.IsAssembly) Commands.FreeFormCommands.EnterDocument(this, ffp, rebuildUi: false);
        RebuildTree();
        RebuildUi();
        OnStateChanged();
        Dispatcher.UIThread.Post(() => View.ZoomToFit(false), DispatcherPriority.Background);
        // k121: новый документ ждёт расчёта — полоса хода (обработка, затем отображение)
        if (s.Document.AllParts().Any(p => p.IsDirty)) StartProgress();
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

    private void OnBuild() => Dispatcher.UIThread.Post(() =>
    {
        if (Session.Document.IsBuilding) StartProgress(); else UpdateProgress();
        using (Diagnostics.UiTimes.Measure("окно: строка состояния")) UpdateStatus();
        // k113: пока открыт предпросмотр операции, дерево обновляется не чаще раза в 0.5 с (каждый шаг ручки пересобирал его
        // дважды — в начале и в конце расчёта: 25 мс в потоке окна на пластине из 20 операций); по окончании расчёта — сразу.
        // P3D_TREETHROTTLE=0 — прежнее.
        using (Diagnostics.UiTimes.Measure("окно: дерево (назначить)"))
        {
            if (!TreeThrottleOff && Session.Document.PreviewPart != null) ScheduleTreeThrottled();
            else if (!Session.Document.IsBuilding || Session.Document.IsVerifying || TreeThrottleOff) ScheduleTree();
        }
        UpdateCages(); View.MarkDirty();
        // k116 (п. 2): расчёт предпросмотра идёт дольше порога — вид перерисовывается (прежняя деталь — без рёбер и выбора)
        if (Session.Document.IsBuilding && Session.Document.PreviewPart is { } pp)
            DispatcherTimer.RunOnce(() => { if (Session.Document.IsBuilding && ReferenceEquals(Session.Document.PreviewPart, pp) && pp.IsDirty) View.MarkDirty(); },
                TimeSpan.FromMilliseconds(Parametric3D.Render.Engine.SceneBuilder.StaleAfterMs + 30));
    });

    static readonly bool TreeThrottleOff = Environment.GetEnvironmentVariable("P3D_TREETHROTTLE") == "0";
    private DispatcherTimer? _treeTimer;
    public static long TreeThrottled;
    private void ScheduleTreeThrottled()
    {
        if (_treeTimer is { IsEnabled: true }) { Interlocked.Increment(ref TreeThrottled); return; }
        _treeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { _treeTimer!.Stop(); ScheduleTree(); });
        _treeTimer.Start();
    }

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
        // k121: чтение и разбор файла — в фоне; в строке состояния — «Загрузка», затем ход обработки
        _loadingFile = Path.GetFileName(path);
        StartProgress();
        DocumentSession opened;
        try { opened = StatusProgressOff ? DocumentSession.Open(path) : await Task.Run(() => DocumentSession.Open(path)); }
        catch (Exception ex) { _loadingFile = null; UpdateProgress(); ShowToast(L("Открыть: ") + ex.Message); return; }
        finally { _loadingFile = null; }
        SetSession(opened);
        UserPrefs.Current.AddRecentFile(path);
        StartProgress();
    }

    public async Task ImportAsync()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Импорт"),
            FileTypeFilter = new[]
            {
                new FilePickerFileType("STEP, IGES, STL") { Patterns = new[] { "*.step", "*.stp", "*.iges", "*.igs", "*.stl" } },
                new FilePickerFileType("STL") { Patterns = new[] { "*.stl" } },
            },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        if (Path.GetExtension(path).Equals(".stl", StringComparison.OrdinalIgnoreCase)) { await ImportStlAsync(path); return; }
        var warnings = new List<string>();
        SetSession(DocumentSession.Import(path, warnings));
        if (warnings.Count > 0) ShowToast(string.Join("\n", warnings.Take(6)), error: false);
    }

    /// <summary>
    /// k119: ИМПОРТ STL с галочкой «Преобразовать в точную геометрию»: окно настроек, новая деталь с операцией «Импорт STL»,
    /// перевод — перестроением (в фоне), итог — в строке сообщения.
    /// </summary>
    public async Task ImportStlAsync(string path, Dialogs.StlOptions? preset = null)
    {
        var o = preset ?? await Dialogs.StlImport(this, Path.GetFileName(path));
        if (o == null) return;
        DocumentSession s;
        try { s = DocumentSession.ImportMesh(path, o.Exact, o.Snap, o.TolScale, o.Scale); }
        catch (Exception ex) { ShowToast(L("Импорт STL: ") + ex.Message); return; }
        SetSession(s);
        ShowToast(L("Импорт STL: перевод сетки в тело…"), error: false);
        var fi = LastStlImport = s.Part!.Features.OfType<MeshImportFeature>().FirstOrDefault();
        void Done()
        {
            s.Document.BuildFinished -= Done;
            Dispatcher.UIThread.Post(() =>
            {
                if (fi == null || !ReferenceEquals(Session, s)) return;
                if (fi.Error != null) ShowToast(L("Импорт STL: ") + fi.Error);
                else if (fi.Report.Length > 0) ShowToast(L("Импорт STL: ") + fi.Report, error: false);
                View.ZoomToFit(animate: false);
            });
        }
        s.Document.BuildFinished += Done;
    }
    /// <summary>Разбор: операция последнего импорта STL.</summary>
    public MeshImportFeature? LastStlImport { get; private set; }

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
        ToolTip.SetTip(b, c.TooltipNow());
        ExplainDisabled(b, c);
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
        FitViewTools();
        BuildCommandBar();
        TreeTools.Children.Clear();
        // k120 (замечание 15:06, п. 16): свернуть и развернуть — шевронами; «Группа» — на панели дерева ниже (п. 13)
        TreeTools.Children.Add(ChevronButton(_reg["tree.collapse"], up: true));
        TreeTools.Children.Add(ChevronButton(_reg["tree.expand"], up: false));
        // k120 (п. 13): команды дерева — на панели дерева модели (иконками, имя — подсказкой); в полосе операций их больше нет
        TreeBar.Children.Clear();
        foreach (var id in TreeBarOff ? Array.Empty<string>() : TreeBarCommands)
            if (_reg.Find(id) is { } tc)
            {
                // без иконки — подписью целиком (п. 13: «Требуют…» обрезалось)
                bool text = !IconProvider.Exists(tc.Icon) && !StripTextOff;
                var b = CmdButton(tc, 16);
                if (b is Layoutable l) { l.Width = text ? double.NaN : 26; l.Height = 26; l.MinWidth = text ? 26 : 0; }
                if (b is TemplatedControl t) t.Padding = text ? new Thickness(6, 0) : new Thickness(0);
                TreeBar.Children.Add(b);
            }
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
        CommandStack.MaxHeight = double.PositiveInfinity;
        CommandStack.MaxWidth = double.PositiveInfinity;
    }

    // ================================================================ п. 3 (21:04): панели перемещаются и прилипают к сторонам вида

    private bool _cmdHorizontal;

    private PanelMode _viewMode = PanelMode.Float;

    /// <summary>Панель вида у стороны — полоса: кнопки одного размера (как у прочих полос).</summary>
    private void FitViewTools()
    {
        DockablePanel.Reorient(ViewTools, _viewMode != PanelMode.StripV);
        bool strip = _viewMode is PanelMode.StripV or PanelMode.StripH;
        foreach (var c in ViewTools.Children.OfType<Layoutable>())
        {
            if (c is Border { Child: null }) continue;
            c.MinHeight = strip ? UiLook.StripButtonPx : 0;
            c.MinWidth = _viewMode == PanelMode.StripV ? UiLook.StripButtonPx : 0;
        }
    }

    private PanelMode _cmdMode = PanelMode.Float, _shapeMode = PanelMode.Float, _sketchMode = PanelMode.Float, _ctxMode = PanelMode.Float;

    /// <summary>Кнопка полосы у стороны вида: иконка, размер — один для всех полос (UiLook).</summary>
    private Control StripButton(AppCommand c)
    {
        var b = CmdButton(c, UiLook.StripIconPx);
        // k120 (замечание 15:06, п. 14): кнопка без иконки — подписью ЦЕЛИКОМ (ширина по тексту), прежде квадрат обрезал
        // подпись: «Измер», «Площ», «Пов. е»
        bool text = !IconProvider.Exists(c.Icon) && !StripTextOff;
        if (b is Layoutable l) { l.Width = text ? double.NaN : UiLook.StripButtonPx; l.Height = UiLook.StripButtonPx; l.MinWidth = text ? UiLook.StripButtonPx : 0; }
        if (b is TemplatedControl tc) tc.Padding = text ? new Thickness(6, 0) : new Thickness(0);
        return b;
    }

    /// <summary>
    /// ПАНЕЛИ ИНСТРУМЕНТОВ — В ПОЛОСЕ НАД ВИДОМ (12:28). Прежде панели плавали поверх вида и прилипали к его сторонам
    /// (DockManager): размещение пересчитывалось на каждое изменение размеров вида и самих панелей — цикл раскладки
    /// мог не сходиться (окно «зависало» ещё до первого объекта), а панели закрывали модель. Теперь все панели — группы
    /// иконок одного размера в полосе (ToolTray, переносится в следующий ряд, если не помещается); вид — целиком под ней.
    /// </summary>
    private void SetupDocks()
    {
        _cmdMode = PanelMode.StripH; _cmdHorizontal = true;
        _shapeMode = PanelMode.StripH;
        _viewMode = PanelMode.StripH;
        _sketchMode = PanelMode.StripH;
        _ctxMode = PanelMode.Float;   // выбор и предлагаемые операции — внизу вида (13:09), не в полосе
        foreach (var b in new Control[] { CommandBar, ShapeBar, SketchBar, ViewBar, DimBar, SketchSetBar })
            ToolTip.SetTip(b, b == CommandBar ? L("Операции") : b == ShapeBar ? L("Фигуры") : b == SketchBar ? L("Эскиз") : b == DimBar ? L("Размеры") : b == SketchSetBar ? L("Настройка эскиза") : L("Вид"));
        if (SelChip.Parent is StackPanel inner) DockablePanel.Reorient(inner, true);
        DockablePanel.Reorient(ContextOps, true);
    }

    /// <summary>Панель кнопок дерева и заголовок дерева (проверки).</summary>
    public Panel TreeBarPanel => TreeBar;
    public Panel PropsBodyPanel => PropsBody;
    /// <summary>Все тексты панели свойств: подписи, поля, выбранные пункты списков (проверки).</summary>
    public List<string> PropsPanelTexts()
    {
        var res = new List<string> { PropsTitle.Text ?? "" };
        foreach (var c in PropsBody.GetVisualDescendants())
            switch (c)
            {
                case TextBox tb: res.Add(tb.Text ?? ""); break;
                case TextBlock t when t.FindAncestorOfType<TextBox>() == null: res.Add(t.Text ?? ""); break;
                case ComboBox cb when cb.SelectedItem is string str: res.Add(str); break;
                case ComboBox cb when cb.SelectedItem is ComboBoxItem { Content: string cs }: res.Add(cs); break;
            }
        foreach (var c in PropsBody.GetVisualDescendants().OfType<Control>()) if (ToolTip.GetTip(c) is string tip) res.Add(tip);
        return res.Where(x => x.Length > 0).ToList();
    }
    public static readonly bool TreeBarOff = Environment.GetEnvironmentVariable("P3D_TREEBAR") == "0";
    public Panel TreeTitleTools => TreeTools;

    public static readonly bool StripTextOff = Environment.GetEnvironmentVariable("P3D_STRIPTEXT") == "0";

    /// <summary>k120: команды панели дерева модели.</summary>
    public static readonly string[] TreeBarCommands = { "tree.rebuild", "tree.needs_attention", "tree.resume", "tree.insert_here", "tree.reorder_up", "tree.reorder_down", "tree.edit_dims", "tree.group", "tree.parents_children", "tree.replace_refs" };

    /// <summary>k120 (п. 16): кнопка-шеврон (свернуть — вверх, развернуть — вниз), двойной шеврон рисунком.</summary>
    private Button ChevronButton(AppCommand c, bool up)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(up ? "M0,6 L5,1 L10,6 M0,11 L5,6 L10,11" : "M0,1 L5,6 L10,1 M0,6 L5,11 L10,6"),
            StrokeThickness = 1.6, Width = 10, Height = 11, Stretch = Stretch.Uniform, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, this.GetResourceObservable("P3.Text"));
        var b = new Button { Content = path, Classes = { "tool" }, Command = c, Width = 24, Height = 24, Padding = new Thickness(0), Name = up ? "tree_collapse" : "tree_expand" };
        ToolTip.SetTip(b, c.Tooltip);
        b.Tag = c;
        return b;
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

    static readonly bool AllButtonsOff = Environment.GetEnvironmentVariable("P3D_ALLBUTTONS") == "0";

    private void BuildCommandBar()
    {
        FitCommandBar();
        CommandStack.Children.Clear();
        bool asmMode = !InSketch && Session?.IsAssembly == true && Session.ActivePart == null;
        bool ffMode = !asmMode && InFreeForm;
        (string[] Main, string[] More)[] groups = ffMode
            ? new[]
            {
                (new[] { "ff.box", "ff.cylinder", "ff.cplane" }, new[] { "ff.cone", "ff.sphere", "ff.torus", "subd.box", "ff.curve_nurbs", "ff.curve_spiral" }),
                (new[] { "ff.extrude", "ff.revolve", "ff.loft" }, new[] { "ff.sweep", "ff.pipe", "ff.thicken", "ff.shell" }),
                (new[] { "ff.push_pull", "ff.move", "ff.rotate", "ff.scale", "ff.xform_local" }, new[] { "ff.align_face", "ff.replace_face", "ff.delete_face", "ff.draft_face" }),
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
                // k96 (пп. 4, 6, 19): размеры, все связи и все настройки — кнопками, без «Ещё…»
                // k97 (замечание 10): размеры — своей панелью в полосе (DimBar)
                (new[] { "con.coincident", "con.point_on", "con.midpoint", "con.horizontal", "con.vertical", "con.parallel", "con.perpendicular", "con.collinear", "con.tangent", "con.equal", "con.concentric", "con.symmetric", "con.fix" }, Array.Empty<string>()),
                // k98 (замечание 14): настройки эскиза — своей панелью слева (SketchSetBar)
            }
            : new[]
            {
                (new[] { "op.sketch" }, Array.Empty<string>()),
                (new[] { "op.extrude", "op.revolve" }, new[] { "op.extrude_thin", "op.sweep", "op.helical_sweep", "op.loft", "op.boundary_blend", "op.swept_blend", "op.rotational_blend", "op.rib", "op.insert_body" }),
                (Array.Empty<string>(), new[] { "op.surface_extrude", "op.surface_revolve", "op.fill", "op.surf_copy", "op.surf_offset", "op.surf_merge", "op.surf_trim", "op.surf_extend", "op.thicken", "op.surf_solidify", "op.replace_face" }),
                (new[] { "op.hole", "op.round", "op.chamfer", "op.shell" }, new[] { "op.round_variable", "op.full_round", "op.vertex_round", "op.vertex_chamfer", "op.draft", "op.hole_coaxial", "op.hole_radial", "op.thread" }),
                (Array.Empty<string>(), new[] { "op.face_offset", "op.face_move", "op.face_radius", "op.face_delete", "op.move_copy" }),
                (Array.Empty<string>(), new[] { "an.measure", "an.area", "an.perimeter", "an.mass", "an.zebra", "an.curvature", "an.check_body", "an.heal" }),
                (new[] { "op.plane" }, new[] { "op.plane_offset", "op.plane_mid", "op.plane_3pt", "op.plane_axis", "op.plane_normal_curve", "op.plane_tangent", "op.axis", "op.axis_face", "op.axis_intersect", "op.point", "op.point_center", "op.point_intersect", "op.csys", "op.curve_points", "op.curve_equation", "op.curve_project" }),
                (new[] { "op.mirror", "op.pattern_linear", "op.pattern_circular" }, new[] { "op.pattern_points", "op.pattern_curve", "op.pattern_fill", "op.move_copy" }),
            };
        // k96 (п. 9): панель операций детали — все кнопки групп, без «Ещё…» (повтор команды в другой группе — один раз)
        if (!ffMode && !asmMode && !InSketch && !AllButtonsOff)
        {
            var seenIds = new HashSet<string>();
            groups = groups.Select(g => (g.Main.Concat(g.More).Where(seenIds.Add).ToArray(), Array.Empty<string>())).Where(g => g.Item1.Length > 0).ToArray();
        }
        bool strip = _cmdMode is PanelMode.StripV or PanelMode.StripH;
        bool horiz = _cmdMode is PanelMode.StripH or PanelMode.Row;
        CommandStack.Orientation = horiz ? Orientation.Horizontal : Orientation.Vertical;
        if (!strip)
            CommandStack.Children.Add(new TextBlock
            {
                Text = InSketch ? L("ЭСКИЗ") : asmMode ? L("СБОРКА") : ffMode ? L("FREEFORM") : L("ОПЕРАЦИИ"), Classes = { "header" }, Margin = new Thickness(6, 2, 6, 4),
                VerticalAlignment = VerticalAlignment.Center,
            });
        for (int g = 0; g < groups.Length; g++)
        {
            if (g > 0 && (groups[g].Main.Length > 0 || strip))
            {
                var sep = Separator(horiz);
                if (horiz) sep.Height = strip ? UiLook.StripButtonPx - 8 : 28; else sep.Width = strip ? UiLook.StripButtonPx - 6 : 128;
                CommandStack.Children.Add(sep);
            }
            // полоса у стороны: все кнопки группы (и из «Ещё…») — иконками, одного размера
            if (strip)
            {
                // полоса над видом: основные — иконками; остальные группы — выпадающим списком (стрелка) с подписями
                foreach (var id in groups[g].Main) if (_reg.Find(id) is { } c) CommandStack.Children.Add(StripButton(c));
                var moreS = groups[g].More.Where(id => _reg.Find(id) != null).ToArray();
                if (moreS.Length > 0)
                {
                    string tip = groups[g].Main.Length > 0 ? L("Ещё…") : L(groups[g].More[0] switch
                    {
                        "op.face_offset" => "Прямая правка", "an.measure" => "Анализ", "ff.curve_offset" => "Кривые", "ff.surf_ruled" => "Поверхности", "subd.extrude" => "SubD", "tree.rebuild" => "Дерево", "op.surface_extrude" => "Поверхности", _ => "Ещё…",
                    });
                    var ic0 = _reg.Find(moreS[0])?.Icon;
                    Control face = groups[g].Main.Length > 0 ? Chevron()
                        : IconProvider.Get(ic0) != null ? IconWithChevron(ic0, tip)
                        : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { new TextBlock { Text = tip, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, Chevron() } };
                    var mb = new Button { Content = face, Classes = { "tool" }, Height = UiLook.StripButtonPx, MinWidth = groups[g].Main.Length > 0 ? 16 : UiLook.StripButtonPx, Padding = new Thickness(3, 0) };
                    ToolTip.SetTip(mb, tip);
                    mb.Click += (_, _) =>
                    {
                        var sp = ListColumns();
                        foreach (var id in moreS) sp.Children.Add(CmdButton(_reg[id], 18, text: true, minWidth: 220));
                        new Flyout { Content = sp, Placement = PlacementMode.BottomEdgeAlignedLeft }.ShowAt(mb);
                    };
                    CommandStack.Children.Add(mb);
                }
                continue;
            }
            foreach (var id in groups[g].Main) if (_reg.Find(id) is { } c) CommandStack.Children.Add(CmdButton(c, 20, text: true, minWidth: horiz ? 0 : 128));
            if (groups[g].More.Length > 0)
            {
                var more = groups[g].More.Where(id => _reg.Find(id) != null).ToArray();
                // u13: группа без основных кнопок — подпись группы вместо «Ещё…»
                string moreLabel = groups[g].Main.Length > 0 ? "Ещё…" : groups[g].More[0] switch
                {
                    "op.face_offset" => "Прямая правка ▸", "an.measure" => "Анализ ▸", "ff.curve_offset" => "Кривые ▸", "ff.surf_ruled" => "Поверхности ▸", "subd.extrude" => "SubD ▸", "tree.rebuild" => "Дерево ▸", "op.surface_extrude" => "Поверхности ▸", _ => "Ещё…",
                };
                var b = new Button { Content = L(moreLabel), Classes = { "tool" }, MinWidth = horiz ? 0 : 128, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2) };
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

    /// <summary>k98: панель настройки эскиза (первая слева в эскизе).</summary>
    public static readonly string[] SketchSetTools = { "sk.setup", "sk.references", "sk.construction", "sk.grid", "sk.snap_items", "sk.show_open_ends", "sk.show_filled", "sk.resolve" };

    /// <summary>k97: панель размеров эскиза.</summary>
    public static readonly string[] DimTools =
    {
        "dim.smart", "dim.linear", "dim.horizontal", "dim.vertical", "dim.radius", "dim.diameter", "dim.angle", "dim.arc_length",
        "dim.reference", "dim.lock", "dim.make_weak", "dim.arrows_outside", "dim.link_param", "dim.area",
    };

    private void BuildSketchBars()
    {
        ShapeStack.Children.Clear();
        SketchBarStack.Children.Clear();
        DimStack.Children.Clear();
        SketchSetStack.Children.Clear();
        ShapeBar.IsVisible = SketchBar.IsVisible = DimBar.IsVisible = SketchSetBar.IsVisible = InSketch;
        // k98 (замечание 14): порядок в эскизе — настройка эскиза, фигуры, связи, размеры, «Готово/Отмена»
        var order = InSketch ? new Control[] { SketchSetBar, ShapeBar, CommandBar, DimBar, SketchBar } : new Control[] { CommandBar, SketchSetBar, ShapeBar, DimBar, SketchBar };
        for (int i = 0; i < order.Length; i++)
        {
            int at = ToolTray.Children.IndexOf(order[i]);
            if (at >= 0 && at != i) { ToolTray.Children.RemoveAt(at); ToolTray.Children.Insert(i, order[i]); }
        }
        if (!InSketch) return;
        ShapeHeader.Text = L("ФИГУРЫ");
        // фигуры: по умолчанию — сетка в три столбца; у стороны — полоса иконок; в углу — ряд с названиями
        bool shStrip = _shapeMode is PanelMode.StripV or PanelMode.StripH, shHoriz = _shapeMode is PanelMode.StripH or PanelMode.Row;
        ShapeHeader.IsVisible = !shStrip;
        if (ShapeStack.Parent is StackPanel outer) outer.Orientation = shHoriz ? Orientation.Horizontal : Orientation.Vertical;
        ShapeStack.Orientation = shHoriz || _shapeMode == PanelMode.Float ? Orientation.Horizontal : Orientation.Vertical;
        ShapeStack.Width = _shapeMode == PanelMode.Float ? 120 : double.NaN;
        ShapeStack.MaxWidth = shHoriz ? Math.Max(300, ViewArea.Bounds.Width - 60) : double.PositiveInfinity;
        ShapeStack.MaxHeight = _shapeMode == PanelMode.StripV ? Math.Max(200, ViewArea.Bounds.Height - 30) : double.PositiveInfinity;
        foreach (var id in ShapeTools)
            if (_reg.Find(id) is { } c)
            {
                Control b = shStrip ? StripButton(c) : _shapeMode == PanelMode.Row ? CmdButton(c, 18, text: true) : CmdButton(c, 22);
                if (_shapeMode == PanelMode.Float) { b.Width = 38; b.Height = 36; }
                ShapeStack.Children.Add(b);
            }
        foreach (var id in SketchSetTools)
            if (_reg.Find(id) is { } sc) SketchSetStack.Children.Add(StripButton(sc));
        // k97 (замечание 10): панель размеров — в полосе над видом
        foreach (var id in DimTools)
            if (_reg.Find(id) is { } dc) DimStack.Children.Add(StripButton(dc));
        bool skStrip = _sketchMode is PanelMode.StripV or PanelMode.StripH;
        foreach (var id in new[] { "sk.finish", "sk.cancel" })
            if (_reg.Find(id) is { } c) SketchBarStack.Children.Add(skStrip ? StripButton(c) : CmdButton(c, 18, text: true));
        DockablePanel.Reorient(SketchBarStack, _sketchMode != PanelMode.StripV);
    }

    /// <summary>Правка значения размера (из полосы предложений).</summary>
    public void EditDimensionValue(SkDimension d) => Dispatcher.UIThread.Post(() => EditDimension(d, default));

    public void RefreshToolStates()
    {
        SketchCheckpoint();       // k105: правка эскиза любым путём — отметка для отмены
        foreach (var (b, c) in _toggles) if (b is ToggleButton t) t.IsChecked = c.IsChecked?.Invoke();
        if (_datumsButton != null) _datumsButton.IsChecked = _reg["view.datums"].IsChecked!();
        UpdateStyleButton();
    }

    private MenuItem Item(AppCommand c)
    {
        var mi = new MenuItem
        {
            Header = c.Text,
            Command = c,
            InputGesture = c.Gesture,
            Icon = IconProvider.Get(c.Icon) is { } img ? new Image { Source = img, Width = 16, Height = 16 } : null,
        };
        if (c.WhyDisabled != null) { ToolTip.SetTip(mi, c.TooltipNow()); ExplainDisabled(mi, c); }
        return mi;
    }

    /// <summary>
    /// k104: ПОЧЕМУ НЕДОСТУПНО. Подсказка кнопки и пункта меню видна и у недоступной команды и при открытии берёт причину
    /// (AppCommand.WhyDisabled): «Удалить» — что именно мешает удалить выбранное.
    /// </summary>
    private static void ExplainDisabled(Control b, AppCommand c)
    {
        if (c.WhyDisabled == null) return;
        ToolTip.SetShowOnDisabled(b, true);
        ToolTip.AddToolTipOpeningHandler(b, (_, _) => ToolTip.SetTip(b, c.TooltipNow()));
    }

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

    /// <summary>Применить визуальные настройки (вкладка «Вид»): шрифт, размеры кнопок и полос, цвета вида, размеры подписей.</summary>
    public void ApplyLook()
    {
        FontSize = UiLook.UiFontSize;
        UiLook.ApplyToRender(View.Theme);
        View.MarkDirty();
        RebuildUi();
    }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        if (Application.Current is { } app) app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        IconProvider.SetForeground(dark ? IconProvider.DarkFg : IconProvider.LightFg);
        // k118 (п.10): гамма другой темы — заранее, в фоне (переключение темы без разбора SVG)
        _ = IconProvider.Preload(dark ? IconProvider.LightFg : IconProvider.DarkFg, delayMs: 2500);
        View.DarkTheme = dark;
        UiLook.ApplyToRender(View.Theme);
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
        View.CameraChanged += () => { Cube.Sync(); UpdateProjectionButton(View.IsPerspective); PlaceSectionBadge(); };
        View.SectionChanged += PlaceSectionBadge;
        View.MeasureMoved += (part, m) => Session.MarkModified();
        SectionBadge.Content = IconProvider.Content("section_view_but.svg", L("Сечение"), 20);
        ToolTip.SetTip(SectionBadge, L("Сечение — настройки плоскости"));
        SectionBadge.Click += (_, _) => { if (CurrentOperation is SectionOp) return; if (CurrentOperation != null) CancelOperation(); if (!InSketch && Session.Part != null) StartOperation(new SectionOp()); };
        View.HoverChanged += h => UpdateStatus(h);
        // k127: три пальца на сенсорной панели — сдвиг вида (Windows Precision Touchpad); жест Windows с тремя пальцами
        // (переключение окон) приложение отменить не может — при первом сдвиге подсказка, как его выключить
        Opened += (_, _) => { if (OperatingSystem.IsWindows()) WinTouchpad.Attach(this, View.Touchpad); };
        View.TouchpadPanned += () =>
        {
            if (UserPrefs.Current.TouchpadHintShown) return;
            UserPrefs.Current.TouchpadHintShown = true; UserPrefs.Current.Save();
            ShowToast(L("Сдвиг вида тремя пальцами. Чтобы Windows не переключала при этом окна: Параметры → Bluetooth и устройства → Сенсорная панель → Жесты тремя пальцами → Прокрутки: «Ничего». Без этого — Shift + прокрутка двумя пальцами."), error: false);
        };
        View.Selection.Changed += OnSelectionChanged;
        View.ContextRequested += p => { if (InSketch && View.DimensionAt(p) is { } dm) OpenDimensionMenu(dm); else OpenViewContextMenu(); };
        View.SceneRebuilt += () =>
        {
            using (Diagnostics.UiTimes.Measure("окно: состояние операции")) CurrentOperation?.RefreshStatus();
            using (Diagnostics.UiTimes.Measure("окно: строка состояния")) UpdateStatus();
            if (_propsOn && CurrentOperation == null) ShowProperties();
            using (Diagnostics.UiTimes.Measure("окно: проверка операции")) CheckCommittedFeature();
        };
        View.ToolChanged += () => { ShowHint(View.Tool != null ? L(View.Tool.Hint) : null); RefreshToolStates(); };
        View.RegionChanged += ShowRegion;
        // значки ограничений эскиза — поверх вида, под панелями
        GlyphLayer = new ConstraintGlyphLayer(View);
        ViewArea.Children.Insert(ViewArea.Children.IndexOf(RegionCanvas) + 1, GlyphLayer);
        SetupDocks();
        View.ComponentDragStarted += _ => Session.BeginEdit(L("Перетащить компонент"));
        View.ComponentDragEnded += _ => { Session.CommitEdit(); Commands.AssemblyCommands.Solve(this); };
        View.SketchEdited += () => { SketchCheckpoint(); ScheduleTree(); UpdateStatus(); UpdateContextBar(); };
        View.SketchEntered += sk => { if (!SketchUndoOff) ResetSketchUndo(sk); };
        View.OpArrowClicked += i => { try { CurrentOperation?.ArrowClicked(i); } catch (Exception ex) { AppLog.Write("operation arrow", ex); } };
        View.ExpandEdge = r => r.Owner is Part p ? Topo.ChainRefs(p, r) : new List<PickRef> { r };
        View.ReferencePicked += AddSketchReference;
        View.DimensionEditRequested += EditDimension;
        View.HandleDragStarted += OnHandleDragStarted;
        View.HandleDragged += OnHandleDragged;
        View.HandleDragEnded += OnHandleDragEnded;
        ModelViewport.GraphicsFallback += msg => Dispatcher.UIThread.Post(() => ShowToast(msg));
        View.GizmoDragStarted += part =>
        {
            if (CurrentOperation == null && View.SectionSelected) { View.SectionGizmoStart(part); return; }
            if (CurrentOperation is not { } op) return;
            op.LivePreview = true;
            op.BeginDrag();
            try { op.GizmoStart(part); } catch (Exception ex) { AppLog.Write("gizmo start", ex); }
        };
        View.GizmoDragged += d =>
        {
            if (CurrentOperation == null && View.SectionSelected) { View.SectionGizmoMove(d); View.Redraw(); return; }
            if (CurrentOperation is not { } op) return;
            try { op.GizmoMove(d); } catch (Exception ex) { AppLog.Write("gizmo drag", ex); }
            View.Redraw();
        };
        View.GizmoDragEnded += () =>
        {
            if (CurrentOperation == null && View.SectionSelected) { View.SectionGizmoEnd(); return; }
            if (CurrentOperation is not { } op) return;
            try { op.GizmoEnd(); op.EndDrag(); } catch (Exception ex) { AppLog.Write("gizmo end", ex); }
            UpdateContextBar();
        };
        View.SceneRebuilt += HideGhostWhenBuilt;
        View.SketchRolled += (sk, _) => { try { sk.Solve(Session.Document.Solver); } catch (Exception ex) { AppLog.Write("sketch roll solve", ex); } SketchCheckpoint(); Session.MarkModified(); ScheduleTree(); UpdateContextBar(); };
        View.SceneRebuilt += OnSceneShown;
        // k99: щелчок по контуру плоскости сечения — её настройки
        // k103: щелчок — выбор (ручки), двойной щелчок — настройки
        View.SectionDoubleClicked += () => { if (CurrentOperation is SectionOp) return; if (CurrentOperation != null) CancelOperation(); if (!InSketch && Session.Part != null) StartOperation(new SectionOp()); };
        View.SectionSelectionChanged += () => { UpdateStatus(); };
        // k99: при перестроении (детали изменились) — рамка и плоскость сечения по новым границам
        View.SceneRebuilt += () => { if (View.Section is { } ss) View.ApplySection(ss); };
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
        // k115 (п. 5): очертание скрытого элемента — пока он выбран
        if (_pinnedContour is { } pc && !View.Selection.Items.Any(r => ReferenceEquals(r.Target, pc) || r.Id == pc.Id)) ShowPinnedContour(null);
        // k118: отметка строки ссылки снимается, когда на виде выбрано другое
        if (selChanged && _refRowSelected is { } rs && View.ActiveSketch is { } rsk
            && !ids.Select(i => i.Id).ToHashSet().SetEquals(rsk.Entities.Where(e => e.Role == SkRole.Reference && e.ReferenceSource == rs).Select(e => e.Id))
            && ids.Count > 0 && !_refSelecting)
        {
            _refRowSelected = null;
            foreach (var rb in RefRows.Values) rb.Background = rb.IsPointerOver ? new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)) : Brushes.Transparent;
        }
        _lastSelIds = ids;
        if (selChanged) CurrentOperation?.SelectionChanged(View.Selection.Items);
        // k120 (пп. 12, 15): доступность кнопок — по выбранному (прежде пересчитывалась только при смене документа и после
        // операции: «Группа» при выборе нескольких операций оставалась недоступной). P3D_CMDREFRESH=0 — как прежде.
        if (selChanged && !CmdRefreshOff) _reg.RefreshAll();
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
            double val = fop0.HandleToValue(fop0.SignedDrag ? (sign > 0 ? v : -v) : Math.Abs(v));
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
        string label = $"{(op.DragLabel.Length > 0 ? op.DragLabel + " " : "")}{Math.Sign(v) * Math.Round(Math.Max(0.01, Math.Abs(v)), 2):0.##}{(op.DragUnit == "°" ? "°" : " " + L(op.DragUnit))}";
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
        if (CurrentOperation is IHandleOp) { RefreshPlaneHandle(); return; }
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
        if (CurrentOperation is not IHandleOp ho || CurrentOperation is not { } op || View.HandleDragging) return;
        View.Handle = null; _handleOp = null; _handleOpFixed = false;
        if (ho.HandleAt() is { } h)
        {
            static Vector3 F(OpenTK.Mathematics.Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
            View.Handle = new OpHandle(F(h.At), F(h.Out), true, true);
            _ghostOut = _ghostIn = null; _validOut = _validIn = null;
            _handleKind = op is PlaneOp ? "plane" : "op"; _handleOp = op; _handleOpFixed = true;
            View.SetHandleValue(op.ValueToHandle(op.DragCurrent ?? 0));
            // k112: контур ручки операции (уклон — шарнир и смещённое ребро)
            View.SetHandleGhost(FullPreview ? null : ho.HandleGhost(), null);
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
        if (CurrentOperation is { Dragging: false } gop && !View.GizmoDragging) gop.RefreshGizmo();
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
        if (View.ActiveSketch is { } ask && r.Owner is Sketch other)
        {
            var e2 = SketchReferences.AddFromSketch(ask, other, r);
            if (e2 != null) { ShowToast(e2, error: false); return; }
            try { ask.Solve(Session.Document.Solver); } catch (Exception ex) { AppLog.Write("sketch solve", ex); }
            View.RefreshDefaultDimensions(); View.MarkDirty(); ScheduleTree();
            if (_refsPanel) ShowReferencesPanel(); else ShowToast(L("Ссылка добавлена: {0}", L("Эскиз") + " «" + other.Name + "»"), error: false);
            AfterSketchEdit(ask);
            return;
        }
        if (View.ActiveSketch is not { } sk || r.Owner is not Part part) return;
        // u13: режим «использовать / сместить / утолщить ребро» — ребро становится основной геометрией эскиза
        if (Commands.SketchEditCommands.OnModelEdge(this, sk, part, r)) { ScheduleTree(); return; }
        var err = r.Kind == PickKind.Face ? SketchReferences.AddFace(sk, part, r.Id) : SketchReferences.Add(sk, part, r);
        if (err != null) { ShowToast(err, error: false); return; }
        try { sk.Solve(Session.Document.Solver); } catch (Exception ex) { AppLog.Write("sketch solve", ex); }
        View.RefreshDefaultDimensions();
        View.MarkDirty();
        ScheduleTree();
        if (_refsPanel) ShowReferencesPanel();
        else
        {
            var srcName = r.Kind == PickKind.Face ? SketchReferences.FaceSource(r.Id) : SketchReferences.SourceOf(r);
            ShowToast(L("Ссылка добавлена: {0}", L(srcName.Split(' ')[0]) + " " + srcName.Split(' ')[1]), error: false);
        }
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
        RefRows.Clear();
        if (_refRowSelected != null && !list.Contains(_refRowSelected)) _refRowSelected = null;
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
            PropsBody.Children.Add(RefRow(sk, src, row));
        }
        var close = new Button { Content = L("Готово"), Classes = { "accent" }, MinWidth = 90 };
        close.Click += (_, _) => ToggleReferencesPanel();
        PropsButtons.Children.Add(close);
        RefreshToolStates();
    }

    public bool ReferencesPanelOpen => _refsPanel;

    public static readonly bool RefRowsOff = Environment.GetEnvironmentVariable("P3D_REFROWS") == "0";
    /// <summary>k118: строки списка ссылок (для проверок): источник → рамка строки.</summary>
    public Dictionary<string, Border> RefRows { get; } = new();
    private string? _refRowSelected;
    public string? RefRowSelected => _refRowSelected;
    private bool _refSelecting;

    /// <summary>
    /// k118 (замечание 08:54, п.1): строка списка ссылок интерактивна — под курсором фон строки чуть светлее и элементы
    /// ссылки подсвечены на эскизе; щелчок выбирает их на виде (строка остаётся отмеченной, пока выбор тот же).
    /// P3D_REFROWS=0 — строки без отклика, как прежде.
    /// </summary>
    private Control RefRow(Sketch sk, string src, DockPanel row)
    {
        if (RefRowsOff) return row;
        var b = new Border { Child = row, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        RefRows[src] = b;
        uint[] Ids() => sk.Entities.Where(e => e.Role == SkRole.Reference && e.ReferenceSource == src).Select(e => e.Id).ToArray();
        void Paint()
        {
            bool sel = _refRowSelected == src;
            bool hov = b.IsPointerOver;
            b.Background = sel ? (IBrush)new SolidColorBrush(Color.FromArgb(0x55, 0x3d, 0x8b, 0xd9))
                : hov ? new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)) : Brushes.Transparent;
        }
        b.PointerEntered += (_, _) => { Paint(); View.HighlightSketchEntities(Ids()); };
        b.PointerExited += (_, _) => { Paint(); View.HighlightSketchEntities(null); };
        b.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
            SelectRefRow(sk, src);
            e.Handled = true;
        };
        if (_refRowSelected == src) Paint();
        return b;
    }

    /// <summary>k118: выбрать на виде элементы ссылки и отметить её строку.</summary>
    public void SelectRefRow(Sketch sk, string src)
    {
        _refRowSelected = src;
        _refSelecting = true;
        try
        {
            View.Selection.Clear();
            foreach (var e in sk.Entities.Where(e => e.Role == SkRole.Reference && e.ReferenceSource == src))
                View.Selection.Select(new PickRef(e.Id, e is SkPoint ? PickKind.SketchPoint : PickKind.SketchCurve, sk, e), additive: true);
        }
        finally { _refSelecting = false; }
        foreach (var (k, rb) in RefRows)
            rb.Background = k == src ? new SolidColorBrush(Color.FromArgb(0x55, 0x3d, 0x8b, 0xd9)) : rb.IsPointerOver ? new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)) : Brushes.Transparent;
        View.MarkDirty();
    }

    /// <summary>Двойной щелчок по размеру: новое значение (число или формула) — размер становится управляющим.</summary>
    /// <summary>k102: окно правки размера открыто (поле ввода — для вставки идентификаторов щелчком по размерам).</summary>
    public TextBox? DimEditBox { get; private set; }
    /// <summary>k103: подсказки поля правки размера.</summary>
    public FormulaAssist? DimEditAssist { get; private set; }
    private Popup? _dimPopup;
    private Func<Point, bool>? _clickBeforeDimEdit;

    /// <summary>
    /// ПРАВКА РАЗМЕРА ЭСКИЗА: число или формула (k102: с идентификаторами d0, d1… этого эскиза, путями s"Эскиз".dN к другим
    /// и функциями). Щелчок по другому размеру эскиза, пока окно открыто, вставляет его идентификатор в место курсора.
    /// Enter/«ОК» — размер становится сильным, формула хранится в размере (видна в «Параметрах» детали); Esc — закрыть.
    /// </summary>
    private void EditDimension(SkDimension d, Point at)
    {
        if (View.ActiveSketch is not { } sk) return;
        if (d.UserLocked) { ShowToast(L("Размер заблокирован — снимите замок (правая кнопка → «Разблокировать»)"), error: false); return; }
        CloseDimensionEdit();
        sk.EnsureDimIds();
        bool angle = d.Type == DimensionType.AngleLineLine;
        // координатный размер хранит знак (сторона от нуля, нужна решателю); показывается и вводится расстояние — без знака
        bool signedCoord = d.Type is DimensionType.CoordX or DimensionType.CoordY;
        // k123: угол в смежном секторе — вводится то, что видно (180 − угол); длины — в единицах детали
        bool supp = angle && d.Supplement;
        double lf = angle ? 1 : DisplayUnits.Factor;
        string shownFormula = d.Formula is { } f0 && supp && f0.StartsWith("180 - (") && f0.EndsWith(")") ? f0[7..^1] : d.Formula ?? "";
        var tb = new TextBox { Text = d.Formula != null ? shownFormula : ((signedCoord ? Math.Abs(d.Value) : supp ? 180 - d.Value : d.Value) / lf).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), MinWidth = 160 };
        // k103: подсказка после точки и подсветка имён в кавычках
        var assist = new FormulaAssist(tb, () => (ModelItem?)Session.Part ?? Session.Document.Root, () => sk);
        DimEditAssist = assist;
        var ok = new Button { Content = L("ОК"), Classes = { "accent" } };
        var close = new Button { Content = "×", Classes = { "tool" }, Padding = new Thickness(6, 0) };
        var err = new TextBlock { Classes = { "err" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, IsVisible = false };
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(8),
            Children =
            {
                new DockPanel { Children = { close, new TextBlock { Text = (angle ? L("Угол, °") : L("Размер, {0}", L(DisplayUnits.LengthUnit))) + "  " + d.Name, Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } },
                assist.View,
                new TextBlock { Text = L("Щёлкните другой размер — его имя встанет в формулу"), Classes = { "dim" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 },
                err, ok,
            } };
        DockPanel.SetDock(close, Dock.Right);
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("P3.PanelBg"));
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("P3.Border"));
        var pop = new Popup { Child = border, PlacementTarget = View, Placement = PlacementMode.Pointer, IsLightDismissEnabled = false };
        ViewArea.Children.Add(pop);
        _dimPopup = pop; DimEditBox = tb;
        // щелчок по другому размеру — его идентификатор в место курсора
        _clickBeforeDimEdit = View.ClickInterceptor;
        View.ClickInterceptor = p =>
        {
            if (View.DimensionAt(p) is { } other && !ReferenceEquals(other, d)) { InsertDimensionName(other.Name); return true; }
            return _clickBeforeDimEdit?.Invoke(p) == true;
        };
        void Apply()
        {
            string t = (tb.Text ?? "").Trim();
            double v; string? formula = null;
            if (double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                v *= lf;                                                             // k123: число — в единицах детали
            else
            {
                var table = Session.Part != null ? Session.Document.ScopeFor(Session.Part) : Session.Document.Parameters;
                if (!sk.TryEvaluate(t, table, d, out v, out var why)) { err.Text = L("Не число и не формула: {0}", t) + (why != null ? " — " + why : ""); err.IsVisible = true; return; }
                // ссылка на себя (прямо или через другие размеры)
                if (System.Text.RegularExpressions.Regex.IsMatch(t, $@"(?<![\w""])d{d.Index}(?!\w)")) { err.Text = L("Формула ссылается на сам размер"); err.IsVisible = true; return; }
                formula = t;
            }
            // k96 (п. 3): размер 0 допустим у расстояний (точка на прямой, совпадение точек); радиус, диаметр, длина дуги — > 0
            bool positiveOnly = d.Type is DimensionType.Radius or DimensionType.Diameter or DimensionType.ArcLength or DimensionType.EllipseRadiusU or DimensionType.EllipseRadiusV;
            if (!angle && (v < 0 || v == 0 && positiveOnly)) { err.Text = L(positiveOnly ? "Размер должен быть больше нуля" : "Размер не может быть отрицательным"); err.IsVisible = true; return; }
            if (signedCoord && d.Value < 0) v = -v;                                  // сторона от нуля — прежняя
            if (supp) { v = 180 - v; if (formula != null) formula = "180 - (" + formula + ")"; }
            string what = L("Размер {0} → {1}", d.DisplayText(), t);
            double vv = v; string? ff = formula;
            Func<SketchEditResult> run = () => SketchEditing.SetDimensionValue(sk, d, vv, ff, Session.Document.Solver);
            ClearConflictFlags(sk);
            var res = run();
            AfterSketchEdit(sk);
            if (!res.Ok) ReportSketchConflict(sk, res, what, run);
            CloseDimensionEdit();
        }
        ok.Click += (_, _) => Apply();
        close.Click += (_, _) => CloseDimensionEdit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } else if (e.Key == Key.Escape) { CloseDimensionEdit(); e.Handled = true; } };
        pop.IsOpen = true;
        Dispatcher.UIThread.Post(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Input);
    }

    /// <summary>Вставить имя (идентификатор размера) в поле правки размера — в место курсора.</summary>
    public void InsertDimensionName(string name)
    {
        if (DimEditBox is not { } tb) return;
        string text = tb.Text ?? "";
        int at = Math.Clamp(tb.SelectionStart == tb.SelectionEnd ? tb.CaretIndex : Math.Min(tb.SelectionStart, tb.SelectionEnd), 0, text.Length);
        int end = tb.SelectionStart == tb.SelectionEnd ? at : Math.Max(tb.SelectionStart, tb.SelectionEnd);
        // всё выделено (значение при открытии) — имя заменяет его
        tb.Text = text[..at] + name + text[Math.Min(end, text.Length)..];
        tb.SelectionStart = tb.SelectionEnd = tb.CaretIndex = at + name.Length;
        tb.Focus();
    }

    public void CloseDimensionEdit()
    {
        if (_dimPopup is { } pop) { pop.IsOpen = false; ViewArea.Children.Remove(pop); }
        if (DimEditBox != null) View.ClickInterceptor = _clickBeforeDimEdit;
        DimEditAssist?.Close();
        _dimPopup = null; DimEditBox = null; DimEditAssist = null; _clickBeforeDimEdit = null;
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
    /// <summary>P3D_AXISDIMMENU=0 — без пунктов «Сделать горизонтальным / вертикальным».</summary>
    static readonly bool AxisDimMenuOff = Environment.GetEnvironmentVariable("P3D_AXISDIMMENU") == "0";
    /// <summary>Разбор (k130): пункты последнего меню размера.</summary>
    public List<string>? LastDimensionMenu { get; private set; }
    /// <summary>Проверки: меню размера (как правой кнопкой) — пункты в LastDimensionMenu; pick — выполнить пункт с такой подписью.</summary>
    public void OpenDimensionMenuForTest(SkDimension d, string? pick = null)
    {
        _dimMenuPick = pick; try { OpenDimensionMenu(d); } finally { _dimMenuPick = null; }
    }
    private string? _dimMenuPick;

    private void OpenDimensionMenu(SkDimension d)
    {
        if (View.ActiveSketch is not { } sk) return;
        var solver = Session.Document.Solver;
        var items = new List<object>();
        // k96: пункты вида (стрелки, одна стрелка, как размер) решения не требуют — прежде каждый пункт решал эскиз и при
        // статусе «переопределён» откатывал эскиз снимком (копии размеров): «одна стрелка» уходила другому размеру (п. 8).
        void Add(string label, bool enabled, Action act, string? icon = null, bool? check = null, bool geometry = false)
        {
            var mi = new MenuItem { Header = (check == true ? "✓ " : "") + L(label), IsEnabled = enabled };
            if (icon != null && IconProvider.Get(icon) is { } img) mi.Icon = new Image { Source = img, Width = 16, Height = 16 };
            mi.Click += (_, _) =>
            {
                if (!geometry) { act(); sk.NotifyChanged(sk); View.MarkDirty(); return; }
                var geo = SketchEditing.Geometry.Take(sk);
                var st0 = sk.Solve(solver); geo.Restore();
                var saved = (d.Value, d.Formula, d.Locked, d.IsDefault, d.Reference, d.UserLocked, d.Type);
                act();
                var st = sk.Solve(solver);
                var fail = SketchEditing.FailureOf(st0, st, false);
                if (fail != SketchEditFailure.None)
                {
                    var res = new SketchEditResult { Failure = fail, Status = st };
                    geo.Restore();
                    SketchEditing.Diagnose(sk, solver, geo, st0, false, new object[] { d }, res);
                    (d.Value, d.Formula, d.Locked, d.IsDefault, d.Reference, d.UserLocked, d.Type) = saved;
                    geo.Restore(); sk.Solve(solver); geo.Restore();
                    ReportSketchConflict(sk, res, L(label) + " — " + DimensionName(d), null);
                }
                AfterSketchEdit(sk);
            };
            items.Add(mi);
        }
        bool radial = d.Type is DimensionType.Radius or DimensionType.Diameter;
        bool weak = d.IsDefault || !d.Locked && !d.Reference;
        if (weak || d.Reference)
            Add("Сделать сильным", true, () =>
            {
                double m = DimensionMeasure.Value(d);
                var r = SketchEditing.SetDimensionValue(sk, d, double.IsNaN(m) ? d.Value : m, d.Formula, solver);
                AfterSketchEdit(sk);
                if (!r.Ok) ReportSketchConflict(sk, r, L("Сделать сильным") + " — " + DimensionName(d), null);
            });
        else
            Add("Сделать слабым", !d.UserLocked, () => { SketchEditing.MakeWeak(sk, new[] { d }, solver); AfterSketchEdit(sk); });
        Add(d.UserLocked ? "Разблокировать" : "Заблокировать", !d.Reference, () =>
        {
            d.UserLocked = !d.UserLocked;
            if (d.UserLocked) { d.IsDefault = false; d.Locked = true; }
        }, d.UserLocked ? "dim_lock_ico.svg" : "dim_unlock_ico.svg", geometry: true);
        Add(d.Reference ? "Сделать управляющим" : "Сделать справочным", !d.UserLocked, () =>
        {
            d.Reference = !d.Reference;
            if (d.Reference) { d.Locked = false; d.IsDefault = false; } else { d.Locked = true; d.Value = DimensionMeasure.Value(d) is var mv && !double.IsNaN(mv) ? mv : d.Value; }
        }, "dim_reference_but.svg", geometry: true);
        // k130 (замечание 28.10, 19:39, п. 4): косой размер «точка — точка» — сделать по горизонтали или по вертикали;
        // у размера, уже горизонтального или вертикального, пунктов нет. Значение — нынешнее расстояние по оси (геометрия на месте).
        if (DimensionMeasure.IsDiagonal(d) && !AxisDimMenuOff)
        {
            items.Add(new Separator());
            foreach (var (label, t, icon) in new[] { ("Сделать горизонтальным", DimensionType.DistanceHorizontal, "dim_size_horizontal.svg"), ("Сделать вертикальным", DimensionType.DistanceVertical, "dim_size_vertical.svg") })
                Add(label, !d.UserLocked, () =>
                {
                    d.Type = t; d.Side = 0; d.Formula = null;
                    double m = DimensionMeasure.Value(d); if (!double.IsNaN(m)) d.Value = m;
                    sk.BumpStructure();
                }, icon, geometry: true);
        }
        LastDimensionMenu = items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList();
        items.Add(new Separator());
        Add("Стрелки снаружи (наружный размер)", true, () => d.Arrows = ArrowPlacement.Outside, d.Type == DimensionType.Diameter ? "dim_diameter_outside_ico.svg" : d.Type == DimensionType.Radius ? "dim_radius_outside_ico.svg" : d.Type == DimensionType.AngleLineLine ? "dim_angle_outside_ico.svg" : "dim_arrows_outside_but.svg", check: d.Arrows == ArrowPlacement.Outside);
        Add("Стрелки внутри (внутренний размер)", true, () => d.Arrows = ArrowPlacement.Inside, check: d.Arrows == ArrowPlacement.Inside);
        Add("Стрелки — автоматически", true, () => d.Arrows = ArrowPlacement.Auto, check: d.Arrows == ArrowPlacement.Auto);
        if (radial)
        {
            items.Add(new Separator());
            Add("Одна стрелка", true, () => d.SingleArrow = !d.SingleArrow, d.Type == DimensionType.Diameter ? "dim_diameter_arrow_ico.svg" : "dim_radius_arrow_ico.svg", check: d.SingleArrow);
            if (d.Type == DimensionType.Radius)
                Add("Сделать диаметром", !d.UserLocked, () => { d.Type = DimensionType.Diameter; d.Value *= 2; d.Formula = d.Formula == null ? null : $"({d.Formula})*2"; }, geometry: true);
            else
            {
                Add("Сделать радиусом", !d.UserLocked, () => { d.Type = DimensionType.Radius; d.Value /= 2; d.Formula = d.Formula == null ? null : $"({d.Formula})/2"; d.DiameterAsLinear = false; }, geometry: true);
                Add("Показать как размер", true, () => d.DiameterAsLinear = !d.DiameterAsLinear, "dim_diameter_size_ico.svg", check: d.DiameterAsLinear);
            }
        }
        items.Add(new Separator());
        Add("Изменить значение…", !d.UserLocked, () => Dispatcher.UIThread.Post(() => EditDimension(d, default)));
        // k96 (п. 17): удалить; нужный эскизу сильный размер становится слабым
        Add("Удалить размер", !d.UserLocked, () =>
        {
            var (removed, weakened) = SketchEditing.DeleteDimensions(sk, new[] { d }, solver);
            AfterSketchEdit(sk);
            if (weakened > 0) ShowToast(L("Размер нужен, чтобы эскиз был определён, — он стал слабым"), error: false);
        }, "delete_but.svg");
        if (_dimMenuPick != null)
        {
            LastDimensionMenu = items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList();
            if (items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == L(_dimMenuPick)) is { } mi) mi.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            return;
        }
        new ContextMenu { ItemsSource = items }.Open(View);
    }

    /// <summary>Разбор: пункты последнего контекстного меню вида.</summary>
    public List<object>? LastContextMenu { get; private set; }
    public void OpenViewContextMenuForTest() => OpenViewContextMenu();

    private void OpenViewContextMenu()
    {
        var items = new List<object>();
        // k109 (11:07): в эскизе — команды выбранного и «Make construction» / «Make sketch»
        if (InSketch && !View.Selection.IsEmpty)
        {
            if (Commands.SketchSuggest.ConstructionTargets(this).Count > 0)
            {
                bool all = Commands.SketchSuggest.AllConstruction(this);
                var mi = new MenuItem { Header = L(all ? "Сделать основной (Make sketch)" : "Сделать вспомогательной (Make construction)"), Name = "makeConstruction" };
                if (IconProvider.Get("sk_construction_but.svg") is { } ci) mi.Icon = new Image { Source = ci, Width = 16, Height = 16 };
                mi.Click += (_, _) => Commands.SketchSuggest.ToggleConstructionSel(this);
                items.Add(mi);
                items.Add(new Separator());
            }
            foreach (var id in Commands.SketchSuggest.For(this).Where(i => i != "sk.construction" && _reg.Find(i) != null)) items.Add(Item(_reg[id]));
            LastContextMenu = items;
            new ContextMenu { ItemsSource = items }.Open(View);
            return;
        }
        var ids = View.Selection.IsEmpty
            ? new[] { "view.fit", "view.home", "view.show_all", "op.sketch", "app.parameters" }
            : SuggestedOps().Concat(new[] { "view.hide", "view.isolate", "edit.delete", "app.properties" }).Distinct().ToArray();
        foreach (var id in ids) items.Add(Item(_reg[id]));
        LastContextMenu = items;
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
        if (sketch) res.AddRange(new[] { "op.extrude", "op.revolve", "edit.feature" });
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
        if (sel.Any(r => EditablePlane(r.Target as ModelItem))) res.Insert(0, "edit.feature");
        // эскиз (в том числе оставшийся без операции) — удаляется отсюда же
        if (sketch && sel.Any(r => r.Target is Sketch)) res.AddRange(new[] { "sk.setup", "edit.delete" });
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
        // у стороны вида — полоса иконок без текста (подпись выбора скрыта)
        SelChip.IsVisible = _ctxMode is not PanelMode.StripV;
        if (InSketch && View.ActiveSketch is { } sk)
        {
            // пп. 9–10: полоса предложений эскиза — только подходящие к выбранному операции (иконками, подпись в подсказке)
            var ops = Commands.SketchSuggest.For(this);
            bool any = !View.Selection.IsEmpty;
            SelChip.Opacity = any ? 1 : 0.75;
            SelChipText.Text = View.Tool != null && !string.IsNullOrEmpty(_sketchHint) ? _sketchHint
                : any ? View.Selection.Describe() : L("Эскиз «{0}»: выберите точки, кривые или размер — здесь появятся подходящие операции; Ctrl+Enter — закончить эскиз", sk.Name);
            if (View.Tool != null) SelChip.Opacity = 1;
            foreach (var id in ops)
            {
                var b = CtxButton(_reg[id], 20);
                if (b is Layoutable lb) lb.MinWidth = 34;
                ContextOps.Children.Add(b);
            }
            if (any) ContextOps.Children.Add(CtxButton(_reg["edit.clear_selection"], 18));
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
                if (_reg.Find(id) is { Enabled: true } oc) ContextOps.Children.Add(CtxButton(oc, 18, text: true));
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
            var b = CtxButton(c, 18, text: true);
            if (id == "edit.feature" && sel.Items.Any(Refs.IsSketch)) ((ContentControl)b).Content = new TextBlock { Text = L("Изменить эскиз"), FontSize = 12 };
            ContextOps.Children.Add(b);
        }
        // u13: закреплённые и недавние для набора, повысить, похожие, ещё, поиск
        var have = SuggestedOps().ToHashSet();
        foreach (var id in UserPrefs.Current.PinnedCommands)
            if (!have.Contains(id) && _reg.Find(id) is { Enabled: true } pc) { ContextOps.Children.Add(CtxButton(pc, 18, text: true)); have.Add(id); }
        var recentOps = UserPrefs.Current.RecentCommands.Where(id => id.StartsWith("op.") && !have.Contains(id)).Select(_reg.Find).OfType<AppCommand>().Where(c => c.Enabled).Take(3).ToList();
        if (recentOps.Count > 0)
        {
            ContextOps.Children.Add(IconProvider.Get("recent_ops_ico.svg") is { } ri
                ? new Image { Source = ri, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) }
                : new TextBlock { Text = "⟲", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), Classes = { "dim" } });
            ToolTip.SetTip(ContextOps.Children[^1], L("Недавние для набора"));
            foreach (var c in recentOps) ContextOps.Children.Add(CtxButton(c, 18, text: true));
        }
        foreach (var id in new[] { "ctx.up", "ctx.similar", "ctx.more", "ctx.search", "ctx.pin" })
            if (_reg.Find(id) is { Enabled: true } xc)
            {
                var xb = CtxButton(xc, 18, text: id == "ctx.more");
                ContextOps.Children.Add(xb);
            }
        if (_reg["view.hide"].Enabled) ContextOps.Children.Add(CtxButton(_reg["view.hide"], 18, text: true));
        var clear = CtxButton(_reg["edit.clear_selection"], 18);
        ContextOps.Children.Add(clear);
        ContextBar.IsVisible = true;
    }

    /// <summary>Пересобрать полосу выбора (после команды, меняющей состояние выбранного: «скрыть / показать»).</summary>
    public void UpdateContextBarPublic() => UpdateContextBar();

    /// <summary>Кнопка полосы выбора: у стороны вида — кнопка полосы (иконка, один размер), иначе — обычная.</summary>
    private Control CtxButton(AppCommand c, double icon, bool text = false) =>
        _ctxMode is PanelMode.StripV or PanelMode.StripH ? StripButton(c) : CmdButton(c, icon, text: text);

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
        View.SectionSelected = false;                   // k103: ручки сечения — у операции
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
        try { op.RefreshGizmo(); } catch (Exception ex) { AppLog.Write("gizmo", ex); }
        if (View.Gizmo != null) ShowHint(null);   // подсказка ручки выбора — не к этой операции
    }

    public void CommitOperation()
    {
        if (CurrentOperation is not { } op) return;
        bool done;
        try { done = op.Commit(); }
        catch (Exception ex) { AppLog.Write("operation commit", ex); ShowToast(L(op.Title) + ": " + ex.Message); return; }
        if (!done) return;
        // k96 (самотест 17:56): предпросмотр (по сеткам) прошёл, а точный расчёт после «ОК» может отказать — сообщить
        _committedWatch = op.EditedFeature is Feature cf ? (new WeakReference<Feature>(cf), L(op.Title)) : null;
        if (ReferenceEquals(CurrentOperation, op)) EndOperation();
    }

    // слабая ссылка: отмена заменяет деталь, и прежняя (с сохранёнными состояниями) не должна жить из-за ожидания (самотест k97: +250 МБ)
    private (WeakReference<Feature> Feature, string Title)? _committedWatch;

    /// <summary>После перестроения: операция, только что принятая «ОК», отказала в точном расчёте — сообщение.</summary>
    private void CheckCommittedFeature()
    {
        if (_committedWatch is not { } w || !w.Feature.TryGetTarget(out var f)) { _committedWatch = null; return; }
        var doc = Session.Document;
        if (f.Parent is not { } fp || !doc.AllParts().Contains(fp)) { _committedWatch = null; return; }   // деталь заменена (отмена)
        if (doc.IsBuilding || doc.AllParts().Any(p => p.IsDirty)) return;   // ещё считается
        _committedWatch = null;
        if (f.Error is { } err)
            ShowToast(L("{0}: точный расчёт после «ОК» не удался — {1}. Операция отмечена в дереве; «Отменить» вернёт деталь.", w.Title, err.Split('\n')[0]));
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
        View.Selection.Predicate = null;
        View.SetOpDims(null); View.SetOpArrows(null); View.SetPointSketches(null);
        _handleOp = null; _handleOpFixed = false;
        _opQuiet = false;
        DragValueBox.IsVisible = false;
        View.Handle = null;
        View.Gizmo = null;
        View.Selection.Filter = InSketch ? PickFilter.Sketch : PickFilter.All;
        if (!InSketch) View.Selection.Clear();
        HideOpPanel();
        View.MarkDirty();
        RefreshAfterOp();
    }

    /// <summary>Мутация P3D_OPRELEASE=0: панель и предпросмотр завершённой операции держат её (и деталь до отмены).</summary>
    public static readonly bool OpReleaseOff = Environment.GetEnvironmentVariable("P3D_OPRELEASE") == "0";

    private void HideOpPanel()
    {
        PropsButtons.Children.Clear();
        if (!OpReleaseOff) PropsBody.Children.Clear();   // k97: поля завершённой операции держали её и прежнюю деталь (самотест: +240 МБ)
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
    /// <summary>k116: опорная плоскость, которую можно править (не стандартные XY/XZ/YZ).</summary>
    public static bool EditablePlane(ModelItem? i) => !PlaneOp.EditOff && i is DatumPlane dp && dp.Parent is Part pp
        && !ReferenceEquals(dp, pp.PlaneXY) && !ReferenceEquals(dp, pp.PlaneXZ) && !ReferenceEquals(dp, pp.PlaneYZ);

    public void EditSelected()
    {
        var item = SelectedItems().FirstOrDefault(i => i is Feature or Sketch || EditablePlane(i));
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
            case DatumPlane dpe when EditablePlane(dpe):
                if (CurrentOperation != null) CancelOperation();
                StartOperation(new PlaneOp { EditTarget = dpe });
                break;
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
        AfterSketchCancel = null;
        // k116 (п. 9): снимок эскиза и расчёта детали — правка вступает в силу только по «ОК»
        _skEdit = null;
        if (!SketchStashOff && (sk.Parent as Part ?? Session.Part) is { } ep && ep.StashBuild() is { } stash)
            _skEdit = (ep, sk, SketchState.Capture(sk), SketchSigRounded(sk), stash);
        SketchRollback(sk);
        EnterSketchUi(sk);
    }

    // k97 (замечание 4): правка эскиза — временный откат к состоянию ДО операции, которая его использует: ссылки (очерки,
    // рёбра) и изображение детали — без будущих операций. По выходу полоска отката возвращается.
    private (Part Part, int Stage)? _sketchStage;
    static readonly bool SketchRollbackOff = Environment.GetEnvironmentVariable("P3D_SKROLLBACK") == "0";

    private void SketchRollback(Sketch sk)
    {
        _sketchStage = null;
        var part = sk.Parent as Part ?? Session.Part;
        if (SketchRollbackOff || part == null) return;
        int k = part.Features.FindIndex(f => f.ConsumedSketchIds().Contains(sk.Id));
        if (k < 0) return;
        int stage = part.ClampStage(k);
        if (part.StageIndex >= 0 && part.StageIndex <= stage) return;   // и так откачено не ниже
        _sketchStage = (part, part.StageIndex);
        // k116: без сброса сохранённых состояний — полоска сдвигается, состояния до неё остаются (выход не пересчитывает всё)
        part.StageIndex = stage; if (SketchStashOff) part.Invalidate(); else part.MarkStageMoved();
        View.MarkDirty();
    }

    /// <summary>k116: правка эскиза — деталь, эскиз, его снимок и подпись, снимок расчёта детали.</summary>
    private (Part Part, Sketch Sketch, SketchState Orig, string Sig, Part.BuildStash Stash)? _skEdit;
    static readonly bool SketchStashOff = Environment.GetEnvironmentVariable("P3D_SKSTASH") == "0";
    /// <summary>Разбор: сколько раз выход из эскиза вернул деталь из снимка расчёта (без пересчёта).</summary>
    public static long SketchStashRestores;

    /// <summary>Вернуть эскиз (если отмена) и расчёт детали из снимка; false — снимка нет или он не подошёл.</summary>
    private bool RestoreSketchEdit(bool revertSketch)
    {
        if (_skEdit is not { } e) return false;
        _skEdit = null;
        if (revertSketch) { e.Orig.RestoreInto(e.Sketch); e.Sketch.NotifyChanged(e.Sketch); }
        else if (SketchSigRounded(e.Sketch) is var sigNow && sigNow != e.Sig)
        {
            if (Environment.GetEnvironmentVariable("P3D_SKSTASHDBG") == "1")
            {
                int k = 0; while (k < Math.Min(sigNow.Length, e.Sig.Length) && sigNow[k] == e.Sig[k]) k++;
                Console.WriteLine($"SKSTASH sig differs at {k}: «{e.Sig.Substring(Math.Max(0, k - 60), Math.Min(140, e.Sig.Length - Math.Max(0, k - 60)))}» vs «{sigNow.Substring(Math.Max(0, k - 60), Math.Min(140, sigNow.Length - Math.Max(0, k - 60)))}»");
            }
            return false;
        }
        _sketchStage = null;
        if (!e.Part.RestoreBuild(e.Stash)) { e.Part.StageIndex = e.Stash.Stage; e.Part.MarkStageMoved(); View.MarkDirty(); return revertSketch; }
        Interlocked.Increment(ref SketchStashRestores);
        View.MarkDirty();
        return true;
    }

    /// <summary>Вернуть полоску отката после правки эскиза.</summary>
    private void SketchRollbackEnd()
    {
        if (_sketchStage is not { } st) return;
        _sketchStage = null;
        st.Part.StageIndex = st.Stage; if (SketchStashOff) st.Part.Invalidate(); else st.Part.MarkStageMoved();
        View.MarkDirty();
    }

    /// <summary>Разбор: эскиз правится с временным откатом.</summary>
    public bool SketchRolledBack => _sketchStage != null;

    private void EnterSketchUi(Sketch sk)
    {
        ConstructionMode = false;
        View.EnterSketch(sk);
        // k96 (п. 10): инструмент «Отрезок» не включается сам — эскиз открывается в режиме выбора
        View.Tool = DefaultLineToolOff ? new LineTool() : null;
        RebuildUi();
        ScheduleTree();
    }

    static readonly bool DefaultLineToolOff = Environment.GetEnvironmentVariable("P3D_SKNOLINETOOL") == "0";

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

    /// <summary>
    /// k131 (замечание 28.10, 21:31, п. 2): выход из эскиза на время «Настройки эскиза» — новый пустой эскиз не удаляется
    /// (прежде «Настройка» закрывала его как пустой); после настройки эскиз снова новый (пустым — удалится при «Готово»).
    /// P3D_SKSETUPKEEP=0 — прежнее.
    /// </summary>
    public (bool WasNew, Func<Sketch, Operation>? Then) FinishSketchForSetup()
    {
        bool wasNew = _sketchIsNew && !SetupKeepOff;
        var then = _afterSketch;
        _keepEmptySketch = wasNew;
        try { FinishSketch(); } finally { _keepEmptySketch = false; }
        return (wasNew, then);
    }
    /// <summary>Снова в эскиз после настройки: новый — остаётся новым (и с операцией «после эскиза»).</summary>
    public void ResumeSketchAfterSetup(Sketch sk, bool wasNew, Func<Sketch, Operation>? then)
    {
        EditSketch(sk);
        if (!wasNew) return;
        _sketchIsNew = true; _skEdit = null; _afterSketch = then;
    }
    private bool _keepEmptySketch;
    static readonly bool SetupKeepOff = Environment.GetEnvironmentVariable("P3D_SKSETUPKEEP") == "0";

    public void FinishSketch()
    {
        Commands.SketchEditCommands.Mode = Commands.SketchEditCommands.EdgeMode.None;
        if (View.ActiveSketch is not { } sk) return;
        View.ToolFinish();
        View.ExitSketch();
        _refsPanel = false; _resolvePanel = false; _pendingEdit = null;
        bool empty = sk.Entities.All(e => e.Role == SkRole.Reference);
        // k116: эскиз не изменился — деталь из снимка расчёта, шаг отмены не нужен
        if (!_sketchIsNew && RestoreSketchEdit(revertSketch: false)) Session.DiscardEdit();
        else
        {
        _skEdit = null;
        SketchRollbackEnd();
        if (empty && _sketchIsNew && !_keepEmptySketch && sk.Parent is Part owner) { owner.Remove(sk); Session.DiscardEdit(); }
        else
        {
            sk.NotifyChanged(sk);
            Session.CommitEdit();
        }
        }
        ShowHint(null);
        RebuildUi();
        ScheduleTree();
        var then = _keepEmptySketch ? null : _afterSketch;
        _afterSketch = null;
        if (_keepEmptySketch) return;
        var back = AfterSketchCancel; AfterSketchCancel = null;
        if (empty && back != null) { View.Selection.Clear(); StartOperation(back()); return; }
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
        _refsPanel = false; _resolvePanel = false; _pendingEdit = null;
        View.ExitSketch();
        // k116 (п. 9): отмена правки — эскиз из снимка, деталь — из снимка расчёта (без перестроения)
        if (!_sketchIsNew && RestoreSketchEdit(revertSketch: true)) Session.DiscardEdit();
        else
        {
        _skEdit = null;
        SketchRollbackEnd();
        // новый эскиз — просто убрать (деталь не заменяется); правка — снимок до неё (вид держит прежнюю деталь)
        if (_sketchIsNew && sk.Parent is Part owner) { owner.Remove(sk); Session.DiscardEdit(); }
        else Session.CancelEdit();
        }
        _afterSketch = null;
        ShowHint(null);
        RebuildUi();
        ScheduleTree();
        // k107: эскиз, начатый из операции (протяжка: «Создать эскиз сечения»), отменили — назад в операцию
        var back = AfterSketchCancel; AfterSketchCancel = null;
        if (back != null) { View.Selection.Clear(); StartOperation(back()); }
    }

    /// <summary>k107: операция, в которую вернуться при отмене эскиза, начатого из неё.</summary>
    public Func<Operation>? AfterSketchCancel;

    // ================================================================ дерево

    private TreeNode? _barDrag;

    /// <summary>
    /// Строка отката — к узлу дерева: ниже узла (lower) или выше. Деталь: после операции (её эскиз — вместе с ней), выше
    /// первой операции — 0; сборка: после компонента. Ниже строки — не в расчёте (подавлены). Один шаг отмены.
    /// </summary>
    public bool MoveRollbackBar(TreeNode bar, TreeNode target, bool lower)
    {
        if (bar.Item is InfoItem { Role: "rollback", Owner: var part })
        {
            var item = target.Item;
            if (item is Sketch tsk && part.Features.FirstOrDefault(f => f.ConsumedSketchIds().Contains(tsk.Id)) is { } owner) { item = owner; lower = true; }
            int at;
            if (item is Feature f && part.Features.Contains(f)) at = part.Features.IndexOf(f) + (lower ? 1 : 0);
            else if (ReferenceEquals(item, part) || item is InfoItem { Role: "params" or "material" or "configs" or "body" or "quilt" } ii && ii.Owner == part) at = 0;
            else if (item is DatumPlane or DatumPoint or DatumAxis or Sketch && item.Parent == part) at = 0;
            else return false;
            int stage = at >= part.Features.Count ? -1 : part.ClampStage(at);
            if (stage == part.StageIndex || (stage < 0 && !part.InInsertMode)) return false;
            Session.Edit(L("Строка отката"), () => { if (stage < 0) part.EndInsertMode(resume: true); else { part.StageIndex = stage; if (Part.StageKeepOffPublic) part.Invalidate(); else part.MarkStageMoved(); } });
        }
        else if (bar.Item is InfoItem { Role: "asm_rollback", Asm: { } asm })
        {
            var parts = asm.Parts.ToList();
            int at;
            if (target.Item is Part tp && parts.Contains(tp)) at = parts.IndexOf(tp) + (lower ? 1 : 0);
            else if (ReferenceEquals(target.Item, asm)) at = 0;
            else return false;
            int stage = at >= parts.Count ? -1 : at;
            if (stage == asm.StageIndex) return false;
            Session.Edit(L("Строка отката сборки"), () => { asm.StageIndex = stage; asm.NotifyChanged(asm); });
        }
        else return false;
        View.MarkDirty(); RebuildTree();
        return true;
    }

    private void WireTree()
    {
        Tree.SelectionChanged += (_, _) =>
        {
            if (_syncingTree) return;
            // k115 (п. 6): несколько строк (Ctrl, Shift) — все их элементы в выделение вида
            var nodes = TreeMultiOff ? new List<TreeNode>() : Tree.SelectedItems.OfType<TreeNode>().ToList();
            if (nodes.Count > 1)
            {
                // k118 (п. 5): выделение вида — из дерева; обратная синхронизация дерева в это время не трогает его строки
                // (прежде она перезаписывала выделение дерева посреди щелчка с Ctrl/Shift — оставалась одна строка)
                _treeDriving = !TreeDriveOff;
                try
                {
                    View.Selection.Clear();
                    foreach (var nn in nodes) foreach (var r in RefsOf(nn.Item)) View.Selection.Select(r, additive: true);
                }
                finally { _treeDriving = false; }
                ShowPinnedContour(null);
                return;
            }
            if (Tree.SelectedItem is TreeNode n)
            {
                SelectItem(n.Item);
                // k115 (п. 5): скрытый элемент — очертанием на виде, пока выбран
                ShowPinnedContour(n.Item.Visible || n.Item is InfoItem ? null : n.Item);
            }
        };
        Tree.AddHandler(PointerMovedEvent, (_, e) =>
        {
            var src = e.Source as Visual;
            var node = (src as StyledElement)?.DataContext as TreeNode;
            View.HighlightFromTree(node?.Item);
            ShowHoverContour(node?.Item);
        }, RoutingStrategies.Tunnel);
        Tree.PointerExited += (_, _) => { View.HighlightFromTree(null); ShowHoverContour(null); };
        // (23:43) п. 16: строка отката тянется мышью между элементами дерева
        Tree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is TreeNode { IsBar: true } bn && e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed && bn.Item is InfoItem { Role: "rollback" or "asm_rollback" })
            {
                _barDrag = bn; e.Pointer.Capture(Tree); e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_barDrag is not { } bn) return;
            _barDrag = null; e.Pointer.Capture(null); e.Handled = true;
            var hit = Tree.InputHitTest(e.GetPosition(Tree)) as Visual;
            var tvi = hit?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            if (tvi?.DataContext is TreeNode target)
            {
                bool lower = e.GetPosition(tvi).Y > Math.Min(tvi.Bounds.Height, 24) / 2;
                MoveRollbackBar(bn, target, lower);
            }
        }, RoutingStrategies.Tunnel);
        // k97 (замечание 3): операция перетаскивается мышью по дереву; полоса показывает ближайшее к курсору ДОПУСТИМОЕ
        // место (не выше родителей, не ниже потомков)
        Tree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _featDrag = null; _featDragOn = false; _sketchDrag = null;
            if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed || FeatureDragOff) return;
            if ((e.Source as StyledElement)?.DataContext is TreeNode { IsBar: false, IsReference: false } n && DragFeatureOf(n.Item) is { } f)
                _featDrag = (f, e.GetPosition(Tree));
            // k109: эскиз, не ушедший в операцию, — тоже тянется (его место среди операций)
            else if ((e.Source as StyledElement)?.DataContext is TreeNode { IsBar: false, Item: Sketch ls } && !SketchDragOff && Session.Part is { } lp
                     && lp.Sketches.Contains(ls) && !lp.Features.Any(x => x.ConsumedSketchIds().Contains(ls.Id)))
                _sketchDrag = (ls, e.GetPosition(Tree));
        }, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_sketchDrag is { } sd0)
            {
                var sp = e.GetPosition(Tree);
                if (!_featDragOn)
                {
                    if ((sp - sd0.Start).X * (sp - sd0.Start).X + (sp - sd0.Start).Y * (sp - sd0.Start).Y < 36) return;
                    _featDragOn = true; e.Pointer.Capture(Tree);
                }
                ShowSketchDrop(sd0.S, sp);
                e.Handled = true;
                return;
            }
            if (_featDrag is not { } fd) return;
            var p = e.GetPosition(Tree);
            if (!_featDragOn)
            {
                if ((p - fd.Start).X * (p - fd.Start).X + (p - fd.Start).Y * (p - fd.Start).Y < 36) return;
                _featDragOn = true; e.Pointer.Capture(Tree);
            }
            ShowFeatureDrop(fd.F, p);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_sketchDrag is { } sd1)
            {
                bool son = _featDragOn; _sketchDrag = null; _featDragOn = false;
                TreeDropLine.IsVisible = false;
                if (!son) return;
                e.Pointer.Capture(null); e.Handled = true;
                if (SketchDropSlot(sd1.S, e.GetPosition(Tree)) is int slot) MoveSketchTo(sd1.S, slot);
                return;
            }
            if (_featDrag is not { } fd) return;
            bool on = _featDragOn; _featDrag = null; _featDragOn = false;
            TreeDropLine.IsVisible = false;
            if (!on) return;
            e.Pointer.Capture(null); e.Handled = true;
            if (FeatureDropIndex(fd.F, e.GetPosition(Tree)) is int to) Commands.TreeCommands.MoveTo(this, fd.F, to);
        }, RoutingStrategies.Tunnel);
        Tree.DoubleTapped += (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is not TreeNode n) return;
            if (n.Item is Feature or Sketch || EditablePlane(n.Item)) EditItem(n.Item);
            else if (n.Item is Part) View.ZoomToFit();
            else if (n.Item is InfoItem ii) OpenInfo(ii);
        };
        Tree.ContextRequested += (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is not TreeNode n) return;
            SelectItem(n.Item);
            var ids = new List<string>();
            if (n.Item is Feature or Sketch || EditablePlane(n.Item)) ids.Add("edit.feature");
            if (n.Item is Feature) ids.AddRange(new[] { "edit.suppress", "tree.resume", "tree.edit_dims", "tree.insert_here", "tree.reorder_up", "tree.reorder_down", "tree.group", "tree.parents_children", "tree.replace_refs" });
            if (n.Item is Sketch) ids.AddRange(new[] { "sk.setup", "tree.replace_refs" });
            // k115 (п. 7): группа — разгруппировать, отразить, массив, копировать, удалить, подавить
            if (n.Item is InfoItem { Role: "group" }) ids.AddRange(new[] { "tree.group", "op.mirror", "op.pattern_linear", "op.pattern_circular", "edit.suppress" });
            ids.AddRange(SuggestedOps().Where(i => i != "edit.feature" && i != "edit.suppress"));
            ids.AddRange(new[] { "edit.rename", "edit.copy", "edit.paste", "view.hide", "view.isolate", "edit.delete" });
            if (n.Item is Part) ids.AddRange(new[] { "part.material", "part.units", "part.configurations", "an.mass", "an.check_body" });
            var items = ids.Distinct().Where(id => _reg.Find(id) != null).Select(id => (object)Item(_reg[id])).ToList();
            new ContextMenu { ItemsSource = items }.Open((Control)e.Source!);
            e.Handled = true;
        };
    }

    /// <summary>k99: значок «Сечение» — у угла рамки плоскости сечения, ближайшего к правому верхнему углу экрана.</summary>
    public void PlaceSectionBadge()
    {
        if (View.SectionCorners() is not { } cs || InSketch) { SectionBadge.IsVisible = false; return; }
        Point? best = null;
        foreach (var c in cs)
            if (View.Project(c) is { } q && (best == null || q.X - q.Y > best.Value.X - best.Value.Y)) best = q;
        if (best is not { } b) { SectionBadge.IsVisible = false; return; }
        double x = Math.Clamp(b.X + 4, 0, Math.Max(0, View.Bounds.Width - 30)), y = Math.Clamp(b.Y - 32, 0, Math.Max(0, View.Bounds.Height - 30));
        Canvas.SetLeft(SectionBadge, x); Canvas.SetTop(SectionBadge, y);
        SectionBadge.IsVisible = true;
    }

    private (Feature F, Point Start)? _featDrag;
    private (Sketch S, Point Start)? _sketchDrag;
    static readonly bool SketchDragOff = Environment.GetEnvironmentVariable("P3D_SKETCHDRAG") == "0";

    /// <summary>
    /// k109: место эскиза при отпускании в точке p — после скольких операций (ближайшая строка операции: ниже середины —
    /// после неё); в пределах Part.SketchSlotRange; null — некуда.
    /// </summary>
    public int? SketchDropSlot(Sketch sk, Point p)
    {
        if (Session.Part is not { } part || part.Features.Count == 0) return null;
        int bestK = -1; double bestD = double.MaxValue; bool below = false;
        for (int k = 0; k < part.Features.Count; k++)
        {
            if (TreeRowRect(part.Features[k]) is not { } r) continue;
            double d = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            if (d < bestD) { bestD = d; bestK = k; below = p.Y > r.Center.Y; }
        }
        if (bestK < 0) return null;
        int slot = below ? bestK + 1 : bestK;
        var (lo, hi) = part.SketchSlotRange(sk);
        return Math.Clamp(slot, lo, hi);
    }

    private void ShowSketchDrop(Sketch sk, Point p)
    {
        TreeDropLine.IsVisible = false;
        if (Session.Part is not { } part || SketchDropSlot(sk, p) is not int slot) return;
        var anchor = slot == 0 ? part.Features[0] : part.Features[slot - 1];
        if (TreeRowRect(anchor) is not { } r) return;
        double y = slot == 0 ? r.Top - 1.5 : r.Bottom - 1.5;
        Canvas.SetLeft(TreeDropLine, r.Left); Canvas.SetTop(TreeDropLine, y);
        TreeDropLine.Width = Math.Max(60, Tree.Bounds.Width - r.Left - 8);
        TreeDropLine.Opacity = slot == part.SketchSlot(sk) ? 0.35 : 1;
        TreeDropLine.IsVisible = true;
        TreeDropTarget = slot;
    }

    /// <summary>k109: переставить эскиз в дереве (после slot операций) — шаг отмены; расчёт не меняется.</summary>
    public bool MoveSketchTo(Sketch sk, int slot)
    {
        if (Session.Part is not { } part) return false;
        var (lo, hi) = part.SketchSlotRange(sk);
        slot = Math.Clamp(slot, lo, hi);
        if (slot == part.SketchSlot(sk)) return false;
        uint id = sk.Id;
        Session.Edit(L("Переставить эскиз"), () => { if (Session.Part!.Sketches.FirstOrDefault(k => k.Id == id) is { } s2) Session.Part.PlaceSketch(s2, slot); });
        RebuildTree();
        return true;
    }
    private bool _featDragOn;
    static readonly bool FeatureDragOff = Environment.GetEnvironmentVariable("P3D_TREEDRAG") == "0";

    /// <summary>Операция детали, которую тянут: сама операция или её эскиз (тянется вместе с операцией).</summary>
    private Feature? DragFeatureOf(ModelItem item)
    {
        if (Session.Part is not { } part) return null;
        if (item is Feature f && part.Features.Contains(f)) return f;
        if (item is Sketch sk) return part.Features.FirstOrDefault(x => x.ConsumedSketchIds().Contains(sk.Id));
        return null;
    }

    /// <summary>Разбор (k104): строка дерева элемента в координатах окна.</summary>
    public Rect? TreeRowInWindow(ModelItem item) => TreeRowRect(item) is { } r && Tree.TranslatePoint(r.Position, this) is { } at ? new Rect(at, r.Size) : null;
    /// <summary>Разбор: текст всплывающего сообщения.</summary>
    public string ToastShown => Toast.IsVisible ? ToastText.Text ?? "" : "";

    /// <summary>Строка дерева (заголовок узла) для элемента: прямоугольник в координатах дерева.</summary>
    private Rect? TreeRowRect(ModelItem item)
    {
        foreach (var tvi in Tree.GetVisualDescendants().OfType<TreeViewItem>())
        {
            if (tvi.DataContext is not TreeNode n || !ReferenceEquals(n.Item, item) || !tvi.IsEffectivelyVisible) continue;
            var head = tvi.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_HeaderPresenter") ?? (Control)tvi;
            if (head.TranslatePoint(new Point(0, 0), Tree) is not { } at) continue;
            return new Rect(at, head.Bounds.Size);
        }
        return null;
    }

    /// <summary>Итоговый номер операции при отпускании в точке p (ближайшее допустимое место); null — некуда.</summary>
    public int? FeatureDropIndex(Feature f, Point p)
    {
        if (FeatureDragOff || Session.Part is not { } part) return null;
        int me = part.Features.IndexOf(f);
        if (me < 0) return null;
        // ближайшая к курсору строка операции (эскиз в дереве — внутри своей операции)
        int bestK = -1; double bestD = double.MaxValue; bool below = false;
        for (int k = 0; k < part.Features.Count; k++)
        {
            if (TreeRowRect(part.Features[k]) is not { } r) continue;
            double d = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            if (d < bestD) { bestD = d; bestK = k; below = p.Y > r.Center.Y; }
        }
        if (bestK < 0) return null;
        int to = below ? (bestK < me ? bestK + 1 : bestK) : (bestK <= me ? bestK : bestK - 1);
        var (lo, hi) = Commands.TreeCommands.MoveRange(part, f);
        if (lo > hi) return null;
        return Math.Clamp(to, lo, hi);
    }

    private void ShowFeatureDrop(Feature f, Point p)
    {
        TreeDropLine.IsVisible = false;
        if (Session.Part is not { } part || FeatureDropIndex(f, p) is not int to) return;
        int me = part.Features.IndexOf(f);
        // полоса: выше операции, на место которой встанет (вверх), или ниже неё (вниз); на своём месте — у самой операции
        var anchor = part.Features[to];
        if (TreeRowRect(anchor) is not { } r) return;
        double y = to < me ? r.Top - 1.5 : to > me ? r.Bottom - 1.5 : r.Center.Y - 1.5;
        Canvas.SetLeft(TreeDropLine, r.Left);
        Canvas.SetTop(TreeDropLine, y);
        TreeDropLine.Width = Math.Max(60, Tree.Bounds.Width - r.Left - 8);
        TreeDropLine.Opacity = to == me ? 0.35 : 1;
        TreeDropLine.IsVisible = true;
        TreeDropTarget = to;
    }

    /// <summary>Разбор: номер, куда встанет перетаскиваемая операция (последний показ полосы).</summary>
    public int TreeDropTarget { get; private set; } = -1;

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
        foreach (var n in AllNodes) RememberExpanded(n);
        List<TreeNode> roots;
        using (Diagnostics.UiTimes.Measure("окно: дерево — узлы")) roots = TreeNode.Build(Session.Document, Session.IsAssembly);
        // k113: ДЕРЕВО ТО ЖЕ — НЕ ПЕРЕСТРАИВАТЬ ВИД. Каждый шаг предпросмотра (начало и конец расчёта) пересобирал дерево целиком
        // (новый источник — заново все строки: 7–20 мс в потоке окна на каждый шаг ручки), хотя строки не менялись. Подпись —
        // всё, что строка показывает (имя, значки, ошибка, подсказка, глаз, вложенность). P3D_TREESIG=0 — всегда заново.
        string sig = TreeSignature(roots);
        if (!TreeSigOff && sig == _treeSig && Tree.ItemsSource != null) { Interlocked.Increment(ref TreeRebuildsSkipped); SyncTreeFromSelection(); return; }
        if (Environment.GetEnvironmentVariable("P3D_TREESIGDBG") == "1" && _treeSig != null)
        {
            var a = _treeSig.Split('\n'); var b = sig.Split('\n');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                if (i >= a.Length || i >= b.Length || a[i] != b[i]) { Console.WriteLine($"TREESIG {i}: «{(i < a.Length ? a[i] : "")}» → «{(i < b.Length ? b[i] : "")}»"); break; }
        }
        _treeSig = sig;
        _roots = roots;
        KeyNodes(_roots);
        foreach (var n in _roots.SelectMany(r => r.SelfAndDescendants()))
            if (ExpandedOf(n) is { } ex) n.RestoreExpanded(ex);
        _syncingTree = true;
        Tree.ItemsSource = _roots;
        _syncingTree = false;
        SyncTreeFromSelection();
    }

    public IEnumerable<TreeNode> AllNodes => _roots.SelectMany(r => r.SelfAndDescendants());
    /// <summary>k115: дерево — для проверок (несколько строк).</summary>
    public TreeView TreeControl => Tree;

    static readonly bool TreeSigOff = Environment.GetEnvironmentVariable("P3D_TREESIG") == "0";
    public static long TreeRebuildsSkipped;
    private string? _treeSig;
    /// <summary>Сбросить подпись дерева (принудительная перестройка вида дерева).</summary>
    public void ForgetTreeSignature() => _treeSig = null;
    static string TreeSignature(IEnumerable<TreeNode> roots)
    {
        var sb = new System.Text.StringBuilder();
        void N(TreeNode n, int depth)
        {
            // строки-сведения (параметры, полоса отката) создаются заново при каждой сборке — у них свой номер каждый раз
            sb.Append(depth).Append('|').Append(n.Item is InfoItem ii ? "info:" + ii.Role : n.Item.Id.ToString()).Append('|').Append(n.Item.GetType().Name).Append('|').Append(n.Name).Append('|')
              .Append(n.IsVisible).Append('|').Append(n.Error).Append('|').Append(n.IsDim).Append('|').Append(n.IconFile).Append('|')
              .Append(n.StateFile).Append('|').Append(n.StateGlyph).Append('|').Append(n.StateTip).Append('|').Append(n.Tooltip).Append('|')
              .Append(n.Glyph).Append('|').Append(n.HasEye).Append('\n');
            foreach (var c in n.Children) N(c, depth + 1);
        }
        foreach (var r in roots) N(r, 0);
        return sb.ToString();
    }

    /// <summary>Свернуть или развернуть всё, включая деталь (п. 5).</summary>
    public void SetTreeExpanded(bool expanded)
    {
        foreach (var n in AllNodes.ToList())
            if (n.Children.Count > 0) { n.IsExpanded = expanded; RememberExpanded(n); }
    }

    // ---------------------------------------------------------------- k116: развёрнутые ветки — по постоянным ключам, в файле

    /// <summary>
    /// k116 (замечание 16:00, п. 7): СОСТОЯНИЕ «РАЗВЁРНУТ/СВЁРНУТ» — по постоянному пути узла внутри детали (ключ операции,
    /// имя эскиза, имя группы, роль строки), в Part.TreeState — пишется в файл. Прежде — по номеру элемента в памяти окна:
    /// у папок групп номер новый при каждой сборке дерева (папка разворачивалась сама), после открытия файла всё — заново.
    /// Узлы вне деталей (сборка) — в памяти окна. P3D_TREESTATE=0 — прежнее.
    /// </summary>
    private readonly Dictionary<TreeNode, (Part? Part, string Key)> _nodeKeys = new();
    private readonly Dictionary<string, bool> _expandAsm = new();
    static readonly bool TreeStateOff = Environment.GetEnvironmentVariable("P3D_TREESTATE") == "0";

    static string LocalKey(TreeNode n) => n.IsReference && n.Item is Feature rf ? "r:" + rf.Key : n.Item switch
    {
        Feature f => "f:" + f.Key,
        Part => "p",
        InfoItem ii => "i:" + ii.Role + ":" + ii.Name,
        _ => n.Item.Kind + ":" + n.Item.Name,
    };

    private void KeyNodes(IEnumerable<TreeNode> roots)
    {
        _nodeKeys.Clear();
        void Walk(TreeNode n, Part? part, string path)
        {
            var key = (path.Length > 0 ? path + "/" : "") + LocalKey(n);
            Part? own = part;
            if (n.Item is Part p && !n.IsReference) { own = p; key = "p"; }
            _nodeKeys[n] = (own, key);
            foreach (var c in n.Children) Walk(c, own, key);
        }
        foreach (var r in roots) Walk(r, null, "");
    }

    private bool? ExpandedOf(TreeNode n)
    {
        if (TreeStateOff) return _expand.TryGetValue(n.Item.Id, out bool e0) ? e0 : null;
        if (!_nodeKeys.TryGetValue(n, out var k)) return null;
        var d = k.Part?.TreeState ?? _expandAsm;
        return d.TryGetValue(k.Key, out bool e) ? e : null;
    }

    private void RememberExpanded(TreeNode n)
    {
        if (TreeStateOff) { _expand[n.Item.Id] = n.IsExpanded; return; }
        if (!_nodeKeys.TryGetValue(n, out var k)) return;
        (k.Part?.TreeState ?? _expandAsm)[k.Key] = n.IsExpanded;
    }

    // ---------------------------------------------------------------- k115: контуры элементов дерева, несколько строк

    static readonly bool TreeMultiOff = Environment.GetEnvironmentVariable("P3D_TREEMULTI") == "0";
    static readonly bool TreeContoursOff = Environment.GetEnvironmentVariable("P3D_TREECONTOURS") == "0";
    private ModelItem? _hoverContour, _pinnedContour;

    /// <summary>Наведение на операцию в дереве: что она добавила (зелёным) и убрала (красным).</summary>
    public void ShowHoverContour(ModelItem? it)
    {
        if (TreeContoursOff || ReferenceEquals(it, _hoverContour)) return;
        _hoverContour = it;
        ModelViewport.Contour? c = null;
        try { c = TreeContours.MaterialOf(it); } catch (Exception ex) { AppLog.Write("tree contour", ex); }
        View.SetContours("hover", c == null ? null : new[] { c });
    }

    /// <summary>Скрытый элемент, выбранный в дереве: очертание пунктиром до щелчка в другом месте.</summary>
    public void ShowPinnedContour(ModelItem? it)
    {
        if (TreeContoursOff) return;
        _pinnedContour = it;
        ModelViewport.Contour? c = null;
        if (it != null) try { c = TreeContours.OutlineOf(it); } catch (Exception ex) { AppLog.Write("tree outline", ex); }
        View.SetContours("pinned", c == null ? null : new[] { c });
    }

    /// <summary>Выбор вида для элемента дерева: группа — её операции.</summary>
    public List<PickRef> RefsOf(ModelItem item)
    {
        if (item is InfoItem { Role: "group" } g)
            return g.Owner.Features.Where(x => x.Group == g.Name).Select(f => new PickRef(f.Id, PickKind.Feature, f.Parent, f)).ToList();
        var r = RefOf(item);
        return r.IsEmpty ? new() : new() { r };
    }

    private static PickRef RefOf(ModelItem item) => item switch
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

    /// <summary>Выбор элемента модели (из дерева): в выделение вида.</summary>
    public void SelectItem(ModelItem item)
    {
        // k115 (п. 6): группа — все её операции
        if (item is InfoItem { Role: "group" } && CurrentOperation == null)
        {
            View.Selection.Clear();
            foreach (var gr in RefsOf(item)) View.Selection.Select(gr, additive: true);
            return;
        }
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

    private bool _treeDriving;
    static readonly bool TreeDriveOff = Environment.GetEnvironmentVariable("P3D_TREEDRIVE") == "0";
    private void SyncTreeFromSelection()
    {
        if (_treeDriving) return;
        // k115: несколько элементов — несколько строк (операции группы, Ctrl в виде)
        if (!TreeMultiOff && View.Selection.Items.Count > 1)
        {
            var want = new HashSet<uint>(View.Selection.Items.Where(r => r.Target is ModelItem).Select(r => r.Id));
            var ns = AllNodes.Where(n => n.Item is not InfoItem && want.Contains(n.Item.Id) && !n.IsReference).GroupBy(n => n.Item.Id).Select(g => g.First()).ToList();
            if (ns.Count > 1)
            {
                _syncingTree = true;
                try { Tree.SelectedItems.Clear(); foreach (var n in ns) Tree.SelectedItems.Add(n); }
                finally { _syncingTree = false; }
                return;
            }
        }
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
        if (_resolvePanel || _refsPanel) return;   // k96: панель эскиза («Ссылки», «Разрешить эскиз») не перекрывается свойствами
        using var _ui = Diagnostics.UiTimes.Measure("окно: свойства");
        PropsBody.Children.Clear();
        PropsButtons.Children.Clear();
        var sel = View.Selection;
        // k120 (замечание 15:06, п. 1): свойства детали — и когда ничего не выбрано, и по щелчку на детали в дереве (на виде)
        var part = sel.IsEmpty ? Session.Part
            : !PartPanelOff && sel.Count == 1 && sel.Items[0] is { Kind: PickKind.Body, Target: Part sp } ? sp : null;
        if (part != null)
        {
            ShowPartProperties(part);
            return;
        }
        PropsTitle.Text = L("ВЫБРАНО");
        ShowSelectionList();
    }

    /// <summary>k120: виды выбранного, скрытые в списке (пользователь снял флажок); не задано — по фильтру выбора вида.</summary>
    private readonly Dictionary<PickKind, bool> _listKinds = new();
    /// <summary>Разбор: строки списка выбранного (номер элемента → строка).</summary>
    public Dictionary<uint, Border> SelectionRows { get; } = new();
    public const int SelectionListMax = 200;
    public static readonly bool SelListOff = Environment.GetEnvironmentVariable("P3D_SELLIST") == "0";

    /// <summary>
    /// k120 (замечание 15:06, пп. 8.1, 8.2): СПИСОК ВЫБРАННОГО — сверху флажки видов (по умолчанию — как фильтр выбора вида),
    /// строки с крестиком «снять выбор»; строка под курсором подсвечивает свой элемент на виде. Строк — не больше 200
    /// (рамка на тысячи элементов не строит тысячи строк).
    /// </summary>
    private void ShowSelectionList()
    {
        var sel = View.Selection;
        SelectionRows.Clear();
        Row("Выбрано", sel.Count.ToString());
        if (SelListOff)
        {
            foreach (var r in sel.Items.Take(12)) Row(Selection.KindName(r), Selection.Label(r));
            return;
        }
        var kinds = sel.Items.GroupBy(r => r.Kind).Select(g => (g.Key, g.Count())).ToList();
        bool Shown(PickKind k) => _listKinds.TryGetValue(k, out var on) ? on : sel.Accepts(k) || k is PickKind.Body or PickKind.Feature or PickKind.Assembly;
        if (kinds.Count > 1 || kinds.Any(k => !Shown(k.Key)))
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
            foreach (var (k, n) in kinds)
            {
                var kk = k;
                var cb = new CheckBox { Content = $"{Selection.KindName(new PickRef(1, k, null, null))} ({n})", IsChecked = Shown(k), Margin = new Thickness(0, 0, 10, 0), Name = "selkind_" + k };
                cb.IsCheckedChanged += (_, _) => { _listKinds[kk] = cb.IsChecked == true; Dispatcher.UIThread.Post(ShowProperties); };
                wrap.Children.Add(cb);
            }
            PropsBody.Children.Add(wrap);
        }
        var visible = sel.Items.Where(r => Shown(r.Kind)).ToList();
        foreach (var r in visible.Take(SelectionListMax))
        {
            var rr = r;
            var x = new Button { Content = "×", Classes = { "tool" }, Padding = new Thickness(6, 0), MinHeight = 20, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(x, L("Снять выбор"));
            x.Click += (_, _) => { View.HighlightSelected(0); View.Selection.Deselect(rr.Id); };
            var text = new TextBlock { Text = Selection.KindName(r) + ": " + Selection.Label(r), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(text, text.Text);
            var dp = new DockPanel();
            DockPanel.SetDock(x, Dock.Right);
            dp.Children.Add(x); dp.Children.Add(text);
            var b = new Border { Child = dp, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1), Background = Brushes.Transparent };
            b.PointerEntered += (_, _) => { b.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)); View.HighlightSelected(rr.Id); };
            b.PointerExited += (_, _) => { b.Background = Brushes.Transparent; View.HighlightSelected(0); };
            SelectionRows[r.Id] = b;
            PropsBody.Children.Add(b);
            if (r.Target is Feature f && sel.Count == 1)
            {
                Row("Описание", f.Summary);
                if (f.Error != null) Row("Ошибка", f.Error);
            }
        }
        if (visible.Count > SelectionListMax) PropsBody.Children.Add(new TextBlock { Text = L("… и ещё {0}", visible.Count - SelectionListMax), Classes = { "dim" } });
    }

    public static readonly bool CmdRefreshOff = Environment.GetEnvironmentVariable("P3D_CMDREFRESH") == "0";
    public static readonly bool PartPanelOff = Environment.GetEnvironmentVariable("P3D_PARTPANEL") == "0";

    /// <summary>
    /// k120 (замечание 15:06, пп. 1–5): ПАНЕЛЬ СВОЙСТВ ДЕТАЛИ — всё, что прежде было узлами дерева («Параметры», «Материал»,
    /// «Тело») и строками на двух языках: правятся имя, номер детали, материал, система единиц (готовые и свои), допуски;
    /// показываются масса, объём, площадь, габариты — в единицах системы детали. Всё пишется в документ.
    /// </summary>
    private void ShowPartProperties(Part part)
    {
        PropsTitle.Text = L("ДЕТАЛЬ");
        var sys = part.Units;
        EditRow("Имя", part.Name, v =>
        {
            if (v.Length == 0) return;
            part.Name = v; part.NotifyChanged(part);
            RebuildTree(); OnStateChanged();
        });
        EditRow("Номер детали", part.Info.Code, v => part.Info.Code = v);
        MaterialRow(part);
        UnitSystemRow(part);
        Row("Тел", part.Bodies.Count.ToString());
        if (part.Bodies.Count > 0)
        {
            Row("Объём", DisplayUnits.Format(part.Volume, Core.Dimension.Volume, sys, "0.###"));
            // 21.10: площадь — по запросу; не посчитана — считается в фоне, панель обновляется (поток окна не ждёт квадратуру)
            if (part.AreaPending)
            {
                Row("Площадь", "…");
                var pp = part;
                _ = Task.Run(() => { _ = pp.Area; }).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (_propsOn && CurrentOperation == null) ShowProperties(); }));
            }
            else Row("Площадь", DisplayUnits.Format(part.Area, Core.Dimension.Area, sys, "0.###"));
            Row("Масса", DisplayUnits.Format(part.Mass, Core.Dimension.Mass, sys, "0.####"));
            if (!part.Bounds.IsEmpty)
            {
                var sz = part.Bounds.Size; var lu = sys.UnitFor(Core.Dimension.Length);
                string N(double v) => (v / lu.Factor).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                Row("Габариты", $"{N(sz.X)} × {N(sz.Y)} × {N(sz.Z)} " + DisplayUnits.Symbol(lu.Symbol));
            }
        }
        Row("Операций", part.Children.OfType<Feature>().Count().ToString());
        if (!PartPropsOff)
        {
            ChoiceRow("Допуски", DocumentInfo.ToleranceChoices, part.Info.Tolerances, v => part.Info.Tolerances = v);
            Row("Сборка", part.Parent is Assembly pa ? pa.Name : "—");
            var deps = Parametric3D.Model.Params.ModelRefs.DependenciesOf(part);
            PropsBody.Children.Add(new TextBlock { Text = L("Зависит от") + (deps.Count == 0 ? ": —" : ":"), Classes = { "dim" }, Margin = new Thickness(0, 6, 0, 2) });
            foreach (var (item, where) in deps)
            {
                var link = new TextBlock { Text = (item is Assembly ? L("Сборка") : L("Деталь")) + " «" + item.Name + "»", Margin = new Thickness(12, 1, 0, 1), TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand) };
                link.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("P3.Accent"));
                ToolTip.SetTip(link, L("Ссылка в") + " " + where);
                var it = item;
                link.PointerEntered += (_, _) => View.HighlightFromTree(it);
                link.PointerExited += (_, _) => View.HighlightFromTree(null);
                PropsBody.Children.Add(link);
            }
            PartDepsShown = deps.Count;
        }
    }

    /// <summary>Готовые системы единиц и свои системы пользователя (настройки приложения).</summary>
    public static List<Core.UnitSystem> UnitSystemLibrary()
    {
        var list = Core.UnitSystem.Standard.ToList();
        foreach (var d in UserPrefs.Current.UnitSystems)
            try { var u = Core.UnitSystem.FromDictionary(d); if (!list.Any(x => x.Name == u.Name && x.SameUnits(u))) list.Add(u); } catch { }
        return list;
    }

    public static string UnitSystemText(Core.UnitSystem u) => (u.Name.Length > 0 ? L(u.Name) + " (" : "(") + string.Join(", ", new[] { u.Length, u.Mass, u.Time }.Select(DisplayUnits.Symbol)) + ")";

    /// <summary>k120 (п. 5): «Система единиц» — список систем и «Создать систему…» (свои единицы длины, массы, времени, угла).</summary>
    private void UnitSystemRow(Part part)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,Auto"), Margin = new Thickness(0, 1) };
        g.Children.Add(new TextBlock { Text = L("Система единиц"), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var lib = UnitSystemLibrary();
        int at = lib.FindIndex(x => x.SameUnits(part.Units) && (part.Units.Name.Length == 0 || x.Name == part.Units.Name));
        if (at < 0) { lib.Add(part.Units.Clone()); at = lib.Count - 1; }
        var cb = new ComboBox { ItemsSource = lib.Select(UnitSystemText).ToList(), SelectedIndex = at, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "prop_units" };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0 && cb.SelectedIndex != at) SetPartUnits(part, lib[cb.SelectedIndex]); };
        var add = new Button { Content = "+", Classes = { "tool" }, Padding = new Thickness(6, 0), Margin = new Thickness(4, 0, 0, 0), Name = "prop_units_new" };
        ToolTip.SetTip(add, L("Создать систему единиц…"));
        add.Click += async (_, _) => { if (await Dialogs.NewUnitSystem(this) is { } u) { AddUnitSystem(u); SetPartUnits(part, u); } };
        Grid.SetColumn(cb, 1); Grid.SetColumn(add, 2);
        g.Children.Add(cb); g.Children.Add(add);
        PropsBody.Children.Add(g);
    }

    /// <summary>Своя система — в настройки приложения (список систем); имя уже есть — заменяется.</summary>
    public static void AddUnitSystem(Core.UnitSystem u)
    {
        var p = UserPrefs.Current;
        p.UnitSystems.RemoveAll(d => d.TryGetValue("name", out var n) && n == u.Name);
        p.UnitSystems.Add(u.ToDictionary());
        p.Save();
    }

    public void SetPartUnits(Part part, Core.UnitSystem u, bool? convert = null) => _ = SetPartUnitsAsync(part, u, convert);

    /// <summary>Проверки: ответ на вопрос «преобразовать размеры?» без окна (true — да, false — нет, null — отмена).</summary>
    public static Func<string, Task<bool?>>? UnitQuestion;
    public static readonly bool UnitAskOff = Environment.GetEnvironmentVariable("P3D_UNITASK") == "0";
    /// <summary>Разбор: итог последней смены единиц.</summary>
    public Model.UnitConversion.Report? LastUnitReport { get; private set; }

    /// <summary>
    /// k123 (замечание 27.10, 21:55, п. 3): СМЕНА СИСТЕМЫ ЕДИНИЦ ДЕТАЛИ. Единица длины меняется — вопрос: преобразовать
    /// размеры (деталь прежняя, числа — в новых единицах, формулы-переводы снимаются) или нет (числа прежние, деталь
    /// меняет размер); «Отмена» — ничего. Один шаг отмены. P3D_UNITASK=0 — без вопроса и без преобразования (прежнее).
    /// </summary>
    public async Task SetPartUnitsAsync(Part part, Core.UnitSystem u, bool? convert = null)
    {
        var old = part.Units.Clone();
        bool lengthChanged = !UnitAskOff && old.Length != u.Length;
        bool hasModel = part.Sketches.Any() || part.Features.Count > 0 || part.LocalParameters.Count > 0;
        if (lengthChanged && hasModel && convert == null)
        {
            string q = L("Единица длины детали: {0} → {1}.", DisplayUnits.Symbol(old.Length), DisplayUnits.Symbol(u.Length)) + "\n\n" +
                       L("«Преобразовать» — деталь остаётся прежней, размеры эскизов, операций и параметры показываются в новых единицах (формулы, которые только переводили единицы, снимаются).") + "\n" +
                       L("«Не преобразовывать» — числа размеров остаются прежними в новых единицах: деталь меняет размер.");
            bool? ans;
            if (UnitQuestion != null) ans = await UnitQuestion(q);
            else
            {
                string yes = L("Преобразовать"), no = L("Не преобразовывать"), cancel = L("Отмена");
                var a = await Dialogs.Ask(this, L("Единицы детали"), q, yes, no, cancel);
                ans = a == yes ? true : a == no ? false : null;
            }
            if (ans == null) { ShowProperties(); return; }
            convert = ans;
        }
        Session.Edit(L("Единицы детали"), () =>
        {
            if (lengthChanged && hasModel) LastUnitReport = Model.UnitConversion.Apply(part, Session.Document.Parameters, old, u, convert ?? true);
            part.Units = u.Clone();
            part.LocalParameters.Units = part.Units;
        });
        if (ReferenceEquals(part, Session.Part)) DisplayUnits.From(part.Units);
        View.MarkDirty(); View.InvalidateOverlay();
        RebuildTree();
        Dispatcher.UIThread.Post(ShowProperties);
    }

    /// <summary>Предел текучести и прочности: σт, σв по-русски; σy, σu по-английски.</summary>
    static string SigY => Loc.En ? "σy" : "σт";
    static string SigU => Loc.En ? "σu" : "σв";

    /// <summary>u10: материал детали — выбор из библиотеки (группы), свойства для массы и расчёта — подсказкой.</summary>
    private void MaterialRow(Part part)
    {
        var lib = Material.Library();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        g.Children.Add(new TextBlock { Text = L("Материал"), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420, Name = "prop_material" };
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
            ToolTip.SetTip(cb, $"{pm.Designation}  ·  " + L("Плотность") + $" {pm.DensityGPerCm3:0.###} " + L("г/см³") +
                               $"  ·  E {pm.YoungModulus / 1e9:0.#} GPa, ν {pm.PoissonRatio:0.##}" +
                               (pm.YieldStrength > 0 ? $"  ·  {SigY} {pm.YieldStrength / 1e6:0} MPa" : "") + (pm.UltimateStrength > 0 ? $", {SigU} {pm.UltimateStrength / 1e6:0} MPa" : ""));
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        PropsBody.Children.Add(g);
        if (part.Material is { } mm)
        {
            Row("Плотность", $"{mm.DensityGPerCm3:0.###} " + L("г/см³"));
            if (mm.YoungModulus > 0) Row("Модуль Юнга", $"{mm.YoungModulus / 1e9:0.#} " + L("ГПа") + $", ν {mm.PoissonRatio:0.##}");
            if (mm.YieldStrength > 0 || mm.UltimateStrength > 0) Row("Прочность", (mm.YieldStrength > 0 ? $"{SigY} {mm.YieldStrength / 1e6:0} " : "") + (mm.UltimateStrength > 0 ? $"{SigU} {mm.UltimateStrength / 1e6:0} " : "") + L("МПа"));
        }
    }

    public static readonly bool PartPropsOff = Environment.GetEnvironmentVariable("P3D_PARTPROPS") == "0";
    /// <summary>Разбор (k118): ссылок «Зависит от» в последней панели детали.</summary>
    public int PartDepsShown { get; private set; }

    /// <summary>k118: строка свойства с полем ввода (правка — по Enter или уходу из поля; документ помечается изменённым).</summary>
    private void EditRow(string k, string v, Action<string> set)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Thickness(0, 1) };
        g.Children.Add(new TextBlock { Text = L(k), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        var tb = new TextBox { Text = v, MinHeight = 24, Padding = new Thickness(4, 2), Name = "prop_" + k };
        void Commit() { string nv = (tb.Text ?? "").Trim(); if (nv == v) return; set(nv); v = nv; Session.MarkModified(); }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        Grid.SetColumn(tb, 1);
        g.Children.Add(tb);
        PropsBody.Children.Add(g);
    }

    private void ChoiceRow(string k, string[] choices, string cur, Action<string> set)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Thickness(0, 1) };
        g.Children.Add(new TextBlock { Text = L(k), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        var list = choices.ToList();
        if (!list.Contains(cur)) list.Add(cur);
        var cb = new ComboBox { ItemsSource = list.Select(c => L(c)).ToList(), SelectedIndex = list.IndexOf(cur), HorizontalAlignment = HorizontalAlignment.Stretch, Name = "prop_" + k };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0 && list[cb.SelectedIndex] != cur) { cur = list[cb.SelectedIndex]; set(cur); Session.MarkModified(); } };
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        PropsBody.Children.Add(g);
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
        dlg.PeekRow = (sk, d, f) => View.ShowPeek(sk, d, f);
        await dlg.ShowDialog(this);
        View.ShowPeek(null, null, null);
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

    // ================================================================ k121: ход загрузки и перестроения

    /// <summary>
    /// k121 (замечание 27.10, 17:27, п. 2): ПОЛОСА ХОДА В СТРОКЕ СОСТОЯНИЯ. Загрузка файла (чтение и разбор — в фоне, окно не
    /// замирает) → обработка (фоновое перестроение: операций сделано из всех, по всем деталям сборки) → отображение (сцена
    /// вида по новым телам). Перестроение короче 0.3 с (шаг ручки в предпросмотре) полосу не показывает. Мутация
    /// P3D_STATUSPROGRESS=0 — полосы нет.
    /// </summary>
    public static readonly bool StatusProgressOff = Environment.GetEnvironmentVariable("P3D_STATUSPROGRESS") == "0";
    private DispatcherTimer? _progressTimer;
    private string? _loadingFile;
    private DateTime _busySince = DateTime.MaxValue, _sceneWaitUntil = DateTime.MinValue;
    /// <summary>Разбор и проверки: что показано (этап, процент); null — полоса скрыта.</summary>
    public (string Stage, double Percent, bool Indeterminate)? ProgressShown { get; private set; }
    /// <summary>Проверки: все показанные этапы за время жизни окна.</summary>
    public List<string> ProgressStages { get; } = new();
    /// <summary>Проверки: показывать полосу сразу (без паузы 0.3 с).</summary>
    public static double ProgressDelayMs = 300;

    private void StartProgress()
    {
        if (StatusProgressOff) return;
        if (_busySince == DateTime.MaxValue) _busySince = DateTime.Now;
        if (_progressTimer == null)
        {
            _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => UpdateProgress());
        }
        if (!_progressTimer.IsEnabled) _progressTimer.Start();
        UpdateProgress();
    }

    /// <summary>Один шаг полосы хода (таймер окна, 10 раз в секунду).</summary>
    public void UpdateProgress()
    {
        if (StatusProgressOff) return;
        string? stage = null; double pct = 0; bool ind = false;
        var doc = Session?.Document;
        if (_loadingFile != null) { stage = L("Загрузка «{0}»…", _loadingFile); ind = true; }
        // k126: деталь показана, идут отложенные проверки (правка их прерывает)
        else if (doc != null && doc.IsVerifying) { stage = L("Проверка детали…"); ind = true; }
        else if (doc != null && doc.IsBuilding)
        {
            var (d, t) = doc.BuildProgress;
            if (t > 0)
            {
                pct = 100.0 * d / t;
                // k122: пересчёт не с начала — с какой операции (предыдущие не пересчитываются)
                stage = doc.BuildFromFeature is { } ff ? L("Пересчёт с «{0}»: {1} из {2}", ff.Name, d, t) : L("Обработка: {0} из {1}", d, t);
            }
            else { stage = L("Обработка…"); ind = true; }
            _sceneWaitUntil = DateTime.Now.AddSeconds(5);
        }
        else if (doc != null && doc.AllParts().Any(p => p.IsDirty)) { stage = L("Обработка…"); ind = true; _sceneWaitUntil = DateTime.Now.AddSeconds(5); }
        else if (DateTime.Now < _sceneWaitUntil) { stage = L("Отображение…"); pct = 100; }
        if (stage == null)
        {
            _busySince = DateTime.MaxValue; _sceneWaitUntil = DateTime.MinValue;
            _progressTimer?.Stop();
            if (ProgressShown != null) { ProgressShown = null; StatusProgress.IsVisible = false; UpdateStatus(); }
            return;
        }
        if (_busySince == DateTime.MaxValue) _busySince = DateTime.Now;
        if (_loadingFile == null && (DateTime.Now - _busySince).TotalMilliseconds < ProgressDelayMs) return;
        ProgressShown = (stage, pct, ind);
        if (ProgressStages.Count == 0 || ProgressStages[^1] != stage) ProgressStages.Add(stage);
        StatusProgress.IsVisible = true;
        StatusProgress.IsIndeterminate = ind;
        if (!ind) StatusProgress.Value = pct;
        StatusBuild.Text = ind || pct >= 100 ? stage : stage + $"  ({pct:0}%)";
        StatusBuild.Foreground = Res("P3.Warning");
    }

    /// <summary>Сцена вида построена по новым телам — этап «Отображение» окончен.</summary>
    private void OnSceneShown() { if (_sceneWaitUntil > DateTime.MinValue && Session?.Document is { IsBuilding: false } d && !d.AllParts().Any(p => p.IsDirty)) { _sceneWaitUntil = DateTime.MinValue; UpdateProgress(); } }

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
        // u13: значки состояния — идёт перестроение, требуют внимания, годно (k121: пока идёт полоса хода — её подпись)
        if (ProgressShown != null) { }
        else if (Session.Document.IsVerifying) { StatusBuild.Text = "⟳ " + L("Проверка…"); StatusBuild.Foreground = Res("P3.Warning"); }
        else if (Session.Document.IsBuilding) { StatusBuild.Text = "⟳ " + L("Перестроение…"); StatusBuild.Foreground = Res("P3.Warning"); }
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
        // в эскизе подсказка — в нижней полосе (отдельная плашка сверху лишняя)
        if (InSketch) { HintBox.IsVisible = false; HintText.Text = text ?? ""; if (_sketchHint != text) { _sketchHint = text; UpdateContextBar(); } return; }
        _sketchHint = null;
        HintBox.IsVisible = !string.IsNullOrEmpty(text);
        HintText.Text = text ?? "";
    }
    private string? _sketchHint;

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

    /// <summary>k102: показ идентификаторов размеров вместо значений (эскиз и размеры операции на виде).</summary>
    /// <summary>Мутация k102 P3D_DIMIDS=0: TAB не переключает значения/идентификаторы размеров.</summary>
    static readonly bool DimIdsOff = Environment.GetEnvironmentVariable("P3D_DIMIDS") == "0";
    public void ToggleDimensionIds()
    {
        if (DimIdsOff) return;
        SketchDimensions.ShowIds = !SketchDimensions.ShowIds;
        View.MarkDirty(); View.InvalidateOverlay();
        ShowHint(SketchDimensions.ShowIds ? L("Размеры: идентификаторы (TAB — значения)") : null);
    }

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
