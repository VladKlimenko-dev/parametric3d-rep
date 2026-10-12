using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>
/// Инструмент, которого нет у точного маршрута: призма над замкнутым контуром из двух сплайнов (боковые грани — выдавливание
/// сплайна). С 2026-10-11 тор — в точном маршруте, и проверки, которым нужен многогранный путь аналитического операнда
/// (гранение под политикой, измеренное отклонение фасет), берут эту призму.
/// </summary>
static partial class Program
{
    internal static TSolid SplinePrism(Vector3d c, double rx, double ry, double h, double rotDeg = 0, Vector3d? rotAxis = null)
    {
        Vector3d P(double x, double y) => new(c.X + x, c.Y + y, c.Z - h / 2);
        var up = new Parametric3D.Kernel.Curves.SplineCurve3(new[] { P(-rx, 0), P(-rx, ry * 1.3), P(-rx * 0.3, ry * 1.1), P(rx * 0.6, ry * 1.4), P(rx, 0) });
        var dn = new Parametric3D.Kernel.Curves.SplineCurve3(new[] { P(rx, 0), P(rx * 0.9, -ry * 1.2), P(0, -ry * 1.3), P(-rx, -ry * 0.8), P(-rx, 0) });
        var prof = ProfileBuilder.Build(new Frame3(P(0, 0), Vector3d.UnitX, Vector3d.UnitY), new List<Parametric3D.Kernel.Curves.ICurve3> { up, dn });
        var s = ExtrudeOp.Build(prof, new Vector3d(0, 0, h));
        if (rotDeg != 0) s = TransformOp.Copy(s, TransformOp.Rotate(c, (rotAxis ?? Vector3d.UnitX).Normalized(), rotDeg));
        return s;
    }
}

static partial class Program
{
    /// <summary>Разбор: отклонение фасет от источника — проекцией поверхности и перебором по сетке параметров.</summary>
    static void FacetDevProbe()
    {
        var r = BooleanOp.Evaluate(new List<TSolid> { Box() }, SplinePrism(new Vector3d(30, 20, 20), 8, 5, 8), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
        Console.WriteLine($"{r.Describe()} acc {r.Accuracy}");
        foreach (var b in r.Bodies)
            foreach (var f in b.Faces)
            {
                if (f.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface fs || fs.Source is not { } src || src.IsPlanar) continue;
                double dProj = 0, dBrute = 0; Vector3d worst = default;
                var grid = new List<Vector3d>();
                if (src is Parametric3D.Kernel.Surfaces.ExtrudedSurface ex) for (int i = 0; i <= 4000; i++) for (int j = 0; j <= 40; j++) grid.Add(ex.PointAt(new Vector2d(ex.Base.T0 + (ex.Base.T1 - ex.Base.T0) * i / 4000.0, j / 40.0)));
                foreach (var (a, bb, c) in fs.Facets.ReadView)
                    foreach (var q in new[] { (a + bb + c) / 3, (a + bb) / 2, (bb + c) / 2, (c + a) / 2 })
                    {
                        double d = (src.PointAt(src.ProjectToUV(q)) - q).Length;
                        if (d > dProj) { dProj = d; worst = q; }
                    }
                if (src is Parametric3D.Kernel.Surfaces.ExtrudedSurface ex2) { var dn = ex2.Dir.Normalized(); var b0 = ex2.Base.PointAt(ex2.Base.T0); var w0 = worst - dn * Vector3d.Dot(worst - b0, dn); dBrute = double.MaxValue; for (int i = 0; i <= 200000; i++) { var q = ex2.Base.PointAt(ex2.Base.T0 + (ex2.Base.T1 - ex2.Base.T0) * i / 200000.0); dBrute = Math.Min(dBrute, (q - w0).Length); } }
                Console.WriteLine($"face {f.Id} {src.GetType().Name} facets {fs.Facets.Count} projDev {dProj:G4} at {worst:0.###}; brute dist at that point {dBrute:G4}");
            }
    }
}

static partial class Program
{
    /// <summary>Разбор KRN03-SPHERE-CAP-BY-LOOP «rot intersect»: площади граней по формулам и по сетке.</summary>
    static void SphCapProbe()
    {
        var rx = TransformOp.Rotate(new Vector3d(30, 20, 10), new Vector3d(0.3, 0.7, 0.2), 37);
        TSolid body = TransformOp.Copy(Box(), rx), tool = PrimitiveOp.Sphere(rx.Point(new Vector3d(30, 20, 20)), 15);
        var res = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(body, tool, Parametric3D.Kernel.Analytic.Bool2.Intersect, out var why)!;
        Parametric3D.Kernel.Analytic.PrismBuild.Finish(res, null);
        foreach (var f in res.Faces)
        {
            double ma = 0; int n = 0;
            if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs) foreach (var (a, b, c) in fs.Facets.ReadView) { ma += Vector3d.Cross(b - a, c - a).Length / 2; n++; }
            Console.WriteLine($"face {f.Id} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count} ces {string.Join("/", f.Loops.Select(l => l.CoEdges.Count))} area {AnalyticVolume.FaceArea(f):G8} int {AnalyticVolume.FaceIntegral(f):G8} meshArea {ma:G8} facets {n} hint {f.InteriorHint:0.###}");
        }
    }
}
