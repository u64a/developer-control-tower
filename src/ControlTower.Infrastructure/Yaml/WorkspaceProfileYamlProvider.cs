using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Yaml.Dto;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ControlTower.Infrastructure.Yaml
{
    public sealed class WorkspaceProfileYamlProvider : IWorkspaceProfileProvider
    {
        public const int CurrentSchemaVersion = 1;

        private readonly string _profilesPath;

        public WorkspaceProfileYamlProvider(string profilesPath)
        {
            if (string.IsNullOrWhiteSpace(profilesPath))
            {
                throw new ArgumentException("Profiles path is required.", nameof(profilesPath));
            }

            _profilesPath = profilesPath;
        }

        public WorkspaceProfileCatalog LoadProfiles()
        {
            var catalog = new WorkspaceProfileCatalog();
            if (!File.Exists(_profilesPath))
            {
                return catalog;
            }

            WorkspaceProfilesYamlDto dto;
            try
            {
                var yaml = File.ReadAllText(_profilesPath);
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    return catalog;
                }

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                dto = deserializer.Deserialize<WorkspaceProfilesYamlDto>(yaml);
            }
            catch (Exception ex)
            {
                catalog.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "profiles/yaml/malformed",
                    "profiles.yml contains malformed YAML: " + ex.Message));
                return catalog;
            }

            if (dto == null)
            {
                return catalog;
            }

            if (dto.SchemaVersion != CurrentSchemaVersion)
            {
                catalog.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "profiles/schema/unsupported",
                    "profiles.yml must use schema_version 1."));
            }

            foreach (var item in dto.Profiles ?? new List<WorkspaceProfileYamlDto>())
            {
                var profile = new WorkspaceProfile
                {
                    Name = item?.Name ?? string.Empty
                };

                if (item != null && Guid.TryParse(item.Id, out var id))
                {
                    profile.Id = id;
                }

                foreach (var member in item?.Members ?? new List<string>())
                {
                    profile.Members.Add(member ?? string.Empty);
                }

                catalog.Profiles.Add(profile);
            }

            foreach (var issue in WorkspaceProfilePolicy.ValidateDefinitions(
                catalog.Profiles,
                requireAtLeastOne: false))
            {
                catalog.Issues.Add(issue);
            }

            return catalog;
        }

        public void SaveProfiles(IReadOnlyList<WorkspaceProfile> profiles)
        {
            var issues = WorkspaceProfilePolicy.ValidateDefinitions(
                    profiles,
                    requireAtLeastOne: true)
                .Where(issue => issue.Severity == IssueSeverity.Error)
                .ToList();
            if (issues.Count > 0)
            {
                throw new WorkspaceProfileValidationException(issues);
            }

            var directory = Path.GetDirectoryName(_profilesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            builder.AppendLine("# Developer Control Tower workspace profiles");
            builder.AppendLine("schema_version: 1");
            builder.AppendLine("profiles:");

            foreach (var profile in profiles)
            {
                builder.AppendLine("  - id: " + YamlScalar.Quote(profile.Id.ToString("D")));
                builder.AppendLine("    name: " + YamlScalar.Quote(profile.Name.Trim()));
                builder.AppendLine("    members:");
                foreach (var member in profile.Members
                    .Select(value => value.Trim())
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine("      - " + YamlScalar.Quote(member));
                }
            }

            WriteAtomic(_profilesPath, builder.ToString());
        }

        private static void WriteAtomic(string destinationPath, string content)
        {
            var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                File.Move(tempPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }
}
