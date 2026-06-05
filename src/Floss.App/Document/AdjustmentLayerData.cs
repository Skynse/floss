namespace Floss.App.Document;

public enum AdjustmentKind
{
    BrightnessContrast,
    HueSaturationLuminosity,
    Posterization,
    LevelCorrection,
    ToneCurve,
    ColorBalance,
    Binarization,
    GradientMap,
    ReverseGradient,
}

public sealed class AdjustmentLayerData
{
    public AdjustmentKind Kind { get; set; }

    public float Brightness    { get; set; } = 0f;      // -255..255
    public float Contrast      { get; set; } = 0f;      // -100..100
    public float Hue           { get; set; } = 0f;      // -180..180
    public float Saturation    { get; set; } = 0f;      // -100..100
    public float Luminosity    { get; set; } = 0f;      // -100..100
    public int   Levels        { get; set; } = 4;       // 2..16
    public float LevelInBlack  { get; set; } = 0f;      // 0..255
    public float LevelInWhite  { get; set; } = 255f;    // 0..255
    public float LevelGamma    { get; set; } = 1f;      // 0.1..10
    public float LevelOutBlack { get; set; } = 0f;      // 0..255
    public float LevelOutWhite { get; set; } = 255f;    // 0..255
    // Tone curve per-channel: flat array [x0,y0, x1,y1, ...] in 0..255
    public float[] CurveAll    { get; set; } = [0f, 0f, 255f, 255f];
    public float[] CurveR      { get; set; } = [0f, 0f, 255f, 255f];
    public float[] CurveG      { get; set; } = [0f, 0f, 255f, 255f];
    public float[] CurveB      { get; set; } = [0f, 0f, 255f, 255f];
    public float ShadowR       { get; set; } = 0f;      // -100..100
    public float ShadowG       { get; set; } = 0f;
    public float ShadowB       { get; set; } = 0f;
    public float MidtoneR      { get; set; } = 0f;
    public float MidtoneG      { get; set; } = 0f;
    public float MidtoneB      { get; set; } = 0f;
    public float HighlightR    { get; set; } = 0f;
    public float HighlightG    { get; set; } = 0f;
    public float HighlightB    { get; set; } = 0f;
    public float Threshold     { get; set; } = 127f;    // 0..255
    // Gradient stops: flat [pos0,r0,g0,b0, pos1,r1,g1,b1, ...] all in 0..1
    public float[] GradientStops { get; set; } = [0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f];

    public AdjustmentLayerData Clone() => new()
    {
        Kind = Kind,
        Brightness = Brightness, Contrast = Contrast,
        Hue = Hue, Saturation = Saturation, Luminosity = Luminosity,
        Levels = Levels,
        LevelInBlack = LevelInBlack, LevelInWhite = LevelInWhite,
        LevelGamma = LevelGamma,
        LevelOutBlack = LevelOutBlack, LevelOutWhite = LevelOutWhite,
        CurveAll = (float[])CurveAll.Clone(),
        CurveR   = (float[])CurveR.Clone(),
        CurveG   = (float[])CurveG.Clone(),
        CurveB   = (float[])CurveB.Clone(),
        ShadowR = ShadowR, ShadowG = ShadowG, ShadowB = ShadowB,
        MidtoneR = MidtoneR, MidtoneG = MidtoneG, MidtoneB = MidtoneB,
        HighlightR = HighlightR, HighlightG = HighlightG, HighlightB = HighlightB,
        Threshold = Threshold,
        GradientStops = (float[])GradientStops.Clone(),
    };

    public static string DisplayName(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.BrightnessContrast      => "Brightness/Contrast",
        AdjustmentKind.HueSaturationLuminosity => "Hue/Sat/Luminosity",
        AdjustmentKind.Posterization           => "Posterization",
        AdjustmentKind.LevelCorrection         => "Level Correction",
        AdjustmentKind.ToneCurve               => "Tone Curve",
        AdjustmentKind.ColorBalance            => "Color Balance",
        AdjustmentKind.Binarization            => "Binarization",
        AdjustmentKind.GradientMap             => "Gradient Map",
        AdjustmentKind.ReverseGradient         => "Reverse Gradient",
        _                                      => kind.ToString()
    };
}
