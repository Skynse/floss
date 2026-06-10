namespace Floss.App.Document.Assistants;

/// <summary>Future 3D / scene object payload for <see cref="DrawingLayer.IsObjectLayer"/> layers.</summary>
public sealed class ObjectLayerData
{
    public ObjectLayerData Clone() => new();
}

public enum ObjectLayerKind
{
    Model3D,
}

public enum RulerShowScope
{
    AllLayers,
    SameFolder,
    EditingTarget,
}

public static class RulerDisplayNames
{
    public static string For(PaintingAssistant ruler) => ruler.TypeId switch
    {
        PaintingAssistant.PerspectiveType => ruler.UsesFisheyeGrid ? "Fisheye Ruler" : "Perspective Ruler",
        PaintingAssistant.RulerType => "Line Ruler",
        _ => "Ruler",
    };
}
