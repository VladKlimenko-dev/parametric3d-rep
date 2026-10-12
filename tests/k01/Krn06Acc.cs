using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

/// <summary>KRN-06 (06.16): приёмка поддержанных сопряжений на уровне детали.</summary>
static partial class Program
{
    static void Krn06AcceptanceChecks()
    {
        Check("KRN06-ACCEPTANCE-NO-FACETING-TANGENCY-REBUILD", () =>
        {
            // поддержанные семейства (плоскость/плоскость с углами, кромка отверстия) в дереве: скругление всех рёбер куба
            // (параметр R), отверстие, скругление кромок отверстия селектором; для R = 5 → 3 → 7: без ошибок, без гранения,
            // объём по формуле, касание полос со смежными гранями (G1), перестроение = сборке с нуля
            var fails = new List<string>();
            double k = 1 - Math.PI / 4, g = (10 - 3 * Math.PI) / (3 * (4 - Math.PI)), rr = 2, a = 60;
            double Want(double R) { double b = a - 2 * R; return b * b * b + 6 * b * b * R + 3 * b * Math.PI * R * R + 4.0 / 3 * Math.PI * R * R * R
                                                               - Math.PI * 100 * a - 2 * k * rr * rr * 2 * Math.PI * (10 + g * rr); }
            (Part P, ParameterTable T) Make(double R)
            {
                var t = new ParameterTable(); t.Add("R", R);
                var p = new Part("acc"); var ex = TestGeometry.AddCube(p, a);
                var r1 = new RoundFeature("R1") { Radius = new ValueRef(R) { Formula = "R" } }; r1.Selectors.Add(new EdgeSelector(EdgeSelectorKind.Inner, ex.Key)); p.AddFeature(r1);
                var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(20) }; h.Points.Add(new Vector3d(0, 0, a / 2)); p.AddFeature(h);
                p.Rebuild(t);
                var r2 = new RoundFeature("R2") { Radius = new ValueRef(rr) }; r2.Selectors.Add(new EdgeSelector(EdgeSelectorKind.Rim, h.Key)); p.AddFeature(r2);
                p.Rebuild(t);
                return (p, t);
            }
            Vector3d N(TFace f, Vector3d at)
            {
                var s = GeometryAudit.AuthoritativeSurface(f)!; var n = s.NormalAt(s.ProjectToUV(at)).Normalized();
                return f.Reversed ? -n : n;
            }
            void Inspect(string name, Part p, double R)
            {
                var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Error));
                if (errs.Length > 0 || p.Integrity != null || p.Bodies.Count != 1) { fails.Add($"{name}: {errs} {p.Integrity}"); return; }
                var b = p.Bodies[0];
                var fac = GeometryAudit.Faceted(b);
                if (fac.Faces != 0 || fac.Edges != 0) fails.Add($"{name}: faceted {fac.Faces} faces, {fac.Edges} edges");
                double v = AnalyticVolume.Of(b) ?? p.Volume;
                if (Math.Abs(v - Want(R)) > 1e-9 * Want(R)) fails.Add($"{name}: V={v:R} vs {Want(R):R}");
                var roundKeys = p.Features.OfType<RoundFeature>().Select(f => "#" + f.Key + "/").ToList();
                bool IsBlend(TFace? f) => f != null && roundKeys.Any(x => f.Tag.StartsWith(x, StringComparison.Ordinal));
                double worst = 0; int edges = 0;
                foreach (var e in b.Edges)
                {
                    if (e.FaceA == null || e.FaceB == null || ReferenceEquals(e.FaceA, e.FaceB)) continue;
                    if (!IsBlend(e.FaceA) && !IsBlend(e.FaceB)) continue;
                    edges++;
                    for (int i = 1; i < 6; i++)
                    {
                        var at = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 6.0);
                        double ang = Math.Acos(Math.Clamp(Math.Abs(Vector3d.Dot(N(e.FaceA, at), N(e.FaceB, at))), 0, 1));   // касательные плоскости (ориентацию проверяет SolidValidator)
                        worst = Math.Max(worst, ang);
                    }
                }
                if (edges == 0 || !(worst < 1e-6)) fails.Add($"{name}: tangency across {edges} blend edges — worst angle {worst:G3} rad");
                Log(new { test = "KRN06-ACCEPTANCE", name, R, v, blendEdges = edges, worstAngle = worst });
            }
            {   // контроль чувствительности измерения касания: фаска кромки отверстия — излом 45°
                var c = new Part("ctl"); TestGeometry.AddCube(c, a);
                var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(20) }; h.Points.Add(new Vector3d(0, 0, a / 2)); c.AddFeature(h); c.Rebuild(null);
                var ch = new ChamferFeature("C1") { D1 = new ValueRef(2), D2 = new ValueRef(2) }; ch.Selectors.Add(new EdgeSelector(EdgeSelectorKind.Rim, h.Key)); c.AddFeature(ch); c.Rebuild(null);
                double worstC = 0;
                foreach (var e in c.Bodies[0].Edges)
                {
                    if (e.FaceA == null || e.FaceB == null || !(e.FaceA.Tag.StartsWith("#" + ch.Key + "/") || e.FaceB.Tag.StartsWith("#" + ch.Key + "/"))) continue;
                    var at = e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2);
                    worstC = Math.Max(worstC, Math.Acos(Math.Clamp(Math.Abs(Vector3d.Dot(N(e.FaceA, at), N(e.FaceB, at))), 0, 1)));
                }
                if (Math.Abs(worstC - Math.PI / 4) > 1e-6) fails.Add($"control: chamfer kink measured {worstC:G6} rad, expected π/4 ({ch.Error})");
            }
            var (pp, tt) = Make(5); Inspect("R=5", pp, 5);
            foreach (double R in new[] { 3.0, 7.0 })
            {
                tt.Find("R")!.Number = R; tt.Recalculate(); pp.ParametersChanged(); pp.Rebuild(tt);
                Inspect($"R={R} after the edit", pp, R);
                var (fresh, _) = Make(R);
                if (PartGeometry(fresh) != PartGeometry(pp)) fails.Add($"R={R}: the rebuild differs from a fresh build ({pp.Volume:R} vs {fresh.Volume:R})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
