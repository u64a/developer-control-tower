using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ControlTower.Core.Models
{
    /// <summary>Which session flag Copilot CLI starts with.</summary>
    public enum CopilotSessionMode
    {
        /// <summary><c>--resume</c>: pick up a previous session.</summary>
        Resume,

        /// <summary><c>--name &lt;name&gt;</c>: start a new, named session.</summary>
        Name
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

        /// <summary>True when nothing has been configured and the block can be omitted from project.yml.</summary>
        public bool IsDefault
        {
            get
            {
                return !Enabled &&
                       !Yolo &&
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
                Yolo = Yolo
            };
        }

        public static bool IsValidName(string value)
        {
            return !string.IsNullOrEmpty(value) && NamePattern.IsMatch(value);
        }

        /// <summary>Parses a persisted session mode; anything unrecognised falls back to resume.</summary>
        public static CopilotSessionMode ParseSessionMode(string value)
        {
            return string.Equals((value ?? string.Empty).Trim(), "name", StringComparison.OrdinalIgnoreCase)
                ? CopilotSessionMode.Name
                : CopilotSessionMode.Resume;
        }

        public static string FormatSessionMode(CopilotSessionMode mode)
        {
            return mode == CopilotSessionMode.Name ? "name" : "resume";
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

            return TryBuildArguments(out var tokens, out _)
                ? name + (tokens.Count == 0 ? string.Empty : " " + string.Join(" ", tokens))
                : name;
        }
    }
}
