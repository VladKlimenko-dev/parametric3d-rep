using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    static void ThinProbe()
    {
        Frame3 XYat(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        void Show(string name, Part p, double expected)
        {
            var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Error));
            double rel = (p.Volume - expected) / expected;
            if (p.Integrity != null) foreach (var b in p.Bodies) foreach (var iss in Parametric3D.Kernel.Topology.SolidValidator.Validate(b).Issues.Take(4)) Console.WriteLine("   " + iss);
            Console.WriteLine($"{name}: bodies {p.Bodies.Count}, V={p.Volume:G10} exp {expected:G10} rel {rel:E2} ±{p.VolumeError:G3}, err=[{errs}], integrity={p.Integrity ?? "ok"}");
        }
        {   // стадион: два отрезка и две дуги
            var p = new Part("stadium"); var s = new Sketch("s", XYat(0)); p.Add(s);
            var a = new SkPoint(-20, -5); var b = new SkPoint(20, -5); var c = new SkPoint(20, 5); var d = new SkPoint(-20, 5);
            s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkArc(new SkPoint(20, 0), b, c) { CCW = true }); s.AddEntity(new SkLine(c, d)); s.AddEntity(new SkArc(new SkPoint(-20, 0), d, a) { CCW = true });
            var ex = new ExtrudeFeature("E") { SketchId = s.Id }; ex.Side1.End = EndType.Distance; ex.Side1.Distance = new ValueRef(30); p.AddFeature(ex); p.Rebuild(null);
            Show("stadium 40+2·r5, h30", p, 40 * 10 * 30 + Math.PI * 25 * 30);
        }
        foreach (double t in (Environment.GetEnvironmentVariable("THIN") ?? "0.1,0.01,0.001,0.0001").Split(',').Select(double.Parse))
        {
            var p = new Part("plate"); var s = PolySketch(p, XYat(0), (0, 0), (100, 0), (100, 100), (0, 100));
            var ex = new ExtrudeFeature("E") { SketchId = s.Id }; ex.Side1.End = EndType.Distance; ex.Side1.Distance = new ValueRef(t); p.AddFeature(ex); p.Rebuild(null);
            Show($"plate 100×100×{t}", p, 1e4 * t);
            var q = new Part("wall"); var s2 = PolySketch(q, XYat(0), (0, 0), (100, 0), (100, t), (0, t));
            var e2 = new ExtrudeFeature("E") { SketchId = s2.Id }; e2.Side1.End = EndType.Distance; e2.Side1.Distance = new ValueRef(50); q.AddFeature(e2); q.Rebuild(null);
            Show($"wall 100×{t}×50", q, 100 * t * 50);
            // карман, оставляющий стенку t: брусок 20×20×20, вырез 20-t по x
            var r = new Part("pocket"); var s3 = PolySketch(r, XYat(0), (0, 0), (20, 0), (20, 20), (0, 20));
            var e3 = new ExtrudeFeature("E") { SketchId = s3.Id }; e3.Side1.End = EndType.Distance; e3.Side1.Distance = new ValueRef(20); r.AddFeature(e3);
            var s4 = PolySketch(r, XYat(20), (t, -5), (25, -5), (25, 25), (t, 25));
            var c4 = new ExtrudeFeature("C") { SketchId = s4.Id, Cut = true }; c4.Side1.End = EndType.Distance; c4.Side1.Distance = new ValueRef(10); c4.Side1.Flip = true; r.AddFeature(c4); r.Rebuild(null);
            Show($"block with a cut leaving wall {t}", r, 8000 - (20 - t) * 20 * 10);
            // тонкая труба: цилиндр r10 минус r(10-t)
            var u = new Part("tube"); var s5 = CircleSketch(u, 0, 0, 0, 10); s5.AddEntity(new SkCircle(new SkPoint(0, 0), 10 - t));
            var e5 = new ExtrudeFeature("E") { SketchId = s5.Id }; e5.Side1.End = EndType.Distance; e5.Side1.Distance = new ValueRef(30); u.AddFeature(e5); u.Rebuild(null);
            Show($"tube wall {t}", u, Math.PI * (100 - (10 - t) * (10 - t)) * 30);
            // отверстие близко к краю: стенка t
            var h = new Part("edgehole"); var s6 = PolySketch(h, XYat(0), (0, 0), (40, 0), (40, 40), (0, 40));
            var e6 = new ExtrudeFeature("E") { SketchId = s6.Id }; e6.Side1.End = EndType.Distance; e6.Side1.Distance = new ValueRef(10); h.AddFeature(e6);
            var hf = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(10) }; hf.Points.Add(new Vector3d(5 + t, 20, 10)); h.AddFeature(hf); h.Rebuild(null);
            Show($"hole wall {t} from the edge", h, 16000 - Math.PI * 25 * 10);
        }
    }
}
