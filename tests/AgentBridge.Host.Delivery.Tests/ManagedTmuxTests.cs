using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Mac;
using Xunit;

public sealed class ManagedTmuxTests
{
    static readonly DateTimeOffset At = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    static MacProcessIdentity Identity(int pid, int parent, string image, int seconds = 0) => new(pid, parent, 501, 501, 501, At.AddSeconds(seconds), image);

    sealed class Probe : IMacProcessProbe
    {
        public uint UserId { get; set; } = 501;
        public Dictionary<int, MacProcessIdentity> Identities { get; } = new()
        {
            [Environment.ProcessId] = Identity(Environment.ProcessId, 1, "/test/host"),
            [200] = Identity(200, 1, "/test/tmux"), [300] = Identity(300, 200, "/test/agent", 1),
        };
        public MacProcessIdentity? Capture(int pid) => Identities.GetValueOrDefault(pid);
        public bool HasExited(MacProcessIdentity identity) => Identities.TryGetValue(identity.ProcessId, out var now) && now.StartedAt != identity.StartedAt;
    }

    sealed class Commands : ITmuxCommand
    {
        public List<string[]> Calls { get; } = [];
        public List<string> Inputs { get; } = [];
        public string Session { get; private set; } = "";
        public int PanePid { get; set; } = 300;
        public int ServerPid { get; set; } = 200;
        public string Screen { get; set; } = "Question\nYes\nNo\n";
        public Func<string[], TmuxResult?>? OnCall { get; set; }
        public Task<TmuxResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, string? standardInput = null)
        {
            var args = arguments.ToArray(); Calls.Add(args);
            if (standardInput is not null) { Inputs.Add(standardInput); }
            if (OnCall?.Invoke(args) is { } custom) { return Task.FromResult(custom); }
            if (args[0] == "new-session") { Session = args[Array.IndexOf(args, "-s") + 1]; }
            return Task.FromResult(new TmuxResult(0, args[0] switch
            {
                "new-session" or "display-message" => $"%1\t{PanePid}\t{Session}\t0\t{ServerPid}\n",
                "capture-pane" => Screen,
                _ => "",
            }));
        }
        public string[][] Writes => Calls.Where(c => c[0] is "send-keys" or "paste-buffer" or "kill-pane").ToArray();
    }

    static (ManagedTmuxBackend Backend, Commands Commands, Probe Probe, Started Started) StartedBackend()
    {
        var commands = new Commands(); var probe = new Probe();
        var backend = new ManagedTmuxBackend(commands, probe, _ => "/test/agent");
        var started = backend.Start(new("/test/agent", [], "/workspace"));
        return (backend, commands, probe, started);
    }

    [Fact]
    public void Launch_uses_multiple_argv_even_without_agent_arguments_and_snapshot_omits_environment()
    {
        var commands = new Commands(); var probe = new Probe();
        var backend = new ManagedTmuxBackend(commands, probe, _ => "/test/agent");
        backend.Start(new("/test/agent", [], "/workspace/#{name}", new Dictionary<string, string> { ["PRIVATE_TOKEN"] = "secret", ["AGENT_BRIDGE_SESSION"] = "not-a-session" }));
        var launch = commands.Calls.Single(c => c[0] == "new-session");
        Assert.Equal(new[] { "--", "/usr/bin/env", "/test/agent" }, launch[^3..]);
        Assert.Contains("/workspace/##{name}", launch);
        Assert.Null(Assert.Single(backend.Snapshot()).BridgeSession);
        Assert.DoesNotContain("secret", Assert.Single(backend.Snapshot()).ToString());
    }

    [Fact]
    public async Task An_unowned_process_or_pane_never_receives_a_key_or_stop()
    {
        var (backend, commands, probe, _) = StartedBackend();
        probe.Identities[400] = Identity(400, 1, "/test/agent", 2);
        Assert.NotNull(backend.Refuse(400));
        Assert.False(backend.Owns(new(300, At.AddSeconds(1)), 400));
        Assert.NotNull(backend.Open(400, At.AddSeconds(2)).Refusal);
        Assert.Equal(InjectionStatus.NothingTyped, (await backend.InjectAsync(400, new([new("hello", true, 0, 0)]))).Status);
        Assert.Empty(commands.Writes);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("image")]
    [InlineData("user")]
    [InlineData("saved-user")]
    [InlineData("server")]
    [InlineData("pane")]
    public async Task A_changed_identity_refuses_both_delivery_and_a_previously_held_stop(string change)
    {
        var (backend, commands, probe, started) = StartedBackend();
        using var held = Assert.IsAssignableFrom<IHeldProcess>(backend.Open(300, started.StartedAt).Process);
        if (change == "pid") { probe.Identities[300] = probe.Identities[300] with { StartedAt = At.AddMinutes(1) }; }
        if (change == "image") { probe.Identities[300] = probe.Identities[300] with { Executable = "/test/replacement" }; }
        if (change == "user") { probe.Identities[300] = probe.Identities[300] with { UserId = 502 }; }
        if (change == "saved-user") { probe.Identities[300] = probe.Identities[300] with { SavedUserId = 0 }; }
        if (change == "server") { probe.Identities[200] = probe.Identities[200] with { StartedAt = At.AddMinutes(1) }; }
        if (change == "pane") { commands.PanePid = 999; }
        Assert.NotNull(held.Terminate());
        Assert.Equal(InjectionStatus.NothingTyped, (await backend.InjectAsync(300, new([new("hello", true, 0, 0)]))).Status);
        Assert.Empty(commands.Writes);
    }

    [Fact]
    public async Task Owned_descendants_are_bound_by_ancestry_start_time_and_executable()
    {
        var (backend, commands, probe, started) = StartedBackend();
        probe.Identities[301] = Identity(301, 300, "/test/native-agent", 2);
        Assert.True(backend.Owns(started, 301));
        Assert.Equal(InjectionStatus.Typed, (await backend.InjectAsync(301, new([new("hello", false, 0, 0)]))).Status);
        var paste = Assert.Single(commands.Writes);
        Assert.Equal("%1", paste[Array.IndexOf(paste, "-t") + 1]);
        probe.Identities[301] = probe.Identities[301] with { Executable = "/test/changed" };
        Assert.NotNull(backend.Refuse(301));
        probe.Identities[302] = Identity(302, 300, "/test/native-agent", -1);
        Assert.NotNull(backend.Refuse(302));
        // A later generation may legitimately be another child of the same managed
        // launcher; the old generation is proven gone before its binding is replaced.
        probe.Identities[301] = Identity(301, 300, "/test/new-child", 3);
        Assert.True(backend.Owns(started, 301));
    }

    [Fact]
    public async Task Changed_screen_or_proceed_guard_refuses_before_any_key()
    {
        var (backend, commands, _, _) = StartedBackend();
        var plan = new KeyPlan([new("y", true, 0, 0, [new(1, "Different option")])]);
        Assert.Equal(InjectionStatus.NothingTyped, (await backend.InjectAsync(300, plan)).Status);
        Assert.Equal(InjectionStatus.NothingTyped, (await backend.InjectAsync(300, new([new("y", true, 0, 0)]) { Proceed = () => "read-only now" })).Status);
        Assert.Empty(commands.Writes);
    }

    [Fact]
    public async Task Literal_text_and_navigation_are_separate_and_later_controls_refuse_the_whole_plan()
    {
        var (backend, commands, _, _) = StartedBackend();
        var plan = new KeyPlan([new(KeyPlan.Down + " ", false, 0, 0), new("Enter; $(ignored)", true, 0, 0)]);
        Assert.Equal(InjectionStatus.Typed, (await backend.InjectAsync(300, plan)).Status);
        Assert.Equal(new[] { "Down", "Enter" }, commands.Writes.Where(c => c[0] == "send-keys").Select(c => c[^1]));
        Assert.Equal(new[] { " ", "Enter; $(ignored)" }, commands.Inputs);
        Assert.Equal(2, commands.Writes.Count(c => c[0] == "paste-buffer"));
        Assert.DoesNotContain(commands.Calls, c => c.Contains("Enter; $(ignored)"));
        commands.Calls.Clear();
        Assert.Equal(InjectionStatus.NothingTyped, (await backend.InjectAsync(300, new([new("safe", false, 0, 0), new("bad\rinput", true, 0, 0)]))).Status);
        Assert.Empty(commands.Writes);
    }

    [Fact]
    public async Task Failed_write_and_cancellation_after_text_are_uncertain_and_never_send_enter()
    {
        var (backend, commands, _, _) = StartedBackend();
        using var cancellation = new CancellationTokenSource();
        commands.OnCall = args => { if (args[0] == "paste-buffer") { cancellation.Cancel(); } return null; };
        Assert.Equal(InjectionStatus.Interrupted, (await backend.InjectAsync(300, new([new("hello", true, 100, 0)]), cancellation.Token)).Status);
        Assert.Single(commands.Writes);
        commands.Calls.Clear();
        commands.OnCall = args => args[0] == "paste-buffer" ? new(1, "") : null;
        Assert.Equal(InjectionStatus.Interrupted, (await backend.InjectAsync(300, new([new("hello", true, 0, 0)]))).Status);
        Assert.Single(commands.Writes);
    }

    [Fact]
    public async Task Literal_secrets_use_stdin_and_the_named_buffer_is_deleted_after_delivery()
    {
        var (backend, commands, _, _) = StartedBackend();
        const string secret = "fixture-secret-not-for-argv";
        Assert.Equal(InjectionStatus.Typed, (await backend.InjectAsync(300, new([new(secret, true, 0, 0)]))).Status);
        Assert.Equal(secret, Assert.Single(commands.Inputs));
        Assert.DoesNotContain(commands.Calls, c => c.Any(a => a.Contains(secret, StringComparison.Ordinal)));
        var loaded = Assert.Single(commands.Calls, c => c[0] == "load-buffer");
        var pasted = Assert.Single(commands.Calls, c => c[0] == "paste-buffer");
        var deleted = Assert.Single(commands.Calls, c => c[0] == "delete-buffer");
        Assert.Equal(loaded[2], pasted[4]);
        Assert.Equal(loaded[2], deleted[2]);
        Assert.Contains("-d", pasted);
        Assert.Contains("-p", pasted);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("identity")]
    [InlineData("cancel")]
    [InlineData("load-failed")]
    public async Task A_change_or_cancellation_after_buffer_load_types_nothing_and_removes_the_buffer(string change)
    {
        var (backend, commands, probe, _) = StartedBackend();
        using var cancellation = new CancellationTokenSource();
        commands.OnCall = args =>
        {
            if (args[0] != "load-buffer") { return null; }
            if (change == "screen") { commands.Screen = "A different question\n"; }
            if (change == "identity") { probe.Identities[300] = probe.Identities[300] with { StartedAt = At.AddMinutes(1) }; }
            if (change == "cancel") { cancellation.Cancel(); }
            return change == "load-failed" ? new(1, "") : null;
        };
        var result = await backend.InjectAsync(300, new([new("fixture secret", true, 0, 0, [new(0, "Question")])]), cancellation.Token);
        Assert.Equal(InjectionStatus.NothingTyped, result.Status);
        Assert.Empty(commands.Writes);
        Assert.Single(commands.Calls, c => c[0] == "delete-buffer");
    }

    [Theory]
    [InlineData(";", "\\;")]
    [InlineData("hello;", "hello\\;")]
    [InlineData("hello\\;", "hello\\\\;")]
    [InlineData("hello;there", "hello;there")]
    public void Tmux_argv_cannot_introduce_a_second_command(string input, string expected) => Assert.Equal(expected, TmuxCommand.EscapeArgument(input));

    [Fact]
    public void Root_and_overridden_home_are_refused_before_a_launch()
    {
        var commands = new Commands(); var probe = new Probe { UserId = 0 };
        var backend = new ManagedTmuxBackend(commands, probe, _ => "/test/agent");
        Assert.NotNull(backend.Unavailable());
        probe.UserId = 501;
        probe.Identities[Environment.ProcessId] = probe.Identities[Environment.ProcessId] with { UserId = 0 };
        Assert.NotNull(backend.Unavailable());
        probe.Identities[Environment.ProcessId] = probe.Identities[Environment.ProcessId] with { UserId = 501 };
        Assert.Null(backend.CommandLine(new("/test/agent", [], "/workspace", new Dictionary<string, string> { ["HOME"] = "/other" }), out var reason));
        Assert.NotNull(reason);
        Assert.Empty(commands.Calls);
    }
}
