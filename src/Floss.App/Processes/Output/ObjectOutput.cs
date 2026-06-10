using Floss.App.Document;
using Floss.App.Processes.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

/// <summary>Commits object selection/manipulation gestures from <see cref="ObjectInputProcess"/>.</summary>
public sealed class ObjectOutput : IOutputProcess
{
    private ObjectInputProcess? _input;

    public bool Antialiasing { get; set; }

    public void BindInput(ObjectInputProcess input) => _input = input;

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is ObjectManipulationInput)
            ctx.InvalidateRender();
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ObjectManipulationInput || _input == null)
            return;

        _input.CommitSession(before => ctx.Document.CommitAssistantsChange(before));
    }

    public void Cancel()
    {
        _input?.CancelSession();
    }
}
