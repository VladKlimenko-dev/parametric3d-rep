using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Parametric3D.App;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Render;

namespace Parametric3D.Views;

/// <summary>
/// Базовый вид OpenGL.
///
/// Отвечает ровно за три вещи: камеру, кадровый буфер и цикл кадра.
/// Никакой модели он не знает — за содержимое отвечают наследники,
/// переопределяя DrawScene и обрабатывая выбор.
///
/// Отличия от старого MainView:
///  * нет статических ссылок на форму и проект;
///  * нет второго прохода отрисовки для пикинга — id читается из
///    целочисленного вложения того же кадра;
///  * анимация перехода вынесена в ViewTransition и не смешана
///    с логикой вида.
/// </summary>
public abstract class GLViewBase : OpenTK.GLControl.GLControl
{
    protected readonly Camera Camera = new();
    protected readonly RenderStyles Styles = new();
    protected readonly RenderContext Ctx = new();
    protected readonly SceneFramebuffer Fbo = new();
    protected readonly BackgroundRenderer Background = new();
    protected readonly CsysGizmo Gizmo = new();
    public readonly ViewTransition Transition = new();

    private readonly System.Windows.Forms.Timer _animTimer;
    private bool _glReady;

    /// <summary>
    /// Здоровье вида: счётчики, журнал, решение о восстановлении (см. GLHealth).
    /// Журнал — %LOCALAPPDATA%\Parametric3D\gl-health.log.
    /// </summary>
    public GLHealth Health { get; } = new();

    // Сторож раз в секунду проверяет, не завис ли вид: перерисовку просили,
    // а кадра нет.
    private readonly System.Windows.Forms.Timer _watchdog;

    // Восстановление откладывается: пересоздавать окно изнутри OnPaint или
    // MakeCurrent нельзя — только из следующего прохода цикла сообщений.
    private readonly System.Windows.Forms.Timer _recovery;
    private string _recoveryReason = "";

    // Состояние мыши
    private Point _lastMouse;
    private MouseButtons _dragButton = MouseButtons.None;
    private bool _dragMoved;

    // Отложенное чтение id под курсором.
    private Point _hoverPos;
    private bool _hoverPending;




    /// <summary>
    /// Сглаживание MSAA: 1 — выключено, обычно 4 или 8.
    /// Фактическое число выборок может оказаться меньше запрошенного.
    /// </summary>
    public int Samples
    {
        get => Fbo.Samples;
        set
        {
            Fbo.Samples = Math.Clamp(value, 1, 16);
            if (EnsureCurrent()) ResizeBuffers();
            Invalidate();
        }
    }

    /// <summary>
    /// Версия OpenGL, которую запрашивает контрол.
    ///
    /// ЧЕТЫРЕ компонента, а не три. Конструктор форм сериализует свойство
    /// APIVersion в InitializeComponent и неверно обрабатывает Version
    /// с неполным числом компонентов: после открытия формы в дизайнере
    /// запрошенная версия портится, и контекст создаётся не тот.
    /// Поэтому версия всегда задаётся как 3.3.0.0 — и здесь, и в разметке.
    /// </summary>
    public static readonly Version RequiredApiVersion = new Version(3, 3, 0, 0);

    // Настройки контекста задаются СВОЙСТВАМИ, а не через конструктор
    // с GLControlSettings. Причина простая: конструктор форм умеет
    // создавать контрол только вызовом без аргументов, а всё остальное
    // выставляет присваиванием свойств. Пусть оба пути будут одинаковыми.
    //
    // Глубина и сглаживание от окна не нужны: сцена рисуется в собственный
    // кадровый буфер со своим буфером глубины (см. SceneFramebuffer).
    protected GLViewBase()
    {
        API = OpenTK.Windowing.Common.ContextAPI.OpenGL;
        APIVersion = RequiredApiVersion;
        Profile = OpenTK.Windowing.Common.ContextProfile.Core;

        DoubleBuffered = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += (_, _) =>
        {
            if (!Transition.Tick(ref Camera.State)) _animTimer.Stop();
            Invalidate();
        };

        Transition.Completed += (kind, payload) => OnTransitionCompleted(kind, payload);
        Gizmo.AxisClicked += o => AnimateTo(o);

        _watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        _watchdog.Tick += (_, _) =>
        {
            bool visible = Visible && ClientSize.Width > 0 && ClientSize.Height > 0
                           && FindForm() is { WindowState: not FormWindowState.Minimized };

            if (Health.CheckStall(DateTime.UtcNow, visible)) ScheduleRecovery("вид не рисует");
        };

        _recovery = new System.Windows.Forms.Timer { Interval = 500 };
        _recovery.Tick += (_, _) =>
        {
            _recovery.Stop();
            Recover();
        };
    }

    // ------------------------------------------------------------ инициализация

    /// <summary>
    /// Инициализация OpenGL. Используется OnHandleCreated, а не OnLoad:
    /// OnLoad есть у Form, но не у Control, и в разных версиях GLControl
    /// его наличие отличается.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DesignMode) return;

        // Хендл мог быть пересоздан: это НОВЫЙ контекст, и всё, что было
        // загружено в прежний, в нём не существует.
        _contextLost = false;
        _glReady = false;

        try
        {
            MakeCurrent();
        }
        catch (Exception ex)
        {
            _contextLost = true;
            ContextLost?.Invoke(ex.Message);
            if (Health.ContextLost(ex.Message, DateTime.UtcNow)) ScheduleRecovery("контекст не создан");
            return;
        }

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);

        // Фон и таблица стилей должны говорить об одном и том же цвете:
        // к нему сходятся растворяющиеся линии сетки.
        Background.Top = Styles.BackgroundTop;
        Background.Bottom = Styles.BackgroundBottom;

        _glReady = true;
        ResizeBuffers();
        OnGLReady();

        Health.ContextCreated(DescribeDevice(), DateTime.UtcNow);
        _watchdog.Start();
        Invalidate();
    }

    /// <summary>Производитель, устройство и версия драйвера — первое, что нужно при разборе зависаний.</summary>
    private static string DescribeDevice()
    {
        try
        {
            return $"{GL.GetString(StringName.Vendor)} | {GL.GetString(StringName.Renderer)} | " +
                   $"OpenGL {GL.GetString(StringName.Version)}";
        }
        catch (Exception ex)
        {
            return "не удалось прочитать: " + ex.Message;
        }
    }

    /// <summary>Точка для создания ресурсов наследника — вызывается при каждом новом контексте.</summary>
    protected virtual void OnGLReady() { }

    /// <summary>
    /// Хендл контрола уничтожается — вместе с ним умирает и контекст.
    ///
    /// Без этого обработчика _glReady оставался true, и следующий же кадр
    /// шёл в несуществующий контекст. Номера буферов и шейдеров, выданные
    /// прежним контекстом, в новом ничего не значат, поэтому ресурсы
    /// освобождаются ЗДЕСЬ, пока контекст ещё жив.
    /// </summary>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseGpuResources();

        // БАЗОВЫЙ ОБРАБОТЧИК ТОЖЕ МОЖЕТ БРОСИТЬ.
        //
        // GLControl в своём OnHandleDestroyed снимает контекст с потока,
        // а к этому моменту окна уже нет: GLFW отвечает «WGL: Failed to
        // clear current context: The handle is invalid» и — через свой
        // обработчик ошибок — превращает это в исключение. Наш try вокруг
        // освобождения ресурсов его не ловил: бросал не он, а строка
        // с вызовом базового метода.
        //
        // Снимать контекст, которого нет, незачем, и падать на этом при
        // закрытии — тем более. Гасится только ЭТА ветвь: ошибки GLFW
        // в остальных местах по-прежнему всплывают наверх.
        try
        {
            base.OnHandleDestroyed(e);
        }
        catch (Exception)
        {
            // Окно уничтожено, снимать с него контекст не с чего.
        }
    }

    /// <summary>Отдать всё, что живёт в контексте. Повторный вызов безвреден.</summary>
    private void ReleaseGpuResources()
    {
        if (!_glReady) return;

        _glReady = false;

        try
        {
            if (!_contextLost && IsHandleCreated && !InvokeRequired) MakeCurrent();

            Fbo.Dispose();
            Gizmo.Dispose();
            Background.Dispose();

            OnGLReleasing();
        }
        catch
        {
            // Контекст мог умереть раньше нас. Освобождать в нём уже нечего,
            // и падать на этом — худшее, что можно сделать при закрытии.
        }

        // СТАТИЧЕСКИЕ ШЕЙДЕРЫ И ОБЩИЕ БУФЕРЫ ЖИВУТ В КОНТЕКСТЕ, КАК И ВСЁ ОСТАЛЬНОЕ.
        //
        // ИСПРАВЛЕНО. DisposeShared не вызывался нигде: программы шейдеров
        // создавались один раз, в первом контексте, и переживали его. Новый
        // контекст получал номера программ мёртвого, и вид молча не рисовал
        // ничего — «после сворачивания 3D-вида просто больше нет».
        // Вызовы стоят вне try выше: даже если удаление ресурсов сцены бросило,
        // статические поля обязаны обнулиться, иначе новый контекст их не пересоздаст.
        TriBatch.DisposeShared();
        PointBatch.DisposeShared();
        LineBatch.DisposeShared();
        BackgroundRenderer.DisposeShared();
    }

    /// <summary>Наследнику: отдать свои ресурсы GPU, контекст ещё жив.</summary>
    protected virtual void OnGLReleasing() { }

    /// <summary>Готов ли контекст OpenGL.</summary>
    protected bool GlReady => _glReady;

    /// <summary>
    /// ЕДИНСТВЕННЫЙ ВХОД В КОНТЕКСТ OpenGL.
    ///
    /// Раньше MakeCurrent звался напрямую из пяти мест, и каждое верило
    /// флагу _glReady. Флаг же говорит лишь о том, что контекст КОГДА-ТО
    /// был создан, а не о том, что он жив сейчас. WinForms пересоздаёт
    /// хендл контрола по множеству поводов — смена родителя, Dock, DPI,
    /// повторный показ формы, — и после пересоздания прежний DC
    /// недействителен. Отсюда и «WGL: Failed to make context current:
    /// The handle is invalid»: исключение прилетало из обработчика ошибок
    /// GLFW прямо в OnPaint или OnResize, то есть в середину кадра.
    ///
    /// ПРОВЕРЯЕТСЯ ТО, ЧТО МОЖНО ПРОВЕРИТЬ, А ОСТАЛЬНОЕ ЛОВИТСЯ.
    /// Между проверкой и вызовом хендл может исчезнуть — гонка здесь
    /// неустранима, поэтому вызов обёрнут, а не только огорожен.
    ///
    /// Потерянный контекст помечается, и больше в него не стучатся:
    /// повторные попытки рисовать в мёртвый контекст дают поток исключений,
    /// из-за которого настоящая причина теряется. Возврат к жизни —
    /// только через OnHandleCreated, то есть через новый контекст.
    /// </summary>
    protected bool EnsureCurrent()
    {
        if (!_glReady || _contextLost) return false;
        if (IsDisposed || Disposing) return false;
        if (!IsHandleCreated) return false;

        // Вызовы GL допустимы только из потока, владеющего контекстом.
        // Чужой поток даёт то же самое исключение, и отличить его
        // по тексту нельзя.
        if (InvokeRequired) return false;

        try
        {
            MakeCurrent();
            return true;
        }
        catch (Exception ex)
        {
            _contextLost = true;
            _glReady = false;

            ContextLost?.Invoke(ex.Message);

            // ИСПРАВЛЕНО. Прежде потеря была вечной: вернуть вид могло только
            // пересоздание окна, а сворачивание его не пересоздаёт. Теперь
            // окно пересоздаётся само — не чаще раза в несколько секунд и
            // с пределом попыток подряд (см. GLHealth).
            if (Health.ContextLost(ex.Message, DateTime.UtcNow)) ScheduleRecovery("MakeCurrent не удался");
            return false;
        }
    }

    /// <summary>Контекст потерян и до пересоздания хендла не вернётся.</summary>
    private bool _contextLost;

    /// <summary>
    /// Сообщение о потере контекста — чтобы приложение могло сказать
    /// об этом человеку, а не молчать с пустым окном.
    /// </summary>
    public event Action<string>? ContextLost;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // Свёрнутое окно даёт нулевой размер. Перестраивать под него буферы
        // незачем, а трогать контекст в этот момент — верный способ
        // получить недействительный DC.
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        if (!EnsureCurrent()) return;

        ResizeBuffers();
        Invalidate();
    }

    private void ResizeBuffers()
    {
        if (!_glReady) return;
        int w = Math.Max(1, ClientSize.Width);
        int h = Math.Max(1, ClientSize.Height);
        Camera.ViewportWidth = w;
        Camera.ViewportHeight = h;
        Fbo.Resize(w, h);
    }

    // ------------------------------------------------------------------- кадр

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!EnsureCurrent()) { base.OnPaint(e); return; }

        // НИ ОДНО ИСКЛЮЧЕНИЕ НЕ ВЫХОДИТ ИЗ OnPaint.
        //
        // ИСПРАВЛЕНО — это и была главная причина «вид замерзает навсегда».
        // Если OnPaint бросает, WinForms помечает контрол «ошибка при отрисовке»
        // и больше НИКОГДА его не вызывает: рисует средствами GDI красный крест,
        // которого на GL-окне не видно. Остаётся застывшая картинка, а после
        // сворачивания — пустое место. А в кадре выполнялось всё: перестроение
        // модели, решатель эскиза, тесселяция сцены. Любое исключение любой
        // операции выключало вид до перезапуска.
        try
        {
            PaintFrame();
            Health.FrameSucceeded(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            if (Health.FrameFailed(ex, DateTime.UtcNow)) ScheduleRecovery("кадры не рисуются");
        }
    }

    private void PaintFrame()
    {
        // Подготовка кадра — выбор под курсором и перестроение модели и сцены —
        // ловится отдельно: если она упала, рисуется прежняя сцена, а ошибка
        // уходит в журнал. Это ошибка модели, а не видеокарты, и пересоздавать
        // из-за неё контекст незачем.
        try
        {
            // Выбор объекта под курсором считается геометрически, на процессоре
            // (см. ScenePicker). Делается это здесь, в начале кадра, чтобы
            // подсветка успела примениться к буферам до отрисовки и появилась
            // в этом же кадре, а не в следующем.
            if (_hoverPending)
            {
                _hoverPending = false;
                OnHoverPick(_hoverPos, 0);
            }

            BeforeFrame();
        }
        catch (Exception ex)
        {
            Health.PrepareFailed(ex, DateTime.UtcNow);
        }

        Fbo.Bind();
        Fbo.Clear(new Vector4(0, 0, 0, 0));
        Background.Draw();

        Ctx.Styles = Styles;
        Ctx.View = Camera.ViewMatrix;
        Ctx.Proj = Camera.ProjMatrix;
        Ctx.Model = Matrix4.Identity;
        Ctx.CamPos = Camera.Position;
        // Кадровый буфер теперь совпадает с контролом пиксель в пиксель:
        // сглаживанием занимается MSAA, а не увеличенное разрешение,
        // поэтому никакого масштабного коэффициента больше нет.
        Ctx.Viewport = new Vector2(ClientSize.Width, ClientSize.Height);
        Ctx.WorldToPx = Camera.WorldToPx;
        Ctx.LineWidthScale = 1f;
        Ctx.Opacity = 1f;

        // Состояние, от которого отталкивается вся отрисовка кадра.
        // Задаётся здесь явно, чтобы ни один слой не зависел от того,
        // что оставил после себя предыдущий.
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);

        DrawScene(Ctx);

        if (ShowGizmo)
            Gizmo.Draw(Camera, Styles, ClientSize.Width, ClientSize.Height, 1);

        Fbo.ResolveToScreen(ClientSize.Width, ClientSize.Height);
        SwapBuffers();

        CheckGlErrors();
    }

    /// <summary>
    /// Ошибки OpenGL за кадр. Потеря контекста и нехватка памяти — повод
    /// пересоздать контекст; остальное пишется в журнал. Не больше восьми
    /// за раз: мёртвый контекст может отвечать ошибкой бесконечно.
    /// </summary>
    private void CheckGlErrors()
    {
        for (int i = 0; i < 8; i++)
        {
            ErrorCode code = GL.GetError();
            if (code == ErrorCode.NoError) return;

            if (Health.GlError((int)code, DateTime.UtcNow)) ScheduleRecovery($"glGetError 0x{(int)code:X4}");
        }
    }

    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        Health.MarkInvalidated(DateTime.UtcNow);
    }

    // ------------------------------------------------------------ восстановление

    private void ScheduleRecovery(string why)
    {
        if (DesignMode || IsDisposed) return;
        if (!Health.MayRecover(DateTime.UtcNow)) return;

        _recoveryReason = why;
        if (!_recovery.Enabled) _recovery.Start();
    }

    /// <summary>
    /// Пересоздать окно — это новый контекст: OnHandleDestroyed отдаёт ресурсы,
    /// включая статические шейдеры, OnHandleCreated поднимает всё заново,
    /// сцена пересобирается при первом кадре.
    /// </summary>
    private void Recover()
    {
        if (IsDisposed || Disposing) return;
        if (!Health.MayRecover(DateTime.UtcNow)) return;

        Health.RecoveryStarted(DateTime.UtcNow, _recoveryReason);

        try
        {
            RecreateHandle();
        }
        catch (Exception ex)
        {
            Health.Log(DateTime.UtcNow, "пересоздание окна не удалось", ex);
        }

        Invalidate();
    }

    /// <summary>Всё, что нужно сделать до кадра (перестроение модели, синхронизация буферов).</summary>
    protected virtual void BeforeFrame() { }

    protected abstract void DrawScene(RenderContext ctx);

    public virtual bool ShowGizmo => true;

    // ------------------------------------------------------------------ пикинг



    // ------------------------------------------------------------------- мышь

    /// <summary>
    /// Правка заблокирована — идёт фоновая пересборка модели. Навигация (вращение,
    /// сдвиг, масштаб, указатель осей) работает; выбор, правка и контекстное меню — нет:
    /// фоновый поток читает модель, и правка из интерфейса дала бы гонку.
    /// </summary>
    protected virtual bool EditingLocked => false;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _lastMouse = e.Location;
        _dragButton = e.Button;
        _dragMoved = false;

        if (e.Button == MouseButtons.Left)
        {
            if (ShowGizmo && Gizmo.HitTest(Camera, e.Location, ClientSize.Height))
            {
                _dragButton = MouseButtons.None;
                return;
            }
            if (EditingLocked) { _dragButton = MouseButtons.None; return; }
            OnPickDown(e, 0);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int dx = e.X - _lastMouse.X;
        int dy = e.Y - _lastMouse.Y;

        if (_dragButton != MouseButtons.None && (Math.Abs(dx) + Math.Abs(dy)) > 0)
            _dragMoved = true;

        bool handled = false;

        // Правая кнопка всегда перемещает вид — в эскизе это основной способ.
        // Средняя вращает, а там, где вращение запрещено (эскиз), тоже
        // перемещает. Shift со средней кнопкой — перемещение в любом режиме.
        if (_dragButton == MouseButtons.Right)
        {
            Camera.Pan(dx, dy);
            handled = true;
        }
        else if (_dragButton == MouseButtons.Middle)
        {
            if (ModifierKeys.HasFlag(Keys.Shift) || !AllowOrbit) Camera.Pan(dx, dy);
            else Camera.Orbit(dx, dy);
            handled = true;
        }
        else if (_dragButton == MouseButtons.Left)
        {
            handled = OnPickDrag(e, dx, dy);
        }

        if (!handled && _dragButton == MouseButtons.None)
        {
            // Инструмент рисования забирает движение себе — ему нужна
            // позиция курсора, а не объект под ним.
            if (!OnHoverMove(e))
            {
                _hoverPos = e.Location;
                _hoverPending = true;
            }
            Invalidate();
        }

        _lastMouse = e.Location;
        if (handled) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && !EditingLocked) OnPickUp(e, _dragMoved);

        // Правая кнопка перемещает вид. Но если её отпустили, не сдвинув
        // мышь, это был не сдвиг вида, а вызов контекстного меню.
        if (e.Button == MouseButtons.Right && !_dragMoved && !EditingLocked) OnContextClick(e);

        _dragButton = MouseButtons.None;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // Координаты передаются как есть. Camera.ScreenRay уже работает
        // в оконных координатах (Y вниз) — она сама переворачивает ось.
        // Лишний переворот здесь зеркалил точку привязки относительно
        // середины экрана, и вид уезжал от курсора вместо приближения к нему.
        Camera.Zoom(e.Delta / 120f, e.X, e.Y);
        Invalidate();
    }

    /// <summary>В эскизе вращение орбитой запрещено — вид смотрит строго на плоскость.</summary>
    protected virtual bool AllowOrbit => true;

    protected virtual void OnPickDown(MouseEventArgs e, uint id) { }
    protected virtual bool OnPickDrag(MouseEventArgs e, int dx, int dy) => false;
    protected virtual void OnPickUp(MouseEventArgs e, bool wasDrag) { }

    /// <summary>Движение мыши без кнопок. true — событие забрал инструмент.</summary>
    protected virtual bool OnHoverMove(MouseEventArgs e) => false;

    /// <summary>Под курсором прочитан id. Вызывается из цикла отрисовки.</summary>
    protected virtual void OnHoverPick(Point pos, uint id) { }

    /// <summary>Щелчок правой кнопкой без перетаскивания — вызов контекстного меню.</summary>
    protected virtual void OnContextClick(MouseEventArgs e) { }

    // -------------------------------------------------------------- переходы

    /// <summary>Плавный поворот к стандартной ориентации.</summary>
    public void AnimateTo(ViewOrientation o)
    {
        var to = Camera.State;
        to.Rotation = Render.Camera.OrientationQuat(o);
        StartTransition(TransitionKind.Reorient, to, null);
    }

    public void AnimateTo(CameraState to, TransitionKind kind = TransitionKind.ToItem, object? payload = null)
        => StartTransition(kind, to, payload);

    protected void StartTransition(TransitionKind kind, CameraState to, object? payload)
    {
        Transition.Start(kind, Camera.State, to, payload);
        _animTimer.Start();
        Invalidate();
    }

    protected virtual void OnTransitionCompleted(TransitionKind kind, object? payload) { }

    public void ZoomToFit(Bounds3 b, bool animate = true)
    {
        if (b.IsEmpty) return;
        var target = Camera.State;
        var saved = Camera.State;
        Camera.ZoomToFit(b.Min.ToF(), b.Max.ToF());
        target = Camera.State;
        if (animate)
        {
            Camera.State = saved;
            StartTransition(TransitionKind.ToItem, target, null);
        }
        else Invalidate();
    }

    // ------------------------------------------------------------------ прочее

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Escape or Keys.Delete or Keys.Up or Keys.Down or Keys.Left or Keys.Right
        || base.IsInputKey(keyData);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Dispose();
            _watchdog.Dispose();
            _recovery.Dispose();
            ReleaseGpuResources();
        }

        // Базовый Dispose разбирает контрол GLControl, а тот снимает
        // контекст с потока — и GLFW отвечает «Failed to clear current
        // context», потому что окна уже нет. Наблюдение верное
        // и бесполезное; исключением его делает обработчик ошибок внутри
        // OpenTK, и бросает он из нативного обратного вызова.
        //
        // Здесь этот путь закрыт. Остальные пути разрушения окна
        // из кода не перехватить: обратный вызов срабатывает до того,
        // как управление вернётся в наш метод.
        try
        {
            base.Dispose(disposing);
        }
        catch (Exception)
        {
            // Окно уже разобрано; освобождать в нём нечего.
        }
    }
}
