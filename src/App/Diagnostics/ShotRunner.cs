using Avalonia.Threading;
using Parametric3D.App.Views;

namespace Parametric3D.App.Diagnostics;

/// <summary>
/// Проверка без человека (Xvfb): через паузу на построение — снимок кадра 3D-вида в PNG, затем выход. Сценарий
/// (--script «команда;команда…») выполняет команды реестра до снимка. Снимок всего окна делает внешний скрипт.
/// </summary>
public static class ShotRunner
{
    /// <summary>
    /// Шаг сценария: id команды; hover:x,y / pick:x,y / add:x,y — навести, выбрать, добавить (доли размера вида);
    /// click:x,y — щелчок инструмента эскиза; ok / cancel — кнопки панели операции; esc.
    /// </summary>
    private static void Step(MainWindow w, string id)
    {
        if (id.Split(':') is [var verb, var arg] && verb is "hover" or "pick" or "add" or "click")
        {
            var xy = arg.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (verb == "click") w.ViewPort.SimulateToolClick(xy[0], xy[1]);
            else w.ViewPort.SimulatePick(xy[0], xy[1], verb != "hover", verb == "add");
            Console.WriteLine($"{verb} {arg}: hover {w.ViewPort.Selection.Hover.Kind}, selected {w.ViewPort.Selection.Count}");
            return;
        }
        if (id.StartsWith("hdrag:") || id.StartsWith("hhold:"))
        {
            var a = id[6..].Split(',');
            w.ViewPort.SimulateHandleDrag(int.Parse(a[0]) * double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), end: id.StartsWith("hdrag:"));
            Console.WriteLine($"handle drag {id}");
            return;
        }
        if (id.StartsWith("gdrag:") || id.StartsWith("ghold:"))
        {
            // 13:09: гизмо — gdrag:часть,dx,dy,dz,угол[,shift|ctrl]; часть — номер GizmoPart (0–2 оси, 3–5 плоскости, 6 угол)
            var a = id[6..].Split(',');
            double D(int i) => double.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
            var mods = a.Length > 5 ? (a[5] == "shift" ? Avalonia.Input.KeyModifiers.Shift : a[5] == "ctrl" ? Avalonia.Input.KeyModifiers.Control : Avalonia.Input.KeyModifiers.None) : Avalonia.Input.KeyModifiers.None;
            w.ViewPort.SimulateGizmoDrag(new Viewport.GizmoDrag((Viewport.GizmoPart)int.Parse(a[0]), new OpenTK.Mathematics.Vector3d(D(1), D(2), D(3)), D(4), mods), end: id.StartsWith("gdrag:"));
            Console.WriteLine($"gizmo drag {id}: {w.ViewPort.Gizmo}");
            return;
        }
        if (id.StartsWith("body:") && w.Session.Part is { } bp && int.TryParse(id[5..], out int bi) && bi < bp.Bodies.Count)
        {
            // выбрать грань тела N (как щелчок по телу в виде)
            w.ViewPort.Selection.Select(new Parametric3D.Core.PickRef(bp.Bodies[bi].Faces.First().Id, Parametric3D.Core.PickKind.Face, bp, null), additive: false);
            Console.WriteLine($"body {bi}: selected {w.ViewPort.Selection.Count}");
            return;
        }
        if (id.StartsWith("snap:"))
        {
            // промежуточный снимок кадра (разбор мигания): snap:имя → рядом с основным снимком
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppStartup.Arg("--shot") ?? ".") ?? ".", id[5..] + ".png");
            w.ViewPort.CaptureNextFrame = (px, pw, ph) => { Shots.SavePng(path, px, pw, ph, flip: true); Console.WriteLine("SNAP " + path); };
            w.ViewPort.Redraw();
            return;
        }
        if (id.StartsWith("rot:"))
        {
            // поворот вида: наклон (вокруг X) и разворот (вокруг Y), градусы — как изометрия «домой»
            var a = id[4..].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180).ToArray();
            w.ViewPort.AnimateTo(OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitX, a[0]) * OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitY, a[1]));
            Console.WriteLine("rot " + id); return;
        }
        if (id.StartsWith("cam:"))
        {
            // cam:tx,ty,tz,расстояние,наклон,разворот — точка взгляда, расстояние и поворот (градусы)
            var a = id[4..].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var st = w.ViewPort.Camera.State;
            st.Target = new OpenTK.Mathematics.Vector3(a[0], a[1], a[2]); st.Distance = a[3];
            st.Rotation = OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitX, a[4] * MathF.PI / 180) * OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitY, a[5] * MathF.PI / 180);
            w.ViewPort.Camera.State = st; w.ViewPort.Redraw();
            Console.WriteLine("cam " + id); return;
        }
        if (id.StartsWith("facetag:") && w.Session.Part is { } tp)
        {
            // facetag:начало метки[,номер] — выбрать грань тела по метке (разбор: подсветка грани)
            var a = id[8..].Split(',');
            var fs = tp.Bodies.SelectMany(b => b.Faces).Where(f => f.Tag.StartsWith(a[0], StringComparison.Ordinal)).ToList();
            int k = a.Length > 1 ? int.Parse(a[1]) : 0;
            if (k < fs.Count) w.ViewPort.Selection.Select(new Parametric3D.Core.PickRef(fs[k].Id, Parametric3D.Core.PickKind.Face, tp, null), additive: false);
            Console.WriteLine($"facetag {id}: {fs.Count} faces, selected {(k < fs.Count ? fs[k].Id : 0)}");
            return;
        }
        if (id == "meshfaces" && w.Session.Part is { } mfp && mfp.Mesh is { } mmd)
        {
            // разбор: треугольники сетки показа — на поверхности грани своего номера?
            var bf = mfp.Bodies.SelectMany(b => b.Faces).ToDictionary(f => f.Id);
            var per = new Dictionary<uint, (int N, int Off, double Area)>();
            for (int t = 0; t + 2 < mmd.Indices.Length; t += 3)
            {
                var ta = mmd.Vertices[mmd.Indices[t]]; var tb = mmd.Vertices[mmd.Indices[t + 1]]; var tc = mmd.Vertices[mmd.Indices[t + 2]];
                var cur = per.GetValueOrDefault(ta.FaceId); int off = 0;
                if (bf.TryGetValue(ta.FaceId, out var mf) && cur.N % 5 == 0)
                {
                    var src = (mf.Surface as Kernel.Surfaces.FacetedSurface)?.Source ?? mf.Surface;
                    var cen = new OpenTK.Mathematics.Vector3d((ta.P.X + tb.P.X + tc.P.X) / 3, (ta.P.Y + tb.P.Y + tc.P.Y) / 3, (ta.P.Z + tb.P.Z + tc.P.Z) / 3);
                    if ((src.PointAt(src.ProjectToUV(cen)) - cen).Length > 0.5) off = 1;
                }
                per[ta.FaceId] = (cur.N + 1, cur.Off + off, cur.Area + OpenTK.Mathematics.Vector3.Cross(tb.P - ta.P, tc.P - ta.P).Length / 2);
            }
            foreach (var (fid, v) in per.OrderBy(k => k.Key)) Console.WriteLine($"meshface {fid} {bf.GetValueOrDefault(fid)?.Tag}: tris {v.N}, area {v.Area:0.#}, off-surface samples {v.Off}");
            return;
        }
        if (id.StartsWith("ray:") && w.Session.Part?.Mesh is { } rmd)
        {
            // ray:px,py — все пересечения луча через пиксель снимка с сеткой детали (разбор артефактов показа)
            var a = id[4..].Split(',').Select(int.Parse).ToArray();
            w.ViewPort.Camera.ScreenRay(a[0], a[1], out var ro, out var rd);
            var hits = new List<(float T, uint Face, float Dot, int Tri)>();
            for (int t = 0; t + 2 < rmd.Indices.Length; t += 3)
            {
                var p0 = rmd.Vertices[rmd.Indices[t]].P; var p1 = rmd.Vertices[rmd.Indices[t + 1]].P; var p2 = rmd.Vertices[rmd.Indices[t + 2]].P;
                var e1 = p1 - p0; var e2 = p2 - p0; var h = OpenTK.Mathematics.Vector3.Cross(rd, e2); float det = OpenTK.Mathematics.Vector3.Dot(e1, h);
                if (MathF.Abs(det) < 1e-12f) continue;
                float inv = 1 / det; var sv = ro - p0; float u = inv * OpenTK.Mathematics.Vector3.Dot(sv, h); if (u < 0 || u > 1) continue;
                var q = OpenTK.Mathematics.Vector3.Cross(sv, e1); float v = inv * OpenTK.Mathematics.Vector3.Dot(rd, q); if (v < 0 || u + v > 1) continue;
                float tt = inv * OpenTK.Mathematics.Vector3.Dot(e2, q); if (tt <= 0) continue;
                var gn = OpenTK.Mathematics.Vector3.Cross(e1, e2).Normalized();
                hits.Add((tt, rmd.Vertices[rmd.Indices[t]].FaceId, OpenTK.Mathematics.Vector3.Dot(gn, rd), t / 3));
            }
            foreach (var hh in hits.OrderBy(x => x.T).Take(8))
            {
                var vv = Enumerable.Range(0, 3).Select(k => rmd.Vertices[rmd.Indices[hh.Tri * 3 + k]]).ToArray();
                var gn = OpenTK.Mathematics.Vector3.Cross(vv[1].P - vv[0].P, vv[2].P - vv[0].P).Normalized();
                Console.WriteLine($"ray {id}: t {hh.T:0.####} face {hh.Face} dot {hh.Dot:0.###} tri {hh.Tri} P {vv[0].P} {vv[1].P} {vv[2].P} N·g {OpenTK.Mathematics.Vector3.Dot(vv[0].N, gn):0.###} {OpenTK.Mathematics.Vector3.Dot(vv[1].N, gn):0.###} {OpenTK.Mathematics.Vector3.Dot(vv[2].N, gn):0.###}");
            }
            return;
        }
        if (id.StartsWith("mat:") && w.Session.Part is { } mp && Model.Material.FromLibrary(id[4..]) is { } mm)
        {
            mp.Material = mm; w.ViewPort.MarkDirty(); Console.WriteLine("material " + mm.Name); return;
        }
        if (id.StartsWith("select:"))
        {
            // выбрать элемент модели по имени (как щелчок в дереве)
            var name = id[7..];
            var it = w.Session.Document.Root.Descendants().FirstOrDefault(x => x.Name == name);
            if (it != null) w.SelectItem(it);
            Console.WriteLine($"select {name}: {(it != null ? "ok" : "not found")}");
            return;
        }
        switch (id)
        {
            case "ok": w.CommitOperation(); Console.WriteLine("ok"); return;
            case "cancel": w.CancelOperation(); Console.WriteLine("cancel"); return;
            case "wait": return;
            case "notool": w.ViewPort.Tool = null; return;
            case "dbg":
                {
                    var part = w.Session.Part!;
                    Console.WriteLine($"camera fwd {w.ViewPort.Camera.Forward} pos {w.ViewPort.Camera.Position} sketch W {w.ViewPort.ActiveSketch?.Plane.W} O {w.ViewPort.ActiveSketch?.Plane.Origin}");
                    var meshIds = part.Mesh?.Vertices.Select(v => v.FaceId).Distinct().OrderBy(x => x).ToList() ?? new();
                    var bodyIds = part.Bodies.SelectMany(b => b.Faces).Select(f => f.Id).OrderBy(x => x).ToList();
                    foreach (var f in part.Bodies.SelectMany(b => b.Faces)) Console.WriteLine($"face {f.Id}: {f.Surface?.GetType().Name} auth {Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} plane {Model.Features.SketchFaceRef.PlaneOf(f) != null}");
                    Console.WriteLine($"mesh faces [{string.Join(",", meshIds)}] body faces [{string.Join(",", bodyIds)}] sel {w.ViewPort.Selection.Primary.Id} owner {w.ViewPort.Selection.Primary.Owner?.GetHashCode()} part {part.GetHashCode()}");
                    return;
                }
        }
        var c = Commands.CommandRegistry.Instance.Find(id);
        Console.WriteLine(c == null ? $"no command {id}" : $"run {id} (enabled {c.Enabled})");
        c?.Execute();
    }

    public static void Start(MainWindow w, string path, Action exit)
    {
        var steps = (AppStartup.Arg("--script") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int wait = int.TryParse(AppStartup.Arg("--wait"), out var s) ? s : 3;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(wait) };
        int stage = 0;
        t.Tick += (_, _) =>
        {
            // шаги — по одному за такт (выбор видеокартой приходит со следующим кадром)
            if (stage == 0 && steps.Count > 0)
            {
                t.Interval = TimeSpan.FromSeconds(0.7);
                var id = steps[0];
                steps.RemoveAt(0);
                try { Step(w, id); }
                catch (Exception ex) { Console.WriteLine($"step {id}: {ex.Message}"); }
                return;
            }
            if (stage == 0)
            {
                stage = 1;
                t.Interval = TimeSpan.FromSeconds(1.5);
                return;
            }
            t.Stop();
            w.ViewPort.CaptureNextFrame = (px, pw, ph) =>
            {
                Shots.SavePng(path, px, pw, ph, flip: true);
                Console.WriteLine($"SHOT {pw}x{ph} | {w.ViewPort.GlInfo}");
                Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(exit, TimeSpan.FromSeconds(AppStartup.Flag("--hold") ? 4 : 0.2)));
            };
            w.ViewPort.Redraw();
        };
        t.Start();
    }
}
