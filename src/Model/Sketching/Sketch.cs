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
        PlaneDescription = PlaneDescription.EndsWith(" (flipped)")
            ? PlaneDescription[..^10]
            : PlaneDescription + " (flipped)";
        BumpStructure();
        NotifyChanged(this);
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
        Vector2d uv = Plane.ToLocal2(worldPoint);
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
    public readonly List<SkConstraint> Constraints = new();
    public readonly List<SkDimension> Dimensions = new();

    public bool SnapToGrid;
    public double GridStep = 5.0;

    /// <summary>Состояние последнего решения системы ограничений.</summary>
    public SolveStatus LastSolve = SolveStatus.Ok;
    public int DegreesOfFreedom;

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
        var c = new SkConstraint(t, items);
        Constraints.Add(c);
        BumpStructure();
        NotifyChanged(this);
        return c;
    }

    public SkDimension AddDimension(DimensionType t, double value, params SkEntity[] items)
    {
        var d = new SkDimension(t, value, items);
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
        foreach (var d in Dimensions)
        {
            if (string.IsNullOrWhiteSpace(d.Formula)) { d.Error = null; continue; }
            if (table.TryEvaluate(d.Formula!, out double v, out string? err))
            {
                d.Value = v;
                d.Error = null;
            }
            else d.Error = err;
        }
    }

    public SolveStatus Solve(ISketchSolver solver, SkPoint? dragged = null)
    {
        // Признаки конфликта пересчитываются перед КАЖДЫМ решением.
        // Так они всегда отражают текущий состав ограничений: удалили
        // одного спорщика — второй сам перестаёт быть конфликтующим.
        ConstraintConflicts.Refresh(this);

        LastSolve = solver.Solve(this, dragged, out int dof);
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
