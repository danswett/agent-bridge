using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Hooks;
using Xunit;

namespace AgentBridge.Hooks.Tests;

// Ported from agent-ha-bridge's hook/main_test.go and tests/test-native-hook.ps1, for this
// product's hook. The difference that matters: with no host taking events, it prints the fixed
// reply and runs nothing else, where agent-ha-bridge's ran its PowerShell hook.
public sealed class HookRunnerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ab-hook-" + Guid.NewGuid().ToString("N")[..8]);
    readonly DateTimeOffset _now = new(2026, 10, 29, 14, 0, 0, TimeSpan.FromHours(-7));
    IReadOnlyList<int> _ancestors = [20, 30];

    string Bin => Path.Combine(_root, "bin");
    string Runtime => Path.Combine(_root, "home", ".agent-ha-bridge", "runtime");
    string Spool => SpoolContract.SpoolDirectory(Runtime);

    public HookRunnerTests()
    {
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Runtime);
        File.WriteAllBytes(Path.Combine(Bin, HookRunner.PointerFile), HookRunner.Pointer(Runtime));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    void Heartbeat(TimeSpan age)
    {
        var path = SpoolContract.HeartbeatPath(Runtime);
        File.WriteAllText(path, "1");
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime - age);
    }

    HookEnvironment Env(DateTimeOffset? at = null) => new()
    {
        ExecutableDirectory = Bin,
        Now = () => at ?? _now,
        ProcessId = 4242,
        Ancestors = () => _ancestors,
        ElapsedMs = () => 7,
        Version = "9.9.9",
    };

    string Invoke(string stdin, params string[] args) => Invoke(stdin, Env(), args);

    static string Invoke(string stdin, HookEnvironment env, params string[] args)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var output = new MemoryStream();
        Assert.Equal(0, HookRunner.Run(args, input, output, env));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    string[] SpoolFiles() => Directory.Exists(Spool) ? Directory.GetFiles(Spool).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray() : [];

    List<JsonElement> Log()
    {
        var path = SpoolContract.LogPath(Runtime);
        if (!File.Exists(path)) { return []; }
        return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public void Each_hook_replies_exactly_what_its_PowerShell_script_printed()
    {
        Heartbeat(TimeSpan.Zero);
        var cases = new Dictionary<string, string>
        {
            ["claude/register"] = "",
            ["claude/stop"] = "",
            ["claude/ask"] = "",
            ["claude/notification"] = "",
            ["codex/hook"] = "",
            ["copilot/ask_user"] = "{\"permissionDecision\":\"allow\"}\n",
            ["copilot/agent_stop"] = "{}\n",
            ["copilot/permission"] = "{}\n",
        };
        foreach (var (key, want) in cases)
        {
            Assert.Equal(want, Invoke("{\"a\":1}", key.Split('/')));
        }
        Assert.Equal(cases.Keys.Order(), SpoolContract.Keys.Order());
    }

    [Fact]
    public void With_the_host_taking_events_the_event_is_spooled_verbatim_with_its_ancestry_and_nothing_is_printed_for_Claude()
    {
        Heartbeat(TimeSpan.FromSeconds(5));
        Assert.Equal("", Invoke("  {\"session_id\":\"s1\"}\n", "claude", "stop"));

        var names = SpoolFiles();
        var name = Assert.Single(names);
        Assert.Matches(new Regex(@"^\d{20}-4242-[0-9a-f]{8}\.json$"), name);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Spool, name)));
        var e = doc.RootElement;
        // The fields agent-ha-bridge's daemon reads (Invoke-DaemonHookSpool), by these names.
        Assert.Equal(["v", "agent", "hook", "ancestors", "receivedAt", "event"], e.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, e.GetProperty("v").GetInt32());
        Assert.Equal("claude", e.GetProperty("agent").GetString());
        Assert.Equal("stop", e.GetProperty("hook").GetString());
        Assert.Equal([20, 30], e.GetProperty("ancestors").EnumerateArray().Select(a => a.GetInt32()));
        Assert.Equal(_now, DateTimeOffset.Parse(e.GetProperty("receivedAt").GetString()!));
        Assert.Equal("{\"session_id\":\"s1\"}", e.GetProperty("event").GetRawText());
    }

    [Fact]
    public void Spool_names_sort_in_the_order_the_events_happened()
    {
        Heartbeat(TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            Invoke("{\"n\":" + i + "}", Env(_now.AddMilliseconds(2 - i)), "codex", "hook");
        }
        var names = SpoolFiles();
        Assert.Equal(3, names.Length);
        using var first = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Spool, names[0])));
        Assert.Equal(2, first.RootElement.GetProperty("event").GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(120)]
    public void With_no_host_taking_events_the_fixed_reply_is_printed_and_nothing_is_spooled(int heartbeatAgeSeconds)
    {
        if (heartbeatAgeSeconds >= 0) { Heartbeat(TimeSpan.FromSeconds(heartbeatAgeSeconds)); }
        Assert.Equal("{\"permissionDecision\":\"allow\"}\n", Invoke("{\"q\":1}", "copilot", "ask_user"));
        Assert.Empty(SpoolFiles());
        var line = Assert.Single(Log());
        Assert.Equal("reply", line.GetProperty("path").GetString());
        Assert.Equal(heartbeatAgeSeconds < 0 ? "no daemon heartbeat" : "daemon heartbeat 120s old", line.GetProperty("reason").GetString());
    }

    [Fact]
    public void The_fallback_target_prints_the_fixed_reply_and_never_spools_even_with_the_host_up()
    {
        Heartbeat(TimeSpan.Zero);
        Assert.Equal("{}\n", Invoke("{}", "--fallback", "copilot", "agent_stop"));
        Assert.Equal("", Invoke("{}", "--fallback", "claude", "stop"));
        Assert.Empty(SpoolFiles());
    }

    [Fact]
    public void A_hook_with_no_pointer_beside_it_prints_the_fixed_reply_and_writes_nowhere()
    {
        File.Delete(Path.Combine(Bin, HookRunner.PointerFile));
        Heartbeat(TimeSpan.Zero);
        Assert.Equal("{}\n", Invoke("{}", "copilot", "permission"));
        File.WriteAllText(Path.Combine(Bin, HookRunner.PointerFile), "{\"version\":1,\"runtimeRoot\":\"relative\\\\path\"}");
        Assert.Equal("{}\n", Invoke("{}", "copilot", "permission"));
        Assert.Empty(SpoolFiles());
        Assert.Empty(Log());
    }

    [Fact]
    public void What_the_PowerShell_hook_would_have_ignored_is_not_handed_on()
    {
        Heartbeat(TimeSpan.Zero);
        foreach (var stdin in new[] { "", "   ", "not json", "{\"a\":1} trailing" })
        {
            Assert.Equal("{\"permissionDecision\":\"allow\"}\n", Invoke(stdin, "copilot", "ask_user"));
        }
        Assert.Empty(SpoolFiles());
        Assert.Equal("", Invoke("{}", "gemini", "stop"));
        Assert.Equal("", Invoke(""));
        Assert.All(Log(), l => Assert.Equal("unusable event", l.GetProperty("reason").GetString()));
    }

    [Fact]
    public void Every_run_is_logged_with_the_path_it_took_and_why()
    {
        Heartbeat(TimeSpan.Zero);
        Invoke("{}", "claude", "stop");
        Invoke("nope", "claude", "stop");
        // A file where the spool folder should be.
        Directory.Delete(Spool, true);
        File.WriteAllText(Spool, "x");
        Invoke("{}", "codex", "hook");
        File.Delete(Spool);
        Heartbeat(TimeSpan.FromMinutes(5));
        Invoke("{}", "codex", "hook");

        var lines = Log();
        Assert.Equal(["spool", "reply", "reply", "reply"], lines.Select(l => l.GetProperty("path").GetString()));
        Assert.False(lines[0].TryGetProperty("reason", out _));
        Assert.Equal("unusable event", lines[1].GetProperty("reason").GetString());
        Assert.StartsWith("spool failed: ", lines[2].GetProperty("reason").GetString());
        Assert.Equal("daemon heartbeat 300s old", lines[3].GetProperty("reason").GetString());
        Assert.All(lines, l =>
        {
            Assert.Equal(7, l.GetProperty("ms").GetInt64());
            Assert.True(DateTimeOffset.TryParse(l.GetProperty("at").GetString(), out _));
        });
    }

    [Fact]
    public void Nothing_is_logged_for_an_unknown_hook_or_a_version_check()
    {
        Heartbeat(TimeSpan.Zero);
        Invoke("{}", "gemini", "stop");
        Assert.Equal("9.9.9\n", Invoke("", "--version"));
        Assert.Empty(Log());
    }

    [Fact]
    public void The_log_keeps_one_previous_megabyte()
    {
        Heartbeat(TimeSpan.Zero);
        var path = SpoolContract.LogPath(Runtime);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', (int)SpoolContract.LogMaxBytes).ToArray());
        Invoke("{}", "claude", "stop");
        Assert.Equal(SpoolContract.LogMaxBytes, new FileInfo(path + ".1").Length);
        Assert.Single(Log());
    }

    [Fact]
    public void An_unexpected_failure_still_prints_the_fixed_reply_and_exits_zero()
    {
        Heartbeat(TimeSpan.Zero);
        var env = new HookEnvironment
        {
            ExecutableDirectory = Bin,
            Now = () => _now,
            Ancestors = () => throw new InvalidOperationException("boom"),
        };
        Assert.Equal("{\"permissionDecision\":\"allow\"}\n", Invoke("{}", env, "copilot", "ask_user"));

        using var output = new MemoryStream();
        Assert.Equal(0, HookRunner.Run(["copilot", "permission"], new Unreadable(), output, Env()));
        Assert.Equal("{}\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    sealed class Unreadable : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("the pipe broke");
    }

    [Fact]
    public void Copilot_ask_user_gets_its_allow_at_once_even_with_more_input_than_any_event()
    {
        Heartbeat(TimeSpan.Zero);
        var huge = "{\"x\":\"" + new string('a', SpoolContract.MaxEventBytes) + "\"}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("{\"permissionDecision\":\"allow\"}\n", Invoke(huge, "copilot", "ask_user"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        // Cut at the limit, it is no longer one JSON value, so it is not spooled half-read.
        Assert.Empty(SpoolFiles());
    }

    [Fact]
    public void A_spool_file_agent_ha_bridges_Go_hook_wrote_reads_back_as_the_same_event()
    {
        var go = "{\"v\":1,\"agent\":\"codex\",\"hook\":\"hook\",\"ancestors\":[812,600,4],\"receivedAt\":\"2026-10-29T14:00:00.123456789-07:00\",\"event\":{\"session_id\":\"x\",\"hook_event_name\":\"PreToolUse\"}}";
        var e = SpoolContract.Parse(Encoding.UTF8.GetBytes(go), "00000000000000000001-1-abcdef01.json")!;
        Assert.Equal(("codex/hook", 1), (e.Key, e.Version));
        Assert.Equal([812, 600, 4], e.Ancestors);
        Assert.Equal("PreToolUse", e.Event.GetProperty("hook_event_name").GetString());
        Assert.NotNull(e.ReceivedAt);
        Assert.Null(SpoolContract.Parse("[1]"u8, "x.json"));
        Assert.Null(SpoolContract.Parse("{\"agent\":1,\"hook\":\"x\"}"u8, "x.json"));
    }

    [Fact]
    public void The_walk_up_the_process_tree_stops_at_its_limit_an_unknown_process_and_a_loop()
    {
        var parents = new Dictionary<int, int> { [10] = 20, [20] = 30, [30] = 1, [1] = 0 };
        int? Lookup(int p) => parents.TryGetValue(p, out var v) ? v : null;
        Assert.Equal([10, 20, 30, 1], Ancestry.Walk(10, 12, Lookup));
        Assert.Equal([10, 20], Ancestry.Walk(10, 2, Lookup));
        var loop = new Dictionary<int, int> { [5] = 6, [6] = 5 };
        Assert.Equal([5, 6], Ancestry.Walk(5, 12, p => loop.TryGetValue(p, out var v) ? v : null));
        Assert.Equal([99], Ancestry.Walk(99, 12, Lookup));
    }

    [Fact]
    public void The_real_ancestry_starts_at_this_processs_parent()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { return; }
        var chain = Ancestry.OfParent();
        Assert.NotEmpty(chain);
        Assert.DoesNotContain(Environment.ProcessId, chain);
        using var parent = System.Diagnostics.Process.GetProcessById(chain[0]);
        Assert.False(parent.HasExited);
    }
}
