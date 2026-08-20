#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using ControlTower.Core.Configuration;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Launch;
using Velopack.Locators;

namespace ControlTower.Infrastructure.Update
{
    /// <summary>
    /// Hands uninstall to Velopack, then removes only explicitly selected
    /// app-owned data after the uninstaller succeeds. Windows Settings
    /// bypasses this service and therefore always preserves user data.
    /// </summary>
    public sealed class VelopackUninstallService : IApplicationUninstallService
    {
        private readonly AppPaths _paths;
        private readonly string _updateExecutablePath;
        private readonly IShellLauncher _shellLauncher;
        private readonly Func<string> _tempPathProvider;

        internal VelopackUninstallService(
            AppPaths paths,
            string updateExecutablePath,
            IShellLauncher shellLauncher,
            Func<string>? tempPathProvider = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _updateExecutablePath = updateExecutablePath ?? string.Empty;
            _shellLauncher = shellLauncher ??
                throw new ArgumentNullException(nameof(shellLauncher));
            _tempPathProvider = tempPathProvider ?? Path.GetTempPath;
        }

        public bool IsAvailable =>
            IsSafeUpdateExecutable(_updateExecutablePath) &&
            TryValidateAppOwnedRoot(_paths.LocalStateRoot, out _) &&
            TryValidateAppOwnedRoot(_paths.ConfigRoot, out _);

        public static VelopackUninstallService? TryCreate(
            AppPaths paths,
            IShellLauncher shellLauncher)
        {
            try
            {
                if (!VelopackLocator.IsCurrentSet)
                {
                    return null;
                }

                var service = new VelopackUninstallService(
                    paths,
                    VelopackLocator.Current.UpdateExePath ?? string.Empty,
                    shellLauncher);
                return service.IsAvailable ? service : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public UninstallLaunchResult Launch(
            UninstallDataMode mode,
            int currentProcessId)
        {
            if (!IsAvailable)
            {
                return new UninstallLaunchResult(
                    false,
                    "Uninstall is available only from an installed release.");
            }

            if (currentProcessId <= 0)
            {
                return new UninstallLaunchResult(
                    false,
                    "The running app process could not be identified.");
            }

            string scriptPath;
            try
            {
                scriptPath = WriteUninstallScript(mode, currentProcessId);
            }
            catch (Exception ex)
            {
                return new UninstallLaunchResult(
                    false,
                    "Could not prepare the uninstall: " + ex.Message);
            }

            try
            {
                var processId = _shellLauncher.LaunchPowerShellScript(scriptPath);
                if (processId <= 0)
                {
                    TryDeleteFile(scriptPath);
                    return new UninstallLaunchResult(
                        false,
                        "The uninstall process could not be started.");
                }

                return new UninstallLaunchResult(
                    true,
                    "Uninstall started. The app will now close.");
            }
            catch (Exception ex)
            {
                TryDeleteFile(scriptPath);
                return new UninstallLaunchResult(
                    false,
                    "Could not start the uninstall process: " + ex.Message);
            }
        }

        internal string WriteUninstallScript(
            UninstallDataMode mode,
            int currentProcessId)
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException(
                    "The installed app paths could not be validated.");
            }

            var scriptFolder = Path.Combine(
                _tempPathProvider(),
                AppPathsResolver.AppFolderName,
                "uninstall");
            Directory.CreateDirectory(scriptFolder);
            var scriptPath = Path.Combine(
                scriptFolder,
                "uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");

            var cleanupTargets = BuildCleanupTargets(mode);
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$updateExe = " + QuotePowerShell(_updateExecutablePath));
            sb.AppendLine("$appPid = " + currentProcessId);
            sb.AppendLine("$scriptPath = $PSCommandPath");
            sb.AppendLine("try {");
            sb.AppendLine("    Wait-Process -Id $appPid -ErrorAction SilentlyContinue");
            sb.AppendLine("    $uninstaller = Start-Process -FilePath $updateExe -ArgumentList @('--silent', 'uninstall') -Wait -PassThru");
            sb.AppendLine("    if ($uninstaller.ExitCode -ne 0) {");
            sb.AppendLine("        throw \"Velopack uninstall exited with code $($uninstaller.ExitCode). No user data was removed.\"");
            sb.AppendLine("    }");
            foreach (var target in cleanupTargets)
            {
                sb.AppendLine("    if (Test-Path -LiteralPath " + QuotePowerShell(target) + ") {");
                sb.AppendLine("        Remove-Item -LiteralPath " + QuotePowerShell(target) + " -Recurse -Force");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            sb.AppendLine("catch {");
            sb.AppendLine("    try {");
            sb.AppendLine("        Add-Type -AssemblyName PresentationFramework");
            sb.AppendLine("        [System.Windows.MessageBox]::Show(");
            sb.AppendLine("            \"Developer Control Tower did not fully uninstall or clean up its selected data.`n`n$($_.Exception.Message)`n`nData that could not be removed has been left in place.\",");
            sb.AppendLine("            'Developer Control Tower uninstall',");
            sb.AppendLine("            [System.Windows.MessageBoxButton]::OK,");
            sb.AppendLine("            [System.Windows.MessageBoxImage]::Warning) | Out-Null");
            sb.AppendLine("    } catch { }");
            sb.AppendLine("    exit 1");
            sb.AppendLine("}");
            sb.AppendLine("finally {");
            sb.AppendLine("    Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("}");

            File.WriteAllText(
                scriptPath,
                sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return scriptPath;
        }

        internal IReadOnlyList<string> BuildCleanupTargets(UninstallDataMode mode)
        {
            if (!TryValidateAppOwnedRoot(_paths.LocalStateRoot, out var localRoot) ||
                !TryValidateAppOwnedRoot(_paths.ConfigRoot, out var configRoot))
            {
                throw new InvalidOperationException(
                    "An app data path is outside the expected user-owned location.");
            }

            var targets = new List<string> { localRoot };

            var roamingRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            var localOverride = Path.GetFullPath(_paths.LocalSettingsOverridePath);
            if (IsWithin(localOverride, roamingRoot))
            {
                targets.Add(localOverride);
            }

            if (mode == UninstallDataMode.RemovePortableConfigurationKeepLibrary)
            {
                targets.Add(Path.GetFullPath(_paths.PortfolioPath));
                targets.Add(Path.GetFullPath(_paths.GlobalSettingsPath));
                targets.Add(Path.GetFullPath(_paths.ProfilesPath));
                targets.Add(Path.Combine(
                    configRoot,
                    ProjectMetadataLocator.ManagedProjectsFolder));
            }
            else if (mode == UninstallDataMode.RemoveAllPortableData)
            {
                targets.Add(configRoot);
            }

            return targets;
        }

        private static bool TryValidateAppOwnedRoot(
            string path,
            out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                if (!string.Equals(
                    Path.GetFileName(fullPath),
                    AppPathsResolver.AppFolderName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (Directory.Exists(fullPath) &&
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var allowedParents = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetEnvironmentVariable("OneDriveCommercial"),
                    Environment.GetEnvironmentVariable("OneDrive")
                };
                foreach (var parent in allowedParents)
                {
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        continue;
                    }

                    var expected = Path.Combine(
                        Path.GetFullPath(parent),
                        AppPathsResolver.AppFolderName);
                    if (string.Equals(
                        fullPath,
                        expected.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException ||
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is SecurityException)
            {
                return false;
            }

            fullPath = string.Empty;
            return false;
        }

        private static bool IsWithin(string child, string parent)
        {
            if (string.IsNullOrWhiteSpace(child) ||
                string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            var normalizedParent = Path.GetFullPath(parent)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var normalizedChild = Path.GetFullPath(child);
            return normalizedChild.StartsWith(
                normalizedParent,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeUpdateExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFileName(path),
                    "Update.exe",
                    StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.GetFullPath(path));
            }
            catch
            {
                return false;
            }
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
