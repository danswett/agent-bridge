using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace AgentBridge.Relay;

public sealed class BlobOptions
{
    // "" (off: no blob store, and devices keep files under 256 KiB in their channels),
    // "azure" (Azure Blob Storage through the app's managed identity), or "local" (an
    // in-memory store the relay serves itself, for tests and dev stacks only; infra/ never
    // sets it, and it would put blob bytes through the relay, which §4.6 rules out).
    public string Backend { get; set; } = "";
    // https://<account>.blob.core.windows.net. No key or SAS: the relay signs user delegation
    // SAS with a key it gets from Entra as its managed identity.
    public string? AccountUrl { get; set; }
    public string Container { get; set; } = "blobs";
    // The user-assigned identity to sign in as. Unset: DefaultAzureCredential (a dev machine).
    public string? ManagedIdentityClientId { get; set; }
    // Stored bytes per blob: a 200 MiB session transfer (design §9) once sealed and padded.
    public long MaxBytes { get; set; } = 201L * 1024 * 1024;
    // Per account per rolling 24 hours, counted as blobs are issued, so a receiver deleting
    // each at once does not reset it. In memory per replica: with one replica (lean) that is
    // per account; with more, an account's home node will hold it (design §5.8).
    public long MaxBytesPerAccountPerDay { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxPutsPerMinute { get; set; } = 30;
    // How long a ticket can be read with. The container's lifecycle rule deletes every blob a
    // day after it was written whatever happens; infra/ keeps the two equal.
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(24);
    // How long one URL works. Long enough for 100 MB over a slow phone uplink.
    public TimeSpan UrlLifetime { get; set; } = TimeSpan.FromMinutes(30);
    // Test hook: the clock tickets and URLs expire by.
    public TimeProvider Time { get; set; } = TimeProvider.System;
}

// Where blob bytes live. The relay only ever issues URLs, checks sizes and deletes: bytes go
// straight between devices and storage.
public interface IBlobBackend
{
    // A URL to Put Blob once (create only, never overwrite), or to Get Blob.
    Task<string> WriteUrlAsync(string name, DateTimeOffset expires, CancellationToken ct);
    Task<string> ReadUrlAsync(string name, DateTimeOffset expires, CancellationToken ct);
    // Null when there is no such blob.
    Task<long?> SizeAsync(string name, CancellationToken ct);
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}

// Azure Blob Storage, as the relay's managed identity. Each URL is a user delegation SAS for
// one blob, one direction, HTTPS only, valid for minutes; the delegation key behind them comes
// from Entra and is cached for a few hours. Nothing secret is configured.
public sealed class AzureBlobBackend : IBlobBackend
{
    readonly BlobServiceClient service;
    readonly BlobContainerClient container;
    readonly TimeProvider time;
    readonly SemaphoreSlim keyLock = new(1, 1);
    UserDelegationKey? key;

    public AzureBlobBackend(BlobOptions o)
    {
        TokenCredential credential = string.IsNullOrEmpty(o.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId));
        service = new BlobServiceClient(new Uri(o.AccountUrl ?? throw new InvalidOperationException("Relay:Blobs:AccountUrl is required for the azure backend")), credential);
        container = service.GetBlobContainerClient(o.Container);
        time = o.Time;
    }

    public async Task<string> WriteUrlAsync(string name, DateTimeOffset expires, CancellationToken ct) =>
        Sas(container.GetBlobClient(name).Uri, container.Name, name, BlobSasPermissions.Create, expires, time.GetUtcNow(), await KeyAsync(ct), service.AccountName);

    public async Task<string> ReadUrlAsync(string name, DateTimeOffset expires, CancellationToken ct) =>
        Sas(container.GetBlobClient(name).Uri, container.Name, name, BlobSasPermissions.Read, expires, time.GetUtcNow(), await KeyAsync(ct), service.AccountName);

    // One blob (sr=b), one permission, HTTPS, from a few minutes ago (clock skew) to expires.
    public static string Sas(Uri blob, string containerName, string name, BlobSasPermissions permissions, DateTimeOffset expires, DateTimeOffset now, UserDelegationKey key, string accountName)
    {
        var b = new BlobSasBuilder(permissions, expires)
        {
            BlobContainerName = containerName,
            BlobName = name,
            Resource = "b",
            StartsOn = now - TimeSpan.FromMinutes(5),
            Protocol = SasProtocol.Https,
        };
        return new UriBuilder(blob) { Query = b.ToSasQueryParameters(key, accountName).ToString() }.Uri.ToString();
    }

    public async Task<long?> SizeAsync(string name, CancellationToken ct)
    {
        try { return (await container.GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength; }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct) =>
        (await container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: ct)).Value;

    async Task<UserDelegationKey> KeyAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        // Valid for a day, renewed with hours to spare, so a URL never outlives its key.
        if (key is { } k && k.SignedExpiresOn - now > TimeSpan.FromHours(12)) { return k; }
        await keyLock.WaitAsync(ct);
        try
        {
            if (key is { } again && again.SignedExpiresOn - now > TimeSpan.FromHours(12)) { return again; }
            key = (await service.GetUserDelegationKeyAsync(now - TimeSpan.FromMinutes(5), now + TimeSpan.FromDays(1), ct)).Value;
            return key;
        }
        finally { keyLock.Release(); }
    }
}

// Tests and dev stacks: blobs in this process's memory, served by the relay itself under
// /v1/dev-blobs with URLs it signs, speaking just enough of the Blob REST API (Put Blob with
// x-ms-blob-type, Get Blob) that clients use the same code against it as against Azure.
public sealed class LocalBlobBackend : IBlobBackend
{
    public const string PathPrefix = "/v1/dev-blobs/";
    readonly byte[] signingKey = RandomNumberGenerator.GetBytes(32);
    readonly ConcurrentDictionary<string, byte[]> blobs = new();

    public IReadOnlyDictionary<string, byte[]> Stored => blobs;

    public Task<string> WriteUrlAsync(string name, DateTimeOffset expires, CancellationToken ct) => Task.FromResult(Url(name, "c", expires));

    public Task<string> ReadUrlAsync(string name, DateTimeOffset expires, CancellationToken ct) => Task.FromResult(Url(name, "r", expires));

    public Task<long?> SizeAsync(string name, CancellationToken ct) => Task.FromResult(blobs.TryGetValue(name, out var b) ? (long?)b.Length : null);

    public Task<bool> DeleteAsync(string name, CancellationToken ct) => Task.FromResult(blobs.TryRemove(name, out _));

    string Url(string name, string permission, DateTimeOffset expires)
    {
        var se = expires.ToUnixTimeSeconds();
        return $"{PathPrefix}{name}?sp={permission}&se={se}&sig={Sign(name, permission, se)}";
    }

    string Sign(string name, string permission, long se) =>
        BlobFormat.Base64Url(HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes($"{permission}\n{name}\n{se}")));

    // Whether a request's query grants permission on name now.
    public bool Allows(string name, string permission, string? sp, string? se, string? sig, DateTimeOffset now) =>
        sp == permission && long.TryParse(se, out var expires) && expires >= now.ToUnixTimeSeconds()
        && sig is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(Sign(name, permission, expires)));

    // Create only, as a SAS with only "c" is: false if the blob is already there.
    public bool TryCreate(string name, byte[] bytes) => blobs.TryAdd(name, bytes);

    public byte[]? Read(string name) => blobs.TryGetValue(name, out var b) ? b : null;
}

// The relay's half of the blob store (docs/protocol/blobs.md): it hands a device a URL to
// upload one blob for another device of the same account, and that device a URL to read it,
// and deletes it for either. Who may do what is carried in the ticket the relay issues, an
// HMAC over the blob's random name, the account, the sender, the receiver, the stored size
// and the expiry, so any replica can check it with nothing stored. The relay never sees a key
// or a byte of content, and logs nothing that names an account, a device or a blob.
public sealed class BlobService
{
    readonly BlobOptions o;
    readonly IBlobBackend? backend;
    readonly RelayMetrics metrics;
    readonly ILogger<BlobService> log;
    readonly byte[] ticketKey;
    readonly ConcurrentDictionary<string, Usage> usage = new();

    sealed class Usage
    {
        public readonly Queue<(long At, long Bytes)> Day = new();
        public readonly Queue<long> Minute = new();
        public long DayBytes;
    }

    public BlobService(RelayOptions options, RelayMetrics metrics, ILogger<BlobService> log, IBlobBackend? backend = null)
    {
        o = options.Blobs;
        this.metrics = metrics;
        this.log = log;
        this.backend = backend;
        // Its own key, derived from the token key, so a ticket can never pass as a token or
        // the other way round. Without a token key, tickets last as long as the process.
        var root = string.IsNullOrEmpty(options.TokenKey) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(options.TokenKey);
        ticketKey = HMACSHA256.HashData(root, "agent-bridge blob ticket v1"u8);
    }

    public bool Enabled => backend is not null;

    public IBlobBackend? Backend => backend;

    // What `ready` tells a device: the largest blob it may put, in stored bytes.
    public object? Advertised => Enabled ? new { max = o.MaxBytes } : null;

    DateTimeOffset Now => o.Time.GetUtcNow();

    public sealed record Ticket(string Name, string From, string To, long Size, DateTimeOffset Expires);

    public string Issue(string accountId, Ticket t)
    {
        var body = BlobFormat.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { f = t.From, t = t.To, s = t.Size, x = t.Expires.ToUnixTimeSeconds() }));
        return $"{t.Name}.{body}.{Mac(t.Name, body, accountId)}";
    }

    // The ticket, if this relay issued it for this account; null otherwise, never a reason
    // that would tell another account's device anything.
    public Ticket? Read(string accountId, string? ticket)
    {
        var parts = ticket is { Length: <= 512 } ? ticket.Split('.') : null;
        if (parts is not { Length: 3 } || BlobFormat.IdOf(ticket) is null) { return null; }
        var expected = Encoding.ASCII.GetBytes(Mac(parts[0], parts[1], accountId));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2]))) { return null; }
        try
        {
            var j = JsonDocument.Parse(BlobFormat.FromBase64Url(parts[1]) ?? []).RootElement;
            return new Ticket(parts[0], j.GetProperty("f").GetString()!, j.GetProperty("t").GetString()!, j.GetProperty("s").GetInt64(),
                DateTimeOffset.FromUnixTimeSeconds(j.GetProperty("x").GetInt64()));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    string Mac(string name, string body, string accountId)
    {
        var data = Encoding.UTF8.GetBytes($"{name}.{body}\0{accountId}");
        return BlobFormat.Base64Url(HMACSHA256.HashData(ticketKey, data));
    }

    // One control message from an admitted device: its account and device id are the relay's,
    // proven at admission, never the message's.
    public async Task<object> HandleAsync(Account account, string device, string kind, JsonElement m)
    {
        var id = m.TryGetProperty("id", out var i) && i.TryGetInt64(out var n) ? n : 0;
        string op = kind switch { "blob-put" => "put", "blob-get" => "get", _ => "delete" };
        if (backend is null) { return Refuse(id, op, "disabled"); }
        try
        {
            return kind switch
            {
                "blob-put" => await PutAsync(id, account, device, m),
                "blob-get" => await GetAsync(id, account, device, m),
                _ => await DeleteAsync(id, account, device, m),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The type only: a storage exception's message can carry the blob's URL.
            log.LogWarning("blob {Op} failed in the store: {Type}", op, e.GetType().Name);
            return Refuse(id, op, "unavailable");
        }
    }

    async Task<object> PutAsync(long id, Account account, string device, JsonElement m)
    {
        var to = m.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (!Wire.IsDeviceId(to)) { return Refuse(id, "put", "bad-request"); }
        if (!m.TryGetProperty("size", out var s) || !s.TryGetInt64(out var size) || size <= 0) { return Refuse(id, "put", "bad-request"); }
        if (size > o.MaxBytes) { return Refuse(id, "put", "too-large"); }
        // Every blob is padded to a bucket, so its size says little; one that is not is refused
        // rather than stored at a telling size.
        if (!BlobFormat.IsBucket(size)) { return Refuse(id, "put", "bad-size"); }
        if (Charge(account.Id, size) is { } why) { return Refuse(id, "put", why); }
        var now = Now;
        var ticket = new Ticket(BlobFormat.Base64Url(RandomNumberGenerator.GetBytes(BlobFormat.IdBytes)), device, to!.ToLowerInvariant(), size, now + o.Lifetime);
        var urlExpires = now + o.UrlLifetime;
        var url = await backend!.WriteUrlAsync(ticket.Name, urlExpires, default);
        metrics.Blob("put", "ok", size);
        log.LogInformation("blob put: {Bytes} bytes", size);
        return new { t = "blob-result", id, blob = Issue(account.Id, ticket), url, expires = urlExpires };
    }

    async Task<object> GetAsync(long id, Account account, string device, JsonElement m)
    {
        var ticket = Read(account.Id, m.TryGetProperty("blob", out var b) ? b.GetString() : null);
        // Another account's ticket and another device's look the same: not found.
        if (ticket is null || ticket.To != device) { return Refuse(id, "get", "not-found"); }
        if (ticket.Expires < Now) { return Refuse(id, "get", "expired"); }
        var size = await backend!.SizeAsync(ticket.Name, default);
        if (size is null) { return Refuse(id, "get", "not-found"); }
        // A write URL cannot cap what is uploaded, so the size is checked here: a blob bigger
        // (or smaller) than the one ticketed is deleted, never handed on.
        if (size != ticket.Size)
        {
            await backend.DeleteAsync(ticket.Name, default);
            return Refuse(id, "get", "size-mismatch");
        }
        var urlExpires = Now + o.UrlLifetime;
        var url = await backend.ReadUrlAsync(ticket.Name, urlExpires, default);
        metrics.Blob("get", "ok", 0);
        return new { t = "blob-result", id, url, size, expires = urlExpires };
    }

    async Task<object> DeleteAsync(long id, Account account, string device, JsonElement m)
    {
        var ticket = Read(account.Id, m.TryGetProperty("blob", out var b) ? b.GetString() : null);
        if (ticket is null || (ticket.To != device && ticket.From != device)) { return Refuse(id, "delete", "not-found"); }
        var deleted = await backend!.DeleteAsync(ticket.Name, default);
        metrics.Blob("delete", deleted ? "ok" : "gone", 0);
        return new { t = "blob-result", id, deleted };
    }

    object Refuse(long id, string op, string error)
    {
        metrics.Blob(op, error, 0);
        return new { t = "blob-result", id, error };
    }

    // Rate and daily quota; null when the put fits.
    string? Charge(string accountId, long size)
    {
        var u = usage.GetOrAdd(accountId, _ => new Usage());
        lock (u)
        {
            var now = Environment.TickCount64;
            while (u.Minute.Count > 0 && now - u.Minute.Peek() > 60_000) { u.Minute.Dequeue(); }
            while (u.Day.Count > 0 && now - u.Day.Peek().At > 86_400_000) { u.DayBytes -= u.Day.Dequeue().Bytes; }
            if (u.Minute.Count >= o.MaxPutsPerMinute) { return "rate-limited"; }
            if (u.DayBytes + size > o.MaxBytesPerAccountPerDay) { return "quota"; }
            u.Minute.Enqueue(now);
            u.Day.Enqueue((now, size));
            u.DayBytes += size;
            return null;
        }
    }

    public static IBlobBackend? CreateBackend(BlobOptions o) => o.Backend.ToLowerInvariant() switch
    {
        "" or "none" or "off" => null,
        "azure" => new AzureBlobBackend(o),
        "local" => new LocalBlobBackend(),
        var other => throw new InvalidOperationException($"Relay:Blobs:Backend {other} is not one of azure, local or empty"),
    };
}
