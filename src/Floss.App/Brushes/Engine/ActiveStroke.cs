using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes.Engine;

public sealed partial class BrushEngine
{
    private sealed class ActiveStroke : IDisposable
    {
        private readonly BrushPreset _brush;
        private const int MaxCachedMasks = 16;
        // Match Drawpile's 1024-dab batching floor. Pressure/rotation dynamics
        // create many nearby keys; trimming below normal batch size turns the
        // cache into churn during fast strokes.
        private const int MaxCachedDabs = 1024;
        // Brushes larger than 1024² logical footprint are downscaled into the
        // cache bitmap and bilinear-upsampled at stamp time.
        private const int MaxCachedDabPixels = 4 * 1024 * 1024;
        private const int MaxCachedColorDabs = 256;
        private readonly Dictionary<(int TipIndex, int Hardness), SKBitmap> _maskCache = new();
        private readonly Dictionary<CachedDabKey, CachedDab> _dabCache = new();
        private readonly Queue<CachedDabKey> _dabCacheOrder = new();
        private readonly Dictionary<CachedDabKey, CachedColorDab> _colorDabCache = new();
        private readonly Queue<CachedDabKey> _colorDabCacheOrder = new();
        private readonly HashSet<SKBitmap> _ownedMasks = new();
        private readonly List<IBrushTip>? _ownedTipSet;
        private int[]? _cachedTipIndices;

        private readonly SKColor _baseColor;
        private SKColor _currentColor;
        private SKColorFilter? _mixedFilter;

        public ActiveStroke(BrushPreset brush, CanvasInputSample sample)
        {
            _brush = brush;
            ParamGraphLookup = new Dictionary<BrushParameterTarget, BrushParameterGraph>(brush.ParameterGraphs.Count);
            foreach (var g in brush.ParameterGraphs)
                if (g.Validate().Count == 0)
                    ParamGraphLookup[g.Target] = g;
            BrushMaterialTips.BindToPreset(brush);
            if (brush.TipSelectionMode != BrushTipSelectionMode.Single && brush.Tips.Count > 0)
                _ownedTipSet = brush.Tips.Select(t => t.CreateTip()).ToList();
            HasAnyColorTip = _ownedTipSet is { Count: > 0 }
                ? _ownedTipSet.Any(t => t.HasColor)
                : brush.Tip.HasColor;

            StrokeRandom = Hash01((int)(sample.X * 997), (int)(sample.Y * 991));
            State = new StrokeState(
                (float)sample.X, (float)sample.Y,
                (float)sample.Pressure, (float)sample.TiltX, (float)sample.TiltY);

            var sp = new StrokePoint(
                (float)sample.X, (float)sample.Y, (float)sample.Pressure,
                (float)sample.TiltX, (float)sample.TiltY, (float)sample.Twist,
                0, 0, 0, 0, 0, StrokeRandom);
            var initSizeMul = EvalParameter(ParamGraphLookup, BrushParameterTarget.Size, sp, brush.Dynamics.EvalSize(sp));
            var initSpacingMul = EvalParameter(ParamGraphLookup, BrushParameterTarget.Spacing, sp, brush.Dynamics.EvalSpacing(sp));
            var initSize = Math.Max(BrushSpacing.MinStampSizePx, (float)brush.Size * Math.Max(0.5f, initSizeMul));
            State.NextStampDistance = BrushSpacing.EffectiveDistance(
                brush, initSize, Math.Clamp(initSpacingMul, 0.05f, 4f), 0f);

            BaseMaskSize = Math.Max(1, Math.Min(512, (int)Math.Ceiling(brush.Size)));
            _deferMaskGeneration = UsesProceduralStampEvaluation(brush, TipFor(0), 0);
            if (!_deferMaskGeneration)
                _mask = TipFor(0).GenerateMask(BaseMaskSize, (float)brush.Hardness);
            _baseColor = ToSkColor(brush.Color);
            _currentColor = _baseColor;
            var initDensity = (float)brush.DensityOfPaint;
            CarriedColor = SKColors.Transparent;
            SmearFirstDabPending = brush.ColorMix && brush.SmudgeMode == SmudgeMode.Smear;
            Paint = new SKPaint
            {
                IsAntialias = true,
#pragma warning disable CS0618
                FilterQuality = brush.Quality == BrushQuality.High ? SKFilterQuality.High : SKFilterQuality.Low,
#pragma warning restore CS0618
                BlendMode = brush.BlendMode,
                Color = _baseColor,
                ColorFilter = SKColorFilter.CreateBlendMode(_baseColor, SKBlendMode.SrcIn)
            };
        }

        public float StrokeRandom { get; }
        public readonly Dictionary<BrushParameterTarget, BrushParameterGraph> ParamGraphLookup;
        public StrokeState State;
        public int BaseMaskSize { get; }
        private SKBitmap? _mask;
        private readonly bool _deferMaskGeneration;
        public SKBitmap Mask => _mask ??= TipFor(0).GenerateMask(BaseMaskSize, (float)_brush.Hardness);
        public SKPaint Paint { get; }
        public SKMatrix Matrix;
        public SKColor CarriedColor;
        public float LastSmearCenterX;
        public float LastSmearCenterY;
        public bool SmearFirstDabPending;
        public SKColor BaseColor => _baseColor;
        public bool HasAnyColorTip { get; }

        public bool Matches(BrushPreset brush)
            => ReferenceEquals(_brush, brush);

        public void UpdateColor(SKColor color)
        {
            if (_currentColor == color) return;
            _currentColor = color;
            _mixedFilter?.Dispose();
            _mixedFilter = SKColorFilter.CreateBlendMode(new SKColor(color.Red, color.Green, color.Blue, 255), SKBlendMode.SrcIn);
            Paint.ColorFilter = _mixedFilter;
        }

        public void UpdateOpacity(float opacity)
        {
            var colorAlphaRatio = _currentColor.Alpha / 255f;
            var alpha = (byte)Math.Clamp((int)(opacity * 255 * colorAlphaRatio), 0, 255);
            Paint.Color = new SKColor(_currentColor.Red, _currentColor.Green, _currentColor.Blue, alpha);
        }

        public void UpdateMatrix(StampSample stamp)
        {
            var scale = stamp.Size / Math.Max(1, BaseMaskSize);
            var thickness = Math.Clamp((float)_brush.TipThickness * stamp.TipThicknessMultiplier, 0.01f, 1f);
            var scaleX = scale;
            var scaleY = scale;
            if (_brush.TipDirection == BrushTipDirection.Horizontal)
                scaleY *= thickness;
            else
                scaleX *= thickness;
            if (_brush.FlipHorizontal) scaleX = -scaleX;
            if (_brush.FlipVertical) scaleY = -scaleY;
            Matrix = SKMatrix.CreateTranslation(-BaseMaskSize * 0.5f, -BaseMaskSize * 0.5f);
            Matrix = Matrix.PostConcat(SKMatrix.CreateScale(scaleX, scaleY));
            if (Math.Abs(stamp.Angle) > 0.001f)
                Matrix = Matrix.PostConcat(SKMatrix.CreateRotationDegrees(stamp.Angle));
            Matrix = Matrix.PostConcat(SKMatrix.CreateTranslation(stamp.X, stamp.Y));
        }

        public IBrushTip TipFor(int tipIndex)
        {
            if (_ownedTipSet is { Count: > 0 })
                return _ownedTipSet[Math.Clamp(tipIndex, 0, _ownedTipSet.Count - 1)];
            return _brush.Tip;
        }

        public int[] TipIndicesFor(int stampTipIndex)
        {
            if (_ownedTipSet is { Count: > 1 } && _brush.TipSelectionMode != BrushTipSelectionMode.Single)
                return _cachedTipIndices ??= Enumerable.Range(0, _ownedTipSet.Count).ToArray();
            return [stampTipIndex];
        }

        public bool IsImageTip(int tipIndex) => TipFor(tipIndex) is ImageBrushTip or NodeBrushTip { IsDirectImageSampler: true };

        public SKBitmap MaskFor(StampSample stamp)
            => MaskFor(stamp.TipIndex, stamp.Hardness);

        public SKBitmap MaskFor(int tipIndex, StampSample stamp)
            => MaskFor(tipIndex, stamp.Hardness);

        public SKBitmap MaskFor(float hardness)
            => MaskFor(0, hardness);

        private SKBitmap MaskFor(int tipIndex, float hardness)
        {
            tipIndex = _ownedTipSet is { Count: > 0 } ? Math.Clamp(tipIndex, 0, _ownedTipSet.Count - 1) : 0;
            var hardnessKey = QuantizeHardness(hardness);
            var key = (tipIndex, hardnessKey);
            if (_maskCache.TryGetValue(key, out var cached))
                return cached;

            var normalizedHardness = hardnessKey / 255f;
            var tip = TipFor(tipIndex);
            var tipMask = tipIndex == 0 && hardnessKey == QuantizeHardness((float)_brush.Hardness)
                ? Mask
                : tip.GenerateMask(BaseMaskSize, normalizedHardness);

            // All tips cache internally; the stroke never owns tip masks.

            if (_brush.Shape == null)
                return CacheOrReturnTemporary(key, tipMask);

            var shapeMask = _brush.Shape.GenerateMask(BaseMaskSize, normalizedHardness);
            var combined = MultiplyMasks(tipMask, shapeMask, BaseMaskSize);
            _ownedMasks.Add(combined);
            // shapeMask is ProceduralBrushTip-owned; tip holds it internally, never dispose
            DisposeTemporary(tipMask);
            return CacheOrReturnTemporary(key, combined);
        }

        public void ReleaseMask(SKBitmap mask)
        {
            if (_maskCache.ContainsValue(mask)) return;
            if (_ownedMasks.Remove(mask))
                mask.Dispose();
        }

        public bool TryGetCachedDab(StampSample stamp, out CachedDab dab)
        {
            dab = null!;

            var key = CachedDabKey.From(_brush, stamp);
            if (_dabCache.TryGetValue(key, out dab!))
                return true;

            var mask = MaskFor(key.TipIndex, key.Hardness / 255f);
            var layout = ComputeDabLayout(key);
            var bitmap = new SKBitmap(new SKImageInfo(layout.BitmapWidth, layout.BitmapHeight, SKColorType.Alpha8, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint
            {
                Color = SKColors.White,
                BlendMode = SKBlendMode.Src,
                IsAntialias = true,
#pragma warning disable CS0618
                FilterQuality = _brush.Quality == BrushQuality.High ? SKFilterQuality.High : SKFilterQuality.Low
#pragma warning restore CS0618
            })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(layout.BitmapWidth * 0.5f, layout.BitmapHeight * 0.5f);
                if (MathF.Abs(layout.AngleDegrees) > 0.001f)
                    canvas.RotateDegrees(layout.AngleDegrees);
                canvas.Scale(layout.RenderScaleX, layout.RenderScaleY);
                canvas.DrawBitmap(mask, -BaseMaskSize * 0.5f, -BaseMaskSize * 0.5f, paint);
            }

            dab = new CachedDab(bitmap, layout.OffsetX, layout.OffsetY, layout.LogicalWidth, layout.LogicalHeight);
            _dabCache[key] = dab;
            _dabCacheOrder.Enqueue(key);
            return true;
        }

        public bool TryGetCachedColorDab(StampSample stamp, out CachedColorDab dab)
        {
            dab = null!;

            var key = CachedDabKey.From(_brush, stamp);
            if (_colorDabCache.TryGetValue(key, out dab!))
                return true;

            var tip = TipFor(key.TipIndex);
            if (!tip.HasColor || tip.GenerateColorStamp(BaseMaskSize) is not { } colorStamp)
                return false;

            var layout = ComputeDabLayout(key);
            var bitmap = new SKBitmap(new SKImageInfo(layout.BitmapWidth, layout.BitmapHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint
            {
                BlendMode = SKBlendMode.Src,
                IsAntialias = true,
#pragma warning disable CS0618
                FilterQuality = _brush.Quality == BrushQuality.High ? SKFilterQuality.High : SKFilterQuality.Low
#pragma warning restore CS0618
            })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(layout.BitmapWidth * 0.5f, layout.BitmapHeight * 0.5f);
                if (MathF.Abs(layout.AngleDegrees) > 0.001f)
                    canvas.RotateDegrees(layout.AngleDegrees);
                canvas.Scale(layout.RenderScaleX, layout.RenderScaleY);
                canvas.DrawBitmap(colorStamp, -BaseMaskSize * 0.5f, -BaseMaskSize * 0.5f, paint);
            }

            dab = new CachedColorDab(bitmap, layout.OffsetX, layout.OffsetY, layout.LogicalWidth, layout.LogicalHeight);
            _colorDabCache[key] = dab;
            _colorDabCacheOrder.Enqueue(key);
            return true;
        }

        private readonly record struct DabLayout(
            int LogicalWidth,
            int LogicalHeight,
            int BitmapWidth,
            int BitmapHeight,
            float RenderScaleX,
            float RenderScaleY,
            int OffsetX,
            int OffsetY,
            float AngleDegrees);

        private DabLayout ComputeDabLayout(CachedDabKey key)
        {
            var baseSize = Math.Max(1, BaseMaskSize);
            var scale = key.Size / (float)baseSize;
            var thickness = key.Thickness / 256f;
            var scaleX = scale;
            var scaleY = scale;
            if (_brush.TipDirection == BrushTipDirection.Horizontal)
                scaleY *= thickness;
            else
                scaleX *= thickness;
            if (_brush.FlipHorizontal) scaleX = -scaleX;
            if (_brush.FlipVertical) scaleY = -scaleY;

            var halfW = baseSize * 0.5f * MathF.Abs(scaleX);
            var halfH = baseSize * 0.5f * MathF.Abs(scaleY);
            var angle = key.Angle;
            var radians = angle * MathF.PI / 180f;
            var cosA = MathF.Cos(radians);
            var sinA = MathF.Sin(radians);
            var boxHX = MathF.Abs(angle) > 0.001f
                ? halfW * MathF.Abs(cosA) + halfH * MathF.Abs(sinA)
                : halfW;
            var boxHY = MathF.Abs(angle) > 0.001f
                ? halfW * MathF.Abs(sinA) + halfH * MathF.Abs(cosA)
                : halfH;
            const float margin = 2.0f;
            var logicalW = Math.Max(1, (int)MathF.Ceiling(boxHX * 2f + margin * 2f));
            var logicalH = Math.Max(1, (int)MathF.Ceiling(boxHY * 2f + margin * 2f));

            var bitmapW = logicalW;
            var bitmapH = logicalH;
            if ((long)logicalW * logicalH > MaxCachedDabPixels)
            {
                var shrink = MathF.Sqrt(MaxCachedDabPixels / (float)((long)logicalW * logicalH));
                bitmapW = Math.Max(1, (int)MathF.Round(logicalW * shrink));
                bitmapH = Math.Max(1, (int)MathF.Round(logicalH * shrink));
            }

            var shrinkX = (float)bitmapW / logicalW;
            var shrinkY = (float)bitmapH / logicalH;
            return new DabLayout(
                logicalW,
                logicalH,
                bitmapW,
                bitmapH,
                scaleX * shrinkX,
                scaleY * shrinkY,
                -logicalW / 2,
                -logicalH / 2,
                angle);
        }

        internal void EnterDabCacheUse() { }

        internal void ExitDabCacheUse() { }

        internal void EnterColorDabCacheUse() { }

        internal void ExitColorDabCacheUse() { }

        internal void TrimDabCache() => TrimDabCacheCore();

        internal void TrimColorDabCache() => TrimColorDabCacheCore();

        private void TrimDabCacheCore()
        {
            while (_dabCache.Count > MaxCachedDabs && _dabCacheOrder.Count > 0)
            {
                var key = _dabCacheOrder.Dequeue();
                if (_dabCache.Remove(key, out var old))
                    old.Mask.Dispose();
            }
        }

        private void TrimColorDabCacheCore()
        {
            while (_colorDabCache.Count > MaxCachedColorDabs && _colorDabCacheOrder.Count > 0)
            {
                var key = _colorDabCacheOrder.Dequeue();
                if (_colorDabCache.Remove(key, out var old))
                    old.Bitmap.Dispose();
            }
        }

        private void DisposeTemporary(SKBitmap mask)
        {
            if (ReferenceEquals(mask, Mask)) return;
            if (_maskCache.ContainsValue(mask)) return;
            if (_ownedMasks.Remove(mask))
                mask.Dispose();
        }

        private SKBitmap CacheOrReturnTemporary((int TipIndex, int Hardness) key, SKBitmap mask)
        {
            if (_maskCache.Count >= MaxCachedMasks)
                return mask;

            _maskCache[key] = mask;
            return mask;
        }

        private static int QuantizeHardness(float hardness)
            => Math.Clamp((int)MathF.Round(Math.Clamp(hardness, 0.001f, 1f) * 255f), 0, 255);

        private static unsafe SKBitmap MultiplyMasks(SKBitmap tip, SKBitmap shape, int size)
        {
            var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));
            var a = (byte*)tip.GetPixels().ToPointer();
            var b = (byte*)shape.GetPixels().ToPointer();
            var dst = (byte*)bmp.GetPixels().ToPointer();
            var aStride = tip.RowBytes;
            var bStride = shape.RowBytes;
            var dStride = bmp.RowBytes;
            var tw = Math.Min(tip.Width, size);
            var th = Math.Min(tip.Height, size);
            var sw = Math.Min(shape.Width, size);
            var sh = Math.Min(shape.Height, size);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var ta = y < th && x < tw ? a[y * aStride + x] : (byte)0;
                    var sa = y < sh && x < sw ? b[y * bStride + x] : (byte)0;
                    dst[y * dStride + x] = (byte)(ta * sa / 255);
                }
            return bmp;
        }

        public void Dispose()
        {
            foreach (var mask in _maskCache.Values)
                if (_ownedMasks.Remove(mask))
                    mask.Dispose();
            foreach (var mask in _ownedMasks)
                mask.Dispose();
            foreach (var dab in _dabCache.Values)
                dab.Mask.Dispose();
            foreach (var dab in _colorDabCache.Values)
                dab.Bitmap.Dispose();
            if (_ownedTipSet != null)
            {
                foreach (var tip in _ownedTipSet)
                    if (tip is IDisposable disposable)
                        disposable.Dispose();
                _ownedTipSet.Clear();
            }
            _maskCache.Clear();
            _dabCache.Clear();
            _dabCacheOrder.Clear();
            _colorDabCache.Clear();
            _colorDabCacheOrder.Clear();
            _ownedMasks.Clear();
            Paint.Dispose();
        }

        private static SKColor ToSkColor(Color c) => new(c.R, c.G, c.B, c.A);

        public sealed class CachedDab(SKBitmap mask, int offsetX, int offsetY, int logicalWidth, int logicalHeight)
        {
            public SKBitmap Mask { get; } = mask;
            public int OffsetX { get; } = offsetX;
            public int OffsetY { get; } = offsetY;
            public int LogicalWidth { get; } = logicalWidth;
            public int LogicalHeight { get; } = logicalHeight;
            public float MaskScaleX => (float)LogicalWidth / Mask.Width;
            public float MaskScaleY => (float)LogicalHeight / Mask.Height;
            public bool IsScaled => LogicalWidth != Mask.Width || LogicalHeight != Mask.Height;
        }

        public sealed class CachedColorDab(SKBitmap bitmap, int offsetX, int offsetY, int logicalWidth, int logicalHeight)
        {
            public SKBitmap Bitmap { get; } = bitmap;
            public int OffsetX { get; } = offsetX;
            public int OffsetY { get; } = offsetY;
            public int LogicalWidth { get; } = logicalWidth;
            public int LogicalHeight { get; } = logicalHeight;
            public float MaskScaleX => (float)LogicalWidth / Bitmap.Width;
            public float MaskScaleY => (float)LogicalHeight / Bitmap.Height;
            public bool IsScaled => LogicalWidth != Bitmap.Width || LogicalHeight != Bitmap.Height;
        }

        private readonly record struct CachedDabKey(int Size, int Hardness, int Thickness, int Angle, int FlipBits, int TipIndex)
        {
            public static CachedDabKey From(BrushPreset brush, StampSample stamp)
            {
                var size = Math.Clamp((int)MathF.Round(stamp.Size), 1, 4096);
                var hardness = Math.Clamp((int)MathF.Round(Math.Clamp(stamp.Hardness, 0.001f, 1f) * 255f), 0, 255);
                var thickness = Math.Clamp((int)MathF.Round(Math.Clamp((float)brush.TipThickness * stamp.TipThicknessMultiplier, 0.01f, 4f) * 256f), 3, 1024);
                var angle = QuantizeAngle(stamp.Angle, brush);
                var flipBits = (brush.FlipHorizontal ? 1 : 0) | (brush.FlipVertical ? 2 : 0);
                return new CachedDabKey(size, hardness, thickness, angle, flipBits, Math.Max(0, stamp.TipIndex));
            }

            private static int QuantizeAngle(float angle, BrushPreset brush)
            {
                var tip = brush.Tip as ProceduralBrushTip;
                var isCircle = tip?.Shape is BrushTipShape.Circle or BrushTipShape.SoftRound;
                if (isCircle && Math.Abs(brush.TipThickness - 1.0) < 0.001)
                    return 0;

                var normalized = angle % 360f;
                if (normalized < 0) normalized += 360f;
                return Math.Clamp((int)MathF.Round(normalized / 2f) * 2, 0, 358);
            }
        }
    }
}
