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
    /// <summary>k120: единица длины показа — из системы единиц детали (мм, см, м, дюйм, фут…).</summary>
    public static string Length
    {
        get => _length;
        set
        {
            _length = value;
            // k123: подписи размеров эскиза — в той же единице (Settings.FormatLength)
            if (!SketchUnitsOff) { Core.Settings.DisplayUnitMm = Factor; Core.Settings.DisplayUnitSuffix = value; }
        }
    }
    static string _length = "mm";
    public static readonly bool SketchUnitsOff = Environment.GetEnvironmentVariable("P3D_SKUNITS") == "0";
    public static bool Inch { get => Length == "in"; set => Length = value ? "in" : "mm"; }
    public const double InchMm = 25.4;
    public static double Factor => Core.Units.TryParse(Length, out var u, out _) && u!.Dim == Core.Dimension.Length ? u.Factor : 1;
    public static string LengthUnit => Symbol(Length);
    public static double ToShow(double mm) => mm / Factor;
    public static double FromShow(double v) => v * Factor;

    /// <summary>Обозначение единицы на языке интерфейса: мм, см, м, дюйм, фут, кг, г, т, фунт, с, мин, ч; степени — ², ³.</summary>
    public static string Symbol(string sym)
    {
        string Tok(string t) => t switch
        {
            "mm" => "мм", "cm" => "см", "m" => "м", "km" => "км", "in" => "дюйм", "ft" => "фут", "yd" => "ярд",
            "kg" => "кг", "g" => "г", "t" => "т", "lb" => "фунт", "s" => "с", "min" => "мин", "h" => "ч", "deg" => "°", "rad" => "рад",
            _ => t,
        };
        var parts = sym.Split('·').Select(p =>
        {
            var b = p.Split('^');
            string u = Loc.En ? b[0] : Tok(b[0]);
            return b.Length == 2 ? u + (b[1] == "2" ? "²" : b[1] == "3" ? "³" : b[1] == "-1" ? "⁻¹" : b[1] == "-3" ? "⁻³" : "^" + b[1]) : u;
        });
        return string.Join("·", parts);
    }

    /// <summary>Величина (во внутренних единицах) в системе sys: число и обозначение.</summary>
    public static string Format(double internalValue, Core.Dimension dim, Core.UnitSystem sys, string fmt = "0.####")
    {
        var u = sys.UnitFor(dim);
        return (internalValue / u.Factor).ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) + " " + Symbol(u.Symbol);
    }

    public static void From(Core.UnitSystem? sys) => Length = sys?.Length ?? "mm";

    /// <summary>
    /// k128 (замечание 28.10, 18:18): ВВОД ДЛИНЫ ИЛИ УГЛА В ОКНАХ — число без единицы — в единице детали, с единицей — в
    /// указанной («4.5in», «10 mm», «1.5*25.4mm»); выражения из чисел и единиц. Значение — во внутренних единицах (мм, °).
    /// </summary>
    /// <summary>P3D_INPUTUNITS=0 — число в окне ввода длины — в мм (прежде).</summary>
    public static readonly bool InputUnitsOff = Environment.GetEnvironmentVariable("P3D_INPUTUNITS") == "0";
    public static bool TryLength(string? text, out double mm) => TryValue(text, Core.Dimension.Length, out mm);
    public static bool TryAngle(string? text, out double deg) => TryValue(text, Core.Dimension.Angle, out deg);
    public static bool TryValue(string? text, Core.Dimension want, out double value)
    {
        value = 0;
        string t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        if (double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x))
        { value = want == Core.Dimension.Length && !InputUnitsOff ? FromShow(x) : x; return true; }
        if (!Model.Params.ExprParser.TryParse(t.Replace(',', '.'), out var e, out _) || e == null) return false;
        try
        {
            var sys = new Core.UnitSystem { Length = Length };
            var q = Model.Params.ExprUnits.Evaluate(e, _ => null, sys);
            if (q.Bare && q.Dim.IsNone) value = want.IsNone ? q.Value : sys.UnitFor(want).ToInternal(q.Value);
            else if (q.Dim == want) value = q.Value;
            else return false;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch { return false; }
    }
    /// <summary>Длина (мм) числом в единице детали — для поля ввода.</summary>
    public static string ShowText(double mm) => ToShow(mm).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
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

    public static readonly bool LangFixOff = Environment.GetEnvironmentVariable("P3D_LANGFIX") == "0";
    /// <summary>Разбор (проверки): последнее окно настроек.</summary>
    public static Window? LastSettingsWindow;

    public static async Task Settings(MainWindow w)
    {
        var prefs = UserPrefs.Current;
        var win = new Window { Title = L("Настройки"), SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, CanResize = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        var theme = new ComboBox { ItemsSource = new[] { L("Тёмная"), L("Светлая") }, SelectedIndex = w.IsDark ? 0 : 1, MinWidth = 200 };
        var lang = new ComboBox { ItemsSource = new[] { "Русский", "English" }, SelectedIndex = prefs.Language == "en" ? 1 : 0, MinWidth = 200, Name = "set_lang" };
        var prev = new ComboBox { ItemsSource = new[] { L("Полный (деталь считается)"), L("Контур") }, SelectedIndex = (prefs.PreviewMode ?? "full") == "contour" ? 1 : 0, MinWidth = 200 };
        var gl = new ComboBox { ItemsSource = new[] { "auto", "angle", "wgl", "software" }, SelectedItem = prefs.GlMode ?? "auto", MinWidth = 200 };
        Control Row(string label, Control c) => new DockPanel { Children = { new TextBlock { Text = L(label), Width = 200, VerticalAlignment = VerticalAlignment.Center }, c } };
        // заголовок «Общие» и языки — иконками пользователя (нет файла — только текст)
        static Control WithIcon(string? icon, string text, double size = 16)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (Icons.IconProvider.Get(icon) is { } img) sp.Children.Add(new Image { Source = img, Width = size, Height = size, Stretch = Avalonia.Media.Stretch.Uniform });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
        // k120 (п. 9): список языков — иконками; выбор ставится ПОСЛЕ замены списка (прежде замена сбрасывала выбор в −1, и «ОК»
        // возвращало язык обратно — английский «не сохранялся»)
        lang.ItemsSource = new[] { WithIcon("language_ru_but.svg", "Русский"), WithIcon("language_en_but.svg", "English") };
        if (!LangFixOff) lang.SelectedIndex = prefs.Language == "en" ? 1 : 0;
        // вкладка «Вид»: размеры текста и кнопок, цвета (UserPrefs.Look)
        var look = prefs.Look.Clone();
        // k120 (п. 11): ползунок со значением слева (вместо поля с кнопками)
        ValueSlider Num(double v, double min, double max, double step) => new ValueSlider(v, min, max, step);
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
            fUi.Value = d.UiFontSize; fStrip.Value = d.StripButtonPx; fStripIcon.Value = d.StripIconPx; fPanelIcon.Value = d.PanelIconPx;
            fDim.Value = d.DimensionTextPx; fDimLine.Value = d.DimensionTextLine; fCon.Value = d.ConstraintIconPx;
            foreach (var t in new[] { tSkD, tSkL, tBgTD, tBgBD, tBgTL, tBgBL, tHatchC, tFillC }) t.Text = "";
            chHatch.IsChecked = d.SectionHatch;
        };
        var ok = new Button { Content = L("ОК"), Classes = { "accent" }, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, Name = "set_ok" };
        ok.Click += (_, _) =>
        {
            double D(ValueSlider n, double dflt) => double.IsFinite(n.Value) ? n.Value : dflt;
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
            prefs.Save();
            w.ApplyLook();
            win.Close();
        };
        var general = new StackPanel
        {
            Margin = new Thickness(6, 12, 6, 6), Spacing = 10,
            Children = { Row("Тема", theme), Row("Язык", lang), Row("Предпросмотр операций", prev), Row("Графика (со следующего запуска)", gl) },
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
        LastSettingsWindow = win;
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
        // k123: через смену системы единиц детали (с вопросом о преобразовании размеров)
        var to = part.Units.Length == "in" ? Core.UnitSystem.Metric : Core.UnitSystem.Imperial;
        _ = Run();
        async Task Run()
        {
            await w.SetPartUnitsAsync(part, to);
            w.ShowToast(L("Единицы длины: {0}", L(DisplayUnits.LengthUnit)), error: false);
        }
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

/// <summary>k120 (замечание 15:06, п. 11): ползунок настройки со значением слева.</summary>
public sealed class ValueSlider : DockPanel
{
    public readonly Slider Slider;
    readonly TextBlock _text;
    readonly double _step;
    public ValueSlider(double v, double min, double max, double step)
    {
        _step = step;
        _text = new TextBlock { Width = 48, VerticalAlignment = VerticalAlignment.Center, TextAlignment = Avalonia.Media.TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        Slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true, Value = v, MinWidth = 200 };
        Slider.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Show(); };
        DockPanel.SetDock(_text, Dock.Left);
        Children.Add(_text); Children.Add(Slider);
        Show();
    }
    void Show() => _text.Text = Slider.Value.ToString(_step < 1 ? "0.##" : "0", System.Globalization.CultureInfo.InvariantCulture);
    public double Value { get => Slider.Value; set => Slider.Value = value; }
    public string ShownText => _text.Text ?? "";
}

