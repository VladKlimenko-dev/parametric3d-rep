using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

/// <summary>
/// k115 (замечание 12:39, п. 1): УКЛОН ЛЮБОЙ ПЛОСКОЙ ГРАНИ И КОНУСА. Тело вращения — усечённый конус; вырез сбоку даёт плоскую
/// грань-«линзу» (кромки — прямые стенок выреза и кривые конуса) — прежде «the drafted face must be planar or cylindrical».
/// Уклон её по прямой кромке; уклон конической боковой грани по нижней окружности — конус с новым верхним радиусом (объём
/// усечённого конуса). Мутации P3D_DRAFTANYFACE=0, P3D_DRAFTCONE=0.
/// </summary>
static partial class Program
{
    static Part Frustum(out Feature rev, double r0 = 30, double r1 = 20, double h = 80)
    {
        var p = new Part("frustum");
        var doc = new Document(); doc.Root.Add(p);
        var s = new Sketch("Rev", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ)); p.Add(s);
        var a = new SkPoint(0, 0); var b = new SkPoint(r0, 0); var c = new SkPoint(r1, h); var d = new SkPoint(0, h);
        s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkLine(b, c)); s.AddEntity(new SkLine(c, d)); s.AddEntity(new SkLine(d, a));
        var ax = new SkLine(new SkPoint(0, -10), new SkPoint(0, 100)) { Role = SkRole.Construction }; s.AddEntity(ax);
        var rf = new RevolveFeature("V") { SketchId = s.Id, AxisEntityId = ax.Id }; p.AddFeature(rf);
        rev = rf;
        p.Rebuild(null);
        return p;
    }

    static void Krn115Checks()
    {
        Check("KRN-EXTRUDE-END-K115", () =>
        {
            // k115 (замечание 12:39, п. 2): «глубина до» и вторая сторона выдавливания — в файле (прежде открывались «на расстояние»)
            var p = new Part("ex"); var doc = new Document(); doc.Root.Add(p);
            TestGeometry.AddCube(p, 100); p.Rebuild(null);
            var sk = RectSketch(p, 50, -10, -10, 20, 20);
            var e = new ExtrudeFeature("Thru") { SketchId = sk.Id, Cut = true, TwoSided = true };
            e.Side1.End = EndType.ThroughAll; e.Side1.Flip = true;
            e.Side2.End = EndType.ToNext; e.Side2.Distance = new ValueRef(7.5) { Formula = "7.5" }; e.Side2.Flip = true;
            p.AddFeature(e); p.Rebuild(null);
            var (q, _) = DocumentFile.Load(DocumentFile.Save(p));
            var g = q.Features.OfType<ExtrudeFeature>().Last();
            Console.WriteLine($"  loaded: side1 {g.Side1.End} flip {g.Side1.Flip}, side2 {g.Side2.End} {g.Side2.Distance.Value} flip {g.Side2.Flip}");
            Require(g.Side1.End == EndType.ThroughAll && g.Side1.Flip && g.TwoSided && g.Side2.End == EndType.ToNext && g.Side2.Distance.Value == 7.5 && g.Side2.Flip,
                "extrude ends were not restored");
            var d2 = new Document(); d2.Root.Add(q); q.Rebuild(null);
            Require(Math.Abs(q.Volume - p.Volume) < 1e-6 * p.Volume, $"volume after reload {q.Volume} vs {p.Volume}");
        });

        Check("KRN-DRAFT-ANYFACE-K115", () =>
        {
            // 1) плоская грань выреза на конусе
            var p = Frustum(out _);
            Require(p.Features.All(f => f.Error == null) && p.Bodies.Count == 1, "frustum: " + string.Join("; ", p.Features.Select(f => f.Error)));
            var sk = new Sketch("Cut", new Frame3(new Vector3d(15, 0, 0), Vector3d.UnitY, Vector3d.UnitZ)); p.Add(sk);
            var q = new[] { (-40.0, 30.0), (40.0, 30.0), (40.0, 60.0), (-40.0, 60.0) }.Select(t => new SkPoint(t.Item1, t.Item2)).ToList();
            for (int i = 0; i < 4; i++) sk.AddEntity(new SkLine(q[i], q[(i + 1) % 4]));
            var cut = new ExtrudeFeature("C") { SketchId = sk.Id, Cut = true }; cut.Side1.Distance = new ValueRef(50); p.AddFeature(cut);
            p.Rebuild(null);
            Require(cut.Error == null, "cut: " + cut.Error);
            double v0 = p.Volume;
            foreach (var f in p.Bodies[0].Faces) Console.WriteLine($"    face {f.Surface.GetType().Name} {(f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fz ? fz.Source?.GetType().Name : "")} edges {f.Edges.Count()} x {string.Join(",", f.Edges.Select(e => $"{e.V0.P.X:0.#}/{e.V0.P.Z:0.#}"))}");
            var flat = p.Bodies[0].Faces.First(f => f.Edges.Count() == 4 && f.Edges.All(e => Math.Abs(e.V0.P.X - 15) < 1e-4 && Math.Abs(e.V1.P.X - 15) < 1e-4));
            var hinge = flat.Edges.First(e => Math.Abs(e.V0.P.Z - 30) < 1e-4 && Math.Abs(e.V1.P.Z - 30) < 1e-4);
            var top = flat.Edges.First(e => Math.Abs(e.V0.P.Z - 60) < 1e-4 && Math.Abs(e.V1.P.Z - 60) < 1e-4);
            var mid = flat.Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).Aggregate(Vector3d.Zero, (s0, x) => s0 + x) / (2 * flat.Edges.Count());
            Console.WriteLine($"  flat face: {flat.Surface.GetType().Name} ({(flat.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? fs.Source?.GetType().Name : "")}), edges {flat.Edges.Count()}");
            var df = new DraftFeature("D") { Hinge = EdgeRef.Of(hinge), Moving = EdgeRef.Of(top), Angle = new ValueRef(5) };
            df.OnFaces.Add(new Vector3d(15 + 3e-5, 0, 45));      // точка выбора — с сетки показа (float): прежде грань не находилась
            p.AddFeature(df); p.Rebuild(null);
            Console.WriteLine($"  flat draft: error {df.Error}, volume {v0:0.#} -> {p.Volume:0.#}, angle {df.LastBefore:0.##} -> {df.LastAfter:0.##}");
            Require(df.Error == null && p.Volume < v0 - 1 && p.Volume > v0 - 0.5 * 30 * 30 * Math.Tan(5 * Math.PI / 180) * 60, $"flat draft: {df.Error}, volume {p.Volume} vs {v0}");
            // 2) коническая боковая грань: шарнир — нижняя окружность
            var c = Frustum(out _);
            var cone = c.Bodies[0].Faces.First(f => f.Edges.Count(e => e.Curve is ArcCurve3) == 2);
            var circ = cone.Edges.Where(e => e.Curve is ArcCurve3).OrderBy(e => e.V0.P.Z).ToList();
            var dc = new DraftFeature("DC") { Hinge = EdgeRef.Of(circ[0]), Moving = EdgeRef.Of(circ[1]), Angle = new ValueRef(4) };
            var onCone = new Vector3d(25, 0, 40);
            dc.OnFaces.Add(onCone);
            c.AddFeature(dc); c.Rebuild(null);
            // образующая повёрнута внутрь на 4°: верхний радиус по геометрии
            double r1n = 30 + 80 / Math.Tan(Math.Atan2(80, -10) + 4 * Math.PI / 180);   // образующая (−10, 80) повёрнута к оси на 4°
            double vWant = Math.PI * 80 / 3 * (900 + 30 * r1n + r1n * r1n);
            Console.WriteLine($"  cone draft: error {dc.Error}, volume {c.Volume:0.#} (want {vWant:0.#}, top radius {r1n:0.###}), angle {dc.LastBefore:0.##} -> {dc.LastAfter:0.##}");
            Require(dc.Error == null && Math.Abs(c.Volume - vWant) < 1e-4 * vWant, $"cone draft: {dc.Error}, volume {c.Volume} want {vWant}");
            Require(Math.Abs(dc.LastAfter!.Value - dc.LastBefore!.Value + 4) < 1e-6, "cone angle to the base");
        });
    }
}
