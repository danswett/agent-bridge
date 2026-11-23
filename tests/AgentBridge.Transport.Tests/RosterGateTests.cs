using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace AgentBridge.Transport.Tests;

// #55 R3b (#1359): the roster gate in ChannelHub. A host that enforces the signed device roster sets RosterGate; a device
// it does not approve for an op is told "approve this machine" and no op runs, on the XX path, on the IK path and for an
// early request. A hub with no gate (off and observe) is exactly what it was before.
[Trait("Category", "Network")]
public class RosterGateTests
{
    static async Task<(TestRelay Relay, Device Host, Device App)> PairAsync(Action<AgentBridge.Relay.RelayOptions>? configure = null)
    {
        var relay = await TestRelay.StartAsync(configure);
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var app = await relay.ConnectAsync(token, "app-b", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        return (relay, host, app);
    }

    static (Func<SecureChannel, string, JsonElement, Task<object?>> Handler, Func<int> Runs) Counting()
    {
        var runs = 0;
        return ((ch, op, data) => { Interlocked.Increment(ref runs); return Task.FromResult<object?>(new { ran = op }); }, () => Volatile.Read(ref runs));
    }

    [Fact]
    public async Task A_device_the_gate_does_not_approve_is_told_to_approve_this_machine_and_no_op_runs_over_XX()
    {
        var (relay, host, app) = await PairAsync();
        await using var lifetime = relay;
        var (handler, runs) = Counting();
        host.Hub.Handler = handler;
        host.Hub.RosterGate = (ch, op) => false;

        var ch = await app.Hub.OpenAsync(host.Id);
        var hello = await ch.RequestAsync("hello");
        var other = await ch.RequestAsync("sessions.list");

        foreach (var reply in new[] { hello, other })
        {
            Assert.False(reply.GetProperty("ok").GetBoolean());
            Assert.Equal(ChannelHub.NotApproved, reply.GetProperty("error").GetString());
            Assert.False(reply.TryGetProperty("result", out _));
        }
        Assert.Equal("approve this machine", ChannelHub.NotApproved);
        Assert.Equal(0, runs());
    }

    [Fact]
    public async Task A_device_the_gate_does_not_approve_gets_nothing_over_IK_and_its_early_request_never_runs()
    {
        var (relay, host, app) = await PairAsync();
        await using var lifetime = relay;
        var (handler, runs) = Counting();
        host.Hub.Handler = handler;
        host.Hub.EarlyOps = ["ping"];
        app.Hub.IkPatience = TimeSpan.FromSeconds(30);
        app.Hub.KnownPeers = new KnownResponders();
        await app.Hub.CloseAsync(await app.Hub.OpenAsync(host.Id));  // learns the host's key: the next opening is IK

        host.Hub.RosterGate = (ch, op) => false;
        var ch = await app.Hub.OpenAsync(host.Id, early: new EarlyRequest("ping"));
        var reply = await ch.RequestAsync("ping");

        Assert.IsType<IKHandshake>(ch.Handshake);
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(ChannelHub.NotApproved, reply.GetProperty("error").GetString());
        Assert.Equal(0, runs());
    }

    [Fact]
    public async Task A_device_the_gate_approves_is_served_exactly_as_without_a_gate_and_one_it_stops_approving_is_refused_at_once()
    {
        var (relay, host, app) = await PairAsync();
        await using var lifetime = relay;
        var (handler, runs) = Counting();
        host.Hub.Handler = handler;
        var approved = true;
        host.Hub.RosterGate = (ch, op) => approved;

        var ch = await app.Hub.OpenAsync(host.Id);
        Assert.True((await ch.RequestAsync("ping")).GetProperty("ok").GetBoolean());
        Assert.Equal(1, runs());

        approved = false;  // removed from the roster while the channel is open
        var refused = await ch.RequestAsync("ping");
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(ChannelHub.NotApproved, refused.GetProperty("error").GetString());
        Assert.Equal(1, runs());
    }

    [Fact]
    public async Task The_gate_is_asked_per_op_so_an_op_that_needs_a_senior_device_is_refused_to_one_on_probation()
    {
        var (relay, host, app) = await PairAsync();
        await using var lifetime = relay;
        var (handler, runs) = Counting();
        host.Hub.Handler = handler;
        var asked = new ConcurrentQueue<string>();
        host.Hub.RosterGate = (ch, op) => { asked.Enqueue($"{ch.PeerId}:{op}"); return op != "senior"; };

        var ch = await app.Hub.OpenAsync(host.Id);
        Assert.True((await ch.RequestAsync("ping")).GetProperty("ok").GetBoolean());
        var senior = await ch.RequestAsync("senior");
        Assert.Equal(ChannelHub.NotApproved, senior.GetProperty("error").GetString());
        Assert.Equal(1, runs());
        Assert.Equal([$"{app.Id}:ping", $"{app.Id}:senior"], asked);
    }

    // "Off is today's behavior": with no gate, and with a gate that approves everyone, the frames the relay carries for an
    // open and two requests are the same in kind and length as before this slice (the golden below was recorded on main
    // before the gate existed). The handshake's introductions are not changed in any mode here: that is a later slice.
    [Fact]
    public async Task With_no_gate_or_a_gate_that_approves_everyone_the_wire_is_what_it_was_before_the_gate()
    {
        async Task<string[]> FramesAsync(bool gate)
        {
            var frames = new ConcurrentQueue<string>();
            var (relay, host, app) = await PairAsync(o =>
            {
                var tap = o.Tap;
                o.Tap = (from, to, payload) => { tap?.Invoke(from, to, payload); frames.Enqueue($"{(payload[0])}:{payload.Length}"); };
            });
            await using var lifetime = relay;
            host.Hub.Handler = (c, op, d) => Task.FromResult<object?>(new { ran = op });
            if (gate) { host.Hub.RosterGate = (ch, op) => true; }
            var ch = await app.Hub.OpenAsync(host.Id);
            Assert.True((await ch.RequestAsync("ping")).GetProperty("ok").GetBoolean());
            Assert.True((await ch.RequestAsync("hello")).GetProperty("ok").GetBoolean());
            await app.Hub.CloseAsync(ch);
            // Not the Close that ends it, which races the end of the test.
            return [.. frames.Where(f => !f.StartsWith("0:", StringComparison.Ordinal) && !f.StartsWith("5:", StringComparison.Ordinal))];
        }

        var without = await FramesAsync(false);
        var approving = await FramesAsync(true);
        Assert.Equal(without, approving);
        Assert.Equal(Golden, without);
    }

    static readonly string[] Golden = ["1:74", "2:225", "3:150", "4:57", "4:67", "4:58", "4:68"];
}
