using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// Результат измерения: значение, уровень геометрии, из которой оно получено, и граница ошибки.
/// Error = 0 у аналитического измерения означает «ошибка только округления double».
/// </summary>
public readonly record struct Measured(double Value, GeometryLevel Level, double Error, string Source)
{
    public bool Exact => Level == GeometryLevel.Analytic && Error == 0;
    public override string ToString() => $"{Value:0.######} ({Level}, ±{Error:0.######}, {Source})";
}

/// <summary>
/// K08. ИЗМЕРЕНИЯ ПО АВТОРИТЕТНОЙ ГЕОМЕТРИИ.
///
/// Прежде размер детали брался из сетки: радиус отверстия — по вершинам гранения (хорда меньше
/// дуги), объём — по треугольникам, и вызывающий код не знал, что получил приближение. Здесь
/// измерение берётся из аналитической поверхности или кривой, когда она есть и согласована с
/// соседями (GeometryAudit), а иначе — из гранения, но с уровнем Faceted и оценкой ошибки.
/// </summary>
public static class AnalyticMeasure
{
    /// <summary>Радиус цилиндрической или сферической грани.</summary>
    public static Measured? Radius(TFace f)
    {
        var s = GeometryAudit.AuthoritativeSurface(f);
        switch (s)
        {
            case ExtrudedSurface ex when ex.Base is ArcCurve3 a:
                return new Measured(a.Radius, GeometryLevel.Analytic, 0, "cylinder");
            case CylinderSurface cs:
                return new Measured(cs.Radius, GeometryLevel.Analytic, 0, "cylinder");
            case RevolvedSurface rv when rv.Base is ArcCurve3 b
                                      && DistanceToAxis(b.Frame.Origin, rv.Axis) < 1e-12:
                return new Measured(b.Radius, GeometryLevel.Analytic, 0, "sphere");
            case RevolvedSurface rv2 when rv2.Base is LineCurve3 l
                                       && Math.Abs(DistanceToAxis(l.A, rv2.Axis) - DistanceToAxis(l.B, rv2.Axis)) < 1e-12:
                return new Measured(DistanceToAxis(l.A, rv2.Axis), GeometryLevel.Analytic, 0, "cylinder of revolution");
            default:
                return null;
        }
    }

    /// <summary>Расстояние между двумя параллельными плоскими гранями.</summary>
    public static Measured? PlaneDistance(TFace a, TFace b)
    {
        if (PlaneOf(a) is not { } pa || PlaneOf(b) is not { } pb) return null;
        var na = pa.Normal; var nb = pb.Normal;
        if (Math.Abs(Math.Abs(Vector3d.Dot(na, nb)) - 1) > 1e-12) return null;
        return new Measured(Math.Abs(Vector3d.Dot(pb.Point - pa.Point, na)), GeometryLevel.Analytic, 0, "parallel planes");
    }

    /// <summary>Угол между плоскими гранями, градусы (по наружным нормалям).</summary>
    public static Measured? Angle(TFace a, TFace b)
    {
        if (PlaneOf(a) is not { } pa || PlaneOf(b) is not { } pb) return null;
        double c = Math.Clamp(Vector3d.Dot(pa.Normal * (a.Reversed ? -1 : 1), pb.Normal * (b.Reversed ? -1 : 1)), -1, 1);
        return new Measured(Math.Acos(c) * 180 / Math.PI, GeometryLevel.Analytic, 0, "planes");
    }

    /// <summary>Длина ребра: точная для отрезка и дуги, иначе по ломаной с уровнем Faceted.</summary>
    public static Measured Length(TEdge e)
    {
        switch (e.Curve)
        {
            case LineCurve3 l: return new Measured((l.B - l.A).Length, GeometryLevel.Analytic, 0, "line");
            case ArcCurve3 a: return new Measured(a.Radius * Math.Abs(a.Sweep), GeometryLevel.Analytic, 0, "arc");
        }
        var pts = new List<Vector3d>();
        e.Curve.Tessellate(pts, true);
        double len = 0;
        for (int i = 0; i + 1 < pts.Count; i++) len += (pts[i + 1] - pts[i]).Length;
        // Ломаная вписана в кривую: её длина не больше настоящей; граница — по прогибу хорд.
        return new Measured(len, GeometryLevel.Faceted, len * 1e-3, e.Curve.GetType().Name);
    }

    /// <summary>
    /// Объём с честной границей ошибки. Сетка аппроксимирует аналитические грани с наибольшим
    /// отходом chord; разница объёмов не больше chord × площадь поверхности. Если у тела есть
    /// точное полиэдральное описание, его объём — точное число для ЭТОГО многогранника, но
    /// многогранник сам аппроксимирует криволинейные грани с тем же порядком ошибки.
    /// </summary>
    static readonly bool PolyBoundOff = Environment.GetEnvironmentVariable("P3D_POLYBOUND") == "0";
    static readonly bool TolMeasuredOff = Environment.GetEnvironmentVariable("P3D_TOLMEASURED") == "0";

    /// <summary>
    /// g.04 (02.2): насколько граница тела может отходить от поверхностей граней — для границы ошибки объёма (× площадь).
    /// Хранимые допуски рёбер и вершин; когда у всех граней элементарная форма — ещё ИЗМЕРЕННОЕ отклонение вершин и кривых
    /// (×2 — запас на точки между замерами) и модельная точность. Прежде нижний предел был 1e-5 мм (предел бюджета, а не
    /// допуск): тонкостенная деталь (оболочка 60 мм, стенка 2) получала границу 0.35 мм³ — больше бюджета интегрирования
    /// при отклонении границы ~1e-12 мм. Грань без элементарной формы — по-прежнему 1e-5 (не измерить). Мутация P3D_TOLMEASURED=0.
    /// </summary>
    public static double EntityBound(TSolid solid)
    {
        double tol = 0;
        foreach (var e in solid.Edges) tol = Math.Max(tol, Math.Max(e.Tolerance, Math.Max(e.V0.Tolerance, e.V1.Tolerance)));
        bool allElementary = !TolMeasuredOff && solid.Faces.All(f => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) != null);
        if (!allElementary) return Math.Max(tol, 1e-5);
        return Math.Max(tol, Math.Max(ToleranceContext.Current.ModelAccuracy, 2 * Analytic.OperandTolerance.Measure(solid)));
    }

    public static Measured Volume(TSolid solid, MeshData? mesh = null)
    {
        mesh ??= Tessellator.Tessellate(solid);
        double v = MassProps.Volume(mesh), area = MassProps.Area(mesh);

        // Точный путь: интеграл по настоящим кривым рёбер (AnalyticVolume). Ошибка — от того,
        // что вершины тела стоят на точной сетке, а не на пересечениях поверхностей: не больше
        // наибольшего допуска вершины или ребра на площадь поверхности, плюс квадратура.
        if (AnalyticVolume.OfWithError(solid) is { } ve)
        {
            double exactV = ve.Volume;
            double tol = EntityBound(solid);
            // g.04: граница — допуск сущностей × площадь + ИЗМЕРЕННАЯ ошибка квадратуры + округление
            double err = tol * area + ve.QuadratureError + 1e-9 * Math.Abs(exactV);
            // g.04 (серия 8, случай 79): ТЕЛО С АВТОРИТЕТНОЙ МНОГОГРАННОЙ ФОРМОЙ — интеграл по источникам граней принимается в 1%
            // от многогранника, но его граница была только «допуски × площадь»: брусок минус тор — сообщаемый объём на 216 мм³
            // (0.55%) от OpenCascade при границе 0.3. Честная граница: |V_интеграл − V_многогранник| + отход фасет от источников
            // × площадь (истина — в пределах гранения от многогранника). Мутация P3D_POLYBOUND=0.
            if (!PolyBoundOff && solid.Exact != null && solid.ExactAuthoritative)
            {
                double vp; try { vp = solid.Exact.Solid.VolumeMm3().ToDouble(); } catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { vp = double.NaN; }
                var aud = GeometryAudit.Of(solid);
                double chordP = aud.Faces.Count == 0 ? 0 : aud.Faces.Max(f => double.IsNaN(f.ChordError) ? 0 : f.ChordError);
                err += (double.IsNaN(vp) ? double.PositiveInfinity : Math.Abs(exactV - vp)) + 2 * chordP * area;
            }
            return new Measured(exactV, GeometryLevel.Analytic, err, "analytic boundary integral");
        }
        var audit = GeometryAudit.Of(solid);
        double chord = audit.Faces.Count == 0 ? 0 : audit.Faces.Max(f => double.IsNaN(f.ChordError) ? 0 : f.ChordError);
        bool faceted = audit.Faces.Any(f => f.Level != GeometryLevel.Analytic);
        // Грань без аналитической основы: ошибка неизвестна, граница — бесконечность.
        double bound = faceted ? double.PositiveInfinity : chord * area;
        return new Measured(v, chord == 0 && !faceted ? GeometryLevel.Analytic : GeometryLevel.Faceted, bound,
                            chord == 0 && !faceted ? "planar mesh" : "mesh of analytic faces");
    }

    private readonly record struct PlaneRec(Vector3d Point, Vector3d Normal);

    private static PlaneRec? PlaneOf(TFace f)
    {
        switch (GeometryAudit.AuthoritativeSurface(f))
        {
            case PlaneSurface p: return new PlaneRec(p.F.Origin, p.F.W.Normalized());
            case ExtrudedSurface ex when ex.Base is LineCurve3 ln:
            {
                var n = Vector3d.Cross((ln.B - ln.A).Normalized(), ex.Dir.Normalized());
                return n.LengthSquared < 1e-18 ? null : new PlaneRec(ln.A, n.Normalized());
            }
            default: return null;
        }
    }

    private static double DistanceToAxis(Vector3d p, Frame3 axis)
    {
        var d = p - axis.Origin; var w = axis.W.Normalized();
        return (d - w * Vector3d.Dot(d, w)).Length;
    }
}
