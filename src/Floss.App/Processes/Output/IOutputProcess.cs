using Floss.App.Tools;

namespace Floss.App.Processes;

// Consumes IProcessedInput and applies an effect to the document.
public interface IOutputProcess
{
    bool Antialiasing { get; set; }

    // Called during active input for live visual updates (e.g., moving layer, applying brush dabs).
    // Should modify state but NOT commit undo mutations.
    void Preview(ToolContext ctx, IProcessedInput input);

    // Called when input completes to finalize and commit undo mutation.
    void Execute(ToolContext ctx, IProcessedInput input);

    // Called when a running input transaction is cancelled.
    // Must release or restore any intermediate state held across Preview calls.
    void Cancel() { }

    // True when this output writes pixels to a layer (paint, fill, gradient).
    // Used by the canvas to block input on locked/invisible/group layers.
    bool IsPaintOutput => false;
}
