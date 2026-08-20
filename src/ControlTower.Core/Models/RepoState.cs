namespace ControlTower.Core.Models
{
    /// <summary>
    /// Coarse, presentation-neutral classification of a project's repo truth,
    /// derived from <see cref="RepoSnapshot"/>. The plain-word label and lamp
    /// colour are a presentation concern (Desktop converters); this enum is
    /// the single source of that classification so the UI never re-derives
    /// state from raw git facts.
    /// </summary>
    public enum RepoState
    {
        /// <summary>No repo scan has happened yet.</summary>
        Unknown = 0,

        /// <summary>
        /// A local or SSH workspace was expected but the repo could not be
        /// read (missing path, broken SSH, git failure). Must surface
        /// visibly — never shown as a benign state.
        /// </summary>
        Unavailable,

        /// <summary>Intentionally hosted-only: no local clone exists by design.</summary>
        HostedOnly,

        /// <summary>Clean working tree, in sync with upstream.</summary>
        Clean,

        /// <summary>Clean tree with local commits not yet pushed.</summary>
        Ahead,

        /// <summary>Clean tree, behind upstream.</summary>
        Behind,

        /// <summary>Clean tree, both ahead of and behind upstream.</summary>
        Diverged,

        /// <summary>Uncommitted working-tree changes (not behind upstream).</summary>
        Uncommitted,

        /// <summary>
        /// Uncommitted changes combined with being behind/diverged from
        /// upstream — the highest-attention everyday state.
        /// </summary>
        Attention
    }
}
