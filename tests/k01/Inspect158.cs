using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>k158: разбор файла: тело из файла и тело полного перестроения — объём, проверка, сетка в STL (ASCII) для просмотра.</summary>
    static void InspectFile(string file, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        void Dump(string tag)
        {
            int i = 0;
            foreach (var b in p.Bodies)
            {
                var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
                var rep = SolidValidator.ValidateBRep(b);
                Console.WriteLine($"{tag} body {i}: g {b.Guarantee}, faces {b.Faces.Count()}, tris {m.Indices.Length / 3}, broken {m.BrokenFaces.Count}, valid {rep.IsValid}, issues {string.Join(" | ", rep.Issues.Take(6))}");
                using var w = new StreamWriter(Path.Combine(outDir, $"{tag}_{i}.stl"));
                w.WriteLine("solid s");
                for (int t = 0; t < m.Indices.Length; t += 3)
                {
                    w.WriteLine("facet normal 0 0 0\nouter loop");
                    for (int k = 0; k < 3; k++) { var v = m.Vertices[m.Indices[t + k]].P; w.WriteLine(FormattableString.Invariant($"vertex {v.X} {v.Y} {v.Z}")); }
                    w.WriteLine("endloop\nendfacet");
                }
                w.WriteLine("endsolid s");
                i++;
            }
        }
        Console.WriteLine($"restored {p.RestoredFromFile}, volume {p.Volume:0.###}");
        if (p.RestoredFromFile) Dump("file");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(pars);
        Console.WriteLine($"rebuild {sw.ElapsedMilliseconds} ms, volume {p.Volume:0.###}");
        foreach (var f in p.Features.Where(f => f.Error != null)) Console.WriteLine($"ERROR {f.Name}: {f.Error}");
        foreach (var w in p.Warnings) Console.WriteLine($"warn {w}");
        Dump("rebuilt");
    }
}

static partial class Program
{
    /// <summary>k161: чертёж файла (4 вида по умолчанию) — картинка листа для разбора видов.</summary>
    static void DrawPng(string file, string png, string? isoStyle = null)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (!p.RestoredFromFile) p.Rebuild(pars);
        var dd = new Parametric3D.Model.Drawing.DrawingDocument(); dd.EnsureViews();
        // k163: чертёж из файла (.p3drw) вместо видов по умолчанию
        if (isoStyle != null && isoStyle.EndsWith(".p3drw")) { dd = Parametric3D.Model.Drawing.DrawingDocument.FromJson(File.ReadAllText(isoStyle)); isoStyle = null; }
        if (isoStyle != null) dd.Views.First(v => v.Name == "iso").Style = isoStyle;
        if (Environment.GetEnvironmentVariable("P3D_ISODIR") is { Length: > 0 } idir) dd.Views.First(v => v.Name == "iso").Dir = idir;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sc = Parametric3D.Model.Drawing.DrawingScene.Build(dd, p.Bodies);
        var g = dd.Compose(sc, new Parametric3D.Model.Drawing.FieldSource());
        if (Environment.GetEnvironmentVariable("P3D_DBGEDGE") is { } de)
            foreach (var (vn, ls) in sc.Lines)
                foreach (var l in ls.Where(l => l.SourceId.ToString() == de))
                    Console.WriteLine($"{vn}: {(l.Visible ? "V" : "h")} {l.Kind} pts {l.Points.Count} sheet {string.Join(" ", l.Points.Take(3).Select(q => dd.ToSheet(vn, q)).Select(q => $"({q.X:0.##},{q.Y:0.##})"))}");
        Console.WriteLine($"scene+compose {sw.ElapsedMilliseconds} ms, scale {dd.LastScale}, views {string.Join("; ", dd.Views.Select(v => $"{v.Name} {v.X:0},{v.Y:0}"))}");
        // ломаные листа в JSON (картинку рисует tools-скрипт): [[kind, x0, y0, x1, y1, …], …]
        var polys = g.Polylines.Select(q => new object[] { q.Kind.ToString() }.Concat(q.Points.SelectMany(t => new object[] { Math.Round(t.X, 3), Math.Round(t.Y, 3) })).ToArray())
            .Concat(g.Lines.Select(l => new object[] { l.Kind.ToString(), l.X1, l.Y1, l.X2, l.Y2 })).ToList();
        File.WriteAllText(png, System.Text.Json.JsonSerializer.Serialize(new { g.Width, g.Height, polys, shades = g.Shades.Select(sh => new object[] { sh.Argb }.Concat(sh.Pts.SelectMany(t => new object[] { t.X, t.Y })).ToArray()) }));
    }
}

static partial class Program
{
    /// <summary>k163: разбор линий вида у края: рёбра тела, чья проекция — в полосе X вида [x0, x1], их участки (видимые/нет).</summary>
    static void ViewLinesDbg(string file, string view, double x0, double x1)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (!p.RestoredFromFile) p.Rebuild(pars);
        var f = Parametric3D.Model.Drawing.ViewFrame.Of(view);
        var body = p.Bodies.Single();
        var lines = Parametric3D.Model.Drawing.ViewProjection.Project(body, f);
        foreach (var e in body.Edges.Where(e => e.Curve != null))
        {
            var pts = Enumerable.Range(0, 9).Select(k => f.Project(e.Curve!.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 8.0))).ToList();
            if (pts.Any(q => q.X < x0 || q.X > x1)) continue;
            var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().Select(fc => $"{fc.Id}:{fc.Surface?.GetType().Name}").ToList();
            var parts = lines.Where(l => l.SourceId == e.Id).Select(l => $"{(l.Visible ? "V" : "h")}{l.Kind}[{l.Points.Min(q => q.Y):0.#}..{l.Points.Max(q => q.Y):0.#}]").ToList();
            Console.WriteLine($"edge {e.Id} {e.Curve!.GetType().Name} y {pts.Min(q => q.Y):0.#}..{pts.Max(q => q.Y):0.#} x {pts.Min(q => q.X):0.##}..{pts.Max(q => q.X):0.##} faces {string.Join(",", faces)} -> {string.Join(" ", parts)}");
        }
        foreach (var l in lines.Where(l => l.Points.All(q => q.X >= x0 && q.X <= x1)))
            Console.WriteLine($"line edge {l.SourceId} {(l.Visible ? "V" : "h")} {l.Kind} y {l.Points.Min(q => q.Y):0.#}..{l.Points.Max(q => q.Y):0.#}");
        if (Environment.GetEnvironmentVariable("P3D_DBGDRW") is { } drw)
        {
            var dd = Parametric3D.Model.Drawing.DrawingDocument.FromJson(File.ReadAllText(drw));
            var sc = Parametric3D.Model.Drawing.DrawingScene.Build(dd, p.Bodies);
            foreach (var l in sc.Lines[view].Where(l => l.Points.All(q => q.X >= x0 && q.X <= x1)))
                Console.WriteLine($"scene edge {l.SourceId} {(l.Visible ? "V" : "h")} {l.Kind} y {l.Points.Min(q => q.Y):0.#}..{l.Points.Max(q => q.Y):0.#} dir {dd.DirOf(dd.Views.First(v => v.Name == view))}");
        }
    }
}

static partial class Program
{
    /// <summary>k163: автоматические размеры и размеры модели на чертеже файла, проверка полноты.</summary>
    static void DrawCheck(string file, string? drw)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (!p.RestoredFromFile) p.Rebuild(pars);
        var dd = drw != null ? Parametric3D.Model.Drawing.DrawingDocument.FromJson(File.ReadAllText(drw)) : new Parametric3D.Model.Drawing.DrawingDocument();
        dd.EnsureViews();
        var sc = Parametric3D.Model.Drawing.DrawingScene.Build(dd, p.Bodies);
        dd.Dims.Clear();
        dd.AutoDimensions(sc); int auto = dd.Dims.Count;
        dd.ModelDimensions(p); dd.ArrangeDims(sc);
        dd.Compose(sc, new Parametric3D.Model.Drawing.FieldSource());
        var items = Parametric3D.Model.Drawing.DrawingCheck.Run(dd, p);
        Console.WriteLine($"dims: auto {auto}, total {dd.Dims.Count}; roles {string.Join(",", dd.Dims.GroupBy(d => d.Role + (d.Model ? "model" : "")).Select(g => $"{g.Key}:{g.Count()}"))}");
        Console.WriteLine($"check: {items.Count} items, covered {items.Count(i => i.Covered)}");
        foreach (var it in items.Where(i => !i.Covered)) Console.WriteLine($"  missing {it.Kind} {it.Label}");
        foreach (var d in dd.Dims.Where(d => d.Auto)) Console.WriteLine($"  auto {d.View} {d.Kind} {d.Role} {d.Value:0.###} ({d.X1:0.##},{d.Y1:0.##})-({d.X2:0.##},{d.Y2:0.##})");
        foreach (var (vn, arcs) in sc.Arcs) Console.WriteLine($"  arcs {vn}: {string.Join(" ", arcs.Select(a => $"R{a.Radius:0.##}@({a.Center.X:0.##},{a.Center.Y:0.##}){(a.Full ? "F" : "")}"))}");
    }
}

static partial class Program
{
    /// <summary>k164: рёбра тела, чья проекция на вид проходит у точки (x, y) ближе r: тип кривой, ось, радиус.</summary>
    static void EdgesNear(string file, string view, double x, double y, double r)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (!p.RestoredFromFile) p.Rebuild(pars);
        var f = Parametric3D.Model.Drawing.ViewFrame.Of(view);
        foreach (var body in p.Bodies)
            foreach (var e in body.Edges.Where(e => e.Curve != null))
            {
                var c = e.Curve!; double best = 1e9;
                for (int k = 0; k <= 40; k++) { var q = f.Project(c.PointAt(c.T0 + (c.T1 - c.T0) * k / 40.0)); best = Math.Min(best, (q - new OpenTK.Mathematics.Vector2d(x, y)).Length); }
                if (best > r) continue;
                string extra = c is Parametric3D.Kernel.Curves.ArcCurve3 a ? $"R {a.Radius:0.###} c {f.Project(a.Frame.Origin)} W {a.Frame.W}" : "";
                var p0 = f.Project(c.PointAt(c.T0)); var p1 = f.Project(c.PointAt(c.T1));
                Console.WriteLine($"edge {e.Id}: {c.GetType().Name} {extra} from ({p0.X:0.###},{p0.Y:0.###}) to ({p1.X:0.###},{p1.Y:0.###}) d {best:0.###}");
            }
    }
}

static partial class Program
{
    /// <summary>
    /// k166: разрывы линий видов: короткие невидимые куски между видимыми кусками той же линии (ребро, очерк) — по направлениям
    /// взгляда (изометрия, повёрнутая вокруг оси Y через 30°, и с другой стороны). Печать: направление, число и длина разрывов.
    /// </summary>
    static void HlrGaps(string file, string? only = null)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (!p.RestoredFromFile) p.Rebuild(pars);
        var iso = Parametric3D.Model.Drawing.ViewFrame.Of("iso");
        for (int k = 0; k < 12; k++)
        {
            if (only != null && k.ToString() != only) continue;
            double a = k * Math.PI / 6;
            Vector3d Ry(Vector3d v) => new(v.X * Math.Cos(a) + v.Z * Math.Sin(a), v.Y, -v.X * Math.Sin(a) + v.Z * Math.Cos(a));
            var f = new Parametric3D.Model.Drawing.ViewFrame(Ry(iso.Right), Ry(iso.Up));
            var lines = Parametric3D.Model.Drawing.ViewProjection.ProjectMany(p.Bodies, f);
            int gaps = 0; double glen = 0; var kinds = new Dictionary<string, int>();
            static double Len(List<Vector2d> q) { double l = 0; for (int i = 1; i < q.Count; i++) l += (q[i] - q[i - 1]).Length; return l; }
            foreach (var grp in lines.GroupBy(l => (l.SourceId, l.Kind, l.Body)))
            {
                var vis = grp.Where(l => l.Visible).ToList();
                double total = grp.Sum(l => Len(l.Points));
                foreach (var h in grp.Where(l => !l.Visible))
                {
                    double hl = Len(h.Points);
                    if (hl > 0.05 * total || hl > 5) continue;
                    bool Touch(Vector2d e) => vis.Any(v => (v.Points[0] - e).Length < 1e-4 || (v.Points[^1] - e).Length < 1e-4);
                    if (Touch(h.Points[0]) && Touch(h.Points[^1]))
                    {
                        gaps++; glen += hl; kinds[grp.Key.Kind.ToString()] = kinds.GetValueOrDefault(grp.Key.Kind.ToString()) + 1;
                        if (only != null) Console.WriteLine($"  gap src {grp.Key.SourceId} {grp.Key.Kind} len {hl:0.###} at ({h.Points[0].X:0.###},{h.Points[0].Y:0.###})");
                    }
                }
            }
            Console.WriteLine($"dir {k * 30}°: lines {lines.Count}, gaps {gaps}, length {glen:0.##} mm, " + string.Join(", ", kinds.Select(x => $"{x.Key} {x.Value}")) + $" | v:{f.Right.X:0.####},{f.Right.Y:0.####},{f.Right.Z:0.####};{f.Up.X:0.####},{f.Up.Y:0.####},{f.Up.Z:0.####}");
        }
    }
}

static partial class Program
{
    /// <summary>k166: грани тела по виду поверхности (своей и точной — источника), площадь и число треугольников сетки.</summary>
    static void SrfStat(string file)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (!p.RestoredFromFile) p.Rebuild(pars);
        foreach (var b in p.Bodies)
        {
            var m = Parametric3D.Model.Drawing.ViewProjection.MeshOf(b);
            var tris = new Dictionary<uint, int>();
            for (int t = 0; t + 2 < m.Indices.Length; t += 3) { var f = m.Vertices[m.Indices[t]].FaceId; tris[f] = tris.GetValueOrDefault(f) + 1; }
            foreach (var g in b.Faces.GroupBy(f => $"{f.Surface.GetType().Name} -> {Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name ?? "null"}"))
                Console.WriteLine($"{g.Key}: faces {g.Count()}, tris {g.Sum(f => tris.GetValueOrDefault(f.Id))}");
            // точная нормаль против нормали сетки в вершинах
            var byId = b.Faces.ToDictionary(f => f.Id);
            var stat = new Dictionary<string, (int N, double Max, double Sum)>();
            for (int i = 0; i < m.Vertices.Length; i += 7)
            {
                var v = m.Vertices[i]; if (!byId.TryGetValue(v.FaceId, out var f)) continue;
                var srf = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f); if (srf == null) continue;
                var ne = srf.NormalAt(srf.ProjectToUV((Vector3d)v.P)).Normalized(); var nm = ((Vector3d)v.N).Normalized();
                double ang = Math.Acos(Math.Clamp(Math.Abs(Vector3d.Dot(ne, nm)), -1, 1)) * 180 / Math.PI;
                var pr = srf.PointAt(srf.ProjectToUV((Vector3d)v.P)); double dist = (pr - (Vector3d)v.P).Length;
                var k = srf.GetType().Name; var o = stat.GetValueOrDefault(k); stat[k] = (o.N + 1, Math.Max(o.Max, ang), o.Sum + dist);
            }
            foreach (var (k, o) in stat) Console.WriteLine($"  {k}: vertices {o.N}, max angle exact vs mesh normal {o.Max:0.##}°, mean projection distance {o.Sum / Math.Max(1, o.N):0.####}");
        }
    }
}
