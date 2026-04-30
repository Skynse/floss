namespace Floss.App.Brushes;

public sealed record ParameterDynamics
{
    public bool              PressureEnabled  { get; init; } = false;
    public ResponseCurveKind Kind             { get; init; } = ResponseCurveKind.Power;
    public float             Gamma            { get; init; } = 1.25f;
    public float             X1               { get; init; } = 0.25f;
    public float             Y1               { get; init; } = 0.25f;
    public float             X2               { get; init; } = 0.75f;
    public float             Y2               { get; init; } = 0.75f;
    public float             Min              { get; init; } = 0.0f;
    public float             Max              { get; init; } = 1.0f;
    public bool              VelocityEnabled  { get; init; } = false;
    public float             VelocityStrength { get; init; } = 0.3f;

    public static ParameterDynamics Off => new();

    public static ParameterDynamics DefaultSize => new()
    {
        PressureEnabled  = true,
        Gamma            = 1.25f,
        Min              = 0.0f,
        Max              = 1.0f,
        VelocityEnabled  = true,
        VelocityStrength = 0.18f
    };

    public static ParameterDynamics DefaultOpacity => new()
    {
        PressureEnabled  = true,
        Gamma            = 1.25f,
        Min              = 0.0f,
        Max              = 1.0f,
        VelocityEnabled  = false
    };
}
