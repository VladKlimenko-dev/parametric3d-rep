using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

/// <summary>
/// Ворота g.04: ТОЧНОСТЬ ЗАДАНА И ИЗМЕРЯЕТСЯ. (1) Многогранный результат булевой несёт измеренное отклонение фасет от
/// источников граней, и оно не больше обещанного гранением. (2) Объём детали несёт границу ошибки (допуски сущностей ×
/// площадь + измеренная ошибка квадратуры + округление), и она покрывает отклонение от формулы на каталоге g.14.
/// </summary>
static partial class Program
{
    static void G04Checks()
    {
        Check("G04-FACETING-MEASURED-WITHIN-PROMISE", () =>
        {
            var fails = new List<string>(); int n = 0;
            var tools = new (string, Func<TSolid>)[] {
                // тор с 2026-10-11 — точный маршрут; многогранный путь аналитического операнда — призма над сплайнами
                ("spline prism top", () => SplinePrism(new Vector3d(30, 20, 20), 8, 5, 8)),
                ("spline prism rotated", () => SplinePrism(new Vector3d(30, 20, 10), 9, 4, 10, 33, new Vector3d(1, 2, 0.5))),
                ("spline prism small", () => SplinePrism(new Vector3d(5, 5, 20), 3, 1.5, 4)),
            };
            foreach (var (name, t) in tools)
            {
                var r = BooleanOp.Evaluate(new List<TSolid> { Box() }, t(), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                if (r.Outcome != OperationOutcome.Success) { fails.Add($"{name}: {r.Describe()}"); continue; }
                if (r.Guarantee is GuaranteeLevel.ExactAnalytic) continue;
                n++;
                Log(new { test = "G04-FACETING", name, acc = r.Accuracy?.ToString() });
                if (r.Accuracy is not { Measured: { } m } a) { fails.Add($"{name}: no measured faceting deviation"); continue; }
                if (!(m <= a.Budget)) fails.Add($"{name}: measured {m:G4} > promised {a.Budget:G4}");
                if (m <= 0) fails.Add($"{name}: measured deviation {m} (not measured?)");
            }
            Require(fails.Count == 0 && n >= 2, $"{n} polyhedral results: " + string.Join("; ", fails));
        });
        Check("G04-PART-GEOMETRY-INDEPENDENT-OF-DISPLAY", () =>
        {
            // хорда и угол отображения — не геометрия: деталь (в т.ч. многогранные операции) одна и та же
            var fails = new List<string>(); int n = 0;
            foreach (var (name, build, _) in G14Catalogue().Take(30))
            {
                string Geo(double ch, double angDeg)
                {
                    using var _c = new KernelContext(KernelSettings.Capture() with { Chord = ch, ChordAngle = angDeg * Math.PI / 180 }).Enter();
                    var p = build(); p.Apply(p.Compute(null));
                    return string.Join("|", p.Bodies.Select(b => $"{b.Faces.Count()}:{b.Guarantee}:{(b.Exact != null && b.ExactAuthoritative ? b.Exact.Solid.VolumeMm3().ToDouble() : AnalyticVolume.Of(b) ?? double.NaN):R}"));
                }
                string a = Geo(0.02, 7.5), b2 = Geo(0.004, 3);
                n++;
                if (a != b2) fails.Add($"{name}: {a} vs {b2}");
            }
            Require(fails.Count == 0, $"{fails.Count} of {n}: " + string.Join("; ", fails.Take(5)));
        });
        Check("G04-VOLUME-BOUND-COVERS-FORMULA", () =>
        {
            var fails = new List<string>(); int n = 0;
            foreach (var (name, build, expected) in G14Catalogue())
            {
                if (double.IsNaN(expected)) continue;
                var p = build(); var r = p.Compute(null); p.Apply(r);
                if (p.Features.Any(f => f.Error != null)) continue;
                double v = 0, bound = 0;
                foreach (var b in p.Bodies) { var m = AnalyticMeasure.Volume(b); v += m.Value; bound += m.Error; }
                n++;
                Log(new { test = "G04-BOUND", name, v, expected, bound = double.IsFinite(bound) ? bound.ToString("G4") : "unknown (mesh of non-analytic faces)", err = Math.Abs(v - expected) });
                if (!double.IsFinite(bound)) { if (p.Bodies.All(b => b.Guarantee == GuaranteeLevel.ExactAnalytic)) fails.Add($"{name}: exact body without a finite bound"); continue; }
                if (!(Math.Abs(p.Volume - expected) <= p.VolumeError)) fails.Add($"{name}: part volume {p.Volume:R} ± {p.VolumeError:G3} does not cover {expected:R}");
                if (!(Math.Abs(v - expected) <= bound)) fails.Add($"{name}: |{v:R} − {expected:R}| = {Math.Abs(v - expected):G3} > bound {bound:G3}");
            }
            Require(fails.Count == 0 && n >= 20, $"{n} parts: " + string.Join("; ", fails.Take(6)));
        });
        Check("G04-INTEGRATION-BUDGET-ENFORCED", () =>
        {
            // бюджет интегрирования (02.2) действует: по умолчанию каталог в нём; бюджет 1e-14 — каждая аналитическая деталь
            // сообщает превышение (иначе предупреждение не проверяет ничего)
            int n = 0, quietDefault = 0, warnTight = 0; var fails = new List<string>();
            static bool Warned(Part p, Part.BuildResult r) => r.Warnings.Any(w => w.Contains("integration budget"));
            foreach (var (name, build, expected) in G14Catalogue().Take(25))
            {
                var p = build(); var r = p.Compute(null); p.Apply(r);
                if (p.Features.Any(f => f.Error != null) || p.Bodies.Any(b => b.Guarantee != GuaranteeLevel.ExactAnalytic)) continue;
                n++;
                if (!Warned(p, r)) quietDefault++; else fails.Add($"{name}: default budget warns: {string.Join(" | ", r.Warnings)}");
                using (new KernelContext(KernelSettings.Capture() with { Tolerance = ToleranceContext.Default with { IntegrationRelative = 1e-14 } }).Enter())
                {
                    var p2 = build(); var r2 = p2.Compute(null);
                    if (Warned(p2, r2)) warnTight++; else fails.Add($"{name}: budget 1e-14 not reported (±{r2.VolumeError:G3})");
                }
            }
            Require(fails.Count == 0 && n >= 10, $"{n} exact parts, quiet {quietDefault}, tight-warned {warnTight}: " + string.Join("; ", fails.Take(5)));
        });
    }
}
