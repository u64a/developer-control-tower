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

            // Metadata that sits inside a working tree can arrive with a clone,
            // so the settings that act on the machine rather than on this
            // project are not taken at face value below. The central store is
            // not a working tree, so its copy stays trusted.
            var metadataIsInRepo = IsInside(workingRootPath, metadataPath) && IsWorkingTree(workingRootPath);
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

                    // Only well-formed ids are kept; unknown-but-valid ids are
                    // resolved (to the default) at launch time.
                    var environmentId = LaunchEnvironmentCatalog.Normalize(dto.Launch.Environment);
                    if (LaunchEnvironmentCatalog.IsValidId(environmentId))
                    {
                        project.Launch.Environment = environmentId;
                    }

                    if (dto.Launch.CopilotAutostart != null)
                    {
                        var autostart = dto.Launch.CopilotAutostart;

                        // Names come from a file on disk and are placed on a
                        // command line, so anything outside the allowlist is
                        // dropped rather than trusted.
                        var sessionName = (autostart.SessionName ?? string.Empty).Trim();
                        var agentName = (autostart.Agent ?? string.Empty).Trim();
                        if (sessionName.Length > 0 && !CopilotAutostart.IsValidName(sessionName))
                        {
                            result.Issues.Add(new ValidationIssue(
                                IssueSeverity.Warning,
                                "project/copilot-autostart/session-name",
                                "launch.copilot_autostart.session_name contains unsupported characters and was ignored."));
                            sessionName = string.Empty;
                        }

                        if (agentName.Length > 0 && !CopilotAutostart.IsValidName(agentName))
                        {
                            result.Issues.Add(new ValidationIssue(
                                IssueSeverity.Warning,
                                "project/copilot-autostart/agent",
                                "launch.copilot_autostart.agent contains unsupported characters and was ignored."));
                            agentName = string.Empty;
                        }

                        var updateChannel = CopilotAutostart.ParseUpdateChannel(autostart.UpdateChannel);
                        if (updateChannel != CopilotUpdateChannel.Stable && metadataIsInRepo)
                        {
                            // A project.yml that ships inside the repository can
                            // arrive from a clone, so it is not allowed to move
                            // the globally installed CLI off the stable channel.
                            // Switching channels stays an explicit user action.
                            result.Issues.Add(new ValidationIssue(
                                IssueSeverity.Warning,
                                "project/copilot-autostart/update-channel",
                                "launch.copilot_autostart.update_channel was ignored because it comes from the repository; the stable channel was used instead."));
                            updateChannel = CopilotUpdateChannel.Stable;
                        }

                        project.Launch.CopilotAutostart = new CopilotAutostart
                        {
                            Enabled = autostart.Enabled,
                            SessionMode = CopilotAutostart.ParseSessionMode(autostart.SessionMode),
                            SessionName = sessionName,
                            AgentName = agentName,
                            Yolo = autostart.Yolo,
                            CheckForUpdates = autostart.CheckUpdates,
                            UpdateChannel = updateChannel,
                            UseIntegratedTerminal = autostart.IntegratedTerminal
                        };
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

        /// <summary>
        /// Whether <paramref name="path"/> sits inside <paramref name="root"/>.
        /// Used to tell metadata this tool owns from a copy that travelled with
        /// a clone.
        /// </summary>
        private static bool IsInside(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var full = Path.GetFullPath(path);
                var basePath = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                // An unusable path cannot be shown to be tool-owned.
                return true;
            }
        }

        /// <summary>
        /// Whether <paramref name="path"/> is a Git working tree. A worktree or
        /// submodule carries a <c>.git</c> file rather than a directory, so both
        /// are checked.
        /// </summary>
        private static bool IsWorkingTree(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var git = Path.Combine(path, ".git");
            return Directory.Exists(git) || File.Exists(git);
        }
    }
}
