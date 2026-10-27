using System.Text.Json;

namespace Parametric3D.App.Services;

/// <summary>
/// Настройки пользователя (не документа): тема, последние файлы, недавние команды. Файл ui.json в профиле
/// (%LOCALAPPDATA%\Parametric3D). Чтение и запись не роняют приложение: испорченный файл — настройки по умолчанию.
/// </summary>
public sealed class UserPrefs
{
    public string Theme { get; set; } = "Dark";
    /// <summary>Язык интерфейса: «ru» или «en» (кнопка у темы).</summary>
    public string Language { get; set; } = "ru";
    /// <summary>Графика: «auto» (Windows — ANGLE), «angle», «wgl», «software» (со следующего запуска).</summary>
    public string? GlMode { get; set; } = "auto";
    /// <summary>u09: предпросмотр операций ручкой — «full» (деталь считается при перетаскивании) или «contour» (контур).</summary>
    public string? PreviewMode { get; set; } = "full";
    /// <summary>u12: версия файла настроек; до 12 — предпросмотр сбрасывается на «полный» (самопроверка могла сохранить «контур»).</summary>
    public int PrefsVersion { get; set; } = 12;
    public List<string> RecentFiles { get; set; } = new();
    public List<string> RecentCommands { get; set; } = new();
    /// <summary>u13: закреплённые команды контекстной панели.</summary>
    public List<string> PinnedCommands { get; set; } = new();
    public bool TreeVisible { get; set; } = true;
    public double TreeWidth { get; set; } = 260;
    /// <summary>Места панелей поверх вида: «Сторона|доля» (прилипла) или «Free|x|y».</summary>
    public Dictionary<string, string> PanelPlaces { get; set; } = new();
    /// <summary>Визуальные настройки (вкладка «Вид»): размеры текста и кнопок, цвета.</summary>
    public LookPrefs Look { get; set; } = new();
    /// <summary>k120: свои системы единиц пользователя (имя и единицы длины, массы, времени, угла).</summary>
    public List<Dictionary<string, string>> UnitSystems { get; set; } = new();

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parametric3D");
    public static string FilePath => Path.Combine(Dir, "ui.json");

    public static UserPrefs Current { get; private set; } = Load();

    public static UserPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var p = JsonSerializer.Deserialize<UserPrefs>(json) ?? new UserPrefs();
                if (!json.Contains("\"PrefsVersion\"") || p.PrefsVersion < 12) { p.PreviewMode = "full"; p.PrefsVersion = 12; }
                return p;
            }
        }
        catch (Exception ex) { Core.AppLog.Write("prefs load", ex); }
        return new UserPrefs();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Core.AppLog.Write("prefs save", ex); }
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }

    public void AddRecentCommand(string id)
    {
        RecentCommands.Remove(id);
        RecentCommands.Insert(0, id);
        if (RecentCommands.Count > 8) RecentCommands.RemoveRange(8, RecentCommands.Count - 8);
        Save();
    }
}
