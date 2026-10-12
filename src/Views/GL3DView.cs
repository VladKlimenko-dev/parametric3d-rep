using OpenTK.Mathematics;
using Parametric3D.App;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;

namespace Parametric3D.Views;

/// <summary>Уровень, на котором сейчас работает пользователь.</summary>
public enum EditMode { Assembly, Part, Sketch }

/// <summary>
/// Основной трёхмерный вид.
///
/// Держит три уровня работы (сборка → деталь → эскиз) и переключается
/// между ними анимированным переходом. Escape поднимает на уровень выше,
/// двойной щелчок — опускает.
/// </summary>
public sealed class GL3DView : GLViewBase
{
    private Document _doc = null!;
    private readonly RenderScene _scene = new();
    private readonly GridRenderer _grid = new();
    private readonly ScenePicker _picker = new();
    public readonly Selection Selection = new();

    public EditMode Mode { get; private set; } = EditMode.Assembly;
    public Part? ActivePart { get; private set; }
    public Sketch? ActiveSketch { get; private set; }

    public bool ShowGrid = true;

    /// <summary>
    /// Привязка к существующим точкам эскиза при построении.
    /// Управляется кнопкой Snap items на панели эскиза.
    /// </summary>
    public bool SnapToItems = true;

    /// <summary>
    /// Сетка показывается только в режиме эскиза.
    ///
    /// В сборке и в детали она мешает: плоскость у неё всё равно одна,
    /// а к геометрии тела она никак не привязана. Смысл сетка имеет
    /// ровно там, где есть плоскость черчения и привязка к ней.
    /// </summary>
    public bool GridVisible => ShowGrid && Mode == EditMode.Sketch && ActiveSketch != null;

    public event Action? ModeChanged;
    public event Action? ToolChanged;

    /// <summary>Сообщение для строки состояния: подсказка режима или ошибка.</summary>
    public event Action<string?>? CommandMessage;

    /// <summary>Режим набора элементов закончился — панели снимают отметку с кнопки.</summary>
    public event Action? PickModeChanged;

    /// <summary>Двойной щелчок по размеру: запрос на ввод нового значения.</summary>
    public event Action<SkDimension>? DimensionEditRequested;

    /// <summary>Щелчок правой кнопкой по размеру: запрос контекстного меню.</summary>
    public event Action<SkDimension, Point>? DimensionMenuRequested;

    /// <summary>
    /// Наложенное ограничение конфликтует с уже существующими. Форма
    /// показывает список и даёт выбрать, что удалить.
    /// </summary>
    public event Action<SkConstraint, List<Conflict>>? ConstraintConflictFound;

    /// <summary>
    /// Пользователь указал отрезок эскиза как ось. Возникает только
    /// в режиме выбора оси (BeginAxisPick).
    /// </summary>
    public event Action<Sketch, SkLine>? AxisPicked;

    /// <summary>Пользователь указал эскиз щелчком (выбор пути протягивания).</summary>
    public event Action<Sketch>? SketchPicked;

    /// <summary>Выбрана плоскость-базис: система координат, id источника и название.</summary>
    public event Action<Frame3, uint, string>? PlanePicked;

    /// <summary>Выбрана нулевая точка: точка в мире и её описание.</summary>
    public event Action<Vector3d, string>? OriginPicked;

    internal void RaiseConstraintConflict(SkConstraint added, List<Conflict> conflicts)
        => ConstraintConflictFound?.Invoke(added, conflicts);
    public event Action? SelectionChanged;

    public BodyViewMode BodyMode
    {
        get => _scene.BodyMode;
        set
        {
            if (_scene.BodyMode == value) { Invalidate(); return; }

            _scene.BodyMode = value;

            // Сетка треугольников строится в Sync и только для своего вида.
            // Переключение вида геометрию не меняет, но сцену пересобрать
            // надо — иначе сетки просто нет, и вид выглядит поломанным.
            if (value == BodyViewMode.Mesh || value == BodyViewMode.VolumeMesh) _sceneDirty = true;

            Invalidate();
        }
    }

    public ProjectionMode Projection
    {
        get => Camera.State.Mode;
        set
        {
            if (Camera.State.Mode == value) return;
            var to = Camera.State;
            to.Mode = value;
            StartTransition(TransitionKind.Reorient, to, null);
        }
    }

    private bool _showDatumPlanes = true;

    /// <summary>
    /// Показывать базовые плоскости. В режиме эскиза они не рисуются
    /// в любом случае: эскиз лежит НА плоскости, и её полупрозрачная
    /// заливка с рамкой только загораживают чертёж.
    /// </summary>
    public bool ShowDatumPlanes
    {
        get => _showDatumPlanes;
        set { _showDatumPlanes = value; MarkDirty(); }
    }

    private bool _sceneDirty = true;
    private int _lastStructureVersion = -1;
    private readonly LineBatch _previewLines = new();
    private readonly PointBatch _previewPoints = new();

    /// <summary>
    /// Превью инструмента перестраивается каждый кадр — это единственное
    /// место, где допустима перезаливка буфера на кадр: там десятки вершин,
    /// а не вся модель.
    /// </summary>
    private void DrawToolPreview(RenderContext ctx)
    {
        Sketch? arrowSketch = ViewArrowSketch();

        bool anything = (ActiveSketch != null && (Tool != null || _hoverProjection != null))
                        || arrowSketch != null
                        || TreeHoverSketches().Count > 0;
        if (!anything) return;

        _previewLines.Begin();
        _previewPoints.Begin();

        if (ActiveSketch != null)
        {
            Tool?.DrawPreview(this, _previewLines, _previewPoints);
            DrawProjectionPreview();
        }

        if (arrowSketch != null) DrawViewArrow(arrowSketch);
        DrawOriginCandidates();
        DrawTreeHoverSketches();

        _previewLines.End();
        _previewPoints.End();

        ctx.Model = Matrix4.Identity;
        _previewLines.Draw(ctx);
        _previewPoints.Draw(ctx);
    }

    // ------------------------------------------- стрелка направления взгляда

    /// <summary>
    /// Эскиз, для которого показывается стрелка взгляда.
    ///
    /// Только вне режима черчения: внутри эскиза камера смотрит прямо
    /// вдоль этой стрелки, и она выродилась бы в точку.
    /// </summary>
    private Sketch? ViewArrowSketch()
    {
        if (!Core.Settings.Current.ShowSketchViewArrow) return null;

        // ТОЛЬКО во время правки настроек эскиза.
        //
        // Раньше стрелка появлялась и у просто выделенного эскиза. В обычной
        // работе она ни к чему: смотреть на модель мешает, а щелчок по ней
        // неожиданно переворачивает эскиз.
        return _setupArrowSketch;
    }

    /// <summary>Эскиз, чьи настройки правятся: его стрелка видна и без выделения.</summary>
    private Sketch? _setupArrowSketch;

    /// <summary>Показывать стрелку взгляда указанного эскиза, пока правятся его настройки.</summary>
    public void ShowViewArrowFor(Sketch? sketch)
    {
        _setupArrowSketch = sketch;
        Invalidate();
    }

    private Vector3d ArrowTail(Sketch sk) =>
        sk.Plane.Origin + sk.Plane.W * (PixelSize * Core.Settings.Current.SketchViewArrowPx);

    /// <summary>
    /// Стрелка от точки, откуда смотрит пользователь, в нулевую точку эскиза.
    ///
    /// Длина задана в ПИКСЕЛЯХ: стрелка — указатель, а не часть модели,
    /// и при масштабировании должна сохранять вид.
    /// </summary>
    private void DrawViewArrow(Sketch sk)
    {
        Vector3d tail = ArrowTail(sk);
        Vector3d head = sk.Plane.Origin;

        var a = ToF(tail);
        var b = ToF(head);

        var cfg = Core.Settings.Current;
        double len = PixelSize * cfg.SketchViewArrowPx;

        // Древко до начала наконечника, затем сам наконечник сужающейся
        // полосой — тем же приёмом, что у размерных стрелок.
        Vector3d dir = (head - tail).Normalized();
        Vector3d neck = head - dir * (len * 0.35);

        // Под курсором стрелка рисуется толще и цветом наведения:
        // без этого непонятно, что по ней можно щёлкнуть.
        LineStyle style = _arrowHover ? LineStyle.SketchViewArrowHover : LineStyle.SketchViewArrow;
        float headWidth = cfg.SketchViewArrowPx * 0.22f * (_arrowHover ? 1.25f : 1f);

        _previewLines.AddSegment(a, ToF(neck), style, 0);
        _previewLines.AddTaperedSegment(ToF(neck), b, style, headWidth, 0);
    }

    private static Vector3 ToF(Vector3d p) => new((float)p.X, (float)p.Y, (float)p.Z);

    /// <summary>
    /// Щелчок по стрелке взгляда переворачивает эскиз.
    /// Проверка экранная: стрелка нарисована в пикселях, ловиться должна так же.
    /// </summary>
    /// <summary>Курсор на стрелке взгляда: она рисуется подсвеченной.</summary>
    private bool _arrowHover;

    /// <summary>
    /// Курсор на стрелке взгляда?
    ///
    /// Зона вокруг нулевой точки исключена: там стоит сама точка, и щелчок
    /// по ней должен выбирать её, а не переворачивать эскиз. Раньше остриё
    /// стрелки перекрывало ноль и забирало щелчок себе.
    /// </summary>
    private bool HitViewArrow(Point pos, out Sketch? sketch)
    {
        sketch = ViewArrowSketch();
        if (sketch == null) return false;

        if (!ProjectToScreen(ArrowTail(sketch), out Vector2 tail)) return false;
        if (!ProjectToScreen(sketch.Plane.Origin, out Vector2 head)) return false;

        var p = new Vector2(pos.X, pos.Y);

        float keepOut = Core.Settings.Current.SketchViewArrowOriginGapPx;
        if ((p - head).Length < keepOut) return false;

        Vector2 dir = head - tail;
        if (dir.Length < 1e-3f) return false;

        // Отрезок укорачивается со стороны нуля на ту же зону.
        Vector2 stop = head - dir.Normalized() * keepOut;

        float d = DistanceToSegment(p, tail, stop);
        return d <= Core.Settings.Current.PickTolerancePx + 3f;
    }

    private bool HandleViewArrowClick(Point pos)
    {
        if (!HitViewArrow(pos, out Sketch? sk) || sk == null) return false;

        sk.Flip();
        _arrowHover = false;
        MarkDirty();
        CommandMessage?.Invoke($"{sk.Name}: flipped");
        return true;
    }

    private bool ProjectToScreen(Vector3d world, out Vector2 screen)
    {
        screen = Vector2.Zero;

        Matrix4 vp = Camera.ViewMatrix * Camera.ProjMatrix;
        Vector4 clip = new Vector4(ToF(world), 1f) * vp;
        if (clip.W <= 1e-6f) return false;

        Vector3 ndc = clip.Xyz / clip.W;
        screen = new Vector2((ndc.X * 0.5f + 0.5f) * ClientSize.Width,
                             (1f - (ndc.Y * 0.5f + 0.5f)) * ClientSize.Height);
        return true;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.LengthSquared;
        if (len2 < 1e-9f) return (p - a).Length;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + ab * t)).Length;
    }

    // --------------------------------------------- проекции чужой геометрии

    /// <summary>
    /// Кандидаты на проецирование: чужая геометрия, приведённая к плоскости
    /// текущего эскиза. Пересчитываются при изменении состава документа,
    /// а не каждый кадр — обход всей модели дорог.
    /// </summary>
    private List<ProjectedRef> _projections = new();
    private int _projectionsVersion = -1;

    /// <summary>
    /// Эскиз, для которого посчитан кэш. Без него счётчик версий одного
    /// эскиза мог случайно совпасть со счётчиком другого, и проекции
    /// достались бы чужому чертежу.
    /// </summary>
    private uint _projectionsSketchId;

    /// <summary>Проекция под курсором. Её и покажет предпросмотр.</summary>
    private ProjectedRef? _hoverProjection;

    /// <summary>
    /// Название стороннего элемента под курсором, чью проекцию видно.
    /// Пусто — под курсором ничего такого нет.
    ///
    /// Строка состояния показывает это вместо «ничего не выбрано»: иначе
    /// подсвеченная проекция висит на экране безымянной, и непонятно,
    /// какого элемента она и откуда взялась.
    /// </summary>
    public string HoverProjectionName =>
        _originHover?.Source ?? _hoverProjection?.Source ?? "";

    private void RefreshProjections()
    {
        var sk = ActiveSketch;
        if (sk == null)
        {
            _projections.Clear();
            _projectionsVersion = -1;
            _projectionsSketchId = 0;
            return;
        }

        if (_projectionsSketchId == sk.Id && _projectionsVersion == sk.StructureVersion) return;

        _projectionsSketchId = sk.Id;
        _projectionsVersion = sk.StructureVersion;
        _projections = SketchProjection.Collect(_doc, sk);
    }

    /// <summary>
    /// Ищет проекцию под курсором.
    ///
    /// Вызывается только когда под курсором нет ничего своего: проекция —
    /// подсказка, а не полноценный объект, и она не должна перебивать
    /// собственную геометрию эскиза.
    /// </summary>
    private void UpdateHoverProjection(Point pos)
    {
        _hoverProjection = null;

        var sk = ActiveSketch;
        if (sk == null || Mode != EditMode.Sketch) return;
        if (!Core.Settings.Current.ShowProjections) return;

        RefreshProjections();
        if (_projections.Count == 0) return;

        Vector2d p = ScreenToSketch(pos.X, pos.Y);
        double tol = PixelSize * Core.Settings.Current.PickTolerancePx;

        double best = tol;
        foreach (var pr in _projections)
        {
            double d = pr.DistanceTo(p);
            if (d < best) { best = d; _hoverProjection = pr; }
        }
    }

    /// <summary>
    /// Точки-кандидаты в нулевую: проекции чужой геометрии на плоскость
    /// эскиза. Под курсором кандидат выделяется.
    /// </summary>
    private void DrawOriginCandidates()
    {
        var sk = ActiveSketch;
        if (sk == null || _originCandidates.Count == 0) return;

        foreach (var c in _originCandidates)
            _previewPoints.Add(W(sk, c.P),
                ReferenceEquals(c, _originHover) ? PointStyle.Snap : PointStyle.Preview, 0);
    }

    /// <summary>Ищет кандидата в нулевую под курсором.</summary>
    private void UpdateOriginHover(Point pos)
    {
        _originHover = null;

        var sk = ActiveSketch;
        if (sk == null || _originCandidates.Count == 0) return;

        double best = Core.Settings.Current.PickTolerancePx + 3;
        foreach (var c in _originCandidates)
        {
            if (!ProjectToScreen(sk.Plane.ToWorld(c.P), out Vector2 sp)) continue;
            double d = (sp - new Vector2(pos.X, pos.Y)).Length;
            if (d < best) { best = d; _originHover = c; }
        }
    }

    /// <summary>
    /// Эскизы, которые надо подсветить из-за наведения в дереве.
    ///
    /// Наведение на эскиз даёт сам эскиз, наведение на операцию — эскизы,
    /// которые она использует. Скрытые тоже: в дереве строка есть, и на
    /// вопрос «что это» вид обязан ответить, даже если эскиз погашен.
    /// </summary>
    private List<Sketch> TreeHoverSketches()
    {
        var result = new List<Sketch>();

        switch (_treeHover)
        {
            case Sketch sk:
                result.Add(sk);
                break;

            case Feature f when f.Parent is Part part:
                foreach (uint id in f.ConsumedSketchIds())
                    foreach (var s in part.Sketches)
                        if (s.Id == id) result.Add(s);
                break;
        }

        return result;
    }

    /// <summary>
    /// Рисует подсвеченные из дерева эскизы поверх сцены.
    ///
    /// Именно поверх, а не через флаги подсветки: у погашенного эскиза
    /// в сцене нет буферов, ставить флаги некуда. Предпросмотр же рисуется
    /// всегда и от видимости не зависит.
    /// </summary>
    private void DrawTreeHoverSketches()
    {
        foreach (var sk in TreeHoverSketches())
        {
            if (sk.Visible && Mode != EditMode.Sketch) continue;   // подсветит сцена
            if (ReferenceEquals(sk, ActiveSketch)) continue;

            foreach (var e in sk.Entities)
            {
                if (e is SkPoint p)
                {
                    _previewPoints.Add(W(sk, p.P), PointStyle.Preview, 0);
                    continue;
                }

                var pts = new List<Vector2d>();
                e.Tessellate(pts, true);
                for (int i = 1; i < pts.Count; i++)
                    _previewLines.AddSegment(W(sk, pts[i - 1]), W(sk, pts[i]), LineStyle.Preview, 0);
            }
        }
    }

    private void DrawProjectionPreview()
    {
        var pr = _hoverProjection;
        var sk = ActiveSketch;
        if (pr == null || sk == null) return;

        switch (pr.Kind)
        {
            case ProjectedKind.Point:
                _previewPoints.Add(W(sk, pr.P), PointStyle.Snap, 0);
                break;

            case ProjectedKind.Circle:
                PreviewArc(sk, pr.P, pr.Radius, pr.Radius, 0, 0, Math.PI * 2);
                break;

            case ProjectedKind.Arc:
                {
                    double sweep = GeomUtil.Norm2Pi(pr.A1 - pr.A0);
                    PreviewArc(sk, pr.P, pr.Radius, pr.Radius, 0, pr.A0, pr.A0 + sweep);
                    break;
                }

            case ProjectedKind.Ellipse:
                PreviewArc(sk, pr.P, pr.RU, pr.RV, pr.Rotation, 0, Math.PI * 2);
                break;

            default:
                for (int i = 1; i < pr.Points.Count; i++)
                    _previewLines.AddSegment(W(sk, pr.Points[i - 1]), W(sk, pr.Points[i]),
                                             LineStyle.Preview, 0);
                break;
        }
    }

    private void PreviewArc(Sketch sk, Vector2d center, double ru, double rv,
                            double rot, double a0, double a1)
    {
        int n = Math.Max(24, (int)(Math.Abs(a1 - a0) * 24 / Math.PI));
        Vector3 prev = default;

        for (int i = 0; i <= n; i++)
        {
            double a = a0 + (a1 - a0) * i / n;
            double x = ru * Math.Cos(a), y = rv * Math.Sin(a);
            Vector2d q = center + new Vector2d(x * Math.Cos(rot) - y * Math.Sin(rot),
                                               x * Math.Sin(rot) + y * Math.Cos(rot));
            Vector3 w = W(sk, q);
            if (i > 0) _previewLines.AddSegment(prev, w, LineStyle.Preview, 0);
            prev = w;
        }
    }

    private static Vector3 W(Sketch sk, Vector2d p)
    {
        Vector3d w = sk.Plane.ToWorld(p);
        return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
    }

    /// <summary>
    /// Переносит проекцию под курсором в эскиз как ссылочную геометрию.
    /// </summary>
    private bool ProjectHovered()
    {
        var pr = _hoverProjection;
        var sk = ActiveSketch;
        if (pr == null || sk == null) return false;

        switch (pr.Kind)
        {
            case ProjectedKind.Point:
                sk.AddEntity(new SkPoint(pr.P)
                { Role = SkRole.Reference, ReferenceSource = pr.Source });
                break;

            case ProjectedKind.Circle:
                sk.AddEntity(new SkCircle(new SkPoint(pr.P), pr.Radius)
                { Role = SkRole.Reference, ReferenceSource = pr.Source });
                break;

            case ProjectedKind.Ellipse:
                sk.AddEntity(new SkEllipse(new SkPoint(pr.P), pr.RU, pr.RV, pr.Rotation)
                { Role = SkRole.Reference, ReferenceSource = pr.Source });
                break;

            case ProjectedKind.Arc:
                {
                    Vector2d s = pr.P + new Vector2d(Math.Cos(pr.A0), Math.Sin(pr.A0)) * pr.Radius;
                    Vector2d t = pr.P + new Vector2d(Math.Cos(pr.A1), Math.Sin(pr.A1)) * pr.Radius;
                    sk.AddEntity(new SkArc(new SkPoint(pr.P), new SkPoint(s), new SkPoint(t))
                    { Role = SkRole.Reference, ReferenceSource = pr.Source });
                    break;
                }

            default:
                // Ломаная переносится отрезками: у эскиза нет своего типа
                // «ломаная», и дробление на отрезки не теряет ничего.
                for (int i = 1; i < pr.Points.Count; i++)
                    sk.AddEntity(new SkLine(new SkPoint(pr.Points[i - 1]), new SkPoint(pr.Points[i]))
                    { Role = SkRole.Reference, ReferenceSource = pr.Source });
                break;
        }

        _hoverProjection = null;
        MarkDirty();
        CommandMessage?.Invoke($"Projected: {pr.Source}");
        return true;
    }

    /// <summary>Устанавливает инструмент эскиза.</summary>
    public void SetTool(ISketchTool? tool)
    {
        Tool?.Cancel(this);
        Tool = tool;
        Cursor = tool == null ? Cursors.Default : Cursors.Cross;
        ToolChanged?.Invoke();
        Invalidate();
    }

    /// <summary>Вызывается инструментами, закончившими построение.</summary>
    public void ToolFinished(ISketchTool tool)
    {
        if (!ReferenceEquals(Tool, tool)) return;
        if (tool.Continuous) return;   // цепочные инструменты остаются активными
        SetTool(null);
    }

    public GL3DView()
    {
        Selection.Changed += () => { ApplyHighlight(); SelectionChanged?.Invoke(); Invalidate(); };
    }

    public void SetDocument(Document doc)
    {
        _doc = doc;
        _doc.Invalidated += MarkDirty;
        Mode = EditMode.Assembly;
        ActivePart = _doc.AllParts().FirstOrDefault();
        MarkDirty();
        Invalidate();
    }

    public void MarkDirty() { _sceneDirty = true; Invalidate(); }

    /// <summary>В эскизе вид смотрит строго на плоскость — вращать нечего.</summary>
    protected override bool AllowOrbit => Mode != EditMode.Sketch;

    // ---------------------------------------------------------------- кадр

    private async Task RebuildInBackground()
    {
        try
        {
            if (await _doc!.RebuildDirtyAsync()) _sceneDirty = true;
        }
        catch (Exception ex)
        {
            Core.AppLog.Write("background rebuild", ex);
        }
        Invalidate();
    }

    /// <summary>Во время фоновой пересборки правка в виде заблокирована (см. GLViewBase).</summary>
    protected override bool EditingLocked => _doc?.IsBuilding == true;

    /// <summary>
    /// Активный владелец для прозрачности и приоритета выбора: сборка в режиме сборки,
    /// текущая деталь в режимах детали и эскиза.
    /// </summary>
    private ModelItem? ActiveOwnerNow() =>
        _doc == null ? null : Mode == EditMode.Assembly ? _doc.Root : (ModelItem?)ActivePart;

    protected override void BeforeFrame()
    {
        if (_doc == null) return;

        // Сменился активный владелец — базовые элементы перекрашиваются (свои / чужие).
        ModelItem? owner = ActiveOwnerNow();
        if (!ReferenceEquals(owner, _scene.ActiveOwner))
        {
            _scene.ActiveOwner = owner;
            _sceneDirty = true;
        }

        // ПЕРЕСБОРКА — В ФОНЕ. Прежде она шла прямо здесь, в подготовке кадра,
        // и тяжёлая операция замораживала окно. Теперь кадр рисует прежние тела,
        // а по готовности сцена помечается грязной и вид перерисовывается.
        if (!_doc.IsBuilding && _doc.AllParts().Any(p => p.IsDirty))
            _ = RebuildInBackground();

        // При выборе нулевой точки базовые элементы показываются независимо
        // от переключателя: выбирать надо из нулевых точек деталей и сборки,
        // а они рисуются вместе с базовыми.
        _scene.ShowDatumPlanes = (_showDatumPlanes || InBasePick) && Mode != EditMode.Sketch;

        // В режиме черчения на экране только сам эскиз. Но пока выбирают
        // плоскость-базис или нулевую точку, надо видеть остальную модель.
        _scene.SketchOnly = Mode == EditMode.Sketch
                            && Core.Settings.Current.HideOthersInSketch
                            && !InBasePick;

        _scene.VisibilityLimit = SceneVisibilityLimit;

        // Чьи размеры показывать: в эскизе — его собственные, вне эскиза —
        // выбранного, и только если это включено в меню вида.
        _scene.DimensionSketchId =
            Mode == EditMode.Sketch ? ActiveSketch?.Id ?? 0
            : Core.Settings.Current.ShowSelectedItemDimensions ? SelectedSketchId()
            : 0;

        // Размеры строятся в пикселях, поэтому при изменении масштаба
        // их надо пересобрать. Проверка стоит здесь, а не в обработчике
        // колеса: масштаб меняют и переходы между уровнями, и Fit all.
        if (Mode == EditMode.Sketch && _scene.SetPixelSize(PixelSize)) _sceneDirty = true;

        // Допуск автопривязки — экранный: «рядом» одинаково на любом масштабе.
        if (ActiveSketch != null)
        {
            ActiveSketch.AutoTolerance = PixelSize * Core.Settings.Current.SnapTolerancePx;

            // Размеры по умолчанию пересобираются только когда изменился
            // СОСТАВ эскиза. На каждое движение мыши это было бы разорительно:
            // подбор перебирает кандидатов и решает эскиз на каждом.
            // Пока рисуют или тянут, набор не пересобирается.
            //
            // Подбор примеряет кандидатов и РЕШАЕТ эскиз на каждом: делать
            // это после каждой поставленной точки значит дёргать уже
            // нарисованное прямо под рукой. Набор пересоберётся, когда
            // инструмент отпустят.
            bool busy = Tool != null || _placingDim != null
                        || _dragPoint != null || _dragLine != null
                        || _dragRadius != null || _dragEllipse != null;

            if (!busy && ActiveSketch.StructureVersion != _lastStructureVersion)
            {
                _lastStructureVersion = ActiveSketch.StructureVersion;
                DefaultDimensions.Refresh(ActiveSketch, _doc.Solver);
                _lastStructureVersion = ActiveSketch.StructureVersion;
                _sceneDirty = true;
            }
        }

        if (_sceneDirty)
        {
            // Проверка эскиза на пересечения и разрывы идёт вместе
            // с пересборкой сцены: она нужна ровно тогда, когда
            // геометрия изменилась, и не чаще.
            if (ActiveSketch != null) SketchDiagnostics.Update(ActiveSketch);

            _scene.ActiveSketch = ActiveSketch;
            _scene.Sync(_doc);
            _scene.ResetAllFlags();     // буферы перезалиты, старый учёт подсветки недействителен
            _treeHover = null;
            ApplyHighlight();
            _sceneDirty = false;
        }

        if (GridVisible)
        {
            _grid.Plane = ActiveSketch!.Plane;
            // К цвету фона сходятся растворяющиеся линии сетки.
            _grid.FadeToColor = Styles.BackgroundBottom;
            _grid.Update(Camera);
        }
    }

    /// <summary>Дополнительный слой поверх сцены (превью инструментов эскиза).</summary>
    public event Action<RenderContext>? Overlay;

    protected override void DrawScene(RenderContext ctx)
    {
        if (_doc == null) return;
        if (GridVisible) _grid.Draw(ctx);
        _scene.Draw(ctx);
        DrawToolPreview(ctx);
        Overlay?.Invoke(ctx);
    }

    /// <summary>Активный документ (нужен инструментам и дереву проекта).</summary>
    public Document Document => _doc;

    /// <summary>Сцена нужна инструментам, чтобы разрешать pick-id.</summary>
    public RenderScene Scene => _scene;

    public double GridStep => _grid.Step;

    /// <summary>Эскиз, выбранный сейчас в виде или в дереве. 0 — ни одного.</summary>
    private uint SelectedSketchId()
    {
        foreach (var i in Selection.Items)
        {
            if (i.Target is Sketch s) return s.Id;
            if (i.Owner is Sketch o) return o.Id;
        }
        return 0;
    }

    /// <summary>Диагностика сетки для статусной строки: шаг, размер клетки, растворение.</summary>
    public string GridInfo => GridVisible ? _grid.Info : "";

    /// <summary>Экранная точка -&gt; координаты в плоскости активного эскиза.</summary>
    public Vector2d ScreenToSketch(int x, int y)
    {
        Frame3 pl = ActiveSketch?.Plane ?? Frame3.XY;
        Vector3 n = new((float)pl.W.X, (float)pl.W.Y, (float)pl.W.Z);
        Vector3 o = new((float)pl.Origin.X, (float)pl.Origin.Y, (float)pl.Origin.Z);
        Vector3 w = Camera.ScreenToWorldOnPlane(x, y, o, n);
        return pl.ToLocal2(new Vector3d(w.X, w.Y, w.Z));
    }

    /// <summary>Сколько единиц модели приходится на пиксель — для допусков захвата.</summary>
    public double PixelSize => 1.0 / Math.Max(1e-6, Camera.WorldToPx);

    // -------------------------------------------- последовательный выбор
    //
    // Кнопка ограничения или размера включает режим набора: пользователь
    // щёлкает элементы эскиза, а команда применяется, как только их
    // набралось достаточно. Первое выделение, сделанное ДО нажатия кнопки,
    // тоже идёт в набор — так работать привычнее, чем начинать с нуля.

    private ConstraintType? _pickConstraint;
    private bool _pickDimension;
    private readonly List<SkEntity> _pickBuffer = new();

    /// <summary>
    /// Размер, который сейчас размещают: он уже создан и следует за курсором,
    /// пока пользователь не зафиксирует положение подписи щелчком.
    /// </summary>
    private SkDimension? _placingDim;

    /// <summary>
    /// Идёт выбор оси вращения: пользователь наводит курсор на сегменты
    /// эскиза и щёлкает нужный. Диалог операции на это время закрыт —
    /// иначе модальное окно не отдало бы виду ни одного события мыши.
    /// </summary>
    private Sketch? _axisPickSketch;

    /// <summary>Включает выбор оси среди отрезков указанного эскиза.</summary>
    public void BeginAxisPick(Sketch sketch)
    {
        EndPickMode(silent: true);
        _axisPickSketch = sketch;
        Selection.Clear();
        CommandMessage?.Invoke("Revolve: click a sketch segment to use as the axis.");
        Invalidate();
    }

    public void EndAxisPick()
    {
        if (_axisPickSketch == null) return;
        _axisPickSketch = null;
        CommandMessage?.Invoke(null);
        Invalidate();
    }

    /// <summary>Идёт ли выбор оси.</summary>
    public bool InAxisPick => _axisPickSketch != null;

    // ------------------------------------------------------- выбор эскиза

    private bool _sketchPick;

    /// <summary>Идёт ли выбор эскиза щелчком.</summary>
    public bool InSketchPick => _sketchPick;

    public void BeginSketchPick()
    {
        EndPickMode(silent: true);
        _sketchPick = true;
        Selection.Clear();
        CommandMessage?.Invoke("Click a sketch to use as the path.");
        MarkDirty();
    }

    public void EndSketchPick()
    {
        if (!_sketchPick) return;
        _sketchPick = false;
        CommandMessage?.Invoke(null);
        MarkDirty();
    }

    private bool HandleSketchPick(PickRef r)
    {
        if (!_sketchPick) return false;

        Sketch? sk = r.Target as Sketch ?? r.Owner as Sketch;
        if (sk == null)
        {
            CommandMessage?.Invoke("That is not a sketch. Click a sketch curve.");
            return true;
        }

        _sketchPick = false;
        CommandMessage?.Invoke(null);
        SketchPicked?.Invoke(sk);
        return true;
    }

    // ------------------------------------------- выбор плоскости и нуля

    private enum BasePick { None, Plane, Origin }

    private BasePick _basePick = BasePick.None;

    /// <summary>
    /// Наибольший идентификатор, который годится в выбор.
    ///
    /// Эскиз не может опираться на то, что создано ПОЗЖЕ него: иначе
    /// в дереве построения возникает петля — тело зависит от эскиза,
    /// а эскиз от этого тела. Идентификаторы выдаются по возрастанию,
    /// поэтому сравнение с id эскиза и есть проверка «создано раньше».
    /// </summary>
    private uint _basePickLimit;

    public void BeginBasePlanePick(Sketch sketch)
    {
        StartBasePick(BasePick.Plane, sketch,
            "Sketch: click a plane or a planar face created before this sketch.");
    }

    public void BeginOriginPick(Sketch sketch)
    {
        StartBasePick(BasePick.Origin, sketch,
            "Sketch: click a point, a vertex, a circle center or an origin.");
    }

    /// <summary>Эскиз возобновится после выбора: режим черчения был лишь приостановлен.</summary>
    private bool _basePickResumeSketch;

    private void StartBasePick(BasePick kind, Sketch sketch, string hint)
    {
        EndPickMode(silent: true);

        // Выбор идёт в трёхмерном виде, но эскиз НЕ закрывается: режим
        // черчения лишь приостанавливается.
        //
        // Иначе выбирать нечего: в эскизе камера смотрит прямо на плоскость,
        // остальная модель скрыта, а фильтр по уровню работы отбрасывает
        // всё, что эскизу не принадлежит. Но и терять эскиз нельзя —
        // пользователь вернётся к нему сразу после выбора.
        if (Mode == EditMode.Sketch)
        {
            _basePickResumeSketch = true;
            Mode = EditMode.Part;
            Selection.Filter = PickFilter.Solid | PickFilter.Datums | PickFilter.Sketch;
            RestoreCamera(TransitionKind.FromSketch);
            ModeChanged?.Invoke();
        }

        _basePick = kind;
        _basePickLimit = sketch.Id;
        _originCandidates = kind == BasePick.Origin
            ? SketchProjection.Collect(_doc, sketch).FindAll(c => c.Kind == ProjectedKind.Point)
            : new List<ProjectedRef>();

        Selection.Clear();
        CommandMessage?.Invoke(hint);
        MarkDirty();
    }

    /// <summary>
    /// Возвращает режим черчения, если выбор его приостанавливал.
    /// </summary>
    private void ResumeSketchAfterBasePick()
    {
        _originCandidates.Clear();
        if (!_basePickResumeSketch) return;

        _basePickResumeSketch = false;
        var sk = ActiveSketch;
        if (sk == null) return;

        // Камера возвращается на плоскость эскиза тем же путём, что
        // и при входе в него.
        Transition.Save(Camera.State);
        Mode = EditMode.Sketch;
        Selection.Filter = PickFilter.Sketch;

        var to = Camera.State;
        to.Rotation = LookAtPlane(sk.Plane);
        Vector3d o = sk.Plane.Origin;
        to.Target = new Vector3((float)o.X, (float)o.Y, (float)o.Z);
        to.Mode = ProjectionMode.Orthographic;

        StartTransition(TransitionKind.ToSketch, to, sk);
        ModeChanged?.Invoke();
        MarkDirty();
    }

    /// <summary>
    /// Точки, годные в нулевую: проекции чужой геометрии, приведённые
    /// к плоскости эскиза. Считаются один раз, в начале выбора.
    /// </summary>
    private List<ProjectedRef> _originCandidates = new();

    /// <summary>Проекция-кандидат под курсором во время выбора нуля.</summary>
    private ProjectedRef? _originHover;

    public void EndBasePick()
    {
        if (_basePick == BasePick.None) return;
        _basePick = BasePick.None;
        _originHover = null;
        CommandMessage?.Invoke(null);
        ResumeSketchAfterBasePick();
        MarkDirty();
    }

    /// <summary>Идёт ли выбор плоскости-базиса или нулевой точки.</summary>
    public bool InBasePick => _basePick != BasePick.None;

    /// <summary>
    /// Что показывать в режиме выбора: только созданное раньше эскиза.
    /// Всё остальное скрыто — по нему всё равно нельзя строиться,
    /// и показывать его значит предлагать невозможное.
    /// </summary>
    public uint SceneVisibilityLimit => InBasePick ? _basePickLimit : 0;

    /// <summary>
    /// Что годится в выборе плоскости-базиса или нулевой точки.
    ///
    /// Фильтр решает сразу две задачи. Во-первых, отсекает негодное
    /// от ПОДСВЕТКИ: подсвечивать ребро, когда просят плоскость, значит
    /// обещать невозможное. Во-вторых, освобождает дорогу нужному:
    /// кандидаты перебираются по возрастанию расстояния, и ребро,
    /// проходящее рядом с гранью, раньше забирало щелчок себе — грань
    /// подсвечивалась, но выбрать её было нельзя.
    ///
    /// Элементы САМОГО эскиза не годятся никогда: эскиз не может стоять
    /// на своей же плоскости и не может взять ноль из своей же точки.
    /// </summary>
    private PickRef ResolveBasePick(PickRef r)
    {
        if (ReferenceEquals(r.Owner, ActiveSketch)) return PickRef.Empty;

        if (_basePick == BasePick.Plane)
        {
            // Базовая плоскость, созданная раньше эскиза.
            if (r.Target is DatumPlane dp)
                return dp.Id < _basePickLimit ? r : PickRef.Empty;

            // Плоская грань тела. Отбор по идентификатору ДЕТАЛИ, а не грани:
            // грани создаются заново при каждой перестройке и всегда «моложе»
            // эскиза, так что по ним отобрать нельзя.
            if (r.Kind == PickKind.Face && r.Owner is Part part && part.Id < _basePickLimit
                && PlanarFaceFrame(part, r.Id) != null)
                return r;

            return PickRef.Empty;
        }

        // Нулевая точка: только то, что даёт однозначную точку.
        switch (r.Target)
        {
            case DatumPoint dpt: return dpt.Id < _basePickLimit ? r : PickRef.Empty;
            case DatumAxis ax: return ax.Id < _basePickLimit ? r : PickRef.Empty;

            case SkPoint when r.Owner is Sketch sp: return sp.Id < _basePickLimit ? r : PickRef.Empty;
            case SkCircle when r.Owner is Sketch sc: return sc.Id < _basePickLimit ? r : PickRef.Empty;
            case SkArc when r.Owner is Sketch sa: return sa.Id < _basePickLimit ? r : PickRef.Empty;
        }

        if (r.Kind == PickKind.Vertex && r.Owner is Part vp && vp.Id < _basePickLimit) return r;

        return PickRef.Empty;
    }

    private bool HandleBasePick(PickRef r)
    {
        if (_basePick == BasePick.None) return false;

        if (_basePick == BasePick.Plane)
        {
            switch (r.Target)
            {
                case DatumPlane dp when dp.Id < _basePickLimit:
                    _basePick = BasePick.None;
                    CommandMessage?.Invoke(null);
                    ResumeSketchAfterBasePick();
                    PlanePicked?.Invoke(dp.Frame, dp.Id, dp.Name);
                    return true;

                default:
                    // Плоская грань тела: система координат строится
                    // по её собственной плоскости.
                    if (r.Kind == PickKind.Face && r.Owner is Part fp
                        && PlanarFaceFrame(fp, r.Id) is { } frame)
                    {
                        _basePick = BasePick.None;
                        CommandMessage?.Invoke(null);
                        ResumeSketchAfterBasePick();
                        PlanePicked?.Invoke(frame, r.Id, $"{fp.Name} face #{r.Id}");
                        return true;
                    }

                    // Сюда попадает только промах: годное фильтр уже отобрал.
                    CommandMessage?.Invoke("Sketch: pick a datum plane or a planar face.");
                    return true;
            }
        }

        // Нулевая точка. Сначала проекции-кандидаты: они нарисованы поверх
        // модели и ловятся по экрану, как и рисуются.
        if (_originHover is { } cand)
        {
            var sk = ActiveSketch;
            _basePick = BasePick.None;
            _originHover = null;
            CommandMessage?.Invoke(null);
            ResumeSketchAfterBasePick();

            if (sk != null) OriginPicked?.Invoke(sk.Plane.ToWorld(cand.P), cand.Source);
            return true;
        }

        Vector3d? p = OriginCandidate(r, out string label);
        if (p == null)
        {
            CommandMessage?.Invoke("Sketch: pick a point, a vertex, a circle center or an axis.");
            return true;
        }

        _basePick = BasePick.None;
        CommandMessage?.Invoke(null);
        ResumeSketchAfterBasePick();
        OriginPicked?.Invoke(p.Value, label);
        return true;
    }

    /// <summary>
    /// Система координат плоской грани тела. null — грань не плоская.
    ///
    /// Проверяется признак IsPlanar самой поверхности, а НЕ её тип.
    /// Раньше принималась только PlaneSurface, то есть крышки выдавливания.
    /// Но боковая грань, выдавленная из прямого отрезка, тоже плоская —
    /// просто описана как поверхность выдавливания. Из-за проверки по типу
    /// перпендикулярные грани даже не подсвечивались.
    ///
    /// Начало координат ставится в геометрический центр грани: средняя
    /// точка её вершин, снесённая на плоскость. Для эскиза это привычнее,
    /// чем начало параметризации поверхности, которое может лежать
    /// в стороне.
    /// </summary>
    private static Frame3? PlanarFaceFrame(Part part, uint faceId)
    {
        foreach (var b in part.Bodies)
            foreach (var f in b.Faces)
            {
                if (f.Id != faceId) continue;
                if (f.Surface is not { IsPlanar: true } surface) return null;

                Vector3d n = surface.NormalAt(new Vector2d(0.5, 0.5));
                if (n.LengthSquared < 1e-18) return null;
                if (f.Reversed) n = -n;

                Vector3d origin = FaceCenter(f) ?? surface.PointAt(new Vector2d(0.5, 0.5));
                return Frame3.FromNormal(origin, n.Normalized());
            }
        return null;
    }

    /// <summary>Средняя точка вершин грани. null — вершин нет.</summary>
    private static Vector3d? FaceCenter(Kernel.Topology.TFace f)
    {
        Vector3d sum = Vector3d.Zero;
        int n = 0;

        foreach (var e in f.Edges)
        {
            sum += e.V0.P; n++;
            sum += e.V1.P; n++;
        }

        return n == 0 ? null : sum / n;
    }

    /// <summary>
    /// Точка, годная в нулевую. Разбираются те случаи, которые имеют
    /// однозначный ответ: сама точка, вершина тела, центр окружности
    /// эскиза, начало оси.
    /// </summary>
    private Vector3d? OriginCandidate(PickRef r, out string label)
    {
        label = "";

        switch (r.Target)
        {
            case DatumPoint dpt:
                label = dpt.Name;
                return dpt.P;

            case DatumAxis ax:
                label = ax.Name + " origin";
                return ax.Origin;

            case SkPoint sp when r.Owner is Sketch osk:
                label = $"{osk.Name} point";
                return osk.Plane.ToWorld(sp.P);

            case SkCircle sc when r.Owner is Sketch csk:
                label = $"{csk.Name} circle center";
                return csk.Plane.ToWorld(sc.Center.P);

            case SkArc sa when r.Owner is Sketch ask:
                label = $"{ask.Name} arc center";
                return ask.Plane.ToWorld(sa.Center.P);
        }

        if (r.Kind == PickKind.Vertex && r.Owner is Part vp)
        {
            foreach (var b in vp.Bodies)
                foreach (var v in b.Vertices)
                    if (v.Id == r.Id) { label = $"{vp.Name} vertex #{v.Id}"; return v.P; }
        }

        return null;
    }

    /// <summary>Идёт ли набор элементов для ограничения или размера.</summary>
    public bool InPickMode => _pickConstraint != null || _pickDimension;

    /// <summary>Включает набор элементов для ограничения.</summary>
    public void BeginConstraintMode(ConstraintType type)
    {
        EndPickMode(silent: true);
        _pickConstraint = type;
        SeedPickBuffer();
        PickModeChanged?.Invoke();
        TryApplyPick();
    }

    /// <summary>Включает набор элементов для размера.</summary>
    public void BeginDimensionMode()
    {
        EndPickMode(silent: true);
        _pickDimension = true;
        SeedPickBuffer();
        PickModeChanged?.Invoke();
        TryApplyPick();
    }

    public void EndPickMode(bool silent = false)
    {
        bool was = InPickMode;
        _pickConstraint = null;
        _pickDimension = false;
        _pickBuffer.Clear();
        if (was && !silent)
        {
            CommandMessage?.Invoke(null);
            PickModeChanged?.Invoke();
        }
    }

    /// <summary>Уже выбранные элементы становятся началом набора.</summary>
    private void SeedPickBuffer()
    {
        _pickBuffer.Clear();
        foreach (var i in Selection.Items)
            if (i.Target is SkEntity e && !_pickBuffer.Contains(e)) _pickBuffer.Add(e);
    }

    /// <summary>Добавляет элемент в набор. true — событие поглощено режимом.</summary>
    private bool AddToPick(PickRef r)
    {
        if (!InPickMode) return false;

        // Щелчок мимо геометрии — это подтверждение набора. Так решается
        // неоднозначность: одна выбранная линия может стать и длиной,
        // и стороной угла, и приложение не должно гадать за пользователя.
        if (r.Target is not SkEntity e)
        {
            TryApplyPick(force: true);
            return true;
        }

        // Узел отсекается сразу, а не после набора: иначе пользователь
        // соберёт пару и лишь тогда узнает, что она не годится.
        if (_pickConstraint is ConstraintType ct && e is SkPoint
            && !SketchCommands.AcceptsPoints(ct))
        {
            CommandMessage?.Invoke($"{ct}: select a line or a curve, not a point.");
            return true;
        }

        if (!_pickBuffer.Contains(e)) _pickBuffer.Add(e);
        TryApplyPick();
        return true;
    }

    private void TryApplyPick(bool force = false)
    {
        if (_pickDimension)
        {
            if (_pickBuffer.Count == 0)
            {
                CommandMessage?.Invoke("Dimension: pick the items to measure.");
                return;
            }

            // Одна линия — случай неоднозначный: с ней можно померить
            // и собственную длину, и угол ко второй линии. Ждём: второй
            // щелчок по линии даст угол, щелчок по пустому месту — длину.
            if (!force && _pickBuffer.Count == 1 && _pickBuffer[0] is SkLine)
            {
                CommandMessage?.Invoke(
                    "Dimension: pick a second line for an angle, or click empty space for the length.");
                return;
            }

            string? err = SketchCommands.AddDimension(this, _pickBuffer.ToArray(), null, out var created);
            if (err == null && created != null)
            {
                // Размер создан и переходит в режим размещения: подпись
                // едет за курсором до следующего щелчка.
                _placingDim = created;
                _pickBuffer.Clear();
                Selection.Clear();
                CommandMessage?.Invoke("Dimension: click to place the text.");
            }
            else
            {
                // Не ошибка, а «пока мало данных»: ждём следующего щелчка.
                CommandMessage?.Invoke(err);
            }
            return;
        }

        if (_pickConstraint is not ConstraintType type) return;

        int need = SketchCommands.Arity(type);
        if (_pickBuffer.Count < need)
        {
            CommandMessage?.Invoke($"{type}: pick {need - _pickBuffer.Count} more item(s).");
            return;
        }

        var items = _pickBuffer.Take(need).ToArray();
        string? error = SketchCommands.ApplyConstraint(this, type, items);

        if (error != null)
        {
            // Набор не годится — оставляем только последний выбранный,
            // иначе пользователь застрянет на неудачной паре.
            var last = _pickBuffer[^1];
            _pickBuffer.Clear();
            _pickBuffer.Add(last);
            CommandMessage?.Invoke(error);
            return;
        }

        // Parallel и Equal назначаются цепочкой: образец остаётся,
        // каждый следующий элемент приравнивается к нему.
        if (SketchCommands.IsChainable(type))
        {
            var reference = _pickBuffer[0];
            _pickBuffer.Clear();
            _pickBuffer.Add(reference);
            Selection.Clear();
            CommandMessage?.Invoke($"{type}: pick the next item.");
        }
        else
        {
            _pickBuffer.Clear();
            Selection.Clear();
            CommandMessage?.Invoke($"{type}: pick the next pair.");
        }
    }

    /// <summary>Элемент, на который навели курсор в дереве проекта.</summary>
    private ModelItem? _treeHover;

    private void ApplyHighlight()
    {
        // Подсветка меняет буферы GPU и может прийти из дерева проекта,
        // а не из цикла отрисовки, поэтому контекст берём явно.
        if (!EnsureCurrent()) return;

        _scene.ClearFlags();

        foreach (var i in Selection.Items) Apply(i, Selection.FlagSelected);

        if (!Selection.Hover.IsEmpty && !Selection.Contains(Selection.Hover.Id))
        {
            Apply(Selection.Hover, Selection.FlagHover);

            // КРОМКА ПОДСВЕЧИВАЕТСЯ ЦЕЛИКОМ — ТАК ЖЕ, КАК ВЫБИРАЕТСЯ.
            //
            // Щелчок по ребру берёт всю касательную цепочку (SelectTangentChain),
            // а под курсором подсвечивался один сегмент: человек видел одно,
            // а получал другое. Отдельный сегмент кромки выбирать незачем —
            // операции идут по кромке целиком.
            foreach (uint id in ChainOf(Selection.Hover))
                if (!Selection.Contains(id)) _scene.SetFlags(id, Selection.FlagHover);
        }

        if (_treeHover != null) ApplyItem(_treeHover, Selection.FlagHover);

        void Apply(PickRef r, uint flags)
        {
            // Деталь в дереве и в режиме сборки — это не один диапазон буфера,
            // а все её грани, рёбра и вершины сразу.
            if (r.Target is Part part) { _scene.SetPartFlags(part.Id, flags); return; }
            if (r.Target is Sketch sketch) { _scene.SetSketchFlags(sketch.Id, flags); return; }
            if (r.Kind == PickKind.Body) { _scene.SetPartFlags(r.Id, flags); return; }
            _scene.SetFlags(r.Id, flags);
        }
    }

    /// <summary>Подсветка по элементу дерева: у детали и эскиза подсвечивается всё содержимое.</summary>
    private void ApplyItem(ModelItem item, uint flags)
    {
        switch (item)
        {
            case Part p: _scene.SetPartFlags(p.Id, flags); break;
            case Sketch sk: _scene.SetSketchFlags(sk.Id, flags); break;

            case Feature f:
                // Грань не помнит, кто её создал, поэтому связь заведена
                // на самой операции при перестройке (CreatedFaceIds).
                foreach (uint id in f.CreatedFaceIds) _scene.SetFlags(id, flags);

                // Заодно подсвечиваются её эскизы — видно, из чего сделано.
                foreach (uint id in f.ConsumedSketchIds())
                    _scene.SetSketchFlags(id, flags);
                break;

            default: _scene.SetFlags(item.Id, flags); break;
        }
    }

    /// <summary>Наведение курсора в дереве проекта. null снимает подсветку.</summary>
    public void HighlightFromTree(ModelItem? item)
    {
        if (ReferenceEquals(_treeHover, item)) return;
        _treeHover = item;
        ApplyHighlight();
        Invalidate();
    }

    /// <summary>Выбор строки в дереве переносится в выделение вида.</summary>
    public void SelectFromTree(ModelItem item)
    {
        PickRef r = item switch
        {
            Assembly a => new PickRef(a.Id, PickKind.Assembly, a.Parent, a),
            Part p => new PickRef(p.Id, PickKind.Body, p.Parent, p),
            Sketch sk => new PickRef(sk.Id, PickKind.SketchCurve, sk.Parent, sk),
            Feature f => new PickRef(f.Id, PickKind.Feature, f.Parent, f),
            DatumPlane dp => new PickRef(dp.Id, PickKind.DatumPlane, dp.Parent, dp),
            DatumAxis ax => new PickRef(ax.Id, PickKind.DatumAxis, ax.Parent, ax),
            DatumPoint pt => new PickRef(pt.Id, PickKind.DatumPoint, pt.Parent, pt),
            Line3DItem ln => new PickRef(ln.Id, PickKind.DatumAxis, ln.Parent, ln),
            _ => PickRef.Empty
        };

        if (r.IsEmpty) { Selection.Clear(); return; }
        Selection.Select(r, additive: false);
    }

    /// <summary>
    /// Превращает прочитанный из кадра id в ссылку на объект — с учётом
    /// РЕЖИМА, а не только пользовательского фильтра типов.
    ///
    /// Смысл режимов в том, на каком уровне работает пользователь:
    ///   Assembly — прямые потомки корневой сборки: детали целиком,
    ///              её собственные плоскости, оси, точки, линии, эскизы;
    ///   Part     — содержимое активной детали: грани, рёбра, вершины,
    ///              её плоскости, оси, точки, линии, эскизы;
    ///   Sketch   — только элементы активного эскиза.
    ///
    /// Без этой фильтрации в сборке ловились бы рёбра чужих деталей,
    /// а в детали — геометрия соседней.
    /// </summary>
    private PickRef ResolveForMode(PickRef r)
    {
        switch (Mode)
        {
            case EditMode.Assembly:
                {
                    // Грань, ребро или вершина означают выбор ДЕТАЛИ целиком:
                    // на уровне сборки оперируют деталями, а не их гранями.
                    if (r.Owner is Part part && r.Kind is PickKind.Face or PickKind.Edge or PickKind.Vertex)
                        return TopLevelOwner(part) is Part top
                            ? new PickRef(top.Id, PickKind.Body, top.Parent, top)
                            : PickRef.Empty;

                    // Всё остальное — плоскости, оси, точки, линии, эскизы —
                    // подсвечивается само по себе, лишь бы принадлежало
                    // прямому потомку корневой сборки. Иначе при пустой
                    // сборке (тел ещё нет) наводить было бы вообще не на что.
                    ModelItem? item = ItemOf(r);
                    return item != null && TopLevelOwner(item) != null ? r : PickRef.Empty;
                }

            case EditMode.Part:
                {
                    if (ActivePart == null) return PickRef.Empty;
                    ModelItem? item = ItemOf(r);
                    if (item != null) return BelongsToActivePart(item) ? r : PickRef.Empty;
                    return r.Owner is Part p && p == ActivePart ? r : PickRef.Empty;
                }

            default: // Sketch
                {
                    if (ActiveSketch == null) return PickRef.Empty;
                    return r.Owner is Sketch sk && sk == ActiveSketch ? r : PickRef.Empty;
                }
        }
    }

    /// <summary>Что можно выбрать в режиме указания оси.</summary>
    private PickRef ResolveAxisPick(PickRef r)
    {
        // Годятся только отрезки того эскиза, для которого строится вращение.
        if (r.Target is SkLine && ReferenceEquals(r.Owner, _axisPickSketch)) return r;
        return PickRef.Empty;
    }

    /// <summary>Узел модели, к которому относится ссылка: сам объект или его владелец.</summary>
    private static ModelItem? ItemOf(PickRef r) =>
        r.Target as ModelItem ?? r.Owner as ModelItem;

    /// <summary>Поднимается по дереву до прямого потомка корневой сборки.</summary>
    private ModelItem? TopLevelOwner(ModelItem item)
    {
        if (_doc == null) return null;
        for (ModelItem? p = item; p != null; p = p.Parent)
            if (ReferenceEquals(p.Parent, _doc.Root)) return p;
        return null;
    }

    private bool BelongsToActivePart(ModelItem item)
    {
        for (ModelItem? p = item; p != null; p = p.Parent)
            if (ReferenceEquals(p, ActivePart)) return true;
        return false;
    }

    // ---------------------------------------------------------------- ввод

    protected override bool OnHoverMove(MouseEventArgs e)
    {
        // Пока размер размещают, курсор тянет за собой его подпись.
        if (_placingDim != null && ActiveSketch != null)
        {
            _placingDim.TextOffset = ScreenToSketch(e.X, e.Y) - DimensionOrigin(_placingDim);
            _placingDim.UserPlaced = true;
            MarkDirty();
            return true;
        }

        if (Tool == null) return false;
        Tool.OnMouseHover(this, e);
        return true;
    }

    /// <summary>
    /// Точка, от которой отсчитывается смещение подписи. Для линейных
    /// размеров это середина измеряемого отрезка, для остальных — опорная
    /// точка элемента.
    /// </summary>
    private static Vector2d DimensionOrigin(SkDimension d)
    {
        var it = d.Items;
        if (it.Count >= 2 && it[0] is SkPoint a && it[1] is SkPoint b) return (a.P + b.P) * 0.5;
        if (it.Count >= 1)
        {
            switch (it[0])
            {
                case SkCircle c: return c.Center.P;
                case SkArc arc: return arc.Center.P;
                case SkEllipse el: return el.Center.P;
                case SkLine l: return (l.A.P + l.B.P) * 0.5;
                case SkPoint p: return p.P;
            }
        }
        return Vector2d.Zero;
    }

    protected override void OnHoverPick(Point pos, uint id)
    {
        bool wasArrow = _arrowHover;
        _arrowHover = HitViewArrow(pos, out _);

        var wasOrigin = _originHover;
        if (_basePick == BasePick.Origin) UpdateOriginHover(pos);

        PickRef r = PickAtPoint(pos);
        var wasProjection = _hoverProjection;

        // Проекция ищется только там, где нет своей геометрии: это
        // подсказка, и перебивать собственные линии эскиза она не должна.
        if (r.IsEmpty) UpdateHoverProjection(pos);
        else _hoverProjection = null;

        bool selectionChanged = Selection.SetHover(r);

        // Смена проекции под курсором — тоже смена того, «что под курсором».
        // Без этого извещения строка состояния не обновлялась бы: выделение
        // при этом остаётся пустым, и SetHover ничего не сообщает.
        if (!selectionChanged && (!ReferenceEquals(wasProjection, _hoverProjection)
                                  || !ReferenceEquals(wasOrigin, _originHover)))
            SelectionChanged?.Invoke();

        if (wasArrow != _arrowHover) MarkDirty();

        Invalidate();
    }

    /// <summary>
    /// Что находится под курсором. Перебираются кандидаты от лучшего
    /// к худшему, берётся первый, проходящий фильтр режима и типов.
    /// </summary>
    public PickRef PickAtPoint(Point pos)
    {
        if (_doc == null) return PickRef.Empty;

        _picker.ActiveOwner = ActiveOwnerNow();
        var hits = _picker.PickAll(_doc, Camera, pos,
                                   _scene.ShowDatumPlanes, ActiveSketch);

        foreach (var h in hits)
        {
            // Выбор плоскости-базиса и нуля идёт по всей модели, но принимает
            // ДАЛЕКО не всё: фильтр здесь свой.
            if (InBasePick)
            {
                PickRef bp = ResolveBasePick(h.Ref);
                if (!bp.IsEmpty) return bp;
                continue;
            }

            if (_axisPickSketch != null)
            {
                PickRef axis = ResolveAxisPick(h.Ref);
                if (!axis.IsEmpty) return axis;
                continue;
            }

            PickRef r = ResolveForMode(h.Ref);
            if (!r.IsEmpty && Selection.Accepts(r.Kind)) return r;
        }
        return PickRef.Empty;
    }

    /// <summary>
    /// Активный инструмент эскиза. Пока он установлен, вид отдаёт ему ввод
    /// вместо обычного выбора. Так тулбар эскиза не превращается в набор
    /// условий внутри обработчиков мыши самого вида.
    /// </summary>
    public ISketchTool? Tool;

    protected override void OnPickDown(MouseEventArgs e, uint id)
    {
        if (Tool != null && Tool.OnMouseDown(this, e))
        {
            if (Tool.Finished) SetTool(Tool.Continuous ? Tool.Restart() : null);
            Invalidate();
            return;
        }
        // Размещение подписи: щелчок фиксирует её и завершает размер.
        if (_placingDim != null)
        {
            _placingDim.UserPlaced = true;
            _placingDim = null;
            CommandMessage?.Invoke("Dimension: pick the items to measure.");
            MarkDirty();
            return;
        }

        // Стрелка взгляда проверяется до всего остального: она нарисована
        // поверх модели и перехватывает щелчок по себе.
        if (HandleViewArrowClick(e.Location)) return;

        PickRef r = PickAtPoint(e.Location);

        if (HandleBasePick(r)) return;
        if (HandleSketchPick(r)) return;

        // Щелчок по проекции переносит её в эскиз ссылочной геометрией.
        if (r.IsEmpty && Tool == null && !InPickMode && ProjectHovered()) return;

        // Выбор оси перехватывает щелчок целиком: ничего не выделяем
        // и не начинаем перетаскивание.
        if (_axisPickSketch != null)
        {
            if (r.Target is SkLine line)
            {
                var sketch = _axisPickSketch;
                _axisPickSketch = null;
                CommandMessage?.Invoke(null);
                AxisPicked?.Invoke(sketch, line);
            }
            else CommandMessage?.Invoke("Revolve: that is not a segment. Click a line.");
            return;
        }

        Selection.Select(r, InPickMode || ModifierKeys.HasFlag(Keys.Control));
        SelectTangentChain(r);

        if (AddToPick(r)) return;

        BeginSketchDrag(r, e.Location);
    }

    /// <summary>
    /// КРОМКА ВЫБИРАЕТСЯ ЦЕЛИКОМ, А НЕ КУСКОМ.
    ///
    /// Скругление разрывает ребро на части: прямой участок, дуга, прямой
    /// участок. Человек видит одну кромку и ждёт, что щелчок возьмёт её
    /// всю; топология видит три ребра. Здесь к выбранному ребру
    /// добавляются его касательные продолжения.
    ///
    /// Снятие — тоже целиком: щелчок с CTRL по выбранному ребру снимает всю
    /// кромку. Отдельный сегмент выбирать незачем — операции идут по кромке.
    ///
    /// Цепочка не строится, когда выбор ограничен одним элементом
    /// (InPickMode): там ждут ровно один объект, и добавить к нему что-то
    /// значит ответить не на тот вопрос.
    /// </summary>
    /// <summary>
    /// Остальные рёбра касательной цепочки ребра под курсором — для подсветки.
    /// Запоминается по номеру ребра и сетке детали: курсор движется часто,
    /// а цепочка меняется только при смене ребра или перестроении детали.
    /// </summary>
    private IReadOnlyList<uint> ChainOf(PickRef r)
    {
        if (InPickMode || r.Kind != PickKind.Edge || r.Owner is not Part part)
            return Array.Empty<uint>();

        if (r.Id == _chainOfId && ReferenceEquals(_chainOfMesh, part.Mesh)) return _chainOf;

        _chainOfId = r.Id;
        _chainOfMesh = part.Mesh;
        _chainOf = new List<uint>();

        foreach (TSolid b in part.Bodies)
        {
            TEdge? edge = b.Edges.FirstOrDefault(e => e.Id == r.Id);
            if (edge == null) continue;

            foreach (TEdge next in EdgeChain.Grow(b, new[] { edge }, out _))
                if (!ReferenceEquals(next, edge) && !next.Hidden) _chainOf.Add(next.Id);

            break;
        }

        return _chainOf;
    }

    private uint _chainOfId;
    private object? _chainOfMesh;
    private List<uint> _chainOf = new();

    private void SelectTangentChain(PickRef r)
    {
        if (InPickMode) return;
        if (r.Kind != PickKind.Edge) return;
        if (r.Owner is not Part part) return;

        // РЕБРО ИЩЕТСЯ ПО НОМЕРУ, А НЕ БЕРЁТСЯ ИЗ ССЫЛКИ.
        //
        // ИСПРАВЛЕНО. Здесь стояло `r.Target is not TEdge`, и условие
        // не выполнялось НИКОГДА: ScenePicker намеренно кладёт в ссылку
        // только номер и владельца, а Target оставляет пустым — искать
        // топологический объект для каждого кандидата под курсором значит
        // делать линейный поиск внутри цикла по всей сетке на каждое
        // движение мыши. Цепочка из-за этого молча не строилась.
        TSolid? body = null;
        TEdge? edge = null;

        foreach (TSolid b in part.Bodies)
        {
            edge = b.Edges.FirstOrDefault(e => e.Id == r.Id);
            if (edge != null) { body = b; break; }
        }

        if (body == null || edge == null) return;

        int added = 0;

        // Список берётся в переменную, а не вызывается прямо в foreach:
        // out-переменная, объявленная в выражении коллекции, живёт
        // только внутри самого цикла и после него не видна.
        List<TEdge> chain = EdgeChain.Grow(body, new[] { edge }, out string why);

        // Щелчок с CTRL по выбранному ребру его снял — снимается вся кромка.
        bool removing = !Selection.Contains(edge.Id);

        foreach (TEdge next in chain)
        {
            if (ReferenceEquals(next, edge)) continue;
            if (next.Hidden) continue;               // гладкий шов не показывается, и выбирать его незачем

            bool selected = Selection.Contains(next.Id);
            if (removing != selected) continue;      // снимать невыбранное и добавлять выбранное незачем

            // Повторный дополнительный выбор снимает, первый — добавляет.
            Selection.Select(new PickRef(next.Id, PickKind.Edge, part, next), additive: true);
            added++;
        }

        if (removing)
        {
            if (added > 0) CommandMessage?.Invoke($"Tangent chain: {added + 1} edges deselected.");
            return;
        }

        // Цепочка сообщает о себе ВСЕГДА, а не только когда выросла.
        //
        // «Продолжений нет» и «код не отработал» выглядят на экране
        // одинаково: ничего не подсветилось. Сообщение различает их,
        // и при отказе называет причину числом — сколько рёбер сошлось
        // в концах и какой излом оказался наименьшим.
        CommandMessage?.Invoke(added > 0
            ? $"Tangent chain: {added + 1} edges selected."
            : $"Tangent chain: nothing added ({why}).");
    }

    // ------------------------------------------------- перетаскивание в эскизе

    /// <summary>Точка эскиза, которую сейчас тянет пользователь.</summary>
    private SkPoint? _dragPoint;
    /// <summary>Окружность, у которой сейчас тянут радиус.</summary>
    private SkCircle? _dragRadius;
    /// <summary>Эллипс, у которого сейчас тянут полуось.</summary>
    private SkEllipse? _dragEllipse;
    /// <summary>Принадлежит ли объект ссылочной геометрии.</summary>
    private static bool IsReference(object? target) => target switch
    {
        SkPoint p => p.Role == SkRole.Reference || p.Owner?.Role == SkRole.Reference,
        SkEntity e => e.Role == SkRole.Reference,
        _ => false
    };

    /// <summary>Отрезок, который сейчас двигают целиком.</summary>
    private SkLine? _dragLine;
    /// <summary>Размер, подпись которого перетаскивают.</summary>
    private SkDimension? _dragDim;
    /// <summary>Позиция курсора в координатах эскиза на прошлом шаге перетаскивания.</summary>
    private Vector2d _dragPrev;

    /// <summary>
    /// Начало перетаскивания. Тянуть можно только элементы активного эскиза
    /// и только когда не выбран инструмент рисования.
    ///
    /// «Зафиксирована ли точка ограничениями» здесь НЕ проверяется заранее:
    /// это решает решатель. Точка идёт за курсором с большим весом, остальное
    /// подстраивается, а если ограничения держат её жёстко — она просто
    /// не сдвинется. Так поведение всегда согласовано с моделью, а не
    /// с отдельным списком «что можно двигать».
    /// </summary>
    private void BeginSketchDrag(PickRef r, Point mouse)
    {
        _dragPoint = null;
        _dragRadius = null;
        _dragEllipse = null;
        _dragLine = null;
        _dragDim = null;

        if (Mode != EditMode.Sketch || ActiveSketch == null || Tool != null) return;

        // Ссылочную геометрию двигать нельзя: она принадлежит другому
        // эскизу или телу, и её положение здесь только отображается.
        if (IsReference(r.Target)) 
        {
            CommandMessage?.Invoke("Reference geometry belongs to another item and cannot be moved.");
            return;
        }

        // Подпись размера тянется независимо от геометрии.
        if (r.Target is SkDimension dim)
        {
            _dragDim = dim;
            _dragPrev = ScreenToSketch(mouse.X, mouse.Y);
            return;
        }

        _dragPrev = ScreenToSketch(mouse.X, mouse.Y);

        switch (r.Target)
        {
            // Отрезок двигается целиком: оба конца смещаются на один вектор.
            case SkLine l when !l.Fixed && !l.A.Fixed && !l.B.Fixed:
                _dragLine = l;
                break;

            case SkPoint p when !p.Fixed:
                _dragPoint = p;
                break;

            // Клик по самой окружности, а не по её центру, меняет радиус.
            case SkCircle c when !c.Fixed:
                _dragRadius = c;
                break;

            case SkEllipse el when !el.Fixed:
                _dragEllipse = el;
                break;
        }
    }

    private bool DragSketch(MouseEventArgs e)
    {
        var sk = ActiveSketch;
        if (sk == null) return false;

        Vector2d p = ScreenToSketch(e.X, e.Y);
        if (sk.SnapToGrid) p = sk.SnapPoint(p);

        if (_dragDim != null)
        {
            _dragDim.TextOffset += p - _dragPrev;
            _dragDim.UserPlaced = true;
            _dragPrev = p;
            MarkDirty();
            return true;
        }

        if (_dragPoint != null)
        {
            _dragPoint.X = p.X;
            _dragPoint.Y = p.Y;
            sk.Solve(_doc.Solver, _dragPoint);
        }
        else if (_dragRadius != null)
        {
            // Центр остаётся на месте, меняется только радиус.
            double r = (p - _dragRadius.Center.P).Length;
            if (r > Tol.Weld) _dragRadius.Radius = r;
            sk.Solve(_doc.Solver);
        }
        else if (_dragLine != null)
        {
            // Двигаем на приращение курсора, а не «в точку»: иначе отрезок
            // прыгнул бы серединой под курсор в первый же кадр.
            Vector2d d = p - _dragPrev;
            _dragLine.A.X += d.X; _dragLine.A.Y += d.Y;
            _dragLine.B.X += d.X; _dragLine.B.Y += d.Y;
            sk.Solve(_doc.Solver);
        }
        else if (_dragEllipse != null)
        {
            // Тянем ту полуось, к направлению которой курсор ближе.
            Vector2d d = p - _dragEllipse.Center.P;
            double rot = _dragEllipse.Rotation;
            double along = d.X * Math.Cos(rot) + d.Y * Math.Sin(rot);
            double across = -d.X * Math.Sin(rot) + d.Y * Math.Cos(rot);

            if (Math.Abs(along) >= Math.Abs(across))
            {
                if (Math.Abs(along) > Tol.Weld) _dragEllipse.RU = Math.Abs(along);
            }
            else
            {
                if (Math.Abs(across) > Tol.Weld) _dragEllipse.RV = Math.Abs(across);
            }
            sk.Solve(_doc.Solver);
        }
        else return false;

        _dragPrev = p;
        MarkDirty();
        return true;
    }

    private void EndSketchDrag()
    {
        if (_dragPoint == null && _dragRadius == null && _dragEllipse == null
            && _dragLine == null && _dragDim == null) return;

        // Отпустили кнопку — решаем ещё раз, уже БЕЗ удержания точки
        // у курсора. Во время перетаскивания оно слегка тянет геометрию
        // от положения, точно удовлетворяющего ограничениям; этот
        // последний проход возвращает её ровно на место.
        if (_dragPoint != null || _dragLine != null || _dragRadius != null || _dragEllipse != null)
            ActiveSketch?.Solve(_doc.Solver);
        _dragPoint = null;
        _dragRadius = null;
        _dragEllipse = null;
        _dragLine = null;
        _dragDim = null;
        MarkDirty();
    }

    protected override bool OnPickDrag(MouseEventArgs e, int dx, int dy)
    {
        if (DragSketch(e)) return true;

        if (Tool != null && Tool.OnMouseDrag(this, e)) return true;
        return base.OnPickDrag(e, dx, dy);
    }

    protected override void OnContextClick(MouseEventArgs e)
    {
        if (Mode != EditMode.Sketch) return;
        var r = PickAtPoint(e.Location);
        if (r.Target is SkDimension dim) DimensionMenuRequested?.Invoke(dim, e.Location);
    }

    protected override void OnPickUp(MouseEventArgs e, bool wasDrag)
    {
        EndSketchDrag();
        Tool?.OnMouseUp(this, e, wasDrag);
        base.OnPickUp(e, wasDrag);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (EditingLocked) return;
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;

        var r = PickAtPoint(e.Location);
        if (r.IsEmpty) return;

        // Двойной щелчок по подписи размера — правка значения.
        if (r.Target is SkDimension dim)
        {
            DimensionEditRequested?.Invoke(dim);
            return;
        }

        switch (Mode)
        {
            case EditMode.Assembly when r.Owner is Part p || r.Target is Part:
                EnterPart(r.Target as Part ?? (Part)r.Owner!);
                break;
            case EditMode.Part when r.Target is Sketch sk:
                EnterSketch(sk);
                break;
            case EditMode.Part when r.Kind == PickKind.DatumPlane && r.Target is DatumPlane dp:
                NewSketchOn(dp);
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (EditingLocked) { base.OnKeyDown(e); return; }
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            if (_sketchPick) EndSketchPick();
            else if (InBasePick) EndBasePick();
            else if (_axisPickSketch != null) EndAxisPick();
            else if (_placingDim != null) { _placingDim = null; MarkDirty(); }
            else if (InPickMode) EndPickMode();
            else if (Tool != null) { Tool.Cancel(this); Tool = null; ToolChanged?.Invoke(); }
            else if (!Selection.IsEmpty) Selection.Clear();
            else GoUp();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F && e.Control)
        {
            ZoomToFit(CurrentBounds());
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------- уровни

    public void EnterPart(Part p)
    {
        if (Mode == EditMode.Part && ActivePart == p) return;
        Transition.Save(Camera.State);
        ActivePart = p;
        Mode = EditMode.Part;
        Selection.Clear();
        Selection.Filter = PickFilter.Solid | PickFilter.Datums | PickFilter.Sketch;

        var to = Camera.State;
        var b = p.Bounds;
        if (!b.IsEmpty)
        {
            var saved = Camera.State;
            Camera.ZoomToFit(b.Min.ToF(), b.Max.ToF());
            to = Camera.State;
            Camera.State = saved;
        }
        StartTransition(TransitionKind.ToItem, to, p);
        MarkDirty();
        _hoverProjection = null;
        ModeChanged?.Invoke();
    }

    /// <summary>
    /// Состояние эскиза на момент входа. По нему кнопка Cancel возвращает
    /// его к исходному виду: журнала операций в приложении нет, поэтому
    /// отмена работает снимком.
    /// </summary>
    private SketchState? _sketchUndo;

    /// <summary>
    /// Закрывает эскиз, ОТМЕНЯЯ всё, что в нём наделали, и выходит
    /// в трёхмерный вид.
    /// </summary>
    public void CancelSketch()
    {
        var sk = ActiveSketch;
        if (sk == null) return;

        SetTool(null);

        if (_sketchUndo != null)
        {
            _sketchUndo.RestoreInto(sk);
            sk.Solve(_doc.Solver);
        }

        _sketchUndo = null;
        InvalidateOwner(sk);
        GoUp();
        MarkDirty();
    }

    /// <summary>Закрывает эскиз, СОХРАНЯЯ правки.</summary>
    public void FinishSketch()
    {
        var sk = ActiveSketch;
        SetTool(null);
        _sketchUndo = null;
        InvalidateOwner(sk);
        GoUp();
    }

    /// <summary>
    /// Помечает деталь на пересчёт после правки её эскиза.
    ///
    /// Перетаскивание узлов меняет координаты напрямую, минуя добавление
    /// и удаление элементов, поэтому деталь о правке не узнаёт и операции,
    /// построенные по этому эскизу, остались бы от прежней формы.
    /// Пересчёт идёт до полоски расчёта — что ниже неё, не строится.
    /// </summary>
    private void InvalidateOwner(Sketch? sk)
    {
        if (sk?.Parent is Part part)
        {
            part.Invalidate();
            _doc.RebuildDirty();
        }
    }

    /// <summary>
    /// Заново нацеливает камеру на плоскость активного эскиза.
    ///
    /// Нужно после поворота его осей вектором «вверх»: камера была нацелена
    /// по прежним осям, и после поворота сетка с чертежом оказывались
    /// повёрнутыми относительно экрана. Сам чертёж при этом не двигался —
    /// съезжал именно взгляд.
    /// </summary>
    public void RealignSketchCamera()
    {
        var sk = ActiveSketch;
        if (sk == null || Mode != EditMode.Sketch) return;

        Transition.Save(Camera.State);

        var to = Camera.State;
        to.Rotation = LookAtPlane(sk.Plane);
        Vector3d o = sk.Plane.Origin;
        to.Target = new Vector3((float)o.X, (float)o.Y, (float)o.Z);
        to.Mode = ProjectionMode.Orthographic;

        StartTransition(TransitionKind.ToSketch, to, sk);
        MarkDirty();
    }

    public void EnterSketch(Sketch sk)
    {
        Transition.Save(Camera.State);
        _sketchUndo = SketchState.Capture(sk);
        ActiveSketch = sk;
        Mode = EditMode.Sketch;
        Selection.Clear();
        Selection.Filter = PickFilter.Sketch;

        // Камера смотрит перпендикулярно плоскости эскиза.
        var to = Camera.State;
        to.Rotation = LookAtPlane(sk.Plane);
        Vector3d o = sk.Plane.Origin;
        to.Target = new Vector3((float)o.X, (float)o.Y, (float)o.Z);
        to.Mode = ProjectionMode.Orthographic;

        StartTransition(TransitionKind.ToSketch, to, sk);
        MarkDirty();
        _hoverProjection = null;
        ModeChanged?.Invoke();
    }

    public Sketch NewSketchOn(DatumPlane dp)
    {
        var part = ActivePart ?? _doc.AllParts().FirstOrDefault() ?? _doc.Root.AddPart("Part1");
        var sk = part.AddSketch($"Sketch{part.Sketches.Count() + 1}", dp.Frame, dp.Id);
        EnterSketch(sk);
        return sk;
    }

    /// <summary>Escape: на уровень выше с возвратом камеры.</summary>
    public void GoUp()
    {
        switch (Mode)
        {
            case EditMode.Sketch:
                ActiveSketch = null;
                Mode = EditMode.Part;
                Selection.Filter = PickFilter.Solid | PickFilter.Datums | PickFilter.Sketch;
                RestoreCamera(TransitionKind.FromSketch);
                break;
            case EditMode.Part:
                Mode = EditMode.Assembly;
                Selection.Filter = PickFilter.Parts | PickFilter.Datums | PickFilter.Sketch;
                RestoreCamera(TransitionKind.ToDefault);
                break;
        }
        Selection.Clear();
        MarkDirty();
        _hoverProjection = null;
        ModeChanged?.Invoke();
    }

    private void RestoreCamera(TransitionKind kind)
    {
        if (Transition.HasSaved) StartTransition(kind, Transition.Saved, null);
        else Invalidate();
    }

    private static Quaternion LookAtPlane(Frame3 plane)
    {
        // Матрица вида, у которой +W плоскости направлен на зрителя.
        Vector3 u = new((float)plane.U.X, (float)plane.U.Y, (float)plane.U.Z);
        Vector3 v = new((float)plane.V.X, (float)plane.V.Y, (float)plane.V.Z);
        Vector3 w = new((float)plane.W.X, (float)plane.W.Y, (float)plane.W.Z);
        var m = new Matrix3(u, v, w);      // строки — оси плоскости
        return Quaternion.FromMatrix(m);
    }

    /// <summary>
    /// Удаляет выбранное. Что именно можно удалить, зависит от режима:
    /// в эскизе — его элементы, в детали — фичи, эскизы и вспомогательную
    /// геометрию, в сборке — детали и собственную геометрию сборки.
    ///
    /// Базовые плоскости и начало координат не удаляются: на них
    /// опирается всё построение.
    /// </summary>
    public int DeleteSelected()
    {
        if (_doc == null || Selection.IsEmpty) return 0;

        if (Mode == EditMode.Sketch)
        {
            var sk = ActiveSketch;
            if (sk == null) return 0;
            int n = 0;

            foreach (var i in Selection.Items.ToArray())
            {
                switch (i.Target)
                {
                    case SkEntity e: sk.RemoveEntity(e); n++; break;
                    case SkConstraint c: sk.Constraints.Remove(c); n++; break;
                    case SkDimension d: sk.Dimensions.Remove(d); n++; break;
                }
            }

            if (n > 0) sk.Solve(_doc.Solver);
            Selection.Clear();
            MarkDirty();
            return n;
        }

        int removed = 0;
        foreach (var i in Selection.Items.ToArray())
        {
            switch (i.Target)
            {
                case Feature f when f.Parent is Part fp:
                    fp.RemoveFeature(f);
                    fp.Invalidate();
                    removed++;
                    break;

                case Part p when p.Parent is ModelItem parent:
                    parent.Remove(p);
                    removed++;
                    break;

                case Sketch sk when sk.Parent is ModelItem sp:
                    sp.Remove(sk);
                    if (ActiveSketch == sk) ActiveSketch = null;
                    (sp as Part)?.Invalidate();
                    removed++;
                    break;

                case DatumPlane dp when !IsBasePlane(dp) && dp.Parent is ModelItem pp:
                    pp.Remove(dp);
                    removed++;
                    break;

                case DatumAxis ax when ax.Parent is ModelItem ap:
                    ap.Remove(ax);
                    removed++;
                    break;

                case DatumPoint pt when !IsOrigin(pt) && pt.Parent is ModelItem ptp:
                    ptp.Remove(pt);
                    removed++;
                    break;

                case Line3DItem ln when ln.Parent is ModelItem lp:
                    lp.Remove(ln);
                    removed++;
                    break;
            }
        }

        if (ActivePart != null && !_doc.AllParts().Contains(ActivePart))
            ActivePart = _doc.AllParts().FirstOrDefault();

        Selection.Clear();
        MarkDirty();
        return removed;
    }

    private bool IsBasePlane(DatumPlane dp) => dp.Parent switch
    {
        Part p => dp == p.PlaneXY || dp == p.PlaneXZ || dp == p.PlaneYZ,
        Assembly a => dp == a.PlaneXY || dp == a.PlaneXZ || dp == a.PlaneYZ,
        _ => false
    };

    private static bool IsOrigin(DatumPoint pt) => pt.Parent switch
    {
        Part p => pt == p.Origin,
        Assembly a => pt == a.Origin,
        _ => false
    };

    /// <summary>Узел, который сейчас активен, — для выделения жирным в дереве.</summary>
    public ModelItem? ActiveItem => Mode switch
    {
        EditMode.Sketch => ActiveSketch,
        EditMode.Part => ActivePart,
        _ => _doc?.Root
    };

    /// <summary>
    /// Разворачивает камеру перпендикулярно плоскости активного эскиза.
    /// Если эскиза нет — ставит изометрию.
    /// </summary>
    public void LookAtActivePlane()
    {
        if (ActiveSketch == null)
        {
            AnimateTo(ViewOrientation.Isometric);
            return;
        }

        var to = Camera.State;
        to.Rotation = LookAtPlane(ActiveSketch.Plane);
        to.Mode = ProjectionMode.Orthographic;
        StartTransition(TransitionKind.Reorient, to, null);
    }

    public Bounds3 CurrentBounds()
    {
        if (_doc == null) return Bounds3.Empty;
        if (Mode == EditMode.Part && ActivePart != null && !ActivePart.Bounds.IsEmpty)
            return ActivePart.Bounds;
        var b = _doc.Bounds;
        if (b.IsEmpty)
        {
            b = Bounds3.Empty;
            b.Add(new Vector3d(-50, -50, -50));
            b.Add(new Vector3d(50, 50, 50));
        }
        return b;
    }

    protected override void OnTransitionCompleted(TransitionKind kind, object? payload)
    {
        MarkDirty();
    }

    /// <summary>
    /// Контекст уходит — вместе с ним и всё, что в него загружено.
    ///
    /// Номера буферов и шейдеров выдаёт КОНТЕКСТ, а не драйвер вообще:
    /// в новом контексте прежний номер либо не значит ничего, либо
    /// значит чужой объект. Поэтому сцена сбрасывается целиком, а флаг
    /// пересборки взводится — следующий кадр в новом контексте построит
    /// буферы заново.
    /// </summary>
    protected override void OnGLReleasing()
    {
        _scene.Dispose();
        _sceneDirty = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scene.Dispose();
            _grid.Dispose();
            _previewLines.Dispose();
            _previewPoints.Dispose();
        }
        base.Dispose(disposing);
    }
}
