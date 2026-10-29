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

/// <summary>Поле числа со стрелками и протяжкой по стрелкам (см. <see cref="Scrub"/>).</summary>
public class DragNumber : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);
    bool _attached;
    public DragNumber()
    {
        if (!Scrub.Off) ToolTip.SetTip(this, Parametric3D.App.Loc.L("Стрелки: щелчок — шаг; тянуть вверх или вниз с нажатой левой кнопкой — изменить значение (Shift ×10, Ctrl ×0.1)"));
    }

    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_attached || Scrub.Off) return;
        _attached = true;
        // протяжка — по стрелкам (нажатие на текст остаётся вводом)
        AddHandler(PointerPressedEvent, (s, a) =>
        {
            if (!OnArrows(a.Source)) return;
            _drag = a.GetPosition(this); _v0 = (double)(Value ?? 0); _moved = false; _dir = DirOf(a.Source);
            a.Pointer.Capture(this); a.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (s, a) =>
        {
            if (_drag is not { } p) return;
            double dy = p.Y - a.GetPosition(this).Y;
            if (!_moved && Math.Abs(dy) < 3) return;
            _moved = true; ScrubTo(_v0 + Scrub.Steps(dy, a.KeyModifiers) * (double)Increment); a.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (s, a) =>
        {
            if (_drag == null) return;
            _drag = null; a.Pointer.Capture(null); a.Handled = true;
            if (!_moved) ScrubTo((double)(Value ?? 0) + _dir * (double)Increment);
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => _drag = null);
    }
    Point? _drag; double _v0; bool _moved; int _dir = 1;

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
