namespace Parametric3D.Kernel.Topology;

/// <summary>Происхождение грани результата операции.</summary>
public enum FaceOrigin
{
    /// <summary>Грань операнда целиком, геометрия не изменилась.</summary>
    Kept,
    /// <summary>Грань операнда, обрезанная или продолженная (та же поверхность, другие контуры).</summary>
    Modified,
    /// <summary>Одна из нескольких граней результата из одной грани операнда.</summary>
    Split,
    /// <summary>Новая грань (лента скругления, угол, фаска).</summary>
    Generated,
}

/// <summary>Грань результата и её источник (null — новая).</summary>
public sealed record FaceProvenance(TFace Result, TFace? Source, FaceOrigin Kind, string? Note = null);

/// <summary>
/// KRN-08 (08.2): ПРОИСХОЖДЕНИЕ КАК ВЫХОД ОПЕРАЦИИ. Построитель записывает, из какой грани операнда получена каждая грань
/// результата и как (сохранена, изменена, разделена, новая), какие грани операндов исчезли и какие слились с совпадающей
/// гранью другого операнда. Это знание построителя в момент сборки, а не сопоставление после факта (по именам или
/// геометрии). Годно для той ревизии тела, при которой записано (дальнейшая правка тела делает его недоступным).
/// </summary>
public sealed class Provenance
{
    public readonly List<FaceProvenance> Faces = new();
    /// <summary>Грани операндов, от которых в результате ничего не осталось.</summary>
    public readonly List<TFace> Deleted = new();
    /// <summary>Грани операндов, слившиеся с совпадающей гранью другого операнда (в результате — одна грань).</summary>
    public readonly List<(TFace Source, TFace Into)> Merged = new();
    public string Operation = "";

    public FaceProvenance? Of(TFace result) => Faces.FirstOrDefault(p => ReferenceEquals(p.Result, result));
    public IEnumerable<TFace> ResultsOf(TFace source) => Faces.Where(p => ReferenceEquals(p.Source, source)).Select(p => p.Result);
}

/// <summary>
/// KRN-08 (08.2): ПРОИСХОЖДЕНИЕ, ВЫВЕДЕННОЕ ПОСЛЕ ФАКТА — для построителей, которые карту не пишут (первое выдавливание,
/// точный прилив и вырез по окружности, прочие операции дерева). Грань результата на той же несущей, что грань тела до
/// операции (та же поверхность или та же неявная форма), — её; та же несущая у нескольких граней результата — Split;
/// вершины контуров совпали до модельной точности — Kept, иначе Modified; несущей нет у входа — Generated; грань входа без
/// граней результата — Deleted. Запись помечена «inferred»: это сопоставление по геометрии, а не знание построителя.
/// </summary>
/// <summary>Ребро результата и его источник (null — новое).</summary>
public sealed record EdgeProvenance(TEdge Result, TEdge? Source, FaceOrigin Kind);

public static class ProvenanceInfer
{
    /// <summary>
    /// KRN-08 (08.2): ПРОИСХОЖДЕНИЕ РЁБЕР (выведенное): ребро результата на той же кривой, что ребро входа (тот же объект или
    /// та же прямая/окружность), — его: концы совпали — Kept, иначе Modified (укорочено, продлено); одна кривая у нескольких
    /// рёбер результата — Split; нет — Generated; ребро входа без рёбер результата — в deleted.
    /// </summary>
    public static (List<EdgeProvenance> Edges, List<TEdge> Deleted) EdgesOf(IReadOnlyList<TSolid> before, TSolid after)
    {
        double acc = 10 * ToleranceContext.Current.ModelAccuracy;
        var ins = before.SelectMany(b => b.Edges).ToList();
        bool SameCurve(Curves.ICurve3 a, Curves.ICurve3 b)
        {
            if (ReferenceEquals(a, b)) return true;
            switch (a, b)
            {
                case (Curves.LineCurve3 x, Curves.LineCurve3 y):
                {
                    var d = (x.B - x.A).Normalized(); double Off(OpenTK.Mathematics.Vector3d p) { var w = p - x.A; return (w - d * OpenTK.Mathematics.Vector3d.Dot(w, d)).Length; }
                    return Off(y.A) <= acc && Off(y.B) <= acc;
                }
                case (Curves.ArcCurve3 x, Curves.ArcCurve3 y):
                    return (x.Frame.Origin - y.Frame.Origin).Length <= acc && Math.Abs(x.Radius - y.Radius) <= acc
                        && Math.Abs(Math.Abs(OpenTK.Mathematics.Vector3d.Dot(x.Frame.W.Normalized(), y.Frame.W.Normalized())) - 1) < 1e-9;
            }
            return false;
        }
        var src = after.Edges.ToDictionary(e => e, e => (TEdge?)ins.FirstOrDefault(i => SameCurve(i.Curve, e.Curve)
                      && (ReferenceEquals(i.Curve, e.Curve) || !(e.Curve is Curves.LineCurve3) || Overlap(i, e, acc))), (IEqualityComparer<TEdge>)ReferenceEqualityComparer.Instance);
        var res = new List<EdgeProvenance>();
        foreach (var (e, s0) in src)
        {
            if (s0 == null) { res.Add(new EdgeProvenance(e, null, FaceOrigin.Generated)); continue; }
            int shares = src.Values.Count(v => ReferenceEquals(v, s0));
            bool same = ((e.V0.P - s0.V0.P).Length <= acc && (e.V1.P - s0.V1.P).Length <= acc) || ((e.V0.P - s0.V1.P).Length <= acc && (e.V1.P - s0.V0.P).Length <= acc);
            res.Add(new EdgeProvenance(e, s0, shares > 1 ? FaceOrigin.Split : same ? FaceOrigin.Kept : FaceOrigin.Modified));
        }
        var deleted = ins.Where(i => !src.Values.Any(v => ReferenceEquals(v, i))).ToList();
        return (res, deleted);
    }

    /// <summary>Отрезки на одной прямой перекрываются (не просто лежат на ней).</summary>
    static bool Overlap(TEdge a, TEdge b, double acc)
    {
        var d = (a.V1.P - a.V0.P); double L = d.Length; if (L < acc) return false; d /= L;
        double s0 = OpenTK.Mathematics.Vector3d.Dot(b.V0.P - a.V0.P, d), s1 = OpenTK.Mathematics.Vector3d.Dot(b.V1.P - a.V0.P, d);
        return Math.Max(s0, s1) > acc && Math.Min(s0, s1) < L - acc;
    }

    public static Provenance Of(IReadOnlyList<TSolid> before, TSolid after, string operation)
    {
        var pv = new Provenance { Operation = operation + " (inferred)" };
        var inFaces = before.SelectMany(b => b.Faces).ToList();
        var inShape = inFaces.Select(f => (F: f, S: GeometryAudit.AuthoritativeSurface(f), K: Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)))).ToList();
        double acc = 10 * ToleranceContext.Current.ModelAccuracy;
        static List<OpenTK.Mathematics.Vector3d> Pts(TFace f) => f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
        var src = new Dictionary<TFace, TFace?>(ReferenceEqualityComparer.Instance);
        foreach (var rf in after.Faces)
        {
            var s = GeometryAudit.AuthoritativeSurface(rf); var k = Geometry.ExactEdges.ShapeOf(s);
            var hit = inShape.Where(x => s != null && (ReferenceEquals(x.S, s) || x.K != null && k != null && Analytic.GeneralBoolean.SameSurface(x.K, k)
                                                       && OpenTK.Mathematics.Vector3d.Dot(NormalAtFirst(x.F), NormalAtFirst(rf)) > 0)).Select(x => x.F).ToList();
            // несколько граней входа на одной несущей — ближайшая по вершинам
            src[rf] = hit.Count == 0 ? null : hit.OrderBy(h => Pts(rf).Sum(p => Pts(h).Min(q => (q - p).Length))).First();
        }
        foreach (var rf in after.Faces)
        {
            var sf = src[rf];
            if (sf == null) { pv.Faces.Add(new FaceProvenance(rf, null, FaceOrigin.Generated, "inferred")); continue; }
            int shares = src.Values.Count(v => ReferenceEquals(v, sf));
            var a = Pts(rf); var b = Pts(sf);
            bool same = a.Count == b.Count && a.All(p => b.Any(q => (q - p).Length <= acc)) && b.All(p => a.Any(q => (q - p).Length <= acc));
            pv.Faces.Add(new FaceProvenance(rf, sf, shares > 1 ? FaceOrigin.Split : same ? FaceOrigin.Kept : FaceOrigin.Modified, "inferred"));
        }
        foreach (var f in inFaces) if (!src.Values.Any(v => ReferenceEquals(v, f))) pv.Deleted.Add(f);
        return pv;
    }

    static OpenTK.Mathematics.Vector3d NormalAtFirst(TFace f)
    {
        var p = f.Loops.SelectMany(l => l.CoEdges).Select(c => (OpenTK.Mathematics.Vector3d?)c.Start.P).FirstOrDefault();
        if (p is not { } q) return OpenTK.Mathematics.Vector3d.Zero;
        try { return f.NormalAt(q); } catch { return OpenTK.Mathematics.Vector3d.Zero; }
    }
}
