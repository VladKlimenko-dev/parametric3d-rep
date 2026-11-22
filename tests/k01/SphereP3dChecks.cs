using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

static partial class Program
{
    static string SpherePath()
    {
        var local = Path.Combine("tests", "k01", "data", "sphere.p3d");
        if (File.Exists(local)) return local;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "k01", "data", "sphere.p3d");
            if (File.Exists(p)) return p;
        }
        throw new Exception("sphere.p3d not found");
    }

    static (Part Part, ParameterTable Pars) LoadSphere()
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(SpherePath()));
        return (p, pars);
    }

    /// <summary>Рёбра между двумя плоскими гранями — для точного прямого скругления/фаски.</summary>
    static List<TEdge> PlanarConvexEdges(TSolid body, int skip = 0)
    {
        var list = new List<TEdge>();
        foreach (var e in body.Edges)
        {
            var fa = e.FaceA; var fb = e.FaceB;
            if (fa == null || fb == null) continue;
            var sa = GeometryAudit.AuthoritativeSurface(fa);
            var sb = GeometryAudit.AuthoritativeSurface(fb);
            if (ExactEdges.ShapeOf(sa) is not Ssi.Plane || ExactEdges.ShapeOf(sb) is not Ssi.Plane) continue;
            // выпуклое ребро: середина внутри, нормали наружу, материал за плоскостями
            var mid = e.Curve.PointAt(0.5);
            var na = fa.NormalAt(mid); var nb = fb.NormalAt(mid);
            var va = Vector3d.Dot(na, mid - ((Ssi.Plane)ExactEdges.ShapeOf(sa)!).P);
            var vb = Vector3d.Dot(nb, mid - ((Ssi.Plane)ExactEdges.ShapeOf(sb)!).P);
            if (va <= 0 && vb <= 0) list.Add(e);
        }
        return list.Skip(skip).ToList();
    }

    static void SphereP3dChecks()
    {
        Check("SPHERE-P3D-TOPOLOGY", () =>
        {
            var (part, pars) = LoadSphere();
            var sw = Stopwatch.StartNew();
            part.Rebuild(pars);
            sw.Stop();
            var errs = part.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error).ToList();
            Log(new { test = "SPHERE-P3D-TOPOLOGY", features = part.Features.Count, kinds = string.Join(",", part.Features.Select(f => f.GetType().Name)), bodies = part.Bodies.Count, faces = part.Bodies.Sum(b => b.Faces.Count()), volume = part.Volume, guarantee = string.Join(",", part.Bodies.Select(b => b.Guarantee)), ms = Math.Round(sw.Elapsed.TotalMilliseconds), errors = string.Join("; ", errs) });
            Require(errs.Count == 0, "feature errors: " + string.Join("; ", errs));
            Require(part.Features.OfType<RevolveFeature>().Count() == 1, "one revolve expected");
            Require(part.Features.OfType<ExtrudeFeature>().Count() == 2, "two extrudes expected");
            Require(part.Features.OfType<RoundFeature>().Count() == 2, "two rounds expected");
            Require(part.Features.OfType<ChamferFeature>().Count() == 1, "one chamfer expected");
            Require(part.Bodies.Count == 1 && part.Bodies.All(b => SolidValidator.Validate(b).IsValid), "bodies must be one and valid");
            Require(part.Bodies.All(b => b.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric or GuaranteeLevel.ExactPolyhedral), "approximate result: " + string.Join(",", part.Bodies.Select(b => b.Guarantee)));
        });

        Check("SPHERE-P3D-ROUND-MATRIX", () =>
        {
            var radii = new[] { 2.0, 8.0, 16.0 };
            var fails = new List<string>();
            foreach (double r in radii)
            foreach (bool multi in new[] { false, true })
            {
                var (part, pars) = LoadSphere();
                part.StageIndex = 3;                    // база без штатных скруглений/фаски
                part.Rebuild(pars);
                var baseBody = part.Bodies.Single();
                var edges = PlanarConvexEdges(baseBody);
                Require(edges.Count >= 2, "not enough planar edges on the base body");
                var rf = new RoundFeature("MatrixRound") { Radius = new ValueRef(r) };
                rf.Edges.Add(EdgeRef.Of(edges[0]));
                if (multi) rf.Edges.Add(EdgeRef.Of(edges[1]));
                part.AddFeature(rf);
                var sw = Stopwatch.StartNew();
                part.Rebuild(pars);
                sw.Stop();
                bool valid = part.Bodies.All(b => SolidValidator.Validate(b).IsValid);
                bool exact = part.Bodies.All(b => b.Guarantee == GuaranteeLevel.ExactAnalytic);
                Log(new { test = "SPHERE-P3D-ROUND-MATRIX", r, multi, err = rf.Error, valid, exact, guarantee = string.Join(",", part.Bodies.Select(b => b.Guarantee)), ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1) });
                if (rf.Error != null || !valid) fails.Add($"round R{r} multi={multi}: {(rf.Error ?? "invalid")}");
                else if (!exact) fails.Add($"round R{r} multi={multi}: not ExactAnalytic ({string.Join(",", part.Bodies.Select(b => b.Guarantee))})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("SPHERE-P3D-CHAMFER-MATRIX", () =>
        {
            var cases = new[] { (D1: 1.0, D2: 1.0), (D1: 3.0, D2: 5.0), (D1: 8.0, D2: 8.0) };
            var fails = new List<string>();
            foreach (var (d1, d2) in cases)
            foreach (bool multi in new[] { false, true })
            {
                var (part, pars) = LoadSphere();
                part.StageIndex = 3;
                part.Rebuild(pars);
                var baseBody = part.Bodies.Single();
                var edges = PlanarConvexEdges(baseBody);
                Require(edges.Count >= 2, "not enough planar edges on the base body");
                var cf = new ChamferFeature("MatrixChamfer") { D1 = new ValueRef(d1), D2 = new ValueRef(d2) };
                cf.Edges.Add(EdgeRef.Of(edges[0]));
                if (multi) cf.Edges.Add(EdgeRef.Of(edges[1]));
                part.AddFeature(cf);
                var sw = Stopwatch.StartNew();
                part.Rebuild(pars);
                sw.Stop();
                bool valid = part.Bodies.All(b => SolidValidator.Validate(b).IsValid);
                bool exact = part.Bodies.All(b => b.Guarantee == GuaranteeLevel.ExactAnalytic);
                Log(new { test = "SPHERE-P3D-CHAMFER-MATRIX", d1, d2, multi, err = cf.Error, valid, exact, guarantee = string.Join(",", part.Bodies.Select(b => b.Guarantee)), ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1) });
                if (cf.Error != null || !valid) fails.Add($"chamfer {d1}x{d2} multi={multi}: {(cf.Error ?? "invalid")}");
                else if (!exact) fails.Add($"chamfer {d1}x{d2} multi={multi}: not ExactAnalytic ({string.Join(",", part.Bodies.Select(b => b.Guarantee))})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
