using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace Floss.App;

public partial class MainWindow
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".floss", ".psd", ".kra", ".clip"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".webp", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico", ".wbmp"
    };

    private Border? _fileDropShade;

    private void WireFileDragDrop()
    {
        void Attach(Control target)
        {
            DragDrop.SetAllowDrop(target, true);
            target.AddHandler(DragDrop.DragOverEvent, OnFileDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            target.AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            target.AddHandler(DragDrop.DropEvent, OnFileDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }

        if (Content is Control root)
            Attach(root);
        if (_workspaceViewport != null)
            Attach(_workspaceViewport);
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (!TryCollectDropPaths(e, out var paths) || paths.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        ShowFileDropShade(true);
        _footerStatusText.Text = paths.Count == 1
            ? $"Drop to open: {Path.GetFileName(paths[0])}"
            : $"Drop to open {paths.Count} files";
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        if (e.Source is not Control) return;
        ShowFileDropShade(false);
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        ShowFileDropShade(false);
        if (!TryCollectDropPaths(e, out var paths) || paths.Count == 0)
            return;

        e.Handled = true;

        var documents = new List<string>();
        var images = new List<string>();

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var ext = Path.GetExtension(path);
            if (DocumentExtensions.Contains(ext))
                documents.Add(path);
            else if (ImageExtensions.Contains(ext))
                images.Add(path);
        }

        foreach (var doc in documents)
        {
            try
            {
                await OpenDocumentFromPathAsync(doc);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MainWindow.FileDrop.Document");
                _footerStatusText.Text = $"Could not open {Path.GetFileName(doc)}: {ex.Message}";
            }
        }

        foreach (var img in images)
        {
            try
            {
                await ImportDroppedImageAsync(img);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MainWindow.FileDrop.Image");
                _footerStatusText.Text = $"Could not import {Path.GetFileName(img)}: {ex.Message}";
            }
        }

        if (documents.Count == 0 && images.Count == 0)
            _footerStatusText.Text = "No supported files in drop";
    }

    private async System.Threading.Tasks.Task ImportDroppedImageAsync(string path)
    {
        if (_activeTab?.HasDocument == true)
        {
            using var skBitmap = SKBitmap.Decode(path);
            if (skBitmap == null)
                throw new InvalidOperationException("Unsupported or corrupt image.");
            _canvas.PasteSKBitmap(skBitmap, Path.GetFileNameWithoutExtension(path));
            _footerStatusText.Text = $"Pasted {Path.GetFileName(path)}";
            return;
        }

        await OpenDocumentFromPathAsync(path);
    }

    private void ShowFileDropShade(bool visible)
    {
        if (_rootGrid == null) return;

        if (!visible)
        {
            if (_fileDropShade != null)
                _fileDropShade.IsVisible = false;
            return;
        }

        _fileDropShade ??= new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(28, 0, 120, 212)),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Config.AppColors.Accent)),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            ZIndex = 4000
        };

        if (_fileDropShade.Parent == null)
        {
            Grid.SetRow(_fileDropShade, 0);
            Grid.SetColumnSpan(_fileDropShade, 6);
            _fileDropShade.ZIndex = 4000;
            _rootGrid.Children.Add(_fileDropShade);
        }

        _fileDropShade.IsVisible = true;
    }

    private static bool TryCollectDropPaths(DragEventArgs e, out List<string> paths)
    {
        paths = [];

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                foreach (var item in files)
                {
                    if (item is IStorageFile file)
                        paths.Add(file.Path.LocalPath);
                }
            }
        }

        if (paths.Count == 0)
            paths.AddRange(TryGetLegacyFilePaths(e));

        paths = paths
            .Select(NormalizeDropPath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return paths.Count > 0;
    }

    private static IEnumerable<string> TryGetLegacyFilePaths(DragEventArgs e)
    {
        var fn = DataFormat.CreateInProcessFormat<string[]>("FileNames");
        if (e.DataTransfer.Contains(fn))
        {
            var arr = e.DataTransfer.TryGetValue<string[]>(fn);
            if (arr != null)
                return arr;
        }

        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            var text = e.DataTransfer.TryGetText();
            if (string.IsNullOrWhiteSpace(text)) return [];
            return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeDropPath)
                .Where(p => !string.IsNullOrWhiteSpace(p));
        }

        return [];
    }

    private static string NormalizeDropPath(string raw)
    {
        var p = raw.Trim();
        if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(p, UriKind.Absolute, out var uri))
                return uri.LocalPath;
            return Uri.UnescapeDataString(p["file://".Length..]).TrimEnd('\r', '\n');
        }

        return p.Trim('"');
    }
}
