using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using S2.Noise;

namespace S4;

// An end-to-end encrypted conversation with one peer device, carried as opaque frames
// through the relay. Requests and responses are JSON inside Noise transport messages.
public sealed class SecureChannel
{
    readonly ChannelHub hub;
    readonly SemaphoreSlim sendLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    long nextRequest;

    internal XXHandshake Handshake { get; }
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal long CreatedAt { get; } = Environment.TickCount64;

    public string PeerId { get; }
    public ulong Id { get; }
    public bool Initiator { get; }
    public string PeerName { get; internal set; } = "";
    public string PeerKind { get; internal set; } = "";
    public byte[]? PeerStatic => Handshake.RemoteStatic;
    public byte[] HandshakeHash => Handshake.HandshakeHash;

    internal SecureChannel(ChannelHub hub, string peerId, ulong id, bool initiator, XXHandshake handshake)
    {
        this.hub = hub; PeerId = peerId; Id = id; Initiator = initiator; Handshake = handshake;
    }

    public async Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref nextRequest);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            await SendJsonAsync(new { id, op, data });
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));
        }
        finally { pending.TryRemove(id, out _); }
    }

    internal async Task SendJsonAsync(object message)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(message);
        // Encrypt and send under one lock, so frames leave in nonce order.
        await sendLock.WaitAsync();
        try { await hub.SendAsync(this, ChannelKind.Transport, Handshake.Send!.EncryptWithAd([], plain)); }
        finally { sendLock.Release(); }
    }

    internal void Complete(JsonElement response)
    {
        if (pending.TryRemove(response.GetProperty("re").GetInt64(), out var tcs)) { tcs.TrySetResult(response); }
    }

    internal void Fail(Exception e)
    {
        Ready.TrySetException(e);
        foreach (var p in pending.Values) { p.TrySetException(e); }
    }
}

public sealed class PeerMismatchException() : CryptographicException("the peer's static key does not hash to the device id it was reached at");

public sealed class ChannelHub
{
    readonly RelayClient relay;
    readonly P256KeyPair key;
    readonly byte[] intro;
    readonly ConcurrentDictionary<(string Peer, ulong Id), SecureChannel> channels = new();

    // Serves a peer's request: (channel, op, data) -> result. Null means no service.
    public Func<SecureChannel, string, JsonElement, Task<object?>>? Handler { get; set; }
    public event Action<SecureChannel>? Accepted;
    public event Action<string, Exception>? Rejected;

    // Peers are not trusted to clean up after themselves (a caller that exits never says
    // so, and a hostile one can open handshakes it never finishes), so the hub bounds what
    // it keeps: unfinished handshakes expire, channels go when their peer leaves presence,
    // and past the cap new handshakes are ignored.
    public int MaxChannels { get; set; } = 256;
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int ChannelCount => channels.Count;

    public ChannelHub(RelayClient relay, P256KeyPair key, string name, string kind)
    {
        this.relay = relay;
        this.key = key;
        intro = JsonSerializer.SerializeToUtf8Bytes(new { name, kind });
        relay.OnData = OnDataAsync;
        relay.PresenceChanged += present =>
        {
            var online = present.Select(p => p.Id).ToHashSet();
            // A presence list can be a moment older than a peer's first frame; spare new channels.
            var settled = Environment.TickCount64 - 5000;
            foreach (var (k, ch) in channels)
            {
                if (!online.Contains(k.Peer) && ch.CreatedAt < settled && channels.TryRemove(k, out _)) { ch.Fail(new IOException("peer left")); }
            }
        };
    }

    void PruneHandshakes()
    {
        var cutoff = Environment.TickCount64 - (long)HandshakeTimeout.TotalMilliseconds;
        foreach (var (k, ch) in channels)
        {
            if (!ch.Handshake.IsComplete && ch.CreatedAt < cutoff && channels.TryRemove(k, out _)) { ch.Fail(new TimeoutException("handshake not completed")); }
        }
    }

    // Binds the handshake to both routing ids and the channel, so a relay cannot splice
    // one channel's messages into another or present a channel to a different device.
    static byte[] Prologue(ulong channel, string initiator, string responder)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes($"agent-bridge-s4|{initiator}|{responder}|"));
        var c = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(c, channel);
        b.AddRange(c);
        return [.. b];
    }

    public async Task<SecureChannel> OpenAsync(string peerId, TimeSpan? timeout = null)
    {
        var id = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        var ch = new SecureChannel(this, peerId, id, true, new XXHandshake(true, key, Prologue(id, relay.DeviceId, peerId)));
        channels[(peerId, id)] = ch;
        try
        {
            await SendAsync(ch, ChannelKind.Msg1, ch.Handshake.WriteMessage([]));
            await ch.Ready.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));
            return ch;
        }
        catch
        {
            channels.TryRemove((peerId, id), out _);
            throw;
        }
    }

    internal Task SendAsync(SecureChannel ch, ChannelKind kind, byte[] body)
    {
        var payload = new byte[Wire.ChannelHeader + body.Length];
        payload[0] = (byte)kind;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), ch.Id);
        body.CopyTo(payload, Wire.ChannelHeader);
        return relay.SendAsync(ch.PeerId, payload);
    }

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
                    ch = new SecureChannel(this, from, id, false, new XXHandshake(false, key, Prologue(id, from, relay.DeviceId)));
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
                    if (message.TryGetProperty("re", out _)) { ch.Complete(message); }
                    else if (message.TryGetProperty("op", out var op)) { _ = ServeAsync(ch, message.GetProperty("id").GetInt64(), op.GetString()!, message.TryGetProperty("data", out var d) ? d : default); }
                    break;
                case ChannelKind.Close when ch is not null:
                    channels.TryRemove((from, id), out _);
                    ch.Fail(new IOException("peer closed the channel"));
                    break;
            }
        }
        catch (Exception e) when (e is CryptographicException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            // A frame that fails authentication ends the channel; Noise state cannot resync.
            channels.TryRemove((from, id), out _);
            ch?.Fail(e);
            Rejected?.Invoke(from, e);
        }
    }

    void ReadIntro(SecureChannel ch, byte[] payload)
    {
        if (Wire.DeviceId(ch.PeerStatic!) != ch.PeerId) { throw new PeerMismatchException(); }
        var j = JsonDocument.Parse(payload).RootElement;
        ch.PeerName = j.GetProperty("name").GetString() ?? "";
        ch.PeerKind = j.GetProperty("kind").GetString() ?? "";
    }

    async Task ServeAsync(SecureChannel ch, long id, string op, JsonElement data)
    {
        object reply;
        try
        {
            var result = Handler is null ? null : await Handler(ch, op, data);
            reply = result is null ? new { re = id, ok = false, error = $"unknown op {op}" } : new { re = id, ok = true, result };
        }
        catch (Exception e) { reply = new { re = id, ok = false, error = e.Message }; }
        try { await ch.SendJsonAsync(reply); } catch (Exception e) when (e is IOException or System.Net.WebSockets.WebSocketException) { }
    }
}
