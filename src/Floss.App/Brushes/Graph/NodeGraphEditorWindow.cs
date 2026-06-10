using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Floss.App.Brushes;

namespace Floss.App.Brushes.Graph;

using static AppColors;

/// <summary>Legacy popup host; prefer the bottom-docked <see cref="NodeGraphEditorPanel"/> in MainWindow.</summary>
public sealed class NodeGraphEditorWindow : Window
{
    private readonly NodeGraphEditorPanel _panel;

    public NodeGraphEditorWindow(BrushTipNodeGraph graph, Action<BrushTipNodeGraph> onCommit,
        Action<BrushTipNodeGraph, string>? onSaveAsNew = null,
        IReadOnlyList<BrushTipData>? imageSamplers = null)
    {
        Title = "Node Graph Editor";
        Width = 960;
        Height = 680;
        MinWidth = 700;
        MinHeight = 400;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        ShowInTaskbar = false;

        _panel = new NodeGraphEditorPanel(graph, onCommit, onSaveAsNew, imageSamplers);
        Content = _panel;
        Opened += (_, _) => _panel.Focus();
    }

    public void SetImageSamplerOptions(IReadOnlyList<BrushTipData>? tips)
        => _panel.SetImageSamplerOptions(tips);

    public void LoadGraph(BrushTipNodeGraph graph)
        => _panel.LoadGraph(graph);
}
