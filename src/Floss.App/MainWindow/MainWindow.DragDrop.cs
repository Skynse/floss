using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;

namespace Floss.App;

public partial class MainWindow
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".floss", ".psd", ".kra"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".webp", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico", ".wbmp"
    };

    private void WireDragDrop()
    {
        DragDrop.SetAllowDrop(_workspaceViewport, true);
        _workspaceViewport.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _workspaceViewport.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasSupportedFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e);
        if (paths == null || paths.Length == 0) return;

        foreach (var path in paths)
        {
            var cleaned = CleanPath(path);
            var ext = Path.GetExtension(cleaned);

            if (DocumentExtensions.Contains(ext))
            {
                try
                {
                    if (File.Exists(cleaned))
                        await OpenDocumentFromPathAsync(cleaned);
                }
                catch (Exception ex) { CrashLog.Write(ex, "MainWindow.Drop.Document"); }
                continue;
            }

            if (ImageExtensions.Contains(ext))
            {
                try
                {
                    using var skBitmap = SKBitmap.Decode(cleaned);
                    if (skBitmap != null)
                        _canvas.PasteSKBitmap(skBitmap, Path.GetFileNameWithoutExtension(cleaned));
                }
                catch (Exception ex) { CrashLog.Write(ex, "MainWindow.Drop.Image"); }
            }
        }
    }

    private static bool HasSupportedFile(DragEventArgs e) => GetDroppedPaths(e) is { Length: > 0 };

    private static string[]? GetDroppedPaths(DragEventArgs e)
    {
        var fn = DataFormat.CreateInProcessFormat<string[]>("FileNames");
        if (e.DataTransfer.Contains(fn))
            return e.DataTransfer.TryGetValue<string[]>(fn);
        return null;
    }

    private static string CleanPath(string p)
        => p.StartsWith("file://") ? Uri.UnescapeDataString(p["file://".Length..]).TrimEnd('\r', '\n') : p;
}
