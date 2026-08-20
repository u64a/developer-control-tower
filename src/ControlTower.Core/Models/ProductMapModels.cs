using System.Collections.Generic;
using ControlTower.Core.Validation;

// Models in this file split into two concerns:
//  - Durable domain (ProductNode, ProductMapSummary, ProductMapLoadResult) —
//    direct read of product-map.yml.
//  - V0-only presentation facade (PlanningNodeSummary, PlanningBoardSummary,
//    PlanningBoardLoadResult) — kept here for V0 UI binding stability per
//    ADR-001. See Composition/Presentation/Presentation.cs for the long-term
//    boundary.
namespace ControlTower.Core.Models
{
    public sealed class ProductNode
    {
        public ProductNode()
        {
            Id = string.Empty;
            Type = string.Empty;
            Title = string.Empty;
            ParentId = string.Empty;
            Status = string.Empty;
            Description = string.Empty;
            ExternalSystem = string.Empty;
            ExternalId = string.Empty;
            ExternalUrl = string.Empty;
        }

        public string Id { get; set; }

        public string Type { get; set; }

        public string Title { get; set; }

        public string ParentId { get; set; }

        public string Status { get; set; }

        public string Description { get; set; }

        public string ExternalSystem { get; set; }

        public string ExternalId { get; set; }

        public string ExternalUrl { get; set; }
    }

    public sealed class ProductMapSummary
    {
        public ProductMapSummary()
        {
            ProjectId = string.Empty;
            PlanningAuthority = "repo";
            ProductTitle = string.Empty;
            TopLevelInitiatives = new List<string>();
        }

        public string ProjectId { get; set; }

        public string PlanningAuthority { get; set; }

        public string ProductTitle { get; set; }

        public IList<string> TopLevelInitiatives { get; private set; }
    }

    public sealed class ProductMapLoadResult
    {
        public ProductMapLoadResult()
        {
            Summary = new ProductMapSummary();
            Issues = new List<ValidationIssue>();
        }

        public ProductMapSummary Summary { get; set; }

        public IList<ValidationIssue> Issues { get; private set; }
    }

    /// <summary>
    /// V0-only thin view over <c>product-map.yml</c>. This is not a planning
    /// system of record; it exists solely to render durable product intent.
    /// Do not extend. See ADR-001.
    /// </summary>
    /// <remarks>Deprecated for any post-V0 use: "V0-only thin view over product-map; do not extend. See ADR-001."</remarks>
    public sealed class PlanningNodeSummary
    {
        public PlanningNodeSummary()
        {
            Id = string.Empty;
            Title = string.Empty;
            Status = string.Empty;
            Subtitle = string.Empty;
            Children = new List<PlanningNodeSummary>();
        }

        public string Id { get; set; }

        public string Title { get; set; }

        public string Status { get; set; }

        public string Subtitle { get; set; }

        public IList<PlanningNodeSummary> Children { get; private set; }
    }

    /// <summary>
    /// V0-only thin view over <c>product-map.yml</c>. This is not a planning
    /// system of record; it exists solely to render durable product intent.
    /// Do not extend. See ADR-001.
    /// </summary>
    /// <remarks>Deprecated for any post-V0 use: "V0-only thin view over product-map; do not extend. See ADR-001."</remarks>
    public sealed class PlanningBoardSummary
    {
        public PlanningBoardSummary()
        {
            Source = string.Empty;
            Title = string.Empty;
            Summary = string.Empty;
            Nodes = new List<PlanningNodeSummary>();
        }

        public string Source { get; set; }

        public string Title { get; set; }

        public string Summary { get; set; }

        public IList<PlanningNodeSummary> Nodes { get; private set; }
    }

    /// <summary>
    /// V0-only thin view over <c>product-map.yml</c>. This is not a planning
    /// system of record; it exists solely to render durable product intent.
    /// Do not extend. See ADR-001.
    /// </summary>
    /// <remarks>Deprecated for any post-V0 use: "V0-only thin view over product-map; do not extend. See ADR-001."</remarks>
    public sealed class PlanningBoardLoadResult
    {
        public PlanningBoardLoadResult()
        {
            Summary = new PlanningBoardSummary();
            Issues = new List<ValidationIssue>();
        }

        public PlanningBoardSummary Summary { get; set; }

        public IList<ValidationIssue> Issues { get; private set; }
    }
}
