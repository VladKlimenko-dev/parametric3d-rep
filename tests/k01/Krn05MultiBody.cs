using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// KRN-05 (05.10): ЦЕПОЧКИ НАД НЕСКОЛЬКИМИ ТЕЛАМИ ПОД PreserveBRep. Три тела (два бруска и цилиндр); вычитание резца,
    /// задевающего два из них; объединение с перемычкой, лежащей на обоих брусках (тела сливаются); пересечение, оставляющее
    /// одно тело (цилиндр вне — удалён); вычитание отверстия в перемычке. Каждый шаг: принят,
    /// все тела точные (ExactAnalytic/ExactQuadric) и годные, число тел и объём — по формулам (1e-9 относительной). Шаг, который
    /// точным путём не строится (шар, погружённый в перемычку на 1e-6 мм), — честный отказ с прежними телами, без фасет.
    /// </summary>
    static void Krn05MultiBodyChecks()
    {
        Check("KRN05-MULTIBODY-CHAIN-PRESERVE-BREP", () =>
        {
            var fails = new List<string>();
            using var _c = new KernelContext(KernelSettings.Capture() with { GeometryPolicy = GeometryPolicy.PreserveBRep }).Enter();
            var bodies = new List<TSolid> { Box(0, 0, 0, 20, 20, 20), Box(40, 0, 0, 20, 20, 20), PrimitiveOp.Cylinder(new Vector3d(100, 10, 0), 8, 20) };
            double V = 8000 + 8000 + Math.PI * 64 * 20;
            void Step(string name, TSolid tool, BooleanOp.BooleanKind k, double want, int count)
            {
                var r = BooleanOp.Evaluate(bodies, tool, k, OperationPolicy.Strict);
                if (!r.Accepted) { fails.Add($"{name}: refused {r.FailureCode}"); return; }
                bodies = r.Bodies.ToList();
                double v = bodies.Sum(OpVolume);
                string g = string.Join(",", bodies.Select(b => b.Guarantee));
                Console.WriteLine($"  {name}: {bodies.Count} bodies, V {v:R} (want {want:R}), {g}");
                if (bodies.Count != count) fails.Add($"{name}: {bodies.Count} bodies, want {count}");
                if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} want {want:R}");
                foreach (var b in bodies)
                {
                    // тело, которого операция не коснулась, — то же входное (уровень — его собственный или по геометрии: все грани на
                    // точных негранёных поверхностях); новые — с уровнем построения
                    bool exactB = b.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric
                        || b.Guarantee == GuaranteeLevel.Unspecified && b.Faces.All(f => GeometryAudit.AuthoritativeSurface(f) is { } sf && sf is not Parametric3D.Kernel.Surfaces.FacetedSurface);
                    if (!exactB) fails.Add($"{name}: body {b.Guarantee}");
                    if (r.Guarantee is not (GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric)) fails.Add($"{name}: result {r.Guarantee}");
                    var val = SolidValidator.Validate(b); if (!val.IsValid) fails.Add($"{name}: invalid {val.Summary()}");
                }
                V = want;
            }
            Step("subtract across two", Box(10, 5, 5, 40, 10, 10), BooleanOp.BooleanKind.Subtract, V - 2000, 3);
            Step("unite bridge", Box(10, 0, 20, 40, 20, 5), BooleanOp.BooleanKind.Unite, V + 4000, 2);
            Step("intersect keeps one", Box(-1, -1, -1, 62, 22, 30), BooleanOp.BooleanKind.Intersect, V - Math.PI * 64 * 20, 1);
            Step("hole in bridge", PrimitiveOp.Cylinder(new Vector3d(30, 10, 15), 3, 15), BooleanOp.BooleanKind.Subtract, V - Math.PI * 9 * 5, 1);
            // шаг без точного пути: отказ, тела прежние, никаких фасет
            var before = bodies.ToList(); double vb = bodies.Sum(OpVolume);
            var rs = BooleanOp.Evaluate(bodies, PrimitiveOp.Sphere(new Vector3d(20, 10, 30 - 1e-6), 5), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            if (rs.Accepted)
            {
                if (rs.Bodies.Any(b => b.Guarantee is not (GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric))) fails.Add($"grazing sphere: accepted as {string.Join(",", rs.Bodies.Select(b => b.Guarantee))} under PreserveBRep");
            }
            else
            {
                if (rs.Bodies.Count != 0) fails.Add("grazing sphere: refusal with bodies");
                if (!bodies.SequenceEqual(before) || bodies.Sum(OpVolume) != vb) fails.Add("grazing sphere: refusal changed the input bodies");
            }
            Console.WriteLine($"  grazing sphere: {(rs.Accepted ? "accepted exactly" : "refused " + rs.FailureCode)}");
            Log(new { test = "KRN05-MULTIBODY-CHAIN-PRESERVE-BREP", fails = fails.Count, sphere = rs.Accepted ? "exact" : rs.FailureCode });
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
