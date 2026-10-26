using System;
using System.Linq;

namespace ControlTower.Core.Appearance
{
    /// <summary>
    /// Turns a <see cref="ThemeDefinition"/> into the concrete
    /// <see cref="ThemePalette"/> the desktop applies.
    /// </summary>
    public static class ThemeResolver
    {
        /// <summary>Hues closer than this read as the same Git state at a glance.</summary>
        public const double MinimumGitStateHueGap = 22;

        /// <summary>Below this saturation a status colour reads as grey.</summary>
        public const double MinimumGitStateSaturation = 0.18;

        public const double MinimumSecondaryTextContrast = 4.5;

        /// <summary>
        /// Resolves a theme. When <paramref name="keepGitStatesDistinct"/> is
        /// set and the theme's Clean / Uncommitted / Critical colours are not
        /// distinguishable, they are blended towards the built-in palette for
        /// the same mode so repository state stays readable.
        /// </summary>
        public static ThemePalette Resolve(ThemeDefinition theme, bool keepGitStatesDistinct)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));

            var palette = theme.BuiltInPalette ?? FromOmarchy(theme.OmarchyPalette);
            if (keepGitStatesDistinct && !theme.IsBuiltIn && HasIndistinctGitStates(palette))
            {
                palette = BlendGitStates(palette, ThemeCatalog.BuiltInFor(palette.Mode).BuiltInPalette);
            }
            return palette;
        }

        /// <summary>
        /// True when any pair of the success, caution and critical colours
        /// share a hue, or any of them is effectively grey.
        /// </summary>
        public static bool HasIndistinctGitStates(ThemePalette palette)
        {
            var states = new[] { palette.Success, palette.Caution, palette.Critical };
            if (states.Any(c => c.Saturation < MinimumGitStateSaturation))
            {
                return true;
            }
            return ThemeColor.HueGap(palette.Success, palette.Caution) < MinimumGitStateHueGap
                || ThemeColor.HueGap(palette.Success, palette.Critical) < MinimumGitStateHueGap
                || ThemeColor.HueGap(palette.Caution, palette.Critical) < MinimumGitStateHueGap;
        }

        /// <summary>Maps Omarchy <c>colors.toml</c> slots onto the app's brushes.</summary>
        public static ThemePalette FromOmarchy(OmarchyPalette p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            var dark = p.Mode == ThemeVariant.Dark;
            var subtleWeight = dark ? 0.20 : 0.14;
            var bg = p.Background;

            // Some themes put orange right next to red; fall back to yellow so
            // "uncommitted" and "critical" don't merge.
            var caution = ThemeColor.HueGap(p.Orange, p.Red) >= MinimumGitStateHueGap ? p.Orange : p.Yellow;
            var neutral = p.DarkForeground.ContrastWith(bg) >= 3 ? p.DarkForeground : p.LightForeground;

            var palette = new ThemePalette
            {
                Mode = p.Mode,
                WindowBackground = p.DarkBackground,
                PaneBackground = bg,
                CardBackground = bg,
                PanelSubtle = p.LighterBackground,
                CardBorder = p.Selection,
                SecondaryBorder = p.Muted,
                PrimaryText = p.BrightForeground,
                SecondaryText = ReadableOn(p.LightForeground, p.BrightForeground, bg, MinimumSecondaryTextContrast),
                StatusBackground = p.DarkBackground,
                TagBackground = p.LighterBackground,

                Accent = p.Accent,
                AccentForeground = ForegroundFor(p.Accent, p.DarkerBackground, p.BrightForeground),
                AccentSubtle = p.Accent.Mix(bg, dark ? 0.16 : 0.10),
                ListHover = p.Foreground.Mix(bg, dark ? 0.05 : 0.04),
                ListSelected = p.Accent.Mix(bg, dark ? 0.24 : 0.16),

                Success = p.Green,
                SuccessSubtle = p.Green.Mix(bg, subtleWeight),
                Caution = caution,
                CautionSubtle = caution.Mix(bg, subtleWeight),
                Critical = p.Red,
                CriticalSubtle = p.Red.Mix(bg, subtleWeight),
                Info = p.Blue,
                InfoSubtle = p.Blue.Mix(bg, subtleWeight),
                Neutral = neutral,
                NeutralSubtle = neutral.Mix(bg, subtleWeight),
                RepoAhead = p.Cyan,
                RepoAheadSubtle = p.Cyan.Mix(bg, subtleWeight),

                BrandStripStart = p.Accent,
                BrandStripMiddle = p.Cyan,
                BrandStripEnd = p.Green
            };
            return palette;
        }

        /// <summary>Best of the theme's own extremes for text on the accent, else black/white.</summary>
        public static ThemeColor ForegroundFor(ThemeColor accent, ThemeColor darkest, ThemeColor brightest)
        {
            var candidate = accent.ContrastWith(darkest) >= accent.ContrastWith(brightest) ? darkest : brightest;
            if (accent.ContrastWith(candidate) >= 4.5)
            {
                return candidate;
            }
            return accent.ContrastWith(ThemeColor.Black) > accent.ContrastWith(ThemeColor.White)
                ? ThemeColor.Black
                : ThemeColor.White;
        }

        /// <summary>Hover shade that moves away from the button text, so hovering never lowers its contrast.</summary>
        public static ThemeColor AccentHover(ThemeColor accent, ThemeColor foreground)
        {
            var delta = foreground.RelativeLuminance < accent.RelativeLuminance ? 24 : -24;
            return new ThemeColor(ShiftChannel(accent.R, delta), ShiftChannel(accent.G, delta), ShiftChannel(accent.B, delta));
        }

        private static byte ShiftChannel(byte value, int delta) => (byte)Math.Clamp(value + delta, 0, 255);

        /// <summary>Moves <paramref name="color"/> towards <paramref name="toward"/> until it meets the contrast target.</summary>
        private static ThemeColor ReadableOn(ThemeColor color, ThemeColor toward, ThemeColor background, double target)
        {
            for (var step = 0; step <= 10 && color.ContrastWith(background) < target; step++)
            {
                color = toward.Mix(color, step / 10.0);
            }
            return color;
        }

        private static ThemePalette BlendGitStates(ThemePalette theme, ThemePalette baseline)
        {
            var weight = theme.Mode == ThemeVariant.Dark ? 0.20 : 0.14;
            ThemeColor Blend(ThemeColor builtIn, ThemeColor own) => builtIn.Mix(own, 0.75);

            var success = Blend(baseline.Success, theme.Success);
            var caution = Blend(baseline.Caution, theme.Caution);
            var critical = Blend(baseline.Critical, theme.Critical);
            var info = Blend(baseline.Info, theme.Info);
            var ahead = Blend(baseline.RepoAhead, theme.RepoAhead);
            var bg = theme.PaneBackground;

            if (HasIndistinctGitStates(theme with { Success = success, Caution = caution, Critical = critical }))
            {
                // A light tint wasn't enough (e.g. monochrome themes): use the built-in colours as-is.
                (success, caution, critical, info, ahead) =
                    (baseline.Success, baseline.Caution, baseline.Critical, baseline.Info, baseline.RepoAhead);
            }

            return theme with
            {
                Success = success,
                SuccessSubtle = success.Mix(bg, weight),
                Caution = caution,
                CautionSubtle = caution.Mix(bg, weight),
                Critical = critical,
                CriticalSubtle = critical.Mix(bg, weight),
                Info = info,
                InfoSubtle = info.Mix(bg, weight),
                RepoAhead = ahead,
                RepoAheadSubtle = ahead.Mix(bg, weight)
            };
        }
    }
}
