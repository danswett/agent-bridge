using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// A host made read-only in its config (delivery.readOnly), as the shadow host beside
// agent-ha-bridge is: nothing another device sends acts on this machine even with agent-ha-bridge
// not running, since read-only through design G5 alone lasted only while its daemon did. Every
// delivery service here is the real one, behind the gate serve puts them behind.
public sealed partial class LaunchFlowTests
{
    const string Asking = "77777777-7777-4777-8777-777777777777";

    sealed class AnyTarget : ITargetGuard
    {
        public string? Refuse(int processId) => null;
    }

    sealed class NoProcessControl : IProcessControl
    {
        public HeldOpen Open(int processId, DateTimeOffset startedAt) => throw new InvalidOperationException("a read-only host must never get as far as ending a process");
    }

    // Every action a phone could take, behind one gate, as serve wires them; with g5, behind the
    // host's one shared G5 check, as serve wires that too.
    SessionTransfers ServeEverything(string? readOnly, SharedAgentHaBridgeProbe? g5 = null)
    {
        var gate = new ReadOnlyGate((IAgentHaBridgeProbe?)g5 ?? probe, readOnly);
        var transfers = new SessionTransfers(host.Key, Path.Combine(f.Root, "profile", "transfers"));
        var launcher = Launcher(Config(), transfers: transfers, gate: gate);
        transfers.Launcher = launcher;
        transfers.Attach(host.Relay, host.Hub);
        var answers = new AnswerService(source, f.Processes, gate, new AnyTarget(), screens, new AnswerOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, CopilotRemoteApproval = true });
        var replies = new ReplyService(source, f.Processes, gate, new AnyTarget(), screens, new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true });
        var stops = new StopService(source, f.Processes, gate, screens, new NoProcessControl(), new StopOptions { CopilotHome = f.CopilotHome, PlatformSupported = true });
        host.Hub.Handler = new SessionService(source, new MachineInfo(host.Id, "Test host", "windows", "0.1.0"), answers: answers, replies: replies, stops: stops, launcher: launcher, transfers: transfers)
        {
            ReadOnly = () => readOnly ?? (g5 is null ? probe.Active() : g5.Recent()),
            G5 = g5,
        }.HandleAsync;
        return transfers;
    }

    [Fact]
    public async Task A_read_only_host_takes_nothing_from_a_phone_with_agent_ha_bridge_not_running_and_says_why()
    {
        var events = f.CopilotSession(Asking, 4242, name: "Asking session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("q1", "ask_user", new
        {
            message = "Which one?",
            requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b" } } }, required = new[] { "pick" } },
        }, 3));
        f.Processes.Run(AgentKind.Copilot, 4242);
        await source.RefreshAsync();
        probe.Reason = null;

        // The same services with the switch off offer everything: what is withdrawn is the switch's doing.
        ServeEverything(null);
        var ch = await OpenAsync();
        Assert.Superset(new HashSet<string> { Capabilities.SessionsAnswer, Capabilities.SessionsApprove, Capabilities.SessionsReply, Capabilities.SessionsStop, LaunchProtocol.Capability, TransferProtocol.ServeCapability },
            (await CapabilitiesAsync(ch)).ToHashSet());

        var transfers = ServeEverything(HostSettings.ReadOnlyReason);
        var hello = await RequestAsync(ch, Ops.Hello, new { });
        string[] offered = [.. hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];
        foreach (var action in new[] { Capabilities.SessionsAnswer, Capabilities.SessionsApprove, Capabilities.SessionsReply, Capabilities.SessionsStop, LaunchProtocol.Capability, TransferProtocol.Capability, TransferProtocol.ServeCapability })
        {
            Assert.DoesNotContain(action, offered);
        }
        Assert.Equal(HostSettings.ReadOnlyReason, hello.GetProperty("machine").GetProperty("readOnly").GetString());
        Assert.False(transfers.Serves);

        // Sent anyway, each is refused with the reason, and nothing is typed, started or ended.
        static (string?, string?) Said(JsonElement r) => (r.GetProperty("outcome").GetString(), r.TryGetProperty("reason", out var why) ? why.GetString() : null);
        var refused = ("refused", HostSettings.ReadOnlyReason);
        Assert.Equal(refused, Said(await RequestAsync(ch, Ops.SessionAnswer, new { id = Asking, question = "q1", request = Guid.NewGuid().ToString(), fields = new { pick = "a" } })));
        Assert.Equal(refused, Said(await RequestAsync(ch, Ops.SessionReply, new { id = Asking, request = Guid.NewGuid().ToString(), text = "hello" })));
        Assert.Equal(refused, Said(await RequestAsync(ch, Ops.SessionStop, new { id = Asking, request = Guid.NewGuid().ToString() })));
        Assert.Equal(refused, Pair(await LaunchAsync(ch, new { prompt = "go" })));
        Assert.Empty(screens.Typed);
        Assert.Empty(starter.Specs);
    }

    // #607: a hello asked agent-ha-bridge's check once for each capability it takes away, every
    // transfer serve included, uncached. Now every one of them comes from one shared look.
    [Fact]
    public async Task A_burst_of_hellos_offering_every_action_looks_for_agent_ha_bridge_once()
    {
        probe.Reason = null;
        var looks = new CountedProbe(probe);
        ServeEverything(null, new SharedAgentHaBridgeProbe(looks, new FakeTimeProvider()));
        var ch = await OpenAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.Superset(new HashSet<string> { Capabilities.SessionsAnswer, Capabilities.SessionsApprove, Capabilities.SessionsReply, Capabilities.SessionsStop, LaunchProtocol.Capability, TransferProtocol.ServeCapability },
                (await CapabilitiesAsync(ch)).ToHashSet());
        }

        Assert.Equal(1, looks.Count);
    }

    sealed class CountedProbe(IAgentHaBridgeProbe inner) : IAgentHaBridgeProbe
    {
        public int Count;

        public string? Active()
        {
            Interlocked.Increment(ref Count);
            return inner.Active();
        }
    }
}