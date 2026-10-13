using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Разбор KRN-06 (06.10): брусок 40×30×20, грань x = 40 наклонена на угол (сечение плоскостью), скругление
    /// верхних рёбер «перед» (y = 0) и «наклонная грань» — стык цепочки несимметричен.</summary>
    internal static (TSolid Body, TEdge A, TEdge B) AsymChainBody(double deg, double degA = 0)
    {
        double a = deg * Math.PI / 180, x0 = 40 - 20 * Math.Tan(a), y0 = 20 * Math.Tan(degA * Math.PI / 180);
        var b = new List<TSolid> { Box(0, 0, 0, 60, 30, 20) };
        if (degA != 0)
        {
            // грань y = 0 наклонена: плоскость через (·, 0, 0) и (·, y0, 20) — резец, повёрнутый вокруг X
            var cutA = TransformOp.Copy(Box(-50, -100, -50, 200, 100, 150), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitX, -degA));
            BooleanOp.Subtract(b, cutA, new List<string>());
        }
        // резец: большой брусок, повёрнутый вокруг Y так, что его грань x = 0 проходит через (40, *, 0) и (x0, *, 20)
        var cut = Box(0, -10, -50, 100, 50, 100);
        var rot = TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, -deg);
        cut = TransformOp.Copy(cut, rot);
        cut = TransformOp.Copy(cut, TransformOp.Move(new Vector3d(40, 0, 0)));
        BooleanOp.Subtract(b, cut, new List<string>());
        var s = b[0];
        return (s, EdgeAt(s, new(0, y0, 20), new(x0, y0, 20)), EdgeAt(s, new(x0, y0, 20), new(x0, 30, 20)));
    }
    /// <summary>Вогнутый вариант: брусок 60×30×20 с угловым карманом (x &gt; стенка, y &gt; 10, z &gt; 10), стенка кармана
    /// наклонена на deg вокруг Y; скругляются два ребра дна (вдоль X при y = 10 и вдоль Y у стенки).</summary>
    internal static (TSolid Body, TEdge A, TEdge B) AsymConcaveBody(double deg)
    {
        var b = new List<TSolid> { Box(0, 0, 0, 60, 30, 20) };
        var cut = TransformOp.Copy(Box(0, 0, -50, 100, 50, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, -deg));
        cut = TransformOp.Copy(cut, TransformOp.Move(new Vector3d(30, 10, 10)));
        var cl = new List<TSolid> { cut };
        BooleanOp.Intersect(cl, Box(-100, -100, 10, 300, 300, 100), new List<string>());
        BooleanOp.Subtract(b, cl[0], new List<string>());
        var s = b[0];
        return (s, EdgeAt(s, new(30, 10, 10), new(60, 10, 10)), EdgeAt(s, new(30, 10, 10), new(30, 30, 10)));
    }
    static void AsymProbe()
    {
        foreach (var (deg, rr) in new[] { (0.0, 2.0), (20.0, 2.0), (-15.0, 2.0) })
        {
            var (s, ea, eb) = AsymConcaveBody(deg);
            Console.WriteLine($"concave deg {deg} r {rr}: body {s.Guarantee} V {AnalyticVolume.Of(s):R} faces {s.Faces.Count()}");
            var bl = new List<TSolid> { s }; var w = new List<string>();
            long j0 = DirectRound.AsymJunctions;
            FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(ea), EdgeRef.Of(eb) }, rr, w);
            var r = bl[0];
            Console.WriteLine($"  round: {r.Guarantee} V {(AnalyticVolume.Of(r) ?? double.NaN):R} faces {r.Faces.Count()} valid {SolidValidator.Validate(r).IsValid} asym {DirectRound.AsymJunctions - j0}; {string.Join(" | ", w.Take(4))}");
        }
        foreach (var (deg, degA, rr) in new[] { (0.0, 0.0, 3.0), (20.0, 0.0, 3.0), (-15.0, 0.0, 3.0), (35.0, 0.0, 2.0), (20.0, 10.0, 3.0), (-10.0, 25.0, 4.0) })
        {
            var (s, ea, eb) = AsymChainBody(deg, degA);
            Console.WriteLine($"deg {deg} degA {degA} r {rr}: body {s.Guarantee} V {AnalyticVolume.Of(s):R} faces {s.Faces.Count()}");
            var bl = new List<TSolid> { s }; var w = new List<string>();
            FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(ea), EdgeRef.Of(eb) }, rr, w);
            var r = bl[0];
            Console.WriteLine($"  round: {r.Guarantee} V {(AnalyticVolume.Of(r) ?? double.NaN):R} faces {r.Faces.Count()} valid {SolidValidator.Validate(r).IsValid}; {string.Join(" | ", w.Take(4))}");
        }
    }
    static void Krn06AsymChecks()
    {
        Check("KRN06-DIRECT-ROUND-ASYM-CHAIN", () =>
        {
            // 06.10: стык цепочки двух скруглений плоскость/плоскость при несимметричных гранях (одна или обе боковые грани
            // наклонены): два цилиндра равного радиуса сходятся по плоскому эллипсу до первой линии касания, далее второй
            // цилиндр — сечение плоскостью соседней грани до третьего ребра. Объём — эталон OCCT (BRepFilletAPI_MakeFillet,
            // те же тела и рёбра, 6 плоскостей + 2 цилиндра) с отн. погрешностью ≤ 1e-9; тело ExactAnalytic, годное, 8 граней;
            // несимметричные случаи идут веткой стыка (счётчик AsymJunctions).
            var fails = new List<string>();
            var cases = new (double Deg, double DegA, double R, double Occt)[]
            {
                (0, 0, 3, 23867.389342033533), (20, 0, 3, 21729.70928925025), (-15, 0, 3, 25419.96309669651),
                (35, 0, 2, 19771.504984369716), (20, 10, 3, 20514.58720042038), (-10, 25, 4, 20954.918434977648),
            };
            foreach (var (deg, degA, rr, occt) in cases)
            {
                var (s, ea, eb) = AsymChainBody(deg, degA);
                var bl = new List<TSolid> { s }; var w = new List<string>();
                long j0 = DirectRound.AsymJunctions;
                FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(ea), EdgeRef.Of(eb) }, rr, w);
                var r = bl[0]; double v = AnalyticVolume.Of(r) ?? double.NaN;
                string nm = $"deg {deg} degA {degA} r {rr}";
                if (r.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic) fails.Add($"{nm}: guarantee {r.Guarantee}");
                if (!(Math.Abs(v - occt) <= 1e-9 * occt)) fails.Add($"{nm}: V {v:R} vs OCCT {occt:R}");
                if (!SolidValidator.Validate(r).IsValid) fails.Add($"{nm}: invalid");
                if (r.Faces.Count() != 8) fails.Add($"{nm}: faces {r.Faces.Count()}");
                Log(new { test = "KRN06-ASYM-CHAIN", nm, volume = v, occt, guarantee = r.Guarantee.ToString(), junctions = DirectRound.AsymJunctions - j0 });
                if ((deg != 0 || degA != 0) && DirectRound.AsymJunctions == j0) fails.Add($"{nm}: no asym junction");
            }
            // вогнутый вариант: два ребра дна углового кармана с наклонённой стенкой (6 плоскостей + 2 цилиндра → 9 + 2)
            foreach (var (deg, rr, occt) in new[] { (0.0, 2.0, 30042.153404627123), (20.0, 2.0, 29668.677454720368), (-15.0, 2.0, 30323.36806959709) })
            {
                var (s, ea, eb) = AsymConcaveBody(deg);
                var bl = new List<TSolid> { s }; var w = new List<string>();
                long j0 = DirectRound.AsymJunctions;
                FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(ea), EdgeRef.Of(eb) }, rr, w);
                var r = bl[0]; double v = AnalyticVolume.Of(r) ?? double.NaN;
                string nm = $"concave deg {deg} r {rr}";
                Log(new { test = "KRN06-ASYM-CHAIN", nm, volume = v, occt, guarantee = r.Guarantee.ToString(), junctions = DirectRound.AsymJunctions - j0 });
                if (r.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic) fails.Add($"{nm}: guarantee {r.Guarantee}");
                if (!(Math.Abs(v - occt) <= 1e-9 * occt)) fails.Add($"{nm}: V {v:R} vs OCCT {occt:R}");
                if (!SolidValidator.Validate(r).IsValid) fails.Add($"{nm}: invalid");
                if (r.Faces.Count() != 11) fails.Add($"{nm}: faces {r.Faces.Count()}");
                if (deg != 0 && DirectRound.AsymJunctions == j0) fails.Add($"{nm}: no asym junction");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
