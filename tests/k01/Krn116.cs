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

        Check("KRN-TANGENT-JOINT-K116", () =>
        {
            // k116: стык касания отрезка и дуги в 2.5D-булевой — вершина эскиза, а не пересечение несущих (двойной корень:
            // вершина уходила на 6.7·10⁻⁷ мм, совпадающая стенка второго кармана с ней уже не сходилась). Мутация P3D_TANJOINT=0.
            var sp = new Part("slot"); var doc = new Document(); doc.Root.Add(sp); TestGeometry.AddCube(sp, 200);
            var sk = UserSlot116(sp, 100);
            var e = new ExtrudeFeature("Pocket") { SketchId = sk.Id, Cut = true }; e.Side1.Distance = new ValueRef(8); e.Side1.Flip = true; sp.AddFeature(e);
            sp.Rebuild(null);
            var exact = new Vector3d(50.453465880133756, 33.45815385549484, 92);
            double off = sp.Bodies.SelectMany(b => b.Vertices).Select(v => (v.P - exact).Length).Min();
            Console.WriteLine($"  slot pocket: error {e.Error}, tangent vertex off {off:G3}");
            Require(e.Error == null && off < 1e-9, $"the tangent joint vertex moved by {off:G3} mm");
        });

        Check("KRN-USER-EXACT-K116", () =>
        {
            // k116 (снимок 21:45): деталь пользователя целиком — ТОЧНЫМ путём. Прежде с 5-й операции — многогранный: двойные
            // стенки двух карманов с одним контуром (полосы при подсветке, пунктир в каркасе), огранённые поверхности. Причины:
            // точка-ссылка эскиза в 1.6·10⁻⁶ мм от ребра (P3D_EDGEREFFOLLOW), стык касания отрезка и дуги в призме (P3D_TANJOINT),
            // конец ребра совпадающей грани на касательной границе (P3D_BNDENDVERTEX), луч классификации вдоль ребра
            // (P3D_RAYSHIFT), торец клина уклона, касательный к отверстию (P3D_WEDGEMARGIN).
            var (p, prm) = DocumentFile.Load(File.ReadAllText(Fixture("user_k116.p3d")));
            var doc = new Document(); doc.Root.Add(p);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.Rebuild(prm);
            var faceted = p.Warnings.Where(w0 => w0.Contains("Geometry downgrade")).ToList();
            bool poly = p.Bodies.Any(b => b.Faces.Any(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs && fs.Source == null));
            Console.WriteLine($"  user part: {sw.ElapsedMilliseconds} ms, volume {p.Volume:0.###}, downgrades {faceted.Count}, errors {string.Join(";", p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error))}");
            foreach (var w0 in faceted) Console.WriteLine("    " + w0);
            Require(p.Features.All(f => f.Error == null), "operation errors");
            Require(faceted.Count == 0, "the part went to the polyhedral path: " + string.Join("; ", faceted));
            // двух совпадающих стенок нет: стенка кармана 0.312in снята стенкой зеркального кармана 0.375in
            // двойных стенок нет: грани на одной и той же поверхности (стенки карманов с одним контуром) у переднего торца не
            // накрывают одно место (ячейки «угол 1° × высота 0.5 мм» по центрам треугольников сетки показа)
            var md = p.Mesh!;
            var cells = new Dictionary<uint, (string Key, HashSet<(int, int)> C)>();
            var faceById = p.Bodies.SelectMany(b => b.Faces).ToDictionary(f => f.Id);
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
            {
                var va = md.Vertices[md.Indices[t]];
                if (!faceById.TryGetValue(va.FaceId, out var f)) continue;
                var src = (f.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source ?? f.Surface;
                if (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(src) is not Parametric3D.Kernel.Geometry.Ssi.Cylinder cy || Math.Abs(Math.Abs(cy.Axis.Normalized().Z) - 1) > 1e-9) continue;
                var cen = (va.P + md.Vertices[md.Indices[t + 1]].P + md.Vertices[md.Indices[t + 2]].P) / 3;
                if (cen.Z < -20) continue;
                if (!cells.TryGetValue(va.FaceId, out var cc)) cells[va.FaceId] = cc = ($"{cy.P.X:0.###},{cy.P.Y:0.###},{cy.R:0.####}", new());
                cc.C.Add(((int)Math.Round(Math.Atan2(cen.Y - cy.P.Y, cen.X - cy.P.X) * 180 / Math.PI), (int)Math.Round(cen.Z / 0.5)));
            }
            int overlaps = 0;
            foreach (var g in cells.GroupBy(kv => kv.Value.Key))
            {
                var l = g.ToList();
                for (int i = 0; i < l.Count; i++) for (int j = i + 1; j < l.Count; j++)
                {
                    int common = l[i].Value.C.Intersect(l[j].Value.C).Count();
                    if (common > 3) { overlaps++; Console.WriteLine($"    overlap on {g.Key}: faces {faceById[l[i].Key].Tag} / {faceById[l[j].Key].Tag}: {common} cells"); }
                }
            }
            Console.WriteLine($"  front wall faces: {cells.Count}, overlapping pairs {overlaps}");
            Require(overlaps == 0, "double walls (two faces on the same surface)");
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

    /// <summary>Два кармана с одним контуром (дуги и отрезки) с верхней грани: глубины d1 и d2 — стенки совпадают.</summary>
    static Part TwinPockets116(double d1 = 8, double d2 = 9.5, bool circle = false, bool groove = false, bool mirror = false)
    {
        var p = Cube116();
        if (groove)
        {
            // канавка-протяжка (конус/цилиндр) — тело не призма: точный путь — общий
            var path = new Sketch("Path", new Frame3(new Vector3d(0, 30, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(path);
            path.AddEntity(new SkLine(new SkPoint(-60, 50), new SkPoint(60, 50)));
            var sec = new Sketch("Sec", new Frame3(new Vector3d(-60, 30, 50), Vector3d.UnitY, Vector3d.UnitZ)); p.Add(sec);
            sec.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
            p.AddFeature(new SweepFeature("Groove") { SectionSketchId = sec.Id, PathSketchId = path.Id, Cut = true });
        }
        Sketch Prof(double z = 50)
        {
            var sk = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY)); p.Add(sk);
            if (circle) { sk.AddEntity(new SkCircle(new SkPoint(0, 0), 20)); return sk; }
            // как Sketch2 пользователя: две дуги разных радиусов и два отрезка
            var a = new SkPoint(-30, -25); var b = new SkPoint(-25, -5); var c = new SkPoint(25, -5); var d = new SkPoint(30, -25);
            sk.AddEntity(new SkLine(a, b)); sk.AddEntity(new SkLine(c, d));
            sk.AddEntity(new SkArc(new SkPoint(0, -15), c, b));                    // верхняя дуга
            sk.AddEntity(new SkArc(new SkPoint(0, -10), a, d));                    // нижняя дуга
            return sk;
        }
        foreach (var d in new[] { d1, d2 })
        {
            bool mir = mirror && d == d2;
            var sk = Prof(mir ? -50 : 50);
            var e = new ExtrudeFeature("P" + d) { SketchId = sk.Id, Cut = true }; e.Side1.Distance = new ValueRef(d); e.Side1.Flip = !mir; p.AddFeature(e);
            if (mir) { var m = new MirrorFeature("M") { At = Vector3d.Zero, Normal = Vector3d.UnitZ }; m.Sources.Add(e.Key); p.AddFeature(m); }
        }
        return p;
    }

    /// <summary>Контур кармана пользователя (Sketch2): дуги R58.5 и R51.2, касательные к ним отрезки.</summary>
    static Sketch UserSlot116(Part p, double z)
    {
        var sk = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY)); p.Add(sk);
        var a = new SkPoint(-50.45346588013375, 33.45815385549485); var b = new SkPoint(-57.60750219233915, -7.114189921068596);
        var c = new SkPoint(50.453465880133756, 33.45815385549484); var d = new SkPoint(57.607502192339155, -7.114189921068604);
        sk.AddEntity(new SkLine(a, b)); sk.AddEntity(new SkLine(c, d));
        sk.AddEntity(new SkArc(new SkPoint(0, -17.272), b, d));          // нижняя
        sk.AddEntity(new SkArc(new SkPoint(0, 24.5618), c, a));          // верхняя
        return sk;
    }

    static void Krn116Probe()
    {
        {
            var sp = new Part("slot"); var doc = new Document(); doc.Root.Add(sp); TestGeometry.AddCube(sp, 200);
            var sk = UserSlot116(sp, 100);
            var e = new ExtrudeFeature("Pocket") { SketchId = sk.Id, Cut = true }; e.Side1.Distance = new ValueRef(8); e.Side1.Flip = true; sp.AddFeature(e);
            sp.Rebuild(null);
            var exact = new Vector3d(50.453465880133756, 33.45815385549484, 92);
            {
                var tp = new Part("tool"); var tdoc = new Document(); tdoc.Root.Add(tp);
                var tsk = UserSlot116(tp, 100);
                var te = new ExtrudeFeature("Boss") { SketchId = tsk.Id }; te.Side1.Distance = new ValueRef(8); te.Side1.Flip = true; tp.AddFeature(te);
                tp.Rebuild(null);
                foreach (var v in tp.Bodies.SelectMany(b => b.Faces).SelectMany(f => f.Edges).SelectMany(e2 => new[] { e2.V0, e2.V1 }).Distinct())
                    if ((v.P - exact).Length < 1e-3) Console.WriteLine($"slot tool-only vertex off {(v.P - exact).Length:G3}");
                var prof = tsk.ToSolidProfile("x");
                Console.WriteLine($"slot profile: {prof.GetType().Name}");
            }
            foreach (var v in sp.Bodies.SelectMany(b => b.Faces).SelectMany(f => f.Edges).SelectMany(e2 => new[] { e2.V0, e2.V1 }).Distinct())
                if ((v.P - exact).Length < 1e-3) Console.WriteLine($"slot vertex off {(v.P - exact).Length:G3}; errors {e.Error}; {string.Join(" | ", sp.Warnings)}");
        }
        foreach (var (circ, gr, mi) in new[] { (false, true, false), (false, true, true), (false, false, true), (true, true, true) })
        {
            var t = TwinPockets116(circle: circ, groove: gr, mirror: mi);
            var bo = Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug;
            Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => { if (!m.Contains("accepted")) Console.WriteLine("   ROUTE " + m); };
            t.Rebuild(null);
            Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = bo;
            Console.WriteLine($"twin pockets circle={circ} groove={gr} mirror={mi}: volume {t.Volume:0.###}, errors {string.Join(";", t.Features.Select(f => f.Error))}, warnings {string.Join(" | ", t.Warnings.Where(w => w.Contains("downgrade") || w.Contains("path")))}");
        }
        return;
        long a0 = Parametric3D.Kernel.Tess.ExactFaceMesher.AnisoCylFaces;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = Tube116(); p.Rebuild(null);
        Console.WriteLine($"tube: {sw.ElapsedMilliseconds} ms, volume {p.Volume:0.###}, errors {string.Join(";", p.Features.Select(f => f.Error))}, aniso {Parametric3D.Kernel.Tess.ExactFaceMesher.AnisoCylFaces - a0}, downgrades {string.Join(",", p.Downgrades.Select(kv => kv.Key.Name + ":" + kv.Value.Faces))}");
        foreach (var f in p.Features) Console.WriteLine($"  {f.Name} {(p.LastFeatureMs.TryGetValue(f, out var ms) ? ms : -1):0} ms");
    }
}
