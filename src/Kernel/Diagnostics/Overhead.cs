namespace Parametric3D.Kernel.Diagnostics;

/// <summary>
/// g.13: ВРЕМЯ СВЕРХ ПОСТРОЕНИЯ B-rep — проверки результата (SolidValidator, сверки маршрута) и разметка граней для показа.
/// Сравнение с OpenCascade честно только «B-rep против B-rep»: BRepAlgoAPI не проверяет тело и не строит сетку. Учитывается
/// внешний уровень вложения в потоке (вложенные области не считаются дважды); счётчик общий (одна операция за раз).
/// </summary>
public static class Overhead
{
    static long _ticks;
    [ThreadStatic] static int _depth;
    public static long Ticks => Interlocked.Read(ref _ticks);
    public static double Ms => Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static IDisposable Scope() => new S();
    sealed class S : IDisposable
    {
        readonly long _t0; readonly bool _outer;
        public S() { _outer = _depth++ == 0; _t0 = _outer ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; }
        public void Dispose() { _depth--; if (_outer) Interlocked.Add(ref _ticks, System.Diagnostics.Stopwatch.GetTimestamp() - _t0); }
    }
}
