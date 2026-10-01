using System.Collections.Generic;
using System.IO;
using System.Text;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Yaml;
using ControlTower.Infrastructure.Yaml.Dto;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ControlTower.Infrastructure.Configuration
{
    public sealed class SettingsWriter
    {
        /// <summary>
        /// Writes stores and basic settings to the given settings YAML path.
        /// Creates the directory if needed. Performs an atomic write.
        /// </summary>
        public void Write(string settingsPath, IReadOnlyList<RepoStore> stores, string libraryPath = null)
        {
            Write(settingsPath, stores, libraryPath, updateOptions: null);
        }

        public void Write(
            string settingsPath,
            IReadOnlyList<RepoStore> stores,
            string libraryPath,
            UpdateOptions updateOptions)
        {
            Write(settingsPath, stores, libraryPath, updateOptions, defaultLaunchEnvironment: null);
        }

        /// <param name="defaultLaunchEnvironment">
        /// Global default launch environment id. <c>null</c> keeps the value
        /// already in the file. Custom <c>launch.environments</c> entries are
        /// always carried over from the existing file.
        /// </param>
        public void Write(
            string settingsPath,
            IReadOnlyList<RepoStore> stores,
            string libraryPath,
            UpdateOptions updateOptions,
            string defaultLaunchEnvironment)
        {
            var existingLaunch = ReadExistingLaunch(settingsPath);
            var defaultEnvironment = defaultLaunchEnvironment == null
                ? LaunchEnvironmentCatalog.Normalize(existingLaunch?.DefaultEnvironment)
                : LaunchEnvironmentCatalog.Normalize(defaultLaunchEnvironment);
            var dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("kind: developer-control-tower/settings");
            sb.AppendLine("schema_version: 0");

            if (!string.IsNullOrWhiteSpace(libraryPath))
            {
                sb.AppendLine();
                sb.AppendLine("library:");
                sb.AppendLine($"  path: {YamlScalar.Quote(libraryPath)}");
            }

            if (updateOptions != null)
            {
                sb.AppendLine();
                sb.AppendLine("updates:");
                sb.AppendLine($"  branch: {YamlScalar.Quote(string.IsNullOrWhiteSpace(updateOptions.Branch) ? "main" : updateOptions.Branch)}");
                sb.AppendLine($"  auto_check_on_launch: {(updateOptions.AutoCheckOnLaunch ? "true" : "false")}");
                sb.AppendLine($"  repo_root_override: {YamlScalar.Quote(updateOptions.RepoRootOverride ?? string.Empty)}");
            }

            if (stores != null && stores.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("stores:");
                foreach (var store in stores)
                {
                    sb.AppendLine($"  {YamlScalar.Quote(store.Id)}:");
                    sb.AppendLine($"    type: {YamlScalar.Quote(store.Type)}");
                    sb.AppendLine($"    root: {YamlScalar.Quote(store.Root)}");

                    if (store.IsSsh)
                    {
                        sb.AppendLine($"    host: {YamlScalar.Quote(store.Host)}");
                        if (!string.IsNullOrWhiteSpace(store.User))
                        {
                            sb.AppendLine($"    user: {YamlScalar.Quote(store.User)}");
                        }
                        if (store.Port > 0 && store.Port != 22)
                        {
                            sb.AppendLine($"    port: {store.Port}");
                        }
                        if (!string.IsNullOrWhiteSpace(store.CredentialTarget))
                        {
                            sb.AppendLine($"    credential_target: {YamlScalar.Quote(store.CredentialTarget)}");
                        }
                    }
                }
            }

            var environments = existingLaunch?.Environments;
            var hasEnvironments = environments != null && environments.Count > 0;
            if (!string.IsNullOrEmpty(defaultEnvironment) || hasEnvironments)
            {
                sb.AppendLine();
                sb.AppendLine("launch:");
                if (!string.IsNullOrEmpty(defaultEnvironment))
                {
                    sb.AppendLine($"  default_environment: {YamlScalar.Quote(defaultEnvironment)}");
                }

                if (hasEnvironments)
                {
                    sb.AppendLine("  environments:");
                    foreach (var kvp in environments)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Key))
                        {
                            continue;
                        }

                        var env = kvp.Value ?? new LaunchEnvironmentDto();
                        sb.AppendLine($"    {YamlScalar.Quote(kvp.Key)}:");
                        var any = false;
                        any |= AppendField(sb, "name", env.Name);
                        any |= AppendField(sb, "type", env.Type);
                        any |= AppendField(sb, "command", env.Command);
                        any |= AppendField(sb, "args", env.Args);
                        any |= AppendField(sb, "remote_command", env.RemoteCommand);
                        any |= AppendField(sb, "icon", env.Icon);
                        if (!any)
                        {
                            sb.AppendLine("      {}");
                        }
                    }
                }
            }

            // Atomic write
            var tempPath = settingsPath + ".tmp";
            File.WriteAllText(tempPath, sb.ToString(), new UTF8Encoding(false));
            File.Copy(tempPath, settingsPath, true);
            try { File.Delete(tempPath); } catch { }
        }

        private static bool AppendField(StringBuilder sb, string key, string value)
        {
            if (value == null)
            {
                return false;
            }

            sb.AppendLine($"      {key}: {YamlScalar.Quote(value)}");
            return true;
        }

        private static LaunchSettingsDto ReadExistingLaunch(string settingsPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
                {
                    return null;
                }

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                return deserializer.Deserialize<SettingsYamlDto>(File.ReadAllText(settingsPath))?.Launch;
            }
            catch
            {
                // Malformed existing file: nothing to preserve.
                return null;
            }
        }
    }
}
