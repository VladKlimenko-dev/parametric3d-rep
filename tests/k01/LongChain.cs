using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-08 (08.10) и вопрос пользователя «ухудшается ли точность детали с числом операций»: длинные цепочки с объёмом в
/// замкнутом виде. На каждом десятом шаге — отклонение объёма от формулы против заявленной границы, наибольший допуск рёбер и
/// вершин, и в конце — что ни то ни другое не растёт с длиной цепочки сверх линейного накопления округления.
/// </summary>
static partial class Program
{
    internal sealed record ChainPoint(int Step, double Error, double Bound, double MaxTol, int Faces, string Guarantee);

    /// <summary>Плита 200×200×10 и n отверстий насквозь, наклонённых на 0–25° (сечение — эллипс: объём π r² t / cos α);
    /// между отверстиями — бобышки-цилиндры сверху и полусферические лунки (центр на верхней грани).</summary>
    internal static List<ChainPoint> LongChain(int n, int seed)
    {
        var rnd = new Random(seed);
        var bodies = new List<TSolid> { Box(0, 0, 0, 200, 200, 10) };
        double vExact = 200.0 * 200 * 10;
        var pts = new List<ChainPoint>();
        int cols = (int)Math.Ceiling(Math.Sqrt(n)); double pitch = 190.0 / cols;
        var w = new List<string>();
        for (int k = 0; k < n; k++)
        {
            double cx = 5 + pitch * (k % cols + 0.5), cy = 5 + pitch * (k / cols + 0.5);
            int kind = k % 3;
            if (kind == 0)
            {
                // наклонное отверстие насквозь
                double r = 1.5 + rnd.NextDouble(), al = rnd.NextDouble() * 25 * Math.PI / 180, az = rnd.NextDouble() * 2 * Math.PI;
                var dir = new Vector3d(Math.Sin(al) * Math.Cos(az), Math.Sin(al) * Math.Sin(az), Math.Cos(al));
                double L = 40; var c0 = new Vector3d(cx, cy, 5) - dir * (L / 2);
                var cyl = PrimitiveOp.Cylinder(Vector3d.Zero, r, L);
                // ось Z → dir: поворот вокруг Z×dir
                var ax = Vector3d.Cross(Vector3d.UnitZ, dir); double deg = al * 180 / Math.PI;
                if (ax.Length > 1e-12) cyl = TransformOp.Copy(cyl, TransformOp.Rotate(Vector3d.Zero, ax.Normalized(), deg));
                cyl = TransformOp.Copy(cyl, TransformOp.Move(c0));
                BooleanOp.Subtract(bodies, cyl, w);
                vExact -= Math.PI * r * r * 10 / Math.Cos(al);
            }
            else if (kind == 1)
            {
                // бобышка сверху (цилиндр, стоящий на верхней грани)
                double r = 1.5 + rnd.NextDouble(), hgt = 2 + 3 * rnd.NextDouble();
                BooleanOp.Unite(bodies, PrimitiveOp.Cylinder(new Vector3d(cx, cy, 10), r, hgt), w);
                vExact += Math.PI * r * r * hgt;
            }
            else
            {
                // полусферическая лунка: центр шара на верхней грани
                double r = 1.5 + rnd.NextDouble();
                BooleanOp.Subtract(bodies, PrimitiveOp.Sphere(new Vector3d(cx, cy, 10), r), w);
                vExact -= 2.0 / 3 * Math.PI * r * r * r;
            }
            if ((k + 1) % 10 == 0 || k == n - 1)
            {
                var b = bodies.Single();
                var m = AnalyticMeasure.Volume(b);
                double tol = b.Edges.Select(e => Math.Max(e.Tolerance, Math.Max(e.V0.Tolerance, e.V1.Tolerance))).DefaultIfEmpty(0).Max();
                pts.Add(new ChainPoint(k + 1, m.Value - vExact, m.Error, tol, b.Faces.Count(), b.Guarantee.ToString()));
            }
        }
        return pts;
    }

    /// <summary>Тело поворачивается и возвращается обратным поворотом m раз: смещение вершин и изменение объёма.</summary>
    internal static (double Drift, double DVol) TransformRoundTrips(int m, int seed)
    {
        var rnd = new Random(seed);
        var b0 = Box(0, 0, 0, 60, 40, 20);
        var bl = new List<TSolid> { b0 }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -1), 8, 22), new List<string>());
        BooleanOp.Subtract(bl, PrimitiveOp.Sphere(new Vector3d(10, 10, 20), 5), new List<string>());
        var start = bl[0]; var b = start; double v0 = AnalyticVolume.Of(start) ?? double.NaN;
        for (int i = 0; i < m; i++)
        {
            var at = new Vector3d(rnd.NextDouble() * 100 - 50, rnd.NextDouble() * 100 - 50, rnd.NextDouble() * 100 - 50);
            var ax = new Vector3d(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5).Normalized();
            double deg = rnd.NextDouble() * 360;
            b = TransformOp.Copy(b, TransformOp.Rotate(at, ax, deg));
            b = TransformOp.Copy(b, TransformOp.Rotate(at, ax, -deg));
        }
        double drift = 0;
        var p0 = start.Vertices.Select(v => v.P).ToList(); var p1 = b.Vertices.Select(v => v.P).ToList();
        for (int i = 0; i < Math.Min(p0.Count, p1.Count); i++) drift = Math.Max(drift, (p0[i] - p1[i]).Length);
        return (drift, (AnalyticVolume.Of(b) ?? double.NaN) - v0);
    }

    static void LongChainChecks()
    {
        Check("KRN08-LONG-CHAIN-ACCURACY", () =>
        {
            var pts = LongChain(120, 31);
            foreach (var p in pts) Log(new { test = "KRN08-LONG-CHAIN", p.Step, p.Error, p.Bound, p.MaxTol, p.Faces, p.Guarantee });
            var bad = pts.Where(p => !(Math.Abs(p.Error) <= p.Bound)).ToList();
            Require(bad.Count == 0, "volume outside the declared bound: " + string.Join("; ", bad.Select(p => $"step {p.Step}: error {p.Error:G3} bound {p.Bound:G3}")));
            Require(pts.All(p => p.Guarantee == "ExactAnalytic"), "a step left the exact route: " + string.Join(",", pts.Where(p => p.Guarantee != "ExactAnalytic").Select(p => p.Step + ":" + p.Guarantee)));
            // допуск рёбер не растёт с длиной цепочки (каждая операция строит рёбра заново по точной геометрии)
            double t10 = pts[0].MaxTol, tEnd = pts[^1].MaxTol;
            Require(tEnd <= Math.Max(2 * t10, 1e-7), $"edge tolerance grows: {t10:G3} at step 10 → {tEnd:G3} at step {pts[^1].Step}");
            // отклонение объёма — на уровне округления, не накапливается: на последнем шаге не больше 1e-9 от объёма
            Require(Math.Abs(pts[^1].Error) <= 1e-9 * 400000, $"final volume error {pts[^1].Error:G3}");
        });
        Check("KRN08-TRANSFORM-ROUND-TRIPS", () =>
        {
            var (drift, dv) = TransformRoundTrips(200, 5);
            Log(new { test = "KRN08-TRANSFORM-ROUND-TRIPS", drift, dv });
            Require(drift <= 1e-9 && Math.Abs(dv) <= 1e-8, $"after 200 rotations there and back: vertex drift {drift:G3} mm, volume change {dv:G3} mm³");
        });
    }
}
