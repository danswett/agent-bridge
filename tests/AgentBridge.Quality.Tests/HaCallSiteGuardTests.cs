using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// HA retirement, "no new HA call sites" for this repo (Part of #1396; the design of the fleet's gate, slipgates-fleet #726).
// Product and tooling code (not tests, docs or fixtures) that reaches Home Assistant, or hands agents its address, must be in
// ha-callsite-allowlist.json with the slice that retires it. A new site fails here, and so does an allowance with nothing left in it,
// so the list only shrinks. The connector project is one pinned directory entry: its file count must match, so a new file in it fails too.
public class HaCallSiteGuardTests
{
    const string AllowlistPath = "tests/AgentBridge.Quality.Tests/ha-callsite-allowlist.json";

    internal sealed record Allowance(string? Path, string? Directory, int? Files, string? Slice, string? Issue, string? Reason);

    // HA access as text: the agent credential and address variables, HA's API paths, its client types, the MQTT and notify services
    // the connector drives, and the host call that starts the connector. Variable names and API paths match in any case (PowerShell and
    // Windows environment names are case-insensitive); client type names match with a prefix or suffix (IHomeAssistantClient, Retrying...Client).
    static readonly Regex Access = new(
        @"(?i:AGENT_HA_AGENT_TOKEN|AGENT_HA_URL|FLEET_HA_(URL|TOKEN)|/api/(states|services|websocket|image/serve)\b)|\w*HomeAssistantClient\b|\bIHomeAssistant\w*"
        + @"|mqtt\.publish|mqtt/subscribe|mobile_app_notification_action|notify\.mobile_app|persistent_notification|\bHaRunning\.Start\b",
        RegexOptions.CultureInvariant);
    static readonly Regex TestFileName = new(@"(^|/)(test[-_][^/]*|[^/]*\.tests?\.[^/]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly string[] Extensions = [".cs", ".ps1", ".psm1", ".py", ".js", ".mjs", ".ts", ".sh"];
    static readonly string[] NotProduct = ["tests/", "docs/", "design/", "spikes/", "backlog/"];
    static readonly string[] NotCode = ["fixtures", "bin", "obj", "node_modules"];
    const string Connector = "src/AgentBridge.HomeAssistant/";

    static bool InScope(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        && !NotProduct.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))
        && !path.Split('/').Any(part => NotCode.Contains(part, StringComparer.OrdinalIgnoreCase))
        && !TestFileName.IsMatch(path);

    // The code lines of a file: not blank lines, not // or # comment lines, and nothing inside a block comment (<# #> or /* */) that starts a line.
    static IEnumerable<string> CodeLines(string text)
    {
        string? end = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            if (end is not null)
            {
                var close = line.IndexOf(end, StringComparison.Ordinal);
                if (close < 0) { continue; }
                line = line[(close + end.Length)..];
                end = null;
            }
            var trimmed = line.TrimStart();
            var open = trimmed.StartsWith("<#", StringComparison.Ordinal) ? "#>" : trimmed.StartsWith("/*", StringComparison.Ordinal) ? "*/" : null;
            if (open is not null)
            {
                var close = trimmed.IndexOf(open, 2, StringComparison.Ordinal);
                if (close < 0) { end = open; continue; }
                line = trimmed[(close + open.Length)..];
                trimmed = line.TrimStart();
            }
            if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('#')) { yield return line; }
        }
    }

    // The paths that are HA call sites: the connector project (every file in it) and any other file with a code line naming HA access.
    internal static SortedSet<string> Sites(IReadOnlyDictionary<string, string> files) =>
        new(files.Where(file => InScope(file.Key) && (file.Key.StartsWith(Connector, StringComparison.Ordinal)
            || CodeLines(file.Value).Any(Access.IsMatch))).Select(file => file.Key), StringComparer.Ordinal);

    internal static (string[] New, string[] Stale) Check(IReadOnlyDictionary<string, string> files, IReadOnlyList<Allowance> allowances)
    {
        var sites = Sites(files);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var allowance in allowances)
        {
            if (allowance.Directory is { } directory)
            {
                var inside = sites.Where(site => site.StartsWith(directory, StringComparison.Ordinal)).ToList();
                if (inside.Count != allowance.Files) { stale.Add($"{directory} holds {inside.Count} files, the allowance says {allowance.Files}"); }
                covered.UnionWith(inside);
            }
            else if (allowance.Path is { } path && sites.Contains(path)) { covered.Add(path); }
            else { stale.Add(allowance.Path ?? "(entry with no path)"); }
        }
        return ([.. sites.Where(site => !covered.Contains(site))], [.. stale]);
    }

    static IReadOnlyDictionary<string, string> Read(string root) =>
        ChangedFiles.Tracked(root).Where(InScope).Where(path => File.Exists(Path.Combine(root, path)))
            .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(root, path)), StringComparer.Ordinal);

    static IReadOnlyList<Allowance> Allowances(string root) =>
        JsonSerializer.Deserialize<Allowance[]>(File.ReadAllText(Path.Combine(root, AllowlistPath)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void The_repo_has_no_HA_call_site_outside_the_allowlist_and_no_allowance_is_stale()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var (added, stale) = Check(Read(root), Allowances(root));
        Assert.True(added.Length == 0, "New HA call site (retire it, or allowlist it with its slice and the #1396 issue): " + string.Join(", ", added));
        Assert.True(stale.Length == 0, "Allowance with nothing left in it (remove it): " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_allowance_names_a_slice_a_reason_and_an_agent_bridge_issue()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var allowances = Allowances(root);
        Assert.NotEmpty(allowances);
        Assert.All(allowances, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Slice) || string.IsNullOrWhiteSpace(a.Reason), $"{a.Path ?? a.Directory} needs a Slice and a Reason");
            Assert.Matches(@"^https://github\.com/danswett/agent-bridge/issues/\d+$", a.Issue ?? "");
            Assert.True((a.Path is null) != (a.Directory is null), "an allowance is a Path or a Directory");
            Assert.True(a.Directory is null || a.Files > 0, "a Directory allowance pins its file count");
        });
    }

    static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);
    static void AssertClean((string[] New, string[] Stale) result) { Assert.Empty(result.New); Assert.Empty(result.Stale); }
    static Allowance Allow(string path) => new(path, null, null, "S7", "https://github.com/danswett/agent-bridge/issues/1396", "known");

    [Fact]
    public void A_new_file_that_reaches_HA_fails_and_an_allowlisted_one_passes()
    {
        var files = Files(("tools/new/poke.ps1", "Invoke-RestMethod -Uri \"$base/api/states\""), ("src/Other/Clean.cs", "var x = 1;"));
        Assert.Equal(["tools/new/poke.ps1"], Check(files, []).New);
        AssertClean(Check(files, [Allow("tools/new/poke.ps1")]));
    }

    [Theory]
    [InlineData("$token = $env:AGENT_HA_AGENT_TOKEN")]
    [InlineData("var c = new HomeAssistantClient(url, token, http);")]
    [InlineData("await Publish(\"mqtt.publish\", payload);")]
    [InlineData("Start-Sleep 1; $r = irm \"$u/API/Services/script/x\"")]
    [InlineData("HaRunning.Start(settings);")]
    [InlineData("sealed class Retrying : IHomeAssistantClient { }")]
    [InlineData("var c = new RetryingHomeAssistantClient(http);")]
    [InlineData("$u = $env:agent_ha_url")]
    public void Each_kind_of_HA_access_is_a_call_site(string line) =>
        Assert.Equal(["src/Host/Caller.cs"], Check(Files(("src/Host/Caller.cs", "// header\n" + line)), []).New);

    [Fact]
    public void Comments_tests_docs_fixtures_and_unrelated_code_are_not_call_sites()
    {
        var files = Files(
            ("src/Host/Note.cs", "// talks to /api/states on Home Assistant\n/** AGENT_HA_URL in a doc block\n * AGENT_HA_AGENT_TOKEN */\n# AGENT_HA_AGENT_TOKEN"),
            ("tests/AgentBridge.X.Tests/Uses.cs", "var c = new HomeAssistantClient(a, b, c);"),
            ("docs/design/ha.md", "/api/states"), ("tools/agents/test-reply.ps1", "$env:AGENT_HA_URL"), ("tools/agents/wake.Tests.ps1", "$env:AGENT_HA_URL"),
            ("tools/x/fixtures/rec.cs", "/api/states"), ("spikes/s1/Program.cs", "/api/states"), ("src/Net/Relay.cs", "new ClientWebSocket();"));
        Assert.Empty(Check(files, []).New);
    }

    [Fact]
    public void Block_comments_are_not_code_but_code_after_them_is()
    {
        var help = "<#\n.NOTES\n  talks to /api/states on Home Assistant\n#>\nWrite-Output 'x'\n/* AGENT_HA_URL */\n";
        Assert.Empty(Check(Files(("tools/x/help.ps1", help)), []).New);
        Assert.Equal(["tools/x/help.ps1"], Check(Files(("tools/x/help.ps1", help + "irm \"$u/api/states\"")), []).New);
        Assert.Equal(["src/X/Block.cs"], Check(Files(("src/X/Block.cs", "/* one line */\nvar u = \"/api/states\";")), []).New);
    }

    [Fact]
    public void An_allowance_with_nothing_left_in_it_is_stale()
    {
        var stale = Check(Files(("src/Host/Clean.cs", "var x = 1;")), [Allow("src/Host/Clean.cs"), Allow("tools/gone.ps1")]).Stale;
        Assert.Equal(["src/Host/Clean.cs", "tools/gone.ps1"], stale);
    }

    [Fact]
    public void The_connector_is_one_pinned_directory_so_a_new_file_in_it_fails_and_a_removed_one_is_stale()
    {
        var connector = new Allowance(null, Connector, 2, "S7", "https://github.com/danswett/agent-bridge/issues/1396", "the HA connector");
        var two = Files((Connector + "A.cs", "class A {}"), (Connector + "B.cs", "class B {}"));
        AssertClean(Check(two, [connector]));
        var three = Files((Connector + "A.cs", "class A {}"), (Connector + "B.cs", "class B {}"), (Connector + "C.cs", "class C {}"));
        Assert.NotEmpty(Check(three, [connector]).Stale);
        Assert.NotEmpty(Check(Files((Connector + "A.cs", "class A {}")), [connector]).Stale);
    }
}
