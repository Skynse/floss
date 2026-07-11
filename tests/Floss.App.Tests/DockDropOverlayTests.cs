using Avalonia;
using AvaloniaC = Avalonia.Controls.Canvas;
using Avalonia.Controls;
using Avalonia.Layout;
using Floss.App.Controls;

namespace Floss.App.Tests;

public class DockDropOverlayTests
{
    private static DockDropOverlay CreateOverlay(double width = 1920, double height = 1080)
    {
        AvaloniaTestBootstrap.EnsureInitialized();
        var overlay = new DockDropOverlay();
        overlay.Width = width;
        overlay.Height = height;
        overlay.Measure(new Size(width, height));
        overlay.Arrange(new Rect(0, 0, width, height));
        return overlay;
    }

    [Fact]
    public void ShowInsertLine_SetsPositionAndWidth()
    {
        var overlay = CreateOverlay();
        overlay.ShowInsertLine(100, 200, 300);

        var line = overlay.Children.OfType<Border>().First(b => b.Height == 2);
        Assert.True(line.IsVisible);
        Assert.Equal(100, AvaloniaC.GetLeft(line));
        Assert.Equal(199, AvaloniaC.GetTop(line));
        Assert.Equal(300, line.Width);
    }

    [Fact]
    public void ShowInsertLine_MinimumWidth40()
    {
        var overlay = CreateOverlay();
        overlay.ShowInsertLine(10, 10, 10);

        var line = overlay.Children.OfType<Border>().First(b => b.Height == 2);
        Assert.Equal(40, line.Width);
    }

    [Fact]
    public void ShowInsertLineVertical_SetsPositionAndHeight()
    {
        var overlay = CreateOverlay();
        overlay.ShowInsertLineVertical(150, 50, 400);

        var line = overlay.Children.OfType<Border>().First(b => b.Width == 2);
        Assert.True(line.IsVisible);
        Assert.Equal(149, AvaloniaC.GetLeft(line));
        Assert.Equal(50, AvaloniaC.GetTop(line));
        Assert.Equal(400, line.Height);
    }

    [Fact]
    public void ShowTabTarget_SetsPositionAndClampsHeight()
    {
        var overlay = CreateOverlay();
        overlay.ShowTabTarget(200, 100, 300, 50);

        var highlight = overlay.Children.OfType<Border>()
            .First(b => b.Background is ISolidColorBrush);
        Assert.True(highlight.IsVisible);
        Assert.Equal(200, AvaloniaC.GetLeft(highlight));
        Assert.Equal(100, AvaloniaC.GetTop(highlight));
        Assert.Equal(300, highlight.Width);
        Assert.True(highlight.Height <= DockDropOverlay.TabStripHeight + 4);
    }

    [Fact]
    public void Clear_HidesAllIndicators()
    {
        var overlay = CreateOverlay();
        overlay.ShowInsertLine(100, 200, 300);
        overlay.ShowHint("test", 100, 200);

        overlay.Clear();

        foreach (var child in overlay.Children.OfType<Border>())
            Assert.False(child.IsVisible);
    }

    [Fact]
    public void ShowHint_ClampsToOverlayBounds()
    {
        var overlay = CreateOverlay(800, 600);
        overlay.ShowHint("Some long hint text", 790, 590);

        var hintCard = overlay.Children.OfType<Border>()
            .First(b => b.Child is TextBlock);
        Assert.True(hintCard.IsVisible);
        var left = AvaloniaC.GetLeft(hintCard);
        var top = AvaloniaC.GetTop(hintCard);
        Assert.True(left < 800, $"Hint left {left} should be within overlay width");
        Assert.True(top < 600, $"Hint top {top} should be within overlay height");
    }

    [Fact]
    public void ShowHint_ClampsToOriginWhenNearZero()
    {
        var overlay = CreateOverlay(800, 600);
        overlay.ShowHint("test", -10, -10);

        var hintCard = overlay.Children.OfType<Border>()
            .First(b => b.Child is TextBlock);
        var left = AvaloniaC.GetLeft(hintCard);
        var top = AvaloniaC.GetTop(hintCard);
        Assert.True(left >= 0, $"Hint left {left} should be >= 0");
        Assert.True(top >= 0, $"Hint top {top} should be >= 0");
    }
}
