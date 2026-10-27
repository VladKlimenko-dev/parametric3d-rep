using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Parametric3D.App.Views;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
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

    // ---------------------------------------------------------------- u13e: группа, родители и потомки, замена ссылок

    /// <summary>Группа: выбранные операции — в папку (имя спрашивается); все уже в одной группе — разгруппировать.</summary>
    public static async void Group(MainWindow w)
    {
        var part = w.Session.Part; var fs = Selected(w);
        if (part == null || fs.Count == 0) { w.ShowToast(L("Выберите операции в дереве"), error: false); return; }
        if (fs.All(f => !string.IsNullOrEmpty(f.Group) && f.Group == fs[0].Group))
        {
            w.Session.Edit(L("Разгруппировать"), () => { foreach (var f in fs) f.Group = null; });
            w.RebuildTree(); return;
        }
        var name = await Dialogs.Prompt(w, L("Группа"), L("Имя группы:"), L("Группа {0}", part.Features.Select(f => f.Group).Where(g => g != null).Distinct().Count() + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        await SetGroupAsync(w, fs, name.Trim());
    }

    /// <summary>Досчитать все детали документа (фоновый расчёт, если он идёт, — дождаться и досчитать за ним).</summary>
    static async Task BuildAll(Document doc)
    {
        for (int i = 0; i < 20 && (doc.IsBuilding || doc.AllParts().Any(p => p.IsDirty)); i++) await doc.RebuildDirtyAsync();
    }

    public static async Task SetGroupAsync(MainWindow w, IEnumerable<Feature> fs, string? name)
    {
        var list = fs.ToList();
        var part = w.Session.Part;
        // k121 (замечание 27.10, 17:27, п. 3): группа — НАСТОЯЩАЯ: её операции становятся подряд в истории построения
        // (а не только показываются вместе в дереве), если перестановка допустима; иначе группа не создаётся
        if (name != null && part != null && !GroupReorderOff && list.Count > 1)
        {
            var order = GatherOrder(part, list, out var why);
            if (order == null) { w.ShowToast(L("Нельзя собрать группу: ") + why); return; }
            bool moves = !order.SequenceEqual(part.Features);
            if (moves)
            {
                await BuildAll(w.Session.Document);
                var shape0 = Shape(part);
                var good = part.Features.Where(x => x.Error == null && !x.Suppressed).ToHashSet();
                w.Session.Edit(L("Группа"), () =>
                {
                    for (int i = 0; i < order.Count; i++) if (!ReferenceEquals(part.Features[i], order[i])) part.MoveFeature(order[i], i);
                    foreach (var f in list) f.Group = name;
                });
                await BuildAll(w.Session.Document);
                var broken = part.Features.FirstOrDefault(x => good.Contains(x) && x.Error != null);
                string? fail = broken != null ? L("«{0}» перестаёт строиться", broken.Name)
                             : !SameShape(shape0, Shape(part)) ? L("перестановка операций меняет форму детали") : null;
                if (fail != null)
                {
                    w.Session.Undo();
                    await BuildAll(w.Session.Document);
                    w.ShowToast(L("Нельзя собрать группу: ") + fail);
                    w.ViewPort.MarkDirty(); w.RebuildTree();
                    return;
                }
                LastGroupMoved = true;
                w.ViewPort.MarkDirty(); w.RebuildTree();
                return;
            }
        }
        LastGroupMoved = false;
        w.Session.Edit(L("Группа"), () => { foreach (var f in list) f.Group = name; });
        w.RebuildTree();
    }

    public static readonly bool GroupReorderOff = Environment.GetEnvironmentVariable("P3D_GROUPREORDER") == "0";
    /// <summary>Разбор: последняя группа переставляла операции.</summary>
    [ThreadStatic] public static bool LastGroupMoved;

    /// <summary>
    /// k121: ПОРЯДОК, В КОТОРОМ ОПЕРАЦИИ ГРУППЫ ИДУТ ПОДРЯД. Между первой и последней операцией группы прочие операции
    /// расходятся: те, что опираются (прямо или через другие такие же) на операции группы, — после группы, остальные — перед
    /// ней; порядок внутри группы и внутри «до» и «после» прежний. Нельзя, если операция группы опирается на операцию,
    /// которой приходится уйти после группы (null и причина).
    /// </summary>
    public static List<Feature>? GatherOrder(Part part, IReadOnlyCollection<Feature> members, out string? why)
    {
        why = null;
        var set = members.ToHashSet();
        var feats = part.Features.ToList();
        var idx = members.Select(f => feats.IndexOf(f)).Where(i => i >= 0).OrderBy(i => i).ToList();
        if (idx.Count < 2) return feats;
        int first = idx[0], last = idx[^1];
        var parents = new Dictionary<Feature, HashSet<ModelItem>>();
        HashSet<ModelItem> Par(Feature f) => parents.TryGetValue(f, out var p) ? p : parents[f] = Parents(part, f).ToHashSet();
        var after = new HashSet<Feature>();
        for (int k = first + 1; k < last; k++)
        {
            var n = feats[k];
            if (set.Contains(n)) continue;
            var pn = Par(n);
            if (pn.Any(x => x is Feature pf && (set.Contains(pf) || after.Contains(pf)))) after.Add(n);
        }
        for (int k = first; k <= last; k++)
        {
            var m = feats[k];
            if (!set.Contains(m)) continue;
            if (Par(m).OfType<Feature>().FirstOrDefault(after.Contains) is { } bad)
            {
                why = L("«{0}» опирается на «{1}», а та — на операции группы", m.Name, bad.Name);
                return null;
            }
        }
        var res = feats.Take(first).ToList();
        res.AddRange(feats.Skip(first).Take(last - first + 1).Where(f => !set.Contains(f) && !after.Contains(f)));
        res.AddRange(feats.Skip(first).Take(last - first + 1).Where(set.Contains));
        res.AddRange(feats.Skip(first).Take(last - first + 1).Where(after.Contains));
        res.AddRange(feats.Skip(last + 1));
        return res;
    }

    static (double Vol, int Faces, Parametric3D.Kernel.Bounds3 B) Shape(Part p) => (p.Volume, p.Bodies.Sum(b => b.Faces.Count()), p.Bounds);
    static bool SameShape((double Vol, int Faces, Parametric3D.Kernel.Bounds3 B) a, (double Vol, int Faces, Parametric3D.Kernel.Bounds3 B) b)
    {
        double size = a.B.IsEmpty ? 1 : Math.Max(1, (a.B.Max - a.B.Min).Length);
        return a.Faces == b.Faces && Math.Abs(a.Vol - b.Vol) <= 1e-6 * Math.Max(1, Math.Abs(a.Vol))
               && a.B.IsEmpty == b.B.IsEmpty && (a.B.IsEmpty || ((a.B.Min - b.B.Min).Length <= 1e-6 * size && (a.B.Max - b.B.Max).Length <= 1e-6 * size));
    }

    /// <summary>Ссылается ли операция на операцию с ключом key (грани и рёбра «#ключ/…», повтор операций, селекторы).</summary>
    public static bool RefersTo(Feature f, string key)
    {
        string tag = "#" + key + "/";
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool Walk(object? o, int depth)
        {
            if (o == null || depth > 4) return false;
            if (o is string str) return str == key || str.Contains(tag, StringComparison.Ordinal);
            if (o.GetType().IsPrimitive || o is OpenTK.Mathematics.Vector3d) return false;
            if (!o.GetType().IsValueType && !seen.Add(o)) return false;
            if (o is System.Collections.IEnumerable en) { foreach (var x in en) if (Walk(x, depth + 1)) return true; return false; }
            if (o.GetType().Namespace?.StartsWith("Parametric3D") != true && !o.GetType().IsGenericType) return false;
            foreach (var fi in o.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (fi.Name is "Parent" or "Children" or "Name" or "Key" or "Group" or "Error" or "_x" || fi.Name.Contains("Changed")) continue;
                if (Walk(fi.GetValue(o), depth + 1)) return true;
            }
            return false;
        }
        return Walk(f, 0);
    }

    /// <summary>
    /// k97 (замечание 3): куда можно переставить операцию, не испортив деталь — диапазон итоговых номеров [Lo, Hi]: не выше
    /// последнего родителя (операции, на грани/рёбра которой она или её эскиз опираются) и не ниже первого потомка.
    /// </summary>
    public static (int Lo, int Hi) MoveRange(Part part, Feature f)
    {
        int me = part.Features.IndexOf(f);
        if (me < 0) return (0, -1);
        int lo = 0, hi = part.Features.Count - 1;
        var sk = Parametric3D.Model.Dependencies.SketchesOf(f);
        var sketches = part.Sketches.Where(s => sk.Contains(s.Id)).ToList();
        for (int k = 0; k < me; k++)
        {
            var g = part.Features[k];
            if (Parents(part, f).Contains(g) || sketches.Any(s => RefersToObj(s, g.Key))) lo = Math.Max(lo, k + 1);
        }
        for (int k = part.Features.Count - 1; k > me; k--)
        {
            var g = part.Features[k];
            var gsk = Parametric3D.Model.Dependencies.SketchesOf(g);
            if (Parents(part, g).Contains(f) || part.Sketches.Any(s => gsk.Contains(s.Id) && RefersToObj(s, f.Key))) hi = Math.Min(hi, k - 1);
        }
        // k104: операция, которой нужно тело (вырез, скругление, фаска…), не встаёт выше первого тела — вырез первым ничего
        // не вырезал молча (объём детали менялся без ошибки); и первое тело не уходит ниже такой операции
        if (!BodyOrderOff)
        {
            bool Ok(int to)
            {
                var order = part.Features.Where(x => !ReferenceEquals(x, f)).ToList();
                order.Insert(Math.Clamp(to, 0, order.Count), f);
                int firstBody = order.FindIndex(x => !x.Suppressed && CreatesBody(x));
                int firstNeed = order.FindIndex(x => !x.Suppressed && !CreatesBody(x) && NeedsBody(x));
                return firstNeed < 0 || firstBody >= 0 && firstBody < firstNeed;
            }
            while (lo <= hi && !Ok(lo)) lo++;
            while (hi >= lo && !Ok(hi)) hi--;
            if (lo > hi) return (me, me);
        }
        return (lo, hi);
    }

    static readonly bool BodyOrderOff = Environment.GetEnvironmentVariable("P3D_MOVEBODYORDER") == "0";
    /// <summary>Операция создаёт тело: добавляющая по эскизу (выдавливание, вращение, протяжка, по сечениям…).</summary>
    public static bool CreatesBody(Feature f) => !f.Cut && f is not (RoundFeature or ChamferFeature or HoleFeature) && Parametric3D.Model.Dependencies.SketchesOf(f).Count > 0;
    /// <summary>Операции нужно тело: вырез, скругление, фаска.</summary>
    public static bool NeedsBody(Feature f) => f.Cut || f is RoundFeature or ChamferFeature or HoleFeature;

    /// <summary>Переставить операцию на номер to (в пределах MoveRange); перестройка, при поломке — откат с сообщением.</summary>
    public static bool MoveTo(MainWindow w, Feature f, int to)
    {
        var part = w.Session.Part; if (part == null) return false;
        int i = part.Features.IndexOf(f);
        var (lo, hi) = MoveRange(part, f);
        to = Math.Clamp(to, lo, hi);
        if (i < 0 || to == i) return false;
        var before = part.Features.Where(x => x.Error == null && !x.Suppressed).ToHashSet();
        w.Session.Edit(L("Переупорядочить"), () => { part.MoveFeature(f, to); });
        w.Session.Document.RebuildDirty();
        var broken = w.Session.Part!.Features.Where(x => before.Any(b => b.Key == x.Key) && x.Error != null).ToList();
        if (broken.Count > 0)
        {
            w.Session.Undo();
            w.Session.Document.RebuildDirty();
            w.ShowToast(L("Нельзя переставить: «{0}» опирается на соседнюю операцию", broken[0].Name));
            w.ViewPort.MarkDirty(); w.RebuildTree();
            return false;
        }
        w.ViewPort.MarkDirty(); w.RebuildTree();
        return true;
    }

    static bool RefersToObj(object o, string key)
    {
        string tag = "#" + key + "/";
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool Walk(object? x, int depth)
        {
            if (x == null || depth > 5) return false;
            if (x is string str) return str.Contains(tag, StringComparison.Ordinal);
            if (x.GetType().IsPrimitive || x is OpenTK.Mathematics.Vector3d or OpenTK.Mathematics.Vector2d) return false;
            if (!x.GetType().IsValueType && !seen.Add(x)) return false;
            if (x is System.Collections.IEnumerable en) { foreach (var y in en) if (Walk(y, depth + 1)) return true; return false; }
            if (x.GetType().Namespace?.StartsWith("Parametric3D") != true && !x.GetType().IsGenericType) return false;
            foreach (var fi in x.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (fi.Name is "Parent" or "Children" or "Name" || fi.FieldType.IsPrimitive) continue;
                if (Walk(fi.GetValue(x), depth + 1)) return true;
            }
            return false;
        }
        return Walk(o, 0);
    }

    /// <summary>Родители операции: её эскизы, опорные элементы и операции, на грани и рёбра которых она опирается.</summary>
    public static List<ModelItem> Parents(Part part, Feature f)
    {
        var res = new List<ModelItem>();
        var sk = Parametric3D.Model.Dependencies.SketchesOf(f);
        res.AddRange(part.Sketches.Where(s => sk.Contains(s.Id)));
        foreach (var d in part.Children.Where(c => c is DatumPlane or DatumAxis or DatumPoint or DatumCsys or DatumCurve or Line3DItem))
            if (Parametric3D.Model.Dependencies.DependsOnItem(f, part, d)) res.Add(d);
        int me = part.Features.IndexOf(f);
        foreach (var g in part.Features.Take(Math.Max(0, me)))
            if (RefersTo(f, g.Key) || part.Sketches.Any(s => sk.Contains(s.Id) && s.BaseFaceTag is { } t && t.StartsWith("#" + g.Key + "/"))) res.Add(g);
        return res;
    }

    /// <summary>Потомки: операции ниже, опирающиеся на эту (или на эскизы на её гранях).</summary>
    public static List<Feature> Children(Part part, Feature f)
    {
        int me = part.Features.IndexOf(f);
        return part.Features.Skip(me + 1).Where(g => Parents(part, g).Contains(f)).ToList();
    }

    public static async void ShowParentsChildren(MainWindow w)
    {
        var part = w.Session.Part; var f = Selected(w).FirstOrDefault();
        if (part == null || f == null) { w.ShowToast(L("Выберите операцию в дереве"), error: false); return; }
        var par = Parents(part, f); var ch = Children(part, f);
        var win = new Window { Title = L("Родители и потомки — {0}", f.Name), SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        ListBox List(IEnumerable<ModelItem> items)
        {
            var lb = new ListBox { MinWidth = 260, MaxHeight = 300 };
            foreach (var it in items) lb.Items.Add(new ListBoxItem { Content = $"{TreeKind(it)}: {it.Name}", Tag = it });
            if (lb.Items.Count == 0) lb.Items.Add(new ListBoxItem { Content = L("нет"), IsEnabled = false });
            lb.SelectionChanged += (_, _) => { if (lb.SelectedItem is ListBoxItem { Tag: ModelItem m }) w.SelectItem(m); };
            return lb;
        }
        win.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 8, Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children =
            {
                new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L("Родители (на что опирается):") }, List(par) } },
                new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L("Потомки (что опирается на неё):") }, List(ch) } },
            },
        };
        await win.ShowDialog(w);
    }

    static string TreeKind(ModelItem it) => Parametric3D.App.ViewModels.TreeNode.KindLabel(it);

    /// <summary>
    /// Заменить ссылки: эскиз операции — на другой эскиз; плоскость эскиза — на другую плоскость (опорную или грань);
    /// грани и рёбра — правкой операции.
    /// </summary>
    public static async void ReplaceRefs(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        var sel = w.SelectedItems();
        if (sel.FirstOrDefault() is Sketch sk0)
        {
            // эскиз + выбранная плоскость (вторым) — сразу; иначе — список плоскостей
            var planes = part.Children.OfType<DatumPlane>().Cast<ModelItem>().Concat(part.Children.OfType<DatumCsys>()).ToList();
            var pick = await Choose(w, L("Новая плоскость эскиза «{0}»", sk0.Name), planes);
            if (pick != null) ReplaceSketchPlane(w, sk0, pick);
            return;
        }
        var f = sel.OfType<Feature>().FirstOrDefault();
        if (f == null) { w.ShowToast(L("Выберите операцию или эскиз"), error: false); return; }
        var fi = f.GetType().GetField("SketchId") ?? f.GetType().GetField("SectionSketchId");
        if (fi != null && fi.FieldType == typeof(uint))
        {
            var cur = (uint)fi.GetValue(f)!;
            var used = new HashSet<uint>(part.Features.Where(x => x != f).SelectMany(x => x.ConsumedSketchIds()));
            var cands = part.Sketches.Where(s => s.Id != cur && !used.Contains(s.Id)).Cast<ModelItem>().ToList();
            var pick = await Choose(w, L("Новый эскиз для «{0}»", f.Name), cands);
            if (pick is Sketch ns) ReplaceSketch(w, f, ns);
            return;
        }
        w.ShowToast(L("Ссылки на грани и рёбра — в правке операции"), error: false);
        w.EditSelected();
    }

    public static void ReplaceSketch(MainWindow w, Feature f, Sketch ns)
    {
        var part = w.Session.Part!;
        var fi = f.GetType().GetField("SketchId") ?? f.GetType().GetField("SectionSketchId");
        if (fi == null) return;
        w.Session.Edit(L("Заменить ссылки"), () => { fi.SetValue(f, ns.Id); part.NotifyChanged(f); });
        w.Session.Document.RebuildDirty(); w.ViewPort.MarkDirty(); w.RebuildTree();
    }

    public static void ReplaceSketchPlane(MainWindow w, Sketch sk, ModelItem plane)
    {
        var fr = plane switch { DatumPlane dp => dp.Frame, DatumCsys cs => cs.Frame, _ => (Parametric3D.Kernel.Frame3?)null };
        if (fr is not { } frame) return;
        w.Session.Edit(L("Заменить ссылки"), () => sk.SetBasePlane(frame, plane.Id, plane.Name));
        w.Session.Document.RebuildDirty(); w.ViewPort.MarkDirty(); w.RebuildTree();
    }

    static async Task<ModelItem?> Choose(MainWindow w, string title, List<ModelItem> items)
    {
        if (items.Count == 0) { w.ShowToast(L("Подходящих элементов нет"), error: false); return null; }
        ModelItem? res = null;
        var win = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        var lb = new ListBox { MinWidth = 320, MaxHeight = 360 };
        foreach (var it in items) lb.Items.Add(new ListBoxItem { Content = $"{TreeKind(it)}: {it.Name}", Tag = it });
        var ok = new Button { Content = L("ОК"), Classes = { "accent" } };
        ok.Click += (_, _) => { res = (lb.SelectedItem as ListBoxItem)?.Tag as ModelItem; win.Close(); };
        lb.DoubleTapped += (_, _) => { res = (lb.SelectedItem as ListBoxItem)?.Tag as ModelItem; win.Close(); };
        win.Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { lb, ok } };
        await win.ShowDialog(w);
        return res;
    }
}
