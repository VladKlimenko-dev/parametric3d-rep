using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model;

/// <summary>
/// Документ: корень всего. Содержит корневую сборку, таблицу параметров,
/// библиотеку материалов и настройки.
///
/// Это замена статическому классу Project. Статика была главной причиной
/// «запутанности»: любой объект мог дотянуться до вида и формы, порядок
/// инициализации был неявным, а два документа открыть было невозможно
/// в принципе. Здесь документ — обычный объект, который передаётся явно.
/// </summary>
public sealed class Document
{
    public string Title = "Untitled";
    public string? FilePath;
    public bool Modified;

    public readonly Assembly Root;

    /// <summary>
    /// Документ сборки. У документа детали корень — служебная сборка: её опорные элементы (начало, плоскости) не существуют
    /// для пользователя — не рисуются и не выбираются (иначе у детали двойные плоскости).
    /// </summary>
    public bool IsAssembly { get; set; }

    /// <summary>Корень — служебный (документ одной детали): опорные элементы корня не показываются.</summary>
    public bool RootIsHidden => !IsAssembly && Root.Children.OfType<Part>().Count() == 1 && !Root.Children.OfType<Assembly>().Any();
    public readonly ParameterTable Parameters = new();

    /// <summary>
    /// Настройки внешнего вида и единиц. Принадлежат документу и
    /// сохраняются вместе с ним; Settings.Current указывает на настройки
    /// активного документа, чтобы рисующий код читал их напрямую.
    /// </summary>
    public readonly Settings Settings = new();
    public readonly List<Material> Materials = new();
    public ISketchSolver Solver = new NumericSketchSolver();

    /// <summary>Поднимается, когда модель изменилась и требуется перестроение/перерисовка.</summary>
    public event Action? Invalidated;

    public Document(string title = "Untitled")
    {
        Title = title;
        Root = new Assembly("Assembly");
        Root.Changed += _ => { Modified = true; Invalidated?.Invoke(); };

        // Документ становится источником настроек для всей отрисовки.
        Core.Settings.Current = Settings;
        Core.Settings.NotifyChanged();

        Materials.Add(Material.Steel());
        Materials.Add(Material.Aluminium());
        Materials.Add(Material.Plastic());
        Materials.Add(Material.Brass());

        Parameters.Geometry = new DocumentGeometryResolver(this);
        Parameters.External = n => Params.ModelRefs.Resolve(Root, n);   // k102: пути к размерам (a"…".p"…".s"…".dN)
        // K06: правка параметра не сбрасывает сохранённые состояния деталей — каждая деталь сама
        // сравнит значения и пересчитает только операции, которые от изменённых параметров зависят.
        Parameters.Changed += () => { Modified = true; ParametersChanged(); };
    }

    public IEnumerable<Part> AllParts()
    {
        foreach (var item in Root.Descendants())
            if (item is Part p) yield return p;
    }

    public Part? FindPart(uint id) => AllParts().FirstOrDefault(p => p.Id == id);

    public Sketch? FindSketch(uint id)
    {
        foreach (var item in Root.Descendants())
            if (item is Sketch s && s.Id == id) return s;
        return null;
    }

    /// <summary>K06: параметры изменились; детали пересчитают только зависимые операции.</summary>
    /// <summary>
    /// UI: ТАБЛИЦА ПАРАМЕТРОВ ДЛЯ РАСЧЁТА ДЕТАЛИ — по уровням: деталь → сборки над ней → документ. Пустые уровни
    /// пропускаются; если своих параметров нет нигде, это таблица документа (как прежде).
    /// </summary>
    /// <summary>UI (кнопка «Тест»): сколько длилось последнее применение результатов расчёта (в потоке окна), мс.</summary>
    public double LastApplyMs { get; private set; }

    static readonly bool ScopeAlwaysOff = Environment.GetEnvironmentVariable("P3D_PARTSCOPE") == "0";

    public ParameterTable ScopeFor(Part part)
    {
        var chain = new List<ParameterTable>();
        // k102: таблица детали — всегда (через неё — пути к размерам от этой детали: s"Эскиз".d0, f"Операция".d1)
        if (part.LocalParameters.Count > 0 || !ScopeAlwaysOff) chain.Add(part.LocalParameters);
        for (var a = part.Parent; a != null && !ReferenceEquals(a, Root); a = a.Parent)
            if (a is Assembly asm && asm.LocalParameters.Count > 0) chain.Add(asm.LocalParameters);
        if (chain.Count == 0) return Parameters;
        for (int i = 0; i < chain.Count; i++) chain[i].Parent = i + 1 < chain.Count ? chain[i + 1] : Parameters;
        for (int i = chain.Count - 1; i >= 0; i--) { chain[i].Geometry = Parameters.Geometry; chain[i].Units = Parameters.Units; chain[i].Recalculate(); }
        return chain[0];
    }

    public void ParametersChanged()
    {
        foreach (var p in AllParts()) p.ParametersChanged();
        Invalidated?.Invoke();
    }

    public void InvalidateAll()
    {
        foreach (var p in AllParts()) p.Invalidate();
        Invalidated?.Invoke();
    }

    // ================================================================ пересборка
    private Task<bool>? _building;

    /// <summary>
    /// Идёт фоновая пересборка. Пока флаг поднят, модель менять нельзя: фоновый
    /// поток читает элементы и эскизы (и решатель пишет координаты в эскизы).
    /// Вид при этом рисует прежние тела — они не меняются до применения результата.
    /// </summary>
    public bool IsBuilding => _isBuilding;
    private bool _isBuilding;

    private List<Part> _progressParts = new();
    /// <summary>
    /// k121: ХОД ФОНОВОЙ ПЕРЕСБОРКИ (строка состояния): шагов сделано / всего по деталям текущего круга (шаг — операция;
    /// у детали ещё шаг сеток и проверки). Нет расчёта — (0, 0).
    /// </summary>
    public (int Done, int Total) BuildProgress
    {
        get
        {
            if (!_isBuilding) return (0, 0);
            int d = 0, t = 0;
            foreach (var p in _progressParts) { int pt = p.ProgressTotal; t += pt; d += Math.Min(p.ProgressDone, pt); }
            return (d, t);
        }
    }

    /// <summary>k122: расчёт одной детали идёт не с начала — с этой операции (иначе null).</summary>
    public Features.Feature? BuildFromFeature
    {
        get
        {
            if (!_isBuilding || _progressParts.Count != 1) return null;
            var p = _progressParts[0]; int i = p.ProgressFrom;
            return i > 0 && i < p.Features.Count && p.ProgressTotal > 0 ? p.Features[i] : null;
        }
    }

    /// <summary>Фоновая пересборка началась / закончилась (события — в главном потоке).</summary>
    public event Action? BuildStarted, BuildFinished;

    /// <summary>
    /// ПЕРЕСБОРКА В ФОНЕ.
    ///
    /// ДОБАВЛЕНО. Прежде пересборка шла в главном потоке — из подготовки кадра и из
    /// команд, — и тяжёлая операция замораживала окно на секунды. Теперь расчёт
    /// (Part.Compute) идёт в фоновой задаче, а результат применяется (Part.Apply)
    /// в главном потоке, куда возвращается продолжение. Если деталь успели изменить,
    /// пока шёл расчёт, её результат устарел и выбрасывается — и делается ещё круг.
    /// Повторный вызов во время расчёта присоединяется к нему.
    /// </summary>
    public Task<bool> RebuildDirtyAsync()
    {
        if (_building != null) return _building;

        // ИСПРАВЛЕНО. Прежде: _building ??= RunBuild(). Если грязных деталей нет,
        // RunBuild кончается без единого ожидания: его finally обнулял поле РАНЬШЕ,
        // чем в него записывалась уже завершённая задача, — и признак расчёта
        // оставался поднятым навсегда, запирая интерфейс. Теперь признак — свой
        // флаг внутри расчёта, а задача запоминается, только если ещё идёт.
        Task<bool> t = RunBuild();
        if (!t.IsCompleted) _building = t;
        return t;
    }

    /// <summary>u09: деталь, чья операция сейчас в живом предпросмотре (ручку тянут): она считается грубо и без проверок.</summary>
    public Part? PreviewPart { get; set; }
    /// <summary>k113: время последнего фонового расчёта (от запуска задачи до её конца, с ожиданием потока), мс.</summary>
    public double LastComputeMs { get; private set; }
    /// <summary>k113: из него — сам расчёт детали предпросмотра (Part.Compute), мс.</summary>
    public double LastPartComputeMs { get; private set; }
    /// <summary>k113: кругов расчёта в последней фоновой пересборке (больше одного — деталь менялась во время расчёта).</summary>
    public int LastRounds { get; private set; }
    /// <summary>k113: от конца расчёта в фоне до продолжения в потоке окна, мс (занятость потока окна).</summary>
    public double LastResumeMs { get; private set; }
    /// <summary>Во сколько раз грубее сетка живого предпросмотра.</summary>
    public double PreviewCoarseness { get; set; } = 4;

    // ================================================================ k113: уточнение мгновенного предпросмотра
    /// <summary>Мутация P3D_PREVIEWREFINE=0: мгновенный предпросмотр не уточняется (ошибки операции — только по «ОК»).</summary>
    public static readonly bool RefineOff = Environment.GetEnvironmentVariable("P3D_PREVIEWREFINE") == "0";
    /// <summary>Пауза после последней правки до уточнения (пока тянут ручку — не уточняется).</summary>
    public int RefineDelayMs { get; set; } = 250;
    private CancellationTokenSource? _refineCts;
    private long _computeEnd;
    private int _refineGen;
    /// <summary>Уточнение назначено (деталь помечена) — виду пора перестроиться.</summary>
    public event Action? RefineRequested;
    public static long RefinesStarted, RefinesCancelled, RefinesApplied;

    /// <summary>Правка во время уточнения: оно прерывается (следующий расчёт — снова мгновенный).</summary>
    public void CancelRefine()
    {
        _refineGen++;
        if (_refineCts is { IsCancellationRequested: false } c) { c.Cancel(); Interlocked.Increment(ref RefinesCancelled); }
    }

    /// <summary>Назначить уточнение через паузу (в потоке окна); правка за это время его отменяет.</summary>
    private void ScheduleRefine()
    {
        if (RefineOff || PreviewPart is not { } pp || !pp.ShowsInstant) return;
        var ctx = SynchronizationContext.Current;
        int gen = ++_refineGen; int ver = pp.Version;
        void Fire()
        {
            if (gen != _refineGen || !ReferenceEquals(PreviewPart, pp) || pp.Version != ver || pp.IsDirty || IsBuilding || !pp.ShowsInstant) return;
            if (pp.RequestRefine()) RefineRequested?.Invoke();
        }
        if (RefineDelayMs <= 0 || ctx == null) { ctx?.Post(_ => Fire(), null); if (ctx == null) Fire(); return; }
        _ = Task.Delay(RefineDelayMs).ContinueWith(_ => ctx.Post(_ => Fire(), null), TaskScheduler.Default);
    }

    /// <summary>Проверкам: уточнить сразу (без паузы).</summary>
    public bool RefineNowForTest() => PreviewPart is { } pp && pp.RequestRefine();

    private async Task<bool> RunBuild()
    {
        _isBuilding = true;
        BuildStarted?.Invoke();
        bool any = false;
        try
        {
            LastRounds = 0;
            for (int round = 0; round < 3; round++)
            {
                var dirty = AllParts().Where(p => p.IsDirty).ToList();
                if (dirty.Count == 0) break;
                LastRounds++;
                foreach (var p in dirty) p.ResetProgress();
                _progressParts = dirty;

                var solver = Solver;
                var preview = PreviewPart;
                var scopes = dirty.ToDictionary(p => p, ScopeFor);
                // k113: уточнение мгновенного предпросмотра — с отменой (правка во время него прерывает его)
                CancellationToken refineToken = CancellationToken.None;
                Part? refining = preview != null && dirty.Contains(preview) && preview.RefinePending ? preview : null;
                if (refining != null)
                {
                    refining.TakeRefine(); refining.SuppressInstant = true;
                    _refineCts = new CancellationTokenSource(); refineToken = _refineCts.Token;
                    Interlocked.Increment(ref RefinesStarted);
                }
                long computeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var results = await Task.Run(() => dirty.Select(p =>
                {
                    try
                    {
                        // u09: живой предпросмотр ручкой — грубая сетка, проверки отложены (окончательный расчёт — обычный)
                        if (ReferenceEquals(p, preview))
                        {
                            var ks = Kernel.KernelSettings.Capture();
                            ks = ks with { Preview = true, LivePreview = true, Chord = ks.Chord * PreviewCoarseness };   // угловой шаг не трогается: от него зависит восстановление дуг
                            long pc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            using (new Kernel.KernelContext(ks).Enter())
                            {
                                var rr = p.Compute(scopes[p], solver, ReferenceEquals(p, refining) ? refineToken : CancellationToken.None);
                                LastPartComputeMs = System.Diagnostics.Stopwatch.GetElapsedTime(pc0).TotalMilliseconds;
                                _computeEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                                return (Part: p, Result: rr, Failed: (Exception?)null);
                            }
                        }
                        // k126: ПРОВЕРКИ — ПОСЛЕ ПОКАЗА. Обычный расчёт идёт с отложенными проверками (как предпросмотр, но с полной
                        // точностью сетки): маршрут операции не ждёт разметки промежуточных тел и их проверки; деталь показывается,
                        // а проверки выполняет следующий шаг (VerifyPhase) — те же проверки на тех же телах; не принятое
                        // пересчитывается с этой операции без отложенных проверок. Мутация P3D_DEFERCHECKS=0 — прежнее.
                        if (!DeferChecksOff)
                        {
                            var ksd = Kernel.KernelSettings.Capture() with { Preview = true, LivePreview = false };
                            using (new Kernel.KernelContext(ksd).Enter()) return (Part: p, Result: p.Compute(scopes[p], solver), Failed: (Exception?)null);
                        }
                        return (Part: p, Result: p.Compute(scopes[p], solver), Failed: (Exception?)null);
                    }
                    catch (Exception ex) { return (Part: p, Result: (Part.BuildResult?)null, Failed: ex); }
                }).ToList());
                LastComputeMs = System.Diagnostics.Stopwatch.GetElapsedTime(computeStart).TotalMilliseconds;
                LastResumeMs = _computeEnd != 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(_computeEnd).TotalMilliseconds : double.NaN;
                if (refining != null) { refining.SuppressInstant = false; _refineCts?.Dispose(); _refineCts = null; }

                bool again = false;
                long applyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var (part, result, failed) in results)
                {
                    // k113: уточнение прервано правкой — не сбой: деталь осталась грязной, следующий круг посчитает её снова
                    if (failed != null && Kernel.KernelContext.IsCancellation(failed)) { again = true; continue; }
                    if (failed == null && ReferenceEquals(part, refining) && result != null && part.Apply(result)) { any = true; Interlocked.Increment(ref RefinesApplied); continue; }
                    if (failed != null)
                    {
                        // РАСЧЁТ УПАЛ ЦЕЛИКОМ — ПОВТОРЯТЬ ЕГО НЕЛЬЗЯ. Прежде он пересобирался в
                        // потоке окна: та же дорогая работа, тот же отказ, но теперь с замершим
                        // приложением на всё её время. Хуже того, на тяжёлой детали пользователь
                        // не видел ни причины, ни конца.
                        //
                        // Теперь отказ запоминается на детали и доходит до дерева оттуда, а
                        // последняя удачная модель остаётся на экране нетронутой.
                        part.LastFailure = failed.Message;

                        // Ошибка видна в дереве там же, где и ошибки отдельных операций.
                        part.Error = "Расчёт не выполнен: " + failed.Message;
                        any = true;
                    }
                    else if (part.Apply(result!)) any = true;
                    else again = true;               // деталь изменили во время расчёта
                }

                LastApplyMs = System.Diagnostics.Stopwatch.GetElapsedTime(applyStart).TotalMilliseconds;
                if (again) continue;
                // k126: деталь показана — проверки отложенных тел (правка во время них их прерывает, и расчёт идёт дальше)
                if (await VerifyPhase()) { any = true; continue; }
                break;
            }
        }
        finally
        {
            _isBuilding = false;
            _building = null;
            BuildFinished?.Invoke();
        }
        ScheduleRefine();
        return any;
    }

    /// <summary>k126: мутация P3D_DEFERCHECKS=0 — расчёт с проверками сразу (прежнее).</summary>
    public static readonly bool DeferChecksOff = Environment.GetEnvironmentVariable("P3D_DEFERCHECKS") == "0";
    /// <summary>k126: идёт проверка показанных деталей (строка состояния).</summary>
    public bool IsVerifying { get; private set; }
    /// <summary>k126: проверок начато / прервано правкой / закончено пересчётом (не принято) — разбор, проверки.</summary>
    public static long VerifiesStarted, VerifiesCancelled, VerifiesRejected;
    /// <summary>k126: время последней проверки (мс).</summary>
    public double LastVerifyMs { get; private set; }
    private CancellationTokenSource? _verifyCts;

    /// <summary>
    /// k126: ПРОВЕРКА ПОКАЗАННЫХ ДЕТАЛЕЙ. Обычный расчёт (без отложенных проверок) детали, тела которой ждут принятия: он
    /// сначала принимает их (те же проверки маршрута на тех же телах — Part.AcceptPreviewBodies) и, если всё принято,
    /// ничего не пересчитывает; не принятое — пересчёт с этой операции. Правка модели во время проверки её прерывает (отмена
    /// расчёта): деталь снова считается с отложенными проверками. Возвращает true, если что-то применено или деталь снова
    /// требует расчёта.
    /// </summary>
    private async Task<bool> VerifyPhase()
    {
        if (DeferChecksOff) return false;
        var parts = AllParts().Where(p => !p.IsDirty && p.NeedsAcceptance && !ReferenceEquals(p, PreviewPart)).ToList();
        if (parts.Count == 0) return false;
        var versions = parts.ToDictionary(p => p, p => p.Version);
        var scopes = parts.ToDictionary(p => p, ScopeFor);
        var solver = Solver;
        _verifyCts = new CancellationTokenSource(); var token = _verifyCts.Token;
        void OnChanged(ModelItem _) { if (parts.Any(p => p.Version != versions[p])) { try { _verifyCts?.Cancel(); } catch (ObjectDisposedException) { } } }
        Root.Changed += OnChanged;
        IsVerifying = true; Interlocked.Increment(ref VerifiesStarted);
        VerifyStarted?.Invoke();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool any = false;
        try
        {
            var results = await Task.Run(() => parts.Select(p =>
            {
                if (token.IsCancellationRequested) return (Part: p, Result: (Part.BuildResult?)null);
                try { return (Part: p, Result: (Part.BuildResult?)p.Compute(scopes[p], solver, token)); }
                catch (Exception ex) when (Kernel.KernelContext.IsCancellation(ex)) { return (Part: p, Result: (Part.BuildResult?)null); }
                catch (Exception) { return (Part: p, Result: (Part.BuildResult?)null); }
            }).ToList());
            foreach (var (p, r) in results)
            {
                if (r == null) { Interlocked.Increment(ref VerifiesCancelled); continue; }
                if (r.Executed > 0) Interlocked.Increment(ref VerifiesRejected);
                // всё принято и тела те же — применять нечего (вид не перестраивается); иначе — как обычный результат
                if (r.Executed == 0 && r.IntegrityError == null) continue;
                if (p.Apply(r)) any = true;
            }
        }
        finally
        {
            Root.Changed -= OnChanged;
            IsVerifying = false;
            _verifyCts.Dispose(); _verifyCts = null;
            LastVerifyMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            VerifyFinished?.Invoke();
        }
        return any || AllParts().Any(p => p.IsDirty);
    }
    /// <summary>k126: проверка показанных деталей началась / закончилась (события — в главном потоке).</summary>
    public event Action? VerifyStarted, VerifyFinished;

    /// <summary>
    /// Пересчитывает все «грязные» детали сразу, в текущем потоке. Во время фоновой
    /// пересборки ничего не делает: второй расчёт поверх первого — гонка в эскизах.
    /// </summary>
    public bool RebuildDirty()
    {
        if (IsBuilding) return false;
        bool any = false;
        foreach (var p in AllParts())
        {
            if (!p.IsDirty) continue;
            // 21.10: деталь в предпросмотре операции — тем же режимом, что и фоновый расчёт (грубая сетка, проверки отложены)
            if (ReferenceEquals(p, PreviewPart))
            {
                var ks = Kernel.KernelSettings.Capture();
                ks = ks with { Preview = true, LivePreview = true, Chord = ks.Chord * PreviewCoarseness };
                using (new Kernel.KernelContext(ks).Enter()) p.Rebuild(ScopeFor(p), Solver);
            }
            else p.Rebuild(ScopeFor(p), Solver);
            any = true;
        }
        return any;
    }

    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty;
            Root.AccumulateBounds(ref b, Matrix4.Identity);
            return b;
        }
    }

    /// <summary>Создаёт документ с одной пустой деталью — стартовое состояние приложения.</summary>
    public static Document NewSinglePart()
    {
        var doc = new Document("Part1");
        var part = doc.Root.AddPart("Part1");

        doc.Parameters.Add("Thickness", 10, ParamKind.Real, "mm");
        doc.Parameters.Add("Width", 60, ParamKind.Real, "mm");
        doc.Parameters.AddFormula("Diagonal", "Length(Width, Width)", "mm");

        // ТЕЛО ЕСТЬ СРАЗУ.
        //
        // Пустой документ заставляет перед каждой проверкой повторять одно
        // и то же: эскиз, квадрат, выдавливание. Руками квадрат выходит
        // каждый раз чуть другой, и результат операции сравнивать не с чем.
        //
        // Куб 100 симметричен относительно XY, поэтому грани стоят на ±50,
        // объём равен 1 000 000, и любое расхождение видно без счёта.
        // Убрать его — одна команда удаления в дереве.
        TestGeometry.AddCube(part, 100);

        return doc;
    }
}

/// <summary>Отдаёт формулам Length/Area/Volume реальные величины из модели.</summary>
public sealed class DocumentGeometryResolver : IGeometryResolver
{
    private readonly Document _doc;
    public DocumentGeometryResolver(Document doc) { _doc = doc; }

    public bool TryVolume(string reference, out double value)
    {
        var p = _doc.AllParts().FirstOrDefault(x =>
            string.Equals(x.Name, reference, StringComparison.OrdinalIgnoreCase));
        value = p?.Volume ?? 0;
        return p != null;
    }

    public bool TryArea(string reference, out double value)
    {
        var p = _doc.AllParts().FirstOrDefault(x =>
            string.Equals(x.Name, reference, StringComparison.OrdinalIgnoreCase));
        if (p != null) { value = p.Area; return true; }
        value = 0;
        return false;
    }

    public bool TryLength(string reference, out double value)
    {
        // Длина ребра / эскизной цепочки по имени. Расширяется по мере надобности.
        foreach (var part in _doc.AllParts())
        {
            foreach (var e in part.Bodies.SelectMany(b => b.Edges))
                if (string.Equals(e.Tag, reference, StringComparison.OrdinalIgnoreCase))
                { value = e.Curve.Length; return true; }
        }
        value = 0;
        return false;
    }
}
