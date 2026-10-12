using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-04 / ворота g.05: ПЕРЕСЕЧЕНИЕ ГРАНЕЙ ПО СОБЫТИЯМ, а не по отсчётам. Узкая допустимая область (ребро шириной w) поперёк
/// кривой пересечения: при отсчётах с шагом 0.05 мм участок короче шага пропускается.
/// </summary>
static partial class Program
{
    /// <summary>Плита 40×40×5 с ребром шириной w (y ∈ [20, 20 + w], z ∈ [5, 10]) — выдавливание профиля в плоскости YZ.</summary>
    static TSolid RibPlate(double w)
    {
        var f = new Frame3(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitZ);
        var pts = new[] { (0.0, 0.0), (40, 0), (40, 5), (20 + w, 5), (20 + w, 10), (20, 10), (20, 5), (0, 5) }.Select(p => f.ToWorld(p.Item1, p.Item2)).ToArray();
        var prof = ProfileBuilder.Build(f, Enumerable.Range(0, pts.Length).Select(i => (ICurve3)new LineCurve3(pts[i], pts[(i + 1) % pts.Length])).ToList());
        return ExtrudeOp.Build(prof, new Vector3d(40, 0, 0));
    }

    /// <summary>Объём, снимаемый шаром (центр c, радиус r) с плиты RibPlate(w) — численно (Симпсон по y и z, точная хорда по x).</summary>
    static double RibSphereRemoved(double w, Vector3d c, double r)
    {
        double Chord(double y, double z) { double q = r * r - (y - c.Y) * (y - c.Y) - (z - c.Z) * (z - c.Z); return q > 0 ? 2 * Math.Sqrt(q) : 0; }
        double Simpson(Func<double, double> g, double a, double b, int n) { if (b <= a) return 0; double h = (b - a) / n, s = g(a) + g(b); for (int i = 1; i < n; i++) s += g(a + i * h) * (i % 2 == 1 ? 4 : 2); return s * h / 3; }
        // плита z ∈ [0, 5]: шапка шара ниже z = 5
        double hcap = Math.Max(0, 5 - (c.Z - r));
        double plate = Math.PI * hcap * hcap * (3 * r - hcap) / 3;
        double zlo = 5, zhi = Math.Min(10, c.Z + r);
        double rib = Simpson(y => Simpson(z => Chord(y, z), zlo, zhi, 2000), 20, 20 + w, 200);
        return plate + rib;
    }

    static void Krn04Probe()
    {
        foreach (var w in new[] { 1.0, 0.1, 0.03, 0.01 })
            for (int k = 0; k < 4; k++)
            {
                double cy = 20 + w / 2 + 3.3 + 0.0123 * k;   // кривая пересекает ребро поперёк, на разных фазах отсчёта
                var c = new Vector3d(20.0 + 0.0071 * k, cy, 10.5);
                var r = BooleanOp.Evaluate(new List<TSolid> { RibPlate(w) }, PrimitiveOp.Sphere(c, 6), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                double expected = 40 * 40 * 5 + 40 * w * 5 - RibSphereRemoved(w, c, 6);
                double v = r.Outcome == OperationOutcome.Success ? r.Bodies.Sum(b => OpVolume(b)) : double.NaN;
                foreach (var b in r.Bodies) foreach (var iss in SolidValidator.Validate(b).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4)) Console.WriteLine("   " + iss);
                Console.WriteLine($"w={w} k={k}: {r.Outcome} {r.Guarantee} bodies {r.Bodies.Count} vol {v:R} expected {expected:R} diff {v - expected:G3} valid {string.Join(",", r.Bodies.Select(b => SolidValidator.Validate(b).Summary()))} :: {string.Join(" | ", r.Diagnostics.Take(3))}");
            }
    }

    /// <summary>
    /// Каталог приёмки KRN-04: узкие допустимые интервалы (ребро 0.002–1 мм на разных фазах), касание кривой с ребром и почти
    /// касание (±1e-3, ±1e-6 мм), кривая через вершину грани (тройная вершина) и рядом, переход через шов цилиндра, петля
    /// внутри грани (без событий). Независимый судья — OpenCascade (tools/occt_diff.py по той же выгрузке, что разностное
    /// сравнение): верно построено или честно отказано.
    /// </summary>
    internal static List<(string Name, Func<TSolid> A, Func<TSolid> B, BooleanOp.BooleanKind Kind)> Krn04Catalogue()
    {
        var L = new List<(string, Func<TSolid>, Func<TSolid>, BooleanOp.BooleanKind)>();
        var cut = BooleanOp.BooleanKind.Subtract; var fuse = BooleanOp.BooleanKind.Unite; var common = BooleanOp.BooleanKind.Intersect;
        foreach (var w in new[] { 1.0, 0.1, 0.03, 0.01, 0.002 })
            for (int k = 0; k < 4; k++)
            {
                double ww = w; int kk = k;
                var c = new Vector3d(20.0 + 0.0071 * kk, 20 + ww / 2 + 3.3 + 0.0123 * kk, 10.5);
                L.Add(($"narrow rib {ww} × sphere, phase {kk}", () => RibPlate(ww), () => PrimitiveOp.Sphere(c, 6), cut));
            }
        foreach (var w in new[] { 0.1, 0.03, 0.01 })
            for (int k = 0; k < 3; k++)
            {
                double ww = w; int kk = k; double cy = 20 + ww / 2 + 2.1 + 0.0137 * kk;
                L.Add(($"narrow rib {ww} × tilted cylinder, phase {kk}", () => RibPlate(ww),
                    () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(20, cy, -2), 4, 24), TransformOp.Rotate(new Vector3d(20, cy, 10), new Vector3d(1, 0.3, 0), 20)), cut));
            }
        // касание: окружность сферы в верхней грани касается ребра y = 40 (и боковая окружность — верхнего ребра)
        double R = 8, h = 3, rc = Math.Sqrt(R * R - h * h);
        foreach (var d in new[] { 0.0, 1e-3, -1e-3, 1e-6, -1e-6 })
        {
            double dd = d;
            L.Add(($"tangent to edge, offset {dd:G2}", () => Box(0, 0, 0, 40, 40, 10), () => PrimitiveOp.Sphere(new Vector3d(20, 40 - rc + dd, 10 - h), R), cut));
            L.Add(($"tangent to edge (common), offset {dd:G2}", () => Box(0, 0, 0, 40, 40, 10), () => PrimitiveOp.Sphere(new Vector3d(20, 40 - rc + dd, 10 - h), R), common));
        }
        // через вершину (40, 40, 10): |(10, 10, 5)| = 15
        foreach (var d in new[] { 0.0, 1e-6, -1e-6, 1e-3 })
        {
            double dd = d;
            L.Add(($"through vertex, radius {15 + dd:R}", () => Box(0, 0, 0, 40, 40, 10), () => PrimitiveOp.Sphere(new Vector3d(30, 30, 5), 15 + dd), cut));
            L.Add(($"through vertex (fuse), radius {15 + dd:R}", () => Box(0, 0, 0, 40, 40, 10), () => PrimitiveOp.Sphere(new Vector3d(30, 30, 5), 15 + dd), fuse));
        }
        // шов цилиндра: наклонная плоскость и шар на шве
        foreach (var ang in new[] { 25.0, 60.0 })
        {
            double a = ang;
            L.Add(($"cylinder seam × tilted box {a}°", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30),
                () => TransformOp.Copy(Box(-20, -20, 15, 40, 40, 30), TransformOp.Rotate(new Vector3d(0, 0, 15), Vector3d.UnitY, a)), cut));
        }
        foreach (var kind in new[] { cut, fuse, common })
        {
            var kd = kind;
            L.Add(($"cylinder seam × sphere on the seam ({kd})", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), () => PrimitiveOp.Sphere(new Vector3d(10, 0, 15), 3), kd));
            L.Add(($"cylinder seam × sphere on the seam, rotated ({kd})", () => TransformOp.Copy(PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), TransformOp.Rotate(Vector3d.Zero, new Vector3d(0.2, 0.1, 1), 11)), () => PrimitiveOp.Sphere(new Vector3d(9.5, 2, 15), 3), kd));
        }
        // шар со смещённым центром на стенке цилиндра, по кругу (оба полюса карты шара — внутри грани результата)
        foreach (var deg in new[] { 0, 45, 100, 200, 290 })
            foreach (var kind in new[] { cut, fuse, common })
            {
                double a = deg * Math.PI / 180; var kd = kind;
                var sc = new Vector3d(9.5 * Math.Cos(a) - 2 * Math.Sin(a), 9.5 * Math.Sin(a) + 2 * Math.Cos(a), 15);
                L.Add(($"sphere off-centre on cylinder wall {deg}° ({kd})", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), () => PrimitiveOp.Sphere(sc, 3), kd));
            }
        // две ветви пересечения цилиндров (малый насквозь через большой) — в обоих порядках операндов
        foreach (var kind in new[] { cut, fuse, common })
        {
            var kd = kind;
            TSolid Small() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0.7, -20), 3, 40), TransformOp.Rotate(new Vector3d(0, 0.7, 0), Vector3d.UnitY, 90));
            TSolid Big() => PrimitiveOp.Cylinder(new Vector3d(0, 0, -15), 10, 30);
            L.Add(($"small cylinder through big (big first, {kd})", Big, Small, kd));
            L.Add(($"small cylinder through big (small first, {kd})", Small, Big, kd));
        }
        // малые петли: шар едва задевает стенку цилиндра (глубина 0.1 … 1e-4 мм), конус — тоже
        foreach (var depth in new[] { 0.1, 1e-2, 1e-3, 1e-4 })
            foreach (var kind in new[] { cut, common })
            {
                double dp = depth; var kd = kind;
                L.Add(($"small loop: sphere grazes cylinder, depth {dp:G2} ({kd})", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), () => PrimitiveOp.Sphere(new Vector3d(13 - dp, 1.3, 15), 3), kd));
                L.Add(($"small loop: cylinder grazes sphere, depth {dp:G2} ({kd})", () => PrimitiveOp.Sphere(new Vector3d(0, 0, 0), 10), () => PrimitiveOp.Cylinder(new Vector3d(13 - dp, 0.4, -5), 3, 10), kd));
            }
        // конус × шар, конус × конус — по образующим конуса
        foreach (var kind in new[] { cut, common, fuse })
        {
            var kd = kind;
            L.Add(($"cone × sphere through the side ({kd})", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 0, 10), () => PrimitiveOp.Sphere(new Vector3d(4, 0.5, 4), 2.5), kd));
            L.Add(($"cone × sphere around the apex ({kd})", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 1, 10), () => PrimitiveOp.Sphere(new Vector3d(0.3, 0.2, 9), 2), kd));
            L.Add(($"cone × sphere, small loop ({kd})", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 0, 10), () => PrimitiveOp.Sphere(new Vector3d(4.1, 0, 6.2), 1.6), kd));
            L.Add(($"truncated cone × sphere ({kd})", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 1, 10), () => PrimitiveOp.Sphere(new Vector3d(4, 0.5, 4), 2.5), kd));
            L.Add(($"cone × tilted cone ({kd})", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 0, 10), () => TransformOp.Copy(PrimitiveOp.Cone(new Vector3d(0, 0, -2), 3, 1, 14), TransformOp.Rotate(new Vector3d(0, 0, 5), new Vector3d(1, 0.2, 0), 70)), kd));
        }
        // петли внутри граней (событий нет)
        L.Add(("loop inside plane face", () => Box(0, 0, 0, 40, 40, 10), () => PrimitiveOp.Sphere(new Vector3d(20, 20, 12), 3), cut));
        L.Add(("loop inside cylinder face", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), () => PrimitiveOp.Sphere(new Vector3d(0, 10, 15), 2), cut));
        L.Add(("loop inside cylinder face (fuse)", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), () => PrimitiveOp.Sphere(new Vector3d(0, 10, 15), 2), fuse));
        L.Add(("two loops: cylinder through sphere", () => PrimitiveOp.Sphere(new Vector3d(0, 0, 15), 8), () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -5), 3, 40), TransformOp.Rotate(new Vector3d(0, 0, 15), Vector3d.UnitX, 30)), cut));
        return L;
    }

    /// <summary>Выгрузка каталога KRN-04 в формате разностного сравнения (tools/occt_diff.py).</summary>
    static void Krn04Dump(string dir)
    {
        Directory.CreateDirectory(dir);
        var man = new List<string> { "id\tops\tkinds\toutcome\tguarantee\tbodies\tvolAnalytic\tvolMesh\tarea\tchord\tdetail\topVolumes\tbound\tvalid" };
        int i = 0;
        foreach (var (name, A, B, kind) in Krn04Catalogue())
        {
            string id = $"k4_{i++:000}";
            string op = kind switch { BooleanOp.BooleanKind.Subtract => "cut", BooleanOp.BooleanKind.Unite => "fuse", _ => "common" };
            try
            {
                var a = A(); var b = B();
                var r = BooleanOp.Evaluate(new List<TSolid> { a }, b, kind, OperationPolicy.Strict);
                var operands = new[] { a, b };
                for (int j = 0; j < 2; j++)
                {
                    var sw = new StringWriter(); var w = new List<string>();
                    if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { operands[j] }, $"{id}_{j}", w)) throw new Exception("operand not exportable: " + string.Join("; ", w));
                    File.WriteAllText(Path.Combine(dir, $"{id}_op{j}.step"), sw.ToString());
                }
                double va = double.NaN, vm = double.NaN, area = 0, bound = 0;
                if (r.Outcome == OperationOutcome.Success)
                {
                    va = 0; vm = 0;
                    foreach (var bb in r.Bodies)
                    {
                        // объём, который сообщает деталь: интеграл по источникам граней (у многогранного — если согласен с
                        // многогранником), иначе — многогранник; граница — его же (AnalyticMeasure.Volume)
                        va += AnalyticVolume.Of(bb) ?? OpVolume(bb);
                        vm += Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bb));
                        area += AnalyticVolume.AreaOf(bb) ?? double.NaN;
                        bound += AnalyticMeasure.Volume(bb).Error;
                    }
                    if (r.Guarantee != GuaranteeLevel.ExactAnalytic) vm = r.Bodies.Sum(bb => OpVolume(bb));   // многогранник — точно
                }
                else if (r.Outcome == OperationOutcome.Empty) { va = 0; vm = 0; }
                bool valid = r.Bodies.All(bb => SolidValidator.Validate(bb).IsValid);
                string detail = (r.Outcome == OperationOutcome.Success ? (valid ? "" : "INVALID ") : r.Describe().Replace('\t', ' ')) + name;
                man.Add($"{id}\t{op}\t{name}\t{r.Outcome}\t{(r.Outcome == OperationOutcome.Success ? r.Guarantee.ToString() : "")}\t{r.Bodies.Count}\t{va:R}\t{vm:R}\t{area:R}\t{Parametric3D.Core.Tol.Chord:R}\t{detail}\t{string.Join(",", operands.Select(o => OpVolume(o).ToString("R")))}\t{bound:R}\t{(r.Outcome == OperationOutcome.Success ? (valid ? "ok" : "INVALID " + string.Join(" ", r.Bodies.Select(bb => SolidValidator.Validate(bb)).Where(v => !v.IsValid).Select(v => v.Summary()))) : "")}");
                Console.WriteLine($"{id} {name}: {r.Outcome} {r.Guarantee} valid {valid} vol {va:R}");
            }
            catch (Exception ex) { man.Add($"{id}\t{op}\t{name}\texception\t\t0\tNaN\tNaN\t0\t0\t{ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}"); Console.WriteLine($"{id} {name}: EXCEPTION {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), man);
    }
}
static partial class Program
{
    static void Krn04One(int idx)
    {
        var (name, A, B, kind) = Krn04Catalogue()[idx];
        BooleanOp.AnalyticRouteDebug = s => Console.WriteLine("  route: " + s);
        if (Environment.GetEnvironmentVariable("K4DBG") == "1")
        {
            Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = (m, p) => Console.WriteLine($"  pt {m} ({p.X:0.######}, {p.Y:0.######}, {p.Z:0.######})");
            Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("  split: " + m);
        }
        var r = BooleanOp.Evaluate(new List<TSolid> { A() }, B(), kind, OperationPolicy.Strict);
        Console.WriteLine($"{name}: {r.Describe()}");
        foreach (var b in r.Bodies) foreach (var iss in SolidValidator.Validate(b).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(6)) Console.WriteLine("   " + iss);
    }
}
static partial class Program
{
    static void Krn04Ring()
    {
        foreach (var kind in new[] { BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
        {
            var sb = new System.Text.StringBuilder();
            for (int deg = 0; deg < 360; deg += 15)
            {
                double a = deg * Math.PI / 180;
                var r = BooleanOp.Evaluate(new List<TSolid> { PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30) }, PrimitiveOp.Sphere(new Vector3d(9.5 * Math.Cos(a) - 2 * Math.Sin(a), 9.5 * Math.Sin(a) + 2 * Math.Cos(a), 15), 3), kind, OperationPolicy.Strict);
                sb.Append($"{deg}:{(r.Guarantee == GuaranteeLevel.ExactAnalytic ? "E" : "p")} ");
            }
            Console.WriteLine($"{kind}: {sb}");
        }
    }
}
static partial class Program
{
    static void Krn04Raw(int idx = -1)
    {
        var a = PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30); var b = PrimitiveOp.Sphere(new Vector3d(9.5, 2, 15), 3);
        var op = Parametric3D.Kernel.Analytic.Bool2.Intersect;
        if (idx >= 0) { var cc = Krn04Catalogue()[idx]; a = cc.A(); b = cc.B(); op = cc.Kind switch { BooleanOp.BooleanKind.Subtract => Parametric3D.Kernel.Analytic.Bool2.Subtract, BooleanOp.BooleanKind.Unite => Parametric3D.Kernel.Analytic.Bool2.Union, _ => Parametric3D.Kernel.Analytic.Bool2.Intersect }; }
        var r = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(a, b, op, out var why);
        Console.WriteLine($"raw: {(r == null ? "null " + why : "faces " + r.Faces.Count())}");
        if (r == null) return;
        Console.WriteLine($"conv {r.TopologyConvention} vol {AnalyticVolume.Of(r)}");
        foreach (var f in r.Faces)
        {
            var p = f.InteriorHint ?? Vector3d.Zero; var n = f.NormalAt(p);
            // векторная площадь по контуру
            var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0);
            var va = Vector3d.Zero; for (int i = 0; i + 1 < pts.Count; i++) va += Vector3d.Cross(pts[i], pts[i + 1]) / 2;
            Console.WriteLine($" face {f.Tag} rev {f.Reversed} surf {f.Surface.GetType().Name} hint {p} n {n} loopVA {va} dot {Vector3d.Dot(n, va):G4}");
        }
        TopologyConventions.AnchorDebug = m => Console.WriteLine("  anchor: " + m);
        var c = TopologyClone.Deep(r); Console.WriteLine($"clone hints {string.Join(",", c.Faces.Select(f => f.InteriorHint.HasValue))}"); TopologyConventions.Normalize(c);
        Console.WriteLine($"normalized vol {AnalyticVolume.Of(c)}");
        foreach (var f in c.Faces) Console.WriteLine($"  integral {f.Tag}: {AnalyticVolume.FaceIntegral(f)}");
        { var c2 = TopologyClone.Deep(c); Parametric3D.Kernel.Export.StepImport.MeshFacesFromLoops(c2, null, "probe");
          foreach (var f in c2.Faces) if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs) { var t0 = fs.Facets.ReadView[0]; var cen = (t0.Item1 + t0.Item2 + t0.Item3) / 3; Console.WriteLine($"   meshed {f.Tag} rev {f.Reversed} centroid {cen} facetN {Vector3d.Cross(t0.Item2 - t0.Item1, t0.Item3 - t0.Item1).Normalized()} srcN {fs.Source!.NormalAt(fs.Source.ProjectToUV(cen))}"); } }
        { var c3 = TopologyClone.Deep(c); Parametric3D.Kernel.Export.StepImport.MeshFacesFromLoops(c3, null, "probe");
          static (long, long, long) K(Vector3d p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));
          var fl = c3.Faces.ToList(); var sets = fl.Select(f => { var d = new Dictionary<((long, long, long), (long, long, long)), int>(); foreach (var t in ((Parametric3D.Kernel.Surfaces.FacetedSurface)f.Surface).Facets) foreach (var (a, b) in new[] { (t.Item1, t.Item2), (t.Item2, t.Item3), (t.Item3, t.Item1) }) d[(K(a), K(b))] = 1; return d; }).ToList();
          int same = sets[0].Keys.Count(k => sets[1].ContainsKey(k)), opp = sets[0].Keys.Count(k => sets[1].ContainsKey((k.Item2, k.Item1)));
          Console.WriteLine($"   shared sides: same {same} opposite {opp}"); }
        Parametric3D.Kernel.Analytic.PrismBuild.Finish(c, null);
        foreach (var f in c.Faces)
        {
            var va2 = Vector3d.Zero; double ar2 = 0; Vector3d nf0 = default; if (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs) { foreach (var (x, y, z) in fs.Facets.ReadView) { va2 += Vector3d.Cross(y - x, z - x) / 2; ar2 += Vector3d.Cross(y - x, z - x).Length / 2; } var t0 = fs.Facets.ReadView[0]; nf0 = Vector3d.Cross(t0.Item2 - t0.Item1, t0.Item3 - t0.Item1).Normalized(); Console.WriteLine($"   facets {fs.Facets.Count} area {ar2:G6} first centroid {(t0.Item1 + t0.Item2 + t0.Item3) / 3} n {nf0}"); }
            var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0);
            var va = Vector3d.Zero; for (int i = 0; i + 1 < pts.Count; i++) va += Vector3d.Cross(pts[i], pts[i + 1]) / 2;
            Console.WriteLine($"  finished {f.Tag} rev {f.Reversed} facetsVA {va2} loopVA {va} integral {AnalyticVolume.FaceIntegral(f)}");
        }
        Console.WriteLine($"  finished vol {AnalyticVolume.Of(c)}");
        { var rnd = new Random(5); int inside = 0, N = 2000000; var sc = new Vector3d(9.5, 2, 15);
          for (int i = 0; i < N; i++) { var q = sc + new Vector3d(rnd.NextDouble() * 6 - 3, rnd.NextDouble() * 6 - 3, rnd.NextDouble() * 6 - 3); if ((q - sc).Length <= 3 && q.X * q.X + q.Y * q.Y <= 100) inside++; }
          Console.WriteLine($"  MC lens {216.0 * inside / N:G6} ± {216.0 * Math.Sqrt(inside) / N * 3:G2}"); }
        foreach (var f in c.Faces)
        {
            var p = f.InteriorHint ?? Vector3d.Zero; var n = f.NormalAt(p);
            var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0);
            var va = Vector3d.Zero; for (int i = 0; i + 1 < pts.Count; i++) va += Vector3d.Cross(pts[i], pts[i + 1]) / 2;
            Console.WriteLine($" face {f.Tag} rev {f.Reversed} n {n} loopVA {va} dot {Vector3d.Dot(n, va):G4}");
        }
    }
}
static partial class Program
{
    /// <summary>Объём шара (центр c, радиус R) внутри цилиндра ρ ≤ Rc (ось z): по z — площадь пересечения двух кругов (замкнуто), Симпсон.</summary>
    static double SphereInCylinder(Vector3d c, double R, double Rc)
    {
        double d = Math.Sqrt(c.X * c.X + c.Y * c.Y);
        double Lens(double r1)
        {
            double r2 = Rc;
            if (r1 <= 0) return 0;
            if (d >= r1 + r2) return 0;
            if (d <= Math.Abs(r1 - r2)) return Math.PI * Math.Min(r1, r2) * Math.Min(r1, r2);
            double a1 = Math.Acos(Math.Clamp((d * d + r1 * r1 - r2 * r2) / (2 * d * r1), -1, 1));
            double a2 = Math.Acos(Math.Clamp((d * d + r2 * r2 - r1 * r1) / (2 * d * r2), -1, 1));
            return r1 * r1 * (a1 - Math.Sin(2 * a1) / 2) + r2 * r2 * (a2 - Math.Sin(2 * a2) / 2);
        }
        // подстановка z = c.Z + R·sin φ сглаживает корень у полюсов
        int n = 20000; double h = Math.PI / n, s = 0;
        for (int i = 0; i <= n; i++)
        {
            double ph = -Math.PI / 2 + i * h; double r1 = R * Math.Cos(ph);
            double g = Lens(r1) * R * Math.Cos(ph);
            s += g * (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2);
        }
        return s * h / 3;
    }

    static void Krn04Checks()
    {
        Check("KRN05-SEW-IDENTIFIES-SAME-ONLY", () =>
        {
            // 05.6: сшивка после сборки только ОТОЖДЕСТВЛЯЕТ совпадающее: парные открытые рёбра с общими концами сливаются,
            // если это доказанно та же кривая (до точности модели), а не «середины ближе 1e-6». Отрезок и дуга через те же
            // концы со стрелой 5e-7 мм (больше точности модели) — не сливаются (прежде сливались); тот же эллипс с другой
            // начальной точкой и та же дуга — сливаются. Мутация P3D_MERGEGUARD=0.
            var fails = new List<string>();
            var A = new Vector3d(0, 0, 0); var B = new Vector3d(10, 0, 0); double sag = 5e-7;
            double R = (25 + sag * sag) / (2 * sag); var C = new Vector3d(5, sag - R, 0);
            double a0 = Math.Atan2(A.Y - C.Y, A.X - C.X), a1 = Math.Atan2(B.Y - C.Y, B.X - C.X);
            var arc = new ArcCurve3(new Frame3(C, Vector3d.UnitX, Vector3d.UnitY), R, a0, a1);
            var line = new LineCurve3(A, B);
            bool m1 = Parametric3D.Kernel.Analytic.GeneralBoolean.SewMergeable(line, arc);
            var f1 = new Frame3(new Vector3d(1, 2, 3), Vector3d.UnitX, Vector3d.UnitY);
            var e1 = new EllipseCurve3(f1, 6, 4, 0, Math.PI); var e2 = new EllipseCurve3(new Frame3(f1.Origin, -Vector3d.UnitX, -Vector3d.UnitY), 6, 4, -Math.PI, 0);
            bool m2 = Parametric3D.Kernel.Analytic.GeneralBoolean.SewMergeable(e1, e2);
            var c1 = new ArcCurve3(f1, 5, 0.3, 2.1); var c2 = new ArcCurve3(f1, 5, 0.3, 2.1);
            bool m3 = Parametric3D.Kernel.Analytic.GeneralBoolean.SewMergeable(c1, c2);
            Log(new { test = "KRN05-SEW", lineVsArc5e7 = m1, sameEllipseShifted = m2, sameArc = m3, sagitta = (line.PointAt(0.5) - arc.PointAt(0.5)).Length });
            if (m1) fails.Add("a segment and an arc 5e-7 apart are merged");
            if (!m2) fails.Add("the same half-ellipse with another frame is not merged");
            if (!m3) fails.Add("the same arc is not merged");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN04-COMMON-VERTICES", () =>
        {
            // 04.4/04.6: ОБЩИЕ ВЕРШИНЫ И ТОЧКИ РАЗРЕЗА — одно событие до точности модели (1e-7 мм). Вершины результата, узлы
            // графа разреза и точки разреза рёбер сводятся по расстоянию лишь до округления одного события; две РАЗНЫЕ точки
            // ближе допуска слияния (2e-5), но дальше точности модели — отказ маршрута, а не молчаливое слияние; часть кривой
            // внутри обеих граней уже допуска — тоже отказ (прежде терялась). Требования:
            //   угол бруска, срезанный плоскостью x + y + z = 30 − s: s = 1e-3 — ExactAnalytic, объём 1000 − s³/6 до 1e-12;
            //   s = 2e-6 — не ExactAnalytic (прежде: ExactAnalytic с целым углом, вершина вне точного тела на 1.2e-6 мм);
            //   на всех операциях проверки сведённые по расстоянию точки не дальше точности модели.
            // Мутация P3D_MERGEGUARD=0.
            var fails = new List<string>();
            double maxMerge = 0; Parametric3D.Kernel.Analytic.GeneralBoolean.MergeDebug = (x, y) => maxMerge = Math.Max(maxMerge, (x - y).Length);
            long far0 = Parametric3D.Kernel.Analytic.GeneralBoolean.VertexMergedFar + Parametric3D.Kernel.Analytic.FaceSplit.NodeMergedFar + Parametric3D.Kernel.Analytic.FaceSplit.CutFar;
            try
            {
                TSolid? Corner(double sc, out GuaranteeLevel g, out List<string> w)
                {
                    var bl = new List<TSolid> { Box(0, 0, 0, 10, 10, 10) }; w = new List<string>();
                    var n = new Vector3d(1, 1, 1).Normalized();
                    var axis = Vector3d.Cross(Vector3d.UnitZ, n).Normalized(); double ang = Math.Acos(Vector3d.Dot(Vector3d.UnitZ, n)) * 180 / Math.PI;
                    var tool = TransformOp.Copy(TransformOp.Copy(Box(-50, -50, 0, 100, 100, 50), TransformOp.Rotate(Vector3d.Zero, axis, ang)), TransformOp.Move(n * ((30 - sc) / Math.Sqrt(3))));
                    BooleanOp.Subtract(bl, tool, w);
                    g = bl.Count == 1 ? bl[0].Guarantee : GuaranteeLevel.ExactPolyhedral;
                    return bl.Count == 1 ? bl[0] : null;
                }
                var b1 = Corner(1e-3, out var g1, out var w1);
                double? v1 = b1 == null ? null : AnalyticVolume.Of(b1); double want1 = 1000 - 1e-9 / 6;
                bool ok1 = g1 == GuaranteeLevel.ExactAnalytic && v1 is double vv && Math.Abs(vv - want1) <= 1e-12 * 1000 && SolidValidator.Validate(b1!).IsValid && b1!.Faces.Count() == 7;
                Log(new { test = "KRN04-COMMON-VERTICES", corner = 1e-3, guarantee = g1.ToString(), volume = v1, want = want1, ok = ok1 });
                if (!ok1) fails.Add($"corner 1e-3: {g1} V {v1} want {want1} | {string.Join(" | ", w1.Take(2))}");
                var b2 = Corner(2e-6, out var g2, out var w2);
                Log(new { test = "KRN04-COMMON-VERTICES", corner = 2e-6, guarantee = g2.ToString() });
                if (g2 == GuaranteeLevel.ExactAnalytic) fails.Add($"corner 2e-6: accepted as ExactAnalytic with {b2?.Faces.Count()} faces (the 2e-6 corner facet is below the merge tolerance)");
                // обычные операции: слияния — только округление
                var cyl = new List<TSolid> { PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 8, 20) };
                BooleanOp.Subtract(cyl, TransformOp.Copy(Box(-3, -20, 5, 6, 40, 6), TransformOp.Rotate(new Vector3d(0, 0, 8), Vector3d.UnitX, 25)), new List<string>());
                BooleanOp.Unite(cyl, PrimitiveOp.Sphere(new Vector3d(0, 0, 20), 5), new List<string>());
                long far = Parametric3D.Kernel.Analytic.GeneralBoolean.VertexMergedFar + Parametric3D.Kernel.Analytic.FaceSplit.NodeMergedFar + Parametric3D.Kernel.Analytic.FaceSplit.CutFar - far0;
                Log(new { test = "KRN04-COMMON-VERTICES", maxMergeDistance = maxMerge, farMerges = far, chain = cyl[0].Guarantee.ToString() });
                if (maxMerge > ToleranceContext.Current.ModelAccuracy) fails.Add($"points merged {maxMerge:G3} mm apart (model accuracy {ToleranceContext.Current.ModelAccuracy:G3})");
                if (cyl.Count != 1 || cyl[0].Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"cylinder chain: {string.Join(",", cyl.Select(b => b.Guarantee))}");
            }
            finally { Parametric3D.Kernel.Analytic.GeneralBoolean.MergeDebug = null; }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN04-EVENTS-RIGOROUS-ALL", () =>
        {
            // 04.2: строгие события для рёбер и поверхностей без алгебраического решения (RigorousEvents, Кравчик по кускам
            // Безье). Пары близких корней (почти касание, расстояние между корнями 1e-4…3e-3 мм — меньше шага выборки):
            //   сплайн (рациональная окружность R10) × плоскость на расстоянии 10 − δ от центра (нормаль (1, 0.03)) — 2 корня;
            //   отрезок × грань NURBS (четверть цилиндра R10) на расстоянии 10 − δ от оси — 2 корня;
            //   ребро NURBS × неявная (окружность R12 = тор NURBS ∩ плоскость z = 0) × плоскость x = 12 − δ — 2 корня;
            //   ребро NURBS × NURBS (та же окружность: тор NURBS ∩ плоскость NURBS) × плоскость x = 12 − δ — 2 корня.
            // Требование: ровно 2 события на месте (до 1e-9 мм). Прежде (выборка 512…8192 и 1024 отсчёта): 1 или 0 событий.
            // Мутация P3D_RIGOREVENTS=0.
            var fails = new List<string>();
            double s2 = Math.Sqrt(0.5);
            void Expect(string name, List<Vector3d> got, List<Vector3d> want)
            {
                bool ok = got.Count == want.Count && want.All(w => got.Any(g => (g - w).Length <= 1e-9));
                Log(new { test = "KRN04-EVENTS-RIGOROUS", name, got = got.Count, want = want.Count, ok });
                if (!ok) fails.Add($"{name}: {got.Count} event(s) [{string.Join(" ", got.Select(p => p.ToString("0.#########")))}], want {want.Count} [{string.Join(" ", want.Select(p => p.ToString("0.#########")))}]");
            }
            // сплайн: полная окружность R10 в z = 0 (рациональная квадратичная, 9 точек)
            var cx = new[] { 1.0, 1, 0, -1, -1, -1, 0, 1, 1 }; var cy = new[] { 0.0, 1, 1, 1, 0, -1, -1, -1, 0 };
            var circ = new SplineCurve3(Enumerable.Range(0, 9).Select(i => new Vector3d(10 * cx[i], 10 * cy[i], 0)).ToList(), 2,
                new[] { 1, s2, 1, s2, 1, s2, 1, s2, 1 }, new[] { 0, 0, 0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1, 1, 1 });
            foreach (double d in new[] { 1e-6, 1e-8, 1e-10 })
            {
                var nrm = new Vector3d(1, 0.03, 0).Normalized(); var pl = new Parametric3D.Kernel.Geometry.Ssi.Plane(nrm * (10 - d), nrm);
                var ev = Parametric3D.Kernel.Analytic.SsiEvents.CurveSurface(circ, pl, out _).Select(t => circ.PointAt(t)).ToList();
                // прямая n·p = n·p0 в плоскости z = 0 и окружность R10: корни квадратного уравнения по параметру вдоль прямой
                var nn = pl.N.Normalized(); var p0 = pl.P; var dir = new Vector3d(-nn.Y, nn.X, 0);
                double bq = 2 * Vector3d.Dot(p0, dir), cq = p0.LengthSquared - 100, dsc = bq * bq - 4 * cq;
                var want = dsc < 0 ? new List<Vector3d>() : new List<Vector3d> { p0 + dir * ((-bq - Math.Sqrt(dsc)) / 2), p0 + dir * ((-bq + Math.Sqrt(dsc)) / 2) };
                Expect($"spline circle × plane δ {d}", ev, want);
            }
            // отрезок × четверть цилиндра NURBS R10 (z 0…20)
            {
                var cp = new Vector3d[3, 2]; var w = new double[3, 2];
                var xy = new[] { new Vector3d(10, 0, 0), new Vector3d(10, 10, 0), new Vector3d(0, 10, 0) }; var wc = new[] { 1, s2, 1 };
                for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++) { cp[i, j] = xy[i] + new Vector3d(0, 0, 20 * j); w[i, j] = wc[i]; }
                var S = new Parametric3D.Kernel.Surfaces.NurbsSurface(cp, 2, 1, w);
                foreach (double d in new[] { 1e-6, 1e-7 })
                {
                    // прямая x + y = (10 − δ)√2 в z = 7 (по нормали к оси на 45°)
                    double c0 = (10 - d) * Math.Sqrt(2); var dir = new Vector3d(-1, 1, 0).Normalized();
                    var mid = new Vector3d(c0 / 2, c0 / 2, 7); var L = new LineCurve3(mid - dir * 8, mid + dir * 8);
                    var got = Parametric3D.Kernel.Analytic.RigorousEvents.OnNurbs(L, S, 0, 1, 0, 1)?.Select(t => L.PointAt(t)).ToList() ?? new List<Vector3d>();
                    double h = Math.Sqrt(100 - (10 - d) * (10 - d));
                    Expect($"line × NURBS cylinder δ {d}", got, new() { mid - dir * h, mid + dir * h });
                }
            }
            // рёбра NURBS: окружность R12 (тор NURBS R10 r2 ∩ плоскость z = 0)
            {
                var T = NurbsTorus(10, 2);
                var r1 = Parametric3D.Kernel.Geometry.NurbsSsi.Intersect(T, 0, 1, 0, 1, new Parametric3D.Kernel.Geometry.Ssi.Plane(Vector3d.Zero, Vector3d.UnitZ), 13);
                var P0 = new Parametric3D.Kernel.Geometry.NurbsNurbsSsi.Rect(0, 1, 0, 1);
                var r2 = Parametric3D.Kernel.Geometry.NurbsNurbsSsi.Intersect(T, P0, NurbsPlane(Vector3d.Zero, Vector3d.UnitZ, 15), P0, 13);
                foreach (var (kind, curves) in new[] { ("NURBS × implicit", r1.Curves), ("NURBS × NURBS", r2.Curves) })
                {
                    // ветвь R12 (внешняя), куски у шва — все
                    var outer = curves.Where(cv => cv.PointAt(0.5).Xy.Length > 11).ToList();
                    foreach (double d in new[] { 1e-7 })
                    {
                        var pl = new Parametric3D.Kernel.Geometry.Ssi.Plane(new Vector3d(0, 12 - d, 0), Vector3d.UnitY);
                        var got = new List<Vector3d>();
                        foreach (var cv in outer) got.AddRange(Parametric3D.Kernel.Analytic.SsiEvents.CurveSurface(cv, pl, out _).Select(t => cv.PointAt(t)));
                        double x = Math.Sqrt(144 - (12 - d) * (12 - d));
                        Expect($"{kind} edge × plane δ {d}", got, new() { new Vector3d(x, 12 - d, 0), new Vector3d(-x, 12 - d, 0) });
                    }
                }
            }
            // цепочка на теле с гранью NURBS: срез x > 8 режет дугу-сплайн основания (остаётся часть сплайна), затем паз
            // y ∈ [3, 4] режет эту часть: её события с плоскостями паза — строго (часть сплайна — сплайн, P3D_SUBSPLINE;
            // прежде — процедурная кривая с выборкой). Эталон — те же операции над четвертью цилиндра из примитивов.
            {
                List<TSolid> Run(TSolid b0)
                {
                    var bl = new List<TSolid> { b0 };
                    BooleanOp.Subtract(bl, Box(8, -5, -5, 10, 20, 30), new List<string>());
                    BooleanOp.Subtract(bl, Box(-5, 3, -5, 20, 1, 30), new List<string>());
                    return bl;
                }
                var nb = Run(TopologyClone.Deep(NurbsQuarterCylinder()));
                var ab = new List<TSolid> { Box(0, 0, 0, 10, 10, 20) }; BooleanOp.Intersect(ab, PrimitiveOp.Cylinder(new Vector3d(0, 0, -1), 10, 22), new List<string>());
                var rb = Run(ab[0]);
                double vn = nb.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), vr = rb.Sum(b => AnalyticVolume.Of(b) ?? double.NaN);
                bool ok = nb.Count == rb.Count && nb.All(b => b.Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic && SolidValidator.Validate(b).IsValid) && Math.Abs(vn - vr) <= 1e-8 * vr;
                Log(new { test = "KRN04-EVENTS-RIGOROUS", name = "NURBS chain: sub-spline edge cut again", bodies = nb.Count, guarantee = string.Join(",", nb.Select(b => b.Guarantee)), vn, vr, ok });
                if (!ok) fails.Add($"NURBS chain: {nb.Count} bodies {string.Join(",", nb.Select(b => b.Guarantee))} V {vn} vs {vr}");
            }
            // интервалы ребра в СОВПАДАЮЩЕЙ грани (общая булева, копланарные грани): верхняя грань бруска с эллиптическим
            // отверстием (наклонный цилиндр r5 под 30°: полуоси 5/cos30° и 5) и отрезок y = 25 − δ в её плоскости — почти
            // касается эллипса сверху: внутри грани — два интервала, между ними хорда эллипса длиной 2a√(1 − (1 − δ/b)²).
            // Прежде для эллипса вспомогательной поверхности не было — выборка 512 точек (шаг 0.08 мм) хорды 0.007 мм не
            // видела. Теперь эллипс — сечение круглого цилиндра радиуса b, наклонённого на arccos(b/a) (P3D_BNDRIGOR).
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 40, 40, 10) };
                var cyl = TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(20, 20, -20), 5, 60), TransformOp.Rotate(new Vector3d(20, 20, 5), Vector3d.UnitY, 30));
                BooleanOp.Subtract(bl, cyl, new List<string>());
                var top = bl[0].Faces.FirstOrDefault(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Plane pz
                                                      && Math.Abs(Math.Abs(pz.N.Normalized().Z) - 1) < 1e-12 && Math.Abs(pz.P.Z * Math.Sign(pz.N.Z) * pz.N.Normalized().Z - 10) < 1e-9 && f.Loops.Count == 2);
                if (top == null) fails.Add("box top with the elliptic hole not found");
                else
                {
                    var el = top.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge.Curve).OfType<EllipseCurve3>().FirstOrDefault();
                    if (el == null) fails.Add("elliptic edge not found");
                    else
                    {
                        double a = Math.Max(el.RU, el.RV), b = Math.Min(el.RU, el.RV); var cen = el.Frame.Origin;
                        var minor = el.RU < el.RV ? el.Frame.U : el.Frame.V;
                        foreach (double d in new[] { 1e-4, 1e-6 })
                        {
                            // отрезок вдоль большой оси на расстоянии b − δ от центра
                            var major = el.RU >= el.RV ? el.Frame.U : el.Frame.V;
                            var p0 = cen + minor * (b - d) - major * 15; var p1 = cen + minor * (b - d) + major * 15;
                            var L = new LineCurve3(p0, p1);
                            var runs = Parametric3D.Kernel.Analytic.GeneralBoolean.EdgeRunsForTest(L, top, out var unres);
                            double half = a * Math.Sqrt(1 - (1 - d / b) * (1 - d / b));
                            double tA = (15 - half) / 30, tB = (15 + half) / 30;
                            bool ok = runs != null && unres == null && runs.Count == 2 && Math.Abs(runs[0].E - tA) < 1e-9 && Math.Abs(runs[1].S - tB) < 1e-9;
                            Log(new { test = "KRN04-EVENTS-RIGOROUS", name = $"coincident face runs across an ellipse δ {d}", runs = runs?.Select(r => $"{r.S:R}..{r.E:R}"), unres, ok });
                            if (!ok) fails.Add($"coincident runs δ {d}: {(runs == null ? "null" : string.Join(" ", runs.Select(r => $"[{r.S:G9},{r.E:G9}]")))} {unres}; want gap [{tA:G9},{tB:G9}]");
                        }
                    }
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN04-EVENTS-TRACED-RIGOROUS", () =>
        {
            // 04.2: СОБЫТИЯ НА РЕБРЕ — ПРОСЛЕЖЕННОЙ КРИВОЙ. Тор (R10, r3) минус вертикальный цилиндр r1.5 через трубку: рёбра —
            // кривые пересечения тор × цилиндр. Плоскость z = верх − δ режет верхнее ребро у его наивысших точек: точки ребра на
            // этой высоте — на торе ρ = 10 ± √(6δ − δ²) и на окружности цилиндра — ровно 4 (при δ → 0 попарно сближаются, у
            // δ 1e-8 — на ~1e-3 мм). Прежде — выборка 512 отсчётов (шаг ~0.02 мм) с уточнением локальных минимумов: при δ ≤ 1e-7
            // находилось 2 из 4. Теперь (SsiEvents.TracedEvents): звенья — в шарах единственности ветви, исключение по Липшицу,
            // монотонность F вдоль дуги по тройному произведению нормалей, иначе деление звена. Требование: 4 события на
            // верхнем ребре (на плоскости, торе и цилиндре до 1e-9, попарно различны), 0 — на нижнем. Мутация P3D_TRACEDEVENTS=0.
            var fails = new List<string>();
            var c = new Vector3d(1, 2, 3);
            var bl = new List<TSolid> { PrimitiveOp.Torus(c, 10, 3) };
            BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(c + new Vector3d(10, 0, -10), 1.5, 20), new List<string>());
            var edges = bl[0].Edges.Where(e => e.Curve is IntersectionCurve3).ToList();
            if (edges.Count != 2) fails.Add($"{edges.Count} traced edges");
            var tor = new Parametric3D.Kernel.Geometry.Ssi.Torus(c, Vector3d.UnitZ, 10, 3);
            var cyl = new Parametric3D.Kernel.Geometry.Ssi.Cylinder(c + new Vector3d(10, 0, 0), Vector3d.UnitZ, 1.5);
            foreach (double d in new[] { 1e-2, 1e-4, 1e-6, 1e-7, 1e-8 })
            {
                double z = c.Z + 3 - d;
                var pl = new Parametric3D.Kernel.Geometry.Ssi.Plane(new Vector3d(0, 0, z), Vector3d.UnitZ);
                foreach (var e in edges)
                {
                    bool top = e.V0.P.Z > c.Z || e.Curve!.PointAt(0.5).Z > c.Z;
                    var ev = Parametric3D.Kernel.Analytic.SsiEvents.CurveSurface(e.Curve!, pl, out _);
                    var pts = ev.Select(t => e.Curve!.PointAt(t)).ToList();
                    int want = top ? 4 : 0;
                    bool onAll = pts.All(p => Math.Abs(p.Z - z) <= 1e-9 && Math.Abs(Parametric3D.Kernel.Geometry.Ssi.Implicit(tor, p).F) <= 1e-9 && Math.Abs(Parametric3D.Kernel.Geometry.Ssi.Implicit(cyl, p).F) <= 1e-9);
                    bool distinct = pts.SelectMany((p, i) => pts.Skip(i + 1).Select(q => (p - q).Length)).All(x => x > 1e-6);
                    Log(new { test = "KRN04-EVENTS-TRACED", delta = d, top, events = ev.Count, onAll, distinct, uncertain = Parametric3D.Kernel.Analytic.SsiEvents.LastUncertain });
                    if (ev.Count != want || !onAll || !distinct) fails.Add($"δ {d} {(top ? "top" : "bottom")} edge: {ev.Count} events (want {want}), on surfaces {onAll}, distinct {distinct}");
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN04-EVENTS-ALGEBRAIC-ROOTS", () =>
        {
            var fails = new List<string>();
            void Expect(string name, ICurve3 e, Parametric3D.Kernel.Geometry.Ssi.Shape s, double[] want, bool coinc = false)
            {
                var got = Parametric3D.Kernel.Analytic.SsiEvents.CurveSurface(e, s, out bool co);
                if (co != coinc) { fails.Add($"{name}: coincident {co}, expected {coinc}"); return; }
                if (coinc) return;
                var g = got.Distinct().ToList(); var dd = new List<double>();
                foreach (var t in g) if (dd.Count == 0 || Math.Abs(t - dd[^1]) > 1e-9) dd.Add(t);
                if (dd.Count != want.Length) { fails.Add($"{name}: roots [{string.Join(", ", dd.Select(x => x.ToString("G10")))}], expected [{string.Join(", ", want)}]"); return; }
                for (int i = 0; i < want.Length; i++) if (Math.Abs(dd[i] - want[i]) > 1e-7) fails.Add($"{name}: root {dd[i]:R} vs {want[i]:R}");
            }
            var Sh = new Func<Vector3d, double, Parametric3D.Kernel.Geometry.Ssi.Shape>((c, r) => new Parametric3D.Kernel.Geometry.Ssi.Sphere(c, r));
            Expect("line × sphere", new LineCurve3(new Vector3d(0, 0, -10), new Vector3d(0, 0, 10)), Sh(Vector3d.Zero, 5), new[] { 0.25, 0.75 });
            Expect("line tangent to sphere", new LineCurve3(new Vector3d(5, -10, 0), new Vector3d(5, 10, 0)), Sh(Vector3d.Zero, 5), new[] { 0.5 });
            Expect("line × cylinder", new LineCurve3(new Vector3d(-10, 0, 1), new Vector3d(10, 0, 1)), new Parametric3D.Kernel.Geometry.Ssi.Cylinder(Vector3d.Zero, Vector3d.UnitZ, 3), new[] { 0.35, 0.65 });
            Expect("line × cone (one nappe)", new LineCurve3(new Vector3d(-10, 0, 5), new Vector3d(10, 0, 5)), new Parametric3D.Kernel.Geometry.Ssi.Cone(Vector3d.Zero, Vector3d.UnitZ, Math.PI / 4), new[] { 0.25, 0.75 });
            Expect("line × cone (other nappe — none)", new LineCurve3(new Vector3d(-10, 0, -5), new Vector3d(10, 0, -5)), new Parametric3D.Kernel.Geometry.Ssi.Cone(Vector3d.Zero, Vector3d.UnitZ, Math.PI / 4), new double[0]);
            var circ = new ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 10, -Math.PI, Math.PI);
            double a6 = Math.Acos(0.6);
            Expect("arc × plane", circ, new Parametric3D.Kernel.Geometry.Ssi.Plane(new Vector3d(6, 0, 0), Vector3d.UnitX), new[] { (-a6 + Math.PI) / (2 * Math.PI), (a6 + Math.PI) / (2 * Math.PI) });
            Expect("arc tangent to plane", circ, new Parametric3D.Kernel.Geometry.Ssi.Plane(new Vector3d(10, 0, 0), Vector3d.UnitX), new[] { 0.5 });
            double b = Math.Acos(1 - 4.0 / 200);   // |p − (10,0,0)| = 2 на окружности радиуса 10: 200(1 − cos θ) = 4
            Expect("arc × sphere", circ, Sh(new Vector3d(10, 0, 0), 2), new[] { (-b + Math.PI) / (2 * Math.PI), (b + Math.PI) / (2 * Math.PI) });
            var torus = new Parametric3D.Kernel.Geometry.Ssi.Torus(Vector3d.Zero, Vector3d.UnitZ, 10, 1);
            Expect("arc on torus (coincident)", new ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 11, 0, 2 * Math.PI), torus, new double[0], coinc: true);
            double x = 10.4625, z = Math.Sqrt(110.25 - x * x);
            var tor4 = new[] { Math.Atan2(-z, -x), Math.Atan2(z, -x), Math.Atan2(-z, x), Math.Atan2(z, x) }.Select(t => (t + Math.PI) / (2 * Math.PI)).OrderBy(t => t).ToArray();
            Expect("arc × torus (4 roots)", new ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), 10.5, -Math.PI, Math.PI), torus, tor4);
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN04-EVENTS-NARROW-INTERVALS", () =>
        {
            // узкая допустимая область поперёк кривой пересечения: точный результат на любой фазе; при отсчётах (P3D_EVENTS=0)
            // ребро 0.01 мм давало 3 отказа маршрута из 4 и точный результат с ошибками B-rep
            var fails = new List<string>(); int n = 0;
            foreach (var w in new[] { 0.03, 0.01, 0.002 })
                for (int k = 0; k < 4; k++)
                {
                    var c = new Vector3d(20.0 + 0.0071 * k, 20 + w / 2 + 3.3 + 0.0123 * k, 10.5);
                    var r = BooleanOp.Evaluate(new List<TSolid> { RibPlate(w) }, PrimitiveOp.Sphere(c, 6), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                    n++;
                    if (r.Outcome != OperationOutcome.Success || r.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"w {w} k {k}: {r.Outcome} {r.Guarantee}"); continue; }
                    var bad = r.Bodies.Select(b => SolidValidator.Validate(b)).FirstOrDefault(v => !v.IsValid);
                    if (bad != null) { fails.Add($"w {w} k {k}: {bad.Summary()}"); continue; }
                    double v = r.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), want = 40 * 40 * 5 + 40 * w * 5 - RibSphereRemoved(w, c, 6);
                    double err = r.Bodies.Sum(b => AnalyticMeasure.Volume(b).Error);
                    if (!(Math.Abs(v - want) <= err + 1e-5)) fails.Add($"w {w} k {k}: volume {v:R} vs {want:R} (bound {err:G3})");
                }
            Require(fails.Count == 0, $"{n} cases: " + string.Join("; ", fails.Take(5)));
        });
        Check("KRN04-EVENTS-MATCH-DENSE-SAMPLING", () =>
        {
            // события не теряют ничего, что видит густая выборка (20000 точек на кривую), и не добавляют лишнего
            var pairs = new (string, Func<TSolid>, Func<TSolid>)[] {
                ("rib 0.03 × sphere", () => RibPlate(0.03), () => PrimitiveOp.Sphere(new Vector3d(20, 23.315, 10.5), 6)),
                ("box × sphere through vertex", () => Box(0, 0, 0, 40, 40, 10), () => PrimitiveOp.Sphere(new Vector3d(30, 30, 5), 15)),
                ("cylinder × sphere off-centre", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), () => PrimitiveOp.Sphere(new Vector3d(9.5, 2, 15), 3)),
                ("cylinder × tilted box", () => PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30), () => TransformOp.Copy(Box(-20, -20, 15, 40, 40, 30), TransformOp.Rotate(new Vector3d(0, 0, 15), Vector3d.UnitY, 25))),
                ("box × tilted cylinder", () => Box(0, 0, 0, 40, 40, 10), () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(20, 20, -5), 6, 25), TransformOp.Rotate(new Vector3d(20, 20, 5), new Vector3d(1, 1, 0), 30))),
            };
            var fails = new List<string>(); int pairsN = 0, pieces = 0;
            foreach (var (name, A, B) in pairs)
            {
                TSolid N(TSolid s) { var c = TopologyClone.Deep(s); if (c.TopologyConvention < TopologyConventions.Version) TopologyConventions.Normalize(c); return c; }
                var a = N(A()); var b = N(B());
                foreach (var fa in a.Faces)
                    foreach (var fb in b.Faces)
                    {
                        var x = Parametric3D.Kernel.Analytic.AnalyticFace.Of(fa, out _); var y = Parametric3D.Kernel.Analytic.AnalyticFace.Of(fb, out _);
                        if (x == null || y == null) continue;
                        var (ev, dn) = Parametric3D.Kernel.Analytic.FaceIntersect.CompareWithDense(x, y, 20000);
                        if (ev.Kind is Parametric3D.Kernel.Analytic.FaceIntersect.Kind.Coincident or Parametric3D.Kernel.Analytic.FaceIntersect.Kind.Tangent) continue;
                        pairsN++;
                        var pe = ev.Pieces.OrderBy(p => p.P0.X).ThenBy(p => p.P0.Y).ThenBy(p => p.P0.Z).ToList();
                        var pd = dn.Pieces.OrderBy(p => p.P0.X).ThenBy(p => p.P0.Y).ThenBy(p => p.P0.Z).ToList();
                        pieces += pe.Count;
                        if (pe.Count != pd.Count) { fails.Add($"{name} {fa.Tag}×{fb.Tag}: events {pe.Count} pieces, dense {pd.Count}"); continue; }
                        double Len(Parametric3D.Kernel.Analytic.FaceIntersect.Piece p) { double l = 0; var q0 = p.Curve.PointAt(p.T0); for (int i = 1; i <= 200; i++) { double t = p.T0 + (p.T1 - p.T0) * i / 200; var q = p.Curve.PointAt(t > p.Curve.T1 ? t - (p.Curve.T1 - p.Curve.T0) : t); l += (q - q0).Length; q0 = q; } return l; }
                        double le = pe.Sum(Len), ld = pd.Sum(Len);
                        if (Math.Abs(le - ld) > 1e-5 * Math.Max(1, ld)) fails.Add($"{name} {fa.Tag}×{fb.Tag}: length {le:G9} vs dense {ld:G9}");
                    }
            }
            Require(fails.Count == 0 && pairsN >= 20 && pieces >= 10, $"{pairsN} face pairs, {pieces} pieces: " + string.Join("; ", fails.Take(5)));
        });
        Check("KRN04-COINCIDENT-NARROW-INTERVALS", () =>
        {
            // совпадающие плоскости: рёбра верхней грани резца пересекают узкую (w) верхнюю грань ребра плиты поперёк — интервал
            // ребра внутри грани длиной w; прежде — знак в 512 точках ребра (шаг 0.04 мм на 20 мм): пропуск (P3D_BNDEVENTS=0)
            var fails = new List<string>(); int n = 0;
            foreach (var w in new[] { 0.03, 0.01 })
                for (int k = 0; k < 3; k++)
                {
                    double x0 = 15 + 0.0077 * k;
                    var u = BooleanOp.Evaluate(new List<TSolid> { Box(x0, 10, 5, 10, 20, 5) }, PrimitiveOp.Sphere(new Vector3d(20, 15, 5.5), 1), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
                    if (u.Outcome != OperationOutcome.Success) { fails.Add($"tool: {u.Outcome}"); continue; }
                    var r = BooleanOp.Evaluate(new List<TSolid> { RibPlate(w) }, u.Bodies[0], BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                    n++;
                    if (r.Outcome != OperationOutcome.Success || r.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"w {w} k {k}: {r.Outcome} {r.Guarantee}"); continue; }
                    var bad = r.Bodies.Select(b => SolidValidator.Validate(b)).FirstOrDefault(v => !v.IsValid);
                    if (bad != null) { fails.Add($"w {w} k {k}: {bad.Summary()}"); continue; }
                    double v = r.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), want = 8000 + 40 * w * 5 - 10 * w * 5 - Math.PI * 0.25 * 2.5 / 3; if (u.Bodies.Count != 1) fails.Add("tool is not one body");
                    if (!(Math.Abs(v - want) <= 1e-6)) fails.Add($"w {w} k {k}: volume {v:R} vs {want:R}");
                }
            Require(fails.Count == 0, $"{n} cases: " + string.Join("; ", fails.Take(5)));
        });
        Check("KRN04-TANGENT-DIAGNOSED-OR-VALID", () =>
        {
            // касание кривой пересечения с ребром (окружность шара в верхней грани касается ребра, боковая — верхнего ребра):
            // точный маршрут честно отказывает (точка самокасания), многогранный результат — годный, в том числе сетка
            // показа (прежде — 201 сторона без пары: грани с вырезом переразмечались по контурам; P3D_POLYDISPLAY=0)
            var fails = new List<string>(); int n = 0;
            double R = 8, h = 3, rc = Math.Sqrt(R * R - h * h);
            foreach (var d in new[] { 0.0, -1e-6, 1e-6, 1e-3 })
                foreach (var kind in new[] { BooleanOp.BooleanKind.Subtract, BooleanOp.BooleanKind.Intersect })
                {
                    var r = BooleanOp.Evaluate(new List<TSolid> { Box(0, 0, 0, 40, 40, 10) }, PrimitiveOp.Sphere(new Vector3d(20, 40 - rc + d, 10 - h), R), kind, OperationPolicy.Strict);
                    n++;
                    if (r.Outcome != OperationOutcome.Success) { fails.Add($"{d} {kind}: {r.Outcome}"); continue; }
                    var bad = r.Bodies.Select(b => SolidValidator.Validate(b)).FirstOrDefault(v => !v.IsValid);
                    if (bad != null) fails.Add($"{d} {kind} ({r.Guarantee}): {bad.Summary()}");
                }
            Require(fails.Count == 0, $"{n} cases: " + string.Join("; ", fails.Take(5)));
        });
        Check("KRN04-ALL-BRANCHES-ORDER-INDEPENDENT", () =>
        {
            // малый цилиндр насквозь через большой: две петли на большом (параметр — по большому) — прежде одна склеенная
            // петля и отказ маршрута при «малый первым» (P3D_CYLINTERVALS=0); объём — интеграл хорд по сечению малого
            double Simp(Func<double, double> g, double a, double b, int n) { double h = (b - a) / n, sm = g(a) + g(b); for (int i = 1; i < n; i++) sm += g(a + i * h) * (i % 2 == 1 ? 4 : 2); return sm * h / 3; }
            // подстановка y = 0.7 + 3 sin t сглаживает корень на краях
            double lens = Simp(t => { double y = 0.7 + 3 * Math.Sin(t); return 2 * 3 * Math.Cos(t) * 2 * Math.Sqrt(100 - y * y) * 3 * Math.Cos(t); }, -Math.PI / 2, Math.PI / 2, 20000);
            TSolid Small() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0.7, -20), 3, 40), TransformOp.Rotate(new Vector3d(0, 0.7, 0), Vector3d.UnitY, 90));
            TSolid Big() => PrimitiveOp.Cylinder(new Vector3d(0, 0, -15), 10, 30);
            var fails = new List<string>();
            foreach (var (name, A, B) in new (string, Func<TSolid>, Func<TSolid>)[] { ("big first", Big, Small), ("small first", Small, Big) })
            {
                var r = BooleanOp.Evaluate(new List<TSolid> { A() }, B(), BooleanOp.BooleanKind.Intersect, OperationPolicy.Strict);
                if (r.Outcome != OperationOutcome.Success || r.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name}: {r.Outcome} {r.Guarantee}"); continue; }
                double v = r.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), err = r.Bodies.Sum(b => AnalyticMeasure.Volume(b).Error);
                if (!(Math.Abs(v - lens) <= err + 1e-7 * lens)) fails.Add($"{name}: {v:R} vs {lens:R} (bound {err:G3})");
            }
            // цилиндр задевает шар на 0.1 мм: петля по образующим цилиндра, площадь грани шара — телесным углом (прежде полюс
            // формы промахивался мимо малого отверстия, P3D_SPHAREA=0)
            var sc = new Vector3d(-12.9, -0.4, 0); double lensS = SphereInCylinder(sc, 10, 3);
            foreach (var kind in new[] { BooleanOp.BooleanKind.Subtract, BooleanOp.BooleanKind.Intersect })
            {
                var r = BooleanOp.Evaluate(new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 10) }, PrimitiveOp.Cylinder(new Vector3d(12.9, 0.4, -5), 3, 10), kind, OperationPolicy.Strict);
                if (r.Outcome != OperationOutcome.Success || r.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"graze {kind}: {r.Outcome} {r.Guarantee}"); continue; }
                double want = kind == BooleanOp.BooleanKind.Intersect ? lensS : 4.0 / 3 * Math.PI * 1000 - lensS;
                double v = r.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), err = r.Bodies.Sum(b => AnalyticMeasure.Volume(b).Error);
                if (!(Math.Abs(v - want) <= err + 1e-9 * Math.Max(1, want))) fails.Add($"graze {kind}: {v:R} vs {want:R} (bound {err:G3})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN04-CONE-GENERATORS-IDENTITIES", () =>
        {
            // конус × шар и конус × конус — по образующим конуса (P3D_CONEGEN=0 — прослеживание), отверстие в области конуса
            // «до вершины» (P3D_CONECAPHOLE=0), грань острого конуса с вершиной и отверстием — разрезом по окружности
            // (P3D_CONESPLIT=0): точные результаты годны и согласованы: V(A ∪ B) + V(A ∩ B) = V(A) + V(B),
            // V(A − B) + V(A ∩ B) = V(A). Обязательные точные: «через бок» — все три, наклонный конус — ∩ и −; прочие
            // сочетания (грань конуса с вершиной и отверстием, малая линза) — честно многогранно, в журнал.
            var cases = new (string, Func<TSolid>, Func<TSolid>, BooleanOp.BooleanKind[])[] {
                ("through the side", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 1, 10), () => PrimitiveOp.Sphere(new Vector3d(4, 0.5, 4), 2.5), new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract }),
                ("tilted cone", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 1, 10), () => TransformOp.Copy(PrimitiveOp.Cone(new Vector3d(0, 0, -2), 3, 1, 14), TransformOp.Rotate(new Vector3d(0, 0, 5), new Vector3d(1, 0.2, 0), 70)), new[] { BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract }),
                ("sharp cone, sphere through the side", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 0, 10), () => PrimitiveOp.Sphere(new Vector3d(4, 0.5, 4), 2.5), new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract }),
                ("sphere grazes the cone 0.01 mm", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 1, 10), () => PrimitiveOp.Sphere(new Vector3d(3 + 10 / Math.Sqrt(136) * 1.49, 0, 5 + 6 / Math.Sqrt(136) * 1.49), 1.5), new BooleanOp.BooleanKind[0]),
            };
            var fails = new List<string>(); var notes = new List<string>();
            foreach (var (name, A, B, must) in cases)
            {
                var v = new Dictionary<BooleanOp.BooleanKind, double>();
                foreach (var kind in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
                {
                    var r = BooleanOp.Evaluate(new List<TSolid> { A() }, B(), kind, OperationPolicy.Strict);
                    if (r.Outcome != OperationOutcome.Success || r.Guarantee != GuaranteeLevel.ExactAnalytic) { (must.Contains(kind) ? fails : notes).Add($"{name} {kind}: {r.Outcome} {r.Guarantee}"); continue; }
                    var bad = r.Bodies.Select(b => SolidValidator.Validate(b)).FirstOrDefault(x => !x.IsValid);
                    if (bad != null) { fails.Add($"{name} {kind}: {bad.Summary()}"); continue; }
                    v[kind] = r.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN);
                }
                double va = OpVolume(A()), vb = OpVolume(B()), tol = 1e-8 * (va + vb);
                if (v.ContainsKey(BooleanOp.BooleanKind.Unite) && v.ContainsKey(BooleanOp.BooleanKind.Intersect) && !(Math.Abs(v[BooleanOp.BooleanKind.Unite] + v[BooleanOp.BooleanKind.Intersect] - va - vb) <= tol))
                    fails.Add($"{name}: ∪ + ∩ = {v[BooleanOp.BooleanKind.Unite] + v[BooleanOp.BooleanKind.Intersect]:R} vs {va + vb:R}");
                if (v.ContainsKey(BooleanOp.BooleanKind.Subtract) && v.ContainsKey(BooleanOp.BooleanKind.Intersect) && !(Math.Abs(v[BooleanOp.BooleanKind.Subtract] + v[BooleanOp.BooleanKind.Intersect] - va) <= tol))
                    fails.Add($"{name}: − + ∩ = {v[BooleanOp.BooleanKind.Subtract] + v[BooleanOp.BooleanKind.Intersect]:R} vs {va:R}");
            }
            Log(new { test = "KRN04-CONE-GENERATORS-IDENTITIES", polyhedral = notes });
            Require(fails.Count == 0, string.Join("; ", fails.Take(6)));
        });
        Check("KRN09-STEP-ROUNDTRIP-PRIMITIVES", () =>
        {
            // наш точный STEP читается нашим же импортом: объём в 1e-8, B-rep годен. Прежде шар, тор и острый конус читались
            // телами объёмом 0 / NaN / ~0 без отказа (P3D_IMPSEAM=0, P3D_SEAMWHOLE=0, P3D_IMPAPEX=0)
            var prims = new (string, Func<TSolid>)[] {
                ("box", () => Box(-5, -5, -5, 10, 12, 14)), ("cyl", () => PrimitiveOp.Cylinder(new Vector3d(1, 2, 3), 5, 10)),
                ("cone", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 6, 3, 10)), ("cone-tip", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 6, 0, 10)),
                ("cone-tip-down", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 0, 6, 10)), ("sphere", () => PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 7)),
                ("torus", () => PrimitiveOp.Torus(new Vector3d(1, 2, 3), 8, 3)) };
            var fails = new List<string>(); int n = 0, meshBad = 0; var meshNotes = new List<string>();
            foreach (var (name, mk) in prims)
                foreach (bool rot in new[] { false, true })
                {
                    var sld = mk(); if (rot) sld = TransformOp.Copy(sld, TransformOp.Rotate(new Vector3d(1, 2, 3), new Vector3d(0.3, 0.5, 0.8).Normalized(), 37));
                    var sw = new StringWriter();
                    if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { sld }, name, new List<string>())) { fails.Add($"{name}: not written"); continue; }
                    var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString());
                    n++;
                    if (back.Count != 1) { fails.Add($"{name} rot {rot}: {back.Count} bodies"); continue; }
                    double v0 = OpVolume(sld), v1 = OpVolume(back[0]);
                    if (!(Math.Abs(v1 - v0) <= 1e-8 * v0)) fails.Add($"{name} rot {rot}: volume {v1:R} vs {v0:R}");
                    var vr = SolidValidator.Validate(back[0]);
                    if (!vr.IsValidFor(Representation.BRep)) fails.Add($"{name} rot {rot}: {vr.Summary()}");
                    if (!vr.IsValidFor(Representation.Mesh)) { meshBad++; meshNotes.Add($"{name} rot {rot}: {vr.Summary()}"); }
                }
            Log(new { test = "KRN09-STEP-ROUNDTRIP-PRIMITIVES", n, meshBad, meshNotes });
            Require(fails.Count == 0 && meshBad == 0, $"{n} round trips, display-mesh defects {meshBad}: " + string.Join("; ", fails.Concat(meshNotes).Take(5)));
        });
        Check("KRN04-SPHERE-CHART-BOTH-POLES", () =>
        {
            // шар со смещённым центром на стенке цилиндра: в грани результата оба полюса карты шара — карта меняется (ось к
            // центру грани); прежде разметчик брал дополнение и маршрут отказывал во всех положениях (P3D_SPHBOTHPOLES=0)
            var fails = new List<string>(); int n = 0;
            for (int deg = 0; deg < 360; deg += 45)
                foreach (var kind in new[] { BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
                {
                    double a = deg * Math.PI / 180; var sc = new Vector3d(9.5 * Math.Cos(a) - 2 * Math.Sin(a), 9.5 * Math.Sin(a) + 2 * Math.Cos(a), 15);
                    var r = BooleanOp.Evaluate(new List<TSolid> { PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30) }, PrimitiveOp.Sphere(sc, 3), kind, OperationPolicy.Strict);
                    n++;
                    if (r.Outcome != OperationOutcome.Success || r.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{deg}° {kind}: {r.Outcome} {r.Guarantee}"); continue; }
                    var bad = r.Bodies.Select(b => SolidValidator.Validate(b)).FirstOrDefault(v => !v.IsValid);
                    if (bad != null) { fails.Add($"{deg}° {kind}: {bad.Summary()}"); continue; }
                    double lens = SphereInCylinder(sc, 3, 10), want = kind == BooleanOp.BooleanKind.Intersect ? lens : Math.PI * 100 * 30 - lens;
                    double v = r.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN), err = r.Bodies.Sum(b => AnalyticMeasure.Volume(b).Error);
                    if (!(Math.Abs(v - want) <= err + 1e-6 * want)) fails.Add($"{deg}° {kind}: volume {v:R} vs {want:R} (bound {err:G3})");
                }
            Require(fails.Count == 0, $"{n} cases: " + string.Join("; ", fails.Take(5)));
        });
    }
}
static partial class Program
{
    static void Krn04Poly(int idx)
    {
        var (name, A, B, kind) = Krn04Catalogue()[idx];
        var r = BooleanOp.Evaluate(new List<TSolid> { A() }, B(), kind, OperationPolicy.Strict);
        Console.WriteLine($"{name}: {r.Describe()}");
        foreach (var b in r.Bodies)
        {
            var mesh = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            var v = SolidValidator.Validate(b);
            foreach (var iss in v.Issues.Take(3)) Console.WriteLine("  " + iss);
            var at = v.Issues.FirstOrDefault(i => i.Code == "open-edge").At;
            if (at is not { } p) continue;
            Console.WriteLine($"  mesh broken faces {mesh.BrokenFaces.Count}: {string.Join(" | ", mesh.BrokenFaces.Take(3))}");
            var fm = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b, facets: true);
            var vf = SolidValidator.ValidateMesh(fm, new ValidationOptions());
            Console.WriteLine($"  facets mesh: {vf.Summary()} {string.Join(" / ", vf.Issues.Take(2))}");
            Console.WriteLine($"  display snapshot faces: {b.Faces.Count(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs0 && fs0.Display != null)}");
            foreach (var g in mesh.Vertices.Where(q => Math.Abs(q.P.X - p.X) < 1e-3 && Math.Abs(q.P.Y - p.Y) < 1e-3 && Math.Abs(q.P.Z - p.Z) < 1e-3).GroupBy(q => q.FaceId))
                Console.WriteLine($"  mesh vertex at the point: face id {g.Key} ({b.Faces.FirstOrDefault(f => f.Id == g.Key)?.Tag}) x{g.Count()}; vertices {mesh.Vertices.Length} indices {mesh.Indices.Length}");
            foreach (var f in b.Faces)
            {
                if (f.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface fs) { Console.WriteLine($"  face {f.Tag} not faceted"); continue; }
                bool has = fs.Facets.ReadView.Any(t => (t.Item1 - p).Length < 1e-3 || (t.Item2 - p).Length < 1e-3 || (t.Item3 - p).Length < 1e-3);
                bool dhas = fs.Display != null && fs.Display.Any(t => (t.A - p).Length < 1e-3 || (t.B - p).Length < 1e-3 || (t.C - p).Length < 1e-3);
                if (has || dhas) Console.WriteLine($"  face {f.Tag} facets {fs.Facets.Count} display {fs.Display?.Count} hasInFacets {has} hasInDisplay {dhas} src {fs.Source?.GetType().Name}");
            }
            Console.WriteLine($"  faces total {b.Faces.Count()} qmesher fits {Parametric3D.Kernel.Tess.QMesher.Fits(b)} tags {string.Join(",", b.Faces.Select(f => f.Tag))}");
        }
    }
}
static partial class Program
{
    /// <summary>Разбор случая разностного сравнения по его STEP: операнды читаются нашим импортом, булева, объём по граням.</summary>
    static void StepOp(string[] args)
    {
        // stepop a.step op1 b.step [op2 c.step ...]
        TSolid Rd(string f) => Parametric3D.Kernel.Export.StepImport.ReadExactSolids(File.ReadAllText(f))[0];
        var cur = new List<TSolid> { Rd(args[1]) };
        for (int i = 1; i < args.Length; i += 2) { var x = Rd(args[i]); Console.WriteLine($"operand {args[i]}: vol {OpVolume(x):R} valid {SolidValidator.Validate(x).Summary()} faces {string.Join(",", x.Faces.Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name + ":" + string.Join("", f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge.Curve.GetType().Name[0]))))}"); }
        BooleanOp.AnalyticRouteDebug = s => Console.WriteLine("  route: " + s);
        OperationResult r = null!;
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            var B = Rd(args[i + 1]); var op = args[i];
            var kind = op == "cut" ? BooleanOp.BooleanKind.Subtract : op == "fuse" ? BooleanOp.BooleanKind.Unite : BooleanOp.BooleanKind.Intersect;
            r = BooleanOp.Evaluate(cur, B, kind, OperationPolicy.Strict);
            Console.WriteLine($"{op}: {r.Describe()}");
            if (r.Outcome != OperationOutcome.Success) return;
            cur = r.Bodies.ToList();
        }
        Console.WriteLine($"total {cur.Sum(b => AnalyticVolume.Of(b) ?? double.NaN):R}");
        foreach (var bb in r.Bodies)
        {
            Console.WriteLine($" body vol {AnalyticVolume.Of(bb):R} bound {AnalyticMeasure.Volume(bb).Error:G3} entity {AnalyticMeasure.EntityBound(bb):G3} valid {SolidValidator.Validate(bb).Summary()}");
            foreach (var f in bb.Faces)
                Console.WriteLine($"  face {f.Tag} {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count} edges {string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge.Curve.GetType().Name[0]))} integral {AnalyticVolume.FaceIntegral(f):R}");
            double maxTol = bb.Edges.Max(e => e.Tolerance); Console.WriteLine($"  max edge tol {maxTol:G3}");
        }
    }
}
static partial class Program
{
    /// <summary>Круговой обмен: заготовка → наш точный STEP → наш импорт: объём и годность.</summary>
    static void StepRoundTrip()
    {
        var prims = new (string, Func<TSolid>)[] {
            ("box", () => Box(-5, -5, -5, 10, 12, 14)),
            ("cyl", () => PrimitiveOp.Cylinder(new Vector3d(1, 2, 3), 5, 10)),
            ("cone", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 6, 3, 10)),
            ("cone-tip", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 6, 0, 10)),
            ("cone-tip-down", () => PrimitiveOp.Cone(new Vector3d(1, 2, 3), 0, 6, 10)),
            ("sphere", () => PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 7)),
            ("torus", () => PrimitiveOp.Torus(new Vector3d(1, 2, 3), 8, 3)),
        };
        foreach (var (name, mk) in prims)
            foreach (bool rot in new[] { false, true })
            {
                var s = mk(); if (rot) s = TransformOp.Copy(s, TransformOp.Rotate(new Vector3d(1, 2, 3), new Vector3d(0.3, 0.5, 0.8).Normalized(), 37));
                var sw = new StringWriter(); var w = new List<string>();
                bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, name, w);
                var iw = new List<string>();
                var back = ok ? Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), iw) : new List<TSolid>();
                string res = back.Count == 0 ? "no body: " + string.Join("; ", iw.Take(2)) : string.Join(" ", back.Select(b => $"vol {OpVolume(b):G10} {SolidValidator.Validate(b).Summary()}"));
                Console.WriteLine($"RT {name}{(rot ? " rot" : "")}: original {OpVolume(s):G10} → {res}");
                foreach (var b in back) foreach (var iss in SolidValidator.Validate(b).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3)) Console.WriteLine("    " + iss);
                foreach (var b in back) foreach (var f in b.Faces) { var sf = GeometryAudit.AuthoritativeSurface(f); Console.WriteLine($"    face {sf?.GetType().Name} {(sf is Parametric3D.Kernel.Surfaces.RevolvedSurface rv ? $"base {rv.Base.GetType().Name} [{rv.Base.PointAt(rv.Base.T0)}..{rv.Base.PointAt(rv.Base.T1)}] sweep {rv.Sweep:G6}" : "")} loops {string.Join("/", f.Loops.Select(l => string.Join(",", l.CoEdges.Select(ce => ce.Edge.Id + (ce.Reversed ? "-" : "+")))))} integral {AnalyticVolume.FaceIntegral(f)}"); }
            }
    }
}
static partial class Program
{
    static void ConeTipMesh()
    {
        var s = PrimitiveOp.Cone(new Vector3d(1, 2, 3), 6, 0, 10);
        var sw = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, "c", new List<string>());
        var b = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString())[0];
        foreach (var f in b.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src is not Parametric3D.Kernel.Surfaces.RevolvedSurface) continue;
            var fs = (Parametric3D.Kernel.Surfaces.FacetedSurface)f.Surface;
            Console.WriteLine($"lateral: facets {fs.Facets.Count}");
            var target = new Vector3d(1.66581, 2.03387, 11.88889);
            foreach (var (x, y, z) in fs.Facets.ReadView)
            {
                var c = (x + y + z) / 3; if ((c - target).Length > 0.6) continue;
                var n = Vector3d.Cross(y - x, z - x); var rad = c - new Vector3d(1, 2, c.Z);
                Console.WriteLine($"  tri {x:0.###} {y:0.###} {z:0.###} area {n.Length / 2:G3} out {Vector3d.Dot(n, rad):G3}");
            }
        }
    }
}
static partial class Program
{
    static void ConeProbe()
    {
        BooleanOp.AnalyticRouteDebug = s => Console.WriteLine("  route: " + s);
        var cases = new (string, Func<TSolid>, Func<TSolid>)[] {
            ("small loop", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 1, 10), () => PrimitiveOp.Sphere(new Vector3d(4.1, 0, 6.2), 1.6)),
            ("tilted cone", () => PrimitiveOp.Cone(Vector3d.Zero, 6, 1, 10), () => TransformOp.Copy(PrimitiveOp.Cone(new Vector3d(0, 0, -2), 3, 1, 14), TransformOp.Rotate(new Vector3d(0, 0, 5), new Vector3d(1, 0.2, 0), 70))) };
        foreach (var (name, A, B) in cases)
            foreach (var kind in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
            {
                Console.WriteLine($"== {name} {kind}");
                var r = BooleanOp.Evaluate(new List<TSolid> { A() }, B(), kind, OperationPolicy.Strict);
                Console.WriteLine($"   {r.Outcome} {r.Guarantee}");
            }
    }
}
static partial class Program
{
    static void MetaProbe()
    {
        Krn02Hard = true;
        BooleanOp.AnalyticRouteDebug = m => { if (!m.Contains("accepted") && !m.Contains("not along")) Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] route: {m}"); };
        var cases = new List<(string, Vector3d, Vector3d, Vector3d, double)> {
            ("scale0.01", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 0.01),
            ("far-rotate-scale1000", new Vector3d(4e5, -4e5, 3e5), new Vector3d(0.8, 0.5, 0.33), new Vector3d(-0.4, 0.9, 0.1), 1000) };
        var rnd = new Random(7);
        Vector3d RU() { while (true) { var v = new Vector3d(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1); if (v.Length > 0.2 && v.Length < 1) return v.Normalized(); } }
        foreach (double k in new[] { 1.0, 0.3, 1, 30 })
        {
            var u = RU(); var v = Vector3d.Cross(u, RU()).Normalized();
            var o = new Vector3d(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * (2e5 * rnd.NextDouble());
            cases.Add(($"rand-k{k}", o, u, v, k));
        }
        foreach (var (n, o, u, v, k) in cases)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = Krn02Scene(o, u, v, k, out var g);
            Console.WriteLine($"{n}: {g} {sw.ElapsedMilliseconds} ms, mem {GC.GetTotalMemory(false) / 1e6:F0} MB");
            sw.Restart(); var vol = AnalyticVolume.Of(s); Console.WriteLine($"  volume {vol} {sw.ElapsedMilliseconds} ms mem {GC.GetTotalMemory(false) / 1e6:F0} MB");
            sw.Restart(); var vb = SolidValidator.ValidateBRep(s); Console.WriteLine($"  brep {vb.Summary()} {sw.ElapsedMilliseconds} ms");
            sw.Restart(); var vp = SolidValidator.ValidatePCurves(s); Console.WriteLine($"  pcurves {vp.Summary()} {sw.ElapsedMilliseconds} ms mem {GC.GetTotalMemory(false) / 1e6:F0} MB");
            sw.Restart(); var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(s); Console.WriteLine($"  mesh {m.Vertices.Length} {sw.ElapsedMilliseconds} ms mem {GC.GetTotalMemory(false) / 1e6:F0} MB");
        }
    }
}
