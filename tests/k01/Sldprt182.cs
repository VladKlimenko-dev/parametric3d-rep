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
        var list = files?.ToList() ?? Directory.GetFiles(Path.Combine("tests", "k01", "data"), "sw_*.SLDPRT").OrderBy(x => x).ToList();
        if (goldOnly && files == null) list = list.Where(x => Gold.Contains(Path.GetFileNameWithoutExtension(x))).ToList();
        foreach (var path in list)
        {
            Check("SLDPRT-IMPORT-K182 " + Path.GetFileName(path), () => WithTimeout(Path.GetFileName(path), () =>
            {
                var w = new List<string>();
                var bytes = File.ReadAllBytes(path);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var xt = SldprtFile.PartitionXt(bytes, w);
                var f = XtReader.Read(xt);
                var counts = f.Order.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.Count());
                sldStage = "convert";
                string step = XtToStep.Convert(f, Path.GetFileNameWithoutExtension(path), w);
                double tConv = sw.Elapsed.TotalMilliseconds;
                sldStage = $"read STEP (convert {tConv:F0} ms, {step.Length / 1024} KB)";
                if (Environment.GetEnvironmentVariable("P3D_SLDOUT") is { Length: > 0 } od) { Directory.CreateDirectory(od); File.WriteAllText(Path.Combine(od, Path.GetFileNameWithoutExtension(path) + ".step"), step); }
                bool prof = Environment.GetEnvironmentVariable("P3D_SLDPROF") == "1";
                if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
                var part = Exchange.Read(step, out _, w);
                if (prof) { Console.Error.WriteLine("SLDPROF " + Path.GetFileName(path) + "\n" + Parametric3D.Kernel.Diagnostics.Prof.Report()); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
                double tAll = sw.Elapsed.TotalMilliseconds;
                sldStage = $"validate (convert {tConv:F0} ms, read {sw.Elapsed.TotalMilliseconds - tConv:F0} ms)";
                var bodies = part?.Bodies ?? new List<TSolid>();
                var b = bodies.FirstOrDefault();
                var vrs = bodies.Select(x => SolidValidator.Validate(x)).ToList();
                var vr = vrs.FirstOrDefault(x => !x.IsValid) ?? vrs.FirstOrDefault();
                double vol = bodies.Sum(x => AnalyticVolume.Of(x) ?? 0), area = bodies.Sum(x => AnalyticVolume.AreaOf(x) ?? 0);
                // Габарит по вершинам и дугам (окружности, эллипсы) рёбер XT и тех же рёбер импортированных тел — должны совпасть; объём не больше объёма габарита.
                var xtPts = XtExtentPoints(f);
                var bodyPts = bodies.SelectMany(BodyExtentPoints).ToList();
                (Vector3d lo, Vector3d hi) Box(List<Vector3d> pts) => pts.Count == 0 ? (Vector3d.Zero, Vector3d.Zero)
                    : (new Vector3d(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z)), new Vector3d(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z)));
                var (xl, xh) = Box(xtPts); var (bl, bh) = Box(bodyPts);
                double boxGap = Math.Max((xl - bl).Length, (xh - bh).Length), boxVol = (xh.X - xl.X) * (xh.Y - xl.Y) * (xh.Z - xl.Z);
                int xtBodies = f.OfType("BODY").Count(n => n.I("body_type") == 1);
                var (swVol, swArea) = SwMassProps(bytes);
                Log(new { test = "sldprt", file = Path.GetFileName(path), f.Modeller, f.Schema, nodes = f.Order.Count, f.Truncated, faces = counts.GetValueOrDefault("FACE"),
                    surfaces = counts.Where(kv => kv.Key is "PLANE" or "CYLINDER" or "CONE" or "SPHERE" or "TORUS" or "B_SURFACE" or "BLENDED_EDGE" or "SWEPT_SURF" or "SPUN_SURF" or "OFFSET_SURF").ToDictionary(kv => kv.Key, kv => kv.Value),
                    curves = counts.Where(kv => kv.Key is "LINE" or "CIRCLE" or "ELLIPSE" or "B_CURVE" or "INTERSECTION" or "SP_CURVE" or "TRIMMED_CURVE").ToDictionary(kv => kv.Key, kv => kv.Value),
                    stepKb = step.Length / 1024, xtBodies, bodies = bodies.Count, bodyFaces = bodies.Sum(x => x.Faces.Count()), valid = vrs.Count > 0 && vrs.All(x => x.IsValid), verdict = vr?.Summary(),
                    errors = vrs.SelectMany(x => x.Issues).Where(x => x.Severity == ValidationSeverity.Error).Take(4).Select(x => x.ToString()).ToList(),
                    guarantee = b?.Guarantee.ToString(), volume = vol, area, boxVol, boxGap, xtBox = new[] { xl.X, xl.Y, xl.Z, xh.X, xh.Y, xh.Z }, bodyBox = new[] { bl.X, bl.Y, bl.Z, bh.X, bh.Y, bh.Z }, swVolume = swVol, swArea, msConvert = Math.Round(tConv), msTotal = Math.Round(tAll), warnings = w.Take(12).ToList() });
                // обязательная планка — для файлов, которые уже годны (ГОДНЫЕ); остальные — для сведения (лог), пока импорт
                // доводится (k183+): провал такого файла не валит прогон
                if (!Gold.Contains(Path.GetFileNameWithoutExtension(path)) && Environment.GetEnvironmentVariable("P3D_SLDSTRICT") != "1") return;
                Require(!f.Truncated, "XT stream not read to the end");
                Require(!w.Any(x => x.Contains("were left out") || x.Contains("is not supported; the edge")), "faces or edges left out: " + string.Join(" | ", w.Where(x => x.Contains("left out"))));
                Require(bodies.Count == xtBodies, $"bodies {bodies.Count} of {xtBodies}");
                Require(vrs.All(x => x.IsValid), "invalid body: " + vr?.Summary());
                Require(boxGap <= 1e-3, $"bounding box differs from the XT edges by {boxGap:G3} mm");
                Require(vol > 0 && vol <= boxVol * (1 + 1e-6), $"volume {vol:F2} mm³ outside (0, {boxVol:F2}] of the bounding box");
            }));
        }
    }

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
        var t = Task.Run(work);
        bool done;
        try { done = t.Wait(TimeSpan.FromSeconds(lim)); }
        catch (AggregateException ae) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException ?? ae).Throw(); throw; }
        if (!done) { Log(new { test = "sldprt", file = name, timeout = lim, stage = sldStage }); throw new Exception($"timeout {lim} s at {sldStage}"); }
    }
}
