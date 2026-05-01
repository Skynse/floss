# Floss Architecture

Floss is a desktop drawing application scaffold built with Avalonia and C# targeting `.NET 10.0`. It relies on a custom rendering pipeline, a robust document model with a history system, and a modular brush and tool engine.

## Core Loop & Canvas

The central UI component is `DrawingCanvas`, a custom Avalonia `Control`. It manages the interaction between the Avalonia layout/input system and the underlying drawing tools.

- **`DrawingCanvas`**: Captures pointer events (mouse, pen, touch) and dispatches them to the `ToolController`. It renders the final composed image to the screen.
- **`LayerCompositor`**: Responsible for taking the layer stack and flattening it into an `Avalonia.Media.Imaging.WriteableBitmap`. It only updates "dirty" regions to minimize CPU overhead.

## Document Model

The state of a drawing is managed by `DrawingDocument`.
- **`DrawingLayer`**: Represents a single layer (or a folder/group layer). It stores properties like visibility, opacity, blend mode, and pixel data.
- **`TiledPixelBuffer`**: Pixel data in a layer is broken down into fixed-size tiles. This allows memory-efficient operations where empty areas consume no memory, and dirty regions can be calculated granularly.
- **`PixelRegion`**: Used to track dirty bounds (bounding boxes that need compositing) across tools and layer operations.
- **History System**: `DrawingDocument` implements a `Stack<IHistoryState>` for undo/redo. Mutations (like strokes or layer property changes) are pushed as states that can capture and restore tiles or properties.

## Input Handling

Avalonia's pointer events (`OnPointerPressed`, `OnPointerMoved`, etc.) are converted into `CanvasInputSample` instances. 
- **`CanvasInputSample`**: Normalizes the input into a unified coordinate space, incorporating the canvas zoom and rotation, and smoothing the pressure values.
- These samples are dispatched to the `ToolController` which routes them to the active tool.

## Tool System

Tools dictate what happens when the user interacts with the canvas.
- **`ITool`**: The interface for any tool (e.g., `BrushTool`, `SelectTool`, `TransformTool`). Tools receive `ToolInputEvent` streams and generate `IToolOperation` instances.
- **`IToolOperation`**: Represents an active interaction (like a single continuous brush stroke, or dragging a selection). Once the user lifts the pen, the operation commits its changes to the `DrawingDocument`.
- **`ToolContext`**: Provides tools access to the document, current brush, active colors, and a temporary `SelectionMask`.

## Rendering Pipeline

1. **Tool Interaction**: The `IToolOperation` modifies the active layer's `TiledPixelBuffer`.
2. **Dirty Notification**: The tool notifies `DrawingDocument` of the dirty `PixelRegion`.
3. **Invalidation**: `DrawingCanvas` listens to document changes and calls `LayerCompositor.Invalidate(dirtyRegion)`.
4. **Composition**: During Avalonia's render pass, `LayerCompositor.Composite` is called. It iterates through the layer tree, applies blending, and copies the final pixels to the `WriteableBitmap`.
5. **Draw**: `DrawingCanvas.Render` calls `context.DrawImage` with the composited bitmap, followed by overlay elements like the tool cursor or selection marquee.
