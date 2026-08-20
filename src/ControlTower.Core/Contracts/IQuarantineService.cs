#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Moves a non-empty source folder out of the way before a clone.
    /// </summary>
    /// <remarks>
    /// <para>The destination is
    /// <c>%USERPROFILE%\projectmgr-quarantine-&lt;UTC yyyyMMdd-HHmmss&gt;\&lt;slug&gt;\</c>.
    /// The quarantine root is created on demand.</para>
    /// <para>Atomic <see cref="System.IO.Directory.Move(string, string)"/>
    /// is used when possible; cross-volume moves fall back to a
    /// recursive copy followed by delete.</para>
    /// </remarks>
    public interface IQuarantineService
    {
        /// <summary>
        /// Moves <paramref name="sourcePath"/> into the quarantine root
        /// under a folder named <paramref name="slug"/>.
        /// </summary>
        /// <returns>The absolute path of the quarantine destination
        /// after the move.</returns>
        /// <exception cref="System.IO.DirectoryNotFoundException">
        /// Thrown when <paramref name="sourcePath"/> does not exist.
        /// </exception>
        Task<string> QuarantineAsync(string sourcePath, string slug, CancellationToken ct);
    }
}
