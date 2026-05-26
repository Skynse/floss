using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Floss.App.Canvas;
using Floss.App.Clip;
using Floss.App.Document;
using Floss.App.FlossFiles;
using Floss.App.ImageFiles;
using Floss.App.Kra;
using Floss.App.Psd;

namespace Floss.App;

using static Floss.App.Config.AppColors;

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
        Patterns = ["*.floss", "*.psd", "*.kra", "*.clip", "*.png", "*.jpg", "*.jpeg", "*.jpe", "*.webp", "*.bmp", "*.dib", "*.gif", "*.tif", "*.tiff", "*.ico", "*.wbmp"]
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

    private static readonly FilePickerFileType FlossSubToolFileType = new("Floss Sub Tool")
    {
        Patterns = ["*.flbr"]
    };

    private static readonly FilePickerFileType FlossSubToolGroupFileType = new("Floss Sub Tool Group")
    {
        Patterns = ["*.flbrg"]
    };

    private static readonly FilePickerFileType Mp4VideoFileType = new("MP4 Video")
    {
        Patterns = ["*.mp4"]
    };

    private async System.Threading.Tasks.Task NewDocumentAsync()
    {
        // Reuse the current tab only if it has never had a document loaded into it.
        bool reuseCurrentTab = _activeTab != null && !_activeTab.HasDocument;
        DocumentTab? createdTab = null;

        if (!reuseCurrentTab)
        {
            // Dirty check before opening a new tab
            if (_canvas.Document.IsDirty)
            {
                var wantsToSave = await new UnsavedChangesDialog().ShowDialog<bool?>(this);
                if (wantsToSave == null) return;
                if (wantsToSave == true)
                {
                    await SaveDocumentAsync();
                    if (_canvas.Document.IsDirty) return;
                }
            }

            createdTab = await NewTabAsync();
        }

        // 2. Show the New Document Wizard
        var result = await new NewDocumentDialog().ShowDialog<DocumentSettings?>(this);
        if (result == null)
        {
            // User canceled — close the empty tab we just created
            if (createdTab != null)
                _ = CloseTabAsync(createdTab);
            return;
        }

        // 3. Create the new document
        var newDoc = new DrawingDocument(result.Width, result.Height);

        // Set the paper (background) color and create physical paper layer if solid.
        var bgColor = result.BackgroundColor.ToAvalonia();
        newDoc.PaperColor = bgColor;
        if (bgColor.A > 0)
        {
            var paperLayer = new DrawingLayer("Paper", result.Width, result.Height);
            paperLayer.IsLocked = true;
            paperLayer.IsPaper = true;
            paperLayer.FillSolid(paperLayer.Pixels.Bounds, bgColor);
            newDoc.AppendLayerForImport(paperLayer);
            newDoc.PaperLayer = paperLayer;

            newDoc.AddLayer();

        }



        // 4. Swap it in and reset session state
        _canvas.Document.ReplaceWith(newDoc);
        _canvas.ResetDisplayAfterDocumentLoad();
        _currentFilePath = null;
        if (_activeTab != null)
        {
            _activeTab.FilePath = null;
            _activeTab.HasDocument = true;
            _activeTab.DocumentName = result.FileName;
            _activeTab.Timelapse = null;
        }
        _canvas.Document.MarkAsSaved(); // Force IsDirty to false for the fresh canvas
        if (result.RecordTimelapse)
            StartTimelapseForActiveDocument(result.FileName);

        // 5. Sync the UI
        _canvasFrame.IsVisible = true;
        SetDocumentPanelsVisible(true);
        SyncCanvasFrameToDocument(fitToViewport: true);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _canvas.EnsureDisplayCompositeSync(),
            Avalonia.Threading.DispatcherPriority.Loaded);
        SyncBrushSizeLimits();
        BuildLayerList();
        UpdateStatus();
        UpdateTitle();
        UpdateTabBar();
        UpdateTimelapseMenuState();

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

        var path = files[0].Path.LocalPath;
        try
        {
            using var busy = BeginBusy($"Opening {Path.GetFileName(path)}…");
            await using var stream = await files[0].OpenReadAsync();
            busy.Report($"Reading {Path.GetFileName(path)}…");
            var imported = await System.Threading.Tasks.Task.Run(() => LoadDocumentFromStream(stream, path));
            busy.Report("Applying document…");
            await NewTabAsync();
            ApplyOpenedDocument(imported.Document, path, imported.TimelapseSessionId);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OpenDocumentAsync");
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

            using var busy = BeginBusy($"Opening {Path.GetFileName(path)}…");
            await using var stream = File.OpenRead(path);
            busy.Report($"Reading {Path.GetFileName(path)}…");
            var imported = await System.Threading.Tasks.Task.Run(() => LoadDocumentFromStream(stream, path));
            busy.Report("Applying document…");
            await NewTabAsync();
            ApplyOpenedDocument(imported.Document, path, imported.TimelapseSessionId);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OpenDocumentFromPathAsync");
            _footerStatusText.Text = $"Open error: {ex.Message}";
        }
    }

    private static (DrawingDocument Document, string? TimelapseSessionId) LoadDocumentFromStream(Stream stream, string path)
    {
        if (IsFlossPath(path))
        {
            var loaded = FlossFileFormat.LoadDocument(stream);
            return (loaded.Document, loaded.TimelapseSessionId);
        }

        var document = IsPsdPath(path) ? PsdImporter.Load(stream)
            : IsKraPath(path) ? KraImporter.Load(stream)
            : IsClipPath(path) ? ClipImporter.Load(stream)
            : ImageFileImporter.Load(stream, path);
        return (document, null);
    }

    private void ApplyOpenedDocument(DrawingDocument imported, string path, string? timelapseSessionId = null)
    {
        _canvas.Document.ReplaceWith(imported);
        _canvas.ResetDisplayAfterDocumentLoad();
        _canvasFrame.IsVisible = true;
        SetDocumentPanelsVisible(true);
        App.Config.AddRecentFile(path);
        _currentFilePath = CanSaveInPlace(path) ? path : null;
        if (_activeTab != null)
        {
            _activeTab.FilePath = _currentFilePath;
            _activeTab.HasDocument = true;
            _activeTab.DocumentName = Path.GetFileNameWithoutExtension(path);
        }
        RestoreTimelapseForActiveDocument(_currentFilePath, timelapseSessionId);
        SyncCanvasFrameToDocument(fitToViewport: true);
        // Layout + viewport must be valid before compositing visible tiles.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _canvas.EnsureDisplayCompositeSync(),
            Avalonia.Threading.DispatcherPriority.Loaded);
        SyncBrushSizeLimits();
        BuildLayerList();
        UpdateStatus();
        UpdateTitle();
        UpdateTabBar();
        UpdateTimelapseMenuState();
        _footerStatusText.Text =
            $"Opened {_canvas.Document.Width}x{_canvas.Document.Height}  {Path.GetFileName(path)}";
    }

    public async System.Threading.Tasks.Task SaveDocumentAsync()
    {
        try
        {
            if (_activeTab == null) return;
            var path = _activeTab.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                await SaveDocumentAsAsync();
                return;
            }
            if (IsFlossPath(path))
                await WriteFlossInternalAsync(path);
            else if (IsPsdPath(path))
                await WritePsdInternalAsync(path);
            else
                await SaveDocumentAsAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.SaveDocumentAsync");
            _footerStatusText.Text = $"Save error: {ex.Message}";
        }
    }

    public async System.Threading.Tasks.Task SaveDocumentAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Document As",
            FileTypeChoices = [FlossFileType, PsdFileType],
            SuggestedFileName = SuggestedDocumentFileName()
        });
        if (file == null) return;
        var path = file.Path.LocalPath;

        try
        {
            if (IsPsdPath(path))
                await WritePsdInternalAsync(path);
            else
                await WriteFlossInternalAsync(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.SaveDocumentAsAsync");
            _footerStatusText.Text = $"Save error: {ex.Message}";
        }
    }

    public async System.Threading.Tasks.Task ExportCurrentDocumentAsync()
        => await ExportImageAsync();

    // ── Actual Disk Writers ───�────────────────────────────────────────────────

    private async System.Threading.Tasks.Task WriteFlossInternalAsync(string path)
    {
        try
        {
            var doc = _canvas.Document;
            using var busy = BeginBusy($"Saving {Path.GetFileName(path)}…", blockInput: false);
            await System.Threading.Tasks.Task.Run(() =>
            {
                using var read = doc.RenderLock.Read();
                using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
                var timelapseSessionId = _activeTab?.Timelapse?.SessionId;
                FlossFileFormat.Save(stream, doc, timelapseSessionId);
            });

            _currentFilePath = path;
            if (_activeTab != null)
            {
                _activeTab.FilePath = path;
                _activeTab.DocumentName = Path.GetFileNameWithoutExtension(path);
                _activeTab.Timelapse?.BindDocumentPath(path);
            }
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
        => await WritePsdInternalAsync(path, updateDocumentState: true);

    private async System.Threading.Tasks.Task WritePsdInternalAsync(string path, bool updateDocumentState)
    {
        try
        {
            var doc = _canvas.Document;
            using var busy = BeginBusy(updateDocumentState
                ? $"Saving {Path.GetFileName(path)}…"
                : $"Exporting PSD {Path.GetFileName(path)}…", blockInput: false);
            await System.Threading.Tasks.Task.Run(() =>
            {
                using var read = doc.RenderLock.Read();
                using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
                PsdExporter.Export(stream, doc);
            });

            if (!updateDocumentState)
            {
                _footerStatusText.Text = $"Exported PSD {Path.GetFileName(path)}";
                return;
            }

            _currentFilePath = path;
            if (_activeTab != null)
            {
                _activeTab.FilePath = path;
                _activeTab.DocumentName = Path.GetFileNameWithoutExtension(path);
            }
            App.Config.AddRecentFile(path);
            _canvas.Document.MarkAsSaved(); // Clears IsDirty
            _footerStatusText.Text = $"Saved PSD {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"PSD Save error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task ExportPsdAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export PSD",
            FileTypeChoices = [PsdFileType],
            SuggestedFileName = SuggestedExportFileName(".psd")
        });
        if (file == null) return;

        await WritePsdInternalAsync(file.Path.LocalPath, updateDocumentState: false);
    }

    private async System.Threading.Tasks.Task ExportImageAsync(string? preferredExtension = null)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Image",
            FileTypeChoices = ExportImageFileTypes(preferredExtension),
            SuggestedFileName = SuggestedExportFileName(preferredExtension ?? ".png")
        });
        if (file == null) return;

        var path = file.Path.LocalPath;
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var doc = _canvas.Document;

        // Show format-specific export wizard
        var settings = await new ExportWizardDialog(ext, doc.Width, doc.Height).ShowDialog<ExportSettings?>(this);
        if (settings == null) return;

        try
        {
            using var busy = BeginBusy($"Exporting {Path.GetFileName(path)}…");
            await using var stream = await file.OpenWriteAsync();
            await System.Threading.Tasks.Task.Run(() =>
            {
                using var read = doc.RenderLock.Read();
                ImageFileExporter.Export(stream, doc, path, settings);
            });
            _footerStatusText.Text = $"Exported {Path.GetFileName(path)}  ({settings.TargetWidth}×{settings.TargetHeight})";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.ExportImageAsync");
            _footerStatusText.Text = $"Export error: {ex.Message}";
        }
    }

    private static bool IsPsdPath(string path)
        => string.Equals(Path.GetExtension(path), ".psd", StringComparison.OrdinalIgnoreCase);

    private static bool IsKraPath(string path)
        => string.Equals(Path.GetExtension(path), ".kra", StringComparison.OrdinalIgnoreCase);

    private static bool IsClipPath(string path)
        => string.Equals(Path.GetExtension(path), ".clip", StringComparison.OrdinalIgnoreCase);

    private static bool IsFlossPath(string path)
        => string.Equals(Path.GetExtension(path), FlossFileFormat.Extension, StringComparison.OrdinalIgnoreCase);

    private static FilePickerFileType[] ExportImageFileTypes(string? preferredExtension)
    {
        var all = new[]
        {
            new FilePickerFileType("PNG Image") { Patterns = ["*.png"] },
            new FilePickerFileType("JPEG Image") { Patterns = ["*.jpg", "*.jpeg"] },
            new FilePickerFileType("TIFF Image") { Patterns = ["*.tif", "*.tiff"] },
            new FilePickerFileType("Bitmap Image") { Patterns = ["*.bmp"] },
            new FilePickerFileType("WebP Image") { Patterns = ["*.webp"] },
            new FilePickerFileType("GIF Image") { Patterns = ["*.gif"] },
            new FilePickerFileType("Icon") { Patterns = ["*.ico"] },
            new FilePickerFileType("Wireless Bitmap") { Patterns = ["*.wbmp"] }
        };

        if (string.IsNullOrWhiteSpace(preferredExtension))
            return all;

        var pattern = "*" + preferredExtension;
        var preferred = all.FirstOrDefault(t =>
            t.Patterns?.Any(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)) == true);
        return preferred == null ? all : [preferred, .. all.Where(t => !ReferenceEquals(t, preferred))];
    }

    private static bool CanSaveInPlace(string path)
        => IsFlossPath(path) || IsPsdPath(path);

    private string SuggestedDocumentFileName()
    {
        if (!string.IsNullOrWhiteSpace(_currentFilePath) && CanSaveInPlace(_currentFilePath))
            return Path.GetFileName(_currentFilePath);

        return "untitled.floss";
    }

    private string SuggestedExportFileName(string extension)
    {
        var baseName = string.IsNullOrWhiteSpace(_currentFilePath)
            ? "untitled"
            : Path.GetFileNameWithoutExtension(_currentFilePath);
        return baseName + extension;
    }

    private static string SafePresetFileName(string name, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name
            .Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c)
            .ToArray())
            .Trim(' ', '.', '-');

        if (string.IsNullOrWhiteSpace(safe))
            safe = "sub-tool";

        return safe + extension;
    }
}
