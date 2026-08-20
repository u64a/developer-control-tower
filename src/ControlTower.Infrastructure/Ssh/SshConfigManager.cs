using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Ssh
{
    public sealed class SshConfigManager : ISshConfigManager
    {
        private const string BeginMarker = "# --- Developer Control Tower managed ---";
        private const string EndMarker = "# --- End Developer Control Tower ---";

        // Allowlist of SSH config directives the tool is permitted to write
        // inside the managed block (H4 / ADR-004). Any directive not in this
        // set must be refused before reaching the file.
        private static readonly HashSet<string> AllowedDirectives = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host",
            "HostName",
            "User",
            "Port",
            "IdentityFile"
        };

        private readonly string _sshConfigPath;

        public SshConfigManager()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh", "config"))
        {
        }

        public SshConfigManager(string sshConfigPath)
        {
            _sshConfigPath = sshConfigPath;
        }

        public void UpdateSshConfig(IReadOnlyList<RepoStore> stores)
        {
            var sshStores = stores?.Where(s => s.IsSsh).ToList() ?? new List<RepoStore>();

            // Validate every value BEFORE touching the file so a single bad
            // entry cannot leave the config half-written (H4).
            foreach (var store in sshStores)
            {
                ValidateDirectiveValue("Host", store.Id);
                ValidateDirectiveValue("HostName", store.Host);
                ValidateDirectiveValue("User", store.User);
            }

            var existingLines = File.Exists(_sshConfigPath)
                ? File.ReadAllLines(_sshConfigPath).ToList()
                : new List<string>();

            var cleanedLines = RemoveManagedBlock(existingLines);
            var managedBlock = BuildManagedBlock(sshStores);

            var result = new List<string>(cleanedLines);
            if (result.Count > 0 && !string.IsNullOrWhiteSpace(result.Last()))
            {
                result.Add(string.Empty);
            }
            result.AddRange(managedBlock);

            var sshDir = Path.GetDirectoryName(_sshConfigPath);
            if (!string.IsNullOrEmpty(sshDir))
            {
                Directory.CreateDirectory(sshDir);
            }

            File.WriteAllLines(_sshConfigPath, result, new UTF8Encoding(false));
        }

        public IReadOnlyList<string> GetManagedHosts()
        {
            if (!File.Exists(_sshConfigPath))
            {
                return Array.Empty<string>();
            }

            var lines = File.ReadAllLines(_sshConfigPath);
            var hosts = new List<string>();
            bool inBlock = false;

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith(BeginMarker, StringComparison.Ordinal))
                {
                    inBlock = true;
                    continue;
                }
                if (line.TrimStart().StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    inBlock = false;
                    continue;
                }
                if (inBlock)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
                    {
                        var host = trimmed.Substring(5).Trim();
                        if (!string.IsNullOrWhiteSpace(host))
                        {
                            hosts.Add(host);
                        }
                    }
                }
            }

            return hosts;
        }

        /// <summary>
        /// Refuses any directive value containing characters that could
        /// terminate the line or break out of its quoting. Throws so the
        /// caller cannot partially write the config (H4).
        /// </summary>
        private static void ValidateDirectiveValue(string directive, string value)
        {
            if (!AllowedDirectives.Contains(directive))
            {
                throw new SshConfigValueException(directive, value, "directive is not on the managed allowlist");
            }

            if (value == null)
            {
                throw new SshConfigValueException(directive, string.Empty, "value is null");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new SshConfigValueException(directive, value, "value is empty");
            }

            foreach (var ch in value)
            {
                if (ch == '\r' || ch == '\n')
                {
                    throw new SshConfigValueException(directive, value, "value contains newline");
                }

                if (char.IsControl(ch))
                {
                    throw new SshConfigValueException(directive, value, "value contains a control character");
                }

                if (ch == '"' || ch == '\'')
                {
                    throw new SshConfigValueException(directive, value, "value contains a quote character");
                }
            }
        }

        private static List<string> RemoveManagedBlock(List<string> lines)
        {
            var result = new List<string>();
            bool inBlock = false;

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith(BeginMarker, StringComparison.Ordinal))
                {
                    inBlock = true;
                    continue;
                }
                if (line.TrimStart().StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    inBlock = false;
                    continue;
                }
                if (!inBlock)
                {
                    result.Add(line);
                }
            }

            while (result.Count > 0 && string.IsNullOrWhiteSpace(result.Last()))
            {
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private static List<string> BuildManagedBlock(List<RepoStore> sshStores)
        {
            var block = new List<string>();
            if (sshStores.Count == 0)
            {
                return block;
            }

            block.Add(BeginMarker);
            foreach (var store in sshStores)
            {
                block.Add($"Host {store.Id}");
                block.Add($"  HostName {store.Host}");
                block.Add($"  User {store.User}");
                if (store.Port > 0 && store.Port != 22)
                {
                    block.Add($"  Port {store.Port}");
                }
                block.Add(string.Empty);
            }

            if (block.Count > 1 && string.IsNullOrWhiteSpace(block.Last()))
            {
                block.RemoveAt(block.Count - 1);
            }

            block.Add(EndMarker);
            return block;
        }
    }
}
