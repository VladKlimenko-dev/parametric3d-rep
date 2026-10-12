using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// G3. МОДЕЛЬ ДОПУСКОВ: допуск каждого ребра и вершины — из ИЗМЕРЕННЫХ отклонений, а не одно
/// число на модель.
///  • ребро: наибольшее из — расхождения концов кривой с вершинами, отклонения кривой от
///    поверхностей её граней (по кривым в параметрах граней, G1), прежнего допуска ребра;
///  • вершина: наибольшее из — расстояния до поверхностей сходящихся граней, допусков её рёбер.
/// Допуск никогда не уменьшается ниже уже заданного (его могла выставить операция или импорт).
/// Проверка (Check) отвечает на вопрос, согласовано ли тело в пределах своих допусков: точка
/// ребра лежит на каждой его грани не дальше допуска ребра, вершина — не дальше своего.
/// </summary>
public static class Tolerances
{
    public sealed class Report
    {
        public int Edges, Vertices, EdgesRaised, VerticesRaised;
        public double MaxEdge, MaxVertex;
        public override string ToString() =>
            $"edges {Edges} (raised {EdgesRaised}, max {MaxEdge:E1} mm), vertices {Vertices} (raised {VerticesRaised}, max {MaxVertex:E1} mm)";
    }

    /// <summary>Не меньше этого (сетка точного тела 1e-5 мм: её отклонения не считаются ошибкой).</summary>
    private static double _gFloor = 1e-7;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.ToleranceFloor), если он задан; иначе общее по умолчанию.</summary>
    public static double Floor
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ToleranceFloor : _gFloor;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gFloor, value, "Tolerances.Floor");
    }

    public static Report Compute(TSolid solid)
    {
        var rep = new Report();
        PCurves.Build(solid);
        foreach (var e in solid.Edges)
        {
            rep.Edges++;
            double t = Math.Max(Floor, e.Tolerance);
            var c = e.Curve;
            Vector3d a = c.PointAt(c.T0), b = c.PointAt(c.T1);
            bool closed = ReferenceEquals(e.V0, e.V1);
            double ends = closed ? Math.Max((a - e.V0.P).Length, (b - e.V0.P).Length)
                                 : Math.Min(Math.Max((a - e.V0.P).Length, (b - e.V1.P).Length), Math.Max((a - e.V1.P).Length, (b - e.V0.P).Length));
            t = Math.Max(t, ends);
            foreach (var ce in e.CoEdges)
            {
                t = Math.Max(t, ce.PCurveDeviation);
                // И прямое расстояние точек ребра до поверхности грани (по проекции): кривая в
                // параметрах мерит отклонение в своих точках, а проверка — в точках ребра.
                var f = ce.Loop?.Face; var sf = f == null ? null : GeometryAudit.AuthoritativeSurface(f);
                if (sf != null)
                    for (int i = 0; i <= 32; i++) { var p = PCurves.At(ce, i / 32.0); t = Math.Max(t, (sf.PointAt(sf.ProjectToUV(p)) - p).Length); }
            }
            if (t > e.Tolerance * (1 + 1e-12) + 1e-15) rep.EdgesRaised++;
            e.Tolerance = t;
            rep.MaxEdge = Math.Max(rep.MaxEdge, t);
        }
        var byVertex = new Dictionary<TVertex, List<TEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in solid.Edges)
            foreach (var v in new[] { e.V0, e.V1 }.Distinct())
            {
                if (!byVertex.TryGetValue(v, out var l)) byVertex[v] = l = new List<TEdge>();
                l.Add(e);
            }
        foreach (var (v, es) in byVertex)
        {
            rep.Vertices++;
            double t = Math.Max(Floor, v.Tolerance);
            foreach (var e in es) t = Math.Max(t, e.Tolerance);
            foreach (var f in es.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct())
            {
                var s = GeometryAudit.AuthoritativeSurface(f);
                if (s == null) continue;
                t = Math.Max(t, (s.PointAt(s.ProjectToUV(v.P)) - v.P).Length);
            }
            if (t > v.Tolerance * (1 + 1e-12) + 1e-15) rep.VerticesRaised++;
            v.Tolerance = t;
            rep.MaxVertex = Math.Max(rep.MaxVertex, t);
        }
        return rep;
    }

    /// <summary>
    /// Согласованность тела в пределах его допусков: (ребро, грань) с точками ребра дальше
    /// допуска ребра от поверхности грани и вершины дальше своего допуска от поверхностей.
    /// </summary>
    public static List<string> Check(TSolid solid, int samples = 16)
    {
        var bad = new List<string>();
        foreach (var e in solid.Edges)
            foreach (var ce in e.CoEdges)
            {
                var f = ce.Loop?.Face; if (f == null) continue;
                var s = GeometryAudit.AuthoritativeSurface(f); if (s == null) continue;
                double worst = 0;
                for (int i = 0; i <= samples; i++)
                {
                    var p = PCurves.At(ce, (double)i / samples);
                    worst = Math.Max(worst, (s.PointAt(s.ProjectToUV(p)) - p).Length);
                }
                if (worst > e.Tolerance * 1.001 + 1e-12) bad.Add($"edge {e.Id} on face {f.Id}: {worst:E2} mm > tolerance {e.Tolerance:E2}");
            }
        var incident = new Dictionary<TVertex, List<TEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in solid.Edges) foreach (var v0 in new[] { e.V0, e.V1 }.Distinct()) { if (!incident.TryGetValue(v0, out var l)) incident[v0] = l = new List<TEdge>(); l.Add(e); }
        foreach (var (v, es) in incident)
            foreach (var f in es.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct())
            {
                var s = GeometryAudit.AuthoritativeSurface(f); if (s == null) continue;
                double d = (s.PointAt(s.ProjectToUV(v.P)) - v.P).Length;
                if (d > v.Tolerance * 1.001 + 1e-12) bad.Add($"vertex {v.Id} on face {f.Id}: {d:E2} mm > tolerance {v.Tolerance:E2}");
            }
        return bad;
    }
}
