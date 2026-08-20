namespace ControlTower.Core.Configuration
{
    public sealed class AppPaths
    {
        public AppPaths(
            string configRoot,
            string portfolioPath,
            string globalSettingsPath,
            string profilesPath,
            string activeProfilePath,
            string defaultLibraryPath,
            string localStateRoot,
            string localSettingsOverridePath,
            string legacyInstallPath)
        {
            ConfigRoot = configRoot;
            PortfolioPath = portfolioPath;
            GlobalSettingsPath = globalSettingsPath;
            ProfilesPath = profilesPath;
            ActiveProfilePath = activeProfilePath;
            DefaultLibraryPath = defaultLibraryPath;
            LocalStateRoot = localStateRoot;
            LocalSettingsOverridePath = localSettingsOverridePath;
            LegacyInstallPath = legacyInstallPath;
        }

        /// <summary>Root folder for portable config (OneDrive or AppData fallback).</summary>
        public string ConfigRoot { get; }

        /// <summary>Full path to portfolio.yml.</summary>
        public string PortfolioPath { get; }

        /// <summary>Full path to the global settings.yml.</summary>
        public string GlobalSettingsPath { get; }

        /// <summary>Full path to the OneDrive-synced profiles.yml.</summary>
        public string ProfilesPath { get; }

        /// <summary>Full path to the machine-local active-profile.txt.</summary>
        public string ActiveProfilePath { get; }

        /// <summary>
        /// User-writable asset library used when settings do not provide an
        /// explicit path. Lives with the portable configuration rather than
        /// beside the replaceable application executable.
        /// </summary>
        public string DefaultLibraryPath { get; }

        /// <summary>Root for logs, cache, theme, and other per-machine state.</summary>
        public string LocalStateRoot { get; }

        /// <summary>
        /// Optional machine override loaded after the portable settings file.
        /// </summary>
        public string LocalSettingsOverridePath { get; }

        /// <summary>
        /// Machine-local pointer to the previous source-built installation,
        /// used only for non-destructive migration and cleanup guidance.
        /// </summary>
        public string LegacyInstallPath { get; }
    }
}
