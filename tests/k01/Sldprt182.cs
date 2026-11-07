using System.Text.RegularExpressions;
using Parametric3D.Kernel.Export.Parasolid;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k182: ИМПОРТ SOLIDWORKS (.SLDPRT) — контейнер → Parasolid XT → STEP → тела (Exchange.Read). Файлы пользователя (винты
    /// McMaster-Carr: шестигранная головка, полукруглая с крестом, под шестигранник; резьба — B-поверхности, скругления
    /// шлица — BLENDED_EDGE). Проверяется: поток XT дочитан до конца, ни одна грань не пропущена, тело одно и годно, объём и
    /// площадь — как в свойствах массы SolidWorks (SW-MassProp-Config: x, y, z центра, объём, площадь… в СИ).
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
                var b = part?.Bodies.FirstOrDefault();
                var vr = b == null ? null : SolidValidator.Validate(b);
                double? vol = b == null ? null : AnalyticVolume.Of(b) ?? part!.Volume;
                double? area = b == null ? null : AnalyticVolume.AreaOf(b);
                var (swVol, swArea) = SwMassProps(bytes);
                Log(new { test = "sldprt", file = Path.GetFileName(path), f.Modeller, f.Schema, nodes = f.Order.Count, f.Truncated, faces = counts.GetValueOrDefault("FACE"),
                    surfaces = counts.Where(kv => kv.Key is "PLANE" or "CYLINDER" or "CONE" or "SPHERE" or "TORUS" or "B_SURFACE" or "BLENDED_EDGE" or "SWEPT_SURF" or "SPUN_SURF" or "OFFSET_SURF").ToDictionary(kv => kv.Key, kv => kv.Value),
                    curves = counts.Where(kv => kv.Key is "LINE" or "CIRCLE" or "ELLIPSE" or "B_CURVE" or "INTERSECTION" or "SP_CURVE" or "TRIMMED_CURVE").ToDictionary(kv => kv.Key, kv => kv.Value),
                    stepKb = step.Length / 1024, bodies = part?.Bodies.Count ?? 0, bodyFaces = b?.Faces.Count() ?? 0, valid = vr?.IsValid, verdict = vr?.Summary(),
                    errors = vr?.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(4).Select(x => x.ToString()).ToList(),
                    guarantee = b?.Guarantee.ToString(), volume = vol, area, swVolume = swVol, swArea, msConvert = Math.Round(tConv), msTotal = Math.Round(tAll), warnings = w.Take(12).ToList() });
                Require(!f.Truncated, "XT stream not read to the end");
                Require(!w.Any(x => x.Contains("were left out") || x.Contains("is not supported; the edge")), "faces or edges left out: " + string.Join(" | ", w.Where(x => x.Contains("left out"))));
                Require(part != null && part.Bodies.Count == 1, $"bodies {part?.Bodies.Count ?? 0}");
                Require(vr!.IsValid, "invalid body: " + vr.Summary());
                if (swArea is double sa && area is double a) Require(Math.Abs(a - sa) <= Math.Max(2, 2e-3 * sa), $"area {a:F2} vs SolidWorks {sa:F2} mm²");
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
