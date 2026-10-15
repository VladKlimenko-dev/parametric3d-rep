using System.Text.Json;

namespace Parametric3D.App.Services;

/// <summary>
/// Настройки пользователя (не документа): тема, последние файлы, недавние команды. Файл ui.json в профиле
/// (%LOCALAPPDATA%\Parametric3D). Чтение и запись не роняют приложение: испорченный файл — настройки по умолчанию.
/// </summary>
public sealed class UserPrefs
{
    public string Theme { get; set; } = "Dark";
    public List<string> RecentFiles { get; set; } = new();
    public List<string> RecentCommands { get; set; } = new();
    public bool TreeVisible { get; set; } = true;
    public double TreeWidth { get; set; } = 260;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parametric3D");
    public static string FilePath => Path.Combine(Dir, "ui.json");

    public static UserPrefs Current { get; private set; } = Load();

    public static UserPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UserPrefs>(File.ReadAllText(FilePath)) ?? new UserPrefs();
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
