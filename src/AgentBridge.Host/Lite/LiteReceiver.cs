using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.AgentEnvelope;
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
        // Nothing is verified while the mode is off; the request id is only read back for the ack.
        if (!mode.On) { return Refuse(Off, data); }
        if (channel.PeerKind != "host" || !channel.InPresence) { return Refuse(NotSameAccount, data); }
        if (!peers.TryFind(channel.PeerId, out var peer) || channel.PeerStatic is not { } staticKey
            || Wire.DeviceId(staticKey) != channel.PeerId) { return Refuse(NotPeer, data); }

        string? text;
        UnverifiedEnvelope envelope;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("envelope", out var e) || e.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String
            || (text = t.GetString()) is null || text.Length > MaxText
            || !Codec.TryBase64Url(e.GetString()!, out var bytes)) { return Refuse(Malformed, data); }
        try { envelope = Envelope.Parse(bytes); }
        catch (EnvelopeException x) { return Refuse(x.Code, data); }

        VerifiedEnvelope verified;
        try { verified = LiteProfile.Check(envelope, peer.SignPoint, channel.Account, Encoding.UTF8.GetBytes(text), time.GetUtcNow().ToUnixTimeSeconds()); }
        catch (EnvelopeException x) { return Refuse(x.Code, data); }

        // Signed fields now trusted: they must name this channel's peer and this machine.
        var body = verified.Body;
        if (body.Principal.Machine != channel.PeerId) { return Refuse(Sender, data, body.Request); }
        if (body.Principal.Add != Codec.Base64Url(SHA256.HashData(staticKey))) { return Refuse(AddMismatch, data, body.Request); }
        if (body.Target.Machine != channel.Self) { return Refuse(Target, data, body.Request); }
        // Only a verified request may take a place in the table, so a stranger cannot fill it.
        switch (Replays.Add(channel.PeerId + "/" + body.Request))
        {
            case ReplayStore.Result.Seen: return Refuse(Replay, data, body.Request);
            case ReplayStore.Result.Full: return Refuse(Busy, data, body.Request);
        }
        if (!Rates.Allow(body.Principal.Session + ">" + channel.PeerId + "/" + body.Target.Session)) { return Refuse(Rate, data, body.Request); }
        LiteDelivery result;
        try { result = await deliver.DeliverAsync(verified, text, ct); }
        catch (Exception x) when (x is not OperationCanceledException) { return Refuse(DeliveryFailed, data, body.Request); }
        return result.Typed
            ? new LiteAck("delivered", null, body.Request, mode.KillEpoch)
            : Refuse(result.Code ?? DeliveryFailed, data, body.Request);
    }

    LiteAck Refuse(string code, JsonElement data, string? request = null) =>
        new("refused", code, request ?? RequestOf(data), mode.KillEpoch);

    // The request id of an envelope that is not trusted, for the acknowledgement only; "" when it cannot be read.
    static string RequestOf(JsonElement data)
    {
        try
        {
            return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("envelope", out var e) && e.ValueKind == JsonValueKind.String
                && Codec.TryBase64Url(e.GetString()!, out var bytes) ? Envelope.Parse(bytes).Body.Request : "";
        }
        catch (EnvelopeException) { return ""; }
    }
}

// Request ids seen from verified senders, in memory only: a restart forgets them, and the envelope's 120 s lifetime
// bounds a replay across one (the durable store is R6). Holds at most Max ids and drops only expired ones, so a full
// table of live ids refuses rather than forgetting.
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

// Per sending session to receiving session: at most 6 a minute and 60 an hour, in memory. Only verified, non-replayed
// requests count. The table is bounded; a full one of live pairs refuses new pairs rather than forgetting.
public sealed class RateCap(TimeProvider time)
{
    public const int PerMinute = 6, PerHour = 60, MaxPairs = 1024;
    readonly Dictionary<string, Queue<DateTimeOffset>> hits = new(StringComparer.Ordinal);
    readonly object gate = new();

    public bool Allow(string pair)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (!hits.TryGetValue(pair, out var q))
            {
                if (hits.Count >= MaxPairs)
                {
                    foreach (var k in hits.Where(p => p.Value.Count == 0 || now - p.Value.Peek() >= TimeSpan.FromHours(1)).Select(p => p.Key).ToList()) { hits.Remove(k); }
                    if (hits.Count >= MaxPairs) { return false; }
                }
                hits[pair] = q = new();
            }
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromHours(1)) { q.Dequeue(); }
            if (q.Count >= PerHour || q.Count(t => now - t < TimeSpan.FromMinutes(1)) >= PerMinute) { return false; }
            q.Enqueue(now);
            return true;
        }
    }
}
