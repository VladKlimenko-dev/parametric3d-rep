using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

/// <summary>k116 (замечание 16:00): протяжка, зеркало зеркала, файл (видимость эскизов, ветки дерева, переводы строк), контур операции, длинный цилиндр.</summary>
static partial class Program
{
    /// <summary>Труба 200×200×L с отверстием R и карманом у верхнего края, задевающим стенку отверстия (как деталь пользователя).</summary>
    static Part Tube116(double len = 559.435, double r = 92.075)
    {
        var p = new Part("tube"); var doc = new Document(); doc.Root.Add(p);
        var s0 = RectSketch(p, 0, -100, -100, 200, 200);
        var ex = new ExtrudeFeature("Body") { SketchId = s0.Id }; ex.Side1.Distance = new ValueRef(len); ex.Side1.Flip = true; p.AddFeature(ex);
        var s1 = CircleSketch(p, 0, 0, 0, r);
        var bore = new ExtrudeFeature("Bore") { SketchId = s1.Id, Cut = true }; bore.Side1.End = EndType.ThroughAll; bore.Side1.Flip = true; p.AddFeature(bore);
        var s2 = RectSketch(p, 0, 60, -20, 50, 40);
        var pk = new ExtrudeFeature("Pocket") { SketchId = s2.Id, Cut = true }; pk.Side1.Distance = new ValueRef(57.15); pk.Side1.Flip = true; p.AddFeature(pk);
        return p;
    }

    static Part Cube116()
    {
        var p = new Part("cube"); var doc = new Document(); doc.Root.Add(p);
        TestGeometry.AddCube(p, 100);
        return p;
    }

    static ExtrudeFeature Pocket116(Part p, double x0, double y0, double w, double d, double depth)
    {
        var sk = RectSketch(p, 50, x0, y0, w, d);
        var e = new ExtrudeFeature("Pocket") { SketchId = sk.Id, Cut = true }; e.Side1.Distance = new ValueRef(depth); e.Side1.Flip = true;
        p.AddFeature(e);
        return e;
    }

    static void Krn116Checks()
    {
        Check("KRN-MIRROR-SWEEP-K116", () =>
        {
            // k116 (замечание 16:00, п. 3): протяжку-вырез отражают как операцию (прежде — «this operation cannot be repeated by a
            // mirror»). Канавка — полукруг R5 по верху куба вдоль X при y = 20; зеркало в плоскости y = 0 — вторая при y = −20.
            // Мутация P3D_SWEEPMIRROR=0.
            var p = Cube116(); p.Rebuild(null); double v0 = p.Volume;
            var path = new Sketch("Path", new Frame3(new Vector3d(0, 20, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(path);
            path.AddEntity(new SkLine(new SkPoint(-60, 50), new SkPoint(60, 50)));
            var sec = new Sketch("Sec", new Frame3(new Vector3d(-60, 20, 50), Vector3d.UnitY, Vector3d.UnitZ)); p.Add(sec);
            sec.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
            var sw = new SweepFeature("Groove") { SectionSketchId = sec.Id, PathSketchId = path.Id, Cut = true }; p.AddFeature(sw);
            p.Rebuild(null);
            double groove = v0 - p.Volume, expect = Math.PI * 25 / 2 * 100;
            Console.WriteLine($"  groove: error {sw.Error}, removed {groove:0.###} (expected {expect:0.###})");
            Require(sw.Error == null && Math.Abs(groove - expect) < 1e-3 * expect, "sweep groove: " + sw.Error);
            var m = new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitY }; m.Sources.Add(sw.Key); p.AddFeature(m);
            p.Rebuild(null);
            Console.WriteLine($"  mirrored: error {m.Error}, removed {v0 - p.Volume:0.###} (expected {2 * expect:0.###})");
            Require(m.Error == null && Math.Abs(v0 - p.Volume - 2 * expect) < 2e-3 * expect, "mirror of the sweep: " + m.Error);
            var hi = p.Bodies[0].Faces.SelectMany(f => f.Edges).Where(e => e.V0.P.Y < -14 && e.V0.P.Y > -26 && Math.Abs(e.V0.P.Z - 50) < 1e-6).ToList();
            Require(hi.Count >= 2, $"the mirrored groove is not at y = -20 ({hi.Count} edges)");
        });

        Check("KRN-MIRROR-NESTED-K116", () =>
        {
            // k116 (замечание 16:00, п. 4): ЗЕРКАЛО ЗЕРКАЛА — операция-зеркало повторяется другим зеркалом (её копии под
            // составным преобразованием): прежде отказ, а зеркало тела вместо него шло минутами. Карман (+,+), зеркало по X —
            // (−,+), зеркало этого зеркала по Y — (−,−): три кармана. Мутация P3D_REPLAYNEST=0.
            var p = Cube116(); p.Rebuild(null); double v0 = p.Volume;
            var pk = Pocket116(p, 20, 20, 10, 10, 10);
            var m1 = new MirrorFeature("M1") { At = Vector3d.Zero, Normal = Vector3d.UnitX }; m1.Sources.Add(pk.Key); p.AddFeature(m1);
            var m2 = new MirrorFeature("M2") { At = Vector3d.Zero, Normal = Vector3d.UnitY }; m2.Sources.Add(m1.Key); p.AddFeature(m2);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.Rebuild(null);
            Console.WriteLine($"  nested: errors {pk.Error}/{m1.Error}/{m2.Error}, removed {v0 - p.Volume:0.###} (expected 3000), {sw.ElapsedMilliseconds} ms");
            Require(m1.Error == null && m2.Error == null && Math.Abs(v0 - p.Volume - 3000) < 1e-3, "mirror of a mirror: " + m2.Error);
            // карман (−,−) есть, (+,−) — нет
            bool At(double x, double y) => p.Bodies[0].Faces.Any(f => f.Edges.Any(e => Math.Abs(e.V0.P.X - x) < 1e-6 && Math.Abs(e.V0.P.Y - y) < 1e-6 && Math.Abs(e.V0.P.Z - 40) < 1e-6));
            Require(At(-20, -20) && !At(20, -20), "pocket positions");
        });

        Check("KRN-FILE-STATE-K116", () =>
        {
            // k116 (замечание 16:00, пп. 7, 8): видимость эскизов, заданная пользователем, и развёрнутые ветки дерева — в файле;
            // подпись поверхностей из файла не зависит от переводов строк (файл Windows открывается без перестроения картинки).
            // Мутации P3D_SKETCHVIS=0, P3D_TREESTATE=0, P3D_FILENEWLINE=0.
            var p = Cube116(); var pk = Pocket116(p, 20, 20, 10, 10, 10);
            var free = RectSketch(p, 0, -80, -80, 10, 10);                      // эскиз без операции — по умолчанию виден
            p.Rebuild(null);
            var used = p.Sketches.First(s0 => s0.Id == pk.SketchId);           // эскиз операции — по умолчанию скрыт
            Require(!used.Visible && free.Visible, $"defaults: used {used.Visible}, free {free.Visible}");
            free.Visible = false; free.VisibleUserSet = true;
            used.Visible = true; used.VisibleUserSet = true;
            p.TreeState["f:" + pk.Key] = true; p.TreeState["i:group:G"] = false;
            var json = DocumentFile.Save(p, null, withDisplay: true);
            var (q, _) = DocumentFile.Load(json);
            var d2 = new Document(); d2.Root.Add(q); q.Rebuild(null);
            var qf = q.Sketches.First(s0 => s0.Name == free.Name); var qu = q.Sketches.First(s0 => s0.Name == used.Name);
            Console.WriteLine($"  loaded: free sketch visible {qf.Visible}, used sketch visible {qu.Visible}, tree {string.Join(",", q.TreeState.Select(kv => kv.Key + "=" + kv.Value))}");
            Require(!qf.Visible && qu.Visible, "sketch visibility was not restored");
            Require(q.TreeState.GetValueOrDefault("f:" + pk.Key) && q.TreeState.TryGetValue("i:group:G", out bool g) && !g, "tree state was not restored");
            // переводы строк: файл, записанный в Windows (подпись k115 — по тексту с \r\n), открыт здесь — поверхности принимаются
            long r0 = DocumentFile.DisplayRejected;
            string lf = json.Replace("\r\n", "\n"); int di = lf.IndexOf(",\n  \"Display\"", StringComparison.Ordinal);
            Require(di > 0, "no stored surfaces in the file");
            int de = lf.IndexOf("\n  }", di, StringComparison.Ordinal) + 4;
            string def = lf[..di] + lf[de..];         // описание модели — файл без сетки показа
            string sigWin = DocumentFile.SignatureRawForTest(def.Replace("\n", "\r\n")), sigLin = DocumentFile.SignatureRawForTest(def);
            var winJson = json.Replace("\r\n", "\n").Replace(sigLin, sigWin).Replace("\n", "\r\n");
            Require(json.Contains(sigLin) && winJson.Contains(sigWin), "signature not found in the file");
            var (w, _) = DocumentFile.Load(winJson);
            var (u, _) = DocumentFile.Load(json);
            Console.WriteLine($"  Windows file: cached {w.ShowsCached}, this system: cached {u.ShowsCached}, rejected {DocumentFile.DisplayRejected - r0}");
            Require(w.ShowsCached && u.ShowsCached, "the stored surfaces were rejected because of line endings");
        });

        Check("KRN-TREE-OWN-K116", () =>
        {
            // k116 (замечание 16:00, п. 6): контур операции при наведении — только то, что она добавила или убрала: рёбра её
            // граней. Карман через кромку куба режет кромку верхней грани на куски — прежняя разность рёбер «до/после» давала
            // и эти куски (линии вдоль всей кромки). Мутация P3D_TREEOWN=0.
            var p = Cube116(); var pk = Pocket116(p, 40, -10, 20, 20, 10);
            p.Rebuild(null);
            Require(pk.Error == null && Math.Abs(1e6 - p.Volume - 2000) < 1e-3, $"pocket: {pk.Error} {p.Volume}");
            var diff = p.MaterialDiff(pk);
            Require(diff is { } dd && dd.Lines.Count > 0 && !dd.Adds, "no material contour");
            var pts = diff!.Value.Lines.SelectMany(l => new[] { l.A, l.B }).ToList();
            bool inside(Vector3d x) => x.X > 40 - 1e-6 && x.X < 50 + 1e-6 && x.Y > -10 - 1e-6 && x.Y < 10 + 1e-6 && x.Z > 40 - 1e-6 && x.Z < 50 + 1e-6;
            int outside = pts.Count(x => !inside(x));
            Console.WriteLine($"  contour: {diff.Value.Lines.Count} segments, {outside} points outside the pocket");
            Require(outside == 0, $"{outside} contour points outside the removed material");
        });

        Check("KRN-LONG-CYLINDER-K116", () =>
        {
            // k116 (замечание 16:00, п. 1): деталь пользователя до 4-й операции — длинная труба R92 × 559 мм, вырез у края:
            // грань трубы размечается развёрткой, сжатой вдоль оси (прежде конформная разметка упиралась в предел точек,
            // операция уходила на многогранный путь: 50 с вместо 9 и огранённый вид). Мутация P3D_CYLANISO=0.
            string path = Fixture("user_k116.p3d");
            var (p, prm) = DocumentFile.Load(File.ReadAllText(path));
            var doc = new Document(); doc.Root.Add(p);
            for (int i = 4; i < p.Features.Count; i++) p.Features[i].Suppressed = true;
            long a0 = Parametric3D.Kernel.Tess.ExactFaceMesher.AnisoCylFaces;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.Rebuild(prm);
            var faceted = p.Warnings.Where(w0 => w0.Contains("Geometry downgrade")).ToList();
            Console.WriteLine($"  user part, 4 operations: {sw.ElapsedMilliseconds} ms, anisotropic cylinder faces {Parametric3D.Kernel.Tess.ExactFaceMesher.AnisoCylFaces - a0}, downgrades {faceted.Count}");
            Require(p.Features.Take(4).All(f => f.Error == null), "errors: " + string.Join("; ", p.Features.Select(f => f.Error)));
            Require(faceted.Count == 0 && Parametric3D.Kernel.Tess.ExactFaceMesher.AnisoCylFaces > a0, "the long cylinder went to the polyhedral path: " + string.Join("; ", faceted));
        });
    }

    static void Krn116Probe()
    {
        long a0 = Parametric3D.Kernel.Tess.ExactFaceMesher.AnisoCylFaces;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = Tube116(); p.Rebuild(null);
        Console.WriteLine($"tube: {sw.ElapsedMilliseconds} ms, volume {p.Volume:0.###}, errors {string.Join(";", p.Features.Select(f => f.Error))}, aniso {Parametric3D.Kernel.Tess.ExactFaceMesher.AnisoCylFaces - a0}, downgrades {string.Join(",", p.Downgrades.Select(kv => kv.Key.Name + ":" + kv.Value.Faces))}");
        foreach (var f in p.Features) Console.WriteLine($"  {f.Name} {(p.LastFeatureMs.TryGetValue(f, out var ms) ? ms : -1):0} ms");
    }
}
