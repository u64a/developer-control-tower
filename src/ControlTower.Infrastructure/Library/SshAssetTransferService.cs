using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Ssh;
using Renci.SshNet;

namespace ControlTower.Infrastructure.Library
{
    /// <summary>
    /// Pushes assets to an SSH-hosted target via SFTP. Same preview-first model
    /// as the local service: hashes both sides, defaults Modified to off,
    /// rejects path-traversal in manifests, no deletes.
    /// </summary>
    public sealed class SshAssetTransferService : IAssetTransferService
    {
        private static readonly HashSet<string> ExcludedFileNames =
            new(StringComparer.OrdinalIgnoreCase) { "asset.yml" };

        private readonly IStoreProvider _storeProvider;
        private readonly ICredentialStore _credentialStore;
        private readonly SshNetClientFactory _sshClientFactory;

        public SshAssetTransferService(
            IStoreProvider storeProvider,
            ICredentialStore credentialStore,
            SshNetClientFactory sshClientFactory)
        {
            _storeProvider = storeProvider;
            _credentialStore = credentialStore;
            _sshClientFactory = sshClientFactory ?? throw new ArgumentNullException(nameof(sshClientFactory));
        }

        public AssetPushPlan PreparePush(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string targetProjectRoot,
            IEnumerable<string> includedFiles = null)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));

            var plan = new AssetPushPlan { Asset = asset, TargetRoot = targetProjectRoot };

            if (!TryParseSshPath(targetProjectRoot, out var hostPart, out var remoteRoot))
            {
                plan.Warnings.Add("Target is not a recognised SSH path.");
                return plan;
            }

            var (hostname, user) = SplitHost(hostPart);
            var store = FindMatchingStore(hostname, user);
            if (store == null)
            {
                plan.Warnings.Add($"No SSH store configured for host '{hostname}'.");
                return plan;
            }

            var password = string.IsNullOrWhiteSpace(store.CredentialTarget)
                ? string.Empty
                : _credentialStore.GetPassword(store.CredentialTarget);
            int port = store.Port > 0 ? store.Port : 22;
            var sshUser = user ?? store.User;

            // Detect remote OS once.
            bool remoteIsWindows;
            try
            {
                using var probe = OpenSsh(hostname, port, sshUser, password);
                probe.Connect();
                using var cmd = probe.RunCommand("echo %OS%");
                remoteIsWindows = cmd.Result?.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0;
                probe.Disconnect();
            }
            catch (Exception ex)
            {
                plan.Warnings.Add("Could not connect to SSH host: " + ex.Message);
                return plan;
            }

            plan.RemoteIsWindows = remoteIsWindows;
            var targetRel = !string.IsNullOrWhiteSpace(asset.DefaultTargetOverride)
                ? asset.DefaultTargetOverride
                : assetType.DefaultTarget;
            targetRel = (targetRel ?? string.Empty).Replace("{asset_id}", asset.Id);

            if (!LibraryPathContainment.TryResolveRemoteTarget(
                    remoteRoot,
                    targetRel,
                    remoteIsWindows,
                    out var resolvedTarget,
                    out var targetIssue))
            {
                plan.Warnings.Add("Remote target rejected: " + targetIssue);
                return plan;
            }

            plan.ResolvedTargetPath = $"{hostPart}:{resolvedTarget}";

            if (string.IsNullOrWhiteSpace(asset.AbsoluteRoot) || !Directory.Exists(asset.AbsoluteRoot))
            {
                plan.Warnings.Add($"Asset folder not found on disk: {asset.AbsoluteRoot}");
                return plan;
            }

            // Build source file list — same rules as LocalAssetTransferService.
            List<string> sourceFiles;
            if (assetType.Layout == AssetLayout.FileCollection)
            {
                var manifest = (asset.Files ?? new List<string>())
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => f.Replace('/', Path.DirectorySeparatorChar))
                    .ToList();

                if (includedFiles != null)
                {
                    var requested = new HashSet<string>(
                        includedFiles.Select(f => f.Replace('/', Path.DirectorySeparatorChar)),
                        StringComparer.OrdinalIgnoreCase);
                    manifest = manifest.Where(m => requested.Contains(m)).ToList();
                }

                sourceFiles = new List<string>();
                foreach (var rel in manifest)
                {
                    if (!IsSafeRelative(rel))
                    {
                        plan.Warnings.Add($"Skipping unsafe file path '{rel}'.");
                        continue;
                    }
                    var srcAbs = Path.GetFullPath(Path.Combine(asset.AbsoluteRoot, rel));
                    if (!IsUnder(srcAbs, asset.AbsoluteRoot) || !File.Exists(srcAbs))
                    {
                        plan.Warnings.Add($"Skipping '{rel}' (missing or escapes asset root).");
                        continue;
                    }
                    sourceFiles.Add(rel);
                }
            }
            else
            {
                sourceFiles = WalkAssetFiles(asset.AbsoluteRoot, plan.Warnings);
            }

            // Open SFTP for diff + later apply.
            try
            {
                using var sftp = OpenSftp(hostname, port, sshUser, password);
                sftp.Connect();

                foreach (var rel in sourceFiles)
                {
                    var srcAbs = Path.Combine(asset.AbsoluteRoot, rel);
                    if (!LibraryPathContainment.TryResolveRemoteTarget(
                            resolvedTarget,
                            rel,
                            remoteIsWindows,
                            out var tgtRemote,
                            out var fileTargetIssue))
                    {
                        plan.Warnings.Add(
                            $"File target '{rel}' rejected: {fileTargetIssue}");
                        continue;
                    }

                    FileChangeKind kind;
                    long? targetSize = null;
                    if (!sftp.Exists(NormaliseSftpPath(tgtRemote, remoteIsWindows)))
                    {
                        kind = FileChangeKind.New;
                    }
                    else
                    {
                        var attrs = sftp.GetAttributes(NormaliseSftpPath(tgtRemote, remoteIsWindows));
                        targetSize = attrs.Size;

                        // Hash both sides.
                        if (HashEqualsRemote(sftp, srcAbs, NormaliseSftpPath(tgtRemote, remoteIsWindows)))
                        {
                            kind = FileChangeKind.Identical;
                        }
                        else
                        {
                            kind = FileChangeKind.Modified;
                        }
                    }

                    plan.Changes.Add(new FileChange
                    {
                        RelativePath = rel,
                        Kind = kind,
                        SourceAbsolutePath = srcAbs,
                        TargetAbsolutePath = tgtRemote,
                        SourceSize = new FileInfo(srcAbs).Length,
                        TargetSize = targetSize,
                        Apply = kind == FileChangeKind.New,
                    });
                }

                sftp.Disconnect();
            }
            catch (Exception ex)
            {
                plan.Warnings.Add("SFTP preview failed: " + ex.Message);
            }

            return plan;
        }

        public AssetPushResult ApplyPush(AssetPushPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var result = new AssetPushResult();

            if (string.IsNullOrWhiteSpace(plan.ResolvedTargetPath) ||
                plan.Changes == null ||
                plan.Changes.Count == 0)
            {
                result.Success = false;
                result.Message = "No resolved target or no changes. Push aborted.";
                return result;
            }

            if (!TryValidateApplyPlan(
                    plan,
                    out var hostPart,
                    out var projectRoot,
                    out var resolvedTarget,
                    out var remoteIsWindows,
                    out var validationIssue))
            {
                result.Success = false;
                result.Message = "Remote target containment check failed: " + validationIssue;
                return result;
            }

            var (hostname, user) = SplitHost(hostPart);
            var store = FindMatchingStore(hostname, user);
            if (store == null)
            {
                result.Success = false;
                result.Message = $"No SSH store configured for host '{hostname}'.";
                return result;
            }

            var password = string.IsNullOrWhiteSpace(store.CredentialTarget)
                ? string.Empty
                : _credentialStore.GetPassword(store.CredentialTarget);
            int port = store.Port > 0 ? store.Port : 22;
            var sshUser = user ?? store.User;

            try
            {
                using var sftp = OpenSftp(hostname, port, sshUser, password);
                sftp.Connect();
                AppLogger.Info("sftp.apply", $"Connected to {hostname}:{port} as {sshUser}; remote=Windows? {remoteIsWindows}");
                var pathAccessor = new SshNetSftpPathAccessor(sftp);

                foreach (var change in plan.Changes)
                {
                    if (change == null)
                    {
                        result.Success = false;
                        result.Message = "Remote target containment check failed: the plan contains a missing change.";
                        return result;
                    }

                    if (!change.Apply)
                    {
                        if (change.Kind == FileChangeKind.Identical)
                        {
                            result.FilesIdentical++;
                        }
                        else
                        {
                            result.FilesSkipped++;
                        }
                        continue;
                    }

                    if (!TryValidateChangeForApply(
                            plan,
                            change,
                            resolvedTarget,
                            remoteIsWindows,
                            out var normalizedTarget,
                            out var changeIssue))
                    {
                        result.Success = false;
                        result.Message =
                            $"Remote target containment check failed for '{change.RelativePath}': {changeIssue}";
                        return result;
                    }

                    using var fs = File.OpenRead(change.SourceAbsolutePath);

                    if (!LibraryPathContainment.TryGetRemoteDescendantSegments(
                            projectRoot,
                            normalizedTarget,
                            remoteIsWindows,
                            allowEqual: false,
                            out var normalizedProjectRoot,
                            out _,
                            out var descendantSegments,
                            out var descendantIssue))
                    {
                        result.Success = false;
                        result.Message =
                            $"Remote target containment check failed for '{change.RelativePath}': {descendantIssue}";
                        return result;
                    }

                    if (!SftpUploadPathGuard.TryPrepareUpload(
                            pathAccessor,
                            NormaliseSftpPath(normalizedProjectRoot, remoteIsWindows),
                            descendantSegments,
                            caseInsensitiveNames: remoteIsWindows,
                            out var sftpPath,
                            out var linkIssue))
                    {
                        result.Success = false;
                        result.Message =
                            $"SFTP upload path rejected for '{change.RelativePath}': {linkIssue}";
                        return result;
                    }

                    var expectedSftpPath = NormaliseSftpPath(
                        normalizedTarget,
                        remoteIsWindows);
                    if (!string.Equals(
                            sftpPath,
                            expectedSftpPath,
                            remoteIsWindows
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        result.Success = false;
                        result.Message =
                            $"SFTP upload path rejected for '{change.RelativePath}': validated path mismatch.";
                        return result;
                    }

                    AppLogger.Info("sftp.apply", $"Uploading {change.SourceAbsolutePath} -> {sftpPath}");
                    sftp.UploadFile(fs, sftpPath, canOverride: true);
                    result.FilesWritten++;
                }

                sftp.Disconnect();
            }
            catch (Exception ex)
            {
                AppLogger.Error("sftp.apply", "SFTP upload failed", ex);
                result.Success = false;
                result.Message = "SFTP upload failed: " + ex.Message;
                return result;
            }

            result.Success = true;
            result.Message = $"Pushed {result.FilesWritten} file(s) over SFTP; skipped {result.FilesSkipped}; {result.FilesIdentical} identical.";
            return result;
        }

        // ---- helpers ----

        public AssetPushPlan PreparePull(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string sourceProjectRoot)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));

            var plan = new AssetPushPlan { Asset = asset, TargetRoot = sourceProjectRoot };

            if (!TryParseSshPath(sourceProjectRoot, out var hostPart, out var remoteRoot))
            {
                plan.Warnings.Add("Source is not a recognised SSH path.");
                return plan;
            }

            var (hostname, user) = SplitHost(hostPart);
            var store = FindMatchingStore(hostname, user);
            if (store == null)
            {
                plan.Warnings.Add($"No SSH store configured for host '{hostname}'.");
                return plan;
            }

            var password = string.IsNullOrWhiteSpace(store.CredentialTarget)
                ? string.Empty
                : _credentialStore.GetPassword(store.CredentialTarget);
            int port = store.Port > 0 ? store.Port : 22;
            var sshUser = user ?? store.User;

            bool remoteIsWindows;
            try
            {
                using var probe = OpenSsh(hostname, port, sshUser, password);
                probe.Connect();
                using var cmd = probe.RunCommand("echo %OS%");
                remoteIsWindows = cmd.Result?.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0;
                probe.Disconnect();
            }
            catch (Exception ex)
            {
                AppLogger.Error("sftp.pull", "Probe failed", ex);
                plan.Warnings.Add("Could not connect to SSH host: " + ex.Message);
                return plan;
            }

            plan.RemoteIsWindows = remoteIsWindows;
            var targetRel = !string.IsNullOrWhiteSpace(asset.DefaultTargetOverride)
                ? asset.DefaultTargetOverride
                : assetType.DefaultTarget;
            targetRel = (targetRel ?? string.Empty).Replace("{asset_id}", asset.Id);

            if (!LibraryPathContainment.TryResolveRemoteTarget(
                    remoteRoot,
                    targetRel,
                    remoteIsWindows,
                    out var remoteAssetFolder,
                    out var targetIssue))
            {
                plan.Warnings.Add("Remote source target rejected: " + targetIssue);
                return plan;
            }

            plan.ResolvedTargetPath = asset.AbsoluteRoot;

            try
            {
                using var sftp = OpenSftp(hostname, port, sshUser, password);
                sftp.Connect();

                var remoteDirSftp = NormaliseSftpPath(remoteAssetFolder, remoteIsWindows);
                if (!sftp.Exists(remoteDirSftp))
                {
                    plan.Warnings.Add($"No deployed copy of '{asset.Id}' found at {remoteDirSftp}.");
                    sftp.Disconnect();
                    return plan;
                }

                // Recursively list remote files.
                var manifest = (asset.Files ?? new List<string>())
                    .Select(f => f.Replace('/', Path.DirectorySeparatorChar))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var remoteFile in EnumerateRemoteFiles(sftp, remoteDirSftp))
                {
                    var relSftp = remoteFile.Substring(remoteDirSftp.Length).TrimStart('/');
                    var rel = relSftp.Replace('/', Path.DirectorySeparatorChar);

                    if (string.Equals(Path.GetFileName(rel), "asset.yml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (assetType.Layout == AssetLayout.FileCollection && !manifest.Contains(rel))
                    {
                        continue;
                    }

                    var tgtAbs = Path.GetFullPath(Path.Combine(asset.AbsoluteRoot, rel));
                    if (!IsUnder(tgtAbs, asset.AbsoluteRoot))
                    {
                        plan.Warnings.Add($"Skipping '{rel}' (target escapes library asset root).");
                        continue;
                    }

                    // Download remote file to a temp location so we can hash + later copy.
                    var tempPath = Path.Combine(Path.GetTempPath(),
                        "dct_pull_" + Guid.NewGuid().ToString("N"));
                    long remoteSize;
                    try
                    {
                        using (var fs = File.Create(tempPath))
                        {
                            sftp.DownloadFile(remoteFile, fs);
                        }
                        remoteSize = new FileInfo(tempPath).Length;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("sftp.pull", $"Failed to download '{remoteFile}': {ex.Message}");
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                        continue;
                    }

                    FileChangeKind kind;
                    long? targetSize = null;
                    if (!File.Exists(tgtAbs))
                    {
                        kind = FileChangeKind.New;
                    }
                    else
                    {
                        targetSize = new FileInfo(tgtAbs).Length;
                        kind = HashEqualsLocal(tempPath, tgtAbs) ? FileChangeKind.Identical : FileChangeKind.Modified;
                    }

                    plan.Changes.Add(new FileChange
                    {
                        RelativePath = rel,
                        Kind = kind,
                        SourceAbsolutePath = tempPath, // points at the downloaded copy
                        TargetAbsolutePath = tgtAbs,
                        SourceSize = remoteSize,
                        TargetSize = targetSize,
                        Apply = kind == FileChangeKind.New,
                    });
                }

                sftp.Disconnect();
            }
            catch (Exception ex)
            {
                AppLogger.Error("sftp.pull", "Pull preview failed", ex);
                plan.Warnings.Add("SFTP pull preview failed: " + ex.Message);
            }

            return plan;
        }

        private static IEnumerable<string> EnumerateRemoteFiles(SftpClient sftp, string remoteDir)
        {
            foreach (var entry in sftp.ListDirectory(remoteDir))
            {
                if (entry.Name == "." || entry.Name == "..") continue;
                if (entry.IsDirectory)
                {
                    foreach (var sub in EnumerateRemoteFiles(sftp, entry.FullName))
                    {
                        yield return sub;
                    }
                }
                else if (entry.IsRegularFile)
                {
                    yield return entry.FullName;
                }
            }
        }

        private static bool HashEqualsLocal(string a, string b)
        {
            using var sha = SHA256.Create();
            byte[] ha, hb;
            using (var s = File.OpenRead(a)) ha = sha.ComputeHash(s);
            using (var s = File.OpenRead(b)) hb = sha.ComputeHash(s);
            if (ha.Length != hb.Length) return false;
            for (var i = 0; i < ha.Length; i++)
            {
                if (ha[i] != hb[i]) return false;
            }
            return true;
        }

        private SshClient OpenSsh(string host, int port, string user, string password)
        {
            _sshClientFactory.EnsureHostKeyPolicyConfigured();
            return _sshClientFactory.CreateSshClient(
                host,
                port,
                user,
                password,
                TimeSpan.FromSeconds(10));
        }

        private SftpClient OpenSftp(string host, int port, string user, string password)
        {
            _sshClientFactory.EnsureHostKeyPolicyConfigured();
            return _sshClientFactory.CreateSftpClient(
                host,
                port,
                user,
                password,
                TimeSpan.FromSeconds(10));
        }

        private static bool TryValidateApplyPlan(
            AssetPushPlan plan,
            out string hostPart,
            out string projectRoot,
            out string resolvedTarget,
            out bool remoteIsWindows,
            out string issue)
        {
            hostPart = string.Empty;
            projectRoot = string.Empty;
            resolvedTarget = string.Empty;
            remoteIsWindows = false;
            issue = string.Empty;

            if (!plan.RemoteIsWindows.HasValue)
            {
                issue = "The preview did not record the detected remote operating system.";
                return false;
            }

            remoteIsWindows = plan.RemoteIsWindows.Value;
            if (!TryParseSshPath(
                    plan.TargetRoot,
                    out var projectHostPart,
                    out var candidateProjectRoot))
            {
                issue = "The original project root is not a recognised SSH path.";
                return false;
            }

            if (!TryParseSshPath(
                    plan.ResolvedTargetPath,
                    out hostPart,
                    out var candidateTarget))
            {
                issue = "The resolved target is not a recognised SSH path.";
                return false;
            }

            if (!string.Equals(
                    projectHostPart,
                    hostPart,
                    StringComparison.OrdinalIgnoreCase))
            {
                issue = "The resolved target host differs from the original project host.";
                return false;
            }

            if (!LibraryPathContainment.TryGetRemoteDescendantSegments(
                    candidateProjectRoot,
                    candidateTarget,
                    remoteIsWindows,
                    allowEqual: true,
                    out projectRoot,
                    out resolvedTarget,
                    out _,
                    out issue))
            {
                return false;
            }

            foreach (var change in plan.Changes)
            {
                if (change == null)
                {
                    issue = "The plan contains a missing change entry.";
                    return false;
                }
                if (!change.Apply)
                {
                    continue;
                }

                if (!TryValidateChangeForApply(
                        plan,
                        change,
                        resolvedTarget,
                        remoteIsWindows,
                        out _,
                        out var changeIssue))
                {
                    issue = $"File '{change.RelativePath}' is unsafe: {changeIssue}";
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateChangeForApply(
            AssetPushPlan plan,
            FileChange change,
            string resolvedTarget,
            bool remoteIsWindows,
            out string normalizedTarget,
            out string issue)
        {
            normalizedTarget = string.Empty;
            issue = string.Empty;

            if (change == null)
            {
                issue = "The change entry is missing.";
                return false;
            }

            if (!LibraryPathContainment.TryResolveRemoteTarget(
                    resolvedTarget,
                    change.RelativePath,
                    remoteIsWindows,
                    out var expectedTarget,
                    out issue))
            {
                return false;
            }

            if (!LibraryPathContainment.TryValidateRemotePathWithinRoot(
                    resolvedTarget,
                    change.TargetAbsolutePath,
                    remoteIsWindows,
                    allowEqual: false,
                    out normalizedTarget,
                    out issue))
            {
                return false;
            }

            if (!LibraryPathContainment.RemotePathsEqual(
                    expectedTarget,
                    normalizedTarget,
                    remoteIsWindows))
            {
                issue = "The target no longer matches the previewed relative file path.";
                return false;
            }

            if (plan.Asset == null || string.IsNullOrWhiteSpace(plan.Asset.AbsoluteRoot))
            {
                issue = "The local asset root is missing.";
                return false;
            }

            if (!LibraryPathContainment.TryResolveLocalDescendant(
                    plan.Asset.AbsoluteRoot,
                    change.RelativePath,
                    inspectRootForReparsePoint: true,
                    out var expectedSource,
                    out issue))
            {
                issue = "The local source is unsafe: " + issue;
                return false;
            }

            if (!LibraryPathContainment.LocalPathsEqual(
                    expectedSource,
                    change.SourceAbsolutePath))
            {
                issue = "The local source no longer matches the asset-relative file path.";
                return false;
            }

            if (!File.Exists(expectedSource))
            {
                issue = "The local source file no longer exists.";
                return false;
            }

            return true;
        }

        private static bool TryParseSshPath(string sshPath, out string host, out string remotePath)
        {
            host = string.Empty;
            remotePath = string.Empty;
            if (string.IsNullOrWhiteSpace(sshPath)) return false;
            var sep = sshPath.IndexOf(':');
            if (sep <= 1 || sep >= sshPath.Length - 1) return false;
            host = sshPath.Substring(0, sep).Trim();
            remotePath = sshPath.Substring(sep + 1);
            return Regex.IsMatch(host, @"^[A-Za-z0-9._@-]+$") &&
                   !string.IsNullOrWhiteSpace(remotePath) &&
                   remotePath.IndexOfAny(new[] { '\r', '\n', '"' }) < 0;
        }

        private static (string hostname, string user) SplitHost(string hostPart)
        {
            var at = hostPart.IndexOf('@');
            if (at > 0)
            {
                return (hostPart.Substring(at + 1), hostPart.Substring(0, at));
            }
            return (hostPart, null);
        }

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

        // SFTP always uses forward slashes on the wire. Windows OpenSSH server
        // expects drive-letter paths to be prefixed with '/' (e.g. /D:/repos/...).
        // Linux/macOS use the usual /path layout.
        private static string NormaliseSftpPath(string path, bool remoteIsWindows)
        {
            var p = (path ?? string.Empty).Replace('\\', '/');
            if (remoteIsWindows && Regex.IsMatch(p, @"^[A-Za-z]:/"))
            {
                p = "/" + p;
            }
            return p;
        }

        private static bool HashEqualsRemote(SftpClient sftp, string localPath, string remoteSftpPath)
        {
            using var sha = SHA256.Create();
            byte[] localHash;
            using (var s = File.OpenRead(localPath))
            {
                localHash = sha.ComputeHash(s);
            }

            byte[] remoteHash;
            using (var ms = new MemoryStream())
            {
                sftp.DownloadFile(remoteSftpPath, ms);
                ms.Position = 0;
                using var sha2 = SHA256.Create();
                remoteHash = sha2.ComputeHash(ms);
            }

            if (localHash.Length != remoteHash.Length) return false;
            for (var i = 0; i < localHash.Length; i++)
            {
                if (localHash[i] != remoteHash[i]) return false;
            }
            return true;
        }

        private static List<string> WalkAssetFiles(string root, IList<string> warnings)
        {
            var files = new List<string>();
            WalkInto(new DirectoryInfo(root), root, files, warnings);
            return files;
        }

        private static void WalkInto(DirectoryInfo dir, string assetRoot, List<string> files, IList<string> warnings)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"Skipping reparse point '{dir.FullName}'.");
                return;
            }
            foreach (var file in dir.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (ExcludedFileNames.Contains(file.Name)) continue;
                files.Add(Path.GetRelativePath(assetRoot, file.FullName));
            }
            foreach (var sub in dir.EnumerateDirectories())
            {
                WalkInto(sub, assetRoot, files, warnings);
            }
        }

        private static bool IsSafeRelative(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return false;
            var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return segments.All(s => s != ".." && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }

        private static bool IsUnder(string path, string root)
        {
            var p = Path.GetFullPath(path);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p, r, StringComparison.OrdinalIgnoreCase);
        }
    }
}
