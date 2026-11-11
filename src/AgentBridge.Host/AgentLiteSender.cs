using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// How the sender reaches the target host. The production link is a secure channel over the relay;
// a test plays the peer.
public interface ILiteLink
{
    string Account { get; }
    bool PeerPresent(string peer);
    Task<JsonElement> RequestAsync(string peer, string op, object data, TimeSpan timeout, CancellationToken ct);
}

// AP5-lite send (#1351, #728): a local agent holding a valid credential asks this host to put a
// reply into a session on another host of the same account. Nothing here retries: the relay has
// no offline queue, so every attempt ends in exactly one outcome and a resend is the agent's choice.
public sealed class AgentLiteSender(string profileFolder, string machine, Func<bool> modeOn, Func<ILiteLink?> link, TimeProvider? time = null)
{
    public const string OfflineText = "that machine is offline; nothing was queued";

    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(10);
    // How often a waiting send looks at the mode again.
    public TimeSpan ModePoll { get; set; } = TimeSpan.FromMilliseconds(250);

    readonly TimeProvider clock = time ?? TimeProvider.System;

    // `credential` is what AgentCredentials.Of gave for the calling channel: null when it has none,
    // or it was revoked or expired. The principal is built from it, never from anything the agent says.
    public async Task<AgentLiteSendResult> SendAsync(AgentCredential? credential, AgentLiteSendRequest request, CancellationToken ct = default)
    {
        if (!modeOn()) { return new(AgentLite.Off, Detail: "agent messaging between machines is off"); }
        if (credential is null || credential.Scope is not (AgentProtocol.ScopeReadReply or AgentProtocol.ScopeReadReplyLaunch))
        {
            return Refused("credential", "an agent credential that may reply is needed");
        }
        if (request.Text.Length is 0 or > AgentLite.MaxTextChars) { return Refused("text", $"a reply is 1 to {AgentLite.MaxTextChars} characters"); }
        if (link() is not { } l) { return new(AgentLite.Undeliverable, Detail: "this host is not connected to the relay; nothing was queued"); }
        if (!l.PeerPresent(request.Machine)) { return new(AgentLite.Undeliverable, Detail: OfflineText); }

        byte[] envelope;
        var data = AgentLite.DataOf(request.Text);
        var requestId = Guid.NewGuid().ToString();
        try
        {
            using var key = AgentSignKey.Open(profileFolder);
            if (key is null) { return Refused("no-sign-key", "this host has no agent signing key"); }
            envelope = LiteProfile.Seal(
                new LiteSender(l.Account, machine, Codec.Base64Url(SHA256.HashData(key.PublicPoint)), credential.Session, Instance(credential),
                    Codec.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(credential.Id)).AsSpan(0, 16)), "copilot", "agent-bridge"),
                new LiteTarget(request.Machine, request.Session, request.Instance),
                requestId, data, clock.GetUtcNow().ToUnixTimeSeconds(), new KeySigner(key));
        }
        catch (EnvelopeException e) { return Refused(e.Code, e.Message); }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var asked = l.RequestAsync(request.Machine, AgentLite.Send,
            new AgentLiteDelivery { Envelope = Encoding.UTF8.GetString(envelope), Text = request.Text }, AckTimeout, stop.Token);
        try
        {
            var response = await WaitOrOffAsync(asked, ct);
            if (response is null) { return Unconfirmed("mode switched off"); }
            return Read(response.Value, requestId);
        }
        catch (TimeoutException) { return Unconfirmed("no acknowledgement in time"); }
        catch (IOException) { return new(AgentLite.Undeliverable, Detail: OfflineText); }
        finally
        {
            stop.Cancel();
            _ = asked.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    // Null when the mode went off while waiting: the host then stops waiting and says it cannot
    // tell, since the target may still have taken the message.
    async Task<JsonElement?> WaitOrOffAsync(Task<JsonElement> asked, CancellationToken ct)
    {
        while (true)
        {
            var done = await Task.WhenAny(asked, Task.Delay(ModePoll, ct));
            if (done == asked) { return modeOn() ? await asked : null; }
            if (!modeOn()) { return null; }
        }
    }

    static AgentLiteSendResult Read(JsonElement response, string requestId)
    {
        if (response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && response.TryGetProperty("result", out var result))
        {
            AgentLiteAck? ack = null;
            try { ack = result.Deserialize<AgentLiteAck>(ProtocolJson.Options); }
            catch (JsonException) { }
            // An answer to some other request is not an answer to this one.
            if (ack is null || ack.Request != requestId) { return Unconfirmed("the acknowledgement did not match the message"); }
            return ack.Outcome == AgentLite.Delivered ? new(AgentLite.Delivered)
                : ack.Outcome == AgentLite.Refused ? new(AgentLite.Refused, ack.Code)
                : Unconfirmed("the acknowledgement was not understood");
        }
        var error = response.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return new(AgentLite.Refused, "refused-by-target", error);
    }

    // The session's process, as design/agent-principals.md §4.5 names it, so a resumed session is
    // a different instance.
    string Instance(AgentCredential c) => Codec.Base64Url(SHA256.HashData(Framing.F(
        Encoding.UTF8.GetBytes("agent-bridge session instance v1"), Encoding.UTF8.GetBytes(machine), Encoding.UTF8.GetBytes(c.Session),
        Encoding.UTF8.GetBytes(c.RootPid.ToString()), Encoding.UTF8.GetBytes(c.RootStartUtc.UtcTicks.ToString()))).AsSpan(0, 12));

    static AgentLiteSendResult Refused(string code, string detail) => new(AgentLite.Refused, code, detail);
    static AgentLiteSendResult Unconfirmed(string detail) => new(AgentLite.Unconfirmed, Detail: detail);

    sealed class KeySigner(AgentSignKey key) : IEnvelopeSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> message) => key.Sign(message);
    }
}

// The production link: one short-lived secure channel per message, closed when the answer is in.
public sealed class RelayLiteLink(RelayClient relay, ChannelHub hub) : ILiteLink
{
    public string Account => relay.AccountId;
    public bool PeerPresent(string peer) => relay.Presence.Any(p => p.Id == peer && p.Id != relay.DeviceId);

    public async Task<JsonElement> RequestAsync(string peer, string op, object data, TimeSpan timeout, CancellationToken ct)
    {
        var ch = await hub.OpenAsync(peer, timeout);
        try { return await ch.RequestAsync(op, data, timeout, ct); }
        finally { await hub.CloseAsync(ch); }
    }
}
