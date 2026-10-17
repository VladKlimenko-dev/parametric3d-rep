using Parametric3D.App.Operations;
using Parametric3D.App.Services;
using Parametric3D.App.Views;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// u13h: FREEFORM — кнопки таблицы иконок: новое FreeForm-тело (отдельный файл), «Править в FreeForm», окружение FreeForm
/// (заготовки на плоскости построения, булевы между телами, разрез, перенос/поворот/масштаб, вытягивание, выравнивание,
/// замена, удаление и уклон граней, скругление и фаска рёбер, оболочка, утолщение, зеркало, симметрия, массивы),
/// «Отменить до этого шага», «Обновить ссылки», «Готово».
/// </summary>
public static class FreeFormCommands
{
    const string G = "FreeForm";

    public static void Register(CommandRegistry r, MainWindow w)
    {
        bool FF() => w.InFreeForm;
        void Op(string id, string label, string shortLabel, string icon, Func<Operation> make, string syn) =>
            r.Add(new AppCommand { Id = id, Label = label, Short = shortLabel, Group = G, Icon = icon, Synonyms = syn, CanRun = FF, Run = () => w.StartOperation(make()) });

        r.Add(new AppCommand { Id = "file.new_freeform", Label = "Новое FreeForm-тело", Short = "FreeForm", Group = "Файл", Icon = "new_freeform_but.svg", Synonyms = "new freeform body plasticity новое тело свободное",
            Run = async () => { if (await w.ConfirmDiscard()) w.SetSession(NewFreeForm()); } });
        r.Add(new AppCommand { Id = "op.edit_in_freeform", Label = "Править в FreeForm", Short = "В FreeForm", Group = "Модель", Icon = "f_edit_in_freeform_but.svg", Synonyms = "edit in freeform direct edit править прямое",
            CanRun = () => w.Session?.Part != null && !w.InSketch && !w.InFreeForm && !(w.Session.IsAssembly && w.Session.ActivePart == null), Run = () => EditInFreeForm(w) });
        r.Add(new AppCommand { Id = "ff.finish", Label = "Готово (выйти из FreeForm)", Short = "Готово", Group = G, Icon = "apply_but.svg", Synonyms = "finish freeform exit готово выйти",
            CanRun = () => FF() && w.Session.Part?.FreeFormDoc != true, Run = () => Exit(w) });
        r.Add(new AppCommand { Id = "ff.step_undo_to", Label = "Отменить до этого шага", Short = "До шага", Group = G, Icon = "step_undo_to_but.svg", Synonyms = "undo to step rollback отменить до шага журнал",
            CanRun = () => SelectedStep(w) != null, Run = () => UndoTo(w, SelectedStep(w)!) });
        r.Add(new AppCommand { Id = "ff.update_refs", Label = "Обновить ссылки FreeForm", Short = "Обновить ссылки", Group = G, Icon = "state_refs_changed_ico.svg", Synonyms = "update references refresh freeform обновить ссылки",
            CanRun = () => NodeOf(w) is { RefsChanged: true }, Run = () => UpdateRefs(w, NodeOf(w)!) });
        r.Add(new AppCommand { Id = "ff.cplane", Label = "Плоскость построения", Short = "Пл. построения", Group = G, Icon = "ff_cplane_but.svg", Synonyms = "construction plane cplane плоскость построения",
            CanRun = FF, Run = () => SetConstructionPlane(w) });

        r.Add(new AppCommand { Id = "tree.fix", Label = "Предложенное исправление", Short = "Исправить", Group = "Правка", Icon = "fix_suggestion_ico.svg", Synonyms = "fix suggestion repair исправить отказ",
            CanRun = () => w.SelectedItems().OfType<Feature>().Any(f => f.Error != null), Run = async () => await Fix(w) });

        Op("ff.box", "Брусок", "Брусок", "ff_box_but.svg", () => new FFPrimitiveOp { Shape = FFPrimitive.Box }, "box cube брусок куб");
        Op("ff.cylinder", "Цилиндр", "Цилиндр", "ff_cylinder_but.svg", () => new FFPrimitiveOp { Shape = FFPrimitive.Cylinder }, "cylinder цилиндр");
        Op("ff.cone", "Конус", "Конус", "ff_cone_but.svg", () => new FFPrimitiveOp { Shape = FFPrimitive.Cone }, "cone конус");
        Op("ff.sphere", "Шар", "Шар", "ff_sphere_but.svg", () => new FFPrimitiveOp { Shape = FFPrimitive.Sphere }, "sphere ball шар сфера");
        Op("ff.torus", "Тор", "Тор", "ff_torus_but.svg", () => new FFPrimitiveOp { Shape = FFPrimitive.Torus }, "torus ring тор кольцо");

        Op("ff.union", "Объединить тела", "Объединить", "ff_union_but.svg", () => new FFBooleanOp { Kind = FFBool.Union }, "union boolean join объединить");
        Op("ff.subtract", "Вычесть тела", "Вычесть", "ff_subtract_but.svg", () => new FFBooleanOp { Kind = FFBool.Subtract }, "subtract boolean difference вычесть");
        Op("ff.intersect", "Пересечь тела", "Пересечь", "ff_intersect_but.svg", () => new FFBooleanOp { Kind = FFBool.Intersect }, "intersect boolean common пересечь");
        Op("ff.split", "Разрез тела", "Разрез", "ff_split_but.svg", () => new FFSplitOp(), "split cut body plane разрез разделить");

        Op("ff.move", "Перенос тел", "Перенос", "ff_move_but.svg", () => new FFTransformOp { Mode = FFXform.Move }, "move translate gizmo перенос сдвиг");
        Op("ff.rotate", "Поворот тел", "Поворот", "ff_rotate_but.svg", () => new FFTransformOp { Mode = FFXform.Rotate }, "rotate gizmo поворот вращать");
        Op("ff.scale", "Масштаб тел", "Масштаб", "ff_scale_but.svg", () => new FFTransformOp { Mode = FFXform.Scale }, "scale gizmo масштаб");

        Op("ff.push_pull", "Вытянуть грань", "Push-pull", "ff_push_pull_but.svg", () => new FaceEditOp { MoveFace = true }, "push pull offset face вытянуть сместить грань");
        Op("ff.align_face", "Выровнять грань", "Выровнять", "ff_align_face_but.svg", () => new FFAlignFaceOp(), "align face coplanar выровнять грань");
        Op("ff.replace_face", "Заменить грань", "Заменить", "ff_replace_face_but.svg", () => new QuiltSolidOp { StartMode = QuiltSolidKind.ReplaceFace }, "replace face surface заменить грань");
        Op("ff.delete_face", "Удалить грань", "Удалить грань", "ff_delete_face_but.svg", () => new FaceEditOp { StartMode = FaceEditMode.Delete }, "delete face heal удалить грань залечить");
        Op("ff.draft_face", "Уклон грани", "Уклон", "ff_draft_face_but.svg", () => new DraftOp(), "draft face taper уклон грани");
        Op("ff.fillet", "Скругление рёбер", "Скругление", "ff_fillet_but.svg", () => new RoundOp(), "fillet round скругление рёбер");
        Op("ff.chamfer", "Фаска рёбер", "Фаска", "ff_chamfer_but.svg", () => new ChamferOp(), "chamfer bevel фаска");
        Op("ff.shell", "Оболочка", "Оболочка", "ff_shell_but.svg", () => new ShellOp(), "shell hollow оболочка стенка");
        Op("ff.thicken", "Утолщение листа", "Утолщение", "ff_thicken_but.svg", () => new QuiltSolidOp(), "thicken sheet утолщение лист");

        Op("ff.extrude", "Выдавливание", "Выдавить", "ff_extrude_but.svg", () => w.SketchFirst(new ExtrudeOp()), "extrude region face выдавливание");
        Op("ff.revolve", "Вращение", "Вращение", "ff_revolve_but.svg", () => w.SketchFirst(new RevolveOp()), "revolve вращение");
        Op("ff.sweep", "Протяжка", "Протяжка", "ff_sweep_but.svg", () => new SweepOp(), "sweep протяжка");
        Op("ff.loft", "Лофт", "Лофт", "ff_loft_but.svg", () => new LoftOp(), "loft лофт переход сечения");

        Op("ff.mirror", "Зеркало тел", "Зеркало", "ff_mirror_but.svg", () => new FFMirrorOp { Mode = FFMirrorMode.Copy }, "mirror copy зеркало отражение");
        Op("ff.symmetry", "Симметрия (живая половина)", "Симметрия", "ff_symmetry_but.svg", () => new FFMirrorOp { Mode = FFMirrorMode.Symmetry }, "symmetry live mirror симметрия половина");
        Op("ff.array_linear", "Прямоугольный массив тел", "Массив", "ff_array_linear_but.svg", () => new FFArrayOp { Kind = FFArrayKind.Linear }, "array linear rectangular массив прямоугольный");
        Op("ff.array_circular", "Круговой массив тел", "Кр. массив", "ff_array_circular_but.svg", () => new FFArrayOp { Kind = FFArrayKind.Circular }, "array circular polar массив круговой");
        Op("ff.array_curve", "Массив тел вдоль кривой", "Вдоль кривой", "ff_array_curve_but.svg", () => new FFArrayOp { Kind = FFArrayKind.Curve }, "array curve path массив кривая");
    }

    // ---------------------------------------------------------------- исправление отказа (UI-32)

    /// <summary>Предложения по тексту отказа: потерянная ссылка — заменить ссылки; размер — изменить операцию; прочее — подавить.</summary>
    public static List<(string Label, string Command)> Suggestions(Feature f)
    {
        var e = f.Error ?? "";
        var res = new List<(string, string)>();
        bool lost = e.Contains("no longer exists") || e.Contains("not found") || e.Contains("missing") || e.Contains("mbiguous");
        bool size = e.Contains("radius") || e.Contains("too large") || e.Contains("too big") || e.Contains("exceeds") || e.Contains("thickness") || e.Contains("distance") || e.Contains("zero");
        if (lost) res.Add((L("Заменить ссылки"), "tree.replace_refs"));
        if (size || !lost) res.Add((L("Изменить параметры операции"), "edit.feature"));
        if (f is FreeFormFeature { RefsChanged: true } || e.Contains("references changed")) res.Add((L("Обновить ссылки FreeForm"), "ff.update_refs"));
        res.Add((L("Подавить операцию"), "edit.suppress"));
        return res;
    }

    static async Task Fix(MainWindow w)
    {
        var f = w.SelectedItems().OfType<Feature>().FirstOrDefault(x => x.Error != null); if (f == null) return;
        var opts = Suggestions(f);
        var pick = await Dialogs.Ask(w, L("Исправление: «{0}»", f.Name), f.Error ?? "", opts.Select(o => o.Label).Append(L("Отмена")).ToArray());
        var cmd = opts.FirstOrDefault(o => o.Label == pick).Command;
        if (cmd != null) CommandRegistry.Instance.Find(cmd)?.Execute();
    }

    // ---------------------------------------------------------------- документ и окружение

    /// <summary>Новый FreeForm-документ: деталь-FreeForm с пустым узлом FreeForm.</summary>
    public static DocumentSession NewFreeForm()
    {
        var s = DocumentSession.NewPart();
        var part = s.Part!;
        part.Name = s.Document.Title = L("FreeForm") + " " + part.Name.TrimStart(L("Деталь").ToCharArray());
        part.FreeFormDoc = true;
        part.AddFeature(new FreeFormFeature(L("FreeForm-тело")));
        return s;
    }

    /// <summary>FreeForm-документ открыт: окружение FreeForm на его (первом) узле; узла нет — создаётся.</summary>
    public static void EnterDocument(MainWindow w, Part part, bool rebuildUi = true)
    {
        var node = part.Features.OfType<FreeFormFeature>().FirstOrDefault();
        if (node == null) { node = new FreeFormFeature(L("FreeForm-тело")); part.Features.Insert(0, node); part.Invalidate(); }
        Enter(w, part, node, rebuildUi);
    }

    /// <summary>Открыть узел FreeForm для правки: новые операции — его шаги (строка отката — после его последнего шага).</summary>
    public static void Enter(MainWindow w, Part part, FreeFormFeature node, bool rebuildUi = true)
    {
        if (w.InSketch) w.FinishSketch();
        if (w.CurrentOperation != null) w.CancelOperation();
        part.EditingFreeForm = node.Key;
        w.FreeFormKey = node.Key;
        int last = part.Features.FindLastIndex(f => f.FreeForm == node.Key);
        _savedStage = part.StageIndex;
        if (last >= 0 && last < part.Features.Count - 1) part.StageIndex = part.ClampStage(last + 1);
        part.Invalidate();
        _savedGrid = w.ViewPort.ModelGrid;
        w.ViewPort.GridFrame = part.ConstructionPlane;
        w.ViewPort.ModelGrid = true;
        if (rebuildUi) { w.RebuildUi(); w.RebuildTree(); w.ViewPort.MarkDirty(); }
    }
    private static int _savedStage = -1;
    private static bool _savedGrid;

    /// <summary>«Готово»: узел FreeForm закрыт — в параметрической детали его тела объединяются, строка отката — как была.</summary>
    public static void Exit(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        if (w.CurrentOperation != null) w.CommitOperation();
        part.EditingFreeForm = null;
        w.FreeFormKey = null;
        if (!part.FreeFormDoc) part.StageIndex = _savedStage < 0 ? -1 : part.ClampStage(_savedStage);
        part.Invalidate();
        w.ViewPort.ModelGrid = _savedGrid;
        w.ViewPort.GridFrame = Frame3.XY;
        w.RebuildUi(); w.RebuildTree(); w.ViewPort.MarkDirty();
    }

    /// <summary>«Править в FreeForm»: узел FreeForm после всех операций детали, вход — её тело (подпись и копия запоминаются).</summary>
    public static void EditInFreeForm(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        if (part.IsDirty) w.Session.Document.RebuildDirty();
        FreeFormFeature node = null!;
        w.Session.Edit(L("Править в FreeForm"), () =>
        {
            node = new FreeFormFeature(L("FreeForm {0}", part.Features.OfType<FreeFormFeature>().Count() + 1));
            node.Capture(part.Bodies);
            if (part.InInsertMode) part.EndInsertMode(resume: true);
            part.AddFeature(node);
        });
        Enter(w, part, node);
        w.ShowToast(L("FreeForm: прямые правки тела; «Готово» — назад к детали"), error: false);
    }

    /// <summary>Выбранный в дереве шаг FreeForm (не узел).</summary>
    static Feature? SelectedStep(MainWindow w) =>
        w.SelectedItems().OfType<Feature>().FirstOrDefault(f => f.FreeForm != null && f is not FreeFormFeature);

    /// <summary>Узел FreeForm: выбранный в дереве или открытый.</summary>
    static FreeFormFeature? NodeOf(MainWindow w)
    {
        var part = w.Session?.Part; if (part == null) return null;
        if (w.SelectedItems().OfType<FreeFormFeature>().FirstOrDefault() is { } sel) return sel;
        var key = part.EditingFreeForm ?? w.SelectedItems().OfType<Feature>().FirstOrDefault(f => f.FreeForm != null)?.FreeForm;
        return key == null ? null : part.Features.OfType<FreeFormFeature>().FirstOrDefault(f => f.Key == key);
    }

    /// <summary>«Отменить до этого шага»: шаг и все следующие шаги его узла убираются (одним шагом отмены).</summary>
    public static void UndoTo(MainWindow w, Feature step)
    {
        var part = w.Session.Part; if (part == null || step.FreeForm == null) return;
        if (w.CurrentOperation != null) w.CancelOperation();
        int at = part.Features.IndexOf(step); if (at < 0) return;
        var drop = part.Features.Skip(at).Where(f => f.FreeForm == step.FreeForm && f is not FreeFormFeature).ToList();
        w.Session.Edit(L("Отменить до шага «{0}»", step.Name), () => { foreach (var f in drop) part.RemoveFeature(f); });
        w.ViewPort.Selection.Clear();
        w.RebuildTree(); w.ViewPort.MarkDirty();
        w.ShowToast(L("Убрано шагов FreeForm: {0}", drop.Count), error: false);
    }

    /// <summary>«Обновить ссылки»: вход узла — текущее тело детали перед ним.</summary>
    public static void UpdateRefs(MainWindow w, FreeFormFeature node)
    {
        var part = w.Session.Part; if (part == null) return;
        int at = part.Features.IndexOf(node); if (at < 0) return;
        // тело перед узлом: расчёт с полоской на узле
        var probe = Model.DocumentFile.Load(Model.DocumentFile.Save(part, w.Session.Document.Parameters)).Part;
        probe.EditingFreeForm = null; probe.StageIndex = at;
        probe.Rebuild(w.Session.Document.ScopeFor(part));
        w.Session.Edit(L("Обновить ссылки FreeForm"), () => { node.Capture(probe.Bodies); part.NotifyChanged(node); });
        w.RebuildTree(); w.ViewPort.MarkDirty();
    }

    /// <summary>Плоскость построения: выбранная плоскость (грань, опорная); ничего не выбрано — по кругу XY → XZ → YZ.</summary>
    public static void SetConstructionPlane(MainWindow w)
    {
        var part = w.Session.Part; if (part == null) return;
        Frame3 fr;
        string name;
        if (w.ViewPort.Selection.Items.FirstOrDefault(Refs.IsPlanar) is { } r && Refs.PlaneOf(r) is { } pl) { fr = pl.Frame; name = pl.Name; }
        else
        {
            var cur = part.ConstructionPlane;
            bool isXY = (cur.W - Frame3.XY.W).Length < 1e-9 && cur.Origin.Length < 1e-9;
            bool isXZ = (cur.W - Frame3.XZ.W).Length < 1e-9 && cur.Origin.Length < 1e-9;
            (fr, name) = isXY ? (Frame3.XZ, "XZ") : isXZ ? (Frame3.YZ, "YZ") : (Frame3.XY, "XY");
        }
        w.Session.Edit(L("Плоскость построения"), () => part.ConstructionPlane = fr);
        w.ViewPort.GridFrame = fr;
        w.ShowToast(L("Плоскость построения: {0}", name), error: false);
    }
}
