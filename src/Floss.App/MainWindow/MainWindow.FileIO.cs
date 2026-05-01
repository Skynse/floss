using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.FlossFiles;
using Floss.App.ImageFiles;
using Floss.App.Kra;
using Floss.App.Psd;

namespace Floss.App;

public partial class MainWindow
{
    // ── File I/O ──────────────────────────────────────────────────────────────
    private static readonly FilePickerFileType DocumentFileType = new("Supported Documents")
    {
        Patterns = ["*.floss", "*.psd", "*.kra", "*.png", "*.jpg", "*.jpeg", "*.jpe", "*.webp", "*.bmp", "*.dib", "*.gif", "*.tif", "*.tiff", "*.ico", "*.wbmp"]
    };

    private static readonly FilePickerFileType KraFileType = new("Krita Document")
    {
        Patterns = ["*.kra"]
    };

    private static readonly FilePickerFileType FlossFileType = new("Floss Document")
    {
        Patterns = ["*.floss"]
    };

    private static readonly FilePickerFileType PsdFileType = new("Photoshop Document")
    {
        Patterns = ["*.psd"]
    };

    private static readonly FilePickerFileType RasterImageFileType = new("Image Files")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.jpe", "*.webp", "*.bmp", "*.dib", "*.gif", "*.tif", "*.tiff", "*.ico", "*.wbmp"]
    };

    private async System.Threading.Tasks.Task OpenDocumentAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = false,
            FileTypeFilter = [DocumentFileType, FlossFileType, PsdFileType, KraFileType, RasterImageFileType]
        });
        if (files.Count == 0) return;

        try
        {
            var path = files[0].Path.LocalPath;
            await using var stream = await files[0].OpenReadAsync();
            var imported = await System.Threading.Tasks.Task.Run(() => LoadDocumentFromStream(stream, path));
            ApplyOpenedDocument(imported, path);
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task OpenPsdAsync() => await OpenDocumentAsync();

    public async System.Threading.Tasks.Task OpenDocumentFromPathAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _footerStatusText.Text = $"Open error: file not found {path}";
                return;
            }

            await using var stream = File.OpenRead(path);
            var imported = await System.Threading.Tasks.Task.Run(() => LoadDocumentFromStream(stream, path));
            ApplyOpenedDocument(imported, path);
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Open error: {ex.Message}";
        }
    }

    private static DrawingDocument LoadDocumentFromStream(Stream stream, string path)
        => IsFlossPath(path) ? FlossFileFormat.Load(stream)
            : IsPsdPath(path) ? PsdImporter.Load(stream)
            : IsKraPath(path) ? KraImporter.Load(stream)
            : ImageFileImporter.Load(stream, path);

    private void ApplyOpenedDocument(DrawingDocument imported, string path)
    {
        _canvas.Document.ReplaceWith(imported);
        App.Config.AddRecentFile(path);
        if (IsFlossPath(path)) _currentFlossPath = path;
        SyncCanvasFrameToDocument(fitToViewport: true);
        BuildLayerList();
        UpdateStatus();
        _footerStatusText.Text =
            $"Opened {_canvas.Document.Width}x{_canvas.Document.Height}  {Path.GetFileName(path)}";
    }

    private async System.Threading.Tasks.Task SaveFlossAsync()
    {
        if (_currentFlossPath != null)
        {
            try
            {
                await using var stream = File.Open(_currentFlossPath, FileMode.Create, FileAccess.Write);
                FlossFileFormat.Save(stream, _canvas.Document);
                _footerStatusText.Text = $"Saved {Path.GetFileName(_currentFlossPath)}";
            }
            catch (Exception ex)
            {
                _footerStatusText.Text = $"Save error: {ex.Message}";
            }
            return;
        }

        await SaveFlossAsAsync();
    }

    private async System.Threading.Tasks.Task SaveFlossAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Floss Document",
            FileTypeChoices = [FlossFileType],
            SuggestedFileName = "untitled.floss"
        });
        if (file == null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            FlossFileFormat.Save(stream, _canvas.Document);
            _currentFlossPath = file.Path.LocalPath;
            App.Config.AddRecentFile(file.Path.LocalPath);
            _footerStatusText.Text = $"Saved {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Save error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task SavePsdAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PSD",
            FileTypeChoices = [PsdFileType]
        });
        if (file == null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            PsdExporter.Export(stream, _canvas.Document);
            App.Config.AddRecentFile(file.Path.LocalPath);
            _footerStatusText.Text = $"Saved {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Save error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task ExportImageAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Image",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG Image") { Patterns = ["*.png"] },
                new FilePickerFileType("JPEG Image") { Patterns = ["*.jpg", "*.jpeg"] },
                new FilePickerFileType("WebP Image") { Patterns = ["*.webp"] },
                new FilePickerFileType("Bitmap Image") { Patterns = ["*.bmp"] },
                new FilePickerFileType("GIF Image") { Patterns = ["*.gif"] },
                new FilePickerFileType("Icon") { Patterns = ["*.ico"] },
                new FilePickerFileType("Wireless Bitmap") { Patterns = ["*.wbmp"] }
            ],
            SuggestedFileName = "floss-export.png"
        });
        if (file == null) return;

        try
        {
            var path = file.Path.LocalPath;
            await using var stream = await file.OpenWriteAsync();
            ImageFileExporter.Export(stream, _canvas.Document, path);
            _footerStatusText.Text = $"Exported {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Export error: {ex.Message}";
        }
    }

    private static bool IsPsdPath(string path)
        => string.Equals(Path.GetExtension(path), ".psd", StringComparison.OrdinalIgnoreCase);

    private static bool IsKraPath(string path)
        => string.Equals(Path.GetExtension(path), ".kra", StringComparison.OrdinalIgnoreCase);

    private static bool IsFlossPath(string path)
        => string.Equals(Path.GetExtension(path), FlossFileFormat.Extension, StringComparison.OrdinalIgnoreCase);
}
