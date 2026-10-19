using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// КНОПКА «ТЕСТ» (2026-10-20): ПЛОСКОСТЬ ГРАНИ ДЛЯ ЭСКИЗА — НАРУЖНАЯ НОРМАЛЬ. SketchFaceRef.PlaneOf у граней после
    /// булевых (стороны задают фасеты, Reversed = false) отдавал нормаль плоскости-источника — у части граней внутрь тела:
    /// «верхняя» грань для ручки и эскиза оказывалась дном. Тела: бруски после фаски, вычитания бруска, цилиндра и шара,
    /// скругления; у каждой плоской грани точка чуть вдоль нормали — снаружи, против — внутри (классификатор тела).
    /// </summary>
    static void Krn08PlaneSideChecks()
    {
        Check("KRN08-FACE-PLANE-OUTWARD", () =>
        {
            var fails = new List<string>(); var w = new List<string>();
            var bodies = new List<(string, TSolid)>();
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, Box(10, 10, 10, 15, 10, 20), w); bodies.Add(("pocket", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), w); bodies.Add(("hole", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Sphere(new Vector3d(20, 15, 20), 8), w); bodies.Add(("sphere cut", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Unite(l, Box(10, 10, 20, 10, 10, 10), w); bodies.Add(("boss", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 30, 20, 10) }; var b0 = l[0]; FilletOp.RoundEdges(l, b0.Edges.Take(3).Select(EdgeRef.Of).ToList(), 2, w); bodies.Add(("rounded", l[0])); }
            int planar = 0, faceted = 0;
            foreach (var (name, b) in bodies)
            {
                var pb = Parametric3D.Kernel.Query.PreparedBody.For(b);
                foreach (var f in b.Faces)
                {
                    if (Parametric3D.Model.Features.SketchFaceRef.PlaneOf(f) is not { } pl) continue;
                    planar++; if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface) faceted++;
                    // точка внутри грани: середина самого длинного ребра, сдвинутая к центру грани
                    var e = f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge).OrderByDescending(x => (x.V1.P - x.V0.P).Length).First();
                    var c = f.Loops.SelectMany(l => l.CoEdges).Select(x => x.Edge.V0.P).Aggregate(Vector3d.Zero, (a, q) => a + q) / f.Loops.Sum(l => l.CoEdges.Count);
                    var m = (e.V0.P + e.V1.P) / 2; var q0 = m + (c - m) * 0.02;
                    var n = pl.N.Normalized();
                    var outside = pb.Classify(q0 + n * 0.05); var inside = pb.Classify(q0 - n * 0.05);
                    if (outside != Parametric3D.Kernel.Query.PointClass.Outside || inside != Parametric3D.Kernel.Query.PointClass.Inside)
                        fails.Add($"{name}: face {f.Tag} ({f.Surface.GetType().Name}, rev {f.Reversed}) plane normal {n:0.###}: +n {outside}, −n {inside}");
                }
            }
            Log(new { test = "KRN08-FACE-PLANE-OUTWARD", bodies = bodies.Count, planar, faceted });
            if (faceted == 0) fails.Add("no faceted planar face exercised");
            Require(fails.Count == 0, string.Join("; ", fails.Take(6)));
        });
    }
}
