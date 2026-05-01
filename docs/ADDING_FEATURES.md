# Adding Features to Floss

This guide will help you navigate the codebase when adding new features or making modifications.

## Adding a New Tool

All tools implement the `ITool` interface. To add a new tool (e.g., a "Smudge" tool):

1. **Create the Tool Class**:
   Create a new file in `src/Floss.App/Tools/SmudgeTool.cs` implementing `ITool`.
   ```csharp
   public class SmudgeTool : ITool
   {
       public IToolOperation? Begin(ToolInputEvent e, ToolContext context)
       {
           // Handle the initial pointer down event.
           // Return a new SmudgeToolOperation if successful.
       }
       public void RenderOverlay(DrawingContext context, double zoom) { }
       public void Cancel() { }
   }
   ```

2. **Create the Tool Operation**:
   Implement `IToolOperation` to handle the movement and release.
   ```csharp
   public class SmudgeToolOperation : IToolOperation
   {
       public void Update(ToolInputEvent e)
       {
           // Handle movement and modify the layer's TiledPixelBuffer
       }
       public void Commit()
       {
           // Notify DrawingDocument to push a history state
       }
       public void Cancel() { }
       public void RenderOverlay(DrawingContext context, double zoom) { }
   }
   ```

3. **Register the Tool**:
   In `MainWindow.axaml.cs` or the relevant UI view model, instantiate your tool and bind it to a UI button that calls `DrawingCanvas.SetActiveTool()`.

## Adding UI Components

Floss uses Avalonia for its UI.

1. **MainWindow Structure**:
   `MainWindow.axaml` defines the overall layout, sidebars, and the placement of the `DrawingCanvas`.
2. **Adding a Panel**:
   If you want to add a new side panel (e.g., a color history palette), define it in `MainWindow.axaml` and handle the logic in `MainWindow.axaml.cs`.
3. **Icons**:
   Icons are managed as geometry paths in `Icons.cs` or as resources in `App.axaml`.

## Modifying the Brush Engine

If you want to add a new brush parameter (e.g., "Scatter"):
1. Add the parameter to `BrushPreset`.
2. Add a `CurveOption` property in `BrushDynamics`.
3. Update `BrushEngine.cs` to evaluate the new curve based on the current `StrokePoint` pressure/speed.
4. Apply the scatter offset during the `IBrushTip.Stamp()` step.
