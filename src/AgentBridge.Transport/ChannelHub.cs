using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Transport;

// Noise channels multiplexed over one relay connection. Either side can open a channel;
// a host mostly accepts them, a client mostly opens them, and both can do both.
public sealed class ChannelHub
{
    readonly RelayClient relay;
    readonly P256KeyPair key;
    readonly string name;
    readonly string kind;
    readonly ConcurrentDictionary<(string Peer, ulong Id), SecureChannel> channels = new();

    // Serves a peer's request: (channel, op, data) -> result. Null means no service, and
    // the peer is told "unknown op". An exception's message goes back as the error.
    public Func<SecureChannel, string, JsonElement, Task<object?>>? Handler { get; set; }
    // Installed before relay callbacks become reachable; applies in both directions.
    public Func<SecureChannel, bool>? PeerAllowed { get; set; }
    // A peer opened a channel to this device and the handshake completed. Raised on the
    // receive path: start long work elsewhere, or later frames wait for it.
    public event Action<SecureChannel>? Accepted;
    public event Action<string, Exception>? Rejected;

    // How results, requests and events are written. Set the protocol's options (camelCase,
    // its converters) once, before the first channel.
    public JsonSerializerOptions JsonOptions { get; set; } = JsonSerializerOptions.Default;

    // Peers are not trusted to clean up after themselves (a caller that exits never says
    // so, and a hostile one can open handshakes it never finishes), so the hub bounds what
    // it keeps: unfinished handshakes expire, channels go when their peer leaves presence,
    // and past the cap new handshakes are ignored.
    public int MaxChannels { get; set; } = 256;
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
    // A presence list can be a moment older than a peer's first frame, so a channel this
    // young is spared when its peer is missing from one.
    public TimeSpan PresenceGrace { get; set; } = TimeSpan.FromSeconds(5);
    // Events held per channel for a reader that has fallen behind (SecureChannel.Events).
    public int EventBufferCapacity { get; set; } = 1024;
    public int ChannelCount => channels.Count;
    public RelayClient Relay => relay;

    // IK (#603). As a responder: whether a channel may open with IK, said in this hub's
    // introductions so an opener knows to try it. As an opener: the responders' keys learned from
    // them, kept by the owner across relay connections (a reconnect makes a new hub); null opens
    // every channel with XX.
    public bool AcceptsIk { get; set; } = true;
    public KnownResponders? KnownPeers { get; set; }
    // The ops this hub answers from an IK first message, before the opener has proved it is live:
    // only what is safe to run again, since a relay can replay that message. Answered after the
    // same PeerAllowed check as any request.
    public IReadOnlyCollection<string> EarlyOps { get; set; } = [];
    // How long an IK opening waits for its answer before trying XX instead: a responder that no
    // longer takes IK (an older version put back) ignores it.
    public TimeSpan IkPatience { get; set; } = TimeSpan.FromSeconds(5);
    // Off only for a test playing a peer from before deflate.
    internal bool SaysInflates { get; set; } = true;

    public ChannelHub(RelayClient relay, P256KeyPair key, string name, string kind, Func<SecureChannel, bool>? peerAllowed = null)
    {
        this.relay = relay;
        this.key = key;
        this.name = name;
        this.kind = kind;
        PeerAllowed = peerAllowed;
        relay.OnData = OnDataAsync;
        relay.PresenceChanged += DropAbsent;
        // The relay could not deliver a frame because the peer has no connection. A peer's
        // channels never outlive its connection (a reconnect makes a new hub), so none of
        // them can carry anything again.
        relay.Undeliverable += peer =>
        {
            foreach (var (k, ch) in channels)
            {
                if (k.Peer == peer) { Forget(ch, new IOException("peer is not connected")); }
            }
        };
        // Nothing crosses a channel once its relay connection has gone, and a reconnect makes
        // a new hub; ending them now tells waiting requests and subscriptions at once.
        relay.Completion.ContinueWith(_ =>
        {
            foreach (var ch in channels.Values) { Forget(ch, new IOException("relay connection ended")); }
        }, TaskScheduler.Default);
    }

    internal void Forget(SecureChannel ch, Exception e)
    {
        channels.TryRemove(new KeyValuePair<(string, ulong), SecureChannel>((ch.PeerId, ch.Id), ch));
        ch.Fail(e);
    }

    internal void RequirePeer(SecureChannel ch)
    {
        if (PeerAllowed?.Invoke(ch) != false) { return; }
        Forget(ch, new IOException("the peer is no longer authorized"));
        _ = CloseAsync(ch);
        throw new InvalidOperationException("the peer is not authorized");
    }

    public void RevalidatePeers()
    {
        foreach (var ch in channels.Values)
        {
            // A partial handshake can expose a static key before curve validation completes.
            if (!ch.IsOpen) { continue; }
            try { RequirePeer(ch); }
            catch (InvalidOperationException) when (ch.Closed.IsCompleted) { }
        }
    }

    void DropAbsent(IReadOnlyList<PresenceEntry> present)
    {
        var online = present.Select(p => p.Id).ToHashSet();
        var settled = Environment.TickCount64 - (long)PresenceGrace.TotalMilliseconds;
        var spared = false;
        foreach (var (k, ch) in channels)
        {
            if (online.Contains(k.Peer)) { continue; }
            if (ch.CreatedAt < settled) { Forget(ch, new IOException("peer left")); }
            else { spared = true; }
        }
        // A spared channel's peer may really have gone, and presence only says so again on
        // its next change, which could be never: a phone that drops within the grace of
        // opening a channel would leave it (and a host's subscription on it) running until
        // the host restarts. So look again once the grace has passed, at presence as it is
        // then.
        if (spared)
        {
            _ = Task.Delay(PresenceGrace + TimeSpan.FromMilliseconds(50)).ContinueWith(_ =>
            {
                if (!relay.Completion.IsCompleted) { DropAbsent(relay.Presence); }
            }, TaskScheduler.Default);
        }
    }

    void PruneHandshakes()
    {
        var cutoff = Environment.TickCount64 - (long)HandshakeTimeout.TotalMilliseconds;
        foreach (var (_, ch) in channels)
        {
            if (!ch.Handshake.IsComplete && ch.CreatedAt < cutoff) { Forget(ch, new TimeoutException("handshake not completed")); }
        }
    }

    // Binds the handshake to both routing ids and the channel, so a relay cannot splice
    // one channel's messages into another or present a channel to a different device.
    // Still says s4: the published PWA computes the same prologue, so changing it is a
    // protocol version.
    static byte[] Prologue(ulong channel, string initiator, string responder)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes($"agent-bridge-s4|{initiator}|{responder}|"));
        var c = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(c, channel);
        b.AddRange(c);
        return [.. b];
    }

    SecureChannel NewChannel(string peerId, ulong id, bool initiator) =>
        new(this, peerId, id, initiator,
            new XXHandshake(initiator, key, initiator ? Prologue(id, relay.DeviceId, peerId) : Prologue(id, peerId, relay.DeviceId)),
            Math.Max(1, EventBufferCapacity));

    static ulong NewId() => BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));

    // What this device says of itself in a handshake. A responder adds which ops it answers from
    // an IK first message when it takes IK at all (ik), and, in an IK reply, its answer (early).
    byte[] Intro(bool responder, JsonNode? early = null)
    {
        var o = new JsonObject();
        // Left out when unset, which only a test playing a peer that introduces itself wrongly does.
        if (name is not null) { o["name"] = name; }
        if (kind is not null) { o["kind"] = kind; }
        // This hub inflates messages (MessageDeflate), so its peers may send them deflated.
        if (SaysInflates) { o["deflate"] = true; }
        if (responder && AcceptsIk) { o["ik"] = new JsonArray([.. EarlyOps.Select(op => (JsonNode?)JsonValue.Create(op))]); }
        if (early is not null) { o["early"] = early; }
        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    // early: the request the caller will make first (hello). To a responder that takes it early
    // the IK first message carries it, and the reply its answer: the channel's first request, if it
    // is exactly that, needs no round trip of its own. Otherwise it is only sent once open.
    public async Task<SecureChannel> OpenAsync(string peerId, TimeSpan? timeout = null, EarlyRequest? early = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        if (KnownPeers is { } cache && cache.Find(peerId) is { } known)
        {
            var started = Stopwatch.GetTimestamp();
            try { return await OpenIkAsync(peerId, known, early, limit < IkPatience ? limit : IkPatience); }
            // Whatever the reason (unanswered, the responder closing it, a reply that does not
            // decrypt), XX finds out afresh and learns the key again if it still holds.
            catch (Exception e) when (e is TimeoutException || EndsChannel(e) || e is IOException)
            {
                cache.Forget(peerId, known);
            }
            limit -= Stopwatch.GetElapsedTime(started);
            if (limit <= TimeSpan.Zero) { throw new TimeoutException("the peer did not answer"); }
        }
        return await OpenXxAsync(peerId, limit);
    }

    async Task<SecureChannel> OpenXxAsync(string peerId, TimeSpan timeout)
    {
        var id = NewId();
        var ch = NewChannel(peerId, id, true);
        channels[(peerId, id)] = ch;
        try
        {
            await SendAsync(ch, ChannelKind.Msg1, ch.Handshake.WriteMessage([]));
            await ch.Ready.Task.WaitAsync(timeout);
            return ch;
        }
        catch (Exception e)
        {
            Forget(ch, e);
            throw;
        }
    }

    async Task<SecureChannel> OpenIkAsync(string peerId, KnownResponders.Responder known, EarlyRequest? early, TimeSpan timeout)
    {
        var id = NewId();
        var ch = new SecureChannel(this, peerId, id, true, new IKHandshake(true, key, Prologue(id, relay.DeviceId, peerId), known.Static), Math.Max(1, EventBufferCapacity));
        JsonNode? asked = null;
        if (early is not null && known.Early.Contains(early.Op))
        {
            ch.ExpectEarly(early.Op, early.Data);
            asked = new JsonObject { ["op"] = early.Op };
            // Omitted rather than null, as a request without data is sent.
            if (JsonNode.Parse(ch.EarlyData!) is { } data) { asked["data"] = data; }
        }
        channels[(peerId, id)] = ch;
        try
        {
            var first = Intro(false, asked);
            // Too large to go first: sent as an ordinary request once open.
            if (asked is not null && first.Length + NoiseConst.PubLen * 2 + NoiseConst.TagLen * 2 > NoiseConst.MaxMessage)
            {
                ch.DropEarly();
                first = Intro(false);
            }
            await SendAsync(ch, ChannelKind.IkMsg1, ch.Handshake.WriteMessage(first));
            await ch.Ready.Task.WaitAsync(timeout);
            return ch;
        }
        catch (Exception e)
        {
            // The responder may answer after all: told to let go of it rather than keep it until
            // this device leaves presence.
            try { await SendAsync(ch, ChannelKind.Close, []); }
            catch (Exception) { }
            Forget(ch, e);
            throw;
        }
    }

    internal Task SendAsync(SecureChannel ch, ChannelKind kind, byte[] body) => SendAsync(ch, kind, () => body, default);

    // Ends a channel this device has finished with, telling the peer so it can let go at once.
    public async Task CloseAsync(SecureChannel ch)
    {
        try { await SendAsync(ch, ChannelKind.Close, []); }
        catch (Exception) { }
        Forget(ch, new IOException("closed"));
    }

    internal Task SendAsync(SecureChannel ch, ChannelKind kind, Func<byte[]> body, CancellationToken ct) => relay.SendAsync(ch.PeerId, () =>
    {
        var b = body();
        var payload = new byte[Wire.ChannelHeader + b.Length];
        payload[0] = (byte)kind;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), ch.Id);
        b.CopyTo(payload, Wire.ChannelHeader);
        return payload;
    }, ct);

    async Task OnDataAsync(string from, byte[] payload)
    {
        if (payload.Length < Wire.ChannelHeader) { return; }
        var kind = (ChannelKind)payload[0];
        var id = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(1, 8));
        var body = payload[Wire.ChannelHeader..];
        channels.TryGetValue((from, id), out var ch);
        try
        {
            switch (kind)
            {
                case ChannelKind.Msg1 when ch is null:
                    PruneHandshakes();
                    if (channels.Count >= MaxChannels) { return; }
                    ch = NewChannel(from, id, false);
                    channels[(from, id)] = ch;
                    ch.Handshake.ReadMessage(body);
                    await SendAsync(ch, ChannelKind.Msg2, ch.Handshake.WriteMessage(Intro(true)));
                    break;
                case ChannelKind.Msg2 when ch is { Initiator: true, Handshake: XXHandshake }:
                    Learn(ch, ReadIntro(ch, ch.Handshake.ReadMessage(body)));
                    await SendAsync(ch, ChannelKind.Msg3, ch.Handshake.WriteMessage(Intro(false)));
                    ch.Ready.TrySetResult();
                    break;
                case ChannelKind.Msg3 when ch is { Initiator: false, Handshake: XXHandshake }:
                    ReadIntro(ch, ch.Handshake.ReadMessage(body));
                    ch.Ready.TrySetResult();
                    Accepted?.Invoke(ch);
                    break;
                case ChannelKind.IkMsg1 when ch is null && AcceptsIk:
                    PruneHandshakes();
                    if (channels.Count >= MaxChannels) { return; }
                    ch = new SecureChannel(this, from, id, false, new IKHandshake(false, key, Prologue(id, from, relay.DeviceId)), Math.Max(1, EventBufferCapacity));
                    channels[(from, id)] = ch;
                    var opening = ReadIntro(ch, ch.Handshake.ReadMessage(body));
                    // Off the receive path: an early answer runs the handler. Nothing else can
                    // arrive on this channel until the reply has gone, since only it lets the
                    // opener encrypt.
                    _ = AnswerIkAsync(ch, opening.TryGetProperty("early", out var asked) ? asked.Clone() : null);
                    break;
                case ChannelKind.IkMsg2 when ch is { Initiator: true, Handshake: IKHandshake { IsComplete: false } }:
                    var reply = ReadIntro(ch, ch.Handshake.ReadMessage(body));
                    Learn(ch, reply);
                    if (reply.TryGetProperty("early", out var answer) && answer.ValueKind == JsonValueKind.Object) { ch.AnsweredEarly(answer); }
                    ch.Ready.TrySetResult();
                    break;
                case ChannelKind.Transport when ch is { Handshake: { IsComplete: true, Receive: { } receive } }:
                    RequirePeer(ch);
                    var message = JsonDocument.Parse(MessageDeflate.Unpack(receive.DecryptWithAd([], body), SecureChannel.MaxMessageBytes)).RootElement;
                    // A response has re, an event ev, a request op. Anything else is a frame
                    // from a newer peer this one does not know, and is ignored.
                    if (message.TryGetProperty("re", out _)) { ch.Complete(message); }
                    else if (message.TryGetProperty("ev", out _)) { ch.Deliver(message); }
                    else if (message.TryGetProperty("op", out var op)) { _ = ServeAsync(ch, message.GetProperty("id").GetInt64(), op.GetString()!, message.TryGetProperty("data", out var d) ? d : default); }
                    break;
                case ChannelKind.Close when ch is not null:
                    Forget(ch, new IOException("peer closed the channel"));
                    break;
            }
        }
        catch (Exception e) when (EndsChannel(e))
        {
            // A frame that fails authentication ends the channel; Noise state cannot resync.
            if (ch is not null) { Forget(ch, e); }
            // An IK opening this side cannot take: said at once, so the opener tries XX now
            // rather than when its patience runs out.
            if (kind == ChannelKind.IkMsg1 && ch is not null)
            {
                try { await SendAsync(ch, ChannelKind.Close, []); }
                catch (Exception) { }
            }
            Rejected?.Invoke(from, e);
        }
    }

    async Task AnswerIkAsync(SecureChannel ch, JsonElement? asked)
    {
        try
        {
            var answer = asked is { } a ? await EarlyAsync(ch, a) : null;
            var reply = Intro(true, answer);
            // An answer too large to go in the reply is left out, and the opener asks again.
            if (reply.Length + NoiseConst.PubLen + NoiseConst.TagLen > NoiseConst.MaxMessage) { reply = Intro(true); }
            // The opener gave up meanwhile (its Close came while the answer was worked out).
            if (ch.Closed.IsCompleted) { return; }
            await ch.SendReplyAsync(ChannelKind.IkMsg2, () => ch.Handshake.WriteMessage(reply));
            Accepted?.Invoke(ch);
        }
        catch (Exception e)
        {
            Forget(ch, e);
            Rejected?.Invoke(ch.PeerId, e);
        }
    }

    // The answer to an IK first message's request, as a response without its re ({ok, result} or
    // {ok: false, error}), or null to leave it to be asked again once the channel is open: an op
    // not taken early (never run from the first message, which a relay can replay), or one with
    // work to follow its response.
    async Task<JsonNode?> EarlyAsync(SecureChannel ch, JsonElement asked)
    {
        if (Handler is null || !asked.TryGetProperty("op", out var o) || o.ValueKind != JsonValueKind.String || o.GetString() is not { } op || !EarlyOps.Contains(op)) { return null; }
        object? result;
        try
        {
            RequirePeer(ch);
            result = await Handler(ch, op, asked.TryGetProperty("data", out var d) ? d : default);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        if (result is null or ChannelReply) { return null; }
        return new JsonObject { ["ok"] = true, ["result"] = JsonSerializer.SerializeToNode(result, JsonOptions) };
    }

    // What a responder said of IK: kept for next time if it takes it, forgotten if it no longer does.
    void Learn(SecureChannel ch, JsonElement intro)
    {
        if (KnownPeers is not { } known) { return; }
        if (intro.TryGetProperty("ik", out var ik) && ik.ValueKind == JsonValueKind.Array)
        {
            known.Learn(ch.PeerId, ch.PeerStatic!, [.. ik.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]);
        }
        else { known.Forget(ch.PeerId); }
    }

    // What a frame that cannot be taken in ends a channel with: it fails authentication, or it is
    // authenticated but not what the protocol says (an introduction without a name, an event
    // without seq). A client holding the channel sees that exception on its pending calls and on
    // opening it, and treats it as a lost connection (RelayClientChannel, the app's fleet).
    public static bool EndsChannel(Exception e) => e is CryptographicException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException;

    JsonElement ReadIntro(SecureChannel ch, byte[] payload)
    {
        if (Wire.DeviceId(ch.PeerStatic!) != ch.PeerId) { throw new PeerMismatchException(); }
        var j = JsonDocument.Parse(payload).RootElement;
        ch.PeerName = j.GetProperty("name").GetString() ?? "";
        ch.PeerKind = j.GetProperty("kind").GetString() ?? "";
        ch.PeerInflates = j.TryGetProperty("deflate", out var z) && z.ValueKind == JsonValueKind.True;
        RequirePeer(ch);
        return j;
    }

    async Task ServeAsync(SecureChannel ch, long id, string op, JsonElement data)
    {
        byte[] reply;
        Func<IProtocolChannel, Task>? then = null;
        var served = new ServedRequest(id, ch.Serialize, b => ch.SendAsync(b));
        ServedRequest.Enter(served);
        try
        {
            RequirePeer(ch);
            var result = Handler is null ? null : await Handler(ch, op, data);
            if (result is ChannelReply r) { (result, then) = (r.Result, r.Then); }
            reply = ch.Serialize(result is null ? new { re = id, ok = false, error = $"unknown op {op}" } : new { re = id, ok = true, result });
            // The caller hears why, rather than waiting out its timeout for a response that
            // could never be sent.
            if (reply.Length > SecureChannel.MaxMessageBytes)
            {
                reply = ch.Serialize(new { re = id, ok = false, error = $"response too large ({reply.Length} bytes)" });
                then = null;
            }
        }
        catch (Exception e)
        {
            reply = ch.Serialize(new { re = id, ok = false, error = e.Message });
            then = null;
        }
        ServedRequest.Enter(null);
        // Any progress the handler gave goes first: a client takes the response as the end.
        await served.CloseAsync();
        var sent = false;
        try
        {
            await ch.SendAsync(reply);
            sent = true;
        }
        catch (Exception e) when (e is IOException or MessageTooLargeException) { return; }
        finally { served.Responded(sent); }
        if (then is null) { return; }
        // Then owns its errors; there is no request left to report them on.
        try { await then(ch); }
        catch (Exception) { }
    }
}

// A handler result with work that must follow its response on the wire. sessions.subscribe
// needs it: the client must have {revision, count} before the snapshot's upserts, and
// anything Then sends goes through the channel's send lock after the response has.
public sealed record ChannelReply(object Result, Func<IProtocolChannel, Task> Then);

// A request to make first, in an IK first message where the responder takes it early
// (ChannelHub.OpenAsync).
public sealed record EarlyRequest(string Op, object? Data = null);

// Responders' static keys, from handshakes with responders that said they take IK, for opening the
// next channel to each with IK (#603). Not secret: a device id is the hash of its key, so a key
// is only ever kept for the id it hashes to. Shared by an owner's hubs across relay connections.
public sealed class KnownResponders
{
    public sealed record Responder(byte[] Static, IReadOnlyList<string> Early);

    readonly ConcurrentDictionary<string, Responder> peers = new();

    public Responder? Find(string peerId) => peers.TryGetValue(peerId, out var r) ? r : null;

    public void Learn(string peerId, byte[] staticKey, IReadOnlyList<string> early)
    {
        if (Wire.DeviceId(staticKey) != peerId) { throw new PeerMismatchException(); }
        peers[peerId] = new Responder(staticKey, early);
    }

    public void Forget(string peerId) => peers.TryRemove(peerId, out _);

    // Only that entry: one learned again meanwhile, by another channel, stays.
    internal void Forget(string peerId, Responder was) => peers.TryRemove(new KeyValuePair<string, Responder>(peerId, was));
}
