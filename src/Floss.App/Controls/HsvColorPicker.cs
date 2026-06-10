using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Floss.App.Controls;

public sealed class HsvColorPicker : Control
{
    private const double HueBarWidth = 18;
    private const double GapWidth = 6;

    private double _hue;
    private double _sat = 1.0;
    private double _val = 0.5;

    private WriteableBitmap? _svBmp;
    private WriteableBitmap? _hueBmp;

    private enum DragTarget { None, Sv, Hue }
    private DragTarget _drag;

    // h: 0–360, s: 0–1, v: 0–1
    public event Action<double, double, double>? HsvChanged;

    public (double H, double S, double V) Hsv => (_hue, _sat, _val);

    public void SetHsv(double h, double s, double v)
    {
        _hue = Math.Clamp(h, 0, 360);
        _sat = Math.Clamp(s, 0, 1);
        _val = Math.Clamp(v, 0, 1);
        RebuildSvBmp();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RebuildSvBmp();
        RebuildHueBmp();
        InvalidateVisual();
    }

    private Rect SvRect => new(0, 0, Math.Max(0, Bounds.Width - HueBarWidth - GapWidth), Bounds.Height);
    private Rect HueRect => new(Bounds.Width - HueBarWidth, 0, HueBarWidth, Bounds.Height);

    private void RebuildSvBmp()
    {
        var w = (int)SvRect.Width;
        var h = (int)Bounds.Height;
        if (w <= 0 || h <= 0) { _svBmp = null; return; }

        _svBmp = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        unsafe
        {
            using var frame = _svBmp.Lock();
            var pixels = (byte*)frame.Address;
            var stride = frame.RowBytes;
            for (int y = 0; y < h; y++)
            {
                var v = 1.0 - (double)y / Math.Max(1, h - 1);
                for (int x = 0; x < w; x++)
                {
                    var s = (double)x / Math.Max(1, w - 1);
                    var (r, g, b) = HsvToRgb(_hue, s, v);
                    var dst = pixels + y * stride + x * 4;
                    dst[0] = (byte)(b * 255);
                    dst[1] = (byte)(g * 255);
                    dst[2] = (byte)(r * 255);
                    dst[3] = 255;
                }
            }
        }
    }

    private void RebuildHueBmp()
    {
        var h = (int)Bounds.Height;
        if (h <= 0) { _hueBmp = null; return; }

        _hueBmp = new WriteableBitmap(
            new PixelSize(1, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        unsafe
        {
            using var frame = _hueBmp.Lock();
            var pixels = (byte*)frame.Address;
            var stride = frame.RowBytes;
            for (int y = 0; y < h; y++)
            {
                var hue = (double)y / Math.Max(1, h - 1) * 360.0;
                var (r, g, b) = HsvToRgb(hue, 1.0, 1.0);
                var dst = pixels + y * stride;
                dst[0] = (byte)(b * 255);
                dst[1] = (byte)(g * 255);
                dst[2] = (byte)(r * 255);
                dst[3] = 255;
            }
        }
    }

    public override void Render(DrawingContext ctx)
    {
        if (_svBmp == null || _hueBmp == null) return;

        var sv = SvRect;
        var hue = HueRect;

        ctx.DrawImage(_svBmp, sv);
        ctx.DrawImage(_hueBmp, hue);

        // Hue indicator — sandwiched black/white lines for contrast on any color
        var hy = _hue / 360.0 * hue.Height + hue.Top;
        var blackPen = new Pen(new SolidColorBrush(Colors.Black), 1);
        var whitePen = new Pen(new SolidColorBrush(Colors.White), 2);
        ctx.DrawLine(whitePen, new Point(hue.Left, hy), new Point(hue.Right, hy));
        ctx.DrawLine(blackPen, new Point(hue.Left, hy - 1.5), new Point(hue.Right, hy - 1.5));
        ctx.DrawLine(blackPen, new Point(hue.Left, hy + 1.5), new Point(hue.Right, hy + 1.5));

        // SV indicator — double ring (black outer, white inner)
        var cx = _sat * sv.Width + sv.Left;
        var cy = (1.0 - _val) * sv.Height + sv.Top;
        const double R = 5.5;
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.Black), 2), new Point(cx, cy), R + 1, R + 1);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White), 1.5), new Point(cx, cy), R, R);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        if (SvRect.Contains(pos))
        {
            _drag = DragTarget.Sv;
            UpdateSv(pos);
        }
        else if (HueRect.Contains(pos))
        {
            _drag = DragTarget.Hue;
            UpdateHue(pos);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragTarget.None) return;
        var pos = e.GetPosition(this);
        if (_drag == DragTarget.Sv) UpdateSv(pos);
        else UpdateHue(pos);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = DragTarget.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void UpdateSv(Point pos)
    {
        var r = SvRect;
        _sat = Math.Clamp(pos.X / Math.Max(1, r.Width), 0, 1);
        _val = Math.Clamp(1.0 - pos.Y / Math.Max(1, r.Height), 0, 1);
        InvalidateVisual();
        HsvChanged?.Invoke(_hue, _sat, _val);
    }

    private void UpdateHue(Point pos)
    {
        var r = HueRect;
        _hue = Math.Clamp((pos.Y - r.Top) / Math.Max(1, r.Height), 0, 1) * 360.0;
        RebuildSvBmp();
        InvalidateVisual();
        HsvChanged?.Invoke(_hue, _sat, _val);
    }

    internal static (double r, double g, double b) HsvToRgb(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        var hi = (int)(h / 60) % 6;
        var f = h / 60 - Math.Floor(h / 60);
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        return hi switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
    }
}
