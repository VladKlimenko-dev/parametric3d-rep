using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Parametric3D.App.Services;
using Parametric3D.App.Views;

namespace Parametric3D.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            // Аргументы: путь к файлу детали — открыть; --sample — пробная деталь; --theme light|dark.
            DocumentSession session;
            var file = AppStartup.Args.FirstOrDefault(a => a.EndsWith(DocumentSession.PartExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
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
