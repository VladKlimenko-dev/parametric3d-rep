using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Parametric3D.App.Diagnostics;

/// <summary>Снимки для проверок: пиксели вида (RGBA) в PNG.</summary>
public static class Shots
{
    public static void SavePng(string path, byte[] rgba, int w, int h, bool flip)
    {
        using var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
        {
            for (int y = 0; y < h; y++)
            {
                int src = (flip ? h - 1 - y : y) * w * 4;
                System.Runtime.InteropServices.Marshal.Copy(rgba, src, fb.Address + y * fb.RowBytes, w * 4);
            }
        }
        bmp.Save(path);
    }
}
