using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Parametric3D.App.Views;

/// <summary>
/// k162 (просьба 05.11, 08:31): ширина боковой панели — перетаскиванием её левого края (5 точек у края; курсор ↔). Ширина
/// запоминается на время работы приложения (по ключу панели).
/// </summary>
public static class PanelResize
{
    static readonly Dictionary<string, double> Remembered = new();
    public const double Grip = 5;

    public static void Attach(Control panel, string key, double min = 240, double max = 1000, Action<double>? done = null)
    {
        if (Remembered.TryGetValue(key, out var w0)) panel.Width = w0;
        bool drag = false; double startX = 0, startW = 0;
        var arrows = new Cursor(StandardCursorType.SizeWestEast);
        panel.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var p = e.GetPosition(panel);
            if (p.X > Grip || !e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed) return;
            drag = true; startX = e.GetPosition(null).X; startW = double.IsNaN(panel.Width) ? panel.Bounds.Width : panel.Width;
            e.Pointer.Capture(panel); e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        panel.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (drag)
            {
                double w = Math.Clamp(startW - (e.GetPosition(null).X - startX), min, max);
                panel.Width = w; Remembered[key] = w; e.Handled = true;
                return;
            }
            panel.Cursor = e.GetPosition(panel).X <= Grip ? arrows : null;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        panel.AddHandler(InputElement.PointerReleasedEvent, (_, e) => { if (drag) { drag = false; e.Pointer.Capture(null); e.Handled = true; done?.Invoke(panel.Width); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Ширина, запомненная для панели (проверки).</summary>
    public static double? WidthOf(string key) => Remembered.TryGetValue(key, out var w) ? w : null;
}
