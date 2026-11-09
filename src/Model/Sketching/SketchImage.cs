using OpenTK.Mathematics;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// k184 (замечание 09.11, 06:35, п. 3): ИЗОБРАЖЕНИЕ В ЭСКИЗЕ (png, jpg, bmp) — подложка для обрисовки, как Sketch Picture
/// SolidWorks. Видно только в своём эскизе при его правке: не геометрия эскиза (не входит в контуры, решатель и связи), в 3D-виде
/// детали и сборки и в других эскизах не показывается. Положение — центр прямоугольника на плоскости эскиза (мм), размеры (мм)
/// и угол поворота (°, против часовой от оси X эскиза). Байты файла хранятся в документе (файл можно удалить).
/// </summary>
public sealed class SkImage
{
    public string FileName = "";
    public int PixelWidth, PixelHeight;
    public byte[] Data = Array.Empty<byte>();
    public Vector2d Center;
    public double Width, Height;
    public double Angle;
    /// <summary>Прозрачность показа 0…1 (1 — непрозрачно).</summary>
    public double Opacity = 0.75;
    /// <summary>k185 (замечание 09.11, 12:02, п. 4): показ инвертированным (белый фон — тёмным: линии эскиза видны).</summary>
    public bool Invert;

    public SkImage Copy() => new()
    {
        FileName = FileName, PixelWidth = PixelWidth, PixelHeight = PixelHeight, Data = Data,
        Center = Center, Width = Width, Height = Height, Angle = Angle, Opacity = Opacity, Invert = Invert,
    };

    /// <summary>Оси прямоугольника на плоскости эскиза: (вдоль ширины, вдоль высоты), единичные.</summary>
    public (Vector2d U, Vector2d V) Axes
    {
        get
        {
            double a = Angle * Math.PI / 180;
            return (new Vector2d(Math.Cos(a), Math.Sin(a)), new Vector2d(-Math.Sin(a), Math.Cos(a)));
        }
    }

    /// <summary>Точка прямоугольника по долям (−1…1 по ширине и высоте; (−1,−1) — левый нижний угол).</summary>
    public Vector2d At(double fu, double fv)
    {
        var (u, v) = Axes;
        return Center + u * (fu * Width / 2) + v * (fv * Height / 2);
    }

    /// <summary>Доли точки p в осях прямоугольника (−1…1 внутри).</summary>
    public Vector2d Local(Vector2d p)
    {
        var (u, v) = Axes; var d = p - Center;
        return new Vector2d(Width > 0 ? 2 * Vector2d.Dot(d, u) / Width : 0, Height > 0 ? 2 * Vector2d.Dot(d, v) / Height : 0);
    }

    /// <summary>Новое изображение: левый нижний угол — в начале эскиза, 1 px = 25.4/96 мм.</summary>
    public static SkImage Create(string fileName, byte[] data, int pxW, int pxH)
    {
        double s = 25.4 / 96;
        var im = new SkImage { FileName = fileName, Data = data, PixelWidth = pxW, PixelHeight = pxH, Width = Math.Max(1e-3, pxW * s), Height = Math.Max(1e-3, pxH * s) };
        im.Center = new Vector2d(im.Width / 2, im.Height / 2);
        return im;
    }
}
