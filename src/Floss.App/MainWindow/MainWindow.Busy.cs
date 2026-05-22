using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Floss.App;

using static Floss.App.AppColors;

/// <summary>
/// Ref-counted busy indicator: footer progress strip + optional canvas overlay.
/// Nested scopes are safe (e.g. open → apply).
/// </summary>
public sealed class BusyScope : IDisposable
{
    private readonly MainWindow _window;
    private int _disposed;

    internal BusyScope(MainWindow window, string message, bool blockInput)
    {
        _window = window;
        _window.PushBusy(message, blockInput);
    }

    public void Report(string message) => _window.UpdateBusy(message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _window.PopBusy();
    }
}

public partial class MainWindow
{
    private int _busyDepth;
    private bool _busySavedPaintSuspended;
    private ProgressBar? _busyFooterProgress;
    private Border? _busyOverlay;
    private TextBlock? _busyOverlayText;

    internal BusyScope BeginBusy(string message, bool blockInput = true)
        => new(this, message, blockInput);

    internal async System.Threading.Tasks.Task RunWithBusyAsync(
        string message,
        Func<System.Threading.Tasks.Task> action,
        bool blockInput = true)
    {
        using (BeginBusy(message, blockInput))
            await action();
    }

    internal async System.Threading.Tasks.Task<T> RunWithBusyAsync<T>(
        string message,
        Func<System.Threading.Tasks.Task<T>> action,
        bool blockInput = true)
    {
        using (BeginBusy(message, blockInput))
            return await action();
    }

    internal void PushBusy(string message, bool blockInput)
    {
        if (Interlocked.Increment(ref _busyDepth) == 1)
            SetBusyVisible(true, blockInput);

        UpdateBusy(message);
    }

    internal void PopBusy()
    {
        var depth = Interlocked.Decrement(ref _busyDepth);
        if (depth < 0)
        {
            Interlocked.Exchange(ref _busyDepth, 0);
            return;
        }

        if (depth == 0)
            SetBusyVisible(false, blockInput: false);
    }

    internal void UpdateBusy(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _footerStatusText.Text = message;
            if (_busyOverlayText != null)
                _busyOverlayText.Text = message;
        }, DispatcherPriority.Render);
    }

    private Control BuildFooterPanel()
    {
        _busyFooterProgress = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 2),
        };

        return new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _busyFooterProgress,
                _footerStatusText,
            }
        };
    }

    private void AttachBusyOverlay()
    {
        _busyOverlayText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        };

        var overlayProgress = new ProgressBar
        {
            Width = 220,
            Height = 4,
            IsIndeterminate = true,
            Margin = new Thickness(0, 0, 0, 10),
        };

        _busyOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(140, 10, 8, 14)),
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.Parse(Bg1)),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(20, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 0,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        overlayProgress,
                        _busyOverlayText,
                    }
                }
            }
        };

        _workspaceViewport.Children.Add(_busyOverlay);
    }

    private void SetBusyVisible(bool visible, bool blockInput)
    {
        if (_busyFooterProgress != null)
            _busyFooterProgress.IsVisible = visible;

        if (_busyOverlay != null)
        {
            _busyOverlay.IsVisible = visible && blockInput;
            _busyOverlay.IsHitTestVisible = visible && blockInput;
        }

        if (visible && blockInput)
        {
            if (_busyDepth == 1)
            {
                _busySavedPaintSuspended = _canvas.PaintInputSuspended;
                _canvas.PaintInputSuspended = true;
            }
        }
        else if (!visible)
        {
            _canvas.PaintInputSuspended = _busySavedPaintSuspended;
        }
    }
}
