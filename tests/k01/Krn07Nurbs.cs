using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    const double NurbsV0 = Math.PI * 100 * 20 / 4;
    /// <summary>Случаи булевых с телом-четвертью цилиндра NURBS (проверка KRN07 и каталог g.14 для OpenCascade).</summary>
    internal static (string Name, Func<TSolid> Tool, double Want)[] NurbsBooleanCases()
    {
        static double Slab(double x0) { double F(double x) => x / 2 * Math.Sqrt(100 - x * x) + 50 * Math.Asin(x / 10); return (F(10) - F(x0)) * 20; }
        return new (string, Func<TSolid>, double)[]
        {
            ("plane z=15 (top cut)", () => Box(-20, -20, 15, 40, 40, 20), NurbsV0 * 15 / 20),
            ("plane x=3 (slab cut)", () => Box(-20, -20, -5, 23, 40, 40), Slab(3)),
            ("hole r2 clear of the NURBS face", () => PrimitiveOp.Cylinder(new Vector3d(5, 5, -5), 2, 40), NurbsV0 - Math.PI * 4 * 20),
            ("cylinder r3 across the NURBS face", () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(-5, 5, 10), 3, 30), TransformOp.Rotate(new Vector3d(-5, 5, 10), Vector3d.UnitY, 90)), double.NaN),
            ("sphere r3 on the NURBS face", () => PrimitiveOp.Sphere(new Vector3d(7.0710678, 7.0710678, 10), 3), double.NaN),
            ("small sphere r0.1 on the NURBS face (closed branch)", () => PrimitiveOp.Sphere(new Vector3d(6, 8, 7), 0.1), double.NaN),
        };
    }
    internal static (string Name, TransformOp.Xform X)[] NurbsNNCases()
    {
        static TransformOp.Xform Comp(TransformOp.Xform r, Vector3d mv) => new(p => r.Point(p) + mv, r.Direction, r.Flips);
        return new (string, TransformOp.Xform)[]
        {
            ("tool turned 60° about X and shifted 1.5 along X (crossing cylinders)", Comp(TransformOp.Rotate(new Vector3d(5, 8, 10), Vector3d.UnitX, 60), new Vector3d(1.5, 0, 0))),
            ("tool shifted (-4,-4,5) (parallel axes)", TransformOp.Move(new Vector3d(-4, -4, 5))),
            ("tool turned 30° about (1,1,0) and shifted", Comp(TransformOp.Rotate(new Vector3d(5, 5, 10), new Vector3d(1, 1, 0).Normalized(), 30), new Vector3d(2, -1, 3))),
        };
    }
    internal static TSolid NurbsQuarterCylinder() => Parametric3D.Kernel.Export.StepImport.ReadExactSolids(File.ReadAllText(Fixture("quarter_cyl.step")), new List<string>())[0];

    static void Krn07NurbsChecks()
    {
        Check("KRN07-NURBS-NURBS-BOOLEAN", () =>
        {
            // 04.9, шаг N2: вычитание тела с гранью NURBS из тела с гранью NURBS — пересечение двух сплайнов (S₁ = S₂, удостоверение
            // парами кусков Безье). Резец — та же четверть цилиндра NURBS, перенесённая точно (TopologyTransform переносит сплайн
            // опорными точками). Эталон — та же операция над четвертями цилиндра из примитивов, перенесёнными тем же образом.
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("NURBSDBG") == "1") BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   ROUTE " + m);
            if (Environment.GetEnvironmentVariable("NURBSDBG") == "1") Parametric3D.Kernel.Geometry.NurbsNurbsSsi.Debug = m => Console.WriteLine("   NN " + m);
            var src = NurbsQuarterCylinder();
            TSolid Analytic() { var bl = new List<TSolid> { Box(0, 0, 0, 10, 10, 20) }; BooleanOp.Intersect(bl, PrimitiveOp.Cylinder(new Vector3d(0, 0, -1), 10, 22), new List<string>()); return bl[0]; }
            foreach (var (name, xf) in NurbsNNCases())
            {
                var tool = TopologyTransform.Apply(TopologyClone.Deep(src), xf);
                if (tool == null) { fails.Add($"{name}: NURBS body not transformed"); continue; }
                var bl = new List<TSolid> { TopologyClone.Deep(src) }; var w = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew(); BooleanOp.Subtract(bl, tool, w); sw.Stop();
                var rl = new List<TSolid> { Analytic() }; BooleanOp.Subtract(rl, TopologyTransform.Apply(Analytic(), xf)!, new List<string>());
                double want = rl.Sum(x => AnalyticVolume.Of(x) ?? double.NaN);
                double v = bl.Sum(x => AnalyticVolume.Of(x) ?? double.NaN);
                double area = bl.Sum(x => AnalyticVolume.AreaOf(x) ?? double.NaN), areaWant = rl.Sum(x => AnalyticVolume.AreaOf(x) ?? double.NaN);
                if (Environment.GetEnvironmentVariable("NURBSDBG") == "1")
                {
                    var swr = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(swr, rl, "ref", new List<string>());
                    File.WriteAllText($"/home/claude/out/nn_ref_{name.Split(' ')[2]}.step", swr.ToString());
                    foreach (var f in rl[0].Faces) Console.WriteLine($"REFAREA {name.Split(' ')[2]} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} {AnalyticVolume.FaceArea(f):R} {string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve.GetType().Name))}");
                }
                if (!(Math.Abs(area - areaWant) <= 1e-8 * areaWant)) fails.Add($"{name}: area {area:R} vs reference {areaWant:R}");
                bool ok = bl.Count == rl.Count && bl.All(x => x.Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic && SolidValidator.Validate(x).IsValid) && Math.Abs(v - want) <= 1e-8 * NurbsV0;
                Log(new { test = "KRN07-NURBS-NN", name, volume = v.ToString("R"), want = want.ToString("R"), area = area.ToString("R"), areaWant = areaWant.ToString("R"), bodies = bl.Count, guarantee = string.Join(",", bl.Select(x => x.Guarantee)), ms = sw.ElapsedMilliseconds });
                if (!ok) fails.Add($"{name}: bodies {bl.Count}/{rl.Count} {string.Join(",", bl.Select(x => x.Guarantee))} V {v:R} want {want:R} {string.Join(" ", bl.Select(x => SolidValidator.Validate(x).Summary()))} | {string.Join(" | ", w.Take(2))}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN07-NURBS-EXACT-BOOLEAN", () =>
        {
            // 07.8 / 04.9, шаг N1: тело с гранью NURBS (четверть цилиндра R10 h20 из чужого STEP — рациональный сплайн) в общем
            // точном маршруте булевой: пересечение грани NURBS с плоскостью, цилиндром, шаром — нулевое множество F(S(u, v)) с
            // удостоверением полноты по кускам Безье; грань, которую резец не задевает, — доказательство пустого пересечения.
            // Эталон — та же операция над четвертью цилиндра из примитивов (точные цилиндр и плоскости) либо формула.
            // Требования: ExactAnalytic, тело годно, объём совпадает с эталоном до 1e-8 отн.
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("NURBSDBG") == "1") BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   ROUTE " + m);
            var src = NurbsQuarterCylinder();
            TSolid Analytic() { var bl = new List<TSolid> { Box(0, 0, 0, 10, 10, 20) }; BooleanOp.Intersect(bl, PrimitiveOp.Cylinder(new Vector3d(0, 0, -1), 10, 22), new List<string>()); return bl[0]; }
            double V0 = NurbsV0;
            if (Environment.GetEnvironmentVariable("NURBSDBG") == "1")
            {
                var ws = new List<string>(); var swr = new StringWriter();
                bool wr = Parametric3D.Kernel.Export.StepExact.Write(swr, new[] { src }, "n", ws);
                var back = wr ? Parametric3D.Kernel.Export.StepImport.ReadExactSolids(swr.ToString(), ws) : new List<TSolid>();
                Console.WriteLine($"   STEP source: written {wr} back {back.Count} V {(back.Count == 1 ? AnalyticVolume.Of(back[0]) : null):R} src V {AnalyticVolume.Of(src):R} | {string.Join(" | ", ws.Take(4))}");
                File.WriteAllText("/home/claude/out/nurbs_src_rt.step", swr.ToString());
            }
            // точные производные: нормаль сплайна-цилиндра на краях параметров — радиальная (разностями у края — ошибка 1e-6)
            foreach (var f in src.Faces)
                if (GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface ns)
                    foreach (var (u, v) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0), (0.5, 0.0), (0.0, 0.5), (0.37, 0.61) })
                    {
                        var q = ns.PointAt(new Vector2d(u, v)); var rad = new Vector3d(q.X, q.Y, 0).Normalized();
                        double dev = Math.Min((ns.NormalAt(new Vector2d(u, v)) - rad).Length, (ns.NormalAt(new Vector2d(u, v)) + rad).Length);
                        if (dev > 1e-10) fails.Add($"NURBS normal at ({u}, {v}) off the cylinder normal by {dev:G3}");
                    }
            var cases = NurbsBooleanCases();
            foreach (var (name, tool, want0) in cases)
            {
                var bl = new List<TSolid> { TopologyClone.Deep(src) }; var w = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BooleanOp.Subtract(bl, tool(), w); sw.Stop();
                double want = want0;
                if (double.IsNaN(want)) { var rl = new List<TSolid> { Analytic() }; BooleanOp.Subtract(rl, tool(), new List<string>()); want = rl.Sum(b => AnalyticVolume.Of(b) ?? double.NaN); }
                var b = bl.FirstOrDefault(); double? v = bl.Count == 1 ? AnalyticVolume.Of(bl[0]) : null;
                bool ok = bl.Count == 1 && b!.Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic && SolidValidator.Validate(b).IsValid && v is double vv && Math.Abs(vv - want) <= 1e-8 * V0;
                Log(new { test = "KRN07-NURBS", name, volume = v, want, guarantee = b?.Guarantee.ToString(), ms = sw.ElapsedMilliseconds, nurbsFaces = b?.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface) });
                if (Environment.GetEnvironmentVariable("NURBSDBG") == "1" && b != null)
                {
                    var ws = new List<string>(); var swr = new StringWriter();
                    bool wr = Parametric3D.Kernel.Export.StepExact.Write(swr, new[] { b }, "n", ws);
                    var back = wr ? Parametric3D.Kernel.Export.StepImport.ReadExactSolids(swr.ToString(), ws) : new List<TSolid>();
                    File.WriteAllText($"/home/claude/out/nurbs_case_{name.Split(' ')[0]}_{name.Length}.step", swr.ToString());
                    Console.WriteLine($"   STEP {name}: written {wr} back {back.Count} V {(back.Count == 1 ? AnalyticVolume.Of(back[0]) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(back[0])) : double.NaN):R} | {string.Join(" | ", ws.Take(3))}");
                }
                if (!ok) fails.Add($"{name}: bodies {bl.Count} {b?.Guarantee} V {v:R} want {want:R} {(b == null ? "" : SolidValidator.Validate(b).Summary())} | {string.Join(" | ", w.Take(2))}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
