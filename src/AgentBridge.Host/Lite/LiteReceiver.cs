using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.AgentEnvelope;
using Codec = AgentBridge.Roster.Codec;
using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

// The acknowledgement of agent.lite.send. Code is null when delivered. KillEpoch is the receiver's, unsigned on purpose:
// it can only turn a mode off, never on.
public sealed record LiteAck(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Code,
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("killEpoch")] long KillEpoch);

// What the receiver needs to know about the channel a request came in on (read from the SecureChannel and the relay).
public sealed record LiteChannel(string PeerId, string PeerKind, byte[]? PeerStatic, string Self, string Account, bool InPresence);

// AP5-lite L4 (#728): serves agent.lite.send. Refuses in a fixed order, each with its own code, and answers
// delivered only when the delivery interface says it typed the text. Types nothing itself.
public sealed class LiteReceiver(ILiteMode mode, ILitePeers peers, ILiteDeliver deliver, TimeProvider? time = null)
{
    public const string Op = "agent.lite.send";
    public const int MaxText = 4096;
    public const string Off = "off", NotSameAccount = "notSameAccount", NotPeer = "notPeer", Replay = "replay", Busy = "busy",
        Malformed = "malformed", Rate = "rate", Sender = "lite-sender", AddMismatch = "lite-add", Target = "lite-target", DeliveryFailed = "deliveryFailed";

    readonly TimeProvider time = time ?? TimeProvider.System;
    public ReplayStore Replays { get; } = new(time ?? TimeProvider.System);
    public RateCap Rates { get; } = new(time ?? TimeProvider.System);

    public async Task<object?> ServeAsync(SecureChannel channel, JsonElement data, CancellationToken ct)
    {
        var seen = new LiteChannel(channel.PeerId, channel.PeerKind, channel.PeerStatic, channel.Relay.DeviceId, channel.Relay.AccountId,
            channel.Relay.Presence.Any(p => p.Id == channel.PeerId && p.Kind == "host"));
        return await ReceiveAsync(seen, data, ct);
    }

    public async Task<LiteAck> ReceiveAsync(LiteChannel channel, JsonElement data, CancellationToken ct)
    {
        if (!mode.On) { return new LiteAck("refused", Off, "", mode.KillEpoch); }
        if (channel.PeerKind != "host" || !channel.InPresence) { return Refuse(NotSameAccount); }
        if (!peers.TryFind(channel.PeerId, out var peer) || channel.PeerStatic is not { } staticKey
            || Wire.DeviceId(staticKey) != channel.PeerId) { return Refuse(NotPeer); }

        string? text; UnverifiedEnvelope envelope;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("envelope", out var e) || e.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String
            || (text = t.GetString()) is null || text.Length > MaxText
            || !Codec.TryBase64Url(e.GetString()!, out var bytes)) { return Refuse(Malformed); }
        try { envelope = Envelope.Parse(bytes); }
        catch (EnvelopeException x) { return Refuse(x.Code); }

        VerifiedEnvelope verified; try { verified = LiteProfile.Check(envelope, peer.SignPoint, channel.Account, Encoding.UTF8.GetBytes(text), time.GetUtcNow().ToUnixTimeSeconds()); }
        catch (EnvelopeException x) { return Refuse(x.Code); }

        var body = verified.Body; // trusted now: must name this channel's peer and this machine
        if (body.Principal.Machine != channel.PeerId) { return Refuse(Sender, body.Request); }
        if (body.Principal.Add != Codec.Base64Url(SHA256.HashData(staticKey))) { return Refuse(AddMismatch, body.Request); }
        if (body.Target.Machine != channel.Self) { return Refuse(Target, body.Request); }
        switch (Replays.Add(channel.PeerId + "/" + body.Request))
        {
            case ReplayStore.Result.Seen: return Refuse(Replay, body.Request);
            case ReplayStore.Result.Full: return Refuse(Busy, body.Request);
        }
        if (!Rates.Allow(channel.PeerId, body.Principal.Session + ">" + body.Target.Session)) { return Refuse(Rate, body.Request); }
        LiteDelivery result; try { result = await deliver.DeliverAsync(verified, text, ct); }
        catch (Exception x) when (x is not OperationCanceledException || !ct.IsCancellationRequested) { return Refuse(DeliveryFailed, body.Request); }
        return result.Typed
            ? new LiteAck("delivered", null, body.Request, mode.KillEpoch)
            : Refuse(result.Code ?? DeliveryFailed, body.Request);
    }

    LiteAck Refuse(string code, string request = "") => new("refused", code, request, mode.KillEpoch);

}

// Verified request ids, in memory (a restart forgets them; the 120 s envelope lifetime bounds that; durable store is R6).
// At most Max ids; only expired ones are dropped, so a full table refuses rather than forgets.
public sealed class ReplayStore(TimeProvider time)
{
    public enum Result { Added, Seen, Full }
    public const int Max = 4096;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(150);

    readonly Dictionary<string, DateTimeOffset> ids = new(StringComparer.Ordinal);
    readonly object gate = new();
    public int Count { get { lock (gate) { return ids.Count; } } }
    public Result Add(string key)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (ids.TryGetValue(key, out var until) && until > now) { return Result.Seen; }
            if (ids.Count >= Max)
            {
                foreach (var k in ids.Where(p => p.Value <= now).Select(p => p.Key).ToList()) { ids.Remove(k); }
                if (ids.Count >= Max) { return Result.Full; }
            }
            ids[key] = now + Lifetime;
            return Result.Added;
        }
    }
}

// Per sending session to receiving session: 6 a minute, 60 an hour, in memory. An active pair keeps its window; only idle
// pairs are dropped. One peer holds at most MaxPerPeer pairs, so it cannot starve the rest.
public sealed class RateCap(TimeProvider time)
{
    public const int PerMinute = 6, PerHour = 60, MaxPairs = 1024, MaxPerPeer = 64;
    readonly Dictionary<(string Peer, string Pair), Queue<DateTimeOffset>> hits = new();
    readonly object gate = new();

    public bool Allow(string peer, string pair)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            var key = (peer, pair);
            if (!hits.TryGetValue(key, out var q))
            {
                if (hits.Count >= MaxPairs || hits.Count(p => p.Key.Peer == peer) >= MaxPerPeer)
                {
                    foreach (var k in hits.Where(p => p.Value.Count == 0 || now - p.Value.Last() >= TimeSpan.FromHours(1)).Select(p => p.Key).ToList()) { hits.Remove(k); }
                    if (hits.Count >= MaxPairs || hits.Count(p => p.Key.Peer == peer) >= MaxPerPeer) { return false; }
                }
                hits[key] = q = new();
            }
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromHours(1)) { q.Dequeue(); }
            if (q.Count >= PerHour || q.Count(t => now - t < TimeSpan.FromMinutes(1)) >= PerMinute) { return false; }
            q.Enqueue(now);
            return true;
        }
    }
}