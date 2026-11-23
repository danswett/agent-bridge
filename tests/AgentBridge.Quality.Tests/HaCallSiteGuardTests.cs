using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// HA retirement, "no new HA call sites" for this repo (Part of #1396; the design of the fleet's gate, slipgates-fleet #726).
// Scope: EVERY tracked text file (no extension too) except binary types (an explicit list) and the top-level tests/, docs/, design/,
// spikes/ and backlog/ folders. Nothing is skipped for its name or a nested folder (bin, obj, node_modules included: only tracked files
// are read, and a script in a folder called bin must not be a hiding place); only a file type's real comment syntax hides a line.
// A file that names HA access must be in ha-callsite-allowlist.json with the COUNT of references it has: higher is red (a new call in an
// allowlisted file), lower is stale, so the list only shrinks. The connector project is one directory entry pinning its file NAMES.
// KNOWN LIMIT: a text scan cannot catch a reference built by string concatenation ("/api/" + "states"), base64 or other encoding, or an
// address read from a file; a count cannot catch adding one call while deleting another in the same file. It stops the plain new call.
public class HaCallSiteGuardTests
{
    const string AllowlistPath = "tests/AgentBridge.Quality.Tests/ha-callsite-allowlist.json";

    internal sealed record Allowance(string? Path, int? Sites, string? Directory, string[]? Names, string? Slice, string? Issue, string? Reason);

    // HA access as text, in any file: AGENT_HA_/FLEET_HA_/HA_/HASS_ names, HA API paths, an HA host or port, the connector's types and library
    // name, and the MQTT and notify services it drives. Names and paths match in any case (environment names are case-insensitive).
    static readonly Regex Access = new(
        @"(?i:\b(AGENT|FLEET)_HA_\w+|\b(HASS|HOMEASSISTANT)_(URL|TOKEN)\b|\bHA_\w+|\bhass\.(services|states|bus)\b|\bSUPERVISOR_TOKEN\b|\bHASSIO_\w+"
        + @"|\bapi/(states|services|websocket|image|config|events|template|history|logbook|camera_proxy\w*|error_log|core|webhook|mobile_app|hassio|stream|discovery_info|intent|calendars|tts_proxy)\b"
        + @"|home-?assistant\.(local|invalid|lan|io|com|net|org)\b|(?<![\w.])home-?assistant\b|(?<=//)supervisor\b|:8123\b|supervisor/core)"
        + @"|HomeAssistantClient\b|\bI?HomeAssistant\w*|\bHa[A-Z]\w*|\bAgentBridge\.HomeAssistant\b"
        + @"|mqtt\.publish|mqtt/subscribe|mobile_app_notification_action|notify\.mobile_app|persistent_notification",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    static readonly string[] Binary =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".icns", ".psd", ".zip", ".gz", ".tgz", ".7z", ".nupkg", ".jar", ".dll", ".exe",
        ".pdb", ".so", ".dylib", ".lib", ".a", ".bin", ".pdf", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".wav", ".mov", ".snk",
        ".pfx", ".cer", ".p12", ".der", ".sqlite", ".db", ".class", ".wasm", ".dat",
    ];
    static readonly string[] NotProduct = ["tests/", "docs/", "design/", "spikes/", "backlog/"];
    const string Connector = "src/AgentBridge.HomeAssistant/";

    static bool InScope(string path) =>
        !Binary.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        && !NotProduct.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

    // A file type's real comment syntax; a type not listed has no comments, so every line of it counts.
    internal sealed record Syntax(string[] Line, (string Open, string Close)[] Block, string[]? Multiline = null, bool HereStrings = false);
    static readonly Syntax CFamily = new(["//"], [("/*", "*/")]);
    static readonly Syntax Hash = new(["#"], []);
    static readonly Syntax Xml = new([], [("<!--", "-->")]);
    static readonly Syntax NoComments = new([], []);
    static Syntax SyntaxOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".csx" => CFamily with { Multiline = ["\"\"\""] },
        ".js" or ".mjs" or ".cjs" or ".ts" or ".mts" or ".tsx" or ".jsx" => CFamily with { Multiline = ["`"] },
        ".fs" => new(["//"], [("(*", "*)")]),
        ".ps1" or ".psm1" or ".psd1" => new(["#"], [("<#", "#>")], HereStrings: true),
        ".py" => Hash with { Multiline = ["\"\"\"", "'''"] },
        ".sh" or ".yml" or ".yaml" => Hash,
        ".cmd" or ".bat" => new(["rem ", "::"], []),
        ".csproj" or ".props" or ".targets" or ".slnx" or ".sln" or ".html" or ".xml" or ".xaml" or ".svg" or ".md" => Xml,
        _ => NoComments,
    };

    // The code lines of a file: not blank lines, not comment lines, and nothing inside a block comment that starts a line. Inside a raw string
    // ("""; C# and Python) or a PowerShell here-string (@' '@, @" "@) every line is text, even one that looks like a comment.
    static IEnumerable<string> CodeLines(string text, Syntax syntax)
    {
        string? end = null;
        string? here = null;
        string? raw = null;
        foreach (var source in text.Split('\n'))
        {
            var line = source.TrimEnd('\r');
            if (raw is not null) { yield return line; if (line.Contains(raw, StringComparison.Ordinal)) { raw = null; } continue; }
            if (here is not null) { yield return line; if (line.StartsWith(here, StringComparison.Ordinal)) { here = null; } continue; }
            if (end is not null)
            {
                var close = line.IndexOf(end, StringComparison.Ordinal);
                if (close < 0) { continue; }
                line = line[(close + end.Length)..];
                end = null;
            }
            var trimmed = line.TrimStart();
            foreach (var (blockOpen, blockClose) in syntax.Block)
            {
                // "(*)" is the F# multiplication operator, not a comment.
                if (!trimmed.StartsWith(blockOpen, StringComparison.Ordinal) || trimmed.StartsWith("(*)", StringComparison.Ordinal)) { continue; }
                var close = trimmed.IndexOf(blockClose, blockOpen.Length, StringComparison.Ordinal);
                if (close < 0) { end = blockClose; trimmed = ""; break; }
                line = trimmed[(close + blockClose.Length)..];
                trimmed = line.TrimStart();
                break;
            }
            if (end is not null || trimmed.Length == 0 || syntax.Line.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) { continue; }
            yield return line;
            if (syntax.Multiline?.FirstOrDefault(token => Regex.Count(line, Regex.Escape(token)) % 2 == 1) is { } token) { raw = token; }
            else if (syntax.HereStrings && line.TrimEnd().EndsWith("@'", StringComparison.Ordinal)) { here = "'@"; }
            else if (syntax.HereStrings && line.TrimEnd().EndsWith("@\"", StringComparison.Ordinal)) { here = "\"@"; }
        }
    }

    // How many HA references each in-scope file has (code lines only; every match counts, so two on one line are two).
    internal static SortedDictionary<string, int> Counts(IReadOnlyDictionary<string, string> files)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, raw) in files.Where(file => InScope(file.Key)))
        {
            // A UTF-16 file read without its BOM has a NUL between its letters: drop them so it is scanned, not skipped.
            var text = raw.Contains('\0') ? raw.Replace("\0", "") : raw;
            var count = CodeLines(text, SyntaxOf(path)).Sum(line => Access.Matches(line).Count);
            // Every file of the connector project is a site, whatever it holds.
            if (count > 0 || path.StartsWith(Connector, StringComparison.Ordinal)) { counts[path] = count; }
        }
        return counts;
    }

    internal static (string[] New, string[] Stale) Check(IReadOnlyDictionary<string, string> files, IReadOnlyList<Allowance> allowances)
    {
        var counts = Counts(files);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<string>();
        var stale = new List<string>();
        foreach (var allowance in allowances)
        {
            if (allowance.Directory is { } directory)
            {
                var actual = counts.Keys.Where(path => path.StartsWith(directory, StringComparison.Ordinal)).Select(path => path[directory.Length..]).ToList();
                var pinned = allowance.Names ?? [];
                added.AddRange(actual.Except(pinned).Select(name => directory + name));
                stale.AddRange(pinned.Except(actual).Select(name => directory + name + " (no longer in the pinned directory)"));
                covered.UnionWith(actual.Select(name => directory + name));
            }
            else if (allowance.Path is { } path && counts.TryGetValue(path, out var count) && count > 0)
            {
                covered.Add(path);
                if (count > allowance.Sites) { added.Add($"{path}: {count} HA references, the allowance says {allowance.Sites}"); }
                else if (count < allowance.Sites) { stale.Add($"{path}: {count} HA references, the allowance says {allowance.Sites} (lower it)"); }
            }
            else { stale.Add((allowance.Path ?? "(entry with no path)") + " (no HA reference left)"); }
        }
        added.AddRange(counts.Where(site => site.Value > 0 && !covered.Contains(site.Key)).Select(site => site.Key));
        return ([.. added], [.. stale]);
    }

    static IReadOnlyDictionary<string, string> Read(string root) =>
        ChangedFiles.Tracked(root).Where(InScope).Where(path => File.Exists(Path.Combine(root, path)))
            .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(root, path)), StringComparer.Ordinal);

    static IReadOnlyList<Allowance> Allowances(string root) =>
        JsonSerializer.Deserialize<Allowance[]>(File.ReadAllText(Path.Combine(root, AllowlistPath)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void The_repo_has_no_new_HA_call_site_and_every_allowance_is_current()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var (added, stale) = Check(Read(root), Allowances(root));
        Assert.True(added.Length == 0, "New HA call site, or more references than allowed (retire it, or allowlist it with its slice, its count and the #1396 issue): " + string.Join("; ", added));
        Assert.True(stale.Length == 0, "Allowance that no longer matches (lower or remove it): " + string.Join("; ", stale));
    }

    [Fact]
    public void Every_allowance_names_a_slice_a_reason_a_count_and_an_agent_bridge_issue()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var allowances = Allowances(root);
        Assert.NotEmpty(allowances);
        Assert.All(allowances, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Slice) || string.IsNullOrWhiteSpace(a.Reason), $"{a.Path ?? a.Directory} needs a Slice and a Reason");
            Assert.Matches(@"^https://github\.com/danswett/agent-bridge/issues/\d+$", a.Issue ?? "");
            Assert.True((a.Path is null) != (a.Directory is null), "an allowance is a Path or a Directory");
            Assert.True(a.Path is null || a.Sites > 0, "a Path allowance pins its HA reference count");
            Assert.True(a.Directory is null || a.Names is { Length: > 0 }, "a Directory allowance pins its file names");
            Assert.True(a.Directory is null || a.Directory.EndsWith('/'), "a Directory allowance ends with a slash, or it would also cover a sibling folder");
        });
    }

    static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);
    static void AssertClean((string[] New, string[] Stale) result) { Assert.Empty(result.New); Assert.Empty(result.Stale); }
    static Allowance Allow(string path, int sites = 1) => new(path, sites, null, null, "S7", "https://github.com/danswett/agent-bridge/issues/1396", "known");

    [Fact]
    public void A_new_file_that_reaches_HA_fails_and_an_allowlisted_one_passes()
    {
        var files = Files(("tools/new/poke.ps1", "Invoke-RestMethod -Uri \"$base/api/states\""), ("src/Other/Clean.cs", "var x = 1;"));
        Assert.Equal(["tools/new/poke.ps1"], Check(files, []).New);
        AssertClean(Check(files, [Allow("tools/new/poke.ps1")]));
    }

    [Fact]
    public void A_new_HA_call_inside_an_allowlisted_file_is_red_and_a_removed_one_is_stale()
    {
        var one = "var u = \"/api/states\";";
        AssertClean(Check(Files(("src/X/Caller.cs", one)), [Allow("src/X/Caller.cs")]));
        var more = Check(Files(("src/X/Caller.cs", one + "\nvar v = \"/api/services\";")), [Allow("src/X/Caller.cs")]);
        Assert.Equal(["src/X/Caller.cs: 2 HA references, the allowance says 1"], more.New);
        var sameLine = Check(Files(("src/X/Caller.cs", "var u = \"/api/states\" + \"/api/services\";")), [Allow("src/X/Caller.cs")]);
        Assert.Single(sameLine.New);
        var fewer = Check(Files(("src/X/Caller.cs", one)), [Allow("src/X/Caller.cs", 3)]);
        Assert.Equal(["src/X/Caller.cs: 1 HA references, the allowance says 3 (lower it)"], fewer.Stale);
    }

    [Theory]
    [InlineData("$token = $env:AGENT_HA_AGENT_TOKEN")]
    [InlineData("await Publish(\"mqtt.publish\", payload);")]
    [InlineData("sealed class Retrying : IHomeAssistantClient { }")]
    [InlineData("var c = new RetryingHomeAssistantClient(http);")]
    [InlineData("$u = $env:agent_ha_url")]
    public void Each_kind_of_HA_access_is_a_call_site(string line) =>
        Assert.Equal(["src/Host/Caller.cs"], Check(Files(("src/Host/Caller.cs", "// header\n" + line)), []).New);

    // Receipt on #4197 (P1-2): spellings of a new HA site that must be red, whatever the file type or name.
    [Theory]
    [InlineData("src/X/Caller.cs", "using AgentBridge.HomeAssistant;")]
    [InlineData("src/X/Caller.cs", "var w = new HaWebSocket(url);")]
    [InlineData("src/X/Caller.cs", "var u = \"/api/config\";")]
    [InlineData("tools/x/run.ps1", "$env:AGENT_HA_SECRET = 'x'")]
    [InlineData("tools/x/run.ps1", "$h = 'homeassistant.local'")]
    [InlineData("tools/x/run.ps1", "$u = 'http://box:8123/'")]
    [InlineData(".github/workflows/x.yml", "        run: curl http://homeassistant.local/api/states")]
    [InlineData("tools/x/config.json", "{ \"url\": \"http://box:8123\" }")]
    [InlineData("src/X/X.csproj", "<ProjectReference Include=\"../AgentBridge.HomeAssistant/AgentBridge.HomeAssistant.csproj\" />")]
    [InlineData("src/X/fixtures/Caller.cs", "var u = \"/api/states\";")]
    [InlineData("tools/x/test-ha.ps1", "$env:AGENT_HA_URL")]
    [InlineData("src/X/Raw.cs", "var s = \"\"\"\n// /api/states\n\"\"\";")]
    [InlineData("tools/x/here.ps1", "$s = @'\n# AGENT_HA_URL\n'@")]
    [InlineData("src/X/Caller.cs", "var u = \"/api/webhook/abc\";")]
    [InlineData("src/X/Caller.cs", "var u = \"/api/camera_proxy_stream/x\";")]
    [InlineData("tools/x/run.ps1", "$u = 'http://supervisor/addons'")]
    [InlineData("tools/x/run.ps1", "$t = $env:SUPERVISOR_TOKEN")]
    [InlineData("tools/x/run.ps1", "$t = $env:HA_TOKEN")]
    [InlineData("tools/x/run.ps1", "$u = 'http://homeassistant/'")]
    [InlineData("src/X/Caller.cs", "var a = new HaCommands(x);")]
    [InlineData("src/X/Caller.cs", "var a = new HaClientFactory();")]
    [InlineData("tools/x/run.py", "s = '''\n# AGENT_HA_URL\n'''")]
    [InlineData("tools/x/run.js", "const s = `\n// AGENT_HA_URL\n`;")]
    [InlineData("tools/x/app.swift", "let u = \"http://box:8123\"")]
    [InlineData("tools/x/config.yml", "url: homeassistant")]
    [InlineData("tools/x/config.json", "{ \"homeAssistant\": { \"url\": \"x\" } }")]
    [InlineData("src/X/Caller.cs", "var s = root.GetProperty(\"homeAssistant\");")]
    [InlineData("src/X/Caller.cs", "var o = new HomeAssistantOptions();")]
    [InlineData("tools/x/run.ps1", "$u = $env:HA_BASE_URL")]
    [InlineData("tools/x/run.ps1", "mosquitto_pub -t homeassistant/sensor/x/config -m 1")]
    [InlineData("tools/x/run.py", "hass.services.call('light', 'turn_on')")]
    [InlineData("tools/bin/run.ps1", "$env:AGENT_HA_URL")]
    [InlineData("tools/x/utf16.ps1", "A\0G\0E\0N\0T\0_\0H\0A\0_\0U\0R\0L\0")]
    [InlineData("src/X/Caller.fs", "(*) 1 2\nlet u = \"/api/states\"")]
    public void Realistic_new_HA_sites_are_red_whatever_the_file_type_or_name(string path, string text) =>
        Assert.Equal([path], Check(Files((path, text)), []).New);

    // Every text file is scanned (an explicit binary skip list; a nested bin, obj or node_modules folder is not exempt), and only a file
    // type's real comment syntax is a comment.
    [Theory]
    [InlineData("tools/x/README.md", "Posts to api/states on the box.")]
    [InlineData("tools/x/run", "export AGENT_HA_URL=x")]
    [InlineData("AgentBridge.slnx", "<Project Path=\"src/AgentBridge.HomeAssistant/AgentBridge.HomeAssistant.csproj\" />")]
    [InlineData("src/X/Caller.cs", "# AGENT_HA_URL")]
    [InlineData("tools/x/config.json", "// http://box:8123")]
    [InlineData("tools/x/run.ps1", "// $env:AGENT_HA_URL")]
    public void Every_text_file_is_scanned_and_only_real_comment_syntax_hides_a_line(string path, string text) =>
        Assert.Equal([path], Check(Files((path, text)), []).New);

    [Theory]
    [InlineData("tools/x/run.yml", "# AGENT_HA_URL")]
    [InlineData("tools/x/run.py", "# AGENT_HA_URL")]
    [InlineData("tools/x/run.js", "// AGENT_HA_URL")]
    [InlineData("tools/x/run.cmd", "REM AGENT_HA_URL")]
    [InlineData("src/X/X.csproj", "<!-- AgentBridge.HomeAssistant -->")]
    [InlineData("tools/x/README.md", "<!-- api/states -->")]
    [InlineData("tools/x/logo.png", "AGENT_HA_URL")]
    [InlineData("tools/x/blob.dat", "AGENT_HA_URL\0more")]
    public void Real_comments_and_binary_files_are_not_call_sites(string path, string text) =>
        Assert.Empty(Check(Files((path, text)), []).New);

    [Fact]
    public void Comments_and_the_top_level_tests_docs_design_spikes_and_backlog_folders_are_not_call_sites()
    {
        var files = Files(
            ("src/Host/Note.cs", "// talks to /api/states on Home Assistant\n/** AGENT_HA_URL in a doc block\n * AGENT_HA_AGENT_TOKEN */"),
            ("tests/AgentBridge.X.Tests/Uses.cs", "var c = new HomeAssistantClient(a, b, c);"),
            ("docs/design/ha.md", "/api/states"), ("design/x.cs", "/api/states"), ("backlog/x.ps1", "$env:AGENT_HA_URL"),
            ("spikes/s1/Program.cs", "/api/states"), ("src/Net/Relay.cs", "new ClientWebSocket();"));
        Assert.Empty(Check(files, []).New);
    }

    [Fact]
    public void Block_comments_are_not_code_but_code_after_them_is()
    {
        var help = "<#\n.NOTES\n  talks to /api/states on Home Assistant\n#>\nWrite-Output 'x'\n";
        Assert.Empty(Check(Files(("tools/x/help.ps1", help)), []).New);
        Assert.Equal(["tools/x/help.ps1"], Check(Files(("tools/x/help.ps1", help + "irm \"$u/api/states\"")), []).New);
        Assert.Equal(["src/X/Block.cs"], Check(Files(("src/X/Block.cs", "/* one line */\nvar u = \"/api/states\";")), []).New);
        Assert.Empty(Check(Files(("src/X/Block.cs", "/*\n AGENT_HA_URL\n*/\n")), []).New);
    }

    [Fact]
    public void An_allowance_with_nothing_left_in_it_is_stale()
    {
        var stale = Check(Files(("src/Host/Clean.cs", "var x = 1;")), [Allow("src/Host/Clean.cs"), Allow("tools/gone.ps1")]).Stale;
        Assert.Equal(["src/Host/Clean.cs (no HA reference left)", "tools/gone.ps1 (no HA reference left)"], stale);
    }

    [Fact]
    public void The_connector_is_pinned_by_file_name_so_a_swap_a_new_file_and_a_removed_file_all_fail()
    {
        var connector = new Allowance(null, null, Connector, ["A.cs", "B.cs"], "S7", "https://github.com/danswett/agent-bridge/issues/1396", "the HA connector");
        AssertClean(Check(Files((Connector + "A.cs", "class A {}"), (Connector + "B.cs", "class B {}")), [connector]));
        var swapped = Check(Files((Connector + "A.cs", "class A {}"), (Connector + "C.cs", "class C {}")), [connector]);
        Assert.Equal([Connector + "C.cs"], swapped.New);
        Assert.Equal([Connector + "B.cs (no longer in the pinned directory)"], swapped.Stale);
        Assert.Equal([Connector + "C.cs"], Check(Files((Connector + "A.cs", "x"), (Connector + "B.cs", "x"), (Connector + "C.cs", "x")), [connector]).New);
        Assert.NotEmpty(Check(Files((Connector + "A.cs", "x")), [connector]).Stale);
    }
}
