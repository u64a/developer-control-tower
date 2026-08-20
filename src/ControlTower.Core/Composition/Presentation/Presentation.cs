// Composition/Presentation/
// =========================
//
// This folder is reserved for the *presentation* boundary of the Control
// Tower core: types and helpers whose only role is to shape data for the
// WPF UI (status strings, summary lines, V0-only board-style views over
// product-map.yml).
//
// Why it exists (ADR-001, finding L4):
//   - The durable domain (ProjectDefinition, ProductMap*, RepoSnapshot) is
//     about what the project IS.
//   - The presentation layer is about how a project LOOKS in V0.
//   - Keeping them mixed in Models/ leaks UI vocabulary into the domain
//     and makes ADR-001's "PlanningBoard* is a deprecated facade" hard to
//     enforce.
//
// Migration plan:
//   - In this remediation pass we do NOT relocate existing types
//     (PlanningBoardSummary, PlanningNodeSummary, PlanningBoardLoadResult).
//     The WPF UI binds to their current namespace and moving them now is
//     out of scope. Each type carries an XML <remarks> tag flagging it as
//     V0-only (see ADR-001).
//   - New presentation-only types added going forward SHOULD live here
//     under ControlTower.Core.Composition.Presentation.
//   - When V1 introduces a non-product-map planning view, the V0 facade
//     types are deleted, not generalised.
//
// Anti-goals:
//   - No IO in this namespace.
//   - No new domain models. If a type represents durable intent, it
//     belongs in ControlTower.Core.Models.

namespace ControlTower.Core.Composition.Presentation
{
    /// <summary>
    /// Marker for the V0 presentation namespace. Intentionally empty. See
    /// the file-level comment and ADR-001 for the boundary contract.
    /// </summary>
    internal static class PresentationBoundary
    {
    }
}
