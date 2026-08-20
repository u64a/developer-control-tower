using ControlTower.Core.Models;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>
    /// One row of the portfolio tally strip: a repo state, how many projects
    /// are in it, and the plain label. <see cref="RepoState"/> is exposed so
    /// the shared StatusLampStyle (which binds RepoState) colours the lamp.
    /// </summary>
    public sealed class PortfolioTallyItem
    {
        public RepoState RepoState { get; set; }

        public int Count { get; set; }

        public string Label { get; set; } = string.Empty;

        /// <summary>True for the "All" chip (no state filter).</summary>
        public bool IsAll { get; set; }

        /// <summary>True when this chip is the active filter.</summary>
        public bool IsActive { get; set; }
    }
}
