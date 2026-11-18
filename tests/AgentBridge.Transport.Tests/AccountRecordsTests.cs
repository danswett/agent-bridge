using AgentBridge.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

public class AccountRecordsTests
{
    static readonly byte[] Ciphertext = [0xff, 0, 0x80, 0x7b];
    static readonly FakeTimeProvider Clock = new(DateTimeOffset.UnixEpoch);

    static AccountRecordService Make(IRosterStore? store, IRecordAdmission? admission = null) =>
        new(store, admission ?? new AllowRecords(), Clock, NullLogger<AccountRecordService>.Instance);

    sealed class AllowRecords : IRecordAdmission
    {
        public Task<bool> AdmitAsync(string accountId, string deviceId, CancellationToken ct) => Task.FromResult(true);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("marker")]
    [InlineData("keywrap")]
    [InlineData("manifest")]
    [InlineData("fleet")]
    public async Task Creating_then_reading_through_another_instance_preserves_opaque_bytes_and_metadata(string kind)
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        Assert.Equal("not-found", (await service.GetAsync("123", "device", kind, "main")).Error);
        var created = await service.PutAsync("123", "device", kind, "main", 0, Ciphertext);
        Assert.Null(created.Error);
        Assert.Equal(1, created.Version);
        var read = await Make(store).GetAsync("123", "device", kind, "main");
        Assert.Null(read.Error);
        Assert.Equal(created.Version, read.Version);
        Assert.Equal(Clock.GetUtcNow(), read.UpdatedAt);
        Assert.Equal(Ciphertext, read.Body);
        read.Body![0] = 0;
        Assert.Equal(Ciphertext, (await service.GetAsync("123", "device", kind, "main")).Body);
        Assert.Equal($"records/123/{kind}/main", store.Names.Single());
        Assert.Equal("not-found", (await service.GetAsync("456", "device", kind, "main")).Error);
    }

    [Fact]
    public async Task A_stale_version_conflicts_without_overwriting_and_successful_versions_increase()
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        await service.PutAsync("123", "device", "order", "main", 0, Ciphertext);
        var updated = await service.PutAsync("123", "device", "order", "main", 1, [7]);
        Assert.Null(updated.Error);
        Assert.Equal(2, updated.Version);
        var conflict = await service.PutAsync("123", "device", "order", "main", 1, [8]);
        Assert.Equal("conflict", conflict.Error);
        Assert.Equal(2, conflict.Version);
        Assert.Null(conflict.Body);
        Assert.Equal(new byte[] { 7 }, (await service.GetAsync("123", "device", "order", "main")).Body);
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task The_ciphertext_limit_includes_65536_bytes_but_refuses_one_more_without_writing()
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        var body = new byte[65536];
        Assert.Null((await service.PutAsync("123", "device", "order", "main", 0, body)).Error);
        Assert.Equal("too-large", (await service.PutAsync("123", "device", "order", "main", 1, new byte[65537])).Error);
        Assert.Equal(body, (await service.GetAsync("123", "device", "order", "main")).Body);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData("123", "unknown", "main")]
    [InlineData("123", "Order", "main")]
    [InlineData("123", "order", "../main")]
    [InlineData("123", "order", "a/b")]
    [InlineData("123", "order", "a\\b")]
    [InlineData("123", "order", "main\n")]
    [InlineData("123", "order", "")]
    [InlineData("123", "order", "123456789012345678901234567890123")]
    [InlineData("../123", "order", "main")]
    public async Task Invalid_names_are_refused_on_reads_and_writes(string account, string kind, string id)
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        Assert.Equal("bad-request", (await service.GetAsync(account, "device", kind, id)).Error);
        Assert.Equal("bad-request", (await service.PutAsync(account, "device", kind, id, 0, Ciphertext)).Error);
        Assert.Empty(store.Names);
    }

    [Fact]
    public async Task Independent_replicas_racing_at_one_version_have_exactly_one_winner()
    {
        var store = new BarrierStore();
        var first = Make(store).PutAsync("123", "a", "order", "main", 0, [1]);
        var second = Make(store).PutAsync("123", "b", "order", "main", 0, [2]);
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(results, r => r.Error is null);
        Assert.Equal("conflict", Assert.Single(results, r => r.Error is not null).Error);
        Assert.All(results, r => Assert.Equal(1, r.Version));
        Assert.Equal(1, store.Inner.Writes);
    }

    sealed class BarrierStore : IRosterStore
    {
        public readonly MemoryRosterStore Inner = new();
        readonly TaskCompletionSource bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads;
        public async Task<StoredRoster?> ReadAsync(string name, CancellationToken ct)
        {
            var read = await Inner.ReadAsync(name, ct);
            if (Interlocked.Increment(ref reads) == 2) { bothRead.SetResult(); }
            await bothRead.Task.WaitAsync(ct);
            return read;
        }
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct) => Inner.WriteAsync(name, body, etag, ct);
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => Inner.DeleteAsync(name, etag, ct);
    }

    [Fact]
    public async Task Default_admission_refuses_reads_and_writes_without_touching_storage()
    {
        var service = Make(new FailingStore(), new DenyRecordAdmission());
        Assert.Equal("needs-device-approval", (await service.GetAsync("123", "device", "order", "main")).Error);
        Assert.Equal("needs-device-approval", (await service.PutAsync("123", "device", "order", "main", 0, Ciphertext)).Error);
    }

    [Fact]
    public async Task A_malformed_stored_record_is_unavailable_and_cannot_be_overwritten()
    {
        var store = new MemoryRosterStore();
        await store.WriteAsync("records/123/order/main", [1, 2, 3], null, default);
        var service = Make(store);
        Assert.Equal("unavailable", (await service.GetAsync("123", "device", "order", "main")).Error);
        Assert.Equal("unavailable", (await service.PutAsync("123", "device", "order", "main", 0, Ciphertext)).Error);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Storage_failure_reports_unavailable_and_keeps_the_previous_record(bool failWrite, bool authenticationFailure)
    {
        var store = new FailingStore();
        store.Fail = false;
        var service = Make(store);
        await service.PutAsync("123", "device", "order", "main", 0, Ciphertext);
        store.Fail = true;
        store.FailWrite = failWrite;
        store.Failure = authenticationFailure ? new Azure.Identity.AuthenticationFailedException("unreachable") : new IOException("unreachable");
        Assert.Equal("unavailable", (await service.PutAsync("123", "device", "order", "main", 1, [7])).Error);
        if (!failWrite) { Assert.Equal("unavailable", (await service.GetAsync("123", "device", "order", "main")).Error); }
        store.Fail = false;
        Assert.Equal(Ciphertext, (await service.GetAsync("123", "device", "order", "main")).Body);
        Assert.Equal(1, store.Inner.Writes);
    }

    sealed class FailingStore : IRosterStore
    {
        public readonly MemoryRosterStore Inner = new();
        public bool Fail = true, FailWrite;
        public Exception Failure = new IOException("unreachable");
        public Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) =>
            Fail && !FailWrite ? throw Failure : Inner.ReadAsync(name, ct);
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct) =>
            Fail && FailWrite ? throw Failure : Inner.WriteAsync(name, body, etag, ct);
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
