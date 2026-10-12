namespace Parametric3D.Core;

/// <summary>
/// Глобальный аллокатор идентификаторов.
///
/// В старом коде id и «цвет для пикинга» выдавались раздельно, цвет
/// раскладывался в RGB8, и после чтения пикселя выполнялось обратное
/// преобразование float -> int с ошибками округления. Здесь id — просто
/// uint: он адресует диапазоны в вершинных буферах (подсветка) и
/// возвращается процедурой выбора (Views/ScenePicker.cs). Никакого
/// кодирования в цвет и обратно нет.
///
/// 0 зарезервирован под «ничего не выбрано».
/// </summary>
public static class Ids
{
    // K04: выдача номеров атомарна. Прежде `_next++` при двух одновременных расчётах выдавал
    // одинаковые номера разным граням, а по номеру грани ведутся и подписи для устойчивых имён.
    private static int _next = 0;
    public static uint New() => unchecked((uint)Interlocked.Increment(ref _next));
    public static void Reset() => Interlocked.Exchange(ref _next, 0);
    public const uint None = 0u;
}

/// <summary>Что именно скрывается за pick-id. Заполняется сценой при построении буферов.</summary>
public enum PickKind
{
    None = 0, Vertex, Edge, Face, Body, DatumPlane, DatumAxis, DatumPoint,
    SketchPoint, SketchCurve, SketchDimension, SketchConstraint, Gizmo,
    // Узлы дерева, у которых нет собственной геометрии в кадре, но которые
    // можно выбрать в дереве проекта и увидеть в статусной строке.
    Feature, Assembly
}

public readonly struct PickRef
{
    public readonly uint Id;
    public readonly PickKind Kind;
    public readonly object? Owner;   // Part / Sketch / DatumPlane ...
    public readonly object? Target;  // TFace / TEdge / SkCurve ...

    public PickRef(uint id, PickKind kind, object? owner, object? target)
    { Id = id; Kind = kind; Owner = owner; Target = target; }

    public static readonly PickRef Empty = new PickRef(0, PickKind.None, null, null);
    public bool IsEmpty => Id == 0;
}
