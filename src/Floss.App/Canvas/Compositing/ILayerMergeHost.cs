using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// Pixel-level apply/recalculate hooks used by <see cref="LayerProjectionPlane"/>
/// (Krita: KisAbstractProjectionPlane::apply / layer merge into parent buffer).
/// </summary>
internal interface ILayerMergeHost
{
    unsafe void CompositePaintLayer(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer layer, double opacityScale, PixelRegion clip, int originX, int originY,
        BlendMode? blendModeOverride = null, double? opacityOverride = null);

    unsafe void CompositeProjectionBuffer(
        byte* dst, int dstStride, TiledPixelBuffer projection,
        BlendMode blendMode, double opacity, PixelRegion clip, int originX, int originY);

    unsafe void CompositeClippedPaintLayer(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer layer, DrawingLayer baseLayer, double opacityScale,
        PixelRegion clip, int originX, int originY);

    unsafe void CompositeClippedGroupIntoBuffer(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer group, DrawingLayer baseLayer, double opacityScale,
        PixelRegion clip, int originX, int originY);

    unsafe void CompositeAlphaPreservingPaintLayer(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer layer, double opacityScale, PixelRegion clip, int originX, int originY);
}
