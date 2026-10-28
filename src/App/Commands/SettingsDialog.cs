using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.Services;
using Parametric3D.App.Views;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// k132 (замечание 28.10, 23:42, пп. 7, 11, 12): НАСТРОЙКИ ПРИЛОЖЕНИЯ по образцу QymCAD — слева поиск и список разделов
/// (Общие, Внешний вид, Панели инструментов, Дерево модели, Панель свойств, Управление операциями, Эскиз, Вид модели),
/// справа строки выбранного раздела и «Сбросить раздел»; поиск показывает подходящие строки всех разделов. Цвета, зависящие
/// от темы, правятся для темы, выбранной первым списком раздела («Цвета для темы»), — без двойных строк «тёмная/светлая».
/// </summary>
public static class SettingsDialog
{
    /// <summary>k133 (замечание 29.10, 08:55, п. 2): все подписи окна — через перевод (P3D_SETTINGSLOC=0 — без перевода).</summary>
    public static readonly bool LocOff = Environment.GetEnvironmentVariable("P3D_SETTINGSLOC") == "0";
    static string L(string ru) => LocOff ? ru : Parametric3D.App.Loc.L(ru);
    public static readonly bool EditThemeOff = Environment.GetEnvironmentVariable("P3D_EDITTHEME") == "0";
    public static readonly bool SectionsOff = Environment.GetEnvironmentVariable("P3D_SETTINGSSECTIONS") == "0";

    /// <summary>Строка раздела: подпись, элемент, загрузка из настроек и запись в них (тема — у цветов по теме).</summary>
    sealed class Bind
    {
        public string Section = "", Label = "";
        public Control Row = null!;
        public bool Themed;
        public Action<LookPrefs, bool>? Load, Save;
    }

    /// <summary>Разбор (проверки): разделы и строки последнего окна, выбор раздела, поиск, тема правки цветов.</summary>
    public sealed class Probe
    {
        public ListBox Sections = null!;
        public TextBox Search = null!;
        public readonly List<(string Section, string Label, Control Row)> Rows = new();
        public readonly List<ComboBox> EditTheme = new();
        public Func<string, Control?> Find = _ => null;
        public Action<string> Reset = _ => { };
        public LookPrefs Look = null!;
    }
    public static Probe? Last;

    public static async Task Show(MainWindow w)
    {
        var prefs = UserPrefs.Current;
        var look = prefs.Look.Clone();
        var probe = new Probe { Look = look };
        var win = new Window
        {
            Title = L("Настройки"), Width = 900, Height = 640, MinWidth = 700, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, CanResize = true,
        };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        var binds = new List<Bind>();
        bool editDark = w.IsDark;
        var editCombos = new List<ComboBox>();
        string section = "";

        static Control WithIcon(string? icon, string text, double size = 16)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (Icons.IconProvider.Get(icon) is { } img) sp.Children.Add(new Image { Source = img, Width = size, Height = size, Stretch = Stretch.Uniform });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
        Control MakeRow(string label, Control c)
        {
            var tb = new TextBlock { Text = L(label), Width = 250, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(tb, Dock.Left);
            return new DockPanel { Children = { tb, c }, Margin = new Thickness(0, 2) };
        }
        Bind Add(string label, Control c, Action<LookPrefs, bool>? load = null, Action<LookPrefs, bool>? save = null, bool themed = false)
        {
            var b = new Bind { Section = section, Label = L(label), Row = MakeRow(label, c), Load = load, Save = save, Themed = themed };
            binds.Add(b); probe.Rows.Add((section, b.Label, b.Row));
            return b;
        }
        void Header(string text)
        {
            var h = new TextBlock { Text = L(text), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2), Opacity = 0.85 };
            var b = new Bind { Section = section, Label = "", Row = h };
            binds.Add(b);
        }
        ValueSlider Slider(string label, Func<LookPrefs, double> get, Action<LookPrefs, double> set, double min, double max, double step)
        {
            var s = new ValueSlider(get(look), min, max, step);
            Add(label, s, (l, _) => s.Value = get(l), (l, _) => { if (double.IsFinite(s.Value)) set(l, s.Value); });
            return s;
        }
        NumericUpDown Nud(string name, string label, Func<LookPrefs, double> get, Action<LookPrefs, double> set, double min, double max, double inc, string fmt = "0.###")
        {
            var n = new NumericUpDown { Name = name, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)inc, FormatString = fmt, Value = (decimal)get(look), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
            if (label != "") Add(label, n, (l, _) => n.Value = (decimal)get(l), (l, _) => set(l, (double)(n.Value ?? 0)));
            return n;
        }
        CheckBox Check(string label, string text, Func<LookPrefs, bool> get, Action<LookPrefs, bool> set)
        {
            var c = new CheckBox { IsChecked = get(look), Content = L(text) };
            Add(label, c, (l, _) => c.IsChecked = get(l), (l, _) => set(l, c.IsChecked == true));
            return c;
        }
        TextBox ColorRow(string label, Func<LookPrefs, string> get, Action<LookPrefs, string> set, string watermark = "по умолчанию (#RRGGBB)")
        {
            var t = new TextBox { Text = get(look), Watermark = L(watermark), MinWidth = 160 };
            var sw = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
            void Upd() => sw.Background = UiLook.ParseColor(t.Text) is { } c ? new SolidColorBrush(c) : Brushes.Transparent;
            t.TextChanged += (_, _) => Upd(); Upd();
            var pick = new Button { Content = sw, Padding = new Thickness(2), Margin = new Thickness(6, 0, 0, 0), Classes = { "tool" } };
            ToolTip.SetTip(pick, L("Выбрать цвет"));
            pick.Flyout = DocCommands.ColorPalette(c => t.Text = c);
            Add(label, new StackPanel { Orientation = Orientation.Horizontal, Children = { t, pick } },
                (l, _) => t.Text = get(l), (l, _) => set(l, t.Text?.Trim() ?? ""));
            return t;
        }
        // цвет по теме: читается и пишется в поле выбранной темы
        TextBox ThemedColor(string label, Func<LookPrefs, string> dark, Action<LookPrefs, string> setDark, Func<LookPrefs, string> light, Action<LookPrefs, string> setLight)
        {
            var t = ColorRow(label, l => editDark ? dark(l) : light(l), (l, v) => { if (editDark) setDark(l, v); else setLight(l, v); });
            binds[^1].Themed = true;
            return t;
        }
        void EditThemeCombo()
        {
            var cb = new ComboBox { ItemsSource = new[] { L("Тёмная"), L("Светлая") }, SelectedIndex = editDark ? 0 : 1, MinWidth = 200, Name = "set_edit_theme" };
            editCombos.Add(cb); probe.EditTheme.Add(cb);
            cb.SelectionChanged += (_, _) =>
            {
                bool d = cb.SelectedIndex == 0;
                if (d == editDark || cb.SelectedIndex < 0) return;
                SwitchTheme(d);
            };
            Add("Цвета для темы", cb);
        }
        void SwitchTheme(bool dark)
        {
            if (EditThemeOff) { editDark = dark; return; }
            foreach (var b in binds) if (b.Themed) b.Save?.Invoke(look, editDark);
            editDark = dark;
            foreach (var b in binds) if (b.Themed) b.Load?.Invoke(look, editDark);
            foreach (var c in editCombos) if (c.SelectedIndex != (dark ? 0 : 1)) c.SelectedIndex = dark ? 0 : 1;
        }

        // ================================================================ Общие
        section = "general";
        var theme = new ComboBox { ItemsSource = new[] { L("Тёмная"), L("Светлая") }, SelectedIndex = w.IsDark ? 0 : 1, MinWidth = 200, Name = "set_theme" };
        var lang = new ComboBox { MinWidth = 200, Name = "set_lang" };
        lang.ItemsSource = new[] { WithIcon("language_ru_but.svg", "Русский"), WithIcon("language_en_but.svg", "English") };
        if (!DocCommands.LangFixOff) lang.SelectedIndex = prefs.Language == "en" ? 1 : 0;
        string[] glModes = { "auto", "angle", "wgl", "software" };
        var gl = new ComboBox { ItemsSource = new[] { L("Автоматически"), L("ANGLE (Direct3D 11)"), L("WGL (настольный OpenGL)"), L("Программная отрисовка") }, SelectedIndex = Math.Max(0, Array.IndexOf(glModes, prefs.GlMode ?? "auto")), MinWidth = 200, Name = "set_gl" };
        Add("Тема интерфейса", theme);
        Add("Язык", lang);
        Add("Графика (со следующего запуска)", gl);

        // ================================================================ Внешний вид
        section = "appearance";
        EditThemeCombo();
        Slider("Размер текста интерфейса", l => l.UiFontSize, (l, v) => l.UiFontSize = v, 9, 22, 1);
        ThemedColor("Фон вида сверху", l => l.BackgroundTopDark, (l, v) => l.BackgroundTopDark = v, l => l.BackgroundTopLight, (l, v) => l.BackgroundTopLight = v);
        ThemedColor("Фон вида снизу", l => l.BackgroundBottomDark, (l, v) => l.BackgroundBottomDark = v, l => l.BackgroundBottomLight, (l, v) => l.BackgroundBottomLight = v);
        Header("Сечение");
        Check("Штриховка", "Штриховать сечение деталей и сборок", l => l.SectionHatch, (l, v) => l.SectionHatch = v);
        ColorRow("Цвет штриховки сечения", l => l.SectionHatchColor, (l, v) => l.SectionHatchColor = v, "по материалу (#RRGGBB)");
        ColorRow("Цвет заливки сечения", l => l.SectionFillColor, (l, v) => l.SectionFillColor = v, "по материалу (#RRGGBB)");

        // ================================================================ Панели инструментов
        section = "toolbars";
        Slider("Кнопка полосы у края вида, пикс.", l => l.StripButtonPx, (l, v) => l.StripButtonPx = v, 22, 64, 1);
        Slider("Иконка на кнопке полосы, пикс.", l => l.StripIconPx, (l, v) => l.StripIconPx = v, 12, 48, 1);
        Slider("Иконка на кнопках панелей, пикс.", l => l.PanelIconPx, (l, v) => l.PanelIconPx = v, 12, 40, 1);

        // ================================================================ Дерево модели
        section = "tree";
        Slider("Размер текста дерева (0 — как интерфейс)", l => l.TreeFontSize, (l, v) => l.TreeFontSize = v, 0, 22, 1);
        Slider("Значки дерева, пикс.", l => l.TreeIconPx, (l, v) => l.TreeIconPx = v, 10, 32, 1);
        Slider("Высота строки, пикс.", l => l.TreeRowPx, (l, v) => l.TreeRowPx = v, 16, 48, 1);
        Slider("Ширина панели дерева, пикс.", l => l.TreePanelPx, (l, v) => l.TreePanelPx = v, 160, 600, 10);

        // ================================================================ Панель свойств
        section = "props";
        Slider("Ширина панели свойств, пикс.", l => l.PropsPanelPx, (l, v) => l.PropsPanelPx = v, 220, 600, 10);
        Slider("Размер текста панели (0 — как интерфейс)", l => l.PropsFontSize, (l, v) => l.PropsFontSize = v, 0, 22, 1);

        // ================================================================ Управление операциями
        section = "ops";
        var prev = new ComboBox { ItemsSource = new[] { L("Полный (деталь считается)"), L("Контур") }, SelectedIndex = (prefs.PreviewMode ?? "full") == "contour" ? 1 : 0, MinWidth = 200 };
        Add("Предпросмотр операций", prev);
        EditThemeCombo();
        Header("Стрелка операции");
        Slider("Длина стрелки, пикс.", l => l.HandleLengthPx, (l, v) => l.HandleLengthPx = v, 30, 200, 1);
        Slider("Шарик стрелки, пикс.", l => l.HandleKnobPx, (l, v) => l.HandleKnobPx = v, 6, 32, 1);
        Slider("Толщина линии стрелки", l => l.HandleLineWidth, (l, v) => l.HandleLineWidth = v, 0.5, 5, 0.1);
        ThemedColor("Цвет стрелки", l => l.HandleColorDark, (l, v) => l.HandleColorDark = v, l => l.HandleColorLight, (l, v) => l.HandleColorLight = v);
        ThemedColor("Цвет обратного направления", l => l.HandleAltColorDark, (l, v) => l.HandleAltColorDark = v, l => l.HandleAltColorLight, (l, v) => l.HandleAltColorLight = v);

        // ================================================================ Эскиз
        section = "sketch";
        EditThemeCombo();
        ThemedColor("Линии эскиза", l => l.SketchColorDark, (l, v) => l.SketchColorDark = v, l => l.SketchColorLight, (l, v) => l.SketchColorLight = v);
        Slider("Толщина линий эскиза", l => l.SketchLineWidth, (l, v) => l.SketchLineWidth = v, 0.5, 6, 0.1);
        Slider("Точки эскиза, пикс.", l => l.SketchPointPx, (l, v) => l.SketchPointPx = v, 3, 16, 1);
        Slider("Высота цифр размеров, пикс.", l => l.DimensionTextPx, (l, v) => l.DimensionTextPx = v, 6, 40, 1);
        Slider("Толщина линии цифр размеров", l => l.DimensionTextLine, (l, v) => l.DimensionTextLine = v, 0.3, 3, 0.05);
        Slider("Значки связей эскиза, пикс.", l => l.ConstraintIconPx, (l, v) => l.ConstraintIconPx = v, 8, 48, 1);
        Check("Автосвязи", "Ставить связи при рисовании", l => l.SketchAutoConstrain, (l, v) => l.SketchAutoConstrain = v);
        Check("Повтор инструмента", "Инструмент остаётся после фигуры", l => l.SketchToolRepeat, (l, v) => l.SketchToolRepeat = v);

        // ================================================================ Вид модели
        section = "model";
        EditThemeCombo();
        Header("Пол");
        Check("Пол", "Показывать пол под моделью", l => l.FloorOn, (l, v) => l.FloorOn = v);
        Check("Тень", "Тень модели на полу", l => l.FloorShadow, (l, v) => l.FloorShadow = v);
        Check("Отражение", "Отражение модели в полу", l => l.FloorReflection, (l, v) => l.FloorReflection = v);
        Nud("set_mv_gap", "Зазор от нижней точки модели до пола, мм", l => l.FloorGapMm, (l, v) => l.FloorGapMm = v, 0, 100000, 1);
        Slider("Доля отражения, %", l => l.FloorReflectPct, (l, v) => l.FloorReflectPct = v, 0, 70, 1);
        ThemedColor("Цвет пола", l => l.FloorColorDark, (l, v) => l.FloorColorDark = v, l => l.FloorColorLight, (l, v) => l.FloorColorLight = v);
        // k133 (замечание 29.10, 08:55, п. 3): размер и форма пола, сетка пола
        Slider("Размер пола (0 — по модели, 100 — в 10 раз больше)", l => l.FloorSizePct, (l, v) => l.FloorSizePct = v, 0, 100, 1);
        {
            string[] shapes = { "Rectangle", "Ellipse", "Circle", "Square" };
            var cb = new ComboBox { ItemsSource = new[] { L("Прямоугольник"), L("Эллипс"), L("Круг"), L("Квадрат") }, MinWidth = 200, Name = "set_mv_shape" };
            int Idx(LookPrefs l) => Math.Max(0, Array.IndexOf(shapes, l.FloorShape));
            cb.SelectedIndex = Idx(look);
            Add("Форма пола", cb, (l, _) => cb.SelectedIndex = Idx(l), (l, _) => l.FloorShape = shapes[Math.Max(0, cb.SelectedIndex)]);
        }
        Check("Сетка пола", "Показывать сетку пола (в единицах документа)", l => l.FloorGrid, (l, v) => l.FloorGrid = v);
        Slider("Толщина линий сетки (0 — 0.1 пикс., 100 — 2 пикс.)", l => l.FloorGridWidthPct, (l, v) => l.FloorGridWidthPct = v, 0, 100, 1);
        ThemedColor("Цвет линий сетки", l => l.FloorGridColorDark, (l, v) => l.FloorGridColorDark = v, l => l.FloorGridColorLight, (l, v) => l.FloorGridColorLight = v);
        void LightRows(string n, string title,
            Func<LookPrefs, double> x, Action<LookPrefs, double> sx, Func<LookPrefs, double> y, Action<LookPrefs, double> sy, Func<LookPrefs, double> z, Action<LookPrefs, double> sz,
            Func<LookPrefs, double> dx, Action<LookPrefs, double> sdx, Func<LookPrefs, double> dy, Action<LookPrefs, double> sdy, Func<LookPrefs, double> dz, Action<LookPrefs, double> sdz,
            Func<LookPrefs, string> col, Action<LookPrefs, string> scol, Func<LookPrefs, double> pw, Action<LookPrefs, double> spw)
        {
            Header(title);
            Control Triple(string nm, Func<LookPrefs, double>[] g, Action<LookPrefs, double>[] s, string label)
            {
                var boxes = new NumericUpDown[3];
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                string[] ax = { "X", "Y", "Z" };
                for (int i = 0; i < 3; i++)
                {
                    boxes[i] = Nud($"set_mv_{nm}{ax[i].ToLowerInvariant()}", "", g[i], s[i], -10, 10, 0.05, "0.###");
                    boxes[i].MinWidth = 70; boxes[i].Width = 84; boxes[i].ShowButtonSpinner = false;
                    sp.Children.Add(new TextBlock { Text = ax[i], VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
                    sp.Children.Add(boxes[i]);
                }
                Add(label, sp,
                    (l, _) => { for (int i = 0; i < 3; i++) boxes[i].Value = (decimal)g[i](l); },
                    (l, _) => { for (int i = 0; i < 3; i++) s[i](l, (double)(boxes[i].Value ?? 0)); });
                return sp;
            }
            Triple(n + "p", new[] { x, y, z }, new[] { sx, sy, sz }, "Положение (X вправо, Y вверх, Z на зрителя)");
            Triple(n + "d", new[] { dx, dy, dz }, new[] { sdx, sdy, sdz }, "Направление (0; 0; 0 — на центр)");
            ColorRow("Цвет", col, scol);
            Slider("Сила", pw, spw, 0, 3, 0.02);
        }
        LightRows("l1", "Свет 1 (основной)",
            l => l.Light1X, (l, v) => l.Light1X = v, l => l.Light1Y, (l, v) => l.Light1Y = v, l => l.Light1Z, (l, v) => l.Light1Z = v,
            l => l.Light1DX, (l, v) => l.Light1DX = v, l => l.Light1DY, (l, v) => l.Light1DY = v, l => l.Light1DZ, (l, v) => l.Light1DZ = v,
            l => l.Light1Color, (l, v) => l.Light1Color = v, l => l.Light1Power, (l, v) => l.Light1Power = v);
        LightRows("l2", "Свет 2 (заполняющий)",
            l => l.Light2X, (l, v) => l.Light2X = v, l => l.Light2Y, (l, v) => l.Light2Y = v, l => l.Light2Z, (l, v) => l.Light2Z = v,
            l => l.Light2DX, (l, v) => l.Light2DX = v, l => l.Light2DY, (l, v) => l.Light2DY = v, l => l.Light2DZ, (l, v) => l.Light2DZ = v,
            l => l.Light2Color, (l, v) => l.Light2Color = v, l => l.Light2Power, (l, v) => l.Light2Power = v);
        Header("Рассеянный свет (Ambient)");
        ColorRow("Цвет рассеянного света", l => l.AmbientColor, (l, v) => l.AmbientColor = v);
        Slider("Сила рассеянного света", l => l.AmbientPower, (l, v) => l.AmbientPower = v, 0, 2, 0.02);
        Header("Опорные плоскости");
        ThemedColor("Цвет плоскостей", l => l.DatumPlaneColorDark, (l, v) => l.DatumPlaneColorDark = v, l => l.DatumPlaneColorLight, (l, v) => l.DatumPlaneColorLight = v);
        Slider("Непрозрачность плоскостей, %", l => l.DatumPlaneOpacityPct, (l, v) => l.DatumPlaneOpacityPct = v, 0, 100, 1);
        Check("Контур плоскостей", "Показывать контур плоскостей", l => l.DatumPlaneOutline, (l, v) => l.DatumPlaneOutline = v);
        Header("Модель и куб вида");
        Slider("Толщина рёбер модели", l => l.ModelEdgeWidth, (l, v) => l.ModelEdgeWidth = v, 0.5, 5, 0.1);
        Slider("Размер куба вида, пикс.", l => l.ViewCubePx, (l, v) => l.ViewCubePx = v, 90, 300, 5);

        // ================================================================ разметка: поиск, список разделов, строки
        var sections = new (string Key, string Title, string Icon)[]
        {
            ("general", "Общие", "settings_general_ico.svg"), ("appearance", "Внешний вид", "theme_but.svg"),
            ("toolbars", "Панели инструментов", "settings_but.svg"), ("tree", "Дерево модели", "tree_part_ico.svg"),
            ("props", "Панель свойств", "properties_but.svg"), ("ops", "Управление операциями", "f_extrude_but.svg"),
            ("sketch", "Эскиз", "tree_sketch_ico.svg"), ("model", "Вид модели", "view_iso_but.svg"),
        };
        var panels = new Dictionary<string, StackPanel>();
        var body = new StackPanel { Spacing = 2, Margin = new Thickness(16, 8, 16, 12) };
        var titles = new Dictionary<string, TextBlock>();
        foreach (var (key, title, _) in sections)
        {
            var sp = new StackPanel { Spacing = 2 };
            var head = new TextBlock { Text = L(title), FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 6) };
            titles[key] = head;
            sp.Children.Add(head);
            foreach (var b in binds.Where(b => b.Section == key)) sp.Children.Add(b.Row);
            panels[key] = sp; body.Children.Add(sp);
        }
        var list = new ListBox { Name = "set_sections", MinWidth = 210 };
        foreach (var (key, title, icon) in sections) list.Items.Add(new ListBoxItem { Content = WithIcon(icon, L(title), 18), Tag = key, Padding = new Thickness(8, 6) });
        var search = new TextBox { Name = "set_search", Watermark = L("Поиск настройки…"), Margin = new Thickness(0, 0, 0, 8) };
        var resetBtn = new Button { Content = L("Сбросить раздел"), Name = "set_reset" };
        string Current() => (list.SelectedItem as ListBoxItem)?.Tag as string ?? "general";
        void Show()
        {
            var q = search.Text?.Trim() ?? "";
            if (q.Length == 0)
            {
                var cur = Current();
                foreach (var (k, p) in panels) p.IsVisible = SectionsOff || k == cur;
                foreach (var b in binds) b.Row.IsVisible = true;
                foreach (var t in titles.Values) t.IsVisible = SectionsOff;
                resetBtn.IsVisible = cur != "general";
                return;
            }
            // поиск: подходящие строки всех разделов под заголовками разделов
            foreach (var (k, p) in panels)
            {
                bool any = false;
                foreach (var b in binds.Where(b => b.Section == k))
                {
                    bool m = b.Label.Length > 0 && b.Label.Contains(q, StringComparison.OrdinalIgnoreCase);
                    b.Row.IsVisible = m; any |= m;
                }
                p.IsVisible = any; titles[k].IsVisible = true;
            }
            resetBtn.IsVisible = false;
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) { search.Text = ""; Show(); } };
        search.TextChanged += (_, _) => Show();
        void ResetSection(string key)
        {
            var d = new LookPrefs();
            bool keep = editDark;
            foreach (var b in binds.Where(b => b.Section == key && b.Load != null))
            {
                if (b.Themed)
                    foreach (var dk in new[] { true, false }) { editDark = dk; b.Load!(d, dk); b.Save?.Invoke(look, dk); }
                editDark = keep;
                b.Load!(b.Themed ? look : d, editDark);
            }
            if (key == "ops") prev.SelectedIndex = 0;
        }
        resetBtn.Click += (_, _) => ResetSection(Current());
        list.SelectedIndex = 0;
        probe.Sections = list; probe.Search = search; probe.Reset = ResetSection;
        probe.Find = name => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(body).OfType<Control>().FirstOrDefault(c => c.Name == name);

        var ok = new Button { Content = L("ОК"), Classes = { "accent" }, MinWidth = 96, Name = "set_ok" };
        var cancel = new Button { Content = L("Отмена"), MinWidth = 96, Name = "set_cancel" };
        cancel.Click += (_, _) => win.Close();
        ok.Click += (_, _) =>
        {
            foreach (var b in binds) b.Save?.Invoke(look, editDark);
            prefs.Look = look;
            if ((theme.SelectedIndex == 0) != w.IsDark) w.ApplyTheme(theme.SelectedIndex == 0);
            if ((lang.SelectedIndex == 1) != (prefs.Language == "en")) w.ToggleLanguage();
            prefs.PreviewMode = prev.SelectedIndex == 1 ? "contour" : "full";
            prefs.GlMode = glModes[Math.Max(0, gl.SelectedIndex)];
            prefs.Save();
            w.ApplyLook();
            win.Close();
        };
        var left = new DockPanel { Margin = new Thickness(12, 12, 0, 12), Width = 230 };
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search); left.Children.Add(list);
        var bottom = new DockPanel { Margin = new Thickness(16, 8, 16, 14) };
        DockPanel.SetDock(resetBtn, Dock.Left);
        bottom.Children.Add(resetBtn);
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        var right = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(bottom);
        right.Children.Add(new ScrollViewer { Content = body });
        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        var sep = new Border { Width = 1, Margin = new Thickness(12, 0, 0, 0) };
        sep.Bind(Border.BackgroundProperty, sep.GetResourceObservable("P3.Border"));
        DockPanel.SetDock(sep, Dock.Left);
        root.Children.Add(left); root.Children.Add(sep); root.Children.Add(right);
        win.Content = root;
        Show();
        Last = probe;
        DocCommands.LastSettingsWindow = win;
        await win.ShowDialog(w);
    }
}
