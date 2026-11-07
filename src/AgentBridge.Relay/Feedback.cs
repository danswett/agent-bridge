using System.Collections.Concurrent;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Relay;

public sealed class FeedbackOptions
{
    // "" (off), "azure" (the feedback container of the blob store's storage account, as the
    // relay's managed identity) or "local" (in memory, tests and dev stacks only).
    public string Backend { get; set; } = "";
    // https://<account>.blob.core.windows.net. No key or SAS, as for Relay:Blobs.
    public string? AccountUrl { get; set; }
    // Its own container: the blob container's one-day lifecycle rule must never reach it, and
    // the feedback lead's read access must never reach attachments.
    public string Container { get; set; } = "feedback";
    public string? ManagedIdentityClientId { get; set; }
    // Stored bytes per bundle (a bucket size). A sealed bundle is text, a screenshot and some logs.
    public long MaxBytes { get; set; } = 8L * 1024 * 1024;
    public int MaxPutsPerMinute { get; set; } = 3;
    public int MaxPutsPerAccountPerDay { get; set; } = 10;
    public long MaxBytesPerAccountPerDay { get; set; } = 40L * 1024 * 1024;
    // The relay takes the upload itself (no storage URL goes to a device), so it can hold the
    // size to exactly what was asked for; short anyway, since the ticket is a bearer.
    public TimeSpan UrlLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeProvider Time { get; set; } = TimeProvider.System;
}

// The relay's half of beta feedback (docs/protocol/feedback.md): a device asks for a URL to
// upload one bundle it has already sealed to the feedback public key, so the relay and storage
// only ever hold ciphertext. The device sends the bytes to the relay (PUT /v1/feedback/<ticket>),
// which checks them against the size it ticketed and creates one blob, never overwriting. The
// relay's identity can only create there: the role assignment forbids read, list and delete
// (infra/modules/blob-store.bicep), so even a compromised relay cannot read feedback back. Nothing
// in the stored name, the logs or the metrics identifies an account or a device.
public sealed class FeedbackService
{
    readonly FeedbackOptions o;
    readonly IFeedbackSink? backend;
    readonly byte[] ticketKey;
    readonly RelayMetrics metrics;
    readonly ILogger<FeedbackService> log;
    readonly ConcurrentDictionary<string, Usage> usage = new();

    sealed class Usage
    {
        public readonly Queue<(long At, long Bytes)> Day = new();
        public readonly Queue<long> Minute = new();
        public long DayBytes;
    }

    public FeedbackService(FeedbackOptions options, RelayMetrics metrics, ILogger<FeedbackService> log, IFeedbackSink? backend, string? tokenKey = null)
    {
        o = options;
        this.metrics = metrics;
        this.log = log;
        this.backend = backend;
        var root = string.IsNullOrEmpty(tokenKey) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(tokenKey);
        ticketKey = HMACSHA256.HashData(root, "agent-bridge feedback ticket v1"u8);
    }

    public bool Enabled => backend is not null;

    public IFeedbackSink? Sink => backend;

    // What `ready` tells a device: the largest bundle it may upload, in stored bytes.
    public object? Advertised => Enabled ? new { max = o.MaxBytes } : null;

    public async Task<object> HandleAsync(Account account, JsonElement m)
    {
        var id = m.TryGetProperty("id", out var i) && i.TryGetInt64(out var n) ? n : 0;
        if (backend is null) { return Refuse(id, "disabled"); }
        if (!m.TryGetProperty("size", out var s) || !s.TryGetInt64(out var size) || size <= 0) { return Refuse(id, "bad-request"); }
        if (size > o.MaxBytes) { return Refuse(id, "too-large"); }
        if (!BlobFormat.IsBucket(size)) { return Refuse(id, "bad-size"); }
        if (Charge(account.Id, size) is { } why) { return Refuse(id, why); }
        try
        {
            var expires = o.Time.GetUtcNow() + o.UrlLifetime;
            var name = BlobFormat.Base64Url(RandomNumberGenerator.GetBytes(BlobFormat.IdBytes));
            var payload = $"{name}.{size}.{expires.ToUnixTimeSeconds()}";
            metrics.Blob("feedback", "ok", 0);
            log.LogInformation("feedback put: {Bytes} bytes", size);
            return new { t = "feedback-result", id, url = $"{PathPrefix}{payload}.{Sign(payload)}", expires };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("feedback ticket failed: {Type}", e.GetType().Name);
            return Refuse(id, "unavailable");
        }
    }

    public const string PathPrefix = "/v1/feedback/";

    string Sign(string payload) => BlobFormat.Base64Url(HMACSHA256.HashData(ticketKey, Encoding.ASCII.GetBytes(payload)));

    // The upload itself: 201 stored, 403 bad or expired ticket, 400 wrong length, 409 already
    // used, 413 more than ticketed, 503 the store failed.
    public async Task<int> UploadAsync(string ticket, Stream body, CancellationToken ct)
    {
        var parts = ticket.Split('.');
        if (backend is null || parts.Length != 4 || ticket.Length > 200
            || !long.TryParse(parts[1], out var size) || !long.TryParse(parts[2], out var exp)) { return 403; }
        var given = Encoding.ASCII.GetBytes(parts[3]);
        var want = Encoding.ASCII.GetBytes(Sign($"{parts[0]}.{parts[1]}.{parts[2]}"));
        if (!CryptographicOperations.FixedTimeEquals(given, want) || size <= 0 || size > o.MaxBytes || o.Time.GetUtcNow().ToUnixTimeSeconds() > exp) { return 403; }
        var buffer = new byte[size + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(read), ct)) > 0) { read += n; }
        if (read > size) { return 413; }
        if (read != size) { return 400; }
        try { return await backend.CreateAsync(parts[0], buffer.AsMemory(0, (int)size), ct) ? 201 : 409; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("feedback store failed: {Type}", e.GetType().Name);
            return 503;
        }
    }

    object Refuse(long id, string error)
    {
        metrics.Blob("feedback", error, 0);
        return new { t = "feedback-result", id, error };
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
            if (u.Day.Count >= o.MaxPutsPerAccountPerDay || u.DayBytes + size > o.MaxBytesPerAccountPerDay) { return "quota"; }
            u.Minute.Enqueue(now);
            u.Day.Enqueue((now, size));
            u.DayBytes += size;
            return null;
        }
    }

    public static IFeedbackSink? CreateBackend(FeedbackOptions o) => o.Backend.ToLowerInvariant() switch
    {
        "" or "none" or "off" => null,
        "azure" => new AzureFeedbackSink(o),
        "local" => new LocalFeedbackSink(),
        var other => throw new InvalidOperationException($"Relay:Feedback:Backend {other} is not one of azure, local or empty"),
    };
}

// Create-only: there is deliberately no read, list or delete here.
public interface IFeedbackSink
{
    // False when the name already exists; the existing blob is never replaced.
    Task<bool> CreateAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken ct);
}

public sealed class AzureFeedbackSink : IFeedbackSink
{
    readonly BlobContainerClient container;

    public AzureFeedbackSink(FeedbackOptions o)
    {
        TokenCredential credential = string.IsNullOrEmpty(o.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId));
        container = new BlobServiceClient(new Uri(o.AccountUrl ?? throw new InvalidOperationException("Relay:Feedback:AccountUrl is required for the azure backend")), credential).GetBlobContainerClient(o.Container);
    }

    public async Task<bool> CreateAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        try
        {
            await container.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
            return true;
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412) { return false; }
    }
}

public sealed class LocalFeedbackSink : IFeedbackSink
{
    readonly ConcurrentDictionary<string, byte[]> blobs = new();

    public IReadOnlyDictionary<string, byte[]> Stored => blobs;

    public Task<bool> CreateAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken ct) => Task.FromResult(blobs.TryAdd(name, bytes.ToArray()));
}
