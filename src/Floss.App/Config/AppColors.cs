namespace Floss.App.Config;

/// <summary>
/// Single source of truth for all application color constants.
/// Photoshop 2023–style neutral dark chrome: flat grays, muted accent used sparingly.
/// Keep <see cref="Styles.Theme.axaml"/> in sync.
/// </summary>
public static class AppColors
{
    // ── Background hierarchy (neutral charcoal) ─────────────────────────
    public const string Bg0      = "#1e1e1e";   // window chrome / canvas surround
    public const string Bg1      = "#323232";   // panel body, active dock tab
    public const string Bg2      = "#2b2b2b";   // tab strip, inputs, recessed
    public const string Bg3      = "#404040";   // hover surface
    public const string BgSidebar = "#2a2a2a";  // tool rail

    // ── Borders ──────────────────────────────────────────────────────────
    public const string Stroke   = "#474747";
    public const string StrokeSubtle = "#3a3a3a";

    // ── Text ─────────────────────────────────────────────────────────────
    public const string TextPrimary   = "#e8e8e8";
    public const string TextSecondary = "#b8b8b8";
    public const string TextMuted     = "#8a8a8a";

    // ── Accent (sparingly — focus, primary buttons, thin indicators) ───
    public const string Accent     = "#3d9eff";
    public const string AccentSoft = "#3d3d3d";  // selection fill (not blue wash)

    // ── List / row selection ─────────────────────────────────────────────
    public const string SelectionBg       = "#3a3a3a";
    public const string SelectionBgActive = "#454545";
    public const string SelectionBorder   = "#5a9fd8";

    public const string AccentWarm     = "#d28a45";
    public const string AccentWarmSoft = "#3d2b1e";

    // ── Semantic ─────────────────────────────────────────────────────────
    public const string Success = "#3fb950";
    public const string Warning = "#d29922";
    public const string Danger  = "#da3633";

    // ── Slider ───────────────────────────────────────────────────────────
    public const string SliderTrack      = "#3c3c3c";
    public const string SliderFill       = "#5a5a5a";
    public const string SliderFillHover  = "#6a6a6a";
    public const string SliderFillActive = "#3d9eff";
    public const string SliderThumb      = "#c8c8c8";
    public const string SliderThumbLine  = "#e0e0e0";

    // ── Corner radii (flat — Photoshop-like) ─────────────────────────────
    public const int SmRadius      = 2;
    public const int ControlRadius = 3;
    public const int CardRadius    = 4;
    public const int FullRadius    = 999;
}
