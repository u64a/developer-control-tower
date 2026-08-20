using System;
using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    // RepoSnapshot is the canonical "repo truth" record. Public field names
    // (e.g. AheadBy/BehindBy/IsDirty) are stable surface used by the UI and
    // composers. Presentation-only strings derived from these values belong in
    // Composition/, not here.
    public sealed class RepoSnapshot
    {
        public RepoSnapshot()
        {
            RepoPath = string.Empty;
            Branch = string.Empty;
            StatusMessage = string.Empty;
            OriginUrl = string.Empty;
        }

        public string RepoPath { get; set; }

        public bool IsAvailable { get; set; }

        public string Branch { get; set; }

        public bool IsDirty { get; set; }

        public bool HasUpstream { get; set; }

        public int AheadBy { get; set; }

        public int BehindBy { get; set; }

        public DateTime? LastCommitUtc { get; set; }

        /// <summary>
        /// When this snapshot was produced by a scan (UTC). Null for snapshots
        /// from older caches. Lets the UI distinguish "last known" from
        /// "current truth" so stale cached state isn't shown as fresh.
        /// </summary>
        public DateTime? ScannedAtUtc { get; set; }

        public string StatusMessage { get; set; }

        public string OriginUrl { get; set; }
    }

    public sealed class ProjectOverview
    {
        private readonly List<PlanningNodeSummary> _planningNodes = new List<PlanningNodeSummary>();

        public ProjectOverview()
        {
            Id = string.Empty;
            StoreId = string.Empty;
            Folder = string.Empty;
            DisplayName = string.Empty;
            Summary = string.Empty;
            LifecycleState = string.Empty;
            Group = "Ungrouped";
            PlanningAuthority = string.Empty;
            SourcePath = string.Empty;
            WorkspaceMode = string.Empty;
            LocalPath = string.Empty;
            RepoLocation = string.Empty;
            SshTarget = string.Empty;
            RemoteUrl = string.Empty;
            ProductTitle = string.Empty;
            InitiativeSummary = string.Empty;
            PlanningSource = string.Empty;
            PlanningSummary = string.Empty;
            Branch = string.Empty;
            WorkingTreeStatus = string.Empty;
            SyncStatus = string.Empty;
            SyncCompact = string.Empty;
            ActivitySummary = string.Empty;
            RiskSummary = string.Empty;
            StatusLine = string.Empty;
            GitHubUrl = string.Empty;
            AdoUrl = string.Empty;
            VsCodePath = string.Empty;
            PrimaryDocPath = string.Empty;
            PlanningPath = string.Empty;
            PlanningAuthorityNote = string.Empty;
            ExternalSystemSummary = string.Empty;
            OriginUrl = string.Empty;
            RepoState = RepoState.Unknown;
        }

        public string Id { get; set; }

        /// <summary>Portfolio store reference (e.g. "local", "devbox"). Empty for v0 legacy entries.</summary>
        public string StoreId { get; set; }

        /// <summary>Effective folder name within the store. Empty for legacy path-only entries.</summary>
        public string Folder { get; set; }

        /// <summary>
        /// True when StoreId/Folder were derived at load-time from the project's SshTarget
        /// via SshStoreResolver (path-based remote-only entry). False for genuine portfolio
        /// store-backed entries, hybrid, local, and unresolved projects.
        /// </summary>
        public bool IsStoreIdentityDerived { get; set; }

        public string DisplayName { get; set; }

        public string Summary { get; set; }

        public string LifecycleState { get; set; }

        /// <summary>Organisational folder the project sits in; "Ungrouped" by default.</summary>
        public string Group { get; set; }

        public string PlanningAuthority { get; set; }

        public string SourcePath { get; set; }

        public string WorkspaceMode { get; set; }

        public string LocalPath { get; set; }

        /// <summary>
        /// Human-readable location of the actual repo for display (table row
        /// under the name). For local/hybrid repos this is the local clone
        /// path; for SSH/remote-only repos it is the remote working path
        /// (e.g. d:\repos\azcra) rather than the local config root.
        /// </summary>
        public string RepoLocation { get; set; }

        public string SshTarget { get; set; }

        public string RemoteUrl { get; set; }

        public string ProductTitle { get; set; }

        public string InitiativeSummary { get; set; }

        public string PlanningSource { get; set; }

        public string PlanningSummary { get; set; }

        public string Branch { get; set; }

        public string WorkingTreeStatus { get; set; }

        public string SyncStatus { get; set; }

        /// <summary>
        /// Compact sync readout for the dense table, e.g. "▲2  ▼3", "in sync",
        /// "no upstream", or "—" when no scan is available. <see cref="AheadBy"/>
        /// / <see cref="BehindBy"/> carry the raw counts for colouring.
        /// </summary>
        public string SyncCompact { get; set; }

        public int AheadBy { get; set; }

        public int BehindBy { get; set; }

        public string ActivitySummary { get; set; }

        public string RiskSummary { get; set; }

        public string StatusLine { get; set; }

        public string GitHubUrl { get; set; }

        public string AdoUrl { get; set; }

        public string VsCodePath { get; set; }

        public string PrimaryDocPath { get; set; }

        public string PlanningPath { get; set; }

        public string PlanningAuthorityNote { get; set; }

        public string ExternalSystemSummary { get; set; }

        public string OriginUrl { get; set; }

        /// <summary>
        /// Best single repo URL for display/copy in the rail: GitHub, else
        /// ADO, else the scanned origin. Empty when none is configured/known
        /// (sentinels like "Not configured"/"Unavailable" are treated as none).
        /// </summary>
        public string PrimaryRepoUrl
        {
            get
            {
                if (Real(GitHubUrl)) return GitHubUrl;
                if (Real(AdoUrl)) return AdoUrl;
                if (Real(OriginUrl)) return OriginUrl;
                return string.Empty;
            }
        }

        private static bool Real(string v)
        {
            return !string.IsNullOrWhiteSpace(v)
                && !string.Equals(v, "Not configured", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, "Unavailable", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, "Unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, "No remote configured", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Single coarse classification of repo truth for the status lamp /
        /// pill. Derived by <see cref="ControlTower.Core.Composition.RepoStateClassifier"/>;
        /// the UI binds this rather than re-deriving from git facts.
        /// </summary>
        public RepoState RepoState { get; set; }

        public IReadOnlyList<PlanningNodeSummary> PlanningNodes => _planningNodes;

        internal void AddPlanningNode(PlanningNodeSummary node) => _planningNodes.Add(node);
    }
}
