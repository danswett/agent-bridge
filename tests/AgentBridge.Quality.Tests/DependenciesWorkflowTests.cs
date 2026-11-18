using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

public class DependenciesWorkflowTests
{
    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);

    [Theory]
    [InlineData("README.md", false)]
    [InlineData("docs/guide.md", false)]
    [InlineData("src/Example/Program.cs", false)]
    [InlineData("pwa/js/main.js", false)]
    [InlineData("Directory.Packages.props", true)]
    [InlineData("src/Directory.Build.targets", true)]
    [InlineData("src/Example/Example.csproj", true)]
    [InlineData("Example.fsproj", true)]
    [InlineData("AgentBridge.slnx", true)]
    [InlineData("Example.sln", true)]
    [InlineData("nuget.config", true)]
    [InlineData("config/NuGet.Config", true)]
    [InlineData("global.json", true)]
    [InlineData(".config/dotnet-tools.json", true)]
    [InlineData("src/Example/packages.lock.json", true)]
    [InlineData("src/Example/packages.config", true)]
    [InlineData("package.json", true)]
    [InlineData("pwa/package.json", true)]
    [InlineData("package-lock.json", true)]
    [InlineData("pwa/package-lock.json", true)]
    [InlineData("tools/example/npm-shrinkwrap.json", true)]
    [InlineData("pwa/.npmrc", true)]
    [InlineData("apple/Package.swift", true)]
    [InlineData("apple/Package.resolved", true)]
    [InlineData(".github/workflows/dependencies.yml", true)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData(".github/actions/example/action.yml", true)]
    [InlineData(".github/dependabot.yml", true)]
    public void Only_dependency_inputs_select_the_workflow(string path, bool expected)
    {
        var pullRequest = (YamlMappingNode)((YamlMappingNode)Workflow()["on"])["pull_request"];
        var selected = !pullRequest.Children.TryGetValue("paths", out var paths)
            || ((YamlSequenceNode)paths).Children.Any(pattern => new Glob(pattern.ToString()).IsMatch(path));
        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Drafts_still_skip_review_and_ready_for_review_can_run_it(bool draft)
    {
        var workflow = Workflow();
        var trigger = (YamlMappingNode)((YamlMappingNode)workflow["on"])["pull_request"];
        Assert.Contains("ready_for_review", ((YamlSequenceNode)trigger["types"]).Children.Select(n => n.ToString()));
        var review = (YamlMappingNode)((YamlMappingNode)workflow["jobs"])["review"];
        var condition = review["if"].ToString().Replace("${{", "").Replace("}}", "")
            .Replace("github.event.pull_request.draft", draft ? "true" : "false", StringComparison.Ordinal);
        Assert.Equal(!draft, MergeRuleTests.IfExpression.Evaluate(condition, new Dictionary<string, string?>()));
    }

    [Theory]
    [InlineData(null, "success")]
    [InlineData("success", "success")]
    [InlineData("pending", "pending")]
    [InlineData("failure", "failure")]
    public void Dependencies_may_be_absent_but_any_existing_run_must_pass(string? state, string expected)
    {
        var config = MergeRuleConfig.Load(Path.Combine(Root, "quality", "merge-rule.yml"));
        var receipt = new StatusFact("independent-review", "success", "CLEAN gpt-6.1-sol", "danswett", DateTimeOffset.UnixEpoch);
        var pr = new PullRequestFacts(42, "OPEN", false, "main", "abcdef0123456789abcdef0123456789abcdef01",
            [], ["docs/guide.md"], true, [], [receipt], new("COMPLETED", "SUCCESS"), null)
        {
            Checks = state is null ? [] : [new("Dependencies / review", state, state)],
        };
        Assert.Equal(expected, MergeRule.Evaluate(config, new("success", "main passed"), pr)!.State);
        var pages = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Root, "quality", "pages-jobs.json")))!;
        Assert.DoesNotContain(pages, job => job.Contains("dependencies", StringComparison.OrdinalIgnoreCase) || job == "review");
    }

    static YamlMappingNode Workflow()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(Root, ".github", "workflows", "dependencies.yml"))));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }
}
