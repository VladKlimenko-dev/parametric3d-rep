using Avalonia.Input;

namespace Parametric3D.App.Commands;

/// <summary>Реестр команд: регистрация, поиск (палитра F), клавиши, недавние.</summary>
public sealed class CommandRegistry
{
    public static CommandRegistry Instance { get; } = new();

    private readonly Dictionary<string, AppCommand> _byId = new();
    private readonly List<AppCommand> _order = new();

    public IReadOnlyList<AppCommand> All => _order;

    /// <summary>Команда выполнена (для недавних и журнала скорости).</summary>
    public event Action<AppCommand>? Executed;
    /// <summary>Команда упала: текст для пользователя.</summary>
    public event Action<AppCommand, Exception>? Failed;

    public AppCommand Add(AppCommand c)
    {
        if (_byId.ContainsKey(c.Id)) throw new InvalidOperationException("duplicate command " + c.Id);
        _byId[c.Id] = c;
        _order.Add(c);
        return c;
    }

    public AppCommand? Find(string id) => _byId.TryGetValue(id, out var c) ? c : null;
    public AppCommand this[string id] => _byId[id];

    public void Clear() { _byId.Clear(); _order.Clear(); }

    internal void Invoke(AppCommand c)
    {
        try
        {
            c.Run();
            Executed?.Invoke(c);
        }
        catch (Exception ex)
        {
            Core.AppLog.Write("command " + c.Id, ex);
            Failed?.Invoke(c, ex);
        }
        RefreshAll();
    }

    public void RefreshAll() { foreach (var c in _order) c.Refresh(); }

    /// <summary>Команда по клавише (null — нет такой или недоступна).</summary>
    public AppCommand? ByGesture(KeyEventArgs e)
    {
        foreach (var c in _order)
            if (c.Gesture != null && c.Gesture.Matches(e)) return c;
        return null;
    }

    /// <summary>
    /// Поиск для палитры: все слова запроса должны встретиться в имени, группе, синонимах или id (без учёта регистра);
    /// выше — совпадение с началом имени, затем по порядку реестра.
    /// </summary>
    public List<AppCommand> Search(string query)
    {
        var words = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var res = new List<(AppCommand C, int Score)>();
        foreach (var c in _order)
        {
            string hay = $"{c.Label} {c.Group} {c.Synonyms} {c.Id}".ToLowerInvariant();
            if (!words.All(w => hay.Contains(w))) continue;
            int score = words.Length == 0 ? 0 : c.Label.ToLowerInvariant().StartsWith(words[0]) ? 0 : 1;
            res.Add((c, score));
        }
        return res.OrderBy(r => r.Score).Select(r => r.C).ToList();
    }
}
