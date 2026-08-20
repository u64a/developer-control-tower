using System.Collections.Generic;
using ControlTower.Core.Validation;

namespace ControlTower.Core.Models
{
    public sealed class PortfolioIndex
    {
        public PortfolioIndex()
        {
            Projects = new List<ProjectRef>();
            Issues = new List<ValidationIssue>();
        }

        public IList<ProjectRef> Projects { get; private set; }

        /// <summary>
        /// Validation issues raised while loading the portfolio (e.g. malformed
        /// YAML, schema errors). Empty when the portfolio loaded cleanly.
        /// </summary>
        public IList<ValidationIssue> Issues { get; private set; }
    }

    public sealed class ProjectRef
    {
        public ProjectRef()
        {
            Id = string.Empty;
            Path = string.Empty;
            StoreId = string.Empty;
            Folder = string.Empty;
            RemoteUrl = string.Empty;
        }

        public string Id { get; set; }

        /// <summary>Resolved filesystem path. May be empty if store resolution failed.</summary>
        public string Path { get; set; }

        /// <summary>Store reference (e.g. "local", "devbox"). Empty for v0 entries.</summary>
        public string StoreId { get; set; }

        /// <summary>Override folder name within the store. Defaults to Id if empty.</summary>
        public string Folder { get; set; }

        /// <summary>
        /// Cached git origin URL for this project. Persisted in portfolio.yml
        /// so the Restore-from-Git flow can re-clone projects whose local
        /// folder has disappeared (laptop rebuild scenario). Updated whenever
        /// the scanner discovers a non-credential-bearing origin URL from a
        /// live working tree. Never written from <c>git remote</c> output that
        /// contains embedded credentials.
        /// </summary>
        public string RemoteUrl { get; set; }

        /// <summary>True if this entry uses a store reference (v1), false if explicit path (v0).</summary>
        public bool UsesStore => !string.IsNullOrWhiteSpace(StoreId);
    }
}
