using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// KRN-03 (03.11): p-КРИВЫЕ, КРИВЫЕ И ПОВЕРХНОСТИ ИМПОРТА IGES СОГЛАСОВАНЫ В БЮДЖЕТЕ. Файлы OpenCascade (17) и наш IGES
    /// тел корпуса (отверстие, вырез шаром, бобышка-тор, конус, повёрнутые копии): у каждого полуребра на точной поверхности —
    /// p-кривая; её отклонение от ребра — не больше max(модельная точность, допуск ребра, бюджет лечения импорта); кольца
    /// граней (отверстия IGES — прежний дефект) — с p-кривыми того же бюджета.
    /// </summary>
    static void Krn03IgesPCurveChecks()
    {
        Check("KRN03-IGES-PCURVES", () =>
        {
            var fails = new List<string>(); int bodies = 0, coedges = 0; double worst = 0;
            var inputs = new List<(string N, string Text)>();
            var dir = Path.GetDirectoryName(Fixture("occt_iges/manifest.tsv"))!;
            foreach (var row in File.ReadAllLines(Path.Combine(dir, "manifest.tsv")).Skip(1)) { var c = row.Split('\t'); inputs.Add(("occt " + c[0], File.ReadAllText(Path.Combine(dir, c[0] + ".igs")))); }
            var w = new List<string>();
            var rx = TransformOp.Rotate(new Vector3d(5, 3, 1), new Vector3d(0.3, 0.7, 0.2).Normalized(), 41);
            var ours = new List<(string, TSolid)>();
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), w); ours.Add(("hole", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Sphere(new Vector3d(20, 15, 20), 8), w); ours.Add(("sphere cut", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Unite(l, PrimitiveOp.Torus(new Vector3d(20, 15, 20), 9, 3), w); ours.Add(("torus boss", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Cone(new Vector3d(20, 15, 8), 6, 0, 14), w); ours.Add(("cone cut", l[0])); }
            foreach (var (n, b) in ours.ToList()) ours.Add((n + " rotated", TransformOp.Copy(b, rx)));
            foreach (var (n, b) in ours) inputs.Add(("ours " + n, Parametric3D.Kernel.Export.IgesSolid.Write(b, new List<string>())));
            foreach (var (name, text) in inputs)
            {
                TSolid? s;
                try { s = Parametric3D.Kernel.Export.IgesImport.Read(text, new List<string>()); }
                catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { fails.Add($"{name}: {ex.Message}"); continue; }
                if (s == null) { fails.Add($"{name}: not read"); continue; }
                bodies++;
                PCurves.Build(s);
                double budget = Math.Max(ToleranceContext.Current.ModelAccuracy, ToleranceContext.Current.ImportHeal);
                int missing = 0, over = 0; double mx = 0;
                foreach (var f in s.Faces)
                {
                    var sf = GeometryAudit.AuthoritativeSurface(f); if (sf == null) continue;
                    foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
                    {
                        coedges++;
                        if (ce.PCurve == null) { missing++; continue; }
                        double lim = Math.Max(budget, ce.Edge.Tolerance);
                        mx = Math.Max(mx, ce.PCurveDeviation);
                        if (ce.PCurveDeviation > lim) { over++; if (Environment.GetEnvironmentVariable("P3D_PCDBG") == "1") Console.WriteLine($"  OVER {name}: surf {sf.GetType().Name} curve {ce.Edge.Curve.GetType().Name} pc {ce.PCurve.GetType().Name} exact {ce.PCurve.IsExact} dev {ce.PCurveDeviation:E2} tol {ce.Edge.Tolerance:E2} onSurf {Enumerable.Range(0, 9).Max(k => { var q = PCurves.At(ce, k / 8.0); return (sf.PointAt(sf.ProjectToUV(q)) - q).Length; }):E2} sweep {(ce.Edge.Curve as Parametric3D.Kernel.Curves.ArcCurve3)?.Sweep:F3} R {(ce.Edge.Curve as Parametric3D.Kernel.Curves.ArcCurve3)?.Radius:G4} uv {string.Join(" ", Enumerable.Range(0, 9).Select(k => { var q = PCurves.At(ce, k / 8.0); var a = sf.ProjectToUV(q); var b = ce.PCurve.PointAt(k / 8.0); return $"[{a.X:F4},{a.Y:F4}|{b.X:F4},{b.Y:F4}]"; }))} base {(sf as Parametric3D.Kernel.Surfaces.RevolvedSurface)?.Base.GetType().Name} A {(sf as Parametric3D.Kernel.Surfaces.RevolvedSurface)?.A0:F3}..{(sf as Parametric3D.Kernel.Surfaces.RevolvedSurface)?.A1:F3}"); }
                    }
                }
                worst = Math.Max(worst, mx);
                if (missing > 0 || over > 0) fails.Add($"{name}: {missing} coedges without p-curve, {over} over budget (max {mx:E2})");
            }
            Log(new { test = "KRN03-IGES-PCURVES", bodies, coedges, worst });
            Require(bodies >= 25 && fails.Count == 0, string.Join("; ", fails.Take(10)));
        });
    }
}
