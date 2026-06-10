namespace Floss.App.Document.Assistants;

public enum PerspectiveAssistantMode
{
    FreeQuad,
    OnePoint,
    TwoPoint,
    ThreePoint,
}

public static class PerspectiveAssistantModeExtensions
{
    public static string DisplayName(this PerspectiveAssistantMode mode) => mode switch
    {
        PerspectiveAssistantMode.OnePoint => "1-Point",
        PerspectiveAssistantMode.TwoPoint => "2-Point",
        PerspectiveAssistantMode.ThreePoint => "3-Point",
        _ => "Free Quad",
    };
}
