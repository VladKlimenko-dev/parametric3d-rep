using System.Text;

namespace Parametric3D.Core;

/// <summary>
/// ЖУРНАЛ ПРИЛОЖЕНИЯ — для исключений, которых никто не поймал.
///
/// Прежде в приложении не было ни одного глобального перехватчика: исключение
/// в обработчике показывало стандартное окно WinForms, и после «Продолжить»
/// от него не оставалось следа. А исключение внутри отрисовки ещё и выключало
/// 3D-вид навсегда. Теперь каждое такое исключение попадает сюда вместе
/// со стеком — и о зависании вида можно судить по записям, а не по догадкам.
///
/// Файл: %LOCALAPPDATA%\Parametric3D\app.log, не больше мегабайта;
/// старый уходит в app.log.old.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Path { get; set; } = DefaultPath();

    public static long MaxBytes { get; set; } = 1 << 20;

    public static string DefaultPath()
    {
        try
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parametric3D", "app.log");
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Записать строку и, если есть, исключение со стеком. Никогда не бросает.</summary>
    public static void Write(string what, Exception? ex = null)
    {
        if (string.IsNullOrEmpty(Path)) return;

        try
        {
            lock (Gate)
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').AppendLine(what);
                if (ex != null) sb.AppendLine(ex.ToString());

                string? dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string old = Path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(Path, old);
                }

                File.AppendAllText(Path, sb.ToString());
            }
        }
        catch
        {
            // Журнал не имеет права уронить приложение.
        }
    }
}
