using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Features;
using Parametric3D.Model.Mates;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model;

/// <summary>
/// Деталь: дерево построения + результат его выполнения.
///
/// По умолчанию содержит начало координат и три базовые плоскости XY/XZ/YZ.
/// Порядок фич в Features — это и есть дерево построения; при любом изменении
/// деталь помечается «грязной» и пересчитывается лениво, один раз перед кадром.
/// </summary>
public sealed class Part : ModelItem
{
    public override ItemKind Kind => ItemKind.Part;

    public readonly DatumPoint Origin;
    public readonly DatumPlane PlaneXY, PlaneXZ, PlaneYZ;

    /// <summary>Упорядоченное дерево построения.</summary>
    public readonly List<Feature> Features = new();

    /// <summary>UI: параметры уровня детали (ищутся первыми; чего нет — выше: сборка, документ). Пишутся в файл детали.</summary>
    public readonly ParameterTable LocalParameters = new();

    public Material Material = Material.Steel();
    /// <summary>m.x4: система единиц документа детали — для ввода и показа; геометрия всегда в мм.</summary>
    public Core.UnitSystem Units = Core.UnitSystem.Metric;

    /// <summary>
    /// Сведения о документе: обозначение, наименование, автор, изменение. То, что попадает в
    /// основную надпись и в перечень; без них деталь в сборке из сорока штук не отличить.
    /// </summary>
    public DocumentInfo Info = new();

    /// <summary>
    /// МАТЕРИАЛ ОТДЕЛЬНОГО ТЕЛА. Деталь может состоять из нескольких тел, и они бывают из разных
    /// веществ: стальная ось в пластиковом корпусе, латунная втулка в алюминиевом рычаге. Масса
    /// каждого тела считается по ЕГО плотности, а не по общей плотности детали.
    ///
    /// Тело без собственного материала берёт материал детали — так поведение не меняется для
    /// тех, кому разные вещества не нужны.
    /// </summary>
    public readonly Dictionary<Kernel.Topology.TSolid, Material> BodyMaterials = new();

    /// <summary>
    /// Именованные наборы граней: то, на что вешают нагрузку и закрепление. Хранятся в детали,
    /// потому что переживают перестроение и должны попадать в документ.
    /// </summary>
    public readonly List<FaceSet> FaceSets = new();

    /// <summary>Материал тела: собственный, иначе материал детали.</summary>
    public Material MaterialOf(Kernel.Topology.TSolid body) =>
        BodyMaterials.TryGetValue(body, out var m) ? m : Material;

    /// <summary>Объём тела в мм³ — по его сетке.</summary>
    public static double VolumeOf(Kernel.Topology.TSolid body) =>
        Math.Abs(Kernel.Tess.MassProps.SignedVolume(Kernel.Tess.Tessellator.Tessellate(body)));

    /// <summary>Масса тела в килограммах, по плотности ЕГО вещества.</summary>
    public double MassOf(Kernel.Topology.TSolid body) =>
        VolumeOf(body) * 1e-9 * MaterialOf(body).Density;

    /// <summary>
    /// Масса детали как сумма масс тел. Прежде масса считалась по общему объёму и одной
    /// плотности; при телах из разных веществ это давало неверное число.
    /// </summary>
    public double MassByBodies()
    {
        double sum = 0;
        foreach (var b in Bodies) sum += MassOf(b);
        return sum;
    }

    /// <summary>Положение детали в сборке.</summary>
    public Vector3 Translation = Vector3.Zero;
    public Quaternion Rotation = Quaternion.Identity;
    /// <summary>u13: компонент сборки зафиксирован (решатель его не двигает).</summary>
    public bool Fixed;
    /// <summary>u13: скелетная модель сборки (опорная геометрия; не входит в спецификацию и массу).</summary>
    public bool Skeleton;
    /// <summary>u13: подсборка компонента (группа в дереве и спецификации); пусто — в корне.</summary>
    public string? SubAssembly;
    /// <summary>u13: путь файла, из которого вставлен компонент (замена, открытие).</summary>
    public string? SourcePath;

    /// <summary>
    /// Связи детали со сборкой. Первая ставит деталь на место, остальные проверяются на
    /// невязку: полного решателя нет, и молча «усреднять» противоречивые связи хуже, чем
    /// показать, какая из них не выполнена и на сколько.
    /// </summary>
    public readonly List<Mate> Mates = new();

    /// <summary>
    /// Ограничения сборки — неподвижные привязки. В отличие от соединений, они не оставляют
    /// степеней свободы, и деталь считается определённой, когда их сумма доходит до шести.
    /// </summary>
    public readonly List<Mates.AssemblyConstraint> Constraints = new();

    /// <summary>
    /// Исполнения: именованные наборы отличий. Хранится только отличие от основной модели —
    /// правка общей части сразу отражается во всех исполнениях.
    /// </summary>
    public readonly List<Configuration> Configurations = new();

    /// <summary>Имя действующего исполнения; пусто — основная модель.</summary>
    public string ActiveConfiguration = "";


    /// <summary>Сколько степеней свободы у детали осталось по её ограничениям (из шести).</summary>
    public int FreeDegrees => Math.Max(0, 6 - Constraints.Where(c => c.Enabled).Sum(c => c.Removes));

    /// <summary>Основная связь (первая в списке) — для прежнего кода и простых случаев.</summary>
    public Mate? AssemblyMate
    {
        get => Mates.Count > 0 ? Mates[0] : null;
        set
        {
            if (value == null) { Mates.Clear(); return; }
            if (Mates.Count == 0) Mates.Add(value); else Mates[0] = value;
        }
    }

    // --------------------------------------------------- результат построения

    public readonly List<TSolid> Bodies = new();
    /// <summary>u13h: имена тел последнего расчёта (тела FreeForm); у параметрической детали — «Тело N».</summary>
    public readonly List<string> BodyNameList = new();
    /// <summary>u13k: SubD-клетки тел последнего расчёта (по имени тела).</summary>
    public readonly Dictionary<string, SubDCage> CagesByBody = new();
    /// <summary>u13h: документ — FreeForm-тело (отдельный файл): тела не объединяются, окружение FreeForm открыто сразу.</summary>
    public bool FreeFormDoc;
    /// <summary>u13h: FreeForm-документ вставлен в сборку или деталь — пересекающиеся тела объединяются (решение 2026-10-13).</summary>
    public bool MergeFreeForm;
    /// <summary>u13h: узел FreeForm, открытый для правки (его тела не объединяются, пока идёт правка). Не сохраняется.</summary>
    public string? EditingFreeForm;
    /// <summary>u13h: плоскость построения FreeForm (заготовки и кривые ставятся на неё).</summary>
    public Frame3 ConstructionPlane = Frame3.XY;

    /// <summary>Имя тела, которому принадлежит грань, ребро или вершина (по последнему расчёту).</summary>
    public string? BodyNameOf(uint entityId)
    {
        for (int i = 0; i < Bodies.Count; i++)
        {
            var b = Bodies[i];
            if (b.Faces.Any(f => f.Id == entityId) || b.Edges.Any(e => e.Id == entityId) || b.Vertices.Any(v => v.Id == entityId))
                return i < BodyNameList.Count ? BodyNameList[i] : "Тело " + (i + 1);
        }
        return null;
    }
    public string BodyName(int index) => index < BodyNameList.Count ? BodyNameList[index] : "Тело " + (index + 1);
    /// <summary>u13: квилты (поверхности) последнего перестроения.</summary>
    public readonly List<Quilt> Quilts = new();

    /// <summary>
    /// До какой операции ведётся расчёт: число выполняемых операций
    /// с начала списка. -1 означает «все».
    ///
    /// В дереве это полоска между строками. Всё, что ниже неё, в расчёт
    /// не идёт и на виде не показывается — обычный приём, когда нужно
    /// вернуться к промежуточному состоянию модели, не удаляя работу.
    /// </summary>
    public int StageIndex = -1;

    /// <summary>Сколько операций выполняется при текущем положении полоски.</summary>
    public int EffectiveStage => StageIndex < 0 ? Features.Count : Math.Min(StageIndex, Features.Count);

    /// <summary>Идёт ли операция в расчёт при текущем положении полоски.</summary>
    public bool IsBeyondStage(Feature f)
    {
        int i = Features.IndexOf(f);
        return i >= 0 && i >= EffectiveStage;
    }

    /// <summary>
    /// Ближайшее ДОПУСТИМОЕ положение полоски не выше указанного.
    ///
    /// Полоску нельзя поставить так, чтобы последней рассчитанной оказалась
    /// подавленная операция: она не выполняется, и полоска стояла бы после
    /// того, чего в модели нет. Правило то же, что в Creo — локатор
    /// сдвигается перед подавленной операцией.
    /// </summary>
    public int ClampStage(int stage)
    {
        stage = Math.Clamp(stage, 0, Features.Count);
        while (stage > 0 && Features[stage - 1].Suppressed) stage--;
        return stage;
    }

    /// <summary>
    /// Выход из режима вставки: полоска возвращается в конец.
    /// </summary>
    /// <param name="resume">
    /// true — операции ниже полоски снова считаются;
    /// false — они помечаются подавленными и остаются вне модели.
    /// Ровно этот выбор предлагает Creo при отмене режима вставки.
    /// </param>
    public void EndInsertMode(bool resume)
    {
        if (!resume)
            for (int i = EffectiveStage; i < Features.Count; i++)
                Features[i].Suppressed = true;

        StageIndex = -1;
        SetDirty();
    }

    /// <summary>Активен ли режим вставки: полоска не в конце.</summary>
    public bool InInsertMode => StageIndex >= 0 && StageIndex < Features.Count;
    public MeshData? Mesh { get; private set; }
    public double Volume { get; private set; }
    BuildResult? _areaFrom;
    /// <summary>Площадь поверхности тел детали (21.10: по запросу — из результата последнего перестроения).</summary>
    public double Area => _areaFrom?.Area ?? 0;
    /// <summary>Площадь ещё не посчитана (чтение Area её посчитает — у ленты скругления это десятки мс).</summary>
    public bool AreaPending => _areaFrom?.AreaPending ?? false;
    /// <summary>21.10: быстрый приближённый предпросмотр операции (LivePreview): без точного объёма и аудита. P3D_FASTPREVIEW=0 — прежний.</summary>
    public static readonly bool FastPreview = Environment.GetEnvironmentVariable("P3D_FASTPREVIEW") != "0";
    public static long FastPreviewBodies, FastPreviewMeshSkips, PolyLazyMeshSkips;
    static readonly bool PolyLazyMeshOff = Environment.GetEnvironmentVariable("P3D_POLYLAZYMESH") == "0";
    static readonly bool LazyAreaOff = Environment.GetEnvironmentVariable("P3D_LAZYAREA") == "0";
    /// <summary>Тел, площадь которых отложена до запроса.</summary>
    public static long LazyAreas;
    /// <summary>g.04: граница ошибки объёма детали (мм³).</summary>
    public double VolumeError { get; private set; }
    public double Mass => Volume * 1e-9 * Material.Density;   // мм^3 -> м^3
    public Bounds3 Bounds { get; private set; } = Bounds3.Empty;
    public readonly List<string> Warnings = new();
    /// <summary>u13: операции последнего перестроения с не точными гранями (гранение) — значок «приближение» в дереве.</summary>
    public readonly Dictionary<Feature, (int Faces, int Edges)> Downgrades = new();

    private bool _dirty = true;
    public bool IsDirty => _dirty;

    /// <summary>
    /// Номер версии детали: растёт при каждой пометке «грязная». Фоновая пересборка
    /// запоминает его в начале и применяет результат, только если он не изменился.
    /// </summary>
    public int Version { get; private set; }

    private void SetDirty()
    {
        _dirty = true;
        Version++;
    }

    public Matrix4 LocalTransform =>
        Matrix4.CreateFromQuaternion(Rotation) * Matrix4.CreateTranslation(Translation);

    public Part(string name)
    {
        LocalParameters.External = n => Params.ModelRefs.Resolve(this, n);   // k102: пути к размерам
        Name = name;

        Origin = new DatumPoint("Origin", Vector3d.Zero);
        PlaneXY = new DatumPlane("XY", Frame3.XY);
        PlaneXZ = new DatumPlane("XZ", Frame3.XZ);
        PlaneYZ = new DatumPlane("YZ", Frame3.YZ);

        Add(Origin);
        Add(PlaneXY);
        Add(PlaneXZ);
        Add(PlaneYZ);

        // Любая правка операции опускает отметку пересчёта: приращение начнётся с неё.
        Changed += f =>
        {
            if (f is Feature feat) MarkDirtyFrom(feat);
            else if (f is Sketching.Sketch sk) SketchChanged(sk);
            else if (f is DatumPlane or DatumAxis or DatumPoint) DatumChanged((ModelItem)f);
            else if (_removingFeature == 0 || RemoveMissingOff) ForgetCheckpoints();   // удаление операции: отметку ставит RemoveFeature
        };
    }

    /// <summary>
    /// Пересчитать всё заново. Сохранённые состояния при этом забываются: раз причина правки
    /// неизвестна, доверять им нельзя.
    /// </summary>
    /// <summary>
    /// Чем кончился последний отказ расчёта, если он был. Пустое означает, что расчёт прошёл.
    /// Хранится здесь, потому что показать его должно дерево, а случиться он может в фоне.
    /// </summary>
    public string? LastFailure;

    public void Invalidate() { ForgetCheckpoints(); SetDirty(); }

    /// <summary>
    /// K06. Параметры документа изменились. Сохранённые состояния не сбрасываются: при расчёте
    /// значения сравниваются с прошлыми, и пересчёт начинается с первой операции, формулы которой
    /// (или формулы размеров её эскизов) ссылаются на изменившийся параметр.
    /// </summary>
    public void ParametersChanged() => SetDirty();

    /// <summary>K06. Изменилось только представление (материал, цвет): геометрия не пересчитывается.</summary>
    public void AppearanceChanged() => SetDirty();

    /// <summary>
    /// K06. Правка эскиза затрагивает только операции, которые его потребляют; эскиз, не
    /// используемый ни одной операцией, геометрию детали не меняет.
    /// </summary>
    /// <summary>
    /// K06. Датум-элемент изменился: пересчёт с первой операции, которая на него ссылается
    /// (номером, именем или через эскиз на этой плоскости). Прежде любая правка датума
    /// сбрасывала все сохранённые состояния детали.
    /// </summary>
    private void DatumChanged(ModelItem item)
    {
        int at = Features.FindIndex(f => Dependencies.DependsOnItem(f, this, item));
        if (at >= 0) DirtyFromIndex(at);
        SetDirty();
    }

    private void SketchChanged(Sketching.Sketch sk)
    {
        int at = Features.FindIndex(f => Dependencies.SketchesOf(f).Contains(sk.Id));
        if (at >= 0) DirtyFromIndex(at);
        SetDirty();
        // k110: ссылки других эскизов на этот — за ним
        if (_propagating == 0)
        {
            _propagating++;
            try { Sketching.SketchLinks.Propagate(this, sk); } finally { _propagating--; }
        }
    }
    private int _propagating;

    /// <summary>
    /// Добавляет операцию В ПОЛОЖЕНИЕ ПОЛОСКИ расчёта, а не в конец списка.
    ///
    /// В этом и смысл полоски: она не просто ограничивает расчёт, а задаёт
    /// МЕСТО ВСТАВКИ. Перетащил её выше, создал операцию — она встала
    /// в середину дерева, между уже готовыми. Иначе полоска остаётся
    /// смотровым приспособлением, а вставить операцию в середину нечем.
    ///
    /// Полоска после вставки сдвигается на одну позицию вниз, чтобы новая
    /// операция сразу попала в расчёт и её было видно.
    /// </summary>
    public T AddFeature<T>(T f) where T : Feature
    {
        int at = EffectiveStage;

        Features.Insert(at, f);
        InsertIntoChildren(f, at);

        // Полоска стоит на конце — она и остаётся на конце (-1),
        // иначе каждая новая операция требовала бы её подтягивать.
        if (StageIndex >= 0) StageIndex = at + 1;

        // Новая операция ставится в место `at`: всё до него уже посчитано и годится.
        DirtyFromIndex(at);
        SetDirty();
        return f;
    }

    static readonly bool ChildOrderOff = Environment.GetEnvironmentVariable("P3D_TREEORDER") == "0";

    /// <summary>
    /// k109: МЕСТО ЭСКИЗА В ДЕРЕВЕ — сколько операций стоит перед ним (эскиз, не ушедший в операцию, — отдельная строка
    /// дерева между операциями; расчёт от него не зависит).
    /// </summary>
    public int SketchSlot(Sketch sk)
    {
        int at = Children.IndexOf(sk);
        if (at < 0) return Features.Count;
        int n = 0;
        for (int i = 0; i < at; i++) if (Children[i] is Feature) n++;
        return n;
    }

    /// <summary>Поставить эскиз в дереве после slot операций (0 — перед первой).</summary>
    public void PlaceSketch(Sketch sk, int slot)
    {
        if (!Children.Remove(sk)) return;
        slot = Math.Clamp(slot, 0, Features.Count);
        int target;
        if (slot == 0) target = Features.Count > 0 && Children.IndexOf(Features[0]) is var f0 && f0 >= 0 ? f0 : Children.Count;
        else target = Children.IndexOf(Features[slot - 1]) is var pv && pv >= 0 ? pv + 1 : Children.Count;
        sk.Parent = this;
        Children.Insert(target, sk);
        NotifyChanged(sk);
    }

    /// <summary>
    /// Куда можно поставить эскиз: не выше операции, на грани или рёбра которой он опирается (основа, ссылки); ниже — куда
    /// угодно.
    /// </summary>
    public (int Lo, int Hi) SketchSlotRange(Sketch sk)
    {
        int lo = 0;
        var tags = new List<string>();
        if (sk.BaseFaceTag is { Length: > 0 } bt) tags.Add(bt);
        foreach (var e in sk.Entities) if (e.ReferenceSource is { Length: > 0 } rs) tags.Add(rs);
        for (int i = 0; i < Features.Count; i++)
        {
            string key = "#" + Features[i].Key + "/";
            if (tags.Any(t => t.Contains(key, StringComparison.Ordinal))) lo = Math.Max(lo, i + 1);
        }
        return (lo, Features.Count);
    }

    /// <summary>
    /// Вставляет операцию в список детей так, чтобы порядок в дереве
    /// совпадал с порядком расчёта.
    ///
    /// В Children лежат и базовые элементы, и эскизы, и операции, поэтому
    /// позиция считается по СОСЕДНЕЙ операции, а не по номеру в Features.
    /// </summary>
    private void InsertIntoChildren(Feature f, int featureIndex)
    {
        int target = Children.Count;

        if (featureIndex + 1 < Features.Count)
        {
            // Встаём прямо перед той операцией, что раньше была на этом месте.
            int i = Children.IndexOf(Features[featureIndex + 1]);
            if (i >= 0) target = i;
        }

        f.Parent = this;
        Children.Insert(target, f);
        NotifyChanged(f);
    }

    public void RemoveFeature(Feature f)
    {
        int at = Features.IndexOf(f);
        // операции нет в дереве — отметку пересчёта не трогать (номер −1 давал «с нулевой»)
        if (at < 0 && !RemoveMissingOff) { _removingFeature++; try { Remove(f); } finally { _removingFeature--; } return; }
        Features.Remove(f);
        // u-нагрузка: удаление из дерева детали извещало «изменилась сама деталь» — все сохранённые состояния забывались, и
        // «Отмена» новой операции (её удаление) пересчитывала всё дерево с нуля (деталь из 40 операций — ~30 с). Отметка
        // пересчёта — с места удалённой операции, ниже. Мутация P3D_REMOVEMISSING=0.
        _removingFeature++;
        try { Remove(f); } finally { _removingFeature--; }

        // Удаление сказывается на всём, что стояло после неё.
        DirtyFromIndex(Math.Max(0, at));
        SetDirty();
    }

    /// <summary>Переставляет фичу в дереве построения (drag&amp;drop в дереве проекта).</summary>
    public void MoveFeature(Feature f, int newIndex)
    {
        int old = Features.IndexOf(f);
        if (old < 0) return;

        int to = Math.Clamp(newIndex, 0, Features.Count - 1);

        Features.RemoveAt(old);
        Features.Insert(Math.Clamp(newIndex, 0, Features.Count), f);
        // k109: и в дереве (Children) — на новом месте (прежде дерево показывало старый порядок до перезагрузки)
        if (!ChildOrderOff && Children.Remove(f))
        {
            int ni = Features.IndexOf(f);
            int target = Children.Count;
            if (ni + 1 < Features.Count && Children.IndexOf(Features[ni + 1]) is var nx && nx >= 0) target = nx;
            else if (ni > 0 && Children.IndexOf(Features[ni - 1]) is var pv && pv >= 0) target = pv + 1;
            Children.Insert(target, f);
            NotifyChanged(f);
        }

        // Перестановка меняет всё начиная с меньшего из двух мест: там порядок уже другой.
        DirtyFromIndex(Math.Min(old, to));
        SetDirty();
    }

    public Sketch AddSketch(string name, Frame3 plane, uint basePlaneId = 0)
    {
        var s = new Sketch(name, plane) { BasePlaneId = basePlaneId };
        Add(s);
        SetDirty();
        return s;
    }

    public IEnumerable<Sketch> Sketches => Children.OfType<Sketch>();
    public IEnumerable<DatumPlane> Planes => Children.OfType<DatumPlane>();

    // ------------------------------------------------------------ перестроение

    /// <summary>
    /// Выполняет дерево построения. Фича, упавшая с ошибкой, помечается и
    /// пропускается — остальные продолжают строиться. Это принципиально:
    /// одна кривая фича не должна «гасить» всю деталь.
    /// </summary>
    /// <summary>
    /// Прячет эскизы, ушедшие в тела.
    ///
    /// Делается при каждой перестройке, а не только при создании операции:
    /// иначе эскизы, использованные ранее, остались бы видны, и открытый
    /// документ показывал бы чертёж поверх готового тела.
    ///
    /// Эскизы, видимость которых пользователь задал сам, не трогаются.
    /// </summary>
    private static HashSet<uint> FaceIds(IEnumerable<Kernel.Topology.TSolid> bodies)
    {
        var set = new HashSet<uint>();
        foreach (var b in bodies)
            foreach (var f in b.Faces) set.Add(f.Id);
        return set;
    }

    private void HideConsumedSketches()
    {
        foreach (var f in Features)
            foreach (uint id in f.ConsumedSketchIds())
                foreach (var sk in Sketches)
                    if (sk.Id == id && !sk.VisibleUserSet) sk.Visible = false;
    }

    /// <summary>
    /// Результат пересборки, собранный без изменения самой детали.
    /// </summary>
    /// <summary>KRN-08 (08.2): тело после операции дерева и карта происхождения его граней.</summary>
    public sealed class BodyProvenance
    {
        readonly Lazy<Kernel.Topology.Provenance?> _map;
        public TSolid Body { get; }
        public bool Unchanged { get; }
        public bool Inferred { get; }
        /// <summary>Карта происхождения (выведенная — по запросу: перестроение её не считает).</summary>
        public Kernel.Topology.Provenance? Map => _map.Value;
        public BodyProvenance(TSolid body, bool unchanged, Kernel.Topology.Provenance? map, IReadOnlyList<TSolid>? before = null)
        { Body = body; Unchanged = unchanged; _map = new(() => map); _edges = new(() => EdgesFor(before, body, unchanged)); }
        public BodyProvenance(TSolid body, Func<Kernel.Topology.Provenance?> infer, IReadOnlyList<TSolid> before)
        { Body = body; Inferred = true; _map = new(infer); _edges = new(() => EdgesFor(before, body, false)); }
        readonly Lazy<(List<Kernel.Topology.EdgeProvenance> Edges, List<Kernel.Topology.TEdge> Deleted)?> _edges;
        /// <summary>KRN-08 (08.2): происхождение рёбер (выведенное по кривым, по запросу); null — не вывести.</summary>
        public (List<Kernel.Topology.EdgeProvenance> Edges, List<Kernel.Topology.TEdge> Deleted)? Edges => _edges.Value;
        static (List<Kernel.Topology.EdgeProvenance>, List<Kernel.Topology.TEdge>)? EdgesFor(IReadOnlyList<TSolid>? before, TSolid body, bool unchanged)
        {
            if (before == null || unchanged) return null;
            try { return Kernel.Topology.ProvenanceInfer.EdgesOf(before, body); }
            catch (Exception ex) when (!Kernel.KernelContext.IsCancellation(ex)) { return null; }
        }
    }
    static readonly bool TreeProvOff = Environment.GetEnvironmentVariable("P3D_TREEPROV") == "0";
    static readonly bool ProvInferOff = Environment.GetEnvironmentVariable("P3D_PROVINFER") == "0";
    static BodyProvenance InferFor(IReadOnlyList<TSolid> before, TSolid after, string name)
    {
        var inputs = before.ToList();
        return new BodyProvenance(after, () => InferProvenance(inputs, after, name), inputs);
    }
    static Kernel.Topology.Provenance? InferProvenance(IReadOnlyList<TSolid> before, TSolid after, string name)
    {
        try { return Kernel.Topology.ProvenanceInfer.Of(before, after, name); }
        catch (Exception ex) when (!Kernel.KernelContext.IsCancellation(ex)) { return null; }
    }

    public sealed class BuildResult
    {
        public int Version;
        /// <summary>KRN-08 (08.6): полоска, политика и пределы, с которыми считался результат.</summary>
        internal (int Stage, Kernel.OperationPolicy Policy, Kernel.KernelLimits Limits)? Inputs;
        /// <summary>KRN-08 (08.6): таблица параметров и её значения на начало расчёта.</summary>
        internal ParameterTable? ParamTable;
        internal Dictionary<string, double>? ParamSnapshot;
        public readonly List<TSolid> Bodies = new();
        /// <summary>u13h: имена тел (FreeForm), по порядку Bodies.</summary>
        public readonly List<string> BodyNames = new();
        /// <summary>u13k: SubD-клетки тел (по имени тела).</summary>
        public readonly Dictionary<string, SubDCage> Cages = new();
        /// <summary>u13: квилты результата.</summary>
        public readonly List<Quilt> Quilts = new();
        /// <summary>Поставка 3: сколько граней получили сетку от донора (прежнего результата детали).</summary>
        public int MeshFacesReused;
        public readonly List<string> Warnings = new();
        public readonly Dictionary<Feature, string?> Errors = new();
        /// <summary>KRN-01 (ворота g.06): типизированный исход каждой выполненной операции — Success, Empty (тел не осталось),
        /// InvalidInput, Unsupported, AccuracyExceeded, InternalFailure; отмена всего перестроения — OperationCanceledException.</summary>
        public readonly Dictionary<Feature, Kernel.OperationOutcome> Outcomes = new();
        /// <summary>KRN-01 (01.11): затронутые сущности отказа операции — устойчивыми именами (грань — имя, ребро — имена граней).</summary>
        public readonly Dictionary<Feature, IReadOnlyList<string>> ErrorEntities = new();
        /// <summary>KRN-08: неоднозначные ссылки операций (исход Ambiguous) — кандидаты для выбора пользователем.</summary>
        public readonly Dictionary<Feature, AmbiguousReferenceException> Ambiguities = new();
        /// <summary>KRN-01 (ворота g.03): операции, после которых в новых телах есть не точные грани/рёбра (гранение) — сколько
        /// таких граней и рёбер. При AllowBoundedFaceting — видимое понижение (и предупреждение); при PreserveBRep — отказ.</summary>
        public readonly Dictionary<Feature, (int Faces, int Edges)> GeometryDowngrades = new();
        public readonly Dictionary<Feature, List<uint>> Created = new();
        /// <summary>UI: время выполнения каждой операции в этом расчёте (мс); операции до точки возобновления — не считались.</summary>
        public readonly Dictionary<Feature, double> FeatureMs = new();
        /// <summary>k113: операция, показанная мгновенно (по сеткам); −1 — нет.</summary>
        public int InstantAt = -1;
        /// <summary>KRN-08 (08.2): ПРОИСХОЖДЕНИЕ НА УРОВНЕ ДЕРЕВА — у каждой выполненной операции тела после неё: то же тело, что
        /// до (Unchanged), или новое с картой происхождения граней своего построителя (Map); построитель карты не пишет — карта
        /// выведена по несущим поверхностям (Inferred). Мутация P3D_TREEPROV=0.</summary>
        public readonly Dictionary<Feature, List<BodyProvenance>> Provenance = new();
        /// <summary>K05: что операция сделала с гранями по их устойчивым именам.</summary>
        public readonly Dictionary<Feature, FeatureHistory> History = new();
        /// <summary>Вспомогательная геометрия, построенная деревом (по именам).</summary>
        public readonly Dictionary<string, Frame3> Planes = new();
        public readonly Dictionary<string, (Vector3d At, Vector3d Dir)> Axes = new();
        public readonly Dictionary<string, Vector3d> Points = new();

        public MeshData? Mesh;
        public double Volume;
        // 21.10 (скорость): ПЛОЩАДЬ ДЕТАЛИ — ПО ЗАПРОСУ. Её смотрят свойства и выражения, а считалась она на каждом перестроении
        // (у ленты скругления — квадратура в два прохода, 60–130 мс). Части площади — числа или отложенные расчёты тел; сумма —
        // при первом чтении, один раз. Мутация P3D_LAZYAREA=0 — сразу.
        double _areaNow; List<Func<double>>? _areaLater; readonly object _areaLock = new();
        public double Area
        {
            get { lock (_areaLock) { if (_areaLater != null) { foreach (var f in _areaLater) _areaNow += f(); _areaLater = null; } return _areaNow; } }
            set { lock (_areaLock) { _areaNow = value; _areaLater = null; } }
        }
        public void AddArea(double a) { lock (_areaLock) _areaNow += a; }
        public void AddAreaLater(Func<double> f) { lock (_areaLock) (_areaLater ??= new()).Add(f); }
        public void ShareAreaFrom(BuildResult o) { lock (o._areaLock) lock (_areaLock) { _areaNow = o._areaNow; _areaLater = o._areaLater == null ? null : new List<Func<double>>(o._areaLater); } }
        public bool AreaPending { get { lock (_areaLock) return _areaLater != null; } }
        /// <summary>g.04: граница ошибки объёма (сумма по телам; ∞ — неизвестна).</summary>
        public double VolumeError;
        /// <summary>K08: объём или площадь хотя бы одного тела взяты по сетке (с ошибкой гранения).</summary>
        public bool MassFromMesh;

        /// <summary>
        /// K01/K02. ЦЕЛОСТНОСТЬ РЕЗУЛЬТАТА: единая проверка каждого тела и его сетки. Тело может
        /// быть верным, а сетка — нет (сферический вырез A06); объём и экспорт берутся из сетки,
        /// поэтому её вердикт идёт вместе с результатом, а не теряется в предупреждениях.
        /// </summary>
        public readonly List<Kernel.Topology.ValidationReport> Validation = new();
        /// <summary>g.13: проверка сетки тел отложена предпросмотром — выполняется при следующем обычном расчёте (тела и сетки те же).</summary>
        internal readonly List<(TSolid Body, MeshData Mesh)> DeferredValidation = new();
        /// <summary>Отложена ли проверка тел (результат предпросмотра, ещё не принятый обычным расчётом).</summary>
        public bool ValidationPending => DeferredValidation.Count > 0;

        /// <summary>Есть ли среди тел приближённые (выбранные явно приближённой политикой).</summary>
        public bool Approximate;

        /// <summary>Сводка нарушений целостности; null, если всё годно.</summary>
        public string? IntegrityError;
        public Bounds3 Bounds = Bounds3.Empty;

        /// <summary>Сколько операций действительно выполнено: остальные взяты из сохранённого.</summary>
        public int Executed;

        /// <summary>С какой операции начат пересчёт: ноль означает «с начала дерева».</summary>
        public int StartedAt;
    }

    /// <summary>
    /// СОХРАНЁННОЕ СОСТОЯНИЕ ПОСЛЕ ОПЕРАЦИИ. Дерево пересчитывается с начала при любой правке, и
    /// на детали из шести операций это шестикратная работа ради изменения одной последней.
    ///
    /// Здесь после каждой операции запоминается состояние построения. При следующем перестроении
    /// работа начинается с последнего сохранённого состояния ПЕРЕД изменённой операцией.
    ///
    /// Чтобы это было безопасно, сохранённое должно быть неизменяемым. Списки копируются, а тела
    /// операции не правят на месте — они создают новые; если эта привычка нарушится, проверка
    /// равенства полному пересчёту это поймает.
    /// </summary>
    /// <summary>
    /// u-нагрузка (2026-10-20): ОТМЕНА, ПОВТОР И «ОТМЕНА» ОПЕРАЦИИ БЕЗ ПЕРЕСЧЁТА ВСЕГО ДЕРЕВА. Восстановление идёт по снимку
    /// документа — новая деталь из файла, которая прежде перестраивалась с нуля: на детали из 40 операций «отмена» правки в
    /// конце дерева стоила ~25 с. Теперь новая деталь перенимает у текущей сохранённые состояния и итоги операций общей
    /// начальной части дерева: операции, совпадающие в обоих снимках побитово (запись в файле), при тех же параметрах, полоске,
    /// материале и эскизах, которыми они пользуются. Пересчёт — с первой различающейся. Возвращает число перенятых операций
    /// (−1 — ничего: другие настройки детали или параметры). Мутация P3D_ADOPT=0.
    /// </summary>
    public int AdoptBuildState(Part current, string currentJson, string freshJson)
    {
        // расчёт текущей детали идёт (фоновый предпросмотр) — её сохранённые состояния меняются: не перенимать
        if (AdoptOff || current._lastInputs == null || !UseCheckpoints || Volatile.Read(ref current._computing) > 0)
        {
            if (Environment.GetEnvironmentVariable("P3D_ADOPTDBG") == "1") Console.WriteLine($"ADOPT refused: off {AdoptOff}, inputs {current._lastInputs != null}, computing {current._computing}");
            return -1;
        }
        try
        {
            // номера эскизов, элементов эскизов и опорных объектов назначаются при каждой загрузке заново — сравнение по
            // порядку появления (номер → «#n»), а не по значению
            var ca = CanonIds(System.Text.Json.Nodes.JsonNode.Parse(currentJson)!.AsObject());
            var cb = CanonIds(System.Text.Json.Nodes.JsonNode.Parse(freshJson)!.AsObject());
            var pa = ca.Where(x => x.Key is not ("Features" or "Sketches" or "Info")).ToDictionary(x => x.Key, x => x.Value?.ToJsonString() ?? "null");
            var pb = cb.Where(x => x.Key is not ("Features" or "Sketches" or "Info")).ToDictionary(x => x.Key, x => x.Value?.ToJsonString() ?? "null");
            if (pa.Count != pb.Count || pa.Any(kv => !pb.TryGetValue(kv.Key, out var v) || v != kv.Value))
            {
                if (Environment.GetEnvironmentVariable("P3D_ADOPTDBG") == "1") Console.WriteLine($"ADOPT refused: document settings differ ({string.Join(",", pa.Where(kv => !pb.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key))})");
                return -1;
            }
            if (ca["Features"] is not System.Text.Json.Nodes.JsonArray fa || cb["Features"] is not System.Text.Json.Nodes.JsonArray fb) return -1;
            // эскизы, которые различаются (или есть только в одном снимке)
            static Dictionary<string, string> Sk(System.Text.Json.Nodes.JsonObject r)
            {
                var d = new Dictionary<string, string>();
                if (r["Sketches"] is System.Text.Json.Nodes.JsonArray ss)
                    foreach (var s in ss) if (s is System.Text.Json.Nodes.JsonObject so) d[so["Id"]?.ToJsonString() ?? "?"] = so.ToJsonString();
                return d;
            }
            var sa = Sk(ca); var sb = Sk(cb);
            var changed = sa.Keys.Union(sb.Keys).Where(k => !sa.TryGetValue(k, out var x) || !sb.TryGetValue(k, out var y) || x != y).ToList();
            var la = fa.Select(x => x?.ToJsonString() ?? "").ToList(); var lb = fb.Select(x => x?.ToJsonString() ?? "").ToList();
            int k = 0;
            while (k < la.Count && k < lb.Count && k < current.Features.Count && k < Features.Count && la[k] == lb[k]
                   && !changed.Any(id => la[k].Contains(id))
                   && current.Features[k].GetType() == Features[k].GetType())
                k++;
            if (Environment.GetEnvironmentVariable("P3D_ADOPTDBG") == "1")
            {
                Console.WriteLine($"ADOPT k {k} of {la.Count}/{lb.Count}; changed sketches {string.Join(",", changed)}");
                if (k < la.Count && k < lb.Count && la[k] != lb[k])
                {
                    int d = 0; while (d < la[k].Length && d < lb[k].Length && la[k][d] == lb[k][d]) d++;
                    Console.WriteLine($"  feature {k} differs at {d}: «{la[k].Substring(Math.Max(0, d - 60), Math.Min(120, la[k].Length - Math.Max(0, d - 60)))}» vs «{lb[k].Substring(Math.Max(0, d - 60), Math.Min(120, lb[k].Length - Math.Max(0, d - 60)))}»");
                }
                foreach (var id in changed.Take(2))
                    if (sa.TryGetValue(id, out var x) && sb.TryGetValue(id, out var y)) { int d = 0; while (d < x.Length && d < y.Length && x[d] == y[d]) d++; Console.WriteLine($"  sketch {id} differs at {d}: «{x.Substring(Math.Max(0, d - 80), Math.Min(160, x.Length - Math.Max(0, d - 80)))}» vs «{y.Substring(Math.Max(0, d - 80), Math.Min(160, y.Length - Math.Max(0, d - 80)))}»"); }
            }
            if (k == 0) return 0;
            // перенять: состояния после операций 0..k−1, итоги этих операций, входы прежнего расчёта
            var map = new Dictionary<Feature, Feature>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < k; i++) map[current.Features[i]] = Features[i];
            _checkpoints.Clear();
            _checkpoints.AddRange(current._checkpoints.Where(c => c.After < k));
            foreach (var (of, nf) in map)
            {
                if (current._records.TryGetValue(of, out var rec)) _records[nf] = rec;
                if (current._retried.Contains(of)) _retried.Add(nf);
            }
            _lastInputs = current._lastInputs; _lastParams = current._lastParams;
            _lastResult = null;
            _dirtyFrom = k;
            _donorKeep = current.Bodies.ToList();                      // доноры сетки неизменённых граней
            return k;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return -1; }
    }

    /// <summary>Номера объектов документа (свойство «Id») — порядковыми метками «#n»; ссылки на них (…Id, …Ids, A/B/C, Pts, Items) — теми же.</summary>
    static System.Text.Json.Nodes.JsonObject CanonIds(System.Text.Json.Nodes.JsonObject root)
    {
        var map = new Dictionary<string, string>();
        void Collect(System.Text.Json.Nodes.JsonNode? n)
        {
            if (n is System.Text.Json.Nodes.JsonObject o)
                foreach (var (k, v) in o)
                {
                    if (k == "Id" && v is System.Text.Json.Nodes.JsonValue jv && jv.ToJsonString() is var t && t != "0" && !map.ContainsKey(t)) map[t] = $"\"#{map.Count}\"";
                    Collect(v);
                }
            else if (n is System.Text.Json.Nodes.JsonArray a) foreach (var x in a) Collect(x);
        }
        Collect(root);
        static bool RefName(string k) => k == "Id" || k.EndsWith("Id") || k is "A" or "B" or "C";
        static bool RefList(string k) => k.EndsWith("Ids") || k is "Pts" or "Items";
        void Rewrite(System.Text.Json.Nodes.JsonNode? n)
        {
            if (n is System.Text.Json.Nodes.JsonObject o)
            {
                foreach (var k in o.Select(x => x.Key).ToList())
                {
                    var v = o[k];
                    if (RefName(k) && v is System.Text.Json.Nodes.JsonValue && map.TryGetValue(v.ToJsonString(), out var c)) o[k] = System.Text.Json.Nodes.JsonNode.Parse(c);
                    else if (RefList(k) && v is System.Text.Json.Nodes.JsonArray arr)
                    {
                        for (int i = 0; i < arr.Count; i++)
                            if (arr[i] is System.Text.Json.Nodes.JsonValue && map.TryGetValue(arr[i]!.ToJsonString(), out var c2)) arr[i] = System.Text.Json.Nodes.JsonNode.Parse(c2);
                    }
                    else Rewrite(v);
                }
            }
            else if (n is System.Text.Json.Nodes.JsonArray a) foreach (var x in a) Rewrite(x);
        }
        Rewrite(root);
        return root;
    }
    private int _computing;
    static readonly bool FeatTrace = Environment.GetEnvironmentVariable("P3D_FEATTRACE") == "1";
    static readonly bool BuildDbg = Environment.GetEnvironmentVariable("P3D_BUILDDBG") == "1";
    private int _removingFeature;
    internal static readonly bool RemoveMissingOff = Environment.GetEnvironmentVariable("P3D_REMOVEMISSING") == "0";
    /// <summary>Разбор: сколько сохранённых состояний держит деталь.</summary>
    public int CheckpointCount => _checkpoints.Count;
    internal static readonly bool AdoptOff = Environment.GetEnvironmentVariable("P3D_ADOPT") == "0";

    /// <summary>Итог выполнения операции (для операций до начала пересчёта).</summary>
    private sealed class FeatureRecord
    {
        public string? Error; public Kernel.OperationOutcome? Outcome; public List<uint> Created = new();
        public IReadOnlyList<string>? Entities; public AmbiguousReferenceException? Ambiguity; public (int, int)? Downgrade;
        public FeatureHistory? History; public double? Ms;
        public static FeatureRecord Of(Feature f, BuildResult r) => new()
        {
            Error = r.Errors.GetValueOrDefault(f), Outcome = r.Outcomes.TryGetValue(f, out var o) ? o : null,
            Created = r.Created.TryGetValue(f, out var c) ? new List<uint>(c) : new(), Entities = r.ErrorEntities.GetValueOrDefault(f),
            Ambiguity = r.Ambiguities.GetValueOrDefault(f), Downgrade = r.GeometryDowngrades.TryGetValue(f, out var d) ? d : null,
            History = r.History.GetValueOrDefault(f), Ms = r.FeatureMs.TryGetValue(f, out var ms) ? ms : null,   // происхождение держит тела — не хранится
        };
        public void CopyTo(Feature f, BuildResult r)
        {
            r.Errors[f] = Error; if (Outcome is { } o) r.Outcomes[f] = o; r.Created[f] = new List<uint>(Created);
            if (Entities != null) r.ErrorEntities[f] = Entities; if (Ambiguity != null) r.Ambiguities[f] = Ambiguity;
            if (Downgrade is { } d) r.GeometryDowngrades[f] = d; if (History != null) r.History[f] = History;
            if (Ms is { } ms) r.FeatureMs[f] = ms;
        }
    }
    private readonly Dictionary<Feature, FeatureRecord> _records = new(ReferenceEqualityComparer.Instance);
    internal static readonly bool KeepRecordsOff = Environment.GetEnvironmentVariable("P3D_KEEPRECORDS") == "0";

    private sealed class Checkpoint
    {
        public int After;                       // номер операции, после которой снято
        public Features.BuildContext.Snapshot State = null!;
    }

    /// <summary>
    /// Пользоваться ли сохранёнными состояниями. Выключается переключателем P3D_INCR=0: нужно,
    /// чтобы сравнить приращение с полным пересчётом на той же детали.
    /// </summary>
    private static bool _gUseCheckpoints = Environment.GetEnvironmentVariable("P3D_INCR") != "0";
    /// <summary>Поставка 2: значение снимка модели в контексте (ModelSettings.UseCheckpoints), если он есть; иначе общее по умолчанию.</summary>
    public static bool UseCheckpoints
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.UseCheckpoints : _gUseCheckpoints;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gUseCheckpoints, value, "Part.UseCheckpoints");
    }

    private readonly List<Checkpoint> _checkpoints = new();

    /// <summary>С какой операции пересчитывать: правка операции опускает эту отметку.</summary>
    private int _dirtyFrom = int.MaxValue;

    /// <summary>Забыть сохранённое: нужно, когда поменялось то, что не привязано к операции.</summary>
    public void ForgetCheckpoints()
    {
        if (BuildDbg) Console.WriteLine($"BUILD {Name}: forget checkpoints < {string.Join(" < ", Environment.StackTrace.Split('\n').Skip(2).Take(6).Select(l => l.Trim()))}");
        _checkpoints.Clear();
        _dirtyFrom = 0;
    }

    /// <summary>Пометить, что операция изменилась: пересчёт пойдёт с неё.</summary>
    /// <summary>k113: на экране — мгновенный предпросмотр по сеткам (последняя операция ещё не посчитана точно).</summary>
    public bool ShowsInstant { get; private set; }
    /// <summary>k113: номер операции, показанной мгновенно (с неё уточняется).</summary>
    public int InstantFeatureIndex { get; private set; } = -1;
    static readonly bool InstantRollbackOff = Environment.GetEnvironmentVariable("P3D_INSTANTROLLBACK") == "0";
    public static long InstantRollbacks;
    /// <summary>k113: следующий расчёт — уточнение мгновенного предпросмотра (без сеток, с проверками операции).</summary>
    public bool RefinePending { get; private set; }
    internal bool SuppressInstant;

    /// <summary>k113: уточнить мгновенный предпросмотр — последняя операция считается обычным предпросмотром.</summary>
    public bool RequestRefine()
    {
        if (!ShowsInstant) return false;
        int k = InstantFeatureIndex >= 0 && InstantFeatureIndex < Features.Count ? InstantFeatureIndex : Features.Count - 1;
        while (k >= 0 && Features[k].Suppressed) k--;
        if (k < 0) return false;
        RefinePending = true;
        DirtyFromIndex(k);
        SetDirty();
        return true;
    }
    internal void TakeRefine() => RefinePending = false;

    public void MarkDirtyFrom(Feature f)
    {
        int at = Features.IndexOf(f);
        if (at < 0) { ForgetCheckpoints(); return; }

        DirtyFromIndex(at);
        SetDirty();
    }

    // u09: ЖИВОЙ ПРЕДПРОСМОТР РУЧКОЙ. Пока тянут, операция считается в режиме предпросмотра (грубая сетка, проверки
    // отложены); доноры сетки для окончательного расчёта — тела, опубликованные ДО начала перетаскивания: грубые грани
    // предпросмотра не должны стать сеткой результата.
    private List<Kernel.Topology.TSolid>? _donorKeep;

    /// <summary>Начало живого предпросмотра: запомнить опубликованные тела как доноров сетки окончательного расчёта.</summary>
    public void BeginPreviewSession() { _donorKeep ??= Bodies.ToList(); }

    /// <summary>
    /// Конец живого предпросмотра операции f: результат и сохранённые состояния с неё — предпросмотровые (грубые) —
    /// забываются, пересчёт с f обычным расчётом; доноры сетки — тела до перетаскивания.
    /// </summary>
    public void EndPreviewSession(Feature? f)
    {
        int i = f == null ? -1 : Features.IndexOf(f);
        if (i >= 0)
        {
            _checkpoints.RemoveAll(c => c.After >= i);
            _lastResult = null;
            DirtyFromIndex(i);
            SetDirty();
        }
    }

    /// <summary>Опустить отметку пересчёта до названного места.</summary>
    private void DirtyFromIndex(int at)
    {
        if (BuildDbg && at <= 0) Console.WriteLine($"BUILD {Name}: dirty from {at} < {string.Join(" < ", Environment.StackTrace.Split('\n').Skip(2).Take(6).Select(l => l.Trim()))}");
        _dirtyFrom = Math.Min(_dirtyFrom, Math.Max(0, at));
    }

    /// <summary>Пересборка сразу, в текущем потоке: вычислить и применить.</summary>
    public void Rebuild(ParameterTable? parameters, ISketchSolver? solver = null)
        => Apply(Compute(parameters, solver));

    /// <summary>
    /// ВЫЧИСЛЕНИЕ ПЕРЕСБОРКИ — без изменения полей детали.
    ///
    /// Можно звать из фонового потока: тела, предупреждения, ошибки и созданные
    /// грани элементов, сетка и меры складываются в результат, а деталь остаётся
    /// прежней, пока интерфейс её рисует. Элементы по-прежнему решают свои эскизы
    /// (решатель пишет координаты в эскиз), поэтому на время фоновой пересборки
    /// правка модели из интерфейса запрещена (см. Document.IsBuilding).
    /// </summary>
    public BuildResult Compute(ParameterTable? parameters, ISketchSolver? solver = null)
        => Compute(parameters, solver, CancellationToken.None);

    /// <summary>
    /// K04. Пределы ресурсов расчёта этой детали (время, размер точного тела). По умолчанию нет.
    /// </summary>
    public Kernel.KernelLimits Limits = Kernel.KernelLimits.None;

    /// <summary>
    /// Вычисление с токеном отмены. Отмена бросает OperationCanceledException наружу и не
    /// оставляет следов: поля детали не меняются, сохранённые состояния остаются годными, а
    /// отметка пересчёта возвращается — следующий расчёт начнёт с того же места.
    /// </summary>
    public BuildResult Compute(ParameterTable? parameters, ISketchSolver? solver, CancellationToken cancellation)
    {
        if (RebuildLog) Console.WriteLine($"REBUILD {Name} features {Features.Count} dirty {IsDirty} {string.Join(" < ", Environment.StackTrace.Split('\n').Skip(2).Take(7).Select(l => l.Trim()))}");
        int dirtyAtStart = _dirtyFrom;
        Interlocked.Increment(ref _computing);
        // KRN-08 (08.6): с чем считался результат, кроме самих операций, — для проверки при применении.
        var inputs = (EffectiveStage, Policy, Limits);
        var snap = Dependencies.Snapshot(parameters);
        try
        {
            var r = ComputeCore(parameters, solver, cancellation);
            r.Inputs = inputs; r.ParamTable = parameters; r.ParamSnapshot = snap;
            return r;
        }
        catch (Exception ex)
        {
            // Любой обрыв расчёта (отмена, предел, внутренняя ошибка вне операций): отметка
            // пересчёта возвращается, а прежний результат больше не годится для выдачи «без
            // изменений» — следующий расчёт обязан выполнить работу.
            _dirtyFrom = Math.Min(_dirtyFrom, dirtyAtStart);
            _lastResult = null;
            if (!Kernel.KernelContext.IsCancellation(ex)) throw;
            var inner = Kernel.KernelContext.Unwrap(ex);
            if (ReferenceEquals(inner, ex)) throw;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        finally { Interlocked.Decrement(ref _computing); Kernel.Diagnostics.ThreadState.Release(); }
    }

    // K06: то, от чего зависел последний расчёт, кроме самих операций.
    private Dictionary<string, double>? _lastParams;
    private (Kernel.KernelSettings Settings, Kernel.OperationPolicy Policy, Kernel.KernelLimits Limits, int Stage)? _lastInputs;
    private BuildResult? _lastResult;

    /// <summary>
    /// K06. Начало пересчёта по входам вне дерева: настройки ядра, политика, полоска и значения
    /// параметров. Возвращает true, если пересчитывать нечего (полный no-op).
    /// </summary>
    private bool ApplyOutsideChanges(ParameterTable? parameters, Kernel.KernelSettings settings)
    {
        if (_lastInputs is { } last)
        {
            // u09: точность сетки предпросмотра (грубее обычной) — не «изменились настройки»: пересчёт всего дерева не нужен,
            // операция предпросмотра потом пересчитывается обычным расчётом (EndPreviewSession)
            static Kernel.KernelSettings Norm(Kernel.KernelSettings k, bool anyPreview) =>
                anyPreview ? k with { Preview = false, LivePreview = false, Chord = 0, ChordAngle = 0 } : k with { Preview = false };
            bool anyPv = last.Settings.Preview || settings.Preview || last.Settings.LivePreview || settings.LivePreview;
            if (!Norm(last.Settings, anyPv).Equals(Norm(settings, anyPv)) || last.Policy != Policy || !last.Limits.Equals(Limits))
            {
                if (BuildDbg) Console.WriteLine($"BUILD {Name}: outside change — settings {!Norm(last.Settings, anyPv).Equals(Norm(settings, anyPv))} ({last.Settings} → {settings}), policy {last.Policy != Policy}, limits {!last.Limits.Equals(Limits)}");
                _dirtyFrom = 0;                               // настройки ядра или политика изменились
            }
            else if (last.Stage != EffectiveStage)
                DirtyFromIndex(Math.Min(last.Stage, EffectiveStage));   // полоска сдвинулась
        }
        else { if (BuildDbg) Console.WriteLine($"BUILD {Name}: no previous inputs"); _dirtyFrom = 0; }

        var now = Dependencies.Snapshot(parameters);
        if (_lastParams != null)
        {
            var changed = Dependencies.Changed(_lastParams, now);
            if (changed.Count > 0)
            {
                var table = parameters ?? new ParameterTable();
                for (int i = 0; i < Features.Count; i++)
                    if (Dependencies.DependsOn(Features[i], this, changed, table)) { DirtyFromIndex(i); break; }
            }
        }

        return _lastResult != null && _dirtyFrom == int.MaxValue && UseCheckpoints;
    }

    static readonly bool PreviewPartOff = Environment.GetEnvironmentVariable("P3D_PREVIEWPART") == "0";
    internal static readonly bool PreviewValidateOff = Environment.GetEnvironmentVariable("P3D_PREVIEWVALIDATE") == "0";

    static void ValidationIntegrity(BuildResult result)
    {
        var bad = result.Validation.Where(v => !v.IsValid).ToList();
        if (bad.Count > 0)
            result.IntegrityError = (result.IntegrityError == null ? "" : result.IntegrityError + " ") + $"Integrity: {bad.Count} body(ies) failed validation — " +
                                    string.Join(" | ", bad.Select(v => v.Summary()));
    }

    /// <summary>g.13: отложенная предпросмотром проверка тел результата — те же проверки на тех же телах и сетках.</summary>
    static void RunDeferredValidation(BuildResult r)
    {
        if (r.DeferredValidation.Count == 0) return;
        foreach (var (b, md) in r.DeferredValidation)
            r.Validation.Add(Kernel.Topology.SolidValidator.Validate(b, md, new Kernel.Topology.ValidationOptions { CheckExact = false }));
        r.DeferredValidation.Clear();
        ValidationIntegrity(r);
    }

    /// <summary>Принять тела предпросмотра в сохранённых состояниях и прежнем результате; не принятое — пересчёт с его места.</summary>
    private void AcceptPreviewBodies()
    {
        foreach (var c in _checkpoints.OrderBy(c => c.After))
            foreach (var b in c.State.Bodies)
                if (b.IsPreview && Kernel.Ops.BooleanOp.AcceptPreview(b) != null) { DirtyFromIndex(c.After); return; }
        if (_lastResult != null)
            foreach (var b in _lastResult.Bodies)
                if (b.IsPreview && Kernel.Ops.BooleanOp.AcceptPreview(b) != null)
                {
                    // место неизвестно — с первой операции после последнего принятого состояния
                    DirtyFromIndex(_checkpoints.Count > 0 ? _checkpoints.Max(c => c.After) : 0); return;
                }
    }

    private BuildResult ComputeCore(ParameterTable? parameters, ISketchSolver? solver, CancellationToken cancellation)
    {
        var settingsNow = Kernel.KernelSettings.Capture();
        // KRN-10 (g.13): ПРЕДПРОСМОТР НЕ МЕНЯЕТ РЕЗУЛЬТАТ — только откладывает проверки. Обычный расчёт сначала принимает
        // тела предпросмотра, оставшиеся в сохранённых состояниях и прежнем результате (те же проверки на тех же телах);
        // не принятое — пересчёт с этой операции без предпросмотра. Мутация P3D_PREVIEWPART=0 — без принятия.
        if (!settingsNow.Preview && !PreviewPartOff) AcceptPreviewBodies();
        if (ApplyOutsideChanges(parameters, settingsNow))
        {
            // g.13: результат предпросмотра принимается — его отложенная проверка тел выполняется сейчас; найденный сбой
            // целостности — как у обычного расчёта (следующий расчёт начнётся с конца).
            if (!settingsNow.Preview && _lastResult!.ValidationPending)
            {
                RunDeferredValidation(_lastResult);
                if (_lastResult.IntegrityError != null && RetryAfterFailure)
                {
                    var failed = _lastResult; _lastResult = null; DirtyFromIndex(Features.Count - 1);
                    return failed;
                }
            }
            // НИЧЕГО НЕ ИЗМЕНИЛОСЬ: прежний результат выдаётся без единой геометрической операции.
            // Прежде такое перестроение шло по ветке «отметки нет» и пересчитывало всё дерево.
            var last = _lastResult!;
            var same = new BuildResult { Version = Version, StartedAt = Features.Count, Executed = 0,
                                         Mesh = last.Mesh, Volume = last.Volume, VolumeError = last.VolumeError, Bounds = last.Bounds,
                                         Approximate = last.Approximate, IntegrityError = last.IntegrityError, MassFromMesh = last.MassFromMesh };
            same.ShareAreaFrom(last);
            same.InstantAt = last.InstantAt;
            same.Bodies.AddRange(last.Bodies); same.Warnings.AddRange(last.Warnings);
            foreach (var kv in last.Errors) same.Errors[kv.Key] = kv.Value;
            foreach (var kv in last.Outcomes) same.Outcomes[kv.Key] = kv.Value;
            foreach (var kv in last.ErrorEntities) same.ErrorEntities[kv.Key] = kv.Value;
            foreach (var kv in last.Ambiguities) same.Ambiguities[kv.Key] = kv.Value;
            foreach (var kv in last.GeometryDowngrades) same.GeometryDowngrades[kv.Key] = kv.Value;
            foreach (var kv in last.Created) same.Created[kv.Key] = kv.Value;
            foreach (var kv in last.History) same.History[kv.Key] = kv.Value;
            foreach (var kv in last.Planes) same.Planes[kv.Key] = kv.Value;
            foreach (var kv in last.Axes) same.Axes[kv.Key] = kv.Value;
            foreach (var kv in last.Points) same.Points[kv.Key] = kv.Value;
            same.Validation.AddRange(last.Validation);
            return same;
        }
        var computed = ComputeFull(parameters, solver, cancellation, settingsNow);
        _lastParams = Dependencies.Snapshot(parameters);
        _lastInputs = (settingsNow, Policy, Limits, EffectiveStage);
        _lastResult = computed;
        // KRN-01 (ворота g.10): РЕЗУЛЬТАТ С ВНУТРЕННИМ СБОЕМ НЕ ОКОНЧАТЕЛЕН. Сбой операции (исключение, не отказ с типом) или
        // целостности (грань не размечена) — не свойство модели, а происшествие расчёта: прежде такой результат запоминался как
        // «ничего не изменилось», и следующий расчёт без правок выдавал тот же сбой, не пробуя снова (перебор прерываний:
        // 28 случаев из 404). Теперь следующий расчёт начинается с первой сбойной операции (с конца, если сбой — в разметке).
        // P3D_RETRYFAIL=0 — прежнее.
        // u-нагрузка (2026-10-20): ПОВТОР — ОДИН РАЗ. Сбой, повторившийся при повторе (та же операция, тот же ввод — отказ
        // детерминирован), больше не повторяется при каждой следующей правке: прежде каждая правка в конце дерева пересчитывала
        // всё от первой сбойной операции — на долгой сессии правки (кнопка «Тест», раздел «Нагрузка») время правки росло с
        // 0.2 до 17 с за 24 правки. Операция, выполненная без сбоя, из списка повторённых выходит. Мутация P3D_RETRYONCE=0.
        if (RetryAfterFailure)
        {
            foreach (var f in Features)
                if (computed.Outcomes.TryGetValue(f, out var o0) && o0 != Kernel.OperationOutcome.InternalFailure) _retried.Remove(f);
            _retried.RemoveWhere(f => !Features.Contains(f));
            int firstFail = -1;
            for (int i = 0; i < Features.Count; i++)
                if (computed.Outcomes.TryGetValue(Features[i], out var o) && o == Kernel.OperationOutcome.InternalFailure)
                {
                    if (!RetryOnceOff && i < computed.StartedAt && !KeepRecordsOff) continue;                       // не выполнялась — итог прежнего расчёта
                    if (!RetryOnceOff && _retried.Contains(Features[i])) continue;                                   // уже повторена — сбой постоянный
                    firstFail = i; break;
                }
            if (firstFail >= 0 || computed.IntegrityError != null)
            {
                _lastResult = null;
                // все сбойные этого расчёта повторяются одним следующим расчётом (с первой) — не по одной на правку
                if (firstFail >= 0)
                    for (int i = firstFail; i < Features.Count; i++)
                        if (computed.Outcomes.TryGetValue(Features[i], out var o2) && o2 == Kernel.OperationOutcome.InternalFailure) _retried.Add(Features[i]);
                DirtyFromIndex(firstFail >= 0 ? firstFail : Features.Count - 1);
            }
        }
        return computed;
    }
    /// <summary>KRN-09 (09.6): поддерживаемый размер модели, мм (координаты результатов по модулю).</summary>
    public const double ModelRange = 1e7;
    internal static readonly bool RangeOff = Environment.GetEnvironmentVariable("P3D_MODELRANGE") == "0";
    internal static readonly bool StaleInputsOff = Environment.GetEnvironmentVariable("P3D_STALEINPUTS") == "0";
    internal static readonly bool GeometryGuard = Environment.GetEnvironmentVariable("P3D_GEOMGUARD") != "0";
    internal static readonly bool RetryAfterFailure = Environment.GetEnvironmentVariable("P3D_RETRYFAIL") != "0";
    internal static readonly bool RetryOnceOff = Environment.GetEnvironmentVariable("P3D_RETRYONCE") == "0";
    /// <summary>Операции, сбой которых уже повторялся (повтор — один раз).</summary>
    private readonly HashSet<Feature> _retried = new(ReferenceEqualityComparer.Instance);

    private BuildResult ComputeFull(ParameterTable? parameters, ISketchSolver? solver, CancellationToken cancellation,
                                    Kernel.KernelSettings settingsNow)
    {
        LastFailure = null;
        Error = null;

        var result = new BuildResult { Version = Version };

        var ctx = new BuildContext
        {
            Part = this,
            Parameters = parameters,
            Solver = solver ?? new NumericSketchSolver()
        };

        int stage = EffectiveStage;

        // ОТКЛАДЫВАНИЕ ДЕЙСТВУЕТ ТОЛЬКО НА ВРЕМЯ ОБХОДА ДЕРЕВА и снимается ДО окончательной
        // разметки — иначе она тоже отложится, и тело останется без точной сетки. На этом я
        // споткнулся: настройка, заданная на весь метод, пропускала разметку целиком, и отказ
        // всплыл в чужой проверке скругления.
        // K04: КОНТЕКСТ РАСЧЁТА. Настройки снимаются один раз в начале: правка общих значений из
        // интерфейса посреди фонового расчёта на него не действует, а два расчёта разных деталей
        // не видят настроек друг друга. Политика, пределы и токен отмены — тоже его часть.
        // Вызванный внутри чужого контекста расчёт наследует его настройки, отмену и наблюдателя
        // этапов; политика и пределы — свои, детали.
        // Поставка 3: прежний опубликованный результат — доноры сетки неизменённых граней.
        // u09: после живого предпросмотра — доноры до перетаскивания (грубые грани предпросмотра в результат не идут)
        var keep = _donorKeep;
        bool previewNow = Kernel.KernelSettings.Capture().Preview;
        var previousBodies = !previewNow && keep != null ? keep : Bodies.ToList();
        if (!previewNow && keep != null) _donorKeep = null;
        var outer = Kernel.KernelContext.Current;
        var context = new Kernel.KernelContext(Kernel.KernelSettings.Capture(), Policy,
                                               Limits == Kernel.KernelLimits.None && outer != null ? outer.Limits : Limits,
                                               cancellation.CanBeCanceled ? cancellation : outer?.Cancellation ?? default)
                      { StageHook = outer?.StageHook };
        // Поставка 2: снимок настроек модели — тот же, что у внешнего расчёта, или снятый сейчас.
        context = context.WithExtension(ModelSettings.Capture());
        using var contextScope = context.Enter();
        var options = new Kernel.KernelOptions { DeferMeshing = true }.Apply();
        // K02: политика операций — свойство детали, а не общий переключатель; действует только
        // в логическом потоке этого расчёта.
        using var policy = Kernel.OperationPolicyScope.Use(Policy);

        // НАЧАЛО ПЕРЕСЧЁТА: последнее сохранённое состояние перед изменённой операцией. Операции
        // до него уже выполнены, и их результат лежит в сохранённом; повторять их незачем.
        int from = 0;
        int dirtyNote = _dirtyFrom;

        if (UseCheckpoints && _dirtyFrom < int.MaxValue)
        {
            var take = _checkpoints.LastOrDefault(c => c.After < _dirtyFrom);
            if (take != null)
            {
                ctx.RollBack(take.State);
                from = take.After + 1;
            }
        }
        else _checkpoints.Clear();

        result.StartedAt = from;
        if (BuildDbg) Console.WriteLine($"BUILD {Name}: from {from} of {Features.Count} (dirty {dirtyNote}), preview {previewNow}, checkpoints {_checkpoints.Count}");
        _checkpoints.RemoveAll(c => c.After >= from);

        // ОТМЕТКА СБРАСЫВАЕТСЯ СРАЗУ. Иначе она остаётся опущенной навсегда: первая же правка
        // ставит её в ноль, и следующая правка берёт наименьшее из нуля и своего номера — то
        // есть снова ноль. Приращение в таком виде не работает вовсе, что и показала проверка:
        // при изменении последней операции выполнялись все четыре.
        _dirtyFrom = int.MaxValue;

        bool poisoned = false;
        int lastActive = -1;
        for (int k = Math.Min(stage, Features.Count) - 1; k >= 0; k--) if (!Features[k].Suppressed) { lastActive = k; break; }
        // k113: ПРАВКА ОПЕРАЦИИ В СЕРЕДИНЕ ДЕРЕВА — предпросмотр «на её месте» (как «правка определения» в Creo): деталь до неё и
        // она сама по сеткам, операции после — до уточнения (пауза после правки) не пересчитываются. P3D_INSTANTROLLBACK=0.
        int instantAt = lastActive;
        if (settingsNow.LivePreview && !SuppressInstant && !InstantRollbackOff && dirtyNote >= from && dirtyNote < lastActive && dirtyNote < Features.Count
            && !Features[dirtyNote].Suppressed && Features[dirtyNote].InstantPreviewSafe && Features[dirtyNote].FreeForm == null)
            instantAt = dirtyNote;
        try
        {
        for (int fi = 0; fi < Features.Count; fi++)
        {
            var f = Features[fi];
            result.Errors[f] = null;
            result.Created[f] = new List<uint>();

            // ИТОГ ОПЕРАЦИИ ДО НАЧАЛА ПЕРЕСЧЁТА — ИЗ ЕЁ ПРЕЖНЕГО РАСЧЁТА. Прежде у операций до сохранённого состояния, с
            // которого начат пересчёт, ошибка, исход, созданные грани, история и происхождение обнулялись: правка в конце
            // дерева «чинила» сбойную операцию выше (её ошибка пропадала из дерева), пока та не выполнялась снова. Мутация
            // P3D_KEEPRECORDS=0.
            if (fi < from)
            {
                if (!KeepRecordsOff && _records.TryGetValue(f, out var rec)) rec.CopyTo(f, result);
                continue;
            }
            if (fi >= stage) continue;
            if (f.Suppressed) continue;

            Kernel.KernelContext.Checkpoint("feature");
            if (FeatTrace) Console.WriteLine($"FEAT {fi} {f.GetType().Name} {f.Name}");

            var before = FaceIds(ctx.Bodies);

            // ОПЕРАЦИЯ ЛИБО ПРОХОДИТ ЦЕЛИКОМ, ЛИБО НЕ МЕНЯЕТ НИЧЕГО. Иначе бывало так: отверстие
            // с цекованием сперва вырезало основное отверстие, затем проверяло диаметр головной
            // части и отказывалось — а тело уже уменьшилось с 48 000 до 47 774 мм³, и следующие
            // операции шли по испорченной заготовке.
            var _pSave = Kernel.Diagnostics.Prof.Time("дерево: снимок до операции");
            var saved = ctx.Remember();
            _pSave.Dispose();
            // Поставка 2: НЕИЗМЕННАЯ ПУБЛИКАЦИЯ. Тела до операции (на них ссылаются сохранённые состояния и
            // прежний результат на экране) замораживаются: правка их формы на месте — исключение, а не
            // обнаружение отпечатком задним числом (VerifyPublishedBodies остаётся вторым рубежом для того,
            // что заморозка не видит: списков граней и рёбер).
            foreach (var b in saved.Bodies) b.Publish();

            // K03: опубликованные тела (они же в сохранённых состояниях) не правятся на месте.
            // Проверка отпечатком дорогая и включается явно — в тестах и при разборе.
            List<string>? guard = VerifyPublishedBodies
                ? saved.Bodies.Select(Kernel.Topology.SolidFingerprint.Authoritative).ToList() : null;

            try
            {
                // K02: своя политика операции (например, «разрешить приближённое» только для одного
                // трудного скругления), иначе — политика детали.
                // g.04 (01.12): операция — с гранением CAD (ToleranceContext.FacetChord), а не с хордой отображения
                using (f.Policy is { } own ? Kernel.OperationPolicyScope.Use(own) : null)
                using (Kernel.Ops.BooleanOp.FacetingScope())
                using (Kernel.Diagnostics.Prof.Time("дерево: операция " + f.GetType().Name))
                {
                    // UI (кнопка «Тест»): время каждой операции последнего расчёта
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        // u13h: шаг FreeForm — на своём теле; конец узла FreeForm в параметрической детали — пересекающиеся тела объединяются
                        // k113: последняя операция дерева в предпросмотре — мгновенно по сеткам (её тело дальше не идёт)
                        Kernel.Ops.InstantPreview.Allowed = settingsNow.LivePreview && !SuppressInstant && f.FreeForm == null && fi == instantAt && f.InstantPreviewSafe;
                        Kernel.Ops.InstantPreview.Target = ctx.Bodies.Count == 1 ? ctx.Bodies[0] : null;
                        try { if (f.FreeForm != null) FreeFormExec.Run(f, ctx); else f.Execute(ctx); }
                        finally { Kernel.Ops.InstantPreview.Allowed = false; Kernel.Ops.InstantPreview.Target = null; }
                        if (f.FreeForm != null && (!FreeFormDoc || MergeFreeForm) && EditingFreeForm != f.FreeForm
                            && (fi + 1 >= Math.Min(stage, Features.Count) || Features[fi + 1].FreeForm != f.FreeForm))
                            FreeFormExec.AutoUnion(ctx);
                    }
                    finally { result.FeatureMs[f] = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
                }
                CheckRange();
                CheckGuard();
                if (!TreeProvOff)
                    result.Provenance[f] = ctx.Bodies.Select(b => saved.Bodies.Any(x => ReferenceEquals(x, b))
                        ? new BodyProvenance(b, true, null)
                        : b.Provenance is { } own || ProvInferOff ? new BodyProvenance(b, false, b.Provenance, ProvInferOff ? null : saved.Bodies.ToList())
                        : InferFor(saved.Bodies, b, f.Name)).ToList();
            }
            catch (RebuildException ex)
            {
                if (FeatStat) Console.Error.WriteLine($"FEATERR rebuild {f.GetType().Name} :: {ex.Message[..Math.Min(140, ex.Message.Length)]}");
                result.Errors[f] = ex.Message;
                result.Outcomes[f] = ex.Outcome;
                var ents = ex.Entities.Count > 0 ? ex.Entities
                    : ex is Kernel.OperationFailedException ofe0 ? ofe0.Result.Diagnostics.SelectMany(d => d.Entities ?? Array.Empty<string>()).Distinct().ToList() : null;
                if (ents is { Count: > 0 }) result.ErrorEntities[f] = ents;
                if (ex is AmbiguousReferenceException amb) result.Ambiguities[f] = amb;
                else if (ex is Kernel.OperationFailedException ofe && ofe.Result.Cause is AmbiguousReferenceException amb2) result.Ambiguities[f] = amb2;
                ctx.RollBack(saved);
            }
            catch (Exception ex) when (!Kernel.KernelContext.IsCancellation(ex))
            {
                if (FeatStat) Console.Error.WriteLine($"FEATERR {ex.GetType().Name} {f.GetType().Name} :: {ex.Message[..Math.Min(140, ex.Message.Length)]}");
                result.Errors[f] = "Internal error: " + ex.Message;
                result.Outcomes[f] = Kernel.OperationOutcome.InternalFailure;
                ctx.RollBack(saved);
            }

            if (guard != null && result.Errors[f] != null && !poisoned)
            {
                // Отказ тоже не должен оставить следов в опубликованных телах.
                try { CheckGuard(); }
                catch (RebuildException ex) { result.Errors[f] += " " + ex.Message; }
            }

            // KRN-09 (09.6): ПРЕДЕЛ РАЗМЕРА МОДЕЛИ. Значение из файла (или ввода) вроде глубины 1e300 давало «успешное» тело с
            // координатами 1e300, на котором дальше отказывали булевы с невнятной причиной. Результат операции за пределами
            // ±ModelRange мм — отказ этой операции с названным пределом. Мутация P3D_MODELRANGE=0.
            void CheckRange()
            {
                if (RangeOff) return;
                foreach (var b in ctx.Bodies)
                {
                    if (saved.Bodies.Any(x => ReferenceEquals(x, b))) continue;
                    double m = 0;
                    foreach (var v in b.Vertices) m = Math.Max(m, Math.Max(Math.Abs(v.P.X), Math.Max(Math.Abs(v.P.Y), Math.Abs(v.P.Z))));
                    if (!(m <= ModelRange))
                        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput,
                            $"{f.Name}: the result extends to {m:G3} mm — outside the supported model range ±{ModelRange:G3} mm.");
                }
            }

            void CheckGuard()
            {
                if (guard == null) return;
                for (int i = 0; i < saved.Bodies.Count; i++)
                    if (Kernel.Topology.SolidFingerprint.Authoritative(saved.Bodies[i]) != guard[i])
                    {
                        // Откат списка тут не поможет: испорчен сам общий объект. Сохранённым
                        // состояниям больше верить нельзя — следующее перестроение пойдёт с нуля.
                        poisoned = true;
                        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InternalFailure, $"Internal error: operation modified published body {i} in place.");
                    }
            }

            // K05: новые тела операции приводятся к соглашению о полурёбрах, а грани, которые
            // операция породила (подпись без ключа операции), получают её постоянный ключ.
            var _pConv = Kernel.Diagnostics.Prof.Time("дерево: соглашение и имена");
            foreach (var b in ctx.Bodies)
            {
                if (saved.Bodies.Any(x => ReferenceEquals(x, b))) continue;
                if (b.TopologyConvention < Kernel.Topology.TopologyConventions.Version)
                    Kernel.Topology.TopologyConventions.Normalize(b);
                if (ShellStat) ShellStatOf(b, f);
                foreach (var face in b.Faces)
                    if (face.Tag.Length == 0 || face.Tag[0] != '#')
                    {
                        face.Tag = $"#{f.Key}/{(face.Tag.Length == 0 ? "face" : face.Tag)}";
                        Kernel.Ops.BooleanOp.RenameSourceTag(face);
                    }
            }
            // KRN-03 (03.5, 03.6): ИНВАРИАНТ ОБОЛОЧЕК ПОСЛЕ ЛЮБОЙ ОПЕРАЦИИ. Новое тело с несколькими компонентами в одной
            // оболочке или с полостью не на своём месте приводится тем же разбором, что у булевой (раздельные куски — разные
            // тела, полость — внутренняя оболочка своего тела); не приводится — операция отказывает, вход не меняется.
            _pConv.Dispose();
            var _pInv = Kernel.Diagnostics.Prof.Time("дерево: инвариант оболочек");
            if (EnforceShellInvariant && result.Errors[f] == null)
                for (int bi = ctx.Bodies.Count - 1; bi >= 0; bi--)
                {
                    var b = ctx.Bodies[bi];
                    if (saved.Bodies.Any(x => ReferenceEquals(x, b))) continue;
                    // k94: тело предпросмотра по сеткам (две перекрывающиеся оболочки заполнителя) — не делится; расчёт по «ОК» точный
                    if (Kernel.Ops.BooleanOp.IsPreviewMesh(b)) continue;
                    var why = Kernel.Topology.ShellComponents.Invariant(b);
                    if (why == null) continue;
                    var sp = Kernel.Topology.ShellComponents.Split(b);
                    var still = sp.Bodies.Select(Kernel.Topology.ShellComponents.Invariant).FirstOrDefault(x => x != null);
                    if (sp.Warning != null || still != null)
                    {
                        result.Errors[f] = $"Internal error: the result violates the shell invariant ({why}; {sp.Warning ?? still})";
                        result.Outcomes[f] = Kernel.OperationOutcome.InternalFailure;
                        ctx.RollBack(saved);
                        break;
                    }
                    ctx.Bodies.RemoveAt(bi);
                    ctx.Bodies.InsertRange(bi, sp.Bodies);
                    // u13h: куски тела FreeForm — с именем тела («Тело 1», «Тело 1.2»…)
                    if (ctx.BodyNames.TryGetValue(b, out var bn))
                        for (int k = 0; k < sp.Bodies.Count; k++) ctx.BodyNames[sp.Bodies[k]] = k == 0 ? bn : $"{bn}.{k + 1}";
                    ctx.Warnings.Add($"Shells normalized after {f.GetType().Name}: {why} → {sp.Bodies.Count} bod{(sp.Bodies.Count == 1 ? "y" : "ies")}, {sp.Cavities} cavit{(sp.Cavities == 1 ? "y" : "ies")}.");
                }
            // KRN-01 (ворота g.03): ГЕОМЕТРИЯ НОВЫХ ТЕЛ — ПО САМОЙ B-rep, А НЕ ПО ЯРЛЫКАМ. Прежде политика PreserveBRep соблюдалась
            // только потому, что операции шли через булеву; операция со своим построителем или со своим контекстом политики
            // проходила бы с гранёным результатом. Теперь при PreserveBRep гранёное новое тело — отказ (Unsupported), при
            // AllowBoundedFaceting — видимое понижение (GeometryDowngrades и предупреждение). P3D_GEOMGUARD=0 — прежнее.
            _pInv.Dispose();
            var _pAud = Kernel.Diagnostics.Prof.Time("дерево: аудит геометрии");
            // 21.10: быстрый предпросмотр операции — аудит геометрии новых тел при подтверждении (точный расчёт), не на каждом шаге
            if (GeometryGuard && result.Errors[f] == null && !(FastPreview && Kernel.KernelSettings.Capture().LivePreview))
            {
                int nf = 0, ne = 0;
                foreach (var b in ctx.Bodies)
                {
                    if (saved.Bodies.Any(x => ReferenceEquals(x, b))) continue;
                    var (bf, be) = Kernel.Topology.GeometryAudit.Faceted(b);
                    nf += bf; ne += be;
                }
                if (nf + ne > 0)
                {
                    if (Kernel.KernelSettings.Capture().GeometryPolicy == Kernel.GeometryPolicy.PreserveBRep)
                    {
                        result.Errors[f] = $"{f.GetType().Name} refused (geometry-downgrade-refused): geometry policy PreserveBRep, the result has {nf} faceted face(s) and {ne} faceted edge(s).";
                        result.Outcomes[f] = Kernel.OperationOutcome.Unsupported;
                        if (!Kernel.OperationContract.DiagEntitiesOff)
                            result.ErrorEntities[f] = ctx.Bodies.Where(b => !saved.Bodies.Any(x => ReferenceEquals(x, b))).SelectMany(Kernel.Topology.EntityNames.Faceted).Distinct().ToList();
                        ctx.RollBack(saved);
                    }
                    else
                    {
                        result.GeometryDowngrades[f] = (nf, ne);
                        ctx.Warnings.Add($"Geometry downgrade after {f.GetType().Name}: {nf} faceted face(s), {ne} faceted edge(s) (policy AllowBoundedFaceting).");
                    }
                }
            }
            if (result.Errors[f] == null)
                result.Outcomes[f] = ctx.Bodies.Count == 0 && saved.Bodies.Count > 0 ? Kernel.OperationOutcome.Empty : Kernel.OperationOutcome.Success;
            _pAud.Dispose();
            using (Kernel.Diagnostics.Prof.Time("дерево: история"))
            if (result.Errors[f] == null && !ctx.Bodies.Any(Kernel.Ops.InstantPreview.IsCarrier)) result.History[f] = FeatureHistory.Between(saved.Bodies, ctx.Bodies);

            foreach (uint id in FaceIds(ctx.Bodies))
                if (!before.Contains(id)) result.Created[f].Add(id);

            result.Executed++;

            _records[f] = FeatureRecord.Of(f, result);
            // Состояние после операции запоминается: следующая правка начнёт отсюда. k113: состояние мгновенного предпросмотра
            // (носитель сетки) — нет: с него нельзя продолжать.
            bool instantHere = ctx.Bodies.Any(Kernel.Ops.InstantPreview.IsCarrier);
            if (UseCheckpoints && !instantHere)
                using (Kernel.Diagnostics.Prof.Time("дерево: снимок после операции"))
                _checkpoints.Add(new Checkpoint { After = fi, State = ctx.Remember() });
            if (instantHere)
            {
                result.InstantAt = fi;
                if (fi < lastActive)
                {
                    // операции после — как в прежнем расчёте (ошибки, созданные грани), сами не выполняются
                    for (int r = fi + 1; r < Features.Count; r++)
                    {
                        var g = Features[r];
                        result.Errors[g] = null; result.Created[g] = new List<uint>();
                        if (_records.TryGetValue(g, out var grec)) grec.CopyTo(g, result);
                    }
                    Interlocked.Increment(ref InstantRollbacks);
                    break;
                }
            }
        }
        }
        finally { options.Dispose(); }

        if (poisoned)
        {
            ForgetCheckpoints();
            result.IntegrityError = "Integrity: an operation modified a published body in place; saved states were discarded, rebuild from scratch.";
        }

        result.Bodies.AddRange(ctx.Bodies);
        result.BodyNames.AddRange(ctx.Bodies.Select(ctx.NameOf));
        foreach (var kv in ctx.Cages) if (result.BodyNames.Contains(kv.Key)) result.Cages[kv.Key] = kv.Value;
        result.Quilts.AddRange(ctx.Quilts);
        result.Warnings.AddRange(ctx.Warnings);

        // ТОЧНАЯ РАЗМЕТКА — ОДИН РАЗ, В КОНЦЕ. Во время перестроения она отложена: промежуточные
        // тела на экран не идут, а размечать их заново на каждой операции дороже всего
        // остального вместе (измерено на настоящей детали: 5.2 с из 8.0 с).
        Kernel.KernelContext.Checkpoint("final mesh");
        using (Kernel.Diagnostics.Prof.Time("дерево: итоговая разметка"))
        if (Kernel.Ops.BooleanOp.ExactMeshing)
        {
            // Поставка 3: доноры сетки — грани прежнего опубликованного результата этой детали (снимок при входе).
            using var donors = Kernel.Ops.BooleanOp.UseMeshDonors(previousBodies);
            foreach (var b in result.Bodies)
            {
                // 21.10: быстрый предпросмотр — тело, все грани которого уже размечены построителем (сетка по контурам), идёт на
                // экран с этой сеткой; окончательная согласованная разметка — при подтверждении операции
                if (FastPreview && settingsNow.LivePreview && b.Faces.All(fc => fc.Surface is Kernel.Surfaces.FacetedSurface fsx && fsx.Facets.Count > 0)) { Interlocked.Increment(ref FastPreviewMeshSkips); continue; }
                // 21.10 (скорость): тело с авторитетной многогранной формой показывается многоугольниками самой формы (Tessellator,
                // источник 2); видимая сетка граней нужна, только если у тех дефекты — тогда её строит сам показ. Тор ∪ брусок с
                // касанием: 0.45–0.57 с разметки, результат которой не показывался. Мутация P3D_POLYLAZYMESH=0.
                if (!PolyLazyMeshOff && b.Exact != null && b.ExactAuthoritative) { Interlocked.Increment(ref PolyLazyMeshSkips); continue; }
                Kernel.Ops.BooleanOp.MeshExactlySolid(b, result.Warnings);
            }
            result.MeshFacesReused = donors.Reused;
        }

        // Поставка 2: результат публикуется после окончательной разметки (сетка — производное, её можно
        // достроить и у опубликованного тела, форму — нельзя).
        foreach (var b in result.Bodies) b.Publish();

        if (result.Bodies.Count > 0)
        {
            var all = new List<MeshData>();
            foreach (var b in result.Bodies)
            {
                MeshData md;
                using (Kernel.Diagnostics.Prof.Time("деталь: сетка тела")) md = Tessellator.Tessellate(b);
                all.Add(md);
                // K08: объём и площадь — по настоящим кривым рёбер, когда все грани тела
                // аналитические (плоскость, цилиндр, сфера); иначе — по сетке, и это помечается.
                // 21.10: быстрый предпросмотр — объём по сетке (точный — при подтверждении операции). Мутация P3D_FASTPREVIEW=0.
                if (FastPreview && settingsNow.LivePreview)
                {
                    result.Volume += MassProps.Volume(md); result.AddArea(MassProps.Area(md)); result.MassFromMesh = true;
                    result.Bounds.Add(md.Bounds);
                    if (ValidateResults && !Kernel.Ops.InstantPreview.IsCarrier(b)) result.DeferredValidation.Add((b, md));
                    Interlocked.Increment(ref FastPreviewBodies);
                    continue;
                }
                var _pVol = Kernel.Diagnostics.Prof.Time("деталь: объём");
                (double Volume, double QuadratureError)? ve;
                using (Kernel.Diagnostics.Prof.Time("  деталь: объём по формулам")) ve = Kernel.Topology.AnalyticVolume.OfWithError(b);
                double? av = ve?.Volume, aa = av == null || !LazyAreaOff ? null : Kernel.Topology.AnalyticVolume.AreaOf(b);
                if (av != null && (aa != null || !LazyAreaOff))
                {
                    result.Volume += av.Value;
                    if (aa != null) result.AddArea(aa.Value);
                    else { var bb = b; var mdd = md; result.AddAreaLater(() => Kernel.Topology.AnalyticVolume.AreaOf(bb) ?? MassProps.Area(mdd)); Interlocked.Increment(ref LazyAreas); }
                    // g.04: граница ошибки объёма (как AnalyticMeasure.Volume: допуски сущностей × площадь + измеренная квадратура +
                    // округление) — сверяется с бюджетом интегрирования
                    // граница — та же, что у AnalyticMeasure.Volume (у многогранного тела — с расхождением интеграла и многогранника)
                    double err = Kernel.Topology.AnalyticMeasure.Volume(b, md).Error;
                    result.VolumeError += err;
                    if (err > Kernel.ToleranceContext.Current.IntegrationRelative * Math.Abs(av.Value))
                        result.Warnings.Add($"Volume accuracy: ±{err:G3} mm³ exceeds the integration budget ({Kernel.ToleranceContext.Current.IntegrationRelative:G2} relative).");
                }
                else if (b.Exact != null && b.ExactAuthoritative)
                {
                    using (Kernel.Diagnostics.Prof.Time("  деталь: граница ошибки объёма")) result.VolumeError += Kernel.Topology.AnalyticMeasure.Volume(b, md).Error;
                    // g.02: многогранное тело, чей интеграл не согласен с точной многогранной формой, — объём этой формы
                    // (рациональная арифметика), площадь — по сетке
                    using (Kernel.Diagnostics.Prof.Time("  деталь: объём точной формы")) result.Volume += Kernel.Exact.ExactAdapter.ApproxVolume(b.Exact.Solid) ?? b.Exact.Solid.VolumeMm3().ToDouble();
                    result.AddArea(MassProps.Area(md)); result.MassFromMesh = true;
                }
                else { result.Volume += MassProps.Volume(md); result.AddArea(MassProps.Area(md)); result.MassFromMesh = true; result.VolumeError += Kernel.Topology.AnalyticMeasure.Volume(b, md).Error; }
                _pVol.Dispose();
                result.Bounds.Add(md.Bounds);
                if (ValidateResults)
                using (Kernel.Diagnostics.Prof.Time("деталь: проверка тела"))
                {
                    // g.13: в предпросмотре проверка сетки тела откладывается до принятия (P3D_PREVIEWVALIDATE=0 — сразу)
                    if (settingsNow.Preview && !PreviewValidateOff) result.DeferredValidation.Add((b, md));
                    else result.Validation.Add(Kernel.Topology.SolidValidator.Validate(b, md,
                        new Kernel.Topology.ValidationOptions { CheckExact = false }));
                }
                if (b.Guarantee == Kernel.GuaranteeLevel.Approximate) result.Approximate = true;
            }
            foreach (var q in result.Quilts) if (QuiltMesh(q) is { } qm) all.Add(qm);
            result.Mesh = all.Count == 1 ? all[0] : Merge(all);

            using (Kernel.Diagnostics.Prof.Time("деталь: целостность")) ValidationIntegrity(result);
            if (result.Mesh != null && !result.Mesh.Whole)
                result.IntegrityError ??= $"Integrity: {result.Mesh.BrokenFaces.Count} face(s) missing from the mesh.";
        }

        // u13: деталь только из квилтов — сетка из них
        if (result.Bodies.Count == 0 && result.Quilts.Count > 0)
        {
            var qs = result.Quilts.Select(QuiltMesh).OfType<MeshData>().ToList();
            if (qs.Count > 0) { result.Mesh = qs.Count == 1 ? qs[0] : Merge(qs); foreach (var q in qs) result.Bounds.Add(q.Bounds); }
        }

        // Вспомогательная геометрия дерева отдаётся вместе с телами: на неё ссылаются
        // следующие операции и оконная часть.
        foreach (var kv in ctx.Planes) result.Planes[kv.Key] = kv.Value;
        foreach (var kv in ctx.Axes) result.Axes[kv.Key] = kv.Value;
        foreach (var kv in ctx.Points) result.Points[kv.Key] = kv.Value;

        return result;
    }

    /// <summary>
    /// ПРИМЕНЕНИЕ ПЕРЕСБОРКИ — в главном потоке. Возвращает false, если результат
    /// устарел: деталь успели изменить, пока шёл расчёт (тогда она остаётся грязной
    /// и будет пересобрана заново).
    /// </summary>
    /// <summary>
    /// K02. ПОЛИТИКА ОПЕРАЦИЙ ДЕТАЛИ. Строгая по умолчанию: подмена точного маршрута приближённым
    /// останавливает операцию с объяснением. Приближённая выбирается явно.
    /// </summary>
    public Kernel.OperationPolicy Policy = Kernel.OperationPolicy.Strict;

    /// <summary>
    /// K03. Проверять ли отпечатком, что операции не правят опубликованные тела на месте.
    /// Дорого (хеш всех фасетов тела перед и после каждой операции); P3D_VERIFY_SNAPSHOTS=1.
    /// </summary>
    /// <summary>KRN-03 замер (P3D_SHELLSTAT=1): у каждого нового тела операции — компоненты граней на оболочку и знаки объёмов оболочек.</summary>
    /// <summary>KRN-03: инвариант оболочек после каждой операции (P3D_SHELLFIX=0 — выключить, для мутационной проверки).</summary>
    internal static readonly bool EnforceShellInvariant = Environment.GetEnvironmentVariable("P3D_SHELLFIX") != "0";
    static readonly bool FeatStat = Environment.GetEnvironmentVariable("P3D_FEATSTAT") == "1";
    static readonly bool RebuildLog = Environment.GetEnvironmentVariable("P3D_REBUILDLOG") == "1";
    static readonly bool ShellStat = Environment.GetEnvironmentVariable("P3D_SHELLSTAT") == "1";
    static void ShellStatOf(Kernel.Topology.TSolid b, Feature f)
    {
        var parts = new List<string>();
        foreach (var sh in b.Shells)
        {
            var faces = sh.Faces.ToList(); if (faces.Count == 0) continue;
            var idx = new Dictionary<Kernel.Topology.TFace, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < faces.Count; i++) idx[faces[i]] = i;
            var par = Enumerable.Range(0, faces.Count).ToArray();
            int Fd(int x) { while (par[x] != x) x = par[x] = par[par[x]]; return x; }
            foreach (var fc in faces) foreach (var e in fc.Edges) foreach (var ce in e.CoEdges)
                if (ce.Loop?.Face is { } g && idx.TryGetValue(g, out int j)) { int a = Fd(idx[fc]), c = Fd(j); if (a != c) par[a] = c; }
            int comps = faces.Select(x => Fd(idx[x])).Distinct().Count();
            double v = Kernel.Topology.ShellComponents.SignedVolume(faces);
            parts.Add($"{comps}c/{(v > 0 ? "+" : v < 0 ? "-" : "0")}");
        }
        bool bad = parts.Any(x => !x.StartsWith("1c")) || (parts.Count > 0 && !parts[0].EndsWith("+")) || parts.Skip(1).Any(x => !x.EndsWith("-"));
        Console.Error.WriteLine($"SHELLBODY {(bad ? "BAD" : "ok")} {f.GetType().Name} shells [{string.Join(" ", parts)}]");
    }

    private static bool _gVerifyPublishedBodies = Environment.GetEnvironmentVariable("P3D_VERIFY_SNAPSHOTS") == "1";
    /// <summary>Поставка 2: значение снимка модели в контексте (ModelSettings.VerifyPublishedBodies), если он есть; иначе общее по умолчанию.</summary>
    public static bool VerifyPublishedBodies
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.VerifyPublishedBodies : _gVerifyPublishedBodies;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gVerifyPublishedBodies, value, "Part.VerifyPublishedBodies");
    }

    /// <summary>Проверять ли результат перестроения единым валидатором (тело и сетка).</summary>
    private static bool _gValidateResults = Environment.GetEnvironmentVariable("P3D_VALIDATE") != "0";
    /// <summary>Поставка 2: значение снимка модели в контексте (ModelSettings.ValidateResults), если он есть; иначе общее по умолчанию.</summary>
    public static bool ValidateResults
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.ValidateResults : _gValidateResults;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gValidateResults, value, "Part.ValidateResults");
    }

    /// <summary>Нарушение целостности последнего применённого результата; null — годен.</summary>
    public string? Integrity { get; private set; }

    /// <summary>Есть ли в детали тела, построенные приближённым маршрутом.</summary>
    public bool Approximate { get; private set; }

    /// <summary>
    /// K08. ЦЕНТР МАСС ДЕТАЛИ — по запросу (не в каждом перестроении: это 25–80 мс). По
    /// аналитической геометрии, если все грани тел поддержаны (Analytic = true), иначе по сетке.
    /// </summary>
    public (OpenTK.Mathematics.Vector3d Centroid, bool Analytic)? CenterOfMass()
    {
        if (Bodies.Count == 0) return null;
        double v = 0; var m = OpenTK.Mathematics.Vector3d.Zero; bool analytic = true;
        foreach (var b in Bodies)
        {
            if (Kernel.Topology.AnalyticFlux.MassProperties(b) is { } mp) { v += mp.Volume; m += mp.Centroid * mp.Volume; continue; }
            analytic = false;
            var md = Tessellator.Tessellate(b);
            var idx = md.Indices; var vt = md.Vertices;
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                var a = new OpenTK.Mathematics.Vector3d(vt[idx[i]].P.X, vt[idx[i]].P.Y, vt[idx[i]].P.Z);
                var bb = new OpenTK.Mathematics.Vector3d(vt[idx[i + 1]].P.X, vt[idx[i + 1]].P.Y, vt[idx[i + 1]].P.Z);
                var c = new OpenTK.Mathematics.Vector3d(vt[idx[i + 2]].P.X, vt[idx[i + 2]].P.Y, vt[idx[i + 2]].P.Z);
                double tv = OpenTK.Mathematics.Vector3d.Dot(a, OpenTK.Mathematics.Vector3d.Cross(bb, c)) / 6;
                v += tv; m += (a + bb + c) / 4 * tv;
            }
        }
        return Math.Abs(v) < 1e-12 ? null : (m / v, analytic);
    }

    /// <summary>
    /// K09. ТЕНЗОР ИНЕРЦИИ ДЕТАЛИ (плотность 1, мм⁵) относительно её центра масс — по запросу,
    /// по аналитической геометрии. null — если хоть одна грань не поддержана точным путём.
    /// Масса и моменты с плотностью — умножением на плотность материала.
    /// </summary>
    public (double Volume, OpenTK.Mathematics.Vector3d Centroid, OpenTK.Mathematics.Matrix3d Inertia)? InertiaAtCentroid()
    {
        if (Bodies.Count == 0) return null;
        double v = 0; var m1 = OpenTK.Mathematics.Vector3d.Zero;
        var parts = new List<(double V, OpenTK.Mathematics.Vector3d C, OpenTK.Mathematics.Matrix3d J)>();
        foreach (var b in Bodies)
        {
            var r = Kernel.Topology.AnalyticFlux.InertiaAtCentroid(b);
            if (r == null) return null;
            parts.Add(r.Value); v += r.Value.Volume; m1 += r.Value.Centroid * r.Value.Volume;
        }
        var cg = m1 / v;
        var J = new OpenTK.Mathematics.Matrix3d();
        foreach (var (pv, pc, pj) in parts)
        {
            // Перенос к общему центру масс (теорема Штейнера): J + m(|d|² I − d dᵀ).
            var d = pc - cg;
            J = OpenTK.Mathematics.Matrix3d.Add(OpenTK.Mathematics.Matrix3d.Add(J, pj), new OpenTK.Mathematics.Matrix3d(
                pv * (d.Y * d.Y + d.Z * d.Z), -pv * d.X * d.Y, -pv * d.X * d.Z,
                -pv * d.X * d.Y, pv * (d.X * d.X + d.Z * d.Z), -pv * d.Y * d.Z,
                -pv * d.X * d.Z, -pv * d.Y * d.Z, pv * (d.X * d.X + d.Y * d.Y)));
        }
        return (v, cg, J);
    }

    /// <summary>Поставка 3: сколько граней последнего применённого результата получили сетку от донора.</summary>
    public int LastMeshFacesReused { get; private set; }
    /// <summary>UI (кнопка «Тест»): время операций последнего применённого расчёта, мс (только выполненные заново).</summary>
    public IReadOnlyDictionary<Feature, double> LastFeatureMs { get; private set; } = new Dictionary<Feature, double>();

    public bool Apply(BuildResult r)
    {
        LastMeshFacesReused = r.MeshFacesReused;
        if (r.Version != Version) return false;
        // KRN-08 (08.6): СТАРЫЙ РЕЗУЛЬТАТ НЕ ЗАМЕНЯЕТ НОВУЮ РЕВИЗИЮ. Номер версии растёт только по уведомлениям; полоска
        // расчёта (её двигает дерево прямым присваиванием), политика, пределы и значения параметров меняются без него —
        // прежде результат, посчитанный до такой правки, применялся и снимал признак «грязная»: деталь показывала прежнюю
        // полоску/значения как текущие. Теперь такой результат отвергается. Мутация P3D_STALEINPUTS=0.
        if (!StaleInputsOff && r.Inputs is { } inp)
        {
            if (inp != (EffectiveStage, Policy, Limits)) return false;
            if (r.ParamTable != null && r.ParamSnapshot != null
                && Dependencies.Changed(r.ParamSnapshot, Dependencies.Snapshot(r.ParamTable)).Count > 0) return false;
        }

        Bodies.Clear();
        // k113: мгновенный предпросмотр — на экране сетка носителя, а тела (выбор, ссылки, ручки) — тело до операции: номера
        // граней, рёбер и вершин в сетке — его
        ShowsInstant = r.Bodies.Any(Kernel.Ops.InstantPreview.IsCarrier);
        InstantFeatureIndex = ShowsInstant ? r.InstantAt : -1;
        Bodies.AddRange(r.Bodies.Select(b => Kernel.Ops.InstantPreview.SourceBody(b) ?? b));
        BodyNameList.Clear();
        BodyNameList.AddRange(r.BodyNames);
        CagesByBody.Clear();
        foreach (var kv in r.Cages) CagesByBody[kv.Key] = kv.Value;
        Warnings.Clear();
        Warnings.AddRange(r.Warnings);
        Downgrades.Clear();
        foreach (var kv in r.GeometryDowngrades) Downgrades[kv.Key] = kv.Value;

        LastFeatureMs = new Dictionary<Feature, double>(r.FeatureMs);
        foreach (var f in Features)
        {
            f.Error = r.Errors.TryGetValue(f, out var e) ? e : null;
            f.CreatedFaceIds.Clear();
            if (r.Created.TryGetValue(f, out var ids)) f.CreatedFaceIds.AddRange(ids);
        }

        HideConsumedSketches();

        Mesh = r.Mesh;
        Quilts.Clear(); Quilts.AddRange(r.Quilts);
        Volume = r.Volume;
        _areaFrom = r;
        VolumeError = r.VolumeError;
        Bounds = r.Bounds;
        _dirty = false;

        int failed = Features.Count(f => f.Error != null);
        Error = failed > 0 ? $"Failed features in the build tree: {failed}." : null;

        // K02: нарушение целостности и приближённость не прячутся в предупреждениях.
        Integrity = r.IntegrityError;
        Approximate = r.Approximate;
        if (r.IntegrityError != null) Error = Error == null ? r.IntegrityError : Error + " " + r.IntegrityError;
        return true;
    }

    private static MeshData Merge(List<MeshData> parts)
    {
        var res = new MeshData();
        int vOff = 0, iOff = 0;
        var verts = new List<MeshVertex>();
        var idx = new List<int>();

        foreach (var p in parts)
        {
            verts.AddRange(p.Vertices);
            foreach (int i in p.Indices) idx.Add(i + vOff);
            foreach (var kv in p.FaceRanges)
            {
                var r = kv.Value;
                res.FaceRanges[kv.Key] = new FaceRange
                {
                    VertexStart = r.VertexStart + vOff,
                    VertexCount = r.VertexCount,
                    IndexStart = r.IndexStart + iOff,
                    IndexCount = r.IndexCount
                };
            }
            res.EdgePolylines.AddRange(p.EdgePolylines);
            // K01: неразмеченные грани переносятся — иначе сводная сетка выглядела целой.
            res.BrokenFaces.AddRange(p.BrokenFaces);
            res.Points.AddRange(p.Points);
            res.Bounds.Add(p.Bounds);
            vOff += p.Vertices.Length;
            iOff += p.Indices.Length;
        }

        res.Vertices = verts.ToArray();
        res.Indices = idx.ToArray();
        return res;
    }

    /// <summary>Ищет топологический элемент по id во всех телах детали.</summary>
    public TFace? FindFace(uint id) => Bodies.SelectMany(b => b.Faces).FirstOrDefault(f => f.Id == id) ?? Quilts.SelectMany(q => q.Faces).FirstOrDefault(f => f.Id == id);
    public TEdge? FindEdge(uint id) => Bodies.SelectMany(b => b.Edges).FirstOrDefault(e => e.Id == id) ?? Quilts.SelectMany(q => q.Faces.SelectMany(f => f.Edges)).FirstOrDefault(e => e.Id == id);
    public TVertex? FindVertex(uint id) => Bodies.SelectMany(b => b.Vertices).FirstOrDefault(v => v.Id == id) ?? Quilts.SelectMany(q => q.Faces.SelectMany(f => f.Edges).SelectMany(e => new[] { e.V0, e.V1 })).FirstOrDefault(v => v.Id == id);
    /// <summary>u13: квилт, которому принадлежит грань.</summary>
    public Quilt? QuiltOfFace(uint faceId) => Quilts.FirstOrDefault(q => q.Faces.Any(f => f.Id == faceId));

    /// <summary>u13: сетка квилта — треугольники, рёбра и вершины только его граней (тесселяция тела-основы).</summary>
    public static MeshData? QuiltMesh(Quilt q)
    {
        MeshData md;
        try { md = Tessellator.Tessellate(q.Parent); } catch { return null; }
        var faceIds = q.Faces.Select(f => f.Id).ToHashSet();
        var edges = q.Faces.SelectMany(f => f.Edges).ToHashSet();
        var edgeIds = edges.Select(e => e.Id).ToHashSet();
        var vIds = edges.SelectMany(e => new[] { e.V0, e.V1 }).Where(v => v != null).Select(v => v.Id).ToHashSet();
        var res = new MeshData();
        var map = new Dictionary<int, int>(); var vs = new List<MeshVertex>(); var ix = new List<int>();
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            if (!faceIds.Contains(md.Vertices[md.Indices[i]].FaceId)) continue;
            for (int k = 0; k < 3; k++)
            {
                int o = md.Indices[i + k];
                if (!map.TryGetValue(o, out int n)) { n = vs.Count; vs.Add(md.Vertices[o]); map[o] = n; }
                ix.Add(n);
            }
        }
        if (ix.Count == 0) return null;
        res.Vertices = vs.ToArray(); res.Indices = ix.ToArray();
        foreach (var e in md.EdgePolylines) if (edgeIds.Contains(e.EdgeId)) res.EdgePolylines.Add(e);
        foreach (var p in md.Points) if (vIds.Contains(p.VertexId)) res.Points.Add(p);
        var b = Bounds3.Empty; foreach (var v in vs) b.Add(new OpenTK.Mathematics.Vector3d(v.P.X, v.P.Y, v.P.Z)); res.Bounds = b;
        return res;
    }
}

/// <summary>
/// K05. ИСТОРИЯ ОПЕРАЦИИ ПО УСТОЙЧИВЫМ ИМЕНАМ ГРАНЕЙ: какие имена появились (generated), какие
/// грани с прежним именем изменились или разделились (modified, split) и какие имена исчезли
/// (deleted). Имя — подпись грани «#ключ операции/роль»; куски одной грани носят одно имя.
/// </summary>
public sealed class FeatureHistory
{
    public readonly List<string> Generated = new();
    public readonly List<string> Modified = new();
    public readonly List<string> Split = new();
    public readonly List<string> Deleted = new();

    /// <summary>
    /// K05. История рёбер: имя ребра — пара имён смежных граней («A|B», по порядку). Рёбра
    /// с одной парой (дуги одной кромки) считаются вместе: изменилось число или геометрия
    /// (длины и концы) — «изменено».
    /// </summary>
    public readonly List<string> EdgesGenerated = new();
    public readonly List<string> EdgesModified = new();
    public readonly List<string> EdgesDeleted = new();

    public static FeatureHistory Between(IReadOnlyList<Kernel.Topology.TSolid> before, IReadOnlyList<Kernel.Topology.TSolid> after)
    {
        var h = new FeatureHistory();
        // Индекс «метка → точные многоугольники» — один раз на тело за вызов (иначе каждая группа граней проходила
        // все многоугольники тела: квадратично по числу граней).
        var byTag = new Dictionary<Kernel.Topology.TSolid, Dictionary<uint, List<Kernel.Exact.ExactPolygon>>>(ReferenceEqualityComparer.Instance);
        var b = before.SelectMany(s => s.Faces).GroupBy(f => f.Tag).ToDictionary(g => g.Key, g => g.ToList());
        var a = after.SelectMany(s => s.Faces).GroupBy(f => f.Tag).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (tag, faces) in a)
        {
            if (!b.TryGetValue(tag, out var old)) { h.Generated.Add(tag); continue; }
            bool same = faces.Count == old.Count && faces.All(x => old.Any(o => ReferenceEquals(o, x)));
            if (same) continue;
            // Булева пересобирает все грани тела заново, поэтому новый объект ещё не изменение:
            // грань с тем же именем и той же областью считается прежней.
            // Поставка 3: ТОЧНОЕ сравнение областей (SameRegionExact), где у тел есть точное описание; иначе — прежнее
            // по площади и центру с допуском. Замер перед переключением (allA + allB, HISTDBG=1): 21 725 точных
            // сравнений — 0 расхождений с прежним ответом; 2 383 без точного описания.
            bool? exact = null;
            // KRN-10/KRN-08: ПОБИТОВАЯ ПОДПИСЬ ГРАНЕЙ — доказательство тождества при любом уровне гарантии (поверхность по
            // содержанию, полурёбра с направлением, кривые — все определяющие числа). Прежде у тел без уровня ExactAnalytic
            // (отверстия, карманы прямыми построителями) решало сравнение по СЕТКЕ: каждая грань тела размечалась после
            // каждой операции — 82% перестроения детали из 48 отверстий (3.9 с из 4.8, 4.2 ГБ выделений), рост O(N²).
            // Совпала — та же область; не совпала — решают прежние сравнения. Мутация P3D_HISTSIG=0.
            if (!HistSigOff && faces.Count == old.Count)
                using (Kernel.Diagnostics.Prof.Time("история: подписи"))
                {
                    // плоские грани: число контуров (компонент границы) — инвариант области; разное — область другая. 23:50: проверка
                    // — до подписей (подпись включает контуры: при разном их числе подписи разные, ответ тот же), — грань со 100
                    // отверстиями не строит строку подписи на каждой операции. Мутация P3D_HISTLOOPSFIRST=0.
                    bool planarLoopsDiffer = faces.Concat(old).All(x => Kernel.Topology.GeometryAudit.AuthoritativeSurface(x) is Kernel.Surfaces.PlaneSurface)
                                             && faces.Sum(x => x.Loops.Count) != old.Sum(x => x.Loops.Count);
                    if (planarLoopsDiffer && !HistLoopsFirstOff) { exact = false; Interlocked.Increment(ref HistLoopsFirstHits); }
                    else if (SameRegionSignature(faces, old)) exact = true;
                    else if (planarLoopsDiffer) exact = false;
                }
            if (exact == null)
            using (Kernel.Diagnostics.Prof.Time("история: точное"))
                exact = faces.Count == old.Count ? SameRegionExact(faces, old, byTag) : false;
            // Аналитические тела (точные поверхности без многоугольников с исходной меткой): та же поверхность по
            // содержанию и то же множество граничных рёбер (вид кривой, биты концов) — без допуска.
            using (Kernel.Diagnostics.Prof.Time("история: аналитическое"))
            if (exact == null && faces.Count == old.Count) exact = SameRegionAnalytic(faces, old);
            // Смешанная пара (одно тело аналитическое, другое — многогранным путём): границы групп совпадают в пределах
            // допуска модели — структурно, без допуска по площади (надрез 3e-5 мм — новый контур далеко от прежней границы).
            using (Kernel.Diagnostics.Prof.Time("история: смешанное"))
            if (exact == null && faces.Count == old.Count) exact = SameBoundaryMixed(faces, old);
            bool same2;
            using (Kernel.Diagnostics.Prof.Time("история: по сетке"))
            {
                if (exact == null && faces.Count == old.Count) Kernel.Diagnostics.Prof.Count("история: сравнений по сетке", 1);
                same2 = exact ?? (faces.Count == old.Count && SameGeometry(faces, old));
            }
            if (CompareDebug != null)
            {
                if (exact == null) CompareDebug($"n/a {tag}");
                else
                {
                    bool tol = faces.Count == old.Count && SameGeometry(faces, old);
                    CompareDebug(tol != exact ? $"DISAGREE {tag}: tolerance {tol}, exact {exact}" : $"agree {tag}: {exact}");
                }
            }
            if (same2) continue;
            if (CountPieces(faces) > CountPieces(old)) h.Split.Add(tag); else h.Modified.Add(tag);
        }
        foreach (var tag in b.Keys) if (!a.ContainsKey(tag)) h.Deleted.Add(tag);

        static string EdgeName(Kernel.Topology.TEdge e) => EdgeInfo(e).Name;
        static string EdgeSig(IEnumerable<Kernel.Topology.TEdge> es) =>
            string.Join(";", es.Select(e => EdgeInfo(e).Sig).OrderBy(x => x, StringComparer.Ordinal));
        var _pe = Kernel.Diagnostics.Prof.Time("история: рёбра");
        var eb = EdgeMap(before); var ea = EdgeMap(after);
        foreach (var (name, sig) in ea)
        {
            if (!eb.TryGetValue(name, out var old)) h.EdgesGenerated.Add(name);
            else if (old != sig) h.EdgesModified.Add(name);
        }
        foreach (var name in eb.Keys) if (!ea.ContainsKey(name)) h.EdgesDeleted.Add(name);
        _pe.Dispose();
        h.EdgesGenerated.Sort(StringComparer.Ordinal); h.EdgesModified.Sort(StringComparer.Ordinal); h.EdgesDeleted.Sort(StringComparer.Ordinal);
        h.Generated.Sort(StringComparer.Ordinal); h.Modified.Sort(StringComparer.Ordinal);
        h.Split.Sort(StringComparer.Ordinal); h.Deleted.Sort(StringComparer.Ordinal);
        return h;
    }

    /// <summary>Разбор (поставка 3): сравнение «по допуску» против точного — каждое решение (null — выключено).</summary>
    [ThreadStatic] public static Action<string>? CompareDebug;

    /// <summary>
    /// ТОЧНОЕ СРАВНЕНИЕ ОБЛАСТЕЙ ГРУПП ГРАНЕЙ (поставка 3). По точным многоугольникам тела с исходными метками
    /// граней группы: для каждой опорной плоскости — S = Σ (вектор площади треугольника · нормаль плоскости) и
    /// M = Σ центр × тот же множитель; величины рациональные, нормаль — целые коэффициенты реестра. Площадь и
    /// первый момент аддитивны, поэтому ответ не зависит от разбиения области на многоугольники и треугольники
    /// (T-стыки на границе его не меняют), а допуска нет. null — точного описания нет (тело без Exact, грань
    /// без исходной метки).
    /// </summary>
    internal static bool? SameRegionExact(List<Kernel.Topology.TFace> a, List<Kernel.Topology.TFace> b,
        Dictionary<Kernel.Topology.TSolid, Dictionary<uint, List<Kernel.Exact.ExactPolygon>>>? index = null)
    {
        index ??= new(ReferenceEqualityComparer.Instance);
        // БЫСТРЫЙ ТОЧНЫЙ ПУТЬ: тела в одном реестре, и наборы точных многоугольников групп совпадают как множества
        // (плоскость, обход, цикл номеров вершин) — области тождественны; сравниваются только целые. Так у
        // нетронутых граней: многоугольники вне коробки локальной булевой проходят как есть.
        (Kernel.Exact.PlaneRegistry? Registry, List<(string Key, Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)> Keys)? pa, pb;
        using (Kernel.Diagnostics.Prof.Time("история: наборы многоугольников")) { pa = PolygonSet(a, index); pb = PolygonSet(b, index); }
        if (pa == null || pb == null) return null;
        // Один реестр: общие многоугольники (те же ключи) дают одинаковый вклад и взаимно уничтожаются —
        // арифметика только по симметрической разности (переразбитые куски). Иначе — по всем.
        IEnumerable<(Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)> da, db;
        if (pa.Value.Registry != null && ReferenceEquals(pa.Value.Registry, pb.Value.Registry))
        {
            var ka = pa.Value.Keys; var kb = pb.Value.Keys; var ra = new List<(Kernel.Exact.ExactPolygon, Kernel.Topology.TSolid)>(); var rb = new List<(Kernel.Exact.ExactPolygon, Kernel.Topology.TSolid)>();
            int i = 0, j = 0;
            while (i < ka.Count || j < kb.Count)
            {
                int c = i >= ka.Count ? 1 : j >= kb.Count ? -1 : string.CompareOrdinal(ka[i].Key, kb[j].Key);
                if (c == 0) { i++; j++; }
                else if (c < 0) { ra.Add((ka[i].P, ka[i].Body)); i++; }
                else { rb.Add((kb[j].P, kb[j].Body)); j++; }
            }
            if (ra.Count == 0 && rb.Count == 0) return true;
            da = ra; db = rb;
        }
        else { da = pa.Value.Keys.Select(x => (x.P, x.Body)); db = pb.Value.Keys.Select(x => (x.P, x.Body)); }
        using var __ar = Kernel.Diagnostics.Prof.Time("история: точная арифметика");
        Kernel.Diagnostics.Prof.Count("история: арифметических сравнений", 1);
        var sa = ExactRegionSignature(da); var sb = ExactRegionSignature(db);
        if (sa.Count != sb.Count) return false;
        foreach (var (k, v) in sa) if (!sb.TryGetValue(k, out var w) || v != w) return false;
        // KRN-08 (08.8): совпадение площади и первых моментов — ФИЛЬТР, не доказательство (квадрат 4×4 и прямоугольник 2×8 с
        // общим центром дают одни и те же суммы). Доказательство — точные границы областей. Мутация P3D_REGIONPROOF=0.
        if (RegionProofOff) return true;
        Kernel.Diagnostics.Prof.Count("история: точных сравнений границ", 1);
        return ExactBoundariesEqual(da.ToList(), db.ToList());
    }

    static readonly bool RegionProofOff = Environment.GetEnvironmentVariable("P3D_REGIONPROOF") == "0";

    /// <summary>
    /// ТОЧНОЕ РАВЕНСТВО ПЛОСКИХ ОБЛАСТЕЙ по границам. Для каждой опорной плоскости (коэффициенты, сокращённые на НОД, со
    /// знаком по первому ненулевому) стороны многоугольников обеих групп делятся во всех вершинах обеих групп, лежащих
    /// внутри стороны (точная коллинеарность в рациональных); встречные стороны взаимно гасятся (знаковая кратность).
    /// Области (как знаковые цепи) равны тогда и только тогда, когда остатки равны: разность цепей с нулевой границей
    /// в плоскости — нулевая. Разбиение области на многоугольники и T-стыки на ответ не влияют.
    /// </summary>
    internal static bool ExactBoundariesEqual(List<(Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)> a,
                                              List<(Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)> b)
    {
        static (System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger) PlaneKey(
            (System.Numerics.BigInteger A, System.Numerics.BigInteger B, System.Numerics.BigInteger C, System.Numerics.BigInteger D) c)
        {
            var g = System.Numerics.BigInteger.GreatestCommonDivisor(System.Numerics.BigInteger.GreatestCommonDivisor(c.A, c.B), System.Numerics.BigInteger.GreatestCommonDivisor(c.C, c.D));
            if (g.IsZero) g = 1;
            var (A, B, C, D) = (c.A / g, c.B / g, c.C / g, c.D / g);
            int sg = !A.IsZero ? A.Sign : !B.IsZero ? B.Sign : C.Sign;
            return sg < 0 ? (-A, -B, -C, -D) : (A, B, C, D);
        }
        var planes = new Dictionary<(System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger),
            (List<(Q3 P, Q3 R)> A, List<(Q3 P, Q3 R)> B, HashSet<Q3> V)>();
        void Add(List<(Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)> src, bool isA)
        {
            foreach (var (p, body) in src)
            {
                var R = body.Exact!.Solid.Registry;
                var key = PlaneKey(R.Coefficients(p.Support.Id));
                if (!planes.TryGetValue(key, out var e)) planes[key] = e = (new(), new(), new());
                var pts = p.Vertices(R).ToArray().Select(v => { var h = R.Homogeneous(v); return new Q3(new(h.X, h.W), new(h.Y, h.W), new(h.Z, h.W)); }).ToArray();
                for (int i = 0; i < pts.Length; i++)
                {
                    var s = (pts[i], pts[(i + 1) % pts.Length]);
                    if (s.Item1.Equals(s.Item2)) continue;
                    (isA ? e.A : e.B).Add(s); e.V.Add(pts[i]);
                }
            }
        }
        Add(a, true); Add(b, false);
        foreach (var (_, (ea, eb, vs)) in planes)
        {
            var verts = vs.ToList();
            Dictionary<(Q3, Q3), int> Boundary(List<(Q3 P, Q3 R)> edges)
            {
                var net = new Dictionary<(Q3, Q3), int>();
                foreach (var (p, q) in edges)
                {
                    var d = q - p; var dd = Q3.Dot(d, d);
                    var on = new List<(Kernel.Exact.BigRational T, Q3 V)>();
                    double pdx = p.X.ToDouble(), pdy = p.Y.ToDouble(), pdz = p.Z.ToDouble(), ddx = d.X.ToDouble(), ddy = d.Y.ToDouble(), ddz = d.Z.ToDouble();
                    double dl2 = ddx * ddx + ddy * ddy + ddz * ddz;
                    foreach (var v in verts)
                    {
                        // грубый отсев в double, затем точно
                        double vx = v.X.ToDouble() - pdx, vy = v.Y.ToDouble() - pdy, vz = v.Z.ToDouble() - pdz;
                        double tt = (vx * ddx + vy * ddy + vz * ddz) / dl2;
                        if (tt < -1e-9 || tt > 1 + 1e-9) continue;
                        double ex = vx - tt * ddx, ey = vy - tt * ddy, ez = vz - tt * ddz;
                        if (ex * ex + ey * ey + ez * ez > 1e-12 * Math.Max(1, dl2)) continue;
                        var w = v - p;
                        if (!Q3.Cross(d, w).IsZero) continue;
                        var t = Q3.Dot(w, d);
                        if (t.Sign <= 0 || t.CompareTo(dd) >= 0) continue;
                        on.Add((t, v));
                    }
                    on.Sort((x, y) => x.T.CompareTo(y.T));
                    var chain = new List<Q3> { p }; chain.AddRange(on.Select(o => o.V)); chain.Add(q);
                    for (int k = 0; k + 1 < chain.Count; k++)
                    {
                        var (u, v2) = (chain[k], chain[k + 1]);
                        int c = u.CompareTo(v2);
                        var key = c < 0 ? (u, v2) : (v2, u);
                        net[key] = net.GetValueOrDefault(key) + (c < 0 ? 1 : -1);
                    }
                }
                foreach (var k in net.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList()) net.Remove(k);
                return net;
            }
            var ba = Boundary(ea); var bb = Boundary(eb);
            if (ba.Count != bb.Count) return false;
            foreach (var (k, v) in ba) if (!bb.TryGetValue(k, out int w) || w != v) return false;
        }
        return true;
    }

    /// <summary>Точка с рациональными координатами (мм·сетка) — для точных сравнений границ.</summary>
    internal readonly struct Q3 : IEquatable<Q3>, IComparable<Q3>
    {
        public readonly Kernel.Exact.BigRational X, Y, Z;
        public Q3(Kernel.Exact.BigRational x, Kernel.Exact.BigRational y, Kernel.Exact.BigRational z) { X = x; Y = y; Z = z; }
        public static Q3 operator -(Q3 a, Q3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Kernel.Exact.BigRational Dot(Q3 a, Q3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Q3 Cross(Q3 a, Q3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public bool IsZero => X.IsZero && Y.IsZero && Z.IsZero;
        public bool Equals(Q3 o) => X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z);
        public override bool Equals(object? obj) => obj is Q3 q && Equals(q);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public int CompareTo(Q3 o) { int c = X.CompareTo(o.X); if (c != 0) return c; c = Y.CompareTo(o.Y); return c != 0 ? c : Z.CompareTo(o.Z); }
    }

    /// <summary>Канонические ключи точных многоугольников группы (один реестр на группу; иначе Registry = null).</summary>
    private static List<Kernel.Exact.ExactPolygon> PolygonsOf(Kernel.Topology.TSolid body, uint tag,
        Dictionary<Kernel.Topology.TSolid, Dictionary<uint, List<Kernel.Exact.ExactPolygon>>> index)
    {
        if (!index.TryGetValue(body, out var map))
        {
            index[body] = map = new();
            foreach (var p in body.Exact!.Solid.Polygons)
            {
                if (!map.TryGetValue(p.Tag, out var l)) map[p.Tag] = l = new();
                l.Add(p);
            }
        }
        return map.TryGetValue(tag, out var list) ? list : new();
    }

    private static (Kernel.Exact.PlaneRegistry? Registry, List<(string Key, Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)> Keys)? PolygonSet(List<Kernel.Topology.TFace> faces,
        Dictionary<Kernel.Topology.TSolid, Dictionary<uint, List<Kernel.Exact.ExactPolygon>>> index)
    {
        Kernel.Exact.PlaneRegistry? reg = null; bool mixed = false;
        var bodies = new Dictionary<Kernel.Topology.TSolid, HashSet<uint>>(ReferenceEqualityComparer.Instance);
        foreach (var f in faces)
        {
            var body = f.Shell?.Solid;
            if (body?.Exact == null || f.SourceTag == 0) return null;
            if (!bodies.TryGetValue(body, out var tags)) bodies[body] = tags = new HashSet<uint>();
            tags.Add(f.SourceTag);
            var r = body.Exact.Solid.Registry;
            if (reg == null) reg = r; else if (!ReferenceEquals(reg, r)) mixed = true;
        }
        var keys = new List<(string Key, Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)>();
        // KRN-08: метка без многоугольников — описания нет (не «пустая область»): иначе две такие группы «равны»
        if (!RegionProofOff && bodies.Any(kv => kv.Value.Any(t => PolygonsOf(kv.Key, t, index).Count == 0))) return null;
        if (!mixed)
            foreach (var (body, tags) in bodies)
            {
                var R = body.Exact!.Solid.Registry;
                foreach (var p in tags.SelectMany(t => PolygonsOf(body, t, index)))
                {
                    var v = p.Vertices(R); int at = 0;
                    for (int i = 1; i < v.Length; i++) if (v[i] < v[at]) at = i;
                    var sb = new System.Text.StringBuilder().Append(p.Support.Id).Append(p.Support.Side > 0 ? '+' : '-');
                    for (int i = 0; i < v.Length; i++) sb.Append(',').Append(v[(at + i) % v.Length]);
                    keys.Add((sb.ToString(), p, body));
                }
            }
        keys.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        return (mixed ? null : reg, keys);
    }

    private static Dictionary<(System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger),
        (Kernel.Exact.BigRational S, Kernel.Exact.BigRational X, Kernel.Exact.BigRational Y, Kernel.Exact.BigRational Z)> ExactRegionSignature(
        IEnumerable<(Kernel.Exact.ExactPolygon P, Kernel.Topology.TSolid Body)> polys)
    {
        var sig = new Dictionary<(System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger),
            (Kernel.Exact.BigRational, Kernel.Exact.BigRational, Kernel.Exact.BigRational, Kernel.Exact.BigRational)>();
        foreach (var (p, body) in polys)
        {
            var R = body.Exact!.Solid.Registry;
            {
                var (A, B, C, D) = R.Coefficients(p.Support.Id);
                var key = (A, B, C, D);
                var hs = p.Vertices(R).ToArray().Select(v => R.Homogeneous(v)).ToArray();
                Kernel.Exact.BigRational s = 0, mx = 0, my = 0, mz = 0;
                if (hs.All(h => h.W.IsOne))
                {
                    // Все вершины — узлы сетки: суммы в целых, без НОД на каждой операции.
                    System.Numerics.BigInteger si = 0, xi = 0, yi = 0, zi = 0;
                    var h0 = hs[0];
                    for (int i = 1; i + 1 < hs.Length; i++)
                    {
                        var u = hs[i]; var t = hs[i + 1];
                        var ux = u.X - h0.X; var uy = u.Y - h0.Y; var uz = u.Z - h0.Z;
                        var tx = t.X - h0.X; var ty = t.Y - h0.Y; var tz = t.Z - h0.Z;
                        var w2i = (uy * tz - uz * ty) * A + (uz * tx - ux * tz) * B + (ux * ty - uy * tx) * C;
                        si += w2i; xi += (h0.X + u.X + t.X) * w2i; yi += (h0.Y + u.Y + t.Y) * w2i; zi += (h0.Z + u.Z + t.Z) * w2i;
                    }
                    s = si; mx = xi; my = yi; mz = zi;
                    sig[key] = sig.TryGetValue(key, out var acc0) ? (acc0.Item1 + s, acc0.Item2 + mx, acc0.Item3 + my, acc0.Item4 + mz) : (s, mx, my, mz);
                    continue;
                }
                // Рациональные вершины — в однородных целых: разности, векторное произведение и сумма вершин без
                // деления; на треугольник — четыре рациональные величины (по НОД на каждую), а не ~40.
                //   u − o = (Xu·Wo − Xo·Wu, …)/(Wu·Wo); w2 = ((u−o)×(t−o))·n = N/(Wo²·Wu·Wt);
                //   o + u + t = (Xo·Wu·Wt + Xu·Wo·Wt + Xt·Wo·Wu, …)/(Wo·Wu·Wt).
                {
                    var o = hs[0];
                    for (int i = 1; i + 1 < hs.Length; i++)
                    {
                        var u = hs[i]; var t = hs[i + 1];
                        var ux = u.X * o.W - o.X * u.W; var uy = u.Y * o.W - o.Y * u.W; var uz = u.Z * o.W - o.Z * u.W;
                        var tx = t.X * o.W - o.X * t.W; var ty = t.Y * o.W - o.Y * t.W; var tz = t.Z * o.W - o.Z * t.W;
                        var n = (uy * tz - uz * ty) * A + (uz * tx - ux * tz) * B + (ux * ty - uy * tx) * C;
                        if (n.IsZero) continue;
                        var ww = o.W * u.W * t.W;                     // знаменатель центра (×3)
                        var den = o.W * ww;                            // знаменатель w2
                        var cx = o.X * u.W * t.W + u.X * o.W * t.W + t.X * o.W * u.W;
                        var cy = o.Y * u.W * t.W + u.Y * o.W * t.W + t.Y * o.W * u.W;
                        var cz = o.Z * u.W * t.W + u.Z * o.W * t.W + t.Z * o.W * u.W;
                        s += new Kernel.Exact.BigRational(n, den);
                        var dm = den * ww;
                        mx += new Kernel.Exact.BigRational(n * cx, dm); my += new Kernel.Exact.BigRational(n * cy, dm); mz += new Kernel.Exact.BigRational(n * cz, dm);
                    }
                }
                sig[key] = sig.TryGetValue(key, out var acc) ? (acc.Item1 + s, acc.Item2 + mx, acc.Item3 + my, acc.Item4 + mz) : (s, mx, my, mz);
            }
        }
        // Плоскость с нулевым итогом (встречные куски взаимно погасились) — не часть области.
        foreach (var k in sig.Where(kv => kv.Value.Item1.Sign == 0 && kv.Value.Item2.Sign == 0 && kv.Value.Item3.Sign == 0 && kv.Value.Item4.Sign == 0).Select(kv => kv.Key).ToList())
            sig.Remove(k);
        return sig;
    }

    /// <summary>
    /// Точное сравнение групп граней аналитических тел: подпись грани — ключ поверхности по содержанию и отсортированные
    /// подписи граничных рёбер (вид кривой, биты концов, у дуги — центра и радиуса). null — не аналитические тела.
    /// </summary>
    static readonly bool HistSigOff = Environment.GetEnvironmentVariable("P3D_HISTSIG") == "0";

    // KRN-10: ПОДПИСИ ГРАНЕЙ И РЁБЕР ДЛЯ ИСТОРИИ — ОДИН РАЗ НА ГРАНЬ. Грани результата операции k — те же объекты, что вход
    // операции k+1 (опубликованы и заморожены), и подпись считалась дважды; ключ — грань и ревизия её тела (правка тела после
    // подсчёта делает запись недействительной). Мутация P3D_HISTCACHE=0.
    static readonly bool HistCacheOff = Environment.GetEnvironmentVariable("P3D_HISTCACHE") == "0";
    sealed class RevBox<T> { public long Rev; public T Value = default!; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Kernel.Topology.TFace, RevBox<string?>> FaceSigCache = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Kernel.Topology.TEdge, RevBox<(string Name, string Sig)>> EdgeInfoCache = new();
    static long RevOf(Kernel.Topology.TFace f) => f.Shell?.Solid?.Revision ?? -1;

    /// <summary>
    /// g.13: подпись ребра (вид кривой и округлённые концы) — функция кривой-объекта и концов: копии тела делят кривые, и
    /// подпись прежнего ребра не строится строкой заново после каждой операции. Мутация P3D_HISTREF=0.
    /// </summary>
    sealed class EdgeSigBox { public readonly List<(OpenTK.Mathematics.Vector3d A, OpenTK.Mathematics.Vector3d B, string S)> L = new(); }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Kernel.Curves.ICurve3, EdgeSigBox> EdgeSigCache = new();
    static string EdgeSigOf(Kernel.Topology.TEdge e)
    {
        static string P(OpenTK.Mathematics.Vector3d q) => $"{Math.Round(q.X, 6)},{Math.Round(q.Y, 6)},{Math.Round(q.Z, 6)}";
        string Make() { var ends = new[] { P(e.V0.P), P(e.V1.P) }.OrderBy(x => x, StringComparer.Ordinal); return e.Curve.GetType().Name + ":" + string.Join("/", ends); }
        if (HistRefOff) return Make();
        var box = EdgeSigCache.GetValue(e.Curve, _ => new EdgeSigBox());
        lock (box) foreach (var (a, b, str) in box.L) if (a.Equals(e.V0.P) && b.Equals(e.V1.P) && !double.IsNaN(a.X)) return str;
        var s = Make();
        lock (box) { if (box.L.Count < 8) box.L.Add((e.V0.P, e.V1.P, s)); }
        return s;
    }

    // 23:50 (скорость): КАРТА «ИМЯ РЕБРА → ПОДПИСЬ» ТЕЛА — ИЗ ПАМЯТИ по телу, его ревизии и отпечатку состава. Тела «до»
    // операции — это тела «после» предыдущей: их карта строилась на прошлом шаге (деталь со 100 отверстиями — 2 мс строк на шаг).
    // Несколько тел — общая карта, если имена в них не пересекаются (иначе — прежняя сборка по всем рёбрам). Мутация P3D_EDGEMAPMEMO=0.
    static readonly bool EdgeMapMemoOff = Environment.GetEnvironmentVariable("P3D_EDGEMAPMEMO") == "0";
    sealed class EdgeMapBox { public long Rev; public ulong Print; public Dictionary<string, string> Map = null!; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Kernel.Topology.TSolid, EdgeMapBox> _edgeMaps = new();
    public static long EdgeMapMemoHits;
    static Dictionary<string, string> EdgeMapRaw(IEnumerable<Kernel.Topology.TSolid> solids) =>
        solids.SelectMany(x => x.Edges).GroupBy(e => EdgeInfo(e).Name)
              .ToDictionary(g => g.Key, g => string.Join(";", g.Select(e => EdgeInfo(e).Sig).OrderBy(x => x, StringComparer.Ordinal)));
    static Dictionary<string, string> EdgeMap(IReadOnlyList<Kernel.Topology.TSolid> solids)
    {
        if (EdgeMapMemoOff || HistCacheOff) return EdgeMapRaw(solids);
        var maps = new List<Dictionary<string, string>>();
        foreach (var sd in solids)
        {
            long rev = sd.Revision; ulong fp = Kernel.Topology.TopoFingerprint.Of(sd, display: false);
            foreach (var f in sd.Faces) fp = (fp ^ (ulong)(uint)string.GetHashCode(f.Tag ?? "", StringComparison.Ordinal)) * 1099511628211UL;   // имена граней — в ключе
            if (_edgeMaps.TryGetValue(sd, out var b) && b.Rev == rev && b.Print == fp) { Interlocked.Increment(ref EdgeMapMemoHits); maps.Add(b.Map); continue; }
            var m = EdgeMapRaw(new[] { sd });
            _edgeMaps.AddOrUpdate(sd, new EdgeMapBox { Rev = rev, Print = fp, Map = m });
            maps.Add(m);
        }
        if (maps.Count == 1) return maps[0];
        var all = new Dictionary<string, string>();
        foreach (var m in maps) foreach (var (k, v) in m) if (!all.TryAdd(k, v)) return EdgeMapRaw(solids);
        return all;
    }

    internal static (string Name, string Sig) EdgeInfo(Kernel.Topology.TEdge e)
    {
        var face0 = e.CoEdges.FirstOrDefault()?.Loop?.Face;
        long rev = face0 == null ? -1 : RevOf(face0);
        if (!HistCacheOff && rev >= 0 && EdgeInfoCache.TryGetValue(e, out var box) && box.Rev == rev) return box.Value;
        var names = e.CoEdges.Select(c => c.Loop?.Face?.Tag ?? "").Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var v = (string.Join("|", names), EdgeSigOf(e));
        if (!HistCacheOff && rev >= 0) EdgeInfoCache.AddOrUpdate(e, new RevBox<(string, string)> { Rev = rev, Value = v });
        return v;
    }

    /// <summary>
    /// Побитовая подпись группы граней: true — доказано тождество (та же поверхность по содержанию, те же контуры с теми же
    /// кривыми и направлениями, та же сторона грани); false — не доказано (не «различны»).
    /// </summary>
    internal static bool SameRegionSignature(List<Kernel.Topology.TFace> a, List<Kernel.Topology.TFace> b)
    {
        // g.13: КОПИЯ ГРАНИ ТЕМИ ЖЕ ОБЪЕКТАМИ. Операция копирует тело (TopologyClone делит поверхности и кривые), и подпись
        // каждой неизменной грани строилась заново строкой после каждой операции (100 отверстий — квадратично). Грани с той
        // же поверхностью-объектом, стороной и теми же полурёбрами (кривая-объект, направление, концы побитно) в том же
        // порядке имеют ту же подпись — сравнение без строк; иначе — подписи. Мутация P3D_HISTREF=0.
        if (!HistRefOff && a.Count == b.Count && a.Count > 0 && SameByReference(a, b)) { Interlocked.Increment(ref HistRefHits); return true; }
        var sa = new List<string>(a.Count); var sb = new List<string>(b.Count);
        foreach (var f in a) { var k = FaceSignature(f); if (k == null) return false; sa.Add(k); }
        foreach (var f in b) { var k = FaceSignature(f); if (k == null) return false; sb.Add(k); }
        sa.Sort(StringComparer.Ordinal); sb.Sort(StringComparer.Ordinal);
        return sa.SequenceEqual(sb);
    }

    static readonly bool HistLoopsFirstOff = Environment.GetEnvironmentVariable("P3D_HISTLOOPSFIRST") == "0";
    public static long HistLoopsFirstHits;
    static readonly bool HistKeyCheapOff = Environment.GetEnvironmentVariable("P3D_HISTKEYCHEAP") == "0";
    static readonly bool HistRefEnvOff = Environment.GetEnvironmentVariable("P3D_HISTREF") == "0";
    [ThreadStatic] static bool _histRefOffForTests;
    /// <summary>Сравнение (KRN10-HISTORY-BY-REFERENCE): прежние подписи в этом потоке.</summary>
    public static bool HistRefOffForTests { get => _histRefOffForTests; set => _histRefOffForTests = value; }
    static bool HistRefOff => HistRefEnvOff || _histRefOffForTests;
    /// <summary>Сравнений групп граней, решённых совпадением объектов (замер KRN10-HISTORY-BY-REFERENCE).</summary>
    public static long HistRefHits;
    static bool SameByReference(List<Kernel.Topology.TFace> a, List<Kernel.Topology.TFace> b)
    {
        var used = new bool[b.Count];
        foreach (var f in a)
        {
            int j = -1;
            for (int k = 0; k < b.Count && j < 0; k++) if (!used[k] && SameFaceObjects(f, b[k])) j = k;
            if (j < 0) return false;
            used[j] = true;
        }
        return true;
    }
    static bool SameFaceObjects(Kernel.Topology.TFace x, Kernel.Topology.TFace y)
    {
        var sx = Kernel.Topology.GeometryAudit.AuthoritativeSurface(x);
        if (sx == null || !ReferenceEquals(sx, Kernel.Topology.GeometryAudit.AuthoritativeSurface(y)) || x.Reversed != y.Reversed) return false;
        // подпись была бы null (поверхность не по содержанию) — решают прежние сравнения, как прежде
        // 23:50 (скорость): признак ключа по содержанию — без построения строки ключа (история детали со 100 отверстиями: 5 550
        // сравнений, 220 МБ строк). Мутация P3D_HISTKEYCHEAP=0.
        if (sx is not Kernel.Surfaces.CylinderSurface && !(HistKeyCheapOff ? Kernel.Ops.BooleanOp.SourceKey(sx) is string : Kernel.Ops.BooleanOp.HasContentKey(sx))) return false;
        if (x.Loops.Count != y.Loops.Count) return false;
        for (int i = 0; i < x.Loops.Count; i++)
        {
            var lx = x.Loops[i].CoEdges; var ly = y.Loops[i].CoEdges;
            if (lx.Count != ly.Count) return false;
            for (int k = 0; k < lx.Count; k++)
            {
                var cx = lx[k]; var cy = ly[k];
                if (!ReferenceEquals(cx.Edge.Curve, cy.Edge.Curve) || cx.Reversed != cy.Reversed) return false;
                if (cx.Edge.Curve is not (Kernel.Curves.LineCurve3 or Kernel.Curves.ArcCurve3 or Kernel.Curves.EllipseCurve3 or Kernel.Curves.SplineCurve3)) return false;
                if (!Bits(cx.Edge.V0.P, cy.Edge.V0.P) || !Bits(cx.Edge.V1.P, cy.Edge.V1.P)) return false;
            }
        }
        return true;
        static bool Bits(OpenTK.Mathematics.Vector3d p, OpenTK.Mathematics.Vector3d q) =>
            BitConverter.DoubleToInt64Bits(p.X) == BitConverter.DoubleToInt64Bits(q.X) && BitConverter.DoubleToInt64Bits(p.Y) == BitConverter.DoubleToInt64Bits(q.Y) && BitConverter.DoubleToInt64Bits(p.Z) == BitConverter.DoubleToInt64Bits(q.Z);
    }

    internal static string? FaceSignature(Kernel.Topology.TFace f)
    {
        long rev = RevOf(f);
        Kernel.Diagnostics.Prof.Count("история: подписей запрошено", 1);
        if (!HistCacheOff && rev >= 0 && FaceSigCache.TryGetValue(f, out var box) && box.Rev == rev) return box.Value;
        Kernel.Diagnostics.Prof.Count("история: подписей вычислено", 1);
        var v = FaceSignatureRaw(f);
        if (!HistCacheOff && rev >= 0) FaceSigCache.AddOrUpdate(f, new RevBox<string?> { Rev = rev, Value = v });
        return v;
    }

    static string? FaceSignatureRaw(Kernel.Topology.TFace f)
    {
        static void V(System.Text.StringBuilder b, OpenTK.Mathematics.Vector3d v) => b.Append(BitConverter.DoubleToInt64Bits(v.X)).Append(',').Append(BitConverter.DoubleToInt64Bits(v.Y)).Append(',').Append(BitConverter.DoubleToInt64Bits(v.Z)).Append(';');
        static void D(System.Text.StringBuilder b, double x) => b.Append(BitConverter.DoubleToInt64Bits(x)).Append(';');
        static void Fr(System.Text.StringBuilder b, Frame3 fr) { V(b, fr.Origin); V(b, fr.U); V(b, fr.V); V(b, fr.W); }
        var src = Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
        if (src == null) return null;
        var sb = new System.Text.StringBuilder();
        if (src is Kernel.Surfaces.CylinderSurface cs) { sb.Append("C:"); Fr(sb, cs.Axis); D(sb, cs.Radius); }
        else if (Kernel.Ops.BooleanOp.SourceKey(src) is string ks) sb.Append(ks);
        else return null;
        sb.Append(f.Reversed ? "|R|" : "|F|");
        var loops = new List<string>();
        foreach (var l in f.Loops)
        {
            var ces = new List<string>();
            foreach (var ce in l.CoEdges)
            {
                var c = ce.Edge.Curve; var e = new System.Text.StringBuilder();
                e.Append(ce.Reversed ? '-' : '+');
                switch (c)
                {
                    case Kernel.Curves.LineCurve3 ln: e.Append("L"); V(e, ln.A); V(e, ln.B); break;
                    case Kernel.Curves.ArcCurve3 ar: e.Append("A"); Fr(e, ar.Frame); D(e, ar.Radius); D(e, ar.A0); D(e, ar.A1); break;
                    case Kernel.Curves.EllipseCurve3 el: e.Append("E"); Fr(e, el.Frame); D(e, el.RU); D(e, el.RV); D(e, el.A0); D(e, el.A1); break;
                    case Kernel.Curves.SplineCurve3 sp: e.Append("S"); foreach (var q in sp.ControlPoints) V(e, q); foreach (var w in sp.Weights ?? Array.Empty<double>()) D(e, w); foreach (var k in sp.Knots ?? Array.Empty<double>()) D(e, k); break;
                    default: return null;                                       // кривая не по содержанию — не берёмся
                }
                V(e, ce.Edge.V0.P); V(e, ce.Edge.V1.P);
                ces.Add(e.ToString());
            }
            // цикл без выбранного начала: поворот к наименьшей строке
            int at = 0; for (int i = 1; i < ces.Count; i++) if (string.CompareOrdinal(ces[i], ces[at]) < 0) at = i;
            loops.Add(string.Join("/", Enumerable.Range(0, ces.Count).Select(i => ces[(at + i) % ces.Count])));
        }
        loops.Sort(StringComparer.Ordinal);
        sb.Append(string.Join("#", loops));
        return sb.ToString();
    }

    internal static bool? SameRegionAnalytic(List<Kernel.Topology.TFace> a, List<Kernel.Topology.TFace> b)
    {
        static bool Analytic(Kernel.Topology.TFace f) => f.Shell?.Solid?.Guarantee == Kernel.GuaranteeLevel.ExactAnalytic;
        if (!a.All(Analytic) || !b.All(Analytic)) return null;
        static string Bits(OpenTK.Mathematics.Vector3d v) => $"{BitConverter.DoubleToInt64Bits(v.X)},{BitConverter.DoubleToInt64Bits(v.Y)},{BitConverter.DoubleToInt64Bits(v.Z)}";
        static string FaceSig(Kernel.Topology.TFace f)
        {
            var src = Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
            if (src == null) return "?";
            var sk = Kernel.Ops.BooleanOp.SourceKey(src);
            if (sk is not string ks) return "?";                       // поверхность не по содержанию — не берёмся
            var edges = f.Loops.SelectMany(l => l.CoEdges).Select(ce =>
            {
                var e = ce.Edge; var p = new[] { Bits(e.V0.P), Bits(e.V1.P) }.OrderBy(x => x, StringComparer.Ordinal);
                string extra = e.Curve is Kernel.Curves.ArcCurve3 arc ? $"|{Bits(arc.Frame.Origin)}|{BitConverter.DoubleToInt64Bits(arc.Radius)}" : "";
                return e.Curve.GetType().Name + ":" + string.Join("|", p) + extra;
            }).OrderBy(x => x, StringComparer.Ordinal);
            return ks + "#" + string.Join(";", edges);
        }
        var sa = a.Select(FaceSig).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var sb = b.Select(FaceSig).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (sa.Contains("?") || sb.Contains("?")) return null;
        // Совпадение побитовых подписей — доказательство тождества; несовпадение — НЕ доказательство различия: та же
        // геометрия может прийти в другом представлении (2.5-мерный маршрут пересобирает боковые грани новыми объектами
        // поверхностей; KRN-01: куб с уровнем ExactAnalytic ронял K05-HISTORY-HOLE — четыре неизменённые стороны
        // признавались изменёнными).
        if (sa.SequenceEqual(sb)) return true;
        // Подписи разные — сравнить ГЕОМЕТРИЮ: те же поверхности (по геометрии, не по объекту) и совпадающие границы.
        // Для поверхностей вне плоскости/цилиндра — «неизвестно» (решают прочие сравнения).
        var shA = a.Select(f => Kernel.Geometry.ExactEdges.ShapeOf(Kernel.Topology.GeometryAudit.AuthoritativeSurface(f))).ToList();
        var shB = b.Select(f => Kernel.Geometry.ExactEdges.ShapeOf(Kernel.Topology.GeometryAudit.AuthoritativeSurface(f))).ToList();
        if (shA.Concat(shB).Any(x => x is not (Kernel.Geometry.Ssi.Plane or Kernel.Geometry.Ssi.Cylinder))) return null;
        bool sameSurfaces = shA.All(x => shB.Any(y => Kernel.Analytic.GeneralBoolean.SameSurface(x, y)))
                         && shB.All(y => shA.Any(x => Kernel.Analytic.GeneralBoolean.SameSurface(x, y)));
        return sameSurfaces && BoundariesMatch(a, b);
    }

    /// <summary>
    /// Смешанная пара групп граней (ровно одна сторона — аналитическое тело): та же, если каждая точка проб каждого
    /// граничного ребра одной группы лежит на границе другой в пределах допуска модели (2e-5 мм — как Region2Ops.Eps), и
    /// наоборот. null — пара не смешанная (её решают другие сравнения).
    /// </summary>
    internal static bool? SameBoundaryMixed(List<Kernel.Topology.TFace> a, List<Kernel.Topology.TFace> b)
    {
        static bool Analytic(Kernel.Topology.TFace f) => f.Shell?.Solid?.Guarantee == Kernel.GuaranteeLevel.ExactAnalytic;
        bool aa = a.All(Analytic), ab = b.All(Analytic);
        if (aa == ab || (!aa && a.Any(Analytic)) || (!ab && b.Any(Analytic))) return null;
        return BoundariesMatch(a, b);
    }

    /// <summary>
    /// Границы двух групп граней совпадают: каждая проба каждого граничного ребра одной группы лежит на границе другой в
    /// пределах допуска модели (2e-5 мм — как Region2Ops.Eps; KRN-02 — связать с бюджетом точности), и наоборот.
    /// </summary>
    internal static bool BoundariesMatch(List<Kernel.Topology.TFace> a, List<Kernel.Topology.TFace> b)
    {
        double tol = Kernel.Analytic.Region2Ops.Eps;
        static List<OpenTK.Mathematics.Vector3d> Probes(Kernel.Topology.TEdge e)
        {
            var c = e.Curve; var r = new List<OpenTK.Mathematics.Vector3d>();
            for (int i = 0; i <= 8; i++) r.Add(c.PointAt(c.T0 + (c.T1 - c.T0) * i / 8));
            return r;
        }
        static List<List<OpenTK.Mathematics.Vector3d>> Polylines(List<Kernel.Topology.TFace> fs) =>
            fs.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Select(ce => { var p = new List<OpenTK.Mathematics.Vector3d>(); ce.Tessellate(p, true); return p; }).ToList();
        double Dist(OpenTK.Mathematics.Vector3d q, List<List<OpenTK.Mathematics.Vector3d>> lines, List<Kernel.Topology.TEdge> edges)
        {
            double best = double.MaxValue;
            // точные кривые: отрезок — расстояние до отрезка; дуга — до окружности (если угол в пределах) или концов
            foreach (var e in edges)
            {
                switch (e.Curve)
                {
                    case Kernel.Curves.LineCurve3 l:
                        {
                            var d = l.B - l.A; double t = OpenTK.Mathematics.Vector3d.Dot(q - l.A, d) / Math.Max(1e-300, d.LengthSquared);
                            t = Math.Clamp(t, 0, 1); best = Math.Min(best, (l.A + d * t - q).Length); break;
                        }
                    case Kernel.Curves.ArcCurve3 arc:
                        {
                            var o = arc.Frame.Origin; var v = q - o; var w = arc.Frame.W.Normalized();
                            double h = OpenTK.Mathematics.Vector3d.Dot(v, w); var inPlane = v - w * h;
                            double ang = Math.Atan2(OpenTK.Mathematics.Vector3d.Dot(inPlane, arc.Frame.V), OpenTK.Mathematics.Vector3d.Dot(inPlane, arc.Frame.U));
                            double d0 = ang - arc.A0, sw = arc.Sweep; double u = sw >= 0 ? ((d0 % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI) / Math.Max(1e-300, sw)
                                                                                         : ((-d0 % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI) / Math.Max(1e-300, -sw);
                            if (u <= 1 + 1e-9 || Math.Abs(Math.Abs(sw) - 2 * Math.PI) < 1e-9)
                                best = Math.Min(best, Math.Sqrt(h * h + Math.Pow(inPlane.Length - arc.Radius, 2)));
                            best = Math.Min(best, Math.Min((arc.PointAt(0) - q).Length, (arc.PointAt(1) - q).Length));
                            break;
                        }
                }
                if (best <= tol) return best;
            }
            // прочие кривые — по их ломаным
            foreach (var pl in lines)
                for (int i = 0; i + 1 < pl.Count; i++)
                {
                    var d = pl[i + 1] - pl[i]; double t = OpenTK.Mathematics.Vector3d.Dot(q - pl[i], d) / Math.Max(1e-300, d.LengthSquared);
                    t = Math.Clamp(t, 0, 1); best = Math.Min(best, (pl[i] + d * t - q).Length);
                    if (best <= tol) return best;
                }
            return best;
        }
        bool Covered(List<Kernel.Topology.TFace> from, List<Kernel.Topology.TFace> to)
        {
            var toEdges = to.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Select(ce => ce.Edge).Distinct().ToList();
            var exactEdges = toEdges.Where(e => e.Curve is Kernel.Curves.LineCurve3 or Kernel.Curves.ArcCurve3).ToList();
            var otherLines = Polylines(to.Where(f => true).ToList());
            foreach (var e in from.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Select(ce => ce.Edge).Distinct())
                foreach (var q in Probes(e))
                    if (Dist(q, otherLines, exactEdges) > tol) return false;
            return true;
        }
        return Covered(a, b) && Covered(b, a);
    }

    private static bool SameGeometry(List<Kernel.Topology.TFace> a, List<Kernel.Topology.TFace> b)
    {
        var sa = a.Select(Signature).OrderBy(x => x.Area).ToList();
        var sb = b.Select(Signature).OrderBy(x => x.Area).ToList();
        for (int i = 0; i < sa.Count; i++)
        {
            double tol = 1e-7 * Math.Max(1, sa[i].Area);
            if (Math.Abs(sa[i].Area - sb[i].Area) > tol || (sa[i].Centroid - sb[i].Centroid).Length > 1e-6) return false;
        }
        return true;
    }

    /// <summary>Площадь и центр грани по её треугольникам разбиения.</summary>
    private static (double Area, OpenTK.Mathematics.Vector3d Centroid) Signature(Kernel.Topology.TFace f)
    {
        IEnumerable<(OpenTK.Mathematics.Vector3d A, OpenTK.Mathematics.Vector3d B, OpenTK.Mathematics.Vector3d C)> tris;
        if (f.Surface is Kernel.Surfaces.FacetedSurface fs) tris = fs.Facets;
        else
        {
            var mb = new Kernel.Tess.MeshBuilder();
            try { f.Surface.TessellateFace(f, mb); } catch { return (double.NaN, default); }
            var p = mb.PositionsD; var idx = mb.IndicesList;
            var list = new List<(OpenTK.Mathematics.Vector3d, OpenTK.Mathematics.Vector3d, OpenTK.Mathematics.Vector3d)>();
            for (int i = 0; i + 2 < idx.Count; i += 3) list.Add((p[idx[i]], p[idx[i + 1]], p[idx[i + 2]]));
            tris = list;
        }
        double area = 0; var c = OpenTK.Mathematics.Vector3d.Zero;
        foreach (var (a, b, cc) in tris)
        {
            double t = OpenTK.Mathematics.Vector3d.Cross(b - a, cc - a).Length / 2;
            area += t; c += (a + b + cc) / 3 * t;
        }
        return (area, area > 0 ? c / area : c);
    }

    /// <summary>Число отдельных кусков: наружных колец (одна грань может нести несколько областей).</summary>
    private static int CountPieces(List<Kernel.Topology.TFace> faces) =>
        faces.Sum(f => Math.Max(1, f.Loops.Count(l => l.IsOuter)));

    public override string ToString() =>
        $"generated {Generated.Count}, modified {Modified.Count}, split {Split.Count}, deleted {Deleted.Count}; " +
        $"edges: generated {EdgesGenerated.Count}, modified {EdgesModified.Count}, deleted {EdgesDeleted.Count}";
}
