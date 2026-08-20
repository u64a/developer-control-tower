#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Infrastructure.Git
{
    /// <summary>
    /// Moves a non-empty source folder under
    /// <c>%USERPROFILE%\projectmgr-quarantine-&lt;UTC ts&gt;\&lt;slug&gt;\</c>.
    /// Uses <see cref="Directory.Move(string,string)"/> when source and
    /// destination share a volume; falls back to recursive copy + delete
    /// across volumes.
    /// </summary>
    public sealed class QuarantineService : IQuarantineService
    {
        private readonly Func<string> _userProfileResolver;
        private readonly Func<DateTime> _utcNow;

        public QuarantineService()
            : this(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                   () => DateTime.UtcNow)
        {
        }

        // Test seam: lets tests redirect the quarantine root and control
        // the timestamp portion of the destination folder name.
        public QuarantineService(Func<string> userProfileResolver, Func<DateTime> utcNow)
        {
            _userProfileResolver = userProfileResolver ?? throw new ArgumentNullException(nameof(userProfileResolver));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        public async Task<string> QuarantineAsync(string sourcePath, string slug, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }
            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException("Quarantine source does not exist: " + sourcePath);
            }

            var safeSlug = string.IsNullOrWhiteSpace(slug) ? "project" : SanitizeSlug(slug);
            var timestamp = _utcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var quarantineRoot = Path.Combine(
                _userProfileResolver(),
                "projectmgr-quarantine-" + timestamp);
            var destination = Path.Combine(quarantineRoot, safeSlug);

            // Disambiguate if a previous quarantine already produced the
            // same root within the same second.
            int suffix = 1;
            while (Directory.Exists(destination) || File.Exists(destination))
            {
                destination = Path.Combine(quarantineRoot, safeSlug + "-" + suffix.ToString(CultureInfo.InvariantCulture));
                suffix++;
                if (suffix > 1000)
                {
                    throw new IOException("Could not allocate a unique quarantine destination under " + quarantineRoot);
                }
            }

            Directory.CreateDirectory(quarantineRoot);

            ct.ThrowIfCancellationRequested();

            await Task.Run(() => MoveOrCopy(sourcePath, destination, ct), ct).ConfigureAwait(false);

            AppLogger.Info("QuarantineService",
                "Quarantined '" + sourcePath + "' -> '" + destination + "'.");

            return destination;
        }

        private static void MoveOrCopy(string source, string destination, CancellationToken ct)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException)
            {
                // Likely cross-volume — fall through to copy + delete.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }

            CopyDirectory(source, destination, ct);
            try
            {
                Directory.Delete(source, recursive: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("QuarantineService",
                    "Copied but could not delete source after fallback move: " + ex.Message);
            }
        }

        private static void CopyDirectory(string source, string destination, CancellationToken ct)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(destination, name), overwrite: true);
            }

            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                CopyDirectory(dir, Path.Combine(destination, name), ct);
            }
        }

        private static string SanitizeSlug(string slug)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = new char[slug.Length];
            int written = 0;
            foreach (var ch in slug)
            {
                bool bad = false;
                foreach (var iv in invalid)
                {
                    if (ch == iv) { bad = true; break; }
                }
                chars[written++] = bad ? '-' : ch;
            }
            var sanitised = new string(chars, 0, written).Trim('.', ' ');
            return string.IsNullOrWhiteSpace(sanitised) ? "project" : sanitised;
        }
    }
}
