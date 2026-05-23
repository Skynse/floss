namespace Floss.App.Tests;

public class KeyboardInputScopeTests
{
    [Fact]
    public void RoutesCanvasOverViewport()
    {
        var scope = new KeyboardInputScope();
        scope.Activate(KeyboardInputRegion.Canvas);
        TestAssertions.True(scope.ShouldRouteToCanvas(null));
        scope.Activate(KeyboardInputRegion.NodeGraph);
        TestAssertions.False(scope.ShouldRouteToCanvas(null));
        scope.Activate(KeyboardInputRegion.Chrome);
        TestAssertions.False(scope.ShouldRouteToCanvas(null));
    }

    [Fact]
    public void RoutesNodeGraphOverDock()
    {
        var scope = new KeyboardInputScope();
        scope.Activate(KeyboardInputRegion.NodeGraph);
        TestAssertions.True(scope.ShouldRouteToNodeGraph(null));
        scope.Activate(KeyboardInputRegion.Canvas);
        TestAssertions.False(scope.ShouldRouteToNodeGraph(null));
        scope.Activate(KeyboardInputRegion.Chrome);
        TestAssertions.False(scope.ShouldRouteToNodeGraph(null));
    }

    [Fact]
    public void BlocksShortcutsDuringTextEntry()
    {
        var scope = new KeyboardInputScope();
        scope.Activate(KeyboardInputRegion.Canvas);
        var textBox = new Avalonia.Controls.TextBox();
        TestAssertions.False(scope.ShouldRouteToCanvas(textBox));
        scope.Activate(KeyboardInputRegion.NodeGraph);
        TestAssertions.False(scope.ShouldRouteToNodeGraph(textBox));
    }

    [Fact]
    public void PointerOverCanvasWinsOverNodeGraphFocus()
    {
        var scope = new KeyboardInputScope();
        var canvas = new Avalonia.Controls.Grid { IsVisible = true };
        var nodeGraph = new Avalonia.Controls.Grid { IsVisible = true };
        scope.RegisterSurface(canvas, KeyboardInputRegion.Canvas);
        scope.RegisterSurface(nodeGraph, KeyboardInputRegion.NodeGraph);
        scope.Activate(KeyboardInputRegion.NodeGraph);
        scope.UpdatePointerVisual(canvas);

        TestAssertions.True(scope.ShouldRouteToCanvas(nodeGraph));
        TestAssertions.False(scope.ShouldRouteToNodeGraph(nodeGraph));
    }

    [Fact]
    public void PointerOverNodeGraphWinsOverCanvasFocus()
    {
        var scope = new KeyboardInputScope();
        var canvas = new Avalonia.Controls.Grid { IsVisible = true };
        var nodeGraph = new Avalonia.Controls.Grid { IsVisible = true };
        scope.RegisterSurface(canvas, KeyboardInputRegion.Canvas);
        scope.RegisterSurface(nodeGraph, KeyboardInputRegion.NodeGraph);
        scope.Activate(KeyboardInputRegion.Canvas);
        scope.UpdatePointerVisual(nodeGraph);

        TestAssertions.True(scope.ShouldRouteToNodeGraph(canvas));
        TestAssertions.False(scope.ShouldRouteToCanvas(canvas));
    }
}
