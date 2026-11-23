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
public sealed record LiteChannel(string PeerId, string PeerKind, byte[]? PeerStatic, string Self, string Account, bool InPresence)
{
    public static LiteChannel Of(SecureChannel c) => new(c.PeerId, c.PeerKind, c.PeerStatic, c.Relay.DeviceId, c.Relay.AccountId,
        c.Relay.Presence.Any(p => p.Id == c.PeerId && p.Kind == "host"));

    // The one trust rule for lite sends and kills (owner, Nov 19): a host the relay lists as present on this signed-in account,
    // whose channel static key hashes to its device id. No pin. Null when trusted, else the refusal code.
    public string? Untrusted => PeerKind != "host" || !InPresence || string.IsNullOrEmpty(Account) ? LiteReceiver.NotSameAccount
        : PeerStatic is not { } s || Wire.DeviceId(s) != PeerId ? LiteReceiver.NotPeer : null;
}

// AP5-lite L4 (#728): serves agent.lite.send. Refuses in a fixed order, each with its own code, and answers
// delivered only when the delivery interface says it typed the text. Types nothing itself.
public sealed class LiteReceiver(ILiteMode mode, ILiteDeliver deliver, TimeProvider? time = null)
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
        var seen = LiteChannel.Of(channel);
        return await ReceiveAsync(seen, data, ct);
    }

    public async Task<LiteAck> ReceiveAsync(LiteChannel channel, JsonElement data, CancellationToken ct)
    {
        if (!mode.On) { return new LiteAck("refused", Off, "", mode.KillEpoch); }
        if (channel.Untrusted is { } untrusted) { return Refuse(untrusted); }
        var staticKey = channel.PeerStatic!;
        // L6: the sender's kill epoch rides unsigned, believed only from this authenticated present host, and only turns off.
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("killEpoch", out var k) && k.ValueKind == JsonValueKind.Number
            && k.TryGetInt64(out var killEpoch) && killEpoch > mode.KillEpoch)
        {
            mode.RaiseEpoch(killEpoch);
            if (!mode.On) { return Refuse(Off); }
        }

        // signPoint is the sender's agent signing key. It is believed only because it arrived on this channel, which the
        // sender's device static key authenticates, and the envelope it verifies must still name that device and key.
        string? text; UnverifiedEnvelope envelope;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("envelope", out var e) || e.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("signPoint", out var p) || p.ValueKind != JsonValueKind.String
            || (text = t.GetString()) is null || text.Length > MaxText
            || !Codec.TryBase64Url(e.GetString()!, out var bytes) || !Codec.TryBase64Url(p.GetString()!, out var signPoint)) { return Refuse(Malformed); }
        try { envelope = Envelope.Parse(bytes); }
        catch (EnvelopeException x) { return Refuse(x.Code); }

        var probe = envelope.Body.Action == LiteProfile.ReceiptAction;
        if (probe && text.Length != 0) { return Refuse(Malformed, envelope.Body.Request); }
        VerifiedEnvelope verified; try { verified = probe ? LiteProfile.CheckReceipt(envelope, signPoint, channel.Account, time.GetUtcNow().ToUnixTimeSeconds()) : LiteProfile.Check(envelope, signPoint, channel.Account, Encoding.UTF8.GetBytes(text), time.GetUtcNow().ToUnixTimeSeconds()); }
        catch (EnvelopeException x) { return Refuse(x.Code); }

        var body = verified.Body; // trusted now: must name this channel's peer and this machine
        if (body.Principal.Machine != channel.PeerId) { return Refuse(Sender, body.Request); }
        if (body.Principal.Add != Codec.Base64Url(SHA256.HashData(staticKey))) { return Refuse(AddMismatch, body.Request); }
        if (body.Target.Machine != channel.Self) { return Refuse(Target, body.Request); }
        var key = channel.PeerId + "/" + body.Request;
        // A receipt lookup (#3875): what the first attempt of this id became, for the peer that made it. Never delivery, never added.
        if (probe)
        {
            if (!Rates.Allow(channel.PeerId, "receipt>" + body.Request)) { return Refuse(Rate, body.Request); }
            return Replays.Peek(key, body.Principal.SenderId) is { } o ? new LiteAck("receipt", o.Code is null ? o.State : o.State + ":" + o.Code, body.Request, mode.KillEpoch)
                : new LiteAck("unknown", null, body.Request, mode.KillEpoch);
        }
        var query = deliver as ILiteReplayQuery;
        var replay = false;
        switch (Replays.Add(key, body.Principal.SenderId))
        {
            case ReplayStore.Result.Seen:
                if (query is null) { return Refuse(Replay, body.Request); }
                replay = true; break;
            case ReplayStore.Result.Full: return Refuse(Busy, body.Request);
        }
        if (!replay && !Rates.Allow(channel.PeerId, body.Principal.SenderId + ">" + body.Target.Session)) { return Done(key, Refuse(Rate, body.Request)); }
        LiteDelivery result; try { result = replay ? await query!.QueryAsync(verified, text, ct) : await deliver.DeliverAsync(verified, text, ct); }
        // Keys may have gone before it failed or was cancelled, so the journal says unconfirmed, never refused (#3875).
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Replays.Record(key, "unconfirmed", "cancelled"); throw; }
        catch (Exception x) when (x is not OperationCanceledException || !ct.IsCancellationRequested) { Replays.Record(key, "unconfirmed", DeliveryFailed); return Refuse(DeliveryFailed, body.Request); }
        if (!result.Typed && result.Outcome is LiteKind.Pending or LiteKind.Queued or LiteKind.Unconfirmed)
        { return Done(key, new LiteAck(result.Outcome.Value.ToString().ToLowerInvariant(), result.Code, body.Request, mode.KillEpoch)); }
        return Done(key, result.Typed && result.Outcome is null or LiteKind.Delivered
            ? new LiteAck("delivered", null, body.Request, mode.KillEpoch)
            : Refuse(result.Code ?? DeliveryFailed, body.Request));
    }

    // What this id's first attempt became, for a later receipt lookup.
    LiteAck Done(string key, LiteAck ack) { Replays.Record(key, ack.Status, ack.Code); return ack; }

    LiteAck Refuse(string code, string request = "") => new("refused", code, request, mode.KillEpoch);

}

// Verified request ids, in memory (a restart forgets them; the 120 s envelope lifetime bounds that; durable store is R6).
// At most Max ids; only expired ones are dropped, so a full table refuses rather than forgets.
public sealed class ReplayStore(TimeProvider time)
{
    public enum Result { Added, Seen, Full }
    public const int Max = 4096;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(150);

    // What the first attempt became (docs/protocol/agent-lite.md "Receipt lookup", #3875): a state and nothing else, never the text.
    public readonly record struct Outcome(string State, string? Code);
    // The signed principal of the original request (a session, or a script's identity) keeps one sender from reading another's outcome on the same host.
    readonly record struct Entry(DateTimeOffset Until, Outcome Outcome, string Principal);
    readonly Dictionary<string, Entry> ids = new(StringComparer.Ordinal);
    readonly object gate = new();
    public int Count { get { lock (gate) { return ids.Count; } } }
    public Result Add(string key, string principal = "")
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (ids.TryGetValue(key, out var seen) && seen.Until > now) { return Result.Seen; }
            if (ids.Count >= Max)
            {
                foreach (var k in ids.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList()) { ids.Remove(k); }
                if (ids.Count >= Max) { return Result.Full; }
            }
            ids[key] = new(now + Lifetime, new Outcome("in-flight", null), principal);
            return Result.Added;
        }
    }

    // The first attempt's outcome, kept for the id's own lifetime; a later state (a lane request moving on) replaces it.
    public void Record(string key, string state, string? code)
    {
        lock (gate) { if (ids.TryGetValue(key, out var e) && e.Until > time.GetUtcNow()) { ids[key] = e with { Outcome = new(state, code) }; } }
    }

    // Looks an id up without adding it: null when it is unknown, expired, or was made by another principal.
    public Outcome? Peek(string key, string principal = "")
    {
        lock (gate) { return ids.TryGetValue(key, out var e) && e.Until > time.GetUtcNow() && string.Equals(e.Principal, principal, StringComparison.Ordinal) ? e.Outcome : null; }
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