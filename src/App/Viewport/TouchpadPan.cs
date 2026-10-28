namespace Parametric3D.App.Viewport;

/// <summary>Касание сенсорной панели в кадре: номер касания, положение в мм, палец прижат.</summary>
public readonly record struct TouchContact(int Id, double X, double Y, bool Tip);

/// <summary>
/// k127 (замечание 28.10, 14:36, п. 1): СДВИГ ВИДА ТРЕМЯ ПАЛЬЦАМИ на сенсорной панели (ноутбук без мыши).
/// Не зависит от платформы: получает отчёты панели (Windows Precision Touchpad — WinTouchpad), собирает из них кадры
/// (в «смешанном» режиме панель шлёт кадр несколькими отчётами: число касаний — только в первом) и, пока прижаты ровно
/// три пальца с теми же номерами, отдаёт сдвиг их центра (мм). Другое число пальцев, новый палец или отрыв — сдвиг
/// начинается заново (без скачка). P3D_TOUCHPAN=0 — сдвига нет.
/// </summary>
public sealed class TouchpadPan
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_TOUCHPAN") == "0";
    public static long Frames, PanSteps;

    /// <summary>Сдвиг центра трёх пальцев, мм (X — вправо, Y — вниз, как на панели).</summary>
    public event Action<double, double>? Pan;

    private readonly List<TouchContact> _frame = new();
    private int _expected;
    private (int A, int B, int C)? _ids;
    private double _cx, _cy;

    /// <summary>Отчёт панели: число касаний кадра (0 — продолжение кадра) и касания отчёта по порядку слотов.</summary>
    public void AddReport(int contactCount, IReadOnlyList<TouchContact> contacts)
    {
        if (contactCount > 0) { _frame.Clear(); _expected = contactCount; }
        if (_expected <= 0) return;
        foreach (var c in contacts)
        {
            if (_frame.Count >= _expected) break;   // слоты сверх числа касаний — устаревшие данные
            _frame.Add(c);
        }
        if (_frame.Count < _expected) return;
        _expected = 0;
        OnFrame(_frame);
    }

    /// <summary>Кадр целиком (все касания).</summary>
    public void OnFrame(IReadOnlyList<TouchContact> contacts)
    {
        Interlocked.Increment(ref Frames);
        int n = 0; double sx = 0, sy = 0; Span<int> id = stackalloc int[3];
        foreach (var c in contacts)
        {
            if (!c.Tip) continue;
            if (n < 3) id[n] = c.Id;
            n++; sx += c.X; sy += c.Y;
        }
        if (Off || n != 3) { _ids = null; return; }
        id.Sort();
        var key = (id[0], id[1], id[2]);
        double cx = sx / 3, cy = sy / 3;
        if (_ids == key)
        {
            double dx = cx - _cx, dy = cy - _cy;
            if (dx != 0 || dy != 0) { Interlocked.Increment(ref PanSteps); Pan?.Invoke(dx, dy); }
        }
        _ids = key; _cx = cx; _cy = cy;
    }

    public void Reset() { _ids = null; _expected = 0; _frame.Clear(); }
}
