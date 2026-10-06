using System;
using System.Collections.Generic;
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
                    var environment = ResolveEnvironment(project);
                    if (environment.Kind == LaunchEnvironmentKind.Terminal)
                    {
                        return LaunchTerminal(project, environment);
                    }

                    // The task file has to exist before the folder opens, so
                    // prepare it ahead of starting the editor.
                    var integrated = PrepareIntegratedCopilot(project, out var integratedNote);
                    var editorResult = LaunchLocalCode(project, environment);
                    if (!editorResult.Success)
                    {
                        return editorResult;
                    }

                    return integrated
                        ? LaunchResult.Ok(editorResult.Message + integratedNote)
                        : StartCompanionCopilot(project, editorResult);
                }

                if (targetKind == LaunchTargetKind.CodeAdmin)
                {
                    var integratedAdmin = PrepareIntegratedCopilot(project, out var adminNote);
                    var adminResult = LaunchLocalCodeAsAdmin(project);
                    if (!adminResult.Success)
                    {
                        return adminResult;
                    }

                    return integratedAdmin
                        ? LaunchResult.Ok(adminResult.Message + adminNote)
                        : StartCompanionCopilot(project, adminResult);
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

        private LaunchEnvironment ResolveEnvironment(ProjectDefinition project)
        {
            var catalog = _settings.LaunchEnvironments ?? LaunchEnvironmentCatalog.CreateDefault(_settings.VsCodeCommand);
            return catalog.Resolve(project.Launch?.Environment);
        }

        /// <summary>The environment Copilot CLI autostart runs as, honouring a user override of the built-in.</summary>
        private LaunchEnvironment CopilotEnvironment()
        {
            var catalog = _settings.LaunchEnvironments ?? LaunchEnvironmentCatalog.CreateDefault(_settings.VsCodeCommand);
            return catalog.Find(LaunchEnvironmentCatalog.CopilotCliId) ?? new LaunchEnvironment(
                LaunchEnvironmentCatalog.CopilotCliId,
                "GitHub Copilot CLI",
                LaunchEnvironmentKind.Terminal,
                "copilot",
                isBuiltIn: true);
        }

        private static bool IsCopilotEnvironment(LaunchEnvironment environment)
        {
            return environment != null && environment.IsCopilotCli;
        }

        /// <summary>
        /// Autostart tokens for a Copilot CLI environment, or null when the
        /// project has not opted in. Returns false with a rejection when the
        /// configured options cannot be turned into a command line.
        /// </summary>
        private static bool TryResolveAutostart(ProjectDefinition project, LaunchEnvironment environment, out CopilotLaunchPlan plan, out LaunchResult rejection)
        {
            plan = null;
            rejection = null;

            var autostart = project.Launch?.CopilotAutostart;
            if (autostart == null || !autostart.Enabled || !IsCopilotEnvironment(environment))
            {
                return true;
            }

            if (!autostart.TryBuildPlan(out var built, out var error))
            {
                rejection = LaunchResult.Rejected("launch/rejected/copilot-autostart", error);
                return false;
            }

            plan = built;
            return true;
        }

        /// <summary>
        /// VS Code has no command line that opens its integrated terminal and
        /// runs a command, so an editor launch with Copilot autostart opens a
        /// terminal beside the editor in the same folder. A failure here never
        /// turns the successful editor launch into a failed one.
        /// </summary>
        /// <summary>
        /// Writes the VS Code task that runs Copilot CLI in the integrated
        /// terminal, when the project asks for it. Returns true when the
        /// integrated route owns this launch, so no terminal opens beside the
        /// editor. When the project does not ask for it, any task this tool
        /// wrote earlier is removed so a cleared checkbox stops taking effect.
        /// </summary>
        private bool PrepareIntegratedCopilot(ProjectDefinition project, out string note)
        {
            note = string.Empty;

            var autostart = project.Launch?.CopilotAutostart;
            var path = ResolveLocalCodePath(project);
            if (path == null)
            {
                return false;
            }

            if (autostart == null || !autostart.Enabled || !autostart.UseIntegratedTerminal)
            {
                VsCodeTaskFile.Remove(path);
                return false;
            }

            if (!autostart.TryBuildPlan(out var plan, out var error))
            {
                note = ". Copilot CLI not started: " + error;
                return true;
            }

            var environment = CopilotEnvironment();
            var written = VsCodeTaskFile.Write(path, environment.Command, plan, true);
            note = written.Success
                ? " and queued " + autostart.Describe(environment.Command) + " in its terminal"
                : ". Copilot CLI not started: " + written.Error;
            return true;
        }

        private LaunchResult StartCompanionCopilot(ProjectDefinition project, LaunchResult editorResult)
        {
            var autostart = project.Launch?.CopilotAutostart;
            if (autostart == null || !autostart.Enabled)
            {
                return editorResult;
            }

            // Copilot CLI runs on this machine, so it can only follow a
            // workspace that exists locally (an SSH project opens remotely).
            var path = ResolveLocalCodePath(project);
            if (path == null)
            {
                return editorResult;
            }

            if (!autostart.TryBuildPlan(out var plan, out var error))
            {
                return LaunchResult.Ok(editorResult.Message + ". Copilot CLI not started: " + error);
            }

            var environment = CopilotEnvironment();
            try
            {
                StartTerminal(environment, project, BuildLocalTerminalScript(path, environment, plan), path, string.Empty);
            }
            catch (Win32Exception)
            {
                return LaunchResult.Ok(editorResult.Message + ". Copilot CLI could not be started: '" + environment.Command + "' was not found.");
            }

            return LaunchResult.Ok(editorResult.Message + " and started " + autostart.Describe(environment.Command));
        }

        /// <summary>The project's local workspace folder, or null when it has none on this machine.</summary>
        private static string ResolveLocalCodePath(ProjectDefinition project)
        {
            var path = !string.IsNullOrWhiteSpace(project.Launch?.VsCodeLocal)
                ? project.Launch.VsCodeLocal
                : project.Locations?.LocalPath;

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path;
            }

            return !string.IsNullOrWhiteSpace(project.ProjectRootPath) && Directory.Exists(project.ProjectRootPath)
                ? project.ProjectRootPath
                : null;
        }

        private static string BuildLocalTerminalScript(string path, LaunchEnvironment environment, CopilotLaunchPlan plan)
        {
            var session = "& " + PsQuote(environment.Command) +
                          (string.IsNullOrWhiteSpace(environment.Arguments) ? string.Empty : " " + environment.Arguments.Trim());

            var script = "Set-Location -LiteralPath " + PsQuote(path) + "; ";
            if (plan != null && plan.HasUpdateStep)
            {
                script += "& " + PsQuote(environment.Command) + FormatExtraArguments(plan.UpdateArguments) + "; ";
            }

            return script + session + FormatExtraArguments(plan?.SessionArguments);
        }

        private static string FormatExtraArguments(IReadOnlyList<string> extraArguments)
        {
            if (extraArguments == null || extraArguments.Count == 0)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var token in extraArguments)
            {
                builder.Append(' ').Append(PsQuote(token));
            }

            return builder.ToString();
        }

        /// <summary>
        /// The built-in VS Code environment always uses <c>tooling.vscode_command</c>
        /// so the editor path, admin launch and Remote-SSH stay consistent.
        /// </summary>
        private string EditorCommand(LaunchEnvironment environment)
        {
            return environment == null || environment.Id == LaunchEnvironmentCatalog.VsCodeId
                ? _settings.VsCodeCommand
                : environment.Command;
        }

        private static string EditorArguments(LaunchEnvironment environment, string target)
        {
            var extra = environment == null || string.IsNullOrWhiteSpace(environment.Arguments)
                ? string.Empty
                : environment.Arguments.Trim() + " ";
            return "--new-window " + extra + target;
        }

        private LaunchResult LaunchLocalCode(ProjectDefinition project, LaunchEnvironment environment = null)
        {
            var path = !string.IsNullOrWhiteSpace(project.Launch.VsCodeLocal)
                ? project.Launch.VsCodeLocal
                : project.Locations.LocalPath;

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return StartProcess(
                    EditorCommand(environment),
                    EditorArguments(environment, "\"" + EscapeQuotes(path) + "\""),
                    path,
                    true,
                    "Opened code workspace");
            }

            if (project.Locations != null && !string.IsNullOrWhiteSpace(project.Locations.SshTarget))
            {
                return LaunchRemoteCode(project, environment);
            }

            if (!string.IsNullOrWhiteSpace(project.ProjectRootPath) && Directory.Exists(project.ProjectRootPath))
            {
                return StartProcess(
                    EditorCommand(environment),
                    EditorArguments(environment, "\"" + EscapeQuotes(project.ProjectRootPath) + "\""),
                    project.ProjectRootPath,
                    true,
                    "Opened project workspace");
            }

            return LaunchResult.Unconfigured("Code path is not available");
        }

        /// <summary>
        /// Opens a terminal launch environment: PowerShell (inside Windows
        /// Terminal when available) in the project folder running the
        /// environment's command, or — for SSH-only projects — ssh to the
        /// host, cd to the remote path and run the remote command.
        /// </summary>
        private LaunchResult LaunchTerminal(ProjectDefinition project, LaunchEnvironment environment)
        {
            var path = !string.IsNullOrWhiteSpace(project.Launch.VsCodeLocal)
                ? project.Launch.VsCodeLocal
                : project.Locations?.LocalPath;

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                path = null;
                var hasSsh = project.Locations != null && !string.IsNullOrWhiteSpace(project.Locations.SshTarget);
                if (hasSsh || !string.IsNullOrWhiteSpace(project.Launch.VsCodeSsh))
                {
                    return LaunchRemoteTerminal(project, environment);
                }

                if (!string.IsNullOrWhiteSpace(project.ProjectRootPath) && Directory.Exists(project.ProjectRootPath))
                {
                    path = project.ProjectRootPath;
                }
            }

            if (path == null)
            {
                return LaunchResult.Unconfigured("Code path is not available");
            }

            if (!TryResolveAutostart(project, environment, out var autostartPlan, out var rejection))
            {
                return rejection;
            }

            var script = BuildLocalTerminalScript(path, environment, autostartPlan);
            return StartTerminal(environment, project, script, path, "Opened " + environment.DisplayName);
        }

        private LaunchResult LaunchRemoteTerminal(ProjectDefinition project, LaunchEnvironment environment)
        {
            if (!TryResolveRemoteTarget(project, out var host, out var remotePath))
            {
                return LaunchResult.Unconfigured("Remote SSH target is not configured");
            }

            var windowsPath = remotePath.Length >= 2 && char.IsLetter(remotePath[0]) && remotePath[1] == ':';
            if (!IsSafeHost(host) || !IsShellNeutralRemotePath(remotePath))
            {
                return LaunchResult.Rejected(
                    "launch/rejected/remote-target",
                    "Remote SSH target is not valid.");
            }

            string remoteLine;
            if (!TryResolveAutostart(project, environment, out var autostartPlan, out var autostartRejection))
            {
                return autostartRejection;
            }

            if (!string.IsNullOrWhiteSpace(environment.RemoteCommand))
            {
                var remoteInvocation = BuildRemoteInvocation(environment.RemoteCommand.Trim(), null, autostartPlan, windowsPath);
                remoteLine = windowsPath
                    ? "cd /d " + remotePath + " && " + remoteInvocation
                    : "cd '" + remotePath + "' && " + remoteInvocation;
            }
            else if (!IsBareCommandName(environment.Command))
            {
                return LaunchResult.Unconfigured(
                    $"{environment.DisplayName} has no remote_command for SSH projects");
            }
            else if (windowsPath)
            {
                remoteLine = "powershell.exe -NoLogo -NoProfile -EncodedCommand " +
                             Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                                 BuildWindowsRemoteScript(remotePath, environment, autostartPlan)));
            }
            else
            {
                remoteLine = "cd '" + remotePath + "' && " +
                             BuildRemoteInvocation(environment.Command.Trim(), environment.Arguments, autostartPlan, false);
            }

            var ssh = string.IsNullOrWhiteSpace(_settings.SshCommand) ? "ssh" : _settings.SshCommand.Trim().Trim('"');
            var configArg = string.IsNullOrWhiteSpace(_settings.SshConfigPath)
                ? string.Empty
                : " -F " + PsQuote(_settings.SshConfigPath);
            var script = "& " + PsQuote(ssh) + configArg + " -t " + PsQuote(host) + " " + PsQuote(remoteLine);
            return StartTerminal(environment, project, script, null, "Opened " + environment.DisplayName + " over SSH");
        }

        private LaunchResult StartTerminal(LaunchEnvironment environment, ProjectDefinition project, string script, string workingDirectory, string successMessage)
        {
            var powershell = string.IsNullOrWhiteSpace(_settings.PowerShellCommand)
                ? "powershell.exe"
                : _settings.PowerShellCommand.Trim().Trim('"');
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var powershellArgs = "-NoLogo -NoExit -EncodedCommand " + encoded;

            var startInfo = new ProcessStartInfo { UseShellExecute = true };
            var terminal = (_settings.TerminalCommand ?? string.Empty).Trim().Trim('"');
            if (terminal.Length > 0)
            {
                var title = SanitizeTitle((project.DisplayName ?? project.Id ?? "Project") + " - " + environment.DisplayName);
                startInfo.FileName = terminal;
                startInfo.Arguments = "-w new new-tab --title \"" + title + "\" --suppressApplicationTitle \"" +
                                      powershell + "\" " + powershellArgs;
            }
            else
            {
                startInfo.FileName = powershell;
                startInfo.Arguments = powershellArgs;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            _processStarter(startInfo);
            return LaunchResult.Ok(successMessage);
        }

        /// <summary>PowerShell single-quoted literal: no expansion; embedded quotes doubled.</summary>
        private static string PsQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        /// <summary>
        /// Remote script for Windows SSH hosts. Microsoft Store app execution
        /// aliases (<c>%LOCALAPPDATA%\Microsoft\WindowsApps\*.exe</c>) fail with
        /// "Access is denied" inside an SSH session, and they are often first on
        /// PATH (e.g. GitHub Copilot CLI), so resolve the first real executable.
        /// </summary>
        private static string BuildWindowsRemoteScript(string remotePath, LaunchEnvironment environment, CopilotLaunchPlan plan)
        {
            var name = environment.Command.Trim();
            var args = (string.IsNullOrWhiteSpace(environment.Arguments) ? string.Empty : " " + environment.Arguments.Trim()) +
                       FormatExtraArguments(plan?.SessionArguments);
            var update = plan != null && plan.HasUpdateStep
                ? "& $c.Source" + FormatExtraArguments(plan.UpdateArguments) + "\n"
                : string.Empty;
            return "Set-Location -LiteralPath " + PsQuote(remotePath) + "\n" +
                   "$c = Get-Command -Name " + PsQuote(name) + " -CommandType Application -All -ErrorAction SilentlyContinue |" +
                   " Where-Object { $_.Source -notlike '*\\WindowsApps\\*' } | Select-Object -First 1\n" +
                   "if (-not $c) { Write-Host " + PsQuote("'" + name + "' was not found on this host (Microsoft Store app aliases cannot run over SSH).") +
                   " -ForegroundColor Red; exit 1 }\n" +
                   update +
                   "& $c.Source" + args + "\n" +
                   "exit $LASTEXITCODE";
        }

        /// <summary>
        /// Builds the command a remote shell runs. Tokens are allowlist-validated
        /// (letters, digits, dot, underscore, hyphen), so they carry no meaning
        /// to cmd or a POSIX shell. When an update step is present the two
        /// commands are grouped and separated by a run-regardless operator, so
        /// an update that cannot reach the network still leaves the session
        /// starting while the whole group stays conditional on the preceding cd.
        /// </summary>
        private static string BuildRemoteInvocation(string command, string environmentArguments, CopilotLaunchPlan plan, bool windowsShell)
        {
            var session = command +
                          (string.IsNullOrWhiteSpace(environmentArguments) ? string.Empty : " " + environmentArguments.Trim()) +
                          FormatRemoteArguments(plan?.SessionArguments);

            if (plan == null || !plan.HasUpdateStep)
            {
                return session;
            }

            return "(" + command + FormatRemoteArguments(plan.UpdateArguments) +
                   (windowsShell ? " & " : "; ") + session + ")";
        }

        private static string FormatRemoteArguments(IReadOnlyList<string> tokens)
        {
            return tokens == null || tokens.Count == 0 ? string.Empty : " " + string.Join(" ", tokens);
        }

        private static bool IsBareCommandName(string command)
        {
            return !string.IsNullOrWhiteSpace(command) && Regex.IsMatch(command.Trim(), "^[A-Za-z0-9._-]+$");
        }

        private static string SanitizeTitle(string value)
        {
            var cleaned = Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9 ._()-]", string.Empty).Trim();
            return cleaned.Length == 0 ? "Developer Control Tower" : cleaned;
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

        private LaunchResult LaunchRemoteCode(ProjectDefinition project, LaunchEnvironment environment = null)
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
                EditorCommand(environment),
                EditorArguments(environment, "--folder-uri \"" + BuildRemoteFolderUri(host, remotePath) + "\""),
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

        /// <summary>
        /// The remote default shell (cmd, PowerShell, sh) is unknown, so remote
        /// paths spliced into an SSH command line are allow-listed to characters
        /// that are inert in all of them.
        /// </summary>
        private static bool IsShellNeutralRemotePath(string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return false;
            }

            foreach (var ch in remotePath)
            {
                if (!char.IsLetterOrDigit(ch) && " _.-:/\\+".IndexOf(ch) < 0)
                {
                    return false;
                }
            }

            return true;
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
