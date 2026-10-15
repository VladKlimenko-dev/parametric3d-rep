using Avalonia.Input;
using Parametric3D.App.Services;
using Parametric3D.App.Views;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;

namespace Parametric3D.App.Commands;

/// <summary>
/// Команды этапов U0–U1: файл, правка, вид, тема, палитра. Имена иконок — из UI_ICONS.md; клавиши — таблица 0.6 ТЗ_UI
/// (стандартные сочетания Windows) и Ctrl+F «вписать».
/// </summary>
public static class StandardCommands
{
    public static void Register(MainWindow w)
    {
        var r = CommandRegistry.Instance;
        r.Clear();
        var vp = w.ViewPort;
        KeyGesture K(Key k, KeyModifiers m = KeyModifiers.None) => new(k, m);

        // ------------------------------------------------------------ файл
        const string F = "Файл";
        r.Add(new AppCommand { Short = "Новая", Id = "file.new_part", Label = "Новая деталь", Group = F, Icon = "new_part_but.svg", Gesture = K(Key.N, KeyModifiers.Control), Synonyms = "new part создать",
            Run = async () => { if (await w.ConfirmDiscard()) w.SetSession(DocumentSession.NewPart()); } });
        r.Add(new AppCommand { Id = "file.sample", Label = "Пробная деталь (куб 100)", Group = F, Synonyms = "sample cube test пример",
            Run = async () => { if (await w.ConfirmDiscard()) w.SetSession(DocumentSession.Sample()); } });
        r.Add(new AppCommand { Short = "Открыть", Id = "file.open", Label = "Открыть…", Group = F, Icon = "open_but.svg", Gesture = K(Key.O, KeyModifiers.Control), Synonyms = "open",
            Run = async () => await w.OpenAsync() });
        r.Add(new AppCommand { Short = "Сохранить", Id = "file.save", Label = "Сохранить", Group = F, Icon = "save_but.svg", Gesture = K(Key.S, KeyModifiers.Control), Synonyms = "save",
            CanRun = () => w.Session?.Part != null, Run = async () => await w.SaveAsync(false) });
        r.Add(new AppCommand { Id = "file.save_as", Label = "Сохранить как…", Group = F, Icon = "save_as_but.svg", Gesture = K(Key.S, KeyModifiers.Control | KeyModifiers.Shift), Synonyms = "save as",
            CanRun = () => w.Session?.Part != null, Run = async () => await w.SaveAsync(true) });
        r.Add(new AppCommand { Id = "file.import", Label = "Импорт (STEP, IGES)…", Group = F, Icon = "import_but.svg", Synonyms = "import step iges",
            Run = async () => await w.ImportAsync() });
        r.Add(new AppCommand { Id = "file.export", Label = "Экспорт (STEP, STL, OBJ)…", Group = F, Icon = "export_but.svg", Synonyms = "export step stl obj",
            CanRun = () => w.Session?.Part?.Bodies.Count > 0, Run = async () => await w.ExportAsync() });
        r.Add(new AppCommand { Id = "app.exit", Label = "Выход", Group = F, Synonyms = "exit quit закрыть", Run = () => w.Close() });

        // ------------------------------------------------------------ правка
        const string E = "Правка";
        r.Add(new AppCommand { Short = "↶", Id = "edit.undo", Label = "Отменить", Group = E, Icon = "undo_but.svg", Gesture = K(Key.Z, KeyModifiers.Control), Synonyms = "undo",
            CanRun = () => w.Session?.CanUndo == true, Run = () => w.Session.Undo() });
        r.Add(new AppCommand { Short = "↷", Id = "edit.redo", Label = "Вернуть", Group = E, Icon = "redo_but.svg", Gesture = K(Key.Y, KeyModifiers.Control), Synonyms = "redo повторить",
            CanRun = () => w.Session?.CanRedo == true, Run = () => w.Session.Redo() });
        r.Add(new AppCommand { Short = "Журнал", Id = "edit.history", Label = "Журнал отмены", Group = E, Icon = "undo_history_but.svg", Synonyms = "history undo журнал история",
            Run = () => w.OpenHistory() });
        r.Add(new AppCommand { Id = "edit.delete", Label = "Удалить", Group = E, Icon = "delete_but.svg", Gesture = K(Key.Delete), Synonyms = "delete remove",
            CanRun = () => Deletable(w).Count > 0, Run = () => Delete(w) });
        r.Add(new AppCommand { Id = "edit.clear_selection", Label = "Снять выбор", Group = E, Icon = "sel_clear_but.svg", Synonyms = "deselect clear",
            Run = () => vp.Selection.Clear() });

        // ------------------------------------------------------------ вид
        const string V = "Вид";
        void Orient(string id, string label, string icon, ViewOrientation o, string syn, KeyGesture? g = null) =>
            r.Add(new AppCommand { Short = label.Replace("Вид ", ""), Id = id, Label = label, Group = V, Icon = icon, Synonyms = syn, Gesture = g, Run = () => vp.AnimateTo(Camera.OrientationQuat(o)) });
        Orient("view.front", "Вид спереди", "view_front_but.svg", ViewOrientation.Front, "front");
        Orient("view.back", "Вид сзади", "view_back_but.svg", ViewOrientation.Back, "back");
        Orient("view.left", "Вид слева", "view_left_but.svg", ViewOrientation.Left, "left");
        Orient("view.right", "Вид справа", "view_right_but.svg", ViewOrientation.Right, "right");
        Orient("view.top", "Вид сверху", "view_top_but.svg", ViewOrientation.Top, "top");
        Orient("view.bottom", "Вид снизу", "view_bottom_but.svg", ViewOrientation.Bottom, "bottom");
        r.Add(new AppCommand { Short = "Изо", Id = "view.iso", Label = "Изометрия (исходный вид)", Group = V, Icon = "view_iso_but.svg", Synonyms = "iso isometric home домой",
            Run = () => vp.GoHome() });
        r.Add(new AppCommand { Short = "Вписать", Id = "view.fit", Label = "Вписать всё", Group = V, Icon = "view_fit_but.svg", Gesture = K(Key.F, KeyModifiers.Control), Synonyms = "fit zoom all",
            Run = () => vp.ZoomToFit() });
        r.Add(new AppCommand { Short = "Персп.", Id = "view.perspective", Label = "Перспектива", Group = V, Icon = "view_perspective_but.svg", Synonyms = "perspective ortho ортогональная проекция",
            IsChecked = () => vp.Camera.State.Mode == ProjectionMode.Perspective, Run = () => vp.ToggleProjection() });
        r.Add(new AppCommand { Short = "Опорные", Id = "view.datums", Label = "Опорные элементы", Group = V, Icon = "datums_visible_but.svg", Synonyms = "datums planes плоскости",
            IsChecked = () => vp.ShowDatumPlanes, Run = () => vp.ShowDatumPlanes = !vp.ShowDatumPlanes });
        void Style(string id, string label, string icon, BodyViewMode m, string syn) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = V, Icon = icon, Synonyms = "style стиль " + syn, IsChecked = () => vp.BodyMode == m, Run = () => vp.BodyMode = m });
        Style("style.shaded", "Тонированный", "style_shaded_but.svg", BodyViewMode.Shaded, "shaded");
        Style("style.shaded_edges", "Тонированный с рёбрами", "style_shaded_edges_but.svg", BodyViewMode.ShadedWithEdges, "edges");
        Style("style.transparent", "Прозрачный", "style_transparent_but.svg", BodyViewMode.Transparent, "transparent");
        Style("style.mesh", "Сетка треугольников", "style_mesh_but.svg", BodyViewMode.Mesh, "mesh wireframe каркас");
        Style("style.volume_mesh", "Объёмная сетка", "style_mesh_but.svg", BodyViewMode.VolumeMesh, "volume mesh");
        r.Add(new AppCommand { Id = "view.hide", Label = "Скрыть", Group = V, Icon = "hide_but.svg", Synonyms = "hide",
            CanRun = () => w.SelectedItems().Count > 0, Run = () => SetVisible(w, w.SelectedItems(), false) });
        r.Add(new AppCommand { Id = "view.isolate", Label = "Изолировать", Group = V, Icon = "isolate_but.svg", Synonyms = "isolate",
            CanRun = () => w.SelectedItems().Count > 0, Run = () => Isolate(w) });
        r.Add(new AppCommand { Id = "view.show_all", Label = "Показать всё", Group = V, Icon = "show_all_but.svg", Synonyms = "show all unhide",
            Run = () => SetVisible(w, w.Session.Document.Root.Descendants().Where(i => i is not Feature).ToList(), true) });

        // ------------------------------------------------------------ приложение
        const string A = "Приложение";
        r.Add(new AppCommand { Short = "◐", Id = "app.theme", Label = "Тема: тёмная / светлая", Group = A, Icon = "theme_but.svg", Synonyms = "theme dark light тема",
            Run = () => w.ApplyTheme(!w.IsDark) });
        r.Add(new AppCommand { Short = "Свойства", Id = "app.properties", Label = "Свойства", Group = A, Icon = "properties_but.svg", Synonyms = "properties info свойства масса объём",
            Run = () => w.ShowProperties() });
        r.Add(new AppCommand { Id = "palette", Label = "Палитра команд", Group = A, Icon = "command_palette_but.svg", Synonyms = "search command palette",
            Run = () => w.OpenPalette() });
        r.Add(new AppCommand { Short = "−", Id = "tree.collapse", Label = "Свернуть дерево", Group = A, Synonyms = "collapse", Run = () => w.SetTreeExpanded(false) });
        r.Add(new AppCommand { Short = "+", Id = "tree.expand", Label = "Развернуть дерево", Group = A, Synonyms = "expand", Run = () => w.SetTreeExpanded(true) });
    }

    // ------------------------------------------------------------ удаление и видимость

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
        string name = items.Count == 1 ? $"Удалить «{(items[0] is Feature f ? f.Summary : items[0].Name)}»" : $"Удалить ({items.Count})";
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

    private static void SetVisible(MainWindow w, List<ModelItem> items, bool visible)
    {
        foreach (var i in items) { i.Visible = visible; i.VisibleUserSet = true; }
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
