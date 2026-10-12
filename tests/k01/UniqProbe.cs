using OpenTK.Mathematics;
using Parametric3D.Kernel.Geometry;

static partial class Program
{
    /// <summary>04.7 замер: две близкие ветви — наклонная плоскость чуть ниже касания к верху тора (две замкнутые кривые рядом).</summary>
    static void UniqProbe()
    {
        foreach (double eps in (Environment.GetEnvironmentVariable("UNIQEPS") ?? "1e-2,3e-3,1e-3,1e-4").Split(",").Select(double.Parse))
            foreach (double tilt in (Environment.GetEnvironmentVariable("UNIQTILT") ?? "1e-4,1e-3,0.1").Split(",").Select(double.Parse))
            {
                var n = new Vector3d(Math.Sin(tilt), 0, Math.Cos(tilt));
                var tor = new Ssi.Torus(Vector3d.Zero, Vector3d.UnitZ, 10, 2);
                // плоскость с нормалью n, касающаяся тора сверху (опорная точка тора по n), опущенная на eps
                double support = 10 * Math.Sin(tilt) + 2;   // max n·x по тору
                var pl = new Ssi.Plane(n * (support - eps), n);
                var r = Ssi.Intersect(pl, tor, Vector3d.Zero, 13);
                double len = r.Curves.Sum(c => c.Length);
                Console.WriteLine($"UNIQ eps {eps:G2} tilt {tilt}: kind {r.Kind} curves {r.Curves.Count} total length {len:F4} uncertified {r.Uncertified} types {string.Join(",", r.Curves.Select(c => c.GetType().Name).Distinct())}");
            }
    }
}
