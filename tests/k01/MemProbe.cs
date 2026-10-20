using Parametric3D.Model;

static partial class Program
{
    /// <summary>Разбор: удерживаемая память при повторных сборках одной детали (деталь каждый раз новая).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (WeakReference, WeakReference, double) BuildOnce(string json)
    {
        var (p, _) = DocumentFile.Load(json);
        if (Environment.GetEnvironmentVariable("P3D_MEMONE") == "1")
            using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { BooleanThreads = 1 }).Enter()) p.Rebuild(null);
        else p.Rebuild(null);
        return (new WeakReference(p), new WeakReference(p.Bodies[0]), p.Volume);
    }

    static void MemProbe(string path)
    {
        long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
        var json = File.ReadAllText(path);
        Console.WriteLine($"MEM start {Mem() / 1048576.0:0.0} MB");
        for (int i = 0; i < 3; i++)
        {
            var (wp, wb, v) = BuildOnce(json);
            Console.WriteLine($"MEM after build {i}: {Mem() / 1048576.0:0.0} MB (volume {v:0.###}); part alive {wp.IsAlive}, body alive {wb.IsAlive}");
        }
        // ThreadStatic этого потока — обнулить и замерить
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Parametric3D") || a.GetName().Name == "K01Checks"))
            foreach (var t in asm.GetTypes())
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    if (!t.IsGenericTypeDefinition && fi.GetCustomAttributes(typeof(ThreadStaticAttribute), false).Length > 0 && !fi.FieldType.IsValueType)
                        try { if (fi.GetValue(null) != null) { Console.WriteLine($"MEM threadstatic {t.Name}.{fi.Name} set"); fi.SetValue(null, null); } } catch { }
        Console.WriteLine($"MEM after clearing thread statics (main thread): {Mem() / 1048576.0:0.0} MB");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Parametric3D") || a.GetName().Name == "K01Checks"))
            foreach (var t in asm.GetTypes())
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (t.IsGenericTypeDefinition || fi.IsLiteral) continue;
                    object? v; try { v = fi.GetValue(null); } catch { continue; }
                    if (v == null || v is string || v.GetType().IsPrimitive || v is Delegate) continue;
                    int n = -1;
                    if (v is System.Collections.ICollection c) n = c.Count;
                    else if (v.GetType().GetProperty("Count") is { } cp && cp.PropertyType == typeof(int) && cp.GetIndexParameters().Length == 0) { try { n = (int)cp.GetValue(v)!; } catch { } }
                    if (n > 20 || (n < 0 && v.GetType().Name.Contains("ConditionalWeakTable")))
                    {
                        int cw = -1;
                        if (v.GetType().Name.Contains("ConditionalWeakTable")) { try { cw = ((System.Collections.IEnumerable)v).Cast<object>().Count(); } catch { } }
                        Console.WriteLine($"MEM static {t.FullName}.{fi.Name}: count {n} cwt {cw} ({v.GetType().Name})");
                    }
                }
    }
}

static partial class Program
{
    /// <summary>Разбор: зависит ли сетка от пути расчёта (полный пересчёт той же детали = расчёт с нуля?).</summary>
    static void PathDepProbe()
    {
        var q = new HistParams(6, 5, false, 4, 3, false, 3);
        var p = BuildHistory(q);
        string g0 = PartGeometry(p);
        p.Invalidate(); p.Rebuild(null);
        string g1 = PartGeometry(p);
        Console.WriteLine($"PATH full rebuild same: {g0 == g1}");
        var f2 = BuildHistory(q);
        Console.WriteLine($"PATH fresh twice same: {PartGeometry(f2) == g0}");
        {
            var pk = BuildHistory(q); pk.MarkDirtyFrom(pk.Features[0]); pk.Rebuild(null);
            string Sig(Parametric3D.Kernel.Topology.TFace f) { var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(f.Shell!.Solid!, facets: true); return ""; }
            var fa = pk.Bodies.SelectMany(b => b.Faces).ToList(); var fb = f2.Bodies.SelectMany(b => b.Faces).ToList();
            Console.WriteLine($"PATH faces {fa.Count} vs {fb.Count}");
            for (int i = 0; i < Math.Min(fa.Count, fb.Count); i++)
            {
                var sa = fa[i].Surface as Parametric3D.Kernel.Surfaces.FacetedSurface; var sb = fb[i].Surface as Parametric3D.Kernel.Surfaces.FacetedSurface;
                int na = sa?.Facets.Count ?? -1, nb = sb?.Facets.Count ?? -1; int da = sa?.Display?.Count ?? -1, db = sb?.Display?.Count ?? -1;
                bool same = na == nb && da == db && (sa == null || sb == null || sa.Facets.ReadView.SequenceEqual(sb.Facets.ReadView));
                if (!same) Console.WriteLine($"PATH face {i} {fa[i].Tag}/{fb[i].Tag}: facets {na}/{nb} display {da}/{db} fromLoops {sa?.FromLoops}/{sb?.FromLoops}");
            }
        }
        // по операциям: пересчёт с операции k (состояние после k−1 сохранено) — совпадает ли с расчётом с нуля
        for (int k = 0; k < p.Features.Count; k++)
        {
            var pk = BuildHistory(q);
            pk.MarkDirtyFrom(pk.Features[k]); pk.Rebuild(null);
            Console.WriteLine($"PATH from {k} ({pk.Features[k].GetType().Name}): same {PartGeometry(pk) == g0}");
        }
    }
}

static partial class Program
{
    /// <summary>
    /// ДЕТАЛЬ, ОТПУЩЕННАЯ ПОСЛЕ СБОРКИ, НЕ ОСТАВЛЯЕТ ТЕЛ В ПАМЯТИ (u-нагрузка, 2026-10-20). Деталь сессии «Нагрузка»
    /// (36 операций, часть — с отказами): прежде поточные поля ядра (соответствие граней общей булевой, происхождение,
    /// разметчик, «последний отвергнутый результат») держали тела последней операции — ~80 МБ после сборки. Теперь тело и
    /// деталь собираются сборщиком мусора, остаток < 16 МБ. Мутация P3D_THREADRELEASE=0 — тело живо.
    /// </summary>
    static void MemReleaseChecks()
    {
        Check("KRN11-PART-RELEASES-MEMORY", () =>
        {
            long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            long m0 = Mem();
            var json = File.ReadAllText(Fixture("load_session_k75.p3d"));
            var (wp, wb, v) = BuildOnce(json);
            long m1 = Mem();
            Log(new { test = "KRN11-PART-RELEASES-MEMORY", before = m0 / 1048576.0, after = m1 / 1048576.0, partAlive = wp.IsAlive, bodyAlive = wb.IsAlive, volume = v });
            Require(!wp.IsAlive && !wb.IsAlive, $"part alive {wp.IsAlive}, body alive {wb.IsAlive} after release");
            Require(m1 - m0 < 16L * 1048576, $"retained {(m1 - m0) / 1048576.0:0.0} MB after the part was released");
        });
    }
}

static partial class Program
{
    /// <summary>
    /// ГРАНЬ РЕЗЦА В ПЛОСКОСТИ ГРАНИ ТЕЛА: фаска вертикального ребра у угловой фаски (сессия «Нагрузка»,
    /// load_session_k75b.p3d, операция 18). Пересечение резца со скруглением — то же ребро «скругление — угловая фаска»
    /// (до 1e-14), но расстояние до края по ломаной классификатора — 6e-7 мм, общий маршрут отказывал, операция шла
    /// многогранным путём, и тело теряло точность для всех следующих операций. Теперь расстояние — по кривым рёбер:
    /// тело после операции 18 — ExactAnalytic, без понижения. Мутация P3D_EXACTBOUNDARY=0.
    /// </summary>
    static void CoplanarToolChecks()
    {
        Check("KRN05-TOOL-FACE-ON-BODY-EDGE", () =>
        {
            var (part, _) = DocumentFile.Load(File.ReadAllText(Fixture("load_session_k75b.p3d")));
            part.StageIndex = 19; part.Invalidate(); part.Rebuild(null);
            var b = part.Bodies.Single();
            Log(new { test = "KRN05-TOOL-FACE-ON-BODY-EDGE", guarantee = b.Guarantee.ToString(), downgrades = part.Downgrades.Count, error18 = part.Features[18].Error });
            Require(part.Features.Take(19).All(f => f.Error == null), "errors: " + string.Join("; ", part.Features.Take(19).Where(f => f.Error != null).Select(f => f.Name)));
            Require(part.Downgrades.Count == 0 && b.Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic, $"guarantee {b.Guarantee}, downgrades {part.Downgrades.Count}");
            Require(Parametric3D.Kernel.Topology.SolidValidator.Validate(b).IsValid, "invalid body");
        });
    }
}
