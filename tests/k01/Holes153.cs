using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k153 (замечание 04.11, 10:34, пп. 3–4): ОТВЕРСТИЯ — дно-острие сверла и привязки положения.
    /// 1. Острие: глухое d10 глубиной 20 с остриём 118° в бруске — снятый объём = πr²·20 + πr²·h/3, h = r / tg 59° (точные объёмы
    ///    до и после, до 1e-6); роль грани острия — hole:drill-point. P3D_HOLEDRILLPOINT=0 — дно плоское (объём меньше).
    /// 2. Линейная привязка: брусок 100×60×30, отверстие на верхней грани в 20 от кромки x = 100 и 15 от кромки y = 60 —
    ///    стенка с осью (80, 45); длина бруска 120 — отверстие уходит вместе с кромкой: ось (100, 45). P3D_HOLEPLACE=0 — на месте.
    /// 3. Радиальная привязка: вал R20 вдоль Z длиной 100, отверстие в боковой грани в 30 от нижнего торца под углом 90° от +X —
    ///    ось отверстия проходит через (0, 20, 30) вдоль −Y; вал длиннее снизу (торец z = −10) — отверстие на z = 20.
    /// </summary>
    static void Hole153Checks()
    {
        Frame3 XY(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        Part Block(double L, out Sketch s, out ExtrudeFeature ex)
        {
            var p = new Part("blk");
            s = PolySketch(p, XY(0), (0, 0), (L, 0), (L, 60), (0, 60));
            ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(30); p.AddFeature(ex);
            return p;
        }
        static double Vol(Part p) => AnalyticVolume.Of(p.Bodies[0]) ?? double.NaN;
        static Vector3d? WallAxisPoint(Part p, double z)
        {
            foreach (var f in p.Bodies[0].Faces)
                if (f.Tag.EndsWith(HoleFeature.RoleWall) && Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Cylinder c)
                {
                    var a = c.Axis.Normalized();
                    if (Math.Abs(a.Z) > 0.5) return c.P + a * ((z - c.P.Z) / a.Z);
                    return c.P;
                }
            return null;
        }

        Check("KRN-HOLE-DRILLPOINT-K153", () =>
        {
            var p = Block(100, out _, out _); p.Rebuild(null);
            double v0 = Vol(p);
            var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(10), Depth = new ValueRef(20), DrillPoint = true };
            h.Points.Add(new Vector3d(50, 30, 30)); p.AddFeature(h); p.Rebuild(null);
            Require(h.Error == null, h.Error ?? "");
            double r = 5, tip = r / Math.Tan(59 * Math.PI / 180);
            double want = Math.PI * r * r * 20 + Math.PI * r * r * tip / 3, got = v0 - Vol(p);
            Log(new { test = "KRN-HOLE-DRILLPOINT-K153", removed = got, want, g = p.Bodies[0].Guarantee.ToString(), roles = string.Join(",", p.Bodies[0].Faces.Select(f => f.Tag).Where(t => t.Contains("hole")).Distinct()) });
            Require(Math.Abs(got - want) <= 1e-6 * want, $"removed {got} vs {want}");
            Require(p.Bodies[0].Faces.Any(f => f.Tag.EndsWith(HoleFeature.RoleDrillPoint)), "no drill-point face");
            Require(SolidValidator.Validate(p.Bodies[0]).IsValid, "invalid body");
        });

        Check("KRN-HOLE-LINEAR-K153", () =>
        {
            var p = Block(100, out var s, out _); p.Rebuild(null);
            TEdge Edge(Func<Vector3d, bool> on) => p.Bodies[0].Faces.SelectMany(f => f.Edges).Distinct().First(e => on(e.V0.P) && on(e.V1.P));
            var ex = Edge(q => Math.Abs(q.X - 100) < 1e-6 && Math.Abs(q.Z - 30) < 1e-6);
            var ey = Edge(q => Math.Abs(q.Y - 60) < 1e-6 && Math.Abs(q.Z - 30) < 1e-6);
            var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(8), ThroughAll = true,
                LinRef1 = EdgeRef.Of(ex), LinD1 = new ValueRef(20), LinRef2 = EdgeRef.Of(ey), LinD2 = new ValueRef(15) };
            h.Points.Add(new Vector3d(70, 40, 30)); p.AddFeature(h); p.Rebuild(null);
            Require(h.Error == null, h.Error ?? "");
            var a1 = WallAxisPoint(p, 30);
            // правка: брусок длиннее (кромка x = 100 → 120)
            var pts = s.Entities.OfType<SkLine>().SelectMany(l => new[] { l.A, l.B }).Distinct().Where(q => Math.Abs(q.X - 100) < 1e-9).ToList();
            foreach (var q in pts) q.X = 120;
            s.NotifyChanged(s); p.Rebuild(null);
            var a2 = WallAxisPoint(p, 30);
            Log(new { test = "KRN-HOLE-LINEAR-K153", before = a1?.ToString(), after = a2?.ToString(), error = h.Error, box = p.Bodies[0].Bounds.Max.ToString(), ref1 = h.LinRef1?.FaceA + " | " + h.LinRef1?.FaceB });
            Require(a1 is { } b1 && Math.Abs(b1.X - 80) < 1e-6 && Math.Abs(b1.Y - 45) < 1e-6, "hole not at 20/15 from the edges: " + a1);
            Require(a2 is { } b2 && Math.Abs(b2.X - 100) < 1e-6 && Math.Abs(b2.Y - 45) < 1e-6, "hole did not follow the edge: " + a2);
        });

        Check("KRN-HOLE-REFS-K157", () =>
        {
            // k157 (BACKLOG k153: привязка к рёбрам-дугам и опорным плоскостям): брусок 100×60×30, отверстие ⌀10 в (30, 30);
            // второе ⌀8 — в 25 от центра кромки первого (ребро-дуга) и в 15 от плоскости XZ (y = 0): центр (50, 15) — эталон
            // по построению (25² = 20² + 15²). Мутации P3D_HOLEARCREF=0 (дуга — прямая по концам) и P3D_HOLEPLANEREF=0 —
            // проверка падает.
            var p = Block(100, out _, out _); p.Rebuild(null);
            var h1 = new HoleFeature("H1") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(10), ThroughAll = true };
            h1.Points.Add(new Vector3d(30, 30, 30)); p.AddFeature(h1); p.Rebuild(null);
            Require(h1.Error == null, h1.Error ?? "");
            var arc = p.Bodies[0].Faces.SelectMany(f => f.Edges).Distinct()
                .First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 5) < 1e-9 && Math.Abs(a.Frame.Origin.Z - 30) < 1e-9);
            var h2 = new HoleFeature("H2") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(8), ThroughAll = true,
                LinRef1 = EdgeRef.Of(arc), LinD1 = new ValueRef(25), LinPlane2 = "XZ", LinD2 = new ValueRef(15) };
            h2.Points.Add(new Vector3d(55, 14, 30)); p.AddFeature(h2); p.Rebuild(null);
            var c2 = p.Bodies[0].Faces.Where(f => f.Tag.EndsWith(HoleFeature.RoleWall)).Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)))
                .OfType<Parametric3D.Kernel.Geometry.Ssi.Cylinder>().FirstOrDefault(c => Math.Abs(c.R - 4) < 1e-9);
            Log(new { test = "KRN-HOLE-REFS-K157", error = h2.Error, centre = c2?.P.ToString() });
            Require(h2.Error == null && c2 != null && Math.Abs(c2.P.X - 50) < 1e-6 && Math.Abs(c2.P.Y - 15) < 1e-6, "hole not at 25 from the arc centre and 15 from XZ: " + (h2.Error ?? c2?.P.ToString()));
            // запись и чтение: привязки сохраняются
            var text = DocumentFile.Save(p, null, withDisplay: false);
            var (q, _) = DocumentFile.Load(text);
            var h2b = q.Features.OfType<HoleFeature>().Last();
            Require(h2b.LinPlane2 == "XZ" && h2b.LinRef1 != null && h2b.LinPlane1 == null && h2b.LinRef2 == null && Math.Abs(h2b.LinD1.Value - 25) < 1e-12, "references after save and load");
        });

        Check("KRN-HOLE-RADIAL-K153", () =>
        {
            var p = new Part("shaft");
            var c = CircleSketch(p, 0, 0, 0, 20);
            var ex = new ExtrudeFeature { SketchId = c.Id }; ex.Side1.Distance = new ValueRef(100); p.AddFeature(ex);
            p.Rebuild(null);
            var side = p.Bodies[0].Faces.First(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Cylinder);
            var bottom = p.Bodies[0].Faces.First(f => SketchFaceRef.PlaneOf(f) is { } pl && Math.Abs(pl.P.Z) < 1e-6);
            var h = new HoleFeature("R") { Diameter = new ValueRef(6), ThroughAll = true, RadFace = side.Tag, RadRefFace = bottom.Tag,
                RadAxial = new ValueRef(30), RadAngle = new ValueRef(90), RadZeroDir = Vector3d.UnitX };
            h.Points.Add(new Vector3d(20, 0, 50)); p.AddFeature(h); p.Rebuild(null);
            Require(h.Error == null, h.Error ?? "");
            Vector3d? Axis() => p.Bodies[0].Faces.Where(f => f.Tag.EndsWith(HoleFeature.RoleWall)).Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))).OfType<Parametric3D.Kernel.Geometry.Ssi.Cylinder>()
                .Select(cy => (Vector3d?)(cy.P + cy.Axis.Normalized() * ((0 - cy.P.X) / (Math.Abs(cy.Axis.X) > 1e-9 ? cy.Axis.Normalized().X : 1)))).FirstOrDefault();
            var w = p.Bodies[0].Faces.Where(f => f.Tag.EndsWith(HoleFeature.RoleWall)).Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))).OfType<Parametric3D.Kernel.Geometry.Ssi.Cylinder>().FirstOrDefault();
            Require(w != null, "no hole wall");
            var ax = w!.Axis.Normalized();
            double offZ = w.P.Z + ax.Z * 0;   // ось вдоль Y: z постоянна
            Log(new { test = "KRN-HOLE-RADIAL-K153", axisP = w.P.ToString(), axis = ax.ToString(), dir = h.Direction.ToString() });
            Require(Math.Abs(Math.Abs(ax.Y) - 1) < 1e-9 && Math.Abs(w.P.Z - 30) < 1e-6 && Math.Abs(w.P.X) < 1e-6, $"hole axis {w.P} {ax}");
            Require(h.Direction.Y < -0.999, "drilled not toward the axis");
            // вал удлинён вниз (торец z = −10): отверстие — в 30 от нового торца
            ex.Side2.Distance = new ValueRef(10); ex.TwoSided = true; p.MarkDirtyFrom(ex); p.Rebuild(null);
            var w2 = p.Bodies[0].Faces.Where(f => f.Tag.EndsWith(HoleFeature.RoleWall)).Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))).OfType<Parametric3D.Kernel.Geometry.Ssi.Cylinder>().FirstOrDefault();
            Log(new { test = "KRN-HOLE-RADIAL-K153", afterP = w2?.P.ToString(), error = h.Error });
            Require(h.Error == null && w2 != null && Math.Abs(w2.P.Z - 20) < 1e-6, "radial hole did not follow the end face: " + (h.Error ?? w2?.P.ToString()));
        });
    }
}
