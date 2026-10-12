using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>Уровень геометрии элемента тела.</summary>
public enum GeometryLevel
{
    /// <summary>Аналитическая поверхность или кривая, согласованная с соседями в пределах допуска.</summary>
    Analytic,
    /// <summary>Аналитическая запись есть, но элемент отходит от смежных поверхностей больше допуска.</summary>
    Inconsistent,
    /// <summary>Только гранение (ломаная, треугольники) без аналитической основы.</summary>
    Faceted
}

/// <summary>Грань: какая поверхность за ней стоит и насколько её гранение отходит от поверхности.</summary>
public readonly record struct FaceGeometry(uint Face, string Surface, GeometryLevel Level, double ChordError);

/// <summary>Ребро: кривая и её отход от обеих смежных поверхностей.</summary>
public readonly record struct EdgeGeometry(uint Edge, string Curve, GeometryLevel Level, double OnA, double OnB);

/// <summary>
/// K08. АВТОРИТЕТНАЯ ГЕОМЕТРИЯ ТЕЛА — ЧТО ИЗ НЕЁ АНАЛИТИЧЕСКОЕ И С КАКОЙ ОШИБКОЙ.
///
/// После булевой грань хранит гранение (FacetedSurface) и ссылку на исходную аналитическую
/// поверхность; ребро — кривую, которую восстановление пытается вернуть к отрезку, дуге или
/// эллипсу. Насколько это удалось, раньше не знал никто: размеры и экспорт брали что есть, и
/// гранение выдавалось за геометрию. Здесь для каждой грани и каждого ребра определяется уровень:
///   • грань: есть ли аналитическая поверхность и как далеко её треугольники отходят от неё
///     (это и есть ошибка аппроксимации, доступная вызывающему коду);
///   • ребро: аналитическая ли кривая и лежит ли она на ОБЕИХ смежных поверхностях — согласованы
///     ли 3D-кривая и поверхности. Кривая, отходящая от своей поверхности, — не «аналитика», а
///     противоречие, и о нём говорится отдельно.
/// </summary>
public sealed class GeometryAudit
{
    public readonly List<FaceGeometry> Faces = new();
    public readonly List<EdgeGeometry> Edges = new();

    public int AnalyticFaces => Faces.Count(f => f.Level == GeometryLevel.Analytic);
    public int AnalyticEdges => Edges.Count(e => e.Level == GeometryLevel.Analytic);
    public int InconsistentEdges => Edges.Count(e => e.Level == GeometryLevel.Inconsistent);
    public double MaxChordError => Faces.Count == 0 ? 0 : Faces.Max(f => f.ChordError);
    public bool FullyAnalytic => Faces.All(f => f.Level == GeometryLevel.Analytic) && Edges.All(e => e.Level == GeometryLevel.Analytic);

    /// <summary>Допуск согласования кривой с поверхностью: шаг точной сетки с запасом и допуск ребра.</summary>
    public static double OnSurfaceTolerance(TEdge e) => Math.Max(1e-4, 2 * e.Tolerance);

    public static ISurface3? AuthoritativeSurface(TFace f) =>
        f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

    /// <summary>Диагностика: вызывается для каждого противоречивого ребра.</summary>
    public static Action<TSolid, TEdge, List<TFace>>? OnInconsistent;

    /// <summary>
    /// KRN-01 (ворота g.03): сколько граней и рёбер тела заданы НЕ точной геометрией. У тела с авторитетной многогранной формой
    /// (solid.Exact) геометрия — фасеты: каждая кривая грань (источник не плоскость) — гранёная, хотя аудит видит её источник и
    /// считает точной; прочие тела — по аудиту.
    /// </summary>
    /// <summary>Мутация P3D_AUDITPROC=0: кривая по формуле (линия касания катящегося шара) — не точная.</summary>
    static readonly bool AuditProcOff = Environment.GetEnvironmentVariable("P3D_AUDITPROC") == "0";

    public static (int Faces, int Edges) Faceted(TSolid solid)
    {
        if ((solid.Exact != null && solid.ExactAuthoritative) || solid.Guarantee is GuaranteeLevel.ExactPolyhedral or GuaranteeLevel.Approximate)
        {
            // представление тела — многогранное: даже у тела из одних плоскостей это не B-rep точных поверхностей (не меньше 1)
            int curved = solid.Faces.Count(f => AuthoritativeSurface(f) is not { IsPlanar: true });
            return (Math.Max(1, curved), 0);
        }
        var a = Of(solid);
        return (a.Faces.Count(f => f.Level != GeometryLevel.Analytic), a.Edges.Count(e => e.Level != GeometryLevel.Analytic));
    }

    public static GeometryAudit Of(TSolid solid)
    {
        var a = new GeometryAudit();
        foreach (var f in solid.Faces)
        {
            var s = AuthoritativeSurface(f);
            if (s == null || s is FacetedSurface)
            {
                a.Faces.Add(new FaceGeometry(f.Id, "faceted", GeometryLevel.Faceted, double.NaN));
                continue;
            }
            double chord = 0;
            if (f.Surface is FacetedSurface fs)
            {
                // Ошибка гранения: наибольший отход центров и середин сторон треугольников.
                // KRN-02 (P0): по ГЕОМЕТРИИ (Facets), не по сетке отображения — прежде аудит, а за ним годность и допуски,
                // зависели от хорды отображения
                var list = fs.Facets.ReadView;
                int step = Math.Max(1, list.Count / 2000);
                for (int i = 0; i < list.Count; i += step)
                {
                    var (p, q, r) = list[i];
                    chord = Math.Max(chord, Distance(s, (p + q + r) / 3));
                    chord = Math.Max(chord, Distance(s, (p + q) / 2));
                }
            }
            a.Faces.Add(new FaceGeometry(f.Id, s.GetType().Name, GeometryLevel.Analytic, chord));
        }

        var chordOf = a.Faces.ToDictionary(f => f.Face, f => double.IsNaN(f.ChordError) ? 0 : f.ChordError);
        // KRN-02: тело из точных поверхностей (аналитический или квадрический путь) — без послабления на гранение: допуск
        // ребра — max(модельная точность, измеренный допуск ребра), допуск больше предела сущности — несогласованность.
        // Прежде для ВСЕХ тел — не меньше 1e-4 мм (в 1000× грубее модельной точности) и + 1.5 × ошибка гранения.
        var budget = ToleranceContext.Current;
        bool exactBody = solid.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric;
        foreach (var e in solid.Edges)
        {
            string kind = e.Curve.GetType().Name;
            bool analytic = e.Curve is LineCurve3 or ArcCurve3 or EllipseCurve3 or SplineCurve3 or IntersectionCurve3 || (e.Curve is ProcCurve3 && !AuditProcOff);
            var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            var sa = faces.Count > 0 ? AuthoritativeSurface(faces[0]) : null;
            var sb = faces.Count > 1 ? AuthoritativeSurface(faces[1]) : sa;
            double da = sa == null || sa is FacetedSurface ? double.NaN : CurveOff(e.Curve, sa);
            double db = sb == null || sb is FacetedSurface ? double.NaN : CurveOff(e.Curve, sb);
            if (exactBody)
            {
                // точное расстояние до элементарной поверхности (проекция на поверхность численная — её шум 1e-9…1e-8)
                if (Geometry.ExactEdges.ShapeOf(sa) is { } ha) da = CurveOffExact(e.Curve, ha);
                if (Geometry.ExactEdges.ShapeOf(sb) is { } hb) db = CurveOffExact(e.Curve, hb);
            }

            // Допуск согласования учитывает и заявленную ошибку гранения смежных граней: точная
            // булева шла по гранёным операндам, и точки её рёбер лежат на хордах, а не на самой
            // поверхности. Отход в пределах этой ошибки — заявленная аппроксимация; сверх неё —
            // противоречие кривой и поверхности.
            double chordNear = faces.Select(f => chordOf.GetValueOrDefault(f.Id)).DefaultIfEmpty(0).Max();
            double tol = exactBody
                ? (e.Tolerance > budget.MaxEntityTolerance ? -1 : Math.Max(budget.ModelAccuracy, e.Tolerance) * (1 + 1e-9))
                : Math.Max(OnSurfaceTolerance(e), 1.5 * chordNear);
            GeometryLevel level;
            if (!analytic || double.IsNaN(da) || double.IsNaN(db)) level = GeometryLevel.Faceted;
            else level = Math.Max(da, db) <= tol ? GeometryLevel.Analytic : GeometryLevel.Inconsistent;
            a.Edges.Add(new EdgeGeometry(e.Id, kind, level, da, db));
            if (level == GeometryLevel.Inconsistent) OnInconsistent?.Invoke(solid, e, faces);
        }
        return a;
    }

    private static double Distance(ISurface3 s, Vector3d p)
    {
        try { return (s.PointAt(s.ProjectToUV(p)) - p).Length; }
        catch { return double.NaN; }
    }

    private static double CurveOffExact(ICurve3 c, Geometry.Ssi.Shape sh)
    {
        double worst = 0;
        for (int k = 0; k <= 16; k++) worst = Math.Max(worst, Analytic.OperandTolerance.Distance(sh, c.PointAt(c.T0 + (c.T1 - c.T0) * k / 16.0)));
        return worst;
    }

    /// <summary>Наибольший отход кривой от поверхности по 17 точкам параметра.</summary>
    private static double CurveOff(ICurve3 c, ISurface3 s)
    {
        double worst = 0;
        for (int k = 0; k <= 16; k++)
        {
            double t = c.T0 + (c.T1 - c.T0) * k / 16.0;
            double d = Distance(s, c.PointAt(t));
            if (double.IsNaN(d)) return double.NaN;
            worst = Math.Max(worst, d);
        }
        return worst;
    }

    public string Summary() =>
        $"faces analytic {AnalyticFaces}/{Faces.Count}, edges analytic {AnalyticEdges}/{Edges.Count}, inconsistent {InconsistentEdges}, " +
        $"max chord error {MaxChordError:0.#####} mm";
}
