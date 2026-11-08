using Parametric3D.Core;

namespace Parametric3D.Model;

public enum ItemKind
{
    Document, Assembly, Part, Body, Feature, Sketch,
    DatumPlane, DatumAxis, DatumPoint, DatumCsys,
    Material, Mate, Parameter
}

/// <summary>
/// Базовый узел дерева модели.
///
/// Что здесь СПЕЦИАЛЬНО отсутствует по сравнению со старым GLItem:
///   * ShaderProgram, VAO/VBO/EBO, vertices/indices — вся отрисовка в Render;
///   * pick_color, hovered, selected — состояние выделения в Selection;
///   * ссылка на TreeNode и подписка на события контрола дерева — UI сам
///     строит своё представление по модели, а не модель лезет в UI;
///   * статические ссылки на Project.main_view / main_form.
///
/// Остаётся только то, что действительно описывает документ.
/// </summary>
public abstract class ModelItem
{
    public readonly uint Id = Ids.New();
    public abstract ItemKind Kind { get; }

    /// <summary>
    /// k179: ПОРЯДОК СОЗДАНИЯ — номер по времени создания элемента (растёт; в файле — свой, при чтении — прежний). Порядок
    /// операций и видимость в правке эскиза — по нему, а не по месту в дереве: операция, созданная позже, может включать
    /// эскизы, созданные раньше, и стоять в дереве выше элемента, созданного до неё.
    /// </summary>
    public long Seq { get; private set; } = Interlocked.Increment(ref _seqCounter);
    static long _seqCounter;
    /// <summary>k179: элемент создан сейчас (вставка копии) — новый номер порядка.</summary>
    public void RenewSeq() => Seq = Interlocked.Increment(ref _seqCounter);
    /// <summary>Порядок из файла (номер, прочитанный при открытии); счётчик — не ниже него.</summary>
    public void RestoreSeq(long seq)
    {
        if (seq <= 0) return;
        Seq = seq;
        long cur;
        while ((cur = Interlocked.Read(ref _seqCounter)) < seq && Interlocked.CompareExchange(ref _seqCounter, seq, cur) != cur) { }
    }
    /// <summary>Создан позже other (у элементов одной детали). Мутация P3D_CREATIONORDER=0 — по месту в дереве (k178).</summary>
    public bool CreatedAfter(ModelItem other)
    {
        if (ReferenceEquals(this, other)) return false;
        if (CreationOrderOff)
        {
            if (Parent is not ModelItem p || !ReferenceEquals(p, other.Parent)) return false;
            int a = p.Children.IndexOf(this), b = p.Children.IndexOf(other);
            return a >= 0 && b >= 0 && a > b;
        }
        return Seq > other.Seq;
    }
    public static readonly bool CreationOrderOff = Environment.GetEnvironmentVariable("P3D_CREATIONORDER") == "0";

    public string Name = "item";
    public ModelItem? Parent;
    public readonly List<ModelItem> Children = new();

    public bool Visible = true;

    /// <summary>
    /// Видимость задана ПОЛЬЗОВАТЕЛЕМ (глазом в дереве).
    ///
    /// Приложение прячет эскизы, ушедшие в тела, при каждой перестройке —
    /// иначе они остались бы видны у операций, созданных ранее. Но если
    /// пользователь однажды сам решил, что показывать, приложение больше
    /// не спорит: без этого признака глаз в дереве было бы не переупрямить.
    /// </summary>
    public bool VisibleUserSet;
    /// <summary>
    /// Подавлена ли операция. Через свойство, а не поле: смена подавления меняет построение
    /// начиная с этой операции, и дерево должно об этом узнать — иначе приращение пересчитает не
    /// тот хвост и выдаст прежнее тело.
    /// </summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value) return;
            _suppressed = value;
            NotifyChanged(this);
        }
    }

    private bool _suppressed;
    /// <summary>Ошибка последнего перестроения. null — всё в порядке.</summary>
    public string? Error;

    /// <summary>Поднимается вверх по дереву при любом изменении, требующем перестроения.</summary>
    public event Action<ModelItem>? Changed;

    public void Add(ModelItem child)
    {
        child.Parent = this;
        Children.Add(child);
        NotifyChanged(child);
    }

    /// <summary>k184: вставка на место index (вставка компонента у строки отката сборки).</summary>
    public void Insert(int index, ModelItem child)
    {
        child.Parent = this;
        Children.Insert(Math.Clamp(index, 0, Children.Count), child);
        NotifyChanged(child);
    }

    public bool Remove(ModelItem child)
    {
        if (!Children.Remove(child)) return false;
        child.Parent = null;
        NotifyChanged(this);
        return true;
    }

    public void NotifyChanged(ModelItem source)
    {
        Changed?.Invoke(source);
        Parent?.NotifyChanged(source);
    }

    public IEnumerable<ModelItem> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public T? FindById<T>(uint id) where T : ModelItem
    {
        if (Id == id && this is T self) return self;
        foreach (var c in Children)
        {
            var r = c.FindById<T>(id);
            if (r != null) return r;
        }
        return null;
    }

    public override string ToString() => $"{Kind} «{Name}» #{Id}";
}
