using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k179 (замечание 07.11, пп. 1–4, 9; модель пользователя gear.p3d): переход «По сечениям» двух шестерен с одной кривой-
    /// направляющей через вершину зуба.
    /// 1. Направляющая ведёт ВСЕ рёбра: у шестерен — повёрнутые копии друг друга с 10 зубьями, так что перенесённая к вершине
    ///    k-го зуба направляющая — это сама направляющая, повёрнутая на 36°·k вокруг оси; ребро тела проходит через середину
    ///    каждой такой кривой. Под P3D_GUIDEALL=0 и с «только своё ребро» — нет.
    /// 2. Отверстие (окружность в обоих эскизах) при направляющей — тело строится (P3D_GUIDEHOLES=0 — прежний отказ).
    /// 3. Опорная кривая идёт за точкой эскиза: правка шестерни (модуль) — номер вершины прежний (P3D_GEARIDS=0), кривая на
    ///    новом месте (P3D_CURVEFOLLOW=0), переход помечен к пересчёту (P3D_CURVEDEPS=0); после записи и чтения — так же
    ///    (номера точек условий переведены, P3D_SPECMAP=0).
    /// 4. Порядок создания (Seq): кривая создана после эскизов — в правке эскиза не видна, хоть и стоит в дереве выше
    ///    (P3D_CREATIONORDER=0 — по месту в дереве); в файле — свой номер, у файла без него — по прежним номерам элементов.
    /// 5. Ломаные эскиза — из памяти, пока геометрия та же (P3D_POLYMEMO=0).
    /// </summary>
    static void Guide179Checks()
    {
        Check("GUIDE-ALL-K179", () =>
        {
            var problems = new List<string>();
            var rep = new List<object>();
            (Part P, Sketch S1, Sketch S2, DatumCurve C, BlendFeature B) Make(bool edgeOnly = false, double hole = 0, double m2 = 10)
            {
                var part = new Part("gear179");
                var sk1 = new Sketch("Sketch1", Frame3.XY); part.Add(sk1);
                sk1.AddGear(new GearParams { Teeth = 10, Module = 10, PitchCircle = false }, out _);
                var fr2 = new Frame3(new Vector3d(0, 0, -70), Vector3d.UnitX, Vector3d.UnitY);
                var sk2 = new Sketch("Sketch2", fr2); part.Add(sk2);
                sk2.AddGear(new GearParams { Teeth = 10, Module = m2, Rotation = 45, PitchCircle = false }, out _);
                if (hole > 0) { sk1.AddEntity(new SkCircle(new SkPoint(0, 0), hole)); sk2.AddEntity(new SkCircle(new SkPoint(0, 0), hole)); }
                var (pa, pb) = TipPair(sk1, sk2);
                var specs = new[]
                {
                    new CurvePointSpec { SketchId = sk1.Id, SketchPointId = pa.Id, SketchNormal = Vector3d.UnitZ, NormalToSketch = true },
                    new CurvePointSpec { SketchId = sk2.Id, SketchPointId = pb.Id, SketchNormal = Vector3d.UnitZ, NormalToSketch = true },
                };
                var A = sk1.Plane.ToWorld(pa.P); var Bw = sk2.Plane.ToWorld(pb.P);
                var sp = CurveThrough.Build(new[] { A, Bw }, specs.Select(x => x.Direction).ToList(), null, null, false)!;
                var dc = new DatumCurve("Curve1", Array.Empty<Vector3d>(), "through points") { Mode = 0 };
                dc.Inputs.Add(A); dc.Inputs.Add(Bw); dc.Specs.AddRange(specs); dc.SetSpline(sp);
                part.Add(dc);
                var bf = new BlendFeature { Mode = BlendKind.Sections, Smooth = true, GuideEdgeOnly = edgeOnly };
                bf.SectionSketchIds.Add(sk1.Id); bf.SectionSketchIds.Add(sk2.Id); bf.GuideCurveIds.Add(dc.Id);
                part.AddFeature(bf);
                part.Rebuild(null);
                return (part, sk1, sk2, dc, bf);
            }
            // вершина зуба: конец дуги вершины (самая дальняя от центра точка), и соответственная — у второй шестерни, повёрнутой на 45°
            (SkPoint, SkPoint) TipPair(Sketch a, Sketch b)
            {
                var ta = a.AllPoints().Where(p => p.Owner != null).OrderByDescending(p => p.P.Length).ThenBy(p => Math.Atan2(p.P.Y, p.P.X)).First();
                double ang = Math.Atan2(ta.P.Y, ta.P.X) + Math.PI / 4;
                var want = new Vector2d(Math.Cos(ang), Math.Sin(ang)) * ta.P.Length;
                double sc = b.Gears.Single().P.Module / a.Gears.Single().P.Module;
                var tb = b.AllPoints().OrderBy(p => (p.P - want * sc).Length).First();
                return (ta, tb);
            }
            double EdgeDist(Part p, Vector3d q) => p.Bodies.Count == 0 ? double.NaN : p.Bodies.SelectMany(x => x.Edges).Min(e =>
            {
                double best = double.MaxValue;
                for (int i = 0; i <= 200; i++) best = Math.Min(best, (e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 200.0) - q).Length);
                return best;
            });
            static Vector3d RotZ(Vector3d v, double a) => new(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a), v.Z);

            // 1. все рёбра по направляющей
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (p1, s1, s2, c1, b1) = Make();
            long ms1 = sw.ElapsedMilliseconds;
            var g = c1.Spline!; var mid = g.PointAt(0.25);   // середина хорды у кубической с осевыми касательными — на хорде; четверть — нет
            double worst = 0;
            for (int k = 1; k < 10; k++) worst = Math.Max(worst, EdgeDist(p1, RotZ(mid, 2 * Math.PI * k / 10)));
            var (pe, _, _, _, be) = Make(edgeOnly: true);
            double worstE = 0;
            for (int k = 1; k < 10; k++) worstE = Math.Max(worstE, EdgeDist(pe, RotZ(mid, 2 * Math.PI * k / 10)));
            rep.Add(new { test = "all edges", ms = ms1, error = b1.Error, volume = p1.Volume, faces = p1.Bodies.FirstOrDefault()?.Faces.Count(), offRotatedGuides = worst, edgeOnlyError = be.Error, edgeOnlyVolume = pe.Volume, edgeOnlyOff = worstE });
            if (b1.Error != null || p1.Bodies.Count != 1) problems.Add($"blend with one guide: {b1.Error}");
            else if (!(worst < 1e-3)) problems.Add($"the other tooth edges do not follow the guide (off {worst:E2} mm)");
            if (be.Error != null || !(worstE > 0.1)) problems.Add($"«guide drives only its edge»: {be.Error}, off {worstE:E2}");

            // 1б. вторая шестерня крупнее (модуль 22.6 — как у пользователя): все рёбра или (запасной путь) — свои, с записью
            {
                var (ps, _, _, cs, bs) = Make(m2: 22.627416997969522);
                var gs = cs.Spline!.PointAt(0.25); double ws = 0;
                for (int k = 1; k < 10; k++) ws = Math.Max(ws, EdgeDist(ps, RotZ(gs, 2 * Math.PI * k / 10)));
                rep.Add(new { test = "scaled gear", error = bs.Error, volume = ps.Volume, warnings = ps.Warnings.Where(x => x.Contains("guide")).ToList(), offRotatedGuides = ws });
                if (bs.Error != null || ps.Bodies.Count != 1) problems.Add($"scaled gear blend: {bs.Error}");
                // k179: поверхность подобия — все рёбра, без запасного пути: кривая вершины k-го зуба — направляющая, повёрнутая
                // на 36°·k (сечения подобны)
                else if (!(ws < 1e-6) || ps.Warnings.Any(x => x.Contains("could not drive all edges"))) problems.Add($"scaled gear: the other edges do not follow the guide ({ws:E2}); {string.Join(" | ", ps.Warnings)}");
            }
            // 2. отверстие при направляющей
            var (ph, _, _, _, bh) = Make(hole: 10);
            rep.Add(new { test = "hole", error = bh.Error, volume = ph.Volume, bodies = ph.Bodies.Count, want = p1.Volume - Math.PI * 100 * 70 });
            if (bh.Error != null || ph.Bodies.Count != 1 || !(ph.Volume < p1.Volume - 0.9 * Math.PI * 100 * 70) || !(ph.Volume > p1.Volume - 1.1 * Math.PI * 100 * 70)) problems.Add($"guide with a hole: {bh.Error} volume {ph.Volume}");

            // 3. кривая идёт за точкой эскиза
            void Follow(Part p, string stage)
            {
                var sk2 = p.Sketches.First(k => k.Name == "Sketch2");
                var dc = p.Children.OfType<DatumCurve>().Single();
                var bf = p.Features.OfType<BlendFeature>().Single();
                uint ptId = dc.Specs[1].SketchPointId;
                var before = sk2.AllPoints().FirstOrDefault(x => x.Id == ptId);
                int idx = before == null ? -1 : sk2.AllPoints().ToList().IndexOf(before);
                double v0 = p.Volume;
                var gr = sk2.Gears.Single(); gr.P.Module = 12; sk2.Regenerate(gr, out _);
                var after = sk2.AllPoints().FirstOrDefault(x => x.Id == ptId);
                bool dirty = p.IsDirty;
                var want = after == null ? Vector3d.Zero : sk2.Plane.ToWorld(after.P);
                double off = after == null ? double.NaN : (dc.Inputs[1] - want).Length;
                double endOff = dc.Spline is { } s ? (s.PointAt(s.T1) - want).Length : double.NaN;
                p.Rebuild(null);
                double eOff = EdgeDist(p, dc.Spline!.PointAt(0.5));
                rep.Add(new { test = "follow " + stage, pointKept = after != null, before = before == null ? null : new[] { before.X, before.Y }, after = after == null ? null : new[] { after.X, after.Y }, inputOff = off, curveEnd = endOff, dirty, error = bf.Error, v0, v1 = p.Volume, edgeOff = eOff });
                if (before == null) problems.Add($"{stage}: the curve point of the sketch is not found before the edit");
                else if (after == null) problems.Add($"{stage}: the gear point the curve is attached to got a new number");
                else if (!(off < 1e-9) || !(endOff < 1e-9)) problems.Add($"{stage}: the curve did not follow the sketch point ({off:E2})");
                if (!dirty) problems.Add($"{stage}: the blend is not marked for the rebuild after the curve changed");
                if (bf.Error != null || !(eOff < 1e-6)) problems.Add($"{stage}: after the rebuild the edge is off the moved curve ({eOff:E2}, {bf.Error})");
            }
            Follow(p1, "in session");
            var (p2, _, _, _, _) = Make();
            var (lp, lps) = DocumentFile.Load(DocumentFile.Save(p2)); lp.Rebuild(lps);
            Follow(lp, "after reload");
            // правка самой кривой (не эскиза) — переход помечен к пересчёту
            {
                var (p3, _, _, c3, _) = Make();
                bool d0 = p3.IsDirty;
                c3.Specs[0].Weight = 2;
                var sp = CurveThrough.Build(c3.Inputs, c3.Specs.Select(x => x.Direction).ToList(), null, c3.Specs.Select(x => x.Weight).ToList(), false)!;
                c3.SetSpline(sp); c3.NotifyChanged(c3);
                rep.Add(new { test = "curve edit marks the blend", before = d0, after = p3.IsDirty });
                if (d0 || !p3.IsDirty) problems.Add("editing the guide curve does not mark the blend for the rebuild");
            }

            // 4. порядок создания
            {
                var (p4, k1, k2, c4, _) = Make();
                p4.PlaceItem(c4, 0);   // в дереве — выше эскизов
                bool hid = k1.HidesLater(c4) && k2.HidesLater(c4) && !k2.HidesLater(k1) && k1.HidesLater(k2);
                var (r4, _) = DocumentFile.Load(DocumentFile.Save(p4));
                var r1 = r4.Sketches.First(k => k.Name == "Sketch1"); var rc = r4.Children.OfType<DatumCurve>().Single();
                bool kept = r1.HidesLater(rc) && rc.Seq > r1.Seq;
                // файл без номеров порядка — по прежним номерам элементов
                var json = System.Text.RegularExpressions.Regex.Replace(DocumentFile.Save(p4), @"""Seq""\s*:\s*\d+\s*,?", "");
                var (o4, _) = DocumentFile.Load(json);
                var o1 = o4.Sketches.First(k => k.Name == "Sketch1"); var oc = o4.Children.OfType<DatumCurve>().Single();
                bool legacy = o1.HidesLater(oc);
                rep.Add(new { test = "creation order", hid, kept, legacy, seq = new[] { k1.Seq, k2.Seq, c4.Seq } });
                if (!hid) problems.Add("in the edit of an earlier sketch a later curve or sketch is shown (creation order)");
                if (!kept) problems.Add("creation order lost in the file");
                if (!legacy) problems.Add("a file without the creation order: the order is not taken from the element numbers");
            }

            // 5. ломаные из памяти
            {
                var k = Make().S1;
                k.Polylines(); long r0 = Sketch.PolylinesReused;
                var l1 = k.Polylines(); long r1 = Sketch.PolylinesReused;
                var hp = k.AllPoints().First();
                hp.P += new Vector2d(0.5, 0);
                var l2 = k.Polylines(); long r2 = Sketch.PolylinesReused;
                rep.Add(new { test = "polyline memo", reusedSame = r1 - r0, reusedMoved = r2 - r1 });
                if (r1 - r0 != 1 || r2 != r1) problems.Add($"sketch polylines: reused {r1 - r0} for the same geometry, {r2 - r1} after a move");
            }
            // 6. файл пользователя: Sketch1 — шестерня с закреплёнными точками (замыкание венца — 1e-5 мм) не противоречива
            {
                string uf = Path.Combine("tests", "ui", "data", "user_k179_gear.p3d");
                if (File.Exists(uf))
                {
                    var (up, _) = DocumentFile.Load(File.ReadAllText(uf));
                    var st = up.Sketches.First(k => k.Name == "Sketch1").Solve(new NumericSketchSolver());
                    rep.Add(new { test = "user Sketch1", status = st.ToString() });
                    if (st is SolveStatus.Inconsistent or SolveStatus.DidNotConverge) problems.Add($"user Sketch1 (fixed gear only): {st}");
                }
            }
            Log(new { test = "GUIDE-ALL-K179", rep, problems });
            if (problems.Count > 0) throw new Exception(string.Join("; ", problems));
        });
    }

    /// <summary>k179: файл пользователя — состояние эскизов (решатель, связи, размеры), ошибка и время перехода.</summary>
    static void UserFile179(string path)
    {
        var (p, ps) = DocumentFile.Load(File.ReadAllText(path));
        var solver = new NumericSketchSolver();
        foreach (var sk in p.Sketches)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = sk.Solve(solver);
            long ms = sw.ElapsedMilliseconds;
            var info = SketchEditing.Inspect(sk, solver);
            Log(new
            {
                test = "sketch", sk.Name, sk.Id, sk.Seq, status = st.ToString(), last = sk.LastSolve.ToString(), dof = sk.DegreesOfFreedom, free = sk.FreeDegrees, ms,
                entities = sk.Entities.Count, points = sk.Entities.Count(e => e is SkPoint), loose = sk.Entities.Count(e => e is SkPoint { Fixed: false }),
                constraints = sk.Constraints.Count, dims = sk.Dimensions.Select(d => $"{d.Type} {string.Join(",", d.Items.Select(x => x.GetType().Name + (x is SkPoint q ? $"({q.X},{q.Y},fixed {q.Fixed})" : "")))} = {d.Value} default {d.IsDefault} conflict {d.Conflict}").ToList(),
                conflictC = info.Constraints.Select(c => c.Type.ToString()).ToList(), conflictD = info.Dimensions.Select(d => d.Type.ToString()).ToList(), failure = info.Failure.ToString(),
                refs = sk.Entities.Where(e => e.Role == SkRole.Reference).Select(e => e.ReferenceSource).Distinct().ToList(),
            });
        }
        foreach (var dc in p.Children.OfType<DatumCurve>())
            Log(new { test = "curve", dc.Name, dc.Seq, specs = dc.Specs.Select(c => new { c.SketchId, c.SketchPointId, found = p.Sketches.FirstOrDefault(k => k.Id == c.SketchId)?.AllPoints().Any(x => x.Id == c.SketchPointId) }).ToList() });
        bool prof = Environment.GetEnvironmentVariable("P3D_PROFREPORT") == "1";
        if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(ps);
        if (prof) Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
        foreach (var f in p.Features) Log(new { test = "feature", f.Name, f.Seq, error = f.Error, ms = sw2.ElapsedMilliseconds, volume = p.Volume, faces = p.Bodies.FirstOrDefault()?.Faces.Count(), warnings = p.Warnings.ToList() });
    }

    /// <summary>k179: разбор — переход шестерен разного модуля без запасного пути; развёртка грани, не разбитой сеткой.</summary>
    static void GuideDump(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Parametric3D.Kernel.Tess.ExactFaceMesher.UVDumpPath = Path.Combine(outDir, "uv.txt");
        Parametric3D.Kernel.Tess.ExactFaceMesher.ObjDumpPath = Path.Combine(outDir, "face.obj");
        var dbg = new List<string>();
        Parametric3D.Kernel.Tess.ExactFaceMesher.SeamDebug = m => { lock (dbg) if (dbg.Count < 200) dbg.Add(m); };
        var part = new Part("g");
        var sk1 = new Sketch("Sketch1", Frame3.XY); part.Add(sk1);
        sk1.AddGear(new GearParams { Teeth = 10, Module = 10, PitchCircle = false }, out _);
        var fr2 = new Frame3(new Vector3d(0, 0, -70), Vector3d.UnitX, Vector3d.UnitY);
        var sk2 = new Sketch("Sketch2", fr2); part.Add(sk2);
        sk2.AddGear(new GearParams { Teeth = 10, Module = 22.627416997969522, Rotation = 45, PitchCircle = false }, out _);
        var ta = sk1.AllPoints().Where(p => p.Owner != null).OrderByDescending(p => p.P.Length).ThenBy(p => Math.Atan2(p.P.Y, p.P.X)).First();
        double ang = Math.Atan2(ta.P.Y, ta.P.X) + Math.PI / 4;
        var want = new Vector2d(Math.Cos(ang), Math.Sin(ang)) * ta.P.Length * 2.2627416997969522;
        var tb = sk2.AllPoints().OrderBy(p => (p.P - want).Length).First();
        var A = sk1.Plane.ToWorld(ta.P); var B = sk2.Plane.ToWorld(tb.P);
        var sp = CurveThrough.Build(new[] { A, B }, new Vector3d?[] { Vector3d.UnitZ, Vector3d.UnitZ }, null, null, false)!;
        var dc = new DatumCurve("Curve1", Array.Empty<Vector3d>(), "through points") { Mode = 0 }; dc.Inputs.Add(A); dc.Inputs.Add(B); dc.SetSpline(sp); part.Add(dc);
        var bf = new BlendFeature { Mode = BlendKind.Sections, Smooth = true }; bf.SectionSketchIds.Add(sk1.Id); bf.SectionSketchIds.Add(sk2.Id); bf.GuideCurveIds.Add(dc.Id);
        part.AddFeature(bf); part.Rebuild(null);
        Log(new { test = "guidedump", bf.Error, part.Volume, warnings = part.Warnings.ToList(), dump = File.Exists(ExactFaceMesherDump()), dbg });
        static string ExactFaceMesherDump() => Parametric3D.Kernel.Tess.ExactFaceMesher.UVDumpPath!;
    }
}

static partial class Program
{
    /// <summary>
    /// k180: рёбра-кривые Безье — точные в аудите геометрии. Переход двух квадратов с одной направляющей-кривой Безье (поверхность
    /// подобия): боковые рёбра — кривые Безье; «гранёных» рёбер нет, понижения геометрии нет. P3D_AUDITBEZIER=0 — рёбра гранёные.
    /// </summary>
    static void Audit180Checks()
    {
        Check("AUDIT-BEZIER-K180", () =>
        {
            var part = new Part("a180");
            Sketch Sq(string n, Frame3 fr, double h)
            {
                var sk = new Sketch(n, fr); part.Add(sk);
                var p = new[] { new SkPoint(-h, -h), new SkPoint(h, -h), new SkPoint(h, h), new SkPoint(-h, h) };
                for (int i = 0; i < 4; i++) sk.AddEntity(new SkLine(p[i], p[(i + 1) % 4]));
                return sk;
            }
            var s1 = Sq("S1", Frame3.XY, 10);
            var s2 = Sq("S2", new Frame3(new Vector3d(0, 0, -30), Vector3d.UnitX, Vector3d.UnitY), 15);
            var A = new Vector3d(10, 10, 0); var B = new Vector3d(15, 15, -30);
            var sp = CurveThrough.Build(new[] { A, B }, new Vector3d?[] { Vector3d.UnitZ, Vector3d.UnitZ }, null, null, false)!;
            var dc = new DatumCurve("G", Array.Empty<Vector3d>(), "through points") { Mode = 0 }; dc.Inputs.Add(A); dc.Inputs.Add(B); dc.SetSpline(sp); part.Add(dc);
            var bf = new BlendFeature { Mode = BlendKind.Sections, Smooth = true }; bf.SectionSketchIds.Add(s1.Id); bf.SectionSketchIds.Add(s2.Id); bf.GuideCurveIds.Add(dc.Id);
            part.AddFeature(bf); part.Rebuild(null);
            var body = part.Bodies.FirstOrDefault();
            var fac = body == null ? (-1, -1) : Parametric3D.Kernel.Topology.GeometryAudit.Faceted(body);
            int bez = body?.Edges.Count(e => e.Curve is BezierCurve3) ?? 0;
            bool valid = body != null && Parametric3D.Kernel.Topology.SolidValidator.Validate(body).IsValid;
            Log(new { test = "AUDIT-BEZIER-K180", error = bf.Error, volume = part.Volume, valid, bezierEdges = bez, facetedFaces = fac.Item1, facetedEdges = fac.Item2, warnings = part.Warnings.ToList() });
            Require(bf.Error == null && valid, $"blend: {bf.Error}, valid {valid}");
            Require(bez > 0, "no Bezier edges in the guided blend");
            Require(fac.Item1 == 0 && fac.Item2 == 0 && !part.Warnings.Any(w => w.Contains("faceted")), $"exact Bezier edges counted as faceted ({fac.Item2})");
        });
    }
}
