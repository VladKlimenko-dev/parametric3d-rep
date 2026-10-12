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
