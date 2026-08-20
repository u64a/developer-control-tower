using ControlTower.Core.Models;

namespace ControlTower.Core.Composition
{
    /// <summary>
    /// Pure classifier mapping a <see cref="RepoSnapshot"/> (plus whether a
    /// local/SSH workspace was expected) onto a single <see cref="RepoState"/>.
    /// Precedence is resolved here so the UI binds one value rather than
    /// re-deriving state from raw git facts. No IO, no presentation strings.
    /// </summary>
    public static class RepoStateClassifier
    {
        /// <param name="snapshot">Repo truth, or null when no scan has run.</param>
        /// <param name="workspaceExpectsLocalRepo">
        /// True when the project declares a local or SSH path. Used to tell an
        /// intentional hosted-only project apart from a broken/missing local
        /// workspace.
        /// </param>
        public static RepoState Classify(RepoSnapshot snapshot, bool workspaceExpectsLocalRepo, bool isHostedOnly = false)
        {
            if (snapshot == null)
            {
                // No scan yet. A hosted-only project (a remote URL but no local
                // or SSH workspace) is knowable from metadata alone — surface it
                // rather than a blank Unknown.
                return isHostedOnly ? RepoState.HostedOnly : RepoState.Unknown;
            }

            if (!snapshot.IsAvailable)
            {
                // A workspace we expected to read but couldn't must fail
                // visibly; only a project with no local/SSH path is benign
                // hosted-only.
                return workspaceExpectsLocalRepo ? RepoState.Unavailable : RepoState.HostedOnly;
            }

            var ahead = snapshot.AheadBy > 0;
            var behind = snapshot.BehindBy > 0;

            if (snapshot.IsDirty)
            {
                // Dirty + behind/diverged is the genuine risk; dirty alone is
                // ordinary work-in-progress.
                return behind ? RepoState.Attention : RepoState.Uncommitted;
            }

            if (ahead && behind)
            {
                return RepoState.Diverged;
            }

            if (behind)
            {
                return RepoState.Behind;
            }

            if (ahead)
            {
                return RepoState.Ahead;
            }

            return RepoState.Clean;
        }
    }
}
