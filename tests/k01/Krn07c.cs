using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

/// <summary>KRN-07 (07.7): массивы и зеркала — пробы.</summary>
static partial class Program
{
    static void Krn07cChecks()
    {
        Check("KRN07-PATTERN-MIRROR-ACCEPTANCE", () =>
        {
            Frame3 XYat(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
            var fails = new List<string>();
            void Want(string name, Part p, double v, int bodies)
            {
                var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Error));
                if (errs.Length > 0 || p.Integrity != null) fails.Add($"{name}: {errs} {p.Integrity}");
                else if (Math.Abs(p.Volume - v) > 1e-9 * v || p.Bodies.Count != bodies) fails.Add($"{name}: V={p.Volume:R} bodies {p.Bodies.Count}, expected {v:R} / {bodies}");
            }
            Part Box(string n, double x0, double x1, double y0, double y1, double h)
            {
                var p = new Part(n); var s = PolySketch(p, XYat(0), (x0, y0), (x1, y0), (x1, y1), (x0, y1));
                var ex = new ExtrudeFeature("E") { SketchId = s.Id }; ex.Side1.End = EndType.Distance; ex.Side1.Distance = new ValueRef(h); p.AddFeature(ex); return p;
            }
            Part TwoBodies(string n, double x0)
            {
                var p = Box(n, x0, 10, 0, 10, 10);
                var s2 = PolySketch(p, XYat(0), (x0, 30), (10, 30), (10, 40), (x0, 40));
                var e2 = new ExtrudeFeature("E2") { SketchId = s2.Id }; e2.Side1.End = EndType.Distance; e2.Side1.Distance = new ValueRef(10); p.AddFeature(e2);
                return p;
            }
            {   // правка числа копий и подавление
                var p = Box("lin", 0, 10, 0, 10, 10); var lp = new LinearPatternFeature("P") { Step = new Vector3d(20, 0, 0), Count = new ValueRef(3) }; p.AddFeature(lp); p.Rebuild(null);
                Want("linear ×3", p, 3000, 3);
                lp.Count = new ValueRef(5); p.MarkDirtyFrom(lp); p.Rebuild(null); Want("linear ×5 after the edit", p, 5000, 5);
                lp.Suppressed = true; p.MarkDirtyFrom(lp); p.Rebuild(null); Want("suppressed", p, 1000, 1);
                lp.Suppressed = false; lp.Count = new ValueRef(2); p.MarkDirtyFrom(lp); p.Rebuild(null); Want("unsuppressed ×2", p, 2000, 2);
            }
            {   // пересечение экземпляров — объединение
                var p = Box("ov", 0, 10, 0, 10, 10); p.AddFeature(new LinearPatternFeature("P") { Step = new Vector3d(5, 0, 0), Count = new ValueRef(3) }); p.Rebuild(null);
                Want("overlapping copies", p, 2000, 1);
                var c = Box("co", 0, 20, -5, 5, 10); c.AddFeature(new CircularPatternFeature("C") { At = Vector3d.Zero, Axis = Vector3d.UnitZ, Count = new ValueRef(4) }); c.Rebuild(null);
                Want("circular ×4 crossing at the axis", c, (4 * 200 - 4 * 25) * 10, 1);
            }
            {   // зеркала: касание, пересечение плоскости, наклонная плоскость, тело с отверстием
                var p = Box("mt", 0, 10, 0, 10, 10); p.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); p.Rebuild(null);
                Want("mirror touching the plane", p, 2000, 1);
                var q = Box("mc", -3, 10, 0, 10, 10); q.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); q.Rebuild(null);
                Want("mirror crossing the plane", q, 2000, 1);
                var r = Box("mo", 5, 10, 0, 10, 10); r.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = new Vector3d(1, 1, 0) }); r.Rebuild(null);
                Want("mirror on an oblique plane", r, 1000, 2);
                var h = Box("mh", 5, 25, 0, 10, 10);
                var hole = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(4) }; hole.Points.Add(new Vector3d(10, 5, 10)); h.AddFeature(hole);
                h.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); h.Rebuild(null);
                Want("mirror of a body with a hole", h, 2 * (2000 - Math.PI * 4 * 10), 2);
            }
            {   // несколько тел: копируются все
                var p = TwoBodies("mb", 0); p.AddFeature(new LinearPatternFeature("P") { Step = new Vector3d(20, 0, 0), Count = new ValueRef(2) }); p.Rebuild(null);
                Want("two bodies, linear ×2", p, 4000, 4);
                var q = TwoBodies("mb2", 5); q.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); q.Rebuild(null);
                Want("two bodies, mirror", q, 2000, 4);
                var c = TwoBodies("mb3", 0); c.AddFeature(new CircularPatternFeature("C") { At = new Vector3d(100, 0, 0), Axis = Vector3d.UnitZ, Count = new ValueRef(3) }); c.Rebuild(null);
                Want("two bodies, circular ×3", c, 6000, 6);
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void PatternProbe()
    {
        Frame3 XYat(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        void Show(string name, Part p, double expected)
        {
            var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Error));
            var tags = p.Bodies.SelectMany(b => b.Faces).Select(f => f.Tag).ToList();
            int dupTags = tags.Count - tags.Distinct().Count();
            Console.WriteLine($"{name}: bodies {p.Bodies.Count}, V={p.Volume:0.####} exp {expected:0.####}, err=[{errs}], integrity={p.Integrity ?? "ok"}, faces {tags.Count}, duplicate names {dupTags}");
        }
        Part Box(string n, double x0, double x1, double y0, double y1, double h)
        {
            var p = new Part(n); var s = PolySketch(p, XYat(0), (x0, y0), (x1, y0), (x1, y1), (x0, y1));
            var ex = new ExtrudeFeature("E") { SketchId = s.Id }; ex.Side1.End = EndType.Distance; ex.Side1.Distance = new ValueRef(h); p.AddFeature(ex); return p;
        }
        {   // массив 3, затем 5, затем 2, подавление
            var p = Box("lin", 0, 10, 0, 10, 10); var lp = new LinearPatternFeature("P") { Step = new Vector3d(20, 0, 0), Count = new ValueRef(3) }; p.AddFeature(lp); p.Rebuild(null);
            Show("linear ×3", p, 3000);
            lp.Count = new ValueRef(5); p.MarkDirtyFrom(lp); p.Rebuild(null); Show("linear ×5 after edit", p, 5000);
            lp.Suppressed = true; p.MarkDirtyFrom(lp); p.Rebuild(null); Show("linear suppressed", p, 1000);
            lp.Suppressed = false; lp.Count = new ValueRef(2); p.MarkDirtyFrom(lp); p.Rebuild(null); Show("linear ×2 unsuppressed", p, 2000);
        }
        {   // перекрывающиеся экземпляры: шаг 5 при ширине 10
            var p = Box("ov", 0, 10, 0, 10, 10); p.AddFeature(new LinearPatternFeature("P") { Step = new Vector3d(5, 0, 0), Count = new ValueRef(3) }); p.Rebuild(null);
            Show("linear overlapping (step 5, width 10)", p, 20 * 10 * 10);
        }
        {   // зеркало: тело касается плоскости — сливается; тело пересекает плоскость
            var p = Box("mt", 0, 10, 0, 10, 10); p.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); p.Rebuild(null);
            Show("mirror touching the plane", p, 2000);
            var q = Box("mc", -3, 10, 0, 10, 10); q.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); q.Rebuild(null);
            Show("mirror crossing the plane", q, 20 * 10 * 10);
            var r = Box("mo", 5, 10, 0, 10, 10); r.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = new Vector3d(1, 1, 0) }); r.Rebuild(null);
            Show("mirror oblique plane (x+y=0)", r, 2 * 500);
        }
        {   // зеркало тела с отверстием (ориентация, рука)
            var p = Box("mh", 5, 25, 0, 10, 10);
            var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(4) }; h.Points.Add(new Vector3d(10, 5, 10)); p.AddFeature(h);
            p.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); p.Rebuild(null);
            Show("mirror with a hole", p, 2 * (2000 - Math.PI * 4 * 10));
        }
        {   // круговой массив перекрывающихся
            var p = Box("co", 0, 20, -5, 5, 10); p.AddFeature(new CircularPatternFeature("C") { At = Vector3d.Zero, Axis = Vector3d.UnitZ, Count = new ValueRef(4) }); p.Rebuild(null);
            Show("circular ×4 overlapping at the axis (cross)", p, (4 * 200 - 4 * 100 + 100) * 10 - 0);
        }
        {   // два тела: массив копирует только первое?
            var p = Box("mb", 0, 10, 0, 10, 10);
            var s2 = PolySketch(p, XYat(0), (0, 30), (10, 30), (10, 40), (0, 40));
            var e2 = new ExtrudeFeature("E2") { SketchId = s2.Id }; e2.Side1.End = EndType.Distance; e2.Side1.Distance = new ValueRef(10); p.AddFeature(e2);
            p.AddFeature(new LinearPatternFeature("P") { Step = new Vector3d(20, 0, 0), Count = new ValueRef(2) }); p.Rebuild(null);
            Show("two bodies, linear ×2", p, 4000);
            var q = Box("mb2", 5, 10, 0, 10, 10);
            var s3 = PolySketch(q, XYat(0), (5, 30), (10, 30), (10, 40), (5, 40));
            var e3 = new ExtrudeFeature("E2") { SketchId = s3.Id }; e3.Side1.End = EndType.Distance; e3.Side1.Distance = new ValueRef(10); q.AddFeature(e3);
            q.AddFeature(new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitX }); q.Rebuild(null);
            Show("two bodies, mirror", q, 2000);
        }
    }
}
