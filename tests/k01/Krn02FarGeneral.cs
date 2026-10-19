using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// KRN-02 (02.1): ОБЩАЯ БУЛЕВА ДАЛЕКО ОТ НАЧАЛА. Пары с кривыми поверхностями (наклонный цилиндр, шар, конус, тор,
    /// два цилиндра под углом) у начала и сдвинутые на 1e4, 1e5, 3e5 мм (с поворотом): тот же маршрут (точный), та же
    /// топология, объём до 1e-9 относительной, B-rep годен; сетка показа (float) — годна до 1e5 мм.
    /// </summary>
    static void Krn02FarGeneralChecks()
    {
        Check("KRN02-FAR-GENERAL", () =>
        {
            var fails = new List<string>(); var rows = new List<string>();
            var cases = new List<(string N, Func<TSolid> A, Func<TSolid> B, BooleanOp.BooleanKind K)>
            {
                ("box − tilted cylinder", () => Box(0, 0, 0, 40, 30, 20), () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(20, 15, -20), 5, 60), TransformOp.Rotate(new Vector3d(20, 15, 10), new Vector3d(1, 0.3, 0).Normalized(), 28)), BooleanOp.BooleanKind.Subtract),
                ("box − sphere", () => Box(0, 0, 0, 40, 30, 20), () => PrimitiveOp.Sphere(new Vector3d(35, 25, 15), 9), BooleanOp.BooleanKind.Subtract),
                ("box ∩ cone", () => Box(0, 0, 0, 40, 30, 20), () => PrimitiveOp.Cone(new Vector3d(20, 15, -5), 14, 4, 30), BooleanOp.BooleanKind.Intersect),
                ("box ∪ torus", () => Box(0, 0, 0, 40, 30, 20), () => PrimitiveOp.Torus(new Vector3d(20, 15, 20), 9, 3), BooleanOp.BooleanKind.Unite),
                ("cylinder × cylinder", () => PrimitiveOp.Cylinder(new Vector3d(0, 0, -20), 8, 40), () => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -20), 6, 40), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitX, 90)), BooleanOp.BooleanKind.Unite),
            };
            var rot = TransformOp.Rotate(Vector3d.Zero, new Vector3d(0.8, 0.5, 0.33).Normalized(), 23);
            foreach (var (name, A, B, K) in cases)
            {
                string? fev0 = null; double v0 = 0; string row = name + ":";
                foreach (double d in new[] { 0.0, 1e4, 1e5, 3e5 })
                {
                    var off = new Vector3d(d, -0.8 * d, 0.35 * d);
                    TransformOp.Xform X(TransformOp.Xform r) => new(p => r.Point(p) + off, r.Direction, false);
                    var a = TransformOp.Copy(A(), X(rot)); var b = TransformOp.Copy(B(), X(rot));
                    var r = BooleanOp.Evaluate(new[] { a }, b, K, OperationPolicy.AllowApproximate);
                    if (!r.Accepted) { fails.Add($"{name} d={d}: refused {r.FailureCode}"); row += $" d{d:G2} refused"; continue; }
                    var s = r.Bodies; string fev = $"{s.Count}:{s.Sum(x => x.Faces.Count())}/{s.Sum(x => x.Edges.Count())}/{s.Sum(x => x.Vertices.Count())}";
                    double v = s.Sum(OpVolume);
                    row += $" d{d:G2} {r.Guarantee} {fev} {v:G12}";
                    if (r.Guarantee is not (GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric)) fails.Add($"{name} d={d}: {r.Guarantee}");
                    if (fev0 == null) { fev0 = fev; v0 = v; continue; }
                    if (fev != fev0) fails.Add($"{name} d={d}: topology {fev} vs {fev0}");
                    if (!(Math.Abs(v - v0) <= 1e-9 * v0)) fails.Add($"{name} d={d}: volume rel {(v - v0) / v0:G3}");
                    // B-rep — годен на любом сдвиге; сетка показа (float) — до 1e5 мм (дальше шаг float 0.03 мм сравним с хордой
                    // показа — известное ограничение показа, не геометрии)
                    foreach (var body in s)
                    {
                        var val = SolidValidator.Validate(body);
                        var brepErr = val.Issues.Where(i => i.Severity == ValidationSeverity.Error && i.Where == Representation.BRep).ToList();
                        if (brepErr.Count > 0) fails.Add($"{name} d={d}: B-rep {string.Join(" | ", brepErr.Take(3))}");
                        if (d <= 1e5 && !val.IsValid) fails.Add($"{name} d={d}: {val.Summary()} {string.Join(" | ", val.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3))}");
                        if (d > 1e5 && !val.IsValid) row += " (display mesh: " + val.Summary() + ")";
                    }
                }
                rows.Add(row);
            }
            foreach (var r in rows) Console.WriteLine("  " + r);
            Log(new { test = "KRN02-FAR-GENERAL", fails = fails.Count });
            Require(fails.Count == 0, string.Join("; ", fails.Take(10)));
        });
    }
}
