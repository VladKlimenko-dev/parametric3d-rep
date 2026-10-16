using Avalonia;
using OpenTK.Mathematics;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Viewport;

/// <summary>Ручка операции у выбранного (п. 6): точка, направление «наружу», можно ли тянуть внутрь.</summary>
public sealed record OpHandle(Vector3 At, Vector3 Out, bool Outward, bool Inward);

/// <summary>
/// РУЧКИ ОПЕРАЦИЙ (как в Plasticity): у выбранного ребра, вершины или грани — стрелка наружу и стрелка внутрь. Тянут за
/// конец стрелки: направление выбирает операцию (окно сообщает знак), расстояние вдоль стрелки — её величину.
/// </summary>
public sealed partial class ModelViewport
{
    private OpHandle? _handle;
    private int _handleSign, _handleHover;
    private Point _hDown;
    private Vector2 _hAxis;
    private double _hWorldPerPx;

    /// <summary>Тянуть начали: +1 — наружу, −1 — внутрь.</summary>
    public event Action<int>? HandleDragStarted;
    /// <summary>Величина вдоль ручки (мм, всегда положительная).</summary>
    public event Action<double>? HandleDragged;
    public event Action? HandleDragEnded;

    public OpHandle? Handle
    {
        get => _handle;
        set { _handle = value; _overlayDirty = true; Redraw(); }
    }

    private double HandleLen => 70 * PixelSize;

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
        float L = (float)HandleLen;
        int best = 0; double bd = 14;
        void Try(Vector3 w, int sign)
        {
            if (ToScreen(w) is not { } q) return;
            double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
            if (d < bd) { bd = d; best = sign; }
        }
        if (h.Outward) Try(h.At + h.Out * L, 1);
        if (h.Inward) Try(h.At - h.Out * (L * 0.75f), -1);
        return best;
    }

    private void BeginHandleDrag(Point p)
    {
        _hDown = p;
        _handleSign = 0;
        if (_handle is not { } h || ToScreen(h.At) is not { } a || ToScreen(h.At + h.Out * (float)HandleLen) is not { } b) return;
        var axis = new Vector2((float)(b.X - a.X), (float)(b.Y - a.Y));
        float len = axis.Length;
        if (len < 1e-3f) { _hAxis = Vector2.Zero; return; }
        _hAxis = axis / len;
        _hWorldPerPx = HandleLen / len;
        // начало — от конца ручки, за который взяли: величина отсчитывается от нуля
        _hStartAlong = HandleHit(p) > 0 ? len : -len * 0.75;
    }

    private double _hStartAlong;

    private void HandleDragMove(Point p)
    {
        if (_handle is not { } h || _hAxis == Vector2.Zero) return;
        double along = (p.X - _hDown.X) * _hAxis.X + (p.Y - _hDown.Y) * _hAxis.Y + _hStartAlong;
        // от середины ручки: длина стрелки — запас, величина — сколько протянули за её конец
        double moved = along - _hStartAlong;
        if (_handleSign == 0)
        {
            if (Math.Abs(moved) < 4) return;
            int sign = _hStartAlong > 0 ? 1 : -1;
            if (sign > 0 && !h.Outward || sign < 0 && !h.Inward) return;
            _handleSign = sign;
            HandleDragStarted?.Invoke(sign);
        }
        double value = Math.Abs(moved) * _hWorldPerPx;
        if (Math.Sign(moved) != _handleSign) value = 0.01;
        HandleDragged?.Invoke(value);
    }

    private void EndHandleDrag()
    {
        if (_handleSign != 0) HandleDragEnded?.Invoke();
        _handleSign = 0;
    }

    /// <summary>Потянуть ручку (сценарии проверок): знак и величина.</summary>
    public void SimulateHandleDrag(int sign, double value)
    {
        HandleDragStarted?.Invoke(sign);
        HandleDragged?.Invoke(value);
        HandleDragEnded?.Invoke();
    }

    private void AddHandleOverlay(List<LineInst> lines, List<PointInst> pts)
    {
        if (_handle is not { } h || ActiveSketch != null) return;
        var th = _r.Theme;
        float L = (float)HandleLen;
        if (h.Outward)
        {
            lines.Add(Inst(h.At, h.At + h.Out * L, th.Handle, 2.2f, 0));
            pts.Add(new PointInst { P = h.At + h.Out * L, Color = SceneData.Pack(th.Handle), Px = 12, Shape = 0 });
        }
        if (h.Inward)
        {
            lines.Add(Inst(h.At, h.At - h.Out * (L * 0.75f), th.HandleAlt, 2.2f, 5));
            pts.Add(new PointInst { P = h.At - h.Out * (L * 0.75f), Color = SceneData.Pack(th.HandleAlt), Px = 11, Shape = 1 });
        }
        pts.Add(new PointInst { P = h.At, Color = SceneData.Pack(th.Handle), Px = 6, Shape = 2 });
    }
}
