using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// Независимый эталон скругления прямого ребра прямого угла с линейным радиусом r(y) = r0 + (r1 − r0)·y/L — по определению
    /// катящегося шара, без поверхности ядра: в сечении y0 снимается область 0 ≤ v ≤ r(y0), 0 ≤ u ≤ u(v), где (u, v) — расстояния
    /// от двух плоскостей, а u(v) = min по шарам y' [r(y') − √(r(y')² − (y0 − y')² − (v − r(y'))²)] — ближняя к ребру граница
    /// объединения шаров (центры — в (r, r)). Минимум по y' — сеткой с уточнением золотым сечением; интегралы — Гаусс.
    /// </summary>
    static double RollingBallVariableCorner(double L, double r0, double r1)
    {
        double g = (r1 - r0) / L;
        double R(double y) => r0 + g * y;
        double rmax = Math.Max(r0, r1) * 1.6;
        double U(double y0, double v)
        {
            double F(double y)
            {
                double r = R(y); double q = r * r - (y0 - y) * (y0 - y) - (v - r) * (v - r);
                return q < 0 ? double.PositiveInfinity : r - Math.Sqrt(q);
            }
            double lo = y0 - rmax, hi = y0 + rmax; int N = 400; double best = double.PositiveInfinity, by = y0;
            for (int i = 0; i <= N; i++) { double y = lo + (hi - lo) * i / N; double f = F(y); if (f < best) { best = f; by = y; } }
            double a = by - (hi - lo) / N, b = by + (hi - lo) / N, gr = (Math.Sqrt(5) - 1) / 2;
            double c = b - gr * (b - a), d = a + gr * (b - a), fc = F(c), fd = F(d);
            for (int it = 0; it < 80; it++)
            {
                if (fc < fd) { b = d; d = c; fd = fc; c = b - gr * (b - a); fc = F(c); }
                else { a = c; c = d; fc = fd; d = a + gr * (b - a); fd = F(d); }
            }
            return Math.Min(best, Math.Min(fc, fd));
        }
        double[] gx = { -0.9931285991850949, -0.9639719272779138, -0.9122344282513259, -0.8391169718222188, -0.7463319064601508, -0.6360536807265150, -0.5108670019508271, -0.3737060887154195, -0.2277858511416451, -0.0765265211334973, 0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150, 0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949 };
        double[] gw = { 0.0176140071391521, 0.0406014298003869, 0.0626720483341091, 0.0832767415767048, 0.1019301198172404, 0.1181945319615184, 0.1316886384491766, 0.1420961093183820, 0.1491729864726037, 0.1527533871307258, 0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184, 0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521 };
        double vol = 0; int panels = 4;
        for (int py = 0; py < panels; py++)
            for (int i = 0; i < 20; i++)
            {
                double y0 = L * (py + (gx[i] + 1) / 2) / panels, wy = gw[i] / 2 * L / panels, r = R(y0), area = 0;
                // v = r(1 − cos τ): у касания с плоскостью v = 0 граница — как окружность (корневая особенность) — снимается
                for (int j = 0; j < 20; j++) { double tau = Math.PI / 4 * (gx[j] + 1); double v = r * (1 - Math.Cos(tau)); area += gw[j] * Math.PI / 4 * r * Math.Sin(tau) * U(y0, v); }
                vol += wy * area;
            }
        return vol;
    }

    static void Krn06VariableChecks()
    {
        Check("KRN06-VARIABLE-RADIUS-EXACT", () =>
        {
            // эталон сам проверен на постоянном радиусе: r²(1 − π/4)·L
            double refConst = RollingBallVariableCorner(40, 3, 3 + 1e-9), exactConst = 9 * (1 - Math.PI / 4) * 40;
            Log(new { test = "KRN06-VARIABLE-REFERENCE", constant = refConst, exact = exactConst });
            Require(Math.Abs(refConst - exactConst) < 1e-6 * exactConst, $"reference at constant radius {refConst:R} vs {exactConst:R}");
            // 06.14: скругление с линейно меняющимся радиусом прямым построителем — точный конус (огибающая шаров), контакты —
            // прямые, торцы — дуги эллипса. Брусок 60×40×20, ребро x = 60, z = 20 (выпуклое) r 2→4 и 4→1; уступ L-профиля
            // (вогнутое ребро) r 2→4. Объём снятого (добавленного) — независимый эталон по определению катящегося шара
            // (сечения, объединение шаров, Гаусс) до 1e-6 отн.; тело ExactAnalytic, годно.
            var fails = new List<string>();
            foreach (var (name, r0, r1, concave) in new[] { ("convex 2→4", 2.0, 4.0, false), ("convex 4→1", 4.0, 1.0, false), ("concave 2→4", 2.0, 4.0, true) })
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                TEdge e;
                if (!concave) e = bl[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.X - 60) < 1e-9 && Math.Abs(x.V1.P.X - 60) < 1e-9 && Math.Abs(x.V0.P.Z - 20) < 1e-9 && Math.Abs(x.V1.P.Z - 20) < 1e-9);
                else
                {
                    BooleanOp.Subtract(bl, Box(30, -1, 10, 31, 42, 11), new List<string>());
                    e = bl[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.X - 30) < 1e-9 && Math.Abs(x.V1.P.X - 30) < 1e-9 && Math.Abs(x.V0.P.Z - 10) < 1e-9 && Math.Abs(x.V1.P.Z - 10) < 1e-9);
                }
                var er = EdgeRef.Of(e);
                // закон по ссылке: R(Start) = r0; эталон — от того же конца
                double v0 = concave ? 36000 : 48000;
                var w = new List<string>();
                FilletOp.RoundEdgesVariable(bl, new List<EdgeRef> { er }, r0, r1, w);
                var b = bl[0]; double? v = AnalyticVolume.Of(b);
                double dv = RollingBallVariableCorner(40, r0, r1), want = concave ? v0 + dv : v0 - dv;
                bool direct = w.Any(x => x.Contains("direct variable blend"));
                Log(new { test = "KRN06-VARIABLE", name, volume = v, reference = want, removed = dv, direct, guarantee = b.Guarantee.ToString() });
                if (!direct) fails.Add($"{name}: not the direct builder ({string.Join(" | ", w.Take(2))})");
                else if (v is not double vv || Math.Abs(vv - want) > 1e-6 * dv) fails.Add($"{name}: V {v:R} vs reference {want:R} (removed {dv:R})");
                if (b.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
            }
            {   // в дереве детали: RoundFeature.RadiusEnd, сохранение и загрузка документа
                var p = new Parametric3D.Model.Part("var"); Parametric3D.Model.TestGeometry.AddCube(p, 60); p.Rebuild(null);     // [-30, 30]³
                var e = p.Bodies[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.X - 30) < 1e-9 && Math.Abs(x.V1.P.X - 30) < 1e-9 && Math.Abs(x.V0.P.Z - 30) < 1e-9 && Math.Abs(x.V1.P.Z - 30) < 1e-9);
                var rf = new Parametric3D.Model.Features.RoundFeature("R") { Radius = new Parametric3D.Model.Features.ValueRef(1.5), RadiusEnd = new Parametric3D.Model.Features.ValueRef(5) };
                rf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(rf); p.Rebuild(null);
                double want = 216000 - RollingBallVariableCorner(60, 1.5, 5);
                var (q, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(p)); q.Rebuild(null);
                Log(new { test = "KRN06-VARIABLE", name = "part R1.5→5", volume = p.Volume, reloaded = q.Volume, reference = want, err = rf.Error });
                if (rf.Error != null || Math.Abs(p.Volume - want) > 1e-6 * (216000 - want)) fails.Add($"part: {rf.Error} V {p.Volume:R} vs {want:R}");
                if (Math.Abs(q.Volume - want) > 1e-6 * (216000 - want)) fails.Add($"part after save/load: V {q.Volume:R} vs {want:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
