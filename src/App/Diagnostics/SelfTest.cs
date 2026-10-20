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
    public const string Build = "k82";

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
            // отладка: только раздел ручек (P3D_SELFTEST_ONLY=handles)
            if (Environment.GetEnvironmentVariable("P3D_SELFTEST_ONLY") == "handles") { await Handles(); await UserEdges(); return; }
            if (Environment.GetEnvironmentVariable("P3D_SELFTEST_ONLY") == "cancel") { await CancelAndFailure(); return; }
            if (Environment.GetEnvironmentVariable("P3D_SELFTEST_ONLY") == "load") { await Load(); return; }
            await Operations();
            await RealisticParts();
            await Holes();
            await Session();
            await Files();
            await View();
            await Ui();
            await Handles();
            await UserEdges();
            await CancelAndFailure();
            await Load();
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
        Line($"Графика: режим «{Program.GlMode}»; кадр рисуется {(v.RenderOnUiThread switch { true => "в потоке окна", false => "в отдельном потоке", _ => "—" })}");
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

    // ---------------------------------------------------------------- отмена и отказ (KRN-09, 09.10)

    /// <summary>
    /// ПРИЛОЖЕНИЕ ПРИ ОТМЕНЕ И ОТКАЗЕ: операция, которую ядро не может выполнить (скругление больше тела), — ошибка у
    /// операции, деталь и окно живы, отмена правки возвращает исходное; «Отмена» операции во время предпросмотра —
    /// модель как была, без шага «вернуть»; правка детали во время фонового расчёта (расчёт прерывается — результат
    /// устарел) — итог совпадает с расчётом с нуля; испорченный файл — отказ открытия, открытая деталь не тронута.
    /// После каждого шага — кадр вида рисуется (окно не повисло).
    /// </summary>
    private async Task CancelAndFailure()
    {
        Head("9. Отмена и отказ");
        var v = _w.ViewPort;
        async Task<bool> Alive()
        {
            long f0 = v.FramesRendered; v.Redraw();
            await Until(() => v.FramesRendered > f0 || string.IsNullOrEmpty(v.GlInfo), 5000);
            return v.FramesRendered > f0 || string.IsNullOrEmpty(v.GlInfo);
        }

        // а) отказ ядра: скругление радиусом больше тела
        _phase = "отмена и отказ: отказ операции";
        Step("отказ операции");
        var part = TestParts.Block("cancel");
        var s = DocumentSession.FromPart(part, "cancel");
        _w.SetSession(s);
        await WaitBuilt();
        double v0 = part.Volume; int f0 = part.Features.Count, u0 = s.UndoNames.Count;
        var edge = EdgeRef(part, e => e.Curve is Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 10);
        v.Selection.Select(edge, additive: false);
        _w.StartOperation(new RoundOp());
        await WaitBuilt();
        var rf = part.Features.LastOrDefault() as RoundFeature;
        string? err = null;
        if (rf != null) { rf.Radius = new ValueRef(500); part.MarkDirtyFrom(rf); v.MarkDirty(); await WaitBuilt(); err = rf.Error; }
        bool aliveA = await Alive();
        _w.CommitOperation();
        await WaitBuilt();
        bool keptA = Math.Abs(part.Volume - v0) < 1e-6 * v0 && part.Bodies.Count == 1;
        _w.Session.Undo();
        await WaitBuilt();
        var pa = _w.Session.Part!;
        bool undoA = pa.Features.Count == f0 && Math.Abs(pa.Volume - v0) < 1e-6 * v0 && pa.Features.All(f => f.Error == null);
        bool okA = rf != null && err != null && aliveA && keptA && undoA && await Alive();
        Count(okA);
        Line($"Отказ операции (скругление R500 на бруске 100×60×20): ошибка у операции — {(err != null ? "да («" + err.Split('\n')[0] + "»)" : "НЕТ")}; окно рисует — {(aliveA ? "да" : "НЕТ")}; тело после «ОК» не изменилось — {(keptA ? "да" : "НЕТ")}; отмена правки вернула исходное — {(undoA ? "да" : "НЕТ")}");

        // б) «Отмена» во время предпросмотра
        _phase = "отмена и отказ: отмена предпросмотра";
        Step("отмена предпросмотра");
        int okB = 0, triesB = 6;
        for (int i = 0; i < triesB; i++)
        {
            var p = _w.Session.Part!; int fc = p.Features.Count, uc = _w.Session.UndoNames.Count;
            v.Selection.Select(EdgeRef(p, e => e.Curve is Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 10), additive: false);
            _w.StartOperation(i % 2 == 0 ? new ChamferOp() : new RoundOp());
            if (i >= 2) await Task.Delay(15 * i);                         // разные мгновения: до расчёта, во время, после
            _w.CancelOperation();
            await WaitBuilt();
            var q = _w.Session.Part!;
            if (q.Features.Count == fc && _w.Session.UndoNames.Count == uc && !_w.Session.CanRedo && Math.Abs(q.Volume - v0) < 1e-6 * v0 && _w.CurrentOperation == null) okB++;
        }
        bool aliveB = await Alive();
        Count(okB == triesB && aliveB);
        Line($"«Отмена» операции во время предпросмотра: {okB}/{triesB} — модель как была, без шага «вернуть»; окно рисует — {(aliveB ? "да" : "НЕТ")}");

        // в) правка во время фонового расчёта: результат устаревшего расчёта выбрасывается
        _phase = "отмена и отказ: правка во время расчёта";
        Step("правка во время расчёта");
        var big = await Task.Run(() => TestParts.Realistic(5, 24));
        var sb = DocumentSession.FromPart(big, "busy");
        _w.SetSession(sb);
        await WaitBuilt(120000);
        double vRef = big.Volume; int interrupted = 0, okC = 0, triesC = 5;
        var rnd = new Random(7);
        for (int i = 0; i < triesC; i++)
        {
            big.MarkDirtyFrom(big.Features[rnd.Next(1, big.Features.Count)]);
            var t = sb.Document.RebuildDirtyAsync();
            await Task.Delay(20 + rnd.Next(80));
            if (sb.Document.IsBuilding) interrupted++;
            big.MarkDirtyFrom(big.Features[rnd.Next(1, big.Features.Count)]);   // правка во время расчёта
            v.MarkDirty();
            await t;
            await WaitBuilt(120000);
            if (Math.Abs(big.Volume - vRef) <= 1e-6 * vRef && big.Features.All(f => f.Error == null)) okC++;
        }
        bool aliveC = await Alive();
        Count(okC == triesC && aliveC);
        Line($"Правка во время фонового расчёта: {okC}/{triesC} итогов совпали с расчётом с нуля (прервано расчётов: {interrupted}); окно рисует — {(aliveC ? "да" : "НЕТ")}");

        // г) испорченный файл: отказ открытия, открытая деталь на месте
        _phase = "отмена и отказ: испорченный файл";
        Step("испорченный файл");
        string bad = Path.Combine(Path.GetTempPath(), "p3d_selftest_bad" + DocumentSession.PartExtension);
        File.WriteAllText(bad, "{ not a part");
        var before = _w.Session;
        bool refused = false;
        try { _w.SetSession(DocumentSession.Open(bad)); } catch { refused = true; }
        bool okD = refused && ReferenceEquals(_w.Session, before) && await Alive();
        try { File.Delete(bad); } catch { }
        Count(okD);
        Line($"Испорченный файл: открытие отклонено — {(refused ? "да" : "НЕТ")}; открытая деталь на месте, окно рисует — {(okD ? "да" : "НЕТ")}");
    }

    /// <summary>
    /// 10. НАГРУЗКА (KRN-11 11.5, «приложение под нагрузкой»): долгая сессия правки в окне — 80 операций скругления и фаски
    /// на разных рёбрах реальной детали (команда, величина, «ОК»), каждые 10 — три «отменить» и три «вернуть»; окно рисует
    /// после каждой; память после сборки мусора — без роста сверх запаса; в конце — полный пересчёт с нуля даёт тот же
    /// объём, а отмена всех правок сессии — исходную деталь.
    /// </summary>
    private async Task Load()
    {
        Head("10. Нагрузка: долгая сессия правки");
        var v = _w.ViewPort;
        async Task<bool> Alive()
        {
            long f0 = v.FramesRendered; v.Redraw();
            await Until(() => v.FramesRendered > f0 || string.IsNullOrEmpty(v.GlInfo), 5000);
            return v.FramesRendered > f0 || string.IsNullOrEmpty(v.GlInfo);
        }
        static long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
        _phase = "нагрузка: подготовка";
        var part = await Task.Run(() => TestParts.Realistic(3, 12));
        var s = DocumentSession.FromPart(part, "load");
        _w.SetSession(s);
        await WaitBuilt(120000);
        double v0 = s.Part!.Volume; int feat0 = s.Part.Features.Count, undo0 = s.UndoNames.Count;
        const int N = 80;
        int done = 0, refused = 0, dead = 0, undoOk = 0, undoTries = 0;
        var undoWhy = new List<string>();
        long mem10 = 0; double ws10 = 0;
        var times = new List<double>();
        var rnd = new Random(11);
        var total = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
        {
            _ct.ThrowIfCancellationRequested();
            _phase = $"нагрузка: правка {i + 1}";
            if (i % 10 == 0) Step($"правка {i + 1} из {N}");
            if (Environment.GetEnvironmentVariable("P3D_LOADDBG") == "1" && times.Count > 0) Console.WriteLine($"LOAD edit {i}: {times[^1]:0} ms, features {_w.Session.Part!.Features.Count}, last build {(_w.Session.Part!.Features.Count > 0 && _w.Session.Part.LastFeatureMs.TryGetValue(_w.Session.Part.Features[^1], out var lm) ? lm : -1):0} ms");
            var p = _w.Session.Part!;
            var edges = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 6
                                                                     && Parametric3D.App.Operations.Topo.EdgeHandle(p, e.Id) != null).ToList();
            if (edges.Count == 0) break;
            var e = edges[rnd.Next(edges.Count)];
            v.Selection.Select(new PickRef(e.Id, PickKind.Edge, p, null), additive: false);
            int fc = p.Features.Count;
            var sw = Stopwatch.StartNew();
            bool round = i % 2 == 0;
            _w.StartOperation(round ? new RoundOp() : new ChamferOp());
            await WaitBuilt();
            var last = p.Features.Count > fc ? p.Features[^1] : null;
            double size = 0.5 + rnd.NextDouble();
            if (last is RoundFeature rf) { rf.Radius = new ValueRef(size); p.MarkDirtyFrom(rf); }
            else if (last is ChamferFeature cf) { cf.D1 = new ValueRef(size); cf.D2 = new ValueRef(size); p.MarkDirtyFrom(cf); }
            v.MarkDirty();
            await WaitBuilt();
            string? err = last?.Error;
            if (err == null && last != null) { _w.CommitOperation(); done++; } else { _w.CancelOperation(); refused++; }
            await WaitBuilt();
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (!await Alive()) dead++;
            if (i == 9) { mem10 = Managed(); ws10 = Environment.WorkingSet / 1048576.0; }
            if (i % 10 == 9)
            {
                undoTries++;
                var q = _w.Session.Part!; double vq = q.Volume; int fq = q.Features.Count;
                for (int k = 0; k < 3 && _w.Session.UndoNames.Count > undo0; k++) { _w.Session.Undo(); await WaitBuilt(); }
                for (int k = 0; k < 3 && _w.Session.CanRedo; k++) { _w.Session.Redo(); await WaitBuilt(); }
                var q2 = _w.Session.Part!;
                if (q2.Features.Count == fq && Math.Abs(q2.Volume - vq) <= 1e-6 * Math.Max(1, vq) && q2.Features.All(f => f.Error == null)) undoOk++;
                else if (undoWhy.Count < 3)
                    undoWhy.Add($"после правки {i + 1}: операций {q2.Features.Count}/{fq}, объём {q2.Volume:0.###}/{vq:0.###}, ошибки: {string.Join("; ", q2.Features.Where(f => f.Error != null).Select(f => f.Name + " — " + f.Error!.Split('\n')[0][..Math.Min(120, f.Error!.Split('\n')[0].Length)]))}");
            }
        }
        double secs = total.Elapsed.TotalSeconds;
        long memEnd = Managed(); double wsEnd = Environment.WorkingSet / 1048576.0;
        double growMb = (memEnd - mem10) / 1048576.0;
        // пересчёт с нуля — тот же объём
        _phase = "нагрузка: пересчёт с нуля";
        var pe = _w.Session.Part!; double ve = pe.Volume;
        string? saved = null;
        try { saved = Path.Combine(Path.GetDirectoryName(ReportPath) ?? ".", $"P3D_load_{_started:yyyyMMdd_HHmmss}.p3d"); File.WriteAllText(saved, DocumentFile.Save(pe, _w.Session.Document.Parameters)); }
        catch { saved = null; }
        long memCk = Managed(); int nCk = pe.CheckpointCount;
        pe.ForgetCheckpoints();
        long memNoCk = Managed();
        await Task.Run(() => { pe.Invalidate(); pe.Rebuild(null); });
        bool same = Math.Abs(pe.Volume - ve) <= 1e-6 * Math.Max(1, ve) && pe.Features.All(f => f.Error == null);
        string sameWhy = same ? "" : $" (объём {pe.Volume:0.###} против {ve:0.###}; ошибки после пересчёта: {string.Join("; ", pe.Features.Where(f => f.Error != null).Select(f => f.Name + " — " + f.Error!.Split('\n')[0][..Math.Min(140, f.Error!.Split('\n')[0].Length)]))})";
        // отмена всех правок сессии — исходная деталь
        _phase = "нагрузка: отмена всех правок";
        int guard = 0;
        while (_w.Session.UndoNames.Count > undo0 && guard++ < 1000) { _w.Session.Undo(); await WaitBuilt(); }
        var p0 = _w.Session.Part!;
        bool back = p0.Features.Count == feat0 && Math.Abs(p0.Volume - v0) <= 1e-6 * v0;
        bool alive = await Alive();
        times.Sort();
        double med = times.Count > 0 ? times[times.Count / 2] : double.NaN, p95 = times.Count > 0 ? times[(int)(times.Count * 0.95)] : double.NaN;
        bool memOk = growMb < 64;
        bool ok = dead == 0 && done >= N / 2 && undoOk == undoTries && same && back && alive && memOk;
        Count(ok);
        Line($"Правок: {done} принято, {refused} отклонено ядром (отказ — с сообщением, деталь не меняется) из {N} за {secs:0.0} с; на правку медиана {F(med)} мс, p95 {F(p95)} мс");
        var ls = _stalls.Where(x => x.Phase.StartsWith("нагрузка")).ToList();
        Line($"Окно рисует после каждой правки — {(dead == 0 ? "да" : $"НЕТ ({dead})")}; остановок окна дольше 100 мс за раздел: {ls.Count}{(ls.Count > 0 ? $", наибольшая {F(ls.Max(x => x.Ms))} мс" : "")}");
        Line($"«Отменить» ×3 и «вернуть» ×3 каждые 10 правок: деталь та же — {undoOk}/{undoTries}");
        foreach (var w in undoWhy) Line("    " + w);
        Line($"Память после сборки мусора: после 10 правок {mem10 / 1048576.0:0.0} МБ, после {N} — {memEnd / 1048576.0:0.0} МБ (рост {growMb:+0.0;-0.0} МБ, допустимо < 64); рабочий набор {ws10:0} → {wsEnd:0} МБ — {(memOk ? "годно" : "РОСТ")}");
        Line($"    из них сохранённые состояния детали ({nCk}): {(memCk - memNoCk) / 1048576.0:0.0} МБ; без них — {memNoCk / 1048576.0:0.0} МБ");
        if (saved != null) Line($"Деталь сессии сохранена: {saved}");
        Line($"Пересчёт с нуля после сессии: тот же объём — {(same ? "да" : "НЕТ" + sameWhy)}; отмена всех правок вернула исходную деталь — {(back ? "да" : "НЕТ")}; итог — {(ok ? "годно" : "ОШИБКА")}");
    }

    // ---------------------------------------------------------------- ручки и последовательности (u09)

    private static PickRef EdgeRef(Part p, Func<Kernel.Topology.TEdge, bool> pick) =>
        p.Bodies.SelectMany(b => b.Edges).Where(pick).Select(e => new PickRef(e.Id, PickKind.Edge, p, null)).FirstOrDefault();

    private static double TopZ(Part p) => p.Bodies.SelectMany(b => b.Vertices).Max(v => v.P.Z);

    /// <summary>Верхнее прямое ребро (вдоль X или Y) на наибольшей высоте.</summary>
    private static PickRef TopEdge(Part p, int axis = 0)
    {
        double z = TopZ(p);
        bool Sharp(Kernel.Topology.TEdge e) => e.Curve is Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.Z - z) < 1e-6 && Math.Abs(e.V1.P.Z - z) < 1e-6
                               && Parametric3D.App.Operations.Topo.EdgeHandle(p, e.Id) != null   // острое ребро, не шов скругления
                               && (e.V0.P - e.V1.P).Length > 5;
        var r = EdgeRef(p, e => Sharp(e) && (axis == 0 ? Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-6 : Math.Abs(e.V0.P.X - e.V1.P.X) < 1e-6));
        // по нужной оси острого верхнего ребра нет (вырез верхней грани снял верх целиком) — любое острое верхнее
        return r.Owner != null ? r : EdgeRef(p, Sharp);
    }

    private static PickRef TopVertex(Part p)
    {
        double z = TopZ(p);
        // вершина, где сходятся только прямые рёбра (скругление вершины — для таких)
        var tops = p.Bodies.SelectMany(b => b.Vertices).Where(x => Math.Abs(x.P.Z - z) < 1e-6).OrderBy(x => x.P.X + x.P.Y).ToList();
        var v = tops.FirstOrDefault(x => x.Edges.All(e => e.Curve is Kernel.Curves.LineCurve3)) ?? tops.First();
        return new PickRef(v.Id, PickKind.Vertex, p, null);
    }

    private static PickRef TopFace(Part p)
    {
        foreach (var f in p.Bodies.SelectMany(b => b.Faces))
            if (Parametric3D.App.Operations.Refs.FacePlane(p, f.Id) is { } pl && pl.N.Z > 0.999) return new PickRef(f.Id, PickKind.Face, p, null);
        return default;
    }

    /// <summary>
    /// Ручка в окне: тянуть 1.5 с (шаг 16 мс) от 0 до величины со знаком, затем отпустить и подтвердить. Считается: время
    /// шага в потоке окна, сколько раз деталь обновилась на экране, сколько длится расчёт предпросмотра, за сколько деталь
    /// догоняет мышь после остановки, время от «отпустили» до готовой детали.
    /// </summary>
    private async Task HandleCase(string name, DocumentSession s, Func<Part, PickRef> pick, double target, bool full, bool chain = true)
    {
        _ct.ThrowIfCancellationRequested();
        string mode = full ? "полный" : "контур";
        Step($"ручка: {name} ({mode})");
        _phase = $"ручка: {name} ({mode})";
        UserPrefs.Current.PreviewMode = full ? "full" : "contour";
        _w.SetSession(s);
        await WaitBuilt();
        await Task.Delay(150);
        var v = _w.ViewPort;
        var part = s.Part!;
        int f0 = part.Features.Count;
        var r = pick(part);
        if (r.Owner == null) { Line($"{name} ({mode}): нет подходящего элемента"); Count(false); return; }
        var sel = chain && r.Kind == PickKind.Edge && v.ExpandEdge != null ? v.ExpandEdge(r) : new List<PickRef> { r };
        v.Selection.Clear();
        foreach (var x in sel) v.Selection.Select(x, additive: true);
        await Task.Delay(100);
        if (v.Handle == null) { Line($"{name} ({mode}): у элемента нет ручки"); Count(false); return; }
        var doc = s.Document;
        int scenes = 0;
        var builds = new List<double>();
        long bt0 = 0;
        void OnScene() => scenes++;
        void OnStart() => bt0 = Stopwatch.GetTimestamp();
        void OnFinish() { if (bt0 != 0) builds.Add(Stopwatch.GetElapsedTime(bt0).TotalMilliseconds); bt0 = 0; }
        v.SceneRebuilt += OnScene; doc.BuildStarted += OnStart; doc.BuildFinished += OnFinish;
        var steps = new List<double>();
        try
        {
            var sw = Stopwatch.StartNew();
            int n = 0;
            int scenes0 = scenes;
            while (sw.ElapsedMilliseconds < 1500)
            {
                double f = Math.Min(1, sw.ElapsedMilliseconds / 1500.0);
                var t = Stopwatch.GetTimestamp();
                v.SimulateHandleDrag(target * (0.2 + 0.8 * f), end: false);
                steps.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                n++;
                await Task.Delay(16);
            }
            v.SimulateHandleDrag(target, end: false);
            int liveScenes = scenes - scenes0;
            // мышь остановилась: деталь догоняет последнюю величину
            var st = Stopwatch.StartNew();
            await WaitBuilt();
            double settle = st.Elapsed.TotalMilliseconds;
            st.Restart();
            v.SimulateHandleDrag(target, end: true);
            await WaitBuilt();
            await Task.Delay(30);
            double release = st.Elapsed.TotalMilliseconds;
            var op = _w.CurrentOperation;
            string? err = part.Features.Count > f0 ? part.Features[^1].Error : "операция не добавлена";
            _w.CommitOperation();
            await WaitBuilt();
            bool ok = err == null && part.Features.Count == f0 + 1 && part.Integrity == null;
            Count(ok);
            double perSec = liveScenes / 1.5;
            Line($"{name,-44} {mode,-7} шаг {F(Median(steps)),5} мс | обновлений {liveScenes,3} ({F(perSec)}/с), расчёт предпросмотра медиана {F(Median(builds)),7} мс | " +
                 $"догоняет {F(settle),7} мс | отпустили → готово {F(release),7} мс | {(ok ? "годно" : "ОШИБКА: " + (err ?? part.Integrity))}");
            if (full) _handleRows.Add((name, Median(builds), perSec));
        }
        finally
        {
            v.SceneRebuilt -= OnScene; doc.BuildStarted -= OnStart; doc.BuildFinished -= OnFinish;
            if (_w.CurrentOperation != null) _w.CancelOperation();
        }
    }

    private readonly List<(string Name, double BuildMs, double PerSec)> _handleRows = new();

    private async Task Handles()
    {
        Head("8б. Ручки операций в окне (предпросмотр полный и контуром)");
        var keepMode = UserPrefs.Current.PreviewMode;
        try
        {
            Line("шаг — время одного движения мыши в потоке окна; обновлений — сколько раз деталь перерисована за 1.5 с перетаскивания;");
            Line("расчёт предпросмотра — один расчёт детали при перетаскивании; догоняет — после остановки мыши; отпустили — окончательный расчёт.");
            DocumentSession Cube() => DocumentSession.Sample();
            DocumentSession CubeRounded()
            {
                var s = DocumentSession.Sample();
                var p = s.Part!;
                p.Rebuild(null);
                var e = TopEdge(p, 0);
                var rf = new RoundFeature { Radius = new ValueRef(30), Name = "R30" };
                if (p.FindEdge(e.Id) is { } te) rf.Edges.Add(Kernel.Topology.EdgeRef.Of(te));
                p.AddFeature(rf);
                return s;
            }
            DocumentSession Heavy() => DocumentSession.FromPart(TestParts.Realistic(1, 40), "40 операций");
            await HandleCase("куб: скругление ребра R 20", Cube(), p => TopEdge(p), -20, true);
            await HandleCase("куб: фаска ребра 15", Cube(), p => TopEdge(p), 15, true);
            await HandleCase("куб: скругление ребра R 20", Cube(), p => TopEdge(p), -20, false);
            await HandleCase("куб со скруглением: цепочка через дугу R 15", CubeRounded(), p => TopEdge(p, 1), -15, true);
            await HandleCase("куб: скругление вершины R 15", Cube(), TopVertex, -15, true);
            await HandleCase("куб: фаска угла 15", Cube(), TopVertex, 15, true);
            await HandleCase("куб: выдавливание грани 30", Cube(), TopFace, 30, true);
            await HandleCase("куб: вырез грани 30", Cube(), TopFace, -30, true);
            await HandleCase("40 операций: скругление ребра R 3", Heavy(), p => EdgeRef(p, e => e.Curve is Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5), -3, true);
            await HandleCase("40 операций: скругление ребра R 3", Heavy(), p => EdgeRef(p, e => e.Curve is Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5), -3, false);
            await HandleCase("40 операций: фаска ребра 2", Heavy(), p => EdgeRef(p, e => e.Curve is Kernel.Curves.LineCurve3 && (e.V0.P - e.V1.P).Length > 5), 2, true);
            foreach (var (n, ms, ps) in _handleRows.Where(x => x.Name.StartsWith("куб")))
                Target($"Ручка, полный предпросмотр — {n}: расчёт шага", "≤ 100 мс", ms, 100);

            // ПОСЛЕДОВАТЕЛЬНО: одна деталь, операции ручками одна за другой (каждая — тянуть, отпустить, ОК)
            Line("");
            Line("Последовательность ручками на одной детали (куб 100): тянуть → отпустить → ОК; время от «отпустили» до готовой детали и всей операции:");
            var seq = DocumentSession.Sample();
            UserPrefs.Current.PreviewMode = "full";
            var steps = new (string Name, Func<Part, PickRef> Pick, double V)[]
            {
                ("скругление верхнего ребра X R 10", p => TopEdge(p, 0), -10),
                ("фаска верхнего ребра Y 8", p => TopEdge(p, 1), 8),
                ("скругление вершины R 6", TopVertex, -6),
                ("выдавливание верхней грани 20", TopFace, 20),
                ("скругление верхнего ребра X R 4", p => TopEdge(p, 0), -4),
                ("вырез верхней грани 5", TopFace, -5),
                ("фаска верхнего ребра Y 3", p => TopEdge(p, 1), 3),
                ("скругление верхнего ребра X R 1.5", p => TopEdge(p, 0), -1.5),   // R2 ровно съедает соседнее R4 целиком (вырожденный случай)
            };
            _w.SetSession(seq);
            await WaitBuilt();
            var total = Stopwatch.StartNew();
            int i = 0;
            foreach (var (n, pick, val) in steps)
            {
                i++;
                _ct.ThrowIfCancellationRequested();
                _phase = "последовательность: " + n;
                var part = seq.Part!;
                int f0 = part.Features.Count;
                if (MeshOffFaces(part) is { Length: > 0 } off) Line($"      сетка не согласована с телом перед шагом: {off}");
                var r = pick(part);
                if (r.Owner == null) { Line($"  {i,2}. {n,-36} нет элемента"); Count(false); continue; }
                if (r.Kind == PickKind.Face && Refs.FacePlane(part, r.Id) is { } fpl) Line($"      грань #{r.Id}: точка {fpl.P:0.##}, нормаль {fpl.N:0.##}");
                var v = _w.ViewPort;
                var sel = r.Kind == PickKind.Edge && v.ExpandEdge != null ? v.ExpandEdge(r) : new List<PickRef> { r };
                v.Selection.Clear();
                foreach (var x in sel) v.Selection.Select(x, additive: true);
                await Task.Delay(60);
                if (v.Handle == null) { Line($"  {i,2}. {n,-36} нет ручки"); Count(false); continue; }
                var sw = Stopwatch.StartNew();
                for (int k = 1; k <= 10; k++) { v.SimulateHandleDrag(val * k / 10, end: false); await Task.Delay(16); }
                var rel = Stopwatch.StartNew();
                v.SimulateHandleDrag(val, end: true);
                await WaitBuilt();
                double release = rel.Elapsed.TotalMilliseconds;
                string? err = part.Features.Count > f0 ? part.Features[^1].Error : "операция не добавлена";
                double op = part.Features.Count > f0 && part.LastFeatureMs.TryGetValue(part.Features[^1], out var ms) ? ms : double.NaN;
                _w.CommitOperation();
                await WaitBuilt();
                bool ok = err == null && part.Features.Count == f0 + 1;
                Count(ok);
                Line($"  {i,2}. {n,-36} отпустили → готово {F(release),7} мс (операция в ядре {F(op),7} мс), всего с перетаскиванием {F(sw.Elapsed.TotalMilliseconds),7} мс  {(ok ? "годно" : "ОШИБКА: " + err)}");
            }
            Line($"  Вся последовательность: {F(total.Elapsed.TotalMilliseconds)} мс; операций в детали {seq.Part!.Features.Count}, объём {F(seq.Part.Volume, "0.###")} мм³");
            // полный пересчёт той же детали
            var pp = seq.Part!;
            var fs = await Task.Run(() => { pp.Invalidate(); var sw = Stopwatch.StartNew(); pp.Rebuild(null); return sw.Elapsed.TotalMilliseconds; });
            Line($"  Полный пересчёт получившейся детали: {F(fs)} мс");
            Line("  по операциям: " + string.Join("; ", pp.Features.Select(f => $"{f.Name} {F(pp.LastFeatureMs.TryGetValue(f, out var fm) ? fm : double.NaN, "0")}")));
            try
            {
                var file = Path.Combine(Path.GetDirectoryName(ReportPath) ?? ".", $"P3D_sequence_{_started:yyyyMMdd_HHmmss}.p3d");
                seq.SaveAs(file);
                Line($"  Деталь последовательности сохранена: {file}");
            }
            catch (Exception ex) { Line("  сохранить деталь не удалось: " + ex.Message); }
        }
        finally { UserPrefs.Current.PreviewMode = keepMode; }
    }

    /// <summary>Сетка показа и тело: грани, чьи треугольники не лежат на их поверхности (номера граней сетки устарели).</summary>
    private static string MeshOffFaces(Part part)
    {
        if (part.Mesh is not { } md) return "";
        var faces = part.Bodies.SelectMany(b => b.Faces).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var bad = new Dictionary<uint, double>();
        foreach (var v in md.Vertices)
        {
            if (!faces.TryGetValue(v.FaceId, out var f)) { bad[v.FaceId] = double.PositiveInfinity; continue; }
            var q = new Vector3d(v.P.X, v.P.Y, v.P.Z);
            var src = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface;
            double d; try { d = (src.PointAt(src.ProjectToUV(q)) - q).Length; } catch { continue; }
            if (d > 0.05 && (!bad.TryGetValue(v.FaceId, out var w0) || d > w0)) bad[v.FaceId] = d;
        }
        return string.Join(", ", bad.Take(6).Select(kv => $"#{kv.Key} {(faces.TryGetValue(kv.Key, out var ff) ? ff.Tag : "нет грани")} {kv.Value:G3}"));
    }

    // ---------------------------------------------------------------- u10: скругления и фаски «как пользователь»

    /// <summary>
    /// 60 случайных острых рёбер реалистичных деталей (с касательной цепочкой, как выбирает окно) — скругление или фаска
    /// случайного размера. Время каждой операции; сводка по видам ребра и по причинам: годно, медленно (> 250 мс — обходной
    /// путь резцом), отказ (обычно размер больше соседней грани — так и должно быть). Совпадение красного контура с отказом.
    /// </summary>
    private async Task UserEdges()
    {
        Head("8в. Скругления и фаски «как пользователь» (случайные рёбра реалистичных деталей)");
        _phase = "случайные рёбра";
        var rows = await Task.Run(() =>
        {
            var rnd = new Random(2026);
            var parts = Enumerable.Range(1, 6).Select(sd => { var pp = TestParts.Realistic(sd, 30); pp.Rebuild(null); return pp; }).ToList();
            var res = new List<(string Kind, double Ms, string? Err, bool Predicted)>();
            for (int t = 0; t < 60 && !_ct.IsCancellationRequested; t++)
            {
                var p = parts[rnd.Next(parts.Count)];
                var all = Topo.AllEdges(p);
                var sharp = all.Where(e => e.Curve != null && Topo.EdgeHandle(p, e.Id) != null).ToList();
                if (sharp.Count == 0) continue;
                var e0 = sharp[rnd.Next(sharp.Count)];
                var chain = Topo.TangentChain(e0, all);
                bool round = rnd.NextDouble() < 0.6;
                double size = Math.Round(0.5 + rnd.NextDouble() * 4, 1);
                bool predicted = Topo.EdgeGhostValid(p, chain.Select(c => c.Id), round)?.Invoke(size) ?? true;
                Feature f;
                if (round) { var rf = new RoundFeature { Radius = new ValueRef(size) }; foreach (var c in chain) rf.Edges.Add(Kernel.Topology.EdgeRef.Of(c)); f = rf; }
                else { var cf = new ChamferFeature { Mode = ChamferKind.TwoDistances, D1 = new ValueRef(size), D2 = new ValueRef(size) }; foreach (var c in chain) cf.Edges.Add(Kernel.Topology.EdgeRef.Of(c)); f = cf; }
                p.AddFeature(f);
                p.Rebuild(null);
                double ms = p.LastFeatureMs.TryGetValue(f, out var m) ? m : double.NaN;
                string kinds = string.Join("+", chain.Select(c => c.Curve is Kernel.Curves.LineCurve3 ? "прямое" : c.Curve is Kernel.Curves.ArcCurve3 ? "дуга" : "кривое").Distinct().OrderBy(x => x));
                res.Add(($"{(round ? "скругление" : "фаска")} {kinds}", ms, f.Error, predicted));
                p.RemoveFeature(f); p.Rebuild(null);
            }
            return res;
        });
        Line($"{"вид",-34} {"число",6} {"медиана, мс",12} {"наиб., мс",10} {"медленно",9} {"отказ",6}");
        foreach (var g in rows.GroupBy(r => r.Kind).OrderBy(g => g.Key))
            Line($"{g.Key,-34} {g.Count(),6} {F(Median(g.Select(x => x.Ms).ToList())),12} {F(g.Max(x => x.Ms)),10} {g.Count(x => x.Ms > 250),9} {g.Count(x => x.Err != null),6}");
        var times = rows.Select(r => r.Ms).ToList();
        int mism = rows.Count(r => r.Predicted != (r.Err == null));
        Line($"Всего {rows.Count}: медиана {F(Median(times))} мс, p95 {F(P95(times))} мс, сумма {F(times.Sum())} мс; медленных {rows.Count(r => r.Ms > 250)}, отказов {rows.Count(r => r.Err != null)}");
        Line($"Красный контур совпал с исходом в {rows.Count - mism} из {rows.Count}");
        foreach (var g in rows.Where(r => r.Err != null).GroupBy(r => System.Text.RegularExpressions.Regex.Replace(r.Err!, @"[-\d.]+", "#")).Take(6))
            Line($"  отказ ×{g.Count()}: {Cut(g.Key, 150)}");
        Count(true);
        Target("Скругление/фаска случайного ребра, p95", "≤ 500 мс", P95(times), 500);
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
