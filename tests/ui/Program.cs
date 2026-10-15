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
    var g = reg.All.Where(c => c.Gesture != null).GroupBy(c => c.Gesture!.ToString()).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
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
    vp.ShowDatumPlanes = false;
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

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed }));
return failed == 0 ? 0 : 1;

sealed class CheckFailed : Exception { public CheckFailed(string m) : base(m) { } }
