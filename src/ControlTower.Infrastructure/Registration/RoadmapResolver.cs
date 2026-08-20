using System;
using System.IO;
using System.Linq;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Ssh;

namespace ControlTower.Infrastructure.Registration
{
    /// <summary>
    /// Reads roadmap.yaml from a project. Prefers SSH remote when the project
    /// has an SshTarget, otherwise reads from the local filesystem.
    /// </summary>
    public sealed class RoadmapResolver : IRoadmapResolver
    {
        private static readonly string[] LocalCandidates =
        {
            Path.Combine(".github", "roadmap.yaml"),
            Path.Combine("resources", "roadmap.yaml"),
        };

        private static readonly string[] RemoteCandidates =
        {
            ".github/roadmap.yaml",
            "resources/roadmap.yaml",
        };

        private readonly ISshService _sshService;
        private readonly IStoreProvider _storeProvider;
        private readonly ICredentialStore _credentialStore;

        public RoadmapResolver()
            : this(null, null, null)
        {
        }

        public RoadmapResolver(
            ISshService sshService,
            IStoreProvider storeProvider,
            ICredentialStore credentialStore)
        {
            _sshService = sshService;
            _storeProvider = storeProvider;
            _credentialStore = credentialStore;
        }

        public RoadmapContent Resolve(ProjectDefinition project)
        {
            if (project == null)
            {
                return null;
            }

            // SSH first when available
            var sshTarget = project.Locations?.SshTarget;
            if (!string.IsNullOrWhiteSpace(sshTarget) && _sshService != null && _credentialStore != null)
            {
                var fromSsh = TryReadFromSsh(sshTarget);
                if (fromSsh != null)
                {
                    return fromSsh;
                }
            }

            // Local fallback (project root or local copy)
            var root = project.Locations?.LocalPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = project.ProjectRootPath;
            }
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                foreach (var rel in LocalCandidates)
                {
                    var candidate = Path.Combine(root, rel);
                    if (File.Exists(candidate))
                    {
                        return new RoadmapContent
                        {
                            Yaml = File.ReadAllText(candidate),
                            SourceLabel = rel,
                        };
                    }
                }
            }

            return null;
        }

        private RoadmapContent TryReadFromSsh(string sshTarget)
        {
            // Parse user@host:remotepath
            var separator = sshTarget.IndexOf(':');
            if (separator <= 1 || separator >= sshTarget.Length - 1)
            {
                return null;
            }
            var hostPart = sshTarget.Substring(0, separator);
            var remotePath = sshTarget.Substring(separator + 1).Trim();

            string user = null;
            var hostname = hostPart;
            var atIdx = hostPart.IndexOf('@');
            if (atIdx > 0)
            {
                user = hostPart.Substring(0, atIdx);
                hostname = hostPart.Substring(atIdx + 1);
            }

            // Find matching store for credentials/port
            var store = FindStore(hostname, user);
            if (store == null)
            {
                return null;
            }

            var password = string.IsNullOrWhiteSpace(store.CredentialTarget)
                ? string.Empty
                : _credentialStore.GetPassword(store.CredentialTarget);
            var sshUser = user ?? store.User;
            int port = store.Port > 0 ? store.Port : 22;

            if (!TryDetectRemoteIsWindows(
                hostname,
                port,
                sshUser,
                password,
                out var isWindows))
            {
                return null;
            }

            foreach (var rel in RemoteCandidates)
            {
                var remoteFile = isWindows
                    ? remotePath.TrimEnd('/', '\\') + "\\" + rel.Replace('/', '\\')
                    : remotePath.TrimEnd('/', '\\') + "/" + rel;

                string quotedRemoteFile;
                try
                {
                    quotedRemoteFile = isWindows
                        ? SshCommandQuoter.QuoteWindows(remoteFile)
                        : SshCommandQuoter.QuotePosix(remoteFile);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(
                        "ssh/unsafe-path: Refusing to build a remote command: the path contains unsafe characters.",
                        ex);
                }

                var cmd = isWindows
                    ? $"if exist {quotedRemoteFile} type {quotedRemoteFile}"
                    : $"[ -f {quotedRemoteFile} ] && cat {quotedRemoteFile}";

                var result = _sshService.RunCommand(hostname, port, sshUser, password, cmd);
                if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
                {
                    return new RoadmapContent
                    {
                        Yaml = result.Output,
                        SourceLabel = rel + " (ssh)",
                    };
                }
            }

            return null;
        }

        private bool TryDetectRemoteIsWindows(
            string hostname,
            int port,
            string user,
            string password,
            out bool isWindows)
        {
            isWindows = false;

            var windowsProbe = _sshService.RunCommand(
                hostname,
                port,
                user,
                password,
                "echo %OS%");
            if (!windowsProbe.Success)
            {
                return false;
            }

            if (windowsProbe.Output.IndexOf("Windows_NT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isWindows = true;
                return true;
            }

            var posixProbe = _sshService.RunCommand(
                hostname,
                port,
                user,
                password,
                "uname -s");
            return posixProbe.Success && IsKnownPosixOs(posixProbe.Output.Trim());
        }

        private static bool IsKnownPosixOs(string value)
        {
            return value.StartsWith("Linux", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Darwin", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("FreeBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("OpenBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("NetBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("SunOS", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("CYGWIN", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("MINGW", StringComparison.OrdinalIgnoreCase);
        }

        private RepoStore FindStore(string hostname, string user)
        {
            if (_storeProvider == null)
            {
                return null;
            }
            var stores = _storeProvider.GetStores();
            if (stores == null || stores.Count == 0)
            {
                return null;
            }

            var match = stores.FirstOrDefault(s =>
                s.IsSsh &&
                string.Equals(s.Host, hostname, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(user) &&
                string.Equals(s.User, user, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }

            return stores.FirstOrDefault(s =>
                s.IsSsh && string.Equals(s.Host, hostname, StringComparison.OrdinalIgnoreCase));
        }
    }
}
