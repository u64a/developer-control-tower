using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Core.Validation;

namespace ControlTower.Infrastructure.Registration
{
    internal enum RegistrationOutcome
    {
        Added,
        Updated,
        RejectedDuplicate
    }

    public sealed class ProjectRegistrationService : IProjectRegistrationService
    {
        private readonly string _portfolioPath;
        private readonly IStoreProvider _storeProvider;

        public ProjectRegistrationService(string portfolioPath)
            : this(portfolioPath, null)
        {
        }

        public ProjectRegistrationService(string portfolioPath, IStoreProvider storeProvider)
        {
            _portfolioPath = portfolioPath;
            _storeProvider = storeProvider;
        }

        public ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest request)
        {
            if (request == null)
            {
                return new ProjectRegistrationResult { Success = false, Message = "Project details were not supplied" };
            }

            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return new ProjectRegistrationResult { Success = false, Message = "Display name is required" };
            }

            if (string.IsNullOrWhiteSpace(request.LocalPath) && string.IsNullOrWhiteSpace(request.SshTarget))
            {
                return new ProjectRegistrationResult { Success = false, Message = "Provide a local path or an SSH target" };
            }

            if (!string.IsNullOrWhiteSpace(request.LocalPath) &&
                !Directory.Exists(request.LocalPath) &&
                string.IsNullOrWhiteSpace(request.SshTarget))
            {
                return new ProjectRegistrationResult { Success = false, Message = "The local repo path does not exist" };
            }

            // Refuse credential-bearing remote URLs before any disk IO. The
            // portfolio + project yaml files must never embed credentials.
            if (UrlSanitizer.HasCredentials(request.RemoteUrl)
                || UrlSanitizer.HasCredentials(request.GitHubUrl)
                || UrlSanitizer.HasCredentials(request.AdoUrl))
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "Remote URL contains embedded credentials. Strip them before registering."
                };
            }

            var projectId = string.IsNullOrWhiteSpace(request.ProjectId)
                ? BuildProjectId(request.DisplayName)
                : request.ProjectId.Trim();

            if (!TryLoadPortfolioForMutation(out var portfolio, out var portfolioFailure))
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "Registration failed: " + portfolioFailure
                };
            }

            // Metadata always lives in the central OneDrive stub
            // ({configRoot}\portfolio-projects\{id}), never inside the target
            // repo. The portfolio entry itself still points at the working
            // location (the repo for local, the stub for SSH-only).
            var metadataRoot = ResolveMetadataRoot(request, projectId);
            var portfolioEntryPath = ResolvePortfolioEntryPath(request, metadataRoot);
            var controlTowerFolder = Path.Combine(metadataRoot, ".controltower");

            var sanitizedRemote = ResolveSanitizedRemoteUrl(request);

            // Apply the portfolio mutation in memory before touching metadata.
            // This completes all load/validation and duplicate checks first;
            // the actual portfolio write happens only after metadata succeeds.
            var outcome = UpdatePortfolioIndex(
                portfolio,
                projectId,
                portfolioEntryPath,
                sanitizedRemote,
                request.AllowOverwrite);
            if (outcome == RegistrationOutcome.RejectedDuplicate)
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "Project id '" + projectId + "' is already registered. Edit the existing entry or choose a different id."
                };
            }

            Directory.CreateDirectory(controlTowerFolder);

            var projectYamlPath = Path.Combine(controlTowerFolder, "project.yml");
            var productMapPath = Path.Combine(controlTowerFolder, "product-map.yml");
            var existingProject = LoadExistingProject(metadataRoot, request);

            File.WriteAllLines(projectYamlPath, BuildProjectYamlLines(request, projectId, existingProject, sanitizedRemote), Encoding.UTF8);
            if (!File.Exists(productMapPath))
            {
                File.WriteAllLines(productMapPath, BuildProductMapYamlLines(request, projectId), Encoding.UTF8);
            }

            WritePortfolio(portfolio);

            return new ProjectRegistrationResult
            {
                Success = true,
                ProjectId = projectId,
                Message = "Project added to the portfolio"
            };
        }

        public ProjectRegistrationResult RemoveProject(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "Project id is required"
                };
            }

            if (!File.Exists(_portfolioPath))
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "Portfolio file was not found"
                };
            }

            if (!TryLoadPortfolioForMutation(out var portfolio, out var portfolioFailure))
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "Removal failed: " + portfolioFailure
                };
            }

            var existing = portfolio.Projects.FirstOrDefault(
                project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "Project was not found in the portfolio"
                };
            }

            portfolio.Projects.Remove(existing);
            WritePortfolio(portfolio);

            return new ProjectRegistrationResult
            {
                Success = true,
                ProjectId = projectId,
                Message = "Project removed from Developer Control Tower. Repo files were left untouched."
            };
        }

        private string ResolveMetadataRoot(ProjectRegistrationRequest request, string projectId)
        {
            // Metadata is always stored centrally, keyed by project id, under the
            // config root (typically OneDrive). It is never written into the
            // target repo working tree. This mirrors how SSH projects have
            // always been handled and keeps managed repos clean.
            var portfolioRoot = Path.GetDirectoryName(_portfolioPath);
            var managedRoot = Path.Combine(
                portfolioRoot ?? string.Empty,
                ProjectMetadataLocator.ManagedProjectsFolder,
                projectId);
            return managedRoot;
        }

        /// <summary>
        /// The path recorded in the portfolio entry. For local projects this is
        /// the repo working directory (so launch / roadmap / scan keep pointing
        /// at the real repo); for SSH-only projects there is no local working
        /// tree, so the entry points at the central metadata stub, exactly as
        /// before.
        /// </summary>
        private static string ResolvePortfolioEntryPath(ProjectRegistrationRequest request, string metadataRoot)
        {
            if (!string.IsNullOrWhiteSpace(request.SourcePath) && Directory.Exists(request.SourcePath))
            {
                return Path.GetFullPath(request.SourcePath);
            }

            if (!string.IsNullOrWhiteSpace(request.LocalPath))
            {
                try
                {
                    return Path.GetFullPath(request.LocalPath);
                }
                catch
                {
                    return request.LocalPath;
                }
            }

            return metadataRoot;
        }

        private bool TryLoadPortfolioForMutation(
            out PortfolioIndex portfolio,
            out string failureMessage)
        {
            if (!File.Exists(_portfolioPath))
            {
                portfolio = new PortfolioIndex();
                failureMessage = string.Empty;
                return true;
            }

            var provider = new Yaml.PortfolioYamlProvider(_portfolioPath, _storeProvider);
            portfolio = provider.LoadPortfolio();
            if (portfolio == null || portfolio.Projects == null || portfolio.Issues == null)
            {
                failureMessage =
                    "The existing portfolio could not be safely validated. No files were changed.";
                return false;
            }

            var error = portfolio.Issues.FirstOrDefault(
                issue => issue != null && issue.Severity == IssueSeverity.Error);
            if (error != null)
            {
                var detail = string.IsNullOrWhiteSpace(error.Message)
                    ? "The loader reported an error."
                    : error.Message;
                failureMessage =
                    "The existing portfolio could not be safely validated: " +
                    detail +
                    " No files were changed.";
                return false;
            }

            failureMessage = string.Empty;
            return true;
        }

        private static RegistrationOutcome UpdatePortfolioIndex(
            PortfolioIndex portfolio,
            string projectId,
            string path,
            string remoteUrl,
            bool allowOverwrite)
        {
            var existing = portfolio.Projects.FirstOrDefault(
                project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));

            // Physical-path guard: if no entry matches the requested ID, check whether
            // the same physical path is already registered under a different ID. Two IDs
            // pointing at the same folder produce visible duplicates in the UI.
            ProjectRef pathMatch = null;
            if (existing == null && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var pathNorm = Path.GetFullPath(path);
                    pathMatch = portfolio.Projects.FirstOrDefault(p =>
                        !p.UsesStore &&
                        !string.IsNullOrWhiteSpace(p.Path) &&
                        string.Equals(Path.GetFullPath(p.Path), pathNorm,
                            StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    // Path.GetFullPath can throw on invalid paths; treat as no match.
                }
            }

            if (pathMatch != null)
            {
                if (!allowOverwrite)
                {
                    return RegistrationOutcome.RejectedDuplicate;
                }
                // Migrate the existing entry to the new ID in place; avoids appending a duplicate.
                pathMatch.Id = projectId;
                pathMatch.Path = path;
                if (!string.IsNullOrWhiteSpace(remoteUrl))
                {
                    pathMatch.RemoteUrl = remoteUrl;
                }
                return RegistrationOutcome.Updated;
            }

            RegistrationOutcome outcome;
            if (existing == null)
            {
                portfolio.Projects.Add(new ProjectRef
                {
                    Id = projectId,
                    Path = path,
                    RemoteUrl = remoteUrl ?? string.Empty
                });
                outcome = RegistrationOutcome.Added;
            }
            else if (allowOverwrite)
            {
                existing.Path = path;
                // Never blank out an existing remote_url with an empty
                // value — only positive updates overwrite the column.
                if (!string.IsNullOrWhiteSpace(remoteUrl))
                {
                    existing.RemoteUrl = remoteUrl;
                }
                outcome = RegistrationOutcome.Updated;
            }
            else
            {
                return RegistrationOutcome.RejectedDuplicate;
            }

            return outcome;
        }

        private void WritePortfolio(PortfolioIndex portfolio)
        {
            var provider = new Yaml.PortfolioYamlProvider(_portfolioPath, _storeProvider);
            provider.SavePortfolio(portfolio);
        }

        private static IEnumerable<string> BuildProjectYamlLines(ProjectRegistrationRequest request, string projectId, ProjectDefinition existingProject, string sanitizedRemote)
        {
            var summary = string.IsNullOrWhiteSpace(request.Summary)
                ? "Project registered from Developer Control Tower."
                : request.Summary.Trim();

            // Remote URL precedence: sanitized request > GitHub > ADO > existing.
            // Only safe (already-sanitized) values participate; an empty update
            // must not blank out a previously stored URL.
            var remoteUrl = FirstNonEmpty(
                sanitizedRemote,
                request.GitHubUrl,
                request.AdoUrl,
                existingProject?.Locations?.RemoteUrl);

            // Preserve custom planning values on edit; new projects get defaults.
            var authority = existingProject != null && !string.IsNullOrWhiteSpace(existingProject.Planning?.Authority)
                ? existingProject.Planning.Authority
                : "repo";
            var sourceRef = existingProject != null && !string.IsNullOrWhiteSpace(existingProject.Planning?.SourceRef)
                ? existingProject.Planning.SourceRef
                : @".controltower\product-map.yml";

            var lines = new List<string>();
            lines.Add("kind: developer-control-tower/project");
            lines.Add("schema_version: 0");
            lines.Add(string.Empty);
            lines.Add("id: " + projectId);
            lines.Add("display_name: " + EscapeScalar(request.DisplayName));
            lines.Add("summary: " + EscapeScalar(summary));
            lines.Add("lifecycle_state: " + EscapeScalar(DefaultValue(request.LifecycleState, "active")));
            if (!string.IsNullOrWhiteSpace(request.Group))
            {
                lines.Add("group: " + EscapeScalar(request.Group.Trim()));
            }
            lines.Add(string.Empty);
            lines.Add("planning:");
            lines.Add("  authority: " + EscapeScalar(authority));
            lines.Add("  source_ref: " + EscapeScalar(sourceRef));
            lines.Add(string.Empty);
            lines.Add("locations:");
            lines.Add("  local_path: " + EscapeScalar(request.LocalPath));
            lines.Add("  ssh_target: " + EscapeScalar(request.SshTarget));
            lines.Add("  remote_url: " + EscapeScalar(remoteUrl));
            lines.Add(string.Empty);
            lines.Add("launch:");
            lines.Add("  vscode_local: " + EscapeScalar(request.LocalPath));
            lines.Add("  vscode_ssh: " + EscapeScalar(BuildVsCodeSsh(request.SshTarget)));
            lines.Add("  github: " + EscapeScalar(request.GitHubUrl));
            lines.Add("  ado: " + EscapeScalar(request.AdoUrl));
            lines.Add(string.Empty);
            AppendDocs(lines, existingProject);
            AppendExternalRefs(lines, existingProject);

            return lines;
        }

        private static void AppendDocs(ICollection<string> lines, ProjectDefinition existingProject)
        {
            if (existingProject == null || existingProject.Docs == null || existingProject.Docs.Count == 0)
            {
                lines.Add("docs: []");
                return;
            }

            lines.Add("docs:");
            foreach (var doc in existingProject.Docs)
            {
                lines.Add("  - id: " + EscapeScalar(doc.Id));
                lines.Add("    title: " + EscapeScalar(doc.Title));
                lines.Add("    kind: " + EscapeScalar(doc.Kind));
                lines.Add("    url: " + EscapeScalar(doc.Url));
            }
        }

        private static void AppendExternalRefs(ICollection<string> lines, ProjectDefinition existingProject)
        {
            if (existingProject == null || existingProject.ExternalRefs == null)
            {
                return;
            }

            var externalRefs = existingProject.ExternalRefs;
            if (string.IsNullOrWhiteSpace(externalRefs.GitHubRepo) &&
                string.IsNullOrWhiteSpace(externalRefs.GitHubDefaultBranch) &&
                string.IsNullOrWhiteSpace(externalRefs.AdoOrganization) &&
                string.IsNullOrWhiteSpace(externalRefs.AdoProject) &&
                string.IsNullOrWhiteSpace(externalRefs.AdoAreaPath) &&
                string.IsNullOrWhiteSpace(externalRefs.AdoWorkItemRootId))
            {
                return;
            }

            lines.Add(string.Empty);
            lines.Add("external_refs:");
            lines.Add("  github:");
            lines.Add("    repo: " + EscapeScalar(externalRefs.GitHubRepo));
            lines.Add("    default_branch: " + EscapeScalar(externalRefs.GitHubDefaultBranch));
            lines.Add("  ado:");
            lines.Add("    organization: " + EscapeScalar(externalRefs.AdoOrganization));
            lines.Add("    project: " + EscapeScalar(externalRefs.AdoProject));
            lines.Add("    area_path: " + EscapeScalar(externalRefs.AdoAreaPath));
            lines.Add("    work_item_root_id: " + EscapeScalar(externalRefs.AdoWorkItemRootId));
        }

        private static IEnumerable<string> BuildProductMapYamlLines(ProjectRegistrationRequest request, string projectId)
        {
            var description = string.IsNullOrWhiteSpace(request.Summary)
                ? "Portable product intent for " + request.DisplayName.Trim() + "."
                : request.Summary.Trim();

            var lines = new List<string>();
            lines.Add("kind: developer-control-tower/product-map");
            lines.Add("schema_version: 0");
            lines.Add(string.Empty);
            lines.Add("project_id: " + projectId);
            lines.Add("planning_authority: repo");
            lines.Add(string.Empty);
            lines.Add("nodes:");
            lines.Add("  - id: product." + projectId.Replace('.', '-'));
            lines.Add("    type: product");
            lines.Add("    title: " + EscapeScalar(request.DisplayName));
            lines.Add("    parent_id: null");
            lines.Add("    status: active");
            lines.Add("    description: " + EscapeScalar(description));
            lines.Add(string.Empty);
            lines.Add("  - id: initiative.foundation");
            lines.Add("    type: initiative");
            lines.Add("    title: Foundation");
            lines.Add("    parent_id: product." + projectId.Replace('.', '-'));
            lines.Add("    status: active");
            lines.Add("    description: Initial planning structure created by Developer Control Tower.");

            return lines;
        }

        private static string BuildProjectId(string displayName)
        {
            var builder = new StringBuilder();
            foreach (var character in displayName.Trim().ToLowerInvariant())
            {
                if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
                {
                    builder.Append(character);
                }
                else if (builder.Length == 0 || builder[builder.Length - 1] != '-')
                {
                    builder.Append('-');
                }
            }

            var id = builder.ToString().Trim('-');
            if (string.IsNullOrWhiteSpace(id))
            {
                id = "project";
            }

            return id;
        }

        private static string BuildVsCodeSsh(string sshTarget)
        {
            if (string.IsNullOrWhiteSpace(sshTarget))
            {
                return string.Empty;
            }

            var separator = sshTarget.IndexOf(':');
            if (separator <= 0 || separator >= sshTarget.Length - 1)
            {
                return string.Empty;
            }

            var host = sshTarget.Substring(0, separator).Trim();
            var path = sshTarget.Substring(separator + 1).Trim().Replace("\\", "/");

            if (path.StartsWith("/", StringComparison.Ordinal))
            {
                // POSIX absolute path: use as-is (e.g. "/srv/repos/project")
            }
            else if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                // Windows absolute path: prepend '/' for the VSCode Remote SSH URI format
                // (e.g. "D:/repos/project" → "/D:/repos/project")
                path = "/" + path;
            }
            else
            {
                // Relative path: cannot determine the correct absolute remote path here.
                // Return empty to prevent a silently wrong launch URI (e.g. "/repos/project").
                return string.Empty;
            }

            return "ssh-remote+" + host + path;
        }

        private static ProjectDefinition LoadExistingProject(string metadataRoot, ProjectRegistrationRequest request)
        {
            // Prefer the central metadata stub. Fall back to any legacy in-repo
            // .controltower (SourcePath / LocalPath) so docs and external_refs
            // survive the one-time move from repo-local to central metadata.
            var candidates = new[] { metadataRoot, request?.SourcePath, request?.LocalPath };
            foreach (var root in candidates)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var projectYamlPath = Path.Combine(root, ".controltower", "project.yml");
                if (File.Exists(projectYamlPath))
                {
                    return new Yaml.ProjectYamlProvider().LoadProject(root).Project;
                }
            }

            return null;
        }

        private static string EscapeScalar(string value)
        {
            return Yaml.YamlScalar.Quote(value);
        }

        private static string DefaultValue(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string ResolveSanitizedRemoteUrl(ProjectRegistrationRequest request)
        {
            // Source-of-truth order for the persisted remote_url column:
            //   1. neutral RemoteUrl (new Phase C path), sanitized
            //   2. GitHubUrl (host-specific launch)
            //   3. AdoUrl (host-specific launch)
            // We deliberately sanitize the explicit RemoteUrl only — the
            // GitHub/Ado URLs are also validated by HasCredentials at the
            // top of RegisterProject so we never reach this point with a
            // credential-bearing fallback.
            if (!string.IsNullOrWhiteSpace(request.RemoteUrl))
            {
                return UrlSanitizer.StripCredentials(request.RemoteUrl);
            }
            if (!string.IsNullOrWhiteSpace(request.GitHubUrl))
            {
                return request.GitHubUrl.Trim();
            }
            if (!string.IsNullOrWhiteSpace(request.AdoUrl))
            {
                return request.AdoUrl.Trim();
            }
            return string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v.Trim();
                }
            }
            return string.Empty;
        }
    }
}
