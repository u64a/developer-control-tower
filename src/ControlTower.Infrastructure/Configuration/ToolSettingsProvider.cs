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

            return settings;
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
