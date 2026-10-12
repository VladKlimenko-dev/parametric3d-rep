using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

/// <summary>
/// Ворота g.11 (KRN-09): ОТОБРАЖЕНИЕ ОТРАЖАЕТ ПРИНЯТУЮ ГЕОМЕТРИЮ. Сетка отображения (то, что уходит в GPU, во float) каждого
/// тела каталога g.14 — замкнута: каждая сторона треугольника имеет пару по ТЕМ ЖЕ float-координатам (нет трещин), и объём
/// сетки близок к принятому объёму тела (ошибка гранения ограничена). Заявленная область — |координаты| ≤ 1000 мм (шаг float
/// там ≤ 6e-5 мм); тела проверяются на месте и перенесёнными точно (поворот + сдвиг до 1000 мм). Дальше — только замер.
/// </summary>
static partial class Program
{
    static (int Unpaired, int Tris, double Volume) DisplayMeshStats(TSolid b)
    {
        if (Environment.GetEnvironmentVariable("G11DBG") == "3") QMesher.Debug = m => Console.WriteLine("   qmesher: " + m);
        var md = Tessellator.Tessellate(b);
        QMesher.Debug = null;
        if (Environment.GetEnvironmentVariable("G11DBG") == "1") foreach (var bf in md.BrokenFaces) Console.WriteLine($"   broken face {bf.Face}: {bf.Why}");
        var count = new Dictionary<(Vector3, Vector3), int>();
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                var a = md.Vertices[md.Indices[i + k]].P; var c = md.Vertices[md.Indices[i + (k + 1) % 3]].P;
                if (a == c) continue;
                var key = (a, c); var rev = (c, a);
                if (count.TryGetValue(rev, out int n) && n > 0) count[rev] = n - 1; else count[key] = count.GetValueOrDefault(key) + 1;
            }
        if (Environment.GetEnvironmentVariable("G11DBG") == "3") QMesher.Debug = m => Console.WriteLine("   qmesher: " + m);
        if (Environment.GetEnvironmentVariable("G11DBG") == "1")
            foreach (var kv in count.Where(kv => kv.Value > 0).Take(8)) Console.WriteLine($"   unpaired {kv.Key.Item1.X:R},{kv.Key.Item1.Y:R},{kv.Key.Item1.Z:R} -> {kv.Key.Item2.X:R},{kv.Key.Item2.Y:R},{kv.Key.Item2.Z:R}");
        return (count.Values.Sum(), md.Indices.Length / 3, MassProps.Volume(md));
    }

    static List<string> G11Display(double reach, out int checkedBodies)
    {
        var fails = new List<string>(); checkedBodies = 0;
        var placements = new[] { (Axis: Vector3d.UnitZ, Deg: 0.0, Move: Vector3d.Zero), (new Vector3d(1, 2, 3), 41.0, new Vector3d(reach, -0.7 * reach, 0.4 * reach)) };
        foreach (var (name, build, _) in G14Catalogue())
        {
            Part p;
            try { p = build(); p.Apply(p.Compute(null)); } catch { continue; }
            if (p.Features.Any(f => f.Error != null)) continue;
            foreach (var b0 in p.Bodies)
                foreach (var (ax, deg, mv) in placements)
                {
                    var b = deg == 0 && mv == Vector3d.Zero ? b0 : TopologyTransform.Apply(b0, Compose(TransformOp.Rotate(Vector3d.Zero, ax, deg), mv));
                    if (b == null) continue;     // многогранное по устройству — точно не переносится
                    checkedBodies++;
                    if (Environment.GetEnvironmentVariable("G11DBG") == "4" && name.StartsWith("hole"))
                        foreach (var fc in b.Faces)
                        {
                            var fs = fc.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface; var t = fs?.Facets.ReadView.FirstOrDefault();
                            string sgn = "-";
                            if (fs != null && fs.Source != null && t is { } tt) { var g = (tt.A + tt.B + tt.C) / 3; var nt = Vector3d.Cross(tt.B - tt.A, tt.C - tt.A); sgn = $"{Vector3d.Dot(nt, fs.Source.NormalAt(fs.Source.ProjectToUV(g))):+0.00;-0.00} faceN·NormalAt {Vector3d.Dot(nt, fc.NormalAt(g)):+0.00;-0.00} disp {fs.Display != null}"; }
                            Console.WriteLine($"   @{mv.Length:0} face {fc.Tag} rev {fc.Reversed} src {fs?.Source?.GetType().Name} {sgn}");
                        }
                    var (unp, tris, vm) = DisplayMeshStats(b);
                    double va = AnalyticVolume.Of(b) ?? (b0.Exact != null && b0.ExactAuthoritative ? b0.Exact.Solid.VolumeMm3().ToDouble() : double.NaN);
                    if (unp > 0 && Environment.GetEnvironmentVariable("G11DBG") == "2")
                        Console.WriteLine($"  {name}: conv {b0.TopologyConvention}->{b.TopologyConvention} faces {string.Join(",", b.Faces.Select(f => (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? "F:" + fs.Source?.GetType().Name : f.Surface.GetType().Name) + "/" + f.Loops.Count))} report {TopologyConventions.Normalize(TopologyClone.Deep(b0))}");
                    if (unp > 0) fails.Add($"{name} @{mv.Length:0}: {unp} unpaired display edges of {tris * 3}");
                    if (!double.IsNaN(va) && Math.Abs(vm - va) > 1e-2 * Math.Abs(va)) fails.Add($"{name} @{mv.Length:0}: display volume {vm:G8} vs accepted {va:G8}");
                }
        }
        return fails;
    }

    static TransformOp.Xform Compose(TransformOp.Xform r, Vector3d move) => new(p => r.Point(p) + move, r.Direction, r.Flips);

    static void G11Checks()
    {
        Check("G11-DISPLAY-CLOSED-IN-DECLARED-REGION", () =>
        {
            var fails = G11Display(1000, out int n);
            Log(new { test = "G11-DISPLAY-CLOSED-IN-DECLARED-REGION", bodies = n, fails = fails.Count, first = fails.Take(5) });
            Require(fails.Count == 0 && n >= 60, $"{fails.Count} of {n}: " + string.Join("; ", fails.Take(6)));
        });
    }
}
