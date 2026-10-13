using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Правильная n-угольная пирамида (вершина степени n над (20, 20, 40)): брусок минус n резцов, плоскости
    /// x' = 40 − z·tg — повёрнуты вокруг вертикали через (20, 20) на 360°/n; slope2 ≠ 0.5 — вторая грань с другим уклоном
    /// (шара, касающегося всех граней, нет).</summary>
    internal static TSolid PyramidBody(int n, double slope2 = 0.5)
    {
        var bl = new List<TSolid> { Box(-30, -30, 0, 100, 100, 60) };
        for (int q = 0; q < n; q++)
        {
            double sl = q == 1 ? slope2 : 0.5, deg = Math.Atan(sl) * 180 / Math.PI;
            // плоскость через (20 + 20·0.5/sl, ·, 0)… проходит через вершину (20, 20, 40): x = 20 + (40 − z)·sl
            var c = TransformOp.Copy(Box(0, -100, -50, 200, 300, 200), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, -deg));
            c = TransformOp.Copy(c, TransformOp.Move(new Vector3d(20 + 40 * sl, -20, 0)));
            BooleanOp.Subtract(bl, TransformOp.Copy(c, TransformOp.Rotate(new Vector3d(20, 20, 0), Vector3d.UnitZ, 360.0 * q / n)), new List<string>());
        }
        return bl[0];
    }
    /// <summary>Объём правильной пирамиды (основание — n-угольник с вписанной окружностью a = 20, высота H = 40) со скруглением
    /// ВСЕХ рёбер радиусом r — по формуле Штейнера: тело = Q ⊕ шар r, Q — внутреннее параллельное тело (у тела со вписанной
    /// сферой — гомотетия с коэффициентом (ρ − r)/ρ от её центра): V = V(Q) + S(Q)·r + M(Q)·r² + 4πr³/3,
    /// M = ½ Σ L_e·(угол между наружными нормалями граней ребра).</summary>
    internal static double PyramidSteiner(int n, double r, double a0 = 20, double H0 = 40)
    {
        double k = a0 / H0, rho = a0 / (k + Math.Sqrt(1 + k * k)), lam = (rho - r) / rho;
        double a = a0 * lam, H = H0 * lam, tn = Math.Tan(Math.PI / n);
        double Ab = n * a * a * tn, V = Ab * H / 3, S = Ab + Ab * Math.Sqrt(a * a + H * H) / a;
        // нормали: основание (0,0,−1); боковая i — (cos θi, sin θi, a/H) / |·|
        Vector3d Ns(double th) => new Vector3d(Math.Cos(th), Math.Sin(th), a / H).Normalized();
        double extBase = Math.Acos(Vector3d.Dot(new Vector3d(0, 0, -1), Ns(0)));
        double extSide = Math.Acos(Vector3d.Dot(Ns(0), Ns(2 * Math.PI / n)));
        double Lb = 2 * a * tn, Ls = Math.Sqrt(a * a / (Math.Cos(Math.PI / n) * Math.Cos(Math.PI / n)) + H * H);
        double M = 0.5 * n * (Lb * extBase + Ls * extSide);
        return V + S * r + M * r * r + 4 * Math.PI * r * r * r / 3;
    }
    static void Krn06Probe2()
    {
        void Show(string nm, List<TSolid> bl, Func<TSolid, List<TEdge>> sel, double r, bool ch = false)
        {
            var s = bl[0]; var es = sel(s);
            Console.WriteLine($"{nm}: body {s.Guarantee} V {AnalyticVolume.Of(s):R} edges sel {es.Count}");
            var w = new List<string>(); long d0 = DirectRound.Declined;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { if (ch) FilletOp.ChamferEdges(bl, es.Select(EdgeRef.Of).ToList(), r, r, w); else FilletOp.RoundEdges(bl, es.Select(EdgeRef.Of).ToList(), r, w); }
            catch (Exception ex) { Console.WriteLine("  threw " + ex.Message); return; }
            var b = bl[0];
            if (Environment.GetEnvironmentVariable("P3D_PROBEMESH") == "1") BooleanOp.MeshExactlySolid(b, w);
            Console.WriteLine($"  {sw.ElapsedMilliseconds} ms {b.Guarantee} V {(AnalyticVolume.Of(b) ?? double.NaN):R} faces {b.Faces.Count()} valid {SolidValidator.Validate(b).IsValid} [{SolidValidator.Validate(b).Summary()}]; declined {DirectRound.Declined - d0}; {string.Join(" | ", w.Take(3))}");
            foreach (var iss in SolidValidator.Validate(b).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4)) Console.WriteLine($"    issue {iss.Where} {iss}");
        }
        DirectRound.Debug = x => { if (x.StartsWith("declined")) Console.WriteLine("    " + x); };
        {   // наклонное отверстие
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
            var cyl = TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(30, 20, -20), 5, 60), TransformOp.Rotate(new Vector3d(30, 20, 10), Vector3d.UnitX, 25));
            BooleanOp.Subtract(bl, cyl, new List<string>());
            Show("inclined hole top r1.5", bl, s => s.Edges.Where(e => e.V0.P.Z > 19.999 && e.V1.P.Z > 19.999 && e.Curve is not Parametric3D.Kernel.Curves.LineCurve3).ToList(), 1.5);
        }
        foreach (var (n, slope2) in new[] { (4, 0.5), (5, 0.5), (6, 0.5), (4, 0.7) })
        {
            var bl = new List<TSolid> { PyramidBody(n, slope2) };
            Console.WriteLine($"pyramid {n} slope2 {slope2}: faces {bl[0].Faces.Count()} V {AnalyticVolume.Of(bl[0]):R}");
            Show($"pyramid {n} apex r2", bl, s => s.Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).ToList(), 2);
            var bl2 = new List<TSolid> { PyramidBody(n, slope2) };
            Show($"pyramid {n} all edges r2 (Steiner {PyramidSteiner(n, 2):R})", bl2, s => s.Edges.ToList(), 2);
        }
        DirectRound.Debug = null;
    }
    static void Krn06NCornerChecks()
    {
        Check("KRN06-DIRECT-ROUND-N-CORNER", () =>
        {
            // 06.13: угол из n > 3 скруглений равного радиуса — шар, касающийся всех n граней, угол — сферический n-угольник.
            // Правильные пирамиды n = 4, 5, 6 со скруглением ВСЕХ рёбер r = 2: тело = Q ⊕ шар (Q — внутреннее параллельное
            // тело), объём — формула Штейнера (независимо от построителя), ≤ 1e-9 отн. Только рёбра вершины: годное точное тело
            // с гранью «round:corner». Пирамида с разными уклонами (общего шара нет): прямой построитель отказывает; обходной
            // путь — либо годное тело, либо отказ итоговой проверки (P3D_FILLETFINALCHECK), но не негодное тело.
            var fails = new List<string>();
            foreach (int n in new[] { 4, 5, 6 })
            {
                var bl = new List<TSolid> { PyramidBody(n) }; var w = new List<string>(); long c0 = DirectRound.NCorners;
                FilletOp.RoundEdges(bl, bl[0].Edges.Select(EdgeRef.Of).ToList(), 2, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN, want = PyramidSteiner(n, 2);
                Log(new { test = "KRN06-N-CORNER", n, all = true, volume = v, want, guarantee = b.Guarantee.ToString(), corners = DirectRound.NCorners - c0 });
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"n {n} all: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"n {n} all: V {v:R} vs Steiner {want:R}");
                if (DirectRound.NCorners == c0) fails.Add($"n {n} all: no n-corner");
            }
            {
                var bl = new List<TSolid> { PyramidBody(4) }; var w = new List<string>(); long c0 = DirectRound.NCorners;
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 2, w);
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                // OpenCascade строит угол заполнением (B-сплайн) — 21230.4886, сфера даёт 21230.2924 (сверка — по формуле выше)
                Log(new { test = "KRN06-N-CORNER", n = 4, all = false, volume = v, occtFilling = 21230.48860214654, guarantee = b.Guarantee.ToString() });
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid || DirectRound.NCorners == c0) fails.Add($"apex only: {b.Guarantee} {SolidValidator.Validate(b).Summary()} corners {DirectRound.NCorners - c0}");
                if (b.Faces.Count(f => f.Tag == "round:corner") != 1) fails.Add("apex only: no single corner face");
                if (!(v < 21333.333333 && v > 21230)) fails.Add($"apex only: V {v:R}");
            }
            {
                var bl = new List<TSolid> { PyramidBody(4, 0.7) }; var w = new List<string>(); long c0 = DirectRound.NCorners;
                try
                {
                    FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 2, w);
                    if (!SolidValidator.Validate(bl[0]).IsValid) fails.Add($"irregular apex: an invalid body was accepted: {SolidValidator.Validate(bl[0]).Summary()}");
                }
                catch (Parametric3D.Core.RebuildException ex) { Log(new { test = "KRN06-N-CORNER", irregular = "refused", ex.Message }); }
                if (DirectRound.NCorners != c0) fails.Add("irregular apex: the direct builder took a corner without a common ball");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
