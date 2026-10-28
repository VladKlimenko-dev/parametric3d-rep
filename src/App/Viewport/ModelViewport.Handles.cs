using Avalonia;
using OpenTK.Mathematics;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Viewport;

/// <summary>Ручка операции у выбранного (п. 6): точка, направление «наружу», можно ли тянуть внутрь.</summary>
public sealed record OpHandle(Vector3 At, Vector3 Out, bool Outward, bool Inward);

/// <summary>
/// РУЧКА ОПЕРАЦИИ (как в Plasticity): одна у выбранного ребра, вершины или грани — шарик на короткой оси вдоль
/// направления «наружу». Тянут шарик от его начального места: в одну сторону — одна операция, в другую — другая
/// (ребро: внутрь — скругление, наружу — фаска; грань: наружу — выдавливание, внутрь — вырез). Шарик идёт за курсором
/// вдоль оси; величина операции — его сдвиг от начального места (мм), знак — сторона.
/// </summary>
public sealed partial class ModelViewport
{
    private OpHandle? _handle;
    private bool _hDragging, _hStarted;
    private Point _hDown;
    private Vector2 _hAxis;
    private double _hWorldPerPx, _hStartValue;
    /// <summary>Сдвиг шарика от начального места, мм (знак — сторона).</summary>
    private double _hValue;

    /// <summary>Тянуть начали (сдвиг больше 4 пикселей).</summary>
    public event Action? HandleDragStarted;
    /// <summary>Знак стороны (+1 наружу, −1 внутрь) и величина (мм, положительная).</summary>
    public event Action<int, double>? HandleDragged;
    public event Action? HandleDragEnded;

    /// <summary>Ручку тянут сейчас.</summary>
    public bool HandleDragging => _hDragging && _hStarted;
    /// <summary>Где курсор при перетаскивании ручки (координаты вида).</summary>
    public Point HandleCursor { get; private set; }
    /// <summary>Текущий сдвиг ручки со знаком, мм.</summary>
    public double HandleValue => _hValue;

    public OpHandle? Handle
    {
        get => _handle;
        set
        {
            _handle = value;
            if (value == null) { _hValue = 0; _ghost = null; _ghostLines = null; if (!Parametric3D.App.Views.MainWindow.OpReleaseOff) _ghostValid = null; }   // k97: проверка держала прежнюю деталь (+250 МБ)
            _overlayDirty = true; Redraw();
        }
    }

    /// <summary>Сдвиг ручки со знаком (операция, начатая ручкой, идёт; 0 — ручка на месте).</summary>
    public void SetHandleValue(double signedValue)
    {
        _hValue = signedValue;
        _overlayDirty = true; Redraw();
    }

    // ---------------------------------------------------------------- быстрый предпросмотр

    private Func<double, List<(Vector3d A, Vector3d B)>>? _ghost;
    private List<(Vector3d A, Vector3d B)>? _ghostLines;
    private int _ghostSign;

    private Func<double, bool>? _ghostValid;
    private bool _ghostOk = true;

    /// <summary>Контур предпросмотра по сдвигу ручки со знаком; null — убрать. valid — выйдет ли операция (иначе контур красный).</summary>
    public void SetHandleGhost(Func<double, List<(Vector3d A, Vector3d B)>>? ghost, Func<double, bool>? valid = null)
    {
        _ghost = ghost; _ghostLines = null; _ghostValid = valid; _ghostOk = true;
        _overlayDirty = true; Redraw();
    }

    /// <summary>Показать контур для сдвига со знаком (или скрыть — show=false).</summary>
    public void ShowGhost(double signedValue, bool show = true)
    {
        if (!show || _ghost == null || Math.Abs(signedValue) < 1e-9) _ghostLines = null;
        else
        {
            try
            {
                _ghostLines = _ghost(signedValue); _ghostSign = Math.Sign(signedValue);
                _ghostOk = _ghostValid?.Invoke(signedValue) ?? true;
            }
            catch (Exception ex) { Parametric3D.Core.AppLog.Write("handle ghost", ex); _ghostLines = null; }
        }
        _overlayDirty = true; Redraw();
    }

    public bool GhostShown => _ghostLines != null;
    /// <summary>Контур показывает, что операция не выйдет (красный).</summary>
    public bool GhostBad => _ghostLines != null && !_ghostOk;
    public int GhostSegments => _ghostLines?.Count ?? 0;

    // ---------------------------------------------------------------- геометрия ручки

    private double HandleLen => Services.UiLook.HandleLengthPx * PixelSize;

    /// <summary>Начальное место шарика — на оси наружу от элемента.</summary>
    private Vector3 Rest(OpHandle h) => h.At + h.Out * (float)HandleLen;
    private Vector3 Knob(OpHandle h) => Rest(h) + h.Out * (float)_hValue;

    private Point? ToScreen(Vector3 w)
    {
        var c = new Vector4(w, 1) * Camera.ViewMatrix * Camera.ProjMatrix;
        if (c.W <= 1e-9f) return null;
        double s = Scaling;
        return new Point((c.X / c.W * 0.5 + 0.5) * Camera.ViewportWidth / s, (0.5 - c.Y / c.W * 0.5) * Camera.ViewportHeight / s);
    }

    /// <summary>Под курсором шарик ручки.</summary>
    private int HandleHit(Point p)
    {
        if (_handle is not { } h || ActiveSketch != null || ToScreen(Knob(h)) is not { } q) return 0;
        return Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y)) < Math.Max(14, Services.UiLook.HandleKnobPx) ? 1 : 0;
    }

    private void BeginHandleDrag(Point p)
    {
        _hDown = p;
        _hDragging = true; _hStarted = false;
        _hAxis = Vector2.Zero;
        if (_handle is not { } h || ToScreen(h.At) is not { } a || ToScreen(h.At + h.Out * (float)HandleLen) is not { } b) return;
        // ось на экране — «наружу»: движение вдоль неё увеличивает сдвиг со знаком
        var axis = new Vector2((float)(b.X - a.X), (float)(b.Y - a.Y));
        float len = axis.Length;
        if (len < 1e-3f) return;
        _hAxis = axis / len;
        _hWorldPerPx = HandleLen / len;
        _hStartValue = _hValue;
    }

    private void HandleDragMove(Point p)
    {
        if (_handle is not { } h || _hAxis == Vector2.Zero) return;
        double moved = (p.X - _hDown.X) * _hAxis.X + (p.Y - _hDown.Y) * _hAxis.Y;
        HandleCursor = p;
        if (!_hStarted)
        {
            if (Math.Abs(moved) < 4) return;
            _hStarted = true;
            HandleDragStarted?.Invoke();
        }
        double v = _hStartValue + moved * _hWorldPerPx;
        // сторона, на которую нельзя (у поверхности — только внутрь), — шарик стоит на месте
        if (v > 0 && !h.Outward || v < 0 && !h.Inward) v = 0;
        _hValue = v;
        ShowGhost(v);
        if (Math.Abs(v) > 1e-9) HandleDragged?.Invoke(Math.Sign(v), Math.Abs(v));
        Redraw();
    }

    private void EndHandleDrag()
    {
        bool was = _hStarted;
        _hDragging = false; _hStarted = false;
        if (was) HandleDragEnded?.Invoke();
    }

    /// <summary>Потянуть ручку (сценарии проверок): сдвиг со знаком; end=false — не отпускать.</summary>
    public void SimulateHandleDrag(double signedValue, bool end = true)
    {
        if (!_hStarted) { _hDragging = true; _hStarted = true; HandleDragStarted?.Invoke(); }
        _hValue = signedValue;
        if (_handle is { } h && ToScreen(Knob(h)) is { } tp) HandleCursor = tp;
        ShowGhost(signedValue);
        if (Math.Abs(signedValue) > 1e-9) HandleDragged?.Invoke(Math.Sign(signedValue), Math.Abs(signedValue));
        if (end) EndHandleDrag();
    }

    /// <summary>
    /// u10: НАЧАЛО КООРДИНАТ — ТРИ СТРЕЛКИ X/Y/Z (красная, зелёная, синяя) по осям детали, 55 пикселей при любом масштабе;
    /// наконечник — две чёрточки в плоскости, обращённой к взгляду. Выбор — по точке в начале (сцена).
    /// </summary>
    private void AddOriginOverlay(List<LineInst> lines, List<PointInst> pts)
    {
        if (_scene == null || ActiveSketch != null || !Show.Origin) return;
        var th = _r.Theme;
        float L = (float)(55 * PixelSize);
        var view = Camera.Forward;
        var vf = new Vector3((float)view.X, (float)view.Y, (float)view.Z);
        foreach (var (p, x, y, z, _) in _scene.Origins)
        {
            foreach (var (ax, col) in new[] { (x, th.AxisX), (y, th.AxisY), (z, th.AxisZ) })
            {
                var tip = p + ax * L;
                lines.Add(Inst(p, tip, col, 2.4f, 0));
                var side = Vector3.Cross(ax, vf);
                if (side.LengthSquared < 1e-8f) side = Vector3.Cross(ax, Vector3.UnitY);
                if (side.LengthSquared < 1e-8f) side = Vector3.Cross(ax, Vector3.UnitX);
                side = side.Normalized();
                float h = L * 0.22f;
                lines.Add(Inst(tip, tip - ax * h + side * (h * 0.45f), col, 2.4f, 0));
                lines.Add(Inst(tip, tip - ax * h - side * (h * 0.45f), col, 2.4f, 0));
            }
            pts.Add(new PointInst { P = p, Color = SceneData.Pack(new Vector4(0.92f, 0.93f, 0.95f, 1)), Px = 5, Shape = 0 });
        }
    }

    /// <summary>k115: контуры поверх вида — набор отрезков, цвет, пунктир (0 — сплошной).</summary>
    public sealed record Contour(List<(Vector3d A, Vector3d B)> Lines, Vector4 Color, float Dash = 0);
    private readonly Dictionary<string, List<Contour>> _contours = new();

    /// <summary>
    /// k115 (замечание 12:39, пп. 4–5): контуры канала (hover — что операция добавила/убрала при наведении в дереве; pinned —
    /// скрытый элемент, выбранный в дереве). null — убрать.
    /// </summary>
    public void SetContours(string channel, IEnumerable<Contour>? cs)
    {
        if (cs == null) { if (!_contours.Remove(channel)) return; }
        else _contours[channel] = cs.ToList();
        _overlayDirty = true; Redraw();
    }
    public int ContourSegments(string channel) => _contours.TryGetValue(channel, out var l) ? l.Sum(c => c.Lines.Count) : 0;
    public Vector4? ContourColor(string channel) => _contours.TryGetValue(channel, out var l) && l.Count > 0 ? l[0].Color : null;

    private void AddHandleOverlay(List<LineInst> lines, List<PointInst> pts)
    {
        var th = _r.Theme;
        foreach (var set in _contours.Values)
            foreach (var c in set)
                foreach (var (a, b) in c.Lines)
                    lines.Add(Inst(new Vector3((float)a.X, (float)a.Y, (float)a.Z), new Vector3((float)b.X, (float)b.Y, (float)b.Z), c.Color, 2.2f, c.Dash));
        if (_ghostLines != null && ActiveSketch == null)
        {
            var gc = !_ghostOk ? th.HandleBad : _ghostSign < 0 ? th.HandleAlt : th.Handle;
            foreach (var (a, b) in _ghostLines)
                lines.Add(Inst(new Vector3((float)a.X, (float)a.Y, (float)a.Z), new Vector3((float)b.X, (float)b.Y, (float)b.Z), gc, 2f, 0));
        }
        if (_handle is not { } h || ActiveSketch != null) return;
        // ось: от элемента к начальному месту и дальше в обе стороны (пунктир), шарик — на текущем сдвиге
        var rest = Rest(h);
        var knob = Knob(h);
        float L = (float)HandleLen;
        float hw = (float)Services.UiLook.HandleLineWidth;
        lines.Add(Inst(h.At, rest, th.Handle, hw, 0));
        if (h.Outward) lines.Add(Inst(rest, rest + h.Out * (L * 0.5f), th.Handle, hw * 0.75f, 5));
        if (h.Inward) lines.Add(Inst(rest, rest - h.Out * (L * 0.5f), th.HandleAlt, hw * 0.75f, 5));
        if (Math.Abs(_hValue) > 1e-9) lines.Add(Inst(rest, knob, _hValue < 0 ? th.HandleAlt : th.Handle, hw * 1.5f, 0));
        var kc = _hValue < 0 ? th.HandleAlt : th.Handle;
        pts.Add(new PointInst { P = rest, Color = SceneData.Pack(th.Handle), Px = 5, Shape = 2 });
        pts.Add(new PointInst { P = knob, Color = SceneData.Pack(kc), Px = (float)Services.UiLook.HandleKnobPx, Shape = 0 });
        pts.Add(new PointInst { P = h.At, Color = SceneData.Pack(th.Handle), Px = 6, Shape = 2 });
    }
}
