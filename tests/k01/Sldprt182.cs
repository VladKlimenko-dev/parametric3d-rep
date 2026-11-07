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
    /// Режим «sldprt файл…» — разбор любых файлов с выводом STEP рядом (P3D_SLDOUT=папка).
    /// </summary>
    static void SldprtChecks(IEnumerable<string>? files = null)
    {
        var list = files?.ToList() ?? Directory.GetFiles(Path.Combine("tests", "k01", "data"), "sw_*.SLDPRT").OrderBy(x => x).ToList();
        foreach (var path in list)
        {
            Check("SLDPRT-IMPORT-K182 " + Path.GetFileName(path), () =>
            {
                var w = new List<string>();
                var bytes = File.ReadAllBytes(path);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var xt = SldprtFile.PartitionXt(bytes, w);
                var f = XtReader.Read(xt);
                var counts = f.Order.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.Count());
                string step = XtToStep.Convert(f, Path.GetFileNameWithoutExtension(path), w);
                double tConv = sw.Elapsed.TotalMilliseconds;
                if (Environment.GetEnvironmentVariable("P3D_SLDOUT") is { Length: > 0 } od) { Directory.CreateDirectory(od); File.WriteAllText(Path.Combine(od, Path.GetFileNameWithoutExtension(path) + ".step"), step); }
                var part = Exchange.Read(step, out _, w);
                double tAll = sw.Elapsed.TotalMilliseconds;
                var bodies = part?.Bodies ?? new List<TSolid>();
                var b = bodies.FirstOrDefault();
                var vrs = bodies.Select(x => SolidValidator.Validate(x)).ToList();
                var vr = vrs.FirstOrDefault(x => !x.IsValid) ?? vrs.FirstOrDefault();
                double vol = bodies.Sum(x => AnalyticVolume.Of(x) ?? 0), area = bodies.Sum(x => AnalyticVolume.AreaOf(x) ?? 0);
                // Габарит по точкам XT (вершины) и по вершинам импортированных тел — должны совпасть; объём не больше объёма габарита.
                var xtPts = f.OfType("POINT").Select(n => n.V("pvec") * 1000).ToList();
                var bodyPts = bodies.SelectMany(x => x.Vertices).Select(v => v.P).ToList();
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
                Require(!f.Truncated, "XT stream not read to the end");
                Require(!w.Any(x => x.Contains("were left out") || x.Contains("is not supported; the edge")), "faces or edges left out: " + string.Join(" | ", w.Where(x => x.Contains("left out"))));
                Require(bodies.Count == xtBodies, $"bodies {bodies.Count} of {xtBodies}");
                Require(vrs.All(x => x.IsValid), "invalid body: " + vr?.Summary());
                Require(boxGap <= 1e-3, $"bounding box differs from the XT vertices by {boxGap:G3} mm");
                Require(vol > 0 && vol <= boxVol * (1 + 1e-6), $"volume {vol:F2} mm³ outside (0, {boxVol:F2}] of the bounding box");
            });
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
}
