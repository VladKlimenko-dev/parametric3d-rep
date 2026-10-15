using System.Windows.Input;
using Avalonia.Input;

namespace Parametric3D.App.Commands;

/// <summary>
/// КОМАНДА ПРИЛОЖЕНИЯ — одна запись реестра: имя, группа, иконка, клавиша, синонимы для поиска (RU/EN), условие
/// доступности и действие. Меню, панели, палитра команд (F) и клавиши строятся из реестра (UI_AVALONIA_STAGE, п. 1).
/// </summary>
public sealed class AppCommand : ICommand
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Group { get; init; } = "";
    /// <summary>Имя файла иконки из UI_ICONS.md (например, «save_but.svg»).</summary>
    public string? Icon { get; init; }
    public KeyGesture? Gesture { get; init; }
    /// <summary>Слова для поиска в палитре: английское имя, синонимы.</summary>
    public string Synonyms { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Подпись кнопки без иконки (пока иконки нет): короткое слово вместо аббревиатуры.</summary>
    public string? Short { get; init; }
    public Func<bool>? CanRun { get; init; }
    public required Action Run { get; init; }
    /// <summary>Переключатель: состояние «включено» (кнопка-флажок).</summary>
    public Func<bool>? IsChecked { get; init; }

    public bool Enabled => CanRun?.Invoke() ?? true;

    public string Tooltip => Gesture != null ? $"{Label}  ({Format(Gesture)})" : Label;

    public static string Format(KeyGesture g)
    {
        var s = g.ToString();
        return s.Replace("Control", "Ctrl");
    }

    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    bool ICommand.CanExecute(object? parameter) => Enabled;
    void ICommand.Execute(object? parameter) => Execute();

    /// <summary>Выполнить (если доступна); ошибки — в журнал и в сообщение пользователю через реестр.</summary>
    public void Execute()
    {
        if (!Enabled) return;
        CommandRegistry.Instance.Invoke(this);
    }
}
