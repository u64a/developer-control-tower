using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ControlTower.Core.Models
{
    /// <summary>How a launch environment opens a project.</summary>
    public enum LaunchEnvironmentKind
    {
        /// <summary>A VS Code-compatible editor: <c>--new-window &lt;path&gt;</c>, Remote-SSH via <c>--folder-uri</c>.</summary>
        Editor,

        /// <summary>A command run in PowerShell inside Windows Terminal, in the project folder (or over SSH).</summary>
        Terminal
    }

    /// <summary>
    /// A named way of opening a project (VS Code, a CLI agent, a custom
    /// script). Definitions come from built-ins merged with the user's
    /// settings; projects reference one by <see cref="Id"/>.
    /// </summary>
    public sealed class LaunchEnvironment
    {
        public LaunchEnvironment(
            string id,
            string displayName,
            LaunchEnvironmentKind kind,
            string command,
            string arguments = null,
            string remoteCommand = null,
            string iconPath = null,
            bool isBuiltIn = false)
        {
            Id = id ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
            Kind = kind;
            Command = command ?? string.Empty;
            Arguments = arguments ?? string.Empty;
            RemoteCommand = remoteCommand ?? string.Empty;
            IconPath = iconPath ?? string.Empty;
            IsBuiltIn = isBuiltIn;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public LaunchEnvironmentKind Kind { get; }

        /// <summary>Executable, script path, or bare command name resolved on PATH.</summary>
        public string Command { get; }

        /// <summary>Extra arguments appended after the command.</summary>
        public string Arguments { get; }

        /// <summary>
        /// Command run on the remote host for SSH-only projects (terminal kind).
        /// Empty means "use <see cref="Command"/> when it is a bare name".
        /// </summary>
        public string RemoteCommand { get; }

        /// <summary>Optional icon source (.ico, .png, .exe, .dll) overriding the command's own icon.</summary>
        public string IconPath { get; }

        public bool IsBuiltIn { get; }
    }

    /// <summary>
    /// The set of available launch environments plus the global default.
    /// Resolution never fails: unknown/empty ids fall back to the default,
    /// and the catalog always contains <see cref="VsCodeId"/>.
    /// </summary>
    public sealed class LaunchEnvironmentCatalog
    {
        public const string VsCodeId = "vscode";
        public const string CopilotCliId = "copilot-cli";
        public const string ClaudeCodeId = "claude-code";

        private static readonly Regex IdPattern = new Regex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

        private readonly List<LaunchEnvironment> _environments;

        public LaunchEnvironmentCatalog(IEnumerable<LaunchEnvironment> environments, string defaultId)
        {
            _environments = new List<LaunchEnvironment>();
            foreach (var environment in environments ?? Enumerable.Empty<LaunchEnvironment>())
            {
                if (environment == null || !IsValidId(environment.Id) || Find(environment.Id) != null)
                {
                    continue;
                }

                _environments.Add(environment);
            }

            if (Find(VsCodeId) == null)
            {
                _environments.Insert(0, CreateVsCode("code"));
            }

            var normalizedDefault = Normalize(defaultId);
            DefaultId = Find(normalizedDefault) != null ? normalizedDefault : VsCodeId;
        }

        public IReadOnlyList<LaunchEnvironment> Environments => _environments;

        public string DefaultId { get; }

        public LaunchEnvironment Default => Find(DefaultId);

        public static LaunchEnvironmentCatalog CreateDefault(string vsCodeCommand = "code")
        {
            return new LaunchEnvironmentCatalog(CreateBuiltIns(vsCodeCommand), VsCodeId);
        }

        public static IReadOnlyList<LaunchEnvironment> CreateBuiltIns(string vsCodeCommand)
        {
            return new[]
            {
                CreateVsCode(vsCodeCommand),
                new LaunchEnvironment(CopilotCliId, "GitHub Copilot CLI", LaunchEnvironmentKind.Terminal, "copilot", isBuiltIn: true),
                new LaunchEnvironment(ClaudeCodeId, "Claude Code", LaunchEnvironmentKind.Terminal, "claude", isBuiltIn: true)
            };
        }

        public static bool IsValidId(string id)
        {
            return !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);
        }

        /// <summary>Trims and lower-cases an id; returns empty for null/whitespace.</summary>
        public static string Normalize(string id)
        {
            return string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim().ToLowerInvariant();
        }

        public LaunchEnvironment Find(string id)
        {
            var normalized = Normalize(id);
            if (normalized.Length == 0)
            {
                return null;
            }

            return _environments.FirstOrDefault(e => string.Equals(e.Id, normalized, StringComparison.Ordinal));
        }

        /// <summary>Effective environment for a project's configured id (empty/unknown → default).</summary>
        public LaunchEnvironment Resolve(string projectEnvironmentId)
        {
            return Find(projectEnvironmentId) ?? Default;
        }

        /// <summary>True when the project names an environment that does not exist in this catalog.</summary>
        public bool IsUnknown(string projectEnvironmentId)
        {
            return !string.IsNullOrWhiteSpace(projectEnvironmentId) && Find(projectEnvironmentId) == null;
        }

        private static LaunchEnvironment CreateVsCode(string command)
        {
            return new LaunchEnvironment(
                VsCodeId,
                "VS Code",
                LaunchEnvironmentKind.Editor,
                string.IsNullOrWhiteSpace(command) ? "code" : command,
                isBuiltIn: true);
        }
    }
}
