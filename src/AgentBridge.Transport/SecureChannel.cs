using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentBridge.Transport;

// An event a peer pushed on a channel: {"ev": name, "seq": n, "data": ...}. Seq starts at 1
// on each channel and goes up by one per event, so a gap means events were lost.
public sealed record ChannelEvent(string Name, long Seq, JsonElement Data);

public sealed class MessageTooLargeException(int size)
    : Exception($"a {size}-byte message does not fit one Noise transport message (at most {SecureChannel.MaxMessageBytes} bytes)")
{
    public int Size => size;
}

public sealed class PeerMismatchException() : CryptographicException("the peer's static key does not hash to the device id it was reached at");

// An end-to-end encrypted conversation with one peer device, carried as opaque frames
// through the relay. Requests, responses and events are JSON inside Noise transport
// messages; the relay sees none of it.
public sealed class SecureChannel : IProtocolChannel
{
    // One message is one Noise transport message: its ciphertext, plaintext plus the tag,
    // must fit NoiseConst.MaxMessage. A larger one would be refused by the relay (4013),
    // taking the sender's whole connection down with it.
    public const int MaxMessageBytes = NoiseConst.MaxMessage - NoiseConst.TagLen;

    readonly ChannelHub hub;
    // Every outgoing message (request, response, event) is encrypted and sent under this
    // one lock, so frames leave in nonce order and events in seq order.
    readonly SemaphoreSlim sendLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    readonly Channel<ChannelEvent> events;
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? closeReason;
    long nextRequest;
    long lastEventSeq;

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
    // The relay connection this channel runs over: its account, and the presence it reports.
    public RelayClient Relay => hub.Relay;

    // Events the peer sent, in the order it sent them. Held for a reader that has not caught
    // up, up to the hub's EventBufferCapacity; past that the oldest go, never the latest,
    // and the reader sees the gap in Seq. Ends when the channel does.
    public ChannelReader<ChannelEvent> Events => events.Reader;

    // Completes (never faults) when the channel ends: the peer closed it or left, a frame
    // failed authentication, a send failed, or the relay connection ended. A host stops
    // sending a subscription's events then; a client opens a new channel and subscribes again.
    public Task Closed => closed.Task;
    public Exception? CloseReason => closeReason;
    public bool IsOpen => Ready.Task.IsCompletedSuccessfully && !closed.Task.IsCompleted;

    internal SecureChannel(ChannelHub hub, string peerId, ulong id, bool initiator, XXHandshake handshake, int eventCapacity)
    {
        this.hub = hub; PeerId = peerId; Id = id; Initiator = initiator; Handshake = handshake;
        events = Channel.CreateBounded<ChannelEvent>(new BoundedChannelOptions(eventCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    // Sends {id, op, data} and waits for the response with the same id: the whole response,
    // {re, ok, result} or {re, ok: false, error}. Any number may be outstanding at once.
    public async Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfClosed();
        var id = Interlocked.Increment(ref nextRequest);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            // Closed between the check above and registering: Fail has already swept pending.
            ThrowIfClosed();
            await SendJsonAsync(new { id, op, data }, ct);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), ct);
        }
        finally { pending.TryRemove(id, out _); }
    }

    // Sends {ev, seq, data} and returns its seq. Safe to call from any number of threads:
    // seq is taken under the send lock, so events arrive in seq order, interleaved with
    // responses exactly as they were sent.
    public async Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await sendLock.WaitAsync(ct);
        try
        {
            var seq = lastEventSeq + 1;
            await SendLockedAsync(Serialize(new { ev = name, seq, data }), ct);
            lastEventSeq = seq;
            return seq;
        }
        finally { sendLock.Release(); }
    }

    internal byte[] Serialize(object message) => JsonSerializer.SerializeToUtf8Bytes(message, hub.JsonOptions);

    internal Task SendJsonAsync(object message, CancellationToken ct = default) => SendAsync(Serialize(message), ct);

    internal async Task SendAsync(byte[] plain, CancellationToken ct = default)
    {
        await sendLock.WaitAsync(ct);
        try { await SendLockedAsync(plain, ct); }
        finally { sendLock.Release(); }
    }

    async Task SendLockedAsync(byte[] plain, CancellationToken ct)
    {
        // Checked before encrypting, so a refused message does not spend a nonce.
        if (plain.Length > MaxMessageBytes) { throw new MessageTooLargeException(plain.Length); }
        ThrowIfClosed();
        var send = Handshake.Send ?? throw new InvalidOperationException("the channel is not open yet");
        var encrypted = false;
        try
        {
            // Encrypted only when the relay is ready to write it, so cancelling a send that
            // is still waiting (behind other channels, or the relay's pacing) spends nothing
            // and leaves the channel usable.
            await hub.SendAsync(this, ChannelKind.Transport, () =>
            {
                encrypted = true;
                return send.EncryptWithAd([], plain);
            }, ct);
        }
        catch (Exception e) when (encrypted)
        {
            // The nonce is spent whether or not the frame left, and Noise cannot skip one:
            // the peer could decrypt nothing more from this channel, so it is over.
            var gone = new IOException("the channel could not send", e);
            hub.Forget(this, gone);
            throw gone;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never reached the relay (its connection is gone): nothing more will cross.
            var gone = new IOException("the channel could not send", e);
            hub.Forget(this, gone);
            throw gone;
        }
    }

    void ThrowIfClosed()
    {
        if (closed.Task.IsCompleted) { throw new IOException("the channel is closed", closeReason); }
    }

    internal void Complete(JsonElement response)
    {
        if (pending.TryRemove(response.GetProperty("re").GetInt64(), out var tcs)) { tcs.TrySetResult(response); }
    }

    internal void Deliver(JsonElement message)
    {
        var name = message.GetProperty("ev").GetString() ?? throw new JsonException("an event without a name");
        var seq = message.GetProperty("seq").GetInt64();
        var data = message.TryGetProperty("data", out var d) ? d.Clone() : default;
        events.Writer.TryWrite(new ChannelEvent(name, seq, data));
    }

    internal void Fail(Exception e)
    {
        Interlocked.CompareExchange(ref closeReason, e, null);
        Ready.TrySetException(e);
        // Nobody may be waiting on Ready (an accepted channel); that is not an error.
        _ = Ready.Task.Exception;
        closed.TrySetResult();
        events.Writer.TryComplete();
        foreach (var p in pending.Values) { p.TrySetException(e); }
    }
}
