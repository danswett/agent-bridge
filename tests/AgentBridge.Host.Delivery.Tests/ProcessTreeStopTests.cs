using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed class ProcessTreeStopTests
{
    static readonly DateTimeOffset T0 = Fixture.T0;

    [Fact]
    public async Task Forced_stop_ends_only_verified_descendants_and_repeated_termination_is_idempotent()
    {
        var rig = new Rig();
        using var held = rig.Open();
        Assert.Null(held.Terminate());
        Assert.Null(held.Terminate());
        Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Equal(new[] { 101, 102, 103 }, rig.Terminated);
        Assert.True(rig.Nodes[104].Running);
    }

    [Fact]
    public async Task Graceful_parent_exit_does_not_hide_an_already_verified_grandchild()
    {
        var rig = new Rig();
        using var held = rig.Open();
        rig.Nodes[101].Running = rig.Nodes[102].Running = false;
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Null(held.Terminate());
        Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.False(rig.Nodes[103].Running);
    }

    [Fact]
    public async Task A_root_exit_is_not_reported_as_ended_while_a_descendant_is_still_running()
    {
        var rig = new Rig();
        using var held = rig.Open();
        rig.Nodes[101].Running = false;
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Null(held.Terminate());
        Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.False(rig.Nodes[102].Running);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("elevation")]
    [InlineData("creation time")]
    [InlineData("snapshot")]
    public async Task An_unverifiable_tree_refuses_before_any_process_is_terminated(string reason)
    {
        var rig = new Rig();
        if (reason == "snapshot") { rig.UnknownSnapshot = true; }
        else if (reason == "creation time") { rig.Nodes[102].UnknownStart = true; }
        else { rig.Nodes[102].Refusal = reason; }
        using var held = rig.Open();
        Assert.NotNull(held.Terminate());
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Empty(rig.Terminated);
        Assert.True(rig.Nodes[101].Running);
    }

    [Fact]
    public async Task A_reused_parent_PID_does_not_make_an_older_unrelated_process_a_descendant()
    {
        var rig = new Rig();
        rig.Nodes[102].Started = T0.AddSeconds(-1);
        using var held = rig.Open();
        Assert.Null(held.Terminate());
        Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Equal(new[] { 101 }, rig.Terminated);
        Assert.True(rig.Nodes[102].Running);
        Assert.True(rig.Nodes[103].Running);
    }

    [Fact]
    public async Task A_descendant_that_survives_termination_prevents_an_ended_result()
    {
        var rig = new Rig();
        rig.Nodes[103].Survive = true;
        using var held = rig.Open();
        Assert.Null(held.Terminate());
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Contains("still running", held.ExitProblem);
    }

    [Fact]
    public async Task A_child_created_during_termination_is_verified_and_ended_before_success()
    {
        var rig = new Rig();
        rig.Nodes[102].OnTerminate = () => rig.Nodes.Add(105, new Node(102, T0.AddSeconds(3)));
        using var held = rig.Open();
        Assert.Null(held.Terminate());
        Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Contains(105, rig.Terminated);
        Assert.False(rig.Nodes[105].Running);
    }

    [Fact]
    public async Task Lost_snapshot_evidence_after_termination_is_reported_as_unknown_not_ended()
    {
        var rig = new Rig();
        using var held = rig.Open();
        Assert.Null(held.Terminate());
        rig.UnknownSnapshot = true;
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Contains("complete process tree", held.ExitProblem);
    }

    [Fact]
    public void A_descendant_replaced_between_discovery_and_open_is_not_terminated_or_traversed()
    {
        var rig = new Rig();
        rig.BeforeOpen = id =>
        {
            if (id == 102) { rig.Nodes[id].Started = T0.AddMinutes(1); }
        };
        using var held = rig.Open();
        Assert.NotNull(held.Terminate());
        Assert.Empty(rig.Terminated);
    }

    [Fact]
    public void Partial_verification_releases_every_held_handle_when_the_stop_is_disposed()
    {
        var rig = new Rig();
        rig.Nodes[103].Refusal = "owner";
        var held = rig.Open();
        Assert.NotNull(held.Terminate());
        Assert.Equal(2, rig.Handles);
        held.Dispose();
        Assert.Equal(0, rig.Handles);
    }

    [Fact]
    public async Task Continued_spawning_exhausts_the_bounded_rescan_without_claiming_ended()
    {
        var rig = new Rig();
        var next = 105;
        void SpawnAfter(int id) => rig.Nodes[id].OnTerminate = () =>
        {
            var child = next++;
            rig.Nodes.Add(child, new Node(id, T0.AddSeconds(child)));
            SpawnAfter(child);
        };
        SpawnAfter(103);
        using var held = rig.Open();
        Assert.Null(held.Terminate());
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Equal(109, next);
        Assert.Contains("kept changing", held.ExitProblem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_reports_a_surviving_descendant_as_failed_and_preserves_confirmation_and_replay(bool working)
    {
        const string id = "77777777-7777-4777-8777-777777777777";
        using var f = new Fixture();
        var events = f.CopilotSession(id, 101);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        if (!working) { Fixture.Append(events, Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4)); }
        f.Processes.Run(AgentKind.Copilot, 101, startedAt: T0);
        await using var source = f.Source();
        await source.RefreshAsync();
        var rig = new Rig();
        rig.Nodes[103].Survive = true;
        var stopper = new StopService(new DeliveryGate(), source, f.Processes, new NoBridge(), new NoKeys(), rig.Control(),
            new StopOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, TerminateWait = TimeSpan.Zero });
        var request = new StopRequest { Id = id, Request = Guid.NewGuid().ToString("D") };
        if (working)
        {
            var first = await stopper.StopAsync(request);
            Assert.Equal(StopOutcome.Confirm, first.Outcome);
            Assert.Empty(rig.Terminated);
            request = request with { Request = Guid.NewGuid().ToString("D"), Confirm = first.Confirm };
        }
        var result = await stopper.StopAsync(request);
        Assert.Equal(StopOutcome.Failed, result.Outcome);
        Assert.Null(result.Forced);
        Assert.Equal(result, await stopper.StopAsync(request));
        Assert.Equal(new[] { 101, 102, 103 }, rig.Terminated);
        Assert.True(rig.Nodes[103].Running);
        Assert.Equal(0, rig.Handles);
    }

    sealed class NoBridge : IAgentHaBridgeProbe
    {
        public string? Active() => null;
    }

    sealed class NoKeys : IConsoleInjector
    {
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, "fixture only"));
    }

    sealed class Node(int parent, DateTimeOffset started)
    {
        public int Parent = parent;
        public DateTimeOffset Started = started;
        public bool Running = true, UnknownStart, Survive;
        public string? Refusal;
        public Action? OnTerminate;
    }

    sealed class Rig
    {
        public Dictionary<int, Node> Nodes { get; } = new()
        {
            [101] = new(0, T0),
            [102] = new(101, T0.AddSeconds(1)),
            [103] = new(102, T0.AddSeconds(2)),
            [104] = new(900, T0),
        };
        public List<int> Terminated { get; } = [];
        public bool UnknownSnapshot;
        public int Handles;
        public Action<int>? BeforeOpen;

        public IHeldProcess Open() => Control().Open(101, T0).Process!;

        public IProcessControl Control() => new ProcessTreeControl(OpenSingle,
            () => UnknownSnapshot ? null : Nodes.Where(p => p.Value.Running).ToDictionary(p => p.Key, p => p.Value.Parent),
            id => Nodes.TryGetValue(id, out var n) && n.Running && !n.UnknownStart ? n.Started : null);

        HeldOpen OpenSingle(int id, DateTimeOffset started)
        {
            BeforeOpen?.Invoke(id);
            if (!Nodes.TryGetValue(id, out var node) || !node.Running || node.Started != started) { return HeldOpen.Exited(); }
            if (node.Refusal is { } why) { return HeldOpen.Refused(why); }
            Handles++;
            return HeldOpen.Of(new Held(this, id, node));
        }

        sealed class Held(Rig rig, int id, Node node) : IHeldProcess
        {
            public int Id => id;
            public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(!node.Running);
            public string? Terminate()
            {
                rig.Terminated.Add(id);
                node.Running = node.Survive;
                node.OnTerminate?.Invoke();
                return null;
            }
            public void Dispose() => rig.Handles--;
        }
    }
}
