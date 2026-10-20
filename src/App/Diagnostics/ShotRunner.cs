using Avalonia.Threading;
using Parametric3D.App.Views;

namespace Parametric3D.App.Diagnostics;

/// <summary>
/// Проверка без человека (Xvfb): через паузу на построение — снимок кадра 3D-вида в PNG, затем выход. Сценарий
/// (--script «команда;команда…») выполняет команды реестра до снимка. Снимок всего окна делает внешний скрипт.
/// </summary>
public static class ShotRunner
{
    /// <summary>
    /// Шаг сценария: id команды; hover:x,y / pick:x,y / add:x,y — навести, выбрать, добавить (доли размера вида);
    /// click:x,y — щелчок инструмента эскиза; ok / cancel — кнопки панели операции; esc.
    /// </summary>
    private static void Step(MainWindow w, string id)
    {
        if (id.Split(':') is [var verb, var arg] && verb is "hover" or "pick" or "add" or "click")
        {
            var xy = arg.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (verb == "click") w.ViewPort.SimulateToolClick(xy[0], xy[1]);
            else w.ViewPort.SimulatePick(xy[0], xy[1], verb != "hover", verb == "add");
            Console.WriteLine($"{verb} {arg}: hover {w.ViewPort.Selection.Hover.Kind}, selected {w.ViewPort.Selection.Count}");
            return;
        }
        if (id.StartsWith("hdrag:") || id.StartsWith("hhold:"))
        {
            var a = id[6..].Split(',');
            w.ViewPort.SimulateHandleDrag(int.Parse(a[0]) * double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), end: id.StartsWith("hdrag:"));
            Console.WriteLine($"handle drag {id}");
            return;
        }
        if (id.StartsWith("snap:"))
        {
            // промежуточный снимок кадра (разбор мигания): snap:имя → рядом с основным снимком
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppStartup.Arg("--shot") ?? ".") ?? ".", id[5..] + ".png");
            w.ViewPort.CaptureNextFrame = (px, pw, ph) => { Shots.SavePng(path, px, pw, ph, flip: true); Console.WriteLine("SNAP " + path); };
            w.ViewPort.Redraw();
            return;
        }
        if (id.StartsWith("rot:"))
        {
            // поворот вида: наклон (вокруг X) и разворот (вокруг Y), градусы — как изометрия «домой»
            var a = id[4..].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180).ToArray();
            w.ViewPort.AnimateTo(OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitX, a[0]) * OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitY, a[1]));
            Console.WriteLine("rot " + id); return;
        }
        if (id.StartsWith("mat:") && w.Session.Part is { } mp && Model.Material.FromLibrary(id[4..]) is { } mm)
        {
            mp.Material = mm; w.ViewPort.MarkDirty(); Console.WriteLine("material " + mm.Name); return;
        }
        if (id.StartsWith("select:"))
        {
            // выбрать элемент модели по имени (как щелчок в дереве)
            var name = id[7..];
            var it = w.Session.Document.Root.Descendants().FirstOrDefault(x => x.Name == name);
            if (it != null) w.SelectItem(it);
            Console.WriteLine($"select {name}: {(it != null ? "ok" : "not found")}");
            return;
        }
        switch (id)
        {
            case "ok": w.CommitOperation(); Console.WriteLine("ok"); return;
            case "cancel": w.CancelOperation(); Console.WriteLine("cancel"); return;
            case "wait": return;
            case "notool": w.ViewPort.Tool = null; return;
            case "dbg":
                {
                    var part = w.Session.Part!;
                    Console.WriteLine($"camera fwd {w.ViewPort.Camera.Forward} pos {w.ViewPort.Camera.Position} sketch W {w.ViewPort.ActiveSketch?.Plane.W} O {w.ViewPort.ActiveSketch?.Plane.Origin}");
                    var meshIds = part.Mesh?.Vertices.Select(v => v.FaceId).Distinct().OrderBy(x => x).ToList() ?? new();
                    var bodyIds = part.Bodies.SelectMany(b => b.Faces).Select(f => f.Id).OrderBy(x => x).ToList();
                    foreach (var f in part.Bodies.SelectMany(b => b.Faces)) Console.WriteLine($"face {f.Id}: {f.Surface?.GetType().Name} auth {Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} plane {Model.Features.SketchFaceRef.PlaneOf(f) != null}");
                    Console.WriteLine($"mesh faces [{string.Join(",", meshIds)}] body faces [{string.Join(",", bodyIds)}] sel {w.ViewPort.Selection.Primary.Id} owner {w.ViewPort.Selection.Primary.Owner?.GetHashCode()} part {part.GetHashCode()}");
                    return;
                }
        }
        var c = Commands.CommandRegistry.Instance.Find(id);
        Console.WriteLine(c == null ? $"no command {id}" : $"run {id} (enabled {c.Enabled})");
        c?.Execute();
    }

    public static void Start(MainWindow w, string path, Action exit)
    {
        var steps = (AppStartup.Arg("--script") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int wait = int.TryParse(AppStartup.Arg("--wait"), out var s) ? s : 3;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(wait) };
        int stage = 0;
        t.Tick += (_, _) =>
        {
            // шаги — по одному за такт (выбор видеокартой приходит со следующим кадром)
            if (stage == 0 && steps.Count > 0)
            {
                t.Interval = TimeSpan.FromSeconds(0.7);
                var id = steps[0];
                steps.RemoveAt(0);
                try { Step(w, id); }
                catch (Exception ex) { Console.WriteLine($"step {id}: {ex.Message}"); }
                return;
            }
            if (stage == 0)
            {
                stage = 1;
                t.Interval = TimeSpan.FromSeconds(1.5);
                return;
            }
            t.Stop();
            w.ViewPort.CaptureNextFrame = (px, pw, ph) =>
            {
                Shots.SavePng(path, px, pw, ph, flip: true);
                Console.WriteLine($"SHOT {pw}x{ph} | {w.ViewPort.GlInfo}");
                Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(exit, TimeSpan.FromSeconds(AppStartup.Flag("--hold") ? 4 : 0.2)));
            };
            w.ViewPort.Redraw();
        };
        t.Start();
    }
}
