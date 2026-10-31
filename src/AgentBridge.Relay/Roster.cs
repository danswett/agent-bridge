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
    // How often the writer looks for something to write.
    public TimeSpan FlushEvery { get; set; } = TimeSpan.FromSeconds(5);
    // How long a forget is remembered against a stale copy (another replica's, mid-deploy)
    // bringing the machine back.
    public TimeSpan ForgottenFor { get; set; } = TimeSpan.FromDays(30);
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
            var r = await container.GetBlobClient(name).DownloadContentAsync(ct);
            var body = r.Value.Details.ContentLength > MaxBytes ? [] : r.Value.Content.ToArray();
            return new StoredRoster(body, r.Value.Details.ETag.ToString());
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
        public bool Dirty;
        public DateTimeOffset WrittenAt = DateTimeOffset.MinValue;
        public DateTimeOffset RefreshedAt;
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

    // The account's machines that are not online, most recently seen first; null without a
    // roster, so presence then says nothing about it.
    public IReadOnlyList<RosterMachine>? Known(string accountId, Func<string, bool> online)
    {
        if (!Enabled) { return null; }
        if (!accounts.TryGetValue(accountId, out var a)) { return []; }
        lock (a)
        {
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

    // One pass of the writer: every account due a write is written, online hosts are brought
    // up to date every RefreshInterval, and an account with no device left is let go once it is
    // written. all: write everything due now, whatever the interval (a drain).
    public async Task FlushAsync(bool all = false, CancellationToken ct = default)
    {
        if (!Enabled) { return; }
        var now = Now;
        foreach (var (id, a) in accounts)
        {
            bool due;
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
            }
            if (due) { await WriteAsync(a, ct); }
            lock (a)
            {
                // Not before the write interval is up: letting go sooner would let a host that
                // keeps reconnecting, alone on its account, write every pass.
                if (a.Connections <= 0 && !a.Dirty && !a.Evicted && Now - a.WrittenAt >= o.WriteInterval)
                {
                    a.Evicted = true;
                    accounts.TryRemove(new KeyValuePair<string, AccountRoster>(id, a));
                }
            }
        }
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

    async Task WriteAsync(AccountRoster a, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        cts.CancelAfter(o.StoreTimeout);
        var ct = cts.Token;
        try { await a.Io.WaitAsync(ct); }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return; }
        try
        {
            // A conflict means another relay wrote since this one read: read, merge, try again.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!a.Loaded) { await LoadAsync(a, ct); }
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
                    // Nothing to remember: nothing is kept. A forget is kept until ForgottenFor
                    // has passed, so a stale copy cannot bring the machine back meanwhile.
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
                    lock (a) { a.ETag = written; }
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
        finally { a.Io.Release(); }
    }

    // Under Io. Merges the stored copy into what is held: per machine, the later sighting; per
    // forget, the later one; a machine shows only if seen after it was forgotten.
    async Task LoadAsync(AccountRoster a, CancellationToken ct)
    {
        var stored = await store!.ReadAsync(a.Name, ct);
        RosterDocument? doc = null;
        if (stored is { Body.Length: > 0 })
        {
            // A copy that cannot be read is replaced by the next write, not trusted.
            try { doc = JsonSerializer.Deserialize<RosterDocument>(stored.Body, Json); }
            catch (JsonException) { metrics.Roster("load", "corrupt"); }
        }
        lock (a)
        {
            a.ETag = stored?.ETag;
            a.Loaded = true;
            foreach (var (id, at) in doc?.Forgotten ?? [])
            {
                if (!Wire.IsDeviceId(id)) { continue; }
                if (!a.Forgotten.TryGetValue(id, out var mine) || at > mine) { a.Forgotten[id] = at; }
            }
            foreach (var m in doc?.Machines ?? [])
            {
                if (!Wire.IsDeviceId(m.Id) || m.Name is null) { continue; }
                // An online host's own record is newer than any stored one.
                if (a.Online.ContainsKey(m.Id)) { continue; }
                if (!a.Machines.TryGetValue(m.Id, out var mine) || m.LastSeen > mine.LastSeen)
                {
                    a.Machines[m.Id] = new RosterMachine(m.Id, Clean(m.Name), "host", m.LastSeen);
                }
            }
            foreach (var (id, at) in a.Forgotten)
            {
                if (!a.Online.ContainsKey(id) && a.Machines.TryGetValue(id, out var m) && m.LastSeen <= at) { a.Machines.Remove(id); }
            }
            Prune(a);
            // What this relay held that storage did not have has to reach it; a device that only
            // reads (a phone opening the app) writes nothing.
            a.Dirty = a.Dirty || Signature(a) != StoredSignature(doc);
        }
        metrics.Roster("load", "ok");
    }

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

    void Cap(AccountRoster a)
    {
        if (a.Machines.Count > o.MaxMachines)
        {
            foreach (var m in a.Machines.Values.Where(m => !a.Online.ContainsKey(m.Id)).OrderBy(m => m.LastSeen).Take(a.Machines.Count - o.MaxMachines).ToList())
            {
                a.Machines.Remove(m.Id);
            }
        }
        if (a.Forgotten.Count > o.MaxMachines)
        {
            foreach (var (id, _) in a.Forgotten.OrderBy(f => f.Value).Take(a.Forgotten.Count - o.MaxMachines).ToList()) { a.Forgotten.Remove(id); }
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

// Runs the roster's writer for the life of the app; a drain writes what is left (RelayHub).
public sealed class RosterWriter(MachineRoster roster) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => roster.RunAsync(stoppingToken);
}
