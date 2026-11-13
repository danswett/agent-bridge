using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

public class ValidationPlanTests
{
    [Theory]
    [InlineData("")]
    [InlineData("validation_plan:")]
    [InlineData("validation_plan: unknown")]
    [InlineData("validation_plan: Native")]
    public void Missing_or_unknown_validation_plans_fail_the_catalog_check(string field)
    {
        var catalog = Catalog.Parse(Yaml(field));

        var problem = Assert.Single(catalog.Validate(_ => 1));

        Assert.Equal("schema", problem.Kind);
        Assert.Contains("validation_plan", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("ha-compatibility")]
    public void Known_validation_plans_are_valid_and_reported_without_changing_selection(string plan)
    {
        Assert.Empty(Catalog.Parse(Yaml($"validation_plan: {plan}")).Validate(_ => 1));
        var baseline = Fixture.Catalog();
        var tagged = Catalog.Parse(Regex.Replace(Fixture.CatalogYaml,
            @"(?m)^    validation_plan:.*$", $"    validation_plan: {plan}"));
        foreach (var ring in new[] { 0, 1, 2, 4 })
        {
            string Report(Catalog catalog) => Program.ToJson(new Selector(catalog, Fixture.Graph())
                .Select(["src/HostCore/Parser.cs", "apple/Sources/App.swift"], ring, "win-x64"), "fixed fixture");
            var before = JsonNode.Parse(Report(baseline))!.AsObject();
            var after = JsonNode.Parse(Report(tagged))!.AsObject();
            foreach (var group in new[] { "suites", "skipped" })
            {
                foreach (var suite in after[group]!.AsArray())
                {
                    var row = suite!.AsObject();
                    Assert.True(row.ContainsKey("validationPlan"), "The suite report has no validationPlan.");
                    Assert.Equal(plan, row["validationPlan"]!.GetValue<string>());
                    row.Remove("validationPlan");
                }
                foreach (var suite in before[group]!.AsArray())
                    suite!.AsObject().Remove("validationPlan");
            }
            Assert.Equal(before.ToJsonString(), after.ToJsonString());
        }
    }

    static string Yaml(string field) => $"""
        suites:
          - id: fixture
            kind: unit
            ring: 0
            select: always
            platforms: [win-x64]
            runner: local
            status: planned
            {field}
        """;
}
