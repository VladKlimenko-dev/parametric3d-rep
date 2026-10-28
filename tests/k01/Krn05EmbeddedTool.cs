using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k133 (11.3, найдено сверкой модели пользователя X-218795-05 с OpenCascade): вырез выдавливанием, профиль которого —
    /// прямоугольник со скруглённым углом, симметрично на ширину МЕНЬШЕ тела: торцы инструмента и цилиндрическая грань
    /// скругления целиком внутри тела, наружу выходит только верх. Строит деталь по шагам: тело (куб или тело вращения),
    /// вырез; ждём точное годное тело и объём по формуле.
    /// </summary>
    static (Part Part, ExtrudeFeature Cut) EmbeddedCut(bool revolved, double r = 8, bool turned = false)
    {
        var p = new Part("emb");
        if (!revolved) TestGeometry.AddCube(p, 60);
        else
        {
            // вал: цилиндр R30 вдоль Z высотой 60 (вращение прямоугольника в плоскости XZ вокруг Z)
            var s0 = p.AddSketch("S0", Frame3.XZ, p.PlaneXZ.Id);
            var a = new SkPoint(0, -30); var b = new SkPoint(30, -30); var c = new SkPoint(30, 30); var d = new SkPoint(0, 30);
            s0.AddEntity(new SkLine(a, b)); s0.AddEntity(new SkLine(b, c)); s0.AddEntity(new SkLine(c, d)); s0.AddEntity(new SkLine(d, a));
            var ax = s0.AddEntity(new SkLine(new SkPoint(0, -40), new SkPoint(0, 40)) { Role = SkRole.Construction });
            var rv = new RevolveFeature("Rev") { SketchId = s0.Id, AxisEntityId = ax.Id };
            p.AddFeature(rv);
        }
        // профиль в XZ: x ∈ [-10, 20], z ∈ [0, 40] (верх выше тела), левый нижний угол скруглён r
        // turned: плоскость эскиза как у пользователя — ось x эскиза = −Z, ось y = +X (нормаль −Y); точка (x, z) → (−z, x)
        var s = p.AddSketch("S1", turned ? new Frame3(Vector3d.Zero, -Vector3d.UnitZ, Vector3d.UnitX) : Frame3.XZ, p.PlaneXZ.Id);
        SkPoint Q(double x, double z) => turned ? new SkPoint(-z, x) : new SkPoint(x, z);
        var p0 = Q(-10 + r, 0); var p1 = Q(20, 0); var p2 = Q(20, 40); var p3 = Q(-10, 40); var p4 = Q(-10, r);
        s.AddEntity(new SkLine(p0, p1)); s.AddEntity(new SkLine(p1, p2)); s.AddEntity(new SkLine(p2, p3)); s.AddEntity(new SkLine(p3, p4));
        s.AddEntity(new SkArc(Q(-10 + r, r), p4, p0, ccw: true));
        var cut = new ExtrudeFeature("Cut") { SketchId = s.Id, Cut = true, Symmetric = true };
        cut.Side1.End = EndType.Distance; cut.Side1.Distance = new ValueRef(20);
        p.AddFeature(cut);
        return (p, cut);
    }

    static void Krn05EmbeddedToolChecks()
    {
        Check("KRN05-EMBEDDED-CURVED-TOOL-FACE", () =>
        {
            var fails = new List<string>();
            foreach (bool turned in new[] { false, true })
            foreach (bool rev in new[] { false, true })
                foreach (double r in new[] { 8.0, 15.0 })
                {
                    var (p, cut) = EmbeddedCut(rev, r, turned);
                    p.Rebuild(null);
                    if (cut.Error != null) { fails.Add($"{(rev ? "shaft" : "cube")}{(turned ? " turned" : "")} r{r}: {cut.Error}"); continue; }
                    var b = p.Bodies.Single();
                    var val = SolidValidator.Validate(b);
                    // вырез: объём профиля внутри тела (z от 0 до верха тела 30) × ширина 20
                    double prof = 30 * 30 - (r * r - Math.PI * r * r / 4);
                    double body = rev ? Math.PI * 900 * 60 : 216000;
                    double want = body - prof * 20;
                    Log(new { test = "KRN05-EMBEDDED-CURVED-TOOL-FACE", body = rev ? "shaft" : "cube", r, turned, guarantee = b.Guarantee.ToString(), valid = val.IsValid, volume = p.Volume, want, downgrades = p.Downgrades.Count });
                    if (!val.IsValid) fails.Add($"{(rev ? "shaft" : "cube")}{(turned ? " turned" : "")} r{r}: invalid {val.Summary()}");
                    if (b.Guarantee.ToString() != "ExactAnalytic") fails.Add($"{(rev ? "shaft" : "cube")}{(turned ? " turned" : "")} r{r}: {b.Guarantee}");
                    if (Math.Abs(p.Volume - want) > 1e-6 * want) fails.Add($"{(rev ? "shaft" : "cube")}{(turned ? " turned" : "")} r{r}: volume {p.Volume} vs {want}");
                }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    /// <summary>
    /// k133 (11.3, модель пользователя X-218795-05): ступенчатый вал (цилиндр R40 при z∈[0,30], конус до R30 при z=50, цилиндр
    /// R30 при z∈[50,80]) и вырез выдавливанием поперёк оси: профиль в плоскости XZ — x ∈ [bottom, 60], z ∈ [20, 50] со
    /// скруглённым углом r8 у (bottom, 20), симметрично ±10 по Y. Правый торец инструмента — В ПЛОСКОСТИ ОКРУЖНОСТИ СТЫКА
    /// конуса и цилиндра (z = 50); низ — касательно цилиндра R30 (bottom 30), ниже (28) или выше (32).
    /// </summary>
    static (Part Part, ExtrudeFeature Cut) JunctionCut(double bottom, double right = 50, double R1 = 40, double R2 = 30, double z1 = 30, double z2 = 50, double left = 20, double top = 60, double half = 10)
    {
        var p = new Part("junction");
        var s0 = p.AddSketch("S0", Frame3.XZ, p.PlaneXZ.Id);
        var pts = new[] { new SkPoint(0, 0), new SkPoint(R1, 0), new SkPoint(R1, z1), new SkPoint(R2, z2), new SkPoint(R2, z2 + 30), new SkPoint(0, z2 + 30) };
        for (int i = 0; i < pts.Length; i++) s0.AddEntity(new SkLine(pts[i], pts[(i + 1) % pts.Length]));
        var ax = s0.AddEntity(new SkLine(new SkPoint(0, -10), new SkPoint(0, 90)) { Role = SkRole.Construction });
        p.AddFeature(new RevolveFeature("Rev") { SketchId = s0.Id, AxisEntityId = ax.Id });
        double r = 8;
        var s = p.AddSketch("S1", Frame3.XZ, p.PlaneXZ.Id);
        var p0 = new SkPoint(bottom, left + r); var p1 = new SkPoint(bottom, right); var p2 = new SkPoint(top, right); var p3 = new SkPoint(top, left); var p4 = new SkPoint(bottom + r, left);
        s.AddEntity(new SkLine(p0, p1)); s.AddEntity(new SkLine(p1, p2)); s.AddEntity(new SkLine(p2, p3)); s.AddEntity(new SkLine(p3, p4));
        s.AddEntity(new SkArc(new SkPoint(bottom + r, left + r), p4, p0, ccw: false));
        var cut = new ExtrudeFeature("Cut") { SketchId = s.Id, Cut = true, Symmetric = true };
        cut.Side1.End = EndType.Distance; cut.Side1.Distance = new ValueRef(2 * half);
        p.AddFeature(cut);
        return (p, cut);
    }

    static void Krn05JunctionChecks()
    {
        Check("KRN05-TOOL-FACE-ON-JUNCTION-CIRCLE", () =>
        {
            var fails = new List<string>();
            // последние два — размеры детали пользователя (конус R168.072 → R148.336, стык при 762.4014083280179; низ инструмента
            // касается цилиндра стыка в углу инструмента — вершина гиперболы «плоскость ∩ конус» в углу)
            const double Zu = 762.4014083280179;
            foreach (var (bottom, right, R1, R2, z1, z2, left, top, half) in new[]
            {
                (32.0, 50.0, 40.0, 30.0, 30.0, 50.0, 20.0, 60.0, 10.0), (30.0, 50.0, 40.0, 30.0, 30.0, 50.0, 20.0, 60.0, 10.0), (28.0, 50.0, 40.0, 30.0, 30.0, 50.0, 20.0, 60.0, 10.0),
                (30.0, 48.0, 40.0, 30.0, 30.0, 50.0, 20.0, 60.0, 10.0), (28.0, 48.0, 40.0, 30.0, 30.0, 50.0, 20.0, 60.0, 10.0),
                (148.336, Zu, 168.072, 148.336, 728.218, Zu, 700.0, 190.0, 25.4), (148.336, Zu, 168.072, 148.336, 728.218, Zu, 667.6887579470026, 190.0, 25.4),
            })
            {
                var (p, cut) = JunctionCut(bottom, right, R1, R2, z1, z2, left, top, half);
                if (Environment.GetEnvironmentVariable("JDBG") == $"{bottom}/{right}") { Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = (m, q) => Console.Error.WriteLine($"PD {m} {q}"); Parametric3D.Kernel.Analytic.FaceIntersect.Debug = m => Console.Error.WriteLine("FI " + m); }
                p.Rebuild(null);
                Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = null; Parametric3D.Kernel.Analytic.FaceIntersect.Debug = null;
                string tag = $"bottom {bottom} right {right} left {left}";
                if (cut.Error != null) { fails.Add($"{tag}: {cut.Error}"); continue; }
                var b = p.Bodies.Single(); var val = SolidValidator.Validate(b);
                Log(new { test = "KRN05-TOOL-FACE-ON-JUNCTION-CIRCLE", bottom, right, guarantee = b.Guarantee.ToString(), valid = val.IsValid, faces = b.Faces.Count(), volume = p.Volume, volErr = p.VolumeError });
                if (!val.IsValid) fails.Add($"{tag}: invalid {val.Summary()}");
                if (b.Guarantee.ToString() != "ExactAnalytic") fails.Add($"{tag}: {b.Guarantee}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
