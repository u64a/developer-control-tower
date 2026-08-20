using System;
using System.Collections.Generic;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Core.Composition
{
    /// <summary>
    /// Planning authority states derived from <c>project.yml</c>'s
    /// <c>planning.authority</c> field cross-checked against the loaded
    /// <c>product-map.yml</c>. See ADR-002.
    /// </summary>
    public enum AuthorityState
    {
        RepoAuthoritative,
        AdoAuthoritative,
        GithubAuthoritative,
        Mismatch
    }

    /// <summary>
    /// Result of evaluating planning authority for a project. Pure value type.
    /// </summary>
    public sealed class AuthorityEvaluation
    {
        public AuthorityEvaluation(AuthorityState state, IReadOnlyList<ValidationIssue> issues)
        {
            State = state;
            Issues = issues ?? Array.Empty<ValidationIssue>();
        }

        public AuthorityState State { get; }

        public IReadOnlyList<ValidationIssue> Issues { get; }

        /// <summary>
        /// True when planning summary rendering should be suppressed because
        /// the authoritative source is an external system (currently: ADO).
        /// See ADR-002.
        /// </summary>
        public bool SuppressPlanningSummary => State == AuthorityState.AdoAuthoritative;
    }

    /// <summary>
    /// Pure decision function that resolves planning authority from a
    /// <see cref="ProjectDefinition"/> and the (possibly empty) loaded
    /// <see cref="ProductMapSummary"/>. Encodes ADR-002:
    /// <list type="bullet">
    /// <item><description><c>planning.authority = repo</c> → <see cref="AuthorityState.RepoAuthoritative"/>.</description></item>
    /// <item><description><c>planning.authority = ado</c> → <see cref="AuthorityState.AdoAuthoritative"/>; emits <c>authority/mismatch</c> warning if a populated <c>product-map.yml</c> is present.</description></item>
    /// <item><description><c>planning.authority = github</c> → <see cref="AuthorityState.GithubAuthoritative"/>; emits <c>authority/mismatch</c> warning if a populated <c>product-map.yml</c> is present.</description></item>
    /// <item><description>Conflicting authority declarations (project vs product-map) → <see cref="AuthorityState.Mismatch"/>.</description></item>
    /// </list>
    /// No IO. Safe to unit test directly.
    /// </summary>
    public static class AuthorityGate
    {
        public const string AuthorityMismatchCode = "authority/mismatch";

        public static AuthorityEvaluation Evaluate(ProjectDefinition project, ProductMapSummary productMap)
        {
            var issues = new List<ValidationIssue>();
            var declared = NormalizeAuthority(project?.Planning?.Authority);
            var productMapAuthority = NormalizeAuthority(productMap?.PlanningAuthority);
            var productMapHasContent = productMap != null && productMap.TopLevelInitiatives != null && productMap.TopLevelInitiatives.Count > 0;

            // Conflict: product-map advertises a different authority than project.yml.
            if (productMap != null &&
                !string.IsNullOrEmpty(productMapAuthority) &&
                !string.IsNullOrEmpty(declared) &&
                !string.Equals(productMapAuthority, declared, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(
                    IssueSeverity.Warning,
                    AuthorityMismatchCode,
                    "project.yml declares planning authority '" + declared +
                    "' but product-map.yml declares '" + productMapAuthority + "'."));
                return new AuthorityEvaluation(AuthorityState.Mismatch, issues);
            }

            if (string.Equals(declared, "ado", StringComparison.OrdinalIgnoreCase))
            {
                if (productMapHasContent)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Warning,
                        AuthorityMismatchCode,
                        "Planning authority is Azure DevOps but a populated product-map.yml is present. " +
                        "product-map.yml is treated as non-authoritative."));
                }
                return new AuthorityEvaluation(AuthorityState.AdoAuthoritative, issues);
            }

            if (string.Equals(declared, "github", StringComparison.OrdinalIgnoreCase))
            {
                if (productMapHasContent)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Warning,
                        AuthorityMismatchCode,
                        "Planning authority is GitHub but a populated product-map.yml is present. " +
                        "product-map.yml is treated as non-authoritative."));
                }
                return new AuthorityEvaluation(AuthorityState.GithubAuthoritative, issues);
            }

            // Default and explicit "repo" both map to RepoAuthoritative.
            return new AuthorityEvaluation(AuthorityState.RepoAuthoritative, issues);
        }

        private static string NormalizeAuthority(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().ToLowerInvariant();
        }
    }
}
