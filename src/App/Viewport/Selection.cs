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
    private PickRef _hover = PickRef.Empty;

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

    public bool Contains(uint id) => _items.Any(i => i.Id == id);

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
            _items.Clear();
            _items.Add(r);
        }
        else
        {
            int i = _items.FindIndex(x => x.Id == r.Id);
            if (i >= 0) _items.RemoveAt(i);   // повторный клик снимает выделение
            else _items.Add(r);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0 && _hover.IsEmpty) return;
        _items.Clear();
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
        if (r.Target is SkDimension sd) return $"{kind}: {Views.MainWindow.DimensionName(sd)} #{r.Id}";
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

        var groups = _items
            .GroupBy(KindName)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}");

        return $"{_items.Count}: " + string.Join(", ", groups);
    }

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
