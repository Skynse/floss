namespace Floss.App.Config;

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
    public const string Bg0      = "#181a1f";   // deepest bg (window chrome)
    public const string Bg1      = "#202227";   // panel/toolbar bg
    public const string Bg2      = "#282a30";   // elevated surface (dockers, dialogs)
    public const string Bg3      = "#343640";   // hover/active surface
    public const string BgSidebar = "#1c1e23";  // sidebar/panel rail

    // ── Borders ──────────────────────────────────────────────────────────
    public const string Stroke   = "#363840";

    // ── Text ─────────────────────────────────────────────────────────────
    public const string TextPrimary   = "#f0f2f5";
    public const string TextSecondary = "#d0d3d8";
    public const string TextMuted     = "#90959c";

    // ── Accent ───────────────────────────────────────────────────────────
    public const string Accent     = "#0078f2";
    public const string AccentSoft = "#0a4f9f";  // muted accent bg
    public const string AccentWarm     = "#d28a45";
    public const string AccentWarmSoft = "#3d2b1e";

    // ── Semantic ─────────────────────────────────────────────────────────
    public const string Success = "#3fb950";
    public const string Warning = "#d29922";
    public const string Danger  = "#da3633";

    // ── Slider ───────────────────────────────────────────────────────────
    public const string SliderTrack      = "#242730";
    public const string SliderFill       = "#4a7bdb";
    public const string SliderFillHover  = "#5b8ded";
    public const string SliderFillActive = "#6c9efa";
    public const string SliderThumb      = "#96b0df";
    public const string SliderThumbLine  = "#88afe8";

    // ── Corner radii ─────────────────────────────────────────────────────
    public const int SmRadius      = 4;
    public const int ControlRadius = 6;
    public const int CardRadius    = 8;
    public const int FullRadius    = 999;
}
