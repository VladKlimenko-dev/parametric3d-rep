using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using OpenTK.Mathematics;

static partial class Program
{
    /// <summary>k150 (реестр п. 2): разбор смешанной фаски файла пользователя — этапами: вогнутые рёбра, затем выпуклые
    /// с новыми рёбрами ленты у смешанной вершины.</summary>
    static void Mixed150Probe(string file)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        var ch = (ChamferFeature)p.Features[^1];
        p.RemoveFeature(ch); p.Rebuild(pars);
        var body = p.Bodies[0]; var one = new List<TSolid> { body };
        double d = double.Parse(Environment.GetEnvironmentVariable("PROBE_D") ?? "9.72", System.Globalization.CultureInfo.InvariantCulture);
        var pdbg = Environment.GetEnvironmentVariable("PROBE_DBG");
        DirectRound.Debug = s => { if (s.StartsWith("declined") || (pdbg != null && s.Contains(pdbg))) Console.WriteLine("DBG " + (s.Length > 400 ? s[..400] : s)); };
        // k150: окрестность вершины (PROBE_V="x,y,z"): рёбра из неё — грани, концы, касательная в вершине
        if (Environment.GetEnvironmentVariable("PROBE_V") is { } pv)
        {
            var c3 = pv.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); var V = new Vector3d(c3[0], c3[1], c3[2]);
            foreach (var e in body.Edges.Where(e => (e.V0.P - V).Length < 0.05 || (e.V1.P - V).Length < 0.05))
            {
                var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                var c = e.Curve; bool at0 = (c.PointAt(c.T0) - V).Length < (c.PointAt(c.T1) - V).Length;
                var tg = c.TangentAt(at0 ? c.T0 : c.T1); tg.Normalize(); if (!at0) tg = -tg;
                Log(new { vEdge = $"e{e.Id} {e.V0.P:0.###}->{e.V1.P:0.###} {c.GetType().Name}", faces = string.Join(" / ", fs.Select(f => $"{f.Tag} {f.Surface.GetType().Name}")), outDir = tg.ToString("0.###"), mid = c.PointAt((c.T0 + c.T1) / 2).ToString("0.###") });
            }
        }
        foreach (var rf in ch.Edges)
        {
            var e = FilletOp.Resolve(one, rf, out _);
            var bl = new List<TSolid> { TransformOp.Copy(body, TransformOp.Move(Vector3d.Zero)) };
            bool ok = DirectRound.TryChamfer(bl, new[] { rf }, d, d, new List<string>());
            Log(new { edge = e == null ? "unresolved" : $"{e.V0.P:0.##}->{e.V1.P:0.##} {e.Curve.GetType().Name}", single = ok, g = ok ? bl[0].Guarantee.ToString() : null });
        }
        // этапы: вогнутые (по заданию — список индексов из PROBE_CONC, по умолчанию 1,2)
        var conc = (Environment.GetEnvironmentVariable("PROBE_CONC") ?? "1,2").Split(',').Select(int.Parse).ToList();
        var b1 = new List<TSolid> { TransformOp.Copy(body, TransformOp.Move(Vector3d.Zero)) }; var w1 = new List<string>();
        bool ok1 = DirectRound.TryChamfer(b1, conc.Select(i => ch.Edges[i]).ToList(), d, d, w1);
        Log(new { stage = "concave", ok = ok1, g = ok1 ? b1[0].Guarantee.ToString() : null, valid = ok1 ? SolidValidator.Validate(b1[0]).IsValid : (bool?)null, w = string.Join(" | ", w1) });
        if (!ok1) return;
        var conv = Enumerable.Range(0, ch.Edges.Count).Where(i => !conc.Contains(i)).Select(i => ch.Edges[i]).ToList();
        foreach (var e in b1[0].Edges)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Any(f => f.Tag == "chamfer:face")) Log(new { newEdge = $"{e.V0.P:0.##}->{e.V1.P:0.##}", faces = string.Join("/", fs.Select(f => f.Tag)) });
        }
        var b2 = new List<TSolid> { TransformOp.Copy(b1[0], TransformOp.Move(Vector3d.Zero)) }; var w2 = new List<string>();
        bool ok2 = DirectRound.TryChamfer(b2, conv, d, d, w2);
        Log(new { stage = "convex", ok = ok2, g = ok2 ? b2[0].Guarantee.ToString() : null, valid = ok2 ? SolidValidator.Validate(b2[0]).IsValid : (bool?)null, w = string.Join(" | ", w2) });
    }
}

static partial class Program
{
    /// <summary>k150: Sweep_Blend пользователя (03.11, 17:00) — фаска 9.72 по трём рёбрам окна после скругления R6.61:
    /// выпуклая кромка торца, вогнутые «стенка ∩ скругление» и угол окна; у вершины кромки и скругления — острие (рёбра
    /// уходят в одну сторону). Норма: (1) ребро «стенка ∩ скругление» (угол граней всюду ~90°) — вогнутое по всей длине,
    /// прямой путь не отвечает «convexity changes along the edge» (мутация P3D_DRCONVEPS=0); (2) файл перестраивается, тело
    /// годно (прямой путь острие пока не строит — резец; реестр п. 2/5).</summary>
    static void SweepBlendC972Checks()
    {
        Check("KRN06-USER-SWEEPBLEND-C972-K150", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            string text = File.ReadAllText(Path.Combine(dir, "user_k150_sweep_blend_c972.p3d"));
            var (p, pars) = DocumentFile.Load(text);
            var ch = (ChamferFeature)p.Features[^1];
            p.RemoveFeature(ch); p.Rebuild(pars);
            var body = p.Bodies[0];
            var bl = new List<TSolid> { TransformOp.Copy(body, TransformOp.Move(Vector3d.Zero)) };
            DirectRound.LastDeclined = null;
            bool ok = DirectRound.TryChamfer(bl, new[] { ch.Edges[1] }, 9.72, 9.72, new List<string>());
            string why = DirectRound.LastDeclined ?? "";
            var (p2, pars2) = DocumentFile.Load(text); p2.Rebuild(pars2);
            var f = p2.Features[^1];
            bool valid = p2.Bodies.Count == 1 && SolidValidator.Validate(p2.Bodies[0]).IsValid;
            Log(new { test = "KRN06-USER-SWEEPBLEND-C972-K150", direct = ok, why = why.Length > 300 ? why[^300..] : why, error = f.Error, valid, g = p2.Bodies.FirstOrDefault()?.Guarantee.ToString(), volume = p2.Volume });
            Require(!why.Contains("convexity changes along the edge"), "the concave edge 'wall ∩ round' is voted mixed: " + why);
            Require(f.Error == null && valid, $"rebuild: error {f.Error}, valid {valid}");
        });
    }
}

static partial class Program
{
    /// <summary>k150: разбор — брусок с карманом, фаска / скругление кромки кармана (конец ленты у вогнутого угла).</summary>
    static void PocketRimProbe()
    {
        DirectRound.Debug = s => { if (s.StartsWith("declined") || s.StartsWith("build declined") || s.Contains("cusp") || s.Contains("invalid")) Console.WriteLine("DBG " + (s.Length > 500 ? s[..500] : s)); };
        foreach (bool round in new[] { false, true })
            foreach (bool preRound in new[] { false, true })
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                BooleanOp.Subtract(bl, Box(20, 10, 5, 20, 20, 30), new List<string>());
                if (preRound)
                {
                    // кромка кармана y = 30 (стенка «дальняя») — скругление R3, как R6.61 у Sweep_Blend
                    var re = bl[0].Edges.First(e => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9 && Math.Abs(e.V0.P.Y - 30) < 1e-9 && Math.Abs(e.V1.P.Y - 30) < 1e-9);
                    var w0 = new List<string>(); FilletOp.RoundEdges(bl, new[] { EdgeRef.Of(re) }, 3, w0);
                    Log(new { preRound = string.Join(" | ", w0.Take(2)), g = bl[0].Guarantee.ToString() });
                }
                // кромка кармана x = 20 (стенка «левая»), z = 20
                var e1 = bl[0].Edges.First(e => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9 && Math.Abs(e.V0.P.X - 20) < 1e-9 && Math.Abs(e.V1.P.X - 20) < 1e-9);
                var w = new List<string>(); double v0 = AnalyticVolume.Of(bl[0]) ?? double.NaN;
                bool ok = round ? DirectRound.TryRound(bl, new[] { EdgeRef.Of(e1) }, 2, w) : DirectRound.TryChamfer(bl, new[] { EdgeRef.Of(e1) }, 2, 2, w);
                if (ok && preRound)
                    foreach (var fc in bl[0].Faces.Where(f => f.Tag.Contains("chamfer") || f.Tag.Contains("round")))
                        Log(new { face = fc.Tag, surf = fc.Surface.GetType().Name, loops = string.Join(" | ", fc.Loops.Select(lp => string.Join(", ", lp.CoEdges.Select(ce => $"{ce.Edge.Curve.GetType().Name} {ce.Start.P:0.###}->{ce.End.P:0.###}")))) });
                if (ok && preRound)
                    foreach (var ed in bl[0].Edges.Where(x => x.Curve is Parametric3D.Kernel.Curves.ProcCurve3 && (x.V0.P - new Vector3d(18, 33, 20)).Length + (x.V1.P - new Vector3d(18, 33, 20)).Length < 6 && Math.Min((x.V0.P - new Vector3d(18, 33, 20)).Length, (x.V1.P - new Vector3d(18, 33, 20)).Length) < 1e-6 && Math.Abs(x.V0.P.Y - x.V1.P.Y) > 1 && Math.Abs(x.V0.P.Y - x.V1.P.Y) < 4))
                    {
                        var c = ed.Curve; double dc = 0, dp = 0;
                        for (int i = 0; i <= 200; i++) { var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 200.0); dc = Math.Max(dc, Math.Abs(Math.Sqrt((q.Y - 33) * (q.Y - 33) + (q.Z - 17) * (q.Z - 17)) - 3)); dp = Math.Max(dp, round ? double.NaN : Math.Abs(q.X + q.Z - 38) / Math.Sqrt(2)); }
                        Log(new { endCurve = $"{ed.V0.P:0.###}->{ed.V1.P:0.###}", offCylinder = dc, offPlane = dp });
                    }
                if (ok) Log(new { mesh = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bl[0])), exact = AnalyticVolume.Of(bl[0]) });
                Log(new { op = round ? "R2" : "C2", preRound, edge = $"{e1.V0.P}->{e1.V1.P}", ok, g = ok ? bl[0].Guarantee.ToString() : null, valid = ok ? SolidValidator.Validate(bl[0]).IsValid : (bool?)null, removed = ok ? v0 - (AnalyticVolume.Of(bl[0]) ?? double.NaN) : (double?)null, w = string.Join(" | ", w.Take(2)) });
            }
    }
}

static partial class Program
{
    /// <summary>k151: конец ленты у острия — брусок 60×40×20 с карманом 20×20×15, кромка кармана y = 30 скруглена R3; фаска 2
    /// и скругление R2 по кромке x = 20. Скругление R3 касается верха вдоль шва y = 33, ребро кромки торец только касается,
    /// лента его пересекает. Эталон — интеграл: за y = 30 снимается сечение ленты до цилиндра R3
    /// (y_c − 30 = 3 − √(6b − b²), b = 20 − z): фаска 40 + ∫₀²(2 − b)(3 − √(6b − b²))db = 42.582631483004,
    /// скругление (1 − π/4)·80 + ∫₀²(2 − √(4 − (b − 2)²))(3 − √(6b − b²))db = 18.543626679417. Мутация P3D_DRCUSPEND=0.</summary>
    static void CuspEndChecks()
    {
        Check("KRN06-CUSP-END-K151", () =>
        {
            var fails = new List<string>();
            foreach (bool round in new[] { false, true })
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                BooleanOp.Subtract(bl, Box(20, 10, 5, 20, 20, 30), new List<string>());
                var re = bl[0].Edges.First(e => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9 && Math.Abs(e.V0.P.Y - 30) < 1e-9 && Math.Abs(e.V1.P.Y - 30) < 1e-9);
                FilletOp.RoundEdges(bl, new[] { EdgeRef.Of(re) }, 3, new List<string>());
                double v0 = AnalyticVolume.Of(bl[0]) ?? double.NaN;
                var e1 = bl[0].Edges.First(e => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9 && Math.Abs(e.V0.P.X - 20) < 1e-9 && Math.Abs(e.V1.P.X - 20) < 1e-9);
                var w = new List<string>(); long c0 = DirectRound.CuspEnds;
                bool ok = round ? DirectRound.TryRound(bl, new[] { EdgeRef.Of(e1) }, 2, w) : DirectRound.TryChamfer(bl, new[] { EdgeRef.Of(e1) }, 2, 2, w);
                double want = round ? 18.543626679417 : 42.582631483004, tol = round ? 1e-6 : 1e-3;
                double removed = ok ? v0 - (AnalyticVolume.Of(bl[0]) ?? double.NaN) : double.NaN;
                Log(new { test = "KRN06-CUSP-END-K151", op = round ? "R2" : "C2", ok, removed = ok ? removed : (double?)null, want, cusp = DirectRound.CuspEnds - c0 });
                if (!ok) { fails.Add($"{(round ? "R2" : "C2")}: not direct ({DirectRound.LastDeclined})"); continue; }
                if (bl[0].Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(bl[0]).IsValid) fails.Add($"{(round ? "R2" : "C2")}: {bl[0].Guarantee} {SolidValidator.Validate(bl[0]).Summary()}");
                if (!(Math.Abs(removed - want) <= tol)) fails.Add($"{(round ? "R2" : "C2")}: removed {removed:R}, integral {want:R}");
                if (DirectRound.CuspEnds == c0) fails.Add($"{(round ? "R2" : "C2")}: no cusp end");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
