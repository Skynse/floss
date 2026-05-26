using System;
using Avalonia.Controls;

namespace Floss.App;

/// <summary>
/// Single source of truth for all application color constants.
/// All windows/controls should reference these instead of defining
/// local const strings — this eliminates the color drift that made
/// the app look visually inconsistent (some windows had #4878d8 accent,
/// others #3d6fd8, others #007acc).
/// </summary>
public static class AppColors
{
    // ── Background hierarchy ─────────────────────────────────────────────
    public const string Bg0      = "#1b1b1b";   // deepest bg (window chrome)
    public const string Bg1      = "#242424";   // panel/toolbar bg
    public const string Bg2      = "#2b2b2b";   // elevated surface (dockers, dialogs)
    public const string Bg3      = "#343434";   // hover/active surface
    public const string BgSidebar = "#202020";  // sidebar/panel rail

    // ── Borders ──────────────────────────────────────────────────────────
    public const string Stroke   = "#3c3c3c";

    // ── Text ─────────────────────────────────────────────────────────────
    public const string TextPrimary   = "#e2e2e2";
    public const string TextSecondary = "#b8b8b8";
    public const string TextMuted     = "#858585";

    // ── Accent ───────────────────────────────────────────────────────────
    public const string Accent     = "#4f78b8";
    public const string AccentSoft = "#30445f";  // muted accent bg
    public const string AccentWarm     = "#d28a45";
    public const string AccentWarmSoft = "#4a3524";

    // ── Semantic ─────────────────────────────────────────────────────────
    public const string Success = "#3fb950";
    public const string Warning = "#d29922";
    public const string Danger  = "#da3633";

    // ── Slider ───────────────────────────────────────────────────────────
    public const string SliderTrack      = "#141414";
    public const string SliderFill       = "#3a6bc9";
    public const string SliderFillHover  = "#4a7de0";
    public const string SliderFillActive = "#5a8ef5";
    public const string SliderThumb      = "#88a6d4";
    public const string SliderThumbLine  = "#7aa4ec";
}

public static class ScrollHelper
{
    public static ScrollViewer Create(Action<ScrollViewer>? configure = null)
    {
        var sv = new ScrollViewer { Padding = new Avalonia.Thickness(0, 0, 12, 0) };
        configure?.Invoke(sv);
        return sv;
    }
}
