#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Ssh;
using Renci.SshNet;

namespace ControlTower.Infrastructure.FileSystem
{
    /// <summary>
    /// Routes file-transfer work for Relocate across three legs:
    /// local→local (recursive disk copy), local→SSH (SFTP upload),
    /// SSH→local (SFTP download). SSH→SSH is intentionally not supported;
    /// callers route those through a local store first.
    /// </summary>
    public sealed class RelocateFileTransfer : IRelocateFileTransfer
    {
        private readonly SshNetClientFactory? _sshClientFactory;

        public RelocateFileTransfer()
            : this(null)
        {
        }

        public RelocateFileTransfer(SshNetClientFactory? sshClientFactory)
        {
            _sshClientFactory = sshClientFactory;
        }

        public Task<RelocateTransferResult> CopyDirectoryAsync(
            RelocateEndpoint source,
            RelocateEndpoint destination,
            string relativeSubdir,
            long maxFileBytes,
            CancellationToken ct)
        {
            return Task.Run(() => CopyDirectoryCore(source, destination, relativeSubdir, maxFileBytes, ct), ct);
        }

        public Task<RelocateTransferResult> CopyFilesAsync(
            RelocateEndpoint source,
            RelocateEndpoint destination,
            IEnumerable<string> relativePaths,
            long maxFileBytes,
            CancellationToken ct)
        {
            return Task.Run(() => CopyFilesCore(source, destination, relativePaths, maxFileBytes, ct), ct);
        }

        private RelocateTransferResult CopyDirectoryCore(
            RelocateEndpoint source,
            RelocateEndpoint destination,
            string relativeSubdir,
            long maxFileBytes,
            CancellationToken ct)
        {
            var result = new RelocateTransferResult();
            if (source == null || destination == null)
            {
                result.ErrorMessage = "Source or destination endpoint missing.";
                return result;
            }

            if (source.IsSsh && destination.IsSsh)
            {
                result.ErrorMessage = "SSH→SSH transfer is not supported. Route via a local store.";
                return result;
            }

            relativeSubdir ??= string.Empty;
            relativeSubdir = relativeSubdir.Replace('\\', '/').Trim('/');

            try
            {
                if (!source.IsSsh && !destination.IsSsh)
                {
                    CopyLocalDirectory(source.LocalPath, destination.LocalPath, relativeSubdir, maxFileBytes, result, ct);
                }
                else if (!source.IsSsh && destination.IsSsh)
                {
                    UploadDirectory(source.LocalPath, destination, relativeSubdir, maxFileBytes, result, ct);
                }
                else
                {
                    DownloadDirectory(source, destination.LocalPath, relativeSubdir, maxFileBytes, result, ct);
                }

                if (string.IsNullOrEmpty(result.ErrorMessage))
                {
                    result.Success = true;
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Transfer cancelled.";
            }
            catch (Exception ex)
            {
                AppLogger.Error("relocate.transfer", "CopyDirectory failed", ex);
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private RelocateTransferResult CopyFilesCore(
            RelocateEndpoint source,
            RelocateEndpoint destination,
            IEnumerable<string> relativePaths,
            long maxFileBytes,
            CancellationToken ct)
        {
            var result = new RelocateTransferResult();
            if (source == null || destination == null)
            {
                result.ErrorMessage = "Source or destination endpoint missing.";
                return result;
            }
            if (source.IsSsh && destination.IsSsh)
            {
                result.ErrorMessage = "SSH→SSH transfer is not supported.";
                return result;
            }

            var list = (relativePaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Replace('\\', '/').Trim('/'))
                .Where(p => IsSafeRelative(p))
                .ToList();

            try
            {
                if (!source.IsSsh && !destination.IsSsh)
                {
                    foreach (var rel in list)
                    {
                        ct.ThrowIfCancellationRequested();
                        var srcAbs = ResolveLocal(source.LocalPath, rel);
                        var tgtAbs = ResolveLocal(destination.LocalPath, rel);
                        if (srcAbs == null || tgtAbs == null) continue;
                        if (!File.Exists(srcAbs)) continue;

                        var fi = new FileInfo(srcAbs);
                        if (fi.Length > maxFileBytes)
                        {
                            result.Warnings.Add($"Skipped large file (>{maxFileBytes / (1024 * 1024)} MB): {rel}");
                            result.FilesSkipped++;
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(tgtAbs)!);
                        File.Copy(srcAbs, tgtAbs, overwrite: true);
                        result.FilesCopied++;
                        result.BytesCopied += fi.Length;
                    }
                }
                else if (!source.IsSsh && destination.IsSsh)
                {
                    using var sftp = OpenSftp(destination);
                    sftp.Connect();
                    bool remoteIsWindows = ContainsWindowsSeparator(destination.RemotePath);
                    foreach (var rel in list)
                    {
                        ct.ThrowIfCancellationRequested();
                        var srcAbs = ResolveLocal(source.LocalPath, rel);
                        if (srcAbs == null || !File.Exists(srcAbs)) continue;
                        var fi = new FileInfo(srcAbs);
                        if (fi.Length > maxFileBytes)
                        {
                            result.Warnings.Add($"Skipped large file (>{maxFileBytes / (1024 * 1024)} MB): {rel}");
                            result.FilesSkipped++;
                            continue;
                        }
                        var sftpDest = NormaliseSftpPath(JoinRemote(destination.RemotePath, rel, remoteIsWindows ? '\\' : '/'), remoteIsWindows);
                        EnsureRemoteDirectory(sftp, GetParentSftpPath(sftpDest));
                        using var fs = File.OpenRead(srcAbs);
                        sftp.UploadFile(fs, sftpDest, canOverride: true);
                        result.FilesCopied++;
                        result.BytesCopied += fi.Length;
                    }
                    sftp.Disconnect();
                }
                else
                {
                    using var sftp = OpenSftp(source);
                    sftp.Connect();
                    bool remoteIsWindows = ContainsWindowsSeparator(source.RemotePath);
                    foreach (var rel in list)
                    {
                        ct.ThrowIfCancellationRequested();
                        var sftpSrc = NormaliseSftpPath(JoinRemote(source.RemotePath, rel, remoteIsWindows ? '\\' : '/'), remoteIsWindows);
                        if (!sftp.Exists(sftpSrc)) continue;
                        var attrs = sftp.GetAttributes(sftpSrc);
                        if (attrs.Size > maxFileBytes)
                        {
                            result.Warnings.Add($"Skipped large file (>{maxFileBytes / (1024 * 1024)} MB): {rel}");
                            result.FilesSkipped++;
                            continue;
                        }
                        var tgtAbs = ResolveLocal(destination.LocalPath, rel);
                        if (tgtAbs == null) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(tgtAbs)!);
                        using var fs = File.Create(tgtAbs);
                        sftp.DownloadFile(sftpSrc, fs);
                        result.FilesCopied++;
                        result.BytesCopied += attrs.Size;
                    }
                    sftp.Disconnect();
                }

                if (string.IsNullOrEmpty(result.ErrorMessage))
                {
                    result.Success = true;
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Transfer cancelled.";
            }
            catch (Exception ex)
            {
                AppLogger.Error("relocate.transfer", "CopyFiles failed", ex);
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static void CopyLocalDirectory(
            string sourceRoot, string destRoot, string relativeSubdir, long maxFileBytes,
            RelocateTransferResult result, CancellationToken ct)
        {
            string srcDir = string.IsNullOrEmpty(relativeSubdir)
                ? sourceRoot
                : Path.Combine(sourceRoot, relativeSubdir.Replace('/', Path.DirectorySeparatorChar));
            string tgtDir = string.IsNullOrEmpty(relativeSubdir)
                ? destRoot
                : Path.Combine(destRoot, relativeSubdir.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(srcDir))
            {
                result.ErrorMessage = $"Source directory not found: {srcDir}";
                return;
            }

            Directory.CreateDirectory(tgtDir);

            foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var fi = new FileInfo(file);
                var rel = Path.GetRelativePath(srcDir, file);
                if (fi.Length > maxFileBytes)
                {
                    result.Warnings.Add($"Skipped large file (>{maxFileBytes / (1024 * 1024)} MB): {rel}");
                    result.FilesSkipped++;
                    continue;
                }
                var dest = Path.Combine(tgtDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                result.FilesCopied++;
                result.BytesCopied += fi.Length;
            }
        }

        private void UploadDirectory(
            string sourceRoot, RelocateEndpoint destination, string relativeSubdir, long maxFileBytes,
            RelocateTransferResult result, CancellationToken ct)
        {
            string srcDir = string.IsNullOrEmpty(relativeSubdir)
                ? sourceRoot
                : Path.Combine(sourceRoot, relativeSubdir.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(srcDir))
            {
                result.ErrorMessage = $"Source directory not found: {srcDir}";
                return;
            }

            using var sftp = OpenSftp(destination);
            sftp.Connect();

            bool remoteIsWindows = ContainsWindowsSeparator(destination.RemotePath);
            char remoteSep = remoteIsWindows ? '\\' : '/';
            string remoteBase = string.IsNullOrEmpty(relativeSubdir)
                ? destination.RemotePath
                : JoinRemote(destination.RemotePath, relativeSubdir, remoteSep);

            EnsureRemoteDirectory(sftp, NormaliseSftpPath(remoteBase, remoteIsWindows));

            foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var fi = new FileInfo(file);
                var rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (fi.Length > maxFileBytes)
                {
                    result.Warnings.Add($"Skipped large file (>{maxFileBytes / (1024 * 1024)} MB): {rel}");
                    result.FilesSkipped++;
                    continue;
                }
                var relRemote = remoteIsWindows ? rel.Replace('/', '\\') : rel;
                var remoteAbs = JoinRemote(remoteBase, relRemote, remoteSep);
                var sftpDest = NormaliseSftpPath(remoteAbs, remoteIsWindows);
                EnsureRemoteDirectory(sftp, GetParentSftpPath(sftpDest));
                using var fs = File.OpenRead(file);
                sftp.UploadFile(fs, sftpDest, canOverride: true);
                result.FilesCopied++;
                result.BytesCopied += fi.Length;
            }

            sftp.Disconnect();
        }

        private void DownloadDirectory(
            RelocateEndpoint source, string destRoot, string relativeSubdir, long maxFileBytes,
            RelocateTransferResult result, CancellationToken ct)
        {
            using var sftp = OpenSftp(source);
            sftp.Connect();

            bool remoteIsWindows = ContainsWindowsSeparator(source.RemotePath);
            char remoteSep = remoteIsWindows ? '\\' : '/';
            string remoteBase = string.IsNullOrEmpty(relativeSubdir)
                ? source.RemotePath
                : JoinRemote(source.RemotePath, relativeSubdir, remoteSep);
            string remoteBaseSftp = NormaliseSftpPath(remoteBase, remoteIsWindows);

            if (!sftp.Exists(remoteBaseSftp))
            {
                result.ErrorMessage = $"Remote directory not found: {remoteBaseSftp}";
                sftp.Disconnect();
                return;
            }

            string tgtDir = string.IsNullOrEmpty(relativeSubdir)
                ? destRoot
                : Path.Combine(destRoot, relativeSubdir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(tgtDir);

            foreach (var remoteFile in EnumerateRemoteFiles(sftp, remoteBaseSftp))
            {
                ct.ThrowIfCancellationRequested();
                var attrs = sftp.GetAttributes(remoteFile);
                var relSftp = remoteFile.Substring(remoteBaseSftp.Length).TrimStart('/');
                var rel = relSftp.Replace('/', Path.DirectorySeparatorChar);
                if (attrs.Size > maxFileBytes)
                {
                    result.Warnings.Add($"Skipped large file (>{maxFileBytes / (1024 * 1024)} MB): {rel}");
                    result.FilesSkipped++;
                    continue;
                }
                var tgtAbs = Path.GetFullPath(Path.Combine(tgtDir, rel));
                if (!IsUnder(tgtAbs, tgtDir))
                {
                    result.Warnings.Add($"Skipped path-escape: {rel}");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(tgtAbs)!);
                using var fs = File.Create(tgtAbs);
                sftp.DownloadFile(remoteFile, fs);
                result.FilesCopied++;
                result.BytesCopied += attrs.Size;
            }

            sftp.Disconnect();
        }

        // ----- Helpers (mirror SshAssetTransferService patterns) -----

        private SftpClient OpenSftp(RelocateEndpoint endpoint)
        {
            if (_sshClientFactory == null)
            {
                throw new HostKeyUnconfirmedException(
                    "ssh/no-host-key-store",
                    "No host-key policy is configured. Refusing to connect (ADR-005).");
            }

            _sshClientFactory.EnsureHostKeyPolicyConfigured();
            int port = endpoint.Port > 0 ? endpoint.Port : 22;
            return _sshClientFactory.CreateSftpClient(
                endpoint.Host,
                port,
                endpoint.User,
                endpoint.Password,
                TimeSpan.FromSeconds(15));
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

        private static string JoinRemote(string a, string b, char sep)
        {
            if (string.IsNullOrEmpty(b)) return a;
            return a.TrimEnd('/', '\\') + sep + b.TrimStart('/', '\\');
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

        private static string GetParentSftpPath(string sftpPath)
        {
            var idx = sftpPath.LastIndexOf('/');
            return idx <= 0 ? string.Empty : sftpPath.Substring(0, idx);
        }

        private static void EnsureRemoteDirectory(SftpClient sftp, string remoteDir)
        {
            if (string.IsNullOrEmpty(remoteDir)) return;
            var segments = remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return;

            bool windowsDrive = Regex.IsMatch(segments[0], @"^[A-Za-z]:$");
            string current = windowsDrive ? "/" + segments[0] : string.Empty;
            int startIdx = windowsDrive ? 1 : 0;

            for (int i = startIdx; i < segments.Length; i++)
            {
                var seg = segments[i];
                current = string.IsNullOrEmpty(current) ? "/" + seg : current + "/" + seg;
                bool exists = false;
                try { exists = sftp.Exists(current); }
                catch { }
                if (!exists)
                {
                    try { sftp.CreateDirectory(current); }
                    catch (Exception ex) { AppLogger.Warn("relocate.sftp.mkdir", $"CreateDirectory failed for '{current}': {ex.Message}"); }
                }
            }
        }

        private static bool ContainsWindowsSeparator(string path)
            => !string.IsNullOrEmpty(path) && path.IndexOf('\\') >= 0;

        private static bool IsSafeRelative(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return false;
            if (rel.Contains("..")) return false;
            if (Path.IsPathRooted(rel)) return false;
            return true;
        }

        private static string? ResolveLocal(string root, string rel)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsUnder(full, root)) return null;
                return full;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUnder(string path, string root)
        {
            var p = Path.GetFullPath(path);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, r, StringComparison.OrdinalIgnoreCase);
        }
    }
}
