using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.Services;
using Parametric3D.Model;
using Parametric3D.Model.Params;
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
    }

    private sealed class Level
    {
        public required string Title;
        public required ParameterTable Table;
        public ModelItem? Owner;     // null — документ
        public int Depth;
        public List<Row>? Rows;      // правка (null — уровень не открывали)
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
                else if (c is Part p) _levels.Add(new Level { Title = L("Деталь «{0}»", p.Name), Table = p.LocalParameters, Owner = p, Depth = depth });
            }
        }
        Walk(doc.Root, 1);

        _lookIn.ItemsSource = _levels.Select(l => new string(' ', l.Depth * 4) + (l.Depth > 0 ? "└ " : "") + l.Title).ToList();
        _lookIn.SelectionChanged += (_, _) => { if (_lookIn.SelectedIndex >= 0) Show(_levels[_lookIn.SelectedIndex]); };

        var add = new Button { Content = "+ " + L("Добавить") };
        var del = new Button { Content = "− " + L("Удалить") };
        add.Click += (_, _) => AddRow();
        del.Click += (_, _) => DeleteRow();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L("Искать в:"), VerticalAlignment = VerticalAlignment.Center }, _lookIn, add, del } };

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

    private void Show(Level l)
    {
        _cur = l;
        l.Rows ??= Load(l.Table);
        _selRow = -1;
        _scopeNote.Text = l.Owner switch
        {
            null => L("Параметры документа видны во всех сборках и деталях."),
            Assembly => L("Параметры сборки видны в её деталях; имя, которого здесь нет, ищется в документе."),
            _ => L("Параметры детали видны только в ней; имя, которого здесь нет, ищется в сборке и документе."),
        };
        Recalc();
        Fill();
    }

    private void Fill()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
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
            Box(r.Name, 0, v => r.Name = v.Trim());
            var type = new ComboBox { ItemsSource = Types.Select(t => L(t)).ToList(), SelectedIndex = r.Type, Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch };
            type.SelectionChanged += (_, _) => { _selRow = ri; r.Type = Math.Max(0, type.SelectedIndex); Recalc(); UpdateResults(); };
            Grid.SetRow(type, gr); Grid.SetColumn(type, 1);
            _grid.Children.Add(type);
            Box(r.Value, 2, v => r.Value = v.Trim());
            Box(r.Unit, 3, v => r.Unit = v.Trim());
            var res = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Tag = ri, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(res, gr); Grid.SetColumn(res, 4);
            _grid.Children.Add(res);
            Box(r.Comment, 5, v => r.Comment = v);
        }
        UpdateResults();
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
    }

    private void AddRow()
    {
        var rows = _cur.Rows!;
        string n = "d";
        for (int i = rows.Count + 1; ; i++) if (rows.All(r => !string.Equals(r.Name, "d" + i, StringComparison.OrdinalIgnoreCase))) { n = "d" + i; break; }
        rows.Add(new Row { Name = n, Value = "0", Unit = "mm" });
        Recalc();
        Fill();
    }

    private void DeleteRow()
    {
        var rows = _cur.Rows!;
        int i = _selRow >= 0 && _selRow < rows.Count ? _selRow : rows.Count - 1;
        if (i < 0) return;
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
        var scratch = new Dictionary<Level, ParameterTable>();
        ParameterTable Scratch(Level l)
        {
            if (scratch.TryGetValue(l, out var t)) return t;
            t = new ParameterTable { Units = _s.Document.Parameters.Units, Geometry = _s.Document.Parameters.Geometry };
            scratch[l] = t;
            t.Parent = ParentLevel(l) is { } pl ? Scratch(pl) : null;
            Write(l.Rows ?? Load(l.Table), t, out _);
            return t;
        }
        var cur = Scratch(_cur);
        foreach (var r in _cur.Rows!)
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
        foreach (var l in _levels.Where(l => l.Rows != null))
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
            foreach (var l in _levels.Where(l => l.Rows != null).OrderBy(l => l.Owner == null ? 0 : 1))
            {
                if (l.Owner != null) l.Table.Parent = ParentLevel(l)?.Table;
                Write(l.Rows!, l.Table, out _);
            }
            foreach (var p in _s.Document.AllParts()) p.ParametersChanged();
            _s.Document.Root.NotifyChanged(_s.Document.Root);
        });
        return true;
    }
}
