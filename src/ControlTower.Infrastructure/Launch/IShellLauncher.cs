using System;
using System.Diagnostics;

namespace ControlTower.Infrastructure.Launch
{
    /// <summary>
    /// Opens a path, folder, or URI through the OS shell. Centralised so
    /// every UI surface that needs to "show this in Explorer / Notepad /
    /// the browser" goes through one validated, testable seam instead of
    /// scattering raw <see cref="Process.Start(ProcessStartInfo)"/> calls.
    /// </summary>
    public interface IShellLauncher
    {
        /// <summary>
        /// Opens the supplied target via ShellExecute. Throws
        /// <see cref="ArgumentException"/> when the target is null/empty.
        /// </summary>
        void Open(string pathOrUri);

        /// <summary>
        /// Spawns cmd.exe with a single script argument in a visible console
        /// window. Used by the self-update flow. Returns the spawned PID, or
        /// 0 if spawn failed.
        /// </summary>
        int LaunchUpdateConsole(string scriptPath);

        /// <summary>
        /// Same as <see cref="LaunchUpdateConsole(string)"/> but launches the
        /// console elevated (UAC prompt). Used by the apply-update flow,
        /// which must write into the installed app location (e.g. under
        /// <c>C:\Program Files\…</c>) that a non-elevated user cannot touch.
        /// Returns the spawned PID, or 0 if spawn failed / UAC was declined.
        /// </summary>
        int LaunchUpdateConsoleElevated(string scriptPath);

        /// <summary>
        /// Starts a generated, local PowerShell maintenance script without
        /// elevation. Returns the spawned PID, or 0 when launch fails.
        /// </summary>
        int LaunchPowerShellScript(string scriptPath);
    }

    public sealed class WindowsShellLauncher : IShellLauncher
    {
        private readonly Action<ProcessStartInfo> _starter;
        private readonly Func<ProcessStartInfo, int> _consoleStarter;
        private readonly Func<ProcessStartInfo, int> _elevatedConsoleStarter;

        public WindowsShellLauncher()
            : this(null, null, null)
        {
        }

        // Test seam mirroring WindowsLaunchService — a custom starter lets
        // tests verify the ProcessStartInfo we hand to ShellExecute without
        // launching real processes.
        public WindowsShellLauncher(Action<ProcessStartInfo> starter)
            : this(starter, null, null)
        {
        }

        public WindowsShellLauncher(
            Action<ProcessStartInfo> starter,
            Func<ProcessStartInfo, int> consoleStarter)
            : this(starter, consoleStarter, null)
        {
        }

        public WindowsShellLauncher(
            Action<ProcessStartInfo> starter,
            Func<ProcessStartInfo, int> consoleStarter,
            Func<ProcessStartInfo, int> elevatedConsoleStarter)
        {
            _starter = starter ?? (info => Process.Start(info));
            _consoleStarter = consoleStarter ?? DefaultConsoleStarter;
            _elevatedConsoleStarter = elevatedConsoleStarter ?? DefaultConsoleStarter;
        }

        public void Open(string pathOrUri)
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
            {
                throw new ArgumentException("Path or URI is required.", nameof(pathOrUri));
            }

            var info = new ProcessStartInfo(pathOrUri) { UseShellExecute = true };
            _starter(info);
        }

        public int LaunchUpdateConsole(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                throw new ArgumentException("Script path is required.", nameof(scriptPath));
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            // ArgumentList quotes each entry for us — never concatenate the
            // script path into the command line by hand.
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(scriptPath);
            return _consoleStarter(psi);
        }

        public int LaunchUpdateConsoleElevated(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                throw new ArgumentException("Script path is required.", nameof(scriptPath));
            }

            // ShellExecute with Verb=runas is the documented Windows way to
            // request UAC elevation from a desktop app. Arguments must be
            // passed via the Arguments string (ArgumentList is ignored when
            // UseShellExecute=true), so we hand-quote the script path.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c \"" + scriptPath + "\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            try
            {
                return _elevatedConsoleStarter(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // ERROR_CANCELLED (1223) is raised when the user declines
                // the UAC prompt. Surface as "spawn failed" rather than
                // crashing the host so the caller can show a clean error.
                return 0;
            }
        }

        public int LaunchPowerShellScript(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                throw new ArgumentException("Script path is required.", nameof(scriptPath));
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            return _consoleStarter(psi);
        }

        private static int DefaultConsoleStarter(ProcessStartInfo psi)
        {
            using var p = Process.Start(psi);
            return p?.Id ?? 0;
        }
    }
}
