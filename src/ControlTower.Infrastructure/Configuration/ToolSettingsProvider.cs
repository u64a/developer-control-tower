using System;
using System.Collections.Generic;
using System.IO;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Yaml.Dto;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ControlTower.Infrastructure.Configuration
{
    public sealed class ToolSettingsProvider
    {
        private static readonly IReadOnlyList<string> AllowedRoots = AllowedSettingsRoots.GetAllowedRoots();

        public ToolSettings Load(string globalSettingsPath)
        {
            var settings = new ToolSettings();

            // Layer 1: synced/global settings (OneDrive or wherever AppPaths points)
            if (!string.IsNullOrWhiteSpace(globalSettingsPath))
            {
                ApplySettingsFile(settings, globalSettingsPath);
            }

            // Layer 2: machine-local override (AppData)
            var localOverride = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeveloperControlTower",
                "settings.local.yml");
            ApplySettingsFile(settings, localOverride);

            settings.VsCodeCommand = ResolveCommand(settings.VsCodeCommand, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
                "code.exe",
                "code.cmd"
            });
            settings.GitCommand = ResolveCommand(settings.GitCommand, new[]
            {
                "git.exe",
                "git.cmd"
            });
            settings.SshCommand = ResolveCommand(settings.SshCommand, new[]
            {
                "ssh.exe",
                Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe")
            });
            settings.TerminalCommand = ResolveCommand(settings.TerminalCommand, new[]
            {
                "wt.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe")
            });
            settings.PowerShellCommand = ResolveCommand(settings.PowerShellCommand, new[]
            {
                "pwsh.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            });
            if (string.IsNullOrWhiteSpace(settings.PowerShellCommand))
            {
                settings.PowerShellCommand = "powershell.exe";
            }

            settings.LaunchEnvironments = BuildLaunchCatalog(settings);

            return settings;
        }

        private static readonly HashSet<string> AllowedIconExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ico", ".png", ".exe", ".dll"
        };

        /// <summary>
        /// Merges built-in launch environments with the layered
        /// <c>launch.environments</c> entries, validating every value. Invalid
        /// entries raise an issue and are skipped (or, for a built-in, the
        /// invalid field is ignored) rather than failing the whole load.
        /// </summary>
        public static LaunchEnvironmentCatalog BuildLaunchCatalog(ToolSettings settings)
        {
            var environments = new List<LaunchEnvironment>(LaunchEnvironmentCatalog.CreateBuiltIns(settings.VsCodeCommand));
            var issues = settings.Issues ?? (settings.Issues = new List<ValidationIssue>());

            foreach (var kvp in settings.LaunchEnvironmentOverrides ?? new Dictionary<string, LaunchEnvironmentOverride>())
            {
                var id = LaunchEnvironmentCatalog.Normalize(kvp.Key);
                var spec = kvp.Value ?? new LaunchEnvironmentOverride();
                if (!LaunchEnvironmentCatalog.IsValidId(id))
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Warning,
                        "settings/launch/invalid-id",
                        $"Ignored launch environment '{kvp.Key}' — ids must be lower-case letters, digits and hyphens (max 40)."));
                    continue;
                }

                var index = environments.FindIndex(e => e.Id == id);
                var baseline = index >= 0 ? environments[index] : null;

                var kind = baseline?.Kind ?? LaunchEnvironmentKind.Terminal;
                if (!string.IsNullOrWhiteSpace(spec.Type))
                {
                    if (string.Equals(spec.Type.Trim(), "editor", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = LaunchEnvironmentKind.Editor;
                    }
                    else if (string.Equals(spec.Type.Trim(), "terminal", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = LaunchEnvironmentKind.Terminal;
                    }
                    else
                    {
                        issues.Add(new ValidationIssue(
                            IssueSeverity.Warning,
                            "settings/launch/invalid-type",
                            $"Ignored launch environment '{id}' — type must be 'editor' or 'terminal'."));
                        continue;
                    }
                }

                var command = FirstNonEmpty(spec.Command, baseline?.Command);
                if (string.IsNullOrWhiteSpace(command) || HasControlChars(command) || command.IndexOf('"') >= 0)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Warning,
                        "settings/launch/invalid-command",
                        $"Ignored launch environment '{id}' — command is missing or contains invalid characters."));
                    continue;
                }

                var args = spec.Args ?? baseline?.Arguments ?? string.Empty;
                var remoteCommand = spec.RemoteCommand ?? baseline?.RemoteCommand ?? string.Empty;
                if (HasControlChars(args) || HasControlChars(remoteCommand))
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Warning,
                        "settings/launch/invalid-args",
                        $"Ignored launch environment '{id}' — args/remote_command must be a single line."));
                    continue;
                }

                var icon = baseline?.IconPath ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(spec.Icon))
                {
                    var candidate = Environment.ExpandEnvironmentVariables(spec.Icon.Trim().Trim('"'));
                    if (IsValidIconPath(candidate))
                    {
                        icon = candidate;
                    }
                    else
                    {
                        issues.Add(new ValidationIssue(
                            IssueSeverity.Warning,
                            "settings/launch/invalid-icon",
                            $"Ignored icon for launch environment '{id}' — must be an existing absolute .ico, .png, .exe or .dll file."));
                    }
                }

                var merged = new LaunchEnvironment(
                    id,
                    FirstNonEmpty(spec.Name, baseline?.DisplayName, id),
                    kind,
                    Environment.ExpandEnvironmentVariables(command.Trim()),
                    args.Trim(),
                    remoteCommand.Trim(),
                    icon,
                    baseline?.IsBuiltIn ?? false);

                if (index >= 0)
                {
                    environments[index] = merged;
                }
                else
                {
                    environments.Add(merged);
                }
            }

            var catalog = new LaunchEnvironmentCatalog(environments, settings.DefaultLaunchEnvironment);
            if (!string.IsNullOrWhiteSpace(settings.DefaultLaunchEnvironment) &&
                catalog.Find(settings.DefaultLaunchEnvironment) == null)
            {
                issues.Add(new ValidationIssue(
                    IssueSeverity.Warning,
                    "settings/launch/unknown-default",
                    $"Default launch environment '{settings.DefaultLaunchEnvironment}' is not defined — using VS Code."));
            }

            return catalog;
        }

        private static bool IsValidIconPath(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) &&
                       !HasControlChars(path) &&
                       Path.IsPathRooted(path) &&
                       AllowedIconExtensions.Contains(Path.GetExtension(path)) &&
                       File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasControlChars(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var c in value)
            {
                if (char.IsControl(c))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static void ApplySettingsFile(ToolSettings settings, string filePath)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var dto = deserializer.Deserialize<SettingsYamlDto>(yaml);

                if (dto?.Tooling != null)
                {
                    if (!string.IsNullOrWhiteSpace(dto.Tooling.VsCodeCommand))
                    {
                        settings.VsCodeCommand = dto.Tooling.VsCodeCommand;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Tooling.GitCommand))
                    {
                        settings.GitCommand = dto.Tooling.GitCommand;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Tooling.SshCommand))
                    {
                        settings.SshCommand = dto.Tooling.SshCommand;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Tooling.TerminalCommand))
                    {
                        settings.TerminalCommand = dto.Tooling.TerminalCommand;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Tooling.PowerShellCommand))
                    {
                        settings.PowerShellCommand = dto.Tooling.PowerShellCommand;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Tooling.SshConfigPath))
                    {
                        if (IsAllowedConfiguredPath(dto.Tooling.SshConfigPath))
                        {
                            settings.SshConfigPath = dto.Tooling.SshConfigPath;
                        }
                        else
                        {
                            settings.Issues.Add(new ValidationIssue(
                                IssueSeverity.Warning,
                                "settings/path/outside-allowed-roots",
                                $"Ignored ssh_config_path '{dto.Tooling.SshConfigPath}' — outside allowed user-local roots."));
                        }
                    }
                }

                if (dto?.Security != null)
                {
                    if (dto.Security.AllowHttpLinks.HasValue)
                    {
                        settings.AllowHttpLinks = dto.Security.AllowHttpLinks.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Security.GitHubCredentialTarget))
                    {
                        settings.GitHubCredentialTarget = dto.Security.GitHubCredentialTarget;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Security.AdoCredentialTarget))
                    {
                        settings.AdoCredentialTarget = dto.Security.AdoCredentialTarget;
                    }
                }

                if (dto?.Library != null && !string.IsNullOrWhiteSpace(dto.Library.Path))
                {
                    if (IsAllowedConfiguredPath(dto.Library.Path))
                    {
                        settings.LibraryPath = dto.Library.Path;
                    }
                    else
                    {
                        settings.Issues.Add(new ValidationIssue(
                            IssueSeverity.Warning,
                            "settings/path/outside-allowed-roots",
                            $"Ignored library path '{dto.Library.Path}' — outside allowed user-local roots."));
                    }
                }

                if (dto?.Updates != null)
                {
                    var current = settings.UpdateOptions ?? UpdateOptions.Defaults();
                    var branch = string.IsNullOrWhiteSpace(dto.Updates.Branch) ? current.Branch : dto.Updates.Branch.Trim();
                    var autoCheck = dto.Updates.AutoCheckOnLaunch ?? current.AutoCheckOnLaunch;
                    var repoRootOverride = dto.Updates.RepoRootOverride ?? current.RepoRootOverride ?? string.Empty;
                    settings.UpdateOptions = new UpdateOptions(branch, autoCheck, repoRootOverride);
                }

                if (dto?.Launch != null)
                {
                    if (!string.IsNullOrWhiteSpace(dto.Launch.DefaultEnvironment))
                    {
                        settings.DefaultLaunchEnvironment = LaunchEnvironmentCatalog.Normalize(dto.Launch.DefaultEnvironment);
                    }

                    if (dto.Launch.Environments != null)
                    {
                        foreach (var kvp in dto.Launch.Environments)
                        {
                            var id = LaunchEnvironmentCatalog.Normalize(kvp.Key);
                            if (id.Length == 0)
                            {
                                continue;
                            }

                            var incoming = kvp.Value ?? new LaunchEnvironmentDto();
                            if (!settings.LaunchEnvironmentOverrides.TryGetValue(id, out var merged))
                            {
                                merged = new LaunchEnvironmentOverride();
                                settings.LaunchEnvironmentOverrides[id] = merged;
                            }

                            merged.Name = incoming.Name ?? merged.Name;
                            merged.Type = incoming.Type ?? merged.Type;
                            merged.Command = incoming.Command ?? merged.Command;
                            merged.Args = incoming.Args ?? merged.Args;
                            merged.RemoteCommand = incoming.RemoteCommand ?? merged.RemoteCommand;
                            merged.Icon = incoming.Icon ?? merged.Icon;
                        }
                    }
                }

                if (dto?.Stores != null)
                {
                    foreach (var kvp in dto.Stores)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                        {
                            continue;
                        }

                        var storeType = kvp.Value.Type ?? "local";
                        if (!string.Equals(storeType, "local", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(storeType, "ssh", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var existing = settings.Stores.FindIndex(
                            s => string.Equals(s.Id, kvp.Key, StringComparison.OrdinalIgnoreCase));
                        var store = new RepoStore
                        {
                            Id = kvp.Key,
                            Type = storeType,
                            Root = kvp.Value.Root ?? string.Empty,
                            Host = kvp.Value.Host ?? string.Empty,
                            User = kvp.Value.User ?? string.Empty,
                            CredentialTarget = kvp.Value.CredentialTarget ?? string.Empty,
                            Port = kvp.Value.Port
                        };

                        if (existing >= 0)
                        {
                            settings.Stores[existing] = store;
                        }
                        else
                        {
                            settings.Stores.Add(store);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Malformed YAML — skip this settings file
            }

            settings.SettingsSource = filePath;
        }

        private static bool IsAllowedConfiguredPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(path);
                var full = Path.GetFullPath(expanded);
                return AllowedSettingsRoots.IsUnderAllowedRoot(full, AllowedRoots);
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveCommand(string configured, IEnumerable<string> candidates)
        {
            var values = new List<string>();

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    values.Add(candidate);
                }
            }

            if (!string.IsNullOrWhiteSpace(configured))
            {
                var trimmed = configured.Trim();
                if (Path.IsPathRooted(trimmed) || Path.HasExtension(trimmed))
                {
                    values.Insert(0, trimmed);
                }
                else
                {
                    values.Add(trimmed);
                }
            }

            foreach (var value in values)
            {
                var resolved = TryResolve(value);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            return string.IsNullOrWhiteSpace(configured) ? string.Empty : configured.Trim();
        }

        private static string TryResolve(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return string.Empty;
            }

            var value = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
            if (Path.IsPathRooted(value))
            {
                return File.Exists(value) ? value : string.Empty;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var rawSegment in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = rawSegment.Trim();
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                var candidate = Path.Combine(segment, value);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }
    }
}
