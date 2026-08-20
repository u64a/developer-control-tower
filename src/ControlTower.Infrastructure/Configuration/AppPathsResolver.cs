using System;
using System.IO;
using ControlTower.Core.Configuration;

namespace ControlTower.Infrastructure.Configuration
{
    public static class AppPathsResolver
    {
        public const string AppFolderName = "DeveloperControlTower";
        private const string PortfolioFileName = "portfolio.yml";
        private const string SettingsFileName = "settings.yml";
        private const string ProfilesFileName = "profiles.yml";
        private const string ActiveProfileFileName = "active-profile.txt";
        private const string LibraryFolderName = "library";
        private const string LocalSettingsFileName = "settings.local.yml";
        private const string LegacyInstallFileName = "legacy-install-path.txt";

        public static AppPaths Resolve()
        {
            var configRoot = DetectConfigRoot();
            Directory.CreateDirectory(configRoot);
            var localStateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);

            return new AppPaths(
                configRoot,
                Path.Combine(configRoot, PortfolioFileName),
                Path.Combine(configRoot, SettingsFileName),
                Path.Combine(configRoot, ProfilesFileName),
                Path.Combine(localStateRoot, ActiveProfileFileName),
                Path.Combine(configRoot, LibraryFolderName),
                localStateRoot,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AppFolderName,
                    LocalSettingsFileName),
                Path.Combine(localStateRoot, LegacyInstallFileName));
        }

        /// <summary>
        /// Attempts migration from legacy locations. Call once at startup.
        /// </summary>
        public static void MigrateLegacyConfig(AppPaths paths, string legacyRepoRoot)
        {
            if (string.IsNullOrWhiteSpace(legacyRepoRoot))
            {
                return;
            }

            MigrateFile(
                Path.Combine(legacyRepoRoot, PortfolioFileName),
                paths.PortfolioPath);

            var legacySettings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName,
                SettingsFileName);

            MigrateFile(legacySettings, paths.GlobalSettingsPath);
        }

        private static string DetectConfigRoot()
        {
            // Business OneDrive (most common in enterprise)
            var oneDriveBiz = Environment.GetEnvironmentVariable("OneDriveCommercial");
            if (!string.IsNullOrWhiteSpace(oneDriveBiz) && Directory.Exists(oneDriveBiz))
            {
                return Path.Combine(oneDriveBiz, AppFolderName);
            }

            // Personal OneDrive
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrWhiteSpace(oneDrive) && Directory.Exists(oneDrive))
            {
                return Path.Combine(oneDrive, AppFolderName);
            }

            // Fallback to AppData
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName);
        }

        private static void MigrateFile(string source, string destination)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            {
                return;
            }

            if (!File.Exists(source) || File.Exists(destination))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination);
            }
            catch
            {
                // Migration is best-effort; don't crash the app
            }
        }
    }
}
