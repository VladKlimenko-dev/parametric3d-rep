using OpenTK.Mathematics;

namespace Parametric3D.Render;

public enum TransitionKind
{
    None = 0,
    ToSketch,       // вход в эскиз
    FromSketch,     // выход из эскиза
    ToItem,         // подлёт к выбранному объекту
    ToDefault,      // возврат к сохранённому виду
    Reorient        // смена ориентации/проекции
}

/// <summary>
/// Плавный переход камеры между состояниями.
///
/// Это единственная часть старого MainView, которая перенесена целиком (по
/// вашей просьбе), но вынута из класса-контрола: раньше переход был размазан
/// по таймеру, полям source_view/target_view/default_view и switch-у на 60
/// строк внутри Tick, который заодно менял режим редактирования и вкладки
/// тулбара. Здесь переход занимается ТОЛЬКО камерой, а что делать по его
/// завершении — решает подписчик через Completed.
/// </summary>
public sealed class ViewTransition
{
    public TransitionKind Kind { get; private set; } = TransitionKind.None;
    public bool IsRunning { get; private set; }
    /// <summary>Число шагов анимации. 1 шаг ~ один тик таймера вида.</summary>
    public int Steps { get; set; } = 18;

    private CameraState _from, _to;
    private int _step;
    private object? _payload;

    /// <summary>Вызывается по завершении. Аргументы: вид перехода и переданный payload.</summary>
    public event Action<TransitionKind, object?>? Completed;

    /// <summary>Состояние, в которое возвращает ReturnToDefault (запоминается при входе в эскиз/объект).</summary>
    public CameraState Saved { get; private set; } = CameraState.Default;
    public bool HasSaved { get; private set; }

    public void Save(CameraState s) { Saved = s; HasSaved = true; }

    public void Start(TransitionKind kind, CameraState from, CameraState to, object? payload = null)
    {
        Kind = kind;
        _from = from;
        _to = to;
        _payload = payload;
        _step = 0;
        IsRunning = true;
    }

    public void Cancel()
    {
        IsRunning = false;
        Kind = TransitionKind.None;
    }

    /// <summary>Один шаг анимации. Возвращает true, если камера изменилась (нужна перерисовка).</summary>
    public bool Tick(ref CameraState current)
    {
        if (!IsRunning) return false;

        _step++;
        float t = Math.Clamp(_step / (float)Math.Max(1, Steps), 0f, 1f);
        t = Ease(t);
        current = CameraState.Lerp(_from, _to, t);

        if (_step >= Steps)
        {
            current = _to;
            IsRunning = false;
            var k = Kind;
            var p = _payload;
            Kind = TransitionKind.None;
            _payload = null;
            Completed?.Invoke(k, p);
        }
        return true;
    }

    /// <summary>Плавный вход/выход (smoothstep) — без рывков в начале и конце.</summary>
    private static float Ease(float t) => t * t * (3f - 2f * t);
}
