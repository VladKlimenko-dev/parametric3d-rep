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
        public Scope(string name) { _name = name; _t0 = Stopwatch.GetTimestamp(); }
        public void Dispose() => Record(_name, Stopwatch.GetElapsedTime(_t0).TotalMilliseconds);
    }
}
