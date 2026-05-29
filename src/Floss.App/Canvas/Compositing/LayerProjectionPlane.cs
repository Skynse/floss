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

            if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                var baseLayer = stack[item.BaseLayerIndex].Layer;
                if (!baseLayer.IsVisible) continue;
                if (item.Layer.IsGroup)
                    _host.CompositeClippedGroupIntoBuffer(dst, dstStride, width, height,
                        item.Layer, baseLayer, opacityScale, clip, originX, originY);
                else if (item.Layer.Adjustment != null)
                    AdjustmentLayerProcessor.ApplyClipped(dst, dstStride, width, height,
                        item.Layer.Adjustment, item.Layer.Opacity * opacityScale,
                        baseLayer, clip, originX, originY);
                else
                    _host.CompositeClippedPaintLayer(dst, dstStride, width, height,
                        item.Layer, baseLayer, opacityScale, clip, originX, originY);
            }
            else if (item.Layer.IsGroup)
            {
                CompositeGroupNode(dst, dstStride, width, height, item.Layer,
                    opacityScale, clip, originX, originY);
            }
            else if (item.Layer.Adjustment != null)
            {
                AdjustmentLayerProcessor.Apply(dst, dstStride, width, height,
                    item.Layer.Adjustment, item.Layer.Opacity * opacityScale,
                    clip, originX, originY);
            }
            else
            {
                _host.CompositePaintLayer(dst, dstStride, width, height,
                    item.Layer, opacityScale, clip, originX, originY);
            }
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
                result.Add(new ProjectionSiblingItem(layer, true, lastNonClipping.Value));
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
        if (group.BlendMode == "PassThrough")
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

internal readonly record struct ProjectionSiblingItem(DrawingLayer Layer, bool IsClipped, int BaseLayerIndex);
