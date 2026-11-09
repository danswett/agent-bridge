using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

// The fleet kill switch for own-agents messaging (L6, #1354). Only ever turns the mode OFF: the
// mode is on when config.json says agentMessaging.remote is own-account, and every path here
// writes "off" there or does nothing. Turning it on again is a person running `config set` at the
// machine, so a peer, a message or an agent can never switch it on.
//
// The epoch (Unix seconds of the latest kill) is kept in its own host-written file, never settable
// by `config set`, and read afresh each time like the config, so `agents lite kill` run in another
// process takes effect in a running host with no restart. It rides unsigned on lite sends and acks
// on purpose: a higher one can only turn the mode off.
public sealed class LiteKill : ILiteMode
{
    public const string Op = "agent.lite.kill";
    public const string FileName = "lite-kill-epoch.json";
    const long MaxSkewSeconds = 86400;

    readonly string folder;
    readonly ILitePeers peers;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly ConcurrentDictionary<CancellationTokenSource, byte> inFlight = new();
    volatile bool off;
    long pushed;
    long seen;

    public LiteKill(string folder, ILitePeers peers, Action<string>? log = null, TimeProvider? time = null)
    {
        this.folder = folder;
        this.peers = peers;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
    }

    // Reading the record each time lets an unreadable one latch the mode off.
    public bool On { get { ReadEpoch(); return !off && HostSettings.CurrentAgentMessaging(folder).RemoteEnabled; } }

    public long KillEpoch => ReadEpoch();

    public void TurnOff(string why)
    {
        // Latched first so a send starting now is refused, then released if the setting took:
        // the setting alone says whether the mode is on, so a person turning it on needs no restart.
        off = true;
        var error = new StringWriter();
        var failed = ConfigCommands.Run(["config", "set", "agentMessaging.remote", "off"], folder, TextWriter.Null, error) != 0;
        // Callers waiting on a send stop now; they fall back to Home Assistant.
        foreach (var flight in inFlight.Keys) { try { flight.Cancel(); } catch (ObjectDisposedException) { } }
        off = failed;
        if (failed)
        {
            log($"lite: could not write agentMessaging.remote=off ({error.ToString().Trim()}); off until restart");
        }
        log($"lite: own-agents messaging turned off ({why})");
    }

    // Monotonic: an equal or lower epoch does nothing.
    public void RaiseEpoch(long epoch) => TryRaise(epoch);

    bool TryRaise(long epoch)
    {
        // An epoch far ahead of the clock would block every later kill, so it is not believed.
        if (epoch > time.GetUtcNow().ToUnixTimeSeconds() + MaxSkewSeconds)
        {
            log($"lite: ignored kill epoch {epoch}, which is too far ahead");
            return false;
        }
        var raised = Locked(() =>
        {
            if (epoch <= ReadEpoch()) { return false; }
            off = true;
            TryWrite(epoch);
            return true;
        });
        if (raised) { TurnOff($"kill epoch {epoch}"); }
        return raised;
    }

    // `agents lite kill`: a new epoch, always above the last, and the mode off at once.
    public long Kill()
    {
        var epoch = Locked(() =>
        {
            var next = Math.Max(time.GetUtcNow().ToUnixTimeSeconds(), ReadEpoch() + 1);
            off = true;
            TryWrite(next);
            return next;
        });
        TurnOff($"killed at this machine, epoch {epoch}");
        return epoch;
    }

    // What a lite send or ack from a device carried (killEpoch). Believed only from a pinned peer.
    public bool Observe(string deviceId, long epoch)
    {
        if (epoch <= KillEpoch) { return false; }
        if (!peers.TryFind(deviceId, out _))
        {
            log($"lite: ignored a kill epoch from {Short(deviceId)}, which is not a pinned own-agents peer");
            return false;
        }
        return TryRaise(epoch);
    }

    // The send runs under a token the kill cancels. A send cut short that way may already have
    // been typed on the other side, so it is Unconfirmed, never a silent drop.
    public async Task<LiteOutcome> GuardAsync(string request, Func<CancellationToken, Task<LiteOutcome>> send, CancellationToken ct)
    {
        using var flight = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inFlight[flight] = 0;
        try
        {
            if (!On) { return new LiteOutcome(LiteKind.Off, "off", request); }
            return await send(flight.Token);
        }
        catch (OperationCanceledException) when (flight.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new LiteOutcome(LiteKind.Unconfirmed, "killed", request);
        }
        finally { inFlight.TryRemove(flight, out _); }
    }

    // In front of the hub's handler, so the kill op is judged here: only a pinned own host, and
    // only to turn the mode off.
    public Func<SecureChannel, string, JsonElement, Task<object?>> Wrap(Func<SecureChannel, string, JsonElement, Task<object?>> inner) =>
        (channel, op, data) => op != Op ? inner(channel, op, data) : Task.FromResult<object?>(HandleKill(channel.PeerId, channel.PeerKind, data));

    public object HandleKill(string peerId, string peerKind, JsonElement data)
    {
        if (peerKind != UsagePeers.HostKind || !peers.TryFind(peerId, out _))
        {
            log($"lite: ignored {Op} from {Short(peerId)}, which is not a pinned own-agents peer");
            return new { applied = false };
        }
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("epoch", out var e) || !e.TryGetInt64(out var epoch))
        {
            throw new ArgumentException($"{Op} needs a numeric epoch");
        }
        var applied = TryRaise(epoch);
        // The sender told the other hosts itself, so this one does not push it on.
        if (applied) { Interlocked.Exchange(ref pushed, Math.Max(Interlocked.Read(ref pushed), epoch)); }
        return new { applied, epoch = KillEpoch };
    }

    // Tells every connected pinned host of the account the latest epoch, when it is newer than the
    // last told. Called on a short tick, which is also how a kill made by the command in another
    // process reaches the other machines.
    public async Task PushAsync(RelayClient relay, ChannelHub hub, CancellationToken ct = default)
    {
        var epoch = ReadEpoch();
        if (epoch == 0 || epoch <= Interlocked.Read(ref pushed)) { return; }
        var told = true;
        foreach (var p in relay.Snapshot.Presence.Where(p => p.Kind == UsagePeers.HostKind && p.Id != relay.DeviceId && peers.TryFind(p.Id, out _)))
        {
            SecureChannel? ch = null;
            try
            {
                ch = await hub.OpenAsync(p.Id, TimeSpan.FromSeconds(10));
                await ch.RequestAsync(Op, new { epoch }, TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { told = false; log($"lite: could not tell {Short(p.Id)} of the kill ({e.GetType().Name})"); }
            finally { if (ch is not null) { try { await hub.CloseAsync(ch); } catch (Exception) { } } }
        }
        if (told) { Interlocked.Exchange(ref pushed, epoch); }
    }

    public void ForgetPushed() => Interlocked.Exchange(ref pushed, 0);

    long ReadEpoch()
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) { return 0; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("epoch", out var e) && e.TryGetInt64(out var n) && n > 0) { seen = Math.Max(seen, n); }
            return seen;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable record must not let an old kill replay: keep the highest seen and stay off.
            off = true;
            log("lite: kill epoch file unreadable; off until restart or the next kill");
            return seen;
        }
    }

    // One writer at a time across the host and `agents lite kill` processes, so a lower epoch
    // can never overwrite a higher one.
    T Locked<T>(Func<T> body)
    {
        lock (inFlight)
        {
            using var mutex = new Mutex(false, "AgentBridgeLiteKill-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder))))[..16]);
            var held = false;
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
            try { return body(); }
            finally { if (held) { mutex.ReleaseMutex(); } }
        }
    }

    // The mode is already latched off, so a failed write cannot leave a kill unapplied.
    void TryWrite(long epoch)
    {
        seen = Math.Max(seen, epoch);
        try { WriteEpoch(epoch); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"lite: could not save kill epoch {epoch} ({e.GetType().Name}); off until restart"); }
    }

    void WriteEpoch(long epoch)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { epoch }));
        File.Move(temp, path, overwrite: true);
    }

    static string Short(string id) => id[..Math.Min(8, id.Length)];
}
