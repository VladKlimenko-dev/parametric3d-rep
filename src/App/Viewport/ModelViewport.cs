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
    /// <summary>
    /// k108: эскизы, у которых концы и центры кривых видны и выбираются и вне правки эскиза (протяжка: точка на траектории
    /// для эскиза сечения).
    /// </summary>
    public readonly HashSet<uint> PointSketchIds = new();
    public void SetPointSketches(IEnumerable<uint>? ids)
    {
        PointSketchIds.Clear();
        if (ids != null && !PointSketchesOff) foreach (var i in ids) PointSketchIds.Add(i);
        _cpuPicker.PointSketches = PointSketchIds.Count > 0 ? PointSketchIds : null;
        MarkDirty();
    }
    static readonly bool PointSketchesOff = Environment.GetEnvironmentVariable("P3D_PATHPOINTS") == "0";
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
        InstallUpdateGuard();
    }

    // ---------------------------------------------------------------- 14:51: защита кадра от отказа FBO Avalonia

    // Журнал пользователя (Windows, Intel Iris Xe, WGL): «Unable to configure OpenGL FBO … GL_NO_ERROR» из
    // OpenGlControlBaseResources.BeginDraw — проверка полноты кадрового буфера Avalonia (глубина + текстура цепочки) не
    // проходит на изменении размера вида (панель операции открылась). Из очереди окна это исключение ловилось и
    // записывалось, но из перерисовки по WM_PAINT (WndProc) оно не может пройти через родной кадр — процесс снимается с
    // кодом 0xC0000409. Кадр вида запускается делегатом OpenGlControlBase._update (Avalonia 12.1): он подменяется обёрткой,
    // отказ кадра записывается в журнал (первый и каждый 50-й), кадр повторяется через 30…300 мс. Мутация P3D_GLUPDATEGUARD=0.
    static readonly bool UpdateGuardOff = Environment.GetEnvironmentVariable("P3D_GLUPDATEGUARD") == "0";
    static readonly System.Reflection.FieldInfo? UpdateField =
        typeof(OpenGlControlBase).GetField("_update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    /// <summary>Обёртка кадра поставлена.</summary>
    public bool UpdateGuarded { get; private set; }
    /// <summary>Отказы кадра подряд и всего.</summary>
    public int FrameFailuresInRow { get; private set; }
    public static int FrameFailuresTotal;

    // 15:57 (второй журнал): с обёрткой отказ кадра записан, но вслед за ним процесс снят без исключения — падение в
    // драйвере (WGL, Intel Iris Xe; так же — 0xC0000374 2026-10-16). Настольный OpenGL на этой машине ненадёжен: при
    // первом отказе кадра в режиме WGL графика переводится на ANGLE (Direct3D 11) со следующего запуска — до падения
    // (ui.json сохраняется сразу), окно сообщает об этом. Мутация P3D_WGLFALLBACK=0.
    static readonly bool WglFallbackOff = Environment.GetEnvironmentVariable("P3D_WGLFALLBACK") == "0";
    static bool _wglFallbackDone;
    /// <summary>Графика переведена с WGL на ANGLE (текст для окна).</summary>
    public static event Action<string>? GraphicsFallback;

    private static void WglFallback()
    {
        if (WglFallbackOff || _wglFallbackDone || Program.GlMode != "wgl") return;
        _wglFallbackDone = true;
        try
        {
            Services.UserPrefs.Current.GlMode = "angle";
            Services.UserPrefs.Current.Save();
            Parametric3D.Core.AppLog.Write("graphics: frame failure under WGL — switched to ANGLE from the next start");
            GraphicsFallback?.Invoke(Loc.L("Сбой видеодрайвера (OpenGL WGL). Со следующего запуска графика — ANGLE (Direct3D 11); перезапустите приложение."));
        }
        catch (Exception ex) { Parametric3D.Core.AppLog.Write("graphics fallback", ex); }
    }

    private void InstallUpdateGuard()
    {
        if (UpdateGuardOff || UpdateField?.GetValue(this) is not Action orig) return;
        try { UpdateField.SetValue(this, (Action)(() => RunFrameGuarded(orig))); UpdateGuarded = true; }
        catch (Exception ex) { Parametric3D.Core.AppLog.Write("frame guard", ex); }
    }

    /// <summary>Кадр под защитой: отказ — в журнал и повтор кадра; true — кадр прошёл.</summary>
    public bool RunFrameGuarded(Action frame)
    {
        try
        {
            frame();
            FrameFailuresInRow = 0;
            return true;
        }
        catch (Exception ex)
        {
            FrameFailuresInRow++;
            int total = System.Threading.Interlocked.Increment(ref FrameFailuresTotal);
            if (total == 1 || total % 50 == 0)
                Parametric3D.Core.AppLog.Write($"frame failed ({total} total, {FrameFailuresInRow} in a row; view {Bounds.Width:F0}×{Bounds.Height:F0} @{_renderScale:F2})", ex);
            WglFallback();
            int delay = Math.Min(300, 30 * FrameFailuresInRow);
            Avalonia.Threading.DispatcherTimer.RunOnce(() => { try { RequestNextFrameRendering(); } catch { } }, TimeSpan.FromMilliseconds(delay));
            return false;
        }
    }

    // ================================================================ документ и показ

    public Document? Document
    {
        get => _doc;
        set
        {
            if (ReferenceEquals(_doc, value)) return;
            if (_doc != null) { _doc.Invalidated -= OnDocInvalidated; _doc.RefineRequested -= OnRefine; }
            _doc = value;
            if (_doc != null) { _doc.Invalidated += OnDocInvalidated; _doc.RefineRequested += OnRefine; }
            Selection.Clear();
            Selection.SetHover(PickRef.Empty);
            MarkDirty();
        }
    }

    private void OnDocInvalidated() => MarkDirty();
    /// <summary>k113: назначено уточнение мгновенного предпросмотра — кадр запустит расчёт.</summary>
    private void OnRefine() => Redraw();

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

    /// <summary>Щелчок по виду (без протяжки) — сначала сюда: true — щелчок обработан (стрелка настройки эскиза), выбора нет.</summary>
    public Func<Point, bool>? ClickInterceptor;
    /// <summary>k101: движение курсора без нажатия (подсветка своих элементов операции, например стрелок настройки эскиза).</summary>
    public Action<Point>? HoverInterceptor;

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
            _r.MeshLinesReady = () => Avalonia.Threading.Dispatcher.UIThread.Post(Redraw);
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
        _renderScale = s; _renderW = e.NewSize.Width; _renderH = e.NewSize.Height;
        Camera.ViewportWidth = Math.Max(1, (int)Math.Round(e.NewSize.Width * s));
        Camera.ViewportHeight = Math.Max(1, (int)Math.Round(e.NewSize.Height * s));
    }

    /// <summary>
    /// Размер и масштаб для кадра — снимок из потока окна (12:28): кадр OpenGL может рисоваться в потоке отрисовки, а
    /// Bounds и поиск окна (RenderScaling) — свойства дерева элементов, читать их можно только из потока окна.
    /// </summary>
    private volatile float _renderScaleF = 1;
    private double _renderScale { get => _renderScaleF; set => _renderScaleF = (float)value; }
    private double _renderW, _renderH;

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

    /// <summary>k113: сборка сцены (на процессоре, без видеокарты) — время, мс (проверки скорости предпросмотра).</summary>
    public double BuildSceneForTest()
    {
        if (_doc == null) return 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _scene = SceneBuilder.Build(_doc, _r.Theme, Show, ActivePart, ActiveSketch, PointSketchIds.Count > 0 ? PointSketchIds : null);
        return System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
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
            bool ui = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            double s = ui ? Scaling : _renderScale;
            double bw = ui ? Bounds.Width : _renderW, bh = ui ? Bounds.Height : _renderH;
            int w = Math.Max(1, (int)Math.Round(bw * s));
            int h = Math.Max(1, (int)Math.Round(bh * s));
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
            _scene = SceneBuilder.Build(_doc, _r.Theme, Show, ActivePart, ActiveSketch, PointSketchIds.Count > 0 ? PointSketchIds : null);
            _cpuPicker.PointSketches = PointSketchIds.Count > 0 ? PointSketchIds : null;
            _overlayDirty = true;   // u10: стрелки начала координат — из сцены
            _r.ShowAllVertices = Show.Vertices;
            _r.Upload(_scene);
            // k102: сдвиг рёбер к глазу — не больше 5·10⁻⁴ габарита модели (порядок прогиба хорд разбиения; стенки тоньше
            // него встречаются редко), см. SceneRenderer.EdgeBiasWorld
            UpdateEdgeBias();
            LastSceneMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            _sceneDirty = false;
            _flagsDirty = true;
            // k101 (замечание 6): слой поверх вида — по новой детали (очерки граней в эскизе строятся по её сетке: при входе
            // в эскиз деталь ещё перестраивалась после отката, сетки не было, и слой без очерков оставался до другой правки)
            _overlayDirty = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SceneRebuilt?.Invoke());
        }
    }

    private DateTime _holdUntil = DateTime.MinValue;

    /// <summary>k102: предел сдвига рёбер к глазу — 5·10⁻⁴ габарита модели (не меньше 0.03 мм).</summary>
    private void UpdateEdgeBias()
    {
        if (_doc == null) return;
        var bb = _doc.Bounds;
        _r.EdgeBiasWorld = bb.IsEmpty ? 0f : (float)Math.Max(0.03, (bb.Max - bb.Min).Length * 5e-4);
    }

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

    private uint[]? _refHover;
    /// <summary>k118 (п.1): подсветка элементов эскиза (ссылки под курсором в списке ссылок); null — снять.</summary>
    public void HighlightSketchEntities(IEnumerable<uint>? ids)
    {
        var a = ids?.ToArray();
        if (a is { Length: 0 }) a = null;
        if (a == null && _refHover == null) return;
        _refHover = a;
        _flagsDirty = true;
        Redraw();
    }
    public IReadOnlyList<uint> RefHoverIds => _refHover ?? Array.Empty<uint>();

    /// <summary>Разбор (k118): счётчики кадра — очертания тел в стилях без тонирования, приглушённые опорные точки.</summary>
    public (int WireOutlines, int DimmedCandidates) RenderCounters => (_r.WireOutlines, _r.DimmedCandidateDraws);

    /// <summary>Разбор (k118): применить признаки подсветки без кадра (сцена — собрать, если нет).</summary>
    public void ApplyFlagsForTest() { if (_scene == null) BuildSceneForTest(); ApplyFlags(); }

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
        // k118 (замечание 08:54, п. 7): центр окружности, дуги, эллипса под курсором — подсвечена и сама кривая
        if (!CenterHoverOff && Selection.Hover is { Kind: PickKind.SketchPoint, Target: SkPoint hq } && CenterOwner(hq) is { } ow && !Selection.Contains(ow.Id))
        { Set(ow.Id, 1); CenterOwnerHovers++; }
        if (_treeHover != null) SetItem(_treeHover, 1);
        if (_refHover != null) foreach (var id in _refHover) if (!Selection.Contains(id)) Set(id, 1);
    }

    public static readonly bool CenterHoverOff = Environment.GetEnvironmentVariable("P3D_CENTERHOVER") == "0";
    /// <summary>Разбор: сколько раз центр под курсором подсветил свою кривую.</summary>
    public int CenterOwnerHovers { get; private set; }
    /// <summary>Кривая, центром которой служит точка (окружность, дуга, эллипс); иначе null.</summary>
    public static SkEntity? CenterOwner(SkPoint q) => q.Owner switch
    {
        SkCircle c when ReferenceEquals(c.Center, q) => c,
        SkArc a when ReferenceEquals(a.Center, q) => a,
        SkEllipse e when ReferenceEquals(e.Center, q) => e,
        _ => null,
    };

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

    private bool _stroking;

    /// <summary>k105: конец протяжки инструмента (обрезка по линии).</summary>
    private void StrokeFinish(Point p)
    {
        if (Tool is not Parametric3D.App.Sketching.IStrokeTool st || ActiveSketch is not { } sk) return;
        if (ScreenToSketch(p) is { } q) st.StrokeMove(q);
        bool changed = st.StrokeEnd(sk);
        AfterTool(changed);
    }

    /// <summary>Протяжка инструмента по точкам вида (доли размера) — для проверок.</summary>
    public void SimulateToolStroke(params (double X, double Y)[] pts)
    {
        if (Tool is not Parametric3D.App.Sketching.IStrokeTool st || ActiveSketch == null) return;
        st.StrokeCancel();
        foreach (var (x, y) in pts) if (ScreenToSketch(new Point(Bounds.Width * x, Bounds.Height * y)) is { } q) st.StrokeMove(q);
        StrokeFinish(new Point(Bounds.Width * pts[^1].X, Bounds.Height * pts[^1].Y));
    }

    private enum Drag { None, Orbit, Pan, RightPending, Select, Tool, Handle, Dim, SketchGeom, Gizmo }
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
        else if (props.IsLeftButtonPressed) _drag = Tool != null ? Drag.Tool : HandleHit(pt.Position) != 0 ? Drag.Handle : GizmoHit(pt.Position) != GizmoPart.None ? Drag.Gizmo : Drag.Select;
        if (_drag == Drag.Handle) BeginHandleDrag(pt.Position);
        else if (_drag == Drag.Gizmo) BeginGizmoDrag(pt.Position, GizmoHit(pt.Position));
        // u12 (замечание 17): подпись размера эскиза тащится мышью (двойной щелчок — правка значения, ниже)
        else if (props.IsLeftButtonPressed && e.ClickCount < 2 && ActiveSketch != null && BeginDimensionDrag(pt.Position)) _drag = Drag.Dim;
        // п. 6: свободные вершины и фигуры эскиза тащатся мышью (без инструмента)
        else if (props.IsLeftButtonPressed && e.ClickCount < 2 && _drag == Drag.Select && ActiveSketch != null && BeginSketchDrag(pt.Position, e.KeyModifiers)) _drag = Drag.SketchGeom;
        if (props.IsLeftButtonPressed && e.ClickCount >= 2 && ActiveSketch != null && TryEditDimension(pt.Position)) { _drag = Drag.None; e.Handled = true; return; }
        // k103: двойной щелчок по контуру сечения — его настройки
        if (props.IsLeftButtonPressed && e.ClickCount >= 2 && _drag == Drag.Select && ActiveSketch == null && Tool == null && ClickInterceptor == null && !SectionSelectOff && SectionOutlineHit(pt.Position))
        { _drag = Drag.None; SectionDoubleClicked?.Invoke(); e.Handled = true; return; }
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
            case Drag.Gizmo: GizmoDragMove(p, e.KeyModifiers); return;
            case Drag.Dim: DimensionDragMove(p); return;
            case Drag.SketchGeom: SketchDragMove(p); return;
            // k105: обрезка протяжкой — линия протяжки за курсором
            case Drag.Tool when Tool is Parametric3D.App.Sketching.IStrokeTool { StrokeEnabled: true } st && ActiveSketch != null
                                && (_stroking || Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 6):
                if (!_stroking) { _stroking = true; st.StrokeCancel(); if (ScreenToSketch(_down) is { } q0) st.StrokeMove(q0); }
                if (ScreenToSketch(p) is { } q) st.StrokeMove(q);
                _overlayDirty = true; Redraw();
                return;
            case Drag.RightPending:
                if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4) { _drag = Drag.Pan; Camera.Pan(dx, dy); _last = p; Redraw(); }
                return;
        }
        if (ComponentDragMove(p)) return;
        if (_drag == Drag.None) HoverInterceptor?.Invoke(p);
        if (RegionMove(p)) return;
        if (_drag == Drag.None && UpdateGizmoHover(p)) return;
        if (_drag == Drag.None && UpdateArrowHover(p)) return;
        if (_drag == Drag.None && UpdateSectionHover(p)) return;
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
        if (was == Drag.Gizmo) { EndGizmoDrag(); return; }
        if (was == Drag.Dim) { if (!EndDimensionDrag()) { if (ClickInterceptor?.Invoke(p) == true) return; RequestPick(p, click: true, e.KeyModifiers); } return; }
        if (was == Drag.SketchGeom) { if (!EndSketchDrag()) RequestPick(p, click: true, e.KeyModifiers); return; }
        if (was == Drag.RightPending && Tool != null) ToolFinish();
        else if (was == Drag.RightPending) ContextRequested?.Invoke(p);
        else if (was == Drag.Tool && _stroking) { _stroking = false; StrokeFinish(p); }
        else if (was == Drag.Tool && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 6) ToolClick(p);
        else if (was == Drag.Select && ComponentDragEnd()) { }
        else if (was == Drag.Select && RegionEnd(p, e.KeyModifiers)) { }
        // k107: стрелка направления операции (протяжка) — развернуть
        else if (was == Drag.Select && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4 && ActiveSketch == null && TryArrowClick(p)) { }
        else if (was == Drag.Select && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4 && ClickInterceptor?.Invoke(p) == true) { }
        // k99: щелчок по контуру плоскости сечения (вне операции и эскиза) — её настройки
        // k103: щелчок — выбор плоскости (ручки), двойной — настройки
        else if (was == Drag.Select && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4 && ClickInterceptor == null && ActiveSketch == null && Tool == null && SectionOutlineHit(p))
        { SectionSelected = true; SectionClicked?.Invoke(); }
        else if (was == Drag.Select && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= 4) { SectionSelected = false; RequestPick(p, click: true, e.KeyModifiers); }
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

    static bool SafePred(Func<PickRef, bool> f, PickRef r) { try { return f(r); } catch { return false; } }

    /// <summary>Разбор (k108): что под курсором (выбор на процессоре, с нынешним фильтром).</summary>
    public PickRef PickForTest(Point p) => CpuPick(p);

    private PickRef CpuPick(Point p)
    {
        if (_doc == null) return PickRef.Empty;
        _cpuPicker.ActiveOwner = ActivePart ?? (ModelItem)_doc.Root;
        foreach (var h in _cpuPicker.PickAll(_doc, Camera, Px(p), Show.Planes, Sketch.ParallelGhostOff ? null : ActiveSketch))
            if (Selection.Accepts(h.Ref.Kind) && (Selection.Predicate is not { } pp || SafePred(pp, h.Ref))) return h.Ref;
        return PickRef.Empty;
    }

    private void ApplyPick(PickRef r, PickRequest req)
    {
        PicksServed++;
        // значки ограничений рисуются поверх вида (не в кадре GL) — ловятся здесь и важнее того, что под ними
        if (ConstraintGlyphAt(req.P) is { IsEmpty: false } glyph && Selection.Accepts(glyph.Kind)) r = glyph;
        if (!r.IsEmpty && Selection.Predicate is { } pred && !SafePred(pred, r)) r = PickRef.Empty;
        // k97: в эскизе — очерк грани детали (цилиндра) под курсором, если под ним нет ничего из эскиза
        if (ActiveSketch != null && (r.IsEmpty || r.Kind == PickKind.Face) && FaceReferenceAt(req.P) is { IsEmpty: false } fref) r = fref;
        if (Selection.SetHover(r))
        {
            _flagsDirty = true;
            if (ActiveSketch != null) _overlayDirty = true;   // k96: очерк грани под курсором — в слое поверх
            HoverChanged?.Invoke(HoverDimension is { } hd && ActiveSketch != null ? DimHoverText(hd) : r.IsEmpty ? "" : Selection.DescribeHover());
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
