using Floss.App;
using Floss.App.Processes;
using Floss.App.Processes.Output;
using Floss.App.Tools;

namespace Floss.App.Tests;

public class ToolPresetSyncTests
{
    [Fact]
    public void Apply_UpdatesSelectionAreaOperationFromPreset()
    {
        var preset = new ToolPreset
        {
            OutputProcess = OutputProcessType.SelectionArea,
            SelectOp = SelectOp.Add
        };
        var tool = new CompositeTool(new Processes.Input.RectInputProcess(), new SelectionAreaOutput
        {
            Operation = SelectOp.Replace
        });

        ToolPresetSync.Apply(tool, preset);

        Assert.Equal(SelectOp.Add, ((SelectionAreaOutput)tool.Output).Operation);
    }

    [Fact]
    public void Apply_UpdatesMagicWandOperationFromPreset()
    {
        var preset = new ToolPreset
        {
            OutputProcess = OutputProcessType.MagicWand,
            SelectOp = SelectOp.Subtract,
            Tolerance = 0.42
        };
        var tool = new CompositeTool(new Processes.Input.ClickInputProcess(), new MagicWandOutput
        {
            Operation = SelectOp.Replace,
            Tolerance = 0.1
        });

        ToolPresetSync.Apply(tool, preset);

        var wand = (MagicWandOutput)tool.Output;
        Assert.Equal(SelectOp.Subtract, wand.Operation);
        Assert.Equal(0.42, wand.Tolerance);
    }
}
