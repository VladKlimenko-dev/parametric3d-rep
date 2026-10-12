using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>02.5/03.9: допуск границы конической грани в мм на разных высотах (около вершины и у основания).</summary>
    static void ConeMetricProbe()
    {
        foreach (var (name, body) in new (string, TSolid)[] { ("frustum r10→r1 h30", PrimitiveOp.Cone(Vector3d.Zero, 10, 1, 30)), ("frustum r10→r0.01 h30", PrimitiveOp.Cone(Vector3d.Zero, 10, 0.01, 30)) })
        {
            var cf = body.Faces.First(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Cone);
            var af = Parametric3D.Kernel.Analytic.AnalyticFace.Of(cf, out var why);
            if (af == null) { Console.WriteLine($"CONE {name}: {why}"); continue; }
            double tol = Parametric3D.Kernel.Analytic.AnalyticFace.Tol;
            var shp = (Parametric3D.Kernel.Geometry.Ssi.Cone)Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(cf))!;
            var W = shp.Axis.Normalized(); var apex = shp.Apex;
            // точки у верхней и нижней окружностей, сдвинутые вдоль образующей на k·допуск внутрь и наружу
            foreach (var (edgeName, z) in new[] { ("bottom rim", 0.0), ("top rim", 30.0) })
            {
                var p0 = new Vector3d(Math.Sqrt(0.5), Math.Sqrt(0.5), 0); // направление
                // точка ребра: на окружности высоты z
                double rz = z == 0 ? 10 : (name.Contains("0.01") ? 0.01 : 1);
                var onEdge = new Vector3d(rz * p0.X, rz * p0.Y, z);
                var gen = (onEdge - apex).Normalized();          // образующая от вершины
                foreach (double k in new[] { -3.0, -0.5, 0.5, 3.0 })
                {
                    // внутрь грани — от кромки к середине: у нижней — к вершине (+gen к вершине = −gen), у верхней — от вершины
                    var dirIn = z == 0 ? (onEdge - apex).Normalized() * Math.Sign(Vector3d.Dot(apex - onEdge, Vector3d.UnitZ)) : (onEdge - apex).Normalized() * -Math.Sign(Vector3d.Dot(onEdge - apex, Vector3d.UnitZ));
                    var q = onEdge + dirIn * (k * tol);
                    q = af.ProjectTo(q);
                    Console.WriteLine($"CONE {name} {edgeName} k {k}: contains {af.Contains(q)} boundaryDist/tol {af.BoundaryDistance(q) / tol:G4} true {Math.Abs(k):G2}");
                }
            }
        }
    }
}
