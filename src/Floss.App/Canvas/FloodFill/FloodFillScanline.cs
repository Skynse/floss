using System;
using System.Collections.Generic;

namespace Floss.App.Canvas.FloodFill;

/// <summary>Scanline seed-fill — O(filled region), shared by wand and bucket fill.</summary>
public static class FloodFillScanline
{
    public static void FillContiguous(
        int width,
        int height,
        int startX,
        int startY,
        Func<int, int, bool> similar,
        int[] visitStamp,
        int visitEpoch,
        Action<int, int> onPixel)
    {
        if (!similar(startX, startY))
            return;

        var stack = new Stack<(int x, int y)>(256);
        stack.Push((startX, startY));

        while (stack.Count > 0)
        {
            var (seedX, seedY) = stack.Pop();
            if ((uint)seedY >= (uint)height)
                continue;

            int seedIdx = seedY * width + seedX;
            if (visitStamp[seedIdx] == visitEpoch || !similar(seedX, seedY))
                continue;

            int left = seedX;
            while (left > 0)
            {
                int li = seedY * width + left - 1;
                if (visitStamp[li] == visitEpoch || !similar(left - 1, seedY))
                    break;
                left--;
            }

            int right = seedX;
            while (right + 1 < width)
            {
                int ri = seedY * width + right + 1;
                if (visitStamp[ri] == visitEpoch || !similar(right + 1, seedY))
                    break;
                right++;
            }

            for (int x = left; x <= right; x++)
            {
                int idx = seedY * width + x;
                visitStamp[idx] = visitEpoch;
                onPixel(x, seedY);
            }

            if (seedY > 0)
                PushNewSpans(stack, width, left, right, seedY - 1, similar, visitStamp, visitEpoch);
            if (seedY + 1 < height)
                PushNewSpans(stack, width, left, right, seedY + 1, similar, visitStamp, visitEpoch);
        }
    }

    private static void PushNewSpans(
        Stack<(int x, int y)> stack,
        int width,
        int left,
        int right,
        int y,
        Func<int, int, bool> similar,
        int[] visitStamp,
        int visitEpoch)
    {
        bool inSpan = false;
        for (int x = left; x <= right; x++)
        {
            int idx = y * width + x;
            if (similar(x, y) && visitStamp[idx] != visitEpoch)
            {
                if (!inSpan)
                {
                    stack.Push((x, y));
                    inSpan = true;
                }
            }
            else
            {
                inSpan = false;
            }
        }
    }
}
