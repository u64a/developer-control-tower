using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

#pragma warning disable CS0618 // PlanningBoard types are V0-only per ADR-001; tests exercise the parser.

public class PlanningBoardYamlProviderTests
{
    [Fact]
    public void ParseFromContent_MalformedYaml_AddsValidationIssue()
    {
        var provider = new PlanningBoardYamlProvider();
        // YAML with an unterminated quote — pure malformed.
        var bad = "roadmap:\n  product:\n    name: \"unterminated\n";

        var result = provider.ParseFromContent(bad, "test/roadmap.yaml");

        Assert.NotEmpty(result.Issues);
        Assert.Contains(result.Issues, i => i.Code == "planning/yaml/malformed");
    }

    [Fact]
    public void ParseFromContent_WellFormedYaml_NoIssues()
    {
        var provider = new PlanningBoardYamlProvider();
        var ok = @"roadmap:
  product:
    name: Demo
";
        var result = provider.ParseFromContent(ok, "test/roadmap.yaml");
        Assert.Empty(result.Issues);
    }
}

#pragma warning restore CS0618
