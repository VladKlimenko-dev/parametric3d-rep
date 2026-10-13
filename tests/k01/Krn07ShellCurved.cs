using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Объём бруска a×b×c со скруглением всех рёбер радиусом r (формула Штейнера для ядра (a−2r)×(b−2r)×(c−2r)).</summary>
    internal static double RoundedBoxVolume(double a, double b, double c, double r)
    {
        double x = a - 2 * r, y = b - 2 * r, z = c - 2 * r;
        return x * y * z + 2 * (x * y + y * z + x * z) * r + Math.PI * (x + y + z) * r * r + 4 * Math.PI * r * r * r / 3;
    }
    static void Krn07ShellCurvedChecks()
    {
        Check("KRN07-SHELL-CURVED-OFFSET", () =>
        {
            // 07.5: оболочка тела с кривыми гранями — полость смещением всех граней без смены топологии (шар, тор, скругления,
            // цилиндры с прямыми рёбрами). Объём стенки — по формуле: брусок со всеми скруглёнными рёбрами R5 (полость — брусок
            // 56×36×16 со скруглением R3, Штейнер), брусок со скруглёнными вертикальными рёбрами (закрытый и с открытым верхом),
            // шар, тор. Полость точная, тело ExactAnalytic и годное.
            var fails = new List<string>();
            TSolid Rounded(bool all)
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                var es = all ? bl[0].Edges.ToList() : bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).ToList();
                FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), 5, new List<string>());
                return bl[0];
            }
            double k = 4 - Math.PI;
            // усечённый конус: радиус r0 при z0 линейно до r1 при z1 (радиусы — продолжение образующей до z0, z1)
            static double Frustum(double r0, double r1, double z0, double z1) => Math.PI * (z1 - z0) * (r0 * r0 + r0 * r1 + r1 * r1) / 3;
            TSolid Rev(List<Parametric3D.Kernel.Curves.ICurve3> c)
            {
                var t = RevolveOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), c), new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
                TopologyConventions.Normalize(t); return t;
            }
            var cases = new (string Name, Func<TSolid> Make, double T, Vector3d[]? Open, double Want)[]
            {
                ("box, all edges R5, wall 2", () => Rounded(true), 2, null, RoundedBoxVolume(60, 40, 20, 5) - RoundedBoxVolume(56, 36, 16, 3)),
                ("box, vertical edges R5, wall 2", () => Rounded(false), 2, null, (2400 - k * 25) * 20 - (56 * 36 - k * 9) * 16),
                ("box, vertical edges R5, wall 2, open top", () => Rounded(false), 2, new[] { new Vector3d(30, 20, 20) }, (2400 - k * 25) * 20 - (56 * 36 - k * 9) * 18),
                ("sphere R10 (revolve), wall 2", () => Rev(new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), 10, -Math.PI / 2, Math.PI / 2), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 10), new Vector3d(0, 0, -10)) }), 2, null, 4.0 / 3 * Math.PI * (1000 - 512)),
                ("cone frustum R20/R12 h20 (revolve), wall 2", () => Rev(new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 0), new Vector3d(20, 0, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(20, 0, 0), new Vector3d(12, 0, 20)), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(12, 0, 20), new Vector3d(0, 0, 20)), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 20), new Vector3d(0, 0, 0)) }), 2, null,
                    Frustum(20, 12, 0, 20) - Frustum(20 - 0.4 * 2 - 2 * Math.Sqrt(1.16), 12 + 0.4 * 2 - 2 * Math.Sqrt(1.16), 2, 18)),
                ("box, all edges R5, wall 2 outward", () => Rounded(true), -2, null, RoundedBoxVolume(64, 44, 24, 7) - RoundedBoxVolume(60, 40, 20, 5)),
                ("sphere R10 (revolve), wall 2 outward", () => Rev(new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), 10, -Math.PI / 2, Math.PI / 2), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 10), new Vector3d(0, 0, -10)) }), -2, null, 4.0 / 3 * Math.PI * (1728 - 1000)),
                ("torus 20/5 (revolve), wall 1", () => Rev(new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitZ), 5, 0, 2 * Math.PI) }), 1, null, 2 * Math.PI * Math.PI * 20 * (25 - 16)),
            };
            foreach (var (name, make, t, open, want) in cases)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>(); long o0 = DirectRound.OffsetSolids;
                if (Environment.GetEnvironmentVariable("P3D_SHELLDBG") == "1")
                    foreach (var f in bl[0].Faces) Console.WriteLine($"   face {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count}: " + string.Join(" ; ", f.Loops.Select(l => string.Join(",", l.CoEdges.Select(c => $"{c.Edge.Curve.GetType().Name}[{c.Start.P:0.#}->{c.End.P:0.#}]")) + (l.Ordered() == null ? " UNORDERED" : ""))));
                if (Environment.GetEnvironmentVariable("P3D_SHELLDBG") == "1") ShellOp.ShellDebug = m => Console.WriteLine("   shell: " + m);
                bool ok = ShellOp.Hollow(bl, t, open, w);
                ShellOp.ShellDebug = null;
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                Log(new { test = "KRN07-SHELL-CURVED", name, ok, volume = double.IsFinite(v) ? v : -1, want, guarantee = b.Guarantee.ToString(), offset = DirectRound.OffsetSolids - o0, w = string.Join(" | ", w.Take(3)) });
                if (!ok || DirectRound.OffsetSolids == o0) fails.Add($"{name}: not built by offset ({string.Join(" | ", w.Take(3))})");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs formula {want:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
