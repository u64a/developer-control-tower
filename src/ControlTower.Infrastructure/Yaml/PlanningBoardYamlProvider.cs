using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;
using YamlDotNet.RepresentationModel;

namespace ControlTower.Infrastructure.Yaml
{
    public sealed class PlanningBoardYamlProvider : IPlanningBoardProvider
    {
        public PlanningBoardLoadResult LoadPlanningBoard(string projectRootPath)
        {
            // Try .github\roadmap.yaml first, then resources\roadmap.yaml
            var candidates = new[]
            {
                Path.Combine(projectRootPath, ".github", "roadmap.yaml"),
                Path.Combine(projectRootPath, "resources", "roadmap.yaml"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    var content = File.ReadAllText(candidate);
                    var label = Path.GetRelativePath(projectRootPath, candidate);
                    return ParseFromContent(content, label);
                }
            }
            return new PlanningBoardLoadResult();
        }

        public PlanningBoardLoadResult ParseFromContent(string yamlContent, string sourceLabel)
        {
            var result = new PlanningBoardLoadResult();
            if (string.IsNullOrWhiteSpace(yamlContent))
            {
                return result;
            }

            result.Summary.Source = sourceLabel ?? string.Empty;

            try
            {
                var yaml = new YamlStream();
                using (var reader = new StringReader(yamlContent))
                {
                    yaml.Load(reader);
                }

                if (yaml.Documents.Count == 0)
                {
                    return result;
                }

                var root = yaml.Documents[0].RootNode as YamlMappingNode;
                if (root == null)
                {
                    return result;
                }

                var roadmapNode = GetMapping(root, "roadmap");
                if (roadmapNode == null)
                {
                    return result;
                }

                var productNode = GetMapping(roadmapNode, "product");
                if (productNode != null)
                {
                    result.Summary.Title = GetScalar(productNode, "name");

                    var mission = GetScalar(productNode, "primary_mission");
                    if (!string.IsNullOrWhiteSpace(mission) && mission != ">")
                    {
                        result.Summary.Summary = mission;
                    }
                }

                // v2.1: prefer current_state.current_focus prose for the summary line
                // because it describes what is happening right now rather than the
                // static product mission.
                var currentStateNode = GetMapping(roadmapNode, "current_state");
                if (currentStateNode != null)
                {
                    var currentFocus = GetScalar(currentStateNode, "current_focus");
                    if (!string.IsNullOrWhiteSpace(currentFocus) && currentFocus != ">")
                    {
                        result.Summary.Summary = currentFocus;
                    }
                }

                var wavesNode = GetSequence(roadmapNode, "waves");
                if (wavesNode != null)
                {
                    foreach (var waveItem in wavesNode.Children)
                    {
                        var waveMap = waveItem as YamlMappingNode;
                        if (waveMap == null)
                        {
                            continue;
                        }

                        var wave = new PlanningNodeSummary
                        {
                            Id = GetScalar(waveMap, "id"),
                            Title = GetScalar(waveMap, "name"),
                            Status = GetScalar(waveMap, "status")
                        };

                        var objective = GetScalar(waveMap, "objective");
                        if (!string.IsNullOrWhiteSpace(objective) && objective != ">")
                        {
                            wave.Subtitle = objective;
                        }

                        var featuresNode = GetSequence(waveMap, "features");
                        if (featuresNode != null)
                        {
                            foreach (var featureItem in featuresNode.Children)
                            {
                                var featureMap = featureItem as YamlMappingNode;
                                if (featureMap == null)
                                {
                                    continue;
                                }

                                var feature = new PlanningNodeSummary
                                {
                                    Id = GetScalar(featureMap, "id"),
                                    Title = GetScalar(featureMap, "name"),
                                    Status = GetScalar(featureMap, "status"),
                                    Subtitle = GetScalar(featureMap, "priority")
                                };

                                var workItemsNode = GetSequence(featureMap, "work_items");
                                if (workItemsNode != null)
                                {
                                    foreach (var wiItem in workItemsNode.Children)
                                    {
                                        var wiMap = wiItem as YamlMappingNode;
                                        if (wiMap == null)
                                        {
                                            continue;
                                        }

                                        var workItem = new PlanningNodeSummary
                                        {
                                            Id = GetScalar(wiMap, "id"),
                                            Title = GetScalar(wiMap, "title"),
                                            Subtitle = GetScalar(wiMap, "area")
                                        };

                                        feature.Children.Add(workItem);
                                    }
                                }

                                wave.Children.Add(feature);
                            }
                        }

                        result.Summary.Nodes.Add(wave);
                    }
                }
            }
            catch (Exception ex)
            {
                // M1: surface parse errors instead of returning a silent
                // empty board. Callers should treat a malformed roadmap as a
                // visible failure, not as "no plan".
                result.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "planning/yaml/malformed",
                    "roadmap.yaml contains malformed YAML: " + ex.Message));
            }

            if (string.IsNullOrWhiteSpace(result.Summary.Title))
            {
                result.Summary.Title = "Planning board";
            }

            if (string.IsNullOrWhiteSpace(result.Summary.Summary))
            {
                var activeWave = result.Summary.Nodes.FirstOrDefault(node => node.Status == "in_progress");
                result.Summary.Summary = activeWave == null
                    ? result.Summary.Nodes.Count + " waves available"
                    : "Current focus: " + activeWave.Title;
            }

            return result;
        }

        private static YamlMappingNode GetMapping(YamlMappingNode parent, string key)
        {
            YamlNode value;
            if (parent.Children.TryGetValue(new YamlScalarNode(key), out value))
            {
                return value as YamlMappingNode;
            }

            return null;
        }

        private static YamlSequenceNode GetSequence(YamlMappingNode parent, string key)
        {
            YamlNode value;
            if (parent.Children.TryGetValue(new YamlScalarNode(key), out value))
            {
                return value as YamlSequenceNode;
            }

            return null;
        }

        private static string GetScalar(YamlMappingNode parent, string key)
        {
            YamlNode value;
            if (parent.Children.TryGetValue(new YamlScalarNode(key), out value))
            {
                var scalar = value as YamlScalarNode;
                if (scalar != null && scalar.Value != null && scalar.Value != "null")
                {
                    return scalar.Value;
                }
            }

            return string.Empty;
        }
    }
}
