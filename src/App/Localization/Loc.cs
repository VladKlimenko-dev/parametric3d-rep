namespace Parametric3D.App;

/// <summary>
/// ЯЗЫК ИНТЕРФЕЙСА: русский (основной) или английский (UI-26). Строки пишутся в коде по-русски через L("…"); перевод —
/// таблица Strings.En. Нет перевода — показывается русская строка (так недостающее сразу видно).
/// Смена языка (кнопка у темы) поднимает Changed: окно перестраивает подписи.
/// </summary>
public static class Loc
{
    public static bool En { get; private set; }
    public static event Action? Changed;

    public static void Set(bool en)
    {
        if (En == en) return;
        En = en;
        Changed?.Invoke();
    }

    /// <summary>Строка на текущем языке.</summary>
    public static string L(string ru) => En && Strings.En.TryGetValue(ru, out var en) ? en : ru;

    /// <summary>Английский вариант (для поиска в палитре на обоих языках).</summary>
    public static string EnOf(string ru) => Strings.En.TryGetValue(ru, out var en) ? en : "";

    /// <summary>Строка с подстановкой: L("Деталь «{0}»", name).</summary>
    public static string L(string ru, params object[] args) => string.Format(L(ru), args);
}
