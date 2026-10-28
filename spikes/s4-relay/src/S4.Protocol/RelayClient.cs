using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using S2.Noise;

namespace S4;

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
    readonly Channel<(string From, byte[] Payload)> inbound = System.Threading.Channels.Channel.CreateBounded<(string, byte[])>(
        new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });
    readonly TaskCompletionSource<Func<string, byte[], Task>> handler = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string DeviceId { get; }
    public string AccountId { get; private set; } = "";
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

    readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<PushResult>> pushes = new();
    long nextPush;

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

    public Task Completion => loop;
    public WebSocketCloseStatus? CloseStatus => ws.CloseStatus;
    public string? CloseReason => ws.CloseStatusDescription;

    RelayClient(ClientWebSocket ws, string deviceId) { this.ws = ws; DeviceId = deviceId; }

    public static Uri SocketUri(Uri relay) => new UriBuilder(relay)
    {
        Scheme = relay.Scheme == "https" ? "wss" : "ws",
        Path = "/v1/ws",
    }.Uri;

    public static async Task<RelayClient> ConnectAsync(Uri relay, string token, P256KeyPair key, string name, string kind, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        // Keeps NATs, proxies and the hosting ingress from timing out an idle host. The timeout
        // makes it a ping that must be answered: a relay replica that vanished without a close
        // frame otherwise leaves the socket open, and the host unreachable, indefinitely.
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        await ws.ConnectAsync(SocketUri(relay), ct);
        var c = new RelayClient(ws, Wire.DeviceId(key.Public));
        try
        {
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

    public async Task SendAsync(string peerId, byte[] payload, CancellationToken ct = default)
    {
        var frame = Wire.DataFrameBytes(peerId, payload);
        await sendLock.WaitAsync(ct);
        try { await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct); }
        finally { sendLock.Release(); }
    }

    // Test use: send raw bytes the relay must refuse.
    public async Task SendRawAsync(byte[] data, WebSocketMessageType type)
    {
        await sendLock.WaitAsync();
        try { await ws.SendAsync(data, type, true, default); }
        finally { sendLock.Release(); }
    }

    async Task SendTextAsync(object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
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
