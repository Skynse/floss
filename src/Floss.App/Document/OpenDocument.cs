using System;
using System.IO;
using Floss.App.Canvas;
using Floss.App.Processes;

namespace Floss.App.Document;

/// <summary>
/// A single open document tab. Owns its canvas, tool factory, and file metadata.
/// </summary>
internal sealed class OpenDocument
{
    public DrawingCanvas Canvas { get; }
    public ToolFactory ToolFactory { get; }
    public string? FilePath { get; set; }

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "Untitled";
    public bool IsDirty => Canvas.Document.IsDirty;

    public OpenDocument(DrawingCanvas canvas, string? filePath)
    {
        Canvas = canvas;
        FilePath = filePath;
        ToolFactory = new ToolFactory(canvas.Document, canvas.BrushEngine);
    }
}
