using System.Reflection;
using System.Reflection.Emit;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Update;

namespace ControlTower.Tests;

public sealed class VelopackUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdates_ReturnsCurrentPackagedReleaseState()
    {
        var client = new FakePackagedUpdateClient
        {
            CurrentVersion = "0.10.0-preview.1",
            Channel = "win-x64",
            Source = "https://github.com/example/app"
        };
        var service = new VelopackUpdateService(client);

        var result = await service.CheckForUpdatesAsync(
            UpdateOptions.Defaults(),
            CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal(UpdateProviderKind.PackagedRelease, result.Provider);
        Assert.Equal("0.10.0-preview.1", result.CurrentVersion);
        Assert.Equal("win-x64", result.Channel);
        Assert.Equal("https://github.com/example/app", result.Source);
    }

    [Fact]
    public async Task LaunchUpdate_DownloadsReportedCandidateAndAppliesIt()
    {
        var candidate = new PackagedUpdateCandidate(
            "0.10.0-preview.2",
            "u64a.DeveloperControlTower-0.10.0-preview.2-full.nupkg",
            new object());
        var client = new FakePackagedUpdateClient
        {
            Candidate = candidate
        };
        var service = new VelopackUpdateService(client);
        var check = await service.CheckForUpdatesAsync(
            UpdateOptions.Defaults(),
            CancellationToken.None);
        var progress = new CapturingProgress();

        var launch = await service.LaunchUpdateAsync(
            check,
            CancellationToken.None,
            progress);

        Assert.True(launch.Spawned);
        Assert.Same(candidate, client.DownloadedCandidate);
        Assert.Same(candidate, client.AppliedCandidate);
        Assert.Equal(64, progress.LastValue);
        Assert.Equal("0.10.0-preview.2", check.TargetVersion);
        Assert.Equal(candidate.Artifact, check.Artifact);
    }

    [Fact]
    public async Task LaunchUpdate_RejectsStaleCheckResult()
    {
        var client = new FakePackagedUpdateClient
        {
            Candidate = new PackagedUpdateCandidate(
                "0.10.0-preview.2",
                "package.nupkg",
                new object())
        };
        var service = new VelopackUpdateService(client);
        var check = await service.CheckForUpdatesAsync(
            UpdateOptions.Defaults(),
            CancellationToken.None);
        var stale = check with { TargetVersion = "0.10.0-preview.1" };

        var launch = await service.LaunchUpdateAsync(
            stale,
            CancellationToken.None);

        Assert.False(launch.Spawned);
        Assert.Null(client.DownloadedCandidate);
        Assert.Null(client.AppliedCandidate);
        Assert.Contains("Check for updates again", launch.Message);
    }

    [Fact]
    public async Task CheckForUpdates_MapsProviderFailureToVisibleStatus()
    {
        var client = new FakePackagedUpdateClient
        {
            CheckException = new InvalidOperationException("feed unavailable")
        };
        var service = new VelopackUpdateService(client);

        var result = await service.CheckForUpdatesAsync(
            UpdateOptions.Defaults(),
            CancellationToken.None);

        Assert.Equal(UpdateStatus.FetchFailed, result.Status);
        Assert.Contains("network connection", result.Message);
    }

    [Fact]
    public async Task CheckForUpdates_RejectsUninstalledClient()
    {
        var client = new FakePackagedUpdateClient
        {
            IsInstalled = false
        };
        var service = new VelopackUpdateService(client);

        var result = await service.CheckForUpdatesAsync(
            UpdateOptions.Defaults(),
            CancellationToken.None);

        Assert.Equal(UpdateStatus.NotEligible, result.Status);
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public void TryReadConfiguration_AcceptsOnlyCanonicalGitHubRepositoryUrl()
    {
        var validAssembly = BuildAssembly(
            "https://github.com/u64a/developer-control-tower/",
            "true");
        var invalidAssembly = BuildAssembly(
            "https://github.com/u64a/developer-control-tower/releases",
            "true");

        var valid = VelopackUpdateService.TryReadConfiguration(
            validAssembly,
            out var repositoryUrl,
            out var includePrereleases);
        var invalid = VelopackUpdateService.TryReadConfiguration(
            invalidAssembly,
            out _,
            out _);

        Assert.True(valid);
        Assert.Equal(
            "https://github.com/u64a/developer-control-tower",
            repositoryUrl);
        Assert.True(includePrereleases);
        Assert.False(invalid);
    }

    [Fact]
    public async Task UnavailablePackagedProvider_DoesNotFallBackToSourceUpdates()
    {
        var service = new UnavailablePackagedUpdateService(
            "Package metadata is invalid.");

        var result = await service.CheckForUpdatesAsync(
            UpdateOptions.Defaults(),
            CancellationToken.None);
        var launch = await service.LaunchUpdateAsync(
            result,
            CancellationToken.None);

        Assert.Equal(UpdateProviderKind.PackagedRelease, service.ProviderKind);
        Assert.Equal(UpdateProviderKind.PackagedRelease, result.Provider);
        Assert.Equal(UpdateStatus.NotEligible, result.Status);
        Assert.False(launch.Spawned);
    }

    private static Assembly BuildAssembly(string repositoryUrl, string prerelease)
    {
        var name = new AssemblyName("VelopackConfigTest-" + Guid.NewGuid().ToString("N"));
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            name,
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor(
            new[] { typeof(string), typeof(string) })!;

        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            new object[] { "UpdateRepositoryUrl", repositoryUrl }));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            new object[] { "UpdateIncludePrereleases", prerelease }));
        return assembly;
    }

    private sealed class CapturingProgress : IProgress<int>
    {
        public int LastValue { get; private set; }

        public void Report(int value)
        {
            LastValue = value;
        }
    }

    private sealed class FakePackagedUpdateClient : IPackagedUpdateClient
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion { get; set; } = "0.10.0-preview.1";
        public string Channel { get; set; } = "win-x64";
        public string Source { get; set; } = "https://github.com/example/app";
        public PackagedUpdateCandidate? Candidate { get; set; }
        public Exception? CheckException { get; set; }
        public int CheckCalls { get; private set; }
        public PackagedUpdateCandidate? DownloadedCandidate { get; private set; }
        public PackagedUpdateCandidate? AppliedCandidate { get; private set; }

        public Task<PackagedUpdateCandidate?> CheckForUpdatesAsync(CancellationToken ct)
        {
            CheckCalls++;
            if (CheckException != null)
            {
                throw CheckException;
            }

            return Task.FromResult(Candidate);
        }

        public Task DownloadAsync(
            PackagedUpdateCandidate candidate,
            Action<int> progress,
            CancellationToken ct)
        {
            DownloadedCandidate = candidate;
            progress(64);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(PackagedUpdateCandidate candidate)
        {
            AppliedCandidate = candidate;
        }
    }
}
