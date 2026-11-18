namespace AgentBridge.Quality.Tests;

public class DocumentationPolicyTests
{
    [Fact]
    public void Tester_guide_is_reachable_from_the_repository_docs_index()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var index = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("](docs/tester-guide.md)", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Tester_guide_relative_links_resolve()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var path = Path.Combine(root, "docs", "tester-guide.md");
        Assert.True(File.Exists(path), "The first-draft tester guide must exist.");
        var text = File.ReadAllText(path);
        var links = System.Text.RegularExpressions.Regex.Matches(text, @"\[[^\]]+\]\(([^)]+)\)");
        Assert.NotEmpty(links);

        foreach (System.Text.RegularExpressions.Match link in links)
        {
            var target = link.Groups[1].Value;
            if (Uri.TryCreate(target, UriKind.Absolute, out _))
                continue;

            var parts = target.Split('#', 2);
            var destination = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, parts[0]));
            Assert.True(File.Exists(destination), $"Broken tester guide link: {target}");
            if (parts.Length == 2)
            {
                var headings = File.ReadLines(destination)
                    .Where(line => line.StartsWith('#'))
                    .Select(line => line.TrimStart('#', ' ').Trim().ToLowerInvariant().Replace(' ', '-'));
                Assert.Contains(parts[1], headings);
            }
        }
    }

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
