using OpenTK.Mathematics;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Kernel.Topology;

/// <summary>Насколько серьёзно замечание: ошибка делает представление негодным, остальное — сведения.</summary>
public enum ValidationSeverity { Error, Warning, Info }

/// <summary>Какое представление тела проверялось.</summary>
public enum Representation { BRep, Exact, Mesh, Geometry }

/// <summary>Одно замечание единой проверки.</summary>
public readonly record struct ValidationIssue(
    Representation Where, ValidationSeverity Severity, string Code, string Message,
    uint? Element = null, Vector3d? At = null)
{
    public override string ToString() =>
        $"[{Where}/{Severity}] {Code}: {Message}" +
        (Element is { } e ? $" (элемент {e})" : "") +
        (At is { } p ? $" at ({p.X:0.#####}, {p.Y:0.#####}, {p.Z:0.#####})" : "");
}

/// <summary>Что проверять. Самопересечения дороги и включаются явно.</summary>
public sealed class ValidationOptions
{
    /// <summary>Тело обязано быть замкнутым (у открытой оболочки рёбра на краю законны).</summary>
    public bool Closed = true;
    public bool CheckBRep = true;
    public bool CheckExact = true;
    public bool CheckMesh = true;
    /// <summary>KRN-03: согласованность p-кривых (полная проверка; булева проверяет только B-rep).</summary>
    public bool CheckPCurves = true;
    /// <summary>Геометрическая проверка самопересечений сетки (комбинаторика их не доказывает).</summary>
    public bool CheckSelfIntersections;
    /// <summary>Сколько однотипных замечаний перечислять поимённо; счёт ведётся полностью.</summary>
    public int MaxListed = 8;
}

/// <summary>Результат единой проверки: замечания и числовые показатели по каждому представлению.</summary>
public sealed class ValidationReport
{
    public readonly List<ValidationIssue> Issues = new();
    public readonly Dictionary<string, double> Metrics = new();
    public readonly HashSet<Representation> Checked = new();

    /// <summary>
    /// Годно: проверено хотя бы одно представление И ошибок нет. KRN-01 (P0): «не проверялось» — не «нет ошибок»;
    /// пустой отчёт (ничего не проверено) прежде считался годным.
    /// </summary>
    public bool IsValid => Checked.Count > 0 && !Issues.Any(i => i.Severity == ValidationSeverity.Error);
    /// <summary>Годно для представления: оно ПРОВЕРЯЛОСЬ и ошибок по нему нет (непроверенное — не годно).</summary>
    public bool IsValidFor(Representation r) =>
        Checked.Contains(r) && !Issues.Any(i => i.Where == r && i.Severity == ValidationSeverity.Error);
    /// <summary>Проверялось ли представление (отличает «не проверялось» от «проверено, ошибок нет»).</summary>
    public bool WasChecked(Representation r) => Checked.Contains(r);
    public int Errors => Issues.Count(i => i.Severity == ValidationSeverity.Error);

    internal void Merge(ValidationReport other)
    {
        Issues.AddRange(other.Issues);
        foreach (var kv in other.Metrics) Metrics[kv.Key] = kv.Value;
        Checked.UnionWith(other.Checked);
    }

    public string Summary()
    {
        var parts = Checked.OrderBy(r => r).Select(r =>
        {
            int e = Issues.Count(i => i.Where == r && i.Severity == ValidationSeverity.Error);
            return $"{r}: {(e == 0 ? "ok" : e + " error(s)")}";
        });
        return string.Join("; ", parts);
    }

    public override string ToString() =>
        Summary() + (Issues.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Issues));
}

/// <summary>
/// K01. ЕДИНАЯ ПРОВЕРКА ТЕЛА И ЕГО ПРОИЗВОДНЫХ ПРЕДСТАВЛЕНИЙ.
///
/// Прежде вердикт давала только TopologyCheck по B-rep. На сферическом вырезе она честно
/// выдавала ноль замечаний — тело было верным, — а выходная сетка была дырявой: десять сторон
/// без пары. Ни одна проверка ядра на сетку не смотрела, и расхождение представлений
/// оставалось невидимым.
///
/// Здесь одна процедура спрашивает каждое представление по одним правилам:
///   • B-rep: замечания TopologyCheck, окрестность каждой вершины — один веер граней, связность
///     оболочек; встречный обход полурёбер пока только считается (соглашение не приведено, K05);
///   • Exact: погашенность отрезков на прямых и кольца с T-стыками — кратность и встречность
///     каждой стороны, связность окрестности каждой точной вершины;
///   • Mesh: в тех float-координатах, что видит потребитель, — парность и встречность сторон,
///     окрестность вершин, целостность (пропавшие грани), ориентация компонент и вложенность
///     полостей; качество (выродившиеся треугольники) — предупреждение, не ошибка топологии;
///   • Geometry (по запросу): собственные пересечения сетки.
///
/// Несколько компонент и полости законны: требуется не «одна оболочка», а чтобы каждая
/// компонента была замкнута, наружные смотрели наружу, а полость лежала внутри наружной.
/// </summary>
public static class SolidValidator
{
    internal static readonly bool ShellStat = Environment.GetEnvironmentVariable("P3D_SHELLSTAT") == "1";
    /// <summary>Граница микроребра, мм: шаг округления внешней проверки A06 и сварки экспорта.</summary>
    public const double MicroEdge = 1e-5;

    /// <summary>Проверить тело; сетку — если передана, иначе построить показную.</summary>
    public static ValidationReport Validate(TSolid solid, MeshData? mesh = null, ValidationOptions? options = null)
    {
        options ??= new ValidationOptions();
        return ValidateCore(solid, mesh, options);
    }

    static ValidationReport Copy(ValidationReport r) { var c = new ValidationReport(); c.Merge(r); return c; }
    /// <summary>Раздел проверки из памяти: ревизия, точная форма (ссылка), настройки ядра — иначе заново.</summary>
    static ValidationReport Section(TSolid solid, string key, Func<ValidationReport> run)
    {
        if (ValCacheOff) return run();
        // 17:31: отложенная разметка (DeferMeshing) и сетка показа граней на проверки тела не влияют — без них в ключе проверка
        // детали после операции берёт разделы, только что посчитанные построителем (скругление: B-rep 2 × 0.9 с). Мутация
        // P3D_VALKEYDISPLAY=0 — прежний ключ.
        var ks = KernelSettings.Capture() with { Preview = false, LivePreview = false };
        if (!ValKeyDisplayOff) ks = ks with { DeferMeshing = false, BooleanThreads = 1, MeshThreads = 1 };   // число потоков результата не меняет
        // 02.6: ключ памяти — со всеми настройками, в т. ч. бюджетом точности (смена единиц/масштаба — новая проверка).
        // Мутация P3D_CACHEKEYTOL=0: ключ без бюджета точности.
        if (CacheKeyTolOff) ks = ks with { Tolerance = ToleranceContext.Default };
        long rev = solid.Revision; var ex = solid.Exact; ulong fp = TopoFingerprint.Of(solid, display: ValKeyDisplayOff);
        var e = _valCache.GetValue(solid, _ => new ValEntry());
        lock (e)
        {
            if (ValCacheDbg) Console.Error.WriteLine($"VALCACHE {key} solid {System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(solid)} rev {rev}/{e.Revision} fp {fp}/{e.Print} exact {ReferenceEquals(e.Exact, ex)} settings {(e.Settings == null ? "null" : e.Settings.Equals(ks) ? "same" : "diff:" + e.Settings + " vs " + ks)} has {e.Sections.ContainsKey(key)}");
            if (e.Revision != rev || e.Print != fp || !ReferenceEquals(e.Exact, ex) || e.Settings == null || !e.Settings.Equals(ks))
            { e.Sections.Clear(); e.Revision = rev; e.Print = fp; e.Exact = ex; e.Settings = ks; }
            if (e.Sections.TryGetValue(key, out var hit)) { Interlocked.Increment(ref ValidationCacheHits); return Copy(hit); }
        }
        var rep = run();
        if (solid.Revision == rev && ReferenceEquals(ex, solid.Exact) && TopoFingerprint.Of(solid, display: ValKeyDisplayOff) == fp)
            lock (e) if (e.Revision == rev && e.Print == fp && ReferenceEquals(e.Exact, ex)) e.Sections[key] = Copy(rep);
        return rep;
    }
    private sealed class ValEntry
    {
        public long Revision = long.MinValue; public ulong Print; public object? Exact; public KernelSettings? Settings;
        public readonly Dictionary<string, ValidationReport> Sections = new();
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TSolid, ValEntry> _valCache = new();
    static readonly bool ValKeyDisplayOff = Environment.GetEnvironmentVariable("P3D_VALKEYDISPLAY") == "0";
    internal static bool KeyDisplayOff => ValKeyDisplayOff;
    static readonly bool ValCacheDbg = Environment.GetEnvironmentVariable("P3D_VALCACHEDBG") == "1";
    static readonly bool ValCacheOff = Environment.GetEnvironmentVariable("P3D_VALCACHE") == "0";
    static readonly bool CacheKeyTolOff = Environment.GetEnvironmentVariable("P3D_CACHEKEYTOL") == "0";
    /// <summary>u11: разделов проверки тела из памяти (разбор).</summary>
    public static long ValidationCacheHits;

    static ValidationReport ValidateCore(TSolid solid, MeshData? mesh, ValidationOptions options)
    {
        using var _oh = Diagnostics.Overhead.Scope();
        var report = new ValidationReport();

        // u11: РАЗДЕЛЫ ПРОВЕРКИ — ИЗ ПАМЯТИ ПО ОТДЕЛЬНОСТИ. Деталь проверяет тело со своей сеткой показа (без точной формы), а
        // построитель только что проверил ту же ревизию целиком: B-rep и p-кривые не зависят от сетки — берутся готовыми
        // (0.3–0.7 с у процедурной ленты). Сетка, переданная извне, разбирается всегда. Мутация P3D_VALCACHE=0.
        string ok = $"{options.Closed}|{options.MaxListed}";
        if (options.CheckBRep) using (Diagnostics.Prof.Time("проверка: B-rep")) report.Merge(Section(solid, "brep|" + ok, () => ValidateBRep(solid, options)));
        if (options.CheckExact && solid.Exact != null) using (Diagnostics.Prof.Time("проверка: точное тело")) report.Merge(Section(solid, "exact|" + ok, () => ValidateExact(solid.Exact.Solid, options)));
        if (options.CheckBRep && options.CheckPCurves) using (Diagnostics.Prof.Time("проверка: p-кривые")) report.Merge(Section(solid, "pc|" + ok, () => ValidatePCurves(solid, options)));
        if (TorStat) TorusStat(solid);
        if (options.CheckMesh || options.CheckSelfIntersections)
        {
            if (mesh == null)
                report.Merge(Section(solid, $"mesh|{ok}|{options.CheckSelfIntersections}|{BitConverter.DoubleToInt64Bits(Core.Tol.Chord)}|{BitConverter.DoubleToInt64Bits(Core.Tol.ChordAngle)}", () =>
                {
                    MeshData m;
                    using (Diagnostics.Prof.Time("проверка: сетка тела")) m = Tessellator.Tessellate(solid);
                    using (Diagnostics.Prof.Time("проверка: сетка — разбор")) return ValidateMesh(m, options);
                }));
            else using (Diagnostics.Prof.Time("проверка: сетка — разбор")) report.Merge(ValidateMesh(mesh, options));
        }
        return report;
    }

    // ================================================================ B-rep

    public static ValidationReport ValidateBRep(TSolid solid, ValidationOptions? options = null)
    {
        options ??= new ValidationOptions();
        var r = new ValidationReport();
        r.Checked.Add(Representation.BRep);

        using (Diagnostics.Prof.Time("проверка: топология"))
        foreach (var i in TopologyCheck.Validate(solid, closed: options.Closed))
            r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "topology", i.ToString(), i.Id));

        var faces = solid.Faces.ToList();
        var edges = solid.Edges.ToList();
        r.Metrics["brep.faces"] = faces.Count;
        r.Metrics["brep.edges"] = edges.Count;
        r.Metrics["brep.shells"] = solid.Shells.Count;
        using (Diagnostics.Prof.Time("проверка: короткие рёбра")) CheckShortEdges(r, edges);
        using (Diagnostics.Prof.Time("проверка: ориентация контуров")) CheckLoopOrientation(r, faces);
        using (Diagnostics.Prof.Time("проверка: полюса")) CheckChartPoles(r, faces);
        using (Diagnostics.Prof.Time("проверка: связность контуров")) CheckLoopConnectivity(r, faces);
        using (Diagnostics.Prof.Time("проверка: компоненты граней")) CheckFaceComponents(r, faces);
        using (Diagnostics.Prof.Time("проверка: знак объёма")) CheckSignedVolume(r, faces);

        // Окрестность вершины: грани, сходящиеся в ней, связаны через рёбра этой вершины в один
        // веер. Два веера в одной вершине — касание тела самим собой в точке.
        if (options.Closed)
        {
            var around = new Dictionary<TVertex, List<TEdge>>();
            foreach (var e in edges)
                foreach (var v in new[] { e.V0, e.V1 }.Distinct())
                {
                    if (!around.TryGetValue(v, out var l)) around[v] = l = new List<TEdge>();
                    l.Add(e);
                }

            int pinched = 0;
            foreach (var (v, list) in around)
            {
                var parent = new Dictionary<TFace, TFace>();
                TFace Find(TFace f) { while (parent[f] != f) f = parent[f] = parent[parent[f]]; return f; }
                foreach (var e in list)
                    foreach (var ce in e.CoEdges)
                        if (ce.Loop?.Face is { } f) parent.TryAdd(f, f);
                foreach (var e in list)
                {
                    var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                    for (int k = 1; k < fs.Count; k++) parent[Find(fs[k])] = Find(fs[0]);
                }
                if (parent.Count > 0 && parent.Keys.Select(Find).Distinct().Count() > 1)
                {
                    if (pinched++ < options.MaxListed)
                        r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "vertex-pinch",
                            "грани вокруг вершины образуют не один веер", v.Id, v.P));
                }
            }
            if (pinched > options.MaxListed)
                r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "vertex-pinch",
                    $"всего вершин с несколькими веерами: {pinched}"));
            r.Metrics["brep.pinchedVertices"] = pinched;
        }

        // Оболочка — связное множество граней ПО ОБЫЧНОМУ СОГЛАШЕНИЮ. В этом ядре сборка тела из
        // многоугольников кладёт все компоненты (тело и полость, два несвязанных куска) в одну
        // TShell. Это соглашение представления, не дыра: замкнутость и ориентацию компонент
        // проверяет сетка. Число компонент на оболочку — показатель для K05, не ошибка.
        int shellComponents = 0;   // KRN-03 замер: P3D_SHELLSTAT=1 — откуда тела с несколькими компонентами в оболочке
        foreach (var sh in solid.Shells)
        {
            if (sh.Faces.Count == 0) continue;
            var seen = new HashSet<TFace> { sh.Faces[0] };
            var stack = new Stack<TFace>(seen);
            while (stack.Count > 0)
                foreach (var e in stack.Pop().Edges)
                    foreach (var ce in e.CoEdges)
                        if (ce.Loop?.Face is { } g && g.Shell == sh && seen.Add(g)) stack.Push(g);
            shellComponents++;
            if (seen.Count != sh.Faces.Count)
            {
                var rest = new HashSet<TFace>(sh.Faces.Where(f => !seen.Contains(f)));
                while (rest.Count > 0)
                {
                    shellComponents++;
                    var st = new Stack<TFace>(); var f0 = rest.First(); rest.Remove(f0); st.Push(f0);
                    while (st.Count > 0)
                        foreach (var e in st.Pop().Edges)
                            foreach (var ce in e.CoEdges)
                                if (ce.Loop?.Face is { } g && rest.Remove(g)) st.Push(g);
                }
                if (ShellStat) Console.Error.WriteLine($"SHELLMULTI faces {sh.Faces.Count} linked {seen.Count} shells {solid.Shells.Count} from {string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Skip(1).Select(fr => fr.GetMethod()).Where(m => m?.DeclaringType != null && m.DeclaringType.Namespace?.StartsWith("Parametric3D") == true && m.DeclaringType.Name != nameof(SolidValidator)).Take(4).Select(m => m!.DeclaringType!.Name + "." + m.Name))}");
                r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Info, "shell-components",
                    $"в одной оболочке несколько связных компонент: связано {seen.Count} граней из {sh.Faces.Count}", sh.Id));
            }
        }
        r.Metrics["brep.faceComponents"] = shellComponents;

        // Встречный обход полурёбер — только показатель: соглашение Reversed в ядре не приведено
        // к одному виду (K05), и требовать его сейчас значит браковать годные тела.
        int sameDir = 0;
        foreach (var e in edges)
            if (e.CoEdges.Count == 2 && e.CoEdges[0].Reversed == e.CoEdges[1].Reversed) sameDir++;
        r.Metrics["brep.coedgesSameDirection"] = sameDir;
        r.Metrics["brep.convention"] = solid.TopologyConvention;

        // K08: авторитетная геометрия. Кривая ребра, отходящая от смежной поверхности, — это
        // противоречие между 3D-кривой и поверхностью, а не «приближение»: ошибка. Доля
        // аналитических граней и рёбер и ошибка гранения — сведения для вызывающего кода.
        GeometryAudit geo; using (Diagnostics.Prof.Time("проверка: аудит геометрии")) geo = GeometryAudit.Of(solid);
        r.Metrics["geometry.analyticFaces"] = geo.AnalyticFaces;
        r.Metrics["geometry.analyticEdges"] = geo.AnalyticEdges;
        r.Metrics["geometry.facetedEdges"] = geo.Edges.Count(e => e.Level == GeometryLevel.Faceted);
        r.Metrics["geometry.maxChordError"] = geo.MaxChordError;
        // ОШИБКА. Прежде (до AnalyticEdges) на повёрнутом скруглении отрезок ребра отходил от
        // цилиндра на 0.08 мм: настоящее пересечение плоскости с цилиндром — дуга эллипса, а ребро
        // было записано отрезком по двум точкам сетки. Теперь такие рёбра получают точную кривую
        // сечения, и противоречие кривой с поверхностью снова считается браком.
        r.Metrics["geometry.inconsistentEdges"] = geo.InconsistentEdges;
        foreach (var e in geo.Edges.Where(e => e.Level == GeometryLevel.Inconsistent).Take(options.MaxListed))
            r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "curve-off-surface",
                $"кривая ребра ({e.Curve}) отходит от смежных поверхностей на {Math.Max(e.OnA, e.OnB):E2} мм", e.Edge));
        // K05: у тела, приведённого к соглашению, встречный обход обязателен.
        if (solid.TopologyConvention >= TopologyConventions.Version && options.Closed && sameDir > 0)
            r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "coedge-orientation",
                $"рёбер, обходимых обеими гранями в одну сторону: {sameDir}"));

        return r;
    }

    // ================================================================ KRN-03: p-кривые

    /// <summary>
    /// KRN-03: p-КРИВЫЕ КАЖДОГО ИСПОЛЬЗОВАНИЯ РЕБРА СОГЛАСОВАНЫ С 3D-КРИВОЙ И ПОВЕРХНОСТЬЮ В БЮДЖЕТЕ. Для точного тела
    /// (ExactAnalytic/ExactQuadric) отклонение S(pcurve(t)) от ребра — не больше max(модельная точность, допуск ребра), иначе
    /// ошибка pcurve-inconsistent; полуребро без p-кривой на точной поверхности — ошибка pcurve-missing. У прочих тел —
    /// метрики (согласованность ограничена гранением). Тело не правится.
    /// </summary>
    public static ValidationReport ValidatePCurves(TSolid solid, ValidationOptions? options = null)
    {
        options ??= new ValidationOptions();
        var r = new ValidationReport();
        r.Checked.Add(Representation.BRep);   // согласованность p-кривых — свойство B-rep
        bool exactBody = solid.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric;
        double acc = ToleranceContext.Current.ModelAccuracy;
        int exact = 0, numeric = 0, missing = 0, bad = 0; double worst = 0;
        foreach (var (ce, pc, dev) in PCurves.Measure(solid))
        {
            if (pc == null)
            {
                if (GeometryAudit.AuthoritativeSurface(ce.Loop!.Face!) != null) missing++;
                continue;
            }
            if (pc.IsExact) exact++; else numeric++;
            worst = Math.Max(worst, dev);
            double allow = Math.Max(acc, ce.Edge.Tolerance) * (1 + 1e-9);
            if (exactBody && dev > allow)
            {
                if (Environment.GetEnvironmentVariable("P3D_PCDBG") == "1")
                {
                    var sf = GeometryAudit.AuthoritativeSurface(ce.Loop!.Face!);
                    var c = ce.Edge.Curve;
                    Console.Error.WriteLine($"PCDBG edge {ce.Edge.Id} {c.GetType().Name} rev {ce.Reversed} surf {sf?.GetType().Name} {(sf is Surfaces.RevolvedSurface rv ? $"base {rv.Base.GetType().Name} A0 {rv.A0:G4} A1 {rv.A1:G4}" : "")} dev {dev:G3}");
                    for (int k = 0; k <= 8; k++) { double t = k / 8.0; var x = PCurves.At(ce, t); var uv = pc.PointAt(t); Console.Error.WriteLine($"   t {t:F3} xyz {x} uvP {uv} proj {sf?.ProjectToUV(x)} S(uv) {sf?.PointAt(uv)}"); }
                }
                bad++;
                if (bad <= options.MaxListed)
                    r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "pcurve-inconsistent",
                        $"p-кривая ({pc.GetType().Name}) отходит от ребра ({ce.Edge.Curve.GetType().Name}) на {dev:E2} мм при допуске {allow:E2}", ce.Edge.Id));
            }
        }
        if (exactBody && missing > 0)
            r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "pcurve-missing", $"полурёбер без p-кривой на точной поверхности: {missing}"));
        r.Metrics["pcurve.exact"] = exact; r.Metrics["pcurve.numeric"] = numeric; r.Metrics["pcurve.missing"] = missing;
        r.Metrics["pcurve.maxDeviation"] = worst; r.Metrics["pcurve.inconsistent"] = bad;
        return r;
    }

    // ================================================================ Exact

    public static ValidationReport ValidateExact(ExactSolid solid, ValidationOptions? options = null)
    {
        options ??= new ValidationOptions();
        var r = new ValidationReport();
        r.Checked.Add(Representation.Exact);

        int uncancelled = solid.UncancelledSegments();
        r.Metrics["exact.polygons"] = solid.Polygons.Count;
        r.Metrics["exact.uncancelledSegments"] = uncancelled;
        if (uncancelled != 0)
            r.Issues.Add(new ValidationIssue(Representation.Exact, ValidationSeverity.Error, "uncancelled",
                $"непогашенных отрезков на прямых рёбер: {uncancelled}"));

        // Ноль непогашенных отрезков недостаточен (A06: точечные самокасания). Кольца с
        // вставленными T-стыками проверяются как оболочка из многоугольников.
        var rings = ExactAdapter.Rings(solid.Registry, solid.Polygons);
        var shell = RingShell.Analyze(rings, options.Closed);
        shell.Report(r, Representation.Exact, options, v =>
        {
            var (x, y, z) = solid.Registry.ToMm(v);
            return new Vector3d(x, y, z);
        });
        return r;
    }

    // ================================================================ Mesh

    /// <summary>
    /// 20:17 (скорость): РАЗБОР СЕТКИ — ИЗ ПАМЯТИ ПО СОДЕРЖАНИЮ. Построитель разбирает сетку своего тела, деталь сразу после —
    /// сетку показа того же тела (грани — из памяти разметки, сетка та же до бита): 30–60 мс дважды. Ключ — точки (float, как
    /// их видит разбор), номера, неразмеченные грани и параметры разбора; при совпадении — копия отчёта. Мутация P3D_MESHVALMEMO=0.
    /// </summary>
    public static ValidationReport ValidateMesh(MeshData mesh, ValidationOptions? options = null)
    {
        options ??= new ValidationOptions();
        if (MeshValMemoOff) return ValidateMeshCore(mesh, options);
        ulong h = 1469598103934665603UL;
        void Mix(ulong x) { h ^= x + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2); h *= 1099511628211UL; }
        Mix((ulong)mesh.Vertices.Length); Mix((ulong)mesh.Indices.Length);
        foreach (var v in mesh.Vertices) { Mix(BitConverter.SingleToUInt32Bits(v.P.X) | ((ulong)BitConverter.SingleToUInt32Bits(v.P.Y) << 32)); Mix(BitConverter.SingleToUInt32Bits(v.P.Z)); }
        foreach (int i in mesh.Indices) Mix((ulong)(uint)i);
        foreach (var (f, why) in mesh.BrokenFaces) { Mix(f); Mix((ulong)why.GetHashCode()); }
        var key = (h, $"{options.Closed}|{options.MaxListed}|{options.CheckSelfIntersections}|{TJunctionSeverity}|{ToleranceContext.Current.ModelAccuracy:R}");
        if (_meshValMemo.TryGetValue(key, out var hit)) { Interlocked.Increment(ref MeshValMemoHits); return Copy(hit); }
        var rep = ValidateMeshCore(mesh, options);
        if (_meshValMemo.Count > 256) _meshValMemo.Clear();
        _meshValMemo[key] = Copy(rep);
        return rep;
    }
    static readonly bool MeshValMemoOff = Environment.GetEnvironmentVariable("P3D_MESHVALMEMO") == "0";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, string), ValidationReport> _meshValMemo = new();
    /// <summary>Разбор: отчётов разбора сетки из памяти.</summary>
    public static long MeshValMemoHits;

    static ValidationReport ValidateMeshCore(MeshData mesh, ValidationOptions options)
    {
        var r = new ValidationReport();
        r.Checked.Add(Representation.Mesh);

        r.Metrics["mesh.triangles"] = mesh.Indices.Length / 3;

        foreach (var (face, why) in mesh.BrokenFaces.Take(options.MaxListed))
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Error, "face-missing",
                $"грань не размечена: {why}", face));
        if (mesh.BrokenFaces.Count > options.MaxListed)
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Error, "face-missing",
                $"всего неразмеченных граней: {mesh.BrokenFaces.Count}"));

        if (mesh.Indices.Length % 3 != 0 || mesh.Indices.Any(i => i < 0 || i >= mesh.Vertices.Length))
        {
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Error, "indices",
                "номера вершин вне диапазона или неполный треугольник"));
            return r;
        }
        if (mesh.Vertices.Any(v => !float.IsFinite(v.P.X) || !float.IsFinite(v.P.Y) || !float.IsFinite(v.P.Z)))
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Error, "nonfinite",
                "нечисловые координаты вершин"));
        if (mesh.Indices.Length == 0)
        {
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Error, "empty", "сетка пуста"));
            return r;
        }

        // Тождество вершин — точное совпадение float-координат: именно их получает потребитель
        // (экран, STL, расчёт). Номера вершин у разных граней разные, нормали разные — это
        // законный разрыв нормалей и шов, не дыра.
        var ids = new Dictionary<(float, float, float), int>();
        var pts = new List<Vector3d>();
        int Id(int i)
        {
            var p = mesh.Vertices[i].P;
            var k = (p.X, p.Y, p.Z);
            if (!ids.TryGetValue(k, out int at)) { ids[k] = at = pts.Count; pts.Add(new Vector3d(p.X, p.Y, p.Z)); }
            return at;
        }

        var tris = new List<int[]>(mesh.Indices.Length / 3);
        int collapsed = 0;
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int a = Id(mesh.Indices[i]), b = Id(mesh.Indices[i + 1]), c = Id(mesh.Indices[i + 2]);
            if (a == b || b == c || c == a) { collapsed++; continue; }
            tris.Add(new[] { a, b, c });
        }
        r.Metrics["mesh.vertices"] = pts.Count;
        r.Metrics["mesh.collapsedTriangles"] = collapsed;

        int zeroArea = tris.Count(t => Vector3d.Cross(pts[t[1]] - pts[t[0]], pts[t[2]] - pts[t[0]]).LengthSquared == 0);
        r.Metrics["mesh.zeroAreaTriangles"] = zeroArea;
        if (zeroArea + collapsed > 0)
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Warning, "degenerate",
                $"треугольников нулевой площади: {zeroArea}, со сведёнными вершинами: {collapsed} (качество, не топология)"));

        var shell = RingShell.Analyze(tris, options.Closed);
        shell.Report(r, Representation.Mesh, options, v => pts[v]);

        // Микрорёбра: разные вершины ближе допуска сварки внешних получателей. Это не дыра в
        // этой сетке, но получатель, сваривающий по допуску (STL-импорт, расчётный пакет),
        // сведёт их и увидит выродившиеся треугольники. Сводятся они только с учётом топологии.
        double shortest = double.MaxValue; int micro = 0;
        var counted = new HashSet<(int, int)>();
        foreach (var t in tris)
            for (int k = 0; k < 3; k++)
            {
                int a = t[k], b = t[(k + 1) % 3];
                if (!counted.Add(a < b ? (a, b) : (b, a))) continue;
                double len = (pts[a] - pts[b]).Length;
                shortest = Math.Min(shortest, len);
                if (len < MicroEdge) micro++;
            }
        r.Metrics["mesh.shortestEdge"] = shortest == double.MaxValue ? 0 : shortest;
        r.Metrics["mesh.microEdges"] = micro;
        if (micro > 0)
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Warning, "micro-edge",
                $"рёбер короче {MicroEdge:0.#####} мм: {micro}, кратчайшее {shortest:E2} мм (сварка по допуску у получателя их сведёт)"));

        if (options.Closed && shell.CleanEdges) CheckComponents(r, tris, pts, shell, options);

        if (options.CheckSelfIntersections)
        {
            r.Checked.Add(Representation.Geometry);
            var st = ClassifyPairs(tris, pts);
            int Count(PairClass c) => st.Counts[(int)c];
            int hits = Count(PairClass.Transversal) + Count(PairClass.CoplanarOverlap) + Count(PairClass.Duplicate);
            r.Metrics["geometry.selfIntersections"] = hits;
            r.Metrics["geometry.pairs.transversal"] = Count(PairClass.Transversal);
            r.Metrics["geometry.pairs.coplanarOverlap"] = Count(PairClass.CoplanarOverlap);
            r.Metrics["geometry.pairs.tJunction"] = Count(PairClass.TJunction);
            r.Metrics["geometry.pairs.duplicate"] = Count(PairClass.Duplicate);
            r.Metrics["geometry.pairs.adjacent"] = Count(PairClass.Adjacent);
            r.Metrics["geometry.pairs.tested"] = st.Tested;
            void Add(PairClass c, ValidationSeverity sev, string code, string what)
            {
                if (Count(c) > 0)
                    r.Issues.Add(new ValidationIssue(Representation.Geometry, sev, code, $"{what}: {Count(c)}", null, st.First[(int)c]));
            }
            Add(PairClass.Transversal, ValidationSeverity.Error, "self-intersection", "пересекающихся пар треугольников");
            Add(PairClass.CoplanarOverlap, ValidationSeverity.Error, "coplanar-overlap", "пар треугольников, наложенных в одной плоскости");
            Add(PairClass.Duplicate, ValidationSeverity.Error, "duplicate-face", "пар совпадающих треугольников (двойная грань, плёнка)");
            Add(PairClass.TJunction, TJunctionSeverity, "t-junction", "касаний без общей вершины (T-стык: вершина на чужом треугольнике)");
        }
        return r;
    }

    /// <summary>
    /// KRN-03: ВЫРОЖДЕНИЯ И МИКРОРЁБРА. Ребро короче модельной точности законно только как сингулярность — вершина на оси
    /// поверхности вращения смежной грани (полюс сферы, вершина конуса); иначе ошибка degenerate-edge. Ребро короче
    /// минимального элемента — предупреждение micro-edge (вне поддержанной области). Длина — по кривой (замкнутое ребро с
    /// одной вершиной — не нулевое).
    /// </summary>
    static void CheckShortEdges(ValidationReport r, List<TEdge> edges)
    {
        var budget = ToleranceContext.Current;
        double shortest = double.MaxValue; int micro = 0, singular = 0;
        foreach (var e in edges)
        {
            double len = 0;
            if (e.Curve is { } c)
            {
                var prev = c.PointAt(c.T0);
                for (int k = 1; k <= 8; k++) { var p = c.PointAt(c.T0 + (c.T1 - c.T0) * k / 8); len += (p - prev).Length; prev = p; }
            }
            else len = (e.V1.P - e.V0.P).Length;
            if (len <= budget.ModelAccuracy)
            {
                bool onAxis = e.CoEdges.Select(ce => ce.Loop?.Face?.Surface).Any(sf => OnRevolveAxis(sf, e.V0.P));
                if (onAxis) { singular++; continue; }
                r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "degenerate-edge",
                    $"ребро длиной {len:E2} мм (меньше модельной точности {budget.ModelAccuracy:E1}) — не сингулярность поверхности", e.Id));
                continue;
            }
            shortest = Math.Min(shortest, len);
            if (len < budget.MinFeature)
            {
                micro++;
                r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Warning, "micro-edge",
                    $"ребро длиной {len:E2} мм короче минимального элемента {budget.MinFeature:E1} мм — вне поддержанной области", e.Id));
            }
        }
        r.Metrics["brep.shortestEdge"] = shortest == double.MaxValue ? 0 : shortest;
        r.Metrics["brep.microEdges"] = micro;
        r.Metrics["brep.singularEdges"] = singular;
    }

    /// <summary>
    /// KRN-03: КОНТУРЫ КРИВОЙ ГРАНИ СОГЛАСОВАНЫ С ЕЁ НОРМАЛЬЮ. Векторная площадь по контурам (½∮ x × dx, по настоящим кривым)
    /// и по фасетам грани (ориентированы по нормали) совпадают в пределах гранения. Прежде не проверялось: острие сверла
    /// с контуром, развёрнутым против нормали (точка пробы стороны за вершиной конуса), давало точный результат с
    /// неверным аналитическим объёмом (47202.5 вместо 47457.1) при «BRep: ok; Mesh: ok». Плоские грани проверяет роль колец.
    /// </summary>
    /// <summary>
    /// KRN-03: КОНТРАКТ КАРТЫ (SurfaceDomain) — полюс карты поверхности вращения не лежит внутри ребра грани (ближе
    /// MinFeature); вершина точно в полюсе — законна. Ошибка: p-кривая такого ребра в карте разрывна.
    /// </summary>
    static void CheckChartPoles(ValidationReport r, List<TFace> faces)
    {
        double mf = ToleranceContext.Current.MinFeature, min = double.PositiveInfinity; int bad = 0;
        // Ошибка — у точного тела (его геометрия — карта поверхности и p-кривые); у многогранного (геометрия — фасеты, карта
        // источника справочная) — предупреждение: прежде многогранный вырез шаром через полюс карты отвергался (регрессия ч.7,
        // найдена проверкой ворот g.06).
        var solid = faces.FirstOrDefault()?.Shell?.Solid;
        // предупреждение — только у тела, ЯВНО помеченного многогранным/приближённым: в маршруте точной булевой уровень ещё не
        // поставлен (Unspecified), и такое тело обязано пройти контракт (иначе карта с полюсом на ребре проходила, мутация keep)
        // ни по уровню (в маршрутах он ещё не поставлен), а по устройству: у многогранного тела авторитетна точная
        // многогранная форма (solid.Exact) — геометрия задана ею, карта поверхности-источника справочная
        bool polyhedral = solid != null && ((solid.Exact != null && solid.ExactAuthoritative) || solid.Guarantee is GuaranteeLevel.ExactPolyhedral or GuaranteeLevel.Approximate);
        var sev = polyhedral ? ValidationSeverity.Warning : ValidationSeverity.Error;
        foreach (var f in faces)
        {
            if (f.Surface is Surfaces.FacetedSurface fs && fs.Source == null) continue;
            var s = GeometryAudit.AuthoritativeSurface(f);
            if (SurfaceDomain.Poles(s).Count == 0) continue;
            double c = SurfaceDomain.PoleClearance(f, s);
            min = Math.Min(min, c);
            if (c < mf)
            {
                bad++;
                r.Issues.Add(new ValidationIssue(Representation.BRep, sev, "pole-on-boundary",
                    $"полюс карты поверхности на ребре грани: {c:E2} мм (MinFeature {mf:E0})", f.Id));
            }
        }
        if (!double.IsPositiveInfinity(min)) r.Metrics["brep.minPoleClearance"] = min;
        r.Metrics["brep.poleOnBoundary"] = bad;
    }

    /// <summary>
    /// KRN-03 (03.2): ГРАНЬ — ОДНА СВЯЗНАЯ КОМПОНЕНТА. У плоской грани контуры с положительной площадью (против часовой
    /// вокруг наружной нормали) — внешние; два и больше — грань из нескольких несвязных кусков. ПОКА — замер
    /// (metric brep.multiComponentFaces; P3D_FACECOMPLOG=1 — печать); нарушение — ошибка проверки.
    /// </summary>
    static readonly bool FaceCompLog = Environment.GetEnvironmentVariable("P3D_FACECOMPLOG") == "1";
    static readonly bool FaceCompErrOff = Environment.GetEnvironmentVariable("P3D_FACECOMPERR") == "0";
    static void CheckFaceComponents(ValidationReport r, List<TFace> faces)
    {
        int bad = 0;
        var solid = faces.FirstOrDefault()?.Shell?.Solid;
        if (solid == null || solid.TopologyConvention < TopologyConventions.Version) return;   // соглашение ещё не применено
        foreach (var f in faces)
        {
            if (f.Loops.Count < 2) continue;
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src is not Surfaces.PlaneSurface) continue;
            // наружная нормаль — как у PlanarTweak.OutwardNormal: у гранёной грани сторону несут треугольники
            if (Ops.PlanarTweak.OutwardNormal(f) is not { } n) continue;
            int outer = 0;
            foreach (var l in f.Loops)
            {
                var pts = new List<Vector3d>();
                foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                Vector3d acc = Vector3d.Zero;
                for (int i = 0; i < pts.Count; i++) acc += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                if (Vector3d.Dot(acc, n) > 0) outer++;
            }
            if (outer > 1)
            {
                bad++; if (FaceCompLog) Console.Error.WriteLine($"FACECOMP face {f.Id} tag {f.Tag} loops {f.Loops.Count} outer {outer}");
                // KRN-03 (03.2): ГРАНЬ — ОДНА СВЯЗНАЯ КОМПОНЕНТА (правило, а не замер: на корпусе allA/allB/A06 нарушений 0).
                // Мутация P3D_FACECOMPERR=0 — только замер.
                if (!FaceCompErrOff) r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "face-components",
                    $"плоская грань {f.Id} из {outer} несвязных кусков (внешних контуров)", f.Id));
            }
        }
        r.Metrics["brep.multiComponentFaces"] = bad;
    }

    /// <summary>
    /// KRN-03 (03.2): КОНТУР — ОДИН СВЯЗНЫЙ ЦИКЛ. Конец каждого полуребра — начало следующего (по кругу). Контур из нескольких
    /// замкнутых кривых (пояс тора: верхняя и нижняя окружности в одном контуре) — не цикл: область грани им не задана
    /// однозначно, разметчик в параметрах отказывает. ПОКА — замер (metric brep.disconnectedLoops), не ошибка.
    /// </summary>
    static void CheckLoopConnectivity(ValidationReport r, List<TFace> faces)
    {
        int bad = 0;
        var solid = faces.FirstOrDefault()?.Shell?.Solid;
        if (solid == null || solid.TopologyConvention < TopologyConventions.Version) return;   // соглашение ещё не применено
        foreach (var f in faces)
            foreach (var l in f.Loops)
            {
                var ces = l.CoEdges.ToList();
                if (ces.Count == 1 && !ReferenceEquals(ces[0].Edge.V0, ces[0].Edge.V1))
                {
                    // g.04: контур из одного незамкнутого полуребра — не контур (прежде не проверялся)
                    r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "open-loop",
                        $"контур грани {f.Id} из одного полуребра не замкнут", f.Id));
                    bad++; continue;
                }
                if (ces.Count < 2) continue;
                // начало и конец полуребра — по геометрии обхода (флаг Reversed — относительно направления КРИВОЙ ребра,
                // а она может идти от V1 к V0): ближайшая вершина ребра к точке обхода t = 0 / t = 1
                // соглашение версии 1: Reversed — обход от V1 к V0
                TVertex Start(TCoEdge c) => c.Reversed ? c.Edge.V1 : c.Edge.V0;
                TVertex End(TCoEdge c) => c.Reversed ? c.Edge.V0 : c.Edge.V1;
                bool ok = true;
                for (int i = 0; i < ces.Count && ok; i++) ok = ReferenceEquals(End(ces[i]), Start(ces[(i + 1) % ces.Count]));
                if (!ok)
                {
                    bad++;
                    // сколько циклов получится, если переставить полурёбра: из каждого начала идти по концам
                    var byStart = ces.GroupBy(Start).ToDictionary(g => g.Key, g => g.ToList());
                    var left = new HashSet<TCoEdge>(ces); int cycles = 0;
                    while (left.Count > 0)
                    {
                        var c0 = left.First(); left.Remove(c0); cycles++; var cur = c0;
                        for (int guard = 0; guard < ces.Count; guard++)
                        {
                            var nx = byStart.TryGetValue(End(cur), out var cand) ? cand.FirstOrDefault(left.Contains) : null;
                            if (nx == null) break; left.Remove(nx); cur = nx;
                        }
                    }
                    if (LoopStat) Console.Error.WriteLine($"LOOPDISC cycles {cycles} {f.Tag} {Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))?.GetType().Name} ce {ces.Count} closedEdges {ces.Count(c => ReferenceEquals(c.Edge.V0, c.Edge.V1))} loops {f.Loops.Count}");
                }
            }
        r.Metrics["brep.disconnectedLoops"] = bad;
    }
    /// <summary>
    /// KRN-03 (03.12): ТЕЛО НЕ ВЫВЕРНУТО — по B-rep, без сетки. Точный объём (теорема о дивергенции по граням и контурам) у
    /// тела, приведённого к соглашению, положителен. Пояс тора с неверно выбранной стороной давал −3426 мм³ при «BRep: ok».
    /// </summary>
    static void CheckSignedVolume(ValidationReport r, List<TFace> faces)
    {
        var solid = faces.FirstOrDefault()?.Shell?.Solid;
        if (solid == null || solid.TopologyConvention < TopologyConventions.Version) return;
        double? v;
        if (VolDbg) Console.WriteLine($"VOLSIGN solid {System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(solid):x} rev {solid.Revision} fp {TopoFingerprint.Of(solid):x} {string.Join(" < ", Environment.StackTrace.Split('\n').Skip(3).Take(4).Select(l => l.Trim().Split(" in ")[0]))}");
        try { v = AnalyticVolume.Of(solid); } catch { return; }
        if (v is not { } vol || double.IsNaN(vol)) return;
        r.Metrics["brep.exactVolume"] = vol;
        if (vol < 0)
            r.Issues.Add(new ValidationIssue(Representation.BRep, ValidationSeverity.Error, "inverted-solid",
                $"точный объём тела отрицателен ({vol:G8}): контуры и стороны граней вывернуты", null));
    }

    static readonly bool VolDbg = Environment.GetEnvironmentVariable("P3D_VOLDBG") == "1";
    internal static readonly bool LoopStat = Environment.GetEnvironmentVariable("P3D_LOOPSTAT") == "1";

    /// <summary>KRN-03 замер (P3D_TORSTAT=1): торические грани — контуры, отклонение p-кривых, разметчик и причина отказа.</summary>
    internal static readonly bool TorStat = Environment.GetEnvironmentVariable("P3D_TORSTAT") == "1";
    static void TorusStat(TSolid solid)
    {
        List<(TCoEdge Ce, Curves.ICurve2? PC, double Dev)>? pcs = null;
        foreach (var f in solid.Faces)
        {
            var s = GeometryAudit.AuthoritativeSurface(f);
            if (Geometry.ExactEdges.ShapeOf(s) is not Geometry.Ssi.Torus t) continue;
            pcs ??= PCurves.Measure(solid);
            double dev = pcs.Where(x => x.Ce.Loop?.Face == f).Select(x => x.PC == null ? double.PositiveInfinity : x.Dev).DefaultIfEmpty(0).Max();
            var uses = f.Loops.SelectMany(l => l.CoEdges).GroupBy(c => c.Edge).Count(g => g.Count() > 1);
            string uvm; try { uvm = Tess.UvMesher.Mesh(f, Core.Tol.Chord, out var w) is { } m ? $"ok {m.T.Count / 3}" : "null: " + w; } catch (Exception ex) { uvm = "exception " + ex.Message; }
            string em; try { em = Tess.ExactFaceMesher.Mesh(f, out var w2) is { } m2 ? $"ok {m2.T.Count / 3}" : "null: " + w2; } catch (Exception ex) { em = "exception " + ex.Message; }
            Console.Error.WriteLine($"TORFACE R {t.R:G4} r {t.r:G4} loops {f.Loops.Count} coedges {f.Loops.Sum(l => l.CoEdges.Count)} seams {uses} faceted {(f.Surface is Surfaces.FacetedSurface)} pcurveDev {dev:E2} uvmesher [{uvm}] exact [{em}] guarantee {solid.Guarantee}");
        }
    }

    static void CheckLoopOrientation(ValidationReport r, List<TFace> faces)
    {
        // Ошибка — у тела, построенного ядром точно (не пропустить неверный аналитический результат); у прочих (импорт IGES:
        // окружности стенки отверстия в одну сторону — известный дефект KRN-09) — предупреждение: видно, но не ломает.
        var solid = faces.FirstOrDefault()?.Shell?.Solid;
        var sev = solid?.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric ? ValidationSeverity.Error : ValidationSeverity.Warning;
        int bad = 0;
        foreach (var f in faces)
        {
            if (f.Surface is not Surfaces.FacetedSurface fs || fs.Facets.Count == 0 || f.Surface.IsPlanar) continue;
            if (f.Loops.Count == 0 || f.Loops.All(l => l.CoEdges.Count == 0)) continue;
            var nf = Vector3d.Zero; double area = 0;
            foreach (var (a, b, c) in fs.Facets) { var x = Vector3d.Cross(b - a, c - a) / 2; nf += x; area += x.Length; }
            Vector3d nl;
            try { nl = AnalyticVolume.NormalIntegral(f); } catch { continue; }
            if (double.IsNaN(nl.X) || area <= 0) continue;
            // сравнение — только при заметной векторной площади (у полосы и крохотной грани она ~0: относительный порог
            // неустойчив); ошибка — расхождение больше половины (переворот: −N; полоса с однонаправленными кольцами: 2πR²
            // при нуле по фасетам)
            double big = Math.Max(nl.Length, nf.Length);
            if (big > 0.1 * area && (nl - nf).Length > 0.5 * big)
            {
                bad++;
                if (bad <= 5)
                    r.Issues.Add(new ValidationIssue(Representation.BRep, sev, "loop-orientation",
                        $"контуры грани {f.Tag} ({(f.Surface is Surfaces.FacetedSurface fz ? fz.Source?.GetType().Name : f.Surface.GetType().Name)}, rev {f.Reversed}, контуров {f.Loops.Count}) не согласованы с её нормалью: векторная площадь по контурам {nl:0.####}, по фасетам {nf:0.####} (площадь {area:G6})", f.Id));
            }
        }
        // KRN-03 (03.12): ТОЧНЫЕ КРИВЫЕ ГРАНИ (цилиндр, конус, шар) — БЕЗ ФАСЕТ: у середины ребра контура шаг в сторону n × t
        // (влево по обходу, если смотреть с наружной нормали) обязан вести внутрь грани, шаг вправо — наружу; принадлежность —
        // чётностью пересечений в параметрах грани (от обхода не зависит). Ребро шва (дважды в грани) и неоднозначный шаг
        // (оба внутри или оба снаружи — узкая грань) не голосуют. Мутация P3D_LOOPEXACT=0.
        // только у тела, приведённого к соглашению о полурёбрах (обход против часовой у наружной нормали): у сырого примитива
        // вращения (конус) все контуры обходят по часовой — соглашение ему ещё не применено
        if (!LoopExactOff && solid != null && solid.TopologyConvention >= TopologyConventions.Version)
            foreach (var f in faces)
            {
                // грань с фасетами: прежде — только проверка по фасетам (выше); 2026-10-19 (вечер) — и по её точной поверхности
                // (источнику фасет), если он есть: нормаль — от источника, не от фасет. P3D_LOOPFACETED=0 — как прежде.
                if (f.Surface is Surfaces.FacetedSurface { Source: var fsrc } && (fsrc == null || LoopFacetedOff)) continue;
                if (GeometryAudit.AuthoritativeSurface(f) is not { IsPlanar: false } || f.Loops.Count == 0) continue;
                if (LoopAgainstNormal(f) is not { } why) continue;
                bad++;
                if (bad <= 5) r.Issues.Add(new ValidationIssue(Representation.BRep, sev, "loop-orientation",
                    $"контуры грани {f.Tag} ({f.Surface.GetType().Name}, rev {f.Reversed}) не согласованы с её нормалью по точной поверхности: {why}", f.Id));
            }
        r.Metrics["brep.loopOrientationErrors"] = bad;
    }
    static readonly bool LoopExactOff = Environment.GetEnvironmentVariable("P3D_LOOPEXACT") == "0";
    // грани с фасетами — по точной поверхности-источнику; P3D_LOOPFACETED=0 — только по фасетам (как прежде)
    static readonly bool LoopFacetedOff = Environment.GetEnvironmentVariable("P3D_LOOPFACETED") == "0";
    static readonly bool LoopDbg = Environment.GetEnvironmentVariable("P3D_LOOPDBG") == "1";

    /// <summary>Контур грани против её нормали (по точной поверхности) — причина; согласован или не решить — null.</summary>
    internal static string? LoopAgainstNormal(TFace f)
    {
        Analytic.AnalyticFace? af;
        try { af = Analytic.AnalyticFace.Of(f, out _); } catch { return null; }
        // цилиндр, конус, шар (диск вращения — нет: плоская форма, у плоских граней своя проверка ролей колец)
        if (af == null || af.Band || af.Param || af.Tor || af.FullSurface || !(af.Cyl || af.Sph)) return null;
        var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
        int against = 0, agree = 0;
        int? facetSign = null;
        foreach (var l in f.Loops)
            foreach (var ce in l.CoEdges)
            {
                if (uses[ce.Edge] != 1) continue;
                var c = ce.Edge.Curve; double t0 = c.T0, t1 = c.T1, tm = (t0 + t1) / 2, h = (t1 - t0) * 1e-4;
                var p = c.PointAt(tm); var tg = (c.PointAt(tm + h) - c.PointAt(tm - h)) * (ce.Reversed ? -1 : 1);
                double len = (c.PointAt(t1) - c.PointAt(t0)).Length + (c.PointAt(tm) - c.PointAt(t0)).Length;
                if (tg.LengthSquared < 1e-30 || len < 1e-6) continue;
                // нормаль — точной поверхности (у грани с фасетами — их источника), со знаком грани
                Vector3d n;
                try
                {
                    var sf = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface; n = sf.NormalAt(sf.ProjectToUV(p));
                    // сторона: у точной грани — флаг Reversed; у грани с фасетами — согласие фасет с источником (по всей грани,
                    // а не по одной опорной нормали: у широкой кривой грани одна нормаль сторону не задаёт)
                    // (флаг Reversed грани поверх стороны поверхности — у обеих, как в TFace.NormalAt)
                    if (f.Surface is Surfaces.FacetedSurface ffs) { facetSign ??= FacetSign(ffs); if (facetSign == 0) return null; n *= facetSign.Value; }
                    if (f.Reversed) n = -n;
                }
                catch { continue; }
                var side = Vector3d.Cross(n, tg); if (side.LengthSquared < 1e-30) continue; side.Normalize();
                double eps = Math.Max(1e-3 * len, 100 * ToleranceContext.Current.ModelAccuracy);
                bool? left = af.Contains(af.ProjectTo(p + side * eps)), right = af.Contains(af.ProjectTo(p - side * eps));
                if (LoopDbg)
                {
                    string cls = "";
                    try
                    {
                        if (f.Shell?.Solid is { } sol)
                        {
                            var pb = Query.PreparedBody.For(sol);
                            var ql = p + side * eps; var qr = p - side * eps; double dn = eps * 0.5;
                            cls = $" L(in {pb.Classify(ql - n * dn)} out {pb.Classify(ql + n * dn)}) R(in {pb.Classify(qr - n * dn)} out {pb.Classify(qr + n * dn)})";
                        }
                    }
                    catch (Exception ex) { cls = " cls " + ex.GetType().Name; }
                    if (af.Cyl && left == false && right == true)
                    {
                        var ql = af.ProjectTo(p + side * eps); var qr = af.ProjectTo(p - side * eps);
                        Console.Error.WriteLine($"LOOPSEG left th {af.Theta(ql):R} h {af.H(ql):R}; right th {af.Theta(qr):R} h {af.H(qr):R}; p th {af.Theta(p):R} h {af.H(p):R}");
                        foreach (var sg in af.Bounds) Console.Error.WriteLine($"LOOPSEG  kind {sg.Kind} A {sg.A} B {sg.B} C {sg.C} R {sg.R} T0 {sg.T0} sw {sg.Sweep}");
                    }
                    Console.Error.WriteLine($"LOOPX {f.Tag} cyl {af.Cyl} cone {af.Cone} sph {af.Sph} faceted {f.Surface is Surfaces.FacetedSurface} sign {facetSign} rev {f.Reversed} edge {c.GetType().Name} p {p} n {n} side {side} eps {eps:G3} left {left} right {right}{cls}");
                }
                if (left == true && right == false) agree++;
                else if (left == false && right == true) against++;
                break;                                                     // одно ребро на контур
            }
        return against > 0 && agree == 0 ? $"{against} контур(ов) — материал справа от обхода" : null;
    }

    /// <summary>Сторона фасет относительно нормали источника: +1 / −1; 0 — не согласованы (решить нельзя).</summary>
    static int FacetSign(Surfaces.FacetedSurface fs)
    {
        if (fs.Source is not { } src) return 0;
        double pos = 0, neg = 0;
        foreach (var (a, b, c) in fs.Facets)
        {
            var x = Vector3d.Cross(b - a, c - a); double ar = x.Length; if (ar < 1e-30) continue;
            Vector3d ns; try { ns = src.NormalAt(src.ProjectToUV((a + b + c) / 3)); } catch { continue; }
            if (ns.LengthSquared < 1e-30) continue;
            if (Vector3d.Dot(x, ns) > 0) pos += ar; else neg += ar;
        }
        return pos > 9 * neg ? 1 : neg > 9 * pos ? -1 : 0;
    }

    static bool OnRevolveAxis(Surfaces.ISurface3? sf, OpenTK.Mathematics.Vector3d p)
    {
        var src = sf is Surfaces.FacetedSurface fs ? fs.Source : sf;
        if (src is not Surfaces.RevolvedSurface rv) return false;
        var w = rv.Axis.W.Normalized(); var d = p - rv.Axis.Origin;
        return (d - w * OpenTK.Mathematics.Vector3d.Dot(d, w)).Length <= 10 * ToleranceContext.Current.ModelAccuracy;
    }

    /// <summary>
    /// Компоненты связности: каждая замкнутая компонента — наружная оболочка (объём больше
    /// нуля) или полость (меньше нуля). Полость законна только внутри наружной оболочки.
    /// </summary>
    private static void CheckComponents(ValidationReport r, List<int[]> tris, List<Vector3d> pts,
                                        RingShell shell, ValidationOptions options)
    {
        var comp = shell.Components;
        int n = comp.Length == 0 ? 0 : comp.Max() + 1;
        var vol = new double[n];
        var anchor = new Vector3d?[n];
        for (int t = 0; t < tris.Count; t++)
        {
            int c = comp[t];
            anchor[c] ??= pts[tris[t][0]];
            var o = anchor[c]!.Value;
            vol[c] += Vector3d.Dot(pts[tris[t][0]] - o, Vector3d.Cross(pts[tris[t][1]] - o, pts[tris[t][2]] - o)) / 6;
        }
        r.Metrics["mesh.components"] = n;
        r.Metrics["mesh.volume"] = vol.Sum();
        r.Metrics["mesh.cavities"] = vol.Count(v => v < 0);

        if (vol.Sum() <= 0)
            r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Error, "inside-out",
                $"суммарный объём {vol.Sum():0.###} не положителен: оболочка вывернута"));

        for (int c = 0; c < n; c++)
        {
            if (vol[c] >= 0) continue;
            // Полость: её точка обязана лежать внутри какой-то другой компоненты (нечётное число
            // пересечений луча с прочими компонентами).
            var probe = anchor[c]!.Value;
            int crossings = RayCrossings(tris, pts, comp, c, probe);
            if (crossings % 2 == 0)
                r.Issues.Add(new ValidationIssue(Representation.Mesh, ValidationSeverity.Error, "orphan-cavity",
                    $"компонента с отрицательным объёмом {vol[c]:0.###} не лежит внутри тела: вывернутая оболочка", null, probe));
        }
    }

    private static int RayCrossings(List<int[]> tris, List<Vector3d> pts, int[] comp, int skip, Vector3d o)
    {
        // Направление «неудобное», чтобы луч не шёл вдоль рёбер и граней по осям.
        var d = new Vector3d(0.5773672, 0.5773229, 0.5773601).Normalized();
        int count = 0;
        for (int t = 0; t < tris.Count; t++)
        {
            if (comp[t] == skip) continue;
            Vector3d a = pts[tris[t][0]], b = pts[tris[t][1]], c = pts[tris[t][2]];
            Vector3d e1 = b - a, e2 = c - a, p = Vector3d.Cross(d, e2);
            double det = Vector3d.Dot(e1, p);
            if (Math.Abs(det) < 1e-18) continue;
            double inv = 1 / det;
            Vector3d s = o - a;
            double u = Vector3d.Dot(s, p) * inv; if (u < 0 || u > 1) continue;
            Vector3d q = Vector3d.Cross(s, e1);
            double v = Vector3d.Dot(d, q) * inv; if (v < 0 || u + v > 1) continue;
            if (Vector3d.Dot(e2, q) * inv > 0) count++;
        }
        return count;
    }

    // ================================================================ самопересечения

    /// <summary>
    /// Серьёзность T-стыка (касания без общей вершины). Ошибка: в замкнутой согласованной сетке несмежные
    /// треугольники не соприкасаются. Замер перед решением: обзор K01 (71 тело, 452 744 пары) — 0 T-стыков.
    /// </summary>
    private static ValidationSeverity _gTJunctionSeverity = ValidationSeverity.Error;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.TJunctionSeverity), если он задан; иначе общее по умолчанию.</summary>
    public static ValidationSeverity TJunctionSeverity
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.TJunctionSeverity : _gTJunctionSeverity;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gTJunctionSeverity, value, "SolidValidator.TJunctionSeverity");
    }

    internal sealed class PairStats
    {
        public readonly int[] Counts = new int[Enum.GetValues<PairClass>().Length];
        public readonly Vector3d?[] First = new Vector3d?[Enum.GetValues<PairClass>().Length];
        public int Tested;
    }

    /// <summary>
    /// Классы всех пар треугольников с пересекающимися габаритами (TrianglePairs.Classify — точно).
    /// Пары с общими вершинами тоже проверяются: складка через общую вершину или ребро — самопересечение.
    /// </summary>
    internal static PairStats ClassifyPairs(List<int[]> tris, List<Vector3d> pts)
    {
        var st = new PairStats();
        if (tris.Count < 2) return st;

        var lo = new Vector3d[tris.Count]; var hi = new Vector3d[tris.Count];
        var all = Bounds3.Empty;
        double sum = 0;
        for (int t = 0; t < tris.Count; t++)
        {
            Vector3d a = pts[tris[t][0]], b = pts[tris[t][1]], c = pts[tris[t][2]];
            lo[t] = Vector3d.ComponentMin(a, Vector3d.ComponentMin(b, c));
            hi[t] = Vector3d.ComponentMax(a, Vector3d.ComponentMax(b, c));
            all.Add(lo[t]); all.Add(hi[t]);
            var ext = hi[t] - lo[t]; sum += Math.Max(ext.X, Math.Max(ext.Y, ext.Z));
        }
        var size = all.Max - all.Min;
        double diag = Math.Max(size.Length, 1e-9);
        double cell = Math.Max(sum / tris.Count * 2, diag / 200);

        var grid = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) C(Vector3d p) => ((long)Math.Floor((p.X - all.Min.X) / cell),
                                            (long)Math.Floor((p.Y - all.Min.Y) / cell),
                                            (long)Math.Floor((p.Z - all.Min.Z) / cell));
        for (int t = 0; t < tris.Count; t++)
        {
            var (x0, y0, z0) = C(lo[t]); var (x1, y1, z1) = C(hi[t]);
            for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                    for (long z = z0; z <= z1; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) grid[(x, y, z)] = l = new List<int>();
                        l.Add(t);
                    }
        }

        var tested = new HashSet<long>();
        var A = new Vector3d[3]; var B = new Vector3d[3];
        foreach (var list in grid.Values)
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    int s = Math.Min(list[i], list[j]), t = Math.Max(list[i], list[j]);
                    if (!tested.Add(((long)s << 32) | (uint)t)) continue;
                    // Касание габаритов засчитывается (<, а не <=): T-стык лежит ровно на границе.
                    if (lo[s].X > hi[t].X || lo[t].X > hi[s].X || lo[s].Y > hi[t].Y || lo[t].Y > hi[s].Y ||
                        lo[s].Z > hi[t].Z || lo[t].Z > hi[s].Z) continue;
                    st.Tested++;
                    for (int k = 0; k < 3; k++) { A[k] = pts[tris[s][k]]; B[k] = pts[tris[t][k]]; }
                    var cls = TrianglePairs.Classify(A, tris[s], B, tris[t]);
                    st.Counts[(int)cls]++;
                    st.First[(int)cls] ??= (A[0] + A[1] + A[2]) / 3;
                }
        return st;
    }

    /// <summary>Число самопересечений: собственные пересечения, компланарные наложения и дубликаты.</summary>
    internal static int SelfIntersections(List<int[]> tris, List<Vector3d> pts, out Vector3d? first)
    {
        var st = ClassifyPairs(tris, pts);
        first = st.First[(int)PairClass.Transversal] ?? st.First[(int)PairClass.CoplanarOverlap] ?? st.First[(int)PairClass.Duplicate];
        return st.Counts[(int)PairClass.Transversal] + st.Counts[(int)PairClass.CoplanarOverlap] + st.Counts[(int)PairClass.Duplicate];
    }
}

/// <summary>
/// ОБОЛОЧКА ИЗ КОЛЕЦ ВЕРШИН — общее ядро проверки для точных многоугольников и треугольников
/// сетки. Вершины заданы номерами; тождество номеров решает вызывающий (номер точной вершины
/// реестра, точное совпадение float-координат).
///
/// Для замкнутого тела каждая неориентированная сторона встречается ровно дважды и обходится
/// навстречу; окрестность каждой вершины — одна цепь из сторон без разветвлений (диск).
/// Обобщает проверки, прежде жившие только в защите скругления (SurfaceScan.PinchPoint) и во
/// внешних тестах A06.
/// </summary>
internal sealed class RingShell
{
    public int Edges, Open, NonManifold, SameDirection, BadLinkVertices, Pinches;
    public readonly List<(int A, int B, string Kind)> Samples = new();
    public readonly List<(int V, string Kind)> VertexSamples = new();
    public int[] Components = Array.Empty<int>();
    public bool CleanEdges => Open == 0 && NonManifold == 0 && SameDirection == 0;

    public static RingShell Analyze(IReadOnlyList<IReadOnlyList<int>> rings, bool closed)
    {
        var s = new RingShell();
        var edges = new Dictionary<(int, int), (int Count, int Balance, int Ring)>();
        var links = new Dictionary<int, List<(int Prev, int Next)>>();

        for (int ri = 0; ri < rings.Count; ri++)
        {
            var ring = rings[ri];
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                int a = ring[i], b = ring[(i + 1) % n];
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                var e = edges.GetValueOrDefault(key);
                edges[key] = (e.Count + 1, e.Balance + (a < b ? 1 : -1), ri);
                if (!links.TryGetValue(a, out var l)) links[a] = l = new List<(int, int)>();
                l.Add((ring[(i + n - 1) % n], b));
            }
        }

        s.Edges = edges.Count;
        foreach (var (key, e) in edges)
        {
            string? kind = null;
            if (e.Count == 1) { if (closed) { s.Open++; kind = "open"; } }
            else if (e.Count > 2) { s.NonManifold++; kind = $"multiplicity-{e.Count}"; }
            else if (e.Balance != 0) { s.SameDirection++; kind = "same-direction"; }
            if (kind != null && s.Samples.Count < 64) s.Samples.Add((key.Item1, key.Item2, kind));
        }

        // Окрестность вершины: стороны (Prev, Next) каждого кольца, проходящего через вершину,
        // образуют граф на соседях. Для внутренней вершины замкнутого тела — один цикл
        // (степени 2, связность); для края открытой оболочки допускается одна цепь.
        if (closed)
            foreach (var (v, list) in links)
            {
                var degree = new Dictionary<int, int>();
                foreach (var (a, b) in list)
                {
                    degree[a] = degree.GetValueOrDefault(a) + 1;
                    degree[b] = degree.GetValueOrDefault(b) + 1;
                }
                if (degree.Values.Any(d => d != 2))
                {
                    s.BadLinkVertices++;
                    if (s.VertexSamples.Count < 64) s.VertexSamples.Add((v, "link-degree"));
                    continue;
                }
                var seen = new HashSet<int> { list[0].Prev };
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var (a, b) in list)
                    {
                        if (seen.Contains(a) && seen.Add(b)) grew = true;
                        if (seen.Contains(b) && seen.Add(a)) grew = true;
                    }
                }
                if (seen.Count != degree.Count)
                {
                    s.Pinches++;
                    if (s.VertexSamples.Count < 64) s.VertexSamples.Add((v, "pinch"));
                }
            }

        // Компоненты связности колец по общим сторонам.
        var parent = Enumerable.Range(0, rings.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var firstRing = new Dictionary<(int, int), int>();
        for (int ri = 0; ri < rings.Count; ri++)
        {
            var ring = rings[ri];
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i], b = ring[(i + 1) % ring.Count];
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                if (firstRing.TryGetValue(key, out int o)) parent[Find(ri)] = Find(o);
                else firstRing[key] = ri;
            }
        }
        var label = new Dictionary<int, int>();
        s.Components = new int[rings.Count];
        for (int ri = 0; ri < rings.Count; ri++)
        {
            int root = Find(ri);
            if (!label.TryGetValue(root, out int c)) label[root] = c = label.Count;
            s.Components[ri] = c;
        }
        return s;
    }

    public static RingShell Analyze(List<int[]> tris, bool closed) =>
        Analyze(tris.Cast<IReadOnlyList<int>>().ToList(), closed);

    public static RingShell Analyze(List<int>[] rings, bool closed) =>
        Analyze(rings.Cast<IReadOnlyList<int>>().ToList(), closed);

    public void Report(ValidationReport r, Representation where, ValidationOptions o, Func<int, Vector3d> at)
    {
        string p = where.ToString().ToLowerInvariant();
        r.Metrics[p + ".edges"] = Edges;
        r.Metrics[p + ".openEdges"] = Open;
        r.Metrics[p + ".nonManifoldEdges"] = NonManifold;
        r.Metrics[p + ".sameDirectionEdges"] = SameDirection;
        r.Metrics[p + ".badLinkVertices"] = BadLinkVertices;
        r.Metrics[p + ".pinchedVertices"] = Pinches;

        void Add(int count, string code, string what, IEnumerable<Vector3d> where2)
        {
            if (count == 0) return;
            var first = where2.Take(o.MaxListed).ToList();
            r.Issues.Add(new ValidationIssue(where, ValidationSeverity.Error, code, $"{what}: {count}", null,
                first.Count > 0 ? first[0] : null));
            foreach (var q in first.Skip(1))
                r.Issues.Add(new ValidationIssue(where, ValidationSeverity.Error, code, what, null, q));
        }

        Vector3d Mid((int A, int B, string Kind) e) => (at(e.A) + at(e.B)) / 2;
        Add(Open, "open-edge", "сторон без пары", Samples.Where(e => e.Kind == "open").Select(Mid));
        Add(NonManifold, "nonmanifold-edge", "сторон кратности больше двух", Samples.Where(e => e.Kind.StartsWith("multiplicity")).Select(Mid));
        Add(SameDirection, "orientation", "сторон, обходимых обеими сторонами в одну сторону", Samples.Where(e => e.Kind == "same-direction").Select(Mid));
        Add(BadLinkVertices, "vertex-link", "вершин с разветвлённой окрестностью", VertexSamples.Where(v => v.Kind == "link-degree").Select(v => at(v.V)));
        Add(Pinches, "vertex-pinch", "вершин, где тело касается себя (несколько вееров)", VertexSamples.Where(v => v.Kind == "pinch").Select(v => at(v.V)));
    }
}
