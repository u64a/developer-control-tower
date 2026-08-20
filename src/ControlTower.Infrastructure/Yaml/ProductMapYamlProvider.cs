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
    public sealed class ProductMapYamlProvider : IProductMapProvider
    {
        public ProductMapLoadResult LoadProductMap(string projectRootPath, string sourceRef)
        {
            var result = new ProductMapLoadResult();

            if (string.IsNullOrWhiteSpace(sourceRef))
            {
                sourceRef = ".controltower\\product-map.yml";
            }

            var filePath = Path.IsPathRooted(sourceRef)
                ? sourceRef
                : Path.GetFullPath(Path.Combine(projectRootPath, sourceRef));

            if (!File.Exists(filePath))
            {
                result.Issues.Add(new ValidationIssue(IssueSeverity.Warning, "Missing product-map.yml"));
                return result;
            }

            ProductMapYamlDto dto = null;
            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                dto = deserializer.Deserialize<ProductMapYamlDto>(yaml);
            }
            catch (Exception)
            {
                result.Issues.Add(new ValidationIssue(IssueSeverity.Error, "product-map.yml contains malformed YAML"));
                return result;
            }

            if (dto == null)
            {
                result.Issues.Add(new ValidationIssue(IssueSeverity.Error, "product-map.yml must contain exactly one product node"));
                return result;
            }

            if (!string.IsNullOrWhiteSpace(dto.ProjectId))
            {
                result.Summary.ProjectId = dto.ProjectId;
            }

            if (!string.IsNullOrWhiteSpace(dto.PlanningAuthority))
            {
                result.Summary.PlanningAuthority = dto.PlanningAuthority;
            }

            var nodes = new List<ProductNode>();
            if (dto.Nodes != null)
            {
                foreach (var nodeDto in dto.Nodes)
                {
                    var node = new ProductNode
                    {
                        Id = nodeDto.Id ?? string.Empty,
                        Type = nodeDto.Type ?? string.Empty,
                        Title = nodeDto.Title ?? string.Empty,
                        ParentId = nodeDto.ParentId ?? string.Empty,
                        Status = nodeDto.Status ?? string.Empty,
                        Description = nodeDto.Description ?? string.Empty
                    };

                    if (nodeDto.ExternalRef != null)
                    {
                        node.ExternalSystem = nodeDto.ExternalRef.System ?? string.Empty;
                        node.ExternalId = nodeDto.ExternalRef.Id ?? string.Empty;
                        node.ExternalUrl = nodeDto.ExternalRef.Url ?? string.Empty;
                    }

                    nodes.Add(node);
                }
            }
            var productNodes = nodes.Where(node => node.Type == "product").ToList();
            if (productNodes.Count != 1)
            {
                result.Issues.Add(new ValidationIssue(IssueSeverity.Error, "product-map.yml must contain exactly one product node"));
            }

            if (productNodes.Count > 0)
            {
                result.Summary.ProductTitle = productNodes[0].Title;
                var rootId = productNodes[0].Id;
                foreach (var initiative in nodes.Where(node => node.Type == "initiative" && node.ParentId == rootId))
                {
                    result.Summary.TopLevelInitiatives.Add(initiative.Title);
                }
            }

            return result;
        }
    }
}
