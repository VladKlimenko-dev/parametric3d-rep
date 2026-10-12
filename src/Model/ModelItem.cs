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
