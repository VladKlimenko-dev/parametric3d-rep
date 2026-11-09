using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenTK.Mathematics;
using Parametric3D.Model.Sketching;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Viewport;

/// <summary>
/// k185 (замечание 09.11, 12:02, п. 3): ИЗОБРАЖЕНИЯ ЭСКИЗА РИСУЮТСЯ В КАДРЕ ВИДА (OpenGL), а не поверх него: под линиями,
/// точками и заливкой эскиза; размеры и значки связей — слои поверх вида, тоже выше. Слой SketchImageLayer оставляет себе
/// рамку, ручки и мышь; картинку он рисует сам только без OpenGL (проверки без видеокарты). P3D_SKIMGGL=0 — как в k184.
/// </summary>
public sealed partial class ModelViewport
{
    public static readonly bool SketchImageGlOff = Environment.GetEnvironmentVariable("P3D_SKIMGGL") == "0";

    /// <summary>Изображения эскиза рисует кадр OpenGL (слой — только рамку и ручки).</summary>
    public bool GlSketchImages => _glReady && !SketchImageGlOff && !_r.ImageShaderFailed;

    /// <summary>Разбор: изображений эскиза в последнем кадре OpenGL.</summary>
    public int GlImagesDrawn => _r.ImagesDrawn;

    /// <summary>Пиксели изображения: RGBA8 premultiplied, строки сверху вниз (не больше 4096 по стороне).</summary>
    public sealed class ImagePixels { public byte[] Rgba = Array.Empty<byte>(); public int W, H; }
    private static readonly ConditionalWeakTable<byte[], ImagePixels?> _pixels = new();

    public static ImagePixels? PixelsOf(SkImage im)
    {
        if (im.Data.Length == 0) return null;
        return _pixels.GetValue(im.Data, _ => { try { return Decode(im); } catch { return null; } });
    }

    private static ImagePixels? Decode(SkImage im)
    {
        var src = SketchImageLayer.BitmapOf(im);
        if (src == null) return null;
        Bitmap bmp = src;
        int w = src.PixelSize.Width, h = src.PixelSize.Height;
        const int max = 4096;
        bool scaled = false;
        if (w > max || h > max)
        {
            double k = (double)max / Math.Max(w, h);
            w = Math.Max(1, (int)(w * k)); h = Math.Max(1, (int)(h * k));
            bmp = src.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.HighQuality);
            scaled = true;
        }
        try
        {
            var px = new byte[w * h * 4];
            var hnd = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(0, 0, w, h), hnd.AddrOfPinnedObject(), px.Length, w * 4); }
            finally { hnd.Free(); }
            bool bgra = bmp.Format is not { } f || !f.Equals(PixelFormats.Rgba8888);
            bool unpremul = bmp.AlphaFormat == AlphaFormat.Unpremul;
            bool opaque = bmp.AlphaFormat == AlphaFormat.Opaque;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (bgra) (px[i], px[i + 2]) = (px[i + 2], px[i]);
                if (opaque) px[i + 3] = 255;
                else if (unpremul && px[i + 3] < 255)
                {
                    int a = px[i + 3];
                    px[i] = (byte)(px[i] * a / 255); px[i + 1] = (byte)(px[i + 1] * a / 255); px[i + 2] = (byte)(px[i + 2] * a / 255);
                }
            }
            return new ImagePixels { Rgba = px, W = w, H = h };
        }
        finally { if (scaled) bmp.Dispose(); }
    }

    /// <summary>Изображения активного эскиза для кадра (null — нет или рисует слой).</summary>
    private List<SceneRenderer.OverlayImage>? SketchImagesForGl()
    {
        if (SketchImageGlOff || _r.ImageShaderFailed || ActiveSketch is not { } sk || sk.Images.Count == 0) return null;
        var res = new List<SceneRenderer.OverlayImage>();
        foreach (var im in sk.Images)
        {
            if (PixelsOf(im) is not { } px) continue;
            var pl = sk.Plane;
            Vector3d bl = pl.ToWorld(im.At(-1, -1)), br = pl.ToWorld(im.At(1, -1)), tl = pl.ToWorld(im.At(-1, 1));
            res.Add(new SceneRenderer.OverlayImage
            {
                Key = px, Rgba = px.Rgba, W = px.W, H = px.H,
                O = V3(bl), U = V3(br - bl), V = V3(tl - bl),
                Opacity = (float)im.Opacity, Invert = im.Invert,
            });
        }
        return res;
    }
}
