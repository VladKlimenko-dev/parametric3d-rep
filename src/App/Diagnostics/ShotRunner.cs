using Avalonia.Threading;
using Parametric3D.App.Views;

namespace Parametric3D.App.Diagnostics;

/// <summary>
/// Проверка без человека (Xvfb): через паузу на построение — снимок кадра 3D-вида в PNG, затем выход. Сценарий
/// (--script «команда;команда…») выполняет команды реестра до снимка. Снимок всего окна делает внешний скрипт.
/// </summary>
public static class ShotRunner
{
    public static void Start(MainWindow w, string path, Action exit)
    {
        var steps = (AppStartup.Arg("--script") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int wait = int.TryParse(AppStartup.Arg("--wait"), out var s) ? s : 3;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(wait) };
        int stage = 0;
        t.Tick += (_, _) =>
        {
            if (stage == 0)
            {
                foreach (var id in steps)
                {
                    // hover:x,y / pick:x,y / add:x,y — доли размера вида
                    if (id.Split(':') is [var verb, var arg] && verb is "hover" or "pick" or "add")
                    {
                        var xy = arg.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        w.ViewPort.SimulatePick(xy[0], xy[1], verb != "hover", verb == "add");
                        Console.WriteLine($"{verb} {arg}: hover {w.ViewPort.Selection.Hover.Kind}, selected {w.ViewPort.Selection.Count}");
                        continue;
                    }
                    var c = Commands.CommandRegistry.Instance.Find(id);
                    Console.WriteLine(c == null ? $"no command {id}" : $"run {id}");
                    c?.Execute();
                }
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
