using OpenTK.Mathematics;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// KRN-02/03: ДОПУСКИ СУЩНОСТЕЙ ТОЧНОГО ТЕЛА — ИЗМЕРЕННЫЕ. Ребро: наибольшее отклонение его концов и 5 внутренних точек от
/// форм смежных граней; вершина: от форм граней её рёбер. Допуск ставится, если отклонение больше половины модельной
/// точности (меньшее — шум вычисления), и только повышается. Предел — бюджет (MaxEntityTolerance) проверяет вызывающий.
/// Замер: ступенька 5e-8 мм (меньше модельной точности) давала точный результат с рёбрами в 1e-7 мм от поверхностей при
/// допуске 0 — «Success», но проверка геометрии — 3 ошибки curve-off-surface.
/// </summary>
public static class EntityTolerance
{
    public static double Assign(TSolid s)
    {
        double half = ToleranceContext.Current.ModelAccuracy / 2, worst = 0;
        var shapeOf = new Dictionary<TFace, Ssi.Shape?>(ReferenceEqualityComparer.Instance);
        Ssi.Shape? Shape(TFace f) { if (!shapeOf.TryGetValue(f, out var sh)) shapeOf[f] = sh = ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)); return sh; }
        var vdev = new Dictionary<TVertex, double>(ReferenceEqualityComparer.Instance);
        foreach (var e in s.Edges)
        {
            var shapes = e.CoEdges.Select(ce => ce.Loop?.Face).OfType<TFace>().Distinct().Select(Shape).OfType<Ssi.Shape>().ToList();
            if (shapes.Count == 0) continue;
            double dev = 0;
            var c = e.Curve;
            var pts = new List<Vector3d> { e.V0.P, e.V1.P };
            if (c != null) for (int k = 1; k <= 5; k++) pts.Add(c.PointAt(c.T0 + (c.T1 - c.T0) * k / 6));
            foreach (var sh in shapes) foreach (var p in pts) dev = Math.Max(dev, OperandTolerance.Distance(sh, p));
            if (dev > half) e.Tolerance = Math.Max(e.Tolerance, dev * (1 + 1e-9));
            worst = Math.Max(worst, dev);
            foreach (var v in new[] { e.V0, e.V1 })
            {
                double dv = shapes.Max(sh => OperandTolerance.Distance(sh, v.P));
                vdev[v] = Math.Max(vdev.GetValueOrDefault(v), dv);
            }
        }
        foreach (var (v, dv) in vdev)
        {
            if (dv > half) v.Tolerance = Math.Max(v.Tolerance, dv * (1 + 1e-9));
            worst = Math.Max(worst, dv);
        }
        return worst;
    }
}
