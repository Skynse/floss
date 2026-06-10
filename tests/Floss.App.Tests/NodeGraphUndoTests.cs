using Avalonia;
using Floss.App.Brushes.Graph;

namespace Floss.App.Tests;

public class NodeGraphUndoTests
{
    private static NodeGraphView CreateSizedView()
    {
        var view = new NodeGraphView();
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        return view;
    }

    [Fact]
    public void UndoToBaseline_KeepsNodeLayout()
    {
        var view = CreateSizedView();
        var graph = BrushTipNodeGraph.SimpleCircle();

        view.LoadGraph(graph, new());
        view.AutoLayout();
        view.CaptureHistoryBaseline();

        TestAssertions.True(view.AllNodesHavePositions());
        var baselineCount = view.Graph.Nodes.Count;

        view.AddNode(BrushTipNodeKind.Value, new Point(200, 200));
        TestAssertions.Equal(baselineCount + 1, view.Graph.Nodes.Count);

        view.Undo();
        TestAssertions.Equal(baselineCount, view.Graph.Nodes.Count);
        TestAssertions.True(view.AllNodesHavePositions());
    }

    [Fact]
    public void CaptureHistoryBaseline_FixesPreLayoutUndoSnapshot()
    {
        var view = CreateSizedView();
        view.LoadGraph(BrushTipNodeGraph.SimpleCircle(), new());

        // Old bug: LoadGraph alone recorded empty positions in history[0].
        view.AutoLayout();
        view.AddNode(BrushTipNodeKind.Value, new Point(100, 100));
        view.Undo();
        view.Undo();

        TestAssertions.True(view.AllNodesHavePositions());
        TestAssertions.True(view.Graph.Nodes.Count > 0);
    }
}
