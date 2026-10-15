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
using Parametric3D.App.Services;
using Parametric3D.App.ViewModels;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;

namespace Parametric3D.App.Views;

/// <summary>
/// ГЛАВНОЕ ОКНО (этапы U0–U1): верхняя полоса (файл, документ и окружение, отмена, поиск команды), дерево модели,
/// 3D-вид с панелью вида и кубом вида, панель свойств, строка состояния, палитра команд (F). Все кнопки строятся из
/// реестра команд (StandardCommands) — те же команды доступны в палитре и по клавишам.
/// </summary>
public partial class MainWindow : Window
{
    public DocumentSession Session { get; private set; } = null!;
    public ModelViewport ViewPort => View;
    private readonly CommandRegistry _reg = CommandRegistry.Instance;
    private List<TreeNode> _roots = new();
    private bool _syncingTree;
    private DispatcherTimer? _toastTimer;

    public MainWindow() : this(DocumentSession.NewPart()) { }

    public MainWindow(DocumentSession session)
    {
        InitializeComponent();
        StandardCommands.Register(this);
        BuildToolbars();
        WireView();
        WireTree();
        WirePalette();
        ApplyTheme(UserPrefs.Current.Theme != "Light");
        SetSession(session);

        _reg.Failed += (c, ex) => ShowToast($"{c.Label}: {ex.Message}");
        _reg.Executed += c => { if (c.Id != "palette") UserPrefs.Current.AddRecentCommand(c.Id); RefreshToolStates(); };
        KeyDown += OnWindowKeyDown;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Write("unhandled", e.Exception);
            ShowToast("Внутренняя ошибка: " + e.Exception.Message);
            e.Handled = true;
        };
        Closing += OnClosing;
        PropsClose.Click += (_, _) => PropsPanel.IsVisible = false;
        SearchButton.Click += (_, _) => OpenPalette();
        TreeNode.VisibilityToggled += _ => { View.MarkDirty(); Session.Document.Root.NotifyChanged(Session.Document.Root); };
    }

    // ================================================================ документ

    public void SetSession(DocumentSession s)
    {
        if (Session != null) { Session.Replaced -= OnReplaced; Session.StateChanged -= OnStateChanged; Session.Document.BuildStarted -= OnBuild; Session.Document.BuildFinished -= OnBuild; }
        Session = s;
        s.Replaced += OnReplaced;
        s.StateChanged += OnStateChanged;
        s.Document.BuildStarted += OnBuild;
        s.Document.BuildFinished += OnBuild;
        s.Document.Root.Changed += _ => Dispatcher.UIThread.Post(RebuildTree, DispatcherPriority.Background);
        View.Document = s.Document;
        View.ActivePart = s.Part;
        View.DarkTheme = _dark;
        RebuildTree();
        OnStateChanged();
        Dispatcher.UIThread.Post(() => View.ZoomToFit(false), DispatcherPriority.Background);
    }

    private void OnReplaced()
    {
        View.Document = null;
        View.Document = Session.Document;
        View.ActivePart = Session.Part;
        View.DarkTheme = _dark;
        RebuildTree();
    }

    private void OnBuild() => Dispatcher.UIThread.Post(() => { UpdateStatus(); RebuildTree(); View.MarkDirty(); });

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
        var r = await Dialogs.Ask(this, "Несохранённые изменения", $"Сохранить изменения в «{Session.Title}»?", "Сохранить", "Не сохранять", "Отмена");
        if (r == "Сохранить") return await SaveAsync(false);
        return r == "Не сохранять";
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
                Title = "Сохранить деталь",
                SuggestedFileName = Session.Title + DocumentSession.PartExtension,
                DefaultExtension = DocumentSession.PartExtension,
                FileTypeChoices = new[] { new FilePickerFileType("Деталь Parametric3D") { Patterns = new[] { "*" + DocumentSession.PartExtension } } },
            });
            path = f?.TryGetLocalPath();
            if (path == null) return false;
        }
        Session.SaveAs(path);
        UserPrefs.Current.AddRecentFile(path);
        ShowToast($"Сохранено: {path}", error: false);
        return true;
    }

    public async Task OpenAsync(string? path = null)
    {
        if (!await ConfirmDiscard()) return;
        if (path == null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Деталь Parametric3D") { Patterns = new[] { "*" + DocumentSession.PartExtension } } },
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
            Title = "Импорт",
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
        if (part == null || part.Bodies.Count == 0) { ShowToast("Экспорт: в детали ещё нет тела."); return; }
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт",
            SuggestedFileName = Session.Title + ".step",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("STEP точный (B-rep)") { Patterns = new[] { "*.step", "*.stp" } },
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

    private Button ToolButton(AppCommand c, double icon = 20)
    {
        var b = new Button { Classes = { "tool" }, Command = c, Content = IconProvider.Content(c.Icon, c.Short ?? c.Label, icon), Tag = c };
        ToolTip.SetTip(b, c.Tooltip);
        return b;
    }

    private ToggleButton ToggleTool(AppCommand c, double icon = 20)
    {
        var b = new ToggleButton { Classes = { "tool" }, Content = IconProvider.Content(c.Icon, c.Short ?? c.Label, icon), Tag = c, IsChecked = c.IsChecked?.Invoke() };
        b.Click += (_, _) => { c.Execute(); b.IsChecked = c.IsChecked?.Invoke(); };
        ToolTip.SetTip(b, c.Tooltip);
        return b;
    }

    private readonly List<ToggleButton> _toggles = new();

    private void BuildToolbars()
    {
        // меню «Файл» — кнопка с выпадающим списком
        var fileBtn = new Button { Classes = { "tool" }, Content = IconProvider.Content(null, "☰", 20) };
        fileBtn.Content = new TextBlock { Text = "☰", FontSize = 16 };
        ToolTip.SetTip(fileBtn, "Файл");
        fileBtn.Click += (_, _) => OpenFileMenu(fileBtn);
        FilePanel.Children.Add(fileBtn);
        foreach (var id in new[] { "file.new_part", "file.open", "file.save" }) FilePanel.Children.Add(ToolButton(_reg[id]));

        foreach (var id in new[] { "edit.undo", "edit.redo", "edit.history" }) EditPanel.Children.Add(ToolButton(_reg[id]));
        RightPanel.Children.Add(ToolButton(_reg["app.theme"]));
        RightPanel.Children.Add(ToolButton(_reg["app.properties"]));

        foreach (var id in new[] { "view.fit", "view.iso", "view.front", "view.top", "view.right" }) ViewTools.Children.Add(ToolButton(_reg[id], 18));
        ViewTools.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 6), Background = Brushes.Gray, Opacity = 0.4 });
        var persp = ToggleTool(_reg["view.perspective"], 18); _toggles.Add(persp); ViewTools.Children.Add(persp);
        var styleBtn = new Button { Classes = { "tool" }, Content = IconProvider.Content("style_shaded_edges_but.svg", "Стиль", 18) };
        ToolTip.SetTip(styleBtn, "Стиль отображения");
        styleBtn.Click += (_, _) => OpenStyleMenu(styleBtn);
        ViewTools.Children.Add(styleBtn);
        var datums = ToggleTool(_reg["view.datums"], 18); _toggles.Add(datums); ViewTools.Children.Add(datums);

        TreeTools.Children.Add(ToolButton(_reg["tree.collapse"], 16));
        TreeTools.Children.Add(ToolButton(_reg["tree.expand"], 16));
    }

    private void RefreshToolStates()
    {
        foreach (var t in _toggles) if (t.Tag is AppCommand c) t.IsChecked = c.IsChecked?.Invoke();
    }

    private MenuItem Item(AppCommand c) => new()
    {
        Header = c.Label,
        Command = c,
        InputGesture = c.Gesture,
        Icon = IconProvider.Get(c.Icon) is { } img ? new Image { Source = img, Width = 16, Height = 16 } : null,
    };

    private void OpenFileMenu(Control anchor)
    {
        var items = new List<object>();
        foreach (var id in new[] { "file.new_part", "file.sample", "file.open" }) items.Add(Item(_reg[id]));
        var recent = new MenuItem { Header = "Последние файлы" };
        var rec = UserPrefs.Current.RecentFiles.Where(File.Exists).ToList();
        foreach (var p in rec)
        {
            var mi = new MenuItem { Header = Path.GetFileName(p) };
            ToolTip.SetTip(mi, p);
            mi.Click += async (_, _) => { try { await OpenAsync(p); } catch (Exception ex) { ShowToast("Открыть: " + ex.Message); } };
            recent.Items.Add(mi);
        }
        recent.IsEnabled = rec.Count > 0;
        items.Add(recent);
        items.Add(new Separator());
        foreach (var id in new[] { "file.save", "file.save_as" }) items.Add(Item(_reg[id]));
        items.Add(new Separator());
        foreach (var id in new[] { "file.import", "file.export" }) items.Add(Item(_reg[id]));
        items.Add(new Separator());
        foreach (var id in new[] { "app.theme", "app.exit" }) items.Add(Item(_reg[id]));
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Open(anchor);
    }

    private void OpenStyleMenu(Control anchor)
    {
        var items = new List<object>();
        foreach (var id in new[] { "style.shaded", "style.shaded_edges", "style.transparent", "style.mesh", "style.volume_mesh" })
        {
            var c = _reg[id];
            var mi = Item(c);
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = c.IsChecked?.Invoke() == true;
            items.Add(mi);
        }
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft }.Open(anchor);
    }

    // ================================================================ тема

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
        // иконки перекрашены — пересоздать содержимое кнопок
        foreach (var panel in new Panel[] { FilePanel, EditPanel, RightPanel, ViewTools, TreeTools })
            foreach (var b in panel.Children.OfType<ContentControl>())
                if (b.Tag is AppCommand c) b.Content = IconProvider.Content(c.Icon, c.Short ?? c.Label, panel == ViewTools ? 18 : panel == TreeTools ? 16 : 20);
        if (Session != null) RebuildTree();
        Cube.InvalidateVisual();
    }

    // ================================================================ вид

    private void WireView()
    {
        Cube.Viewport = View;
        View.CameraChanged += () => Cube.Sync();
        View.HoverChanged += h => UpdateStatus(h);
        View.Selection.Changed += () => { SyncTreeFromSelection(); UpdateStatus(); if (PropsPanel.IsVisible) ShowProperties(); };
        View.ContextRequested += p => OpenViewContextMenu();
        View.GlReady += info => Dispatcher.UIThread.Post(() =>
        {
            StatusGl.Text = ShortGl(info);
            ToolTip.SetTip(StatusGl, info);
        });
        Dispatcher.UIThread.Post(() => { if (View.GlError != null) ShowToast("OpenGL: " + View.GlError); }, DispatcherPriority.Background);
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
        foreach (var id in new[] { "view.hide", "view.isolate", "view.show_all", "view.fit", "app.properties", "edit.delete" }) items.Add(Item(_reg[id]));
        new ContextMenu { ItemsSource = items }.Open(View);
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
        Tree.DoubleTapped += (_, _) => { if (Tree.SelectedItem is TreeNode n) ZoomToItem(n.Item); };
    }

    public void RebuildTree()
    {
        var expanded = new HashSet<uint>(_roots.SelectMany(r => r.SelfAndDescendants()).Where(n => !n.IsExpanded).Select(n => n.Item.Id));
        _roots = TreeNode.Build(Session.Document);
        foreach (var n in _roots.SelectMany(r => r.SelfAndDescendants()))
            if (expanded.Contains(n.Item.Id)) n.IsExpanded = false;
        _syncingTree = true;
        Tree.ItemsSource = _roots;
        _syncingTree = false;
        SyncTreeFromSelection();
    }

    public IEnumerable<TreeNode> AllNodes => _roots.SelectMany(r => r.SelfAndDescendants());

    public void SetTreeExpanded(bool expanded)
    {
        foreach (var n in AllNodes) if (n.Children.Count > 0 && n != _roots.FirstOrDefault()) n.IsExpanded = expanded;
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
        if (r.IsEmpty) View.Selection.Clear();
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

    private void ZoomToItem(ModelItem item)
    {
        if (item is Part) View.ZoomToFit();
    }

    /// <summary>Выбранные элементы модели (из выделения вида).</summary>
    public List<ModelItem> SelectedItems()
    {
        var res = new List<ModelItem>();
        foreach (var r in View.Selection.Items)
        {
            if (r.Target is ModelItem mi) { res.Add(mi); continue; }
            var it = Session.Document.Root.Descendants().FirstOrDefault(x => x.Id == r.Id);
            if (it != null) res.Add(it);
        }
        return res;
    }

    // ================================================================ свойства

    public void ShowProperties()
    {
        PropsPanel.IsVisible = true;
        PropsBody.Children.Clear();
        var sel = View.Selection;
        var part = Session.Part;
        if (sel.IsEmpty && part != null)
        {
            PropsTitle.Text = "ДЕТАЛЬ";
            Row("Имя", part.Name);
            Row("Материал", part.Material?.Name ?? "—");
            Row("Тел", part.Bodies.Count.ToString());
            if (part.Bodies.Count > 0)
            {
                var s = Settings.Current;
                Row("Объём", $"{part.Volume / 1000.0:0.###} см³");
                Row("Площадь", $"{part.Area / 100.0:0.###} см²");
                Row("Масса", $"{part.Mass:0.####} кг");
            }
            Row("Операций", part.Children.OfType<Feature>().Count().ToString());
            return;
        }
        PropsTitle.Text = "ВЫБРАНО";
        Row("Выбрано", sel.Count.ToString());
        foreach (var r in sel.Items.Take(12)) Row(Selection.KindName(r), Selection.Label(r));
    }

    private void Row(string k, string v)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        g.Children.Add(new TextBlock { Text = k, Classes = { "dim" }, TextWrapping = TextWrapping.Wrap });
        var t = new SelectableTextBlock { Text = v, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        PropsBody.Children.Add(g);
    }

    // ================================================================ строка состояния и сообщения

    private string _hoverText = "";

    public void UpdateStatus(string? hover = null)
    {
        if (hover != null) _hoverText = hover;
        var part = Session?.Part;
        StatusMode.Text = part != null ? $"Деталь «{part.Name}»" : "Сборка";
        string sel = View.Selection.IsEmpty ? "" : "Выбрано: " + View.Selection.Describe();
        StatusSel.Text = string.IsNullOrEmpty(_hoverText) ? sel : (sel.Length > 0 ? sel + "   ·   " : "") + "Под курсором: " + _hoverText;
        if (Session == null) return;
        int errors = Session.Document.Root.Descendants().Count(i => i.Error != null);
        if (Session.Document.IsBuilding) { StatusBuild.Text = "Перестроение…"; StatusBuild.Foreground = Res("P3.Warning"); }
        else if (errors > 0) { StatusBuild.Text = $"Требуют внимания: {errors}"; StatusBuild.Foreground = Res("P3.Error"); }
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
        if (names.Count == 0) items.Add(new MenuItem { Header = "Журнал пуст", IsEnabled = false });
        for (int i = names.Count - 1; i >= 0; i--)
        {
            int steps = names.Count - i;
            var mi = new MenuItem { Header = $"{i + 1}. {names[i]}" };
            ToolTip.SetTip(mi, steps == 1 ? "Отменить этот шаг" : $"Отменить {steps} шагов — до состояния перед этим");
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
            if (Toast.IsVisible) { Toast.IsVisible = false; e.Handled = true; return; }
            if (!View.Selection.IsEmpty) { View.Selection.Clear(); e.Handled = true; return; }
            if (PropsPanel.IsVisible) { PropsPanel.IsVisible = false; e.Handled = true; return; }
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
        var icon = IconProvider.Content(c.Icon, c.Label, 18);
        g.Children.Add(icon);
        var t = new TextBlock { Text = c.Label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 1); g.Children.Add(t);
        var grp = new TextBlock { Text = c.Group, Classes = { "dim" }, FontSize = 11, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(grp, 2); g.Children.Add(grp);
        if (c.Gesture != null)
        {
            var k = new TextBlock { Text = AppCommand.Format(c.Gesture), Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(k, 3); g.Children.Add(k);
        }
        ToolTip.SetTip(g, c.Description ?? c.Label);
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
