using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace AgentBridge.Relay;

// Account and device come from the authenticated connection, never a record's payload.
public interface IRecordAdmission
{
    Task<bool> AdmitAsync(string accountId, string deviceId, CancellationToken ct);
}

public sealed class DenyRecordAdmission : IRecordAdmission
{
    public Task<bool> AdmitAsync(string accountId, string deviceId, CancellationToken ct) => Task.FromResult(false);
}

public sealed class AccountRecordService(IRosterStore? store, IRecordAdmission admission, TimeProvider time, ILogger<AccountRecordService> log)
{
    public const int MaxBodyBytes = 65536;
    const int HeaderBytes = 24;
    public sealed record Result(string? Error, long Version = 0, byte[]? Body = null, DateTimeOffset? UpdatedAt = null);

    static bool ValidName(string accountId, string kind, string id) =>
        accountId is not null && id is not null &&
        Regex.IsMatch(accountId, @"\A[0-9]{1,20}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) &&
        kind is "order" or "marker" or "keywrap" or "manifest" or "fleet" &&
        Regex.IsMatch(id, @"\A[a-z0-9-]{1,32}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<Result> GetAsync(string accountId, string deviceId, string kind, string id, CancellationToken ct = default)
    {
        if (!ValidName(accountId, kind, id)) { return new("bad-request"); }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            if (!await admission.AdmitAsync(accountId, deviceId, linked.Token)) { return new("needs-device-approval"); }
            if (store is null) { return Unavailable(0); }
            return Decode(await store.ReadAsync($"records/{accountId}/{kind}/{id}", linked.Token));
        }
        catch (Exception e) when (StoreFailure(e, ct)) { return Unavailable(0); }
    }

    public async Task<Result> PutAsync(string accountId, string deviceId, string kind, string id, long expectedVersion, byte[] body, CancellationToken ct = default)
    {
        if (!ValidName(accountId, kind, id) || expectedVersion < 0) { return new("bad-request"); }
        if (body.Length > MaxBodyBytes) { return new("too-large"); }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            if (!await admission.AdmitAsync(accountId, deviceId, linked.Token)) { return new("needs-device-approval"); }
            if (store is null) { return Unavailable(body.Length); }
            var name = $"records/{accountId}/{kind}/{id}";
            var read = await store.ReadAsync(name, linked.Token);
            var current = Decode(read);
            if (current.Version != expectedVersion) { return new("conflict", current.Version); }
            if (current.Version == long.MaxValue) { return Unavailable(body.Length); }
            var version = current.Version + 1;
            var at = time.GetUtcNow().ToUnixTimeMilliseconds();
            var encoded = new byte[HeaderBytes + body.Length];
            "ABR1"u8.CopyTo(encoded);
            BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(4), version);
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(12), body.Length);
            BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(16), at);
            body.CopyTo(encoded, HeaderBytes);
            // Only the shared store's ETag arbitrates, including across relay replicas.
            if (await store.WriteAsync(name, encoded, read?.ETag, linked.Token) is null)
            {
                var latest = Decode(await store.ReadAsync(name, linked.Token));
                return new("conflict", latest.Version);
            }
            return new(null, version, UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(at));
        }
        catch (Exception e) when (StoreFailure(e, ct)) { return Unavailable(body.Length); }
    }

    static Result Decode(StoredRoster? read)
    {
        if (read is null) { return new("not-found"); }
        var bytes = read.Body.AsSpan();
        if (bytes.Length < HeaderBytes || bytes.Length > HeaderBytes + MaxBodyBytes ||
            !bytes[..4].SequenceEqual("ABR1"u8)) { throw new InvalidDataException("Invalid record header"); }
        var version = BinaryPrimitives.ReadInt64BigEndian(bytes[4..]);
        var size = BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
        var at = BinaryPrimitives.ReadInt64BigEndian(bytes[16..]);
        if (version < 1 || size != bytes.Length - HeaderBytes ||
            at < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || at > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        { throw new InvalidDataException("Invalid record metadata"); }
        return new(null, version, bytes[HeaderBytes..].ToArray(), DateTimeOffset.FromUnixTimeMilliseconds(at));
    }

    static bool StoreFailure(Exception e, CancellationToken ct) =>
        e is IOException or InvalidDataException or Azure.RequestFailedException or HttpRequestException or TimeoutException ||
        e is OperationCanceledException && !ct.IsCancellationRequested;

    Result Unavailable(int bytes)
    {
        // Store exceptions can contain blob names; do not attach them to logs.
        log.LogWarning("Account record operation unavailable ({Bytes} bytes)", bytes);
        return new("unavailable");
    }
}
