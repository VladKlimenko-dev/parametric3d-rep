using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn06UnequalChecks()
    {
        Check("KRN06-DIRECT-CHAMFER-UNEQUAL", () =>
        {
            // 06.15: несимметричная фаска d1×d2 прямым построителем (плоские ленты), грань первого размера — по правилу резцов
            // (ход цепочки, «правая рука»). Брусок 60×40×20: одно ребро 2×5; два несмежных ребра 1.5×3; вогнутое ребро уступа
            // L-профиля 2×4. Объём — ½·d1·d2·L (точно) и тот же, что у пути резцов; площади граней по именам — те же, что у
            // пути резцов (размеры легли на те же грани); тело ExactAnalytic.
            var fails = new List<string>();
            TEdge Edge(TSolid b, double x, double z) => b.Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.X - x) < 1e-9 && Math.Abs(e.V1.P.X - x) < 1e-9 && Math.Abs(e.V0.P.Z - z) < 1e-9 && Math.Abs(e.V1.P.Z - z) < 1e-9);
            TSolid L() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, Box(30, -1, 10, 31, 42, 11), new List<string>()); return bl[0]; }
            var cases = new (string Name, Func<TSolid> Make, (double X, double Z)[] Edges, double D1, double D2, double Want)[]
            {
                ("one edge 2x5", () => Box(0, 0, 0, 60, 40, 20), new[] { (60.0, 20.0) }, 2, 5, 48000 - 0.5 * 2 * 5 * 40),
                ("two edges 1.5x3", () => Box(0, 0, 0, 60, 40, 20), new[] { (60.0, 20.0), (0.0, 0.0) }, 1.5, 3, 48000 - 2 * 0.5 * 1.5 * 3 * 40),
                ("concave step 2x4", L, new[] { (30.0, 10.0) }, 2, 4, 36000 + 0.5 * 2 * 4 * 40),
                // окружность плоскость/цилиндр (кромка отверстия R5): конус с разными размерами на плоскости и на стенке
                ("hole rim 1x2", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30), new List<string>()); return bl[0]; },
                    new[] { (-1.0, -1.0) }, 1, 2, -1),
                // цепочка (все 4 ребра верха) и угловые узлы (три ребра в вершине) — объём по пути резцов (сверка)
                ("top loop 2x3 (chain)", () => Box(0, 0, 0, 60, 40, 20), Array.Empty<(double, double)>(), 2, 3, double.NaN),
                ("corner: three edges 2x3", () => Box(0, 0, 0, 60, 40, 20), Array.Empty<(double, double)>(), 2, 3, double.NaN),
            };
            foreach (var (name, make, eds, d1, d2, want0) in cases)
            {
                double want = want0;
                (TSolid B, List<string> W) Run(bool direct)
                {
                    var bl = new List<TSolid> { make() }; var w = new List<string>();
                    var refs = eds.Where(xz => xz.X >= 0).Select(xz => EdgeRef.Of(Edge(bl[0], xz.X, xz.Z))).ToList();
                    if (name.StartsWith("hole rim")) refs = bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 5) < 1e-9 && Math.Abs(e.V0.P.Z - 20) < 1e-9).Select(EdgeRef.Of).ToList();
                    if (name.StartsWith("top loop")) refs = bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9).Select(EdgeRef.Of).ToList();
                    if (name.StartsWith("corner")) refs = bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && ((e.V0.P - new Vector3d(60, 40, 20)).Length < 1e-9 || (e.V1.P - new Vector3d(60, 40, 20)).Length < 1e-9)).Select(EdgeRef.Of).ToList();
                    DirectRound.OffForTest = !direct;
                    try { FilletOp.ChamferEdges(bl, refs, d1, d2, w); } finally { DirectRound.OffForTest = false; }
                    return (bl[0], w);
                }
                if (double.IsNaN(want))
                {
                    // цепочка и угол: путь резцов фаску d1 ≠ d2 на стыках не строит — сверка иначе: верх цепочки — формула
                    // ½ab·P − 4·a²b/3 (a — размер на верхней грани, из её площади (60 − 2a)(40 − 2a)); угол — тело ∩ полупространства
                    // плоскостей фасок результата (общей булевой) — та же форма, что у построителя
                    var (a2, w2) = Run(true);
                    bool dir2 = w2.Any(x => x.StartsWith("Chamfer: direct cut " + d1.ToString("0.###")));
                    double v2 = AnalyticVolume.Of(a2) ?? double.NaN, wantX;
                    if (name.StartsWith("top loop"))
                    {
                        var top = a2.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.PlaneSurface && PlanarTweak.OutwardNormal(f) is { } nn && nn.Z > 0.999).OrderByDescending(f => AnalyticVolume.FaceArea(f) ?? 0).First();
                        double At = AnalyticVolume.FaceArea(top) ?? double.NaN;
                        double aa = (200 - Math.Sqrt(200 * 200 - 16 * (2400 - At))) / 8;           // (60 − 2a)(40 − 2a) = At
                        double bb = Math.Abs(aa - d1) < 1e-6 ? d2 : d1;
                        wantX = 48000 - (0.5 * aa * bb * 200 - 4 * aa * aa * bb / 3);
                        if (!(Math.Abs(aa - d1) < 1e-6 || Math.Abs(aa - d2) < 1e-6)) fails.Add($"{name}: the top face width {aa:R} is neither size");
                    }
                    else
                    {
                        var hs = new List<TSolid> { make() };
                        foreach (var f in a2.Faces.Where(f => f.Tag.Contains("chamfer")))
                        {
                            var pl = (Parametric3D.Kernel.Geometry.Ssi.Plane)Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))!;
                            var no = PlanarTweak.OutwardNormal(f)!.Value;
                            var u = Vector3d.Cross(no, Math.Abs(no.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized(); var vv = Vector3d.Cross(no, u);
                            var fr = new Parametric3D.Kernel.Frame3(pl.P, u, vv); double S = 400;
                            var pts = new[] { pl.P - u * S - vv * S, pl.P + u * S - vv * S, pl.P + u * S + vv * S, pl.P - u * S + vv * S };
                            var cur = new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.LineCurve3(pts[0], pts[1]), new Parametric3D.Kernel.Curves.LineCurve3(pts[1], pts[2]), new Parametric3D.Kernel.Curves.LineCurve3(pts[2], pts[3]), new Parametric3D.Kernel.Curves.LineCurve3(pts[3], pts[0]) };
                            BooleanOp.Subtract(hs, ExtrudeOp.Build(ProfileBuilder.Build(fr, cur), no * S), new List<string>());
                        }
                        wantX = OpVolume(hs[0]);
                    }
                    Log(new { test = "KRN06-UNEQUAL", name, direct = dir2, volume = v2, want = wantX, guarantee = a2.Guarantee.ToString() });
                    if (!dir2) fails.Add($"{name}: not the direct builder ({string.Join(" | ", w2.Take(2))})");
                    if (Math.Abs(v2 - wantX) > 1e-9 * Math.Abs(wantX)) fails.Add($"{name}: V {v2:R} vs {wantX:R}");
                    if (a2.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(a2).IsValid) fails.Add($"{name}: {a2.Guarantee} {SolidValidator.Validate(a2).Summary()}");
                    continue;
                }
                var (a, wa) = Run(true); var (c, _) = Run(false);
                double va = AnalyticVolume.Of(a) ?? double.NaN, vc = OpVolume(c);
                bool direct = wa.Any(x => x.StartsWith("Chamfer: direct cut " + d1.ToString("0.###")));
                string Areas(TSolid b) => string.Join(";", b.Faces.Where(f => !f.Tag.Contains("chamfer")).GroupBy(f => EdgeRef.Bare(f.Tag)).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Sum(f => AnalyticVolume.FaceArea(f) ?? double.NaN):F6}"));
                string sa = Areas(a), sc = Areas(c);
                Log(new { test = "KRN06-UNEQUAL", name, direct, volume = va, cutter = vc, want, guarantee = a.Guarantee.ToString() });
                if (!direct) fails.Add($"{name}: not the direct builder ({string.Join(" | ", wa.Take(2))})");
                if (want < 0)
                {
                    // Папп: кольцо с треугольным сечением ½ab, центр тяжести на R + a/3 (a — размер на плоскости)
                    double Vp(double a) => 48000 - Math.PI * 25 * 20 - 2 * Math.PI * (5 + a / 3) * 0.5 * d1 * d2;
                    want = Math.Abs(va - Vp(d1)) < Math.Abs(va - Vp(d2)) ? Vp(d1) : Vp(d2);
                }
                if (!double.IsNaN(want) && Math.Abs(va - want) > 1e-9 * want) fails.Add($"{name}: V {va:R} vs {want:R}");
                if (Math.Abs(va - vc) > 1e-6 * Math.Abs(va)) fails.Add($"{name}: V {va:R} vs cutter path {vc:R}");
                if (sa != sc) fails.Add($"{name}: face areas differ from the cutter path — sizes on other faces? [{sa}] vs [{sc}]");
                if (a.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(a).IsValid) fails.Add($"{name}: {a.Guarantee} {SolidValidator.Validate(a).Summary()}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
