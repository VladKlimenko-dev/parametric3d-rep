using Parametric3D.Model;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// Проверка механизма общей булевой на реалистичных деталях (слияние рёбер, проверки сетки, доноры): с местной плитой
    /// (KRN-10, QuadricCut) отверстия этих деталей идут точным путём, и механизм на них не работает — проверка шла бы
    /// впустую. Здесь — прежнее правило плиты, как при замере механизма.
    /// </summary>
    static void OldSlab(Action a)
    {
        Parametric3D.Kernel.Ops.QuadricCut.LocalOffForTests = true;
        try { a(); } finally { Parametric3D.Kernel.Ops.QuadricCut.LocalOffForTests = false; }
    }

    static void Krn10PreviewChecks()
    {
        Check("KRN10-MERGE-EDGES-SAME", () => OldSlab(() =>
        {
            // 10.7: слияние рёбер на прямой и дуге без пересчёта всего тела после каждого стыка — тот же результат, что прежнее
            // (пластины K08 3 зерна, полное перестроение: те же грани, те же рёбра — концы и вид кривой, тот же объём), и
            // меньше выделений памяти на перестройку
            var fails = new List<string>();
            static List<string> Sig(IEnumerable<TSolid> bs) => bs.SelectMany(b => b.Faces).Select(f => f.Tag + ":" + string.Join(";", f.Loops.Select(l => string.Join(",", l.CoEdges.Select(c =>
                { var a = c.Start.P; var z = c.End.P; return $"{c.Edge.Curve.GetType().Name[0]}{a.X:R}/{a.Y:R}/{a.Z:R}>{z.X:R}/{z.Y:R}/{z.Z:R}"; }).OrderBy(x => x, StringComparer.Ordinal))))).OrderBy(x => x, StringComparer.Ordinal).ToList();
            double allocOld = 0, allocNew = 0;
            foreach (int seed in new[] { 1, 3, 6 })
            {
                var p = RealisticPart(seed, 40, out _);
                GC.Collect(); long a0 = GC.GetTotalAllocatedBytes(true);
                p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null);
                allocNew += (GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0;
                var sNew = Sig(p.Bodies); var vNew = p.Volume;
                Parametric3D.Kernel.Analytic.PrismBuild.MergeOldForTest = true;
                try
                {
                    GC.Collect(); a0 = GC.GetTotalAllocatedBytes(true);
                    p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null);
                    allocOld += (GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0;
                }
                finally { Parametric3D.Kernel.Analytic.PrismBuild.MergeOldForTest = false; }
                var sOld = Sig(p.Bodies);
                int diff = sNew.Zip(sOld).Count(z => z.First != z.Second) + Math.Abs(sNew.Count - sOld.Count);
                if (diff != 0 && Environment.GetEnvironmentVariable("MERGEDBG") == "1") foreach (var z in sNew.Zip(sOld).Where(z => z.First != z.Second).Take(2)) Console.WriteLine($"NEW {z.First}\nOLD {z.Second}");
                if (diff != 0 || vNew != p.Volume) fails.Add($"seed {seed}: {diff} face(s) differ, volume {vNew:R} vs {p.Volume:R}");
            }
            Log(new { test = "KRN10-MERGE", allocOldMb = allocOld, allocNewMb = allocNew });
            if (!(allocNew < 0.9 * allocOld)) fails.Add($"allocation saving under 10%: {allocNew:F0} vs {allocOld:F0} MB");
            Require(fails.Count == 0, string.Join("; ", fails));
        }));

        Check("KRN10-MESH-CHECKS-SAME", () => OldSlab(() =>
        {
            // g.13: быстрые проверки сетки тела (грубый ключ повторов граней, стороны без владельцев, слияние точек с памятью)
            // дают ту же сетку отображения, что прежние (P3D_MESHCHECKFAST=0), и быстрее
            var fails = new List<string>();
            static List<string> MeshSig(IEnumerable<TSolid> bs) => bs.SelectMany(b => b.Faces).Select(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs && fs.Display is { } d
                ? f.Tag + ":" + string.Join(";", d.Select(t => $"{t.Item1.X:R},{t.Item1.Y:R},{t.Item1.Z:R},{t.Item2.X:R},{t.Item2.Y:R},{t.Item2.Z:R},{t.Item3.X:R},{t.Item3.Y:R},{t.Item3.Z:R}"))
                : f.Tag + ":-").OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (int seed in new[] { 1, 4, 6 })
            {
                var p = RealisticPart(seed, 40, out _);
                p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null);
                var sNew = MeshSig(p.Bodies);
                BooleanOp.MeshChecksFastOffForTests = true;
                try { p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null); }
                finally { BooleanOp.MeshChecksFastOffForTests = false; }
                var sOld = MeshSig(p.Bodies);
                int diff = sNew.Zip(sOld).Count(z => z.First != z.Second) + Math.Abs(sNew.Count - sOld.Count);
                if (diff != 0) fails.Add($"seed {seed}: {diff} face mesh(es) differ");
                if (sNew.All(x => x.EndsWith(":-"))) fails.Add($"seed {seed}: no display meshes to compare");
            }
            // выигрыш — на самих проверках: выделения памяти в вызывающем потоке (проверки последовательны; разметка граней —
            // в других потоках) при разметке копий тела каждым способом; память детерминирована, время на 2 ядрах — нет
            var body = RealisticPart(1, 40, out _); body.MarkDirtyFrom(body.Features[0]); body.Rebuild(null);
            var solid = body.Bodies[0];
            (double Mb, double Ms) M(bool off)
            {
                BooleanOp.MeshChecksFastOffForTests = off;
                try
                {
                    var copies = Enumerable.Range(0, 5).Select(_ => TopologyClone.Deep(solid)).ToList();
                    foreach (var c in copies) foreach (var f in c.Faces) foreach (var e in f.Edges) Parametric3D.Kernel.Tess.ExactFaceMesher.EdgePoints(e);
                    long a0 = GC.GetAllocatedBytesForCurrentThread(); var sw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var c in copies) BooleanOp.MeshExactlyForTest(c);
                    return ((GC.GetAllocatedBytesForCurrentThread() - a0) / 1048576.0, sw.Elapsed.TotalMilliseconds);
                }
                finally { BooleanOp.MeshChecksFastOffForTests = false; }
            }
            M(false); M(true);
            var mNew = M(false); var mOld = M(true);
            Log(new { test = "KRN10-MESH-CHECKS", newMb = mNew.Mb, oldMb = mOld.Mb, newMs = mNew.Ms, oldMs = mOld.Ms });
            if (!(mNew.Mb < 0.92 * mOld.Mb)) fails.Add($"no saving: {mNew.Mb:F1} vs {mOld.Mb:F1} MB in the checking thread");
            Require(fails.Count == 0, string.Join("; ", fails));
        }));

        Check("KRN10-REFINE-BY-SHAPE", () =>
        {
            // g.13: точки кривой пересечения уточняются проекцией по аналитической форме поверхностей (q = x − F·∇F) — те же
            // точки, что прежней проекцией в параметры (тор × цилиндр: тела вращения), до 1e-10 мм, на обеих поверхностях.
            // Мутация P3D_REFINESHAPE=0 (счётчик проекций по форме не растёт).
            var fails = new List<string>();
            var c = new Vector3d(1, 2, 3);
            var bl = new List<TSolid> { PrimitiveOp.Torus(c, 10, 3) };
            BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(c + new Vector3d(10, 0, -10), 1.5, 20), new List<string>());
            var edges = bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3).Select(e => (Parametric3D.Kernel.Curves.IntersectionCurve3)e.Curve!).ToList();
            if (edges.Count == 0) fails.Add("no traced edges");
            var tor = new Parametric3D.Kernel.Geometry.Ssi.Torus(c, Vector3d.UnitZ, 10, 3);
            var cyl = new Parametric3D.Kernel.Geometry.Ssi.Cylinder(c + new Vector3d(10, 0, 0), Vector3d.UnitZ, 1.5);
            double worst = 0, off = 0; long n0 = Parametric3D.Kernel.Curves.IntersectionCurve3.ShapeProjections;
            foreach (var ic in edges)
            {
                var seed = Enumerable.Range(0, 17).Select(i => ic.PointAt(i / 16.0)).ToList();
                var fresh = new Parametric3D.Kernel.Curves.IntersectionCurve3(ic.A, ic.B, seed);
                Parametric3D.Kernel.Curves.IntersectionCurve3.OldProjectionForTests = true;
                Parametric3D.Kernel.Curves.IntersectionCurve3 old;
                try { old = new Parametric3D.Kernel.Curves.IntersectionCurve3(ic.A, ic.B, seed); old.PointAt(0.5); }
                finally { Parametric3D.Kernel.Curves.IntersectionCurve3.OldProjectionForTests = false; }
                for (int i = 1; i < 40; i++)
                {
                    double t = i / 40.0 + 0.003; var p = fresh.PointAt(t); var q = old.PointAt(t);
                    worst = Math.Max(worst, (p - q).Length);
                    off = Math.Max(off, Math.Max(Math.Abs(Parametric3D.Kernel.Geometry.Ssi.Implicit(tor, p).F), Math.Abs(Parametric3D.Kernel.Geometry.Ssi.Implicit(cyl, p).F)));
                }
            }
            long used = Parametric3D.Kernel.Curves.IntersectionCurve3.ShapeProjections - n0;
            Log(new { test = "KRN10-REFINE-BY-SHAPE", worst, off, used });
            if (worst > 1e-10) fails.Add($"points differ from the parametric projection by {worst:G3} mm");
            if (off > 1e-11) fails.Add($"refined points off the surfaces by {off:G3} mm");
            if (used == 0) fails.Add("no projection by the analytic shape");
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN10-AUDIT-CACHE-SAME", () =>
        {
            // g.13: аудит геометрии с памятью (отход кривой от поверхности — по паре неизменяемых объектов; ошибка гранения —
            // у замороженной сетки) — те же уровни и отходы, что без памяти; память срабатывает. Мутация P3D_AUDITCACHE=0.
            var fails = new List<string>();
            static string Sig(GeometryAudit a) => string.Join(";", a.Faces.Select(f => $"{f.Face}:{f.Level}:{f.ChordError:R}")) + "|" + string.Join(";", a.Edges.Select(e => $"{e.Edge}:{e.Level}:{e.OnA:R}:{e.OnB:R}"));
            long h0 = GeometryAudit.CacheHits;
            foreach (int seed in new[] { 1, 4 })
            {
                var p = RealisticPart(seed, 40, out _); p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null);
                foreach (var b in p.Bodies)
                {
                    var s1 = Sig(GeometryAudit.Of(b)); var s2 = Sig(GeometryAudit.Of(b));
                    GeometryAudit.CacheOffForTests = true;
                    string s0; try { s0 = Sig(GeometryAudit.Of(b)); } finally { GeometryAudit.CacheOffForTests = false; }
                    if (s1 != s0 || s2 != s0) fails.Add($"seed {seed}: audit with memory differs");
                }
            }
            long hits = GeometryAudit.CacheHits - h0;
            Log(new { test = "KRN10-AUDIT-CACHE", hits });
            if (hits == 0) fails.Add("the audit memory was never used");
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN10-CDT-BOX-SAME", () =>
        {
            // g.13: отбор отверстий по габариту при проверке середины треугольника (CDT плоской грани) — те же треугольники,
            // обходов контуров меньше: квадрат 200×200 с 100 круглыми отверстиями. Мутация P3D_CDTBOX=0.
            var fails = new List<string>();
            var outer = new List<Vector2d> { new(-100, -100), new(100, -100), new(100, 100), new(-100, 100) };
            var holes = new List<IReadOnlyList<Vector2d>>();
            for (int i = 0; i < 100; i++)
            {
                var c = new Vector2d(-90 + 18 * (i % 10) + 0.37 * (i % 3), -90 + 18 * (i / 10)); var h = new List<Vector2d>();
                for (int k = 0; k < 24; k++) { double a = -2 * Math.PI * k / 24; h.Add(c + new Vector2d(Math.Cos(a), Math.Sin(a)) * 2); }
                holes.Add(h);
            }
            Parametric3D.Kernel.Tess.Cdt.LoopWalks = 0; var t1 = Parametric3D.Kernel.Tess.Cdt.Triangulate(outer, holes, out var p1); long w1 = Parametric3D.Kernel.Tess.Cdt.LoopWalks;
            Parametric3D.Kernel.Tess.Cdt.BoxOffForTests = true;
            List<int>? t0; List<Vector2d> p0; long w0;
            try { Parametric3D.Kernel.Tess.Cdt.LoopWalks = 0; t0 = Parametric3D.Kernel.Tess.Cdt.Triangulate(outer, holes, out p0); w0 = Parametric3D.Kernel.Tess.Cdt.LoopWalks; }
            finally { Parametric3D.Kernel.Tess.Cdt.BoxOffForTests = false; }
            Log(new { test = "KRN10-CDT-BOX", walksNew = w1, walksOld = w0, tris = t1?.Count / 3 });
            if (t1 == null || t0 == null || !t1.SequenceEqual(t0) || !p1.SequenceEqual(p0)) fails.Add("triangulations differ");
            if (!(w1 < 0.5 * w0)) fails.Add($"loop walks {w1} vs {w0}");
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN10-HISTORY-BY-REFERENCE", () =>
        {
            // g.13: история дерева — грани и рёбра, скопированные теми же объектами, сравниваются без строковых подписей;
            // история (созданные, изменённые, разделённые, удалённые грани и рёбра) та же, что прежняя. Мутация P3D_HISTREF=0.
            var fails = new List<string>(); long h0 = FeatureHistory.HistRefHits;
            static string H(Part p, Part.BuildResult r) => string.Join("\n", p.Features.Select(f => r.History.TryGetValue(f, out var h) ? $"{f.Name}: G[{string.Join(",", h.Generated)}] M[{string.Join(",", h.Modified)}] S[{string.Join(",", h.Split)}] D[{string.Join(",", h.Deleted)}] EG[{string.Join(",", h.EdgesGenerated)}] EM[{string.Join(",", h.EdgesModified)}] ED[{string.Join(",", h.EdgesDeleted)}]" : f.Name + ": -"));
            foreach (int seed in new[] { 1, 3, 6 })
            {
                var p = RealisticPart(seed, 40, out _);
                p.MarkDirtyFrom(p.Features[0]); var rn = p.Compute(null); p.Apply(rn); var hNew = H(p, rn);
                FeatureHistory.HistRefOffForTests = true;
                Part.BuildResult ro;
                try { p.MarkDirtyFrom(p.Features[0]); ro = p.Compute(null); p.Apply(ro); } finally { FeatureHistory.HistRefOffForTests = false; }
                if (H(p, ro) != hNew) fails.Add($"seed {seed}: history differs");
            }
            long hits = FeatureHistory.HistRefHits - h0;
            Log(new { test = "KRN10-HISTORY-BY-REFERENCE", hits });
            if (hits == 0) fails.Add("no group compared by reference");
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN10-PART-PREVIEW-ACCEPT", () =>
        {
            // деталь: правка последней операции в предпросмотре — тело помечено; следующий обычный расчёт без изменений
            // принимает его (отложенные проверки) и не перестраивает дерево; предпросмотр не сбрасывает сохранённые состояния
            var fails = new List<string>();
            var p = RealisticPart(1, 20, out _);
            var last = p.Features[^1];
            p.MarkDirtyFrom(last);
            Part.BuildResult rp;
            using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) { rp = p.Compute(null); p.Apply(rp); }
            // g.13: проверки призматического маршрута в предпросмотре отложены (P3D_PREVIEWPRISM=0 — сразу): брусок минус
            // цилиндр — тело помечено, принятие проходит, объём тот же, что без предпросмотра
            {
                List<TSolid> Cut() { var bl = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), new List<string>()); return bl; }
                List<TSolid> pb; long dp = BooleanOp.PrismChecksDeferred;
                using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) pb = Cut();
                if (BooleanOp.PrismChecksDeferred == dp || !pb[0].IsPreview) fails.Add("the prism route checks were not deferred in preview");
                else if (BooleanOp.AcceptPreview(pb[0]) is { } why) fails.Add("prism preview not accepted: " + why);
                if (AnalyticVolume.Of(pb[0]) != AnalyticVolume.Of(Cut()[0])) fails.Add("prism preview body differs");
            }
            int pending = p.Bodies.Count(b => b.IsPreview);
            var vols = p.Bodies.Select(b => AnalyticVolume.Of(b)).ToList();
            if (pending == 0) fails.Add("preview edit left no pending body (the last operation did not take a deferred route)");
            if (rp.StartedAt < p.Features.Count - 1) fails.Add($"preview rebuilt from operation {rp.StartedAt}, not the last");
            bool pendingVal = rp.ValidationPending; int valAtPreview = rp.Validation.Count;
            var rn = p.Compute(null); p.Apply(rn);
            if (p.Bodies.Any(b => b.IsPreview)) fails.Add("ordinary rebuild kept preview bodies unaccepted");
            if (!p.Bodies.Select(b => AnalyticVolume.Of(b)).SequenceEqual(vols)) fails.Add("accepted bodies differ from the preview");
            if (rn.Executed != 0) fails.Add($"ordinary rebuild after preview executed {rn.Executed} operation(s)");
            // g.13: проверка сетки тел в предпросмотре отложена (P3D_PREVIEWVALIDATE=0 — сразу) и выполнена при принятии
            if (Part.ValidateResults)
            {
                if (!pendingVal || valAtPreview != 0) fails.Add($"preview validated the bodies at once ({valAtPreview} report(s)) instead of deferring");
                if (rn.ValidationPending || rn.Validation.Count != rn.Bodies.Count) fails.Add($"acceptance ran {rn.Validation.Count} body validation(s) for {rn.Bodies.Count} body(ies)");
                if (rn.Validation.Any(v => !v.IsValid) != (rn.IntegrityError != null)) fails.Add("deferred validation outcome not reflected in the integrity error");
            }
            Log(new { test = "KRN10-PART-PREVIEW", pending, startedAt = rp.StartedAt, executedAfter = rn.Executed });
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN10-MESH-DONOR-FROM-LOOPS", () => OldSlab(() =>
        {
            // g.13: правка последней операции детали — грани, чьи контуры не изменились, берут сетку у той же грани
            // прежнего тела (ключ: поверхность по содержанию, контуры побитно, точность гранения). Прежде донором годилась
            // только грань с сеткой отображения, а у точных маршрутов её нет — доноров не было вовсе (0 из 59 граней).
            // Сетки граней — побитно те же, что без доноров (KernelSettings.ReuseFaceMeshes = false).
            var fails = new List<string>();
            foreach (int seed in new[] { 1, 3, 4, 6 })
            {
                var p = RealisticPart(seed, 40, out _);
                var last = p.Features[^1];
                // полное перестроение (как после открытия файла): у граней нет сетки отображения
                p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null);
                Parametric3D.Kernel.Export.StepImport.DonorReused = 0;
                p.MarkDirtyFrom(last); p.Rebuild(null);
                int reused = Parametric3D.Kernel.Export.StepImport.DonorReused;
                var withDonors = p.Bodies.ToList();
                p.MarkDirtyFrom(last);
                using (new KernelContext(KernelSettings.Capture() with { ReuseFaceMeshes = false }).Enter()) p.Rebuild(null);
                var plain = p.Bodies.ToList();
                static List<string> Sig(IEnumerable<TSolid> bs) => bs.SelectMany(b => b.Faces).Select(f => f.Tag + ":" + (f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs
                    ? string.Join(";", fs.Facets.ReadView.Select(t => $"{t.A.X:R},{t.A.Y:R},{t.A.Z:R},{t.B.X:R},{t.B.Y:R},{t.B.Z:R},{t.C.X:R},{t.C.Y:R},{t.C.Z:R}").OrderBy(x => x, StringComparer.Ordinal)) : "-")).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var s1 = Sig(withDonors); var s2 = Sig(plain);
                int diff = s1.Zip(s2).Count(z => z.First != z.Second) + Math.Abs(s1.Count - s2.Count);
                Log(new { test = "KRN10-MESH-DONOR", seed, reused, faces = s1.Count, differing = diff });
                if (reused == 0) fails.Add($"seed {seed}: no face took a donor mesh");
                // g.13: ключ без направления кривой отрезка находит больше доноров, чем прежний
                p.MarkDirtyFrom(p.Features[0]); p.Rebuild(null);
                BooleanOp.DonorCanonOffForTests = true;
                Parametric3D.Kernel.Export.StepImport.DonorReused = 0;
                try { p.MarkDirtyFrom(last); p.Rebuild(null); } finally { BooleanOp.DonorCanonOffForTests = false; }
                int reusedOld = Parametric3D.Kernel.Export.StepImport.DonorReused;
                Log(new { test = "KRN10-MESH-DONOR-KEY", seed, reused, reusedOld });
                if (seed == 1 && !(reused > reusedOld)) fails.Add($"seed {seed}: the line-direction-free key reuses {reused}, the old one {reusedOld}");
                if (diff != 0) fails.Add($"seed {seed}: {diff} face mesh(es) differ from the build without donors");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        }));

        Check("KRN10-PREVIEW-ACCEPT", () =>
        {
            // g.13 (решение пользователя 2026-10-12): предпросмотр строит ту же точную булеву, проверки результата маршрута
            // (B-rep/сетка, p-кривые, объём по формулам и по фасетам, согласие с CSG) откладываются до принятия. Случайные
            // пары (брусок/цилиндр/конус/шар/тор, повороты) — где обычный режим принимает общий точный маршрут: тело
            // предпросмотра то же (объём побитно, число граней), помечено «не принято», принятие проходит и снимает отметку.
            // Отложенная проверка, которая отказывает, — принятие возвращает причину, тело остаётся непринятым.
            var fails = new List<string>();
            var rnd = new Random(20261012);
            double U(double a, double b) => a + (b - a) * rnd.NextDouble();
            Func<TSolid> Prim()
            {
                int k = rnd.Next(5); var at = new Vector3d(U(-8, 8), U(-8, 8), U(-8, 8));
                double p1 = U(2, 12), p2 = U(8, 30), p3 = U(1.5, 5);
                bool rot = rnd.Next(2) == 0; var axis = new Vector3d(U(-1, 1), U(-1, 1), U(-1, 1)); if (axis.Length < 0.1) axis = Vector3d.UnitX; double deg = U(0, 180);
                return () =>
                {
                    TSolid s = k switch
                    {
                        0 => Box(at.X - p2 / 2, at.Y - p2 / 3, at.Z - p2 / 2.5, p2, p2 / 1.5, p2 / 1.25),
                        1 => PrimitiveOp.Cylinder(at - new Vector3d(0, 0, p2 / 2), p1, p2),
                        2 => PrimitiveOp.Cone(at - new Vector3d(0, 0, p2 / 2), p1, p1 * 0.4, p2),
                        3 => PrimitiveOp.Sphere(at, p1),
                        _ => PrimitiveOp.Torus(at, p3 + p1 * 0.6 + 1, p3),
                    };
                    return rot ? TransformOp.Copy(s, TransformOp.Rotate(at, axis.Normalized(), deg)) : s;
                };
            }
            int general = 0, accepted = 0; double msFull = 0, msPrev = 0, msAccept = 0;
            for (int i = 0; i < 24; i++)
            {
                double w = U(20, 40), d = U(20, 40), h = U(15, 30);
                Func<TSolid> A = () => Box(-w / 2, -d / 2, -h / 2, w, d, h); var B = Prim();
                int kind = rnd.Next(3);
                void Op(List<TSolid> bl, TSolid t, List<string> ws) { if (kind == 0) BooleanOp.Subtract(bl, t, ws); else if (kind == 1) BooleanOp.Unite(bl, t, ws); else BooleanOp.Intersect(bl, t, ws); }
                var wf = new List<string>(); var full = new List<TSolid> { A() }; var tb = B();
                var sw = System.Diagnostics.Stopwatch.StartNew(); Op(full, tb, wf); sw.Stop();
                if (full.Count != 1 || !wf.Any(x => x.Contains("general analytic path"))) continue;
                general++; msFull += sw.Elapsed.TotalMilliseconds;
                var wp = new List<string>(); var prev = new List<TSolid> { A() }; var tb2 = B();
                sw.Restart();
                using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) Op(prev, tb2, wp);
                sw.Stop(); msPrev += sw.Elapsed.TotalMilliseconds;
                if (prev.Count != 1) { fails.Add($"case {i}: preview gave {prev.Count} bodies"); continue; }
                var p = prev[0]; var f = full[0];
                double? vp = AnalyticVolume.Of(p), vf = AnalyticVolume.Of(f);
                if (!p.IsPreview) { fails.Add($"case {i}: preview body not marked"); continue; }
                if (f.IsPreview) { fails.Add($"case {i}: full-mode body marked as preview"); continue; }
                if (vp != vf || p.Faces.Count() != f.Faces.Count()) { fails.Add($"case {i}: preview differs: V {vp:R} vs {vf:R}, faces {p.Faces.Count()} vs {f.Faces.Count()}"); continue; }
                sw.Restart(); var why = BooleanOp.AcceptPreview(p); sw.Stop(); msAccept += sw.Elapsed.TotalMilliseconds;
                if (why != null || p.IsPreview) { fails.Add($"case {i}: acceptance failed: {why}"); continue; }
                if (AnalyticVolume.Of(p) != vf) { fails.Add($"case {i}: accepted body changed"); continue; }
                accepted++;
                if (accepted == 1)
                {
                    // отказ отложенной проверки: принятие обязано вернуть причину
                    var prev2 = new List<TSolid> { A() };
                    using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) Op(prev2, B(), new List<string>());
                    BooleanOp.ForceAcceptFailure = true;
                    try { var w2 = BooleanOp.AcceptPreview(prev2[0]); if (w2 == null || !prev2[0].IsPreview) fails.Add("failing deferred check was accepted"); }
                    finally { BooleanOp.ForceAcceptFailure = false; }
                }
            }
            // прямой построитель скруглений и фасок: те же отложенные проверки (B-rep и сетка, объём, объём по фасетам)
            int rounds = 0;
            foreach (var (name, make, pick, rr, chf) in new (string, Func<TSolid>, Func<TSolid, List<EdgeRef>>, double, bool)[]
            {
                ("box all edges R3", () => Box(0, 0, 0, 60, 40, 20), b => b.Edges.Select(EdgeRef.Of).ToList(), 3, false),
                ("box top loop C2", () => Box(0, 0, 0, 60, 40, 20), b => b.Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9).Select(EdgeRef.Of).ToList(), 2, true),
                ("hole rim R2", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 6, 30), new List<string>()); return bl[0]; },
                    b => b.Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-9).Select(EdgeRef.Of).ToList(), 2, false),
            })
            {
                TSolid? Run(bool preview)
                {
                    var b0 = make(); var bl = new List<TSolid> { b0 }; var ws = new List<string>();
                    using (preview ? (IDisposable)new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter() : null)
                        if (chf) FilletOp.ChamferEdges(bl, pick(b0), rr, rr, ws); else FilletOp.RoundEdges(bl, pick(b0), rr, ws);
                    return ws.Any(x => x.Contains("direct")) ? bl[0] : null;
                }
                var f = Run(false); var p = Run(true);
                if (f == null || p == null) { fails.Add($"{name}: direct builder not used (full {f != null}, preview {p != null})"); continue; }
                if (!p.IsPreview || f.IsPreview) { fails.Add($"{name}: preview marks wrong ({p.IsPreview}/{f.IsPreview})"); continue; }
                if (AnalyticVolume.Of(p) != AnalyticVolume.Of(f) || p.Faces.Count() != f.Faces.Count()) { fails.Add($"{name}: preview body differs"); continue; }
                if (BooleanOp.AcceptPreview(p) is { } why || p.IsPreview) { fails.Add($"{name}: acceptance failed"); continue; }
                rounds++;
            }
            Log(new { test = "KRN10-PREVIEW", general, rounds, accepted, msFull, msPreview = msPrev, msAccept, speedup = msPrev > 0 ? msFull / msPrev : 0 });
            if (general < 8) fails.Add($"only {general} cases took the general analytic path");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN10-PAIRS-PARALLEL-SAME", () =>
        {
            // g.13: пересечения пар граней общей булевой — параллельно (2 потока), разбор — в прежнем порядке: тело то же
            // до бита (объём, площади граней, координаты вершин), что при одном потоке, и пары действительно шли
            // одновременно. Мутация P3D_PAIRPAR=0.
            var fails = new List<string>();
            List<TSolid> Run(int threads)
            {
                using (new KernelContext(KernelSettings.Capture() with { BooleanThreads = threads }).Enter())
                {
                    var bl = new List<TSolid> { PrimitiveOp.Torus(new Vector3d(1, 2, 3), 10, 3) };
                    BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(11, 2, -7), 1.5, 20), new List<string>());
                    BooleanOp.Subtract(bl, Box(-20, -20, 4, 40, 40, 10), new List<string>());
                    return bl;
                }
            }
            Parametric3D.Kernel.Analytic.GeneralBoolean.PairsMaxConcurrent = 0;
            var p2 = Run(2); long conc = Parametric3D.Kernel.Analytic.GeneralBoolean.PairsMaxConcurrent;
            var p1 = Run(1);
            string Sig(List<TSolid> b) => string.Join("|", b.Select(x => $"{AnalyticVolume.Of(x):R};{string.Join(",", x.Vertices.Select(v => $"{v.P.X:R},{v.P.Y:R},{v.P.Z:R}").OrderBy(z => z))}"));
            bool same = Sig(p1) == Sig(p2);
            Log(new { test = "KRN10-PAIRS-PARALLEL", same, maxConcurrent = conc, guarantee = string.Join(",", p2.Select(x => x.Guarantee)) });
            if (!same) fails.Add("two-thread result differs from one-thread");
            if (Environment.ProcessorCount > 1 && conc < 2) fails.Add($"pairs were not computed concurrently (max {conc})");
            if (p2.Any(x => x.Guarantee != GuaranteeLevel.ExactAnalytic)) fails.Add("not exact");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
