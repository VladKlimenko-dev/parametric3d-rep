using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn07LoftNurbsChecks()
    {
        Check("KRN07-LOFT-NURBS-EXACT", () =>
        {
            // 07.4: лофт по отрезкам и дугам — точные NURBS-грани (рациональные квадратичные куски сечений, поперёк — линейчатый
            // или гладкий интерполяционный). Эталоны независимы от построителя:
            //  • линейчатый между параллельными окружностями R10 и R5 со смещением центров — наклонный усечённый конус, по
            //    Кавальери V = πh(r0² + r0r1 + r1²)/3;
            //  • гладкий по трём соосным окружностям R10/R15/R10 (z = 0, 10, 20) — поверхность вращения с меридианом —
            //    квадратичный Безье через (10, 0), (15, 10), (10, 20): ρ(v) = 10 + 20v − 20v², z = 20v, V = 20π∫ρ²dv;
            //  • квадрат 20×20 → окружность R12 (линейчатый и гладкий через окружность R11 посередине) — годность и точность.
            var fails = new List<string>();
            Profile Circle(Vector3d c, double r) => ProfileBuilder.Build(new Frame3(c, Vector3d.UnitX, Vector3d.UnitY), new List<ICurve3> { new ArcCurve3(new Frame3(c, Vector3d.UnitX, Vector3d.UnitY), r, 0, 2 * Math.PI) });
            Profile Square(double z, double a)
            {
                var p = new[] { new Vector3d(-a, -a, z), new Vector3d(a, -a, z), new Vector3d(a, a, z), new Vector3d(-a, a, z) };
                return ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), Enumerable.Range(0, 4).Select(i => (ICurve3)new LineCurve3(p[i], p[(i + 1) % 4])).ToList());
            }
            // ∫₀¹ ρ(v)² dv — Гаусс–Лежандр по 5 узлам (точно для степени 4)
            double[] gx = { -0.9061798459386640, -0.5384693101056831, 0, 0.5384693101056831, 0.9061798459386640 }, gw = { 0.2369268850561891, 0.4786286704993665, 0.5688888888888889, 0.4786286704993665, 0.2369268850561891 };
            double rho2 = 0; for (int i = 0; i < 5; i++) { double v = (gx[i] + 1) / 2, r = 10 + 20 * v - 20 * v * v; rho2 += gw[i] / 2 * r * r; }
            var cases = new (string Name, List<Profile> Sections, bool Smooth, double Want, int Faces)[]
            {
                ("ruled oblique cone frustum", new() { Circle(Vector3d.Zero, 10), Circle(new Vector3d(4, -3, 20), 5) }, false, Math.PI * 20 * (100 + 50 + 25) / 3, -1),
                ("smooth three coaxial circles", new() { Circle(Vector3d.Zero, 10), Circle(new Vector3d(0, 0, 10), 15), Circle(new Vector3d(0, 0, 20), 10) }, true, 20 * Math.PI * rho2, -1),
                ("ruled square to circle", new() { Square(0, 10), Circle(new Vector3d(0, 0, 20), 12) }, false, double.NaN, 6),
                ("smooth square, circle, square", new() { Square(0, 10), Circle(new Vector3d(0, 0, 10), 11), Square(20, 8) }, true, double.NaN, 6),
                ("ruled three sections (two spans)", new() { Square(0, 10), Circle(new Vector3d(0, 0, 10), 11), Square(20, 8) }, false, double.NaN, 10),
            };
            foreach (var (name, secs, smooth, want, nf) in cases)
            {
                long n0 = DirectRound.NurbsLofts;
                TSolid b; try { b = LoftOp.Build(secs, 64, smooth); } catch (Exception ex) { fails.Add($"{name}: {ex.Message}"); continue; }
                double v = AnalyticVolume.Of(b) ?? double.NaN;
                int nurbs = b.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface);
                Log(new { test = "KRN07-LOFT-NURBS", name, volume = double.IsFinite(v) ? v : -1, want = double.IsFinite(want) ? want : -1, guarantee = b.Guarantee.ToString(), faces = b.Faces.Count(), nurbs });
                if (DirectRound.NurbsLofts == n0) fails.Add($"{name}: not the NURBS loft");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (double.IsFinite(want) && !(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                if (!double.IsFinite(v) || BooleanOp.VolumeVsFacets(b) is { Agree: false }) fails.Add($"{name}: volume {v:R} disagrees with facets");
                if (nf > 0 && b.Faces.Count() != nf) fails.Add($"{name}: faces {b.Faces.Count()} (want {nf})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
