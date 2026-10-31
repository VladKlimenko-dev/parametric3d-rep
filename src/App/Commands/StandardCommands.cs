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
        r.Add(new AppCommand { Id = "file.export", Label = "Экспорт (STEP, IGES, STL, OBJ)…", Group = F, Icon = "export_but.svg", Synonyms = "export step iges stl obj",
            CanRun = () => w.Session?.Part?.Bodies.Count > 0, Run = async () => await w.ExportAsync() });
        r.Add(new AppCommand { Id = "app.exit", Label = "Выход", Group = F, Synonyms = "exit quit закрыть", Run = () => w.Close() });

        // ------------------------------------------------------------ правка
        const string E = "Правка";
        r.Add(new AppCommand { Short = "↶", Id = "edit.undo", Label = "Отменить", Group = E, Icon = "undo_but.svg", Gesture = K(Key.Z, KeyModifiers.Control), Synonyms = "undo",
            CanRun = () => w.InSketch ? w.CanSketchUndo : w.Session?.CanUndo == true && w.CurrentOperation == null, Run = () => { if (w.InSketch) w.SketchUndo(); else w.Session.Undo(); } });
        r.Add(new AppCommand { Short = "↷", Id = "edit.redo", Label = "Вернуть", Group = E, Icon = "redo_but.svg", Gesture = K(Key.Y, KeyModifiers.Control), Synonyms = "redo повторить",
            CanRun = () => w.InSketch ? w.CanSketchRedo : w.Session?.CanRedo == true && w.CurrentOperation == null, Run = () => { if (w.InSketch) w.SketchRedo(); else w.Session.Redo(); } });
        r.Add(new AppCommand { Short = "▾", Id = "edit.history", Label = "Журнал отмены", Group = E, Icon = "undo_history_but.svg", Synonyms = "history undo журнал история",
            Run = () => w.OpenHistory() });
        r.Add(new AppCommand { Short = "Удалить", Id = "edit.delete", Label = "Удалить", Group = E, Icon = "delete_but.svg", Gesture = K(Key.Delete), Synonyms = "delete remove",
            CanRun = () => Modeling() && Deletable(w).Count > 0, Run = () => Delete(w), WhyDisabled = () => WhyNotDeletable(w) });
        r.Add(new AppCommand { Short = "Изменить", Id = "edit.feature", Label = "Изменить операцию", Group = E, Icon = "tree_edit_def_but.svg", Synonyms = "edit definition редактировать",
            CanRun = () => Modeling() && w.SelectedItems().Any(i => i is Feature or Sketch || MainWindow.EditablePlane(i)), Run = () => w.EditSelected() });
        r.Add(new AppCommand { Short = "Подавить", Id = "edit.suppress", Label = "Подавить / вернуть", Group = E, Icon = "tree_suppress_but.svg", Synonyms = "suppress resume",
            CanRun = () => Modeling() && w.SelectedItems().Any(i => i is Feature), Run = () => Suppress(w) });
        r.Add(new AppCommand { Short = "×", Id = "edit.clear_selection", Label = "Снять выбор", Group = E, Icon = "sel_clear_but.svg", Synonyms = "deselect clear",
            Run = () => vp.Selection.Clear() });

        // ------------------------------------------------------------ вид
        const string V = "Вид";
        // u13: стандартные виды — ViewSelectCommands (с иконками и клавишами)
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
        // 13:09: КНОПКА-ПЕРЕКЛЮЧАТЕЛЬ. Выбранное скрыто — повторный щелчок возвращает видимость (выбор остаётся после
        // скрытия, поэтому вторым щелчком скрытое показывается обратно); значок — «скрыть» или «показать»
        static bool AllHidden(List<ModelItem> s) => s.Count > 0 && s.All(i => !i.Visible);
        r.Add(new AppCommand { Short = "Скрыть / показать", Id = "view.hide", Label = "Скрыть / показать выбранное", Group = V, Synonyms = "hide show скрыть показать",
            IconOf = () => AllHidden(w.SelectedItems()) ? "eye_hidden_ico.svg" : "hide_but.svg",
            IsChecked = () => AllHidden(w.SelectedItems()),
            CanRun = () => w.SelectedItems().Count > 0, Run = () => { var s = w.SelectedItems(); SetVisible(w, s, AllHidden(s)); w.RefreshToolStates(); w.UpdateContextBarPublic(); } });
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
            ["op.extrude_thin"] = () => Fits(x => SketchLike(x) || PlaneLike(x)),
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
            // u13
            ["op.round_variable"] = () => Fits(RealEdge),
            ["op.sweep"] = () => Fits(SketchLike),
            ["op.loft"] = () => Fits(SketchLike),
            ["op.draft"] = () => Fits(x => x.Kind == PickKind.Face || PlaneLike(x)),
            ["op.rib"] = () => Fits(SketchLike),
            ["op.axis"] = () => Fits(x => !IsFeat(x)),
            ["op.point"] = () => Fits(x => !IsFeat(x)),
            // u13c
            ["op.helical_sweep"] = () => Fits(x => SketchLike(x) || x.Target is DatumAxis),
            ["op.move_copy"] = () => Fits(x => Refs.IsLinear(x)),
            ["op.face_offset"] = () => Fits(x => x.Kind == PickKind.Face),
            ["op.face_move"] = () => Fits(x => x.Kind == PickKind.Face),
            ["op.face_radius"] = () => Fits(x => x.Kind == PickKind.Face),
            ["op.face_delete"] = () => Fits(x => x.Kind == PickKind.Face),
            ["op.pattern_points"] = () => Fits(x => IsFeat(x) || SketchLike(x)),
            ["op.pattern_curve"] = () => Fits(x => IsFeat(x) || SketchLike(x)),
            ["op.pattern_fill"] = () => Fits(x => IsFeat(x) || SketchLike(x)),
            ["op.thread"] = () => Fits(x => x.Kind == PickKind.Face),
            // u13f
            ["op.surface_extrude"] = () => Fits(SketchLike), ["op.surface_revolve"] = () => Fits(SketchLike), ["op.fill"] = () => Fits(SketchLike),
            ["op.surf_copy"] = () => Fits(x => x.Kind == PickKind.Face),
            ["op.boundary_blend"] = () => Fits(SketchLike), ["op.swept_blend"] = () => Fits(SketchLike), ["op.rotational_blend"] = () => Fits(SketchLike),
            // u13e
            ["op.full_round"] = () => Fits(x => x.Kind == PickKind.Face),
            ["op.hole_coaxial"] = () => Fits(x => x.Kind is PickKind.Face or PickKind.Edge or PickKind.DatumAxis or PickKind.DatumPlane),
            ["op.hole_radial"] = () => Fits(x => x.Kind is PickKind.Face or PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint),
        };
        void Op(string id, string label, string shortLabel, string icon, Func<Operation> make, string syn, KeyGesture? g = null) =>
            r.Add(new AppCommand { Id = id, Label = label, Short = shortLabel, Group = M, Icon = icon, Synonyms = syn, Gesture = g,
                CanRun = () => Modeling() && (w.CurrentOperation != null || !fits.TryGetValue(id, out var f) || f()), Run = () => w.StartOperation(make()) });
        Op("op.sketch", "Эскиз", "Эскиз", "f_sketch_but.svg", () => new SketchOp(), "sketch эскиз", K(Key.S));
        Op("op.extrude", "Выдавливание", "Выдавить", "f_extrude_but.svg", () => w.SketchFirst(new ExtrudeOp()), "extrude выдавить", K(Key.E));
        Op("op.extrude_thin", "Тонкое выдавливание", "Тонкое", "f_extrude_thin_but.svg", () => w.SketchFirst(new ExtrudeOp { StartThin = true }), "thin extrude тонкое выдавливание стенка");
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
        // u13: кнопки таблицы иконок — формообразование, конструктивные, опорные
        Op("op.round_variable", "Переменное скругление", "Перем. скругл.", "f_round_variable_but.svg", () => new RoundOp { Variable = true }, "variable round fillet переменное скругление");
        Op("op.sweep", "Протяжка", "Протяжка", "f_sweep_but.svg", () => new SweepOp(), "sweep протяжка траектория");
        Op("op.loft", "Переход по сечениям", "Переход", "f_blend_but.svg", () => new LoftOp(), "loft blend переход сечения");
        Op("op.draft", "Уклон", "Уклон", "f_draft_but.svg", () => new DraftOp(), "draft уклон литейный");
        Op("op.rib", "Ребро жёсткости", "Ребро", "f_rib_but.svg", () => new RibOp(), "rib ребро жёсткости");
        Op("op.axis", "Опорная ось", "Ось", "dat_axis_but.svg", () => new AxisOp(), "datum axis ось");
        Op("op.point", "Опорная точка", "Точка", "dat_point_but.svg", () => new PointOp(), "datum point точка");
        Op("op.plane_offset", "Плоскость со смещением", "Пл. смещение", "dat_plane_offset_but.svg", () => new PlaneOp { InitialMethod = 0 }, "plane offset плоскость смещение");
        Op("op.plane_3pt", "Плоскость через три точки", "Пл. 3 точки", "dat_plane_3pt_but.svg", () => new PlaneOp { InitialMethod = 1 }, "plane three points плоскость три точки");
        Op("op.plane_axis", "Плоскость через ось под углом", "Пл. под углом", "dat_plane_axis_but.svg", () => new PlaneOp { InitialMethod = 4 }, "plane angle axis плоскость угол ось");
        Op("op.plane_normal_curve", "Плоскость нормально к кривой", "Пл. ⟂ кривой", "dat_plane_normal_curve_but.svg", () => new PlaneOp { InitialMethod = 5 }, "plane normal curve плоскость нормаль кривая");
        Op("op.plane_mid", "Плоскость посередине", "Пл. посередине", "dat_plane_mid_but.svg", () => new PlaneOp { InitialMethod = 6 }, "mid plane midplane плоскость посередине");
        Op("op.plane_tangent", "Плоскость касательная", "Пл. касательная", "dat_plane_tangent_but.svg", () => new PlaneOp { InitialMethod = 7 }, "tangent plane плоскость касательная");
        Op("op.axis_face", "Ось грани вращения", "Ось грани", "dat_axis_face_but.svg", () => new AxisOp { InitialMethod = 3 }, "axis face cylinder ось грани цилиндр");
        // u13c
        Op("op.helical_sweep", "Винтовая протяжка", "Винтовая", "f_helical_sweep_but.svg", () => new HelicalSweepOp(), "helical sweep helix spring thread coil винтовая пружина");
        Op("op.move_copy", "Перенос и поворот", "Перенос", "f_move_copy_but.svg", () => new MoveCopyOp(), "move copy rotate translate перенос поворот копия");
        Op("op.face_offset", "Смещение граней", "Смещ. граней", "dir_offset_face_but.svg", () => new FaceEditOp(), "offset faces push pull смещение граней");
        Op("op.face_move", "Перенос грани", "Перенос грани", "dir_move_face_but.svg", () => new FaceEditOp { MoveFace = true }, "move face push pull перенос грани");
        Op("op.face_radius", "Новый радиус", "Радиус", "dir_new_radius_but.svg", () => new FaceEditOp { StartMode = FaceEditMode.Radius }, "new radius diameter радиус диаметр изменить");
        Op("op.face_delete", "Удаление граней", "Удалить грани", "dir_delete_face_but.svg", () => new FaceEditOp { StartMode = FaceEditMode.Delete }, "delete face remove fillet удалить грань скругление");
        Op("op.pattern_points", "Массив по точкам", "По точкам", "f_pattern_points_but.svg", () => new PointsPatternOp(), "pattern points sketch массив точки");
        Op("op.pattern_curve", "Массив по кривой", "По кривой", "f_pattern_curve_but.svg", () => new PointsPatternOp { StartMode = PointsPatternMode.Curve }, "pattern curve path массив кривая");
        Op("op.pattern_fill", "Массив заполнением", "Заполнение", "f_pattern_fill_but.svg", () => new PointsPatternOp { StartMode = PointsPatternMode.Fill }, "pattern fill grid массив заполнение сетка");
        // u13f: поверхности и переходы
        Op("op.surface_extrude", "Поверхность выдавливанием", "Пов. выдавл.", "f_surface_extrude_but.svg", () => new SurfaceOp(), "surface extrude поверхность выдавливание квилт");
        Op("op.surface_revolve", "Поверхность вращением", "Пов. вращ.", "surf_revolve_but.svg", () => new SurfaceOp { StartMode = SurfaceKind.Revolve }, "surface revolve поверхность вращение квилт");
        Op("op.fill", "Заливка (плоская поверхность)", "Заливка", "f_fill_but.svg", () => new SurfaceOp { StartMode = SurfaceKind.Fill }, "fill flat surface заливка плоская поверхность");
        Op("op.surf_copy", "Копия поверхности", "Копия пов.", "surf_copy_but.svg", () => new SurfaceOp { StartMode = SurfaceKind.Copy }, "copy surface копия поверхности граней");
        Op("op.surf_offset", "Смещение поверхности", "Смещ. пов.", "surf_offset_but.svg", () => new QuiltEditOp(), "offset surface смещение поверхности");
        Op("op.surf_merge", "Объединение квилтов", "Объединить", "surf_merge_but.svg", () => new QuiltEditOp { StartMode = QuiltEditKind.Merge }, "merge quilts объединение квилтов");
        Op("op.surf_trim", "Обрезка квилта", "Обрезать", "surf_trim_but.svg", () => new QuiltEditOp { StartMode = QuiltEditKind.Trim }, "trim surface обрезка квилта");
        Op("op.surf_extend", "Продление квилта", "Продлить", "surf_extend_but.svg", () => new QuiltEditOp { StartMode = QuiltEditKind.Extend }, "extend surface продление квилта");
        Op("op.surf_solidify", "Отверждение квилта", "Отвердить", "surf_solidify_but.svg", () => new QuiltSolidOp { StartMode = QuiltSolidKind.Solidify }, "solidify отверждение квилта");
        Op("op.thicken", "Утолщение квилта", "Утолщить", "f_thicken_but.svg", () => new QuiltSolidOp(), "thicken утолщение квилта");
        Op("op.replace_face", "Замена грани квилтом", "Заменить грань", "dir_replace_face_but.svg", () => new QuiltSolidOp { StartMode = QuiltSolidKind.ReplaceFace }, "replace face замена грани");
        Op("op.boundary_blend", "Граничный переход", "Гран. переход", "f_boundary_blend_but.svg", () => new BlendOp(), "boundary blend граничный переход");
        Op("op.swept_blend", "Протяжка с переходом", "Протяжка-переход", "f_swept_blend_but.svg", () => new BlendOp { StartMode = BlendKind.Swept }, "swept blend протяжка переход");
        Op("op.rotational_blend", "Переход вращением", "Переход вращ.", "f_rotational_blend_but.svg", () => new BlendOp { StartMode = BlendKind.Rotational }, "rotational blend переход вращением");
        r.Add(new AppCommand { Id = "op.insert_body", Label = "Вставить FreeForm / тело из файла", Short = "Вставить тело", Group = M, Icon = "f_insert_freeform_but.svg", Synonyms = "insert freeform body step вставить тело",
            CanRun = Modeling, Run = async () => await DocCommands.InsertBody(w) });
        // u13e
        Op("op.full_round", "Полное скругление", "Полн. скругл.", "f_round_full_but.svg", () => new FullRoundOp(), "full round полное скругление");
        Op("op.hole_coaxial", "Отверстие соосное", "Соосное", "f_hole_coaxial_but.svg", () => new AxialHoleOp(), "coaxial hole соосное отверстие");
        Op("op.hole_radial", "Отверстие радиальное", "Радиальное", "f_hole_radial_but.svg", () => new AxialHoleOp { Radial = true }, "radial hole радиальное отверстие");
        Op("op.csys", "Система координат", "СК", "dat_csys_but.svg", () => new CsysOp(), "csys coordinate system система координат");
        Op("op.curve_points", "Кривая через точки", "Кривая т.", "dat_curve_points_but.svg", () => new CurveOp(), "curve through points кривая через точки");
        Op("op.curve_equation", "Кривая по уравнению", "Кривая ур.", "dat_curve_equation_but.svg", () => new CurveOp { Mode = 1 }, "curve equation кривая уравнение формула");
        Op("op.curve_project", "Проекция кривой на грань", "Проекция", "dat_curve_project_but.svg", () => new CurveOp { Mode = 2 }, "project curve проекция кривой");
        Op("op.thread", "Косметическая резьба", "Резьба", "f_thread_but.svg", () => new CosmeticThreadOp(), "cosmetic thread резьба");
        Op("op.axis_intersect", "Ось пересечения плоскостей", "Ось пересеч.", "dat_axis_intersect_but.svg", () => new AxisOp { InitialMethod = 2 }, "axis intersection planes ось пересечение");
        Op("op.point_center", "Точка в центре дуги", "Центр дуги", "dat_point_center_but.svg", () => new PointOp { InitialMethod = 1 }, "point centre arc точка центр дуги");
        Op("op.point_intersect", "Точка пересечения", "Точка пересеч.", "dat_point_intersect_but.svg", () => new PointOp { InitialMethod = 3 }, "point intersection точка пересечение");

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
        Tool("sk.ellipse", "Эллипс", "Эллипс", "sk_ellipse_3p_but.svg", () => new EllipseTool(), "ellipse эллипс");
        Tool("sk.spline", "Сплайн", "Сплайн", "sk_spline_but.svg", () => new SplineTool(), "spline сплайн");
        Tool("sk.point", "Точка", "Точка", "sk_point_but.svg", () => new PointTool(), "point точка");
        Tool("sk.centerline", "Осевая линия", "Осевая", "sk_centerline_but.svg", () => new LineTool(false) { Role = SkRole.Centerline }, "centerline axis осевая");
        r.Add(new AppCommand { Short = "Вспом.", Id = "sk.construction", Label = "Вспомогательная геометрия", Group = S, Icon = "sk_construction_but.svg", Synonyms = "construction вспомогательная", Gesture = K(Key.G),
            CanRun = () => w.InSketch, IsChecked = () => w.ConstructionMode, Run = () => w.ToggleConstruction() });
        r.Add(new AppCommand { Short = "Сетка", Id = "sk.grid", Label = "Привязка к сетке", Group = S, Icon = "sk_snap_grid_but.svg", Synonyms = "grid snap сетка",
            CanRun = () => w.InSketch, IsChecked = () => vp.SnapGrid, Run = () => { vp.SnapGrid = !vp.SnapGrid; w.RefreshToolStates(); } });
        r.Add(new AppCommand { Short = "Ссылки", Id = "sk.references", Label = "Ссылки эскиза", Group = S, Icon = "sk_references_but.svg", Synonyms = "references ссылки проекция ребро",
            CanRun = () => w.InSketch, IsChecked = () => w.ReferencesPanelOpen, Run = () => w.ToggleReferencesPanel() });
        // u13: инструменты рисования и правки эскиза (таблица иконок)
        Tool("sk.rect_center", "Прямоугольник от центра", "Прям. ц.", "sk_rect_center_but.svg", () => new RectCenterTool(), "rectangle center прямоугольник центр");
        Tool("sk.rect_3p", "Прямоугольник по трём точкам", "Прям. 3т", "sk_rect_3p_but.svg", () => new Rect3PTool(), "rectangle 3 points наклонный прямоугольник");
        Tool("sk.circle_2p", "Окружность по двум точкам", "Окр. 2т", "sk_circle_2p_but.svg", () => new Circle2PTool(), "circle 2 points diameter окружность диаметр");
        Tool("sk.arc_tangent", "Дуга касательная", "Дуга кас.", "sk_arc_tangent_but.svg", () => new ArcTangentTool(), "tangent arc касательная дуга");
        Tool("sk.ellipse_3p", "Эллипс (центр и оси)", "Эллипс 3т", "sk_ellipse_3p_but.svg", () => new EllipseTool(), "ellipse эллипс оси");
        Tool("sk.ellipse_5p", "Эллипс по пяти точкам", "Эллипс 5т", "sk_ellipse_5p_but.svg", () => new Ellipse5PTool(), "ellipse 5 points эллипс пять точек");
        Tool("sk.spline_through", "Сплайн через точки", "Сплайн т.", "sk_spline_but.svg", () => new SplineThroughTool(), "spline through points сплайн через точки");
        Tool("sk.spline_cv", "Сплайн по контрольным точкам", "Сплайн КТ", "sk_spline_cv_but.svg", () => new SplineTool(), "spline control points сплайн контрольные");
        Tool("sk.slot", "Паз", "Паз", "sk_slot_but.svg", () => new SlotTool(), "slot паз");
        Tool("sk.csys", "Система координат эскиза", "СК эскиза", "sk_csys_but.svg", () => new SketchCsysTool(), "sketch coordinate system origin начало система координат");
        Tool("sk.trim", "Обрезка", "Обрезка", "sk_trim_but.svg", () => new TrimTool(), "trim обрезка", K(Key.T));
        Tool("sk.split", "Разделить", "Разделить", "sk_split_but.svg", () => new TrimTool(split: true), "split разделить");
        Tool("sk.line_tangent2", "Отрезок касательный к двум дугам", "Кас. 2 дуг", "sk_line_tangent2_but.svg", () => new LineTangent2Tool(), "tangent line two arcs касательная");
        void SkCmd(string id, string label, string icon, Action run, string syn) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = S, Icon = icon, Synonyms = syn, CanRun = () => w.InSketch, Run = run });
        SkCmd("sk.polygon", "Многоугольник", "sk_polygon_but.svg", () => SketchEditCommands.Polygon(w), "polygon многоугольник");
        SkCmd("sk.text", "Текст", "sk_text_but.svg", () => SketchEditCommands.Text(w), "text текст надпись");
        SkCmd("sk.fillet", "Скругление угла", "sk_fillet_but.svg", () => SketchEditCommands.Corner(w, false), "sketch fillet скругление угла");
        SkCmd("sk.chamfer", "Фаска угла", "sk_chamfer_but.svg", () => SketchEditCommands.Corner(w, true), "sketch chamfer фаска угла");
        SkCmd("sk.mirror", "Зеркало относительно осевой", "sk_mirror_but.svg", () => SketchEditCommands.Mirror(w), "sketch mirror зеркало осевая");
        SkCmd("sk.transform", "Перенести, повернуть, масштаб", "sk_transform_but.svg", () => SketchEditCommands.Transform(w), "move rotate scale перенести повернуть масштаб");
        SkCmd("sk.offset", "Смещение цепочки", "sk_offset_but.svg", () => SketchEditCommands.Offset(w), "offset смещение цепочки");
        SkCmd("sk.use_edge", "Использовать ребро модели", "sk_use_edge_but.svg", () => SketchEditCommands.StartEdgeMode(w, SketchEditCommands.EdgeMode.Use), "use edge project проекция ребро");
        SkCmd("sk.offset_edge", "Смещение ребра", "sk_offset_edge_but.svg", () => SketchEditCommands.StartEdgeMode(w, SketchEditCommands.EdgeMode.Offset), "offset edge смещение ребра");
        SkCmd("sk.thicken_edge", "Утолщение ребра", "sk_thicken_edge_but.svg", () => SketchEditCommands.StartEdgeMode(w, SketchEditCommands.EdgeMode.Thicken), "thicken edge утолщение ребра");
        r.Add(new AppCommand { Id = "sk.setup", Label = "Настройка эскиза", Group = S, Icon = "sk_setup_but.svg", Synonyms = "sketch setup настройка плоскость",
            CanRun = () => w.InSketch || w.SelectedItems().OfType<Parametric3D.Model.Sketching.Sketch>().Any(), Run = () => SketchEditCommands.Setup(w) });
        SkCmd("sk.show_conflicts", "Показать конфликтующие связи", "sk_show_conflicts_but.svg", () => SketchEditCommands.ShowConflicts(w), "conflicts конфликты связи");
        // k96 (п. 16): «Разрешить эскиз» (как Resolve Sketch в Creo) — панель с участниками противоречия и действиями
        r.Add(new AppCommand { Id = "sk.resolve", Label = "Разрешить эскиз", Short = "Разрешить", Group = S, Icon = "sk_resolve_but.svg", Synonyms = "resolve sketch conflicts разрешить эскиз конфликты противоречия",
            CanRun = () => w.InSketch, IsChecked = () => w.ResolvePanelOpen, Run = () => w.ToggleResolvePanel() });
        r.Add(new AppCommand { Id = "sk.snap_items", Label = "Привязка к элементам", Group = S, Icon = "sk_snap_items_but.svg", Synonyms = "snap items привязка элементы", CanRun = () => w.InSketch, IsChecked = () => vp.SnapItems, Run = () => { vp.SnapItems = !vp.SnapItems; w.RefreshToolStates(); } });
        r.Add(new AppCommand { Id = "sk.show_open_ends", Label = "Показ открытых концов", Group = S, Icon = "sk_show_open_ends_but.svg", Synonyms = "open ends открытые концы", CanRun = () => w.InSketch, IsChecked = () => vp.ShowOpenEnds, Run = () => { vp.ShowOpenEnds = !vp.ShowOpenEnds; vp.MarkDirty(); w.RefreshToolStates(); } });
        r.Add(new AppCommand { Id = "sk.show_filled", Label = "Заливка замкнутых областей", Group = S, Icon = "sk_show_filled_shapes_but.svg", Synonyms = "fill shapes заливка области", CanRun = () => w.InSketch, IsChecked = () => vp.ShowFilled, Run = () => { vp.ShowFilled = !vp.ShowFilled; vp.MarkDirty(); w.RefreshToolStates(); } });

        // u13: связи и размеры эскиза кнопками (таблица иконок)
        void Con(string id, string label, string icon, Parametric3D.Model.Sketching.ConstraintType t, string syn, KeyGesture? g = null, bool collinear = false) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = S, Icon = icon, Synonyms = "constraint связь " + syn, Gesture = g, CanRun = () => w.InSketch, Run = () => SketchCommands.Constrain(w, t, collinear) });
        Con("con.coincident", "Совпадение", "con_coincident_but.svg", Parametric3D.Model.Sketching.ConstraintType.Coincident, "coincident совпадение");
        Con("con.point_on", "Точка на кривой", "con_point_on_but.svg", Parametric3D.Model.Sketching.ConstraintType.PointOnEntity, "point on curve точка на кривой");
        Con("con.midpoint", "Середина", "con_midpoint_but.svg", Parametric3D.Model.Sketching.ConstraintType.MidPoint, "midpoint середина");
        Con("con.horizontal", "Горизонтально", "con_horizontal_but.svg", Parametric3D.Model.Sketching.ConstraintType.Horizontal, "horizontal горизонтально", K(Key.H, KeyModifiers.Shift));
        Con("con.vertical", "Вертикально", "con_vertical_but.svg", Parametric3D.Model.Sketching.ConstraintType.Vertical, "vertical вертикально", K(Key.V, KeyModifiers.Shift));
        Con("con.parallel", "Параллельно", "con_parallel_but.svg", Parametric3D.Model.Sketching.ConstraintType.Parallel, "parallel параллельно");
        Con("con.perpendicular", "Перпендикулярно", "con_perpendicular_but.svg", Parametric3D.Model.Sketching.ConstraintType.Perpendicular, "perpendicular перпендикулярно");
        Con("con.collinear", "Коллинеарно", "con_collinear_but.svg", Parametric3D.Model.Sketching.ConstraintType.Parallel, "collinear коллинеарно на одной прямой", collinear: true);
        Con("con.tangent", "Касание", "con_tangent_but.svg", Parametric3D.Model.Sketching.ConstraintType.Tangent, "tangent касание", K(Key.T, KeyModifiers.Shift));
        Con("con.equal", "Равенство", "con_equal_but.svg", Parametric3D.Model.Sketching.ConstraintType.Equal, "equal равенство равные", K(Key.E, KeyModifiers.Shift));
        Con("con.concentric", "Концентрично", "con_concentric_but.svg", Parametric3D.Model.Sketching.ConstraintType.Concentric, "concentric концентрично соосно");
        Con("con.symmetric", "Симметрия", "con_symmetric_but.svg", Parametric3D.Model.Sketching.ConstraintType.Symmetric, "symmetric симметрия");
        Con("con.fix", "Зафиксировать", "con_fix_but.svg", Parametric3D.Model.Sketching.ConstraintType.Fix, "fix lock зафиксировать");
        void Dim(string id, string label, string icon, SketchCommands.DimKind k, string syn, KeyGesture? g = null) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = S, Icon = icon, Synonyms = "dimension размер " + syn, Gesture = g, CanRun = () => w.InSketch, Run = () => SketchCommands.Dimension(w, k) });
        Dim("dim.smart", "Размер", "dim_smart_but.svg", SketchCommands.DimKind.Smart, "smart dimension размер", K(Key.D));
        Dim("dim.linear", "Линейный размер", "dim_linear_but.svg", SketchCommands.DimKind.Linear, "linear distance length линейный расстояние длина");
        Dim("dim.horizontal", "Размер по горизонтали", "dim_size_horizontal.svg", SketchCommands.DimKind.Horizontal, "horizontal distance размер по горизонтали горизонтальный");
        Dim("dim.vertical", "Размер по вертикали", "dim_size_vertical.svg", SketchCommands.DimKind.Vertical, "vertical distance размер по вертикали вертикальный");
        Dim("dim.radius", "Радиус", "dim_radius_but.svg", SketchCommands.DimKind.Radius, "radius радиус");
        Dim("dim.diameter", "Диаметр", "dim_diameter_but.svg", SketchCommands.DimKind.Diameter, "diameter диаметр");
        Dim("dim.angle", "Угловой размер", "dim_angle_but.svg", SketchCommands.DimKind.Angle, "angle угол угловой");
        Dim("dim.arc_length", "Длина дуги", "dim_curve_length_but.svg", SketchCommands.DimKind.ArcLength, "arc length длина дуги");
        r.Add(new AppCommand { Id = "dim.reference", Label = "Справочный / управляющий", Group = S, Icon = "dim_reference_but.svg", Synonyms = "reference driven справочный управляющий", CanRun = () => w.InSketch, Run = () => SketchCommands.ToggleReference(w) });
        r.Add(new AppCommand { Id = "dim.link_param", Label = "Связать с параметром", Group = S, Icon = "dim_link_param_but.svg", Synonyms = "link parameter formula параметр формула", CanRun = () => w.InSketch, Run = () => SketchCommands.LinkParam(w) });
        r.Add(new AppCommand { Id = "dim.arrows_outside", Label = "Стрелки снаружи", Group = S, Icon = "dim_arrows_outside_but.svg", Synonyms = "arrows outside стрелки снаружи", CanRun = () => w.InSketch,
            Run = () => SketchCommands.OnDimensions(w, d => d.Arrows = d.Arrows == Parametric3D.Model.Sketching.ArrowPlacement.Outside ? Parametric3D.Model.Sketching.ArrowPlacement.Auto : Parametric3D.Model.Sketching.ArrowPlacement.Outside) });
        r.Add(new AppCommand { Id = "dim.lock", Label = "Заблокировать размер", Group = S, Icon = "dim_lock_ico.svg", Synonyms = "lock dimension замок заблокировать", CanRun = () => w.InSketch,
            Run = () => SketchCommands.OnDimensions(w, d => { if (d.Reference) return; d.UserLocked = !d.UserLocked; if (d.UserLocked) { d.IsDefault = false; d.Locked = true; } }) });
        r.Add(new AppCommand { Id = "dim.area", Label = "Площадь области", Short = "Площадь", Group = S, Icon = "dim_area_but.svg", Synonyms = "area площадь область", CanRun = () => w.InSketch,
            Run = () => SketchCommands.ShowArea(w) });
        SketchSuggest.Register(r, w);
        r.Add(new AppCommand { Short = "Готово", Id = "sk.finish", Label = "Закончить эскиз", Group = S, Icon = "sketch_finish_but.svg", Synonyms = "finish sketch готово выйти", Gesture = K(Key.Enter, KeyModifiers.Control),
            CanRun = () => w.InSketch, Run = () => w.FinishSketch() });
        r.Add(new AppCommand { Short = "Отмена", Id = "sk.cancel", Label = "Отменить эскиз", Group = S, Icon = "sketch_cancel_but.svg", Synonyms = "cancel sketch",
            CanRun = () => w.InSketch, Run = () => w.CancelSketch() });

        // ------------------------------------------------------------ приложение
        const string A = "Приложение";
        r.Add(new AppCommand { Short = "◐", Id = "app.theme", Label = "Тема: тёмная / светлая", Group = A, Icon = "theme_but.svg", Synonyms = "theme dark light тема",
            Run = () => w.ApplyTheme(!w.IsDark) });
        r.Add(new AppCommand { Short = "EN", Id = "app.language", Label = "Язык: русский / английский", Group = A, Icon = "language_but.svg", IconOf = () => Loc.En ? "language_en_but.svg" : "language_ru_but.svg", Synonyms = "language english russian язык",
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
        // u13: команды дерева операций (таблица иконок)
        bool HasPartT() => w.Session.Part != null && !w.InSketch;
        bool OneFeature() => HasPartT() && w.SelectedItems().OfType<Parametric3D.Model.Features.Feature>().Count() == 1;
        r.Add(new AppCommand { Id = "tree.resume", Label = "Вернуть подавленную", Group = E, Icon = "tree_resume_but.svg", Synonyms = "resume unsuppress вернуть подавленную", CanRun = HasPartT, Run = () => TreeCommands.Resume(w) });
        r.Add(new AppCommand { Id = "tree.insert_here", Label = "Вставить здесь (строка отката)", Group = E, Icon = "tree_insert_here_but.svg", Synonyms = "insert here rollback строка отката вставить", CanRun = HasPartT, Run = () => TreeCommands.InsertHere(w) });
        r.Add(new AppCommand { Id = "tree.reorder_up", Label = "Переупорядочить: выше", Group = E, Icon = "tree_reorder_but.svg", Synonyms = "reorder up move переставить выше", CanRun = OneFeature, Run = () => TreeCommands.Reorder(w, -1) });
        r.Add(new AppCommand { Id = "tree.reorder_down", Label = "Переупорядочить: ниже", Group = E, Icon = "tree_reorder_but.svg", Synonyms = "reorder down move переставить ниже", CanRun = OneFeature, Run = () => TreeCommands.Reorder(w, +1) });
        r.Add(new AppCommand { Id = "tree.rebuild", Label = "Перестроить", Group = E, Icon = "tree_rebuild_but.svg", Synonyms = "rebuild regenerate перестроить", Gesture = K(Key.B, KeyModifiers.Control), CanRun = HasPartT, Run = () => TreeCommands.Rebuild(w) });
        r.Add(new AppCommand { Id = "tree.edit_dims", Label = "Править размеры", Group = E, Icon = "tree_edit_dims_but.svg", Synonyms = "edit dimensions размеры числа", CanRun = OneFeature, Run = () => TreeCommands.EditDims(w) });
        r.Add(new AppCommand { Id = "tree.needs_attention", Label = "Требуют внимания", Group = E, Icon = "tree_needs_attention_but.svg", Synonyms = "errors attention ошибки внимание", CanRun = HasPartT, Run = () => TreeCommands.ShowNeedsAttention(w) });
        r.Add(new AppCommand { Id = "tree.group", Label = "Группа (папка операций)", Group = E, Icon = "tree_group_but.svg", Synonyms = "group folder группа папка", CanRun = () => HasPartT() && w.SelectedItems().OfType<Parametric3D.Model.Features.Feature>().Count() >= 2, Run = () => TreeCommands.Group(w) });   // k120 (п. 12): от двух операций
        r.Add(new AppCommand { Id = "tree.parents_children", Label = "Родители и потомки", Group = E, Icon = "tree_parents_children_but.svg", Synonyms = "parents children dependencies родители потомки зависимости", CanRun = OneFeature, Run = () => TreeCommands.ShowParentsChildren(w) });
        r.Add(new AppCommand { Id = "tree.replace_refs", Label = "Заменить ссылки", Group = E, Icon = "tree_replace_refs_but.svg", Synonyms = "replace references redefine заменить ссылки", CanRun = () => HasPartT() && w.SelectedItems().Count == 1, Run = () => TreeCommands.ReplaceRefs(w) });
        ViewSelectCommands.Register(r, w);
        DocCommands.Register(r, w);
        ContextCommands.Register(r, w);
        ContextCommands.RegisterEnv(r, w);
        AssemblyCommands.Register(r, w);
        FreeFormCommands.Register(r, w);
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

    /// <summary>
    /// k104: ПОЧЕМУ ВЫБРАННОЕ НЕ УДАЛЯЕТСЯ: правка эскиза, нет детали, ничего не выбрано, основные плоскости, элемент другой
    /// детали (правится одна — активная) или вложенный в другой элемент.
    /// </summary>
    public static string? WhyNotDeletable(MainWindow w)
    {
        if (w.InSketch) return Loc.L("идёт правка эскиза — сначала закончите его");
        var part = w.Session?.Part;
        if (part == null) return Loc.L("нет активной детали");
        var sel = w.SelectedItems().ToList();
        if (sel.Count == 0) return Loc.L("ничего не выбрано");
        var basic = new HashSet<ModelItem> { part.Origin, part.PlaneXY, part.PlaneXZ, part.PlaneYZ };
        foreach (var it in sel)
        {
            if (basic.Contains(it)) return Loc.L("основные плоскости и начало координат детали не удаляются");
            if (it.Parent == part) continue;
            var owner = it.Parent;
            while (owner != null && owner is not Part) owner = owner.Parent;
            if (owner is Part op && !ReferenceEquals(op, part)) return Loc.L("«{0}» — в детали «{1}», а правится «{2}»: сделайте её активной", it.Name, op.Name, part.Name);
            if (it.Parent is { } par && par != part) return Loc.L("«{0}» — часть «{1}»: удаляется вместе с ней", it.Name, par.Name);
            if (it is Part or Assembly) return Loc.L("деталь или сборка удаляется в сборке, а не в дереве детали");
            return Loc.L("«{0}» не удаляется", it.Name);
        }
        return null;
    }

    private static void Delete(MainWindow w)
    {
        var items = Deletable(w);
        var part = w.Session.Part!;
        string name = items.Count == 1 ? Loc.L("Удалить «{0}»", items[0].Name) : Loc.L("Удалить ({0})", items.Count);
        var kept = new List<string>();
        w.Session.Edit(name, () =>
        {
            foreach (var it in items)
            {
                if (it is Feature f)
                {
                    // эскизы операции уходят вместе с ней, если ими больше никто не пользуется (прежде оставались скрытыми)
                    var own = f.ConsumedSketchIds().ToHashSet();
                    part.RemoveFeature(f);
                    foreach (var sk in part.Sketches.Where(k => own.Contains(k.Id)).ToList())
                    {
                        // k104: эскиз, которым пользуется другая операция, остаётся — с объяснением
                        var user = part.Features.FirstOrDefault(o => o.ConsumedSketchIds().Contains(sk.Id));
                        if (user == null && !items.Contains(sk)) part.Remove(sk);
                        else if (user != null) kept.Add(Loc.L("Эскиз «{0}» оставлен: им пользуется «{1}»", sk.Name, user.Name));
                    }
                }
                else part.Remove(it);
            }
            part.Invalidate();
        });
        w.ViewPort.Selection.Clear();
        w.ViewPort.MarkDirty();
        if (kept.Count > 0) w.ShowToast(string.Join("\n", kept.Distinct()), error: false);
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
