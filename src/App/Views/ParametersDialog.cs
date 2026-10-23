using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.Services;
using Parametric3D.Model;
using Parametric3D.Model.Params;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Avalonia.Controls.Primitives;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// ПАРАМЕТРЫ (как «Parameters» в PTC Creo): таблица имя — тип — значение или формула — единица — результат — описание
/// по уровням «Искать в»: документ, сборки, детали (раскрывающийся список-дерево). Формула детали видит параметры
/// своего уровня, затем сборки над ней и документа; своё имя закрывает такое же выше. Результат пересчитывается при
/// правке; «ОК» записывает все уровни одним шагом отмены и перестраивает модель.
/// </summary>
public sealed class ParametersDialog : Window
{
    private sealed class Row
    {
        public string Name = "", Value = "", Unit = "", Comment = "";
        public int Type;          // 0 число или формула, 1 целое, 2 логическое, 3 строка
        public string Result = "", Error = "";
        // k102: строка размера (эскиза или операции): имя — путь, правится только значение/формула
        public SkDimension? Dim; public Sketch? Sketch;
        public Feature? Feature; public FeatureDims.Slot? Slot;
        public string Original = "";
        public bool IsDim => Dim != null || Slot != null;
    }

    private sealed class Level
    {
        public required string Title;
        public required ParameterTable Table;
        public ModelItem? Owner;     // null — документ
        public int Depth;
        public List<Row>? Rows;      // правка (null — уровень не открывали)
        /// <summary>k102: уровень «размеры детали» (эскизы и операции).</summary>
        public bool Dims;
    }

    private static readonly string[] Types = { "Число / формула", "Целое", "Логическое", "Строка" };

    private readonly DocumentSession _s;
    private readonly List<Level> _levels = new();
    private Level _cur = null!;
    private readonly ComboBox _lookIn = new() { MinWidth = 320 };
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("150,150,190,70,120,*") };
    private readonly TextBlock _scopeNote = new() { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private int _selRow = -1;
    public bool Applied { get; private set; }

    public ParametersDialog(DocumentSession s, ModelItem? start)
    {
        _s = s;
        Title = L("Параметры");
        Width = 900; Height = 560; MinWidth = 640; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("P3.PanelBg"));

        var doc = s.Document;
        _levels.Add(new Level { Title = L("Документ «{0}»", s.Title), Table = doc.Parameters });
        void Walk(ModelItem item, int depth)
        {
            foreach (var c in item.Children)
            {
                if (c is Assembly a) { _levels.Add(new Level { Title = L("Сборка «{0}»", a.Name), Table = a.LocalParameters, Owner = a, Depth = depth }); Walk(a, depth + 1); }
                else if (c is Part p)
                {
                    _levels.Add(new Level { Title = L("Деталь «{0}»", p.Name), Table = p.LocalParameters, Owner = p, Depth = depth });
                    _levels.Add(new Level { Title = L("Размеры детали «{0}»", p.Name), Table = p.LocalParameters, Owner = p, Depth = depth + 1, Dims = true });
                }
            }
        }
        Walk(doc.Root, 1);

        _lookIn.ItemsSource = _levels.Select(l => new string(' ', l.Depth * 4) + (l.Depth > 0 ? "└ " : "") + l.Title).ToList();
        _lookIn.SelectionChanged += (_, _) => { if (_lookIn.SelectedIndex >= 0) Show(_levels[_lookIn.SelectedIndex]); };

        var add = new Button { Content = "+ " + L("Добавить") };
        var del = new Button { Content = "− " + L("Удалить") };
        add.Click += (_, _) => AddRow();
        del.Click += (_, _) => DeleteRow();

        var top0 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L("Искать в:"), VerticalAlignment = VerticalAlignment.Center }, _lookIn, add, del } };
        // k102 (замечание 9): формулы — категории и кнопки функций; кнопка вставляет текст в место курсора поля значения
        BuildFormulaBars();
        var top = new StackPanel { Spacing = 4, Children = { top0, _catBar, _funcBar } };

        var ok = new Button { Content = L("ОК"), MinWidth = 96, Classes = { "accent" } };
        var cancel = new Button { Content = L("Отмена"), MinWidth = 96 };
        ok.Click += (_, _) => { if (ApplyAll()) { Applied = true; Close(); } };
        cancel.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_scopeNote, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        _scopeNote.Margin = new Thickness(0, 6, 0, 8);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(top);
        root.Children.Add(_scopeNote);
        root.Children.Add(buttons);
        root.Children.Add(new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4),
            Child = new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
        });
        ((Border)root.Children[^1]).Bind(Border.BorderBrushProperty, this.GetResourceObservable("P3.Border"));
        Content = root;

        int startIdx = Math.Max(0, _levels.FindIndex(l => ReferenceEquals(l.Owner, start)));
        _lookIn.SelectedIndex = startIdx;
    }

    // ------------------------------------------------------------------ формулы (k102)

    /// <summary>Категории формул: название и кнопки (подпись, вставляемый текст; «|» — место курсора после вставки).</summary>
    public static readonly (string Category, (string Label, string Insert, string Tip)[] Items)[] FormulaCategories =
    {
        ("Тригонометрия", new[]
        {
            ("sin", "sin(|)", "Синус угла в градусах"), ("cos", "cos(|)", "Косинус угла в градусах"), ("tan", "tan(|)", "Тангенс угла в градусах"),
            ("asin", "asin(|)", "Арксинус, градусы"), ("acos", "acos(|)", "Арккосинус, градусы"), ("atan", "atan(|)", "Арктангенс, градусы"),
            ("atan2", "atan2(|, )", "Угол точки (y, x), градусы"), ("sinh", "sinh(|)", "Гиперболический синус"), ("cosh", "cosh(|)", "Гиперболический косинус"),
            ("tanh", "tanh(|)", "Гиперболический тангенс"),
        }),
        ("Логика и условия", new[]
        {
            ("if", "if(|, , )", "if(условие, если да, если нет)"), ("<", " < ", "Меньше"), (">", " > ", "Больше"), ("<=", " <= ", "Не больше"),
            (">=", " >= ", "Не меньше"), ("==", " == ", "Равно"), ("!=", " != ", "Не равно"), ("&&", " && ", "И"), ("||", " || ", "Или"),
            ("not", "not(|)", "Не"), ("sign", "sign(|)", "Знак: −1, 0, 1"), ("mod", "mod(|, )", "Остаток от деления"),
            ("min", "min(|, )", "Наименьшее"), ("max", "max(|, )", "Наибольшее"), ("bound", "bound(|, , )", "Ограничить: bound(x, от, до)"),
        }),
        ("Математика", new[]
        {
            ("+", " + ", "Сложение"), ("−", " - ", "Вычитание"), ("×", " * ", "Умножение"), ("÷", " / ", "Деление"), ("^", "^", "Степень"),
            ("( )", "(|)", "Скобки"), ("sqrt", "sqrt(|)", "Квадратный корень"), ("pow", "pow(|, )", "Степень pow(a, b)"), ("abs", "abs(|)", "Модуль"),
            ("log", "log(|)", "Десятичный логарифм"), ("ln", "ln(|)", "Натуральный логарифм"), ("exp", "exp(|)", "Экспонента"),
            ("ceil", "ceil(|)", "Вверх до целого"), ("floor", "floor(|)", "Вниз до целого"), ("round", "round(|)", "Округлить"), ("trunc", "trunc(|)", "Отбросить дробную часть"),
            ("pi", "pi", "Число π"), ("e", "e()", "Число e"),
        }),
        ("Размеры и ссылки", new[]
        {
            ("d0", "d|0", "Размер этого эскиза (в формуле размера эскиза)"), ("s\"…\".d0", "s\"|Эскиз1\".d0", "Размер эскиза детали"),
            ("f\"…\".d0", "f\"|Выдавливание\".d0", "Размер операции детали"), ("p\"…\".x", "p\"|Деталь1\".x", "Параметр другой детали"),
            ("p\"…\".s\"…\".d0", "p\"|Деталь1\".s\"Эскиз1\".d0", "Размер эскиза другой детали"),
            ("a\"…\".p\"…\"…", "a\"|Сборка\".p\"Деталь1\".s\"Эскиз1\".d0", "Размер в детали сборки"),
            ("Length", "Length(|)", "Длина ребра/кривой по ссылке"), ("Area", "Area(|)", "Площадь грани"), ("Volume", "Volume(|)", "Объём детали"),
        }),
    };

    private readonly StackPanel _catBar = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly WrapPanel _funcBar = new() { Orientation = Orientation.Horizontal };
    private TextBox? _lastValueBox;
    private int _cat;

    private void BuildFormulaBars()
    {
        _catBar.Children.Clear();
        for (int i = 0; i < FormulaCategories.Length; i++)
        {
            int k = i;
            var b = new ToggleButton { Content = L(FormulaCategories[i].Category), IsChecked = i == _cat, Padding = new Thickness(10, 3) };
            b.Click += (_, _) => { _cat = k; BuildFormulaBars(); };
            _catBar.Children.Add(b);
        }
        _funcBar.Children.Clear();
        foreach (var (label, insert, tip) in FormulaCategories[_cat].Items)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 4), MinWidth = 34, Focusable = false };
            ToolTip.SetTip(b, L(tip) + "\n" + insert.Replace("|", ""));
            string ins = insert;
            b.Click += (_, _) => InsertFormula(ins);
            _funcBar.Children.Add(b);
        }
    }

    /// <summary>Вставить текст формулы в место курсора поля значения (последнего, где был курсор); «|» — курсор после вставки.</summary>
    public void InsertFormula(string insert)
    {
        if (_lastValueBox is not { } tb) return;
        int caretAt = insert.IndexOf('|'); string text = insert.Replace("|", "");
        string cur = tb.Text ?? "";
        int a = Math.Clamp(Math.Min(tb.SelectionStart, tb.SelectionEnd), 0, cur.Length), z = Math.Clamp(Math.Max(tb.SelectionStart, tb.SelectionEnd), 0, cur.Length);
        if (a == z) a = z = Math.Clamp(tb.CaretIndex, 0, cur.Length);
        tb.Text = cur[..a] + text + cur[z..];
        int caret = a + (caretAt >= 0 ? caretAt : text.Length);
        tb.SelectionStart = tb.SelectionEnd = tb.CaretIndex = caret;
        tb.Focus();
    }

    /// <summary>Разбор (проверки): поле значения строки i текущего уровня — поставить в него курсор.</summary>
    public TextBox? ValueBox(int row) => _assists.TryGetValue(row, out var a) ? a.Box : null;
    /// <summary>Разбор (k103): подсказки поля значения строки i.</summary>
    public FormulaAssist? Assist(int row) => _assists.TryGetValue(row, out var a) ? a : null;
    private readonly Dictionary<int, FormulaAssist> _assists = new();
    /// <summary>Разбор: значок ошибки строки i виден.</summary>
    public bool ErrorShown(int row) => _grid.Children.OfType<DockPanel>().Where(d => d.Tag is int r && r == row).SelectMany(d => d.Children.OfType<Control>()).Any(c => c.Name == "err" && c.IsVisible);
    public void SelectLevel(int i) => _lookIn.SelectedIndex = i;
    public int LevelCount => _levels.Count;
    public string LevelTitle(int i) => _levels[i].Title;
    public int RowCount => _cur.Rows?.Count ?? 0;
    public string RowName(int i) => _cur.Rows![i].Name;
    public string RowResult(int i) => _cur.Rows![i].Result;
    public string RowError(int i) => _cur.Rows![i].Error;
    public void SetRowValue(int i, string v) { _cur.Rows![i].Value = v; Recalc(); UpdateResults(); }
    public bool Apply() { if (ApplyAll()) { Applied = true; return true; } return false; }

    // ------------------------------------------------------------------ уровни и строки

    private static List<Row> Load(ParameterTable t) => t.All.Select(p => new Row
    {
        Name = p.Name,
        Type = p.Kind switch { ParamKind.Integer => 1, ParamKind.Boolean => 2, ParamKind.String => 3, _ => 0 },
        Value = p.Kind switch
        {
            ParamKind.Formula => p.Formula,
            ParamKind.Boolean => p.Flag ? "1" : "0",
            ParamKind.String => p.Text,
            _ => p.Number.ToString("0.######", CultureInfo.InvariantCulture),
        },
        Unit = p.Unit, Comment = p.Comment,
    }).ToList();

    /// <summary>k102: строки размеров детали: размеры эскизов (s"Эскиз".dN) и числовые поля операций (f"Операция".dN).</summary>
    private static List<Row> LoadDims(Part part)
    {
        var rows = new List<Row>();
        static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        foreach (var sk in part.Sketches)
        {
            sk.EnsureDimIds();
            foreach (var d in sk.Dimensions.OrderBy(x => x.Index))
            {
                string val = d.Formula ?? Num(d.Value);
                rows.Add(new Row { Name = ModelRefs.PathOf(sk, d), Value = val, Original = val, Unit = d.IsAngular ? "°" : "mm", Dim = d, Sketch = sk,
                    Comment = MainWindow.DimensionName(d) + (d.IsDefault ? " · " + L("слабый") : "") });
            }
        }
        foreach (var f in part.Features)
        {
            var slots = FeatureDims.Of(f);
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Get() is not { } v) continue;
                string val = v.Formula ?? Num(v.Value);
                rows.Add(new Row { Name = ModelRefs.PathOf(f, i), Value = val, Original = val, Feature = f, Slot = slots[i], Comment = slots[i].Name });
            }
        }
        return rows;
    }

    private void Show(Level l)
    {
        _cur = l;
        // k103 (замечание 1): у детали в своих параметрах — и размеры с формулами (эскизов и операций)
        l.Rows ??= l.Dims && l.Owner is Part dp ? LoadDims(dp)
            : l.Owner is Part fp && !FormulaRowsOff ? Load(l.Table).Concat(LoadDims(fp).Where(r => !TryNum(r.Value, out _))).ToList()
            : Load(l.Table);
        _selRow = -1;
        _scopeNote.Text = l.Dims ? L("Размеры эскизов и операций детали. Значение — число или формула: d0 — размер того же эскиза, s\"Эскиз\".d0, f\"Операция\".d1, p\"Деталь\".s\"Эскиз\".d0, a\"Сборка\".p\"Деталь\"… — другие. Формула делает размер сильным.")
            : l.Owner switch
        {
            null => L("Параметры документа видны во всех сборках и деталях."),
            Assembly => L("Параметры сборки видны в её деталях; имя, которого здесь нет, ищется в документе."),
            _ => L("Параметры детали видны только в ней; имя, которого здесь нет, ищется в сборке и документе."),
        };
        Recalc();
        Fill();
    }

    static readonly bool FormulaRowsOff = Environment.GetEnvironmentVariable("P3D_PARAMDIMROWS") == "0";

    private void Fill()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _assists.Clear();
        string[] heads = { "Имя", "Тип", "Значение или формула", "Единица", "Результат", "Описание" };
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < heads.Length; c++)
        {
            var h = new TextBlock { Text = L(heads[c]), Classes = { "header" }, Margin = new Thickness(4, 2, 4, 6) };
            Grid.SetColumn(h, c);
            _grid.Children.Add(h);
        }
        var rows = _cur.Rows!;
        for (int i = 0; i < rows.Count; i++)
        {
            int ri = i;
            var r = rows[i];
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            int gr = i + 1;
            TextBox Box(string text, int col, Action<string> set)
            {
                var tb = new TextBox { Text = text, Margin = new Thickness(2), MinHeight = 26 };
                tb.GotFocus += (_, _) => _selRow = ri;
                tb.LostFocus += (_, _) => { set(tb.Text ?? ""); Recalc(); UpdateResults(); };
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { set(tb.Text ?? ""); Recalc(); UpdateResults(); e.Handled = true; } };
                Grid.SetRow(tb, gr); Grid.SetColumn(tb, col);
                _grid.Children.Add(tb);
                return tb;
            }
            if (r.IsDim)
            {
                // k102: имя размера — путь (не правится), тип — что за размер
                var nm = new TextBlock { Text = r.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), FontFamily = new FontFamily("Consolas, Menlo, monospace") };
                Grid.SetRow(nm, gr); Grid.SetColumn(nm, 0); _grid.Children.Add(nm);
                var ty = new TextBlock { Text = r.Comment, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetRow(ty, gr); Grid.SetColumn(ty, 1); _grid.Children.Add(ty);
            }
            else
            {
                Box(r.Name, 0, v => r.Name = v.Trim());
                var type = new ComboBox { ItemsSource = Types.Select(t => L(t)).ToList(), SelectedIndex = r.Type, Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch };
                type.SelectionChanged += (_, _) => { _selRow = ri; r.Type = Math.Max(0, type.SelectedIndex); Recalc(); UpdateResults(); };
                Grid.SetRow(type, gr); Grid.SetColumn(type, 1);
                _grid.Children.Add(type);
            }
            // значение или формула — с красным значком ошибки справа (k102, замечание 9)
            {
                var tb = new TextBox { Text = r.Value, Margin = new Thickness(2), MinHeight = 26 };
                tb.GotFocus += (_, _) => { _selRow = ri; _lastValueBox = tb; };
                tb.LostFocus += (_, _) => { r.Value = (tb.Text ?? "").Trim(); Recalc(); UpdateResults(); };
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { r.Value = (tb.Text ?? "").Trim(); Recalc(); UpdateResults(); e.Handled = true; } };
                var icon = ErrorIcon();
                var dock = new DockPanel { Tag = ri };
                DockPanel.SetDock(icon, Dock.Right);
                // k103: подсказка после точки, имена в кавычках — синим (есть) / красным (нет)
                var assist = new FormulaAssist(tb, () => _cur.Owner ?? _s.Document.Root, () => r.Sketch);
                _assists[ri] = assist;
                dock.Children.Add(icon); dock.Children.Add(assist.View);
                Grid.SetRow(dock, gr); Grid.SetColumn(dock, 2);
                _grid.Children.Add(dock);
            }
            if (r.IsDim)
            {
                var un = new TextBlock { Text = r.Unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Classes = { "dim" } };
                Grid.SetRow(un, gr); Grid.SetColumn(un, 3); _grid.Children.Add(un);
            }
            else Box(r.Unit, 3, v => r.Unit = v.Trim());
            var res = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Tag = ri, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(res, gr); Grid.SetColumn(res, 4);
            _grid.Children.Add(res);
            if (!r.IsDim) Box(r.Comment, 5, v => r.Comment = v);
        }
        UpdateResults();
    }

    /// <summary>Красный треугольник с «!» — формула не вычисляется (подсказка — причина).</summary>
    private static Control ErrorIcon()
    {
        var tri = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M9,1 L17,16 L1,16 Z"), Fill = new SolidColorBrush(Color.Parse("#E5484D")), Stretch = Stretch.None };
        var bang = new TextBlock { Text = "!", Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
        return new Panel { Name = "err", Width = 18, Height = 17, Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Children = { tri, bang } };
    }

    private void UpdateResults()
    {
        foreach (var tb in _grid.Children.OfType<TextBlock>())
        {
            if (tb.Tag is not int i || i >= _cur.Rows!.Count) continue;
            var r = _cur.Rows[i];
            tb.Text = r.Error.Length > 0 ? r.Error : r.Result;
            tb.Classes.Set("err", r.Error.Length > 0);
            ToolTip.SetTip(tb, tb.Text);
        }
        foreach (var dock in _grid.Children.OfType<DockPanel>())
        {
            if (dock.Tag is not int i || i >= _cur.Rows!.Count) continue;
            var r = _cur.Rows[i];
            foreach (var c in dock.Children.OfType<Control>().Where(c => c.Name == "err")) { c.IsVisible = r.Error.Length > 0; ToolTip.SetTip(c, r.Error); }
        }
    }

    private void AddRow()
    {
        if (_cur.Dims) return;
        var rows = _cur.Rows!;
        string n = "d";
        for (int i = rows.Count + 1; ; i++) if (rows.All(r => !string.Equals(r.Name, "d" + i, StringComparison.OrdinalIgnoreCase))) { n = "d" + i; break; }
        int at = rows.FindIndex(r => r.IsDim);
        rows.Insert(at < 0 ? rows.Count : at, new Row { Name = n, Value = "0", Unit = "mm" });
        Recalc();
        Fill();
    }

    private void DeleteRow()
    {
        if (_cur.Dims) return;
        var rows = _cur.Rows!;
        int i = _selRow >= 0 && _selRow < rows.Count ? _selRow : rows.FindLastIndex(r => !r.IsDim);
        if (i < 0 || rows[i].IsDim) return;
        rows.RemoveAt(i);
        _selRow = -1;
        Recalc();
        Fill();
    }

    // ------------------------------------------------------------------ расчёт и запись

    /// <summary>Таблица из строк уровня (для пересчёта результата и для записи).</summary>
    private static void Write(List<Row> rows, ParameterTable t, out List<string> problems)
    {
        problems = new();
        foreach (var p in t.All.ToList()) t.Remove(p.Name);
        foreach (var r in rows)
        {
            if (r.IsDim) continue;
            if (string.IsNullOrWhiteSpace(r.Name)) { problems.Add(L("Пустое имя параметра")); continue; }
            if (t.Find(r.Name) != null) { problems.Add(L("Имя «{0}» повторяется", r.Name)); continue; }
            Parameter p;
            switch (r.Type)
            {
                case 1:
                case 0 when TryNum(r.Value, out _):
                    TryNum(r.Value, out double v);
                    p = t.Add(r.Name, v, r.Type == 1 ? ParamKind.Integer : ParamKind.Real, r.Unit);
                    break;
                case 2:
                    p = t.Add(r.Name, 0, ParamKind.Boolean, r.Unit);
                    p.Flag = r.Value.Trim().ToLowerInvariant() is "1" or "true" or "да" or "yes";
                    break;
                case 3:
                    p = t.Add(r.Name, 0, ParamKind.String, "");
                    p.Text = r.Value;
                    break;
                default:
                    p = t.AddFormula(r.Name, r.Value, r.Unit);
                    break;
            }
            if (p.Name != r.Name) t.Rename(p, r.Name);
            p.Comment = r.Comment;
        }
        t.Recalculate();
    }

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Пересчитать результат текущего уровня на копии (с уровнями выше — уже исправленными).</summary>
    private void Recalc()
    {
        if (_cur.Owner is Part dpart && _cur.Rows!.Any(r => r.IsDim))
        {
            var scope = _s.Document.ScopeFor(dpart);
            foreach (var r in _cur.Rows!.Where(r => r.IsDim))
            {
                r.Error = ""; r.Result = "";
                string v = r.Value.Trim();
                double x; string? why = null;
                bool ok = TryNum(v, out x)
                    || (r.Sketch != null ? r.Sketch.TryEvaluate(v, scope, out x, out why) : ParameterTable.TryEvaluateWith(v, scope, scope.Geometry, out x, out why));
                if (ok && r.Dim != null && System.Text.RegularExpressions.Regex.IsMatch(v, $@"(?<![\w""])d{r.Dim.Index}(?!\w)") && !TryNum(v, out _)) { ok = false; why = L("Формула ссылается на сам размер"); }
                if (ok) r.Result = x.ToString("0.######", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(r.Unit) ? "" : " " + r.Unit);
                else r.Error = why ?? L("Не число и не формула");
            }
            if (_cur.Dims) return;
        }
        var scratch = new Dictionary<Level, ParameterTable>();
        ParameterTable Scratch(Level l)
        {
            if (scratch.TryGetValue(l, out var t)) return t;
            t = new ParameterTable { Units = _s.Document.Parameters.Units, Geometry = _s.Document.Parameters.Geometry, External = l.Table.External };
            scratch[l] = t;
            t.Parent = ParentLevel(l) is { } pl ? Scratch(pl) : null;
            Write(l.Rows ?? Load(l.Table), t, out _);
            return t;
        }
        var cur = Scratch(_cur);
        foreach (var r in _cur.Rows!.Where(r => !r.IsDim))
        {
            var p = cur.Find(r.Name);
            r.Error = p?.Error ?? (p == null && !string.IsNullOrWhiteSpace(r.Name) ? L("имя повторяется") : "");
            r.Result = p == null ? "" : p.Kind switch
            {
                ParamKind.String => p.Text,
                ParamKind.Boolean => p.Flag ? L("да") : L("нет"),
                _ => p.Value.ToString("0.######", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(p.Unit) ? "" : " " + p.Unit),
            };
        }
    }

    private Level? ParentLevel(Level l)
    {
        if (l.Owner == null) return null;
        for (var a = l.Owner.Parent; a != null; a = a.Parent)
        {
            var lv = _levels.FirstOrDefault(x => ReferenceEquals(x.Owner, a));
            if (lv != null) return lv;
        }
        return _levels[0];
    }

    private bool ApplyAll()
    {
        var problems = new List<string>();
        foreach (var l in _levels.Where(l => l.Rows != null && !l.Dims))
        {
            var test = new ParameterTable();
            Write(l.Rows!, test, out var pr);
            problems.AddRange(pr.Select(x => l.Title + ": " + x));
        }
        if (problems.Count > 0)
        {
            _ = Dialogs.Info(this, L("Параметры"), string.Join("\n", problems.Distinct().Take(10)));
            return false;
        }
        _s.Edit(L("Параметры"), () =>
        {
            // сначала верхние уровни: нижние вычисляются по ним
            foreach (var l in _levels.Where(l => l.Rows != null && !l.Dims).OrderBy(l => l.Owner == null ? 0 : 1))
            {
                if (l.Owner != null) l.Table.Parent = ParentLevel(l)?.Table;
                Write(l.Rows!, l.Table, out _);
            }
            // k102: размеры — значение или формула (формула и новое значение делают размер эскиза сильным); с ошибкой — тоже
            // записывается: деталь перестраивается до операции, которая её использует, та — с ошибкой
            foreach (var l in _levels.Where(l => l.Rows != null && l.Owner is Part && l.Rows.Any(r => r.IsDim)))
            {
                var part = (Part)l.Owner!;
                foreach (var r in l.Rows!.Where(r => r.Value.Trim() != r.Original))
                {
                    string v = r.Value.Trim();
                    bool num = TryNum(v, out double x);
                    if (r.Dim != null)
                    {
                        if (num) { r.Dim.Value = r.Dim.Type is DimensionType.CoordX or DimensionType.CoordY && r.Dim.Value < 0 ? -Math.Abs(x) : x; r.Dim.Formula = null; }
                        else { r.Dim.Formula = v; if (r.Sketch!.TryEvaluate(v, _s.Document.ScopeFor(part), out double fx, out _)) r.Dim.Value = fx; }
                        r.Dim.Locked = true; r.Dim.IsDefault = false; r.Dim.Reference = false;
                        r.Sketch!.BumpStructure(); r.Sketch.NotifyChanged(r.Sketch);
                    }
                    else if (r.Slot != null)
                    {
                        var cur = r.Slot.Get() ?? new ValueRef(0);
                        r.Slot.Set(num ? new ValueRef(x) : new ValueRef(cur.Value) { Formula = v });
                        r.Feature!.NotifyChanged(r.Feature);
                    }
                }
                part.Invalidate();
            }
            foreach (var p in _s.Document.AllParts()) p.ParametersChanged();
            _s.Document.Root.NotifyChanged(_s.Document.Root);
        });
        return true;
    }
}
