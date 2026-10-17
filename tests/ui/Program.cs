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

// u13: эскиз на плоскости с рисованием инструментами (для проверок операций по эскизам)
Sketch DrawSketch(MainWindow w, Part part, ModelItem plane, Action<Sketch> draw)
{
    w.ViewPort.Selection.Clear();
    w.SelectItem(plane);
    w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch mode did not start");
    var sk = w.ViewPort.ActiveSketch!;
    draw(sk);
    w.FinishSketch();
    w.ViewPort.Selection.Clear();
    return sk;
}
void Rect(Sketch sk, double x0, double y0, double x1, double y1) { var r = new RectangleTool(); r.Click(new SketchInput(sk, new Vector2d(x0, y0), null)); r.Click(new SketchInput(sk, new Vector2d(x1, y1), null)); }
void Select(MainWindow w, PickRef r) => w.ViewPort.Selection.Select(r, additive: true);

// u13: переход по сечениям (квадраты 20 и 10 на высоте 40 — усечённая пирамида) и протяжка (круг R5 по отрезку 50)
Check("UI-U13-LOFT-SWEEP", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var a = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, -10, -10, 10, 10));
    var p40 = new DatumPlane("P40", Parametric3D.Kernel.Frame3.FromNormal(new Vector3d(0, 0, 40), Vector3d.UnitZ)); part.Add(p40);
    var b = DrawSketch(w, part, p40, sk => Rect(sk, -5, -5, 5, 5));
    Select(w, new PickRef(a.Id, PickKind.SketchCurve, part, a)); Select(w, new PickRef(b.Id, PickKind.SketchCurve, part, b));
    w.StartOperation(new LoftOp());
    w.CommitOperation(); s.Document.RebuildDirty();
    var loft = part.Features.OfType<LoftFeature>().Single();
    double want = 40.0 / 3 * (400 + 100 + 200);
    Console.WriteLine($"  loft: error {loft.Error}, volume {part.Volume:F4} want {want:F4}");
    Require(loft.Error == null && Math.Abs(part.Volume - want) < 1e-6 * want, $"loft volume {part.Volume} ({loft.Error})");
    w.Close();

    var s2 = DocumentSession.NewPart(); var w2 = Open(s2); var p2 = s2.Part!;
    var sec = DrawSketch(w2, p2, p2.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(5, 0), null)); });
    var path = DrawSketch(w2, p2, p2.PlaneXZ, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, Vector2d.Zero, null)); l.Click(new SketchInput(sk, new Vector2d(0, 50), null)); });
    Select(w2, new PickRef(sec.Id, PickKind.SketchCurve, p2, sec)); Select(w2, new PickRef(path.Id, PickKind.SketchCurve, p2, path));
    w2.StartOperation(new SweepOp());
    w2.CommitOperation(); s2.Document.RebuildDirty();
    var sw = p2.Features.OfType<SweepFeature>().Single();
    double want2 = Math.PI * 25 * 50;
    Console.WriteLine($"  sweep: error {sw.Error}, volume {p2.Volume:F4} want {want2:F4}");
    Require(sw.Error == null && Math.Abs(p2.Volume - want2) < 1e-3 * want2, $"sweep volume {p2.Volume} ({sw.Error})");
    w2.Close();
});

// u13: опорные элементы кнопок (плоскость посередине и касательная, ось грани, точка в центре дуги) и уклон
Check("UI-U13-DATUMS-DRAFT", () =>
{
    var s = DocumentSession.Sample(); var w = Open(s); var part = s.Part!; part.Rebuild(null);
    var fx = part.Bodies[0].Faces.Where(f => Refs.FacePlane(part, f.Id) is { } pl && Math.Abs(Math.Abs(pl.N.X) - 1) < 1e-9).ToList();
    Require(fx.Count == 2, "two x faces");
    w.ViewPort.Selection.Clear(); foreach (var f in fx) Select(w, new PickRef(f.Id, PickKind.Face, part, null));
    int n0 = part.Planes.Count();
    w.StartOperation(new PlaneOp { InitialMethod = 6 }); w.CommitOperation();
    var mid = part.Planes.Last();
    Console.WriteLine($"  mid plane: {mid.Frame.Origin} n {mid.Frame.W}");
    Require(part.Planes.Count() == n0 + 1 && Math.Abs(mid.Frame.Origin.X) < 1e-9 && Math.Abs(Math.Abs(mid.Frame.W.X) - 1) < 1e-9, "mid plane");
    // уклон 5° боковой грани +x от нижней
    double v0 = part.Volume;
    var bottom = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z < -0.99);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(fx[0].Id, PickKind.Face, part, null));
    var dop = new DraftOp(); w.StartOperation(dop);
    var f0 = part.Features.OfType<DraftFeature>().LastOrDefault();
    // нейтральная плоскость — нижняя грань (поле второе)
    w.ViewPort.Selection.Select(new PickRef(bottom.Id, PickKind.Face, part, null), additive: true);
    w.CommitOperation(); s.Document.RebuildDirty();
    var dr = part.Features.OfType<DraftFeature>().Single();
    Console.WriteLine($"  draft: error {dr.Error}, volume {v0:F2} -> {part.Volume:F2}");
    Require(dr.Error == null && Math.Abs(part.Volume - v0) > 1, $"draft {dr.Error}");
    w.Close();

    // ось грани и точка в центре дуги — у скругления
    var s2 = DocumentSession.Sample(); var w2 = Open(s2); var p2 = s2.Part!; p2.Rebuild(null);
    double zMax = p2.Bodies[0].Vertices.Max(v => v.P.Z);
    var e = p2.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - zMax) < 1e-6 && Math.Abs(x.V1.P.Z - zMax) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(10) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e)); p2.AddFeature(rf); p2.Rebuild(null);
    var cyl = p2.Bodies[0].Faces.First(f => f.Tag.Contains("round:face"));
    w2.ViewPort.Selection.Clear(); Select(w2, new PickRef(cyl.Id, PickKind.Face, p2, null));
    w2.StartOperation(new AxisOp { InitialMethod = 3 }); w2.CommitOperation();
    var ax = p2.Children.OfType<DatumAxis>().Last();
    var ed = (e.V1.P - e.V0.P).Normalized();
    Console.WriteLine($"  face axis: {ax.Origin} dir {ax.Direction}");
    Require(Math.Abs(Math.Abs(Vector3d.Dot(ax.Direction.Normalized(), ed)) - 1) < 1e-9, "face axis direction");
    w2.ViewPort.Selection.Clear(); Select(w2, new PickRef(cyl.Id, PickKind.Face, p2, null));
    w2.StartOperation(new PlaneOp { InitialMethod = 7 }); w2.CommitOperation();
    var tp = p2.Planes.Last(); var rad = tp.Frame.Origin - ax.Origin; rad -= ax.Direction.Normalized() * Vector3d.Dot(rad, ax.Direction.Normalized());
    Console.WriteLine($"  tangent plane at {tp.Frame.Origin}, distance from axis {rad.Length:F6}");
    Require(Math.Abs(rad.Length - 10) < 1e-6 && Math.Abs(Vector3d.Dot(tp.Frame.W.Normalized(), rad.Normalized())) > 1 - 1e-9, "tangent plane");
    var arc = p2.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3);
    w2.ViewPort.Selection.Clear(); Select(w2, new PickRef(arc.Id, PickKind.Edge, p2, null));
    w2.StartOperation(new PointOp { InitialMethod = 1 }); w2.CommitOperation();
    var pt = p2.Children.OfType<DatumPoint>().Last();
    Console.WriteLine($"  arc centre point {pt.P} (arc centre {((Parametric3D.Kernel.Curves.ArcCurve3)arc.Curve).Frame.Origin})");
    Require((pt.P - ((Parametric3D.Kernel.Curves.ArcCurve3)arc.Curve).Frame.Origin).Length < 1e-9, "arc centre");
    w2.Close();
});

// u13: команды дерева — вставить здесь, переупорядочить (с отказом, если ломается), вернуть подавленную, размеры операции
Check("UI-U13-TREE", () =>
{
    var s = DocumentSession.Sample(); var w = Open(s); var part = s.Part!; part.Rebuild(null);
    double zMax = part.Bodies[0].Vertices.Max(v => v.P.Z);
    var es = part.Bodies[0].Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - zMax) < 1e-6 && Math.Abs(x.V1.P.Z - zMax) < 1e-6).ToList();
    es = new List<Parametric3D.Kernel.Topology.TEdge> { es[0], es.First(x => x.V0 != es[0].V0 && x.V0 != es[0].V1 && x.V1 != es[0].V0 && x.V1 != es[0].V1) };   // противоположные рёбра
    var r1 = new RoundFeature { Radius = new ValueRef(10), Name = "R10" }; r1.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(es[0])); part.AddFeature(r1);
    var c1 = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(5), D2 = new ValueRef(5), Name = "C5" }; c1.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(es[1])); part.AddFeature(c1);
    s.Document.RebuildDirty();
    double vAll = part.Volume;
    // вставить здесь после скругления: фаска вне расчёта
    w.SelectItem(r1); TreeCommands.InsertHere(w); s.Document.RebuildDirty();
    Require(part.InInsertMode && part.IsBeyondStage(c1) && !part.IsBeyondStage(r1), "insert here");
    Require(TreeCommands.NeedsAttention(part).Any(x => x.F == c1), "needs attention lists the rolled-back feature");
    w.SelectItem(r1); TreeCommands.InsertHere(w); s.Document.RebuildDirty();
    Require(!part.InInsertMode && Math.Abs(part.Volume - vAll) < 1e-6, "insert mode ended");
    // переупорядочить: фаска выше скругления (независимы) — объём тот же
    w.SelectItem(c1); TreeCommands.Reorder(w, -1); s.Document.RebuildDirty();
    Require(part.Features.IndexOf(c1) < part.Features.IndexOf(r1) && Math.Abs(part.Volume - vAll) < 1e-6, $"reorder: volume {part.Volume} vs {vAll}");
    // подавить и вернуть
    w.Session.Edit("s", () => r1.Suppressed = true); s.Document.RebuildDirty();
    w.ViewPort.Selection.Clear(); TreeCommands.Resume(w); s.Document.RebuildDirty();
    Require(!r1.Suppressed && Math.Abs(part.Volume - vAll) < 1e-6, "resume");
    // размеры операции: радиус скругления меняется через список чисел
    var vals = TreeCommands.Values(r1);
    Require(vals.Any(v => v.Name == "Radius"), "round values");
    w.Session.Edit("r", () => { vals.First(v => v.Name == "Radius").Set(new ValueRef(12)); part.NotifyChanged(r1); }); s.Document.RebuildDirty();
    Require(Math.Abs(r1.Radius.Value - 12) < 1e-12 && part.Volume < vAll, "edit value");
    TreeCommands.Rebuild(w);
    Require(part.Features.All(f => f.Error == null), "rebuild");
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

Check("UI-HOLE-ON-FACE", () =>
{
    // п. 3 (u10): отверстие на выбранной грани — одно отверстие в середине грани, без ошибки
    foreach (bool before in new[] { true, false })
    {
        var s = DocumentSession.Sample();
        var w = Open(s);
        var vp = w.ViewPort;
        var part = s.Part!;
        BuildNow(s);
        int f0 = part.Features.Count;
        double v0 = part.Volume;
        var top = part.Bodies[0].Faces.First(f => Parametric3D.App.Operations.Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
        var pick = new PickRef(top.Id, PickKind.Face, part, null);
        if (before) vp.Selection.Select(pick, additive: false);
        w.StartOperation(new HoleOp());
        if (!before)
        {
            Require(part.Features.Count == f0, "the hole was added before its entry face was chosen");
            vp.Selection.Select(pick, additive: false); Dispatcher.UIThread.RunJobs();
        }
        BuildNow(s);
        var h = part.Features[^1] as HoleFeature;
        Console.WriteLine($"  before={before}: features +{part.Features.Count - f0}, points {h?.Points.Count}, error {h?.Error}, dir {h?.Direction}, volume {v0:0} -> {part.Volume:0}");
        Require(h != null && h.Error == null && h.Points.Count == 1 && part.Volume < v0 - 1, $"hole failed (before={before}): {h?.Error}");
        Require(Math.Abs(h!.Points[0].X) < 1e-6 && Math.Abs(h.Points[0].Y) < 1e-6, $"hole not in the middle of the face: {h.Points[0]}");
        w.CommitOperation();
        w.Close();
    }
});

Check("UI-SHELL-HANDLE", () =>
{
    // п. 1 (u10): у оболочки ручка — внутрь стенка внутрь (толщина +), наружу — стенка наружу (толщина −); одна операция
    UserPrefs.Current.PreviewMode = "full";
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    var part = s.Part!;
    BuildNow(s);
    var top = part.Bodies[0].Faces.First(f => Parametric3D.App.Operations.Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    vp.Selection.Select(new PickRef(top.Id, PickKind.Face, part, null), additive: false);
    Dispatcher.UIThread.RunJobs();
    w.StartOperation(new ShellOp());
    Dispatcher.UIThread.RunJobs();
    Require(vp.Handle != null, "shell started by command has no handle");
    var op = w.CurrentOperation;
    vp.SimulateHandleDrag(-5, end: false);
    BuildNow(s);
    var sf = (ShellFeature)part.Features[^1];
    Require(ReferenceEquals(op, w.CurrentOperation) && Math.Abs(sf.Thickness.Value - 5) < 1e-9 && sf.Error == null, $"inward: thickness {sf.Thickness.Value}, error {sf.Error}");
    double vIn = part.Volume;
    vp.SimulateHandleDrag(3, end: true);
    BuildNow(s);
    Require(ReferenceEquals(op, w.CurrentOperation) && Math.Abs(sf.Thickness.Value + 3) < 1e-9 && sf.Error == null, $"outward: thickness {sf.Thickness.Value}, error {sf.Error}");
    Console.WriteLine($"  shell volumes: inward 5 mm {vIn:0}, outward 3 mm {part.Volume:0}");
    Require(part.Volume > 100000 && Math.Abs(part.Volume - vIn) > 1000, "outward shell volume");
    w.CommitOperation();
    w.Close();
});

// u12 (замечания 1, 2): переменный радиус и переменная фаска на цепочке «ребро — дуга R30 — ребро» — плавно по всей длине,
// точная лента (закон по длине цепочки), без резца и гранения
Check("UI-VAR-CHAIN", () =>
{
    foreach (bool ch in new[] { false, true })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var r30 = new RoundFeature { Radius = new ValueRef(30), Name = "R30" }; r30.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(r30); p.Rebuild(null);
        var arc = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 30) < 1e-6);
        var chain = Parametric3D.App.Operations.Topo.TangentChain(arc, Parametric3D.App.Operations.Topo.AllEdges(p));
        if (chain.Count != 3) throw new Exception($"chain of {chain.Count}");
        Feature f;
        if (ch) { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(10), D2 = new ValueRef(10), SizeEnd = new ValueRef(20) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
        else { var rf = new RoundFeature { Radius = new ValueRef(10), RadiusEnd = new ValueRef(20) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
        p.AddFeature(f); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        if (f.Error != null) throw new Exception((ch ? "chamfer: " : "round: ") + f.Error);
        string want = ch ? "direct variable chamfer" : "radius law";
        if (!p.Warnings.Any(w => w.Contains(want))) throw new Exception("not the exact path: " + string.Join(" | ", p.Warnings));
        if (p.Warnings.Any(w => w.Contains("Geometry downgrade"))) throw new Exception("faceted faces: " + string.Join(" | ", p.Warnings));
        Console.WriteLine($"  variable {(ch ? "chamfer" : "round")} 10→20 on line–arc–line: {sw.Elapsed.TotalMilliseconds:0} ms, faces {p.Bodies.Sum(b => b.Faces.Count())}");
    }
});

// u12 (замечание 8): оболочка с открытой лентой скругления (кривая грань): полость выходит сквозь ленту; закрытая оболочка
// куба со скруглением R20 и стенкой 2 — полость по сектору ленты (прежде весь цилиндр R18 отсекал полость: 116 000 вместо 878 000)
Check("UI-SHELL-OPEN-ROUND", () =>
{
    foreach (bool open in new[] { true, false })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var rf = new RoundFeature { Radius = new ValueRef(20) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        double v0 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        var face = p.Bodies.SelectMany(b => b.Faces).First(f => f.Tag.Contains("round:face"));
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(2) };
        if (open) { sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); }
        p.AddFeature(sh); p.Rebuild(null);
        if (sh.Error != null) throw new Exception($"open {open}: {sh.Error}");
        double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        Console.WriteLine($"  shell 2 (open round {open}): volume {v:F1} of {v0:F1}; {p.Warnings.LastOrDefault(w => w.StartsWith("Shell: wall"))}");
        if (!(v < 0.15 * v0)) throw new Exception($"open {open}: the cavity is too small (volume {v:F1} of {v0:F1})");
        if (open && !p.Warnings.Any(w => w.Contains("1 face(s) left open") || w.Contains("open"))) throw new Exception("the round face did not open");
    }
});

// u12 (замечание 11): кромка (касательный стык) — не ребро: края ленты скругления — кромки, рёбра куба — нет
Check("UI-SEAM-EDGES", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3);
    if (Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam(top)) throw new Exception("a cube edge taken for a seam");
    var rf = new RoundFeature { Radius = new ValueRef(20) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => f.Tag.Contains("round:face"));
    var seams = face.Edges.Where(Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam).Count();
    var all = p.Bodies.SelectMany(b => b.Edges).Count(Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam);
    if (seams != 2 || all != 2) throw new Exception($"seams on the round {seams}, in the body {all} (want 2 and 2)");
    Console.WriteLine($"  seams: {all} (the two lines along the round)");
});

// u12a (замечание по файлу Part2): куб 100, скругление R80, оболочка 2 (открыт торец), скругление R60 / фаска 40 внутреннего
// ребра у закрытого торца — прибавка не выходит сквозь тонкую стенку (прежде снаружи на скруглении R80 торчал уголок)
Check("UI-SHELL-INNER-BLEND", () =>
{
    foreach (bool ch in new[] { false, true })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
        var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
        var rf = new RoundFeature { Radius = new ValueRef(80) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        var cyl = p.Bodies.SelectMany(b => b.Faces).Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)) as Parametric3D.Kernel.Geometry.Ssi.Cylinder).First(c => c != null)!;
        var roundPts = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Cylinder)
            .Edges.SelectMany(e => new[] { e.V0!.P, e.V1!.P }).ToList();
        double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
        var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(2) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
        var ie = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.X + 48) < 1e-6 && Math.Abs(e.V1!.P.X + 48) < 1e-6 && Math.Abs(e.V0.P.Z + 48) < 1e-6 && Math.Abs(e.V1.P.Z + 48) < 1e-6);
        Feature f2 = ch ? new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(40), D2 = new ValueRef(40) } : new RoundFeature { Radius = new ValueRef(60) };
        if (f2 is ChamferFeature cf2) cf2.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ie)); else ((RoundFeature)f2).Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ie));
        p.AddFeature(f2); p.Rebuild(null);
        if (f2.Error != null) throw new Exception((ch ? "chamfer: " : "round: ") + f2.Error);
        // ни одна вершина тела не выходит за наружную стенку скругления R80
        var ax = cyl.Axis.Normalized(); double worst = 0;
        foreach (var v in p.Bodies.SelectMany(b => b.Vertices))
        {
            var d = v.P - cyl.P; var rad = d - ax * Vector3d.Dot(d, ax);
            if (rad.Length > cyl.R + 1e-6) worst = Math.Max(worst, rad.Length - cyl.R);
        }
        // вершины за цилиндром законны только в стороне от скругления (в углу куба нет материала) — проверка по сектору ленты
        // угол скругления — габарит его ленты поперёк оси (до оболочки): за цилиндром внутри этого угла материала быть не должно
        var rb = Parametric3D.Kernel.Bounds3.Empty;
        foreach (var q in roundPts) rb.Add(q);
        bool InCorner(Vector3d q) { for (int k = 0; k < 3; k++) { if (Math.Abs(ax[k]) > 0.5) continue; if (q[k] <= rb.Min[k] + 1e-3 || q[k] >= rb.Max[k] - 1e-3) return false; } return true; }
        var outside = p.Bodies.SelectMany(b => b.Vertices).Where(v => { var d = v.P - cyl.P; var rad = d - ax * Vector3d.Dot(d, ax); return rad.Length > cyl.R + 1e-6 && InCorner(v.P); }).ToList();
        Console.WriteLine($"  inner {(ch ? "chamfer 40" : "round R60")} after shell: vertices beyond the R80 wall {outside.Count}");
        if (outside.Count > 0) throw new Exception($"material beyond the outer round wall at {outside[0].P}");
    }
});

// u12b (файл пользователя Part3): куб 100, скругление R40, фаска 15 на цепочке с дугой, оболочка НАРУЖУ на грани — толщина
// больше 3.65 отказывала: образующая смещённого конуса фаски не продлевалась, интеграл объёма грани шёл по чужой области
Check("UI-SHELL-OUT-CONE", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "data", "shell_out_cone.p3d");
    foreach (double t in new[] { -3.66, -8.0, -16.0 })
    {
        var (part, pars) = DocumentStorage.Load(path);
        var sh = part.Features.OfType<ShellFeature>().Single(); sh.Thickness = new ValueRef(t);
        part.Invalidate(); part.Rebuild(pars);
        var w = part.Warnings.FirstOrDefault(x => x.StartsWith("Shell")) ?? "";
        Console.WriteLine($"  outward shell {t}: {(sh.Error ?? w)}");
        if (sh.Error != null || !w.Contains("exact")) throw new Exception($"shell {t}: {sh.Error ?? w}");
    }
});

// u12b: оболочка внутрь толще скругления (R4 цепочкой «ребро — дуга R24 — ребро», стенка 5.31): смещённый тор и цилиндры
// исчезают — их полосы заостряются, полость — смещение острого тела. Проверка объёма: стенка тела со скруглением R4 = стенка
// без него минус снятый скруглением объём (полость у обоих одна и та же)
Check("UI-SHELL-SHARPEN", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "data", "shell_sharpen.p3d");
    double Vol(Part q) => q.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    foreach (double t in new[] { 5.31, 8.0 })
    {
        var (p1, pr1) = DocumentStorage.Load(path); var sh1 = p1.Features.OfType<ShellFeature>().Single(); sh1.Thickness = new ValueRef(t);
        p1.Invalidate(); p1.Rebuild(pr1);
        if (sh1.Error != null) throw new Exception($"shell {t}: {sh1.Error} {string.Join(" ", p1.Warnings.Where(w => w.StartsWith("Shell")))}");
        double wall = Vol(p1);
        var r4 = p1.Features.OfType<RoundFeature>().Single(r => Math.Abs(r.Radius.Value - 4) < 1e-9);
        p1.RemoveFeature(sh1); p1.Rebuild(pr1); double bodyR = Vol(p1);
        p1.RemoveFeature(r4); p1.Rebuild(pr1); double bodyS = Vol(p1);
        p1.AddFeature(sh1); p1.Rebuild(pr1); double wallS = Vol(p1);
        double want = wallS - (bodyS - bodyR);
        Console.WriteLine($"  shell {t} over R4: wall {wall:F4}, sharp wall {wallS:F4} − removed {bodyS - bodyR:F4} = {want:F4}");
        if (Math.Abs(wall - want) > 1e-6 * want) throw new Exception($"wall {wall} vs {want}");
    }
});

// u12b: куб 100, скругление R5 ребра x = 50, z = 50, стенка НАРУЖУ 2.82 с открытой гранью x = 50 (касательной к скруглению).
// Прежде: «ребро не кончается в смещённых вершинах» — точка касания уходила по открытой плоскости на нижний из двух корней.
// Объём стенки — по формуле: наружное тело (сечение 102.82 × 105.64 без угла у цилиндра R7.82) минус куб без угла R5
Check("UI-SHELL-OUT-TANGENT", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    var edge = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.X - xMax) < 1e-6 && Math.Abs(e.V1!.P.X - xMax) < 1e-6 && Math.Abs(e.V0.P.Z - zMax) < 1e-6 && Math.Abs(e.V1.P.Z - zMax) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(5) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); p.AddFeature(rf); p.Rebuild(null);
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
    var sh = new ShellFeature("Shell") { Thickness = new ValueRef(-2.82) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
    if (sh.Error != null) throw new Exception(sh.Error + " " + string.Join(" ", p.Warnings.Where(w => w.StartsWith("Shell"))));
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    // угол у цилиндра R7.82 (ось — 5 от краёв): прямоугольник 5 × 7.82 минус часть круга в нём
    double R = 7.82, inCircle = 0; int n = 200000;
    for (int i = 0; i < n; i++) { double x = (i + 0.5) / n * 5; inCircle += Math.Min(7.82, Math.Sqrt(Math.Max(R * R - x * x, 0))) * 5 / n; }
    double aOut = 102.82 * 105.64 - (5 * 7.82 - inCircle), aBody = 10000 - (25 - Math.PI * 25 / 4);
    double want = aOut * 105.64 - aBody * 100;
    Console.WriteLine($"  outward 2.82 at a tangent open face: volume {v:F4}, formula {want:F4}");
    if (Math.Abs(v - want) > 1e-3) throw new Exception($"volume {v} vs {want}");
});

// u12b: куб 100, скругление R20 одного нижнего ребра (y = 50, z = −50), оболочка 5 с открытым низом: открытая грань касается
// скругления по одному ребру и встречает боковины под углом по трём. Полость — тело, смещённое внутрь целиком, продлённое
// сквозь образ дна (у касания — стенка y = 30 вдоль нормали, у острых рёбер — продолжения смещённых боковин). Объём — по формуле
Check("UI-SHELL-OPEN-MIXED", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double yMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Y, e.V1!.P.Y));
    double zMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Z, e.V1!.P.Z));
    var edge = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Y - yMax) < 1e-6 && Math.Abs(e.V1!.P.Y - yMax) < 1e-6 && Math.Abs(e.V0.P.Z - zMin) < 1e-6 && Math.Abs(e.V1.P.Z - zMin) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(20) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); p.AddFeature(rf); p.Rebuild(null);
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && Math.Abs(pl.N.Z) > 0.99 && Math.Abs(pl.P.Z - zMin) < 1e-6);
    var sh = new ShellFeature("Shell") { Thickness = new ValueRef(5) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
    var w = p.Warnings.FirstOrDefault(x => x.StartsWith("Shell")) ?? "";
    if (sh.Error != null) throw new Exception(sh.Error + " " + w);
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    double body = 1e6 - 100 * (400 - Math.PI * 400 / 4);
    double inner = 90 * (8100 - (225 - Math.PI * 225 / 4)), plug = 90 * 75 * 5;
    double want = body - inner - plug;
    Console.WriteLine($"  shell 5, open bottom (one tangent edge): volume {v:F4}, formula {want:F4}; {w}");
    if (Math.Abs(v - want) > 1e-3) throw new Exception($"volume {v} vs {want}");
});

// u12 (замечание 15): зеркало ОПЕРАЦИИ — скругление верхнего ребра куба R20 отражается на противоположное ребро (прежде
// отражалось и объединялось тело: скругление заполнялось, деталь становилась кубом без ошибки)
Check("UI-MIRROR-OPS", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
    var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
    double v0 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    var rf = new RoundFeature { Radius = new ValueRef(20), Name = "R20" }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf);
    p.Rebuild(null); double v1 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    var bb = p.Bodies[0].Bounds; var c = (bb.Min + bb.Max) / 2;
    var mf = new MirrorFeature("Mirror") { At = c, Normal = Vector3d.UnitY }; mf.Sources.Add(rf.Key); p.AddFeature(mf);
    var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
    if (rf.Error != null || mf.Error != null) throw new Exception($"errors: {rf.Error} / {mf.Error}");
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN), want = v0 - 2 * (v0 - v1);
    if (!(Math.Abs(v - want) < 1e-6 * want)) throw new Exception($"volume {v:F3}, want {want:F3}");
    Console.WriteLine($"  mirror of a round: {sw.Elapsed.TotalMilliseconds:0} ms, volume {v:F3} (want {want:F3})");
});

// u12 (замечание 18): массив ОПЕРАЦИИ — вырез Ø10 в кубе со скруглением R30 повторяется 3 раза; прежде массив копировал всё
// тело (три смещённых куба объединялись, перестроение не кончалось)
Check("UI-PATTERN-OPS", () =>
{
    foreach (double step in new[] { 15.0, 5.0 })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var r30 = new RoundFeature { Radius = new ValueRef(30), Name = "R30" }; r30.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(r30);
        var sk = Parametric3D.App.Diagnostics.TestParts.CircleSketch(p, zMax, 10, 0, 5);
        var cut = new ExtrudeFeature("Cut") { SketchId = sk.Id, Cut = true }; cut.Side1.Distance = new ValueRef(20); cut.Side1.Flip = true; p.AddFeature(cut);
        var lp = new LinearPatternFeature("Pattern") { Step = new Vector3d(step, 0, 0), Count = new ValueRef(3) }; lp.Sources.Add(cut.Key); p.AddFeature(lp);
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null); double ms = sw.Elapsed.TotalMilliseconds;
        if (cut.Error != null || lp.Error != null) throw new Exception($"step {step}: errors {cut.Error} / {lp.Error}");
        double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        double want = 1e6 - (1 - Math.PI / 4) * 900 * 100 - 3 * Math.PI * 25 * 20;
        Console.WriteLine($"  pattern of a cut (step {step}): {ms:0} ms, volume {v:F3}{(step >= 10 ? $" (want {want:F3})" : "")}, bodies {p.Bodies.Count}");
        if (step >= 10 && !(Math.Abs(v - want) < 0.5)) throw new Exception($"volume {v:F3}, want {want:F3}");
        if (ms > 20000) throw new Exception($"step {step}: {ms:0} ms");
    }
});

// u11: открытое скругление вогнутого прямого ребра между плоскостями, концы — на наклонных плоских торцах: лента — точный
// цилиндр (а не таблица сечений), края на торцах — эллипсы; объём — как у пути сечений (315690.4877073967), время — доли секунды
Check("UI-OPEN-CYL", () =>
{
    var p = Parametric3D.App.Diagnostics.TestParts.Realistic(4, 30); p.Rebuild(null);
    var at = new Vector3d(71.68894768445239, 30.120455190595457, 10);
    var e0 = p.Bodies.SelectMany(b => b.Edges).OrderBy(e => Math.Min((e.V0!.P - at).Length, (e.V1!.P - at).Length)).First();
    var rf = new RoundFeature { Radius = new ValueRef(2.1) };
    foreach (var c in Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p))) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c));
    p.AddFeature(rf); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null); double ms = sw.Elapsed.TotalMilliseconds;
    if (rf.Error != null) throw new Exception("round failed: " + rf.Error);
    if (!p.Warnings.Any(w => w.Contains("direct blend"))) throw new Exception("not the direct route: " + string.Join(" | ", p.Warnings));
    var cyl = p.Bodies.SelectMany(b => b.Faces).Where(f => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.CylinderSurface cs && Math.Abs(cs.Radius - 2.1) < 1e-9).ToList();
    if (Environment.GetEnvironmentVariable("P3D_OPENCYL") != "0" && cyl.Count == 0) throw new Exception("blend is not an exact cylinder");
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    if (!(Math.Abs(v - 315690.4877073967) < 1e-6)) throw new Exception($"volume {v:R}");
    Console.WriteLine($"  open round R2.1: {ms:0} ms, cylinder faces {cyl.Count}, volume {v:F6}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellcurved")
Check("BENCH-SHELL-CURVED", () =>
{
    // u12 (замечание 8): оболочка с открытой кривой гранью (лента скругления R20)
    foreach (double t in new[] { 2.0, 5.0, -3.0 })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var rf = new RoundFeature { Radius = new ValueRef(20), Name = "R20" }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        var face = p.Bodies.SelectMany(b => b.Faces).First(f => f.Tag.Contains("round:face"));
        var c = Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value;
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(t) }; sh.OpenFaces.Add(c); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh);
        Parametric3D.Kernel.Ops.DirectRound.Debug = null;
        Parametric3D.Kernel.Ops.ShellOp.ShellDebug = Environment.GetEnvironmentVariable("P3D_SHDBG") == "1" ? m => Console.WriteLine("    sh: " + m) : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        Console.WriteLine($"  shell t {t}: {sw.Elapsed.TotalMilliseconds:0} ms error {sh.Error} volume {p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):F1}");
        foreach (var w in p.Warnings.TakeLast(3)) Console.WriteLine("    warn " + (w.Length > 250 ? w[..250] : w));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellout")
Check("BENCH-SHELL-OUT", () =>
{
    // замечание: куб 100, скругление ребра R40, фаска 15 на дуге скругления, оболочка наружу с открытой гранью
    foreach (double t in (Environment.GetEnvironmentVariable("P3D_T") ?? "-3.6,-3.67,-5").Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)))
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
        double xMin0 = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.X, e.V1!.P.X));
        var top = Environment.GetEnvironmentVariable("P3D_VERT") == "1"
            ? p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.X - xMin0) < 1e-6 && Math.Abs(e.V1!.P.X - xMin0) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6)
            : p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
        var rf = new RoundFeature { Radius = new ValueRef(40) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        double xMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.X, e.V1!.P.X));
        var arc = Environment.GetEnvironmentVariable("P3D_VERT") == "1"
            ? p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 40) < 1e-6 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6)
            : Environment.GetEnvironmentVariable("P3D_SEAM") == "1"
            ? p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - (yMin + 40)) < 1e-6)
            : p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 40) < 1e-6 && Math.Abs(e.V0!.P.X - xMin) < 1e-6);
        var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(15), D2 = new ValueRef(15) };
        if (Environment.GetEnvironmentVariable("P3D_CHAIN") == "1") foreach (var ce in Parametric3D.App.Operations.Topo.TangentChain(arc, Parametric3D.App.Operations.Topo.AllEdges(p))) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ce));
        else cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(arc));
        p.AddFeature(cf);
        if (Environment.GetEnvironmentVariable("P3D_CHDBG") == "1") Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (!m.StartsWith("face ")) Console.WriteLine("    chdr: " + (m.Length > 300 ? m[..300] : m)); };
        p.Rebuild(null); Parametric3D.Kernel.Ops.DirectRound.Debug = null;
        if (rf.Error != null || cf.Error != null) throw new Exception($"{rf.Error} / {cf.Error}");
        if (Environment.GetEnvironmentVariable("P3D_SAVE") is { } sv0) Parametric3D.Model.DocumentStorage.Save(sv0, p);
        foreach (var w in p.Warnings) Console.WriteLine("    pre " + (w.Length > 200 ? w[..200] : w));
        double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
        var face = Environment.GetEnvironmentVariable("P3D_VERT") == "1"
            ? p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.Y < -0.99)
            : p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(t) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh);
        Parametric3D.Kernel.Ops.ShellOp.ShellDebug = Environment.GetEnvironmentVariable("P3D_SHDBG") == "1" ? m => Console.WriteLine("    sh: " + m) : null;
        Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_SHDBG") == "1" ? m => { if (!m.StartsWith("face ")) Console.WriteLine("    dr: " + (m.Length > 300 ? m[..300] : m)); } : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        Console.WriteLine($"  shell t {t}: {sw.Elapsed.TotalMilliseconds:0} ms error {sh.Error} volume {p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):F1}");
        foreach (var w in p.Warnings.TakeLast(3)) Console.WriteLine("    warn " + (w.Length > 400 ? w[..400] : w));
        Parametric3D.Kernel.Ops.DirectRound.Debug = null; Parametric3D.Kernel.Ops.ShellOp.ShellDebug = null;
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellfuzz")
Check("BENCH-SHELL-FUZZ", () =>
{
    // u12b: «как пользователь» — куб 100, 1–3 скругления/фаски случайных рёбер (с касательной цепочкой), затем оболочка
    // случайной толщины внутрь или наружу с открытой гранью (плоской или кривой) или без
    var rnd = new Random(int.Parse(Environment.GetEnvironmentVariable("P3D_FUZZSEED") ?? "3"));
    int trials = int.Parse(Environment.GetEnvironmentVariable("P3D_TRIALS") ?? "60");
    var classes = new Dictionary<string, (int N, double Ms, string Ex)>();
    int fails = 0; double total = 0;
    for (int t = 0; t < trials; t++)
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        var log = new List<string>();
        int nOps = 1 + rnd.Next(3);
        bool preOk = true;
        for (int k = 0; k < nOps; k++)
        {
            var sharp = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve != null && Parametric3D.App.Operations.Topo.FacesOf(p, e).Count() == 2).Where(e =>
            {
                var fs = Parametric3D.App.Operations.Topo.FacesOf(p, e).ToList(); var c = e.Curve!; var mid = c.PointAt(0.5 * (c.T0 + c.T1));
                return Math.Abs(Vector3d.Dot(Parametric3D.App.Operations.Topo.FaceNormal(p, fs[0], mid), Parametric3D.App.Operations.Topo.FaceNormal(p, fs[1], mid))) < 0.995;
            }).ToList();
            if (sharp.Count == 0) break;
            var e0 = sharp[rnd.Next(sharp.Count)];
            var chain = Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p));
            bool round = rnd.NextDouble() < 0.6; double size = Math.Round(3 + rnd.NextDouble() * 40);
            Feature f;
            if (round) { var rf = new RoundFeature { Radius = new ValueRef(size) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
            else { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(size), D2 = new ValueRef(size) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
            p.AddFeature(f); p.Rebuild(null);
            if (f.Error != null) { p.RemoveFeature(f); p.Rebuild(null); continue; }
            log.Add($"{(round ? "R" : "C")}{size}x{chain.Count}@{e0.V0!.P.X:0},{e0.V0.P.Y:0},{e0.V0.P.Z:0}");
        }
        if (!preOk) continue;
        double v0 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        double th = Math.Round((0.5 + rnd.NextDouble() * 9.5) * (rnd.NextDouble() < 0.6 ? 1 : -1), 2);
        var faces = p.Bodies.SelectMany(b => b.Faces).ToList();
        int mode = rnd.Next(4); // 0 — без открытой, 1–2 — плоская, 3 — любая
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(th) };
        string open = "none";
        if (mode > 0)
        {
            var cand = mode < 3 ? faces.Where(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) != null).ToList() : faces;
            var fo = cand[rnd.Next(cand.Count)];
            if (Parametric3D.App.Operations.Refs.FacePoint(p, fo.Id) is { } fp) { sh.OpenFaces.Add(fp); sh.OpenFaceNames.Add(fo.Tag); open = (Parametric3D.App.Operations.Refs.FacePlane(p, fo.Id) != null ? "plane " : "curved ") + fo.Tag; }
        }
        // треугольники наружной поверхности до оболочки (без открытых граней) — для замера толщины стенки
        var outerTris = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var fo in p.Bodies.SelectMany(b => b.Faces))
        {
            if (fo.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface fsx) continue;
            bool isOpen = sh.OpenFaces.Any(q => fsx.Facets.ReadView.Any(tr => Parametric3D.Kernel.Ops.DirectRound.DistToTriangle(q, tr.Item1, tr.Item2, tr.Item3) < 1e-6));
            if (!isOpen || th < 0) foreach (var tr in fsx.Facets.ReadView) outerTris.Add(tr);
        }
        p.AddFeature(sh);
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null); double ms = sw.Elapsed.TotalMilliseconds; total += ms;
        string thick = "";
        if (sh.Error == null)
        {
            string tagW = th > 0 ? "shell:inner" : "shell:outer";
            // каждая вершина внутренней поверхности (грани shell:inner, кроме продлений) — в t от наружной
            double lo = double.MaxValue, hi = 0;
            foreach (var b in p.Bodies)
                foreach (var vx in b.Vertices)
                {
                    var fsV = b.Edges.Where(e => e.V0 == vx || e.V1 == vx).SelectMany(e => e.CoEdges.Select(c => c.Loop?.Face)).OfType<Parametric3D.Kernel.Topology.TFace>().Distinct().ToList();
                    if (fsV.Count == 0 || !fsV.All(f => f.Tag.StartsWith(tagW))) continue;
                    double dmin = outerTris.Min(tr => Parametric3D.Kernel.Ops.DirectRound.DistToTriangle(vx.P, tr.Item1, tr.Item2, tr.Item3));
                    lo = Math.Min(lo, dmin); hi = Math.Max(hi, dmin);
                }
            double at = Math.Abs(th), chord = 0.05 + 0.01 * at;
            if (lo != double.MaxValue && (lo < at - chord || hi > at + chord)) thick = $" WALL {lo:F3}..{hi:F3} (t {th})";
        }
        double v1 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        string w = p.Warnings.FirstOrDefault(x => x.StartsWith("Shell")) ?? "";
        string res = sh.Error != null ? "FAIL " + w : (th > 0 ? v1 < v0 : v1 > 0) ? (w.Contains("exact") ? "ok exact" : "ok " + w) : $"BAD VOLUME {v0:F0}->{v1:F0} " + w;
        res = System.Text.RegularExpressions.Regex.Replace(res, @"[-\d.]+(E[-+]\d+)?", "#"); if (res.Length > 200) res = res[..200];
        if (thick != "") res = "THICKNESS " + res;
        if (sh.Error != null) fails++;
        string key = $"{(th > 0 ? "in" : "out")} {(mode == 0 ? "closed" : open.StartsWith("plane") ? "plane" : "curved")}: {res}";
        var (n, sm, ex) = classes.GetValueOrDefault(key);
        string example = $"t {th} open {open} ops {string.Join(" ", log)}{thick}";
        classes[key] = (n + 1, sm + ms, ex ?? example);
        if (Environment.GetEnvironmentVariable("P3D_SHOWALL") == "1" || ((sh.Error != null || thick != "") && Environment.GetEnvironmentVariable("P3D_SHOWFAIL") == "1")) Console.WriteLine($"   #{t} {ms,6:0} ms {key}\n        {example}");
        if (sh.Error != null && Environment.GetEnvironmentVariable("P3D_SAVEDIR") is { } sd) { sh.Error = null; Parametric3D.Model.DocumentStorage.Save(Path.Combine(sd, $"shf{t}.p3d"), p); }
    }
    Console.WriteLine($"  trials {trials}: total {total:0} ms, fails {fails}");
    foreach (var kv in classes.OrderByDescending(x => x.Value.N)) Console.WriteLine($"   {kv.Value.N,4} {kv.Value.Ms,8:0} ms  {kv.Key}\n        e.g. {kv.Value.Ex}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellround")
Check("BENCH-SHELL-ROUND", () =>
{
    // замечание: куб 100, скругление ребра R80, оболочка 2 (открыт торец), скругление внутреннего ребра R60 — лишний уголок
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
    var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(80) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
    double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
    var sh = new ShellFeature("Shell") { Thickness = new ValueRef(2) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
    if (sh.Error != null) throw new Exception("shell: " + sh.Error);
    double v1 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    if (Environment.GetEnvironmentVariable("P3D_SAVESHELL") is { } svs) Parametric3D.Model.DocumentStorage.Save(svs, p);
    bool In48(Vector3d q) => Math.Abs(q.X) <= 48.0001 + (q.X > 0 ? 2 : 0) && Math.Abs(q.Y) <= 48.0001 && Math.Abs(q.Z) <= 48.0001;
    var inner = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && In48(e.V0!.P) && In48(e.V1!.P) && e.CoEdges.Count == 2).ToList();
    Console.WriteLine($"  shell volume {v1:F1}, inner straight edges {inner.Count}");
    if (Environment.GetEnvironmentVariable("P3D_EDGEIX") is { } eix) inner = new List<Parametric3D.Kernel.Topology.TEdge> { inner[int.Parse(eix)] };
    foreach (var ie in inner)
    {
        var ref0 = Parametric3D.Kernel.Topology.EdgeRef.Of(ie);
        double rr2 = double.Parse(Environment.GetEnvironmentVariable("P3D_R") ?? "60");
        var r2 = new RoundFeature { Radius = new ValueRef(rr2) }; r2.Edges.Add(ref0);
        Feature f2 = r2;
        if (Environment.GetEnvironmentVariable("P3D_CH") == "1") { var c2 = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(rr2), D2 = new ValueRef(rr2) }; c2.Edges.Add(ref0); f2 = c2; }
        p.AddFeature(f2);
        Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => { if (!m.StartsWith("face ")) Console.WriteLine("    dr: " + (m.Length > 300 ? m[..300] : m)); } : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        double v2 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        double L = (ie.V1!.P - ie.V0!.P).Length, R = r2.Radius.Value, want = v1 + (f2 is ChamferFeature ? 0.5 : 1 - Math.PI / 4) * R * R * L;
        var bb = p.Bodies[0].Bounds;
        Console.WriteLine($"  inner edge {ie.V0.P}->{ie.V1.P}: {sw.Elapsed.TotalMilliseconds:0} ms error {f2.Error} volume {v2:F1} want {want:F1} (Δ {v2 - want:F1}) bounds {bb.Min} {bb.Max} faces {p.Bodies.Sum(b => b.Faces.Count())}");
        foreach (var w in p.Warnings.TakeLast(2)) Console.WriteLine("    warn " + (w.Length > 250 ? w[..250] : w));
        if (Environment.GetEnvironmentVariable("P3D_FACES") == "1")
            foreach (var fc in p.Bodies.SelectMany(b => b.Faces))
            {
                var bbf = Parametric3D.Kernel.Bounds3.Empty; foreach (var ed in fc.Edges) { bbf.Add(ed.V0!.P); bbf.Add(ed.V1!.P); }
                Console.WriteLine($"    face {Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} {fc.Surface.GetType().Name} {fc.Tag} loops {fc.Loops.Count} edges {fc.Edges.Count()} bounds {bbf.Min} {bbf.Max}");
            }
        if (Environment.GetEnvironmentVariable("P3D_SAVE") is { } sv && f2.Error == null) Parametric3D.Model.DocumentStorage.Save(sv, p);
        p.RemoveFeature(f2); p.Rebuild(null);
        Parametric3D.Kernel.Ops.DirectRound.Debug = null;
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "var")
Check("BENCH-VAR", () =>
{
    // u12 (замечание 1): куб 100, R30 верхнего ребра, затем цепочка «ребро — дуга R30 — ребро» переменным радиусом 10 → 20
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
    var r30 = new RoundFeature { Radius = new ValueRef(30), Name = "R30" }; r30.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(r30); p.Rebuild(null);
    if (r30.Error != null) throw new Exception("R30: " + r30.Error);
    var arc = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 30) < 1e-6);
    var chain = Parametric3D.App.Operations.Topo.TangentChain(arc, Parametric3D.App.Operations.Topo.AllEdges(p));
    foreach (var e in chain) Console.WriteLine($"  chain {e.Curve?.GetType().Name} {e.V0!.P} -> {e.V1!.P}");
    double r0 = double.Parse(Environment.GetEnvironmentVariable("P3D_R0") ?? "10"), r1 = double.Parse(Environment.GetEnvironmentVariable("P3D_R1") ?? "20");
    Feature f;
    if (Environment.GetEnvironmentVariable("P3D_CH") == "1") { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(r0), D2 = new ValueRef(r0), SizeEnd = new ValueRef(r1) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
    else { var rf = new RoundFeature { Radius = new ValueRef(r0), RadiusEnd = new ValueRef(r1), Name = "var" }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
    Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined") || m.StartsWith("build declined")) Console.WriteLine("    direct: " + (m.Length > 300 ? m[..300] : m)); };
    Parametric3D.Kernel.Diagnostics.Prof.Enabled = Environment.GetEnvironmentVariable("P3D_PROF") == "1"; Parametric3D.Kernel.Diagnostics.Prof.Reset();
    if (Environment.GetEnvironmentVariable("P3D_UVDBG") == "1") Parametric3D.Kernel.Tess.UvMesher.Debug = m => Console.WriteLine("    uv: " + (m.Length > 300 ? m[..300] : m));
    p.AddFeature(f); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
    Console.WriteLine($"  var: {sw.Elapsed.TotalMilliseconds:0} ms error {f.Error}");
    if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(70)));
    foreach (var w in p.Warnings) Console.WriteLine("    warn " + (w.Length > 300 ? w[..300] : w));
    foreach (var fc in p.Bodies.SelectMany(b => b.Faces)) Console.WriteLine($"    face {Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} {fc.Surface.GetType().Name} {fc.Tag}");
    if (Environment.GetEnvironmentVariable("P3D_SAVE") is { } sv) Parametric3D.Model.DocumentStorage.Save(sv, p);
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shell")
Check("BENCH-SHELL", () =>
{
    foreach (string open in new[] { "top", "front", "none" })
    foreach (double t in new[] { 5.0, 9.9, 10, 12, 20, -5, -12 })
    {
        var part = DocumentSession.Sample().Part!;
        part.Rebuild(null);
        double zTop = part.Bodies[0].Vertices.Max(v => v.P.Z);
        var e1 = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - zTop) < 1e-6 && Math.Abs(e.V1.P.Z - zTop) < 1e-6 && Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-6);
        var rf = new RoundFeature { Radius = new ValueRef(10) };
        rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e1));
        part.AddFeature(rf); part.Rebuild(null);
        var sh = new ShellFeature { Thickness = new ValueRef(t) };
        if (open != "none")
        {
            var f = part.Bodies[0].Faces.First(ff => Parametric3D.App.Operations.Refs.FacePlane(part, ff.Id) is { } pl && (open == "top" ? pl.N.Z > 0.99 : pl.N.X > 0.99));
            sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(part, f.Id)!.Value);
        }
        part.AddFeature(sh);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        part.Rebuild(null);
        Console.WriteLine($"  open {open,-5} T {t,5}: {sw.Elapsed.TotalMilliseconds,6:0} ms volume {part.Volume,10:0} faces {part.Bodies.Sum(b => b.Faces.Count()),3} err {(sh.Error is { } er ? (er.Length > 200 ? er[..200] : er) : "")}");
        if (sh.Error != null) foreach (var wmsg in part.Warnings.Where(x => x.StartsWith("Shell"))) Console.WriteLine("       " + (wmsg.Length > 300 ? wmsg[..300] : wmsg));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "routes")
Check("BENCH-ROUTES", () =>
{
    // где обходной путь: операции дольше 250 мс на реалистичных деталях, причины отказа прямого построителя
    var reasons = new Dictionary<string, (int N, double Ms)>();
    var slowByType = new Dictionary<string, (int N, double Ms)>();
    int total = 0, slow = 0; double all = 0;
    string? last = null;
    Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined")) last = m; };
    int n0 = int.Parse(Environment.GetEnvironmentVariable("P3D_SEEDS") ?? "20");
    for (int seed = 1; seed <= n0; seed++)
    {
        var p = Parametric3D.App.Diagnostics.TestParts.Realistic(seed, 40);
        p.Invalidate();
        // по одной операции, чтобы ловить причину отказа каждой
        var feats = p.Features.ToList();
        p.StageIndex = 0; p.Rebuild(null);
        for (int i = 1; i <= feats.Count; i++)
        {
            last = null;
            p.StageIndex = i; p.Rebuild(null);
            var f = feats[i - 1];
            double ms = p.LastFeatureMs.GetValueOrDefault(f);
            total++; all += ms;
            string k = f.GetType().Name.Replace("Feature", "");
            if (ms > 250)
            {
                slow++;
                var (a1, b1) = slowByType.GetValueOrDefault(k); slowByType[k] = (a1 + 1, b1 + ms);
                string why = last == null ? "(no direct decline)" : System.Text.RegularExpressions.Regex.Replace(last, @"\(.*?\)|[-\d.]+(E[-+]\d+)?", "#");
                if (why.Length > 160) why = why[..160];
                var (a2, b2) = reasons.GetValueOrDefault(k + ": " + why); reasons[k + ": " + why] = (a2 + 1, b2 + ms);
            }
        }
    }
    Console.WriteLine($"  features {total}, total {all:0} ms, slow (>250 ms) {slow}");
    foreach (var kv in slowByType.OrderByDescending(x => x.Value.Ms)) Console.WriteLine($"   {kv.Key,-16} {kv.Value.N,4}  {kv.Value.Ms,8:0} ms");
    foreach (var kv in reasons.OrderByDescending(x => x.Value.Ms).Take(25)) Console.WriteLine($"   {kv.Value.N,4} {kv.Value.Ms,8:0} ms  {kv.Key}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "fuzz")
Check("BENCH-FUZZ", () =>
{
    // «как пользователь»: случайное острое ребро (с касательной цепочкой) — скругление или фаска случайного размера
    var rnd = new Random(int.Parse(Environment.GetEnvironmentVariable("P3D_FUZZSEED") ?? "7"));
    int trials = int.Parse(Environment.GetEnvironmentVariable("P3D_TRIALS") ?? "120");
    var classes = new Dictionary<string, (int N, double Ms, int Fail, string Ex)>();
    string? last = null;
    Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined")) last = m; };
    var parts = new List<Part>();
    for (int sd = 1; sd <= 6; sd++) { var pp = Parametric3D.App.Diagnostics.TestParts.Realistic(sd, 30); pp.Rebuild(null); parts.Add(pp); }
    { var cube = DocumentSession.Sample().Part!; cube.Rebuild(null); parts.Add(cube); }
    double sumMs = 0; int fails = 0, slowN = 0, mism = 0;
    for (int t = 0; t < trials; t++)
    {
        var p = parts[rnd.Next(parts.Count)];
        var sharp = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve != null && Parametric3D.App.Operations.Topo.FacesOf(p, e).Count() == 2).ToList();
        sharp = sharp.Where(e =>
        {
            var fs = Parametric3D.App.Operations.Topo.FacesOf(p, e).ToList();
            var c = e.Curve!; var mid = c.PointAt(0.5 * (c.T0 + c.T1));
            var n1 = Parametric3D.App.Operations.Topo.FaceNormal(p, fs[0], mid); var n2 = Parametric3D.App.Operations.Topo.FaceNormal(p, fs[1], mid);
            return Math.Abs(Vector3d.Dot(n1, n2)) < 0.995;
        }).ToList();
        if (sharp.Count == 0) continue;
        var e0 = sharp[rnd.Next(sharp.Count)];
        var chain = Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p));
        bool round = rnd.NextDouble() < 0.6;
        double size = Math.Round(0.5 + rnd.NextDouble() * (p.Bodies.Count > 0 && p.Name.Contains("cube") ? 30 : 4), 1);
        Feature f;
        if (round) { var rf = new RoundFeature { Radius = new ValueRef(size) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
        else { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(size), D2 = new ValueRef(size) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
        last = null;
        bool predicted = Parametric3D.App.Operations.Topo.EdgeGhostValid(p, chain.Select(c => c.Id), round)?.Invoke(size) ?? true;
        p.AddFeature(f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(null);
        double ms = sw.Elapsed.TotalMilliseconds;
        double fms = p.LastFeatureMs.GetValueOrDefault(f);
        string kinds = string.Join("+", chain.Select(c => c.Curve is Parametric3D.Kernel.Curves.LineCurve3 ? "L" : c.Curve is Parametric3D.Kernel.Curves.ArcCurve3 ? "A" : "C").Distinct().OrderBy(x => x));
        string why = f.Error != null ? "FAIL " + System.Text.RegularExpressions.Regex.Replace(f.Error, @"[-\d.]+(E[-+]\d+)?", "#")
                   : fms > 250 ? "SLOW " + (last == null ? "-" : System.Text.RegularExpressions.Regex.Replace(last, @"\(.*?\)|[-\d.]+(E[-+]\d+)?", "#")) : "ok";
        if (why.Length > 170) why = why[..170];
        string key = $"{(round ? "R" : "C")} {kinds}: {why}";
        var (n, sm, fl, ex) = classes.GetValueOrDefault(key);
        classes[key] = (n + 1, sm + fms, fl + (f.Error != null ? 1 : 0), ex ?? $"part {p.Name} size {size} edge {e0.V0?.P} -> {e0.V1?.P}");
        sumMs += fms; if (f.Error != null) fails++; if (fms > 250) slowN++;
        if (predicted != (f.Error == null)) { mism++; Console.WriteLine($"   MISMATCH predicted {(predicted ? "ok" : "fail")}, actual {(f.Error == null ? "ok" : "fail")}: {(round ? "R" : "C")}{size} {kinds} part {p.Name} edge {e0.V0?.P} -> {e0.V1?.P}"); }
        p.RemoveFeature(f); p.Rebuild(null);
    }
    Console.WriteLine($"  trials {trials}: total {sumMs:0} ms, fails {fails}, slow {slowN}, contour mismatches {mism}");
    foreach (var kv in classes.OrderByDescending(x => x.Value.Ms)) Console.WriteLine($"   {kv.Value.N,4} {kv.Value.Ms,8:0} ms fail {kv.Value.Fail,3}  {kv.Key}\n        e.g. {kv.Value.Ex}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "edge")
Check("BENCH-EDGE", () =>
{
    var a = Environment.GetEnvironmentVariable("P3D_EDGE")!.Split(';');   // seed;x,y,z
    var p = Parametric3D.App.Diagnostics.TestParts.Realistic(int.Parse(a[0]), 30); p.Rebuild(null);
    var q = a[1].Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    var at = new Vector3d(q[0], q[1], q[2]);
    var e0 = p.Bodies.SelectMany(b => b.Edges).OrderBy(e => Math.Min((e.V0!.P - at).Length, (e.V1!.P - at).Length)).First();
    string S(Parametric3D.Kernel.Topology.TFace f) => $"{Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} {f.Tag}";
    foreach (var e in Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p)))
    {
        Console.WriteLine($"  edge {e.Curve?.GetType().Name} {e.V0?.P} -> {e.V1?.P} faces: {string.Join(" | ", Parametric3D.App.Operations.Topo.FacesOf(p, e).Select(S))}");
        foreach (var v in new[] { e.V0!, e.V1! })
            Console.WriteLine($"     vertex {v.P} degree {v.Edges.Count()}: {string.Join("; ", v.Edges.Select(x => x.Curve?.GetType().Name + " " + (x.V0 == v ? x.V1!.P : x.V0!.P)))}");
    }
    if (Environment.GetEnvironmentVariable("P3D_OP") is { } op)
    {
        var chain = Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p));
        double size = double.Parse(op[1..], System.Globalization.CultureInfo.InvariantCulture);
        Parametric3D.App.Operations.Topo.GhostDebug = m => Console.WriteLine("    ghost: " + m);
        Console.WriteLine("  predicted: " + (Parametric3D.App.Operations.Topo.EdgeGhostValid(p, chain.Select(c => c.Id), op[0] == 'R')?.Invoke(size)));
        Feature f;
        if (op[0] == 'R') { var rf = new RoundFeature { Radius = new ValueRef(size) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
        else { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(size), D2 = new ValueRef(size) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
        Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (!m.StartsWith("face ") && !m.StartsWith("normalize") && !m.Contains("normalize")) Console.WriteLine("    direct: " + (m.Length > 400 ? m[..400] : m)); };
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = Environment.GetEnvironmentVariable("P3D_PROF") == "1"; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        if (Environment.GetEnvironmentVariable("P3D_UVDBG") == "1") Parametric3D.Kernel.Tess.UvMesher.Debug = m => Console.WriteLine("    uv: " + (m.Length > 300 ? m[..300] : m));
        p.AddFeature(f); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(40)));
        Console.WriteLine($"  {op}: {sw.Elapsed.TotalMilliseconds:0} ms error {f.Error} volume {p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):R}");
        foreach (var w in p.Warnings) Console.WriteLine("    warn " + (w.Length > 300 ? w[..300] : w));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "file")
Check("BENCH-FILE", () =>
{
    var (part, pars) = DocumentStorage.Load(Environment.GetEnvironmentVariable("P3D_FILE")!);
    part.Rebuild(pars);
    Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
    Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => { if (!m.StartsWith("face ")) Console.WriteLine("       direct: " + (m.Length > 500 ? m[..500] : m)); } : null;
    Parametric3D.Kernel.Ops.ShellOp.ShellDebug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => Console.WriteLine("       shell: " + m) : null;
    part.Invalidate();
    var sw = System.Diagnostics.Stopwatch.StartNew(); part.Rebuild(pars);
    Console.WriteLine($"  full rebuild {sw.Elapsed.TotalMilliseconds:0} ms, bodies {part.Bodies.Count}, volume {part.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):F3}, faces {part.Bodies.Sum(b => b.Faces.Count())}, valid {part.Bodies.All(b => Parametric3D.Kernel.Topology.SolidValidator.Validate(b).IsValid)}");
    foreach (var w in part.Warnings.Where(w => w.StartsWith("Shell"))) Console.WriteLine("   shellwarn " + (w.Length > 600 ? w[..600] : w));
    foreach (var f in part.Features) Console.WriteLine($"   {f.Name,-24} {part.LastFeatureMs.GetValueOrDefault(f),8:0} ms  {f.GetType().Name} {f.Error}");
    Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(30)));
    foreach (var f in part.Features.Where(f => f.Error != null))
    {
        if (f is RoundFeature rf2) foreach (var e in rf2.Edges) Console.WriteLine($"   ref {e.Start} -> {e.End} mid {e.Mid} faces {e.FaceA} | {e.FaceB}");
        if (f is ChamferFeature cf2) foreach (var e in cf2.Edges) Console.WriteLine($"   ref {e.Start} -> {e.End} mid {e.Mid} faces {e.FaceA} | {e.FaceB}");
        foreach (var w in part.Warnings.Where(x => x.Contains("Round") || x.Contains("Chamfer"))) Console.WriteLine("   warn " + (w.Length > 300 ? w[..300] : w));
        int at = part.Features.IndexOf(f);
        // тело до операции: стадия
        part.StageIndex = at; part.Invalidate(); part.Rebuild(pars);
        foreach (var e in part.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && e.V0.P.Z > 49 && e.V1.P.Z > 49))
            Console.WriteLine($"   body edge {e.V0.P} -> {e.V1.P}");
        part.StageIndex = -1;
    }
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
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(70)));
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
