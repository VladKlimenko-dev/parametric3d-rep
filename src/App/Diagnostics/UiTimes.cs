using System.Diagnostics;

namespace Parametric3D.App.Diagnostics;

/// <summary>
/// ДОЛГИЕ УЧАСТКИ В ПОТОКЕ ОКНА: участок дольше порога (40 мс) запоминается — имя, длительность, время. Кнопка «Тест»
/// выводит их в отчёт: по ним видно, что именно останавливает окно (то самое «вид иногда зависает»). Последние 200 записей.
/// </summary>
public static class UiTimes
{
    public const double ThresholdMs = 40;
    private static readonly object Gate = new();
    private static readonly List<(DateTime At, string Name, double Ms)> _slow = new();

    public static Scope Measure(string name) => new(name);

    public static void Record(string name, double ms)
    {
        if (ms < ThresholdMs) return;
        lock (Gate)
        {
            _slow.Add((DateTime.Now, name, ms));
            if (_slow.Count > 200) _slow.RemoveAt(0);
        }
    }

    public static List<(DateTime At, string Name, double Ms)> Since(DateTime t)
    {
        lock (Gate) return _slow.Where(x => x.At >= t).ToList();
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _t0;
        public Scope(string name) { _name = name; _t0 = Stopwatch.GetTimestamp(); Current = name; }
        public void Dispose() { Current = null; Record(_name, Stopwatch.GetElapsedTime(_t0).TotalMilliseconds); }
    }

    /// <summary>Участок, который выполняется сейчас (для сторожа зависаний).</summary>
    public static volatile string? Current;
}

/// <summary>
/// СТОРОЖ ЗАВИСАНИЙ ОКНА (12:28, «приложение зависло, затем AccessViolation в Avalonia.OpenGL»): таймер окна отмечает
/// «жив» каждые 200 мс; отдельный поток раз в секунду проверяет отметку — окно не отвечало дольше 3 с — запись в журнал
/// приложения (%LOCALAPPDATA%/Parametric3D/app.log): сколько, какой участок выполнялся, что было последним в журнале
/// долгих участков, режим графики. После возврата — запись о длительности.
/// </summary>
public static class UiWatchdog
{
    static long _beat = Stopwatch.GetTimestamp();
    static Thread? _thread;

    public static void Start()
    {
        if (_thread != null) return;
        var t = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => Interlocked.Exchange(ref _beat, Stopwatch.GetTimestamp()));
        t.Start();
        _thread = new Thread(Watch) { IsBackground = true, Name = "P3D UI watchdog" };
        _thread.Start();
    }

    static void Watch()
    {
        bool stalled = false; double worst = 0;
        while (true)
        {
            Thread.Sleep(1000);
            double s = Stopwatch.GetElapsedTime(Interlocked.Read(ref _beat)).TotalSeconds;
            if (s > 3 && !stalled)
            {
                stalled = true;
                var last = UiTimes.Since(DateTime.Now.AddSeconds(-30)).LastOrDefault();
                Parametric3D.Core.AppLog.Write($"UI stall: the window has not responded for {s:0.0} s; running: {UiTimes.Current ?? "—"}; last slow part: {last.Name} {last.Ms:0} ms; graphics {Program.GlMode}");
            }
            if (stalled) worst = Math.Max(worst, s);
            if (stalled && s < 1) { Parametric3D.Core.AppLog.Write($"UI stall ended after {worst:0.0} s"); stalled = false; worst = 0; }
        }
    }
}
