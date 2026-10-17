using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model;

/// <summary>
/// u13: КВИЛТ — поверхность без объёма (набор граней). Хранится как тело-основа и выбранные его грани: основа даёт
/// замкнутую топологию для ядра (утолщение — полость основы с открытыми остальными гранями, отверждение — сама основа,
/// смещение — смещение граней основы, продление — продление основы сквозь граничные грани), показываются только грани
/// квилта. Квилт создаёт операция дерева; на него ссылаются по ключу этой операции.
/// </summary>
public sealed class Quilt
{
    public string Key;
    public string Name;
    public TSolid Parent;
    public HashSet<TFace> Faces;
    /// <summary>u13: точное утолщение (поверхность выдавливанием/вращением — полоса из цепочки и её эквидистанты): толщина,
    /// другая сторона → тело; null — общим путём.</summary>
    public Func<double, bool, TSolid?>? Thicken;
    /// <summary>Объединённый квилт — из нескольких основ.</summary>
    public readonly List<Quilt> Parts = new();

    public Quilt(string key, string name, TSolid parent, IEnumerable<TFace> faces)
    {
        Key = key; Name = name; Parent = parent; Faces = faces.ToHashSet();
    }

    /// <summary>Грани основы, не входящие в квилт (граничные).</summary>
    public IEnumerable<TFace> Others => Parent.Faces.Where(f => !Faces.Contains(f));
    public double Area => Faces.Sum(f => Math.Abs(AnalyticVolume.FaceArea(f) ?? 0));
}
