using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// KRN-11 (11.2): МЕТАМОРФНЫЕ ТОЖДЕСТВА ПРОЧИХ ОПЕРАЦИЙ — скругления, фаски, оболочки, массивов. Операция над телом,
    /// сдвинутым и повёрнутым, отражённым или подобно растянутым в k раз (параметры операции — тоже в k раз), даёт тело того
    /// же объёма (у подобия — k³); массив несоприкасающихся копий — n объёмов, соприкасающихся — объём объединения.
    /// Каждый результат — валидное тело с названной гарантией; точный результат проверяется до 1e-7 относительной.
    /// </summary>
    static void Krn11QualOpsChecks()
    {
        Check("KRN11-QUAL-METAMORPHIC-OPS", () =>
        {
            var fails = new List<string>(); var rows = new List<string>();
            List<int>? topIdx = null; List<TEdge>? es0 = null; Func<Vector3d, Vector3d> curX = p => p; double curK = 1;
            static Vector3d Mid(TEdge e) => e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2);
            TSolid HoleBox() { var l = new List<TSolid> { Box(0, 0, 0, 30, 20, 10) }; BooleanOp.Subtract(l, PrimitiveOp.Cylinder(new Vector3d(15, 10, -5), 4, 20), new List<string>()); return l[0]; }
            // операция: тело, отображение точек (для точек открытых граней), множитель размеров → тела результата
            var cases = new List<(string Name, Func<TSolid> Base, Func<TSolid, Func<Vector3d, Vector3d>, double, List<TSolid>?> Op)>
            {
                ("round all box edges r2", () => Box(0, 0, 0, 30, 20, 10), (b, x, k) => Edges(b, _ => true, (l, e) => FilletOp.RoundEdges(l, e, 2 * k, new List<string>()))),
                ("round top edges of holed box r1.5", HoleBox, (b, x, k) => Edges(b, null, (l, e) => FilletOp.RoundEdges(l, e, 1.5 * k, new List<string>()))),
                ("chamfer top edges 2x1", () => Box(0, 0, 0, 30, 20, 10), (b, x, k) => Edges(b, null, (l, e) => FilletOp.ChamferEdges(l, e, 2 * k, 1 * k, new List<string>()))),
                ("chamfer top edges of holed box 1", HoleBox, (b, x, k) => Edges(b, null, (l, e) => FilletOp.ChamferEdges(l, e, 1 * k, 1 * k, new List<string>()))),
                ("round both cylinder rims r2", () => PrimitiveOp.Cylinder(Vector3d.Zero, 8, 15), (b, x, k) => Edges(b, e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3, (l, e) => FilletOp.RoundEdges(l, e, 2 * k, new List<string>()))),
                ("round cylinder rim r2", () => PrimitiveOp.Cylinder(Vector3d.Zero, 8, 15), (b, x, k) => Edges(b, null, (l, e) => FilletOp.RoundEdges(l, e, 2 * k, new List<string>()))),
                ("shell open top t2", () => Box(0, 0, 0, 30, 20, 10), (b, x, k) => { var l = new List<TSolid> { b }; return ShellOp.Hollow(l, 2 * k, new[] { x(new Vector3d(15, 10, 10)) }, new List<string>()) ? l : null; }),
                ("shell closed holed box t1.5", HoleBox, (b, x, k) => { var l = new List<TSolid> { b }; return ShellOp.Hollow(l, 1.5 * k, null, new List<string>()) ? l : null; }),
            };
            // «верхние» рёбра — выбраны на исходном теле по z = max; у образов — те же номера рёбер (порядок копии тот же)
            List<TSolid>? Edges(TSolid b, Func<TEdge, bool>? pick, Action<List<TSolid>, List<EdgeRef>> op)
            {
                // выбор — на исходном теле (по z = max или предикатом); у образа — ребро, чья середина — образ середины
                var es = b.Edges.ToList();
                var want = (pick != null ? es0!.Where(pick) : topIdx!.Select(i => es0![i])).ToList();
                var sel = new List<TEdge>();
                foreach (var e0 in want)
                {
                    var m = curX(Mid(e0)); var hit = es.Where(e => (Mid(e) - m).Length < 1e-7 * curK).ToList();
                    if (hit.Count != 1) throw new Exception($"edge at {Mid(e0)}: {hit.Count} images");
                    sel.Add(hit[0]);
                }
                var l = new List<TSolid> { b }; op(l, sel.Select(EdgeRef.Of).ToList()); return l;
            }
            var rigid = TransformOp.Rotate(new Vector3d(3, -2, 7), new Vector3d(0.3, -0.8, 0.52).Normalized(), 63);
            var move = TransformOp.Move(new Vector3d(-41, 17.5, 9));
            var mirror = TransformOp.Mirror(new Vector3d(1, 2, 0), new Vector3d(0.2, 1, -0.4).Normalized());
            const double kS = 2.5;
            foreach (var (name, mk, op) in cases)
            {
                try
                {
                    var b0 = mk();
                    es0 = b0.Edges.ToList(); double zmax = b0.Vertices.Max(v => v.P.Z);
                    topIdx = Enumerable.Range(0, es0.Count).Where(i => Math.Abs(es0[i].V0.P.Z - zmax) < 1e-9 && Math.Abs(es0[i].V1.P.Z - zmax) < 1e-9).ToList();
                    (double V, double E, string G)? Run(Func<Vector3d, Vector3d> x, TSolid src, double k)
                    {
                        curX = x; curK = k;
                        var r = op(src, x, k);
                        if (r == null || r.Count == 0) return null;
                        double v = 0, err = 0;
                        foreach (var body in r)
                        {
                            var val = SolidValidator.Validate(body);
                            if (!val.IsValid) throw new Exception($"invalid result: {val.Summary()}");
                            var mv = AnalyticMeasure.Volume(body); v += mv.Value; err += mv.Error;
                        }
                        return (v, err, string.Join("+", r.Select(q => q.Guarantee)));
                    }
                    (double V, double E, string G)? basis;
                    try { basis = Run(p => p, mk(), 1); } catch (Exception e) { fails.Add($"{name}/basis: {e.Message}"); continue; }
                    if (basis is not { } bv) { fails.Add($"{name}: the operation declined"); continue; }
                    if (bv.G != "ExactAnalytic") fails.Add($"{name}: {bv.G}, not exact");
                    Func<Vector3d, Vector3d> T = p => move.Point(rigid.Point(p)), Mr = p => mirror.Point(p), S = p => new Vector3d(1, 2, 3) + (p - new Vector3d(1, 2, 3)) * kS;
                    var images = new (string N, Func<Vector3d, Vector3d> X, Func<TSolid> B, double K)[]
                    {
                        ("rigid", T, () => TransformOp.Copy(TransformOp.Copy(mk(), rigid), move), 1),
                        ("mirror", Mr, () => TransformOp.Copy(mk(), mirror), 1),
                        ("scale", S, () => TopologyTransform.Scaled(mk(), new Vector3d(1, 2, 3), kS)!, kS),
                    };
                    var row = $"{name}: V {bv.V:G10}±{bv.E:G2} {bv.G}";
                    foreach (var (iname, X, B, K) in images)
                    {
                        (double V, double E, string G)? r;
                        try { r = Run(X, B(), K); } catch (Exception e) { fails.Add($"{name}/{iname}: {e.Message}"); continue; }
                        if (r is not { } rv) { fails.Add($"{name}/{iname}: the operation declined on the image"); continue; }
                        // тождество в пределах заявленных границ объёма обоих результатов (у подобия граница исходного — ×k³)
                        double k3 = K * K * K, want = bv.V * k3, tol = 1e-9 * Math.Abs(want) + rv.E + bv.E * k3;
                        row += $"; {iname} {rv.V:G10}±{rv.E:G2} {rv.G}";
                        // все случаи набора строятся точно (прямые построители); многогранный путь — регрессия
                        if (rv.G != "ExactAnalytic") fails.Add($"{name}/{iname}: {rv.G}, not exact");
                        if (!(Math.Abs(rv.V - want) <= tol)) fails.Add($"{name}/{iname}: V {rv.V:R} want {want:R}, |Δ| {Math.Abs(rv.V - want):G3} > bounds {tol:G3} ({rv.G} vs {bv.G})");
                    }
                    rows.Add(row);
                }
                catch (Exception e) { fails.Add($"{name}: {e.Message}"); }
            }
            // массивы: несоприкасающиеся копии — n объёмов; перекрывающиеся — объём объединения
            {
                double v1 = 30 * 20 * 10;
                var l = new List<TSolid> { Box(0, 0, 0, 30, 20, 10) };
                if (!TransformOp.LinearPattern(l, new Vector3d(40, 0, 0), 3, new List<string>())) fails.Add("linear pattern declined");
                else { double v = l.Sum(OpVolume); rows.Add($"linear ×3 disjoint: {v:G10}"); if (Math.Abs(v - 3 * v1) > 1e-7 * v1) fails.Add($"linear pattern ×3: V {v:R} want {3 * v1}"); }
                l = new List<TSolid> { HoleBox() }; double vh = OpVolume(l[0]);
                if (!TransformOp.CircularPattern(l, new Vector3d(15, 80, 0), Vector3d.UnitZ, 6, new List<string>())) fails.Add("circular pattern declined");
                else { double v = l.Sum(OpVolume); rows.Add($"circular ×6 disjoint: {v:G10}"); if (Math.Abs(v - 6 * vh) > 1e-7 * 6 * vh) fails.Add($"circular pattern ×6: V {v:R} want {6 * vh:R}"); }
                l = new List<TSolid> { Box(0, 0, 0, 30, 20, 10) };
                if (!TransformOp.LinearPattern(l, new Vector3d(10, 0, 0), 3, new List<string>())) fails.Add("overlapping pattern declined");
                else { double v = l.Sum(OpVolume); rows.Add($"linear ×3 overlapping: {v:G10} in {l.Count} bodies"); if (Math.Abs(v - 50 * 20 * 10) > 1e-7 * v1) fails.Add($"overlapping pattern ×3: V {v:R} want {50 * 20 * 10}"); }
            }
            foreach (var r in rows) Console.WriteLine("  " + r);
            Log(new { test = "KRN11-QUAL-METAMORPHIC-OPS", cases = cases.Count, fails = fails.Count });
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
static partial class Program
{
    static void QualOpsDebug()
    {
        foreach (var rot in new[] { false, true })
        {
            var l0 = new List<TSolid> { Box(0, 0, 0, 30, 20, 10) }; BooleanOp.Subtract(l0, PrimitiveOp.Cylinder(new Vector3d(15, 10, -5), 4, 20), new List<string>());
            TSolid b = l0[0];
            var x = TransformOp.Rotate(new Vector3d(3, -2, 7), new Vector3d(0.3, -0.8, 0.52).Normalized(), 63);
            var top0 = b.Edges.Where(e => Math.Abs(e.V0.P.Z - 10) < 1e-9 && Math.Abs(e.V1.P.Z - 10) < 1e-9).Select(e => e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2)).ToList();
            if (rot) b = TransformOp.Copy(b, x);
            var top = b.Edges.Where(e => top0.Any(m => ((rot ? x.Point(m) : m) - e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2)).Length < 1e-7)).ToList();
            Console.WriteLine($"rot {rot}: {top.Count} edges: {string.Join(",", top.Select(e => e.Curve.GetType().Name))}");
            Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (!m.StartsWith("anchor") && !m.StartsWith("face ") && !m.StartsWith("same-sense")) Console.WriteLine("   dr: " + m[..Math.Min(300, m.Length)]); };
            var l = new List<TSolid> { b }; var w = new List<string>();
            try { FilletOp.RoundEdges(l, top.Select(EdgeRef.Of).ToList(), 1.5, w); Console.WriteLine($"  ok {l[0].Guarantee} {OpVolume(l[0])}"); }
            catch (Exception e) { Console.WriteLine("  EX " + e.Message[..Math.Min(200, e.Message.Length)]); }
            Parametric3D.Kernel.Ops.DirectRound.Debug = null;
            foreach (var s in w) Console.WriteLine("  w: " + s);
        }
    }
}
