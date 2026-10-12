using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-05 (05.1, 05.2): РАЗДЕЛЬНЫЕ И ВЛОЖЕННЫЕ ТЕЛА В ОБЩЕЙ БУЛЕВОЙ. Без пересечения поверхностей грани целиком отбираются
/// классификаторами; инвариант оболочек делит результат на тела и полости. Ответы — формулами объёмов шаров и бруска.
/// </summary>
static partial class Program
{
    static void Krn05Lens()
    {
        foreach (var op in new[] { Parametric3D.Kernel.Analytic.Bool2.Union, Parametric3D.Kernel.Analytic.Bool2.Intersect })
        {
            var lp = (Environment.GetEnvironmentVariable("LENS") ?? "10,10,6").Split(',').Select(double.Parse).ToArray();
            var raw = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(PrimitiveOp.Sphere(Vector3d.Zero, lp[0]), PrimitiveOp.Sphere(new Vector3d(lp[2], 0, 0), lp[1]), op, out var why);
            Console.WriteLine($"RAW {op}: {why}");
            void Dump(string stage)
            {
                foreach (var f in raw!.Faces)
                {
                    var ce = f.Loops[0].CoEdges[0]; var a = (Parametric3D.Kernel.Curves.ArcCurve3)ce.Edge.Curve;
                    // направление обхода вокруг оси X (окружность пересечения в плоскости x = 3)
                    var t = PCurvesDir(ce);
                    var c = (GeometryAudit.AuthoritativeSurface(f) as Parametric3D.Kernel.Surfaces.RevolvedSurface)!;
                    var p = a.PointAt(0.25); var n = f.NormalAt(p);
                    Console.WriteLine($"  {stage} face {f.Id} rev {f.Reversed} ceRev {ce.Reversed} circleW {a.Frame.W:0.##} sweep {a.Sweep:0.##} travel·X {Vector3d.Dot(Vector3d.Cross(p - a.Frame.Origin, t), Vector3d.UnitX):0.##} n {n:0.##} at {p:0.##} surf {f.Surface.GetType().Name}");
                }
            }
            Vector3d PCurvesDir(TCoEdge ce) { var a = ce.Edge.Curve; var tt = a.TangentAt(0.25); return ce.Reversed ? -tt : tt; }
            Dump("raw");
            foreach (var f in raw!.Faces) Console.WriteLine($"  hint face {f.Id}: {f.InteriorHint}");
            TopologyConventions.AnchorDebug = m => Console.WriteLine("  anchor: " + m);
            var fw = new List<string>(); Parametric3D.Kernel.Analytic.PrismBuild.Finish(raw!, fw);
            TopologyConventions.AnchorDebug = null; foreach (var x in fw) Console.WriteLine("  finish: " + x);
            Dump("finished");
            Console.WriteLine($"  volume {AnalyticVolume.Of(raw!)}");
        }
        foreach (var k in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect })
        {
            var r = BooleanOp.Evaluate(new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 10) }, PrimitiveOp.Sphere(new Vector3d(6, 0, 0), 10), k, OperationPolicy.Strict);
            Console.WriteLine($"{k}: {r.Describe()}");
            foreach (var b in r.Bodies)
                foreach (var f in b.Faces)
                {
                    var src = GeometryAudit.AuthoritativeSurface(f) as Parametric3D.Kernel.Surfaces.RevolvedSurface;
                    var fs = f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface;
                    double meshA = fs == null ? -1 : fs.Facets.ToList().Sum(t => Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length / 2);
                    var mean = fs == null ? Vector3d.Zero : fs.Facets.ToList().Aggregate(Vector3d.Zero, (acc, t) => acc + (t.Item1 + t.Item2 + t.Item3) / 3) / Math.Max(1, fs.Facets.Count);
                    Console.WriteLine($"  face {f.Id} rev {f.Reversed} loops {f.Loops.Count} ces {f.Loops.Sum(l => l.CoEdges.Count)} meshA {meshA:0.###} meshMean {mean:0.##} area {AnalyticVolume.FaceArea(f):0.###} int {AnalyticVolume.FaceIntegral(f):0.###} closedUsed {AnalyticVolume.SphereClosedUsed} rej {AnalyticVolume.SphereClosedRejected}");
                }
        }
    }

    /// <summary>
    /// g.02: ТОЖДЕСТВА БУЛЕВЫХ без внешнего эталона: V(A∪B) + V(A∩B) = V(A) + V(B), V(A−B) + V(A∩B) = V(A) — каждое по
    /// точным объёмам наших результатов (успешных). Пары — заготовки со случайными размерами, сдвигами, поворотами
    /// (детерминированно). Неверное принятие одной из трёх операций ломает тождество (объединение конуса с шаром давало
    /// разность — найдено разностным сравнением с OpenCascade).
    /// </summary>
    /// <summary>Операнд проверки тождеств (тот же генератор, что в BooleanIdentities).</summary>
    internal static TSolid IdentityPrim(Random rnd, out string d)
    {
        double U(double a, double b) => a + (b - a) * rnd.NextDouble();

        var at = new Vector3d(U(-8, 8), U(-8, 8), U(-8, 8)); TSolid s;
        switch (rnd.Next(5))
        {
            case 0: { double w = U(8, 30), dd = U(8, 30), h = U(8, 30); s = Box(at.X - w / 2, at.Y - dd / 2, at.Z - h / 2, w, dd, h); d = "box"; break; }
            case 1: { double r = U(2, 10), h = U(8, 30); s = PrimitiveOp.Cylinder(at - new Vector3d(0, 0, h / 2), r, h); d = "cyl"; break; }
            case 2: { double r0 = U(2, 10), r1 = rnd.Next(3) == 0 ? 0 : U(1, 9), h = U(8, 25); s = PrimitiveOp.Cone(at - new Vector3d(0, 0, h / 2), r0, r1, h); d = r1 == 0 ? "cone-tip" : "cone"; break; }
            case 3: { double r = U(3, 12); s = PrimitiveOp.Sphere(at, r); d = "sphere"; break; }
            default: { double tube = U(1.5, 4), ring = tube + U(2, 9); s = PrimitiveOp.Torus(at, ring, tube); d = "torus"; break; }
        }
        if (rnd.Next(2) == 0)
        {
            var ax = new Vector3d(U(-1, 1), U(-1, 1), U(-1, 1)); if (ax.Length < 0.1) ax = Vector3d.UnitX;
            double deg = U(0, 180); s = TransformOp.Copy(s, TransformOp.Rotate(at, ax.Normalized(), deg)); d += $" rot{deg:0}";
        }
        return s;
    }

    /// <summary>Пара операндов случая i проверки тождеств.</summary>
    internal static (TSolid A, string Da, TSolid B, string Db) IdentityOperands(int seed, int i)
    {
        var rnd = new Random(seed); TSolid a = null!, b = null!; string da = "", db = "";
        for (int k = 0; k <= i; k++) { a = IdentityPrim(rnd, out da); b = IdentityPrim(rnd, out db); }
        return (a, da, b, db);
    }

    internal static List<string> BooleanIdentities(int seed, int n, out int checkedPairs)
    {
        var rnd = new Random(seed);
        var fails = new List<string>(); checkedPairs = 0;
        TSolid Prim(out string d) => IdentityPrim(rnd, out d);
        for (int i = 0; i < n; i++)
        {
            var a = Prim(out var da); var b = Prim(out var db);
            double va = OpVolume(a), vb = OpVolume(b);
            OperationResult Run(BooleanOp.BooleanKind k) => BooleanOp.Evaluate(new List<TSolid> { a }, b, k, OperationPolicy.Strict);
            bool idd = Environment.GetEnvironmentVariable("IDDBG") == i.ToString();
            if (idd) BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("  route∪∩: " + m);
            var f = Run(BooleanOp.BooleanKind.Unite); var c = Run(BooleanOp.BooleanKind.Intersect);
            if (idd) { TopologyConventions.AnchorDebug = m => Console.WriteLine("  anchor: " + m); BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("  route: " + m); }
            var x = Run(BooleanOp.BooleanKind.Subtract);
            TopologyConventions.AnchorDebug = null; BooleanOp.AnalyticRouteDebug = null;
            if (Environment.GetEnvironmentVariable("IDDBG") == i.ToString())
            {
                Console.WriteLine($"operands: A {da}, B {db}");
                var ma = Parametric3D.Kernel.Tess.Tessellator.Tessellate(a); var mbm = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
                var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
                foreach (var vv in ma.Vertices.Concat(mbm.Vertices)) { var q = new Vector3d(vv.P.X, vv.P.Y, vv.P.Z); lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q); }
                lo -= new Vector3d(0.5); hi += new Vector3d(0.5);
                var ca = new Parametric3D.Kernel.Analytic.Classifier(a); var cb = new Parametric3D.Kernel.Analytic.Classifier(b);
                var cu = f.Bodies.Select(bb => new Parametric3D.Kernel.Analytic.Classifier(bb)).ToList();
                var rr2 = new Random(3); int N = 40000, nA = 0, nB = 0, nU = 0, nUr = 0, bad = 0;
                for (int k2 = 0; k2 < N; k2++)
                {
                    var p = new Vector3d(lo.X + (hi.X - lo.X) * rr2.NextDouble(), lo.Y + (hi.Y - lo.Y) * rr2.NextDouble(), lo.Z + (hi.Z - lo.Z) * rr2.NextDouble());
                    bool ia = ca.Classify(p) > 0, ib = cb.Classify(p) > 0, iu = cu.Any(c2 => c2.Classify(p) > 0);
                    if (ia) nA++; if (ib) nB++; if (ia || ib) nU++; if (iu) nUr++;
                    if ((ia || ib) != iu && bad++ < 5) Console.WriteLine($"   MC mismatch at {p:0.###}: A {ia} B {ib} result∪ {iu}");
                }
                double bv = (hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z);
                Console.WriteLine($"MC: A {bv * nA / N:0.#} B {bv * nB / N:0.#} A∪B {bv * nU / N:0.#} result∪ {bv * nUr / N:0.#} mismatches {bad}");
            }
            if (Environment.GetEnvironmentVariable("IDDBG") == i.ToString())
                foreach (var (nm, rr) in new[] { ("A", (OperationResult?)null), ("∪", f), ("∩", c), ("−", x) })
                {
                    var bodies = rr == null ? new List<TSolid> { a, b } : rr.Bodies.ToList();
                    Console.WriteLine($"{nm}: {rr?.Describe()} VA {va:R} VB {vb:R}");
                    foreach (var bb in bodies) foreach (var fc in bb.Faces)
                    {
                        double mi = fc.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx ? fsx.Facets.ToList().Sum(t => Vector3d.Dot(t.Item1, Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1)) / 2) : double.NaN;
                        Console.WriteLine($"   face {fc.Id} {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} ces {string.Join("/", fc.Loops.Select(l => l.CoEdges.Count))} {string.Join(",", fc.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge.Curve.GetType().Name.Replace("Curve3", "")))} int {AnalyticVolume.FaceIntegral(fc):0.###} mesh {mi:0.###}");
                    }
                }
            bool ok(OperationResult r) => r.Outcome is OperationOutcome.Success or OperationOutcome.Empty;
            double V(OperationResult r) => r.Outcome == OperationOutcome.Empty ? 0 : r.Bodies.Sum(bb => OpVolume(bb));
            double VM(OperationResult r) => r.Outcome == OperationOutcome.Empty ? 0 : r.Bodies.Sum(bb => bb.Exact != null && bb.ExactAuthoritative ? bb.Exact.Solid.VolumeMm3().ToDouble() : Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bb)));
            // допуск: точные — 1e-9 суммы; многогранное представление — по хорде
            double tolOf(params OperationResult[] rs) => rs.All(r => r.Outcome == OperationOutcome.Empty || r.Guarantee == GuaranteeLevel.ExactAnalytic) ? 1e-8 * (va + vb) : 1e-2 * (va + vb);   // многогранное: шаг угла 7.5° — вписанный многогранник меньше до ~0.6%
            if (ok(f) && ok(c))
            {
                checkedPairs++;
                double e = V(f) + V(c) - va - vb;
                if (!(Math.Abs(e) <= tolOf(f, c))) fails.Add($"#{i} {da} ∪/∩ {db}: V∪+V∩−VA−VB = {e:G6} ({f.Guarantee}/{c.Guarantee}); by exact/mesh form {VM(f) + VM(c) - va - vb:G6}; ∪ {V(f):G8}/{VM(f):G8} ∩ {V(c):G8}/{VM(c):G8}");
            }
            if (ok(x) && ok(c))
            {
                double e = V(x) + V(c) - va;
                if (!(Math.Abs(e) <= tolOf(x, c))) fails.Add($"#{i} {da} −/∩ {db}: V−+V∩−VA = {e:G6} ({x.Guarantee}/{c.Guarantee}); by exact/mesh form {VM(x) + VM(c) - va:G6}; − {V(x):G8}/{VM(x):G8}");
            }
        }
        return fails;
    }

    internal static void SelfIdentities()
    {
        Check("KRN05-SELF-IDENTITIES", () =>
        {
            // 05.8: A∪A = A, A∩A = A, A−A = ∅ — второй операнд — отдельная копия той же геометрии (все грани совпадают)
            var rnd = new Random(20261011); var fails = new List<string>(); int n = 0, exact = 0;
            var cases = new List<(string, TSolid)>();
            for (int i = 0; i < 24; i++) { var p = IdentityPrim(rnd, out var d); cases.Add(($"#{i} {d}", p)); }
            foreach (var id in new[] { "S-HOLE", "S-ROUND", "S-SPHERE", "S-UNION" }) cases.Add((id, Scenario(id, new List<string>())[0]));
            foreach (var (name, a) in cases)
            {
                double va = OpVolume(a);
                foreach (var k in new[] { BooleanOp.BooleanKind.Unite, BooleanOp.BooleanKind.Intersect, BooleanOp.BooleanKind.Subtract })
                {
                    var b = TransformOp.Copy(a, TransformOp.Move(Vector3d.Zero));
                    OperationResult r;
                    var swk = System.Diagnostics.Stopwatch.StartNew();
                    try { r = BooleanOp.Evaluate(new List<TSolid> { a }, b, k, OperationPolicy.Strict); }
                    catch (Exception ex) { fails.Add($"{name} {k}: {ex.GetType().Name} {ex.Message}"); continue; }
                    finally { if (Environment.GetEnvironmentVariable("SELFDBG") == "1") Console.WriteLine($"SELF {name} {k} {swk.Elapsed.TotalSeconds:F2} s"); }
                    n++;
                    if (k == BooleanOp.BooleanKind.Subtract)
                    {
                        if (r.Outcome != OperationOutcome.Empty) fails.Add($"{name} A−A: {r.Outcome} ({r.Describe()})");
                        // пустота точного операнда доказывается точным маршрутом (не многогранным путём по совпадающим граням)
                        else if (a.Guarantee == GuaranteeLevel.ExactAnalytic && r.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name} A−A: empty only by the {r.Guarantee} route");
                        continue;
                    }
                    if (r.Outcome != OperationOutcome.Success || r.Bodies.Count != 1) { fails.Add($"{name} {k}: {r.Outcome} bodies {r.Bodies.Count} ({r.Describe()})"); continue; }
                    double v = OpVolume(r.Bodies[0]);
                    bool ex2 = r.Guarantee == GuaranteeLevel.ExactAnalytic; if (ex2) exact++;
                    double tol = ex2 ? 1e-9 * va : 1e-2 * va;
                    if (!(Math.Abs(v - va) <= tol)) fails.Add($"{name} {k}: volume {v:R} vs {va:R} ({r.Guarantee})");
                    if (!SolidValidator.Validate(r.Bodies[0]).IsValid) fails.Add($"{name} {k}: invalid");
                }
            }
            Log(new { test = "KRN05-SELF-IDENTITIES", ops = n, exact, fails = fails.Count, first = fails.Take(6) });
            Require(fails.Count == 0, $"{fails.Count} of {n}: " + string.Join("; ", fails.Take(8)));
        });
    }

    static void Krn05()
    {
        Check("KRN05-BOOLEAN-IDENTITIES", () =>
        {
            var fails = BooleanIdentities(20261010, 60, out int pairs);
            Log(new { test = "KRN05-BOOLEAN-IDENTITIES", pairs, fails = fails.Count, first = fails.Take(5) });
            Require(fails.Count == 0 && pairs >= 40, $"{fails.Count} broken identities of {pairs}: " + string.Join("; ", fails.Take(6)));
        });
        SelfIdentities();
        Check("KRN05-NESTED-DISJOINT-ANALYTIC", () =>
        {
            double Ball(double r) => 4.0 / 3 * Math.PI * r * r * r;
            var S = BooleanOp.BooleanKind.Subtract; var U = BooleanOp.BooleanKind.Unite; var I = BooleanOp.BooleanKind.Intersect;
            TSolid Sph(double x, double r) => PrimitiveOp.Sphere(new Vector3d(x, 0, 0), r);
            var fails = new List<string>();
            void Case(string name, Func<List<TSolid>> a, Func<TSolid> b, BooleanOp.BooleanKind k, int bodies, int[] shells, double[] vols)
            {
                var r = BooleanOp.Evaluate(a(), b(), k, OperationPolicy.Strict);
                if (bodies == 0) { if (r.Outcome != OperationOutcome.Empty) fails.Add($"{name}: {r.Describe()} (want Empty)"); return; }
                if (r.Outcome != OperationOutcome.Success || r.Bodies.Count != bodies) { fails.Add($"{name}: {r.Describe()} bodies {r.Bodies.Count}"); return; }
                if (r.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {r.Guarantee}");
                var got = r.Bodies.Select(x => (S: x.Shells.Count, V: AnalyticVolume.Of(x) ?? double.NaN)).OrderBy(x => x.V).ToList();
                var want = shells.Zip(vols).Select(z => (S: z.First, V: z.Second)).OrderBy(x => x.V).ToList();
                for (int i = 0; i < bodies; i++)
                    if (got[i].S != want[i].S || !(Math.Abs(got[i].V - want[i].V) <= 1e-12 * want[i].V))
                        fails.Add($"{name}: body {i} shells {got[i].S} volume {got[i].V:R} vs {want[i].S} / {want[i].V:R}");
                foreach (var x in r.Bodies)
                {
                    var v = SolidValidator.Validate(x);
                    if (!v.IsValid) fails.Add($"{name}: {v.Summary()}");
                    if (ShellComponents.Invariant(x) is { } inv) fails.Add($"{name}: {inv}");
                }
            }
            // шар B внутри шара A, шар C — отдельно
            Case("A − B (nested)", () => new() { Sph(0, 10) }, () => Sph(1, 4), S, 1, new[] { 2 }, new[] { Ball(10) - Ball(4) });
            Case("A ∪ B (nested)", () => new() { Sph(0, 10) }, () => Sph(1, 4), U, 1, new[] { 1 }, new[] { Ball(10) });
            Case("A ∩ B (nested)", () => new() { Sph(0, 10) }, () => Sph(1, 4), I, 1, new[] { 1 }, new[] { Ball(4) });
            Case("B − A (inside)", () => new() { Sph(1, 4) }, () => Sph(0, 10), S, 0, Array.Empty<int>(), Array.Empty<double>());
            Case("B ∪ A (inside)", () => new() { Sph(1, 4) }, () => Sph(0, 10), U, 1, new[] { 1 }, new[] { Ball(10) });
            Case("B ∩ A (inside)", () => new() { Sph(1, 4) }, () => Sph(0, 10), I, 1, new[] { 1 }, new[] { Ball(4) });
            Case("A − C (disjoint)", () => new() { Sph(0, 10) }, () => Sph(30, 5), S, 1, new[] { 1 }, new[] { Ball(10) });
            Case("A ∪ C (disjoint)", () => new() { Sph(0, 10) }, () => Sph(30, 5), U, 2, new[] { 1, 1 }, new[] { Ball(10), Ball(5) });
            Case("A ∩ C (disjoint)", () => new() { Sph(0, 10) }, () => Sph(30, 5), I, 0, Array.Empty<int>(), Array.Empty<double>());
            // брусок 60×40×20 с полостью-шаром: полость заполняется большим шаром; шар внутри полости — отдельное тело
            List<TSolid> Hollow(double r) { var l = new List<TSolid> { Box() }; BooleanOp.Subtract(l, PrimitiveOp.Sphere(new Vector3d(30, 20, 10), r), new List<string>()); return l; }
            Case("box with cavity ∪ larger ball (cavity filled)", () => Hollow(4), () => PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 6), U, 1, new[] { 1 }, new[] { 48000.0 });
            Case("box with cavity ∪ ball inside the void", () => Hollow(6), () => PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 3), U, 2, new[] { 2, 1 }, new[] { 48000 - Ball(6), Ball(3) });
            Case("box with cavity − second cavity", () => Hollow(4), () => PrimitiveOp.Sphere(new Vector3d(12, 20, 10), 5), S, 1, new[] { 3 }, new[] { 48000 - Ball(4) - Ball(5) });
            Case("box with cavity ∩ ball around the cavity", () => Hollow(4), () => PrimitiveOp.Sphere(new Vector3d(30, 20, 10), 8), I, 1, new[] { 2 }, new[] { Ball(8) - Ball(4) });
            // пересекающиеся шары (линза): грань — шар без шапки, БОЛЬШЕ полусферы (найдено разностным сравнением g.02)
            double Lens(double R, double r, double d) => Math.PI * Math.Pow(R + r - d, 2) * (d * d + 2 * d * r - 3 * r * r + 2 * d * R + 6 * r * R - 3 * R * R) / (12 * d);
            foreach (var (R, r, d) in new[] { (14.46, 9.23, 10.0), (10.0, 10.0, 6.0), (12.0, 5.0, 14.0), (8.0, 6.0, 3.0) })
            {
                double L = Lens(R, r, d);
                Case($"lens R{R} r{r} d{d} ∪", () => new() { Sph(0, R) }, () => Sph(d, r), U, 1, new[] { 1 }, new[] { Ball(R) + Ball(r) - L });
                Case($"lens R{R} r{r} d{d} ∩", () => new() { Sph(0, R) }, () => Sph(d, r), I, 1, new[] { 1 }, new[] { L });
                Case($"lens R{R} r{r} d{d} −", () => new() { Sph(0, R) }, () => Sph(d, r), S, 1, new[] { 1 }, new[] { Ball(R) - L });
            }
            // цилиндр насквозь через шар (пояс шара — грань с двумя кольцами; серия 4 разностного сравнения)
            foreach (var (R, a, h) in new[] { (14.37, 4.44, 39.21), (10.0, 3.0, 30.0), (8.0, 6.0, 20.0) })
            {
                double Vc = Math.PI * a * a * h, Vi = 4.0 / 3 * Math.PI * (R * R * R - Math.Pow(R * R - a * a, 1.5));
                TSolid Cyl() => PrimitiveOp.Cylinder(new Vector3d(0, 0, -h / 2), a, h);
                Case($"sphere R{R} ∪ cylinder r{a} through", () => new() { Sph(0, R) }, Cyl, U, 1, new[] { 1 }, new[] { Ball(R) + Vc - Vi });
                Case($"sphere R{R} − cylinder r{a} through", () => new() { Sph(0, R) }, Cyl, S, 1, new[] { 1 }, new[] { Ball(R) - Vi });
                Case($"sphere R{R} ∩ cylinder r{a} through", () => new() { Sph(0, R) }, Cyl, I, 1, new[] { 1 }, new[] { Vi });
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
