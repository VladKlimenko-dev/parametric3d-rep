using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Варианты цилиндр/цилиндр: прямое построение (или отказ), изменение объёма, время — для сверки с OpenCascade.</summary>
    static void CylCylVariants()
    {
        string? why = null;
        DirectRound.Debug = m => { if (m.StartsWith("declined")) why = m; };
        TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));
        TSolid Tool(double rad, double tiltDeg, double yOff, double z0, double h)
        {
            var c = PrimitiveOp.Cylinder(new Vector3d(0, yOff, z0), rad, h);
            return tiltDeg == 0 ? c : TransformOp.Copy(c, TransformOp.Rotate(new Vector3d(0, yOff, 0), Vector3d.UnitY, tiltDeg));
        }
        void Run(string name, bool cut, double rad, double tilt, double yOff, double r, bool both)
        {
            var b = new List<TSolid> { Shaft() };
            if (cut) BooleanOp.Subtract(b, Tool(rad, tilt, yOff, -40, 80), new List<string>());
            else BooleanOp.Unite(b, Tool(rad, tilt, yOff, 0, 40), new List<string>());
            double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
            double vm0 = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b[0]));
            var edges = b[0].Edges.Where(e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && (both ? Math.Abs(e.Curve.PointAt(0.5).Z) > 5 : e.Curve.PointAt(0.5).Z > 5)).ToList();
            why = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string res;
            try
            {
                FilletOp.RoundEdges(b, edges.Select(EdgeRef.Of).ToList(), r, new List<string>());
                double v1 = AnalyticVolume.Of(b[0]) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b[0]));
                if (Environment.GetEnvironmentVariable("P3D_CCDUMP") is { } dumpTo && dumpTo.Contains(name.Replace(" ", "_")))
                {
                    using var wtr = new StreamWriter(dumpTo);
                    foreach (var bf in b[0].Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface))
                    {
                        var ss = (Parametric3D.Kernel.Surfaces.SectionSurface)GeometryAudit.AuthoritativeSurface(bf)!;
                        for (int i = 0; i < 400; i++) for (int k = 0; k <= 10; k++)
                        { var q = ss.PointAt(new Vector2d(ss.UMin + (ss.UMax - ss.UMin) * (i + 0.5) / 400, k / 10.0)); wtr.WriteLine($"{q.X:R} {q.Y:R} {q.Z:R}"); }
                        break;
                    }
                }
                double vm1 = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b[0]));
                double div = cut && yOff == 0 ? CuspByDivergence(b[0], 20, new Vector3d(Math.Sin(tilt * Math.PI / 180), 0, Math.Cos(tilt * Math.PI / 180))) : double.NaN;
                res = $"div {div:R} V0 {v0:R} mesh dV {vm1 - vm0:R} dV {v1 - v0:R} est {FilletOp.LastEstimate} guarantee {b[0].Guarantee} valid {SolidValidator.Validate(b[0]).IsValid}";
            }
            catch (Exception e) { res = "EXCEPTION " + e.Message[..Math.Min(120, e.Message.Length)]; }
            Console.WriteLine($"   mesher: passes {Parametric3D.Kernel.Tess.ExactFaceMesher.RefinePasses} bad {Parametric3D.Kernel.Tess.ExactFaceMesher.RefineBad} added {Parametric3D.Kernel.Tess.ExactFaceMesher.RefineAdded} tol {Parametric3D.Kernel.Tess.ExactFaceMesher.LastTol}");
            Console.WriteLine($"VAR {name}: edges {edges.Count} r {r}: {res}; {sw.ElapsedMilliseconds} ms; {why}");
        }
        var only = Environment.GetEnvironmentVariable("P3D_CCVAR");
        if (only == "d30") { DirectRound.Prof = m => Console.WriteLine("   prof " + m); Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); Run("hole30 rim", true, 15, 0, 0, 1, false); foreach (var l in Parametric3D.Kernel.Diagnostics.Prof.Report().Split('\n').Take(25)) Console.WriteLine("   P " + l); DirectRound.Prof = null; return; }
        if (only == "tilt") { Run("hole8 rim", true, 4, 0, 0, 1, false); Run("hole8 tilt30 rim", true, 4, 30, 0, 1, false); return; }
        if (only == "big") { Run("hole8 rim r2", true, 4, 0, 0, 2, false); Run("boss8 r5", false, 8, 0, 0, 5, false); Run("hole30 rim", true, 15, 0, 0, 1, false); return; }
        Run("hole8 rim", true, 4, 0, 0, 1, false);
        Run("hole8 both rims", true, 4, 0, 0, 1, true);
        Run("hole8 rim r2", true, 4, 0, 0, 2, false);
        Run("hole8 rim r3", true, 4, 0, 0, 3, false);
        Run("hole8 off6 rim", true, 4, 0, 6, 1, false);
        Run("hole8 tilt30 rim", true, 4, 30, 0, 1, false);
        Run("hole30 rim", true, 15, 0, 0, 1, false);
        Run("boss8 r1", false, 8, 0, 0, 1, false);
        Run("boss8 r5", false, 8, 0, 0, 5, false);
        Run("boss12 off4 r2", false, 12, 0, 4, 2, false);
        Run("boss8 tilt20 r2", false, 8, 20, 0, 2, false);
        DirectRound.Debug = null;
    }
}
