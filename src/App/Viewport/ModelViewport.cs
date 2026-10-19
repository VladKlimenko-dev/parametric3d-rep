using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;
using Parametric3D.Render.Engine;
using Parametric3D.Render.Gl;

namespace Parametric3D.App.Viewport;

/// <summary>
/// 3D-ВИД МОДЕЛИ на движке Render/Engine (SceneRenderer): контекст OpenGL от Avalonia, камера, наведение и выбор,
/// плавные переходы вида, стили и список «что показывать».
///
/// Мышь (ТЗ_UI, 0.6): средняя — вращать, Shift+средняя или правая с перетаскиванием — сдвиг, колесо — масштаб к курсору;
/// левая — выбрать, Shift — добавить, Ctrl — исключить. Короткий правый щелчок — общие команды (ContextRequested).
///
/// Выбор под курсором — проходом выбора видеокарты (один пиксель, заслонение учтено, во всех стилях, вершины и рёбра
/// тела ловятся с допуском). Без OpenGL (проверки без экрана) — расчётом на процессоре (ScenePicker).
/// Всё, что трогает видеокарту, делается только в OnOpenGlRender: мышь лишь запоминает запрос.
/// </summary>
public sealed partial class ModelViewport : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>Вид принимает мышь всей площадью (и до первого кадра, и без OpenGL).</summary>
    public bool HitTest(Point p) => new Rect(Bounds.Size).Contains(p);

    public readonly Camera Camera = new();
    public readonly Selection Selection = new();
    public readonly ViewTransition Transition = new() { Steps = 14 };

    // новый контекст OpenGL — новый рисовальщик: номера объектов прежнего контекста в новом не значат ничего (и удалять
    // их там нельзя — это были бы чужие объекты)
    private SceneRenderer _r = new();
    private readonly ScenePicker _cpuPicker = new();
    private SceneData? _scene;
    private Document? _doc;
    private bool _glReady, _sceneDirty = true, _flagsDirty = true, _rebuilding;

    /// <summary>Допуск выбора линий и точек, логические пиксели.</summary>
    public float PickTolerance = 5f;

    public string GlInfo { get; private set; } = "";
    public string? GlError { get; private set; }
    public event Action<string>? GlReady;
    public event Action<string>? HoverChanged;
    public event Action? CameraChanged;
    public event Action<Point>? ContextRequested;
    /// <summary>Сцена перестроена (после перестроения модели) — панели обновляют сведения.</summary>
    public event Action? SceneRebuilt;

    public ModelViewport()
    {
        Camera.State.Rotation = HomeRotation;
        Focusable = true;
        ClipToBounds = true;
        Selection.Changed += () => { _flagsDirty = true; Redraw(); };
        Transition.Completed += (_, _) => CameraChanged?.Invoke();
    }

    // ================================================================ документ и показ

    public Document? Document
    {
        get => _doc;
        set
        {
            if (ReferenceEquals(_doc, value)) return;
            if (_doc != null) _doc.Invalidated -= OnDocInvalidated;
            _doc = value;
            if (_doc != null) _doc.Invalidated += OnDocInvalidated;
            Selection.Clear();
            Selection.SetHover(PickRef.Empty);
            MarkDirty();
        }
    }

    private void OnDocInvalidated() => MarkDirty();

    public Part? ActivePart { get; set; }

    /// <summary>Стиль тел (по умолчанию — тонированный с рёбрами).</summary>
    public DisplayStyle Style
    {
        get => _r.Style;
        set { _r.Style = value; Redraw(); }
    }

    /// <summary>Что показывать (кнопка «Опорные» и её список). После правки — MarkDirty.</summary>
    public ShowOptions Show { get; } = new();

    public void MarkDirty() { _sceneDirty = true; _flagsDirty = true; Redraw(); }

    public bool DarkTheme
    {
        get => _r.Theme.Dark;
        set { _r.Theme = value ? RenderTheme.DarkTheme() : RenderTheme.LightTheme(); MarkDirty(); }
    }

    public void Redraw() => RequestNextFrameRendering();

    /// <summary>Цвета вида (для надстроек поверх него — значков ограничений и т. п.).</summary>
    public RenderTheme Theme => _r.Theme;

    /// <summary>Кадр нарисован (вызывается в потоке окна): надстройки поверх вида перерисовываются вслед за ним.</summary>
    public event Action? FrameDrawn;

    /// <summary>Таблица выбора текущей сцены (для подсказок и проверок).</summary>
    public SceneData? Scene => _scene;

    // ================================================================ OpenGL

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            bool es = GlVersion.Type == GlProfileType.OpenGLES;
            GL.Load(gl.GetProcAddress, es);
            var old = _r;
            _r = new SceneRenderer { Theme = old.Theme, Style = old.Style, ShowAllVertices = old.ShowAllVertices };
            _scene = null;
            _overlayDirty = true;
            GlInfo = $"{GL.GetString(StringName.Vendor)} | {GL.GetString(StringName.Renderer)} | {(es ? "OpenGL ES" : "OpenGL")} {GL.GetString(StringName.Version)}";
            AppLog.Write("GL: " + GlInfo);
            _glReady = true;
            MarkDirty();
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
        AppLog.Write("GL: deinit");
        _r.Dispose();
        _glReady = false;
    }

    protected override void OnOpenGlLost()
    {
        AppLog.Write("GL: context lost");
        _glReady = false;
        _sceneDirty = true;
        base.OnOpenGlLost();
    }

    protected override void OnSizeChanged(Avalonia.Controls.SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        double s = Scaling;
        Camera.ViewportWidth = Math.Max(1, (int)Math.Round(e.NewSize.Width * s));
        Camera.ViewportHeight = Math.Max(1, (int)Math.Round(e.NewSize.Height * s));
    }

    public double Scaling => Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>Снимок следующего кадра (RGBA снизу вверх, ширина, высота) — для проверок.</summary>
    public Action<byte[], int, int>? CaptureNextFrame;

    private int _frameErrors;

    // ---- замеры для кнопки «Тест»
    /// <summary>Сколько кадров нарисовано с запуска.</summary>
    public long FramesRendered { get; private set; }
    /// <summary>Время последнего кадра на процессоре (подготовка, проход выбора, команды кадра), мс.</summary>
    public double LastFrameMs { get; private set; }
    /// <summary>Последняя сборка сцены и загрузка в видеокарту, мс.</summary>
    public double LastSceneMs { get; private set; }
    /// <summary>Ошибки OpenGL и исключения кадра с запуска.</summary>
    public int FrameErrors => _frameErrors;
    /// <summary>Сглаживание (выборок на пиксель).</summary>
    public int Msaa => _r.Samples;
    /// <summary>Сколько запросов выбора под курсором обслужено (наведение и щелчки).</summary>
    public long PicksServed { get; private set; }
    /// <summary>Выбор делает видеокарта (иначе — процессор, без OpenGL).</summary>
    public bool GpuPicking => _glReady;
    /// <summary>Кадр рисуется в потоке окна (null — кадров ещё не было).</summary>
    public bool? RenderOnUiThread { get; private set; }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_glReady) return;
        if (RenderOnUiThread == null)
        {
            RenderOnUiThread = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            AppLog.Write($"GL: render on {(RenderOnUiThread == true ? "UI thread" : "render thread")}");
        }
        long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            double s = Scaling;
            int w = Math.Max(1, (int)Math.Round(Bounds.Width * s));
            int h = Math.Max(1, (int)Math.Round(Bounds.Height * s));
            Camera.ViewportWidth = w;
            Camera.ViewportHeight = h;
            _r.Resize(w, h);

            if (Transition.IsRunning)
            {
                var st = Camera.State;
                if (Transition.Tick(ref st)) { Camera.State = st; CameraChanged?.Invoke(); }
                if (Transition.IsRunning) RequestNextFrameRendering();
            }

            using (Diagnostics.UiTimes.Measure("вид: подготовка сцены")) PrepareScene();
            var f = Frame(s);

            // запросы выбора (мышь) — номер под курсором читается до кадра
            if (_pickRequest is { } pr)
            {
                _pickRequest = null;
                uint id;
                using (Diagnostics.UiTimes.Measure("вид: проход выбора")) id = _scene == null ? 0 : _r.Pick(f, (int)(pr.P.X * s), (int)(pr.P.Y * s), PickTolerance);
                var r = id > 0 && _scene != null ? _scene.PickTable[(int)id].Ref : PickRef.Empty;
                if (!r.IsEmpty && !Selection.Accepts(r.Kind)) r = PickRef.Empty;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyPick(r, pr));
            }

            if (_overlayDirty || GridStale()) { _overlayDirty = false; var (g, l, pt) = BuildOverlay(); _r.SetOverlay(g, l, pt); }
            _r.SketchMode = ActiveSketch != null;
            if (_flagsDirty) { ApplyFlags(); _flagsDirty = false; }
            using (Diagnostics.UiTimes.Measure("вид: кадр")) _r.Draw(f, fb);

            if (CaptureNextFrame is { } cap)
            {
                CaptureNextFrame = null;
                var px = new byte[w * h * 4];
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fb);
                GL.ReadPixelsRgba(0, 0, w, h, px);
                cap(px, w, h);
            }
            FramesRendered++;
            if (FrameDrawn != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => FrameDrawn?.Invoke(), Avalonia.Threading.DispatcherPriority.Render);
            LastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
            for (int i = 0; i < 4; i++)
            {
                var e = GL.GetError();
                if (e == ErrorCode.NoError) break;
                if (_frameErrors++ < 20) AppLog.Write($"GL error 0x{(int)e:X4}");
            }
        }
        catch (Exception ex)
        {
            if (_frameErrors++ < 20) AppLog.Write("frame", ex);
        }
    }

    private SceneRenderer.FrameParams Frame(double scale) => new()
    {
        View = Camera.ViewMatrix,
        Proj = Camera.ProjMatrix,
        Near = Camera.State.Mode == ProjectionMode.Perspective ? Camera.ZNear * 1.01f : 0f,
        Scale = (float)scale,
    };

    private void PrepareScene()
    {
        if (_doc == null) { if (_scene != null) { _scene = new SceneData(); _r.Upload(_scene); } return; }
        // перестроение модели — только из потока окна (результат применяется к модели там же, где её правят)
        if (!_doc.IsBuilding && !_rebuilding && _doc.AllParts().Any(p => p.IsDirty))
        {
            _rebuilding = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RebuildInBackground());
        }
        // деталь заменена снимком (отмена правки): пока новая не построена, на экране остаётся прежняя (п. 2)
        if (_sceneDirty && _holdUntil > DateTime.Now && (_doc.IsBuilding || _doc.AllParts().Any(p => p.IsDirty))) return;
        if (_sceneDirty)
        {
            _holdUntil = DateTime.MinValue;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _scene = SceneBuilder.Build(_doc, _r.Theme, Show, ActivePart, ActiveSketch);
            _overlayDirty = true;   // u10: стрелки начала координат — из сцены
            _r.ShowAllVertices = Show.Vertices;
            _r.Upload(_scene);
            LastSceneMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            _sceneDirty = false;
            _flagsDirty = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SceneRebuilt?.Invoke());
        }
    }

    private DateTime _holdUntil = DateTime.MinValue;

    /// <summary>Не перестраивать сцену, пока детали не посчитаны (не дольше 10 с): деталь не «пропадает» на время расчёта.</summary>
    public void HoldSceneUntilBuilt() { _holdUntil = DateTime.Now.AddSeconds(10); }

    private async Task RebuildInBackground()
    {
        _rebuilding = true;
        try
        {
            if (_doc != null && !_doc.IsBuilding && await _doc.RebuildDirtyAsync()) _sceneDirty = true;
        }
        catch (Exception ex) { AppLog.Write("background rebuild", ex); }
        finally { _rebuilding = false; }
        if (_doc != null) Diagnostics.UiTimes.Record("модель: применение расчёта в потоке окна", _doc.LastApplyMs);
        _sceneDirty = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(Redraw);
    }

    // ================================================================ подсветка

    private ModelItem? _treeHover;

    public void HighlightFromTree(ModelItem? item)
    {
        if (ReferenceEquals(_treeHover, item)) return;
        _treeHover = item;
        _flagsDirty = true;
        Redraw();
    }

    private void ApplyFlags()
    {
        _r.ClearFlags();
        if (_scene == null) return;
        // ошибки операций — красным по созданным ими граням
        if (_doc != null)
            foreach (var f in _doc.Root.Descendants().OfType<Feature>())
                if (f.Error != null) foreach (var id in f.CreatedFaceIds) Set(id, 8);
        // u13: грани квилтов (поверхностей) — признак 16 (оттенок поверхности)
        if (_doc != null)
            foreach (var p in _doc.AllParts())
                foreach (var q in p.Quilts) foreach (var sq in Simple(q)) foreach (var f in sq.Faces) Set(f.Id, 16);
        foreach (var r in Selection.Items) SetRef(r, 2);
        if (!Selection.Hover.IsEmpty && !Selection.Contains(Selection.Hover.Id)) SetRef(Selection.Hover, 1);
        if (_treeHover != null) SetItem(_treeHover, 1);
    }

    private static IEnumerable<Quilt> Simple(Quilt q) => q.Parts.Count > 0 ? q.Parts.SelectMany(Simple) : new[] { q };

    private void Set(uint id, byte flag)
    {
        if (_scene!.ById.TryGetValue(id, out var l)) foreach (int i in l) _r.SetFlags(i, flag);
    }

    private void SetRef(PickRef r, byte flag)
    {
        if (r.Target is Sketch sk) { SetItem(sk, flag); return; }
        if (r.Kind is PickKind.Body or PickKind.Feature or PickKind.Assembly && r.Target is ModelItem mi) { SetItem(mi, flag); return; }
        Set(r.Id, flag);
    }

    private void SetItem(ModelItem item, byte flag)
    {
        var t = _scene!.PickTable;
        switch (item)
        {
            case Part p:
                for (int i = 1; i < t.Count; i++) if (t[i].PartId == p.Id) _r.SetFlags(i, flag);
                break;
            case Sketch sk:
                for (int i = 1; i < t.Count; i++) if (t[i].SketchId == sk.Id) _r.SetFlags(i, flag);
                break;
            case Feature f:
                foreach (uint id in f.CreatedFaceIds) Set(id, flag);
                foreach (uint id in f.ConsumedSketchIds()) for (int i = 1; i < t.Count; i++) if (t[i].SketchId == id) _r.SetFlags(i, flag);
                break;
            case Assembly a:
                foreach (var p in a.Descendants().OfType<Part>()) SetItem(p, flag);
                break;
            default: Set(item.Id, flag); break;
        }
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

    public void GoHome()
    {
        var cam = new Camera { State = Camera.State, ViewportWidth = Camera.ViewportWidth, ViewportHeight = Camera.ViewportHeight };
        cam.State.Rotation = HomeRotation;
        FitInto(cam);
        AnimateTo(cam.State);
    }

    public static Quaternion HomeRotation => ViewMath.LookFrom(new Vector3(1, 1, 1), Vector3.UnitY);

    public void ZoomToFit(bool animate = true)
    {
        if (_doc == null) return;
        var cam = new Camera { State = Camera.State, ViewportWidth = Camera.ViewportWidth, ViewportHeight = Camera.ViewportHeight };
        FitInto(cam);
        if (animate) AnimateTo(cam.State); else { Camera.State = cam.State; CameraChanged?.Invoke(); Redraw(); }
    }

    private void FitInto(Camera cam)
    {
        var b = _doc?.Bounds ?? Kernel.Bounds3.Empty;
        if (b.IsEmpty) cam.ZoomToFit(new Vector3(-50), new Vector3(50));
        else cam.ZoomToFit((Vector3)b.Min, (Vector3)b.Max);
    }

    public bool IsPerspective => Camera.State.Mode == ProjectionMode.Perspective;

    public void ToggleProjection()
    {
        var t = Camera.State;
        t.Mode = t.Mode == ProjectionMode.Perspective ? ProjectionMode.Orthographic : ProjectionMode.Perspective;
        AnimateTo(t);
    }

    // ================================================================ мышь

    private enum Drag { None, Orbit, Pan, RightPending, Select, Tool, Handle, Dim }
    private Drag _drag;
    private Point _last, _down;

    private sealed record PickRequest(Point P, bool Click, KeyModifiers Mods);
    private PickRequest? _pickRequest;

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
        else if (props.IsLeftButtonPressed) _drag = Tool != null ? Drag.Tool : HandleHit(pt.Position) != 0 ? Drag.Handle : Drag.Select;
        if (_drag == Drag.Handle) BeginHandleDrag(pt.Position);
        // u12 (замечание 17): подпись размера эскиза тащится мышью (двойной щелчок — правка значения, ниже)
        else if (props.IsLeftButtonPressed && e.ClickCount < 2 && ActiveSketch != null && BeginDimensionDrag(pt.Position)) _drag = Drag.Dim;
        if (props.IsLeftButtonPressed && e.ClickCount >= 2 && ActiveSketch != null && TryEditDimension(pt.Position)) { _drag = Drag.None; e.Handled = true; return; }
        if (_drag == Drag.Tool && e.ClickCount >= 2) { ToolFinish(); _drag = Drag.None; e.Handled = true; return; }
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
            case Drag.Handle: HandleDragMove(p); return;
            case Drag.Dim: DimensionDragMove(p); return;
            case Drag.RightPending:
                if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4) { _drag = Drag.Pan; Camera.Pan(dx, dy); _last = p; Redraw(); }
                return;
        }
        if (ComponentDragMove(p)) return;
        if (RegionMove(p)) return;
        if (Tool != null) ToolMove(p);
        if (ActiveSketch != null) UpdateDimensionHover(p);
        RequestPick(p, click: false, KeyModifiers.None);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var was = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        if (was == Drag.Handle) { EndHandleDrag(); return; }
        if (was == Drag.Dim) { if (!EndDimensionDrag()) RequestPick(p, click: true, e.KeyModifiers); return; }
        if (was == Drag.RightPending && Tool != null) ToolFinish();
        else if (was == Drag.RightPending) ContextRequested?.Invoke(p);
        else if (was == Drag.Tool && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 6) ToolClick(p);
        else if (was == Drag.Select && ComponentDragEnd()) { }
        else if (was == Drag.Select && RegionEnd(p, e.KeyModifiers)) { }
        else if (was == Drag.Select && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4) RequestPick(p, click: true, e.KeyModifiers);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Transition.Cancel();
        var px = Px(e.GetPosition(this));
        Camera.Zoom((float)e.Delta.Y, px.X, px.Y);
        CameraChanged?.Invoke();
        if (ActiveSketch != null) _overlayDirty = true;
        Redraw();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pickRequest = null;
        if (Selection.SetHover(PickRef.Empty)) { _flagsDirty = true; HoverChanged?.Invoke(""); Redraw(); }
    }

    /// <summary>Выбор под курсором: видеокартой в следующем кадре или сразу на процессоре (без OpenGL).</summary>
    private void RequestPick(Point p, bool click, KeyModifiers mods)
    {
        var req = new PickRequest(p, click, mods);
        if (!_glReady)
        {
            ApplyPick(CpuPick(p), req);
            return;
        }
        // щелчок не теряется: наведение, запрошенное раньше, им заменяется
        if (_pickRequest is { Click: true } && !click) return;
        _pickRequest = req;
        Redraw();
    }

    private PickRef CpuPick(Point p)
    {
        if (_doc == null) return PickRef.Empty;
        _cpuPicker.ActiveOwner = ActivePart ?? (ModelItem)_doc.Root;
        foreach (var h in _cpuPicker.PickAll(_doc, Camera, Px(p), Show.Planes, null))
            if (Selection.Accepts(h.Ref.Kind)) return h.Ref;
        return PickRef.Empty;
    }

    private void ApplyPick(PickRef r, PickRequest req)
    {
        PicksServed++;
        // значки ограничений рисуются поверх вида (не в кадре GL) — ловятся здесь и важнее того, что под ними
        if (ConstraintGlyphAt(req.P) is { IsEmpty: false } glyph && Selection.Accepts(glyph.Kind)) r = glyph;
        if (Selection.SetHover(r))
        {
            _flagsDirty = true;
            HoverChanged?.Invoke(r.IsEmpty ? "" : Selection.DescribeHover());
            Redraw();
        }
        if (!req.Click) return;
        // п. 7: Ctrl — добавить или (повторный щелчок) убрать; Shift — добавить
        bool toggle = req.Mods.HasFlag(KeyModifiers.Control), add = req.Mods.HasFlag(KeyModifiers.Shift);
        if (r.IsEmpty) { if (!add && !toggle) Selection.Clear(); return; }
        if (TryReference(r)) return;
        // u13: режим выбора «операция» и др. — щелчок по грани заменяется (операция, создавшая грань)
        if (PickMap != null) { r = PickMap(r); if (r.IsEmpty) return; }
        // п. 4: ребро — вместе с касательной цепочкой (Alt — одно ребро; u13: выключаемо кнопкой)
        var group = r.Kind == PickKind.Edge && AutoChain && !req.Mods.HasFlag(KeyModifiers.Alt) && ExpandEdge != null ? ExpandEdge(r) : new List<PickRef> { r };
        if (toggle && Selection.Contains(r.Id))
        {
            foreach (var g in group) if (Selection.Contains(g.Id)) Selection.Select(g, additive: true);
            return;
        }
        if (toggle || add)
        {
            foreach (var g in group) if (!Selection.Contains(g.Id)) Selection.Select(g, additive: true);
            return;
        }
        Selection.Select(group[0], additive: false);
        foreach (var g in group.Skip(1)) Selection.Select(g, additive: true);
    }

    /// <summary>Ребро → касательная цепочка рёбер (задаёт окно; null — только само ребро).</summary>
    public Func<PickRef, List<PickRef>>? ExpandEdge;

    /// <summary>Навести и выбрать в точке вида (доли размера 0..1) — для сценариев проверок.</summary>
    public void SimulatePick(double fx, double fy, bool select, bool add = false)
    {
        var p = new Point(Bounds.Width * fx, Bounds.Height * fy);
        RequestPick(p, select, add ? KeyModifiers.Shift : KeyModifiers.None);
    }
}
