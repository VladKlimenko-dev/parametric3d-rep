using Avalonia;
using OpenTK.Mathematics;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Viewport;

/// <summary>Ручка операции у выбранного (п. 6): точка, направление «наружу», можно ли тянуть внутрь.</summary>
public sealed record OpHandle(Vector3 At, Vector3 Out, bool Outward, bool Inward);

/// <summary>
/// РУЧКИ ОПЕРАЦИЙ (как в Plasticity): у выбранного ребра, вершины или грани — стрелка наружу и стрелка внутрь. Тянут за
/// конец стрелки: направление выбирает операцию (окно сообщает знак), расстояние вдоль стрелки — её величину. Конец
/// стрелки идёт за курсором вдоль своего направления (на величину операции), а форма результата видна сразу линиями
/// быстрого предпросмотра (<see cref="HandleGhost"/>) — деталь считается, когда ручку отпустили.
/// </summary>
public sealed partial class ModelViewport
{
    private OpHandle? _handle;
    private int _handleSign;
    private Point _hDown;
    private Vector2 _hAxis;
    private double _hWorldPerPx, _hStartValue;
    private int _activeSign;
    private double _activeValue;

    /// <summary>Тянуть начали: +1 — наружу, −1 — внутрь.</summary>
    public event Action<int>? HandleDragStarted;
    /// <summary>Величина вдоль ручки (мм, всегда положительная).</summary>
    public event Action<double>? HandleDragged;
    public event Action? HandleDragEnded;

    /// <summary>Ручку тянут сейчас.</summary>
    public bool HandleDragging => _handleSign != 0;
    /// <summary>Где курсор при перетаскивании ручки (координаты вида).</summary>
    public Point HandleCursor { get; private set; }

    public OpHandle? Handle
    {
        get => _handle;
        set
        {
            _handle = value;
            if (value == null) { _activeSign = 0; _activeValue = 0; _ghost = null; _ghostLines = null; }
            _overlayDirty = true; Redraw();
        }
    }

    /// <summary>Конец ручки этого знака сдвинут на величину операции (операция, начатая ручкой, идёт).</summary>
    public void SetHandleValue(int sign, double value)
    {
        _activeSign = sign; _activeValue = Math.Max(0, value);
        _overlayDirty = true; Redraw();
    }

    // ---------------------------------------------------------------- быстрый предпросмотр

    private Func<double, List<(Vector3d A, Vector3d B)>>? _ghost;
    private List<(Vector3d A, Vector3d B)>? _ghostLines;
    private int _ghostSign;

    /// <summary>Линии предпросмотра по величине ручки; null — убрать.</summary>
    public void SetHandleGhost(Func<double, List<(Vector3d A, Vector3d B)>>? ghost, int sign)
    {
        _ghost = ghost; _ghostSign = sign; _ghostLines = null;
        _overlayDirty = true; Redraw();
    }

    /// <summary>Показать предпросмотр для величины (или скрыть линии, оставив функцию — show=false).</summary>
    public void ShowGhost(double value, bool show = true)
    {
        if (!show || _ghost == null) _ghostLines = null;
        else
        {
            try { _ghostLines = _ghost(value); }
            catch (Exception ex) { Parametric3D.Core.AppLog.Write("handle ghost", ex); _ghostLines = null; }
        }
        _overlayDirty = true; Redraw();
    }

    public bool GhostShown => _ghostLines != null;
    public int GhostSegments => _ghostLines?.Count ?? 0;

    // ---------------------------------------------------------------- геометрия ручки

    private double HandleLen => 70 * PixelSize;

    /// <summary>Конец стрелки знака: длина стрелки плюс величина (если операция этого знака идёт).</summary>
    private Vector3 Tip(OpHandle h, int sign)
    {
        float baseLen = (float)(sign > 0 ? HandleLen : HandleLen * 0.75);
        float extra = _activeSign == sign ? (float)_activeValue : 0;
        return h.At + h.Out * (sign * (baseLen + extra));
    }

    private Point? ToScreen(Vector3 w)
    {
        var c = new Vector4(w, 1) * Camera.ViewMatrix * Camera.ProjMatrix;
        if (c.W <= 1e-9f) return null;
        double s = Scaling;
        return new Point((c.X / c.W * 0.5 + 0.5) * Camera.ViewportWidth / s, (0.5 - c.Y / c.W * 0.5) * Camera.ViewportHeight / s);
    }

    /// <summary>Под курсором конец ручки: +1 наружу, −1 внутрь, 0 — нет.</summary>
    private int HandleHit(Point p)
    {
        if (_handle is not { } h || ActiveSketch != null) return 0;
        int best = 0; double bd = 14;
        void Try(Vector3 w, int sign)
        {
            if (ToScreen(w) is not { } q) return;
            double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
            if (d < bd) { bd = d; best = sign; }
        }
        if (h.Outward) Try(Tip(h, 1), 1);
        if (h.Inward) Try(Tip(h, -1), -1);
        return best;
    }

    private int _hGrab;

    private void BeginHandleDrag(Point p)
    {
        _hDown = p;
        _handleSign = 0;
        _hGrab = HandleHit(p);
        _hAxis = Vector2.Zero;
        if (_handle is not { } h || _hGrab == 0 || ToScreen(h.At) is not { } a || ToScreen(h.At + h.Out * (float)(HandleLen * _hGrab)) is not { } b) return;
        // ось на экране — в сторону захваченной стрелки: движение вдоль неё увеличивает величину
        var axis = new Vector2((float)(b.X - a.X), (float)(b.Y - a.Y));
        float len = axis.Length;
        if (len < 1e-3f) return;
        _hAxis = axis / len;
        _hWorldPerPx = HandleLen / len;
        _hStartValue = _activeSign == _hGrab ? _activeValue : 0;
    }

    private void HandleDragMove(Point p)
    {
        if (_handle is not { } h || _hAxis == Vector2.Zero) return;
        double moved = (p.X - _hDown.X) * _hAxis.X + (p.Y - _hDown.Y) * _hAxis.Y;
        HandleCursor = p;
        if (_handleSign == 0)
        {
            if (Math.Abs(moved) < 4) return;
            if (_hGrab > 0 && !h.Outward || _hGrab < 0 && !h.Inward) return;
            _handleSign = _hGrab;
            HandleDragStarted?.Invoke(_hGrab);
        }
        double value = Math.Max(0.01, _hStartValue + moved * _hWorldPerPx);
        // конец стрелки — за курсором вдоль её направления; линии предпросмотра — для этой величины
        _activeSign = _handleSign; _activeValue = value;
        if (_ghost != null && _ghostSign == _handleSign) ShowGhost(value);
        _overlayDirty = true;
        HandleDragged?.Invoke(value);
        Redraw();
    }

    private void EndHandleDrag()
    {
        if (_handleSign != 0) HandleDragEnded?.Invoke();
        _handleSign = 0;
    }

    /// <summary>Потянуть ручку (сценарии проверок): знак и величина; end=false — не отпускать.</summary>
    public void SimulateHandleDrag(int sign, double value, bool end = true)
    {
        if (_handleSign == 0) { _handleSign = sign; HandleDragStarted?.Invoke(sign); }
        _activeSign = sign; _activeValue = value;
        if (_handle is { } h && ToScreen(Tip(h, sign)) is { } tp) HandleCursor = tp;
        if (_ghost != null && _ghostSign == sign) ShowGhost(value);
        HandleDragged?.Invoke(value);
        if (end) EndHandleDrag();
    }

    private void AddHandleOverlay(List<LineInst> lines, List<PointInst> pts)
    {
        var th = _r.Theme;
        if (_ghostLines != null && ActiveSketch == null)
        {
            var gc = _ghostSign < 0 ? th.HandleAlt : th.Handle;
            foreach (var (a, b) in _ghostLines)
                lines.Add(Inst(new Vector3((float)a.X, (float)a.Y, (float)a.Z), new Vector3((float)b.X, (float)b.Y, (float)b.Z), gc, 1.8f, 0));
        }
        if (_handle is not { } h || ActiveSketch != null) return;
        if (h.Outward)
        {
            var tip = Tip(h, 1);
            lines.Add(Inst(h.At, tip, th.Handle, 2.2f, 0));
            pts.Add(new PointInst { P = tip, Color = SceneData.Pack(th.Handle), Px = 12, Shape = 0 });
        }
        if (h.Inward)
        {
            var tip = Tip(h, -1);
            lines.Add(Inst(h.At, tip, th.HandleAlt, 2.2f, 5));
            pts.Add(new PointInst { P = tip, Color = SceneData.Pack(th.HandleAlt), Px = 11, Shape = 1 });
        }
        pts.Add(new PointInst { P = h.At, Color = SceneData.Pack(th.Handle), Px = 6, Shape = 2 });
    }
}
