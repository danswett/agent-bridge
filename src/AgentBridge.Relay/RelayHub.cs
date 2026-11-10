using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Relay;

// Routes opaque frames between the devices of one account. It sees routing ids, sizes and
// timing, and nothing a session says: payloads are Noise ciphertext end to end.
public sealed class RelayHub(RelayOptions options, Tokens tokens, PushGateway push, BlobService blobs, FeedbackService feedback, MachineRoster roster, DeviceRoster deviceRoster, RelayMetrics metrics, ILogger<RelayHub> log)
{
    static readonly HashSet<string> Kinds = ["host", "app", "pwa", "phone", "test"];
    // The kinds a person uses. Only these may forget a machine: never a host, which is where an
    // agent acts, nor a test device, nor any kind added later until it is put here.
    static readonly HashSet<string> PersonKinds = ["app", "pwa", "phone"];
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Connection>> accounts = new();
    readonly SessionRegistry registry = new(options.Roster.Time);
    long presenceVersion;

    public int Online => accounts.Values.Sum(a => a.Count);
    // Sockets by device kind, for the live-sockets gauge. Kinds only, never an account or device.
    // Every configured hostname (and other) x every kind is listed, zero included, so a hostname
    // that has emptied reads 0 rather than vanishing like a relay that is down.
    public IEnumerable<(string Kind, string Site, int Count)> OnlineByKind()
    {
        var live = accounts.Values.SelectMany(a => a.Values).GroupBy(c => (c.Kind, c.Site)).ToDictionary(g => g.Key, g => g.Count());
        var sites = options.Hostnames.Select(h => h.ToLowerInvariant()).Append("other").Distinct();
        return sites.SelectMany(s => Kinds.Select(k => (k, s, live.GetValueOrDefault((k, s))))).ToList();
    }

    // The Host header reduced to a configured name or "other": never free text, so a client
    // cannot mint metric labels by sending its own.
    public string SiteOf(string? host)
    {
        if (string.IsNullOrEmpty(host)) { return "other"; }
        var name = host.Split(":")[0].TrimEnd(".".ToCharArray());
        return options.Hostnames.FirstOrDefault(h => h.Equals(name, StringComparison.OrdinalIgnoreCase))?.ToLowerInvariant() ?? "other";
    }

    // Set when the process is told to stop. Readiness then fails and new sockets are refused,
    // so a device told to reconnect (1012) lands on a replica that is staying, not this one.
    public bool Draining { get; private set; }

    // The roster's last write comes after this, when the app stops its writer (RosterWriter).
    public Task DrainAsync()
    {
        Draining = true;
        return CloseAllAsync();
    }

    // Confirm removals from storage, explicit forgets, or admission's roster cap. Existing
    // hosts need two messages too, not only a new host's first complete roster (#206).
    public async Task RosterChangedAsync(string accountId)
    {
        await BroadcastPresenceAsync(accountId);
        await BroadcastPresenceAsync(accountId);
    }

    // On shutdown, tell every device to reconnect now. A rolling deploy otherwise leaves old
    // sockets on the draining replica for up to its drain timeout (about 90 s observed on
    // Container Apps) while new devices land on the new one, and neither half sees the other.
    public Task CloseAllAsync() => Task.WhenAll(accounts.Values.SelectMany(a => a.Values).Select(c => c.CloseAsync(CloseCodes.Restarting, "restarting")));

    sealed class Connection(WebSocket ws, Account account, string deviceId, string name, string kind, string site, string? start = null)
    {
        public string? Start => start;
        // Correlates one socket's log lines. Random and meaningless outside this process, so
        // logs carry no account, device or name (design §5.10).
        public string Tag { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        readonly SemaphoreSlim sendLock = new(1, 1);
        public WebSocket Socket => ws;
        public Account Account => account;
        public string DeviceId => deviceId;
        public string Name => name;
        public string Kind => kind;
        public string Site => site;
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
        public long FramesIn, FramesOut, BytesIn, BytesOut;
        public long WindowStart = Environment.TickCount64;
        public int WindowFrames;
        public int RosterInFlight;
        public long RegistryGetAt = -2000;

        public async Task SendAsync(byte[] data, WebSocketMessageType type)
        {
            await sendLock.WaitAsync();
            try { await SendLockedAsync(data, type); }
            finally { sendLock.Release(); }
        }

        // The newest presence this device has been sent. Announcements are taken in order but
        // sent concurrently, so an older one could reach a device after a newer one and stay its
        // last word: a machine forgotten a moment earlier listed again (#151), or a device that
        // left listed as here. One older than what was sent is dropped instead.
        long presenceSent;
        bool rosterConfirmed;

        public async Task SendPresenceAsync(byte[] data, long version, bool hasRoster)
        {
            await sendLock.WaitAsync();
            try
            {
                if (version <= presenceSent) { return; }
                presenceSent = version;
                await SendLockedAsync(data, WebSocketMessageType.Text);
                // A returning host may have missed a forget while offline. Confirm its first
                // complete roster, including one loaded late, without weakening its two-frame guard.
                if (!hasRoster) { rosterConfirmed = false; }
                else if (Kind == "host" && !rosterConfirmed)
                {
                    await SendLockedAsync(data, WebSocketMessageType.Text);
                    rosterConfirmed = true;
                }
            }
            finally { sendLock.Release(); }
        }

        async Task SendLockedAsync(byte[] data, WebSocketMessageType type)
        {
            try
            {
                if (ws.State != WebSocketState.Open) { return; }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ws.SendAsync(data, type, true, cts.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
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

    // early: the device asked for the challenge with the WebSocket's opening (?admit=1).
    public async Task RunAsync(WebSocket ws, CancellationToken aborted, bool early = false, string? host = null)
    {
        var opened = Stopwatch.GetTimestamp();
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        Connection? conn;
        try { conn = await AdmitAsync(ws, early && options.EarlyChallenge, SiteOf(host), handshakeTimeout.Token); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException or FrameTooLargeException)
        {
            await RefuseAsync(ws, CloseCodes.Protocol, "bad-hello");
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
            metrics.Refused(CloseCodes.TooManyDevices);
            await conn.CloseAsync(CloseCodes.TooManyDevices, "too-many-devices");
            return;
        }
        if (replaced != null) { await replaced.CloseAsync(CloseCodes.Replaced, "replaced"); }
        // A host is recorded as itself: the id it proved and the name it gave, nothing a message says.
        await roster.ConnectedAsync(conn.Account.Id, conn.DeviceId, conn.Name, conn.Kind, conn.Start);

        metrics.Connected(conn.Kind, conn.Site);
        log.LogInformation("connect {Conn} {Kind}; {Count} online in account", conn.Tag, conn.Kind, devices.Count);
        // Announce before "ready", so peers learn of a device before it can send them anything.
        await RosterChangedAsync(conn.Account.Id);
        // blobs: present only with a blob store, so a device knows whether files over 256 KiB can go.
        await conn.SendJsonAsync(new { t = "ready", device = conn.DeviceId, account = conn.Account.Id, login = conn.Account.Login, blobs = blobs.Advertised, feedback = feedback.Advertised });
        metrics.ConnectTime(conn.Kind, Stopwatch.GetElapsedTime(opened));

        string end = "closed";
        Exception? endedBy = null;
        try { end = await ForwardAsync(conn, devices, aborted); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { end = "dropped"; endedBy = e; }
        finally
        {
            // Update roster bookkeeping before removing the slot, under the broadcast lock.
            // Only remove this connection: a replacement may already hold the slot.
            lock (devices)
            {
                roster.Disconnected(conn.Account.Id, conn.DeviceId, conn.Kind);
                if (conn.Kind == "host" && devices.TryGetValue(conn.DeviceId, out var held) && ReferenceEquals(held, conn)) { registry.Drop(conn.Account.Id, conn.DeviceId); }
                ((ICollection<KeyValuePair<string, Connection>>)devices).Remove(new(conn.DeviceId, conn));
            }
            // Read before our own close below: this is what the device sent, not our answer.
            var cause = DisconnectCause.Describe(endedBy, ws.CloseStatus);
            await conn.CloseAsync((int)WebSocketCloseStatus.NormalClosure, "");
            metrics.Disconnected(end);
            log.LogInformation("disconnect {Conn} ({End}) after {Seconds:F0}s; frames in {In} out {Out}, bytes in {BytesIn} out {BytesOut}; cause {Cause}",
                conn.Tag, end, (DateTimeOffset.UtcNow - conn.Since).TotalSeconds, conn.FramesIn, conn.FramesOut, conn.BytesIn, conn.BytesOut, cause);
            await BroadcastPresenceAsync(conn.Account.Id);
        }
    }

    // hello, challenge, proof, ready: two round trips after the WebSocket opens. With early, the
    // challenge is sent as soon as it opens, before hello: it arrives with the opening, the device
    // sends its hello and its proof together, and ready is one round trip later (#603). It carries
    // no account, since none is known before the token, and the proof is the early one, which
    // leaves the account out (Wire.EarlyProofMac). Either way the relay's ephemeral key and nonce
    // are this connection's alone, so a proof is good for nothing else.
    async Task<Connection?> AdmitAsync(WebSocket ws, bool early, string site, CancellationToken ct)
    {
        using var eph = P256KeyPair.Generate();
        var nonce = RandomNumberGenerator.GetBytes(32);
        if (early) { await SendJsonAsync(ws, new { t = "challenge", eph = Wire.Hex(eph.Public), nonce = Wire.Hex(nonce), early = true }, ct); }
        var hello = await ReceiveControlAsync(ws, ct);
        if (hello.GetProperty("t").GetString() != "hello") { await RefuseAsync(ws, CloseCodes.Protocol, "expected-hello"); return null; }
        var account = tokens.Validate(hello.GetProperty("token").GetString());
        if (account is null) { await RefuseAsync(ws, CloseCodes.AuthFailed, "bad-token"); return null; }
        var pub = Wire.Unhex(hello.GetProperty("pub").GetString()!);
        try { P256KeyPair.AssertOnCurve(pub); }
        catch (CryptographicException) { await RefuseAsync(ws, CloseCodes.Protocol, "bad-key"); return null; }
        var kind = hello.GetProperty("kind").GetString() ?? "";
        if (!Kinds.Contains(kind)) { await RefuseAsync(ws, CloseCodes.Protocol, "bad-kind"); return null; }
        var name = Clean(hello.GetProperty("name").GetString());
        // Only a host records how it starts, and only the two known words are kept.
        var start = kind == "host" && hello.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        if (start is not ("boot" or "logon")) { start = null; }

        if (!early) { await SendJsonAsync(ws, new { t = "challenge", eph = Wire.Hex(eph.Public), nonce = Wire.Hex(nonce), account = account.Id }, ct); }
        var proof = await ReceiveControlAsync(ws, ct);
        var mac = proof.GetProperty("t").GetString() == "proof" ? Wire.Unhex(proof.GetProperty("mac").GetString()!) : [];
        var expected = early ? Wire.EarlyProofMac(eph.Dh(pub), nonce) : Wire.ProofMac(eph.Dh(pub), nonce, account.Id);
        if (!CryptographicOperations.FixedTimeEquals(mac, expected))
        {
            await RefuseAsync(ws, CloseCodes.ProofFailed, "proof-failed");
            return null;
        }
        return new Connection(ws, account, Wire.DeviceId(pub), name, kind, site, start);
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
                // The control messages a device sends after admission: forward a push, or ask the
                // blob store for a URL (Blobs.cs). Each counts against the frame rate.
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
                        metrics.Pushed(r);
                        await c.SendJsonAsync(new { t = "push-result", id, status = r.Status, error = r.Error });
                    });
                }
                else if (m.TryGetProperty("t", out var bt) && bt.GetString() is "blob-put" or "blob-get" or "blob-delete")
                {
                    var c = conn;
                    var kind = bt.GetString()!;
                    var message = m.Clone();
                    _ = Task.Run(async () => await c.SendJsonAsync(await blobs.HandleAsync(c.Account, c.DeviceId, kind, message)));
                }
                else if (m.TryGetProperty("t", out var fbt) && fbt.GetString() == "feedback-put")
                {
                    var c = conn;
                    var message = m.Clone();
                    _ = Task.Run(async () => await c.SendJsonAsync(await feedback.HandleAsync(c.Account, message)));
                }
                else if (m.TryGetProperty("t", out var rt) && rt.GetString() is "roster-get" or "roster-append")
                {
                    var c = conn;
                    var message = m.Clone();
                    var replyType = rt.GetString() == "roster-get" ? "roster" : "roster-append-result";
                    var rid = m.TryGetProperty("id", out var ridp) && ridp.TryGetInt64(out var rn) ? rn : 0;
                    if (Interlocked.Increment(ref c.RosterInFlight) > MaxRosterInFlight)
                    {
                        Interlocked.Decrement(ref c.RosterInFlight);
                        _ = Task.Run(async () => await c.SendJsonAsync(new { t = replyType, id = rid, error = "unavailable" }));
                        continue;
                    }
                    _ = Task.Run(async () =>
                    {
                        object reply;
                        try { reply = await RosterMessageAsync(c, message); }
                        catch (Exception e)
                        {
                            log.LogWarning(e, "roster message failed");
                            reply = new { t = replyType, id = rid, error = "unavailable" };
                        }
                        finally { Interlocked.Decrement(ref c.RosterInFlight); }
                        try { await c.SendJsonAsync(reply); } catch (Exception) { }
                    });
                }
                else if (m.TryGetProperty("t", out var ft) && ft.GetString() == "forget")
                {
                    await ForgetAsync(conn, devices, m);
                }
                else if (m.TryGetProperty("t", out var gt) && gt.GetString() is "registry-put" or "registry-get")
                {
                    await RegistryAsync(conn, devices, m, gt.GetString() == "registry-put");
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
            var received = Stopwatch.GetTimestamp();
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
                metrics.Undeliverable();
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
            metrics.Forwarded(outFrame.Length);
            metrics.ForwardLatency(conn.Kind, Stopwatch.GetElapsedTime(received));
        }
        return "closed";
    }

    // The session registry (docs/protocol/roster.md): a host puts its own machine's snapshot, any
    // device of the account gets every machine's. Nothing here leaves the sender's account.
    async Task RegistryAsync(Connection conn, ConcurrentDictionary<string, Connection> devices, JsonElement m, bool put)
    {
        var id = m.TryGetProperty("id", out var i) && i.TryGetInt64(out var n) ? n : 0;
        var account = conn.Account.Id;
        if (put)
        {
            string? error = conn.Kind != "host" ? "not-allowed"
                : m.TryGetProperty("record", out var rec) ? registry.Put(account, conn.DeviceId, rec) : "bad-record";
            // Only the outcome is logged, never the record.
            log.LogInformation("registry-put {Conn}: {Outcome}", conn.Tag, error ?? "ok");
            await conn.SendJsonAsync(new { t = "registry-result", id, error });
            return;
        }
        var now = Environment.TickCount64;
        if (now - conn.RegistryGetAt < 2000) { await conn.SendJsonAsync(new { t = "registry", id, error = "slow-down" }); return; }
        conn.RegistryGetAt = now;
        var parts = new List<Dictionary<string, object?>>();
        foreach (var (machine, json, lastSeen, fresh) in registry.Read(account))
        {
            var host = devices.TryGetValue(machine, out var d) && d.Kind == "host" ? d : null;
            var online = host is not null && fresh;
            parts.Add(new() { ["t"] = "registry", ["id"] = id, ["machine"] = machine, ["name"] = host?.Name, ["online"] = online, ["lastSeen"] = lastSeen, ["record"] = online ? JsonDocument.Parse(json).RootElement : null });
        }
        var known = (roster.Known(account, _ => false) ?? [])
            .Select(k => new { machine = k.Id, name = k.Name, lastSeen = k.LastSeen });
        for (var p = 0; p < parts.Count; p++)
        {
            parts[p]["part"] = p + 1;
            parts[p]["of"] = parts.Count;
            await conn.SendJsonAsync(parts[p]);
        }
        await conn.SendJsonAsync(new { t = "registry", id, known });
    }

    // A person's device forgetting one of the account's machines that is gone (docs/protocol/roster.md).
    async Task ForgetAsync(Connection conn, ConcurrentDictionary<string, Connection> devices, JsonElement m)
    {
        var id = m.TryGetProperty("id", out var i) && i.TryGetInt64(out var n) ? n : 0;
        var machine = m.TryGetProperty("machine", out var mm) && mm.ValueKind == JsonValueKind.String ? mm.GetString() : null;
        string? error;
        if (!PersonKinds.Contains(conn.Kind))
        {
            metrics.Roster("forget", "not-allowed");
            error = "not-allowed";
        }
        else
        {
            lock (devices) { error = roster.Forget(conn.Account.Id, machine, devices.ContainsKey); }
        }
        log.LogInformation("forget {Conn} {Kind}: {Outcome}", conn.Tag, conn.Kind, error ?? "ok");
        await conn.SendJsonAsync(new { t = "forget-result", id, machine = Wire.IsDeviceId(machine) ? machine!.ToLowerInvariant() : null, error });
        if (error is null) { await RosterChangedAsync(conn.Account.Id); }
    }

    // The signed device roster (docs/protocol/devices.md): read it, or offer entries to it. Open to
    // every kind of device, host included, because a host needs the log to verify it. The relay
    // only stores; it enforces nothing.
    async Task<object> RosterMessageAsync(Connection c, JsonElement m)
    {
        var id = m.TryGetProperty("id", out var idp) && idp.TryGetInt64(out var n) ? n : 0;
        if (m.GetProperty("t").GetString() == "roster-get")
        {
            var s = await deviceRoster.GetAsync(c.Account.Id);
            return s is null
                ? new { t = "roster", id, error = "unavailable" }
                : (object)new { t = "roster", id, mode = s.Mode, generation = s.Generation, entries = s.Entries };
        }
        if (!m.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() is 0 or > DeviceRoster.MaxPerAppend
            || arr.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            return new { t = "roster-append-result", id, error = "bad-request" };
        }
        var results = await deviceRoster.AppendAsync(c.Account.Id, [.. arr.EnumerateArray().Select(e => e.GetString()!)]);
        return results is null
            ? new { t = "roster-append-result", id, error = "unavailable" }
            : DeviceRoster.AppendReply(id, results);
    }

    // Roster requests one connection may have running at once; more get "unavailable" at once.
    const int MaxRosterInFlight = 2;

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
        byte[] message;
        long version;
        bool hasRoster;
        Connection[] to;
        // Taken and numbered under the lock admission and removal hold, so a higher number is
        // always a later view of the account (Connection.SendPresenceAsync).
        lock (devices)
        {
            version = Interlocked.Increment(ref presenceVersion);
            to = [.. devices.Values];
            var list = to.Select(c => new { id = c.DeviceId, name = c.Name, kind = c.Kind, since = c.Since }).ToArray();
            // known: the account's machines that are not here, with when each was last seen. Only
            // with a roster; older clients read devices alone and ignore it.
            var known = roster.Known(accountId, devices.ContainsKey);
            hasRoster = known is not null;
            object snapshot = known is not null
                ? new { t = "presence", devices = list, known = known.Select(k => new { id = k.Id, name = k.Name, kind = k.Kind, lastSeen = k.LastSeen, start = k.Start }).ToArray() }
                : new { t = "presence", devices = list };
            message = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        }
        await Task.WhenAll(to.Select(c => c.SendPresenceAsync(message, version, hasRoster)));
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

    async Task RefuseAsync(WebSocket ws, int code, string reason)
    {
        metrics.Refused(code);
        await CloseAsync(ws, code, reason);
        // A device answering an early challenge sends its proof with its hello, so it is still
        // sending when its hello is refused; returning now would reset the socket and discard the
        // close frame before it reads why (4001, which stops a host retrying). As CloseAndDrainAsync.
        var buffer = new byte[Wire.MaxControl];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (ws.State == WebSocketState.CloseSent)
            {
                if ((await ws.ReceiveAsync(buffer, cts.Token)).MessageType == WebSocketMessageType.Close) { break; }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
    }

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
