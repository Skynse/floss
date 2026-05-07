namespace Floss.App.Brushes;

public sealed record ParameterDynamics
{
    public bool PressureEnabled { get; init; } = false;
    public float[] CurveData { get; init; } = [0f, 0f, 1f, 1f];
    public float Min { get; init; } = 0.0f;
    public float Max { get; init; } = 1.0f;
    public bool VelocityEnabled { get; init; } = false;
    public float VelocityStrength { get; init; } = 0.3f;

    public static ParameterDynamics Off => new();

    public static ParameterDynamics DefaultSize => new()
    {
        PressureEnabled = true,
        CurveData = [0f, 0f, 0.4f, 0.25f, 1f, 1f],
        Min = 0.0f,
        Max = 1.0f,
        VelocityEnabled = true,
        VelocityStrength = 0.18f
    };

    public static ParameterDynamics DefaultOpacity => new()
    {
        PressureEnabled = true,
        CurveData = [0f, 0f, 0.4f, 0.25f, 1f, 1f],
        Min = 0.0f,
        Max = 1.0f,
        VelocityEnabled = false
    };
}
