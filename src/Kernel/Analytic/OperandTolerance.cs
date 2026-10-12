using OpenTK.Mathematics;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// KRN-02: ИЗМЕРЕННЫЙ ДОПУСК ОПЕРАНДОВ — наибольшее отклонение вершин и точек рёбер от точных форм поверхностей смежных
/// граней. Допуск операции — max(модельная точность, измеренный допуск операндов) вместо глобальных 2e-5 мм.
/// </summary>
public static class OperandTolerance
{
    /// <summary>Расстояние точки до элементарной поверхности (плоскость, цилиндр, конус, сфера, тор); прочие — 0.</summary>
    public static double Distance(Ssi.Shape sh, Vector3d p)
    {
        switch (sh)
        {
            case Ssi.Plane pl: return Math.Abs(Vector3d.Dot(p - pl.P, pl.N.Normalized()));
            case Ssi.Cylinder cy:
                {
                    var a = cy.Axis.Normalized(); var w = p - cy.P; w -= a * Vector3d.Dot(w, a);
                    return Math.Abs(w.Length - cy.R);
                }
            case Ssi.Sphere sp: return Math.Abs((p - sp.C).Length - sp.R);
            case Ssi.Cone kn:
                {
                    var a = kn.Axis.Normalized(); var w = p - kn.Apex; double h = Vector3d.Dot(w, a);
                    double r = (w - a * h).Length;
                    return Math.Abs(r * Math.Cos(kn.HalfAngle) - h * Math.Sin(kn.HalfAngle));
                }
            case Ssi.Torus t:
                {
                    var a = t.Axis.Normalized(); var w = p - t.C; double h = Vector3d.Dot(w, a);
                    double rho = (w - a * h).Length;
                    return Math.Abs(Math.Sqrt((rho - t.R) * (rho - t.R) + h * h) - t.r);
                }
            default: return 0;
        }
    }

    /// <summary>Измеренный допуск граней: вершины и 5 внутренних точек каждого ребра — до форм смежных граней из набора.</summary>
    public static double MeasureFaces(IEnumerable<TFace> faces)
    {
        double worst = 0;
        var set = faces.ToList();
        foreach (var f in set)
        {
            var sh = ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
            if (sh == null) continue;
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
            {
                worst = Math.Max(worst, Distance(sh, ce.Start.P));
                var c = ce.Edge.Curve; if (c == null) continue;
                for (int k = 1; k <= 5; k++) worst = Math.Max(worst, Distance(sh, c.PointAt(c.T0 + (c.T1 - c.T0) * k / 6)));
            }
        }
        return worst;
    }

    public static double Measure(TSolid s) => MeasureFaces(s.Faces);

    /// <summary>Допуск операции над двумя телами: max(модельная точность, измеренные допуски операндов).</summary>
    public static double Of(TSolid a, TSolid b) => Math.Max(ToleranceContext.Current.ModelAccuracy, Math.Max(Measure(a), Measure(b)));
}
