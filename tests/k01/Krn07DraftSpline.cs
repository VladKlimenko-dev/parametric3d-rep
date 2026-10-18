using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>«Стадион» с концами-кривыми Безье (G1): отрезки y = ±b, x ∈ [−c, c], концы — кубические Безье с выносом k.</summary>
    static (List<ICurve3> Loop, double A, double L) BezierStadium(double c, double b, double k)
    {
        var right = new BezierCurve3(new[] { new Vector3d(c, -b, 0), new Vector3d(c + k, -b, 0), new Vector3d(c + k, b, 0), new Vector3d(c, b, 0) });
        var left = new BezierCurve3(new[] { new Vector3d(-c, b, 0), new Vector3d(-c - k, b, 0), new Vector3d(-c - k, -b, 0), new Vector3d(-c, -b, 0) });
        var loop = new List<ICurve3> { new LineCurve3(new(-c, -b, 0), new(c, -b, 0)), right, new LineCurve3(new(c, b, 0), new(-c, b, 0)), left };
        // площадь — ∮ x dy (у отрезков dy = 0; у кривой Безье x·y′ — многочлен 5-й степени, Гаусс по 3 узлам точен)
        double[] g3 = { -0.7745966692414834, 0, 0.7745966692414834 }, w3 = { 5.0 / 9, 8.0 / 9, 5.0 / 9 };
        double A = 0, Lb = 0;
        foreach (var bz in new[] { right, left })
        {
            for (int q = 0; q < 3; q++)
            {
                double t = 0.5 + 0.5 * g3[q];
                var d = 3 * ((bz.P[1] - bz.P[0]) * (1 - t) * (1 - t) + (bz.P[2] - bz.P[1]) * (2 * t * (1 - t)) + (bz.P[3] - bz.P[2]) * (t * t));
                A += 0.5 * w3[q] * bz.PointAt(t).X * d.Y;
            }
            // длина — составной Гаусс (64 куска по 8 узлов): подынтегральное гладкое, ошибка ниже 1e-15
            double[] g8 = { -0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498, 0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363 };
            double[] w8 = { 0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620, 0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763 };
            const int P = 64;
            for (int pi = 0; pi < P; pi++)
                for (int q = 0; q < 8; q++)
                {
                    double t = (pi + 0.5 + 0.5 * g8[q]) / P;
                    var d = 3 * ((bz.P[1] - bz.P[0]) * (1 - t) * (1 - t) + (bz.P[2] - bz.P[1]) * (2 * t * (1 - t)) + (bz.P[3] - bz.P[2]) * (t * t));
                    Lb += 0.5 * w8[q] / P * d.Length;
                }
        }
        return (loop, Math.Abs(A), 4 * c + Lb);
    }

    static void Krn07DraftSplineChecks()
    {
        Check("KRN07-DRAFT-BEZIER-WALLS", () =>
        {
            // 07.6: УКЛОН СТЕНОК-ВЫДАВЛИВАНИЙ КРИВЫХ БЕЗЬЕ. «Стадион» c = 10, b = 8, концы — кубические Безье с выносом 8 (касание
            // с отрезками), выдавлен на H = 20. Стенки наклоняются вокруг нейтрали; сечение на высоте z — эквидистанта контура
            // на δ(z) (контур гладкий, поворот касательной 2π): площадь A − L·δ + π·δ² (точно, пока δ меньше наименьшего радиуса
            // кривизны 6). V = ∫ (A − Lδ + πδ²) dz. Выбор одной стенки Безье распространяется на все по касательным рёбрам.
            var fails = new List<string>();
            var (loop, A, L) = BezierStadium(10, 8, 8);
            const double H = 20;
            TSolid Body() => ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), loop), new Vector3d(0, 0, H));
            double Want(double deg, double z0, double s) { double t = Math.Tan(deg * Math.PI / 180); double V = 0;
                // δ(z) = t·s·(z − z0) — многочлен, Симпсон точен для квадратичной площади
                double Ar(double z) { double d = t * s * (z - z0); return A - L * d + Math.PI * d * d; }
                V = H / 6 * (Ar(0) + 4 * Ar(H / 2) + Ar(H)); return V; }
            var rightMid = new Vector3d(10 + 6, 0, H / 2);       // середина правой кривой Безье: x = c + 0.75k
            var cases = new (string Name, Vector3d[] Probes, double Z0, double Deg, double S)[]
            {
                ("one Bezier wall 5°, neutral at bottom (propagation)", new[] { rightMid }, 0, 5, 1),
                ("one Bezier wall 4°, neutral at top", new[] { rightMid }, H, 4, -1),
                ("one Bezier wall −3° (outward)", new[] { rightMid }, 0, -3, 1),
            };
            foreach (var (name, probes, z0, deg, s) in cases)
            {
                var body0 = Body();
                if (!SolidValidator.Validate(body0).IsValid) { fails.Add($"{name}: the extruded body is invalid"); continue; }
                var bl = new List<TSolid> { body0 }; var w = new List<string>(); long d0 = DirectRound.DraftSplineWalls;
                bool ok = DraftOp.Apply(bl, probes, new Vector3d(0, 0, z0), Vector3d.UnitZ, deg, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN, want = Want(deg, z0, s);
                Console.WriteLine($"  {name}: ok {ok}, V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}, {b.Faces.Count()} faces | {string.Join(" | ", w.Take(2))}");
                if (!ok || DirectRound.DraftSplineWalls == d0) fails.Add($"{name}: not the Bezier-wall draft ({string.Join(" | ", w.Take(3))})");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                var sw = new StringWriter(); var ws = new List<string>();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "dr", ws) || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Take(2))})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>
    /// m.g4: УКЛОН СТЕНОК NURBS. То же тело «стадион», но лофтом двух одинаковых сечений (z = 0 и 20): стенки — грани NURBS,
    /// линейчатые вдоль вытяжки. Уклон — та же формула эквидистанты, что у стенок-выдавливаний (KRN07-DRAFT-BEZIER-WALLS).
    /// </summary>
    static void Krn07DraftNurbsChecks()
    {
        Check("KRN07-DRAFT-NURBS-WALLS", () =>
        {
            var fails = new List<string>();
            var (loop, A, L) = BezierStadium(10, 8, 8);
            const double H = 20;
            Parametric3D.Kernel.Ops.Profile Sec(double z) => ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY),
                loop.Select(c => c switch { LineCurve3 l => (ICurve3)new LineCurve3(l.A + new Vector3d(0, 0, z), l.B + new Vector3d(0, 0, z)), BezierCurve3 b => new BezierCurve3(b.P.Select(q => q + new Vector3d(0, 0, z))), _ => c }).ToList());
            TSolid Body() => LoftOp.Build(new[] { Sec(0), Sec(H) }, 64, false);
            double Want(double deg, double z0, double sg) { double t = Math.Tan(deg * Math.PI / 180); double Ar(double z) { double d = t * sg * (z - z0); return A - L * d + Math.PI * d * d; } return H / 6 * (Ar(0) + 4 * Ar(H / 2) + Ar(H)); }
            var rightMid = new Vector3d(10 + 6, 0, H / 2);
            foreach (var (name, z0, deg, sg) in new (string, double, double, double)[] { ("NURBS wall 5°, neutral at bottom", 0, 5, 1), ("NURBS wall 4°, neutral at top", H, 4, -1), ("NURBS wall −3°", 0, -3, 1) })
            {
                var body0 = Body();
                int nurbs = body0.Faces.Count(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.NurbsSurface);
                if (nurbs < 2 || !SolidValidator.Validate(body0).IsValid) { fails.Add($"{name}: the lofted body has {nurbs} NURBS walls, valid {SolidValidator.Validate(body0).IsValid}"); continue; }
                var bl = new List<TSolid> { body0 }; var w = new List<string>(); long d0 = DirectRound.DraftSplineWalls;
                bool ok = DraftOp.Apply(bl, new[] { rightMid }, new Vector3d(0, 0, z0), Vector3d.UnitZ, deg, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN, want = Want(deg, z0, sg);
                Console.WriteLine($"  {name}: ok {ok}, V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}, {b.Faces.Count()} faces ({nurbs} NURBS walls) | {string.Join(" | ", w.Take(2))}");
                if (!ok || DirectRound.DraftSplineWalls == d0) fails.Add($"{name}: not the wall draft ({string.Join(" | ", w.Take(3))})");
                else
                {
                    if (!SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {SolidValidator.Validate(b).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>
    /// m.g4: ОБОЛОЧКА ТЕЛА СО СТЕНКАМИ ПО КРИВЫМ БЕЗЬЕ («стадион» c = 10, b = 8, вынос 8, H = 20). Сечение полости — эквидистанта
    /// контура внутрь на t: площадь A − L·t + π·t² (точно, пока t меньше наименьшего радиуса кривизны 6). Закрытая полость —
    /// высота H − 2t, открытый верх — H − t. Стенки смещаются точно (поверхность смещения по нормали), объём — интегралом
    /// (до 1e-6: рёбра полости после булевой — по формуле сечения стенки; бюджет интегрирования ядра 1e-5).
    /// </summary>
    static void Krn07ShellBezierChecks()
    {
        Check("KRN07-SHELL-BEZIER-WALLS", () =>
        {
            var fails = new List<string>();
            var (loop, A, L) = BezierStadium(10, 8, 8);
            const double H = 20;
            if (Environment.GetEnvironmentVariable("P3D_SHELLDBG") == "1") { ShellOp.ShellDebug = m => Console.WriteLine("   [shell] " + m); DirectRound.Debug = m => Console.WriteLine("   [dr] " + m); }
            if (Environment.GetEnvironmentVariable("P3D_SHELLMESHDBG") == "1") Parametric3D.Kernel.Tess.ExactFaceMesher.TryDebug = m => Console.WriteLine("   [mesh] " + m);
            TSolid Body() => ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), loop), new Vector3d(0, 0, H));
            foreach (var (name, t, open) in new (string, double, Vector3d[]?)[]
            {
                ("closed shell t = 1", 1, null), ("open top, t = 1.5", 1.5, new[] { new Vector3d(0, 0, H) }), ("open bottom, t = 2", 2, new[] { new Vector3d(0, 0, 0) }),
            }.Where(_ => Environment.GetEnvironmentVariable("P3D_SHELLNURBSONLY") != "1"))
            {
                var bl = new List<TSolid> { Body() }; var w = new List<string>();
                if (Environment.GetEnvironmentVariable("P3D_SHELLDBG") == "1")
                {
                    string? wy = null; var off = DirectRound.OffsetSolid(Body(), f => -t, "x", ref wy);
                    Console.WriteLine($"     offset solid: {(off == null ? wy : $"V {AnalyticVolume.Of(off):R} want {(A - L * t + Math.PI * t * t) * (H - 2 * t):R}")}");
                    if (off != null) foreach (var vx in off.Vertices) Console.WriteLine($"       V {vx.P.X:R} {vx.P.Y:R} {vx.P.Z:R}");
                    if (off != null) foreach (var fc in off.Faces) Console.WriteLine($"       {GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} int {AnalyticVolume.FaceIntegral(fc)} area {AnalyticVolume.FaceArea(fc)} edges {string.Join(",", fc.Edges.Select(e => e.Curve.GetType().Name))}");
                }
                bool ok = ShellOp.Hollow(bl, t, open, w);
                double cav = (A - L * t + Math.PI * t * t) * (H - (open == null ? 2 : 1) * t), want = A * H - cav;
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                var vr = SolidValidator.Validate(b);
                Console.WriteLine($"  {name}: ok {ok}, V {v:R} want {want:R} ({(v - want) / want:E1}) {b.Guarantee}, {b.Faces.Count()} faces, valid {vr.IsValid} | {string.Join(" | ", w.Take(3))}");
                if (Environment.GetEnvironmentVariable("P3D_SHELLDBG") == "1")
                    foreach (var fc in b.Faces) Console.WriteLine($"     face {fc.Tag} {fc.Surface.GetType().Name}/{GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name} int {AnalyticVolume.FaceIntegral(fc)} edges {string.Join(",", fc.Edges.Select(e => e.Curve.GetType().Name))}");
                if (!ok) { fails.Add($"{name}: refused ({string.Join(" | ", w.Take(3))})"); continue; }
                if (!vr.IsValid) fails.Add($"{name}: {vr.Summary()}");
                if (!(Math.Abs(v - want) <= 1e-6 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    /// <summary>
    /// m.g4: ОБОЛОЧКА ТЕЛА С ГРАНЯМИ NURBS (гладкий лофт сечений «D» из отрезка и кривой Безье). Закрытая оболочка t = 0.5:
    /// годна, узлы сеток внутренних граней — на расстоянии t от исходного тела (1e-6), объём = V(тело) − V(смещённое тело).
    /// </summary>
    static void Krn07ShellNurbsChecks()
    {
        Check("KRN07-SHELL-NURBS-LOFT", () =>
        {
            if (Environment.GetEnvironmentVariable("P3D_SHELLDBG") == "1") { ShellOp.ShellDebug = m => Console.WriteLine("   [shell] " + m); DirectRound.Debug = m => Console.WriteLine("   [dr] " + m); }
            if (Environment.GetEnvironmentVariable("P3D_SHELLMESHDBG") == "1") Parametric3D.Kernel.Tess.ExactFaceMesher.TryDebug = m => Console.WriteLine("   [mesh] " + m);
            var fails = new List<string>();
            Parametric3D.Kernel.Ops.Profile D(double lam, double z) { Vector3d P(double x, double y) => new(x * lam, y * lam, z); return ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new ICurve3[] { new LineCurve3(P(-5, 0), P(5, 0)), new BezierCurve3(new[] { P(5, 0), P(5, 8), P(-5, 8), P(-5, 0) }) }); }
            TSolid Loft() => LoftOp.Build(new[] { D(1, 0), D(0.75, 10), D(0.5, 20) }, 64, true);
            foreach (var (t, open) in new (double, Vector3d[]?)[] { (0.5, null), (1.0, null), (0.5, new[] { new Vector3d(0, 1, 20) }) })
            {
                var body0 = Loft(); double v0 = AnalyticVolume.Of(body0) ?? double.NaN;
                string? wy = null; var cav = DirectRound.OffsetSolid(Loft(), f => -t, "x", ref wy); double vc = cav == null ? double.NaN : AnalyticVolume.Of(cav) ?? double.NaN;
                var bl = new List<TSolid> { Loft() }; var w = new List<string>();
                bool ok = ShellOp.Hollow(bl, t, open, w); var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                double worst = 0; int n = 0;
                foreach (var f in b.Faces.Where(x => x.Tag.StartsWith("shell:inner") && x.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface))
                    foreach (var (a0, _, _) in ((Parametric3D.Kernel.Surfaces.FacetedSurface)f.Surface).Facets.Take(200))
                    {
                        // узел сетки — на точную поверхность своей грани (сетка многогранного результата отходит на прогиб хорды)
                        var sf = GeometryAudit.AuthoritativeSurface(f)!; var a = sf.PointAt(sf.ProjectToUV(a0));
                        double dm = double.MaxValue;
                        foreach (var g in body0.Faces)
                        {
                            var sg = GeometryAudit.AuthoritativeSurface(g)!;
                            if (open != null && open.Any(o => (sg.PointAt(sg.ProjectToUV(o)) - o).Length < 1e-6)) continue;   // открытая грань
                            dm = Math.Min(dm, (sg.PointAt(sg.ProjectToUV(a)) - a).Length);
                        }
                        worst = Math.Max(worst, Math.Abs(dm - t)); n++;
                    }
                var vr = SolidValidator.Validate(b);
                Console.WriteLine($"  NURBS loft shell t = {t}{(open != null ? " open top" : "")}: ok {ok}, V {v:R}, body {v0:R} − offset {vc:R} = {v0 - vc:R}, {n} inner points off t by ≤ {worst:E1}, valid {vr.IsValid} {b.Guarantee} | {wy} | {string.Join(" | ", w.Take(2))}");
                if (!ok || !vr.IsValid) { fails.Add($"t {t}: ok {ok}, {vr.Summary()} ({wy}; {string.Join(" | ", w.Take(2))})"); continue; }
                // закрытая — объём тела минус смещённое тело; открытая — объём против сетки (1e-3) и больше закрытой той же толщины
                if (open == null && !(Math.Abs(v - (v0 - vc)) <= 1e-6 * v)) fails.Add($"t {t}: V {v:R} vs {v0 - vc:R}");
                if (open != null)
                {
                    double vt = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
                    Console.WriteLine($"    open: tessellated {vt:R}");
                    if (!(Math.Abs(v - vt) <= 1e-3 * v) || !(v < v0 - vc)) fails.Add($"t {t} open: V {v:R}, mesh {vt:R}, closed {v0 - vc:R}");
                }
                if (n < 50 || !(worst <= 1e-6)) fails.Add($"t {t}: inner points off the offset distance by {worst:E1} ({n} points)");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void Krn07DraftNonRuledChecks()
    {
        Check("KRN07-DRAFT-NONRULED-REFUSED", () =>
        {
            // 07.6: грань без образующих вдоль вытяжки (шар, тор, тор скругления контура) уклоном не наклоняется — у неё нет
            // линии на нейтрали, вокруг которой поворачивать образующие (так же отказывают распространённые CAD). Отказ с
            // причиной, тело не тронуто (объём и годность прежние).
            var fails = new List<string>();
            TSolid RoundedTop()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>());
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-7 && Math.Abs(e.V1.P.Z - 20) < 1e-7).Select(EdgeRef.Of).ToList(), 3, new List<string>());
                return bl[0];
            }
            double q = 3 / Math.Sqrt(2);
            var cases = new (string Name, Func<TSolid> Make, Vector3d Probe)[]
            {
                ("sphere r10", () => PrimitiveOp.Sphere(Vector3d.Zero, 10), new Vector3d(10, 0, 0)),
                ("torus 20/5", () => PrimitiveOp.Torus(Vector3d.Zero, 20, 5), new Vector3d(25, 0, 0)),
                ("torus of a rounded top loop", RoundedTop, new Vector3d(5 - (5 - 3 + q) / Math.Sqrt(2) * 0 , 0, 0)),
            };
            foreach (var (name, make, probe0) in cases)
            {
                var b0 = make(); double v0 = AnalyticVolume.Of(b0) ?? double.NaN;
                var probe = probe0;
                if (name.StartsWith("torus of"))
                {
                    // точка на торе скругления у угла (0, 0): центр дуги (5, 5), ось — z; середина четверти тора
                    var dir = new Vector3d(-1, -1, 0).Normalized(); double rc = 5 - 3 + 3 * Math.Cos(Math.PI / 4);
                    probe = new Vector3d(5, 5, 17) + dir * rc + new Vector3d(0, 0, 3 * Math.Sin(Math.PI / 4));
                }
                var bl = new List<TSolid> { b0 }; var w = new List<string>();
                bool ok = DraftOp.Apply(bl, new[] { probe }, Vector3d.Zero, Vector3d.UnitZ, 5, w);
                double v = AnalyticVolume.Of(bl[0]) ?? double.NaN;
                Console.WriteLine($"  {name}: ok {ok}, V {v:R} (was {v0:R}) | {string.Join(" | ", w.Take(2))}");
                if (ok || !ReferenceEquals(bl[0], b0) && !(Math.Abs(v - v0) <= 1e-12 * Math.Abs(v0)) || !SolidValidator.Validate(bl[0]).IsValid || w.Count == 0)
                    fails.Add($"{name}: ok {ok}, V {v:R} vs {v0:R} (must refuse with a reason, body untouched)");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
