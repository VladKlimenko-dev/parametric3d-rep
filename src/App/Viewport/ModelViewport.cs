using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;
using Parametric3D.Render.Gl;

namespace Parametric3D.App.Viewport;

/// <summary>
/// 3D-ВИД МОДЕЛИ (этап U1): контекст OpenGL от Avalonia, рендер прежнего приложения (RenderScene, фон, сетка),
/// камера, наведение и выбор, плавные переходы вида.
///
/// Мышь (ТЗ_UI, 0.6): средняя — вращать, Shift+средняя или правая с перетаскиванием — сдвиг, колесо — масштаб к курсору;
/// левая — выбрать, Shift/Ctrl — добавить / исключить. Короткий правый щелчок — общие команды (событие ContextRequested).
///
/// Вид отрисовывает кадр в свой кадровый буфер с MSAA и переносит его в буфер Avalonia (номер fb в OnOpenGlRender).
/// Всё, что меняет буферы GPU (сборка сцены, подсветка), делается только в OnOpenGlRender — там контекст текущий.
/// </summary>
public sealed class ModelViewport : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>Вид принимает мышь всей площадью (и до первого кадра, и без OpenGL).</summary>
    public bool HitTest(Point p) => new Rect(Bounds.Size).Contains(p);

    public readonly Camera Camera = new();
    public readonly Selection Selection = new();
    public readonly ViewTransition Transition = new() { Steps = 14 };

    private readonly RenderStyles _styles = new();
    private readonly RenderContext _ctx = new();
    private readonly BackgroundRenderer _background = new();
    private readonly SceneFramebuffer _fbo = new();
    private readonly RenderScene _scene = new();
    private readonly ScenePicker _picker = new();

    private Document? _doc;
    private bool _glReady, _sceneDirty = true, _highlightDirty = true, _rebuilding;
    private DispatcherTimer? _anim;

    /// <summary>Сведения о контексте («производитель | устройство | версия») — для строки состояния и журнала.</summary>
    public string GlInfo { get; private set; } = "";
    public string? GlError { get; private set; }
    public event Action<string>? GlReady;

    /// <summary>Наведение изменилось (подпись — для строки состояния).</summary>
    public event Action<string>? HoverChanged;
    /// <summary>Камера изменилась (куб вида следует за ней).</summary>
    public event Action? CameraChanged;
    /// <summary>Короткий правый щелчок вне команды — общие команды (UI-11).</summary>
    public event Action<Point>? ContextRequested;

    public ModelViewport()
    {
        Camera.State.Rotation = HomeRotation;
        Focusable = true;
        ClipToBounds = true;
        Selection.Changed += () => { _highlightDirty = true; Redraw(); };
        Transition.Completed += (_, _) => CameraChanged?.Invoke();
    }

    // ================================================================ документ

    public Document? Document
    {
        get => _doc;
        set
        {
            if (ReferenceEquals(_doc, value)) return;
            if (_doc != null) _doc.Invalidated -= OnDocInvalidated;
            _doc = value;
            if (_doc != null) { _doc.Invalidated += OnDocInvalidated; _paletteDirty = true; }
            Selection.Clear();
            Selection.SetHover(PickRef.Empty);
            _sceneDirty = true;
            Redraw();
        }
    }

    private void OnDocInvalidated() { _sceneDirty = true; Redraw(); }

    /// <summary>Активная деталь (уровень детали): её элементы выбираются, чужие — с пониженным приоритетом.</summary>
    public Part? ActivePart { get; set; }

    public BodyViewMode BodyMode
    {
        get => _scene.BodyMode;
        set { if (_scene.BodyMode == value) return; _scene.BodyMode = value; _sceneDirty = true; Redraw(); }
    }

    public bool ShowDatumPlanes
    {
        get => _scene.ShowDatumPlanes;
        set { _scene.ShowDatumPlanes = value; _sceneDirty = true; Redraw(); }
    }

    public void MarkDirty() { _sceneDirty = true; Redraw(); }

    private bool _dark = true;
    /// <summary>Тема вида (UI_VIEW_PALETTE): цвета пишутся в настройки активного документа.</summary>
    public bool DarkTheme
    {
        get => _dark;
        set { _dark = value; ApplyPalette(); }
    }

    private bool _paletteDirty = true;
    private void ApplyPalette() { _paletteDirty = true; _sceneDirty = true; Redraw(); }

    /// <summary>Исходный вид: изометрия спереди-справа-сверху, вся модель в окне.</summary>
    public void GoHome()
    {
        var cam = new Camera { State = Camera.State, ViewportWidth = Camera.ViewportWidth, ViewportHeight = Camera.ViewportHeight };
        cam.State.Rotation = HomeRotation;
        FitInto(cam);
        AnimateTo(cam.State);
    }

    public static Quaternion HomeRotation => ViewMath.LookFrom(new Vector3(1, 1, 1), Vector3.UnitY);

    /// <summary>Перерисовать (кадр будет нарисован в ближайшем проходе композиции).</summary>
    public void Redraw() => RequestNextFrameRendering();

    // ================================================================ OpenGL

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            bool es = GlVersion.Type == GlProfileType.OpenGLES;
            GL.Load(gl.GetProcAddress, es);
            GlInfo = $"{GL.GetString(StringName.Vendor)} | {GL.GetString(StringName.Renderer)} | {(es ? "OpenGL ES" : "OpenGL")} {GL.GetString(StringName.Version)}";
            AppLog.Write("GL: " + GlInfo);
            _background.Top = _styles.BackgroundTop;
            _background.Bottom = _styles.BackgroundBottom;
            _glReady = true;
            _sceneDirty = true;
            GlReady?.Invoke(GlInfo);
        }
        catch (Exception ex)
        {
            GlError = ex.Message;
            AppLog.Write("GL init", ex);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try
        {
            _scene.Dispose();
            _fbo.Dispose();
            _background.Dispose();
        }
        catch (Exception ex) { AppLog.Write("GL deinit", ex); }
        _glReady = false;
    }

    protected override void OnOpenGlLost()
    {
        _glReady = false;
        _sceneDirty = true;
        base.OnOpenGlLost();
    }

    protected override void OnSizeChanged(Avalonia.Controls.SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // размер окна камеры — сразу (выбор под курсором до первого кадра)
        double s = Scaling;
        Camera.ViewportWidth = Math.Max(1, (int)Math.Round(e.NewSize.Width * s));
        Camera.ViewportHeight = Math.Max(1, (int)Math.Round(e.NewSize.Height * s));
    }

    /// <summary>Масштаб экрана (физических пикселей на логический).</summary>
    public double Scaling => Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>Снимок следующего кадра (RGBA снизу вверх, ширина, высота) — для проверок.</summary>
    public Action<byte[], int, int>? CaptureNextFrame;

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_glReady) return;
        try
        {
            double s = Scaling;
            int w = Math.Max(1, (int)Math.Round(Bounds.Width * s));
            int h = Math.Max(1, (int)Math.Round(Bounds.Height * s));
            Camera.ViewportWidth = w;
            Camera.ViewportHeight = h;
            _fbo.Resize(w, h);

            // анимация перехода
            if (Transition.IsRunning)
            {
                var st = Camera.State;
                if (Transition.Tick(ref st)) { Camera.State = st; CameraChanged?.Invoke(); }
                if (Transition.IsRunning) RequestNextFrameRendering();
            }

            PrepareScene();

            _fbo.Bind();
            _fbo.Clear(new Vector4(0, 0, 0, 0));
            _background.Draw();

            _ctx.Styles = _styles;
            _ctx.View = Camera.ViewMatrix;
            _ctx.Proj = Camera.ProjMatrix;
            _ctx.Model = Matrix4.Identity;
            _ctx.CamPos = Camera.Position;
            _ctx.Viewport = new Vector2(w, h);
            _ctx.WorldToPx = Camera.WorldToPx;
            _ctx.LineWidthScale = (float)s;
            _ctx.Opacity = 1f;

            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);

            if (_doc != null) _scene.Draw(_ctx);

            _fbo.ResolveTo(fb, w, h);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.DepthMask(true);

            if (CaptureNextFrame is { } cap)
            {
                CaptureNextFrame = null;
                var px = new byte[w * h * 4];
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fb);
                GL.ReadPixelsRgba(0, 0, w, h, px);
                cap(px, w, h);
            }

            for (int i = 0; i < 4; i++)
            {
                var e = GL.GetError();
                if (e == ErrorCode.NoError) break;
                AppLog.Write($"GL error 0x{(int)e:X4}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("frame", ex);
        }
    }

    private void PrepareScene()
    {
        if (_doc == null) return;

        // ПЕРЕСБОРКА — В ФОНЕ: кадр рисует прежние тела, по готовности сцена перестраивается.
        if (!_doc.IsBuilding && !_rebuilding && _doc.AllParts().Any(p => p.IsDirty))
            _ = RebuildInBackground();

        ModelItem? owner = ActivePart ?? (ModelItem)_doc.Root;
        if (!ReferenceEquals(owner, _scene.ActiveOwner)) { _scene.ActiveOwner = owner; _sceneDirty = true; }

        if (_paletteDirty)
        {
            ViewPalette.Apply(Settings.Current, _dark);
            _styles.Reload();
            _background.Top = _styles.BackgroundTop;
            _background.Bottom = _styles.BackgroundBottom;
            _paletteDirty = false;
            _sceneDirty = true;
        }
        if (_sceneDirty)
        {
            _scene.Sync(_doc);
            _scene.ResetAllFlags();
            _sceneDirty = false;
            _highlightDirty = true;
        }
        if (_highlightDirty) { ApplyHighlight(); _highlightDirty = false; }
    }

    private async Task RebuildInBackground()
    {
        _rebuilding = true;
        try
        {
            if (_doc != null && await _doc.RebuildDirtyAsync()) _sceneDirty = true;
        }
        catch (Exception ex) { AppLog.Write("background rebuild", ex); }
        finally { _rebuilding = false; }
        Redraw();
    }

    private void ApplyHighlight()
    {
        _scene.ClearFlags();
        foreach (var r in Selection.Items) Apply(r, Selection.FlagSelected);
        if (!Selection.Hover.IsEmpty && !Selection.Contains(Selection.Hover.Id)) Apply(Selection.Hover, Selection.FlagHover);
        if (_treeHover != null) ApplyItem(_treeHover, Selection.FlagHover);

        void Apply(PickRef r, uint flags)
        {
            if (r.Target is Part part) { _scene.SetPartFlags(part.Id, flags); return; }
            if (r.Target is Sketch sketch) { _scene.SetSketchFlags(sketch.Id, flags); return; }
            if (r.Kind == PickKind.Body) { _scene.SetPartFlags(r.Id, flags); return; }
            _scene.SetFlags(r.Id, flags);
        }
    }

    private ModelItem? _treeHover;

    private void ApplyItem(ModelItem item, uint flags)
    {
        switch (item)
        {
            case Part p: _scene.SetPartFlags(p.Id, flags); break;
            case Sketch sk: _scene.SetSketchFlags(sk.Id, flags); break;
            case Model.Features.Feature f:
                foreach (uint id in f.CreatedFaceIds) _scene.SetFlags(id, flags);
                foreach (uint id in f.ConsumedSketchIds()) _scene.SetSketchFlags(id, flags);
                break;
            default: _scene.SetFlags(item.Id, flags); break;
        }
    }

    /// <summary>Наведение в дереве: подсветить элемент в виде (null — снять).</summary>
    public void HighlightFromTree(ModelItem? item)
    {
        if (ReferenceEquals(_treeHover, item)) return;
        _treeHover = item;
        _highlightDirty = true;
        Redraw();
    }

    // ================================================================ камера

    public void AnimateTo(CameraState target)
    {
        Transition.Start(TransitionKind.Reorient, Camera.State, target);
        Redraw();
    }

    public void AnimateTo(Quaternion rotation)
    {
        var t = Camera.State;
        t.Rotation = rotation;
        AnimateTo(t);
    }

    public void AnimateTo(ViewOrientation o) => AnimateTo(Camera.OrientationQuat(o));

    /// <summary>Вписать всю модель (или заданный габарит).</summary>
    public void ZoomToFit(bool animate = true)
    {
        if (_doc == null) return;
        var t = Camera.State;
        var cam = new Camera { State = t, ViewportWidth = Camera.ViewportWidth, ViewportHeight = Camera.ViewportHeight };
        FitInto(cam);
        if (animate) AnimateTo(cam.State); else { Camera.State = cam.State; CameraChanged?.Invoke(); Redraw(); }
    }

    private void FitInto(Camera cam)
    {
        var b = _doc?.Bounds ?? Kernel.Bounds3.Empty;
        if (b.IsEmpty) cam.ZoomToFit(new Vector3(-50), new Vector3(50));
        else cam.ZoomToFit((Vector3)b.Min, (Vector3)b.Max);
    }

    public void ToggleProjection()
    {
        var t = Camera.State;
        t.Mode = t.Mode == ProjectionMode.Perspective ? ProjectionMode.Orthographic : ProjectionMode.Perspective;
        AnimateTo(t);
    }

    // ================================================================ мышь

    private enum Drag { None, Orbit, Pan, RightPending, Select }
    private Drag _drag;
    private Point _last, _down;

    private Vector2i Px(Point p) { double s = Scaling; return new Vector2i((int)(p.X * s), (int)(p.Y * s)); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        _last = _down = pt.Position;
        var props = pt.Properties;
        if (props.IsMiddleButtonPressed) _drag = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Drag.Pan : Drag.Orbit;
        else if (props.IsRightButtonPressed) _drag = Drag.RightPending;
        else if (props.IsLeftButtonPressed) _drag = Drag.Select;
        if (_drag != Drag.None) { Transition.Cancel(); e.Pointer.Capture(this); }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        double s = Scaling;
        float dx = (float)((p.X - _last.X) * s), dy = (float)((p.Y - _last.Y) * s);
        switch (_drag)
        {
            case Drag.Orbit: Camera.Orbit(dx, dy); _last = p; CameraChanged?.Invoke(); Redraw(); return;
            case Drag.Pan: Camera.Pan(dx, dy); _last = p; Redraw(); return;
            case Drag.RightPending:
                if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4) { _drag = Drag.Pan; Camera.Pan(dx, dy); _last = p; Redraw(); }
                return;
        }
        UpdateHover(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var was = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        if (was == Drag.RightPending) ContextRequested?.Invoke(p);
        else if (was == Drag.Select && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4) ClickSelect(p, e.KeyModifiers);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Transition.Cancel();
        var px = Px(e.GetPosition(this));
        Camera.Zoom((float)e.Delta.Y, px.X, px.Y);
        CameraChanged?.Invoke();
        Redraw();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (Selection.SetHover(PickRef.Empty)) { _highlightDirty = true; HoverChanged?.Invoke(""); Redraw(); }
    }

    private PickRef PickAt(Point p)
    {
        if (_doc == null) return PickRef.Empty;
        _picker.ActiveOwner = ActivePart ?? (ModelItem)_doc.Root;
        var hits = _picker.PickAll(_doc, Camera, Px(p), _scene.ShowDatumPlanes, null);
        foreach (var h in hits)
            if (Selection.Accepts(h.Ref.Kind)) return h.Ref;
        return PickRef.Empty;
    }

    private void UpdateHover(Point p)
    {
        var r = PickAt(p);
        if (Selection.SetHover(r))
        {
            _highlightDirty = true;
            HoverChanged?.Invoke(r.IsEmpty ? "" : Selection.DescribeHover());
            Redraw();
        }
    }

    /// <summary>Навести и выбрать в точке вида (доли размера 0..1) — для сценариев проверок.</summary>
    public void SimulatePick(double fx, double fy, bool select, bool add = false)
    {
        var p = new Point(Bounds.Width * fx, Bounds.Height * fy);
        UpdateHover(p);
        if (select) ClickSelect(p, add ? KeyModifiers.Shift : KeyModifiers.None);
    }

    private void ClickSelect(Point p, KeyModifiers mods)
    {
        var r = PickAt(p);
        bool add = mods.HasFlag(KeyModifiers.Shift), remove = mods.HasFlag(KeyModifiers.Control);
        if (r.IsEmpty) { if (!add && !remove) Selection.Clear(); return; }
        if (remove) { if (Selection.Contains(r.Id)) Selection.Select(r, additive: true); return; }
        if (add) { if (!Selection.Contains(r.Id)) Selection.Select(r, additive: true); return; }
        Selection.Select(r, additive: false);
    }
}
