using Floss.App.Tools;

namespace Floss.App.Processes;

// Consumes IProcessedInput and applies an effect to the document.
public interface IOutputProcess
{
    bool Antialiasing { get; set; }

    void Execute(ToolContext ctx, IProcessedInput input);
}
