using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn07ShellOwnThicknessChecks()
    {
        Check("KRN07-SHELL-CURVED-OWN-THICKNESS", () =>
        {
            // 07.5: СВОЯ ТОЛЩИНА ГРАНЕЙ У ТЕЛА С КРИВЫМИ ГРАНЯМИ. Диск r20 h30 (открыт верх): стенка-цилиндр 3, дно 5 —
            // полость — цилиндр r17 высотой 25. Брусок 60×40×20 со скруглёнными вертикальными рёбрами R5 (открыт верх): стенки
            // (плоскости и цилиндры) 2, дно 4 — полость — скруглённый прямоугольник 56×36 R3 высотой 16. Стенка-плоскость 3 при
            // касательных цилиндрах 2 — касание теряется: честный отказ, тело не тронуто.
            var fails = new List<string>();
            TSolid Disk() => ExtrudeOp.Build(ProfileBuilder.Build(Frame3.XY, new ICurve3[] { new ArcCurve3(Frame3.XY, 20, 0, 2 * Math.PI) }), new Vector3d(0, 0, 30));
            TSolid RBox()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>());
                return bl[0];
            }
            double k = 4 - Math.PI;
            var cases = new (string Name, Func<TSolid> Make, double T, Vector3d[] Open, (Vector3d, double)[] Own, double Want)[]
            {
                ("disk: wall 3, bottom 5", Disk, 3, new[] { new Vector3d(0, 0, 30) }, new[] { (new Vector3d(5, 5, 0), 5.0) }, Math.PI * 400 * 30 - Math.PI * 17 * 17 * 25),
                ("disk: wall 2 (default), bottom 6, side own 2.5", Disk, 2, new[] { new Vector3d(0, 0, 30) }, new[] { (new Vector3d(5, 5, 0), 6.0), (new Vector3d(20, 0, 15), 2.5) }, Math.PI * 400 * 30 - Math.PI * 17.5 * 17.5 * 24),
                ("rounded box: walls 2, bottom 4", RBox, 2, new[] { new Vector3d(30, 20, 20) }, new[] { (new Vector3d(30, 20, 0), 4.0) }, (60 * 40 - k * 25) * 20 - (56 * 36 - k * 9) * 16),
            };
            foreach (var (name, make, t, open, own, want) in cases)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>();
                bool ok; try { ok = ShellOp.Hollow(bl, t, open, w, own); } catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                Console.WriteLine($"  {name}: ok {ok}, V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee} | {string.Join(" | ", w.Take(2))}");
                if (!ok || bl.Count != 1 || b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid || !(Math.Abs(v - want) <= 1e-9 * want))
                    fails.Add($"{name}: ok {ok}, V {v:R} vs {want:R}, {b.Guarantee} {SolidValidator.Validate(b).Summary()} | {string.Join(" | ", w.Take(2))}");
            }
            // касание теряется — отказ, тело не тронуто
            {
                var b0 = RBox(); double v0 = AnalyticVolume.Of(b0) ?? double.NaN; var bl = new List<TSolid> { b0 }; var w = new List<string>();
                bool ok = ShellOp.Hollow(bl, 2, new[] { new Vector3d(30, 20, 20) }, w, new[] { (new Vector3d(60, 20, 10), 3.0) });
                Console.WriteLine($"  tangent wall with another thickness: ok {ok} | {string.Join(" | ", w.Take(2))}");
                if (ok || !(Math.Abs((AnalyticVolume.Of(bl[0]) ?? 0) - v0) < 1e-9 * v0)) fails.Add($"tangent wall with another thickness: ok {ok} (must refuse, body untouched)");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void Krn07ShellOpenBlendChecks()
    {
        Check("KRN07-SHELL-OPEN-AT-BLEND", () =>
        {
            // 07.5: ОТКРЫТАЯ ГРАНЬ У СОПРЯЖЕНИЯ. Брусок 60×40×20, вертикальные рёбра R5, верхний контур скруглён r3; открыт верх
            // (касается скруглений). Полость — тело, смещённое внутрь на t (58×38×18 при t = 1: R4, r2), и призма над образом
            // верха: плоский верх смещённого — тот же скруглённый прямоугольник 54×34 R2, что и у исходного; внутри тела призма
            // занимает слой толщины t. V = V(тело) − V(смещённое) − (54·34 − (4 − π)·2²)·t. Объём скруглённого бруска — по Паппу
            // (как в KRN06-DIRECT-TANGENT-LOOP).
            var fails = new List<string>();
            double k = 4 - Math.PI, kk = 1 - Math.PI / 4, c = (10 - 3 * Math.PI) / (12 - 3 * Math.PI);
            double VR(double W, double D, double H, double R, double r) => (W * D - k * R * R) * H - kk * r * r * (2 * (W - 2 * R) + 2 * (D - 2 * R) + 2 * Math.PI * (R - c * r));
            foreach (double t in new[] { 1.0, 2.0 })
            {
                string name = $"rounded box R5 r3, open top, t {t}";
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>());
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7).Select(EdgeRef.Of).ToList(), 3, new List<string>());
                double want = VR(60, 40, 20, 5, 3) - VR(60 - 2 * t, 40 - 2 * t, 20 - 2 * t, 5 - t, 3 - t) - (54 * 34 - k * 4) * t;
                var w = new List<string>(); long c0 = ShellOp.OpenAtBlendShells;
                if (Environment.GetEnvironmentVariable("P3D_SHELLDBG") == "1") ShellOp.ShellDebug = m => Console.WriteLine("   [sh] " + m);
                bool ok; try { ok = ShellOp.Hollow(bl, t, new[] { new Vector3d(30, 20, 20) }, w); } catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                Console.WriteLine($"  {name}: ok {ok}, V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}, {b.Faces.Count()} faces | {string.Join(" | ", w.Take(2))}");
                if (!ok || bl.Count != 1 || ShellOp.OpenAtBlendShells == c0 || b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid || !(Math.Abs(v - want) <= 1e-9 * want))
                    fails.Add($"{name}: ok {ok}, V {v:R} vs {want:R}, {b.Guarantee} {SolidValidator.Validate(b).Summary()} | {string.Join(" | ", w.Take(2))}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
