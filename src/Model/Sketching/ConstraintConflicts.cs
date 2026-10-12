namespace Parametric3D.Model.Sketching;

/// <summary>Что мешает добавить ограничение.</summary>
public enum ConflictKind
{
    /// <summary>Такое же ограничение на тех же элементах уже есть.</summary>
    Duplicate,
    /// <summary>Два ограничения требуют несовместимого.</summary>
    Constraint,
    /// <summary>Ограничение спорит с заданным размером.</summary>
    Dimension
}

/// <summary>Одна найденная помеха.</summary>
public sealed class Conflict
{
    public ConflictKind Kind;
    public SkConstraint? Constraint;
    public SkDimension? Dimension;
    public string Description = "";

    public override string ToString() => Description;
}

/// <summary>
/// Поиск противоречий между ограничениями и размерами.
///
/// Проверка СТРУКТУРНАЯ, а не численная: смотрим, что именно наложено на
/// те же самые элементы. Полагаться на решатель здесь нельзя — он сообщает
/// лишь «эскиз переопределён», без указания виновных, а пользователю нужен
/// список: что с чем спорит и что можно убрать.
///
/// Список правил намеренно короткий и покрывает случаи, которые встречаются
/// на практике. Он расширяется добавлением одной ветки, а не переписыванием.
/// </summary>
public static class ConstraintConflicts
{
    private const double AngleTol = 1e-3;

    /// <summary>
    /// Находит всё, что мешает наложить ограничение type на items.
    /// Пустой список — препятствий нет.
    /// </summary>
    /// <param name="ignore">Ограничение, которое не считать помехой самому себе.</param>
    public static List<Conflict> Find(Sketch sk, ConstraintType type, IReadOnlyList<SkEntity> items,
                                      SkConstraint? ignore = null)
    {
        var found = new List<Conflict>();

        foreach (var c in sk.Constraints)
        {
            if (ReferenceEquals(c, ignore)) continue;
            if (!c.Enabled || !c.SameItems(items)) continue;

            if (c.Type == type)
            {
                found.Add(new Conflict
                {
                    Kind = ConflictKind.Duplicate,
                    Constraint = c,
                    Description = $"{type} is already applied to the same items."
                });
                continue;
            }

            if (Incompatible(type, c.Type))
                found.Add(new Conflict
                {
                    Kind = ConflictKind.Constraint,
                    Constraint = c,
                    Description = $"{type} contradicts {c.Type} on the same items."
                });
        }

        // Угловые размеры между теми же двумя линиями.
        double? required = RequiredAngle(type);
        if (required != null && items.Count == 2)
        {
            foreach (var d in sk.Dimensions)
            {
                if (!d.Enabled || d.Reference || d.Type != DimensionType.AngleLineLine) continue;
                if (!SameSet(d.Items, items)) continue;

                double have = Math.Abs(d.Value % 180);
                double want = required.Value;
                if (Math.Abs(have - want) < AngleTol || Math.Abs(have - (180 - want)) < AngleTol) continue;

                found.Add(new Conflict
                {
                    Kind = ConflictKind.Dimension,
                    Dimension = d,
                    Description = $"{type} requires {want:0.##}\u00b0, but the angle dimension is set to {d.Value:0.##}\u00b0."
                });
            }
        }

        return found;
    }

    /// <summary>
    /// Пересчитывает признаки конфликта по всему эскизу.
    ///
    /// Конфликт — состояние ВЫЧИСЛЯЕМОЕ, а не однажды выставленный флаг.
    /// Раньше он ставился в момент наложения и оставался навсегда: удалишь
    /// одного из спорщиков — второй так и горит красным, хотя спорить ему
    /// уже не с кем. Пересчёт снимает этот вопрос целиком.
    ///
    /// Из пары спорящих исключается тот, что добавлен ПОЗЖЕ (у него больше
    /// идентификатор). Так эскиз продолжает держаться на том, что было
    /// раньше, а не разваливается разом.
    /// </summary>
    public static void Refresh(Sketch sk)
    {
        foreach (var c in sk.Constraints) c.Conflict = false;
        foreach (var d in sk.Dimensions) d.Conflict = false;

        foreach (var c in sk.Constraints)
        {
            if (!c.Enabled || c.Conflict) continue;

            foreach (var conflict in Find(sk, c.Type, c.Items, ignore: c))
            {
                if (conflict.Constraint is { } other)
                {
                    // Проигрывает тот, кто появился позже.
                    if (other.Conflict) continue;
                    if (c.Id > other.Id) c.Conflict = true;
                    else other.Conflict = true;
                }
                else if (conflict.Dimension is { } dim)
                {
                    if (c.Id > dim.Id) c.Conflict = true;
                    else dim.Conflict = true;
                }
            }
        }
    }

    /// <summary>Угол, который ограничение навязывает паре линий. null — не навязывает.</summary>
    private static double? RequiredAngle(ConstraintType t) => t switch
    {
        ConstraintType.Parallel => 0,
        ConstraintType.Perpendicular => 90,
        _ => null
    };

    /// <summary>Пары, которые не могут действовать одновременно на одном наборе.</summary>
    private static bool Incompatible(ConstraintType a, ConstraintType b)
    {
        (ConstraintType, ConstraintType)[] pairs =
        {
            (ConstraintType.Horizontal, ConstraintType.Vertical),
            (ConstraintType.Parallel, ConstraintType.Perpendicular),
            (ConstraintType.Parallel, ConstraintType.Tangent),
            (ConstraintType.Perpendicular, ConstraintType.Tangent)
        };

        foreach (var (x, y) in pairs)
            if ((a == x && b == y) || (a == y && b == x)) return true;

        return false;
    }

    private static bool SameSet(IReadOnlyList<SkEntity> a, IReadOnlyList<SkEntity> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var e in b)
            if (!a.Contains(e)) return false;
        return true;
    }
}
