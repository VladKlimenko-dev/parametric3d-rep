using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>Деталь квалификации истории — только сохраняемые операции (бобышки — выдавливание эскиза, высота 3·SphR).</summary>
    static Part BuildQual(HistParams q)
    {
        var p = new Part("qual"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
        var round = new RoundFeature("R1") { Radius = new ValueRef(q.RoundR) }; round.Edges.Add(EdgeRef.Of(CornerEdge(p, 50, 50)));
        p.AddFeature(round); round.Suppressed = q.RoundOff;
        var ch = new ChamferFeature { Name = "C1", D1 = new ValueRef(q.ChamferD), D2 = new ValueRef(q.ChamferD) }; ch.Edges.Add(EdgeRef.Of(CornerEdge(p, -50, 50)));
        p.AddFeature(ch);
        for (int k = 0; k < 12; k++)
        {
            var h = new HoleFeature($"H{k + 1}") { Direction = -OpenTK.Mathematics.Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(k < 6 ? q.D1 : 5) };
            h.Points.Add(new OpenTK.Mathematics.Vector3d(-25 + (k % 4) * 15, -30 + (k / 4) * 15, 50));
            p.AddFeature(h);
            if (k == 6) h.Suppressed = q.Hole7Off;
        }
        for (int k = 0; k < 3; k++)
        {
            var sk = CircleSketch(p, 50, -25 + 20 * k, 32, 5);
            var ex = new ExtrudeFeature($"B{k + 1}") { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(3 * q.SphR); p.AddFeature(ex);
        }
        p.AddFeature(new LinearPatternFeature("P1") { Step = new OpenTK.Mathematics.Vector3d(250, 0, 0), Count = new ValueRef(q.Copies) });
        p.Rebuild(null);
        return p;
    }

    static void ApplyHist(Part p, HistParams o, HistParams q)
    {
        if (q.D1 != o.D1) { foreach (var h in p.Features.OfType<HoleFeature>().Take(6)) h.Diameter = new ValueRef(q.D1); p.MarkDirtyFrom(p.Features.OfType<HoleFeature>().First()); }
        if (q.RoundR != o.RoundR) { var r = p.Features.OfType<RoundFeature>().Single(); r.Radius = new ValueRef(q.RoundR); p.MarkDirtyFrom(r); }
        if (q.RoundOff != o.RoundOff) { var r = p.Features.OfType<RoundFeature>().Single(); r.Suppressed = q.RoundOff; p.MarkDirtyFrom(r); }
        if (q.SphR != o.SphR) { foreach (var e in p.Features.OfType<ExtrudeFeature>().Where(x => x.Name.StartsWith("B"))) e.Side1.Distance = new ValueRef(3 * q.SphR); p.MarkDirtyFrom(p.Features.OfType<ExtrudeFeature>().First(x => x.Name.StartsWith("B"))); }
        if (q.Copies != o.Copies) { var pf = p.Features.OfType<LinearPatternFeature>().Single(); pf.Count = new ValueRef(q.Copies); p.MarkDirtyFrom(pf); }
        if (q.Hole7Off != o.Hole7Off) { var h = p.Features.OfType<HoleFeature>().ElementAt(6); h.Suppressed = q.Hole7Off; p.MarkDirtyFrom(h); }
        if (q.ChamferD != o.ChamferD) { var c = p.Features.OfType<ChamferFeature>().Single(); c.D1 = new ValueRef(q.ChamferD); c.D2 = new ValueRef(q.ChamferD); p.MarkDirtyFrom(c); }
    }

    static void Krn11HistoryChecks()
    {
        Check("KRN11-HISTORY-QUALIFICATION", () =>
        {
            // 11.4: КВАЛИФИКАЦИЯ ИСТОРИИ — случайная последовательность правок детали из 25 операций (отверстия, скругление,
            // фаска, сферы, бобышки, массив): параметр, подавление/возврат; вперемежку — сохранение и загрузка документа,
            // отмена и повтор, отмена с новой правкой. После каждого шага геометрия (побитно) и документ — как у сборки с нуля
            // при тех же параметрах; тела годны. Два зерна по 16 шагов.
            var fails = new List<string>();
            var fresh = new Dictionary<HistParams, string>();
            string Fresh(HistParams q) { if (!fresh.TryGetValue(q, out var g)) fresh[q] = g = PartGeometry(BuildQual(q)); return g; }
            foreach (int seed in new[] { 11, 12 })
            {
                var rnd = new Random(seed);
                var q = new HistParams(6, 5, false, 4, 3, false, 3);
                var p = BuildQual(q); var hist = new UndoHistory(); var qs = new Stack<HistParams>(); var redo = new Stack<HistParams>();
                HistParams Next(HistParams x) => rnd.Next(7) switch
                {
                    0 => x with { D1 = new[] { 5.5, 6, 7, 8 }[rnd.Next(4)] },
                    1 => x with { RoundR = new[] { 2.0, 3, 4, 5 }[rnd.Next(4)] },
                    2 => x with { RoundOff = !x.RoundOff },
                    3 => x with { SphR = new[] { 2.5, 3, 4 }[rnd.Next(3)] },
                    4 => x with { Copies = x.Copies == 2 ? 3 : 2 },
                    5 => x with { Hole7Off = !x.Hole7Off },
                    _ => x with { ChamferD = x.ChamferD == 2 ? 3 : 2 },
                };
                for (int step = 0; step < 16; step++)
                {
                    string act;
                    int kind = rnd.Next(10);
                    if (kind < 6 || qs.Count == 0)
                    {
                        var q2 = Next(q); act = $"edit {q2}";
                        hist.Record(p); qs.Push(q); redo.Clear();
                        ApplyHist(p, q, q2); p.Rebuild(null); q = q2;
                    }
                    else if (kind < 8)
                    {
                        act = "save/load";
                        var (re, _) = DocumentFile.Load(DocumentFile.Save(p)); re.Rebuild(null); p = re;
                    }
                    else if (kind == 8)
                    {
                        act = "undo";
                        p = hist.Undo(p)!.Value.Part; redo.Push(q); q = qs.Pop();
                    }
                    else
                    {
                        act = "undo+redo";
                        p = hist.Undo(p)!.Value.Part; var back = q; q = qs.Pop();
                        p = hist.Redo(p)!.Value.Part; qs.Push(q); q = back;
                    }
                    var errs = p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}").ToList();
                    if (errs.Count > 0) fails.Add($"seed {seed} step {step} ({act}): {string.Join("; ", errs.Take(2))}");
                    else if (PartGeometry(p) != Fresh(q)) fails.Add($"seed {seed} step {step} ({act}): geometry differs from a fresh build of {q}");
                    foreach (var b in p.Bodies) if (!SolidValidator.ValidateBRep(b).IsValid) { fails.Add($"seed {seed} step {step} ({act}): invalid body"); break; }
                    Log(new { test = "KRN11-HISTORY", seed, step, act, volume = p.Volume });
                    if (fails.Count > 4) break;
                }
            }
            Require(fails.Count == 0, string.Join(" | ", fails.Take(5)));
        });
    }
}
static partial class Program
{
    static void Krn11LongSessionChecks()
    {
        Check("KRN11-LONG-SESSION", () =>
        {
            // 11.5: ДОЛГАЯ СЕССИЯ — 160 случайных правок детали квалификации; каждая пятая перестройка прерывается отменой в
            // случайный момент (деталь остаётся в прежнем состоянии, следующая перестройка — та же, что сборка с нуля); каждые
            // 20 шагов — сравнение с чистой сборкой и замер управляемой памяти после сборки мусора. Память не растёт: после
            // прогрева прирост за сессию ≤ 64 МБ.
            var fails = new List<string>();
            var rnd = new Random(31);
            var q = new HistParams(6, 5, false, 4, 3, false, 3);
            var p = BuildQual(q);
            long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            var mem = new List<long>(); int cancelled = 0;
            for (int step = 1; step <= 160; step++)
            {
                var q2 = rnd.Next(7) switch
                {
                    0 => q with { D1 = new[] { 5.5, 6, 7, 8 }[rnd.Next(4)] },
                    1 => q with { RoundR = new[] { 2.0, 3, 4, 5 }[rnd.Next(4)] },
                    2 => q with { RoundOff = !q.RoundOff },
                    3 => q with { SphR = new[] { 2.5, 3, 4 }[rnd.Next(3)] },
                    4 => q with { Copies = q.Copies == 2 ? 3 : 2 },
                    5 => q with { Hole7Off = !q.Hole7Off },
                    _ => q with { ChamferD = q.ChamferD == 2 ? 3 : 2 },
                };
                ApplyHist(p, q, q2); q = q2;
                if (step % 5 == 0)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1 + rnd.Next(40)));
                    try { var rr = p.Compute(null, null, cts.Token); p.Apply(rr); }
                    catch (OperationCanceledException) { cancelled++; }
                }
                p.Rebuild(null);
                var errs = p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}").ToList();
                if (errs.Count > 0) { fails.Add($"step {step}: {string.Join("; ", errs.Take(2))}"); break; }
                if (step % 20 == 0)
                {
                    if (PartGeometry(p) != PartGeometry(BuildQual(q))) fails.Add($"step {step}: geometry differs from a fresh build of {q}");
                    mem.Add(Mem());
                }
            }
            double mb(long x) => x / 1048576.0;
            Log(new { test = "KRN11-LONG-SESSION", cancelled, memoryMb = mem.Select(x => Math.Round(mb(x), 1)) });
            if (cancelled == 0) fails.Add("no rebuild was actually cancelled");
            if (mem.Count >= 3 && mb(mem[^1] - mem[1]) > 64) fails.Add($"memory grew {mb(mem[^1] - mem[1]):F1} MB over the session");
            Require(fails.Count == 0, string.Join(" | ", fails.Take(5)));
        });
    }
}
