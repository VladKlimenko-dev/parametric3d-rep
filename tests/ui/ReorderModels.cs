using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

/// <summary>
/// k104: детали для проверки перестановки операций в дереве — разные формы и масштабы, со скруглениями и фасками.
/// </summary>
static class ReorderModels
{
    static Document Doc(Part p) { var d = new Document(); d.Root.Add(p); return d; }

    static void Rect(Sketch s, double x0, double y0, double x1, double y1)
    {
        foreach (var (a, b) in new[] { ((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)), ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0)) })
            s.AddEntity(new SkLine(new SkPoint(a.Item1, a.Item2), new SkPoint(b.Item1, b.Item2)));
    }

    static ExtrudeFeature Ext(Part p, Sketch s, double d, bool cut = false, bool flip = false)
    {
        var e = new ExtrudeFeature { SketchId = s.Id, Cut = cut };
        e.Side1.Distance = new ValueRef(d); e.Side1.Flip = flip;
        p.AddFeature(e);
        return e;
    }

    static List<EdgeRef> Edges(Part p, Func<TEdge, bool> pred) => p.Bodies.SelectMany(b => b.Edges).Where(pred).Select(EdgeRef.Of).ToList();
    static bool Line(TEdge e) => e.Curve is Parametric3D.Kernel.Curves.LineCurve3;

    static RoundFeature Round(Part p, double r, List<EdgeRef> edges) { var f = new RoundFeature { Radius = new ValueRef(r) }; f.Edges.AddRange(edges); p.AddFeature(f); return f; }
    static ChamferFeature Chamfer(Part p, double d, List<EdgeRef> edges) { var f = new ChamferFeature { D1 = new ValueRef(d), D2 = new ValueRef(d) }; f.Edges.AddRange(edges); p.AddFeature(f); return f; }

    static void Build(Document d) { d.RebuildDirty(); }

    /// <summary>Брусок, сквозное отверстие, скругление вертикальных рёбер, фаска верхних, бобышка сверху со скруглением.</summary>
    public static (Document, Part) BoxHoleBoss(double k)
    {
        var p = new Part("box"); var d = Doc(p);
        var s1 = p.AddSketch("S1", Frame3.XY, p.PlaneXY.Id); Rect(s1, 0, 0, 60 * k, 40 * k); Ext(p, s1, 20 * k); Build(d);
        var s2 = p.AddSketch("S2", Frame3.XY, p.PlaneXY.Id); s2.AddEntity(new SkCircle(new SkPoint(20 * k, 20 * k), 6 * k)); Ext(p, s2, 30 * k, cut: true); Build(d);
        Round(p, 3 * k, Edges(p, e => Line(e) && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 19 * k)); Build(d);
        Chamfer(p, 1.5 * k, Edges(p, e => Line(e) && e.V0.P.Z > 20 * k - 1e-6 && e.V1.P.Z > 20 * k - 1e-6)); Build(d);
        var top = new Frame3(new Vector3d(0, 0, 20 * k), Vector3d.UnitX, Vector3d.UnitY);
        var s3 = p.AddSketch("S3", top); s3.AddEntity(new SkCircle(new SkPoint(42 * k, 20 * k), 6 * k)); Ext(p, s3, 10 * k); Build(d);
        Round(p, 1 * k, Edges(p, e => !Line(e) && e.V0.P.Z > 30 * k - 1e-6)); Build(d);
        return (d, p);
    }

    /// <summary>Уголок (профиль L), вырез-паз, скругление внутреннего угла, фаски наружных рёбер.</summary>
    public static (Document, Part) Bracket(double k)
    {
        var p = new Part("bracket"); var d = Doc(p);
        var s1 = p.AddSketch("S1", Frame3.XY, p.PlaneXY.Id);
        var pts = new (double, double)[] { (0, 0), (50 * k, 0), (50 * k, 8 * k), (8 * k, 8 * k), (8 * k, 40 * k), (0, 40 * k) };
        for (int i = 0; i < pts.Length; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Length]; s1.AddEntity(new SkLine(new SkPoint(a.Item1, a.Item2), new SkPoint(b.Item1, b.Item2))); }
        Ext(p, s1, 30 * k); Build(d);
        Round(p, 4 * k, Edges(p, e => Line(e) && Math.Abs(e.V0.P.X - 8 * k) < 1e-6 && Math.Abs(e.V0.P.Y - 8 * k) < 1e-6 && Math.Abs(e.V1.P.X - 8 * k) < 1e-6 && Math.Abs(e.V1.P.Y - 8 * k) < 1e-6)); Build(d);
        var s2 = p.AddSketch("S2", Frame3.XY, p.PlaneXY.Id); Rect(s2, 25 * k, -1 * k, 35 * k, 10 * k); Ext(p, s2, 12 * k, cut: true); Build(d);
        Chamfer(p, 1 * k, Edges(p, e => Line(e) && Math.Abs(e.V0.P.Z - 30 * k) < 1e-6 && Math.Abs(e.V1.P.Z - 30 * k) < 1e-6 && (Math.Abs(e.V0.P.X) < 1e-6 && Math.Abs(e.V1.P.X) < 1e-6))); Build(d);
        Round(p, 2 * k, Edges(p, e => Line(e) && Math.Abs(e.V0.P.X - 50 * k) < 1e-6 && Math.Abs(e.V1.P.X - 50 * k) < 1e-6 && Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-6 && Math.Abs(e.V0.P.Y - 8 * k) < 1e-6)); Build(d);
        return (d, p);
    }

    /// <summary>Пластина с двумя отверстиями, скругление углов, фаска отверстия, второе отверстие после фаски.</summary>
    public static (Document, Part) Plate(double k)
    {
        var p = new Part("plate"); var d = Doc(p);
        var s1 = p.AddSketch("S1", Frame3.XY, p.PlaneXY.Id); Rect(s1, 0, 0, 80 * k, 50 * k); Ext(p, s1, 6 * k); Build(d);
        Round(p, 8 * k, Edges(p, e => Line(e) && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 5 * k)); Build(d);
        var s2 = p.AddSketch("S2", Frame3.XY, p.PlaneXY.Id); s2.AddEntity(new SkCircle(new SkPoint(20 * k, 25 * k), 5 * k)); Ext(p, s2, 10 * k, cut: true); Build(d);
        Chamfer(p, 1 * k, Edges(p, e => !Line(e) && e.V0.P.Z > 6 * k - 1e-6 && Math.Abs(e.Curve.PointAt(0.5).X - 20 * k) < 6 * k)); Build(d);
        var s3 = p.AddSketch("S3", Frame3.XY, p.PlaneXY.Id); s3.AddEntity(new SkCircle(new SkPoint(60 * k, 25 * k), 7 * k)); Ext(p, s3, 10 * k, cut: true); Build(d);
        // кромка второго отверстия снизу (касательная цепочка верхних рёбер через дуги углов зависела бы от порядка — так задумано)
        Round(p, 0.5 * k, Edges(p, e => !Line(e) && e.V0.P.Z < 1e-6 && Math.Abs(e.Curve.PointAt(0.5).X - 60 * k) < 8 * k)); Build(d);
        return (d, p);
    }

    /// <summary>Площадь граней, созданных операцией (по сетке; подпись грани начинается с «#ключ/»).</summary>
    public static double AreaOf(Part p, Feature f)
    {
        if (p.Mesh is not { } m) return 0;
        var ids = p.Bodies.SelectMany(b => b.Faces).Where(x => x.Tag != null && x.Tag.StartsWith("#" + f.Key + "/", StringComparison.Ordinal)).Select(x => x.Id).ToHashSet();
        double a = 0;
        foreach (var (id, r) in m.FaceRanges)
        {
            if (!ids.Contains(id)) continue;
            for (int i = r.IndexStart; i + 2 < r.IndexStart + r.IndexCount; i += 3)
            {
                var v0 = m.Vertices[m.Indices[i]].P; var v1 = m.Vertices[m.Indices[i + 1]].P; var v2 = m.Vertices[m.Indices[i + 2]].P;
                a += OpenTK.Mathematics.Vector3.Cross(v1 - v0, v2 - v0).Length / 2;
            }
        }
        return a;
    }

    public static IEnumerable<(string Name, Func<(Document, Part)> Make)> All()
    {
        foreach (double k in new[] { 0.2, 1.0, 7.0 })
        {
            yield return ($"box×{k}", () => BoxHoleBoss(k));
            yield return ($"bracket×{k}", () => Bracket(k));
            yield return ($"plate×{k}", () => Plate(k));
        }
    }

    /// <summary>
    /// Все допустимые перестановки (по диапазону range) — каждая на копии детали: перестройка по отметке (как в окне) и с нуля
    /// должны дать одно и то же; операции, строившиеся до перестановки, — без ошибок; скругления и фаски — на всех своих рёбрах
    /// (объём близок к исходному, когда операции не пересекаются). Возвращает список отклонений.
    /// </summary>
    public static List<string> CheckAll(Func<Part, Feature, (int Lo, int Hi)> range, bool verbose = false)
    {
        var bad = new List<string>();
        foreach (var (name, make) in All())
        {
            var (d0, p0) = make();
            var errs0 = p0.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}").ToList();
            if (errs0.Count > 0) { bad.Add($"{name}: setup errors: {string.Join("; ", errs0)}"); continue; }
            string json = Parametric3D.Model.DocumentFile.Save(p0);
            double v0 = p0.Volume;
            var area0 = p0.Features.Where(x => x is RoundFeature or ChamferFeature).ToDictionary(x => x.Key, x => AreaOf(p0, x));
            for (int i = 0; i < p0.Features.Count; i++)
            {
                var (lo, hi) = range(p0, p0.Features[i]);
                for (int to = lo; to <= hi; to++)
                {
                    if (to == i) continue;
                    var (p, _) = Parametric3D.Model.DocumentFile.Load(json);
                    var d = new Document(); d.Root.Add(p); d.RebuildDirty();
                    var f = p.Features[i];
                    string what = $"{name}: {f.Name} {i}→{to}";
                    p.MoveFeature(f, to);
                    d.RebuildDirty();
                    var errs = p.Features.Where(x => x.Error != null).Select(x => $"{x.Name}: {x.Error}").ToList();
                    var (pf, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(p));
                    var df = new Document(); df.Root.Add(pf); df.RebuildDirty();
                    double vi = p.Volume, vf = pf.Volume;
                    if (verbose) Console.WriteLine($"{what}: V {vi:0.###} full {vf:0.###} orig {v0:0.###} errs {string.Join("; ", errs)}");
                    if (errs.Count > 0) bad.Add($"{what}: errors {string.Join("; ", errs)}");
                    if (Math.Abs(vi - vf) > 1e-6 * Math.Max(1, vf)) bad.Add($"{what}: incremental {vi:0.######} vs full {vf:0.######}");
                    if (Math.Abs(vi - v0) > 0.01 * v0) bad.Add($"{what}: volume {vi:0.###} vs original {v0:0.###}");
                    var fw = p.Warnings.Where(w => w.Contains("no longer", StringComparison.OrdinalIgnoreCase) || w.Contains("not found", StringComparison.OrdinalIgnoreCase) || w.Contains("skipped", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (fw.Count > 0) bad.Add($"{what}: warnings {string.Join("; ", fw)}");
                    // скругления и фаски — на всех своих рёбрах: площадь их граней почти та же (меняются только углы встречи)
                    if (errs.Count == 0)
                        foreach (var bf in p.Features.Where(x => x is RoundFeature or ChamferFeature))
                        {
                            double a0 = area0.TryGetValue(bf.Key, out var aa) ? aa : 0, a1 = AreaOf(p, bf);
                            if (a0 <= 0 || Math.Abs(a1 - a0) > 0.1 * a0) bad.Add($"{what}: {bf.Name} faces area {a1:0.###} vs {a0:0.###}");
                        }
                }
            }
        }
        return bad;
    }
}
