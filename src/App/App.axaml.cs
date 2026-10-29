using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Parametric3D.App.Services;
using Parametric3D.App.Views;

namespace Parametric3D.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // k121: мутация P3D_TIPONELINE=0 — прежние подсказки (перенос, ширина до 420)
        if (TipOneLineOff)
        {
            var tip = new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<Avalonia.Controls.ToolTip>(x));
            tip.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Layout.Layoutable.MaxWidthProperty, 420.0));
            var tb = new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<Avalonia.Controls.TextBlock>(Avalonia.Styling.Selectors.Descendant(Avalonia.Styling.Selectors.OfType<Avalonia.Controls.ToolTip>(x))));
            tb.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.TextBlock.TextWrappingProperty, Avalonia.Media.TextWrapping.Wrap));
            Styles.Add(tip); Styles.Add(tb);
        }
    }
    public static readonly bool TipOneLineOff = Environment.GetEnvironmentVariable("P3D_TIPONELINE") == "0";

    /// <summary>Прошлый запуск не дошёл до выхода (аварийное завершение).</summary>
    public static bool PreviousRunCrashed { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (!Avalonia.Controls.Design.IsDesignMode) Diagnostics.UiWatchdog.Start();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            // метка «приложение работает»: осталась от прошлого запуска — тот завершился аварийно (не через выход)
            try
            {
                string flag = Path.Combine(Path.GetDirectoryName(Core.AppLog.Path) ?? ".", "running.flag");
                PreviousRunCrashed = File.Exists(flag);
                if (PreviousRunCrashed) Core.AppLog.Write("previous run ended abnormally (no clean exit)");
                Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
                File.WriteAllText(flag, DateTime.Now.ToString("O"));
                d.Exit += (_, _) => { try { File.Delete(flag); } catch { } Core.AppLog.Write("exit"); };
            }
            catch { }
            Core.AppLog.Write($"start {Diagnostics.SelfTest.Build}; graphics mode {Program.GlMode}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {Diagnostics.KernelWarmup.BuildInfo}");
            // Аргументы: путь к файлу детали — открыть; --sample — пробная деталь; --theme light|dark.
            DocumentSession session;
            var file = AppStartup.Args.FirstOrDefault(a => (a.EndsWith(DocumentSession.PartExtension, StringComparison.OrdinalIgnoreCase) || a.EndsWith(DocumentSession.AssemblyExtension, StringComparison.OrdinalIgnoreCase)) && File.Exists(a));
            var swStart = System.Diagnostics.Stopwatch.StartNew(); bool startDbg = Environment.GetEnvironmentVariable("P3D_STARTDBG") == "1";
            try { session = file != null ? DocumentSession.Open(file) : AppStartup.Flag("--sample") ? DocumentSession.Sample() : DocumentSession.NewPart(); }
            catch (Exception ex) { Core.AppLog.Write("open at start", ex); session = DocumentSession.NewPart(); }
            if (AppStartup.Arg("--theme") is { } th) UserPrefs.Current.Theme = th.Equals("light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
            if (startDbg) Console.WriteLine($"START open {swStart.ElapsedMilliseconds} ms");
            var w = new MainWindow(session);
            if (startDbg) { Console.WriteLine($"START window ctor {swStart.ElapsedMilliseconds} ms"); w.Opened += (_, _) => Console.WriteLine($"START opened {swStart.ElapsedMilliseconds} ms ({(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms since process start)"); }
            d.MainWindow = w;
            if (AppStartup.Arg("--shot") is { } shot) Diagnostics.ShotRunner.Start(w, shot, () => d.Shutdown());
            // 20:17: прогрев ядра в фоне (первая операция — без компиляции кода ядра); не в проверочных запусках
            else if (!AppStartup.Flag("--selftest") && !Diagnostics.KernelWarmup.ShouldSkipFor(session)) Diagnostics.KernelWarmup.Start();
            else if (Diagnostics.KernelWarmup.ShouldSkipFor(session)) Core.AppLog.Write("kernel warm-up skipped: the opened document rebuilds the kernel code itself");
            if (startDbg)
            {
                double T() => (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                session.Document.BuildStarted += () => Console.WriteLine($"START build started {T():0} ms");
                session.Document.BuildFinished += () => Console.WriteLine($"START build finished {T():0} ms; compute {session.Document.LastComputeMs:0} ms; " + string.Join(", ", session.Part!.Features.Select(f => $"{f.Name} {(session.Part.LastFeatureMs.TryGetValue(f, out var m) ? m : 0):0}")));
                session.Document.VerifyStarted += () => Console.WriteLine($"START verify started {T():0} ms");
                session.Document.VerifyFinished += () => Console.WriteLine($"START verify finished {T():0} ms ({session.Document.LastVerifyMs:0} ms)");
            }
            // --selftest: кнопка «Тест» без человека — отчёт и выход (путь отчёта — в стандартный вывод)
            if (AppStartup.Flag("--selftest"))
                Avalonia.Threading.DispatcherTimer.RunOnce(async () =>
                {
                    var path = await Diagnostics.SelfTest.RunAsync(w, Console.WriteLine, CancellationToken.None);
                    Console.WriteLine("REPORT " + path);
                    d.Shutdown();
                }, TimeSpan.FromSeconds(3));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
