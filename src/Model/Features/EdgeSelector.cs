using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

/// <summary>
/// KRN-08 (08.3): СЕЛЕКТОР РЁБЕР ПО РОЛИ — правило, которое разрешается заново при каждой перестройке, а не запомненные
/// рёбра. Роль грани — операция, которая её породила (постоянный ключ в имени грани «#ключ/…»).
/// <list type="bullet">
/// <item>Rim — кромки операции: рёбра между гранью операции и гранью не её (кромки отверстия, бобышки, паза);</item>
/// <item>Between — рёбра между гранями двух операций (стык бобышки с плитой: Between(бобышка, плита));</item>
/// <item>Inner — рёбра между двумя гранями одной операции (рёбра самой бобышки).</item>
/// </list>
/// Правка операции (диаметр отверстия, высота бобышки) меняет рёбра — селектор находит новые: замысел «скруглить кромки
/// отверстия» переживает правку, геометрическая ссылка — нет. Пустое разрешение — ошибка (а не молчаливое «ничего»).
/// </summary>
public sealed record EdgeSelector(EdgeSelectorKind Kind, string FeatureKey, string? OtherKey = null)
{
    static bool Of(TFace? f, string key) => f != null && f.Tag.StartsWith("#" + key + "/", StringComparison.Ordinal);

    public List<TEdge> Resolve(IEnumerable<TSolid> bodies)
    {
        var res = new List<TEdge>();
        foreach (var e in bodies.SelectMany(b => b.Edges))
        {
            if (e.Hidden) continue;
            TFace? a = e.FaceA, b = e.FaceB;
            if (a == null || b == null || ReferenceEquals(a, b)) continue;
            bool ok = Kind switch
            {
                EdgeSelectorKind.Rim => Of(a, FeatureKey) != Of(b, FeatureKey),
                EdgeSelectorKind.Inner => Of(a, FeatureKey) && Of(b, FeatureKey),
                EdgeSelectorKind.Between => OtherKey != null && ((Of(a, FeatureKey) && Of(b, OtherKey)) || (Of(b, FeatureKey) && Of(a, OtherKey))),
                _ => false,
            };
            if (ok) res.Add(e);
        }
        return res;
    }

    public override string ToString() => Kind == EdgeSelectorKind.Between ? $"{Kind}({FeatureKey}, {OtherKey})" : $"{Kind}({FeatureKey})";
}

public enum EdgeSelectorKind { Rim = 0, Between = 1, Inner = 2 }

/// <summary>
/// KRN-08 (08.3): СЕЛЕКТОР ГРАНЕЙ ПО РОЛИ. Feature — все грани операции; Role — грани операции с данной ролью в имени
/// («top», «bottom», «side», «wall»…: «#ключ/…:роль» или «#ключ/…:роль[…]»). Разрешается при каждой перестройке: грань,
/// разделённая последующей операцией на куски, даёт все куски (ссылка по точке в таком случае — неоднозначность).
/// Пустое разрешение — ошибка операции.
/// </summary>
public sealed record FaceSelector(FaceSelectorKind Kind, string FeatureKey, string? Role = null)
{
    public bool Matches(TFace f)
    {
        if (!f.Tag.StartsWith("#" + FeatureKey + "/", StringComparison.Ordinal)) return false;
        if (Kind == FaceSelectorKind.Feature) return true;
        if (string.IsNullOrEmpty(Role)) return false;
        string bare = Kernel.Ops.TransformOp.WithoutInstance(EdgeRef.Bare(f.Tag));
        return bare == Role || bare.EndsWith(":" + Role, StringComparison.Ordinal) || bare.Contains(":" + Role + "[", StringComparison.Ordinal)
               || bare.StartsWith(Role + "[", StringComparison.Ordinal);
    }

    public List<TFace> Resolve(IEnumerable<TSolid> bodies) => bodies.SelectMany(b => b.Faces).Where(Matches).ToList();

    public override string ToString() => Kind == FaceSelectorKind.Role ? $"Faces({FeatureKey}:{Role})" : $"Faces({FeatureKey})";
}

public enum FaceSelectorKind { Feature = 0, Role = 1 }
