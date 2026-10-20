using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// МАСШТАБ ПО ОСЯМ (13:09, гизмо FreeForm): брусок, профиль с дугой (стенка выдавливания — дуга → дуга эллипса),
    /// цилиндр вдоль оси масштаба с равным поперечным коэффициентом — точно (объём = исходный × sx·sy·sz до 1e-9, тело
    /// годное, гарантия не ниже исходной); цилиндр поперёк неравного масштаба — стенка с дугой эллипса; шар при неравном масштабе — null (вызывающий переносит по
    /// треугольникам).
    /// В повёрнутой рамке — то же.
    /// </summary>
    static void ScaleAxesChecks()
    {
        Check("KRN-FF-SCALE-AXES", () =>
        {
            var fails = new List<string>();
            var rot = new Vector3d(1, 1, 0).Normalized(); var rotY = Vector3d.Cross(Vector3d.UnitZ, rot);
            // профиль: прямоугольник со скруглённым углом (дуга)
            TSolid Rounded()
            {
                var f = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
                var c = new List<ICurve3>
                {
                    new LineCurve3(f.ToWorld(0, 0), f.ToWorld(30, 0)), new LineCurve3(f.ToWorld(30, 0), f.ToWorld(30, 15)),
                    new ArcCurve3(new Frame3(f.ToWorld(25, 15), Vector3d.UnitX, Vector3d.UnitY), 5, 0, Math.PI / 2),
                    new LineCurve3(f.ToWorld(25, 20), f.ToWorld(0, 20)), new LineCurve3(f.ToWorld(0, 20), f.ToWorld(0, 0)),
                };
                return ExtrudeOp.Build(ProfileBuilder.Build(f, c), new Vector3d(0, 0, 12));
            }
            var cases = new (string Name, TSolid Body, Vector3d U, Vector3d V, double Sx, double Sy, double Sz, bool Exact)[]
            {
                ("box", Box(0, 0, 0, 10, 20, 30), Vector3d.UnitX, Vector3d.UnitY, 2, 1, 0.5, true),
                ("box rotated frame", Box(0, 0, 0, 10, 20, 30), rot, rotY, 2, 1, 0.5, true),
                ("rounded profile", Rounded(), Vector3d.UnitX, Vector3d.UnitY, 1.7, 0.6, 2.2, true),
                ("cylinder along Z", PrimitiveOp.Cylinder(new Vector3d(5, 5, 0), 4, 10), Vector3d.UnitX, Vector3d.UnitY, 1.5, 1.5, 2, true),
                ("cylinder across (elliptic wall)", PrimitiveOp.Cylinder(new Vector3d(5, 5, 0), 4, 10), Vector3d.UnitX, Vector3d.UnitY, 2, 1, 1, true),
                ("sphere non-uniform", PrimitiveOp.Sphere(new Vector3d(0, 0, 0), 5), Vector3d.UnitX, Vector3d.UnitY, 2, 1, 1, false),
            };
            foreach (var (name, b, u, v, sx, sy, sz, exact) in cases)
            {
                double v0 = OpVolume(b);
                var w = Vector3d.Cross(u, v);
                var r = TopologyTransform.ScaledAxes(b, new Vector3d(1, 2, 3), u, v, w, sx, sy, sz);
                if (!exact) { if (r != null) fails.Add($"{name}: expected null (not exactly representable)"); continue; }
                if (r == null) { fails.Add($"{name}: not scaled exactly"); continue; }
                var val = SolidValidator.Validate(r);
                double v1a = AnalyticVolume.Of(r) ?? double.NaN, want = v0 * sx * sy * sz;
                double v1 = double.IsNaN(v1a) ? val.Metrics.GetValueOrDefault("mesh.volume", double.NaN) : v1a;
                double tol = double.IsNaN(v1a) ? 5e-3 : 1e-9;   // нет формулы объёма (стенка с дугой эллипса) — по сетке
                if (!r.Edges.Any(e => e.Curve is EllipseCurve3) && name.Contains("elliptic")) fails.Add($"{name}: no ellipse edges");
                Log(new { test = "KRN-FF-SCALE-AXES", name, v0 = v0.ToString("R"), v1 = v1.ToString("R"), want = want.ToString("R"), guarantee = r.Guarantee.ToString(), valid = val.Summary() });
                if (!val.IsValid) fails.Add($"{name}: {val.Summary()}");
                if (!(Math.Abs(v1 - want) <= tol * want)) fails.Add($"{name}: volume {v1:R} vs {want:R}");
                if (r.Guarantee < b.Guarantee) fails.Add($"{name}: guarantee {r.Guarantee} < {b.Guarantee}");
            }
            Require(fails.Count == 0, string.Join(" | ", fails));
        });
    }
}
