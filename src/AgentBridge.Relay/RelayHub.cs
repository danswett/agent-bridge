using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Relay;

// Routes opaque frames between the devices of one account. It sees routing ids, sizes and
// timing, and nothing a session says: payloads are Noise ciphertext end to end.
public sealed class RelayHub(RelayOptions options, Tokens tokens, PushGateway push, ILogger<RelayHub> log)
{
    static readonly HashSet<string> Kinds = ["host", "app", "pwa", "phone", "test"];
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Connection>> accounts = new();

    public int Online => accounts.Values.Sum(a => a.Count);

    // On shutdown, tell every device to reconnect now. A rolling deploy otherwise leaves old
    // sockets on the draining replica for up to its drain timeout (about 90 s observed on
    // Container Apps) while new devices land on the new one, and neither half sees the other.
    public Task CloseAllAsync() => Task.WhenAll(accounts.Values.SelectMany(a => a.Values).Select(c => c.CloseAsync(CloseCodes.Restarting, "restarting")));

    sealed class Connection(WebSocket ws, Account account, string deviceId, string name, string kind)
    {
        readonly SemaphoreSlim sendLock = new(1, 1);
        public WebSocket Socket => ws;
        public Account Account => account;
        public string DeviceId => deviceId;
        public string Name => name;
        public string Kind => kind;
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
        public long FramesIn, FramesOut, BytesIn, BytesOut;
        public long WindowStart = Environment.TickCount64;
        public int WindowFrames;

        public async Task SendAsync(byte[] data, WebSocketMessageType type)
        {
            await sendLock.WaitAsync();
            try
            {
                if (ws.State != WebSocketState.Open) { return; }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ws.SendAsync(data, type, true, cts.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            finally { sendLock.Release(); }
        }

        public Task SendJsonAsync(object message) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text);

        public async Task CloseAsync(int code, string reason)
        {
            await sendLock.WaitAsync();
            try { if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) { await ws.CloseOutputAsync((WebSocketCloseStatus)code, reason, default); } }
            catch (WebSocketException) { }
            finally { sendLock.Release(); }
        }
    }

    public async Task RunAsync(WebSocket ws, CancellationToken aborted)
    {
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        Connection? conn;
        try { conn = await AdmitAsync(ws, handshakeTimeout.Token); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException or FrameTooLargeException)
        {
            await CloseAsync(ws, CloseCodes.Protocol, "bad-hello");
            return;
        }
        if (conn is null) { return; }

        var devices = accounts.GetOrAdd(conn.Account.Id, _ => new());
        // Check and register under one lock: two admissions racing near the cap must not
        // both see room.
        Connection? replaced;
        bool full;
        lock (devices)
        {
            full = !devices.TryGetValue(conn.DeviceId, out replaced) && devices.Count >= options.MaxDevicesPerAccount;
            if (!full) { devices[conn.DeviceId] = conn; }
        }
        if (full)
        {
            await conn.CloseAsync(CloseCodes.TooManyDevices, "too-many-devices");
            return;
        }
        if (replaced != null) { await replaced.CloseAsync(CloseCodes.Replaced, "replaced"); }

        log.LogInformation("connect {Login} device {Device} {Kind} \"{Name}\"; {Count} online in account", conn.Account.Login, conn.DeviceId[..8], conn.Kind, conn.Name, devices.Count);
        // Announce before "ready", so peers learn of a device before it can send them anything.
        await BroadcastPresenceAsync(conn.Account.Id);
        await conn.SendJsonAsync(new { t = "ready", device = conn.DeviceId, account = conn.Account.Id, login = conn.Account.Login });

        string end = "closed";
        try { end = await ForwardAsync(conn, devices, aborted); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { end = "dropped"; }
        finally
        {
            // Only remove this connection: a replacement may already hold the slot.
            lock (devices) { ((ICollection<KeyValuePair<string, Connection>>)devices).Remove(new(conn.DeviceId, conn)); }
            await conn.CloseAsync((int)WebSocketCloseStatus.NormalClosure, "");
            log.LogInformation("disconnect {Login} device {Device} ({End}) after {Seconds:F0}s; frames in {In} out {Out}, bytes in {BytesIn} out {BytesOut}",
                conn.Account.Login, conn.DeviceId[..8], end, (DateTimeOffset.UtcNow - conn.Since).TotalSeconds, conn.FramesIn, conn.FramesOut, conn.BytesIn, conn.BytesOut);
            await BroadcastPresenceAsync(conn.Account.Id);
        }
    }

    async Task<Connection?> AdmitAsync(WebSocket ws, CancellationToken ct)
    {
        var hello = await ReceiveControlAsync(ws, ct);
        if (hello.GetProperty("t").GetString() != "hello") { await CloseAsync(ws, CloseCodes.Protocol, "expected-hello"); return null; }
        var account = tokens.Validate(hello.GetProperty("token").GetString());
        if (account is null) { await CloseAsync(ws, CloseCodes.AuthFailed, "bad-token"); return null; }
        var pub = Wire.Unhex(hello.GetProperty("pub").GetString()!);
        try { P256KeyPair.AssertOnCurve(pub); }
        catch (CryptographicException) { await CloseAsync(ws, CloseCodes.Protocol, "bad-key"); return null; }
        var kind = hello.GetProperty("kind").GetString() ?? "";
        if (!Kinds.Contains(kind)) { await CloseAsync(ws, CloseCodes.Protocol, "bad-kind"); return null; }
        var name = Clean(hello.GetProperty("name").GetString());

        using var eph = P256KeyPair.Generate();
        var nonce = RandomNumberGenerator.GetBytes(32);
        await SendJsonAsync(ws, new { t = "challenge", eph = Wire.Hex(eph.Public), nonce = Wire.Hex(nonce), account = account.Id }, ct);
        var proof = await ReceiveControlAsync(ws, ct);
        var mac = proof.GetProperty("t").GetString() == "proof" ? Wire.Unhex(proof.GetProperty("mac").GetString()!) : [];
        var expected = Wire.ProofMac(eph.Dh(pub), nonce, account.Id);
        if (!CryptographicOperations.FixedTimeEquals(mac, expected))
        {
            await CloseAsync(ws, CloseCodes.ProofFailed, "proof-failed");
            return null;
        }
        return new Connection(ws, account, Wire.DeviceId(pub), name, kind);
    }

    async Task<string> ForwardAsync(Connection conn, ConcurrentDictionary<string, Connection> devices, CancellationToken aborted)
    {
        var ws = conn.Socket;
        while (ws.State == WebSocketState.Open)
        {
            (WebSocketMessageType Type, byte[] Data)? frame;
            try { frame = await Wire.ReceiveAsync(ws, Wire.MaxFrame, aborted); }
            catch (FrameTooLargeException)
            {
                await CloseAndDrainAsync(conn, CloseCodes.TooLarge, "frame-too-large");
                return "frame too large";
            }
            if (frame is null) { return "closed"; }
            var (type, data) = frame.Value;
            if (type == WebSocketMessageType.Text)
            {
                // The one control message a device sends after admission: forward a push.
                if (!WithinRate(conn))
                {
                    await CloseAndDrainAsync(conn, CloseCodes.RateLimited, "rate-limited");
                    return "rate limited";
                }
                JsonElement m;
                try { m = JsonDocument.Parse(data).RootElement; } catch (JsonException) { continue; }
                if (m.TryGetProperty("t", out var t) && t.GetString() == "push" && m.TryGetProperty("id", out var pid) && pid.TryGetInt64(out var id))
                {
                    var c = conn;
                    _ = Task.Run(async () =>
                    {
                        var r = await push.SendAsync(c.Account.Id, m);
                        await c.SendJsonAsync(new { t = "push-result", id, status = r.Status, error = r.Error });
                    });
                }
                continue;
            }
            if (type != WebSocketMessageType.Binary) { continue; }
            if (!WithinRate(conn))
            {
                await CloseAndDrainAsync(conn, CloseCodes.RateLimited, "rate-limited");
                return "rate limited";
            }
            if (data.Length <= 1 + Wire.IdBytes || data[0] != Wire.DataFrame) { continue; }
            conn.FramesIn++;
            conn.BytesIn += data.Length;
            var to = Wire.Hex(data.AsSpan(1, Wire.IdBytes));
            var from = conn.DeviceId;
            if (options.Route is { } route) { (from, to) = route(from, to); }
            var payload = data[(1 + Wire.IdBytes)..];
            options.Tap?.Invoke(from, to, payload);
            // Routing never leaves the sender's account.
            if (!devices.TryGetValue(to, out var target))
            {
                await conn.SendJsonAsync(new { t = "undeliverable", to });
                continue;
            }
            var outFrame = new byte[data.Length];
            outFrame[0] = Wire.DataFrame;
            Wire.Unhex(from).CopyTo(outFrame, 1);
            payload.CopyTo(outFrame, 1 + Wire.IdBytes);
            await target.SendAsync(outFrame, WebSocketMessageType.Binary);
            Interlocked.Increment(ref target.FramesOut);
            Interlocked.Add(ref target.BytesOut, outFrame.Length);
        }
        return "closed";
    }

    bool WithinRate(Connection c)
    {
        var now = Environment.TickCount64;
        if (now - c.WindowStart >= 1000) { c.WindowStart = now; c.WindowFrames = 0; }
        return ++c.WindowFrames <= options.MaxFramesPerSecond;
    }

    // Ending the request straight after a close frame resets the TCP connection while the
    // client is still sending, and the reset discards the close frame before the client
    // reads it, so it never learns why. Read and discard until it answers, briefly.
    static async Task CloseAndDrainAsync(Connection conn, int code, string reason)
    {
        await conn.CloseAsync(code, reason);
        var buffer = new byte[16 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (conn.Socket.State == WebSocketState.CloseSent)
            {
                if ((await conn.Socket.ReceiveAsync(buffer, cts.Token)).MessageType == WebSocketMessageType.Close) { break; }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
    }

    async Task BroadcastPresenceAsync(string accountId)
    {
        if (!accounts.TryGetValue(accountId, out var devices)) { return; }
        var list = devices.Values.Select(c => new { id = c.DeviceId, name = c.Name, kind = c.Kind, since = c.Since }).ToArray();
        var message = new { t = "presence", devices = list };
        await Task.WhenAll(devices.Values.Select(c => c.SendJsonAsync(message)));
    }

    static string Clean(string? name)
    {
        var s = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > 64 ? s[..64] : s.Length == 0 ? "unnamed" : s;
    }

    static async Task<JsonElement> ReceiveControlAsync(WebSocket ws, CancellationToken ct)
    {
        var frame = await Wire.ReceiveAsync(ws, Wire.MaxControl, ct) ?? throw new WebSocketException("closed during hello");
        if (frame.Type != WebSocketMessageType.Text) { throw new InvalidOperationException("expected a text frame"); }
        return JsonDocument.Parse(frame.Data).RootElement;
    }

    static Task SendJsonAsync(WebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);

    static async Task CloseAsync(WebSocket ws, int code, string reason)
    {
        try
        {
            // CloseOutputAsync: a refused client gets the code at once, without the relay
            // waiting on its close reply.
            if (ws.State == WebSocketState.Open) { await ws.CloseOutputAsync((WebSocketCloseStatus)code, reason, default); }
        }
        catch (WebSocketException) { }
    }
}
