using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Launch
{
    /// <summary>Result of the optional, guarded maintenance that precedes a local VS Code launch.</summary>
    public sealed class VsCodeMaintenanceResult
    {
        public VsCodeMaintenanceResult(bool canLaunch, string message)
        {
            CanLaunch = canLaunch;
            Message = message ?? string.Empty;
        }

        public bool CanLaunch { get; }
        public string Message { get; }
    }

    /// <summary>Public seam for exercising launch ordering without downloading or installing software.</summary>
    public interface IVsCodePreLaunchMaintenance
    {
        VsCodeMaintenanceResult Run(VsCodeMaintenance options, string codeCommand);
    }

    /// <summary>
    /// Updates only the official per-user stable Windows install. Other install
    /// types are deliberately skipped instead of changing scope/channel or
    /// asking an installer to close an existing editor.
    /// </summary>
    public sealed class VsCodePreLaunchMaintenance : IVsCodePreLaunchMaintenance
    {
        private const string StableInstallerEndpointPrefix = "https://update.code.visualstudio.com/latest/win32-";
        private const string UserStableProductId = "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1";
        private const string MicrosoftPublisher = "Microsoft Corporation";
        private static readonly object ActiveMaintenanceLock = new object();
        private static Process _activeMaintenanceProcess;
        private readonly Func<ProcessStartInfo, Process> _startProcess;
        private readonly Func<string> _processBlockReason;
        private readonly Func<bool> _hasPendingMaintenance;
        private readonly Func<VsCodeMaintenanceResult> _updateCode;
        private readonly Func<string, VsCodeMaintenanceResult> _updateExtensions;

        public VsCodePreLaunchMaintenance()
            : this(info => Process.Start(info))
        {
        }

        public VsCodePreLaunchMaintenance(Func<ProcessStartInfo, Process> startProcess)
        {
            _startProcess = startProcess ?? (info => Process.Start(info));
            _processBlockReason = GetProcessBlockReason;
            _hasPendingMaintenance = HasActiveMaintenanceProcess;
            _updateCode = null;
            _updateExtensions = null;
        }

        internal VsCodePreLaunchMaintenance(
            Func<string> processBlockReason,
            Func<VsCodeMaintenanceResult> updateCode,
            Func<string, VsCodeMaintenanceResult> updateExtensions)
            : this(processBlockReason, updateCode, updateExtensions, () => false)
        {
        }

        internal VsCodePreLaunchMaintenance(
            Func<string> processBlockReason,
            Func<VsCodeMaintenanceResult> updateCode,
            Func<string, VsCodeMaintenanceResult> updateExtensions,
            Func<bool> hasPendingMaintenance)
        {
            _startProcess = info => Process.Start(info);
            _processBlockReason = processBlockReason ?? (() => "VS Code process detection is unavailable; updates fail closed.");
            _hasPendingMaintenance = hasPendingMaintenance ?? (() => true);
            _updateCode = updateCode ?? (() => new VsCodeMaintenanceResult(false, "VS Code update is unavailable; updates fail closed."));
            _updateExtensions = updateExtensions ?? (_ => new VsCodeMaintenanceResult(false, "VS Code extension update is unavailable; updates fail closed."));
        }

        public VsCodeMaintenanceResult Run(VsCodeMaintenance options, string codeCommand)
        {
            if (HasPendingMaintenance())
            {
                return new VsCodeMaintenanceResult(false, "VS Code maintenance is still running; editor launch was not started.");
            }

            if (options == null || options.IsDefault)
            {
                return new VsCodeMaintenanceResult(true, string.Empty);
            }

            if (!TryGetProcessBlockReason(out var runningMessage))
            {
                return new VsCodeMaintenanceResult(true, "VS Code and extension updates skipped: " + runningMessage);
            }

            var notes = string.Empty;
            if (options.UpdateVsCode)
            {
                var update = InvokeUpdateCode(codeCommand);
                notes = update.Message;
                if (!update.CanLaunch)
                {
                    return update;
                }
            }

            if (options.UpdateExtensions)
            {
                if (!TryGetProcessBlockReason(out runningMessage))
                {
                    return new VsCodeMaintenanceResult(true, Join(notes, "VS Code extensions skipped: " + runningMessage));
                }

                var extension = InvokeUpdateExtensions(codeCommand);
                notes = Join(notes, extension.Message);
                if (!extension.CanLaunch)
                {
                    return new VsCodeMaintenanceResult(false, notes);
                }
            }

            return new VsCodeMaintenanceResult(true, notes);
        }

        private bool HasPendingMaintenance()
        {
            try
            {
                return _hasPendingMaintenance();
            }
            catch
            {
                return true;
            }
        }

        private bool TryGetProcessBlockReason(out string reason)
        {
            try
            {
                reason = _processBlockReason();
                return string.IsNullOrWhiteSpace(reason);
            }
            catch (Exception ex)
            {
                reason = "process detection failed (" + ex.Message + "); updates fail closed.";
                return false;
            }
        }

        private VsCodeMaintenanceResult InvokeUpdateCode(string codeCommand)
        {
            try
            {
                return _updateCode == null
                    ? UpdateOfficialUserStableInstall(codeCommand)
                    : _updateCode();
            }
            catch (Exception ex)
            {
                return new VsCodeMaintenanceResult(false, "VS Code update failed closed: " + ex.Message);
            }
        }

        private VsCodeMaintenanceResult InvokeUpdateExtensions(string codeCommand)
        {
            try
            {
                return _updateExtensions == null
                    ? UpdateExtensions(codeCommand)
                    : _updateExtensions(codeCommand);
            }
            catch (Exception ex)
            {
                return new VsCodeMaintenanceResult(false, "VS Code extension update failed closed: " + ex.Message);
            }
        }

        private VsCodeMaintenanceResult UpdateExtensions(string codeCommand)
        {
            if (!TryGetProcessBlockReason(out var runningMessage))
            {
                return new VsCodeMaintenanceResult(true, "VS Code extensions skipped: " + runningMessage);
            }

            if (!TryGetOfficialUserInstall(out var codeExe, out _, out _, out var installReason))
            {
                return new VsCodeMaintenanceResult(true, "VS Code extensions skipped: " + installReason);
            }

            if (!TryResolveOfficialCodeCommand(codeCommand, codeExe, out var resolvedCommand, out var commandReason))
            {
                return new VsCodeMaintenanceResult(true, "VS Code extensions skipped: " + commandReason);
            }

            try
            {
                var process = _startProcess(new ProcessStartInfo
                {
                    FileName = resolvedCommand,
                    Arguments = "--update-extensions",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                if (process == null)
                {
                    return new VsCodeMaintenanceResult(true, "VS Code extensions were not updated: command did not start.");
                }

                var retained = false;
                try
                {
                    if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
                    {
                        RetainActiveMaintenanceProcess(process);
                        retained = true;
                        return new VsCodeMaintenanceResult(false, "VS Code extension update is still running; editor launch was not started.");
                    }

                    var output = ReadBoundedOutput(process);
                    if (process.ExitCode != 0)
                    {
                        return new VsCodeMaintenanceResult(true, "VS Code extension update returned exit code " + process.ExitCode + "; opening editor.");
                    }

                    return output.IndexOf("updated", StringComparison.OrdinalIgnoreCase) >= 0
                        ? new VsCodeMaintenanceResult(true, "VS Code extensions updated.")
                        : new VsCodeMaintenanceResult(true, "VS Code extension update completed without reported changes; opening editor.");
                }
                finally
                {
                    if (!retained)
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return new VsCodeMaintenanceResult(true, "VS Code extensions were not updated: " + ex.Message);
            }
        }

        private VsCodeMaintenanceResult UpdateOfficialUserStableInstall(string codeCommand)
        {
            if (!TryGetOfficialUserInstall(out var codeExe, out var installedVersion, out var architecture, out var unsupportedReason))
            {
                return new VsCodeMaintenanceResult(true, "VS Code update skipped: " + unsupportedReason);
            }

            if (!TryResolveOfficialCodeCommand(codeCommand, codeExe, out _, out var commandReason))
            {
                return new VsCodeMaintenanceResult(true, "VS Code update skipped: " + commandReason);
            }

            var maintenanceDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperControlTower",
                "maintenance");
            var installerPath = Path.Combine(maintenanceDirectory, "VSCodeUserSetup.exe");

            try
            {
                Directory.CreateDirectory(maintenanceDirectory);
                var endpoint = StableInstallerEndpointPrefix + architecture + "-user/stable";
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                using (var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                using (var response = client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                    {
                        return new VsCodeMaintenanceResult(true, "VS Code update skipped: official update service was unavailable.");
                    }

                    using (var source = response.Content.ReadAsStream(cancellation.Token))
                    using (var target = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        source.CopyToAsync(target, 81920, cancellation.Token).GetAwaiter().GetResult();
                    }
                }

                if (!IsMicrosoftSigned(installerPath))
                {
                    return new VsCodeMaintenanceResult(true, "VS Code update skipped: downloaded installer did not have a valid Microsoft Authenticode signature.");
                }

                if (!TryGetVsCodeVersion(installerPath, out var candidateVersion) ||
                    !IsNewerVersion(candidateVersion, installedVersion))
                {
                    return new VsCodeMaintenanceResult(true, "VS Code is already up to date.");
                }

                if (!TryGetProcessBlockReason(out var runningMessage))
                {
                    return new VsCodeMaintenanceResult(true, "VS Code update skipped: " + runningMessage);
                }

                var installer = _startProcess(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NOCLOSEAPPLICATIONS /NOFORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /NORESTART /MERGETASKS=\"!runcode\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (installer == null)
                {
                    return new VsCodeMaintenanceResult(true, "VS Code update did not start; opening editor.");
                }

                var retained = false;
                try
                {
                    if (!installer.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
                    {
                        RetainActiveMaintenanceProcess(installer);
                        retained = true;
                        return new VsCodeMaintenanceResult(false, "VS Code updater is still running; editor launch was not started.");
                    }

                    if (installer.ExitCode != 0)
                    {
                        return new VsCodeMaintenanceResult(true, "VS Code update returned exit code " + installer.ExitCode + "; opening editor.");
                    }
                }
                finally
                {
                    if (!retained)
                    {
                        installer.Dispose();
                    }
                }

                return TryGetOfficialUserInstall(out var updatedCodeExe, out var updatedVersion, out _, out _) &&
                       string.Equals(updatedCodeExe, codeExe, StringComparison.OrdinalIgnoreCase) &&
                       IsNewerVersion(updatedVersion, installedVersion) &&
                       VersionsEqual(updatedVersion, candidateVersion)
                    ? new VsCodeMaintenanceResult(true, "VS Code updated.")
                    : new VsCodeMaintenanceResult(true, "VS Code update completed but the expected version could not be verified; opening editor.");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is OperationCanceledException)
            {
                return new VsCodeMaintenanceResult(true, "VS Code update skipped: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
            }
        }

        private static bool TryGetOfficialUserInstall(out string codeExe, out string installedVersion, out string architecture, out string reason)
        {
            codeExe = string.Empty;
            installedVersion = string.Empty;
            architecture = string.Empty;
            reason = "only the official per-user stable VS Code installer is supported.";
            if (!OperatingSystem.IsWindows())
            {
                reason = "VS Code pre-launch updates are only supported on Windows.";
                return false;
            }
            try
            {
                using (var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall == null) return false;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        if (!string.Equals(name, UserStableProductId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using (var key = uninstall.OpenSubKey(name))
                        {
                            if (!string.Equals(key?.GetValue("Publisher") as string, MicrosoftPublisher, StringComparison.Ordinal) ||
                                string.IsNullOrWhiteSpace(key.GetValue("DisplayVersion") as string))
                            {
                                continue;
                            }

                            var location = key.GetValue("InstallLocation") as string;
                            var candidate = string.IsNullOrWhiteSpace(location) ? string.Empty : Path.Combine(location, "Code.exe");
                            var expected = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Programs",
                                "Microsoft VS Code",
                                "Code.exe");
                            if (!File.Exists(candidate) || !PathsEqual(candidate, expected))
                            {
                                reason = "the registered stable per-user VS Code executable was not found at its trusted install path.";
                                return false;
                            }

                            codeExe = Path.GetFullPath(candidate);
                            if (!TryGetVsCodeVersion(codeExe, out installedVersion))
                            {
                                reason = "the registered VS Code executable did not expose a valid VS Code version.";
                                return false;
                            }
                            if (!TryGetArchitecture(candidate, out architecture))
                            {
                                reason = "the installed VS Code architecture is unsupported.";
                                return false;
                            }
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                reason = "the local VS Code installation could not be safely identified.";
            }
            return false;
        }

        private static bool TryGetArchitecture(string executable, out string architecture)
        {
            architecture = string.Empty;
            try
            {
                using (var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Position = 0x3c;
                    stream.Position = reader.ReadInt32() + 4;
                    var machine = reader.ReadUInt16();
                    architecture = machine == 0x8664 ? "x64" : machine == 0xaa64 ? "arm64" : string.Empty;
                    return architecture.Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryEnsureNoVsCodeProcesses(out string reason)
        {
            reason = string.Empty;
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        var name = process.ProcessName;
                        if (string.Equals(name, "Code", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "Code - Insiders", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "code-tunnel", StringComparison.OrdinalIgnoreCase))
                        {
                            reason = "a VS Code, VS Code Insiders, or Code tunnel process is already running.";
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "process detection failed (" + ex.Message + "); updates fail closed.";
                return false;
            }
        }

        private static bool IsNewerVersion(string candidate, string installed)
        {
            return Version.TryParse(NormalizeVersion(candidate), out var next) &&
                   Version.TryParse(NormalizeVersion(installed), out var current) &&
                   next > current;
        }

        private static string NormalizeVersion(string value)
        {
            var text = value ?? string.Empty;
            var end = text.IndexOfAny(new[] { '-', '+', ' ' });
            return end >= 0 ? text.Substring(0, end) : text;
        }

        private static bool VersionsEqual(string first, string second)
        {
            return Version.TryParse(NormalizeVersion(first), out var left) &&
                   Version.TryParse(NormalizeVersion(second), out var right) &&
                   left == right;
        }

        private static bool TryGetVsCodeVersion(string path, out string version)
        {
            version = string.Empty;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (info.ProductName?.IndexOf("Visual Studio Code", StringComparison.OrdinalIgnoreCase) < 0 ||
                    !Version.TryParse(NormalizeVersion(info.ProductVersion), out _))
                {
                    return false;
                }

                version = info.ProductVersion;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMicrosoftSigned(string path)
        {
            try
            {
#pragma warning disable SYSLIB0057 // No supported .NET API extracts the Authenticode signer from a PE file.
                using (var cert = X509Certificate2.CreateFromSignedFile(path))
#pragma warning restore SYSLIB0057
                {
                    return cert.Subject.IndexOf("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           VerifyAuthenticode(path);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetProcessBlockReason()
        {
            return TryEnsureNoVsCodeProcesses(out var reason) ? string.Empty : reason;
        }

        private static bool HasActiveMaintenanceProcess()
        {
            lock (ActiveMaintenanceLock)
            {
                if (_activeMaintenanceProcess == null)
                {
                    return false;
                }

                try
                {
                    if (!_activeMaintenanceProcess.HasExited)
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }

                _activeMaintenanceProcess.Dispose();
                _activeMaintenanceProcess = null;
                return false;
            }
        }

        private static void RetainActiveMaintenanceProcess(Process process)
        {
            lock (ActiveMaintenanceLock)
            {
                _activeMaintenanceProcess?.Dispose();
                _activeMaintenanceProcess = process;
                process.EnableRaisingEvents = true;
                process.Exited += (sender, _) =>
                {
                    lock (ActiveMaintenanceLock)
                    {
                        if (!ReferenceEquals(_activeMaintenanceProcess, sender))
                        {
                            return;
                        }

                        _activeMaintenanceProcess.Dispose();
                        _activeMaintenanceProcess = null;
                    }
                };
            }
        }

        private static bool TryResolveOfficialCodeCommand(string codeCommand, string officialCodeExe, out string resolvedCommand, out string reason)
        {
            resolvedCommand = string.Empty;
            reason = "the configured VS Code command does not resolve to the official per-user stable installation.";
            var command = (codeCommand ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(new[] { ' ', '\t', '\r', '\n', ';', '&', '|', '<', '>' }) >= 0)
            {
                return false;
            }

            var candidates = new List<string>();
            if (Path.IsPathFullyQualified(command))
            {
                candidates.Add(command);
            }
            else if (string.Equals(command, "code", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(command, "code.cmd", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(command, "Code.exe", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
                {
                    if (Path.IsPathFullyQualified(directory))
                    {
                        candidates.Add(Path.Combine(directory, command));
                    }
                }
            }
            else
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(candidate);
                    if (string.Equals(name, "Code.exe", StringComparison.OrdinalIgnoreCase) && PathsEqual(candidate, officialCodeExe))
                    {
                        resolvedCommand = Path.GetFullPath(candidate);
                        return true;
                    }

                    var expectedLauncher = Path.Combine(Path.GetDirectoryName(officialCodeExe), "bin", name);
                    if ((string.Equals(name, "code", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, "code.cmd", StringComparison.OrdinalIgnoreCase)) &&
                        PathsEqual(candidate, expectedLauncher))
                    {
                        resolvedCommand = Path.GetFullPath(candidate);
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Invalid PATH entries are ignored; only an exact trusted match is eligible.
                }
            }

            return false;
        }

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadBoundedOutput(Process process)
        {
            const int maximumLength = 4096;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            var combined = output + Environment.NewLine + error;
            return combined.Length <= maximumLength ? combined : combined.Substring(0, maximumLength);
        }

        private static bool VerifyAuthenticode(string path)
        {
            var fileInfo = new WinTrustFileInfo(path);
            var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo));
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData(fileInfoPointer);
            var trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf(trustData));
            try
            {
                Marshal.StructureToPtr(trustData, trustDataPointer, false);
                var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                return WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(trustDataPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] ref Guid actionId, IntPtr trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;

            public WinTrustFileInfo(string filePath)
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
                FilePath = filePath;
                FileHandle = IntPtr.Zero;
                KnownSubject = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SIPClientData;
            public uint UIChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr URLReference;
            public uint ProvFlags;
            public uint UIContext;
            public IntPtr SignatureSettings;

            public WinTrustData(IntPtr fileInfo)
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>();
                PolicyCallbackData = IntPtr.Zero;
                SIPClientData = IntPtr.Zero;
                UIChoice = 2; // WTD_UI_NONE
                RevocationChecks = 0;
                UnionChoice = 1; // WTD_CHOICE_FILE
                FileInfo = fileInfo;
                StateAction = 0;
                StateData = IntPtr.Zero;
                URLReference = IntPtr.Zero;
                ProvFlags = 0x00000040; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                UIContext = 0;
                SignatureSettings = IntPtr.Zero;
            }
        }

        private static string Join(string first, string second)
        {
            return string.IsNullOrWhiteSpace(first) ? second ?? string.Empty :
                string.IsNullOrWhiteSpace(second) ? first : first + " " + second;
        }
    }
}
