using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Floss.App.Docking;
using Floss.App.Document;
using Floss.App.Features;
using Floss.App.Features.Overview;

namespace Floss.App.Features.Dock.BuiltIn;

/// <summary>
/// Navigator / mini viewport docker — exercises <see cref="ICanvasViewHost"/> without MainWindow edits.
/// </summary>
public sealed class MiniViewportDockFeature : IFeatureModule
{
    public const string PanelId = "overview";

    public void Register(IFeatureSession session)
    {
        DockFeature.Register(
            PanelId,
            "Navigator",
            () => new MiniViewportPanel(session, session.GetService<IDocumentOverviewSource>()),
            defaultZone: "right-0",
            proportion: 0.15,
            minHeight: 120,
            sizing: DockPanelSizing.Fill);
    }

    private sealed class MiniViewportPanel : Control
    {
        private static readonly ISolidColorBrush PanelBg = new SolidColorBrush(Color.Parse("#2a2a2a"));
        private static readonly IPen DocBorderPen = new Pen(new SolidColorBrush(Color.Parse("#888888")), 1);
        private static readonly IBrush ViewportFill = new SolidColorBrush(Color.FromArgb(48, 100, 160, 255));
        private static readonly IPen ViewportPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 100, 160, 255)), 1.5);

        private readonly IFeatureSession _session;
        private readonly ICanvasViewHost _view;
        private readonly IDocumentOverviewSource _overview;
        private DocumentOverviewSnapshot? _snapshot;
        private bool _attached;
        private Size _lastRequestedSize;
        private bool _waitingForSnapshot;
        private bool _dragging;
        private Point _lastPointer;

        public MiniViewportPanel(IFeatureSession session, IDocumentOverviewSource overview)
        {
            _session = session;
            _view = session.View;
            _overview = overview;
            Focusable = true;
            MinHeight = 96;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            _view.ViewTransformChanged += OnViewTransformChanged;
            _view.DocumentVisualChanged += OnDocumentVisualChanged;
            _session.ActiveCanvasChanged += OnActiveCanvasChanged;
            _overview.SnapshotReady += OnSnapshotReady;
            LayoutUpdated += OnLayoutUpdated;
            // Bounds are often 0 at attach; run after the first layout pass.
            Dispatcher.UIThread.Post(RequestOverviewRefresh, DispatcherPriority.Loaded);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            _waitingForSnapshot = false;
            LayoutUpdated -= OnLayoutUpdated;
            _view.ViewTransformChanged -= OnViewTransformChanged;
            _view.DocumentVisualChanged -= OnDocumentVisualChanged;
            _overview.SnapshotReady -= OnSnapshotReady;
            _session.ActiveCanvasChanged -= OnActiveCanvasChanged;
            _overview.CancelPending();
            _snapshot?.Dispose();
            _snapshot = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnViewTransformChanged() => InvalidateVisual();

        private void OnActiveCanvasChanged()
        {
            _snapshot?.Dispose();
            _snapshot = null;
            _lastRequestedSize = default;
            _waitingForSnapshot = _view.HasDocument;
            InvalidateVisual();
        }

        private void OnDocumentVisualChanged()
        {
            InvalidateVisual();

            if (_snapshot == null)
                return;

            if (!_view.HasDocument
                || _snapshot.DocumentWidth != _view.DocumentWidth
                || _snapshot.DocumentHeight != _view.DocumentHeight)
            {
                _snapshot.Dispose();
                _snapshot = null;
                _lastRequestedSize = default;
                _waitingForSnapshot = true;
                if (_attached)
                    Dispatcher.UIThread.Post(RequestOverviewRefresh, DispatcherPriority.Background);
            }
        }

        private void OnSnapshotReady(DocumentOverviewSnapshot? snapshot)
        {
            if (snapshot != null)
            {
                _snapshot?.Dispose();
                _snapshot = snapshot;
                _waitingForSnapshot = false;
            }
            else
            {
                _snapshot?.Dispose();
                _snapshot = null;
                _waitingForSnapshot = false;
            }

            InvalidateVisual();
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (!_attached)
                return;

            var size = Bounds.Size;
            if (size.Width < 4 || size.Height < 4)
                return;

            if (_snapshot != null
                && Math.Abs(size.Width - _lastRequestedSize.Width) < 1
                && Math.Abs(size.Height - _lastRequestedSize.Height) < 1)
                return;

            RequestOverviewRefresh();
        }

        private void RequestOverviewRefresh()
        {
            if (!_attached)
                return;

            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var maxW = (int)Math.Ceiling(Bounds.Width * scale);
            var maxH = (int)Math.Ceiling(Bounds.Height * scale);
            if (maxW < 4 || maxH < 4)
                return;

            _lastRequestedSize = Bounds.Size;
            _waitingForSnapshot = true;
            _overview.RequestSnapshot(maxW, maxH);
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            var bounds = Bounds;
            var w = bounds.Width;
            var h = bounds.Height;
            if (w < 4 || h < 4) return;

            ctx.FillRectangle(PanelBg, new Rect(0, 0, w, h));

            if (!_view.HasDocument || _view.DocumentWidth <= 0 || _view.DocumentHeight <= 0)
            {
                DrawHint(ctx, w, h, "No document");
                return;
            }

            if (!TryGetDocumentRect(w, h, out var docRect, out _))
            {
                DrawHint(ctx, w, h, "—");
                return;
            }

            if (_snapshot != null)
            {
                using (ctx.PushRenderOptions(new RenderOptions
                {
                    BitmapInterpolationMode = BitmapInterpolationMode.HighQuality
                }))
                    ctx.DrawImage(_snapshot.Bitmap, docRect);
            }
            else if (_waitingForSnapshot)
                DrawUpdatingOverlay(ctx, docRect);

            ctx.DrawRectangle(null, DocBorderPen, docRect);

            var visible = _view.VisibleDocumentRegion;
            if (visible is { Width: > 0, Height: > 0 } region)
            {
                var viewRect = MapVisibleRegionToPanel(region, docRect, _view.DocumentWidth, _view.DocumentHeight);
                // ctx.FillRectangle(ViewportFill, viewRect); // we don't want to fill this
                ctx.DrawRectangle(null, ViewportPen, viewRect);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (!TryMapPointerToDocument(e.GetPosition(this), out var docPoint)) return;

            _dragging = true;
            _lastPointer = e.GetPosition(this);
            CenterOnDocumentPoint(docPoint.X, docPoint.Y);
            e.Handled = true;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (!TryGetDocumentRect(Bounds.Width, Bounds.Height, out var docRect, out _)) return;

            var pos = e.GetPosition(this);
            var dx = pos.X - _lastPointer.X;
            var dy = pos.Y - _lastPointer.Y;
            _lastPointer = pos;

            var docDx = dx * (_view.DocumentWidth / docRect.Width);
            var docDy = dy * (_view.DocumentHeight / docRect.Height);
            var (panDx, panDy) = CanvasViewTransformMath.DocumentDeltaToViewportPan(
                docDx,
                docDy,
                _view.Zoom,
                _view.Rotation,
                _view.FlipX,
                _view.FlipY);
            _view.PanBy(panDx, panDy);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragging)
            {
                _dragging = false;
                if (ReferenceEquals(e.Pointer.Captured, this))
                    e.Pointer.Capture(null);
                e.Handled = true;
            }
        }

        private void CenterOnDocumentPoint(double docX, double docY)
        {
            var (targetPanX, targetPanY) = CanvasViewTransformMath.PanToCenterDocumentPoint(
                docX,
                docY,
                _view.DocumentWidth,
                _view.DocumentHeight,
                _view.Zoom,
                _view.Rotation,
                _view.FlipX,
                _view.FlipY);
            _view.PanBy(targetPanX - _view.PanOffsetX, targetPanY - _view.PanOffsetY);
        }

        private bool TryMapPointerToDocument(Point pointer, out Point docPoint)
        {
            docPoint = default;
            if (!TryGetDocumentRect(Bounds.Width, Bounds.Height, out var docRect, out var docSize))
                return false;
            if (!docRect.Contains(pointer)) return false;

            var nx = (pointer.X - docRect.X) / docRect.Width;
            var ny = (pointer.Y - docRect.Y) / docRect.Height;
            docPoint = new Point(nx * docSize.Width, ny * docSize.Height);
            return true;
        }

        private bool TryGetDocumentRect(double panelW, double panelH, out Rect docRect, out PixelRegion docSize)
        {
            docSize = new PixelRegion(0, 0, _view.DocumentWidth, _view.DocumentHeight);
            docRect = default;

            var docW = _view.DocumentWidth;
            var docH = _view.DocumentHeight;
            if (docW <= 0 || docH <= 0) return false;

            const double pad = 6;
            var availW = Math.Max(1, panelW - pad * 2);
            var availH = Math.Max(1, panelH - pad * 2);
            var scale = Math.Min(availW / docW, availH / docH);
            var fitW = docW * scale;
            var fitH = docH * scale;
            docRect = new Rect(
                pad + (availW - fitW) * 0.5,
                pad + (availH - fitH) * 0.5,
                fitW,
                fitH);
            return fitW > 0 && fitH > 0;
        }

        private static Rect MapVisibleRegionToPanel(PixelRegion region, Rect docRect, int fullDocW, int fullDocH)
        {
            var sx = docRect.Width / fullDocW;
            var sy = docRect.Height / fullDocH;
            return new Rect(
                docRect.X + region.X * sx,
                docRect.Y + region.Y * sy,
                Math.Max(1, region.Width * sx),
                Math.Max(1, region.Height * sy));
        }

        private static void DrawUpdatingOverlay(DrawingContext ctx, Rect docRect)
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(200, 42, 42, 42)), docRect);
            var formatted = new FormattedText(
                "Updating…",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter"),
                11,
                Avalonia.Media.Brushes.Gray);
            ctx.DrawText(
                formatted,
                new Point(
                    docRect.X + (docRect.Width - formatted.Width) * 0.5,
                    docRect.Y + (docRect.Height - formatted.Height) * 0.5));
        }

        private static void DrawHint(DrawingContext ctx, double w, double h, string text)
        {
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter"),
                11,
                Avalonia.Media.Brushes.Gray);
            ctx.DrawText(formatted, new Point((w - formatted.Width) * 0.5, (h - formatted.Height) * 0.5));
        }
    }
}
