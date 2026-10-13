using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn06LineCylChamferChecks()
    {
        Check("KRN06-DIRECT-CHAMFER-LINE-CYLINDER", () =>
        {
            // 06.15: фаска прямого ребра плоскость/цилиндр вдоль оси прямым построителем — на плоскости d поперёк ребра,
            // на цилиндре — точка на хорде d (соглашение BRepFilletAPI_MakeChamfer); грань фаски — плоскость. Выпуклое ребро
            // полудиска R15 × 20 (одно ребро C3, оба C2) и вогнутое ребро вала R8 на плите (C2, C3). Объём — эталон OCCT
            // (те же тела) с отн. погрешностью ≤ 1e-9; ExactAnalytic, годное, плоская грань фаски, счётчик LineCylChamfers.
            var fails = new List<string>();
            var prof = new List<Parametric3D.Kernel.Curves.ICurve3>
            {
                new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(-15, 0, 0), new Vector3d(15, 0, 0)),
                new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 15, 0, Math.PI),
            };
            TSolid D() => ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), prof), new Vector3d(0, 0, 20));
            TSolid Shaft()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                BooleanOp.Unite(bl, TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 20, 20), 8, 60), TransformOp.Rotate(new Vector3d(0, 20, 20), Vector3d.UnitY, 90)), new List<string>());
                return bl[0];
            }
            TEdge ShaftEdge(TSolid b) => b.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 20) < 1e-6 && Math.Abs(x.V1.P.Z - 20) < 1e-6 && Math.Abs(x.V0.P.Y - 28) < 1e-6 && Math.Abs(x.V1.P.Y - 28) < 1e-6);
            var cases = new (string Name, Func<TSolid> Make, Func<TSolid, List<TEdge>> Sel, double D, double Occt)[]
            {
                ("D-profile one edge C3", D, b => new() { EdgeAt(b, new(15, 0, 0), new(15, 0, 20)) }, 3, 6976.0255527084),
                ("D-profile both edges C2", D, b => new() { EdgeAt(b, new(15, 0, 0), new(15, 0, 20)), EdgeAt(b, new(-15, 0, 0), new(-15, 0, 20)) }, 2, 6986.98129251766),
                ("shaft on plate concave C2", Shaft, b => new() { ShaftEdge(b) }, 2, 54145.89306819657),
                ("shaft on plate concave C3", Shaft, b => new() { ShaftEdge(b) }, 3, 54280.01292879427),
            };
            foreach (var (name, make, sel, d, occt) in cases)
            {
                var bl = new List<TSolid> { make() }; var w = new List<string>();
                long c0 = DirectRound.LineCylChamfers;
                FilletOp.ChamferEdges(bl, sel(bl[0]).Select(EdgeRef.Of).ToList(), d, d, w);
                var r = bl[0]; double v = AnalyticVolume.Of(r) ?? double.NaN;
                int planes = r.Faces.Count(f => f.Tag == "chamfer:face" && GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.PlaneSurface);
                Log(new { test = "KRN06-LINECYL-CHAMFER", name, volume = v, occt, guarantee = r.Guarantee.ToString(), planes, n = DirectRound.LineCylChamfers - c0 });
                if (r.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {r.Guarantee}");
                if (!(Math.Abs(v - occt) <= 1e-9 * occt)) fails.Add($"{name}: V {v:R} vs OCCT {occt:R}");
                if (!SolidValidator.Validate(r).IsValid) fails.Add($"{name}: invalid");
                if (DirectRound.LineCylChamfers == c0) fails.Add($"{name}: not the direct builder ({string.Join(" | ", w.Take(2))})");
            }
            {
                // несимметричная фаска 2×4 на ребре полудиска: грань первого размера — по правилу резцов (как у прежнего пути:
                // его объём ближе к одному из двух эталонов OCCT — «2 на плоскости» / «2 на цилиндре»); прямой — тот же до 1e-9
                double occtPl = 6982.148368139338, occtCy = 6987.8713694217595;
                (TSolid B, List<string> W) Run(bool direct)
                {
                    var bl = new List<TSolid> { D() }; var w = new List<string>();
                    DirectRound.OffForTest = !direct;
                    try { FilletOp.ChamferEdges(bl, new List<EdgeRef> { EdgeRef.Of(EdgeAt(bl[0], new(15, 0, 0), new(15, 0, 20))) }, 2, 4, w); }
                    finally { DirectRound.OffForTest = false; }
                    return (bl[0], w);
                }
                long c0 = DirectRound.LineCylChamfers;
                var (dB, _) = Run(true); var (oB, _) = Run(false);
                double dv = AnalyticVolume.Of(dB) ?? double.NaN, ov = AnalyticVolume.Of(oB) ?? double.NaN;
                double want = Math.Abs(ov - occtPl) < Math.Abs(ov - occtCy) ? occtPl : occtCy;
                Log(new { test = "KRN06-LINECYL-CHAMFER", name = "D-profile unequal 2x4", volume = dv, old = ov, occt = want });
                if (!(Math.Abs(ov - want) < 0.5)) fails.Add($"unequal: the cutter path {ov:R} is near neither OCCT variant");
                if (!(Math.Abs(dv - want) <= 1e-9 * want)) fails.Add($"unequal: V {dv:R} vs OCCT {want:R}");
                if (dB.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(dB).IsValid || DirectRound.LineCylChamfers == c0) fails.Add($"unequal: {dB.Guarantee}, direct {DirectRound.LineCylChamfers - c0}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
