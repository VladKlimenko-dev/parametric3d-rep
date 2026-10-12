using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>07.7: ссылка на ребро экземпляра массива переживает правку шага и числа копий.</summary>
    static (Part P, LinearPatternFeature L, RoundFeature R, HoleFeature H) InstanceCase()
    {
        Frame3 xy = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        var p = new Part("inst"); var s = PolySketch(p, xy, (0, 0), (10, 0), (10, 10), (0, 10));
        var ex = new ExtrudeFeature("E") { SketchId = s.Id }; ex.Side1.End = EndType.Distance; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
        var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(4) }; h.Points.Add(new Vector3d(5, 5, 10)); p.AddFeature(h);
        var lp = new LinearPatternFeature("P") { Step = new Vector3d(20, 0, 0), Count = new ValueRef(3) }; p.AddFeature(lp); p.Rebuild(null);
        // ребро входа отверстия ТРЕТЬЕГО экземпляра (x ≈ 45)
        var e = p.Bodies.SelectMany(b => b.Edges).Where(x => x.FaceA != null && x.FaceB != null && x.Curve.PointAt(0.5).Z > 9.9
                  && (x.FaceA.Tag.Contains("hole:") || x.FaceB.Tag.Contains("hole:")) && x.Curve.PointAt(0.5).X > 40).ToList();
        var rf = new RoundFeature("R") { Radius = new ValueRef(0.5) }; rf.Edges.AddRange(e.Select(EdgeRef.Of)); p.AddFeature(rf); p.Rebuild(null);
        return (p, lp, rf, h);
    }

    static void InstanceProbe()
    {
        var (p, lp, rf, h) = InstanceCase();
        double Cx() { var f = p.Bodies.SelectMany(b => b.Faces).Where(x => x.Tag.StartsWith("#" + rf.Key + "/")).ToList();
            return f.Count == 0 ? double.NaN : f.SelectMany(x => x.Loops).SelectMany(l => l.CoEdges).Average(ce => ce.Edge.Curve.PointAt(0.5).X); }
        void Show(string w) => Console.WriteLine($"INST {w}: V {p.Volume:F4} round err {rf.Error ?? "-"} round faces at x≈{Cx():F3}; bodies {p.Bodies.Count}\n   W: {string.Join(" | ", p.Warnings)}");
        Show("×3 step 20");
        foreach (var b in p.Bodies) Console.WriteLine("BODY " + string.Join(", ", b.Faces.Select(f => EdgeRef.Bare(f.Tag) + "/" + f.Surface.GetType().Name + "/L" + f.Loops.Count)));
        lp.Count = new ValueRef(5); p.MarkDirtyFrom(lp); p.Rebuild(null); Show("×5 step 20");
        lp.Step = new Vector3d(25, 0, 0); p.MarkDirtyFrom(lp); p.Rebuild(null); Show("×5 step 25");
        lp.Count = new ValueRef(2); p.MarkDirtyFrom(lp); p.Rebuild(null); Show("×2 step 25 (instance gone)");
    }
}
