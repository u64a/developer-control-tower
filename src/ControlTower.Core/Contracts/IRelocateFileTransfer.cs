#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Describes one end of a file-transfer operation. <see cref="IsSsh"/>
    /// flips the transport: local endpoints walk the disk directly, SSH
    /// endpoints upload / download via SFTP.
    /// </summary>
    public sealed class RelocateEndpoint
    {
        public bool IsSsh { get; set; }
        public string LocalPath { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string User { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
    }

    public sealed class RelocateTransferResult
    {
        public bool Success { get; set; }
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public long BytesCopied { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Moves files between local folders and SSH-hosted folders. Used by
    /// Relocate to migrate <c>.controltower/</c> and (optionally) the
    /// per-project list of ignored files. SSH→SSH transfers are
    /// intentionally not supported here — Relocate routes those through a
    /// local store.
    /// </summary>
    public interface IRelocateFileTransfer
    {
        /// <summary>
        /// Recursively copy <paramref name="relativeSubdir"/> from
        /// <paramref name="source"/> into <paramref name="destination"/>.
        /// Files exceeding <paramref name="maxFileBytes"/> are skipped
        /// with a warning entry.
        /// </summary>
        Task<RelocateTransferResult> CopyDirectoryAsync(
            RelocateEndpoint source,
            RelocateEndpoint destination,
            string relativeSubdir,
            long maxFileBytes,
            CancellationToken ct);

        /// <summary>
        /// Copy a specific list of repo-relative paths. Each entry must
        /// be a path under the endpoint root; path-traversal is rejected.
        /// </summary>
        Task<RelocateTransferResult> CopyFilesAsync(
            RelocateEndpoint source,
            RelocateEndpoint destination,
            IEnumerable<string> relativePaths,
            long maxFileBytes,
            CancellationToken ct);
    }
}
