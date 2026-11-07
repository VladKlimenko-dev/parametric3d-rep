using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k150: окно «Тесты ядра» (замечание 03.11, 18:33). Наборы проверок ядра (K01Checks: allA, allB, фикстуры лофтов,
/// последовательный корпус) собираются из исходников клона и идут отдельным процессом; всё, что они печатают, пишется в
/// файл отчёта (Документы\Parametric3D\Tests\P3D_kernel_*.txt) вместе со сведениями о компьютере и версии. В окне — ход:
/// число пройденных проверок и упавшие. Отчёт присылается разработчику.
/// </summary>
public sealed class KernelTestWindow : Window
{
    /// <summary>Наборы: режим K01Checks, подпись, отмечен по умолчанию.</summary>
    public static readonly (string Mode, string Label, bool On)[] Suites =
    {
        ("allA", "allA — основные проверки ядра", true),
        ("allB", "allB — расширенные проверки ядра", true),
        ("loftcorpus140", "Фикстуры лофтов (скругления и фаски)", false),
        ("loftseq", "Последовательный корпус лофтов (60 операций)", false),
        ("allC", "Tests AllC — все операции × размеры, пары и тройки операций (время и параметры каждого шага)", false),
    };

    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
        FontSize = 12, MinHeight = 260,
    };
    private readonly List<CheckBox> _boxes = new();
    private readonly Button _start, _folder, _close;
    private readonly ProgressBar _bar = new() { IsIndeterminate = false, Height = 4 };
    private CancellationTokenSource? _cts;
    private Process? _proc;
    private string? _report;

    public KernelTestWindow() : this(null) { }

    /// <summary>k181: <paramref name="only"/> — отметить только этот набор (пункт меню «Tests AllC»).</summary>
    public KernelTestWindow(string? only)
    {
        Title = L("Тесты ядра");
        Width = 780; Height = 620; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("P3.PanelBg"));

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = L("Приложение соберёт проверки ядра из исходников этой папки и запустит выбранные наборы отдельным процессом. Всё, что они напечатают, и сведения о компьютере пишутся в файл отчёта — пришлите его. allA и allB вместе идут от получаса до пары часов (зависит от процессора); приложением можно пользоваться, но тяжёлые операции замедлят проверки. «Остановить» прерывает прогон, отчёт сохраняется."),
        };
        var boxes = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (mode, label, on) in Suites)
        {
            var cb = new CheckBox { Content = L(label), IsChecked = only == null ? on : mode == only, Name = "kt_" + mode, Tag = mode };
            _boxes.Add(cb); boxes.Children.Add(cb);
        }
        _start = new Button { Content = L("Запустить"), Classes = { "accent" }, MinWidth = 110, Name = "kt_start" };
        _folder = new Button { Content = L("Открыть папку"), IsEnabled = false };
        _close = new Button { Content = L("Закрыть"), MinWidth = 90 };
        _start.Click += async (_, _) => await Start();
        _folder.Click += async (_, _) => await OpenFolder();
        _close.Click += (_, _) => { if (_cts != null) Stop(); else Close(); };
        Closing += (_, e) => { if (_cts != null) { e.Cancel = true; Stop(); } };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, _folder } };
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_close, Dock.Right);
        bottom.Children.Add(_close);
        bottom.Children.Add(buttons);

        var top = new StackPanel { Children = { intro, boxes } };
        intro.Margin = new Thickness(0, 0, 0, 8);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_bar, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _bar.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(top);
        root.Children.Add(_bar);
        root.Children.Add(bottom);
        root.Children.Add(_log);
        Content = root;
    }

    private void Add(string s)
    {
        _log.Text = (_log.Text ?? "") + s + "\n";
        _log.CaretIndex = _log.Text.Length;
    }

    private void Stop()
    {
        _cts?.Cancel();
        try { if (_proc is { HasExited: false } p) p.Kill(true); } catch { }
    }

    /// <summary>Корень клона: вверх от папки приложения — где есть tests/k01/K01Checks.csproj и src.</summary>
    public static string? RepoRoot(string? from = null)
    {
        // P3D_REPO — корень клона явно (приложение собрано в другую папку)
        var d = new DirectoryInfo(from ?? Environment.GetEnvironmentVariable("P3D_REPO") ?? AppContext.BaseDirectory);
        for (; d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tests", "k01", "K01Checks.csproj")) && Directory.Exists(Path.Combine(d.FullName, "src")))
                return d.FullName;
        return null;
    }

    /// <summary>Папка отчётов — та же, что у «Теста» (Документы\Parametric3D\Tests).</summary>
    public static string ReportDir()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try { var dir = Path.Combine(docs, "Parametric3D", "Tests"); Directory.CreateDirectory(dir); return dir; }
        catch { var dir = Path.Combine(Path.GetTempPath(), "Parametric3D", "Tests"); Directory.CreateDirectory(dir); return dir; }
    }

    /// <summary>Сведения о компьютере для отчёта: ОС, процессор, ядра, память, .NET.</summary>
    public static IEnumerable<string> MachineInfo()
    {
        yield return "ОС: " + RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture;
        yield return "Процессор: " + CpuName() + $"; логических ядер: {Environment.ProcessorCount}";
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        yield return $"Память: {ram / (1024.0 * 1024 * 1024):0.0} ГБ";
        yield return ".NET: " + RuntimeInformation.FrameworkDescription;
    }

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim() ?? "?";
            if (File.Exists("/proc/cpuinfo"))
                return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "?";
        }
        catch { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "?";
    }

    /// <summary>Итог строки вывода K01Checks: проверка (имя, прошла) или null.</summary>
    public static (string Test, bool Ok, string? Error)? ParseResult(string line)
    {
        if (!line.StartsWith("{\"test\"")) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            string? err = r.TryGetProperty("error", out var e) ? e.ToString() : null;
            return (r.GetProperty("test").ToString(), ok.GetBoolean(), err);
        }
        catch { return null; }
    }

    private async Task Start()
    {
        var modes = _boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToList();
        if (modes.Count == 0) { Add(L("Не выбран ни один набор.")); return; }
        _start.IsEnabled = false; _folder.IsEnabled = false;
        foreach (var b in _boxes) b.IsEnabled = false;
        _close.Content = L("Остановить");
        _bar.IsIndeterminate = true;
        _log.Text = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        void Ui(string s) => Dispatcher.UIThread.Post(() => Add(s));
        try { _report = await Task.Run(() => Run(modes, Ui, ct)); }
        catch (Exception ex) { Add(ex.Message); }
        finally
        {
            _cts = null; _proc = null;
            _bar.IsIndeterminate = false;
            _close.Content = L("Закрыть");
            _start.Content = L("Повторить");
            _start.IsEnabled = true;
            foreach (var b in _boxes) b.IsEnabled = true;
            _folder.IsEnabled = _report != null;
        }
    }

    private async Task<string?> Run(List<string> modes, Action<string> ui, CancellationToken ct)
    {
        var root = RepoRoot();
        if (root == null) { ui(L("Нет исходников проверок (tests/k01): запустите приложение через run.bat из папки репозитория.")); return null; }
        var started = DateTime.Now;
        string report = Path.Combine(ReportDir(), $"P3D_kernel_{started:yyyyMMdd_HHmmss}.txt");
        await using var w = new StreamWriter(report, false, new UTF8Encoding(true)) { AutoFlush = true };
        void Both(string s) { w.WriteLine(s); ui(s); }
        Both("Parametric3D — тесты ядра, отчёт " + started.ToString("yyyy-MM-dd HH:mm:ss"));
        Both("Версия: " + (await Capture("git", $"-C \"{root}\" log -1 --format=\"%h %ad %s\" --date=short", root, ct) ?? "?").Trim());
        foreach (var s in MachineInfo()) Both(s);
        Both("Наборы: " + string.Join(", ", modes));
        Both("Отчёт: " + report);
        Both("");

        // сборка проверок: из NuGet; не вышло — автономно (tools/deps)
        string outDir = Path.Combine(root, "tests", "k01", "bin", "kerneltest");
        string proj = Path.Combine(root, "tests", "k01", "K01Checks.csproj");
        Both(L("Сборка проверок…"));
        var sw = Stopwatch.StartNew();
        int code = await RunProcess("dotnet", $"build \"{proj}\" -c Release -o \"{outDir}\" -nologo -v:q", root, null, l => { w.WriteLine("  build: " + l); if (l.Contains(" error ")) ui("  " + l); }, ct);
        if (code != 0 && !ct.IsCancellationRequested)
        {
            Both(L("Сборка из NuGet не удалась — повтор автономно (tools/deps)…"));
            code = await RunProcess("dotnet", $"build \"{proj}\" -c Release -o \"{outDir}\" -nologo -v:q -p:LocalDeps=\"{Path.Combine(root, "tools", "deps")}\"", root, null, l => { w.WriteLine("  build: " + l); if (l.Contains(" error ")) ui("  " + l); }, ct);
        }
        Both($"Сборка: {(code == 0 ? "ok" : "ошибка " + code)}, {sw.Elapsed.TotalSeconds:0} с");
        if (code != 0) return report;
        string dll = Path.Combine(outDir, "K01Checks.dll");

        var summary = new List<string>();
        foreach (var mode in modes)
        {
            if (ct.IsCancellationRequested) break;
            Both("");
            Both($"=== {mode} ===");
            string args = mode == "loftseq" ? $"\"{dll}\" loftseq 10 101 6 \"{Path.Combine(Path.GetTempPath(), "Parametric3D", "loftseq")}\"" : $"\"{dll}\" {mode}";
            var env = new Dictionary<string, string> { ["P3D_USERMODELS"] = Path.Combine(root, "tests", "ui", "data") };
            int passed = 0, failed = 0, exact = 0; var fails = new List<string>(); string? total = null;
            sw.Restart();
            code = await RunProcess("dotnet", args, root, env, l =>
            {
                w.WriteLine(l);
                if (ParseResult(l) is { } r)
                {
                    if (r.Ok) passed++; else { failed++; fails.Add(r.Test); ui($"  ✗ {r.Test}: {(r.Error is { } er && er.Length > 160 ? er[..160] + "…" : r.Error)}"); }
                    if ((passed + failed) % 20 == 0) ui($"  {mode}: {passed + failed} {L("проверок")}, {L("упало")} {failed}, {sw.Elapsed:hh\\:mm\\:ss}");
                }
                else if (l.StartsWith("{\"passed\"") || l.StartsWith("{\"ok\":")) { total = l; ui("  " + l); }
                else if (mode == "loftseq" && l.Contains("\"guarantee\":\"ExactAnalytic\"")) exact++;
            }, ct);
            string line = $"{mode}: {(ct.IsCancellationRequested ? "остановлен" : "код " + code)}, {sw.Elapsed:hh\\:mm\\:ss}, итог {total ?? "—"}" + (mode == "loftseq" ? $", точных {exact}" : "") + (fails.Count > 0 ? ", упали: " + string.Join(", ", fails) : "");
            summary.Add(line);
            Both(line);
        }
        Both("");
        Both("=== ИТОГ ===");
        foreach (var s in summary) Both(s);
        Both($"Всего: {(DateTime.Now - started):hh\\:mm\\:ss}" + (ct.IsCancellationRequested ? " (остановлено)" : ""));
        Both(L("Пришлите файл отчёта: {0}", report));
        return report;
    }

    private async Task<string?> Capture(string exe, string args, string dir, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try { int c = await RunProcess(exe, args, dir, null, l => sb.AppendLine(l), ct, track: false); return c == 0 ? sb.ToString() : null; }
        catch { return null; }
    }

    private async Task<int> RunProcess(string exe, string args, string dir, Dictionary<string, string>? env, Action<string> line, CancellationToken ct, bool track = true)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi };
        var gate = new object();
        void On(string? s) { if (s != null) lock (gate) line(s); }
        p.OutputDataReceived += (_, e) => On(e.Data);
        p.ErrorDataReceived += (_, e) => On(e.Data);
        p.Start();
        if (track) _proc = p;
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return -1; }
        p.WaitForExit();
        return p.ExitCode;
    }

    private async Task OpenFolder()
    {
        if (_report == null) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_report}\"") { UseShellExecute = true });
            else await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetDirectoryName(_report)!));
        }
        catch (Exception ex) { Add(ex.Message); }
    }
}
