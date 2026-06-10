using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Floss.App.Canvas;
using Floss.App.Document;
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
    public string? DocumentPath { get; set; }
    public int DocumentWidth { get; set; }
    public int DocumentHeight { get; set; }
    public int CaptureWidth { get; set; }
    public int CaptureHeight { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public List<TimelapseFrame> Frames { get; set; } = [];
}

public sealed class TimelapseExportSettings
{
    public TimelapseLength Length { get; set; } = TimelapseLength.All;
    public TimelapseAspect Aspect { get; set; } = TimelapseAspect.Original;
    public int LongestSidePixels { get; set; } = 1280;
}

public sealed class TimelapseSession : IDisposable
{
    private const int ExportFps = 12;
    private const int MaxCaptureLongestSide = 4096;
    private const long MaxAssemblePixels = 64_000_000;
    private const int StoredFrameJpegQuality = 92;
    private const string ManifestFileName = "manifest.json";
    private const string FrameLogFileName = "frames.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DirectoryPath { get; }
    public TimelapseManifest Manifest { get; private set; }
    public string SessionId => Manifest.SessionId;
    public bool IsRecording { get { lock (_gate) return _isRecording; } }
    public int FrameCount { get { lock (_gate) return Manifest.Frames.Count; } }

    private readonly object _gate = new();
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private readonly LayerCompositor _compositor = new();
    private readonly List<DrawingLayer> _layerCache = [];
    private bool _isRecording;
    private bool _manifestDirty;
    private bool _pendingFullRefresh = true;
    private PixelRegion _pendingDirtyRegion = PixelRegion.Empty;
    private int _docWidth;
    private int _docHeight;
    private int _captureWidth;
    private int _captureHeight;
    private uint _paperColor;
    private int _cachedLayerCount;
    private int _cachedDocWidth;
    private int _cachedDocHeight;

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

        var (captureWidth, captureHeight) = ComputeCaptureDimensions(document.Width, document.Height);
        var session = new TimelapseSession(directory, new TimelapseManifest
        {
            SessionId = sessionId,
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "Untitled" : documentName.Trim(),
            DocumentWidth = Math.Max(1, document.Width),
            DocumentHeight = Math.Max(1, document.Height),
            CaptureWidth = captureWidth,
            CaptureHeight = captureHeight,
            CreatedUtc = DateTimeOffset.UtcNow
        }, isRecording: true);
        session.SaveManifest();
        return session;
    }

    public static TimelapseSession? TryLoad(string directoryPath, bool isRecording = false)
    {
        if (!Directory.Exists(directoryPath))
            return null;

        var manifestPath = Path.Combine(directoryPath, ManifestFileName);
        if (!File.Exists(manifestPath))
            return null;

        TimelapseManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TimelapseManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("Timelapse manifest was empty.");
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.SessionId))
            return null;

        ReconcileFrameRecords(manifest, directoryPath);
        return new TimelapseSession(directoryPath, manifest, isRecording);
    }

    public static TimelapseSession? FindForDocument(
        string? documentPath,
        string documentName,
        int documentWidth,
        int documentHeight,
        string? sessionId = null,
        string? timelapsesRoot = null)
    {
        var root = timelapsesRoot ?? AppPaths.TimelapsesDirectory;
        if (!Directory.Exists(root))
            return null;

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var session = TryLoad(directory);
                if (session != null && string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                    return session;
            }
        }

        var normalizedPath = NormalizeDocumentPath(documentPath);
        TimelapseSession? bestMatch = null;
        var bestScore = long.MinValue;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var session = TryLoad(directory);
            if (session == null)
                continue;

            long score;
            if (!string.IsNullOrEmpty(normalizedPath)
                && string.Equals(NormalizeDocumentPath(session.Manifest.DocumentPath), normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                score = 1_000_000_000L + session.FrameCount;
            }
            else if (string.Equals(session.Manifest.DocumentName, documentName.Trim(), StringComparison.OrdinalIgnoreCase)
                && session.Manifest.DocumentWidth == Math.Max(1, documentWidth)
                && session.Manifest.DocumentHeight == Math.Max(1, documentHeight)
                && session.FrameCount > 0)
            {
                score = 100_000_000L + session.FrameCount;
            }
            else
            {
                continue;
            }

            var lastFrameUtc = session.Manifest.Frames.Count > 0
                ? session.Manifest.Frames[^1].CreatedUtc.UtcTicks
                : session.Manifest.CreatedUtc.UtcTicks;
            score = score * 10_000_000_000L + lastFrameUtc;

            if (score > bestScore)
            {
                bestMatch = session;
                bestScore = score;
            }
        }

        return bestMatch;
    }

    public void BindDocumentPath(string? documentPath)
    {
        var normalized = NormalizeDocumentPath(documentPath);
        if (string.IsNullOrEmpty(normalized))
            return;

        lock (_gate)
        {
            if (string.Equals(Manifest.DocumentPath, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            Manifest.DocumentPath = normalized;
            _manifestDirty = true;
        }

        FlushManifest();
    }

    public void SetRecording(bool isRecording)
    {
        lock (_gate)
            _isRecording = isRecording;
        FlushManifest();
    }

    public void PrepareCaptureFromDocument(DrawingDocument document)
    {
        using (document.RenderLock.Read())
        {
            _docWidth = document.Width;
            _docHeight = document.Height;
            (_captureWidth, _captureHeight) = ComputeCaptureDimensions(_docWidth, _docHeight);
            var paper = document.PaperColor;
            _paperColor = (uint)(paper.B | (paper.G << 8) | (paper.R << 16) | (paper.A << 24));
            _pendingDirtyRegion = document.LastHistoryVisualDirtyRegion;
            _pendingFullRefresh = document.LastHistoryRequiresFullVisualRefresh;

            var layerCount = document.Layers.Count;
            var structureChanged = _layerCache.Count == 0
                || layerCount != _cachedLayerCount
                || _docWidth != _cachedDocWidth
                || _docHeight != _cachedDocHeight;

            if (structureChanged || _pendingFullRefresh)
                RebuildLayerCache(document);
            else
                UpdateLayerCache(document, _pendingDirtyRegion);

            _cachedLayerCount = layerCount;
            _cachedDocWidth = _docWidth;
            _cachedDocHeight = _docHeight;

            lock (_gate)
            {
                Manifest.DocumentWidth = Math.Max(1, _docWidth);
                Manifest.DocumentHeight = Math.Max(1, _docHeight);
                Manifest.CaptureWidth = _captureWidth;
                Manifest.CaptureHeight = _captureHeight;
            }
        }
    }

    public async Task<bool> CapturePreparedFrameAsync()
    {
        await _captureSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(CapturePreparedFrameCore).ConfigureAwait(false);
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
        FlushManifest();
    }

    public void Dispose()
    {
        _captureSemaphore.Dispose();
        _compositor.Dispose();
        foreach (var layer in _layerCache)
            layer.Dispose();
        _layerCache.Clear();
    }

    public static (int Width, int Height) ComputeCaptureDimensions(int docWidth, int docHeight, int maxLongestSide = MaxCaptureLongestSide)
    {
        if (docWidth <= 0 || docHeight <= 0)
            return (Math.Max(1, docWidth), Math.Max(1, docHeight));

        var longest = Math.Max(docWidth, docHeight);
        if (longest <= maxLongestSide)
            return (docWidth, docHeight);

        var scale = maxLongestSide / (double)longest;
        return (
            Math.Max(1, (int)Math.Round(docWidth * scale)),
            Math.Max(1, (int)Math.Round(docHeight * scale)));
    }

    private const int MaxCompositeLod = 2;

    public static int ChooseAssembleLod(int docWidth, int docHeight)
    {
        for (var lod = 0; lod <= MaxCompositeLod; lod++)
        {
            var width = Math.Max(1, docWidth >> lod);
            var height = Math.Max(1, docHeight >> lod);
            if ((long)width * height <= MaxAssemblePixels)
                return lod;
        }
        return MaxCompositeLod;
    }

    private bool CapturePreparedFrameCore()
    {
        lock (_gate)
        {
            if (!_isRecording)
                return false;
        }

        if (_docWidth <= 0 || _docHeight <= 0 || _layerCache.Count == 0)
            return false;

        Directory.CreateDirectory(DirectoryPath);

        int index;
        lock (_gate)
            index = Manifest.Frames.Count;
        var fileName = $"frame_{index:D6}.jpg";
        var path = Path.Combine(DirectoryPath, fileName);

        using var bitmap = RenderPreparedFrameBitmap();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, StoredFrameJpegQuality)
            ?? throw new InvalidDataException("Failed to encode timelapse frame.");
        using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            data.SaveTo(stream);

        TimelapseFrame frame;
        lock (_gate)
        {
            if (!_isRecording)
            {
                try { File.Delete(path); } catch { }
                return false;
            }

            frame = new TimelapseFrame
            {
                Index = index,
                CreatedUtc = DateTimeOffset.UtcNow,
                FileName = fileName
            };
            Manifest.Frames.Add(frame);
            _manifestDirty = true;
        }

        AppendFrameRecord(frame);
        return true;
    }

    private SKBitmap RenderPreparedFrameBitmap()
    {
        var assembleLod = ChooseAssembleLod(_docWidth, _docHeight);
        var effectiveWidth = Math.Max(1, _docWidth >> assembleLod);
        var effectiveHeight = Math.Max(1, _docHeight >> assembleLod);

        // Timelapse frames must capture the whole canvas. The live-viewport compositor
        // composites tiles in budgeted batches (32/frame) and AssembleSkBitmap only
        // blits finished tiles — partial passes leave black gaps that grow with each stroke.
        _compositor.Invalidate(null);
        var fullViewport = new PixelRegion(0, 0, _docWidth, _docHeight);
        var guard = 0;
        while (_compositor.Composite(_layerCache, _docWidth, _docHeight, _paperColor, fullViewport, forceLod: assembleLod))
        {
            if (++guard > 8192)
                throw new InvalidOperationException("Timelapse composite did not finish.");
        }

        using var assembled = _compositor.AssembleSkBitmap(effectiveWidth, effectiveHeight, assembleLod, _paperColor);
        if (assembled.Width == _captureWidth && assembled.Height == _captureHeight)
            return assembled.Copy();

        return assembled.Resize(
            new SKImageInfo(_captureWidth, _captureHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            SKSamplingOptions.Default);
    }

    private void RebuildLayerCache(DrawingDocument document)
    {
        foreach (var layer in _layerCache)
            layer.Dispose();
        _layerCache.Clear();

        var map = new Dictionary<DrawingLayer, DrawingLayer>(document.Layers.Count);
        foreach (var layer in document.Layers)
        {
            var clone = CloneLayer(layer);
            _layerCache.Add(clone);
            map[layer] = clone;
        }

        for (var i = 0; i < document.Layers.Count; i++)
        {
            var source = document.Layers[i];
            var clone = _layerCache[i];
            if (source.Parent != null && map.TryGetValue(source.Parent, out var parent))
            {
                clone.Parent = parent;
                if (!parent.Children.Contains(clone))
                    parent.Children.Add(clone);
            }
        }
    }

    private void UpdateLayerCache(DrawingDocument document, PixelRegion dirtyRegion)
    {
        for (var i = 0; i < document.Layers.Count; i++)
            SyncLayerProperties(document.Layers[i], _layerCache[i]);

        if (dirtyRegion.IsEmpty)
            return;

        for (var i = 0; i < document.Layers.Count; i++)
        {
            var source = document.Layers[i];
            var clone = _layerCache[i];
            var layerRegion = source.DocumentContentBounds
                .Translate(source.OffsetX, source.OffsetY)
                .ClipTo(_docWidth, _docHeight);
            var region = dirtyRegion.Intersect(layerRegion);
            if (region.IsEmpty)
                continue;

            foreach (var (key, data) in source.CaptureTiles(region))
                clone.RestoreTile(key.X, key.Y, data);
        }
    }

    private static DrawingLayer CloneLayer(DrawingLayer source)
    {
        var clone = new DrawingLayer(source.Name, source.Width, source.Height);
        SyncLayerProperties(source, clone);
        clone.RestoreTiles(source.CaptureTiles());
        return clone;
    }

    private static void SyncLayerProperties(DrawingLayer source, DrawingLayer clone)
    {
        clone.IsVisible = source.IsVisible;
        clone.IsLocked = source.IsLocked;
        clone.IsAlphaLocked = source.IsAlphaLocked;
        clone.IsReference = source.IsReference;
        clone.IsPaper = source.IsPaper;
        clone.Opacity = source.Opacity;
        clone.BlendMode = source.BlendMode;
        clone.LayerColor = source.LayerColor;
        clone.ExpressionColor = source.ExpressionColor;
        clone.OffsetX = source.OffsetX;
        clone.OffsetY = source.OffsetY;
        clone.IsGroup = source.IsGroup;
        clone.IsOpen = source.IsOpen;
        clone.IsClipping = source.IsClipping;
        clone.IndentLevel = source.IndentLevel;
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
            using var source = DecodeFrame(frames[i])
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
        await Task.Run(() => ExportVideoCore(outputPath, settings, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private void ExportVideoCore(string outputPath, TimelapseExportSettings settings, CancellationToken cancellationToken)
    {
        var frames = SelectFrames(FramePaths(), settings.Length).ToArray();
        if (frames.Length == 0)
            throw new InvalidOperationException("No timelapse frames have been recorded.");

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

        using var firstSource = DecodeFrame(frames[0])
            ?? throw new InvalidDataException($"Timelapse frame could not be decoded: {frames[0]}");
        using var firstComposed = ComposeFrame(firstSource, settings);

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
        tr.U.VideoWidth = firstComposed.Width;
        tr.U.VideoHeight = firstComposed.Height;

        var trackId = mux.AddTrack(tr);
        const int frameDuration = 1;

        EncodeAndMuxFrame(mux, trackId, firstComposed, frameDuration);

        for (var i = 1; i < frames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var source = DecodeFrame(frames[i])
                ?? throw new InvalidDataException($"Timelapse frame could not be decoded: {frames[i]}");
            using var composed = ComposeFrame(source, settings);
            EncodeAndMuxFrame(mux, trackId, composed, frameDuration);
        }
    }

    private static SKBitmap? DecodeFrame(string path)
    {
        if (!File.Exists(path))
            return null;

        var ext = Path.GetExtension(path);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return SKBitmap.Decode(path);

        return null;
    }

    private static void EncodeAndMuxFrame(Mp4Muxer mux, int trackId, SKBitmap composed, int frameDuration)
    {
        using var image = SKImage.FromBitmap(composed);
        using var jpegData = image.Encode(SKEncodedImageFormat.Jpeg, 92)
            ?? throw new InvalidDataException("Failed to encode JPEG frame.");

        var err = mux.PutSample(trackId, jpegData.ToArray(), frameDuration, Mp4SampleKind.RandomAccess);
        if (err != Mp4Status.Ok)
            throw new InvalidOperationException($"MP4 muxer error: {err}");
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

    private void AppendFrameRecord(TimelapseFrame frame)
    {
        var line = JsonSerializer.Serialize(frame) + Environment.NewLine;
        File.AppendAllText(Path.Combine(DirectoryPath, FrameLogFileName), line);
    }

    private void FlushManifest()
    {
        lock (_gate)
        {
            if (!_manifestDirty)
                return;
        }
        SaveManifest();
    }

    private void SaveManifest()
    {
        Directory.CreateDirectory(DirectoryPath);
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(Manifest, JsonOptions);
            _manifestDirty = false;
        }

        File.WriteAllText(Path.Combine(DirectoryPath, ManifestFileName), json);
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

    private static string? NormalizeDocumentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static void ReconcileFrameRecords(TimelapseManifest manifest, string directoryPath)
    {
        var framesByIndex = manifest.Frames
            .Where(f => !string.IsNullOrWhiteSpace(f.FileName))
            .GroupBy(f => f.Index)
            .ToDictionary(g => g.Key, g => g.Last());

        foreach (var file in Directory.EnumerateFiles(directoryPath, "frame_*.jpg"))
        {
            var name = Path.GetFileName(file);
            if (!TryParseFrameIndex(name, out var index))
                continue;

            if (framesByIndex.ContainsKey(index))
                continue;

            framesByIndex[index] = new TimelapseFrame
            {
                Index = index,
                CreatedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero),
                FileName = name
            };
        }

        manifest.Frames = framesByIndex.Values
            .OrderBy(f => f.Index)
            .ToList();
    }

    private static bool TryParseFrameIndex(string fileName, out int index)
    {
        index = -1;
        if (!fileName.StartsWith("frame_", StringComparison.OrdinalIgnoreCase))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length <= "frame_".Length)
            return false;

        return int.TryParse(stem["frame_".Length..], out index);
    }
}
