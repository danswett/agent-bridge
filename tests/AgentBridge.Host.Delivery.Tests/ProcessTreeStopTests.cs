using System.Runtime.Versioning;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Every native operation is injected: the Windows facade never touches an OS process here.
[SupportedOSPlatform("windows")]
public sealed class ProcessTreeStopTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Stop_reports_the_whole_tree_preserves_confirmation_and_replays_without_retermination(bool working, bool survives)
    {
        var rig = new TreeRig();
        rig.Nodes[103].Survive = survives;
        await Run(rig, working, async (stopper, keys, request, source, logs) =>
        {
            if (working)
            {
                var first = await stopper.StopAsync(request);
                Assert.Equal(StopOutcome.Confirm, first.Outcome);
                Assert.Empty(rig.Terminated);
                request = request with { Request = Guid.NewGuid().ToString("D"), Confirm = first.Confirm };
            }
            var result = await stopper.StopAsync(request);
            Assert.Equal(survives ? StopOutcome.Failed : StopOutcome.Ended, result.Outcome);
            if (survives) { Assert.Null(result.Forced); Assert.Contains(logs, line => line.Contains("tree exit")); }
            else { Assert.True(result.Forced); Assert.Null(source.Get(request.Id)); }
            Assert.Equal(result, await stopper.StopAsync(request));
            Assert.Equal(new[] { 101, 102, 103 }, rig.Terminated);
            Assert.Equal(survives, rig.Nodes[103].Running);
            Assert.True(rig.Nodes[104].Running);
            Assert.Equal(0, rig.Handles);
        });
    }

    [Theory]
    [InlineData("owner", false)]
    [InlineData("elevation", true)]
    public async Task The_first_press_refuses_an_unverifiable_descendant_before_typing_or_confirmation(string reason, bool working)
    {
        var rig = new TreeRig();
        rig.Nodes[102].Refusal = reason;
        await Run(rig, working, async (stopper, keys, request, source, logs) =>
        {
            Assert.Equal(StopOutcome.Refused, (await stopper.StopAsync(request)).Outcome);
            Assert.Equal(0, keys.Calls);
            Assert.Empty(rig.Terminated);
            Assert.Equal(0, rig.Handles);
        });
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("handles")]
    [InlineData("time")]
    public async Task Uncertain_tree_evidence_skips_exit_typing_ends_the_root_and_reports_partial_failure(string problem)
    {
        var rig = new TreeRig();
        var clock = new FakeTimeProvider();
        if (problem == "snapshot") { rig.SnapshotError = 5; }
        else if (problem == "handles") { for (var id = 105; id < 170; id++) { rig.Nodes.Add(id, new TreeNode(101, TreeRig.T0.AddSeconds(id))); } }
        else { rig.BeforeOpen = id => { if (id != 101) { clock.Advance(TimeSpan.FromSeconds(1)); } }; }
        await Run(rig, false, async (stopper, keys, request, source, logs) =>
        {
            var result = await stopper.StopAsync(request);
            Assert.Equal(StopOutcome.Failed, result.Outcome);
            Assert.False(rig.Nodes[101].Running);
            Assert.Equal(0, keys.Calls);
            Assert.Contains(logs, line => line.Contains("tree exit"));
            Assert.Equal(0, rig.Handles);
        }, clock);
    }

    [Theory]
    [InlineData("reused", false)]
    [InlineData("vanished", false)]
    [InlineData("older", false)]
    [InlineData("reused", true)]
    [InlineData("vanished", true)]
    [InlineData("older", true)]
    public async Task Stop_preserves_preflight_race_uncertainty_until_the_final_outcome(string race, bool working)
    {
        var rig = new TreeRig();
        rig.Nodes.Remove(103);
        var raced = false;
        void Change(int id)
        {
            if (id != 102 || raced) { return; }
            raced = true;
            if (race == "reused") { rig.Nodes[id] = new TreeNode(900, TreeRig.T0.AddMinutes(1)); }
            else
            {
                rig.Nodes[id] = new TreeNode(101, TreeRig.T0.AddSeconds(1)) { Running = false };
                rig.Nodes.Add(103, new TreeNode(102, TreeRig.T0.AddSeconds(2)));
            }
        }
        if (race == "reused") { rig.BeforeCreated = Change; }
        else if (race == "vanished") { rig.BeforeOpen = Change; }
        else { rig.Nodes[102].Started = TreeRig.T0.AddSeconds(-1); rig.AfterCreated = Change; }
        await Run(rig, working, async (stopper, keys, request, source, logs) =>
        {
            if (working)
            {
                var first = await stopper.StopAsync(request);
                Assert.Equal(StopOutcome.Confirm, first.Outcome);
                Assert.Empty(rig.Terminated);
                request = request with { Request = Guid.NewGuid().ToString("D"), Confirm = first.Confirm };
            }
            var result = await stopper.StopAsync(request);
            Assert.True(raced);
            Assert.Equal(StopOutcome.Failed, result.Outcome);
            Assert.Equal(result, await stopper.StopAsync(request));
            Assert.False(rig.Nodes[101].Running);
            Assert.True(rig.Nodes[race == "reused" ? 102 : 103].Running);
            Assert.DoesNotContain(102, rig.Terminated);
            Assert.Equal(0, keys.Calls);
            Assert.Equal(0, rig.Handles);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_incomplete_inspector_snapshot_cannot_erase_an_armed_lineage_loss(bool unknownStart)
    {
        const string other = "88888888-8888-4888-8888-888888888888";
        var rig = new TreeRig();
        rig.Nodes.Remove(103);
        rig.BeforeOpen = id =>
        {
            if (id != 102 || !rig.Nodes[102].Running) { return; }
            rig.Nodes[102].Running = false;
            rig.Nodes.Add(103, new TreeNode(102, TreeRig.T0.AddSeconds(2)));
        };
        Fixture? fixture = null;
        await Run(rig, true, async (stopper, keys, request, source, logs) =>
        {
            var first = await stopper.StopAsync(request);
            Assert.Equal(StopOutcome.Confirm, first.Outcome);
            if (unknownStart) { fixture!.Processes.RunUndated(AgentKind.Copilot, 101); }
            else { fixture!.Processes.Exit(AgentKind.Copilot, 101); }
            Assert.Equal(StopOutcome.Ended, (await stopper.StopAsync(new StopRequest { Id = other, Request = Guid.NewGuid().ToString("D") })).Outcome);
            fixture.Processes.Run(AgentKind.Copilot, 101, startedAt: TreeRig.T0);
            await source.RefreshAsync();
            var result = await stopper.StopAsync(request with { Request = Guid.NewGuid().ToString("D"), Confirm = first.Confirm });
            Assert.Equal(StopOutcome.Failed, result.Outcome);
            Assert.False(rig.Nodes[101].Running);
            Assert.True(rig.Nodes[103].Running);
            Assert.Equal(1, keys.Calls);
            Assert.Equal(0, rig.Handles);
        }, prepare: f =>
        {
            fixture = f;
            var events = f.CopilotSession(other, 104);
            Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
            f.Processes.Run(AgentKind.Copilot, 104, startedAt: TreeRig.T0);
            rig.Nodes[104].OnTerminate = () => f.Processes.Exit(AgentKind.Copilot, 104);
        });
    }

    static async Task Run(TreeRig rig, bool working,
        Func<StopService, Keys, StopRequest, ISessionSource, List<string>, Task> check, TimeProvider? time = null, Action<Fixture>? prepare = null)
    {
        const string id = "77777777-7777-4777-8777-777777777777";
        using var f = new Fixture();
        var events = f.CopilotSession(id, 101);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        if (!working) { Fixture.Append(events, Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4)); }
        f.Processes.Run(AgentKind.Copilot, 101, startedAt: TreeRig.T0);
        rig.Nodes[101].OnTerminate = () => f.Processes.Exit(AgentKind.Copilot, 101);
        prepare?.Invoke(f);
        await using var source = f.Source();
        await source.RefreshAsync();
        var keys = new Keys();
        var logs = new List<string>();
        var control = new WindowsProcessControl(rig.Open, rig.Scan, rig.Created, time ?? new FakeTimeProvider());
        var stopper = new StopService(new DeliveryGate(), source, f.Processes, new NoBridge(), keys, control,
            new StopOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, Grace = TimeSpan.FromSeconds(1), TerminateWait = TimeSpan.Zero }, logs.Add);
        await check(stopper, keys, new StopRequest { Id = id, Request = Guid.NewGuid().ToString("D") }, source, logs);
    }

    sealed class NoBridge : IAgentHaBridgeProbe { public string? Active() => null; }
    sealed class Keys : IConsoleInjector
    {
        public int Calls;
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, "offline fixture")); }
    }
}
