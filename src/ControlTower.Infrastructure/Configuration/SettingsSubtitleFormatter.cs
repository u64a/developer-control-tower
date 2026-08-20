namespace ControlTower.Infrastructure.Configuration
{
    /// <summary>
    /// Builds the subtitle string for the Settings window. The literal
    /// "synced via OneDrive" claim was misleading — the app performs no
    /// sync; the file lives wherever the resolved <c>AppPaths</c> says.
    /// We now surface the resolved path itself so what the user sees is
    /// the truth (copilot-instructions §2 — fail safely and visibly).
    /// </summary>
    public static class SettingsSubtitleFormatter
    {
        private const string Lead =
            "Configure repo stores, the asset library, and SSH credentials.";

        public static string Format(string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
            {
                return Lead + " Settings file: (not resolved)";
            }
            return Lead + " Settings file: " + settingsPath;
        }
    }
}
