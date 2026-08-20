using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Infrastructure.Launch
{
    public sealed class WindowsLaunchService : ILaunchService
    {
        private readonly ToolSettings _settings;
        private readonly Action<ProcessStartInfo> _processStarter;

        public WindowsLaunchService()
            : this(new ToolSettings(), null)
        {
        }

        public WindowsLaunchService(ToolSettings settings)
            : this(settings, null)
        {
        }

        // Test seam: a custom starter lets tests verify the validated start-info
        // without actually invoking ShellExecute. Default starts the real process.
        public WindowsLaunchService(ToolSettings settings, Action<ProcessStartInfo> processStarter)
        {
            _settings = settings ?? new ToolSettings();
            _processStarter = processStarter ?? (info => Process.Start(info));
        }

        public LaunchResult Launch(ProjectDefinition project, LaunchTargetKind targetKind)
        {
            if (project == null)
            {
                return LaunchResult.Unconfigured("No project selected");
            }

            try
            {
                if (targetKind == LaunchTargetKind.Code)
                {
                    return LaunchLocalCode(project);
                }

                if (targetKind == LaunchTargetKind.CodeAdmin)
                {
                    return LaunchLocalCodeAsAdmin(project);
                }

                if (targetKind == LaunchTargetKind.RemoteCode)
                {
                    return LaunchRemoteCode(project);
                }

                if (targetKind == LaunchTargetKind.GitHub)
                {
                    return OpenPathOrUrl(project.Launch.GitHub, ResolveProjectRoot(project));
                }

                if (targetKind == LaunchTargetKind.Ado)
                {
                    return OpenPathOrUrl(project.Launch.Ado, ResolveProjectRoot(project));
                }

                if (targetKind == LaunchTargetKind.PrimaryDoc)
                {
                    if (project.Docs.Count == 0)
                    {
                        return LaunchResult.Unconfigured("No key doc is configured");
                    }

                    return OpenPathOrUrl(project.Docs[0].Url, ResolveProjectRoot(project));
                }

                if (targetKind == LaunchTargetKind.Plan)
                {
                    var planPath = ResolveRoadmapPath(project);
                    if (!string.IsNullOrWhiteSpace(planPath) && File.Exists(planPath))
                    {
                        return OpenPathOrUrl(planPath, ResolveProjectRoot(project));
                    }

                    if (project.Planning != null && !string.IsNullOrWhiteSpace(project.Planning.SourceRef))
                    {
                        return OpenPathOrUrl(project.Planning.SourceRef, ResolveProjectRoot(project));
                    }

                    return LaunchResult.Unconfigured("No planning file is configured");
                }

                return LaunchResult.Rejected("launch/rejected/unsupported", "Unsupported launch target");
            }
            catch (Win32Exception)
            {
                return LaunchResult.Failed("Unable to find the target application or handler");
            }
            catch (InvalidOperationException ex)
            {
                return LaunchResult.Failed(ex.Message);
            }
            catch (IOException ex)
            {
                return LaunchResult.Failed(ex.Message);
            }
        }

        private LaunchResult LaunchLocalCode(ProjectDefinition project)
        {
            var path = !string.IsNullOrWhiteSpace(project.Launch.VsCodeLocal)
                ? project.Launch.VsCodeLocal
                : project.Locations.LocalPath;

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return StartProcess(
                    _settings.VsCodeCommand,
                    "--new-window \"" + EscapeQuotes(path) + "\"",
                    path,
                    true,
                    "Opened code workspace");
            }

            if (project.Locations != null && !string.IsNullOrWhiteSpace(project.Locations.SshTarget))
            {
                return LaunchRemoteCode(project);
            }

            if (!string.IsNullOrWhiteSpace(project.ProjectRootPath) && Directory.Exists(project.ProjectRootPath))
            {
                return StartProcess(
                    _settings.VsCodeCommand,
                    "--new-window \"" + EscapeQuotes(project.ProjectRootPath) + "\"",
                    project.ProjectRootPath,
                    true,
                    "Opened project workspace");
            }

            return LaunchResult.Unconfigured("Code path is not available");
        }

        private LaunchResult LaunchLocalCodeAsAdmin(ProjectDefinition project)
        {
            var path = !string.IsNullOrWhiteSpace(project.Launch.VsCodeLocal)
                ? project.Launch.VsCodeLocal
                : project.Locations.LocalPath;

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                if (!string.IsNullOrWhiteSpace(project.ProjectRootPath) && Directory.Exists(project.ProjectRootPath))
                {
                    path = project.ProjectRootPath;
                }
                else
                {
                    return LaunchResult.Unconfigured("Code path is not available");
                }
            }

            return StartProcessAsAdmin(
                _settings.VsCodeCommand,
                "--new-window \"" + EscapeQuotes(path) + "\"",
                path,
                "Opened code workspace as Administrator");
        }

        private LaunchResult OpenPathOrUrl(string value, string projectRootPath)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return LaunchResult.Unconfigured("Launch target is not configured");
            }

            // URL handling — per ADR-004 §1, allow only https (and http when
            // the user has explicitly opted in). Everything else is blocked.
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme.ToLowerInvariant();
                bool allowed = scheme == "https" ||
                    (_settings.AllowHttpLinks && scheme == "http");

                if (!allowed)
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/scheme",
                        $"Blocked an unsupported or insecure link target (scheme '{scheme}').");
                }

                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/embedded-credentials",
                        "Blocked URL with embedded credentials.");
                }

                // Reject obvious nested-scheme smuggling like
                // "https://example.com/javascript:alert(1)" where the entire
                // value looks like a URL but the path contains another scheme.
                if (ContainsEmbeddedScheme(uri.AbsoluteUri))
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/embedded-url",
                        "Blocked URL with an embedded secondary scheme.");
                }

                var urlStart = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
                _processStarter(urlStart);
                return LaunchResult.Ok("Opened target");
            }

            // Filesystem path handling — per ADR-004 §2 / §3.
            string resolved;
            var pathCheck = ValidateLocalPath(value, projectRootPath, out resolved);
            if (pathCheck != null)
            {
                return pathCheck;
            }

            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                return LaunchResult.Failed("Target path does not exist");
            }

            var startInfo = new ProcessStartInfo(resolved) { UseShellExecute = true };
            _processStarter(startInfo);
            return LaunchResult.Ok("Opened target");
        }

        /// <summary>
        /// Validates a filesystem launch target against ADR-004 §2/§3.
        /// Returns null on success and sets <paramref name="resolved"/> to the
        /// resolved absolute path. Returns a structured Rejected result on
        /// failure.
        /// </summary>
        private LaunchResult ValidateLocalPath(string value, string projectRootPath, out string resolved)
        {
            resolved = string.Empty;

            // Reject UNC, extended-path, and device paths outright.
            if (value.StartsWith(@"\\", StringComparison.Ordinal) ||
                value.StartsWith("//", StringComparison.Ordinal))
            {
                return LaunchResult.Rejected(
                    "launch/rejected/unc",
                    "Blocked a UNC or extended path target.");
            }

            if (ContainsTraversal(value))
            {
                return LaunchResult.Rejected(
                    "launch/rejected/traversal",
                    "Blocked a path containing parent-directory traversal.");
            }

            // A local filesystem target can only be safely confined when the
            // project has a concrete local root to bound it to. For root-less
            // projects (e.g. SSH-only), refuse local paths outright instead of
            // resolving them against the process working directory and skipping
            // the under-root check below — otherwise a relative or rooted
            // SourceRef/doc could open a file outside any project boundary.
            if (string.IsNullOrWhiteSpace(projectRootPath))
            {
                return LaunchResult.Rejected(
                    "launch/rejected/no-root",
                    "Blocked a local file target: the project has no local root to confine it to.");
            }

            try
            {
                resolved = Path.IsPathRooted(value)
                    ? Path.GetFullPath(value)
                    : Path.GetFullPath(Path.Combine(projectRootPath, value));
            }
            catch (Exception)
            {
                return LaunchResult.Rejected("launch/rejected/path", "Blocked an invalid path target.");
            }

            if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return LaunchResult.Rejected("launch/rejected/unc", "Blocked a UNC or extended path target.");
            }

            if (!string.IsNullOrWhiteSpace(projectRootPath))
            {
                string root;
                try { root = Path.GetFullPath(projectRootPath); }
                catch { root = projectRootPath; }

                if (!IsUnderRoot(resolved, root))
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/outside-root",
                        "Blocked a path that resolves outside the project root.");
                }
            }

            if (!Directory.Exists(resolved))
            {
                var ext = Path.GetExtension(resolved);
                if (string.IsNullOrEmpty(ext))
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/extension",
                        "Blocked a target with no recognised file extension.");
                }

                if (LaunchAllowlist.BlockedExecutableExtensions.Contains(ext))
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/extension",
                        $"Blocked an executable target ('{ext}'). Only the configured editor may launch executables.");
                }

                if (!LaunchAllowlist.Extensions.Contains(ext))
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/extension",
                        $"Blocked a target with disallowed extension '{ext}'.");
                }
            }

            return null;
        }

        private static bool ContainsTraversal(string value)
        {
            // Simple textual check on raw input — Path.GetFullPath would
            // silently collapse traversal segments, so we look at the value
            // as-written.
            var normalized = value.Replace('/', '\\');
            var parts = normalized.Split('\\');
            foreach (var part in parts)
            {
                if (string.Equals(part, "..", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnderRoot(string fullPath, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return true;
            }

            var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsEmbeddedScheme(string absoluteUri)
        {
            // Look for nested scheme markers (javascript:, data:, vbscript:,
            // file:, ms-…) anywhere after the authority. The Uri parser strips
            // them when valid, but a malicious value may smuggle them in.
            var lower = absoluteUri.ToLowerInvariant();
            string[] suspicious = { "javascript:", "data:", "vbscript:", "file:", "about:" };
            foreach (var marker in suspicious)
            {
                var firstHit = lower.IndexOf(marker, StringComparison.Ordinal);
                if (firstHit > 0 && !lower.StartsWith(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private LaunchResult LaunchRemoteCode(ProjectDefinition project)
        {
            string host;
            string remotePath;
            if (!TryResolveRemoteTarget(project, out host, out remotePath))
            {
                return LaunchResult.Unconfigured("Remote SSH target is not configured");
            }

            if (!IsSafeHost(host) || !IsSafeRemotePath(remotePath))
            {
                return LaunchResult.Rejected(
                    "launch/rejected/remote-target",
                    "Remote SSH target is not valid.");
            }

            return StartProcess(
                _settings.VsCodeCommand,
                "--new-window --folder-uri \"" + BuildRemoteFolderUri(host, remotePath) + "\"",
                ResolveProjectRoot(project),
                true,
                "Opened remote SSH workspace");
        }

        private static bool TryResolveRemoteTarget(ProjectDefinition project, out string host, out string remotePath)
        {
            host = string.Empty;
            remotePath = string.Empty;

            if (project != null &&
                project.Launch != null &&
                !string.IsNullOrWhiteSpace(project.Launch.VsCodeSsh))
            {
                var configured = project.Launch.VsCodeSsh.Trim();
                if (TryParseConfiguredRemote(configured, out host, out remotePath))
                {
                    return true;
                }
            }

            if (project != null && project.Locations != null)
            {
                if (TryParseSshTarget(project.Locations.SshTarget, out host, out remotePath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseSshTarget(string sshTarget, out string host, out string path)
        {
            host = string.Empty;
            path = string.Empty;

            if (string.IsNullOrWhiteSpace(sshTarget))
            {
                return false;
            }

            var separator = sshTarget.IndexOf(':');
            if (separator <= 0 || separator >= sshTarget.Length - 1)
            {
                return false;
            }

            host = sshTarget.Substring(0, separator).Trim();
            path = sshTarget.Substring(separator + 1).Trim();
            if (!path.StartsWith("/", StringComparison.OrdinalIgnoreCase) &&
                !(path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
            {
                path = "/" + path;
            }

            return IsSafeHost(host) && IsSafeRemotePath(path);
        }

        private static bool TryParseConfiguredRemote(string value, out string host, out string remotePath)
        {
            host = string.Empty;
            remotePath = string.Empty;
            var configured = value ?? string.Empty;

            if (configured.StartsWith("vscode-remote://", StringComparison.OrdinalIgnoreCase))
            {
                configured = configured.Substring("vscode-remote://".Length);
            }

            const string prefix = "ssh-remote+";
            if (!configured.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var remainder = configured.Substring(prefix.Length);
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex < 0)
            {
                return false;
            }

            host = remainder.Substring(0, slashIndex);
            remotePath = remainder.Substring(slashIndex + 1)
                .Replace("%3A", ":")
                .Replace("%3a", ":")
                .Replace("%20", " ");

            if (remotePath.StartsWith("/", StringComparison.OrdinalIgnoreCase) &&
                remotePath.Length >= 3 &&
                char.IsLetter(remotePath[1]) &&
                remotePath[2] == ':')
            {
                remotePath = remotePath.Substring(1);
            }

            if (!remotePath.Contains(":") && !remotePath.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                remotePath = "/" + remotePath;
            }

            remotePath = remotePath.Replace("/", "\\");
            if (remotePath.StartsWith("\\", StringComparison.OrdinalIgnoreCase) &&
                !(remotePath.Length >= 3 && char.IsLetter(remotePath[1]) && remotePath[2] == ':'))
            {
                remotePath = remotePath.Replace("\\", "/");
            }

            return IsSafeHost(host) && IsSafeRemotePath(remotePath);
        }

        private static bool IsSafeHost(string host)
        {
            return !string.IsNullOrWhiteSpace(host) &&
                   Regex.IsMatch(host, @"^[A-Za-z0-9._@-]+$");
        }

        private static bool IsSafeRemotePath(string remotePath)
        {
            return !string.IsNullOrWhiteSpace(remotePath) &&
                   remotePath.IndexOfAny(new[] { '\r', '\n', '"' }) < 0;
        }

        private static string ResolveProjectRoot(ProjectDefinition project)
        {
            if (project == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(project.ProjectRootPath))
            {
                return project.ProjectRootPath;
            }

            if (project.Locations != null && !string.IsNullOrWhiteSpace(project.Locations.LocalPath))
            {
                return project.Locations.LocalPath;
            }

            return string.Empty;
        }

        private static string ResolveRoadmapPath(ProjectDefinition project)
        {
            var projectRoot = ResolveProjectRoot(project);
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return string.Empty;
            }

            var githubRoadmap = Path.Combine(projectRoot, ".github", "roadmap.yaml");
            if (File.Exists(githubRoadmap))
            {
                return githubRoadmap;
            }

            var defaultRoadmap = Path.Combine(projectRoot, "resources", "roadmap.yaml");
            if (File.Exists(defaultRoadmap))
            {
                return defaultRoadmap;
            }

            if (project != null &&
                project.Planning != null &&
                !string.IsNullOrWhiteSpace(project.Planning.SourceRef))
            {
                var sourceRef = project.Planning.SourceRef;
                if (!Path.IsPathRooted(sourceRef))
                {
                    sourceRef = Path.GetFullPath(Path.Combine(projectRoot, sourceRef));
                }

                return sourceRef;
            }

            return defaultRoadmap;
        }

        private LaunchResult StartProcess(string fileName, string arguments, string workingDirectory, bool useShellExecute, string successMessage)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return LaunchResult.Unconfigured("The configured tool command is missing");
            }

            var normalized = fileName.Trim().Trim('"');
            var startInfo = new ProcessStartInfo();

            if (!useShellExecute && RequiresCommandWrapper(normalized))
            {
                // cmd.exe performs %VAR% environment-variable expansion across
                // its entire command line. A '%' in the tool path or arguments
                // could expand to unintended content, so refuse to build the
                // wrapper when one is present — valid Windows paths never need a
                // literal '%'. Fail visibly rather than launch something the
                // user did not intend.
                if (normalized.IndexOf('%') >= 0 ||
                    (arguments != null && arguments.IndexOf('%') >= 0))
                {
                    return LaunchResult.Rejected(
                        "launch/rejected/unsafe-arg",
                        "Blocked a launch whose command or arguments contain '%', which cmd.exe would expand.");
                }

                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c \"" + normalized + " " + arguments + "\"";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
            }
            else
            {
                startInfo.FileName = normalized;
                startInfo.Arguments = arguments ?? string.Empty;
                startInfo.UseShellExecute = useShellExecute;
                startInfo.CreateNoWindow = !useShellExecute;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            _processStarter(startInfo);
            return LaunchResult.Ok(successMessage);
        }

        private LaunchResult StartProcessAsAdmin(string fileName, string arguments, string workingDirectory, string successMessage)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return LaunchResult.Unconfigured("The configured tool command is missing");
            }

            var normalized = fileName.Trim().Trim('"');
            var startInfo = new ProcessStartInfo
            {
                FileName = normalized,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            try
            {
                _processStarter(startInfo);
                return LaunchResult.Ok(successMessage);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — user declined the UAC prompt
                return LaunchResult.Failed("Elevation was cancelled by the user");
            }
        }

        private static bool RequiresCommandWrapper(string fileName)
        {
            return fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        }

        private static string EscapeQuotes(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private static string BuildRemoteFolderUri(string host, string remotePath)
        {
            var normalizedPath = (remotePath ?? string.Empty).Replace("\\", "/");
            if (!normalizedPath.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = "/" + normalizedPath;
            }

            normalizedPath = normalizedPath.Replace(":", "%3A").Replace(" ", "%20");
            return "vscode-remote://ssh-remote+" + host + normalizedPath;
        }
    }
}
