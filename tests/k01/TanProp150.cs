using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using OpenTK.Mathematics;

static partial class Program
{
    /// <summary>k150 (реестр п. 5): разбор — скругление кромки торца лофта, затем ребро «лента ∩ соседняя стенка» (у концов —
    /// гладкий переход в следующее ребро). Печать: прямой путь, распространение, гарантия, годность.</summary>
    static void TanProp150Probe(int s0, int n)
    {
        for (int sd = s0; sd < s0 + n; sd++)
        {
            CorpusModel m;
            try { m = CorpusMake(sd); m.Part.Rebuild(null); } catch { continue; }
            if (m.Part.Bodies.Count != 1) continue;
            var b0 = m.Part.Bodies[0];
            var cap = b0.Edges.Where(e => Math.Abs(e.V0.P.Z) < 1e-9 && Math.Abs(e.V1.P.Z) < 1e-9 && e.V0 != e.V1).Take(2).ToList();
            foreach (var ce in cap)
            {
                var bl = new List<TSolid> { TransformOp.Copy(b0, TransformOp.Move(Vector3d.Zero)) };
                var e0 = bl[0].Edges.FirstOrDefault(e => (e.V0.P - ce.V0.P).Length < 1e-9 && (e.V1.P - ce.V1.P).Length < 1e-9);
                if (e0 == null) continue;
                var w = new List<string>();
                if (!DirectRound.TryRound(bl, new[] { EdgeRef.Of(e0) }, 3, w) || bl[0].Guarantee != GuaranteeLevel.ExactAnalytic) { Log(new { sd, step0 = "not direct" }); continue; }
                var bodyR = bl[0];
                var ends = bodyR.Edges.Where(e => { var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().ToList(); return fs.Any(f => f.Tag == "round:face") && fs.Any(f => f.Tag != "round:face" && !(f.Surface is Parametric3D.Kernel.Surfaces.PlaneSurface)); }).ToList();
                foreach (var ee in ends)
                    foreach (var chamfer in new[] { false, true })
                    {
                        var b2 = new List<TSolid> { TransformOp.Copy(bodyR, TransformOp.Move(Vector3d.Zero)) };
                        var e2 = b2[0].Edges.FirstOrDefault(e => (e.V0.P - ee.V0.P).Length < 1e-9 && (e.V1.P - ee.V1.P).Length < 1e-9);
                        if (e2 == null) continue;
                        long p0 = DirectRound.OpenTanPropagations; var w2 = new List<string>();
                        DirectRound.LastDeclined = null;
                        bool ok = chamfer ? DirectRound.TryChamfer(b2, new[] { EdgeRef.Of(e2) }, 0.8, 0.8, w2) : DirectRound.TryRound(b2, new[] { EdgeRef.Of(e2) }, 0.8, w2);
                        var rep = ok ? SolidValidator.Validate(b2[0]) : null;
                        Log(new { sd, edge = $"{ee.V0.P:0.##}->{ee.V1.P:0.##}", op = chamfer ? "C0.8" : "R0.8", ok, prop = DirectRound.OpenTanPropagations - p0, g = ok ? b2[0].Guarantee.ToString() : null, valid = rep?.IsValid, why = ok ? null : (DirectRound.LastDeclined is { } ld ? ld[Math.Max(0, ld.Length - 220)..] : null) });
                    }
            }
        }
    }
}

static partial class Program
{
    /// <summary>k150: тело корпуса (зерно sd) со скруглением R3 кромки торца, начинающейся в cap0, и ребро «лента ∩ стенка»,
    /// начинающееся в e0.</summary>
    static (TSolid Body, TEdge Edge)? TanProp150Case(int sd, Vector3d e0)
    {
        var m = CorpusMake(sd); m.Part.Rebuild(null);
        var b0 = m.Part.Bodies[0];
        foreach (var ce in b0.Edges.Where(e => Math.Abs(e.V0.P.Z) < 1e-9 && Math.Abs(e.V1.P.Z) < 1e-9 && e.V0 != e.V1).Take(2))
        {
            var bl = new List<TSolid> { TransformOp.Copy(b0, TransformOp.Move(Vector3d.Zero)) };
            var c = bl[0].Edges.First(e => (e.V0.P - ce.V0.P).Length < 1e-9 && (e.V1.P - ce.V1.P).Length < 1e-9);
            if (!DirectRound.TryRound(bl, new[] { EdgeRef.Of(c) }, 3, new List<string>())) continue;
            var x = bl[0].Edges.FirstOrDefault(e => (e.V0.P - e0).Length < 0.01);
            if (x != null) return (bl[0], x);
        }
        return null;
    }

    static void TanProp150Checks()
    {
        Check("KRN06-OPEN-TANPROP-K150", () =>
        {
            // k150 (реестр п. 5): ребро «лента скругления ∩ стенка лофта» у концов гладко переходит в следующие рёбра (торец
            // касается грани ребра) — лента торец не пересекает. Выбор дополняется по касательным (как OCCT/Parasolid), путь —
            // прямой, тело точное. Эталон — резец по той же дополненной цепочке (сетка против сетки, DirectRound.OffForTest).
            // Мутация P3D_DROPENTANPROP=0 — прямой путь отказывает.
            var fails = new List<string>();
            static double MeshV(TSolid t) => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(t));
            var cases = new (int Sd, Vector3d E0)[] { (102, new(-31.57, -12.06, -3.43)), (111, new(15.96, 30.31, -2.91)), (111, new(15.87, 30.3, -2.49)) };
            foreach (var (sd, e0) in cases)
                foreach (bool ch in new[] { false, true })
                {
                    string name = $"s{sd} {(ch ? "C0.8" : "R0.8")} {e0:0.##}";
                    if (TanProp150Case(sd, e0) is not { } cs) { fails.Add(name + ": case not built"); continue; }
                    var refs = new[] { EdgeRef.Of(cs.Edge) };
                    var more = DirectRound.TangentClosure(cs.Body, refs);
                    if (more == null || more.Count <= 1) { fails.Add(name + ": no tangent continuation"); continue; }
                    double v0 = MeshV(cs.Body);
                    var bd = new List<TSolid> { TransformOp.Copy(cs.Body, TransformOp.Move(Vector3d.Zero)) }; var w = new List<string>();
                    long p0 = DirectRound.OpenTanPropagations;
                    bool ok = ch ? DirectRound.TryChamfer(bd, refs, 0.8, 0.8, w) : DirectRound.TryRound(bd, refs, 0.8, w);
                    double? va = ok ? AnalyticVolume.Of(bd[0]) : null; double vd = ok ? MeshV(bd[0]) : double.NaN;
                    var bc = new List<TSolid> { TransformOp.Copy(cs.Body, TransformOp.Move(Vector3d.Zero)) };
                    DirectRound.OffForTest = true;
                    try { if (ch) FilletOp.ChamferEdges(bc, more, 0.8, 0.8, new List<string>()); else FilletOp.RoundEdges(bc, more, 0.8, new List<string>()); }
                    finally { DirectRound.OffForTest = false; }
                    double vc = MeshV(bc[0]);
                    double dd = (AnalyticVolume.Of(cs.Body) ?? double.NaN) - (va ?? double.NaN), dc = v0 - vc;
                    double fs = SectionsFormula150(cs.Body, more, 0.8, ch);
                    Log(new { test = "KRN06-OPEN-TANPROP-K150", name, ok, added = more.Count - 1, g = ok ? bd[0].Guarantee.ToString() : null, removedDirect = double.IsFinite(dd) ? dd : (double?)null, removedCutter = double.IsFinite(dc) ? dc : (double?)null, formula = double.IsFinite(fs) ? fs : (double?)null, exact = va });
                    if (!ok || DirectRound.OpenTanPropagations == p0) { fails.Add($"{name}: not direct ({DirectRound.LastDeclined})"); continue; }
                    if (bd[0].Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(bd[0]).IsValid) fails.Add($"{name}: {bd[0].Guarantee} {SolidValidator.Validate(bd[0]).Summary()}");
                    // формула сечений по рёбрам цепочки — без концов (углы у острых стыков: O(r³))
                    // резец по той же цепочке (сетка против сетки): 3 %; у фаски ещё формула сечений — 1 % + 2r³ (у скругления формула
                    // без сбега ленты у острых концов цепочки завышает, только в лог)
                    if (!(Math.Abs(dd - dc) <= 0.03 * Math.Abs(dc))) fails.Add($"{name}: removed {dd:G6} vs cutter {dc:G6}");
                    if (ch && !(Math.Abs(dd - fs) <= 2 * 0.8 * 0.8 * 0.8 + 0.01 * fs)) fails.Add($"{name}: removed {dd:G6} vs sections formula {fs:G6}");
                }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>k150: формула сечений — интеграл площади сечения ленты вдоль рёбер цепочки (угол граней α по нормалям):
    /// скругление r²(ctg(α/2) − (π − α)/2), фаска равных расстояний d²·sin α / 2. Концы цепочки не учитываются.</summary>
    static double SectionsFormula150(TSolid body, IReadOnlyList<EdgeRef> refs, double r, bool chamfer)
    {
        var one = new List<TSolid> { body }; double sum = 0;
        foreach (var rf in refs)
        {
            var e = FilletOp.Resolve(one, rf, out _); if (e == null) return double.NaN;
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList(); if (fs.Count != 2) return double.NaN;
            Vector3d N(TFace f, Vector3d p) { var su = GeometryAudit.AuthoritativeSurface(f); return (su.NormalAt(su.ProjectToUV(p)) * (f.Reversed ? -1 : 1)).Normalized(); }
            double A(Vector3d p)
            {
                double th = Math.Acos(Math.Clamp(Vector3d.Dot(N(fs[0], p), N(fs[1], p)), -1, 1)), al = Math.PI - th;
                return chamfer ? 0.5 * r * r * Math.Sin(al) : r * r * (1 / Math.Tan(al / 2) - th / 2);
            }
            const int n = 200; var c = e.Curve; var prev = c.PointAt(c.T0); double aPrev = A(prev);
            for (int i = 1; i <= n; i++)
            {
                var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / n); double aq = A(q);
                sum += 0.5 * (aPrev + aq) * (q - prev).Length; prev = q; aPrev = aq;
            }
        }
        return sum;
    }
}
