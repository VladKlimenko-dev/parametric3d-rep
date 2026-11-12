using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Viewport;

/// <summary>
/// Состояние выделения и подсветки.
///
/// В старом коде это жило внутри GLItem: каждый объект сам помнил, выбран
/// он или нет, и сам лез в вид, чтобы попросить перерисовку. Из-за этого
/// «снять выделение со всего» означало обход всего дерева, а два вида на
/// один документ были невозможны.
///
/// Здесь выделение — отдельный объект, принадлежащий виду. Модель о нём
/// не знает; рендер получает флаги через RenderScene.SetFlags.
/// </summary>
public sealed class Selection
{
    public const uint FlagHover = 1u;
    public const uint FlagSelected = 2u;

    private readonly List<PickRef> _items = new();
    /// <summary>k120: номера выбранного — для проверки «выбран ли» за O(1) (рамка на тысячи граней проверяла списком).</summary>
    private readonly HashSet<uint> _ids = new();
    private PickRef _hover = PickRef.Empty;
    /// <summary>
    /// Edge chains picked as ONE item (a thread edge is 800+ pieces): member id → head id, head id → member ids. The list,
    /// status line and row × treat a chain as one entry; all members stay in Items for operations. P3D_SELCHAIN=0 — off.
    /// </summary>
    private readonly Dictionary<uint, uint> _chainHead = new();
    private readonly Dictionary<uint, List<uint>> _chains = new();
    public static readonly bool SelChainOff = Environment.GetEnvironmentVariable("P3D_SELCHAIN") == "0";

    /// <summary>Number of edges in the chain that contains id (0 — not part of a chain).</summary>
    public int ChainSize(uint id) => _chainHead.TryGetValue(id, out var h) && _chains.TryGetValue(h, out var m) ? m.Count : 0;
    /// <summary>Members of the chain that contains id (empty — not part of a chain).</summary>
    public IReadOnlyList<uint> ChainMembers(uint id) => _chainHead.TryGetValue(id, out var h) && _chains.TryGetValue(h, out var m) ? m : Array.Empty<uint>();
    /// <summary>Selected items as the user sees them: a chain is its head only.</summary>
    public IEnumerable<PickRef> Logical
    {
        get { foreach (var r in _items) if (!_chainHead.TryGetValue(r.Id, out var h) || h == r.Id) yield return r; }
    }
    public int LogicalCount => _items.Count - _chainHead.Count + _chains.Count;

    private void ClearChains() { _chainHead.Clear(); _chains.Clear(); }
    private void UnchainMember(uint id)
    {
        if (!_chainHead.Remove(id, out var h) || !_chains.TryGetValue(h, out var m)) return;
        m.Remove(id);
        if (m.Count <= 1 || id == h)
        {
            foreach (var x in m) _chainHead.Remove(x);
            _chains.Remove(h);
        }
    }

    /// <summary>Какие типы элементов сейчас можно выбирать.</summary>
    public PickFilter Filter = PickFilter.All;

    public event Action? Changed;

    public IReadOnlyList<PickRef> Items => _items;
    public PickRef Hover => _hover;
    public PickRef Primary => _items.Count > 0 ? _items[0] : PickRef.Empty;
    public PickRef Last => _items.Count > 0 ? _items[^1] : PickRef.Empty;
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    public bool SetHover(PickRef r)
    {
        if (_hover.Id == r.Id) return false;
        _hover = r;
        Changed?.Invoke();
        return true;
    }

    public bool Contains(uint id) => _ids.Contains(id);

    /// <summary>
    /// k120 (замечание 15:06, п. 8): ВЫБОР МНОГИХ ОДНИМ ШАГОМ (рамка, аркан). Прежде каждый элемент рамки добавлялся
    /// отдельно: поиск по списку и событие «выбор изменился» на каждый — панель свойств, дерево и подсветка пересобирались
    /// тысячи раз, окно «подвисало». Теперь — один проход и одно событие. P3D_SELMANY=0 — по одному, как прежде.
    /// </summary>
    public void SelectMany(IEnumerable<PickRef> refs, bool additive)
    {
        if (SelectManyOff)
        {
            if (!additive) Clear();
            foreach (var r in refs) if (!Contains(r.Id)) Select(r, additive: true);
            return;
        }
        if (!additive) { _items.Clear(); _ids.Clear(); ClearChains(); }
        foreach (var r in refs) if (!r.IsEmpty && _ids.Add(r.Id)) _items.Add(r);
        ChangedCount++;
        Changed?.Invoke();
    }
    public static readonly bool SelectManyOff = Environment.GetEnvironmentVariable("P3D_SELMANY") == "0";
    /// <summary>Разбор: сколько раз сообщено «выбор изменился» (выбор, снятие, наведение).</summary>
    public int ChangedCount { get; private set; }

    /// <summary>k120 (п. 8.1): снять выбор одного элемента.</summary>
    public void Deselect(uint id)
    {
        // a chain row's × drops the whole chain
        if (_chainHead.TryGetValue(id, out var head) && _chains.TryGetValue(head, out var mem))
        {
            var drop = new HashSet<uint>(mem);
            foreach (var x in drop) { _ids.Remove(x); _chainHead.Remove(x); }
            _chains.Remove(head);
            _items.RemoveAll(x => drop.Contains(x.Id));
            ChangedCount++;
            Changed?.Invoke();
            return;
        }
        int i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        _items.RemoveAt(i); _ids.Remove(id);
        ChangedCount++;
        Changed?.Invoke();
    }

    /// <param name="additive">Зажат CTRL — последовательный выбор.</param>
    public void Select(PickRef r, bool additive)
    {
        if (r.IsEmpty)
        {
            if (!additive) Clear();
            return;
        }

        if (!additive)
        {
            _items.Clear(); _ids.Clear(); ClearChains();
            _items.Add(r); _ids.Add(r.Id);
        }
        else
        {
            int i = _ids.Contains(r.Id) ? _items.FindIndex(x => x.Id == r.Id) : -1;
            if (i >= 0) { _items.RemoveAt(i); _ids.Remove(r.Id); UnchainMember(r.Id); }   // повторный клик снимает выделение
            else { _items.Add(r); _ids.Add(r.Id); }
        }
        ChangedCount++;
        Changed?.Invoke();
    }

    /// <summary>
    /// Select a group (an edge chain) with ONE change notification. Selecting a thread edge chain
    /// one by one raised Changed per edge: command refresh, status and operation updates for every
    /// prefix of the chain — quadratic, a long freeze on threads with hundreds of edge pieces.
    /// add: true — add the missing ones; remove: true — drop those that are selected.
    /// </summary>
    public void SelectGroup(IReadOnlyList<PickRef> group, bool additive, bool remove = false)
    {
        bool changed = false;
        if (!additive && !remove) { changed = _items.Count > 0; _items.Clear(); _ids.Clear(); ClearChains(); }
        HashSet<uint>? drop = remove ? new HashSet<uint>() : null;
        foreach (var r in group)
        {
            if (r.IsEmpty) continue;
            if (remove) { if (_ids.Remove(r.Id)) { drop!.Add(r.Id); changed = true; UnchainMember(r.Id); } }
            else if (_ids.Add(r.Id)) { _items.Add(r); changed = true; }
        }
        if (drop is { Count: > 0 }) _items.RemoveAll(x => drop.Contains(x.Id));
        // the group is one chain: head — the first (clicked) edge; members leave any chain they were in
        if (!remove && !SelChainOff && group.Count > 1)
        {
            var mem = new List<uint>(group.Count); var seen = new HashSet<uint>();
            foreach (var r in group) if (!r.IsEmpty && _ids.Contains(r.Id) && seen.Add(r.Id)) mem.Add(r.Id);
            // the same chain picked from another of its edges — unchanged
            bool same = _chainHead.TryGetValue(mem[0], out var oh) && _chains.TryGetValue(oh, out var old) && old.Count == mem.Count && seen.SetEquals(old);
            if (mem.Count > 1 && !same)
            {
                foreach (var x in mem) UnchainMember(x);
                foreach (var x in mem) _chainHead[x] = mem[0];
                _chains[mem[0]] = mem;
                changed = true;
            }
        }
        if (!changed) return;
        ChangedCount++;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0 && _hover.IsEmpty) return;
        _items.Clear(); _ids.Clear(); ClearChains();
        ChangedCount++;
        Changed?.Invoke();
    }

    public IEnumerable<T> OfKind<T>(PickKind kind) where T : class
    {
        foreach (var i in _items)
            if (i.Kind == kind && i.Target is T t) yield return t;
    }

    public IEnumerable<uint> IdsOfKind(PickKind kind)
    {
        foreach (var i in _items)
            if (i.Kind == kind) yield return i.Id;
    }

    /// <summary>k99: дополнительное условие выбора под курсором (активное поле операции); null — нет.</summary>
    public Func<PickRef, bool>? Predicate { get; set; }

    public bool Accepts(PickKind k) => k switch
    {
        PickKind.Face => Filter.HasFlag(PickFilter.Faces),
        PickKind.Edge => Filter.HasFlag(PickFilter.Edges),
        PickKind.Vertex => Filter.HasFlag(PickFilter.Vertices),
        PickKind.SketchCurve => Filter.HasFlag(PickFilter.SketchCurves),
        PickKind.SketchPoint => Filter.HasFlag(PickFilter.SketchPoints),
        // Размеры выбираются всегда: они не геометрия, а подпись к ней.
        PickKind.SketchDimension => true,
        PickKind.SketchConstraint => true,
        PickKind.DatumPlane => Filter.HasFlag(PickFilter.Datums),
        PickKind.DatumAxis => Filter.HasFlag(PickFilter.Datums),
        PickKind.DatumPoint => Filter.HasFlag(PickFilter.Datums),
        PickKind.Body => Filter.HasFlag(PickFilter.Parts),
        // Узлы дерева фильтром типов геометрии не ограничиваются.
        PickKind.Feature => true,
        PickKind.Assembly => true,
        _ => false
    };

    // ------------------------------------------------------ описание выбора

    /// <summary>
    /// Человекочитаемое имя типа выбранного объекта.
    ///
    /// Смотрим не только на PickKind, но и на сам объект: под
    /// SketchCurve скрываются и отрезок, и дуга, и сплайн, и это
    /// разные вещи для пользователя.
    /// </summary>
    public static string KindName(PickRef r) => Loc.L(KindNameRu(r));

    public static string KindNameRu(PickRef r) => r.Target switch
    {
        Assembly => "Сборка",
        Part => "Деталь",
        Sketch => "Эскиз",
        SkDimension => "Размер",
        SkConstraint => "Связь",
        Feature => "Операция",
        SkLine => "Отрезок",
        SkArc => "Дуга",
        SkCircle => "Окружность",
        SkEllipse => "Эллипс",
        SkSpline => "Сплайн",
        SkPoint => "Точка",
        DatumPlane => "Плоскость",
        DatumAxis => "Ось",
        DatumPoint => "Точка",
        Line3DItem => "Линия",
        _ => r.Kind switch
        {
            PickKind.Face => "Грань",
            PickKind.Edge => "Ребро",
            PickKind.Vertex => "Вершина",
            PickKind.Body => "Деталь",
            PickKind.SketchCurve => "Кривая",
            PickKind.SketchPoint => "Точка",
            PickKind.DatumPlane => "Плоскость",
            PickKind.DatumAxis => "Ось",
            PickKind.DatumPoint => "Точка",
            _ => "Элемент"
        }
    };

    /// <summary>Подтип грани или ребра по геометрии (плоская, цилиндр, прямое, дуга…), если деталь известна.</summary>
    public static string? Subtype(PickRef r) => SubtypeRu(r) is { } s ? Loc.L(s) : null;

    private static string? SubtypeRu(PickRef r)
    {
        if (r.Owner is not Part p) return null;
        try
        {
            if (r.Kind == PickKind.Face && p.FindFace(r.Id) is { } f)
            {
                string n = f.Surface?.GetType().Name ?? "";
                return n.Contains("Plane") || Operations.Refs.FacePlane(p, r.Id) != null ? "плоская" : n.Contains("Cylind") ? "цилиндр" : n.Contains("Cone") ? "конус"
                     : n.Contains("Sphere") ? "шар" : n.Contains("Torus") ? "тор" : n.Contains("Nurbs") || n.Contains("Spline") ? "свободная" : null;
            }
            if (r.Kind == PickKind.Edge && p.FindEdge(r.Id) is { } e)
            {
                string n = e.Curve?.GetType().Name ?? "";
                return n.Contains("Line") ? "прямое" : n.Contains("Arc") || n.Contains("Circle") ? "дуга" : n.Contains("Ellipse") ? "эллипс" : "кривое";
            }
        }
        catch { }
        return null;
    }

    /// <summary>Имя конкретного объекта, если оно у него есть.</summary>
    public static string Label(PickRef r)
    {
        string kind = KindName(r);
        if (r.Target is Feature f) return $"{kind} «{f.Summary}»";
        // k96 (п. 11): связь — с видом («Связь: Горизонтально #12»), размер — со значением и ролью, нуль и оси — по имени
        if (r.Target is SkConstraint sc) return $"{kind}: {Views.MainWindow.ConstraintName(sc.Type)} #{r.Id}";
        if (r.Target is SkDimension sd) return $"{kind} {sd.Name}: {Views.MainWindow.DimensionName(sd)}";
        if (r.Target is SkEntity { Builtin: true } be) return Loc.L(be.Name);
        if (r.Target is ModelItem m && !string.IsNullOrEmpty(m.Name))
            return $"{kind} «{m.Name}»";
        string? sub = Subtype(r);
        return sub != null ? $"{kind} ({sub}) #{r.Id}" : $"{kind} #{r.Id}";
    }

    /// <summary>Строка для статусной панели: что именно сейчас выбрано.</summary>
    public string Describe()
    {
        if (_items.Count == 0) return "";
        if (_items.Count == 1) return Label(_items[0]);
        var logical = Logical.ToList();
        if (logical.Count == 1) return LabelIn(logical[0]);

        var groups = logical
            .GroupBy(r => ChainSize(r.Id) > 0 ? Loc.L(r.Kind == PickKind.Face ? "Цепочка граней" : "Цепочка рёбер") : KindName(r))
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}");

        return $"{logical.Count}: " + string.Join(", ", groups);
    }

    /// <summary>Label of a selected item; a chain head reads as the whole chain.</summary>
    public string LabelIn(PickRef r) => ChainSize(r.Id) is var n && n > 0 ? Loc.L(r.Kind == PickKind.Face ? "Цепочка граней ({0})" : "Цепочка рёбер ({0})", n) + " — " + Label(r) : Label(r);

    /// <summary>Что под курсором — для той же панели.</summary>
    public string DescribeHover() => _hover.IsEmpty ? "" : Label(_hover);
}

[Flags]
public enum PickFilter
{
    None = 0,
    Faces = 1,
    Edges = 2,
    Vertices = 4,
    SketchCurves = 8,
    SketchPoints = 16,
    Datums = 32,
    Parts = 64,
    Solid = Faces | Edges | Vertices,
    Sketch = SketchCurves | SketchPoints,
    All = Faces | Edges | Vertices | SketchCurves | SketchPoints | Datums | Parts
}
