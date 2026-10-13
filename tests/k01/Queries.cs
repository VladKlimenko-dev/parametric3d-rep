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

    /// <summary>
    /// m.q1, m.q3: КЛАССИФИКАЦИЯ ТОЧКИ всех видов граней (плоскость, цилиндр, конус, шар, тор, NURBS необрезанная и обрезанная)
    /// против формул; ПРОЕКЦИИ на грань и ребро; РАССТОЯНИЕ ТЕЛО–ТЕЛО против формул (шар–шар, брусок–брусок, цилиндр–шар,
    /// тор–шар, повёрнутые, NURBS–шар, пересекающиеся — 0).
    /// </summary>
    static void QueryChecks2()
    {
        Check("Q1-CLASSIFY-ALL-KINDS", () =>
        {
            var fails = new List<string>(); var rnd = new Random(20261014);
            void Run(string name, TSolid body, Func<Vector3d, double> sdf, Vector3d lo, Vector3d hi)
            {
                var cls = BodyQuery.Classifier(body); int n = 0, wrong = 0, unknown = 0; string first = "";
                for (int i = 0; i < 4000; i++)
                {
                    var p = new Vector3d(lo.X + (hi.X - lo.X) * rnd.NextDouble(), lo.Y + (hi.Y - lo.Y) * rnd.NextDouble(), lo.Z + (hi.Z - lo.Z) * rnd.NextDouble());
                    double d = sdf(p); if (Math.Abs(d) < 1e-6) continue;      // у границы — не мерило
                    var c = cls(p); n++;
                    if (c == PointClass.Unknown) { unknown++; continue; }
                    if ((c == PointClass.Inside) != (d < 0) || c == PointClass.OnBoundary) { wrong++; if (first == "") first = $"{p:0.###} {c} sdf {d:G3}"; }
                }
                Log(new { test = "Q1-CLASSIFY-ALL-KINDS", name, n, wrong, unknown });
                if (wrong > 0 || unknown > 0) fails.Add($"{name}: {wrong} wrong, {unknown} unknown of {n} (first {first})");
            }
            double BoxSdf(Vector3d p, Vector3d a, Vector3d b) { var c = (a + b) / 2; var h = (b - a) / 2; var q = new Vector3d(Math.Abs(p.X - c.X) - h.X, Math.Abs(p.Y - c.Y) - h.Y, Math.Abs(p.Z - c.Z) - h.Z); return Math.Max(q.X, Math.Max(q.Y, q.Z)) > 0 ? Math.Max(q.X, Math.Max(q.Y, q.Z)) : Math.Max(q.X, Math.Max(q.Y, q.Z)); }
            Run("box", Box(0, 0, 0, 60, 40, 20), p => BoxSdf(p, Vector3d.Zero, new(60, 40, 20)), new(-5, -5, -5), new(65, 45, 25));
            Run("cylinder", PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), p => Math.Max(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 10, Math.Max(-p.Z, p.Z - 30)), new(-12, -12, -2), new(12, 12, 32));
            Run("cone", PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 20), p => Math.Max(Math.Sqrt(p.X * p.X + p.Y * p.Y) - (10 - 0.3 * p.Z), Math.Max(-p.Z, p.Z - 20)), new(-12, -12, -2), new(12, 12, 22));
            Run("sphere", PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 8), p => (p - new Vector3d(1, 2, 3)).Length - 8, new(-9, -8, -7), new(11, 12, 13));
            Run("torus", PrimitiveOp.Torus(Vector3d.Zero, 12, 3), p => { double r = Math.Sqrt(p.X * p.X + p.Y * p.Y) - 12; return Math.Sqrt(r * r + p.Z * p.Z) - 3; }, new(-16, -16, -4), new(16, 16, 4));
            double QuarterSdf(Vector3d p) => Math.Max(Math.Max(-p.X, -p.Y), Math.Max(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 10, Math.Max(-p.Z, p.Z - 20)));
            Run("NURBS quarter cylinder", NurbsQuarterCylinder(), QuarterSdf, new(-2, -2, -2), new(12, 12, 22));
            {
                // обрезанная грань NURBS: четверть цилиндра минус поперечный цилиндр r3 через её NURBS-грань
                var bl = new List<TSolid> { NurbsQuarterCylinder() };
                var tool = TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(-5, 5, 10), 3, 30), TransformOp.Rotate(new Vector3d(-5, 5, 10), Vector3d.UnitY, 90));
                BooleanOp.Subtract(bl, tool, new List<string>());
                Require(bl.Count == 1, "trimmed case: " + bl.Count + " bodies");
                Run("NURBS trimmed (quarter cylinder minus a cross hole)", bl[0], p => Math.Max(QuarterSdf(p), 3 - Math.Sqrt((p.Y - 5) * (p.Y - 5) + (p.Z - 10) * (p.Z - 10))), new(-2, -2, -2), new(12, 12, 22));
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("Q3-PROJECT-AND-BODY-DISTANCE", () =>
        {
            var fails = new List<string>();
            void Near(string name, double got, double want, double tol = 1e-9) { if (!(Math.Abs(got - want) <= tol * Math.Max(1, Math.Abs(want)))) fails.Add($"{name}: {got:R} vs {want:R}"); }
            void D(string name, TSolid a, TSolid b, double want, double tol = 1e-9)
            {
                var r = BodyQuery.Distance(a, b);
                if (r == null) { fails.Add($"{name}: no answer"); return; }
                Log(new { test = "Q3-PROJECT-AND-BODY-DISTANCE", name, r.Value.Distance, want, r.Value.WhereA, r.Value.WhereB });
                Near(name, r.Value.Distance, want, tol);
                if (want > 0) Near(name + " (points)", (r.Value.A - r.Value.B).Length, r.Value.Distance, tol);
            }
            // проекции
            var box = Box(0, 0, 0, 60, 40, 20);
            var top = box.Faces.Single(f => f.Loops.SelectMany(l => l.CoEdges).All(c => Math.Abs(c.Start.P.Z - 20) < 1e-9));
            Near("project to top face inside", (BodyQuery.ProjectToFace(top, new(30, 20, 50))!.Value - new Vector3d(30, 20, 20)).Length, 0);
            Near("project to top face outside", (BodyQuery.ProjectToFace(top, new(70, 20, 50))!.Value - new Vector3d(60, 20, 20)).Length, 0);
            var cyl = PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30);
            var side = cyl.Faces.Single(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface or Parametric3D.Kernel.Surfaces.RevolvedSurface);
            Near("project to cylinder side", (BodyQuery.ProjectToFace(side, new(20, 0, 15))!.Value - new Vector3d(10, 0, 15)).Length, 0);
            Near("project to cylinder side beyond the rim", (BodyQuery.ProjectToFace(side, new(20, 0, 40))!.Value - new Vector3d(10, 0, 30)).Length, 0, 1e-7);
            var rim = cyl.Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 && Math.Abs(e.Curve.PointAt(0).Z - 30) < 1e-9);
            Near("project to rim edge", (BodyQuery.ProjectToEdge(rim, new(0, 20, 35)) - new Vector3d(0, 10, 30)).Length, 0);
            var nq = NurbsQuarterCylinder();
            var nface = nq.Faces.Single(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface);
            Near("project to NURBS face", (BodyQuery.ProjectToFace(nface, new(12, 12, 7))!.Value - new Vector3d(Math.Sqrt(50), Math.Sqrt(50), 7)).Length, 0, 1e-7);
            Near("closest on NURBS body", BodyQuery.Closest(nq, new(12, 12, 7))!.Value.Distance, Math.Sqrt(288) - 10, 1e-7);
            Near("support of NURBS body along (1,1,0)", BodyQuery.Support(nq, new Vector3d(1, 1, 0).Normalized()) ?? double.NaN, 10, 1e-7);
            {
                var bb = BodyQuery.ExactBounds(nq);
                if (bb == null) fails.Add("NURBS bounds: no answer");
                else { Near("NURBS bounds min", (bb.Value.Min - Vector3d.Zero).Length, 0, 1e-7); Near("NURBS bounds max", (bb.Value.Max - new Vector3d(10, 10, 20)).Length, 0, 1e-7); }
                var rq = TransformOp.Copy(nq, TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitZ, 45));
                Near("rotated NURBS support along +Y", BodyQuery.Support(rq, Vector3d.UnitY) ?? double.NaN, 10, 1e-7);
            }
            // расстояния тело–тело
            D("sphere–sphere", PrimitiveOp.Sphere(Vector3d.Zero, 5), PrimitiveOp.Sphere(new(20, 3, 4), 6), Math.Sqrt(425) - 11);
            D("box–box (faces)", Box(0, 0, 0, 10, 10, 10), Box(15, 2, 3, 5, 5, 5), 5);
            D("box–box (corners)", Box(0, 0, 0, 10, 10, 10), Box(13, 14, 22, 5, 5, 5), Math.Sqrt(9 + 16 + 144));
            D("box–box (edges crossing)", Box(0, 0, 0, 10, 10, 10), TransformOp.Copy(Box(-5, 12, -2, 20, 3, 3), TransformOp.Rotate(new(5, 13.5, -0.5), Vector3d.UnitY, 0)), 2);
            D("cylinder–sphere (side)", PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), PrimitiveOp.Sphere(new(25, 0, 15), 4), 11);
            D("cylinder–sphere (rim)", PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), PrimitiveOp.Sphere(new(20, 0, 40), 4), Math.Sqrt(200) - 4);
            D("torus–sphere (in the hole)", PrimitiveOp.Torus(Vector3d.Zero, 12, 3), PrimitiveOp.Sphere(Vector3d.Zero, 4), 5);
            D("cone–plane box", PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 20), Box(-20, -20, 25, 40, 40, 5), 5);
            D("cylinders, crossing axes", PrimitiveOp.Cylinder(new(0, 0, -20), 3, 40), TransformOp.Copy(PrimitiveOp.Cylinder(new(10, 0, -20), 2, 40), TransformOp.Rotate(new(10, 0, 0), Vector3d.UnitX, 90)), 5, 1e-7);
            var rot = TransformOp.Rotate(new(1, 2, 3), new Vector3d(0.4, -0.7, 0.59).Normalized(), 33);
            D("rotated cylinder–sphere", TransformOp.Copy(PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), rot), TransformOp.Copy(PrimitiveOp.Sphere(new(25, 0, 15), 4), rot), 11, 1e-7);
            D("NURBS quarter cylinder–sphere", NurbsQuarterCylinder(), PrimitiveOp.Sphere(new(12, 12, 10), 2), Math.Sqrt(288) - 12, 1e-7);
            D("overlapping", Box(0, 0, 0, 10, 10, 10), PrimitiveOp.Sphere(new(10, 10, 10), 3), 0);
            D("contained", Box(0, 0, 0, 10, 10, 10), PrimitiveOp.Sphere(new(5, 5, 5), 2), 0);
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
