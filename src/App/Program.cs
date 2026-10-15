using Avalonia;

namespace Parametric3D.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppStartup.Args = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Отрисовка вида — OpenGL. Под Windows сначала настольный OpenGL (WGL) — тот же, что у прежнего приложения; если
    /// драйвер его не даёт — OpenGL ES 3 через ANGLE (Direct3D 11). Под Linux — GLX, затем EGL.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Wgl, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software } })
            .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software } })
            .WithInterFont()
            .LogToTrace();
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
