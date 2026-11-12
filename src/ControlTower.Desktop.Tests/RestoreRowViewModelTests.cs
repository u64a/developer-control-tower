using ControlTower.Core.Models;
using ControlTower.Desktop.ViewModels;

namespace ControlTower.Desktop.Tests;

public sealed class RestoreRowViewModelTests
{
    [Fact]
    public void RowWithoutOrigin_BecomesSelectedOnceAValidUrlIsEntered()
    {
        var row = Row(RestoreClassification.MissingNeedsUrl, remoteUrl: "");
        Assert.False(row.IsSelectable);
        Assert.Equal("Needs URL", row.StateLabel);

        row.OriginUrl = "not a url";
        Assert.False(row.IsSelectable);
        Assert.True(row.IsUrlHintError);

        row.OriginUrl = "  git@github.com:org/repo.git  ";
        Assert.Equal("git@github.com:org/repo.git", row.OriginUrl);
        Assert.True(row.IsSelectable);
        Assert.True(row.IsSelected);
        Assert.False(row.IsUrlHintError);
    }

    [Fact]
    public void CredentialBearingUrl_DeselectsAndBlocksTheRow()
    {
        var row = Row(RestoreClassification.Missing, remoteUrl: "https://github.com/org/repo.git");
        row.IsSelected = true;

        row.OriginUrl = "https://user:token@github.com/org/repo.git";

        Assert.False(row.IsSelectable);
        Assert.False(row.IsSelected);
        Assert.True(row.IsUrlHintError);
    }

    [Fact]
    public void ConflictRow_IsNeverAutoSelected_AndRestoredRowCannotBeReselected()
    {
        var conflict = Row(RestoreClassification.ConflictNonEmpty, remoteUrl: "");
        conflict.OriginUrl = "https://github.com/org/repo.git";
        Assert.True(conflict.IsSelectable);
        Assert.False(conflict.IsSelected);
        Assert.Equal(RestoreAction.QuarantineAndClone, conflict.Action);

        var row = Row(RestoreClassification.Missing, remoteUrl: "https://github.com/org/repo.git");
        row.IsSelected = true;
        row.Apply(new RestoreRowUpdate("p", RestoreRowState.Failed, null, null, null, "CommandFailed", "boom"));
        Assert.True(row.IsSelectable);
        Assert.True(row.IsSelected);
        Assert.Equal("boom", row.ErrorMessage);

        row.Apply(new RestoreRowUpdate("p", RestoreRowState.Done, null, "Cloned.", null, null, null));
        Assert.False(row.IsSelectable);
        Assert.False(row.IsSelected);
    }

    private static RestoreRowViewModel Row(RestoreClassification classification, string remoteUrl) =>
        RestoreRowViewModel.FromCandidate(new RestoreCandidate(
            ProjectId: "p", ProjectName: "P", Slug: "p", ExpectedPath: @"C:\repos\p",
            RemoteUrl: remoteUrl, CanonicalRemoteUrl: remoteUrl,
            Classification: classification, Detail: string.Empty));
}
