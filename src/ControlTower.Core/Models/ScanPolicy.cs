namespace ControlTower.Core.Models
{
    /// <summary>
    /// How aggressively to obtain a repo's <see cref="RepoSnapshot"/> when
    /// composing a project overview. Keeps the portfolio lightweight and
    /// offline-tolerant: only an explicit, user-invoked scan is allowed to
    /// touch the network (SSH).
    /// </summary>
    public enum ScanPolicy
    {
        /// <summary>Use the last-known cached snapshot only; no git work.</summary>
        CacheOnly,

        /// <summary>
        /// Scan local clones only; never probe SSH. Used by the automatic,
        /// one-shot portfolio seed so startup never depends on network reach.
        /// </summary>
        LocalOnly,

        /// <summary>
        /// User-invoked full scan; may probe SSH-hosted repositories. The only
        /// policy permitted to perform network work.
        /// </summary>
        FullExplicit
    }
}
