using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Export;
using Parametric3D.Kernel.MeshFit;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>k119: тело-образец для проверок перевода сетки (имя → тело и точный объём).</summary>
    static (List<TSolid> Bodies, double Volume) MeshFitSample(string which)
    {
        List<TSolid> B(params TSolid[] b) => b.ToList();
        List<TSolid> bodies = which switch
        {
            "box" => B(BoxSolid(new Vector3d(0, 0, 0), 100, 60, 20)),
            "cyl" => B(PrimitiveOp.Cylinder(new Vector3d(3, 4, 0), 7, 20)),
            "cone" => B(PrimitiveOp.Cone(Vector3d.Zero, 10, 4, 15)),
            "sphere" => B(PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 12)),
            "torus" => B(PrimitiveOp.Torus(Vector3d.Zero, 20, 5)),
            "holes" => HolesSolid(),
            "real" => PartBodies(RealisticPart(3, 20, out _)),
            "real7" => PartBodies(RealisticPart(7, 14, out _)),
            "user" => UserBodies(),
            "rounded" => RoundedBodies(false),
            "holes100" => Holes100Bodies(),
            "rounded4" => RoundedBodies(true),
            _ => throw new ArgumentException(which),
        };
        double v = bodies.Sum(MeshToBrep.VolumeOf);
        return (bodies, v);
    }

    static TSolid BoxSolid(Vector3d at, double w, double d, double h)
    {
        var f = new Frame3(at, Vector3d.UnitX, Vector3d.UnitY);
        var a = at; var b = at + new Vector3d(w, 0, 0); var c = at + new Vector3d(w, d, 0); var e = at + new Vector3d(0, d, 0);
        var prof = ProfileBuilder.Build(f, new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.LineCurve3(a, b), new Parametric3D.Kernel.Curves.LineCurve3(b, c), new Parametric3D.Kernel.Curves.LineCurve3(c, e), new Parametric3D.Kernel.Curves.LineCurve3(e, a) });
        return ExtrudeOp.Build(prof, new Vector3d(0, 0, h));
    }

    /// <summary>Плита 100×60×20 со сквозными отверстиями R5 и R8, глухим R6 и бобышкой R10 с конусом.</summary>
    static List<TSolid> HolesSolid()
    {
        var bodies = new List<TSolid> { BoxSolid(Vector3d.Zero, 100, 60, 20) };
        var w = new List<string>();
        BooleanOp.Subtract(bodies, PrimitiveOp.Cylinder(new Vector3d(20, 20, -1), 5, 22), w);
        BooleanOp.Subtract(bodies, PrimitiveOp.Cylinder(new Vector3d(50, 30, -1), 8, 22), w);
        BooleanOp.Subtract(bodies, PrimitiveOp.Cylinder(new Vector3d(80, 40, 8), 6, 13), w);
        BooleanOp.Unite(bodies, PrimitiveOp.Cylinder(new Vector3d(25, 45, 20), 10, 8), w);
        BooleanOp.Unite(bodies, PrimitiveOp.Cone(new Vector3d(25, 45, 28), 10, 6, 5), w);
        return bodies;
    }

    /// <summary>Брусок 60×40×20, скруглены все 12 рёбер R3 (цилиндры и шары в углах) или четыре вертикальных.</summary>
    static List<TSolid> RoundedBodies(bool vertical)
    {
        var p = new Part("rb"); var s = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (0, 0), (60, 0), (60, 40), (0, 40));
        var ex = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(20); p.AddFeature(ex); p.Rebuild(null);
        var rf = new Parametric3D.Model.Features.RoundFeature { Radius = new Parametric3D.Model.Features.ValueRef(3) };
        foreach (var e in p.Bodies[0].Edges.Where(e => !vertical || e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.X - e.V1.P.X) < 1e-9 && Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-9)) rf.Edges.Add(EdgeRef.Of(e));
        p.AddFeature(rf);
        var r = p.Compute(null); p.Apply(r);
        return p.Bodies.ToList();
    }

    static List<TSolid> Holes100Bodies()
    {
        var bodies = new List<TSolid> { BoxSolid(new Vector3d(-100, -100, -100), 200, 200, 200) };
        var w = new List<string>();
        int side = 10; double step = 200.0 / (side + 1);
        for (int i = 0; i < 100; i++)
        {
            double x = -100 + step * (1 + i % side), y = -100 + step * (1 + i / side);
            BooleanOp.Subtract(bodies, PrimitiveOp.Cylinder(new Vector3d(x, y, -101), 2, 202), w);
        }
        return bodies;
    }

    static List<TSolid> UserBodies()
    {
        var (p, prm) = DocumentFile.Load(File.ReadAllText(Fixture("user_k116.p3d")));
        var doc = new Document(); doc.Root.Add(p);
        p.Rebuild(prm);
        return p.Bodies.ToList();
    }

    static List<TSolid> PartBodies(Part p) { p.Rebuild(null); return p.Bodies.ToList(); }

    /// <summary>Треугольники тел через двоичный STL (точность float, как у файла).</summary>
    static List<(Vector3d, Vector3d, Vector3d)> StlOf(List<TSolid> bodies)
    {
        var tris = MeshExport.Triangles(bodies).Select(t => (t.A, t.B, t.C)).ToList();
        return StlFile.Read(StlFile.WriteBinary(tris));
    }

    static void MeshFitProbe(string which, string mode)
    {
        if (mode == "part")
        {
            var (bb, v00) = MeshFitSample(which);
            var p = new Part("mi"); var doc = new Document(); doc.Root.Add(p);
            var f = new Parametric3D.Model.Features.MeshImportFeature("Import") { SourceName = which, MeshData = Parametric3D.Model.Features.MeshImportFeature.Pack(StlOf(bb)) };
            p.AddFeature(f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = true;
            p.Rebuild(null);
            Console.WriteLine($"part rebuild {sw.ElapsedMilliseconds} ms, faces {p.Bodies.Sum(b => b.Faces.Count())}, volume {p.Volume:0.###} vs {v00:0.###}, error {f.Error}; {f.Report}");
            Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
            return;
        }
        if (mode.StartsWith("seed"))
        {
            var (bb, _) = MeshFitSample(which);
            var pmx = PreparedMesh.Prepare(StlOf(bb));
            var sg = new Segmenter(pmx, pmx.Diag * 1e-5);
            if (mode == "seedrun") { sg.Trace = Console.WriteLine; sg.Run(); return; }
            for (int t = 0; t < pmx.TriCount; t += Math.Max(1, pmx.TriCount / 8)) Console.WriteLine($"seed {t} n {pmx.TN[t]}: {sg.DebugSeed(t)}");
            return;
        }
        var (bodies, v0) = which.StartsWith("file:") ? (new List<TSolid>(), double.NaN) : MeshFitSample(which);
        var tris = which.StartsWith("file:") ? StlFile.ReadFile(which[5..]) : StlOf(bodies);
        var opt = new MeshFitOptions { Exact = mode != "flat" };
        if (mode.StartsWith("noise"))
        {
            // шум «скана»: каждая вершина сдвинута случайно (одинаково во всех треугольниках) на ±amp
            double amp = double.Parse(mode[5..].TrimEnd('v'), System.Globalization.CultureInfo.InvariantCulture);
            var rnd = new Random(7); var moved = new Dictionary<(long, long, long), Vector3d>();
            Vector3d J(Vector3d p) { var k = ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4)); if (!moved.TryGetValue(k, out var d)) moved[k] = d = new Vector3d(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1) * amp; return p + d; }
            tris = tris.Select(t => (J(t.Item1), J(t.Item2), J(t.Item3))).ToList();
            opt.TolScale = Math.Max(1, 3 * amp / (Fits.Span(tris.Select(t => t.Item1).ToList()) * 1e-5));
        }
        if (mode.EndsWith("v")) MeshToBrep.Debug = Console.WriteLine;
        var r = MeshToBrep.Convert(tris, opt, which);
        Console.WriteLine($"{which}: {tris.Count} triangles; {r.Summary}");
        Console.WriteLine($"  original: faces {bodies.Sum(b => b.Faces.Count())}: " + string.Join(", ", bodies.SelectMany(b => b.Faces).GroupBy(f => f.Surface.GetType().Name).Select(g => g.Key + " " + g.Count())));
        Console.WriteLine($"  volume: exact {v0:0.####}, mesh {r.MeshVolume:0.####}, body {r.BodyVolume:0.####} (err {(r.BodyVolume - v0) / v0 * 100:0.####}%), tol {r.Tolerance:g3}");
        Console.WriteLine($"  snaps: dir {r.DirSnaps}, coaxial {r.Coaxial}, values {r.ValueSnaps}, tangents {r.Tangents}; tolerant edges {r.TolerantEdges}; leashes {r.Leashes}; notes: {string.Join(" | ", r.Notes)}");
        if (MeshToBrep.LastRegions is { } regs && mode == "v")
            foreach (var g in regs.GroupBy(x => x.S.ToString()).OrderByDescending(g => g.Count()).Take(40)) Console.WriteLine($"    {g.Count()} × {g.Key}");
        if (MeshToBrep.LastRegions is { } regs2 && mode == "v" && MeshToBrep.LastMesh is { } pm)
            foreach (var rg in regs2.Where(x => x.S.Kind != FitKind.Plane))
            {
                var ns = rg.Tris.Select(t => pm.TN[t]).ToList();
                var cl = new List<Vector3d>();
                foreach (var n in ns) if (!cl.Any(c => Vector3d.Dot(c, n) > Math.Cos(0.5 * Math.PI / 180))) cl.Add(n);
                Console.WriteLine($"    {rg.S}: tris {rg.Tris.Count}, area {rg.Area:0.##}, normal clusters {cl.Count}, sense {rg.Sense}");
            }
    }

    /// <summary>
    /// k119: СЕТКА → ТОЧНОЕ ТЕЛО. Тела нашего ядра выводятся в двоичный STL (float, как у файла) и переводятся обратно:
    /// число граней — как у исходного тела, виды поверхностей — те же, объём — до 10⁻⁵ точного (у сетки — до 0.5%).
    /// </summary>
    static void Krn119Checks()
    {
        MeshFitResult Run(string which, bool exact = true)
        {
            var (bodies, _) = MeshFitSample(which);
            return MeshToBrep.Convert(StlOf(bodies), new MeshFitOptions { Exact = exact }, which);
        }
        void Exact(string which, int faces, int planes, int cyl, int cones, int spheres, int tori, double relVol = 1e-5)
        {
            var (bodies, v0) = MeshFitSample(which);
            var r = MeshToBrep.Convert(StlOf(bodies), new MeshFitOptions(), which);
            Console.WriteLine($"  {which}: {r.Summary}; volume {r.BodyVolume:0.####} vs {v0:0.####} (mesh {r.MeshVolume:0.####})");
            Require(r.Body != null && r.Exact, $"{which}: not converted exactly: {string.Join("; ", r.Notes)}");
            Require(r.Regions == faces && r.Planes == planes && r.Cylinders == cyl && r.Cones == cones && r.Spheres == spheres && r.Tori == tori,
                $"{which}: faces {r.Regions} (planes {r.Planes}, cylinders {r.Cylinders}, cones {r.Cones}, spheres {r.Spheres}, tori {r.Tori})");
            Require(Math.Abs(r.BodyVolume - v0) <= relVol * Math.Abs(v0), $"{which}: volume {r.BodyVolume} vs {v0}");
            Require(SolidValidator.ValidateBRep(r.Body!).IsValid, $"{which}: invalid body");
        }
        Check("KRN-MESHFIT-PRIMITIVES-K119", () =>
        {
            // плоскости, цилиндр, конус (полоса в один ряд — ось по разбросу нормалей, P3D_MESHFITCONE), сфера, тор (слияние
            // полос, P3D_MESHFITMERGE; целый тор — четыре грани)
            Exact("box", 6, 6, 0, 0, 0, 0);
            Exact("cyl", 3, 2, 1, 0, 0, 0);
            Exact("cone", 3, 2, 0, 1, 0, 0);
            Exact("sphere", 2, 0, 0, 0, 2, 0);
            Exact("torus", 4, 0, 0, 0, 0, 4);
        });
        Check("KRN-MESHFIT-PARTS-K119", () =>
        {
            // плита с отверстиями и бобышкой с конусом, скруглённый брусок (цилиндры и шары в углах), детали-образцы из кубиков
            // (стенки под 40° — не «цилиндр», P3D_MESHFITCURVED), деталь пользователя (касания, допуски рёбер — P3D_MESHFITTOL)
            Exact("holes", 13, 8, 4, 1, 0, 0);
            Exact("rounded", 26, 6, 12, 0, 8, 0);
            Exact("rounded4", 10, 6, 4, 0, 0, 0);
            Exact("real7", 30, 25, 5, 0, 0, 0);
            Exact("real", 35, 22, 13, 0, 0, 0);
            Exact("user", 35, 22, 9, 4, 0, 0);
        });
        Check("KRN-MESHFIT-SNAP-K119", () =>
        {
            // выравнивание по замыслу (P3D_MESHFITSNAP): ось цилиндра — точно Z, радиус — точно 7, центр — (3, 4); у плиты — оси
            // отверстий точно Z, радиусы 5, 8, 6, 10; касание стенок и скруглений точно — вершины тела на всех гранях (P3D_MESHFITTANGENT)
            var r = Run("cyl");
            var c = MeshToBrep.LastRegions!.Single(x => x.S.Kind == FitKind.Cylinder).S;
            Console.WriteLine($"  cylinder: R {c.R:R}, axis {c.A}, point {c.P}");
            Require(c.R == 7 && c.A == Vector3d.UnitZ && Math.Abs(c.P.X - 3) < 1e-12 && Math.Abs(c.P.Y - 4) < 1e-12, "cylinder is not aligned to the design intent");
            Run("holes");
            var radii = MeshToBrep.LastRegions!.Where(x => x.S.Kind == FitKind.Cylinder).Select(x => x.S.R).OrderBy(x => x).ToList();
            Console.WriteLine($"  plate radii: {string.Join(", ", radii.Select(x => x.ToString("R")))}");
            Require(radii.SequenceEqual(new double[] { 5, 6, 8, 10 }), "hole radii are not round");
            var rr = Run("real");
            Console.WriteLine($"  realistic part: tangents {rr.Tangents}, corners left at mesh vertices {rr.Leashes}");
            Require(rr.Leashes == 0, $"corners left off their faces: {rr.Leashes}");
        });
        Check("KRN-MESHFIT-FLAT-K119", () =>
        {
            // без преобразования: тело из плоских граней, объём — как у сетки; STL текстом читается так же, как двоичный
            var (bodies, _) = MeshFitSample("holes");
            var tris = StlOf(bodies);
            var r = MeshToBrep.Convert(tris, new MeshFitOptions { Exact = false }, "flat");
            Console.WriteLine($"  flat: {r.Summary}");
            Require(r.Body != null && !r.Exact && r.Planes == r.Regions && Math.Abs(r.BodyVolume - r.MeshVolume) <= 1e-6 * r.MeshVolume, "flat body");
            var ascii = new System.Text.StringBuilder("solid t\n");
            foreach (var (a, b, c) in tris.Take(50))
                ascii.Append(System.FormattableString.Invariant($"facet normal 0 0 1\n outer loop\n  vertex {a.X:R} {a.Y:R} {a.Z:R}\n  vertex {b.X:R} {b.Y:R} {b.Z:R}\n  vertex {c.X:R} {c.Y:R} {c.Z:R}\n endloop\nendfacet\n"));
            ascii.Append("endsolid t\n");
            var back = StlFile.Read(System.Text.Encoding.ASCII.GetBytes(ascii.ToString()));
            Require(back.Count == 50 && back.Zip(tris).All(x => x.First == x.Second), "ASCII STL");
            // перевод отключён (P3D_MESHFIT=0) — только плоские; здесь включён: точное тело
            Require(MeshToBrep.Convert(tris, new MeshFitOptions(), "x").Exact, "exact conversion is off");
        });
    }
}

