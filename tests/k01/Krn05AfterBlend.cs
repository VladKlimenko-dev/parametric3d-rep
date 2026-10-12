using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

static partial class Program
{
    static void Krn05AfterBlendChecks()
    {
        Check("KRN05-BOOLEAN-AFTER-BLEND", () =>
        {
            // ОПЕРАЦИИ ПОСЛЕ СКРУГЛЕНИЯ: у вала R20 с поперечным отверстием d8 кромка скруглена r1 (лента катящегося шара —
            // поверхность по формуле). Отверстие R5 сквозь вал в стороне (x = 30) и бобышка R5 высотой 30 (x = −30): точный
            // маршрут (лента — пассивная грань: классификатор пересекает её лучом точно), объём = до + ∓∬ хорд по кругу
            // (полярные координаты, Гаусс). Резец сквозь ленту — честно многогранный путь, годное тело.
            var fails = new List<string>();
            TSolid Rounded()
            {
                var b = new List<TSolid> { TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)) };
                BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
                var e = b[0].Edges.Where(x => x.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && x.Curve.PointAt(0.5).Z > 5).ToList();
                FilletOp.RoundEdges(b, e.Select(EdgeRef.Of).ToList(), 1, new List<string>());
                return b[0];
            }
            // ∬ по кругу радиуса 5 вокруг (xc, 0) функции g(y) — Гаусс по радиусу и углу
            double Disc(Func<double, double> g)
            {
                double[] x = { -0.9739065285171717, -0.8650633666889845, -0.6794095682990244, -0.4333953941292472, -0.1488743389816312, 0.1488743389816312, 0.4333953941292472, 0.6794095682990244, 0.8650633666889845, 0.9739065285171717 };
                double[] w = { 0.0666713443086881, 0.1494513491505806, 0.2190863625159820, 0.2692667193099963, 0.2955242247147529, 0.2955242247147529, 0.2692667193099963, 0.2190863625159820, 0.1494513491505806, 0.0666713443086881 };
                double s = 0; int na = 64;
                for (int ia = 0; ia < na; ia++) for (int i = 0; i < 10; i++) for (int j = 0; j < 10; j++)
                {
                    double th = 2 * Math.PI * (ia + (x[i] + 1) / 2) / na, rr = 5 * (x[j] + 1) / 2;
                    s += g(rr * Math.Sin(th)) * rr * w[i] / 2 * 2 * Math.PI / na * w[j] / 2 * 5;
                }
                return s;
            }
            var b0 = Rounded(); double v0 = AnalyticVolume.Of(b0) ?? double.NaN;
            if (b0.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"rounded body: {b0.Guarantee}");
            foreach (var (name, cut, tool, want) in new (string, bool, TSolid, double)[]
            {
                ("hole R5 at x=30", true, PrimitiveOp.Cylinder(new Vector3d(30, 0, -40), 5, 80), v0 - Disc(y => 2 * Math.Sqrt(400 - y * y))),
                ("boss R5 at x=-30", false, PrimitiveOp.Cylinder(new Vector3d(-30, 0, 0), 5, 30), v0 + Math.PI * 25 * 30 - Disc(y => Math.Sqrt(400 - y * y))),
            })
            {
                var b = new List<TSolid> { TopologyClone.Deep(b0) };
                var r = BooleanOp.Evaluate(b, tool, cut ? BooleanOp.BooleanKind.Subtract : BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
                var s = r.Bodies.FirstOrDefault();
                if (s == null || r.Bodies.Count != 1) { fails.Add($"{name}: {r.Outcome}"); continue; }
                double v = AnalyticVolume.Of(s) ?? double.NaN;
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: {s.Guarantee}");
                if (!SolidValidator.Validate(s).IsValid) fails.Add($"{name}: invalid");
                if (!(Math.Abs(v - want) < 1e-6)) fails.Add($"{name}: V {v:R} vs {want:R}");
                if (s.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface) != 2) fails.Add($"{name}: blend faces lost");
                Console.WriteLine($"  {name}: V {v:R} want {want:R}");
            }
            {   // резец сквозь ленту — не строится точно: многогранный путь, годное тело
                var b = new List<TSolid> { TopologyClone.Deep(b0) };
                var r = BooleanOp.Evaluate(b, PrimitiveOp.Cylinder(new Vector3d(4.5, 0, -40), 2, 80), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                var s = r.Bodies.FirstOrDefault();
                if (s == null || !SolidValidator.Validate(s).IsValid) fails.Add($"through the blend: {r.Outcome}");
                else Console.WriteLine($"  through the blend: {s.Guarantee}");
            }
            {   // ДЕРЕВО ДЕТАЛИ: вал, поперечное отверстие, скругление кромок, затем ещё одно отверстие R5 в стороне (x = 30)
                var p = new Part("afterblend");
                var sk = new Sketch("shaft", p.PlaneYZ.Frame); sk.SetBasePlane(p.PlaneYZ.Frame, p.PlaneYZ.Id, p.PlaneYZ.Name); p.Add(sk);
                sk.AddEntity(new SkCircle(new SkPoint(0, 0), 20));
                var ex = new ExtrudeFeature("shaft") { SketchId = sk.Id, Symmetric = true }; ex.Side1.End = EndType.Distance; ex.Side1.Distance = new ValueRef(100); p.AddFeature(ex);
                var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(8) }; h.Points.Add(new Vector3d(0, 0, 20)); p.AddFeature(h);
                var rf = new RoundFeature("R1") { Radius = new ValueRef(1) }; rf.Selectors.Add(new EdgeSelector(EdgeSelectorKind.Rim, h.Key)); p.AddFeature(rf);
                var h2 = new HoleFeature("H2") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(10) }; h2.Points.Add(new Vector3d(30, 0, 20)); p.AddFeature(h2);
                p.Rebuild(null);
                var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Key + ": " + f.Error));
                if (errs.Length > 0 || p.Bodies.Count != 1) fails.Add($"part: {errs} bodies {p.Bodies.Count}");
                else
                {
                    var pb = p.Bodies[0]; double pv = AnalyticVolume.Of(pb) ?? double.NaN;
                    // у тела Rounded() скруглена одна кромка, у детали — обе (селектор Rim)
                    double want = v0 - RollingBallCusp(20, 4, 1, true) - Disc(y => 2 * Math.Sqrt(400 - y * y));
                    if (pb.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"part: {pb.Guarantee}");
                    if (!(Math.Abs(pv - want) < 1e-5)) fails.Add($"part: V {pv:R} vs {want:R}");
                    Console.WriteLine($"  part after blend: V {pv:R} want {want:R} {pb.Guarantee}");
                }
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
