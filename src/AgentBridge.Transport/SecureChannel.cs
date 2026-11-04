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
    readonly ConcurrentDictionary<long, TaskCompletionSource<(JsonElement, long)>> pending = new();
    // Progress listeners of pending requests that have one, by request id.
    readonly ConcurrentDictionary<long, Action<JsonElement>> listeners = new();
    readonly Channel<ChannelEvent> events;
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? closeReason;
    long nextRequest;
    long lastEventSeq;
    // The seq of the last event delivered, set by the hub's one receive loop that also completes
    // responses: each response is stamped with it.
    long lastReceivedSeq = -1;

    internal INoiseHandshake Handshake { get; }
    // An IK opener's first request, sent in its first message (ChannelHub.OpenAsync's early):
    // the op and its data as serialized, then the responder's answer from its reply, if it gave
    // one. The channel's first request, if it is exactly that, is answered from it.
    sealed record EarlyAnswer(string Op, byte[] Data, JsonElement? Response);
    EarlyAnswer? early;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal long CreatedAt { get; } = Environment.TickCount64;

    public string PeerId { get; }
    public ulong Id { get; }
    public bool Initiator { get; }
    public string PeerName { get; internal set; } = "";
    public string PeerKind { get; internal set; } = "";
    // The peer's introduction said it inflates (MessageDeflate): larger messages go to it deflated.
    public bool PeerInflates { get; internal set; }
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

    internal SecureChannel(ChannelHub hub, string peerId, ulong id, bool initiator, INoiseHandshake handshake, int eventCapacity)
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
    // progress hears each {re, progress} frame the host sends for this request before its response
    // (only when the request asked for them, as session.reply's progress: true does); it is raised
    // on the channel's receive path, so it must be quick, and what it throws is ignored.
    public async Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null) =>
        (await RequestOrderedAsync(op, data, timeout, ct, progress)).Response;

    // The same, with the seq of the last event received before the response (-1 for none): an
    // event with a higher seq came after it (IClientChannel.RequestOrderedAsync).
    public async Task<(JsonElement Response, long EventsBefore)> RequestOrderedAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null)
    {
        ThrowIfClosed();
        // The first request only: answered already if it is the one the first message carried.
        // Any other first request drops that answer, which would be older than what it asks.
        if (Interlocked.Exchange(ref early, null) is { Response: { } answer } asked && asked.Op == op && asked.Data.AsSpan().SequenceEqual(SerializeData(data)))
        {
            // As the response would have been: with its re.
            var response = new System.Text.Json.Nodes.JsonObject { ["re"] = Interlocked.Increment(ref nextRequest) };
            foreach (var p in answer.EnumerateObject()) { response[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText()); }
            return (JsonSerializer.SerializeToElement(response), -1);
        }
        var id = Interlocked.Increment(ref nextRequest);
        var tcs = new TaskCompletionSource<(JsonElement, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        if (progress is not null) { listeners[id] = progress; }
        try
        {
            // Closed between the check above and registering: Fail has already swept pending.
            ThrowIfClosed();
            await SendJsonAsync(new { id, op, data }, ct);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), ct);
        }
        finally
        {
            pending.TryRemove(id, out _);
            listeners.TryRemove(id, out _);
        }
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

    // A request's data alone, as it is compared with the first message's.
    byte[] SerializeData(object? data) => JsonSerializer.SerializeToUtf8Bytes(data, hub.JsonOptions);

    internal void ExpectEarly(string op, object? data) => early = new EarlyAnswer(op, SerializeData(data), null);

    internal byte[]? EarlyData => early?.Data;

    internal void DropEarly() => early = null;

    internal void AnsweredEarly(JsonElement response)
    {
        if (early is { } asked) { early = asked with { Response = response.Clone() }; }
    }

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
        // Deflated before it is encrypted: ciphertext does not compress. Never larger than plain,
        // so what fit above still fits.
        var body = PeerInflates ? MessageDeflate.Pack(plain) : plain;
        var encrypted = false;
        try
        {
            // Encrypted only when the relay is ready to write it, so cancelling a send that
            // is still waiting (behind other channels, or the relay's pacing) spends nothing
            // and leaves the channel usable.
            await hub.SendAsync(this, ChannelKind.Transport, () =>
            {
                hub.RequirePeer(this);
                ThrowIfClosed();
                encrypted = true;
                return send.EncryptWithAd([], body);
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

    // An IK responder's reply, which makes the transport keys: written and sent under the send
    // lock, as the relay takes it, so nothing this side sends (an event, hello.changed) can be
    // encrypted before it or leave ahead of it, which the opener could not read (its first nonce
    // missed, nothing after would decrypt). Checked as a transport message is: still allowed, not
    // closed. Open from the moment it is written, so the opener's first request, which only it
    // lets the opener send, finds the channel open.
    internal async Task SendReplyAsync(ChannelKind kind, Func<byte[]> write)
    {
        await sendLock.WaitAsync();
        try
        {
            await hub.SendAsync(this, kind, () =>
            {
                hub.RequirePeer(this);
                ThrowIfClosed();
                var message = write();
                Ready.TrySetResult();
                return message;
            }, default);
        }
        finally { sendLock.Release(); }
    }

    void ThrowIfClosed()
    {
        if (closed.Task.IsCompleted) { throw new IOException("the channel is closed", closeReason); }
    }

    internal void Complete(JsonElement response)
    {
        var re = response.GetProperty("re").GetInt64();
        // Progress is never the response, whatever else the frame holds: the call goes on waiting.
        if (response.TryGetProperty("progress", out var progress))
        {
            Report(listeners, re, progress);
            return;
        }
        // The listener goes before the call is answered: a progress frame after the response is for a
        // request that has finished, and reaches no one (#611, found in review of #616).
        listeners.TryRemove(re, out _);
        if (pending.TryRemove(re, out var tcs)) { tcs.TrySetResult((response, lastReceivedSeq)); }
    }

    // A listener's fault is the caller's, never the channel's: thrown here it would end the channel.
    internal static void Report(ConcurrentDictionary<long, Action<JsonElement>> listeners, long re, JsonElement progress)
    {
        if (!listeners.TryGetValue(re, out var listener)) { return; }
        try { listener(progress.Clone()); }
        catch (Exception) { }
    }

    internal void Deliver(JsonElement message)
    {
        var name = message.GetProperty("ev").GetString() ?? throw new JsonException("an event without a name");
        var seq = message.GetProperty("seq").GetInt64();
        var data = message.TryGetProperty("data", out var d) ? d.Clone() : default;
        lastReceivedSeq = seq;
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
