using OpenTK.Mathematics;
using Parametric3D.Kernel.Analytic;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Query;

/// <summary>
/// KRN-10 (10.2): ПОДГОТОВЛЕННОЕ ТЕЛО ДЛЯ ПОВТОРНЫХ ЗАПРОСОВ. Точные формы граней (AnalyticFace.Of — разбор поверхности,
/// контуры в параметрах, гранение рёбер), классификатор точки и консервативные габариты граней строятся один раз на
/// состояние тела и запоминаются по телу, его ревизии и отпечатку состава (TopoFingerprint): любая правка — открытая
/// (BeginEdit) или на месте до публикации — даёт новую подготовку, устаревшая не отвечает. Ответы те же, что у
/// BodyQuery по телу (та же процедура над теми же формами граней); ближайшая точка дополнительно пропускает грани, чей
/// габарит дальше лучшего найденного кандидата — точка грани не ближе её габарита.
/// Мутации: P3D_PREPAREDBODY=0 — без запоминания (подготовка на каждый запрос), P3D_PREPAREDREV=0 — запомненное без
/// сверки состояния тела, P3D_PREPAREDPRUNE=0 — без отсева граней по габариту.
/// </summary>
public sealed class PreparedBody
{
    internal static readonly bool CacheOff = Environment.GetEnvironmentVariable("P3D_PREPAREDBODY") == "0";
    internal static readonly bool RevOff = Environment.GetEnvironmentVariable("P3D_PREPAREDREV") == "0";
    internal static readonly bool PruneOff = Environment.GetEnvironmentVariable("P3D_PREPAREDPRUNE") == "0";

    /// <summary>Счётчики: сколько подготовок построено и сколько граней отсеяно габаритом в ближайшей точке.</summary>
    public static long Builds, PrunedFaces;

    public TSolid Body { get; }
    public long Revision { get; }
    readonly ulong _print;
    readonly List<AnalyticFace>? _faces;               // null — у тела есть грань без точной формы
    readonly (Vector3d Lo, Vector3d Hi)[]? _boxes;
    readonly Classifier _classifier;
    /// <summary>Причина, по которой точных форм граней нет (null — все грани точные).</summary>
    public string? Unsupported { get; }

    PreparedBody(TSolid body)
    {
        Body = body; Revision = body.Revision; _print = TopoFingerprint.Of(body);
        System.Threading.Interlocked.Increment(ref Builds);
        var faces = new List<AnalyticFace>();
        foreach (var f in body.Faces)
        {
            var af = AnalyticFace.Of(f, out var why);
            if (af == null) { Unsupported = why ?? "face without exact form"; faces = null; break; }
            faces.Add(af);
        }
        _faces = faces;
        _boxes = faces?.Select(FaceIntersect.PairBox).ToArray();
        _classifier = faces != null ? new Classifier(faces) : new Classifier(body);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TSolid, PreparedBody> _cache = new();

    /// <summary>Подготовка текущего состояния тела (запомненная, если тело с тех пор не менялось).</summary>
    public static PreparedBody For(TSolid body)
    {
        if (!CacheOff && _cache.TryGetValue(body, out var hit) && hit.Matches(body)) return hit;
        var p = new PreparedBody(body);
        if (!CacheOff) _cache.AddOrUpdate(body, p);
        return p;
    }

    /// <summary>Подготовка согласована с текущим состоянием тела.</summary>
    public bool IsCurrent => Matches(Body);
    bool Matches(TSolid b) => RevOff || b.Revision == Revision && TopoFingerprint.Of(b) == _print;

    /// <summary>Устаревшая подготовка (тело правили после неё) отвечает за текущее состояние — через новую.</summary>
    PreparedBody Current => IsCurrent ? this : For(Body);

    public PointClass Classify(Vector3d p)
    {
        var c = Current; return BodyQuery.Of(c._classifier.Classify(p));
    }

    public BodyQuery.ClosestResult? Closest(Vector3d p)
    {
        var c = Current; return c._faces == null ? null : BodyQuery.ClosestCore(c.Body, p, c._faces, c._boxes);
    }

    public double? Support(Vector3d d)
    {
        var c = Current; return c._faces == null ? null : BodyQuery.SupportCore(c.Body, d, c._faces);
    }
}
