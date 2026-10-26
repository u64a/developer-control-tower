using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlTower.Core.Appearance
{
    /// <summary>
    /// Every selectable theme: the two Control Tower built-ins, six further
    /// hand-tuned retro-console and holiday palettes (also Developer Control
    /// Tower's own), plus palettes adapted from Omarchy
    /// (https://github.com/omacom/omarchy, MIT, <c>themes/*/colors.toml</c>
    /// at commit c0b10882).
    /// </summary>
    public static class ThemeCatalog
    {
        public const string DefaultDarkId = "control-tower-dark";
        public const string DefaultLightId = "control-tower-light";

        private static readonly ThemeColor BrandCyan = ThemeColor.Parse("#4CC2FF");
        private static readonly ThemeColor BrandTeal = ThemeColor.Parse("#2BD9B0");
        private static readonly ThemeColor BrandGreen = ThemeColor.Parse("#4EF0A0");

        private static readonly ThemeDefinition ControlTowerDark = new(DefaultDarkId, "Control Tower Dark", new ThemePalette
        {
            Mode = ThemeVariant.Dark,
            WindowBackground = C("#11161D"),
            PaneBackground = C("#18202B"),
            CardBackground = C("#18202B"),
            PanelSubtle = C("#202938"),
            CardBorder = C("#2F3A4B"),
            SecondaryBorder = C("#3D4A5F"),
            PrimaryText = C("#F2F5F8"),
            SecondaryText = C("#B8C2D4"),
            StatusBackground = C("#16202C"),
            TagBackground = C("#202938"),
            Accent = BrandCyan,
            AccentForeground = C("#11161D"),
            AccentSubtle = C("#17304D"),
            ListHover = C("#232D3D"),
            ListSelected = C("#1F3958"),
            Success = C("#7BD97B"),
            SuccessSubtle = C("#1E3A1E"),
            Caution = C("#FFB26B"),
            CautionSubtle = C("#3A2C14"),
            Critical = C("#FF6E6E"),
            CriticalSubtle = C("#3A1E1E"),
            Info = C("#5AA6FF"),
            InfoSubtle = C("#15304D"),
            Neutral = C("#A6B2C4"),
            NeutralSubtle = C("#262E3A"),
            RepoAhead = C("#45C7B8"),
            RepoAheadSubtle = C("#103833"),
            BrandStripStart = BrandCyan,
            BrandStripMiddle = BrandTeal,
            BrandStripEnd = BrandGreen
        });

        private static readonly ThemeDefinition ControlTowerLight = new(DefaultLightId, "Control Tower Light", new ThemePalette
        {
            Mode = ThemeVariant.Light,
            WindowBackground = C("#F3F5F8"),
            PaneBackground = C("#FFFFFF"),
            CardBackground = C("#FFFFFF"),
            PanelSubtle = C("#F7F9FC"),
            CardBorder = C("#D9E0EA"),
            SecondaryBorder = C("#C9D2DF"),
            PrimaryText = C("#19202A"),
            SecondaryText = C("#5B6A7E"),
            StatusBackground = C("#F9FBFD"),
            TagBackground = C("#F2F5F8"),
            Accent = C("#0E7AB8"),
            AccentForeground = ThemeColor.White,
            AccentSubtle = C("#EAF3FF"),
            ListHover = C("#F5F8FC"),
            ListSelected = C("#E8F1FB"),
            Success = C("#0E8A00"),
            SuccessSubtle = C("#E6F4E0"),
            Caution = C("#D83B01"),
            CautionSubtle = C("#FBEFE2"),
            Critical = C("#C42B1C"),
            CriticalSubtle = C("#FDE7E9"),
            Info = C("#005FB8"),
            InfoSubtle = C("#E9F2FC"),
            Neutral = C("#66758A"),
            NeutralSubtle = C("#EDF0F4"),
            RepoAhead = C("#0E8C7E"),
            RepoAheadSubtle = C("#E0F4F1"),
            BrandStripStart = BrandCyan,
            BrandStripMiddle = BrandTeal,
            BrandStripEnd = BrandGreen
        });

        // Six original, hand-tuned retro-console and holiday palettes
        // (Developer Control Tower's own; no official console artwork or
        // logos). christmas and halloween also carry a subtle, semantic
        // decorative-pattern identity the desktop project renders.

        private static readonly ThemeDefinition XboxOg = new("xbox-og", "Xbox OG Dashboard", new ThemePalette
        {
            Mode = ThemeVariant.Dark,
            WindowBackground = C("#0B1008"),
            PaneBackground = C("#1a2818"),
            CardBackground = C("#202020"),
            PanelSubtle = C("#2a3a28"),
            CardBorder = C("#3d5938"),
            SecondaryBorder = C("#4a6a45"),
            PrimaryText = C("#E8FF7A"),
            SecondaryText = C("#B8CB86"),
            StatusBackground = C("#1a2818"),
            TagBackground = C("#2a3a28"),
            Accent = C("#88DD00"),
            AccentForeground = C("#000000"),
            AccentSubtle = C("#1f4a1a"),
            ListHover = C("#2a3a28"),
            ListSelected = C("#2d5a3a"),
            Success = C("#7BD97B"),
            SuccessSubtle = C("#1E3A1E"),
            Caution = C("#FFB26B"),
            CautionSubtle = C("#3A2C14"),
            Critical = C("#FF6E6E"),
            CriticalSubtle = C("#3A1E1E"),
            Info = C("#5AA6FF"),
            InfoSubtle = C("#15304D"),
            Neutral = C("#A6B2C4"),
            NeutralSubtle = C("#262E3A"),
            RepoAhead = C("#45C7B8"),
            RepoAheadSubtle = C("#103833"),
            BrandStripStart = C("#88DD00"),
            BrandStripMiddle = C("#66BB00"),
            BrandStripEnd = C("#44AA00")
        });

        private static readonly ThemeDefinition Ps3Xmb = new("ps3-xmb", "PS3 XMB", new ThemePalette
        {
            Mode = ThemeVariant.Dark,
            WindowBackground = C("#0F0319"),
            PaneBackground = C("#1a1a3a"),
            CardBackground = C("#242854"),
            PanelSubtle = C("#2d2d5d"),
            CardBorder = C("#3d3d7a"),
            SecondaryBorder = C("#4a4a8a"),
            PrimaryText = C("#E8D5FF"),
            SecondaryText = C("#C0A8D8"),
            StatusBackground = C("#1a1a3a"),
            TagBackground = C("#2d2d5d"),
            Accent = C("#B488FF"),
            AccentForeground = C("#000000"),
            AccentSubtle = C("#3d2d6a"),
            ListHover = C("#2d2d5d"),
            ListSelected = C("#3a3a7a"),
            Success = C("#7BD97B"),
            SuccessSubtle = C("#1E3A1E"),
            Caution = C("#FFB26B"),
            CautionSubtle = C("#3A2C14"),
            Critical = C("#FF6E6E"),
            CriticalSubtle = C("#3A1E1E"),
            Info = C("#5AA6FF"),
            InfoSubtle = C("#15304D"),
            Neutral = C("#A6B2C4"),
            NeutralSubtle = C("#262E3A"),
            RepoAhead = C("#45C7B8"),
            RepoAheadSubtle = C("#103833"),
            BrandStripStart = C("#B488FF"),
            BrandStripMiddle = C("#8866DD"),
            BrandStripEnd = C("#6644BB")
        });

        private static readonly ThemeDefinition NesGrey = new("nes-grey", "NES Console", new ThemePalette
        {
            Mode = ThemeVariant.Light,
            WindowBackground = C("#D9D9D9"),
            PaneBackground = C("#EFEFEF"),
            CardBackground = C("#FFFFFF"),
            PanelSubtle = C("#E8E8E8"),
            CardBorder = C("#B8B8B8"),
            SecondaryBorder = C("#A8A8A8"),
            PrimaryText = C("#2A2A2A"),
            SecondaryText = C("#5C5C5C"),
            StatusBackground = C("#F0F0F0"),
            TagBackground = C("#E8E8E8"),
            Accent = C("#CC3333"),
            AccentForeground = C("#FFFFFF"),
            AccentSubtle = C("#F5D9D9"),
            ListHover = C("#E8E8E8"),
            ListSelected = C("#D9D9D9"),
            Success = C("#236329"),
            SuccessSubtle = C("#E8F5E8"),
            Caution = C("#80510A"),
            CautionSubtle = C("#F5E8D9"),
            Critical = C("#B42128"),
            CriticalSubtle = C("#F5D9D9"),
            Info = C("#24518B"),
            InfoSubtle = C("#D9E8F5"),
            Neutral = C("#545454"),
            NeutralSubtle = C("#E0E0E0"),
            RepoAhead = C("#176557"),
            RepoAheadSubtle = C("#D9F5F0"),
            BrandStripStart = C("#CC3333"),
            BrandStripMiddle = C("#BB2222"),
            BrandStripEnd = C("#AA1111")
        });

        private static readonly ThemeDefinition SuperFamicom = new("super-famicom", "Super Famicom", new ThemePalette
        {
            Mode = ThemeVariant.Light,
            WindowBackground = C("#E0DED8"),
            PaneBackground = C("#F5F3F0"),
            CardBackground = C("#FFFFFF"),
            PanelSubtle = C("#EDE8E0"),
            CardBorder = C("#C8C0B8"),
            SecondaryBorder = C("#B8B0A8"),
            PrimaryText = C("#2A2825"),
            SecondaryText = C("#5F584F"),
            StatusBackground = C("#F8F6F3"),
            TagBackground = C("#EDE8E0"),
            Accent = C("#B83528"),
            AccentForeground = C("#FFFFFF"),
            AccentSubtle = C("#F5E0D9"),
            ListHover = C("#EDE8E0"),
            ListSelected = C("#E0D8D0"),
            Success = C("#236329"),
            SuccessSubtle = C("#E0F5E0"),
            Caution = C("#80510A"),
            CautionSubtle = C("#F5E8D0"),
            Critical = C("#B42128"),
            CriticalSubtle = C("#F5D0D0"),
            Info = C("#24518B"),
            InfoSubtle = C("#D9E8F5"),
            Neutral = C("#545454"),
            NeutralSubtle = C("#E8E0D8"),
            RepoAhead = C("#176557"),
            RepoAheadSubtle = C("#D8F0E8"),
            BrandStripStart = C("#D93636"),
            BrandStripMiddle = C("#E6B92E"),
            BrandStripEnd = C("#27864B"),
            BrandStripFourth = C("#315CB8")
        });

        private static readonly ThemeDefinition Halloween = new("halloween", "Halloween", new ThemePalette
        {
            Mode = ThemeVariant.Dark,
            WindowBackground = C("#1A0A0A"),
            PaneBackground = C("#2d1515"),
            CardBackground = C("#3a1f1f"),
            PanelSubtle = C("#452a2a"),
            CardBorder = C("#5a3a3a"),
            SecondaryBorder = C("#6a4545"),
            PrimaryText = C("#FFD9A8"),
            SecondaryText = C("#D9A575"),
            StatusBackground = C("#2d1515"),
            TagBackground = C("#452a2a"),
            Accent = C("#FF8833"),
            AccentForeground = C("#000000"),
            AccentSubtle = C("#5a2a1a"),
            ListHover = C("#452a2a"),
            ListSelected = C("#5a3a2a"),
            Success = C("#7BD97B"),
            SuccessSubtle = C("#1E3A1E"),
            Caution = C("#FFB26B"),
            CautionSubtle = C("#3A2C14"),
            Critical = C("#FF6E6E"),
            CriticalSubtle = C("#3A1E1E"),
            Info = C("#5AA6FF"),
            InfoSubtle = C("#15304D"),
            Neutral = C("#A6B2C4"),
            NeutralSubtle = C("#262E3A"),
            RepoAhead = C("#45C7B8"),
            RepoAheadSubtle = C("#103833"),
            BrandStripStart = C("#FF8833"),
            BrandStripMiddle = C("#7a3aaa"),
            BrandStripEnd = C("#5a2aaa")
        }, ThemeDecorativePattern.Halloween);

        private static readonly ThemeDefinition Christmas = new("christmas", "White Christmas", new ThemePalette
        {
            Mode = ThemeVariant.Light,
            WindowBackground = C("#F4F7FA"),
            PaneBackground = C("#FFFFFF"),
            CardBackground = C("#FFFFFF"),
            PanelSubtle = C("#EDF2F6"),
            CardBorder = C("#D5DFE5"),
            SecondaryBorder = C("#A5B8AF"),
            PrimaryText = C("#193C30"),
            SecondaryText = C("#55645F"),
            StatusBackground = C("#F8FAFC"),
            TagBackground = C("#EDF3EF"),
            Accent = C("#BE2839"),
            AccentForeground = C("#FFFFFF"),
            AccentSubtle = C("#FCECEF"),
            ListHover = C("#EDF4F0"),
            ListSelected = C("#E5EFE8"),
            Success = C("#236329"),
            SuccessSubtle = C("#E7F2E9"),
            Caution = C("#80510A"),
            CautionSubtle = C("#FBF0D8"),
            Critical = C("#B42128"),
            CriticalSubtle = C("#FBE7EA"),
            Info = C("#24518B"),
            InfoSubtle = C("#E7EEF8"),
            Neutral = C("#545454"),
            NeutralSubtle = C("#EDF0F2"),
            RepoAhead = C("#176557"),
            RepoAheadSubtle = C("#E3F2EB"),
            BrandStripStart = C("#BE2839"),
            BrandStripMiddle = C("#C89B3C"),
            BrandStripEnd = C("#236B49")
        }, ThemeDecorativePattern.Christmas);

        private static readonly IReadOnlyList<ThemeDefinition> Themes = new[]
        {
            ControlTowerDark,
            ControlTowerLight,
            XboxOg,
            Ps3Xmb,
            NesGrey,
            SuperFamicom,
            Halloween,
            Christmas,
            //                          mode          accent     selection  muted      background dark_bg    darker_bg  lighter_bg foreground dark_fg    light_fg   bright_fg  red        yellow     orange     green      cyan       blue
            O("tokyo-night", "Tokyo Night", ThemeVariant.Dark, "#7aa2f7", "#292e42", "#414868", "#1a1b26", "#13141c", "#0e0e14", "#24283b", "#a9b1d6", "#565f89", "#b4bee6", "#c0caf5", "#f7768e", "#e0af68", "#eb927b", "#9ece6a", "#449dab", "#7aa2f7"),
            O("catppuccin", "Catppuccin", ThemeVariant.Dark, "#89b4fa", "#45475a", "#585b70", "#1e1e2e", "#161622", "#101019", "#313244", "#cdd6f4", "#6c7086", "#bac2de", "#cdd6f4", "#f38ba8", "#f9e2af", "#f6b6ab", "#a6e3a1", "#94e2d5", "#89b4fa"),
            O("gruvbox", "Gruvbox", ThemeVariant.Dark, "#7daea3", "#504945", "#665c54", "#282828", "#1e1e1e", "#161616", "#3c3836", "#d4be98", "#7c6f64", "#bdae93", "#d4be98", "#ea6962", "#d8a657", "#e1875c", "#a9b665", "#89b482", "#7daea3"),
            O("kanagawa", "Kanagawa", ThemeVariant.Dark, "#dcd7ba", "#363646", "#54546D", "#1f1f28", "#17171e", "#111116", "#223249", "#dcd7ba", "#727169", "#c8c093", "#dcd7ba", "#c34043", "#c0a36e", "#c17158", "#76946a", "#6a9589", "#7e9cd8"),
            O("nord", "Nord", ThemeVariant.Dark, "#81a1c1", "#434c5e", "#4c566a", "#2e3440", "#222730", "#191c23", "#3b4252", "#d8dee9", "#667080", "#adb5c4", "#d8dee9", "#bf616a", "#ebcb8b", "#d5967a", "#a3be8c", "#88c0d0", "#81a1c1"),
            O("everforest", "Everforest", ThemeVariant.Dark, "#7fbbb3", "#3d484d", "#475258", "#2d353b", "#21272c", "#181d20", "#343f44", "#d3c6aa", "#4f585e", "#9da9a0", "#d3c6aa", "#e67e80", "#dbbc7f", "#e09d7f", "#a7c080", "#83c092", "#7fbbb3"),
            O("matte-black", "Matte Black", ThemeVariant.Dark, "#e68e0d", "#2a2a2a", "#333333", "#121212", "#0d0d0d", "#090909", "#1e1e1e", "#bebebe", "#555555", "#8a8a8d", "#bebebe", "#D35F5F", "#b91c1c", "#c63d3d", "#FFC107", "#bebebe", "#e68e0d"),
            O("ristretto", "Ristretto", ThemeVariant.Dark, "#f38d70", "#403e41", "#72696a", "#2c2525", "#211b1b", "#181414", "#3d2f2a", "#e6d9db", "#72696a", "#c3b7b8", "#e6d9db", "#fd6883", "#f9cc6c", "#fb9a77", "#adda78", "#85dacc", "#f38d70"),
            O("hackerman", "Hackerman", ThemeVariant.Dark, "#82FB9C", "#1f253a", "#2d3450", "#0B0C16", "#080910", "#06060c", "#151828", "#ddf7ff", "#6a6e95", "#b5c5db", "#ddf7ff", "#50f872", "#50f7d4", "#50f7a3", "#4fe88f", "#7cf8f7", "#829dd4"),
            O("ethereal", "Ethereal", ThemeVariant.Dark, "#7d82d9", "#252e56", "#6d7db6", "#060B1E", "#040816", "#030610", "#131a3a", "#ffcead", "#6d7db6", "#c9b8a6", "#ffcead", "#ED5B5A", "#E9BB4F", "#eb8b54", "#92a593", "#a3bfd1", "#7d82d9"),
            O("lumon", "Lumon", ThemeVariant.Dark, "#8bc9eb", "#243d56", "#304860", "#16242d", "#101b21", "#0b1216", "#1b2d40", "#d6e2ee", "#4d86b0", "#d6e2ee", "#f2fcff", "#4d86b0", "#6fa4c9", "#8bc9eb", "#5e95bc", "#b4e4f6", "#6fb8e3"),
            O("miasma", "Miasma", ThemeVariant.Dark, "#78824b", "#383838", "#666666", "#222222", "#191919", "#121212", "#2c2c2c", "#c2c2b0", "#555555", "#8a8a7e", "#c2c2b0", "#685742", "#b36d43", "#8d6242", "#5f875f", "#c9a554", "#78824b"),
            O("vantablack", "Vantablack", ThemeVariant.Dark, "#8d8d8d", "#1a1a1a", "#7a7a7a", "#000000", "#090909", "#070707", "#1a1a1a", "#ffffff", "#505050", "#ececec", "#ffffff", "#a4a4a4", "#cecece", "#b9b9b9", "#b6b6b6", "#b0b0b0", "#8d8d8d"),
            O("retro-82", "Retro 82", ThemeVariant.Dark, "#faa968", "#134e5a", "#2a6b78", "#05182e", "#031222", "#020c17", "#0a2540", "#f6dcac", "#3f8f8a", "#a7c9c6", "#f6dcac", "#f85525", "#e97b3c", "#faa968", "#028391", "#8cbfb8", "#3f8f8a"),
            O("jade-horse", "Jade Horse", ThemeVariant.Dark, "#8bcbb1", "#2b5147", "#64887b", "#142620", "#0f1e19", "#0a1713", "#203b31", "#dfede5", "#91ad9f", "#bed6c9", "#f0faf4", "#d88d91", "#c5cc91", "#d2ab8d", "#8bcbb1", "#86c7c5", "#96b7d0"),
            // Last Horizon, Solitude and White define no orange; red/yellow stand in.
            O("last-horizon", "Last Horizon", ThemeVariant.Dark, "#b59790", "#584e51", "#584e51", "#0c0b0c", "#090809", "#060606", "#0c0b0c", "#FAFCFB", "#584e51", "#cfd3cd", "#e2dddc", "#c38b7b", "#6B5E73", "#c38b7b", "#87a9b0", "#a5a0b6", "#b59790"),
            O("solitude", "Solitude", ThemeVariant.Dark, "#798186", "#343d41", "#4b4e55", "#101315", "#0c0e10", "#080a0b", "#101315", "#cacccc", "#4b4e55", "#cbc2be", "#a5aeb4", "#565d60", "#d9dbdc", "#d9dbdc", "#9fa5a9", "#707070", "#798186"),
            O("starship", "Starship", ThemeVariant.Dark, "#ff9f0a", "#3a3a3c", "#8e8e93", "#1c1c1e", "#161618", "#0e0e10", "#2c2c2e", "#f5f5f7", "#98989d", "#fbfbfd", "#ffffff", "#ff5f4a", "#ffc27a", "#ff6a3d", "#ff9f0a", "#a6dcef", "#8fb8de"),
            O("rose-pine", "Rosé Pine Dawn", ThemeVariant.Light, "#56949f", "#dfdad9", "#cecacd", "#faf4ed", "#ede7e1", "#e1dbd5", "#f2e9e1", "#575279", "#9893a5", "#6e6a86", "#575279", "#b4637a", "#ea9d34", "#cf8057", "#286983", "#d7827e", "#56949f"),
            O("catppuccin-latte", "Catppuccin Latte", ThemeVariant.Light, "#1e66f5", "#ccd0da", "#acb0be", "#eff1f5", "#e3e4e8", "#d7d8dc", "#dce0e8", "#4c4f69", "#9ca0b0", "#5c5f77", "#4c4f69", "#d20f39", "#df8e1d", "#d84e2b", "#40a02b", "#179299", "#1e66f5"),
            O("flexoki-light", "Flexoki Light", ThemeVariant.Light, "#205EA6", "#CECDC3", "#B7B5AC", "#FFFCF0", "#f2efe4", "#e5e2d8", "#E6E4D9", "#100F0F", "#878580", "#403E3C", "#100F0F", "#D14D41", "#D0A215", "#d0772b", "#879A39", "#3AA99F", "#205EA6"),
            O("lupine", "Lupine", ThemeVariant.Light, "#3264eb", "#d0d0d0", "#9e9e9e", "#fafafa", "#ececec", "#dedede", "#f5f5f5", "#212121", "#757575", "#424242", "#000000", "#c900c4", "#026fde", "#026fde", "#4a2fd0", "#0c67de", "#3264eb"),
            O("white", "White", ThemeVariant.Light, "#6e6e6e", "#c0c0c0", "#808080", "#ffffff", "#f5f5f5", "#e8e8e8", "#c0c0c0", "#000000", "#c0c0c0", "#000000", "#000000", "#2a2a2a", "#4a4a4a", "#4a4a4a", "#3a3a3a", "#3e3e3e", "#1a1a1a"),
        };

        private static readonly Dictionary<string, ThemeDefinition> ById =
            Themes.ToDictionary(t => t.Id, StringComparer.Ordinal);

        /// <summary>All themes, built-ins first.</summary>
        public static IReadOnlyList<ThemeDefinition> All => Themes;

        /// <summary>Themes designed for <paramref name="mode"/>, built-in first.</summary>
        public static IReadOnlyList<ThemeDefinition> ForMode(ThemeVariant mode) =>
            Themes.Where(t => t.Mode == mode).ToList();

        public static ThemeDefinition BuiltInFor(ThemeVariant mode) =>
            mode == ThemeVariant.Dark ? ControlTowerDark : ControlTowerLight;

        public static bool TryGet(string id, out ThemeDefinition theme)
        {
            theme = null;
            return id != null && ById.TryGetValue(id, out theme);
        }

        /// <summary>
        /// Returns the theme for <paramref name="id"/> if it exists and suits
        /// <paramref name="mode"/>; otherwise the built-in theme for that mode.
        /// </summary>
        public static ThemeDefinition GetForMode(string id, ThemeVariant mode) =>
            TryGet(id, out var theme) && theme.Mode == mode ? theme : BuiltInFor(mode);

        private static ThemeColor C(string hex) => ThemeColor.Parse(hex);

        private static ThemeDefinition O(
            string id, string name, ThemeVariant mode,
            string accent, string selection, string muted,
            string background, string darkBackground, string darkerBackground, string lighterBackground,
            string foreground, string darkForeground, string lightForeground, string brightForeground,
            string red, string yellow, string orange, string green, string cyan, string blue)
        {
            return new ThemeDefinition(id, name, new OmarchyPalette
            {
                Mode = mode,
                Accent = C(accent),
                Selection = C(selection),
                Muted = C(muted),
                Background = C(background),
                DarkBackground = C(darkBackground),
                DarkerBackground = C(darkerBackground),
                LighterBackground = C(lighterBackground),
                Foreground = C(foreground),
                DarkForeground = C(darkForeground),
                LightForeground = C(lightForeground),
                BrightForeground = C(brightForeground),
                Red = C(red),
                Yellow = C(yellow),
                Orange = C(orange),
                Green = C(green),
                Cyan = C(cyan),
                Blue = C(blue)
            });
        }
    }
}
