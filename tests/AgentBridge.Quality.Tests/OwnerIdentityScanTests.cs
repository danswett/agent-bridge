using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// Part of #4241: a git identity or personal address on a command line trips a hidden privacy
// prompt that stalls the session, and git identity is already configured on the hosts. No tracked
// file may carry a personal-mail address, or the owner's name inside a git identity (-c user.*,
// config user.*, GIT_*_NAME/EMAIL, --author). Fixture identities are fine. The patterns are
// assembled from pieces so this file never holds what it bans.
public class OwnerIdentityScanTests
{
    static readonly Regex Mail = new(@"[\w.+-]+@(" + string.Join('|', "gmail", "outlook", "hotmail", "live", "yahoo", "icloud", "proton") + @")\.(com|me)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Identity = new(@"user\.(name|email)|GIT_(AUTHOR|COMMITTER)_(NAME|EMAIL)|--author", RegexOptions.CultureInvariant);
    static readonly Regex Owner = new(@"dan\s*" + "swett|swett", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool Hit(string line) => Mail.IsMatch(line) || (Identity.IsMatch(line) && Owner.IsMatch(line));

    [Theory]
    [InlineData("git -c user.email=<addr> commit")]
    [InlineData("git -c user.name=<first> <last> commit")]
    [InlineData("$env:GIT_AUTHOR_NAME = '<first> <last>'")]
    [InlineData("notify <addr>")]
    public void Detector_flags_the_owner_identity(string template)
    {
        var line = template.Replace("<addr>", "dan" + "swett@" + "gmail.com").Replace("<first>", "Dan").Replace("<last>", "Swett");
        Assert.True(Hit(line));
    }

    [Theory]
    [InlineData("git -c user.name=t -c user.email=t@example.com commit")]
    [InlineData("-c user.email=41898282+github-actions[bot]@users.noreply.github.com")]
    [InlineData("Copyright (c) 2026 Dan Swett. Licensed under the AGPL")]
    public void Detector_allows_fixtures_and_plain_prose(string line) => Assert.False(Hit(line));

    [Fact]
    public void Repo_scan_finds_no_owner_identity()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var hits = ChangedFiles.Tracked(root)
            .Where(path => File.Exists(Path.Combine(root, path)) && File.ReadAllText(Path.Combine(root, path)) is var text
                && !text.Contains('\0') && text.Split('\n').Any(Hit))
            .ToList();
        Assert.True(hits.Count == 0, "owner identity in: " + string.Join(", ", hits));
    }

    [Fact]
    public void Agent_rules_state_the_plain_commit_rule() =>
        Assert.Contains("plain `git commit`", File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "AGENTS.md")));
}
