using System;
using System.Buffers;
using System.Collections.Generic;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// Drawpile-style layer merge: per-tile recursion into groups, no cached
/// projection buffers. Groups are flattened into a temp buffer, then merged
/// with the group's blend mode and opacity (Drawpile's "isolated" path).
/// Pass-through groups recurse with accumulated opacity directly.
/// </summary>
internal sealed class LayerProjectionPlane
{
    private readonly ILayerMergeHost _host;

    public LayerProjectionPlane(ILayerMergeHost host) => _host = host;
    public void SetSize(int width, int height) { }

    public void InvalidateGroupCaches(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex,
        bool fullGroupInvalidation = false) { }
    public void InvalidateGroupCaches(PixelRegion? region, DrawingLayer? changedLayer,
        bool fullGroupInvalidation = false) { }

    public unsafe void CompositeSiblingList(
        byte* dst, int dstStride, int width, int height,
        IReadOnlyList<DrawingLayer> siblings, double opacityScale,
        PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0) return;
        var stack = BuildSiblingStack(siblings);
        CompositeSiblingStack(dst, dstStride, width, height, stack, opacityScale, clip, originX, originY);
    }

    public unsafe void CompositeSiblingStack(
        byte* dst, int dstStride, int width, int height,
        IReadOnlyList<ProjectionSiblingItem> stack, double opacityScale,
        PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0) return;

        for (var i = 0; i < stack.Count; i++)
        {
            var item = stack[i];
            if (!item.Layer.IsVisible) continue;

            if (item.HasClippingChildren) continue;

            if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                var baseLayer = stack[item.BaseLayerIndex].Layer;
                if (!baseLayer.IsVisible) continue;

                // Drawpile DP_layer_list_flatten_clipping_tile_to:
                // 1. Composite base into temp buffer at full opacity
                // 2. Composite all clip layers into temp with alpha-preserving blend
                // 3. Merge temp onto dst with base layer's blend/opacity
                var tempLen = clip.Width * clip.Height * 4;
                var temp = ArrayPool<byte>.Shared.Rent(tempLen);
                try
                {
                    Array.Clear(temp, 0, tempLen);
                    fixed (byte* tp = temp)
                    {
                        // Drawpile: base must be composited into temp with Normal
                        // blend, 100% opacity — its real blend/opacity are deferred
                        // to the final merge of the entire clip group.
                        _host.CompositePaintLayer(tp, clip.Width * 4, width, height,
                            baseLayer, 1.0, clip, originX, originY,
                            blendModeOverride: BlendMode.Normal, opacityOverride: 1.0);

                        // Process this clip item and all consecutive clip items
                        // with the same base layer
                        var end = i;
                        while (end + 1 < stack.Count
                               && stack[end + 1].IsClipped
                               && stack[end + 1].BaseLayerIndex == item.BaseLayerIndex)
                            end++;

                        for (var j = i; j <= end; j++)
                        {
                            var clipItem = stack[j];
                            if (!clipItem.Layer.IsVisible) continue;
                            if (clipItem.Layer.IsGroup)
                                // Groups as clip layers: composite children into temp
                                // with alpha-preserving blend
                                CompositeGroupIntoBufferAlphaPreserving(tp, clip.Width * 4,
                                    clip.Width, clip.Height, clipItem.Layer,
                                    clip, originX, originY);
                            else if (clipItem.Layer.Adjustment != null)
                                AdjustmentLayerProcessor.ApplyWithLayer(
                                    tp, clip.Width * 4, clip.Width, clip.Height,
                                    clipItem.Layer,
                                    clipItem.Layer.Opacity, clip, originX, originY);
                            else
                                LayerCompositorPixelOps.CompositeLayerAlphaPreserving(
                                    tp, clip.Width * 4, clip.Width, clip.Height,
                                    clipItem.Layer, 1.0, clip, originX, originY);
                        }

                        // Step 3: merge temp onto dst with base layer blend + opacity
                        var baseBlend = baseLayer.BlendMode;
                        var baseOp = baseLayer.Opacity * opacityScale;
                        LayerCompositorPixelOps.CompositeBgraBuffer(
                            dst, dstStride, tp, clip.Width * 4,
                            baseBlend, baseOp, clip, originX, originY);
                        i = end;
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(temp); }
            }
            else if (item.Layer.IsGroup)
            {
                CompositeGroupNode(dst, dstStride, width, height, item.Layer,
                    opacityScale, clip, originX, originY);
            }
            else if (item.Layer.Adjustment != null)
            {
                AdjustmentLayerProcessor.ApplyWithLayer(dst, dstStride, width, height,
                    item.Layer, item.Layer.Opacity * opacityScale,
                    clip, originX, originY);
            }
            else
            {
                _host.CompositePaintLayer(dst, dstStride, width, height,
                    item.Layer, opacityScale, clip, originX, originY);
            }
        }
    }

    /// <summary>
    /// Composite group children into a buffer using alpha-preserving blend.
    /// Matches Drawpile: group children as clipping layers flatten into the
    /// base buffer without modifying its alpha.
    /// </summary>
    private unsafe void CompositeGroupIntoBufferAlphaPreserving(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer group, PixelRegion clip, int originX, int originY)
    {
        if (group.Children.Count == 0) return;
        if (group.BlendMode == BlendMode.PassThrough)
        {
            var stack = BuildSiblingStack(group.Children);
            for (var i = 0; i < stack.Count; i++)
            {
                var item = stack[i];
                if (!item.Layer.IsVisible) continue;
                if (item.Layer.IsGroup)
                    CompositeGroupIntoBufferAlphaPreserving(
                        dst, dstStride, width, height,
                        item.Layer, clip, originX, originY);
                else if (item.Layer.Adjustment != null)
                    AdjustmentLayerProcessor.ApplyWithLayer(dst, dstStride, width, height,
                        item.Layer, item.Layer.Opacity,
                        clip, originX, originY);
                else
                    LayerCompositorPixelOps.CompositeLayerAlphaPreserving(
                        dst, dstStride, width, height,
                        item.Layer, 1.0, clip, originX, originY);
            }
        }
        else
        {
            // Isolated clip group: flatten children into temp at full opacity,
            // then merge onto dst using alpha-preserving blend (Drawpile:
            // DP_transient_tile_merge with DP_blend_mode_clip → alpha-preserving).
            var tempLen = clip.Width * clip.Height * 4;
            var temp = ArrayPool<byte>.Shared.Rent(tempLen);
            try
            {
                Array.Clear(temp, 0, tempLen);
                fixed (byte* tp = temp)
                {
                    var stack = BuildSiblingStack(group.Children);
                    for (var i = 0; i < stack.Count; i++)
                    {
                        var item = stack[i];
                        if (!item.Layer.IsVisible) continue;
                        if (item.Layer.IsGroup)
                            CompositeGroupNode(tp, clip.Width * 4, clip.Width, clip.Height,
                                item.Layer, 1.0, clip, clip.X, clip.Y);
                        else if (item.Layer.Adjustment != null)
                            AdjustmentLayerProcessor.ApplyWithLayer(tp, clip.Width * 4, clip.Width, clip.Height,
                                item.Layer, item.Layer.Opacity,
                                clip, clip.X, clip.Y);
                        else
                            _host.CompositePaintLayer(tp, clip.Width * 4, clip.Width, clip.Height,
                                item.Layer, 1.0, clip, clip.X, clip.Y);
                    }
                    // Merge onto dst using group's alpha-preserving blend mode
                    LayerCompositorPixelOps.CompositeBgraBufferAlphaPreserving(
                        dst, dstStride, tp, clip.Width * 4,
                        group.BlendMode, group.Opacity, clip, originX, originY);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(temp); }
        }
    }

    public static List<ProjectionSiblingItem> BuildSiblingStack(IReadOnlyList<DrawingLayer> siblings)
    {
        var result = new List<ProjectionSiblingItem>(siblings.Count);
        int? lastNonClipping = null;
        for (var i = 0; i < siblings.Count; i++)
        {
            var layer = siblings[i];
            if (layer.IsClipping && lastNonClipping.HasValue)
            {
                var baseIdx = lastNonClipping.Value;
                result[baseIdx] = result[baseIdx] with { HasClippingChildren = true };
                result.Add(new ProjectionSiblingItem(layer, true, baseIdx));
            }
            else
            {
                result.Add(new ProjectionSiblingItem(layer, false, -1));
                if (!layer.IsClipping) lastNonClipping = result.Count - 1;
            }
        }
        return result;
    }

    /// <summary>
    /// Drawpile DP_layer_group_flatten_tile_to: pass-through groups recurse
    /// directly with accumulated opacity. Normal groups (isolated) flatten
    /// children into a temp buffer at full opacity, then merge the temp onto
    /// destination with the group's blend mode and opacity.
    /// No caching — recomputed per tile.
    /// </summary>
    public unsafe void CompositeGroupNode(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer group, double opacityScale,
        PixelRegion clip, int originX, int originY)
    {
        if (group.Children.Count == 0) return;
        var groupOpacity = group.Opacity * opacityScale;
        if (groupOpacity <= 0) return;

        // Drawpile: pass-through → recurse with accumulated opacity
        if (group.BlendMode == BlendMode.PassThrough)
        {
            CompositeSiblingList(dst, dstStride, width, height,
                group.Children, groupOpacity, clip, originX, originY);
            return;
        }

        // Drawpile isolated group: flatten children at full opacity into
        // a temp buffer, then merge onto dst with group blend + opacity.
        var tempLen = clip.Width * clip.Height * 4;
        var temp = ArrayPool<byte>.Shared.Rent(tempLen);
        Array.Clear(temp, 0, tempLen);
        fixed (byte* tp = temp)
        {
            CompositeSiblingList(tp, clip.Width * 4, clip.Width, clip.Height,
                group.Children, 1.0, clip, clip.X, clip.Y);
            LayerCompositorPixelOps.CompositeBgraBuffer(dst, dstStride,
                tp, clip.Width * 4, group.BlendMode, groupOpacity, clip, originX, originY);
        }
        ArrayPool<byte>.Shared.Return(temp);
    }
}

internal readonly record struct ProjectionSiblingItem(DrawingLayer Layer, bool IsClipped, int BaseLayerIndex, bool HasClippingChildren = false);
