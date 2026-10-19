using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.Services;

namespace Parametric3D.App.Views;

/// <summary>Вид панели поверх окна вида.</summary>
public enum PanelMode
{
    /// <summary>Как в разметке окна (или отпущена посреди вида).</summary>
    Float,
    /// <summary>У левой или правой стороны: вертикальная полоса, только иконки.</summary>
    StripV,
    /// <summary>У верхней или нижней стороны: горизонтальная полоса, только иконки, все кнопки (без «Ещё…»).</summary>
    StripH,
    /// <summary>В углу: один ряд кнопок с названиями.</summary>
    Row,
}

/// <summary>
/// ПЕРЕМЕЩАЕМАЯ ПАНЕЛЬ ПОВЕРХ ВИДА, ПРИЛИПАЮЩАЯ К ЕГО СТОРОНАМ И УГЛАМ.
///
/// Ручка ⠿ — тянуть мышью. Отпущенная ближе <see cref="SnapPx"/> к стороне вида панель прилипает: у левой и правой —
/// вертикальная полоса иконок, у верхней и нижней — горизонтальная полоса иконок (все кнопки, без «Ещё…»); у двух
/// сторон сразу (угол) — один ряд кнопок с названиями. Где ставить прилипшую панель, решает <see cref="DockManager"/>:
/// панели не перекрываются — следующая на той же стороне встаёт рядом, если помещается, иначе дальше от края.
/// Двойной щелчок по ручке — на место по умолчанию. Место запоминается (UserPrefs.PanelPlaces).
/// </summary>
public sealed class DockablePanel
{
    public const double SnapPx = 48, Gap = 6;
    public enum Side { Default, Free, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public string Key { get; }
    public Border Panel { get; }
    public Side Docked { get; private set; } = Side.Default;
    public PanelMode Mode { get; private set; } = PanelMode.Float;
    /// <summary>Сейчас горизонтальная.</summary>
    public bool Horizontal => Mode is PanelMode.StripH or PanelMode.Row || (Mode == PanelMode.Float && _h0);
    public bool UserPlaced => Docked != Side.Default;
    public bool IsCorner => Docked is Side.TopLeft or Side.TopRight or Side.BottomLeft or Side.BottomRight;
    public bool IsDocked => Docked is not (Side.Default or Side.Free);

    /// <summary>Доля положения центра вдоль стороны (0…1) — куда панель хотят поставить.</summary>
    internal double Along = 0.5;
    /// <summary>Порядок прилипания: раньше прилипшая стоит ближе к краю.</summary>
    internal long Order;

    private readonly DockManager _mgr;
    private readonly HorizontalAlignment _ha0;
    private readonly VerticalAlignment _va0;
    private readonly Thickness _m0;
    private readonly bool _h0;
    private readonly Action<PanelMode> _build;
    private readonly DockPanel _wrap;
    private readonly Border _grip;
    private readonly TextBlock _gripText;
    private Point _grab;
    private bool _dragging;

    /// <summary>Изменилось место (панели, чьё место считается от неё, пересчитывают своё).</summary>
    public event Action? Moved;

    public DockablePanel(string key, Border panel, DockManager mgr, bool horizontal, Action<PanelMode> build)
    {
        Key = key; Panel = panel; _mgr = mgr; _build = build;
        _ha0 = panel.HorizontalAlignment; _va0 = panel.VerticalAlignment; _m0 = panel.Margin; _h0 = horizontal;
        var child = panel.Child;
        panel.Child = null;
        _gripText = new TextBlock { Text = "⠿", FontSize = 13, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll), Child = _gripText, Padding = new Thickness(1) };
        ToolTip.SetTip(_grip, Loc.L("Перетащите панель; у края вида она прилипает. Двойной щелчок — на место"));
        _wrap = new DockPanel();
        _wrap.Children.Add(_grip);
        if (child != null) _wrap.Children.Add(child);
        panel.Child = _wrap;
        SetGripDock();
        mgr.Add(this);
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
            var p = e.GetPosition(_mgr.Host);
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
        panel.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) _mgr.Relayout(); };
    }

    private void SetGripDock()
    {
        DockPanel.SetDock(_grip, Horizontal ? Dock.Left : Dock.Top);
        _grip.Width = Horizontal ? 12 : double.NaN;
        _grip.Height = Horizontal ? double.NaN : 12;
        _gripText.RenderTransform = Horizontal ? null : new RotateTransform(90);
    }

    private void SetMode(PanelMode m)
    {
        if (m == Mode) return;
        Mode = m;
        SetGripDock();
        _build(m);
    }

    /// <summary>Свободное место: левый верхний угол панели в координатах вида (вид панели — по умолчанию).</summary>
    public void MoveTo(double x, double y)
    {
        bool wasDocked = IsDocked;
        Docked = Side.Free;
        SetMode(PanelMode.Float);
        double w = Panel.Bounds.Width, h = Panel.Bounds.Height;
        x = Math.Clamp(x, 0, Math.Max(0, _mgr.Host.Bounds.Width - w));
        y = Math.Clamp(y, 0, Math.Max(0, _mgr.Host.Bounds.Height - h));
        Panel.HorizontalAlignment = HorizontalAlignment.Left;
        Panel.VerticalAlignment = VerticalAlignment.Top;
        Panel.Margin = new Thickness(x, y, 0, 0);
        if (wasDocked) _mgr.Relayout();
        Moved?.Invoke();
    }

    /// <summary>Прилипнуть к ближайшей стороне или углу, если они ближе SnapPx; иначе остаться свободной.</summary>
    public void Snap()
    {
        double x = Panel.Margin.Left, y = Panel.Margin.Top, w = Panel.Bounds.Width, h = Panel.Bounds.Height;
        double W = _mgr.Host.Bounds.Width, H = _mgr.Host.Bounds.Height;
        double l = x, r = W - x - w, t = y, b = H - y - h;
        bool nl = l <= SnapPx, nr = r <= SnapPx, nt = t <= SnapPx, nb = b <= SnapPx;
        Side s = (nt, nb, nl, nr) switch
        {
            (true, _, true, _) => Side.TopLeft,
            (true, _, _, true) => Side.TopRight,
            (_, true, true, _) => Side.BottomLeft,
            (_, true, _, true) => Side.BottomRight,
            _ => new[] { (Side.Left, l), (Side.Right, r), (Side.Top, t), (Side.Bottom, b) }.OrderBy(q => q.Item2).First() is var best && best.Item2 <= SnapPx ? best.Item1 : Side.Free,
        };
        if (s == Side.Free) { Docked = Side.Free; return; }
        double along = s is Side.Left or Side.Right ? (y + h / 2) / Math.Max(1, H) : (x + w / 2) / Math.Max(1, W);
        DockTo(s, along);
    }

    /// <summary>Прилипнуть к стороне (along — доля положения центра вдоль неё) или к углу.</summary>
    public void DockTo(Side side, double along = 0.5)
    {
        Along = Math.Clamp(along, 0, 1);
        Docked = side;
        Order = _mgr.NextOrder();
        SetMode(side switch { Side.Left or Side.Right => PanelMode.StripV, Side.Top or Side.Bottom => PanelMode.StripH, _ => PanelMode.Row });
        _mgr.Relayout();
        Moved?.Invoke();
    }

    /// <summary>На место по умолчанию (как в разметке окна).</summary>
    public void Reset()
    {
        bool was = IsDocked;
        Docked = Side.Default;
        SetMode(PanelMode.Float);
        Panel.HorizontalAlignment = _ha0; Panel.VerticalAlignment = _va0; Panel.Margin = _m0;
        if (was) _mgr.Relayout();
        Moved?.Invoke();
    }

    /// <summary>Свободная панель за пределами вида после изменения его размеров — внутрь.</summary>
    internal void KeepInside()
    {
        if (Docked != Side.Free) return;
        double w = Panel.Bounds.Width, h = Panel.Bounds.Height, x = Panel.Margin.Left, y = Panel.Margin.Top;
        double cx = Math.Clamp(x, 0, Math.Max(0, _mgr.Host.Bounds.Width - w)), cy = Math.Clamp(y, 0, Math.Max(0, _mgr.Host.Bounds.Height - h));
        if (Math.Abs(cx - x) > 0.5 || Math.Abs(cy - y) > 0.5) Panel.Margin = new Thickness(cx, cy, 0, 0);
    }

    public void Save()
    {
        var map = UserPrefs.Current.PanelPlaces;
        if (Docked == Side.Default) map.Remove(Key);
        else map[Key] = Docked == Side.Free
            ? FormattableString.Invariant($"Free|{Panel.Margin.Left:0}|{Panel.Margin.Top:0}")
            : FormattableString.Invariant($"{Docked}|{Along:0.###}|{Order}");
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
            else if (Enum.TryParse<Side>(a[0], out var s) && s is not (Side.Default or Side.Free) && a.Length >= 2)
            {
                DockTo(s, double.Parse(a[1], ci));
                if (a.Length >= 3 && long.TryParse(a[2], out var o)) { Order = o; _mgr.SeenOrder(o); _mgr.Relayout(); }
            }
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
                    sep.Width = horizontal ? 1 : double.NaN; sep.Height = horizontal ? 24 : 1;
                    sep.Margin = horizontal ? new Thickness(3, 4) : new Thickness(4, 3);
                }
            }
    }
}

/// <summary>
/// РАССТАНОВКА ПРИЛИПШИХ ПАНЕЛЕЙ. Панели ставятся в порядке прилипания; каждая — у своей стороны (угла) на ближайшем к краю
/// «ряду», где она помещается, не задевая уже поставленных и видимых свободных панелей; в ряду — как можно ближе к месту,
/// где её отпустили (рядом с соседней, если влезает). Полосы одной толщины: размер кнопок — один (UiLook).
/// </summary>
public sealed class DockManager
{
    public Control Host { get; }
    private readonly List<DockablePanel> _panels = new();
    private long _order;
    private bool _busy;

    public DockManager(Control host) { Host = host; }
    /// <summary>Прочие надстройки вида, которые прилипшие панели обходят (куб вида).</summary>
    public readonly List<Control> Obstacles = new();
    public IReadOnlyList<DockablePanel> Panels => _panels;
    internal void Add(DockablePanel p) => _panels.Add(p);
    internal long NextOrder() => ++_order;
    internal void SeenOrder(long o) { if (o > _order) _order = o; }

    public void Relayout()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            double W = Host.Bounds.Width, H = Host.Bounds.Height, G = DockablePanel.Gap;
            if (W < 50 || H < 50) return;
            var placed = new List<Rect>();
            foreach (var o in Obstacles) if (o.IsVisible && o.Bounds.Width > 0) placed.Add(o.Bounds.Inflate(2));
            // препятствия — видимые свободные панели и панели на своих местах по умолчанию
            foreach (var p in _panels)
            {
                if (p.IsDocked) p.KeepInside();
                else if (p.Panel.IsVisible && p.Panel.Bounds.Width > 0) placed.Add(p.Panel.Bounds);
            }
            foreach (var p in _panels.Where(q => q.IsDocked && q.Panel.IsVisible).OrderBy(q => q.Order))
            {
                p.Panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // желаемый размер — с полями (Margin): поля — место панели, а не её размер
                var m = p.Panel.Margin;
                double w = Math.Min(p.Panel.DesiredSize.Width - m.Left - m.Right, W - 2 * G), h = Math.Min(p.Panel.DesiredSize.Height - m.Top - m.Bottom, H - 2 * G);
                var r = Place(p, w, h, W, H, placed);
                placed.Add(r);
                p.Panel.HorizontalAlignment = HorizontalAlignment.Left;
                p.Panel.VerticalAlignment = VerticalAlignment.Top;
                p.Panel.Margin = new Thickness(r.X, r.Y, 0, 0);
            }
        }
        finally { _busy = false; }
    }

    private static Rect Place(DockablePanel p, double w, double h, double W, double H, List<Rect> placed)
    {
        double G = DockablePanel.Gap;
        var s = p.Docked;
        bool vertical = s is DockablePanel.Side.Left or DockablePanel.Side.Right;
        double len = vertical ? h : w, thick = vertical ? w : h, L = vertical ? H : W, D = vertical ? W : H;
        // отступ от края: G, затем — за каждой уже поставленной панелью этой стороны
        Rect Make(double off, double at) => s switch
        {
            DockablePanel.Side.Left => new Rect(off, at, w, h),
            DockablePanel.Side.Right => new Rect(W - off - w, at, w, h),
            DockablePanel.Side.Top or DockablePanel.Side.TopLeft or DockablePanel.Side.TopRight => new Rect(at, off, w, h),
            _ => new Rect(at, H - off - h, w, h),
        };
        bool Free(Rect r) => r.X >= -0.5 && r.Y >= -0.5 && r.Right <= W + 0.5 && r.Bottom <= H + 0.5 && placed.All(q => !q.Intersects(r.Deflate(0.5)));
        var offsets = new SortedSet<double> { G };
        foreach (var q in placed)
        {
            double far = s switch
            {
                DockablePanel.Side.Left => q.Right, DockablePanel.Side.Right => W - q.X,
                DockablePanel.Side.Top or DockablePanel.Side.TopLeft or DockablePanel.Side.TopRight => q.Bottom,
                _ => H - q.Y,
            };
            if (far + G > G && far + G + thick <= D) offsets.Add(far + G);
        }
        double want = p.IsCorner
            ? (s is DockablePanel.Side.TopLeft or DockablePanel.Side.BottomLeft ? G : W - G - w)
            : Math.Clamp(p.Along * L - len / 2, G, Math.Max(G, L - len - G));
        foreach (double off in offsets)
        {
            // места вдоль стороны: желаемое, затем вплотную к соседям — ближайшее к желаемому
            var cands = new List<double> { want };
            foreach (var q in placed)
            {
                double qs = vertical ? q.Y : q.X, qe = vertical ? q.Bottom : q.Right;
                cands.Add(qe + G); cands.Add(qs - len - G);
            }
            if (!p.IsCorner) { cands.Add(G); cands.Add(L - len - G); }
            foreach (double at in cands.Where(a => a >= G - 0.5 && a + len <= L - G + 0.5).OrderBy(a => Math.Abs(a - want)))
            {
                var r = Make(off, at);
                if (Free(r)) return r;
            }
        }
        return Make(G, want);                                        // места нет — на желаемое (перекрытие лучше, чем пропажа)
    }
}
