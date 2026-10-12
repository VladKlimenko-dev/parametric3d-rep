using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

static partial class Program
{
    static void Krn06CylCylPartChecks()
    {
        Check("KRN06-CYLCYL-PART-REBUILD", () =>
        {
            // ДЕТАЛЬ: вал R20 (эскиз-окружность на YZ, симметричное выдавливание 100 вдоль X), поперечное сквозное отверстие
            // d8 (операция «отверстие» вниз по Z с верха вала), скругление обеих кромок отверстия селектором кромки с
            // параметром R: 1 → 2 → 1.5. Каждый раз: без ошибок, без гранения, ExactAnalytic, объём = вал − отверстие −
            // 2 уголка катящегося шара (RollingBallCusp), касание лент; перестроение = сборке с нуля (объём, число граней).
            var fails = new List<string>();
            // объём вала с отверстием: πR²L − ∬_{круг d8} 2√(R² − y²)
            double hole = 0; { int n = 4000; for (int i = 0; i < n; i++) { double th = -Math.PI / 2 + Math.PI * (i + 0.5) / n, y = 4 * Math.Sin(th); hole += 2 * 4 * Math.Cos(th) * 2 * Math.Sqrt(400 - y * y) * 4 * Math.Cos(th) * Math.PI / n; } }   // y = 4 sin θ: гладкая подынтегральная
            double baseV = Math.PI * 400 * 100 - hole;
            (Part P, ParameterTable T) Make(double R)
            {
                var t = new ParameterTable(); t.Add("R", R);
                var p = new Part("ccpart");
                var s = new Sketch("shaft", p.PlaneYZ.Frame); s.SetBasePlane(p.PlaneYZ.Frame, p.PlaneYZ.Id, p.PlaneYZ.Name); p.Add(s);
                s.AddEntity(new SkCircle(new SkPoint(0, 0), 20));
                var ex = new ExtrudeFeature("shaft") { SketchId = s.Id, Symmetric = true }; ex.Side1.End = EndType.Distance; ex.Side1.Distance = new ValueRef(100); p.AddFeature(ex);
                var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(8) }; h.Points.Add(new Vector3d(0, 0, 20)); p.AddFeature(h);
                p.Rebuild(t);
                if (Environment.GetEnvironmentVariable("P3D_CCPARTDBG") == "1") foreach (var f in p.Bodies[0].Faces) Console.WriteLine($"   face {f.Tag} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}");
                var r = new RoundFeature("R1") { Radius = new ValueRef(R) { Formula = "R" } }; r.Selectors.Add(new EdgeSelector(EdgeSelectorKind.Rim, h.Key)); p.AddFeature(r);
                p.Rebuild(t);
                return (p, t);
            }
            void Inspect(string name, Part p, double R)
            {
                var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Key + ": " + f.Error));
                if (errs.Length > 0 || p.Integrity != null || p.Bodies.Count != 1) { fails.Add($"{name}: {errs} {p.Integrity} bodies {p.Bodies.Count}"); return; }
                var b = p.Bodies[0];
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {b.Guarantee}");
                var fac = GeometryAudit.Faceted(b); if (fac != (0, 0)) fails.Add($"{name}: faceted {fac}");
                double v = AnalyticVolume.Of(b) ?? double.NaN, want = baseV - 2 * RollingBallCusp(20, 4, R, true);
                if (!(Math.Abs(v - want) < 1e-5)) fails.Add($"{name}: V {v:R} vs {want:R}");
                double tan = BlendTangency(b); if (!(tan < 1e-5)) fails.Add($"{name}: tangency {tan:E2}");
                int blends = b.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface);
                if (blends != 4) fails.Add($"{name}: blend faces {blends} (want 4: two halves per rim)");
                Console.WriteLine($"  {name}: V {v:R} want {want:R} faces {b.Faces.Count()} tangency {tan:E1}");
            }
            var (pp, tt) = Make(1); Inspect("R=1", pp, 1);
            foreach (double R in new[] { 2.0, 1.5 })
            {
                tt.Find("R")!.Number = R; tt.Recalculate(); pp.ParametersChanged(); pp.Rebuild(tt); Inspect($"R={R} edited", pp, R);
                var (fresh, _) = Make(R);
                if (PartGeometry(fresh) != PartGeometry(pp)) fails.Add($"R={R}: rebuild differs from a fresh build ({pp.Volume:R} vs {fresh.Volume:R})");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
