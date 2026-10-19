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
using Parametric3D.Model.Params;
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
    // m.x2: IGES из интерфейса — записан, прочитан обратно с тем же объёмом
    string igs = Path.ChangeExtension(path, ".igs"); string m3 = ExportService.Export(s2, igs);
    Require(File.Exists(igs) && m3.Contains("объём сходится"), "IGES: " + m3);
    foreach (var f in new[] { path, step, stl, igs }) File.Delete(f);
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
    // контур без fill (по умолчанию чёрный) и почти чёрный #191919 — тоже цвет темы; серые и белые — как есть
    string r2 = IconProvider.Recolor("<svg width=\"2\"><path d=\"M0 0\"/><path fill=\"#191919\"/><path fill=\"#CCCCCC\"/></svg>", "#E6E8EC");
    Require(r2.StartsWith("<svg fill=\"#E6E8EC\"") && !r2.Contains("#191919") && r2.Contains("#CCCCCC"), r2);
    // без файла иконки — подпись
    Require(IconProvider.Get("no_such_icon_but.svg") == null, "missing icon");
    w.Close();
});


Check("UI-ICONS-USER-SET", () =>
{
    // набор иконок пользователя (icons1, 331 файл): каждый вшит, читается, пропорции рисунка = пропорциям файла (viewBox =
    // width × height), при вписывании в квадрат кнопки — по центру без растяжения; иконка языка — по текущему языку
    var w = Open(DocumentSession.NewPart());
    var files = Avalonia.Platform.AssetLoader.GetAssets(new Uri("avares://Parametric3D/App/Assets/Icons/"), null).Select(u => u.AbsolutePath.Split('/').Last()).Where(n => n.EndsWith(".svg")).ToList();
    Require(files.Count >= 331, $"icons embedded: {files.Count}");
    var bad = new List<string>();
    foreach (var f in files)
    {
        var img = IconProvider.Get(f);
        if (img == null) { bad.Add(f + ": not loaded"); continue; }
        using var st = Avalonia.Platform.AssetLoader.Open(IconProvider.UriOf(f)); var text = new StreamReader(st).ReadToEnd();
        var m = System.Text.RegularExpressions.Regex.Match(text, "viewBox=\"[-\\d.]+ [-\\d.]+ ([\\d.]+) ([\\d.]+)\"");
        if (!m.Success || !text.Contains("preserveAspectRatio=\"xMidYMid meet\"")) { bad.Add(f + ": no viewBox/preserveAspectRatio"); continue; }
        double vw = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), vh = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(img.Size.Width / img.Size.Height - vw / vh) > 1e-6) bad.Add($"{f}: drawing {img.Size} vs file {vw}x{vh}");
        var r = IconProvider.Fit(img.Size, new Rect(0, 0, 24, 24));
        if (Math.Abs(r.Width / r.Height - vw / vh) > 1e-6 || r.Width > 24.0001 || r.Height > 24.0001 || Math.Abs(r.Center.X - 12) > 1e-9 || Math.Abs(r.Center.Y - 12) > 1e-9) bad.Add($"{f}: fit {r}");
    }
    Require(bad.Count == 0, string.Join("; ", bad.Take(8)));
    // лист иконок (по запросу: UI_ICONSHEET=путь.png): каждая — в квадрате 32×32 с рамкой, по 24 в ряду
    if (Environment.GetEnvironmentVariable("UI_ICONSHEET") is { Length: > 0 } sheet)
    {
        int cols = 24, cell = 40, rows = (files.Count + cols - 1) / cols;
        var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(cols * cell, rows * cell));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E2128")), new Rect(0, 0, cols * cell, rows * cell));
            var pen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3F4A")), 1);
            for (int i = 0; i < files.Count; i++)
            {
                var box = new Rect(i % cols * cell + 4, i / cols * cell + 4, 32, 32);
                dc.DrawRectangle(null, pen, box);
                var img = IconProvider.Get(files[i])!;
                img.Draw(dc, new Rect(img.Size), IconProvider.Fit(img.Size, box.Deflate(2)));
            }
        }
        rtb.Save(sheet); File.WriteAllLines(sheet + ".txt", files.Select((f, i) => $"{i / 24},{i % 24} {f}"));
    }
    // команды: иконка есть у каждой команды, для которой файл передан
    var lang = CommandRegistry.Instance.All.First(c => c.Id == "app.language");
    Require(lang.Icon == (Loc.En ? "language_en_but.svg" : "language_ru_but.svg"), "language icon " + lang.Icon);
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

// u13b: кнопки эскиза — связи, размеры (в т. ч. длина дуги), обрезка, скругление угла, новые инструменты рисования, смещение, зеркало
Check("UI-U13B-SKETCH", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    w.ViewPort.Selection.Clear(); w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch mode");
    var sk = w.ViewPort.ActiveSketch!; var solver = s.Document.Solver;
    void Pick(params SkEntity[] es) { w.ViewPort.Selection.Clear(); foreach (var e in es) w.ViewPort.Selection.Select(new PickRef(e.Id, e is SkPoint ? PickKind.SketchPoint : PickKind.SketchCurve, sk, e), additive: true); }
    SkLine Line(double x0, double y0, double x1, double y1) { var t = new LineTool(chain: false); t.Click(new SketchInput(sk, new Vector2d(x0, y0), null)); t.Click(new SketchInput(sk, new Vector2d(x1, y1), null)); return sk.Entities.OfType<SkLine>().Last(); }
    int Count<T>() => sk.Entities.OfType<T>().Count();

    // связи: горизонтально, параллельно, равенство
    var l1 = Line(0, 0, 10, 1); Pick(l1);
    Require(SketchCommands.Constrain(w, ConstraintType.Horizontal) && Math.Abs(l1.A.P.Y - l1.B.P.Y) < 1e-7, "horizontal");
    var l2 = Line(0, 5, 8, 9); Pick(l1, l2);
    Require(SketchCommands.Constrain(w, ConstraintType.Parallel) && Math.Abs(l1.Direction.X * l2.Direction.Y - l1.Direction.Y * l2.Direction.X) < 1e-7, "parallel");
    Pick(l1, l2);
    Require(SketchCommands.Constrain(w, ConstraintType.Equal) && Math.Abs((l1.B.P - l1.A.P).Length - (l2.B.P - l2.A.P).Length) < 1e-6, "equal");
    // размер отрезка → 20: оба отрезка 20 (равенство)
    Pick(l1); var d1 = SketchCommands.Dimension(w, SketchCommands.DimKind.Linear);
    Require(d1 != null && !d1.Reference, "linear dimension");
    d1!.Value = 20; sk.Solve(solver);
    Console.WriteLine($"  lines: {(l1.B.P - l1.A.P).Length:F6} {(l2.B.P - l2.A.P).Length:F6}");
    Require(Math.Abs((l1.B.P - l1.A.P).Length - 20) < 1e-6 && Math.Abs((l2.B.P - l2.A.P).Length - 20) < 1e-6, "dimension drives both lines");
    // противоречивая связь снимается: вертикально к горизонтальному отрезку с длиной 20
    int nc = sk.Constraints.Count; Pick(l1);
    Require(!SketchCommands.Constrain(w, ConstraintType.Vertical) && sk.Constraints.Count == nc, "conflicting constraint reverted");
    // коллинеарно
    var l3 = Line(30, 3, 40, 4); Pick(l1, l3);
    Require(SketchCommands.Constrain(w, ConstraintType.Parallel, collinear: true), "collinear");
    double off = (l3.A.P - l1.A.P).X * l1.Direction.Y - (l3.A.P - l1.A.P).Y * l1.Direction.X;
    Require(Math.Abs(off) < 1e-6, $"collinear offset {off}");

    // длина дуги: дуга R10 на 90°, размер 2π·10/4 → задать 20 (радиус свободен, сохраняется угол? — проверяем R·sweep)
    var c = sk.AddEntity(new SkPoint(new Vector2d(0, -50))); var a0 = sk.AddEntity(new SkPoint(new Vector2d(10, -50))); var a1 = sk.AddEntity(new SkPoint(new Vector2d(0, -40)));
    var arc = sk.AddEntity(new SkArc(c, a0, a1));
    Pick(arc); var da = SketchCommands.Dimension(w, SketchCommands.DimKind.ArcLength);
    Require(da != null && Math.Abs(da.Value - Math.PI * 5) < 1e-6, $"arc length {da?.Value}");
    Pick(arc); var dr = SketchCommands.Dimension(w, SketchCommands.DimKind.Radius);
    Require(dr != null, "radius dimension");
    da!.Value = 10 * Math.PI / 3; sk.Solve(solver); // 60° при R10
    double sweep = Math.Abs(Math.Atan2(((arc.Start.P - c.P).X * (arc.End.P - c.P).Y - (arc.Start.P - c.P).Y * (arc.End.P - c.P).X), Vector2d.Dot(arc.Start.P - c.P, arc.End.P - c.P)));
    Console.WriteLine($"  arc: R {arc.Radius:F6} sweep {sweep * 180 / Math.PI:F4}°");
    Require(Math.Abs(arc.Radius - 10) < 1e-6 && Math.Abs(sweep - Math.PI / 3) < 1e-6, "arc length drives sweep");

    // обрезка: крест, убрать правую половину горизонтального
    var h = Line(100, 0, 120, 0); var v = Line(110, -10, 110, 10);
    Require(TrimTool.Trim(sk, h, new Vector2d(117, 0)), "trim");
    var hl = sk.Entities.OfType<SkLine>().Where(l => Math.Abs(l.A.P.Y) < 1e-9 && Math.Abs(l.B.P.Y) < 1e-9 && Math.Min(l.A.P.X, l.B.P.X) >= 99).ToList();
    Console.WriteLine($"  trim: {string.Join(" ", hl.Select(l => $"[{l.A.P.X:F3},{l.B.P.X:F3}]"))}");
    Require(hl.Count == 1 && Math.Abs(Math.Max(hl[0].A.P.X, hl[0].B.P.X) - 110) < 1e-6, "trimmed to the cross");
    int nl = Count<SkLine>();
    Require(TrimTool.Split(sk, v, new Vector2d(110, 5)) && Count<SkLine>() == nl + 1, "split");

    // скругление угла прямоугольника R3
    Rect(sk, 200, 0, 220, 10); int na = Count<SkArc>();
    Require(CornerTool.Apply(sk, new Vector2d(220, 10), false, 3) && Count<SkArc>() == na + 1 && Math.Abs(sk.Entities.OfType<SkArc>().Last().Radius - 3) < 1e-6, "corner fillet");
    int nl2 = Count<SkLine>();
    Require(CornerTool.Apply(sk, new Vector2d(200, 0), true, 2) && Count<SkLine>() == nl2 + 1, "corner chamfer");

    // инструменты: многоугольник 6, паз, прямоугольник от центра и по трём точкам, окружность по двум точкам
    int n0 = Count<SkLine>(); var pg = new PolygonTool(6); pg.Click(new SketchInput(sk, new Vector2d(300, 0), null)); pg.Click(new SketchInput(sk, new Vector2d(310, 0), null));
    Require(Count<SkLine>() == n0 + 6, "polygon 6");
    n0 = Count<SkLine>(); na = Count<SkArc>(); var sl = new SlotTool();
    sl.Click(new SketchInput(sk, new Vector2d(300, 50), null)); sl.Click(new SketchInput(sk, new Vector2d(330, 50), null)); sl.Click(new SketchInput(sk, new Vector2d(330, 55), null));
    Require(Count<SkLine>() == n0 + 2 && Count<SkArc>() == na + 2, "slot");
    n0 = Count<SkLine>(); var rc = new RectCenterTool(); rc.Click(new SketchInput(sk, new Vector2d(400, 0), null)); rc.Click(new SketchInput(sk, new Vector2d(405, 3), null));
    var rcl = sk.Entities.OfType<SkLine>().Skip(n0).Where(l => l.Role != SkRole.Construction).ToList();
    Require(rcl.Count == 4 && Math.Abs(rcl.Min(l => Math.Min(l.A.P.X, l.B.P.X)) - 395) < 1e-6, "centre rectangle");
    n0 = Count<SkLine>(); var r3 = new Rect3PTool();
    foreach (var q in new[] { new Vector2d(500, 0), new Vector2d(508, 6), new Vector2d(505, 12) }) r3.Click(new SketchInput(sk, q, null));
    var r3l = sk.Entities.OfType<SkLine>().Skip(n0).ToList();
    Require(r3l.Count == 4 && r3l.Any(l => Math.Abs((l.B.P - l.A.P).Length - 10) < 1e-6), "3-point rectangle");
    int ncir = Count<SkCircle>(); var c2 = new Circle2PTool(); c2.Click(new SketchInput(sk, new Vector2d(600, 0), null)); c2.Click(new SketchInput(sk, new Vector2d(610, 0), null));
    Require(Count<SkCircle>() == ncir + 1 && Math.Abs(sk.Entities.OfType<SkCircle>().Last().Radius - 5) < 1e-6, "2-point circle");

    // эллипс по пяти точкам: полуоси 8 и 3, поворот 30°
    double ang = Math.PI / 6; var pts5 = Enumerable.Range(0, 5).Select(i => { double t = i * 1.1; var e = new Vector2d(8 * Math.Cos(t), 3 * Math.Sin(t)); return new Vector2d(700 + e.X * Math.Cos(ang) - e.Y * Math.Sin(ang), e.X * Math.Sin(ang) + e.Y * Math.Cos(ang)); }).ToList();
    var el = Ellipse5PTool.Fit(pts5);
    Console.WriteLine($"  ellipse: {el?.Center.P} {el?.RU:F6} {el?.RV:F6}");
    Require(el != null && (el.Center.P - new Vector2d(700, 0)).Length < 1e-6 && Math.Abs(Math.Max(el.RU, el.RV) - 8) < 1e-6 && Math.Abs(Math.Min(el.RU, el.RV) - 3) < 1e-6, "5-point ellipse");
    // сплайн через точки проходит через них
    var q4 = new List<Vector2d> { new(800, 0), new(810, 5), new(820, -3), new(830, 2) };
    var cv = SplineThroughTool.Interpolate(q4);
    Vector2d Bez(List<Vector2d> P, double t) { var b = P.ToList(); for (int k = b.Count - 1; k > 0; k--) for (int i = 0; i < k; i++) b[i] = b[i] * (1 - t) + b[i + 1] * t; return b[0]; }
    double tot = 0; var tq = new List<double> { 0 }; for (int i = 1; i < q4.Count; i++) { tot += (q4[i] - q4[i - 1]).Length; tq.Add(tot); }
    Require(cv != null && q4.Select((q, i) => (Bez(cv, tq[i] / tot) - q).Length).Max() < 1e-7, "spline through points");

    // смещение контура прямоугольника на 2 наружу: квадрат 10 → 14
    Rect(sk, 900, 0, 910, 10);
    var rect = sk.Entities.OfType<SkLine>().TakeLast(4).ToList();
    var offs = SketchEditCommands.OffsetChain(sk, rect.Cast<SkEntity>().ToList(), 2);
    var xs = offs.OfType<SkLine>().SelectMany(l => new[] { l.A.P.X, l.B.P.X }).ToList();
    Console.WriteLine($"  offset: {offs.Count} lines, x {xs.Min():F4}..{xs.Max():F4}");
    Require(offs.Count == 4 && (Math.Abs(Math.Abs(xs.Max() - xs.Min()) - 14) < 1e-6 || Math.Abs(Math.Abs(xs.Max() - xs.Min()) - 6) < 1e-6), "offset chain");
    // зеркало отрезка относительно вертикальной оси x=1000
    var m = Line(1003, 0, 1005, 4); var ax = Line(1000, -10, 1000, 10);
    int nm = Count<SkLine>(); Pick(m, ax); SketchEditCommands.Mirror(w);
    var mir = sk.Entities.OfType<SkLine>().Last();
    Require(Count<SkLine>() == nm + 1 && new[] { mir.A.P.X, mir.B.P.X }.Order().SequenceEqual(new[] { 995.0, 997.0 }.Select(x => x), new Tol()), $"mirror {mir.A.P} {mir.B.P}");
    // площадь: отдельный эскиз — квадрат 10 с отверстием R2
    w.FinishSketch();
    w.ViewPort.Selection.Clear(); w.SelectItem(part.PlaneXZ); w.StartOperation(new SketchOp());
    var ska = w.ViewPort.ActiveSketch!; Rect(ska, 0, 0, 10, 10);
    var ct = new CircleTool(); ct.Click(new SketchInput(ska, new Vector2d(5, 5), null)); ct.Click(new SketchInput(ska, new Vector2d(7, 5), null));
    var ar = SketchCommands.Area(ska);
    Console.WriteLine($"  area: {ar}");
    Require(ar is { } aa && Math.Abs(aa.Area - (100 - Math.PI * 4)) < 1e-6 && aa.Regions == 1, "area");
    w.FinishSketch();
    var st = sk.Solve(solver);
    Console.WriteLine($"  solve: {st}, entities {sk.Entities.Count}, constraints {sk.Constraints.Count}");
    Require(st is not (SolveStatus.Overconstrained or SolveStatus.Inconsistent), $"sketch solves: {st}");
    w.Close();
});

// u13c: тонкое выдавливание, винтовая протяжка, перенос/копия, прямая правка граней, массивы по точкам/кривой/заполнением, резьба
Check("UI-U13C-FEATURES", () =>
{
    // тонкое выдавливание: квадрат 20, стенка 2 внутрь, высота 10 → (400 − 256)·10
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var a = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, -10, -10, 10, 10));
    var ex = new ExtrudeFeature { SketchId = a.Id, Thin = new ValueRef(2) }; ex.Side1.Distance = new ValueRef(10); part.AddFeature(ex); s.Document.RebuildDirty();
    Console.WriteLine($"  thin: error {ex.Error}, volume {part.Volume:F6}");
    Require(ex.Error == null && Math.Abs(part.Volume - 1440) < 1e-6, $"thin extrude {part.Volume} {ex.Error}");
    ex.ThinOutside = true; part.NotifyChanged(ex); s.Document.RebuildDirty();
    Console.WriteLine($"  thin outside: error {ex.Error}, volume {part.Volume:F6}");
    Require(ex.Error == null && Math.Abs(part.Volume - (576 - 400) * 10) < 1e-6, $"thin outside {part.Volume} {ex.Error}");
    w.Close();

    // винтовая протяжка: круг r1 на расстоянии 10 от оси, шаг 5, 3 витка
    var s2 = DocumentSession.NewPart(); var w2 = Open(s2); var p2 = s2.Part!;
    var sec = DrawSketch(w2, p2, p2.PlaneXZ, sk =>
    {
        var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(10, 0), null)); c.Click(new SketchInput(sk, new Vector2d(11, 0), null));
        var cl = new LineTool(chain: false); cl.Click(new SketchInput(sk, new Vector2d(0, -5), null)); cl.Click(new SketchInput(sk, new Vector2d(0, 5), null));
        sk.Entities.OfType<SkLine>().Last().Role = SkRole.Centerline;
    });
    var hs = new HelicalSweepFeature { SketchId = sec.Id, Pitch = new ValueRef(5), Turns = new ValueRef(3) }; p2.AddFeature(hs); s2.Document.RebuildDirty();
    double wantH = Math.PI * 3 * Math.Sqrt(Math.Pow(2 * Math.PI * 10, 2) + 25);
    Console.WriteLine($"  helical: error {hs.Error}, volume {p2.Volume:F3} want {wantH:F3}");
    Require(hs.Error == null && Math.Abs(p2.Volume - wantH) < 0.02 * wantH, $"helical sweep {p2.Volume} ({hs.Error})");
    var zs = p2.Bodies[0].Vertices.Select(v => v.P.Z).ToList();
    Require(zs.Count == 0 || zs.Max() - zs.Min() > 14, "helix height");
    w2.Close();

    // брусок 40×20×10 для прямой правки, копии, массивов
    (DocumentSession S, MainWindow W, Part P, Sketch Sk) Box()
    {
        var sb = DocumentSession.NewPart(); var wb = Open(sb); var pb = sb.Part!;
        var b = DrawSketch(wb, pb, pb.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
        var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); pb.AddFeature(e); sb.Document.RebuildDirty();
        Require(Math.Abs(pb.Volume - 8000) < 1e-6, "box");
        return (sb, wb, pb, b);
    }
    var (s3, w3, p3, _) = Box();
    var mc = new MoveCopyFeature { Dx = new ValueRef(100), Copy = true }; p3.AddFeature(mc); s3.Document.RebuildDirty();
    Require(mc.Error == null && Math.Abs(p3.Volume - 16000) < 1e-6, $"copy {p3.Volume} {mc.Error}");
    mc.Copy = false; mc.Angle = new ValueRef(90); p3.NotifyChanged(mc); s3.Document.RebuildDirty();
    var bx = p3.Bodies[0].Vertices.Select(v => v.P).ToList();
    Console.WriteLine($"  move: x {bx.Min(v => v.X):F3}..{bx.Max(v => v.X):F3} y {bx.Min(v => v.Y):F3}..{bx.Max(v => v.Y):F3}");
    Require(mc.Error == null && Math.Abs(p3.Volume - 8000) < 1e-6 && Math.Abs(bx.Min(v => v.X) - 80) < 1e-6 && Math.Abs(bx.Max(v => v.Y) - 40) < 1e-6, "move + rotate");
    p3.RemoveFeature(mc); s3.Document.RebuildDirty();
    // смещение верхней грани на 5 → 40·20·15; через операцию UI
    var top = p3.Bodies[0].Faces.First(f => Refs.FacePlane(p3, f.Id) is { } pl && pl.N.Z > 0.99);
    w3.ViewPort.Selection.Clear(); Select(w3, new PickRef(top.Id, PickKind.Face, p3, null));
    var fop = new FaceEditOp(); w3.StartOperation(fop);
    var fe = p3.Features.OfType<FaceEditFeature>().Last(); fe.Distance = new ValueRef(5);
    w3.CommitOperation(); s3.Document.RebuildDirty();
    Console.WriteLine($"  offset face: error {fe.Error}, volume {p3.Volume:F6}");
    Require(fe.Error == null && Math.Abs(p3.Volume - 12000) < 1e-6, $"offset face {p3.Volume} {fe.Error}");
    p3.RemoveFeature(fe); s3.Document.RebuildDirty();
    // скругление R5 верхнего ребра → новый радиус 3 → удаление скругления
    double zMax = 10;
    var edge = p3.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - zMax) < 1e-6 && Math.Abs(x.V1.P.Z - zMax) < 1e-6 && Math.Abs(x.V0.P.Y - x.V1.P.Y) < 1e-9 && Math.Abs(x.V0.P.Y) < 1e-9);
    var rf = new RoundFeature { Radius = new ValueRef(5) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); p3.AddFeature(rf); s3.Document.RebuildDirty();
    double vR5 = p3.Volume, cut5 = (25 - Math.PI * 25 / 4) * 40;
    Require(Math.Abs(vR5 - (8000 - cut5)) < 1e-6, $"round {vR5}");
    var rface = p3.Bodies[0].Faces.First(f => f.Tag.Contains("round:face"));
    var at = Parametric3D.Model.Features.FaceRefs.Inside(rface);
    var nr = new FaceEditFeature { Mode = FaceEditMode.Radius, Radius = new ValueRef(3) }; nr.OnFaces.Add(at); p3.AddFeature(nr); s3.Document.RebuildDirty();
    double cut3 = (9 - Math.PI * 9 / 4) * 40;
    Console.WriteLine($"  new radius: error {nr.Error}, volume {p3.Volume:F6} want {8000 - cut3:F6}");
    Require(nr.Error == null && Math.Abs(p3.Volume - (8000 - cut3)) < 1e-6, $"new radius {p3.Volume} {nr.Error}");
    nr.Mode = FaceEditMode.Delete; p3.NotifyChanged(nr); s3.Document.RebuildDirty();
    Console.WriteLine($"  delete round: error {nr.Error}, volume {p3.Volume:F6}");
    Require(nr.Error == null && Math.Abs(p3.Volume - 8000) < 1e-6, $"delete round {p3.Volume} {nr.Error}");
    // резьба на цилиндре: отверстие Ø6 насквозь
    p3.RemoveFeature(nr); p3.RemoveFeature(rf); s3.Document.RebuildDirty();
    w3.Close();

    // массивы: вырез-круг r2 в (5,5) и копии в точках, по кривой, заполнением
    var (s4, w4, p4, _) = Box();
    var hole = DrawSketch(w4, p4, p4.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(5, 5), null)); c.Click(new SketchInput(sk, new Vector2d(7, 5), null)); });
    var cut = new ExtrudeFeature { SketchId = hole.Id, Cut = true }; cut.Side1.Distance = new ValueRef(10); p4.AddFeature(cut); s4.Document.RebuildDirty();
    double vHole = Math.PI * 4 * 10;
    Require(Math.Abs(p4.Volume - (8000 - vHole)) < 1e-6, $"hole {p4.Volume}");
    var pts = DrawSketch(w4, p4, p4.PlaneXY, sk => { foreach (var q in new[] { new Vector2d(5, 5), new Vector2d(15, 5), new Vector2d(25, 15) }) sk.AddEntity(new SkPoint(q)); });
    var pp = new PointsPatternFeature { SketchId = pts.Id }; pp.Sources.Add(cut.Key); p4.AddFeature(pp); s4.Document.RebuildDirty();
    Console.WriteLine($"  points pattern: error {pp.Error}, volume {p4.Volume:F6}");
    Require(pp.Error == null && Math.Abs(p4.Volume - (8000 - 3 * vHole)) < 1e-6, $"points pattern {p4.Volume} {pp.Error}");
    p4.RemoveFeature(pp);
    var path = DrawSketch(w4, p4, p4.PlaneXY, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, new Vector2d(5, 5), null)); l.Click(new SketchInput(sk, new Vector2d(35, 5), null)); });
    var pc = new PointsPatternFeature { SketchId = path.Id, Mode = PointsPatternMode.Curve, Count = new ValueRef(4) }; pc.Sources.Add(cut.Key); p4.AddFeature(pc); s4.Document.RebuildDirty();
    Console.WriteLine($"  curve pattern: error {pc.Error}, volume {p4.Volume:F6}");
    Require(pc.Error == null && Math.Abs(p4.Volume - (8000 - 4 * vHole)) < 1e-6, $"curve pattern {p4.Volume} {pc.Error}");
    p4.RemoveFeature(pc);
    var region = DrawSketch(w4, p4, p4.PlaneXY, sk => { Rect(sk, 1, 1, 39, 19); sk.AddEntity(new SkPoint(new Vector2d(5, 5))); });
    var pf = new PointsPatternFeature { SketchId = region.Id, Mode = PointsPatternMode.Fill, Spacing = new ValueRef(10), Margin = new ValueRef(2) }; pf.Sources.Add(cut.Key); p4.AddFeature(pf); s4.Document.RebuildDirty();
    // сетка от (5,5) с шагом 10: x 5,15,25,35; y 5,15 → 8 мест (все дальше 2 от края области 1..39×1..19)
    Console.WriteLine($"  fill pattern: error {pf.Error}, volume {p4.Volume:F6}");
    Require(pf.Error == null && Math.Abs(p4.Volume - (8000 - 8 * vHole)) < 1e-6, $"fill pattern {p4.Volume} {pf.Error}");
    // резьба на стенке отверстия
    var hf = p4.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 2) < 1e-9);
    var th = new CosmeticThreadFeature(); th.OnFaces.Add(Parametric3D.Model.Features.FaceRefs.Inside(hf)); p4.AddFeature(th); s4.Document.RebuildDirty();
    Console.WriteLine($"  thread: error {th.Error}, d {th.Diameter} internal {th.Internal}");
    Require(th.Error == null && Math.Abs(th.Diameter - 4) < 1e-9 && th.Internal, "thread");
    // сохранение и загрузка: новые операции переживают файл
    var json = Parametric3D.Model.DocumentFile.Save(p4);
    Require(json.Contains("points_pattern") && json.Contains("\"thread\""), "saved kinds");
    w4.Close();
});

// u13d: вид, выбор (режимы, рамка, контур грани), измерение и анализ
Check("UI-U13D-VIEW-SELECT", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); part.AddFeature(e); s.Document.RebuildDirty(); part.Rebuild(null);
    var vp = w.ViewPort;
    foreach (var id in new[] { "view.front", "view.top", "view.grid", "view.grid", "an.zebra", "an.zebra", "view.section", "view.section" })
        CommandRegistry.Instance.Find(id)!.Run!();
    Require(vp.SectionPlane == null && !vp.Zebra && !vp.ModelGrid, "toggles return");
    vp.Camera.State.Rotation = Parametric3D.Render.Camera.OrientationQuat(Parametric3D.Render.ViewOrientation.Isometric); vp.ZoomToFit(animate: false);
    // рамка на весь вид в режиме граней — шесть граней бруска
    ViewSelectCommands.SetMode(w, ViewSelectCommands.SelMode.Face);
    double W = vp.Camera.ViewportWidth / vp.Scaling, H = vp.Camera.ViewportHeight / vp.Scaling;
    var all = vp.ItemsInRegion(new[] { new Avalonia.Point(0, 0), new Avalonia.Point(W, 0), new Avalonia.Point(W, H), new Avalonia.Point(0, H) }, crossing: false);
    Console.WriteLine($"  region: {all.Count} items, kinds {string.Join(",", all.Select(x => x.Kind).Distinct())}");
    Require(all.Count == 6 && all.All(x => x.Kind == PickKind.Face), "box select faces");
    // режим «операция»: грань → выдавливание
    ViewSelectCommands.SetMode(w, ViewSelectCommands.SelMode.Feature);
    var mapped = vp.PickMap!(all[0]);
    Require(mapped.Kind == PickKind.Feature && ReferenceEquals(mapped.Target, e), "feature mode");
    ViewSelectCommands.SetMode(w, ViewSelectCommands.SelMode.Feature);   // повторно — выключить
    Require(ViewSelectCommands.Mode == ViewSelectCommands.SelMode.All && vp.PickMap == null && vp.Selection.Filter == PickFilter.All, "mode off");
    // контур верхней грани, площадь и периметр
    var top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    vp.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    Require(Math.Abs(ViewSelectCommands.SelectedArea(w) - 800) < 1e-6, $"area {ViewSelectCommands.SelectedArea(w)}");
    Require(Math.Abs(ViewSelectCommands.SelectedLength(w) - 120) < 1e-6, $"perimeter {ViewSelectCommands.SelectedLength(w)}");
    Require(ViewSelectCommands.FaceLoop(w) == 4 && vp.Selection.Items.All(x => x.Kind == PickKind.Edge), "face loop");
    // измерение двух вершин по диагонали
    var vs = part.Bodies[0].Vertices.ToList();
    var v0 = vs.First(v => v.P.Length < 1e-9); var v1 = vs.First(v => (v.P - new Vector3d(40, 20, 10)).Length < 1e-9);
    vp.Selection.Clear(); Select(w, new PickRef(v0.Id, PickKind.Vertex, part, null)); Select(w, new PickRef(v1.Id, PickKind.Vertex, part, null));
    var m = ViewSelectCommands.Measure(w);
    Console.WriteLine("  measure: " + string.Join(" | ", m));
    Require(m.Any(x => x.Contains(Math.Sqrt(2100).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))), "measure distance");
    // гребёнка кривизны: скругление R5 — постоянная кривизна
    var edge = part.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 10) < 1e-6 && Math.Abs(x.V1.P.Z - 10) < 1e-6 && Math.Abs(x.V0.P.Y) < 1e-9 && Math.Abs(x.V1.P.Y) < 1e-9);
    var rf = new RoundFeature { Radius = new ValueRef(5) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); part.AddFeature(rf); s.Document.RebuildDirty(); part.Rebuild(null);
    var arc = part.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 5) < 1e-9);
    vp.Selection.Clear(); Select(w, new PickRef(arc.Id, PickKind.Edge, part, null));
    var comb = ViewSelectCommands.CurvatureComb(w, scale: 10);
    var ac = ((Parametric3D.Kernel.Curves.ArcCurve3)arc.Curve).Frame.Origin;
    var spikes = comb.Where(c => Math.Abs((c.A - ac).Length - 5) < 1e-6 && (c.B - c.A).Length > 1e-9 && Math.Abs(Vector3d.Dot((c.B - c.A).Normalized(), (c.A - ac).Normalized())) > 0.999).Select(c => (c.B - c.A).Length).ToList();
    Console.WriteLine($"  comb: {comb.Count} lines, spike {spikes.Min():F4}..{spikes.Max():F4}");
    Require(comb.Count > 0 && spikes.All(x => Math.Abs(x - 10.0 / 5) < 1e-3), "curvature comb of R5");
    var chk = ViewSelectCommands.CheckBodies(part);
    Require(chk.Count == 1 && chk[0].Contains("годно") || chk[0].Contains("valid"), "check body: " + string.Join(";", chk));
    var mt = ViewSelectCommands.MassText(part);
    Require(mt.Contains("7785.398"), "mass text: " + mt);
    w.Close();
});

// u13d: копировать/вставить операции, исполнения (переменные и погашения), единицы
Check("UI-U13D-DOC", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var table = s.Document.ScopeFor(part); table.Add("H", 10, ParamKind.Real, "mm");
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10) { Formula = "H" }; part.AddFeature(e); s.Document.RebuildDirty();
    var hole = DrawSketch(w, part, part.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(5, 5), null)); c.Click(new SketchInput(sk, new Vector2d(7, 5), null)); });
    var cut = new ExtrudeFeature { SketchId = hole.Id, Cut = true, Name = "Hole" }; cut.Side1.Distance = new ValueRef(100); part.AddFeature(cut); s.Document.RebuildDirty();
    double v1 = part.Volume;
    Require(Math.Abs(v1 - (8000 - Math.PI * 40)) < 1e-6, $"base volume {v1}");
    // копировать и вставить вырез: ещё одна операция и эскиз, объём тот же (то же место)
    int nf = part.Features.Count, ns = part.Children.OfType<Sketch>().Count();
    w.SelectItem(cut); Require(DocCommands.Copy(w) == 1, "copy");
    var pasted = DocCommands.Paste(w); s.Document.RebuildDirty();
    Console.WriteLine($"  paste: {pasted.Count}, features {part.Features.Count}, sketches {part.Children.OfType<Sketch>().Count()}, volume {part.Volume:F6}, error {pasted.FirstOrDefault()?.Error}");
    Require(pasted.Count == 1 && part.Features.Count == nf + 1 && part.Children.OfType<Sketch>().Count() == ns + 1 && pasted[0].Error == null && Math.Abs(part.Volume - v1) < 1e-6, "paste");
    Require(pasted[0].Key != cut.Key && pasted[0].Name != cut.Name, "pasted key and name");
    part.RemoveFeature(pasted[0]); s.Document.RebuildDirty();
    // исполнения: «Высокая» — H = 20; «Без отверстия» — вырез погашен
    table.Find("H")!.Number = 20; table.Recalculate();
    part.Configurations.Add(Configurations.Capture(part, table, "Высокая"));
    table.Find("H")!.Number = 10; table.Recalculate(); cut.Suppressed = true;
    part.Configurations.Add(Configurations.Capture(part, table, "Без отверстия"));
    cut.Suppressed = false; part.Invalidate(); s.Document.RebuildDirty();
    Configurations.Apply(part, table, "Высокая"); s.Document.RebuildDirty();
    double vHigh = part.Volume;
    Configurations.Apply(part, table, "Без отверстия"); s.Document.RebuildDirty();
    double vNo = part.Volume;
    Configurations.Apply(part, table, ""); s.Document.RebuildDirty();
    double vBase = part.Volume;
    Console.WriteLine($"  configurations: high {vHigh:F3}, no hole {vNo:F3}, base {vBase:F3}");
    Require(Math.Abs(vHigh - (16000 - Math.PI * 80)) < 1e-6 && Math.Abs(vNo - 8000) < 1e-6 && Math.Abs(vBase - v1) < 1e-6 && !cut.Suppressed && part.ActiveConfiguration == "", "configurations");
    // единицы: дюймы в поле операции
    DocCommands.ToggleUnits(w);
    Require(DisplayUnits.Inch && part.Units.Length == "in", "inch on");
    DocCommands.ToggleUnits(w);
    Require(!DisplayUnits.Inch, "inch off");
    var json = Parametric3D.Model.DocumentFile.Save(part, table);
    Require(json.Contains("Высокая"), "configurations saved");
    w.Close();
});

// u13d: контекстная панель (повысить, похожие, режим, направление, ещё) и состояния дерева
Check("UI-U13D-CONTEXT", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); part.AddFeature(e); s.Document.RebuildDirty(); part.Rebuild(null);
    var vp = w.ViewPort;
    // похожие: верхняя грань → нижняя той же площади
    var top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    vp.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    Require(ContextCommands.SelectSimilar(w) == 1, $"similar faces {vp.Selection.Count}");
    // повысить: вершина → три ребра → четыре... грани → тело
    var v0 = part.Bodies[0].Vertices.First(v => v.P.Length < 1e-9);
    vp.Selection.Clear(); Select(w, new PickRef(v0.Id, PickKind.Vertex, part, null));
    Require(ContextCommands.Promote(w) == 3, "vertex → edges");
    Require(ContextCommands.Promote(w) == 3 && vp.Selection.Items.All(x => x.Kind == PickKind.Face), $"edges → faces {vp.Selection.Count}");
    Require(ContextCommands.Promote(w) == 1 && vp.Selection.Primary.Kind == PickKind.Body, "faces → body");
    // режим и направление в операции выдавливания
    vp.Selection.Clear();
    var op = new ExtrudeOp { SeedSketch = b }; w.StartOperation(op);
    var f = (ExtrudeFeature)op.EditedFeature!;
    var reg = CommandRegistry.Instance;
    Require(reg.Find("ctx.mode_cut")!.Enabled && reg.Find("ctx.flip")!.Enabled && reg.Find("ctx.both_sides")!.Enabled, "op context commands");
    reg.Find("ctx.mode_cut")!.Run!(); Require(f.Cut, "cut mode");
    reg.Find("ctx.mode_add")!.Run!(); Require(!f.Cut, "add mode");
    reg.Find("ctx.flip")!.Run!(); Require(f.Side1.Flip, "flip");
    reg.Find("ctx.both_sides")!.Run!(); Require(f.Symmetric, "both sides");
    reg.Find("ctx.next")!.Run!();
    w.CancelOperation();
    // «ещё»: допустимые операции вне предложенных
    vp.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    var more = ContextCommands.MoreOps(w);
    Require(more.Count > 0 && more.All(c => c.Id.StartsWith("op.")) && !more.Any(c => w.SuggestedOps().Contains(c.Id)), "more ops");
    // состояния дерева: подавленная операция и узлы детали
    e.Suppressed = true; s.Document.RebuildDirty();
    var st = TreeNode.StateOf(e, part);
    Require(st.File == "state_suppressed_ico.svg", "suppressed state");
    e.Suppressed = false; s.Document.RebuildDirty();
    var roots = TreeNode.Build(s.Document);
    var infos = roots[0].Children.Where(n => n.Item is InfoItem).Select(n => ((InfoItem)n.Item).Role).ToList();
    Console.WriteLine($"  tree info nodes: {string.Join(",", infos)}");
    Require(infos.Contains("params") && infos.Contains("material") && infos.Contains("body"), "info nodes");
    w.Close();
});

// u13e: система координат, опорные кривые (через точки, уравнение, проекция), протяжка по кривой, соосное и радиальное
// отверстия, полное скругление, группа, родители и потомки, замена эскиза, сохранение опорных элементов
Check("UI-U13E-DATUMS-HOLES", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); part.AddFeature(e); s.Document.RebuildDirty(); part.Rebuild(null);
    // система координат по трём плоскостям детали — в начале
    w.ViewPort.Selection.Clear(); foreach (var pl in new[] { part.PlaneXY, part.PlaneXZ, part.PlaneYZ }) Select(w, new PickRef(pl.Id, PickKind.DatumPlane, part, pl));
    var cop = new CsysOp(); w.StartOperation(cop); w.CommitOperation();
    var cs = part.Children.OfType<DatumCsys>().Single();
    Require(cs.Frame.Origin.Length < 1e-9, $"csys origin {cs.Frame.Origin}");
    // кривая по уравнению: винтовая линия R50, 2 витка
    var eq = CurveOp.Equation(s.Document.ScopeFor(part), "50*cos(t)", "50*sin(t)", "t/10", 0, 720, 200, out var why);
    Require(eq != null && eq.Count == 201 && (eq[^1] - new Vector3d(50, 0, 72)).Length < 1e-9, $"equation curve {why}");
    // через точки: гладкая проходит через точки
    var q = new List<Vector3d> { new(0, 0, 0), new(10, 5, 0), new(20, 0, 5), new(30, 5, 5) };
    var tp = CurveOp.ThroughPoints(q, false, true);
    Require(q.All(x => tp.Any(y => (x - y).Length < 1e-9)), "through points");
    // проекция отрезка на верхнюю грань вдоль −Z
    var top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    var tris = new List<(Vector3d, Vector3d, Vector3d)>();
    var md = part.Mesh!; for (int i = 0; i + 2 < md.Indices.Length; i += 3) if (md.Vertices[md.Indices[i]].FaceId == top.Id) tris.Add((V(md.Vertices[md.Indices[i]].P), V(md.Vertices[md.Indices[i + 1]].P), V(md.Vertices[md.Indices[i + 2]].P)));
    var pr = CurveOp.Project(Enumerable.Range(0, 11).Select(i => new Vector3d(5 + 3 * i, 10, 50)), -Vector3d.UnitZ, tris);
    Require(pr.Count == 11 && pr.All(x => Math.Abs(x.Z - 10) < 1e-6), "projection");
    // соосное отверстие Ø6 на оси отверстия Ø4 в (10, 10) — с верхней грани насквозь
    var hs = DrawSketch(w, part, part.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(10, 10), null)); c.Click(new SketchInput(sk, new Vector2d(12, 10), null)); });
    var cut = new ExtrudeFeature { SketchId = hs.Id, Cut = true }; cut.Side1.Distance = new ValueRef(10); part.AddFeature(cut); s.Document.RebuildDirty(); part.Rebuild(null);
    var cylFace = part.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 2) < 1e-9);
    top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(cylFace.Id, PickKind.Face, part, null)); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    var hop = new AxialHoleOp(); w.StartOperation(hop);
    var hf = part.Features.OfType<HoleFeature>().Last(); hf.ThroughAll = true; hf.Diameter = new ValueRef(6);
    w.CommitOperation(); s.Document.RebuildDirty();
    Console.WriteLine($"  coaxial: error {hf.Error}, at {hf.Points.FirstOrDefault()}, dir {hf.Direction}, volume {part.Volume:F4}");
    Require(hf.Error == null && Math.Abs(part.Volume - (8000 - Math.PI * 9 * 10)) < 1e-4, "coaxial hole");
    // группа, родители и потомки
    TreeCommands.SetGroup(w, new Feature[] { cut, hf }, "Отверстия");
    var roots = TreeNode.Build(s.Document);
    var grp = roots[0].Children.FirstOrDefault(n => n.Item is InfoItem { Role: "group" });
    Require(grp != null && grp.Children.Count == 2, "group node");
    var redge = part.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 10) < 1e-6 && Math.Abs(x.V1.P.Z - 10) < 1e-6 && Math.Abs(x.V0.P.Y - 20) < 1e-9 && Math.Abs(x.V1.P.Y - 20) < 1e-9);
    var rnd = new RoundFeature { Radius = new ValueRef(2) }; rnd.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(redge)); part.AddFeature(rnd); s.Document.RebuildDirty();
    var par = TreeCommands.Parents(part, rnd);
    Console.WriteLine($"  parents of round: {string.Join(", ", par.Select(x => x.Name))}");
    Require(par.Contains(e) && TreeCommands.Children(part, e).Contains(rnd), "parents and children");
    part.RemoveFeature(rnd); s.Document.RebuildDirty();
    Require(TreeCommands.Parents(part, cut).Any(x => x is Sketch), "sketch parent");
    // сохранение и загрузка: опорные элементы и группа
    var cv = new DatumCurve("K", tp, "through points"); part.Add(cv);
    var json = Parametric3D.Model.DocumentFile.Save(part, s.Document.ScopeFor(part));
    var (p2, _) = Parametric3D.Model.DocumentFile.Load(json);
    Require(p2.Children.OfType<DatumCsys>().Count() == 1 && p2.Children.OfType<DatumCurve>().Single().Points.Count == tp.Count, "datums saved");
    Require(p2.Features.Count(f => f.Group == "Отверстия") == 2, "group saved");
    // замена эскиза выдавливания: прямоугольник 40×30
    var b2 = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 30));
    TreeCommands.ReplaceSketch(w, e, b2); s.Document.RebuildDirty();
    Console.WriteLine($"  replace sketch: volume {part.Volume:F4}");
    Require(e.SketchId == b2.Id && Math.Abs(part.Volume - (12000 - Math.PI * 9 * 10)) < 1e-4, "replace sketch");
    w.Close();

    // радиальное отверстие Ø4 насквозь в цилиндре R10 высотой 40
    var s3 = DocumentSession.NewPart(); var w3 = Open(s3); var p3 = s3.Part!;
    var cs3 = DrawSketch(w3, p3, p3.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(10, 0), null)); });
    var ce = new ExtrudeFeature { SketchId = cs3.Id }; ce.Side1.Distance = new ValueRef(40); p3.AddFeature(ce); s3.Document.RebuildDirty(); p3.Rebuild(null);
    double v3 = p3.Volume;
    var side = p3.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 10) < 1e-9);
    var pt = DrawSketch(w3, p3, p3.PlaneXZ, sk => sk.AddEntity(new SkPoint(new Vector2d(10, 20))));
    var ptRef = new PickRef(pt.Entities.OfType<SkPoint>().First().Id, PickKind.SketchPoint, pt, pt.Entities.OfType<SkPoint>().First());
    w3.ViewPort.Selection.Clear(); Select(w3, new PickRef(side.Id, PickKind.Face, p3, null)); Select(w3, ptRef);
    var rop = new AxialHoleOp { Radial = true }; w3.StartOperation(rop);
    var rh = p3.Features.OfType<HoleFeature>().Last(); rh.ThroughAll = true; rh.Diameter = new ValueRef(4);
    w3.CommitOperation(); s3.Document.RebuildDirty();
    double removed = v3 - p3.Volume, approx = Math.PI * 4 * 20;
    Console.WriteLine($"  radial: error {rh.Error}, at {rh.Points.FirstOrDefault()}, dir {rh.Direction}, removed {removed:F3} (≈ {approx:F3})");
    Require(rh.Error == null && removed > 0.97 * approx && removed < approx, "radial hole");
    w3.Close();

    // полное скругление ребра 10 × 40 × 20: верхняя грань → полуцилиндр R5
    var s4 = DocumentSession.NewPart(); var w4 = Open(s4); var p4 = s4.Part!;
    var rs = DrawSketch(w4, p4, p4.PlaneXY, sk => Rect(sk, 0, 0, 10, 40));
    var re = new ExtrudeFeature { SketchId = rs.Id }; re.Side1.Distance = new ValueRef(20); p4.AddFeature(re); s4.Document.RebuildDirty(); p4.Rebuild(null);
    Vector3d FaceN(Vector3d n) { var f = p4.Bodies[0].Faces.First(x => Refs.FacePlane(p4, x.Id) is { } pl && (pl.N - n).Length < 1e-9); return Parametric3D.Model.Features.FaceRefs.Inside(f); }
    var fr = new FullRoundFeature(); fr.OnFaces.Add(FaceN(-Vector3d.UnitX)); fr.OnFaces.Add(FaceN(Vector3d.UnitZ)); fr.OnFaces.Add(FaceN(Vector3d.UnitX));
    p4.AddFeature(fr); s4.Document.RebuildDirty();
    double want = 8000 - 2000 + Math.PI * 25 / 2 * 40;
    Console.WriteLine($"  full round: error {fr.Error}, volume {p4.Volume:F4} want {want:F4}, faces {p4.Bodies.FirstOrDefault()?.Faces.Count()}");
    Require(fr.Error == null && Math.Abs(p4.Volume - want) < 1e-3 * want, "full round");
    w4.Close();
});
Vector3d V(OpenTK.Mathematics.Vector3 v) => new(v.X, v.Y, v.Z);

// u13f: поверхности (квилты): выдавливание, вращение, заливка, копия, смещение, обрезка, продление, объединение,
// утолщение, отверждение, замена грани; переходы граничный, с протяжкой и вращением; вставка тела
Check("UI-U13F-SURFACES", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    double QArea(string key) => Parametric3D.Model.Features.QuiltUtilT.Area(part.Quilts.First(q => q.Key == key));
    // поверхность выдавливанием незамкнутого отрезка 20 на 10 → 200; утолщение 2 → 20·10·2
    var ln = DrawSketch(w, part, part.PlaneXY, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, Vector2d.Zero, null)); l.Click(new SketchInput(sk, new Vector2d(20, 0), null)); });
    var se = new SurfaceFeature { SketchId = ln.Id, Depth = new ValueRef(10) }; part.AddFeature(se); s.Document.RebuildDirty();
    Console.WriteLine($"  surface extrude: error {se.Error}, quilts {part.Quilts.Count}, area {QArea(se.Key):F6}");
    Require(se.Error == null && Math.Abs(QArea(se.Key) - 200) < 1e-6, "surface extrude");
    Require(part.Mesh != null && part.Mesh.Indices.Length > 0, "quilt mesh shown");
    var th = new QuiltSolidFeature { Source = se.Key, Thickness = new ValueRef(2) }; part.AddFeature(th); s.Document.RebuildDirty();
    Console.WriteLine($"  thicken: error {th.Error}, volume {part.Volume:F6}");
    Require(th.Error == null && Math.Abs(part.Volume - 400) < 1e-6, "thicken");
    part.RemoveFeature(th); s.Document.RebuildDirty();
    // продление на 5 — площадь больше
    var ex = new QuiltEditFeature { Mode = QuiltEditKind.Extend, Distance = new ValueRef(5) }; ex.Sources.Add(se.Key); part.AddFeature(ex); s.Document.RebuildDirty();
    Console.WriteLine($"  extend: error {ex.Error}, area {(ex.Error == null ? QArea(ex.Key) : 0):F4}");
    Require(ex.Error == null && QArea(ex.Key) > 200 + 2 * 5 * 20 - 1e-6, "extend");
    part.RemoveFeature(ex); part.RemoveFeature(se); s.Document.RebuildDirty();

    // заливка 20×10 → утолщение 3 = 600; обрезка по x = 5 → 50 (другая сторона — 150)
    var fs = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 20, 10));
    var fill = new SurfaceFeature { Mode = SurfaceKind.Fill, SketchId = fs.Id }; part.AddFeature(fill); s.Document.RebuildDirty();
    if (Environment.GetEnvironmentVariable("P3D_SAVE_SURF") is { } sp) File.WriteAllText(sp, Parametric3D.Model.DocumentFile.Save(part, s.Document.ScopeFor(part)));
    Require(fill.Error == null && Math.Abs(QArea(fill.Key) - 200) < 1e-6, $"fill {fill.Error}");
    var tr = new QuiltEditFeature { Mode = QuiltEditKind.Trim, PlaneAt = new Vector3d(5, 0, 0), PlaneNormal = Vector3d.UnitX }; tr.Sources.Add(fill.Key); part.AddFeature(tr); s.Document.RebuildDirty();
    Console.WriteLine($"  trim: error {tr.Error}, area {(tr.Error == null ? QArea(tr.Key) : 0):F6}");
    Require(tr.Error == null && Math.Abs(QArea(tr.Key) - 50) < 1e-6, "trim");
    var th2 = new QuiltSolidFeature { Source = tr.Key, Thickness = new ValueRef(3) }; part.AddFeature(th2); s.Document.RebuildDirty();
    Console.WriteLine($"  thicken fill: error {th2.Error}, volume {part.Volume:F6}");
    Require(th2.Error == null && Math.Abs(part.Volume - 150) < 1e-6, "thicken trimmed fill");
    part.RemoveFeature(th2); part.RemoveFeature(tr); part.RemoveFeature(fill); s.Document.RebuildDirty();

    // поверхность вращением: образующая x = 10 высотой 20 вокруг оси → цилиндр, площадь 2π·10·20; утолщение 2 внутрь
    var rv = DrawSketch(w, part, part.PlaneXZ, sk =>
    {
        var l = new LineTool(chain: false); l.Click(new SketchInput(sk, new Vector2d(10, 0), null)); l.Click(new SketchInput(sk, new Vector2d(10, 20), null));
        var c = new LineTool(chain: false); c.Click(new SketchInput(sk, new Vector2d(0, -5), null)); c.Click(new SketchInput(sk, new Vector2d(0, 25), null));
        sk.Entities.OfType<SkLine>().Last().Role = SkRole.Centerline;
    });
    var sr = new SurfaceFeature { Mode = SurfaceKind.Revolve, SketchId = rv.Id }; part.AddFeature(sr); s.Document.RebuildDirty();
    Console.WriteLine($"  surface revolve: error {sr.Error}, area {(sr.Error == null ? QArea(sr.Key) : 0):F4}");
    Require(sr.Error == null && Math.Abs(QArea(sr.Key) - 2 * Math.PI * 200) < 1e-3 * 1256, "surface revolve");
    var th3 = new QuiltSolidFeature { Source = sr.Key, Thickness = new ValueRef(2) }; part.AddFeature(th3); s.Document.RebuildDirty();
    Console.WriteLine($"  thicken cylinder: error {th3.Error}, volume {part.Volume:F4} want {Math.PI * 36 * 20:F4}; parent faces {string.Join(",", part.Quilts.FirstOrDefault()?.Parent.Faces.Select(f => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name) ?? new string[0])}; {string.Join(" | ", part.Warnings.TakeLast(4))}");
    Require(th3.Error == null && Math.Abs(part.Volume - Math.PI * 36 * 20) < 1e-6 * 2262, "thicken cylinder");
    part.RemoveFeature(th3); part.RemoveFeature(sr); s.Document.RebuildDirty();
    w.Close();

    // брусок: копия верхней грани и смещение на 5; замена верхней грани заливкой на высоте 15
    var s2 = DocumentSession.NewPart(); var w2 = Open(s2); var p2 = s2.Part!;
    var b = DrawSketch(w2, p2, p2.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); p2.AddFeature(e); s2.Document.RebuildDirty(); p2.Rebuild(null);
    var top = p2.Bodies[0].Faces.First(f => Refs.FacePlane(p2, f.Id) is { } pl && pl.N.Z > 0.99);
    var cp = new SurfaceFeature { Mode = SurfaceKind.Copy }; cp.OnFaces.Add(Parametric3D.Model.Features.FaceRefs.Inside(top)); p2.AddFeature(cp);
    var of = new QuiltEditFeature { Mode = QuiltEditKind.Offset, Distance = new ValueRef(5) }; of.Sources.Add(cp.Key); p2.AddFeature(of); s2.Document.RebuildDirty();
    var oq = p2.Quilts.First(q => q.Key == of.Key);
    var oz = oq.Faces.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Select(c => c.Start.P.Z).ToList();
    Console.WriteLine($"  offset copy: error {cp.Error}/{of.Error}, area {Parametric3D.Model.Features.QuiltUtilT.Area(oq):F4}, z {oz.Min():F4}..{oz.Max():F4}");
    Require(cp.Error == null && of.Error == null && Math.Abs(Parametric3D.Model.Features.QuiltUtilT.Area(oq) - 800) < 1e-6 && oz.All(z => Math.Abs(z - 15) < 1e-9), "offset surface");
    p2.RemoveFeature(of); p2.RemoveFeature(cp);
    var pl15 = new DatumPlane("P15", Parametric3D.Kernel.Frame3.XY.Translated(new Vector3d(0, 0, 15))); p2.Add(pl15);
    var big = DrawSketch(w2, p2, pl15, sk => Rect(sk, -10, -10, 50, 30));
    var f15 = new SurfaceFeature { Mode = SurfaceKind.Fill, SketchId = big.Id }; p2.AddFeature(f15); s2.Document.RebuildDirty(); p2.Rebuild(null);
    top = p2.Bodies[0].Faces.First(f => Refs.FacePlane(p2, f.Id) is { } pl && pl.N.Z > 0.99 && Math.Abs(pl.P.Z - 10) < 1e-9);
    var rp = new QuiltSolidFeature { Mode = QuiltSolidKind.ReplaceFace, Source = f15.Key }; rp.OnFaces.Add(Parametric3D.Model.Features.FaceRefs.Inside(top)); p2.AddFeature(rp); s2.Document.RebuildDirty();
    Console.WriteLine($"  replace face: error {rp.Error}, volume {p2.Volume:F6}; {string.Join(" | ", p2.Warnings.Where(x => x.Contains("dbg")))}");
    Require(rp.Error == null && Math.Abs(p2.Volume - 12000) < 1e-6, "replace face");
    // вставка тела: копия бруска со сдвигом по X на 100 — объём удваивается
    var step = Parametric3D.Model.Exchange.Write(p2, Parametric3D.Model.Exchange.Format.Step);
    var ib = new InsertBodyFeature { Step = step, SourceName = "box", Offset = new Vector3d(100, 0, 0) }; p2.AddFeature(ib); s2.Document.RebuildDirty();
    Console.WriteLine($"  insert body: error {ib.Error}, volume {p2.Volume:F4}");
    Require(ib.Error == null && Math.Abs(p2.Volume - 24000) < 1e-3, "insert body");
    var json = Parametric3D.Model.DocumentFile.Save(p2, s2.Document.ScopeFor(p2));
    var (p3, _) = Parametric3D.Model.DocumentFile.Load(json);
    Require(p3.Features.OfType<QuiltSolidFeature>().Count() == 1 && p3.Features.OfType<InsertBodyFeature>().Single().Step.Length == step.Length, "saved");
    w2.Close();

    // переходы
    var s4 = DocumentSession.NewPart(); var w4 = Open(s4); var p4 = s4.Part!;
    var a = DrawSketch(w4, p4, p4.PlaneXY, sk => Rect(sk, -10, -10, 10, 10));
    var p40 = new DatumPlane("P40", Parametric3D.Kernel.Frame3.FromNormal(new Vector3d(0, 0, 40), Vector3d.UnitZ)); p4.Add(p40);
    var b4 = DrawSketch(w4, p4, p40, sk => Rect(sk, -5, -5, 5, 5));
    var bb = new BlendFeature { Mode = BlendKind.Boundary }; bb.SectionSketchIds.Add(a.Id); bb.SectionSketchIds.Add(b4.Id); p4.AddFeature(bb); s4.Document.RebuildDirty();
    Console.WriteLine($"  boundary blend: error {bb.Error}, volume {p4.Volume:F4}");
    Require(bb.Error == null && Math.Abs(p4.Volume - 40.0 / 3 * 700) < 1e-6 * 9333, "boundary blend");
    p4.RemoveFeature(bb); s4.Document.RebuildDirty();
    // с протяжкой: круги r5 и r2 на концах прямой траектории 40 → усечённый конус
    var c0 = DrawSketch(w4, p4, p4.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(5, 0), null)); });
    var c1 = DrawSketch(w4, p4, p40, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(2, 0), null)); });
    var path = DrawSketch(w4, p4, p4.PlaneXZ, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, Vector2d.Zero, null)); l.Click(new SketchInput(sk, new Vector2d(0, 40), null)); });
    var sb = new BlendFeature { Mode = BlendKind.Swept, PathSketchId = path.Id }; sb.SectionSketchIds.Add(c0.Id); sb.SectionSketchIds.Add(c1.Id); p4.AddFeature(sb); s4.Document.RebuildDirty();
    double cone = Math.PI / 3 * 40 * (25 + 10 + 4);
    Console.WriteLine($"  swept blend: error {sb.Error}, volume {p4.Volume:F4} want {cone:F4}");
    Require(sb.Error == null && Math.Abs(p4.Volume - cone) < 0.01 * cone, "swept blend");
    p4.RemoveFeature(sb); s4.Document.RebuildDirty();
    // вращением: квадраты 5×5 на радиусе 20–25 в плоскостях XZ и YZ (90°) — по Паппу 25·(π/2)·22.5
    var q0 = DrawSketch(w4, p4, p4.PlaneXZ, sk => { Rect(sk, 20, 0, 25, 5); var l = new LineTool(chain: false); l.Click(new SketchInput(sk, new Vector2d(0, -5), null)); l.Click(new SketchInput(sk, new Vector2d(0, 10), null)); sk.Entities.OfType<SkLine>().Last().Role = SkRole.Centerline; });
    var q1 = DrawSketch(w4, p4, p4.PlaneYZ, sk => Rect(sk, 20, 0, 25, 5));
    var rb = new BlendFeature { Mode = BlendKind.Rotational }; rb.SectionSketchIds.Add(q0.Id); rb.SectionSketchIds.Add(q1.Id); p4.AddFeature(rb); s4.Document.RebuildDirty();
    double pap = 25 * Math.PI / 2 * 22.5;
    Console.WriteLine($"  rotational blend: error {rb.Error}, volume {p4.Volume:F4} want {pap:F4}");
    Require(rb.Error == null && Math.Abs(p4.Volume - pap) < 0.02 * pap, "rotational blend");
    w4.Close();
});

// u13g: сборка — компоненты, ограничения, соединения, массивы, зеркало, спецификация, разнесённый вид, файл, отмена
Part CubePart(string name, double side) { var p = new Part(name); TestGeometry.AddCube(p, side); return p; }
Part CylPart(string name, double r, double h)
{
    var p = new Part(name);
    var sk = new Sketch("S", p.PlaneXY.Frame); sk.SetBasePlane(p.PlaneXY.Frame, p.PlaneXY.Id, "XY"); p.Add(sk);
    sk.AddEntity(new SkCircle(new SkPoint(0, 0), r));
    var e = new ExtrudeFeature("Cyl") { SketchId = sk.Id }; e.Side1.Distance = new ValueRef(h); p.AddFeature(e);
    return p;
}
Check("UI-U13G-ASSEMBLY", () =>
{
    var s = DocumentSession.NewAssembly(); var w = Open(s);
    Require(s.IsAssembly && s.Part == null, "assembly mode");
    var a = AssemblyCommands.AddComponent(w, CubePart("Основание", 40), "add A");
    var b = AssemblyCommands.AddComponent(w, CubePart("Кубик", 20), "add B");
    Require(a.Fixed && !b.Fixed && b.Translation.X > 20, $"placement {b.Translation}");
    Vector3d Wc(Part p) => AssemblyCommands.ToWorld(p, (p.Bounds.Min + p.Bounds.Max) / 2);
    PickRef Face(Part p, Vector3d n) { var f = p.Bodies[0].Faces.First(x => Refs.FacePlane(p, x.Id) is { } pl && (pl.N - n).Length < 1e-9); return new PickRef(f.Id, PickKind.Face, p, null); }
    // совпадение: низ кубика на верх основания; затем две боковые и вторая пара — кубик встаёт в угол
    w.ViewPort.Selection.Clear(); Select(w, Face(b, -Vector3d.UnitZ)); Select(w, Face(a, Vector3d.UnitZ));
    AssemblyCommands.Constrain(w, AssemblyCommands.MateCmd.Coincident);
    w.ViewPort.Selection.Clear(); Select(w, Face(b, -Vector3d.UnitX)); Select(w, Face(a, -Vector3d.UnitX));
    var plan = AssemblyCommands.Plan(AssemblyCommands.MateCmd.Distance, AssemblyCommands.GeoOf(Face(b, -Vector3d.UnitX))!.Value, AssemblyCommands.GeoOf(Face(a, -Vector3d.UnitX))!.Value, 0, out var why);
    // выравнивание боковых: плоскости одного направления — «перевернуть» совпадение
    var mate = AssemblyCommands.Plan(AssemblyCommands.MateCmd.Coincident, AssemblyCommands.GeoOf(Face(b, -Vector3d.UnitX))!.Value, AssemblyCommands.GeoOf(Face(a, -Vector3d.UnitX))!.Value, 0, out why)!;
    mate[0].C.Kind = Parametric3D.Model.Mates.ConstraintKind.Align;
    var mate2 = AssemblyCommands.Plan(AssemblyCommands.MateCmd.Coincident, AssemblyCommands.GeoOf(Face(b, -Vector3d.UnitY))!.Value, AssemblyCommands.GeoOf(Face(a, -Vector3d.UnitY))!.Value, 0, out why)!;
    mate2[0].C.Kind = Parametric3D.Model.Mates.ConstraintKind.Align;
    AssemblyCommands.Apply(w, mate.Concat(mate2).ToList(), "align");
    var bc = Wc(b);
    Console.WriteLine($"  cube placed: centre {bc}, constraints {b.Constraints.Count}");
    Require((bc - new Vector3d(-10, -10, 30)).Length < 1e-3, $"cube in the corner {bc}");
    // отмена последнего шага — ограничений два → одно
    s.Undo(); var b1 = s.Document.Root.Children.OfType<Part>().First(p => p.Name == "Кубик");
    Require(b1.Constraints.Count == 1, $"undo constraints {b1.Constraints.Count}");
    s.Redo(); b = s.Document.Root.Children.OfType<Part>().First(p => p.Name == "Кубик"); a = s.Document.Root.Children.OfType<Part>().First(p => p.Name == "Основание");
    Require(b.Constraints.Count == 3, "redo");
    // соединение «шарнир»: вал R5 в цилиндр R5 (ось к оси)
    var shaft = AssemblyCommands.AddComponent(w, CylPart("Вал", 5, 30), "add shaft");
    var hub = AssemblyCommands.AddComponent(w, CylPart("Втулка", 5, 10), "add hub");
    s.Document.RebuildDirty();
    PickRef Side(Part p) { var f = p.Bodies[0].Faces.First(x => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(x, 0) is { } rr && Math.Abs(rr - 5) < 1e-9); return new PickRef(f.Id, PickKind.Face, p, null); }
    shaft.Fixed = true;
    w.ViewPort.Selection.Clear(); Select(w, Side(hub)); Select(w, Side(shaft));
    AssemblyCommands.Joint(w, Parametric3D.Model.Mates.MateType.Pin);
    var hc = AssemblyCommands.ToWorld(hub, Vector3d.Zero); var sc = AssemblyCommands.ToWorld(shaft, Vector3d.Zero);
    var hz = AssemblyCommands.DirToWorld(hub, Vector3d.UnitZ);
    Console.WriteLine($"  pin: hub origin {hc}, shaft origin {sc}, hub axis {hz}");
    Require(hub.Mates.Count == 1 && Math.Abs(Math.Abs(hz.Z) - 1) < 1e-4 && Math.Abs(hc.X - sc.X) < 1e-3 && Math.Abs(hc.Y - sc.Y) < 1e-3, "pin joint");
    // массив кубика: 3 шт. с шагом 30 по Y; спецификация: «Кубик» ×3
    var copies = AssemblyCommands.Pattern(w, b, 3, new Vector3d(0, 30, 0));
    Require(copies.Count == 2 && Math.Abs(copies[1].Translation.Y - b.Translation.Y - 60) < 1e-3, "pattern");
    var bom = AssemblyCommands.Bom(w);
    Console.WriteLine("  bom: " + string.Join("; ", bom.Select(r => $"{r.Name} x{r.Count}")));
    Require(bom.Any(r => r.Name == "Кубик" && r.Count == 3) && bom.Any(r => r.Name == "Основание" && r.Count == 1), "bom");
    // зеркало кубика относительно плоскости YZ сборки: объём тот же, центр по X — с другой стороны
    var m = AssemblyCommands.Mirror(w, b, Vector3d.Zero, Vector3d.UnitX); s.Document.RebuildDirty();
    var mc = Wc(m); bc = Wc(b);
    Console.WriteLine($"  mirror: centre {mc} (source {bc}), volume {m.Volume:F3}");
    Require(Math.Abs(mc.X + bc.X) < 1e-3 && Math.Abs(m.Volume - 8000) < 1e-3, "mirror component");
    // разнесённый вид: туда и обратно
    var t0 = b.Translation;
    AssemblyCommands.ToggleExplode(w); Require((b.Translation - t0).Length > 1, "exploded");
    AssemblyCommands.ToggleExplode(w); Require((b.Translation - t0).Length < 1e-5, "collapsed");
    // запись и чтение сборки
    var path = Path.Combine(Path.GetTempPath(), "u13g_test.p3a");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path);
    var parts2 = s2.Document.Root.Children.OfType<Part>().ToList();
    Console.WriteLine($"  reopened: {parts2.Count} components, fixed {parts2.Count(p => p.Fixed)}, constraints {parts2.Sum(p => p.Constraints.Count)}, joints {parts2.Sum(p => p.Mates.Count)}");
    Require(s2.IsAssembly && parts2.Count == s.Document.Root.Children.OfType<Part>().Count() && parts2.Sum(p => p.Constraints.Count) == s.Document.Root.Children.OfType<Part>().Sum(p => p.Constraints.Count) && parts2.Sum(p => p.Mates.Count) == 1, "assembly file");
    // правка в контексте: операции — над компонентом
    AssemblyCommands.EditInContext(w, b);
    Require(s.Part == b && w.ViewPort.ActivePart == b, "edit in context");
    AssemblyCommands.EditInContext(w, null);
    Require(s.Part == null, "back to assembly");
    // замена компонента: ограничения переходят к новому
    var nb = CubePart("Кубик2", 20);
    AssemblyCommands.Replace(w, b, nb);
    Require(nb.Constraints.Count == 3 && !s.Document.Root.Children.Contains(b), "replace keeps constraints");
    // дерево: компоненты и папка ограничений
    var roots = TreeNode.Build(s.Document, assembly: true);
    Require(roots[0].Children.Any(n => n.Item is InfoItem { Role: "mates" } mm && n.Children.Count >= 4) && roots[0].Children.Count(n => n.Item is Part) >= 5, "assembly tree");
    w.Close();
});

// u13h: FreeForm — документ, заготовки, булевы между телами, перенос/масштаб, разрез, зеркало и симметрия, массивы, шаги
// на своём теле (скругление, push-pull, выравнивание), фиксация шагов, «отменить до шага», запись, «Править в FreeForm»
// с автоматическим объединением и «ссылки изменились».
Check("UI-U13H-FREEFORM", () =>
{
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    Require(part.FreeFormDoc && w.InFreeForm, "freeform document opens in the FreeForm environment");
    Require(CommandRegistry.Instance["ff.box"].Enabled && !CommandRegistry.Instance["op.edit_in_freeform"].Enabled, "freeform commands");
    var node = part.Features.OfType<FreeFormFeature>().Single();
    // брусок 40×30×20 командой (панель), «ОК» — шаг узла, зафиксирован
    CommandRegistry.Instance["ff.box"].Execute();
    Require(w.CurrentOperation is FFPrimitiveOp, "box op");
    w.CommitOperation(); s.Document.RebuildDirty();
    var box = part.Features.OfType<FFPrimitiveFeature>().Single();
    Console.WriteLine($"  box: error {box.Error}, volume {part.Volume:F6}, bodies {string.Join(",", part.BodyNameList)}, frozen {box.Frozen}");
    Require(box.FreeForm == node.Key && box.Frozen && box.Error == null && Math.Abs(part.Volume - 24000) < 1e-6, "box step");
    // правка зафиксированного шага не открывается
    w.ViewPort.Selection.Clear(); w.ViewPort.Selection.Select(new PickRef(box.Id, PickKind.Feature, part, box), additive: false);
    w.EditSelected();
    Require(w.CurrentOperation == null, "frozen step is not editable");
    T Add<T>(T f) where T : Feature { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Require(f.Error == null, $"{f.GetType().Name}: {f.Error}"); return f; }
    // цилиндр R10 H40 — второе тело (FreeForm-документ тела не объединяет)
    var cyl = Add(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(10), B = new ValueRef(40) });
    double vc = Math.PI * 100 * 40;
    Console.WriteLine($"  cylinder: bodies {part.Bodies.Count} ({string.Join(",", part.BodyNameList)}), volume {part.Volume:F6}");
    Require(part.Bodies.Count == 2 && part.BodyNameList.SequenceEqual(new[] { "Тело 1", "Тело 2" }) && Math.Abs(part.Volume - (24000 + vc)) < 1e-6 * vc, "two bodies");
    // вычесть: Тело 1 − Тело 2 → отверстие R10 сквозь брусок (высота 20)
    var sub = Add(new FFBooleanFeature { Op = FFBool.Subtract, Target = "Тело 1", Tools = { "Тело 2" } });
    double v1 = 24000 - Math.PI * 100 * 20;
    Console.WriteLine($"  subtract: bodies {part.Bodies.Count}, volume {part.Volume:F6} want {v1:F6}");
    Require(part.Bodies.Count == 1 && Math.Abs(part.Volume - v1) < 1e-6 * v1, "subtract");
    // копия переносом на 100 по X и масштаб копии ×0.5 → объём копии /8
    var mv = Add(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(100), Copy = true });
    // имена не переиспользуются: копия — «Тело 3» («Тело 2» был цилиндр)
    var sc = Add(new FFTransformFeature { Mode = FFXform.Scale, BodyNames = { "Тело 3" }, Factor = new ValueRef(0.5), AxisAt = new Vector3d(100, 0, 0) });
    Console.WriteLine($"  copy + scale: bodies {string.Join(",", part.BodyNameList)}, volume {part.Volume:F6}");
    Require(part.Bodies.Count == 2 && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "copy and exact scale");
    // разрез тела 1 плоскостью z = 5 → два тела, объём тот же
    var sp = Add(new FFSplitFeature { Body = "Тело 1", PlaneAt = new Vector3d(0, 0, 5), Normal = Vector3d.UnitZ });
    Console.WriteLine($"  split: bodies {string.Join(",", part.BodyNameList)}, volume {part.Volume:F6}");
    Require(part.Bodies.Count == 3 && part.BodyNameList.Contains("Тело 1.2") && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "split");
    // объединить части обратно
    var un = Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 1.2" } });
    Require(part.Bodies.Count == 2 && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "union back");
    // зеркальная копия тела 3 через плоскость x = 0 и прямоугольный массив тела 1 (3 шт. по Y с шагом 50)
    var mi = Add(new FFMirrorFeature { Mode = FFMirrorMode.Copy, BodyNames = { "Тело 3" }, PlaneAt = Vector3d.Zero, Normal = Vector3d.UnitX });
    var ar = Add(new FFArrayFeature { Kind = FFArrayKind.Linear, BodyNames = { "Тело 1" }, Count = new ValueRef(3), Step = new Vector3d(0, 50, 0) });
    Console.WriteLine($"  mirror + array: bodies {part.Bodies.Count} ({string.Join(",", part.BodyNameList)}), volume {part.Volume:F3}");
    Require(part.Bodies.Count == 5 && Math.Abs(part.Volume - v1 * (3 + 0.25)) < 1e-6 * v1, "mirror and array");
    // скругление ребра тела «Тело 1 [2]» (шаг на своём теле): ребро бруска длиной 40 вдоль X, R3
    int bi = part.BodyNameList.IndexOf("Тело 1 [2]");
    var edge = part.Bodies[bi].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-6 && Math.Abs(e.V1.P.Z - 20) < 1e-6 && Math.Abs(Math.Abs(e.V0.P.X - e.V1.P.X) - 40) < 1e-6 && Math.Abs(e.V0.P.Y - 65) < 1e-6);
    var rf = new RoundFeature("R") { Radius = new ValueRef(3), FFBody = "Тело 1 [2]" }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge));
    double before = part.Volume;
    Add(rf);
    double cut = (1 - Math.PI / 4) * 9 * 40;
    Console.WriteLine($"  fillet on one body: volume {part.Volume:F4}, removed {before - part.Volume:F4} want {cut:F4}, bodies {part.Bodies.Count}");
    Require(part.Bodies.Count == 5 && Math.Abs(before - part.Volume - cut) < 1e-3, "fillet in its own body");
    // push-pull: верх тела «Тело 1 [3]» +5 (брусок с отверстием: + 5 × (1200 − 100π))
    int b3 = part.BodyNameList.IndexOf("Тело 1 [3]");
    var top = part.Bodies[b3].Faces.First(f => Parametric3D.Kernel.Ops.PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.99);
    var pp = new FaceEditFeature { Mode = FaceEditMode.Offset, Distance = new ValueRef(5), FFBody = "Тело 1 [3]" };
    pp.OnFaces.Add(Refs.FacePoint(part, top.Id)!.Value);
    before = part.Volume; Add(pp);
    double grow = 5 * (1200 - Math.PI * 100);
    Console.WriteLine($"  push-pull: added {part.Volume - before:F4} want {grow:F4}");
    Require(Math.Abs(part.Volume - before - grow) < 1e-3, "push-pull");
    // выровнять: верх того же тела — к плоскости z = 30 (было 25)
    b3 = part.BodyNameList.IndexOf("Тело 1 [3]");
    top = part.Bodies[b3].Faces.First(f => Parametric3D.Kernel.Ops.PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.99);
    var al = new FFAlignFaceFeature { PlaneAt = new Vector3d(0, 0, 30), Normal = Vector3d.UnitZ, FFBody = "Тело 1 [3]" };
    al.OnFaces.Add(Refs.FacePoint(part, top.Id)!.Value);
    before = part.Volume; Add(al);
    Console.WriteLine($"  align: added {part.Volume - before:F4} want {grow:F4}");
    Require(Math.Abs(part.Volume - before - grow) < 1e-3, "align face");
    // дерево: узел FreeForm со своим журналом шагов
    var roots = TreeNode.Build(s.Document);
    var ffNode = roots[0].SelfAndDescendants().First(n => n.Item is FreeFormFeature);
    Console.WriteLine($"  tree: freeform node with {ffNode.Children.Count} steps, icon {ffNode.IconFile}");
    Require(ffNode.Children.Count == part.Features.Count - 1 && ffNode.IconFile == "tree_freeform_ico.svg" && ffNode.Children[0].IconFile == "tree_ff_step_ico.svg", "freeform tree");
    // запись и чтение: те же тела и признаки шагов
    double vAll = part.Volume;
    var path = Path.Combine(Path.GetTempPath(), "u13h_ff.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty(); var p2 = s2.Part!;
    Console.WriteLine($"  reopened: freeform {p2.FreeFormDoc}, steps {p2.Features.Count}, volume {p2.Volume:F4}, bodies {p2.Bodies.Count}");
    Require(p2.FreeFormDoc && p2.Features.Count == part.Features.Count && p2.Features.Skip(1).All(f => f.FreeForm == node.Key && f.Frozen)
            && p2.Features.OfType<RoundFeature>().Single().FFBody == "Тело 1 [2]" && Math.Abs(p2.Volume - vAll) < 1e-6 * vAll && p2.Bodies.Count == 5, "save and load");
    // «отменить до шага» (зеркало): зеркало и всё после него убираются одним шагом отмены
    FreeFormCommands.UndoTo(w, mi); s.Document.RebuildDirty();
    Console.WriteLine($"  undo to mirror: steps {part.Features.Count}, bodies {part.Bodies.Count}");
    Require(!part.Features.Contains(mi) && !part.Features.Contains(al) && part.Bodies.Count == 2 && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "undo to step");
    s.Undo(); s.Document.RebuildDirty();
    Require(s.Part!.Features.Count == p2.Features.Count, "undo of undo-to");
    w.Close();

    // «Править в FreeForm» параметрической детали: куб 40; шар R10 в углу — после «Готово» тела объединяются
    var s3 = DocumentSession.NewPart(); var w3 = Open(s3); var p3 = s3.Part!;
    var sk = DrawSketch(w3, p3, p3.PlaneXY, k => Rect(k, 0, 0, 40, 40));
    var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(40); p3.AddFeature(ex); s3.Document.RebuildDirty();
    FreeFormCommands.EditInFreeForm(w3);
    Require(w3.InFreeForm && p3.Features.Last() is FreeFormFeature { InputSig.Length: > 0 }, "edit in freeform");
    var ffn = (FreeFormFeature)p3.Features.Last();
    var sph = new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(10), Origin = new Vector3d(40, 40, 40), FreeForm = ffn.Key, Frozen = true };
    p3.AddFeature(sph); s3.Document.RebuildDirty();
    Console.WriteLine($"  in freeform: bodies {p3.Bodies.Count} ({string.Join(",", p3.BodyNameList)}), volume {p3.Volume:F3}");
    Require(p3.Bodies.Count == 2 && p3.BodyNameList[0] == "Исходное тело", "separate while editing");
    FreeFormCommands.Exit(w3); s3.Document.RebuildDirty();
    double vu = 64000 + 4.0 / 3 * Math.PI * 1000 * 7 / 8;
    Console.WriteLine($"  done: bodies {p3.Bodies.Count}, volume {p3.Volume:F3} want {vu:F3}");
    Require(!w3.InFreeForm && p3.Bodies.Count == 1 && Math.Abs(p3.Volume - vu) < 1e-3, "auto union on embedding");
    // тело выше в дереве изменилось: шаги не перестраиваются, узел — «ссылки изменились»; «Обновить ссылки» — по новой форме
    ex.Side1.Distance = new ValueRef(60); p3.NotifyChanged(ex); s3.Document.RebuildDirty();
    Console.WriteLine($"  upstream changed: refs changed {ffn.RefsChanged}, volume {p3.Volume:F3}");
    Require(ffn.RefsChanged && Math.Abs(p3.Volume - vu) < 1e-2, "references changed — stored input");
    Require(TreeNode.StateOf(ffn, p3).File == "state_refs_changed_ico.svg", "refs changed state");
    w3.ViewPort.Selection.Clear(); w3.ViewPort.Selection.Select(new PickRef(ffn.Id, PickKind.Feature, p3, ffn), additive: false);
    FreeFormCommands.UpdateRefs(w3, ffn); s3.Document.RebuildDirty();
    // брусок 40×40×60, центр шара (40,40,40) на вертикальном ребре: внутри — четверть шара
    double vn = 96000 + 4.0 / 3 * Math.PI * 1000 * 3 / 4;
    Console.WriteLine($"  update refs: refs changed {ffn.RefsChanged}, volume {p3.Volume:F3} want {vn:F3}");
    Require(!ffn.RefsChanged && Math.Abs(p3.Volume - vn) < 1e-2, "update references");
    // предложенное исправление: отказ операции — варианты
    var sug = FreeFormCommands.Suggestions(new RoundFeature("x") { Error = "Round: the radius 500 is too large" });
    Require(sug.Any(x => x.Command == "edit.feature") && sug.Any(x => x.Command == "edit.suppress"), "fix suggestions");
    var lost = FreeFormCommands.Suggestions(new RoundFeature("y") { Error = "Round: the edge no longer exists" });
    Require(lost[0].Command == "tree.replace_refs", "lost reference suggestion");
    w3.Close();
});

// u13i: кривые FreeForm и тела из кривых — интерполяция и перестроение NURBS, спираль, эквидистанта, скругление углов,
// соединение, продление, мостик, обрезка, изопараметрическая, вставка узла и вес, труба, отпечаток, команды и запись.
Check("UI-U13I-CURVES", () =>
{
    double Len(IReadOnlyList<Vector3d> q) { double l = 0; for (int i = 1; i < q.Count; i++) l += (q[i] - q[i - 1]).Length; return l; }
    // интерполяция: кривая проходит точки; перестроение окружности 8 опорными — отклонение < 0.5 % радиуса
    var pts = new List<Vector3d> { new(0, 0, 0), new(10, 5, 0), new(20, -3, 2), new(30, 4, 0), new(40, 0, 5) };
    var ip = FFCurves.Interpolate(pts, 3);
    var ipPts = FFCurves.Sample(ip, 400);
    double dev = FFCurves.Deviation(pts, ipPts);
    Console.WriteLine($"  interpolate: control points {ip.ControlPoints.Count}, deviation {dev:E2}");
    Require(dev < 1e-3 && ip.ControlPoints.Count == pts.Count, "interpolation through points");
    var circle = Enumerable.Range(0, 201).Select(i => new Vector3d(20 * Math.Cos(i * Math.PI / 100), 20 * Math.Sin(i * Math.PI / 100), 0)).ToList();
    var half = circle.Take(101).ToList();
    var fit = FFCurves.Fit(half, 8, 3);
    double fdev = FFCurves.Deviation(half, FFCurves.Sample(fit, 400));
    Console.WriteLine($"  rebuild half circle R20 with 8 control points: deviation {fdev:E2}");
    Require(fdev < 0.1 && fit.ControlPoints.Count == 8 && (fit.PointAt(0) - half[0]).Length < 1e-9 && (fit.PointAt(1) - half[^1]).Length < 1e-9, "rebuild");
    // вставка узла не меняет форму; вес точки — меняет
    var ins = fit.WithUnitKnots().InsertKnot(0.37);
    double kdev = FFCurves.Deviation(FFCurves.Sample(fit, 200), FFCurves.Sample(ins, 400));
    Require(ins.ControlPoints.Count == 9 && kdev < 1e-6, $"insert knot keeps the shape ({kdev})");
    // спираль: 3 витка R20, шаг 10 — высота 30, радиус 20
    var sp = FFCurves.Spiral(Parametric3D.Kernel.Frame3.XY, 20, 20, 10, 3);
    Require(Math.Abs(sp[^1].Z - 30) < 1e-9 && sp.All(p => Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 20) < 1e-9), "spiral");
    // эквидистанта квадрата 20 наружу на 2 → периметр 96; скругление углов R2 → 4·16 + 4π
    var sq = new List<Vector3d> { new(0, 0, 0), new(20, 0, 0), new(20, 20, 0), new(0, 20, 0), new(0, 0, 0) };
    var off = FFCurves.Offset(sq, Vector3d.UnitZ, -2);
    Console.WriteLine($"  offset square: perimeter {Len(off):F6}");
    Require(Math.Abs(Len(off) - 96) < 1e-9, "offset");
    var fil = FFCurves.FilletCorners(sq, 2, out int nk, 64);
    Console.WriteLine($"  fillet corners: {nk}, length {Len(fil):F4} want {64 + 4 * Math.PI:F4}");
    Require(nk == 4 && Math.Abs(Len(fil) - (64 + 4 * Math.PI)) < 0.01, "curve fillet");
    // соединение, продление, мостик, обрезка
    var j = FFCurves.Join(new List<IReadOnlyList<Vector3d>> { new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, new[] { new Vector3d(20, 5, 0), new Vector3d(10, 0, 0) } }, 1e-6);
    Require(j.Count == 3 && (j[^1] - new Vector3d(20, 5, 0)).Length < 1e-12, "join");
    var ex = FFCurves.Extend(new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, 5, false, true, false);
    Require((ex[^1] - new Vector3d(15, 0, 0)).Length < 1e-12, "extend line");
    var arcEx = FFCurves.Extend(half, 10, false, true, true);
    Require(arcEx.All(p => Math.Abs(p.Length - 20) < 1e-6) && Math.Abs(Len(arcEx) - Len(half) - 10) < 0.01, "extend along the arc");
    var br = FFCurves.Bridge(new[] { new Vector3d(-10, 0, 0), new Vector3d(0, 0, 0) }, new[] { new Vector3d(20, 10, 0), new Vector3d(30, 10, 0) }, g2: false);
    var t0 = (br[1] - br[0]).Normalized(); var t1 = (br[^1] - br[^2]).Normalized();
    Require((br[0] - Vector3d.Zero).Length < 1e-12 && (br[^1] - new Vector3d(20, 10, 0)).Length < 1e-12 && t0.X > 0.999 && t1.X > 0.999, "bridge G1 tangents");
    var br2 = FFCurves.Bridge(half, new[] { new Vector3d(-40, 30, 0), new Vector3d(-50, 30, 0) }, g2: true);
    Require(br2.Count == 65, "bridge G2");
    var tr = FFCurves.TrimByPlane(new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, new Vector3d(4, 0, 0), Vector3d.UnitX, keepStart: true);
    Require((tr[^1] - new Vector3d(4, 0, 0)).Length < 1e-12, "trim by plane");
    var tc = FFCurves.TrimByCurve(new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, new[] { new Vector3d(6, -5, 0), new Vector3d(6, 5, 0) }, false, 1e-6);
    Require((tc[0] - new Vector3d(6, 0, 0)).Length < 1e-12 && tc.Count == 2, "trim by curve");

    // в окне: FreeForm-документ; опорные кривые командами; труба вдоль прямой и спирали; отпечаток на грани бруска
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    var dps = new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 50) }.Select((p, i) => { var d = new DatumPoint($"P{i}", p); part.Add(d); return d; }).ToList();
    w.ViewPort.Selection.Clear(); foreach (var d in dps) Select(w, new PickRef(d.Id, PickKind.DatumPoint, part, d));
    var cop = new FFCurveOp { Mode = FFCurveMode.Nurbs }; w.StartOperation(cop); w.CommitOperation();
    var line = part.Children.OfType<DatumCurve>().Last();
    Require(line.IsNurbs && Math.Abs(line.Length - 50) < 1e-9, $"nurbs curve command (length {line.Length})");
    var pipe = new FFPipeFeature { CurveName = line.Name, Radius = new ValueRef(3), Inner = new ValueRef(2), FreeForm = node.Key, Frozen = true };
    part.AddFeature(pipe); s.Document.RebuildDirty();
    Console.WriteLine($"  pipe R3/r2 L50: error {pipe.Error}, volume {part.Volume:F4} want {Math.PI * 5 * 50:F4}");
    Require(pipe.Error == null && Math.Abs(part.Volume - Math.PI * 5 * 50) < 0.02 * Math.PI * 5 * 50, "pipe");
    // спираль командой (центр — начало плоскости построения), труба R1 вдоль неё: объём ≈ π·1²·длина
    var spo = new FFCurveOp { Mode = FFCurveMode.Spiral }; w.ViewPort.Selection.Clear(); w.StartOperation(spo); w.CommitOperation();
    var helix = part.Children.OfType<DatumCurve>().Last();
    double hl = helix.Length;
    var pipe2 = new FFPipeFeature { CurveName = helix.Name, Radius = new ValueRef(1), FreeForm = node.Key, Frozen = true };
    double v0 = part.Volume;
    part.AddFeature(pipe2); s.Document.RebuildDirty();
    Console.WriteLine($"  pipe along helix (length {hl:F2}): error {pipe2.Error}, volume {part.Volume - v0:F3} want {Math.PI * hl:F3}, bodies {part.Bodies.Count}");
    Require(pipe2.Error == null && Math.Abs(part.Volume - v0 - Math.PI * hl) < 0.03 * Math.PI * hl && part.Bodies.Count == 2, "pipe along a helix");
    // перестроение спирали на месте (12 опорных) и «Отмена» — кривая как была
    var before = helix.Points.ToList();
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(helix.Id, PickKind.DatumAxis, part, helix));
    var rb = new FFCurveOp { Mode = FFCurveMode.Rebuild }; w.StartOperation(rb);
    Require(helix.IsNurbs && helix.Control!.Count == 8, "rebuild in place (preview)");
    w.CancelOperation();
    Require(!helix.IsNurbs && helix.Points.SequenceEqual(before), "cancel restores the curve");
    // брусок 40×30×20 и отпечаток квадрата 10 на верхней грани: грань делится, объём тот же
    var box = new FFPrimitiveFeature { Shape = FFPrimitive.Box, Origin = new Vector3d(100, 0, 0), FreeForm = node.Key, Frozen = true };
    part.AddFeature(box); s.Document.RebuildDirty();
    int bi = part.BodyNameList.IndexOf("Тело 3");
    Console.WriteLine($"  box: error {box.Error}, bodies {string.Join(",", part.BodyNameList)}");
    int faces0 = part.Bodies[bi].Faces.Count();
    var sqc = new DatumCurve("Квадрат", new[] { new Vector3d(95, -5, 20), new Vector3d(105, -5, 20), new Vector3d(105, 5, 20), new Vector3d(95, 5, 20), new Vector3d(95, -5, 20) }); part.Add(sqc);
    var im = new FFImprintFeature { CurveName = "Квадрат", FFBody = "Тело 3", FreeForm = node.Key, Frozen = true };
    var topF = part.Bodies[bi].Faces.First(f => Parametric3D.Kernel.Ops.PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.99);
    im.OnFaces.Add(Refs.FacePoint(part, topF.Id)!.Value);
    double vb = part.Volume;
    part.AddFeature(im); s.Document.RebuildDirty();
    bi = part.BodyNameList.IndexOf("Тело 3");
    Console.WriteLine($"  imprint closed square: error {im.Error}, faces {faces0} → {part.Bodies[bi].Faces.Count()}, volume change {part.Volume - vb:E2}");
    Require(im.Error == null && part.Bodies[bi].Faces.Count() == faces0 + 1 && Math.Abs(part.Volume - vb) < 1e-6, "imprint closed curve");
    // незамкнутая: прямая поперёк верхней грани (x = 110)
    var cross = new DatumCurve("Поперёк", new[] { new Vector3d(110, -10, 20), new Vector3d(110, 10, 20) }); part.Add(cross);
    var im2 = new FFImprintFeature { CurveName = "Поперёк", FFBody = "Тело 3", FreeForm = node.Key, Frozen = true };
    im2.OnFaces.Add(new Vector3d(115, 10, 20));
    int faces1 = part.Bodies[bi].Faces.Count();
    part.AddFeature(im2); s.Document.RebuildDirty();
    bi = part.BodyNameList.IndexOf("Тело 3");
    Console.WriteLine($"  imprint open line: error {im2.Error}, faces {faces1} → {part.Bodies[bi].Faces.Count()}, volume change {part.Volume - vb:E2}");
    Require(im2.Error == null && part.Bodies[bi].Faces.Count() == faces1 + 1 && Math.Abs(part.Volume - vb) < 1e-6, "imprint open curve");
    // запись и чтение: NURBS опорной кривой
    var path = Path.Combine(Path.GetTempPath(), "u13i_curves.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty();
    var l2 = s2.Part!.Children.OfType<DatumCurve>().First(c => c.Name == line.Name);
    Require(l2.IsNurbs && l2.Control!.Count == line.Control!.Count && Math.Abs(s2.Part.Volume - part.Volume) < 1e-6 * part.Volume, "save and load curves");
    // изопараметрическая кривая цилиндра R5: точки на радиусе 5
    var cyl = CylPart("Цилиндр", 5, 30); cyl.Rebuild(null);
    var side = cyl.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 5) < 1e-9);
    var iso = FFCurves.Isoparm(side, alongV: true, 0.25);
    Console.WriteLine($"  isoparm on a cylinder: {iso.Count} points, z {iso.Min(p => p.Z):F3}..{iso.Max(p => p.Z):F3}");
    Require(iso.All(p => Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 5) < 1e-6) && iso.Max(p => p.Z) - iso.Min(p => p.Z) > 29, "isoparm");
    w.Close();
});

// u13j: поверхности FreeForm — линейчатая, сетка кривых, заплатка, плоская, мостик краёв, продление, обращение,
// опорные точки, вес, узел, сшивка в тело, отверждение и замена грани NURBS-квилтом, запись.
Check("UI-U13J-SURFACES", () =>
{
    double QArea(Quilt q)
    {
        double a = 0;
        foreach (var f in q.Faces)
        {
            var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
            for (int i = 0; i + 2 < md.Indices.Length; i += 3) { var p0 = mb.PositionsD[md.Indices[i]]; a += Vector3d.Cross(mb.PositionsD[md.Indices[i + 1]] - p0, mb.PositionsD[md.Indices[i + 2]] - p0).Length / 2; }
        }
        return a;
    }
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    T Add<T>(T f) where T : Feature { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Require(f.Error == null, $"{f.GetType().Name}: {f.Error}"); return f; }
    DatumCurve Curve(string n, params Vector3d[] p) { var c = new DatumCurve(n, p); part.Add(c); return c; }
    PickRef CRef(DatumCurve c) => new(c.Id, PickKind.DatumAxis, part, c);
    // линейчатая между отрезками: полоса 40 × √(30² + 10²)
    var a = Curve("A", new(0, 0, 0), new(40, 0, 0)); var b = Curve("B", new(0, 30, 10), new(40, 30, 10));
    w.ViewPort.Selection.Clear(); Select(w, CRef(a)); Select(w, CRef(b));
    var ro = new FFSurfaceOp { Mode = FFSurfMode.Ruled }; w.StartOperation(ro); w.CommitOperation(); s.Document.RebuildDirty();
    var ruled = part.Features.OfType<FFNurbsQuiltFeature>().Last();
    var rq = part.Quilts.Single(q => q.Key == ruled.Key);
    double want = 40 * Math.Sqrt(1000);
    Console.WriteLine($"  ruled: error {ruled.Error}, quilt area {QArea(rq):F4} want {want:F4}, grid {ruled.Def.Nu}×{ruled.Def.Nv}");
    Require(ruled.Error == null && rq.Nurbs != null && Math.Abs(QArea(rq) - want) < 1e-6 * want && ruled.Frozen, "ruled surface");
    // продление края U1 на 10: площадь + 10 × √1000 (вдоль полосы)
    var ext = Add(new FFNurbsQuiltFeature { ReplacesKey = ruled.Key, Def = NurbsDef.Of(FFSurfaces.Extend(rq.Nurbs!, 1, 10)) });
    rq = part.Quilts.Single(q => q.Key == ruled.Key);
    Console.WriteLine($"  extend: area {QArea(rq):F4} want {want * 1.25:F4}");
    Require(Math.Abs(QArea(rq) - want * 1.25) < 1e-3 * want, "extend surface");
    // опорная точка командой (выбрана поверхность → правка поверхности), сдвиг по Z на 5
    var face = rq.Faces.First();
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(face.Id, PickKind.Face, part, null));
    CommandRegistry.Instance["ff.cv_edit"].Execute();
    Require(w.CurrentOperation is FFSurfaceOp, "cv edit dispatches to the surface");
    var cvF = (FFNurbsQuiltFeature)w.CurrentOperation!.EditedFeature!;
    w.CommitOperation(); s.Document.RebuildDirty();
    rq = part.Quilts.Single(q => q.Key == ruled.Key);
    Console.WriteLine($"  cv edit: error {cvF.Error}, replaces {cvF.ReplacesKey == ruled.Key}, cp00 {rq.Nurbs!.ControlPoint(0, 0)}");
    Require(cvF.Error == null && cvF.ReplacesKey == ruled.Key && Math.Abs(rq.Nurbs!.ControlPoint(0, 0).Z - 5) < 1e-9, "cv edit");
    // вставка узла: форма та же; вес: форма меняется; обращение: нормаль наоборот
    var s0 = rq.Nurbs!; var k = s0.InsertKnotU(0.37);
    double kd = 0; for (int i = 0; i <= 10; i++) for (int j = 0; j <= 10; j++) kd = Math.Max(kd, (s0.PointAt(new Vector2d(i / 10.0, j / 10.0)) - k.PointAt(new Vector2d(i / 10.0, j / 10.0))).Length);
    Require(k.CountU == s0.CountU + 1 && kd < 1e-9, $"knot keeps the shape ({kd})");
    var fl = FFSurfaces.Flip(NurbsDef.Of(s0)).Surface;
    var c0 = s0.PointAt(new Vector2d(0.5, 0.5));
    Require(Vector3d.Dot(s0.NormalAt(new Vector2d(0.5, 0.5)), fl.NormalAt(fl.ProjectToUV(c0))) < -0.99, "flip normal");
    // сетка кривых: параболоид z = (x² + y²)/100 по трём кривым U и трём V
    List<Vector3d> Par(Func<double, Vector3d> f) => Enumerable.Range(0, 41).Select(i => f(i)).ToList();
    Vector3d Z(double x, double y) => new(x, y, (x * x + y * y) / 100);
    var us = new[] { 0.0, 20, 40 }.Select(y => (IReadOnlyList<Vector3d>)Par(t => Z(t, y))).ToList();
    var vs = new[] { 0.0, 20, 40 }.Select(x => (IReadOnlyList<Vector3d>)Par(t => Z(x, t))).ToList();
    var net = FFSurfaces.Network(us, vs);
    double ndev = FFSurfaces.Deviation(net, us.Concat(vs).SelectMany(c => c));
    double cdev = (net.PointAt(net.ProjectToUV(Z(10, 10))) - Z(10, 10)).Length;
    Console.WriteLine($"  network: deviation from curves {ndev:E2}, from the paraboloid at (10,10) {cdev:E2}");
    Require(ndev < 0.05 && cdev < 0.2, "network surface");
    // заплатка по квадрату 20 (четыре стороны) — плоская, площадь 400
    var sq = new[] { new Vector3d(0, 0, 50), new(20, 0, 50), new(20, 20, 50), new(0, 20, 50) };
    var sides = Enumerable.Range(0, 4).Select(i => (IReadOnlyList<Vector3d>)new[] { sq[i], sq[(i + 1) % 4] }).ToList();
    var patch = FFSurfaces.Patch(sides);
    var pq = FFSurfaces.Quilt(patch, "p", "patch");
    Console.WriteLine($"  patch: area {QArea(pq):F6}");
    Require(Math.Abs(QArea(pq) - 400) < 1e-6, "coons patch");
    // плоская поверхность по замкнутому квадрату и утолщение 2 → 800
    var loop = Curve("Loop", new(100, 0, 0), new(120, 0, 0), new(120, 20, 0), new(100, 20, 0), new(100, 0, 0));
    var pl = Add(new FFPlanarQuiltFeature { Boundary = { new(100, 0, 0), new(120, 0, 0), new(120, 20, 0), new(100, 20, 0), new(100, 0, 0) } });
    var th = Add(new QuiltSolidFeature { Source = pl.Key, Thickness = new ValueRef(2) });
    Console.WriteLine($"  planar + thicken: volume {part.Volume:F6}");
    Require(Math.Abs(part.Volume - 800) < 1e-6, "planar surface and thicken");
    // сшивка шести плоских граней куба 10 (обход — наружу) → тело 1000
    var cube = new[] {
        new[] { new Vector3d(200, 0, 0), new(200, 10, 0), new(210, 10, 0), new(210, 0, 0) },
        new[] { new Vector3d(200, 0, 10), new(210, 0, 10), new(210, 10, 10), new(200, 10, 10) },
        new[] { new Vector3d(200, 0, 0), new(210, 0, 0), new(210, 0, 10), new(200, 0, 10) },
        new[] { new Vector3d(200, 10, 0), new(200, 10, 10), new(210, 10, 10), new(210, 10, 0) },
        new[] { new Vector3d(200, 0, 0), new(200, 0, 10), new(200, 10, 10), new(200, 10, 0) },
        new[] { new Vector3d(210, 0, 0), new(210, 10, 0), new(210, 10, 10), new(210, 0, 10) } };
    var keys = new List<string>();
    foreach (var f4 in cube) { var pf = new FFPlanarQuiltFeature(); pf.Boundary.AddRange(f4); pf.Boundary.Add(f4[0]); Add(pf); keys.Add(pf.Key); }
    double v0 = part.Volume;
    var st = new FFStitchFeature(); st.Sources.AddRange(keys); Add(st);
    Console.WriteLine($"  stitch: error {st.Error}, volume added {part.Volume - v0:F6}, bodies {part.Bodies.Count}, quilts left {part.Quilts.Count}");
    Require(Math.Abs(part.Volume - v0 - 1000) < 1e-6 && part.Bodies.Count == 2, "stitch into a body");
    // мостик между верхними рёбрами двух брусков (G1 к верхним граням)
    var b1 = Add(new FFPrimitiveFeature { Shape = FFPrimitive.Box, Origin = new Vector3d(0, 100, 0), A = new ValueRef(20), B = new ValueRef(20), C = new ValueRef(10) });
    var b2 = Add(new FFPrimitiveFeature { Shape = FFPrimitive.Box, Origin = new Vector3d(60, 100, 0), A = new ValueRef(20), B = new ValueRef(20), C = new ValueRef(10) });
    Parametric3D.Kernel.Topology.TEdge TopEdge(double x) => part.Bodies.SelectMany(bb => bb.Edges).First(e => Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9 && Math.Abs(e.V0.P.X - x) < 1e-9 && Math.Abs(e.V1.P.X - x) < 1e-9);
    var ea = TopEdge(10); var eb = TopEdge(50);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(ea.Id, PickKind.Edge, part, null)); Select(w, new PickRef(eb.Id, PickKind.Edge, part, null));
    var bo = new FFSurfaceOp { Mode = FFSurfMode.Blend }; w.StartOperation(bo); w.CommitOperation(); s.Document.RebuildDirty();
    var blend = part.Features.OfType<FFNurbsQuiltFeature>().Last();
    var bq = part.Quilts.Single(q => q.Key == blend.Key);
    var mid = bq.Nurbs!.PointAt(new Vector2d(0.5, 0.5));
    var tan = bq.Nurbs.NormalAt(new Vector2d(0.5, 0.0));
    Console.WriteLine($"  blend: error {blend.Error}, mid {mid}, normal at the first edge {tan}");
    Require(blend.Error == null && Math.Abs(mid.X - 30) < 1e-6 && Math.Abs(mid.Z - 10) < 1e-6 && Math.Abs(Math.Abs(tan.Z) - 1) < 1e-6, "edge blend G1 (flat between two tops)");
    // замена верхней грани бруска (20×20, z = 10) выпуклой NURBS-поверхностью z = 10 + 5·sin(πu)·sin(πv) над ней:
    // объём растёт на 5·400·(2/π)²
    var gq = new Vector3d[17, 17];
    for (int i = 0; i <= 16; i++) for (int j = 0; j <= 16; j++) { double u = i / 16.0, v = j / 16.0; gq[i, j] = new Vector3d(-10 + 20 * u, 90 + 20 * v, 10 + 5 * Math.Sin(Math.PI * u) * Math.Sin(Math.PI * v)); }
    var bump = Add(new FFNurbsQuiltFeature { Def = NurbsDef.Of(FFSurfaces.Interpolate(gq)), QuiltName = "Bump" });
    string b1name = part.BodyNameOf(ea.Id)!;
    var rf = new QuiltSolidFeature { Mode = QuiltSolidKind.ReplaceFace, Source = bump.Key, FFBody = b1name };
    rf.OnFaces.Add(new Vector3d(0, 100, 10));
    double vr0 = part.Volume;
    Add(rf);
    double vbump = 5 * 400 * 4 / (Math.PI * Math.PI);
    Console.WriteLine($"  replace face by NURBS: error {rf.Error}, volume added {part.Volume - vr0:F3} want {vbump:F3}");
    Require(Math.Abs(part.Volume - vr0 - vbump) < 0.01 * vbump, "replace face by a NURBS surface");
    // запись и чтение: те же квилты и объём
    var path = Path.Combine(Path.GetTempPath(), "u13j_surf.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty();
    Console.WriteLine($"  reopened: quilts {s2.Part!.Quilts.Count} (was {part.Quilts.Count}), volume {s2.Part.Volume:F4} (was {part.Volume:F4})");
    Require(s2.Part.Quilts.Count == part.Quilts.Count && Math.Abs(s2.Part.Volume - part.Volume) < 1e-6 * part.Volume
            && s2.Part.Quilts.Any(q => q.Nurbs != null && Math.Abs(q.Nurbs.ControlPoint(0, 0).Z - 5) < 1e-9), "save and load surfaces");
    w.Close();
});

// u13k: SubD — клетка-брусок, уровни, складки (все рёбра — брусок точно), выдавливание граней, петля рёбер, в NURBS,
// перенос с клеткой, выбор граней и рёбер тела → клетка, запись.
Check("UI-U13K-SUBD", () =>
{
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    CommandRegistry.Instance["subd.box"].Execute();
    Require(w.CurrentOperation is SubDOp, "subd box op");
    w.CommitOperation(); s.Document.RebuildDirty();
    var box = part.Features.OfType<FFSubDFeature>().Single();
    var name = part.BodyNameList.Single();
    double v2 = part.Volume;
    Console.WriteLine($"  box cage: error {box.Error}, body {name}, volume L2 {v2:F2}, faces {part.Bodies[0].Faces.Count()}, cage {part.CagesByBody[name].V.Count}/{part.CagesByBody[name].F.Count}");
    Require(box.Error == null && part.CagesByBody.ContainsKey(name) && part.Bodies[0].Faces.Count() == 6 && v2 > 6000 && v2 < 14400
            && part.Bodies[0].Edges.All(e => e.CoEdges.Count == 2), "subd body from the box cage");
    // уровни 3 и 4 — объём сходится (разница < 2 %; между 2 и 3 — больше)
    var c3 = part.CagesByBody[name];
    double v3 = MeshVol(c3.ToSolid(3)), v4 = MeshVol(c3.ToSolid(4));
    Console.WriteLine($"  volume L3 {v3:F2}, L4 {v4:F2}");
    Require(Math.Abs(v4 - v3) < 0.02 * v3 && Math.Abs(v4 - v3) < Math.Abs(v3 - v2), "levels converge");
    // все рёбра — складки: тело — брусок точно
    var cr = part.CagesByBody[name].Clone(); foreach (var k in cr.EdgeFaces().Keys) cr.Creases.Add(k);
    double vc = MeshVol(cr.ToSolid(2));
    Console.WriteLine($"  all creased: volume {vc:F6}");
    Require(Math.Abs(vc - 24000) < 1e-6, "all creases keep the box");
    // выдавить верхнюю грань клетки командой: выбрана грань тела → грань клетки
    var top = part.Bodies[0].Faces.First(f => SubDCage.FaceOfTag(f.Tag) == 1);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    var ex = new SubDOp { Mode = SubDMode.Extrude }; w.StartOperation(ex); w.CommitOperation(); s.Document.RebuildDirty();
    var exF = part.Features.OfType<FFSubDFeature>().Last();
    var cg = part.CagesByBody[name];
    Console.WriteLine($"  extrude: error {exF.Error}, cage {cg.V.Count}/{cg.F.Count}, volume {part.Volume:F2}, bodies {part.Bodies.Count}");
    Require(exF.Error == null && exF.Body == name && cg.V.Count == 12 && cg.F.Count == 10 && part.Volume > v2 + 1000 && part.Bodies.Count == 1, "extrude cage face");
    // петля рёбер: вертикальное ребро тела нижнего яруса → кольцо из 4 боковых граней
    var vEdge = part.Bodies[0].Edges.First(e => e.FaceA != null && e.FaceB != null && SubDRefs.CageEdge(new PickRef(e.Id, PickKind.Edge, part, null), cg) is { } ce && Math.Abs(cg.V[ce.Item1].Z - cg.V[ce.Item2].Z) > 15 && cg.V[ce.Item1].Z < 1 + Math.Min(cg.V[ce.Item1].Z, cg.V[ce.Item2].Z) && Math.Min(cg.V[ce.Item1].Z, cg.V[ce.Item2].Z) < 1e-6);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(vEdge.Id, PickKind.Edge, part, null));
    var lo = new SubDOp { Mode = SubDMode.EdgeLoop }; w.StartOperation(lo); w.CommitOperation(); s.Document.RebuildDirty();
    cg = part.CagesByBody[name];
    Console.WriteLine($"  edge loop: error {part.Features.Last().Error}, cage {cg.V.Count}/{cg.F.Count}, closed {cg.IsClosed()}");
    Require(part.Features.Last().Error == null && cg.V.Count == 16 && cg.F.Count == 14 && cg.IsClosed(), "edge loop");
    // складка: два ребра тела → рёбра клетки
    var edges = part.Bodies[0].Edges.Where(e => e.FaceA != null && e.FaceB != null && SubDRefs.CageEdge(new PickRef(e.Id, PickKind.Edge, part, null), cg) != null)
        .GroupBy(e => SubDRefs.CageEdge(new PickRef(e.Id, PickKind.Edge, part, null), cg)).Select(g => g.First()).Take(2).ToList();
    w.ViewPort.Selection.Clear(); foreach (var e in edges) Select(w, new PickRef(e.Id, PickKind.Edge, part, null));
    var cro = new SubDOp { Mode = SubDMode.Crease }; w.StartOperation(cro); w.CommitOperation(); s.Document.RebuildDirty();
    Require(part.CagesByBody[name].Creases.Count == 2, $"crease ({part.CagesByBody[name].Creases.Count})");
    // перенос тела SubD — клетка вместе с ним
    double z0 = part.CagesByBody[name].V.Min(p => p.X);
    var mv = new FFTransformFeature { Mode = FFXform.Move, BodyNames = { name }, Dx = new ValueRef(100), FreeForm = node.Key, Frozen = true };
    part.AddFeature(mv); s.Document.RebuildDirty();
    Require(mv.Error == null && Math.Abs(part.CagesByBody[name].V.Min(p => p.X) - z0 - 100) < 1e-9, "move carries the cage");
    // запись и чтение
    var path = Path.Combine(Path.GetTempPath(), "u13k_subd.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty();
    Console.WriteLine($"  reopened: volume {s2.Part!.Volume:F3} (was {part.Volume:F3}), cages {s2.Part.CagesByBody.Count}, creases {s2.Part.CagesByBody.Values.First().Creases.Count}");
    Require(Math.Abs(s2.Part.Volume - part.Volume) < 1e-6 * part.Volume && s2.Part.CagesByBody.Values.First().Creases.Count == 2, "save and load subd");
    // в NURBS: грани — NURBS-лоскуты через точки предельной поверхности, тело замкнуто; объём — в 2 % от подразделения
    // уровня 5 (подразделение сходится к пределу снаружи, разметка лоскутов — хорды изнутри)
    double vs = MeshVol(part.CagesByBody[name].ToSolid(5));
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(part.Bodies[0].Faces.First().Id, PickKind.Face, part, null));
    var tn = new SubDOp { Mode = SubDMode.ToNurbs }; w.StartOperation(tn); w.CommitOperation(); s.Document.RebuildDirty();
    var nb = part.Bodies[0];
    int nurbsFaces = nb.Faces.Count(f => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface);
    Console.WriteLine($"  to NURBS: error {part.Features.Last().Error}, faces {nb.Faces.Count()} (NURBS {nurbsFaces}), open edges {nb.Edges.Count(e => e.CoEdges.Count < 2)}, volume {part.Volume:F2} (subd L5 {vs:F2}), cages {part.CagesByBody.Count}");
    Require(part.Features.Last().Error == null && nurbsFaces == nb.Faces.Count() && nb.Edges.All(e => e.CoEdges.Count == 2) && Math.Abs(part.Volume - vs) < 0.02 * vs && part.Volume < vs && part.CagesByBody.Count == 0, "subd to nurbs");
    w.Close();
});

double MeshVol(Parametric3D.Kernel.Topology.TSolid b)
{
    var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b); double v = 0;
    for (int i = 0; i + 2 < md.Indices.Length; i += 3) { var p0 = md.Vertices[md.Indices[i]].P; var p1 = md.Vertices[md.Indices[i + 1]].P; var p2 = md.Vertices[md.Indices[i + 2]].P; v += Vector3.Dot(p0, Vector3.Cross(p1, p2)) / 6.0; }
    return v;
}

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
sealed class Tol : IEqualityComparer<double> { public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-6; public int GetHashCode(double x) => 0; }
