using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Features;
using Floss.App.Features.Overview.Histogram;

namespace Floss.App.Features.Dock.Panels;

internal sealed class HistogramDockPanel : Control
{
    private static readonly IBrush PanelBg = new SolidColorBrush(Color.Parse("#2a2a2a"));
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), 1);
    private static readonly IBrush RedFill = new SolidColorBrush(Color.FromArgb(96, 232, 85, 85));
    private static readonly IBrush GreenFill = new SolidColorBrush(Color.FromArgb(96, 85, 232, 120));
    private static readonly IBrush BlueFill = new SolidColorBrush(Color.FromArgb(96, 85, 136, 232));

    private readonly IFeatureSession _session;
    private readonly IDocumentHistogramSource _histogram;
    private DocumentHistogram? _data;
    private bool _attached;
    private bool _waiting;

    public HistogramDockPanel(IFeatureSession session, IDocumentHistogramSource histogram)
    {
        _session = session;
        _histogram = histogram;
        Focusable = true;
        MinHeight = 96;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _histogram.HistogramReady += OnHistogramReady;
        _session.ActiveCanvasChanged += OnActiveCanvasChanged;
        _waiting = true;
        _histogram.RequestUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _waiting = false;
        _histogram.HistogramReady -= OnHistogramReady;
        _session.ActiveCanvasChanged -= OnActiveCanvasChanged;
        _histogram.CancelPending();
        _data = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnActiveCanvasChanged()
    {
        _data = null;
        if (_attached)
            _waiting = true;
        InvalidateVisual();
    }

    private void OnHistogramReady(DocumentHistogram? histogram)
    {
        _data = histogram;
        _waiting = false;
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8)
            return;

        ctx.FillRectangle(PanelBg, new Rect(0, 0, w, h));

        var plot = new Rect(6, 6, Math.Max(1, w - 12), Math.Max(1, h - 12));
        ctx.DrawRectangle(null, GridPen, plot);

        if (_data == null || _data.TotalSamples == 0)
        {
            var hint = _waiting ? "Updating…" : "No document";
            var ft = new FormattedText(
                hint,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11,
                HintBrush);
            ctx.DrawText(ft, new Point(plot.X + (plot.Width - ft.Width) / 2, plot.Y + (plot.Height - ft.Height) / 2));
            return;
        }

        var peak = Math.Max(1, _data.PeakCount);
        DrawChannel(ctx, plot, _data.Red.Span, RedFill, peak);
        DrawChannel(ctx, plot, _data.Green.Span, GreenFill, peak);
        DrawChannel(ctx, plot, _data.Blue.Span, BlueFill, peak);
    }

    private static void DrawChannel(DrawingContext ctx, Rect plot, ReadOnlySpan<int> bins, IBrush fill, int peak)
    {
        if (bins.Length != 256)
            return;

        var barW = plot.Width / 256.0;
        for (var i = 0; i < 256; i++)
        {
            var count = bins[i];
            if (count <= 0)
                continue;

            var barH = plot.Height * (count / (double)peak);
            var x = plot.X + i * barW;
            var y = plot.Bottom - barH;
            ctx.FillRectangle(fill, new Rect(x, y, Math.Max(1, barW), barH));
        }
    }
}
