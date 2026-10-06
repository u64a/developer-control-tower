using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Launch
{
    /// <summary>Outcome of preparing a workspace to run Copilot CLI on folder open.</summary>
    public sealed class VsCodeTaskResult
    {
        private VsCodeTaskResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        public bool Success { get; }

        /// <summary>User-facing reason the task could not be written; null on success.</summary>
        public string Error { get; }

        public static VsCodeTaskResult Ok()
        {
            return new VsCodeTaskResult(true, null);
        }

        public static VsCodeTaskResult Failed(string error)
        {
            return new VsCodeTaskResult(false, error);
        }
    }

    /// <summary>
    /// Writes the <c>.vscode/tasks.json</c> entry that starts Copilot CLI in
    /// VS Code's integrated terminal. VS Code exposes no command line for
    /// running something in its terminal, so a task with
    /// <c>runOptions.runOn = folderOpen</c> is the only supported mechanism.
    /// </summary>
    public static class VsCodeTaskFile
    {
        /// <summary>Identifies the task this tool owns, so user tasks are never touched.</summary>
        public const string TaskLabel = "Developer Control Tower: Copilot CLI";

        /// <summary>
        /// The <c>copilot update</c> task the session task depends on. A task
        /// takes a command plus an argument array rather than a shell line, so
        /// the update subcommand cannot be chained onto the session command and
        /// runs as its own task instead.
        /// </summary>
        public const string UpdateTaskLabel = "Developer Control Tower: Copilot CLI update";

        private const string ExcludeEntry = "/.vscode/tasks.json";

        /// <summary>
        /// Creates or updates the Copilot task in <paramref name="workspacePath"/>.
        /// An existing tasks.json is merged: every other task is preserved and
        /// only the tool's own labels are replaced. A file that cannot be parsed
        /// is left untouched rather than overwritten.
        /// </summary>
        public static VsCodeTaskResult Write(
            string workspacePath,
            string command,
            CopilotLaunchPlan plan,
            bool addGitExclude)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            {
                return VsCodeTaskResult.Failed("the workspace folder is not available");
            }

            var tasksPath = Path.Combine(workspacePath, ".vscode", "tasks.json");

            JsonObject root;
            if (File.Exists(tasksPath))
            {
                if (!TryParse(tasksPath, out root, out var parseError))
                {
                    return VsCodeTaskResult.Failed(parseError);
                }
            }
            else
            {
                root = new JsonObject();
            }

            if (root["version"] == null)
            {
                root["version"] = "2.0.0";
            }

            if (root["tasks"] is not JsonArray tasks)
            {
                tasks = new JsonArray();
                root["tasks"] = tasks;
            }

            RemoveOwnTasks(tasks);

            var hasUpdate = plan != null && plan.HasUpdateStep;
            if (hasUpdate)
            {
                var update = BuildTask(UpdateTaskLabel, command, plan.UpdateArguments);
                update["detail"] = "Downloads the latest Copilot CLI before the session starts.";
                update["presentation"]["focus"] = false;
                update.Remove("runOptions");
                tasks.Add(update);
            }

            var session = BuildTask(TaskLabel, command, plan?.SessionArguments);
            if (hasUpdate)
            {
                session["dependsOn"] = new JsonArray { UpdateTaskLabel };
                session["dependsOrder"] = "sequence";
            }

            tasks.Add(session);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tasksPath));
                var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tasksPath, json + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return VsCodeTaskResult.Failed("could not write .vscode/tasks.json (" + ex.Message + ")");
            }

            if (addGitExclude)
            {
                TryAddGitExclude(workspacePath);
            }

            return VsCodeTaskResult.Ok();
        }

        /// <summary>Removes the tool's task, leaving any user tasks in place.</summary>
        public static void Remove(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                return;
            }

            var tasksPath = Path.Combine(workspacePath, ".vscode", "tasks.json");
            if (!File.Exists(tasksPath) || !TryParse(tasksPath, out var root, out _))
            {
                return;
            }

            if (root["tasks"] is not JsonArray tasks || !RemoveOwnTasks(tasks))
            {
                return;
            }

            try
            {
                var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tasksPath, json + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Leaving a stale task behind is harmless; it simply starts a
                // session the user can close.
            }
        }

        private static bool TryParse(string path, out JsonObject root, out string error)
        {
            root = null;
            error = null;

            try
            {
                // tasks.json is JSON with comments, which VS Code accepts.
                var node = JsonNode.Parse(
                    File.ReadAllText(path),
                    null,
                    new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                root = node as JsonObject;
                if (root == null)
                {
                    error = "the existing .vscode/tasks.json is not a JSON object, so it was left unchanged";
                    return false;
                }

                return true;
            }
            catch (JsonException)
            {
                error = "the existing .vscode/tasks.json could not be parsed, so it was left unchanged";
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = "the existing .vscode/tasks.json could not be read (" + ex.Message + ")";
                return false;
            }
        }

        private static bool RemoveOwnTasks(JsonArray tasks)
        {
            var removed = false;
            for (var i = tasks.Count - 1; i >= 0; i--)
            {
                if (tasks[i] is JsonObject task &&
                    task["label"] is JsonValue label &&
                    IsOwnLabel(label.GetValue<string>()))
                {
                    tasks.RemoveAt(i);
                    removed = true;
                }
            }

            return removed;
        }

        private static bool IsOwnLabel(string label)
        {
            return string.Equals(label, TaskLabel, StringComparison.Ordinal) ||
                   string.Equals(label, UpdateTaskLabel, StringComparison.Ordinal);
        }

        private static JsonObject BuildTask(string label, string command, IReadOnlyList<string> arguments)
        {
            var args = new JsonArray();
            foreach (var argument in arguments ?? Array.Empty<string>())
            {
                args.Add(argument);
            }

            return new JsonObject
            {
                ["label"] = label,
                ["detail"] = "Started by Developer Control Tower when this folder opens.",
                ["type"] = "shell",
                ["command"] = string.IsNullOrWhiteSpace(command) ? "copilot" : command.Trim(),
                ["args"] = args,
                ["options"] = new JsonObject { ["cwd"] = "${workspaceFolder}" },
                ["presentation"] = new JsonObject
                {
                    ["reveal"] = "always",
                    ["panel"] = "dedicated",
                    ["focus"] = true,
                    ["clear"] = true
                },
                ["runOptions"] = new JsonObject { ["runOn"] = "folderOpen" },
                ["problemMatcher"] = new JsonArray()
            };
        }

        /// <summary>
        /// Keeps the generated task out of version control without editing a
        /// tracked .gitignore. Only applies at a repository root with a real
        /// .git directory; worktrees and non-repositories are skipped.
        /// </summary>
        private static void TryAddGitExclude(string workspacePath)
        {
            try
            {
                var infoDir = Path.Combine(workspacePath, ".git", "info");
                if (!Directory.Exists(Path.Combine(workspacePath, ".git")))
                {
                    return;
                }

                Directory.CreateDirectory(infoDir);
                var excludePath = Path.Combine(infoDir, "exclude");
                var lines = File.Exists(excludePath)
                    ? File.ReadAllLines(excludePath).ToList()
                    : new List<string>();

                if (lines.Any(line => string.Equals(line.Trim(), ExcludeEntry, StringComparison.Ordinal)))
                {
                    return;
                }

                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add("# Developer Control Tower: generated Copilot CLI task");
                lines.Add(ExcludeEntry);
                File.WriteAllLines(excludePath, lines);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The task still works; it just shows up as an untracked file.
            }
        }
    }
}
