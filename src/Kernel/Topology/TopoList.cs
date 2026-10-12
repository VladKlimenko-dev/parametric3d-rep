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

    public int Count => _items.Count;
    public bool IsReadOnly => _owner.IsFrozen;
    public T this[int index] { get => _items[index]; set { Check(); _items[index] = value; } }

    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public void Add(T item) { Check(); _items.Add(item); }
    public void AddRange(IEnumerable<T> items) { Check(); _items.AddRange(items); }
    public void Insert(int index, T item) { Check(); _items.Insert(index, item); }
    public bool Remove(T item) { Check(); return _items.Remove(item); }
    public void RemoveAt(int index) { Check(); _items.RemoveAt(index); }
    public int RemoveAll(Predicate<T> match) { Check(); return _items.RemoveAll(match); }
    public void Clear() { Check(); _items.Clear(); }
    public void Reverse() { Check(); _items.Reverse(); }

    public bool Contains(T item) => _items.Contains(item);
    public int IndexOf(T item) => _items.IndexOf(item);
    public int FindIndex(Predicate<T> match) => _items.FindIndex(match);
    public T[] ToArray() => _items.ToArray();

    /// <summary>
    /// Тот же список для ЧТЕНИЯ в горячих путях ядра, ожидающих List&lt;T&gt; (без копии и без интерфейсного
    /// индексатора). Писать через него нельзя — это обход заморозки; только внутри ядра и только чтение.
    /// </summary>
    internal List<T> ReadView => _items;
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
}
