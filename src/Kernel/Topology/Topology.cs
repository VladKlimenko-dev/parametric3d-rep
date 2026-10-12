using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// Граничное представление (B-Rep).
///
/// Ключевая идея, которой не было в старом коде: ТОПОЛОГИЯ ОТДЕЛЕНА ОТ ГЕОМЕТРИИ
/// И ОТ ОТРИСОВКИ. TFace знает свою поверхность и свои контуры; он ничего не знает
/// про VAO, шейдеры и подсветку. Всё это живёт в Render и подключается по Id.
///
/// Иерархия: TSolid -> TShell -> TFace -> TLoop -> TCoEdge -> TEdge -> TVertex
/// TEdge всегда общая для двух смежных граней; направление обхода задаётся
/// флагом Reversed в TCoEdge. Именно это даёт «настоящие» рёбра для скруглений,
/// фасок и для отрисовки каркаса без дублирования линий.
/// </summary>
public abstract class TEntity : IFreezable
{
    public readonly uint Id = Ids.New();
    private string _tag = string.Empty;
    /// <summary>Устойчивое имя для параметрических ссылок (Round по ребру и т.п.).</summary>
    public string Tag { get => _tag; set { Guard(nameof(Tag)); _tag = value; } }
    public object? Attr;

    /// <summary>
    /// ПОСТАВКА 2. Сущность опубликованного тела (TSolid.Publish): форма и тождество неизменяемы — запись в
    /// защищённое поле бросает исключение (запрет, а не обнаружение задним числом). Производные данные
    /// (допуски, p-кривые, кэши ломаных и сеток) писать можно: это кэши, а не форма.
    /// </summary>
    internal bool Frozen;
    bool IFreezable.IsFrozen => Frozen;
    string IFreezable.FreezeName => $"{GetType().Name} {Id}";

    private protected void Guard(string field)
    {
        if (Frozen) throw new PublishedBodyMutationException(this, field);
    }
}

/// <summary>Попытка изменить форму или тождество опубликованного (неизменяемого) тела.</summary>
public sealed class PublishedBodyMutationException(string owner, string field)
    : InvalidOperationException($"published body is immutable: {owner}.{field} modified in place")
{
    public PublishedBodyMutationException(TEntity entity, string field) : this($"{entity.GetType().Name} {entity.Id}", field) { }
    public string Owner { get; } = owner;
    public string Field { get; } = field;
}

public sealed class TVertex : TEntity
{
    private Vector3d _p;
    public Vector3d P { get => _p; set { Guard(nameof(P)); _p = value; } }
    public readonly TopoList<TEdge> Edges;

    /// <summary>
    /// СОБСТВЕННЫЙ ДОПУСК ВЕРШИНЫ. Промышленное ядро хранит допуск у каждого элемента, а не
    /// один на всю модель: в одном месте тела геометрия сходится до микрона, в другом — лишь до
    /// сотых, и требовать везде одного значения значит либо отвергать годные тела, либо
    /// принимать негодные. Ноль означает «как у модели» (общий допуск склейки).
    /// </summary>
    public double Tolerance;

    /// <summary>Действующий допуск: собственный, если задан, иначе общий.</summary>
    public double EffectiveTolerance => Tolerance > 0 ? Tolerance : Core.Tol.Weld;

    public TVertex(Vector3d p) { Edges = new(this, nameof(Edges)); P = p; }
    public override string ToString() => $"V{Id} {P}";
}

public sealed class TEdge : TEntity
{
    public TEdge() { CoEdges = new(this, nameof(CoEdges)); }

    private TVertex _v0 = null!, _v1 = null!;
    private ICurve3 _curve = null!;
    public TVertex V0 { get => _v0; set { Guard(nameof(V0)); _v0 = value; } }
    public TVertex V1 { get => _v1; set { Guard(nameof(V1)); _v1 = value; } }
    public ICurve3 Curve { get => _curve; set { Guard(nameof(Curve)); _curve = value; } }

    /// <summary>
    /// СОБСТВЕННЫЙ ДОПУСК РЕБРА: насколько его кривая расходится с вершинами и с соседними
    /// гранями. Измеряется при сборке тела и хранится здесь, чтобы проверки спрашивали ребро, а
    /// не общий допуск модели. Ноль означает «как у модели».
    /// </summary>
    public double Tolerance;

    public double EffectiveTolerance => Tolerance > 0 ? Tolerance : Core.Tol.Weld;

    /// <summary>
    /// Измерить и запомнить собственный допуск: наибольшее расхождение концов кривой с
    /// вершинами. Это то самое расхождение, которое мы мерили отдельно (до 0.02 мм у дуг).
    /// </summary>
    public double MeasureTolerance()
    {
        if (Curve == null || V0 == null || V1 == null) return Tolerance;
        double d0 = (Curve.PointAt(Curve.T0) - V0.P).Length;
        double d1 = (Curve.PointAt(Curve.T1) - V1.P).Length;
        Tolerance = Math.Max(Tolerance, Math.Max(d0, d1));
        return Tolerance;
    }

    /// <summary>
    /// Ребро в каркасе не показывается: гладкий стык граней или вырожденное.
    /// Из топологии при этом не выпадает — петли граней на него ссылаются.
    /// </summary>
    public bool Hidden;
    /// <summary>
    /// Точки ребра по его точной кривой, от V0 к V1 (этап 3): считаются один раз, и обе
    /// соседние грани строят свою сетку по ним же — щелей на шве нет по построению.
    /// </summary>
    /// <summary>
    /// Разметка ребра точками, посчитанная однажды. Рядом хранится ТО, ПРИ КАКОЙ ТОЧНОСТИ она
    /// посчитана: без этого смена прогиба и углового шага не действовала вовсе — на окружности
    /// R10 переход с прогиба 0.5 на 0.001 оставлял прежние 11 точек вместо 224, и повышение
    /// качества молча ничего не меняло.
    /// </summary>
    public List<Vector3d>? PolylineCache;

    /// <summary>Точность, при которой посчитана разметка: прогиб и угловой шаг.</summary>
    public (double Chord, double Angle) PolylineQuality = (double.NaN, double.NaN);

    /// <summary>
    /// K03. ОТ ЧЕГО ПОСЧИТАНА ЛОМАНАЯ: кривая и концы. Прежде ключом кэша была только точность,
    /// и после замены кривой (подгонка дуги, перенос вершины) ребро отдавало точки прежней.
    /// </summary>
    internal (Curves.ICurve3? Curve, OpenTK.Mathematics.Vector3d P0, OpenTK.Mathematics.Vector3d P1) PolylineSource;

    /// <summary>Полурёбра, ссылающиеся на это ребро (обычно 2 — по одному на грань).</summary>
    public readonly TopoList<TCoEdge> CoEdges;
    /// <summary>Замкнутое ребро (окружность): V0 == V1.</summary>
    public bool IsSeam => V0 == V1;

    public TFace? FaceA => CoEdges.Count > 0 ? CoEdges[0].Loop?.Face : null;
    public TFace? FaceB => CoEdges.Count > 1 ? CoEdges[1].Loop?.Face : null;

    /// <summary>Двугранный угол между смежными гранями в середине ребра (рад). NaN, если грань одна.</summary>
    public double DihedralAngle()
    {
        if (FaceA == null || FaceB == null) return double.NaN;
        Vector3d p = Curve.PointAt(0.5);
        Vector3d na = FaceA.NormalAt(p);
        Vector3d nb = FaceB.NormalAt(p);
        double d = Math.Clamp(Vector3d.Dot(na, nb), -1, 1);
        return Math.Acos(d);
    }

    public override string ToString() => $"E{Id}";
}

/// <summary>Полуребро: ребро в контексте конкретного контура, с направлением обхода.</summary>
public sealed class TCoEdge
{
    private TEdge _edge = null!;
    private bool _reversed;
    private TLoop? _loop;
    /// <summary>Поставка 2: у полуребра нет своей сущности — его поля неизменяемы, когда опубликован его контур.</summary>
    private void Guard(string field)
    {
        if (_loop is { Frozen: true }) throw new PublishedBodyMutationException(_loop, "CoEdge." + field);
    }
    public TEdge Edge { get => _edge; set { Guard(nameof(Edge)); _edge = value; } }
    public bool Reversed { get => _reversed; set { Guard(nameof(Reversed)); _reversed = value; } }
    public TLoop? Loop { get => _loop; set { Guard(nameof(Loop)); _loop = value; } }

    /// <summary>G1: кривая этого полуребра в параметрах поверхности грани (от Start к End) или null.</summary>
    public Curves.ICurve2? PCurve;
    /// <summary>G1: наибольшее расстояние S(pcurve(t)) до точки ребра, мм.</summary>
    public double PCurveDeviation;

    public TVertex Start => Reversed ? Edge.V1 : Edge.V0;
    public TVertex End => Reversed ? Edge.V0 : Edge.V1;

    public void Tessellate(List<Vector3d> dst, bool includeFirst)
    {
        int mark = dst.Count;
        Edge.Curve.Tessellate(dst, true);
        if (Reversed) dst.Reverse(mark, dst.Count - mark);
        if (!includeFirst && dst.Count > mark) dst.RemoveAt(mark);
    }
}

public sealed class TLoop : TEntity
{
    public TLoop() { CoEdges = new(this, nameof(CoEdges)); }

    public TFace? Face;
    public readonly TopoList<TCoEdge> CoEdges;
    /// <summary>true — внешний контур грани, false — отверстие.</summary>
    public bool IsOuter = true;

    /// <summary>
    /// ОБХОД КОНТУРА ПО СВЯЗЯМ, А НЕ ПО ПОРЯДКУ В СПИСКЕ. Полурёбра складываются в список в том
    /// порядке, в каком их нашла сборка, и он не обязан совпадать с обходом — проверки, которые
    /// на это полагались, находили «разрывы» в десятки миллиметров на целом теле.
    ///
    /// Здесь цепочка строится по вершинам: конец одного полуребра ищется началом следующего.
    /// Шов (одно ребро, пройденное дважды) обходится верно, потому что каждое полуребро берётся
    /// ровно один раз. Возвращает null, если контур действительно не сходится.
    /// </summary>
    public List<TCoEdge>? Ordered()
    {
        if (CoEdges.Count == 0) return null;
        if (CoEdges.Count == 1) return new List<TCoEdge>(CoEdges);

        var left = new List<TCoEdge>(CoEdges);
        var chain = new List<TCoEdge> { left[0] };
        left.RemoveAt(0);

        var at = chain[0].End;
        while (left.Count > 0)
        {
            int found = -1;
            for (int i = 0; i < left.Count; i++)
                if (ReferenceEquals(left[i].Start, at)) { found = i; break; }

            // Вершины могут быть разными объектами в одном месте: тогда сходимся по положению,
            // но в пределах допуска самой вершины, а не общего.
            if (found < 0)
                for (int i = 0; i < left.Count; i++)
                {
                    double allow = Math.Max(at.EffectiveTolerance, left[i].Start.EffectiveTolerance);
                    if ((left[i].Start.P - at.P).Length <= allow) { found = i; break; }
                }

            if (found < 0) return null;                  // цепочка оборвалась

            chain.Add(left[found]);
            at = left[found].End;
            left.RemoveAt(found);
        }

        // Контур замкнут, когда конец последнего сходится с началом первого.
        double close = Math.Max(at.EffectiveTolerance, chain[0].Start.EffectiveTolerance);
        return (at.P - chain[0].Start.P).Length <= close ? chain : null;
    }

    /// <summary>
    /// Складываются ли полурёбра в ЗАМКНУТЫЕ КОЛЬЦА. Проверяется не последовательным подбором
    /// соседа — он спотыкается там, где шов даёт вершину четвёртой степени, — а чётностью
    /// степеней вершин: у замкнутого обхода каждая вершина входит столько же раз, сколько
    /// выходит.
    ///
    /// СВЯЗНОСТЬ НЕ ТРЕБУЕТСЯ, и это главное. Измерено на грани после булевой: в одном списке
    /// контура лежат ДВА кольца — наружный прямоугольник из четырёх рёбер и отверстие из трёх
    /// дуг. Требование связности объявляло такую грань незамкнутой, хотя она целая.
    /// </summary>
    public bool FormsCycle()
    {
        if (CoEdges.Count == 0) return false;

        // Степени считаются ПО ПОЛОЖЕНИЮ, а не по ссылке: одна и та же точка контура бывает
        // представлена разными объектами вершин, и тогда у каждой выходит нечётная степень при
        // целом контуре.
        static (long, long, long) K(Vector3d p) =>
            ((long)Math.Round(p.X / Core.Tol.Weld), (long)Math.Round(p.Y / Core.Tol.Weld), (long)Math.Round(p.Z / Core.Tol.Weld));

        var degree = new Dictionary<(long, long, long), int>();
        foreach (var ce in CoEdges)
        {
            if (ce.Start == null || ce.End == null) return false;
            var a = K(ce.Start.P); var b = K(ce.End.P);
            degree[a] = degree.GetValueOrDefault(a) + 1;
            degree[b] = degree.GetValueOrDefault(b) + 1;
        }

        foreach (var (_, n) in degree)
            if (n % 2 != 0) return false;                // нечётная вершина — обхода нет

        return true;
    }

    public void Add(TEdge e, bool reversed)
    {
        var ce = new TCoEdge { Edge = e, Reversed = reversed, Loop = this };
        CoEdges.Add(ce);
        e.CoEdges.Add(ce);
    }

    public List<Vector3d> TessellatePolyline()
    {
        var pts = new List<Vector3d>();
        for (int i = 0; i < CoEdges.Count; i++)
            CoEdges[i].Tessellate(pts, i == 0);
        // Замыкание: убрать дубль. Остаётся та из двух точек, что совпадает с вершиной контура:
        // у развёрнутой окружности первой идёт точка конца параметра (угол 2π, sin 2π ≠ 0), и
        // если оставить её, крышка цилиндра расходилась со стенкой у шва (4 стороны без пары у
        // цилиндра после соглашения K05, у которого нижняя крышка обходится наоборот).
        if (pts.Count > 1 && (pts[0] - pts[^1]).LengthSquared < Tol.Weld * Tol.Weld)
        {
            // Единственное замкнутое ребро: держаться точки НАЧАЛА ПАРАМЕТРА его кривой — её
            // же берут соседние грани (у тела вращения — стенка); вершина может отличаться от неё
            // в последнем знаке (поворот на π/2 даёт 1e-15 вместо 0).
            var v = CoEdges.Count == 1 && ReferenceEquals(CoEdges[0].Edge.V0, CoEdges[0].Edge.V1)
                ? CoEdges[0].Edge.Curve.PointAt(CoEdges[0].Edge.Curve.T0)
                : CoEdges.Count > 0 ? CoEdges[0].Start.P : pts[^1];
            if (pts[0] != v && pts[^1] == v) pts.RemoveAt(0);
            else pts.RemoveAt(pts.Count - 1);
        }
        return pts;
    }
}

public sealed class TFace : TEntity
{
    public TFace() { Loops = new(this, nameof(Loops)); }

    private ISurface3 _surface = null!;
    private bool _reversed;
    public ISurface3 Surface { get => _surface; set { Guard(nameof(Surface)); _surface = value; } }
    public readonly TopoList<TLoop> Loops;
    public TShell? Shell;
    /// <summary>Нормаль поверхности инвертирована относительно наружной нормали тела.</summary>
    public bool Reversed { get => _reversed; set { Guard(nameof(Reversed)); _reversed = value; } }
    public int MaterialSlot = 0;
    /// <summary>g.02: точка внутри области грани, известная построителю (общая булева) — для выбора стороны колец при
    /// нормализации, когда геометрия её не определяет (грань-сфера с одним кольцом). null — не задана.</summary>
    public OpenTK.Mathematics.Vector3d? InteriorHint;
    /// <summary>Номер исходной грани многоугольников точного тела, из которых собрана грань (0 — нет).</summary>
    public uint SourceTag;

    public TLoop? Outer => Loops.FirstOrDefault(l => l.IsOuter);
    public IEnumerable<TLoop> Inners => Loops.Where(l => !l.IsOuter);

    public TLoop AddLoop(bool outer = true)
    {
        var l = new TLoop { Face = this, IsOuter = outer };
        Loops.Add(l);
        return l;
    }

    public Vector3d NormalAt(Vector3d worldPoint)
    {
        Vector3d n = Surface.NormalAt(Surface.ProjectToUV(worldPoint));
        return Reversed ? -n : n;
    }

    public IEnumerable<TEdge> Edges => Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge).Distinct();

    public override string ToString() => $"F{Id} [{Surface.GetType().Name}]";
}

public sealed class TShell : TEntity
{
    public TShell() { Faces = new(this, nameof(Faces)); }

    public readonly TopoList<TFace> Faces;
    public TSolid? Solid;
    public bool IsClosed = true;

    public void Add(TFace f) { f.Shell = this; Faces.Add(f); }
}

public sealed class TSolid : TEntity
{
    public TSolid() { Shells = new(this, nameof(Shells)); }

    public readonly TopoList<TShell> Shells;

    /// <summary>
    /// ТОЧНАЯ ФОРМА ТЕЛА, если оно получено точной булевой.
    ///
    /// Следующая булева берёт её, а не переводит тело заново: округление
    /// на сетку происходит один раз. Годна, пока тело не меняли.
    /// Поставка 2: прежде «код, меняющий вершины или грани готового тела, обязан обнулить её» — соглашение
    /// вручную. Теперь точная форма помнит ревизию B-rep, с которой согласована (ExactRevision), и правка
    /// формы (BeginEdit) делает её устаревшей явно — см. ExactIsCurrent.
    /// </summary>
    public Exact.ExactBody? Exact
    {
        get => _exact;
        set { Guard(nameof(Exact)); _exact = value; ExactRevision = value == null ? -1 : Revision; ExactAuthoritative = value != null; }
    }

    /// <summary>
    /// Точная форма — ИСТИНА тела (результат точной булевой: B-rep построен из неё). Иначе она производна от
    /// B-rep (подогнанный резец, AttachDerivedExact) и устаревает от любой правки B-rep.
    /// </summary>
    public bool ExactAuthoritative { get; private set; }

    /// <summary>Положить точную форму, ПРОИЗВОДНУЮ от текущего B-rep (B-rep — истина).</summary>
    /// <summary>
    /// ЛЕНИВАЯ ПРОИЗВОДНАЯ ТОЧНАЯ ФОРМА (аналитическое ядро): строится по требованию (ExactAdapter.ToExact), когда
    /// следующую операцию выполняет многогранный путь; в цепочках аналитических операций — ни разу (построение дорогое:
    /// гранение точных поверхностей). Годна, пока ревизия тела — та, при которой фабрика задана. Кэш — внутри фабрики
    /// (производные данные, как видимая сетка), не в замороженном состоянии тела.
    /// </summary>
    internal (Lazy<Exact.ExactBody> Form, long Revision)? LazyDerivedExact;

    private (Provenance P, long Revision)? _provenance;
    /// <summary>KRN-08: происхождение граней, записанное построившей тело операцией (null — не записано или тело правили).</summary>
    public Provenance? Provenance => _provenance is { } pv && pv.Revision == Revision ? pv.P : null;
    public void SetProvenance(Provenance p) => _provenance = (p, Revision);

    public void AttachDerivedExact(Exact.ExactBody exact)
    {
        Exact = exact; ExactAuthoritative = false;
    }

    /// <summary>Последняя открытая правка (для разбора устаревших представлений).</summary>
    public string? LastEdit { get; private set; }
    private Exact.ExactBody? _exact;

    // ============================================================ поставка 2: ревизии и публикация

    /// <summary>Ревизия B-rep: растёт на каждой открытой правке формы (BeginEdit).</summary>
    public long Revision { get; private set; }
    /// <summary>Ревизия B-rep, с которой согласована точная форма (−1 — её нет).</summary>
    public long ExactRevision { get; private set; } = -1;
    /// <summary>Точная форма есть и согласована с текущей ревизией B-rep.</summary>
    public bool ExactIsCurrent => _exact != null && ExactRevision == Revision;
    /// <summary>Ревизия B-rep, для которой построена точная видимая сетка (−1 — не строилась).</summary>
    public long MeshRevision { get; internal set; } = -1;
    /// <summary>Тело опубликовано (результат перестроения, сохранённое состояние): неизменяемо.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>Состояния представлений тела — одним значением (для проверок, интерфейса, экспорта).</summary>
    public RepresentationState Representations => new(Revision, IsPublished,
        _exact != null, ExactRevision, ExactIsCurrent, MeshRevision, MeshRevision == Revision && DisplayMeshKey != null);

    /// <summary>
    /// ОТКРЫТЬ ПРАВКУ B-REP НА МЕСТЕ. Опубликованное тело править нельзя (исключение); иначе ревизия растёт.
    /// Точная форма остаётся согласованной после Layout, а после Derive — если она истина тела (доводка
    /// производного B-rep до неё); после Shape — устаревает.
    /// </summary>
    public void BeginEdit(string who, EditKind kind)
    {
        if (IsPublished) throw new PublishedBodyMutationException(this, who);
        bool wasCurrent = ExactIsCurrent;
        Revision++; LastEdit = who;
        bool keeps = kind == EditKind.Layout || kind == EditKind.Derive && ExactAuthoritative;
        if (keeps && wasCurrent) ExactRevision = Revision;
    }

    /// <summary>
    /// Состояния представлений структурной копии (TopologyClone.Deep): точная форма разделяется и остаётся
    /// согласованной, только если была согласована у оригинала; сетка — так же.
    /// </summary>
    internal void CopyRepresentationStateFrom(TSolid src)
    {
        _exact = src._exact; ExactAuthoritative = src.ExactAuthoritative;
        ExactRevision = src._exact == null ? -1 : src.ExactIsCurrent ? Revision : -2;
        DisplayMeshKey = src.DisplayMeshKey;
        MeshRevision = src.MeshRevision == src.Revision ? Revision : -1;
        LastEdit = "clone of " + src.Id;
    }

    /// <summary>
    /// ОПУБЛИКОВАТЬ: тело и все его сущности становятся неизменяемыми (один обход; повторный вызов — даром).
    /// </summary>
    public void Publish()
    {
        if (IsPublished) return;
        IsPublished = true; Frozen = true;
        foreach (var sh in Shells)
        {
            sh.Frozen = true;
            foreach (var f in sh.Faces)
            {
                f.Frozen = true;
                if (f.Surface is Surfaces.FacetedSurface fs) fs.Freeze();
                foreach (var l in f.Loops)
                {
                    l.Frozen = true;
                    foreach (var ce in l.CoEdges)
                    {
                        ce.Edge.Frozen = true; ce.Edge.V0.Frozen = true; ce.Edge.V1.Frozen = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// ТОЧНОЕ ОПИСАНИЕ С КВАДРИКАМИ. Прежнее поле хранит тело ПЛОСКОСТЯМИ, и тело с настоящей
    /// цилиндрической гранью в него не укладывается — оно оставалось без точного описания вовсе,
    /// и всё, что на него опирается (объём, замкнутость), переставало работать.
    ///
    /// Здесь лежит описание расширенного ядра: «внутри этих поверхностей, кроме этих вырезов».
    /// Оно точно отвечает, где точка, а объём по нему считается счётом точек — с известной
    /// погрешностью, а не выдаваемой за точную.
    /// </summary>
    public Exact.QRegion? QExact;

    /// <summary>
    /// K02. УРОВЕНЬ ГАРАНТИЙ, с которым тело выдала последняя операция с контрактом. Приближённое
    /// тело несёт пометку дальше: результат операции с ним тоже приближённый. Интерфейс, массовые
    /// свойства и экспорт читают её, а не текст предупреждений.
    /// </summary>
    public GuaranteeLevel Guarantee = GuaranteeLevel.Unspecified;

    /// <summary>
    /// K05. Версия соглашения о полурёбрах, которой тело приведено (TopologyConventions); 0 —
    /// прежнее состояние без гарантий направления полурёбер.
    /// </summary>
    public int TopologyConvention;

    /// <summary>
    /// K03. С КАКИМИ НАСТРОЙКАМИ ТЕЛУ УЖЕ ВЫДАНА ТОЧНАЯ ВИДИМАЯ СЕТКА. Видимая сетка — производное
    /// от неизменяемого опубликованного тела; при тех же настройках считать её заново незачем, а
    /// перезапись у тела, на которое ссылаются сохранённые состояния и экран, — лишняя правка
    /// опубликованного объекта (и лишняя работа каждого перестроения без изменений).
    /// </summary>
    internal (double Chord, double Angle, bool PerFace)? DisplayMeshKey;

    /// <summary>
    /// Объём по ТОЧНОМУ описанию, каким бы оно ни было. У тела из плоскостей это целая
    /// арифметика и ответ без погрешности; у тела с квадриками — счёт точек, и тогда
    /// возвращается ещё и его погрешность. Спрашивающему не нужно знать, какое описание внутри,
    /// — ему нужен объём и мера доверия к нему.
    ///
    /// Возвращает null, если точного описания нет вовсе: тогда остаётся объём по сетке, и
    /// спрашивающий должен знать, что это уже приближение.
    /// </summary>
    public (double Volume, double Error)? ExactVolume()
    {
        if (Exact != null) return (Exact.Solid.VolumeMm3().ToDouble(), 0);

        if (QExact == null) return null;

        // Габарит для счёта берётся по самому телу с небольшим запасом.
        Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
        foreach (var v in Vertices) { lo = Vector3d.ComponentMin(lo, v.P); hi = Vector3d.ComponentMax(hi, v.P); }
        if (hi.X < lo.X) return null;

        Vector3d pad = new(1, 1, 1);
        return QExact.MeasureVolume(lo - pad, hi + pad);
    }

    public IEnumerable<TFace> Faces => Shells.SelectMany(s => s.Faces);
    public IEnumerable<TEdge> Edges => Faces.SelectMany(f => f.Edges).Distinct();
    public IEnumerable<TVertex> Vertices => Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct();

    public TShell AddShell()
    {
        var s = new TShell { Solid = this };
        Shells.Add(s);
        return s;
    }

    /// <summary>
    /// ГАБАРИТ ТЕЛА, ОХВАТЫВАЮЩИЙ ЕГО ЦЕЛИКОМ.
    ///
    /// Прежде он считался только по вершинам, и это давало неверный ответ везде, где кривая
    /// выходит за свои концы. У цилиндра R10 высотой 20 замкнутая окружность имеет ОДНУ
    /// топологическую вершину, и габарит выходил (10, 0, 0)…(10, 0, 20) вместо
    /// (−10, −10, 0)…(10, 10, 20). На таком габарите нельзя строить ни ранний отказ булевой, ни
    /// отсев по коробкам: тела, которые пересекаются, объявлялись бы непересекающимися.
    ///
    /// Теперь в габарит входят и точки самих кривых: дуга и окружность добавляются вместе со
    /// своими крайними точками, прочие кривые — гранением по прогибу. Это даёт габарит не
    /// меньше настоящего, а лишний запас безопасен.
    /// </summary>
    /// <summary>Запас габарита на прогиб гранения: треугольники вписаны, поверхность снаружи.</summary>
    private static double Pad => Math.Max(Core.Tol.Chord, 1e-6);

    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty;
            foreach (var v in Vertices) b.Add(v.P);

            foreach (var e in Edges)
            {
                if (e.Curve == null) continue;

                switch (e.Curve)
                {
                    case Curves.LineCurve3:
                        break;                       // концы уже учтены вершинами

                    case Curves.ArcCurve3 arc:
                    {
                        // Крайние точки дуги: где касательная перпендикулярна оси координат.
                        // Проще и надёжнее взять достаточно частые точки — дуга их не обманет.
                        int n = Math.Max(8, (int)Math.Ceiling(Math.Abs(arc.Sweep) / (Math.PI / 16)));
                        for (int i = 0; i <= n; i++)
                            b.Add(arc.PointAt(arc.T0 + (arc.T1 - arc.T0) * i / n));
                        break;
                    }

                    default:
                    {
                        var pts = new List<Vector3d>();
                        try { e.Curve.Tessellate(pts, true); } catch { }
                        foreach (var p in pts) b.Add(p);
                        break;
                    }
                }
            }

            // ТЕЛО МОЖЕТ НЕ ИМЕТЬ НИ ОДНОЙ ВЕРШИНЫ И НИ ОДНОГО РЕБРА. Шар — именно такой случай:
            // одна замкнутая поверхность без швов. По вершинам и рёбрам габарит у него выходил
            // пустым, то есть бесконечным, и отсев по коробкам на нём не работал вовсе.
            //
            // Поэтому берутся и точки самих граней: у гранёной поверхности — её треугольники, у
            // прочих — гранение. Это дороже, но делается лишь тогда, когда иначе габарита нет.
            bool empty = b.Min.X > b.Max.X;

            foreach (var f in Faces)
            {
                if (f.Surface is Surfaces.FacetedSurface fs && fs.Facets.Count > 0)
                {
                    foreach (var t in fs.Facets) { b.Add(t.A); b.Add(t.B); b.Add(t.C); }
                    continue;
                }

                // ГРАНЬ БЕЗ ТРЕУГОЛЬНИКОВ опрашивается по сетке параметров. Так у конуса боковая
                // поверхность даёт вершину: по рёбрам и вершинам её не видно вовсе — там только
                // окружность основания, и габарит выходил плоским (высота 0.04 вместо 12).
                //
                // Девять на девять точек дёшевы и берутся лишь у неогранённых граней; недостающее
                // добирает запас на прогиб ниже.
                const int n = 8;
                for (int i = 0; i <= n; i++)
                    for (int j = 0; j <= n; j++)
                    {
                        try { b.Add(f.Surface.PointAt(new Vector2d(i / (double)n, j / (double)n))); }
                        catch { }
                    }

                if (!empty) continue;

                var mb = new Tess.MeshBuilder();
                try { f.Surface.TessellateFace(f, mb); } catch { }
                foreach (var p in mb.PositionsD) b.Add(p);
            }

            // ЗАПАС НА ПРОГИБ ГРАНЕНИЯ. Треугольники вписаны в поверхность: между хордой и дугой
            // поверхность выходит наружу на величину прогиба. Габарит, снятый по треугольникам,
            // оказался бы чуть меньше тела, а он обязан охватывать его целиком — лишний запас
            // безопасен, недостаток нет.
            if (b.Min.X <= b.Max.X)
            {
                double pad = Pad;
                b.Add(b.Min - new Vector3d(pad, pad, pad));
                b.Add(b.Max + new Vector3d(pad, pad, pad));
            }

            return b;
        }
    }
}

/// <summary>Поставка 2. Состояния представлений тела: что есть, что согласовано и с какой ревизией B-rep.</summary>
public readonly record struct RepresentationState(
    long BRepRevision, bool Published,
    bool HasExact, long ExactRevision, bool ExactCurrent,
    long MeshRevision, bool MeshCurrent);

/// <summary>Вид правки B-rep на месте (TSolid.BeginEdit).</summary>
public enum EditKind
{
    /// <summary>Раскладка и имена (полурёбра, метки): форма та же.</summary>
    Layout,
    /// <summary>Доводка производного B-rep (лечение, привязка вершин и кривых) в пределах допуска.</summary>
    Derive,
    /// <summary>Изменение формы.</summary>
    Shape,
}
