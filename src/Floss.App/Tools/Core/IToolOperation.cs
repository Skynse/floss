using Floss.App.Input;

namespace Floss.App.Tools;

public interface IToolOperation
{
    int SampleCount { get; }
    void Update(CanvasInputSample sample);
    void Commit(CanvasInputSample sample);
    void Cancel();
}
