using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// k148 (замечание пользователя 01.11, 09:14, модель HelSweep): ВИНТОВАЯ ПРОТЯЖКА — ВИНТОВОЕ ДВИЖЕНИЕ СЕЧЕНИЯ. Трапеция в
    /// плоскости XZ (через ось Z) на радиусе 30…36, шаг 20, 2.5 витка. Норма: тело точное и годное; торцы — в плоскостях
    /// сечения (через ось: нормаль торца поперёк оси и поперёк радиуса); объём — по Паппу (осевой сдвиг лежит в плоскости
    /// сечения и объёма не прибавляет): площадь × 2π·r центра тяжести × витки. Левая рука и обратное направление — тот же
    /// объём, торец конца — по ту сторону. Мутация P3D_HELIXSCREW=0 (протяжка по ломаной рамкой без вращения) роняет проверку.
    /// </summary>
    static void Krn07HelixScrewChecks()
    {
        Check("KRN07-HELIX-SCREW", () =>
        {
            var fails = new List<string>();
            if (Environment.GetEnvironmentVariable("HXDBG") == "1") DirectRound.Debug = m => Console.WriteLine("  dr: " + m);
            var pl = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);       // плоскость XZ: U = X, V = Z
            Vector3d P(double x, double z) => new(x, 0, z);
            // трапеция: основание у оси 8, верх 4, высота 6 (радиус 30…36)
            var pts = new[] { P(30, -4), P(36, -2), P(36, 2), P(30, 4) };
            var loop = Enumerable.Range(0, 4).Select(i => (ICurve3)new LineCurve3(pts[i], pts[(i + 1) % 4])).ToArray();
            double area = (8 + 4) / 2.0 * 6, rc = 30 + 6.0 * (8 + 2 * 4) / (3.0 * (8 + 4));   // центр тяжести трапеции от большего основания
            foreach (var (name, left, flip) in new[] { ("right", false, false), ("left", true, false), ("flip", false, true) })
            {
                var sec = ProfileBuilder.Build(pl, loop);
                var a = flip ? -Vector3d.UnitZ : Vector3d.UnitZ;
                TSolid b;
                try { b = SweepOp.BuildHelical(sec, Vector3d.Zero, a, 20, 2.5, left, 72, p => SweepOp.Build(sec, new PolylineCurve3(p))); }
                catch (Exception ex) { fails.Add($"{name}: {ex.Message}"); continue; }
                var vr = SolidValidator.Validate(b);
                double v = AnalyticVolume.Of(b) ?? double.NaN, want = area * 2 * Math.PI * rc * 2.5;
                Log(new { test = "KRN07-HELIX-SCREW", name, guarantee = b.Guarantee.ToString(), v = v.ToString("R"), want, faces = b.Faces.Count(), valid = vr.Summary() });
                if (!vr.IsValid) fails.Add($"{name}: invalid {vr.Summary()}");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {b.Guarantee}");
                if (!(Math.Abs(v - want) <= 1e-6 * want)) fails.Add($"{name}: volume {v:0.######} vs Pappus {want:0.######}");
                // торцы: плоскость через ось (нормаль ⊥ оси и ⊥ радиусу центра торца); начало — плоскость XZ
                foreach (var f in b.Faces.Where(f => f.Tag is "cap:start" or "cap:end"))
                {
                    var sf = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface;
                    if (sf is not Parametric3D.Kernel.Surfaces.PlaneSurface ps) { fails.Add($"{name}: {f.Tag} is not planar"); continue; }
                    var n = ps.F.W; var o = ps.F.Origin; var rad = new Vector3d(o.X, o.Y, 0);
                    if (Math.Abs(n.Z) > 1e-9 || Math.Abs(Vector3d.Dot(n, rad.Normalized())) > 1e-9) fails.Add($"{name}: {f.Tag} plane does not contain the axis (n {n:0.####}, at {o:0.###})");
                    if (f.Tag == "cap:start" && Math.Abs(Math.Abs(n.Y) - 1) > 1e-9) fails.Add($"{name}: start cap is not the sketch plane (n {n:0.####})");
                }
                // ход вдоль оси: конец выше (ниже у обратного) на шаг × витки
                double zEnd = b.Faces.Where(f => f.Tag == "cap:end").SelectMany(f => f.Edges).SelectMany(e => new[] { e.V0.P.Z, e.V1.P.Z }).DefaultIfEmpty(double.NaN).Average();
                if (!(Math.Abs(zEnd - (flip ? -50 : 50)) < 1e-6)) fails.Add($"{name}: end cap at z {zEnd:0.###}, expected {(flip ? -50 : 50)}");
            }
            DirectRound.Debug = null;
            Require(fails.Count == 0, string.Join(" | ", fails.Take(6)));
        });
    }
}
