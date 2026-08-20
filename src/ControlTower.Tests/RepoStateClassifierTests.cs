using ControlTower.Core.Composition;
using ControlTower.Core.Models;

namespace ControlTower.Tests;

public class RepoStateClassifierTests
{
    private static RepoSnapshot Snapshot(
        bool available = true,
        bool dirty = false,
        int ahead = 0,
        int behind = 0,
        bool hasUpstream = true)
    {
        return new RepoSnapshot
        {
            IsAvailable = available,
            IsDirty = dirty,
            AheadBy = ahead,
            BehindBy = behind,
            HasUpstream = hasUpstream,
            Branch = "main"
        };
    }

    [Fact]
    public void NullSnapshot_IsUnknown()
    {
        Assert.Equal(RepoState.Unknown, RepoStateClassifier.Classify(null, workspaceExpectsLocalRepo: true));
    }

    [Fact]
    public void NullSnapshot_HostedOnlyIntent_IsHostedOnly()
    {
        var state = RepoStateClassifier.Classify(null, workspaceExpectsLocalRepo: false, isHostedOnly: true);
        Assert.Equal(RepoState.HostedOnly, state);
    }

    [Fact]
    public void Unavailable_WithLocalWorkspace_FailsVisiblyAsUnavailable()
    {
        var state = RepoStateClassifier.Classify(Snapshot(available: false), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Unavailable, state);
    }

    [Fact]
    public void Unavailable_WithoutLocalWorkspace_IsHostedOnly()
    {
        var state = RepoStateClassifier.Classify(Snapshot(available: false), workspaceExpectsLocalRepo: false);
        Assert.Equal(RepoState.HostedOnly, state);
    }

    [Fact]
    public void Clean_InSync_IsClean()
    {
        Assert.Equal(RepoState.Clean, RepoStateClassifier.Classify(Snapshot(), workspaceExpectsLocalRepo: true));
    }

    [Fact]
    public void Clean_AheadOnly_IsAhead()
    {
        var state = RepoStateClassifier.Classify(Snapshot(ahead: 2), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Ahead, state);
    }

    [Fact]
    public void Clean_BehindOnly_IsBehind()
    {
        var state = RepoStateClassifier.Classify(Snapshot(behind: 3), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Behind, state);
    }

    [Fact]
    public void Clean_AheadAndBehind_IsDiverged()
    {
        var state = RepoStateClassifier.Classify(Snapshot(ahead: 1, behind: 4), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Diverged, state);
    }

    [Fact]
    public void Dirty_NotBehind_IsUncommitted()
    {
        var state = RepoStateClassifier.Classify(Snapshot(dirty: true), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Uncommitted, state);
    }

    [Fact]
    public void Dirty_AheadOnly_IsUncommitted()
    {
        var state = RepoStateClassifier.Classify(Snapshot(dirty: true, ahead: 2), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Uncommitted, state);
    }

    [Fact]
    public void Dirty_AndBehind_IsAttention()
    {
        var state = RepoStateClassifier.Classify(Snapshot(dirty: true, behind: 1), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Attention, state);
    }

    [Fact]
    public void Dirty_AndDiverged_IsAttention()
    {
        var state = RepoStateClassifier.Classify(Snapshot(dirty: true, ahead: 2, behind: 2), workspaceExpectsLocalRepo: true);
        Assert.Equal(RepoState.Attention, state);
    }
}
