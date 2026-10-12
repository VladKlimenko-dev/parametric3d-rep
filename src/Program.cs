using Parametric3D.Core;
using Parametric3D.UI;

namespace Parametric3D;

internal static class Program
{
    private static DateTime _lastShown = DateTime.MinValue;

    [STAThread]
    private static void Main()
    {
        // ГЛОБАЛЬНЫЕ ПЕРЕХВАТЧИКИ.
        //
        // Прежде их не было: исключение в обработчике показывало стандартное
        // окно WinForms, после «Продолжить» от него не оставалось следа.
        // Теперь каждое уходит в журнал со стеком (Core/AppLog), а человеку
        // показывается коротко — не чаще раза в десять секунд, чтобы повторяющаяся
        // ошибка не завалила его окнами. Режим задаётся до создания окон.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) => Report("необработанное исключение в потоке интерфейса", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write("необработанное исключение вне потока интерфейса" + (e.IsTerminating ? " (приложение завершается)" : ""),
                         e.ExceptionObject as Exception);

        AppLog.Write("запуск");

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());

        AppLog.Write("завершение");
    }

    private static void Report(string what, Exception ex)
    {
        AppLog.Write(what, ex);

        if (DateTime.UtcNow - _lastShown < TimeSpan.FromSeconds(10)) return;
        _lastShown = DateTime.UtcNow;

        try
        {
            MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\nПодробности — в журнале:\n{AppLog.Path}",
                            "Parametric3D", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            // Окно не показалось — запись в журнале уже есть.
        }
    }
}
