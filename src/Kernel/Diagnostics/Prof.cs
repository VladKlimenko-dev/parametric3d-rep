using System.Diagnostics;

namespace Parametric3D.Kernel.Diagnostics;

/// <summary>
/// СЧЁТЧИК ВРЕМЕНИ ПО ЭТАПАМ ЯДРА.
///
/// Выключен по умолчанию и тогда ничего не стоит: Time возвращает пустую
/// заглушку. Включается для замеров — проверочным прогоном или тестом скорости:
///   Prof.Enabled = true; Prof.Reset(); ...; Console.WriteLine(Prof.Report());
/// </summary>
public static class Prof
{
    public static bool Enabled;

    private static readonly Dictionary<string, (long Ticks, int Count)> Totals = new();

    /// <summary>
    /// K07: выделенная память по этапам (байты, по счётчику текущего потока). Точна при одном
    /// потоке ядра; при нескольких считает только поток, открывший засечку.
    /// </summary>
    private static readonly Dictionary<string, long> Allocated = new();

    /// <summary>Очистить всё измеренное, включая счёт предикатов у булевой.</summary>
    public static void ResetAll()
    {
        Reset();
        Ops.BooleanOp.ResetCounters();
    }

    public static void Reset() { lock (Totals) { Totals.Clear(); Allocated.Clear(); } lock (Counts) Counts.Clear(); }

    public static IDisposable Time(string phase) => Enabled ? new Scope(phase) : Nothing.Instance;

    /// <summary>
    /// Записать уже измеренный отрезок времени. Нужно там, где метод уходит раньше по отказу и
    /// закрытие засечки не выполняется: отметку ставят по ходу, а не блоком.
    /// </summary>
    public static void Add(string phase, long ticks)
    {
        if (!Enabled) return;
        lock (Totals)
        {
            Totals.TryGetValue(phase, out var v);
            Totals[phase] = (v.Ticks + ticks, v.Count + 1);
        }
    }

    private static readonly Dictionary<string, long> Counts = new();

    /// <summary>Добавить к счётчику количества (не времени).</summary>
    public static void Count(string name, long value)
    {
        if (!Enabled) return;
        lock (Counts) Counts[name] = Counts.GetValueOrDefault(name) + value;
    }

    /// <summary>Значение счётчика количества (0 — не было).</summary>
    public static long CountOf(string name) { lock (Counts) return Counts.GetValueOrDefault(name); }

    public static string Report()
    {
        lock (Totals)
        {
            string times = string.Join("\n", Totals.OrderByDescending(kv => kv.Value.Ticks).Select(kv =>
                $"  {kv.Key,-34} {kv.Value.Ticks * 1000.0 / Stopwatch.Frequency,9:0.0} мс  ×{kv.Value.Count}" +
                (Allocated.TryGetValue(kv.Key, out long b) ? $"  {b / 1048576.0,8:0.0} МБ" : "")));
            lock (Counts)
                return Counts.Count == 0 ? times
                    : times + "\n" + string.Join("\n", Counts.OrderBy(kv => kv.Key).Select(kv => $"  # {kv.Key,-32} {kv.Value,12:N0}"));
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _phase;
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly long _bytes = GC.GetAllocatedBytesForCurrentThread();
        private bool _closed;

        public Scope(string phase) => _phase = phase;

        /// <summary>
        /// Закрытие ДЕЙСТВУЕТ ОДИН РАЗ. Засечку, объявленную через using, иногда закрывают и
        /// вручную, когда этап кончается раньше метода; тогда она закрывалась дважды, и в отчёт
        /// шло двойное время и двойной счёт вызовов. Измерено на построении рёбер: показывало
        /// 18 вызовов при девяти настоящих и, соответственно, вдвое большее время.
        /// </summary>
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;

            long dt = Stopwatch.GetTimestamp() - _start;
            long db = GC.GetAllocatedBytesForCurrentThread() - _bytes;
            lock (Totals)
            {
                Totals.TryGetValue(_phase, out var v);
                Totals[_phase] = (v.Ticks + dt, v.Count + 1);
                Allocated[_phase] = Allocated.GetValueOrDefault(_phase) + db;
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
