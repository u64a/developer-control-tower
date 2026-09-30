using System;
using System.Collections.Generic;
using System.IO;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Launch
{
    /// <summary>
    /// Finds files that may carry a launch environment's product icon. The
    /// icon is read at runtime from the user's own installation — nothing is
    /// bundled. Candidates are returned in priority order; the UI takes the
    /// first one it can actually extract an icon from.
    /// </summary>
    public static class LaunchEnvironmentIconLocator
    {
        private static readonly string[] ScriptExtensions = { ".cmd", ".bat", ".ps1" };

        public static IReadOnlyList<string> GetCandidates(LaunchEnvironment environment)
        {
            return GetCandidates(environment, File.Exists, Environment.GetEnvironmentVariable("PATH"));
        }

        public static IReadOnlyList<string> GetCandidates(
            LaunchEnvironment environment,
            Func<string, bool> fileExists,
            string pathVariable)
        {
            var results = new List<string>();
            if (environment == null)
            {
                return results;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string candidate)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate) && SafeExists(fileExists, candidate))
                {
                    results.Add(candidate);
                }
            }

            if (!string.IsNullOrWhiteSpace(environment.IconPath))
            {
                Add(environment.IconPath);
            }

            var command = Environment.ExpandEnvironmentVariables((environment.Command ?? string.Empty).Trim().Trim('"'));
            if (command.Length == 0 || command.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return results;
            }

            if (Path.IsPathRooted(command))
            {
                AddForFile(command, Add);
                return results;
            }

            if (command.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                return results;
            }

            var name = Path.HasExtension(command) ? Path.GetFileNameWithoutExtension(command) : command;
            var directories = (pathVariable ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            // Real executables first, then shims (whose siblings often hold the icon).
            foreach (var raw in directories)
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    continue;
                }

                Add(Path.Combine(dir, name + ".exe"));
            }

            foreach (var raw in directories)
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    continue;
                }

                foreach (var extension in ScriptExtensions)
                {
                    var script = Path.Combine(dir, name + extension);
                    if (SafeExists(fileExists, script))
                    {
                        AddScriptSiblings(script, Add);
                    }
                }
            }

            return results;
        }

        private static void AddForFile(string file, Action<string> add)
        {
            var extension = Path.GetExtension(file);
            if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                add(file);
                return;
            }

            foreach (var scriptExtension in ScriptExtensions)
            {
                if (string.Equals(extension, scriptExtension, StringComparison.OrdinalIgnoreCase))
                {
                    AddScriptSiblings(file, add);
                    return;
                }
            }
        }

        /// <summary>
        /// Shims such as VS Code's <c>bin\code.cmd</c> sit next to or one level
        /// below the real executable (<c>Code.exe</c>).
        /// </summary>
        private static void AddScriptSiblings(string script, Action<string> add)
        {
            var name = Path.GetFileNameWithoutExtension(script);
            var dir = Path.GetDirectoryName(script);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            add(Path.Combine(dir, name + ".exe"));
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent))
            {
                add(Path.Combine(parent, name + ".exe"));
            }
        }

        private static bool SafeExists(Func<string, bool> fileExists, string path)
        {
            try
            {
                return fileExists(path);
            }
            catch
            {
                return false;
            }
        }
    }
}
