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
}

