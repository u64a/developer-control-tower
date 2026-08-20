using System;
using System.Collections.Generic;
using System.Linq;
using ControlTower.Core.Models;
using ControlTower.Core.Time;
using ControlTower.Core.Validation;

namespace ControlTower.Core.Composition
{
    public static class OverviewComposer
    {
        public static ProjectOverview Compose(ProjectDefinition project, ProductMapSummary productMap, PlanningBoardSummary planningBoard, RepoSnapshot snapshot, IEnumerable<ValidationIssue> issues)
        {
            return Compose(project, productMap, planningBoard, snapshot, issues, null);
        }

        public static ProjectOverview Compose(ProjectDefinition project, ProductMapSummary productMap, PlanningBoardSummary planningBoard, RepoSnapshot snapshot, IEnumerable<ValidationIssue> issues, IClock clock)
        {
            var nowUtc = (clock ?? SystemClock.Instance).UtcNow;
            var overview = new ProjectOverview();
            var issueList = issues == null ? new List<ValidationIssue>() : issues.ToList();

            overview.Id = project.Id;
            overview.DisplayName = ValueOr(project.DisplayName, project.Id, "Unnamed project");
            overview.Summary = ValueOr(project.Summary, "No summary provided.");
            overview.LifecycleState = ValueOr(project.LifecycleState, "unknown");
            overview.Group = ValueOr(project.Group, "Ungrouped");
            overview.PlanningAuthority = project.Planning == null ? "unknown" : ValueOr(project.Planning.Authority, "unknown");
            overview.SourcePath = ValueOr(project.ProjectRootPath, string.Empty);
            overview.WorkspaceMode = ResolveWorkspaceMode(project);
            overview.LocalPath = ValueOr(project.Locations == null ? string.Empty : project.Locations.LocalPath, "Not available");
            overview.RepoLocation = ValueOr(ResolveRepoLocation(project, snapshot), overview.LocalPath);
            overview.SshTarget = project.Locations == null ? "Not configured" : ValueOr(project.Locations.SshTarget, "Not configured");
            overview.RemoteUrl = project.Locations == null ? "Not configured" : ValueOr(project.Locations.RemoteUrl, "Not configured");
            overview.GitHubUrl = OriginUrlResolver.ResolveGitHubUrl(project, snapshot);
            overview.AdoUrl = OriginUrlResolver.ResolveAdoUrl(project, snapshot);
            overview.VsCodePath = project.Launch == null ? string.Empty : project.Launch.VsCodeLocal;
            overview.PrimaryDocPath = ResolvePrimaryDoc(project);
            overview.PlanningPath = ResolvePlanningPath(project);
            overview.PlanningAuthorityNote = BuildPlanningAuthorityNote(project);
            overview.ExternalSystemSummary = BuildExternalSystemSummary(project);

            if (planningBoard != null && planningBoard.Nodes.Count > 0)
            {
                overview.ProductTitle = ValueOr(planningBoard.Title, "No planning board");
                overview.InitiativeSummary = string.Empty;
                overview.PlanningSource = ValueOr(planningBoard.Source, "roadmap");
                overview.PlanningSummary = ValueOr(planningBoard.Summary, "Planning board available");

                foreach (var node in planningBoard.Nodes)
                {
                    overview.AddPlanningNode(node);
                }
            }
            else if (productMap != null)
            {
                overview.ProductTitle = ValueOr(productMap.ProductTitle, "No product map");
                overview.InitiativeSummary = productMap.TopLevelInitiatives.Count == 0
                    ? "No initiatives mapped yet."
                    : string.Join(", ", productMap.TopLevelInitiatives);
                overview.PlanningSource = "product-map.yml";
                overview.PlanningSummary = overview.InitiativeSummary;
                foreach (var initiative in productMap.TopLevelInitiatives)
                {
                    overview.AddPlanningNode(new PlanningNodeSummary
                    {
                        Title = initiative,
                        Subtitle = "Initiative"
                    });
                }
            }
            else
            {
                overview.ProductTitle = "No product map";
                overview.InitiativeSummary = string.Empty;
                overview.PlanningSource = "None";
                overview.PlanningSummary = "No planning board available";
            }

            if (snapshot != null && snapshot.IsAvailable)
            {
                overview.Branch = ValueOr(snapshot.Branch, "unknown");
                overview.WorkingTreeStatus = snapshot.IsDirty ? "Dirty" : "Clean";
                overview.SyncStatus = FormatAheadBehind(snapshot);
                overview.SyncCompact = FormatSyncCompact(snapshot);
                overview.AheadBy = snapshot.AheadBy;
                overview.BehindBy = snapshot.BehindBy;
                overview.ActivitySummary = FormatActivity(snapshot.LastCommitUtc, nowUtc);
                overview.RiskSummary = FormatRepoState(snapshot, nowUtc);
                overview.OriginUrl = ValueOr(snapshot.OriginUrl, "No remote configured");
            }
            else if (snapshot != null)
            {
                overview.Branch = "Unavailable";
                overview.WorkingTreeStatus = "Unavailable";
                overview.SyncStatus = string.IsNullOrWhiteSpace(snapshot.StatusMessage) ? "Repo unavailable" : snapshot.StatusMessage;
                overview.SyncCompact = "—";
                overview.ActivitySummary = "No recent repo signal";
                overview.RiskSummary = "Repo unavailable";
                overview.OriginUrl = "Unavailable";
            }
            else
            {
                overview.Branch = "Unknown";
                overview.WorkingTreeStatus = "Unknown";
                overview.SyncStatus = "No repo scan yet";
                overview.SyncCompact = "—";
                overview.ActivitySummary = "No recent repo signal";
                overview.RiskSummary = "Needs refresh";
                overview.OriginUrl = "Unknown";
            }

            overview.StatusLine = BuildRepoStateLine(issueList, snapshot);
            overview.RepoState = RepoStateClassifier.Classify(
                snapshot, WorkspaceExpectsLocalRepo(project), IsHostedOnlyIntent(project));
            return overview;
        }

        private static bool IsHostedOnlyIntent(ProjectDefinition project)
        {
            if (project == null || project.Locations == null)
            {
                return false;
            }

            var hasLocal = !string.IsNullOrWhiteSpace(project.Locations.LocalPath);
            var hasSsh = !string.IsNullOrWhiteSpace(project.Locations.SshTarget);
            var hasRemote = !string.IsNullOrWhiteSpace(project.Locations.RemoteUrl);
            return !hasLocal && !hasSsh && hasRemote;
        }

        private static bool WorkspaceExpectsLocalRepo(ProjectDefinition project)
        {
            return project != null &&
                   project.Locations != null &&
                   (!string.IsNullOrWhiteSpace(project.Locations.LocalPath) ||
                    !string.IsNullOrWhiteSpace(project.Locations.SshTarget));
        }

        // DISPLAY path: where the repo actually lives, for the table row under
        // the project name. Unlike the logical LocalPath, an SSH/remote-only
        // repo shows its remote working path (e.g. d:\repos\azcra) rather than
        // the local .controltower config root.
        private static string ResolveRepoLocation(ProjectDefinition project, RepoSnapshot snapshot)
        {
            var locations = project == null ? null : project.Locations;
            var hasLocal = locations != null && !string.IsNullOrWhiteSpace(locations.LocalPath);
            var hasSsh = locations != null && !string.IsNullOrWhiteSpace(locations.SshTarget);

            if (hasLocal)
            {
                return locations.LocalPath;
            }

            // SSH/remote-only repo: show the remote path, not the config root.
            if (hasSsh)
            {
                return ExtractSshRemotePath(locations.SshTarget);
            }

            if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.RepoPath))
            {
                return snapshot.RepoPath;
            }

            return project == null ? string.Empty : project.ProjectRootPath;
        }

        // Extracts the remote working path from an SSH target of the form
        // "[user@]host:remotePath" (e.g. "devuser@host:d:\repos\azcra" =>
        // "d:\repos\azcra"). The first colon separates host from path; a bare
        // drive-letter colon is not treated as the separator because the host
        // portion is always longer than one character.
        private static string ExtractSshRemotePath(string sshTarget)
        {
            if (string.IsNullOrWhiteSpace(sshTarget))
            {
                return string.Empty;
            }

            var sep = sshTarget.IndexOf(':');
            if (sep > 1 && sep < sshTarget.Length - 1)
            {
                return sshTarget.Substring(sep + 1).Trim();
            }

            return sshTarget.Trim();
        }

        private static string ResolvePrimaryDoc(ProjectDefinition project)
        {
            if (project == null || project.Docs == null || project.Docs.Count == 0)
            {
                return string.Empty;
            }

            return project.Docs[0].Url ?? string.Empty;
        }

        private static string ResolvePlanningPath(ProjectDefinition project)
        {
            if (project == null)
            {
                return string.Empty;
            }

            return ValueOr(project.ProjectRootPath, string.Empty);
        }

        private static string BuildPlanningAuthorityNote(ProjectDefinition project)
        {
            var authority = project == null || project.Planning == null
                ? string.Empty
                : project.Planning.Authority;

            if (string.Equals(authority, "ado", StringComparison.OrdinalIgnoreCase))
            {
                return "Planning authority is Azure DevOps. Control Tower should stay read-only for planning changes.";
            }

            if (string.Equals(authority, "github", StringComparison.OrdinalIgnoreCase))
            {
                return "Planning authority is GitHub. Control Tower should preserve identity and launch context only.";
            }

            return "Planning authority is repo-native. Draft updates can be reviewed here before you apply them.";
        }

        private static string BuildExternalSystemSummary(ProjectDefinition project)
        {
            if (project == null)
            {
                return "No external board references configured.";
            }

            var parts = new List<string>();
            if (project.ExternalRefs != null)
            {
                if (!string.IsNullOrWhiteSpace(project.ExternalRefs.GitHubRepo))
                {
                    parts.Add("GitHub repo: " + project.ExternalRefs.GitHubRepo);
                }

                if (!string.IsNullOrWhiteSpace(project.ExternalRefs.AdoProject))
                {
                    var area = string.IsNullOrWhiteSpace(project.ExternalRefs.AdoAreaPath)
                        ? string.Empty
                        : " (" + project.ExternalRefs.AdoAreaPath + ")";
                    parts.Add("ADO project: " + project.ExternalRefs.AdoProject + area);
                }
            }
            if (parts.Count == 0)
            {
                if (project.Launch != null && !string.IsNullOrWhiteSpace(project.Launch.GitHub))
                {
                    parts.Add("GitHub linked");
                }

                if (project.Launch != null && !string.IsNullOrWhiteSpace(project.Launch.Ado))
                {
                    parts.Add("Azure DevOps linked");
                }
            }

            return parts.Count == 0
                ? "No external board references configured."
                : string.Join(" | ", parts);
        }

        private static string ResolveWorkspaceMode(ProjectDefinition project)
        {
            var hasLocal = project != null &&
                           project.Locations != null &&
                           !string.IsNullOrWhiteSpace(project.Locations.LocalPath);
            var hasSsh = project != null &&
                         project.Locations != null &&
                         !string.IsNullOrWhiteSpace(project.Locations.SshTarget);
            var hasRemote = project != null &&
                            project.Locations != null &&
                            !string.IsNullOrWhiteSpace(project.Locations.RemoteUrl);

            if (hasLocal && hasSsh)
            {
                return "Hybrid";
            }

            if (hasSsh)
            {
                return "Remote SSH";
            }

            if (hasLocal)
            {
                return "Local";
            }

            if (hasRemote)
            {
                return "Hosted only";
            }

            return "Unknown";
        }

        // Internal repo-truth helpers below. Naming uses "ahead/behind" and
        // "repo state" framing per ADR-001 / M4: the public ProjectOverview
        // surface still exposes SyncStatus / StatusLine for UI binding
        // stability, but inside the composer we describe what the git
        // snapshot literally says, not a synchronization concept.

        private static string FormatAheadBehind(RepoSnapshot snapshot)
        {
            if (!snapshot.HasUpstream)
            {
                return string.IsNullOrWhiteSpace(snapshot.OriginUrl)
                    ? "No upstream configured"
                    : "Branch has no upstream (push with -u to publish)";
            }

            return "Ahead " + snapshot.AheadBy + ", behind " + snapshot.BehindBy;
        }

        // Compact glyph form for the dense table SYNC column. ▲ = ahead (local
        // commits to push), ▼ = behind (upstream commits to pull). Zeros are
        // omitted; an up-to-date upstream reads "in sync".
        private static string FormatSyncCompact(RepoSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsAvailable)
            {
                return "—";
            }

            if (!snapshot.HasUpstream)
            {
                return "no upstream";
            }

            if (snapshot.AheadBy == 0 && snapshot.BehindBy == 0)
            {
                return "in sync";
            }

            var parts = new System.Collections.Generic.List<string>();
            if (snapshot.AheadBy > 0)
            {
                parts.Add("▲" + snapshot.AheadBy);
            }
            if (snapshot.BehindBy > 0)
            {
                parts.Add("▼" + snapshot.BehindBy);
            }
            return string.Join("  ", parts);
        }

        private static string FormatRepoState(RepoSnapshot snapshot, DateTime nowUtc)
        {
            if (!snapshot.IsAvailable)
            {
                return "Repo unavailable";
            }

            if (snapshot.IsDirty)
            {
                return "Needs attention";
            }

            if (snapshot.LastCommitUtc.HasValue)
            {
                var age = nowUtc - snapshot.LastCommitUtc.Value;
                if (age.TotalDays >= 14)
                {
                    return "Stale";
                }
            }

            if (!snapshot.HasUpstream)
            {
                return string.IsNullOrWhiteSpace(snapshot.OriginUrl)
                    ? "Local only"
                    : "Branch not published";
            }

            return "Healthy";
        }

        private static string FormatActivity(DateTime? lastCommitUtc, DateTime nowUtc)
        {
            if (!lastCommitUtc.HasValue)
            {
                return "No commit data";
            }

            var age = nowUtc - lastCommitUtc.Value;
            if (age.TotalDays < 1)
            {
                return "Updated today";
            }

            if (age.TotalDays < 7)
            {
                return "Updated " + Math.Max(1, (int)age.TotalDays) + " days ago";
            }

            return "Stale: " + Math.Max(1, (int)age.TotalDays) + " days since last commit";
        }

        private static string BuildRepoStateLine(IList<ValidationIssue> issues, RepoSnapshot snapshot)
        {
            if (issues != null && issues.Any(issue => issue.Severity == IssueSeverity.Error))
            {
                return issues.First(issue => issue.Severity == IssueSeverity.Error).Message;
            }

            if (snapshot != null && !snapshot.IsAvailable && !string.IsNullOrWhiteSpace(snapshot.StatusMessage))
            {
                return snapshot.StatusMessage;
            }

            if (snapshot != null && snapshot.IsDirty)
            {
                return "Working tree has uncommitted changes";
            }

            if (issues != null && issues.Count > 0)
            {
                return issues[0].Message;
            }

            return "Ready";
        }

        private static string ValueOr(string first, string second, string third)
        {
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }

            if (!string.IsNullOrWhiteSpace(second))
            {
                return second;
            }

            return third;
        }

        private static string ValueOr(string value, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return fallback;
        }
    }
}
