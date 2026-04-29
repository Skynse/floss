using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Document;

namespace Floss.App.Canvas;

public sealed class LayerCompositor
{
    private WriteableBitmap? _composited;
    private int _width;
    private int _height;
    private bool _dirty = true;

    public void SetSize(int width, int height)
    {
        if (_width == width && _height == height && _composited != null) return;
        _width = width;
        _height = height;
        _composited?.Dispose();
        _composited = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        _dirty = true;
    }

    public WriteableBitmap Bitmap
    {
        get
        {
            if (_composited == null)
                SetSize(2048, 2048);
            return _composited!;
        }
    }

    public void Invalidate() => _dirty = true;

    public unsafe void Composite(IReadOnlyList<DrawingLayer> layers, int width, int height)
    {
        SetSize(width, height);
        if (!_dirty) return;
        _dirty = false;

        using var frame = _composited!.Lock();
        var dst = (byte*)frame.Address;
        var stride = frame.RowBytes;

        FillWithPaperColor(dst, stride, width, height);

        // Only process root-level layers; group children are composited inside CompositeGroup.
        // Filtering by Parent==null avoids double-rendering when the flat document list
        // contains both a group and its children (as happens after PSD import).
        var rootLayers = new System.Collections.Generic.List<DrawingLayer>(layers.Count);
        foreach (var l in layers)
            if (l.Parent == null) rootLayers.Add(l);

        var renderList = FlattenForRender(rootLayers);

        // Composite each layer
        for (int i = 0; i < renderList.Count; i++)
        {
            var item = renderList[i];
            if (!item.Layer.IsVisible) continue;

            if (item.Layer.IsGroup)
            {
                // Composite group children into temp buffer, then composite group
                CompositeGroup(dst, stride, width, height, item.Layer);
            }
            else if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                // This layer clips to the base layer
                var baseLayer = renderList[item.BaseLayerIndex].Layer;
                CompositeClippedLayer(dst, stride, width, height, item.Layer, baseLayer);
            }
            else
            {
                CompositeLayer(dst, stride, width, height, item.Layer);
            }
        }
    }

    private static unsafe void FillWithPaperColor(byte* dst, int stride, int width, int height)
    {
        var paper = DrawingDocument.PaperColor;
        for (int y = 0; y < height; y++)
        {
            var row = dst + y * stride;
            for (int x = 0; x < width; x++)
            {
                row[x * 4 + 0] = paper.B;
                row[x * 4 + 1] = paper.G;
                row[x * 4 + 2] = paper.R;
                row[x * 4 + 3] = 255;
            }
        }
    }

    private record RenderItem(DrawingLayer Layer, bool IsClipped, int BaseLayerIndex);

    private static List<RenderItem> FlattenForRender(IReadOnlyList<DrawingLayer> layers)
    {
        var result = new List<RenderItem>();
        int? lastNonClippingIndex = null;

        // Build bottom-to-top render list with clipping info
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.IsClipping && lastNonClippingIndex.HasValue)
            {
                result.Add(new RenderItem(layer, true, lastNonClippingIndex.Value));
            }
            else
            {
                result.Add(new RenderItem(layer, false, -1));
                if (!layer.IsClipping)
                    lastNonClippingIndex = result.Count - 1;
            }
        }

        return result;
    }

    private static unsafe void CompositeGroup(byte* dst, int dstStride, int width, int height, DrawingLayer group)
    {
        if (group.Children.Count == 0) return;

        // Create temp buffer for group contents
        var temp = new byte[width * height * 4];
        fixed (byte* tempPtr = temp)
        {
            // Initialize temp with paper color (or transparent?)
            // Groups in Photoshop composite their children onto transparent,
            // then the group blend mode composites that onto the canvas
            for (int i = 0; i < width * height * 4; i++) tempPtr[i] = 0;

            // Composite children bottom-to-top
            var childrenList = FlattenForRender(group.Children);
            for (int i = 0; i < childrenList.Count; i++)
            {
                var item = childrenList[i];
                if (!item.Layer.IsVisible) continue;
                if (item.IsClipped && item.BaseLayerIndex >= 0)
                {
                    CompositeClippedLayer(tempPtr, width * 4, width, height, item.Layer, childrenList[item.BaseLayerIndex].Layer);
                }
                else
                {
                    CompositeLayer(tempPtr, width * 4, width, height, item.Layer);
                }
            }

            // Now composite the group result onto the main canvas
            var groupOpacity = group.Opacity;
            if (groupOpacity <= 0) return;

            for (int y = 0; y < height; y++)
            {
                var srcRow = tempPtr + y * width * 4;
                var dstRow = dst + y * dstStride;

                for (int x = 0; x < width; x++)
                {
                    var idx = x * 4;
                    var srcB = srcRow[idx + 0] / 255.0;
                    var srcG = srcRow[idx + 1] / 255.0;
                    var srcR = srcRow[idx + 2] / 255.0;
                    var srcA = srcRow[idx + 3] / 255.0 * groupOpacity;

                    if (srcA <= 0) continue;

                    var dstB = dstRow[idx + 0] / 255.0;
                    var dstG = dstRow[idx + 1] / 255.0;
                    var dstR = dstRow[idx + 2] / 255.0;
                    var dstA = dstRow[idx + 3] / 255.0;

                    var (blendR, blendG, blendB) = ApplyBlendMode(
                        srcR, srcG, srcB, srcA,
                        dstR, dstG, dstB, dstA,
                        group.BlendMode);

                    var outAlpha = srcA + dstA * (1.0 - srcA);
                    if (outAlpha > 0)
                    {
                        dstRow[idx + 0] = (byte)Math.Clamp((blendB * srcA + dstB * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                        dstRow[idx + 1] = (byte)Math.Clamp((blendG * srcA + dstG * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                        dstRow[idx + 2] = (byte)Math.Clamp((blendR * srcA + dstR * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                        dstRow[idx + 3] = (byte)Math.Clamp(outAlpha * 255, 0, 255);
                    }
                }
            }
        }
    }

    private static unsafe void CompositeClippedLayer(byte* dst, int dstStride, int width, int height, DrawingLayer layer, DrawingLayer baseLayer)
    {
        var opacity = layer.Opacity;
        if (opacity <= 0) return;

        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;
        var baseOffsetX = baseLayer.OffsetX;
        var baseOffsetY = baseLayer.OffsetY;

        using var frame = layer.Bitmap.Lock();
        var src = (byte*)frame.Address;
        var srcStride = frame.RowBytes;

        using var baseFrame = baseLayer.Bitmap.Lock();
        var basePtr = (byte*)baseFrame.Address;
        var baseStride = baseFrame.RowBytes;
        var baseW = baseLayer.Bitmap.PixelSize.Width;
        var baseH = baseLayer.Bitmap.PixelSize.Height;

        var srcLeft = Math.Max(0, -offsetX);
        var srcTop = Math.Max(0, -offsetY);
        var srcRight = Math.Min(layer.Bitmap.PixelSize.Width, width - offsetX);
        var srcBottom = Math.Min(layer.Bitmap.PixelSize.Height, height - offsetY);

        if (srcLeft >= srcRight || srcTop >= srcBottom) return;

        for (int y = srcTop; y < srcBottom; y++)
        {
            var srcRow = src + y * srcStride;
            var dstRow = dst + (y + offsetY) * dstStride;

            for (int x = srcLeft; x < srcRight; x++)
            {
                var srcIdx = x * 4;
                var dstIdx = (x + offsetX) * 4;

                var srcB = srcRow[srcIdx + 0] / 255.0;
                var srcG = srcRow[srcIdx + 1] / 255.0;
                var srcR = srcRow[srcIdx + 2] / 255.0;
                var srcA = srcRow[srcIdx + 3] / 255.0 * opacity;

                if (srcA <= 0) continue;

                var baseX = x + offsetX - baseOffsetX;
                var baseY = y + offsetY - baseOffsetY;
                double baseAlpha = 0;
                if (baseX >= 0 && baseX < baseW && baseY >= 0 && baseY < baseH)
                {
                    var baseRow = basePtr + baseY * baseStride;
                    baseAlpha = baseRow[baseX * 4 + 3] / 255.0;
                }

                srcA *= baseAlpha;
                if (srcA <= 0) continue;

                var dstB = dstRow[dstIdx + 0] / 255.0;
                var dstG = dstRow[dstIdx + 1] / 255.0;
                var dstR = dstRow[dstIdx + 2] / 255.0;
                var dstA = dstRow[dstIdx + 3] / 255.0;

                var (blendR, blendG, blendB) = ApplyBlendMode(
                    srcR, srcG, srcB, srcA,
                    dstR, dstG, dstB, dstA,
                    layer.BlendMode);

                var outAlpha = srcA + dstA * (1.0 - srcA);
                if (outAlpha > 0)
                {
                    dstRow[dstIdx + 0] = (byte)Math.Clamp((blendB * srcA + dstB * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                    dstRow[dstIdx + 1] = (byte)Math.Clamp((blendG * srcA + dstG * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                    dstRow[dstIdx + 2] = (byte)Math.Clamp((blendR * srcA + dstR * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                    dstRow[dstIdx + 3] = (byte)Math.Clamp(outAlpha * 255, 0, 255);
                }
            }
        }
    }

    private static unsafe void CompositeLayer(byte* dst, int dstStride, int width, int height, DrawingLayer layer)
    {
        using var frame = layer.Bitmap.Lock();
        var src = (byte*)frame.Address;
        var srcStride = frame.RowBytes;

        var opacity = layer.Opacity;
        if (opacity <= 0) return;

        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;

        var srcLeft = Math.Max(0, -offsetX);
        var srcTop = Math.Max(0, -offsetY);
        var srcRight = Math.Min(layer.Bitmap.PixelSize.Width, width - offsetX);
        var srcBottom = Math.Min(layer.Bitmap.PixelSize.Height, height - offsetY);

        if (srcLeft >= srcRight || srcTop >= srcBottom) return;

        for (int y = srcTop; y < srcBottom; y++)
        {
            var srcRow = src + y * srcStride;
            var dstRow = dst + (y + offsetY) * dstStride;

            for (int x = srcLeft; x < srcRight; x++)
            {
                var srcIdx = x * 4;
                var dstIdx = (x + offsetX) * 4;

                var srcB = srcRow[srcIdx + 0] / 255.0;
                var srcG = srcRow[srcIdx + 1] / 255.0;
                var srcR = srcRow[srcIdx + 2] / 255.0;
                var srcA = srcRow[srcIdx + 3] / 255.0 * opacity;

                if (srcA <= 0) continue;

                var dstB = dstRow[dstIdx + 0] / 255.0;
                var dstG = dstRow[dstIdx + 1] / 255.0;
                var dstR = dstRow[dstIdx + 2] / 255.0;
                var dstA = dstRow[dstIdx + 3] / 255.0;

                var (blendR, blendG, blendB) = ApplyBlendMode(
                    srcR, srcG, srcB, srcA,
                    dstR, dstG, dstB, dstA,
                    layer.BlendMode);

                var outAlpha = srcA + dstA * (1.0 - srcA);
                if (outAlpha > 0)
                {
                    dstRow[dstIdx + 0] = (byte)Math.Clamp((blendB * srcA + dstB * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                    dstRow[dstIdx + 1] = (byte)Math.Clamp((blendG * srcA + dstG * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                    dstRow[dstIdx + 2] = (byte)Math.Clamp((blendR * srcA + dstR * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
                    dstRow[dstIdx + 3] = (byte)Math.Clamp(outAlpha * 255, 0, 255);
                }
            }
        }
    }

    private static (double r, double g, double b) ApplyBlendMode(
        double srcR, double srcG, double srcB, double srcA,
        double dstR, double dstG, double dstB, double dstA,
        string blendMode)
    {
        return blendMode switch
        {
            "Normal" or "PassThrough" => (srcR, srcG, srcB),
            "Dissolve" => (srcR, srcG, srcB),
            "Multiply" => (dstR * srcR, dstG * srcG, dstB * srcB),
            "Screen" => (1.0 - (1.0 - dstR) * (1.0 - srcR),
                        1.0 - (1.0 - dstG) * (1.0 - srcG),
                        1.0 - (1.0 - dstB) * (1.0 - srcB)),
            "Overlay" => (Overlay(dstR, srcR), Overlay(dstG, srcG), Overlay(dstB, srcB)),
            "SoftLight" => (SoftLight(dstR, srcR), SoftLight(dstG, srcG), SoftLight(dstB, srcB)),
            "HardLight" => (HardLight(dstR, srcR), HardLight(dstG, srcG), HardLight(dstB, srcB)),
            "ColorDodge" => (ColorDodge(dstR, srcR), ColorDodge(dstG, srcG), ColorDodge(dstB, srcB)),
            "ColorBurn" => (ColorBurn(dstR, srcR), ColorBurn(dstG, srcG), ColorBurn(dstB, srcB)),
            "Darken" => (Math.Min(dstR, srcR), Math.Min(dstG, srcG), Math.Min(dstB, srcB)),
            "Lighten" => (Math.Max(dstR, srcR), Math.Max(dstG, srcG), Math.Max(dstB, srcB)),
            "Difference" => (Math.Abs(dstR - srcR), Math.Abs(dstG - srcG), Math.Abs(dstB - srcB)),
            "Exclusion" => (dstR + srcR - 2.0 * dstR * srcR,
                           dstG + srcG - 2.0 * dstG * srcG,
                           dstB + srcB - 2.0 * dstB * srcB),
            "LinearBurn" => (dstR + srcR - 1.0, dstG + srcG - 1.0, dstB + srcB - 1.0),
            "LinearDodge" => (dstR + srcR, dstG + srcG, dstB + srcB),
            "VividLight" => (VividLight(dstR, srcR), VividLight(dstG, srcG), VividLight(dstB, srcB)),
            "LinearLight" => (LinearLight(dstR, srcR), LinearLight(dstG, srcG), LinearLight(dstB, srcB)),
            "PinLight" => (PinLight(dstR, srcR), PinLight(dstG, srcG), PinLight(dstB, srcB)),
            "HardMix" => (HardMix(dstR, srcR), HardMix(dstG, srcG), HardMix(dstB, srcB)),
            "DarkerColor" => LuminosityBlend(dstR, dstG, dstB, srcR, srcG, srcB, useDarker: true),
            "LighterColor" => LuminosityBlend(dstR, dstG, dstB, srcR, srcG, srcB, useDarker: false),
            "Subtract" => (dstR - srcR, dstG - srcG, dstB - srcB),
            "Divide" => (SafeDivide(dstR, srcR), SafeDivide(dstG, srcG), SafeDivide(dstB, srcB)),
            "Hue" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 0),
            "Saturation" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 1),
            "Color" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 2),
            "Luminosity" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 3),
            _ => (srcR, srcG, srcB)
        };
    }

    private static double Overlay(double dst, double src)
    {
        if (dst < 0.5)
            return 2.0 * dst * src;
        else
            return 1.0 - 2.0 * (1.0 - dst) * (1.0 - src);
    }

    private static double SoftLight(double dst, double src)
    {
        if (src < 0.5)
            return dst - (1.0 - 2.0 * src) * dst * (1.0 - dst);
        else
        {
            double d = dst < 0.25 ? ((16.0 * dst - 12.0) * dst + 4.0) * dst : Math.Sqrt(dst);
            return dst + (2.0 * src - 1.0) * (d - dst);
        }
    }

    private static double HardLight(double dst, double src)
    {
        if (src < 0.5)
            return 2.0 * dst * src;
        else
            return 1.0 - 2.0 * (1.0 - dst) * (1.0 - src);
    }

    private static double ColorDodge(double dst, double src)
    {
        if (dst == 0.0) return 0.0;
        if (src == 1.0) return 1.0;
        return Math.Min(1.0, dst / (1.0 - src));
    }

    private static double ColorBurn(double dst, double src)
    {
        if (dst == 1.0) return 1.0;
        if (src == 0.0) return 0.0;
        return 1.0 - Math.Min(1.0, (1.0 - dst) / src);
    }

    private static double LinearLight(double dst, double src)
    {
        if (src < 0.5)
            return dst + 2.0 * src - 1.0;
        else
            return dst + 2.0 * (src - 0.5);
    }

    private static double VividLight(double dst, double src)
    {
        if (src < 0.5)
            return ColorBurn(dst, 2.0 * src);
        else
            return ColorDodge(dst, 2.0 * (src - 0.5));
    }

    private static double PinLight(double dst, double src)
    {
        if (src < 0.5)
            return Math.Min(dst, 2.0 * src);
        else
            return Math.Max(dst, 2.0 * (src - 0.5));
    }

    private static double HardMix(double dst, double src)
    {
        return VividLight(dst, src) < 0.5 ? 0.0 : 1.0;
    }

    private static double SafeDivide(double dst, double src)
    {
        if (src == 0.0) return 0.0;
        return Math.Min(1.0, dst / src);
    }

    private static (double r, double g, double b) LuminosityBlend(
        double dstR, double dstG, double dstB,
        double srcR, double srcG, double srcB,
        bool useDarker)
    {
        var dstLum = RgbToLuma(dstR, dstG, dstB);
        var srcLum = RgbToLuma(srcR, srcG, srcB);
        var cmp = useDarker ? srcLum < dstLum : srcLum > dstLum;
        return cmp ? (srcR, srcG, srcB) : (dstR, dstG, dstB);
    }

    private static double RgbToLuma(double r, double g, double b)
    {
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static (double r, double g, double b) HslBlend(
        double dstR, double dstG, double dstB,
        double srcR, double srcG, double srcB,
        int mode)
    {
        var (dstH, dstS, dstL) = RgbToHsl(dstR, dstG, dstB);
        var (srcH, srcS, srcL) = RgbToHsl(srcR, srcG, srcB);

        double outH, outS, outL;

        switch (mode)
        {
            case 0:
                outH = srcS == 0 ? dstH : srcH;
                outS = dstS;
                outL = dstL;
                break;
            case 1:
                outH = dstH;
                outS = srcS;
                outL = dstL;
                break;
            case 2:
                outH = srcH;
                outS = srcS;
                outL = dstL;
                break;
            default:
                outH = dstH;
                outS = dstS;
                outL = srcL;
                break;
        }

        return HslToRgb(outH, outS, outL);
    }

    private static (double h, double s, double l) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;

        if (max == min)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        else if (max == g)
            h = (b - r) / d + 2.0;
        else
            h = (r - g) / d + 4.0;
        h /= 6.0;

        return (h, s, l);
    }

    private static (double r, double g, double b) HslToRgb(double h, double s, double l)
    {
        if (s == 0)
            return (l, l, l);

        double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        var q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        var p = 2.0 * l - q;

        return (
            HueToRgb(p, q, h + 1.0 / 3.0),
            HueToRgb(p, q, h),
            HueToRgb(p, q, h - 1.0 / 3.0)
        );
    }
}