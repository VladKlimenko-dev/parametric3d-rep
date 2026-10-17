using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Parametric3D.App.Views;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// u13: КОМАНДЫ ДЕРЕВА ОПЕРАЦИЙ по таблице иконок: вернуть подавленную, вставить здесь (строка отката), переупорядочить,
/// перестроить, править размеры (числа операции без панели), «требуют внимания».
/// </summary>
public static class TreeCommands
{
    static List<Feature> Selected(MainWindow w) => w.SelectedItems().OfType<Feature>().ToList();

    /// <summary>Вернуть подавленные: выбранные, а без выбора — все подавленные операции детали.</summary>
    public static void Resume(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        var fs = Selected(w).Where(f => f.Suppressed).ToList();
        if (fs.Count == 0) fs = part.Features.Where(f => f.Suppressed).ToList();
        if (fs.Count == 0) { w.ShowToast(L("Подавленных операций нет"), error: false); return; }
        w.Session.Edit(L("Вернуть подавленную"), () => { foreach (var f in fs) f.Suppressed = false; part.Invalidate(); });
        w.ViewPort.MarkDirty(); w.RebuildTree();
    }

    /// <summary>
    /// «Вставить здесь»: строка отката встаёт после выбранной операции (новые операции добавляются туда, ниже — не в расчёте);
    /// повторно на той же операции или без выбора в режиме вставки — строка возвращается в конец.
    /// </summary>
    public static void InsertHere(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        var f = Selected(w).FirstOrDefault();
        int at = f == null ? -1 : part.Features.IndexOf(f) + 1;
        if (f == null || (part.InInsertMode && part.EffectiveStage == at))
        {
            if (!part.InInsertMode) { w.ShowToast(L("Выберите операцию, после которой вставлять"), error: false); return; }
            w.Session.Edit(L("Строка отката — в конец"), () => part.EndInsertMode(resume: true));
        }
        else w.Session.Edit(L("Вставить здесь"), () => { part.StageIndex = part.ClampStage(at); part.Invalidate(); });
        w.ViewPort.MarkDirty(); w.RebuildTree();
    }

    /// <summary>
    /// Переупорядочить: выбранная операция на шаг вверх (−1) или вниз (+1). Перестановка, после которой операция или
    /// следующие за ней перестают строиться (ссылки на то, что ещё не создано), отменяется с сообщением.
    /// </summary>
    public static void Reorder(MainWindow w, int dir)
    {
        var part = w.Session.Part; if (part == null) return;
        var f = Selected(w).FirstOrDefault();
        if (f == null) { w.ShowToast(L("Выберите операцию в дереве"), error: false); return; }
        int i = part.Features.IndexOf(f), j = i + dir;
        if (i < 0 || j < 0 || j >= part.Features.Count) return;
        var before = part.Features.Where(x => x.Error == null && !x.Suppressed).ToHashSet();
        w.Session.Edit(L("Переупорядочить"), () => { part.MoveFeature(f, j); });
        w.Session.Document.RebuildDirty();
        var broken = part.Features.Where(x => before.Contains(x) && x.Error != null).ToList();
        if (broken.Count > 0)
        {
            w.Session.Undo();
            w.Session.Document.RebuildDirty();
            w.ShowToast(L("Нельзя переставить: «{0}» опирается на соседнюю операцию", broken[0].Name));
        }
        w.ViewPort.MarkDirty(); w.RebuildTree();
    }

    /// <summary>Перестроить деталь целиком (сброс промежуточных состояний).</summary>
    public static void Rebuild(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        part.Invalidate();
        w.Session.Document.RebuildDirty();
        int errs = part.Features.Count(x => x.Error != null);
        w.ShowToast(errs == 0 ? L("Перестроено за {0} мс", sw.ElapsedMilliseconds) : L("Перестроено; с ошибкой: {0}", errs), error: errs > 0);
        w.ViewPort.MarkDirty(); w.RebuildTree();
    }

    /// <summary>Числовые параметры операции (поля ValueRef и ValueRef?): имя поля → получить и задать.</summary>
    public static List<(string Name, Func<ValueRef?> Get, Action<ValueRef> Set)> Values(Feature f)
    {
        var res = new List<(string, Func<ValueRef?>, Action<ValueRef>)>();
        foreach (var fi in f.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (fi.FieldType == typeof(ValueRef)) res.Add((fi.Name, () => (ValueRef)fi.GetValue(f)!, v => fi.SetValue(f, v)));
            else if (fi.FieldType == typeof(ValueRef?)) res.Add((fi.Name, () => (ValueRef?)fi.GetValue(f), v => fi.SetValue(f, (ValueRef?)v)));
        }
        return res;
    }

    static readonly Dictionary<string, string> Names = new()
    {
        ["Radius"] = "Радиус", ["RadiusEnd"] = "Радиус в конце", ["Thickness"] = "Толщина", ["Angle"] = "Угол", ["Distance"] = "Расстояние",
        ["D1"] = "Расстояние 1", ["D2"] = "Расстояние 2", ["SizeEnd"] = "Размер в конце", ["Diameter"] = "Диаметр", ["Depth"] = "Глубина",
        ["Twist"] = "Закрутка", ["LengthFraction"] = "Доля траектории", ["MaxLength"] = "Наибольшая длина", ["Offset"] = "Смещение",
        ["Count"] = "Число", ["Spacing"] = "Шаг", ["Step"] = "Шаг",
    };

    /// <summary>«Править размеры»: числа выбранной операции в окошке у указателя — без панели операции.</summary>
    public static void EditDims(MainWindow w)
    {
        var part = w.Session.Part; var f = Selected(w).FirstOrDefault();
        if (part == null || f == null) { w.ShowToast(L("Выберите операцию в дереве"), error: false); return; }
        var vals = Values(f).Where(v => v.Get() != null).ToList();
        if (vals.Count == 0) { w.ShowToast(L("У операции «{0}» нет размеров", f.Name), error: false); return; }
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(6) };
        panel.Children.Add(new TextBlock { Text = f.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var boxes = new List<(TextBox Box, Action<ValueRef> Set)>();
        foreach (var (name, get, set) in vals)
        {
            var cur = get()!.Value;
            var tb = new TextBox { Text = cur.Formula ?? cur.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), MinWidth = 120 };
            panel.Children.Add(new TextBlock { Text = L(Names.TryGetValue(name, out var ru) ? ru : name), Classes = { "dim" }, FontSize = 12 });
            panel.Children.Add(tb);
            boxes.Add((tb, set));
        }
        var ok = new Button { Content = L("ОК"), Classes = { "accent" } };
        panel.Children.Add(ok);
        var fl = new Flyout { Content = panel, Placement = PlacementMode.Pointer };
        void Apply()
        {
            var table = w.Session.Document.ScopeFor(part);
            var parsed = new List<(Action<ValueRef>, ValueRef)>();
            foreach (var (tb, set) in boxes)
            {
                string t = (tb.Text ?? "").Trim();
                if (double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) parsed.Add((set, new ValueRef(v)));
                else if (table.TryEvaluate(t, out v, out _)) parsed.Add((set, new ValueRef(v) { Formula = t }));
                else { w.ShowToast(L("Не число и не формула: {0}", t)); return; }
            }
            w.Session.Edit(L("Править размеры «{0}»", f.Name), () => { foreach (var (set, v) in parsed) set(v); part.NotifyChanged(f); });
            w.ViewPort.MarkDirty();
            fl.Hide();
        }
        ok.Click += (_, _) => Apply();
        foreach (var (tb, _) in boxes) tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } else if (e.Key == Key.Escape) { fl.Hide(); e.Handled = true; } };
        fl.ShowAt(w.ViewPort, showAtPointer: true);
        Dispatcher.UIThread.Post(() => { boxes[0].Box.Focus(); boxes[0].Box.SelectAll(); }, DispatcherPriority.Input);
    }

    /// <summary>«Требуют внимания»: операции с ошибкой, подавленные и ниже строки отката — список; щелчок выбирает операцию.</summary>
    public static List<(Feature F, string Why)> NeedsAttention(Part part)
    {
        var res = new List<(Feature, string)>();
        foreach (var f in part.Features)
        {
            if (f.Error != null) res.Add((f, f.Error));
            else if (part.IsBeyondStage(f)) res.Add((f, L("ниже строки отката")));
        }
        return res;
    }

    public static async void ShowNeedsAttention(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        var list = NeedsAttention(part);
        if (list.Count == 0) { w.ShowToast(L("Все операции построены"), error: false); return; }
        var lb = new ListBox { MaxHeight = 400, MinWidth = 420 };
        foreach (var (f, why) in list) lb.Items.Add(new ListBoxItem { Content = $"{f.Name} — {why}", Tag = f });
        var win = new Window { Title = L("Требуют внимания"), SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        lb.SelectionChanged += (_, _) => { if (lb.SelectedItem is ListBoxItem { Tag: Feature f }) { w.SelectItem(f); } };
        win.Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { new TextBlock { Text = L("Операции с ошибкой и ниже строки отката:") }, lb } };
        await win.ShowDialog(w);
    }
}
