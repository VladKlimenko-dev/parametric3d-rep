using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// allD (первый этап): импорт OCCT BREP из SMLib TestFiles/occt_breps. Тело обязано быть
    /// замкнутым (B-rep и сетка) и иметь положительный точный объём.
    /// </summary>
    static void OcctBrepChecks()
    {
        string dir = "tests/k01/data/smlib";
        if (!Directory.Exists(dir)) { Log(new { test = "ALLD-BREP", ok = false, error = "no directory " + dir }); return; }
        foreach (var file in Directory.EnumerateFiles(dir, "*.brep").OrderBy(x => x, StringComparer.Ordinal))
        {
            Check($"ALLD-BREP {Path.GetFileName(file)}", () =>
            {
                var warnings = new List<string>();
                var solid = Parametric3D.Kernel.Export.OcctBrepReader.Read(File.ReadAllText(file), warnings);
                Require(solid != null, "not read: " + string.Join(" | ", warnings.Take(3)));
                var vr = SolidValidator.Validate(solid!);
                Require(vr.IsValid, vr.Summary());
                var vol = AnalyticVolume.Of(solid!);
                Require(vol is > 0, $"exact volume {vol}");
                Log(new { test = "ALLD-BREP", file = Path.GetFileName(file), faces = solid!.Faces.Count(), volume = vol });
            });
        }
    }
}
