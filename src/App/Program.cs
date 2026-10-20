using Avalonia;

namespace Parametric3D.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppStartup.Args = args;
        // АВАРИЙНОЕ ЗАВЕРШЕНИЕ (13:09: выход 0xC0000409 — необработанное исключение вне потока окна): полный текст
        // исключения с трассой — в журнал приложения до завершения процесса
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { Core.AppLog.Write($"FATAL unhandled exception (terminating {e.IsTerminating}) on thread {Environment.CurrentManagedThreadId} «{System.Threading.Thread.CurrentThread.Name}»", e.ExceptionObject as Exception); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { try { Core.AppLog.Write("unobserved task exception", e.Exception); e.SetObserved(); } catch { } };
        // проверка: шейдеры в виде для OpenGL ES 3 (ANGLE) — в папку, без окна
        if (AppStartup.Arg("--dump-shaders") is { } dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var f in typeof(Render.Engine.EngineShaders).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (f.GetValue(null) is string src && src.Contains("#version"))
                    File.WriteAllText(Path.Combine(dir, f.Name + ".glsl"), Render.ShaderProgram.ToEs(src));
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// ГРАФИКА. Под Windows по умолчанию — OpenGL ES 3 через ANGLE (Direct3D 11): так рисует и сама Avalonia, и это
    /// самый надёжный путь на встроенных видеокартах. Настольный OpenGL (WGL) — по выбору: на Intel Iris Xe с WGL
    /// приложение аварийно завершалось (0xC0000374 — порча кучи в драйвере, 2026-10-16). Под Linux — GLX, затем EGL.
    /// Выбор: параметр запуска «--gl angle|wgl|glx|egl|software», переменная P3D_GL или меню «Файл → Графика»
    /// (ui.json, GlMode; действует со следующего запуска).
    /// </summary>
    public static string GlMode =>
        (AppStartup.Arg("--gl") ?? Environment.GetEnvironmentVariable("P3D_GL") ?? Services.UserPrefs.Current.GlMode ?? "auto").ToLowerInvariant();

    public static AppBuilder BuildAvaloniaApp()
    {
        string m = GlMode;
        var win = m switch
        {
            "wgl" => new[] { Win32RenderingMode.Wgl, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
            "software" => new[] { Win32RenderingMode.Software },
            _ => new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software },
        };
        var x11 = m switch
        {
            "egl" or "angle" => new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software },
            "software" => new[] { X11RenderingMode.Software },
            _ => new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software },
        };
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = win })
            .With(new X11PlatformOptions { RenderingMode = x11 })
            .WithInterFont()
            .LogToTrace();
    }
}

/// <summary>Параметры запуска (проверки: --shot путь, --theme light).</summary>
public static class AppStartup
{
    public static string[] Args = Array.Empty<string>();
    public static string? Arg(string name)
    {
        int i = Array.IndexOf(Args, name);
        return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }
    public static bool Flag(string name) => Array.IndexOf(Args, name) >= 0;
}
