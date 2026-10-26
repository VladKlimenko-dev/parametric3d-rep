using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Parametric3D.App.Services;
using Parametric3D.App.Views;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>Единицы показа длины в полях операций и измерениях: мм или дюймы (по единицам детали).</summary>
public static class DisplayUnits
{
    public static bool Inch;
    public const double InchMm = 25.4;
    public static string LengthUnit => Inch ? "дюйм" : "мм";
    public static double ToShow(double mm) => Inch ? mm / InchMm : mm;
    public static double FromShow(double v) => Inch ? v * InchMm : v;
}

/// <summary>
/// u13d: КНОПКИ ТАБЛИЦЫ ИКОНОК — ФАЙЛ, ПРАВКА, ПРИЛОЖЕНИЕ: копировать и вставить операции, переименовать, закрыть,
/// последние файлы, новый документ, настройки, справка, все команды, материал, единицы, исполнения.
/// </summary>
public static class DocCommands
{
    public static void Register(CommandRegistry r, MainWindow w)
    {
        KeyGesture K(Key k, KeyModifiers m = KeyModifiers.None) => new(k, m);
        bool HasPart() => w.Session?.Part != null && !w.InSketch;
        const string F = "Файл", E = "Правка", A = "Приложение";
        r.Add(new AppCommand { Id = "file.new_doc", Label = "Новый документ…", Group = F, Icon = "new_doc_but.svg", Synonyms = "new document создать документ", Run = async () => await NewDocument(w) });
        r.Add(new AppCommand { Id = "file.recent", Label = "Последние файлы", Group = F, Icon = "recent_files_but.svg", Synonyms = "recent files последние", Run = () => RecentMenu(w, null) });
        r.Add(new AppCommand { Id = "file.close", Label = "Закрыть документ", Group = F, Icon = "close_doc_but.svg", Synonyms = "close закрыть", Gesture = K(Key.W, KeyModifiers.Control), Run = async () => await CloseDocument(w) });
        r.Add(new AppCommand { Id = "edit.copy", Label = "Копировать", Group = E, Icon = "copy_but.svg", Synonyms = "copy копировать", Gesture = K(Key.C, KeyModifiers.Control),
            CanRun = () => HasPart() && w.SelectedItems().Any(x => x is Feature or Sketch), Run = () => Copy(w) });
        r.Add(new AppCommand { Id = "edit.paste", Label = "Вставить", Group = E, Icon = "paste_but.svg", Synonyms = "paste вставить", Gesture = K(Key.V, KeyModifiers.Control),
            CanRun = () => HasPart() && _clip.Count > 0, Run = () => Paste(w) });
        r.Add(new AppCommand { Id = "edit.rename", Label = "Переименовать", Group = E, Icon = "rename_but.svg", Synonyms = "rename переименовать имя", Gesture = K(Key.F2),
            CanRun = () => w.SelectedItems().Count == 1, Run = async () => await Rename(w) });
        r.Add(new AppCommand { Id = "app.settings", Label = "Настройки", Group = A, Icon = "settings_but.svg", Synonyms = "settings preferences options настройки", Run = async () => await Settings(w) });
        r.Add(new AppCommand { Id = "app.help", Label = "Справка и подсказки", Group = A, Icon = "help_but.svg", Synonyms = "help справка подсказки клавиши", Gesture = K(Key.F1), Run = async () => await Help(w) });
        r.Add(new AppCommand { Id = "app.all_commands", Label = "Все команды", Group = A, Icon = "all_commands_but.svg", Synonyms = "all commands все команды список", Run = async () => await AllCommands(w) });
        r.Add(new AppCommand { Id = "part.material", Label = "Материал детали", Group = A, Icon = "material_but.svg", Synonyms = "material материал плотность", CanRun = HasPart, Run = () => MaterialMenu(w, null) });
        r.Add(new AppCommand { Id = "part.units", Label = "Единицы: мм / дюймы", Group = A, Icon = "units_but.svg", Synonyms = "units inch mm единицы дюймы миллиметры",
            IsChecked = () => DisplayUnits.Inch, CanRun = () => w.Session?.Part != null, Run = () => ToggleUnits(w) });
        r.Add(new AppCommand { Id = "part.configurations", Label = "Исполнения (таблица семейства)", Group = A, Icon = "configurations_but.svg", Synonyms = "configurations family table исполнения конфигурации",
            CanRun = HasPart, Run = async () => await ConfigurationsDialog(w) });
    }

    // ---------------------------------------------------------------- копировать и вставить

    static readonly List<(Part Part, int Index)> _clip = new();

    public static int Copy(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return 0;
        _clip.Clear();
        var sel = w.SelectedItems();
        foreach (var f in sel.OfType<Feature>()) if (part.Features.IndexOf(f) is int i && i >= 0) _clip.Add((part, i));
        // эскиз без операции — копируется сам
        foreach (var sk in sel.OfType<Sketch>()) if (part.Children.IndexOf(sk) is int j && j >= 0) _clip.Add((part, -1 - j));
        w.ShowToast(L("Скопировано: {0}", _clip.Count), error: false);
        return _clip.Count;
    }

    /// <summary>Вставка: копии операций (и их эскизов) в конец дерева — через сохранение и загрузку детали.</summary>
    public static List<Feature> Paste(MainWindow w)
    {
        var part = w.Session.Part; var made = new List<Feature>();
        if (part == null || _clip.Count == 0) return made;
        var src = _clip[0].Part;
        var json = DocumentFile.Save(src, w.Session.Document.ScopeFor(src));
        var (tmp, _) = DocumentFile.Load(json);
        w.Session.Edit(L("Вставить"), () =>
        {
            var movedSketches = new HashSet<Sketch>();
            void TakeSketch(Sketch s)
            {
                if (!movedSketches.Add(s)) return;
                tmp.Remove(s); s.Name = UniqueName(part, s.Name); part.Add(s);
            }
            foreach (var (_, idx) in _clip.OrderBy(c => c.Index))
            {
                if (idx < 0)
                {
                    // эскиз — по порядку среди детей исходной детали: тот же номер среди эскизов
                    var srcSk = src.Children[-1 - idx] as Sketch; if (srcSk == null) continue;
                    int k = src.Children.OfType<Sketch>().ToList().IndexOf(srcSk);
                    var t = tmp.Children.OfType<Sketch>().ElementAtOrDefault(k);
                    if (t != null) TakeSketch(t);
                    continue;
                }
                if (idx >= tmp.Features.Count) continue;
                var f = tmp.Features[idx];
                foreach (var id in f.ConsumedSketchIds().ToList())
                    if (tmp.Children.OfType<Sketch>().FirstOrDefault(s => s.Id == id) is { } s) TakeSketch(s);
                made.Add(f);
            }
            // k115 (п. 7): вставленная группа — новая группа (прежде копия вливалась в исходную с тем же именем)
            var groupNames = part.Features.Select(x => x.Group).Where(g => !string.IsNullOrEmpty(g)).ToHashSet()!;
            var renamed = new Dictionary<string, string>();
            foreach (var f in made)
            {
                tmp.RemoveFeature(f);
                f.Key = Feature.NewKey();
                f.Name = UniqueName(part, f.Name);
                if (!string.IsNullOrEmpty(f.Group) && !PasteGroupOff)
                {
                    if (!renamed.TryGetValue(f.Group, out var ng))
                    {
                        ng = f.Group;
                        for (int i = 2; groupNames.Contains(ng); i++) ng = $"{f.Group} ({i})";
                        groupNames.Add(ng); renamed[f.Group] = ng;
                    }
                    f.Group = ng;
                }
                part.AddFeature(f);
            }
        });
        w.Session.Document.RebuildDirty();
        w.ViewPort.MarkDirty(); w.RebuildTree();
        w.ShowToast(L("Вставлено операций: {0}", made.Count), error: false);
        return made;
    }

    static readonly bool PasteGroupOff = Environment.GetEnvironmentVariable("P3D_PASTEGROUP") == "0";

    static string UniqueName(Part part, string name)
    {
        var names = part.Children.Select(c => c.Name).ToHashSet();
        if (!names.Contains(name)) return name;
        for (int i = 2; ; i++) { var n = $"{name} ({i})"; if (!names.Contains(n)) return n; }
    }

    // ---------------------------------------------------------------- переименовать, закрыть, последние, новый

    public static async Task Rename(MainWindow w)
    {
        var it = w.SelectedItems().FirstOrDefault(); if (it == null) return;
        var name = await Dialogs.Prompt(w, L("Переименовать"), L("Новое имя:"), it.Name);
        if (string.IsNullOrWhiteSpace(name) || name == it.Name) return;
        w.Session.Edit(L("Переименовать"), () => { it.Name = name.Trim(); });
        w.RebuildTree();
    }

    public static async Task CloseDocument(MainWindow w)
    {
        if (!await w.ConfirmDiscard()) return;
        w.SetSession(DocumentSession.NewPart());
    }

    public static void RecentMenu(MainWindow w, Control? anchor)
    {
        var list = UserPrefs.Current.RecentFiles.ToList();
        var sp = new StackPanel { Spacing = 1, Margin = new Thickness(4) };
        if (list.Count == 0) sp.Children.Add(new TextBlock { Text = L("Список пуст"), Margin = new Thickness(6) });
        var fl = new Flyout { Content = sp, Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var p in list)
        {
            var b = new Button { Content = System.IO.Path.GetFileName(p), Classes = { "tool" }, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 260 };
            ToolTip.SetTip(b, p);
            b.IsEnabled = System.IO.File.Exists(p);
            b.Click += async (_, _) => { fl.Hide(); await w.OpenAsync(p); };
            sp.Children.Add(b);
        }
        if (anchor != null) fl.ShowAt(anchor); else fl.ShowAt(w.ViewPort, showAtPointer: true);
    }

    public static async Task NewDocument(MainWindow w)
    {
        string part = L("Деталь"), asm = L("Сборка"), ff = L("FreeForm-тело");
        var r = await Dialogs.Ask(w, L("Новый документ"), L("Что создать?"), part, asm, ff, L("Отмена"));
        if (r == part) { if (await w.ConfirmDiscard()) w.SetSession(DocumentSession.NewPart()); }
        else if (r == asm) CommandRegistry.Instance.Find("file.new_assembly")?.Run?.Invoke();
        else if (r == ff) CommandRegistry.Instance.Find("file.new_freeform")?.Run?.Invoke();
    }

    // ---------------------------------------------------------------- настройки, справка, все команды

    public static async Task Settings(MainWindow w)
    {
        var prefs = UserPrefs.Current;
        var win = new Window { Title = L("Настройки"), SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, CanResize = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        var theme = new ComboBox { ItemsSource = new[] { L("Тёмная"), L("Светлая") }, SelectedIndex = w.IsDark ? 0 : 1, MinWidth = 200 };
        var lang = new ComboBox { ItemsSource = new[] { "Русский", "English" }, SelectedIndex = prefs.Language == "en" ? 1 : 0, MinWidth = 200 };
        var prev = new ComboBox { ItemsSource = new[] { L("Полный (деталь считается)"), L("Контур") }, SelectedIndex = (prefs.PreviewMode ?? "full") == "contour" ? 1 : 0, MinWidth = 200 };
        var gl = new ComboBox { ItemsSource = new[] { "auto", "angle", "wgl", "software" }, SelectedItem = prefs.GlMode ?? "auto", MinWidth = 200 };
        var units = new ComboBox { ItemsSource = new[] { L("Миллиметры"), L("Дюймы") }, SelectedIndex = DisplayUnits.Inch ? 1 : 0, MinWidth = 200 };
        Control Row(string label, Control c) => new DockPanel { Children = { new TextBlock { Text = L(label), Width = 200, VerticalAlignment = VerticalAlignment.Center }, c } };
        // заголовок «Общие» и языки — иконками пользователя (нет файла — только текст)
        static Control WithIcon(string? icon, string text, double size = 16)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (Icons.IconProvider.Get(icon) is { } img) sp.Children.Add(new Image { Source = img, Width = size, Height = size, Stretch = Avalonia.Media.Stretch.Uniform });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
        lang.ItemsSource = new[] { WithIcon("language_ru_but.svg", "Русский"), WithIcon("language_en_but.svg", "English") };
        // вкладка «Вид»: размеры текста и кнопок, цвета (UserPrefs.Look)
        var look = prefs.Look.Clone();
        NumericUpDown Num(double v, double min, double max, double step) => new NumericUpDown { Value = (decimal)v, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, MinWidth = 200, FormatString = step < 1 ? "0.##" : "0" };
        var fUi = Num(look.UiFontSize, 9, 22, 1); var fStrip = Num(look.StripButtonPx, 22, 64, 1); var fStripIcon = Num(look.StripIconPx, 12, 48, 1);
        var fPanelIcon = Num(look.PanelIconPx, 12, 40, 1); var fDim = Num(look.DimensionTextPx, 6, 40, 1); var fDimLine = Num(look.DimensionTextLine, 0.3, 3, 0.05);
        var fCon = Num(look.ConstraintIconPx, 8, 48, 1);
        Control ColorBox(string value, out TextBox tb)
        {
            var t = new TextBox { Text = value, Watermark = L("по теме (#RRGGBB)"), MinWidth = 160 };
            var sw = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Avalonia.Media.Brushes.Gray };
            void Upd() => sw.Background = UiLook.ParseColor(t.Text) is { } c ? new Avalonia.Media.SolidColorBrush(c) : Avalonia.Media.Brushes.Transparent;
            t.TextChanged += (_, _) => Upd(); Upd();
            tb = t;
            // k103: образец — кнопка выбора цвета (палитра; «по теме» — очистить)
            var pick = new Button { Content = sw, Padding = new Thickness(2), Margin = new Thickness(6, 0, 0, 0), Classes = { "tool" } };
            ToolTip.SetTip(pick, L("Выбрать цвет"));
            pick.Flyout = ColorPalette(c => t.Text = c);
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { t, pick } };
        }
        var cHatchC = ColorBox(look.SectionHatchColor, out var tHatchC); var cFillC = ColorBox(look.SectionFillColor, out var tFillC);
        var chHatch = new CheckBox { IsChecked = look.SectionHatch, Content = L("Штриховать сечение деталей и сборок") };
        var cSkD = ColorBox(look.SketchColorDark, out var tSkD); var cSkL = ColorBox(look.SketchColorLight, out var tSkL);
        var cBgTD = ColorBox(look.BackgroundTopDark, out var tBgTD); var cBgBD = ColorBox(look.BackgroundBottomDark, out var tBgBD);
        var cBgTL = ColorBox(look.BackgroundTopLight, out var tBgTL); var cBgBL = ColorBox(look.BackgroundBottomLight, out var tBgBL);
        var resetLook = new Button { Content = L("Сбросить вид"), HorizontalAlignment = HorizontalAlignment.Left };
        resetLook.Click += (_, _) =>
        {
            var d = new LookPrefs();
            fUi.Value = (decimal)d.UiFontSize; fStrip.Value = (decimal)d.StripButtonPx; fStripIcon.Value = (decimal)d.StripIconPx; fPanelIcon.Value = (decimal)d.PanelIconPx;
            fDim.Value = (decimal)d.DimensionTextPx; fDimLine.Value = (decimal)d.DimensionTextLine; fCon.Value = (decimal)d.ConstraintIconPx;
            foreach (var t in new[] { tSkD, tSkL, tBgTD, tBgBD, tBgTL, tBgBL, tHatchC, tFillC }) t.Text = "";
            chHatch.IsChecked = d.SectionHatch;
        };
        var ok = new Button { Content = L("ОК"), Classes = { "accent" }, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) =>
        {
            double D(NumericUpDown n, double dflt) => n.Value is { } v ? (double)v : dflt;
            look.UiFontSize = D(fUi, 13); look.StripButtonPx = D(fStrip, 34); look.StripIconPx = D(fStripIcon, 20); look.PanelIconPx = D(fPanelIcon, 20);
            look.DimensionTextPx = D(fDim, 12); look.DimensionTextLine = D(fDimLine, 0.75); look.ConstraintIconPx = D(fCon, 16);
            look.SketchColorDark = tSkD.Text?.Trim() ?? ""; look.SketchColorLight = tSkL.Text?.Trim() ?? "";
            look.BackgroundTopDark = tBgTD.Text?.Trim() ?? ""; look.BackgroundBottomDark = tBgBD.Text?.Trim() ?? "";
            look.BackgroundTopLight = tBgTL.Text?.Trim() ?? ""; look.BackgroundBottomLight = tBgBL.Text?.Trim() ?? "";
            look.SectionHatch = chHatch.IsChecked == true; look.SectionHatchColor = tHatchC.Text?.Trim() ?? ""; look.SectionFillColor = tFillC.Text?.Trim() ?? "";
            prefs.Look = look;
            if ((theme.SelectedIndex == 0) != w.IsDark) w.ApplyTheme(theme.SelectedIndex == 0);
            if ((lang.SelectedIndex == 1) != (prefs.Language == "en")) w.ToggleLanguage();
            prefs.PreviewMode = prev.SelectedIndex == 1 ? "contour" : "full";
            prefs.GlMode = gl.SelectedItem as string ?? "auto";
            if ((units.SelectedIndex == 1) != DisplayUnits.Inch) ToggleUnits(w);
            prefs.Save();
            w.ApplyLook();
            win.Close();
        };
        var general = new StackPanel
        {
            Margin = new Thickness(6, 12, 6, 6), Spacing = 10,
            Children = { Row("Тема", theme), Row("Язык", lang), Row("Предпросмотр операций", prev), Row("Графика (со следующего запуска)", gl), Row("Единицы длины", units) },
        };
        var visual = new StackPanel
        {
            Margin = new Thickness(6, 12, 6, 6), Spacing = 8,
            Children =
            {
                Row("Размер текста интерфейса", fUi), Row("Кнопка полосы у края вида, пикс.", fStrip), Row("Иконка на кнопке полосы, пикс.", fStripIcon),
                Row("Иконка на кнопках панелей, пикс.", fPanelIcon), Row("Высота цифр размеров, пикс.", fDim), Row("Толщина линии цифр размеров", fDimLine),
                Row("Значки связей эскиза, пикс.", fCon), Row("Линии эскиза (тёмная тема)", cSkD), Row("Линии эскиза (светлая тема)", cSkL),
                Row("Фон вида сверху (тёмная)", cBgTD), Row("Фон вида снизу (тёмная)", cBgBD), Row("Фон вида сверху (светлая)", cBgTL), Row("Фон вида снизу (светлая)", cBgBL),
                Row("Сечение", chHatch), Row("Цвет штриховки сечения", cHatchC), Row("Цвет заливки сечения", cFillC),
                resetLook,
            },
        };
        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = WithIcon("settings_general_ico.svg", L("Общие"), 18), Content = general },
                new TabItem { Header = WithIcon("theme_but.svg", L("Вид"), 18), Content = visual },
            },
        };
        win.Content = new StackPanel { Margin = new Thickness(18), Spacing = 10, Children = { tabs, ok } };
        await win.ShowDialog(w);
    }

    /// <summary>k103: палитра цветов (5 оттенков 10 тонов, серые) и «по теме / по материалу» — пустое значение.</summary>
    public static Flyout ColorPalette(Action<string> set)
    {
        var fl = new Flyout();
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Width = 10 * 24 };
        var hues = new[] { 0, 30, 50, 90, 140, 175, 200, 225, 265, 310 };
        var levels = new[] { (0.35, 0.95), (0.55, 0.90), (0.75, 0.80), (0.90, 0.60), (0.95, 0.40) };   // (насыщенность, яркость)
        string Hex(Avalonia.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        void Add(Avalonia.Media.Color c)
        {
            var b = new Button { Width = 22, Height = 22, Margin = new Thickness(1), Padding = new Thickness(0), Background = new Avalonia.Media.SolidColorBrush(c), BorderThickness = new Thickness(1), BorderBrush = Avalonia.Media.Brushes.Gray };
            ToolTip.SetTip(b, Hex(c));
            b.Click += (_, _) => { set(Hex(c)); fl.Hide(); };
            wrap.Children.Add(b);
        }
        foreach (var (sat, val) in levels)
            foreach (var h in hues) Add(Avalonia.Media.HsvColor.ToRgb(h, sat, val));
        for (int i = 0; i < 10; i++) { byte g = (byte)(i * 255 / 9); Add(Avalonia.Media.Color.FromRgb(g, g, g)); }
        var reset = new Button { Content = L("По умолчанию"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(1, 6, 1, 0) };
        reset.Click += (_, _) => { set(""); fl.Hide(); };
        fl.Content = new StackPanel { Children = { wrap, reset } };
        return fl;
    }

    public static string HelpText() => string.Join("\n", new[]
    {
        L("Мышь: средняя кнопка — вращение, Shift + средняя или правая — сдвиг, колесо — масштаб; протяжка по пустому месту — рамка выбора."),
        L("Выбор: 1 — вершины, 2 — рёбра, 3 — грани, 4 — тела, 5 — операции (повторно — всё); Ctrl — добавить или убрать, Shift — добавить, Alt — одно ребро без цепочки."),
        L("Поиск команды — кнопка поиска или клавиша F; ПКМ — меню выбранного."),
        L("Эскиз: S — новый, L — отрезок, Ctrl+Enter — закончить; размеры — D, обрезка — T."),
        L("Операции: E — выдавливание, R — скругление, C — фаска; Enter — применить, Esc — отменить; Ctrl+B — перестроить."),
        L("Виды: Ctrl+1…6 — спереди, сзади, слева, справа, сверху, снизу; H — изометрия; N — по нормали; Z — вписать выбранное; P — проекция."),
        L("Анализ: M — измерить; в панели вида — «Анализ ▾»: площадь, периметр, масса, зебра, кривизна, проверка тела."),
    });

    public static async Task Help(MainWindow w)
    {
        var keys = CommandRegistry.Instance.All.Where(c => c.Gesture != null).OrderBy(c => c.Group).Select(c => $"{c.Gesture}\t{c.Text}");
        await Dialogs.Info(w, L("Справка"), HelpText() + "\n\n" + L("Клавиши:") + "\n" + string.Join("\n", keys));
    }

    /// <summary>Все команды по группам — сворачиваемые разделы; щелчок выполняет команду.</summary>
    public static async Task AllCommands(MainWindow w)
    {
        var win = new Window { Title = L("Все команды"), Width = 460, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        var sp = new StackPanel { Spacing = 4, Margin = new Thickness(10) };
        foreach (var g in CommandRegistry.Instance.All.GroupBy(c => c.Group))
        {
            var inner = new StackPanel { Spacing = 1 };
            foreach (var c in g)
            {
                var b = new Button { Content = c.Text + (c.Gesture != null ? $"   ({c.Gesture})" : ""), Classes = { "tool" }, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch };
                b.IsEnabled = c.CanRun?.Invoke() ?? true;
                b.Click += (_, _) => { win.Close(); c.Run?.Invoke(); };
                inner.Children.Add(b);
            }
            sp.Children.Add(new Expander { Header = $"{L(g.Key)} ({g.Count()})", Content = inner, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        win.Content = new ScrollViewer { Content = sp };
        await win.ShowDialog(w);
    }

    // ---------------------------------------------------------------- вставить тело

    /// <summary>Текст STEP тел файла: STEP/IGES — чтением, деталь Parametric3D — перестроением и записью STEP.</summary>
    public static string StepOf(string path, List<string> warnings)
    {
        if (path.EndsWith(DocumentSession.PartExtension, StringComparison.OrdinalIgnoreCase))
        {
            var s = DocumentSession.Open(path);
            s.Document.RebuildDirty();
            var part = s.Part ?? throw new InvalidDataException(L("В файле нет детали"));
            if (part.FreeFormDoc) { part.MergeFreeForm = true; part.Rebuild(s.Document.ScopeFor(part)); }
            if (part.Bodies.Count == 0) part.Rebuild(s.Document.ScopeFor(part));
            return Exchange.Write(part, Exchange.Format.Step, warnings);
        }
        var text = System.IO.File.ReadAllText(path);
        if (Exchange.Guess(text) == Exchange.Format.Step) return text;
        var p2 = Exchange.Read(text, out _, warnings) ?? throw new InvalidDataException(string.Join("; ", warnings));
        return Exchange.Write(p2, Exchange.Format.Step, warnings);
    }

    public static async Task InsertBody(MainWindow w)
    {
        var files = await w.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = L("Вставить тело"), AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType(L("Тела: деталь, STEP, IGES")) { Patterns = new[] { "*" + DocumentSession.PartExtension, "*.step", "*.stp", "*.iges", "*.igs" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var warnings = new List<string>();
            var step = StepOf(path, warnings);
            w.StartOperation(new Operations.InsertBodyOp { Step = step, Source = System.IO.Path.GetFileName(path) });
        }
        catch (Exception ex) { w.ShowToast(L("Вставить тело: {0}", ex.Message)); }
    }

    // ---------------------------------------------------------------- материал, единицы, исполнения

    public static void SetMaterial(MainWindow w, Material m)
    {
        var part = w.Session.Part; if (part == null) return;
        w.Session.Edit(L("Материал: {0}", m.Name), () => { part.Material = m; part.NotifyChanged(part); });
        w.ViewPort.MarkDirty(); w.ShowProperties();
    }

    public static void MaterialMenu(MainWindow w, Control? anchor)
    {
        var sp = new StackPanel { Spacing = 1, Margin = new Thickness(4) };
        var fl = new Flyout { Content = new ScrollViewer { Content = sp, MaxHeight = 520 }, Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var m in Material.Library())
        {
            var b = new Button { Content = $"{m.Name} — {m.Density:0} кг/м³", Classes = { "tool" }, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 260 };
            b.Click += (_, _) => { fl.Hide(); SetMaterial(w, m); };
            sp.Children.Add(b);
        }
        if (anchor != null) fl.ShowAt(anchor); else fl.ShowAt(w.ViewPort, showAtPointer: true);
    }

    public static void ToggleUnits(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        DisplayUnits.Inch = !DisplayUnits.Inch;
        part.Units = DisplayUnits.Inch ? Core.UnitSystem.Imperial : Core.UnitSystem.Metric;
        w.Session.MarkModified();
        w.ShowToast(L("Единицы длины: {0}", L(DisplayUnits.LengthUnit)), error: false);
        if (w.CurrentOperation != null) w.ShowProperties();
    }

    public static async Task ConfigurationsDialog(MainWindow w)
    {
        var part = w.Session.Part!; var table = w.Session.Document.ScopeFor(part);
        var win = new Window { Title = L("Исполнения"), SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        var lb = new ListBox { MinWidth = 380, MaxHeight = 360 };
        void Fill()
        {
            lb.Items.Clear();
            lb.Items.Add(new ListBoxItem { Content = (part.ActiveConfiguration == "" ? "● " : "  ") + L("Основная модель"), Tag = "" });
            foreach (var c in part.Configurations) lb.Items.Add(new ListBoxItem { Content = (part.ActiveConfiguration == c.Name ? "● " : "  ") + c, Tag = c.Name });
        }
        Fill();
        string? Sel() => (lb.SelectedItem as ListBoxItem)?.Tag as string;
        var add = new Button { Content = L("Новое из текущего…") };
        add.Click += async (_, _) =>
        {
            var name = await Dialogs.Prompt(win, L("Новое исполнение"), L("Имя исполнения:"), L("Исполнение {0}", part.Configurations.Count + 1));
            if (string.IsNullOrWhiteSpace(name) || part.Configurations.Any(c => c.Name == name)) return;
            w.Session.Edit(L("Новое исполнение"), () => part.Configurations.Add(Configurations.Capture(part, table, name.Trim())));
            Fill();
        };
        var apply = new Button { Content = L("Применить"), Classes = { "accent" } };
        apply.Click += (_, _) =>
        {
            if (Sel() is not { } n) return;
            w.Session.Edit(L("Исполнение: {0}", n == "" ? L("основная") : n), () => Configurations.Apply(part, table, n));
            w.Session.Document.RebuildDirty(); w.ViewPort.MarkDirty(); w.RebuildTree(); Fill();
        };
        var del = new Button { Content = L("Удалить") };
        del.Click += (_, _) =>
        {
            if (Sel() is not { Length: > 0 } n || n == part.ActiveConfiguration) return;
            w.Session.Edit(L("Удалить исполнение"), () => part.Configurations.RemoveAll(c => c.Name == n));
            Fill();
        };
        win.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children =
            {
                new TextBlock { Text = L("Исполнение — значения переменных и погашенные операции; основная модель запоминается при переходе."), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 },
                lb,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, apply, del } },
            },
        };
        await win.ShowDialog(w);
    }
}
