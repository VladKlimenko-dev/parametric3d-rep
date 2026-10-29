using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.App.Diagnostics;

static partial class Program
{
    /// <summary>
    /// 11.5 (раздел «Нагрузка» кнопки «Тест»): долгая сессия скруглений и фасок случайных прямых рёбер реалистичной детали —
    /// рёбра, концы которых упираются в прежние скругления и фаски сессии. Каждая правка — операция в конец дерева;
    /// отказ — откат. Разбор: сколько отказов и их причины.
    /// </summary>
    static Part? last;
    static (int Done, int Refused, Dictionary<string, int> Why) BlendSession(int seed, int n, int partSeed = 3, int partFeatures = 12, Action<int, string>? log = null, int stopAt = -1)
    {
        var p = TestParts.Realistic(partSeed, partFeatures); p.Rebuild(null);
        var rnd = new Random(seed);
        int done = 0, refused = 0; var why = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            var edges = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 6 && Sharp(e)).ToList();
            if (edges.Count == 0) break;
            var e = edges[rnd.Next(edges.Count)];
            bool round = i % 2 == 0; double size = 0.5 + rnd.NextDouble();
            Feature f = round ? new RoundFeature($"R{i}") { Radius = new ValueRef(size) } : new ChamferFeature($"C{i}") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
            if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(e)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(e));
            p.AddFeature(f); p.Rebuild(null);
            if (f.Error == null) { done++; log?.Invoke(i, $"OK {(round ? "round" : "chamfer")} {size:0.###} edge ({e.V0.P.X:0.##},{e.V0.P.Y:0.##},{e.V0.P.Z:0.##})-({e.V1.P.X:0.##},{e.V1.P.Y:0.##},{e.V1.P.Z:0.##})"); if (stopAt == i + 1) { last = p; return (done, refused, why); } continue; }
            refused++;
            string key = f.Error.Split('\n').Last(l => l.Contains(": the cut") || l.Contains("refused") || l.Length > 0);
            key = System.Text.RegularExpressions.Regex.Replace(key, @"\([-0-9., ]+\)", "(…)");
            why[key] = why.GetValueOrDefault(key) + 1;
            log?.Invoke(i, $"{(round ? "round" : "chamfer")} {size:0.###} edge ({e.V0.P.X:0.##},{e.V0.P.Y:0.##},{e.V0.P.Z:0.##})-({e.V1.P.X:0.##},{e.V1.P.Y:0.##},{e.V1.P.Z:0.##}): {f.Error.Replace('\n', '#')}");
            p.RemoveFeature(f); p.Rebuild(null);
            if (stopAt == i + 1) { last = p; return (done, refused, why); }
        }
        last = p;
        return (done, refused, why);
    }

    /// <summary>Острое ребро: нормали двух граней у середины ребра расходятся больше чем на 1° (гладкий стык — не ребро для скругления).</summary>
    public static bool Sharp(TEdge e)
    {
        var faces = e.CoEdges.Select(ce => ce.Loop?.Face).Where(f => f != null).Distinct().ToList();
        if (faces.Count != 2) return false;
        var m = (e.V0.P + e.V1.P) / 2;
        var n = new List<OpenTK.Mathematics.Vector3d>();
        foreach (var f in faces)
        {
            var srf = GeometryAudit.AuthoritativeSurface(f!) ?? f!.Surface;
            try { n.Add(srf.NormalAt(srf.ProjectToUV(m)).Normalized()); } catch { return true; }
        }
        return Math.Abs(OpenTK.Mathematics.Vector3d.Dot(n[0], n[1])) < Math.Cos(Math.PI / 180);
    }

    static void BlendSessionProbe(int n)
    {
        var (d, r, why) = BlendSession(11, n, log: (i, m) => Console.WriteLine($"REFUSE {i}: {m}"));
        Log(new { done = d, refused = r });
        foreach (var kv in why.OrderByDescending(k => k.Value)) Console.WriteLine($"{kv.Value,4}  {kv.Key}");
    }
}

static partial class Program
{
    /// <summary>Разбор: скругление/фаска каждого прямого ребра реалистичной детали поодиночке (на исходной детали).</summary>
    static void BlendEachEdgeProbe(int partSeed, int partFeatures, double size)
    {
        var p0 = TestParts.Realistic(partSeed, partFeatures); p0.Rebuild(null);
        var edges = p0.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 6).ToList();
        var why = new Dictionary<string, int>(); int ok = 0, bad = 0;
        foreach (var e in edges)
            foreach (bool round in new[] { true, false })
            {
                var p = TestParts.Realistic(partSeed, partFeatures); p.Rebuild(null);
                var e2 = p.Bodies.SelectMany(b => b.Edges).FirstOrDefault(q => (q.V0.P - e.V0.P).Length < 1e-6 && (q.V1.P - e.V1.P).Length < 1e-6);
                if (e2 == null) continue;
                Feature f = round ? new RoundFeature("R") { Radius = new ValueRef(size) } : new ChamferFeature("C") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
                if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(e2)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(e2));
                p.AddFeature(f); p.Rebuild(null);
                if (f.Error == null) { ok++; continue; }
                bad++;
                string key = f.Error.Split('\n').Last();
                key = System.Text.RegularExpressions.Regex.Replace(key, @"[-0-9]+\.[0-9]+", "#");
                why[key] = why.GetValueOrDefault(key) + 1;
                Console.WriteLine($"FAIL {(round ? "round" : "chamfer")} ({e.V0.P.X:0.##},{e.V0.P.Y:0.##},{e.V0.P.Z:0.##})-({e.V1.P.X:0.##},{e.V1.P.Y:0.##},{e.V1.P.Z:0.##}): {f.Error.Replace('\n', '#')}");
            }
        Log(new { ok, bad });
        foreach (var kv in why.OrderByDescending(k => k.Value)) Console.WriteLine($"{kv.Value,4}  {kv.Key}");
    }
}

static partial class Program
{
    /// <summary>
    /// 11.5: каждое острое прямое ребро СОХРАНЁННОГО состояния сессии поодиночке (скругление и фаска) — мера, не зависящая от
    /// хода сессии (в самой сессии каждое принятое ребро меняет деталь, и счёт после правки ядра «плывёт»).
    /// Причины «размер больше грани» — законные отказы, считаются отдельно.
    /// </summary>
    static (int Ok, int Legit, int Bad, List<string> Fails) BlendEachOnFile(string text, int maxEdges = 1000)
    {
        var (p0, pars0) = Parametric3D.Model.DocumentFile.Load(text); p0.Rebuild(pars0);
        var edges = p0.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 6 && Sharp(e))
            .Select(e => (e.V0.P, e.V1.P)).Take(maxEdges).ToList();
        int ok = 0, legit = 0, bad = 0; var fails = new List<string>();
        foreach (var (A, B) in edges)
            foreach (bool round in new[] { true, false })
            {
                var (p, pars) = Parametric3D.Model.DocumentFile.Load(text); p.Rebuild(pars);
                var e2 = p.Bodies.SelectMany(b => b.Edges).FirstOrDefault(q => (q.V0.P - A).Length < 1e-6 && (q.V1.P - B).Length < 1e-6);
                if (e2 == null) continue;
                double size = 0.6;
                Feature f = round ? new RoundFeature("R") { Radius = new ValueRef(size) } : new ChamferFeature("C") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
                if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(e2)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(e2));
                p.AddFeature(f); p.Rebuild(pars);
                if (f.Error == null) { ok++; continue; }
                if (f.Error.Contains(": the cut reaches")) { legit++; continue; }
                bad++;
                fails.Add($"{(round ? "round" : "chamfer")} {A.X:0.##},{A.Y:0.##},{A.Z:0.##},{B.X:0.##},{B.Y:0.##},{B.Z:0.##}: {f.Error.Split('\n').Last()}");
            }
        return (ok, legit, bad, fails);
    }

    static void BlendEachFileProbe(string file)
    {
        var (ok, legit, bad, fails) = BlendEachOnFile(File.ReadAllText(file));
        foreach (var f in fails) Console.WriteLine("FAIL " + f);
        Log(new { ok, legit, bad });
    }
}

static partial class Program
{
    /// <summary>Разбор: сессия до шага k (без него), затем шаг k с отладкой обходного пути.</summary>
    static void BlendStepProbe(int k)
    {
        BlendSession(11, k, stopAt: k);
        var p = last!;
        var rnd = new Random(11);
        // повторить выбор шага k: тот же генератор — прогнать k выборов
        Console.WriteLine($"part after {k} steps: features {p.Features.Count}");
        Parametric3D.Model.DocumentFile.Save(p);
        File.WriteAllText($"/tmp/claude-0/blendstep{k}.p3d", Parametric3D.Model.DocumentFile.Save(p));
    }
}

static partial class Program
{
    static void BlendEdgeProbe(string file, double[] a, bool round, double size)
    {
        var (p, pars) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(file));
        if (Environment.GetEnvironmentVariable("BLENDDEFER") == "1")
        {
            using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { Preview = true, LivePreview = false }).Enter()) p.Rebuild(pars);
        }
        else p.Rebuild(pars);
        var A = new OpenTK.Mathematics.Vector3d(a[0], a[1], a[2]); var B = new OpenTK.Mathematics.Vector3d(a[3], a[4], a[5]);
        var e = p.Bodies.SelectMany(b => b.Edges).OrderBy(q => Math.Min((q.V0.P - A).Length + (q.V1.P - B).Length, (q.V0.P - B).Length + (q.V1.P - A).Length)).First();
        Console.WriteLine($"edge {e.V0.P} → {e.V1.P}; faces {string.Join(" | ", e.CoEdges.Select(c => c.Loop?.Face?.Tag + " " + GeometryAudit.AuthoritativeSurface(c.Loop!.Face!)?.GetType().Name))}");
        Parametric3D.Kernel.Ops.BooleanOp.GeneralInvalidProbe = r =>
        {
            var v = SolidValidator.Validate(r);
            foreach (var iss in v.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4))
            {
                Console.WriteLine("INVALID " + iss);
                if (iss.Element is uint id && r.Edges.FirstOrDefault(x => x.Id == id) is { } ed)
                    Console.WriteLine($"   edge {ed.V0.P:0.####} → {ed.V1.P:0.####} {ed.Curve?.GetType().Name}; faces: {string.Join(" | ", ed.CoEdges.Select(c => c.Loop?.Face?.Tag + " " + GeometryAudit.AuthoritativeSurface(c.Loop!.Face!)?.GetType().Name + (c.Reversed ? " rev" : "")))}");
            }
        };
        var bodyBefore = TopologyClone.Deep(p.Bodies[0]);
        TSolid? builtTool = null;
        Parametric3D.Kernel.Ops.FilletOp.BuiltTool = t => builtTool ??= TopologyClone.Deep(t);
        Feature f = round ? new RoundFeature("R") { Radius = new ValueRef(size) } : new ChamferFeature("C") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
        if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(e)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(e));
        if (Environment.GetEnvironmentVariable("BLENDCHAIN") == "1")
        {   // разбор: и все рёбра, касательно продолжающие выбранное (как цепочка в приложении)
            var all = p.Bodies.SelectMany(b => b.Edges).ToList(); var chain = new List<TEdge> { e };
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var q in all)
                {
                    if (chain.Contains(q) || q.Curve == null) continue;
                    foreach (var c in chain.ToList())
                        foreach (var (v, tq, tc) in new[] { (q.V0, q.Curve.TangentAt(q.Curve.T0), 0), (q.V1, q.Curve.TangentAt(q.Curve.T1), 1) })
                            if ((ReferenceEquals(v, c.V0) || ReferenceEquals(v, c.V1)) && c.Curve != null)
                            {
                                var tcv = ReferenceEquals(v, c.V0) ? c.Curve.TangentAt(c.Curve.T0) : c.Curve.TangentAt(c.Curve.T1);
                                if (Math.Abs(OpenTK.Mathematics.Vector3d.Dot(tq.Normalized(), tcv.Normalized())) > 0.9995 && !chain.Contains(q)) { chain.Add(q); grew = true; }
                            }
                }
            }
            foreach (var q in chain.Skip(1)) { if (f is RoundFeature rf2) rf2.Edges.Add(EdgeRef.Of(q)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(q)); }
            Console.WriteLine($"chain: {chain.Count} edge(s)");
        }
        p.AddFeature(f);
        if (Environment.GetEnvironmentVariable("BLENDDEFER") == "1")
        {   // как приложение: обычный расчёт с отложенными проверками (Document, k126)
            using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { Preview = true, LivePreview = false }).Enter()) p.Rebuild(pars);
        }
        else if (Environment.GetEnvironmentVariable("BLENDDEFER") == "2")
        {   // как предпросмотр приложения: живой, сетка грубее
            var k0 = Parametric3D.Kernel.KernelSettings.Capture();
            using (new Parametric3D.Kernel.KernelContext(k0 with { Preview = true, LivePreview = true, Chord = k0.Chord * 4 }).Enter()) p.Rebuild(pars);
        }
        else p.Rebuild(pars);
        Parametric3D.Kernel.Ops.BooleanOp.GeneralInvalidProbe = null; Parametric3D.Kernel.Ops.FilletOp.BuiltTool = null;
        if (Environment.GetEnvironmentVariable("BLENDDUMP") is { Length: > 0 } dump && builtTool != null)
        {
            Directory.CreateDirectory(dump);
            var w = new List<string>();
            void Step(string n, TSolid b) { var sw = new StringWriter(); if (Parametric3D.Kernel.Export.StepExact.Write(sw, new List<TSolid> { b }, n, w)) File.WriteAllText(Path.Combine(dump, n + ".step"), sw.ToString()); }
            Step("A", bodyBefore); Step("B", builtTool);
            var u = new List<TSolid> { TopologyClone.Deep(bodyBefore) }; var wu = new List<string>();
            try { Parametric3D.Kernel.Ops.BooleanOp.Unite(u, TopologyClone.Deep(builtTool), wu); } catch (Exception ex) { Console.WriteLine("unite: " + ex.Message); }
            if (u.Count == 1) { Step("U", u[0]); var vu = SolidValidator.Validate(u[0]); Console.WriteLine("DUMP U valid " + vu.IsValid + " " + string.Join(" / ", vu.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3))); }
            Console.WriteLine($"DUMP vA {AnalyticVolume.Of(bodyBefore)} vB {AnalyticVolume.Of(builtTool)} vBmesh {Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(builtTool))} Bbox {builtTool.Bounds.Min} {builtTool.Bounds.Max} vU {(u.Count == 1 ? AnalyticVolume.Of(u[0]) : null)} {string.Join(" / ", wu.TakeLast(2))}");
        }
        Console.WriteLine("ERROR: " + (f.Error?.Replace('\n', '#') ?? "none"));
        if (Environment.GetEnvironmentVariable("BLENDSAVE") is { Length: > 0 } bsave) File.WriteAllText(bsave, Parametric3D.Model.DocumentFile.Save(p, pars));
        if (Environment.GetEnvironmentVariable("BLENDFACES") == "1")
            foreach (var fz in p.Bodies.SelectMany(b => b.Faces))
            {
                var pts = new List<OpenTK.Mathematics.Vector3d>(); foreach (var ce in fz.Loops.SelectMany(l => l.CoEdges)) ce.Tessellate(pts, true);
                if (pts.Count == 0) continue;
                double rmin = pts.Min(q => Math.Sqrt(q.X * q.X + q.Y * q.Y)), rmax = pts.Max(q => Math.Sqrt(q.X * q.X + q.Y * q.Y)), zmin = pts.Min(q => q.Z), zmax = pts.Max(q => q.Z);
                if (zmax > -5) Console.WriteLine($"FACE {fz.Tag} {GeometryAudit.AuthoritativeSurface(fz)?.GetType().Name} r {rmin:0.###}..{rmax:0.###} z {zmin:0.###}..{zmax:0.###}");
            }
        foreach (var wm in p.Warnings.Where(x => x.Contains("Round") || x.Contains("Chamfer") || x.Contains("Boolean"))) Console.WriteLine("WARN " + wm);
        if (Parametric3D.Kernel.Ops.BooleanOp.LastInvalidResult is { } bad)
        {
            var v = SolidValidator.ValidateBRep(bad);
            Console.WriteLine($"LASTINVALID guarantee {bad.Guarantee} faces {bad.Faces.Count()}");
            foreach (var iss in v.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4))
            {
                Console.WriteLine("INVALID " + iss);
                if (iss.Element is uint id && bad.Edges.FirstOrDefault(x => x.Id == id) is { } ed)
                    Console.WriteLine($"   edge {ed.V0.P:0.####} → {ed.V1.P:0.####} {ed.Curve?.GetType().Name}; faces: {string.Join(" | ", ed.CoEdges.Select(c => c.Loop?.Face?.Tag + " " + GeometryAudit.AuthoritativeSurface(c.Loop!.Face!)?.GetType().Name + (c.Reversed ? " rev" : "")))}");
            }
        }
        Log(new { volume = p.Volume, guarantee = string.Join(",", p.Bodies.Select(b => b.Guarantee)) });
    }
}

static partial class Program
{
    /// <summary>
    /// 11.5: СКРУГЛЕНИЕ И ФАСКА РЕБРА, КОНЦЫ КОТОРОГО УПИРАЮТСЯ В ПРЕЖНИЕ СКРУГЛЕНИЯ. Состояния сессии раздела «Нагрузка»
    /// (сохранены после 2 и 6 правок, tests/ui/data/blend_session*.p3d) и рёбра, на которых правка прежде откатывалась:
    /// — угол кармана над скруглением дна (R0.672): щепки-фасеты узкого вогнутого скругления переворачивались по нормали
    ///   против соседей (мутация P3D_SLIVERWIND=0);
    /// — ребро дна вдоль скругления: торцевой цилиндр наполнителя касался плоскости ребра, общий путь булевой отказывал,
    ///   а гранёный давал ребро с четырьмя гранями (P3D_FILLERTANGENTEND=0);
    /// — проверка «вырез не больше инструмента» сравнивала объёмы по сеткам при точных телах (P3D_DAMAGEEXACTVOL=0).
    /// Норма: правка принята, тело точное и годное, объём изменился, но не больше профиля × длина.
    /// </summary>
    static void Krn11BlendAtBlendsChecks()
    {
        Check("KRN11-BLEND-AT-PRIOR-BLENDS", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var cases = new (string File, double[] E, bool Round, double Size)[]
            {
                ("blend_session2.p3d", new[] { 34.31, 18.11, 2.53, 34.31, 18.11, 8.59 }, true, 0.672),
                ("blend_session6.p3d", new[] { 34.31, 4.41, 1.99, 34.31, 17.56, 1.99 }, true, 0.6),
                ("blend_session6.p3d", new[] { 34.31, 4.41, 1.99, 34.31, 17.56, 1.99 }, false, 0.6),
                ("blend_session6.p3d", new[] { 58.48, 4.41, 10, 58.48, 19.51, 10 }, true, 0.6),
            };
            var fails = new List<string>();
            foreach (var (file, a, round, size) in cases)
            {
                var (p, pars) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(Path.Combine(dir, file))); p.Rebuild(pars);
                double v0 = p.Volume;
                var A = new OpenTK.Mathematics.Vector3d(a[0], a[1], a[2]); var B = new OpenTK.Mathematics.Vector3d(a[3], a[4], a[5]);
                var e = p.Bodies.SelectMany(b => b.Edges).OrderBy(q => Math.Min((q.V0.P - A).Length + (q.V1.P - B).Length, (q.V0.P - B).Length + (q.V1.P - A).Length)).First();
                Feature f = round ? new RoundFeature("R") { Radius = new ValueRef(size) } : new ChamferFeature("C") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
                if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(e)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(e));
                p.AddFeature(f); p.Rebuild(pars);
                string tag = $"{file} {(round ? "round" : "chamfer")} {size} ({a[0]},{a[1]},{a[2]})";
                if (f.Error != null) { fails.Add($"{tag}: {f.Error.Split('\n').Last()}"); continue; }
                var body = p.Bodies.Single();
                bool valid = SolidValidator.Validate(body).IsValid;
                double dv = p.Volume - v0, len = (A - B).Length, prof = size * size;
                Log(new { tag, guarantee = body.Guarantee.ToString(), valid, dv = Math.Round(dv, 4) });
                if (!valid) fails.Add(tag + ": invalid body");
                if (!body.Guarantee.ToString().StartsWith("Exact")) fails.Add(tag + ": guarantee " + body.Guarantee);
                if (Math.Abs(dv) < 1e-4 || Math.Abs(dv) > prof * len) fails.Add($"{tag}: volume change {dv:G4}");
            }
            // предпросмотр (как приложение: живой, сетка грубее, последняя операция — мгновенный по сеткам): прибавка у вогнутого
            // ребра шла через булеву, итогом был носитель сетки без граней, и защитная проверка отказывала «no surface left»
            // (P3D_CARRIERCHECK=0)
            {
                var (p, pars) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(Path.Combine(dir, "blend_session2.p3d"))); p.Rebuild(pars);
                var A = new OpenTK.Mathematics.Vector3d(34.31, 18.11, 2.53); var B = new OpenTK.Mathematics.Vector3d(34.31, 18.11, 8.59);
                var e = p.Bodies.SelectMany(b => b.Edges).OrderBy(q => (q.V0.P - A).Length + (q.V1.P - B).Length).First();
                var rf = new RoundFeature("R") { Radius = new ValueRef(0.672) }; rf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(rf);
                var k0 = Parametric3D.Kernel.KernelSettings.Capture();
                long hits0 = Parametric3D.Kernel.Ops.InstantPreview.Hits;
                using (new Parametric3D.Kernel.KernelContext(k0 with { Preview = true, LivePreview = true, Chord = k0.Chord * 4 }).Enter()) p.Rebuild(pars);
                Log(new { preview = rf.Error ?? "ok", instantHits = Parametric3D.Kernel.Ops.InstantPreview.Hits - hits0 });
                if (rf.Error != null) fails.Add("preview: " + rf.Error.Split('\n').Last());
                if (Parametric3D.Kernel.Ops.InstantPreview.Hits == hits0) fails.Add("preview: the instant mesh preview was not used");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
