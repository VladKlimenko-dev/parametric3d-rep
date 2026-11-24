using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ПРИКЛЕИВАНИЕ ИМПОРТИРОВАННЫХ РЁБЕР К NURBS-ПОВЕРХНОСТЯМ.
///
/// Parasolid XT → STEP переносит границу как B-сплайн, слегка не лежащий на B-сплайновой поверхности.
/// Валидация «curve off surface» и сеточная валидация при этом расходятся: поверхность мешится точно, а
/// кривая ребра — нет. Здесь кривые импортированных рёбер заменяются ломаной, спроецированной на
/// авторитетную NURBS-поверхность, только если отклонение маленькое и не ломает концевые точки.
/// </summary>
internal static class NurbsEdgeSnap
{
    static readonly bool Off = Environment.GetEnvironmentVariable("P3D_NURBSEDGESNAP") == "0";
    public static long Snapped;

    public static void Apply(TSolid solid, List<string>? warnings)
    {
        if (Off) return;
        foreach (var e in solid.Edges.ToList())
        {
            if (e.Curve is not SplineCurve3 && e.Curve is not IntersectionCurve3) continue;
            if (e.CoEdges.Count() != 2) continue;

            var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().GroupBy(f => f.Id).Select(g => g.First()).ToList();
            if (faces.Count != 2) continue;
            var sa = GeometryAudit.AuthoritativeSurface(faces[0]);
            var sb = GeometryAudit.AuthoritativeSurface(faces[1]);
            var s = sa as NurbsSurface ?? sb as NurbsSurface;
            if (s == null) continue;

            // Отклонение в середине.
            var mid = e.Curve.PointAt(0.5 * (e.Curve.T0 + e.Curve.T1));
            var proj = s.ProjectToUV(mid);
            var onSurf = s.PointAt(proj);
            if ((onSurf - mid).Length > 3.0) continue;   // кривая не на этой поверхности

            // Сэмплируем и проецируем; концевые точки сохраняем, чтобы не разошлись вершины.
            int n = 9;
            var pts = new List<Vector3d>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double t = e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / n;
                var p = e.Curve.PointAt(t);
                if (i == 0) { pts.Add(e.V0.P); continue; }
                if (i == n) { pts.Add(e.V1.P); continue; }
                var uv = s.ProjectToUV(p);
                var q = s.PointAt(uv);
                if ((q - p).Length > 0.5) { pts.Add(p); }   // проекция не надёжна — сохраняем исходную точку
                else pts.Add(q);
            }

            double dev = 0;
            for (int i = 0; i <= n; i++)
            {
                double t = e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / n;
                var old = e.Curve.PointAt(t);
                var neu = i == 0 ? e.V0.P : i == n ? e.V1.P : pts[i];
                dev = Math.Max(dev, (old - neu).Length);
            }
            if (dev > 3.0) continue;

            var curve = new PolylineCurve3(pts.ToArray());
            e.Curve = curve;
            e.PolylineCache = null;
            e.Tolerance = Math.Max(e.Tolerance, dev);
            Interlocked.Increment(ref Snapped);
            if (Environment.GetEnvironmentVariable("P3D_NURBSEDGESNAPDBG") == "1")
                Console.Error.WriteLine($"NurbsEdgeSnap edge {e.Id} dev={dev:G4}");
        }
    }
}
