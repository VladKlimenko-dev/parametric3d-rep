using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenTK.Mathematics;
using Parametric3D.App;
using Parametric3D.App.Commands;
using Parametric3D.App.Icons;
using Parametric3D.App.Operations;
using Parametric3D.App.Sketching;
using Parametric3D.App.ViewModels;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;
using Parametric3D.App.Services;
using Parametric3D.App.Viewport;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Render;

// ПРОВЕРКИ ПРИЛОЖЕНИЯ (этапы U0–U1) без экрана: Avalonia.Headless, вид — без OpenGL (выбор под курсором считается на
// процессоре, см. ScenePicker). Каждая проверка — строка JSON {"test", "ok", "error"}; итог — {"passed", "failed"}.

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

string? only = args.FirstOrDefault();
int passed = 0, failed = 0;

void Check(string name, Action body)
{
    if (only != null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return;
    string? err = null;
    try { body(); } catch (Exception ex) { err = ex is CheckFailed ? ex.Message : ex.ToString(); }
    if (err == null) passed++; else failed++;
    Console.WriteLine(JsonSerializer.Serialize(new { test = name, ok = err == null, error = err }));
}

void Require(bool cond, string msg) { if (!cond) throw new CheckFailed(msg); }

MainWindow Open(DocumentSession s)
{
    var w = new MainWindow(s) { Width = 1400, Height = 860 };
    w.Show();
    Dispatcher.UIThread.RunJobs();
    w.ViewPort.Document?.RebuildDirty();
    w.ViewPort.ZoomToFit(animate: false);
    Dispatcher.UIThread.RunJobs();
    return w;
}

if (only == "probe-selftest")
{
    // разбор: самотест приложения в окне без экрана (P3D_SELFTEST_ONLY=load|cancel|handles — раздел)
    var w = Open(DocumentSession.NewPart());
    // без экрана кадров нет — фоновый расчёт, который запускает кадр вида, запускается таймером
    var pump = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, async (_, _) =>
    {
        var doc = w.Session.Document;
        if (!doc.IsBuilding && doc.AllParts().Any(p => p.IsDirty)) { try { await doc.RebuildDirtyAsync(); } catch { } }
    });
    pump.Start();
    var task = Parametric3D.App.Diagnostics.SelfTest.RunAsync(w, m => Console.WriteLine("ST " + m), CancellationToken.None);
    var cts = new CancellationTokenSource();
    _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => cts.Cancel()));
    Dispatcher.UIThread.MainLoop(cts.Token);
    Console.WriteLine("REPORT " + task.Result);
    Console.WriteLine(File.ReadAllText(task.Result));
    for (int k = 0; k < 3; k++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    var dp = Parametric3D.App.Diagnostics.SelfTest.DebugParts;
    Console.WriteLine($"PROBE load parts alive {dp.Count(x => x.IsAlive)} of {dp.Count}");
    var db = Parametric3D.App.Diagnostics.SelfTest.DebugBodies;
    Console.WriteLine($"PROBE load bodies alive {db.Count(x => x.IsAlive)} of {db.Count}: [{string.Join(",", db.Select((x, k) => x.IsAlive ? k : -1).Where(k => k >= 0))}]");
    var curB = w.Session.Part?.Bodies.FirstOrDefault();
    foreach (var wr in db.Where(x => x.IsAlive && !ReferenceEquals(x.Target, curB)).Skip(3).Take(1)) PathTo(wr.Target!, new (object, string)[] { (w, "window"), (w.Session, "session") });
    {
        long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
        Console.WriteLine($"PROBE memory now: {Mem() / 1048576.0:0.0} MB");
        w.Session.Part?.ForgetCheckpoints();
        Console.WriteLine($"PROBE memory w/o checkpoints: {Mem() / 1048576.0:0.0} MB");
        for (int rep = 0; rep < 4; rep++) System.Threading.Tasks.Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 64 }, _ => { Parametric3D.Kernel.Diagnostics.ThreadState.Release(); Thread.Sleep(2); });
        Console.WriteLine($"PROBE memory after thread release: {Mem() / 1048576.0:0.0} MB");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Parametric3D")))
            foreach (var t in asm.GetTypes())
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (fi.FieldType.IsGenericTypeDefinition || t.IsGenericTypeDefinition || fi.IsLiteral) continue;
                    object? v; try { v = fi.GetValue(null); } catch { continue; }
                    if (v == null) continue;
                    var clear = v.GetType().GetMethod("Clear", Type.EmptyTypes);
                    if (clear == null) continue;
                    long m0 = Mem();
                    try { clear.Invoke(v, null); } catch { continue; }
                    long m1 = Mem();
                    if (m0 - m1 > 1 << 20) Console.WriteLine($"PROBE cleared {t.FullName}.{fi.Name}: −{(m0 - m1) / 1048576.0:0.0} MB");
                }
        Console.WriteLine($"PROBE memory after clearing statics: {Mem() / 1048576.0:0.0} MB");
    }
    foreach (var wr in dp.Take(dp.Count - 2).Where(x => x.IsAlive && !ReferenceEquals(x.Target, w.Session.Part)).Take(1)) PathTo(wr.Target!, new (object, string)[] { (w, "window"), (w.Session, "session") });
    return 0;
}

if (only == "probe-silhouette")
{
    // разбор: очерки цилиндра (трубы) в эскизе на плоскости через ось — разные плоскости и оси выдавливания
    foreach (var (sketchPlane, baseName) in new[] { (Parametric3D.Kernel.Frame3.YZ, "YZ"), (Parametric3D.Kernel.Frame3.XY, "XY"), (Parametric3D.Kernel.Frame3.XZ, "XZ") })
        foreach (bool tube in new[] { false, true })
        {
            var s = DocumentSession.NewPart(); var part = s.Part!;
            var bp = baseName == "YZ" ? part.PlaneYZ : baseName == "XY" ? part.PlaneXY : part.PlaneXZ;
            var s1 = part.AddSketch("S1", sketchPlane, bp.Id);
            s1.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
            if (tube) s1.AddEntity(new SkCircle(new SkPoint(0, 0), 6));
            part.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Side1 = { Distance = new ValueRef(200) } });
            s.Document.RebuildDirty();
            foreach (var other in new[] { part.PlaneXY, part.PlaneXZ, part.PlaneYZ }.Where(x => x != bp))
            {
                var sk = new Sketch("S2", other.Frame);
                Console.WriteLine($"SIL base {baseName} tube {tube} sketch on {other.Name}: faces {part.Bodies.Sum(b => b.Faces.Count())}; " + string.Join("; ", part.Bodies.SelectMany(b => b.Faces).Select(f =>
                {
                    string shape = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f))?.GetType().Name ?? "null";
                    int n; try { n = SketchReferences.FaceProjection(sk, f).Count; } catch (Exception ex) { n = -1; shape += " " + ex.Message; }
                    return $"{f.Surface?.GetType().Name}/{shape}:{n}";
                })));
            }
        }
    return 0;
}

if (only == "probe-tube")
{
    // разбор: труба вдоль X (эскиз на YZ), эскиз на XY — очерки цилиндров
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var s1 = part.AddSketch("S1", part.PlaneYZ.Frame, part.PlaneYZ.Id);
    s1.AddEntity(new SkCircle(new SkPoint(0, 0), 10)); s1.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
    part.AddFeature(new ExtrudeFeature { SketchId = s1.Id });
    s.Document.RebuildDirty();
    Console.WriteLine($"TUBE faces: {string.Join("; ", part.Bodies.SelectMany(b => b.Faces).Select(f => $"#{f.Id} {f.Surface?.GetType().Name} auth {Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} shape {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f))?.GetType().Name}"))}");
    var w = Open(s);
    w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp());
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    Console.WriteLine($"TUBE sketch plane {sk.Plane.Origin} U {sk.Plane.U} V {sk.Plane.V}");
    foreach (var f in part.Bodies.SelectMany(b => b.Faces))
    {
        try { var pr = SketchReferences.FaceProjection(sk, f); Console.WriteLine($"TUBE face #{f.Id}: {string.Join(", ", pr.Select(x => $"{x.A}-{x.B} r{x.R} c{x.Circle}"))}"); }
        catch (Exception ex) { Console.WriteLine($"TUBE face #{f.Id}: {ex.Message}"); }
    }
    vp.BuildOverlayForTest();
    Console.WriteLine($"TUBE silhouettes {vp.SilhouetteDrawn}, items {vp.FaceRefItems()?.Count}");
    // вырез вращением (канавка) и правка его эскиза (откат)
    foreach (var (a, b) in new[] { ((40.0, 8.0), (50.0, 8.0)), ((50.0, 8.0), (50.0, 12.0)), ((50.0, 12.0), (40.0, 12.0)), ((40.0, 12.0), (40.0, 8.0)) })
        sk.AddEntity(new SkLine(new SkPoint(a.Item1, a.Item2), new SkPoint(b.Item1, b.Item2)));
    var axl = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(100, 0)) { Role = SkRole.Centerline });
    w.FinishSketch();
    part.AddFeature(new RevolveFeature { SketchId = sk.Id, AxisEntityId = axl.Id, Cut = true });
    s.Document.RebuildDirty();
    Console.WriteLine($"TUBE after revolve: errors {string.Join(";", part.Features.Select(f => f.Error))}; faces: {string.Join("; ", part.Bodies.SelectMany(b => b.Faces).Select(f => $"#{f.Id} {Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f))?.GetType().Name ?? f.Surface?.GetType().Name}"))}");
    // новый эскиз на XY после выреза
    w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp());
    var sk3 = vp.ActiveSketch!;
    s.Document.RebuildDirty(); vp.BuildOverlayForTest();
    Console.WriteLine($"TUBE new sketch after revolve: silhouettes {vp.SilhouetteDrawn}: {string.Join(" | ", vp.FaceRefItems()!.Select(x => x.R.Id + ":" + string.Join(",", x.L.Select(l => $"{l.A}-{l.B}"))))}");
    w.CancelSketch();
    w.EditSketch(part.Sketches.First(k => k.Id == sk.Id));
    s.Document.RebuildDirty(); vp.BuildOverlayForTest();
    Console.WriteLine($"TUBE edit revolve sketch (rolled back {w.SketchRolledBack}): silhouettes {vp.SilhouetteDrawn}");
    return 0;
}

if (only == "probe-edges")
{
    // разбор: рёбра детали после построения (теги граней, концы)
    var (dd, pp) = args[1] == "plate" ? ReorderModels.Plate(1) : ReorderModels.BoxHoleBoss(1);
    if (args.Length > 3) { pp.MoveFeature(pp.Features[int.Parse(args[2])], int.Parse(args[3])); dd.RebuildDirty(); }
    foreach (var f in pp.Features.OfType<RoundFeature>()) foreach (var r in f.Edges) Console.WriteLine($"R {f.Name} {r.Start} {r.End} A {r.FaceA} B {r.FaceB} e0 {r.End0} e1 {r.End1}");
    foreach (var f in pp.Features) Console.WriteLine($"F {f.Name} {f.Key} err {f.Error} area {ReorderModels.AreaOf(pp, f):0.###}");
    foreach (var fc in pp.Bodies.SelectMany(b => b.Faces).Where(x => x.Tag != null && x.Tag.Contains("round")))
        Console.WriteLine($"FACE {fc.Id} {fc.Surface.GetType().Name} {fc.Tag} loops {fc.Loops.Count} edges {fc.Loops.Sum(l => l.CoEdges.Count)}");
    Console.WriteLine("V " + pp.Volume + " warnings " + string.Join(" | ", pp.Warnings));
    foreach (var e in pp.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is not Parametric3D.Kernel.Curves.LineCurve3))
        Console.WriteLine($"E {e.Id} {e.Curve.GetType().Name} {e.V0.P} {e.V1.P} mid {e.Curve.PointAt(0.5)} A {e.FaceA?.Tag} B {e.FaceB?.Tag} tag {e.Tag}");
    return 0;
}

if (only == "probe-delrev")
{
    var ds = DocumentSession.NewPart(); var dp = ds.Part!;
    var sk = dp.AddSketch("S1", Parametric3D.Kernel.Frame3.XZ, dp.PlaneXZ.Id);
    foreach (var (a, b) in new[] { ((10.0, 0.0), (20.0, 0.0)), ((20.0, 0.0), (20.0, 10.0)), ((20.0, 10.0), (10.0, 10.0)), ((10.0, 10.0), (10.0, 0.0)) })
        sk.AddEntity(new SkLine(new SkPoint(a.Item1, a.Item2), new SkPoint(b.Item1, b.Item2)));
    var ax = new SkLine(new SkPoint(0, 0), new SkPoint(0, 10)) { Role = SkRole.Centerline }; sk.AddEntity(ax);
    var rv = new RevolveFeature { SketchId = sk.Id, AxisEntityId = ax.Id }; dp.AddFeature(rv);
    ds.Document.RebuildDirty();
    var w = Open(ds);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    Run();
    Console.WriteLine($"err {rv.Error} sketches {dp.Sketches.Count()}");
    w.SelectItem(rv); Run();
    Console.WriteLine("sel " + string.Join(",", w.SelectedItems().Select(i => i.GetType().Name + ":" + i.Name)));
    CommandRegistry.Instance["edit.delete"].Execute(); Run();
    var p2 = ds.Part!;
    Console.WriteLine($"after: features {p2.Features.Count} sketches {string.Join(",", p2.Sketches.Select(k => k.Name + " parent " + k.Parent?.GetType().Name))}");
    foreach (var k in p2.Sketches.ToList())
    {
        w.SelectItem(k); Run();
        Console.WriteLine("sel2 " + string.Join(",", w.SelectedItems().Select(i => i.GetType().Name + ":" + i.Name)) + " enabled " + CommandRegistry.Instance["edit.delete"].Enabled);
    }
    return 0;
}

if (only == "probe-reorder")
{
    var bad = ReorderModels.CheckAll((p, f) => TreeCommands.MoveRange(p, f), verbose: true);
    Console.WriteLine("BAD " + bad.Count);
    foreach (var b in bad) Console.WriteLine("BAD " + b);
    return 0;
}

if (only == "probe-formula")
{
    // разбор (k103): поле формулы с цветными именами — снимок (надпись поверх текста поля должна совпадать с ним)
    var ps = DocumentSession.NewPart(); var pp = ps.Part!;
    pp.AddSketch("S1", Parametric3D.Kernel.Frame3.XY, pp.PlaneXY.Id);
    var sk0 = pp.Sketches.First(); var l0 = sk0.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(10, 0))); sk0.AddDimension(DimensionType.DistancePointPoint, 10, l0.A, l0.B);
    var tbx = new TextBox { Text = "s\"S1\".d0 + s\"Nope\".d1 * 2 + d0 - d7", MinWidth = 300 };
    var fa = new FormulaAssist(tbx, () => pp, () => sk0);
    var pw = new Window { Width = 420, Height = 90, Content = new StackPanel { Margin = new Thickness(20), Children = { fa.View } } };
    pw.Show(); Dispatcher.UIThread.RunJobs(); pw.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    tbx.Focus(); tbx.CaretIndex = tbx.Text!.Length - 1; Dispatcher.UIThread.RunJobs();
    var frame = pw.CaptureRenderedFrame();
    frame?.Save(args.Length > 1 ? args[1] : "/tmp/formula.png");
    Console.WriteLine("spans " + string.Join(",", fa.Spans));
    return 0;
}

if (only == "probe-icons")
{
    // разбор: рамка рисунка каждой иконки (доля листа)
    foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory), "*.svg", SearchOption.AllDirectories).Select(Path.GetFileName).Concat(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "icons.tsv")).Where(l => !l.StartsWith("#") && l.Length > 0).Select(l => l.Split('\t')[0])).Distinct())
    {
        IconProvider.Get(f);
        Console.WriteLine($"ICON {f}: {(IconProvider.ContentBox(f!) is { } b ? $"{b.W:0.#}x{b.H:0.#} at {b.X:0.#},{b.Y:0.#}" : "full")}");
    }
    return 0;
}

if (only == "probe-load")
{
    // разбор: скругления на реальной детали, перезагрузка из снимка (как «отменить»/«вернуть») — время операции до и после
    var part = Parametric3D.App.Diagnostics.TestParts.Realistic(3, 12);
    part.Rebuild(null);
    long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
    Console.WriteLine($"PROBE memory start: {Mem() / 1048576.0:0.0} MB");
    var rnd = new Random(11);
    int nEdits = int.TryParse(Environment.GetEnvironmentVariable("P3D_PROBEN"), out var pn) ? pn : 24;
    var bodyRefs = new List<WeakReference>();
    for (int i = 0; i < nEdits; i++)
    {
        if ((i == 8 || i == 16) && nEdits == 24)
        {
            var (p2, _) = DocumentFile.Load(DocumentFile.Save(part, null)); p2.Rebuild(null); part = p2;
            Console.WriteLine($"PROBE memory before reload: {Mem() / 1048576.0:0.0} MB");
            Console.WriteLine($"PROBE reload: {part.Features.Count} features, bodies {part.Bodies.Count}, guarantee {string.Join(",", part.Bodies.Select(b => b.Guarantee))}");
        }
        var edges = part.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 6
                     && Topo.EdgeHandle(part, e.Id) != null).ToList();
        var e = edges[rnd.Next(edges.Count)];
        Feature f;
        if (i % 2 == 0) { var rf = new RoundFeature { Radius = new ValueRef(0.5 + rnd.NextDouble()) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e)); f = rf; }
        else { var cf = new ChamferFeature { D1 = new ValueRef(0.5 + rnd.NextDouble()), D2 = new ValueRef(0.7) }; cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e)); f = cf; }
        part.AddFeature(f);
        bool prof = i == 7 || i == 14;
        if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        part.Rebuild(null);
        if (prof) { Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(25))); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
        if (part.Bodies.Count > 0) bodyRefs.Add(new WeakReference(part.Bodies[0]));
        Console.WriteLine($"PROBE edit {i}: {sw.ElapsedMilliseconds} ms, feature {(part.LastFeatureMs.TryGetValue(f, out var ms) ? ms : -1):0} ms, err {f.Error?.Split('\n')[0]}");
        if (f.Error != null && Environment.GetEnvironmentVariable("P3D_PROBESAVE") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, $"probe_fail_{i}.p3d"), DocumentFile.Save(part, null));
    }
    {
        double vi = part.Volume; var errs = part.Features.Select(x => x.Error != null).ToList();
        if (Environment.GetEnvironmentVariable("P3D_PROBESAVE") is { Length: > 0 } dir2) File.WriteAllText(Path.Combine(dir2, "probe_end.p3d"), DocumentFile.Save(part, null));
        part.Invalidate(); part.Rebuild(null);
        var errs2 = part.Features.Select(x => x.Error != null).ToList();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"PROBE memory: {GC.GetTotalMemory(true) / 1048576.0:0.0} MB, features {part.Features.Count}, faces {part.Bodies.Sum(b => b.Faces.Count())}");
        Console.WriteLine($"PROBE intermediate bodies alive: {bodyRefs.Count(r => r.IsAlive)} of {bodyRefs.Count}");
        // оценка памяти по полям детали (обход графа отражением; общее — в первом поле, где встретилось)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            long Walk(object root, int budget)
            {
                long bytes = 0; var st = new Stack<object>(); st.Push(root);
                while (st.Count > 0 && budget-- > 0)
                {
                    var o = st.Pop();
                    if (!seen.Add(o)) continue;
                    var t = o.GetType();
                    if (t == typeof(string)) { bytes += 20 + 2L * ((string)o).Length; continue; }
                    if (t.IsArray)
                    {
                        var a = (Array)o; var et = t.GetElementType()!;
                        if (et.IsPrimitive || et.IsValueType && !et.IsGenericType && et.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).All(f => f.FieldType.IsValueType))
                        { bytes += 24 + (long)a.Length * (et.IsPrimitive ? System.Runtime.InteropServices.Marshal.SizeOf(et) : Math.Max(8, System.Runtime.CompilerServices.Unsafe.SizeOf<long>() * et.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Length)); continue; }
                        bytes += 24 + 8L * a.Length;
                        foreach (var x in a) if (x != null) { if (x.GetType().IsValueType) { foreach (var f in x.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) if (!f.FieldType.IsValueType && f.GetValue(x) is { } fv) st.Push(fv); } else st.Push(x); }
                        continue;
                    }
                    if (t.IsPrimitive || t.IsEnum || t.IsPointer) continue;
                    if (typeof(Delegate).IsAssignableFrom(t) || typeof(System.Reflection.MemberInfo).IsAssignableFrom(t) || t.Namespace?.StartsWith("Avalonia") == true) continue;
                    var fields = new List<System.Reflection.FieldInfo>();
                    for (var tt = t; tt != null && tt != typeof(object); tt = tt.BaseType) fields.AddRange(tt.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly));
                    bytes += 16 + 8L * fields.Count;
                    foreach (var f in fields)
                    {
                        if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType.IsPointer) continue;
                        object? v; try { v = f.GetValue(o); } catch { continue; }
                        if (v == null) continue;
                        if (v.GetType().IsValueType) { foreach (var g in v.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) if (!g.FieldType.IsValueType && !g.FieldType.IsPointer) { try { if (g.GetValue(v) is { } gv) st.Push(gv); } catch { } } }
                        else st.Push(v);
                    }
                }
                return bytes;
            }
            seen.Add(part);
            var pf = new List<System.Reflection.FieldInfo>();
            for (var tt = part.GetType(); tt != null && tt != typeof(object); tt = tt.BaseType) pf.AddRange(tt.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly));
            foreach (var f in pf)
            {
                if (f.GetValue(part) is not { } v || v.GetType().IsPrimitive) continue;
                long b = Walk(v, 20_000_000);
                if (b > 1 << 20) Console.WriteLine($"PROBE part field {f.Name}: ~{b / 1048576.0:0.0} MB");
            }
        }
        foreach (var asm in new[] { typeof(Part).Assembly, typeof(Parametric3D.Kernel.Topology.TSolid).Assembly, typeof(MainWindow).Assembly }.Distinct())
            foreach (var t in asm.GetTypes())
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (fi.FieldType.IsGenericTypeDefinition || t.IsGenericTypeDefinition) continue;
                    object? v; try { v = fi.GetValue(null); } catch { continue; }
                    int n = v switch { System.Collections.ICollection c => c.Count, _ => -1 };
                    if (n < 0 && v != null && v.GetType().GetProperty("Count") is { } cp && cp.PropertyType == typeof(int)) { try { n = (int)cp.GetValue(v)!; } catch { } }
                    if (n > 200) Console.WriteLine($"PROBE static {t.FullName}.{fi.Name}: {n} ({v!.GetType().Name})");
                }
        Console.WriteLine($"PROBE scratch: incremental {vi:R} vs from scratch {part.Volume:R}; failed features incremental [{string.Join(",", errs.Select((e, j) => e ? j : -1).Where(j => j >= 0))}] scratch [{string.Join(",", errs2.Select((e, j) => e ? j : -1).Where(j => j >= 0))}]");
        Console.WriteLine($"PROBE memory after scratch: {Mem() / 1048576.0:0.0} MB");
        part.ForgetCheckpoints();
        Console.WriteLine($"PROBE memory without checkpoints: {Mem() / 1048576.0:0.0} MB");
        var wrPart = new WeakReference(part); var wrBody = new WeakReference(part.Bodies[0]);
        part = null!;
        Console.WriteLine($"PROBE memory without the part: {Mem() / 1048576.0:0.0} MB; part alive {wrPart.IsAlive}, body alive {wrBody.IsAlive}");
        if (wrPart.Target is { } target) PathTo(target, Array.Empty<(object, string)>());
        // поточные ссылки рабочих потоков: отпустить на всех потоках пула
        for (int rep = 0; rep < 4; rep++) System.Threading.Tasks.Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 64 }, _ => { Parametric3D.Kernel.Diagnostics.ThreadState.Release(); Thread.Sleep(2); });
        Console.WriteLine($"PROBE memory after releasing worker thread state: {Mem() / 1048576.0:0.0} MB");
        // какие статические поля держат память: очищаются по одному, замер после каждого
        foreach (var asm in new[] { typeof(Part).Assembly, typeof(Parametric3D.Kernel.Topology.TSolid).Assembly }.Distinct())
            foreach (var t in asm.GetTypes())
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (fi.FieldType.IsGenericTypeDefinition || t.IsGenericTypeDefinition || fi.IsLiteral) continue;
                    object? v; try { v = fi.GetValue(null); } catch { continue; }
                    if (v == null) continue;
                    var clear = v.GetType().GetMethod("Clear", Type.EmptyTypes);
                    if (clear == null) continue;
                    long m0 = Mem();
                    try { clear.Invoke(v, null); } catch { continue; }
                    long m1 = Mem();
                    if (m0 - m1 > 1 << 20) Console.WriteLine($"PROBE cleared {t.FullName}.{fi.Name}: −{(m0 - m1) / 1048576.0:0.0} MB");
                }
        Console.WriteLine($"PROBE memory after clearing statics: {Mem() / 1048576.0:0.0} MB");
    }
    return 0;
}

if (only == "probe-ffround")
{
    // 17:31: FreeForm цилиндр ∪ шар, скругление / фаска ребра пересечения и верхней кромки — время шагов
    if (Environment.GetEnvironmentVariable("P3D_PROBEWARM") == "1")
    {
        var swW = System.Diagnostics.Stopwatch.StartNew();
        Parametric3D.App.Diagnostics.KernelWarmup.Run();
        Console.WriteLine($"PROBE warm-up: {swW.ElapsedMilliseconds} ms, feature 0 ms");
    }
    if (Environment.GetEnvironmentVariable("P3D_PROBEDRDBG") == "1") Parametric3D.Kernel.Ops.DirectRound.Debug = m => Console.WriteLine("  DR: " + m);
    if (Environment.GetEnvironmentVariable("P3D_PROBEREBDBG") == "1") Parametric3D.Kernel.Ops.FilletOp.RebuildDebug = m => Console.WriteLine("  REBUILD: " + m);
    if (Environment.GetEnvironmentVariable("P3D_PROBEARDBG") == "1") Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("  AR: " + m);
    int repsAll = int.TryParse(Environment.GetEnvironmentVariable("P3D_PROBEREPS"), out var rpa) ? rpa : 1;
    for (int repAll = 0; repAll < repsAll; repAll++)
    {
    var s = FreeFormCommands.NewFreeForm(); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    long Step(Feature f, string name)
    {
        f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f);
        bool prof = Environment.GetEnvironmentVariable("P3D_PROBEPROF") is { } pp && (pp == name || pp == "all");
        if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        s.Document.RebuildDirty();
        if (prof) { Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(int.TryParse(Environment.GetEnvironmentVariable("P3D_PROBEPROFN"), out var pfn) ? pfn : 30))); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
        Console.WriteLine($"PROBE {name}: {sw.ElapsedMilliseconds} ms, feature {(part.LastFeatureMs.TryGetValue(f, out var ms) ? ms : -1):0} ms, err {(Environment.GetEnvironmentVariable("P3D_PROBEFULLERR") == "1" ? f.Error : f.Error?.Split('\n')[0])}, bodies {part.Bodies.Count}, guarantee {string.Join(",", part.Bodies.Select(b => b.Guarantee))}");
        if (Environment.GetEnvironmentVariable("P3D_PROBEFULLERR") == "1") { Console.WriteLine($"  volume {part.Volume:0.###}"); foreach (var wn in part.Warnings) Console.WriteLine("  warn: " + wn); }
        return sw.ElapsedMilliseconds;
    }
    double PEnv(string n, double d) => double.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    Step(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(PEnv("P3D_PROBECYLR", 15)), B = new ValueRef(PEnv("P3D_PROBECYLH", 40)) }, "cylinder");
    Step(new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(PEnv("P3D_PROBESPHR", 20)) }, "sphere");
    Step(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(PEnv("P3D_PROBEDX", 8)), Dz = new ValueRef(PEnv("P3D_PROBEDZ", 5)) }, "move");
    Step(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } }, "union");
    string Kind(Parametric3D.Kernel.Topology.TEdge e) => string.Join("/", e.CoEdges.Select(c => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(c.Loop.Face)?.GetType().Name).OrderBy(x => x));
    foreach (var (name, kind, round) in new[] { ("round intersection", "ExtrudedSurface/RevolvedSurface", true), ("chamfer intersection", "ExtrudedSurface/RevolvedSurface", false), ("round top", "ExtrudedSurface/PlaneSurface", true), ("round bottom", "PlaneSurface/RevolvedSurface", true), ("round both", "ExtrudedSurface/RevolvedSurface", true), ("round rim", "ExtrudedSurface/PlaneSurface", true) })
    {
        if (Environment.GetEnvironmentVariable("P3D_PROBEONLY") is { Length: > 0 } po && !po.Split(',').Contains(name)) continue;
        var body = part.Bodies[0];
        if (!body.Edges.Any(x => Kind(x) == kind)) { Console.WriteLine($"PROBE {name}: no edge {kind}; kinds: {string.Join(", ", body.Edges.Select(Kind).Distinct())}"); continue; }
        var e = body.Edges.First(x => Kind(x) == kind && (name == "round rim" ? !ReferenceEquals(x.V0, x.V1) : kind.StartsWith("Extruded") && kind.Contains("Plane") ? ReferenceEquals(x.V0, x.V1) : kind.Contains("Plane") || !ReferenceEquals(x.V0, x.V1)));
        Feature f;
        double rProbe = (name == "round bottom" && double.TryParse(Environment.GetEnvironmentVariable("P3D_PROBER"), System.Globalization.CultureInfo.InvariantCulture, out var rpv)) ? rpv : 5;
        if (name is "round both" or "round rim") rProbe = PEnv("P3D_PROBER", 5);
        if (round) { var rf = new RoundFeature { Radius = new ValueRef(rProbe) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e));
            if (name == "round both") foreach (var e2 in body.Edges.Where(x => Kind(x) == "PlaneSurface/RevolvedSurface")) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e2));
            f = rf; }
        else { var cf = new ChamferFeature { D1 = new ValueRef(3), D2 = new ValueRef(3) }; cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e)); f = cf; }
        f.FFBody = part.BodyName(0);
        if (Environment.GetEnvironmentVariable("P3D_PROBEPREVIEW") == "1")
        {
            // как живой предпросмотр ручкой: проверки отложены, сетка грубее
            f.FreeForm = node.Key; part.AddFeature(f);
            var ks = Parametric3D.Kernel.KernelSettings.Capture();
            ks = ks with { Preview = true, LivePreview = true, Chord = ks.Chord * 4, ChordAngle = ks.ChordAngle * (double.TryParse(Environment.GetEnvironmentVariable("P3D_PROBEANGK"), System.Globalization.CultureInfo.InvariantCulture, out var angk) ? angk : 1) };
            bool profP = Environment.GetEnvironmentVariable("P3D_PROBEPROF") is { } ppp && (ppp == name || ppp == "all");
            if (profP) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
            var swp = System.Diagnostics.Stopwatch.StartNew();
            Part.BuildResult res;
            using (new Parametric3D.Kernel.KernelContext(ks).Enter()) res = part.Compute(null, null);
            if (profP) { Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(30))); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
            Console.WriteLine($"PROBE preview {name}: compute {swp.ElapsedMilliseconds} ms, feature {(res.FeatureMs.TryGetValue(f, out var fm) ? fm : -1):0} ms, err {(Environment.GetEnvironmentVariable("P3D_PROBEFULLERR") == "1" ? res.Errors.GetValueOrDefault(f) : res.Errors.GetValueOrDefault(f)?.Split('\n')[0])}, tris {(res.Mesh?.Indices.Length ?? 0) / 3}, defects {(res.Mesh == null ? -1 : Parametric3D.Kernel.Tess.MeshData.FloatDefects(res.Mesh))}, vol {res.Volume:0.##}");
            if (Environment.GetEnvironmentVariable("P3D_PROBESAVE") is { Length: > 0 } svp && res.Mesh != null)
            {
                var mm = res.Mesh; using var ow = new StreamWriter(Path.Combine(svp, "pv_" + name.Replace(' ', '_') + ".obj"));
                foreach (var mv in mm.Vertices) ow.WriteLine(FormattableString.Invariant($"v {mv.P.X} {mv.P.Y} {mv.P.Z}"));
                for (int i = 0; i < mm.Indices.Length; i += 3) ow.WriteLine($"f {mm.Indices[i] + 1} {mm.Indices[i + 1] + 1} {mm.Indices[i + 2] + 1}");
            }
            if (Environment.GetEnvironmentVariable("P3D_PROBECHAIN") == "1" && name != "round bottom") { s.Document.RebuildDirty(); continue; }
            part.RemoveFeature(f); s.Document.RebuildDirty();
            continue;
        }
        Step(f, name);
        if (Environment.GetEnvironmentVariable("P3D_PROBESAVE") is { Length: > 0 } sv && part.Bodies.Count > 0 && f.Error == null)
        {
            var b0 = part.Bodies[0];
            Console.WriteLine($"PROBE {name}: exact {b0.Exact != null}, auth {b0.ExactAuthoritative}, faceted {b0.Faces.Count(x => x.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface)} [{string.Join(" ", b0.Faces.GroupBy(x => (x.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Source?.GetType().Name ?? x.Surface.GetType().Name).Select(g => g.Key + ":" + g.Count()))}], faces {b0.Faces.Count()}, edges {b0.Edges.Count()}, volume {part.Volume:0.###}, issues {string.Join("; ", Parametric3D.Kernel.Topology.SolidValidator.Validate(b0).Issues.Take(5).Select(i => i.ToString()))}");
            if (Environment.GetEnvironmentVariable("P3D_PROBEFACES") == "1")
                foreach (var fc in b0.Faces)
                {
                    var fsx = fc.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface;
                    double ar = 0; if (fsx != null) foreach (var (a, b, c) in fsx.Facets) ar += OpenTK.Mathematics.Vector3d.Cross(b - a, c - a).Length / 2;
                    var cen = OpenTK.Mathematics.Vector3d.Zero; int cn = 0; if (fsx != null) foreach (var (a, b, c) in fsx.Facets) { cen += a + b + c; cn += 3; }
                    Console.WriteLine($"  face {fc.Id} {fc.Tag} src {fsx?.Source?.GetType().Name} facets {fsx?.Facets.Count} area {ar:0.###} loops {fc.Loops.Count} edges {fc.Edges.Count()} at {(cn > 0 ? cen / cn : cen):0.##}");
                }
            using (var ow = new StreamWriter(Path.Combine(sv, name.Replace(' ', '_') + ".obj"))) Parametric3D.Kernel.Export.MeshExport.WriteObj(ow, Parametric3D.Kernel.Export.MeshExport.Triangles(part.Bodies), name);
        }
        if (Environment.GetEnvironmentVariable("P3D_PROBECHAIN") != "1") { part.RemoveFeature(f); s.Document.RebuildDirty(); }
    }
    }
    return 0;
}

if (only == "probe-ffall")
{
    // 23:50: «скорость всех операций» — набор FreeForm-операций (заготовки, булевы всех видов, скругления/фаски разных рёбер,
    // перенос/поворот/масштаб, зеркало, массив, разрез). Каждая — в своём документе; время пересчёта детали и операции.
    // P3D_PROBEREPS — повторы, P3D_PROBEPROF=<имя>|all — профиль ядра, P3D_PROBEWARM=1 — прогрев.
    if (Environment.GetEnvironmentVariable("P3D_PROBEWARM") == "1") Parametric3D.App.Diagnostics.KernelWarmup.Run();
    int reps = int.TryParse(Environment.GetEnvironmentVariable("P3D_PROBEREPS"), out var rpx) ? rpx : 1;
    string KindOf(Parametric3D.Kernel.Topology.TEdge e) => string.Join("/", e.CoEdges.Select(c => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(c.Loop.Face)?.GetType().Name).OrderBy(x => x));
    FFPrimitiveFeature Prim(FFPrimitive sh, double a, double b = 0, double c = 0, double x = 0, double y = 0, double z = 0) =>
        new() { Shape = sh, A = new ValueRef(a), B = new ValueRef(b > 0 ? b : 30), C = new ValueRef(c > 0 ? c : 20), Origin = new OpenTK.Mathematics.Vector3d(x, y, z) };
    Feature Bool(FFBool op) => new FFBooleanFeature { Op = op, Target = "Тело 1", Tools = { "Тело 2" } };
    Feature EdgeOp(Part part, Func<Parametric3D.Kernel.Topology.TEdge, bool> pick, bool round, double d)
    {
        var e = part.Bodies[0].Edges.First(pick);
        Feature f;
        if (round) { var rf = new RoundFeature { Radius = new ValueRef(d) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e)); f = rf; }
        else { var cf = new ChamferFeature { D1 = new ValueRef(d), D2 = new ValueRef(d) }; cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e)); f = cf; }
        f.FFBody = part.BodyName(0); return f;
    }
    bool Line(Parametric3D.Kernel.Topology.TEdge e) => e.Curve is Parametric3D.Kernel.Curves.LineCurve3;
    bool Closed(Parametric3D.Kernel.Topology.TEdge e) => ReferenceEquals(e.V0, e.V1);
    var scenes = new List<(string Name, Func<Feature>[] Setup, Func<Part, Feature> Op)>
    {
        ("box", new Func<Feature>[0], _ => Prim(FFPrimitive.Box, 40, 30, 20)),
        ("torus", new Func<Feature>[0], _ => Prim(FFPrimitive.Torus, 20, 5)),
        ("sphere", new Func<Feature>[0], _ => Prim(FFPrimitive.Sphere, 20)),
        ("cylinder", new Func<Feature>[0], _ => Prim(FFPrimitive.Cylinder, 15, 40)),
        ("cone", new Func<Feature>[0], _ => Prim(FFPrimitive.Cone, 15, 40, 5)),
        ("union box+cyl", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 8, 30, 0, 10, 0, 5) }, _ => Bool(FFBool.Union)),
        ("subtract box-cyl", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 6, 40, 0, 0, 0, -10) }, _ => Bool(FFBool.Subtract)),
        ("subtract box-sphere", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Sphere, 12, 0, 0, 20, 15, 20) }, _ => Bool(FFBool.Subtract)),
        ("intersect box*sphere", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Sphere, 22, 0, 0, 0, 0, 10) }, _ => Bool(FFBool.Intersect)),
        ("union cyl+cyl", new[] { () => (Feature)Prim(FFPrimitive.Cylinder, 10, 40), () => new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(7), B = new ValueRef(50), Origin = new OpenTK.Mathematics.Vector3d(-25, 0, 20), U = OpenTK.Mathematics.Vector3d.UnitY, V = OpenTK.Mathematics.Vector3d.UnitZ } }, _ => Bool(FFBool.Union)),
        ("subtract cone-box", new[] { () => (Feature)Prim(FFPrimitive.Cone, 15, 5, 30), () => Prim(FFPrimitive.Box, 40, 40, 10, 0, 0, 25) }, _ => Bool(FFBool.Subtract)),
        ("union torus+box", new[] { () => (Feature)Prim(FFPrimitive.Torus, 20, 5), () => Prim(FFPrimitive.Box, 12, 10, 20, 20, 0, -10) }, _ => Bool(FFBool.Union)),
        ("union torus+box tangent", new[] { () => (Feature)Prim(FFPrimitive.Torus, 20, 5), () => Prim(FFPrimitive.Box, 10, 10, 20, 20, 0, -10) }, _ => Bool(FFBool.Union)),
        ("union torus+box outer tangent", new[] { () => (Feature)Prim(FFPrimitive.Torus, 20, 5), () => Prim(FFPrimitive.Box, 9, 10, 20, 20.5, 0, -10) }, _ => Bool(FFBool.Union)),
        ("round box edge", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20) }, p => EdgeOp(p, Line, true, 3)),
        ("chamfer box edge", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20) }, p => EdgeOp(p, Line, false, 3)),
        ("round cyl top", new[] { () => (Feature)Prim(FFPrimitive.Cylinder, 15, 40) }, p => EdgeOp(p, Closed, true, 3)),
        ("round hole edge", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 6, 40, 0, 0, 0, -10), () => Bool(FFBool.Subtract) }, p => EdgeOp(p, e => Closed(e) && KindOf(e).Contains("Extruded"), true, 2)),
        ("round box-sphere edge", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Sphere, 12, 0, 0, 20, 15, 20), () => Bool(FFBool.Subtract) }, p => EdgeOp(p, e => !Line(e) && KindOf(e).Contains("Revolved"), true, 2)),
        ("move", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 6, 40, 0, 0, 0, -10), () => Bool(FFBool.Subtract) }, _ => new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(10) }),
        ("rotate", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 6, 40, 0, 0, 0, -10), () => Bool(FFBool.Subtract) }, _ => new FFTransformFeature { Mode = FFXform.Rotate, BodyNames = { "Тело 1" }, Angle = new ValueRef(30) }),
        ("scale xyz", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Sphere, 12, 0, 0, 20, 15, 20), () => Bool(FFBool.Subtract) }, _ => new FFTransformFeature { Mode = FFXform.Scale, BodyNames = { "Тело 1" }, Factor = new ValueRef(1.5) }),
        ("mirror", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 6, 40, 0, 0, 0, -10), () => Bool(FFBool.Subtract) }, _ => new FFMirrorFeature { BodyNames = { "Тело 1" }, PlaneAt = new OpenTK.Mathematics.Vector3d(30, 0, 0) }),
        ("array 4", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 6, 40, 0, 0, 0, -10), () => Bool(FFBool.Subtract) }, _ => new FFArrayFeature { BodyNames = { "Тело 1" }, Count = new ValueRef(4), Step = new OpenTK.Mathematics.Vector3d(50, 0, 0) }),
        ("split", new[] { () => (Feature)Prim(FFPrimitive.Box, 40, 30, 20), () => Prim(FFPrimitive.Cylinder, 6, 40, 0, 0, 0, -10), () => Bool(FFBool.Subtract) }, _ => new FFSplitFeature { Body = "Тело 1", PlaneAt = new OpenTK.Mathematics.Vector3d(0, 0, 10), Normal = OpenTK.Mathematics.Vector3d.UnitZ }),
    };
    string? onlyScene = Environment.GetEnvironmentVariable("P3D_PROBESCENE");
    if (Environment.GetEnvironmentVariable("P3D_PROBEROUTE") == "1") Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("  route: " + m);
    var totals = new Dictionary<string, List<double>>();
    for (int rep = 0; rep < reps; rep++)
        foreach (var (name, setup, op) in scenes)
        {
            if (onlyScene != null && onlyScene != name) continue;
            var s = FreeFormCommands.NewFreeForm(); var part = s.Part!;
            var node = part.Features.OfType<FreeFormFeature>().Single();
            foreach (var mk in setup) { var f0 = mk(); f0.FreeForm = node.Key; f0.Frozen = true; part.AddFeature(f0); s.Document.RebuildDirty(); }
            var f = op(part); f.FreeForm = node.Key; f.Frozen = true;
            bool prof = rep == reps - 1 && Environment.GetEnvironmentVariable("P3D_PROBEPROF") is { } pp && (pp == name || pp == "all");
            if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
            part.AddFeature(f);
            long ah0 = Parametric3D.Kernel.Topology.GeometryAudit.CacheHits, am0 = Parametric3D.Kernel.Topology.GeometryAudit.CacheMisses;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Environment.GetEnvironmentVariable("P3D_PROBEPREVIEW") == "1")
            {
                // 21.10: как предпросмотр операции в окне (Document: Preview + LivePreview, сетка грубее)
                var ksP = Parametric3D.Kernel.KernelSettings.Capture();
                ksP = ksP with { Preview = true, LivePreview = true, Chord = ksP.Chord * s.Document.PreviewCoarseness };
                Part.BuildResult resP;
                using (new Parametric3D.Kernel.KernelContext(ksP).Enter()) resP = part.Compute(null, null);
                part.Apply(resP);
            }
            else s.Document.RebuildDirty();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (prof) Console.WriteLine($"  audit cache hits {Parametric3D.Kernel.Topology.GeometryAudit.CacheHits - ah0} misses {Parametric3D.Kernel.Topology.GeometryAudit.CacheMisses - am0}");
            if (prof) { Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(int.TryParse(Environment.GetEnvironmentVariable("P3D_PROBEPROFN"), out var ppn) ? ppn : 25))); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
            double fms = part.LastFeatureMs.TryGetValue(f, out var fm) ? fm : -1;
            (totals.TryGetValue(name, out var l) ? l : totals[name] = new List<double>()).Add(ms);
            if (Environment.GetEnvironmentVariable("P3D_PROBEWARN") == "1") foreach (var wn in part.Warnings.Distinct()) Console.WriteLine("  warn: " + wn.Split('\n')[0]);
            Console.WriteLine($"PROBE {name}: {ms:0} ms, feature {fms:0} ms, err {(Environment.GetEnvironmentVariable("P3D_PROBEFULLERR") == "1" ? f.Error : f.Error?.Split('\n')[0])}, bodies {part.Bodies.Count}, faces {part.Bodies.Sum(b => b.Faces.Count())}, vol {part.Volume:R}, guarantee {string.Join(",", part.Bodies.Select(b => b.Guarantee).Distinct())}");
        }
    foreach (var (name, l) in totals) Console.WriteLine($"MIN {name}: {l.Min():0} ms");
    Console.WriteLine($"SUM of minima: {totals.Values.Sum(l => l.Min()):0} ms");
    return 0;
}

if (only == "probe-ffui")
{
    // 20:17: та же сцена, что probe-ffround, но через окно: команда «Скругление» на выбранном ребре, «ОК» — время шагов окна
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    void Add(Feature f) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs(); }
    if (Environment.GetEnvironmentVariable("P3D_PROBEWARM") == "1") Parametric3D.App.Diagnostics.KernelWarmup.Run();
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(15), B = new ValueRef(40) });
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(20) });
    Add(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(8), Dz = new ValueRef(5) });
    Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
    string Kind(Parametric3D.Kernel.Topology.TEdge e) => string.Join("/", e.CoEdges.Select(c => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(c.Loop.Face)?.GetType().Name).OrderBy(x => x));
    for (int rep = 0; rep < 2; rep++)
    foreach (var (name, kind, cmd) in new[] { ("round intersection", "ExtrudedSurface/RevolvedSurface", "ff.fillet"), ("chamfer intersection", "ExtrudedSurface/RevolvedSurface", "ff.chamfer"), ("round top", "ExtrudedSurface/PlaneSurface", "ff.fillet") })
    {
        var e = part.Bodies[0].Edges.First(x => Kind(x) == kind && (kind.Contains("Plane") ? ReferenceEquals(x.V0, x.V1) : !ReferenceEquals(x.V0, x.V1)));
        w.ViewPort.Selection.Clear(); w.ViewPort.Selection.Select(new PickRef(e.Id, PickKind.Edge, part, null), additive: false);
        bool prof = Environment.GetEnvironmentVariable("P3D_PROBEPROF") is { } pp && (pp == name || pp == "all") && rep == 1;
        if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
        var probeT0 = DateTime.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CommandRegistry.Instance[cmd].Execute();
        long tStart = sw.ElapsedMilliseconds;
        s.Document.RebuildDirty();
        long tRebuilt = sw.ElapsedMilliseconds;
        bool profJobs = Environment.GetEnvironmentVariable("P3D_PROBEJOBS") == "1" && rep == 1;
        var gcP0 = GC.GetTotalPauseDuration(); int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
        if (profJobs) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
        Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("P3D_PROBEJOBS") == "1") Console.WriteLine($"  jobs: gc pause {(GC.GetTotalPauseDuration() - gcP0).TotalMilliseconds:0} ms, gen0 {GC.CollectionCount(0) - gc0}, gen2 {GC.CollectionCount(2) - gc2}");
        if (profJobs) { Console.WriteLine("  jobs profile:\n" + string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(15))); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
        long tBuilt = sw.ElapsedMilliseconds;
        if (Environment.GetEnvironmentVariable("P3D_PROBESCENEB") == "1")
        {
            var swS = System.Diagnostics.Stopwatch.StartNew();
            var scn = Parametric3D.Render.Engine.SceneBuilder.Build(s.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions());
            long t1 = swS.ElapsedMilliseconds; swS.Restart();
            Parametric3D.Render.Engine.SceneBuilder.Build(s.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions());
            Console.WriteLine($"  scene build {t1} ms, again {swS.ElapsedMilliseconds} ms");
        }
        var f = (Feature?)w.CurrentOperation?.EditedFeature;
        w.CommitOperation(); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
        long tDone = sw.ElapsedMilliseconds;
        if (prof) { Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(40))); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
        foreach (var g in Parametric3D.App.Diagnostics.UiTimes.Since(probeT0).GroupBy(x => x.Name).OrderByDescending(g => g.Sum(x => x.Ms)).Take(12))
            Console.WriteLine($"  ui: {g.Key} {g.Sum(x => x.Ms):0.0} ms ×{g.Count()}");
        Console.WriteLine($"PROBE ui {name}: start {tStart} ms, rebuilt {tRebuilt} ms, built {tBuilt} ms, committed {tDone} ms, feature {(f != null && part.LastFeatureMs.TryGetValue(f, out var fm) ? fm : -1):0} ms, err {f?.Error?.Split('\n')[0]}");
        if (f != null) { part.RemoveFeature(f); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs(); }
    }
    w.Close();
    return 0;
}

if (only == "probe-leak")
{
    // разбор: держит ли окно детали, заменённые отменой/повтором
    var s0 = DocumentSession.FromPart(Parametric3D.App.Diagnostics.TestParts.Realistic(3, 12), "leak");
    var w0 = Open(s0);
    s0.Document.RebuildDirty();
    var weak = new List<WeakReference>();
    var rnd = new Random(5);
    for (int i = 0; i < 6; i++)
    {
        var p = s0.Part!;
        weak.Add(new WeakReference(p));
        var e = p.Bodies.SelectMany(b => b.Edges).Where(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (x.V0.P - x.V1.P).Length > 6).ElementAt(rnd.Next(10));
        w0.ViewPort.Selection.Select(new PickRef(e.Id, PickKind.Edge, p, null), additive: false);
        w0.StartOperation(new RoundOp()); Dispatcher.UIThread.RunJobs(); s0.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
        w0.CommitOperation(); s0.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
        weak.Add(new WeakReference(s0.Part));
        s0.Undo(); s0.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
        s0.Redo(); s0.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    }
    w0.ViewPort.Selection.Clear(); Dispatcher.UIThread.RunJobs();
    for (int k = 0; k < 3; k++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    Console.WriteLine($"LEAK parts alive {weak.Count(x => x.IsAlive)} of {weak.Count}; memory {GC.GetTotalMemory(true) / 1048576.0:0.0} MB");
    foreach (var wr in weak.Where(x => x.IsAlive && !ReferenceEquals(x.Target, s0.Part)).Take(1)) PathTo(wr.Target!, new (object, string)[] { (w0, "window"), (s0, "session") });
    return 0;
}

static void PathTo(object target, (object, string)[] extra)
{
            // путь от статических полей к детали (обход в ширину отражением)
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var parent = new Dictionary<object, (object? from, string via)>(ReferenceEqualityComparer.Instance);
            var q = new Queue<object>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                Type[] ts; try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var t in ts)
                {
                    if (t.IsGenericTypeDefinition || t.ContainsGenericParameters) continue;
                    foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (fi.IsLiteral || fi.FieldType.IsPrimitive) continue;
                        object? v; try { v = fi.GetValue(null); } catch { continue; }
                        if (v == null || v.GetType().IsValueType && !v.GetType().IsGenericType) { if (v == null) continue; }
                        if (parent.TryAdd(v, (null, $"{t.FullName}.{fi.Name}"))) q.Enqueue(v);
                    }
                }
            }
            foreach (var (eo, en) in extra) if (parent.TryAdd(eo, (null, en))) q.Enqueue(eo);
            bool found = false; int visited = 0;
            void Push(object from, object? v, string via)
            {
                if (v == null) return; var vt = v.GetType();
                if (vt.IsPrimitive || vt.IsEnum || vt == typeof(string) || vt.IsPointer) return;
                if (vt.IsValueType)
                {
                    foreach (var g in vt.GetFields(BF)) { if (g.FieldType.IsPrimitive || g.FieldType.IsPointer) continue; object? gv; try { gv = g.GetValue(v); } catch { continue; } Push(from, gv, via + "." + g.Name); }
                    return;
                }
                if (parent.TryAdd(v, (from, via))) q.Enqueue(v);
            }
            while (q.Count > 0 && !found && visited++ < 30_000_000)
            {
                var o = q.Dequeue();
                if (ReferenceEquals(o, target)) { found = true; break; }
                var t = o.GetType();
                if (typeof(System.Reflection.MemberInfo).IsAssignableFrom(t) || typeof(System.Reflection.Assembly).IsAssignableFrom(t) || typeof(System.Reflection.Module).IsAssignableFrom(t)) continue;
                if (t.IsArray) { int k = 0; if (t.GetElementType()!.IsPrimitive) continue; foreach (var x in (Array)o) Push(o, x, $"[{k++}]"); continue; }
                if (o is System.Runtime.CompilerServices.ConditionalWeakTable<object, object>) continue;
                for (var tt = t; tt != null && tt != typeof(object); tt = tt.BaseType)
                    foreach (var f in tt.GetFields(BF | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType.IsPrimitive || f.FieldType.IsPointer) continue;
                        object? v; try { v = f.GetValue(o); } catch { continue; }
                        Push(o, v, f.Name);
                    }
            }
            if (!found) Console.WriteLine($"PROBE path to part: not found from statics (visited {visited})");
            else
            {
                var path = new List<string>(); object? cur = target;
                while (cur != null && parent.TryGetValue(cur, out var pr)) { path.Add($"{pr.via} -> {cur.GetType().Name}"); cur = pr.from; }
                path.Reverse(); Console.WriteLine("PROBE path to part:\n  " + string.Join("\n  ", path));
            }
        }

string IconsTsv = Path.Combine(AppContext.BaseDirectory, "icons.tsv");

Check("UI-COMMAND-REGISTRY", () =>
{
    // реестр: уникальные имена, у каждой команды есть подпись и группа; иконки — только из списка UI_ICONS (icons.tsv)
    var w = Open(DocumentSession.NewPart());
    var reg = CommandRegistry.Instance;
    Require(reg.All.Count >= 30, $"commands: {reg.All.Count}");
    Require(reg.All.Select(c => c.Id).Distinct().Count() == reg.All.Count, "duplicate ids");
    Require(reg.All.All(c => !string.IsNullOrWhiteSpace(c.Label) && !string.IsNullOrWhiteSpace(c.Group)), "a command without label or group");
    var known = File.ReadAllLines(IconsTsv).Where(l => !l.StartsWith("#") && l.Length > 0).Select(l => l.Split('\t')[0]).ToHashSet();
    var bad = reg.All.Where(c => c.Icon != null && !known.Contains(c.Icon)).Select(c => $"{c.Id}:{c.Icon}").ToList();
    Require(bad.Count == 0, "icons not in UI_ICONS: " + string.Join(", ", bad));
    // клавиши без конфликтов
    // одна клавиша допустима у операции модели и у инструмента эскиза (они не доступны одновременно)
    var g = reg.All.Where(c => c.Gesture != null).GroupBy(c => c.Gesture!.ToString())
        .Where(x => x.Count() > 1 && !(x.Count() == 2 && x.Any(c => c.Group == "Эскиз") && x.Any(c => c.Group is "Модель" or "Правка")))
        .Select(x => x.Key).ToList();
    Require(g.Count == 0, "gesture conflicts: " + string.Join(", ", g));
    w.Close();
});

Check("UI-PALETTE-SEARCH", () =>
{
    // палитра (F): поиск по-русски и по-английски, все слова запроса (UI-10)
    var w = Open(DocumentSession.NewPart());
    var reg = CommandRegistry.Instance;
    Require(reg.Search("сохран").FirstOrDefault()?.Id == "file.save", "«сохран» → " + reg.Search("сохран").FirstOrDefault()?.Id);
    Require(reg.Search("save").Any(c => c.Id == "file.save_as"), "«save» does not find save as");
    Require(reg.Search("front").FirstOrDefault()?.Id == "view.front", "«front»");
    Require(reg.Search("вид сверху").FirstOrDefault()?.Id == "view.top", "«вид сверху»");
    Require(reg.Search("нет такой команды").Count == 0, "garbage query found something");
    // палитра открывается по F и выполняет команду Enter
    w.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
    Dispatcher.UIThread.RunJobs();
    var shade = w.FindControl<Avalonia.Controls.Border>("PaletteShade")!;
    Require(shade.IsVisible, "F does not open the palette");
    var input = w.FindControl<Avalonia.Controls.TextBox>("PaletteInput")!;
    input.Text = "вид сверху";
    Dispatcher.UIThread.RunJobs();
    input.Focus();
    w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
    Dispatcher.UIThread.RunJobs();
    Require(!shade.IsVisible, "Enter does not close the palette");
    Require(w.ViewPort.Transition.IsRunning, "the command did not start a view transition");
    w.Close();
});

Check("UI-UNDO-REDO", () =>
{
    // одна правка — один шаг отмены; отмена и повтор возвращают модель (UI-22, UI-37)
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    int f0 = part.Children.OfType<Feature>().Count();
    Require(f0 > 0, "the sample part has no features");
    var feat = part.Children.OfType<Feature>().Last();
    w.SelectItem(feat);
    CommandRegistry.Instance["edit.delete"].Execute();
    Require(s.Part!.Children.OfType<Feature>().Count() == f0 - 1, "delete did not remove the feature");
    Require(s.CanUndo && s.UndoNames.Count == 1, "no undo step");
    CommandRegistry.Instance["edit.undo"].Execute();
    Require(s.Part!.Children.OfType<Feature>().Count() == f0, "undo did not restore");
    Require(s.CanRedo, "no redo");
    CommandRegistry.Instance["edit.redo"].Execute();
    Require(s.Part!.Children.OfType<Feature>().Count() == f0 - 1, "redo did not delete again");
    w.Close();
});

Check("UI-FILE-ROUNDTRIP", () =>
{
    // сохранить и открыть: тот же замысел (операции) и тот же объём после перестроения
    var s = DocumentSession.Sample();
    var w = Open(s);
    s.Part!.Rebuild(s.Document.Parameters);
    double v0 = s.Part.Volume;
    string path = Path.Combine(Path.GetTempPath(), $"uicheck_{Guid.NewGuid():N}.p3d");
    s.SaveAs(path);
    Require(!s.Modified, "modified after save");
    var s2 = DocumentSession.Open(path);
    s2.Part!.Rebuild(s2.Document.Parameters);
    Require(s2.Part.Children.OfType<Feature>().Count() == s.Part.Children.OfType<Feature>().Count(), "feature count differs");
    Require(Math.Abs(s2.Part.Volume - v0) <= 1e-9 * Math.Max(1, v0), $"volume {s2.Part.Volume} vs {v0}");
    // экспорт: точный STEP и STL
    string step = Path.ChangeExtension(path, ".step"), stl = Path.ChangeExtension(path, ".stl");
    string m1 = ExportService.Export(s2, step), m2 = ExportService.Export(s2, stl);
    Require(File.Exists(step) && m1.Contains("точный"), "STEP: " + m1);
    Require(File.Exists(stl) && m2.Contains("замкнута"), "STL: " + m2);
    // m.x2: IGES из интерфейса — записан, прочитан обратно с тем же объёмом
    string igs = Path.ChangeExtension(path, ".igs"); string m3 = ExportService.Export(s2, igs);
    Require(File.Exists(igs) && m3.Contains("объём сходится"), "IGES: " + m3);
    foreach (var f in new[] { path, step, stl, igs }) File.Delete(f);
    w.Close();
});

Check("UI-VIEW-PICK", () =>
{
    // выбор в виде мышью: наведение на середину куба — грань (подсветка), щелчок — выбор, Shift — добавить, щелчок в
    // пустоту — снять выбор; выбор детали в дереве — тело (UI-21, раздел 2)
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    vp.Show.Planes = false; vp.MarkDirty();
    vp.Camera.State.Rotation = ModelViewport.HomeRotation;
    vp.ZoomToFit(animate: false);
    var c = vp.TranslatePoint(new Point(vp.Bounds.Width / 2, vp.Bounds.Height / 2), w)!.Value;
    w.MouseMove(c);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.Hover.Kind == PickKind.Face || vp.Selection.Hover.Kind == PickKind.Edge || vp.Selection.Hover.Kind == PickKind.Vertex,
        $"hover at the middle: {vp.Selection.Hover.Kind}");
    w.MouseDown(c, MouseButton.Left); w.MouseUp(c, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.Count == 1, $"selected {vp.Selection.Count}");
    var first = vp.Selection.Primary;
    var c2 = new Point(c.X + 60, c.Y + 60);
    w.MouseMove(c2); w.MouseDown(c2, MouseButton.Left, RawInputModifiers.Shift); w.MouseUp(c2, MouseButton.Left, RawInputModifiers.Shift);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.Count == 2 || vp.Selection.Primary.Id == first.Id, $"shift-add: {vp.Selection.Count}");
    var empty = vp.TranslatePoint(new Point(8, vp.Bounds.Height - 8), w)!.Value;
    w.MouseMove(empty); w.MouseDown(empty, MouseButton.Left); w.MouseUp(empty, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.IsEmpty, "click on empty space did not clear");
    w.SelectItem(s.Part!);
    Require(vp.Selection.Primary.Kind == PickKind.Body, "tree part → body");
    // Esc снимает выбор
    w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
    Dispatcher.UIThread.RunJobs();
    Require(vp.Selection.IsEmpty, "Esc did not clear the selection");
    w.Close();
});

Check("UI-VIEWCUBE-ORIENT", () =>
{
    // куб вида (UI-45): грань — взгляд по нормали, ребро — по биссектрисе, угол — изометрия; верх не переворачивается;
    // согласовано с прежними видами камеры (Camera.OrientationQuat)
    static Vector3 Fwd(Quaternion q) => Vector3.Transform(-Vector3.UnitZ, Quaternion.Invert(q));
    static Vector3 Up(Quaternion q) => Vector3.Transform(Vector3.UnitY, Quaternion.Invert(q));
    var fails = new List<string>();
    for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (x == 0 && y == 0 && z == 0) continue;
                var d = new Vector3(x, y, z);
                var q = ViewMath.LookFrom(d, Vector3.UnitY);
                var f = Fwd(q);
                if ((f + d.Normalized()).Length > 1e-5f) fails.Add($"{d}: forward {f}");
                var u = Up(q);
                if (MathF.Abs(Vector3.Dot(u, f)) > 1e-5f) fails.Add($"{d}: up not orthogonal");
                if (y == 0 && u.Y < 0.5f) fails.Add($"{d}: up {u}");
            }
    foreach (var (o, d) in new[] { (ViewOrientation.Front, Vector3.UnitZ), (ViewOrientation.Top, Vector3.UnitY), (ViewOrientation.Right, Vector3.UnitX), (ViewOrientation.Back, -Vector3.UnitZ), (ViewOrientation.Left, -Vector3.UnitX) })
        if ((Fwd(Camera.OrientationQuat(o)) + d).Length > 1e-5f) fails.Add($"{o}: camera forward {Fwd(Camera.OrientationQuat(o))}");
    // вид сверху с верхом «назад» — как Camera.OrientationQuat(Top)
    var top = ViewMath.LookFrom(Vector3.UnitY, -Vector3.UnitZ);
    if ((Up(top) + Vector3.UnitZ).Length > 1e-5f) fails.Add($"top up {Up(top)}");
    // клик по кубу запускает плавный переход к нужному виду
    var w = Open(DocumentSession.Sample());
    var cube = w.FindControl<ViewCube>("Cube")!;
    cube.Click(new Vector3i(1, 0, 0), 0);
    Require(w.ViewPort.Transition.IsRunning, "cube click did not start a transition");
    // поворот на 90° вокруг взгляда сохраняет направление взгляда
    var q0 = ViewMath.LookFrom(Vector3.UnitZ, Vector3.UnitY);
    var q1 = Quaternion.FromAxisAngle(Vector3.UnitZ, MathHelper.PiOver2) * q0;
    if ((Fwd(q1) - Fwd(q0)).Length > 1e-5f) fails.Add("rotate 90 changes the view direction");
    if (MathF.Abs(Vector3.Dot(Up(q1), Up(q0))) > 1e-5f) fails.Add("rotate 90 does not turn the up vector");
    w.Close();
    Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
});

Check("UI-THEME-ICONS", () =>
{
    // тема: тёмная/светлая меняет тему Avalonia, цвет перекраски иконок и палитру вида; перекраска трогает только чёрный
    var w = Open(DocumentSession.NewPart());
    w.ApplyTheme(false);
    Require(Application.Current!.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light, "light theme not applied");
    Require(IconProvider.Foreground.R < 80, "icon foreground on light theme");
    Require(!w.ViewPort.DarkTheme, "view palette");
    w.ApplyTheme(true);
    Require(IconProvider.Foreground.R > 200, "icon foreground on dark theme");
    string svg = "<svg><path stroke=\"#000000\" fill=\"#FF7A1A\"/><path style=\"fill:black;stroke:#000\"/></svg>";
    string r = IconProvider.Recolor(svg, "#E6E8EC");
    Require(r.Contains("stroke=\"#E6E8EC\"") && r.Contains("#FF7A1A") && r.Contains("fill:#E6E8EC") && r.Contains("stroke:#E6E8EC"), r);
    // контур без fill (по умолчанию чёрный) и почти чёрный #191919 — тоже цвет темы; серые и белые — как есть
    string r2 = IconProvider.Recolor("<svg width=\"2\"><path d=\"M0 0\"/><path fill=\"#191919\"/><path fill=\"#CCCCCC\"/></svg>", "#E6E8EC");
    Require(r2.StartsWith("<svg fill=\"#E6E8EC\"") && !r2.Contains("#191919") && r2.Contains("#333333"), r2);
    // тёмная тема: белое → чёрное, светло-серое → тёмно-серое, чёрное → цвет текста; цветное — как есть; светлая — как нарисовано
    string r3 = IconProvider.Recolor("<svg fill=\"none\"><path fill=\"white\" stroke=\"black\"/><path fill=\"#ccc\"/><path fill=\"#6EA0FF\"/></svg>", "#E6E8EC");
    Require(r3.Contains("fill=\"#000000\"") && r3.Contains("stroke=\"#E6E8EC\"") && r3.Contains("fill=\"#333333\"") && r3.Contains("#6EA0FF"), r3);
    string r4 = IconProvider.Recolor("<svg fill=\"none\"><path fill=\"white\" stroke=\"black\"/><path fill=\"#ccc\"/></svg>", "#1C1E22");
    Require(r4.Contains("fill=\"white\"") && r4.Contains("stroke=\"#1C1E22\"") && r4.Contains("fill=\"#ccc\""), r4);
    // без файла иконки — подпись
    Require(IconProvider.Get("no_such_icon_but.svg") == null, "missing icon");
    w.Close();
});


Check("UI-ICONS-USER-SET", () =>
{
    // набор иконок пользователя (icons1, 331 файл): каждый вшит, читается, пропорции рисунка = пропорциям файла (viewBox =
    // width × height), при вписывании в квадрат кнопки — по центру без растяжения; иконка языка — по текущему языку
    var w = Open(DocumentSession.NewPart());
    var files = Avalonia.Platform.AssetLoader.GetAssets(new Uri("avares://Parametric3D/App/Assets/Icons/"), null).Select(u => u.AbsolutePath.Split('/').Last()).Where(n => n.EndsWith(".svg")).ToList();
    Require(files.Count >= 331, $"icons embedded: {files.Count}");
    var bad = new List<string>();
    foreach (var f in files)
    {
        var img = IconProvider.Get(f);
        if (img == null) { bad.Add(f + ": not loaded"); continue; }
        using var st = Avalonia.Platform.AssetLoader.Open(IconProvider.UriOf(f)); var text = new StreamReader(st).ReadToEnd();
        var m = System.Text.RegularExpressions.Regex.Match(text, "viewBox=\"[-\\d.]+ [-\\d.]+ ([\\d.]+) ([\\d.]+)\"");
        if (!m.Success || !text.Contains("preserveAspectRatio=\"xMidYMid meet\"")) { bad.Add(f + ": no viewBox/preserveAspectRatio"); continue; }
        double vw = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), vh = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        // k98: иконка с полями вписана по рисунку — пропорции рамки рисунка (с полем 4 %)
        if (IconProvider.ContentBox(f) is { } cb) { vw = cb.W; vh = cb.H; }
        if (Math.Abs(img.Size.Width / img.Size.Height - vw / vh) > 1e-6) bad.Add($"{f}: drawing {img.Size} vs file {vw}x{vh}");
        var r = IconProvider.Fit(img.Size, new Rect(0, 0, 24, 24));
        if (Math.Abs(r.Width / r.Height - vw / vh) > 1e-6 || r.Width > 24.0001 || r.Height > 24.0001 || Math.Abs(r.Center.X - 12) > 1e-9 || Math.Abs(r.Center.Y - 12) > 1e-9) bad.Add($"{f}: fit {r}");
    }
    Require(bad.Count == 0, string.Join("; ", bad.Take(8)));
    // лист иконок (по запросу: UI_ICONSHEET=путь.png): каждая — в квадрате 32×32 с рамкой, по 24 в ряду
    if (Environment.GetEnvironmentVariable("UI_ICONSHEET") is { Length: > 0 } sheet)
    {
        int cols = 24, cell = 40, rows = (files.Count + cols - 1) / cols;
        var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(cols * cell, rows * cell));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E2128")), new Rect(0, 0, cols * cell, rows * cell));
            var pen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3F4A")), 1);
            for (int i = 0; i < files.Count; i++)
            {
                var box = new Rect(i % cols * cell + 4, i / cols * cell + 4, 32, 32);
                dc.DrawRectangle(null, pen, box);
                var img = IconProvider.Get(files[i])!;
                img.Draw(dc, new Rect(img.Size), IconProvider.Fit(img.Size, box.Deflate(2)));
            }
        }
        rtb.Save(sheet);
        // крупно — выбранные (UI_ICONBIG=имя,имя): по 160 px
        if (Environment.GetEnvironmentVariable("UI_ICONBIG") is { Length: > 0 } big)
        {
            var names = big.Split(','); var rb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(names.Length * 180, 180));
            using (var dc2 = rb.CreateDrawingContext())
            {
                dc2.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E2128")), new Rect(0, 0, names.Length * 180, 180));
                for (int i = 0; i < names.Length; i++)
                {
                    var box = new Rect(i * 180 + 10, 10, 160, 160); var im = IconProvider.Get(names[i])!;
                    dc2.DrawRectangle(null, new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3F4A")), 1), box);
                    im.Draw(dc2, new Rect(im.Size), IconProvider.Fit(im.Size, box));
                }
            }
            rb.Save(sheet + ".big.png");
        }
        File.WriteAllLines(sheet + ".txt", files.Select((f, i) => $"{i / 24},{i % 24} {f}"));
    }
    // команды: иконка есть у каждой команды, для которой файл передан
    var lang = CommandRegistry.Instance.All.First(c => c.Id == "app.language");
    Require(lang.Icon == (Loc.En ? "language_en_but.svg" : "language_ru_but.svg"), "language icon " + lang.Icon);
    w.Close();
});

Check("UI-TREE-VISIBILITY", () =>
{
    // п. 4: глаз детали скрывает всё внутри; п. 5: «свернуть дерево» сворачивает и деталь; п. 13: перестройка дерева
    // (глаз эскиза) не сворачивает развёрнутое
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    var tree = w.FindControl<TreeView>("Tree")!;
    var root = ((IEnumerable<TreeNode>)tree.ItemsSource!).First();
    Require(root.Item == part, "tree root is not the part");
    root.IsVisible = false;
    Require(part.Descendants().All(d => !d.Visible), "hiding the part did not hide its children");
    Require(root.SelfAndDescendants().Where(n => n.HasEye).All(n => !n.IsVisible), "child eyes are still open");
    root.IsVisible = true;
    Require(part.Descendants().All(d => d.Visible), "showing the part did not show its children");
    var feat = w.AllNodes.First(n => n.Item is Feature && n.Children.Count > 0);
    feat.IsExpanded = true;
    var sk = feat.Children.First(n => n.Item is Sketch);
    sk.IsVisible = !sk.IsVisible;
    w.RebuildTree();
    Require(w.AllNodes.First(n => n.Item == feat.Item).IsExpanded, "rebuild collapsed the feature branch");
    w.SetTreeExpanded(false);
    Require(!w.AllNodes.First().IsExpanded, "collapse does not collapse the part");
    w.SetTreeExpanded(true);
    Require(w.AllNodes.First().IsExpanded, "expand does not expand the part");
    w.Close();
});

Check("UI-OPERATION-ROUND", () =>
{
    // операция в панели: выбор до команды → поле ссылок, предпросмотр в модели, «ОК» — один шаг отмены, «Отмена» — как было
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    int f0 = part.Features.Count, u0 = s.UndoNames.Count;
    var edge = part.Bodies[0].Edges.First();
    w.ViewPort.Selection.Select(new PickRef(edge.Id, PickKind.Edge, part, null), additive: false);
    Require(w.SuggestedOps().Contains("op.round"), "edge does not suggest a round");
    w.StartOperation(new RoundOp());
    Require(w.CurrentOperation != null && part.Features.Count == f0 + 1, "round was not started with a preview");
    var round = (RoundFeature)part.Features.Last();
    Require(round.Edges.Count == 1, $"edges from the selection: {round.Edges.Count}");
    s.Document.RebuildDirty();
    Require(round.Error == null, "round error: " + round.Error);
    w.CommitOperation();
    Require(w.CurrentOperation == null && s.UndoNames.Count == u0 + 1, "commit is not one undo step");
    // отмена новой операции
    w.ViewPort.Selection.Select(new PickRef(part.Bodies[0].Edges.Last().Id, PickKind.Edge, part, null), additive: false);
    w.StartOperation(new ChamferOp());
    w.CancelOperation();
    Require(s.Part!.Features.Count == f0 + 1 && s.UndoNames.Count == u0 + 1, "cancel did not restore the model");
    Require(!s.CanRedo, "cancel left a redo step");
    w.Close();
});

Check("UI-SKETCH-EXTRUDE", () =>
{
    // эскиз на плоскости → прямоугольник → закончить → выдавливание по нему (эскиз сначала, п. 3 и 9)
    var s = DocumentSession.NewPart();
    var w = Open(s);
    var part = s.Part!;
    w.SelectItem(part.PlaneXY);
    w.StartOperation(new SketchOp { Then = sk => new ExtrudeOp { SeedSketch = sk } });
    Require(w.InSketch, "sketch mode did not start on the selected plane");
    var sketch = w.ViewPort.ActiveSketch!;
    var rect = new RectangleTool();
    rect.Click(new SketchInput(sketch, new Vector2d(-20, -10), null));
    Require(rect.Click(new SketchInput(sketch, new Vector2d(20, 10), null)), "rectangle not created");
    w.FinishSketch();
    Require(!w.InSketch && w.CurrentOperation is ExtrudeOp, "extrude did not follow the sketch");
    w.CommitOperation();
    s.Document.RebuildDirty();
    Require(part.Bodies.Count == 1 && Math.Abs(part.Volume - 40 * 20 * 20) < 1e-6, $"volume {part.Volume}");
    w.Close();
});

Check("UI-CONSTRAINT-GLYPHS", () =>
{
    // значки ограничений на эскизе (правила прежнего приложения): у линии — строкой вдоль неё СНАРУЖИ замкнутого
    // контура, у узла/центра — под ним; иконки con_* поверх вида, ловятся мышью; конфликт — красная рамка
    var s = DocumentSession.NewPart();
    var w = Open(s);
    var part = s.Part!;
    w.SelectItem(part.PlaneXY);
    w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch mode did not start");
    var sk = w.ViewPort.ActiveSketch!;
    var rect = new RectangleTool();
    rect.Click(new SketchInput(sk, new Vector2d(-20, -10), null));
    Require(rect.Click(new SketchInput(sk, new Vector2d(20, 10), null)), "rectangle not created");
    var circ = sk.AddEntity(new SkCircle(new SkPoint(50, 0), 6));
    sk.AddConstraint(ConstraintType.Fix, circ.Center);
    Dispatcher.UIThread.RunJobs();
    double px = w.ViewPort.PixelSize;
    var list = ConstraintLayout.Build(sk, px);
    int expected = sk.Constraints.Where(c => c.Enabled).Sum(c => c.Items.Count(i => ConstraintLayout.TryAnchor(i, out _, out _, out _)));
    Require(list.Count > 0 && list.Count <= expected, $"placements {list.Count} of {expected}");
    foreach (var t in Enum.GetValues<ConstraintType>())
        Require(IconProvider.Exists(ConstraintLayout.IconOf(t)), "no icon for " + t);
    // горизонтальность верхней и нижней сторон — снаружи прямоугольника, вертикальность — слева/справа снаружи
    foreach (var c in sk.Constraints.Where(c => c.Type is ConstraintType.Horizontal or ConstraintType.Vertical))
        foreach (var g in c.GlyphPositions)
            Require(Math.Abs(g.X) > 20 || Math.Abs(g.Y) > 10, $"{c.Type} glyph inside the closed contour at {g}");
    // совпадения углов: одна строка на угол (не два значка друг на друге), под узлом
    var coinc = sk.Constraints.Where(c => c.Type == ConstraintType.Coincident).SelectMany(c => c.GlyphPositions).ToList();
    Require(coinc.Count == 4, $"coincident glyphs {coinc.Count}");
    Require(coinc.All(g => sk.AllPoints().Any(p => Math.Abs(p.P.X - g.X) < 1e-6 && p.P.Y > g.Y)), "coincident glyph is not below its node");
    var fix = sk.Constraints.First(c => c.Type == ConstraintType.Fix);
    Require(fix.GlyphPositions.Count == 1 && fix.GlyphPositions[0].Y < 0 && Math.Abs(fix.GlyphPositions[0].X - 50) < 1e-6, "fix glyph is not below the circle centre");
    // шаг строки — не меньше размера значка (не наезжают)
    var row = list.GroupBy(p => Math.Round(p.Center.Y / px)).Where(g => g.Count() > 1).SelectMany(g => g.OrderBy(p => p.Center.X).Zip(g.OrderBy(p => p.Center.X).Skip(1)));
    foreach (var (a, b) in row) Require((b.Center - a.Center).Length >= Settings.Current.ConstraintIconSizePx * px * 0.99 || Math.Abs(b.Center.X - a.Center.X) < 1e-9, "glyphs overlap in a row");
    // значки не наезжают друг на друга (строка, задевшая соседей, отодвигается)
    for (int i = 0; i < list.Count; i++)
        for (int j = i + 1; j < list.Count; j++)
            Require(Math.Abs(list[i].Center.X - list[j].Center.X) >= Settings.Current.ConstraintIconSizePx * px * 0.99
                 || Math.Abs(list[i].Center.Y - list[j].Center.Y) >= Settings.Current.ConstraintIconSizePx * px * 0.99, $"glyphs {list[i].Constraint.Type} and {list[j].Constraint.Type} overlap");
    // попадание мышью: значок над точкой экрана, где он нарисован
    if (w.ViewPort.Project(sk.Plane.ToWorld(fix.GlyphPositions[0])) is { } q)
    {
        var hit = w.ViewPort.ConstraintGlyphAt(q);
        Require(hit.Kind == PickKind.SketchConstraint && hit.Id == fix.Id, "glyph is not picked at its screen position");
        Require(w.ViewPort.ConstraintGlyphAt(new Avalonia.Point(q.X + 60, q.Y + 60)).Id != fix.Id, "glyph picked far away");
    }
    // отрисовка слоя: все значки; конфликт — другим цветом рамки
    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(Math.Max(64, (int)w.GlyphLayer.Bounds.Width), Math.Max(64, (int)w.GlyphLayer.Bounds.Height)));
    using (var ctx = rtb.CreateDrawingContext())
    {
        ctx.FillRectangle(w.ViewPort.Theme.Dark ? Avalonia.Media.Brushes.Black : Avalonia.Media.Brushes.White, new Avalonia.Rect(rtb.Size));
        w.GlyphLayer.Render(ctx);
    }
    Require(w.GlyphLayer.LastDrawn > 0, "glyph layer drew nothing");
    if (Environment.GetEnvironmentVariable("UI_GLYPHSHOT") is { Length: > 0 } shot) rtb.Save(shot);
    Settings.Current.ShowConstraintGlyphs = false;
    Require(ConstraintLayout.Build(sk, px).Count == 0 && fix.GlyphPositions.Count == 0, "glyphs shown while switched off");
    Settings.Current.ShowConstraintGlyphs = true;
    Console.WriteLine(JsonSerializer.Serialize(new { info = "UI-CONSTRAINT-GLYPHS", placements = list.Count, drawn = w.GlyphLayer.LastDrawn, loopBuilds = ConstraintLayout.LoopBuilds }));
    w.CancelSketch();
    w.Close();
});

Check("UI-SKETCH-UX", () =>
{
    // пп. 6–12: перетаскивание вершин и фигур, панель фигур, полоса предложений по выбору, «Готово/Отмена» у куба,
    // инструмент снимается после построения, у эллипса есть иконка
    var s = DocumentSession.NewPart();
    var w = Open(s);
    var part = s.Part!;
    w.SelectItem(part.PlaneXY);
    w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch mode did not start");
    var vp = w.ViewPort;
    var sk = vp.ActiveSketch!;
    Dispatcher.UIThread.RunJobs();
    var reg = CommandRegistry.Instance;
    // п. 12: прямоугольник построен — инструмента нет (режим по умолчанию)
    w.SetTool(new RectangleTool());
    // вид ещё не повернулся к плоскости (без кадров GL) — точки щелчка с запасом по обеим осям
    vp.SimulateToolClick(0.3, 0.1); vp.SimulateToolClick(0.7, 0.9);
    Require(sk.Entities.OfType<SkLine>().Count() == 4, $"rectangle not drawn: {sk.Entities.Count}");
    Require(vp.Tool == null, "tool still active after the shape was built");
    // п. 8 и 7: иконка эллипса; панель фигур со всеми инструментами построения; основная панель без правки и размеров
    Require(IconProvider.Exists(reg["sk.ellipse"].Icon) && IconProvider.Exists(reg["sk.ellipse_3p"].Icon), "ellipse has no icon");
    foreach (var id in MainWindow.ShapeTools) Require(reg.Find(id) != null && IconProvider.Exists(reg[id].Icon), "shape tool without icon: " + id);
    // п. 6: вершина тянется — прямоугольник остаётся прямоугольником (горизонтальность и вертикальность держатся)
    var lines = sk.Entities.OfType<SkLine>().ToList();
    var corner = lines[0].A;
    var c0 = corner.P;
    Require(vp.BeginSketchDragAt(c0), "free corner cannot be dragged");
    Require(vp.SketchDragKind == "point", "drag kind " + vp.SketchDragKind);
    vp.SketchDragTo(c0 + new Vector2d(-5, -3));
    Require(vp.EndSketchDrag(), "drag not finished");
    Require((corner.P - (c0 + new Vector2d(-5, -3))).Length < 0.02, $"corner did not follow: {corner.P} vs {c0}");
    foreach (var l in lines) Require(Math.Abs(l.A.P.X - l.B.P.X) < 1e-6 || Math.Abs(l.A.P.Y - l.B.P.Y) < 1e-6, "rectangle lost its H/V constraints");
    // фигура целиком: тянем за середину стороны — все вершины сдвигаются одинаково
    var before = sk.AllPoints().Distinct().ToDictionary(x => x, x => x.P);
    var mid = (lines[1].A.P + lines[1].B.P) / 2;
    Require(vp.BeginSketchDragAt(mid), "shape cannot be dragged by its side");
    Require(vp.SketchDragKind == "shape", "drag kind " + vp.SketchDragKind);
    vp.SketchDragTo(mid + new Vector2d(12, 7));
    vp.EndSketchDrag();
    foreach (var (pt, p0) in before) Require(((pt.P - p0) - new Vector2d(12, 7)).Length < 0.02, $"shape point moved by {pt.P - p0}");
    // зафиксированный центр окружности не тянется
    var circ = sk.AddEntity(new SkCircle(new SkPoint(80, 0), 6));
    sk.AddConstraint(ConstraintType.Fix, circ.Center);
    Require(!vp.BeginSketchDragAt(circ.Center.P), "fixed point was dragged");
    vp.EndSketchDrag();
    // пп. 9–10: предложения по выбору
    List<string> Sugg() => Parametric3D.App.Commands.SketchSuggest.For(w);
    vp.Selection.Clear();
    Require(Sugg().Contains("sk.trim") && !Sugg().Contains("sk.merge_points"), "empty selection: " + string.Join(",", Sugg()));
    SkPoint pa = lines[0].A, pb = lines[2].B;
    vp.Selection.Select(new PickRef(pa.Id, PickKind.SketchPoint, sk, pa), additive: true);
    var one = Sugg();
    Require(one.Contains("sk.split_point") && !one.Contains("sk.merge_points") && one.Contains("con.fix"), "one point: " + string.Join(",", one));
    vp.Selection.Select(new PickRef(pb.Id, PickKind.SketchPoint, sk, pb), additive: true);
    var two = Sugg();
    Require(two.Contains("sk.merge_points") && two.Contains("dim.linear") && !two.Contains("sk.split_point") && !two.Contains("dim.radius"), "two points: " + string.Join(",", two));
    vp.Selection.Clear();
    var la = lines[0]; var lb = lines.First(l => l != la && Parametric3D.App.Commands.SketchSuggest.CommonCorner(la, l) != null);
    vp.Selection.Select(new PickRef(la.Id, PickKind.SketchCurve, sk, la), additive: true);
    vp.Selection.Select(new PickRef(lb.Id, PickKind.SketchCurve, sk, lb), additive: true);
    var twoLines = Sugg();
    // соседние стороны прямоугольника: уже горизонтальна/вертикальна и перпендикулярны — эти связи не предлагаются, параллельность тоже
    Require(!twoLines.Contains("con.horizontal") && !twoLines.Contains("con.parallel") && !twoLines.Contains("con.collinear") && twoLines.Contains("con.equal"), "two lines: implausible constraints offered: " + string.Join(",", twoLines));
    Require(twoLines.Take(2).SequenceEqual(new[] { "sk.fillet", "sk.chamfer" }) && twoLines.Contains("dim.angle") && twoLines.Contains("sk.offset") && !twoLines.Contains("dim.radius"), "two lines: " + string.Join(",", twoLines));
    vp.Selection.Clear();
    var rd = sk.AddDimension(DimensionType.Radius, 6, circ);
    vp.Selection.Select(new PickRef(rd.Id, PickKind.SketchDimension, sk, rd), additive: true);
    var dim = Sugg();
    foreach (var id in new[] { "dim.to_diameter", "dim.single_arrow", "dim.outside", "dim.lock_toggle", "dim.ref_toggle" })
        Require(dim.Contains(id), "radius dimension misses " + id + ": " + string.Join(",", dim));
    Require(!dim.Contains("dim.to_radius") && !dim.Contains("dim.as_linear") && !dim.Contains("sk.trim"), "radius dimension: unsuitable " + string.Join(",", dim));
    reg["dim.outside"].Execute();
    Require(rd.Arrows == ArrowPlacement.Outside && reg["dim.outside"].IsChecked!() && reg["dim.outside"].Icon == "dim_radius_outside_ico.svg", "outside arrows");
    reg["dim.to_diameter"].Execute();
    Require(rd.Type == DimensionType.Diameter && Math.Abs(rd.Value - 12) < 1e-9, "radius -> diameter");
    Require(Sugg().Contains("dim.to_radius") && Sugg().Contains("dim.as_linear"), "diameter suggestions");
    // объединить и разделить точку; удалить
    vp.Selection.Clear();
    var lone1 = sk.AddEntity(new SkPoint(-40, 30)); var lone2 = sk.AddEntity(new SkPoint(-35, 30));
    vp.Selection.Select(new PickRef(lone1.Id, PickKind.SketchPoint, sk, lone1), additive: true);
    vp.Selection.Select(new PickRef(lone2.Id, PickKind.SketchPoint, sk, lone2), additive: true);
    reg["sk.merge_points"].Execute();
    Require((lone1.P - lone2.P).Length < 1e-6, "points not merged");
    vp.Selection.Clear(); vp.Selection.Select(new PickRef(lone1.Id, PickKind.SketchPoint, sk, lone1), additive: true);
    Require(Sugg().Contains("sk.split_point"), "merged point: no split");
    reg["sk.split_point"].Execute();
    Require(!sk.Constraints.Any(c => c.Type == ConstraintType.Coincident && c.Items.Contains(lone1)), "split did not remove coincidence");
    vp.Selection.Clear(); vp.Selection.Select(new PickRef(circ.Id, PickKind.SketchCurve, sk, circ), additive: true);
    reg["sk.delete"].Execute();
    Require(!sk.Entities.Contains(circ) && !sk.Dimensions.Contains(rd), "circle not deleted with its dimension");
    // п. 11 (с 12:28 — в полосе панелей над видом): «Готово / Отмена» видны; полоса снизу — предложения
    var bar = w.FindControl<Border>("SketchBar")!;
    Require(bar.IsVisible && bar.Parent == w.FindControl<WrapPanel>("ToolTray"), "sketch done/cancel bar is not in the toolbar tray");
    Require(w.FindControl<Border>("ShapeBar")!.IsVisible, "shape panel hidden");
    if (Environment.GetEnvironmentVariable("UI_SKSHOT") is { Length: > 0 } skshot)
    {
        vp.Selection.Clear(); vp.Selection.Select(new PickRef(la.Id, PickKind.SketchCurve, sk, la), additive: true); vp.Selection.Select(new PickRef(lb.Id, PickKind.SketchCurve, sk, lb), additive: true);
        w.RefreshContext(); Dispatcher.UIThread.RunJobs();
        var shot = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height));
        shot.Render(w); shot.Save(skshot);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { info = "UI-SKETCH-UX", empty = "trim…", twoPoints = two, twoLines, radius = dim }));
    w.CancelSketch();
    Require(!w.FindControl<Border>("SketchBar")!.IsVisible && !w.FindControl<Border>("ShapeBar")!.IsVisible, "sketch bars visible after the sketch");
    w.Close();
});

Check("UI-TOOLBAR-TRAY", () =>
{
    // (12:28): все панели инструментов — в полосе над видом, не поверх вида: операции, вид, выбор; в эскизе — ещё фигуры и
    // «Готово / Отмена». Кнопки — иконки одного размера; у групп «Ещё…» — выпадающий список. Вид — целиком под полосой.
    // Шрифт ГОСТ — только у объектов вида; эскиз в правке, скрытый в дереве, — всё равно в сцене
    var s = DocumentSession.NewPart();
    var w = Open(s);
    var tray = w.FindControl<WrapPanel>("ToolTray")!;
    var trayBorder = w.FindControl<Border>("ToolTrayBorder")!;
    var area = w.FindControl<Grid>("ViewArea")!;
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    Run();
    string[] names = { "CommandBar", "ShapeBar", "SketchBar", "DimBar", "SketchSetBar" };
    foreach (var n in names) Require(w.FindControl<Border>(n)!.Parent == tray, n + " is not in the toolbar tray");
    // k98 (замечание 1): вид и стиль — в окне вида сверху посередине
    var vbar = w.FindControl<Border>("ViewBar")!;
    Require(vbar.Parent == area && vbar.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Center && vbar.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Top, "view bar is not at the top centre of the view");
    // (13:09): панель предложенных операций — внизу вида, поверх него (не в полосе)
    var ctxBar = w.FindControl<Border>("ContextBar")!;
    Require(ctxBar.Parent == area && ctxBar.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Bottom && !ctxBar.GetVisualAncestors().Contains(trayBorder), "context bar is not at the bottom of the view");
    Require(!area.Children.OfType<Border>().Any(b => names.Contains(b.Name)), "a tool panel is still over the view");
    var trayBox = trayBorder.Bounds; var areaTop = area.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.Y;
    Require(areaTop >= trayBorder.TranslatePoint(new Avalonia.Point(0, trayBox.Height), w)!.Value.Y - 0.5, "the view starts above the end of the tray");
    var stack = w.FindControl<WrapPanel>("CommandStack")!;
    var cmdBtns = stack.Children.OfType<Avalonia.Controls.Primitives.TemplatedControl>().Where(b => b.Tag is AppCommand).ToList();
    Require(cmdBtns.Count >= 10 && cmdBtns.All(b => Math.Abs(b.Bounds.Height - UiLook.StripButtonPx) < 0.5), $"command icons: {cmdBtns.Count}, heights {string.Join(",", cmdBtns.Select(b => b.Bounds.Height).Distinct())}");
    // k96 (п. 9): панель операций детали — все кнопки, без выпадающих «Ещё…»
    Require(!stack.Children.OfType<Button>().Any(b => b.Tag is not AppCommand), "a drop-down is left on the part operations bar (all buttons must be shown)");
    Require(cmdBtns.Count >= 60, $"part operations bar shows {cmdBtns.Count} buttons (expected all)");
    Require(!stack.Children.OfType<TextBlock>().Any(), "the tray has a text header");
    // окно уже: полоса переносится, панели не перекрываются
    w.Width = 1000; Run();
    var vis = tray.Children.OfType<Control>().Where(c => c.IsVisible).Select(c => c.Bounds).ToList();
    for (int a = 0; a < vis.Count; a++) for (int b = a + 1; b < vis.Count; b++)
            Require(!vis[a].Intersects(vis[b].Deflate(0.5)), $"toolbars overlap: {vis[a]} / {vis[b]}");
    w.Width = 1400; Run();
    // шрифт ГОСТ только у вида: панели — шрифтом интерфейса
    var gost = Parametric3D.Render.GostFont.Family;
    var panelText = tray.GetVisualDescendants().OfType<TextBlock>().ToList();
    Require(panelText.All(t => !t.FontFamily.Name.Contains(gost)), "toolbar text uses the GOST font");
    // эскиз: фигуры и «Готово / Отмена» — в полосе
    var part = s.Part!;
    w.SelectItem(part.PlaneXY);
    w.StartOperation(new SketchOp());
    Run();
    Require(w.FindControl<Border>("ShapeBar")!.IsVisible && w.FindControl<Border>("SketchBar")!.IsVisible, "sketch toolbars are not shown in the tray");
    var sk = w.ViewPort.ActiveSketch!;
    Rect(sk, 0, 0, 30, 20);
    sk.Visible = false;
    var scene = Parametric3D.Render.Engine.SceneBuilder.Build(s.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions(), part, sk);
    Require(scene.Lines.Count(l => scene.PickTable[(int)l.Pick].Ref.Kind == PickKind.SketchCurve) >= 4, "hidden active sketch is not drawn");
    w.CancelSketch();
    w.Close();
});

Check("UI-HIDE-TOGGLE", () =>
{
    // (13:09) п. 4: «Скрыть / показать выбранное» — переключатель: второй щелчок возвращает видимость
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    s.Document.RebuildDirty();
    var items = new ModelItem[] { part.PlaneXY, part.PlaneYZ };
    w.SelectItem(items[0]);
    w.ViewPort.Selection.Select(new PickRef(items[1].Id, PickKind.DatumPlane, part, items[1]), additive: true);
    var cmd = CommandRegistry.Instance["view.hide"];
    var sel = w.SelectedItems();
    Console.WriteLine($"  selected {sel.Count}: {string.Join(",", sel.Select(i => i.Name))}");
    Require(sel.Count >= 1 && cmd.Enabled && cmd.IsChecked?.Invoke() != true, "hide command state");
    cmd.Execute();
    Require(sel.All(i => !i.Visible) && cmd.IsChecked?.Invoke() == true && cmd.Icon == "eye_hidden_ico.svg", "first click hides");
    cmd.Execute();
    Require(sel.All(i => i.Visible) && cmd.IsChecked?.Invoke() != true, "second click shows again");
    w.Close();
});

Check("UI-FF-GIZMO", () =>
{
    // (13:09) пп. 5–6: гизмо переноса, поворота и масштаба тел FreeForm; переключатель Local / Global
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    var vp = w.ViewPort;
    var box = new FFPrimitiveFeature { Shape = FFPrimitive.Box, A = new ValueRef(40), B = new ValueRef(30), C = new ValueRef(20), FreeForm = node.Key, Frozen = true };
    part.AddFeature(box); s.Document.RebuildDirty();
    Require(box.Error == null && Math.Abs(part.Volume - 24000) < 1e-6, "box");
    void Run() { Dispatcher.UIThread.RunJobs(); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs(); }
    (Vector3d Min, Vector3d Max) BB(Vector3d? x = null, Vector3d? y = null)
    {
        var X = x ?? Vector3d.UnitX; var Y = y ?? Vector3d.UnitY;
        return FreeFormExec.BoundsIn(part.Bodies, X, Y, Vector3d.Cross(X, Y))!.Value;
    }
    bool Near(Vector3d a, Vector3d b, double tol = 1e-6) => (a - b).Length < tol;
    string V(Vector3d v) => $"({v.X:F4}, {v.Y:F4}, {v.Z:F4})";
    FFTransformOp Start(FFXform mode)
    {
        vp.Selection.Clear(); Select(w, new PickRef(part.Bodies[0].Faces.First().Id, PickKind.Face, part, null));
        var op = new FFTransformOp { Mode = mode };
        w.StartOperation(op); Run(); op.RefreshGizmo();
        return op;
    }
    void Drag(GizmoPart p, Vector3d d, double ang = 0, KeyModifiers m = KeyModifiers.None)
    {
        vp.SimulateGizmoDrag(new GizmoDrag(p, d, ang, m)); Run(); w.CurrentOperation!.RefreshGizmo();
    }
    FFTransformOp.Local = false;

    // перенос: открыт тождественным (тело на месте, без ошибки), гизмо — в середине тела, оси детали
    var mo = Start(FFXform.Move);
    var mf = (FFTransformFeature)mo.EditedFeature!;
    Console.WriteLine($"  move: gizmo {vp.Gizmo}, error {mf.Error}, volume {part.Volume:F4}");
    Require(vp.Gizmo is { Mode: GizmoMode.Move } g0 && Near(g0.Origin, new(0, 0, 10)) && Near(g0.X, Vector3d.UnitX) && mf.Error == null && Math.Abs(part.Volume - 24000) < 1e-6, "move gizmo at the body centre, identity step");
    Require(vp.GizmoHit(new Avalonia.Point(-100, -100)) == GizmoPart.None, "gizmo hit off the view");
    Drag(GizmoPart.AxisX, new(15, 0, 0));
    Drag(GizmoPart.PlaneXY, new(5, 7, 0));
    var bm = BB();
    Console.WriteLine($"  move: shift ({mf.Dx}, {mf.Dy}, {mf.Dz}), box {V(bm.Min)}–{V(bm.Max)}, gizmo {V(vp.Gizmo!.Origin)}");
    Require(Near(bm.Min, new(0, -8, 0)) && Near(bm.Max, new(40, 22, 20)) && Near(vp.Gizmo!.Origin, new(20, 7, 10)), "move by axis, then by plane");
    w.CommitOperation(); Run();

    // масштаб: без SHIFT — тянется сторона (противоположная стоит), SHIFT — в обе стороны, CTRL — пропорции
    Start(FFXform.Scale);
    Drag(GizmoPart.AxisX, new(20, 0, 0));
    var b1 = BB();
    Console.WriteLine($"  scale X one side: {V(b1.Min)}–{V(b1.Max)}");
    Require(Near(b1.Min, new(0, -8, 0)) && Near(b1.Max, new(60, 22, 20)), "scale one side");
    Drag(GizmoPart.AxisY, new(0, 6, 0), 0, KeyModifiers.Shift);
    var b2 = BB();
    Console.WriteLine($"  scale Y both sides: {V(b2.Min)}–{V(b2.Max)}");
    Require(Near(b2.Min, new(0, -14, 0)) && Near(b2.Max, new(60, 28, 20)), "scale both sides (SHIFT)");
    Drag(GizmoPart.AxisZ, new(0, 0, 10), 0, KeyModifiers.Control);
    var b3 = BB();
    var sf = (FFTransformFeature)w.CurrentOperation!.EditedFeature!;
    Console.WriteLine($"  scale Z keep ratio: {V(b3.Min)}–{V(b3.Max)}, S ({sf.Sx}, {sf.Sy}, {sf.Sz}), volume {part.Volume:F4}");
    Require(Near(b3.Max - b3.Min, new(90, 63, 30)) && Math.Abs(b3.Min.Z) < 1e-6 && ((b3.Min + b3.Max).Xy * 0.5 - new Vector2d(30, 7)).Length < 1e-6, "scale keeping the aspect ratio (CTRL)");
    Require(Math.Abs(part.Volume - 90 * 63 * 30) < 1e-6 * 90 * 63 * 30, "scaled volume");
    // угол — пропорционально по диагонали, SHIFT — от середины
    double diag = (b3.Max - b3.Min).Length, along = diag / 2;
    Drag(GizmoPart.Uniform, new Vector3d(along, along, along) / Math.Sqrt(3), 0, KeyModifiers.Shift);
    var b4 = BB();
    Console.WriteLine($"  scale corner: {V(b4.Min)}–{V(b4.Max)}");
    Require(Near(b4.Max - b4.Min, new(180, 126, 60), 1e-4) && Near((b4.Min + b4.Max) * 0.5, (b3.Min + b3.Max) * 0.5, 1e-4), "uniform scale by the corner");
    w.CancelOperation(); Run();
    Require(Near(BB().Max - BB().Min, new(40, 30, 20)), "cancel restores the body");

    // поворот: кольца; два поворота подряд складываются в один (ось через середину тела)
    var ro = Start(FFXform.Rotate);
    Require(vp.Gizmo is { Mode: GizmoMode.Rotate } gr && Near(gr.Origin, new(20, 7, 10)), "rotate gizmo at the body centre");
    Drag(GizmoPart.AxisZ, Vector3d.Zero, 90);
    var r1 = BB();
    Console.WriteLine($"  rotate Z 90: {V(r1.Min)}–{V(r1.Max)}");
    Require(Near(r1.Max - r1.Min, new(30, 40, 20), 1e-5) && Near((r1.Min + r1.Max) * 0.5, new(20, 7, 10), 1e-5), "rotate about Z");
    Drag(GizmoPart.AxisX, Vector3d.Zero, 90);
    var r2 = BB();
    var rf = (FFTransformFeature)ro.EditedFeature!;
    Console.WriteLine($"  then X 90: {V(r2.Min)}–{V(r2.Max)}, axis {V(rf.AxisDir)} angle {rf.Angle}");
    Require(Near(r2.Max - r2.Min, new(30, 20, 40), 1e-5) && Near((r2.Min + r2.Max) * 0.5, new(20, 7, 10), 1e-5) && Math.Abs(rf.Angle.Value - 120) < 1e-3, "two rotations compose into one");
    w.CancelOperation(); Run();

    // Local: тело повёрнуто на 30° вокруг Z — оси гизмо по телу; перенос по локальной X
    var rot = new FFTransformFeature { Mode = FFXform.Rotate, BodyNames = { "Тело 1" }, AxisAt = new(20, 7, 10), AxisDir = Vector3d.UnitZ, Angle = new ValueRef(30), FreeForm = node.Key, Frozen = true };
    part.AddFeature(rot); Run();
    Require(rot.Error == null, "rotate step: " + rot.Error);
    CommandRegistry.Instance["ff.xform_local"].Execute();
    Require(FFTransformOp.Local && CommandRegistry.Instance["ff.xform_local"].Icon == "ff_xform_local_but.svg", "local toggle");
    var lo = Start(FFXform.Move);
    var c30 = Math.Cos(Math.PI / 6); var s30 = Math.Sin(Math.PI / 6);
    Console.WriteLine($"  local gizmo: X {V(vp.Gizmo!.X)} Y {V(vp.Gizmo!.Y)} origin {V(vp.Gizmo!.Origin)}");
    Require(Near(vp.Gizmo!.X, new(c30, s30, 0), 1e-9) && Near(vp.Gizmo!.Y, new(-s30, c30, 0), 1e-9) && Near(vp.Gizmo!.Origin, new(20, 7, 10), 1e-6), "local frame of the rotated body");
    var before = BB(vp.Gizmo!.X, vp.Gizmo!.Y);
    Drag(GizmoPart.AxisX, new(10, 0, 0));
    var after = BB(new(c30, s30, 0), new(-s30, c30, 0));
    Require(Near(after.Min - before.Min, new(10, 0, 0), 1e-5) && Near(after.Max - after.Min, new(40, 30, 20), 1e-5), "move along the local X");
    // Global: сдвиг тот же в мире, числа — по осям детали
    CommandRegistry.Instance["ff.xform_local"].Execute(); Run();
    var lf = (FFTransformFeature)lo.EditedFeature!;
    Console.WriteLine($"  global: shift ({lf.Dx}, {lf.Dy}, {lf.Dz}), gizmo X {V(vp.Gizmo!.X)}");
    Require(!FFTransformOp.Local && Near(vp.Gizmo!.X, Vector3d.UnitX) && Math.Abs(lf.Dx.Value - 10 * c30) < 1e-3 && Math.Abs(lf.Dy.Value - 10 * s30) < 1e-3, "switch to global keeps the shift");
    w.CommitOperation(); Run();
    Require(vp.Gizmo == null, "gizmo removed after the operation");
    w.Close();
});

Check("UI-FRAME-GUARD", () =>
{
    // (14:51, журнал пользователя): отказ кадра Avalonia («Unable to configure OpenGL FBO») не снимает приложение — кадр вида
    // идёт через обёртку: исключение — в журнал, кадр повторяется
    var w = Open(DocumentSession.NewPart());
    var vp = w.ViewPort;
    Require(vp.UpdateGuarded, "frame guard is not installed (Avalonia OpenGlControlBase._update not found)");
    // (15:57) режим WGL: первый отказ кадра переводит графику на ANGLE со следующего запуска (сохранено сразу)
    var prefs = Parametric3D.App.Services.UserPrefs.Current;
    string? oldMode = prefs.GlMode;
    prefs.GlMode = "wgl";
    string? toast = null;
    Action<string> onFallback = m => toast = m;
    ModelViewport.GraphicsFallback += onFallback;
    bool ok = vp.RunFrameGuarded(() => throw new Avalonia.OpenGL.OpenGlException("Unable to configure OpenGL FBO"));
    ModelViewport.GraphicsFallback -= onFallback;
    string? newMode = prefs.GlMode;
    prefs.GlMode = oldMode; prefs.Save();
    Console.WriteLine($"  fallback: mode wgl → {newMode}, toast «{toast}»");
    Require(!ok && vp.FrameFailuresInRow == 1 && ModelViewport.FrameFailuresTotal >= 1, "failed frame is not contained");
    Require(newMode == "angle" && toast != null, "WGL frame failure does not switch the graphics to ANGLE");
    Require(vp.RunFrameGuarded(() => { }) && vp.FrameFailuresInRow == 0, "a good frame resets the failure count");
    w.Close();
});

Check("UI-KERNEL-WARMUP", () =>
{
    // (20:17) прогрев ядра в фоне после открытия окна: сцена прогрева строится без ошибок и не трогает документ
    var s = DocumentSession.NewPart(); var w = Open(s);
    int before = s.Part!.Features.Count;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int errors = Parametric3D.App.Diagnostics.KernelWarmup.Run();
    Console.WriteLine($"  warm-up: {sw.ElapsedMilliseconds} ms, errors {errors}; {Parametric3D.App.Diagnostics.KernelWarmup.BuildInfo}");
    Require(errors == 0, $"warm-up scene: {errors} feature(s) failed");
    Require(s.Part!.Features.Count == before && !s.Modified, "warm-up touched the user's document");
    Require(Parametric3D.App.Diagnostics.KernelWarmup.Enabled, "warm-up is off");
    w.Close();
});

Check("UI-DELETE-FEATURE-SKETCH", () =>
{
    // (23:43) п. 14: удаление операции убирает и её эскиз (если им больше никто не пользуется); оставшийся эскиз удаляется
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    s.Document.RebuildDirty();
    var ex = part.Features.OfType<ExtrudeFeature>().First();
    var sk = part.Sketches.First(k => ex.ConsumedSketchIds().Contains(k.Id));
    w.SelectItem(ex);
    var del = CommandRegistry.Instance["edit.delete"];
    Require(del.Enabled, "delete is disabled for the feature");
    del.Execute();
    Require(!part.Features.Contains(ex), "feature not deleted");
    Require(!part.Sketches.Contains(sk), "the feature's sketch is left behind");
    // эскиз без операции удаляется сам по себе
    var sk2 = new Sketch("free", Parametric3D.Kernel.Frame3.XY); part.Add(sk2);
    w.SelectItem(sk2);
    Require(del.Enabled, "delete is disabled for a free sketch");
    del.Execute();
    Require(!part.Sketches.Contains(sk2), "a free sketch cannot be deleted");
    // отмена возвращает операцию вместе с эскизом
    s.Undo();
    s.Undo();
    var p2 = s.Part!;
    Require(p2.Features.OfType<ExtrudeFeature>().Any() && p2.Sketches.Any(k => p2.Features.OfType<ExtrudeFeature>().First().ConsumedSketchIds().Contains(k.Id)), "undo did not restore the feature with its sketch");
    w.Close();
});

Check("UI-FACE-OUTLINE-ARCS", () =>
{
    // Кнопка «Тест», последовательность ручками: эскиз верхней грани после скругления вершины. Дуга скругления на грани
    // (кривая общего вида: пересечение шара с плоскостью) приходила в эскиз ломаной из 23 хорд — профиль расходился с гранью
    // на 5e-4 мм, выдавливание грани шло приближённым путём. Теперь — дуга (EdgeProjection.Recognize). Мутация P3D_PROJFIT=0.
    var s = DocumentSession.Sample();
    var part = s.Part!;
    s.Document.RebuildDirty();
    // как в последовательности: R10 на верхнем ребре вдоль X, фаска 8 на верхнем ребре вдоль Y, затем вершина
    double zt = part.Bodies[0].Vertices.Max(v => v.P.Z);
    bool Top(Parametric3D.Kernel.Topology.TEdge e) => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - zt) < 1e-6 && Math.Abs(e.V1.P.Z - zt) < 1e-6;
    var ex0 = part.Bodies[0].Edges.Where(e => Top(e) && Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-6).OrderBy(e => e.V0.P.Y).First();
    var rf0 = new RoundFeature { Radius = new ValueRef(10) }; rf0.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ex0)); part.AddFeature(rf0); s.Document.RebuildDirty();
    var ey0 = part.Bodies[0].Edges.Where(e => Top(e) && Math.Abs(e.V0.P.X - e.V1.P.X) < 1e-6).OrderByDescending(e => e.V0.P.X).First();
    var cf0 = new ChamferFeature { D1 = new ValueRef(8), D2 = new ValueRef(8) }; cf0.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ey0)); part.AddFeature(cf0); s.Document.RebuildDirty();
    var corner = part.Bodies[0].Vertices.Where(v => Math.Abs(v.P.Z - zt) < 1e-6).OrderBy(v => v.P.X - v.P.Y).First().P;   // угол (−x, +y) — прямые рёбра
    part.AddFeature(new VertexRoundFeature { Vertex = corner, Radius = new ValueRef(6) });
    s.Document.RebuildDirty();
    Require(part.Features.All(f => f.Error == null), string.Join("; ", part.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error)));
    var top = part.Bodies[0].Faces.Where(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99).OrderByDescending(f => Refs.FacePlane(part, f.Id)!.Value.P.Z).First();
    var sk = Parametric3D.App.Operations.Topo.FaceOutlineSketch(part, top.Id, "t");
    Require(sk != null, "no outline sketch");
    int arcs = sk!.Entities.OfType<SkArc>().Count(), lines = sk.Entities.OfType<SkLine>().Count();
    Console.WriteLine($"  outline: {arcs} arc(s), {lines} line(s); corner {corner}; edges {string.Join(" ", top.Edges.Select(e => e.Curve.GetType().Name))}");
    Require(arcs >= 1 && lines <= 8, $"outline came as {lines} lines and {arcs} arcs");
    var prof = sk.ToProfile();
    Require(prof.Loops.Count == 1, $"outline profile has {prof.Loops.Count} loops");
    // кривая общего вида (кривая пересечения, часть ребра) — дугой и отрезком, если она ими является; эллипс — ломаной
    var sk2 = new Sketch("r", Parametric3D.Kernel.Frame3.XY);
    var pool = new EdgeProjection.PointPool(SkRole.Object, "");
    var arcC = new Parametric3D.Kernel.Curves.ProcCurve3(t => new Vector3d(3 + 5 * Math.Cos(t), -2 + 5 * Math.Sin(t), 0), 0.3, 2.1, false, "test-arc");
    var lineC = new Parametric3D.Kernel.Curves.ProcCurve3(t => new Vector3d(1 + 2 * t, 4 - t, 0), 0, 1, false, "test-line");
    var ellC = new Parametric3D.Kernel.Curves.ProcCurve3(t => new Vector3d(6 * Math.Cos(t), 3 * Math.Sin(t), 0), 0, 1.5, false, "test-ellipse");
    var ea = EdgeProjection.Project(sk2, arcC, pool, SkRole.Object, ""); var el = EdgeProjection.Project(sk2, lineC, pool, SkRole.Object, "");
    var ee = EdgeProjection.Project(sk2, ellC, pool, SkRole.Object, "");
    Console.WriteLine($"  generic: arc -> {string.Join(",", ea.Select(x => x.GetType().Name))}; line -> {string.Join(",", el.Select(x => x.GetType().Name))}; ellipse -> {ee.Count} entities");
    Require(ea.Count == 1 && ea[0] is SkArc a1 && Math.Abs((a1.Center.P - new Vector2d(3, -2)).Length) < 1e-7 && a1.CCW, "generic arc was not recognised");
    Require(el.Count == 1 && el[0] is SkLine, "generic line was not recognised");
    Require(ee.Count > 2 && ee.All(x => x is SkLine), "an ellipse must stay a polyline");
});

Check("UI-ROLLBACK-BAR", () =>
{
    // (23:43) п. 16: строка отката в дереве — всегда (деталь — оранжевая, сборка — синяя), тянется мышью между элементами:
    // ниже строки — не в расчёте; отмена правки возвращает. п. 13: у документа детали опорных элементов служебной сборки
    // нет (в сцене их не рисуют); у сборки — есть. п. 2: в правке эскиза опорные плоскости не рисуются.
    var part = Parametric3D.App.Diagnostics.TestParts.Realistic(3, 5);
    var s = DocumentSession.FromPart(part, "rb");
    var w = Open(s);
    s.Document.RebuildDirty();
    double vAll = part.Volume;
    w.RebuildTree();
    var nodes = w.AllNodes.ToList();
    var bar = nodes.FirstOrDefault(n => n.IsBar);
    Require(bar != null && bar.BarBrush != null && bar.Item is InfoItem { Role: "rollback" }, "no part rollback bar in the tree");
    var f0 = nodes.First(n => n.Item == part.Features[0]);
    Require(w.MoveRollbackBar(bar!, f0, lower: true), "bar did not move");
    s.Document.RebuildDirty();
    Require(part.StageIndex == 1 && part.Features.Skip(1).All(part.IsBeyondStage), $"stage {part.StageIndex}");
    Require(Math.Abs(part.Volume - vAll) > 1e-6, "volume did not change below the bar");
    w.RebuildTree();
    Require(w.AllNodes.Where(n => n.Item is Feature f && part.Features.IndexOf(f) >= 1).All(n => n.IsDim), "features below the bar are not dimmed");
    s.Undo(); s.Document.RebuildDirty();
    var p2 = s.Part!;
    Require(!p2.InInsertMode && Math.Abs(p2.Volume - vAll) < 1e-6 * vAll, "undo did not return the bar");
    // служебный корень документа детали: его плоскостей в сцене нет
    var scene = Parametric3D.Render.Engine.SceneBuilder.Build(s.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions());
    var root = s.Document.Root;
    Require(!scene.PickTable.Skip(1).Any(e => e.Ref.Target == root.PlaneXY || e.Ref.Target == root.Origin || e.Ref.Id == root.PlaneXY.Id), "part document draws the hidden root datums");
    Require(scene.PickTable.Skip(1).Any(e => e.Ref.Id == p2.PlaneXY.Id), "part datums are not drawn");
    w.Close();
    // сборка: опорные корня есть; строка отката сборки — компоненты ниже не рисуются
    var sa = DocumentSession.NewAssembly(); var wa = Open(sa);
    var a = AssemblyCommands.AddComponent(wa, CubePart("A", 40), "add A");
    var b = AssemblyCommands.AddComponent(wa, CubePart("B", 20), "add B");
    sa.Document.RebuildDirty();
    wa.RebuildTree();
    var abar = wa.AllNodes.FirstOrDefault(n => n.Item is InfoItem { Role: "asm_rollback" });
    Require(abar != null, "no assembly rollback bar");
    var sceneA = Parametric3D.Render.Engine.SceneBuilder.Build(sa.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions());
    Require(sceneA.PickTable.Skip(1).Any(e => e.Ref.Id == sa.Document.Root.PlaneXY.Id), "assembly datums are not drawn");
    var an = wa.AllNodes.First(n => n.Item == a);
    Require(wa.MoveRollbackBar(abar!, an, lower: true), "assembly bar did not move");
    Require(sa.Document.Root.IsBeyondStage(b) && !sa.Document.Root.IsBeyondStage(a), "component below the bar");
    var sceneB = Parametric3D.Render.Engine.SceneBuilder.Build(sa.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions());
    Require(!sceneB.PickTable.Skip(1).Any(e => e.PartId == b.Id), "component below the assembly bar is drawn");
    wa.Close();
    // в правке эскиза опорные плоскости не рисуются
    var s3 = DocumentSession.Sample(); var w3 = Open(s3); var p3 = s3.Part!;
    w3.SelectItem(p3.PlaneXY); w3.StartOperation(new SketchOp());
    var sceneS = Parametric3D.Render.Engine.SceneBuilder.Build(s3.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions(), p3, w3.ViewPort.ActiveSketch);
    Require(!sceneS.PickTable.Skip(1).Any(e => e.Ref.Kind == PickKind.DatumPlane), "datum planes are drawn in sketch mode");
    w3.CancelSketch(); w3.Close();
});

Check("UI-SKETCH-SETUP", () =>
{
    // (23:43) п. 15: настройка эскиза — особый режим: стрелки нормали и «верха» в виде, щелчок по стрелке нормали —
    // взгляд с обратной стороны (тело выдавливается в другую сторону); новая плоскость-основа; основа нуля и смещение
    var s = DocumentSession.FromPart(Parametric3D.App.Diagnostics.TestParts.Block("setup"), "setup");
    var w = Open(s);
    var part = s.Part!;
    s.Document.RebuildDirty();
    var sk = part.Sketches.First();
    double z0 = part.Bounds.Min.Z, z1 = part.Bounds.Max.Z;
    w.SelectItem(sk);
    var cmd = CommandRegistry.Instance["sk.setup"];
    Require(cmd.Enabled, "setup is disabled for a selected sketch");
    cmd.Execute();
    Require(w.CurrentOperation is SketchSetupOp, "setup mode did not start");
    Require(w.ViewPort.Annotations.Count >= 10, $"arrows are not shown: {w.ViewPort.Annotations.Count} lines");
    var op = (SketchSetupOp)w.CurrentOperation!;
    var n0 = op.Sketch.Plane.W;
    op.FlipView();
    Require(Vector3d.Dot(op.Sketch.Plane.W, n0) < -0.99, "normal did not flip");
    w.CommitOperation();
    s.Document.RebuildDirty();
    var p1 = s.Part!;
    Console.WriteLine($"  flipped: z {p1.Bounds.Min.Z:0.##}..{p1.Bounds.Max.Z:0.##} (was {z0:0.##}..{z1:0.##})");
    Require(Math.Abs(p1.Bounds.Min.Z - (z0 - (z1 - z0))) < 1e-6 || Math.Abs(p1.Bounds.Max.Z - z0) < 1e-6, "flip did not turn the body to the other side");
    Require(w.ViewPort.Annotations.Count == 0, "arrows left after the setup");
    // новая плоскость-основа: XZ — тело вдоль Y
    var sk1 = p1.Sketches.First(); w.SelectItem(sk1); cmd.Execute();
    var op2 = (SketchSetupOp)w.CurrentOperation!;
    w.ViewPort.Selection.Select(new PickRef(p1.PlaneXZ.Id, PickKind.DatumPlane, p1, p1.PlaneXZ), additive: false);
    Require(op2.Sketch.BasePlaneId == p1.PlaneXZ.Id && op2.Sketch.PlaneDescription.Contains("XZ"), $"base plane not changed: {op2.Sketch.PlaneDescription}");
    // основа нулевой точки — начало детали, смещение 10 по X эскиза
    w.ViewPort.Selection.Select(new PickRef(p1.Origin.Id, PickKind.DatumPoint, p1, p1.Origin), additive: false);
    Require(op2.Sketch.OriginBase is { } ob && ob.Length < 1e-9, "origin base not set");
    op2.Sketch.SetOriginOffset(new Vector2d(10, 0));
    Require((op2.Sketch.Plane.Origin - op2.Sketch.Plane.U * 10).Length < 1e-9, $"origin offset: {op2.Sketch.Plane.Origin}");
    w.CommitOperation();
    s.Document.RebuildDirty();
    var p2 = s.Part!;
    Console.WriteLine($"  on XZ: bounds {p2.Bounds.Min:0.##} .. {p2.Bounds.Max:0.##}");
    Require(p2.Bounds.Max.Y - p2.Bounds.Min.Y < 21 && p2.Bounds.Max.Z - p2.Bounds.Min.Z > 59, "body is not along Y after the new base plane (XZ)");
    // настройка сохраняется в файле
    var path = Path.Combine(Path.GetTempPath(), "p3d_setup" + DocumentSession.PartExtension);
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path);
    var skr = s2.Part!.Sketches.First();
    Require(skr.PlaneDescription.Contains("XZ") && skr.OriginOffset.X == 10 && skr.OriginBase != null, $"setup not saved: {skr.PlaneDescription} {skr.OriginOffset}");
    File.Delete(path);
    w.Close();
});

// u13: эскиз на плоскости с рисованием инструментами (для проверок операций по эскизам)
Sketch DrawSketch(MainWindow w, Part part, ModelItem plane, Action<Sketch> draw)
{
    w.ViewPort.Selection.Clear();
    w.SelectItem(plane);
    w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch mode did not start");
    var sk = w.ViewPort.ActiveSketch!;
    draw(sk);
    w.FinishSketch();
    w.ViewPort.Selection.Clear();
    return sk;
}
void Rect(Sketch sk, double x0, double y0, double x1, double y1) { var r = new RectangleTool(); r.Click(new SketchInput(sk, new Vector2d(x0, y0), null)); r.Click(new SketchInput(sk, new Vector2d(x1, y1), null)); }
void Select(MainWindow w, PickRef r) => w.ViewPort.Selection.Select(r, additive: true);

// u13: переход по сечениям (квадраты 20 и 10 на высоте 40 — усечённая пирамида) и протяжка (круг R5 по отрезку 50)
Check("UI-U13-LOFT-SWEEP", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var a = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, -10, -10, 10, 10));
    var p40 = new DatumPlane("P40", Parametric3D.Kernel.Frame3.FromNormal(new Vector3d(0, 0, 40), Vector3d.UnitZ)); part.Add(p40);
    var b = DrawSketch(w, part, p40, sk => Rect(sk, -5, -5, 5, 5));
    Select(w, new PickRef(a.Id, PickKind.SketchCurve, part, a)); Select(w, new PickRef(b.Id, PickKind.SketchCurve, part, b));
    w.StartOperation(new LoftOp());
    w.CommitOperation(); s.Document.RebuildDirty();
    var loft = part.Features.OfType<LoftFeature>().Single();
    double want = 40.0 / 3 * (400 + 100 + 200);
    Console.WriteLine($"  loft: error {loft.Error}, volume {part.Volume:F4} want {want:F4}");
    Require(loft.Error == null && Math.Abs(part.Volume - want) < 1e-6 * want, $"loft volume {part.Volume} ({loft.Error})");
    w.Close();

    var s2 = DocumentSession.NewPart(); var w2 = Open(s2); var p2 = s2.Part!;
    var sec = DrawSketch(w2, p2, p2.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(5, 0), null)); });
    var path = DrawSketch(w2, p2, p2.PlaneXZ, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, Vector2d.Zero, null)); l.Click(new SketchInput(sk, new Vector2d(0, 50), null)); });
    Select(w2, new PickRef(sec.Id, PickKind.SketchCurve, p2, sec)); Select(w2, new PickRef(path.Id, PickKind.SketchCurve, p2, path));
    w2.StartOperation(new SweepOp());
    w2.CommitOperation(); s2.Document.RebuildDirty();
    var sw = p2.Features.OfType<SweepFeature>().Single();
    double want2 = Math.PI * 25 * 50;
    Console.WriteLine($"  sweep: error {sw.Error}, volume {p2.Volume:F4} want {want2:F4}");
    Require(sw.Error == null && Math.Abs(p2.Volume - want2) < 1e-3 * want2, $"sweep volume {p2.Volume} ({sw.Error})");
    w2.Close();
});

// u13b: кнопки эскиза — связи, размеры (в т. ч. длина дуги), обрезка, скругление угла, новые инструменты рисования, смещение, зеркало
Check("UI-U13B-SKETCH", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    w.ViewPort.Selection.Clear(); w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch mode");
    var sk = w.ViewPort.ActiveSketch!; var solver = s.Document.Solver;
    void Pick(params SkEntity[] es) { w.ViewPort.Selection.Clear(); foreach (var e in es) w.ViewPort.Selection.Select(new PickRef(e.Id, e is SkPoint ? PickKind.SketchPoint : PickKind.SketchCurve, sk, e), additive: true); }
    SkLine Line(double x0, double y0, double x1, double y1) { var t = new LineTool(chain: false); t.Click(new SketchInput(sk, new Vector2d(x0, y0), null)); t.Click(new SketchInput(sk, new Vector2d(x1, y1), null)); return sk.Entities.OfType<SkLine>().Last(); }
    int Count<T>() => sk.Entities.OfType<T>().Count();

    // связи: горизонтально, параллельно, равенство
    var l1 = Line(0, 0, 10, 1); Pick(l1);
    Require(SketchCommands.Constrain(w, ConstraintType.Horizontal) && Math.Abs(l1.A.P.Y - l1.B.P.Y) < 1e-7, "horizontal");
    var l2 = Line(0, 5, 8, 9); Pick(l1, l2);
    Require(SketchCommands.Constrain(w, ConstraintType.Parallel) && Math.Abs(l1.Direction.X * l2.Direction.Y - l1.Direction.Y * l2.Direction.X) < 1e-7, "parallel");
    Pick(l1, l2);
    Require(SketchCommands.Constrain(w, ConstraintType.Equal) && Math.Abs((l1.B.P - l1.A.P).Length - (l2.B.P - l2.A.P).Length) < 1e-6, "equal");
    // размер отрезка → 20: оба отрезка 20 (равенство)
    Pick(l1); var d1 = SketchCommands.Dimension(w, SketchCommands.DimKind.Linear);
    Require(d1 != null && !d1.Reference, "linear dimension");
    d1!.Value = 20; sk.Solve(solver);
    Console.WriteLine($"  lines: {(l1.B.P - l1.A.P).Length:F6} {(l2.B.P - l2.A.P).Length:F6}");
    Require(Math.Abs((l1.B.P - l1.A.P).Length - 20) < 1e-6 && Math.Abs((l2.B.P - l2.A.P).Length - 20) < 1e-6, "dimension drives both lines");
    // противоречивая связь снимается: вертикально к горизонтальному отрезку с длиной 20
    int nc = sk.Constraints.Count; Pick(l1);
    Require(!SketchCommands.Constrain(w, ConstraintType.Vertical) && sk.Constraints.Count == nc, "conflicting constraint reverted");
    // коллинеарно
    var l3 = Line(30, 3, 40, 4); Pick(l1, l3);
    Require(SketchCommands.Constrain(w, ConstraintType.Parallel, collinear: true), "collinear");
    double off = (l3.A.P - l1.A.P).X * l1.Direction.Y - (l3.A.P - l1.A.P).Y * l1.Direction.X;
    Require(Math.Abs(off) < 1e-6, $"collinear offset {off}");

    // длина дуги: дуга R10 на 90°, размер 2π·10/4 → задать 20 (радиус свободен, сохраняется угол? — проверяем R·sweep)
    var c = sk.AddEntity(new SkPoint(new Vector2d(0, -50))); var a0 = sk.AddEntity(new SkPoint(new Vector2d(10, -50))); var a1 = sk.AddEntity(new SkPoint(new Vector2d(0, -40)));
    var arc = sk.AddEntity(new SkArc(c, a0, a1));
    Pick(arc); var da = SketchCommands.Dimension(w, SketchCommands.DimKind.ArcLength);
    Require(da != null && Math.Abs(da.Value - Math.PI * 5) < 1e-6, $"arc length {da?.Value}");
    Pick(arc); var dr = SketchCommands.Dimension(w, SketchCommands.DimKind.Radius);
    Require(dr != null, "radius dimension");
    da!.Value = 10 * Math.PI / 3; sk.Solve(solver); // 60° при R10
    double sweep = Math.Abs(Math.Atan2(((arc.Start.P - c.P).X * (arc.End.P - c.P).Y - (arc.Start.P - c.P).Y * (arc.End.P - c.P).X), Vector2d.Dot(arc.Start.P - c.P, arc.End.P - c.P)));
    Console.WriteLine($"  arc: R {arc.Radius:F6} sweep {sweep * 180 / Math.PI:F4}°");
    Require(Math.Abs(arc.Radius - 10) < 1e-6 && Math.Abs(sweep - Math.PI / 3) < 1e-6, "arc length drives sweep");

    // обрезка: крест, убрать правую половину горизонтального
    var h = Line(100, 0, 120, 0); var v = Line(110, -10, 110, 10);
    Require(TrimTool.Trim(sk, h, new Vector2d(117, 0)), "trim");
    var hl = sk.Entities.OfType<SkLine>().Where(l => Math.Abs(l.A.P.Y) < 1e-9 && Math.Abs(l.B.P.Y) < 1e-9 && Math.Min(l.A.P.X, l.B.P.X) >= 99).ToList();
    Console.WriteLine($"  trim: {string.Join(" ", hl.Select(l => $"[{l.A.P.X:F3},{l.B.P.X:F3}]"))}");
    Require(hl.Count == 1 && Math.Abs(Math.Max(hl[0].A.P.X, hl[0].B.P.X) - 110) < 1e-6, "trimmed to the cross");
    int nl = Count<SkLine>();
    Require(TrimTool.Split(sk, v, new Vector2d(110, 5)) && Count<SkLine>() == nl + 1, "split");

    // скругление угла прямоугольника R3
    Rect(sk, 200, 0, 220, 10); int na = Count<SkArc>();
    Require(CornerTool.Apply(sk, new Vector2d(220, 10), false, 3) && Count<SkArc>() == na + 1 && Math.Abs(sk.Entities.OfType<SkArc>().Last().Radius - 3) < 1e-6, "corner fillet");
    int nl2 = Count<SkLine>();
    Require(CornerTool.Apply(sk, new Vector2d(200, 0), true, 2) && Count<SkLine>() == nl2 + 1, "corner chamfer");

    // инструменты: многоугольник 6, паз, прямоугольник от центра и по трём точкам, окружность по двум точкам
    int n0 = Count<SkLine>(); var pg = new PolygonTool(6); pg.Click(new SketchInput(sk, new Vector2d(300, 0), null)); pg.Click(new SketchInput(sk, new Vector2d(310, 0), null));
    Require(Count<SkLine>() == n0 + 6, "polygon 6");
    n0 = Count<SkLine>(); na = Count<SkArc>(); var sl = new SlotTool();
    sl.Click(new SketchInput(sk, new Vector2d(300, 50), null)); sl.Click(new SketchInput(sk, new Vector2d(330, 50), null)); sl.Click(new SketchInput(sk, new Vector2d(330, 55), null));
    Require(Count<SkLine>() == n0 + 2 && Count<SkArc>() == na + 2, "slot");
    n0 = Count<SkLine>(); var rc = new RectCenterTool(); rc.Click(new SketchInput(sk, new Vector2d(400, 0), null)); rc.Click(new SketchInput(sk, new Vector2d(405, 3), null));
    var rcl = sk.Entities.OfType<SkLine>().Skip(n0).Where(l => l.Role != SkRole.Construction).ToList();
    Require(rcl.Count == 4 && Math.Abs(rcl.Min(l => Math.Min(l.A.P.X, l.B.P.X)) - 395) < 1e-6, "centre rectangle");
    n0 = Count<SkLine>(); var r3 = new Rect3PTool();
    foreach (var q in new[] { new Vector2d(500, 0), new Vector2d(508, 6), new Vector2d(505, 12) }) r3.Click(new SketchInput(sk, q, null));
    var r3l = sk.Entities.OfType<SkLine>().Skip(n0).ToList();
    Require(r3l.Count == 4 && r3l.Any(l => Math.Abs((l.B.P - l.A.P).Length - 10) < 1e-6), "3-point rectangle");
    int ncir = Count<SkCircle>(); var c2 = new Circle2PTool(); c2.Click(new SketchInput(sk, new Vector2d(600, 0), null)); c2.Click(new SketchInput(sk, new Vector2d(610, 0), null));
    Require(Count<SkCircle>() == ncir + 1 && Math.Abs(sk.Entities.OfType<SkCircle>().Last().Radius - 5) < 1e-6, "2-point circle");

    // эллипс по пяти точкам: полуоси 8 и 3, поворот 30°
    double ang = Math.PI / 6; var pts5 = Enumerable.Range(0, 5).Select(i => { double t = i * 1.1; var e = new Vector2d(8 * Math.Cos(t), 3 * Math.Sin(t)); return new Vector2d(700 + e.X * Math.Cos(ang) - e.Y * Math.Sin(ang), e.X * Math.Sin(ang) + e.Y * Math.Cos(ang)); }).ToList();
    var el = Ellipse5PTool.Fit(pts5);
    Console.WriteLine($"  ellipse: {el?.Center.P} {el?.RU:F6} {el?.RV:F6}");
    Require(el != null && (el.Center.P - new Vector2d(700, 0)).Length < 1e-6 && Math.Abs(Math.Max(el.RU, el.RV) - 8) < 1e-6 && Math.Abs(Math.Min(el.RU, el.RV) - 3) < 1e-6, "5-point ellipse");
    // сплайн через точки проходит через них
    var q4 = new List<Vector2d> { new(800, 0), new(810, 5), new(820, -3), new(830, 2) };
    var cv = SplineThroughTool.Interpolate(q4);
    Vector2d Bez(List<Vector2d> P, double t) { var b = P.ToList(); for (int k = b.Count - 1; k > 0; k--) for (int i = 0; i < k; i++) b[i] = b[i] * (1 - t) + b[i + 1] * t; return b[0]; }
    double tot = 0; var tq = new List<double> { 0 }; for (int i = 1; i < q4.Count; i++) { tot += (q4[i] - q4[i - 1]).Length; tq.Add(tot); }
    Require(cv != null && q4.Select((q, i) => (Bez(cv, tq[i] / tot) - q).Length).Max() < 1e-7, "spline through points");

    // смещение контура прямоугольника на 2 наружу: квадрат 10 → 14
    Rect(sk, 900, 0, 910, 10);
    var rect = sk.Entities.OfType<SkLine>().TakeLast(4).ToList();
    var offs = SketchEditCommands.OffsetChain(sk, rect.Cast<SkEntity>().ToList(), 2);
    var xs = offs.OfType<SkLine>().SelectMany(l => new[] { l.A.P.X, l.B.P.X }).ToList();
    Console.WriteLine($"  offset: {offs.Count} lines, x {xs.Min():F4}..{xs.Max():F4}");
    Require(offs.Count == 4 && (Math.Abs(Math.Abs(xs.Max() - xs.Min()) - 14) < 1e-6 || Math.Abs(Math.Abs(xs.Max() - xs.Min()) - 6) < 1e-6), "offset chain");
    // зеркало отрезка относительно вертикальной оси x=1000
    var m = Line(1003, 0, 1005, 4); var ax = Line(1000, -10, 1000, 10);
    int nm = Count<SkLine>(); Pick(m, ax); SketchEditCommands.Mirror(w);
    var mir = sk.Entities.OfType<SkLine>().Last();
    Require(Count<SkLine>() == nm + 1 && new[] { mir.A.P.X, mir.B.P.X }.Order().SequenceEqual(new[] { 995.0, 997.0 }.Select(x => x), new Tol()), $"mirror {mir.A.P} {mir.B.P}");
    // площадь: отдельный эскиз — квадрат 10 с отверстием R2
    w.FinishSketch();
    w.ViewPort.Selection.Clear(); w.SelectItem(part.PlaneXZ); w.StartOperation(new SketchOp());
    var ska = w.ViewPort.ActiveSketch!; Rect(ska, 0, 0, 10, 10);
    var ct = new CircleTool(); ct.Click(new SketchInput(ska, new Vector2d(5, 5), null)); ct.Click(new SketchInput(ska, new Vector2d(7, 5), null));
    var ar = SketchCommands.Area(ska);
    Console.WriteLine($"  area: {ar}");
    Require(ar is { } aa && Math.Abs(aa.Area - (100 - Math.PI * 4)) < 1e-6 && aa.Regions == 1, "area");
    w.FinishSketch();
    var st = sk.Solve(solver);
    Console.WriteLine($"  solve: {st}, entities {sk.Entities.Count}, constraints {sk.Constraints.Count}: {string.Join(", ", sk.Constraints.GroupBy(c => c.Type).Select(g => g.Key + " " + g.Count()))}; conflicts: {string.Join(", ", sk.Constraints.Where(c => c.Conflict).Select(c => c.Type + "[" + string.Join("/", c.Items.Select(i => i.GetType().Name + (i is SkPoint pp ? $"({pp.X:0.#},{pp.Y:0.#})" : ""))) + "]"))}");
    Require(st is not (SolveStatus.Overconstrained or SolveStatus.Inconsistent), $"sketch solves: {st}");
    w.Close();
});

// u13c: тонкое выдавливание, винтовая протяжка, перенос/копия, прямая правка граней, массивы по точкам/кривой/заполнением, резьба
Check("UI-U13C-FEATURES", () =>
{
    // тонкое выдавливание: квадрат 20, стенка 2 внутрь, высота 10 → (400 − 256)·10
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var a = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, -10, -10, 10, 10));
    var ex = new ExtrudeFeature { SketchId = a.Id, Thin = new ValueRef(2) }; ex.Side1.Distance = new ValueRef(10); part.AddFeature(ex); s.Document.RebuildDirty();
    Console.WriteLine($"  thin: error {ex.Error}, volume {part.Volume:F6}");
    Require(ex.Error == null && Math.Abs(part.Volume - 1440) < 1e-6, $"thin extrude {part.Volume} {ex.Error}");
    ex.ThinOutside = true; part.NotifyChanged(ex); s.Document.RebuildDirty();
    Console.WriteLine($"  thin outside: error {ex.Error}, volume {part.Volume:F6}");
    Require(ex.Error == null && Math.Abs(part.Volume - (576 - 400) * 10) < 1e-6, $"thin outside {part.Volume} {ex.Error}");
    w.Close();

    // винтовая протяжка: круг r1 на расстоянии 10 от оси, шаг 5, 3 витка
    var s2 = DocumentSession.NewPart(); var w2 = Open(s2); var p2 = s2.Part!;
    var sec = DrawSketch(w2, p2, p2.PlaneXZ, sk =>
    {
        var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(10, 0), null)); c.Click(new SketchInput(sk, new Vector2d(11, 0), null));
        var cl = new LineTool(chain: false); cl.Click(new SketchInput(sk, new Vector2d(0, -5), null)); cl.Click(new SketchInput(sk, new Vector2d(0, 5), null));
        sk.Entities.OfType<SkLine>().Last().Role = SkRole.Centerline;
    });
    var hs = new HelicalSweepFeature { SketchId = sec.Id, Pitch = new ValueRef(5), Turns = new ValueRef(3) }; p2.AddFeature(hs); s2.Document.RebuildDirty();
    double wantH = Math.PI * 3 * Math.Sqrt(Math.Pow(2 * Math.PI * 10, 2) + 25);
    Console.WriteLine($"  helical: error {hs.Error}, volume {p2.Volume:F3} want {wantH:F3}");
    Require(hs.Error == null && Math.Abs(p2.Volume - wantH) < 0.02 * wantH, $"helical sweep {p2.Volume} ({hs.Error})");
    var zs = p2.Bodies[0].Vertices.Select(v => v.P.Z).ToList();
    Require(zs.Count == 0 || zs.Max() - zs.Min() > 14, "helix height");
    w2.Close();

    // брусок 40×20×10 для прямой правки, копии, массивов
    (DocumentSession S, MainWindow W, Part P, Sketch Sk) Box()
    {
        var sb = DocumentSession.NewPart(); var wb = Open(sb); var pb = sb.Part!;
        var b = DrawSketch(wb, pb, pb.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
        var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); pb.AddFeature(e); sb.Document.RebuildDirty();
        Require(Math.Abs(pb.Volume - 8000) < 1e-6, "box");
        return (sb, wb, pb, b);
    }
    var (s3, w3, p3, _) = Box();
    var mc = new MoveCopyFeature { Dx = new ValueRef(100), Copy = true }; p3.AddFeature(mc); s3.Document.RebuildDirty();
    Require(mc.Error == null && Math.Abs(p3.Volume - 16000) < 1e-6, $"copy {p3.Volume} {mc.Error}");
    mc.Copy = false; mc.Angle = new ValueRef(90); p3.NotifyChanged(mc); s3.Document.RebuildDirty();
    var bx = p3.Bodies[0].Vertices.Select(v => v.P).ToList();
    Console.WriteLine($"  move: x {bx.Min(v => v.X):F3}..{bx.Max(v => v.X):F3} y {bx.Min(v => v.Y):F3}..{bx.Max(v => v.Y):F3}");
    Require(mc.Error == null && Math.Abs(p3.Volume - 8000) < 1e-6 && Math.Abs(bx.Min(v => v.X) - 80) < 1e-6 && Math.Abs(bx.Max(v => v.Y) - 40) < 1e-6, "move + rotate");
    p3.RemoveFeature(mc); s3.Document.RebuildDirty();
    // смещение верхней грани на 5 → 40·20·15; через операцию UI
    var top = p3.Bodies[0].Faces.First(f => Refs.FacePlane(p3, f.Id) is { } pl && pl.N.Z > 0.99);
    w3.ViewPort.Selection.Clear(); Select(w3, new PickRef(top.Id, PickKind.Face, p3, null));
    var fop = new FaceEditOp(); w3.StartOperation(fop);
    var fe = p3.Features.OfType<FaceEditFeature>().Last(); fe.Distance = new ValueRef(5);
    w3.CommitOperation(); s3.Document.RebuildDirty();
    Console.WriteLine($"  offset face: error {fe.Error}, volume {p3.Volume:F6}");
    Require(fe.Error == null && Math.Abs(p3.Volume - 12000) < 1e-6, $"offset face {p3.Volume} {fe.Error}");
    p3.RemoveFeature(fe); s3.Document.RebuildDirty();
    // скругление R5 верхнего ребра → новый радиус 3 → удаление скругления
    double zMax = 10;
    var edge = p3.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - zMax) < 1e-6 && Math.Abs(x.V1.P.Z - zMax) < 1e-6 && Math.Abs(x.V0.P.Y - x.V1.P.Y) < 1e-9 && Math.Abs(x.V0.P.Y) < 1e-9);
    var rf = new RoundFeature { Radius = new ValueRef(5) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); p3.AddFeature(rf); s3.Document.RebuildDirty();
    double vR5 = p3.Volume, cut5 = (25 - Math.PI * 25 / 4) * 40;
    Require(Math.Abs(vR5 - (8000 - cut5)) < 1e-6, $"round {vR5}");
    var rface = p3.Bodies[0].Faces.First(f => f.Tag.Contains("round:face"));
    var at = Parametric3D.Model.Features.FaceRefs.Inside(rface);
    var nr = new FaceEditFeature { Mode = FaceEditMode.Radius, Radius = new ValueRef(3) }; nr.OnFaces.Add(at); p3.AddFeature(nr); s3.Document.RebuildDirty();
    double cut3 = (9 - Math.PI * 9 / 4) * 40;
    Console.WriteLine($"  new radius: error {nr.Error}, volume {p3.Volume:F6} want {8000 - cut3:F6}");
    Require(nr.Error == null && Math.Abs(p3.Volume - (8000 - cut3)) < 1e-6, $"new radius {p3.Volume} {nr.Error}");
    nr.Mode = FaceEditMode.Delete; p3.NotifyChanged(nr); s3.Document.RebuildDirty();
    Console.WriteLine($"  delete round: error {nr.Error}, volume {p3.Volume:F6}");
    Require(nr.Error == null && Math.Abs(p3.Volume - 8000) < 1e-6, $"delete round {p3.Volume} {nr.Error}");
    // резьба на цилиндре: отверстие Ø6 насквозь
    p3.RemoveFeature(nr); p3.RemoveFeature(rf); s3.Document.RebuildDirty();
    w3.Close();

    // массивы: вырез-круг r2 в (5,5) и копии в точках, по кривой, заполнением
    var (s4, w4, p4, _) = Box();
    var hole = DrawSketch(w4, p4, p4.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(5, 5), null)); c.Click(new SketchInput(sk, new Vector2d(7, 5), null)); });
    var cut = new ExtrudeFeature { SketchId = hole.Id, Cut = true }; cut.Side1.Distance = new ValueRef(10); p4.AddFeature(cut); s4.Document.RebuildDirty();
    double vHole = Math.PI * 4 * 10;
    Require(Math.Abs(p4.Volume - (8000 - vHole)) < 1e-6, $"hole {p4.Volume}");
    var pts = DrawSketch(w4, p4, p4.PlaneXY, sk => { foreach (var q in new[] { new Vector2d(5, 5), new Vector2d(15, 5), new Vector2d(25, 15) }) sk.AddEntity(new SkPoint(q)); });
    var pp = new PointsPatternFeature { SketchId = pts.Id }; pp.Sources.Add(cut.Key); p4.AddFeature(pp); s4.Document.RebuildDirty();
    Console.WriteLine($"  points pattern: error {pp.Error}, volume {p4.Volume:F6}");
    Require(pp.Error == null && Math.Abs(p4.Volume - (8000 - 3 * vHole)) < 1e-6, $"points pattern {p4.Volume} {pp.Error}");
    p4.RemoveFeature(pp);
    var path = DrawSketch(w4, p4, p4.PlaneXY, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, new Vector2d(5, 5), null)); l.Click(new SketchInput(sk, new Vector2d(35, 5), null)); });
    var pc = new PointsPatternFeature { SketchId = path.Id, Mode = PointsPatternMode.Curve, Count = new ValueRef(4) }; pc.Sources.Add(cut.Key); p4.AddFeature(pc); s4.Document.RebuildDirty();
    Console.WriteLine($"  curve pattern: error {pc.Error}, volume {p4.Volume:F6}");
    Require(pc.Error == null && Math.Abs(p4.Volume - (8000 - 4 * vHole)) < 1e-6, $"curve pattern {p4.Volume} {pc.Error}");
    p4.RemoveFeature(pc);
    var region = DrawSketch(w4, p4, p4.PlaneXY, sk => { Rect(sk, 1, 1, 39, 19); sk.AddEntity(new SkPoint(new Vector2d(5, 5))); });
    var pf = new PointsPatternFeature { SketchId = region.Id, Mode = PointsPatternMode.Fill, Spacing = new ValueRef(10), Margin = new ValueRef(2) }; pf.Sources.Add(cut.Key); p4.AddFeature(pf); s4.Document.RebuildDirty();
    // сетка от (5,5) с шагом 10: x 5,15,25,35; y 5,15 → 8 мест (все дальше 2 от края области 1..39×1..19)
    Console.WriteLine($"  fill pattern: error {pf.Error}, volume {p4.Volume:F6}");
    Require(pf.Error == null && Math.Abs(p4.Volume - (8000 - 8 * vHole)) < 1e-6, $"fill pattern {p4.Volume} {pf.Error}");
    // резьба на стенке отверстия
    var hf = p4.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 2) < 1e-9);
    var th = new CosmeticThreadFeature(); th.OnFaces.Add(Parametric3D.Model.Features.FaceRefs.Inside(hf)); p4.AddFeature(th); s4.Document.RebuildDirty();
    Console.WriteLine($"  thread: error {th.Error}, d {th.Diameter} internal {th.Internal}");
    Require(th.Error == null && Math.Abs(th.Diameter - 4) < 1e-9 && th.Internal, "thread");
    // сохранение и загрузка: новые операции переживают файл
    var json = Parametric3D.Model.DocumentFile.Save(p4);
    Require(json.Contains("points_pattern") && json.Contains("\"thread\""), "saved kinds");
    w4.Close();
});

// u13d: вид, выбор (режимы, рамка, контур грани), измерение и анализ
Check("UI-U13D-VIEW-SELECT", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); part.AddFeature(e); s.Document.RebuildDirty(); part.Rebuild(null);
    var vp = w.ViewPort;
    foreach (var id in new[] { "view.front", "view.top", "view.grid", "view.grid", "an.zebra", "an.zebra", "view.section", "view.section" })
        CommandRegistry.Instance.Find(id)!.Run!();
    Require(vp.SectionPlane == null && !vp.Zebra && !vp.ModelGrid, "toggles return");
    vp.Camera.State.Rotation = Parametric3D.Render.Camera.OrientationQuat(Parametric3D.Render.ViewOrientation.Isometric); vp.ZoomToFit(animate: false);
    // рамка на весь вид в режиме граней — шесть граней бруска
    ViewSelectCommands.SetMode(w, ViewSelectCommands.SelMode.Face);
    double W = vp.Camera.ViewportWidth / vp.Scaling, H = vp.Camera.ViewportHeight / vp.Scaling;
    var all = vp.ItemsInRegion(new[] { new Avalonia.Point(0, 0), new Avalonia.Point(W, 0), new Avalonia.Point(W, H), new Avalonia.Point(0, H) }, crossing: false);
    Console.WriteLine($"  region: {all.Count} items, kinds {string.Join(",", all.Select(x => x.Kind).Distinct())}");
    Require(all.Count == 6 && all.All(x => x.Kind == PickKind.Face), "box select faces");
    // режим «операция»: грань → выдавливание
    ViewSelectCommands.SetMode(w, ViewSelectCommands.SelMode.Feature);
    var mapped = vp.PickMap!(all[0]);
    Require(mapped.Kind == PickKind.Feature && ReferenceEquals(mapped.Target, e), "feature mode");
    ViewSelectCommands.SetMode(w, ViewSelectCommands.SelMode.Feature);   // повторно — выключить
    Require(ViewSelectCommands.Mode == ViewSelectCommands.SelMode.All && vp.PickMap == null && vp.Selection.Filter == PickFilter.All, "mode off");
    // контур верхней грани, площадь и периметр
    var top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    vp.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    Require(Math.Abs(ViewSelectCommands.SelectedArea(w) - 800) < 1e-6, $"area {ViewSelectCommands.SelectedArea(w)}");
    Require(Math.Abs(ViewSelectCommands.SelectedLength(w) - 120) < 1e-6, $"perimeter {ViewSelectCommands.SelectedLength(w)}");
    Require(ViewSelectCommands.FaceLoop(w) == 4 && vp.Selection.Items.All(x => x.Kind == PickKind.Edge), "face loop");
    // измерение двух вершин по диагонали
    var vs = part.Bodies[0].Vertices.ToList();
    var v0 = vs.First(v => v.P.Length < 1e-9); var v1 = vs.First(v => (v.P - new Vector3d(40, 20, 10)).Length < 1e-9);
    vp.Selection.Clear(); Select(w, new PickRef(v0.Id, PickKind.Vertex, part, null)); Select(w, new PickRef(v1.Id, PickKind.Vertex, part, null));
    var m = ViewSelectCommands.Measure(w);
    Console.WriteLine("  measure: " + string.Join(" | ", m));
    Require(m.Any(x => x.Contains(Math.Sqrt(2100).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))), "measure distance");
    // гребёнка кривизны: скругление R5 — постоянная кривизна
    var edge = part.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 10) < 1e-6 && Math.Abs(x.V1.P.Z - 10) < 1e-6 && Math.Abs(x.V0.P.Y) < 1e-9 && Math.Abs(x.V1.P.Y) < 1e-9);
    var rf = new RoundFeature { Radius = new ValueRef(5) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); part.AddFeature(rf); s.Document.RebuildDirty(); part.Rebuild(null);
    var arc = part.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 5) < 1e-9);
    vp.Selection.Clear(); Select(w, new PickRef(arc.Id, PickKind.Edge, part, null));
    var comb = ViewSelectCommands.CurvatureComb(w, scale: 10);
    var ac = ((Parametric3D.Kernel.Curves.ArcCurve3)arc.Curve).Frame.Origin;
    var spikes = comb.Where(c => Math.Abs((c.A - ac).Length - 5) < 1e-6 && (c.B - c.A).Length > 1e-9 && Math.Abs(Vector3d.Dot((c.B - c.A).Normalized(), (c.A - ac).Normalized())) > 0.999).Select(c => (c.B - c.A).Length).ToList();
    Console.WriteLine($"  comb: {comb.Count} lines, spike {spikes.Min():F4}..{spikes.Max():F4}");
    Require(comb.Count > 0 && spikes.All(x => Math.Abs(x - 10.0 / 5) < 1e-3), "curvature comb of R5");
    var chk = ViewSelectCommands.CheckBodies(part);
    Require(chk.Count == 1 && chk[0].Contains("годно") || chk[0].Contains("valid"), "check body: " + string.Join(";", chk));
    var mt = ViewSelectCommands.MassText(part);
    Require(mt.Contains("7785.398"), "mass text: " + mt);
    w.Close();
});

// u13d: копировать/вставить операции, исполнения (переменные и погашения), единицы
Check("UI-U13D-DOC", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var table = s.Document.ScopeFor(part); table.Add("H", 10, ParamKind.Real, "mm");
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10) { Formula = "H" }; part.AddFeature(e); s.Document.RebuildDirty();
    var hole = DrawSketch(w, part, part.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(5, 5), null)); c.Click(new SketchInput(sk, new Vector2d(7, 5), null)); });
    var cut = new ExtrudeFeature { SketchId = hole.Id, Cut = true, Name = "Hole" }; cut.Side1.Distance = new ValueRef(100); part.AddFeature(cut); s.Document.RebuildDirty();
    double v1 = part.Volume;
    Require(Math.Abs(v1 - (8000 - Math.PI * 40)) < 1e-6, $"base volume {v1}");
    // копировать и вставить вырез: ещё одна операция и эскиз, объём тот же (то же место)
    int nf = part.Features.Count, ns = part.Children.OfType<Sketch>().Count();
    w.SelectItem(cut); Require(DocCommands.Copy(w) == 1, "copy");
    var pasted = DocCommands.Paste(w); s.Document.RebuildDirty();
    Console.WriteLine($"  paste: {pasted.Count}, features {part.Features.Count}, sketches {part.Children.OfType<Sketch>().Count()}, volume {part.Volume:F6}, error {pasted.FirstOrDefault()?.Error}");
    Require(pasted.Count == 1 && part.Features.Count == nf + 1 && part.Children.OfType<Sketch>().Count() == ns + 1 && pasted[0].Error == null && Math.Abs(part.Volume - v1) < 1e-6, "paste");
    Require(pasted[0].Key != cut.Key && pasted[0].Name != cut.Name, "pasted key and name");
    part.RemoveFeature(pasted[0]); s.Document.RebuildDirty();
    // исполнения: «Высокая» — H = 20; «Без отверстия» — вырез погашен
    table.Find("H")!.Number = 20; table.Recalculate();
    part.Configurations.Add(Configurations.Capture(part, table, "Высокая"));
    table.Find("H")!.Number = 10; table.Recalculate(); cut.Suppressed = true;
    part.Configurations.Add(Configurations.Capture(part, table, "Без отверстия"));
    cut.Suppressed = false; part.Invalidate(); s.Document.RebuildDirty();
    Configurations.Apply(part, table, "Высокая"); s.Document.RebuildDirty();
    double vHigh = part.Volume;
    Configurations.Apply(part, table, "Без отверстия"); s.Document.RebuildDirty();
    double vNo = part.Volume;
    Configurations.Apply(part, table, ""); s.Document.RebuildDirty();
    double vBase = part.Volume;
    Console.WriteLine($"  configurations: high {vHigh:F3}, no hole {vNo:F3}, base {vBase:F3}");
    Require(Math.Abs(vHigh - (16000 - Math.PI * 80)) < 1e-6 && Math.Abs(vNo - 8000) < 1e-6 && Math.Abs(vBase - v1) < 1e-6 && !cut.Suppressed && part.ActiveConfiguration == "", "configurations");
    // единицы: дюймы в поле операции
    DocCommands.ToggleUnits(w);
    Require(DisplayUnits.Inch && part.Units.Length == "in", "inch on");
    DocCommands.ToggleUnits(w);
    Require(!DisplayUnits.Inch, "inch off");
    var json = Parametric3D.Model.DocumentFile.Save(part, table);
    Require(json.Contains("Высокая"), "configurations saved");
    w.Close();
});

// u13d: контекстная панель (повысить, похожие, режим, направление, ещё) и состояния дерева
Check("UI-U13D-CONTEXT", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); part.AddFeature(e); s.Document.RebuildDirty(); part.Rebuild(null);
    var vp = w.ViewPort;
    // похожие: верхняя грань → нижняя той же площади
    var top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    vp.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    Require(ContextCommands.SelectSimilar(w) == 1, $"similar faces {vp.Selection.Count}");
    // повысить: вершина → три ребра → четыре... грани → тело
    var v0 = part.Bodies[0].Vertices.First(v => v.P.Length < 1e-9);
    vp.Selection.Clear(); Select(w, new PickRef(v0.Id, PickKind.Vertex, part, null));
    Require(ContextCommands.Promote(w) == 3, "vertex → edges");
    Require(ContextCommands.Promote(w) == 3 && vp.Selection.Items.All(x => x.Kind == PickKind.Face), $"edges → faces {vp.Selection.Count}");
    Require(ContextCommands.Promote(w) == 1 && vp.Selection.Primary.Kind == PickKind.Body, "faces → body");
    // режим и направление в операции выдавливания
    vp.Selection.Clear();
    var op = new ExtrudeOp { SeedSketch = b }; w.StartOperation(op);
    var f = (ExtrudeFeature)op.EditedFeature!;
    var reg = CommandRegistry.Instance;
    Require(reg.Find("ctx.mode_cut")!.Enabled && reg.Find("ctx.flip")!.Enabled && reg.Find("ctx.both_sides")!.Enabled, "op context commands");
    reg.Find("ctx.mode_cut")!.Run!(); Require(f.Cut, "cut mode");
    reg.Find("ctx.mode_add")!.Run!(); Require(!f.Cut, "add mode");
    reg.Find("ctx.flip")!.Run!(); Require(f.Side1.Flip, "flip");
    reg.Find("ctx.both_sides")!.Run!(); Require(f.Symmetric, "both sides");
    reg.Find("ctx.next")!.Run!();
    w.CancelOperation();
    // «ещё»: допустимые операции вне предложенных
    vp.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    var more = ContextCommands.MoreOps(w);
    Require(more.Count > 0 && more.All(c => c.Id.StartsWith("op.")) && !more.Any(c => w.SuggestedOps().Contains(c.Id)), "more ops");
    // состояния дерева: подавленная операция и узлы детали
    e.Suppressed = true; s.Document.RebuildDirty();
    var st = TreeNode.StateOf(e, part);
    Require(st.File == "state_suppressed_ico.svg", "suppressed state");
    e.Suppressed = false; s.Document.RebuildDirty();
    var roots = TreeNode.Build(s.Document);
    var infos = roots[0].Children.Where(n => n.Item is InfoItem).Select(n => ((InfoItem)n.Item).Role).ToList();
    Console.WriteLine($"  tree info nodes: {string.Join(",", infos)}");
    Require(infos.Contains("params") && infos.Contains("material") && infos.Contains("body"), "info nodes");
    w.Close();
});

// u13e: система координат, опорные кривые (через точки, уравнение, проекция), протяжка по кривой, соосное и радиальное
// отверстия, полное скругление, группа, родители и потомки, замена эскиза, сохранение опорных элементов
Check("UI-U13E-DATUMS-HOLES", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    var b = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); part.AddFeature(e); s.Document.RebuildDirty(); part.Rebuild(null);
    // система координат по трём плоскостям детали — в начале
    w.ViewPort.Selection.Clear(); foreach (var pl in new[] { part.PlaneXY, part.PlaneXZ, part.PlaneYZ }) Select(w, new PickRef(pl.Id, PickKind.DatumPlane, part, pl));
    var cop = new CsysOp(); w.StartOperation(cop); w.CommitOperation();
    var cs = part.Children.OfType<DatumCsys>().Single();
    Require(cs.Frame.Origin.Length < 1e-9, $"csys origin {cs.Frame.Origin}");
    // кривая по уравнению: винтовая линия R50, 2 витка
    var eq = CurveOp.Equation(s.Document.ScopeFor(part), "50*cos(t)", "50*sin(t)", "t/10", 0, 720, 200, out var why);
    Require(eq != null && eq.Count == 201 && (eq[^1] - new Vector3d(50, 0, 72)).Length < 1e-9, $"equation curve {why}");
    // через точки: гладкая проходит через точки
    var q = new List<Vector3d> { new(0, 0, 0), new(10, 5, 0), new(20, 0, 5), new(30, 5, 5) };
    var tp = CurveOp.ThroughPoints(q, false, true);
    Require(q.All(x => tp.Any(y => (x - y).Length < 1e-9)), "through points");
    // проекция отрезка на верхнюю грань вдоль −Z
    var top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    var tris = new List<(Vector3d, Vector3d, Vector3d)>();
    var md = part.Mesh!; for (int i = 0; i + 2 < md.Indices.Length; i += 3) if (md.Vertices[md.Indices[i]].FaceId == top.Id) tris.Add((V(md.Vertices[md.Indices[i]].P), V(md.Vertices[md.Indices[i + 1]].P), V(md.Vertices[md.Indices[i + 2]].P)));
    var pr = CurveOp.Project(Enumerable.Range(0, 11).Select(i => new Vector3d(5 + 3 * i, 10, 50)), -Vector3d.UnitZ, tris);
    Require(pr.Count == 11 && pr.All(x => Math.Abs(x.Z - 10) < 1e-6), "projection");
    // соосное отверстие Ø6 на оси отверстия Ø4 в (10, 10) — с верхней грани насквозь
    var hs = DrawSketch(w, part, part.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, new Vector2d(10, 10), null)); c.Click(new SketchInput(sk, new Vector2d(12, 10), null)); });
    var cut = new ExtrudeFeature { SketchId = hs.Id, Cut = true }; cut.Side1.Distance = new ValueRef(10); part.AddFeature(cut); s.Document.RebuildDirty(); part.Rebuild(null);
    var cylFace = part.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 2) < 1e-9);
    top = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(cylFace.Id, PickKind.Face, part, null)); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    var hop = new AxialHoleOp(); w.StartOperation(hop);
    var hf = part.Features.OfType<HoleFeature>().Last(); hf.ThroughAll = true; hf.Diameter = new ValueRef(6);
    w.CommitOperation(); s.Document.RebuildDirty();
    Console.WriteLine($"  coaxial: error {hf.Error}, at {hf.Points.FirstOrDefault()}, dir {hf.Direction}, volume {part.Volume:F4}");
    Require(hf.Error == null && Math.Abs(part.Volume - (8000 - Math.PI * 9 * 10)) < 1e-4, "coaxial hole");
    // группа, родители и потомки
    TreeCommands.SetGroup(w, new Feature[] { cut, hf }, "Отверстия");
    var roots = TreeNode.Build(s.Document);
    var grp = roots[0].Children.FirstOrDefault(n => n.Item is InfoItem { Role: "group" });
    Require(grp != null && grp.Children.Count == 2, "group node");
    var redge = part.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 10) < 1e-6 && Math.Abs(x.V1.P.Z - 10) < 1e-6 && Math.Abs(x.V0.P.Y - 20) < 1e-9 && Math.Abs(x.V1.P.Y - 20) < 1e-9);
    var rnd = new RoundFeature { Radius = new ValueRef(2) }; rnd.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(redge)); part.AddFeature(rnd); s.Document.RebuildDirty();
    var par = TreeCommands.Parents(part, rnd);
    Console.WriteLine($"  parents of round: {string.Join(", ", par.Select(x => x.Name))}");
    Require(par.Contains(e) && TreeCommands.Children(part, e).Contains(rnd), "parents and children");
    part.RemoveFeature(rnd); s.Document.RebuildDirty();
    Require(TreeCommands.Parents(part, cut).Any(x => x is Sketch), "sketch parent");
    // сохранение и загрузка: опорные элементы и группа
    var cv = new DatumCurve("K", tp, "through points"); part.Add(cv);
    var json = Parametric3D.Model.DocumentFile.Save(part, s.Document.ScopeFor(part));
    var (p2, _) = Parametric3D.Model.DocumentFile.Load(json);
    Require(p2.Children.OfType<DatumCsys>().Count() == 1 && p2.Children.OfType<DatumCurve>().Single().Points.Count == tp.Count, "datums saved");
    Require(p2.Features.Count(f => f.Group == "Отверстия") == 2, "group saved");
    // замена эскиза выдавливания: прямоугольник 40×30
    var b2 = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 40, 30));
    TreeCommands.ReplaceSketch(w, e, b2); s.Document.RebuildDirty();
    Console.WriteLine($"  replace sketch: volume {part.Volume:F4}");
    Require(e.SketchId == b2.Id && Math.Abs(part.Volume - (12000 - Math.PI * 9 * 10)) < 1e-4, "replace sketch");
    w.Close();

    // радиальное отверстие Ø4 насквозь в цилиндре R10 высотой 40
    var s3 = DocumentSession.NewPart(); var w3 = Open(s3); var p3 = s3.Part!;
    var cs3 = DrawSketch(w3, p3, p3.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(10, 0), null)); });
    var ce = new ExtrudeFeature { SketchId = cs3.Id }; ce.Side1.Distance = new ValueRef(40); p3.AddFeature(ce); s3.Document.RebuildDirty(); p3.Rebuild(null);
    double v3 = p3.Volume;
    var side = p3.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 10) < 1e-9);
    var pt = DrawSketch(w3, p3, p3.PlaneXZ, sk => sk.AddEntity(new SkPoint(new Vector2d(10, 20))));
    var ptRef = new PickRef(pt.Entities.OfType<SkPoint>().First().Id, PickKind.SketchPoint, pt, pt.Entities.OfType<SkPoint>().First());
    w3.ViewPort.Selection.Clear(); Select(w3, new PickRef(side.Id, PickKind.Face, p3, null)); Select(w3, ptRef);
    var rop = new AxialHoleOp { Radial = true }; w3.StartOperation(rop);
    var rh = p3.Features.OfType<HoleFeature>().Last(); rh.ThroughAll = true; rh.Diameter = new ValueRef(4);
    w3.CommitOperation(); s3.Document.RebuildDirty();
    double removed = v3 - p3.Volume, approx = Math.PI * 4 * 20;
    Console.WriteLine($"  radial: error {rh.Error}, at {rh.Points.FirstOrDefault()}, dir {rh.Direction}, removed {removed:F3} (≈ {approx:F3})");
    Require(rh.Error == null && removed > 0.97 * approx && removed < approx, "radial hole");
    w3.Close();

    // полное скругление ребра 10 × 40 × 20: верхняя грань → полуцилиндр R5
    var s4 = DocumentSession.NewPart(); var w4 = Open(s4); var p4 = s4.Part!;
    var rs = DrawSketch(w4, p4, p4.PlaneXY, sk => Rect(sk, 0, 0, 10, 40));
    var re = new ExtrudeFeature { SketchId = rs.Id }; re.Side1.Distance = new ValueRef(20); p4.AddFeature(re); s4.Document.RebuildDirty(); p4.Rebuild(null);
    Vector3d FaceN(Vector3d n) { var f = p4.Bodies[0].Faces.First(x => Refs.FacePlane(p4, x.Id) is { } pl && (pl.N - n).Length < 1e-9); return Parametric3D.Model.Features.FaceRefs.Inside(f); }
    var fr = new FullRoundFeature(); fr.OnFaces.Add(FaceN(-Vector3d.UnitX)); fr.OnFaces.Add(FaceN(Vector3d.UnitZ)); fr.OnFaces.Add(FaceN(Vector3d.UnitX));
    p4.AddFeature(fr); s4.Document.RebuildDirty();
    double want = 8000 - 2000 + Math.PI * 25 / 2 * 40;
    Console.WriteLine($"  full round: error {fr.Error}, volume {p4.Volume:F4} want {want:F4}, faces {p4.Bodies.FirstOrDefault()?.Faces.Count()}");
    Require(fr.Error == null && Math.Abs(p4.Volume - want) < 1e-3 * want, "full round");
    w4.Close();
});
Vector3d V(OpenTK.Mathematics.Vector3 v) => new(v.X, v.Y, v.Z);

// u13f: поверхности (квилты): выдавливание, вращение, заливка, копия, смещение, обрезка, продление, объединение,
// утолщение, отверждение, замена грани; переходы граничный, с протяжкой и вращением; вставка тела
Check("UI-U13F-SURFACES", () =>
{
    var s = DocumentSession.NewPart(); var w = Open(s); var part = s.Part!;
    double QArea(string key) => Parametric3D.Model.Features.QuiltUtilT.Area(part.Quilts.First(q => q.Key == key));
    // поверхность выдавливанием незамкнутого отрезка 20 на 10 → 200; утолщение 2 → 20·10·2
    var ln = DrawSketch(w, part, part.PlaneXY, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, Vector2d.Zero, null)); l.Click(new SketchInput(sk, new Vector2d(20, 0), null)); });
    var se = new SurfaceFeature { SketchId = ln.Id, Depth = new ValueRef(10) }; part.AddFeature(se); s.Document.RebuildDirty();
    Console.WriteLine($"  surface extrude: error {se.Error}, quilts {part.Quilts.Count}, area {QArea(se.Key):F6}");
    Require(se.Error == null && Math.Abs(QArea(se.Key) - 200) < 1e-6, "surface extrude");
    Require(part.Mesh != null && part.Mesh.Indices.Length > 0, "quilt mesh shown");
    var th = new QuiltSolidFeature { Source = se.Key, Thickness = new ValueRef(2) }; part.AddFeature(th); s.Document.RebuildDirty();
    Console.WriteLine($"  thicken: error {th.Error}, volume {part.Volume:F6}");
    Require(th.Error == null && Math.Abs(part.Volume - 400) < 1e-6, "thicken");
    part.RemoveFeature(th); s.Document.RebuildDirty();
    // продление на 5 — площадь больше
    var ex = new QuiltEditFeature { Mode = QuiltEditKind.Extend, Distance = new ValueRef(5) }; ex.Sources.Add(se.Key); part.AddFeature(ex); s.Document.RebuildDirty();
    Console.WriteLine($"  extend: error {ex.Error}, area {(ex.Error == null ? QArea(ex.Key) : 0):F4}");
    Require(ex.Error == null && QArea(ex.Key) > 200 + 2 * 5 * 20 - 1e-6, "extend");
    part.RemoveFeature(ex); part.RemoveFeature(se); s.Document.RebuildDirty();

    // заливка 20×10 → утолщение 3 = 600; обрезка по x = 5 → 50 (другая сторона — 150)
    var fs = DrawSketch(w, part, part.PlaneXY, sk => Rect(sk, 0, 0, 20, 10));
    var fill = new SurfaceFeature { Mode = SurfaceKind.Fill, SketchId = fs.Id }; part.AddFeature(fill); s.Document.RebuildDirty();
    if (Environment.GetEnvironmentVariable("P3D_SAVE_SURF") is { } sp) File.WriteAllText(sp, Parametric3D.Model.DocumentFile.Save(part, s.Document.ScopeFor(part)));
    Require(fill.Error == null && Math.Abs(QArea(fill.Key) - 200) < 1e-6, $"fill {fill.Error}");
    var tr = new QuiltEditFeature { Mode = QuiltEditKind.Trim, PlaneAt = new Vector3d(5, 0, 0), PlaneNormal = Vector3d.UnitX }; tr.Sources.Add(fill.Key); part.AddFeature(tr); s.Document.RebuildDirty();
    Console.WriteLine($"  trim: error {tr.Error}, area {(tr.Error == null ? QArea(tr.Key) : 0):F6}");
    Require(tr.Error == null && Math.Abs(QArea(tr.Key) - 50) < 1e-6, "trim");
    var th2 = new QuiltSolidFeature { Source = tr.Key, Thickness = new ValueRef(3) }; part.AddFeature(th2); s.Document.RebuildDirty();
    Console.WriteLine($"  thicken fill: error {th2.Error}, volume {part.Volume:F6}");
    Require(th2.Error == null && Math.Abs(part.Volume - 150) < 1e-6, "thicken trimmed fill");
    part.RemoveFeature(th2); part.RemoveFeature(tr); part.RemoveFeature(fill); s.Document.RebuildDirty();

    // поверхность вращением: образующая x = 10 высотой 20 вокруг оси → цилиндр, площадь 2π·10·20; утолщение 2 внутрь
    var rv = DrawSketch(w, part, part.PlaneXZ, sk =>
    {
        var l = new LineTool(chain: false); l.Click(new SketchInput(sk, new Vector2d(10, 0), null)); l.Click(new SketchInput(sk, new Vector2d(10, 20), null));
        var c = new LineTool(chain: false); c.Click(new SketchInput(sk, new Vector2d(0, -5), null)); c.Click(new SketchInput(sk, new Vector2d(0, 25), null));
        sk.Entities.OfType<SkLine>().Last().Role = SkRole.Centerline;
    });
    var sr = new SurfaceFeature { Mode = SurfaceKind.Revolve, SketchId = rv.Id }; part.AddFeature(sr); s.Document.RebuildDirty();
    Console.WriteLine($"  surface revolve: error {sr.Error}, area {(sr.Error == null ? QArea(sr.Key) : 0):F4}");
    Require(sr.Error == null && Math.Abs(QArea(sr.Key) - 2 * Math.PI * 200) < 1e-3 * 1256, "surface revolve");
    var th3 = new QuiltSolidFeature { Source = sr.Key, Thickness = new ValueRef(2) }; part.AddFeature(th3); s.Document.RebuildDirty();
    Console.WriteLine($"  thicken cylinder: error {th3.Error}, volume {part.Volume:F4} want {Math.PI * 36 * 20:F4}; parent faces {string.Join(",", part.Quilts.FirstOrDefault()?.Parent.Faces.Select(f => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name) ?? new string[0])}; {string.Join(" | ", part.Warnings.TakeLast(4))}");
    Require(th3.Error == null && Math.Abs(part.Volume - Math.PI * 36 * 20) < 1e-6 * 2262, "thicken cylinder");
    part.RemoveFeature(th3); part.RemoveFeature(sr); s.Document.RebuildDirty();
    w.Close();

    // брусок: копия верхней грани и смещение на 5; замена верхней грани заливкой на высоте 15
    var s2 = DocumentSession.NewPart(); var w2 = Open(s2); var p2 = s2.Part!;
    var b = DrawSketch(w2, p2, p2.PlaneXY, sk => Rect(sk, 0, 0, 40, 20));
    var e = new ExtrudeFeature { SketchId = b.Id }; e.Side1.Distance = new ValueRef(10); p2.AddFeature(e); s2.Document.RebuildDirty(); p2.Rebuild(null);
    var top = p2.Bodies[0].Faces.First(f => Refs.FacePlane(p2, f.Id) is { } pl && pl.N.Z > 0.99);
    var cp = new SurfaceFeature { Mode = SurfaceKind.Copy }; cp.OnFaces.Add(Parametric3D.Model.Features.FaceRefs.Inside(top)); p2.AddFeature(cp);
    var of = new QuiltEditFeature { Mode = QuiltEditKind.Offset, Distance = new ValueRef(5) }; of.Sources.Add(cp.Key); p2.AddFeature(of); s2.Document.RebuildDirty();
    var oq = p2.Quilts.First(q => q.Key == of.Key);
    var oz = oq.Faces.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Select(c => c.Start.P.Z).ToList();
    Console.WriteLine($"  offset copy: error {cp.Error}/{of.Error}, area {Parametric3D.Model.Features.QuiltUtilT.Area(oq):F4}, z {oz.Min():F4}..{oz.Max():F4}");
    Require(cp.Error == null && of.Error == null && Math.Abs(Parametric3D.Model.Features.QuiltUtilT.Area(oq) - 800) < 1e-6 && oz.All(z => Math.Abs(z - 15) < 1e-9), "offset surface");
    p2.RemoveFeature(of); p2.RemoveFeature(cp);
    var pl15 = new DatumPlane("P15", Parametric3D.Kernel.Frame3.XY.Translated(new Vector3d(0, 0, 15))); p2.Add(pl15);
    var big = DrawSketch(w2, p2, pl15, sk => Rect(sk, -10, -10, 50, 30));
    var f15 = new SurfaceFeature { Mode = SurfaceKind.Fill, SketchId = big.Id }; p2.AddFeature(f15); s2.Document.RebuildDirty(); p2.Rebuild(null);
    top = p2.Bodies[0].Faces.First(f => Refs.FacePlane(p2, f.Id) is { } pl && pl.N.Z > 0.99 && Math.Abs(pl.P.Z - 10) < 1e-9);
    var rp = new QuiltSolidFeature { Mode = QuiltSolidKind.ReplaceFace, Source = f15.Key }; rp.OnFaces.Add(Parametric3D.Model.Features.FaceRefs.Inside(top)); p2.AddFeature(rp); s2.Document.RebuildDirty();
    Console.WriteLine($"  replace face: error {rp.Error}, volume {p2.Volume:F6}; {string.Join(" | ", p2.Warnings.Where(x => x.Contains("dbg")))}");
    Require(rp.Error == null && Math.Abs(p2.Volume - 12000) < 1e-6, "replace face");
    // вставка тела: копия бруска со сдвигом по X на 100 — объём удваивается
    var step = Parametric3D.Model.Exchange.Write(p2, Parametric3D.Model.Exchange.Format.Step);
    var ib = new InsertBodyFeature { Step = step, SourceName = "box", Offset = new Vector3d(100, 0, 0) }; p2.AddFeature(ib); s2.Document.RebuildDirty();
    Console.WriteLine($"  insert body: error {ib.Error}, volume {p2.Volume:F4}");
    Require(ib.Error == null && Math.Abs(p2.Volume - 24000) < 1e-3, "insert body");
    var json = Parametric3D.Model.DocumentFile.Save(p2, s2.Document.ScopeFor(p2));
    var (p3, _) = Parametric3D.Model.DocumentFile.Load(json);
    Require(p3.Features.OfType<QuiltSolidFeature>().Count() == 1 && p3.Features.OfType<InsertBodyFeature>().Single().Step.Length == step.Length, "saved");
    w2.Close();

    // переходы
    var s4 = DocumentSession.NewPart(); var w4 = Open(s4); var p4 = s4.Part!;
    var a = DrawSketch(w4, p4, p4.PlaneXY, sk => Rect(sk, -10, -10, 10, 10));
    var p40 = new DatumPlane("P40", Parametric3D.Kernel.Frame3.FromNormal(new Vector3d(0, 0, 40), Vector3d.UnitZ)); p4.Add(p40);
    var b4 = DrawSketch(w4, p4, p40, sk => Rect(sk, -5, -5, 5, 5));
    var bb = new BlendFeature { Mode = BlendKind.Boundary }; bb.SectionSketchIds.Add(a.Id); bb.SectionSketchIds.Add(b4.Id); p4.AddFeature(bb); s4.Document.RebuildDirty();
    Console.WriteLine($"  boundary blend: error {bb.Error}, volume {p4.Volume:F4}");
    Require(bb.Error == null && Math.Abs(p4.Volume - 40.0 / 3 * 700) < 1e-6 * 9333, "boundary blend");
    p4.RemoveFeature(bb); s4.Document.RebuildDirty();
    // с протяжкой: круги r5 и r2 на концах прямой траектории 40 → усечённый конус
    var c0 = DrawSketch(w4, p4, p4.PlaneXY, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(5, 0), null)); });
    var c1 = DrawSketch(w4, p4, p40, sk => { var c = new CircleTool(); c.Click(new SketchInput(sk, Vector2d.Zero, null)); c.Click(new SketchInput(sk, new Vector2d(2, 0), null)); });
    var path = DrawSketch(w4, p4, p4.PlaneXZ, sk => { var l = new LineTool(chain: false); l.Click(new SketchInput(sk, Vector2d.Zero, null)); l.Click(new SketchInput(sk, new Vector2d(0, 40), null)); });
    var sb = new BlendFeature { Mode = BlendKind.Swept, PathSketchId = path.Id }; sb.SectionSketchIds.Add(c0.Id); sb.SectionSketchIds.Add(c1.Id); p4.AddFeature(sb); s4.Document.RebuildDirty();
    double cone = Math.PI / 3 * 40 * (25 + 10 + 4);
    Console.WriteLine($"  swept blend: error {sb.Error}, volume {p4.Volume:F4} want {cone:F4}");
    Require(sb.Error == null && Math.Abs(p4.Volume - cone) < 0.01 * cone, "swept blend");
    p4.RemoveFeature(sb); s4.Document.RebuildDirty();
    // вращением: квадраты 5×5 на радиусе 20–25 в плоскостях XZ и YZ (90°) — по Паппу 25·(π/2)·22.5
    var q0 = DrawSketch(w4, p4, p4.PlaneXZ, sk => { Rect(sk, 20, 0, 25, 5); var l = new LineTool(chain: false); l.Click(new SketchInput(sk, new Vector2d(0, -5), null)); l.Click(new SketchInput(sk, new Vector2d(0, 10), null)); sk.Entities.OfType<SkLine>().Last().Role = SkRole.Centerline; });
    var q1 = DrawSketch(w4, p4, p4.PlaneYZ, sk => Rect(sk, 20, 0, 25, 5));
    var rb = new BlendFeature { Mode = BlendKind.Rotational }; rb.SectionSketchIds.Add(q0.Id); rb.SectionSketchIds.Add(q1.Id); p4.AddFeature(rb); s4.Document.RebuildDirty();
    double pap = 25 * Math.PI / 2 * 22.5;
    Console.WriteLine($"  rotational blend: error {rb.Error}, volume {p4.Volume:F4} want {pap:F4}");
    Require(rb.Error == null && Math.Abs(p4.Volume - pap) < 0.02 * pap, "rotational blend");
    w4.Close();
});

// u13g: сборка — компоненты, ограничения, соединения, массивы, зеркало, спецификация, разнесённый вид, файл, отмена
Part CubePart(string name, double side) { var p = new Part(name); TestGeometry.AddCube(p, side); return p; }
Part CylPart(string name, double r, double h)
{
    var p = new Part(name);
    var sk = new Sketch("S", p.PlaneXY.Frame); sk.SetBasePlane(p.PlaneXY.Frame, p.PlaneXY.Id, "XY"); p.Add(sk);
    sk.AddEntity(new SkCircle(new SkPoint(0, 0), r));
    var e = new ExtrudeFeature("Cyl") { SketchId = sk.Id }; e.Side1.Distance = new ValueRef(h); p.AddFeature(e);
    return p;
}
Check("UI-U13G-ASSEMBLY", () =>
{
    var s = DocumentSession.NewAssembly(); var w = Open(s);
    Require(s.IsAssembly && s.Part == null, "assembly mode");
    var a = AssemblyCommands.AddComponent(w, CubePart("Основание", 40), "add A");
    var b = AssemblyCommands.AddComponent(w, CubePart("Кубик", 20), "add B");
    Require(a.Fixed && !b.Fixed && b.Translation.X > 20, $"placement {b.Translation}");
    Vector3d Wc(Part p) => AssemblyCommands.ToWorld(p, (p.Bounds.Min + p.Bounds.Max) / 2);
    PickRef Face(Part p, Vector3d n) { var f = p.Bodies[0].Faces.First(x => Refs.FacePlane(p, x.Id) is { } pl && (pl.N - n).Length < 1e-9); return new PickRef(f.Id, PickKind.Face, p, null); }
    // совпадение: низ кубика на верх основания; затем две боковые и вторая пара — кубик встаёт в угол
    w.ViewPort.Selection.Clear(); Select(w, Face(b, -Vector3d.UnitZ)); Select(w, Face(a, Vector3d.UnitZ));
    AssemblyCommands.Constrain(w, AssemblyCommands.MateCmd.Coincident);
    w.ViewPort.Selection.Clear(); Select(w, Face(b, -Vector3d.UnitX)); Select(w, Face(a, -Vector3d.UnitX));
    var plan = AssemblyCommands.Plan(AssemblyCommands.MateCmd.Distance, AssemblyCommands.GeoOf(Face(b, -Vector3d.UnitX))!.Value, AssemblyCommands.GeoOf(Face(a, -Vector3d.UnitX))!.Value, 0, out var why);
    // выравнивание боковых: плоскости одного направления — «перевернуть» совпадение
    var mate = AssemblyCommands.Plan(AssemblyCommands.MateCmd.Coincident, AssemblyCommands.GeoOf(Face(b, -Vector3d.UnitX))!.Value, AssemblyCommands.GeoOf(Face(a, -Vector3d.UnitX))!.Value, 0, out why)!;
    mate[0].C.Kind = Parametric3D.Model.Mates.ConstraintKind.Align;
    var mate2 = AssemblyCommands.Plan(AssemblyCommands.MateCmd.Coincident, AssemblyCommands.GeoOf(Face(b, -Vector3d.UnitY))!.Value, AssemblyCommands.GeoOf(Face(a, -Vector3d.UnitY))!.Value, 0, out why)!;
    mate2[0].C.Kind = Parametric3D.Model.Mates.ConstraintKind.Align;
    AssemblyCommands.Apply(w, mate.Concat(mate2).ToList(), "align");
    var bc = Wc(b);
    Console.WriteLine($"  cube placed: centre {bc}, constraints {b.Constraints.Count}");
    Require((bc - new Vector3d(-10, -10, 30)).Length < 1e-3, $"cube in the corner {bc}");
    // отмена последнего шага — ограничений два → одно
    s.Undo(); var b1 = s.Document.Root.Children.OfType<Part>().First(p => p.Name == "Кубик");
    Require(b1.Constraints.Count == 1, $"undo constraints {b1.Constraints.Count}");
    s.Redo(); b = s.Document.Root.Children.OfType<Part>().First(p => p.Name == "Кубик"); a = s.Document.Root.Children.OfType<Part>().First(p => p.Name == "Основание");
    Require(b.Constraints.Count == 3, "redo");
    // соединение «шарнир»: вал R5 в цилиндр R5 (ось к оси)
    var shaft = AssemblyCommands.AddComponent(w, CylPart("Вал", 5, 30), "add shaft");
    var hub = AssemblyCommands.AddComponent(w, CylPart("Втулка", 5, 10), "add hub");
    s.Document.RebuildDirty();
    PickRef Side(Part p) { var f = p.Bodies[0].Faces.First(x => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(x, 0) is { } rr && Math.Abs(rr - 5) < 1e-9); return new PickRef(f.Id, PickKind.Face, p, null); }
    shaft.Fixed = true;
    w.ViewPort.Selection.Clear(); Select(w, Side(hub)); Select(w, Side(shaft));
    AssemblyCommands.Joint(w, Parametric3D.Model.Mates.MateType.Pin);
    var hc = AssemblyCommands.ToWorld(hub, Vector3d.Zero); var sc = AssemblyCommands.ToWorld(shaft, Vector3d.Zero);
    var hz = AssemblyCommands.DirToWorld(hub, Vector3d.UnitZ);
    Console.WriteLine($"  pin: hub origin {hc}, shaft origin {sc}, hub axis {hz}");
    Require(hub.Mates.Count == 1 && Math.Abs(Math.Abs(hz.Z) - 1) < 1e-4 && Math.Abs(hc.X - sc.X) < 1e-3 && Math.Abs(hc.Y - sc.Y) < 1e-3, "pin joint");
    // массив кубика: 3 шт. с шагом 30 по Y; спецификация: «Кубик» ×3
    var copies = AssemblyCommands.Pattern(w, b, 3, new Vector3d(0, 30, 0));
    Require(copies.Count == 2 && Math.Abs(copies[1].Translation.Y - b.Translation.Y - 60) < 1e-3, "pattern");
    var bom = AssemblyCommands.Bom(w);
    Console.WriteLine("  bom: " + string.Join("; ", bom.Select(r => $"{r.Name} x{r.Count}")));
    Require(bom.Any(r => r.Name == "Кубик" && r.Count == 3) && bom.Any(r => r.Name == "Основание" && r.Count == 1), "bom");
    // зеркало кубика относительно плоскости YZ сборки: объём тот же, центр по X — с другой стороны
    var m = AssemblyCommands.Mirror(w, b, Vector3d.Zero, Vector3d.UnitX); s.Document.RebuildDirty();
    var mc = Wc(m); bc = Wc(b);
    Console.WriteLine($"  mirror: centre {mc} (source {bc}), volume {m.Volume:F3}");
    Require(Math.Abs(mc.X + bc.X) < 1e-3 && Math.Abs(m.Volume - 8000) < 1e-3, "mirror component");
    // разнесённый вид: туда и обратно
    var t0 = b.Translation;
    AssemblyCommands.ToggleExplode(w); Require((b.Translation - t0).Length > 1, "exploded");
    AssemblyCommands.ToggleExplode(w); Require((b.Translation - t0).Length < 1e-5, "collapsed");
    // запись и чтение сборки
    var path = Path.Combine(Path.GetTempPath(), "u13g_test.p3a");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path);
    var parts2 = s2.Document.Root.Children.OfType<Part>().ToList();
    Console.WriteLine($"  reopened: {parts2.Count} components, fixed {parts2.Count(p => p.Fixed)}, constraints {parts2.Sum(p => p.Constraints.Count)}, joints {parts2.Sum(p => p.Mates.Count)}");
    Require(s2.IsAssembly && parts2.Count == s.Document.Root.Children.OfType<Part>().Count() && parts2.Sum(p => p.Constraints.Count) == s.Document.Root.Children.OfType<Part>().Sum(p => p.Constraints.Count) && parts2.Sum(p => p.Mates.Count) == 1, "assembly file");
    // правка в контексте: операции — над компонентом
    AssemblyCommands.EditInContext(w, b);
    Require(s.Part == b && w.ViewPort.ActivePart == b, "edit in context");
    AssemblyCommands.EditInContext(w, null);
    Require(s.Part == null, "back to assembly");
    // замена компонента: ограничения переходят к новому
    var nb = CubePart("Кубик2", 20);
    AssemblyCommands.Replace(w, b, nb);
    Require(nb.Constraints.Count == 3 && !s.Document.Root.Children.Contains(b), "replace keeps constraints");
    // дерево: компоненты и папка ограничений
    var roots = TreeNode.Build(s.Document, assembly: true);
    Require(roots[0].Children.Any(n => n.Item is InfoItem { Role: "mates" } mm && n.Children.Count >= 4) && roots[0].Children.Count(n => n.Item is Part) >= 5, "assembly tree");
    w.Close();
});

// u13h: FreeForm — документ, заготовки, булевы между телами, перенос/масштаб, разрез, зеркало и симметрия, массивы, шаги
// на своём теле (скругление, push-pull, выравнивание), фиксация шагов, «отменить до шага», запись, «Править в FreeForm»
// с автоматическим объединением и «ссылки изменились».
Check("UI-U13H-FREEFORM", () =>
{
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    Require(part.FreeFormDoc && w.InFreeForm, "freeform document opens in the FreeForm environment");
    Require(CommandRegistry.Instance["ff.box"].Enabled && !CommandRegistry.Instance["op.edit_in_freeform"].Enabled, "freeform commands");
    var node = part.Features.OfType<FreeFormFeature>().Single();
    // брусок 40×30×20 командой (панель), «ОК» — шаг узла, зафиксирован
    CommandRegistry.Instance["ff.box"].Execute();
    Require(w.CurrentOperation is FFPrimitiveOp, "box op");
    w.CommitOperation(); s.Document.RebuildDirty();
    var box = part.Features.OfType<FFPrimitiveFeature>().Single();
    Console.WriteLine($"  box: error {box.Error}, volume {part.Volume:F6}, bodies {string.Join(",", part.BodyNameList)}, frozen {box.Frozen}");
    Require(box.FreeForm == node.Key && box.Frozen && box.Error == null && Math.Abs(part.Volume - 24000) < 1e-6, "box step");
    // правка зафиксированного шага не открывается
    w.ViewPort.Selection.Clear(); w.ViewPort.Selection.Select(new PickRef(box.Id, PickKind.Feature, part, box), additive: false);
    w.EditSelected();
    Require(w.CurrentOperation == null, "frozen step is not editable");
    T Add<T>(T f) where T : Feature { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Require(f.Error == null, $"{f.GetType().Name}: {f.Error}"); return f; }
    // цилиндр R10 H40 — второе тело (FreeForm-документ тела не объединяет)
    var cyl = Add(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(10), B = new ValueRef(40) });
    double vc = Math.PI * 100 * 40;
    Console.WriteLine($"  cylinder: bodies {part.Bodies.Count} ({string.Join(",", part.BodyNameList)}), volume {part.Volume:F6}");
    Require(part.Bodies.Count == 2 && part.BodyNameList.SequenceEqual(new[] { "Тело 1", "Тело 2" }) && Math.Abs(part.Volume - (24000 + vc)) < 1e-6 * vc, "two bodies");
    // вычесть: Тело 1 − Тело 2 → отверстие R10 сквозь брусок (высота 20)
    var sub = Add(new FFBooleanFeature { Op = FFBool.Subtract, Target = "Тело 1", Tools = { "Тело 2" } });
    double v1 = 24000 - Math.PI * 100 * 20;
    Console.WriteLine($"  subtract: bodies {part.Bodies.Count}, volume {part.Volume:F6} want {v1:F6}");
    Require(part.Bodies.Count == 1 && Math.Abs(part.Volume - v1) < 1e-6 * v1, "subtract");
    // копия переносом на 100 по X и масштаб копии ×0.5 → объём копии /8
    var mv = Add(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(100), Copy = true });
    // имена не переиспользуются: копия — «Тело 3» («Тело 2» был цилиндр)
    var sc = Add(new FFTransformFeature { Mode = FFXform.Scale, BodyNames = { "Тело 3" }, Factor = new ValueRef(0.5), AxisAt = new Vector3d(100, 0, 0) });
    Console.WriteLine($"  copy + scale: bodies {string.Join(",", part.BodyNameList)}, volume {part.Volume:F6}");
    Require(part.Bodies.Count == 2 && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "copy and exact scale");
    // разрез тела 1 плоскостью z = 5 → два тела, объём тот же
    var sp = Add(new FFSplitFeature { Body = "Тело 1", PlaneAt = new Vector3d(0, 0, 5), Normal = Vector3d.UnitZ });
    Console.WriteLine($"  split: bodies {string.Join(",", part.BodyNameList)}, volume {part.Volume:F6}");
    Require(part.Bodies.Count == 3 && part.BodyNameList.Contains("Тело 1.2") && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "split");
    // объединить части обратно
    var un = Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 1.2" } });
    Require(part.Bodies.Count == 2 && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "union back");
    // зеркальная копия тела 3 через плоскость x = 0 и прямоугольный массив тела 1 (3 шт. по Y с шагом 50)
    var mi = Add(new FFMirrorFeature { Mode = FFMirrorMode.Copy, BodyNames = { "Тело 3" }, PlaneAt = Vector3d.Zero, Normal = Vector3d.UnitX });
    var ar = Add(new FFArrayFeature { Kind = FFArrayKind.Linear, BodyNames = { "Тело 1" }, Count = new ValueRef(3), Step = new Vector3d(0, 50, 0) });
    Console.WriteLine($"  mirror + array: bodies {part.Bodies.Count} ({string.Join(",", part.BodyNameList)}), volume {part.Volume:F3}");
    Require(part.Bodies.Count == 5 && Math.Abs(part.Volume - v1 * (3 + 0.25)) < 1e-6 * v1, "mirror and array");
    // скругление ребра тела «Тело 1 [2]» (шаг на своём теле): ребро бруска длиной 40 вдоль X, R3
    int bi = part.BodyNameList.IndexOf("Тело 1 [2]");
    var edge = part.Bodies[bi].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-6 && Math.Abs(e.V1.P.Z - 20) < 1e-6 && Math.Abs(Math.Abs(e.V0.P.X - e.V1.P.X) - 40) < 1e-6 && Math.Abs(e.V0.P.Y - 65) < 1e-6);
    var rf = new RoundFeature("R") { Radius = new ValueRef(3), FFBody = "Тело 1 [2]" }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge));
    double before = part.Volume;
    Add(rf);
    double cut = (1 - Math.PI / 4) * 9 * 40;
    Console.WriteLine($"  fillet on one body: volume {part.Volume:F4}, removed {before - part.Volume:F4} want {cut:F4}, bodies {part.Bodies.Count}");
    Require(part.Bodies.Count == 5 && Math.Abs(before - part.Volume - cut) < 1e-3, "fillet in its own body");
    // push-pull: верх тела «Тело 1 [3]» +5 (брусок с отверстием: + 5 × (1200 − 100π))
    int b3 = part.BodyNameList.IndexOf("Тело 1 [3]");
    var top = part.Bodies[b3].Faces.First(f => Parametric3D.Kernel.Ops.PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.99);
    var pp = new FaceEditFeature { Mode = FaceEditMode.Offset, Distance = new ValueRef(5), FFBody = "Тело 1 [3]" };
    pp.OnFaces.Add(Refs.FacePoint(part, top.Id)!.Value);
    before = part.Volume; Add(pp);
    double grow = 5 * (1200 - Math.PI * 100);
    Console.WriteLine($"  push-pull: added {part.Volume - before:F4} want {grow:F4}");
    Require(Math.Abs(part.Volume - before - grow) < 1e-3, "push-pull");
    // выровнять: верх того же тела — к плоскости z = 30 (было 25)
    b3 = part.BodyNameList.IndexOf("Тело 1 [3]");
    top = part.Bodies[b3].Faces.First(f => Parametric3D.Kernel.Ops.PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.99);
    var al = new FFAlignFaceFeature { PlaneAt = new Vector3d(0, 0, 30), Normal = Vector3d.UnitZ, FFBody = "Тело 1 [3]" };
    al.OnFaces.Add(Refs.FacePoint(part, top.Id)!.Value);
    before = part.Volume; Add(al);
    Console.WriteLine($"  align: added {part.Volume - before:F4} want {grow:F4}");
    Require(Math.Abs(part.Volume - before - grow) < 1e-3, "align face");
    // дерево: узел FreeForm со своим журналом шагов
    var roots = TreeNode.Build(s.Document);
    var ffNode = roots[0].SelfAndDescendants().First(n => n.Item is FreeFormFeature);
    Console.WriteLine($"  tree: freeform node with {ffNode.Children.Count} steps, icon {ffNode.IconFile}");
    Require(ffNode.Children.Count == part.Features.Count - 1 && ffNode.IconFile == "tree_freeform_ico.svg" && ffNode.Children[0].IconFile == "tree_ff_step_ico.svg", "freeform tree");
    // запись и чтение: те же тела и признаки шагов
    double vAll = part.Volume;
    var path = Path.Combine(Path.GetTempPath(), "u13h_ff.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty(); var p2 = s2.Part!;
    Console.WriteLine($"  reopened: freeform {p2.FreeFormDoc}, steps {p2.Features.Count}, volume {p2.Volume:F4}, bodies {p2.Bodies.Count}");
    Require(p2.FreeFormDoc && p2.Features.Count == part.Features.Count && p2.Features.Skip(1).All(f => f.FreeForm == node.Key && f.Frozen)
            && p2.Features.OfType<RoundFeature>().Single().FFBody == "Тело 1 [2]" && Math.Abs(p2.Volume - vAll) < 1e-6 * vAll && p2.Bodies.Count == 5, "save and load");
    // «отменить до шага» (зеркало): зеркало и всё после него убираются одним шагом отмены
    FreeFormCommands.UndoTo(w, mi); s.Document.RebuildDirty();
    Console.WriteLine($"  undo to mirror: steps {part.Features.Count}, bodies {part.Bodies.Count}");
    Require(!part.Features.Contains(mi) && !part.Features.Contains(al) && part.Bodies.Count == 2 && Math.Abs(part.Volume - v1 * 1.125) < 1e-6 * v1, "undo to step");
    s.Undo(); s.Document.RebuildDirty();
    Require(s.Part!.Features.Count == p2.Features.Count, "undo of undo-to");
    w.Close();

    // «Править в FreeForm» параметрической детали: куб 40; шар R10 в углу — после «Готово» тела объединяются
    var s3 = DocumentSession.NewPart(); var w3 = Open(s3); var p3 = s3.Part!;
    var sk = DrawSketch(w3, p3, p3.PlaneXY, k => Rect(k, 0, 0, 40, 40));
    var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(40); p3.AddFeature(ex); s3.Document.RebuildDirty();
    FreeFormCommands.EditInFreeForm(w3);
    Require(w3.InFreeForm && p3.Features.Last() is FreeFormFeature { InputSig.Length: > 0 }, "edit in freeform");
    var ffn = (FreeFormFeature)p3.Features.Last();
    var sph = new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(10), Origin = new Vector3d(40, 40, 40), FreeForm = ffn.Key, Frozen = true };
    p3.AddFeature(sph); s3.Document.RebuildDirty();
    Console.WriteLine($"  in freeform: bodies {p3.Bodies.Count} ({string.Join(",", p3.BodyNameList)}), volume {p3.Volume:F3}");
    Require(p3.Bodies.Count == 2 && p3.BodyNameList[0] == "Исходное тело", "separate while editing");
    FreeFormCommands.Exit(w3); s3.Document.RebuildDirty();
    double vu = 64000 + 4.0 / 3 * Math.PI * 1000 * 7 / 8;
    Console.WriteLine($"  done: bodies {p3.Bodies.Count}, volume {p3.Volume:F3} want {vu:F3}");
    Require(!w3.InFreeForm && p3.Bodies.Count == 1 && Math.Abs(p3.Volume - vu) < 1e-3, "auto union on embedding");
    // тело выше в дереве изменилось: шаги не перестраиваются, узел — «ссылки изменились»; «Обновить ссылки» — по новой форме
    ex.Side1.Distance = new ValueRef(60); p3.NotifyChanged(ex); s3.Document.RebuildDirty();
    Console.WriteLine($"  upstream changed: refs changed {ffn.RefsChanged}, volume {p3.Volume:F3}");
    Require(ffn.RefsChanged && Math.Abs(p3.Volume - vu) < 1e-2, "references changed — stored input");
    Require(TreeNode.StateOf(ffn, p3).File == "state_refs_changed_ico.svg", "refs changed state");
    w3.ViewPort.Selection.Clear(); w3.ViewPort.Selection.Select(new PickRef(ffn.Id, PickKind.Feature, p3, ffn), additive: false);
    FreeFormCommands.UpdateRefs(w3, ffn); s3.Document.RebuildDirty();
    // брусок 40×40×60, центр шара (40,40,40) на вертикальном ребре: внутри — четверть шара
    double vn = 96000 + 4.0 / 3 * Math.PI * 1000 * 3 / 4;
    Console.WriteLine($"  update refs: refs changed {ffn.RefsChanged}, volume {p3.Volume:F3} want {vn:F3}");
    Require(!ffn.RefsChanged && Math.Abs(p3.Volume - vn) < 1e-2, "update references");
    // предложенное исправление: отказ операции — варианты
    var sug = FreeFormCommands.Suggestions(new RoundFeature("x") { Error = "Round: the radius 500 is too large" });
    Require(sug.Any(x => x.Command == "edit.feature") && sug.Any(x => x.Command == "edit.suppress"), "fix suggestions");
    var lost = FreeFormCommands.Suggestions(new RoundFeature("y") { Error = "Round: the edge no longer exists" });
    Require(lost[0].Command == "tree.replace_refs", "lost reference suggestion");
    w3.Close();
});

// u13i: кривые FreeForm и тела из кривых — интерполяция и перестроение NURBS, спираль, эквидистанта, скругление углов,
// соединение, продление, мостик, обрезка, изопараметрическая, вставка узла и вес, труба, отпечаток, команды и запись.
Check("UI-U13I-CURVES", () =>
{
    double Len(IReadOnlyList<Vector3d> q) { double l = 0; for (int i = 1; i < q.Count; i++) l += (q[i] - q[i - 1]).Length; return l; }
    // интерполяция: кривая проходит точки; перестроение окружности 8 опорными — отклонение < 0.5 % радиуса
    var pts = new List<Vector3d> { new(0, 0, 0), new(10, 5, 0), new(20, -3, 2), new(30, 4, 0), new(40, 0, 5) };
    var ip = FFCurves.Interpolate(pts, 3);
    var ipPts = FFCurves.Sample(ip, 400);
    double dev = FFCurves.Deviation(pts, ipPts);
    Console.WriteLine($"  interpolate: control points {ip.ControlPoints.Count}, deviation {dev:E2}");
    Require(dev < 1e-3 && ip.ControlPoints.Count == pts.Count, "interpolation through points");
    var circle = Enumerable.Range(0, 201).Select(i => new Vector3d(20 * Math.Cos(i * Math.PI / 100), 20 * Math.Sin(i * Math.PI / 100), 0)).ToList();
    var half = circle.Take(101).ToList();
    var fit = FFCurves.Fit(half, 8, 3);
    double fdev = FFCurves.Deviation(half, FFCurves.Sample(fit, 400));
    Console.WriteLine($"  rebuild half circle R20 with 8 control points: deviation {fdev:E2}");
    Require(fdev < 0.1 && fit.ControlPoints.Count == 8 && (fit.PointAt(0) - half[0]).Length < 1e-9 && (fit.PointAt(1) - half[^1]).Length < 1e-9, "rebuild");
    // вставка узла не меняет форму; вес точки — меняет
    var ins = fit.WithUnitKnots().InsertKnot(0.37);
    double kdev = FFCurves.Deviation(FFCurves.Sample(fit, 200), FFCurves.Sample(ins, 400));
    Require(ins.ControlPoints.Count == 9 && kdev < 1e-6, $"insert knot keeps the shape ({kdev})");
    // спираль: 3 витка R20, шаг 10 — высота 30, радиус 20
    var sp = FFCurves.Spiral(Parametric3D.Kernel.Frame3.XY, 20, 20, 10, 3);
    Require(Math.Abs(sp[^1].Z - 30) < 1e-9 && sp.All(p => Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 20) < 1e-9), "spiral");
    // эквидистанта квадрата 20 наружу на 2 → периметр 96; скругление углов R2 → 4·16 + 4π
    var sq = new List<Vector3d> { new(0, 0, 0), new(20, 0, 0), new(20, 20, 0), new(0, 20, 0), new(0, 0, 0) };
    var off = FFCurves.Offset(sq, Vector3d.UnitZ, -2);
    Console.WriteLine($"  offset square: perimeter {Len(off):F6}");
    Require(Math.Abs(Len(off) - 96) < 1e-9, "offset");
    var fil = FFCurves.FilletCorners(sq, 2, out int nk, 64);
    Console.WriteLine($"  fillet corners: {nk}, length {Len(fil):F4} want {64 + 4 * Math.PI:F4}");
    Require(nk == 4 && Math.Abs(Len(fil) - (64 + 4 * Math.PI)) < 0.01, "curve fillet");
    // соединение, продление, мостик, обрезка
    var j = FFCurves.Join(new List<IReadOnlyList<Vector3d>> { new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, new[] { new Vector3d(20, 5, 0), new Vector3d(10, 0, 0) } }, 1e-6);
    Require(j.Count == 3 && (j[^1] - new Vector3d(20, 5, 0)).Length < 1e-12, "join");
    var ex = FFCurves.Extend(new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, 5, false, true, false);
    Require((ex[^1] - new Vector3d(15, 0, 0)).Length < 1e-12, "extend line");
    var arcEx = FFCurves.Extend(half, 10, false, true, true);
    Require(arcEx.All(p => Math.Abs(p.Length - 20) < 1e-6) && Math.Abs(Len(arcEx) - Len(half) - 10) < 0.01, "extend along the arc");
    var br = FFCurves.Bridge(new[] { new Vector3d(-10, 0, 0), new Vector3d(0, 0, 0) }, new[] { new Vector3d(20, 10, 0), new Vector3d(30, 10, 0) }, g2: false);
    var t0 = (br[1] - br[0]).Normalized(); var t1 = (br[^1] - br[^2]).Normalized();
    Require((br[0] - Vector3d.Zero).Length < 1e-12 && (br[^1] - new Vector3d(20, 10, 0)).Length < 1e-12 && t0.X > 0.999 && t1.X > 0.999, "bridge G1 tangents");
    var br2 = FFCurves.Bridge(half, new[] { new Vector3d(-40, 30, 0), new Vector3d(-50, 30, 0) }, g2: true);
    Require(br2.Count == 65, "bridge G2");
    var tr = FFCurves.TrimByPlane(new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, new Vector3d(4, 0, 0), Vector3d.UnitX, keepStart: true);
    Require((tr[^1] - new Vector3d(4, 0, 0)).Length < 1e-12, "trim by plane");
    var tc = FFCurves.TrimByCurve(new[] { new Vector3d(0, 0, 0), new Vector3d(10, 0, 0) }, new[] { new Vector3d(6, -5, 0), new Vector3d(6, 5, 0) }, false, 1e-6);
    Require((tc[0] - new Vector3d(6, 0, 0)).Length < 1e-12 && tc.Count == 2, "trim by curve");

    // в окне: FreeForm-документ; опорные кривые командами; труба вдоль прямой и спирали; отпечаток на грани бруска
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    var dps = new[] { new Vector3d(0, 0, 0), new Vector3d(0, 0, 50) }.Select((p, i) => { var d = new DatumPoint($"P{i}", p); part.Add(d); return d; }).ToList();
    w.ViewPort.Selection.Clear(); foreach (var d in dps) Select(w, new PickRef(d.Id, PickKind.DatumPoint, part, d));
    var cop = new FFCurveOp { Mode = FFCurveMode.Nurbs }; w.StartOperation(cop); w.CommitOperation();
    var line = part.Children.OfType<DatumCurve>().Last();
    Require(line.IsNurbs && Math.Abs(line.Length - 50) < 1e-9, $"nurbs curve command (length {line.Length})");
    var pipe = new FFPipeFeature { CurveName = line.Name, Radius = new ValueRef(3), Inner = new ValueRef(2), FreeForm = node.Key, Frozen = true };
    part.AddFeature(pipe); s.Document.RebuildDirty();
    Console.WriteLine($"  pipe R3/r2 L50: error {pipe.Error}, volume {part.Volume:F4} want {Math.PI * 5 * 50:F4}");
    Require(pipe.Error == null && Math.Abs(part.Volume - Math.PI * 5 * 50) < 0.02 * Math.PI * 5 * 50, "pipe");
    // спираль командой (центр — начало плоскости построения), труба R1 вдоль неё: объём ≈ π·1²·длина
    var spo = new FFCurveOp { Mode = FFCurveMode.Spiral }; w.ViewPort.Selection.Clear(); w.StartOperation(spo); w.CommitOperation();
    var helix = part.Children.OfType<DatumCurve>().Last();
    double hl = helix.Length;
    var pipe2 = new FFPipeFeature { CurveName = helix.Name, Radius = new ValueRef(1), FreeForm = node.Key, Frozen = true };
    double v0 = part.Volume;
    part.AddFeature(pipe2); s.Document.RebuildDirty();
    Console.WriteLine($"  pipe along helix (length {hl:F2}): error {pipe2.Error}, volume {part.Volume - v0:F3} want {Math.PI * hl:F3}, bodies {part.Bodies.Count}");
    Require(pipe2.Error == null && Math.Abs(part.Volume - v0 - Math.PI * hl) < 0.03 * Math.PI * hl && part.Bodies.Count == 2, "pipe along a helix");
    // перестроение спирали на месте (12 опорных) и «Отмена» — кривая как была
    var before = helix.Points.ToList();
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(helix.Id, PickKind.DatumAxis, part, helix));
    var rb = new FFCurveOp { Mode = FFCurveMode.Rebuild }; w.StartOperation(rb);
    Require(helix.IsNurbs && helix.Control!.Count == 8, "rebuild in place (preview)");
    w.CancelOperation();
    Require(!helix.IsNurbs && helix.Points.SequenceEqual(before), "cancel restores the curve");
    // брусок 40×30×20 и отпечаток квадрата 10 на верхней грани: грань делится, объём тот же
    var box = new FFPrimitiveFeature { Shape = FFPrimitive.Box, Origin = new Vector3d(100, 0, 0), FreeForm = node.Key, Frozen = true };
    part.AddFeature(box); s.Document.RebuildDirty();
    int bi = part.BodyNameList.IndexOf("Тело 3");
    Console.WriteLine($"  box: error {box.Error}, bodies {string.Join(",", part.BodyNameList)}");
    int faces0 = part.Bodies[bi].Faces.Count();
    var sqc = new DatumCurve("Квадрат", new[] { new Vector3d(95, -5, 20), new Vector3d(105, -5, 20), new Vector3d(105, 5, 20), new Vector3d(95, 5, 20), new Vector3d(95, -5, 20) }); part.Add(sqc);
    var im = new FFImprintFeature { CurveName = "Квадрат", FFBody = "Тело 3", FreeForm = node.Key, Frozen = true };
    var topF = part.Bodies[bi].Faces.First(f => Parametric3D.Kernel.Ops.PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.99);
    im.OnFaces.Add(Refs.FacePoint(part, topF.Id)!.Value);
    double vb = part.Volume;
    part.AddFeature(im); s.Document.RebuildDirty();
    bi = part.BodyNameList.IndexOf("Тело 3");
    Console.WriteLine($"  imprint closed square: error {im.Error}, faces {faces0} → {part.Bodies[bi].Faces.Count()}, volume change {part.Volume - vb:E2}");
    Require(im.Error == null && part.Bodies[bi].Faces.Count() == faces0 + 1 && Math.Abs(part.Volume - vb) < 1e-6, "imprint closed curve");
    // незамкнутая: прямая поперёк верхней грани (x = 110)
    var cross = new DatumCurve("Поперёк", new[] { new Vector3d(110, -10, 20), new Vector3d(110, 10, 20) }); part.Add(cross);
    var im2 = new FFImprintFeature { CurveName = "Поперёк", FFBody = "Тело 3", FreeForm = node.Key, Frozen = true };
    im2.OnFaces.Add(new Vector3d(115, 10, 20));
    int faces1 = part.Bodies[bi].Faces.Count();
    part.AddFeature(im2); s.Document.RebuildDirty();
    bi = part.BodyNameList.IndexOf("Тело 3");
    Console.WriteLine($"  imprint open line: error {im2.Error}, faces {faces1} → {part.Bodies[bi].Faces.Count()}, volume change {part.Volume - vb:E2}");
    Require(im2.Error == null && part.Bodies[bi].Faces.Count() == faces1 + 1 && Math.Abs(part.Volume - vb) < 1e-6, "imprint open curve");
    // запись и чтение: NURBS опорной кривой
    var path = Path.Combine(Path.GetTempPath(), "u13i_curves.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty();
    var l2 = s2.Part!.Children.OfType<DatumCurve>().First(c => c.Name == line.Name);
    Require(l2.IsNurbs && l2.Control!.Count == line.Control!.Count && Math.Abs(s2.Part.Volume - part.Volume) < 1e-6 * part.Volume, "save and load curves");
    // изопараметрическая кривая цилиндра R5: точки на радиусе 5
    var cyl = CylPart("Цилиндр", 5, 30); cyl.Rebuild(null);
    var side = cyl.Bodies[0].Faces.First(f => Parametric3D.Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr && Math.Abs(rr - 5) < 1e-9);
    var iso = FFCurves.Isoparm(side, alongV: true, 0.25);
    Console.WriteLine($"  isoparm on a cylinder: {iso.Count} points, z {iso.Min(p => p.Z):F3}..{iso.Max(p => p.Z):F3}");
    Require(iso.All(p => Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 5) < 1e-6) && iso.Max(p => p.Z) - iso.Min(p => p.Z) > 29, "isoparm");
    w.Close();
});

// u13j: поверхности FreeForm — линейчатая, сетка кривых, заплатка, плоская, мостик краёв, продление, обращение,
// опорные точки, вес, узел, сшивка в тело, отверждение и замена грани NURBS-квилтом, запись.
Check("UI-U13J-SURFACES", () =>
{
    double QArea(Quilt q)
    {
        double a = 0;
        foreach (var f in q.Faces)
        {
            var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
            for (int i = 0; i + 2 < md.Indices.Length; i += 3) { var p0 = mb.PositionsD[md.Indices[i]]; a += Vector3d.Cross(mb.PositionsD[md.Indices[i + 1]] - p0, mb.PositionsD[md.Indices[i + 2]] - p0).Length / 2; }
        }
        return a;
    }
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    T Add<T>(T f) where T : Feature { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Require(f.Error == null, $"{f.GetType().Name}: {f.Error}"); return f; }
    DatumCurve Curve(string n, params Vector3d[] p) { var c = new DatumCurve(n, p); part.Add(c); return c; }
    PickRef CRef(DatumCurve c) => new(c.Id, PickKind.DatumAxis, part, c);
    // линейчатая между отрезками: полоса 40 × √(30² + 10²)
    var a = Curve("A", new(0, 0, 0), new(40, 0, 0)); var b = Curve("B", new(0, 30, 10), new(40, 30, 10));
    w.ViewPort.Selection.Clear(); Select(w, CRef(a)); Select(w, CRef(b));
    var ro = new FFSurfaceOp { Mode = FFSurfMode.Ruled }; w.StartOperation(ro); w.CommitOperation(); s.Document.RebuildDirty();
    var ruled = part.Features.OfType<FFNurbsQuiltFeature>().Last();
    var rq = part.Quilts.Single(q => q.Key == ruled.Key);
    double want = 40 * Math.Sqrt(1000);
    Console.WriteLine($"  ruled: error {ruled.Error}, quilt area {QArea(rq):F4} want {want:F4}, grid {ruled.Def.Nu}×{ruled.Def.Nv}");
    Require(ruled.Error == null && rq.Nurbs != null && Math.Abs(QArea(rq) - want) < 1e-6 * want && ruled.Frozen, "ruled surface");
    // продление края U1 на 10: площадь + 10 × √1000 (вдоль полосы)
    var ext = Add(new FFNurbsQuiltFeature { ReplacesKey = ruled.Key, Def = NurbsDef.Of(FFSurfaces.Extend(rq.Nurbs!, 1, 10)) });
    rq = part.Quilts.Single(q => q.Key == ruled.Key);
    Console.WriteLine($"  extend: area {QArea(rq):F4} want {want * 1.25:F4}");
    Require(Math.Abs(QArea(rq) - want * 1.25) < 1e-3 * want, "extend surface");
    // опорная точка командой (выбрана поверхность → правка поверхности), сдвиг по Z на 5
    var face = rq.Faces.First();
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(face.Id, PickKind.Face, part, null));
    CommandRegistry.Instance["ff.cv_edit"].Execute();
    Require(w.CurrentOperation is FFSurfaceOp, "cv edit dispatches to the surface");
    var cvF = (FFNurbsQuiltFeature)w.CurrentOperation!.EditedFeature!;
    w.CommitOperation(); s.Document.RebuildDirty();
    rq = part.Quilts.Single(q => q.Key == ruled.Key);
    Console.WriteLine($"  cv edit: error {cvF.Error}, replaces {cvF.ReplacesKey == ruled.Key}, cp00 {rq.Nurbs!.ControlPoint(0, 0)}");
    Require(cvF.Error == null && cvF.ReplacesKey == ruled.Key && Math.Abs(rq.Nurbs!.ControlPoint(0, 0).Z - 5) < 1e-9, "cv edit");
    // вставка узла: форма та же; вес: форма меняется; обращение: нормаль наоборот
    var s0 = rq.Nurbs!; var k = s0.InsertKnotU(0.37);
    double kd = 0; for (int i = 0; i <= 10; i++) for (int j = 0; j <= 10; j++) kd = Math.Max(kd, (s0.PointAt(new Vector2d(i / 10.0, j / 10.0)) - k.PointAt(new Vector2d(i / 10.0, j / 10.0))).Length);
    Require(k.CountU == s0.CountU + 1 && kd < 1e-9, $"knot keeps the shape ({kd})");
    var fl = FFSurfaces.Flip(NurbsDef.Of(s0)).Surface;
    var c0 = s0.PointAt(new Vector2d(0.5, 0.5));
    Require(Vector3d.Dot(s0.NormalAt(new Vector2d(0.5, 0.5)), fl.NormalAt(fl.ProjectToUV(c0))) < -0.99, "flip normal");
    // сетка кривых: параболоид z = (x² + y²)/100 по трём кривым U и трём V
    List<Vector3d> Par(Func<double, Vector3d> f) => Enumerable.Range(0, 41).Select(i => f(i)).ToList();
    Vector3d Z(double x, double y) => new(x, y, (x * x + y * y) / 100);
    var us = new[] { 0.0, 20, 40 }.Select(y => (IReadOnlyList<Vector3d>)Par(t => Z(t, y))).ToList();
    var vs = new[] { 0.0, 20, 40 }.Select(x => (IReadOnlyList<Vector3d>)Par(t => Z(x, t))).ToList();
    var net = FFSurfaces.Network(us, vs);
    double ndev = FFSurfaces.Deviation(net, us.Concat(vs).SelectMany(c => c));
    double cdev = (net.PointAt(net.ProjectToUV(Z(10, 10))) - Z(10, 10)).Length;
    Console.WriteLine($"  network: deviation from curves {ndev:E2}, from the paraboloid at (10,10) {cdev:E2}");
    Require(ndev < 0.05 && cdev < 0.2, "network surface");
    // заплатка по квадрату 20 (четыре стороны) — плоская, площадь 400
    var sq = new[] { new Vector3d(0, 0, 50), new(20, 0, 50), new(20, 20, 50), new(0, 20, 50) };
    var sides = Enumerable.Range(0, 4).Select(i => (IReadOnlyList<Vector3d>)new[] { sq[i], sq[(i + 1) % 4] }).ToList();
    var patch = FFSurfaces.Patch(sides);
    var pq = FFSurfaces.Quilt(patch, "p", "patch");
    Console.WriteLine($"  patch: area {QArea(pq):F6}");
    Require(Math.Abs(QArea(pq) - 400) < 1e-6, "coons patch");
    // плоская поверхность по замкнутому квадрату и утолщение 2 → 800
    var loop = Curve("Loop", new(100, 0, 0), new(120, 0, 0), new(120, 20, 0), new(100, 20, 0), new(100, 0, 0));
    var pl = Add(new FFPlanarQuiltFeature { Boundary = { new(100, 0, 0), new(120, 0, 0), new(120, 20, 0), new(100, 20, 0), new(100, 0, 0) } });
    var th = Add(new QuiltSolidFeature { Source = pl.Key, Thickness = new ValueRef(2) });
    Console.WriteLine($"  planar + thicken: volume {part.Volume:F6}");
    Require(Math.Abs(part.Volume - 800) < 1e-6, "planar surface and thicken");
    // сшивка шести плоских граней куба 10 (обход — наружу) → тело 1000
    var cube = new[] {
        new[] { new Vector3d(200, 0, 0), new(200, 10, 0), new(210, 10, 0), new(210, 0, 0) },
        new[] { new Vector3d(200, 0, 10), new(210, 0, 10), new(210, 10, 10), new(200, 10, 10) },
        new[] { new Vector3d(200, 0, 0), new(210, 0, 0), new(210, 0, 10), new(200, 0, 10) },
        new[] { new Vector3d(200, 10, 0), new(200, 10, 10), new(210, 10, 10), new(210, 10, 0) },
        new[] { new Vector3d(200, 0, 0), new(200, 0, 10), new(200, 10, 10), new(200, 10, 0) },
        new[] { new Vector3d(210, 0, 0), new(210, 10, 0), new(210, 10, 10), new(210, 0, 10) } };
    var keys = new List<string>();
    foreach (var f4 in cube) { var pf = new FFPlanarQuiltFeature(); pf.Boundary.AddRange(f4); pf.Boundary.Add(f4[0]); Add(pf); keys.Add(pf.Key); }
    double v0 = part.Volume;
    var st = new FFStitchFeature(); st.Sources.AddRange(keys); Add(st);
    Console.WriteLine($"  stitch: error {st.Error}, volume added {part.Volume - v0:F6}, bodies {part.Bodies.Count}, quilts left {part.Quilts.Count}");
    Require(Math.Abs(part.Volume - v0 - 1000) < 1e-6 && part.Bodies.Count == 2, "stitch into a body");
    // мостик между верхними рёбрами двух брусков (G1 к верхним граням)
    var b1 = Add(new FFPrimitiveFeature { Shape = FFPrimitive.Box, Origin = new Vector3d(0, 100, 0), A = new ValueRef(20), B = new ValueRef(20), C = new ValueRef(10) });
    var b2 = Add(new FFPrimitiveFeature { Shape = FFPrimitive.Box, Origin = new Vector3d(60, 100, 0), A = new ValueRef(20), B = new ValueRef(20), C = new ValueRef(10) });
    Parametric3D.Kernel.Topology.TEdge TopEdge(double x) => part.Bodies.SelectMany(bb => bb.Edges).First(e => Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9 && Math.Abs(e.V0.P.X - x) < 1e-9 && Math.Abs(e.V1.P.X - x) < 1e-9);
    var ea = TopEdge(10); var eb = TopEdge(50);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(ea.Id, PickKind.Edge, part, null)); Select(w, new PickRef(eb.Id, PickKind.Edge, part, null));
    var bo = new FFSurfaceOp { Mode = FFSurfMode.Blend }; w.StartOperation(bo); w.CommitOperation(); s.Document.RebuildDirty();
    var blend = part.Features.OfType<FFNurbsQuiltFeature>().Last();
    var bq = part.Quilts.Single(q => q.Key == blend.Key);
    var mid = bq.Nurbs!.PointAt(new Vector2d(0.5, 0.5));
    var tan = bq.Nurbs.NormalAt(new Vector2d(0.5, 0.0));
    Console.WriteLine($"  blend: error {blend.Error}, mid {mid}, normal at the first edge {tan}");
    Require(blend.Error == null && Math.Abs(mid.X - 30) < 1e-6 && Math.Abs(mid.Z - 10) < 1e-6 && Math.Abs(Math.Abs(tan.Z) - 1) < 1e-6, "edge blend G1 (flat between two tops)");
    // замена верхней грани бруска (20×20, z = 10) выпуклой NURBS-поверхностью z = 10 + 5·sin(πu)·sin(πv) над ней:
    // объём растёт на 5·400·(2/π)²
    var gq = new Vector3d[17, 17];
    for (int i = 0; i <= 16; i++) for (int j = 0; j <= 16; j++) { double u = i / 16.0, v = j / 16.0; gq[i, j] = new Vector3d(-10 + 20 * u, 90 + 20 * v, 10 + 5 * Math.Sin(Math.PI * u) * Math.Sin(Math.PI * v)); }
    var bump = Add(new FFNurbsQuiltFeature { Def = NurbsDef.Of(FFSurfaces.Interpolate(gq)), QuiltName = "Bump" });
    string b1name = part.BodyNameOf(ea.Id)!;
    var rf = new QuiltSolidFeature { Mode = QuiltSolidKind.ReplaceFace, Source = bump.Key, FFBody = b1name };
    rf.OnFaces.Add(new Vector3d(0, 100, 10));
    double vr0 = part.Volume;
    Add(rf);
    double vbump = 5 * 400 * 4 / (Math.PI * Math.PI);
    Console.WriteLine($"  replace face by NURBS: error {rf.Error}, volume added {part.Volume - vr0:F3} want {vbump:F3}");
    Require(Math.Abs(part.Volume - vr0 - vbump) < 0.01 * vbump, "replace face by a NURBS surface");
    // запись и чтение: те же квилты и объём
    var path = Path.Combine(Path.GetTempPath(), "u13j_surf.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty();
    Console.WriteLine($"  reopened: quilts {s2.Part!.Quilts.Count} (was {part.Quilts.Count}), volume {s2.Part.Volume:F4} (was {part.Volume:F4})");
    Require(s2.Part.Quilts.Count == part.Quilts.Count && Math.Abs(s2.Part.Volume - part.Volume) < 1e-6 * part.Volume
            && s2.Part.Quilts.Any(q => q.Nurbs != null && Math.Abs(q.Nurbs.ControlPoint(0, 0).Z - 5) < 1e-9), "save and load surfaces");
    w.Close();
});

// u13k: SubD — клетка-брусок, уровни, складки (все рёбра — брусок точно), выдавливание граней, петля рёбер, в NURBS,
// перенос с клеткой, выбор граней и рёбер тела → клетка, запись.
Check("UI-U13K-SUBD", () =>
{
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    CommandRegistry.Instance["subd.box"].Execute();
    Require(w.CurrentOperation is SubDOp, "subd box op");
    w.CommitOperation(); s.Document.RebuildDirty();
    var box = part.Features.OfType<FFSubDFeature>().Single();
    var name = part.BodyNameList.Single();
    double v2 = part.Volume;
    Console.WriteLine($"  box cage: error {box.Error}, body {name}, volume L2 {v2:F2}, faces {part.Bodies[0].Faces.Count()}, cage {part.CagesByBody[name].V.Count}/{part.CagesByBody[name].F.Count}");
    Require(box.Error == null && part.CagesByBody.ContainsKey(name) && part.Bodies[0].Faces.Count() == 6 && v2 > 6000 && v2 < 14400
            && part.Bodies[0].Edges.All(e => e.CoEdges.Count == 2), "subd body from the box cage");
    // уровни 3 и 4 — объём сходится (разница < 2 %; между 2 и 3 — больше)
    var c3 = part.CagesByBody[name];
    double v3 = MeshVol(c3.ToSolid(3)), v4 = MeshVol(c3.ToSolid(4));
    Console.WriteLine($"  volume L3 {v3:F2}, L4 {v4:F2}");
    Require(Math.Abs(v4 - v3) < 0.02 * v3 && Math.Abs(v4 - v3) < Math.Abs(v3 - v2), "levels converge");
    // все рёбра — складки: тело — брусок точно
    var cr = part.CagesByBody[name].Clone(); foreach (var k in cr.EdgeFaces().Keys) cr.Creases.Add(k);
    double vc = MeshVol(cr.ToSolid(2));
    Console.WriteLine($"  all creased: volume {vc:F6}");
    Require(Math.Abs(vc - 24000) < 1e-6, "all creases keep the box");
    // выдавить верхнюю грань клетки командой: выбрана грань тела → грань клетки
    var top = part.Bodies[0].Faces.First(f => SubDCage.FaceOfTag(f.Tag) == 1);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(top.Id, PickKind.Face, part, null));
    var ex = new SubDOp { Mode = SubDMode.Extrude }; w.StartOperation(ex); w.CommitOperation(); s.Document.RebuildDirty();
    var exF = part.Features.OfType<FFSubDFeature>().Last();
    var cg = part.CagesByBody[name];
    Console.WriteLine($"  extrude: error {exF.Error}, cage {cg.V.Count}/{cg.F.Count}, volume {part.Volume:F2}, bodies {part.Bodies.Count}");
    Require(exF.Error == null && exF.Body == name && cg.V.Count == 12 && cg.F.Count == 10 && part.Volume > v2 + 1000 && part.Bodies.Count == 1, "extrude cage face");
    // петля рёбер: вертикальное ребро тела нижнего яруса → кольцо из 4 боковых граней
    var vEdge = part.Bodies[0].Edges.First(e => e.FaceA != null && e.FaceB != null && SubDRefs.CageEdge(new PickRef(e.Id, PickKind.Edge, part, null), cg) is { } ce && Math.Abs(cg.V[ce.Item1].Z - cg.V[ce.Item2].Z) > 15 && cg.V[ce.Item1].Z < 1 + Math.Min(cg.V[ce.Item1].Z, cg.V[ce.Item2].Z) && Math.Min(cg.V[ce.Item1].Z, cg.V[ce.Item2].Z) < 1e-6);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(vEdge.Id, PickKind.Edge, part, null));
    var lo = new SubDOp { Mode = SubDMode.EdgeLoop }; w.StartOperation(lo); w.CommitOperation(); s.Document.RebuildDirty();
    cg = part.CagesByBody[name];
    Console.WriteLine($"  edge loop: error {part.Features.Last().Error}, cage {cg.V.Count}/{cg.F.Count}, closed {cg.IsClosed()}");
    Require(part.Features.Last().Error == null && cg.V.Count == 16 && cg.F.Count == 14 && cg.IsClosed(), "edge loop");
    // складка: два ребра тела → рёбра клетки
    var edges = part.Bodies[0].Edges.Where(e => e.FaceA != null && e.FaceB != null && SubDRefs.CageEdge(new PickRef(e.Id, PickKind.Edge, part, null), cg) != null)
        .GroupBy(e => SubDRefs.CageEdge(new PickRef(e.Id, PickKind.Edge, part, null), cg)).Select(g => g.First()).Take(2).ToList();
    w.ViewPort.Selection.Clear(); foreach (var e in edges) Select(w, new PickRef(e.Id, PickKind.Edge, part, null));
    var cro = new SubDOp { Mode = SubDMode.Crease }; w.StartOperation(cro); w.CommitOperation(); s.Document.RebuildDirty();
    Require(part.CagesByBody[name].Creases.Count == 2, $"crease ({part.CagesByBody[name].Creases.Count})");
    // перенос тела SubD — клетка вместе с ним
    double z0 = part.CagesByBody[name].V.Min(p => p.X);
    var mv = new FFTransformFeature { Mode = FFXform.Move, BodyNames = { name }, Dx = new ValueRef(100), FreeForm = node.Key, Frozen = true };
    part.AddFeature(mv); s.Document.RebuildDirty();
    Require(mv.Error == null && Math.Abs(part.CagesByBody[name].V.Min(p => p.X) - z0 - 100) < 1e-9, "move carries the cage");
    // запись и чтение
    var path = Path.Combine(Path.GetTempPath(), "u13k_subd.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path); s2.Document.RebuildDirty();
    Console.WriteLine($"  reopened: volume {s2.Part!.Volume:F3} (was {part.Volume:F3}), cages {s2.Part.CagesByBody.Count}, creases {s2.Part.CagesByBody.Values.First().Creases.Count}");
    Require(Math.Abs(s2.Part.Volume - part.Volume) < 1e-6 * part.Volume && s2.Part.CagesByBody.Values.First().Creases.Count == 2, "save and load subd");
    // в NURBS: грани — NURBS-лоскуты через точки предельной поверхности, тело замкнуто; объём — в 2 % от подразделения
    // уровня 5 (подразделение сходится к пределу снаружи, разметка лоскутов — хорды изнутри)
    double vs = MeshVol(part.CagesByBody[name].ToSolid(5));
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(part.Bodies[0].Faces.First().Id, PickKind.Face, part, null));
    var tn = new SubDOp { Mode = SubDMode.ToNurbs }; w.StartOperation(tn); w.CommitOperation(); s.Document.RebuildDirty();
    var nb = part.Bodies[0];
    int nurbsFaces = nb.Faces.Count(f => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface);
    Console.WriteLine($"  to NURBS: error {part.Features.Last().Error}, faces {nb.Faces.Count()} (NURBS {nurbsFaces}), open edges {nb.Edges.Count(e => e.CoEdges.Count < 2)}, volume {part.Volume:F2} (subd L5 {vs:F2}), cages {part.CagesByBody.Count}");
    Require(part.Features.Last().Error == null && nurbsFaces == nb.Faces.Count() && nb.Edges.All(e => e.CoEdges.Count == 2) && Math.Abs(part.Volume - vs) < 0.02 * vs && part.Volume < vs && part.CagesByBody.Count == 0, "subd to nurbs");
    w.Close();
});

double MeshVol(Parametric3D.Kernel.Topology.TSolid b)
{
    var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b); double v = 0;
    for (int i = 0; i + 2 < md.Indices.Length; i += 3) { var p0 = md.Vertices[md.Indices[i]].P; var p1 = md.Vertices[md.Indices[i + 1]].P; var p2 = md.Vertices[md.Indices[i + 2]].P; v += Vector3.Dot(p0, Vector3.Cross(p1, p2)) / 6.0; }
    return v;
}

// u13: опорные элементы кнопок (плоскость посередине и касательная, ось грани, точка в центре дуги) и уклон
Check("UI-U13-DATUMS-DRAFT", () =>
{
    var s = DocumentSession.Sample(); var w = Open(s); var part = s.Part!; part.Rebuild(null);
    var fx = part.Bodies[0].Faces.Where(f => Refs.FacePlane(part, f.Id) is { } pl && Math.Abs(Math.Abs(pl.N.X) - 1) < 1e-9).ToList();
    Require(fx.Count == 2, "two x faces");
    w.ViewPort.Selection.Clear(); foreach (var f in fx) Select(w, new PickRef(f.Id, PickKind.Face, part, null));
    int n0 = part.Planes.Count();
    w.StartOperation(new PlaneOp { InitialMethod = 6 }); w.CommitOperation();
    var mid = part.Planes.Last();
    Console.WriteLine($"  mid plane: {mid.Frame.Origin} n {mid.Frame.W}");
    Require(part.Planes.Count() == n0 + 1 && Math.Abs(mid.Frame.Origin.X) < 1e-9 && Math.Abs(Math.Abs(mid.Frame.W.X) - 1) < 1e-9, "mid plane");
    // уклон 5° боковой грани +x от нижней
    double v0 = part.Volume;
    var bottom = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z < -0.99);
    w.ViewPort.Selection.Clear(); Select(w, new PickRef(fx[0].Id, PickKind.Face, part, null));
    var dop = new DraftOp(); w.StartOperation(dop);
    var f0 = part.Features.OfType<DraftFeature>().LastOrDefault();
    // нейтральная плоскость — нижняя грань (поле второе)
    w.ViewPort.Selection.Select(new PickRef(bottom.Id, PickKind.Face, part, null), additive: true);
    w.CommitOperation(); s.Document.RebuildDirty();
    var dr = part.Features.OfType<DraftFeature>().Single();
    Console.WriteLine($"  draft: error {dr.Error}, volume {v0:F2} -> {part.Volume:F2}");
    Require(dr.Error == null && Math.Abs(part.Volume - v0) > 1, $"draft {dr.Error}");
    w.Close();

    // ось грани и точка в центре дуги — у скругления
    var s2 = DocumentSession.Sample(); var w2 = Open(s2); var p2 = s2.Part!; p2.Rebuild(null);
    double zMax = p2.Bodies[0].Vertices.Max(v => v.P.Z);
    var e = p2.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - zMax) < 1e-6 && Math.Abs(x.V1.P.Z - zMax) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(10) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e)); p2.AddFeature(rf); p2.Rebuild(null);
    var cyl = p2.Bodies[0].Faces.First(f => f.Tag.Contains("round:face"));
    w2.ViewPort.Selection.Clear(); Select(w2, new PickRef(cyl.Id, PickKind.Face, p2, null));
    w2.StartOperation(new AxisOp { InitialMethod = 3 }); w2.CommitOperation();
    var ax = p2.Children.OfType<DatumAxis>().Last();
    var ed = (e.V1.P - e.V0.P).Normalized();
    Console.WriteLine($"  face axis: {ax.Origin} dir {ax.Direction}");
    Require(Math.Abs(Math.Abs(Vector3d.Dot(ax.Direction.Normalized(), ed)) - 1) < 1e-9, "face axis direction");
    w2.ViewPort.Selection.Clear(); Select(w2, new PickRef(cyl.Id, PickKind.Face, p2, null));
    w2.StartOperation(new PlaneOp { InitialMethod = 7 }); w2.CommitOperation();
    var tp = p2.Planes.Last(); var rad = tp.Frame.Origin - ax.Origin; rad -= ax.Direction.Normalized() * Vector3d.Dot(rad, ax.Direction.Normalized());
    Console.WriteLine($"  tangent plane at {tp.Frame.Origin}, distance from axis {rad.Length:F6}");
    Require(Math.Abs(rad.Length - 10) < 1e-6 && Math.Abs(Vector3d.Dot(tp.Frame.W.Normalized(), rad.Normalized())) > 1 - 1e-9, "tangent plane");
    var arc = p2.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3);
    w2.ViewPort.Selection.Clear(); Select(w2, new PickRef(arc.Id, PickKind.Edge, p2, null));
    w2.StartOperation(new PointOp { InitialMethod = 1 }); w2.CommitOperation();
    var pt = p2.Children.OfType<DatumPoint>().Last();
    Console.WriteLine($"  arc centre point {pt.P} (arc centre {((Parametric3D.Kernel.Curves.ArcCurve3)arc.Curve).Frame.Origin})");
    Require((pt.P - ((Parametric3D.Kernel.Curves.ArcCurve3)arc.Curve).Frame.Origin).Length < 1e-9, "arc centre");
    w2.Close();
});

// u13: команды дерева — вставить здесь, переупорядочить (с отказом, если ломается), вернуть подавленную, размеры операции
Check("UI-U13-TREE", () =>
{
    var s = DocumentSession.Sample(); var w = Open(s); var part = s.Part!; part.Rebuild(null);
    double zMax = part.Bodies[0].Vertices.Max(v => v.P.Z);
    var es = part.Bodies[0].Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - zMax) < 1e-6 && Math.Abs(x.V1.P.Z - zMax) < 1e-6).ToList();
    es = new List<Parametric3D.Kernel.Topology.TEdge> { es[0], es.First(x => x.V0 != es[0].V0 && x.V0 != es[0].V1 && x.V1 != es[0].V0 && x.V1 != es[0].V1) };   // противоположные рёбра
    var r1 = new RoundFeature { Radius = new ValueRef(10), Name = "R10" }; r1.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(es[0])); part.AddFeature(r1);
    var c1 = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(5), D2 = new ValueRef(5), Name = "C5" }; c1.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(es[1])); part.AddFeature(c1);
    s.Document.RebuildDirty();
    double vAll = part.Volume;
    // вставить здесь после скругления: фаска вне расчёта
    w.SelectItem(r1); TreeCommands.InsertHere(w); s.Document.RebuildDirty();
    Require(part.InInsertMode && part.IsBeyondStage(c1) && !part.IsBeyondStage(r1), "insert here");
    Require(TreeCommands.NeedsAttention(part).Any(x => x.F == c1), "needs attention lists the rolled-back feature");
    w.SelectItem(r1); TreeCommands.InsertHere(w); s.Document.RebuildDirty();
    Require(!part.InInsertMode && Math.Abs(part.Volume - vAll) < 1e-6, "insert mode ended");
    // переупорядочить: фаска выше скругления (независимы) — объём тот же
    w.SelectItem(c1); TreeCommands.Reorder(w, -1); s.Document.RebuildDirty();
    Require(part.Features.IndexOf(c1) < part.Features.IndexOf(r1) && Math.Abs(part.Volume - vAll) < 1e-6, $"reorder: volume {part.Volume} vs {vAll}");
    // подавить и вернуть
    w.Session.Edit("s", () => r1.Suppressed = true); s.Document.RebuildDirty();
    w.ViewPort.Selection.Clear(); TreeCommands.Resume(w); s.Document.RebuildDirty();
    Require(!r1.Suppressed && Math.Abs(part.Volume - vAll) < 1e-6, "resume");
    // размеры операции: радиус скругления меняется через список чисел
    var vals = TreeCommands.Values(r1);
    Require(vals.Any(v => v.Name == "Radius"), "round values");
    w.Session.Edit("r", () => { vals.First(v => v.Name == "Radius").Set(new ValueRef(12)); part.NotifyChanged(r1); }); s.Document.RebuildDirty();
    Require(Math.Abs(r1.Radius.Value - 12) < 1e-12 && part.Volume < vAll, "edit value");
    TreeCommands.Rebuild(w);
    Require(part.Features.All(f => f.Error == null), "rebuild");
    w.Close();
});

Check("UI-PARAMETERS-SCOPE", () =>
{
    // параметры по уровням: формула детали видит параметр документа, своё имя закрывает документное; файл хранит оба уровня
    var s = DocumentSession.NewPart();
    var part = s.Part!;
    s.Document.Parameters.Add("W", 30, Parametric3D.Model.Params.ParamKind.Real, "mm");
    s.Document.Parameters.Add("H", 5, Parametric3D.Model.Params.ParamKind.Real, "mm");
    part.LocalParameters.Add("H", 7, Parametric3D.Model.Params.ParamKind.Real, "mm");
    part.LocalParameters.AddFormula("D", "W + H", "mm");
    var t = s.Document.ScopeFor(part);
    Require(t.TryValue("D", out double d) && Math.Abs(d - 37) < 1e-9, $"D = {d}");
    Require(s.Document.Parameters.TryValue("H", out double h) && h == 5, "document H changed");
    string path = Path.Combine(Path.GetTempPath(), $"uiparams_{Guid.NewGuid():N}.p3d");
    s.SaveAs(path);
    var s2 = DocumentSession.Open(path);
    var t2 = s2.Document.ScopeFor(s2.Part!);
    Require(s2.Part!.LocalParameters.Count == 2 && t2.TryValue("D", out double d2) && Math.Abs(d2 - 37) < 1e-9, "part parameters were not saved");
    File.Delete(path);
});

Check("UI-LANGUAGE", () =>
{
    // язык (п. 2): английские подписи команд и обратно; у каждой команды на панелях есть перевод
    var w = Open(DocumentSession.NewPart());
    Loc.Set(true);
    var reg = CommandRegistry.Instance;
    Require(reg["file.save"].Text == "Save" && reg["op.extrude"].Text == "Extrude", "english labels");
    var missing = reg.All.Where(c => c.Text == c.Label || c.ShortText == (c.Short ?? c.Label) && c.Short != null && c.Short.Any(ch => ch >= 'А' && ch <= 'я'))
        .Where(c => c.Label.Any(ch => ch >= 'А' && ch <= 'я')).Select(c => c.Id).ToList();
    Loc.Set(false);
    Require(reg["file.save"].Text == "Сохранить", "russian labels");
    Require(missing.Count == 0, "no translation: " + string.Join(", ", missing));
    w.Close();
});

Check("UI-TANGENT-CHAIN-CHAMFER", () =>
{
    // п. 4: ребро вместе с касательной цепочкой; фаска по цепочке строится годным телом (снимок пользователя — фаска
    // только части цепочки)
    var p = Parametric3D.App.Diagnostics.TestParts.Block("chain");
    p.Rebuild(null);
    var vertical = p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.X - 50) < 1e-6 && Math.Abs(e.V0.P.Y - 30) < 1e-6 && Math.Abs(e.V1.P.X - 50) < 1e-6 && Math.Abs(e.V1.P.Y - 30) < 1e-6);
    var rf = new RoundFeature("R") { Radius = new ValueRef(8) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(vertical));
    p.AddFeature(rf); p.Rebuild(null);
    Require(rf.Error == null, "round: " + rf.Error);
    // верхнее прямое ребро у скругления: z = 20, y = 30
    var top = p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-6 && Math.Abs(e.V1.P.Z - 20) < 1e-6 && Math.Abs(e.V0.P.Y - 30) < 1e-6 && Math.Abs(e.V1.P.Y - 30) < 1e-6);
    var chain = Topo.TangentChain(top);
    Require(chain.Count == 3, $"chain of the top edge: {chain.Count} edges (expected line + arc + line)");
    var cf = new ChamferFeature("C") { D1 = new ValueRef(2), D2 = new ValueRef(2) };
    foreach (var e in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e));
    p.AddFeature(cf); p.Rebuild(null);
    Require(cf.Error == null, "chamfer of the chain: " + cf.Error);
    Require(p.Bodies.All(b => Parametric3D.Kernel.Topology.SolidValidator.Validate(b).IsValid), "chamfered body is not valid");
});

Check("UI-OP-RELEASE", () =>
{
    // k97 (самотест 17:56, раздел 10: +249 МБ): после «ОК» и «Отменить» прежняя деталь не держится окном — ни полями
    // панели операции, ни проверкой контура ручки, ни ожиданием точного расчёта. Мутация P3D_OPRELEASE=0.
    var s0 = DocumentSession.FromPart(Parametric3D.App.Diagnostics.TestParts.Realistic(3, 6), "rel");
    var w0 = Open(s0);
    s0.Document.RebuildDirty();
    var weak = new List<WeakReference>();
    // правка — в отдельном методе: локальные переменные кадра не держат деталь (без инлайнинга)
    for (int i = 0; i < 3; i++) EditOnce(i);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void EditOnce(int i)
    {
        var p = s0.Part!;
        var e = p.Bodies.SelectMany(b => b.Edges).Where(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (x.V0.P - x.V1.P).Length > 6 && Topo.EdgeHandle(p, x.Id) != null).ElementAt(i);
        w0.ViewPort.Selection.Select(new PickRef(e.Id, PickKind.Edge, p, null), additive: false);
        w0.StartOperation(new RoundOp()); Dispatcher.UIThread.RunJobs(); s0.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
        w0.CommitOperation(); s0.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
        weak.Add(new WeakReference(s0.Part));
        s0.Undo(); s0.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    }
    w0.ViewPort.Selection.Clear(); Dispatcher.UIThread.RunJobs();
    // поточные ссылки потоков пула (последняя операция потока) — ограничены числом потоков, не утечка; отпускаются
    for (int rep = 0; rep < 3; rep++) System.Threading.Tasks.Parallel.For(0, 128, new ParallelOptions { MaxDegreeOfParallelism = 32 }, _ => { Parametric3D.Kernel.Diagnostics.ThreadState.Release(); Thread.Sleep(2); });
    for (int k = 0; k < 3; k++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    int alive = weak.Count(x => x.IsAlive && !ReferenceEquals(x.Target, s0.Part));
    if (alive > 0 && Environment.GetEnvironmentVariable("P3D_RELDBG") == "1") PathTo(weak.First(x => x.IsAlive && !ReferenceEquals(x.Target, s0.Part)).Target!, new (object, string)[] { (w0, "window"), (s0, "session") });
    // последняя заменённая может держаться временно (показ до перестроения новой), прежние — нет
    Require(alive <= 1, $"replaced parts still alive: {alive} of {weak.Count}");
});

Check("UI-SKETCH-K109B", () =>
{
    // k109 (11:11): в правке эскиза видны эскизы параллельных плоскостей (и скрытые); их кривые и точки — ссылки
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var low = part.AddSketch("Low", new Parametric3D.Kernel.Frame3(new Vector3d(0, 0, -30), Vector3d.UnitX, Vector3d.UnitY));
    var gl = low.AddEntity(new SkLine(new SkPoint(30, 0), new SkPoint(50, 0)));
    low.Visible = false; low.VisibleUserSet = true;
    var side = part.AddSketch("Side", part.PlaneYZ.Frame, part.PlaneYZ.Id); side.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(10, 10)));
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); } }
    w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp()); Run();
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    vp.EnterSketch(sk, animate: false); vp.ZoomToFit(animate: false); Run();
    var pt = vp.Project(low.Plane.ToWorld(new Vector2d(40, 0)))!.Value;
    var under = vp.PickForTest(pt);
    Require(under.Kind == PickKind.SketchCurve && ReferenceEquals(under.Owner, low), $"hidden parallel sketch curve is not pickable: {under.Kind} {(under.Owner as ModelItem)?.Name}");
    var wp = vp.TranslatePoint(pt, w)!.Value;
    w.MouseMove(wp); Run(); w.MouseDown(wp, MouseButton.Left); w.MouseUp(wp, MouseButton.Left); Run();
    var refs = sk.Entities.Where(e => e.Role == SkRole.Reference && e.ReferenceSource == $"Эскиз #{gl.Id}").ToList();
    Require(refs.OfType<SkLine>().Count() == 1 && refs.OfType<SkLine>().First() is { } rl && Math.Abs(rl.Length - 20) < 1e-9, "reference from the parallel sketch: " + string.Join(",", refs.Select(r => r.GetType().Name)));
    // перпендикулярный эскиз — не ссылка
    Require(!side.IsParallelTo(sk) && low.IsParallelTo(sk), "parallel test");
    w.FinishSketch(); Run();
});

Check("UI-SKETCH-K109", () =>
{
    // k109 (11:07): контекстное меню выбранного в эскизе — «Make construction» / «Make sketch»
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var w = Open(s);
    void Run() { for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); } }
    w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp()); Run();
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    var l = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(20, 0)));
    var c = sk.AddEntity(new SkCircle(new SkPoint(40, 0), 5));
    w.AfterSketchEdit(sk); Run();
    vp.Selection.Select(new PickRef(l.Id, PickKind.SketchCurve, sk, l), false);
    vp.Selection.Select(new PickRef(c.Id, PickKind.SketchCurve, sk, c), true);
    MenuItem Toggle() { w.OpenViewContextMenuForTest(); Run(); return w.LastContextMenu!.OfType<MenuItem>().First(m => m.Name == "makeConstruction"); }
    var mi = Toggle();
    Require((string)mi.Header! == Loc.L("Сделать вспомогательной (Make construction)"), "menu: " + mi.Header);
    mi.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Run();
    Require(l.Role == SkRole.Construction && c.Role == SkRole.Construction, "not construction");
    mi = Toggle();
    Require((string)mi.Header! == Loc.L("Сделать основной (Make sketch)"), "menu after: " + mi.Header);
    mi.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Run();
    Require(l.Role == SkRole.Object && c.Role == SkRole.Object, "not back to sketch geometry");
    CommandRegistry.Instance["edit.undo"].Execute(); Run();
    Require(sk.Entities.OfType<SkLine>().First().Role == SkRole.Construction, "undo of make sketch");
    w.FinishSketch(); Run();
});

Check("UI-TREE-K109", () =>
{
    // k109 (снимок 11:04): эскиз, созданный после выдавливания, в дереве — после него (и после отмены/открытия файла);
    // эскиз не в операции тянется мышью между операциями (не выше операции, на гранях которой стоит); перестановка
    // операции меняет и порядок в дереве
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var s1 = part.AddSketch("S1", Parametric3D.Kernel.Frame3.XY, part.PlaneXY.Id);
    s1.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
    var ex = new ExtrudeFeature { SketchId = s1.Id }; part.AddFeature(ex);
    s.Document.RebuildDirty();
    var s2 = part.AddSketch("S2", part.PlaneYZ.Frame, part.PlaneYZ.Id);
    s2.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(5, 5)));
    Require(part.SketchSlot(s2) == 1, $"new sketch slot {part.SketchSlot(s2)}");
    var (lp, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(part));
    var ls2 = lp.Sketches.First(k => k.Name == "S2");
    Require(lp.SketchSlot(ls2) == 1 && lp.Children.IndexOf(ls2) > lp.Children.IndexOf(lp.Features[0]), $"after reload S2 is above the extrude: slot {lp.SketchSlot(ls2)}");
    // файл до k109 (без места): эскиз на грани выдавливания — после него
    var s3 = part.AddSketch("S3", Parametric3D.Kernel.Frame3.XY); s3.BaseFaceTag = "#" + ex.Key + "/extrude[0]:top";
    var json = System.Text.RegularExpressions.Regex.Replace(Parametric3D.Model.DocumentFile.Save(part), ",\\s*\"Slot\":\\s*\\d+", "");
    var (op, _) = Parametric3D.Model.DocumentFile.Load(json);
    Require(op.SketchSlot(op.Sketches.First(k => k.Name == "S3")) == 1, "legacy sketch on the extrude face is not after it");
    part.Remove(s3);
    var w = Open(s);
    void Run() { for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); } }
    Run();
    // отмена правки не поднимает эскиз выше выдавливания
    s.Edit("rename", () => s.Part!.Name = "P"); s.Undo(); s.Document.RebuildDirty(); Run();
    part = s.Part!; s2 = part.Sketches.First(k => k.Name == "S2");
    Require(part.SketchSlot(s2) == 1, $"after undo S2 slot {part.SketchSlot(s2)}");
    // перетащить выше и мышью — обратно ниже
    Require(w.MoveSketchTo(s2, 0), "move sketch above"); Run();
    part = s.Part!; s2 = part.Sketches.First(k => k.Name == "S2"); ex = (ExtrudeFeature)part.Features[0];
    Require(part.SketchSlot(s2) == 0, "sketch is not above the extrude");
    var rs = w.TreeRowInWindow(s2); var re = w.TreeRowInWindow(ex);
    Require(rs != null && re != null, "tree rows");
    var a = rs!.Value.Center; var b = new Avalonia.Point(re!.Value.Center.X, re.Value.Bottom - 2);
    w.MouseDown(a, MouseButton.Left); Run();
    for (int i = 1; i <= 8; i++) { w.MouseMove(new Avalonia.Point(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8), RawInputModifiers.LeftMouseButton); Run(); }
    w.MouseUp(b, MouseButton.Left); Run();
    part = s.Part!; s2 = part.Sketches.First(k => k.Name == "S2");
    Require(part.SketchSlot(s2) == 1, $"mouse drag did not move the sketch below the extrude: slot {part.SketchSlot(s2)}");
    // эскиз на грани выдавливания — не выше него
    var s4 = part.AddSketch("S4", Parametric3D.Kernel.Frame3.XY); s4.BaseFaceTag = "#" + part.Features[0].Key + "/extrude[0]:top";
    Require(part.SketchSlotRange(s4).Lo == 1 && !w.MoveSketchTo(s4, 0), "sketch on the extrude face moved above it");
    // перестановка операций — порядок и в дереве
    var s5 = part.AddSketch("S5", Parametric3D.Kernel.Frame3.XY, part.PlaneXY.Id); s5.AddEntity(new SkCircle(new SkPoint(30, 0), 5));
    var ex2 = new ExtrudeFeature { SketchId = s5.Id }; part.AddFeature(ex2);
    part.MoveFeature(ex2, 0);
    Require(part.Children.IndexOf(ex2) < part.Children.IndexOf(part.Features[1]), "MoveFeature did not reorder the tree");
});

Check("UI-SWEEP-K107", () =>
{
    // k107 (отзыв 18:05): протяжка как в Creo — эскизов нет: «Создать эскиз траектории» (плоскость → эскиз → назад в
    // протяжку), «Создать эскиз сечения» (точка на траектории → плоскость поперёк траектории → эскиз → назад); стрелка
    // направления — щелчок разворачивает; отмена эскиза — назад в протяжку; есть только сечение — создать траекторию
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var w = Open(s);
    void Run() { for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); } }
    void Click(Button b) { b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Run(); }
    var vp = w.ViewPort;
    w.StartOperation(new SweepOp()); Run();
    var op = (SweepOp)w.CurrentOperation!;
    Require(!op.Panel!.Buttons["Создать эскиз сечения"].IsEnabled, "section sketch button is enabled without a path");
    // (1) эскиз траектории: плоскость XY
    Click(op.Panel.Buttons["Создать эскиз траектории"]);
    Require(op.PickingPathPlane, "path plane pick is not active");
    op.SelectionChanged(new[] { new PickRef(part.PlaneXY.Id, PickKind.DatumPlane, part, part.PlaneXY) }); Run();
    Require(w.InSketch, "path sketch did not start");
    var psk = vp.ActiveSketch!;
    Require(Math.Abs(Math.Abs(psk.Plane.W.Z) - 1) < 1e-9, "path sketch is not on XY");
    var l1 = psk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(40, 0)));
    var l2 = psk.AddEntity(new SkLine(l1.B, new SkPoint(40, 30)));
    w.FinishSketch(); Run();
    Require(w.CurrentOperation is SweepOp, "did not return to the sweep after the path sketch");
    op = (SweepOp)w.CurrentOperation!;
    Require(op.Panel!.Buttons["Создать эскиз сечения"].IsEnabled, "section sketch button is disabled with a path");
    // стрелка направления — у начала траектории
    Require(vp.OpArrows.Count == 1, $"direction arrows: {vp.OpArrows.Count}");
    var (at0, dir0) = vp.OpArrows[0];
    bool startA = at0.Length < 1e-6;
    // отмена эскиза сечения — назад в протяжку
    Click(op.Panel.Buttons["Создать эскиз сечения"]);
    Require(op.PickingSectionPoint, "section point pick is not active");
    var endPt = startA ? l2.B : l1.A;    // щёлкнуть дальний конец — он станет началом
    var near = startA ? l1.A : l2.B;
    op.SelectionChanged(new[] { new PickRef(near.Id, PickKind.SketchPoint, psk, near) }); Run();
    Require(w.InSketch, "section sketch did not start");
    w.CancelSketch(); Run();
    Require(w.CurrentOperation is SweepOp, "cancel of the section sketch did not return to the sweep");
    op = (SweepOp)w.CurrentOperation!;
    // (2) эскиз сечения у начала (0,0): плоскость поперёк траектории; k108 — точка траектории выбирается щелчком в виде
    Click(op.Panel!.Buttons["Создать эскиз сечения"]);
    vp.ZoomToFit(animate: false); Run();
    var sp0 = vp.Project(psk.Plane.ToWorld(l1.A.P))!.Value;
    var under = vp.PickForTest(sp0);
    Require(under.Kind == PickKind.SketchPoint && ReferenceEquals(under.Target, l1.A), $"path start point is not pickable: {under.Kind} {under.Target?.GetType().Name}");
    var wp = vp.TranslatePoint(sp0, w)!.Value;
    w.MouseMove(wp); Run(); w.MouseDown(wp, MouseButton.Left); w.MouseUp(wp, MouseButton.Left); Run();
    Require(w.InSketch, "section sketch did not start (2)");
    var ssk = vp.ActiveSketch!;
    Require(ssk.Plane.Origin.Length < 1e-6 && Math.Abs(Math.Abs(ssk.Plane.W.X) - 1) < 1e-9, $"section plane: origin {ssk.Plane.Origin}, normal {ssk.Plane.W}");
    ssk.AddEntity(new SkCircle(new SkPoint(0, 0), 3));
    w.FinishSketch(); Run();
    Require(w.CurrentOperation is SweepOp, "did not return to the sweep after the section sketch");
    op = (SweepOp)w.CurrentOperation!;
    Require(vp.OpArrows.Count == 1 && vp.OpArrows[0].At.Length < 1e-6 && vp.OpArrows[0].Dir.X > 0.99, $"arrow after choosing the start: {(vp.OpArrows.Count > 0 ? vp.OpArrows[0].ToString() : "none")}");
    // стрелка: щелчок — разворот (начало — другой конец)
    vp.RaiseOpArrowClicked(0); Run();
    Require(vp.OpArrows.Count == 1 && (vp.OpArrows[0].At - new Vector3d(40, 30, 0)).Length < 1e-6, "arrow click did not reverse the sweep: " + vp.OpArrows[0]);
    vp.RaiseOpArrowClicked(0); Run();
    w.CommitOperation(); Run();
    Require(w.CurrentOperation == null, "sweep commit failed");
    s.Document.RebuildDirty(); Run();
    part = s.Part!;
    var sw = part.Features.OfType<SweepFeature>().Single();
    Require(sw.Error == null && part.Volume > Math.PI * 9 * 60, $"sweep: error {sw.Error}, volume {part.Volume:0.#}");
    // (3) есть только сечение — создать траекторию из протяжки
    var s3 = DocumentSession.NewPart(); var p3 = s3.Part!;
    var sec = p3.AddSketch("Sec", p3.PlaneYZ.Frame, p3.PlaneYZ.Id); sec.AddEntity(new SkCircle(new SkPoint(0, 0), 2));
    s3.Document.RebuildDirty();
    var w3 = Open(s3);
    void Run3() { for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w3.UpdateLayout(); } }
    w3.ViewPort.Selection.Select(new PickRef(sec.Id, PickKind.SketchCurve, p3, sec), false);
    w3.StartOperation(new SweepOp()); Run3();
    var op3 = (SweepOp)w3.CurrentOperation!;
    op3.Panel!.Buttons["Создать эскиз траектории"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Run3();
    op3.SelectionChanged(new[] { new PickRef(p3.PlaneXY.Id, PickKind.DatumPlane, p3, p3.PlaneXY) }); Run3();
    Require(w3.InSketch, "path sketch did not start (section only)");
    var pk = w3.ViewPort.ActiveSketch!; pk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(50, 0)));
    w3.FinishSketch(); Run3();
    Require(w3.CurrentOperation is SweepOp, "sweep with a new path");
    w3.CommitOperation(); Run3();
    s3.Document.RebuildDirty();
    var sw3 = s3.Part!.Features.OfType<SweepFeature>().Single();
    Require(sw3.Error == null && Math.Abs(s3.Part.Volume - Math.PI * 4 * 50) < 0.02 * Math.PI * 4 * 50, $"section-only sweep: {sw3.Error} volume {s3.Part.Volume:0.##}");
    // направление сохраняется в файле
    sw3.Reverse = true;
    var (lp, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(s3.Part));
    Require(lp.Features.OfType<SweepFeature>().Single().Reverse, "reverse is not saved");
});

Check("UI-SKETCH-K106", () =>
{
    // k106 (снимок 13:13): вершина открытого конца — поверх красного квадрата (2); совпадение концов дуг, уже касающихся,
    // принимается командой «Совпадение» (1)
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp()); Run();
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    vp.EnterSketch(sk, animate: false); Run();
    var a = sk.AddEntity(new SkArc(new SkPoint(0, 0), new SkPoint(0, 10), new SkPoint(10, 0), false));
    var b = sk.AddEntity(new SkArc(new SkPoint(25, 0), new SkPoint(10, 0), new SkPoint(25, 15), false));
    sk.AddConstraint(ConstraintType.Tangent, a, b);
    sk.Solve(s.Document.Solver); w.AfterSketchEdit(sk); Run();
    int before = vp.OpenEndVertices;
    vp.InvalidateOverlay(); vp.BuildOverlayForTest();
    Require(vp.OpenEndVertices - before >= 2, $"open-end vertices on top: {vp.OpenEndVertices - before}");
    vp.Selection.Clear();
    vp.Selection.Select(new PickRef(a.End.Id, PickKind.SketchPoint, sk, a.End), false);
    vp.Selection.Select(new PickRef(b.Start.Id, PickKind.SketchPoint, sk, b.Start), true);
    Require(Parametric3D.App.Commands.SketchCommands.Constrain(w, ConstraintType.Coincident), "coincident of tangent arcs' ends was refused: " + w.ToastShown);
    Require(sk.Constraints.Any(c => c.Type == ConstraintType.Coincident), "no coincident");
    w.FinishSketch(); Run();
});

Check("UI-SKETCH-K105", () =>
{
    // k105 (отзыв 10:18): обрезка щелчком — кусок между пересечениями и точками на кривой (1), протяжкой — все пересечённые
    // куски (1); отмена и возврат в эскизе (2); размер окружности после обрезки остаётся тем же размером (3); подпись
    // внутреннего диаметра — вдоль линии (4), наружного — горизонтально на полке (5)
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp()); Run();
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    vp.EnterSketch(sk, animate: false); Run();
    // (4, 5) подпись диаметра
    var circ = sk.AddEntity(new SkCircle(new SkPoint(60, 0), 10));
    var dd = sk.AddDimension(DimensionType.Diameter, 20, circ); dd.Locked = true; dd.Formula = "20";
    dd.TextOffset = new Vector2d(4, 3);   // внутри: 5 мм по лучу (0.8, 0.6)
    // крупно: окружность на весь вид (подпись помещается между центром и стрелкой)
    for (int i = 0; i < 40 && vp.PixelSize > 0.03; i++) { var cp = vp.Project(sk.Plane.ToWorld(new Vector2d(60, 0)))!.Value; vp.Camera.Zoom(1, (int)(cp.X * vp.Scaling), (int)(cp.Y * vp.Scaling)); }
    Run();
    vp.InvalidateOverlay(); vp.BuildOverlayForTest();
    Require(SketchDimensions.LastDiameterText == "along", "inside diameter text: " + SketchDimensions.LastDiameterText);
    var rel = dd.TextCenter - new Vector2d(60, 0);
    Require(Math.Abs(Vector2d.Dot(rel, new Vector2d(0.8, 0.6)) - 5) < 2.5 && Math.Abs(rel.X * 0.6 - rel.Y * 0.8) < 3, $"inside text is not along the dimension line: {rel}");
    dd.TextOffset = new Vector2d(16, 12);  // снаружи
    vp.InvalidateOverlay(); vp.BuildOverlayForTest();
    Require(SketchDimensions.LastDiameterText == "shelf", "outside diameter text: " + SketchDimensions.LastDiameterText);
    // (2) отмена и возврат
    w.AfterSketchEdit(sk); Run();
    int undo0 = w.SketchUndoDepth;
    var l1 = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(40, 0)));
    w.AfterSketchEdit(sk); Run();
    var undo = CommandRegistry.Instance["edit.undo"]; var redo = CommandRegistry.Instance["edit.redo"];
    Require(undo.Enabled && w.SketchUndoDepth == undo0 + 1, $"undo is not available in the sketch: depth {w.SketchUndoDepth}");
    uint id1 = l1.Id;
    w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z"); Run();
    Require(!sk.Entities.OfType<SkLine>().Any(l => l.Id == id1), "Ctrl+Z did not undo the line");
    Require(redo.Enabled, "redo is not available");
    redo.Execute(); Run();
    Require(sk.Entities.OfType<SkLine>().Any(l => l.Id == id1), "redo did not return the line (same id)");
    l1 = sk.Entities.OfType<SkLine>().First(l => l.Id == id1);
    // (1) обрезка щелчком: пересечения
    var v1 = sk.AddEntity(new SkLine(new SkPoint(10, -10), new SkPoint(10, 10)));
    var v2 = sk.AddEntity(new SkLine(new SkPoint(30, -10), new SkPoint(30, 10)));
    Require(TrimTool.Trim(sk, l1, new Vector2d(20, 0)), "trim click");
    var hs = sk.Entities.OfType<SkLine>().Where(l => Math.Abs(l.A.Y) < 1e-9 && Math.Abs(l.B.Y) < 1e-9).Select(l => (Math.Min(l.A.X, l.B.X), Math.Max(l.A.X, l.B.X))).OrderBy(x => x).ToList();
    Require(hs.Count == 2 && Math.Abs(hs[0].Item2 - 10) < 1e-6 && Math.Abs(hs[1].Item1 - 30) < 1e-6, "trim between intersections: " + string.Join(" ", hs));
    // точки на кривой ограничивают кусок (как в Creo)
    var l2 = sk.AddEntity(new SkLine(new SkPoint(0, 20), new SkPoint(40, 20)));
    sk.AddEntity(new SkPoint(15, 20));
    var t2 = sk.AddEntity(new SkLine(new SkPoint(25, 20), new SkPoint(25, 30)));
    Require(TrimTool.Trim(sk, l2, new Vector2d(20, 20)), "trim between points");
    var hs2 = sk.Entities.OfType<SkLine>().Where(l => Math.Abs(l.A.Y - 20) < 1e-9 && Math.Abs(l.B.Y - 20) < 1e-9).Select(l => (Math.Min(l.A.X, l.B.X), Math.Max(l.A.X, l.B.X))).OrderBy(x => x).ToList();
    Require(hs2.Count == 2 && Math.Abs(hs2[0].Item2 - 15) < 1e-6 && Math.Abs(hs2[1].Item1 - 25) < 1e-6, "trim bounded by points on the line: " + string.Join(" ", hs2));
    // протяжка мышью: линия y = −5 пересекает нижние части v1 и v2
    w.AfterSketchEdit(sk); Run();
    int before = w.SketchUndoDepth;
    vp.Tool = new TrimTool(); Run();
    var plane = sk.Plane;
    (double, double) F(double x, double y) { var q = vp.Project(plane.ToWorld(new Vector2d(x, y)))!.Value; return (q.X / vp.Bounds.Width, q.Y / vp.Bounds.Height); }
    vp.SimulateToolStroke(F(5, -5), F(20, -5), F(35, -5)); Run();
    var verts = sk.Entities.OfType<SkLine>().Where(l => Math.Abs(l.A.X - l.B.X) < 1e-9 && (Math.Abs(l.A.X - 10) < 1e-9 || Math.Abs(l.A.X - 30) < 1e-9)).ToList();
    Require(verts.Count == 2 && verts.All(l => Math.Min(l.A.Y, l.B.Y) > -1e-6), "stroke trim: " + string.Join(" ", verts.Select(l => $"{l.A.P}-{l.B.P}")));
    Require(w.SketchUndoDepth == before + 1, $"stroke is not one undo step: {w.SketchUndoDepth} vs {before + 1}");
    undo.Execute(); Run();
    Require(sk.Entities.OfType<SkLine>().Count(l => Math.Abs(l.A.X - l.B.X) < 1e-9 && Math.Min(l.A.Y, l.B.Y) < -9) == 2, "undo of the stroke trim");
    vp.Tool = null;
    // (3) обрезка окружности — размер тот же (dN, формула)
    circ = sk.Entities.OfType<SkCircle>().First();
    dd = sk.Dimensions.First(x => x.Type == DimensionType.Diameter);
    int ix = dd.Index;
    sk.AddEntity(new SkLine(new SkPoint(40, 0), new SkPoint(80, 0)));
    Require(TrimTool.Trim(sk, circ, new Vector2d(60, 10)), "circle trim");
    Require(sk.Dimensions.Contains(dd) && dd.Items[0] is SkArc arc && dd.Index == ix && dd.Formula == "20" && Math.Abs(arc.Radius - 10) < 1e-9 && ReferenceEquals(arc.Center, dd.Items[0] is SkArc a2 ? a2.Center : null),
        $"circle dimension after trim: present {sk.Dimensions.Contains(dd)}, on {dd.Items[0].GetType().Name}, {dd.Name} formula {dd.Formula}");
    w.FinishSketch(); Run();
});

Check("UI-REORDER-K104", () =>
{
    // k104 (отзыв 19:43): перестановка операций в дереве — на разных деталях и размерах (брусок с отверстием и бобышкой,
    // уголок с пазом, пластина с отверстиями; ×0.2, ×1, ×7): каждая допустимая перестановка строится без ошибок, по
    // отметке — как с нуля, скругления и фаски — на всех своих рёбрах; перетаскивание мышью по дереву
    var bad = ReorderModels.CheckAll((p, f) => TreeCommands.MoveRange(p, f));
    Require(bad.Count == 0, $"{bad.Count} bad reorders: " + string.Join(" | ", bad.Take(6)));
    // вырез не встаёт выше первого тела
    var (_, bp) = ReorderModels.BoxHoleBoss(1);
    var cut = bp.Features[1];
    Require(TreeCommands.MoveRange(bp, cut).Lo >= 1 && TreeCommands.MoveRange(bp, bp.Features[0]).Hi == 0, $"cut above the first body: {TreeCommands.MoveRange(bp, cut)} / base {TreeCommands.MoveRange(bp, bp.Features[0])}");
    // мышью: фаска (3) — под бобышку (4)
    var (pl, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(bp));
    var s = DocumentSession.FromPart(pl, "box");
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    Run();
    var part = s.Part!;
    var ch = part.Features[3]; var boss = part.Features[4];
    var r0 = w.TreeRowInWindow(ch); var r1 = w.TreeRowInWindow(boss);
    Require(r0 != null && r1 != null, "tree rows not found");
    var a = r0!.Value.Center; var b = new Avalonia.Point(r1!.Value.Center.X, r1.Value.Bottom - 2);
    w.MouseDown(a, MouseButton.Left); Run();
    for (int i = 1; i <= 8; i++) { w.MouseMove(new Avalonia.Point(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8), RawInputModifiers.LeftMouseButton); Run(); }
    w.MouseUp(b, MouseButton.Left); Run();
    s.Document.RebuildDirty(); Run();
    part = s.Part!;
    Require(part.Features[4].Key == ch.Key && part.Features[3].Key == boss.Key, "mouse drag did not move the chamfer: " + string.Join(", ", part.Features.Select(f => f.Name)) + " toast " + w.ToastShown);
    Require(part.Features.All(f => f.Error == null), "errors after the drag: " + string.Join("; ", part.Features.Where(f => f.Error != null).Select(f => f.Error)));
    // (22:51) почему не удаляется: основная плоскость — причина в подсказке
    w.SelectItem(part.PlaneXY); Run();
    var del = CommandRegistry.Instance["edit.delete"];
    Require(!del.Enabled && del.TooltipNow().Contains(Loc.L("основные плоскости и начало координат детали не удаляются")), "delete reason: " + del.TooltipNow());
    // эскиз, которым пользуется другая операция, остаётся — с сообщением
    var s2 = part.Sketches.First(k => k.Name == "S2");
    var ex2 = new ExtrudeFeature { SketchId = s2.Id, Cut = true }; ex2.Side1.Distance = new ValueRef(5); part.AddFeature(ex2);
    s.Document.RebuildDirty(); Run();
    w.SelectItem(part.Features[1]); Run();
    del.Execute(); Run();
    Require(s.Part!.Sketches.Any(k => k.Name == "S2") && w.ToastShown.Contains("S2"), "shared sketch: " + w.ToastShown);
});

Check("UI-K103", () =>
{
    // k103 (отзыв 18:21): формулы размеров — в параметрах детали (1); подсказка после точки (3); имена в кавычках — синим /
    // красным (4); цвета и штриховка сечения из настроек (5); ручки сечения (6); подсветка, выбор и двойной щелчок (7)
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var s1 = part.AddSketch("S1", Parametric3D.Kernel.Frame3.XY, part.PlaneXY.Id);
    { var rt = new RectangleTool(); rt.Click(new SketchInput(s1, new Vector2d(0, 0), null)); rt.Click(new SketchInput(s1, new Vector2d(40, 20), null)); }
    var ex = new ExtrudeFeature { SketchId = s1.Id }; ex.Side1.Distance = new ValueRef(15); part.AddFeature(ex);
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    var vp = w.ViewPort;
    // (5) цвета крышек
    var saved = UserPrefs.Current.Look.Clone();
    try
    {
        var look = UserPrefs.Current.Look;
        look.SectionHatch = false; look.SectionFillColor = "#FF0000"; UiLook.ApplyToRender(vp.Theme);
        var (fc, hc) = Parametric3D.Render.Engine.SceneRenderer.CapColors(vp.Theme, new Vector4(0.5f, 0.5f, 0.5f, 1));
        Require(fc.X > 0.99f && fc.Y < 0.01f && hc == fc, $"section fill / hatch off: {fc} {hc}");
        look.SectionHatch = true; look.SectionHatchColor = "#00FF00"; UiLook.ApplyToRender(vp.Theme);
        (fc, hc) = Parametric3D.Render.Engine.SceneRenderer.CapColors(vp.Theme, new Vector4(0.5f, 0.5f, 0.5f, 1));
        Require(hc.Y > 0.99f && hc.X < 0.01f && fc.X > 0.99f, $"section hatch color: {hc}");
    }
    finally { UserPrefs.Current.Look = saved; UiLook.ApplyToRender(vp.Theme); }
    // (6, 7) сечение: ручки, подсветка, выбор, двойной щелчок
    vp.ZoomToFit(animate: false);
    CommandRegistry.Instance["view.section"].Execute(); Run();
    Require(w.CurrentOperation is SectionOp && vp.Gizmo is { Mode: GizmoMode.MoveRotate }, "section settings handles");
    w.CommitOperation(); Run();
    Require(vp.Gizmo == null && !vp.SectionSelected, "handles stay after OK");
    var c = vp.SectionCorners()!; var pa = vp.Project(c[0])!.Value; var pb = vp.Project(c[1])!.Value;
    var mid = new Avalonia.Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
    Require(vp.SectionHoverAt(mid) && vp.SectionHover, "section outline is not highlighted under the cursor");
    Require(!vp.SectionHoverAt(new Avalonia.Point(-500, -500)) && !vp.SectionHover, "hover stays");
    vp.RaiseSectionClicked(); Run();
    Require(vp.SectionSelected && vp.Gizmo is { Mode: GizmoMode.MoveRotate } && w.CurrentOperation == null, "click did not select the section");
    double off0 = vp.Section!.Offset;
    vp.SimulateGizmoDrag(new GizmoDrag(GizmoPart.AxisZ, new Vector3d(0, 0, 7), 0, KeyModifiers.None));
    Require(Math.Abs(vp.Section!.Offset - off0 - 7) < 1e-9, $"offset by the handle: {vp.Section.Offset}");
    vp.SimulateGizmoDrag(new GizmoDrag(GizmoPart.AxisY, Vector3d.Zero, 20, KeyModifiers.None));
    Require(Math.Abs(vp.Section!.AngleB - 20) < 1e-9 && vp.SectionSelected, $"angle B by the handle: {vp.Section.AngleB}");
    vp.RaiseSectionDoubleClicked(); Run();
    Require(w.CurrentOperation is SectionOp so && Math.Abs(so.State.AngleB - 20) < 1e-9 && !vp.SectionSelected && vp.Gizmo is { Mode: GizmoMode.MoveRotate }, "double click did not open the settings");
    w.CancelOperation(); Run();
    CommandRegistry.Instance["view.section"].Execute(); Run();
    Require(vp.SectionPlane == null, "section did not switch off");
    // (3, 4) поле формулы размера
    w.EditSketch(s1); s.Document.RebuildDirty(); Run();
    part = s.Part!;
    var sk = vp.ActiveSketch!;
    var top = sk.Entities.OfType<SkLine>().First(l => Math.Abs(l.A.Y - 20) < 1e-9 && Math.Abs(l.B.Y - 20) < 1e-9);
    var side = sk.Entities.OfType<SkLine>().First(l => Math.Abs(l.A.X - 40) < 1e-9 && Math.Abs(l.B.X - 40) < 1e-9);
    var dw = sk.AddDimension(DimensionType.DistancePointPoint, 40, top.A, top.B); dw.Locked = true;
    var dh = sk.AddDimension(DimensionType.DistancePointPoint, 20, side.A, side.B); dh.Locked = true;
    sk.Solve(s.Document.Solver); vp.RefreshDefaultDimensions();
    w.EditDimensionValue(dh); Run();
    var a = w.DimEditAssist!; var tb = w.DimEditBox!;
    void Type(string t) { tb.CaretIndex = (tb.Text ?? "").Length; tb.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = t }); Run(); }
    tb.Text = "s\"S1\""; Type(".");
    Require(tb.Text == "s\"S1\"." && a.ListOpen && a.Items.Any(i => i.Label == dw.Name + " = 40"), $"list after the dot: {tb.Text} open {a.ListOpen}: " + string.Join(" | ", a.Items.Select(i => i.Label)));
    a.Choose(a.Items.FindIndex(i => i.Insert == dw.Name));
    Require(tb.Text == "s\"S1\"." + dw.Name && !a.ListOpen, "chosen: " + tb.Text);
    tb.Text = dw.Name + " + "; Type(".");
    Require(a.ListOpen && a.Items[0].Insert.StartsWith("d"), "top-level list: " + string.Join(" | ", a.Items.Select(i => i.Label)));
    a.Choose(a.Items.FindIndex(i => i.Insert == dw.Name));
    Require(tb.Text == dw.Name + " + " + dw.Name, "top-level insert: " + tb.Text);
    tb.Text = "2"; Type(".");
    Require(!a.ListOpen, "list on a decimal point");
    tb.Text = "s\"S1\".d0 + s\"Nope\".d1"; Run();
    Require(a.Spans.Count == 2 && a.Spans[0].Ok && !a.Spans[1].Ok && a.Spans[1].Start == tb.Text.IndexOf("s\"Nope\""), "quoted names: " + string.Join(",", a.Spans));
    // 18:52: размеры — зелёным (есть), красным (нет)
    tb.Text = dw.Name + " + d99 + s\"S1\"." + dh.Name; Run();
    Require(a.DimSpans.Count == 3 && a.DimSpans[0].Ok && !a.DimSpans[1].Ok && a.DimSpans[2].Ok && a.DimSpans[2].Start == tb.Text.Length - dh.Name.Length, "dimension names: " + string.Join(",", a.DimSpans));
    tb.Text = dw.Name + " / 2";
    tb.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); Run();
    Require(dh.Formula == dw.Name + " / 2", "formula: " + dh.Formula);
    w.FinishSketch(); s.Document.RebuildDirty(); Run();
    // (1) формула размера — в параметрах детали
    var dlg = new ParametersDialog(s, part);
    int lv = Enumerable.Range(0, dlg.LevelCount).First(i => dlg.LevelTitle(i) == Loc.L("Деталь «{0}»", part.Name));
    dlg.SelectLevel(lv); dlg.Show(); Run();
    string path = "s\"S1\"." + dh.Name;
    int row = Enumerable.Range(0, dlg.RowCount).FirstOrDefault(i => dlg.RowName(i) == path, -1);
    Require(row >= 0 && dlg.RowResult(row).StartsWith("20"), "formula dimension is not in the part parameters: " + string.Join(" | ", Enumerable.Range(0, dlg.RowCount).Select(dlg.RowName)));
    dlg.SetRowValue(row, dw.Name + " / 4");
    Require(dlg.Apply(), "apply"); dlg.Close();
    s.Document.RebuildDirty(); Run();
    var dh2 = s.Part!.Sketches.First().Dimensions.First(d => d.Index == dh.Index);
    Require(dh2.Formula == dw.Name + " / 4" && Math.Abs(dh2.Value - 10) < 1e-9, $"edited in the part parameters: {dh2.Formula} = {dh2.Value}");
});

Check("UI-PARAMS-K102", () =>
{
    // k102 (отзыв 15:15): идентификаторы размеров (TAB, строка состояния, вставка щелчком в правку), формулы размеров,
    // размеры операции на виде, размеры в «Параметрах» (формулы, значок ошибки), сдвиг рёбер не больше стенки
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var s1 = part.AddSketch("S1", Parametric3D.Kernel.Frame3.XY, part.PlaneXY.Id);
    { var rt = new RectangleTool(); rt.Click(new SketchInput(s1, new Vector2d(0, 0), null)); rt.Click(new SketchInput(s1, new Vector2d(40, 20), null)); }
    var ex = new ExtrudeFeature { SketchId = s1.Id }; ex.Side1.Distance = new ValueRef(15); part.AddFeature(ex);
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    var vp = w.ViewPort;
    // (1) сдвиг рёбер: на мелком масштабе — не больше 5·10⁻⁴ габарита (стенка 1 мм — больше)
    vp.ZoomToFit(); for (int i = 0; i < 40; i++) vp.Camera.Zoom(-1, 10, 10); Run();
    Require(vp.EdgeBiasNow <= 0.05, $"edge bias at a small scale: {vp.EdgeBiasNow:0.###} mm");
    // (7) размеры операции на виде
    w.SelectItem(ex); w.StartOperation(new ExtrudeOp(ex)); Run();
    Require(w.CurrentOperation is ExtrudeOp eo && eo.ViewDims().Count() == 1 && vp.OpDims.Count == 1, "extrude depth is not shown in the view");
    var vd = vp.OpDims[0];
    Require(Math.Abs((vd.B - vd.A).Length - 15) < 1e-9 && vd.Id == "f\"" + ex.Name + "\".d0", $"extrude dim {vd.A}-{vd.B} {vd.Id}");
    w.CancelOperation(); Run();
    Require(vp.OpDims.Count == 0, "operation dims stay after the operation");
    // «Отмена» правки операции — деталь из снимка (новые объекты)
    part = s.Part!; s1 = part.Sketches.First(); ex = (ExtrudeFeature)part.Features[0];
    // эскиз: размеры, TAB, строка состояния, правка с формулой и вставкой щелчком
    w.EditSketch(s1); s.Document.RebuildDirty(); Run();
    var sk = vp.ActiveSketch!;
    var top = sk.Entities.OfType<SkLine>().First(l => Math.Abs(l.A.Y - 20) < 1e-9 && Math.Abs(l.B.Y - 20) < 1e-9);
    var side = sk.Entities.OfType<SkLine>().First(l => Math.Abs(l.A.X - 40) < 1e-9 && Math.Abs(l.B.X - 40) < 1e-9);
    // нижний левый угол — к нулю (рост ширины — вправо)
    var corner = sk.AllPoints().First(p => p.P.Length < 1e-9); sk.AddConstraint(ConstraintType.Coincident, corner, sk.OriginPoint);
    var dw = sk.AddDimension(DimensionType.DistancePointPoint, 40, top.A, top.B); dw.Locked = true;
    var dh = sk.AddDimension(DimensionType.DistancePointPoint, 20, side.A, side.B); dh.Locked = true;
    sk.Solve(s.Document.Solver); vp.RefreshDefaultDimensions();
    Require(!SketchDimensions.ShowIds, "ids shown on entering the sketch");
    w.ToggleDimensionIds();
    Require(SketchDimensions.ShowIds && SketchDimensions.Label(dw) == dw.Name, "TAB did not switch to ids");
    w.ToggleDimensionIds();
    Require(ModelViewport.DimHoverText(dw).StartsWith(dw.Name + " = "), "hover text: " + ModelViewport.DimHoverText(dw));
    w.EditDimensionValue(dh); Run();
    Require(w.DimEditBox != null, "dimension edit did not open");
    w.DimEditBox!.Text = ""; w.InsertDimensionName(dw.Name); w.DimEditBox.Text += " / 2";
    Require(w.DimEditBox.Text == dw.Name + " / 2", "inserted: " + w.DimEditBox.Text);
    w.DimEditBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
    Run();
    Require(w.DimEditBox == null && dh.Formula == dw.Name + " / 2" && dh.Locked && !dh.IsDefault && Math.Abs(dh.Value - 20) < 1e-9, $"formula dim: {dh.Formula} {dh.Value} locked {dh.Locked}");
    // d0 → 60: высота по формуле — 30
    var r = SketchEditing.SetDimensionValue(sk, dw, 60, null, s.Document.Solver); w.AfterSketchEdit(sk);
    Require(r.Ok && Math.Abs(dh.Value - 30) < 1e-9 && Math.Abs(side.B.Y - side.A.Y) is var hh && Math.Abs(hh - 30) < 1e-6, $"formula follows: {dh.Value}, side {Math.Abs(side.B.Y - side.A.Y)}");
    w.FinishSketch(); s.Document.RebuildDirty(); Run();
    // (8, 9) «Параметры»: уровень размеров детали; формула операции из размера эскиза; значок ошибки; ОК с ошибкой
    var dlg = new ParametersDialog(s, part);
    int lv = Enumerable.Range(0, dlg.LevelCount).First(i => dlg.LevelTitle(i) == Loc.L("Размеры детали «{0}»", part.Name));
    dlg.SelectLevel(lv); dlg.Show(); Run();
    int row = Enumerable.Range(0, dlg.RowCount).First(i => dlg.RowName(i) == "f\"" + ex.Name + "\".d0");
    var box = dlg.ValueBox(row)!;
    box.Focus(); box.Text = ""; Run();
    dlg.InsertFormula("s\"|S1\".d" + dw.Index);
    Require(box.Text == "s\"S1\".d" + dw.Index, "formula button insert: " + box.Text);
    dlg.SetRowValue(row, "s\"S1\".d" + dw.Index + " / 4"); Run();
    Require(dlg.RowError(row) == "" && dlg.RowResult(row).StartsWith("15"), $"result {dlg.RowResult(row)} {dlg.RowError(row)}");
    int bad = Enumerable.Range(0, dlg.RowCount).First(i => dlg.RowName(i) == "s\"S1\"." + dh.Name);
    dlg.SetRowValue(bad, "nothing + 1"); Run();
    Require(dlg.RowError(bad) != "" && dlg.ErrorShown(bad), "error icon is not shown");
    dlg.SetRowValue(bad, dw.Name + " / 2");
    Require(dlg.Apply(), "parameters with dims were not applied");
    dlg.Close();
    s.Document.RebuildDirty(); Run();
    Require(ex.Side1.Distance.Formula == "s\"S1\".d" + dw.Index + " / 4" && ex.Error == null && Math.Abs(ex.Side1.Distance.Value - 15) < 1e-9, $"extrude by a sketch dimension: {ex.Side1.Distance} err {ex.Error}");
    Require(Math.Abs(part.Volume - 60 * 30 * 15) < 1e-6 * part.Volume, $"volume {part.Volume}");
});

Check("UI-SKETCH-K101", () =>
{
    // k101 (отзыв 13:41): очерки трубы в эскизе (6), в т. ч. по сетке; совпадение через нуль — без лишней связи (3);
    // «взгляд с обратной стороны» — геометрия на месте (2); «верх» — сторона рамки или ребро основы, подсветка стрелок (1, 5),
    // сохраняется (4)
    var s = DocumentSession.NewPart(); var part = s.Part!;
    var s1 = part.AddSketch("S1", part.PlaneYZ.Frame, part.PlaneYZ.Id);
    s1.AddEntity(new SkCircle(new SkPoint(0, 0), 10)); s1.AddEntity(new SkCircle(new SkPoint(0, 0), 5));
    part.AddFeature(new ExtrudeFeature { SketchId = s1.Id });
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    w.SelectItem(part.PlaneXY); w.StartOperation(new SketchOp());
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    vp.EnterSketch(sk, animate: false); Run();
    vp.BuildOverlayForTest();
    Require(vp.SilhouetteDrawn >= 4, $"tube silhouettes: {vp.SilhouetteDrawn}");
    // очерк по сетке (для граней без аналитического очерка) — у цилиндра R10 вдоль X: линии y = ±10
    var cyl = part.Bodies.SelectMany(b => b.Faces).First(f => f.Surface is not Parametric3D.Kernel.Surfaces.PlaneSurface && Parametric3D.App.Operations.Refs.FaceCentre(part, f.Id) is { } fc && Math.Abs(Math.Sqrt(fc.Y * fc.Y + fc.Z * fc.Z)) > 7);
    var ms = SketchReferences.MeshSilhouette(sk, part.Mesh!, cyl.Id);
    Require(ms.Count >= 2 && ms.All(m => Math.Abs(Math.Abs(m.A.Y) - 10) < 0.2 && Math.Abs(Math.Abs(m.B.Y) - 10) < 0.2), "mesh silhouette: " + string.Join(", ", ms.Select(m => $"{m.A}-{m.B}")));
    // (3) совпадение через нуль
    var la = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, -30)) { Role = SkRole.Centerline });
    var lb = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(30, 0)));
    sk.AddConstraint(ConstraintType.Coincident, la.A, sk.OriginPoint);
    sk.AddConstraint(ConstraintType.Coincident, lb.A, sk.OriginPoint);
    int nc = sk.Constraints.Count;
    var again = sk.AddConstraint(ConstraintType.Coincident, la.A, lb.A);
    Require(sk.Constraints.Count == nc && again.Type == ConstraintType.Coincident, "a coincident closing a cycle was added");
    sk.Constraints.Add(new SkConstraint(ConstraintType.Coincident, la.A, lb.A));   // старый файл
    Require(sk.Solve(s.Document.Solver) != SolveStatus.Overconstrained && sk.Constraints.Count == nc, $"cycle coincident not removed: {sk.Constraints.Count} vs {nc}");
    // (2) взгляд с обратной стороны — точки на месте в мире, «горизонтально» становится «вертикально»
    var hc = sk.AddConstraint(ConstraintType.Horizontal, lb);
    var wb = sk.Plane.ToWorld(lb.B.P);
    sk.Flip();
    Require((sk.Plane.ToWorld(lb.B.P) - wb).Length < 1e-9 && hc.Type == ConstraintType.Vertical, $"flip moved the geometry: {sk.Plane.ToWorld(lb.B.P)} vs {wb}, {hc.Type}");
    sk.Flip();
    Require((sk.Plane.ToWorld(lb.B.P) - wb).Length < 1e-9 && hc.Type == ConstraintType.Horizontal, "flip twice is not identity");
    w.FinishSketch(); Run();
    // (1, 4, 5) настройка эскиза
    var sk2 = part.Sketches.Last();
    var setup = new SketchSetupOp(sk2, false);
    w.StartOperation(setup); Run();
    vp.ZoomToFit(); Run();
    var side = setup.FrameSides()[1];
    var m = vp.Project((side.A + side.B) / 2)!.Value;
    vp.HoverInterceptor!(m);
    Require(setup.Hot == 11, $"frame side under the cursor is not highlighted: {setup.Hot}");
    Require(vp.ClickInterceptor!(m), "frame side click refused");
    Require(sk2.UpDescription == "правая сторона рамки", "up: " + sk2.UpDescription);
    w.CommitOperation(); Run();
    var setup2 = new SketchSetupOp(part.Sketches.Last(), false);
    w.StartOperation(setup2); Run();
    Require(setup2.Panel!.Refs[2].Summary == Loc.L("правая сторона рамки"), "up after reopening: " + setup2.Panel!.Refs[2].Summary);
    // ребро грани-основы годится, опорная плоскость — нет
    Require(!setup2.UpCandidate(new PickRef(part.PlaneXZ.Id, PickKind.DatumPlane, part, part.PlaneXZ)), "a datum plane is accepted as «up»");
    w.CancelOperation();
});

Check("UI-SECTION-K99", () =>
{
    // k99 (отзыв 11:03): настройка эскиза показывает заданное (1), «верх» — только подходящее (2); сечение — настраиваемая
    // плоскость с контуром и значком (3), ручки по полю (4), штриховка деталей разная (5)
    var s = DocumentSession.NewPart();
    var part = s.Part!;
    var s1 = part.AddSketch("S1", Parametric3D.Kernel.Frame3.XY, part.PlaneXY.Id);
    s1.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
    part.AddFeature(new ExtrudeFeature { SketchId = s1.Id });
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    // (1, 2) настройка эскиза
    var setup = new SketchSetupOp(s1, false);
    w.StartOperation(setup); Run();
    var refs = setup.Panel!.Refs;
    Require(refs.Count == 3 && refs.All(r => r.Summary != Loc.L("не задано")), "sketch setup fields are empty: " + string.Join(" | ", refs.Select(r => r.Summary)));
    Require(refs[0].Items.Count == 1 && ReferenceEquals(refs[0].Items[0].Target, part.PlaneXY), "base plane is not the sketch's plane");
    var upPref = refs[2].Prefer;
    // k101: «верх» — только рёбра основы; плоскости не берутся
    Require(upPref != null && !upPref(new PickRef(part.PlaneXY.Id, PickKind.DatumPlane, part, part.PlaneXY)) && !upPref(new PickRef(part.PlaneXZ.Id, PickKind.DatumPlane, part, part.PlaneXZ)),
        "«up» accepts a plane parallel to the sketch or rejects a perpendicular one");
    w.CancelOperation(); Run();
    // (3) сечение
    var vp = w.ViewPort;
    CommandRegistry.Instance["view.section"].Execute(); Run();
    Require(w.CurrentOperation is SectionOp, "section command did not open the section settings");
    var op = (SectionOp)w.CurrentOperation!;
    Require(vp.SectionPlane is { } sp0 && Math.Abs(Math.Abs(sp0.N.Y) - 1) < 1e-9, "default section is not on XZ");
    var at0 = vp.SectionPlane!.Value.At;
    op.State.Offset = 5; op.FieldChanged();
    Require((vp.SectionPlane!.Value.At - at0 - vp.SectionPlane.Value.N * 5).Length < 1e-9 || (vp.SectionPlane!.Value.At - at0 + vp.SectionPlane.Value.N * 5).Length < 1e-9, "offset does not move the plane along the base normal");
    // (4) ручки по полю
    // k103: ручки сразу все — стрелка смещения и кольца углов
    Require(vp.Gizmo is { Mode: GizmoMode.MoveRotate } g0 && g0.Has(GizmoPart.AxisZ) && g0.Has(GizmoPart.AxisX) && g0.Has(GizmoPart.AxisY), "section handles");
    op.SetGizmoField(1);
    vp.SimulateGizmoDrag(new GizmoDrag(GizmoPart.AxisX, Vector3d.Zero, 30, Avalonia.Input.KeyModifiers.None));
    Require(Math.Abs(op.State.AngleA - 30) < 1e-9 && Math.Abs(Math.Abs(vp.SectionPlane!.Value.N.Y) - Math.Cos(Math.PI / 6)) < 1e-9, $"angle A by the handle: {op.State.AngleA}, n {vp.SectionPlane!.Value.N}");
    // начало: по умолчанию — центр основы; «начало основы» выключает «центр» и «точку»
    Require(op.State.Origin == SectionState.OriginKind.MidPlane && op.Panel!.Refs[1].Enabled!() == false, "origin point field must be disabled with mid-plane origin");
    // k100: «Swap normal» — нормаль в обратную сторону, плоскость на месте
    var nBefore = vp.SectionPlane!.Value.N; var atBefore = vp.SectionPlane.Value.At;
    op.State.Swap = true; op.FieldChanged();
    Require((vp.SectionPlane!.Value.N + nBefore).Length < 1e-9 && (vp.SectionPlane.Value.At - atBefore).Length < 1e-9, "swap normal did not reverse the section normal");
    op.State.Swap = false; op.FieldChanged();
    op.State.Origin = SectionState.OriginKind.BasePlane; op.FieldChanged();
    w.CommitOperation(); Run();
    Require(vp.SectionPlane != null && vp.SectionCorners() != null, "section is gone after OK");
    var badge = w.FindControl<Button>("SectionBadge")!;
    Require(badge.IsVisible, "section view badge is not shown");
    // щелчок по контуру — настройки
    var c = vp.SectionCorners()!; var pa = vp.Project(c[0])!.Value; var pb = vp.Project(c[1])!.Value;
    Require(vp.SectionOutlineHit(new Avalonia.Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2)), "outline is not hit");
    vp.RaiseSectionDoubleClicked(); Run();
    Require(w.CurrentOperation is SectionOp so2 && Math.Abs(so2.State.AngleA - 30) < 1e-9, "settings reopened without the current values");
    ((SectionOp)w.CurrentOperation!).State.Offset = 50; w.CurrentOperation!.FieldChanged();
    w.CancelOperation(); Run();
    Require(vp.Section is { } back && Math.Abs(back.Offset - 5) < 1e-9, "cancel did not restore the section");
    // (5) штриховка соседних деталей различается
    var h0 = Parametric3D.Render.Engine.SceneRenderer.HatchOf(0, true, 2);
    var h1 = Parametric3D.Render.Engine.SceneRenderer.HatchOf(1, true, 2);
    Require(h0.Step != h1.Step && h0.Angle != h1.Angle && Parametric3D.Render.Engine.SceneRenderer.HatchOf(0, false, 2).Cross, "hatch of adjacent parts / non-metal");
    CommandRegistry.Instance["view.section"].Execute(); Run();
    Require(vp.SectionPlane == null && !badge.IsVisible, "section did not switch off");
});

Check("UI-SKETCH-K98", () =>
{
    // k98 (отзыв 08:25): очерк цилиндра в эскизе виден всегда (2); подпись радиуса горизонтальна, продолжение дуги до луча
    // размера (3, 4); значок касания — у своего конца дуги (5); фаска — два размера катетов (6); подпись без контура (7);
    // «Вертикально» у почти вертикального отрезка (8); угловой размер снаружи — подпись вне угла (9); рамка — все точки
    // (10); без «Вырез выдавливанием» (11); иконки — по рисунку (13); порядок панелей эскиза (14)
    var s = DocumentSession.NewPart();
    var part = s.Part!;
    var s1 = part.AddSketch("S1", Parametric3D.Kernel.Frame3.XY, part.PlaneXY.Id);
    s1.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
    part.AddFeature(new ExtrudeFeature { SketchId = s1.Id });
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    // (11)
    var cmdIds = w.FindControl<WrapPanel>("CommandStack")!.Children.OfType<Avalonia.Controls.Primitives.TemplatedControl>().Select(b => (b.Tag as AppCommand)?.Id).ToHashSet();
    Require(!cmdIds.Contains("op.cut") && cmdIds.Contains("op.extrude"), "extruded cut button is still on the bar");
    w.SelectItem(part.PlaneXZ);
    w.StartOperation(new SketchOp());
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    vp.EnterSketch(sk, animate: false); Run();
    // (14) порядок панелей
    var tray = w.FindControl<WrapPanel>("ToolTray")!;
    var visible = tray.Children.OfType<Border>().Where(b => b.IsVisible).Select(b => b.Name).ToList();
    Require(visible.SequenceEqual(new[] { "SketchSetBar", "ShapeBar", "CommandBar", "DimBar", "SketchBar" }), "sketch panels order: " + string.Join(", ", visible));
    // (8) вид эскиза: ось U — вправо, V — вверх
    var camUp = vp.Camera.Up; var vAx = sk.Plane.V;
    Require(Vector3.Dot(camUp, new Vector3((float)vAx.X, (float)vAx.Y, (float)vAx.Z)) > 0.99f, $"sketch view up {camUp} is not the sketch V axis");
    var lv = sk.AddEntity(new SkLine(new SkPoint(30, 0), new SkPoint(31, 20)));
    vp.Selection.Clear(); vp.Selection.Select(new PickRef(lv.Id, PickKind.SketchCurve, sk, lv), additive: false);
    Require(SketchCommands.Constrain(w, ConstraintType.Vertical), "vertical refused");
    Require(Math.Abs(lv.A.X - lv.B.X) < 1e-7 && Math.Abs(lv.A.Y - lv.B.Y) > 10, $"near-vertical line after «Vertical»: {lv.A.P} {lv.B.P}");
    // (2) очерк цилиндра
    vp.BuildOverlayForTest();
    Require(vp.SilhouetteDrawn >= 2, $"cylinder silhouettes drawn: {vp.SilhouetteDrawn}");
    // (5, 6) скругление и фаска ломаной
    var p0 = new SkPoint(60, 0); var p1 = new SkPoint(100, 0); var p2 = new SkPoint(100, 40);
    var a1 = sk.AddEntity(new SkLine(p0, p1)); var a2 = sk.AddEntity(new SkLine(p1, p2));
    sk.AddConstraint(ConstraintType.Horizontal, a1); sk.AddConstraint(ConstraintType.Vertical, a2);
    int nd = sk.Dimensions.Count;
    Require(CornerTool.Apply(sk, new Vector2d(100, 0), true, 5), "chamfer refused");
    var cd = sk.Dimensions.Skip(nd).ToList();
    Require(cd.Count == 2 && cd.All(d => d.Type == DimensionType.DistancePointLine && d.IsDriving && Math.Abs(d.Value - 5) < 1e-6), $"chamfer dimensions: {string.Join(", ", cd.Select(d => $"{d.Type} {d.Value:0.##}"))}");
    Require(sk.Solve(s.Document.Solver) is SolveStatus.Ok or SolveStatus.Underconstrained, "sketch after chamfer does not solve");
    var q0 = new SkPoint(60, 60); var q1 = new SkPoint(100, 60); var q2 = new SkPoint(100, 100);
    var b1 = sk.AddEntity(new SkLine(q0, q1)); var b2 = sk.AddEntity(new SkLine(q1, q2));
    Require(CornerTool.Apply(sk, new Vector2d(100, 60), false, 8), "fillet refused");
    var fa = sk.Entities.OfType<SkArc>().Last();
    var tans = sk.Constraints.Where(c => c.Type == ConstraintType.Tangent && c.Items.Contains(fa)).ToList();
    var tps = tans.Select(c => Parametric3D.Render.ConstraintLayout.TangentPoint(c)).ToList();
    Require(tps.Count == 2 && tps.All(t => t != null) && tps.Any(t => (t!.Value - fa.Start.P).Length < 1e-6) && tps.Any(t => (t!.Value - fa.End.P).Length < 1e-6), "tangent glyph points are not at the arc ends");
    Parametric3D.Render.ConstraintLayout.Build(sk, vp.PixelSize);
    foreach (var c in tans)
        Require(c.GlyphPositions.Count == 1 && ((c.GlyphPositions[0] - fa.Start.P).Length < 40 * vp.PixelSize || (c.GlyphPositions[0] - fa.End.P).Length < 40 * vp.PixelSize), "tangent glyph is not near its arc end");
    // (3, 4) радиус скругления: луч мимо дуги — продолжение дуги; подпись горизонтальна на экране
    var rd = sk.AddDimension(DimensionType.Radius, 8, fa); rd.Locked = true;
    rd.TextOffset = new Vector2d(-1, -1) * 3;   // луч внутрь угла, мимо дуги
    SketchDimensions.Build(sk, new DimBatch(), vp.PixelSize);
    Require(Math.Abs(SketchDimensions.LastArcExtension) > 1e-3, "no arc extension for a radius pointing past the arc");
    rd.TextOffset = (fa.Start.P + fa.End.P) * 0.5 - fa.Center.P;   // луч на середину дуги
    SketchDimensions.Build(sk, new DimBatch(), vp.PixelSize);
    Require(Math.Abs(SketchDimensions.LastArcExtension) < 1e-12, "arc extension drawn for a ray on the arc");
    var tdir = SketchDimensions.LastTextDir; var sr = SketchDimensions.ScreenRight.Normalized();
    Require(Math.Abs(tdir.X * sr.Y - tdir.Y * sr.X) < 1e-9, $"radius text direction {tdir} is not horizontal on the screen ({sr})");
    // (9) угловой размер снаружи — подпись вне угла
    var l1 = sk.AddEntity(new SkLine(new SkPoint(150, 0), new SkPoint(200, 0))); var l2 = sk.AddEntity(new SkLine(new SkPoint(150, 0), new SkPoint(190, 20)));
    var ad = sk.AddDimension(DimensionType.AngleLineLine, 26.57, l1, l2);
    ad.Arrows = ArrowPlacement.Inside; SketchDimensions.Build(sk, new DimBatch(), vp.PixelSize);
    double inAt = SketchDimensions.LastAngleTextAt;
    ad.Arrows = ArrowPlacement.Outside; SketchDimensions.Build(sk, new DimBatch(), vp.PixelSize);
    double outAt = SketchDimensions.LastAngleTextAt, sweepA = Math.Atan2(20, 40);
    Require(inAt < sweepA && outAt > sweepA + 1e-3, $"angle text at {inAt:0.###} (inside) / {outAt:0.###} (outside), angle {sweepA:0.###}");
    // (7) подпись — заливка без контура
    var fill = new List<List<Vector2d>>();
    StrokeFont.LayoutFill(SketchDimensions.Label(ad), Vector2d.Zero, Vector2d.UnitX, Vector2d.UnitY, 1, 0.7 / SketchDimensions.TextHeightPx, 0, fill);
    Require(!StrokeFont.UseGost || SketchDimensions.LastTextSegments == fill.Count, $"text segments {SketchDimensions.LastTextSegments}, fill rows {fill.Count} (outline drawn over the fill)");
    // (10) рамка вокруг ломаной — и отрезки, и все их точки
    var r0 = vp.Project(sk.Plane.ToWorld(new Vector2d(55, -5)))!.Value; var r1 = vp.Project(sk.Plane.ToWorld(new Vector2d(105, 45)))!.Value;
    var inBox = vp.ItemsInRegion(new List<Avalonia.Point> { r0, new(r1.X, r0.Y), r1, new(r0.X, r1.Y) }, crossing: false);
    int pts = inBox.Count(r => r.Kind == PickKind.SketchPoint), crv = inBox.Count(r => r.Kind == PickKind.SketchCurve);
    Require(crv >= 3 && pts >= 4, $"box selection: {crv} curves, {pts} points");
    // (13) иконка с полями вписывается по рисунку
    var anyFit = new[] { "dim_linear_but.svg", "f_revolve_but.svg", "dim_angle_but.svg", "dim_radius_but.svg" }.Select(f => { IconProvider.Get(f); return IconProvider.ContentBox(f); }).ToList();
    Require(anyFit.Any(b => b != null), "no icon was fitted to its drawing");
    w.FinishSketch();
});

Check("UI-SKETCH-K97", () =>
{
    // k97 (отзыв 21:14): панель размеров (10); скругление угла ломаной (12); оси эскиза — после его элементов (13); значок
    // связи подсвечивает её элементы (8); стрелки углового размера снаружи (11); знаки ГОСТ залиты (9, 14); правка эскиза —
    // временный откат (4); очерк цилиндра под курсором (5); перестановка операции мышью — не выше родителей (3)
    var s = DocumentSession.NewPart();
    var part = s.Part!;
    var s1 = part.AddSketch("S1", Parametric3D.Kernel.Frame3.XY, part.PlaneXY.Id);
    s1.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
    var ex = new ExtrudeFeature { SketchId = s1.Id }; part.AddFeature(ex);
    var s2 = part.AddSketch("S2", Parametric3D.Kernel.Frame3.XZ, part.PlaneXZ.Id);
    foreach (var (a, b) in new[] { ((40.0, 5.0), (60.0, 5.0)), ((60.0, 5.0), (60.0, 15.0)), ((60.0, 15.0), (40.0, 15.0)), ((40.0, 15.0), (40.0, 5.0)) })
        s2.AddEntity(new SkLine(new SkPoint(a.Item1, a.Item2), new SkPoint(b.Item1, b.Item2)));
    var ex2 = new ExtrudeFeature { SketchId = s2.Id, Symmetric = true }; part.AddFeature(ex2);
    s.Document.RebuildDirty();
    Require(part.Features.All(f => f.Error == null), "setup: " + string.Join("; ", part.Features.Select(f => f.Error)));
    var topEdge = part.Bodies.SelectMany(b => b.Edges).First(e => e.V0.P.Z > 19.9 && e.V1.P.Z > 19.9 && Math.Sqrt(e.V0.P.X * e.V0.P.X + e.V0.P.Y * e.V0.P.Y) < 10.01);
    var rf = new RoundFeature { Radius = new ValueRef(1) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(topEdge)); part.AddFeature(rf);
    s.Document.RebuildDirty();
    var w = Open(s);
    void Run() { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    // (3) перестановка: скругление — не выше выдавливания, на ребре которого оно стоит; вырез — куда угодно
    var (lo, hi) = TreeCommands.MoveRange(part, rf);
    Require(lo == 1 && hi == 2, $"round move range {lo}..{hi} (expected 1..2)");
    var (lo2, hi2) = TreeCommands.MoveRange(part, ex2);
    Require(lo2 == 0 && hi2 == 2, $"second extrude move range {lo2}..{hi2}");
    Run();
    Require(w.FeatureDropIndex(rf, new Avalonia.Point(10, -500)) == 1, $"drop above everything: {w.FeatureDropIndex(rf, new Avalonia.Point(10, -500))} (expected clamp to 1)");
    Require(TreeCommands.MoveTo(w, ex2, 0) && s.Part!.Features[0].Key == ex2.Key && s.Part.Features.All(f => f.Error == null), "moving the second extrude to the top failed");
    s.Undo(); s.Document.RebuildDirty(); Run();
    part = s.Part!; var s2l = part.Sketches.First(k => k.Name == "S2");
    // (4) правка эскиза S2 — откат до его операции
    w.EditSketch(s2l);
    int exIdx = part.Features.FindIndex(f => f.ConsumedSketchIds().Contains(s2l.Id));
    Require(w.SketchRolledBack && part.StageIndex == exIdx, $"sketch edit is not rolled back: stage {part.StageIndex}, owner {exIdx}, parent {s2l.Parent?.GetType().Name}, same part {ReferenceEquals(part, w.Session.Part)}, in sketch {w.InSketch}");
    s.Document.RebuildDirty();
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    vp.EnterSketch(sk, animate: false); Run();
    // (5) очерк цилиндра (x = ±10) под курсором
    var at = vp.Project(new Vector3d(10, 0, 10))!.Value;
    var fr = vp.FaceReferenceAt(at);
    Require(!fr.IsEmpty && fr.Kind == PickKind.Face, "cylinder silhouette is not found under the cursor");
    // (13) отрезок на оси эскиза выбирается раньше оси
    var onAxis = sk.AddEntity(new SkLine(new SkPoint(-60, 0), new SkPoint(-30, 0)));
    var q = vp.Project(sk.Plane.ToWorld(new Vector2d(-45, 0)))!.Value;
    var hits = new ScenePicker().PickAll(s.Document, vp.Camera, new Vector2i((int)q.X, (int)q.Y), false, sk);
    var first = hits.FirstOrDefault(h => h.Ref.Kind is PickKind.SketchCurve or PickKind.SketchPoint);
    Require(ReferenceEquals(first.Ref.Target, onAxis), $"under the cursor first: {first.Ref.Target?.GetType().Name} {(ReferenceEquals(first.Ref.Target, sk.AxisH) ? "(axis)" : "")}");
    // проход выбора видеокарты: побеждает нарисованное позже — оси эскиза в сцене раньше его кривых
    var sd = Parametric3D.Render.Engine.SceneBuilder.Build(s.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions(), part, sk);
    int LineOf(object t) => sd.Lines.FindIndex(li => li.Pick > 0 && ReferenceEquals(sd.PickTable[(int)li.Pick].Ref.Target, t));
    Require(LineOf(sk.AxisH) >= 0 && LineOf(sk.AxisH) < LineOf(onAxis), $"scene order: axis line {LineOf(sk.AxisH)}, sketch line {LineOf(onAxis)}");
    // (8) значок связи под курсором — её элементы подсвечены
    var hc = sk.AddConstraint(ConstraintType.Horizontal, onAxis);
    vp.Selection.SetHover(new PickRef(hc.Id, PickKind.SketchConstraint, sk, hc));
    vp.BuildOverlayForTest();
    Require(vp.ConstraintHoverShown == 1, $"constraint hover highlighted {vp.ConstraintHoverShown}");
    vp.Selection.SetHover(PickRef.Empty);
    // (12) скругление угла ломаной (общая точка)
    var shared = new SkPoint(-20, 40);
    var la = sk.AddEntity(new SkLine(new SkPoint(-60, 40), shared)); var lb = sk.AddEntity(new SkLine(shared, new SkPoint(-20, 80)));
    Require(CornerTool.Apply(sk, new Vector2d(-20, 40), false, 5), "fillet refused");
    sk.Solve(s.Document.Solver);
    var arc = sk.Entities.OfType<SkArc>().Last();
    Require((arc.Start.P - arc.End.P).Length > 5 && Math.Abs(arc.Radius - 5) < 1e-6, $"fillet arc start {arc.Start.P} end {arc.End.P} r {arc.Radius}");
    Require(!ReferenceEquals(la.B, lb.A) && (la.B.P - arc.Start.P).Length < 1e-6 && (lb.A.P - arc.End.P).Length < 1e-6, "lines are not trimmed to the fillet");
    {
        // скругление угла прямоугольника — эскиз решается (не переопределён)
        var sk3 = new Sketch("r", Parametric3D.Kernel.Frame3.XY);
        var rt = new RectangleTool(); rt.Click(new SketchInput(sk3, new Vector2d(0, 0), null)); rt.Click(new SketchInput(sk3, new Vector2d(20, 10), null));
        // касание дуги скругления у общего конца — перпендикулярностью радиуса (P3D_SKTANGENTEND=0 — «переопределён»)
        var st0 = sk3.Solve(s.Document.Solver);
        Require(CornerTool.Apply(sk3, new Vector2d(20, 10), false, 3), "rectangle fillet refused");
        var st3 = sk3.Solve(s.Document.Solver);
        Require(st3 is SolveStatus.Ok or SolveStatus.Underconstrained, $"rectangle after fillet: {st3} (before {st0}); shared corners {sk3.Entities.OfType<SkLine>().Count(l1 => sk3.Entities.OfType<SkLine>().Any(l2 => l2 != l1 && (l2.A == l1.B || l2.B == l1.B || l2.A == l1.A)))}; {string.Join(", ", sk3.Constraints.Select(c => c.Type))}");
    }
    // (11) угловой размер: стрелки снаружи
    var l1 = sk.AddEntity(new SkLine(new SkPoint(100, 0), new SkPoint(140, 0))); var l2 = sk.AddEntity(new SkLine(new SkPoint(100, 0), new SkPoint(130, 25)));
    var ad = sk.AddDimension(DimensionType.AngleLineLine, 40, l1, l2);
    ad.Arrows = ArrowPlacement.Outside; SketchDimensions.Build(sk, new DimBatch(), vp.PixelSize);
    bool outA = ad.LastArrowsOutside;
    ad.Arrows = ArrowPlacement.Inside; SketchDimensions.Build(sk, new DimBatch(), vp.PixelSize);
    Require(outA && !ad.LastArrowsOutside, "angle dimension ignores the arrows inside/outside switch");
    // (9, 14) знаки ГОСТ залиты строками
    var fill = new List<List<Vector2d>>();
    StrokeFont.LayoutFill("28.14", Vector2d.Zero, Vector2d.UnitX, Vector2d.UnitY, 12, 0.7 / 12, 0.1, fill);
    Require(!StrokeFont.UseGost || fill.Count > 40, $"glyph fill rows: {fill.Count}");
    // (10) панель размеров
    var dimIds = w.FindControl<WrapPanel>("DimStack")!.Children.OfType<Avalonia.Controls.Primitives.TemplatedControl>().Select(b => (b.Tag as AppCommand)?.Id).ToHashSet();
    Require(w.FindControl<Border>("DimBar")!.IsVisible && dimIds.Contains("dim.smart") && dimIds.Contains("dim.angle") && dimIds.Contains("dim.make_weak"), "dimension bar is missing in the sketch");
    w.FinishSketch();
    Require(!w.SketchRolledBack && s.Part!.StageIndex == -1, $"rollback is not restored after the sketch: stage {s.Part!.StageIndex}");
    Require(!w.FindControl<Border>("DimBar")!.IsVisible, "dimension bar stays outside the sketch");
});

Check("UI-SKETCH-K96", () =>
{
    // k96 (отзыв 17:55): эскиз открывается без инструмента «Отрезок», заливка и открытые концы включены; полоса эскиза —
    // размеры, все связи и настройки кнопками (без «Ещё…»), «Разрешить эскиз»; связи к нулю и осям; рамка выбирает центры
    // окружностей; строка состояния называет вид связи; правка размера, противоречие — участники красным, панель
    // «Разрешить эскиз»; «Сделать слабым» и удаление размера; грань поперёк эскиза и окружность в перпендикулярной
    // плоскости — ссылочные отрезки (без точек разбиения)
    var s = DocumentSession.NewPart();
    var w = Open(s);
    var part = s.Part!;
    s.Document.RebuildDirty();
    w.SelectItem(part.PlaneXY);
    w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch mode did not start");
    var vp = w.ViewPort; var sk = vp.ActiveSketch!;
    Dispatcher.UIThread.RunJobs();
    Require(vp.Tool == null, "a drawing tool is active right after entering the sketch (п. 10)");
    Require(vp.ShowFilled && vp.ShowOpenEnds, "fill and open ends are not on by default (п. 7)");
    var stack = w.FindControl<WrapPanel>("CommandStack")!;
    var ids = stack.Children.OfType<Avalonia.Controls.Primitives.TemplatedControl>().Select(b => (b.Tag as AppCommand)?.Id).Where(x => x != null).ToHashSet();
    // k97 (замечание 10): размеры — своей панелью (DimBar) в полосе
    ids.UnionWith(w.FindControl<WrapPanel>("DimStack")!.Children.OfType<Avalonia.Controls.Primitives.TemplatedControl>().Select(b => (b.Tag as AppCommand)?.Id).Where(x => x != null)!);
    ids.UnionWith(w.FindControl<WrapPanel>("SketchSetStack")!.Children.OfType<Avalonia.Controls.Primitives.TemplatedControl>().Select(b => (b.Tag as AppCommand)?.Id).Where(x => x != null)!);
    foreach (var id in new[] { "dim.smart", "dim.linear", "dim.radius", "dim.diameter", "dim.angle", "dim.arc_length", "con.coincident", "con.point_on", "con.midpoint", "con.horizontal",
                               "con.vertical", "con.parallel", "con.perpendicular", "con.collinear", "con.tangent", "con.equal", "con.concentric", "con.symmetric", "con.fix",
                               "sk.construction", "sk.grid", "sk.snap_items", "sk.show_open_ends", "sk.show_filled", "sk.references", "sk.setup", "sk.resolve" })
        Require(ids.Contains(id), "sketch bar has no button " + id);
    Require(!stack.Children.OfType<Button>().Any(b => b.Tag is not AppCommand), "a drop-down is left on the sketch bar");
    // п. 1: связи к нулю и осям эскиза
    var l = sk.AddEntity(new SkLine(new SkPoint(5, 6), new SkPoint(30, 12)));
    vp.Selection.Clear();
    vp.Selection.Select(new PickRef(l.A.Id, PickKind.SketchPoint, sk, l.A), additive: false);
    vp.Selection.Select(new PickRef(sk.OriginPoint.Id, PickKind.SketchPoint, sk, sk.OriginPoint), additive: true);
    Require(SketchCommands.Constrain(w, ConstraintType.Coincident), "coincident with the sketch origin refused");
    vp.Selection.Clear();
    vp.Selection.Select(new PickRef(l.B.Id, PickKind.SketchPoint, sk, l.B), additive: false);
    vp.Selection.Select(new PickRef(sk.AxisH.Id, PickKind.SketchCurve, sk, sk.AxisH), additive: true);
    Require(SketchCommands.Constrain(w, ConstraintType.PointOnEntity), "point on the sketch axis refused");
    Require(l.A.P.Length < 1e-7 && Math.Abs(l.B.Y) < 1e-7, $"line {l.A.P} {l.B.P} is not on the origin and the axis");
    Require(Selection.Label(new PickRef(sk.AxisH.Id, PickKind.SketchCurve, sk, sk.AxisH)) == Loc.L("Горизонтальная ось эскиза"), "axis label");
    // п. 11: вид связи в строке состояния
    var con = sk.Constraints.First(c => c.Type == ConstraintType.PointOnEntity);
    Require(Selection.Label(new PickRef(con.Id, PickKind.SketchConstraint, sk, con)).Contains(MainWindow.ConstraintName(ConstraintType.PointOnEntity)), "constraint label has no type");
    // п. 12: значки связей перекрашиваются под тему вида
    Require(IconProvider.Get("con_horizontal_but.svg", Avalonia.Media.Color.FromRgb(232, 232, 232)) != null, "glyph icon for the view theme");
    // п. 5: рамка вокруг двух центров окружностей — выбраны центры
    var c1 = sk.AddEntity(new SkCircle(new SkPoint(60, 40), 15)); var c2 = sk.AddEntity(new SkCircle(new SkPoint(75, 40), 15));
    vp.EnterSketch(sk, animate: false);   // вид — нормально к плоскости (без кадров GL анимация не идёт)
    Dispatcher.UIThread.RunJobs();
    var pa = vp.Project(sk.Plane.ToWorld(new Vector2d(55, 35)))!.Value; var pb = vp.Project(sk.Plane.ToWorld(new Vector2d(80, 45)))!.Value;
    var box = new List<Avalonia.Point> { pa, new(pb.X, pa.Y), pb, new(pa.X, pb.Y) };
    var inBox = vp.ItemsInRegion(box, crossing: false);
    Require(inBox.Any(r => ReferenceEquals(r.Target, c1.Center)) && inBox.Any(r => ReferenceEquals(r.Target, c2.Center)), $"box selection: {inBox.Count} items, centers not selected");
    // п. 18, 15, 16: правка размера, противоречие — участники красным, панель «Разрешить эскиз»
    var rect = new RectangleTool();
    rect.Click(new SketchInput(sk, new Vector2d(-60, -40), null));
    rect.Click(new SketchInput(sk, new Vector2d(-20, -10), null));
    sk.Solve(s.Document.Solver); vp.RefreshDefaultDimensions();
    var sides = sk.Entities.OfType<SkLine>().Where(x => x.A.X < -19 && x.B.X < -19).ToList();
    var wdim = sk.Dimensions.First(d => d.Type == DimensionType.DistancePointPoint && sides.Any(x => d.Items.Contains(x.A) && d.Items.Contains(x.B)) && Math.Abs(d.Value - 40) < 1e-6);
    var r1 = SketchEditing.SetDimensionValue(sk, wdim, 50, null, s.Document.Solver);
    Require(r1.Ok && !wdim.IsDefault && wdim.Locked, "dimension edit failed");
    w.AfterSketchEdit(sk);
    var diag = sk.Dimensions.FirstOrDefault(d => d.IsDefault && d.Type == DimensionType.DistancePointPoint && Math.Abs(d.Value - 30) < 1e-6);
    Require(diag != null, "no weak height dimension");
    var r2 = SketchEditing.SetDimensionValue(sk, diag!, 30, null, s.Document.Solver);
    Require(r2.Ok, "height did not become strong");
    // ширина 50, высота 30: ещё один сильный размер — диагональ 10 невозможна
    var corner = sides.SelectMany(x => new[] { x.A, x.B }).Distinct().ToList();
    var pA = corner.OrderBy(p => p.X + p.Y).First(); var pB = corner.OrderBy(p => p.X + p.Y).Last();
    var dd = sk.AddDimension(DimensionType.DistancePointPoint, (pA.P - pB.P).Length, pA, pB); dd.Reference = true;
    var r3 = SketchEditing.SetDimensionValue(sk, dd, 10, null, s.Document.Solver);
    Require(!r3.Ok && r3.Constraints.Count + r3.Dimensions.Count > 0, $"conflict participants: ok {r3.Ok}, {r3.Constraints.Count}+{r3.Dimensions.Count}");
    Require(Math.Abs(dd.Value - Math.Sqrt(50 * 50 + 30 * 30)) < 1e-6 && dd.Reference, $"rejected value kept: {dd.Value}");
    w.ReportSketchConflict(sk, r3, "test", () => SketchEditing.SetDimensionValue(sk, dd, 10, null, s.Document.Solver));
    Require(r3.Constraints.All(c => c.Flagged) && r3.Dimensions.All(d => d.Flagged), "conflicting items are not shown red");
    w.ToggleResolvePanel();
    Require(w.ResolvePanelOpen && w.FindControl<TextBlock>("PropsTitle")!.Text == Loc.L("РАЗРЕШИТЬ ЭСКИЗ"), "resolve panel did not open");
    w.ToggleResolvePanel();
    Require(!w.ResolvePanelOpen && !sk.Constraints.Any(c => c.Flagged) && !sk.Dimensions.Any(d => d.Flagged), "resolve panel did not close / flags stay");
    // п. 2, 17: «Сделать слабым», удалить размер
    vp.Selection.Clear();
    vp.Selection.Select(new PickRef(wdim.Id, PickKind.SketchDimension, sk, wdim), additive: false);
    CommandRegistry.Instance["dim.make_weak"].Execute();
    Require(sk.Dimensions.Contains(wdim) && wdim.IsDefault && !wdim.Locked, "make weak: the needed width did not stay weak");
    vp.Selection.Clear();
    vp.Selection.Select(new PickRef(dd.Id, PickKind.SketchDimension, sk, dd), additive: false);
    CommandRegistry.Instance["dim.delete"].Execute();
    Require(!sk.Dimensions.Contains(dd), "the reference dimension was not deleted");
    // п. 13: окружность в плоскости, перпендикулярной эскизу, — отрезок между крайними точками
    var circ = new Parametric3D.Kernel.Curves.ArcCurve3(new Parametric3D.Kernel.Frame3(new Vector3d(0, 70, 0), Vector3d.UnitX, Vector3d.UnitZ), 12, 0, 2 * Math.PI);
    var proj = EdgeProjection.Project(sk, circ, new EdgeProjection.PointPool(SkRole.Reference, "t"), SkRole.Reference, "t");
    Require(proj.Count == 1 && proj[0] is SkLine pl && Math.Abs(pl.Length - 24) < 1e-6, $"perpendicular circle projected as {proj.Count} element(s)");
    w.CancelSketch();
    w.Close();
    // п. 14: боковая грань бруска (поперёк эскиза на верхней грани) — ссылочный отрезок; верхняя (параллельна) — нет
    var bp = Parametric3D.App.Diagnostics.TestParts.Block("k96");
    var bs = DocumentSession.FromPart(bp, "k96");
    var bw = Open(bs);
    bs.Document.RebuildDirty();
    var topF = bp.Bodies[0].Faces.First(f => Refs.FacePlane(bp, f.Id) is { } fp && fp.N.Z > 0.99);
    var sideF = bp.Bodies[0].Faces.First(f => Refs.FacePlane(bp, f.Id) is { } fp && Math.Abs(fp.N.X) > 0.99);
    bw.ViewPort.Selection.Select(new PickRef(topF.Id, PickKind.Face, bp, null), additive: false);
    bw.StartOperation(new SketchOp());
    var bsk = bw.ViewPort.ActiveSketch!;
    Require(SketchReferences.FaceProjection(bsk, sideF).Count == 1 && SketchReferences.FaceProjection(bsk, topF).Count == 0, "face projection");
    Require(bw.ViewPort.FaceReference(new PickRef(sideF.Id, PickKind.Face, bp, null)) != null, "side face is not a reference candidate under the cursor");
    Require(SketchReferences.AddFace(bsk, bp, sideF.Id) == null && bsk.Entities.Any(e => e is SkLine { Role: SkRole.Reference }), "face reference not added");
    bw.CancelSketch();
    bw.Close();
    // цилиндр (ось Z): эскиз на XZ — две образующие очерка x = ±10 высотой 40; эскиз на XY — окружность R10
    var cylB = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 10, 40);
    var wall = cylB.Faces.First(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Cylinder);
    var skXZ = new Sketch("xz", new Parametric3D.Kernel.Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ));
    var sil = SketchReferences.FaceProjection(skXZ, wall);
    Require(sil.Count == 2 && sil.All(x => !x.Circle && Math.Abs(Math.Abs(x.A.X) - 10) < 1e-6 && Math.Abs((x.B - x.A).Length - 40) < 1e-6), $"cylinder silhouettes: {sil.Count}");
    var cir = SketchReferences.FaceProjection(new Sketch("xy", Parametric3D.Kernel.Frame3.XY), wall);
    Require(cir.Count == 1 && cir[0].Circle && Math.Abs(cir[0].R - 10) < 1e-9, "cylinder along the sketch normal is not a circle");
});

Check("UI-SKETCH-REFERENCES-DIMS", () =>
{
    // п. 2.3–2.4: ребро детали — ссылка эскиза; размеры по умолчанию определяют эскиз полностью (степеней свободы 0)
    var part = Parametric3D.App.Diagnostics.TestParts.Block("refs");
    var s = DocumentSession.FromPart(part, "refs");
    var w = Open(s);
    s.Document.RebuildDirty();
    var face = part.Bodies[0].Faces.First(f => Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    w.ViewPort.Selection.Select(new PickRef(face.Id, PickKind.Face, part, null), additive: false);
    w.StartOperation(new SketchOp());
    Require(w.InSketch, "sketch did not start on the top face");
    var sk = w.ViewPort.ActiveSketch!;
    var edge = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-6 && Math.Abs(e.V1.P.Z - 20) < 1e-6);
    Require(SketchReferences.Add(sk, part, new PickRef(edge.Id, PickKind.Edge, part, null)) == null, "reference not added");
    Require(sk.Entities.Any(e => e.Role == SkRole.Reference), "no reference entity");
    var rect = new RectangleTool();
    rect.Click(new SketchInput(sk, new Vector2d(-10, -5), null));
    rect.Click(new SketchInput(sk, new Vector2d(10, 5), null));
    sk.Solve(s.Document.Solver);
    w.ViewPort.RefreshDefaultDimensions();
    // размеры по умолчанию — «слабые» (как в Creo): показывают, чем определена фигура; заданные вместе — 0 степеней свободы
    var defaults = sk.Dimensions.Where(d => d.IsDefault).ToList();
    foreach (var d in defaults) d.Locked = true;
    s.Document.Solver.Solve(sk, null, out int dof);
    foreach (var d in defaults) d.Locked = false;
    Require(dof == 0, $"sketch is not fully defined: {dof} degrees of freedom left");
    Require(defaults.Any(d => d.Items.Any(i => i.Role == SkRole.Reference || i is SkPoint { Owner.Role: SkRole.Reference } || (i is SkPoint sp && sp.Role == SkRole.Reference))), "no dimension to the reference edge");
    Require(sk.Dimensions.Count(d => d.IsDefault) >= 4, $"default dimensions: {sk.Dimensions.Count}");
    w.CancelSketch();
    Require(!part.Sketches.Contains(sk), "cancelled new sketch is still in the part");
    w.Close();
});

Check("UI-CANCEL-KEEPS-PART", () =>
{
    // п. 2: «Отмена» новой операции не заменяет деталь (на экране не пропадает) и не оставляет шага отмены
    var s = DocumentSession.Sample();
    var w = Open(s);
    var part = s.Part!;
    int f0 = part.Features.Count, u0 = s.UndoNames.Count;
    w.ViewPort.Selection.Select(new PickRef(part.Bodies[0].Edges.First().Id, PickKind.Edge, part, null), additive: false);
    w.StartOperation(new RoundOp());
    w.CancelOperation();
    Require(ReferenceEquals(s.Part, part), "the part was replaced on cancel");
    Require(part.Features.Count == f0 && s.UndoNames.Count == u0 && !s.CanRedo, "cancel left traces");
    w.Close();
});

Check("UI-CTRL-TOGGLE", () =>
{
    // п. 7: Ctrl+щелчок — добавить, повторно — убрать
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    vp.Show.Planes = false; vp.MarkDirty();
    vp.Camera.State.Rotation = ModelViewport.HomeRotation;
    vp.ZoomToFit(animate: false);
    var c = vp.TranslatePoint(new Point(vp.Bounds.Width / 2 + 40, vp.Bounds.Height / 2 - 60), w)!.Value;
    var c2 = vp.TranslatePoint(new Point(vp.Bounds.Width / 2 - 60, vp.Bounds.Height / 2 + 60), w)!.Value;
    w.MouseMove(c); w.MouseDown(c, MouseButton.Left); w.MouseUp(c, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    int n1 = vp.Selection.Count;
    w.MouseMove(c2); w.MouseDown(c2, MouseButton.Left, RawInputModifiers.Control); w.MouseUp(c2, MouseButton.Left, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
    int n2 = vp.Selection.Count;
    w.MouseDown(c2, MouseButton.Left, RawInputModifiers.Control); w.MouseUp(c2, MouseButton.Left, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
    int n3 = vp.Selection.Count;
    Require(n1 >= 1 && n2 > n1 && n3 == n1, $"ctrl toggle: {n1} → {n2} → {n3}");
    w.Close();
});

void BuildNow(DocumentSession ses)
{
    for (int k = 0; k < 20; k++)
    {
        var t = ses.Document.RebuildDirtyAsync();
        while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!ses.Document.AllParts().Any(p => p.IsDirty)) break;
    }
    Dispatcher.UIThread.RunJobs();
}

Check("UI-HANDLE-CONTOUR", () =>
{
    // п. 6, предпросмотр контуром: пока тянут — деталь не меняется, панель закрыта; одна ручка: внутрь — скругление,
    // через начальное место наружу — фаска; отпустили — одна операция; повторно — та же; Esc — следов нет
    UserPrefs.Current.PreviewMode = "contour";
    try
    {
        var s = DocumentSession.Sample();
        var w = Open(s);
        var vp = w.ViewPort;
        var part = s.Part!;
        int f0 = part.Features.Count, v0 = part.Version;
        var edge = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3);
        vp.Selection.Select(new PickRef(edge.Id, PickKind.Edge, part, null), additive: false);
        Dispatcher.UIThread.RunJobs();
        Require(vp.Handle != null, "no handle on the edge");
        vp.SimulateHandleDrag(-3, end: false);
        Require(w.CurrentOperation is RoundOp, "inward drag did not start a round");
        int segRound = vp.GhostSegments;
        vp.SimulateHandleDrag(2, end: false);
        Require(w.CurrentOperation is ChamferOp, "outward past the rest did not switch to a chamfer");
        vp.SimulateHandleDrag(-4.5, end: false);
        Require(w.CurrentOperation is RoundOp, "back inward did not switch to a round");
        Require(part.Features.Count == f0 && part.Version == v0, "part changed while dragging (contour mode)");
        // контур: две границы и торцы — у одного прямого ребра 2 + 2·8 отрезков
        Require(vp.GhostShown && vp.GhostSegments <= 2 + 2 * 8 && segRound > 0, $"outline expected, segments {vp.GhostSegments}");
        Require(!w.OperationPanelVisible, "panel opened while the properties panel is closed");
        vp.SimulateHandleDrag(-5, end: true);
        Require(part.Features.Count == f0 + 1 && part.Features[^1] is RoundFeature rf && Math.Abs(rf.Radius.Value - 5) < 1e-9, "round not added with the dragged value");
        var op = w.CurrentOperation;
        vp.SimulateHandleDrag(-6, end: true);
        Require(ReferenceEquals(op, w.CurrentOperation) && ((RoundFeature)part.Features[^1]).Radius.Value == 6, "second drag did not continue the operation");
        w.CancelOperation();
        Require(part.Features.Count == f0 && !vp.GhostShown && s.UndoNames.Count == 0, "cancel left traces");
        w.Close();
    }
    finally { UserPrefs.Current.PreviewMode = "full"; }
});

Check("UI-HANDLE-LIVE", () =>
{
    // полный предпросмотр: операция в детали сразу, считается по ходу (грубо, без проверок); сторона меняет операцию с теми
    // же рёбрами; отпустили — окончательный расчёт обычной точности; Enter — один шаг отмены
    UserPrefs.Current.PreviewMode = "full";
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    var part = s.Part!;
    BuildNow(s);
    int f0 = part.Features.Count;
    var edge = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3);
    vp.Selection.Select(new PickRef(edge.Id, PickKind.Edge, part, null), additive: false);
    Dispatcher.UIThread.RunJobs();
    vp.SimulateHandleDrag(-3, end: false);
    Require(w.CurrentOperation is RoundOp && part.Features.Count == f0 + 1, "live round not added while dragging");
    Require(ReferenceEquals(s.Document.PreviewPart, part), "preview part not set");
    BuildNow(s);
    Require(part.Features[^1].Error == null && part.Bodies.Count == 1, "live round failed: " + part.Features[^1].Error);
    int triPreview = part.Mesh?.Indices.Length ?? 0;
    vp.SimulateHandleDrag(4, end: false);
    Require(w.CurrentOperation is ChamferOp && part.Features.Count == f0 + 1 && part.Features[^1] is ChamferFeature, "outward did not switch to chamfer");
    BuildNow(s);
    var ch = (ChamferFeature)part.Features[^1];
    Require(ch.Error == null && ch.Edges.Count == 1 && Math.Abs(ch.D1.Value - 4) < 1e-9, $"live chamfer: edges {ch.Edges.Count}, error {ch.Error}");
    vp.SimulateHandleDrag(-7, end: false);
    BuildNow(s);
    var rf = (RoundFeature)part.Features[^1];
    Require(rf.Error == null && rf.Edges.Count == 1, "switch back to round lost the edge");
    vp.SimulateHandleDrag(-7, end: true);
    // 21.10: панель операции ещё открыта — предпросмотр продолжается (быстрый, приближённый); точный расчёт — при «ОК»
    // (P3D_OPPREVIEW=0 — прежнее: окончательный расчёт сразу после отпускания)
    if (Environment.GetEnvironmentVariable("P3D_OPPREVIEW") != "0") Require(ReferenceEquals(s.Document.PreviewPart, part), "operation preview ended on release (panel still open)");
    else Require(s.Document.PreviewPart == null, "preview part left after release");
    BuildNow(s);
    Require(rf.Error == null && Math.Abs(rf.Radius.Value - 7) < 1e-9, "final round");
    w.CommitOperation();
    Require(s.Document.PreviewPart == null, "preview part left after commit");
    BuildNow(s);
    Require(rf.Error == null && part.Bodies.Count == 1 && !part.Bodies[0].IsPreview && Math.Abs(part.Volume - (Parametric3D.Kernel.Topology.AnalyticVolume.Of(part.Bodies[0]) ?? double.NaN)) < 1e-6 * Math.Max(1, part.Volume), "exact build after commit: " + rf.Error);
    int triFinal = part.Mesh?.Indices.Length ?? 0;
    Console.WriteLine($"  preview tris {triPreview / 3}, final tris {triFinal / 3}");
    Require(part.Features.Count == f0 + 1 && s.UndoNames.Count == 1, $"commit: features {part.Features.Count - f0}, undo {s.UndoNames.Count}");
    w.Close();
});

Check("UI-OP-PREVIEW-FAST", () =>
{
    // 21.10 (запрос 15:30): пока панель операции открыта — быстрый приближённый предпросмотр (сетка грубее, объём по сетке,
    // проверки, аудит и окончательная разметка отложены); «ОК» — точный расчёт. FreeForm: цилиндр ∪ шар, скругление R5 ребра
    // пересечения. P3D_OPPREVIEW=0 / P3D_FASTPREVIEW=0 — прежнее.
    for (int rep = 0; rep < 2; rep++) { // второй проход — тёплый замер
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    void Add(Feature f) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs(); }
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(15), B = new ValueRef(40) });
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(20) });
    Add(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(8), Dz = new ValueRef(5) });
    Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
    string Kind(Parametric3D.Kernel.Topology.TEdge e) => string.Join("/", e.CoEdges.Select(c => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(c.Loop.Face)?.GetType().Name).OrderBy(x => x));
    var e = part.Bodies[0].Edges.First(x => Kind(x) == "ExtrudedSurface/RevolvedSurface" && !ReferenceEquals(x.V0, x.V1));
    w.ViewPort.Selection.Clear(); w.ViewPort.Selection.Select(new PickRef(e.Id, PickKind.Edge, part, null), additive: false);
    long fb0 = Part.FastPreviewBodies;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    CommandRegistry.Instance["ff.fillet"].Execute();
    s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    long tPreview = sw.ElapsedMilliseconds;
    var f = (Feature?)w.CurrentOperation?.EditedFeature;
    Require(f != null && f.Error == null && part.Bodies.Count == 1, "preview round failed: " + f?.Error);
    bool on = Environment.GetEnvironmentVariable("P3D_OPPREVIEW") != "0";
    if (on)
    {
        Require(ReferenceEquals(s.Document.PreviewPart, part), "the open operation is not previewed");
        Require(Part.FastPreviewBodies > fb0, "the preview was not the fast one (exact volume and checks)");
    }
    sw.Restart();
    w.CommitOperation(); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    long tExact = sw.ElapsedMilliseconds;
    Require(s.Document.PreviewPart == null, "preview part left after commit");
    Require(f!.Error == null && part.Bodies.Count == 1 && part.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic && !part.Bodies[0].IsPreview, "exact result after commit: " + f.Error);
    double va = Parametric3D.Kernel.Topology.AnalyticVolume.Of(part.Bodies[0]) ?? double.NaN;
    Require(Math.Abs(part.Volume - va) < 1e-6 * va, $"part volume {part.Volume} vs exact {va} after commit");
    Console.WriteLine($"  {(rep == 0 ? "cold" : "warm")}: preview {tPreview} ms, exact after commit {tExact} ms");
    w.Close();
    }
});

void RimBall(bool cutter)
{
    // 21.10 (снимок пользователя 17:40): цилиндр ∪ шар, скругление R5 нижней кромки цилиндра (дуга торца, концы на шаре).
    // Прежде: резец по дуге с продлением за концы врезался в шар (выемка; точный расчёт — отказ, в предпросмотре без проверки —
    // испорченное тело), предпросмотр шёл 4–6 с. Теперь резец продлевается, пока сечение не уйдёт в шар, и из него вычитается
    // шар; предпросмотр — рез по сеткам. Мутации P3D_ARCBALLTRIM=0, P3D_PREVIEWCSG=0.
    // 22.10: по умолчанию — восстановление тела как цилиндр ∪ шар, скругление всей кромки цилиндра прямым построителем и
    // объединение с шаром (точная форма, P3D_REBUILDROUND=0 — резцы); проверяются оба пути.
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    void Add(Feature f) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs(); }
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(15), B = new ValueRef(40) });
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(20) });
    Add(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(8), Dz = new ValueRef(5) });
    Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
    double v0 = part.Volume;
    string Kind(Parametric3D.Kernel.Topology.TEdge e) => string.Join("/", e.CoEdges.Select(c => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(c.Loop.Face)?.GetType().Name).OrderBy(x => x));
    var e = part.Bodies[0].Edges.First(x => Kind(x) == "ExtrudedSurface/PlaneSurface" && !ReferenceEquals(x.V0, x.V1));
    w.ViewPort.Selection.Clear(); w.ViewPort.Selection.Select(new PickRef(e.Id, PickKind.Edge, part, null), additive: false);
    long trims0 = Parametric3D.Kernel.Ops.FilletOp.ArcBallTrims, csg0 = Parametric3D.Kernel.Ops.FilletOp.PreviewCsgCuts;
    long abort0 = Parametric3D.Kernel.Ops.BooleanOp.MeshEarlyAborts, reb0 = Parametric3D.Kernel.Ops.FilletOp.RebuildRounds;
    CommandRegistry.Instance["ff.fillet"].Execute();
    s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    var op = w.CurrentOperation!; var f = (Feature)op.EditedFeature!;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    op.DragValue!(5); op.FieldChanged(); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    long tPreview = sw.ElapsedMilliseconds;
    Require(f.Error == null && part.Bodies.Count == 1, "preview R5 failed: " + f.Error);
    double vPreview = part.Volume;
    if (cutter) Require(Parametric3D.Kernel.Ops.FilletOp.PreviewCsgCuts > csg0, "the preview cut was not made on the meshes");
    sw.Restart();
    w.CommitOperation(); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    long tExact = sw.ElapsedMilliseconds;
    Require(f.Error == null && part.Bodies.Count == 1 && !part.Bodies[0].IsPreview, "exact R5 failed: " + f.Error);
    double removed = v0 - part.Volume;
    if (cutter)
    {
        Require(Parametric3D.Kernel.Ops.FilletOp.ArcBallTrims > trims0, "the cutter was not trimmed by the sphere");
        // ожидаемо (Монте-Карло по формулам): 158 мм³ + недобор гранения ~120 мм³; выемка в шаре снимала бы больше
        Require(removed > 100 && removed < 330, $"removed volume {removed:0.#} (expected ≈ 160–280)");
    }
    else
    {
        Require(Parametric3D.Kernel.Ops.FilletOp.RebuildRounds > reb0, "the round was not made on the rebuilt cylinder");
        Require(part.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic, $"guarantee {part.Bodies[0].Guarantee}");
        // Монте-Карло по формулам (4·10⁶ точек): 157.97 мм³
        Require(Math.Abs(removed - 158.0) < 2.0, $"removed volume {removed:0.###} (expected 158 ± 2)");
    }
    Require(Math.Abs(vPreview - part.Volume) < 0.01 * part.Volume, $"preview volume {vPreview:0.#} vs exact {part.Volume:0.#}");
    // шар не тронут: точка шара у продолжения кромки внутри шара (под торцом цилиндра, вне цилиндра) — материал
    var tris = Parametric3D.Kernel.Voxels.Occupancy.Triangles(Parametric3D.Kernel.Tess.Tessellator.Tessellate(part.Bodies[0], facets: true));
    foreach (double ang in new[] { 80.0, 90.0, 110.0, -90.0 })
    {
        double a = ang * Math.PI / 180;
        var q = new OpenTK.Mathematics.Vector3d(8 + 15.5 * Math.Cos(a), 15.5 * Math.Sin(a), 5.5);   // у кромки, внутри шара
        if (q.Length > 19.5) continue;
        Require(Parametric3D.Kernel.Voxels.PointInSolid.Test(tris, q), $"the sphere is grooved at {q}");
    }
    // видимая сетка граней многогранного тела (деталь её не строит; показ — многоугольниками формы): одна грань не строится —
    // остальные не считаются (P3D_MESHEARLYABORT=0 — прежнее)
    var swM = System.Diagnostics.Stopwatch.StartNew();
    if (cutter)
    {
        Parametric3D.Kernel.Ops.BooleanOp.MeshExactlySolid(part.Bodies[0]);
        Require(Parametric3D.Kernel.Ops.BooleanOp.MeshEarlyAborts > abort0, "the face meshing went on after a face failed");
    }
    Console.WriteLine($"  {(cutter ? "cutter" : "rebuild")}: preview {tPreview} ms (volume {vPreview:0.#}), exact after commit {tExact} ms (volume {part.Volume:0.#}, removed {removed:0.#}), face meshing {swM.ElapsedMilliseconds} ms");
    w.Close();
}
Check("UI-ROUND-RIM-BALL", () =>
{
    RimBall(cutter: false);
    Parametric3D.Kernel.Ops.FilletOp.RebuildRoundOff = true;
    try { RimBall(cutter: true); }
    finally { Parametric3D.Kernel.Ops.FilletOp.RebuildRoundOff = Environment.GetEnvironmentVariable("P3D_REBUILDROUND") == "0"; }
});

Check("UI-ROUND-CAP-SPHERE", () =>
{
    // 22.10 (снимок 17:40, «займись вогнутым ребром»): цилиндр ∪ шар, скругление R5 вогнутого ребра «нижний торец цилиндра —
    // шар». Прежде: точный расчёт — отказ (у шара скругления точка касания с торцом вне лунки торца), резец — 40–70 граней-щепок,
    // 8–11 с. Теперь заполнитель угла — тело вращения вокруг оси шара (торец — тор катящегося шара — шар), обрезанное
    // продолжением цилиндра, и объединение с телом общим точным путём. Мутации P3D_CAPSPHEREROUND=0, P3D_FSDUPPIECE=0,
    // P3D_REVOLVEHINT=0; предпросмотр — обе булевы по сеткам (P3D_CAPSPHEREPREVIEW=0).
    var s = FreeFormCommands.NewFreeForm(); var w = Open(s); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    void Add(Feature f) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs(); }
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(15), B = new ValueRef(40) });
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(20) });
    Add(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(8), Dz = new ValueRef(5) });
    Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
    double v0 = part.Volume;
    string Kind(Parametric3D.Kernel.Topology.TEdge e) => string.Join("/", e.CoEdges.Select(c => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(c.Loop.Face)?.GetType().Name).OrderBy(x => x));
    var e = part.Bodies[0].Edges.First(x => Kind(x) == "PlaneSurface/RevolvedSurface");
    w.ViewPort.Selection.Clear(); w.ViewPort.Selection.Select(new PickRef(e.Id, PickKind.Edge, part, null), additive: false);
    long n0 = Parametric3D.Kernel.Ops.FilletOp.CapSphereRounds, p0 = Parametric3D.Kernel.Ops.FilletOp.CapSpherePreviews;
    CommandRegistry.Instance["ff.fillet"].Execute();
    s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    var op = w.CurrentOperation!; var f = (Feature)op.EditedFeature!;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    op.DragValue!(5); op.FieldChanged(); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    long tPreview = sw.ElapsedMilliseconds;
    Require(f.Error == null && part.Bodies.Count == 1, "preview R5 failed: " + f.Error);
    double vPreview = part.Volume;
    Require(Parametric3D.Kernel.Ops.FilletOp.CapSpherePreviews > p0, "the preview was not made on meshes by the filler");
    sw.Restart();
    w.CommitOperation(); s.Document.RebuildDirty(); Dispatcher.UIThread.RunJobs();
    long tExact = sw.ElapsedMilliseconds;
    Require(f.Error == null && part.Bodies.Count == 1 && !part.Bodies[0].IsPreview, "exact R5 failed: " + f.Error);
    Require(Parametric3D.Kernel.Ops.FilletOp.CapSphereRounds > n0, "the concave edge was not rounded by the filler of revolution");
    Require(part.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic, $"guarantee {part.Bodies[0].Guarantee}");
    double added = part.Volume - v0;
    // Монте-Карло по формулам (4·10⁶ точек): 152.9 мм³; точный объём заполнителя 152.75
    Require(Math.Abs(added - 152.8) < 1.5, $"added volume {added:0.###} (expected 152.8 ± 1.5)");
    int nf = part.Bodies[0].Faces.Count();
    Require(nf <= 6, $"{nf} faces (expected: top, wall, sphere, blend — no slivers)");
    Require(Math.Abs(vPreview - part.Volume) < 0.01 * part.Volume, $"preview volume {vPreview:0.#} vs exact {part.Volume:0.#}");
    var tris = Parametric3D.Kernel.Voxels.Occupancy.Triangles(Parametric3D.Kernel.Tess.Tessellator.Tessellate(part.Bodies[0], facets: true));
    // материал заполнителя под торцом вне шара; свободно — внутри шара скругления
    Require(Parametric3D.Kernel.Voxels.PointInSolid.Test(tris, new OpenTK.Mathematics.Vector3d(21, 0, 4.5)), "no filler under the cap");
    Require(!Parametric3D.Kernel.Voxels.PointInSolid.Test(tris, new OpenTK.Mathematics.Vector3d(22.5, 0, 1)), "material inside the rolling ball");
    Console.WriteLine($"  preview {tPreview} ms (volume {vPreview:0.#}), exact after commit {tExact} ms (added {added:0.###}), faces {nf}");
    w.Close();
});

Check("UI-POLY-FAST", () =>
{
    // 21.10 (скорость многогранного пути): тор ∪ брусок с касанием в седловой точке (точный маршрут отказывает — разбиение
    // пространства). Деталь не размечает грани заранее (показ — многоугольниками формы, запомненными булевой), объём — по ним
    // (double), рациональный объём — один раз; крышки локальной булевой — по дереву тела, построенному лишь где нужно.
    // Мутации P3D_POLYLAZYMESH=0, P3D_CSGMEMO=0, P3D_APPROXVOL=0, P3D_EXACTVOLMEMO=0, P3D_LAZYCAPS=0.
    (double Vol, int Faces, long Ms, Part P) Build()
    {
        var s = FreeFormCommands.NewFreeForm(); var part = s.Part!;
        var node = part.Features.OfType<FreeFormFeature>().Single();
        void Add(Feature f) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); }
        Add(new FFPrimitiveFeature { Shape = FFPrimitive.Torus, A = new ValueRef(20), B = new ValueRef(5) });
        Add(new FFPrimitiveFeature { Shape = FFPrimitive.Box, A = new ValueRef(10), B = new ValueRef(10), C = new ValueRef(20), Origin = new OpenTK.Mathematics.Vector3d(20, 0, -10) });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
        return (part.Volume, part.Bodies.Sum(b => b.Faces.Count()), sw.ElapsedMilliseconds, part);
    }
    long lazy0 = Part.PolyLazyMeshSkips, memo0 = Parametric3D.Kernel.Exact.ExactAdapter.CsgMemoHits, appr0 = Parametric3D.Kernel.Exact.ExactAdapter.ApproxVolumes, caps0 = Parametric3D.Kernel.Exact.ExactBsp.LazyCapsRuns;
    var (v1, f1, ms1, p1) = Build();
    var b1 = p1.Bodies.Single();
    Require(b1.Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactPolyhedral && b1.Exact != null && b1.ExactAuthoritative, $"expected the polyhedral route, got {b1.Guarantee}");
    Require(Part.PolyLazyMeshSkips > lazy0, "the part meshed the faces of the polyhedral body in advance");
    Require(Parametric3D.Kernel.Exact.ExactAdapter.CsgMemoHits > memo0, "the display recomputed the exact polygons");
    Require(Parametric3D.Kernel.Exact.ExactAdapter.ApproxVolumes > appr0, "the part volume was taken in rational arithmetic");
    Require(Parametric3D.Kernel.Exact.ExactBsp.LazyCapsRuns > caps0, "the caps of the local boolean built the whole body tree");
    double vx = b1.Exact!.Solid.VolumeMm3().ToDouble();
    long vh0 = Parametric3D.Kernel.Exact.ExactSolid.VolumeMemoHits;
    Require(b1.Exact.Solid.VolumeMm3().ToDouble() == vx && Parametric3D.Kernel.Exact.ExactSolid.VolumeMemoHits > vh0, "the exact volume is not remembered");
    Require(Math.Abs(v1 - vx) <= 1e-9 * vx, $"part volume {v1:R} vs exact {vx:R}");
    var md = p1.Mesh!;
    Require(Parametric3D.Kernel.Tess.MeshData.FloatDefects(md) == 0, "the display mesh has defects");
    Require(Math.Abs(Parametric3D.Kernel.Tess.MassProps.Volume(md) - vx) < 1e-3 * vx, "display mesh volume differs from the body");
    // крышки: ленивое дерево = полное дерево (тот же объём и те же грани)
    Parametric3D.Kernel.Exact.ExactBsp.LazyCapsOff = true;
    try
    {
        var (v2, f2, ms2, _) = Build();
        Require(Math.Abs(v2 - v1) <= 1e-9 * v1 && f2 == f1, $"lazy caps changed the result: volume {v1:R} vs {v2:R}, faces {f1} vs {f2}");
        Console.WriteLine($"  union {ms1} ms (eager caps: {ms2} ms), volume {v1:0.###}, faces {f1}, display tris {md.Indices.Length / 3}");
    }
    finally { Parametric3D.Kernel.Exact.ExactBsp.LazyCapsOff = Environment.GetEnvironmentVariable("P3D_LAZYCAPS") == "0"; }
});

Check("UI-MESH-FAST", () =>
{
    // 21.10 (скорость разметки): целый тор — правильной сеткой по точкам шва (P3D_TORUSGRID=0); доводка — точкой у середины стороны с наибольшим отходом (P3D_UVEDGEREFINE=0).
    long tg0 = Parametric3D.Kernel.Tess.ExactFaceMesher.TorusGrids, er0 = Parametric3D.Kernel.Tess.UvMesher.EdgeRefines;
    (Part P, double Exact) Make(params Feature[] fs)
    {
        var s = FreeFormCommands.NewFreeForm(); var part = s.Part!;
        var node = part.Features.OfType<FreeFormFeature>().Single();
        foreach (var f in fs) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); }
        return (part, Parametric3D.Kernel.Topology.AnalyticVolume.Of(part.Bodies[0]) ?? double.NaN);
    }
    var (tor, tv) = Make(new FFPrimitiveFeature { Shape = FFPrimitive.Torus, A = new ValueRef(20), B = new ValueRef(5) });
    Require(Parametric3D.Kernel.Tess.ExactFaceMesher.TorusGrids > tg0, "the whole torus was not meshed by the seam grid");
    var tm = tor.Mesh!;
    Require(Parametric3D.Kernel.Tess.MeshData.FloatDefects(tm) == 0, "the torus mesh has defects");
    Require(Math.Abs(Parametric3D.Kernel.Tess.MassProps.Volume(tm) - tv) < 5e-3 * tv, $"torus mesh volume {Parametric3D.Kernel.Tess.MassProps.Volume(tm):0.#} vs {tv:0.#}");
    var (cc, cv) = Make(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(10), B = new ValueRef(40) },
        new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(7), B = new ValueRef(50), Origin = new OpenTK.Mathematics.Vector3d(-25, 0, 20), U = OpenTK.Mathematics.Vector3d.UnitY, V = OpenTK.Mathematics.Vector3d.UnitZ },
        new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
    Require(cc.Bodies.Count == 1 && cc.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic, "cyl ∪ cyl not exact");
    Require(Parametric3D.Kernel.Tess.UvMesher.EdgeRefines > er0, "refinement did not split long sides");
    var cm = cc.Mesh!;
    Require(Parametric3D.Kernel.Tess.MeshData.FloatDefects(cm) == 0, "cyl ∪ cyl mesh has defects");
    Require(Math.Abs(Parametric3D.Kernel.Tess.MassProps.Volume(cm) - cv) < 5e-3 * cv, $"cyl ∪ cyl mesh volume {Parametric3D.Kernel.Tess.MassProps.Volume(cm):0.#} vs {cv:0.#}");
    Console.WriteLine($"  torus tris {tm.Indices.Length / 3}, cyl ∪ cyl tris {cm.Indices.Length / 3}");
});

Check("UI-PREVIEW-MESHBOOL", () =>
{
    // 21.10 (скорость предпросмотра): булева, ушедшая с точного маршрута на разбиение пространства (тор ∪ брусок с касанием в
    // седловой точке), в предпросмотре идёт по сеткам показа — тело только для вида; точный расчёт — после. Мутация
    // P3D_PREVIEWMESHBOOL=0.
    var s = FreeFormCommands.NewFreeForm(); var part = s.Part!;
    var node = part.Features.OfType<FreeFormFeature>().Single();
    void Add(Feature f) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); s.Document.RebuildDirty(); }
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Torus, A = new ValueRef(20), B = new ValueRef(5) });
    Add(new FFPrimitiveFeature { Shape = FFPrimitive.Box, A = new ValueRef(10), B = new ValueRef(10), C = new ValueRef(20), Origin = new OpenTK.Mathematics.Vector3d(20, 0, -10) });
    var u = new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } };
    u.FreeForm = node.Key; u.Frozen = true; part.AddFeature(u);
    long m0 = Parametric3D.Kernel.Ops.BooleanOp.PreviewMeshBooleans;
    var ks = Parametric3D.Kernel.KernelSettings.Capture();
    ks = ks with { Preview = true, LivePreview = true, Chord = ks.Chord * s.Document.PreviewCoarseness };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Part.BuildResult res;
    using (new Parametric3D.Kernel.KernelContext(ks).Enter()) res = part.Compute(null, null);
    long tPrev = sw.ElapsedMilliseconds;
    Require(res.Errors.GetValueOrDefault(u) == null && res.Bodies.Count == 1, "preview union failed: " + res.Errors.GetValueOrDefault(u));
    Require(Parametric3D.Kernel.Ops.BooleanOp.PreviewMeshBooleans > m0, "the preview boolean was not made on the meshes");
    sw.Restart(); s.Document.RebuildDirty(); long tExact = sw.ElapsedMilliseconds;
    Require(u.Error == null && part.Bodies.Count == 1 && part.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactPolyhedral, "exact union failed: " + u.Error);
    Require(Math.Abs(res.Volume - part.Volume) < 1e-2 * part.Volume, $"preview volume {res.Volume:0.#} vs exact {part.Volume:0.#}");
    Console.WriteLine($"  preview {tPrev} ms (volume {res.Volume:0.##}), exact {tExact} ms (volume {part.Volume:0.##})");
});

Check("UI-HOLE-ON-FACE", () =>
{
    // п. 3 (u10): отверстие на выбранной грани — одно отверстие в середине грани, без ошибки
    foreach (bool before in new[] { true, false })
    {
        var s = DocumentSession.Sample();
        var w = Open(s);
        var vp = w.ViewPort;
        var part = s.Part!;
        BuildNow(s);
        int f0 = part.Features.Count;
        double v0 = part.Volume;
        var top = part.Bodies[0].Faces.First(f => Parametric3D.App.Operations.Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
        var pick = new PickRef(top.Id, PickKind.Face, part, null);
        if (before) vp.Selection.Select(pick, additive: false);
        w.StartOperation(new HoleOp());
        if (!before)
        {
            Require(part.Features.Count == f0, "the hole was added before its entry face was chosen");
            vp.Selection.Select(pick, additive: false); Dispatcher.UIThread.RunJobs();
        }
        BuildNow(s);
        var h = part.Features[^1] as HoleFeature;
        Console.WriteLine($"  before={before}: features +{part.Features.Count - f0}, points {h?.Points.Count}, error {h?.Error}, dir {h?.Direction}, volume {v0:0} -> {part.Volume:0}");
        Require(h != null && h.Error == null && h.Points.Count == 1 && part.Volume < v0 - 1, $"hole failed (before={before}): {h?.Error}");
        Require(Math.Abs(h!.Points[0].X) < 1e-6 && Math.Abs(h.Points[0].Y) < 1e-6, $"hole not in the middle of the face: {h.Points[0]}");
        w.CommitOperation();
        w.Close();
    }
});

Check("UI-SHELL-HANDLE", () =>
{
    // п. 1 (u10): у оболочки ручка — внутрь стенка внутрь (толщина +), наружу — стенка наружу (толщина −); одна операция
    UserPrefs.Current.PreviewMode = "full";
    var s = DocumentSession.Sample();
    var w = Open(s);
    var vp = w.ViewPort;
    var part = s.Part!;
    BuildNow(s);
    var top = part.Bodies[0].Faces.First(f => Parametric3D.App.Operations.Refs.FacePlane(part, f.Id) is { } pl && pl.N.Z > 0.99);
    vp.Selection.Select(new PickRef(top.Id, PickKind.Face, part, null), additive: false);
    Dispatcher.UIThread.RunJobs();
    w.StartOperation(new ShellOp());
    Dispatcher.UIThread.RunJobs();
    Require(vp.Handle != null, "shell started by command has no handle");
    var op = w.CurrentOperation;
    vp.SimulateHandleDrag(-5, end: false);
    BuildNow(s);
    var sf = (ShellFeature)part.Features[^1];
    Require(ReferenceEquals(op, w.CurrentOperation) && Math.Abs(sf.Thickness.Value - 5) < 1e-9 && sf.Error == null, $"inward: thickness {sf.Thickness.Value}, error {sf.Error}");
    double vIn = part.Volume;
    vp.SimulateHandleDrag(3, end: true);
    BuildNow(s);
    Require(ReferenceEquals(op, w.CurrentOperation) && Math.Abs(sf.Thickness.Value + 3) < 1e-9 && sf.Error == null, $"outward: thickness {sf.Thickness.Value}, error {sf.Error}");
    Console.WriteLine($"  shell volumes: inward 5 mm {vIn:0}, outward 3 mm {part.Volume:0}");
    Require(part.Volume > 100000 && Math.Abs(part.Volume - vIn) > 1000, "outward shell volume");
    w.CommitOperation();
    w.Close();
});

// u12 (замечания 1, 2): переменный радиус и переменная фаска на цепочке «ребро — дуга R30 — ребро» — плавно по всей длине,
// точная лента (закон по длине цепочки), без резца и гранения
Check("UI-VAR-CHAIN", () =>
{
    foreach (bool ch in new[] { false, true })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var r30 = new RoundFeature { Radius = new ValueRef(30), Name = "R30" }; r30.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(r30); p.Rebuild(null);
        var arc = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 30) < 1e-6);
        var chain = Parametric3D.App.Operations.Topo.TangentChain(arc, Parametric3D.App.Operations.Topo.AllEdges(p));
        if (chain.Count != 3) throw new Exception($"chain of {chain.Count}");
        Feature f;
        if (ch) { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(10), D2 = new ValueRef(10), SizeEnd = new ValueRef(20) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
        else { var rf = new RoundFeature { Radius = new ValueRef(10), RadiusEnd = new ValueRef(20) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
        p.AddFeature(f); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        if (f.Error != null) throw new Exception((ch ? "chamfer: " : "round: ") + f.Error);
        string want = ch ? "direct variable chamfer" : "radius law";
        if (!p.Warnings.Any(w => w.Contains(want))) throw new Exception("not the exact path: " + string.Join(" | ", p.Warnings));
        if (p.Warnings.Any(w => w.Contains("Geometry downgrade"))) throw new Exception("faceted faces: " + string.Join(" | ", p.Warnings));
        Console.WriteLine($"  variable {(ch ? "chamfer" : "round")} 10→20 on line–arc–line: {sw.Elapsed.TotalMilliseconds:0} ms, faces {p.Bodies.Sum(b => b.Faces.Count())}");
    }
});

// u12 (замечание 8): оболочка с открытой лентой скругления (кривая грань): полость выходит сквозь ленту; закрытая оболочка
// куба со скруглением R20 и стенкой 2 — полость по сектору ленты (прежде весь цилиндр R18 отсекал полость: 116 000 вместо 878 000)
Check("UI-SHELL-OPEN-ROUND", () =>
{
    foreach (bool open in new[] { true, false })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var rf = new RoundFeature { Radius = new ValueRef(20) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        double v0 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        var face = p.Bodies.SelectMany(b => b.Faces).First(f => f.Tag.Contains("round:face"));
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(2) };
        if (open) { sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); }
        p.AddFeature(sh); p.Rebuild(null);
        if (sh.Error != null) throw new Exception($"open {open}: {sh.Error}");
        double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        Console.WriteLine($"  shell 2 (open round {open}): volume {v:F1} of {v0:F1}; {p.Warnings.LastOrDefault(w => w.StartsWith("Shell: wall"))}");
        if (!(v < 0.15 * v0)) throw new Exception($"open {open}: the cavity is too small (volume {v:F1} of {v0:F1})");
        if (open && !p.Warnings.Any(w => w.Contains("1 face(s) left open") || w.Contains("open"))) throw new Exception("the round face did not open");
    }
});

// u12 (замечание 11): кромка (касательный стык) — не ребро: края ленты скругления — кромки, рёбра куба — нет
Check("UI-SEAM-EDGES", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3);
    if (Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam(top)) throw new Exception("a cube edge taken for a seam");
    var rf = new RoundFeature { Radius = new ValueRef(20) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => f.Tag.Contains("round:face"));
    var seams = face.Edges.Where(Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam).Count();
    var all = p.Bodies.SelectMany(b => b.Edges).Count(Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam);
    if (seams != 2 || all != 2) throw new Exception($"seams on the round {seams}, in the body {all} (want 2 and 2)");
    Console.WriteLine($"  seams: {all} (the two lines along the round)");
});

// u12a (замечание по файлу Part2): куб 100, скругление R80, оболочка 2 (открыт торец), скругление R60 / фаска 40 внутреннего
// ребра у закрытого торца — прибавка не выходит сквозь тонкую стенку (прежде снаружи на скруглении R80 торчал уголок)
Check("UI-SHELL-INNER-BLEND", () =>
{
    foreach (bool ch in new[] { false, true })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
        var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
        var rf = new RoundFeature { Radius = new ValueRef(80) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        var cyl = p.Bodies.SelectMany(b => b.Faces).Select(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)) as Parametric3D.Kernel.Geometry.Ssi.Cylinder).First(c => c != null)!;
        var roundPts = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Cylinder)
            .Edges.SelectMany(e => new[] { e.V0!.P, e.V1!.P }).ToList();
        double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
        var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(2) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
        var ie = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.X + 48) < 1e-6 && Math.Abs(e.V1!.P.X + 48) < 1e-6 && Math.Abs(e.V0.P.Z + 48) < 1e-6 && Math.Abs(e.V1.P.Z + 48) < 1e-6);
        Feature f2 = ch ? new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(40), D2 = new ValueRef(40) } : new RoundFeature { Radius = new ValueRef(60) };
        if (f2 is ChamferFeature cf2) cf2.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ie)); else ((RoundFeature)f2).Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ie));
        p.AddFeature(f2); p.Rebuild(null);
        if (f2.Error != null) throw new Exception((ch ? "chamfer: " : "round: ") + f2.Error);
        // ни одна вершина тела не выходит за наружную стенку скругления R80
        var ax = cyl.Axis.Normalized(); double worst = 0;
        foreach (var v in p.Bodies.SelectMany(b => b.Vertices))
        {
            var d = v.P - cyl.P; var rad = d - ax * Vector3d.Dot(d, ax);
            if (rad.Length > cyl.R + 1e-6) worst = Math.Max(worst, rad.Length - cyl.R);
        }
        // вершины за цилиндром законны только в стороне от скругления (в углу куба нет материала) — проверка по сектору ленты
        // угол скругления — габарит его ленты поперёк оси (до оболочки): за цилиндром внутри этого угла материала быть не должно
        var rb = Parametric3D.Kernel.Bounds3.Empty;
        foreach (var q in roundPts) rb.Add(q);
        bool InCorner(Vector3d q) { for (int k = 0; k < 3; k++) { if (Math.Abs(ax[k]) > 0.5) continue; if (q[k] <= rb.Min[k] + 1e-3 || q[k] >= rb.Max[k] - 1e-3) return false; } return true; }
        var outside = p.Bodies.SelectMany(b => b.Vertices).Where(v => { var d = v.P - cyl.P; var rad = d - ax * Vector3d.Dot(d, ax); return rad.Length > cyl.R + 1e-6 && InCorner(v.P); }).ToList();
        Console.WriteLine($"  inner {(ch ? "chamfer 40" : "round R60")} after shell: vertices beyond the R80 wall {outside.Count}");
        if (outside.Count > 0) throw new Exception($"material beyond the outer round wall at {outside[0].P}");
    }
});

// u12b (файл пользователя Part3): куб 100, скругление R40, фаска 15 на цепочке с дугой, оболочка НАРУЖУ на грани — толщина
// больше 3.65 отказывала: образующая смещённого конуса фаски не продлевалась, интеграл объёма грани шёл по чужой области
Check("UI-SHELL-OUT-CONE", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "data", "shell_out_cone.p3d");
    foreach (double t in new[] { -3.66, -8.0, -16.0 })
    {
        var (part, pars) = DocumentStorage.Load(path);
        var sh = part.Features.OfType<ShellFeature>().Single(); sh.Thickness = new ValueRef(t);
        part.Invalidate(); part.Rebuild(pars);
        var w = part.Warnings.FirstOrDefault(x => x.StartsWith("Shell")) ?? "";
        Console.WriteLine($"  outward shell {t}: {(sh.Error ?? w)}");
        if (sh.Error != null || !w.Contains("exact")) throw new Exception($"shell {t}: {sh.Error ?? w}");
    }
});

// u12b: оболочка внутрь толще скругления (R4 цепочкой «ребро — дуга R24 — ребро», стенка 5.31): смещённый тор и цилиндры
// исчезают — их полосы заостряются, полость — смещение острого тела. Проверка объёма: стенка тела со скруглением R4 = стенка
// без него минус снятый скруглением объём (полость у обоих одна и та же)
Check("UI-SHELL-SHARPEN", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "data", "shell_sharpen.p3d");
    double Vol(Part q) => q.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    foreach (double t in new[] { 5.31, 8.0 })
    {
        var (p1, pr1) = DocumentStorage.Load(path); var sh1 = p1.Features.OfType<ShellFeature>().Single(); sh1.Thickness = new ValueRef(t);
        p1.Invalidate(); p1.Rebuild(pr1);
        if (sh1.Error != null) throw new Exception($"shell {t}: {sh1.Error} {string.Join(" ", p1.Warnings.Where(w => w.StartsWith("Shell")))}");
        double wall = Vol(p1);
        var r4 = p1.Features.OfType<RoundFeature>().Single(r => Math.Abs(r.Radius.Value - 4) < 1e-9);
        p1.RemoveFeature(sh1); p1.Rebuild(pr1); double bodyR = Vol(p1);
        p1.RemoveFeature(r4); p1.Rebuild(pr1); double bodyS = Vol(p1);
        p1.AddFeature(sh1); p1.Rebuild(pr1); double wallS = Vol(p1);
        double want = wallS - (bodyS - bodyR);
        Console.WriteLine($"  shell {t} over R4: wall {wall:F4}, sharp wall {wallS:F4} − removed {bodyS - bodyR:F4} = {want:F4}");
        if (Math.Abs(wall - want) > 1e-6 * want) throw new Exception($"wall {wall} vs {want}");
    }
});

// u12b: куб 100, скругление R5 ребра x = 50, z = 50, стенка НАРУЖУ 2.82 с открытой гранью x = 50 (касательной к скруглению).
// Прежде: «ребро не кончается в смещённых вершинах» — точка касания уходила по открытой плоскости на нижний из двух корней.
// Объём стенки — по формуле: наружное тело (сечение 102.82 × 105.64 без угла у цилиндра R7.82) минус куб без угла R5
Check("UI-SHELL-OUT-TANGENT", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    var edge = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.X - xMax) < 1e-6 && Math.Abs(e.V1!.P.X - xMax) < 1e-6 && Math.Abs(e.V0.P.Z - zMax) < 1e-6 && Math.Abs(e.V1.P.Z - zMax) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(5) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); p.AddFeature(rf); p.Rebuild(null);
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
    var sh = new ShellFeature("Shell") { Thickness = new ValueRef(-2.82) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
    if (sh.Error != null) throw new Exception(sh.Error + " " + string.Join(" ", p.Warnings.Where(w => w.StartsWith("Shell"))));
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    // угол у цилиндра R7.82 (ось — 5 от краёв): прямоугольник 5 × 7.82 минус часть круга в нём
    double R = 7.82, inCircle = 0; int n = 200000;
    for (int i = 0; i < n; i++) { double x = (i + 0.5) / n * 5; inCircle += Math.Min(7.82, Math.Sqrt(Math.Max(R * R - x * x, 0))) * 5 / n; }
    double aOut = 102.82 * 105.64 - (5 * 7.82 - inCircle), aBody = 10000 - (25 - Math.PI * 25 / 4);
    double want = aOut * 105.64 - aBody * 100;
    Console.WriteLine($"  outward 2.82 at a tangent open face: volume {v:F4}, formula {want:F4}");
    if (Math.Abs(v - want) > 1e-3) throw new Exception($"volume {v} vs {want}");
});

// u12b: куб 100, скругление R20 одного нижнего ребра (y = 50, z = −50), оболочка 5 с открытым низом: открытая грань касается
// скругления по одному ребру и встречает боковины под углом по трём. Полость — тело, смещённое внутрь целиком, продлённое
// сквозь образ дна (у касания — стенка y = 30 вдоль нормали, у острых рёбер — продолжения смещённых боковин). Объём — по формуле
Check("UI-SHELL-OPEN-MIXED", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double yMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Y, e.V1!.P.Y));
    double zMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Z, e.V1!.P.Z));
    var edge = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Y - yMax) < 1e-6 && Math.Abs(e.V1!.P.Y - yMax) < 1e-6 && Math.Abs(e.V0.P.Z - zMin) < 1e-6 && Math.Abs(e.V1.P.Z - zMin) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(20) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge)); p.AddFeature(rf); p.Rebuild(null);
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && Math.Abs(pl.N.Z) > 0.99 && Math.Abs(pl.P.Z - zMin) < 1e-6);
    var sh = new ShellFeature("Shell") { Thickness = new ValueRef(5) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
    var w = p.Warnings.FirstOrDefault(x => x.StartsWith("Shell")) ?? "";
    if (sh.Error != null) throw new Exception(sh.Error + " " + w);
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    double body = 1e6 - 100 * (400 - Math.PI * 400 / 4);
    double inner = 90 * (8100 - (225 - Math.PI * 225 / 4)), plug = 90 * 75 * 5;
    double want = body - inner - plug;
    Console.WriteLine($"  shell 5, open bottom (one tangent edge): volume {v:F4}, formula {want:F4}; {w}");
    if (Math.Abs(v - want) > 1e-3) throw new Exception($"volume {v} vs {want}");
});

// u12 (замечание 15): зеркало ОПЕРАЦИИ — скругление верхнего ребра куба R20 отражается на противоположное ребро (прежде
// отражалось и объединялось тело: скругление заполнялось, деталь становилась кубом без ошибки)
Check("UI-MIRROR-OPS", () =>
{
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
    var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
    double v0 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    var rf = new RoundFeature { Radius = new ValueRef(20), Name = "R20" }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf);
    p.Rebuild(null); double v1 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    var bb = p.Bodies[0].Bounds; var c = (bb.Min + bb.Max) / 2;
    var mf = new MirrorFeature("Mirror") { At = c, Normal = Vector3d.UnitY }; mf.Sources.Add(rf.Key); p.AddFeature(mf);
    var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
    if (rf.Error != null || mf.Error != null) throw new Exception($"errors: {rf.Error} / {mf.Error}");
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN), want = v0 - 2 * (v0 - v1);
    if (!(Math.Abs(v - want) < 1e-6 * want)) throw new Exception($"volume {v:F3}, want {want:F3}");
    Console.WriteLine($"  mirror of a round: {sw.Elapsed.TotalMilliseconds:0} ms, volume {v:F3} (want {want:F3})");
});

// u12 (замечание 18): массив ОПЕРАЦИИ — вырез Ø10 в кубе со скруглением R30 повторяется 3 раза; прежде массив копировал всё
// тело (три смещённых куба объединялись, перестроение не кончалось)
Check("UI-PATTERN-OPS", () =>
{
    foreach (double step in new[] { 15.0, 5.0 })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var r30 = new RoundFeature { Radius = new ValueRef(30), Name = "R30" }; r30.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(r30);
        var sk = Parametric3D.App.Diagnostics.TestParts.CircleSketch(p, zMax, 10, 0, 5);
        var cut = new ExtrudeFeature("Cut") { SketchId = sk.Id, Cut = true }; cut.Side1.Distance = new ValueRef(20); cut.Side1.Flip = true; p.AddFeature(cut);
        var lp = new LinearPatternFeature("Pattern") { Step = new Vector3d(step, 0, 0), Count = new ValueRef(3) }; lp.Sources.Add(cut.Key); p.AddFeature(lp);
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null); double ms = sw.Elapsed.TotalMilliseconds;
        if (cut.Error != null || lp.Error != null) throw new Exception($"step {step}: errors {cut.Error} / {lp.Error}");
        double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        double want = 1e6 - (1 - Math.PI / 4) * 900 * 100 - 3 * Math.PI * 25 * 20;
        Console.WriteLine($"  pattern of a cut (step {step}): {ms:0} ms, volume {v:F3}{(step >= 10 ? $" (want {want:F3})" : "")}, bodies {p.Bodies.Count}");
        if (step >= 10 && !(Math.Abs(v - want) < 0.5)) throw new Exception($"volume {v:F3}, want {want:F3}");
        if (ms > 20000) throw new Exception($"step {step}: {ms:0} ms");
    }
});

// u11: открытое скругление вогнутого прямого ребра между плоскостями, концы — на наклонных плоских торцах: лента — точный
// цилиндр (а не таблица сечений), края на торцах — эллипсы; объём — как у пути сечений (315690.4877073967), время — доли секунды
Check("UI-OPEN-CYL", () =>
{
    var p = Parametric3D.App.Diagnostics.TestParts.Realistic(4, 30); p.Rebuild(null);
    var at = new Vector3d(71.68894768445239, 30.120455190595457, 10);
    var e0 = p.Bodies.SelectMany(b => b.Edges).OrderBy(e => Math.Min((e.V0!.P - at).Length, (e.V1!.P - at).Length)).First();
    var rf = new RoundFeature { Radius = new ValueRef(2.1) };
    foreach (var c in Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p))) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c));
    p.AddFeature(rf); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null); double ms = sw.Elapsed.TotalMilliseconds;
    if (rf.Error != null) throw new Exception("round failed: " + rf.Error);
    if (!p.Warnings.Any(w => w.Contains("direct blend"))) throw new Exception("not the direct route: " + string.Join(" | ", p.Warnings));
    var cyl = p.Bodies.SelectMany(b => b.Faces).Where(f => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.CylinderSurface cs && Math.Abs(cs.Radius - 2.1) < 1e-9).ToList();
    if (Environment.GetEnvironmentVariable("P3D_OPENCYL") != "0" && cyl.Count == 0) throw new Exception("blend is not an exact cylinder");
    double v = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    if (!(Math.Abs(v - 315690.4877073967) < 1e-6)) throw new Exception($"volume {v:R}");
    Console.WriteLine($"  open round R2.1: {ms:0} ms, cylinder faces {cyl.Count}, volume {v:F6}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellcurved")
Check("BENCH-SHELL-CURVED", () =>
{
    // u12 (замечание 8): оболочка с открытой кривой гранью (лента скругления R20)
    foreach (double t in new[] { 2.0, 5.0, -3.0 })
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
        var rf = new RoundFeature { Radius = new ValueRef(20), Name = "R20" }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        var face = p.Bodies.SelectMany(b => b.Faces).First(f => f.Tag.Contains("round:face"));
        var c = Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value;
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(t) }; sh.OpenFaces.Add(c); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh);
        Parametric3D.Kernel.Ops.DirectRound.Debug = null;
        Parametric3D.Kernel.Ops.ShellOp.ShellDebug = Environment.GetEnvironmentVariable("P3D_SHDBG") == "1" ? m => Console.WriteLine("    sh: " + m) : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        Console.WriteLine($"  shell t {t}: {sw.Elapsed.TotalMilliseconds:0} ms error {sh.Error} volume {p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):F1}");
        foreach (var w in p.Warnings.TakeLast(3)) Console.WriteLine("    warn " + (w.Length > 250 ? w[..250] : w));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellout")
Check("BENCH-SHELL-OUT", () =>
{
    // замечание: куб 100, скругление ребра R40, фаска 15 на дуге скругления, оболочка наружу с открытой гранью
    foreach (double t in (Environment.GetEnvironmentVariable("P3D_T") ?? "-3.6,-3.67,-5").Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)))
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
        double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
        double xMin0 = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.X, e.V1!.P.X));
        var top = Environment.GetEnvironmentVariable("P3D_VERT") == "1"
            ? p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.X - xMin0) < 1e-6 && Math.Abs(e.V1!.P.X - xMin0) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6)
            : p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
        var rf = new RoundFeature { Radius = new ValueRef(40) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
        double xMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.X, e.V1!.P.X));
        var arc = Environment.GetEnvironmentVariable("P3D_VERT") == "1"
            ? p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 40) < 1e-6 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6)
            : Environment.GetEnvironmentVariable("P3D_SEAM") == "1"
            ? p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - (yMin + 40)) < 1e-6)
            : p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 40) < 1e-6 && Math.Abs(e.V0!.P.X - xMin) < 1e-6);
        var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(15), D2 = new ValueRef(15) };
        if (Environment.GetEnvironmentVariable("P3D_CHAIN") == "1") foreach (var ce in Parametric3D.App.Operations.Topo.TangentChain(arc, Parametric3D.App.Operations.Topo.AllEdges(p))) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ce));
        else cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(arc));
        p.AddFeature(cf);
        if (Environment.GetEnvironmentVariable("P3D_CHDBG") == "1") Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (!m.StartsWith("face ")) Console.WriteLine("    chdr: " + (m.Length > 300 ? m[..300] : m)); };
        p.Rebuild(null); Parametric3D.Kernel.Ops.DirectRound.Debug = null;
        if (rf.Error != null || cf.Error != null) throw new Exception($"{rf.Error} / {cf.Error}");
        if (Environment.GetEnvironmentVariable("P3D_SAVE") is { } sv0) Parametric3D.Model.DocumentStorage.Save(sv0, p);
        foreach (var w in p.Warnings) Console.WriteLine("    pre " + (w.Length > 200 ? w[..200] : w));
        double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
        var face = Environment.GetEnvironmentVariable("P3D_VERT") == "1"
            ? p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.Y < -0.99)
            : p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(t) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh);
        Parametric3D.Kernel.Ops.ShellOp.ShellDebug = Environment.GetEnvironmentVariable("P3D_SHDBG") == "1" ? m => Console.WriteLine("    sh: " + m) : null;
        Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_SHDBG") == "1" ? m => { if (!m.StartsWith("face ")) Console.WriteLine("    dr: " + (m.Length > 300 ? m[..300] : m)); } : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        Console.WriteLine($"  shell t {t}: {sw.Elapsed.TotalMilliseconds:0} ms error {sh.Error} volume {p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):F1}");
        foreach (var w in p.Warnings.TakeLast(3)) Console.WriteLine("    warn " + (w.Length > 400 ? w[..400] : w));
        Parametric3D.Kernel.Ops.DirectRound.Debug = null; Parametric3D.Kernel.Ops.ShellOp.ShellDebug = null;
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellfuzz")
Check("BENCH-SHELL-FUZZ", () =>
{
    // u12b: «как пользователь» — куб 100, 1–3 скругления/фаски случайных рёбер (с касательной цепочкой), затем оболочка
    // случайной толщины внутрь или наружу с открытой гранью (плоской или кривой) или без
    var rnd = new Random(int.Parse(Environment.GetEnvironmentVariable("P3D_FUZZSEED") ?? "3"));
    int trials = int.Parse(Environment.GetEnvironmentVariable("P3D_TRIALS") ?? "60");
    var classes = new Dictionary<string, (int N, double Ms, string Ex)>();
    int fails = 0; double total = 0;
    for (int t = 0; t < trials; t++)
    {
        var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
        var log = new List<string>();
        int nOps = 1 + rnd.Next(3);
        bool preOk = true;
        for (int k = 0; k < nOps; k++)
        {
            var sharp = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve != null && Parametric3D.App.Operations.Topo.FacesOf(p, e).Count() == 2).Where(e =>
            {
                var fs = Parametric3D.App.Operations.Topo.FacesOf(p, e).ToList(); var c = e.Curve!; var mid = c.PointAt(0.5 * (c.T0 + c.T1));
                return Math.Abs(Vector3d.Dot(Parametric3D.App.Operations.Topo.FaceNormal(p, fs[0], mid), Parametric3D.App.Operations.Topo.FaceNormal(p, fs[1], mid))) < 0.995;
            }).ToList();
            if (sharp.Count == 0) break;
            var e0 = sharp[rnd.Next(sharp.Count)];
            var chain = Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p));
            bool round = rnd.NextDouble() < 0.6; double size = Math.Round(3 + rnd.NextDouble() * 40);
            Feature f;
            if (round) { var rf = new RoundFeature { Radius = new ValueRef(size) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
            else { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(size), D2 = new ValueRef(size) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
            p.AddFeature(f); p.Rebuild(null);
            if (f.Error != null) { p.RemoveFeature(f); p.Rebuild(null); continue; }
            log.Add($"{(round ? "R" : "C")}{size}x{chain.Count}@{e0.V0!.P.X:0},{e0.V0.P.Y:0},{e0.V0.P.Z:0}");
        }
        if (!preOk) continue;
        double v0 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        double th = Math.Round((0.5 + rnd.NextDouble() * 9.5) * (rnd.NextDouble() < 0.6 ? 1 : -1), 2);
        var faces = p.Bodies.SelectMany(b => b.Faces).ToList();
        int mode = rnd.Next(4); // 0 — без открытой, 1–2 — плоская, 3 — любая
        var sh = new ShellFeature("Shell") { Thickness = new ValueRef(th) };
        string open = "none";
        if (mode > 0)
        {
            var cand = mode < 3 ? faces.Where(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) != null).ToList() : faces;
            var fo = cand[rnd.Next(cand.Count)];
            if (Parametric3D.App.Operations.Refs.FacePoint(p, fo.Id) is { } fp) { sh.OpenFaces.Add(fp); sh.OpenFaceNames.Add(fo.Tag); open = (Parametric3D.App.Operations.Refs.FacePlane(p, fo.Id) != null ? "plane " : "curved ") + fo.Tag; }
        }
        // треугольники наружной поверхности до оболочки (без открытых граней) — для замера толщины стенки
        var outerTris = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var fo in p.Bodies.SelectMany(b => b.Faces))
        {
            if (fo.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface fsx) continue;
            bool isOpen = sh.OpenFaces.Any(q => fsx.Facets.ReadView.Any(tr => Parametric3D.Kernel.Ops.DirectRound.DistToTriangle(q, tr.Item1, tr.Item2, tr.Item3) < 1e-6));
            if (!isOpen || th < 0) foreach (var tr in fsx.Facets.ReadView) outerTris.Add(tr);
        }
        p.AddFeature(sh);
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null); double ms = sw.Elapsed.TotalMilliseconds; total += ms;
        string thick = "";
        if (sh.Error == null)
        {
            string tagW = th > 0 ? "shell:inner" : "shell:outer";
            // каждая вершина внутренней поверхности (грани shell:inner, кроме продлений) — в t от наружной
            double lo = double.MaxValue, hi = 0;
            foreach (var b in p.Bodies)
                foreach (var vx in b.Vertices)
                {
                    var fsV = b.Edges.Where(e => e.V0 == vx || e.V1 == vx).SelectMany(e => e.CoEdges.Select(c => c.Loop?.Face)).OfType<Parametric3D.Kernel.Topology.TFace>().Distinct().ToList();
                    if (fsV.Count == 0 || !fsV.All(f => f.Tag.StartsWith(tagW))) continue;
                    double dmin = outerTris.Min(tr => Parametric3D.Kernel.Ops.DirectRound.DistToTriangle(vx.P, tr.Item1, tr.Item2, tr.Item3));
                    lo = Math.Min(lo, dmin); hi = Math.Max(hi, dmin);
                }
            double at = Math.Abs(th), chord = 0.05 + 0.01 * at;
            if (lo != double.MaxValue && (lo < at - chord || hi > at + chord)) thick = $" WALL {lo:F3}..{hi:F3} (t {th})";
        }
        double v1 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        string w = p.Warnings.FirstOrDefault(x => x.StartsWith("Shell")) ?? "";
        string res = sh.Error != null ? "FAIL " + w : (th > 0 ? v1 < v0 : v1 > 0) ? (w.Contains("exact") ? "ok exact" : "ok " + w) : $"BAD VOLUME {v0:F0}->{v1:F0} " + w;
        res = System.Text.RegularExpressions.Regex.Replace(res, @"[-\d.]+(E[-+]\d+)?", "#"); if (res.Length > 200) res = res[..200];
        if (thick != "") res = "THICKNESS " + res;
        if (sh.Error != null) fails++;
        string key = $"{(th > 0 ? "in" : "out")} {(mode == 0 ? "closed" : open.StartsWith("plane") ? "plane" : "curved")}: {res}";
        var (n, sm, ex) = classes.GetValueOrDefault(key);
        string example = $"t {th} open {open} ops {string.Join(" ", log)}{thick}";
        classes[key] = (n + 1, sm + ms, ex ?? example);
        if (Environment.GetEnvironmentVariable("P3D_SHOWALL") == "1" || ((sh.Error != null || thick != "") && Environment.GetEnvironmentVariable("P3D_SHOWFAIL") == "1")) Console.WriteLine($"   #{t} {ms,6:0} ms {key}\n        {example}");
        if (sh.Error != null && Environment.GetEnvironmentVariable("P3D_SAVEDIR") is { } sd) { sh.Error = null; Parametric3D.Model.DocumentStorage.Save(Path.Combine(sd, $"shf{t}.p3d"), p); }
    }
    Console.WriteLine($"  trials {trials}: total {total:0} ms, fails {fails}");
    foreach (var kv in classes.OrderByDescending(x => x.Value.N)) Console.WriteLine($"   {kv.Value.N,4} {kv.Value.Ms,8:0} ms  {kv.Key}\n        e.g. {kv.Value.Ex}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shellround")
Check("BENCH-SHELL-ROUND", () =>
{
    // замечание: куб 100, скругление ребра R80, оболочка 2 (открыт торец), скругление внутреннего ребра R60 — лишний уголок
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    double yMin = p.Bodies.SelectMany(b => b.Edges).Min(e => Math.Min(e.V0!.P.Y, e.V1!.P.Y));
    var top = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6 && Math.Abs(e.V0.P.Y - yMin) < 1e-6 && Math.Abs(e.V1.P.Y - yMin) < 1e-6);
    var rf = new RoundFeature { Radius = new ValueRef(80) }; rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(rf); p.Rebuild(null);
    double xMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.X, e.V1!.P.X));
    var face = p.Bodies.SelectMany(b => b.Faces).First(f => Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.X > 0.99 && Math.Abs(pl.P.X - xMax) < 1e-6);
    var sh = new ShellFeature("Shell") { Thickness = new ValueRef(2) }; sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(p, face.Id)!.Value); sh.OpenFaceNames.Add(face.Tag); p.AddFeature(sh); p.Rebuild(null);
    if (sh.Error != null) throw new Exception("shell: " + sh.Error);
    double v1 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
    if (Environment.GetEnvironmentVariable("P3D_SAVESHELL") is { } svs) Parametric3D.Model.DocumentStorage.Save(svs, p);
    bool In48(Vector3d q) => Math.Abs(q.X) <= 48.0001 + (q.X > 0 ? 2 : 0) && Math.Abs(q.Y) <= 48.0001 && Math.Abs(q.Z) <= 48.0001;
    var inner = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && In48(e.V0!.P) && In48(e.V1!.P) && e.CoEdges.Count == 2).ToList();
    Console.WriteLine($"  shell volume {v1:F1}, inner straight edges {inner.Count}");
    if (Environment.GetEnvironmentVariable("P3D_EDGEIX") is { } eix) inner = new List<Parametric3D.Kernel.Topology.TEdge> { inner[int.Parse(eix)] };
    foreach (var ie in inner)
    {
        var ref0 = Parametric3D.Kernel.Topology.EdgeRef.Of(ie);
        double rr2 = double.Parse(Environment.GetEnvironmentVariable("P3D_R") ?? "60");
        var r2 = new RoundFeature { Radius = new ValueRef(rr2) }; r2.Edges.Add(ref0);
        Feature f2 = r2;
        if (Environment.GetEnvironmentVariable("P3D_CH") == "1") { var c2 = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(rr2), D2 = new ValueRef(rr2) }; c2.Edges.Add(ref0); f2 = c2; }
        p.AddFeature(f2);
        Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => { if (!m.StartsWith("face ")) Console.WriteLine("    dr: " + (m.Length > 300 ? m[..300] : m)); } : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        double v2 = p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN);
        double L = (ie.V1!.P - ie.V0!.P).Length, R = r2.Radius.Value, want = v1 + (f2 is ChamferFeature ? 0.5 : 1 - Math.PI / 4) * R * R * L;
        var bb = p.Bodies[0].Bounds;
        Console.WriteLine($"  inner edge {ie.V0.P}->{ie.V1.P}: {sw.Elapsed.TotalMilliseconds:0} ms error {f2.Error} volume {v2:F1} want {want:F1} (Δ {v2 - want:F1}) bounds {bb.Min} {bb.Max} faces {p.Bodies.Sum(b => b.Faces.Count())}");
        foreach (var w in p.Warnings.TakeLast(2)) Console.WriteLine("    warn " + (w.Length > 250 ? w[..250] : w));
        if (Environment.GetEnvironmentVariable("P3D_FACES") == "1")
            foreach (var fc in p.Bodies.SelectMany(b => b.Faces))
            {
                var bbf = Parametric3D.Kernel.Bounds3.Empty; foreach (var ed in fc.Edges) { bbf.Add(ed.V0!.P); bbf.Add(ed.V1!.P); }
                Console.WriteLine($"    face {Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} {fc.Surface.GetType().Name} {fc.Tag} loops {fc.Loops.Count} edges {fc.Edges.Count()} bounds {bbf.Min} {bbf.Max}");
            }
        if (Environment.GetEnvironmentVariable("P3D_SAVE") is { } sv && f2.Error == null) Parametric3D.Model.DocumentStorage.Save(sv, p);
        p.RemoveFeature(f2); p.Rebuild(null);
        Parametric3D.Kernel.Ops.DirectRound.Debug = null;
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "var")
Check("BENCH-VAR", () =>
{
    // u12 (замечание 1): куб 100, R30 верхнего ребра, затем цепочка «ребро — дуга R30 — ребро» переменным радиусом 10 → 20
    var s = DocumentSession.Sample(); var p = s.Part!; p.Rebuild(null);
    double zMax = p.Bodies.SelectMany(b => b.Edges).Max(e => Math.Max(e.V0!.P.Z, e.V1!.P.Z));
    var top = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0!.P.Z - zMax) < 1e-6 && Math.Abs(e.V1!.P.Z - zMax) < 1e-6).OrderBy(e => e.V0!.P.X + e.V1!.P.X).First();
    var r30 = new RoundFeature { Radius = new ValueRef(30), Name = "R30" }; r30.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(top)); p.AddFeature(r30); p.Rebuild(null);
    if (r30.Error != null) throw new Exception("R30: " + r30.Error);
    var arc = p.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 30) < 1e-6);
    var chain = Parametric3D.App.Operations.Topo.TangentChain(arc, Parametric3D.App.Operations.Topo.AllEdges(p));
    foreach (var e in chain) Console.WriteLine($"  chain {e.Curve?.GetType().Name} {e.V0!.P} -> {e.V1!.P}");
    double r0 = double.Parse(Environment.GetEnvironmentVariable("P3D_R0") ?? "10"), r1 = double.Parse(Environment.GetEnvironmentVariable("P3D_R1") ?? "20");
    Feature f;
    if (Environment.GetEnvironmentVariable("P3D_CH") == "1") { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(r0), D2 = new ValueRef(r0), SizeEnd = new ValueRef(r1) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
    else { var rf = new RoundFeature { Radius = new ValueRef(r0), RadiusEnd = new ValueRef(r1), Name = "var" }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
    Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined") || m.StartsWith("build declined")) Console.WriteLine("    direct: " + (m.Length > 300 ? m[..300] : m)); };
    Parametric3D.Kernel.Diagnostics.Prof.Enabled = Environment.GetEnvironmentVariable("P3D_PROF") == "1"; Parametric3D.Kernel.Diagnostics.Prof.Reset();
    if (Environment.GetEnvironmentVariable("P3D_UVDBG") == "1") Parametric3D.Kernel.Tess.UvMesher.Debug = m => Console.WriteLine("    uv: " + (m.Length > 300 ? m[..300] : m));
    p.AddFeature(f); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
    Console.WriteLine($"  var: {sw.Elapsed.TotalMilliseconds:0} ms error {f.Error}");
    if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(70)));
    foreach (var w in p.Warnings) Console.WriteLine("    warn " + (w.Length > 300 ? w[..300] : w));
    foreach (var fc in p.Bodies.SelectMany(b => b.Faces)) Console.WriteLine($"    face {Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} {fc.Surface.GetType().Name} {fc.Tag}");
    if (Environment.GetEnvironmentVariable("P3D_SAVE") is { } sv) Parametric3D.Model.DocumentStorage.Save(sv, p);
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "shell")
Check("BENCH-SHELL", () =>
{
    foreach (string open in new[] { "top", "front", "none" })
    foreach (double t in new[] { 5.0, 9.9, 10, 12, 20, -5, -12 })
    {
        var part = DocumentSession.Sample().Part!;
        part.Rebuild(null);
        double zTop = part.Bodies[0].Vertices.Max(v => v.P.Z);
        var e1 = part.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - zTop) < 1e-6 && Math.Abs(e.V1.P.Z - zTop) < 1e-6 && Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-6);
        var rf = new RoundFeature { Radius = new ValueRef(10) };
        rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e1));
        part.AddFeature(rf); part.Rebuild(null);
        var sh = new ShellFeature { Thickness = new ValueRef(t) };
        if (open != "none")
        {
            var f = part.Bodies[0].Faces.First(ff => Parametric3D.App.Operations.Refs.FacePlane(part, ff.Id) is { } pl && (open == "top" ? pl.N.Z > 0.99 : pl.N.X > 0.99));
            sh.OpenFaces.Add(Parametric3D.App.Operations.Refs.FacePoint(part, f.Id)!.Value);
        }
        part.AddFeature(sh);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        part.Rebuild(null);
        Console.WriteLine($"  open {open,-5} T {t,5}: {sw.Elapsed.TotalMilliseconds,6:0} ms volume {part.Volume,10:0} faces {part.Bodies.Sum(b => b.Faces.Count()),3} err {(sh.Error is { } er ? (er.Length > 200 ? er[..200] : er) : "")}");
        if (sh.Error != null) foreach (var wmsg in part.Warnings.Where(x => x.StartsWith("Shell"))) Console.WriteLine("       " + (wmsg.Length > 300 ? wmsg[..300] : wmsg));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "routes")
Check("BENCH-ROUTES", () =>
{
    // где обходной путь: операции дольше 250 мс на реалистичных деталях, причины отказа прямого построителя
    var reasons = new Dictionary<string, (int N, double Ms)>();
    var slowByType = new Dictionary<string, (int N, double Ms)>();
    int total = 0, slow = 0; double all = 0;
    string? last = null;
    Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined")) last = m; };
    int n0 = int.Parse(Environment.GetEnvironmentVariable("P3D_SEEDS") ?? "20");
    for (int seed = 1; seed <= n0; seed++)
    {
        var p = Parametric3D.App.Diagnostics.TestParts.Realistic(seed, 40);
        p.Invalidate();
        // по одной операции, чтобы ловить причину отказа каждой
        var feats = p.Features.ToList();
        p.StageIndex = 0; p.Rebuild(null);
        for (int i = 1; i <= feats.Count; i++)
        {
            last = null;
            p.StageIndex = i; p.Rebuild(null);
            var f = feats[i - 1];
            double ms = p.LastFeatureMs.GetValueOrDefault(f);
            total++; all += ms;
            string k = f.GetType().Name.Replace("Feature", "");
            if (ms > 250)
            {
                slow++;
                var (a1, b1) = slowByType.GetValueOrDefault(k); slowByType[k] = (a1 + 1, b1 + ms);
                string why = last == null ? "(no direct decline)" : System.Text.RegularExpressions.Regex.Replace(last, @"\(.*?\)|[-\d.]+(E[-+]\d+)?", "#");
                if (why.Length > 160) why = why[..160];
                var (a2, b2) = reasons.GetValueOrDefault(k + ": " + why); reasons[k + ": " + why] = (a2 + 1, b2 + ms);
            }
        }
    }
    Console.WriteLine($"  features {total}, total {all:0} ms, slow (>250 ms) {slow}");
    foreach (var kv in slowByType.OrderByDescending(x => x.Value.Ms)) Console.WriteLine($"   {kv.Key,-16} {kv.Value.N,4}  {kv.Value.Ms,8:0} ms");
    foreach (var kv in reasons.OrderByDescending(x => x.Value.Ms).Take(25)) Console.WriteLine($"   {kv.Value.N,4} {kv.Value.Ms,8:0} ms  {kv.Key}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "fuzz")
Check("BENCH-FUZZ", () =>
{
    // «как пользователь»: случайное острое ребро (с касательной цепочкой) — скругление или фаска случайного размера
    var rnd = new Random(int.Parse(Environment.GetEnvironmentVariable("P3D_FUZZSEED") ?? "7"));
    int trials = int.Parse(Environment.GetEnvironmentVariable("P3D_TRIALS") ?? "120");
    var classes = new Dictionary<string, (int N, double Ms, int Fail, string Ex)>();
    string? last = null;
    Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.StartsWith("declined")) last = m; };
    var parts = new List<Part>();
    for (int sd = 1; sd <= 6; sd++) { var pp = Parametric3D.App.Diagnostics.TestParts.Realistic(sd, 30); pp.Rebuild(null); parts.Add(pp); }
    { var cube = DocumentSession.Sample().Part!; cube.Rebuild(null); parts.Add(cube); }
    double sumMs = 0; int fails = 0, slowN = 0, mism = 0;
    for (int t = 0; t < trials; t++)
    {
        var p = parts[rnd.Next(parts.Count)];
        var sharp = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve != null && Parametric3D.App.Operations.Topo.FacesOf(p, e).Count() == 2).ToList();
        sharp = sharp.Where(e =>
        {
            var fs = Parametric3D.App.Operations.Topo.FacesOf(p, e).ToList();
            var c = e.Curve!; var mid = c.PointAt(0.5 * (c.T0 + c.T1));
            var n1 = Parametric3D.App.Operations.Topo.FaceNormal(p, fs[0], mid); var n2 = Parametric3D.App.Operations.Topo.FaceNormal(p, fs[1], mid);
            return Math.Abs(Vector3d.Dot(n1, n2)) < 0.995;
        }).ToList();
        if (sharp.Count == 0) continue;
        var e0 = sharp[rnd.Next(sharp.Count)];
        var chain = Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p));
        bool round = rnd.NextDouble() < 0.6;
        double size = Math.Round(0.5 + rnd.NextDouble() * (p.Bodies.Count > 0 && p.Name.Contains("cube") ? 30 : 4), 1);
        Feature f;
        if (round) { var rf = new RoundFeature { Radius = new ValueRef(size) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
        else { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(size), D2 = new ValueRef(size) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
        last = null;
        bool predicted = Parametric3D.App.Operations.Topo.EdgeGhostValid(p, chain.Select(c => c.Id), round)?.Invoke(size) ?? true;
        p.AddFeature(f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(null);
        double ms = sw.Elapsed.TotalMilliseconds;
        double fms = p.LastFeatureMs.GetValueOrDefault(f);
        string kinds = string.Join("+", chain.Select(c => c.Curve is Parametric3D.Kernel.Curves.LineCurve3 ? "L" : c.Curve is Parametric3D.Kernel.Curves.ArcCurve3 ? "A" : "C").Distinct().OrderBy(x => x));
        string why = f.Error != null ? "FAIL " + System.Text.RegularExpressions.Regex.Replace(f.Error, @"[-\d.]+(E[-+]\d+)?", "#")
                   : fms > 250 ? "SLOW " + (last == null ? "-" : System.Text.RegularExpressions.Regex.Replace(last, @"\(.*?\)|[-\d.]+(E[-+]\d+)?", "#")) : "ok";
        if (why.Length > 170) why = why[..170];
        string key = $"{(round ? "R" : "C")} {kinds}: {why}";
        var (n, sm, fl, ex) = classes.GetValueOrDefault(key);
        classes[key] = (n + 1, sm + fms, fl + (f.Error != null ? 1 : 0), ex ?? $"part {p.Name} size {size} edge {e0.V0?.P} -> {e0.V1?.P}");
        sumMs += fms; if (f.Error != null) fails++; if (fms > 250) slowN++;
        if (predicted != (f.Error == null)) { mism++; Console.WriteLine($"   MISMATCH predicted {(predicted ? "ok" : "fail")}, actual {(f.Error == null ? "ok" : "fail")}: {(round ? "R" : "C")}{size} {kinds} part {p.Name} edge {e0.V0?.P} -> {e0.V1?.P}"); }
        p.RemoveFeature(f); p.Rebuild(null);
    }
    Console.WriteLine($"  trials {trials}: total {sumMs:0} ms, fails {fails}, slow {slowN}, contour mismatches {mism}");
    foreach (var kv in classes.OrderByDescending(x => x.Value.Ms)) Console.WriteLine($"   {kv.Value.N,4} {kv.Value.Ms,8:0} ms fail {kv.Value.Fail,3}  {kv.Key}\n        e.g. {kv.Value.Ex}");
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "edge")
Check("BENCH-EDGE", () =>
{
    var a = Environment.GetEnvironmentVariable("P3D_EDGE")!.Split(';');   // seed;x,y,z
    var p = Parametric3D.App.Diagnostics.TestParts.Realistic(int.Parse(a[0]), 30); p.Rebuild(null);
    var q = a[1].Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    var at = new Vector3d(q[0], q[1], q[2]);
    var e0 = p.Bodies.SelectMany(b => b.Edges).OrderBy(e => Math.Min((e.V0!.P - at).Length, (e.V1!.P - at).Length)).First();
    string S(Parametric3D.Kernel.Topology.TFace f) => $"{Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} {f.Tag}";
    foreach (var e in Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p)))
    {
        Console.WriteLine($"  edge {e.Curve?.GetType().Name} {e.V0?.P} -> {e.V1?.P} faces: {string.Join(" | ", Parametric3D.App.Operations.Topo.FacesOf(p, e).Select(S))}");
        foreach (var v in new[] { e.V0!, e.V1! })
            Console.WriteLine($"     vertex {v.P} degree {v.Edges.Count()}: {string.Join("; ", v.Edges.Select(x => x.Curve?.GetType().Name + " " + (x.V0 == v ? x.V1!.P : x.V0!.P)))}");
    }
    if (Environment.GetEnvironmentVariable("P3D_OP") is { } op)
    {
        var chain = Parametric3D.App.Operations.Topo.TangentChain(e0, Parametric3D.App.Operations.Topo.AllEdges(p));
        double size = double.Parse(op[1..], System.Globalization.CultureInfo.InvariantCulture);
        Parametric3D.App.Operations.Topo.GhostDebug = m => Console.WriteLine("    ghost: " + m);
        Console.WriteLine("  predicted: " + (Parametric3D.App.Operations.Topo.EdgeGhostValid(p, chain.Select(c => c.Id), op[0] == 'R')?.Invoke(size)));
        Feature f;
        if (op[0] == 'R') { var rf = new RoundFeature { Radius = new ValueRef(size) }; foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = rf; }
        else { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(size), D2 = new ValueRef(size) }; foreach (var c in chain) cf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c)); f = cf; }
        Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (!m.StartsWith("face ") && !m.StartsWith("normalize") && !m.Contains("normalize")) Console.WriteLine("    direct: " + (m.Length > 400 ? m[..400] : m)); };
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = Environment.GetEnvironmentVariable("P3D_PROF") == "1"; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        if (Environment.GetEnvironmentVariable("P3D_UVDBG") == "1") Parametric3D.Kernel.Tess.UvMesher.Debug = m => Console.WriteLine("    uv: " + (m.Length > 300 ? m[..300] : m));
        p.AddFeature(f); var sw = System.Diagnostics.Stopwatch.StartNew(); p.Rebuild(null);
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(40)));
        Console.WriteLine($"  {op}: {sw.Elapsed.TotalMilliseconds:0} ms error {f.Error} volume {p.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):R}");
        foreach (var w in p.Warnings) Console.WriteLine("    warn " + (w.Length > 300 ? w[..300] : w));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "file")
Check("BENCH-FILE", () =>
{
    var (part, pars) = DocumentStorage.Load(Environment.GetEnvironmentVariable("P3D_FILE")!);
    part.Rebuild(pars);
    Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
    Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => { if (!m.StartsWith("face ")) Console.WriteLine("       direct: " + (m.Length > 500 ? m[..500] : m)); } : null;
    Parametric3D.Kernel.Ops.ShellOp.ShellDebug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => Console.WriteLine("       shell: " + m) : null;
    part.Invalidate();
    var sw = System.Diagnostics.Stopwatch.StartNew(); part.Rebuild(pars);
    Console.WriteLine($"  full rebuild {sw.Elapsed.TotalMilliseconds:0} ms, bodies {part.Bodies.Count}, volume {part.Bodies.Sum(b => Parametric3D.Kernel.Topology.AnalyticVolume.Of(b) ?? double.NaN):F3}, faces {part.Bodies.Sum(b => b.Faces.Count())}, valid {part.Bodies.All(b => Parametric3D.Kernel.Topology.SolidValidator.Validate(b).IsValid)}");
    foreach (var w in part.Warnings.Where(w => w.StartsWith("Shell"))) Console.WriteLine("   shellwarn " + (w.Length > 600 ? w[..600] : w));
    foreach (var f in part.Features) Console.WriteLine($"   {f.Name,-24} {part.LastFeatureMs.GetValueOrDefault(f),8:0} ms  {f.GetType().Name} {f.Error}");
    Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(30)));
    foreach (var f in part.Features.Where(f => f.Error != null))
    {
        if (f is RoundFeature rf2) foreach (var e in rf2.Edges) Console.WriteLine($"   ref {e.Start} -> {e.End} mid {e.Mid} faces {e.FaceA} | {e.FaceB}");
        if (f is ChamferFeature cf2) foreach (var e in cf2.Edges) Console.WriteLine($"   ref {e.Start} -> {e.End} mid {e.Mid} faces {e.FaceA} | {e.FaceB}");
        foreach (var w in part.Warnings.Where(x => x.Contains("Round") || x.Contains("Chamfer"))) Console.WriteLine("   warn " + (w.Length > 300 ? w[..300] : w));
        int at = part.Features.IndexOf(f);
        // тело до операции: стадия
        part.StageIndex = at; part.Invalidate(); part.Rebuild(pars);
        foreach (var e in part.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && e.V0.P.Z > 49 && e.V1.P.Z > 49))
            Console.WriteLine($"   body edge {e.V0.P} -> {e.V1.P}");
        part.StageIndex = -1;
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "3")
Check("BENCH-CHAIN", () =>
{
    // скругление цепочки «прямое — дуга прежнего скругления — прямое» на кубе (снимок пользователя)
    var part = DocumentSession.Sample().Part!;
    part.Rebuild(null);
    var e1 = part.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5);
    var r1 = new RoundFeature { Radius = new ValueRef(double.Parse(Environment.GetEnvironmentVariable("P3D_R1") ?? "40", System.Globalization.CultureInfo.InvariantCulture)) };
    r1.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(e1));
    part.AddFeature(r1); part.Rebuild(null);
    Console.WriteLine($"  first round: faces {part.Bodies.Sum(b => b.Faces.Count())} err {r1.Error}");
    // ребро, касательно продолжающееся через дугу скругления
    var roundFaceIds = r1.CreatedFaceIds.ToHashSet();
    Parametric3D.Kernel.Topology.TEdge? start = null; List<Parametric3D.Kernel.Topology.TEdge>? chain = null;
    foreach (var e in part.Bodies.SelectMany(b => b.Edges))
    {
        if (e.Curve is not Parametric3D.Kernel.Curves.LineCurve3) continue;
        var ch = Parametric3D.App.Operations.Topo.TangentChain(e);
        if (ch.Count >= 3) { start = e; chain = ch; break; }
    }
    if (chain == null) { Console.WriteLine("  no chain"); return; }
    Console.WriteLine($"  chain {chain.Count}: {string.Join(",", chain.Select(c => c.Curve?.GetType().Name))}");
    var rf = new RoundFeature { Radius = new ValueRef(5) };
    foreach (var c in chain) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(c));
    part.AddFeature(rf); part.Rebuild(null);
    foreach (var r in new[] { 5.0, 10, 20, 30, 40, 5, 20 })
    {
        rf.Radius = new ValueRef(r); part.NotifyChanged(rf);
        var st = new Dictionary<string, double>(); string last = "start"; long lt = System.Diagnostics.Stopwatch.GetTimestamp();
        void Hook(string sname) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); st[last] = st.GetValueOrDefault(last) + System.Diagnostics.Stopwatch.GetElapsedTime(lt, n).TotalMilliseconds; last = sname; lt = n; }
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = Environment.GetEnvironmentVariable("P3D_PROF") == "1" && r == 20; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        Parametric3D.Kernel.Tess.UvMesher.Debug = Environment.GetEnvironmentVariable("P3D_UVDBG") == "1" ? m => Console.WriteLine("       uv: " + m) : null;
        var sw = System.Diagnostics.Stopwatch.StartNew(); Part.BuildResult res;
        double cf = double.Parse(Environment.GetEnvironmentVariable("P3D_COARSE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var ks = Parametric3D.Kernel.KernelSettings.Capture();
        if (cf > 0) ks = ks with { Preview = true, Chord = ks.Chord * cf, ChordAngle = Math.Min(ks.ChordAngle * cf, 0.6) };
        using (new Parametric3D.Kernel.KernelContext(ks).WithStageHook(Hook).Enter()) res = part.Compute(null);
        Hook("end"); double t = sw.Elapsed.TotalMilliseconds; part.Apply(res);
        Console.WriteLine($"  R {r}: {t:0} ms faces {part.Bodies.Sum(b => b.Faces.Count())} tris {part.Mesh?.Indices.Length / 3} err {rf.Error} | " + string.Join(", ", st.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key}={k.Value:0}")));
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(string.Join("\n", Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(70)));
        Console.WriteLine("     tris/face: " + string.Join(" ", part.Bodies.SelectMany(b => b.Faces).Select(f => $"{Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name.Replace("Surface","")}:{(part.Mesh!.FaceRanges.TryGetValue(f.Id, out var fr) ? fr.IndexCount / 3 : -1)}")));
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "2")
Check("BENCH-SWEEP", () =>
{
    var part = Environment.GetEnvironmentVariable("P3D_BENCHPART") == "r" ? Parametric3D.App.Diagnostics.TestParts.Realistic(1, 40) : DocumentSession.Sample().Part!;
    part.Rebuild(null);
    var edge = part.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5);
    var rf = new RoundFeature { Radius = new ValueRef(1) };
    foreach (var ce in Environment.GetEnvironmentVariable("P3D_CHAIN") == "1" ? Parametric3D.App.Operations.Topo.TangentChain(edge) : new() { edge }) rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(ce));
    part.AddFeature(rf); part.Rebuild(null);
    foreach (var r in (Environment.GetEnvironmentVariable("P3D_BENCHR") is { } rr ? rr.Split(",").Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : new[] { 1.0, 1.2, 1.4, 1.6, 2, 3, 5, 8, 12, 20, 1.0, 1.4 }))
    {
        rf.Radius = new ValueRef(r); part.NotifyChanged(rf);
        var st = new Dictionary<string, double>(); string last = "start"; long lt = System.Diagnostics.Stopwatch.GetTimestamp();
        void Hook(string sname) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); st[last] = st.GetValueOrDefault(last) + System.Diagnostics.Stopwatch.GetElapsedTime(lt, n).TotalMilliseconds; last = sname; lt = n; }
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = r is 1.6 && Environment.GetEnvironmentVariable("P3D_PROF") == "1"; Parametric3D.Kernel.Diagnostics.Prof.Reset();
        Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("P3D_DRDBG") == "1" ? m => Console.WriteLine("       direct: " + (m.Length > 600 ? m[..600] : m)) : null;

        var sw = System.Diagnostics.Stopwatch.StartNew(); Part.BuildResult res;
        double cf = double.Parse(Environment.GetEnvironmentVariable("P3D_COARSE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var ks = Parametric3D.Kernel.KernelSettings.Capture();
        if (cf > 0) ks = ks with { Preview = true, LivePreview = true, Chord = ks.Chord * cf };
        using (new Parametric3D.Kernel.KernelContext(ks).WithStageHook(Hook).Enter()) res = part.Compute(null);
        Hook("end");
        double t = sw.Elapsed.TotalMilliseconds; part.Apply(res);
        Console.WriteLine("     " + string.Join(", ", st.OrderByDescending(k => k.Value).Take(5).Select(k => $"{k.Key}={k.Value:0}")));
        if (Parametric3D.Kernel.Diagnostics.Prof.Enabled) Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
        if (part.Bodies.Sum(b => b.Faces.Count()) < 20) Console.WriteLine("     tris/face: " + string.Join(" ", part.Bodies.SelectMany(b => b.Faces).Select(f => $"{f.Surface?.GetType().Name.Replace("Surface","")}:{(part.Mesh!.FaceRanges.TryGetValue(f.Id, out var fr) ? fr.IndexCount / 3 : -1)}")));
        Console.WriteLine($"  R {r}: {t:0} ms (feature {part.LastFeatureMs.GetValueOrDefault(rf):0}) faces {part.Bodies.Sum(b => b.Faces.Count())} tris {part.Mesh?.Indices.Length / 3} err {rf.Error}");
    }
});

if (Environment.GetEnvironmentVariable("P3D_BENCH") == "1")
Check("BENCH-DRAG", () =>
{
    foreach (var (name, part) in new[] { ("sample", DocumentSession.Sample().Part!), ("realistic40", Parametric3D.App.Diagnostics.TestParts.Realistic(1, 40)) })
    {
        part.Rebuild(null);
        var s = DocumentSession.FromPart(part, name);
        var edge = part.Bodies.SelectMany(b => b.Edges).First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5);
        var rf = new RoundFeature { Radius = new ValueRef(1) };
        rf.Edges.Add(Parametric3D.Kernel.Topology.EdgeRef.Of(edge));
        part.AddFeature(rf);
        part.Rebuild(null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var full = new List<double>(); var prev = new List<double>(); var apply = new List<double>(); var scene = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            rf.Radius = new ValueRef(1 + 0.1 * i); part.NotifyChanged(rf);
            sw.Restart(); var r = part.Compute(null); full.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart(); part.Apply(r); apply.Add(sw.Elapsed.TotalMilliseconds);
            rf.Radius = new ValueRef(1.05 + 0.1 * i); part.NotifyChanged(rf);
            sw.Restart();
            using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { Preview = true }).Enter()) { var r2 = part.Compute(null); part.Apply(r2); }
            prev.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart(); Parametric3D.Render.Engine.SceneBuilder.Build(s.Document, Parametric3D.Render.Engine.RenderTheme.DarkTheme(), new Parametric3D.Render.Engine.ShowOptions()); scene.Add(sw.Elapsed.TotalMilliseconds);
        }
        string M(List<double> l) => string.Join(" ", l.Select(x => x.ToString("0")));
        Console.WriteLine($"  {name}: faces {part.Bodies.Sum(b => b.Faces.Count())}; full [{M(full)}] apply [{M(apply)}] preview [{M(prev)}] scene [{M(scene)}] feature {part.LastFeatureMs.GetValueOrDefault(rf):0} err {rf.Error} faces {string.Join(",", part.LastFeatureMs.OrderByDescending(k => k.Value).Take(3).Select(k => k.Key.Name + "=" + k.Value.ToString("0")))}");
    }
});

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed }));
return failed == 0 ? 0 : 1;

sealed class CheckFailed : Exception { public CheckFailed(string m) : base(m) { } }
sealed class Tol : IEqualityComparer<double> { public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-6; public int GetHashCode(double x) => 0; }
