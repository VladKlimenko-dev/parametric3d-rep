using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
static partial class Program
{
    internal static void Bench()
    {
        foreach (var id in new[] { "S-HOLE", "S-HOLES8", "S-SPHERE", "S-SPHERE-IN", "S-ROUNDHOLE" })
        {
            var w = new List<string>(); var s = Scenario(id, w)[0];
            for (int k = 0; k < 3; k++) { SolidValidator.ValidateBRep(s); TopologyCheck.Validate(s); }
            var sw = Stopwatch.StartNew(); for (int k = 0; k < 50; k++) SolidValidator.ValidateBRep(s); double a = sw.Elapsed.TotalMilliseconds / 50;
            sw.Restart(); for (int k = 0; k < 50; k++) TopologyCheck.Validate(s); double b = sw.Elapsed.TotalMilliseconds / 50;
            double ex = 0; if (s.Exact != null) { SolidValidator.ValidateExact(s.Exact.Solid); sw.Restart(); for (int k = 0; k < 10; k++) SolidValidator.ValidateExact(s.Exact.Solid); ex = sw.Elapsed.TotalMilliseconds / 10; }
            var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(s);
            for (int k = 0; k < 2; k++) SolidValidator.ValidateMesh(m);
            sw.Restart(); for (int k = 0; k < 10; k++) SolidValidator.ValidateMesh(m); double c = sw.Elapsed.TotalMilliseconds / 10;
            sw.Restart(); for (int k = 0; k < 10; k++) Parametric3D.Kernel.Tess.Tessellator.Tessellate(s); double t = sw.Elapsed.TotalMilliseconds / 10;
            Log(new { bench = id, validateExactMs = ex, validateMeshMs = c, tessellateMs = t, triangles = m.Indices.Length / 3, validateBRepMs = a, topologyCheckMs = b, faces = s.Faces.Count(), edges = s.Edges.Count() });
        }
    }
}
