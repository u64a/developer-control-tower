using System;
using System.IO;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Yaml.Dto;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ControlTower.Infrastructure.Yaml
{
    public sealed class ProjectYamlProvider : IProjectProvider
    {
        public ProjectLoadResult LoadProject(string projectRootPath)
        {
            return LoadProject(projectRootPath, projectRootPath);
        }

        public ProjectLoadResult LoadProject(string workingRootPath, string metadataRootPath)
        {
            var result = new ProjectLoadResult();
            var project = result.Project;
            var metadataPath = Path.Combine(metadataRootPath, ".controltower", "project.yml");

            // Robustness/transition: if the central stub has no metadata yet
            // (never migrated, or a migration that failed for this project),
            // fall back to any legacy in-repo .controltower so the project
            // still resolves. New projects only ever have the stub copy.
            if (!File.Exists(metadataPath))
            {
                var legacyPath = Path.Combine(workingRootPath, ".controltower", "project.yml");
                if (File.Exists(legacyPath))
                {
                    metadataPath = legacyPath;
                }
            }
            project.ProjectRootPath = workingRootPath;
            project.MetadataPath = metadataPath;
            project.Locations.LocalPath = workingRootPath;
            project.Launch.VsCodeLocal = workingRootPath;

            if (!File.Exists(metadataPath))
            {
                result.Issues.Add(new ValidationIssue(IssueSeverity.Error, "Missing .controltower\\project.yml"));
                project.Id = ProjectIdentity.CreateFallback(ProjectIdentity.MissingPrefix, workingRootPath);
                project.DisplayName = Path.GetFileName(workingRootPath);
                return result;
            }

            ProjectYamlDto dto = null;
            try
            {
                var yaml = File.ReadAllText(metadataPath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                dto = deserializer.Deserialize<ProjectYamlDto>(yaml);
            }
            catch (Exception ex)
            {
                // M1: include a structured code so callers can distinguish
                // "missing file" from "broken file" rather than treating
                // malformed YAML as a silent default.
                result.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "project/yaml/malformed",
                    "project.yml contains malformed YAML: " + ex.Message));
            }

            if (dto != null)
            {
                project.Id = dto.Id ?? string.Empty;
                project.DisplayName = dto.DisplayName ?? string.Empty;
                project.Summary = dto.Summary ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(dto.LifecycleState))
                {
                    project.LifecycleState = dto.LifecycleState;
                }

                if (!string.IsNullOrWhiteSpace(dto.Group))
                {
                    project.Group = dto.Group.Trim();
                }

                if (dto.Planning != null)
                {
                    if (!string.IsNullOrWhiteSpace(dto.Planning.Authority))
                    {
                        project.Planning.Authority = dto.Planning.Authority;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Planning.SourceRef))
                    {
                        project.Planning.SourceRef = dto.Planning.SourceRef;
                    }
                }

                if (dto.Locations != null)
                {
                    if (!string.IsNullOrWhiteSpace(dto.Locations.LocalPath))
                    {
                        project.Locations.LocalPath = dto.Locations.LocalPath;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Locations.SshTarget))
                    {
                        project.Locations.SshTarget = dto.Locations.SshTarget;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Locations.RemoteUrl))
                    {
                        project.Locations.RemoteUrl = dto.Locations.RemoteUrl;
                    }
                }

                if (dto.Launch != null)
                {
                    if (!string.IsNullOrWhiteSpace(dto.Launch.VsCodeLocal))
                    {
                        project.Launch.VsCodeLocal = dto.Launch.VsCodeLocal;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Launch.VsCodeSsh))
                    {
                        project.Launch.VsCodeSsh = dto.Launch.VsCodeSsh;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Launch.GitHub))
                    {
                        project.Launch.GitHub = dto.Launch.GitHub;
                    }

                    if (!string.IsNullOrWhiteSpace(dto.Launch.Ado))
                    {
                        project.Launch.Ado = dto.Launch.Ado;
                    }
                }

                if (dto.Docs != null)
                {
                    foreach (var docDto in dto.Docs)
                    {
                        project.Docs.Add(new DocLink
                        {
                            Id = docDto.Id ?? string.Empty,
                            Title = docDto.Title ?? string.Empty,
                            Kind = docDto.Kind ?? string.Empty,
                            Url = docDto.Url ?? string.Empty
                        });
                    }
                }

                if (dto.ExternalRefs != null)
                {
                    if (dto.ExternalRefs.GitHub != null)
                    {
                        if (!string.IsNullOrWhiteSpace(dto.ExternalRefs.GitHub.Repo))
                        {
                            project.ExternalRefs.GitHubRepo = dto.ExternalRefs.GitHub.Repo;
                        }

                        if (!string.IsNullOrWhiteSpace(dto.ExternalRefs.GitHub.DefaultBranch))
                        {
                            project.ExternalRefs.GitHubDefaultBranch = dto.ExternalRefs.GitHub.DefaultBranch;
                        }
                    }

                    if (dto.ExternalRefs.Ado != null)
                    {
                        if (!string.IsNullOrWhiteSpace(dto.ExternalRefs.Ado.Organization))
                        {
                            project.ExternalRefs.AdoOrganization = dto.ExternalRefs.Ado.Organization;
                        }

                        if (!string.IsNullOrWhiteSpace(dto.ExternalRefs.Ado.Project))
                        {
                            project.ExternalRefs.AdoProject = dto.ExternalRefs.Ado.Project;
                        }

                        if (!string.IsNullOrWhiteSpace(dto.ExternalRefs.Ado.AreaPath))
                        {
                            project.ExternalRefs.AdoAreaPath = dto.ExternalRefs.Ado.AreaPath;
                        }

                        if (!string.IsNullOrWhiteSpace(dto.ExternalRefs.Ado.WorkItemRootId))
                        {
                            project.ExternalRefs.AdoWorkItemRootId = dto.ExternalRefs.Ado.WorkItemRootId;
                        }
                    }
                }
            }

            // An SSH/remote-only project must not inherit the .controltower
            // config root (typically under OneDrive) as a fake local clone.
            // Lines 22-23 default LocalPath/VsCodeLocal to the config root for
            // genuinely config-local projects; clear that default when the
            // project has a remote working copy (SSH/remote URL) and no
            // explicit local clone, so it classifies as Remote SSH and its
            // actions/scan/display point at the remote, not the OneDrive folder.
            var hasExplicitLocalPath = dto != null && dto.Locations != null
                && !string.IsNullOrWhiteSpace(dto.Locations.LocalPath);
            var hasRemoteWorkingCopy = !string.IsNullOrWhiteSpace(project.Locations.SshTarget)
                || !string.IsNullOrWhiteSpace(project.Locations.RemoteUrl);
            if (!hasExplicitLocalPath && hasRemoteWorkingCopy)
            {
                project.Locations.LocalPath = string.Empty;
                project.Launch.VsCodeLocal = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(project.Id))
            {
                result.Issues.Add(new ValidationIssue(IssueSeverity.Error, "project.yml is missing id"));
                project.Id = ProjectIdentity.CreateFallback(ProjectIdentity.InvalidPrefix, workingRootPath);
            }
            if (string.IsNullOrWhiteSpace(project.DisplayName))
            {
                result.Issues.Add(new ValidationIssue(IssueSeverity.Warning, "project.yml is missing display_name"));
                project.DisplayName = Path.GetFileName(workingRootPath);
            }

            if (string.IsNullOrWhiteSpace(project.Planning.Authority))
            {
                project.Planning.Authority = "repo";
            }

            return result;
        }
    }
}
