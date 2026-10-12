using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    static void Krn07HoleContractChecks()
    {
        Check("KRN07-HOLE-ROUTE-CONTRACT", () =>
        {
            // 07.2 ЕДИНЫЙ КОНТРАКТ ОТВЕРСТИЯ: одно и то же отверстие особым маршрутом (соосные квадрики, наклонное, у края,
            // поперёк вала) и общей булевой (ExactHoles=false) — тот же объём (1e-9 отн.), объявленный уровень гарантии (не
            // Unspecified), те же имена граней отверстия (роли по геометрии, а не по маршруту), та же длина кромок Rim.
            var fails = new List<string>();
            foreach (var (name, build) in HoleContractCases())
            {
                var (p1, h1) = build();
                (Part P, HoleFeature H) g;
                using (new KernelContext().WithExtension(ModelSettings.Capture() with { ExactHoles = false }).Enter()) g = build();
                string route = h1.LastRoutes[0][^1].Route, groute = g.H.LastRoutes[0][^1].Route;
                if (route == "general-boolean") { fails.Add($"{name}: the special route was not taken ({Trace(h1)})"); continue; }
                if (groute != "general-boolean") { fails.Add($"{name}: ExactHoles=false still took {groute}"); continue; }
                if (h1.Error != null || g.H.Error != null) { fails.Add($"{name}: error {h1.Error ?? g.H.Error}"); continue; }
                double v1 = p1.Volume, v2 = g.P.Volume;
                if (Math.Abs(v1 - v2) > 1e-9 * Math.Abs(v2)) fails.Add($"{name}: volume {v1:R} vs general {v2:R}");
                foreach (var (tag, p) in new[] { (route, p1), ("general", g.P) })
                    if (p.Bodies.Any(b => b.Guarantee == GuaranteeLevel.Unspecified)) fails.Add($"{name}: {tag} body has no declared guarantee");
                string Roles(Part p, HoleFeature h) => string.Join(",", p.Bodies.SelectMany(b => b.Faces).Where(f => f.Tag.StartsWith("#" + h.Key + "/"))
                    .Select(f => EdgeRef.Bare(f.Tag)).OrderBy(x => x, StringComparer.Ordinal));
                string r1 = Roles(p1, h1), r2 = Roles(g.P, g.H);
                if (r1 != r2) fails.Add($"{name}: hole faces {route} [{r1}] vs general [{r2}]");
                double Rim(Part p, HoleFeature h) => new EdgeSelector(EdgeSelectorKind.Rim, h.Key).Resolve(p.Bodies).Sum(e => e.Curve.Length);
                double l1 = Rim(p1, h1), l2 = Rim(g.P, g.H);
                if (l2 <= 0 || Math.Abs(l1 - l2) > 1e-3 * l2) fails.Add($"{name}: rim length {l1:F6} vs general {l2:F6}");
            }

            // ССЫЛКА НА КРОМКУ ПЕРЕЖИВАЕТ СМЕНУ МАРШРУТА: скругление по выбранной кромке входа отверстия (ссылка — имена
            // граней и точки), затем ось отверстия наклонена на 3° — отверстие уходит с соосного пути на точную булеву по
            // пересечениям (прежде имя стенки менялось «quadric:hole» → «exact:hole-wall», и ссылка рвалась); затем точные
            // пути отключены — общая булева. Во всех трёх скругление строится, объём убывает на величину порядка r²·L.
            {
                var (p, h) = HoleInCube(new Vector3d(0, 0, 30), new Vector3d(0, 0, -1), 10, true);
                var rim = new EdgeSelector(EdgeSelectorKind.Rim, h.Key).Resolve(p.Bodies)
                    .Where(e => e.Curve.PointAt(0.5).Z > 29).ToList();
                var rf = new RoundFeature("R") { Radius = new ValueRef(1) };
                rf.Edges.AddRange(rim.Select(EdgeRef.Of)); p.AddFeature(rf); p.Rebuild(null);
                double v0 = p.Volume; string? e0 = rf.Error;
                double a3 = 3 * Math.PI / 180;
                h.Direction = new Vector3d(Math.Sin(a3), 0, -Math.Cos(a3)); p.MarkDirtyFrom(h); p.Rebuild(null);
                double v1 = p.Volume; string? e1 = rf.Error; string tr1 = Trace(h);
                double vg = double.NaN; string? eg = null;
                using (new KernelContext().WithExtension(ModelSettings.Capture() with { ExactHoles = false }).Enter())
                { p.MarkDirtyFrom(h); p.Rebuild(null); vg = p.Volume; eg = rf.Error; }
                Console.WriteLine($"  rim ref: {rim.Count} edge(s); axial V {v0:R} err {e0 ?? "-"}; tilted ({tr1}) V {v1:R} err {e1 ?? "-"}; general V {vg:R} err {eg ?? "-"}");
                if (e0 != null || e1 != null || eg != null) fails.Add($"rim reference lost: axial {e0 ?? "ok"}; tilted {e1 ?? "ok"}; general {eg ?? "ok"}");
                else if (Math.Abs(vg - v1) > 1e-3 * Math.Abs(v1)) fails.Add($"rounded tilted hole: volume {v1:R} vs general {vg:R}");
            }
            // ССЫЛКА ИЗ ДОКУМЕНТА ДО ЕДИНОГО КОНТРАКТА: имена граней отверстия — прежние, по маршруту («…/quadric:hole»).
            {
                var (p, h) = HoleInCube(new Vector3d(0, 0, 30), new Vector3d(0, 0, -1), 10, true);
                static string Old(string? t) => t == null ? "" : t.Replace("hole:wall", "quadric:hole");
                var refs = new EdgeSelector(EdgeSelectorKind.Rim, h.Key).Resolve(p.Bodies).Where(e => e.Curve.PointAt(0.5).Z > 29)
                    .Select(EdgeRef.Of).Select(r => new EdgeRef(r.Start, r.End, r.Mid, Old(r.FaceA), Old(r.FaceB), Old(r.End0), Old(r.End1), r.Piece)).ToList();
                var rf = new RoundFeature("R") { Radius = new ValueRef(1) }; rf.Edges.AddRange(refs); p.AddFeature(rf); p.Rebuild(null);
                Console.WriteLine($"  legacy ref: {refs.Count} edge(s), faces {(refs.Count > 0 ? refs[0].FaceA + " | " + refs[0].FaceB : "-")}; err {rf.Error ?? "-"}");
                if (rf.Error != null) fails.Add($"legacy hole-face reference: {rf.Error}");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
