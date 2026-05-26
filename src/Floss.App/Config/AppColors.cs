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
    public const string Bg0      = "#131313";   // deepest bg (window chrome)
    public const string Bg1      = "#1a1a1a";   // panel/toolbar bg
    public const string Bg2      = "#212121";   // elevated surface (dockers, dialogs)
    public const string Bg3      = "#292929";   // hover/active surface
    public const string BgSidebar = "#171717";  // sidebar/panel rail

    // ── Borders ──────────────────────────────────────────────────────────
    public const string Stroke   = "#2e2e2e";

    // ── Text ─────────────────────────────────────────────────────────────
    public const string TextPrimary   = "#e0e0e0";
    public const string TextSecondary = "#aaaaaa";
    public const string TextMuted     = "#787878";

    // ── Accent ───────────────────────────────────────────────────────────
    public const string Accent     = "#4f78b8";
    public const string AccentSoft = "#263850";  // muted accent bg
    public const string AccentWarm     = "#d28a45";
    public const string AccentWarmSoft = "#3d2b1e";

    // ── Semantic ─────────────────────────────────────────────────────────
    public const string Success = "#3fb950";
    public const string Warning = "#d29922";
    public const string Danger  = "#da3633";

    // ── Slider ───────────────────────────────────────────────────────────
    public const string SliderTrack      = "#0d0d0d";
    public const string SliderFill       = "#3a6bc9";
    public const string SliderFillHover  = "#4a7de0";
    public const string SliderFillActive = "#5a8ef5";
    public const string SliderThumb      = "#88a6d4";
    public const string SliderThumbLine  = "#7aa4ec";

    // ── Corner radii ─────────────────────────────────────────────────────
    public const int SmRadius      = 4;
    public const int ControlRadius = 6;
    public const int CardRadius    = 8;
    public const int FullRadius    = 999;
}
