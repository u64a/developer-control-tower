#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ControlTower.Infrastructure.Update
{
    internal sealed record PackagedUpdateCandidate(
        string Version,
        string Artifact,
        object NativeUpdate);

    internal interface IPackagedUpdateClient
    {
        bool IsInstalled { get; }
        string CurrentVersion { get; }
        string Channel { get; }
        string Source { get; }

        Task<PackagedUpdateCandidate?> CheckForUpdatesAsync(CancellationToken ct);

        Task DownloadAsync(
            PackagedUpdateCandidate candidate,
            Action<int> progress,
            CancellationToken ct);

        void ApplyAndRestart(PackagedUpdateCandidate candidate);
    }

    internal sealed class VelopackUpdateClient : IPackagedUpdateClient
    {
        private readonly UpdateManager _manager;

        public VelopackUpdateClient(string repositoryUrl, bool includePrereleases)
        {
            var source = new GithubSource(
                repositoryUrl,
                accessToken: null,
                prerelease: includePrereleases,
                downloader: null);
            _manager = new UpdateManager(
                source,
                new Velopack.UpdateOptions(),
                locator: null);
            Source = repositoryUrl;
        }

        public bool IsInstalled => _manager.IsInstalled;

        public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? string.Empty;

        public string Channel
        {
            get
            {
                try
                {
                    return VelopackLocator.Current.Channel ?? string.Empty;
                }
                catch (InvalidOperationException)
                {
                    return string.Empty;
                }
            }
        }

        public string Source { get; }

        public async Task<PackagedUpdateCandidate?> CheckForUpdatesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (update == null)
            {
                return null;
            }

            return new PackagedUpdateCandidate(
                update.TargetFullRelease.Version?.ToString() ?? string.Empty,
                update.TargetFullRelease.FileName ?? string.Empty,
                update);
        }

        public Task DownloadAsync(
            PackagedUpdateCandidate candidate,
            Action<int> progress,
            CancellationToken ct)
        {
            var update = RequireNativeUpdate(candidate);
            return _manager.DownloadUpdatesAsync(update, progress, ct);
        }

        public void ApplyAndRestart(PackagedUpdateCandidate candidate)
        {
            var update = RequireNativeUpdate(candidate);
            _manager.ApplyUpdatesAndRestart(
                update.TargetFullRelease,
                Array.Empty<string>());
        }

        private static UpdateInfo RequireNativeUpdate(PackagedUpdateCandidate candidate)
        {
            if (candidate.NativeUpdate is not UpdateInfo update)
            {
                throw new InvalidOperationException(
                    "The packaged update candidate is not a Velopack update.");
            }

            return update;
        }
    }

    public sealed class VelopackUpdateService : IUpdateService
    {
        private const string RepositoryMetadataKey = "UpdateRepositoryUrl";
        private const string PrereleaseMetadataKey = "UpdateIncludePrereleases";

        private readonly IPackagedUpdateClient _client;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private PackagedUpdateCandidate? _pendingUpdate;

        internal VelopackUpdateService(IPackagedUpdateClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public UpdateProviderKind ProviderKind => UpdateProviderKind.PackagedRelease;

        public static IUpdateService? TryCreateFromEntryAssembly()
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly == null)
            {
                return IsPackagedInstall()
                    ? new UnavailablePackagedUpdateService(
                        "Packaged update metadata could not be read.")
                    : null;
            }

            if (!TryReadConfiguration(
                assembly,
                out var repositoryUrl,
                out var includePrereleases))
            {
                return IsPackagedInstall()
                    ? new UnavailablePackagedUpdateService(
                        "This installed release has no valid GitHub update source.")
                    : null;
            }

            try
            {
                var client = new VelopackUpdateClient(
                    repositoryUrl,
                    includePrereleases);
                return client.IsInstalled
                    ? new VelopackUpdateService(client)
                    : null;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "Update",
                    "Packaged update initialization failed: " + ex.Message,
                    ex);
                return IsPackagedInstall()
                    ? new UnavailablePackagedUpdateService(
                        "The packaged update provider could not be initialized.")
                    : null;
            }
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(
            Core.Models.UpdateOptions options,
            CancellationToken ct)
        {
            await _operationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _pendingUpdate = null;
                if (!_client.IsInstalled)
                {
                    return Result(
                        UpdateStatus.NotEligible,
                        "Packaged updates are available only from an installed release.");
                }

                PackagedUpdateCandidate? candidate;
                try
                {
                    candidate = await _client
                        .CheckForUpdatesAsync(ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "Update",
                        "Packaged update check failed: " + ex.Message);
                    return Result(
                        UpdateStatus.FetchFailed,
                        "Could not check GitHub Releases. Check the network connection and try again.");
                }

                if (candidate == null)
                {
                    return Result(
                        UpdateStatus.UpToDate,
                        "You are running the latest release.");
                }

                _pendingUpdate = candidate;
                return Result(
                    UpdateStatus.UpdateAvailable,
                    "Version " + candidate.Version + " is available.",
                    candidate);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task<UpdateLaunchResult> LaunchUpdateAsync(
            UpdateCheckResult lastCheck,
            CancellationToken ct,
            IProgress<int>? progress = null)
        {
            if (lastCheck == null)
            {
                return new UpdateLaunchResult(
                    false,
                    string.Empty,
                    "No previous check result is available.");
            }

            if (lastCheck.Provider != UpdateProviderKind.PackagedRelease ||
                lastCheck.Status != UpdateStatus.UpdateAvailable)
            {
                return new UpdateLaunchResult(
                    false,
                    string.Empty,
                    "A packaged update is not available.");
            }

            await _operationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var candidate = _pendingUpdate;
                if (candidate == null ||
                    !string.Equals(
                        candidate.Version,
                        lastCheck.TargetVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateLaunchResult(
                        false,
                        string.Empty,
                        "The available release changed. Check for updates again.");
                }

                try
                {
                    await _client.DownloadAsync(
                        candidate,
                        value => progress?.Report(Math.Clamp(value, 0, 100)),
                        ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    _client.ApplyAndRestart(candidate);
                    return new UpdateLaunchResult(
                        true,
                        candidate.Artifact,
                        "The update was downloaded and handed to the installer.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new UpdateLaunchResult(
                        false,
                        candidate.Artifact,
                        "The update download was cancelled.");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        "Update",
                        "Packaged update failed: " + ex.Message,
                        ex);
                    return new UpdateLaunchResult(
                        false,
                        candidate.Artifact,
                        "The update could not be downloaded or applied: " + ex.Message);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        internal static bool TryReadConfiguration(
            Assembly assembly,
            out string repositoryUrl,
            out bool includePrereleases)
        {
            repositoryUrl = string.Empty;
            includePrereleases = false;

            var metadata = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!string.IsNullOrWhiteSpace(attribute.Key))
                {
                    metadata[attribute.Key] = attribute.Value ?? string.Empty;
                }
            }

            if (!metadata.TryGetValue(RepositoryMetadataKey, out var configuredUrl) ||
                !Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !HasGitHubRepositoryPath(uri))
            {
                return false;
            }

            repositoryUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (metadata.TryGetValue(PrereleaseMetadataKey, out var prereleaseValue))
            {
                bool.TryParse(prereleaseValue, out includePrereleases);
            }

            return true;
        }

        private static bool HasGitHubRepositoryPath(Uri uri)
        {
            var segments = uri.AbsolutePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2)
            {
                return false;
            }

            foreach (var segment in segments)
            {
                if (segment.Length == 0 ||
                    segment.Any(character =>
                        !char.IsLetterOrDigit(character) &&
                        character != '-' &&
                        character != '_' &&
                        character != '.'))
                {
                    return false;
                }
            }

            return true;
        }

        private UpdateCheckResult Result(
            UpdateStatus status,
            string message,
            PackagedUpdateCandidate? candidate = null)
        {
            return new UpdateCheckResult(
                Status: status,
                CurrentSha: string.Empty,
                RemoteSha: string.Empty,
                Branch: string.Empty,
                ConfiguredBranch: string.Empty,
                CommitsBehind: 0,
                CommitsAhead: 0,
                RepoRoot: string.Empty,
                ExecutablePath: Environment.ProcessPath ?? string.Empty,
                Message: message,
                Provider: UpdateProviderKind.PackagedRelease,
                CurrentVersion: _client.CurrentVersion,
                TargetVersion: candidate?.Version ?? string.Empty,
                Channel: _client.Channel,
                Source: _client.Source,
                Artifact: candidate?.Artifact ?? string.Empty);
        }

        private static bool IsPackagedInstall()
        {
            try
            {
                return VelopackLocator.IsCurrentSet &&
                    !string.IsNullOrWhiteSpace(VelopackLocator.Current.AppId) &&
                    VelopackLocator.Current.CurrentlyInstalledVersion != null &&
                    File.Exists(VelopackLocator.Current.UpdateExePath);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException ||
                ex is IOException ||
                ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal sealed class UnavailablePackagedUpdateService : IUpdateService
    {
        private readonly string _message;

        public UnavailablePackagedUpdateService(string message)
        {
            _message = message;
        }

        public UpdateProviderKind ProviderKind =>
            UpdateProviderKind.PackagedRelease;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(
            Core.Models.UpdateOptions options,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateCheckResult(
                Status: UpdateStatus.NotEligible,
                CurrentSha: string.Empty,
                RemoteSha: string.Empty,
                Branch: string.Empty,
                ConfiguredBranch: string.Empty,
                CommitsBehind: 0,
                CommitsAhead: 0,
                RepoRoot: string.Empty,
                ExecutablePath: Environment.ProcessPath ?? string.Empty,
                Message: _message,
                Provider: UpdateProviderKind.PackagedRelease));
        }

        public Task<UpdateLaunchResult> LaunchUpdateAsync(
            UpdateCheckResult lastCheck,
            CancellationToken ct,
            IProgress<int>? progress = null)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateLaunchResult(
                false,
                string.Empty,
                _message));
        }
    }
}
