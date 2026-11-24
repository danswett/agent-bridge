using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// Part of #4241: a git identity or personal address on a command line trips a hidden privacy
// prompt that stalls the session, and git identity is already configured on the hosts. No tracked
// file may carry a personal-mail address, or the owner's name inside a git identity (-c user.*,
// config user.*, GIT_*_NAME/EMAIL, --author). Fixture identities are fine. The patterns are
// assembled from pieces so this file never holds what it bans.
public class OwnerIdentityScanTests
{
    static readonly Regex Mail = new(@"[\w.+-]+@(" + string.Join('|', "gmail", "outlook", "hotmail", "live", "yahoo", "icloud", "proton", "protonmail", "googlemail") + @")(\.[a-z]{2,3}){1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Identity = new(@"user\.(name|email)|GIT_(AUTHOR|COMMITTER)_(NAME|EMAIL)|--author", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Owner = new(@"dan\s*" + "swett|swett", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool Hit(string line) => Mail.IsMatch(line) || (Identity.IsMatch(line) && Owner.IsMatch(line));

    [Theory]
    [InlineData("git -c user.email=<addr> commit")]
    [InlineData("git -c user.name=<first> <last> commit")]
    [InlineData("$env:GIT_AUTHOR_NAME = '<first> <last>'")]
    [InlineData("notify <addr>")]
    [InlineData("git commit --author=\"<first> <last> <addr>\"")]
    [InlineData("$env:git_author_name = '<first> <last>'")]
    [InlineData("-c user.Name=<first> <last>")]
    // Only the owner's name and the flag: with --author dropped from the context pattern nothing else catches these.
    [InlineData("git commit --author=\"<first> <last> <noreply@example.com>\"")]
    [InlineData("git commit --author \"<last>, <first>\"")]
    [InlineData("notify dan" + "swett@" + "hotmail.co.uk")]
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

    // The rehearsal script refuses without the sign-in it belongs to, and the owner runs it (#4273 review): the soak that calls it must pass it
    // on, or -Prepare, -Start and -Finish stop at the guard (-Finish would leave both VMs billing), and the owner's docs must say how to set it.
    [Fact]
    public void The_soak_resolves_the_azure_user_passes_it_to_every_rehearsal_call_and_has_no_owner_step()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var soak = File.ReadAllLines(Path.Combine(root, "build", "shadow-soak-vm.ps1"));
        var calls = soak.Where(line => line.Contains("& $vmScript", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, line => Assert.Contains("@who", line));
        // From the environment only (#4241): no soak parameter takes the address, so no command line has to carry it.
        Assert.Contains(soak, line => line.Contains("$who = @{ ExpectedAzureUser = (& (Join-Path $PSScriptRoot 'resolve-rehearsal-azure-user.ps1')", StringComparison.Ordinal));
        Assert.DoesNotContain(soak, line => line.Contains("[string] $ExpectedAzureUser", StringComparison.Ordinal));
    }

    // The owner's plain, one-time step is in Windows settings; no doc asks the owner to type the address into a command (#4241).
    [Theory]
    [InlineData("docs/parity-checklist.md")]
    [InlineData("docs/host-shadow.md")]
    [InlineData("docs/status.md")]
    public void The_operator_docs_name_the_variable_and_never_ask_for_the_address_in_a_command(string doc)
    {
        var text = File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), doc));
        Assert.Contains("AGENT_BRIDGE_REHEARSAL_AZURE_USER", text);
        Assert.DoesNotContain("-ExpectedAzureUser", text);
        Assert.DoesNotContain("SetEnvironmentVariable", text);
    }

    [Fact]
    public void The_parity_checklist_tells_the_owner_the_one_time_step_in_windows_settings()
    {
        var text = File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "docs", "parity-checklist.md"));
        Assert.Contains("Edit environment variables for your account", text);
    }

    // The scan reads every tracked text file. Mutants that narrowed it (src/ only, AGENTS.md left out) survived while the scan was one
    // anonymous query; the files it read are now named, and this holds each area to it.
    static (List<string> Scanned, List<string> Hits) Scan(string root)
    {
        var scanned = new List<string>();
        var hits = new List<string>();
        foreach (var path in ChangedFiles.Tracked(root))
        {
            var full = Path.Combine(root, path);
            if (!File.Exists(full)) { continue; }
            var text = File.ReadAllText(full);
            if (text.Contains('\0')) { continue; }
            scanned.Add(path);
            if ((Mail.IsMatch(text) || Identity.IsMatch(text)) && text.Split('\n').Any(Hit)) { hits.Add(path); }
        }
        return (scanned, hits);
    }

    [Fact]
    public void The_scan_reads_every_area_of_the_repository_and_the_agent_rules_not_a_subtree()
    {
        var scanned = Scan(ChangedFiles.RepoRoot(AppContext.BaseDirectory)).Scanned;
        Assert.Contains("AGENTS.md", scanned);
        Assert.Contains("build/rehearsal-azure-vmw.ps1", scanned);
        Assert.Contains("build/shadow-soak-vm.ps1", scanned);
        Assert.Contains("tests/AgentBridge.Quality.Tests/OwnerIdentityScanTests.cs", scanned);
        foreach (var area in new[] { "src/", "tests/", "build/", "docs/", "pwa/", "tools/", "quality/", ".github/" })
        {
            Assert.Contains(scanned, path => path.StartsWith(area, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Repo_scan_finds_no_owner_identity()
    {
        var hits = Scan(ChangedFiles.RepoRoot(AppContext.BaseDirectory)).Hits;
        Assert.True(hits.Count == 0, "owner identity in: " + string.Join(", ", hits));
    }

    [Fact]
    public void Agent_rules_state_the_plain_commit_rule() =>
        Assert.Contains("plain `git commit`", File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "AGENTS.md")));
}
