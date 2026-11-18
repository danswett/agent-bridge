namespace AgentBridge.Quality.Tests;

public class DocumentationPolicyTests
{
    [Fact]
    public void Documentation_uses_roster_roles_or_neutral_examples_not_fleet_machine_names()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var paths = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "spikes"), "RESULTS.md", SearchOption.AllDirectories))
            .Concat(new[] { "quality/README.md", "pwa/README.md", "infra/cloudflare/release-feed/README.md" }
                .Select(path => Path.Combine(root, path)));
        var names = new System.Text.RegularExpressions.Regex("VM[1-9]|DSWETT-|ARM8",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var violations = paths
            .Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/')
                .StartsWith("docs/protocol/vectors/", StringComparison.Ordinal))
            .Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/')
                .StartsWith("docs/protocol/examples/", StringComparison.Ordinal))
            .Where(path => names.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path));

        Assert.Empty(violations);
    }

    [Fact]
    public void Status_links_to_the_relay_roster_instead_of_keeping_machine_rows()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var status = File.ReadAllText(Path.Combine(root, "docs", "status.md"));
        var machines = status.Split("### Machines", 2)[1].Split("\n### ", 2)[0];

        Assert.Contains("](protocol/roster.md)", machines, StringComparison.Ordinal);
        Assert.DoesNotContain("|", machines, StringComparison.Ordinal);
    }

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
