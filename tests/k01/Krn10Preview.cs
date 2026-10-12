using Parametric3D.Model;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn10PreviewChecks()
    {
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
            int pending = p.Bodies.Count(b => b.IsPreview);
            var vols = p.Bodies.Select(b => AnalyticVolume.Of(b)).ToList();
            if (pending == 0) fails.Add("preview edit left no pending body (the last operation did not take a deferred route)");
            if (rp.StartedAt < p.Features.Count - 1) fails.Add($"preview rebuilt from operation {rp.StartedAt}, not the last");
            var rn = p.Compute(null); p.Apply(rn);
            if (p.Bodies.Any(b => b.IsPreview)) fails.Add("ordinary rebuild kept preview bodies unaccepted");
            if (!p.Bodies.Select(b => AnalyticVolume.Of(b)).SequenceEqual(vols)) fails.Add("accepted bodies differ from the preview");
            if (rn.Executed != 0) fails.Add($"ordinary rebuild after preview executed {rn.Executed} operation(s)");
            Log(new { test = "KRN10-PART-PREVIEW", pending, startedAt = rp.StartedAt, executedAfter = rn.Executed });
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN10-MESH-DONOR-FROM-LOOPS", () =>
        {
            // g.13: правка последней операции детали — грани, чьи контуры не изменились, берут сетку у той же грани
            // прежнего тела (ключ: поверхность по содержанию, контуры побитно, точность гранения). Прежде донором годилась
            // только грань с сеткой отображения, а у точных маршрутов её нет — доноров не было вовсе (0 из 59 граней).
            // Сетки граней — побитно те же, что без доноров (KernelSettings.ReuseFaceMeshes = false).
            var fails = new List<string>();
            foreach (int seed in new[] { 1, 3 })
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
                if (diff != 0) fails.Add($"seed {seed}: {diff} face mesh(es) differ from the build without donors");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });

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
    }
}
