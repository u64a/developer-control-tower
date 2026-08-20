using System.Collections.Generic;
using System.IO;
using System.Text;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Yaml;

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

            // Atomic write
            var tempPath = settingsPath + ".tmp";
            File.WriteAllText(tempPath, sb.ToString(), new UTF8Encoding(false));
            File.Copy(tempPath, settingsPath, true);
            try { File.Delete(tempPath); } catch { }
        }
    }
}
