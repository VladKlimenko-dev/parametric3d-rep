using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn02UvMetricChecks()
    {
        Check("KRN02-UV-METRIC-PHYSICAL-TOLERANCE", () =>
        {
            // 02.5/03.9: ДОПУСК ГРАНИЦЫ ГРАНИ — В МИЛЛИМЕТРАХ НА ПОВЕРХНОСТИ, а не в параметрах. Точки на поверхности в k
            // допусков от края грани (по поверхности поперёк края: вдоль образующей конуса и цилиндра, по меридиану шара) при
            // k = 0.5, 1.5, 3: расстояние до границы = k допусков (до 1e-3 отн.); «на границе» (null) — только при k = 0.5.
            // Конусы с полууглом 16.7° и 60° (у второго ошибка метрики вдвое), цилиндр и шар — контроль.
            var fails = new List<string>();
            double tol = Parametric3D.Kernel.Analytic.AnalyticFace.Tol;
            var sph = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 10) };
            BooleanOp.Subtract(sph, PrimitiveOp.Cylinder(new Vector3d(0, 0, -20), 4, 40), new List<string>());
            var bodies = new (string Name, TSolid Body)[]
            {
                ("cone 16.7°", PrimitiveOp.Cone(Vector3d.Zero, 10, 1, 30)),
                ("cone 60°", PrimitiveOp.Cone(Vector3d.Zero, 20, 20 - 10 * Math.Sqrt(3), 10)),
                ("cylinder", PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30)),
                ("sphere with a hole", sph[0]),
            };
            foreach (var (name, body) in bodies)
            {
                int checkedPts = 0;
                foreach (var f in body.Faces)
                {
                    var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
                    if (sh is Parametric3D.Kernel.Geometry.Ssi.Plane) continue;
                    var af = Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out var why);
                    if (af == null) { fails.Add($"{name}: {why}"); continue; }
                    var srf = GeometryAudit.AuthoritativeSurface(f)!;
                    foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
                    {
                        var c = ce.Edge.Curve; if (c is not Parametric3D.Kernel.Curves.ArcCurve3) continue;
                        foreach (double fr in new[] { 0.21, 0.67 })
                        {
                            var e = c.PointAt(c.T0 + (c.T1 - c.T0) * fr);
                            // направление по поверхности поперёк края: касательная поверхности ⟂ касательной ребра
                            var n = srf.NormalAt(srf.ProjectToUV(e)).Normalized(); var t = c.TangentAt(c.T0 + (c.T1 - c.T0) * fr).Normalized();
                            var across = Vector3d.Cross(n, t).Normalized();
                            foreach (double k in new[] { 0.5, 1.5, 3.0 })
                                foreach (double sgn in new[] { 1.0, -1.0 })
                                {
                                    // шаг по поверхности длиной k·tol: малыми шагами с проекцией (кривизна учтена)
                                    var q = e; int m = 16;
                                    for (int i = 0; i < m; i++)
                                    {
                                        var nn = srf.NormalAt(srf.ProjectToUV(q)).Normalized(); var dir = (across - nn * Vector3d.Dot(across, nn)).Normalized() * sgn;
                                        q = af.ProjectTo(q + dir * (k * tol / m));
                                    }
                                    double bd = af.BoundaryDistance(q) / tol;
                                    var cont = af.Contains(q);
                                    checkedPts++;
                                    if (Math.Abs(bd - k) > 1e-3 * k) fails.Add($"{name}: boundary distance {bd:G6}·tol at {k}·tol");
                                    if (k < 1 ? cont != null : cont == null) fails.Add($"{name}: contains {cont?.ToString() ?? "null"} at {k}·tol");
                                }
                        }
                    }
                }
                if (checkedPts == 0) fails.Add($"{name}: no points checked");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails.Take(8)));
        });
    }
}
