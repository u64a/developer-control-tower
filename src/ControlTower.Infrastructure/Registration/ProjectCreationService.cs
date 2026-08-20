using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Ssh;

namespace ControlTower.Infrastructure.Registration
{
    public sealed class ProjectCreationService : IProjectCreationService
    {
        private static readonly Regex SafeFolderRegex =
            new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);

        private readonly IStoreProvider _storeProvider;
        private readonly IProjectRegistrationService _registrationService;
        private readonly ISshService _sshService;
        private readonly ICredentialStore _credentialStore;

        public ProjectCreationService(
            IStoreProvider storeProvider,
            IProjectRegistrationService registrationService,
            ISshService sshService,
            ICredentialStore credentialStore)
        {
            _storeProvider = storeProvider;
            _registrationService = registrationService;
            _sshService = sshService;
            _credentialStore = credentialStore;
        }

        public ProjectCreationResult CreateProject(ProjectCreationRequest request)
        {
            if (request == null)
            {
                return ProjectCreationResult.Fail("Request is required.");
            }

            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return ProjectCreationResult.Fail("Display name is required.");
            }

            if (string.IsNullOrWhiteSpace(request.StoreId))
            {
                return ProjectCreationResult.Fail("Store is required.");
            }

            var store = _storeProvider.GetStore(request.StoreId);
            if (store == null)
            {
                return ProjectCreationResult.Fail($"Store '{request.StoreId}' not found.");
            }

            var projectId = string.IsNullOrWhiteSpace(request.ProjectId)
                ? BuildProjectId(request.DisplayName)
                : request.ProjectId.Trim();

            var folder = string.IsNullOrWhiteSpace(request.Folder)
                ? projectId
                : request.Folder.Trim();

            // Reject anything that could escape the store root or inject shell metacharacters.
            if (!SafeFolderRegex.IsMatch(folder))
            {
                return ProjectCreationResult.Fail(
                    "Folder name must start with a letter or digit and contain only letters, digits, '.', '_' or '-' (max 100 chars).");
            }

            if (store.IsSsh)
            {
                return CreateSshProject(store, projectId, folder, request);
            }
            else
            {
                return CreateLocalProject(store, projectId, folder, request);
            }
        }

        private ProjectCreationResult CreateLocalProject(
            RepoStore store, string projectId, string folder, ProjectCreationRequest request)
        {
            // Defence in depth: confirm the resolved path is inside the store root.
            string localPath;
            try
            {
                var combined = Path.GetFullPath(Path.Combine(store.Root, folder));
                var rootFull = Path.GetFullPath(store.Root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    return ProjectCreationResult.Fail("Resolved project path falls outside the store root.");
                }
                localPath = combined;
            }
            catch (Exception)
            {
                return ProjectCreationResult.Fail("Invalid folder name for this store.");
            }

            if (Directory.Exists(localPath) && !request.AdoptExisting)
            {
                return ProjectCreationResult.Exists(projectId, localPath);
            }

            Directory.CreateDirectory(localPath);

            // git init if not already a repo
            var gitDir = Path.Combine(localPath, ".git");
            if (!Directory.Exists(gitDir))
            {
                RunGitInit(localPath);
            }

            // Metadata (.controltower) is written to the central stub by
            // RegisterProject below — never into the repo working tree.

            // ProjectCreationService is invoked both for fresh local creation
            // AND for re-saving an existing project from the Edit dialog (the
            // dialog reuses the same Save button). Phase C added a duplicate-
            // id guard to RegisterProject; allow overwrite here because every
            // path through CreateProject is an authoritative author of the
            // entry (it just created or adopted the folder on disk).
            var regResult = _registrationService.RegisterProject(new ProjectRegistrationRequest
            {
                ProjectId = projectId,
                DisplayName = request.DisplayName,
                Summary = request.Summary,
                LifecycleState = string.IsNullOrWhiteSpace(request.LifecycleState) ? "active" : request.LifecycleState.Trim(),
                LocalPath = localPath,
                GitHubUrl = request.GitHubUrl,
                AdoUrl = request.AdoUrl,
                Group = request.Group,
                AllowOverwrite = true
            });

            if (!regResult.Success)
            {
                return ProjectCreationResult.Fail($"Folder created but registration failed: {regResult.Message}");
            }

            return ProjectCreationResult.Ok(projectId, localPath, "Project created in local store.");
        }

        private ProjectCreationResult CreateSshProject(
            RepoStore store, string projectId, string folder, ProjectCreationRequest request)
        {
            // Fail-safe: reject ambiguous User/Host before any remote mutation.
            // '@' in User or ':' in Host would make the persisted "user@host:path"
            // target unparseable on reload.
            if (!string.IsNullOrEmpty(store.User) && store.User.IndexOf('@') >= 0)
            {
                return ProjectCreationResult.Fail(
                    $"Store '{store.Id}' has an ambiguous User (contains '@'). " +
                    "SSH target syntax cannot represent UPN-style usernames.");
            }
            if (!string.IsNullOrEmpty(store.Host) && store.Host.IndexOf(':') >= 0)
            {
                return ProjectCreationResult.Fail(
                    $"Store '{store.Id}' has an ambiguous Host (contains ':'). " +
                    "Use a hostname or DNS name instead of an IPv6 literal.");
            }

            // Reject blank Root before any SSH interaction — an empty root would
            // synthesize paths like "/folder" or "\folder" from nothing.
            if (string.IsNullOrWhiteSpace(store.Root))
            {
                return ProjectCreationResult.Fail(
                    $"Store '{store.Id}' has a blank Root. " +
                    "Configure a valid remote directory before creating projects.");
            }

            // Reject relative store roots before any remote mutation.
            // A relative root (e.g. "repos") cannot be reliably resolved to an absolute
            // remote path, so BuildVsCodeSsh would produce an incorrect launch target.
            // Require an absolute path (POSIX '/' prefix or Windows drive letter) up front.
            if (!IsAbsoluteRemotePath(store.Root))
            {
                return ProjectCreationResult.Fail(
                    $"Store '{store.Id}' has a relative Root ('{store.Root}'). " +
                    "SSH stores must use an absolute path (starting with '/' for POSIX " +
                    "or a drive letter e.g. 'C:\\' for Windows) to ensure correct launch metadata.");
            }

            // Reject hosts shorter than 2 characters — too short for a valid hostname.
            if (string.IsNullOrWhiteSpace(store.Host) || store.Host.Trim().Length < 2)
            {
                return ProjectCreationResult.Fail(
                    $"Store '{store.Id}' has an invalid Host (must be at least 2 characters). " +
                    "Configure a valid hostname or IP address.");
            }

            var password = string.Empty;
            if (!string.IsNullOrWhiteSpace(store.CredentialTarget))
            {
                password = _credentialStore.GetPassword(store.CredentialTarget);
            }

            if (string.IsNullOrEmpty(password))
            {
                return ProjectCreationResult.Fail(
                    $"No credential found for target '{store.CredentialTarget}'. " +
                    "Set the SSH password via Settings before creating remote projects.");
            }

            int port = store.Port > 0 ? store.Port : 22;

            // Test connection
            var connResult = _sshService.TestConnection(store.Host, port, store.User, password);
            if (!connResult.Success)
            {
                return ProjectCreationResult.Fail($"SSH connection failed: {connResult.Error}");
            }

            // Detect remote OS explicitly using a two-step probe.
            // Step 1: Windows cmd returns "Windows_NT" for 'echo %OS%'.
            // Step 2: if the first probe does not confirm Windows, verify POSIX via
            //         'uname -s' (available on all POSIX systems, not on Windows).
            // Reject any output that does not match a known OS — in particular,
            // literal '%OS%' is ambiguous because Windows/PowerShell also echoes it.
            var winProbe = _sshService.RunCommand(store.Host, port, store.User, password, "echo %OS%");
            if (!winProbe.Success)
            {
                return ProjectCreationResult.Fail(
                    $"Remote OS probe failed on '{store.Host}': {winProbe.Error}. " +
                    "Verify the SSH connection supports command execution.");
            }

            bool remoteIsWindows;
            if (winProbe.Output.Contains("Windows_NT", StringComparison.OrdinalIgnoreCase))
            {
                remoteIsWindows = true;
            }
            else
            {
                // The echo probe returned something other than "Windows_NT".
                // This could be a POSIX shell (echoes '%OS%' literally) or
                // Windows/PowerShell (also echoes '%OS%' literally).
                // Confirm POSIX by running uname -s, which only exists on POSIX systems.
                var posixProbe = _sshService.RunCommand(store.Host, port, store.User, password, "uname -s");
                if (posixProbe.Success && IsKnownPosixOs(posixProbe.Output.Trim()))
                {
                    remoteIsWindows = false;
                }
                else
                {
                    return ProjectCreationResult.Fail(
                        $"Cannot determine remote OS on '{store.Host}'. " +
                        "'echo %OS%' did not return 'Windows_NT' and 'uname -s' did not return a " +
                        "recognized POSIX name. Verify the SSH connection and remote shell configuration.");
                }
            }

            // Join store root + folder with the remote OS's native separator so
            // the resulting ssh_target round-trips through SshStoreResolver.
            var remotePath = remoteIsWindows
                ? store.Root.TrimEnd('\\', '/') + "\\" + folder
                : store.Root.TrimEnd('\\', '/') + "/" + folder;

            // Check if directory exists — built with the appropriate quoter so no injection.
            string checkCmd;
            string gitInitCmd;
            // mkdirCmd is built here, alongside checkCmd and gitInitCmd, so all three
            // commands use the same already-verified remoteIsWindows classification.
            // CreateDirectory on ISshService is NOT called for this workflow: it runs an
            // independent OS probe that would violate the verified-classification contract.
            string mkdirCmd;
            try
            {
                checkCmd = remoteIsWindows
                    ? $"if exist {SshCommandQuoter.QuoteWindows(remotePath)} (echo EXISTS) else (echo NOTFOUND)"
                    : $"[ -d {SshCommandQuoter.QuotePosix(remotePath)} ] && echo EXISTS || echo NOTFOUND";

                gitInitCmd = remoteIsWindows
                    ? $"cd /d {SshCommandQuoter.QuoteWindows(remotePath)} && git init && git commit --allow-empty -m \"Initial commit\""
                    : $"cd {SshCommandQuoter.QuotePosix(remotePath)} && git init && git commit --allow-empty -m 'Initial commit'";

                mkdirCmd = remoteIsWindows
                    ? $"if not exist {SshCommandQuoter.QuoteWindows(remotePath)} mkdir {SshCommandQuoter.QuoteWindows(remotePath)}"
                    : $"mkdir -p {SshCommandQuoter.QuotePosix(remotePath)}";
            }
            catch (ArgumentException)
            {
                return ProjectCreationResult.Fail("ssh/unsafe-path: Refusing to build a remote command: the path contains unsafe characters.");
            }

            var checkResult = _sshService.RunCommand(store.Host, port, store.User, password, checkCmd);

            // Fail-safe: treat any SSH failure as abort, never as absence.
            // Only exact protocol tokens are safe to act on.
            if (!checkResult.Success)
            {
                return ProjectCreationResult.Fail(
                    $"Remote existence check failed on '{store.Host}': {checkResult.Error}. " +
                    "Aborting to avoid unintended mutation.");
            }

            // Exact-token parse: after trimming surrounding whitespace the entire output
            // must equal exactly "EXISTS" or "NOTFOUND". Substring matching allows
            // mixed-token output such as "EXISTS\nNOTFOUND" to set both flags true,
            // bypassing the abort and reaching remote mutation via the notFound branch
            // when AdoptExisting=true.
            var probeToken = checkResult.Output.Trim();

            if (probeToken != "EXISTS" && probeToken != "NOTFOUND")
            {
                return ProjectCreationResult.Fail(
                    $"Remote existence check returned unexpected output on '{store.Host}': " +
                    $"'{probeToken}'. Expected exactly 'EXISTS' or 'NOTFOUND'. " +
                    "Aborting to avoid unintended mutation.");
            }

            bool exists = probeToken == "EXISTS";
            bool notFound = probeToken == "NOTFOUND";

            if (exists && !request.AdoptExisting)
            {
                var sshPath = $"{store.Host}:{remotePath}";
                return ProjectCreationResult.Exists(projectId, sshPath);
            }

            if (notFound)
            {
                // Execute mkdir via RunCommand using the verified remoteIsWindows classification;
                // this is the same classification used for existence and init commands above.
                var mkdirResult = _sshService.RunCommand(store.Host, port, store.User, password, mkdirCmd);
                if (!mkdirResult.Success)
                {
                    return ProjectCreationResult.Fail($"Failed to create remote directory: {mkdirResult.Error}");
                }

                // git init + initial commit; require success before registration.
                var initResult = _sshService.RunCommand(store.Host, port, store.User, password, gitInitCmd);
                if (!initResult.Success)
                {
                    return ProjectCreationResult.Fail(
                        $"Remote git init failed on '{store.Host}': {initResult.Error}. " +
                        "The directory was created but no git repository was initialized. " +
                        "Registration aborted to preserve consistency.");
                }
            }

            // Register with store reference (SSH projects get store-relative registration)
            var userPrefix = string.IsNullOrWhiteSpace(store.User) ? "" : $"{store.User}@";
            var sshTarget = $"{userPrefix}{store.Host}:{remotePath}";
            // Same allow-overwrite rationale as the local-project branch above:
            // the SSH adoption flow may re-register an existing project id.
            var regResult = _registrationService.RegisterProject(new ProjectRegistrationRequest
            {
                ProjectId = projectId,
                DisplayName = request.DisplayName,
                Summary = request.Summary,
                LifecycleState = string.IsNullOrWhiteSpace(request.LifecycleState) ? "active" : request.LifecycleState.Trim(),
                SshTarget = sshTarget,
                GitHubUrl = request.GitHubUrl,
                AdoUrl = request.AdoUrl,
                Group = request.Group,
                AllowOverwrite = true
            });

            if (!regResult.Success)
            {
                return ProjectCreationResult.Fail($"Remote folder created but registration failed: {regResult.Message}");
            }

            return ProjectCreationResult.Ok(projectId, sshTarget, "Project created on SSH remote.");
        }

        private static void RunGitInit(string path)
        {
            try
            {
                var psi = new ProcessStartInfo("git", "init")
                {
                    WorkingDirectory = path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                process?.WaitForExit(15_000);
            }
            catch
            {
                // git init failure is not fatal
            }
        }

        /// <summary>
        /// Returns true only for paths that are unambiguously absolute on the remote.
        /// Relative paths (e.g. "repos") are rejected because they cannot be resolved
        /// to a correct launch target without a live SSH round-trip.
        /// </summary>
        private static bool IsAbsoluteRemotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path[0] == '/') return true;                                     // POSIX absolute
            if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\') return true; // Windows UNC
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/')) return true;                // Windows drive
            return false;
        }

        /// <summary>
        /// Returns true when the uname -s output identifies a known POSIX kernel.
        /// Keeps the check explicit: unknown output is rejected, not assumed POSIX.
        /// </summary>
        private static bool IsKnownPosixOs(string unameOutput)
        {
            if (string.IsNullOrWhiteSpace(unameOutput)) return false;
            return unameOutput.StartsWith("Linux", StringComparison.OrdinalIgnoreCase)
                || unameOutput.StartsWith("Darwin", StringComparison.OrdinalIgnoreCase)
                || unameOutput.StartsWith("FreeBSD", StringComparison.OrdinalIgnoreCase)
                || unameOutput.StartsWith("OpenBSD", StringComparison.OrdinalIgnoreCase)
                || unameOutput.StartsWith("NetBSD", StringComparison.OrdinalIgnoreCase)
                || unameOutput.StartsWith("SunOS", StringComparison.OrdinalIgnoreCase)
                || unameOutput.StartsWith("CYGWIN", StringComparison.OrdinalIgnoreCase)
                || unameOutput.StartsWith("MINGW", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildProjectId(string displayName)
        {
            var builder = new StringBuilder();
            foreach (var c in displayName.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                }
                else if (builder.Length == 0 || builder[builder.Length - 1] != '-')
                {
                    builder.Append('-');
                }
            }

            var id = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(id) ? "project" : id;
        }
    }
}
