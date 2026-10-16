using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using OpenTK.Mathematics;
using Parametric3D.App.Operations;
using Parametric3D.App.Services;
using Parametric3D.App.Sketching;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Export;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Diagnostics;

/// <summary>
/// КНОПКА «ТЕСТ»: приложение само строит детали и замеряет время — каждую операцию отдельно, реалистичные детали по
/// целям g.13 (полное перестроение, правка последней и средней операции, предпросмотр, память), 100 отверстий, долгую
/// сессию, файл и экспорт, затем вид на OpenGL этого компьютера (кадры в секунду во всех стилях, задержку выбора под
/// курсором, наибольшую остановку окна) и сценарии интерфейса (скругление в панели, эскиз → выдавливание).
/// Отчёт — текстовый файл в «Документы\Parametric3D\Tests»; пишется по ходу, так что при зависании остаётся то, что
/// успело выполниться (последняя строка — на чём остановилось).
/// </summary>
public sealed class SelfTest
{
    public const string Build = "u05";

    private readonly MainWindow _w;
    private readonly Action<string> _progress;
    private readonly CancellationToken _ct;
    private readonly StreamWriter _out;
    private readonly List<(string Name, string Target, string Value, bool Ok)> _targets = new();
    private int _ok, _bad;
    private double _maxStall;
    private string _stallPhase = "";
    private readonly DateTime _started = DateTime.Now;
    public string ReportPath { get; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private SelfTest(MainWindow w, Action<string> progress, CancellationToken ct)
    {
        _w = w; _progress = progress; _ct = ct;
        string dir;
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try { dir = Path.Combine(docs, "Parametric3D", "Tests"); Directory.CreateDirectory(dir); }
        catch { dir = Path.Combine(Path.GetTempPath(), "Parametric3D", "Tests"); Directory.CreateDirectory(dir); }
        ReportPath = Path.Combine(dir, $"P3D_test_{_started:yyyyMMdd_HHmmss}.txt");
        _out = new StreamWriter(ReportPath, false, new System.Text.UTF8Encoding(true)) { AutoFlush = true };
    }

    /// <summary>Выполнить тест; возвращает путь к отчёту.</summary>
    public static async Task<string> RunAsync(MainWindow w, Action<string> progress, CancellationToken ct)
    {
        var t = new SelfTest(w, progress, ct);
        try { await t.Run(); }
        catch (OperationCanceledException) { t.Line(""); t.Line("ТЕСТ ОСТАНОВЛЕН ПОЛЬЗОВАТЕЛЕМ."); }
        catch (Exception ex) { t.Line(""); t.Line("ТЕСТ ПРЕРВАН ИСКЛЮЧЕНИЕМ: " + ex); AppLog.Write("self test", ex); }
        finally { t._out.Dispose(); }
        return t.ReportPath;
    }

    // ================================================================ вывод

    private void Line(string s) { _out.WriteLine(s); }
    private void Head(string s) { Line(""); Line("=== " + s + " ==="); _progress(s); }
    private void Step(string s) { _progress("  " + s); }
    private static string F(double v, string fmt = "0.0") => double.IsNaN(v) ? "—" : v.ToString(fmt, Inv);

    private void Target(string name, string target, double value, double limit, string unit = "мс")
    {
        bool ok = !double.IsNaN(value) && value <= limit;
        _targets.Add((name, target, F(value) + " " + unit, ok));
    }

    private void TargetAtLeast(string name, string target, double value, double min, string unit)
    {
        bool ok = !double.IsNaN(value) && value >= min;
        _targets.Add((name, target, F(value) + " " + unit, ok));
    }

    private void Count(bool ok) { if (ok) _ok++; else _bad++; }

    private static double Median(List<double> v) { if (v.Count == 0) return double.NaN; var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
    private static double P95(List<double> v) { if (v.Count == 0) return double.NaN; var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)Math.Ceiling(0.95 * s.Count) - 1)]; }

    // ================================================================ наблюдение за окном: остановки потока окна

    private DispatcherTimer? _watch;
    private long _lastTick;

    private void StartWatch()
    {
        _lastTick = Stopwatch.GetTimestamp();
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) =>
        {
            double gap = Stopwatch.GetElapsedTime(_lastTick).TotalMilliseconds;
            _lastTick = Stopwatch.GetTimestamp();
            if (gap > _maxStall) { _maxStall = gap; _stallPhase = _phase; }
            if (gap > 100) _stalls.Add((_phase, gap));
        });
        _watch.Start();
    }

    private string _phase = "";
    private readonly List<(string Phase, double Ms)> _stalls = new();

    // ================================================================ ход

    private async Task Run()
    {
        var original = _w.Session;
        StartWatch();
        try
        {
            SystemInfo();
            await Operations();
            await RealisticParts();
            await Holes();
            await Session();
            await Files();
            await View();
            await Ui();
        }
        finally
        {
            _watch?.Stop();
            try { _w.CancelOperation(); if (_w.InSketch) _w.CancelSketch(); _w.SetSession(original); } catch { }
        }
        Summary();
    }

    // ---------------------------------------------------------------- система

    private void SystemInfo()
    {
        Line("Parametric3D — отчёт кнопки «Тест»");
        Line($"Поставка: {Build}; сборка приложения: {BuildTime()}; запуск теста: {_started:yyyy-MM-dd HH:mm:ss}");
        Head("1. Система");
        Line($"ОС: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); процесс {RuntimeInformation.ProcessArchitecture}");
        Line($".NET: {RuntimeInformation.FrameworkDescription}; сборщик мусора: {(System.Runtime.GCSettings.IsServerGC ? "серверный" : "рабочей станции")}, режим {System.Runtime.GCSettings.LatencyMode}");
        Line($"Процессор: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? Cpu()}; логических ядер: {Environment.ProcessorCount}");
        var gc = GC.GetGCMemoryInfo();
        Line($"Память: доступно процессу {gc.TotalAvailableMemoryBytes / 1048576.0:0} МБ; рабочий набор сейчас {Environment.WorkingSet / 1048576.0:0} МБ");
        var v = _w.ViewPort;
        Line($"OpenGL: {(string.IsNullOrEmpty(v.GlInfo) ? "нет" : v.GlInfo)}");
        if (v.GlError != null) Line($"Ошибка OpenGL при запуске: {v.GlError}");
        Line($"Сглаживание: {v.Msaa}×; выбор под курсором: {(v.GpuPicking ? "видеокарта" : "процессор (OpenGL не готов)")}; ошибок кадра с запуска: {v.FrameErrors}");
        Line($"Масштаб экрана: {v.Scaling:0.##}; окно {_w.Bounds.Width:0}×{_w.Bounds.Height:0}, вид {v.Bounds.Width:0}×{v.Bounds.Height:0}");
        Line($"Язык интерфейса: {(Loc.En ? "EN" : "RU")}; журнал приложения: {AppLog.Path}");
    }

    private static string BuildTime()
    {
        try { return File.GetLastWriteTime(typeof(SelfTest).Assembly.Location).ToString("yyyy-MM-dd HH:mm"); } catch { return "?"; }
    }

    private static string Cpu()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
                return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "?";
        }
        catch { }
        return "?";
    }

    // ---------------------------------------------------------------- операции по одной

    private async Task Operations()
    {
        Head("2. Операции по одной (каждая — на своей детали)");
        Line($"{"операция",-42} {"всего, мс",10} {"операция, мс",13} {"объём, мм³",14} {"ожидалось",14} {"тело",6}  ошибка");
        // прогрев: первый расчёт включает загрузку и компиляцию кода
        _phase = "прогрев";
        await Task.Run(() => { var p = TestParts.Block("warm"); p.Rebuild(null); var q = TestParts.Realistic(99, 12); q.Rebuild(null); });
        foreach (var c in TestParts.OperationCases())
        {
            _ct.ThrowIfCancellationRequested();
            Step(c.Name);
            _phase = "операция: " + c.Name;
            var r = await Task.Run(() =>
            {
                var p = c.Make();
                p.Invalidate();
                var sw = Stopwatch.StartNew();
                p.Rebuild(null);
                double total = sw.Elapsed.TotalMilliseconds;
                var last = p.Features[^1];
                double op = p.LastFeatureMs.TryGetValue(last, out var ms) ? ms : double.NaN;
                bool valid = p.Bodies.Count > 0 && p.Bodies.All(b => SolidValidator.Validate(b).IsValid);
                string? err = p.Features.Select(f => f.Error).FirstOrDefault(e => e != null) ?? p.Integrity;
                return (total, op, p.Volume, valid, err);
            });
            bool volOk = c.Volume == null || Math.Abs(r.Volume - c.Volume.Value) <= 1e-6 * Math.Max(1, c.Volume.Value) + 1e-3;
            bool ok = r.err == null && r.valid && volOk;
            Count(ok);
            Line($"{Cut(c.Name, 42),-42} {F(r.total),10} {F(r.op),13} {F(r.Volume, "0.###"),14} {(c.Volume is { } e ? F(e, "0.###") : "—"),14} {(r.valid ? "годно" : "НЕТ"),6}  " +
                 (r.err ?? (volOk ? "" : "ОБЪЁМ НЕ СОВПАЛ")));
        }
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ---------------------------------------------------------------- реалистичные детали (g.13)

    private async Task RealisticParts()
    {
        Head("3. Реалистичные детали, до 40 операций (цели g.13)");
        Line($"{"зерно",5} {"опер.",5} {"граней",6} {"ошиб.",5} {"полное",8} {"правка посл.",12} {"правка сред.",12} {"предпр. мед.",12} {"выдел., МБ",10}");
        var byType = new Dictionary<string, List<double>>();
        var previews = new List<double>(); var lastEdits = new List<double>(); var midEdits = new List<double>(); var fulls = new List<double>(); var allocs = new List<double>();
        var slow = new List<(double Ms, string What)>();
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            _ct.ThrowIfCancellationRequested();
            Step($"деталь, зерно {seed}");
            _phase = $"реалистичная деталь {seed}";
            var r = await Task.Run(() =>
            {
                var p = TestParts.Realistic(seed, 40);
                var sw = new Stopwatch();
                GC.Collect(); long a0 = GC.GetTotalAllocatedBytes(true);
                p.Invalidate(); sw.Restart(); p.Rebuild(null); double full = sw.Elapsed.TotalMilliseconds;
                double alloc = (GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0;
                var perFeature = p.Features.Select(f => (f, ms: p.LastFeatureMs.TryGetValue(f, out var m) ? m : double.NaN)).ToList();
                var last = p.Features[^1]; var mid = p.Features[p.Features.Count / 2];
                var lt = new List<double>(); var mt = new List<double>(); var pt = new List<double>();
                for (int i = 0; i < 3; i++) { p.MarkDirtyFrom(last); sw.Restart(); p.Rebuild(null); lt.Add(sw.Elapsed.TotalMilliseconds); }
                for (int i = 0; i < 3; i++) { p.MarkDirtyFrom(mid); sw.Restart(); p.Rebuild(null); mt.Add(sw.Elapsed.TotalMilliseconds); }
                for (int i = 0; i < 5; i++)
                {
                    p.MarkDirtyFrom(last); sw.Restart();
                    using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) { var res = p.Compute(null); p.Apply(res); }
                    pt.Add(sw.Elapsed.TotalMilliseconds);
                }
                p.Rebuild(null);
                return (features: p.Features.Count, faces: p.Bodies.Sum(b => b.Faces.Count()), errors: p.Features.Count(f => f.Error != null),
                        full, alloc, perFeature, lt, mt, pt, errText: p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error).FirstOrDefault());
            });
            fulls.Add(r.full); allocs.Add(r.alloc); lastEdits.Add(Median(r.lt)); midEdits.Add(Median(r.mt)); previews.AddRange(r.pt);
            foreach (var (f, ms) in r.perFeature)
            {
                if (double.IsNaN(ms)) continue;
                string k = f.GetType().Name.Replace("Feature", "") + (f is HoleFeature h ? "/" + h.Kind + (h.ThroughAll ? "/насквозь" : "") : f is ExtrudeFeature e && e.Cut ? "/вырез" : "");
                (byType.TryGetValue(k, out var l) ? l : byType[k] = new List<double>()).Add(ms);
                slow.Add((ms, $"зерно {seed}, {f.Name} ({k})"));
            }
            Count(r.errors == 0);
            Line($"{seed,5} {r.features,5} {r.faces,6} {r.errors,5} {F(r.full),8} {F(Median(r.lt)),12} {F(Median(r.mt)),12} {F(Median(r.pt)),12} {F(r.alloc),10}" +
                 (r.errText != null ? "   ошибка: " + Cut(r.errText, 120) : ""));
        }
        Line("");
        Line("Время операций по видам (полное перестроение, все 6 деталей):");
        Line($"  {"вид",-32} {"число",6} {"медиана",9} {"p95",9} {"наиб.",9} {"сумма",9}");
        foreach (var (k, l) in byType.OrderByDescending(kv => kv.Value.Sum()))
            Line($"  {k,-32} {l.Count,6} {F(Median(l)),9} {F(P95(l)),9} {F(l.Max()),9} {F(l.Sum()),9}");
        Line("Самые медленные операции:");
        foreach (var (ms, what) in slow.OrderByDescending(x => x.Ms).Take(8)) Line($"  {F(ms),8} мс  {what}");

        Target("Предпросмотр последней операции, p95", "≤ 150 мс (цель 100)", P95(previews), 150);
        Target("Правка последней операции, наибольшая медиана", "≤ 500 мс", lastEdits.Max(), 500);
        Target("Правка средней операции, наибольшая медиана", "≤ 1000 мс", midEdits.Max(), 1000);
        Target("Полное перестроение 40 операций, наибольшее", "≤ 2000 мс", fulls.Max(), 2000);
        Target("Выделения памяти на полное перестроение, наибольшее", "≤ 500 МБ", allocs.Max(), 500, "МБ");
    }

    // ---------------------------------------------------------------- 100 отверстий

    private async Task Holes()
    {
        Head("4. 100 сквозных отверстий");
        _phase = "100 отверстий";
        Step("100 отверстий");
        var r = await Task.Run(() =>
        {
            var p = TestParts.Holes100();
            p.Rebuild(null);
            p.MarkDirtyFrom(p.Features[0]);
            var sw = Stopwatch.StartNew(); p.Rebuild(null);
            return (ms: sw.Elapsed.TotalMilliseconds, errors: p.Features.Count(f => f.Error != null), faces: p.Bodies.Sum(b => b.Faces.Count()));
        });
        Count(r.errors == 0);
        Line($"Полное перестроение: {F(r.ms)} мс; граней {r.faces}; ошибок {r.errors}");
        Target("100 отверстий, полное перестроение", "≤ 3000 мс", r.ms, 3000);
    }

    // ---------------------------------------------------------------- долгая сессия

    private async Task Session()
    {
        Head("5. Долгая сессия: 60 правок (рост памяти)");
        _phase = "долгая сессия";
        Step("60 правок");
        var r = await Task.Run(() =>
        {
            var p = TestParts.Realistic(7, 40);
            p.Rebuild(null);
            var last = p.Features[^1]; var mid = p.Features[p.Features.Count / 2];
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long m0 = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++)
            {
                _ct.ThrowIfCancellationRequested();
                p.MarkDirtyFrom(last); p.Rebuild(null); p.MarkDirtyFrom(mid); p.Rebuild(null);
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long m1 = GC.GetTotalMemory(true);
            return (ms, growth: (m1 - m0) / 1048576.0, ws: Environment.WorkingSet / 1048576.0);
        });
        Line($"60 правок: {F(r.ms)} мс (в среднем {F(r.ms / 60)} мс); рост управляемой памяти {F(r.growth)} МБ; рабочий набор {F(r.ws, "0")} МБ");
        Target("Рост памяти за долгую сессию", "≤ 50 МБ", r.growth, 50, "МБ");
    }

    // ---------------------------------------------------------------- файл и экспорт

    private async Task Files()
    {
        Head("6. Файл и экспорт");
        _phase = "файл и экспорт";
        Step("сохранить, открыть, STEP, STL");
        var dir = Path.Combine(Path.GetTempPath(), "p3d_selftest");
        Directory.CreateDirectory(dir);
        var r = await Task.Run(() =>
        {
            var p = TestParts.Realistic(3, 40);
            p.Rebuild(null);
            var lines = new List<string>();
            string file = Path.Combine(dir, "t.p3d");
            var sw = Stopwatch.StartNew();
            DocumentStorage.Save(file, p);
            double save = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var (q, t) = DocumentStorage.Load(file);
            double load = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); q.Rebuild(t); double rebuild = sw.Elapsed.TotalMilliseconds;
            bool same = Math.Abs(q.Volume - p.Volume) <= 1e-9 * Math.Max(1, p.Volume);
            lines.Add($"Сохранить {F(save)} мс, открыть {F(load)} мс, перестроить открытое {F(rebuild)} мс; объём {(same ? "совпал" : $"НЕ СОВПАЛ: {q.Volume} против {p.Volume}")}");
            sw.Restart();
            var swr = new StringWriter();
            var rep = Exchange.WriteStep(swr, new[] { (p, (Matrix4d?)null) }, "selftest");
            double step = sw.Elapsed.TotalMilliseconds;
            lines.Add($"STEP точный: {F(step)} мс, {(rep.Exact ? $"записан ({rep.Bodies} тел, {swr.GetStringBuilder().Length / 1024} КБ)" : "НЕ ЗАПИСАН: " + string.Join("; ", rep.Warnings))}");
            sw.Restart();
            var tris = MeshExport.Triangles(p.Bodies).ToList();
            string stl = Path.Combine(dir, "t.stl");
            using (var fs = File.Create(stl)) MeshExport.WriteStlBinary(fs, tris, "selftest");
            MeshExport.MeshCheck check;
            using (var rs = File.OpenRead(stl)) check = MeshExport.Check(MeshExport.ReadStlBinary(rs));
            double stlMs = sw.Elapsed.TotalMilliseconds;
            lines.Add($"STL: {F(stlMs)} мс, {check.Triangles} треугольников, открытых рёбер {check.Holes}");
            return (lines, ok: same && rep.Exact && check.Holes == 0);
        });
        try { Directory.Delete(dir, true); } catch { }
        foreach (var l in r.lines) Line(l);
        Count(r.ok);
    }

    // ---------------------------------------------------------------- вид (OpenGL этого компьютера)

    private async Task Until(Func<bool> cond, int timeoutMs, bool fine = false)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
        {
            _ct.ThrowIfCancellationRequested();
            if (fine) await Yield(); else await Task.Delay(10);
        }
    }

    /// <summary>
    /// Уступить потоку окна без таймера: Task.Delay под Windows идёт шагами ~15.6 мс, и замер кадров и выбора упирался
    /// в этот шаг (u04: 37–40 кадров/с при 0.3 мс кадра — это предел таймера, а не видеокарты).
    /// </summary>
    private static async Task Yield() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    private async Task WaitBuilt(int timeoutMs = 60000)
    {
        var doc = _w.Session.Document;
        await Until(() => !doc.IsBuilding && !doc.AllParts().Any(p => p.IsDirty), timeoutMs);
    }

    private async Task View()
    {
        Head("7. Вид (OpenGL этого компьютера)");
        var v = _w.ViewPort;
        _phase = "вид: загрузка детали";
        Step("деталь в окне");
        var part = await Task.Run(() => TestParts.Realistic(3, 40));
        var sw = Stopwatch.StartNew();
        _w.SetSession(DocumentSession.FromPart(part, "Тест"));
        await WaitBuilt();
        long f0 = v.FramesRendered;
        v.ZoomToFit(false);
        await Until(() => v.FramesRendered > f0, 5000);
        Line($"Деталь в окне (перестроение и первый кадр): {F(sw.Elapsed.TotalMilliseconds)} мс; сборка сцены и загрузка в видеокарту: {F(v.LastSceneMs)} мс");
        if (!v.GpuPicking) Line("OpenGL не готов — замеры кадров ниже недостоверны.");

        // кадры в секунду при вращении, во всех стилях
        Line($"{"стиль",-30} {"кадров/с",9} {"кадр ЦП, мс (сред./наиб.)",26}");
        var fpsAll = new List<double>();
        foreach (var style in Enum.GetValues<DisplayStyle>())
        {
            _ct.ThrowIfCancellationRequested();
            Step("стиль " + style);
            _phase = "вид: вращение, " + style;
            _w.SetStyle(style);
            await Task.Delay(150);
            long start = v.FramesRendered;
            var times = new List<double>();
            long lastSeen = start;
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 2000)
            {
                v.Camera.Orbit(6, 1.5f);
                v.Redraw();
                await Yield();
                if (v.FramesRendered != lastSeen) { lastSeen = v.FramesRendered; times.Add(v.LastFrameMs); }
            }
            double fps = (v.FramesRendered - start) / t.Elapsed.TotalSeconds;
            fpsAll.Add(fps);
            Line($"{style,-30} {F(fps),9} {F(times.Count > 0 ? times.Average() : double.NaN, "0.00") + " / " + F(times.Count > 0 ? times.Max() : double.NaN, "0.00"),26}");
        }
        _w.SetStyle(DisplayStyle.ShadedWithEdges);
        Line("Кадров в секунду не больше частоты обновления экрана (обычно 60): при вертикальной синхронизации 55–60 — предел монитора.");
        Count(fpsAll.Count > 0 && fpsAll.Min() >= 20);
        TargetAtLeast("Кадров в секунду при вращении, наименьшее по стилям", "≥ 30", fpsAll.DefaultIfEmpty(0).Min(), 30, "кадров/с");

        // задержка выбора под курсором
        _phase = "вид: выбор под курсором";
        Step("выбор под курсором");
        v.GoHome();
        await Task.Delay(800);
        var lat = new List<double>();
        var rnd = new Random(5);
        for (int i = 0; i < 30; i++)
        {
            long before = v.PicksServed;
            var t = Stopwatch.StartNew();
            v.SimulatePick(0.3 + rnd.NextDouble() * 0.4, 0.3 + rnd.NextDouble() * 0.4, select: false);
            await Until(() => v.PicksServed > before, 3000, fine: true);
            lat.Add(t.Elapsed.TotalMilliseconds);
        }
        Line($"Выбор под курсором ({(v.GpuPicking ? "видеокарта" : "процессор")}): медиана {F(Median(lat))} мс, наибольшее {F(lat.Max())} мс (30 наведений)");
        Target("Задержка выбора под курсором, медиана", "≤ 50 мс", Median(lat), 50);
        Line($"Ошибок кадра с запуска: {v.FrameErrors}");
        Count(v.FrameErrors == 0);
    }

    // ---------------------------------------------------------------- сценарии интерфейса

    private async Task Ui()
    {
        Head("8. Сценарии интерфейса");
        var v = _w.ViewPort;

        // скругление в панели на детали из окна
        _phase = "интерфейс: скругление";
        Step("скругление в панели");
        var part = _w.Session.Part!;
        var edge = part.Bodies.SelectMany(b => b.Edges).FirstOrDefault(e => e.Curve is Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1);
        if (edge == null) { Line("Скругление: подходящего ребра нет"); Count(false); }
        else
        {
            int f0 = part.Features.Count;
            v.Selection.Select(new PickRef(edge.Id, PickKind.Edge, part, null), additive: false);
            var sw = Stopwatch.StartNew();
            _w.StartOperation(new RoundOp());
            await WaitBuilt();
            double preview = sw.Elapsed.TotalMilliseconds;
            var round = part.Features.LastOrDefault() as RoundFeature;
            string? err = round?.Error;
            sw.Restart();
            _w.CommitOperation();
            double commit = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            _w.Session.Undo();
            await WaitBuilt();
            double undo = sw.Elapsed.TotalMilliseconds;
            bool ok = round != null && err == null && part.Features.Count == f0 + 1;
            Count(ok);
            Line($"Скругление в панели: предпросмотр готов через {F(preview)} мс, «ОК» {F(commit)} мс, отмена {F(undo)} мс; {(ok ? "без ошибок" : "ОШИБКА: " + (err ?? "операция не добавлена"))}");
            Target("Скругление в панели: предпросмотр на детали 40 операций", "≤ 1000 мс", preview, 1000);
        }

        // эскиз → прямоугольник → выдавливание на новой детали
        _phase = "интерфейс: эскиз и выдавливание";
        Step("эскиз → выдавливание");
        var s = DocumentSession.NewPart();
        _w.SetSession(s);
        await Task.Delay(200);
        var p = s.Part!;
        var sw2 = Stopwatch.StartNew();
        _w.SelectItem(p.PlaneXY);
        _w.StartOperation(new SketchOp { Then = sk => new ExtrudeOp { SeedSketch = sk } });
        bool inSketch = _w.InSketch;
        if (inSketch)
        {
            var sk = v.ActiveSketch!;
            var rect = new RectangleTool();
            rect.Click(new SketchInput(sk, new Vector2d(-30, -20), null));
            rect.Click(new SketchInput(sk, new Vector2d(30, 20), null));
            await Task.Delay(300);
            _w.FinishSketch();
            await WaitBuilt();
            _w.CommitOperation();
            await WaitBuilt();
        }
        double total = sw2.Elapsed.TotalMilliseconds;
        bool vol = Math.Abs(p.Volume - 60 * 40 * 20) < 1e-3;
        Count(inSketch && vol);
        Line($"Эскиз → прямоугольник → выдавливание: {F(total)} мс (с паузами сценария 0.5 с); {(inSketch ? "" : "ЭСКИЗ НЕ НАЧАЛСЯ; ")}объём {F(p.Volume, "0.###")} (ожидалось 48000) — {(vol ? "верно" : "НЕВЕРНО")}");

        Line("");
        Line($"Наибольшая остановка окна за весь тест: {F(_maxStall)} мс (этап: {_stallPhase})");
        Line("Остановки окна дольше 100 мс (этап — длительность):");
        foreach (var (ph, ms) in _stalls.OrderByDescending(x => x.Ms).Take(12)) Line($"  {F(ms),8} мс  {ph}");
        if (_stalls.Count == 0) Line("  нет");
        Line($"Долгие участки в потоке окна (≥ {UiTimes.ThresholdMs:0} мс), по суммарному времени:");
        var slow = UiTimes.Since(_started);
        foreach (var g in slow.GroupBy(x => x.Name).OrderByDescending(g => g.Sum(x => x.Ms)).Take(12))
            Line($"  {g.Key,-44} ×{g.Count(),-4} наиб. {F(g.Max(x => x.Ms)),8} мс, сумма {F(g.Sum(x => x.Ms)),8} мс");
        if (slow.Count == 0) Line("  нет");
        Target("Наибольшая остановка окна (окно не отвечает)", "≤ 500 мс", _maxStall, 500);
    }

    // ---------------------------------------------------------------- итог

    private void Summary()
    {
        Head("9. Цели");
        foreach (var t in _targets)
            Line($"  [{(t.Ok ? "да " : "НЕТ")}] {t.Name}: {t.Value}  (цель {t.Target})");
        Head("10. Журнал приложения за время теста");
        try
        {
            if (File.Exists(AppLog.Path))
            {
                var lines = File.ReadAllLines(AppLog.Path);
                var mine = new List<string>();
                bool on = false;
                foreach (var l in lines)
                {
                    if (l.Length >= 23 && DateTime.TryParseExact(l[..23], "yyyy-MM-dd HH:mm:ss.fff", Inv, DateTimeStyles.None, out var t)) on = t >= _started;
                    if (on) mine.Add(l);
                }
                if (mine.Count == 0) Line("записей нет");
                foreach (var l in mine.Take(200)) Line("  " + l);
            }
            else Line("журнала нет");
        }
        catch (Exception ex) { Line("журнал не прочитан: " + ex.Message); }
        int tOk = _targets.Count(t => t.Ok);
        Line("");
        Line($"ИТОГ: проверок пройдено {_ok}, не пройдено {_bad}; целей выполнено {tOk} из {_targets.Count}; длительность {F((DateTime.Now - _started).TotalSeconds)} с");
        _progress($"Готово: проверок {_ok} из {_ok + _bad}, целей {tOk} из {_targets.Count}");
    }
}
