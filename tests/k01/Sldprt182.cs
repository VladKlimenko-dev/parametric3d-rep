using OpenTK.Mathematics;
using System.Text.RegularExpressions;
using Parametric3D.Kernel.Export.Parasolid;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k182: ИМПОРТ SOLIDWORKS (.SLDPRT) — контейнер → Parasolid XT → STEP → тела (Exchange.Read). Файлы пользователя (винты
    /// McMaster-Carr: шестигранная головка, полукруглая с крестом, под шестигранник; резьба — B-поверхности, скругления
    /// шлица — BLENDED_EDGE). Проверяется: поток XT дочитан до конца, ни одна грань не пропущена, тел столько же, сколько твёрдых BODY в XT,
    /// все годны, габарит по вершинам совпадает с XT, объём положителен и не больше объёма габарита. Свойства массы SolidWorks
    /// (SW-MassProp-Config) только выводятся: их номер конфигурации часто не совпадает с сохранённой геометрией (устаревшие).
    /// Файлы — детали McMaster-Carr от пользователя (крепёж, фитинги, пружины, шестерни, подшипники, клапаны…).
    /// Режим «sldprt файл…» — разбор любых файлов с выводом STEP рядом (P3D_SLDOUT=папка).
    /// </summary>
    static void SldprtChecks(IEnumerable<string>? files = null, bool goldOnly = false)
    {
        // аргумент-каталог — все детали в нём (корпус: ветка corpus/sldprt); P3D_SLDSHARD=i/n — каждая n-я начиная с i
        var list = files?.SelectMany<string, string>(a => Directory.Exists(a) ? Directory.GetFiles(a, "*.*", SearchOption.AllDirectories)
                        .Where(x => x.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".x_b", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x, StringComparer.Ordinal)
                        : new[] { a }).ToList()
                   ?? Directory.GetFiles(Path.Combine("tests", "k01", "data"), "sw_*.SLDPRT").OrderBy(x => x).ToList();
        if (Environment.GetEnvironmentVariable("P3D_SLDSHARD") is { } sh && sh.Split('/') is [var si, var sn] && int.TryParse(si, out var shI) && int.TryParse(sn, out var shN) && shN > 0)
            list = list.Where((_, k) => k % shN == shI).ToList();
        // k185: P3D_SLDNAME=часть имени;часть имени — только эти детали (разбор деталей корпуса)
        if (Environment.GetEnvironmentVariable("P3D_SLDNAME") is { Length: > 0 } nm)
            list = list.Where(x => nm.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Any(n => Path.GetFileName(x).Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (goldOnly && files == null) list = list.Where(x => Gold.Contains(Path.GetFileNameWithoutExtension(x))).ToList();
        sldGood = 0;
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in list)
        {
            Check("SLDPRT-IMPORT-K182 " + Path.GetFileName(path), () => WithTimeout(Path.GetFileName(path), () =>
            {
                var w = new List<string>();
                var bytes = File.ReadAllBytes(path);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fs = SldprtFile.PartsXt(bytes, w).Select(x => XtReader.Read(x)).ToList();
                var f = fs[0];
                bool truncated = fs.Any(x => x.Truncated);
                var counts = fs.SelectMany(x => x.Order).GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.Count());
                bool prof0 = Environment.GetEnvironmentVariable("P3D_SLDPROF") == "1";
                if (prof0) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
                sldStage = "convert";
                string step = XtToStep.Convert(fs, Path.GetFileNameWithoutExtension(path), w);
                double tConv = sw.Elapsed.TotalMilliseconds;
                sldStage = $"read STEP (convert {tConv:F0} ms, {step.Length / 1024} KB)";
                if (Environment.GetEnvironmentVariable("P3D_SLDOUT") is { Length: > 0 } od) { Directory.CreateDirectory(od); File.WriteAllText(Path.Combine(od, Path.GetFileNameWithoutExtension(path) + ".step"), step); }
                bool prof = Environment.GetEnvironmentVariable("P3D_SLDPROF") == "1";
                var part = Exchange.Read(step, out _, w);
                if (prof) Console.Error.WriteLine("SLDPROF convert+read " + Path.GetFileName(path) + "\n" + Parametric3D.Kernel.Diagnostics.Prof.Report());
                double tAll = sw.Elapsed.TotalMilliseconds;
                sldStage = $"validate (convert {tConv:F0} ms, read {sw.Elapsed.TotalMilliseconds - tConv:F0} ms)";
                var bodies = part?.Bodies ?? new List<TSolid>();
                var b = bodies.FirstOrDefault();
                var vrs = bodies.Select(x => SolidValidator.Validate(x)).ToList();
                var vr = vrs.FirstOrDefault(x => !x.IsValid) ?? vrs.FirstOrDefault();
                if (Environment.GetEnvironmentVariable("P3D_SLDDIAG") == "1") SldDiag(Path.GetFileName(path), bodies, vrs);
                if (prof) { Console.Error.WriteLine("SLDPROF all " + Path.GetFileName(path) + "\n" + Parametric3D.Kernel.Diagnostics.Prof.Report()); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
                double vol = bodies.Sum(x => AnalyticVolume.Of(x) ?? 0), area = bodies.Sum(x => AnalyticVolume.AreaOf(x) ?? 0);
                // Габарит по вершинам и дугам (окружности, эллипсы) рёбер XT и тех же рёбер импортированных тел — должны совпасть; объём не больше объёма габарита.
                var xtPts = fs.SelectMany(XtExtentPoints).ToList();
                var bodyPts = bodies.SelectMany(BodyExtentPoints).ToList();
                (Vector3d lo, Vector3d hi) Box(List<Vector3d> pts) => pts.Count == 0 ? (Vector3d.Zero, Vector3d.Zero)
                    : (new Vector3d(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z)), new Vector3d(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z)));
                var (xl, xh) = Box(xtPts); var (bl, bh) = Box(bodyPts);
                double boxGap = Math.Max((xl - bl).Length, (xh - bh).Length), boxVol = (xh.X - xl.X) * (xh.Y - xl.Y) * (xh.Z - xl.Z);
                int xtBodies = fs.Sum(x => x.OfType("BODY").Count(n => n.I("body_type") == 1));
                var (swVol, swArea) = SwMassProps(bytes);
                Log(new { test = "sldprt", file = Path.GetFileName(path), f.Modeller, f.Schema, nodes = fs.Sum(x => x.Order.Count), Truncated = truncated, parts = fs.Count, faces = counts.GetValueOrDefault("FACE"),
                    surfaces = counts.Where(kv => kv.Key is "PLANE" or "CYLINDER" or "CONE" or "SPHERE" or "TORUS" or "B_SURFACE" or "BLENDED_EDGE" or "SWEPT_SURF" or "SPUN_SURF" or "OFFSET_SURF").ToDictionary(kv => kv.Key, kv => kv.Value),
                    curves = counts.Where(kv => kv.Key is "LINE" or "CIRCLE" or "ELLIPSE" or "B_CURVE" or "INTERSECTION" or "SP_CURVE" or "TRIMMED_CURVE").ToDictionary(kv => kv.Key, kv => kv.Value),
                    stepKb = step.Length / 1024, xtBodies, bodies = bodies.Count, bodyFaces = bodies.Sum(x => x.Faces.Count()), valid = vrs.Count > 0 && vrs.All(x => x.IsValid), verdict = vr?.Summary(),
                    errors = vrs.SelectMany(x => x.Issues).Where(x => x.Severity == ValidationSeverity.Error).Take(4).Select(x => x.ToString()).ToList(),
                    guarantee = b?.Guarantee.ToString(), volume = vol, area, boxVol, boxGap, xtBox = new[] { xl.X, xl.Y, xl.Z, xh.X, xh.Y, xh.Z }, bodyBox = new[] { bl.X, bl.Y, bl.Z, bh.X, bh.Y, bh.Z }, swVolume = swVol, swArea, msConvert = Math.Round(tConv), msTotal = Math.Round(tAll), warnings = w.Take(12).ToList(), heal = w.Where(x => x.Contains("HEALSIDE") || x.Contains("turned") || x.StartsWith("Import:")).Take(40).ToList() });
                bool good = !truncated && !w.Any(x => x.Contains("were left out") || x.Contains("is not supported; the edge")) && bodies.Count == xtBodies
                            && vrs.Count > 0 && vrs.All(x => x.IsValid) && boxGap <= 1e-3 && vol > 0 && vol <= boxVol * (1 + 1e-6);
                if (good) Interlocked.Increment(ref sldGood);
                // обязательная планка — для файлов, которые уже годны (ГОДНЫЕ); остальные — для сведения (лог), пока импорт
                // доводится (k183+): провал такого файла не валит прогон
                if (!Gold.Contains(Path.GetFileNameWithoutExtension(path)) && Environment.GetEnvironmentVariable("P3D_SLDSTRICT") != "1") return;
                Require(!truncated, "XT stream not read to the end");
                Require(!w.Any(x => x.Contains("were left out") || x.Contains("is not supported; the edge")), "faces or edges left out: " + string.Join(" | ", w.Where(x => x.Contains("left out"))));
                Require(bodies.Count == xtBodies, $"bodies {bodies.Count} of {xtBodies}");
                Require(vrs.All(x => x.IsValid), "invalid body: " + vr?.Summary());
                Require(boxGap <= 1e-3, $"bounding box differs from the XT edges by {boxGap:G3} mm");
                Require(vol > 0 && vol <= boxVol * (1 + 1e-6), $"volume {vol:F2} mm³ outside (0, {boxVol:F2}] of the bounding box");
            }));
        }
        Log(new { test = "sldprt-summary", files = list.Count, good = sldGood, seconds = Math.Round(sw0.Elapsed.TotalSeconds) });
    }
    static int sldGood;

    /// <summary>Объём и площадь из свойств массы SolidWorks (мм³, мм²) — поля 4 и 5 записи SW-MassProp-Config-N (СИ).</summary>
    static (double? Vol, double? Area) SwMassProps(byte[] bytes)
    {
        var st = SldprtFile.Streams(bytes);
        if (!st.TryGetValue("docProps/ISolidWorksInformation.xml", out var x)) return (null, null);
        var t = System.Text.Encoding.UTF8.GetString(x);
        var m = Regex.Match(t, "SW-MassProp-Config-\\d+\" pid=\"\\d+\"><vt:lpstr>([^<]*)<");
        if (!m.Success) return (null, null);
        var v = m.Groups[1].Value.Split(',').Select(s => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
        return v.Length >= 5 ? (v[3] * 1e9, v[4] * 1e6) : (null, null);
    }

    /// <summary>Точки габарита XT: вершины и дуги окружностей и эллипсов рёбер (направление дуги — по знаку кривой; ребро с
    /// одной вершиной — полная окружность), мм.</summary>
    static List<Vector3d> XtExtentPoints(XtFile f)
    {
        var res = f.OfType("POINT").Select(n => n.V("pvec") * 1000).ToList();
        foreach (var e in f.OfType("EDGE"))
        {
            var cn = f[e.P("curve")]; var h = f[e.P("halfedge")];
            if (cn == null || h == null || cn.Name is not ("CIRCLE" or "ELLIPSE")) continue;
            var o = f[h.P("other")];
            var vE = h.C("sense") != '-' ? f[h.P("vertex")] : o == null ? null : f[o.P("vertex")];
            var vS = h.C("sense") != '-' ? (o == null ? null : f[o.P("vertex")]) : f[h.P("vertex")];
            var c = cn.V("centre"); var z = cn.V("normal").Normalized(); var x = cn.V("x_axis").Normalized(); var y = Vector3d.Cross(z, x);
            double ra = cn.Name == "CIRCLE" ? cn.D("radius") : cn.D("major_radius"), rb = cn.Name == "CIRCLE" ? ra : cn.D("minor_radius");
            double Ang(Vector3d q) => Math.Atan2(Vector3d.Dot(q - c, y) / rb, Vector3d.Dot(q - c, x) / ra);
            double a0 = 0, sweep = 2 * Math.PI;
            if (vS != null && vE != null && f[vS.P("point")] is { } ps && f[vE.P("point")] is { } pe && (ps.V("pvec") - pe.V("pvec")).Length > 1e-12)
            {
                a0 = Ang(ps.V("pvec")); double a1 = Ang(pe.V("pvec"));
                bool ccw = cn.C("sense") != '-';
                sweep = ccw ? a1 - a0 : a0 - a1; sweep = ((sweep % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                if (!ccw) sweep = -sweep;
            }
            for (int i = 0; i <= 64; i++) { double t = a0 + sweep * i / 64; res.Add((c + x * (ra * Math.Cos(t)) + y * (rb * Math.Sin(t))) * 1000); }
        }
        return res;
    }

    /// <summary>Точки габарита тела: вершины и дуги (окружности, эллипсы) его рёбер, мм.</summary>
    static IEnumerable<Vector3d> BodyExtentPoints(TSolid b)
    {
        foreach (var v in b.Vertices) yield return v.P;
        foreach (var e in b.Edges)
            if (e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 or Parametric3D.Kernel.Curves.EllipseCurve3)
                for (int i = 0; i <= 64; i++) yield return e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 64);
    }

    /// <summary>Разбор файла не дольше P3D_SLDTIMEOUT секунд (по умолчанию 300): долгий файл не задерживает остальные.</summary>
    static volatile string sldStage = "";

    /// <summary>Файлы, импорт которых уже даёт годное тело (k182): для них проверка обязательна.</summary>
    static readonly HashSet<string> Gold = new() { "sw_hex_screw", "sw_socket_screw", "sw_steel_nail", "sw_finishing_nail", "sw_hydraulic_valve", "sw_6261k973_roller_chain", "sw_6264k521_resistant_roller_chain", "sw_cutting_wheel", "sw_97633a170_external_retaining_ring", "sw_97633a130_external_retaining_ring", "sw_plastic_bevel_gear", "sw_metal_bevel_gear", "sw_bevel_pinion", "sw_6529k53_metal_miter_gear", "sw_6383k22_steel_ball_bearing", "sw_57545k633_steel_worm", "sw_6010n12_metal_bevel_pinion", "sw_flat_sprocket", "sw_98430a156_external_retaining_rings", "sw_98306a210_steel_clevis_pin", "sw_98296a121_slotted_spring_pin", "sw_90107a038_stainless_steel_washer" };

    static void WithTimeout(string name, Action work)
    {
        sldStage = "read XT";
        int lim = int.TryParse(Environment.GetEnvironmentVariable("P3D_SLDTIMEOUT"), out var l) ? l : 300;
        // k185: разбор — в контексте ядра с отменой: по превышению времени он останавливается на ближайшей точке прерывания.
        // Прежде брошенный разбор считал дальше в фоне и отнимал процессор у следующих файлов (6169K406: перевод 352 с вместо
        // 10 с после таймаута соседа). Мутация P3D_SLDCANCEL=0.
        using var cts = new CancellationTokenSource();
        bool cancelOn = Environment.GetEnvironmentVariable("P3D_SLDCANCEL") != "0";
        var kctx = (Parametric3D.Kernel.KernelContext.Current ?? new Parametric3D.Kernel.KernelContext()).WithCancellation(cts.Token);
        var t = cancelOn ? Task.Run(() => { using var _ = kctx.Enter(); work(); }) : Task.Run(work);
        bool done;
        try { done = t.Wait(TimeSpan.FromSeconds(lim)); }
        catch (AggregateException ae) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException ?? ae).Throw(); throw; }
        if (!done)
        {
            Log(new { test = "sldprt", file = name, timeout = lim, stage = sldStage });
            // k185: профиль и при превышении времени (что успело набраться)
            if (Environment.GetEnvironmentVariable("P3D_SLDPROF") == "1") Console.Error.WriteLine("SLDPROF timeout " + name + "\n" + Parametric3D.Kernel.Diagnostics.Prof.Report());
            if (cancelOn)
            {
                cts.Cancel();
                bool stopped = false; try { stopped = t.Wait(TimeSpan.FromSeconds(60)); } catch (AggregateException) { stopped = true; }
                Log(new { test = "sldprt-cancel", file = name, stopped });
            }
            throw new Exception($"timeout {lim} s at {sldStage}");
        }
    }

    /// <summary>Разбор: у тела с ошибками сетки — какие грани (метка STEP, поверхность, сторона) у сторон без пары / в одну сторону.</summary>
    static string GeometryAuditName(TFace f) => Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name ?? "?";

    static void SldDiag(string file, List<TSolid> bodies, List<ValidationReport> vrs)
    {
        // k184: отход кривой ребра от поверхности — проекция против перебора по плотной сетке параметров
        for (int bi = 0; bi < bodies.Count; bi++)
        {
            var offIds = vrs[bi].Issues.Where(x => x.Code == "curve-off-surface" && x.Element is { }).Select(x => x.Element!.Value).Take(3).ToList();
            foreach (var eid in offIds)
            {
                var e = bodies[bi].Edges.FirstOrDefault(x => x.Id == eid); if (e == null) continue;
                var parts = new List<string>();
                foreach (var ce in e.CoEdges)
                {
                    var sf = ce.Loop?.Face is { } ff ? Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(ff) : null;
                    if (sf == null) continue;
                    double proj = 0, brute = 0; double extMax = 0, extD = -1, extUvLo = 0, extUvHi = 1; int extFail = 0; Vector2d worstUv = default; Vector3d worstP = default;
                    const int GU = 64, GV = 4096;
                    var grid = new Vector3d[(GU + 1) * (GV + 1)];
                    for (int i = 0; i <= GU; i++) for (int j = 0; j <= GV; j++) grid[i * (GV + 1) + j] = sf.PointAt(new Vector2d(i / (double)GU, j / (double)GV));
                    for (int k = 0; k <= 16; k++)
                    {
                        var c = e.Curve; var q = c.PointAt(c.T0 + (c.T1 - c.T0) * k / 16.0);
                        double dp; Vector2d uv; try { uv = sf.ProjectToUV(q); dp = (sf.PointAt(uv) - q).Length; } catch { continue; }
                        double db = double.MaxValue; int bk = 0; for (int gi = 0; gi < grid.Length; gi++) { double dd = (grid[gi] - q).LengthSquared; if (dd < db) { db = dd; bk = gi; } } db = Math.Sqrt(db);
                        if (sf is Parametric3D.Kernel.Surfaces.NurbsSurface ns0)
                        {
                            var h0 = new Vector2d((bk / (GV + 1)) / (double)GU, (bk % (GV + 1)) / (double)GV);
                            try { var uvh = ns0.ProjectToUV(q, h0); db = Math.Min(db, (ns0.PointAt(uvh) - q).Length); } catch { }
                        }
                        if (dp > proj) { proj = dp; worstUv = uv; worstP = q; }
                        brute = Math.Max(brute, db);
                        // k185: продолжение поверхности за край области (Parasolid допускает рёбра на продолжении B-поверхности)
                        if (sf is Parametric3D.Kernel.Surfaces.NurbsSurface nsx)
                            try { if (nsx.ProjectExtended(q, 0.5) is { } ex) { double de = (ex.P - q).Length; if (de < extD || extD < 0) { } extMax = Math.Max(extMax, de); extUvLo = Math.Min(extUvLo, Math.Min(ex.Uv.X, ex.Uv.Y)); extUvHi = Math.Max(extUvHi, Math.Max(ex.Uv.X, ex.Uv.Y)); } else extFail++; } catch { extFail++; }
                    }
                    if (sf is Parametric3D.Kernel.Surfaces.NurbsSurface) parts.Add($"  extended: max dist {extMax:G3}, uv range [{extUvLo:0.####}, {extUvHi:0.####}], failed {extFail}");
                    string extra = sf is Parametric3D.Kernel.Surfaces.RevolvedSurface rvd ? $" base {rvd.Base.GetType().Name} {rvd.Base.PointAt(rvd.Base.T0)}->{rvd.Base.PointAt(rvd.Base.T1)} axis {rvd.Axis.Origin}/{rvd.Axis.W}" : "";
                    parts.Add($"{ce.Loop!.Face!.Tag} {sf.GetType().Name}{extra}: proj {proj:G3} at uv ({worstUv.X:0.####},{worstUv.Y:0.####}) p ({worstP.X:0.###},{worstP.Y:0.###},{worstP.Z:0.###}), grid64x4096 {brute:G3}");
                }
                Log(new { test = "sldprt-curveoff", file, body = bi + 1, edge = eid, curve = e.Curve.GetType().Name, parts });
            }
        }
        for (int bi = 0; bi < bodies.Count; bi++)
        {
            if (vrs[bi].IsValid) continue;
            var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(bodies[bi]);
            var faceOfTri = new Dictionary<int, uint>();
            for (int t = 0; t + 2 < md.Indices.Length; t += 3) faceOfTri[t / 3] = md.Vertices[md.Indices[t]].FaceId;
            var vpos = md.Vertices.Select(v => new Vector3d(Math.Round(v.P.X, 4), Math.Round(v.P.Y, 4), Math.Round(v.P.Z, 4))).ToArray();
            var edges = new Dictionary<(Vector3d, Vector3d), List<(int Tri, int Dir)>>();
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    var a = vpos[md.Indices[t + k]]; var b = vpos[md.Indices[t + (k + 1) % 3]];
                    bool less = a.X < b.X || a.X == b.X && (a.Y < b.Y || a.Y == b.Y && a.Z < b.Z);
                    var key = less ? (a, b) : (b, a);
                    if (!edges.TryGetValue(key, out var l)) edges[key] = l = new();
                    l.Add((t / 3, less ? 1 : -1));
                }
            var cnt = new Dictionary<string, int>();
            foreach (var (key, l) in edges)
            {
                string kind = l.Count == 1 ? "open" : l.Count > 2 ? "multi" : l[0].Dir == l[1].Dir ? "same" : "";
                if (kind == "") continue;
                foreach (var (tri, _) in l)
                {
                    var f = faceOfTri.TryGetValue(tri, out var fid) ? bodies[bi].Faces.FirstOrDefault(x => x.Id == fid) : null;
                    string fd = f == null ? "?" : $"{f.Tag}/{Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f)?.GetType().Name}/{(f.Reversed ? "R" : "N")}";
                    string k2 = kind + " " + fd; cnt[k2] = cnt.GetValueOrDefault(k2) + 1;
                }
            }
            // центроид и площадь сетки каждой упомянутой грани
            var faceStat = new Dictionary<uint, (Vector3d S, double A, int N)>();
            var agree = new Dictionary<uint, (int Yes, int No)>();
            var byId = bodies[bi].Faces.ToDictionary(x => x.Id);
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
            {
                uint fid = md.Vertices[md.Indices[t]].FaceId;
                var a = (Vector3d)md.Vertices[md.Indices[t]].P; var b = (Vector3d)md.Vertices[md.Indices[t + 1]].P; var c = (Vector3d)md.Vertices[md.Indices[t + 2]].P;
                double ar = Vector3d.Cross(b - a, c - a).Length / 2;
                var st = faceStat.GetValueOrDefault(fid); faceStat[fid] = (st.S + (a + b + c) / 3 * ar, st.A + ar, st.N + 1);
                if (byId.TryGetValue(fid, out var ff) && ar > 0)
                {
                    var cn = (a + b + c) / 3; Vector3d fn; try { fn = ff.NormalAt(cn); } catch { continue; }
                    var tn = Vector3d.Cross(b - a, c - a); var ag = agree.GetValueOrDefault(fid);
                    agree[fid] = Vector3d.Dot(tn, fn) >= 0 ? (ag.Yes + 1, ag.No) : (ag.Yes, ag.No + 1);
                }
            }
            var mentioned = cnt.Keys.Select(k => k.Split(' ')[1].Split('/')[0]).Distinct().Take(8).ToList();
            // луч из середины треугольника грани вдоль её нормали: чётное число пересечений с прочими треугольниками — нормаль
            // наружу (верно), нечётное — внутрь
            var tris = new List<(Vector3d A, Vector3d B, Vector3d C, uint F)>();
            for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                tris.Add(((Vector3d)md.Vertices[md.Indices[t]].P, (Vector3d)md.Vertices[md.Indices[t + 1]].P, (Vector3d)md.Vertices[md.Indices[t + 2]].P, md.Vertices[md.Indices[t]].FaceId));
            string Ray(TFace x)
            {
                var mine = tris.Where(q => q.F == x.Id).OrderByDescending(q => Vector3d.Cross(q.B - q.A, q.C - q.A).Length).FirstOrDefault();
                if (mine.F != x.Id) return "?";
                var c = (mine.A + mine.B + mine.C) / 3; var n = Vector3d.Cross(mine.B - mine.A, mine.C - mine.A).Normalized();
                var o = c + n * 1e-4; var dir = (n + new Vector3d(1.234e-3, -2.345e-3, 3.456e-3)).Normalized();
                int hits = 0;
                foreach (var q in tris)
                {
                    var e1 = q.B - q.A; var e2 = q.C - q.A; var pv = Vector3d.Cross(dir, e2); double det = Vector3d.Dot(e1, pv);
                    if (Math.Abs(det) < 1e-18) continue; double inv = 1 / det; var tv = o - q.A; double uu = Vector3d.Dot(tv, pv) * inv; if (uu < 0 || uu > 1) continue;
                    var qv = Vector3d.Cross(tv, e1); double vv = Vector3d.Dot(dir, qv) * inv; if (vv < 0 || uu + vv > 1) continue;
                    if (Vector3d.Dot(e2, qv) * inv > 1e-9) hits++;
                }
                return hits % 2 == 0 ? "out" : "IN";
            }
            var fstats = bodies[bi].Faces.Where(x => mentioned.Contains(x.Tag) && faceStat.ContainsKey(x.Id))
                .Select(x => { var st = faceStat[x.Id]; var cc = st.A > 0 ? st.S / st.A : Vector3d.Zero; var ag = agree.GetValueOrDefault(x.Id); return $"{x.Tag}: tris {st.N} area {st.A:G4} centroid ({cc.X:F3},{cc.Y:F3},{cc.Z:F3}) normal agrees {ag.Yes}/{ag.Yes + ag.No} rev {x.Reversed} ray {Ray(x)} exactArea {AnalyticVolume.FaceArea(x):G4}"; }).ToList();
            var multiCo = bodies[bi].Edges.Where(e => e.CoEdges.Count > 2).Take(4)
                .Select(e => $"{e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} closed {ReferenceEquals(e.V0, e.V1)}: " + string.Join(",", e.CoEdges.Select(c => (c.Loop?.Face?.Tag ?? "?") + (c.Reversed ? "-" : "+")))).ToList();
            if (multiCo.Count > 0) Log(new { test = "sldprt-multicoedge", file, body = bi + 1, multiCo });
            var sameCo = bodies[bi].Edges.Where(e => e.CoEdges.Count == 2 && e.CoEdges[0].Reversed == e.CoEdges[1].Reversed)
                .Select(e => $"{e.Curve.GetType().Name}:{e.CoEdges[0].Loop?.Face?.Tag}|{e.CoEdges[1].Loop?.Face?.Tag}").Take(10).ToList();
            // k184: выгрузка сетки тела, фасет граней и рёбер для разбора вне CI (P3D_SLDDIAGOBJ=каталог)
            if (Environment.GetEnvironmentVariable("P3D_SLDDIAGOBJ") is { Length: > 0 } od)
            {
                Directory.CreateDirectory(od);
                var sb = new System.Text.StringBuilder(); var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var v in md.Vertices) sb.Append(inv, $"v {v.P.X:R} {v.P.Y:R} {v.P.Z:R}\n");
                for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                    sb.Append(inv, $"f {md.Indices[t] + 1} {md.Indices[t + 1] + 1} {md.Indices[t + 2] + 1} {md.Vertices[md.Indices[t]].FaceId}\n");
                foreach (var ff in bodies[bi].Faces)
                {
                    sb.Append(inv, $"F {ff.Id} {ff.Tag} {GeometryAuditName(ff)} rev {ff.Reversed}\n");
                    if (ff.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsf)
                        foreach (var (a, b, c) in fsf.Facets) sb.Append(inv, $"t {a.X:R} {a.Y:R} {a.Z:R} {b.X:R} {b.Y:R} {b.Z:R} {c.X:R} {c.Y:R} {c.Z:R}\n");
                    foreach (var l in ff.Loops)
                    {
                        sb.Append(inv, $"L {(l.IsOuter ? 1 : 0)}\n");
                        foreach (var ce in l.CoEdges)
                        {
                            List<Vector3d> pts; try { pts = Parametric3D.Kernel.Tess.ExactFaceMesher.EdgePoints(ce.Edge).ToList(); } catch { pts = new(); }
                            if (ce.Reversed) pts.Reverse();
                            sb.Append(inv, $"c {ce.Edge.Id} {(ce.Reversed ? 1 : 0)} {ce.Edge.Curve.GetType().Name}");
                            foreach (var q in pts) sb.Append(inv, $" {q.X:R} {q.Y:R} {q.Z:R}");
                            sb.Append('\n');
                        }
                    }
                }
                File.WriteAllText(Path.Combine(od, $"{Path.GetFileNameWithoutExtension(file)}_b{bi + 1}.dump"), sb.ToString());
            }
            Log(new { test = "sldprt-diag", file, body = bi + 1, sameCoEdges = sameCo, faces = cnt.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} ×{kv.Value}").ToList(), fstats });
        }
    }

    /// <summary>k184: импорт и проверка файлов STEP (разбор: тот же STEP с правкой руками).</summary>
    static void StepFileChecks(IEnumerable<string> files)
    {
        foreach (var path in files)
            Check("STEPCHECK " + Path.GetFileName(path), () =>
            {
                var w = new List<string>();
                var part = Exchange.Read(File.ReadAllText(path), out _, w);
                var bodies = part?.Bodies ?? new List<TSolid>();
                var vrs = bodies.Select(x => SolidValidator.Validate(x)).ToList();
                Log(new { test = "stepcheck", file = Path.GetFileName(path), bodies = bodies.Count, valid = vrs.Select(v => v.IsValid).ToList(),
                    errors = vrs.SelectMany(x => x.Issues).Where(x => x.Severity == ValidationSeverity.Error).Take(6).Select(x => x.ToString()).ToList(),
                    volume = bodies.Select(x => AnalyticVolume.Of(x)).ToList(), warnings = w.Take(10).ToList() });
                if (Environment.GetEnvironmentVariable("P3D_SLDDIAG") == "1") SldDiag(Path.GetFileName(path), bodies, vrs);
            });
    }
}
