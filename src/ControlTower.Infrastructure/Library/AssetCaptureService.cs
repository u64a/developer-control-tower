using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Ssh;
using Renci.SshNet;

namespace ControlTower.Infrastructure.Library
{
    /// <summary>
    /// Captures an asset from a project (local or SSH-hosted) into the library.
    /// SSH support requires the SSH services to be wired in the constructor.
    /// </summary>
    public sealed class AssetCaptureService : IAssetCaptureService
    {
        private static readonly Regex SafeIdRegex =
            new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);

        private readonly ILibraryProvider _libraryProvider;
        private readonly IStoreProvider _storeProvider;
        private readonly ICredentialStore _credentialStore;
        private readonly SshNetClientFactory _sshClientFactory;

        public AssetCaptureService(ILibraryProvider libraryProvider)
            : this(libraryProvider, null, null, null)
        {
        }

        public AssetCaptureService(
            ILibraryProvider libraryProvider,
            IStoreProvider storeProvider,
            ICredentialStore credentialStore,
            SshNetClientFactory sshClientFactory)
        {
            _libraryProvider = libraryProvider;
            _storeProvider = storeProvider;
            _credentialStore = credentialStore;
            _sshClientFactory = sshClientFactory;
        }

        public AssetCaptureResult CaptureFromLocal(
            string libraryRoot,
            LibraryIndex index,
            string assetId,
            string assetTypeId,
            string sourceFolder,
            string fromProjectId)
        {
            var precheck = ValidateCommon(libraryRoot, index, assetId, assetTypeId, out var type, out var destRoot, out var relPath);
            if (precheck != null) return precheck;

            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                return Fail("Source folder not found.");
            }

            int copied;
            try
            {
                Directory.CreateDirectory(destRoot);
                copied = CopyTreeSafe(sourceFolder, destRoot);
            }
            catch (Exception ex)
            {
                return Fail("Copy failed: " + ex.Message);
            }

            return FinishCapture(libraryRoot, type, destRoot, relPath, assetId, fromProjectId, copied,
                "Captured from local project '" + fromProjectId + "'.");
        }

        public AssetCaptureResult CaptureFromSsh(
            string libraryRoot,
            LibraryIndex index,
            string assetId,
            string assetTypeId,
            string sshTarget,
            string remoteRelativePath,
            string fromProjectId)
        {
            if (_storeProvider == null || _credentialStore == null || _sshClientFactory == null)
            {
                return Fail("SSH services not configured.");
            }

            var precheck = ValidateCommon(libraryRoot, index, assetId, assetTypeId, out var type, out var destRoot, out var relPath);
            if (precheck != null) return precheck;

            if (string.IsNullOrWhiteSpace(sshTarget))
            {
                return Fail("SSH target is required.");
            }
            if (string.IsNullOrWhiteSpace(remoteRelativePath))
            {
                return Fail("Remote relative path is required (e.g. .github/skills/my-skill).");
            }
            if (Path.IsPathRooted(remoteRelativePath) ||
                remoteRelativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(s => s == ".."))
            {
                return Fail("Remote relative path must be inside the project root and not escape with '..'.");
            }

            // Parse user@host:remoteRoot
            var sep = sshTarget.IndexOf(':');
            if (sep <= 1 || sep >= sshTarget.Length - 1)
            {
                return Fail("SSH target is not a recognised host:path.");
            }
            var hostPart = sshTarget.Substring(0, sep);
            var remoteProjectRoot = sshTarget.Substring(sep + 1).Trim();

            string user = null;
            var hostname = hostPart;
            var atIdx = hostPart.IndexOf('@');
            if (atIdx > 0)
            {
                user = hostPart.Substring(0, atIdx);
                hostname = hostPart.Substring(atIdx + 1);
            }

            var store = FindMatchingStore(hostname, user);
            if (store == null)
            {
                return Fail($"No SSH store configured for host '{hostname}'.");
            }

            var password = string.IsNullOrWhiteSpace(store.CredentialTarget)
                ? string.Empty
                : _credentialStore.GetPassword(store.CredentialTarget);
            int port = store.Port > 0 ? store.Port : 22;
            var sshUser = user ?? store.User;

            // Detect remote OS for path joining and SFTP normalisation.
            bool remoteIsWindows;
            try
            {
                _sshClientFactory.EnsureHostKeyPolicyConfigured();
                using var probe = _sshClientFactory.CreateSshClient(
                    hostname,
                    port,
                    sshUser,
                    password,
                    TimeSpan.FromSeconds(10));
                probe.Connect();
                using var cmd = probe.RunCommand("echo %OS%");
                remoteIsWindows = cmd.Result?.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0;
                probe.Disconnect();
            }
            catch (Exception ex)
            {
                AppLogger.Error("capture.ssh", "SSH probe failed", ex);
                return Fail("Could not connect to SSH host: " + ex.Message);
            }

            var sep2 = remoteIsWindows ? '\\' : '/';
            var remoteRel = remoteIsWindows ? remoteRelativePath.Replace('/', '\\') : remoteRelativePath.Replace('\\', '/');
            var remoteAssetFolder = remoteProjectRoot.TrimEnd('/', '\\') + sep2 + remoteRel.TrimStart('/', '\\');
            var remoteSftp = NormaliseSftpPath(remoteAssetFolder, remoteIsWindows);

            int copied = 0;
            try
            {
                Directory.CreateDirectory(destRoot);
                using var sftp = _sshClientFactory.CreateSftpClient(
                    hostname,
                    port,
                    sshUser,
                    password,
                    TimeSpan.FromSeconds(10));
                sftp.Connect();

                if (!sftp.Exists(remoteSftp))
                {
                    sftp.Disconnect();
                    return Fail($"Remote source not found at {remoteSftp}.");
                }

                copied = DownloadTree(sftp, remoteSftp, destRoot);
                sftp.Disconnect();
            }
            catch (Exception ex)
            {
                AppLogger.Error("capture.ssh", "SFTP download failed", ex);
                return Fail("SFTP download failed: " + ex.Message);
            }

            return FinishCapture(libraryRoot, type, destRoot, relPath, assetId, fromProjectId, copied,
                "Captured from SSH project '" + fromProjectId + "'.");
        }

        // ---- shared finish + validation helpers ----

        private AssetCaptureResult ValidateCommon(
            string libraryRoot,
            LibraryIndex index,
            string assetId,
            string assetTypeId,
            out AssetType type,
            out string destRoot,
            out string relPath)
        {
            type = null; destRoot = null; relPath = null;

            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return Fail("Library root not found.");
            }
            if (!SafeIdRegex.IsMatch(assetId ?? string.Empty))
            {
                return Fail("Asset id must start with a letter or digit and contain only letters, digits, '.', '_' or '-'.");
            }
            if (string.IsNullOrWhiteSpace(assetTypeId))
            {
                return Fail("Asset type is required.");
            }

            type = index.AssetTypes.FirstOrDefault(t =>
                string.Equals(t.Id, assetTypeId, StringComparison.OrdinalIgnoreCase));
            if (type == null)
            {
                return Fail($"Unknown asset type '{assetTypeId}'. Update library.yml asset_types first.");
            }
            if (index.Assets.Any(a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase)))
            {
                return Fail($"An asset with id '{assetId}' already exists. Use Pull update instead.");
            }

            var typeFolder = type.Id + "s";
            relPath = $"{typeFolder}/{assetId}";
            destRoot = Path.GetFullPath(Path.Combine(libraryRoot, typeFolder, assetId));

            var libRootFull = Path.GetFullPath(libraryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!destRoot.StartsWith(libRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Resolved destination falls outside library root.");
            }

            return null;
        }

        private AssetCaptureResult FinishCapture(
            string libraryRoot,
            AssetType type,
            string destRoot,
            string relPath,
            string assetId,
            string fromProjectId,
            int filesCopied,
            string description)
        {
            var asset = new LibraryAsset
            {
                Id = assetId,
                TypeId = type.Id,
                Path = relPath,
                Version = "1.0.0",
                LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                Description = description,
                AbsoluteRoot = destRoot,
            };

            if (type.Layout == AssetLayout.FileCollection)
            {
                asset.Files = Directory.EnumerateFiles(destRoot)
                    .Select(p => Path.GetFileName(p))
                    .Where(n => !string.Equals(n, "asset.yml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            try
            {
                _libraryProvider.RegisterAsset(libraryRoot, asset, fromProjectId);
            }
            catch (Exception ex)
            {
                return Fail("Capture copied files but registry update failed: " + ex.Message);
            }

            return new AssetCaptureResult
            {
                Success = true,
                AssetId = assetId,
                FilesCopied = filesCopied,
                Message = $"Captured '{assetId}' ({filesCopied} file(s)) into the library.",
            };
        }

        // ---- local helpers ----

        private static int CopyTreeSafe(string sourceRoot, string destRoot)
        {
            int count = 0;
            CopyInto(new DirectoryInfo(sourceRoot), sourceRoot, destRoot, ref count);
            return count;
        }

        private static void CopyInto(DirectoryInfo dir, string sourceRoot, string destRoot, ref int count)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in dir.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (string.Equals(file.Name, "asset.yml", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(sourceRoot, file.FullName);
                var dest = Path.Combine(destRoot, rel);
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(file.FullName, dest, overwrite: true);
                count++;
            }
            foreach (var sub in dir.EnumerateDirectories())
            {
                CopyInto(sub, sourceRoot, destRoot, ref count);
            }
        }

        // ---- SFTP helpers ----

        private RepoStore FindMatchingStore(string hostname, string user)
        {
            var stores = _storeProvider?.GetStores();
            if (stores == null || stores.Count == 0) return null;
            var match = stores.FirstOrDefault(s =>
                s.IsSsh &&
                string.Equals(s.Host, hostname, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(user) &&
                string.Equals(s.User, user, StringComparison.OrdinalIgnoreCase));
            return match ?? stores.FirstOrDefault(s =>
                s.IsSsh && string.Equals(s.Host, hostname, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormaliseSftpPath(string path, bool remoteIsWindows)
        {
            var p = (path ?? string.Empty).Replace('\\', '/');
            if (remoteIsWindows && Regex.IsMatch(p, @"^[A-Za-z]:/"))
            {
                p = "/" + p;
            }
            return p;
        }

        private static int DownloadTree(SftpClient sftp, string remoteDir, string localRoot)
        {
            int count = 0;
            foreach (var entry in sftp.ListDirectory(remoteDir))
            {
                if (entry.Name == "." || entry.Name == "..") continue;
                if (string.Equals(entry.Name, "asset.yml", StringComparison.OrdinalIgnoreCase)) continue;

                if (entry.IsDirectory)
                {
                    var subLocal = Path.Combine(localRoot, entry.Name);
                    Directory.CreateDirectory(subLocal);
                    count += DownloadTree(sftp, entry.FullName, subLocal);
                }
                else if (entry.IsRegularFile)
                {
                    var localPath = Path.Combine(localRoot, entry.Name);
                    using var fs = File.Create(localPath);
                    sftp.DownloadFile(entry.FullName, fs);
                    count++;
                }
            }
            return count;
        }

        private static AssetCaptureResult Fail(string message)
            => new() { Success = false, Message = message };
    }
}
