using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

/// <summary>
/// Ворота g.14. НЕЗАВИСИМАЯ ПРОВЕРКА: каждая деталь каталога пишется точным STEP, а файл читает другое ядро
/// (OpenCascade, tools/occt_check.py) — годность B-rep, число тел и граней, объём и площадь своим интегрированием.
/// Здесь — только выгрузка: STEP + наш ответ (формула, если известна; объём и площадь детали; число граней).
/// </summary>
static partial class Program
{
    internal static List<(string Name, Func<Part> Build, double Expected)> G14Catalogue()
    {
        var res = new List<(string, Func<Part>, double)>();
        var known = K09Cases().ToDictionary(c => c.Name, c => c.Expected);
        foreach (var (name, build) in GateCatalogue()) res.Add((name, build, known.GetValueOrDefault(name, double.NaN)));
        foreach (var c in G14RealParts()) res.Add(c);
        // KRN-06 (06.14): скругление с переменным радиусом — лента-конус, торцы — дуги эллипса
        res.Add(("variable round R1.5→5 on a cube edge", () =>
        {
            var p = new Part("vr"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
            var e = p.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.X - 30) < 1e-9 && Math.Abs(x.V1.P.X - 30) < 1e-9 && Math.Abs(x.V0.P.Z - 30) < 1e-9 && Math.Abs(x.V1.P.Z - 30) < 1e-9);
            var rf = new RoundFeature("R") { Radius = new ValueRef(1.5), RadiusEnd = new ValueRef(5) }; rf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(rf); return p;
        }, 216000 - RollingBallVariableCorner(60, 1.5, 5)));
        // Полости (матрица «Обмен: ориентация полостей»): тело с внутренними оболочками пишется BREP_WITH_VOIDS.
        res.Add(("cavity: sphere R10 inside cube 60", () => { var p = new Part("cvs"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutSphere(0, 0, 0, 10)); return p; },
            216000 - 4.0 / 3 * Math.PI * 1000));
        res.Add(("cavity: closed cylinder r8 h20 inside cube 60", () => { var p = new Part("cvc"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutSolid(() => PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 8, 20))); return p; },
            216000 - Math.PI * 64 * 20));
        res.Add(("two cavities: sphere R6 and cylinder r5 h10", () =>
        {
            var p = new Part("cv2"); TestGeometry.AddCube(p, 60);
            p.AddFeature(new CutSphere(-15, 0, 0, 6)); p.AddFeature(new CutSolid(() => PrimitiveOp.Cylinder(new Vector3d(15, 0, -5), 5, 10))); return p;
        }, 216000 - 4.0 / 3 * Math.PI * 216 - Math.PI * 25 * 10));
        res.Add(("sphere cavity R6, then a through hole r5 elsewhere", () =>
        {
            var p = new Part("cvh"); TestGeometry.AddCube(p, 60);
            p.AddFeature(new CutSphere(-15, 0, 0, 6)); p.AddFeature(new CutCyl(15, 0, 5)); return p;
        }, 216000 - 4.0 / 3 * Math.PI * 216 - Math.PI * 25 * 60));
        return res;
    }

    /// <summary>g.11: сборки для точного STEP интерфейса — деталь и её размещения (матрица OpenTK, строка-вектор).</summary>
    internal static List<(string Name, Func<List<(Part, Matrix4d?)>> Build)> G11Assemblies()
    {
        Part Flange() { var c = G14RealParts().First(x => x.Name.StartsWith("flange")); var p = c.Build(); p.Apply(p.Compute(null)); return p; }
        Part Shaft() { var c = G14RealParts().First(x => x.Name.StartsWith("shaft")); var p = c.Build(); p.Apply(p.Compute(null)); return p; }
        Part Pattern() { var c = K09Cases().First(x => x.Name.StartsWith("linear pattern")); var p = c.Build(); p.Apply(p.Compute(null)); return p; }
        Matrix4d Rot(Vector3d axis, double deg, Vector3d move) => Matrix4d.CreateFromAxisAngle(axis.Normalized(), deg * Math.PI / 180) * Matrix4d.CreateTranslation(move);
        Matrix4d Mirror(Vector3d n, double d) { n = n.Normalized(); var m = Matrix4d.Identity;
            m.M11 = 1 - 2 * n.X * n.X; m.M12 = -2 * n.X * n.Y; m.M13 = -2 * n.X * n.Z; m.M21 = -2 * n.Y * n.X; m.M22 = 1 - 2 * n.Y * n.Y; m.M23 = -2 * n.Y * n.Z;
            m.M31 = -2 * n.Z * n.X; m.M32 = -2 * n.Z * n.Y; m.M33 = 1 - 2 * n.Z * n.Z; m.M41 = 2 * d * n.X; m.M42 = 2 * d * n.Y; m.M43 = 2 * d * n.Z; return m; }
        return new()
        {
            ("assembly: flange ×4 (as is, moved, rotated 37° about (1,1,1), mirrored about x = −80)", () => { var f = Flange(); return new() {
                (f, null), (f, Matrix4d.CreateTranslation(150, 0, 0)), (f, Rot(new Vector3d(1, 1, 1), 37, new Vector3d(0, 160, 20))), (f, Mirror(Vector3d.UnitX, -80)) }; }),
            ("assembly: shaft rotated 90° about X + 3-body pattern rotated 120° about Z, moved far (+5000 mm)", () => new() {
                (Shaft(), Rot(Vector3d.UnitX, 90, new Vector3d(5000, -3000, 1200))), (Pattern(), Rot(Vector3d.UnitZ, 120, new Vector3d(5100, -3000, 1200))) }),
        };
    }

    internal sealed class CutSolid(Func<TSolid> tool) : Feature
    {
        public override void Execute(BuildContext c) => BooleanOp.Subtract(c.Bodies, tool(), c.Warnings);
    }

    static void G14Explain(string which)
    {
        foreach (var (name, build, expected) in G14Catalogue().Where(c => c.Name.StartsWith(which)))
        {
            BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("  route: " + m);
            var p = build(); var r = p.Compute(null); p.Apply(r);
            BooleanOp.AnalyticRouteDebug = null;
            Console.WriteLine($"{name}: volume {p.Volume:R} expected {expected:R} faces {p.Bodies.Sum(b => b.Faces.Count())}");
            foreach (var f in p.Features) Console.WriteLine($"  {f.Name} [{f.GetType().Name}] error={f.Error} outcome={r.Outcomes.GetValueOrDefault(f)}");
            foreach (var w in r.Warnings) Console.WriteLine("  warn: " + w);
            foreach (var b in p.Bodies) foreach (var fc in b.Faces)
                Console.WriteLine($"  face {fc.Id} {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} area {AnalyticVolume.FaceArea(fc):R} int {AnalyticVolume.FaceIntegral(fc):R} edges {string.Join(",", fc.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge.Curve.GetType().Name))}");
            Console.WriteLine($"  sphere closed used {AnalyticVolume.SphereClosedUsed} rejected {AnalyticVolume.SphereClosedRejected}");
        }
    }

    /// <summary>Разбор: сценарий → STEP-файл, обратное чтение нашим импортом (объёмы сеток).</summary>
    static void G14Scenario(string id, string file)
    {
        var w = new List<string>(); var b = Scenario(id, w)[0]; var sw = new StringWriter();
        bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, id, w);
        File.WriteAllText(file, sw.ToString());
        var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), w);
        double a0 = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
        Console.WriteLine($"{id}: ok {ok} guarantee {b.Guarantee} shells {b.Shells.Count} faces {b.Faces.Count()} mesh volume {a0:R} exact {AnalyticVolume.Of(b)}");
        foreach (var x in back) Console.WriteLine($"  back: shells {x.Shells.Count} faces {x.Faces.Count()} mesh volume {Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(x)):R}");
    }

    static void G14Dump(string dir)
    {
        Directory.CreateDirectory(dir);
        var man = new List<string> { "id\tname\tstatus\tbodies\tfaces\tvolume\tarea\texpected\tguarantee\tmassFromMesh\tvolumeError" };
        int i = 0;
        foreach (var (name, build, expected) in G14Catalogue())
        {
            string id = $"c{i++:D2}";
            try
            {
                var p = build(); var r = p.Compute(null); p.Apply(r);
                var err = string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}"));
                if (err.Length > 0) { man.Add($"{id}\t{name}\terror: {err.Replace('\t', ' ')}\t\t\t\t\t\t\t"); continue; }
                var w = new List<string>(); var sw = new StringWriter();
                bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, p.Bodies, id, w);
                if (!ok) { man.Add($"{id}\t{name}\tno exact step: {string.Join("; ", w).Replace('\t', ' ')}\t\t\t\t\t\t\t"); continue; }
                File.WriteAllText(Path.Combine(dir, id + ".step"), sw.ToString());
                string g = string.Join(",", p.Bodies.Select(b => b.Guarantee.ToString()));
                man.Add($"{id}\t{name}\tok\t{p.Bodies.Count}\t{p.Bodies.Sum(b => b.Faces.Count())}\t{p.Volume:R}\t{p.Area:R}\t{expected:R}\t{g}\t{r.MassFromMesh}\t{p.VolumeError:R}");
            }
            catch (Exception ex) { man.Add($"{id}\t{name}\texception: {ex.Message.Replace('\t', ' ')}\t\t\t\t\t\t\t"); }
        }
        // g.11: СБОРКИ — точный STEP интерфейса (Exchange.WriteStep): экземпляры со сдвигом, поворотом, зеркалом; все тела.
        foreach (var (name, build) in G11Assemblies())
        {
            string id = $"c{i++:D2}";
            try
            {
                var list = build(); double vol = 0, area = 0; int nb = 0, nf = 0;
                foreach (var (p, _) in list) { if (p.Bodies.Count == 0) p.Apply(p.Compute(null)); vol += p.Volume; area += p.Area; nb += p.Bodies.Count; nf += p.Bodies.Sum(b => b.Faces.Count()); }
                var sw = new StringWriter();
                var rep = Parametric3D.Model.Exchange.WriteStep(sw, list, id);
                if (!rep.Exact) { man.Add($"{id}\t{name}\tno exact step: {rep.Summary}\t\t\t\t\t\t\t"); continue; }
                File.WriteAllText(Path.Combine(dir, id + ".step"), sw.ToString());
                man.Add($"{id}\t{name}\tok\t{nb}\t{nf}\t{vol:R}\t{area:R}\tNaN\tExactAnalytic\tFalse");
            }
            catch (Exception ex) { man.Add($"{id}\t{name}\texception: {ex.Message.Replace('\t', ' ')}\t\t\t\t\t\t\t"); }
        }
        // KRN-07 (07.8): ТЕЛА С ГРАНЬЮ NURBS после точной булевой (рациональный сплайн с весами, кривые пересечения NURBS ×
        // плоскость/квадрика) — OpenCascade читает точный STEP и сверяет объём и площадь
        foreach (var (name, tool, _) in NurbsBooleanCases())
        {
            string id = $"c{i++:D2}";
            try
            {
                var bl = new List<TSolid> { TopologyClone.Deep(NurbsQuarterCylinder()) }; var w = new List<string>();
                BooleanOp.Subtract(bl, tool(), w);
                var b = bl[0];
                if (b.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic) { man.Add($"{id}\tnurbs: {name}\tnot exact: {b.Guarantee}\t\t\t\t\t\t\t"); continue; }
                var sw = new StringWriter(); var ws = new List<string>();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, bl, id, ws)) { man.Add($"{id}\tnurbs: {name}\tno exact step: {string.Join("; ", ws).Replace('\t', ' ')}\t\t\t\t\t\t\t"); continue; }
                File.WriteAllText(Path.Combine(dir, id + ".step"), sw.ToString());
                man.Add($"{id}\tnurbs: {name}\tok\t1\t{b.Faces.Count()}\t{AnalyticVolume.Of(b):R}\t{AnalyticVolume.AreaOf(b):R}\tNaN\tExactAnalytic\tFalse\t0");
            }
            catch (Exception ex) { man.Add($"{id}\tnurbs: {name}\texception: {ex.Message.Replace('\t', ' ')}\t\t\t\t\t\t\t"); }
        }
        // KRN-04 (04.9): ДВА ТЕЛА С ГРАНЯМИ NURBS (кривые пересечения двух сплайнов) — тоже OpenCascade
        foreach (var (name, xf) in NurbsNNCases())
        {
            string id = $"c{i++:D2}";
            try
            {
                var bl = new List<TSolid> { TopologyClone.Deep(NurbsQuarterCylinder()) };
                BooleanOp.Subtract(bl, TopologyTransform.Apply(TopologyClone.Deep(NurbsQuarterCylinder()), xf)!, new List<string>());
                if (bl.Any(b => b.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic)) { man.Add($"{id}\tnurbs×nurbs: {name}\tnot exact\t\t\t\t\t\t\t"); continue; }
                var sw = new StringWriter(); var ws = new List<string>();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, bl, id, ws)) { man.Add($"{id}\tnurbs×nurbs: {name}\tno exact step: {string.Join("; ", ws).Replace('\t', ' ')}\t\t\t\t\t\t\t"); continue; }
                File.WriteAllText(Path.Combine(dir, id + ".step"), sw.ToString());
                if (Environment.GetEnvironmentVariable("NURBSDBG") == "1") foreach (var b in bl) foreach (var f in b.Faces) Console.WriteLine($"FACEAREA {id} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} {AnalyticVolume.FaceArea(f):R} {string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve.GetType().Name))}");
                man.Add($"{id}\tnurbs×nurbs: {name}\tok\t{bl.Count}\t{bl.Sum(b => b.Faces.Count())}\t{bl.Sum(b => AnalyticVolume.Of(b) ?? double.NaN):R}\t{bl.Sum(b => AnalyticVolume.AreaOf(b) ?? double.NaN):R}\tNaN\tExactAnalytic\tFalse\t0");
            }
            catch (Exception ex) { man.Add($"{id}\tnurbs×nurbs: {name}\texception: {ex.Message.Replace('\t', ' ')}\t\t\t\t\t\t\t"); }
        }
        // KRN-06 (06.12, 06.13): УГОЛ С ОТСТУПОМ И N-СТОРОННИМ ЛОСКУТОМ (вершина пирамиды с разными уклонами; карман) — лоскут
        // в STEP кубическим B-сплайном; НЕЗАМКНУТЫЕ ЛЕНТЫ катящегося шара (закон радиуса, кривой торец) — рациональным
        // B-сплайном на охвате грани; ТОЧНАЯ ПРОТЯЖКА по кривой Безье и с закруткой; OpenCascade читает и сверяет объём и площадь
        foreach (var (name, make) in new (string, Func<TSolid>)[]
        {
            ("setback corner: irregular pyramid apex r2", () => { var bl = new List<TSolid> { PyramidBody(4, 0.7) }; FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 2, new List<string>()); return bl[0]; }),
            ("open rolling ball: hole d8 edge on a half shaft, radius law 2-0.8-1.5-1", () =>
            {
                var bl = new List<TSolid> { TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)) };
                BooleanOp.Subtract(bl, Box(-60, -60, -60, 120, 60, 120), new List<string>());
                BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>());
                var es = bl[0].Edges.Where(e => e.Curve is not Parametric3D.Kernel.Curves.LineCurve3 && e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) is var m && m.Z > 5 && Math.Abs(Math.Sqrt(m.X * m.X + m.Y * m.Y) - 4) < 1e-6).ToList();
                FilletOp.RoundEdgesLaw(bl, es.Select(EdgeRef.Of).ToList(), new List<(double, double)> { (0, 2), (0.3, 0.8), (0.7, 1.5), (1, 1) }, new List<string>());
                return bl[0];
            }),
            ("open rolling ball: plate edge ending on a concave cylinder r2", () =>
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 10) };
                BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(65, 5, -5), 15, 20), new List<string>());
                var es = bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Y) < 1e-9 && Math.Abs(e.V1.P.Y) < 1e-9 && Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9).ToList();
                FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), 2, new List<string>());
                return bl[0];
            }),
            ("exact sweep: ring 1.5/0.5 along a Bezier S", () => SweepOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(Frame3.XY, new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(Frame3.XY, 1.5, 0, 2 * Math.PI), new Parametric3D.Kernel.Curves.ArcCurve3(Frame3.XY, 0.5, 0, 2 * Math.PI) }), new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 20), new Vector3d(20, 0, 20), new Vector3d(20, 0, 40) }))),
            ("exact sweep: square 2 along a line 40, twist 90°", () => SweepOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(Frame3.XY, new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.LineCurve3(new(-1, -1, 0), new(1, -1, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(1, -1, 0), new(1, 1, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(1, 1, 0), new(-1, 1, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(-1, 1, 0), new(-1, -1, 0)) }), new Parametric3D.Kernel.Curves.LineCurve3(Vector3d.Zero, new Vector3d(0, 0, 40)), 1, 90)),
            ("exact sweep: circle r1 along a closed ellipse 20×12", () => SweepOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitY), 1, 0, 2 * Math.PI) }), new Parametric3D.Kernel.Curves.EllipseCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), 20, 12, 0, 2 * Math.PI))),
            ("exact sweep: square 2 along a Bezier S, kept orientation", () => SweepOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(Frame3.XY, new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.LineCurve3(new(-1, -1, 0), new(1, -1, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(1, -1, 0), new(1, 1, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(1, 1, 0), new(-1, 1, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(-1, 1, 0), new(-1, -1, 0)) }), new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 20), new Vector3d(20, 0, 20), new Vector3d(20, 0, 40) }), 1, 0, true)),
            ("exact sweep: Bezier lens along a 3D Bezier (m.s1)", () => SweepOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(Frame3.XY, new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.BezierCurve3(new Vector3d[] { new(2, 0, 0), new(1, 1.5, 0), new(-1, 1.5, 0), new(-2, 0, 0) }), new Parametric3D.Kernel.Curves.BezierCurve3(new Vector3d[] { new(-2, 0, 0), new(-1, -1.5, 0), new(1, -1.5, 0), new(2, 0, 0) }) }), new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 15), new Vector3d(10, 12, 25), new Vector3d(25, 5, 30), new Vector3d(30, 20, 45) }), 1, 0)),
            ("exact sweep: Bezier D along a Bezier S, kept orientation (m.s1)", () => SweepOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(Frame3.XY, new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.LineCurve3(new(-2, -1, 0), new(2, -1, 0)), new Parametric3D.Kernel.Curves.BezierCurve3(new Vector3d[] { new(2, -1, 0), new(2, 3, 0), new(-2, 3, 0), new(-2, -1, 0) }) }), new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 20), new Vector3d(20, 0, 20), new Vector3d(20, 0, 40) }), 1, 0, true)),
            ("point tangency: box minus a sphere touching the top from inside (m.b3)", () => { var bl = new List<TSolid> { Box(-10, -10, -10, 20, 20, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Sphere(new Vector3d(1, -2, 5), 5), new List<string>()); return bl[0]; }),
            ("point tangency: sphere minus a sphere touching from inside (m.b3)", () => { var bl = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 6) }; BooleanOp.Subtract(bl, PrimitiveOp.Sphere(new Vector3d(1.8, 2.4, 0), 3), new List<string>()); return bl[0]; }),
            ("circle tangency: box minus a torus touching the bottom from inside (m.b3)", () => { var bl = new List<TSolid> { Box(-20, -20, -10, 40, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Torus(new Vector3d(1, 2, -7), 8, 3), new List<string>()); return bl[0]; }),
            ("circle tangency: cylinder minus a sphere of the same radius (m.b3)", () => { var bl = new List<TSolid> { PrimitiveOp.Cylinder(new Vector3d(0, 0, -10), 5, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Sphere(new Vector3d(0, 0, 1), 5), new List<string>()); return bl[0]; }),
            ("spindle torus with poles: lemon (m.t3)", () => ExactCopy(Lemon(-2))),
            ("spindle torus with poles: apple (m.t3)", () => ExactCopy(Lemon(1))),
            ("spindle torus with poles: lemon, rotated (m.t3)", () => ExactCopy(TransformOp.Copy(Lemon(-2), TransformOp.Rotate(Vector3d.Zero, new Vector3d(1, 2, 3), 33)))),
            ("NURBS loft: Bezier \"D\" sections, smooth, λ 1→0.75→0.5", () => { Parametric3D.Kernel.Ops.Profile D(double lam, double z) { Vector3d P(double x, double y) => new(x * lam, y * lam, z); return Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.LineCurve3(P(-5, 0), P(5, 0)), new Parametric3D.Kernel.Curves.BezierCurve3(new[] { P(5, 0), P(5, 8), P(-5, 8), P(-5, 0) }) }); } return LoftOp.Build(new[] { D(1, 0), D(0.75, 10), D(0.5, 20) }, 64, true); }),
            ("guided loft: square 5 → 3, four quadratic Bezier guides", () => { Parametric3D.Kernel.Ops.Profile Sq(double a, double z) { Vector3d P(double x, double y) => new(x, y, z); return Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.LineCurve3(P(-a, -a), P(a, -a)), new Parametric3D.Kernel.Curves.LineCurve3(P(a, -a), P(a, a)), new Parametric3D.Kernel.Curves.LineCurve3(P(a, a), P(-a, a)), new Parametric3D.Kernel.Curves.LineCurve3(P(-a, a), P(-a, -a)) }); } var gs = new[] { (1, 1), (-1, 1), (-1, -1), (1, -1) }.Select(q => (Parametric3D.Kernel.Curves.ICurve3)new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(q.Item1 * 5, q.Item2 * 5, 0), new Vector3d(q.Item1 * 7, q.Item2 * 7, 15), new Vector3d(q.Item1 * 3, q.Item2 * 3, 30) })).ToList(); return LoftOp.Build(new[] { Sq(5, 0), Sq(3, 30) }, 64, false, gs); }),
            ("guided loft: circle r10 → r10, two bulging arc guides", () => { Parametric3D.Kernel.Ops.Profile Ci(double z) => Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), 10, 0, 2 * Math.PI) }); double t = Math.Atan2(15, 36); var g1 = new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(-26, 0, 15), Vector3d.UnitX, Vector3d.UnitZ), 39, -t, t); var g2 = new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(26, 0, 15), -Vector3d.UnitX, Vector3d.UnitZ), 39, -t, t); return LoftOp.Build(new[] { Ci(0), Ci(30) }, 64, false, new Parametric3D.Kernel.Curves.ICurve3[] { g1, g2 }); }),
            ("loft with end tangency: circle r10 → r5, tangents 1 and 1", () => { Parametric3D.Kernel.Ops.Profile Ci(double r, double z) => Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), r, 0, 2 * Math.PI) }); return LoftOp.Build(new[] { Ci(10, 0), Ci(5, 30) }, 64, false, null, 1, 1); }),
            ("composite (g.08): housing — rounds, draft, top loop, shell open at blends, inner boss", () => { var p = Krn07Scenarios()[0].Make(); p.Rebuild(null); return p.Bodies[0]; }),
            ("composite (g.08): handle — plate, Bezier-arch sweep, union, mirror", () => { var p = Krn07Scenarios()[1].Make(); p.Rebuild(null); return p.Bodies[0]; }),
            ("composite (g.08): transition — plate, loft square → circle with tangents", () => { var p = Krn07Scenarios()[2].Make(); p.Rebuild(null); return p.Bodies[0]; }),
            ("composite (06.2): lid — vertical rounds, hole, top loop with a radius law", () => { var p = Krn07Scenarios()[3].Make(); p.Rebuild(null); return p.Bodies[0]; }),
            ("radius law on an open chain: two right corners R8, top chain 3 → 1.5 → 3.5 → 2, ends on the left wall", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1 && e.V0.P.X > 59).Select(EdgeRef.Of).ToList(), 8, new List<string>()); var es = bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - 20) < 1e-7 && e.Curve.PointAt(0.5).X > 1e-3).ToList(); var front = es.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Y) < 1e-7 && Math.Abs(e.V1.P.Y) < 1e-7); var refs = new List<EdgeRef> { front.V0.P.X < front.V1.P.X ? EdgeRef.Of(front) : new EdgeRef(front.V1.P, front.V0.P, front.Curve.PointAt(0.5), front.FaceA?.Tag, front.FaceB?.Tag) }; refs.AddRange(es.Where(e => e != front).Select(EdgeRef.Of)); FilletOp.RoundEdgesLaw(bl, refs, new[] { (0.0, 3.0), (0.25, 1.5), (0.6, 3.5), (1.0, 2.0) }, new List<string>()); return bl[0]; }),
            ("radius law across a mitre: box top front and right edges, 2 → 4", () => { var b = Box(0, 0, 0, 60, 40, 20); var front = b.Edges.First(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7 && Math.Abs(e.V0.P.Y) < 1e-7 && Math.Abs(e.V1.P.Y) < 1e-7); var right = b.Edges.First(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7 && Math.Abs(e.V0.P.X - 60) < 1e-7 && Math.Abs(e.V1.P.X - 60) < 1e-7); var bl = new List<TSolid> { b }; FilletOp.RoundEdgesLaw(bl, new List<EdgeRef> { front.V0.P.X < front.V1.P.X ? EdgeRef.Of(front) : new EdgeRef(front.V1.P, front.V0.P, front.Curve.PointAt(0.5), front.FaceA?.Tag, front.FaceB?.Tag), EdgeRef.Of(right) }, new[] { (0.0, 2.0), (1.0, 4.0) }, new List<string>()); return bl[0]; }),
            ("radius law on a tangent loop: 3 → 1.5 → 3.5 → 3 (lunes and creases at joints)", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 8, new List<string>()); var top = bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - 20) < 1e-7).Select(EdgeRef.Of).ToList(); FilletOp.RoundEdgesLaw(bl, top, new[] { (0.0, 3.0), (0.2, 1.5), (0.55, 3.5), (1.0, 3.0) }, new List<string>()); return bl[0]; }),
            ("boolean over a rolling-ball band: shaft R20, hole d8 r1, minus a slot", () => { var b = new List<TSolid> { TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)) }; BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), new List<string>()); FilletOp.RoundEdges(b, b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt(0.5).Z > 5).Select(EdgeRef.Of).ToList(), 1, new List<string>()); BooleanOp.Subtract(b, Box(-1, -30, 15, 2, 60, 10), new List<string>()); return b[0]; }),
            ("equal-radius tee: shaft R20 ∪ boss R20, junction rounded r3 (lens bands, saddles)", () => { var b = new List<TSolid> { TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90)) }; BooleanOp.Unite(b, PrimitiveOp.Cylinder(Vector3d.Zero, 20, 40), new List<string>()); FilletOp.RoundEdges(b, b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.Curve.PointAt(0.5).Z > 1).Select(EdgeRef.Of).ToList(), 3, new List<string>()); return b[0]; }),
            ("draft 5° of Bezier walls: stadium with Bezier ends, neutral at bottom", () => { var (lp, _, _) = BezierStadium(10, 8, 8); var bl = new List<Parametric3D.Kernel.Topology.TSolid> { ExtrudeOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), lp), new Vector3d(0, 0, 20)) }; if (!DraftOp.Apply(bl, new[] { new Vector3d(16, 0, 10) }, Vector3d.Zero, Vector3d.UnitZ, 5, new List<string>())) throw new InvalidOperationException("draft declined"); return bl[0]; }),
            ("shell, open face at blends: box R5 + top loop r3, open top, wall 1", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>()); FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7).Select(EdgeRef.Of).ToList(), 3, new List<string>()); if (!ShellOp.Hollow(bl, 1, new[] { new Vector3d(30, 20, 20) }, new List<string>())) throw new InvalidOperationException("shell declined"); return bl[0]; }),
            ("shell, own thickness: rounded box R5, walls 2, bottom 4, open top", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>()); if (!ShellOp.Hollow(bl, 2, new[] { new Vector3d(30, 20, 20) }, new List<string>(), new[] { (new Vector3d(30, 20, 0), 4.0) })) throw new InvalidOperationException("shell declined"); return bl[0]; }),
            ("tangent loop r = R: box R5 vertical rounds, top loop r5 (sphere corners)", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>()); FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7).Select(EdgeRef.Of).ToList(), 5, new List<string>()); return bl[0]; }),
            ("tangent loop over inclined walls: drafted box R5 5°, top loop r2", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>()); DraftOp.Apply(bl, new[] { new Vector3d(60, 20, 10), new Vector3d(30, 0, 10), new Vector3d(0, 20, 10), new Vector3d(30, 40, 10) }, Vector3d.Zero, Vector3d.UnitZ, 5, new List<string>()); FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - 20) < 1e-7).Select(EdgeRef.Of).ToList(), 2, new List<string>()); return bl[0]; }),
            ("partial vertex (06.9): pyramid 4, two adjacent apex edges of four, r2", () => { var src = PyramidBody(4, 0.5); var bl = new List<TSolid> { src }; FilletOp.RoundEdges(bl, new[] { 0, 1 }.Select(q => EdgeRef.Of(PyramidEdge(src, 4, 0.5, q)!)).ToList(), 2, new List<string>()); return bl[0]; }),
            ("partial vertex (06.9): pyramid 6, three alternating apex edges of six, r1.5", () => { var src = PyramidBody(6, 0.7); var bl = new List<TSolid> { src }; FilletOp.RoundEdges(bl, new[] { 0, 2, 4 }.Select(q => EdgeRef.Of(PyramidEdge(src, 6, 0.7, q)!)).ToList(), 1.5, new List<string>()); return bl[0]; }),
            ("setback corner: pocket ceiling r2", () => { var bl = new List<TSolid> { Box(-20, -20, 25, 80, 80, 30) }; BooleanOp.Subtract(bl, PyramidBody(4, 0.7), new List<string>()); FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => (e.V0.P - new Vector3d(20, 20, 40)).Length < 1e-6 || (e.V1.P - new Vector3d(20, 20, 40)).Length < 1e-6).Select(EdgeRef.Of).ToList(), 2, new List<string>()); return bl[0]; }),
        })
        {
            string id = $"c{i++:D2}";
            try
            {
                var b = make(); var bl = new List<TSolid> { b };
                if (b.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic) { man.Add($"{id}\t{name}\tnot exact: {b.Guarantee}\t\t\t\t\t\t\t"); continue; }
                var sw = new StringWriter(); var ws = new List<string>();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, bl, id, ws)) { man.Add($"{id}\t{name}\tno exact step: {string.Join("; ", ws).Replace('\t', ' ')}\t\t\t\t\t\t\t"); continue; }
                File.WriteAllText(Path.Combine(dir, id + ".step"), sw.ToString());
                man.Add($"{id}\t{name}\tok\t1\t{b.Faces.Count()}\t{AnalyticVolume.Of(b):R}\t{AnalyticVolume.AreaOf(b):R}\tNaN\tExactAnalytic\tFalse\t0");
            }
            catch (Exception ex) { man.Add($"{id}\t{name}\texception: {ex.Message.Replace('\t', ' ')}\t\t\t\t\t\t\t"); }
        }
        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), man);
        Console.WriteLine($"G14 dump: {man.Count - 1} cases → {dir}");
    }

    /// <summary>
    /// Детали «реального использования»: составные истории из многих операций. Пластины K08 (генератор деталей «как рисуют
    /// руками»: карманы, бобышки, отверстия простые/сквозные/с цековкой, скругления и фаски углов) и детали тел вращения.
    /// Ожидание по формуле — где оно выводится независимо (фланец, вал); у пластин — только сверка двух ядер.
    /// </summary>
    internal static List<(string Name, Func<Part> Build, double Expected)> G14RealParts()
    {
        var res = new List<(string, Func<Part>, double)>();
        foreach (var (seed, n) in new[] { (8001, 20), (8002, 30), (8003, 40), (8004, 60), (8005, 20), (8006, 20), (8007, 25), (8008, 25) })
            res.Add(($"plate K08 seed {seed}, {n} features", () => RealisticPart(seed, n, out _), double.NaN));
        Frame3 XY(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        Frame3 XZ = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
        // Фланец: кольцо вращения (ступица + диск), 6 сквозных отверстий d9 на окружности R45 круговым массивом.
        res.Add(("flange: revolve hub + disc, 6 through holes d9 on R45", () =>
        {
            var p = new Part("flange");
            var s = PolySketch(p, XZ, (15, 0), (60, 0), (60, 12), (25, 12), (25, 40), (15, 40));
            var ax = new SkLine(new SkPoint(0, 0), new SkPoint(0, 40)) { Role = SkRole.Centerline }; s.AddEntity(ax);
            p.AddFeature(new RevolveFeature { SketchId = s.Id, AxisEntityId = ax.Id });
            // Массив тела (CircularPatternFeature) объединяет повёрнутые копии ТЕЛА — отверстия заполнились бы соседними
            // копиями (так и вышло в первой записи модели: объём без отверстий, по смыслу операции верно). Отверстия — точками.
            var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(9), ThroughAll = true };
            for (int k = 0; k < 6; k++) h.Points.Add(new Vector3d(45 * Math.Cos(k * Math.PI / 3), 45 * Math.Sin(k * Math.PI / 3), 12));
            p.AddFeature(h);
            return p;
        }, Math.PI * (60 * 60 - 15 * 15) * 12 + Math.PI * (25 * 25 - 15 * 15) * 28 - 6 * Math.PI * 4.5 * 4.5 * 12));
        // Вал: ступени Ø40×30, Ø30×50, Ø20×20 (вращение), канавка 2×2 на Ø30, глухое осевое отверстие d6 глубиной 15 в торце.
        res.Add(("shaft: stepped revolve, groove by revolve cut, axial hole", () =>
        {
            var p = new Part("shaft");
            var s = PolySketch(p, XZ, (0, 0), (20, 0), (20, 30), (15, 30), (15, 80), (10, 80), (10, 100), (0, 100));
            var ax = new SkLine(new SkPoint(0, 0), new SkPoint(0, 100)) { Role = SkRole.Centerline }; s.AddEntity(ax);
            p.AddFeature(new RevolveFeature { SketchId = s.Id, AxisEntityId = ax.Id });
            var g = PolySketch(p, XZ, (13, 50), (16, 50), (16, 52), (13, 52));
            var ax2 = new SkLine(new SkPoint(0, 0), new SkPoint(0, 100)) { Role = SkRole.Centerline }; g.AddEntity(ax2);
            p.AddFeature(new RevolveFeature { SketchId = g.Id, AxisEntityId = ax2.Id, Cut = true });
            var h = new HoleFeature("C") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(6), Depth = new ValueRef(15) }; h.Points.Add(new Vector3d(0, 0, 100));
            p.AddFeature(h);
            return p;
        }, Math.PI * (400 * 30 + 225 * 50 + 100 * 20) - Math.PI * (225 - 169) * 2 - Math.PI * 9 * 15));
        // Корпус: брусок 80×60×40, оболочка 3 с открытым верхом, 4 бобышки Ø10 высотой 6 на дне внутри, отверстия d4 в них.
        res.Add(("housing: block, shell t=3 open top, inner bosses with holes", () =>
        {
            var p = new Part("housing");
            var s = PolySketch(p, XY(0), (-40, -30), (40, -30), (40, 30), (-40, 30));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(40); p.AddFeature(ex);
            var sh = new ShellFeature { Thickness = new ValueRef(3) }; sh.OpenFaces.Add(new Vector3d(0, 0, 40)); p.AddFeature(sh);
            foreach (var (x, y) in new[] { (-28.0, -18.0), (28.0, -18.0), (28.0, 18.0), (-28.0, 18.0) })
            {
                var c = CircleSketch(p, 3, x, y, 5);
                var b = new ExtrudeFeature { SketchId = c.Id }; b.Side1.Distance = new ValueRef(6); p.AddFeature(b);
                var h = new HoleFeature("B") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(4), Depth = new ValueRef(6) }; h.Points.Add(new Vector3d(x, y, 9));
                p.AddFeature(h);
            }
            return p;
        }, 80.0 * 60 * 40 - 74.0 * 54 * 37 + 4 * (Math.PI * 25 * 6 - Math.PI * 4 * 6)));
        return res;
    }

}
