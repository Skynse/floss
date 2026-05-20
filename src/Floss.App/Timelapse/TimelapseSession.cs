using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.ImageFiles;
using SkiaSharp;

namespace Floss.App.Timelapse;

public enum TimelapseLength
{
    All,
    Seconds15,
    Seconds30,
    Seconds60
}

public enum TimelapseAspect
{
    Original,
    Landscape,
    Portrait
}

public sealed class TimelapseFrame
{
    public int Index { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string FileName { get; set; } = "";
}

public sealed class TimelapseManifest
{
    public string SessionId { get; set; } = "";
    public string DocumentName { get; set; } = "Untitled";
    public int DocumentWidth { get; set; }
    public int DocumentHeight { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public List<TimelapseFrame> Frames { get; set; } = [];
}

public sealed class TimelapseExportSettings
{
    public TimelapseLength Length { get; set; } = TimelapseLength.All;
    public TimelapseAspect Aspect { get; set; } = TimelapseAspect.Original;
    public int LongestSidePixels { get; set; } = 1280;
}

public sealed class TimelapseDocumentSnapshot : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public uint PaperColor { get; }
    public IReadOnlyList<DrawingLayer> Layers { get; }

    private TimelapseDocumentSnapshot(int width, int height, uint paperColor, IReadOnlyList<DrawingLayer> layers)
    {
        Width = width;
        Height = height;
        PaperColor = paperColor;
        Layers = layers;
    }

    public static TimelapseDocumentSnapshot Capture(DrawingDocument document)
    {
        var clones = new List<DrawingLayer>(document.Layers.Count);
        var map = new Dictionary<DrawingLayer, DrawingLayer>(document.Layers.Count);

        foreach (var layer in document.Layers)
        {
            var clone = CloneLayerShallow(layer);
            clones.Add(clone);
            map[layer] = clone;
        }

        for (var i = 0; i < document.Layers.Count; i++)
        {
            var source = document.Layers[i];
            var clone = clones[i];
            if (source.Parent != null && map.TryGetValue(source.Parent, out var parent))
            {
                clone.Parent = parent;
                if (!parent.Children.Contains(clone))
                    parent.Children.Add(clone);
            }
        }

        var paper = document.PaperColor;
        var paperColor = (uint)(paper.B | (paper.G << 8) | (paper.R << 16) | (paper.A << 24));
        return new TimelapseDocumentSnapshot(document.Width, document.Height, paperColor, clones);
    }

    public void Dispose()
    {
        foreach (var layer in Layers)
            layer.Dispose();
    }

    private static DrawingLayer CloneLayerShallow(DrawingLayer source)
    {
        var clone = new DrawingLayer(source.Name, source.Width, source.Height)
        {
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            IsAlphaLocked = source.IsAlphaLocked,
            IsReference = source.IsReference,
            IsPaper = source.IsPaper,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
            LayerColor = source.LayerColor,
            ExpressionColor = source.ExpressionColor,
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY,
            IsGroup = source.IsGroup,
            IsOpen = source.IsOpen,
            IsClipping = source.IsClipping,
            IndentLevel = source.IndentLevel
        };
        clone.RestoreTiles(source.CaptureTiles());
        return clone;
    }
}

public sealed class TimelapseSession
{
    private const int ExportFps = 12;
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DirectoryPath { get; }
    public TimelapseManifest Manifest { get; private set; }
    public bool IsRecording { get { lock (_gate) return _isRecording; } }
    public int FrameCount { get { lock (_gate) return Manifest.Frames.Count; } }

    private readonly object _gate = new();
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private bool _isRecording;

    private TimelapseSession(string directoryPath, TimelapseManifest manifest, bool isRecording)
    {
        DirectoryPath = directoryPath;
        Manifest = manifest;
        _isRecording = isRecording;
    }

    public static TimelapseSession StartNew(string documentName, DrawingDocument document)
    {
        var sessionId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var safeName = SafePathPart(documentName);
        var directory = Path.Combine(AppPaths.TimelapsesDirectory, $"{safeName}-{sessionId}");
        Directory.CreateDirectory(directory);

        var session = new TimelapseSession(directory, new TimelapseManifest
        {
            SessionId = sessionId,
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "Untitled" : documentName.Trim(),
            DocumentWidth = Math.Max(1, document.Width),
            DocumentHeight = Math.Max(1, document.Height),
            CreatedUtc = DateTimeOffset.UtcNow
        }, isRecording: true);
        session.SaveManifest();
        return session;
    }

    public void SetRecording(bool isRecording)
    {
        lock (_gate)
            _isRecording = isRecording;
        SaveManifest();
    }

    public async Task<bool> CaptureFrameAsync(TimelapseDocumentSnapshot snapshot)
    {
        await _captureSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CaptureFrameCore(snapshot)).ConfigureAwait(false);
        }
        finally
        {
            _captureSemaphore.Release();
        }
    }

    public async Task WaitForIdleAsync()
    {
        await _captureSemaphore.WaitAsync().ConfigureAwait(false);
        _captureSemaphore.Release();
    }

    private bool CaptureFrameCore(TimelapseDocumentSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_isRecording)
                return false;
        }

        if (snapshot.Width <= 0 || snapshot.Height <= 0 || snapshot.Layers.Count == 0)
            return false;

        Directory.CreateDirectory(DirectoryPath);

        int index;
        lock (_gate)
            index = Manifest.Frames.Count;
        var fileName = $"frame_{index:D6}.png";
        var path = Path.Combine(DirectoryPath, fileName);

        using var bitmap = RenderSnapshotBitmap(snapshot);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Failed to encode timelapse frame.");
        using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            data.SaveTo(stream);

        lock (_gate)
        {
            if (!_isRecording)
            {
                try { File.Delete(path); } catch { }
                return false;
            }

            Manifest.Frames.Add(new TimelapseFrame
            {
                Index = index,
                CreatedUtc = DateTimeOffset.UtcNow,
                FileName = fileName
            });
        }
        SaveManifest();
        return true;
    }

    private static unsafe SKBitmap RenderSnapshotBitmap(TimelapseDocumentSnapshot snapshot)
    {
        var bgra = new LayerCompositor().CompositeToBgra(snapshot.Layers, snapshot.Width, snapshot.Height, snapshot.PaperColor);
        var bitmap = new SKBitmap(new SKImageInfo(snapshot.Width, snapshot.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        fixed (byte* src = bgra)
        {
            var dst = (byte*)bitmap.GetPixels().ToPointer();
            Buffer.MemoryCopy(src, dst, bgra.Length, bgra.Length);
        }
        return bitmap;
    }

    public IReadOnlyList<string> FramePaths()
    {
        TimelapseFrame[] frames;
        lock (_gate)
            frames = Manifest.Frames.ToArray();

        return frames.Select(f => Path.Combine(DirectoryPath, f.FileName))
            .Where(File.Exists)
            .ToArray();
    }

    public string? FirstFramePath()
        => FramePaths().FirstOrDefault();

    public bool HasEnoughFrames(TimelapseLength length)
        => length == TimelapseLength.All || FrameCount >= RequiredFrameCount(length);

    public void ExportSequence(string outputDirectory, TimelapseExportSettings settings)
    {
        Directory.CreateDirectory(outputDirectory);
        var frames = SelectFrames(FramePaths(), settings.Length).ToArray();
        if (frames.Length == 0)
            throw new InvalidOperationException("No timelapse frames have been recorded.");

        for (var i = 0; i < frames.Length; i++)
        {
            using var source = SKBitmap.Decode(frames[i])
                ?? throw new InvalidDataException($"Timelapse frame could not be decoded: {frames[i]}");
            using var composed = ComposeFrame(source, settings);
            using var image = SKImage.FromBitmap(composed);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidDataException("Failed to encode exported timelapse frame.");
            var path = Path.Combine(outputDirectory, $"timelapse_{i:D5}.png");
            using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            data.SaveTo(stream);
        }

        File.WriteAllText(Path.Combine(outputDirectory, "timelapse-export.txt"),
            $"Floss timelapse export\nFrames: {frames.Length}\nFPS: {ExportFps}\nLength: {settings.Length}\nAspect: {settings.Aspect}\n");
    }

    public async Task ExportVideoAsync(string outputPath, TimelapseExportSettings settings, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "floss-timelapse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ExportSequence(tempDir, settings);
            await Task.Run(() => TranscodeImageSequence(tempDir, outputPath, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TranscodeImageSequence(string frameDirectory, string outputPath, CancellationToken cancellationToken)
    {
        var frames = Directory.EnumerateFiles(frameDirectory, "timelapse_*.png").OrderBy(p => p).ToArray();
        if (frames.Length == 0)
            throw new InvalidOperationException("No timelapse frames were rendered for video export.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Mp4WriteCallback writeCb = (long offset, ReadOnlySpan<byte> data, object? token) =>
        {
            var stream = (FileStream)token!;
            if (stream.Position != offset)
                stream.Seek(offset, SeekOrigin.Begin);
            stream.Write(data);
            return false;
        };

        using var mux = Mp4Muxer.Open(sequentialMode: true, fragmentation: false, writeCb, fs)
            ?? throw new InvalidOperationException("Failed to initialize MP4 muxer.");

        // Use the first frame to get dimensions
        using var firstFrame = SKBitmap.Decode(frames[0]);
        var tr = new Mp4TrackInfo
        {
            ObjectTypeIndication = Mp4ObjectType.Mjpeg,
            Language0 = (byte)'u',
            Language1 = (byte)'n',
            Language2 = (byte)'d',
            TrackMediaKind = TrackMediaKind.Video,
            TimeScale = ExportFps,
            DefaultDuration = 1
        };
        tr.U.VideoWidth = firstFrame.Width;
        tr.U.VideoHeight = firstFrame.Height;

        var trackId = mux.AddTrack(tr);
        var frameDuration = 1;

        foreach (var framePath in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var bitmap = SKBitmap.Decode(framePath)
                ?? throw new InvalidDataException($"Could not decode frame: {framePath}");

            using var image = SKImage.FromBitmap(bitmap);
            using var jpegData = image.Encode(SKEncodedImageFormat.Jpeg, 92)
                ?? throw new InvalidDataException("Failed to encode JPEG frame.");

            var jpegBytes = jpegData.ToArray();

            var err = mux.PutSample(trackId, jpegBytes, frameDuration, Mp4SampleKind.RandomAccess);
            if (err != Mp4Status.Ok)
                throw new InvalidOperationException($"MP4 muxer error: {err}");
        }

    }

    public static IReadOnlyList<string> SelectFrames(IReadOnlyList<string> framePaths, TimelapseLength length)
    {
        if (length == TimelapseLength.All)
            return framePaths.ToArray();

        var target = RequiredFrameCount(length);
        if (framePaths.Count <= target)
            return framePaths.ToArray();

        var result = new List<string>(target);
        var max = framePaths.Count - 1;
        for (var i = 0; i < target; i++)
        {
            var src = (int)Math.Round(i * max / Math.Max(1.0, target - 1.0));
            result.Add(framePaths[src]);
        }
        return result;
    }

    public static int RequiredFrameCount(TimelapseLength length) => length switch
    {
        TimelapseLength.Seconds15 => 15 * ExportFps,
        TimelapseLength.Seconds30 => 30 * ExportFps,
        TimelapseLength.Seconds60 => 60 * ExportFps,
        _ => 0
    };

    public static SKBitmap ComposeFrame(SKBitmap source, TimelapseExportSettings settings)
    {
        var longest = Math.Clamp(settings.LongestSidePixels, 64, 8192);
        var sourceAspect = source.Width / (double)Math.Max(1, source.Height);
        double targetAspect = settings.Aspect switch
        {
            TimelapseAspect.Landscape => 16.0 / 9.0,
            TimelapseAspect.Portrait => 9.0 / 16.0,
            _ => sourceAspect
        };

        int targetW;
        int targetH;
        if (targetAspect >= 1)
        {
            targetW = longest;
            targetH = Math.Max(1, (int)Math.Round(longest / targetAspect));
        }
        else
        {
            targetH = longest;
            targetW = Math.Max(1, (int)Math.Round(longest * targetAspect));
        }

        var result = new SKBitmap(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);

        var scale = Math.Min(targetW / (double)source.Width, targetH / (double)source.Height);
        var drawW = Math.Max(1, (float)(source.Width * scale));
        var drawH = Math.Max(1, (float)(source.Height * scale));
        var dest = new SKRect((targetW - drawW) / 2f, (targetH - drawH) / 2f, (targetW + drawW) / 2f, (targetH + drawH) / 2f);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(source, dest, paint);
        canvas.Flush();
        return result;
    }

    private void SaveManifest()
    {
        Directory.CreateDirectory(DirectoryPath);
        string json;
        lock (_gate)
            json = JsonSerializer.Serialize(Manifest, JsonOptions);

        File.WriteAllText(Path.Combine(DirectoryPath, ManifestFileName),
            json);
    }

    private static string SafePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string((string.IsNullOrWhiteSpace(value) ? "Untitled" : value)
            .Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c)
            .ToArray())
            .Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(safe) ? "Untitled" : safe;
    }
}
