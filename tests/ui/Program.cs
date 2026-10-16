using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using OpenTK.Mathematics;
using Parametric3D.App;
using Parametric3D.App.Commands;
using Parametric3D.App.Icons;
using Parametric3D.App.Operations;
using Parametric3D.App.Sketching;
using Parametric3D.App.ViewModels;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;
using Parametric3D.App.Services;
using Parametric3D.App.Viewport;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Model.Features;
using Parametric3D.Render;

// ПРОВЕРКИ ПРИЛОЖЕНИЯ (этапы U0–U1) без экрана: Avalonia.Headless, вид — без OpenGL (выбор под курсором считается на
// процессоре, см. ScenePicker). Каждая проверка — строка JSON {"test", "ok", "error"}; итог — {"passed", "failed"}.

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

string? only = args.FirstOrDefault();
int passed = 0, failed = 0;

void Check(string name, Action body)
{
    if (only != null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return;
    string? err = null;
    try { body(); } catch (Exception ex) { err = ex is CheckFailed ? ex.Message : ex.ToString(); }
    if (err == null) passed++; else failed++;
    Console.WriteLine(JsonSerializer.Serialize(new { test = name, ok = err == null, error = err }));
}

void Require(bool cond, string msg) { if (!cond) throw new CheckFailed(msg); }

MainWindow Open(DocumentSession s)
{
    var w = new MainWindow(s) { Width = 1400, Height = 860 };
    w.Show();
    Dispatcher.UIThread.RunJobs();
    w.ViewPort.Document?.RebuildDirty();
    w.ViewPort.ZoomToFit(animate: false);
    Dispatcher.UIThread.RunJobs();
    return w;
}

string IconsTsv = Path.Combine(AppContext.BaseDirectory, "icons.tsv");

Check("UI-COMMAND-REGISTRY", () =>
{
    // реестр: уникальные имена, у каждой команды есть подпись и группа; иконки — только из списка UI_ICONS (icons.tsv)
    var w = Open(DocumentSession.NewPart());
    var reg = CommandRegistry.Instance;
    Require(reg.All.Count >= 30, $"commands: {reg.All.Count}");
    Require(reg.All.Select(c => c.Id).Distinct().Count() == reg.All.Count, "duplicate ids");
    Require(reg.All.All(c => !string.IsNullOrWhiteSpace(c.Label) && !string.IsNullOrWhiteSpace(c.Group)), "a command without label or group");
    var known = File.ReadAllLines(IconsTsv).Where(l => !l.StartsWith("#") && l.Length > 0).Select(l => l.Split('\t')[0]).ToHashSet();
    var bad = reg.All.Where(c => c.Icon != null && !known.Contains(c.Icon)).Select(c => $"{c.Id}:{c.Icon}").ToList();
    Require(bad.Count == 0, "icons not in UI_ICONS: " + string.Join(", ", bad));
    // клавиши без конфликтов
    // одна клавиша допустима у операции модели и у инструмента эскиза (они не доступны одновременно)
    var g = reg.All.Where(c => c.Gesture != null).GroupBy(c => c.Gesture!.ToString())
        .Where(x => x.Count() > 1 && !(x.Count() == 2 && x.Select(c => c.Group).OrderBy(n => n).SequenceEqual(new[] { "Модель", "Эскиз" })))
        .Select(x => x.Key).ToList();
    Require(g.Count == 0, "gesture conflicts: " + string.Join(", ", g));
    w.Close();
});

Check("UI-PALETTE-SEARCH", () =>
{
    // палитра (F): поиск по-русски и по-английски, все слова запроса (UI-10)
    var w = Open(DocumentSession.NewPart());
    var reg = CommandRegistry.Instance;
    Require(reg.Search("сохран").FirstOrDefault()?.Id == "file.save", "«сохран» → " + reg.Search("сохран").FirstOrDefault()?.Id);
    Require(reg.Search("save").Any(c => c.Id == "file.save_as"), "«save» does not find save as");
    Require(reg.Search("front").FirstOrDefault()?.Id == "view.front", "«front»");
    Require(reg.Search("вид сверху").FirstOrDefault()?.Id == "view.top", "«вид сверху»");
    Require(reg.Search("нет такой команды").Count == 0, "garbage query found something");
    // палитра открывается по F и выполняет команду Enter
    w.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
    Dispatcher.UIThread.RunJobs();
    var shade = w.FindControl<Avalonia.Controls.Border>("PaletteShade")!;
    Require(shade.IsVisible, "F does not open the palette");
    var input = w.FindControl<Avalonia.Controls.TextBox>("PaletteInput")!;
    input.Text = "вид сверху";
    Dispatcher.UIThread.RunJobs();
    input.Focus();
    w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
    Dispatcher.UIThread.RunJobs();
    Require(!shade.IsVisible, "Enter does not close the palette");
    Require(w.ViewPort.Transition.IsRunning, "the command did not start a view transition");
    w.Close();
});

Check("UI-UNDO-REDO", () =>
{
    // одна правка — один шаг отмены; отмена и повтор возвращают модель (UI-22, UI-37)
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    int f0 = part.Children.OfType<Feature>().Count();
    Require(f0 > 0, "the sample part has no features");
    var feat = part.Children.OfType<Feature>().Last();
    w.SelectItem(feat);
    CommandRegistry.Instance["edit.delete"].Execute();
    Require(s.Part!.Children.OfType<Feature>().Count() == f0 - 1, "delete did not remove the feature");
    Require(s.CanUndo && s.UndoNames.Count == 1, "no undo step");
    CommandRegistry.Instance["edit.undo"].Execute();
    Require(s.Part!.Children.OfType<Feature>().Count() == f0, "undo did not restore");
    Require(s.CanRedo, "no redo");
    CommandRegistry.Instance["edit.redo"].Execute();
    Require(s.Part!.Children.OfType<Feature>().Count() == f0 - 1, "redo did not delete again");
    w.Close();
});

Check("UI-FILE-ROUNDTRIP", () =>
{
    // сохранить и открыть: тот же замысел (операции) и тот же объём после перестроения
    var s = DocumentSession.Sample();
    var w = Open(s);
    s.Part!.Rebuild(s.Document.Parameters);
    double v0 = s.Part.Volume;
    string path = Path.Combine(Path.GetTempPath(), $"uicheck_{Guid.NewGuid():N}.p3d");
    s.SaveAs(path);
    Require(!s.Modified, "modified after save");
    var s2 = DocumentSession.Open(path);
    s2.Part!.Rebuild(s2.Document.Parameters);
    Require(s2.Part.Children.OfType<Feature>().Count() == s.Part.Children.OfType<Feature>().Count(), "feature count differs");
    Require(Math.Abs(s2.Part.Volume - v0) <= 1e-9 * Math.Max(1, v0), $"volume {s2.Part.Volume} vs {v0}");
    // экспорт: точный STEP и STL
    string step = Path.ChangeExtension(path, ".step"), stl = Path.ChangeExtension(path, ".stl");
    string m1 = ExportService.Export(s2, step), m2 = ExportService.Export(s2, stl);
    Require(File.Exists(step) && m1.Contains("точный"), "STEP: " + m1);
    Require(File.Exists(stl) && m2.Contains("замкнута"), "STL: " + m2);
    foreach (var f in new[] { path, step, stl }) File.Delete(f);
    w.Close();
});

Check("UI-VIEW-PICK", () =>
{
    // выбор в виде мышью: наведение на середину куба — грань (подсветка), щелчок — выбор, Shift — добавить, щелчок в
    // пустоту — снять выбор; выбор детали в дереве — тело (UI-21, раздел 2)
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    vp.Show.Planes = false; vp.MarkDirty();
    vp.Camera.State.Rotation = ModelViewport.HomeRotation;
    vp.ZoomToFit(animate: false);
    var c = vp.TranslatePoint(new Point(vp.Bounds.Width / 2, vp.Bounds.Height / 2), w)!.Value;
    w.MouseMove(c);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.Hover.Kind == PickKind.Face || vp.Selection.Hover.Kind == PickKind.Edge || vp.Selection.Hover.Kind == PickKind.Vertex,
        $"hover at the middle: {vp.Selection.Hover.Kind}");
    w.MouseDown(c, MouseButton.Left); w.MouseUp(c, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.Count == 1, $"selected {vp.Selection.Count}");
    var first = vp.Selection.Primary;
    var c2 = new Point(c.X + 60, c.Y + 60);
    w.MouseMove(c2); w.MouseDown(c2, MouseButton.Left, RawInputModifiers.Shift); w.MouseUp(c2, MouseButton.Left, RawInputModifiers.Shift);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.Count == 2 || vp.Selection.Primary.Id == first.Id, $"shift-add: {vp.Selection.Count}");
    var empty = vp.TranslatePoint(new Point(8, vp.Bounds.Height - 8), w)!.Value;
    w.MouseMove(empty); w.MouseDown(empty, MouseButton.Left); w.MouseUp(empty, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.IsEmpty, "click on empty space did not clear");
    w.SelectItem(s.Part!);
    Require(vp.Selection.Primary.Kind == PickKind.Body, "tree part → body");
    // Esc снимает выбор
    w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.IsEmpty, "Esc did not clear the selection");
    w.Close();
});

Check("UI-VIEWCUBE-ORIENT", () =>
{
    // куб вида (UI-45): грань — взгляд по нормали, ребро — по биссектрисе, угол — изометрия; верх не переворачивается;
    // согласовано с прежними видами камеры (Camera.OrientationQuat)
    static Vector3 Fwd(Quaternion q) => Vector3.Transform(-Vector3.UnitZ, Quaternion.Invert(q));
    static Vector3 Up(Quaternion q) => Vector3.Transform(Vector3.UnitY, Quaternion.Invert(q));
    var fails = new List<string>();
    for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (x == 0 && y == 0 && z == 0) continue;
                var d = new Vector3(x, y, z);
                var q = ViewMath.LookFrom(d, Vector3.UnitY);
                var f = Fwd(q);
                if ((f + d.Normalized()).Length > 1e-5f) fails.Add($"{d}: forward {f}");
                var u = Up(q);
                if (MathF.Abs(Vector3.Dot(u, f)) > 1e-5f) fails.Add($"{d}: up not orthogonal");
                if (y == 0 && u.Y < 0.5f) fails.Add($"{d}: up {u}");
            }
    foreach (var (o, d) in new[] { (ViewOrientation.Front, Vector3.UnitZ), (ViewOrientation.Top, Vector3.UnitY), (ViewOrientation.Right, Vector3.UnitX), (ViewOrientation.Back, -Vector3.UnitZ), (ViewOrientation.Left, -Vector3.UnitX) })
        if ((Fwd(Camera.OrientationQuat(o)) + d).Length > 1e-5f) fails.Add($"{o}: camera forward {Fwd(Camera.OrientationQuat(o))}");
    // вид сверху с верхом «назад» — как Camera.OrientationQuat(Top)
    var top = ViewMath.LookFrom(Vector3.UnitY, -Vector3.UnitZ);
    if ((Up(top) + Vector3.UnitZ).Length > 1e-5f) fails.Add($"top up {Up(top)}");
    // клик по кубу запускает плавный переход к нужному виду
    var w = Open(DocumentSession.Sample());
    var cube = w.FindControl<ViewCube>("Cube")!;
    cube.Click(new Vector3i(1, 0, 0), 0);
    Require(w.ViewPort.Transition.IsRunning, "cube click did not start a transition");
    // поворот на 90° вокруг взгляда сохраняет направление взгляда
    var q0 = ViewMath.LookFrom(Vector3.UnitZ, Vector3.UnitY);
    var q1 = Quaternion.FromAxisAngle(Vector3.UnitZ, MathHelper.PiOver2) * q0;
    if ((Fwd(q1) - Fwd(q0)).Length > 1e-5f) fails.Add("rotate 90 changes the view direction");
    if (MathF.Abs(Vector3.Dot(Up(q1), Up(q0))) > 1e-5f) fails.Add("rotate 90 does not turn the up vector");
    w.Close();
    Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
});

Check("UI-THEME-ICONS", () =>
{
    // тема: тёмная/светлая меняет тему Avalonia, цвет перекраски иконок и палитру вида; перекраска трогает только чёрный
    var w = Open(DocumentSession.NewPart());
    w.ApplyTheme(false);
    Require(Application.Current!.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light, "light theme not applied");
    Require(IconProvider.Foreground.R < 80, "icon foreground on light theme");
    Require(!w.ViewPort.DarkTheme, "view palette");
    w.ApplyTheme(true);
    Require(IconProvider.Foreground.R > 200, "icon foreground on dark theme");
    string svg = "<svg><path stroke=\"#000000\" fill=\"#FF7A1A\"/><path style=\"fill:black;stroke:#000\"/></svg>";
    string r = IconProvider.Recolor(svg, "#E6E8EC");
    Require(r.Contains("stroke=\"#E6E8EC\"") && r.Contains("#FF7A1A") && r.Contains("fill:#E6E8EC") && r.Contains("stroke:#E6E8EC"), r);
    // без файла иконки — подпись
    Require(IconProvider.Get("no_such_icon_but.svg") == null, "missing icon");
    w.Close();
});


Check("UI-TREE-VISIBILITY", () =>
{
    // п. 4: глаз детали скрывает всё внутри; п. 5: «свернуть дерево» сворачивает и деталь; п. 13: перестройка дерева
    // (глаз эскиза) не сворачивает развёрнутое
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    var tree = w.FindControl<TreeView>("Tree")!;
    var root = ((IEnumerable<TreeNode>)tree.ItemsSource!).First();
    Require(root.Item == part, "tree root is not the part");
    root.IsVisible = false;
    Require(part.Descendants().All(d => !d.Visible), "hiding the part did not hide its children");
    Require(root.SelfAndDescendants().Where(n => n.HasEye).All(n => !n.IsVisible), "child eyes are still open");
    root.IsVisible = true;
    Require(part.Descendants().All(d => d.Visible), "showing the part did not show its children");
    var feat = w.AllNodes.First(n => n.Item is Feature && n.Children.Count > 0);
    feat.IsExpanded = true;
    var sk = feat.Children.First(n => n.Item is Sketch);
    sk.IsVisible = !sk.IsVisible;
    w.RebuildTree();
    Require(w.AllNodes.First(n => n.Item == feat.Item).IsExpanded, "rebuild collapsed the feature branch");
    w.SetTreeExpanded(false);
    Require(!w.AllNodes.First().IsExpanded, "collapse does not collapse the part");
    w.SetTreeExpanded(true);
    Require(w.AllNodes.First().IsExpanded, "expand does not expand the part");
    w.Close();
});

Check("UI-OPERATION-ROUND", () =>
{
    // операция в панели: выбор до команды → поле ссылок, предпросмотр в модели, «ОК» — один шаг отмены, «Отмена» — как было
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    int f0 = part.Features.Count, u0 = s.UndoNames.Count;
    var edge = part.Bodies[0].Edges.First();
    w.ViewPort.Selection.Select(new PickRef(edge.Id, PickKind.Edge, part, null), additive: false);
    Require(w.SuggestedOps().Contains("op.round"), "edge does not suggest a round");
    w.StartOperation(new RoundOp());
    Require(w.CurrentOperation != null && part.Features.Count == f0 + 1, "round was not started with a preview");
    var round = (RoundFeature)part.Features.Last();
    Require(round.Edges.Count == 1, $"edges from the selection: {round.Edges.Count}");
    s.Document.RebuildDirty();
    Require(round.Error == null, "round error: " + round.Error);
    w.CommitOperation();
    Require(w.CurrentOperation == null && s.UndoNames.Count == u0 + 1, "commit is not one undo step");
    // отмена новой операции
    w.ViewPort.Selection.Select(new PickRef(part.Bodies[0].Edges.Last().Id, PickKind.Edge, part, null), additive: false);
    w.StartOperation(new ChamferOp());
    w.CancelOperation();
    Require(s.Part!.Features.Count == f0 + 1 && s.UndoNames.Count == u0 + 1, "cancel did not restore the model");
    Require(!s.CanRedo, "cancel left a redo step");
    w.Close();
});

Check("UI-SKETCH-EXTRUDE", () =>
{
    // эскиз на плоскости → прямоугольник → закончить → выдавливание по нему (эскиз сначала, п. 3 и 9)
    var s = DocumentSession.NewPart();
    var w = Open(s);
    var part = s.Part!;
    w.SelectItem(part.PlaneXY);
    w.StartOperation(new SketchOp { Then = sk => new ExtrudeOp { SeedSketch = sk } });
    Require(w.InSketch, "sketch mode did not start on the selected plane");
    var sketch = w.ViewPort.ActiveSketch!;
    var rect = new RectangleTool();
    rect.Click(new SketchInput(sketch, new Vector2d(-20, -10), null));
    Require(rect.Click(new SketchInput(sketch, new Vector2d(20, 10), null)), "rectangle not created");
    w.FinishSketch();
    Require(!w.InSketch && w.CurrentOperation is ExtrudeOp, "extrude did not follow the sketch");
    w.CommitOperation();
    s.Document.RebuildDirty();
    Require(part.Bodies.Count == 1 && Math.Abs(part.Volume - 40 * 20 * 20) < 1e-6, $"volume {part.Volume}");
    w.Close();
});

Check("UI-PARAMETERS-SCOPE", () =>
{
    // параметры по уровням: формула детали видит параметр документа, своё имя закрывает документное; файл хранит оба уровня
    var s = DocumentSession.NewPart();
    var part = s.Part!;
    s.Document.Parameters.Add("W", 30, Parametric3D.Model.Params.ParamKind.Real, "mm");
    s.Document.Parameters.Add("H", 5, Parametric3D.Model.Params.ParamKind.Real, "mm");
    part.LocalParameters.Add("H", 7, Parametric3D.Model.Params.ParamKind.Real, "mm");
    part.LocalParameters.AddFormula("D", "W + H", "mm");
    var t = s.Document.ScopeFor(part);
    Require(t.TryValue("D", out double d) && Math.Abs(d - 37) < 1e-9, $"D = {d}");
    Require(s.Document.Parameters.TryValue("H", out double h) && h == 5, "document H changed");
    string path = Path.Combine(Path.GetTempPath(), $"uiparams_{Guid.NewGuid():N}.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path);
    var t2 = s2.Document.ScopeFor(s2.Part!);
    Require(s2.Part!.LocalParameters.Count == 2 && t2.TryValue("D", out double d2) && Math.Abs(d2 - 37) < 1e-9, "part parameters were not saved");
    File.Delete(path);
});

Check("UI-LANGUAGE", () =>
{
    // язык (п. 2): английские подписи команд и обратно; у каждой команды на панелях есть перевод
    var w = Open(DocumentSession.NewPart());
    Loc.Set(true);
    var reg = CommandRegistry.Instance;
    Require(reg["file.save"].Text == "Save" && reg["op.extrude"].Text == "Extrude", "english labels");
    var missing = reg.All.Where(c => c.Text == c.Label || c.ShortText == (c.Short ?? c.Label) && c.Short != null && c.Short.Any(ch => ch >= 'А' && ch <= 'я'))
        .Where(c => c.Label.Any(ch => ch >= 'А' && ch <= 'я')).Select(c => c.Id).ToList();
    Loc.Set(false);
    Require(reg["file.save"].Text == "Сохранить", "russian labels");
    Require(missing.Count == 0, "no translation: " + string.Join(", ", missing));
    w.Close();
});

Check("UI-TANGENT-CHAIN-CHAMFER", () =>
{
    // п. 4: ребро вместе с касательной цепочкой; фаска по цепочке строится годным телом (снимок пользователя — фаска
    // только части цепочки)
    var p = Parametric3D.App.Diagnostics.TestParts.Block("chain");
    p.Rebuild(null);
    var vertical = p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.X - 50) < 1e-6 && Math.Abs(e.V0.P.Y - 30) < 1e-6 && Math.Abs(e.V1.P.X - 50) < 1e-6 && Math.Abs(e.V1.P.Y - 30) < 1e-6);
    var rf = new RoundFeature("R") { Radius = new ValueRef(8) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(vertical));
    p.AddFeature(rf); p.Rebuild(null);
    Require(rf.Error == null, "round: " + rf.Error);
    // верхнее прямое ребро у скругления: z = 20, y = 30
    var top = p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-6 && Math.Abs(e.V1.P.Z - 20) < 1e-6 && Math.Abs(e.V0.P.Y - 30) < 1e-6 && Math.Abs(e.V1.P.Y - 30) < 1e-6);
    var chain = Topo.TangentChain(top);
    Require(chain.Count == 3, $"chain of the top edge: {chain.Count} edges (expected line + arc + line)");
    var cf = new ChamferFeature("C") { D1 = new ValueRef(2), D2 = new ValueRef(2) };
    foreach (var e in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e));
    p.AddFeature(cf); p.Rebuild(null);
    Require(cf.Error == null, "chamfer of the chain: " + cf.Error);
    Require(p.Bodies.All(b => Parametric3D.Kernel.Topology.SolidValidator.Validate(b).IsValid), "chamfered body is not valid");
});

Check("UI-SKETCH-REFERENCES-DIMS", () =>
{
    // п. 2.3–2.4: ребро детали — ссылка эскиза; размеры по умолчанию определяют эскиз полностью (степеней свободы 0)
    var part = Parametric3D.App.Diagnostics.TestParts.Block("refs");
    var s = DocumentSession.FromPart(part, "refs");
    var w = Open(s);
    s.Document.RebuildDirty();
    var face = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    w.ViewPort.Selection.Select(new PickRef(face.Id, PickKind.Face, part, null), additive: false);
    w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch did not start on the top face");
    var sk = w.ViewPort.ActiveSketch!;
    var edge = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-6 && Math.Abs(e.V1.P.Z - 20) < 1e-6);
    Require(SketchReferences.Add(sk, part, new PickRef(edge.Id, PickKind.Edge, part, null)) == null, "reference not added");
    Require(sk.Entities.Any(e => e.Role == SkRole.Reference), "no reference entity");
    var rect = new RectangleTool();
    rect.Click(new SketchInput(sk, new Vector2d(-10, -5), null));
    rect.Click(new SketchInput(sk, new Vector2d(10, 5), null));
    sk.Solve(s.Document.Solver);
    w.ViewPort.RefreshDefaultDimensions();
    // размеры по умолчанию — «слабые» (как в Creo): показывают, чем определена фигура; заданные вместе — 0 степеней свободы
    var defaults = sk.Dimensions.Where(d => d.IsDefault).ToList();
    foreach (var d in defaults) d.Locked = true;
    s.Document.Solver.Solve(sk, null, out int dof);
    foreach (var d in defaults) d.Locked = false;
    Require(dof == 0, $"sketch is not fully defined: {dof} degrees of freedom left");
    Require(defaults.Any(d => d.Items.Any(i => i.Role == SkRole.Reference || i is SkPoint { Owner.Role: SkRole.Reference } || (i is SkPoint sp && sp.Role == SkRole.Reference))), "no dimension to the reference edge");
    Require(sk.Dimensions.Count(d => d.IsDefault) >= 4, $"default dimensions: {sk.Dimensions.Count}");
    w.CancelSketch();
    Require(!part.Sketches.Contains(sk), "cancelled new sketch is still in the part");
    w.Close();
});

Check("UI-CANCEL-KEEPS-PART", () =>
{
    // п. 2: «Отмена» новой операции не заменяет деталь (на экране не пропадает) и не оставляет шага отмены
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    int f0 = part.Features.Count, u0 = s.UndoNames.Count;
    w.ViewPort.Selection.Select(new PickRef(part.Bodies[0].Edges.First().Id, PickKind.Edge, part, null), additive: false);
    w.StartOperation(new RoundOp());
    w.CancelOperation();
    Require(ReferenceEquals(s.Part, part), "the part was replaced on cancel");
    Require(part.Features.Count == f0 && s.UndoNames.Count == u0 && !s.CanRedo, "cancel left traces");
    w.Close();
});

Check("UI-CTRL-TOGGLE", () =>
{
    // п. 7: Ctrl+щелчок — добавить, повторно — убрать
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    vp.Show.Planes = false; vp.MarkDirty();
    vp.Camera.State.Rotation = ModelViewport.HomeRotation;
    vp.ZoomToFit(animate: false);
    var c = vp.TranslatePoint(new Point(vp.Bounds.Width / 2 + 40, vp.Bounds.Height / 2 - 60), w)!.Value;
    var c2 = vp.TranslatePoint(new Point(vp.Bounds.Width / 2 - 60, vp.Bounds.Height / 2 + 60), w)!.Value;
    w.MouseMove(c); w.MouseDown(c, MouseButton.Left); w.MouseUp(c, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    int n1 = vp.Selection.Count;
    w.MouseMove(c2); w.MouseDown(c2, MouseButton.Left, RawInputModifiers.Control); w.MouseUp(c2, MouseButton.Left, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
    int n2 = vp.Selection.Count;
    w.MouseDown(c2, MouseButton.Left, RawInputModifiers.Control); w.MouseUp(c2, MouseButton.Left, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
    int n3 = vp.Selection.Count;
    Require(n1 >= 1 && n2 > n1 && n3 == n1, $"ctrl toggle: {n1} → {n2} → {n3}");
    w.Close();
});

void BuildNow(DocumentSession ses)
{
    for (int k = 0; k < 20; k++)
    {
        var t = ses.Document.RebuildDirtyAsync();
        while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!ses.Document.AllParts().Any(p => p.IsDirty)) break;
    }
    Dispatcher.UIThread.RunJobs();
}

Check("UI-HANDLE-CONTOUR", () =>
{
    // п. 6, предпросмотр контуром: пока тянут — деталь не меняется, панель закрыта; одна ручка: внутрь — скругление,
    // через начальное место наружу — фаска; отпустили — одна операция; повторно — та же; Esc — следов нет
    UserPrefs.Current.PreviewMode = "contour";
    try
    {
        var s = DocumentSession.Sample();
        var w = Open(s);
        var vp = w.ViewPort;
        var part = s.Part!;
        int f0 = part.Features.Count, v0 = part.Version;
        var edge = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3);
        vp.Selection.Select(new PickRef(edge.Id, PickKind.Edge, part, null), additive: false);
        Dispatcher.UIThread.RunJobs();
        Require(vp.Handle != null, "no handle on the edge");
        vp.SimulateHandleDrag(-3, end: false);
        Require(w.CurrentOperation is RoundOp, "inward drag did not start a round");
        int segRound = vp.GhostSegments;
        vp.SimulateHandleDrag(2, end: false);
        Require(w.CurrentOperation is ChamferOp, "outward past the rest did not switch to a chamfer");
        vp.SimulateHandleDrag(-4.5, end: false);
        Require(w.CurrentOperation is RoundOp, "back inward did not switch to a round");
        Require(part.Features.Count == f0 && part.Version == v0, "part changed while dragging (contour mode)");
        // контур: две границы и торцы — у одного прямого ребра 2 + 2·8 отрезков
        Require(vp.GhostShown && vp.GhostSegments <= 2 + 2 * 8 && segRound > 0, $"outline expected, segments {vp.GhostSegments}");
        Require(!w.OperationPanelVisible, "panel opened while the properties panel is closed");
        vp.SimulateHandleDrag(-5, end: true);
        Require(part.Features.Count == f0 + 1 && part.Features[^1] is RoundFeature rf && Math.Abs(rf.Radius.Value - 5) < 1e-9, "round not added with the dragged value");
        var op = w.CurrentOperation;
        vp.SimulateHandleDrag(-6, end: true);
        Require(ReferenceEquals(op, w.CurrentOperation) && ((RoundFeature)part.Features[^1]).Radius.Value == 6, "second drag did not continue the operation");
        w.CancelOperation();
        Require(part.Features.Count == f0 && !vp.GhostShown && s.UndoNames.Count == 0, "cancel left traces");
        w.Close();
    }
    finally { UserPrefs.Current.PreviewMode = "full"; }
});

Check("UI-HANDLE-LIVE", () =>
{
    // полный предпросмотр: операция в детали сразу, считается по ходу (грубо, без проверок); сторона меняет операцию с теми
    // же рёбрами; отпустили — окончательный расчёт обычной точности; Enter — один шаг отмены
    UserPrefs.Current.PreviewMode = "full";
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    var part = s.Part!;
    BuildNow(s);
    int f0 = part.Features.Count;
    var edge = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3);
    vp.Selection.Select(new PickRef(edge.Id, PickKind.Edge, part, null), additive: false);
    Dispatcher.UIThread.RunJobs();
    vp.SimulateHandleDrag(-3, end: false);
    Require(w.CurrentOperation is RoundOp && part.Features.Count == f0 + 1, "live round not added while dragging");
    Require(ReferenceEquals(s.Document.PreviewPart, part), "preview part not set");
    BuildNow(s);
    Require(part.Features[^1].Error == null && part.Bodies.Count == 1, "live round failed: " + part.Features[^1].Error);
    int triPreview = part.Mesh?.Indices.Length ?? 0;
    vp.SimulateHandleDrag(4, end: false);
    Require(w.CurrentOperation is ChamferOp && part.Features.Count == f0 + 1 && part.Features[^1] is ChamferFeature, "outward did not switch to chamfer");
    BuildNow(s);
    var ch = (ChamferFeature)part.Features[^1];
    Require(ch.Error == null && ch.Edges.Count == 1 && Math.Abs(ch.D1.Value - 4) < 1e-9, $"live chamfer: edges {ch.Edges.Count}, error {ch.Error}");
    vp.SimulateHandleDrag(-7, end: false);
    BuildNow(s);
    var rf = (RoundFeature)part.Features[^1];
    Require(rf.Error == null && rf.Edges.Count == 1, "switch back to round lost the edge");
    vp.SimulateHandleDrag(-7, end: true);
    Require(s.Document.PreviewPart == null, "preview part left after release");
    BuildNow(s);
    Require(rf.Error == null && Math.Abs(rf.Radius.Value - 7) < 1e-9, "final round");
    int triFinal = part.Mesh?.Indices.Length ?? 0;
    Console.WriteLine($"  preview tris {triPreview / 3}, final tris {triFinal / 3}");
    w.CommitOperation();
    Require(part.Features.Count == f0 + 1 && s.UndoNames.Count == 1, $"commit: features {part.Features.Count - f0}, undo {s.UndoNames.Count}");
    w.Close();
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "file")
Check("BENCH-FILE", () =>
{
    var (part, pars) = DocumentStorage.Load(Environment.GetEnvironmentVariable("P3D_FILE")!);
    part.Rebuild(pars);
    Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
    Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => { if (!m.StartsWith("face ")) Console.WriteLine("       direct: " + (m.Length > 500 ? m[..500] : m)); } : null;
    part.Invalidate();
    var sw = System.Diagnostics.Stopwatch.StartNew(); part.Rebuild(pars);
    Console.WriteLine($"  full rebuild {sw.Elapsed.TotalMilliseconds:0} ms");
    foreach (var f in part.Features) Console.WriteLine($"   {f.Name,-24} {part.LastFeatureMs.GetValueOrDefault(f),8:0} ms  {f.GetType().Name} {f.Error}");
    Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(30)));
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "3")
Check("BENCH-CHAIN", () =>
{
    // скругление цепочки «прямое — дуга прежнего скругления — прямое» на кубе (снимок пользователя)
    var part = DocumentSession.Sample().Part!;
    part.Rebuild(null);
    var e1 = part.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5);
    var r1 = new RoundFeature { Radius = new ValueRef(double.Parse(Environment.GetEnvironmentVariable("P3D_R1") ?? "40", System.Globalization.CultureInfo.InvariantCulture)) };
    r1.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e1));
    part.AddFeature(r1); part.Rebuild(null);
    Console.WriteLine($"  first round: faces {part.Bodies.Sum(b => b.Faces.Count())} err {r1.Error}");
    // ребро, касательно продолжающееся через дугу скругления
    var roundFaceIds = r1.CreatedFaceIds.ToHashSet();
    Parametric3D.Kernel.Topology.TEdge? start = null; List<Parametric3D.Kernel.Topology.TEdge>? chain = null;
    foreach (var e in part.Bodies.SelectMany(b => b.Edges))
    {
        if (e.Curve is not Parametric3D.Kernel.Curves.LineCurve3) continue;
        var ch = Parametric3D.App.Operations.Topo.TangentChain(e);
        if (ch.Count >= 3) { start = e; chain = ch; break; }
    }
    if (chain == null) { Console.WriteLine("  no chain"); return; }
    Console.WriteLine($"  chain {chain.Count}: {string.Join(",", chain.Select(c => c.Curve?.GetType().Name))}");
    var rf = new RoundFeature { Radius = new ValueRef(5) };
    foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c));
    part.AddFeature(rf); part.Rebuild(null);
    foreach (var r in new[] { 5.0, 10, 20, 30, 40, 5, 20 })
    {
        rf.Radius = new ValueRef(r); part.NotifyChanged(rf);
        var st = new Dictionary<string, double>(); string last = "start"; long lt = System.Diagnostics.Stopwatch.GetTimestamp();
        void Hook(string sname) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); st[last] = st.GetValueOrDefault(last) + System.Diagnostics.Stopwatch.GetElapsedTime(lt, n).TotalMilliseconds; last = sname; lt = n; }
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = Environment.GetEnvironmentVariable("P3D_PROF") == "1" && r == 20; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        Parametric3D.Kernel.Tess.UvMesher.Debug = Environment.GetEnvironmentVariable("P3D_UVDBG") == "1" ? m => Console.WriteLine("       uv: " + m) : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); Part.BuildResult res;
        double cf = double.Parse(Environment.GetEnvironmentVariable("P3D_COARSE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var ks = Parametric3D.Kernel.KernelSettings.Capture();
        if (cf > 0) ks = ks with { Preview = true, Chord = ks.Chord * cf, ChordAngle = Math.Min(ks.ChordAngle * cf, 0.6) };
        using (new Parametric3D.Kernel.KernelContext(ks).WithStageHook(Hook).Enter()) res = part.Compute(null);
        Hook("end"); double t = sw.Elapsed.TotalMilliseconds; part.Apply(res);
        Console.WriteLine($"  R {r}: {t:0} ms faces {part.Bodies.Sum(b => b.Faces.Count())} tris {part.Mesh?.Indices.Length / 3} err {rf.Error} | " + string.Join(", ", st.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key}={k.Value:0}")));
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(25)));
        Console.WriteLine("     tris/face: " + string.Join(" ", part.Bodies.SelectMany(b => b.Faces).Select(f => $"{Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name.Replace("Surface","")}:{(part.Mesh!.FaceRanges.TryGetValue(f.Id, out var fr) ? fr.IndexCount / 3 : -1)}")));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "2")
Check("BENCH-SWEEP", () =>
{
    var part = Environment.GetEnvironmentVariable("P3D_BENCHPART") == "r" ? Parametric3D.App.Diagnostics.TestParts.Realistic(1, 40) : DocumentSession.Sample().Part!;
    part.Rebuild(null);
    var edge = part.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5);
    var rf = new RoundFeature { Radius = new ValueRef(1) };
    foreach (var ce in Environment.GetEnvironmentVariable("P3D_CHAIN") == "1" ? Parametric3D.App.Operations.Topo.TangentChain(edge) : new() { edge }) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ce));
    part.AddFeature(rf); part.Rebuild(null);
    foreach (var r in (Environment.GetEnvironmentVariable("P3D_BENCHR") is { } rr ? rr.Split(",").Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : new[] { 1.0, 1.2, 1.4, 1.6, 2, 3, 5, 8, 12, 20, 1.0, 1.4 }))
    {
        rf.Radius = new ValueRef(r); part.NotifyChanged(rf);
        var st = new Dictionary<string, double>(); string last = "start"; long lt = System.Diagnostics.Stopwatch.GetTimestamp();
        void Hook(string sname) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); st[last] = st.GetValueOrDefault(last) + System.Diagnostics.Stopwatch.GetElapsedTime(lt, n).TotalMilliseconds; last = sname; lt = n; }
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = r is 1.6 && Environment.GetEnvironmentVariable("P3D_PROF") == "1"; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => Console.WriteLine("       direct: " + (m.Length > 600 ? m[..600] : m)) : null;

        var sw = System.Diagnostics.Stopwatch.StartNew(); Part.BuildResult res;
        double cf = double.Parse(Environment.GetEnvironmentVariable("P3D_COARSE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var ks = Parametric3D.Kernel.KernelSettings.Capture();
        if (cf > 0) ks = ks with { Preview = true, LivePreview = true, Chord = ks.Chord * cf };
        using (new Parametric3D.Kernel.KernelContext(ks).WithStageHook(Hook).Enter()) res = part.Compute(null);
        Hook("end");
        double t = sw.Elapsed.TotalMilliseconds; part.Apply(res);
        Console.WriteLine("     " + string.Join(", ", st.OrderByDescending(k => k.Value).Take(5).Select(k => $"{k.Key}={k.Value:0}")));
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
        if (part.Bodies.Sum(b => b.Faces.Count()) < 20) Console.WriteLine("     tris/face: " + string.Join(" ", part.Bodies.SelectMany(b => b.Faces).Select(f => $"{f.Surface?.GetType().Name.Replace("Surface","")}:{(part.Mesh!.FaceRanges.TryGetValue(f.Id, out var fr) ? fr.IndexCount / 3 : -1)}")));
        Console.WriteLine($"  R {r}: {t:0} ms (feature {part.LastFeatureMs.GetValueOrDefault(rf):0}) faces {part.Bodies.Sum(b => b.Faces.Count())} tris {part.Mesh?.Indices.Length / 3} err {rf.Error}");
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "1")
Check("BENCH-DRAG", () =>
{
    foreach (var (name, part) in new[] { ("sample", DocumentSession.Sample().Part!), ("realistic40", Parametric3D.App.Diagnostics.TestParts.Realistic(1, 40)) })
    {
        part.Rebuild(null);
        var s = DocumentSession.FromPart(part, name);
        var edge = part.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5);
        var rf = new RoundFeature { Radius = new ValueRef(1) };
        rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge));
        part.AddFeature(rf);
        part.Rebuild(null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var full = new List<double>(); var prev = new List<double>(); var apply = new List<double>(); var scene = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            rf.Radius = new ValueRef(1 + 0.1 * i); part.NotifyChanged(rf);
            sw.Restart(); var r = part.Compute(null); full.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart(); part.Apply(r); apply.Add(sw.Elapsed.TotalMilliseconds);
            rf.Radius = new ValueRef(1.05 + 0.1 * i); part.NotifyChanged(rf);
            sw.Restart();
            using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { Preview = true }).Enter()) { var r2 = part.Compute(null); part.Apply(r2); }
            prev.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart(); Parametric3D.Render.Engine.SceneBuilder.Build(s.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions()); scene.Add(sw.Elapsed.TotalMilliseconds);
        }
        string M(List<double> l) => string.Join(" ", l.Select(x => x.ToString("0")));
        Console.WriteLine($"  {name}: faces {part.Bodies.Sum(b => b.Faces.Count())}; full [{M(full)}] apply [{M(apply)}] preview [{M(prev)}] scene [{M(scene)}] feature {part.LastFeatureMs.GetValueOrDefault(rf):0} err {rf.Error} faces {string.Join(",", part.LastFeatureMs.OrderByDescending(k => k.Value).Take(3).Select(k => k.Key.Name + "=" + k.Value.ToString("0")))}");
    }
});

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed }));
return failed == 0 ? 0 : 1;

sealed class CheckFailed : Exception { public CheckFailed(string m) : base(m) { } }
