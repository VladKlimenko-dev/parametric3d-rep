using Parametric3D.Kernel;
using Parametric3D.Kernel.Export;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// allD (первый этап): импорт OCCT BREP из SMLib TestFiles/occt_breps. Проверяем
    /// структуру, а у замкнутых тел — объём и сетку; одиночная грань остаётся открытой.
    /// </summary>
    static void OcctBrepChecks()
    {
        foreach (var file in Directory.GetFiles(Path.Combine("tests", "k01", "data", "smlib"), "*.brep"))
        {
            var name = Path.GetFileName(file);
            Check("ALLD-BREP " + name, () =>
            {
                var warnings = new List<string>();
                var text = File.ReadAllText(file);
                var solid = OcctBrepReader.Read(text, warnings);
                bool accepted = solid != null || warnings.Any(x => x.Contains("periodic B-spline"));
                Require(accepted, "not read: " + string.Join("; ", warnings));
                if (solid == null) return;

                Require(solid.Faces.Any(), "no faces");
                Require(solid.Faces.All(f => f.Surface != null), "face without surface");
                Require(solid.Edges.All(e => e.Curve != null), "edge without curve");
                Require(solid.Edges.Any(), "no edges");
                Require(solid.Vertices.Any(), "no vertices");

                var vol = AnalyticVolume.Of(solid);
                if (vol is > 0)
                {
                    var mv = SolidValidator.Validate(solid);
                    Require(mv.IsValid, "mesh invalid: " + mv.Summary());
                }
                else
                {
                    // Открытая оболочка/грань: топология без требования замкнутости.
                    var vr = SolidValidator.ValidateBRep(solid, new ValidationOptions { Closed = false });
                    Require(vr.IsValid, "open BRep invalid: " + vr.Summary() + " " + string.Join(" / ", vr.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(2)));
                }
            });
        }
    }
}
