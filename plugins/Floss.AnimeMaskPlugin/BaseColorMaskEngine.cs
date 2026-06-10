using System.Net.Http;
using Avalonia.Media;
using Floss.App.Config;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Floss.AnimeMaskPlugin;

public enum AnimeSegStatus
{
    Applied,
    ModelMissing,
    ModelLoadFailed,
    InferenceFailed,
    NoForegroundDetected,
}

public readonly struct MaskGenerationResult
{
    public required List<byte[]> Masks { get; init; }
    public AnimeSegStatus AnimeSeg { get; init; }
}

/// <summary>
/// SkyTNT anime-segmentation (isnet_is) — character silhouette masks for base color blocking.
/// https://github.com/SkyTNT/anime-segmentation
/// </summary>
public static class BaseColorMaskEngine
{
    public static readonly Color DefaultMaskFillColor = Color.FromRgb(200, 200, 200);

    private static InferenceSession? _isnetSession;
    private static readonly object _isnetLock = new();
    private static bool _modelConsentGiven;
    private static string? _lastError;

    private const int IsnetInputSize = 1024;
    private const string ModelDownloadUrl = "https://cdn.flosspaint.com/isnetis.onnx";

    private static string ModelCachePath => AppPaths.AnimeSegModelPath;

    private static string ConsentFlagPath => AppPaths.ModelConsentPath;

    public static string ModelPath => ModelCachePath;
    public static string? LastError => _lastError;

    public static bool NeedsConsentPrompt =>
        !_modelConsentGiven && !ModelFileExists && !File.Exists(ConsentFlagPath);

    public static void GrantConsent()
    {
        _modelConsentGiven = true;
        try { Directory.CreateDirectory(AppPaths.DataDirectory); File.WriteAllText(ConsentFlagPath, "ok"); }
        catch { /* best effort */ }
    }

    public static bool WasDeclined =>
        !_modelConsentGiven && !ModelFileExists && File.Exists(ConsentFlagPath);

    public static bool ModelFileExists => File.Exists(ModelCachePath);

    public static bool IsModelAvailable => _isnetSession != null || ModelFileExists;

    private static async Task<string?> EnsureModelAsync()
    {
        if (File.Exists(ModelCachePath))
            return ModelCachePath;

        try
        {
            Directory.CreateDirectory(AppPaths.ModelsDirectory);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync(ModelDownloadUrl).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ModelCachePath, bytes).ConfigureAwait(false);
            _lastError = null;
            return ModelCachePath;
        }
        catch (Exception ex)
        {
            _lastError = $"Download failed: {ex.Message}";
            return null;
        }
    }

    public static async Task<bool> EnsureModelReadyAsync()
    {
        var path = await EnsureModelAsync().ConfigureAwait(false);
        if (path == null)
            return false;

        lock (_isnetLock)
        {
            if (_isnetSession != null)
                return true;

            try
            {
                _isnetSession = new InferenceSession(path);
                _lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"Failed to load model: {ex.Message}";
                _isnetSession = null;
                return false;
            }
        }
    }

    private static InferenceSession? GetIsnetSession()
    {
        if (_isnetSession != null) return _isnetSession;
        lock (_isnetLock)
        {
            if (_isnetSession != null) return _isnetSession;

            string? path = File.Exists(ModelCachePath) ? ModelCachePath : null;
            if (path == null)
                return null;

            try
            {
                _isnetSession = new InferenceSession(path);
                _lastError = null;
            }
            catch (Exception ex)
            {
                _lastError = $"Failed to load model: {ex.Message}";
                _isnetSession = null;
            }

            return _isnetSession;
        }
    }

    public static MaskGenerationResult GenerateMasks(byte[] bgra, int w, int h, Color? maskFillColor = null)
    {
        if (!ModelFileExists && GetIsnetSession() == null)
            return Empty(AnimeSegStatus.ModelMissing);

        if (GetIsnetSession() == null)
            return Empty(AnimeSegStatus.ModelLoadFailed);

        byte[]? silhouette;
        try
        {
            silhouette = RunAnimeSeg(bgra, w, h);
        }
        catch (Exception ex)
        {
            _lastError = $"Inference failed: {ex.Message}";
            return Empty(AnimeSegStatus.InferenceFailed);
        }

        if (silhouette == null)
            return Empty(AnimeSegStatus.NoForegroundDetected);

        var fill = maskFillColor ?? DefaultMaskFillColor;
        var layer = TintCharacterMask(silhouette, w, h, fill);
        return new MaskGenerationResult
        {
            Masks = HasVisiblePixels(layer) ? [layer] : [],
            AnimeSeg = AnimeSegStatus.Applied,
        };
    }

    private static MaskGenerationResult Empty(AnimeSegStatus status) =>
        new() { Masks = [], AnimeSeg = status };

    public static byte[] TintCharacterMask(byte[] characterMask, int w, int h, Color fill)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            if (characterMask[i] == 0) continue;
            var p = i * 4;
            bgra[p] = fill.B;
            bgra[p + 1] = fill.G;
            bgra[p + 2] = fill.R;
            bgra[p + 3] = 255;
        }
        return bgra;
    }

    private static bool HasVisiblePixels(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i + 3] != 0)
                return true;
        return false;
    }

    private static byte[]? RunAnimeSeg(byte[] bgra, int w, int h)
    {
        var sess = GetIsnetSession();
        if (sess == null) return null;

        var inp = new float[1 * 3 * IsnetInputSize * IsnetInputSize];
        var letterbox = PreprocessAnimeSegInput(bgra, w, h, inp);

        var tensor = new DenseTensor<float>(inp, [1, 3, IsnetInputSize, IsnetInputSize]);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("img", tensor) };

        using var results = sess.Run(inputs);
        var output = results[0].AsTensor<float>();

        var mask = new byte[w * h];
        DecodeAnimeSegOutput(output, letterbox, w, h, mask);

        var fg = 0;
        for (var i = 0; i < mask.Length; i++)
            if (mask[i] != 0) fg++;
        if (fg < mask.Length / 200)
            return null;

        return mask;
    }

    private struct AnimeSegLetterbox
    {
        public int InnerW;
        public int InnerH;
        public int PadX;
        public int PadY;
    }

    private static AnimeSegLetterbox PreprocessAnimeSegInput(byte[] bgra, int srcW, int srcH, float[] dst)
    {
        var s = IsnetInputSize;
        int innerH, innerW;
        if (srcH > srcW)
        {
            innerH = s;
            innerW = (int)(s * (long)srcW / srcH);
        }
        else
        {
            innerW = s;
            innerH = (int)(s * (long)srcH / srcW);
        }

        var padY = (s - innerH) / 2;
        var padX = (s - innerW) / 2;

        for (var y = 0; y < innerH; y++)
        {
            var sy = (y + 0.5) * srcH / innerH - 0.5;
            var y0 = (int)Math.Floor(sy);
            var y1 = Math.Min(y0 + 1, srcH - 1);
            var fy = (float)(sy - y0);
            y0 = Math.Clamp(y0, 0, srcH - 1);

            for (var x = 0; x < innerW; x++)
            {
                var sx = (x + 0.5) * srcW / innerW - 0.5;
                var x0 = (int)Math.Floor(sx);
                var x1 = Math.Min(x0 + 1, srcW - 1);
                var fx = (float)(sx - x0);
                x0 = Math.Clamp(x0, 0, srcW - 1);

                float Sample(int px, int py, int channel)
                {
                    var p = (py * srcW + px) * 4;
                    return bgra[p + channel] / 255f;
                }

                float Lerp(float a, float b, float t) => a + (b - a) * t;

                var r = Lerp(Lerp(Sample(x0, y0, 2), Sample(x1, y0, 2), fx),
                             Lerp(Sample(x0, y1, 2), Sample(x1, y1, 2), fx), fy);
                var g = Lerp(Lerp(Sample(x0, y0, 1), Sample(x1, y0, 1), fx),
                             Lerp(Sample(x0, y1, 1), Sample(x1, y1, 1), fx), fy);
                var b = Lerp(Lerp(Sample(x0, y0, 0), Sample(x1, y0, 0), fx),
                             Lerp(Sample(x0, y1, 0), Sample(x1, y1, 0), fx), fy);

                var dy = padY + y;
                var dx = padX + x;
                var idx = dy * s + dx;
                dst[0 * s * s + idx] = r;
                dst[1 * s * s + idx] = g;
                dst[2 * s * s + idx] = b;
            }
        }

        return new AnimeSegLetterbox { InnerW = innerW, InnerH = innerH, PadX = padX, PadY = padY };
    }

    private static void DecodeAnimeSegOutput(Tensor<float> output, AnimeSegLetterbox box, int dstW, int dstH, byte[] mask)
    {
        var s = IsnetInputSize;
        for (var y = 0; y < dstH; y++)
        {
            var sy = (y + 0.5) * box.InnerH / dstH - 0.5 + box.PadY;
            var y0 = (int)Math.Floor(sy);
            var y1 = Math.Min(y0 + 1, s - 1);
            var fy = (float)(sy - y0);
            y0 = Math.Clamp(y0, 0, s - 1);

            for (var x = 0; x < dstW; x++)
            {
                var sx = (x + 0.5) * box.InnerW / dstW - 0.5 + box.PadX;
                var x0 = (int)Math.Floor(sx);
                var x1 = Math.Min(x0 + 1, s - 1);
                var fx = (float)(sx - x0);
                x0 = Math.Clamp(x0, 0, s - 1);

                float Sample(int px, int py) => output[0, 0, py, px];
                float Lerp(float a, float b, float t) => a + (b - a) * t;
                var val = Lerp(Lerp(Sample(x0, y0), Sample(x1, y0), fx),
                                Lerp(Sample(x0, y1), Sample(x1, y1), fx), fy);
                mask[y * dstW + x] = val > 0.5f ? byte.MaxValue : (byte)0;
            }
        }
    }
}
