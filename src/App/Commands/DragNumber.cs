using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Parametric3D.App.Commands;

/// <summary>
/// k139 (замечание 30.10, 00:56, пп. 1, 3): ПРОТЯЖКА ЧИСЛА. Нажать левую кнопку на стрелках поля и тянуть вверх — значение
/// растёт, вниз — убывает (шаг поля на каждые <see cref="PixelsPerStep"/> пикселей; Shift — ×10, Ctrl — ×0.1). Щелчок без
/// протяжки — один шаг, как обычные стрелки. Значение меняется при протяжке, а не по её окончании, — вид следует сразу.
/// Мутация P3D_SCRUB=0 — протяжки нет.
/// </summary>
public static class Scrub
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_SCRUB") == "0";
    public const double PixelsPerStep = 4;
    /// <summary>Сдвиг протяжки (пикселей вверх) → число шагов с учётом Shift/Ctrl.</summary>
    public static double Steps(double dyUp, KeyModifiers mods)
    {
        double k = mods.HasFlag(KeyModifiers.Shift) ? 10 : mods.HasFlag(KeyModifiers.Control) ? 0.1 : 1;
        return Math.Round(dyUp / PixelsPerStep) * k;
    }

    /// <summary>
    /// Протяжка на элементе zone: get — значение в начале, set(v) — новое значение во время протяжки, click(dir) — щелчок без
    /// протяжки (dir = +1 в верхней половине, −1 в нижней), end — после отпускания.
    /// </summary>
    public static void Attach(Control zone, Func<double> get, Action<double> set, Func<double> step, Action<int>? click = null, Action? end = null)
    {
        if (Off) return;
        Point? start = null; double v0 = 0; bool moved = false;
        zone.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        zone.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(zone).Properties.IsLeftButtonPressed) return;
            start = e.GetPosition(zone); v0 = get(); moved = false;
            e.Pointer.Capture(zone); e.Handled = true;
        }, RoutingStrategies.Tunnel);
        zone.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (start is not { } s) return;
            double dy = s.Y - e.GetPosition(zone).Y;
            if (!moved && Math.Abs(dy) < 3) return;
            moved = true;
            set(v0 + Steps(dy, e.KeyModifiers) * step());
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        zone.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (start is not { } s) return;
            start = null; e.Pointer.Capture(null); e.Handled = true;
            if (!moved) click?.Invoke(s.Y < zone.Bounds.Height / 2 ? 1 : -1);
            end?.Invoke();
        }, RoutingStrategies.Tunnel);
        zone.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => { if (start != null) { start = null; end?.Invoke(); } });
    }
}

/// <summary>
/// Поле числа со стрелками и протяжкой (см. <see cref="Scrub"/>).
/// k185 (замечание 10.11, 08:19, а3.3): ПРОТЯЖКА ПО ВСЕМУ ПОЛЮ, как в Figma. Нажать левую кнопку на поле и тянуть вверх или
/// вправо — значение растёт, вниз или влево — убывает (сдвиг считается как «вверх + вправо»). На стрелках протяжка начинается
/// сразу; на тексте — только когда мышь сдвинулась на <see cref="TextDragPx"/> пикселей, поэтому щелчок в текст по-прежнему
/// ставит курсор ввода, а двойной щелчок выделяет. Мутация P3D_SCRUBANY=0 — протяжка только по стрелкам и только по вертикали.
/// </summary>
public class DragNumber : NumericUpDown
{
    public static readonly bool AnyOff = Environment.GetEnvironmentVariable("P3D_SCRUBANY") == "0";
    public const double TextDragPx = 4;
    protected override Type StyleKeyOverride => typeof(NumericUpDown);
    bool _attached;
    public DragNumber()
    {
        if (!Scrub.Off) ToolTip.SetTip(this, Parametric3D.App.Loc.L(AnyOff
            ? "Стрелки: щелчок — шаг; тянуть вверх или вниз с нажатой левой кнопкой — изменить значение (Shift ×10, Ctrl ×0.1)"
            : "Тянуть с нажатой левой кнопкой вверх или вправо — больше, вниз или влево — меньше (Shift ×10, Ctrl ×0.1); стрелки: щелчок — шаг"));
    }

    /// <summary>Сдвиг протяжки в пикселях «вверх» с учётом горизонтали: вправо — как вверх.</summary>
    public static double Shift(Point start, Point now) => AnyOff ? start.Y - now.Y : (start.Y - now.Y) + (now.X - start.X);

    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_attached || Scrub.Off) return;
        _attached = true;
        AddHandler(PointerPressedEvent, (s, a) =>
        {
            if (!a.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (OnArrows(a.Source))
            {
                _drag = a.GetPosition(this); _v0 = (double)(Value ?? 0); _moved = false; _fromText = false; _dir = DirOf(a.Source);
                a.Pointer.Capture(this); a.Handled = true;
                return;
            }
            // на тексте: нажатие уходит полю ввода; протяжка начнётся, только если мышь заметно сдвинется
            if (!AnyOff && IsEnabled) { _pend = a.GetPosition(this); _v0 = (double)(Value ?? 0); }
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (s, a) =>
        {
            if (_drag is { } p)
            {
                double d = Shift(p, a.GetPosition(this));
                if (!_moved && Math.Abs(d) < 3) return;
                _moved = true; ScrubTo(_v0 + Scrub.Steps(d, a.KeyModifiers) * (double)Increment); a.Handled = true;
                return;
            }
            if (_pend is { } q)
            {
                if (!a.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pend = null; return; }
                var now = a.GetPosition(this);
                if (Math.Abs(now.X - q.X) + Math.Abs(now.Y - q.Y) < TextDragPx) return;
                // протяжка с текста: перехватить мышь у поля ввода, снять выделение
                _pend = null; _drag = q; _moved = true; _fromText = true;
                a.Pointer.Capture(this);
                if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } tb) tb.ClearSelection();
                Cursor = new Cursor(StandardCursorType.SizeAll);
                ScrubTo(_v0 + Scrub.Steps(Shift(q, now), a.KeyModifiers) * (double)Increment);
                a.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (s, a) =>
        {
            _pend = null;
            if (_drag == null) return;
            _drag = null; a.Pointer.Capture(null); a.Handled = true;
            if (_fromText) { Cursor = null; _fromText = false; return; }
            if (!_moved) ScrubTo((double)(Value ?? 0) + _dir * (double)Increment);
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => { if (_fromText && _drag != null) { _drag = null; _fromText = false; Cursor = null; } else if (!_fromText) _drag = null; });
    }
    Point? _drag, _pend; double _v0; bool _moved, _fromText; int _dir = 1;

    /// <summary>Протяжка с текста поля: из точки start в точку now (разбор и проверки; та же арифметика, что у мыши).</summary>
    public void ScrubFromTo(Point start, Point now, KeyModifiers mods = KeyModifiers.None) =>
        ScrubTo((double)(Value ?? 0) + Scrub.Steps(Shift(start, now), mods) * (double)Increment);

    /// <summary>Значение протяжки — в пределах поля.</summary>
    public void ScrubTo(double v)
    {
        decimal d = (decimal)Math.Round(v, 6);
        if (d < Minimum) d = Minimum; if (d > Maximum) d = Maximum;
        Value = d;
    }
    /// <summary>Протяжка на dyUp пикселей вверх от значения v0 (разбор и проверки).</summary>
    public void ScrubBy(double dyUp, KeyModifiers mods = KeyModifiers.None) => ScrubTo((double)(Value ?? 0) + Scrub.Steps(dyUp, mods) * (double)Increment);

    bool OnArrows(object? src)
    {
        for (var v = src as Visual; v != null && !ReferenceEquals(v, this); v = v.GetVisualParent())
        {
            if (v is TextBox) return false;
            if (v is Button) return true;
        }
        return false;
    }
    int DirOf(object? src)
    {
        for (var v = src as Visual; v != null && !ReferenceEquals(v, this); v = v.GetVisualParent())
            if (v is Button b) return (b.Name ?? "").Contains("Decrease") ? -1 : 1;
        return 1;
    }
}
