using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Local;

// One connection on the local pipe, from either end: requests, responses and events framed
// as in docs/protocol/local-pipe.md, with the semantics of a SecureChannel (ordered sends
// under one lock, seq per channel, any number of requests outstanding, a bounded event
// buffer that drops the oldest). The host's end serves requests through a handler, exactly
// as ChannelHub does for the relay; the app's end sends them.
public sealed class LocalChannel : IProtocolChannel, IAsyncDisposable
{
    readonly Stream stream;
    readonly SemaphoreSlim sendLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    readonly Channel<ChannelEvent> events;
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource stop = new();
    readonly Func<IProtocolChannel, string, JsonElement, Task<object?>>? handler;
    readonly JsonSerializerOptions json;
    Exception? closeReason;
    long nextRequest;
    long lastEventSeq;
    Task reader = Task.CompletedTask;

    public string PeerId { get; }
    public string PeerName { get; }
    public string PeerKind { get; }
    // The process on the other end and its ancestors, as the server found them when it
    // accepted the connection. Null on a client's own end.
    public PipePeer? Peer { get; internal set; }
    public bool IsOpen => !closed.Task.IsCompleted;
    public Task Closed => closed.Task;
    public Exception? CloseReason => closeReason;
    public ChannelReader<ChannelEvent> Events => events.Reader;

    internal LocalChannel(Stream stream, string peerId, string peerName, string peerKind,
        Func<IProtocolChannel, string, JsonElement, Task<object?>>? handler, JsonSerializerOptions? json = null, int eventCapacity = 1024)
    {
        this.stream = stream;
        this.handler = handler;
        this.json = json ?? ProtocolJson.Options;
        PeerId = peerId;
        PeerName = peerName;
        PeerKind = peerKind;
        events = Channel.CreateBounded<ChannelEvent>(new BoundedChannelOptions(Math.Max(1, eventCapacity))
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    internal void Start() => reader = Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfClosed();
        var id = Interlocked.Increment(ref nextRequest);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            ThrowIfClosed();
            await SendAsync(Serialize(new { id, op, data }), ct);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), ct);
        }
        finally { pending.TryRemove(id, out _); }
    }

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

    byte[] Serialize(object message) => JsonSerializer.SerializeToUtf8Bytes(message, json);

    // Tests: as if events had been dropped on the way, so the next one arrives after a gap.
    // Under the send lock: an event being sent meanwhile stored its own seq when done and wrote
    // over a skip made in between, so the gap a test made sometimes never happened.
    internal void SkipEventSeq(long count)
    {
        sendLock.Wait();
        try { lastEventSeq += count; }
        finally { sendLock.Release(); }
    }

    async Task SendAsync(byte[] plain, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try { await SendLockedAsync(plain, ct); }
        finally { sendLock.Release(); }
    }

    async Task SendLockedAsync(byte[] plain, CancellationToken ct)
    {
        // Checked before writing, so a refused message leaves the connection usable, as a
        // SecureChannel's does.
        if (plain.Length > Frames.MaxMessageBytes) { throw new MessageTooLargeException(plain.Length); }
        ThrowIfClosed();
        try { await Frames.WriteAsync(stream, plain, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var gone = new IOException("the pipe could not send", e);
            Fail(gone);
            throw gone;
        }
    }

    void ThrowIfClosed()
    {
        if (closed.Task.IsCompleted) { throw new IOException("the pipe is closed", closeReason); }
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var frame = await Frames.ReadAsync(stream, stop.Token);
                if (frame is null) { Fail(new IOException("the peer closed the pipe")); return; }
                var message = JsonDocument.Parse(frame).RootElement;
                if (message.ValueKind != JsonValueKind.Object) { throw new JsonException("a message that is not an object"); }
                // A response has re, an event ev, a request op; anything else is from a newer
                // peer and is ignored, as on the relay.
                if (message.TryGetProperty("re", out var re))
                {
                    if (pending.TryRemove(re.GetInt64(), out var tcs)) { tcs.TrySetResult(message.Clone()); }
                }
                else if (message.TryGetProperty("ev", out var ev))
                {
                    var name = ev.GetString() ?? throw new JsonException("an event without a name");
                    var seq = message.GetProperty("seq").GetInt64();
                    var data = message.TryGetProperty("data", out var d) ? d.Clone() : default;
                    events.Writer.TryWrite(new ChannelEvent(name, seq, data));
                }
                else if (message.TryGetProperty("op", out var op) && handler is not null)
                {
                    _ = ServeAsync(message.GetProperty("id").GetInt64(), op.GetString() ?? "", message.TryGetProperty("data", out var d) ? d.Clone() : default);
                }
            }
        }
        catch (OperationCanceledException) { Fail(new IOException("the pipe was closed")); }
        // A frame that cannot be read leaves nothing to resynchronize on: the connection is over.
        catch (Exception e) { Fail(e); }
    }

    async Task ServeAsync(long id, string op, JsonElement data)
    {
        byte[] reply;
        Func<IProtocolChannel, Task>? then = null;
        try
        {
            var result = await handler!(this, op, data);
            if (result is ChannelReply r) { (result, then) = (r.Result, r.Then); }
            reply = Serialize(result is null ? new { re = id, ok = false, error = $"unknown op {op}" } : new { re = id, ok = true, result });
            if (reply.Length > Frames.MaxMessageBytes)
            {
                reply = Serialize(new { re = id, ok = false, error = $"response too large ({reply.Length} bytes)" });
                then = null;
            }
        }
        catch (Exception e)
        {
            reply = Serialize(new { re = id, ok = false, error = e.Message });
            then = null;
        }
        try { await SendAsync(reply, default); }
        catch (Exception e) when (e is IOException or MessageTooLargeException or ObjectDisposedException) { return; }
        if (then is null) { return; }
        try { await then(this); }
        catch (Exception) { }
    }

    internal void Fail(Exception e)
    {
        Interlocked.CompareExchange(ref closeReason, e, null);
        if (!closed.TrySetResult()) { return; }
        events.Writer.TryComplete();
        foreach (var p in pending.Values) { p.TrySetException(new IOException("the pipe closed", e)); }
        stop.Cancel();
        try { stream.Dispose(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        Fail(new IOException("the pipe was closed by this end"));
        try { await reader; } catch (Exception) { }
    }
}
