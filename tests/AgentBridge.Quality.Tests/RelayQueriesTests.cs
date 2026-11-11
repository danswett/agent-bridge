using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// infra/modules/saved-searches.bicep deploys each "// ==== <id>: <description>" block of
// ops/queries/relay.kql as a saved search, named by its id. A block it can't deploy would fail
// the Azure deployment, which only the owner runs, so this catches it in CI. A marker is at the
// start of a line: the file's header quotes one mid-line.
public class RelayQueriesTests
{
    const string Marker = "// ==== ";

    static readonly Regex KebabId = new("^[a-z0-9]+(-[a-z0-9]+)*$");

    [Fact]
    public void The_real_relay_queries_can_all_be_deployed_as_saved_searches()
    {
        var path = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "ops", "queries", "relay.kql");

        var problems = Problems(File.ReadAllText(path));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void A_well_formed_file_has_no_problems()
    {
        Assert.Empty(Problems("// header\n\n// ==== a-1: first\nT\n| take 1\n\n// ==== b: second\nU\n"));
    }

    [Theory]
    [InlineData("// ==== a: one\nT\n// ==== a: two\nU\n", "repeated")]
    [InlineData("// ==== : one\nT\n", "empty id")]
    [InlineData("// ==== Relay_Lines: one\nT\n", "kebab")]
    [InlineData("// ==== a: one\n// just a comment\n\n", "no query")]
    [InlineData("// ==== a: one", "no query")]
    [InlineData("// ==== a no colon\nT\n", "description")]
    [InlineData("// ==== a:   \nT\n", "description")]
    public void A_block_that_cannot_become_a_saved_search_is_reported(string kql, string expected)
    {
        Assert.Contains(Problems(kql), p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    static List<string> Problems(string kql)
    {
        var problems = new List<string>();
        var blocks = ("\n" + kql.Replace("\r", "")).Split("\n" + Marker).Skip(1).ToList();
        if (blocks.Count == 0) problems.Add("no '// ==== ' blocks found");

        var seen = new HashSet<string>();
        foreach (var block in blocks)
        {
            var newline = block.IndexOf('\n');
            var first = newline < 0 ? block : block[..newline];
            var body = newline < 0 ? "" : block[(newline + 1)..];

            var colon = first.IndexOf(':');
            if (colon < 0)
            {
                problems.Add($"block '{first.Trim()}' has no ': description' on its first line");
                continue;
            }

            var id = first[..colon].Trim();
            if (id.Length == 0) problems.Add($"empty id in block '{first.Trim()}'");
            else if (!KebabId.IsMatch(id)) problems.Add($"id '{id}' is not lower-case kebab");
            else if (!seen.Add(id)) problems.Add($"id '{id}' is repeated");

            if (first[(colon + 1)..].Trim().Length == 0) problems.Add($"block '{id}' has an empty description");

            // Lines starting with // are comments, so a block of only those is no query.
            var hasQuery = body.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//"));
            if (!hasQuery) problems.Add($"block '{id}' has no query");
        }
        return problems;
    }
}
