using Avalonia;
using OpenTK.Mathematics;
using Parametric3D.App.Sketching;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Viewport;

/// <summary>
/// РЕЖИМ ЭСКИЗА ВИДА: камера нормально к плоскости (ортогонально), сетка на плоскости, инструмент построения с
/// резиновой нитью поверх модели, привязка к точкам эскиза (в пределах SnapTolerancePx) и к сетке. Точки эскиза в
/// правке выбираются (SceneBuilder). Выход — с возвратом прежней камеры.
/// </summary>
public sealed partial class ModelViewport
{
    public Sketch? ActiveSketch { get; private set; }
    private CameraState _beforeSketch;
    private ISketchTool? _tool;
    private bool _overlayDirty;
    private double _gridKey;
    private SkPoint? _snap;
    private Vector2d _cursor2;

    /// <summary>Эскиз изменён инструментом (дерево, состояние, решатель).</summary>
    public event Action? SketchEdited;
    /// <summary>Сменился инструмент или его шаг (подсказка у вида).</summary>
    public event Action? ToolChanged;

    /// <summary>Привязка к сетке (кнопка панели эскиза).</summary>
    public bool SnapGrid { get; set; } = true;

    public ISketchTool? Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            if (value != null && ActiveSketch == null) _tool = null;
            Selection.Filter = _tool != null ? PickFilter.SketchPoints : ActiveSketch != null ? PickFilter.Sketch : PickFilter.All;
            _overlayDirty = true;
            ToolChanged?.Invoke();
            Redraw();
        }
    }

    public void EnterSketch(Sketch sk, bool animate = true)
    {
        if (ActiveSketch == null) _beforeSketch = Camera.State;
        ActiveSketch = sk;
        Selection.Clear();
        Selection.Filter = PickFilter.Sketch;
        var to = Camera.State;
        var pl = sk.Plane;
        // «верх» вида — ось плоскости, ближайшая к нынешнему верху (вид не переворачивается)
        var camUp = Camera.Up;
        var up = new[] { V3(pl.V), -V3(pl.V), V3(pl.U), -V3(pl.U) }.OrderByDescending(a => Vector3.Dot(a, camUp)).First();
        to.Rotation = ViewMath.FromForwardUp(-V3(pl.W), up);
        to.Target = V3(pl.Origin);
        to.Mode = ProjectionMode.Orthographic;
        if (animate) AnimateTo(to); else { Camera.State = to; CameraChanged?.Invoke(); }
        _overlayDirty = true;
        MarkDirty();
    }

    public void ExitSketch(bool restoreCamera = true)
    {
        if (ActiveSketch == null) return;
        _tool = null;
        ActiveSketch = null;
        Selection.Filter = PickFilter.All;
        Selection.Clear();
        if (restoreCamera) AnimateTo(_beforeSketch);
        _overlayDirty = true;
        ToolChanged?.Invoke();
        MarkDirty();
    }

    private static Vector3 V3(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>Размер логического пикселя на плоскости (мм).</summary>
    public double PixelSize => 1.0 / Math.Max(1e-6, Camera.WorldToPx) * Scaling;

    /// <summary>Точка экрана на плоскости эскиза (без привязок); null — луч параллелен плоскости.</summary>
    public Vector2d? ScreenToSketch(Point p)
    {
        if (ActiveSketch is not { } sk) return null;
        double s = Scaling;
        Camera.ScreenRay((int)(p.X * s), (int)(p.Y * s), out var o, out var d);
        var pl = sk.Plane;
        var n = pl.W;
        double den = Vector3d.Dot(new Vector3d(d.X, d.Y, d.Z), n);
        if (Math.Abs(den) < 1e-9) return null;
        double t = Vector3d.Dot(pl.Origin - new Vector3d(o.X, o.Y, o.Z), n) / den;
        var w = new Vector3d(o.X, o.Y, o.Z) + new Vector3d(d.X, d.Y, d.Z) * t;
        return pl.ToLocal2(w);
    }

    /// <summary>Точка с привязкой: ближняя точка эскиза в пределах допуска, иначе узел сетки.</summary>
    private SketchInput? Capture(Point p)
    {
        if (ActiveSketch is not { } sk || ScreenToSketch(p) is not { } q) return null;
        double tol = PixelSize * Settings.Current.SnapTolerancePx;
        SkPoint? best = null; double bd = tol;
        foreach (var pt in sk.AllPoints())
        {
            double d = (pt.P - q).Length;
            if (d < bd) { bd = d; best = pt; }
        }
        if (best != null) return new SketchInput(sk, best.P, best);
        if (SnapGrid)
        {
            double step = GridStep();
            q = new Vector2d(Math.Round(q.X / step) * step, Math.Round(q.Y / step) * step);
        }
        return new SketchInput(sk, q, null);
    }

    private void ToolMove(Point p)
    {
        if (_tool == null || Capture(p) is not { } i) return;
        _tool.Move(i);
        _snap = i.Snap;
        _cursor2 = i.P;
        _overlayDirty = true;
        Redraw();
    }

    private void ToolClick(Point p)
    {
        if (_tool == null || Capture(p) is not { } i) return;
        bool changed = _tool.Click(i);
        AfterTool(changed);
    }

    /// <summary>Enter, правая кнопка, двойной щелчок: закончить начатое; повторно (ничего не начато) — снять инструмент.</summary>
    public void ToolFinish()
    {
        if (_tool == null || ActiveSketch == null) return;
        bool changed = _tool.Finish(ActiveSketch);
        AfterTool(changed);
    }

    private void AfterTool(bool changed)
    {
        if (ActiveSketch is { } sk && changed)
        {
            try { sk.Solve(_doc?.Solver ?? new NumericSketchSolver()); }
            catch (Exception ex) { AppLog.Write("sketch solve", ex); }
            sk.NotifyChanged(sk);
            MarkDirty();
            SketchEdited?.Invoke();
        }
        if (_tool is { Finished: true } t) _tool = t.Continuous ? t.Restart() : null;
        if (_tool == null) Selection.Filter = ActiveSketch != null ? PickFilter.Sketch : PickFilter.All;
        _overlayDirty = true;
        ToolChanged?.Invoke();
        Redraw();
    }

    /// <summary>Щелчок инструмента в точке вида (доли размера) — для сценариев проверок.</summary>
    public void SimulateToolClick(double fx, double fy)
    {
        var p = new Point(Bounds.Width * fx, Bounds.Height * fy);
        ToolMove(p);
        ToolClick(p);
    }

    /// <summary>Шаг сетки: «круглое» число, чтобы клетка была 25–60 пикселей.</summary>
    public double GridStep()
    {
        if (ActiveSketch is { SnapToGrid: true } sk && sk.GridStep > 0) return sk.GridStep;
        double raw = PixelSize * 30;
        double e = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double m in new[] { 1, 2, 5, 10 }) if (m * e >= raw) return m * e;
        return 10 * e;
    }

    private bool GridStale() => ActiveSketch != null && Math.Abs(GridKeyNow() - _gridKey) > 1e-9;
    private double GridKeyNow() => Math.Round(Camera.WorldToPx, 4) + Camera.State.Target.X * 1e-3 + Camera.State.Target.Y * 1e-5;

    private (List<LineInst> Grid, List<LineInst> Lines, List<PointInst> Points) BuildOverlay()
    {
        var grid = new List<LineInst>();
        var lines = new List<LineInst>();
        var pts = new List<PointInst>();
        if (ActiveSketch is not { } sk) return (grid, lines, pts);
        _gridKey = GridKeyNow();
        var th = _r.Theme;
        var pl = sk.Plane;
        Vector3 W(Vector2d p) => V3(pl.ToWorld(p));

        // сетка: вокруг проекции центра вида на плоскость, на размер вида
        double step = GridStep();
        var c3 = Camera.State.Target;
        var c = pl.ToLocal2(new Vector3d(c3.X, c3.Y, c3.Z));
        double half = Math.Max(Bounds.Width, Bounds.Height) * PixelSize * 0.75;
        int n = (int)Math.Min(200, Math.Ceiling(half / step));
        double x0 = Math.Round(c.X / step) * step, y0 = Math.Round(c.Y / step) * step;
        var minor = th.Dark ? new Vector4(1, 1, 1, 0.06f) : new Vector4(0, 0, 0, 0.07f);
        var major = th.Dark ? new Vector4(1, 1, 1, 0.13f) : new Vector4(0, 0, 0, 0.14f);
        for (int i = -n; i <= n; i++)
        {
            double x = x0 + i * step, y = y0 + i * step;
            bool mx = Math.Abs(Math.Round(x / step) % 5) < 0.5, my = Math.Abs(Math.Round(y / step) % 5) < 0.5;
            grid.Add(Inst(W(new Vector2d(x, y0 - n * step)), W(new Vector2d(x, y0 + n * step)), mx ? major : minor, 1f, 0));
            grid.Add(Inst(W(new Vector2d(x0 - n * step, y)), W(new Vector2d(x0 + n * step, y)), my ? major : minor, 1f, 0));
        }
        // оси эскиза
        grid.Add(Inst(W(new Vector2d(x0 - n * step, 0)), W(new Vector2d(x0 + n * step, 0)), new Vector4(0.85f, 0.35f, 0.35f, 0.55f), 1.2f, 0));
        grid.Add(Inst(W(new Vector2d(0, y0 - n * step)), W(new Vector2d(0, y0 + n * step)), new Vector4(0.35f, 0.75f, 0.4f, 0.55f), 1.2f, 0));

        // резиновая нить инструмента
        if (_tool != null)
        {
            var pl2 = new List<Vector2d[]>();
            var pp = new List<Vector2d>();
            _tool.Preview(pl2, pp);
            var col = th.SketchCurve; col.W = 0.9f;
            foreach (var poly in pl2)
                for (int i = 1; i < poly.Length; i++) lines.Add(Inst(W(poly[i - 1]), W(poly[i]), col, th.SketchWidth, 6));
            foreach (var p in pp) pts.Add(new PointInst { P = W(p), Color = SceneData.Pack(th.SketchPoint), Px = 6, Shape = 0 });
            if (_snap != null) pts.Add(new PointInst { P = W(_snap.P), Color = SceneData.Pack(th.Select), Px = 11, Shape = 1 });
        }
        return (grid, lines, pts);
    }

    private static LineInst Inst(Vector3 a, Vector3 b, Vector4 c, float w, float dash) =>
        new() { A = a, B = b, Color = SceneData.Pack(c), Width = w, Dash = dash, Pick = 0 };
}
