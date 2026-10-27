using OpenTK.Mathematics;

namespace Parametric3D.Model.Sketching;

public enum ConstraintType
{
    Coincident,      // две точки совпадают
    PointOnEntity,   // точка лежит на линии/дуге/окружности
    Horizontal,
    Vertical,
    Parallel,
    Perpendicular,
    Tangent,
    Equal,           // равные длины или равные радиусы
    Symmetric,       // две точки симметричны относительно линии
    MidPoint,        // точка в середине отрезка
    Concentric,
    Fix              // элемент зафиксирован
}

public enum DimensionType
{
    DistancePointPoint,
    DistancePointLine,
    DistanceLineLine,
    AngleLineLine,
    Radius,
    Diameter,
    CoordX,
    CoordY,

    /// <summary>Большая полуось эллипса.</summary>
    EllipseRadiusU,
    /// <summary>u13: длина дуги (R·|угол|).</summary>
    ArcLength = 100,
    /// <summary>Малая полуось эллипса.</summary>
    EllipseRadiusV
}

/// <summary>Где ставить стрелки размера.</summary>
public enum ArrowPlacement
{
    /// <summary>Решает приложение: не влезли внутрь — выносит наружу.</summary>
    Auto,
    Inside,
    Outside
}

public sealed class SkConstraint
{
    public uint Id { get; internal set; } = Core.Ids.New();
    public ConstraintType Type;
    /// <summary>Участники: точки и/или кривые, в зависимости от типа.</summary>
    public readonly List<SkEntity> Items = new();
    public bool Enabled = true;

    /// <summary>
    /// Ограничение конфликтует с другими и ИСКЛЮЧЕНО из решателя.
    ///
    /// Не то же самое, что Enabled: выключенное ограничение пользователь
    /// отложил сам, конфликтующее — то, которое приложение не может
    /// удовлетворить вместе с остальными. Рисуется красным.
    /// </summary>
    public bool Conflict;

    /// <summary>k96: показан красным как участник противоречия последней правки (пока открыто объяснение).</summary>
    public bool Flagged;
    /// <summary>k123: подсвечен наведением на строку таблицы «Разрешить эскиз».</summary>
    public bool Highlight;

    /// <summary>Позиция значка на эскизе (задаётся пользователем при перетаскивании).</summary>
    public Vector2d IconPos;

    /// <summary>
    /// Где оказались значки этого ограничения при последней отрисовке.
    /// Их несколько: у Parallel, Equal и им подобных значок ставится
    /// на КАЖДЫЙ участник. Кэш нужен выбору мышью.
    /// </summary>
    public readonly List<Vector2d> GlyphPositions = new();

    /// <summary>Тот же набор участников, без учёта порядка.</summary>
    public bool SameItems(IReadOnlyList<SkEntity> other)
    {
        if (other.Count != Items.Count) return false;
        foreach (var e in other)
            if (!Items.Contains(e)) return false;
        return true;
    }

    public SkConstraint(ConstraintType t, params SkEntity[] items)
    {
        Type = t;
        Items.AddRange(items);
    }
}

/// <summary>
/// Размер эскиза. Значение может быть числом или ФОРМУЛОЙ из таблицы
/// параметров документа — именно так параметризация «протекает» из
/// таблицы в геометрию.
/// </summary>
public sealed class SkDimension
{
    public uint Id { get; internal set; } = Core.Ids.New();
    /// <summary>k102: номер размера в эскизе (идентификатор dN; нумерация каждого эскиза — с нуля); -1 — ещё не задан.</summary>
    public int Index = -1;
    /// <summary>Идентификатор размера в эскизе: «d0», «d1»…</summary>
    public string Name => Index >= 0 ? $"d{Index}" : "d?";
    public DimensionType Type;
    public readonly List<SkEntity> Items = new();

    /// <summary>Текущее числовое значение (мм или градусы).</summary>
    public double Value;
    /// <summary>Если задана — Value пересчитывается из формулы при каждом обновлении.</summary>
    public string? Formula;
    /// <summary>k118: описание размера (окно «Параметры», колонка «Описание»); сохраняется в файле.</summary>
    public string? Note;
    /// <summary>Размер справочный: не управляет геометрией, только показывает её.</summary>
    public bool Reference;

    /// <summary>
    /// Размер конфликтует с ограничениями и исключён из решателя.
    /// Рисуется красным.
    /// </summary>
    public bool Conflict;

    /// <summary>k96: показан красным как участник противоречия последней правки (пока открыто объяснение).</summary>
    public bool Flagged;

    /// <summary>
    /// Где стрелки: внутри замера или снаружи. В режиме Auto они выносятся
    /// наружу, когда между выносными линиями не помещаются ни стрелки,
    /// ни подпись.
    /// </summary>
    public ArrowPlacement Arrows = ArrowPlacement.Auto;

    /// <summary>
    /// Размер поставлен приложением, чтобы эскиз был определён полностью.
    ///
    /// Он не заблокирован и меняется как обычный, но рисуется серым:
    /// это подсказка «вот чем сейчас держится фигура», а не решение
    /// конструктора. Как только пользователь задаёт своё, набор
    /// пересобирается и лишние такие размеры исчезают.
    /// </summary>
    public bool IsDefault;

    /// <summary>
    /// Пользователь сам поставил подпись мышью. Пока этого не произошло,
    /// размер сам выбирает сторону — снаружи фигуры.
    /// </summary>
    public bool UserPlaced;

    /// <summary>
    /// Размер УПРАВЛЯЕТ эскизом: решатель обязан его выдержать.
    ///
    /// Блокировка и «управление» — это одно и то же, и разводить их было
    /// ошибкой: заблокированное значение по смыслу и есть ограничение.
    /// Незаблокированный размер эскизом не управляет, а измеряет его —
    /// значение следует за геометрией.
    /// </summary>
    public bool Locked;

    /// <summary>Размер управляет геометрией и участвует в решении.</summary>
    public bool IsDriving => Enabled && Locked && !Reference && !Conflict;

    /// <summary>
    /// u12: ЗАМОК (оранжевый): сильный размер, который не меняется ни перетаскиванием геометрии, ни «сделать слабым»;
    /// значение правится только после снятия замка. Слабый (серый) — IsDefault, сильный (голубой) — заданный
    /// пользователем (Locked: управляет эскизом).
    /// </summary>
    public bool UserLocked;
    /// <summary>u12: радиус/диаметр — одной стрелкой (от центра к окружности или снаружи).</summary>
    public bool SingleArrow;
    /// <summary>u12: диаметр — как обычный размер между двумя точками окружности (размерная линия с выносными).</summary>
    public bool DiameterAsLinear;
    public bool Enabled = true;

    /// <summary>Смещение текста размера от измеряемой геометрии.</summary>
    public Vector2d TextOffset = new(0, 12);

    /// <summary>
    /// Где в координатах эскиза оказался центр подписи при последней
    /// отрисовке. Кэш заполняет DimensionRenderer.
    ///
    /// Хранить это в модели не идеально, но альтернатива — повторять
    /// весь расчёт положения подписи ещё и в процедуре выбора мышью,
    /// и следить, чтобы две копии не разошлись. Размер рисуется каждый
    /// кадр, поэтому значение всегда свежее.
    /// </summary>
    public Vector2d TextCenter;

    /// <summary>
    /// Угловой размер показан дополняющей дугой: в подписи стоит 360 минус
    /// угол. Признак только отображения — решателем управляет Value,
    /// то есть сам угол между прямыми. Ставится при отрисовке по тому,
    /// в какую сторону от угла утащена подпись.
    /// </summary>
    public bool Reflex;
    /// <summary>k123: подсвечен наведением на строку таблицы «Разрешить эскиз».</summary>
    public bool Highlight;
    /// <summary>
    /// k123: угловой размер показан в смежном секторе (между лучом одной прямой и продолжением другой): в подписи 180
    /// минус угол. Только отображение и ввод — решателем управляет Value (угол между прямыми).
    /// </summary>
    public bool Supplement;
    /// <summary>Подпись углового размера: что видит пользователь (смежный сектор — 180 − угол, дополняющая дуга — 360 − угол).</summary>
    public double ShownAngle => Supplement ? 180 - Value : Reflex ? 360 - Value : Value;
    /// <summary>Разбор (k97): стрелки размера при последнем рисовании — снаружи.</summary>
    public bool LastArrowsOutside;
    public string? Error;

    public SkDimension(DimensionType t, double value, params SkEntity[] items)
    {
        Type = t; Value = value; Items.AddRange(items);
    }

    public bool IsAngular => Type == DimensionType.AngleLineLine;

    /// <summary>Размер, который рисуется лучом из центра: радиус, диаметр, полуоси эллипса.</summary>
    public bool IsRadial => Type is DimensionType.Radius or DimensionType.Diameter
                                 or DimensionType.EllipseRadiusU or DimensionType.EllipseRadiusV;

    public string DisplayText()
    {
        if (Error != null) return "?";
        var s = Core.Settings.Current;
        return IsAngular ? s.FormatAngle(Value) : s.FormatLength(Math.Abs(Value));
    }
}

/// <summary>
/// Измерение размера по текущей геометрии.
///
/// Нужно тем размерам, которые НЕ управляют эскизом, а показывают его:
/// справочным и поставленным по умолчанию. После каждого решения их
/// значения пересчитываются, иначе на экране висели бы числа от прошлого
/// положения.
/// </summary>
public static class DimensionMeasure
{
    /// <summary>Текущее значение размера. NaN — измерить нечем.</summary>
    public static double Value(SkDimension d)
    {
        var it = d.Items;

        switch (d.Type)
        {
            case DimensionType.DistancePointPoint:
                if (it.Count >= 2 && it[0] is SkPoint a && it[1] is SkPoint b)
                    return (a.P - b.P).Length;
                break;

            case DimensionType.DistancePointLine:
                if (it.Count >= 2 && it[0] is SkPoint p && it[1] is SkLine l)
                    return Math.Abs(PointToLine(p.P, l));
                break;

            case DimensionType.DistanceLineLine:
                if (it.Count >= 2 && it[0] is SkLine l1 && it[1] is SkLine l2)
                {
                    if (LineLineOff) return Math.Abs(PointToLine(l2.A.P, l1));
                    var (pt, rf) = LineLinePair(l1, l2);
                    return Math.Abs(PointToLine(pt, rf));
                }
                break;

            case DimensionType.AngleLineLine:
                if (it.Count >= 2 && it[0] is SkLine a1 && it[1] is SkLine a2)
                    return LineAngleDegrees(a1, a2);
                break;

            case DimensionType.Radius:
                if (it.Count >= 1 && it[0] is SkCircle rc) return rc.Radius;
                if (it.Count >= 1 && it[0] is SkArc ra) return ra.Radius;
                break;

            case DimensionType.Diameter:
                if (it.Count >= 1 && it[0] is SkCircle dc) return dc.Radius * 2;
                if (it.Count >= 1 && it[0] is SkArc da) return da.Radius * 2;
                break;

            case DimensionType.ArcLength:
                if (it.Count >= 1 && it[0] is SkArc al) return al.Radius * Math.Abs(al.Sweep);
                break;

            case DimensionType.EllipseRadiusU:
                if (it.Count >= 1 && it[0] is SkEllipse eu) return eu.RU;
                break;

            case DimensionType.EllipseRadiusV:
                if (it.Count >= 1 && it[0] is SkEllipse ev) return ev.RV;
                break;

            case DimensionType.CoordX:
                if (it.Count >= 1 && it[0] is SkPoint cx) return cx.X;
                break;

            case DimensionType.CoordY:
                if (it.Count >= 1 && it[0] is SkPoint cy) return cy.Y;
                break;
        }

        return double.NaN;
    }

    /// <summary>
    /// k124 (замечание 27.10, 23:41): РАССТОЯНИЕ МЕЖДУ ПРЯМЫМИ — от СЕРЕДИНЫ отрезка до другой прямой (ось эскиза или более
    /// длинный отрезок). Прежде мерилось от начала второго элемента до первого: у размера «отрезок — ось» это конец оси за
    /// 100 м, и наклон короткого отрезка на 10⁻⁵ давал десятки миллиметров — сильный размер не держал отрезок (эскиз
    /// пользователя: 131.68 при настоящих 84.40), решение не сходилось, отрезок стягивался в точку, угол к оси
    /// «конфликтовал». Одна формула для измерения, решателя и рисования. P3D_SKLINELINE=0 — прежняя.
    /// </summary>
    public static (Vector2d Point, SkLine Reference) LineLinePair(SkLine a, SkLine b)
    {
        double la = a.Builtin ? double.MaxValue : (a.B.P - a.A.P).Length;
        double lb = b.Builtin ? double.MaxValue : (b.B.P - b.A.P).Length;
        var (from, rf) = la <= lb ? (a, b) : (b, a);
        return ((from.A.P + from.B.P) * 0.5, rf);
    }
    public static readonly bool LineLineOff = Environment.GetEnvironmentVariable("P3D_SKLINELINE") == "0";

    private static double PointToLine(Vector2d p, SkLine l)
    {
        Vector2d d = l.B.P - l.A.P;
        double len = d.Length;
        if (len < 1e-12) return (p - l.A.P).Length;
        return ((p.X - l.A.P.X) * d.Y - (p.Y - l.A.P.Y) * d.X) / len;
    }

    /// <summary>
    /// Лучи, между которыми меряется угол: из точки пересечения прямых
    /// к ДАЛЬНИМ концам каждого отрезка.
    ///
    /// Именно эти лучи рисует размерная дуга, поэтому и величина обязана
    /// считаться по ним. Раньше угол брался между направлениями самих
    /// отрезков — а у ломаной оба направления смотрят «вперёд», и почти
    /// прямой участок давал не 177°, а 3°: дуга показывала одно, число
    /// говорило другое.
    /// </summary>
    public static bool AngleRays(SkLine a, SkLine b,
                                 out Vector2d corner, out Vector2d rayA, out Vector2d rayB)
    {
        corner = Vector2d.Zero;
        rayA = new Vector2d(1, 0);
        rayB = new Vector2d(1, 0);

        Vector2d da = a.B.P - a.A.P, db = b.B.P - b.A.P;
        if (da.Length < 1e-12 || db.Length < 1e-12) return false;

        double den = da.X * db.Y - da.Y * db.X;
        if (Math.Abs(den) < 1e-12) return false;              // параллельны

        Vector2d r = b.A.P - a.A.P;
        double t = (r.X * db.Y - r.Y * db.X) / den;
        corner = a.A.P + da * t;

        rayA = AxisRay(a) ?? Farthest(corner, a);
        rayB = AxisRay(b) ?? Farthest(corner, b);
        return true;
    }

    /// <summary>
    /// k124: у ОСИ ЭСКИЗА луч — её собственное направление (+X, +Y), а не к дальнему концу. Концы оси — за 100 м по обе
    /// стороны нуля, и «дальний» менялся, когда точка пересечения переходила через ноль: угол к оси скачком становился
    /// 180 − угол, решение такого размера не сходилось («угол к оси конфликтует»). P3D_SKAXISRAY=0 — прежнее. Старые
    /// размеры при чтении файла переводятся (MigrateAxisAngles) — геометрия та же.
    /// </summary>
    static Vector2d? AxisRay(SkLine l) => AxisRayOff || !l.Builtin ? null : (l.B.P - l.A.P).Normalized();
    public static readonly bool AxisRayOff = Environment.GetEnvironmentVariable("P3D_SKAXISRAY") == "0";

    /// <summary>Угол прямых по прежнему правилу (лучи к дальним концам, у оси тоже) — для перевода старых размеров.</summary>
    public static double LegacyLineAngleDegrees(SkLine a, SkLine b)
    {
        Vector2d da = a.B.P - a.A.P, db = b.B.P - b.A.P;
        double den = da.X * db.Y - da.Y * db.X;
        if (da.Length < 1e-12 || db.Length < 1e-12 || Math.Abs(den) < 1e-12) return LineAngleDegrees(a, b);
        double t = ((b.A.P - a.A.P).X * db.Y - (b.A.P - a.A.P).Y * db.X) / den;
        var c = a.A.P + da * t;
        var ra = Farthest(c, a); var rb = Farthest(c, b);
        return Math.Abs(Math.Atan2(ra.X * rb.Y - ra.Y * rb.X, Vector2d.Dot(ra, rb)) * 180.0 / Math.PI);
    }

    /// <summary>
    /// Угловые размеры к осям эскиза из файла прежнего правила: значение по прежнему правилу — переводится в новое (180 −
    /// угол, где «дальний» конец оси был отрицательным), чтобы решатель не перевернул отрезок. Возвращает число переводов.
    /// </summary>
    public static int MigrateAxisAngles(Sketch sk)
    {
        if (AxisRayOff) return 0;
        int n = 0;
        foreach (var d in sk.Dimensions)
        {
            if (d.Type != DimensionType.AngleLineLine || d.Items.Count < 2 || d.Items[0] is not SkLine a || d.Items[1] is not SkLine b || !(a.Builtin || b.Builtin)) continue;
            double now = LineAngleDegrees(a, b), old = LegacyLineAngleDegrees(a, b);
            if (Math.Abs(now - old) < 1e-9) continue;
            // значение — по прежнему правилу: в новом то же положение — дополнение до 180
            if (Math.Abs(d.Value - old) < Math.Abs(d.Value - now) + 1e-9) { d.Value = 180 - d.Value; if (d.Formula is { } f && !string.IsNullOrWhiteSpace(f)) d.Formula = "180 - (" + f + ")"; d.Supplement = !d.Supplement; n++; }
        }
        return n;
    }

    private static Vector2d Farthest(Vector2d c, SkLine l)
    {
        Vector2d p = l.A.P - c, q = l.B.P - c;
        Vector2d far = p.LengthSquared >= q.LengthSquared ? p : q;
        return far.Length < 1e-12 ? new Vector2d(1, 0) : far.Normalized();
    }

    /// <summary>
    /// Угол между двумя прямыми в градусах, БЕЗ знака, в пределах 0..180.
    ///
    /// Единственная формула угла во всём приложении — и решатель, и подбор
    /// размеров, и отрисовка обязаны звать именно её. Разойдись они хоть
    /// в мелочи, и размер начнёт двигать геометрию.
    ///
    /// Знак не используется намеренно: он зависит от того, в какую сторону
    /// пользователь нарисовал отрезки, а угол между прямыми от этого
    /// зависеть не должен.
    /// </summary>
    public static double LineAngleDegrees(SkLine a, SkLine b)
    {
        if (!AngleRays(a, b, out _, out Vector2d ra, out Vector2d rb))
        {
            // Параллельные: угол либо 0, либо 180 — по взаимному направлению.
            Vector2d da = (a.B.P - a.A.P);
            Vector2d db = (b.B.P - b.A.P);
            if (da.Length < 1e-12 || db.Length < 1e-12) return 0;
            return Vector2d.Dot(da.Normalized(), db.Normalized()) >= 0 ? 0 : 180;
        }

        return Math.Abs(Math.Atan2(ra.X * rb.Y - ra.Y * rb.X,
                                   Vector2d.Dot(ra, rb)) * 180.0 / Math.PI);
    }

}
