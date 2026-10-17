using Avalonia.Input;
using Parametric3D.App.Operations;
using Parametric3D.App.Services;
using Parametric3D.App.Sketching;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Commands;

/// <summary>
/// Команды приложения: файл, правка, вид (стиль, проекция, «Опорные»), операции модели, инструменты эскиза, приложение
/// (тема, язык, свойства, параметры). Имена — по-русски (на экране — Loc.L), иконки — UI_ICONS.md, клавиши — ТЗ_UI 0.6.
/// Виды спереди/сверху/справа — только в палитре и в кубе вида (п. 8).
/// </summary>
public static class StandardCommands
{
    public static void Register(MainWindow w)
    {
        var r = CommandRegistry.Instance;
        r.Clear();
        var vp = w.ViewPort;
        KeyGesture K(Key k, KeyModifiers m = KeyModifiers.None) => new(k, m);
        bool HasPart() => w.Session?.Part != null;
        bool Modeling() => HasPart() && !w.InSketch;

        // ------------------------------------------------------------ файл
        const string F = "Файл";
        r.Add(new AppCommand { Short = "Новая", Id = "file.new_part", Label = "Новая деталь", Group = F, Icon = "new_part_but.svg", Gesture = K(Key.N, KeyModifiers.Control), Synonyms = "new part создать",
            Run = async () => { if (await w.ConfirmDiscard()) w.SetSession(DocumentSession.NewPart()); } });
        r.Add(new AppCommand { Id = "file.sample", Label = "Пробная деталь (куб 100)", Group = F, Synonyms = "sample cube test пример",
            Run = async () => { if (await w.ConfirmDiscard()) w.SetSession(DocumentSession.Sample()); } });
        r.Add(new AppCommand { Short = "Открыть", Id = "file.open", Label = "Открыть…", Group = F, Icon = "open_but.svg", Gesture = K(Key.O, KeyModifiers.Control), Synonyms = "open",
            Run = async () => await w.OpenAsync() });
        r.Add(new AppCommand { Short = "Сохранить", Id = "file.save", Label = "Сохранить", Group = F, Icon = "save_but.svg", Gesture = K(Key.S, KeyModifiers.Control), Synonyms = "save",
            CanRun = HasPart, Run = async () => await w.SaveAsync(false) });
        r.Add(new AppCommand { Id = "file.save_as", Label = "Сохранить как…", Group = F, Icon = "save_as_but.svg", Gesture = K(Key.S, KeyModifiers.Control | KeyModifiers.Shift), Synonyms = "save as",
            CanRun = HasPart, Run = async () => await w.SaveAsync(true) });
        r.Add(new AppCommand { Id = "file.import", Label = "Импорт (STEP, IGES)…", Group = F, Icon = "import_but.svg", Synonyms = "import step iges",
            Run = async () => await w.ImportAsync() });
        r.Add(new AppCommand { Id = "file.export", Label = "Экспорт (STEP, STL, OBJ)…", Group = F, Icon = "export_but.svg", Synonyms = "export step stl obj",
            CanRun = () => w.Session?.Part?.Bodies.Count > 0, Run = async () => await w.ExportAsync() });
        r.Add(new AppCommand { Id = "app.exit", Label = "Выход", Group = F, Synonyms = "exit quit закрыть", Run = () => w.Close() });

        // ------------------------------------------------------------ правка
        const string E = "Правка";
        r.Add(new AppCommand { Short = "↶", Id = "edit.undo", Label = "Отменить", Group = E, Icon = "undo_but.svg", Gesture = K(Key.Z, KeyModifiers.Control), Synonyms = "undo",
            CanRun = () => w.Session?.CanUndo == true && w.CurrentOperation == null && !w.InSketch, Run = () => w.Session.Undo() });
        r.Add(new AppCommand { Short = "↷", Id = "edit.redo", Label = "Вернуть", Group = E, Icon = "redo_but.svg", Gesture = K(Key.Y, KeyModifiers.Control), Synonyms = "redo повторить",
            CanRun = () => w.Session?.CanRedo == true && w.CurrentOperation == null && !w.InSketch, Run = () => w.Session.Redo() });
        r.Add(new AppCommand { Short = "▾", Id = "edit.history", Label = "Журнал отмены", Group = E, Icon = "undo_history_but.svg", Synonyms = "history undo журнал история",
            Run = () => w.OpenHistory() });
        r.Add(new AppCommand { Short = "Удалить", Id = "edit.delete", Label = "Удалить", Group = E, Icon = "delete_but.svg", Gesture = K(Key.Delete), Synonyms = "delete remove",
            CanRun = () => Modeling() && Deletable(w).Count > 0, Run = () => Delete(w) });
        r.Add(new AppCommand { Short = "Изменить", Id = "edit.feature", Label = "Изменить операцию", Group = E, Icon = "tree_edit_def_but.svg", Synonyms = "edit definition редактировать",
            CanRun = () => Modeling() && w.SelectedItems().Any(i => i is Feature or Sketch), Run = () => w.EditSelected() });
        r.Add(new AppCommand { Short = "Подавить", Id = "edit.suppress", Label = "Подавить / вернуть", Group = E, Icon = "tree_suppress_but.svg", Synonyms = "suppress resume",
            CanRun = () => Modeling() && w.SelectedItems().Any(i => i is Feature), Run = () => Suppress(w) });
        r.Add(new AppCommand { Short = "×", Id = "edit.clear_selection", Label = "Снять выбор", Group = E, Icon = "sel_clear_but.svg", Synonyms = "deselect clear",
            Run = () => vp.Selection.Clear() });

        // ------------------------------------------------------------ вид
        const string V = "Вид";
        void Orient(string id, string label, ViewOrientation o, string syn) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = V, Synonyms = syn, Run = () => vp.AnimateTo(Camera.OrientationQuat(o)) });
        Orient("view.front", "Вид спереди", ViewOrientation.Front, "front");
        Orient("view.back", "Вид сзади", ViewOrientation.Back, "back");
        Orient("view.left", "Вид слева", ViewOrientation.Left, "left");
        Orient("view.right", "Вид справа", ViewOrientation.Right, "right");
        Orient("view.top", "Вид сверху", ViewOrientation.Top, "top");
        Orient("view.bottom", "Вид снизу", ViewOrientation.Bottom, "bottom");
        r.Add(new AppCommand { Short = "Домой", Id = "view.home", Label = "Исходный вид (изометрия)", Group = V, Icon = "view_iso_but.svg", Gesture = K(Key.H), Synonyms = "iso isometric home домой",
            Run = () => vp.GoHome() });
        r.Add(new AppCommand { Short = "Вписать", Id = "view.fit", Label = "Вписать всё", Group = V, Icon = "view_fit_but.svg", Gesture = K(Key.F, KeyModifiers.Control), Synonyms = "fit zoom all",
            Run = () => vp.ZoomToFit() });
        r.Add(new AppCommand { Short = "Персп.", Id = "view.projection", Label = "Проекция: перспектива / ортогональ", Group = V, Icon = "view_perspective_but.svg", Gesture = K(Key.P), Synonyms = "perspective ortho ортогональная проекция изо",
            IsChecked = () => vp.IsPerspective, Run = () => vp.ToggleProjection() });
        void Style(string id, string label, string icon, DisplayStyle m, string syn, KeyGesture? g = null) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = V, Icon = icon, Synonyms = "style стиль " + syn, Gesture = g, IsChecked = () => vp.Style == m, Run = () => w.SetStyle(m) });
        Style("style.shaded", "Тонированный", "style_shaded_but.svg", DisplayStyle.Shaded, "shaded", K(Key.D1, KeyModifiers.Alt));
        Style("style.shaded_edges", "Тонированный с рёбрами", "style_shaded_edges_but.svg", DisplayStyle.ShadedWithEdges, "edges", K(Key.D2, KeyModifiers.Alt));
        Style("style.wireframe", "Каркас", "style_wireframe_but.svg", DisplayStyle.Wireframe, "wireframe каркас", K(Key.D3, KeyModifiers.Alt));
        Style("style.hidden_lines", "Невидимые линии штрихом", "style_hidden_lines_but.svg", DisplayStyle.HiddenLines, "hidden lines", K(Key.D4, KeyModifiers.Alt));
        Style("style.transparent", "Прозрачный", "style_transparent_but.svg", DisplayStyle.Transparent, "transparent", K(Key.D5, KeyModifiers.Alt));
        Style("style.material", "Материал", "style_material_but.svg", DisplayStyle.Material, "material материал металл realistic", K(Key.D6, KeyModifiers.Alt));
        Style("style.view_mesh", "Сетка вида", "style_mesh_but.svg", DisplayStyle.ViewMesh, "mesh triangles сетка треугольники вида", K(Key.D7, KeyModifiers.Alt));
        Style("style.fea_mesh", "Сетка FEA", "style_fea_mesh_but.svg", DisplayStyle.FeaMesh, "fea mesh расчёт сетка элементы мкэ", K(Key.D8, KeyModifiers.Alt));
        r.Add(new AppCommand { Short = "Скрыть", Id = "view.hide", Label = "Скрыть", Group = V, Icon = "hide_but.svg", Synonyms = "hide",
            CanRun = () => w.SelectedItems().Count > 0, Run = () => SetVisible(w, w.SelectedItems(), false) });
        r.Add(new AppCommand { Short = "Изолировать", Id = "view.isolate", Label = "Изолировать", Group = V, Icon = "isolate_but.svg", Synonyms = "isolate",
            CanRun = () => w.SelectedItems().Count > 0, Run = () => Isolate(w) });
        r.Add(new AppCommand { Short = "Показать всё", Id = "view.show_all", Label = "Показать всё", Group = V, Icon = "show_all_but.svg", Synonyms = "show all unhide",
            Run = () => SetVisible(w, w.Session.Document.Root.Descendants().Where(i => i is not Feature).ToList(), true) });
        r.Add(new AppCommand { Short = "Опорные", Id = "view.datums", Label = "Опорные элементы: показать / скрыть", Group = V, Icon = "datums_visible_but.svg", Synonyms = "datums planes плоскости",
            IsChecked = () => vp.Show.Planes || vp.Show.Axes || vp.Show.Points, Run = () => w.ToggleDatums() });
        foreach (var (key, label) in ShowItems)
            r.Add(new AppCommand { Id = "show." + key, Label = "Показывать: " + label, Group = V, Synonyms = "show показать " + key,
                IsChecked = () => GetShow(vp.Show, key), Run = () => { SetShow(vp.Show, key, !GetShow(vp.Show, key)); vp.MarkDirty(); w.RefreshToolStates(); } });

        // ------------------------------------------------------------ модель (операции)
        const string M = "Модель";
        // u12 (замечание 12): ОПЕРАЦИЯ ДОСТУПНА, ЕСЛИ ПОДХОДИТ ВЫБРАННОЕ. Пустой выбор — всё доступно (ссылки выбираются
        // в панели); иначе каждый выбранный элемент должен годиться операции: кромка (касательный стык) — не ребро для
        // скругления, фаски и массива; вершина — только для операций вершины; операции дерева — для зеркала и массивов.
        IReadOnlyList<PickRef> Sel() => vp.Selection.Items;
        bool Fits(Func<PickRef, bool> ok) { var l = Sel(); return l.Count == 0 || l.All(ok); }
        bool IsFeat(PickRef x) => x.Kind == PickKind.Feature && x.Target is Feature;
        bool RealEdge(PickRef x) => x.Kind == PickKind.Edge && !Topo.IsSeam(x);
        bool PlaneLike(PickRef x) => Refs.IsPlanar(x);
        bool SketchLike(PickRef x) => Refs.IsSketch(x) || x.Target is Sketch;
        bool Pointy(PickRef x) => x.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint;
        var fits = new Dictionary<string, Func<bool>>
        {
            ["op.sketch"] = () => Fits(x => PlaneLike(x) || x.Kind == PickKind.Edge) && (Sel().Count == 0 || Sel().Any(PlaneLike) || Topo.CoplanarFrame(Sel()) != null),
            ["op.extrude"] = () => Fits(x => SketchLike(x) || PlaneLike(x)),
            ["op.cut"] = () => Fits(x => SketchLike(x) || PlaneLike(x)),
            ["op.revolve"] = () => Fits(x => SketchLike(x) || PlaneLike(x) || Refs.IsLinear(x)),
            ["op.hole"] = () => Fits(x => PlaneLike(x) || Pointy(x)),
            ["op.round"] = () => Fits(RealEdge),
            ["op.chamfer"] = () => Fits(RealEdge),
            ["op.vertex_round"] = () => Fits(x => x.Kind == PickKind.Vertex),
            ["op.vertex_chamfer"] = () => Fits(x => x.Kind == PickKind.Vertex),
            ["op.shell"] = () => Fits(x => x.Kind == PickKind.Face),
            ["op.plane"] = () => Fits(x => !IsFeat(x)),
            ["op.mirror"] = () => Fits(x => IsFeat(x) || PlaneLike(x)),
            ["op.pattern_linear"] = () => Fits(x => IsFeat(x) || (Refs.IsLinear(x) && !Topo.IsSeam(x))),
            ["op.pattern_circular"] = () => Fits(x => IsFeat(x) || (Refs.IsLinear(x) && !Topo.IsSeam(x))),
        };
        void Op(string id, string label, string shortLabel, string icon, Func<Operation> make, string syn, KeyGesture? g = null) =>
            r.Add(new AppCommand { Id = id, Label = label, Short = shortLabel, Group = M, Icon = icon, Synonyms = syn, Gesture = g,
                CanRun = () => Modeling() && (w.CurrentOperation != null || !fits.TryGetValue(id, out var f) || f()), Run = () => w.StartOperation(make()) });
        Op("op.sketch", "Эскиз", "Эскиз", "f_sketch_but.svg", () => new SketchOp(), "sketch эскиз", K(Key.S));
        Op("op.extrude", "Выдавливание", "Выдавить", "f_extrude_but.svg", () => w.SketchFirst(new ExtrudeOp()), "extrude выдавить", K(Key.E));
        Op("op.cut", "Вырез выдавливанием", "Вырез", "f_extrude_but.svg", () => w.SketchFirst(new ExtrudeOp { StartCut = true }), "cut вырез");
        Op("op.revolve", "Вращение", "Вращение", "f_revolve_but.svg", () => w.SketchFirst(new RevolveOp()), "revolve вращать");
        Op("op.hole", "Отверстие", "Отверстие", "f_hole_but.svg", () => new HoleOp(), "hole отверстие");
        Op("op.round", "Скругление", "Скругление", "f_round_but.svg", () => new RoundOp(), "round fillet скругление", K(Key.R));
        Op("op.chamfer", "Фаска", "Фаска", "f_chamfer_but.svg", () => new ChamferOp(), "chamfer фаска", K(Key.C));
        Op("op.vertex_round", "Скругление вершины", "Скругл. вершины", "f_round_vertex_but.svg", () => new VertexRoundOp(), "vertex round вершина");
        Op("op.vertex_chamfer", "Фаска угла", "Фаска угла", "f_chamfer_vertex_but.svg", () => new VertexChamferOp(), "vertex chamfer corner угол");
        Op("op.shell", "Оболочка", "Оболочка", "f_shell_but.svg", () => new ShellOp(), "shell оболочка стенка");
        Op("op.plane", "Опорная плоскость", "Плоскость", "dat_plane_but.svg", () => new PlaneOp(), "datum plane offset плоскость");
        Op("op.mirror", "Зеркало", "Зеркало", "f_mirror_but.svg", () => new MirrorOp(), "mirror зеркало");
        Op("op.pattern_linear", "Линейный массив", "Массив", "f_pattern_linear_but.svg", () => new LinearPatternOp(), "pattern linear массив");
        Op("op.pattern_circular", "Круговой массив", "Кр. массив", "f_pattern_circular_but.svg", () => new CircularPatternOp(), "pattern circular массив круговой");

        // ------------------------------------------------------------ эскиз
        const string S = "Эскиз";
        void Tool(string id, string label, string shortLabel, string icon, Func<ISketchTool> make, string syn, KeyGesture? g = null) =>
            r.Add(new AppCommand { Id = id, Label = label, Short = shortLabel, Group = S, Icon = icon, Synonyms = syn, Gesture = g, CanRun = () => w.InSketch,
                IsChecked = () => vp.Tool != null && vp.Tool.GetType() == make().GetType() && vp.Tool.Title == make().Title,
                Run = () => w.SetTool(make()) });
        Tool("sk.line", "Отрезок", "Отрезок", "sk_line_but.svg", () => new LineTool(), "line отрезок", K(Key.L));
        Tool("sk.rect", "Прямоугольник", "Прямоуг.", "sk_rect_2p_but.svg", () => new RectangleTool(), "rectangle прямоугольник", K(Key.R));
        Tool("sk.circle", "Окружность", "Окружн.", "sk_circle_but.svg", () => new CircleTool(), "circle окружность", K(Key.C));
        Tool("sk.circle3", "Окружность по 3 точкам", "Окр. 3т", "sk_circle_3p_but.svg", () => new CircleTool(true), "circle 3 points");
        Tool("sk.arc3", "Дуга по 3 точкам", "Дуга", "sk_arc_3p_but.svg", () => new ArcTool(true), "arc дуга", K(Key.A));
        Tool("sk.arc_center", "Дуга от центра", "Дуга ц.", "sk_arc_center_but.svg", () => new ArcTool(false), "arc center");
        Tool("sk.ellipse", "Эллипс", "Эллипс", "sk_ellipse_but.svg", () => new EllipseTool(), "ellipse эллипс");
        Tool("sk.spline", "Сплайн", "Сплайн", "sk_spline_but.svg", () => new SplineTool(), "spline сплайн");
        Tool("sk.point", "Точка", "Точка", "sk_point_but.svg", () => new PointTool(), "point точка");
        Tool("sk.centerline", "Осевая линия", "Осевая", "sk_centerline_but.svg", () => new LineTool(false) { Role = SkRole.Centerline }, "centerline axis осевая");
        r.Add(new AppCommand { Short = "Вспом.", Id = "sk.construction", Label = "Вспомогательная геометрия", Group = S, Icon = "sk_construction_but.svg", Synonyms = "construction вспомогательная", Gesture = K(Key.G),
            CanRun = () => w.InSketch, IsChecked = () => w.ConstructionMode, Run = () => w.ToggleConstruction() });
        r.Add(new AppCommand { Short = "Сетка", Id = "sk.grid", Label = "Привязка к сетке", Group = S, Icon = "sk_snap_grid_but.svg", Synonyms = "grid snap сетка",
            CanRun = () => w.InSketch, IsChecked = () => vp.SnapGrid, Run = () => { vp.SnapGrid = !vp.SnapGrid; w.RefreshToolStates(); } });
        r.Add(new AppCommand { Short = "Ссылки", Id = "sk.references", Label = "Ссылки эскиза", Group = S, Icon = "sk_references_but.svg", Synonyms = "references ссылки проекция ребро",
            CanRun = () => w.InSketch, IsChecked = () => w.ReferencesPanelOpen, Run = () => w.ToggleReferencesPanel() });
        r.Add(new AppCommand { Short = "Готово", Id = "sk.finish", Label = "Закончить эскиз", Group = S, Icon = "sketch_finish_but.svg", Synonyms = "finish sketch готово выйти", Gesture = K(Key.Enter, KeyModifiers.Control),
            CanRun = () => w.InSketch, Run = () => w.FinishSketch() });
        r.Add(new AppCommand { Short = "Отмена", Id = "sk.cancel", Label = "Отменить эскиз", Group = S, Icon = "sketch_cancel_but.svg", Synonyms = "cancel sketch",
            CanRun = () => w.InSketch, Run = () => w.CancelSketch() });

        // ------------------------------------------------------------ приложение
        const string A = "Приложение";
        r.Add(new AppCommand { Short = "◐", Id = "app.theme", Label = "Тема: тёмная / светлая", Group = A, Icon = "theme_but.svg", Synonyms = "theme dark light тема",
            Run = () => w.ApplyTheme(!w.IsDark) });
        r.Add(new AppCommand { Short = "EN", Id = "app.language", Label = "Язык: русский / английский", Group = A, Icon = "language_but.svg", Synonyms = "language english russian язык",
            Run = () => w.ToggleLanguage() });
        r.Add(new AppCommand { Short = "Свойства", Id = "app.properties", Label = "Свойства", Group = A, Icon = "properties_but.svg", Synonyms = "properties info свойства масса объём",
            IsChecked = () => w.PropertiesVisible, Run = () => w.ToggleProperties() });
        r.Add(new AppCommand { Short = "Параметры", Id = "app.parameters", Label = "Параметры", Group = A, Icon = "parameters_but.svg", Synonyms = "parameters relations параметры переменные формулы",
            CanRun = () => w.Session != null, Run = async () => await w.OpenParameters() });
        r.Add(new AppCommand { Short = "Тест", Id = "app.selftest", Label = "Тест: замер скорости и отчёт", Group = A, Icon = "selftest_but.svg",
            Synonyms = "test self benchmark speed report тест замер скорость отчёт", Run = async () => await w.OpenSelfTest() });
        foreach (var (mode, label) in new[] { ("auto", "Графика: автоматически (Windows — ANGLE)"), ("angle", "Графика: ANGLE (Direct3D 11)"), ("wgl", "Графика: OpenGL (WGL)"), ("software", "Графика: программная") })
            r.Add(new AppCommand { Id = "app.gl_" + mode, Label = label, Group = A, Synonyms = "graphics opengl angle wgl direct3d графика",
                IsChecked = () => (UserPrefs.Current.GlMode ?? "auto") == mode,
                Run = () => { UserPrefs.Current.GlMode = mode; UserPrefs.Current.Save(); w.ShowToast(Loc.L("Графика сменится при следующем запуске приложения."), error: false); } });
        foreach (var (mode, label) in new[] { ("full", "Предпросмотр: полный (деталь считается при перетаскивании)"), ("contour", "Предпросмотр: контуром (деталь считается, когда отпустили)") })
            r.Add(new AppCommand { Id = "app.preview_" + mode, Label = label, Group = A, Synonyms = "preview handle live contour предпросмотр ручка контур",
                IsChecked = () => (UserPrefs.Current.PreviewMode ?? "full") == mode,
                Run = () => { UserPrefs.Current.PreviewMode = mode; UserPrefs.Current.Save(); } });
        r.Add(new AppCommand { Id = "palette", Label = "Палитра команд", Group = A, Icon = "command_palette_but.svg", Synonyms = "search command palette",
            Run = () => w.OpenPalette() });
        r.Add(new AppCommand { Short = "−", Id = "tree.collapse", Label = "Свернуть дерево", Group = A, Synonyms = "collapse", Run = () => w.SetTreeExpanded(false) });
        r.Add(new AppCommand { Short = "+", Id = "tree.expand", Label = "Развернуть дерево", Group = A, Synonyms = "expand", Run = () => w.SetTreeExpanded(true) });
    }

    /// <summary>Список «Опорные» (п. 12): ключ ShowOptions — подпись.</summary>
    public static readonly (string Key, string Label)[] ShowItems =
    {
        ("planes", "Плоскости"), ("axes", "Оси"), ("origin", "Начало координат"), ("points", "Точки"), ("lines", "Линии"),
        ("vertices", "Вершины"), ("centerlines", "Осевые линии"), ("sketches", "Эскизы"), ("dimensions", "Размеры"),
        ("annotations", "Обозначения"), ("measurements", "Измерения"),
    };

    public static bool GetShow(ShowOptions s, string key) => key switch
    {
        "planes" => s.Planes, "axes" => s.Axes, "origin" => s.Origin, "points" => s.Points, "lines" => s.Lines,
        "vertices" => s.Vertices, "centerlines" => s.Centerlines, "sketches" => s.Sketches, "dimensions" => s.Dimensions,
        "annotations" => s.Annotations, "measurements" => s.Measurements, _ => false,
    };

    public static void SetShow(ShowOptions s, string key, bool v)
    {
        switch (key)
        {
            case "planes": s.Planes = v; break;
            case "axes": s.Axes = v; break;
            case "origin": s.Origin = v; break;
            case "points": s.Points = v; break;
            case "lines": s.Lines = v; break;
            case "vertices": s.Vertices = v; break;
            case "centerlines": s.Centerlines = v; break;
            case "sketches": s.Sketches = v; break;
            case "dimensions": s.Dimensions = v; break;
            case "annotations": s.Annotations = v; break;
            case "measurements": s.Measurements = v; break;
        }
    }

    // ------------------------------------------------------------ удаление, подавление, видимость

    /// <summary>Что можно удалить из выбранного: операции, эскизы, опорные (кроме базовых плоскостей и начала).</summary>
    private static List<ModelItem> Deletable(MainWindow w)
    {
        var part = w.Session?.Part;
        if (part == null) return new();
        var basic = new HashSet<ModelItem> { part.Origin, part.PlaneXY, part.PlaneXZ, part.PlaneYZ };
        return w.SelectedItems().Where(i => i.Parent == part && !basic.Contains(i)).Distinct().ToList();
    }

    private static void Delete(MainWindow w)
    {
        var items = Deletable(w);
        var part = w.Session.Part!;
        string name = items.Count == 1 ? Loc.L("Удалить «{0}»", items[0].Name) : Loc.L("Удалить ({0})", items.Count);
        w.Session.Edit(name, () =>
        {
            foreach (var it in items)
            {
                if (it is Feature f) part.RemoveFeature(f);
                else part.Remove(it);
            }
            part.Invalidate();
        });
        w.ViewPort.Selection.Clear();
        w.ViewPort.MarkDirty();
    }

    private static void Suppress(MainWindow w)
    {
        var fs = w.SelectedItems().OfType<Feature>().ToList();
        bool to = !fs.All(f => f.Suppressed);
        w.Session.Edit(to ? Loc.L("Подавить") : Loc.L("Вернуть"), () => { foreach (var f in fs) f.Suppressed = to; });
        w.ViewPort.MarkDirty();
    }

    private static void SetVisible(MainWindow w, List<ModelItem> items, bool visible)
    {
        foreach (var i in items)
        {
            i.Visible = visible; i.VisibleUserSet = true;
            if (i is Part or Assembly) foreach (var d in i.Descendants()) { d.Visible = visible; d.VisibleUserSet = true; }
        }
        w.ViewPort.MarkDirty();
        w.RebuildTree();
    }

    private static void Isolate(MainWindow w)
    {
        var keep = new HashSet<ModelItem>(w.SelectedItems());
        var all = w.Session.Document.Root.Descendants().Where(i => i is not Feature && i is not Assembly).ToList();
        foreach (var i in all)
        {
            bool k = keep.Contains(i) || keep.Any(x => IsAncestor(x, i)) || keep.Any(x => IsAncestor(i, x));
            i.Visible = k; i.VisibleUserSet = true;
        }
        w.ViewPort.MarkDirty();
        w.RebuildTree();
    }

    private static bool IsAncestor(ModelItem a, ModelItem b)
    {
        for (var p = b.Parent; p != null; p = p.Parent) if (ReferenceEquals(p, a)) return true;
        return false;
    }
}
