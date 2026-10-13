using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ControlTower.Core.Models
{
    /// <summary>Which session flag Copilot CLI starts with.</summary>
    public enum CopilotSessionMode
    {
        /// <summary><c>--resume</c>: open the session picker.</summary>
        Resume,

        /// <summary><c>--name &lt;name&gt;</c>: start a new, named session.</summary>
        Name,

        /// <summary><c>--continue</c>: resume the most recent session without prompting.</summary>
        Continue
    }

    /// <summary>Release channel passed to the <c>copilot update</c> subcommand.</summary>
    public enum CopilotUpdateChannel
    {
        /// <summary><c>copilot update stable</c>.</summary>
        Stable,

        /// <summary><c>copilot update prerelease</c>.</summary>
        Prerelease
    }

    /// <summary>
    /// The commands one Copilot autostart turns into, in the order they run.
    /// The update step is a separate command because <c>update</c> is a
    /// subcommand rather than a flag, and it deliberately never receives the
    /// launch environment's own arguments — those are session flags that the
    /// update subcommand would reject.
    /// </summary>
    public sealed class CopilotLaunchPlan
    {
        public CopilotLaunchPlan(IReadOnlyList<string> updateArguments, IReadOnlyList<string> sessionArguments)
        {
            UpdateArguments = updateArguments ?? Array.Empty<string>();
            SessionArguments = sessionArguments ?? Array.Empty<string>();
        }

        /// <summary>Tokens for <c>copilot update</c>, or empty when the project does not check for updates.</summary>
        public IReadOnlyList<string> UpdateArguments { get; }

        /// <summary>Tokens for the session command itself.</summary>
        public IReadOnlyList<string> SessionArguments { get; }

        public bool HasUpdateStep
        {
            get { return UpdateArguments.Count > 0; }
        }
    }

    /// <summary>
    /// Per-project Copilot CLI autostart options. Every value defaults to
    /// off, so a project that has never been configured launches exactly as
    /// it did before.
    /// </summary>
    public sealed class CopilotAutostart
    {
        /// <summary>
        /// Session and agent names end up as tokens on a command line that may
        /// also carry <c>--yolo</c>, so they are restricted to characters that
        /// carry no meaning to a shell.
        /// </summary>
        private static readonly Regex NamePattern = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
            RegexOptions.CultureInvariant);

        /// <summary>Starts Copilot CLI when the project is launched.</summary>
        public bool Enabled { get; set; }

        public CopilotSessionMode SessionMode { get; set; } = CopilotSessionMode.Resume;

        /// <summary>New session name; used only when <see cref="SessionMode"/> is <see cref="CopilotSessionMode.Name"/>.</summary>
        public string SessionName { get; set; } = string.Empty;

        /// <summary>Custom agent for <c>--agent</c>. Empty means no agent.</summary>
        public string AgentName { get; set; } = string.Empty;

        /// <summary>Adds <c>--yolo</c> (allow all tools, paths and URLs).</summary>
        public bool Yolo { get; set; }

        /// <summary>
        /// Runs <c>copilot update</c> immediately before the session starts.
        /// <c>update</c> is a subcommand rather than a flag, so it runs as its
        /// own command chained ahead of the session. On standalone installs a
        /// downloaded version applies at the next launch, which is the chained
        /// session command.
        /// </summary>
        public bool CheckForUpdates { get; set; }

        /// <summary>Channel passed to <c>copilot update</c>; only meaningful when <see cref="CheckForUpdates"/> is set.</summary>
        public CopilotUpdateChannel UpdateChannel { get; set; } = CopilotUpdateChannel.Stable;

        /// <summary>
        /// Runs Copilot CLI inside VS Code's integrated terminal instead of a
        /// terminal beside it. VS Code has no command line that runs a command
        /// in its terminal, so this works by writing a <c>.vscode/tasks.json</c>
        /// task that VS Code runs when it opens the folder. Only meaningful for
        /// editor environments; a terminal environment already is the session.
        /// </summary>
        public bool UseIntegratedTerminal { get; set; }

        /// <summary>True when nothing has been configured and the block can be omitted from project.yml.</summary>
        public bool IsDefault
        {
            get
            {
                return !Enabled &&
                       !Yolo &&
                       !UseIntegratedTerminal &&
                       !CheckForUpdates &&
                       UpdateChannel == CopilotUpdateChannel.Stable &&
                       SessionMode == CopilotSessionMode.Resume &&
                       string.IsNullOrWhiteSpace(SessionName) &&
                       string.IsNullOrWhiteSpace(AgentName);
            }
        }

        public CopilotAutostart Clone()
        {
            return new CopilotAutostart
            {
                Enabled = Enabled,
                SessionMode = SessionMode,
                SessionName = SessionName ?? string.Empty,
                AgentName = AgentName ?? string.Empty,
                Yolo = Yolo,
                CheckForUpdates = CheckForUpdates,
                UpdateChannel = UpdateChannel,
                UseIntegratedTerminal = UseIntegratedTerminal
            };
        }

        public static bool IsValidName(string value)
        {
            return !string.IsNullOrEmpty(value) && NamePattern.IsMatch(value);
        }

        /// <summary>Parses a persisted session mode; anything unrecognised falls back to resume.</summary>
        public static CopilotSessionMode ParseSessionMode(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (string.Equals(text, "name", StringComparison.OrdinalIgnoreCase))
            {
                return CopilotSessionMode.Name;
            }

            return string.Equals(text, "continue", StringComparison.OrdinalIgnoreCase)
                ? CopilotSessionMode.Continue
                : CopilotSessionMode.Resume;
        }

        public static string FormatSessionMode(CopilotSessionMode mode)
        {
            if (mode == CopilotSessionMode.Name)
            {
                return "name";
            }

            return mode == CopilotSessionMode.Continue ? "continue" : "resume";
        }

        /// <summary>Parses a persisted update channel; anything unrecognised falls back to stable.</summary>
        public static CopilotUpdateChannel ParseUpdateChannel(string value)
        {
            return string.Equals((value ?? string.Empty).Trim(), "prerelease", StringComparison.OrdinalIgnoreCase)
                ? CopilotUpdateChannel.Prerelease
                : CopilotUpdateChannel.Stable;
        }

        public static string FormatUpdateChannel(CopilotUpdateChannel channel)
        {
            return channel == CopilotUpdateChannel.Prerelease ? "prerelease" : "stable";
        }

        /// <summary>
        /// Tokens for the <c>copilot update</c> command that runs ahead of the
        /// session, or an empty list when the project does not ask for it. The
        /// channel is always written out so the command says what it does
        /// rather than relying on the CLI's default.
        /// </summary>
        public IReadOnlyList<string> BuildUpdateArguments()
        {
            if (!Enabled || !CheckForUpdates)
            {
                return Array.Empty<string>();
            }

            return new[] { "update", FormatUpdateChannel(UpdateChannel) };
        }

        /// <summary>
        /// The ordered commands this project launches. Returns false with a
        /// user-facing reason when the configured options cannot be turned
        /// into a safe command line.
        /// </summary>
        public bool TryBuildPlan(out CopilotLaunchPlan plan, out string error)
        {
            plan = null;

            if (!TryBuildArguments(out var session, out error))
            {
                return false;
            }

            plan = new CopilotLaunchPlan(BuildUpdateArguments(), session);
            return true;
        }

        /// <summary>
        /// Builds the Copilot CLI argument tokens for these options. Returns
        /// false with a user-facing reason when a configured name would not be
        /// safe to place on a command line, so the caller can refuse rather
        /// than silently dropping a flag the user asked for.
        /// </summary>
        public bool TryBuildArguments(out IReadOnlyList<string> arguments, out string error)
        {
            var tokens = new List<string>();
            arguments = tokens;
            error = null;

            if (!Enabled)
            {
                return true;
            }

            if (Yolo)
            {
                tokens.Add("--yolo");
            }

            var agent = (AgentName ?? string.Empty).Trim();
            if (agent.Length > 0)
            {
                if (!IsValidName(agent))
                {
                    error = "Agent name may only contain letters, numbers, dot, underscore and hyphen.";
                    return false;
                }

                tokens.Add("--agent");
                tokens.Add(agent);
            }

            if (SessionMode == CopilotSessionMode.Name)
            {
                var session = (SessionName ?? string.Empty).Trim();
                if (!IsValidName(session))
                {
                    error = session.Length == 0
                        ? "Enter a session name, or switch the session option back to Resume."
                        : "Session name may only contain letters, numbers, dot, underscore and hyphen.";
                    return false;
                }

                tokens.Add("--name");
                tokens.Add(session);
            }
            else if (SessionMode == CopilotSessionMode.Continue)
            {
                tokens.Add("--continue");
            }
            else
            {
                tokens.Add("--resume");
            }

            return true;
        }

        /// <summary>Human-readable preview of the command, for tooltips and status messages.</summary>
        public string Describe(string command)
        {
            var name = string.IsNullOrWhiteSpace(command) ? "copilot" : command.Trim();
            if (!Enabled)
            {
                return string.Empty;
            }

            if (!TryBuildPlan(out var plan, out _))
            {
                return name;
            }

            var parts = new List<string>();
            if (plan.HasUpdateStep)
            {
                parts.Add(name + " " + string.Join(" ", plan.UpdateArguments));
            }

            parts.Add(plan.SessionArguments.Count == 0
                ? name
                : name + " " + string.Join(" ", plan.SessionArguments));

            return string.Join("; ", parts);
        }
    }
}
