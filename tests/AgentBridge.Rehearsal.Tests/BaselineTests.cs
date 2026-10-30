using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class BaselineTests
{
    sealed class Tasks(params TaskEntry[] tasks) : ITaskReader
    {
        public IReadOnlyList<TaskEntry>? Read() => tasks;
    }

    static readonly TaskEntry Daemon = new("AgentBridgeDaemon_abc", true, "Running", "aa");

    static Scratch Install()
    {
        var s = new Scratch();
        s.File(".agent-ha-bridge/config.json", "{\"homeAssistant\":{\"token\":\"secret-token-value\"}}");
        s.File(".agent-ha-bridge/installation.json", "{}");
        s.File(".agent-ha-bridge/runtime/heartbeat", "1");
        s.File(".agent-ha-bridge/logs/daemon.log", "started");
        s.File(".agent-ha-bridge/daemon.log.1", "older");
        s.File(".copilot/hooks/agent-ha-bridge.json", "{\"hooks\":1}");
        s.File(".copilot/instructions/agent-ha-bridge.instructions.md", "block");
        s.File(".claude/settings.json", "{}");
        s.File(".codex/config.toml", "x = 1");
        return s;
    }

    static IReadOnlyList<Difference> Again(Scratch s, Baseline before, ITaskReader? tasks = null, params string[] ignore) =>
        BaselineCapture.Compare(before, BaselineCapture.Capture(Homes.For(s.Root), tasks ?? new Tasks(Daemon), DateTimeOffset.UtcNow), ignore);

    [Fact]
    public void An_install_left_alone_compares_identical_to_its_baseline()
    {
        using var s = Install();
        var before = BaselineCapture.Capture(Homes.For(s.Root), new Tasks(Daemon), DateTimeOffset.UtcNow);
        var diffs = Again(s, before);
        Assert.Empty(diffs);
        Assert.Equal(Verdict.Pass, BaselineCapture.Judge(before, diffs, new HashSet<string>()).Verdict);
    }

    [Fact]
    public void The_daemons_runtime_state_and_logs_are_not_part_of_the_baseline()
    {
        using var s = Install();
        var before = BaselineCapture.Capture(Homes.For(s.Root), new Tasks(Daemon), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(before.Files, f => f.Path.Contains("runtime", StringComparison.Ordinal) || f.Path.Contains(".log", StringComparison.Ordinal));
        s.File(".agent-ha-bridge/runtime/heartbeat", "2");
        s.File(".agent-ha-bridge/logs/daemon.log", "more");
        Assert.Empty(Again(s, before));
    }

    [Fact]
    public void A_changed_hook_fails_the_comparison_unless_the_step_allows_hooks_to_change()
    {
        using var s = Install();
        var before = BaselineCapture.Capture(Homes.For(s.Root), new Tasks(Daemon), DateTimeOffset.UtcNow);
        s.File(".copilot/hooks/agent-ha-bridge.json", "{\"hooks\":2}");
        var diffs = Again(s, before);
        var d = Assert.Single(diffs);
        Assert.Equal(BaselineCapture.Hooks, d.Category);
        Assert.Equal("~/.copilot/hooks/agent-ha-bridge.json", d.Path);
        Assert.Equal(Verdict.Fail, BaselineCapture.Judge(before, diffs, new HashSet<string>()).Verdict);
        Assert.Equal(Verdict.Pass, BaselineCapture.Judge(before, diffs, new HashSet<string> { BaselineCapture.Hooks }).Verdict);
    }

    [Fact]
    public void A_removed_file_an_added_file_and_a_disabled_task_are_each_reported()
    {
        using var s = Install();
        var before = BaselineCapture.Capture(Homes.For(s.Root), new Tasks(Daemon), DateTimeOffset.UtcNow);
        File.Delete(Path.Combine(s.Root, ".codex", "config.toml"));
        s.File(".codex/AGENTS.md", "new");
        var diffs = Again(s, before, new Tasks(Daemon with { Enabled = false, Sha256 = "bb" }));
        Assert.Contains(diffs, d => d is { Category: BaselineCapture.Agents, Path: "~/.codex/config.toml", Change: "removed" });
        Assert.Contains(diffs, d => d is { Category: BaselineCapture.Agents, Path: "~/.codex/AGENTS.md", Change: "added" });
        Assert.Contains(diffs, d => d is { Category: BaselineCapture.Tasks, Path: "AgentBridgeDaemon_abc", Change: "disabled" });
    }

    [Fact]
    public void Ignored_paths_are_left_out_of_the_comparison()
    {
        using var s = Install();
        var before = BaselineCapture.Capture(Homes.For(s.Root), new Tasks(Daemon), DateTimeOffset.UtcNow);
        s.File(".agent-ha-bridge/installation.json", "{\"seen\":1}");
        Assert.Single(Again(s, before));
        Assert.Empty(Again(s, before, null, "~/.agent-ha-bridge/installation.json"));
    }

    [Fact]
    public void A_saved_baseline_holds_hashes_and_never_what_a_file_says()
    {
        using var s = Install();
        var before = BaselineCapture.Capture(Homes.For(s.Root), new Tasks(Daemon), DateTimeOffset.UtcNow);
        var file = Path.Combine(s.Root, "evidence", "baseline.json");
        BaselineCapture.Save(before, file);
        Assert.DoesNotContain("secret-token-value", File.ReadAllText(file), StringComparison.Ordinal);
        var loaded = BaselineCapture.Load(file);
        Assert.Equal(before.Files.Select(f => (f.Path, f.Sha256)), loaded.Files.Select(f => (f.Path, f.Sha256)));
    }

    [Theory]
    [InlineData("<Task><Settings><Enabled>false</Enabled></Settings></Task>", false)]
    [InlineData("<Task><Settings><Enabled>true</Enabled></Settings></Task>", true)]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings><Enabled>false</Enabled></Settings></Task>", false)]
    [InlineData("<Task><Settings /></Task>", true)]
    public void A_task_reads_as_disabled_only_when_its_definition_says_so(string xml, bool enabled) =>
        Assert.Equal(enabled, ScheduledTasks.Enabled(xml));
}
