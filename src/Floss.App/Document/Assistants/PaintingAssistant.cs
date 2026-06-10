using System;
using Avalonia;

namespace Floss.App.Document.Assistants;

/// <summary>Document-persisted guide.</summary>
public sealed class PaintingAssistant
{
    public const string RulerType = "ruler";
    public const string PerspectiveType = "perspective";
    public const string FisheyeType = "fisheye";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string TypeId { get; set; } = RulerType;

    public Point HandleA { get; set; }

    public Point HandleB { get; set; }

    public Point HandleC { get; set; }

    public Point HandleD { get; set; }

    public bool IsVisible { get; set; } = true;

    public bool SnapEnabled { get; set; } = true;

    public PerspectiveAssistantMode PerspectiveMode { get; set; } = PerspectiveAssistantMode.FreeQuad;

    /// <summary>spherical grid on the perspective ruler.</summary>
    public bool FisheyeEnabled { get; set; }

    /// <summary>Fisheye field of view in degrees (10–360).</summary>
    public double FovDegrees { get; set; } = 180;

    public bool UsesFisheyeGrid =>
        (TypeId == PerspectiveType && FisheyeEnabled) || TypeId == FisheyeType;

    /// <summary>Grid line count between guide edges (2–12).</summary>
    public int GridSubdivisions { get; set; } = 4;

    public int HandleCount => TypeId switch
    {
        PerspectiveType => PerspectiveMode switch
        {
            PerspectiveAssistantMode.OnePoint => 2,
            PerspectiveAssistantMode.TwoPoint => 3,
            PerspectiveAssistantMode.ThreePoint => 3,
            _ => 4,
        },
        _ => 2,
    };

    public PaintingAssistant Clone()
        => new()
        {
            Id = Id,
            TypeId = TypeId,
            HandleA = HandleA,
            HandleB = HandleB,
            HandleC = HandleC,
            HandleD = HandleD,
            IsVisible = IsVisible,
            SnapEnabled = SnapEnabled,
            PerspectiveMode = PerspectiveMode,
            FisheyeEnabled = FisheyeEnabled,
            FovDegrees = FovDegrees,
            GridSubdivisions = GridSubdivisions,
        };

    /// <summary>Legacy assistants saved as type <c>fisheye</c> become perspective + fisheye.</summary>
    public void NormalizeLegacyType()
    {
        if (TypeId != FisheyeType)
            return;

        TypeId = PerspectiveType;
        FisheyeEnabled = true;
        if (PerspectiveMode == PerspectiveAssistantMode.FreeQuad
            && HandleC == default
            && HandleD == default)
            PerspectiveMode = PerspectiveAssistantMode.OnePoint;
    }

    public void SetHandle(int index, Point value)
    {
        switch (index)
        {
            case 1: HandleA = value; break;
            case 2: HandleB = value; break;
            case 3: HandleC = value; break;
            case 4: HandleD = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public Point GetHandle(int index) => index switch
    {
        1 => HandleA,
        2 => HandleB,
        3 => HandleC,
        4 => HandleD,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static PaintingAssistant CreateDefaultAt(
        Point center,
        int documentWidth,
        int documentHeight,
        AssistantCreateSettings settings)
    {
        var extent = Math.Min(Math.Max(documentWidth, 1), Math.Max(documentHeight, 1)) * 0.65;
        var halfW = extent * 0.5;
        var halfH = extent * 0.4;
        return FromDrag(
            settings.TypeId,
            new Point(center.X - halfW, center.Y - halfH),
            new Point(center.X + halfW, center.Y + halfH),
            settings);
    }

    public static PaintingAssistant FromDrag(string typeId, Point start, Point end, AssistantCreateSettings? settings = null)
    {
        var assistant = typeId switch
        {
            PerspectiveType => CreatePerspectiveDrag(start, end, settings),
            FisheyeType => CreatePerspectiveDrag(start, end, MergeFisheyeSettings(settings)),
            _ => new PaintingAssistant
            {
                TypeId = RulerType,
                HandleA = start,
                HandleB = end,
            },
        };

        if (settings != null)
            settings.Value.ApplyTo(assistant);
        else if (typeId == FisheyeType)
            assistant.FovDegrees = 180;

        assistant.NormalizeLegacyType();
        return assistant;
    }

    public void RepositionForCurrentMode(Point start, Point end)
    {
        if (TypeId != PerspectiveType)
            return;

        var mode = PerspectiveMode;
        var settings = new AssistantCreateSettings(
            TypeId, mode, FisheyeEnabled, FovDegrees, GridSubdivisions, SnapEnabled, CreateAtEditingLayer: true);
        var repositioned = FromDrag(PerspectiveType, start, end, settings);
        HandleA = repositioned.HandleA;
        HandleB = repositioned.HandleB;
        HandleC = repositioned.HandleC;
        HandleD = repositioned.HandleD;
    }

    private static PaintingAssistant CreatePerspectiveDrag(
        Point start,
        Point end,
        AssistantCreateSettings? settings)
    {
        var mode = settings?.PerspectiveMode ?? PerspectiveAssistantMode.FreeQuad;
        var left = Math.Min(start.X, end.X);
        var right = Math.Max(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var bottom = Math.Max(start.Y, end.Y);
        var midX = (left + right) * 0.5;
        var midY = (top + bottom) * 0.5;

        var assistant = mode switch
        {
            PerspectiveAssistantMode.OnePoint => new PaintingAssistant
            {
                TypeId = PerspectiveType,
                PerspectiveMode = mode,
                HandleA = new Point(midX, midY),
                HandleB = new Point(right, midY),
            },
            PerspectiveAssistantMode.TwoPoint => new PaintingAssistant
            {
                TypeId = PerspectiveType,
                PerspectiveMode = mode,
                HandleA = new Point(left, midY),
                HandleB = new Point(right, midY),
                HandleC = new Point(midX, bottom),
            },
            PerspectiveAssistantMode.ThreePoint => new PaintingAssistant
            {
                TypeId = PerspectiveType,
                PerspectiveMode = mode,
                HandleA = new Point(left, midY),
                HandleB = new Point(right, midY),
                HandleC = new Point(midX, bottom),
            },
            _ => new PaintingAssistant
            {
                TypeId = PerspectiveType,
                PerspectiveMode = mode,
                HandleA = new Point(left, top),
                HandleB = new Point(right, top),
                HandleC = new Point(right, bottom),
                HandleD = new Point(left, bottom),
            },
        };

        return assistant;
    }

    private static AssistantCreateSettings MergeFisheyeSettings(AssistantCreateSettings? settings)
    {
        if (settings == null)
            return new AssistantCreateSettings(
                PerspectiveType,
                PerspectiveAssistantMode.OnePoint,
                FisheyeEnabled: true,
                180,
                4,
                SnapEnabled: true,
                CreateAtEditingLayer: true);

        return settings.Value with
        {
            TypeId = PerspectiveType,
            FisheyeEnabled = true,
        };
    }
}
