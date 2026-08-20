using System;
using System.Globalization;
using System.IO;
using System.Text;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Infrastructure.Library
{
    /// <summary>
    /// Append-only audit log for library operations. Stored alongside library.yml
    /// in a separate file (library.audit.yml) so the curated library.yml stays
    /// untouched. Atomic-ish appends via File.AppendAllText.
    /// </summary>
    public sealed class LibraryAuditLogger : IAuditLogger
    {
        public void RecordPush(string libraryRoot, AuditEntry entry)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || entry == null)
            {
                return;
            }
            if (!Directory.Exists(libraryRoot))
            {
                return;
            }

            var auditPath = Path.Combine(libraryRoot, "library.audit.yml");
            var sb = new StringBuilder();
            if (!File.Exists(auditPath))
            {
                sb.AppendLine("# Developer Control Tower — library audit log (append-only)");
                sb.AppendLine("entries:");
            }
            sb.AppendLine($"  - asset: {YamlScalar.Quote(entry.Asset)}");
            if (!string.IsNullOrWhiteSpace(entry.AssetVersion))
            {
                sb.AppendLine($"    asset_version: {YamlScalar.Quote(entry.AssetVersion)}");
            }
            sb.AppendLine($"    action: {YamlScalar.Quote(entry.Action)}");
            if (!string.IsNullOrWhiteSpace(entry.TargetProject))
            {
                sb.AppendLine($"    target_project: {YamlScalar.Quote(entry.TargetProject)}");
            }
            if (!string.IsNullOrWhiteSpace(entry.TargetPath))
            {
                sb.AppendLine($"    target_path: {YamlScalar.Quote(entry.TargetPath)}");
            }
            sb.AppendLine($"    on: {entry.OnUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"    files_written: {entry.FilesWritten}");
            sb.AppendLine($"    files_skipped: {entry.FilesSkipped}");

            File.AppendAllText(auditPath, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
