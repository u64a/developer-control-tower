using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ControlTower.Core.Appearance;
using ControlTower.Desktop.Appearance;
using ControlTower.Desktop.Bootstrap;
using ControlTower.Desktop.Interop;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Desktop
{
    public partial class App : Application
    {
        public bool IsDarkMode { get; private set; }

        /// <summary>
        /// When true, the user has flipped the in-app theme toggle and we
        /// suppress further OS-driven theme changes until restart. Default
        /// behaviour (false) is auto-follow.
        /// </summary>
        public bool ManualThemeOverride { get; private set; }

        public CompositionRoot CompositionRoot { get; private set; }

        private SystemThemeObserver _themeObserver;
        private readonly HkcuRegistryDword _registry = new();
        private readonly ThemePreferenceStore _themePreferences = new();
        private readonly AccentPreferenceStore _accentPreferences = new();
        private AccentPreference _accentPreference = AccentPreference.TowerCyan;
        private readonly ThemeSelectionStore _themeSelections = new();
        private ThemeSelection _themeSelection = ThemeSelection.Default;
        private ThemePalette _palette = ThemeCatalog.BuiltInFor(ThemeVariant.Dark).BuiltInPalette;

        /// <summary>The user's current accent choice (theme accent or OS accent).</summary>
        public AccentPreference AccentPreference => _accentPreference;

        /// <summary>Which theme fills the dark and light slots.</summary>
        public ThemeSelection ThemeSelection => _themeSelection;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppLogger.Info("startup", "Developer Control Tower starting. Log file: " + AppLogger.CurrentLogFile);

            // Resolve the accent preference before the first ApplyTheme call
            // (ApplyTheme reseeds the accent), so the brand vs Windows-accent
            // choice is honoured on first paint.
            _accentPreference = _accentPreferences.Read();
            _themeSelection = _themeSelections.Read();

            // Resolve initial theme. A persisted user preference wins over
            // the OS setting and locks the session into manual-override mode
            // (so OS theme changes don't fight the user's explicit choice).
            // If no preference is on disk, follow the OS preference as before.
            var savedPreference = _themePreferences.Read();
            if (savedPreference == ThemePreference.Dark || savedPreference == ThemePreference.Light)
            {
                var wantDark = savedPreference == ThemePreference.Dark;
                ApplyTheme(wantDark);
                ManualThemeOverride = true;
                AppLogger.Info("startup", "Theme resolved from saved preference: " + (IsDarkMode ? "Dark" : "Light"));
            }
            else
            {
                var osTheme = SystemThemeReader.Resolve(_registry, SystemTheme.Dark);
                ApplyTheme(osTheme == SystemTheme.Dark);
                AppLogger.Info("startup", "Theme resolved from OS preference: " + (IsDarkMode ? "Dark" : "Light"));
            }

            _themeObserver = new SystemThemeObserver(OnSystemPreferenceChanged);
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;

            CompositionRoot = new CompositionRoot();
            AppLogger.Info("startup", "Settings path: " + CompositionRoot.SettingsPath);
            AppLogger.Info("startup", "Portfolio path: " + CompositionRoot.PortfolioPath);

            var window = new MainWindow(CompositionRoot);
            MainWindow = window;
            window.Show();
            AppLogger.Info("startup", "MainWindow shown.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Info("shutdown", "Developer Control Tower exiting.");
            _themeObserver?.Dispose();
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            base.OnExit(e);
        }

        private void OnSystemParameterChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyTheme(IsDarkMode)));
            }
        }

        private void OnSystemPreferenceChanged()
        {
            // Re-read accent on any preference change.
            ApplyAccentFromSystem();

            // Only auto-flip theme if the user hasn't manually overridden.
            if (ManualThemeOverride) return;

            var osTheme = SystemThemeReader.Resolve(_registry,
                IsDarkMode ? SystemTheme.Dark : SystemTheme.Light);
            var wantDark = osTheme == SystemTheme.Dark;
            if (wantDark != IsDarkMode)
            {
                ApplyTheme(wantDark);
            }
        }

        public void ToggleTheme()
        {
            // Manual toggle becomes an override; future OS theme changes
            // are ignored until app restart (matches Compass guidance —
            // "default behavior is auto, with override"). The override is
            // also persisted so it survives across launches; subsequent
            // sessions start in the chosen theme without flashing the OS
            // default first.
            ManualThemeOverride = true;
            ApplyTheme(!IsDarkMode);
            _themePreferences.Write(IsDarkMode ? ThemePreference.Dark : ThemePreference.Light);
        }

        public void ApplyTheme(bool darkMode)
        {
            IsDarkMode = darkMode;

            var mode = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
            var theme = ThemeCatalog.GetForMode(_themeSelection.IdFor(mode), mode);
            _palette = ThemeResolver.Resolve(theme, _themeSelection.KeepGitStatesDistinct);
            var p = _palette;

            SetWindowBackground(theme, p);
            SetBrush("PaneBackgroundBrush", p.PaneBackground);
            SetBrush("CardBackgroundBrush", p.CardBackground);
            SetBrush("PanelSubtleBrush", p.PanelSubtle);
            SetBrush("CardBorderBrush", p.CardBorder);
            SetBrush("SecondaryBorderBrush", p.SecondaryBorder);
            SetBrush("PrimaryTextBrush", p.PrimaryText);
            SetBrush("SecondaryTextBrush", p.SecondaryText);
            SetBrush("AccentSubtleBrush", p.AccentSubtle);
            SetBrush("ListHoverBrush", p.ListHover);
            SetBrush("ListSelectedBrush", p.ListSelected);
            SetBrush("StatusBackgroundBrush", p.StatusBackground);
            SetBrush("TagBackgroundBrush", p.TagBackground);

            SetBrush("SuccessBrush", p.Success);
            SetBrush("SuccessSubtleBrush", p.SuccessSubtle);
            SetBrush("CautionBrush", p.Caution);
            SetBrush("WarningBrush", p.Caution);
            SetBrush("CriticalBrush", p.Critical);
            SetBrush("CriticalSubtleBrush", p.CriticalSubtle);
            SetBrush("InfoBrush", p.Info);
            SetBrush("NeutralBrush", p.Neutral);
            SetBrush("PositiveBrush", p.Success);
            SetBrush("CautionSubtleBrush", p.CautionSubtle);
            SetBrush("InfoSubtleBrush", p.InfoSubtle);
            SetBrush("NeutralSubtleBrush", p.NeutralSubtle);
            SetBrush("RepoAheadBrush", p.RepoAhead);
            SetBrush("RepoAheadSubtleBrush", p.RepoAheadSubtle);

            var strip = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            strip.GradientStops.Add(new GradientStop(ToColor(p.BrandStripStart), 0));
            strip.GradientStops.Add(new GradientStop(ToColor(p.BrandStripMiddle), p.BrandStripFourth.HasValue ? 1.0 / 3 : 0.55));
            strip.GradientStops.Add(new GradientStop(ToColor(p.BrandStripEnd), p.BrandStripFourth.HasValue ? 2.0 / 3 : 1));
            if (p.BrandStripFourth is ThemeColor fourth)
            {
                strip.GradientStops.Add(new GradientStop(ToColor(fourth), 1));
            }
            strip.Freeze();
            Resources["BrandStripBrush"] = strip;

            // Status lamp glows follow the Git-state colours.
            SetGlow("SuccessGlowEffect", p.Success, 8, 0.8);
            SetGlow("AheadGlowEffect", p.RepoAhead, 8, 0.8);
            SetGlow("InfoGlowEffect", p.Info, 8, 0.8);
            SetGlow("CautionGlowEffect", p.Caution, 8, 0.8);
            SetGlow("CriticalGlowEffect", p.Critical, 9, 0.9);
            SetGlow("CriticalSoftGlowEffect", p.Critical, 8, 0.75);

            // Accent comes from the theme, or the OS when the user prefers it.
            ApplyAccentFromSystem();

            // Override WPF SystemColors so any control template that uses them
            // (e.g. TreeViewItem default selection, DataGrid headers, scrollbars)
            // picks up our theme rather than the OS blue/white.
            ApplySystemColorOverrides();

            foreach (Window window in Windows)
            {
                window.Background = (Brush)Resources["WindowBackgroundBrush"];
                window.Foreground = (Brush)Resources["PrimaryTextBrush"];
                MicaHelper.TryApply(window, darkMode);
            }
        }

        /// <summary>
        /// Applies and persists the per-mode theme choice from Settings ▸
        /// Appearance. Takes effect immediately.
        /// </summary>
        public void SetThemeSelection(ThemeSelection selection)
        {
            if (selection == null || selection == _themeSelection)
            {
                return;
            }

            _themeSelection = selection;
            ApplyTheme(IsDarkMode);
            _themeSelections.Write(selection);
            AppLogger.Info("appearance", "Theme selection set to dark=" + selection.DarkThemeId
                + ", light=" + selection.LightThemeId + ", distinct-git-states=" + selection.KeepGitStatesDistinct + ".");
        }
        // UI refresh: the "Tower" look pins a cyan brand accent to match the
        // approved mockup. The choice is now a persisted user setting
        // (Settings ▸ Appearance) read into _accentPreference at startup;
        // TowerCyan keeps the brand accent, WindowsAccent follows the OS.
        private void ApplyAccentFromSystem()
        {
            // TowerCyan means "the theme's own accent" (cyan for the built-ins).
            var accentTheme = _palette.Accent;
            var accentForegroundTheme = _palette.AccentForeground;
            if (_accentPreference == AccentPreference.WindowsAccent)
            {
                var osAccent = SystemAccentReader.TryReadAccent();
                if (osAccent.HasValue)
                {
                    accentTheme = new ThemeColor(osAccent.Value.R, osAccent.Value.G, osAccent.Value.B);
                }
                else
                {
                    // Brand fallback matches the previous hardcoded values.
                    accentTheme = ThemeColor.Parse(IsDarkMode ? "#5AA6FF" : "#0F6CBD");
                }
                // The OS accent can be light or dark, so pick readable text for it.
                accentForegroundTheme = ThemeResolver.ForegroundFor(accentTheme, _palette.WindowBackground, _palette.PrimaryText);
            }

            var accent = ToColor(accentTheme);
            var accentForegroundBrush = new SolidColorBrush(ToColor(accentForegroundTheme));
            accentForegroundBrush.Freeze();
            Resources["AccentForegroundBrush"] = accentForegroundBrush;

            var hover = ToColor(ThemeResolver.AccentHover(accentTheme, accentForegroundTheme));
            var light1 = Shift(accent, +20);
            var light2 = Shift(accent, +40);
            var dark1 = Shift(accent, -20);
            var dark2 = Shift(accent, -40);

            Resources["AccentBrush"] = new SolidColorBrush(accent);
            Resources["AccentBrushHover"] = new SolidColorBrush(hover);
            Resources["SystemAccentColorBrush"] = new SolidColorBrush(accent);
            Resources["SystemAccentColorLight1Brush"] = new SolidColorBrush(light1);
            Resources["SystemAccentColorLight2Brush"] = new SolidColorBrush(light2);
            Resources["SystemAccentColorDark1Brush"] = new SolidColorBrush(dark1);
            Resources["SystemAccentColorDark2Brush"] = new SolidColorBrush(dark2);
        }

        /// <summary>
        /// Applies and persists an accent choice from Settings ▸ Appearance.
        /// Re-seeds the accent brushes immediately so the change is live, then
        /// records the preference (best-effort) for the next launch.
        /// </summary>
        public void SetAccentPreference(AccentPreference preference)
        {
            if (_accentPreference == preference)
            {
                return;
            }

            _accentPreference = preference;
            ApplyAccentFromSystem();
            _accentPreferences.Write(preference);
            AppLogger.Info("appearance", "Accent preference set to " + preference + ".");
        }

        private static Color Shift(Color c, int delta)
        {
            int r = Math.Clamp(c.R + delta, 0, 255);
            int g = Math.Clamp(c.G + delta, 0, 255);
            int b = Math.Clamp(c.B + delta, 0, 255);
            return Color.FromArgb(c.A, (byte)r, (byte)g, (byte)b);
        }

        private void ApplySystemColorOverrides()
        {
            var pane = (SolidColorBrush)Resources["PaneBackgroundBrush"];
            var listSelected = (SolidColorBrush)Resources["ListSelectedBrush"];
            var listHover = (SolidColorBrush)Resources["ListHoverBrush"];
            var primaryText = (SolidColorBrush)Resources["PrimaryTextBrush"];
            var subtle = (SolidColorBrush)Resources["PanelSubtleBrush"];
            var border = (SolidColorBrush)Resources["CardBorderBrush"];

            Resources[SystemColors.HighlightBrushKey] = listSelected;
            Resources[SystemColors.HighlightColorKey] = listSelected.Color;
            Resources[SystemColors.HighlightTextBrushKey] = primaryText;
            Resources[SystemColors.HighlightTextColorKey] = primaryText.Color;
            Resources[SystemColors.InactiveSelectionHighlightBrushKey] = listHover;
            Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = primaryText;

            Resources[SystemColors.WindowBrushKey] = pane;
            Resources[SystemColors.WindowColorKey] = pane.Color;
            Resources[SystemColors.WindowTextBrushKey] = primaryText;
            Resources[SystemColors.WindowTextColorKey] = primaryText.Color;
            Resources[SystemColors.ControlBrushKey] = subtle;
            Resources[SystemColors.ControlColorKey] = subtle.Color;
            Resources[SystemColors.ControlTextBrushKey] = primaryText;
            Resources[SystemColors.ControlTextColorKey] = primaryText.Color;
            Resources[SystemColors.ControlLightBrushKey] = pane;
            Resources[SystemColors.ControlDarkBrushKey] = border;
            Resources[SystemColors.GrayTextBrushKey] = (SolidColorBrush)Resources["SecondaryTextBrush"];
        }

        private void SetBrush(string resourceKey, ThemeColor color)
        {
            Resources[resourceKey] = new SolidColorBrush(ToColor(color));
        }

        /// <summary>
        /// Sets the one brush that may carry a theme's decorative pattern.
        /// Every other surface (rows, cards, panels, dialog controls, text)
        /// keeps its own solid brush regardless, so this never affects
        /// readability. Switching to a theme without a pattern - or to a
        /// plain solid colour when Windows high contrast is on - always
        /// clears any previously applied pattern.
        /// </summary>
        private void SetWindowBackground(ThemeDefinition theme, ThemePalette p)
        {
            var background = ToColor(p.WindowBackground);
            if (theme.Pattern == ThemeDecorativePattern.None || SystemParameters.HighContrast)
            {
                SetBrush("WindowBackgroundBrush", p.WindowBackground);
                return;
            }

            Resources["WindowBackgroundBrush"] = theme.Pattern == ThemeDecorativePattern.Christmas
                ? HolidayPatternBrushes.Christmas(background)
                : HolidayPatternBrushes.Halloween(background);
        }

        private void SetGlow(string resourceKey, ThemeColor color, double blurRadius, double opacity)
        {
            var effect = new DropShadowEffect
            {
                Color = ToColor(color),
                BlurRadius = blurRadius,
                ShadowDepth = 0,
                Opacity = opacity
            };
            effect.Freeze();
            Resources[resourceKey] = effect;
        }

        private static Color ToColor(ThemeColor color) => Color.FromRgb(color.R, color.G, color.B);
    }
}
