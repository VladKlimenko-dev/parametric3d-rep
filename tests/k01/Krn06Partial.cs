using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Ребро пирамиды PyramidBody(n, slope2) от вершины (20, 20, 40) к основанию между гранями q и q + 1.</summary>
    static TEdge? PyramidEdge(TSolid src, int n, double slope2, int q)
    {
        Vector3d N(int k) { double sl = k % n == 1 ? slope2 : 0.5, th = 2 * Math.PI * (k % n) / n; return new Vector3d(Math.Cos(th), Math.Sin(th), sl); }
        var A = new Vector3d(20, 20, 40); var na = N(q); var nb = N(q + 1);
        double ra = Vector3d.Dot(na, A), rb = Vector3d.Dot(nb, A), det = na.X * nb.Y - na.Y * nb.X;
        var low = new Vector3d((ra * nb.Y - rb * na.Y) / det, (na.X * rb - nb.X * ra) / det, 0);
        var es = src.Edges.Where(e => (e.V0.P - A).Length < 1e-6 && (e.V1.P - low).Length < 1e-6 || (e.V1.P - A).Length < 1e-6 && (e.V0.P - low).Length < 1e-6).ToList();
        return es.Count == 1 ? es[0] : null;
    }

    static void Krn06PartialVertexChecks()
    {
        Check("KRN06-PARTIAL-VERTEX-SELECTION", () =>
        {
            // 06.1/06.9: В ВЕРШИНЕ СТЕПЕНИ n > 3 ВЫБРАНА ЧАСТЬ РЁБЕР (2 … n − 2): снятое — объединение уголков рёбер (не зависит от
            // порядка). Эталон — независимый расчёт tools/ref_apex_two_edges.py: V₁ + V₂ − V₁₂ (уголки ∩ тело, пересечение уголков —
            // по прямым вдоль ребра₁: дельтоид₂ — отрезок, круг₂ — квадратное уравнение; адаптивная квадратура 1e-13). Три ребра —
            // порядок рёбер не меняет тело (объём до 1e-10). ExactAnalytic, годность, точный STEP.
            var fails = new List<string>();
            foreach (var (n, slope2, r, qs, want) in new (int, double, double, int[], double)[]
            {
                (4, 0.5, 2.0, new[] { 0, 1 }, 50.40979861869977),
                (4, 0.5, 2.0, new[] { 0, 2 }, 50.64205664478534),
                (4, 0.7, 2.0, new[] { 0, 1 }, 46.89584343138262),
                (5, 0.5, 2.0, new[] { 0, 2 }, 23.873090199669612),
                (6, 0.7, 1.5, new[] { 0, 1 }, 7.15694324320623),
                (6, 0.7, 1.5, new[] { 0, 2, 4 }, 11.079463147719038),
                (4, 0.5, 2.0, new[] { 0, 1, 2 }, 75.35718017356803),
            })
            {
                string name = $"pyramid {n} slope {slope2} r {r} edges {string.Join(",", qs.Select(q => $"{q}/{q + 1}"))}";
                var src = PyramidBody(n, slope2); double v0 = AnalyticVolume.Of(src) ?? double.NaN;
                var es = qs.Select(q => PyramidEdge(src, n, slope2, q)).ToList();
                if (es.Any(e => e == null)) { fails.Add($"{name}: an edge is not found"); continue; }
                double dvFirst = double.NaN;
                foreach (bool reversed in new[] { false, true })
                {
                    var refs = es.Select(e => EdgeRef.Of(e!)).ToList(); if (reversed) refs.Reverse();
                    var bl = new List<TSolid> { TopologyClone.Deep(src) }; var w = new List<string>(); long p0 = DirectRound.PartialVertexBuilt;
                    if (Environment.GetEnvironmentVariable("P3D_PVDBG") == "1") DirectRound.Debug = m => { if (m.StartsWith("declined") || m.Contains("partial ∩")) Console.WriteLine("   dr: " + m); };
                    try { FilletOp.RoundEdges(bl, refs, r, w); }
                    catch (Exception ex) { fails.Add($"{name}{(reversed ? " (reversed)" : "")}: {ex.GetType().Name} {ex.Message}"); continue; }
                    var b = bl[0]; double dv = v0 - (AnalyticVolume.Of(b) ?? double.NaN);
                    Console.WriteLine($"  {name}{(reversed ? " (reversed order)" : "")}: removed {dv:R}{(double.IsNaN(want) ? "" : $" reference {want:R} ({(dv - want) / want:E1})")} {b.Guarantee}, {b.Faces.Count()} faces | {string.Join(" | ", w.Take(1))}");
                    if (DirectRound.PartialVertexBuilt == p0) fails.Add($"{name}: not the staged partial-vertex builder ({string.Join(" | ", w.Take(2))})");
                    // рёбра-щепки (вершины-двойники от неточного касательного корня): OCCT читает такое тело незамкнутым
                    var sliver = b.Edges.FirstOrDefault(e => e.V0 != e.V1 && (e.V1.P - e.V0.P).Length < 1e-5);
                    if (sliver != null) fails.Add($"{name}{(reversed ? " (reversed)" : "")}: sliver edge {sliver.Curve?.GetType().Name} {(sliver.V1.P - sliver.V0.P).Length:G3} mm at {sliver.V0.P:0.#####}");
                    if (b.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b).IsValid) fails.Add($"{name}: {b.Guarantee} {SolidValidator.Validate(b).Summary()}");
                    if (!double.IsNaN(want) && !(Math.Abs(dv - want) <= 1e-9 * want)) fails.Add($"{name}: removed {dv:R} vs reference {want:R}");
                    if (reversed && !(Math.Abs(dv - dvFirst) <= 1e-10 * Math.Abs(dvFirst))) fails.Add($"{name}: the order of edges changes the result ({dv:R} vs {dvFirst:R})");
                    if (!reversed)
                    {
                        dvFirst = dv;
                        var sw = new StringWriter(); var ws = new List<string>();
                        if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "pv", ws) || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Where(x => x.Contains("not")).Take(2))})");
                    }
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
