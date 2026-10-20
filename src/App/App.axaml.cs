using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Parametric3D.App.Services;
using Parametric3D.App.Views;

namespace Parametric3D.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
            Core.AppLog.Write($"start {Diagnostics.SelfTest.Build}; graphics mode {Program.GlMode}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            // Аргументы: путь к файлу детали — открыть; --sample — пробная деталь; --theme light|dark.
            DocumentSession session;
            var file = AppStartup.Args.FirstOrDefault(a => (a.EndsWith(DocumentSession.PartExtension, StringComparison.OrdinalIgnoreCase) || a.EndsWith(DocumentSession.AssemblyExtension, StringComparison.OrdinalIgnoreCase)) && File.Exists(a));
            try { session = file != null ? DocumentSession.Open(file) : AppStartup.Flag("--sample") ? DocumentSession.Sample() : DocumentSession.NewPart(); }
            catch (Exception ex) { Core.AppLog.Write("open at start", ex); session = DocumentSession.NewPart(); }
            if (AppStartup.Arg("--theme") is { } th) UserPrefs.Current.Theme = th.Equals("light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
            var w = new MainWindow(session);
            d.MainWindow = w;
            if (AppStartup.Arg("--shot") is { } shot) Diagnostics.ShotRunner.Start(w, shot, () => d.Shutdown());
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
