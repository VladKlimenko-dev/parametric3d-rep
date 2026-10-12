using System.Text;

namespace Parametric3D.Views;

/// <summary>
/// ЗДОРОВЬЕ ВИДА OPENGL — счётчики, политика восстановления и журнал.
///
/// Зачем. Вид замерзал навсегда в случайный момент, а после сворачивания
/// окна пропадал совсем. Путей к этому было три, и ни один не оставлял следа:
///
/// 1. Исключение внутри OnPaint. WinForms после этого помечает контрол
///    «ошибка при отрисовке» и больше НИКОГДА не зовёт OnPaint — рисует
///    средствами GDI красный крест, которого на GL-окне не видно. А в кадре
///    выполнялось всё: перестроение модели, решатель, тесселяция сцены.
/// 2. Однократный сбой MakeCurrent (сброс драйвера, смена монитора, выход
///    из сна) выставлял вечный флаг «контекст потерян»; вернуть вид могло
///    только пересоздание окна, а сворачивание его не пересоздаёт.
/// 3. Статические шейдеры переживали контекст: новый контекст получал номера
///    программ мёртвого и молча не рисовал ничего.
///
/// Этот класс не знает ни WinForms, ни OpenGL: вид сообщает ему события,
/// а он решает, пора ли восстанавливаться, и ведёт журнал. Поэтому его
/// поведение проверяется тестами без окна и без видеокарты.
/// </summary>
public sealed class GLHealth
{
    // ------------------------------------------------------------- счётчики
    public long FramesOk { get; private set; }
    public long FramesFailed { get; private set; }
    public long PrepareFailures { get; private set; }
    public long GlErrors { get; private set; }
    public long ContextLosses { get; private set; }
    public long Recoveries { get; private set; }
    public long Stalls { get; private set; }

    public DateTime? LastFrameOk { get; private set; }
    public DateTime? LastInvalidate { get; private set; }
    public string? LastProblem { get; private set; }
    public string? Device { get; private set; }

    /// <summary>Сообщение для человека — строка состояния, например. Не чаще раза в секунду.</summary>
    public event Action<string>? Problem;

    // --------------------------------------------------------------- политика
    /// <summary>Сколько сбоев кадра подряд — повод пересоздать контекст.</summary>
    public int FailuresBeforeRecovery { get; init; } = 3;

    /// <summary>Не чаще одного восстановления за этот промежуток.</summary>
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Столько попыток подряд без единого удачного кадра — потом только журнал.</summary>
    public int MaxRecoveriesWithoutFrame { get; init; } = 5;

    /// <summary>Вид просили перерисовать, а кадра нет дольше этого — вид завис.</summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(3);

    private int _consecutiveFailures;
    private int _recoveriesWithoutFrame;
    private DateTime? _lastRecovery;
    private DateTime? _lastProblemRaised;
    private bool _stallReported;

    // ----------------------------------------------------------------- журнал
    /// <summary>Куда пишется журнал. Пустая строка — не писать.</summary>
    public string LogPath { get; init; } = DefaultLogPath();

    /// <summary>Потолок размера журнала; при превышении старый файл уходит в .old.</summary>
    public long MaxLogBytes { get; init; } = 1 << 20;

    private string? _lastLine;
    private int _repeats;

    public static string DefaultLogPath()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parametric3D");
            return Path.Combine(dir, "gl-health.log");
        }
        catch
        {
            return "";
        }
    }

    // ================================================================ события
    public void ContextCreated(string device, DateTime now)
    {
        Device = device;
        _consecutiveFailures = 0;
        Log(now, $"контекст создан: {device}");
    }

    /// <summary>MakeCurrent не удался. Возвращает true, если пора пересоздать контекст.</summary>
    public bool ContextLost(string message, DateTime now)
    {
        ContextLosses++;
        Raise(now, $"контекст OpenGL потерян: {message}");
        return MayRecover(now);
    }

    public void MarkInvalidated(DateTime now)
    {
        LastInvalidate ??= now;
        if (LastFrameOk is DateTime ok && LastInvalidate < ok) LastInvalidate = now;
    }

    public void FrameSucceeded(DateTime now)
    {
        FramesOk++;
        LastFrameOk = now;
        LastInvalidate = null;
        _consecutiveFailures = 0;
        _recoveriesWithoutFrame = 0;

        if (_stallReported)
        {
            _stallReported = false;
            Log(now, "вид снова рисует");
        }
    }

    /// <summary>
    /// Подготовка кадра (перестроение модели, сцены) бросила исключение.
    /// Кадр при этом рисуется из прежней сцены, поэтому восстанавливать
    /// контекст незачем — это ошибка модели, а не видеокарты.
    /// </summary>
    public void PrepareFailed(Exception ex, DateTime now)
    {
        PrepareFailures++;
        Raise(now, $"перестроение сцены не удалось: {Describe(ex)}", ex);
    }

    /// <summary>Сам кадр бросил исключение. Возвращает true, если пора пересоздать контекст.</summary>
    public bool FrameFailed(Exception ex, DateTime now)
    {
        FramesFailed++;
        _consecutiveFailures++;
        Raise(now, $"кадр не нарисован ({_consecutiveFailures} подряд): {Describe(ex)}", ex);
        return _consecutiveFailures >= FailuresBeforeRecovery && MayRecover(now);
    }

    /// <summary>
    /// Ошибка glGetError после кадра. Потеря контекста (0x0507) и нехватка
    /// памяти (0x0505) — повод пересоздать контекст; остальное — только журнал.
    /// </summary>
    public bool GlError(int code, DateTime now)
    {
        GlErrors++;
        bool fatal = code == 0x0507 || code == 0x0505;
        Raise(now, $"glGetError: 0x{code:X4}{(fatal ? " — контекст непригоден" : "")}");
        return fatal && MayRecover(now);
    }

    /// <summary>
    /// Сторож: вид просили перерисовать давно, а кадра так и нет. Возвращает
    /// true, если пора пересоздать контекст. Невидимый вид не считается.
    /// </summary>
    public bool CheckStall(DateTime now, bool visible)
    {
        if (!visible || LastInvalidate is not DateTime asked) return false;
        if (now - asked < StallAfter) return false;

        if (!_stallReported)
        {
            _stallReported = true;
            Stalls++;
            Raise(now, $"вид не рисует {(now - asked).TotalSeconds:0} с после запроса перерисовки");
        }

        return MayRecover(now);
    }

    /// <summary>Можно ли восстанавливаться сейчас — с учётом частоты и числа попыток.</summary>
    public bool MayRecover(DateTime now)
    {
        if (_recoveriesWithoutFrame >= MaxRecoveriesWithoutFrame) return false;
        if (_lastRecovery is DateTime last && now - last < RecoveryInterval) return false;
        return true;
    }

    public void RecoveryStarted(DateTime now, string why)
    {
        Recoveries++;
        _recoveriesWithoutFrame++;
        _lastRecovery = now;
        _consecutiveFailures = 0;
        LastInvalidate = now;

        Log(now, $"восстановление #{Recoveries} ({why})" +
                 (_recoveriesWithoutFrame >= MaxRecoveriesWithoutFrame
                     ? " — последняя попытка подряд без удачного кадра; дальше только журнал" : ""));
    }

    /// <summary>Сводка для окна диагностики.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Устройство: {Device ?? "неизвестно"}");
        sb.AppendLine($"Кадров: удачных {FramesOk}, неудачных {FramesFailed}");
        sb.AppendLine($"Сбоев перестроения сцены: {PrepareFailures}");
        sb.AppendLine($"Ошибок glGetError: {GlErrors}");
        sb.AppendLine($"Потерь контекста: {ContextLosses}, восстановлений: {Recoveries}, зависаний: {Stalls}");
        sb.AppendLine($"Последний удачный кадр: {LastFrameOk?.ToLocalTime().ToString("HH:mm:ss") ?? "не было"}");
        if (LastProblem != null) sb.AppendLine($"Последняя проблема: {LastProblem}");
        if (!string.IsNullOrEmpty(LogPath)) sb.AppendLine($"Журнал: {LogPath}");
        return sb.ToString();
    }

    // ============================================================ служебное
    private void Raise(DateTime now, string message, Exception? ex = null)
    {
        LastProblem = message;
        Log(now, message, ex);

        if (_lastProblemRaised is DateTime t && now - t < TimeSpan.FromSeconds(1)) return;
        _lastProblemRaised = now;

        try { Problem?.Invoke(message); } catch { /* обработчик не должен ронять монитор */ }
    }

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner is System.Reflection.TargetInvocationException { InnerException: { } i }) inner = i;
        return $"{inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>
    /// Запись в журнал. Одинаковые строки подряд схлопываются в счётчик:
    /// сбой, повторяющийся на каждом кадре, не должен забить диск.
    /// </summary>
    public void Log(DateTime now, string line, Exception? ex = null)
    {
        if (string.IsNullOrEmpty(LogPath)) return;

        try
        {
            if (line == _lastLine && ex == null)
            {
                _repeats++;
                return;
            }

            var sb = new StringBuilder();
            if (_repeats > 0) sb.AppendLine($"{Stamp(now)}   (предыдущая строка повторилась ещё {_repeats} раз)");
            _repeats = 0;
            _lastLine = line;

            sb.AppendLine($"{Stamp(now)} {line}");
            if (ex != null) sb.AppendLine(ex.ToString());

            string? dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes)
            {
                string old = LogPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(LogPath, old);
            }

            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // Журнал — помощник, а не участник: его сбой не должен трогать вид.
        }
    }

    private static string Stamp(DateTime now) => now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
}
