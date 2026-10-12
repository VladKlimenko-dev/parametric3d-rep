using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

/// <summary>KRN-07: протяжка и лофт — пробы и приёмка (07.3, 07.4).</summary>
static partial class Program
{
    static void Krn07bChecks()
    {
        Frame3 XZ = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY.Equals(Vector3d.UnitY) ? Vector3d.UnitZ : Vector3d.UnitZ);
        Frame3 XYat(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        (Part P, Feature F) Built(Part p, Feature f, ParameterTable? t = null) { p.AddFeature(f); p.Rebuild(t); return (p, f); }
        Check("KRN07-SWEEP-ACCEPTANCE", () =>
        {
            var fails = new List<string>();
            void Ok(string name, Part p, Feature f, double exact, double rel)
            {
                if (f.Error != null || p.Bodies.Count != 1 || p.Integrity != null) { fails.Add($"{name}: {f.Error} {p.Integrity}"); return; }
                if (Math.Abs(p.Volume - exact) > rel * exact) fails.Add($"{name}: V={p.Volume:R}, expected {exact:R} (rel {rel})");
            }
            void Refused(string name, Part p, Feature f, string why)
            {
                if (f.Error == null || !f.Error.Contains(why) || p.Bodies.Count != 0) fails.Add($"{name}: must be refused ('{why}'), got error '{f.Error}', bodies {p.Bodies.Count}");
            }
            {   // замкнутая траектория — точный тор
                var p = new Part("sc"); var sec = new Sketch("sec", new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(sec);
                sec.AddEntity(new SkCircle(new SkPoint(0, 0), 2));
                var path = new Sketch("path", Frame3.XY); p.Add(path); path.AddEntity(new SkCircle(new SkPoint(0, 0), 20));
                var (q, f) = Built(p, new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
                Ok("closed circle path", q, f, 2 * Math.PI * Math.PI * 20 * 4, 1e-9);
            }
            {   // сильная кривизна: дуга R3 при сечении r5
                var p = new Part("sk"); var sec = new Sketch("sec", new Frame3(new Vector3d(3, 0, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(sec);
                sec.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
                var path = new Sketch("path", Frame3.XY); p.Add(path);
                path.AddEntity(new SkArc(new SkPoint(0, 0), new SkPoint(3, 0), new SkPoint(0, 3)) { CCW = true });
                var (q, f) = Built(p, new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
                Refused("arc R3, section r5", q, f, "arc radius");
            }
            {   // излом 177°: стык на ус выходит за отрезок
                var p = new Part("sk2"); var sec = CircleSketch(p, 0, 0, 0, 3);
                var path = new Sketch("path", XZ); p.Add(path);
                path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 40))); path.AddEntity(new SkLine(new SkPoint(0, 40), new SkPoint(2, 0)));
                var (q, f) = Built(p, new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
                Refused("fold-back corner", q, f, "mitre");
            }
            {   // самопересекающаяся траектория (петля сплайна)
                var p = new Part("sx"); var sec = CircleSketch(p, 0, 0, 0, 1);
                var path = new Sketch("path", XZ); p.Add(path);
                path.AddEntity(new SkSpline(new[] { new SkPoint(0, 0), new SkPoint(0, 40), new SkPoint(30, 20), new SkPoint(-30, 20), new SkPoint(0, 40) }));
                var (q, f) = Built(p, new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
                Refused("self-intersecting spline path", q, f, "cross itself");
            }
            {   // петля, вернувшаяся к себе: два прямых куска не соседние — ломаная П-образная, ноги на 1.5 при сечении r1
                var p = new Part("su"); var sec = CircleSketch(p, 0, 0, 0, 1);
                var path = new Sketch("path", XZ); p.Add(path);
                path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 30))); path.AddEntity(new SkLine(new SkPoint(0, 30), new SkPoint(1.5, 30)));
                path.AddEntity(new SkLine(new SkPoint(1.5, 30), new SkPoint(1.5, 0)));
                var (q, f) = Built(p, new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
                Refused("U-turn legs 1.5 apart, section r1", q, f, "cross itself");
            }
            {   // общий сплайн: годное многогранное тело, гранение видно, объём ≈ π·L
                var p = new Part("ss"); var sec = CircleSketch(p, 0, 0, 0, 1);
                var path = new Sketch("path", XZ); p.Add(path);
                var sp = new SkSpline(new[] { new SkPoint(0, 0), new SkPoint(0, 20), new SkPoint(20, 20), new SkPoint(20, 40) }); path.AddEntity(sp);
                var pl = new List<Vector2d>(); sp.Tessellate(pl, true); double L = 0; for (int i = 1; i < pl.Count; i++) L += (pl[i] - pl[i - 1]).Length;
                var (q, f) = Built(p, new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
                Ok("spline path (faceted)", q, f, Math.PI * L, 5e-3);
                if (q.Bodies.Count == 1 && (q.Bodies[0].Faces.Any(fc => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc) is Parametric3D.Kernel.Ops.SweptSurface)
                    || !q.Bodies[0].Faces.Any(fc => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc) == null))) fails.Add("spline path: faceting is not visible (no face without an exact surface)");
            }
            {   // правка параметра сечения
                var t = new ParameterTable(); t.Add("R", 3);
                var p = new Part("sp"); var sec = CircleSketch(p, 0, 0, 0, 2);
                var d = sec.AddDimension(DimensionType.Radius, 2, sec.Entities.OfType<SkCircle>().Single()); d.Locked = true; d.Formula = "R";
                var path = new Sketch("path", XZ); p.Add(path); path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 40)));
                var (q, f) = Built(p, new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }, t);
                Ok("parameter R=3", q, f, Math.PI * 9 * 40, 1e-9);
                t.Find("R")!.Number = 4; t.Recalculate(); q.ParametersChanged(); q.Rebuild(t);
                Ok("parameter R=4 after the edit", q, f, Math.PI * 16 * 40, 1e-9);
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN07-LOFT-ACCEPTANCE", () =>
        {
            var fails = new List<string>();
            void Ok(string name, Part p, Feature f, double exact, double rel)
            {
                if (f.Error != null || p.Bodies.Count != 1 || p.Integrity != null) { fails.Add($"{name}: {f.Error} {p.Integrity}"); return; }
                if (Math.Abs(p.Volume - exact) > rel * exact) fails.Add($"{name}: V={p.Volume:R}, expected {exact:R} (rel {rel})");
            }
            void Refused(string name, Part p, Feature f, string why, OperationOutcome? outcome = null)
            {
                if (f.Error == null || !f.Error.Contains(why) || p.Bodies.Count != 0) fails.Add($"{name}: must be refused ('{why}'), got '{f.Error}', bodies {p.Bodies.Count}");
                if (outcome != null)
                {
                    p.MarkDirtyFrom(f); var r = p.Compute(null); p.Apply(r);
                    if (!r.Outcomes.TryGetValue(f, out var o) || o != outcome) fails.Add($"{name}: outcome {o}, expected {outcome}");
                }
            }
            LoftFeature Lf(params Sketch[] ss) { var lf = new LoftFeature(); foreach (var x in ss) lf.SectionSketchIds.Add(x.Id); return lf; }
            {   // размер сечения по параметру
                var t = new ParameterTable(); t.Add("R", 3);
                var p = new Part("lp"); var a = CircleSketch(p, 0, 0, 0, 10); var b = CircleSketch(p, 30, 0, 0, 5);
                var d = b.AddDimension(DimensionType.Radius, 5, b.Entities.OfType<SkCircle>().Single()); d.Locked = true; d.Formula = "R";
                var (q, f) = Built(p, Lf(a, b), t);
                Ok("section radius by parameter", q, f, Math.PI * 30 / 3 * (100 + 30 + 9), 1e-9);
            }
            {   // два контура: кольцо → кольцо
                var p = new Part("lr"); var a = CircleSketch(p, 0, 0, 0, 10); a.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
                var b = CircleSketch(p, 30, 0, 0, 6); b.AddEntity(new SkCircle(new SkPoint(0, 0), 3));
                var (q, f) = Built(p, Lf(a, b));
                Ok("ring → ring", q, f, Math.PI * 30 / 3 * ((100 + 60 + 36) - (25 + 15 + 9)), 1e-9);
            }
            {   // отверстие не по оси: многогранно, ≈ формуле
                var p = new Part("lsh"); var a = PolySketch(p, XYat(0), (-10, -10), (10, -10), (10, 10), (-10, 10)); a.AddEntity(new SkCircle(new SkPoint(4, 0), 3));
                var b = PolySketch(p, XYat(30), (-5, -5), (5, -5), (5, 5), (-5, 5)); b.AddEntity(new SkCircle(new SkPoint(2, 0), 1.5));
                var (q, f) = Built(p, Lf(a, b));
                Ok("square with off-axis hole", q, f, 30.0 / 3 * (400 + 100 + 200) - Math.PI * 30 / 3 * (9 + 4.5 + 2.25), 1e-3);
            }
            {   // разное число контуров
                var p = new Part("lrd"); var a = CircleSketch(p, 0, 0, 0, 10); a.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
                var b = CircleSketch(p, 30, 0, 0, 6);
                var (q, f) = Built(p, Lf(a, b));
                Refused("ring → disc", q, f, "contours");
            }
            {   // соответствие по вершинам: прямоугольник 20×10 → 18×12 — призматоид точно
                var p = new Part("la1"); var a = PolySketch(p, XYat(0), (-10, -5), (10, -5), (10, 5), (-10, 5));
                var b = PolySketch(p, XYat(30), (-9, -6), (9, -6), (9, 6), (-9, 6));
                var (q, f) = Built(p, Lf(a, b));
                Ok("rectangle 20×10 → 18×12", q, f, 30.0 / 6 * (200 + 4 * 19 * 11 + 216), 1e-12);
            }
            {   // неоднозначное соответствие: квадрат → квадрат, повёрнутый на 45°
                var p = new Part("lt"); var a = PolySketch(p, XYat(0), (-10, -10), (10, -10), (10, 10), (-10, 10));
                double r = 10 * Math.Sqrt(2);
                var b = PolySketch(p, XYat(30), (0, -r), (r, 0), (0, r), (-r, 0));
                var (q, f) = Built(p, Lf(a, b));
                Refused("square → square turned 45°", q, f, "ambiguity", OperationOutcome.Ambiguous);
            }
            {   // порядок сечений: z 0, 30, 10
                var p = new Part("lx"); var a = CircleSketch(p, 0, 0, 0, 10); var b = CircleSketch(p, 30, 0, 0, 5); var c = CircleSketch(p, 10, 0, 0, 8);
                var (q, f) = Built(p, Lf(a, b, c));
                Refused("sections out of order", q, f, "out of order");
            }
            {   // негодное тело по равномерной разметке (разное число вершин) — отказ, а не публикация
                var p = new Part("lsc"); var a = PolySketch(p, XYat(0), (-10, -5), (0, -5), (10, -5), (10, 5), (-10, 5));
                var b = PolySketch(p, XYat(30), (-5, -10), (5, -10), (5, 10), (-5, 10));
                var (q, f) = Built(p, Lf(a, b));
                Refused("5-vertex → 4-vertex section", q, f, "not a valid solid");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void SweepLoftProbe()
    {
        Frame3 XZ = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
        void Run(string name, Func<(Part P, ParameterTable? T)> make, double expected)
        {
            try
            {
                var (p, t) = make(); p.Rebuild(t);
                var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Error));
                double rel = double.IsNaN(expected) ? double.NaN : (p.Volume - expected) / expected;
                Console.WriteLine($"{name}: bodies {p.Bodies.Count}, V={p.Volume:0.####} exp {expected:0.####} rel {rel:E2} ±{p.VolumeError:G3}, err=[{errs}], integrity={p.Integrity ?? "ok"}, faces={p.Bodies.Sum(b => b.Faces.Count())}, surf={string.Join(",", p.Bodies.SelectMany(b => b.Faces).Select(f => (Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name ?? "-")).Distinct())}");
                if (p.Integrity != null && Environment.GetEnvironmentVariable("SWDETAIL") == "1")
                    foreach (var b in p.Bodies) foreach (var iss in Parametric3D.Kernel.Topology.SolidValidator.Validate(b).Issues.Take(8)) Console.WriteLine("   " + iss);
            }
            catch (Exception e) { Console.WriteLine($"{name}: EXCEPTION {e.GetType().Name}: {e.Message}"); }
        }
        // протяжка
        Run("sweep closed circle path R20, r2", () =>
        {
            var p = new Part("sc"); var sec = new Sketch("sec", new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(sec);
            sec.AddEntity(new SkCircle(new SkPoint(0, 0), 2));
            var path = new Sketch("path", Frame3.XY); p.Add(path); path.AddEntity(new SkCircle(new SkPoint(0, 0), 20));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return (p, null);
        }, 2 * Math.PI * Math.PI * 20 * 4);
        Run("sweep strong curvature: arc R3 path, section r5 (must refuse)", () =>
        {
            var p = new Part("sk"); var sec = new Sketch("sec", new Frame3(new Vector3d(3, 0, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(sec);
            sec.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
            var path = new Sketch("path", Frame3.XY); p.Add(path);
            path.AddEntity(new SkArc(new SkPoint(0, 0), new SkPoint(3, 0), new SkPoint(0, 3)) { CCW = true });
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return (p, null);
        }, double.NaN);
        Run("sweep strong curvature polyline corner: L path, section r3, sharp 170° turn (must refuse or be valid)", () =>
        {
            var p = new Part("sk2"); var sec = CircleSketch(p, 0, 0, 0, 3);
            var path = new Sketch("path", XZ); p.Add(path);
            path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 40)));
            path.AddEntity(new SkLine(new SkPoint(0, 40), new SkPoint(2, 0)));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return (p, null);
        }, double.NaN);
        Run("sweep self-intersecting path (spline loop crossing itself)", () =>
        {
            var p = new Part("sx"); var sec = CircleSketch(p, 0, 0, 0, 1);
            var path = new Sketch("path", XZ); p.Add(path);
            path.AddEntity(new SkSpline(new[] { new SkPoint(0, 0), new SkPoint(0, 40), new SkPoint(30, 20), new SkPoint(-30, 20), new SkPoint(0, 40) }));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return (p, null);
        }, double.NaN);
        Run("sweep along a spline (gentle S), r1: V = π·L", () =>
        {
            var p = new Part("ss"); var sec = CircleSketch(p, 0, 0, 0, 1);
            var path = new Sketch("path", XZ); p.Add(path);
            var sp = new SkSpline(new[] { new SkPoint(0, 0), new SkPoint(0, 20), new SkPoint(20, 20), new SkPoint(20, 40) }); path.AddEntity(sp);
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return (p, null);
        }, double.NaN);
        Run("sweep parameter R (section radius 2→3 via formula) along 40", () =>
        {
            var t = new ParameterTable(); t.Add("R", 3);
            var p = new Part("sp"); var sec = CircleSketch(p, 0, 0, 0, 2);
            var c = sec.Entities.OfType<SkCircle>().Single();
            var d = sec.AddDimension(DimensionType.Radius, 2, c); d.Locked = true; d.Formula = "R";
            var path = new Sketch("path", XZ); p.Add(path); path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 40)));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return (p, t);
        }, Math.PI * 9 * 40);
        // лофт
        Run("loft parameter R (section b radius via formula = 3)", () =>
        {
            var t = new ParameterTable(); t.Add("R", 3);
            var p = new Part("lp"); var a = CircleSketch(p, 0, 0, 0, 10); var b = CircleSketch(p, 30, 0, 0, 5);
            var c = b.Entities.OfType<SkCircle>().Single();
            var d = b.AddDimension(DimensionType.Radius, 5, c); d.Locked = true; d.Formula = "R";
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, t);
        }, Math.PI * 30 / 3 * (100 + 30 + 9));
        Run("loft square 20 → square 20 rotated 45° (twist) over 30", () =>
        {
            var p = new Part("lt"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -10), (10, -10), (10, 10), (-10, 10));
            double r = 10 * Math.Sqrt(2);
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (0, -r), (r, 0), (0, r), (-r, 0));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
        Run("loft square 20 → triangle (different segment counts)", () =>
        {
            var p = new Part("ltr"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -10), (10, -10), (10, 10), (-10, 10));
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (-10, -8), (10, -8), (0, 12));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
        Run("loft ring (two contours) r10/r5 → r6/r3 over 30", () =>
        {
            var p = new Part("lr"); var a = CircleSketch(p, 0, 0, 0, 10); a.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
            var b = CircleSketch(p, 30, 0, 0, 6); b.AddEntity(new SkCircle(new SkPoint(0, 0), 3));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, Math.PI * 30 / 3 * ((100 + 60 + 36) - (25 + 15 + 9)));
        Run("loft three sections square 20 → 10 → 20 (z 0, 15, 30)", () =>
        {
            var p = new Part("l3"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -10), (10, -10), (10, 10), (-10, 10));
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 15), Vector3d.UnitX, Vector3d.UnitY), (-5, -5), (5, -5), (5, 5), (-5, 5));
            var c = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (-10, -10), (10, -10), (10, 10), (-10, 10));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); lf.SectionSketchIds.Add(c.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
        Run("loft crossing sections (b behind a: z 30 → 0 → 30 ordering ambiguity — sections a z0, b z30, c z10)", () =>
        {
            var p = new Part("lx"); var a = CircleSketch(p, 0, 0, 0, 10); var b = CircleSketch(p, 30, 0, 0, 5); var c = CircleSketch(p, 10, 0, 0, 8);
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); lf.SectionSketchIds.Add(c.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
        Run("loft rectangle 20×10 → 10×20 (twist ±90° ambiguous)", () =>
        {
            var p = new Part("lam"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -5), (10, -5), (10, 5), (-10, 5));
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (-5, -10), (5, -10), (5, 10), (-5, 10));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
        Run("loft ring → disc (contour count differs: refuse)", () =>
        {
            var p = new Part("lrd"); var a = CircleSketch(p, 0, 0, 0, 10); a.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
            var b = CircleSketch(p, 30, 0, 0, 6);
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
        Run("loft square 20 with off-centre hole r3 → square 10 with hole r1.5 (non-coaxial holes)", () =>
        {
            var p = new Part("lsh"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -10), (10, -10), (10, 10), (-10, 10));
            a.AddEntity(new SkCircle(new SkPoint(4, 0), 3));
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (-5, -5), (5, -5), (5, 5), (-5, 5));
            b.AddEntity(new SkCircle(new SkPoint(2, 0), 1.5));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, 30.0 / 3 * (400 + 100 + 200) - Math.PI * 30 / 3 * (9 + 4.5 + 2.25));
        Run("loft rectangle 20×10 → 18×12 aligned", () =>
        {
            var p = new Part("la1"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -5), (10, -5), (10, 5), (-10, 5));
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (-9, -6), (9, -6), (9, 6), (-9, 6));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, 30.0 / 6 * (200 + 4 * 19 * 11 + 216));
        Run("loft rectangle 20×10 → 10×20 starting at the same corner direction", () =>
        {
            var p = new Part("la2"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -5), (10, -5), (10, 5), (-10, 5));
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (-5, -10), (5, -10), (5, 10), (-5, 10));
            var lf = new LoftFeature { Samples = 120 }; lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
        Run("loft rect 20×10 (5 vertices) → 10×20 (4 vertices): uniform matching, self-crossing", () =>
        {
            var p = new Part("lsc"); var a = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (-10, -5), (0, -5), (10, -5), (10, 5), (-10, 5));
            var b = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (-5, -10), (5, -10), (5, 10), (-5, 10));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return (p, null);
        }, double.NaN);
    }
}
