using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Transport;

// Noise channels multiplexed over one relay connection. Either side can open a channel;
// a host mostly accepts them, a client mostly opens them, and both can do both.
public sealed class ChannelHub
{
    readonly RelayClient relay;
    readonly P256KeyPair key;
    readonly byte[] intro;
    readonly ConcurrentDictionary<(string Peer, ulong Id), SecureChannel> channels = new();

    // Serves a peer's request: (channel, op, data) -> result. Null means no service, and
    // the peer is told "unknown op". An exception's message goes back as the error.
    public Func<SecureChannel, string, JsonElement, Task<object?>>? Handler { get; set; }
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

    public ChannelHub(RelayClient relay, P256KeyPair key, string name, string kind)
    {
        this.relay = relay;
        this.key = key;
        intro = JsonSerializer.SerializeToUtf8Bytes(new { name, kind });
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

    public async Task<SecureChannel> OpenAsync(string peerId, TimeSpan? timeout = null)
    {
        var id = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        var ch = NewChannel(peerId, id, true);
        channels[(peerId, id)] = ch;
        try
        {
            await SendAsync(ch, ChannelKind.Msg1, ch.Handshake.WriteMessage([]));
            await ch.Ready.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));
            return ch;
        }
        catch (Exception e)
        {
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
                    await SendAsync(ch, ChannelKind.Msg2, ch.Handshake.WriteMessage(intro));
                    break;
                case ChannelKind.Msg2 when ch is { Initiator: true }:
                    ReadIntro(ch, ch.Handshake.ReadMessage(body));
                    await SendAsync(ch, ChannelKind.Msg3, ch.Handshake.WriteMessage(intro));
                    ch.Ready.TrySetResult();
                    break;
                case ChannelKind.Msg3 when ch is { Initiator: false }:
                    ReadIntro(ch, ch.Handshake.ReadMessage(body));
                    ch.Ready.TrySetResult();
                    Accepted?.Invoke(ch);
                    break;
                case ChannelKind.Transport when ch is { Handshake.IsComplete: true }:
                    var message = JsonDocument.Parse(ch.Handshake.Receive!.DecryptWithAd([], body)).RootElement;
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
            Rejected?.Invoke(from, e);
        }
    }

    // What a frame that cannot be taken in ends a channel with: it fails authentication, or it is
    // authenticated but not what the protocol says (an introduction without a name, an event
    // without seq). A client holding the channel sees that exception on its pending calls and on
    // opening it, and treats it as a lost connection (RelayClientChannel, the app's fleet).
    public static bool EndsChannel(Exception e) => e is CryptographicException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException;

    void ReadIntro(SecureChannel ch, byte[] payload)
    {
        if (Wire.DeviceId(ch.PeerStatic!) != ch.PeerId) { throw new PeerMismatchException(); }
        var j = JsonDocument.Parse(payload).RootElement;
        ch.PeerName = j.GetProperty("name").GetString() ?? "";
        ch.PeerKind = j.GetProperty("kind").GetString() ?? "";
    }

    async Task ServeAsync(SecureChannel ch, long id, string op, JsonElement data)
    {
        byte[] reply;
        Func<IProtocolChannel, Task>? then = null;
        try
        {
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
        try { await ch.SendAsync(reply); }
        catch (Exception e) when (e is IOException or MessageTooLargeException) { return; }
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
