namespace Floss.App.Kra;

internal static class KraBlendModes
{
    public static string Map(string? compositeOp) => compositeOp?.Trim().ToLowerInvariant() switch
    {
        null or "" or "normal" or "over" => "Normal",
        "pass through" => "PassThrough",
        "dissolve" => "Dissolve",
        "darken" => "Darken",
        "multiply" or "mult" => "Multiply",
        "burn" => "ColorBurn",
        "linear_burn" => "LinearBurn",
        "darker color" => "DarkerColor",
        "lighten" => "Lighten",
        "screen" => "Screen",
        "dodge" or "dodge_hdr" => "ColorDodge",
        "easy_dodge" or "easy dodge" => "EasyDodge",
        "linear_dodge" => "LinearDodge",
        "lighter color" => "LighterColor",
        "overlay" => "Overlay",
        "soft_light" or "soft_light_pegtop_delphi" or "soft_light_svg" or "soft_light_ifs_illusions" => "SoftLight",
        "hard_light" => "HardLight",
        "vivid_light" or "vivid_light_hdr" => "VividLight",
        "linear light" => "LinearLight",
        "pin_light" => "PinLight",
        "hard mix" or "hard_mix_hdr" or "hard_mix_photoshop" or "hard_mix_softer_photoshop" => "HardMix",
        "diff" => "Difference",
        "exclusion" => "Exclusion",
        "subtract" => "Subtract",
        "divide" => "Divide",
        "hue" or "hue_hsv" or "hue_hsl" or "hue_hsi" or "hue_hsy" => "Hue",
        "saturation" or "saturation_hsv" or "saturation_hsl" or "saturation_hsi" or "saturation_hsy" => "Saturation",
        "color" or "color_hsv" or "color_hsl" or "color_hsi" or "color_hsy" => "Color",
        "luminize" or "lightness" or "value" or "luminosity_sai" => "Luminosity",
        _ => "Normal"
    };
}
