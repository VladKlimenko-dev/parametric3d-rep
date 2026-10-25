using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model.Params;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// Эскиз: плоскость + плоские элементы + ограничения + размеры.
///
/// Эскиз НЕ рисует себя сам и не хранит ни матриц, ни буферов. Он отдаёт
/// плоские ломаные (Tessellate) для рендера и трёхмерные кривые (ToProfile)
/// для ядра. Всё остальное — снаружи.
/// </summary>
public sealed class Sketch : ModelItem
{
    public override ItemKind Kind => ItemKind.Sketch;

    /// <summary>Плоскость эскиза в координатах детали.</summary>
    public Frame3 Plane = Frame3.XY;

    /// <summary>Название плоскости-базиса для окна настроек.</summary>
    public string PlaneDescription = "XY";

    /// <summary>
    /// Система координат плоскости-базиса КАК ОНА ЕСТЬ, без поворота
    /// на вектор «вверх». От неё отсчитывается ориентация эскиза.
    ///
    /// Хранится отдельно от Plane, потому что иначе повороты накапливались
    /// бы: задав «вверх» дважды, пользователь получил бы сумму поворотов
    /// вместо заданного направления.
    /// </summary>
    public Frame3 BaseFrame = Frame3.XY;

    /// <summary>
    /// Направление «вверх» на эскизе, выраженное в осях плоскости-базиса.
    /// (0, 1) — как у самой плоскости.
    /// </summary>
    public Vector2d UpVector = new(0, 1);

    /// <summary>Откуда взята нулевая точка. Пусто — геометрический центр.</summary>
    public string OriginDescription = "Geometric center";

    /// <summary>Настройка эскиза: точка-основа нулевой точки (в мире) и смещение нуля от неё в осях эскиза.</summary>
    public Vector3d? OriginBase;
    public Vector2d OriginOffset;

    /// <summary>Настройка эскиза: чем задан «верх» (точка, ребро, грань, плоскость); пусто — осью плоскости-базиса.</summary>
    public string UpDescription = "";

    /// <summary>Нулевая точка — от основы со смещением (в осях эскиза); основы нет — на месте.</summary>
    public void SetOriginOffset(Vector2d offset)
    {
        OriginOffset = offset;
        if (OriginBase is not { } b) return;
        var at = Plane.ToWorld(Plane.ToLocal2(b) + offset);
        Plane = new Frame3(at, Plane.U, Plane.V);
        BumpStructure();
        NotifyChanged(this);
    }

    /// <summary>«Верх» эскиза по направлению в мире (проекция на плоскость-базис); вырождено — false.</summary>
    public bool SetUpDirection(Vector3d worldDir, string description)
    {
        var u = BaseFrame.U; var v = BaseFrame.V;
        var d = new Vector2d(Vector3d.Dot(worldDir, u), Vector3d.Dot(worldDir, v));
        if (d.Length < 1e-9) return false;
        SetUpVector(d);
        UpDescription = description;
        return true;
    }

    /// <summary>
    /// Взгляд с обратной стороны плоскости-базиса.
    ///
    /// Разворачивается нормаль и вместе с ней одна из осей в плоскости.
    /// Только нормаль менять нельзя: система координат стала бы левой,
    /// и все построения оказались бы зеркальными.
    /// </summary>
    public void Flip()
    {
        Plane = new Frame3(Plane.Origin, Plane.V, Plane.U);
        // k101 (замечание 2): ГЕОМЕТРИЯ ОСТАЁТСЯ НА МЕСТЕ В МИРЕ. Оси эскиза меняются местами — прежде плоские координаты
        // оставались прежними, и эскиз (с рамкой настройки) зеркально «улетал» в сторону; основа не переворачивалась, и
        // следующий «верх» возвращал прежнюю сторону. Теперь координаты меняются местами (x ↔ y), основа — тоже.
        if (!FlipKeepOff)
        {
            BaseFrame = new Frame3(BaseFrame.Origin, BaseFrame.V, BaseFrame.U);
            UpVector = new Vector2d(UpVector.Y, UpVector.X);
            OriginOffset = new Vector2d(OriginOffset.Y, OriginOffset.X);
            SwapAxes();
        }
        PlaneDescription = PlaneDescription.EndsWith(" (flipped)")
            ? PlaneDescription[..^10]
            : PlaneDescription + " (flipped)";
        BumpStructure();
        NotifyChanged(this);
    }

    public static readonly bool FlipKeepOff = Environment.GetEnvironmentVariable("P3D_SKFLIPKEEP") == "0";

    /// <summary>Плоские координаты x ↔ y (зеркало по диагонали): точки, обход дуг, эллипсы, «горизонтально/вертикально»,
    /// координатные размеры, ссылки на оси эскиза.</summary>
    private void SwapAxes()
    {
        var builtin = new HashSet<SkEntity>(Builtins, ReferenceEqualityComparer.Instance);
        var done = new HashSet<SkPoint>(ReferenceEqualityComparer.Instance);
        foreach (var p in AllPoints())
        {
            if (builtin.Contains(p) || p.Owner is { } o && builtin.Contains(o) || !done.Add(p)) continue;
            (p.X, p.Y) = (p.Y, p.X);
        }
        foreach (var e in Entities)
        {
            if (e is SkArc a) a.CCW = !a.CCW;
            if (e is SkEllipse el) { el.Rotation = Math.PI / 2 - el.Rotation; (el.A0, el.A1) = (-el.A1, -el.A0); }
        }
        SkEntity Map(SkEntity x) => ReferenceEquals(x, AxisH) ? AxisV : ReferenceEquals(x, AxisV) ? AxisH : x;
        foreach (var c in Constraints)
        {
            c.Type = c.Type switch { ConstraintType.Horizontal => ConstraintType.Vertical, ConstraintType.Vertical => ConstraintType.Horizontal, var t => t };
            for (int i = 0; i < c.Items.Count; i++) c.Items[i] = Map(c.Items[i]);
        }
        foreach (var d in Dimensions)
        {
            d.Type = d.Type switch { DimensionType.CoordX => DimensionType.CoordY, DimensionType.CoordY => DimensionType.CoordX, var t => t };
            for (int i = 0; i < d.Items.Count; i++) d.Items[i] = Map(d.Items[i]);
            d.TextOffset = new Vector2d(d.TextOffset.Y, d.TextOffset.X);
            d.TextCenter = new Vector2d(d.TextCenter.Y, d.TextCenter.X);
        }
    }

    /// <summary>
    /// Ставит новую плоскость-базис. Нулевая точка при этом уходит
    /// в геометрический центр плоскости — иначе эскиз оказался бы
    /// отсчитанным от места, к новой плоскости отношения не имеющего.
    /// </summary>
    public void SetBasePlane(Frame3 frame, uint sourceId, string description)
    {
        BaseFrame = frame;
        Plane = frame;
        UpVector = new Vector2d(0, 1);
        BasePlaneId = sourceId;
        BaseFaceTag = null;
        PlaneDescription = description;
        OriginDescription = "Geometric center";
        BumpStructure();
        NotifyChanged(this);
    }

    /// <summary>
    /// Переносит нулевую точку эскиза в указанную точку мира,
    /// проецируя её на плоскость эскиза.
    /// </summary>
    /// <summary>
    /// Задаёт направление «вверх» и поворачивает оси эскиза.
    ///
    /// ЧТО ПРОИСХОДИТ С ГЕОМЕТРИЕЙ. Плоские координаты нарисованного
    /// НЕ меняются, поэтому на эскизе всё выглядит по-прежнему: размеры,
    /// углы и ограничения заданы в этих же координатах и остаются верными.
    /// Меняется положение эскиза в пространстве — тело, построенное по нему,
    /// окажется повёрнутым.
    ///
    /// А вот ссылочная геометрия привязана к чужим телам и обязана остаться
    /// на месте В МИРЕ. Её плоские координаты поворачиваются на обратный
    /// угол — ровно настолько, чтобы мировое положение не сдвинулось.
    /// Поэтому спроецированная фигура после смены «верха» окажется
    /// на эскизе повёрнутой, как и должно быть.
    /// </summary>
    public void SetUpVector(Vector2d up)
    {
        if (up.LengthSquared < 1e-12) return;

        up = up.Normalized();

        // Угол, на который надо повернуть оси, чтобы заданное направление
        // стало вертикалью.
        double delta = Math.Atan2(up.Y, up.X) - Math.PI / 2;

        double c = Math.Cos(delta), s = Math.Sin(delta);

        Vector3d u = BaseFrame.U * c + BaseFrame.V * s;
        Vector3d v = BaseFrame.U * -s + BaseFrame.V * c;

        // Начало координат сохраняется: «вверх» задаёт поворот, а не сдвиг.
        Plane = new Frame3(Plane.Origin, u, v);
        UpVector = up;

        RotateReferenceGeometry(-delta);

        BumpStructure();
        NotifyChanged(this);
    }

    /// <summary>
    /// Поворачивает плоские координаты ссылочной геометрии, чтобы её
    /// положение в мире не изменилось.
    /// </summary>
    private void RotateReferenceGeometry(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);

        var moved = new HashSet<SkPoint>();

        foreach (var e in Entities)
        {
            if (e.Role != SkRole.Reference) continue;

            foreach (var h in e.Handles)
            {
                if (!moved.Add(h)) continue;
                double x = h.X, y = h.Y;
                h.X = x * c - y * s;
                h.Y = x * s + y * c;
            }

            if (e is SkEllipse el) el.Rotation += angle;
        }
    }

    public void SetOrigin(Vector3d worldPoint, string description)
    {
        OriginBase = worldPoint;
        Vector2d uv = Plane.ToLocal2(worldPoint) + OriginOffset;
        Vector3d moved = Plane.ToWorld(uv);

        Plane = new Frame3(moved, Plane.U, Plane.V);
        OriginDescription = description;
        BumpStructure();
        NotifyChanged(this);
    }
    /// <summary>
    /// Базовая плоскость или грань, на которой построен эскиз.
    /// Хранится и для перестроения, и чтобы окно настроек могло показать,
    /// на чём эскиз стоит.
    /// </summary>
    public uint BasePlaneId;
    /// <summary>KRN-08: устойчивое имя плоской грани модели, на которой стоит эскиз (null — не на грани).</summary>
    public string? BaseFaceTag;
    /// <summary>Ссылки на референсную геометрию, спроецированную в эскиз.</summary>
    public readonly List<uint> ReferenceIds = new();

    public readonly List<SkEntity> Entities = new();

    // ------------------------------------------------------------ k96: встроенные ссылки — нуль и оси эскиза

    /// <summary>Полудлина отрезков осей (для выбора мышью и привязки; в уравнениях ось — бесконечная прямая).</summary>
    public const double AxisHalfLength = 1e5;
    /// <summary>Номера встроенных ссылок в файле (элементов с такими номерами в файле нет).</summary>
    public const uint OriginSaveId = 0xFFFF_FF01, AxisHSaveId = 0xFFFF_FF02, AxisVSaveId = 0xFFFF_FF03;
    private SkPoint? _origin; private SkLine? _axisH, _axisV;

    static SkPoint BuiltinPoint(double x, double y, string name) => new(x, y) { Role = SkRole.Reference, Fixed = true, Builtin = true, Name = name, ReferenceSource = name };
    static SkLine BuiltinAxis(bool horizontal, string name)
    {
        double L = AxisHalfLength;
        var l = horizontal ? new SkLine(BuiltinPoint(-L, 0, name), BuiltinPoint(L, 0, name)) : new SkLine(BuiltinPoint(0, -L, name), BuiltinPoint(0, L, name));
        l.Role = SkRole.Reference; l.Fixed = true; l.Builtin = true; l.Name = name; l.ReferenceSource = name;
        return l;
    }

    /// <summary>Нулевая точка эскиза (0, 0) — встроенная ссылка.</summary>
    public SkPoint OriginPoint => _origin ??= BuiltinPoint(0, 0, "Начало координат эскиза");
    /// <summary>Горизонтальная ось эскиза (y = 0) — встроенная ссылка.</summary>
    public SkLine AxisH => _axisH ??= BuiltinAxis(true, "Горизонтальная ось эскиза");
    /// <summary>Вертикальная ось эскиза (x = 0) — встроенная ссылка.</summary>
    public SkLine AxisV => _axisV ??= BuiltinAxis(false, "Вертикальная ось эскиза");
    /// <summary>Нуль и обе оси.</summary>
    public IEnumerable<SkEntity> Builtins { get { yield return OriginPoint; yield return AxisH; yield return AxisV; } }

    public SkEntity? BuiltinBySaveId(uint id) => id switch { OriginSaveId => OriginPoint, AxisHSaveId => AxisH, AxisVSaveId => AxisV, _ => null };
    public uint SaveIdOf(SkEntity e) => ReferenceEquals(e, _origin) ? OriginSaveId : ReferenceEquals(e, _axisH) ? AxisHSaveId : ReferenceEquals(e, _axisV) ? AxisVSaveId : e.Id;
    public readonly List<SkConstraint> Constraints = new();
    public readonly List<SkDimension> Dimensions = new();

    public bool SnapToGrid;
    public double GridStep = 5.0;

    /// <summary>Состояние последнего решения системы ограничений.</summary>
    public SolveStatus LastSolve = SolveStatus.Ok;
    public int DegreesOfFreedom;
    /// <summary>
    /// k97 (замечание 7): степени свободы, НЕ закрытые и слабыми размерами. Слабые (приложением поставленные) размеры в
    /// решение не входят, и DegreesOfFreedom их не учитывает: эскиз, полностью образмеренный слабыми размерами, в дереве
    /// значился «не полностью определённым» (значок с крестиком), а «Разрешение эскиза» — «конфликтов нет». Каждый слабый
    /// размер подбирается независимым (снимает ровно одну степень). Мутация P3D_SKWEAKDOF=0 — прежний счёт.
    /// </summary>
    public int FreeDegrees => WeakDofOff ? DegreesOfFreedom : Math.Max(0, DegreesOfFreedom - Dimensions.Count(d => d.Enabled && d.IsDefault && !d.Reference));
    public static readonly bool WeakDofOff = Environment.GetEnvironmentVariable("P3D_SKWEAKDOF") == "0";

    public Sketch(string name, Frame3 plane)
    {
        Name = name;
        Plane = plane;
    }

    // ------------------------------------------------------------ построение

    /// <summary>
    /// Допуск привязки в единицах модели. Вид пересчитывает его из пикселей
    /// при каждом кадре: «рядом» — понятие экранное, а не модельное.
    /// </summary>
    public double AutoTolerance = 0.5;

    /// <summary>
    /// Счётчик изменений СОСТАВА эскиза: добавления и удаления элементов,
    /// ограничений и размеров. Перемещение точек его не меняет.
    ///
    /// По нему вид понимает, когда пора пересобрать размеры по умолчанию:
    /// делать это на каждое движение мыши было бы разорительно, а после
    /// каждой правки состава — необходимо.
    /// </summary>
    public int StructureVersion { get; private set; }

    public void BumpStructure() => StructureVersion++;

    /// <summary>k109 (11:11): плоскость эскиза параллельна плоскости other (такой эскиз виден и служит ссылкой при правке).</summary>
    public bool IsParallelTo(Sketch other) => !ReferenceEquals(this, other) && Math.Abs(OpenTK.Mathematics.Vector3d.Dot(Plane.W.Normalized(), other.Plane.W.Normalized())) > 1 - 1e-6;
    public static readonly bool ParallelGhostOff = Environment.GetEnvironmentVariable("P3D_SKGHOST") == "0";

    /// <summary>
    /// Пересчитывает значения размеров, которые эскизом не управляют:
    /// справочных и поставленных по умолчанию. Они показывают геометрию,
    /// а не задают её, поэтому обязаны следовать за ней.
    /// </summary>
    public void RefreshMeasuredDimensions()
    {
        foreach (var d in Dimensions)
        {
            if (d.IsDriving) continue;      // управляющий задаёт значение сам

            double v = DimensionMeasure.Value(d);
            if (!double.IsNaN(v)) d.Value = v;
        }
    }

    /// <param name="autoConstrain">
    /// Разобрать элемент и навесить очевидные привязки. Инструменты рисования
    /// передают true; построения вроде скругления — нет, они ставят свои
    /// ограничения сами и точнее.
    /// </param>
    public T AddEntity<T>(T e, bool autoConstrain = false) where T : SkEntity
    {
        Entities.Add(e);
        BumpStructure();
        if (autoConstrain) AutoConstrain.Apply(this, e);
        NotifyChanged(this);
        return e;
    }

    public void RemoveEntity(SkEntity e)
    {
        // Вместе с элементом уходят и его УЗЛЫ, если больше никто их
        // не держит. Раньше убирались только ограничения и размеры,
        // ссылавшиеся на сам элемент, а те, что ссылались на его концы,
        // оставались висеть: удалишь сегмент ломаной — а размер до его
        // бывшего узла продолжает жить на чертеже.
        var points = e.Handles.ToList();
        Entities.Remove(e);

        var orphans = new HashSet<SkPoint>(
            points.Where(p => !Entities.Any(other => other.Handles.Contains(p))));

        bool Touches(IReadOnlyList<SkEntity> items) =>
            items.Contains(e) || items.Any(i => i is SkPoint sp && orphans.Contains(sp));

        Constraints.RemoveAll(c => Touches(c.Items));
        Dimensions.RemoveAll(d => Touches(d.Items));

        BumpStructure();
        NotifyChanged(this);
    }

    public SkConstraint AddConstraint(ConstraintType t, params SkEntity[] items)
    {
        // k101 (замечание 3): совпадение точек, уже совпадающих через другие связи (цепочка через нуль эскиза), — лишнее
        // уравнение: решатель помечал его красным. Не добавляется — возвращается связь, которая их уже соединяет.
        if (t == ConstraintType.Coincident && items.Length == 2 && !DupCoincidentOff && CoincidentPath(items[0], items[1]) is { } have) return have;
        var c = new SkConstraint(t, items);
        Constraints.Add(c);
        BumpStructure();
        NotifyChanged(this);
        return c;
    }

    /// <summary>k102: следующий номер размера (номера не переиспользуются после удаления).</summary>
    public int NextDimIndex;

    /// <summary>Размерам без номера — номера по порядку (поставленные приложением добавляются в список напрямую).</summary>
    public void EnsureDimIds()
    {
        foreach (var d in Dimensions)
        {
            if (d.Index >= NextDimIndex) NextDimIndex = d.Index + 1;
        }
        foreach (var d in Dimensions) if (d.Index < 0) d.Index = NextDimIndex++;
    }

    /// <summary>Размер по идентификатору «dN»; null — нет.</summary>
    public SkDimension? DimensionByName(string name) => Params.ModelRefs.DimIndex(name) is var i && i >= 0 ? Dimensions.FirstOrDefault(d => d.Index == i) : null;

    /// <summary>
    /// k102: ФОРМУЛА В ЭСКИЗЕ — свои размеры по идентификатору (d0, d1…), затем параметры (и пути к размерам других эскизов,
    /// операций и деталей — через таблицу). Размер с формулой берёт значение своей формулы (цепочки — до 8 шагов).
    /// </summary>
    public bool TryEvaluate(string formula, ParameterTable? table, out double value, out string? error)
    {
        EnsureDimIds();
        var scope = new SketchScope(this, table);
        return ParameterTable.TryEvaluateWith(formula, scope, table?.Geometry, out value, out error);
    }

    sealed class SketchScope : IValueResolver
    {
        readonly Sketch _sk; readonly ParameterTable? _t; int _depth;
        public SketchScope(Sketch sk, ParameterTable? t) { _sk = sk; _t = t; }
        public bool TryValue(string name, out double value)
        {
            if (_sk.DimensionByName(name) is { } d)
            {
                value = d.Value;
                if (!string.IsNullOrWhiteSpace(d.Formula) && _depth < 8)
                {
                    _depth++;
                    try { if (ParameterTable.TryEvaluateWith(d.Formula!, this, _t?.Geometry, out double v, out _)) value = v; else return false; }
                    finally { _depth--; }
                }
                return true;
            }
            if (_t != null) return _t.TryValue(name, out value);
            value = 0; return false;
        }
    }

    /// <summary>k103: таблица, по которой последний раз вычислялись формулы размеров (имена вне эскиза — по ней).</summary>
    public ParameterTable? FormulaScope;
    /// <summary>Решение при перетаскивании: слабые размеры в формулах — по текущей геометрии.</summary>
    private bool _liveMeasure;
    public static readonly bool FormulaLiveOff = Environment.GetEnvironmentVariable("P3D_SKFORMULALIVE") == "0";

    /// <summary>
    /// k103 (замечание 2): ФОРМУЛА РАЗМЕРА В РЕШАТЕЛЕ. Формула ссылается на размеры этого же эскиза — значение размера
    /// вычисляется по текущей геометрии: слабый (измеряющий) размер — измерением, управляющий — своим значением, размер
    /// с формулой — его формулой. Решатель держит размер равным формуле, поэтому при перетаскивании точки отрезка со
    /// слабым размером d1 отрезок с размером d2 = d1 + 5 меняется вместе с ним. null — формулы нет или в ней нет своих
    /// размеров (её значение уже подставлено в размер). Мутация P3D_SKFORMULALIVE=0.
    /// </summary>
    public Func<double>? LiveFormula(SkDimension d)
    {
        if (FormulaLiveOff || string.IsNullOrWhiteSpace(d.Formula)) return null;
        if (!ExprParser.TryParse(d.Formula!, out Expr? e, out _) || e == null) return null;
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        e.CollectRefs(refs);
        if (!refs.Any(r => DimensionByName(r) is { } x && !ReferenceEquals(x, d))) return null;
        var scope = new LiveScope(this, FormulaScope, d);
        var geom = FormulaScope?.Geometry;
        return () =>
        {
            try
            {
                double v = e.Eval(scope, geom);
                if (double.IsNaN(v) || double.IsInfinity(v)) return d.Value;
                return d.Type is DimensionType.CoordX or DimensionType.CoordY && d.Value < 0 && v > 0 ? -v : v;
            }
            catch { return d.Value; }
        };
    }

    sealed class LiveScope : IValueResolver
    {
        readonly Sketch _sk; readonly ParameterTable? _t; readonly SkDimension _self; int _depth;
        readonly Dictionary<SkDimension, Expr?> _parsed = new();
        public LiveScope(Sketch sk, ParameterTable? t, SkDimension self) { _sk = sk; _t = t; _self = self; }
        public bool TryValue(string name, out double value)
        {
            if (_sk.DimensionByName(name) is { } d)
            {
                if (ReferenceEquals(d, _self)) { value = d.Value; return true; }
                if (!string.IsNullOrWhiteSpace(d.Formula) && _depth < 8)
                {
                    if (!_parsed.TryGetValue(d, out var pe)) { ExprParser.TryParse(d.Formula!, out pe, out _); _parsed[d] = pe; }
                    if (pe != null)
                    {
                        _depth++;
                        try { value = pe.Eval(this, _t?.Geometry); if (!double.IsNaN(value)) return true; }
                        catch { }
                        finally { _depth--; }
                    }
                }
                // без перетаскивания исходный слабый размер стоит на месте (меняется зависимый); при перетаскивании — измерение
                if (d.IsDriving || !_sk._liveMeasure) { value = d.Value; return true; }
                double m = DimensionMeasure.Value(d);
                value = double.IsNaN(m) ? d.Value : m;
                return true;
            }
            if (_t != null) return _t.TryValue(name, out value);
            value = 0; return false;
        }
    }

    /// <summary>k103: значения размеров с формулами от своих размеров — по геометрии после решения.</summary>
    public void RefreshLiveFormulas()
    {
        foreach (var d in Dimensions)
            if (d.Enabled && LiveFormula(d) is { } f) { double v = f(); if (!double.IsNaN(v)) d.Value = v; }
    }

    public SkDimension AddDimension(DimensionType t, double value, params SkEntity[] items)
    {
        var d = new SkDimension(t, value, items);
        EnsureDimIds();
        d.Index = NextDimIndex++;
        Dimensions.Add(d);
        BumpStructure();
        NotifyChanged(this);
        return d;
    }

    public SkEntity? Find(uint id) => Entities.FirstOrDefault(e => e.Id == id);

    /// <summary>Все точки, включая принадлежащие другим элементам.</summary>
    public IEnumerable<SkPoint> AllPoints()
    {
        foreach (var e in Entities)
        {
            if (e is SkPoint p) yield return p;
            foreach (var h in e.Handles) yield return h;
        }
    }

    /// <summary>Ближайший элемент к точке в координатах эскиза, в пределах допуска.</summary>
    public SkEntity? Pick(Vector2d p, double tolerance)
    {
        SkEntity? best = null;
        double bd = tolerance;
        foreach (var e in Entities)
        {
            double d = e.DistanceTo(p);
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    // ------------------------------------------------------------ пересчёт

    /// <summary>Подставляет значения формул размеров из таблицы параметров.</summary>
    public void UpdateDimensionsFromParameters(ParameterTable table)
    {
        FormulaScope = table;
        foreach (var d in Dimensions)
        {
            if (string.IsNullOrWhiteSpace(d.Formula)) { d.Error = null; continue; }
            if (TryEvaluate(d.Formula!, table, out double v, out string? err))
            {
                // координатный размер: формула задаёт расстояние, знак (сторона от нуля) — прежний
                d.Value = d.Type is DimensionType.CoordX or DimensionType.CoordY && d.Value < 0 && v > 0 ? -v : v;
                d.Error = null;
            }
            else d.Error = err;
        }
    }

    /// <summary>
    /// k96: ПУСТЫЕ СВЯЗИ — выполненные тождественно: конец отрезка (дуги) «на» своём же отрезке (дуге), совпадение точки с
    /// собой. Их ставила автопривязка у каждого узла ломаной (общая точка соседних отрезков «лежала на» предыдущем): решатель
    /// видел избыточные уравнения — эскиз «переопределён», и любая правка размера отвергалась («противоречит другим связям»),
    /// а геометрия после отказа оставалась сдвинутой. Убираются перед каждым решением. Мутация P3D_SKTRIVIAL=0.
    /// </summary>
    public static readonly bool DupCoincidentOff = Environment.GetEnvironmentVariable("P3D_SKDUPCOINC") == "0";

    /// <summary>Связь совпадения на пути между точками a и b (через другие совпадения); null — не совпадают.</summary>
    public SkConstraint? CoincidentPath(SkEntity a, SkEntity b)
    {
        if (ReferenceEquals(a, b)) return Constraints.FirstOrDefault(c => c.Type == ConstraintType.Coincident && c.Items.Contains(a));
        var seen = new HashSet<SkEntity>(ReferenceEqualityComparer.Instance) { a };
        var q = new Queue<SkEntity>(); q.Enqueue(a);
        SkConstraint? first = null;
        while (q.Count > 0)
        {
            var x = q.Dequeue();
            foreach (var c in Constraints)
            {
                if (!c.Enabled || c.Type != ConstraintType.Coincident || c.Items.Count != 2) continue;
                int k = ReferenceEquals(c.Items[0], x) ? 1 : ReferenceEquals(c.Items[1], x) ? 0 : -1;
                if (k < 0) continue;
                var y = c.Items[k];
                if (!seen.Add(y)) continue;
                first ??= c;
                if (ReferenceEquals(y, b)) return c;
                q.Enqueue(y);
            }
        }
        return null;
    }

    public int RemoveTrivialConstraints()
    {
        if (TrivialOff) return 0;
        int n = Constraints.RemoveAll(IsTrivial);
        // k101: совпадения, замыкающие цикл (точки уже совпадают через другие), — лишние
        if (!DupCoincidentOff)
        {
            var parent = new Dictionary<SkEntity, SkEntity>(ReferenceEqualityComparer.Instance);
            SkEntity Find(SkEntity e) { while (parent.TryGetValue(e, out var p) && !ReferenceEquals(p, e)) e = p; return e; }
            var extra = new List<SkConstraint>();
            foreach (var c in Constraints)
            {
                if (!c.Enabled || c.Type != ConstraintType.Coincident || c.Items.Count != 2) continue;
                var ra = Find(c.Items[0]); var rb = Find(c.Items[1]);
                if (ReferenceEquals(ra, rb)) extra.Add(c); else parent[ra] = rb;
            }
            foreach (var c in extra) { Constraints.Remove(c); n++; }
        }
        if (n > 0) BumpStructure();
        return n;
    }
    public static readonly bool TrivialOff = Environment.GetEnvironmentVariable("P3D_SKTRIVIAL") == "0";

    public static bool IsTrivial(SkConstraint c)
    {
        var it = c.Items;
        switch (c.Type)
        {
            case ConstraintType.Coincident:
                return it.Count >= 2 && ReferenceEquals(it[0], it[1]);
            case ConstraintType.PointOnEntity:
                if (it.Count < 2 || it[0] is not SkPoint p) return false;
                return it[1] switch
                {
                    SkLine l => ReferenceEquals(l.A, p) || ReferenceEquals(l.B, p),
                    SkArc a => ReferenceEquals(a.Start, p) || ReferenceEquals(a.End, p),
                    _ => false
                };
            default: return false;
        }
    }

    public SolveStatus Solve(ISketchSolver solver, SkPoint? dragged = null)
    {
        RemoveTrivialConstraints();
        EnsureDimIds();
        // Признаки конфликта пересчитываются перед КАЖДЫМ решением.
        // Так они всегда отражают текущий состав ограничений: удалили
        // одного спорщика — второй сам перестаёт быть конфликтующим.
        ConstraintConflicts.Refresh(this);

        _liveMeasure = dragged != null;
        try { LastSolve = solver.Solve(this, dragged, out int dof0); DegreesOfFreedom = dof0; }
        finally { _liveMeasure = false; }
        int dof = DegreesOfFreedom;
        DegreesOfFreedom = dof;
        // KRN-08 (08.9): условия выполнены выродившейся геометрией (отрезок нулевой длины, окружность нулевого радиуса) —
        // особое состояние. По рангу якобиана его не отличить: решатель останавливается в 1e-9 от вырождения, где ранг
        // полный (измерено: размер 0 у стороны прямоугольника — статус «недоопределён»). Мутация P3D_SKETCHSINGULAR=0.
        DegenerateEntities.Clear();
        if (LastSolve is SolveStatus.Ok or SolveStatus.Underconstrained or SolveStatus.Overconstrained && !SingularOff)
        {
            DegenerateEntities.AddRange(FindDegenerate());
            if (DegenerateEntities.Count > 0) LastSolve = SolveStatus.Singular;
        }

        RefreshMeasuredDimensions();
        RefreshLiveFormulas();
        return LastSolve;
    }

    static readonly bool SingularOff = Environment.GetEnvironmentVariable("P3D_SKETCHSINGULAR") == "0";
    static readonly bool StateCheckOff = Environment.GetEnvironmentVariable("P3D_SKETCHSTATE") == "0";

    /// <summary>Выродившиеся элементы при последнем решении (для состояния Singular).</summary>
    public readonly List<SkEntity> DegenerateEntities = new();

    /// <summary>Отрезки нулевой длины, окружности и дуги нулевого радиуса (ссылочная геометрия не в счёт).</summary>
    public IEnumerable<SkEntity> FindDegenerate()
    {
        double L = 1;
        foreach (var p in Entities.OfType<SkPoint>()) L = Math.Max(L, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
        double tol = 1e-9 * L;
        foreach (var e in Entities)
        {
            if (e.Role == SkRole.Reference) continue;
            bool bad = e switch
            {
                SkLine l => l.Length <= tol,
                SkCircle c => c.Radius <= Math.Max(tol, NumericSketchSolver.MinRadiusValue * (1 + 1e-9)),
                SkArc a => a.Radius <= tol,
                _ => false
            };
            if (bad) yield return e;
        }
    }

    /// <summary>
    /// KRN-08 (08.9): операция по эскизу не строит тело по противоречивому, нерешённому или выродившемуся эскизу — прежде
    /// результат решения не проверялся, и выдавливание шло по компромиссу наименьших квадратов (стороны «почти» нужной
    /// длины) молча. Недоопределённый и избыточный, но согласный эскиз допустимы. Мутация P3D_SKETCHSTATE=0.
    /// </summary>
    public void RequireSolved(string what)
    {
        if (StateCheckOff) return;
        string? why = LastSolve switch
        {
            SolveStatus.Inconsistent => "its constraints and dimensions are conflicting",
            SolveStatus.DidNotConverge => "the solver failed to satisfy its constraints",
            SolveStatus.Singular => $"it is singular: {DegenerateEntities.Count} element(s) degenerated to zero size",
            _ => null
        };
        if (why != null)
            throw new Parametric3D.Core.RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: the sketch «{Name}» cannot be used — {why}.");
    }

    // ---------------------------------------------------- выдача наружу

    /// <summary>Плоские ломаные всех элементов (для рендера).</summary>
    public IEnumerable<(SkEntity Entity, List<Vector2d> Pts, bool Closed)> Polylines()
    {
        foreach (var e in Entities)
        {
            if (e is SkPoint) continue;
            var pts = new List<Vector2d>();
            e.Tessellate(pts, true);
            if (pts.Count < 2) continue;
            bool closed = e is SkCircle || (e is SkEllipse el && Math.Abs(el.A1 - el.A0 - Math.PI * 2) < 1e-9);
            if (closed && pts.Count > 1) pts.RemoveAt(pts.Count - 1);
            yield return (e, pts, closed);
        }
    }

    /// <summary>Плоский профиль для ядра: только элементы роли Object.</summary>
    public Profile ToProfile()
    {
        var curves = new List<ICurve3>();
        foreach (var e in Entities)
        {
            if (e.Role != SkRole.Object) continue;
            if (e is SkPoint) continue;
            curves.Add(e.ToCurve3(Plane));
        }
        return ProfileBuilder.Build(Plane, curves);
    }

    /// <summary>
    /// KRN-11 (11.2): ПРОФИЛЬ ДЛЯ ТЕЛА. Самопересекающийся контур («бабочка») прежде уходил в выдавливание как есть: знаковая
    /// площадь двух треугольников взаимно гасилась, операция «успешно» не добавляла ничего (найдено квалификационным набором,
    /// KRN11-QUAL-NEG-BOWTIE-SKETCH). Теперь — отказ InvalidInput с названной причиной; эскиз уже подсвечивает такие
    /// элементы (SketchDiagnostics). Мутация P3D_PROFILESELFX=0.
    /// </summary>
    public Profile ToSolidProfile(string operation)
    {
        var pr = ToProfile();
        if (!ProfileSelfXOff && pr.HasSelfIntersections)
            throw new Parametric3D.Core.RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput,
                $"{operation}: the sketch «{Name}» cannot be used — its profile crosses itself.");
        return pr;
    }
    static readonly bool ProfileSelfXOff = Environment.GetEnvironmentVariable("P3D_PROFILESELFX") == "0";

    /// <summary>Ось вращения — первая осевая линия эскиза.</summary>
    /// <summary>Ось по конкретному отрезку эскиза, выбранному пользователем.</summary>
    public Frame3? AxisFromEntity(uint entityId)
    {
        foreach (var e in Entities)
        {
            if (e.Id != entityId || e is not SkLine l) continue;
            return AxisOf(l);
        }
        return null;
    }

    private Frame3? AxisOf(SkLine l)
    {
        Vector3d a = Plane.ToWorld(l.A.P);
        Vector3d b = Plane.ToWorld(l.B.P);
        Vector3d d = b - a;
        if (d.LengthSquared < 1e-18) return null;

        // Длина отрезка роли не играет: ось — это прямая, заданная точкой
        // и направлением. Пользователь указывает отрезок лишь как способ
        // задать эту прямую.
        return Frame3.FromNormal(a, d.Normalized());
    }

    public Frame3? RevolutionAxis()
    {
        foreach (var e in Entities)
        {
            if (e.Role != SkRole.Centerline || e is not SkLine l) continue;
            var f = AxisOf(l);
            if (f != null) return f;
        }
        return null;
    }

    public Vector2d SnapPoint(Vector2d p)
    {
        if (!SnapToGrid || GridStep <= 0) return p;
        return new Vector2d(Math.Round(p.X / GridStep) * GridStep,
                            Math.Round(p.Y / GridStep) * GridStep);
    }
}
