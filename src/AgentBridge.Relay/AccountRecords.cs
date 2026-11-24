using System.Buffers.Binary;
using Azure;
using Azure.Identity;

namespace AgentBridge.Relay;

public interface IRecordAdmission
{
    Task<bool> AdmitAsync(string accountId, string deviceId, CancellationToken ct);
}

public sealed class DenyRecordAdmission : IRecordAdmission
{
    public Task<bool> AdmitAsync(string accountId, string deviceId, CancellationToken ct) => Task.FromResult(false);
}

// No socket or HTTP route calls this until record admission and the wire slice land.
public sealed class AccountRecordService(IRosterStore? store, IRecordAdmission admission,
    ILogger<AccountRecordService> log, TimeProvider? time = null)
{
    public const int MaxBodyBytes = 65536;
    const int HeaderBytes = 24;
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public sealed record Snapshot(long Version, int Size, DateTimeOffset Time, byte[] Body);
    public sealed record Result(string? Error, long Version = 0, Snapshot? Record = null);

    public Task<Result> GetAsync(string accountId, string deviceId, string kind, string id, CancellationToken ct = default) =>
        RunAsync(accountId, deviceId, kind, id, 0, async (storage, name, token) =>
        {
            var snapshot = Decode(await storage.ReadAsync(name, token));
            return snapshot is null ? new("not-found") : new(null, snapshot.Version, snapshot);
        }, ct);

    public Task<Result> PutAsync(string accountId, string deviceId, string kind, string id,
        long expectedVersion, byte[] body, CancellationToken ct = default)
    {
        if (expectedVersion < 0) { return Task.FromResult(new Result("bad-request")); }
        if (body.Length > MaxBodyBytes) { return Task.FromResult(new Result("too-large")); }
        var payload = body.ToArray();
        return RunAsync(accountId, deviceId, kind, id, payload.Length, async (storage, name, token) =>
        {
            var stored = await storage.ReadAsync(name, token);
            var current = Decode(stored);
            var version = current?.Version ?? 0;
            if (version != expectedVersion) { return new("conflict", version); }
            if (version == long.MaxValue) { return Unavailable(payload.Length); }
            var next = version + 1;
            var bytes = Encode(next, payload, clock.GetUtcNow());
            if (await storage.WriteAsync(name, bytes, stored?.ETag, token) is not null)
            {
                log.LogDebug("Account record stored ({Bytes} bytes)", payload.Length);
                return new(null, next);
            }
            // ETag CAS, not an in-process lock, arbitrates across relay replicas.
            var winner = Decode(await storage.ReadAsync(name, token));
            return new("conflict", winner?.Version ?? 0);
        }, ct);
    }

    async Task<Result> RunAsync(string accountId, string deviceId, string kind, string id, int bytes,
        Func<IRosterStore, string, CancellationToken, Task<Result>> operation, CancellationToken ct)
    {
        if (!ValidName(accountId, kind, id) || string.IsNullOrEmpty(deviceId)) { return new("bad-request"); }
        ct.ThrowIfCancellationRequested();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (!await admission.AdmitAsync(accountId, deviceId, timeout.Token)) { return new("needs-device-approval"); }
            if (store is null) { return Unavailable(bytes); }
            return await operation(store, $"records/{accountId}/{kind}/{id}", timeout.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested &&
            e is IOException or InvalidDataException or RequestFailedException or AuthenticationFailedException or HttpRequestException or OperationCanceledException or TimeoutException)
        {
            // Storage exception text can contain account names or URLs.
            return Unavailable(bytes);
        }
    }

    Result Unavailable(int bytes)
    {
        log.LogWarning("Account record operation unavailable ({Bytes} bytes)", bytes);
        return new("unavailable");
    }

    static bool ValidName(string account, string kind, string id) =>
        account is { Length: > 0 and <= 20 } && account.All(c => c is >= '0' and <= '9') &&
        kind is "order" or "marker" or "keywrap" or "manifest" or "fleet" &&
        id is { Length: > 0 and <= 32 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    static byte[] Encode(long version, byte[] body, DateTimeOffset at)
    {
        var bytes = new byte[HeaderBytes + body.Length];
        "ABR1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), body.Length);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16), at.ToUnixTimeMilliseconds());
        body.CopyTo(bytes, HeaderBytes);
        return bytes;
    }

    static Snapshot? Decode(StoredRoster? stored)
    {
        if (stored is null) { return null; }
        var bytes = stored.Body.AsSpan();
        if (bytes.Length < HeaderBytes || !bytes[..4].SequenceEqual("ABR1"u8)) { throw new InvalidDataException(); }
        var version = BinaryPrimitives.ReadInt64BigEndian(bytes[4..]);
        var size = BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
        var milliseconds = BinaryPrimitives.ReadInt64BigEndian(bytes[16..]);
        if (version <= 0 || size < 0 || size > MaxBodyBytes || bytes.Length != HeaderBytes + size ||
            milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            throw new InvalidDataException();
        }
        return new(version, size, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), bytes[HeaderBytes..].ToArray());
    }
}
