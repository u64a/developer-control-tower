using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Infrastructure.Ssh
{
    public sealed class SshHostCatalog
    {
        private static readonly IReadOnlyList<string> AllowedRoots = AllowedSettingsRoots.GetAllowedRoots();

        private readonly ToolSettings _settings;

        public SshHostCatalog()
            : this(new ToolSettings())
        {
        }

        public SshHostCatalog(ToolSettings settings)
        {
            _settings = settings ?? new ToolSettings();
        }

        public IReadOnlyList<string> GetHosts()
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var configPath = ResolveConfigPath(_settings);

            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return hosts.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
            }

            foreach (var rawLine in File.ReadAllLines(configPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                {
                    continue;
                }

                if (!line.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line.Substring(5).Trim();
                var items = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var item in items)
                {
                    if (item.Contains("*") || item.Contains("?") || string.Equals(item, "!", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    hosts.Add(item);
                }
            }
            return hosts.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ResolveConfigPath(ToolSettings settings)
        {
            if (settings != null &&
                !string.IsNullOrWhiteSpace(settings.SshConfigPath) &&
                File.Exists(settings.SshConfigPath) &&
                IsAllowedPath(settings.SshConfigPath))
            {
                return settings.SshConfigPath;
            }

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh",
                "config");

            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Code",
                "User",
                "settings.json");

            if (File.Exists(settingsPath))
            {
                var settingsJson = File.ReadAllText(settingsPath);
                var match = Regex.Match(settingsJson, "\"remote\\.SSH\\.configFile\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success)
                {
                    var configured = match.Groups[1].Value
                        .Replace("\\\\", "\\")
                        .Replace("/", "\\");
                    if (File.Exists(configured) && IsAllowedPath(configured))
                    {
                        return configured;
                    }
                }
            }

            return defaultPath;
        }

        private static bool IsAllowedPath(string path)
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
    }
}
