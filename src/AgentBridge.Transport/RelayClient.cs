using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentBridge.Transport;

public sealed record PresenceEntry(string Id, string Name, string Kind, DateTimeOffset Since);

public sealed record PushResult(int Status, string? Error);

public sealed class RelayRefusedException(WebSocketCloseStatus? status, string? reason)
    : Exception($"relay closed the connection: {(int?)status} {reason}")
{
    public int? Code => (int?)status;
}

// One device's connection to the relay: sign in to it, prove the device key, then send
// and receive opaque frames addressed by device id.
public sealed class RelayClient : IAsyncDisposable
{
    readonly ClientWebSocket ws;
    readonly SemaphoreSlim sendLock = new(1, 1);
    Task loop = Task.CompletedTask;
    Task dispatch = Task.CompletedTask;

    // Data can arrive before anyone is listening: peers learn of a device from presence,
    // which the relay sends before "ready", and the caller attaches its handler only after
    // ConnectAsync returns. A dropped Msg1 is never resent, so the channel attempt would
    // time out. Every data frame goes through this queue, in order, to one dispatcher that
    // starts once a handler is attached. Bounded so a client that never attaches one cannot
    // be made to hold unlimited data.
    readonly Channel<(string From, byte[] Payload)> inbound = Channel.CreateBounded<(string, byte[])>(
        new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });
    readonly TaskCompletionSource<Func<string, byte[], Task>> handler = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string DeviceId { get; }
    // The relay this connection is to; a blob URL is only followed to it or to Azure Storage.
    public Uri Relay { get; }
    public string AccountId { get; private set; } = "";
    // The largest blob (stored bytes) the relay's blob store takes, or 0 when it has none.
    public long BlobMaxBytes { get; private set; }
    public string Login { get; private set; } = "";
    public IReadOnlyList<PresenceEntry> Presence { get; private set; } = [];

    // Called in order, one frame at a time, so frames from one sender are handled in the
    // order they were sent, which Noise transport messages require. Set once.
    public Func<string, byte[], Task>? OnData
    {
        get => handler.Task.IsCompletedSuccessfully ? handler.Task.Result : null;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!handler.TrySetResult(value)) { throw new InvalidOperationException("the data handler is already set"); }
        }
    }
    public event Action<IReadOnlyList<PresenceEntry>>? PresenceChanged;
    public event Action<string>? Undeliverable;

    readonly ConcurrentDictionary<long, TaskCompletionSource<PushResult>> pushes = new();
    long nextPush;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> blobRequests = new();

    // Frames held for a handler that is not attached yet.
    internal int QueuedFrames => inbound.Reader.Count;

    // Asks the relay to deliver an already-encrypted Web Push body (WebPushCrypto) to a
    // subscription's endpoint. Status is the push service's HTTP status (201 accepted;
    // 404/410 mean the subscription is gone), or 0 with the relay's reason for refusing.
    public async Task<PushResult> PushAsync(string endpoint, byte[] body, int ttl = 3600, string urgency = "high", string? topic = null)
    {
        var id = Interlocked.Increment(ref nextPush);
        var tcs = new TaskCompletionSource<PushResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pushes[id] = tcs;
        try
        {
            await SendTextAsync(new { t = "push", id, endpoint, body = Convert.ToBase64String(body), ttl, urgency, topic }, default);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { pushes.TryRemove(id, out _); }
    }

    // The blob store (docs/protocol/blobs.md): a write-once URL for one blob for the device
    // 	o, sized as BlobFormat.StoredSize says. The ticket names the blob from then on.
    public async Task<BlobSlot> BlobPutAsync(string to, long storedSize, CancellationToken ct = default)
    {
        var r = await BlobRequestAsync(id => new { t = "blob-put", id, to, size = storedSize }, ct);
        return new BlobSlot(r.GetProperty("blob").GetString()!, r.GetProperty("url").GetString()!, r.GetProperty("expires").GetDateTimeOffset());
    }

    // A short-lived read URL for a blob sent to this device, and its stored size.
    public async Task<BlobLocation> BlobGetAsync(string blob, CancellationToken ct = default)
    {
        var r = await BlobRequestAsync(id => new { t = "blob-get", id, blob }, ct);
        return new BlobLocation(r.GetProperty("url").GetString()!, r.GetProperty("size").GetInt64());
    }

    // Deletes a blob this device sent or received; true if it was there.
    public async Task<bool> BlobDeleteAsync(string blob, CancellationToken ct = default)
    {
        var r = await BlobRequestAsync(id => new { t = "blob-delete", id, blob }, ct);
        return r.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True;
    }

    async Task<JsonElement> BlobRequestAsync(Func<long, object> message, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextPush);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        blobRequests[id] = tcs;
        try
        {
            await SendTextAsync(message(id), ct);
            var r = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (r.TryGetProperty("error", out var e) && e.GetString() is { } why) { throw new BlobRefusedException(why); }
            return r;
        }
        finally { blobRequests.TryRemove(id, out _); }
    }

    // Ends, never faults, when the connection does; the caller then reconnects.
    public Task Completion => loop;
    public WebSocketCloseStatus? CloseStatus => ws.CloseStatus;
    public string? CloseReason => ws.CloseStatusDescription;

    RelayClient(ClientWebSocket ws, string deviceId, Uri relay) { this.ws = ws; DeviceId = deviceId; Relay = relay; }

    public static Uri SocketUri(Uri relay) => new UriBuilder(relay)
    {
        Scheme = relay.Scheme == "https" ? "wss" : "ws",
        Path = "/v1/ws",
    }.Uri;

    // kind is one of the relay's device kinds: host, app, pwa, phone or test.
    public static async Task<RelayClient> ConnectAsync(Uri relay, string token, P256KeyPair key, string name, string kind, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        // Keeps NATs, proxies and the hosting ingress from timing out an idle host. The timeout
        // makes it a ping that must be answered: a relay replica that vanished without a close
        // frame otherwise leaves the socket open, and the host unreachable, indefinitely.
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        RelayClient c;
        try
        {
            await ws.ConnectAsync(SocketUri(relay), ct);
            c = new RelayClient(ws, Wire.DeviceId(key.Public), relay);
            await c.SendTextAsync(new { t = "hello", token, pub = Wire.Hex(key.Public), name, kind }, ct);
            var challenge = await c.ReceiveControlAsync("challenge", ct);
            var eph = Wire.Unhex(challenge.GetProperty("eph").GetString()!);
            var nonce = Wire.Unhex(challenge.GetProperty("nonce").GetString()!);
            var account = challenge.GetProperty("account").GetString()!;
            var mac = Wire.ProofMac(key.Dh(eph), nonce, account);
            await c.SendTextAsync(new { t = "proof", mac = Wire.Hex(mac) }, ct);
            var ready = await c.ReceiveControlAsync("ready", ct);
            if (ready.GetProperty("device").GetString() != c.DeviceId) { throw new CryptographicException("relay assigned a different device id"); }
            c.AccountId = account;
            c.Login = ready.GetProperty("login").GetString() ?? "";
            // Absent from a relay without a blob store, and from one that predates it.
            if (ready.TryGetProperty("blobs", out var blobs) && blobs.ValueKind == JsonValueKind.Object && blobs.TryGetProperty("max", out var max) && max.TryGetInt64(out var m)) { c.BlobMaxBytes = Math.Max(0, m); }
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        c.loop = Task.Run(c.RunAsync);
        c.dispatch = Task.Run(c.DispatchAsync);
        return c;
    }

    void Enqueue(byte[] data)
    {
        if (data.Length > 1 + Wire.IdBytes && data[0] == Wire.DataFrame)
        {
            inbound.Writer.TryWrite((Wire.Hex(data.AsSpan(1, Wire.IdBytes)), data[(1 + Wire.IdBytes)..]));
        }
    }

    async Task DispatchAsync()
    {
        var h = await handler.Task;
        await foreach (var (from, payload) in inbound.Reader.ReadAllAsync())
        {
            // One frame the handler cannot cope with must not stop delivery of the rest.
            try { await h(from, payload); }
            catch (Exception) { }
        }
    }

    public Task SendAsync(string peerId, byte[] payload, CancellationToken ct = default) => SendAsync(peerId, () => payload, ct);

    // payload is called only once this frame's turn has come: after the lock and the
    // pacing, both of which ct can cancel. A channel encrypts there, so a send cancelled
    // while it waits spends no Noise nonce. Once the frame is made it is written whole,
    // never cut off part way, which would corrupt the socket for every other channel.
    internal async Task SendAsync(string peerId, Func<byte[]> payload, CancellationToken ct)
    {
        if (!Wire.IsDeviceId(peerId)) { throw new ArgumentException("not a device id", nameof(peerId)); }
        await sendLock.WaitAsync(ct);
        try
        {
            await PaceAsync(ct);
            var frame = Wire.DataFrameBytes(peerId, payload());
            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        finally { sendLock.Release(); }
    }

    // The relay closes a connection that sends more than its limit in a second (4008, 200
    // by default). The spike never came near it; a host sending a subscription's snapshot,
    // one event per session, does. So sends wait rather than burst: at most this many in
    // any second, under the relay's limit so jitter that bunches frames in transit still
    // fits. Zero turns pacing off.
    public int MaxFramesPerSecond { get; set; } = 150;
    readonly Queue<long> recentSends = new();

    // Called under sendLock, so waiting here holds back every later send, in order.
    async Task PaceAsync(CancellationToken ct)
    {
        var limit = MaxFramesPerSecond;
        if (limit <= 0) { return; }
        while (true)
        {
            var now = Environment.TickCount64;
            while (recentSends.Count > 0 && now - recentSends.Peek() >= 1000) { recentSends.Dequeue(); }
            if (recentSends.Count < limit) { recentSends.Enqueue(now); return; }
            await Task.Delay(TimeSpan.FromMilliseconds(1000 - (now - recentSends.Peek()) + 1), ct);
        }
    }

    // Test use: send raw bytes the relay must refuse.
    internal async Task SendRawAsync(byte[] data, WebSocketMessageType type)
    {
        await sendLock.WaitAsync();
        try { await ws.SendAsync(data, type, true, default); }
        finally { sendLock.Release(); }
    }

    async Task SendTextAsync(object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await sendLock.WaitAsync(ct);
        try
        {
            await PaceAsync(ct);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { sendLock.Release(); }
    }

    async Task<JsonElement> ReceiveControlAsync(string expected, CancellationToken ct)
    {
        while (true)
        {
            var frame = await Wire.ReceiveAsync(ws, Wire.MaxFrame, ct)
                ?? throw new RelayRefusedException(ws.CloseStatus, ws.CloseStatusDescription);
            if (frame.Type != WebSocketMessageType.Text) { Enqueue(frame.Data); continue; }
            var j = JsonDocument.Parse(frame.Data).RootElement;
            var t = j.GetProperty("t").GetString();
            if (t == expected) { return j; }
            if (t == "presence") { ApplyPresence(j); }
        }
    }

    async Task RunAsync()
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var frame = await Wire.ReceiveAsync(ws, Wire.MaxFrame, default);
                if (frame is null) { break; }
                var (type, data) = frame.Value;
                if (type == WebSocketMessageType.Text)
                {
                    var j = JsonDocument.Parse(data).RootElement;
                    switch (j.GetProperty("t").GetString())
                    {
                        case "presence": ApplyPresence(j); break;
                        case "undeliverable": Undeliverable?.Invoke(j.GetProperty("to").GetString()!); break;
                        case "push-result":
                            if (pushes.TryRemove(j.GetProperty("id").GetInt64(), out var p))
                            {
                                p.TrySetResult(new PushResult(j.GetProperty("status").GetInt32(), j.TryGetProperty("error", out var err) ? err.GetString() : null));
                            }
                            break;
                        case "blob-result":
                            if (blobRequests.TryRemove(j.GetProperty("id").GetInt64(), out var b)) { b.TrySetResult(j.Clone()); }
                            break;
                    }
                }
                else { Enqueue(data); }
            }
        }
        catch (WebSocketException) { }
        // An oversized frame is a relay protocol violation: end this connection cleanly so
        // the caller reconnects, rather than faulting Completion and taking the host down.
        catch (FrameTooLargeException)
        {
            try { await ws.CloseOutputAsync((WebSocketCloseStatus)CloseCodes.TooLarge, "frame-too-large", default); } catch (WebSocketException) { }
        }
        // A malformed control message from the relay ends the connection; the caller reconnects.
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        inbound.Writer.TryComplete();
        // Lets the dispatcher finish even if nobody ever attached a handler.
        handler.TrySetResult((_, _) => Task.CompletedTask);
        foreach (var p in pushes.Values) { p.TrySetException(new IOException("relay connection ended")); }
        foreach (var b in blobRequests.Values) { b.TrySetException(new IOException("relay connection ended")); }
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default); } catch (WebSocketException) { }
        }
    }

    void ApplyPresence(JsonElement j)
    {
        Presence = [.. j.GetProperty("devices").EnumerateArray().Select(d => new PresenceEntry(
            d.GetProperty("id").GetString()!, d.GetProperty("name").GetString()!,
            d.GetProperty("kind").GetString()!, d.GetProperty("since").GetDateTimeOffset()))];
        PresenceChanged?.Invoke(Presence);
    }

    public async Task<PresenceEntry> WaitForPeerAsync(Func<PresenceEntry, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hit = Presence.FirstOrDefault(p => p.Id != DeviceId && match(p));
            if (hit != null) { return hit; }
            await Task.Delay(50);
        }
        throw new TimeoutException("peer did not appear");
    }

    public async ValueTask DisposeAsync()
    {
        if (ws.State == WebSocketState.Open)
        {
            try
            {
                // CloseOutputAsync, not CloseAsync: the receive loop owns the receive side,
                // and sees the relay's close frame itself.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException) { }
        }
        try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        try { await dispatch.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        ws.Dispose();
    }
}
