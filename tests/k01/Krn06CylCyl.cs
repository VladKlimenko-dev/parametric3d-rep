using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn06CylCylChecks()
    {
        Check("KRN06-CYLCYL-ROLLING-BALL-EXACT", () =>
        {
            // ПРЯМОЕ ТОЧНОЕ СКРУГЛЕНИЕ ЦИЛИНДР/ЦИЛИНДР (06.12): вал R20 (ось X), поперечный цилиндр (ось Z) — кромка отверстия
            // (выпуклое) и стык бобышки (вогнутое). Эталон объёма — независимая квадратура «уголка» катящегося шара по трём
            // полосам границы (RollingBallCusp; сверена второй независимой мерой по столбцам до 3e-6). Требуется: ExactAnalytic,
            // годность, ни одной гранёной грани и ребра, две грани ленты на поверхности по формуле сечения, касание по линиям
            // касания (нормали лент и цилиндров совпадают), изменение объёма — как у эталона.
            var fails = new List<string>();
            TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
            foreach (var (name, cut, rho, r) in new[] { ("hole d8 r1", true, 4.0, 1.0), ("hole d8 r2", true, 4.0, 2.0), ("boss R8 r3", false, 8.0, 3.0), ("hole d30 r1", true, 15.0, 1.0) })
            {
                var b = new List<TSolid> { Shaft() };
                if (cut) BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), rho, 80), new List<string>());
                else BooleanOp.Unite(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), rho, 40), new List<string>());
                double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN; int f0 = b[0].Faces.Count();
                var edges = b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt(0.5).Z > 5).ToList();
                if (edges.Count != 1) { fails.Add($"{name}: edges {edges.Count}"); continue; }
                long ev0 = Parametric3D.Kernel.Tess.ExactFaceMesher.RefineEvals;
                try { FilletOp.RoundEdges(b, edges.Select(EdgeRef.Of).ToList(), r, new List<string>()); }
                catch (Exception e) { fails.Add($"{name}: {e.GetType().Name} {e.Message}"); continue; }
                long evals = Parametric3D.Kernel.Tess.ExactFaceMesher.RefineEvals - ev0;
                var s = b[0];
                double exact = RollingBallCusp(20, rho, r, cut), dv = (AnalyticVolume.Of(s) ?? double.NaN) - v0;
                double want = cut ? -exact : exact;
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {s.Guarantee}");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"{name}: invalid");
                var fac = GeometryAudit.Faceted(s); if (fac != (0, 0)) fails.Add($"{name}: faceted {fac}");
                if (!(Math.Abs(dv - want) <= 2e-6 * exact + 1e-6)) fails.Add($"{name}: dV {dv:R} vs exact {want:R}");
                var blends = s.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface).ToList();
                if (blends.Count != 2 || s.Faces.Count() != f0 + 2) fails.Add($"{name}: blend faces {blends.Count}, faces {f0}->{s.Faces.Count()}");
                // касание: на рёбрах ленты с цилиндрами нормали поверхностей параллельны
                double worst = 0;
                foreach (var bf in blends)
                    foreach (var e in bf.Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.ProcCurve3))
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
                if (!(worst < 1e-5)) fails.Add($"{name}: tangency off by {worst:E2} rad");
                // перенос копии (зеркало) — точно: та же формула ленты под отображением, объём тот же
                if (name == "hole d8 r1")
                {
                    var m = TransformOp.Copy(s, TransformOp.Mirror(new Vector3d(3, 0, 0), new Vector3d(1, 0.2, 0)));
                    double vm = AnalyticVolume.Of(m) ?? double.NaN, vs = AnalyticVolume.Of(s) ?? double.NaN;
                    if (m.Guarantee != GuaranteeLevel.ExactAnalytic || !(Math.Abs(vm - vs) < 1e-6) || !SolidValidator.Validate(m).IsValid)
                        fails.Add($"{name}: mirror copy {m.Guarantee} V {vm:R} vs {vs:R}");
                }
                // KRN-10: уточнение сетки не перемеряет годные треугольники (у отверстия d30 без этого — около 10× проверок)
                if (name == "hole d30 r1" && evals > 60000) fails.Add($"{name}: refine evaluations {evals}");
                Console.WriteLine($"  {name}: dV {dv:R} exact {want:R} tangency {worst:E1} rad refine evals {evals}");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
