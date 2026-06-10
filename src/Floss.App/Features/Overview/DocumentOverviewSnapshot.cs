using System;
using Avalonia.Media.Imaging;

namespace Floss.App.Features.Overview;

/// <summary>
/// Downscaled full-document image for navigator-style dockers.
/// </summary>
public sealed class DocumentOverviewSnapshot : IDisposable
{
    public DocumentOverviewSnapshot(WriteableBitmap bitmap, int documentWidth, int documentHeight)
    {
        Bitmap = bitmap;
        DocumentWidth = documentWidth;
        DocumentHeight = documentHeight;
    }

    public WriteableBitmap Bitmap { get; }

    public int DocumentWidth { get; }

    public int DocumentHeight { get; }

    public int PixelWidth => Bitmap.PixelSize.Width;

    public int PixelHeight => Bitmap.PixelSize.Height;

    public void Dispose() => Bitmap.Dispose();
}
