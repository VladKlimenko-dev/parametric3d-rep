using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Diagnostics;

/// <summary>
/// ДЕТАЛИ ДЛЯ КНОПКИ «ТЕСТ»: «реалистичная» деталь (та же, что в замерах ворот g.13: плита, отверстия простые, сквозные и с
/// цекованием, карманы, бобышки, скругления и фаски углов; по зерну) и по одной детали на каждую операцию дерева.
/// </summary>
public static class TestParts
{
    public static Sketch RectSketch(Part p, double z, double x0, double y0, double w, double d)
    {
        var f = new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s);
        var a = new SkPoint(x0, y0); var b = new SkPoint(x0 + w, y0); var c = new SkPoint(x0 + w, y0 + d); var e = new SkPoint(x0, y0 + d);
        s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkLine(b, c)); s.AddEntity(new SkLine(c, e)); s.AddEntity(new SkLine(e, a));
        return s;
    }

    public static Sketch CircleSketch(Part p, double z, double x, double y, double r)
    {
        var f = new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s);
        s.AddEntity(new SkCircle(new SkPoint(x, y), r));
        return s;
    }

    /// <summary>Реалистичная деталь (как RealisticPart проверок ядра): features операций, зерно — состав и размеры.</summary>
    public static Part Realistic(int seed, int features)
    {
        var rnd = new Random(seed);
        var p = new Part($"real{seed}");
        double W = 160, D = 100, H = 20;
        var plate = RectSketch(p, -H / 2, -W / 2, -D / 2, W, D);
        var ex = new ExtrudeFeature("Plate") { SketchId = plate.Id };
        ex.Side1.Distance = new ValueRef(H);
        p.AddFeature(ex);
        double top = H / 2;
        var used = new List<(double X, double Y, double R)>();
        bool Free(double x, double y, double r) =>
            Math.Abs(x) + r < W / 2 - 4 && Math.Abs(y) + r < D / 2 - 4 && used.All(u => (new Vector2d(x - u.X, y - u.Y)).Length > r + u.R + 3);

        for (int i = 1; i < features; i++)
        {
            int kind = rnd.Next(10);
            double x = (rnd.NextDouble() - 0.5) * (W - 30), y = (rnd.NextDouble() - 0.5) * (D - 30);
            switch (kind)
            {
                case 0: case 1: case 2:
                {
                    double r = 1.5 + rnd.NextDouble() * 3;
                    if (!Free(x, y, r + 3)) { i--; continue; }
                    used.Add((x, y, r + 3));
                    var h = new HoleFeature($"Hole{i}") { Direction = -Vector3d.UnitZ };
                    h.Points.Add(new Vector3d(x, y, top));
                    h.Diameter = new ValueRef(2 * r);
                    if (rnd.Next(3) == 0) { h.Kind = HoleKind.Counterbore; h.ThroughAll = true; h.HeadDiameter = new ValueRef(2 * r + 4); h.HeadDepth = new ValueRef(3); }
                    else if (rnd.Next(2) == 0) h.ThroughAll = true; else h.Depth = new ValueRef(6 + rnd.NextDouble() * 8);
                    p.AddFeature(h);
                    break;
                }
                case 3: case 4:
                {
                    double w = 8 + rnd.NextDouble() * 20, d = 6 + rnd.NextDouble() * 14;
                    if (!Free(x, y, Math.Max(w, d) / 2 + 2)) { i--; continue; }
                    used.Add((x, y, Math.Max(w, d) / 2 + 2));
                    var s = RectSketch(p, top, x - w / 2, y - d / 2, w, d);
                    var cut = new ExtrudeFeature($"Pocket{i}") { SketchId = s.Id, Cut = true };
                    cut.Side1.Distance = new ValueRef(3 + rnd.NextDouble() * 6); cut.Side1.Flip = true;
                    p.AddFeature(cut);
                    break;
                }
                case 5: case 6:
                {
                    double r = 3 + rnd.NextDouble() * 5;
                    if (!Free(x, y, r + 2)) { i--; continue; }
                    used.Add((x, y, r + 2));
                    var s = CircleSketch(p, top, x, y, r);
                    var boss = new ExtrudeFeature($"Boss{i}") { SketchId = s.Id };
                    boss.Side1.Distance = new ValueRef(4 + rnd.NextDouble() * 8);
                    p.AddFeature(boss);
                    break;
                }
                default:
                {
                    p.Rebuild(null);
                    var corners = new[] { (80.0, 50.0), (-80.0, 50.0), (-80.0, -50.0), (80.0, -50.0) };
                    TEdge? e = null;
                    foreach (var (cx, cy) in corners.OrderBy(_ => rnd.Next()))
                    {
                        e = p.Bodies.SelectMany(bb => bb.Edges).FirstOrDefault(q => q.Curve is LineCurve3 &&
                            Math.Abs(q.V0.P.X - cx) < 1e-6 && Math.Abs(q.V0.P.Y - cy) < 1e-6 && Math.Abs(q.V1.P.X - cx) < 1e-6 && Math.Abs(q.V1.P.Y - cy) < 1e-6);
                        if (e != null) break;
                    }
                    if (e == null) continue;
                    Feature f;
                    if (rnd.Next(2) == 0) { var rf = new RoundFeature($"Round{i}") { Radius = new ValueRef(2 + rnd.NextDouble() * 4) }; rf.Edges.Add(EdgeRef.Of(e)); f = rf; }
                    else { var cf = new ChamferFeature($"Chamfer{i}") { D1 = new ValueRef(1 + rnd.NextDouble() * 2), D2 = new ValueRef(1 + rnd.NextDouble() * 2) }; cf.Edges.Add(EdgeRef.Of(e)); f = cf; }
                    p.AddFeature(f);
                    break;
                }
            }
        }
        return p;
    }

    /// <summary>Плита 100 × 60 × 20 (z от 0 до 20).</summary>
    public static Part Block(string name, double w = 100, double d = 60, double h = 20)
    {
        var p = new Part(name);
        var s = RectSketch(p, 0, -w / 2, -d / 2, w, d);
        var ex = new ExtrudeFeature("Block") { SketchId = s.Id };
        ex.Side1.Distance = new ValueRef(h);
        p.AddFeature(ex);
        return p;
    }

    private static TEdge? VerticalEdge(Part p, double x, double y) =>
        p.Bodies.SelectMany(b => b.Edges).FirstOrDefault(e => e.Curve is LineCurve3 &&
            Math.Abs(e.V0.P.X - x) < 1e-6 && Math.Abs(e.V0.P.Y - y) < 1e-6 && Math.Abs(e.V1.P.X - x) < 1e-6 && Math.Abs(e.V1.P.Y - y) < 1e-6);

    /// <summary>Случай проверки операции: имя, деталь (последняя операция — проверяемая), ожидаемый объём (если известен).</summary>
    public sealed record OpCase(string Name, Func<Part> Make, double? Volume);

    public static List<OpCase> OperationCases() => new()
    {
        new("Выдавливание (плита)", () => Block("extrude"), 100 * 60 * 20),
        new("Вырез выдавливанием (карман 30×20×8)", () =>
        {
            var p = Block("cut");
            var s = RectSketch(p, 20, -15, -10, 30, 20);
            var c = new ExtrudeFeature("Pocket") { SketchId = s.Id, Cut = true };
            c.Side1.Distance = new ValueRef(8); c.Side1.Flip = true;
            p.AddFeature(c);
            return p;
        }, 100 * 60 * 20 - 30 * 20 * 8),
        new("Выдавливание бобышки (Ø20 × 15)", () =>
        {
            var p = Block("boss");
            var s = CircleSketch(p, 20, 0, 0, 10);
            var b = new ExtrudeFeature("Boss") { SketchId = s.Id };
            b.Side1.Distance = new ValueRef(15);
            p.AddFeature(b);
            return p;
        }, 100 * 60 * 20 + Math.PI * 100 * 15),
        new("Вращение (кольцо 10…30 × 40, 360°)", () =>
        {
            var p = new Part("revolve");
            var s = new Sketch("S1", Frame3.XZ); p.Add(s);
            var a = new SkPoint(10, 0); var b = new SkPoint(30, 0); var c = new SkPoint(30, 40); var d = new SkPoint(10, 40);
            s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkLine(b, c)); s.AddEntity(new SkLine(c, d)); s.AddEntity(new SkLine(d, a));
            s.AddEntity(new SkLine(new SkPoint(0, -10), new SkPoint(0, 50)) { Role = SkRole.Centerline });
            p.AddFeature(new RevolveFeature("Revolve") { SketchId = s.Id });
            return p;
        }, Math.PI * (30 * 30 - 10 * 10) * 40),
        new("Отверстие простое глухое Ø8 × 12", () => Hole(HoleKind.Simple, false), null),
        new("Отверстие сквозное с цекованием", () => Hole(HoleKind.Counterbore, true), null),
        new("Отверстие сквозное с зенковкой", () => Hole(HoleKind.Countersink, true), null),
        new("Скругление ребра R5", () =>
        {
            var p = Block("round"); p.Rebuild(null);
            var r = new RoundFeature("Round") { Radius = new ValueRef(5) };
            if (VerticalEdge(p, 50, 30) is { } e) r.Edges.Add(EdgeRef.Of(e));
            p.AddFeature(r);
            return p;
        }, 100 * 60 * 20 - (25 - Math.PI * 25 / 4) * 20),
        new("Скругление 4 рёбер R5", () =>
        {
            var p = Block("round4"); p.Rebuild(null);
            var r = new RoundFeature("Round4") { Radius = new ValueRef(5) };
            foreach (var (x, y) in new[] { (50.0, 30.0), (-50.0, 30.0), (-50.0, -30.0), (50.0, -30.0) })
                if (VerticalEdge(p, x, y) is { } e) r.Edges.Add(EdgeRef.Of(e));
            p.AddFeature(r);
            return p;
        }, 100 * 60 * 20 - 4 * (25 - Math.PI * 25 / 4) * 20),
        new("Фаска ребра 4×4", () =>
        {
            var p = Block("chamfer"); p.Rebuild(null);
            var c = new ChamferFeature("Chamfer") { D1 = new ValueRef(4), D2 = new ValueRef(4) };
            if (VerticalEdge(p, 50, 30) is { } e) c.Edges.Add(EdgeRef.Of(e));
            p.AddFeature(c);
            return p;
        }, 100 * 60 * 20 - 8 * 20),
        new("Скругление вершины R4", () =>
        {
            var p = Block("vround");
            p.AddFeature(new VertexRoundFeature("VRound") { Vertex = new Vector3d(50, 30, 20), Radius = new ValueRef(4) });
            return p;
        }, null),
        new("Фаска угла 4", () =>
        {
            var p = Block("vchamfer");
            p.AddFeature(new VertexChamferFeature("VChamfer") { Vertex = new Vector3d(50, 30, 20), Distance = new ValueRef(4) });
            return p;
        }, 100 * 60 * 20 - 4 * 4 * 4 / 6.0),
        new("Оболочка 2 мм, открыт верх", () =>
        {
            var p = Block("shell");
            var sh = new ShellFeature("Shell") { Thickness = new ValueRef(2) };
            sh.OpenFaces.Add(new Vector3d(0, 0, 20));
            p.AddFeature(sh);
            return p;
        }, 100 * 60 * 20 - 96 * 56 * 18),
        new("Зеркало относительно YZ", () =>
        {
            var p = new Part("mirror");
            var s = RectSketch(p, 0, 10, -10, 30, 20);
            var ex = new ExtrudeFeature("Block") { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(10);
            p.AddFeature(ex);
            p.AddFeature(new MirrorFeature("Mirror") { At = Vector3d.Zero, Normal = Vector3d.UnitX });
            return p;
        }, null),
        new("Линейный массив ×4 (шаг 25)", () =>
        {
            var p = new Part("lpattern");
            var s = RectSketch(p, 0, 0, 0, 10, 10);
            var ex = new ExtrudeFeature("Block") { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(10);
            p.AddFeature(ex);
            p.AddFeature(new LinearPatternFeature("Pattern") { Step = new Vector3d(25, 0, 0), Count = new ValueRef(4) });
            return p;
        }, 4 * 1000),
        new("Круговой массив ×6", () =>
        {
            var p = new Part("cpattern");
            var s = RectSketch(p, 0, 30, -5, 10, 10);
            var ex = new ExtrudeFeature("Block") { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(10);
            p.AddFeature(ex);
            p.AddFeature(new CircularPatternFeature("CPattern") { At = Vector3d.Zero, Axis = Vector3d.UnitZ, Count = new ValueRef(6) });
            return p;
        }, 6 * 1000),
    };

    private static Part Hole(HoleKind kind, bool through)
    {
        var p = Block("hole");
        var h = new HoleFeature("Hole") { Direction = -Vector3d.UnitZ, Kind = kind, Diameter = new ValueRef(8), ThroughAll = through, Depth = new ValueRef(12) };
        h.HeadDiameter = new ValueRef(14); h.HeadDepth = new ValueRef(4);
        h.Points.Add(new Vector3d(0, 0, 20));
        p.AddFeature(h);
        return p;
    }

    public static Part Holes100()
    {
        var p = new Part("holes100");
        TestGeometry.AddCube(p, 200);
        int side = 10; double step = 200.0 / (side + 1);
        for (int i = 0; i < 100; i++)
        {
            double x = -100 + step * (1 + i % side), y = -100 + step * (1 + i / side);
            var h = new HoleFeature($"H{i}") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(4), ThroughAll = true };
            h.Points.Add(new Vector3d(x, y, 100));   // как в замерах g.13 (KRN-10)
            p.AddFeature(h);
        }
        return p;
    }
}
