using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>KRN-07: приёмка оболочки и уклона в объявленной области (плоские и цилиндрические грани) — объём по формуле.</summary>
static partial class Program
{
    static void Krn07()
    {
        Check("KRN07-REVOLVE-EXTRUDE-ACCEPTANCE", () =>
        {
            // 07.1: тела вращения и выдавливания по формулам (Папп, конус, шар, косое выдавливание), касание оси, отказы
            var fails = new List<string>();
            var XY = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
            var XZ = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);   // профиль в плоскости XZ, ось — Z
            Parametric3D.Kernel.Curves.ICurve3 L(double x0, double z0, double x1, double z1) => new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(x0, 0, z0), new Vector3d(x1, 0, z1));
            var axisZ = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
            void Check1(string name, Func<TSolid> make, double expected)
            {
                TSolid b; try { b = make(); } catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); return; }
                double v = OpVolume(b);
                if (double.IsNaN(v) && name.Contains("oblique"))
                {
                    // косой эллиптический цилиндр: аналитического объёма нет (известное ограничение) — объём по сетке, допуск по хорде
                    double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
                    Log(new { test = "KRN07-REVOLVE-EXTRUDE", name, meshVolume = vm, expected });
                    if (!(Math.Abs(vm - expected) <= 2e-3 * expected)) fails.Add($"{name}: mesh volume {vm:R} vs {expected:R}");
                    if (SolidValidator.Validate(b) is { IsValid: false } vr2) fails.Add($"{name}: invalid {vr2.Summary()}");
                    return;
                }
                // построитель примитива не ставит уровень (Unspecified): тело точное по построению — допуск как у точного
                double tol = b.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.Unspecified ? 1e-9 * expected : AnalyticMeasure.Volume(b).Error;
                Log(new { test = "KRN07-REVOLVE-EXTRUDE", name, volume = double.IsFinite(v) ? v : (double?)null, expected, bound = double.IsFinite(tol) ? tol : (double?)null, guarantee = b.Guarantee.ToString() });
                if (!(Math.Abs(v - expected) <= tol)) fails.Add($"{name}: volume {v:R} vs {expected:R} ({b.Guarantee}, bound {tol:G3})");
                if (SolidValidator.Validate(b) is { IsValid: false } vr) fails.Add($"{name}: invalid {vr.Summary()} {string.Join(" / ", vr.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2))}");
            }
            void Refused(string name, Func<TSolid> make)
            {
                try { make(); fails.Add($"{name}: accepted"); }
                catch (RebuildException ex) when (ex.Outcome == OperationOutcome.InvalidInput) { }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} instead of InvalidInput"); }
            }
            var ring = new List<Parametric3D.Kernel.Curves.ICurve3> { L(5, 0, 15, 0), L(15, 0, 15, 10), L(15, 10, 5, 10), L(5, 10, 5, 0) };
            Check1("revolve ring full", () => RevolveOp.Build(ProfileBuilder.Build(XZ, ring), axisZ, 0, 2 * Math.PI), Math.PI * (225 - 25) * 10);
            Check1("revolve ring 90°", () => RevolveOp.Build(ProfileBuilder.Build(XZ, ring), axisZ, 0, Math.PI / 2), Math.PI * (225 - 25) * 10 / 4);
            Check1("revolve ring −135°", () => RevolveOp.Build(ProfileBuilder.Build(XZ, ring), axisZ, 0, -3 * Math.PI / 4), Math.PI * (225 - 25) * 10 * 3 / 8);
            var tri = new List<Parametric3D.Kernel.Curves.ICurve3> { L(0, 0, 10, 0), L(10, 0, 0, 20), L(0, 20, 0, 0) };
            Check1("revolve triangle on the axis (cone)", () => RevolveOp.Build(ProfileBuilder.Build(XZ, tri), axisZ, 0, 2 * Math.PI), Math.PI * 100 * 20 / 3);
            var half = new List<Parametric3D.Kernel.Curves.ICurve3> { L(0, -8, 0, 8), new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX), 8, 0, Math.PI) };
            Check1("revolve half disk on the axis (sphere)", () => RevolveOp.Build(ProfileBuilder.Build(XZ, half), axisZ, 0, 2 * Math.PI), 4.0 / 3 * Math.PI * 512);
            var rectHole = new List<Parametric3D.Kernel.Curves.ICurve3>
            {
                new Parametric3D.Kernel.Curves.LineCurve3(new(0, 0, 0), new(40, 0, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(40, 0, 0), new(40, 30, 0)),
                new Parametric3D.Kernel.Curves.LineCurve3(new(40, 30, 0), new(0, 30, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(0, 30, 0), new(0, 0, 0)),
                Parametric3D.Kernel.Curves.ArcCurve3.FullCircle(new Frame3(new Vector3d(20, 15, 0), Vector3d.UnitX, Vector3d.UnitY), 6),
            };
            Check1("extrude plate with hole", () => ExtrudeOp.Build(ProfileBuilder.Build(XY, rectHole), new Vector3d(0, 0, 12)), (1200 - Math.PI * 36) * 12);
            Check1("extrude oblique", () => ExtrudeOp.Build(ProfileBuilder.Build(XY, rectHole), new Vector3d(5, -3, 12)), (1200 - Math.PI * 36) * 12);
            Check1("extrude oblique without hole", () => ExtrudeOp.Build(ProfileBuilder.Build(XY, rectHole.Take(4).ToList()), new Vector3d(5, -3, 12)), 1200 * 12);
            var bow = new List<Parametric3D.Kernel.Curves.ICurve3>
            {
                new Parametric3D.Kernel.Curves.LineCurve3(new(0, 0, 0), new(20, 20, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(20, 20, 0), new(20, 0, 0)),
                new Parametric3D.Kernel.Curves.LineCurve3(new(20, 0, 0), new(0, 20, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(0, 20, 0), new(0, 0, 0)),
            };
            Refused("extrude self-crossing profile", () => ExtrudeOp.Build(ProfileBuilder.Build(XY, bow), new Vector3d(0, 0, 5)));
            Refused("revolve self-crossing profile", () => RevolveOp.Build(ProfileBuilder.Build(XZ, new List<Parametric3D.Kernel.Curves.ICurve3> { L(5, 0, 15, 10), L(15, 10, 15, 0), L(15, 0, 5, 10), L(5, 10, 5, 0) }), axisZ, 0, 2 * Math.PI));
            Refused("revolve zero angle", () => RevolveOp.Build(ProfileBuilder.Build(XZ, ring), axisZ, 0, 0));
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN07-SHELL-ACCEPTANCE", () =>
        {
            var fails = new List<string>();
            void Case(string name, Func<TSolid> make, double t, Vector3d[]? open, double expected, (Vector3d, double)[]? ft = null)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>();
                bool ok; try { ok = ShellOp.Hollow(bl, t, open, w, ft); } catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); return; }
                if (!ok) { fails.Add($"{name}: refused ({string.Join(" | ", w.Take(2))})"); return; }
                var b = bl[0]; double v = OpVolume(b);
                double tol = b.Guarantee == GuaranteeLevel.ExactAnalytic ? 1e-9 * expected : AnalyticMeasure.Volume(b).Error;   // точный — до 1e-9, многогранный — в заявленной границе
                Log(new { test = "KRN07-SHELL", name, volume = v, expected, bound = tol, guarantee = b.Guarantee.ToString() });
                if (!(Math.Abs(v - expected) <= tol)) fails.Add($"{name}: volume {v:R} vs {expected:R} ({b.Guarantee})");
                if (!SolidValidator.Validate(b).IsValid) fails.Add($"{name}: invalid");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: {b.Guarantee}, not an exact body");
            }
            Case("box open top t2", () => Box(0, 0, 0, 60, 40, 20), 2, new[] { new Vector3d(30, 20, 20) }, 48000 - 56 * 36 * 18);
            Case("box closed t2", () => Box(0, 0, 0, 60, 40, 20), 2, null, 48000 - 56 * 36 * 16);
            // KRN-07 (07.5): стенка наружу (толщина < 0) и своя толщина грани — сдвигом плоскостей
            Case("box outward t2 closed", () => Box(0, 0, 0, 60, 40, 20), -2, null, 64 * 44 * 24 - 48000);
            Case("box outward t2 open top", () => Box(0, 0, 0, 60, 40, 20), -2, new[] { new Vector3d(30, 20, 20) }, 64 * 44 * 22 - 48000);
            Case("box open top t2, front wall t5", () => Box(0, 0, 0, 60, 40, 20), 2, new[] { new Vector3d(30, 20, 20) }, 48000 - 56 * 33 * 18, new[] { (new Vector3d(30, 0, 10), 5.0) });
            {   // в дереве детали: своя толщина грани — параметр операции, переживает сохранение и загрузку
                var p = new Parametric3D.Model.Part("shft"); var ex = Parametric3D.Model.TestGeometry.AddCube(p, 60);   // куб [-30, 30]³
                var sh = new Parametric3D.Model.Features.ShellFeature("S") { Thickness = new Parametric3D.Model.Features.ValueRef(2) };
                sh.OpenSelectors.Add(new Parametric3D.Model.Features.FaceSelector(Parametric3D.Model.Features.FaceSelectorKind.Role, ex.Key, "top"));
                sh.FaceThickness.Add((new Vector3d(30, 0, 0), new Parametric3D.Model.Features.ValueRef(4)));
                p.AddFeature(sh); p.Rebuild(null);
                double want = 216000 - 54.0 * 56 * 58;
                var (q, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(p)); q.Rebuild(null);
                Log(new { test = "KRN07-SHELL", name = "part: wall x=+30 t4", volume = p.Volume, reloaded = q.Volume, expected = want });
                if (sh.Error != null || Math.Abs(p.Volume - want) > 1e-9 * want) fails.Add($"part shell with a face thickness: {sh.Error} V {p.Volume:R} vs {want:R}");
                if (Math.Abs(q.Volume - want) > 1e-9 * want) fails.Add($"part shell with a face thickness after save/load: V {q.Volume:R} vs {want:R}");
            }
            // ВОГНУТОЕ тело (L-профиль: брусок 60×40×20 без [30,60]×[0,40]×[10,20]): полость — L, сдвинутый внутрь на t
            // (уступ — тоже), а не пересечение полупространств всех граней
            TSolid L() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, Box(30, -1, 10, 31, 42, 11), new List<string>()); return bl[0]; }
            Case("L-profile closed t2", L, 2, null, 36000 - (56 * 36 * 16 - 30 * 36 * 10));
            Case("L-profile open top of the tall part t2", L, 2, new[] { new Vector3d(15, 20, 20) }, 36000 - (26 * 36 * 18 + 30 * 36 * 6));
            {   // вогнутое тело с цилиндрической гранью: сдвиг плоскостей неприменим, пересечение полупространств неверно —
                // честный отказ (прежде — неверная полость, принятая как точная)
                var bl = new List<TSolid> { L() }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(15, 20, -2), 5, 24), new List<string>());
                var w = new List<string>(); bool ok = ShellOp.Hollow(bl, 2, null, w);
                if (ok) fails.Add($"concave L-profile with a hole: accepted (V {OpVolume(bl[0]):R}), an honest refusal expected");
            }
            Case("box open top and front t3", () => Box(0, 0, 0, 60, 40, 20), 3, new[] { new Vector3d(30, 20, 20), new Vector3d(30, 0, 10) }, 48000 - 54 * 37 * 17);
            {
                var rot = TransformOp.Rotate(new Vector3d(4, 1, -2), new Vector3d(0.3, -0.5, 0.8).Normalized(), 41);
                Case("rotated box open top t2", () => TransformOp.Copy(Box(0, 0, 0, 60, 40, 20), rot), 2, new[] { rot.Point(new Vector3d(30, 20, 20)) }, 48000 - 56 * 36 * 18);
            }
            {   // правильная шестиугольная призма (апофема 20, высота 15), открыт верх: внутренняя — апофема 18, высота 13
                double Hex(double ap) => 6 * ap * ap / Math.Sqrt(3) * 1.0;   // площадь: 2√3·ap²
                var prof = new List<Parametric3D.Kernel.Curves.ICurve3>(); double R = 20 / Math.Cos(Math.PI / 6);
                var pts = Enumerable.Range(0, 6).Select(i => new Vector3d(R * Math.Cos(i * Math.PI / 3), R * Math.Sin(i * Math.PI / 3), 0)).ToArray();
                for (int i = 0; i < 6; i++) prof.Add(new Parametric3D.Kernel.Curves.LineCurve3(pts[i], pts[(i + 1) % 6]));
                Case("hex prism open top t2", () => ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), prof), new Vector3d(0, 0, 15)), 2,
                    new[] { new Vector3d(0, 0, 15) }, Hex(20) * 15 - Hex(18) * 13);
            }
            {   // плита с отверстием R8, открыт верх: полость — брусок 56×36×18 минус цилиндр R10
                Case("plate with hole open top t2", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), new List<string>()); return bl[0]; },
                    2, new[] { new Vector3d(5, 5, 20) }, (48000 - Math.PI * 64 * 20) - (56 * 36 * 18 - Math.PI * 100 * 18));
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN07-DRAFT-ACCEPTANCE", () =>
        {
            var fails = new List<string>();
            void Case(string name, Func<TSolid> make, Vector3d[] faces, Vector3d np, Vector3d nn, double deg, double expected)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>();
                bool ok; try { ok = DraftOp.Apply(bl, faces, np, nn, deg, w); } catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); return; }
                if (!ok) { fails.Add($"{name}: refused ({string.Join(" | ", w.Take(2))})"); return; }
                var b = bl[0]; double v = OpVolume(b);
                double tol = b.Guarantee == GuaranteeLevel.ExactAnalytic ? 1e-9 * expected : AnalyticMeasure.Volume(b).Error;   // точный — до 1e-9, многогранный — в заявленной границе
                Log(new { test = "KRN07-DRAFT", name, volume = v, expected, bound = tol, guarantee = b.Guarantee.ToString() });
                if (!(Math.Abs(v - expected) <= tol)) fails.Add($"{name}: volume {v:R} vs {expected:R} ({b.Guarantee})");
                if (!SolidValidator.Validate(b).IsValid) fails.Add($"{name}: invalid");
            }
            double a5 = Math.Tan(5 * Math.PI / 180);
            // одна грань (x = 60) наклонена на 5° от нейтральной плоскости z = 0: снят клин ½·h²·tgα·d
            Case("box one face 5deg", () => Box(0, 0, 0, 60, 40, 20), new[] { new Vector3d(60, 20, 10) }, Vector3d.Zero, Vector3d.UnitZ, 5, 48000 - 0.5 * 400 * a5 * 40);
            // четыре боковые: призматоид V = h/6 (A0 + 4Am + A1), Am, A1 — сечения со сдвигом δ = z·tgα с каждой стороны
            {
                double d = 20 * a5, A0 = 60 * 40, A1 = (60 - 2 * d) * (40 - 2 * d), Am = (60 - d) * (40 - d);
                Case("box four sides 5deg", () => Box(0, 0, 0, 60, 40, 20), new[] { new Vector3d(60, 20, 10), new Vector3d(0, 20, 10), new Vector3d(30, 0, 10), new Vector3d(30, 40, 10) },
                    Vector3d.Zero, Vector3d.UnitZ, 5, 20.0 / 6 * (A0 + 4 * Am + A1));
            }
            // KRN-07 (07.6): уклон заменой плоскости — точное тело (ExactAnalytic), внутрь и наружу (угол < 0)
            {
                double d = 20 * a5;
                Case("box one face -5deg (outward)", () => Box(0, 0, 0, 60, 40, 20), new[] { new Vector3d(60, 20, 10) }, Vector3d.Zero, Vector3d.UnitZ, -5, 48000 + 0.5 * 400 * a5 * 40);
                double A1o = (60 + 2 * d) * (40 + 2 * d), Amo = (60 + d) * (40 + d);
                Case("box four sides -5deg (outward)", () => Box(0, 0, 0, 60, 40, 20), new[] { new Vector3d(60, 20, 10), new Vector3d(0, 20, 10), new Vector3d(30, 0, 10), new Vector3d(30, 40, 10) },
                    Vector3d.Zero, Vector3d.UnitZ, -5, 20.0 / 6 * (2400 + 4 * Amo + A1o));
                // нейтраль посередине (z = 10): выше нейтрали снят клин ½·10²·tgα·40, ниже добавлен такой же — объём прежний
                Case("box one face 5deg, neutral at mid-height", () => Box(0, 0, 0, 60, 40, 20), new[] { new Vector3d(60, 20, 10) }, new Vector3d(0, 0, 10), Vector3d.UnitZ, 5, 48000);
                {   // и форма: при z = 20 грань на x = 60 − 10·tgα, при z = 0 — на 60 + 10·tgα
                    var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                    if (DraftOp.Apply(bl, new[] { new Vector3d(60, 20, 10) }, new Vector3d(0, 0, 10), Vector3d.UnitZ, 5, new List<string>()))
                    {
                        double xt = bl[0].Vertices.Where(v => Math.Abs(v.P.Z - 20) < 1e-9).Max(v => v.P.X), xb = bl[0].Vertices.Where(v => Math.Abs(v.P.Z) < 1e-9).Max(v => v.P.X);
                        if (Math.Abs(xt - (60 - 10 * a5)) > 1e-9 || Math.Abs(xb - (60 + 10 * a5)) > 1e-9) fails.Add($"neutral at mid-height: x top {xt:R}, bottom {xb:R}");
                    }
                }
                // KRN-07 (07.6): цилиндрические грани — конус. Бобышка R8 h15 на бруске, нейтраль у основания: усечённый конус
                // r0 = 8, r1 = 8 − 15·tg 3°; сквозное отверстие R5, нейтраль внизу: стенка расходится вверх, r1 = 5 + 20·tg 3°
                double a3 = Math.Tan(3 * Math.PI / 180);
                double Frustum(double h, double r0, double r1) => Math.PI * h / 3 * (r0 * r0 + r0 * r1 + r1 * r1);
                Case("boss side 3deg (cone)", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Unite(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, 20), 8, 15), new List<string>()); return bl[0]; },
                    new[] { new Vector3d(38, 20, 27) }, new Vector3d(0, 0, 20), Vector3d.UnitZ, 3, 48000 + Frustum(15, 8, 8 - 15 * a3));
                Case("hole wall 3deg (cone)", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30), new List<string>()); return bl[0]; },
                    new[] { new Vector3d(35, 20, 10) }, Vector3d.Zero, Vector3d.UnitZ, 3, 48000 - Frustum(20, 5, 5 + 20 * a3));
                // L-образный профиль (вогнутое ребро): наклонена внешняя грань высокой части
                Case("L-profile outer face 5deg", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, Box(30, -1, 10, 31, 42, 11), new List<string>()); return bl[0]; },
                    new[] { new Vector3d(0, 20, 10) }, Vector3d.Zero, Vector3d.UnitZ, 5, 48000 - 30 * 40 * 10 - 0.5 * 400 * a5 * 40);
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
