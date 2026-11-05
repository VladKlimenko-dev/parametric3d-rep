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
