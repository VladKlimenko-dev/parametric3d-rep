using System.Collections;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// ПОСТАВКА 2. СПИСОК ТОПОЛОГИИ С ЗАМОРОЗКОЙ ВЛАДЕЛЬЦА. Обёртка над List&lt;T&gt;: чтение и перечисление — те же
/// (перечислитель — структура List&lt;T&gt;.Enumerator, без выделений и виртуальных вызовов: ядро обходит
/// топологию в горячих циклах), изменение — только пока владелец не опубликован (TEntity.Frozen). Так
/// заморозка видит и добавление/удаление граней, контуров и полурёбер, а не только поля формы.
/// </summary>
/// <summary>Владелец, которого публикация делает неизменяемым (сущность топологии, гранёная поверхность).</summary>
public interface IFreezable
{
    bool IsFrozen { get; }
    string FreezeName { get; }
}

public sealed class TopoList<T> : IList<T>, IReadOnlyList<T>
{
    private readonly List<T> _items = new();
    private readonly IFreezable _owner;
    private readonly string _name;

    public TopoList(IFreezable owner, string name) { _owner = owner; _name = name; }

    private void Check()
    {
        if (_owner.IsFrozen) throw new PublishedBodyMutationException(_owner.FreezeName, _name);
    }

    // k126: ОТЛОЖЕННОЕ НАПОЛНЕНИЕ (сетка грани промежуточного тела — по требованию). Первое обращение к списку вызывает
    // заполнитель один раз (под замком), дальше — обычный список. Заполнитель не трогает этот же список.
    private volatile Func<List<T>>? _pending;
    internal void SetPending(Func<List<T>> fill) { Check(); _pending = fill; }
    /// <summary>Список ждёт отложенного наполнения (обращение к нему его выполнит).</summary>
    public bool IsPending => _pending != null;
    /// <summary>Выполнить отложенное наполнение (если есть).</summary>
    public void EnsureFilled() { if (_pending != null) Fill(); }
    private void Fill()
    {
        lock (_items)
        {
            var p = _pending; if (p == null) return;
            var items = p();
            _items.AddRange(items);
            _pending = null;
        }
    }
    private List<T> L { get { if (_pending != null) Fill(); return _items; } }

    public int Count => L.Count;
    public bool IsReadOnly => _owner.IsFrozen;
    public T this[int index] { get => L[index]; set { Check(); L[index] = value; } }

    public List<T>.Enumerator GetEnumerator() => L.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => L.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => L.GetEnumerator();

    public void Add(T item) { Check(); L.Add(item); }
    public void AddRange(IEnumerable<T> items) { Check(); L.AddRange(items); }
    public void Insert(int index, T item) { Check(); L.Insert(index, item); }
    public bool Remove(T item) { Check(); return L.Remove(item); }
    public void RemoveAt(int index) { Check(); L.RemoveAt(index); }
    public int RemoveAll(Predicate<T> match) { Check(); return L.RemoveAll(match); }
    public void Clear() { Check(); _pending = null; _items.Clear(); }
    public void Reverse() { Check(); L.Reverse(); }

    public bool Contains(T item) => L.Contains(item);
    public int IndexOf(T item) => L.IndexOf(item);
    public int FindIndex(Predicate<T> match) => L.FindIndex(match);
    public T[] ToArray() => L.ToArray();

    /// <summary>
    /// Тот же список для ЧТЕНИЯ в горячих путях ядра, ожидающих List&lt;T&gt; (без копии и без интерфейсного
    /// индексатора). Писать через него нельзя — это обход заморозки; только внутри ядра и только чтение.
    /// </summary>
    internal List<T> ReadView => L;
    public void CopyTo(T[] array, int arrayIndex) => L.CopyTo(array, arrayIndex);
}
