using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Query;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn10PreparedChecks()
    {
        Check("KRN10-PREPARED-BODY", () =>
        {
            // 10.2: подготовленное тело. (а) Ответы те же, что у BodyQuery по телу (классификация, ближайшая точка побитно,
            // опорная функция) на деталях K08, шаре, торе, конусе, повёрнутом цилиндре. (б) Подготовка одна на тело за все
            // запросы; (в) ближайшая точка отсеивает грани габаритом; (г) повторные запросы быстрее; (д) правка тела —
            // новая подготовка, прежняя устарела.
            var fails = new List<string>();
            var bodies = new List<TSolid>();
            for (int seed = 1; seed <= 2; seed++) bodies.AddRange(RealisticPart(seed, 10, out _).Bodies);
            bodies.Add(PrimitiveOp.Sphere(new Vector3d(20, 10, 5), 12));
            bodies.Add(PrimitiveOp.Torus(new Vector3d(-10, 0, 10), 20, 4));
            bodies.Add(PrimitiveOp.Cone(new Vector3d(30, -20, -5), 10, 2, 25));
            bodies.Add(TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -30), 6, 60), TransformOp.Rotate(Vector3d.Zero, new Vector3d(1, 1, 0).Normalized(), 37)));
            var rnd = new Random(7);
            long b0 = PreparedBody.Builds, p0 = PreparedBody.PrunedFaces;
            double tPlain = 0, tPrep = 0; int queries = 0, mism = 0;
            var dirs = new[] { Vector3d.UnitX, -Vector3d.UnitY, new Vector3d(1, 2, -3).Normalized() };
            foreach (var body in bodies)
            {
                var bb = Parametric3D.Kernel.Bounds3.Empty; foreach (var v in body.Vertices) bb.Add(v.P);
                if (body.Vertices.Count() < 2) { bb.Add(new Vector3d(-40, -40, -40)); bb.Add(new Vector3d(60, 60, 60)); }
                var lo = bb.Min - new Vector3d(5); var sz = bb.Max - bb.Min + new Vector3d(10);
                var pts = Enumerable.Range(0, 40).Select(_ => lo + new Vector3d(sz.X * rnd.NextDouble(), sz.Y * rnd.NextDouble(), sz.Z * rnd.NextDouble())).ToList();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var plain = pts.Select(p => (BodyQuery.Classify(body, p), BodyQuery.Closest(body, p))).ToList();
                var sup = dirs.Select(d => BodyQuery.Support(body, d)).ToList();
                tPlain += sw.Elapsed.TotalMilliseconds; sw.Restart();
                var prepared = pts.Select(p => { var pb = PreparedBody.For(body); return (pb.Classify(p), pb.Closest(p)); }).ToList();
                var sup2 = dirs.Select(d => PreparedBody.For(body).Support(d)).ToList();
                tPrep += sw.Elapsed.TotalMilliseconds;
                for (int i = 0; i < pts.Count; i++)
                {
                    queries++;
                    var (c1, d1) = plain[i]; var (c2, d2) = prepared[i];
                    bool same = c1 == c2 && (d1 is null) == (d2 is null) && (d1 is not { } x || d2 is { } y && x.Distance == y.Distance && x.Point == y.Point);
                    if (!same && mism++ < 3) fails.Add($"{body.Faces.Count()}-face body, point {pts[i]}: plain {c1} {d1?.Distance:R}, prepared {c2} {d2?.Distance:R}");
                }
                for (int i = 0; i < dirs.Length; i++)
                    if (sup[i] != sup2[i] && mism++ < 3) fails.Add($"support {dirs[i]}: plain {sup[i]:R}, prepared {sup2[i]:R}");
            }
            long builds = PreparedBody.Builds - b0, pruned = PreparedBody.PrunedFaces - p0;
            if (builds != bodies.Count) fails.Add($"{builds} preparations for {bodies.Count} bodies and {queries} queries — not reused");
            if (pruned == 0) fails.Add("closest point: no face pruned by its bounds");
            if (tPrep * 2 > tPlain) fails.Add($"prepared queries {tPrep:F0} ms vs plain {tPlain:F0} ms — no gain");
            // (д) правка: новая ревизия — новая подготовка, прежняя устарела
            var box = Box(0, 0, 0, 10, 10, 10);
            var pa = PreparedBody.For(box);
            if (!ReferenceEquals(pa, PreparedBody.For(box))) fails.Add("same body state, a second preparation");
            box.BeginEdit("KRN10-PREPARED-BODY", EditKind.Layout);
            long bb0 = PreparedBody.Builds; var pb2 = PreparedBody.For(box);
            if (pa.IsCurrent || ReferenceEquals(pa, pb2) || PreparedBody.Builds != bb0 + 1) fails.Add("edited body: the old preparation is still used");
            if (pa.Classify(new Vector3d(5, 5, 5)) != PointClass.Inside) fails.Add("stale preparation does not answer for the current state");
            Log(new { test = "KRN10-PREPARED-BODY", bodies = bodies.Count, queries, builds, pruned, plainMs = Math.Round(tPlain), preparedMs = Math.Round(tPrep), mism });
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
static partial class Program
{
    static void UnifyProf()
    {
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        var w = new List<string>();
        var pl = new List<TSolid> { Box(0, 0, 0, 400, 60, 20) };
        for (int k = 0; k < 40; k++) BooleanOp.Subtract(pl, PrimitiveOp.Cylinder(new Vector3d(5 + 10 * k, 15 + 30 * (k % 2), -5), 3, 30), w);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        BooleanOp.Subtract(pl, TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 4, 80), TransformOp.Rotate(new Vector3d(212, 30, 10), Vector3d.UnitY, 25)), w);
        Console.WriteLine($"plate tilted cut {sw.ElapsedMilliseconds} ms");
        using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticGeneral = true }).Enter())
        { var p = RealisticPart(8001, 20, out _); p.Rebuild(null); }
        foreach (bool gen in new[] { false, true })
        {
            var a = new List<TSolid> { Box(0, 0, 0, 210, 20, 10) };
            for (int k = 0; k < 10; k++) BooleanOp.Subtract(a, Box(10 + 20 * k, -1, 5, 10, 22, 6), w);
            var fill = new List<TSolid> { Box(10, 0, 5, 10, 20, 5) };
            for (int k = 1; k < 10; k++) BooleanOp.Unite(fill, Box(10 + 20 * k, 0, 5, 10, 20, 5), w);
            Parametric3D.Kernel.Diagnostics.Prof.Reset();
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { AnalyticGeneral = gen }).Enter())
                foreach (var f in fill) BooleanOp.Unite(a, f, w);
            Console.WriteLine($"comb gen {gen}: {sw2.ElapsedMilliseconds} ms, bodies {a.Count}, faces {a.Sum(b => b.Faces.Count())}, fill bodies {fill.Count}");
            foreach (var l in Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains("слиян") || l.Contains("объединение сторон") || l.Contains("целиком"))) Console.WriteLine(l);
        }
        foreach (var l in Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Where(l => l.Contains("общая")).Take(30)) Console.WriteLine(l);
    }
}
static partial class Program
{
    static void Krn10UnifyLocalChecks()
    {
        Check("KRN10-SAME-DOMAIN-LOCAL", () =>
        {
            // 10.6: слияние граней одной несущей с локальным обновлением соседства. Брусок, верхняя и нижняя грани которого
            // отпечатками разрезаны на полосы (по 16), сливается обратно в 6 граней: результат тот же, что у полного пересчёта
            // после каждого слияния (порядок граней, имена, контуры, вершины, объём), а проверок пар — линейно, не квадратично.
            var fails = new List<string>();
            TSolid Striped()
            {
                TSolid b = Box(0, 0, 0, 170, 20, 10); string? why = null;
                for (int k = 1; k <= 16; k++)
                {
                    double x = 10 * k;
                    foreach (var (p, q, probe) in new[] { (new Vector3d(x, 0, 10), new Vector3d(x, 20, 10), new Vector3d(x - 5, 10, 10)), (new Vector3d(x, 0, 0), new Vector3d(x, 20, 0), new Vector3d(x - 5, 10, 0)) })
                    {
                        var face = b.Faces.First(f => Parametric3D.Kernel.Analytic.AnalyticFace.Of(f, out _) is { } af && af.SurfaceDistance(probe) < 1e-9 && af.Contains(probe) == true);
                        b = ImprintOp.Apply(b, face, new[] { p, q }, null, ref why) ?? throw new Exception("imprint: " + why);
                    }
                }
                return b;
            }
            string Sig(TSolid s) => string.Join(";", s.Faces.Select(f => $"{f.Tag}|{f.Loops.Count}|{string.Join(",", f.Loops.Select(l => l.CoEdges.Count))}|{string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(c => $"{c.Start.P.X:R}/{c.Start.P.Y:R}/{c.Start.P.Z:R}"))}"));
            var a = Striped(); var b = Striped();
            int faces0 = a.Faces.Count();
            long c0 = Parametric3D.Kernel.Analytic.GeneralBoolean.UnifyChecks;
            int na = Parametric3D.Kernel.Analytic.GeneralBoolean.UnifySameDomain(a);
            long cLocal = Parametric3D.Kernel.Analytic.GeneralBoolean.UnifyChecks - c0;
            Parametric3D.Kernel.Analytic.GeneralBoolean.UnifyOldForTest = true;
            long c1 = Parametric3D.Kernel.Analytic.GeneralBoolean.UnifyChecks; int nb;
            try { nb = Parametric3D.Kernel.Analytic.GeneralBoolean.UnifySameDomain(b); }
            finally { Parametric3D.Kernel.Analytic.GeneralBoolean.UnifyOldForTest = false; }
            long cFull = Parametric3D.Kernel.Analytic.GeneralBoolean.UnifyChecks - c1;
            int edges0 = 0;
            Log(new { test = "KRN10-SAME-DOMAIN-LOCAL", faces0, faces = a.Faces.Count(), merges = na, checksLocal = cLocal, checksFull = cFull });
            if (a.Faces.Count() != 6) fails.Add($"{faces0} faces merged into {a.Faces.Count()}, want 6");
            if (na != nb || Sig(a) != Sig(b)) fails.Add($"local and full recomputation differ: {na} vs {nb} merges");
            var va = OpVolume(a); if (Math.Abs(va - 170 * 20 * 10) > 1e-7) fails.Add($"volume {va:R}");
            var val = SolidValidator.Validate(a); if (!val.IsValid) fails.Add("invalid: " + val.Summary() + " " + string.Join(" | ", val.Issues.Take(3).Select(i => i.ToString())));
            if (cLocal * 4 > cFull) fails.Add($"{cLocal} pair checks vs {cFull} with full recomputation — not local");
            _ = edges0;
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
