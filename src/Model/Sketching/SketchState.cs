using OpenTK.Mathematics;
using Parametric3D.Kernel;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// Снимок эскиза целиком: геометрия, ограничения, размеры и настройки.
///
/// Нужен для отмены правки. Кнопка Cancel в эскизе должна возвращать его
/// к тому виду, в котором он открывался, а откатывать по шагам нечем —
/// журнала операций в приложении нет.
///
/// Снимок ГЛУБОКИЙ. Поверхностная копия списков не годится: элементы
/// правятся на месте, а точки вообще общие у соседних отрезков, и после
/// восстановления списков геометрия осталась бы изменённой.
/// </summary>
public sealed class SketchState
{
    internal Frame3 Plane;
    internal uint BasePlaneId;
    internal string? BaseFaceTag;
    internal string PlaneDescription = "";
    internal string OriginDescription = "";
    internal string Name = "";
    internal bool SnapToGrid;
    internal double GridStep;

    internal int NextDimIndex;
    internal readonly List<SkEntity> Entities = new();
    internal readonly List<SkConstraint> Constraints = new();
    internal readonly List<SkDimension> Dimensions = new();

    /// <summary>Снимает состояние эскиза.</summary>
    public static SketchState Capture(Sketch sk)
    {
        var s = new SketchState
        {
            Plane = sk.Plane,
            BasePlaneId = sk.BasePlaneId,
            BaseFaceTag = sk.BaseFaceTag,
            PlaneDescription = sk.PlaneDescription,
            OriginDescription = sk.OriginDescription,
            Name = sk.Name,
            SnapToGrid = sk.SnapToGrid,
            GridStep = sk.GridStep,
            NextDimIndex = sk.NextDimIndex,
        };

        // Точки копируются ПЕРВЫМИ и запоминаются в карте: у соседних
        // отрезков конец общий, и копия должна сохранить это родство.
        // Скопируй точки по ходу дела — и вместо ломаной получились бы
        // отдельные отрезки.
        var map = new Dictionary<SkPoint, SkPoint>();

        foreach (var e in sk.Entities)
            foreach (var h in e.Handles)
                if (!map.ContainsKey(h)) map[h] = ClonePoint(h);

        var entityMap = new Dictionary<SkEntity, SkEntity>();

        foreach (var e in sk.Entities)
        {
            SkEntity copy = CloneEntity(e, map);
            entityMap[e] = copy;
            s.Entities.Add(copy);
        }

        foreach (var c in sk.Constraints)
        {
            var copy = new SkConstraint(c.Type)
            {
                Enabled = c.Enabled,
                IconPos = c.IconPos,
                Id = c.Id,                  // k105: номера те же — на них ссылаются (отмена в эскизе)
            };
            foreach (var it in c.Items) copy.Items.Add(Remap(it, map, entityMap));
            s.Constraints.Add(copy);
        }

        foreach (var d in sk.Dimensions)
        {
            var copy = new SkDimension(d.Type, d.Value)
            {
                Enabled = d.Enabled,
                Reference = d.Reference,
                Locked = d.Locked,
                IsDefault = d.IsDefault,
                UserPlaced = d.UserPlaced,
                UserLocked = d.UserLocked, SingleArrow = d.SingleArrow, DiameterAsLinear = d.DiameterAsLinear,
                Arrows = d.Arrows,
                TextOffset = d.TextOffset,
                Formula = d.Formula,
                Index = d.Index, Reflex = d.Reflex, Id = d.Id,
            };
            foreach (var it in d.Items) copy.Items.Add(Remap(it, map, entityMap));
            s.Dimensions.Add(copy);
        }

        return s;
    }

    /// <summary>Возвращает эскиз к снятому состоянию.</summary>
    public void RestoreInto(Sketch sk)
    {
        sk.Plane = Plane;
        sk.BasePlaneId = BasePlaneId;
        sk.BaseFaceTag = BaseFaceTag;
        sk.PlaneDescription = PlaneDescription;
        sk.OriginDescription = OriginDescription;
        sk.Name = Name;
        sk.SnapToGrid = SnapToGrid;
        sk.GridStep = GridStep;

        sk.Entities.Clear();
        sk.Entities.AddRange(Entities);
        sk.Constraints.Clear();
        sk.Constraints.AddRange(Constraints);
        sk.Dimensions.Clear();
        sk.Dimensions.AddRange(Dimensions);
        sk.NextDimIndex = Math.Max(sk.NextDimIndex, NextDimIndex);

        sk.BumpStructure();
    }

    // ------------------------------------------------------------ копирование

    private static SkPoint ClonePoint(SkPoint p) => new(p.X, p.Y)
    {
        Id = p.Id,
        Role = p.Role,
        Fixed = p.Fixed,
        Name = p.Name,
        ReferenceSource = p.ReferenceSource
    };

    private static SkPoint Get(Dictionary<SkPoint, SkPoint> map, SkPoint p) =>
        map.TryGetValue(p, out var q) ? q : ClonePoint(p);

    private static SkEntity CloneEntity(SkEntity e, Dictionary<SkPoint, SkPoint> map)
    {
        SkEntity copy = e switch
        {
            SkPoint p => Get(map, p),
            SkLine l => new SkLine(Get(map, l.A), Get(map, l.B)),
            SkCircle c => new SkCircle(Get(map, c.Center), c.Radius),
            SkArc a => new SkArc(Get(map, a.Center), Get(map, a.Start), Get(map, a.End), a.CCW),
            SkEllipse el => new SkEllipse(Get(map, el.Center), el.RU, el.RV, el.Rotation),
            SkSpline sp => new SkSpline(sp.Ctrl.Select(q => Get(map, q))),
            _ => throw new NotSupportedException($"Unknown sketch entity: {e.Kind}")
        };

        if (e is SkEllipse se && copy is SkEllipse ce) { ce.A0 = se.A0; ce.A1 = se.A1; }
        if (e is not SkPoint) copy.Id = e.Id;
        copy.Role = e.Role;
        copy.Fixed = e.Fixed;
        copy.Name = e.Name;
        copy.ReferenceSource = e.ReferenceSource;
        return copy;
    }

    private static SkEntity Remap(SkEntity item,
                                  Dictionary<SkPoint, SkPoint> points,
                                  Dictionary<SkEntity, SkEntity> entities)
    {
        if (item.Builtin) return item;                       // k96: нуль и оси эскиза — общие, не копируются
        if (item is SkPoint p) return Get(points, p);
        return entities.TryGetValue(item, out var e) ? e : item;
    }
}
