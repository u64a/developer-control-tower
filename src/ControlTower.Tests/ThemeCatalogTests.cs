using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ControlTower.Core.Appearance;

namespace ControlTower.Tests;

public class ThemeColorTests
{
    [Theory]
    [InlineData("#1A1B26")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    public void Parse_RoundTripsThroughUpperCaseHex(string hex)
    {
        Assert.Equal(hex, ThemeColor.Parse(hex.ToLowerInvariant()).ToHex());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1A1B26")]
    [InlineData("#1A1B2")]
    [InlineData("#1A1B266")]
    [InlineData("#GGGGGG")]
    [InlineData("#-12345")]
    public void TryParse_RejectsAnythingButStrictHex(string? hex)
    {
        Assert.False(ThemeColor.TryParse(hex, out _));
        Assert.Throws<FormatException>(() => ThemeColor.Parse(hex));
    }

    [Fact]
    public void Mix_WeightsTowardsThisColour()
    {
        Assert.Equal(ThemeColor.White, ThemeColor.White.Mix(ThemeColor.Black, 1));
        Assert.Equal(ThemeColor.Black, ThemeColor.White.Mix(ThemeColor.Black, 0));
        Assert.Equal(ThemeColor.Parse("#404040"), ThemeColor.White.Mix(ThemeColor.Black, 0.25));
    }

    [Fact]
    public void ContrastWith_MatchesWcagExtremes()
    {
        Assert.Equal(21, ThemeColor.Black.ContrastWith(ThemeColor.White), 3);
        Assert.Equal(1, ThemeColor.White.ContrastWith(ThemeColor.White), 3);
    }

    [Fact]
    public void HueGap_IsShortestWayRoundTheWheel()
    {
        var red = ThemeColor.Parse("#FF0000");
        var magentaRed = ThemeColor.Parse("#FF0040");
        var green = ThemeColor.Parse("#00FF00");

        Assert.Equal(120, ThemeColor.HueGap(red, green), 3);
        Assert.True(ThemeColor.HueGap(red, magentaRed) < 20);
        Assert.Equal(0, ThemeColor.Parse("#808080").Saturation);
    }
}

public class ThemeCatalogTests
{
    private static readonly string[] LightIds = { "control-tower-light", "nes-grey", "super-famicom", "christmas", "rose-pine", "catppuccin-latte", "flexoki-light", "lupine", "white" };

    public static IEnumerable<object[]> AllThemeIds() => ThemeCatalog.All.Select(t => new object[] { t.Id });

    [Fact]
    public void Catalog_HasBuiltInsPlusAllOmarchyThemes()
    {
        Assert.Equal(31, ThemeCatalog.All.Count);
        Assert.Equal(8, ThemeCatalog.All.Count(t => t.IsBuiltIn));
        Assert.Equal(ThemeCatalog.DefaultDarkId, ThemeCatalog.ForMode(ThemeVariant.Dark)[0].Id);
        Assert.Equal(ThemeCatalog.DefaultLightId, ThemeCatalog.ForMode(ThemeVariant.Light)[0].Id);
    }

    [Fact]
    public void Ids_AreUniqueAndSafeToPersist()
    {
        var ids = ThemeCatalog.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches(new Regex("^[a-z0-9-]{1,40}$"), id));
    }

    [Fact]
    public void Modes_MatchTheSourceThemes()
    {
        Assert.Equal(LightIds.OrderBy(i => i), ThemeCatalog.ForMode(ThemeVariant.Light).Select(t => t.Id).OrderBy(i => i));
        Assert.Equal(22, ThemeCatalog.ForMode(ThemeVariant.Dark).Count);
    }

    [Theory]
    [InlineData("tokyo-night", ThemeVariant.Dark, "tokyo-night")]
    [InlineData("tokyo-night", ThemeVariant.Light, "control-tower-light")]
    [InlineData("rose-pine", ThemeVariant.Dark, "control-tower-dark")]
    [InlineData("does-not-exist", ThemeVariant.Dark, "control-tower-dark")]
    [InlineData(null, ThemeVariant.Light, "control-tower-light")]
    public void GetForMode_FallsBackToBuiltInForUnknownOrWrongMode(string? id, ThemeVariant mode, string expected)
    {
        Assert.Equal(expected, ThemeCatalog.GetForMode(id, mode).Id);
    }

    [Theory]
    [InlineData("xbox-og", ThemeVariant.Dark, ThemeDecorativePattern.None)]
    [InlineData("ps3-xmb", ThemeVariant.Dark, ThemeDecorativePattern.None)]
    [InlineData("nes-grey", ThemeVariant.Light, ThemeDecorativePattern.None)]
    [InlineData("super-famicom", ThemeVariant.Light, ThemeDecorativePattern.None)]
    [InlineData("halloween", ThemeVariant.Dark, ThemeDecorativePattern.Halloween)]
    [InlineData("christmas", ThemeVariant.Light, ThemeDecorativePattern.Christmas)]
    public void ThemePacks_ResolveOnlyInTheirModeWithReadableContent(string id, ThemeVariant mode, ThemeDecorativePattern pattern)
    {
        var theme = ThemeCatalog.GetForMode(id, mode);
        Assert.Equal(id, theme.Id);
        Assert.Equal(pattern, theme.Pattern);
        var opposite = mode == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        Assert.Same(ThemeCatalog.BuiltInFor(opposite), ThemeCatalog.GetForMode(id, opposite));

        var palette = ThemeResolver.Resolve(theme, keepGitStatesDistinct: true);
        var surfaces = new[] { palette.WindowBackground, palette.PaneBackground, palette.CardBackground,
            palette.PanelSubtle, palette.ListSelected, palette.ListHover };
        foreach (var surface in surfaces)
        {
            Assert.True(palette.PrimaryText.ContrastWith(surface) >= 4.5, id + " primary text");
            Assert.True(palette.SecondaryText.ContrastWith(surface) >= 4.5, id + " secondary text");
        }
        var chips = new[] { (palette.Success, palette.SuccessSubtle), (palette.Caution, palette.CautionSubtle),
            (palette.Critical, palette.CriticalSubtle), (palette.Info, palette.InfoSubtle),
            (palette.Neutral, palette.NeutralSubtle), (palette.RepoAhead, palette.RepoAheadSubtle) };
        Assert.All(chips, chip => Assert.True(chip.Item1.ContrastWith(chip.Item2) >= 4.5, id + " status chip"));
        Assert.False(ThemeResolver.HasIndistinctGitStates(palette), id);
    }
}

public class ThemeResolverTests
{
    // Themes whose Clean / Uncommitted / Critical colours share a hue or are grey.
    private static readonly string[] ExpectedIndistinct =
    {
        "kanagawa", "matte-black", "hackerman", "ethereal", "lumon", "miasma", "vantablack",
        "retro-82", "last-horizon", "solitude", "starship", "white"
    };

    public static IEnumerable<object[]> AllThemesBothGuardSettings() =>
        ThemeCatalog.All.SelectMany(t => new[] { new object[] { t.Id, true }, new object[] { t.Id, false } });

    private static ThemePalette Resolve(string id, bool guard = true)
    {
        Assert.True(ThemeCatalog.TryGet(id, out var theme));
        return ThemeResolver.Resolve(theme, guard);
    }

    [Fact]
    public void BuiltInDark_KeepsTheShippedColours()
    {
        var p = Resolve(ThemeCatalog.DefaultDarkId, guard: true);

        Assert.Equal("#11161D", p.WindowBackground.ToHex());
        Assert.Equal("#18202B", p.PaneBackground.ToHex());
        Assert.Equal("#F2F5F8", p.PrimaryText.ToHex());
        Assert.Equal("#B8C2D4", p.SecondaryText.ToHex());
        Assert.Equal("#4CC2FF", p.Accent.ToHex());
        Assert.Equal("#11161D", p.AccentForeground.ToHex());   // dark text: white on cyan was ~2.1:1
        Assert.Equal("#7BD97B", p.Success.ToHex());
        Assert.Equal("#FFB26B", p.Caution.ToHex());
        Assert.Equal("#FF6E6E", p.Critical.ToHex());
        Assert.Equal("#45C7B8", p.RepoAhead.ToHex());
        Assert.Equal("#2BD9B0", p.BrandStripMiddle.ToHex());
    }

    [Fact]
    public void BuiltInLight_KeepsTheShippedColours()
    {
        var p = Resolve(ThemeCatalog.DefaultLightId, guard: true);

        Assert.Equal("#F3F5F8", p.WindowBackground.ToHex());
        Assert.Equal("#FFFFFF", p.PaneBackground.ToHex());
        Assert.Equal("#19202A", p.PrimaryText.ToHex());
        Assert.Equal("#0E7AB8", p.Accent.ToHex());
        Assert.Equal("#0E8A00", p.Success.ToHex());
        Assert.Equal("#D83B01", p.Caution.ToHex());
        Assert.Equal("#C42B1C", p.Critical.ToHex());
        Assert.Equal("#0E8C7E", p.RepoAhead.ToHex());
    }

    [Fact]
    public void TokyoNight_MapsColorsTomlSlots()
    {
        var p = Resolve("tokyo-night");

        Assert.Equal("#13141C", p.WindowBackground.ToHex());   // dark_bg
        Assert.Equal("#1A1B26", p.PaneBackground.ToHex());     // background
        Assert.Equal("#24283B", p.PanelSubtle.ToHex());        // lighter_bg
        Assert.Equal("#292E42", p.CardBorder.ToHex());         // selection
        Assert.Equal("#C0CAF5", p.PrimaryText.ToHex());        // bright_fg
        Assert.Equal("#7AA2F7", p.Accent.ToHex());
        Assert.Equal("#9ECE6A", p.Success.ToHex());            // green
        Assert.Equal("#EB927B", p.Caution.ToHex());            // orange, distinct from red
        Assert.Equal("#F7768E", p.Critical.ToHex());           // red
        Assert.Equal("#449DAB", p.RepoAhead.ToHex());          // cyan
        Assert.Equal("#0E0E14", p.AccentForeground.ToHex());   // darker_bg reads best on the accent
    }

    [Fact]
    public void Caution_FallsBackToYellowWhenOrangeSitsOnRed()
    {
        // Matte Black's orange (#c63d3d) is the same hue as its red.
        Assert.Equal("#B91C1C", Resolve("matte-black", guard: false).Caution.ToHex());
    }

    [Fact]
    public void IndistinctGitStates_DetectsExactlyTheExpectedThemes()
    {
        var flagged = ThemeCatalog.All
            .Where(t => !t.IsBuiltIn && ThemeResolver.HasIndistinctGitStates(ThemeResolver.FromOmarchy(t.OmarchyPalette)))
            .Select(t => t.Id)
            .OrderBy(i => i);

        Assert.Equal(ExpectedIndistinct.OrderBy(i => i), flagged);
    }

    [Fact]
    public void Guard_BlendsOnlyIndistinctThemes()
    {
        Assert.Equal("#76946A", Resolve("kanagawa", guard: false).Success.ToHex());
        Assert.NotEqual(Resolve("kanagawa", guard: false).Success, Resolve("kanagawa", guard: true).Success);
        Assert.Equal(Resolve("tokyo-night", guard: false), Resolve("tokyo-night", guard: true));
    }

    [Fact]
    public void Guard_LeavesNonStateColoursAlone()
    {
        var raw = Resolve("lumon", guard: false);
        var guarded = Resolve("lumon", guard: true);

        Assert.Equal(raw.PaneBackground, guarded.PaneBackground);
        Assert.Equal(raw.Accent, guarded.Accent);
        Assert.Equal(raw.PrimaryText, guarded.PrimaryText);
        Assert.Equal(raw.Neutral, guarded.Neutral);
    }

    [Theory]
    [MemberData(nameof(AllThemesBothGuardSettings))]
    public void EveryTheme_MeetsReadabilityFloors(string id, bool guard)
    {
        var p = Resolve(id, guard);

        Assert.True(p.PrimaryText.ContrastWith(p.PaneBackground) >= 4.5, id + " body text");
        Assert.True(p.SecondaryText.ContrastWith(p.PaneBackground) >= ThemeResolver.MinimumSecondaryTextContrast, id + " secondary text");
        Assert.True(p.AccentForeground.ContrastWith(p.Accent) >= 4.5, id + " accent button text");
        var hover = ThemeResolver.AccentHover(p.Accent, p.AccentForeground);
        Assert.True(p.AccentForeground.ContrastWith(hover) >= p.AccentForeground.ContrastWith(p.Accent), id + " hover keeps contrast");
    }

    [Theory]
    [InlineData("#5AA6FF")] // dark-mode fallback
    [InlineData("#0F6CBD")] // light-mode fallback
    [InlineData("#FFB900")] // bright Windows gold
    [InlineData("#0063B1")] // default Windows blue
    public void WindowsAccent_GetsReadableText(string accentHex)
    {
        var accent = ThemeColor.Parse(accentHex);
        foreach (var mode in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var p = ThemeResolver.Resolve(ThemeCatalog.BuiltInFor(mode), keepGitStatesDistinct: true);
            var foreground = ThemeResolver.ForegroundFor(accent, p.WindowBackground, p.PrimaryText);
            Assert.True(foreground.ContrastWith(accent) >= 4.5, accentHex + " " + mode);
        }
    }

    [Theory]
    [MemberData(nameof(AllThemesBothGuardSettings))]
    public void GuardedThemes_HaveDistinctGitStates(string id, bool guard)
    {
        Assert.True(ThemeCatalog.TryGet(id, out var theme));
        if (!guard || theme.IsBuiltIn)
        {
            return;
        }

        var p = ThemeResolver.Resolve(theme, guard);
        var baseline = ThemeCatalog.BuiltInFor(theme.Mode).BuiltInPalette;
        var usesBaseline = p.Success == baseline.Success && p.Caution == baseline.Caution && p.Critical == baseline.Critical;
        Assert.True(usesBaseline || !ThemeResolver.HasIndistinctGitStates(p), id);
    }

    [Theory]
    [InlineData("matte-black")]
    [InlineData("white")]
    public void Guard_FallsBackToBuiltInStatesWhenBlendingIsNotEnough(string id)
    {
        Assert.True(ThemeCatalog.TryGet(id, out var theme));
        var p = ThemeResolver.Resolve(theme, keepGitStatesDistinct: true);
        var baseline = ThemeCatalog.BuiltInFor(theme.Mode).BuiltInPalette;

        Assert.Equal(baseline.Success, p.Success);
        Assert.Equal(baseline.Critical, p.Critical);
        Assert.Equal(baseline.Success.Mix(p.PaneBackground, theme.Mode == ThemeVariant.Dark ? 0.20 : 0.14), p.SuccessSubtle);
    }
}
