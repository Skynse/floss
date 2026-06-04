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
    // ── Background hierarchy (warm charcoal — avoid pure black) ────────
    public const string Bg0      = "#1c1d21";   // deepest bg (window chrome)
    public const string Bg1      = "#24262b";   // panel/toolbar bg
    public const string Bg2      = "#2e3036";   // elevated surface (dockers, dialogs)
    public const string Bg3      = "#3a3d45";   // hover/active surface
    public const string BgSidebar = "#212328";  // tool rail / sidebar

    // ── Borders ──────────────────────────────────────────────────────────
    public const string Stroke   = "#40444d";

    // ── Text ─────────────────────────────────────────────────────────────
    public const string TextPrimary   = "#f0f2f5";
    public const string TextSecondary = "#c6c9cf";
    public const string TextMuted     = "#7f858e";

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
    public const string SliderTrack      = "#32353c";
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
