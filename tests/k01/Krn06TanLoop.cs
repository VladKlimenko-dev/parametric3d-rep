using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn06TanLoopChecks()
    {
        Check("KRN06-DIRECT-TANGENT-LOOP", () =>
        {
            // 06.10/06.11: касательный контур из отрезков и четвертей дуг (верх бруска со скруглёнными вертикальными рёбрами,
            // основание такой бобышки на плите) — прямой построитель: цилиндры над отрезками, торы над дугами, стыки —
            // четверти окружностей. Объём — по Паппу: площадь сечения (1 − π/4)r² (фаска — r²/2) на длину пути её центра
            // тяжести (дуги — радиус R ∓ c·r, c = (10 − 3π)/(12 − 3π); у фаски c = 1/3). Тело ExactAnalytic, без резцов.
            var fails = new List<string>();
            double k = 1 - Math.PI / 4, c = (10 - 3 * Math.PI) / (12 - 3 * Math.PI);
            TSolid RoundedBox(double x, double y, double z, double w, double d, double h, double R)
            {
                var bl = new List<TSolid> { Box(x, y, z, w, d, h) };
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), R, new List<string>());
                return bl[0];
            }
            var ax = new Vector3d(1, 0.3, 0.2).Normalized(); var mv = new Vector3d(120, -40, 15);
            var rot = Compose(TransformOp.Rotate(Vector3d.Zero, ax, 33), mv); var inv = TransformOp.Rotate(Vector3d.Zero, ax, -33);
            var cases = new (string Name, Func<TSolid> Make, double Zsel, double R, double r, bool Ch, double Sign, double W, double D, bool Rot)[]
            {
                ("top loop R5 r2", () => RoundedBox(0, 0, 0, 60, 40, 20, 5), 20, 5, 2, false, -1, 60, 40, false),
                ("top loop R8 r3", () => RoundedBox(0, 0, 0, 60, 40, 20, 8), 20, 8, 3, false, -1, 60, 40, false),
                ("top loop R5 C2", () => RoundedBox(0, 0, 0, 60, 40, 20, 5), 20, 5, 2, true, -1, 60, 40, false),
                ("bottom loop R5 r1.5", () => RoundedBox(0, 0, 0, 60, 40, 20, 5), 0, 5, 1.5, false, -1, 60, 40, false),
                ("rotated top loop R5 r2", () => { var b = RoundedBox(0, 0, 0, 60, 40, 20, 5); return TopologyTransform.Apply(b, rot)!; }, 20, 5, 2, false, -1, 60, 40, true),
                ("boss base R5 r2 (concave)", () => { var bl = new List<TSolid> { Box(-10, -10, -10, 60, 50, 10) }; BooleanOp.Unite(bl, RoundedBox(0, 0, 0, 40, 30, 12, 5), new List<string>()); return bl[0]; }, 0, 5, 2, false, +1, 40, 30, false),
            };
            foreach (var (name, make, zs, R, r, chf, sg, W, D, isRot) in cases)
            {
                var b0 = make(); var bl = new List<TSolid> { b0 };
                double v0 = AnalyticVolume.Of(b0) ?? double.NaN;
                Vector3d Back(Vector3d p) => isRot ? inv.Point(p - mv) : p;
                // выбранный контур: рёбра на высоте zs, у бобышки — только её основание (внутри её габарита)
                var sel = b0.Edges.Where(e => Math.Abs(Back(e.V0.P).Z - zs) < 1e-7 && Math.Abs(Back(e.V1.P).Z - zs) < 1e-7
                                              && (sg < 0 || (Back(e.V0.P).X > -1e-7 && Back(e.V0.P).X < W + 1e-7 && Back(e.V0.P).Y > -1e-7 && Back(e.V0.P).Y < D + 1e-7))
                                              && e.Curve.PointAt(0.5) is var m && Math.Abs(Back(m).Z - zs) < 1e-7).Select(EdgeRef.Of).ToList();
                if (sel.Count != 8) { fails.Add($"{name}: {sel.Count} edges selected"); continue; }
                var w = new List<string>();
                if (chf) FilletOp.ChamferEdges(bl, sel, r, r, w); else FilletOp.RoundEdges(bl, sel, r, w);
                var b = bl[0];
                double area = chf ? r * r / 2 : k * r * r, cc = chf ? 1.0 / 3 : c;
                // выпуклое (sg = −1): снято area·(путь, дуги R − c r); вогнутое (+1): добавлено area·(путь, дуги R + c r)
                double want = v0 + sg * area * (2 * (W - 2 * R) + 2 * (D - 2 * R) + 2 * Math.PI * (R + sg * cc * r));
                double? v = AnalyticVolume.Of(b);
                bool direct = w.Any(x => x.Contains("direct"));
                if (b.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic || !direct || v == null || Math.Abs(v.Value - want) > 1e-7 * want || !SolidValidator.Validate(b).IsValid)
                    fails.Add($"{name}: V {v:R} want {want:R} {b.Guarantee} direct {direct} {SolidValidator.Validate(b).Summary()} | {string.Join(" | ", w.Take(2))}");
                int tori = b.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.RevolvedSurface);
                Log(new { test = "KRN06-TANGENT-LOOP", name, volume = v, want, guarantee = b.Guarantee.ToString(), direct, faces = b.Faces.Count(), revolved = tori });
            }
            // радиус, равный радиусу дуги контура (тор вырождается), — честный отказ прямого построителя и прежний путь
            {
                var b0 = RoundedBox(0, 0, 0, 60, 40, 20, 5); var bl = new List<TSolid> { b0 };
                var sel = b0.Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7).Select(EdgeRef.Of).ToList();
                var w = new List<string>(); FilletOp.RoundEdges(bl, sel, 5, w);
                if (w.Any(x => x.Contains("direct blend"))) fails.Add("r = R: direct builder accepted a degenerate torus");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
