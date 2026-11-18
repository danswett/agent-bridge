using AgentBridge.Host.Delivery;
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

        public IHeldProcess Open() => new ProcessTreeControl(OpenSingle,
            () => UnknownSnapshot ? null : Nodes.Where(p => p.Value.Running).ToDictionary(p => p.Key, p => p.Value.Parent),
            id => Nodes.TryGetValue(id, out var n) && n.Running && !n.UnknownStart ? n.Started : null)
            .Open(101, T0).Process!;

        HeldOpen OpenSingle(int id, DateTimeOffset started)
        {
            if (!Nodes.TryGetValue(id, out var node) || !node.Running || node.Started != started) { return HeldOpen.Exited(); }
            if (node.Refusal is { } why) { return HeldOpen.Refused(why); }
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
            public void Dispose() { }
        }
    }
}
