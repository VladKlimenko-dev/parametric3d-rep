using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>Разбор (k153): какие типы геометрии и вложений у построенных тел файлов — для формата записи тела в документ.
    /// Режим «brephist файл…».</summary>
    static void BrepHist(IEnumerable<string> files)
    {
        var all = new SortedDictionary<string, int>();
        foreach (var file in files)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
            p.Rebuild(pars);
            var h = new SortedDictionary<string, int>();
            void Add(string k) { h[k] = h.GetValueOrDefault(k) + 1; all[k] = all.GetValueOrDefault(k) + 1; }
            foreach (var b in p.Bodies)
            {
                Add("solid " + b.Guarantee + (b.Exact != null ? " +exact" : "") + (b.Attr != null ? " attr " + b.Attr.GetType().Name : ""));
                foreach (var f in b.Faces)
                {
                    Add("S " + Surf(f.Surface));
                    if (f.Attr != null) Add("faceAttr " + f.Attr.GetType().Name);
                    foreach (var l in f.Loops) foreach (var ce in l.CoEdges) Add("P " + (ce.PCurve?.GetType().Name ?? "null"));
                }
                foreach (var e in b.Faces.SelectMany(f => f.Edges).Distinct())
                {
                    Add("C " + e.Curve.GetType().Name);
                    if (e.Attr != null) Add("edgeAttr " + e.Attr.GetType().Name);
                }
            }
            Log(new { file = Path.GetFileName(file), ms = sw.ElapsedMilliseconds, bodies = p.Bodies.Count, hist = h });
        }
        Log(new { total = all });
    }
    static string Surf(Parametric3D.Kernel.Surfaces.ISurface3 s) => s switch
    {
        Parametric3D.Kernel.Surfaces.OffsetSurface o => "Offset(" + Surf(o.Base) + ")",
        Parametric3D.Kernel.Surfaces.TrimmedSurface t => "Trimmed(" + Surf(t.Base) + ")",
        Parametric3D.Kernel.Surfaces.SectionSurface ss => ss.IsProcedural ? "Section(proc)" : "Section",
        Parametric3D.Kernel.Surfaces.FacetedSurface fs => "Faceted(" + (fs.Source == null ? "-" : Surf(fs.Source)) + ")",
        _ => s.GetType().Name,
    };
}

static partial class Program
{
    /// <summary>k153: запись тела в документ и чтение обратно — то же тело (режим «breprt файл…»).</summary>
    static void BrepRoundTrip(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
            p.Rebuild(pars);
            long buildMs = sw.ElapsedMilliseconds;
            var probs = new List<string>();
            byte[]? data = null;
            sw.Restart();
            try { data = Parametric3D.Kernel.Topology.BrepFile.Write(p.Bodies); }
            catch (NotSupportedException ex) { Log(new { file = Path.GetFileName(file), buildMs, unsupported = ex.Message }); continue; }
            long writeMs = sw.ElapsedMilliseconds; sw.Restart();
            var back = Parametric3D.Kernel.Topology.BrepFile.ReadAll(data);
            long readMs = sw.ElapsedMilliseconds;
            if (back.Count != p.Bodies.Count) probs.Add($"bodies {back.Count} vs {p.Bodies.Count}");
            double worstS = 0, worstC = 0;
            var rnd = new Random(7);
            for (int i = 0; i < Math.Min(back.Count, p.Bodies.Count); i++)
            {
                var a = p.Bodies[i]; var b = back[i].Body;
                var fa = a.Faces.ToList(); var fb = b.Faces.ToList();
                var ea = a.Faces.SelectMany(f => f.Edges).Distinct().ToList(); var eb = b.Faces.SelectMany(f => f.Edges).Distinct().ToList();
                if (fa.Count != fb.Count || ea.Count != eb.Count) { probs.Add($"faces {fb.Count}/{fa.Count} edges {eb.Count}/{ea.Count}"); continue; }
                for (int k = 0; k < fa.Count; k++)
                {
                    if (fa[k].Tag != fb[k].Tag || fa[k].Reversed != fb[k].Reversed) probs.Add($"face {k} tag/sense");
                    if (back[i].IdMap.GetValueOrDefault(fa[k].Id) != fb[k].Id) probs.Add($"face {k} id map");
                    var sa = GeometryAudit.AuthoritativeSurface(fa[k]); var sb = GeometryAudit.AuthoritativeSurface(fb[k]);
                    if (sa == null || sb == null) continue;
                    for (int t = 0; t < 8; t++)
                    {
                        var uv = new OpenTK.Mathematics.Vector2d(rnd.NextDouble(), rnd.NextDouble());
                        try { worstS = Math.Max(worstS, (sa.PointAt(uv) - sb.PointAt(uv)).Length); } catch { }
                    }
                }
                for (int k = 0; k < ea.Count; k++)
                {
                    if (ea[k].Tag != eb[k].Tag) probs.Add($"edge {k} tag");
                    var ca = ea[k].Curve; var cb = eb[k].Curve;
                    for (int t = 0; t <= 8; t++) { double u = ca.T0 + (ca.T1 - ca.T0) * t / 8; worstC = Math.Max(worstC, (ca.PointAt(u) - cb.PointAt(u)).Length); }
                }
                var va = SolidValidator.Validate(a); var vb = SolidValidator.Validate(b);
                if (va.IsValid != vb.IsValid) probs.Add($"valid {vb.IsValid} vs {va.IsValid}");
                double vola = AnalyticVolume.Of(a) ?? double.NaN, volb = AnalyticVolume.Of(b) ?? double.NaN;
                if (!(Math.Abs(vola - volb) <= 1e-9 * Math.Max(1, Math.Abs(vola))) && !(double.IsNaN(vola) && double.IsNaN(volb))) probs.Add($"volume {volb} vs {vola}");
                if (a.Guarantee != b.Guarantee) probs.Add($"guarantee {b.Guarantee} vs {a.Guarantee}");
                if ((a.Exact != null && a.ExactAuthoritative) != (b.Exact != null && b.ExactAuthoritative)) probs.Add("exact form presence");
                else if (a.Exact != null && a.ExactAuthoritative && b.Exact != null)
                {
                    double xa = a.Exact.Solid.VolumeMm3().ToDouble(), xb = b.Exact.Solid.VolumeMm3().ToDouble();
                    if (a.Exact.Solid.Polygons.Count != b.Exact.Solid.Polygons.Count || xa != xb) probs.Add($"exact form {b.Exact.Solid.Polygons.Count}/{a.Exact.Solid.Polygons.Count} polygons, volume {xb} vs {xa}");
                }
            }
            if (worstS > 1e-9) probs.Add($"surface points differ {worstS:E1}");
            if (worstC > 1e-8) probs.Add($"curve points differ {worstC:E1}");
            Log(new { file = Path.GetFileName(file), buildMs, writeMs, readMs, bytes = data.Length, ok = probs.Count == 0, worstS, worstC, problems = probs.Take(6) });
        }
    }
}

static partial class Program
{
    /// <summary>Разбор (k153): тело детали после перестроения — в OBJ (режим «p3dobj файл выход.obj»), для снимка вне приложения.</summary>
    static void P3dObj(string file, string outPath)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        p.Rebuild(pars);
        using var w = new StreamWriter(outPath);
        int baseIdx = 1;
        foreach (var b in p.Bodies)
        {
            var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            foreach (var v in md.Vertices) w.WriteLine(FormattableString.Invariant($"v {v.P.X} {v.P.Y} {v.P.Z}"));
            for (int i = 0; i + 2 < md.Indices.Length; i += 3) w.WriteLine($"f {md.Indices[i] + baseIdx} {md.Indices[i + 1] + baseIdx} {md.Indices[i + 2] + baseIdx}");
            baseIdx += md.Vertices.Length;
        }
        Log(new { file = Path.GetFileName(file), bodies = p.Bodies.Count, errors = string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error)), warnings = p.Warnings.Take(5) });
    }
}

static partial class Program
{
    /// <summary>Разбор (k153): стадии расчёта детали со временем (режим «stagetimes файл»).</summary>
    /// <summary>
    /// k156: ВРЕМЯ ОТКРЫТИЯ. Файл как есть: чтение (DocumentFile.Load), восстановлено ли тело; если нет — перестроение и запись
    /// с телом, затем повторное чтение. Печать: размер файла, время разбора JSON, восстановления тела, причина отказа.
    /// </summary>
    static void LoadTime(string file)
    {
        void One(string tag, string text)
        {
            long rej0 = DocumentFile.BrepRejected;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = true;
            var (p, pars) = DocumentFile.Load(text);
            long ms = sw.ElapsedMilliseconds;
            Console.WriteLine($"{tag}: {text.Length / 1e6:0.0} MB, load {ms} ms, restored {p.RestoredFromFile}, cached {p.ShowsCached}, dirty {p.IsDirty}, brepRejected {DocumentFile.BrepRejected - rej0}, hasBrep {text.Contains("\"Brep\"")}");
            Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
            Parametric3D.Kernel.Diagnostics.Prof.Reset();
        }
        string t0 = File.ReadAllText(file);
        One("as is", t0);
        var (q, qp) = DocumentFile.Load(t0);
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        if (!q.RestoredFromFile) q.Rebuild(qp);
        Console.WriteLine($"rebuild {sw2.ElapsedMilliseconds} ms, errors {q.Features.Count(f => f.Error != null)}");
        sw2.Restart();
        string t1 = DocumentFile.Save(q, qp, withDisplay: true);
        Console.WriteLine($"save {sw2.ElapsedMilliseconds} ms");
        if (Environment.GetEnvironmentVariable("LOADTIME_KEEP") is { Length: > 0 } keep) File.WriteAllText(keep, t1);
        One("resaved", t1);
    }

    /// <summary>k156: ВРЕМЯ ЗАПИСИ по частям: тела (BrepFile.Write), сетка показа, весь документ; профиль записи тел.</summary>
    static void SaveTime(string file)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (!p.RestoredFromFile) p.Rebuild(pars);
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = Parametric3D.Kernel.Topology.BrepFile.Write(p.Bodies);
        Console.WriteLine($"brep write {sw.ElapsedMilliseconds} ms, {data.Length / 1e6:0.0} MB");
        Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
        Parametric3D.Kernel.Diagnostics.Prof.Reset();
        sw.Restart();
        var t = DocumentFile.Save(p, pars, withDisplay: true);
        Console.WriteLine($"document save {sw.ElapsedMilliseconds} ms, {t.Length / 1e6:0.0} MB");
        Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
    }

    /// <summary>k156: перестроение файла и ошибки операций (с выбранным числом процессоров — DOTNET_PROCESSOR_COUNT).</summary>
    static void RebuildErrors(string file)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(pars);
        Console.WriteLine($"cpu {Environment.ProcessorCount}, rebuild {sw.ElapsedMilliseconds} ms, volume {p.Volume:0.###}");
        foreach (var f in p.Features.Where(f => f.Error != null)) Console.WriteLine($"ERROR {f.Name}: {f.Error}");
        foreach (var w in p.Warnings.Where(w => w.Contains("Chamfer") || w.Contains("rolled back"))) Console.WriteLine($"warn {w}");
    }

    /// <summary>k156: последняя фаска файла с разными размерами (перестроение с неё): ошибка, объём, гарантия.</summary>
    static void ChamferSweep(string file, IEnumerable<string> sizes)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        p.Rebuild(pars);
        var ch = p.Features.OfType<Parametric3D.Model.Features.ChamferFeature>().Last();
        foreach (var s in sizes)
        {
            double d = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            ch.D1 = new Parametric3D.Model.Features.ValueRef(d); ch.D2 = new Parametric3D.Model.Features.ValueRef(d);
            p.MarkDirtyFrom(ch);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.Rebuild(pars);
            Console.WriteLine($"C{d}: {sw.ElapsedMilliseconds} ms, volume {p.Volume:0.###}, g {string.Join(",", p.Bodies.Select(b => b.Guarantee))}, error {ch.Error?.Split('\n').Last()}");
        }
    }

    static void StageTimes(string file)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string last = ""; double lastT = 0; int rep = 0;
        var ctx = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture())
        {
            StageHook = st =>
            {
                double t = sw.Elapsed.TotalSeconds;
                if (st == last && t - lastT < 5) { rep++; return; }
                Console.WriteLine($"{t,8:0.0}s {st}" + (rep > 0 ? $" (prev ×{rep + 1})" : ""));
                last = st; lastT = t; rep = 0;
            }
        };
        Parametric3D.Kernel.Diagnostics.Prof.Enabled = true;
        using (ctx.Enter()) p.Rebuild(pars);
        Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report());
        foreach (var b in p.Bodies)
            Console.WriteLine($"body {b.Guarantee} exact {b.Exact != null}/{b.ExactAuthoritative} faces " + string.Join(", ", b.Faces.GroupBy(f => Surf(f.Surface) + "/" + (GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface ss ? (ss.IsProcedural ? "proc" : "table") : GeometryAudit.AuthoritativeSurface(f)?.GetType().Name)).Select(g => g.Key + "×" + g.Count())));
        Console.WriteLine($"{sw.Elapsed.TotalSeconds,8:0.0}s done; " + string.Join(", ", p.LastFeatureMs.Select(k => k.Key.Name + " " + k.Value.ToString("0"))));
    }
}
