using System;
using System.Windows;
using System.Windows.Media;
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

        /// <summary>The user's current accent choice (brand cyan or OS accent).</summary>
        public AccentPreference AccentPreference => _accentPreference;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppLogger.Info("startup", "Developer Control Tower starting. Log file: " + AppLogger.CurrentLogFile);

            // Resolve the accent preference before the first ApplyTheme call
            // (ApplyTheme reseeds the accent), so the brand vs Windows-accent
            // choice is honoured on first paint.
            _accentPreference = _accentPreferences.Read();

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
            base.OnExit(e);
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

            if (darkMode)
            {
                SetBrush("WindowBackgroundBrush", "#11161D");
                SetBrush("PaneBackgroundBrush", "#18202B");
                SetBrush("CardBackgroundBrush", "#18202B");
                SetBrush("PanelSubtleBrush", "#202938");
                SetBrush("CardBorderBrush", "#2F3A4B");
                SetBrush("SecondaryBorderBrush", "#3D4A5F");
                SetBrush("PrimaryTextBrush", "#F2F5F8");
                SetBrush("SecondaryTextBrush", "#B8C2D4");
                SetBrush("AccentSubtleBrush", "#17304D");
                SetBrush("ListHoverBrush", "#232D3D");
                SetBrush("ListSelectedBrush", "#1F3958");
                SetBrush("StatusBackgroundBrush", "#16202C");
                SetBrush("TagBackgroundBrush", "#202938");

                // Semantic palette (dark)
                SetBrush("SuccessBrush", "#7BD97B");
                SetBrush("SuccessSubtleBrush", "#1E3A1E");
                SetBrush("CautionBrush", "#FFB26B");
                SetBrush("WarningBrush", "#FFB26B");
                SetBrush("CriticalBrush", "#FF6E6E");
                SetBrush("CriticalSubtleBrush", "#3A1E1E");
                SetBrush("InfoBrush", "#5AA6FF");
                SetBrush("NeutralBrush", "#A6B2C4");
                SetBrush("PositiveBrush", "#7BD97B");
                SetBrush("CautionSubtleBrush", "#3A2C14");
                SetBrush("InfoSubtleBrush", "#15304D");
                SetBrush("NeutralSubtleBrush", "#262E3A");
                SetBrush("RepoAheadBrush", "#45C7B8");
                SetBrush("RepoAheadSubtleBrush", "#103833");
            }
            else
            {
                SetBrush("WindowBackgroundBrush", "#F3F5F8");
                SetBrush("PaneBackgroundBrush", "#FFFFFF");
                SetBrush("CardBackgroundBrush", "#FFFFFF");
                SetBrush("PanelSubtleBrush", "#F7F9FC");
                SetBrush("CardBorderBrush", "#D9E0EA");
                SetBrush("SecondaryBorderBrush", "#C9D2DF");
                SetBrush("PrimaryTextBrush", "#19202A");
                SetBrush("SecondaryTextBrush", "#5B6A7E");
                SetBrush("AccentSubtleBrush", "#EAF3FF");
                SetBrush("ListHoverBrush", "#F5F8FC");
                SetBrush("ListSelectedBrush", "#E8F1FB");
                SetBrush("StatusBackgroundBrush", "#F9FBFD");
                SetBrush("TagBackgroundBrush", "#F2F5F8");

                // Semantic palette (light)
                SetBrush("SuccessBrush", "#0E8A00");
                SetBrush("SuccessSubtleBrush", "#E6F4E0");
                SetBrush("CautionBrush", "#D83B01");
                SetBrush("WarningBrush", "#D83B01");
                SetBrush("CriticalBrush", "#C42B1C");
                SetBrush("CriticalSubtleBrush", "#FDE7E9");
                SetBrush("InfoBrush", "#005FB8");
                SetBrush("NeutralBrush", "#66758A");
                SetBrush("PositiveBrush", "#0E8A00");
                SetBrush("CautionSubtleBrush", "#FBEFE2");
                SetBrush("InfoSubtleBrush", "#E9F2FC");
                SetBrush("NeutralSubtleBrush", "#EDF0F4");
                SetBrush("RepoAheadBrush", "#0E8C7E");
                SetBrush("RepoAheadSubtleBrush", "#E0F4F1");
            }

            // Accent comes from the OS where possible; brand-blue fallback
            // is reseeded inside ApplyAccentFromSystem if SystemParameters
            // can't produce a color.
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

        // UI refresh: the "Tower" look pins a cyan brand accent to match the
        // approved mockup. The choice is now a persisted user setting
        // (Settings ▸ Appearance) read into _accentPreference at startup;
        // TowerCyan keeps the brand accent, WindowsAccent follows the OS.
        private void ApplyAccentFromSystem()
        {
            Color accent;
            if (_accentPreference == AccentPreference.TowerCyan)
            {
                accent = IsDarkMode
                    ? (Color)ColorConverter.ConvertFromString("#4CC2FF")
                    : (Color)ColorConverter.ConvertFromString("#0E7AB8");
            }
            else
            {
                var osAccent = SystemAccentReader.TryReadAccent();
                if (osAccent.HasValue)
                {
                    accent = osAccent.Value;
                }
                else
                {
                    // Brand fallback matches the previous hardcoded values.
                    accent = IsDarkMode
                        ? (Color)ColorConverter.ConvertFromString("#5AA6FF")
                        : (Color)ColorConverter.ConvertFromString("#0F6CBD");
                }
            }

            var hover = Shift(accent, IsDarkMode ? +24 : -24);
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

        private void SetBrush(string resourceKey, string colorHex)
        {
            Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }
    }
}
