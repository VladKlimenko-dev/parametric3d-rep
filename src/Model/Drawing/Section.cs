using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Drawing;

/// <summary>
/// k156 (D5): РАЗРЕЗ ГОСТ 2.305. Секущая плоскость — плоскость вида (нормаль — к наблюдателю) на глубине Depth; часть тела между
/// наблюдателем и плоскостью удаляется точной булевой (вычитание полупространства — бруска за плоскостью к наблюдателю),
/// оставшееся проецируется как обычный вид. Грани, лежащие в секущей плоскости, — фигура сечения: штриховка ГОСТ 2.306
/// (тонкие линии под 45°, шаг 2–10 мм листа).
/// </summary>
public static class SectionCut
{
    /// <summary>Разбор: разрезов построено.</summary>
    public static long Built;

    /// <summary>Тела после разреза и отрезки контура фигуры сечения (координаты вида). Тело, которое плоскость не задевает со
    /// стороны наблюдателя, остаётся как есть.</summary>
    public static (List<TSolid> Bodies, List<(Vector2d A, Vector2d B)> Boundary, List<string> Problems) Cut(IReadOnlyList<TSolid> bodies, ViewFrame view, double depth)
    {
        var w = view.Toward;
        var problems = new List<string>();
        // габарит всех тел (по вершинам сетки)
        var pts = bodies.SelectMany(b => Kernel.Tess.Tessellator.Tessellate(b).Vertices.Select(v => (Vector3d)v.P)).ToList();
        if (pts.Count == 0) return (bodies.ToList(), new(), problems);
        var mn = new Vector3d(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
        var mx = new Vector3d(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
        double size = (mx - mn).Length + 10;
        var centre = (mn + mx) / 2;
        // брусок: квадрат 2·size в секущей плоскости (центр — проекция центра тел), выдавлен к наблюдателю на 2·size
        var o = centre - w * (Vector3d.Dot(centre, w) - depth);
        var plane = new Frame3(o, view.Right, view.Up);
        double h = size;
        var c0 = plane.ToWorld(-h, -h); var c1 = plane.ToWorld(h, -h); var c2 = plane.ToWorld(h, h); var c3 = plane.ToWorld(-h, h);
        var profile = ProfileBuilder.Build(plane, new List<ICurve3> { new LineCurve3(c0, c1), new LineCurve3(c1, c2), new LineCurve3(c2, c3), new LineCurve3(c3, c0) });
        var res = new List<TSolid>();
        foreach (var b in bodies)
        {
            var bp = Kernel.Tess.Tessellator.Tessellate(b).Vertices.Select(v => Vector3d.Dot((Vector3d)v.P, w)).ToList();
            if (bp.Count == 0 || bp.Max() <= depth + 1e-9) { res.Add(b); continue; }   // тело целиком за плоскостью
            if (bp.Min() >= depth - 1e-9) continue;                                     // целиком перед плоскостью — удалено
            var tool = ExtrudeOp.Build(profile, w * (2 * size));
            var list = new List<TSolid> { b };
            var warnings = new List<string>();
            try { BooleanOp.Subtract(list, tool, warnings); res.AddRange(list); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { problems.Add(ex.Message); res.Add(b); }
        }
        // фигура сечения: грани в секущей плоскости (плоские, нормаль вдоль взгляда, точки на глубине depth)
        var boundary = new List<(Vector2d, Vector2d)>();
        foreach (var b in res)
            foreach (var f in b.Faces)
            {
                if (GeometryAudit.AuthoritativeSurface(f) is not Kernel.Surfaces.PlaneSurface ps) continue;
                if (Math.Abs(Math.Abs(Vector3d.Dot(ps.F.W.Normalized(), w)) - 1) > 1e-9) continue;
                if (Math.Abs(Vector3d.Dot(ps.F.Origin, w) - depth) > 1e-6 * Math.Max(1, size)) continue;
                foreach (var e in f.Edges)
                {
                    if (e.Curve == null) continue;
                    var c = e.Curve; int n = c is LineCurve3 ? 1 : 48;
                    var prev = view.Project(c.PointAt(c.T0));
                    for (int k = 1; k <= n; k++) { var q = view.Project(c.PointAt(c.T0 + (c.T1 - c.T0) * k / n)); boundary.Add((prev, q)); prev = q; }
                }
            }
        Interlocked.Increment(ref Built);
        return (res, boundary, problems);
    }

    /// <summary>
    /// Штриховка области (контур — отрезки, правило «чёт-нечет»): параллельные линии под углом angle (градусы) с шагом step.
    /// </summary>
    public static List<(Vector2d A, Vector2d B)> Hatch(List<(Vector2d A, Vector2d B)> boundary, double angleDeg, double step)
    {
        var res = new List<(Vector2d, Vector2d)>();
        if (boundary.Count == 0 || step <= 0) return res;
        double a = angleDeg * Math.PI / 180;
        var d = new Vector2d(Math.Cos(a), Math.Sin(a)); var n = new Vector2d(-d.Y, d.X);
        double lo = boundary.Min(s => Math.Min(Vector2d.Dot(s.A, n), Vector2d.Dot(s.B, n)));
        double hi = boundary.Max(s => Math.Max(Vector2d.Dot(s.A, n), Vector2d.Dot(s.B, n)));
        for (double t = Math.Ceiling(lo / step) * step; t <= hi; t += step)
        {
            var xs = new List<double>();
            foreach (var (p, q) in boundary)
            {
                double fp = Vector2d.Dot(p, n) - t, fq = Vector2d.Dot(q, n) - t;
                if ((fp < 0) == (fq < 0)) continue;   // полуоткрытое правило: концы на линии считаются один раз
                double u = fp / (fp - fq);
                var x = p + (q - p) * u;
                xs.Add(Vector2d.Dot(x, d));
            }
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > 1e-9) res.Add((d * xs[i] + n * t, d * xs[i + 1] + n * t));
        }
        return res;
    }

    /// <summary>Площадь области по штриховке нулевого угла с малым шагом — для проверок (ширина × шаг).</summary>
    public static double Area(List<(Vector2d A, Vector2d B)> boundary, double step = 0.01) => Hatch(boundary, 0, step).Sum(s => (s.B - s.A).Length) * step;
}
