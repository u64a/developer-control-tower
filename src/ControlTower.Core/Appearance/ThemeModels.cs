namespace ControlTower.Core.Appearance
{
    /// <summary>Whether a theme is designed for dark or light mode.</summary>
    public enum ThemeVariant
    {
        Dark,
        Light
    }

    /// <summary>
    /// Semantic identity of the subtle decorative pattern a theme asks for,
    /// if any. This names the motif only; the desktop project owns how (or
    /// whether) it actually renders one.
    /// </summary>
    public enum ThemeDecorativePattern
    {
        None,
        Christmas,
        Halloween
    }

    /// <summary>
    /// A fully resolved set of colours, one per brush the desktop app binds
    /// to. Every theme, built-in or imported, is reduced to this shape.
    /// </summary>
    public sealed record ThemePalette
    {
        public ThemeVariant Mode { get; init; }

        public ThemeColor WindowBackground { get; init; }
        public ThemeColor PaneBackground { get; init; }
        public ThemeColor CardBackground { get; init; }
        public ThemeColor PanelSubtle { get; init; }
        public ThemeColor CardBorder { get; init; }
        public ThemeColor SecondaryBorder { get; init; }
        public ThemeColor PrimaryText { get; init; }
        public ThemeColor SecondaryText { get; init; }
        public ThemeColor StatusBackground { get; init; }
        public ThemeColor TagBackground { get; init; }

        public ThemeColor Accent { get; init; }
        public ThemeColor AccentForeground { get; init; }
        public ThemeColor AccentSubtle { get; init; }
        public ThemeColor ListHover { get; init; }
        public ThemeColor ListSelected { get; init; }

        public ThemeColor Success { get; init; }
        public ThemeColor SuccessSubtle { get; init; }
        public ThemeColor Caution { get; init; }
        public ThemeColor CautionSubtle { get; init; }
        public ThemeColor Critical { get; init; }
        public ThemeColor CriticalSubtle { get; init; }
        public ThemeColor Info { get; init; }
        public ThemeColor InfoSubtle { get; init; }
        public ThemeColor Neutral { get; init; }
        public ThemeColor NeutralSubtle { get; init; }
        public ThemeColor RepoAhead { get; init; }
        public ThemeColor RepoAheadSubtle { get; init; }

        /// <summary>Colours of the thin brand strip under each title bar.</summary>
        public ThemeColor BrandStripStart { get; init; }
        public ThemeColor BrandStripMiddle { get; init; }
        public ThemeColor BrandStripEnd { get; init; }
        public ThemeColor? BrandStripFourth { get; init; }
    }

    /// <summary>
    /// The colour slots of an Omarchy <c>colors.toml</c> that the resolver
    /// maps from. Names follow the TOML keys.
    /// </summary>
    public sealed record OmarchyPalette
    {
        public ThemeVariant Mode { get; init; }
        public ThemeColor Accent { get; init; }
        public ThemeColor Selection { get; init; }
        public ThemeColor Muted { get; init; }
        public ThemeColor Background { get; init; }
        public ThemeColor DarkBackground { get; init; }
        public ThemeColor DarkerBackground { get; init; }
        public ThemeColor LighterBackground { get; init; }
        public ThemeColor Foreground { get; init; }
        public ThemeColor DarkForeground { get; init; }
        public ThemeColor LightForeground { get; init; }
        public ThemeColor BrightForeground { get; init; }
        public ThemeColor Red { get; init; }
        public ThemeColor Yellow { get; init; }
        public ThemeColor Orange { get; init; }
        public ThemeColor Green { get; init; }
        public ThemeColor Cyan { get; init; }
        public ThemeColor Blue { get; init; }
    }

    /// <summary>A selectable theme. Exactly one of the palettes is set.</summary>
    public sealed class ThemeDefinition
    {
        public ThemeDefinition(string id, string displayName, ThemePalette palette,
            ThemeDecorativePattern pattern = ThemeDecorativePattern.None)
        {
            Id = id;
            DisplayName = displayName;
            Mode = palette.Mode;
            BuiltInPalette = palette;
            Pattern = pattern;
        }

        public ThemeDefinition(string id, string displayName, OmarchyPalette palette)
        {
            Id = id;
            DisplayName = displayName;
            Mode = palette.Mode;
            OmarchyPalette = palette;
            Pattern = ThemeDecorativePattern.None;
        }

        /// <summary>Stable, lower-case kebab-case identifier persisted on disk.</summary>
        public string Id { get; }
        public string DisplayName { get; }
        public ThemeVariant Mode { get; }

        /// <summary>Set for the app's own hand-tuned themes.</summary>
        public ThemePalette BuiltInPalette { get; }

        /// <summary>Set for themes adapted from Omarchy.</summary>
        public OmarchyPalette OmarchyPalette { get; }

        /// <summary>
        /// The decorative motif, or None for a plain theme.
        /// </summary>
        public ThemeDecorativePattern Pattern { get; }

        public bool IsBuiltIn => BuiltInPalette != null;
        public string Source => IsBuiltIn ? "Developer Control Tower" : "Omarchy";

        public override string ToString() => DisplayName;
    }
}
