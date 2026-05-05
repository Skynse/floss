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

using Avalonia.Media;
using SkiaSharp;


public static class ColorExtensions
{
    // Convert Avalonia Color to Skia Color
    public static SKColor ToSkia(this Color color)
    {
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    // Convert Skia Color to Avalonia Color
    public static Color ToAvalonia(this SKColor color)
    {
        return new Color(color.Alpha, color.Red, color.Green, color.Blue);
    }
}


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

    private async System.Threading.Tasks.Task NewDocumentAsync()
    {
        // 1. Check for unsaved changes FIRST
        if (_canvas.Document.IsDirty)
        {
            // NOTE: A proper save prompt should return a bool? (Yes = true, No = false, Cancel = null).
            // If the user clicks "Cancel" or closes the window, you must abort the New Document process.
            var wantsToSave = await new UnsavedChangesDialog().ShowDialog<bool?>(this);

            if (wantsToSave == null)
            {
                return; // User canceled the operation entirely
            }

            if (wantsToSave == true)
            {
                await SaveDocumentAsync();

                // Safety check: If they clicked "Save" but then canceled out of the
                // "Save As" file picker, the document is STILL dirty. Abort.
                if (_canvas.Document.IsDirty) return;
            }
            // If wantsToSave == false, they clicked "Don't Save". We just continue and discard.
        }

        // 2. Show the New Document Wizard
        var result = await new NewDocumentDialog().ShowDialog<DocumentSettings?>(this);
        if (result == null) return; // User canceled the wizard

        // 3. Create the new document
        var newDoc = new DrawingDocument(result.Width, result.Height);

        // Add a locked paper layer with the chosen background color (if not transparent)
        if (result.BackgroundColor.Alpha > 0)
        {
            var bgColor = result.BackgroundColor.ToAvalonia();
            var paperLayer = new DrawingLayer("Paper", result.Width, result.Height);
            paperLayer.IsLocked = true;

            var pixelCount = result.Width * result.Height;
            var pixels = new byte[pixelCount * 4];
            for (var i = 0; i < pixelCount; i++)
            {
                var off = i * 4;
                pixels[off] = bgColor.B;
                pixels[off + 1] = bgColor.G;
                pixels[off + 2] = bgColor.R;
                pixels[off + 3] = 255;
            }
            paperLayer.RestorePixels(pixels);
            newDoc.InsertLayerNear(paperLayer, newDoc.Layers[0], LayerDropPlacement.Below);
        }

        // The transparent drawing layer should be active, not the locked paper.
        newDoc.SelectLayer(newDoc.Layers.Count > 1 ? 1 : 0);

        // 4. Swap it in and reset session state
        _canvas.Document.ReplaceWith(newDoc);
        _currentFilePath = null;
        _canvas.Document.MarkAsSaved(); // Force IsDirty to false for the fresh canvas

        // 5. Sync the UI
        _canvasFrame.IsVisible = true;
        SyncCanvasFrameToDocument(fitToViewport: true);
        BuildLayerList();
        UpdateStatus(); // This will trigger your title bar update

        _footerStatusText.Text = $"Created '{result.FileName}'";
    }

    private void AddBackgroundLayer()
    {
        _canvas.AddBackgroundLayer();
        BuildLayerList();
    }
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
            _currentFilePath = path;
            ApplyOpenedDocument(imported, path);
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Error: {ex.Message}";
        }
    }


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
        _canvasFrame.IsVisible = true;
        App.Config.AddRecentFile(path);
        if (IsFlossPath(path)) _currentFilePath = path;
        SyncCanvasFrameToDocument(fitToViewport: true);
        BuildLayerList();
        UpdateStatus();
        _footerStatusText.Text =
            $"Opened {_canvas.Document.Width}x{_canvas.Document.Height}  {Path.GetFileName(path)}";
    }

    private async System.Threading.Tasks.Task SaveDocumentAsync()
    {
        // 1. If we've never saved, force a Save As
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            Console.WriteLine("No current file path, forcing Save As");
            await SaveDocumentAsAsync();
            return;
        }

        // 2. If it's a native project or a PSD, silently overwrite it
        if (IsFlossPath(_currentFilePath))
        {
            await WriteFlossInternalAsync(_currentFilePath);
        }
        else if (IsPsdPath(_currentFilePath))
        {
            await WritePsdInternalAsync(_currentFilePath);
        }
        // 3. If they opened a PNG or KRA, DON'T overwrite. Force Save As.
        else
        {
            await SaveDocumentAsAsync();
        }
    }

    // Wire your "Save As" (Ctrl+Shift+S) to this!
    private async System.Threading.Tasks.Task SaveDocumentAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Document As",
            FileTypeChoices = [FlossFileType, PsdFileType], // Support both natively
            SuggestedFileName = string.IsNullOrEmpty(_currentFilePath) ? "untitled.floss" : Path.GetFileNameWithoutExtension(_currentFilePath)
        });

        if (file == null) return;
        var path = file.Path.LocalPath;

        if (IsPsdPath(path))
        {
            await WritePsdInternalAsync(path);
        }
        else
        {
            await WriteFlossInternalAsync(path); // Default to Floss format
        }
    }

    // ── Actual Disk Writers ───────────────────────────────────────────────────

    private async System.Threading.Tasks.Task WriteFlossInternalAsync(string path)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
            FlossFileFormat.Save(stream, _canvas.Document);

            _currentFilePath = path; // Update universal state
            App.Config.AddRecentFile(path);
            _canvas.Document.MarkAsSaved(); // Clears IsDirty
            _footerStatusText.Text = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Save error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task WritePsdInternalAsync(string path)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
            PsdExporter.Export(stream, _canvas.Document);

            _currentFilePath = path; // Update universal state
            App.Config.AddRecentFile(path);
            _canvas.Document.MarkAsSaved(); // Clears IsDirty
            _footerStatusText.Text = $"Saved PSD {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"PSD Save error: {ex.Message}";
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
