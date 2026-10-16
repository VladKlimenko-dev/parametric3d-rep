using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Threading;
using Parametric3D.App.Diagnostics;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// Окно кнопки «Тест»: что будет сделано, «Начать», ход по шагам, по окончании — путь к отчёту, «Копировать отчёт»,
/// «Открыть папку». Во время теста приложение само открывает в окне тестовые детали; документ пользователя
/// возвращается по окончании.
/// </summary>
public sealed class SelfTestWindow : Window
{
    private readonly MainWindow _main;
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
        FontSize = 12, MinHeight = 260,
    };
    private readonly Button _start, _copy, _folder, _close;
    private readonly ProgressBar _bar = new() { IsIndeterminate = false, Height = 4 };
    private CancellationTokenSource? _cts;
    private string? _report;

    public SelfTestWindow(MainWindow main)
    {
        _main = main;
        Title = L("Тест");
        Width = 760; Height = 600; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("P3.PanelBg"));

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = L("Приложение построит детали и замерит время: каждую операцию отдельно, реалистичные детали (полное перестроение, правка, предпросмотр, память), 100 отверстий, долгую сессию, файл и экспорт, затем вид на OpenGL этого компьютера (кадры в секунду во всех стилях, выбор под курсором) и сценарии интерфейса. Займёт 2–5 минут; во время теста не трогайте мышь над окном. Документ откроется снова по окончании. Отчёт сохраняется в файл — пришлите его."),
        };
        _start = new Button { Content = L("Начать"), Classes = { "accent" }, MinWidth = 110 };
        _copy = new Button { Content = L("Копировать отчёт"), IsEnabled = false };
        _folder = new Button { Content = L("Открыть папку"), IsEnabled = false };
        _close = new Button { Content = L("Закрыть"), MinWidth = 90 };
        _start.Click += async (_, _) => await Start();
        _copy.Click += async (_, _) => await Copy();
        _folder.Click += async (_, _) => await OpenFolder();
        _close.Click += (_, _) => { if (_cts != null) _cts.Cancel(); else Close(); };
        Closing += (_, e) => { if (_cts != null) { e.Cancel = true; _cts.Cancel(); } };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, _copy, _folder } };
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_close, Dock.Right);
        bottom.Children.Add(_close);
        bottom.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(_bar, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        intro.Margin = new Thickness(0, 0, 0, 10);
        _bar.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(intro);
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

    private async Task Start()
    {
        _start.IsEnabled = false; _copy.IsEnabled = false; _folder.IsEnabled = false;
        _close.Content = L("Остановить");
        _bar.IsIndeterminate = true;
        _log.Text = "";
        _cts = new CancellationTokenSource();
        try
        {
            _report = await SelfTest.RunAsync(_main, s => Dispatcher.UIThread.Post(() => Add(s)), _cts.Token);
            Add("");
            Add(L("Отчёт: {0}", _report));
        }
        finally
        {
            _cts = null;
            _bar.IsIndeterminate = false;
            _close.Content = L("Закрыть");
            _start.Content = L("Повторить");
            _start.IsEnabled = true;
            _copy.IsEnabled = _folder.IsEnabled = _report != null;
        }
    }

    private async Task Copy()
    {
        if (_report == null || Clipboard is not { } cb) return;
        try { await cb.SetTextAsync(await File.ReadAllTextAsync(_report)); Add(L("Отчёт скопирован в буфер обмена.")); }
        catch (Exception ex) { Add(ex.Message); }
    }

    private async Task OpenFolder()
    {
        if (_report == null) return;
        try
        {
            var dir = Path.GetDirectoryName(_report)!;
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_report}\"") { UseShellExecute = true });
            else await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
        }
        catch (Exception ex) { Add(ex.Message); }
    }
}
