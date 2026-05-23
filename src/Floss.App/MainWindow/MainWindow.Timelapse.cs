using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Floss.App.Document;
using Floss.App.Timelapse;
using Floss.App.Windows;

namespace Floss.App;

public partial class MainWindow
{
    private bool _timelapseCaptureRunning;
    private bool _timelapseCapturePending;
    private bool _timelapseCaptureScheduled;

    private void StartTimelapseForActiveDocument(string documentName)
    {
        if (_activeTab == null || !_canvas.HasDocument) return;
        _activeTab.DocumentName = string.IsNullOrWhiteSpace(documentName) ? "Untitled" : documentName.Trim();
        _activeTab.Timelapse ??= TimelapseSession.StartNew(_activeTab.DocumentName, _canvas.Document);
        if (!string.IsNullOrEmpty(_activeTab.FilePath))
            _activeTab.Timelapse.BindDocumentPath(_activeTab.FilePath);
        _activeTab.Timelapse.SetRecording(true);
        UpdateTimelapseMenuState();
    }

    private void RestoreTimelapseForActiveDocument(string? filePath, string? timelapseSessionId = null)
    {
        if (_activeTab == null || !_canvas.HasDocument)
            return;

        var bindPath = CanSaveInPlace(filePath ?? "") ? filePath : null;
        var session = TimelapseSession.FindForDocument(
            bindPath,
            _activeTab.DocumentName,
            _canvas.Document.Width,
            _canvas.Document.Height,
            timelapseSessionId);

        if (session != null)
        {
            _activeTab.Timelapse = session;
            if (!string.IsNullOrEmpty(bindPath))
                session.BindDocumentPath(bindPath);

            if (App.Config.RecordTimelapse)
                session.SetRecording(true);
        }
        else if (App.Config.RecordTimelapse)
        {
            StartTimelapseForActiveDocument(_activeTab.DocumentName);
        }
    }

    private void StopTimelapseForActiveDocument()
    {
        if (_activeTab?.Timelapse == null) return;
        _activeTab.Timelapse.SetRecording(false);
        UpdateTimelapseMenuState();
    }

    private void ToggleTimelapseRecording()
    {
        if (_activeTab == null || !_canvas.HasDocument) return;

        var shouldRecord = !(_activeTab?.Timelapse?.IsRecording == true);
        App.Config.RecordTimelapse = shouldRecord;
        App.Config.Save();

        if (shouldRecord)
            StartTimelapseForActiveDocument(_activeTab!.DocumentName);
        else
            StopTimelapseForActiveDocument();
    }

    private void CaptureTimelapseFrameAfterHistory()
    {
        var session = _activeTab?.Timelapse;
        if (session?.IsRecording != true || !_canvas.HasDocument)
            return;

        if (_canvas.Document.LastHistoryChangeKind == DocumentHistoryChangeKind.Undo)
            return;

        if (!_canvas.Document.LastHistoryAffectsVisual)
            return;

        // CommitLayerTileMutation and CommitStroke both fire HistoryChanged in the same
        // synchronous commit — defer to Background so they coalesce into one capture.
        if (_timelapseCaptureScheduled)
            return;

        _timelapseCaptureScheduled = true;
        Dispatcher.UIThread.Post(CaptureTimelapseFrameDeferred, DispatcherPriority.Background);
    }

    private void CaptureTimelapseFrameDeferred()
    {
        _timelapseCaptureScheduled = false;

        var session = _activeTab?.Timelapse;
        if (session?.IsRecording != true || !_canvas.HasDocument)
            return;

        if (_canvas.Document.LastHistoryChangeKind == DocumentHistoryChangeKind.Undo)
            return;

        if (!_canvas.Document.LastHistoryAffectsVisual)
            return;

        if (_timelapseCaptureRunning)
        {
            _timelapseCapturePending = true;
            return;
        }

        try
        {
            session.PrepareCaptureFromDocument(_canvas.Document);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.Timelapse.Prepare");
            _footerStatusText.Text = $"Timelapse snapshot error: {ex.Message}";
            return;
        }

        RunTimelapseCaptureLoopAsync(session, _canvas.Document)
            .FireAndForget("MainWindow.Timelapse.CaptureLoop");
    }

    private async Task RunTimelapseCaptureLoopAsync(TimelapseSession session, DrawingDocument document)
    {
        _timelapseCaptureRunning = true;
        try
        {
            while (true)
            {
                _timelapseCapturePending = false;
                try
                {
                    var captured = await session.CapturePreparedFrameAsync();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (captured)
                            _footerStatusText.Text = $"Timelapse frame {session.FrameCount}";
                        UpdateTimelapseMenuState();
                    });
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "MainWindow.Timelapse.Capture");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _footerStatusText.Text = $"Timelapse capture error: {ex.Message}";
                        UpdateTimelapseMenuState();
                    });
                    break;
                }

                if (!_timelapseCapturePending || !session.IsRecording)
                    break;

                await Dispatcher.UIThread.InvokeAsync(() => session.PrepareCaptureFromDocument(document));
            }
        }
        finally
        {
            _timelapseCaptureRunning = false;
        }
    }

    private async Task ExportTimelapseAsync()
    {
        var session = _activeTab?.Timelapse;
        if (session == null || session.FrameCount == 0)
        {
            _footerStatusText.Text = "No timelapse frames recorded";
            return;
        }

        var settings = await new TimelapseExportDialog(session).ShowDialog<TimelapseExportSettings?>(this);
        if (settings == null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Timelapse Video",
            FileTypeChoices = [Mp4VideoFileType],
            SuggestedFileName = SuggestedTimelapseExportFileName()
        });
        if (file == null) return;

        try
        {
            var path = file.Path.LocalPath;
            using var busy = BeginBusy("Exporting timelapse video…");
            await session.ExportVideoAsync(path, settings);
            _footerStatusText.Text = $"Exported timelapse video {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.Timelapse.Export");
            _footerStatusText.Text = $"Timelapse export error: {ex.Message}";
        }
    }

    private void UpdateTimelapseMenuState()
    {
        var hasDocument = _activeTab?.HasDocument == true;
        var recording = _activeTab?.Timelapse?.IsRecording == true;
        if (_recordTimelapseMenuItem != null)
        {
            _recordTimelapseMenuItem.Header = recording ? "Stop Recording Timelapse" : "Record Timelapse";
            _recordTimelapseMenuItem.IsEnabled = hasDocument;
        }

        if (_exportTimelapseMenuItem != null)
            _exportTimelapseMenuItem.IsEnabled = hasDocument && (_activeTab?.Timelapse?.FrameCount ?? 0) > 0;
    }

    private string SuggestedTimelapseExportFileName()
    {
        var baseName = _activeTab?.DisplayTitle;
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "untitled";
        baseName = Path.GetFileNameWithoutExtension(baseName);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
            baseName = baseName.Replace(c, '-');
        return $"{baseName}-timelapse.mp4";
    }
}
