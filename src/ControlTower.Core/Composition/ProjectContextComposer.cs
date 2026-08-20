using System.Collections.Generic;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Core.Composition
{
    /// <summary>
    /// Pure view composer over already-loaded project inputs. Implements
    /// ADR-003: business logic lives here (no IO); <see cref="UseCases.ControlTowerService"/>
    /// only orchestrates providers and hands the results to this composer.
    /// <para>
    /// Also consumes <see cref="AuthorityGate"/> (ADR-002): when the gate
    /// returns <see cref="AuthorityState.AdoAuthoritative"/>, planning summary
    /// rendering derived from <c>product-map.yml</c> is suppressed and any
    /// <c>authority/mismatch</c> issues are surfaced to the caller.
    /// </para>
    /// </summary>
    public static class ProjectContextComposer
    {
        public static ProjectOverview Compose(
            ProjectDefinition project,
            ProductMapSummary productMap,
            PlanningBoardSummary planningBoard,
            RepoSnapshot snapshot,
            IEnumerable<ValidationIssue> issues)
        {
            var merged = new List<ValidationIssue>();
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    merged.Add(issue);
                }
            }

            var authority = AuthorityGate.Evaluate(project, productMap);
            foreach (var authorityIssue in authority.Issues)
            {
                merged.Add(authorityIssue);
            }

            // ADR-002: never render product-map / planning-board as if it were
            // authoritative when planning authority is Azure DevOps.
            var effectiveProductMap = authority.SuppressPlanningSummary ? null : productMap;
            var effectivePlanningBoard = authority.SuppressPlanningSummary ? null : planningBoard;

            return OverviewComposer.Compose(project, effectiveProductMap, effectivePlanningBoard, snapshot, merged);
        }
    }
}
