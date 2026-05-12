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
    public const string Bg0      = "#0f0f10";   // deepest bg (window chrome)
    public const string Bg1      = "#161618";   // panel/toolbar bg
    public const string Bg2      = "#1e1e20";   // elevated surface (cards, dialogs)
    public const string Bg3      = "#252527";   // hover/active surface
    public const string BgSidebar = "#121214";  // sidebar/panel rail

    // ── Borders ──────────────────────────────────────────────────────────
    public const string Stroke   = "#2e2e32";

    // ── Text ─────────────────────────────────────────────────────────────
    public const string TextPrimary   = "#dde1e8";
    public const string TextSecondary = "#9ea8b4";
    public const string TextMuted     = "#5e6878";

    // ── Accent ───────────────────────────────────────────────────────────
    public const string Accent     = "#4878d8";
    public const string AccentSoft = "#1e2e52";  // muted accent bg
    public const string AccentWarm     = "#d87e3d";
    public const string AccentWarmSoft = "#3d2415";

    // ── Semantic ─────────────────────────────────────────────────────────
    public const string Success = "#3fb950";
    public const string Warning = "#d29922";
    public const string Danger  = "#da3633";

    // ── Slider ───────────────────────────────────────────────────────────
    public const string SliderTrack = "#161820";
    public const string SliderFill  = "#3a6fd8";
    public const string SliderThumb = "#6a94ec";
}
