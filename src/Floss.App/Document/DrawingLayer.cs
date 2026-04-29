using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Floss.App.Document;

public sealed class DrawingLayer
{
    public DrawingLayer(string name, int width, int height)
    {
        Name = name;
        Bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        Clear();
    }

    public string Name { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public double Opacity { get; set; } = 1.0;
    public string BlendMode { get; set; } = "Normal";
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool IsGroup { get; set; }
    public bool IsOpen { get; set; } = true;
    public bool IsClipping { get; set; }
    public int IndentLevel { get; set; }
    public DrawingLayer? Parent { get; set; }
    public List<DrawingLayer> Children { get; } = [];
    public WriteableBitmap Bitmap { get; }

    public void Clear()
    {
        using var frame = Bitmap.Lock();
        unsafe
        {
            var pixels = (byte*)frame.Address;
            for (var y = 0; y < frame.Size.Height; y++)
            {
                var row = pixels + y * frame.RowBytes;
                for (var x = 0; x < frame.Size.Width * 4; x++)
                {
                    row[x] = 0;
                }
            }
        }
    }

    public byte[] CapturePixels()
    {
        var width = Bitmap.PixelSize.Width;
        var height = Bitmap.PixelSize.Height;
        var bytes = new byte[width * height * 4];
        using var frame = Bitmap.Lock();
        unsafe
        {
            var src = (byte*)frame.Address;
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    (IntPtr)(src + y * frame.RowBytes),
                    bytes,
                    y * width * 4,
                    width * 4);
            }
        }

        return bytes;
    }

    public void RestorePixels(byte[] bytes)
    {
        var width = Bitmap.PixelSize.Width;
        var height = Bitmap.PixelSize.Height;
        using var frame = Bitmap.Lock();
        unsafe
        {
            var dst = (byte*)frame.Address;
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    bytes,
                    y * width * 4,
                    (IntPtr)(dst + y * frame.RowBytes),
                    width * 4);
            }
        }
    }
}