using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgentBridge.Relay;

public sealed class RosterOptions
{
    // "" (the default): kept in memory, so a restart forgets every machine that is offline;
    // "azure": one blob per account in Azure Storage, through the app's managed identity;
    // "off": no roster at all, and presence carries no `known`.
    public string Backend { get; set; } = "";
    // https://<account>.blob.core.windows.net, for the azure backend.
    public string? AccountUrl { get; set; }
    public string Container { get; set; } = "roster";
    // The user-assigned identity to sign in as. Unset: DefaultAzureCredential (a dev machine).
    public string? ManagedIdentityClientId { get; set; }
    // Machines remembered per account. Online hosts are capped lower, by MaxDevicesPerAccount,
    // so the oldest offline one goes first.
    public int MaxMachines { get; set; } = 64;
    // The fewest seconds between two writes of one account's roster, however often its hosts
    // come and go: a host in a reconnect loop costs one write per interval, not one per loop.
    public TimeSpan WriteInterval { get; set; } = TimeSpan.FromSeconds(30);
    // How often an online host's "last seen" is brought up to date in storage, so a relay that
    // dies without draining leaves it at most this stale.
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);
    // How long after first reading an account's roster it is read again: long enough for a relay
    // handing over in a deploy to have written its last (RosterWriter.FinalFlush). After that,
    // every RefreshInterval while any of the account's devices is connected.
    public TimeSpan ReadAgainAfter { get; set; } = TimeSpan.FromSeconds(60);
    // How often the writer looks for something to write.
    public TimeSpan FlushEvery { get; set; } = TimeSpan.FromSeconds(5);
    // How long, and how many, forgets are kept: they settle a forget on one relay against the
    // same machine seen on another at the same moment (mid-deploy). A stale copy is kept from
    // bringing a forgotten machine back by the merge itself (MachineRoster.LoadAsync).
    public TimeSpan ForgottenFor { get; set; } = TimeSpan.FromDays(1);
    public int MaxForgotten { get; set; } = 256;
    public int MaxForgetsPerMinute { get; set; } = 10;
    // The longest any one read or write of the store may take: admission waits this long at most
    // for an account's stored roster, and the writer gives up on a call (and tries again later).
    public TimeSpan StoreTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeProvider Time { get; set; } = TimeProvider.System;
    // Test only: the store to use, whatever Backend says (a restart keeping its rosters).
    public IRosterStore? Store { get; set; }
}

// A machine the account has had: always a host, the only kind recorded.
public sealed record RosterMachine(string Id, string Name, string Kind, DateTimeOffset LastSeen);

// What is stored for an account: its machines, and the ones forgotten lately, each with when.
// A machine shows while it was seen after it was last forgotten, so copies merge whichever
// order they meet in.
public sealed class RosterDocument
{
    public int V { get; set; } = 1;
    public List<RosterMachine> Machines { get; set; } = [];
    public Dictionary<string, DateTimeOffset> Forgotten { get; set; } = [];
}

public sealed record StoredRoster(byte[] Body, string ETag);

// Where rosters are kept, by an opaque name. Writes are conditional, so two relays (one
// draining in a deploy, one starting) cannot lose each other's changes: a write made against
// a copy that has changed since is refused, and the writer reads and merges again.
public interface IRosterStore
{
    Task<StoredRoster?> ReadAsync(string name, CancellationToken ct);
    // The new ETag; null when the stored copy is no longer the one etag names (or, with etag
    // null, when one has been created since).
    Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct);
    // False when the stored copy is no longer the one etag names.
    Task<bool> DeleteAsync(string name, string etag, CancellationToken ct);
}

public sealed class MemoryRosterStore : IRosterStore
{
    readonly ConcurrentDictionary<string, StoredRoster> blobs = new();
    long next;

    public int Count => blobs.Count;
    public IEnumerable<string> Names => blobs.Keys;
    public int Writes;

    public Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) =>
        Task.FromResult(blobs.TryGetValue(name, out var b) ? b : null);

    public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct)
    {
        lock (blobs)
        {
            blobs.TryGetValue(name, out var have);
            if (have?.ETag != etag) { return Task.FromResult<string?>(null); }
            var tag = Interlocked.Increment(ref next).ToString();
            blobs[name] = new StoredRoster(body, tag);
            Interlocked.Increment(ref Writes);
            return Task.FromResult<string?>(tag);
        }
    }

    public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct)
    {
        lock (blobs)
        {
            if (!blobs.TryGetValue(name, out var have)) { return Task.FromResult(true); }
            if (have.ETag != etag) { return Task.FromResult(false); }
            blobs.TryRemove(name, out _);
            return Task.FromResult(true);
        }
    }
}

// One block blob per account in a container of its own, written with If-Match/If-None-Match.
// The app's managed identity holds Storage Blob Data Contributor on that container alone
// (infra/modules/roster-store.bicep); there is no key.
public sealed class AzureRosterStore : IRosterStore
{
    // A roster of 64 machines is about 10 KiB; anything far bigger was not written by a relay.
    const long MaxBytes = 256 * 1024;
    readonly BlobContainerClient container;

    public AzureRosterStore(RosterOptions o)
    {
        TokenCredential credential = string.IsNullOrEmpty(o.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId));
        container = new BlobServiceClient(new Uri(o.AccountUrl ?? throw new InvalidOperationException("Relay:Roster:AccountUrl is required for the azure backend")), credential)
            .GetBlobContainerClient(o.Container);
    }

    public async Task<StoredRoster?> ReadAsync(string name, CancellationToken ct)
    {
        try
        {
            // Streamed, and its length checked before a byte is read: a stored copy far bigger
            // than a relay writes is not buffered, only replaced (an empty body) by the next write.
            var r = await container.GetBlobClient(name).DownloadStreamingAsync(cancellationToken: ct);
            using var download = r.Value;
            var etag = download.Details.ETag.ToString();
            if (download.Details.ContentLength > MaxBytes) { return new StoredRoster([], etag); }
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await download.Content.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > MaxBytes) { return new StoredRoster([], etag); }
                buffer.Write(chunk, 0, n);
            }
            return new StoredRoster(buffer.ToArray(), etag);
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct)
    {
        try
        {
            var r = await container.GetBlobClient(name).UploadAsync(new BinaryData(body), new BlobUploadOptions
            {
                Conditions = etag is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(etag) },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            }, ct);
            return r.Value.ETag.ToString();
        }
        // 412 for If-Match, 409 (BlobAlreadyExists) for If-None-Match.
        catch (RequestFailedException e) when (e.Status is 409 or 412) { return null; }
    }

    public async Task<bool> DeleteAsync(string name, string etag, CancellationToken ct)
    {
        try
        {
            await container.GetBlobClient(name).DeleteAsync(DeleteSnapshotsOption.None, new BlobRequestConditions { IfMatch = new ETag(etag) }, ct);
            return true;
        }
        catch (RequestFailedException e) when (e.Status == 404) { return true; }
        catch (RequestFailedException e) when (e.Status == 412) { return false; }
    }
}

// Every machine an account has had, so its devices can show one that is offline, with when it
// was last seen, and a person can forget one that is gone (docs/protocol/roster.md).
//
// Who may change it is fixed by the relay, never by a message: a host is recorded as itself
// (the device id it proved and the name it gave) when it is admitted and when it leaves, and
// nothing else adds a machine. The one write a device can ask for is forget, from a person's
// device (RelayHub). The account is always the one the connection's token proved, so no
// device ever reads or changes another account's roster.
//
// Held in memory while any device of the account is connected, and written behind, at most
// once per WriteInterval per account. Storage is named by an HMAC of the account id under a
// key derived from the token key, so a stored name says nothing about whose it is, and logs and
// metrics carry no account, device or name.
public sealed class MachineRoster
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly RosterOptions o;
    readonly IRosterStore? store;
    readonly RelayMetrics metrics;
    readonly ILogger<MachineRoster> log;
    readonly byte[] nameKey;
    readonly ConcurrentDictionary<string, AccountRoster> accounts = new();

    sealed class AccountRoster(string name)
    {
        public readonly string Name = name;
        public readonly SemaphoreSlim Io = new(1, 1);
        public readonly Dictionary<string, RosterMachine> Machines = new(StringComparer.Ordinal);
        public readonly Dictionary<string, DateTimeOffset> Forgotten = new(StringComparer.Ordinal);
        // Online hosts, by device id: a replaced connection and its replacement overlap.
        public readonly Dictionary<string, int> Online = new(StringComparer.Ordinal);
        public readonly Queue<long> Forgets = new();
        public int Connections;
        public bool Loaded;
        public string? ETag;
        // Each machine as the stored copy this relay last read or wrote had it.
        public Dictionary<string, DateTimeOffset> Base = new(StringComparer.Ordinal);
        public bool Dirty;
        public DateTimeOffset WrittenAt = DateTimeOffset.MinValue;
        public DateTimeOffset RefreshedAt;
        // When a loaded roster is next read again for what another relay wrote meanwhile.
        public DateTimeOffset ReadAgainAt;
        public bool Evicted;
    }

    public MachineRoster(RelayOptions options, RelayMetrics metrics, ILogger<MachineRoster> log, IRosterStore? store)
    {
        o = options.Roster;
        this.metrics = metrics;
        this.log = log;
        this.store = store;
        if (store is not null and not MemoryRosterStore && string.IsNullOrEmpty(options.TokenKey))
        {
            // Names from a per-process key could never be found again after a restart.
            throw new InvalidOperationException("Relay:TokenKey is required for a stored roster");
        }
        var root = string.IsNullOrEmpty(options.TokenKey) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(options.TokenKey);
        nameKey = HMACSHA256.HashData(root, "agent-bridge roster name v1"u8);
    }

    public bool Enabled => store is not null;

    public static IRosterStore? CreateStore(RosterOptions o) => o.Store ?? o.Backend.ToLowerInvariant() switch
    {
        "" or "memory" => new MemoryRosterStore(),
        "off" or "none" => null,
        "azure" => new AzureRosterStore(o),
        var other => throw new InvalidOperationException($"Relay:Roster:Backend {other} is not one of azure, memory, off or empty"),
    };

    DateTimeOffset Now => o.Time.GetUtcNow();

    // The name an account's roster is stored under.
    public string NameOf(string accountId) =>
        "v1/" + Convert.ToHexString(HMACSHA256.HashData(nameKey, Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant() + ".json";

    AccountRoster Hold(string accountId, Action<AccountRoster> change)
    {
        while (true)
        {
            var a = accounts.GetOrAdd(accountId, id => new AccountRoster(NameOf(id)) { RefreshedAt = Now });
            lock (a)
            {
                if (a.Evicted) { continue; }
                change(a);
                return a;
            }
        }
    }

    // An admitted device; a host is recorded as itself. Waits (briefly) for the stored roster,
    // so the first presence an account's device sees already lists its offline machines.
    public async Task ConnectedAsync(string accountId, string device, string name, string kind)
    {
        if (!Enabled) { return; }
        var a = Hold(accountId, r =>
        {
            r.Connections++;
            if (kind != "host") { return; }
            r.Online[device] = r.Online.GetValueOrDefault(device) + 1;
            r.Machines[device] = new RosterMachine(device, name, kind, Now);
            // A machine that comes back after being forgotten is the account's again.
            r.Forgotten.Remove(device);
            r.Dirty = true;
            Cap(r);
        });
        if (a.Loaded) { return; }
        using var cts = new CancellationTokenSource(o.StoreTimeout);
        try
        {
            await a.Io.WaitAsync(cts.Token);
            try { if (!a.Loaded) { await LoadAsync(a, cts.Token); } }
            finally { a.Io.Release(); }
        }
        // Admission goes on without it; the writer reads it again before it writes anything.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            metrics.Roster("load", e is OperationCanceledException ? "timeout" : "error");
            log.LogWarning("roster load failed: {Type}", e.GetType().Name);
        }
    }

    public void Disconnected(string accountId, string device, string kind)
    {
        if (!Enabled || !accounts.TryGetValue(accountId, out var a)) { return; }
        lock (a)
        {
            a.Connections--;
            if (kind != "host" || !a.Online.TryGetValue(device, out var n)) { return; }
            if (n > 1) { a.Online[device] = n - 1; return; }
            a.Online.Remove(device);
            if (a.Machines.TryGetValue(device, out var m)) { a.Machines[device] = m with { LastSeen = Now }; a.Dirty = true; }
        }
    }

    // The account's machines that are not online, most recently seen first. Null without a
    // roster, and while the account's stored roster has not been read (the store was down at
    // admission): presence then says nothing, rather than an empty list a device would take as
    // every offline machine forgotten.
    public IReadOnlyList<RosterMachine>? Known(string accountId, Func<string, bool> online)
    {
        if (!Enabled || !accounts.TryGetValue(accountId, out var a)) { return null; }
        lock (a)
        {
            if (!a.Loaded) { return null; }
            return [.. a.Machines.Values.Where(m => !online(m.Id) && !a.Online.ContainsKey(m.Id)).OrderByDescending(m => m.LastSeen)];
        }
    }

    // A person's device forgetting a machine that is gone: null when forgotten, or why not.
    // Who may ask is the hub's to decide; this checks only what the roster knows.
    public string? Forget(string accountId, string? machine, Func<string, bool> online)
    {
        if (!Enabled) { return Outcome("disabled"); }
        if (!Wire.IsDeviceId(machine)) { return Outcome("bad-request"); }
        var id = machine!.ToLowerInvariant();
        if (!accounts.TryGetValue(accountId, out var a)) { return Outcome("not-found"); }
        lock (a)
        {
            var now = Environment.TickCount64;
            while (a.Forgets.Count > 0 && now - a.Forgets.Peek() > 60_000) { a.Forgets.Dequeue(); }
            if (a.Forgets.Count >= o.MaxForgetsPerMinute) { return Outcome("rate-limited"); }
            a.Forgets.Enqueue(now);
            if (!a.Loaded) { return Outcome("unavailable"); }
            if (online(id) || a.Online.ContainsKey(id)) { return Outcome("online"); }
            if (!a.Machines.Remove(id)) { return Outcome("not-found"); }
            a.Forgotten[id] = Now;
            a.Dirty = true;
            Cap(a);
        }
        metrics.Roster("forget", "ok");
        return null;

        string Outcome(string why)
        {
            metrics.Roster("forget", why);
            return why;
        }
    }

    // One pass of the writer: an account whose stored roster could not be read is read again;
    // every account due a write is written; online hosts are brought up to date every
    // RefreshInterval; and an account with no device left is let go once it is written.
    // all: write everything now, whatever the interval (shutdown). Accounts go in parallel, a
    // few at a time, each call to the store bounded by StoreTimeout.
    public async Task FlushAsync(bool all = false, CancellationToken ct = default)
    {
        if (!Enabled) { return; }
        await Parallel.ForEachAsync(accounts, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (entry, token) =>
        {
            var (id, a) = entry;
            var now = Now;
            bool due, unread;
            lock (a)
            {
                if (a.Online.Count > 0 && (all || now - a.RefreshedAt >= o.RefreshInterval))
                {
                    foreach (var device in a.Online.Keys)
                    {
                        if (a.Machines.TryGetValue(device, out var m)) { a.Machines[device] = m with { LastSeen = now }; }
                    }
                    a.RefreshedAt = now;
                    a.Dirty = true;
                }
                due = a.Dirty && (all || now - a.WrittenAt >= o.WriteInterval);
                // Unread (the store was down at admission), or read a while ago: a relay handing
                // over in a deploy writes its last changes after this one has read, and only the
                // devices of an account with no host here would otherwise never hear of them.
                unread = a.Connections > 0 && !all && (!a.Loaded || now >= a.ReadAgainAt);
            }
            if (due) { await WriteAsync(id, a, token); }
            // A device that only reads, admitted while the store was down, still gets its list.
            else if (unread) { await ReadAgainAsync(id, a, token); }
            lock (a)
            {
                // Not before the write interval is up, nor while its forgets still count against
                // the minute: letting go sooner would let a host that keeps reconnecting, alone on
                // its account, write every pass, or a device reconnect for a fresh allowance.
                if (a.Connections <= 0 && !a.Dirty && !a.Evicted && Now - a.WrittenAt >= o.WriteInterval
                    && (a.Forgets.Count == 0 || Environment.TickCount64 - a.Forgets.Max() > 60_000))
                {
                    a.Evicted = true;
                    accounts.TryRemove(new KeyValuePair<string, AccountRoster>(id, a));
                }
            }
        });
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled) { return; }
        using var timer = new PeriodicTimer(o.FlushEvery, o.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await FlushAsync(false, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("roster flush failed: {Type}", e.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Raised with an account id when what its devices should be told changed other than by its
    // own devices: a stored roster read late. The hub sends that account presence again.
    public Func<string, Task>? Changed { get; set; }

    async Task ReadAgainAsync(string accountId, AccountRoster a, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        cts.CancelAfter(o.StoreTimeout);
        bool changed;
        try
        {
            await a.Io.WaitAsync(cts.Token);
            try { changed = await LoadAsync(a, cts.Token); }
            finally { a.Io.Release(); }
        }
        catch (Exception e) when (e is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            metrics.Roster("load", e is OperationCanceledException ? "timeout" : "error");
            log.LogWarning("roster load failed: {Type}", e.GetType().Name);
            return;
        }
        if (changed) { await AnnounceAsync(accountId); }
    }

    async Task AnnounceAsync(string accountId)
    {
        if (Changed is not { } changed) { return; }
        try { await changed(accountId); }
        catch (Exception e) when (e is not OutOfMemoryException) { log.LogWarning("roster announcement failed: {Type}", e.GetType().Name); }
    }

    async Task WriteAsync(string accountId, AccountRoster a, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        cts.CancelAfter(o.StoreTimeout);
        var ct = cts.Token;
        try { await a.Io.WaitAsync(ct); }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return; }
        var announce = false;
        try
        {
            // A conflict means another relay wrote since this one read: read, merge, try again.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!a.Loaded) { announce |= await LoadAsync(a, ct); }
                RosterDocument doc;
                string? etag;
                lock (a)
                {
                    Prune(a);
                    doc = new RosterDocument { Machines = [.. a.Machines.Values.OrderByDescending(m => m.LastSeen)], Forgotten = new(a.Forgotten) };
                    etag = a.ETag;
                    a.Dirty = false;
                    a.WrittenAt = Now;
                }
                string? written;
                bool ok;
                if (doc.Machines.Count == 0 && doc.Forgotten.Count == 0)
                {
                    // Nothing to remember: nothing is kept.
                    ok = etag is null || await store!.DeleteAsync(a.Name, etag, ct);
                    written = null;
                }
                else
                {
                    written = await store!.WriteAsync(a.Name, JsonSerializer.SerializeToUtf8Bytes(doc, Json), etag, ct);
                    ok = written is not null;
                }
                if (ok)
                {
                    lock (a)
                    {
                        a.ETag = written;
                        // What storage holds now: the base of the next merge.
                        a.Base = doc.Machines.ToDictionary(m => m.Id, m => m.LastSeen, StringComparer.Ordinal);
                    }
                    metrics.Roster("write", "ok");
                    return;
                }
                metrics.Roster("write", "conflict");
                lock (a) { a.Loaded = false; a.Dirty = true; }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            metrics.Roster("write", e is OperationCanceledException ? "timeout" : "error");
            log.LogWarning("roster write failed: {Type}", e.GetType().Name);
            lock (a) { a.Dirty = true; }
        }
        // Stopping: what this write was taking to storage is still to be written, by the final
        // flush (RosterWriter.StopAsync).
        catch (OperationCanceledException)
        {
            lock (a) { a.Dirty = true; }
            throw;
        }
        finally
        {
            a.Io.Release();
            if (announce) { await AnnounceAsync(accountId); }
        }
    }

    // Under Io. Merges the stored copy into what is held, against the copy this relay last read
    // or wrote (the base), as a three-way merge:
    //  * a machine both have: the later sighting;
    //  * one only storage has: added, unless this relay had it in the base and has since let it
    //    go (forgotten or capped here), with no newer sighting since;
    //  * one only this relay has: kept, unless it was in the base and storage no longer has it
    //    (another relay forgot or capped it) and this relay has not seen it since;
    //  * a forget, kept (the later of two), removes any machine not seen after it.
    // So a relay holding an old copy never brings back a machine another forgot, however many
    // forgets have been made since; the forgets themselves only settle a forget and a sighting
    // made on two relays at once, and are kept for ForgottenFor.
    // True when what the account's devices are told (the offline machines) changed.
    async Task<bool> LoadAsync(AccountRoster a, CancellationToken ct)
    {
        var stored = await store!.ReadAsync(a.Name, ct);
        RosterDocument? doc = null;
        if (stored is { Body.Length: > 0 })
        {
            // A copy that cannot be read is replaced by the next write, not trusted.
            try { doc = JsonSerializer.Deserialize<RosterDocument>(stored.Body, Json); }
            catch (JsonException) { metrics.Roster("load", "corrupt"); }
        }
        bool changed;
        lock (a)
        {
            var shown = Shown(a);
            a.ETag = stored?.ETag;
            // Soon after the first read, for a relay handing over; then now and then.
            a.ReadAgainAt = Now + (a.Loaded ? o.RefreshInterval : o.ReadAgainAfter);
            a.Loaded = true;
            var theirs = (doc?.Machines ?? [])
                .Where(m => Wire.IsDeviceId(m.Id) && m.Name is not null)
                .GroupBy(m => m.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.MaxBy(m => m.LastSeen)!, StringComparer.Ordinal);
            foreach (var (id, at) in doc?.Forgotten ?? [])
            {
                if (!Wire.IsDeviceId(id)) { continue; }
                if (!a.Forgotten.TryGetValue(id, out var mine) || at > mine) { a.Forgotten[id] = at; }
            }
            foreach (var (id, m) in theirs)
            {
                // An online host's own record is newer than any stored one.
                if (a.Online.ContainsKey(id)) { continue; }
                if (a.Machines.TryGetValue(id, out var mine))
                {
                    if (m.LastSeen > mine.LastSeen) { a.Machines[id] = new RosterMachine(id, Clean(m.Name), "host", m.LastSeen); }
                }
                else if (!a.Base.TryGetValue(id, out var based) || m.LastSeen > based)
                {
                    a.Machines[id] = new RosterMachine(id, Clean(m.Name), "host", m.LastSeen);
                }
            }
            foreach (var (id, m) in a.Machines.ToList())
            {
                if (a.Online.ContainsKey(id) || theirs.ContainsKey(id)) { continue; }
                if (a.Base.TryGetValue(id, out var based) && m.LastSeen <= based) { a.Machines.Remove(id); }
            }
            foreach (var (id, at) in a.Forgotten)
            {
                if (!a.Online.ContainsKey(id) && a.Machines.TryGetValue(id, out var m) && m.LastSeen <= at) { a.Machines.Remove(id); }
            }
            Prune(a);
            a.Base = theirs.ToDictionary(t => t.Key, t => t.Value.LastSeen, StringComparer.Ordinal);
            // What this relay held that storage did not have has to reach it; a device that only
            // reads (a phone opening the app) writes nothing.
            a.Dirty = a.Dirty || Signature(a) != StoredSignature(doc);
            changed = Shown(a) != shown;
        }
        metrics.Roster("load", "ok");
        return changed;
    }

    // What the account's devices are told: its offline machines, as known.
    static string Shown(AccountRoster a) => string.Join('|',
        a.Machines.Values.Where(m => !a.Online.ContainsKey(m.Id)).OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => $"{m.Id} {m.Name} {m.LastSeen.UtcTicks}"));

    // Under the lock: a forget superseded by a later sighting, or older than ForgottenFor, goes;
    // then the caps.
    void Prune(AccountRoster a)
    {
        var horizon = Now - o.ForgottenFor;
        foreach (var (id, at) in a.Forgotten.ToList())
        {
            if (at < horizon || (a.Machines.TryGetValue(id, out var m) && m.LastSeen > at)) { a.Forgotten.Remove(id); }
        }
        Cap(a);
    }

    // The oldest offline machines past MaxMachines go. Forgets past MaxForgotten go oldest first:
    // the base already keeps a stale copy from bringing a machine back, and at MaxForgetsPerMinute
    // the oldest of them is far older than two relays ever overlap.
    void Cap(AccountRoster a)
    {
        if (a.Machines.Count > o.MaxMachines)
        {
            foreach (var m in a.Machines.Values.Where(m => !a.Online.ContainsKey(m.Id)).OrderBy(m => m.LastSeen).Take(a.Machines.Count - o.MaxMachines).ToList())
            {
                a.Machines.Remove(m.Id);
            }
        }
        if (a.Forgotten.Count > o.MaxForgotten)
        {
            foreach (var (id, _) in a.Forgotten.OrderBy(f => f.Value).Take(a.Forgotten.Count - o.MaxForgotten).ToList()) { a.Forgotten.Remove(id); }
        }
    }

    static string Signature(AccountRoster a) => string.Join('|',
        a.Machines.Values.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => $"{m.Id} {m.Name} {m.LastSeen.UtcTicks}")
            .Concat(a.Forgotten.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"-{f.Key} {f.Value.UtcTicks}")));

    static string StoredSignature(RosterDocument? doc) => doc is null ? "" : string.Join('|',
        doc.Machines.Where(m => Wire.IsDeviceId(m.Id) && m.Name is not null).OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => $"{m.Id} {Clean(m.Name)} {m.LastSeen.UtcTicks}")
            .Concat(doc.Forgotten.Where(f => Wire.IsDeviceId(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"-{f.Key} {f.Value.UtcTicks}")));

    static string Clean(string name)
    {
        var s = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > 64 ? s[..64] : s.Length == 0 ? "unnamed" : s;
    }
}

// Runs the roster's writer for the life of the app, and writes everything left when the app
// stops: after the hub has drained (ApplicationStopping), with every host online here marked
// seen now, so the next relay says when each was last here. Awaited by the host, inside its
// shutdown timeout (RelayApp), and after the periodic writer has stopped, so the two never
// write at once.
public sealed class RosterWriter(MachineRoster roster, ILogger<RosterWriter> log) : BackgroundService
{
    public static readonly TimeSpan FinalFlush = TimeSpan.FromSeconds(10);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => roster.RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FinalFlush);
        try { await roster.FlushAsync(all: true, cts.Token); }
        catch (Exception e) when (e is not OutOfMemoryException) { log.LogWarning("roster flush at shutdown failed: {Type}", e.GetType().Name); }
    }
}
