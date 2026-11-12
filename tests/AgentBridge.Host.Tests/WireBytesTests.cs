using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1334 (#597 S1): what one subscriber that asked for patches, said what it shows (sessions.focus)
// and negotiated deflate is sent over 40 s. #597's goal is under 5 KB; the audit of 2026-11-03
// measured 95 KB for four sessions with none of the three. The real SessionService and
// SessionSource serve it, both on one fake clock, so 40 s costs no waiting.
[Trait("Category", "Network")]
public sealed class WireBytesTests : IAsyncLifetime
{
    // The workload, fixed: one focused session that changes every second, and four quiet ones
    // that change every 250 ms (the source's own coalesce interval), for 40 s.
    const int QuietSessions = 4;
    const int Seconds = 40;
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    const string Focused = "88888888-8888-4888-8888-888888888888";

    // Lowering the deflate floor from 512 to 150 bytes (#2171) cuts this unchanged workload from
    // 13,140 to 10,155 bytes: 2,985 of the 8,020-byte gap to #597's goal, still 5,035 over it.
    // The budget adds 1,045 bytes (10.3%) of margin; reaching the goal needs further cuts.
    const int Goal = 5 * 1024;
    const int Budget = 9_000;

    // What each event costs beyond its (deflated) JSON: the AEAD tag, the channel header and the
    // relay's data frame (type byte and the peer id).
    const int Overhead = 16 + Wire.ChannelHeader + 1 + Wire.IdBytes;

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
    readonly string[] quiet = new string[QuietSessions];
    TestRelay relay = null!;
    SessionSource source = null!;
    SessionService service = null!;
    Device host = null!;
    Device client = null!;
    string focused = "";

    static string QuietId(int i) => $"99999999-9999-4999-8999-99999999999{i}";

    public async Task InitializeAsync()
    {
        focused = f.CopilotSession(Focused, 100, name: "Focused session");
        f.Processes.Run(AgentKind.Copilot, 100);
        for (var i = 0; i < QuietSessions; i++)
        {
            quiet[i] = f.CopilotSession(QuietId(i), 101 + i, name: $"Quiet session {i}");
            f.Processes.Run(AgentKind.Copilot, 101 + i);
        }
        source = new SessionSource(f.Options(), f.Processes, time);
        await source.RefreshAsync();
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Time = time };
        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = service.HandleAsync;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    static async Task<ChannelEvent> NextAsync(SecureChannel ch)
    {
        using var cts = new CancellationTokenSource(Patience);
        return await ch.Events.ReadAsync(cts.Token);
    }

    // What the host's SecureChannel sent for one event: the envelope it serialises, deflated as
    // MessageDeflate.Pack does for a peer that said it inflates, plus the overhead.
    static int WireBytes(ChannelEvent e)
    {
        var plain = Encoding.UTF8.GetBytes($"{{\"ev\":{JsonSerializer.Serialize(e.Name)},\"seq\":{e.Seq},\"data\":{e.Data.GetRawText()}}}");
        return MessageDeflate.Pack(plain).Length + Overhead;
    }

    [Fact]
    public async Task A_subscriber_that_did_not_ask_for_handles_is_sent_session_ids_as_before()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        var ch = await client.Hub.OpenAsync(host.Id);
        var hello = await ch.RequestAsync(Ops.Hello, new { });
        Assert.Contains("sessions.handles", hello.GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true });
        ChannelEvent e;
        do { e = await NextAsync(ch); } while (e.Name != Events.SessionsSynced);
        Assert.False(e.Data.TryGetProperty("handles", out _));
        Fixture.Append(focused, Copilot.Assistant("changed", 4));
        await source.RefreshAsync();
        time.Advance(Step);
        e = await NextAsync(ch);
        Assert.Equal(Events.SessionPatch, e.Name);
        Assert.Equal(Focused, e.Data.GetProperty("id").GetString());
        Assert.False(e.Data.TryGetProperty("handle", out _));
    }

    [Fact]
    public async Task A_subscriber_with_patch_focus_and_deflate_stays_within_its_wire_budget_over_40_seconds_of_changes()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        var ch = await client.Hub.OpenAsync(host.Id);
        Assert.True(ch.PeerInflates, "the host must have said it inflates, or nothing is deflated");
        Assert.True((await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true, handles = true })).GetProperty("ok").GetBoolean());
        var snapshot = new List<ChannelEvent>();
        do { snapshot.Add(await NextAsync(ch)); } while (snapshot[^1].Name != Events.SessionsSynced);
        var handles = snapshot[^1].Data.GetProperty("handles").Deserialize<Dictionary<string, int>>()!;
        Assert.Equal(QuietSessions + 1, handles.Count);
        Assert.True((await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Focused })).GetProperty("ok").GetBoolean());

        var steps = Seconds * 4;
        for (var n = 0; n < steps; n++)
        {
            for (var i = 0; i < QuietSessions; i++)
            {
                Fixture.Append(quiet[i], Copilot.Assistant($"quiet {i} change {n}", 4 + n));
            }
            if (n % 4 == 0) { Fixture.Append(focused, Copilot.Assistant($"focused change {n / 4}", 4 + n)); }
            await source.RefreshAsync();
            time.Advance(Step);
        }
        // What the quiet interval was still holding at the 40 s mark is not counted: the next
        // release is after it.
        await Task.Delay(200);

        var wire = new SortedDictionary<string, (int Count, int Bytes)>();
        var total = 0;
        while (ch.Events.TryRead(out var e))
        {
            Assert.False(e.Data.TryGetProperty("id", out _));
            Assert.Contains(e.Data.GetProperty("handle").GetInt32(), handles.Values);
            var bytes = WireBytes(e);
            total += bytes;
            wire.TryGetValue(e.Name, out var so);
            wire[e.Name] = (so.Count + 1, so.Bytes + bytes);
        }
        Console.WriteLine($"wire bytes over {Seconds} s: {total} (goal {Goal}, over by {total - Goal}); " + string.Join(", ", wire.Select(p => $"{p.Key} x{p.Value.Count} = {p.Value.Bytes}")));

        // Not passing by sending nothing: changes arrived, as patches, and none as a whole upsert.
        Assert.True(wire.GetValueOrDefault(Events.SessionPatch).Count > 0, "no patch was sent");
        Assert.False(wire.ContainsKey(Events.SessionUpsert), "a whole upsert was sent after the snapshot");
        Assert.True(total <= Budget, $"{total} bytes on the wire in {Seconds} s is over the {Budget}-byte budget");
    }
}
