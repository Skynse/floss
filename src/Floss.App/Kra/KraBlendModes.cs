using Floss.App.Canvas.Compositing;

namespace Floss.App.Kra;

internal static class KraBlendModes
{
    public static BlendMode Map(string? compositeOp) => compositeOp?.Trim().ToLowerInvariant() switch
    {
        null or "" or "normal" or "over" => BlendMode.Normal,
        "pass through" => BlendMode.PassThrough,
        "dissolve" => BlendMode.Dissolve,
        "darken" => BlendMode.Darken,
        "multiply" or "mult" => BlendMode.Multiply,
        "burn" => BlendMode.ColorBurn,
        "linear_burn" => BlendMode.LinearBurn,
        "darker color" => BlendMode.DarkerColor,
        "lighten" => BlendMode.Lighten,
        "screen" => BlendMode.Screen,
        "dodge" or "dodge_hdr" => BlendMode.ColorDodge,
        "easy_dodge" or "easy dodge" => BlendMode.EasyDodge,
        "linear_dodge" => BlendMode.LinearDodge,
        "lighter color" => BlendMode.LighterColor,
        "overlay" => BlendMode.Overlay,
        "soft_light" or "soft_light_pegtop_delphi" or "soft_light_svg" or "soft_light_ifs_illusions" => BlendMode.SoftLight,
        "hard_light" => BlendMode.HardLight,
        "vivid_light" or "vivid_light_hdr" => BlendMode.VividLight,
        "linear light" => BlendMode.LinearLight,
        "pin_light" => BlendMode.PinLight,
        "hard mix" or "hard_mix_hdr" or "hard_mix_photoshop" or "hard_mix_softer_photoshop" => BlendMode.HardMix,
        "diff" => BlendMode.Difference,
        "exclusion" => BlendMode.Exclusion,
        "subtract" => BlendMode.Subtract,
        "divide" => BlendMode.Divide,
        "hue" or "hue_hsv" or "hue_hsl" or "hue_hsi" or "hue_hsy" => BlendMode.Hue,
        "saturation" or "saturation_hsv" or "saturation_hsl" or "saturation_hsi" or "saturation_hsy" => BlendMode.Saturation,
        "color" or "color_hsv" or "color_hsl" or "color_hsi" or "color_hsy" => BlendMode.Color,
        "luminize" or "lightness" or "value" or "luminosity_sai" => BlendMode.Luminosity,
        _ => BlendMode.Normal
    };
}
