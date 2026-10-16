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

    public ParameterTable ScopeFor(Part part)
    {
        var chain = new List<ParameterTable>();
        if (part.LocalParameters.Count > 0) chain.Add(part.LocalParameters);
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
    /// <summary>Во сколько раз грубее сетка живого предпросмотра.</summary>
    public double PreviewCoarseness { get; set; } = 4;

    private async Task<bool> RunBuild()
    {
        _isBuilding = true;
        BuildStarted?.Invoke();
        bool any = false;
        try
        {
            for (int round = 0; round < 3; round++)
            {
                var dirty = AllParts().Where(p => p.IsDirty).ToList();
                if (dirty.Count == 0) break;

                var solver = Solver;
                var preview = PreviewPart;
                var scopes = dirty.ToDictionary(p => p, ScopeFor);
                var results = await Task.Run(() => dirty.Select(p =>
                {
                    try
                    {
                        // u09: живой предпросмотр ручкой — грубая сетка, проверки отложены (окончательный расчёт — обычный)
                        if (ReferenceEquals(p, preview))
                        {
                            var ks = Kernel.KernelSettings.Capture();
                            ks = ks with { Preview = true, LivePreview = true, Chord = ks.Chord * PreviewCoarseness };   // угловой шаг не трогается: от него зависит восстановление дуг
                            using (new Kernel.KernelContext(ks).Enter())
                                return (Part: p, Result: p.Compute(scopes[p], solver), Failed: (Exception?)null);
                        }
                        return (Part: p, Result: p.Compute(scopes[p], solver), Failed: (Exception?)null);
                    }
                    catch (Exception ex) { return (Part: p, Result: (Part.BuildResult?)null, Failed: ex); }
                }).ToList());

                bool again = false;
                long applyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var (part, result, failed) in results)
                {
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
                if (!again) break;
            }
        }
        finally
        {
            _isBuilding = false;
            _building = null;
            BuildFinished?.Invoke();
        }
        return any;
    }

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
            p.Rebuild(ScopeFor(p), Solver);
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
