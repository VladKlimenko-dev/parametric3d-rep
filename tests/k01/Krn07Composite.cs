using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Kernel.Ops;

static partial class Program
{
    /// <summary>Составные сценарии KRN-07 (g.08): деталь деревом операций модели.</summary>
    internal static List<(string Name, Func<Part> Make, Action<Part, bool> Edit)> Krn07Scenarios()
    {
        Frame3 XYz(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        Sketch Poly(Part p, Frame3 f, params (double X, double Y)[] pts)
        {
            var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s);
            var sp = pts.Select(q => new SkPoint(q.X, q.Y)).ToList();
            for (int i = 0; i < sp.Count; i++) s.AddEntity(new SkLine(sp[i], sp[(i + 1) % sp.Count]));
            return s;
        }
        Sketch Circ(Part p, Frame3 f, double x, double y, double r) { var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s); s.AddEntity(new SkCircle(new SkPoint(x, y), r)); return s; }
        var list = new List<(string, Func<Part>, Action<Part, bool>)>();

        // 1. КОРПУС: брусок 60×40×25 → скругление вертикальных рёбер R6 → уклон стенок 3° (нейтраль внизу) → скругление верхнего
        //    контура r2 (над наклонными стенками) → оболочка 1.5 с открытым верхом (у сопряжений) → бобышка r4 изнутри со дна
        list.Add(("housing: box, rounds, draft, top loop, shell open at blends, inner boss", () =>
        {
            var p = new Part("housing");
            var s = Poly(p, XYz(0), (0, 0), (60, 0), (60, 40), (0, 40));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(25); p.AddFeature(ex); p.Rebuild(null);
            var r1 = new RoundFeature { Radius = new ValueRef(6) };
            r1.Edges.AddRange(p.Bodies[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of)); p.AddFeature(r1); p.Rebuild(null);
            var df = new DraftFeature { Angle = new ValueRef(3), NeutralPoint = Vector3d.Zero, NeutralNormal = Vector3d.UnitZ };
            df.OnFaces.AddRange(new[] { new Vector3d(60, 20, 12), new Vector3d(30, 0, 12), new Vector3d(0, 20, 12), new Vector3d(30, 40, 12) }); p.AddFeature(df); p.Rebuild(null);
            var r2 = new RoundFeature { Radius = new ValueRef(2) };
            r2.Edges.AddRange(p.Bodies[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 25) < 1e-7 && Math.Abs(e.V1.P.Z - 25) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - 25) < 1e-7).Select(EdgeRef.Of)); p.AddFeature(r2); p.Rebuild(null);
            var sh = new ShellFeature { Thickness = new ValueRef(1.5) }; sh.OpenFaces.Add(new Vector3d(30, 20, 25)); p.AddFeature(sh); p.Rebuild(null);
            var bs = Circ(p, XYz(1.5), 20, 20, 4);
            var eb = new ExtrudeFeature { SketchId = bs.Id }; eb.Side1.Distance = new ValueRef(10); p.AddFeature(eb);
            return p;
        }, (p, on) => { var df = p.Features.OfType<DraftFeature>().First(); df.Angle = new ValueRef(on ? 4 : 3); p.MarkDirtyFrom(df); }));

        // 2. РУЧКА: плита 60×30×10 → протяжка круга r3 по дуге Безье (концы внутри плиты), объединение → зеркало плиты с ручкой
        list.Add(("handle: plate, sweep along a Bezier arch, union, mirror", () =>
        {
            var p = new Part("handle");
            var s = Poly(p, XYz(0), (0, 0), (60, 0), (60, 30), (0, 30));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
            var sec = Circ(p, new Frame3(new Vector3d(10, 15, 5), Vector3d.UnitX, Vector3d.UnitY), 0, 0, 3);
            var path = new Sketch("path", new Frame3(new Vector3d(0, 15, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(path);
            path.AddEntity(new SkSpline(new[] { new SkPoint(10, 5), new SkPoint(10, 40), new SkPoint(50, 40), new SkPoint(50, 5) }));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
            p.AddFeature(new MirrorFeature { At = new Vector3d(0, 0, 0), Normal = Vector3d.UnitX });
            return p;
        }, (p, on) => { var c = p.Sketches.SelectMany(sk => sk.Entities).OfType<SkCircle>().First(); c.Radius = on ? 3.5 : 3; p.Invalidate(); }));

        // 3. ПЕРЕХОД: плита 50×50×5 → лофт квадрат 30 → окружность r10 на высоте 30 с касательными, объединение
        list.Add(("transition: plate, loft square → circle with end tangency, union", () =>
        {
            var p = new Part("transition");
            var s = Poly(p, XYz(0), (-25, -25), (25, -25), (25, 25), (-25, 25));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(5); p.AddFeature(ex);
            var a = Poly(p, XYz(5), (-15, -15), (15, -15), (15, 15), (-15, 15));
            var b = Circ(p, XYz(35), 0, 0, 10);
            var lf = new LoftFeature { StartTangent = 0.5, EndTangent = 0.5 }; lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf);
            return p;
        }, (p, on) => { var lf = p.Features.OfType<LoftFeature>().First(); lf.EndTangent = on ? 0.8 : 0.5; p.MarkDirtyFrom(lf); }));

        // 4. КРЫШКА С ПЕРЕМЕННЫМ СКРУГЛЕНИЕМ (06.2): брусок 60×40×20 → вертикальные рёбра R8 → сквозное отверстие r5 в центре →
        //    верхний контур с законом радиуса по точкам (2 → 4 → 2.5 → 2 по длине контура)
        list.Add(("lid: box, vertical rounds, through hole, top loop with a radius law", () =>
        {
            var p = new Part("lid");
            var s = Poly(p, XYz(0), (0, 0), (60, 0), (60, 40), (0, 40));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(20); p.AddFeature(ex); p.Rebuild(null);
            var r1 = new RoundFeature { Radius = new ValueRef(8) };
            r1.Edges.AddRange(p.Bodies[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of)); p.AddFeature(r1); p.Rebuild(null);
            var hs = Circ(p, XYz(0), 30, 20, 5);
            var eh = new ExtrudeFeature { SketchId = hs.Id, Cut = true }; eh.Side1.Distance = new ValueRef(20); p.AddFeature(eh); p.Rebuild(null);
            var r2 = new RoundFeature { Radius = new ValueRef(2) };
            r2.Edges.AddRange(p.Bodies[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - 20) < 1e-7
                                                          && (e.Curve.PointAt(0.5) - new Vector3d(30, 20, 20)).Length > 6).Select(EdgeRef.Of));
            r2.LawPoints.Add((0.0, new ValueRef(2))); r2.LawPoints.Add((0.3, new ValueRef(4))); r2.LawPoints.Add((0.65, new ValueRef(2.5))); r2.LawPoints.Add((1.0, new ValueRef(2)));
            p.AddFeature(r2);
            return p;
        }, (p, on) => { var rf = p.Features.OfType<RoundFeature>().Last(); rf.LawPoints[1] = (0.3, new ValueRef(on ? 3.5 : 4)); p.MarkDirtyFrom(rf); }));
        return list;
    }

    static void Krn07CompositeChecks()
    {
        Check("KRN07-COMPOSITE-SCENARIOS", () =>
        {
            // g.08: составные сценарии деревом операций модели: каждая операция без ошибки, тело точное и годное; сохранение и
            // загрузка — тот же объём; правка параметра и возврат — тот же объём (перестроение детерминировано); точный STEP
            // пишется (OpenCascade — в G14).
            var fails = new List<string>();
            foreach (var (name, make, edit) in Krn07Scenarios())
            {
                if (Environment.GetEnvironmentVariable("P3D_COMPCASE") is { } cc && !name.StartsWith(cc)) continue;
                try
                {
                    if (Environment.GetEnvironmentVariable("P3D_COMPDBG") == "1") BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("    route: " + m);
                    var p = make(); p.Rebuild(null);
                    BooleanOp.AnalyticRouteDebug = null;
                    var errs = p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}").ToList();
                    if (Environment.GetEnvironmentVariable("P3D_COMPDBG") == "1") foreach (var w in p.Warnings) Console.WriteLine("    w: " + w);
                    var b = p.Bodies.FirstOrDefault();
                    double v = p.Volume;
                    Console.WriteLine($"  {name}: V {v:R}, bodies {p.Bodies.Count}, {b?.Guarantee}, {b?.Faces.Count()} faces{(errs.Count > 0 ? " | " + string.Join(" | ", errs) : "")}");
                    if (errs.Count > 0) { fails.Add($"{name}: {string.Join(" | ", errs)}"); continue; }
                    if (p.Bodies.Count != 1 || b!.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) { fails.Add($"{name}: {p.Bodies.Count} bodies, {b?.Guarantee} {(b == null ? "" : SolidValidator.Validate(b).Summary())}"); continue; }
                    var (q, pars) = DocumentFile.Load(DocumentFile.Save(p)); q.Rebuild(pars);
                    if (!(Math.Abs(q.Volume - v) <= 1e-9 * v)) fails.Add($"{name}: reloaded V {q.Volume:R} vs {v:R}");
                    edit(p, true); p.Rebuild(null);
                    double v1 = p.Volume;
                    if (Math.Abs(v1 - v) <= 1e-12 * v) fails.Add($"{name}: the edit did not change the part (V {v1:R})"); bool okEdit = p.Features.All(f => f.Error == null) && p.Bodies.Count == 1 && p.Bodies[0].Guarantee == GuaranteeLevel.ExactAnalytic;
                    edit(p, false); p.Rebuild(null);
                    Console.WriteLine($"    edited V {v1:R} ({(okEdit ? "exact" : "NOT exact")}), reverted V {p.Volume:R}");
                    if (!okEdit) fails.Add($"{name}: the edited tree is not exact ({string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Error))})");
                    if (!(Math.Abs(p.Volume - v) <= 1e-12 * v)) fails.Add($"{name}: reverted V {p.Volume:R} vs {v:R}");
                    var sw = new StringWriter(); var ws = new List<string>();
                    if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { p.Bodies[0] }, "sc", ws) || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Take(2))})");
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    static void CompositeProbe()
    {
        if (Environment.GetEnvironmentVariable("P3D_HANDLEONLY") == "1")
        {
            Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   [route] " + m);
            var sc = Krn07Scenarios()[1]; var p = sc.Make(); p.Rebuild(null);
            foreach (var f in p.Features) Console.WriteLine($"  feature {f.Name}: {f.Error}");
            Console.WriteLine($"  handle: {p.Bodies[0].Guarantee}");
            return;
        }
        Profile Sq(double s, double z) { Vector3d P(double x, double y) => new(x, y, z); return Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.LineCurve3(P(-s, -s), P(s, -s)), new Parametric3D.Kernel.Curves.LineCurve3(P(s, -s), P(s, s)), new Parametric3D.Kernel.Curves.LineCurve3(P(s, s), P(-s, s)), new Parametric3D.Kernel.Curves.LineCurve3(P(-s, s), P(-s, -s)) }); }
        Profile Ci(double r, double z) => Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), r, 0, 2 * Math.PI) });
        Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   [route] " + m);
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        {
            var loft = Parametric3D.Kernel.Ops.LoftOp.Build(new[] { Sq(15, 5), Ci(10, 35) }, 64, false, null, 0.5, 0.5);
            var bl = new List<TSolid> { Box(-25, -25, 0, 50, 50, 5) }; var w = new List<string>();
            Parametric3D.Kernel.Ops.BooleanOp.Unite(bl, loft, w);
            Console.WriteLine($"  loft ∪ plate (touching): {bl[0].Guarantee} {sw0.ElapsedMilliseconds} ms | {string.Join(" | ", w.Take(4))}");
        }
        sw0.Restart();
        {
            var loft = Parametric3D.Kernel.Ops.LoftOp.Build(new[] { Sq(15, 3), Ci(10, 35) }, 64, false, null, 0, 0.5);
            var bl = new List<TSolid> { Box(-25, -25, 0, 50, 50, 5) }; var w = new List<string>();
            Parametric3D.Kernel.Ops.BooleanOp.Unite(bl, loft, w);
            Console.WriteLine($"  loft ∪ plate (overlapping): {bl[0].Guarantee} {sw0.ElapsedMilliseconds} ms | {string.Join(" | ", w.Take(4))}");
        }
        sw0.Restart();
        {
            var path = new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(10, 15, 5), new Vector3d(10, 15, 40), new Vector3d(50, 15, 40), new Vector3d(50, 15, 5) });
            var sec = Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(new Vector3d(10, 15, 5), Vector3d.UnitX, Vector3d.UnitY), new Parametric3D.Kernel.Curves.ICurve3[] { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(10, 15, 5), Vector3d.UnitX, Vector3d.UnitY), 3, 0, 2 * Math.PI) });
            var sweep = Parametric3D.Kernel.Ops.SweepOp.Build(sec, path, 1);
            Console.WriteLine($"  sweep alone: {sweep.Guarantee}");
            {
                var px = Parametric3D.Kernel.Ops.NurbsProxy.Of(sweep, out _)!;
                var c0 = new Parametric3D.Kernel.Analytic.Classifier(sweep); var c1 = new Parametric3D.Kernel.Analytic.Classifier(px);
                Console.WriteLine($"  classifier unsupported: orig {c0.Unsupported} proxy {c1.Unsupported}");
                foreach (var q in new[] { new Vector3d(2.2543, 10.4332, -0.3616), new Vector3d(7.758, 14.4168, 10.9041), new Vector3d(10, 15, 20), new Vector3d(30, 15, 34), new Vector3d(30, 15, 50) })
                    Console.WriteLine($"   cls {q}: orig {c0.Classify(q)} proxy {c1.Classify(q)}");
                double vo = AnalyticVolume.Of(sweep) ?? double.NaN, vp = AnalyticVolume.Of(px) ?? double.NaN;
                Console.WriteLine($"   volume orig {vo:R} proxy {vp:R} valid {SolidValidator.Validate(px).Summary()}");
            }
            {
                // одно пересечение: траектория выходит из плиты и кончается снаружи
                var path1 = new Parametric3D.Kernel.Curves.BezierCurve3(new[] { new Vector3d(10, 15, 5), new Vector3d(10, 15, 40), new Vector3d(50, 15, 40), new Vector3d(70, 15, 30) });
                var sw1 = Parametric3D.Kernel.Ops.SweepOp.Build(sec, path1, 1);
                {
                    var px1 = Parametric3D.Kernel.Ops.NurbsProxy.Of(sw1, out _)!; var c1 = new Parametric3D.Kernel.Analytic.Classifier(px1);
                    foreach (var q in new[] { new Vector3d(2.2543, 10.4332, -0.3616), new Vector3d(7.758, 14.4168, 10.9041), new Vector3d(0, 0, -5), new Vector3d(-10, 15, 5), new Vector3d(30, 15, 34) })
                        Console.WriteLine($"   cls1 {q}: proxy {c1.Classify(q)}");
                    foreach (var f in px1.Faces) Console.WriteLine($"   px1 face {f.Id} {f.Surface.GetType().Name} rev {f.Reversed} loops {f.Loops.Count} edges {f.Edges.Count()}");
                    Parametric3D.Kernel.Analytic.Classifier.Debug = m => Console.WriteLine("    [cl] " + m);
                    c1.Classify(new Vector3d(2.2543, 10.4332, -0.3616));
                    Parametric3D.Kernel.Analytic.Classifier.Debug = null;
                }
                var bl1 = new List<TSolid> { Box(0, 0, 0, 60, 30, 10) }; var w1 = new List<string>();
                Parametric3D.Kernel.Ops.BooleanOp.CsgDisagreeProbe = (pt, a, b, c, r) => Console.WriteLine($"   [csg] {pt:0.####} a {a} b {b} result {c}");
                Parametric3D.Kernel.Ops.BooleanOp.Unite(bl1, sw1, w1);
                Console.WriteLine($"  sweep (one crossing) ∪ plate: {bl1[0].Guarantee} | {string.Join(" | ", w1.Take(2))}");
            }
            {
                var px = Parametric3D.Kernel.Ops.NurbsProxy.Of(sweep, out var pwhy);
                var plate = Box(0, 0, 0, 60, 30, 10);
                foreach (var pf in px!.Faces.Where(f => f.Tag.Contains("side[0.2]")))
                {
                    var nsx = (Parametric3D.Kernel.Surfaces.NurbsSurface)Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(pf)!;
                    var rs = Parametric3D.Kernel.Geometry.NurbsSsi.Intersect(nsx, 0, 1, 0, 1, new Parametric3D.Kernel.Geometry.Ssi.Plane(new Vector3d(0, 0, 10), Vector3d.UnitZ), 60);
                    Console.WriteLine($"   ssi side[0.2]: {rs.Kind} curves {rs.Curves.Count} uncert {rs.Uncertified}");
                    foreach (var cc in rs.Curves) Console.WriteLine($"     curve {cc.PointAt(cc.T0):0.####} -> {cc.PointAt(cc.T1):0.####}; mid {cc.PointAt((cc.T0 + cc.T1) / 2):0.####}");
                }
                if (Environment.GetEnvironmentVariable("P3D_FSDBG") == "1") Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("    [fs] " + m);
                var r = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(plate, px!, Parametric3D.Kernel.Analytic.Bool2.Union, out var gw);
                Parametric3D.Kernel.Analytic.FaceSplit.Debug = null;
                if (r != null)
                    foreach (var e in r.Edges.Where(e => e.Curve is not Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 10) < 1e-6 && Math.Abs(e.V1.P.Z - 10) < 1e-6 && e.V0.P.X > 40))
                    {
                        Console.WriteLine($"   edge {e.Id} {e.V0.P:0.###}->{e.V1.P:0.###} T {e.Curve.T0}..{e.Curve.T1}");
                        for (int k = 0; k <= 8; k++) { var q = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 8.0); Console.Write($" {q:0.###}"); }
                        Console.WriteLine();
                        foreach (var ce in e.CoEdges)
                        {
                            var ff = ce.Loop!.Face!; var su = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(ff);
                            if (su is not Parametric3D.Kernel.Surfaces.NurbsSurface nsx) continue;
                            Console.Write($"     on face {ff.Id} {ff.Tag}:");
                            for (int k = 0; k <= 8; k++) { var q = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 8.0); var uv = nsx.ProjectToUV(q); Console.Write($" ({uv.X:0.####},{uv.Y:0.###};{(nsx.PointAt(uv) - q).Length:E0})"); }
                            Console.WriteLine();
                        }
                    }
                Console.WriteLine($"  direct general: {(r == null ? "null " + gw : r.Faces.Count() + " faces")} proxy {pwhy}");
                if (r != null)
                {
                    var mw = new List<string>(); int un = Parametric3D.Kernel.Analytic.PrismBuild.Finish(r, mw, null);
                    Console.WriteLine($"  finish: unmeshed {un} | {string.Join(" | ", mw.Take(3))}");
                    foreach (var f in r.Faces.Where(f => f.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface { Facets.Count: > 0 }))
                    {
                        Console.WriteLine($"   unmeshed face {f.Id} {f.Surface.GetType().Name}: loops {f.Loops.Count}, cycles {(Parametric3D.Kernel.Tess.ExactFaceMesher.Cycles(f)?.Count.ToString() ?? "null")}");
                        foreach (var e in f.Edges) Console.WriteLine($"     e{e.Id} {e.Curve.GetType().Name} v{e.V0.Id} {e.V0.P:0.#########} -> v{e.V1.Id} {e.V1.P:0.#########} cap {e.CoEdges.Count(c => c.Loop?.Face == f)} uses {e.CoEdges.Count}");
                    }
                }
                if (r != null)
                    foreach (var f in r.Faces.Where(f => f.Surface.IsPlanar && Math.Abs(f.Loops[0].CoEdges[0].Start.P.Z - 10) < 1e-9))
                    {
                        Console.WriteLine($"   top face {f.Id}: loops {f.Loops.Count}");
                        foreach (var e in f.Edges) Console.WriteLine($"     e{e.Id} {e.Curve.GetType().Name} v{e.V0.Id} {e.V0.P:0.#########} -> v{e.V1.Id} {e.V1.P:0.#########}");
                    }
            }
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 30, 10) }; var w = new List<string>();
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
            var swp = System.Diagnostics.Stopwatch.StartNew(); var pxx = Parametric3D.Kernel.Ops.NurbsProxy.Of(sweep, out _); Console.WriteLine($"   proxy alone: {swp.ElapsedMilliseconds} ms");
            Parametric3D.Kernel.Ops.BooleanOp.Unite(bl, sweep, w);
            Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = false;
            Console.WriteLine($"  sweep ∪ plate: {bl[0].Guarantee} {sw0.ElapsedMilliseconds} ms | {string.Join(" | ", w.Take(4))}");
        }
    }
}
