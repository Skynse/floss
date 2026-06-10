using Avalonia;
using Avalonia.Headless;
using Floss.App.Canvas;
using Floss.App.Config;
using Floss.App.Document;
using Floss.App.Features;

namespace Floss.App.Tests;

public class DocumentHistoryTests
{
    private static readonly object AvaloniaGate = new();
    private static bool _avaloniaInitialized;

    private static void EnsureAvalonia()
    {
        lock (AvaloniaGate)
        {
            if (_avaloniaInitialized || Application.Current != null)
            {
                _avaloniaInitialized = true;
                return;
            }

            try
            {
                AppBuilder.Configure<Floss.App.App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
            }
            catch (InvalidOperationException) { }

            _avaloniaInitialized = true;
        }
    }

    [Fact]
    public void Constructor_StartsWithDocumentOrigin()
    {
        var doc = new DrawingDocument(8, 8);
        Assert.Single(doc.HistoryEntries);
        Assert.Equal(0, doc.HistoryIndex);
        Assert.Equal("Document", doc.HistoryEntries[0].Label);
        Assert.Equal(0, doc.HistoryEntries[0].StateId);
    }

    [Fact]
    public void HistoryTimeline_GrowsAndLabelsEdits()
    {
        var doc = new DrawingDocument(8, 8);
        doc.AddLayer();
        Assert.Equal(2, doc.HistoryEntries.Count);
        Assert.Equal(1, doc.HistoryIndex);
        Assert.Equal("Document state", doc.HistoryEntries[1].Label);

        doc.SetActiveLayerName("Ink");
        Assert.Equal(3, doc.HistoryEntries.Count);
        Assert.Equal("Rename layer", doc.HistoryEntries[2].Label);
    }

    [Fact]
    public void UndoRedo_MovesTimelineIndex()
    {
        var doc = new DrawingDocument(8, 8);
        doc.AddLayer();
        doc.SetActiveLayerName("Ink");
        Assert.Equal(2, doc.HistoryIndex);

        doc.Undo();
        Assert.Equal(1, doc.HistoryIndex);
        Assert.True(doc.CanUndo);

        doc.Redo();
        Assert.Equal(2, doc.HistoryIndex);
        Assert.Equal("Ink", doc.ActiveLayer!.Name);
    }

    [Fact]
    public void JumpToHistoryIndex_RestoresEarlierState()
    {
        var doc = new DrawingDocument(8, 8);
        doc.AddLayer();
        doc.SetActiveLayerName("Ink");
        doc.SetActiveLayerName("Paint");

        doc.JumpToHistoryIndex(1);
        Assert.Equal(1, doc.HistoryIndex);
        Assert.Equal("Layer 1", doc.ActiveLayer!.Name);
    }

    [Fact]
    public void MarkAsSaved_FlagsCurrentTimelineEntry()
    {
        var doc = new DrawingDocument(8, 8);
        doc.AddLayer();
        doc.MarkAsSaved();
        Assert.True(doc.HistoryEntries[doc.HistoryIndex].IsSaved);
    }

    [Fact]
    public void BindCanvas_SwitchesActiveHistory()
    {
        EnsureAvalonia();

        using var canvasA = new DrawingCanvas();
        using var canvasB = new DrawingCanvas();
        canvasB.AddLayer();

        var history = new DocumentHistorySource(canvasA);
        history.BindCanvas(canvasB);
        Assert.True(history.HasDocument);
        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(1, history.CurrentIndex);
    }

    [Fact]
    public void HistoryLabels_FromPreset_UsesTrimmedName()
    {
        Assert.Equal("Soft Round", HistoryLabels.FromPreset(new ToolPreset { Name = " Soft Round " }));
        Assert.Null(HistoryLabels.FromPreset(new ToolPreset { Name = "  " }));
        Assert.Null(HistoryLabels.FromPreset(null));
        Assert.Equal("Transform", HistoryLabels.FromPresetOrDefault(null, "Transform"));
    }

    [Fact]
    public void PendingHistoryLabel_OverridesTypeBasedDescription()
    {
        var doc = new DrawingDocument(8, 8);
        doc.AddLayer();
        doc.SetPendingHistoryLabel("Gouache");
        doc.BeginDocumentMutation();
        Assert.Equal("Gouache", doc.HistoryEntries[^1].Label);
    }

    [Fact]
    public void ClearForImport_NotifiesHistoryListeners()
    {
        var doc = new DrawingDocument(8, 8);
        doc.AddLayer();
        var changed = 0;
        doc.HistoryChanged += (_, _) => changed++;

        doc.ClearForImport();

        Assert.Equal(1, changed);
        Assert.Single(doc.HistoryEntries);
        Assert.Equal("Document", doc.HistoryEntries[0].Label);
    }
}
