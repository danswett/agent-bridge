namespace AgentBridge.Quality.Tests;

public class DocumentationPolicyTests
{
    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("docs/status.md")]
    public void Status_is_a_weekly_Product_snapshot_not_a_per_PR_requirement(string path)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var text = File.ReadAllText(Path.Combine(root, path));
        var policy = path == "AGENTS.md"
            ? text[text.IndexOf("## Style", StringComparison.Ordinal)..]
            : text.Split("\n## ", 2)[0];

        Assert.Contains("weekly snapshot by Product", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not required in each PR", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_policy_does_not_keep_the_superseded_per_merge_instructions()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        var status = File.ReadAllText(Path.Combine(root, "docs", "status.md"));

        Assert.DoesNotContain("When a merge changes the project's state, update", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep this page current when a change moves the project along", status, StringComparison.Ordinal);
    }
}
