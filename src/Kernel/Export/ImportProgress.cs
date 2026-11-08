namespace Parametric3D.Kernel.Export;

/// <summary>
/// k184: ХОД ИМПОРТА ДЛЯ СТРОКИ СОСТОЯНИЯ — доля выполненной работы 0…1 по этапам с весами: перевод Parasolid в STEP
/// (0–0.15, по граням), разбор STEP (0.15–0.2), разметка граней по контурам (0.2–0.92, по готовым граням всех тел), лечение
/// импорта (0.92–0.97), сборка документа (0.97–1). Веса — по замерам корпуса из 412 деталей SolidWorks: разметка граней
/// занимает основную часть времени. Значение только растёт; читается окном 10 раз в секунду. Один импорт за раз.
/// </summary>
public static class ImportProgress
{
    static double _value; static string _stage = "";
    static int _facesTotal, _facesDone; static bool _totalFixed;
    public static bool Active { get; private set; }
    public static double Value => Volatile.Read(ref _value);
    public static string Stage => _stage;

    public static void Begin() { _value = 0; _stage = ""; _facesTotal = 0; _facesDone = 0; _totalFixed = false; Active = true; }

    /// <summary>Число граней известно заранее (перевод Parasolid): дальше разметка считается от него.</summary>
    public static void SetFaceTotal(int n) { if (!Active || n <= 0) return; _facesTotal = n; _totalFixed = true; }
    public static void End() { Active = false; _value = 1; }

    /// <summary>Доля f этапа [a, b].</summary>
    public static void Set(string stage, double a, double b, double f)
    {
        if (!Active) return;
        _stage = stage;
        double v = a + (b - a) * Math.Clamp(f, 0, 1);
        double cur;
        do { cur = Volatile.Read(ref _value); if (v <= cur) return; } while (Interlocked.CompareExchange(ref _value, v, cur) != cur);
    }

    /// <summary>Перевод Parasolid: готово граней из всех.</summary>
    public static void Convert(int done, int total) { if (Active && total > 0) Set("convert", 0, 0.15, (double)done / total); }

    /// <summary>Разметка граней: добавить грани тела к общему числу (по мере чтения тел).</summary>
    public static void FacesAdded(int n) { if (Active && !_totalFixed) Interlocked.Add(ref _facesTotal, n); }
    public static void FaceDone()
    {
        if (!Active) return;
        int d = Interlocked.Increment(ref _facesDone), t = Math.Max(1, Volatile.Read(ref _facesTotal));
        Set("mesh", 0.2, 0.92, (double)d / t);
    }
}
