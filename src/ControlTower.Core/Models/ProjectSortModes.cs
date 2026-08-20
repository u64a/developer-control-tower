namespace ControlTower.Core.Models
{
    /// <summary>
    /// Canonical sort-mode labels for the portfolio list. The default
    /// (<see cref="Default"/>) is alphabetical by display name — the calmest
    /// truth-first ordering that doesn't require background data to be
    /// meaningful (see spec §1).
    /// </summary>
    public static class ProjectSortModes
    {
        public const string Name = "Name";
        public const string NeedsAttention = "Needs attention first";
        public const string RecentActivity = "Recent activity";

        public const string Default = Name;
    }
}
