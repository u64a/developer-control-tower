using ControlTower.Core.Models;
using ControlTower.Infrastructure.Archive;

namespace ControlTower.Tests;

public class ArchiveStateStoreTests
{
    [Theory]
    [InlineData(ArchiveReleaseStatus.Captured, "")]
    [InlineData(ArchiveReleaseStatus.Incomplete, "Windows asset download failed (HTTP 503).")]
    public void SaveAndLoad_PreservesExplicitReleaseOutcomeAndCapturedAssetInventory(
        ArchiveReleaseStatus status,
        string error)
    {
        var root = Path.Combine(Path.GetTempPath(), "archive-state-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStateStore(root);
            var state = new ArchiveRepoState
            {
                ProjectId = "forgejo-project",
                ArchivePath = Path.Combine(root, "archive", "forgejo-project"),
                ReleaseStatus = status,
                LastReleaseTag = "v0.2.1",
                LastReleaseDirectoryName = "v0.2.1",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Unverifiable,
                LastReleaseCapturedUtc = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                LastReleaseError = error
            };
            foreach (var name in ForgejoReleaseFixture.AssetNames)
            {
                state.LastReleaseAssetNames.Add(name);
            }

            store.Save(state);
            var loaded = store.Load(state.ProjectId);

            Assert.Equal(status, loaded.ReleaseStatus);
            Assert.Equal(state.LastReleaseTag, loaded.LastReleaseTag);
            Assert.Equal(state.LastReleaseDirectoryName, loaded.LastReleaseDirectoryName);
            Assert.Equal(state.LastReleaseIntegrity, loaded.LastReleaseIntegrity);
            Assert.Equal(state.LastReleaseCapturedUtc, loaded.LastReleaseCapturedUtc);
            Assert.Equal(state.LastReleaseAssetNames, loaded.LastReleaseAssetNames);
            Assert.Equal(error, loaded.LastReleaseError);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_LegacyStateWithOldReleaseTag_DoesNotClaimCurrentReleaseWasChecked()
    {
        var root = Path.Combine(Path.GetTempPath(), "archive-state-legacy-" + Guid.NewGuid().ToString("N"));
        var stateFolder = Path.Combine(root, "archive-state");
        Directory.CreateDirectory(stateFolder);
        try
        {
            File.WriteAllLines(Path.Combine(stateFolder, "legacy.state"),
            [
                "ProjectId=legacy",
                "LastReleaseTag=v0.2.1",
                "LastReleaseIntegrity=Verified",
                "LastReleaseCapturedUtc=2026-06-02T00:00:00.0000000Z"
            ]);

            var loaded = new ArchiveStateStore(root).Load("legacy");

            Assert.Equal(ArchiveReleaseStatus.NotChecked, loaded.ReleaseStatus);
            Assert.Equal("v0.2.1", loaded.LastReleaseTag);
            Assert.Empty(loaded.LastReleaseDirectoryName);
            Assert.Empty(loaded.LastReleaseAssetNames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
