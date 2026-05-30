using System;
using System.Runtime.CompilerServices;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// Drawpile blend mode enum. Replaces all string-based blend mode dispatch.
/// Values match the order used in LayerCompositorPixelOps and KraBlendModes.
/// </summary>
public enum BlendMode : byte
{
    Normal,
    PassThrough,
    Dissolve,
    Multiply,
    Screen,
    Overlay,
    SoftLight,
    HardLight,
    ColorDodge,
    ColorBurn,
    EasyDodge,
    Darken,
    Lighten,
    Difference,
    Exclusion,
    LinearBurn,
    LinearDodge,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,
    Subtract,
    Divide,
    DarkerColor,
    LighterColor,
    Hue,
    Saturation,
    Color,
    Luminosity,
    // Alpha-preserving variant of Normal (Drawpile: DP_BLEND_MODE_RECOLOR)
    NormalAlphaPreserving,
    // Brush-specific modes
    Erase,
    SrcOver,
    DstOut,
    Clear,
    // Pigment modes (future)
    Pigment,
    PigmentAlpha,
    PigmentAndEraser,
    // OKLAB modes (future)
    OklabNormal,
    OklabNormalAndEraser,
    OklabRecolor,
}

public static class BlendModeExtensions
{
    /// <summary>Drawpile DP_blend_mode_preserves_alpha: true for alpha-preserving modes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool PreservesAlpha(this BlendMode mode) => mode switch
    {
        BlendMode.NormalAlphaPreserving => true,
        BlendMode.Multiply => true,
        BlendMode.Screen => true,
        BlendMode.Overlay => true,
        BlendMode.SoftLight => true,
        BlendMode.HardLight => true,
        BlendMode.ColorDodge => true,
        BlendMode.ColorBurn => true,
        BlendMode.EasyDodge => true,
        BlendMode.Darken => true,
        BlendMode.Lighten => true,
        BlendMode.Difference => true,
        BlendMode.Exclusion => true,
        BlendMode.LinearBurn => true,
        BlendMode.LinearDodge => true,
        BlendMode.VividLight => true,
        BlendMode.LinearLight => true,
        BlendMode.PinLight => true,
        BlendMode.HardMix => true,
        BlendMode.Subtract => true,
        BlendMode.Divide => true,
        BlendMode.DarkerColor => true,
        BlendMode.LighterColor => true,
        BlendMode.Hue => true,
        BlendMode.Saturation => true,
        BlendMode.Color => true,
        BlendMode.Luminosity => true,
        _ => false,
    };

    /// <summary>
    /// Drawpile DP_blend_mode_clip: returns alpha-preserving variant when clip=true.
    /// For Normal → NormalAlphaPreserving (ReColor).
    /// For already alpha-preserving modes → same mode.
    /// For alpha-affecting modes → same mode (they'll be applied as alpha-preserving in clip context).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BlendMode Clip(this BlendMode mode, bool clip)
        => clip ? (mode == BlendMode.Normal ? BlendMode.NormalAlphaPreserving : mode) : mode;

    /// <summary>Drawpile DP_blend_mode_can_decrease_opacity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanDecreaseOpacity(this BlendMode mode) => mode switch
    {
        BlendMode.Erase => true,
        BlendMode.DstOut => true,
        BlendMode.Clear => true,
        BlendMode.Normal => true,
        BlendMode.NormalAlphaPreserving => false,
        _ => false,
    };

    /// <summary>Check if mode has a precomputed LUT.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasLut(this BlendMode mode) => mode switch
    {
        BlendMode.Multiply => true,
        BlendMode.Screen => true,
        BlendMode.Overlay => true,
        BlendMode.SoftLight => true,
        BlendMode.HardLight => true,
        BlendMode.ColorDodge => true,
        BlendMode.ColorBurn => true,
        BlendMode.LinearBurn => true,
        BlendMode.LinearDodge => true,
        BlendMode.VividLight => true,
        BlendMode.LinearLight => true,
        BlendMode.PinLight => true,
        BlendMode.HardMix => true,
        BlendMode.Subtract => true,
        BlendMode.Divide => true,
        _ => false,
    };

    /// <summary>Check if mode requires HSL/double-precision math.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHslMode(this BlendMode mode) => mode switch
    {
        BlendMode.DarkerColor => true,
        BlendMode.LighterColor => true,
        BlendMode.Hue => true,
        BlendMode.Saturation => true,
        BlendMode.Color => true,
        BlendMode.Luminosity => true,
        _ => false,
    };

    /// <summary>Parse from legacy string name.</summary>
    public static BlendMode FromString(string name) => name switch
    {
        "Normal" => BlendMode.Normal,
        "PassThrough" => BlendMode.PassThrough,
        "Dissolve" => BlendMode.Dissolve,
        "Multiply" => BlendMode.Multiply,
        "Screen" => BlendMode.Screen,
        "Overlay" => BlendMode.Overlay,
        "SoftLight" => BlendMode.SoftLight,
        "HardLight" => BlendMode.HardLight,
        "ColorDodge" => BlendMode.ColorDodge,
        "ColorBurn" => BlendMode.ColorBurn,
        "EasyDodge" => BlendMode.EasyDodge,
        "Darken" => BlendMode.Darken,
        "Lighten" => BlendMode.Lighten,
        "Difference" => BlendMode.Difference,
        "Exclusion" => BlendMode.Exclusion,
        "LinearBurn" => BlendMode.LinearBurn,
        "LinearDodge" => BlendMode.LinearDodge,
        "VividLight" => BlendMode.VividLight,
        "LinearLight" => BlendMode.LinearLight,
        "PinLight" => BlendMode.PinLight,
        "HardMix" => BlendMode.HardMix,
        "Subtract" => BlendMode.Subtract,
        "Divide" => BlendMode.Divide,
        "DarkerColor" => BlendMode.DarkerColor,
        "LighterColor" => BlendMode.LighterColor,
        "Hue" => BlendMode.Hue,
        "Saturation" => BlendMode.Saturation,
        "Color" => BlendMode.Color,
        "Luminosity" => BlendMode.Luminosity,
        _ => BlendMode.Normal,
    };

    /// <summary>Convert to legacy string name.</summary>
    public static string ToLegacyString(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => "Normal",
        BlendMode.PassThrough => "PassThrough",
        BlendMode.Dissolve => "Dissolve",
        BlendMode.Multiply => "Multiply",
        BlendMode.Screen => "Screen",
        BlendMode.Overlay => "Overlay",
        BlendMode.SoftLight => "SoftLight",
        BlendMode.HardLight => "HardLight",
        BlendMode.ColorDodge => "ColorDodge",
        BlendMode.ColorBurn => "ColorBurn",
        BlendMode.EasyDodge => "EasyDodge",
        BlendMode.Darken => "Darken",
        BlendMode.Lighten => "Lighten",
        BlendMode.Difference => "Difference",
        BlendMode.Exclusion => "Exclusion",
        BlendMode.LinearBurn => "LinearBurn",
        BlendMode.LinearDodge => "LinearDodge",
        BlendMode.VividLight => "VividLight",
        BlendMode.LinearLight => "LinearLight",
        BlendMode.PinLight => "PinLight",
        BlendMode.HardMix => "HardMix",
        BlendMode.Subtract => "Subtract",
        BlendMode.Divide => "Divide",
        BlendMode.DarkerColor => "DarkerColor",
        BlendMode.LighterColor => "LighterColor",
        BlendMode.Hue => "Hue",
        BlendMode.Saturation => "Saturation",
        BlendMode.Color => "Color",
        BlendMode.Luminosity => "Luminosity",
        BlendMode.NormalAlphaPreserving => "NormalAlphaPreserving",
        _ => "Normal",
    };
}
