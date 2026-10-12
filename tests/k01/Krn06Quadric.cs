using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Наибольший угол между нормалями ленты и соседней грани на линиях касания (рад).</summary>
    static double BlendTangency(TSolid s)
    {
        double worst = 0;
        foreach (var bf in s.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface))
            foreach (var e in bf.Edges.Where(x => x.Curve is ProcCurve3))
            {
                var other = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().First(f => f != bf);
                var sa = GeometryAudit.AuthoritativeSurface(bf)!; var sb = GeometryAudit.AuthoritativeSurface(other)!;
                for (int k = 0; k <= 16; k++)
                {
                    var p = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 16.0);
                    double c = Math.Abs(Vector3d.Dot(sa.NormalAt(sa.ProjectToUV(p)).Normalized(), sb.NormalAt(sb.ProjectToUV(p)).Normalized()));
                    worst = Math.Max(worst, Math.Acos(Math.Min(1, c)));
                }
            }
        return worst;
    }

    static void Krn06QuadricChecks()
    {
        Check("KRN06-CLOSED-QUADRIC-ROLLING-BALL-EXACT", () =>
        {
            // ЗАМКНУТЫЕ РЁБРА МЕЖДУ КВАДРИКАМИ (06.12/06.14): кромка усечённого конуса (плоскость/конус), стыки цилиндр/конус
            // (выпуклый) и конус/цилиндр (вогнутый), стык цилиндр/шар; эталон — Папп в меридиане (PappusCorner, независимо от
            // ядра: 2D-окружность, касающаяся двух кривых профиля). Кромка отверстия не по оси шара и бобышка на шаре — без
            // эталона объёма: точный маршрут, годность, касание. Требуется: ExactAnalytic, годность, ни одной гранёной
            // грани и ребра, касание до 1e-5 рад, изменение объёма = эталону до 2e-6 отн.
            var fails = new List<string>();
            bool At(TEdge e, double rr, double z) { var p = e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2); return Math.Abs(new Vector2d(p.X, p.Y).Length - rr) < 1e-6 && Math.Abs(p.Z - z) < 1e-6; }
            double zc = 20 - Math.Sqrt(225 - 100);
            var sph = new List<Vector2d> { new(0, 0), new(10, 0), new(10, 20) };
            for (int i = 1; i < 200; i++) { double a = Math.Atan2(20 - zc, 10) + (Math.PI / 2 - Math.Atan2(20 - zc, 10)) * i / 199.0; sph.Add(new(15 * Math.Cos(a), zc + 15 * Math.Sin(a))); }
            var cases = new (string Name, Func<TSolid> Make, Func<TEdge, bool> Pick, double R, double Want)[]
            {
                ("frustum rim", () => RevolveProfile(Poly((0, 0), (20, 0), (12, 20), (0, 20))), e => At(e, 12, 20), 1,
                    -PappusCorner(MCurve.Line(0, 20, 12, 20), MCurve.Line(20, 0, 12, 20), new(12, 20), 1, true, new List<Vector2d> { new(0, 0), new(20, 0), new(12, 20), new(0, 20) })),
                ("cyl/cone convex", () => RevolveProfile(Poly((0, 0), (20, 0), (20, 10), (12, 25), (0, 25))), e => At(e, 20, 10), 2,
                    -PappusCorner(MCurve.Line(20, 0, 20, 10), MCurve.Line(20, 10, 12, 25), new(20, 10), 2, true, new List<Vector2d> { new(0, 0), new(20, 0), new(20, 10), new(12, 25), new(0, 25) })),
                ("cone/cyl concave", () => RevolveProfile(Poly((0, 0), (20, 0), (20, 10), (10, 15), (10, 30), (0, 30))), e => At(e, 10, 15), 2,
                    PappusCorner(MCurve.Line(20, 10, 10, 15), MCurve.Line(10, 15, 10, 30), new(10, 15), 2, false, new List<Vector2d> { new(0, 0), new(20, 0), new(20, 10), new(10, 15), new(10, 30), new(0, 30) })),
                ("cyl/sphere convex", () => RevolveProfile(new List<ICurve3> {
                        new LineCurve3(new Vector3d(0, 0, 0), new Vector3d(10, 0, 0)), new LineCurve3(new Vector3d(10, 0, 0), new Vector3d(10, 0, 20)),
                        new ArcCurve3(new Frame3(new Vector3d(0, 0, zc), Vector3d.UnitX, Vector3d.UnitZ), 15, Math.Atan2(20 - zc, 10), Math.PI / 2),
                        new LineCurve3(new Vector3d(0, 0, zc + 15), new Vector3d(0, 0, 0)) }), e => At(e, 10, 20), 1.5,
                    -PappusCorner(MCurve.Line(10, 0, 10, 20), MCurve.Circle(0, zc, 15), new(10, 20), 1.5, true, sph)),
                ("sphere off-axis hole rim", () => { var b = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 20) }; BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(6, 0, -30), 4, 60), new List<string>()); return b[0]; },
                    e => e.Curve is not (LineCurve3 or ArcCurve3) && e.Curve.PointAt(0.5).Z > 5, 1, double.NaN),
            };
            foreach (var (name, make, pick, r, want) in cases)
            {
                var b = new List<TSolid> { make() };
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
                var edges = b[0].Edges.Where(pick).ToList();
                if (edges.Count != 1) { fails.Add($"{name}: edges {edges.Count}"); continue; }
                try { FilletOp.RoundEdges(b, edges.Select(EdgeRef.Of).ToList(), r, new List<string>()); }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                var s = b[0];
                double dv = (AnalyticVolume.Of(s) ?? double.NaN) - v0;
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {s.Guarantee}");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"{name}: invalid");
                var fac = GeometryAudit.Faceted(s); if (fac != (0, 0)) fails.Add($"{name}: faceted {fac}");
                double tan = BlendTangency(s); if (!(tan < 1e-5)) fails.Add($"{name}: tangency off by {tan:E2} rad");
                if (!double.IsNaN(want) && !(Math.Abs(dv - want) <= 2e-6 * Math.Abs(want) + 1e-6)) fails.Add($"{name}: dV {dv:R} vs Pappus {want:R}");
                Console.WriteLine($"  {name}: dV {dv:R} Pappus {want:R} tangency {tan:E1} rad");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
