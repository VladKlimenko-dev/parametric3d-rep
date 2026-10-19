using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.Services;

namespace Parametric3D.App.Views;

/// <summary>
/// ПЕРЕМЕЩАЕМАЯ ПАНЕЛЬ ПОВЕРХ ВИДА, ПРИЛИПАЮЩАЯ К ЕГО СТОРОНАМ.
///
/// У панели появляется ручка (точки ⠿). Её тянут мышью — панель едет за курсором в пределах вида. Отпустили ближе
/// <see cref="SnapPx"/> к стороне вида — панель прилипает к ней (и остаётся у неё при изменении размеров окна);
/// у верхней и нижней стороны панель становится горизонтальной, у левой и правой — вертикальной. Дальше от сторон —
/// остаётся там, где отпустили. Двойной щелчок по ручке — на место по умолчанию. Место запоминается (UserPrefs).
/// </summary>
public sealed class DockablePanel
{
    public const double SnapPx = 48, Gap = 10;
    public enum Side { Default, Free, Left, Right, Top, Bottom }

    public string Key { get; }
    public Border Panel { get; }
    public Side Docked { get; private set; } = Side.Default;
    /// <summary>Сейчас горизонтальная.</summary>
    public bool Horizontal { get; private set; }
    public bool UserPlaced => Docked != Side.Default;

    private readonly Control _host;
    private readonly HorizontalAlignment _ha0;
    private readonly VerticalAlignment _va0;
    private readonly Thickness _m0;
    private readonly bool _h0;
    private readonly Action<bool> _orient;
    private readonly DockPanel _wrap;
    private readonly Border _grip;
    private readonly TextBlock _gripText;
    private Point _grab;
    private bool _dragging;

    /// <summary>Изменилось место (панели, чьё место считается от неё, пересчитывают своё).</summary>
    public event Action? Moved;

    public DockablePanel(string key, Border panel, Control host, bool horizontal, Action<bool> orient)
    {
        Key = key; Panel = panel; _host = host; _orient = orient;
        _ha0 = panel.HorizontalAlignment; _va0 = panel.VerticalAlignment; _m0 = panel.Margin; _h0 = horizontal; Horizontal = horizontal;
        var child = panel.Child;
        panel.Child = null;
        _gripText = new TextBlock { Text = "⠿", FontSize = 13, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll), Child = _gripText, Padding = new Thickness(2) };
        ToolTip.SetTip(_grip, Loc.L("Перетащите панель; у края вида она прилипает. Двойной щелчок — на место"));
        _wrap = new DockPanel();
        _wrap.Children.Add(_grip);
        if (child != null) _wrap.Children.Add(child);
        panel.Child = _wrap;
        SetGripDock();
        _grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2) { Reset(); Save(); e.Handled = true; return; }
            _grab = e.GetPosition(Panel);
            _dragging = true;
            e.Pointer.Capture(_grip);
            e.Handled = true;
        };
        _grip.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            var p = e.GetPosition(_host);
            MoveTo(p.X - _grab.X, p.Y - _grab.Y);
            e.Handled = true;
        };
        _grip.PointerReleased += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            Snap();
            Save();
            e.Handled = true;
        };
    }

    private void SetGripDock()
    {
        DockPanel.SetDock(_grip, Horizontal ? Dock.Left : Dock.Top);
        _grip.Width = Horizontal ? 12 : double.NaN;
        _grip.Height = Horizontal ? double.NaN : 12;
        _gripText.RenderTransform = Horizontal ? null : new RotateTransform(90);
    }

    private void SetHorizontal(bool h)
    {
        if (h == Horizontal) return;
        Horizontal = h;
        SetGripDock();
        _orient(h);
    }

    /// <summary>Свободное место: левый верхний угол панели в координатах вида.</summary>
    public void MoveTo(double x, double y)
    {
        double w = Panel.Bounds.Width, h = Panel.Bounds.Height;
        x = Math.Clamp(x, 0, Math.Max(0, _host.Bounds.Width - w));
        y = Math.Clamp(y, 0, Math.Max(0, _host.Bounds.Height - h));
        Panel.HorizontalAlignment = HorizontalAlignment.Left;
        Panel.VerticalAlignment = VerticalAlignment.Top;
        Panel.Margin = new Thickness(x, y, 0, 0);
        Docked = Side.Free;
        Moved?.Invoke();
    }

    /// <summary>Прилипнуть к ближайшей стороне, если она ближе SnapPx.</summary>
    public void Snap()
    {
        double x = Panel.Margin.Left, y = Panel.Margin.Top, w = Panel.Bounds.Width, h = Panel.Bounds.Height;
        double W = _host.Bounds.Width, H = _host.Bounds.Height;
        var d = new (Side S, double D)[] { (Side.Left, x), (Side.Right, W - x - w), (Side.Top, y), (Side.Bottom, H - y - h) };
        var best = d.OrderBy(t => t.D).First();
        if (best.D > SnapPx) { Docked = Side.Free; return; }
        // вдоль стороны — доля положения центра (окно меняет размер — панель остаётся на том же месте стороны)
        double along = best.S is Side.Left or Side.Right ? (y + h / 2) / Math.Max(1, H) : (x + w / 2) / Math.Max(1, W);
        DockTo(best.S, along);
    }

    private double _along = 0.5;

    /// <summary>Прилипнуть к стороне; along — доля положения центра панели вдоль стороны (0…1).</summary>
    public void DockTo(Side side, double along)
    {
        _along = Math.Clamp(along, 0, 1);
        Docked = side;
        SetHorizontal(side is Side.Top or Side.Bottom);
        Panel.HorizontalAlignment = side switch { Side.Left => HorizontalAlignment.Left, Side.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };
        Panel.VerticalAlignment = side switch { Side.Top => VerticalAlignment.Top, Side.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Top };
        Relayout();
        Moved?.Invoke();
    }

    /// <summary>Пересчитать отступ вдоль стороны (после изменения размеров вида или панели).</summary>
    public void Relayout()
    {
        if (Docked is Side.Default or Side.Free)
        {
            if (Docked == Side.Free) { var m = Panel.Margin; MoveToKeep(m.Left, m.Top); }
            return;
        }
        double W = _host.Bounds.Width, H = _host.Bounds.Height, w = Panel.Bounds.Width, h = Panel.Bounds.Height;
        double pos;
        switch (Docked)
        {
            case Side.Left: case Side.Right:
                pos = Math.Clamp(_along * H - h / 2, Gap, Math.Max(Gap, H - h - Gap));
                Panel.Margin = Docked == Side.Left ? new Thickness(Gap, pos, 0, 0) : new Thickness(0, pos, Gap, 0);
                break;
            default:
                pos = Math.Clamp(_along * W - w / 2, Gap, Math.Max(Gap, W - w - Gap));
                Panel.Margin = Docked == Side.Top ? new Thickness(pos, Gap, 0, 0) : new Thickness(pos, 0, 0, Gap);
                break;
        }
    }

    private void MoveToKeep(double x, double y)
    {
        double w = Panel.Bounds.Width, h = Panel.Bounds.Height;
        double cx = Math.Clamp(x, 0, Math.Max(0, _host.Bounds.Width - w)), cy = Math.Clamp(y, 0, Math.Max(0, _host.Bounds.Height - h));
        if (Math.Abs(cx - x) > 0.5 || Math.Abs(cy - y) > 0.5) Panel.Margin = new Thickness(cx, cy, 0, 0);
    }

    /// <summary>На место по умолчанию (как в разметке окна).</summary>
    public void Reset()
    {
        Docked = Side.Default;
        SetHorizontal(_h0);
        Panel.HorizontalAlignment = _ha0; Panel.VerticalAlignment = _va0; Panel.Margin = _m0;
        Moved?.Invoke();
    }

    public void Save()
    {
        var map = UserPrefs.Current.PanelPlaces;
        if (Docked == Side.Default) map.Remove(Key);
        else map[Key] = Docked == Side.Free
            ? FormattableString.Invariant($"Free|{Panel.Margin.Left:0}|{Panel.Margin.Top:0}")
            : FormattableString.Invariant($"{Docked}|{_along:0.###}");
        try { UserPrefs.Current.Save(); } catch { }
    }

    public void Restore()
    {
        if (!UserPrefs.Current.PanelPlaces.TryGetValue(Key, out var v)) return;
        var a = v.Split('|');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            if (a[0] == "Free" && a.Length >= 3) MoveTo(double.Parse(a[1], ci), double.Parse(a[2], ci));
            else if (Enum.TryParse<Side>(a[0], out var s) && s is Side.Left or Side.Right or Side.Top or Side.Bottom && a.Length >= 2) DockTo(s, double.Parse(a[1], ci));
        }
        catch { }
    }

    /// <summary>Повернуть содержимое панели: StackPanel/WrapPanel — ориентация, разделители (1 px) — поперёк.</summary>
    public static void Reorient(Control root, bool horizontal)
    {
        switch (root)
        {
            case StackPanel sp: sp.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical; break;
            case WrapPanel wp: wp.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical; break;
        }
        if (root is Panel p)
            foreach (var c in p.Children)
            {
                if (c is Border { Child: null } sep && (sep.Width == 1 || sep.Height == 1))
                {
                    // разделитель: в горизонтальной панели — вертикальная черта, в вертикальной — горизонтальная
                    sep.Width = horizontal ? 1 : double.NaN; sep.Height = horizontal ? 28 : 1;
                    sep.Margin = horizontal ? new Thickness(4, 6) : new Thickness(4, 4);
                }
            }
    }
}
