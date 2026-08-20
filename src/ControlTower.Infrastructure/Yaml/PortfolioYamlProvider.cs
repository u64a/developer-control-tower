using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Yaml.Dto;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ControlTower.Infrastructure.Yaml
{
    public sealed class PortfolioYamlProvider : IPortfolioProvider
    {
        private readonly string _portfolioPath;
        private readonly IStoreProvider _storeProvider;

        public PortfolioYamlProvider(string portfolioPath)
            : this(portfolioPath, null)
        {
        }

        public PortfolioYamlProvider(string portfolioPath, IStoreProvider storeProvider)
        {
            _portfolioPath = portfolioPath;
            _storeProvider = storeProvider;
        }

        public PortfolioIndex LoadPortfolio()
        {
            var portfolio = new PortfolioIndex();

            if (!File.Exists(_portfolioPath))
            {
                return portfolio;
            }

            PortfolioYamlDto dto = null;
            try
            {
                var yaml = File.ReadAllText(_portfolioPath);
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    portfolio.Issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "portfolio/yaml/empty",
                        "portfolio.yml exists but is empty."));
                    return portfolio;
                }

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                dto = deserializer.Deserialize<PortfolioYamlDto>(yaml);
            }
            catch (Exception ex)
            {
                // M1: surface parse errors instead of returning a silent empty
                // portfolio. Callers can then warn the user that the file is
                // broken rather than acting as if it were empty.
                portfolio.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "portfolio/yaml/malformed",
                    "portfolio.yml contains malformed YAML: " + ex.Message));
                return portfolio;
            }

            if (dto == null)
            {
                portfolio.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "portfolio/yaml/structure",
                    "portfolio.yml does not contain a portfolio mapping."));
                return portfolio;
            }

            if (!dto.SchemaVersion.HasValue)
            {
                portfolio.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "portfolio/schema/missing",
                    "portfolio.yml is missing schema_version."));
            }
            else if (dto.SchemaVersion.Value != 0 && dto.SchemaVersion.Value != 1)
            {
                portfolio.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "portfolio/schema/unsupported",
                    "portfolio.yml uses unsupported schema_version " + dto.SchemaVersion.Value + "."));
            }

            if (dto.Projects == null)
            {
                portfolio.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "portfolio/projects/missing",
                    "portfolio.yml must contain a projects list. Use projects: [] for an empty portfolio."));
                return portfolio;
            }

            for (var index = 0; index < dto.Projects.Count; index++)
            {
                var item = dto.Projects[index];
                if (item == null)
                {
                    portfolio.Issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "portfolio/project/invalid",
                        "portfolio.yml project entry " + (index + 1) + " is empty."));
                    continue;
                }

                var hasPath = !string.IsNullOrWhiteSpace(item.Path);
                var hasStore = !string.IsNullOrWhiteSpace(item.Store);

                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    portfolio.Issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "portfolio/project/id-missing",
                        "portfolio.yml project entry " + (index + 1) + " is missing id."));
                }

                if (!hasPath && !hasStore)
                {
                    portfolio.Issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "portfolio/project/location-missing",
                        "portfolio.yml project entry " + (index + 1) + " must contain path or store."));
                }
                else if (hasPath && hasStore)
                {
                    portfolio.Issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "portfolio/project/location-ambiguous",
                        "portfolio.yml project entry " + (index + 1) + " cannot contain both path and store."));
                }

                var projectRef = new ProjectRef
                {
                    Id = item.Id ?? string.Empty,
                    StoreId = item.Store ?? string.Empty,
                    Folder = item.Folder ?? string.Empty,
                    RemoteUrl = item.RemoteUrl ?? string.Empty
                };

                if (hasStore && _storeProvider != null)
                {
                    projectRef.Path = _storeProvider.ResolveProjectPath(
                        item.Store, projectRef.Id, projectRef.Folder);
                }
                else if (hasPath)
                {
                    projectRef.Path = item.Path;
                }

                portfolio.Projects.Add(projectRef);
            }

            var portfolioRoot = Path.GetDirectoryName(_portfolioPath);
            foreach (var project in portfolio.Projects)
            {
                if (string.IsNullOrWhiteSpace(project.Path) && !project.UsesStore)
                {
                    project.Path = portfolioRoot ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(project.Path) &&
                    !project.UsesStore &&
                    !Path.IsPathRooted(project.Path))
                {
                    project.Path = Path.GetFullPath(
                        Path.Combine(portfolioRoot ?? string.Empty, project.Path));
                }

                if (string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Path))
                {
                    project.Id = Path.GetFileName(project.Path);
                }
            }

            var deduped = portfolio.Projects
                .GroupBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            // Secondary dedup by physical path: self-heals portfolio.yml files that already
            // contain same-path/different-id entries from before this guard existed.
            // Keep the first occurrence of each resolved path; preserve overall order.
            // Store-backed entries and empty paths are never collapsed.
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pathDeduped = new List<ProjectRef>();
            foreach (var project in deduped)
            {
                if (project.UsesStore || string.IsNullOrWhiteSpace(project.Path))
                {
                    pathDeduped.Add(project);
                    continue;
                }
                try
                {
                    var norm = Path.GetFullPath(project.Path);
                    if (seenPaths.Add(norm))
                    {
                        pathDeduped.Add(project);
                    }
                }
                catch
                {
                    // Invalid path; keep the entry rather than silently dropping it.
                    pathDeduped.Add(project);
                }
            }

            portfolio.Projects.Clear();
            foreach (var project in pathDeduped)
            {
                portfolio.Projects.Add(project);
            }

            return portfolio;
        }

        public void SavePortfolio(PortfolioIndex portfolio)
        {
            if (portfolio == null) throw new ArgumentNullException(nameof(portfolio));

            var ordered = portfolio.Projects
                .Where(project => !string.IsNullOrWhiteSpace(project.Id))
                .OrderBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var directory = Path.GetDirectoryName(_portfolioPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var lines = new List<string>();
            lines.Add("# Developer Control Tower portfolio");
            lines.Add("schema_version: 1");
            lines.Add(ordered.Count == 0 ? "projects: []" : "projects:");
            foreach (var project in ordered)
            {
                lines.Add("  - id: '" + EscapeSingleQuoted(project.Id) + "'");
                if (project.UsesStore)
                {
                    lines.Add("    store: '" + EscapeSingleQuoted(project.StoreId) + "'");
                    if (!string.IsNullOrWhiteSpace(project.Folder) &&
                        !string.Equals(project.Folder, project.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add("    folder: '" + EscapeSingleQuoted(project.Folder) + "'");
                    }
                }
                else
                {
                    lines.Add("    path: '" + EscapeSingleQuoted(project.Path) + "'");
                }

                if (!string.IsNullOrWhiteSpace(project.RemoteUrl))
                {
                    lines.Add("    remote_url: '" + EscapeSingleQuoted(project.RemoteUrl) + "'");
                }
            }

            // Atomic write: write to temp file then copy + delete.
            var tempPath = _portfolioPath + ".tmp";
            File.WriteAllLines(tempPath, lines.ToArray());
            File.Copy(tempPath, _portfolioPath, true);
            try { File.Delete(tempPath); } catch { }
        }

        private static string EscapeSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }
}
