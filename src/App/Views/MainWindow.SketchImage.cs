using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k184 (замечание 09.11, 06:35, п. 3): ИЗОБРАЖЕНИЕ В ЭСКИЗЕ — кнопка «Импорт изображения» на панели фигур эскиза (png, jpg,
/// bmp; левый нижний угол — в начале эскиза), слой изображений с ручками (SketchImageLayer) и свойства: имя файла, размер в
/// пикселях; правятся место (центр), размеры на эскизе и угол поворота.
/// </summary>
public partial class MainWindow
{
    public SketchImageLayer ImageLayer { get; private set; } = null!;

    private void SetupSketchImages()
    {
        ImageLayer = new SketchImageLayer(View);
        ViewArea.Children.Insert(ViewArea.Children.IndexOf(GlyphLayer), ImageLayer);
        ImageLayer.SelectionChanged += () => { if (ImageLayer.Selected != null) View.Selection.Clear(); ShowProperties(); };
        ImageLayer.Dragging += () => { if (_propsOn && CurrentOperation == null && ImageLayer.Selected != null) ShowProperties(); };
        ImageLayer.Edited += () => { SketchCheckpoint(); View.InvalidateOverlay(); ShowProperties(); };
        // щелчок вне изображения (вид получает его сам) снимает выбор изображения
        ViewArea.AddHandler(PointerPressedEvent, (_, e) => { if (!ReferenceEquals(e.Source, ImageLayer)) ImageLayer.Select(null); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        View.SketchEntered += _ => ImageLayer.Select(null);
    }

    /// <summary>Выбрать файл и вставить изображение в активный эскиз.</summary>
    public async Task ImportSketchImageAsync()
    {
        if (View.ActiveSketch is not { } sk) { ShowToast(L("Изображение вставляется в эскиз — откройте эскиз")); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Импорт изображения"), AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(L("Изображения")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.PNG", "*.JPG", "*.JPEG", "*.BMP" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try { InsertSketchImage(sk, path); }
        catch (Exception ex) { ShowToast(L("Импорт изображения: {0}", ex.Message)); }
    }

    /// <summary>Вставка изображения из файла в эскиз (выбирается; шаг отмены эскиза).</summary>
    public SkImage InsertSketchImage(Sketch sk, string path)
    {
        var data = File.ReadAllBytes(path);
        Avalonia.Media.Imaging.Bitmap bmp;
        using (var ms = new MemoryStream(data)) bmp = new Avalonia.Media.Imaging.Bitmap(ms);
        var im = SkImage.Create(Path.GetFileName(path), data, bmp.PixelSize.Width, bmp.PixelSize.Height);
        bmp.Dispose();
        sk.Images.Add(im);
        SketchCheckpoint();
        View.InvalidateOverlay();
        ImageLayer.Select(im);
        return im;
    }

    private void ShowSketchImageProperties(Sketch sk, SkImage im)
    {
        PropsTitle.Text = L("ИЗОБРАЖЕНИЕ");
        void Apply(Action change)
        {
            change();
            View.InvalidateOverlay(); ImageLayer.InvalidateVisual();
            SketchCheckpoint();
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowProperties);
        }
        void NumRow(string label, double v, Action<double> set, Func<double, bool>? ok = null)
            => EditRow(label, Num(v), s => { if (TryNum(s, out var x) && (ok == null || ok(x))) Apply(() => set(x)); else { ShowToast(L("Неверное значение: {0}", s)); Avalonia.Threading.Dispatcher.UIThread.Post(ShowProperties); } });

        Row("Файл", im.FileName);
        Row("Размер изображения, px", $"{im.PixelWidth} × {im.PixelHeight}");
        NumRow("Центр X", im.Center.X, x => im.Center = new Vector2d(x, im.Center.Y));
        NumRow("Центр Y", im.Center.Y, y => im.Center = new Vector2d(im.Center.X, y));
        NumRow("Ширина", im.Width, w => im.Width = w, w => w > 0);
        NumRow("Высота", im.Height, h => im.Height = h, h => h > 0);
        NumRow("Поворот, °", im.Angle, a => im.Angle = a);
        NumRow("Прозрачность, %", Math.Round((1 - im.Opacity) * 100), t => im.Opacity = 1 - t / 100, t => t >= 0 && t <= 95);
        var keep = new Button { Content = L("Пропорции файла") };
        keep.Click += (_, _) => Apply(() => { if (im.PixelWidth > 0) im.Height = im.Width * im.PixelHeight / im.PixelWidth; });
        var del = new Button { Content = L("Удалить изображение") };
        del.Click += (_, _) => { sk.Images.Remove(im); ImageLayer.Select(null); SketchCheckpoint(); View.InvalidateOverlay(); ShowProperties(); };
        PropsButtons.Children.Add(keep);
        PropsButtons.Children.Add(del);
    }

    /// <summary>Delete при выбранном изображении — удалить его.</summary>
    internal bool DeleteSelectedSketchImage()
    {
        if (ImageLayer?.Selected is not { } im || View.ActiveSketch is not { } sk || !sk.Images.Remove(im)) return false;
        ImageLayer.Select(null); SketchCheckpoint(); View.InvalidateOverlay(); ShowProperties();
        return true;
    }
}
