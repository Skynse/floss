using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;

/// <summary>
/// Frameless popup chrome with pen-aware title-bar dragging.
/// </summary>
internal static class CustomWindowChrome
{
    public static void ConfigurePopup(Window window)
    {
        window.WindowDecorations = WindowDecorations.None;
        window.ShowInTaskbar = false;
        window.Background = Avalonia.Media.Brushes.Transparent;
    }

    public static Control Wrap(Window window, string title, Control body, Action? onClose = null)
    {
        var header = BuildHeader(window, title, onClose ?? (() => window.Close()));
        WireTitleBarDrag(header, window);

        var shell = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = new SolidColorBrush(Color.Parse(Bg1))
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        shell.Children.Add(header);
        shell.Children.Add(body);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = shell
        };
    }

    public static void WireTitleBarDrag(Control handle, Window window)
    {
        PixelPoint? startWindow = null;
        PixelPoint? startPointerScreen = null;
        var dragging = false;

        handle.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(handle);
            if (!TabletInput.CanBeginUiDrag(pt))
                return;

            e.Handled = true;

            if (pt.Pointer.Type == PointerType.Mouse && !HasTabletAxisData(pt.Properties))
            {
                window.BeginMoveDrag(e);
                return;
            }

            startWindow = window.Position;
            startPointerScreen = handle.PointToScreen(e.GetPosition(handle));
            dragging = true;
            e.Pointer.Capture(handle);
        };

        handle.PointerMoved += (_, e) =>
        {
            if (!dragging || startWindow is not { } w0 || startPointerScreen is not { } s0)
                return;

            var pt = e.GetCurrentPoint(handle);
            if (!TabletInput.IsPrimaryPointerActive(pt))
            {
                EndDrag(e.Pointer);
                return;
            }

            var screen = handle.PointToScreen(e.GetPosition(handle));
            window.Position = new PixelPoint(
                w0.X + screen.X - s0.X,
                w0.Y + screen.Y - s0.Y);
        };

        void EndDrag(IPointer? pointer)
        {
            if (!dragging)
                return;
            dragging = false;
            startWindow = null;
            startPointerScreen = null;
            pointer?.Capture(null);
        }

        handle.PointerReleased += (_, e) => EndDrag(e.Pointer);
        handle.PointerCaptureLost += (_, _) => EndDrag(null);
    }

    private static Control BuildHeader(Window window, string title, Action onClose)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeBtn = new Button
        {
            Content = MaterialIcon(Icons.Close, 16),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        ToolTip.SetTip(closeBtn, "Close");
        closeBtn.Click += (_, _) => onClose();

        var header = new DockPanel
        {
            LastChildFill = true,
            Height = 32,
            Margin = new Thickness(8, 0),
            Background = new SolidColorBrush(Color.Parse(Bg1))
        };
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);
        header.Children.Add(titleBlock);
        return header;
    }

    private static bool HasTabletAxisData(PointerPointProperties props)
        => props.Pressure > 0 || props.XTilt != 0 || props.YTilt != 0 || props.Twist != 0;

    private static PathIcon MaterialIcon(string pathData, double size) =>
        new()
        {
            Data = Geometry.Parse(pathData),
            Width = size,
            Height = size,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
        };
}
