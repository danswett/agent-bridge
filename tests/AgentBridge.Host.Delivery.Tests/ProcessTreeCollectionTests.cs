using AgentBridge.Host.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class ProcessTreeCollectionTests
{
    static IProcessControl Control(TreeRig rig, TimeProvider? time = null) => new ProcessTreeControl(rig.Open, rig.Evidence(), time ?? new FakeTimeProvider());
    static IHeldProcess Open(TreeRig rig, TimeProvider? time = null) => Control(rig, time).Open(101, TreeRig.T0).Process!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_current_descendants_are_ended_once_and_every_lease_is_released(bool older)
    {
        var rig = new TreeRig();
        if (older) { rig.Nodes[102].Started = TreeRig.T0.AddSeconds(-1); }
        using (var held = Open(rig))
        {
            Assert.Equal(older ? 2 : 3, rig.Handles);
            Assert.Null(held.Terminate());
            Assert.Null(held.Terminate());
            Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
            Assert.Equal(older ? new[] { 101 } : new[] { 101, 102, 103 }, rig.Terminated);
            Assert.True(rig.Nodes[104].Running);
            Assert.Equal(older, rig.Nodes[103].Running);
        }
        Assert.Equal(0, rig.Handles);
    }

    [Fact]
    public async Task Graceful_parent_exit_does_not_hide_a_previously_captured_grandchild()
    {
        var rig = new TreeRig();
        using var held = Open(rig);
        rig.Nodes[101].Running = rig.Nodes[102].Running = false;
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        held.Terminate();
        Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.False(rig.Nodes[103].Running);
    }

    [Fact]
    public async Task A_surviving_descendant_prevents_a_successful_exit_proof()
    {
        var rig = new TreeRig();
        rig.Nodes[103].Survive = true;
        using var held = Open(rig);
        held.Terminate();
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.NotNull(held.ExitProblem);
    }

    [Fact]
    public async Task A_child_created_during_termination_is_verified_before_exit_is_reported()
    {
        var rig = new TreeRig();
        rig.Nodes[102].OnTerminate = () => rig.Nodes.Add(105, new TreeNode(102, TreeRig.T0.AddSeconds(3)));
        using var held = Open(rig);
        held.Terminate();
        Assert.True(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Contains(105, rig.Terminated);
    }

    [Fact]
    public async Task Continued_spawning_exhausts_rescans_without_false_success()
    {
        var rig = new TreeRig();
        var next = 105;
        void SpawnAfter(int id) => rig.Nodes[id].OnTerminate = () =>
        {
            var child = next++;
            rig.Nodes.Add(child, new TreeNode(id, TreeRig.T0.AddSeconds(child)));
            SpawnAfter(child);
        };
        SpawnAfter(103);
        using var held = Open(rig);
        held.Terminate();
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.NotNull(held.ExitProblem);
    }

    [Theory]
    [InlineData("transient")]
    [InlineData("persistent")]
    [InlineData("post-open")]
    [InlineData("lost")]
    public async Task Snapshot_failures_never_leave_the_held_root_alive_or_falsely_prove_tree_exit(string failure)
    {
        var rig = new TreeRig { SnapshotFailures = failure == "transient" ? 1 : 0, SnapshotError = failure == "persistent" ? 5 : 18 };
        if (failure == "post-open") { rig.AfterOpen = id => { if (id == 102) { rig.SnapshotError = 5; } }; }
        using var held = Open(rig);
        held.Terminate();
        if (failure == "lost") { rig.SnapshotError = 5; }
        Assert.False(rig.Nodes[101].Running);
        Assert.Equal(failure == "transient", await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.Equal(failure is "persistent" or "post-open", rig.Nodes[103].Running);
        if (failure != "transient") { Assert.NotNull(held.ExitProblem); }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("elevation")]
    [InlineData("creation")]
    public void An_unverifiable_descendant_is_refused_in_first_press_preflight(string reason)
    {
        var rig = new TreeRig();
        if (reason == "creation") { rig.Nodes[102].UnknownStart = true; } else { rig.Nodes[102].Refusal = reason; }
        var opened = Control(rig).Open(101, TreeRig.T0);
        using var held = opened.Process;
        Assert.Null(held);
        Assert.NotNull(opened.Refusal);
        Assert.Empty(rig.Terminated);
        Assert.Equal(0, rig.Handles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stale_child_identity_and_vanished_ancestry_never_end_the_wrong_process_or_claim_success(bool reused)
    {
        var rig = new TreeRig();
        rig.Nodes.Remove(103);
        var raced = false;
        if (reused) { rig.BeforeCreated = id => { if (id == 102) { raced = true; rig.Nodes[id] = new TreeNode(900, TreeRig.T0.AddMinutes(1)); } }; }
        else { rig.BeforeOpen = id => { if (id == 102) { raced = true; rig.Nodes.Add(103, new TreeNode(102, TreeRig.T0.AddSeconds(2))); rig.Nodes[id].Running = false; } }; }
        var opened = Control(rig).Open(101, TreeRig.T0);
        using var held = opened.Process;
        held?.Terminate();
        var ended = held is not null && await held.WaitForExitAsync(TimeSpan.Zero);
        Assert.True(raced);
        Assert.False(ended);
        Assert.NotNull(opened.Refusal ?? held?.ExitProblem);
        Assert.DoesNotContain(102, rig.Terminated);
        Assert.True(rig.Nodes[reused ? 102 : 103].Running);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_and_fake_clock_limits_end_the_root_but_leave_the_tree_outcome_unknown(bool wide)
    {
        var rig = new TreeRig();
        var clock = new FakeTimeProvider();
        if (wide) { for (var id = 105; id < 170; id++) { rig.Nodes.Add(id, new TreeNode(101, TreeRig.T0.AddSeconds(id))); } }
        else { rig.BeforeOpen = id => { if (id != 101) { clock.Advance(TimeSpan.FromSeconds(1)); } }; }
        using var held = Open(rig, clock);
        held.Terminate();
        Assert.Contains(101, rig.Terminated);
        Assert.InRange(rig.MaxHandles, 1, wide ? 64 : 2);
        Assert.False(await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.NotNull(held.ExitProblem);
    }

    [Fact]
    public async Task An_unpinned_older_creation_read_cannot_hide_a_vanished_replacement_and_its_child()
    {
        var rig = new TreeRig();
        rig.Nodes.Remove(103);
        rig.Nodes[102].Started = TreeRig.T0.AddSeconds(-1);
        rig.AfterCreated = id =>
        {
            if (id != 102) { return; }
            rig.Nodes[id] = new TreeNode(101, TreeRig.T0.AddSeconds(1)) { Running = false };
            rig.Nodes.Add(103, new TreeNode(102, TreeRig.T0.AddSeconds(2)));
        };
        var opened = Control(rig).Open(101, TreeRig.T0);
        using var held = opened.Process;
        held?.Terminate();
        Assert.False(held is not null && await held.WaitForExitAsync(TimeSpan.Zero));
        Assert.NotNull(opened.Refusal ?? held?.ExitProblem);
        Assert.True(rig.Nodes[103].Running);
    }
}
