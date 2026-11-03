using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Mac;
using AgentBridge.Protocol;
using Xunit;

public sealed class ManagedSessionDiscoveryTests
{
    static readonly DateTimeOffset At = Fixture.T0;
    static readonly Started Root = new(100, At.AddSeconds(-1));
    static MacProcessIdentity Identity(int pid, int seconds = 0) => new(pid, 100, 501, 501, 501, At.AddSeconds(seconds), "/test/codex");

    sealed class Rig
    {
        public FakeProcesses Processes { get; } = new();
        public Dictionary<int, MacProcessIdentity> Identities { get; } = [];
        public HashSet<int> Owned { get; } = [];
        public IReadOnlyList<Started> Roots { get; set; } = [Root];
        public Func<string, IReadOnlyList<int>, IReadOnlyList<int>> Holders { get; set; } = (_, _) => [];
        public Action<int>? OnOwns { get; set; }
        public ManagedSessionProcessInspector Inspector { get; }

        public Rig()
        {
            Inspector = new(Processes, () => Roots,
                (root, pid) => { OnOwns?.Invoke(pid); return root == Root && Owned.Contains(pid); },
                pid => Identities.GetValueOrDefault(pid), (path, pids) => Holders(path, pids));
        }

        public void Run(int pid, bool owned = true, string command = "codex --no-daemon")
        {
            Processes.Run(AgentKind.Codex, pid, command, At);
            Identities[pid] = Identity(pid);
            if (owned) { Owned.Add(pid); }
        }
    }

    [Fact]
    public void External_sessions_and_global_uncertainty_remain_visible_without_becoming_owned()
    {
        var rig = new Rig();
        rig.Processes.Run(AgentKind.Copilot, 500, "copilot", At);
        rig.Processes.Unreadable(AgentKind.Copilot);
        var observed = rig.Inspector.Observe(AgentKind.Copilot);
        Assert.Equal(500, Assert.Single(observed.Processes).Id);
        Assert.False(observed.Known);
        Assert.False(observed.Listed);
        Assert.Equal(500, Assert.Single(rig.Inspector.GetAgentProcesses(AgentKind.Copilot)).Id);
        rig.Run(600, owned: false);
        rig.Holders = (_, _) => throw new InvalidOperationException("no owned candidate should query files");
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
    }

    [Fact]
    public void Only_registered_generations_survive_a_file_holder_lookup()
    {
        var rig = new Rig();
        rig.Run(101);
        rig.Run(102, owned: false);
        rig.Holders = (path, pids) =>
        {
            Assert.Equal("/test/lock", path);
            Assert.Equal(new[] { 101, 102 }, pids.Order());
            return [101, 101];
        };
        Assert.Equal(new[] { 101 }, rig.Inspector.FileHolders("/test/lock"));
        rig.Identities[101] = Identity(101, 1);
        rig.Holders = (_, _) => throw new InvalidOperationException("a reused PID should not query files");
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("image")]
    [InlineData("ownership")]
    [InlineData("unknown-holder")]
    [InlineData("shared-external")]
    public void Identity_changes_and_foreign_shared_holders_cannot_acquire_a_binding(string change)
    {
        var rig = new Rig();
        rig.Run(101);
        rig.Run(102, owned: false, command: "codex app-server");
        rig.Holders = (_, _) =>
        {
            if (change == "pid") { rig.Identities[101] = Identity(101, 1); }
            if (change == "image") { rig.Identities[101] = Identity(101) with { Executable = "/test/other" }; }
            if (change == "ownership") { rig.Owned.Clear(); }
            return change switch { "unknown-holder" => [999], "shared-external" => [101, 102], _ => [101] };
        };
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
    }

    [Fact]
    public void An_identity_change_during_the_ownership_check_is_rejected()
    {
        var rig = new Rig();
        rig.Run(101);
        rig.OnOwns = pid => rig.Identities[pid] = Identity(pid, 1);
        rig.Holders = (_, _) => throw new InvalidOperationException("a changed process should not query files");
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
    }

    [Fact]
    public void Losing_one_shared_holder_does_not_turn_an_ambiguous_lock_into_a_unique_match()
    {
        var rig = new Rig();
        rig.Run(101);
        rig.Run(102);
        rig.Holders = (_, _) => { rig.Owned.Remove(102); return [101, 102]; };
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
    }

    [Fact]
    public void An_unidentified_runtime_sharing_the_lock_refuses_the_binding()
    {
        var rig = new Rig();
        rig.Run(101);
        rig.Run(102, owned: false);
        rig.Processes.Unidentify(AgentKind.Codex, 102);
        rig.Holders = (_, pids) => { Assert.Contains(102, pids); return [101, 102]; };
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
    }

    [Fact]
    public void An_unreadable_or_oversized_process_list_never_queries_file_holders()
    {
        var rig = new Rig();
        rig.Run(101);
        rig.Processes.Unreadable(AgentKind.Codex);
        rig.Holders = (_, _) => throw new InvalidOperationException("an incomplete list should not query files");
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
        rig.Processes.Unreadable(AgentKind.Codex, false);
        for (var i = 0; i < 256; i++) { rig.Run(200 + i); }
        Assert.Empty(rig.Inspector.FileHolders("/test/lock"));
    }

    [Theory]
    [InlineData("p101\nf3\np102\nf4\n", true)]
    [InlineData("p101\nf3\nf4\n", true)]
    [InlineData("p101\nf3\n", true)]
    [InlineData("p101\np102\nf3\n", false)]
    [InlineData("p101\nf3\np999\nf4\n", false)]
    [InlineData("p101\nf3\nn/private/file\n", false)]
    [InlineData("f3\np101\nf3\n", false)]
    [InlineData("p0\n", false)]
    [InlineData("p+101\n", false)]
    [InlineData("p101\r\n", false)]
    public void File_holder_output_is_only_bounded_requested_processes_and_numeric_descriptors(string output, bool valid)
    {
        Assert.Equal(valid, MacFileHolders.Parse(output, [101, 102]).Count > 0);
        Assert.Empty(MacFileHolders.Parse(new string('p', 16 * 1024 + 1), [101]));
    }

    [Fact]
    public async Task Session_source_maps_an_owned_Codex_lock_and_preserves_an_external_Copilot_card()
    {
        using var fixture = new Fixture();
        var rig = new Rig();
        const string codex = "11111111-1111-4111-8111-111111111111";
        const string copilot = "22222222-2222-4222-8222-222222222222";
        var path = fixture.CodexLock(codex);
        rig.Run(101);
        rig.Holders = (file, _) => file == path ? [101] : [];
        fixture.CopilotSession(copilot, 200, lockWrite: At);
        rig.Processes.Run(AgentKind.Copilot, 200, "copilot", At.AddMinutes(-1));
        await using var source = new SessionSource(fixture.Options(), rig.Inspector, fixture.Time);
        await source.RefreshAsync();
        Assert.Equal(new[] { codex, copilot }, source.Snapshot().Select(s => s.Id).Order());
        Assert.Equal(new SessionProcess(101, At), source.ProcessOf(codex));
        Assert.Equal(200, source.ProcessOf(copilot)?.Id);
        Assert.DoesNotContain(200, rig.Owned);
    }

    [Fact]
    public async Task Real_Mac_file_holder_query_reports_only_the_requested_process_and_exact_file()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        using var fixture = new Fixture();
        var path = fixture.CodexLock("11111111-1111-4111-8111-111111111111");
        await using var opened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal(new[] { Environment.ProcessId }, MacFileHolders.Find(path, [Environment.ProcessId]));
        var other = fixture.CodexLock("22222222-2222-4222-8222-222222222222");
        Assert.Empty(MacFileHolders.Find(other, [Environment.ProcessId]));
    }

    [ManagedTmuxFact]
    public async Task A_real_private_panes_kernel_identity_and_open_lock_bind_through_the_adapter()
    {
        using var fixture = new Fixture();
        const string session = "11111111-1111-4111-8111-111111111111";
        var path = fixture.CodexLock(session);
        var runtime = "/tmp/abmd-" + Guid.NewGuid().ToString("N")[..12];
        var executable = File.Exists("/opt/homebrew/bin/tmux") ? "/opt/homebrew/bin/tmux" : "/usr/local/bin/tmux";
        var backend = new ManagedTmuxBackend(executable, runtime);
        var commands = new TmuxCommand(executable, backend.SocketPath);
        try
        {
            // This authored shell fixture stands in for an interactive lock holder;
            // its agent classification is injected, but ownership and lsof are real.
            var started = backend.Start(new("/bin/bash",
                ["-c", "exec 3<> \"$1\"; while IFS= read -r line; do :; done", "--", path], runtime));
            var processes = new FakeProcesses();
            processes.Run(AgentKind.Codex, started.ProcessId, "codex --no-daemon", started.StartedAt);
            var inspector = new ManagedSessionProcessInspector(processes,
                () => backend.Snapshot().Select(s => s.Started).ToArray(), backend.Owns,
                MacProcessIdentityReader.Capture, MacFileHolders.Find);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (inspector.FileHolders(path).Count == 0 && DateTime.UtcNow < deadline) { await Task.Delay(25); }
            Assert.Equal(new[] { started.ProcessId }, inspector.FileHolders(path));
            await using var source = new SessionSource(fixture.Options(), inspector, fixture.Time);
            await source.RefreshAsync();
            Assert.Equal(session, Assert.Single(source.Snapshot()).Id);
            Assert.Equal(new SessionProcess(started.ProcessId, started.StartedAt), source.ProcessOf(session));
            Assert.Null(backend.Refuse(started.ProcessId));
            Assert.NotNull(backend.Refuse(Environment.ProcessId));
        }
        finally
        {
            await commands.RunAsync(["kill-server"]);
            Directory.Delete(runtime, true);
        }
    }
}
