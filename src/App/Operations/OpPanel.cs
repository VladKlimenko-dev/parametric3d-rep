using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

/// <summary>
/// ПОЛЕ ССЫЛОК ОПЕРАЦИИ: что выбрать в виде или дереве (рёбра, грань, плоскость, эскиз…). Активное поле получает выбор
/// вида: щелчки по модели попадают в него (фильтр выбора — по полю). Щелчок по полю делает его активным.
/// </summary>
public sealed class RefField
{
    public required string Label { get; init; }
    public PickFilter Filter { get; init; } = PickFilter.All;
    public Func<PickRef, bool> Accept { get; init; } = _ => true;
    public bool Single { get; init; }
    /// <summary>Как назвать выбранное (по умолчанию — имя выбора вида).</summary>
    public Func<PickRef, string>? Describe { get; init; }
    /// <summary>Поле можно оставить пустым (необязательная ссылка).</summary>
    public bool Optional { get; init; }
    /// <summary>k137: щелчок по виду добавляет к полю (по порядку), а не заменяет.</summary>
    public bool Accumulate { get; init; }
    /// <summary>k137: подсказка поля (что выбирать и зачем).</summary>
    public string? Tip { get; init; }
    public readonly List<PickRef> Items = new();
    /// <summary>Правка готовой операции: сколько ссылок уже записано в ней (пока поле не трогали).</summary>
    public int Kept;
    /// <summary>k98+: правка — что уже задано (текстом), пока поле не трогали (настройка эскиза: основа, нуль, верх).</summary>
    public string? KeptText;
    /// <summary>k99: что из видимого под курсором годится полю (иное под курсором не подсвечивается и не берётся).</summary>
    public Func<PickRef, bool>? Prefer { get; init; }
    /// <summary>k99: поле доступно только при условии (иначе — серое, щелчок не делает его активным).</summary>
    public Func<bool>? Enabled { get; init; }
    /// <summary>k107: поле видно (и берёт выбор) только при условии.</summary>
    public Func<bool>? Visible { get; set; }
    public bool Live => (Enabled?.Invoke() ?? true) && (Visible?.Invoke() ?? true);
    public bool Touched;

    internal Border? Box;
    internal TextBlock? Text;
    /// <summary>k148 (замечание 01.11, 09:14, п. 4): отметка поля эскиза — ✓ эскиз задан и годен операции, ! — не годен.</summary>
    internal TextBlock? Mark;
    /// <summary>k148: правка — эскизы, записанные в операции (имена вместо «как было»), пока поле не трогали.</summary>
    public List<Parametric3D.Model.Sketching.Sketch>? KeptSketches;
    /// <summary>k148: годен ли эскиз этой операции (замкнутый контур у тела, кривые у траектории); null — отметки нет.</summary>
    public Func<Parametric3D.Model.Sketching.Sketch, bool>? SketchFits { get; set; }
    /// <summary>Эскизы поля сейчас: выбранные или записанные в операции.</summary>
    public List<Parametric3D.Model.Sketching.Sketch> CurrentSketches =>
        Items.Count > 0 ? Items.Select(Operations.Refs.SketchOf).OfType<Parametric3D.Model.Sketching.Sketch>().Distinct().ToList()
        : !Touched && KeptSketches != null ? KeptSketches : new();
    /// <summary>true — все эскизы поля годны; false — какой-то нет; null — эскизов нет (или проверка не задана).</summary>
    public bool? Fits
    {
        get
        {
            if (SketchFits == null) return null;
            var sk = CurrentSketches; if (sk.Count == 0) return null;
            foreach (var s in sk) { bool ok; try { ok = SketchFits(s); } catch { ok = false; } if (!ok) return false; }
            return true;
        }
    }
    /// <summary>Обновить подпись и отметку поля.</summary>
    public void Refresh()
    {
        if (Text != null) Text.Text = Summary;
        if (Mark != null)
        {
            var fits = Fits;
            Mark.IsVisible = fits != null;
            Mark.Text = fits == true ? "✓" : "!";
            Mark.Foreground = Box?.TryFindResource(fits == true ? "P3.Success" : "P3.Error", Box.ActualThemeVariant, out var v) == true && v is IBrush b ? b : (fits == true ? Brushes.SeaGreen : Brushes.IndianRed);
            ToolTip.SetTip(Mark, fits == true ? L("Эскиз задан и подходит операции") : L("Эскиз не подходит операции (нет замкнутого контура или нет кривых)"));
        }
    }

    public bool IsSet => Items.Count > 0 || (!Touched && (Kept > 0 || KeptText != null));

    public string Summary =>
        Items.Count > 0 ? (Single ? (Describe ?? Viewport.Selection.Label)(Items[0]) : L("выбрано: {0}", Items.Count))
        : !Touched && KeptText != null ? KeptText
        : !Touched && KeptSketches is { Count: > 0 } ks ? string.Join(", ", ks.Select(k => L("Эскиз «{0}»", k.Name)))
        : !Touched && Kept > 0 ? L("как было ({0})", Kept)
        : Optional ? L("не задано") : L("выберите в виде");

    /// <summary>Взять подходящее из выбора вида; true — что-то изменилось.</summary>
    public bool Take(IEnumerable<PickRef> sel)
    {
        // u12: уже взятое поле не теряет: после перестроения предпросмотра номер грани в детали другой, и проверка «подходит ли»
        // (плоская ли грань) по старому номеру не проходит — поле опустошалось, операция выпадала, деталь возвращалась, поле
        // снова заполнялось: отверстие мигало при каждом движении мыши
        var ok = sel.Where(r => Accept(r) || Items.Any(i => i.Id == r.Id && i.Kind == r.Kind)).ToList();
        if (Single && ok.Count > 1) ok = ok.Skip(ok.Count - 1).ToList();
        if (ok.Count == Items.Count && ok.Select(r => r.Id).SequenceEqual(Items.Select(r => r.Id))) return false;
        Items.Clear();
        Items.AddRange(ok);
        Touched = true;
        return true;
    }
}

/// <summary>Строитель панели операции: подписи слева, поля справа; изменение поля сразу перестраивает предпросмотр.</summary>
public sealed class OpPanel
{
    private readonly StackPanel _host;
    /// <summary>k153: корень панели (проверки).</summary>
    public StackPanel Host => _host;
    private readonly Operation _op;
    public readonly List<RefField> Refs = new();
    public TextBlock Status { get; }

    public OpPanel(StackPanel host, Operation op)
    {
        _host = host;
        _op = op;
        Status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    }

    private Grid Row(string label, Control field)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("108,*"), Margin = new Thickness(0, 1) };
        g.Children.Add(new TextBlock { Text = L(label), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
        _host.Children.Add(g);
        return g;
    }

    public void Header(string text) =>
        _host.Children.Add(new TextBlock { Text = L(text), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 2) });

    /// <summary>k112: строка-справка, обновляемая при каждом Refresh (уклон: угол между гранями было → станет).</summary>
    public TextBlock Info(Func<string> get, Func<bool>? visible = null)
    {
        var tb = new TextBlock { Text = get(), Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _syncers.Add(() => tb.Text = get());
        _host.Children.Add(tb);
        When(tb, visible);
        Infos.Add(tb);
        return tb;
    }
    public readonly List<TextBlock> Infos = new();

    public void Note(string text) =>
        _host.Children.Add(new TextBlock { Text = L(text), Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });

    /// <summary>Поле ссылок: щелчок — сделать активным (выбор вида идёт в него), «×» — очистить.</summary>
    public RefField Ref(RefField f) => Ref(f, null);

    /// <summary>k107: поле ссылок, видное только при условии (выбор по кнопке: точка начала, плоскость нового эскиза).</summary>
    public RefField Ref(RefField f, Func<bool>? visible)
    {
        Refs.Add(f);
        var text = new TextBlock { Text = f.Summary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var clear = new Button { Content = "×", Classes = { "tool" }, Padding = new Thickness(6, 0), MinHeight = 22 };
        ToolTip.SetTip(clear, L("Очистить"));
        var dock = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        dock.Children.Add(clear);
        // k148: отметка поля эскиза (✓ — задан и годен)
        var mark = new TextBlock { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0), IsVisible = false };
        DockPanel.SetDock(mark, Dock.Right);
        dock.Children.Add(mark);
        dock.Children.Add(text);
        var box = new Border
        {
            Child = dock, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 2, 3), BorderThickness = new Thickness(1.5),
            Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent,
        };
        f.Box = box; f.Text = text; f.Mark = mark;
        if (f.Tip != null && !TipsOff) ToolTip.SetTip(box, L(f.Tip));
        box.PointerPressed += (_, e) => { if (f.Enabled?.Invoke() == false) return; _op.Activate(f); e.Handled = true; };
        if (f.Enabled != null) _enabled.Add((box, f.Enabled));
        clear.Click += (_, _) => { f.Items.Clear(); f.Touched = true; _op.Activate(f); _op.RefChanged(f); };
        When(Row(f.Label, box), visible);
        if (visible != null) f.Visible = visible;
        return f;
    }

    /// <summary>k107: кнопка действия в панели (под полем): «Создать эскиз сечения» и т. п.</summary>
    public Button Button(string label, Action click, Func<bool>? enabled = null, string? tip = null)
    {
        var b = new Button { Content = L(label), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(108, 1, 0, 3) };
        if (tip != null) ToolTip.SetTip(b, L(tip));
        b.Click += (_, _) => click();
        if (enabled != null) _enabled.Add((b, enabled));
        _host.Children.Add(b);
        Buttons[label] = b;
        return b;
    }
    /// <summary>Кнопки панели по подписи (проверки).</summary>
    public readonly Dictionary<string, Button> Buttons = new();

    public void Number(string label, Func<ValueRef> get, Action<ValueRef> set, string unit = "мм", Func<bool>? visible = null, Action? focused = null, double scrub = 0, Func<bool>? enabled = null)
    {
        // u13: длины — в единицах детали (мм или дюймы); формулы — как записаны
        // k123: поле длины — в единице системы детали (см, м, дюйм, фут…), не только в дюймах
        double uf = Commands.DisplayUnits.SketchUnitsOff ? (Commands.DisplayUnits.Inch ? Commands.DisplayUnits.InchMm : 1) : Commands.DisplayUnits.Factor;
        // k128: вид величины поля — для формулы без единицы (длина — в единице детали, угол — в градусах)
        sbyte kind = unit == "мм" ? ValueRef.KindLength : unit == "°" ? ValueRef.KindAngle : ValueRef.KindNumber;
        bool scaled = unit == "мм" && Math.Abs(uf - 1) > 1e-12;
        if (scaled) { var g0 = get; var s0 = set; get = () => { var v = g0(); return v.Formula != null ? v : new ValueRef(v.Value / uf); }; set = v => s0(v.Formula != null ? v : new ValueRef(v.Value * uf)); unit = Commands.DisplayUnits.LengthUnit; }
        var tb = new TextBox { Text = Show(get()), MinWidth = 60 };
        var unitText = new TextBlock { Text = L(unit), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(unitText, Dock.Right);
        dock.Children.Add(unitText);
        // k139 (замечание 30.10, 00:56, п. 1): стрелки поля с протяжкой — тянуть вверх с нажатой левой кнопкой — значение
        // растёт, вниз — убывает (шаг scrub на 4 пикселя; Shift ×10, Ctrl ×0.1); щелчок по стрелке — один шаг. Вид меняется
        // при протяжке (FieldChanged на каждом шаге).
        if (scrub > 0 && !Commands.Scrub.Off)
        {
            var arrows = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            foreach (bool up in new[] { true, false })
                arrows.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = Avalonia.Media.Geometry.Parse(up ? "M0,5 L4,0 L8,5 Z" : "M0,0 L4,5 L8,0 Z"),
                    Width = 8, Height = 5, Stretch = Avalonia.Media.Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center,
                });
            foreach (var pa in arrows.Children.OfType<Avalonia.Controls.Shapes.Path>()) pa.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, pa.GetResourceObservable("P3.Text"));
            var handle = new Border { Child = arrows, Width = 16, Background = Avalonia.Media.Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0), Name = "scrub_" + label };
            ToolTip.SetTip(handle, L("Тянуть вверх или вниз с нажатой левой кнопкой — изменить значение; щелчок — шаг"));
            void Put(double v) { var r = new ValueRef(Math.Round(v, 6)); tb.Text = Show(r); tb.Classes.Remove("err"); set(r); _op.FieldChanged(); }
            Commands.Scrub.Attach(handle, () => get().Value, Put, () => scrub, dir => Put(get().Value + dir * scrub));
            DockPanel.SetDock(handle, Dock.Right);
            dock.Children.Add(handle);
            Scrubs[label] = handle;
        }
        dock.Children.Add(tb);
        void Commit()
        {
            var v = Parse(tb.Text ?? "", get(), kind);
            if (v == null) { tb.Classes.Add("err"); return; }
            tb.Classes.Remove("err");
            set(v.Value);
            _op.FieldChanged();
        }
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down && double.TryParse((tb.Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
            {
                double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1 : 1;
                tb.Text = (x + (e.Key == Key.Up ? step : -step)).ToString("0.###", CultureInfo.InvariantCulture);
                Commit();
                e.Handled = true;
            }
        };
        tb.LostFocus += (_, _) => Commit();
        if (focused != null) tb.GotFocus += (_, _) => focused();
        ToolTip.SetTip(tb, L("Число или формула с параметрами; ↑↓ — шаг 1, Shift — 10, Ctrl — 0.1; Enter — применить"));
        var row = Row(label, dock);
        When(row, visible);
        if (enabled != null) { _enabled.Add((row, enabled)); row.IsEnabled = enabled(); row.Opacity = row.IsEnabled ? 1 : 0.45; }
        _numbers.Add((tb, get));
    }

    /// <summary>
    /// Список с меняющимися пунктами (типы резьбы по выбранной грани): пункты не переводятся (обозначения); при каждом
    /// Refresh пункты и выбранный берутся заново.
    /// </summary>
    public ComboBox List(string label, Func<IReadOnlyList<string>> items, Func<int> get, Action<int> set, Func<bool>? visible = null, Func<bool>? enabled = null)
    {
        var shown = items().ToList();
        var cb = new ComboBox { ItemsSource = shown, SelectedIndex = get(), HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 360 };
        bool sync = false;
        cb.SelectionChanged += (_, _) => { if (sync || cb.SelectedIndex < 0) return; set(cb.SelectedIndex); _op.FieldChanged(); };
        _syncers.Add(() =>
        {
            sync = true;
            try
            {
                var now = items();
                if (!now.SequenceEqual(shown)) { shown = now.ToList(); cb.ItemsSource = shown; }
                int g = get();
                if (cb.SelectedIndex != g) cb.SelectedIndex = g;
            }
            finally { sync = false; }
        });
        var row = Row(label, cb);
        When(row, visible);
        if (enabled != null) _enabled.Add((cb, enabled));
        Lists[label] = cb;
        return cb;
    }
    /// <summary>Списки панели по подписи (проверки).</summary>
    public readonly Dictionary<string, ComboBox> Lists = new();

    /// <summary>u13: строка текста (формула кривой и т. п.): применяется по Enter и при уходе из поля.</summary>
    public void Text(string label, Func<string> get, Action<string> set, Func<bool>? visible = null)
    {
        var tb = new TextBox { Text = get(), MinWidth = 120 };
        void Commit() { var t = tb.Text ?? ""; if (t == get()) return; set(t); _op.FieldChanged(); }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        tb.LostFocus += (_, _) => Commit();
        When(Row(label, tb), visible);
    }

    /// <summary>k137: мутация P3D_OPTIPS=0 — поля и флажки панели без подсказок.</summary>
    public static readonly bool TipsOff = Environment.GetEnvironmentVariable("P3D_OPTIPS") == "0";
    public readonly Dictionary<string, Control> Checks = new();
    /// <summary>k139: стрелки с протяжкой у числовых полей (по подписи поля).</summary>
    public readonly Dictionary<string, Control> Scrubs = new();
    public void Check(string label, Func<bool> get, Action<bool> set, Func<bool>? visible = null, Func<bool>? enabled = null, string? tip = null)
    {
        var cb = new CheckBox { IsChecked = get(), Content = L(label) };
        if (tip != null && !TipsOff) ToolTip.SetTip(cb, L(tip));
        Checks[label] = cb;
        if (enabled != null) _enabled.Add((cb, enabled));
        bool sync = false;
        cb.IsCheckedChanged += (_, _) => { if (sync) return; set(cb.IsChecked == true); _op.FieldChanged(); };
        _syncers.Add(() => { sync = true; cb.IsChecked = get(); sync = false; });
        _host.Children.Add(cb);
        When(cb, visible);
    }

    public void Choice(string label, string[] options, Func<int> get, Action<int> set, Func<bool>? visible = null)
    {
        var cb = new ComboBox { ItemsSource = options.Select(o => L(o)).ToList(), SelectedIndex = get(), HorizontalAlignment = HorizontalAlignment.Stretch };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(cb.SelectedIndex); _op.FieldChanged(); } };
        When(Row(label, cb), visible);
    }

    /// <summary>Переключатель из двух-трёх кнопок (Добавить / Вырезать).</summary>
    public void Segments(string label, string[] options, Func<int> get, Action<int> set)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var buttons = new List<ToggleButton>();
        for (int i = 0; i < options.Length; i++)
        {
            int k = i;
            var b = new ToggleButton { Content = L(options[i]), IsChecked = get() == i, Padding = new Thickness(10, 3) };
            b.Click += (_, _) => { set(k); foreach (var o in buttons) o.IsChecked = buttons.IndexOf(o) == k; _op.FieldChanged(); };
            buttons.Add(b);
            sp.Children.Add(b);
        }
        _syncers.Add(() => { int g = get(); for (int j = 0; j < buttons.Count; j++) buttons[j].IsChecked = j == g; });
        Row(label, sp);
    }

    /// <summary>
    /// k153: выбор одним из вариантов-иконок (типы отверстия): кнопки с иконкой, подпись — в подсказке; выбранная — нажата.
    /// Переносится по ширине панели.
    /// </summary>
    public void IconSegments(string label, (string Icon, string Tip)[] options, Func<int> get, Action<int> set, Func<bool>? visible = null)
    {
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<ToggleButton>();
        for (int i = 0; i < options.Length; i++)
        {
            int k = i;
            var b = new ToggleButton { Content = Icons.IconProvider.Content(options[i].Icon, L(options[i].Tip), 30), IsChecked = get() == i, Padding = new Thickness(3), Margin = new Thickness(0, 0, 2, 2) };
            ToolTip.SetTip(b, L(options[i].Tip));
            b.Click += (_, _) => { set(k); foreach (var o in buttons) o.IsChecked = buttons.IndexOf(o) == k; _op.FieldChanged(); };
            buttons.Add(b);
            wp.Children.Add(b);
        }
        _syncers.Add(() => { int g = get(); for (int j = 0; j < buttons.Count; j++) buttons[j].IsChecked = j == g; });
        var head = new TextBlock { Text = L(label), Margin = new Thickness(0, 4, 0, 2) };
        var sp = new StackPanel { Spacing = 0, Children = { head, wp } };
        _host.Children.Add(sp);
        When(sp, visible);
    }

    public void Finish() { _host.Children.Add(Status); Refresh(); }

    // ------------------------------------------------------------------ зависимые поля

    private readonly List<(Control C, Func<bool> Visible)> _conditional = new();
    private readonly List<(Control C, Func<bool> Enabled)> _enabled = new();
    private readonly List<(TextBox Box, Func<ValueRef> Get)> _numbers = new();
    private readonly List<Action> _syncers = new();

    /// <summary>Поле видно только при условии (например, угол — только у фаски «расстояние и угол»).</summary>
    private void When(Control c, Func<bool>? visible)
    {
        if (visible == null) return;
        _conditional.Add((c, visible));
        c.IsVisible = visible();
    }

    /// <summary>После изменения: показать нужные поля, обновить числа, изменённые извне (ручкой в виде).</summary>
    public void Refresh()
    {
        foreach (var (c, v) in _conditional) c.IsVisible = v();
        foreach (var (c, en) in _enabled) { bool e = en(); c.IsEnabled = e; c.Opacity = e ? 1 : 0.45; }
        foreach (var (tb, get) in _numbers)
            if (!tb.IsFocused) tb.Text = Show(get());
        foreach (var a in _syncers) a();
    }

    // ------------------------------------------------------------------ числа и формулы

    public static string Show(ValueRef v) => v.Formula ?? v.Value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Число («12,5» и «12.5») или формула; формула проверяется по параметрам детали и документа.</summary>
    public ValueRef? Parse(string text, ValueRef old, sbyte kind = 0)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return new ValueRef(v) { Kind = kind };
        var table = _op.Parameters;
        // k128: число без единицы в формуле — в единице детали (поле длины), с единицей — в указанной
        if (table != null && (kind != 0 ? table.TryEvaluateAs(text, ValueRef.DimOf(kind), out double r, out _) : table.TryEvaluate(text, out r, out _)))
            return new ValueRef(r) { Formula = text, Kind = kind };
        return null;
    }
}
