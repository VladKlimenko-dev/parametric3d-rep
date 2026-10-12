using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Query;
using Parametric3D.Kernel.Topology;

/// <summary>m.q3: расстояния и точный габарит — против замкнутых формул.</summary>
static partial class Program
{
    static void QueryChecks()
    {
        Check("Q3-DISTANCE-AND-BOUNDS", () =>
        {
            var fails = new List<string>();
            void Dist(string name, TSolid b, Vector3d p, double expected)
            {
                var r = BodyQuery.Closest(b, p);
                if (r == null) { fails.Add($"{name}: no answer"); return; }
                if (!(Math.Abs(r.Value.Distance - expected) <= 1e-9 * Math.Max(1, expected))) fails.Add($"{name}: distance {r.Value.Distance:R} vs {expected:R} ({r.Value.Where})");
            }
            void Box3(string name, TSolid b, Vector3d lo, Vector3d hi)
            {
                var bb = BodyQuery.ExactBounds(b);
                if (bb == null) { fails.Add($"{name}: no bounds"); return; }
                if ((bb.Value.Min - lo).Length > 1e-9 || (bb.Value.Max - hi).Length > 1e-9) fails.Add($"{name}: bounds {bb.Value.Min:0.#########}–{bb.Value.Max:0.#########} vs {lo:0.#########}–{hi:0.#########}");
            }
            var box = Box(0, 0, 0, 60, 40, 20);
            Dist("box corner", box, new(70, 50, 30), Math.Sqrt(300));
            Dist("box face", box, new(30, 20, 25), 5);
            Dist("box inside", box, new(30, 20, 12), 8);
            Box3("box", box, Vector3d.Zero, new(60, 40, 20));
            var cyl = PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30);
            Dist("cylinder side", cyl, new(20, 0, 15), 10);
            Dist("cylinder cap", cyl, new(3, 4, 40), 10);
            Dist("cylinder rim", cyl, new(20, 0, 40), Math.Sqrt(200));
            var sph = PrimitiveOp.Sphere(Vector3d.Zero, 8);
            Dist("sphere outside", sph, new(10, 0, 0), 2);
            Dist("sphere inside", sph, new(0, 0, 1), 7);
            Box3("sphere", sph, new(-8, -8, -8), new(8, 8, 8));
            var tor = PrimitiveOp.Torus(Vector3d.Zero, 12, 3);
            Dist("torus above", tor, new(12, 0, 10), 7);
            Dist("torus centre", tor, Vector3d.Zero, 9);
            Dist("torus outside", tor, new(20, 0, 0), 5);
            Box3("torus", tor, new(-15, -15, -3), new(15, 15, 3));
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), new List<string>());
                Dist("plate hole axis", bl[0], new(30, 20, 10), 8);
                Dist("plate above the hole", bl[0], new(30, 20, 25), Math.Sqrt(64 + 25));
                Box3("plate with hole", bl[0], Vector3d.Zero, new(60, 40, 20));
            }
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; FilletOp.RoundEdges(bl, bl[0].Edges.Select(EdgeRef.Of).ToList(), 3, new List<string>());
                Dist("rounded box corner", bl[0], new(70, 50, 30), Math.Sqrt(3 * 169) - 3);
                Box3("rounded box", bl[0], Vector3d.Zero, new(60, 40, 20));
            }
            // повороты: расстояние не меняется; габарит — по формулам опорной функции
            var rot = TransformOp.Rotate(new Vector3d(1, 2, 3), new Vector3d(0.4, -0.7, 0.59).Normalized(), 33);
            {
                var rb = TransformOp.Copy(box, rot);
                Dist("rotated box corner", rb, rot.Point(new(70, 50, 30)), Math.Sqrt(300));
                var corners = new[] { 0, 60 }.SelectMany(x => new[] { 0, 40 }.SelectMany(y => new[] { 0, 20 }.Select(z => rot.Point(new Vector3d(x, y, z))))).ToList();
                Box3("rotated box", rb, corners.Aggregate(Vector3d.ComponentMin), corners.Aggregate(Vector3d.ComponentMax));
            }
            {
                var rc = TransformOp.Copy(cyl, rot);
                var c = rot.Point(new Vector3d(0, 0, 15)); var w = (rot.Point(new Vector3d(0, 0, 1)) - rot.Point(Vector3d.Zero)).Normalized();
                Vector3d Ext() => new(15 * Math.Abs(w.X) + 10 * Math.Sqrt(1 - w.X * w.X), 15 * Math.Abs(w.Y) + 10 * Math.Sqrt(1 - w.Y * w.Y), 15 * Math.Abs(w.Z) + 10 * Math.Sqrt(1 - w.Z * w.Z));
                Box3("rotated cylinder", rc, c - Ext(), c + Ext());
                Dist("rotated cylinder side", rc, rot.Point(new(20, 0, 15)), 10);
            }
            {
                var rt = TransformOp.Copy(tor, rot);
                var c = rot.Point(Vector3d.Zero); var w = (rot.Point(new Vector3d(0, 0, 1)) - c).Normalized();
                Vector3d Ext() => new(12 * Math.Sqrt(1 - w.X * w.X) + 3, 12 * Math.Sqrt(1 - w.Y * w.Y) + 3, 12 * Math.Sqrt(1 - w.Z * w.Z) + 3);
                Box3("rotated torus", rt, c - Ext(), c + Ext());
                Dist("rotated torus above", rt, rot.Point(new(12, 0, 10)), 7);
            }
            {
                var cone = TransformOp.Copy(PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 20), rot);
                var c0 = rot.Point(Vector3d.Zero); var c1 = rot.Point(new Vector3d(0, 0, 20)); var w = (c1 - c0).Normalized();
                double Sup(Vector3d d) => Math.Max(Vector3d.Dot(c0, d) + 10 * Math.Sqrt(Math.Max(0, 1 - Vector3d.Dot(w, d) * Vector3d.Dot(w, d))), Vector3d.Dot(c1, d) + 4 * Math.Sqrt(Math.Max(0, 1 - Vector3d.Dot(w, d) * Vector3d.Dot(w, d))));
                Box3("rotated cone", cone, new(-Sup(-Vector3d.UnitX), -Sup(-Vector3d.UnitY), -Sup(-Vector3d.UnitZ)), new(Sup(Vector3d.UnitX), Sup(Vector3d.UnitY), Sup(Vector3d.UnitZ)));
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
