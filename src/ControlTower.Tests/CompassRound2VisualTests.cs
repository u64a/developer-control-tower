using System;
using System.Collections.Generic;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Tests;

/// <summary>
/// Tests pinning the visual-refresh logic from Compass round 2 (PR-B).
/// We test the pure helpers in Infrastructure — WPF rendering is static
/// review territory.
/// </summary>
public class CompassRound2VisualTests
{
    // ---- Mica gate ----------------------------------------------------

    [Theory]
    [InlineData(10, 0, 22000, true)]   // Win11 RTM
    [InlineData(10, 0, 22621, true)]   // Win11 22H2
    [InlineData(10, 0, 26100, true)]   // Win11 24H2
    [InlineData(10, 0, 19045, false)]  // Win10 22H2 — no Mica
    [InlineData(10, 0, 21390, false)]  // pre-22000 dev build
    [InlineData(6, 1, 7601, false)]    // Win7
    public void MicaSupportPolicy_Matches_KnownBuilds(int major, int minor, int build, bool expected)
    {
        var v = new Version(major, minor, build);
        Assert.Equal(expected, MicaSupportPolicy.IsSupported(v));
    }

    [Fact]
    public void MicaSupportPolicy_NullVersion_IsFalse()
    {
        Assert.False(MicaSupportPolicy.IsSupported(null));
    }

    [Fact]
    public void MicaSupportPolicy_ExactMinimumBuild_IsSupported()
    {
        Assert.True(MicaSupportPolicy.IsSupported(new Version(10, 0, MicaSupportPolicy.MinimumBuild)));
        Assert.False(MicaSupportPolicy.IsSupported(new Version(10, 0, MicaSupportPolicy.MinimumBuild - 1)));
    }

    // ---- System theme reader ------------------------------------------

    private sealed class FakeRegistry : IRegistryDword
    {
        private readonly Dictionary<string, int?> _values = new();
        public FakeRegistry Set(string keyPath, string valueName, int? v)
        {
            _values[keyPath + "::" + valueName] = v;
            return this;
        }
        public int? Read(string keyPath, string valueName)
            => _values.TryGetValue(keyPath + "::" + valueName, out var v) ? v : null;
    }

    [Fact]
    public void SystemThemeReader_Zero_Means_Dark()
    {
        var reg = new FakeRegistry().Set(SystemThemeReader.PersonalizeKey, SystemThemeReader.AppsUseLightValue, 0);
        Assert.Equal(SystemTheme.Dark, SystemThemeReader.Resolve(reg));
    }

    [Fact]
    public void SystemThemeReader_One_Means_Light()
    {
        var reg = new FakeRegistry().Set(SystemThemeReader.PersonalizeKey, SystemThemeReader.AppsUseLightValue, 1);
        Assert.Equal(SystemTheme.Light, SystemThemeReader.Resolve(reg));
    }

    [Fact]
    public void SystemThemeReader_Missing_Falls_Back()
    {
        var reg = new FakeRegistry(); // no values
        Assert.Equal(SystemTheme.Dark, SystemThemeReader.Resolve(reg, SystemTheme.Dark));
        Assert.Equal(SystemTheme.Light, SystemThemeReader.Resolve(reg, SystemTheme.Light));
    }

    [Fact]
    public void SystemThemeReader_NullRegistry_Falls_Back()
    {
        Assert.Equal(SystemTheme.Dark, SystemThemeReader.Resolve(null, SystemTheme.Dark));
    }

    // ---- Accent policy ------------------------------------------------

    [Theory]
    [InlineData("#0078D4", "#5AA6FF", "#0078D4")]   // OS wins when present
    [InlineData("",        "#5AA6FF", "#5AA6FF")]   // fallback when blank
    [InlineData(null,      "#5AA6FF", "#5AA6FF")]   // fallback when null
    [InlineData("   ",     "#5AA6FF", "#5AA6FF")]   // fallback when whitespace
    public void AccentPolicy_Resolves(string? os, string fallback, string expected)
    {
        Assert.Equal(expected, AccentPolicy.Resolve(os, fallback));
    }

    // ---- Settings subtitle (PR-A regression pin from H-04) -----------
    // Re-asserted here so PR-B's broader theme refactor doesn't silently
    // break the existing subtitle contract.

    [Fact]
    public void SettingsSubtitleFormatter_IncludesPath_WhenProvided()
    {
        var s = SettingsSubtitleFormatter.Format(@"D:\Profiles\example\AppData\controltower.settings.yml");
        Assert.Contains("Settings file:", s);
        Assert.Contains("controltower.settings.yml", s);
    }

    [Fact]
    public void SettingsSubtitleFormatter_NotResolved_WhenBlank()
    {
        Assert.Contains("(not resolved)", SettingsSubtitleFormatter.Format(""));
        Assert.Contains("(not resolved)", SettingsSubtitleFormatter.Format(null));
    }
}
